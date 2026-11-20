using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Who pauses the world, and what happens when a mod pauses it every frame.
///
/// Measured on a real save (serverTick 2505, appliedTick 2499, tps 0, paused false): <c>Pause()</c> ran 300
/// times per 5 s window — exactly once per frame — and <c>ApplyConfirmed</c> put the speed back to Normal on
/// every frame. The journal entry said so: <c>Native speed applied: Normal (was Paused) … 此前 5 秒内另有
/// 299 次同类写入</c>. The world therefore stopped at the first tick where that started, and the status file
/// still read <c>paused=false</c> because we repaired it after the decision.
///
/// The caller, once named: <c>AtmosphereOverhaul.RimWorld.Global.GlobalAtmosphereWorldComponent
/// .SetWeatherTimeSpeed(TimeSpeed)</c> — <c>if ((int)speed == 0) Find.TickManager.Pause();</c>. Its weather
/// clock owns the pause until the GPU climate engine produces a frame; inside a container the engine stops
/// (<c>Only a software (CPU) WebGPU adapter is available</c>), so the clock never resumes and the pause is
/// re-asserted forever.
///
/// The vanilla property patch cannot see any of it: <c>Pause()</c> delegates to <c>TogglePaused()</c>, and both
/// write the backing field <c>curTimeSpeed</c> directly (decompiled 1.7: <c>prePauseTimeSpeed = curTimeSpeed;
/// curTimeSpeed = TimeSpeed.Paused;</c>) instead of going through the <c>CurTimeSpeed</c> property.
///
/// So this both reports and refuses. While a session is live and the server's policy is not itself paused, a
/// pause from mod code is dropped: the timeline belongs to the room, and one peer's local weather engine cannot
/// be allowed to stop it. A player's own pause still goes through NativeSpeedSync (which turns it into a room
/// request), because that path runs inside <c>TimeControls</c> where <c>controlsDepth &gt; 0</c>.</summary>
internal static class PauseAudit
{
    private const long IntervalMilliseconds = 5_000;

    private static readonly Dictionary<string, int> Refusals = new(StringComparer.Ordinal);
    private static long lastMilliseconds = -IntervalMilliseconds;
    private static int pausedWindow;
    private static int refusedWindow;
    private static string pausedCaller = "?";

    /// <summary>Counts one pause. The stack is walked at most once per window: a pause loop runs at frame
    /// rate, and one stack walk per call would cost more than the pause it is reporting.</summary>
    internal static void NotePause()
    {
        if (!HeadlessRuntime.Active) return;
        if (pausedWindow == 0) pausedCaller = Caller();
        ++pausedWindow;
        Close();
    }

    internal static void NoteRefusal(string reason)
    {
        if (!HeadlessRuntime.Active) return;
        Refusals[reason] = Refusals.TryGetValue(reason, out var count) ? count + 1 : 1;
        Close();
    }

    /// <summary>True when this pause must be dropped rather than performed.
    ///
    /// Room policy wins: a peer whose local weather engine wants the world stopped cannot stop everyone else's.
    /// The check is skipped while a local dialog is open (<see cref="TickManager.ForcePaused"/>), because that
    /// pause is the vanilla one that belongs to the window, not to the timeline.</summary>
    internal static bool ShouldRefuse()
    {
        if (!HeadlessRuntime.Active) return false;
        if (!TurboState.Authority.SessionRequested) return false;
        if (TurboState.Authority.Current.TimeSpeed == 0) return false;   // the room itself asked for a pause
        if (Find.TickManager is { ForcePaused: true }) return false;      // a local window owns this one
        ++refusedWindow;
        Close();
        return true;
    }

    /// <summary>First frame in the stack that is neither our own plumbing nor the vanilla trampoline.</summary>
    private static string Caller()
    {
        var frames = Environment.StackTrace.Split('\n');
        var skip = new[]
        {
            "PauseAudit", "TickManager.Pause", "TickManager.TogglePaused", "System.Environment.get_StackTrace",
            "HarmonyLib"
        };
        foreach (var frame in frames)
        {
            var text = frame.Trim();
            if (!text.StartsWith("at ", StringComparison.Ordinal)) continue;
            var skipThis = false;
            foreach (var prefix in skip)
            {
                if (text.Contains(prefix, StringComparison.Ordinal)) { skipThis = true; break; }
            }
            if (!skipThis) return text;
        }
        return "?";
    }

    private static void Close()
    {
        var now = Environment.TickCount64;
        if (now - lastMilliseconds < IntervalMilliseconds) return;
        lastMilliseconds = now;
        var paused = pausedWindow;
        var refused = refusedWindow;
        pausedWindow = 0;
        refusedWindow = 0;
        if (paused == 0 && refused == 0 && Refusals.Count == 0) return;
        var report = new StringBuilder("[TurboTick headless] 暂停审计：5 秒内暂停世界 ").Append(paused).Append(" 次（")
            .Append(pausedCaller).Append("），拒绝 ").Append(refused).Append(" 次");
        if (Refusals.Count > 0)
        {
            var parts = new List<string>();
            foreach (var pair in Refusals) parts.Add(pair.Key + "=" + pair.Value);
            parts.Sort(StringComparer.Ordinal);
            report.Append("；tick 被拒原因：").Append(string.Join("，", parts));
            Refusals.Clear();
        }
        Log.Warning(report.ToString());
    }
}

/// <summary>TickManager.Pause() writes curTimeSpeed through TogglePaused, so a property patch cannot see it.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.Pause))]
internal static class PauseAuditPausePatch
{
    private static bool Prefix()
    {
        if (!HeadlessRuntime.Active) return true;
        // Only a real transition matters: Pause() on an already paused manager does nothing.
        if (Find.TickManager is { Paused: true }) return true;
        if (PauseAudit.ShouldRefuse()) return false;
        PauseAudit.NotePause();
        return true;
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.TogglePaused))]
internal static class PauseAuditTogglePatch
{
    private static bool Prefix()
    {
        if (!HeadlessRuntime.Active) return true;
        // A toggle from an unpaused state is a pause request; a toggle back to running is always allowed.
        if (Find.TickManager is { Paused: false } && PauseAudit.ShouldRefuse()) return false;
        PauseAudit.NotePause();
        return true;
    }
}
