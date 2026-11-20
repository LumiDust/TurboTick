using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Stops any dialog from pausing a headless world.
///
/// Measured failure this file exists for: a real save on the dedicated server sat at <c>serverTick=7</c> with
/// <c>tps=0</c> for the whole session while frames kept going out. <c>headless-status.json</c> said
/// <c>paused=true</c>, so the diagnostics were extended to name the cause and they answered immediately:
/// <c>forcePauseWindows = ["Verse.Dialog_MessageBox"]</c>.
///
/// <c>Dialog_MessageBox..ctor</c> sets <c>forcePause = true</c> unconditionally (verified by decompiling
/// <c>tools/ref17-5005/Assembly-CSharp.dll</c>; <c>Verse.Window.forcePause</c> itself defaults to false, so every
/// message box opts in). <c>TickManager.Paused</c> is <c>curTimeSpeed != Paused ? ForcePaused : true</c>, and
/// <c>ForcePaused</c> short-circuits to true when <c>WindowStack.WindowsForcePause</c> - which walks the stack and
/// returns true if <b>any</b> window sets the flag.
///
/// In a headless process nothing can dismiss a dialog: there is no input, no mouse, no accept key. So one dialog -
/// including our own desync report - suspends the colony permanently while the server happily keeps ticking the
/// message queue. The world looks alive and never advances.
///
/// Two layers, because one of them cannot see everything:
/// <list type="bullet">
/// <item>a Harmony postfix on <c>WindowStack.Add</c> catches every window at the moment it enters the stack,
/// including ones opened by third-party mods (a real save here loads 14 of them);</item>
/// <item><see cref="Enforce"/> runs every frame from <c>HeadlessRuntime.Update</c> and clears the flag on whatever
/// is on the stack right now. That covers a window that was already open before patches installed, and a mod that
/// sets <c>forcePause</c> after adding itself rather than in its constructor.</item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
internal static class HeadlessWindowGuardPatch
{
    [HarmonyPostfix]
    public static void Postfix(Window window)
    {
        // Order this way, not by Harmony priority: the queue decides whether the window is a real question, and the
        // guard needs that answer. Two independent postfixes on the same method would make the meaning of the guard
        // depend on registration order, which is exactly the kind of thing that breaks silently later.
        HeadlessDialogQueue.Register(window, "添加时");
        HeadlessWindowGuard.Clear(window, "添加时");
    }
}

internal static class HeadlessWindowGuard
{
    /// <summary>Window types already reported, so the per-frame sweep does not fill the log.</summary>
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    internal static void Clear(Window? window, string when)
    {
        if (window is null || !HeadlessRuntime.Active) return;
        if (!window.forcePause) return;
        // A dialog that asks something keeps its pause: the mod's own state machine is waiting behind that answer,
        // and running the colony on while the question hangs would be a silently stuck workflow instead of a
        // visible one. `HeadlessDialogQueue` records it and the administration page answers it.
        if (HeadlessDialogQueue.HoldsPause(window)) return;
        window.forcePause = false;
        // Worth a line: it is the difference between "the world is paused for a reason" and "a dialog nobody can
        // see is holding the colony hostage", and in a headless log the window type is the only clue.
        var name = window.GetType().FullName ?? window.GetType().Name;
        Log.Warning("[TurboTick headless] 窗口 " + name + " 请求暂停世界（" + when + "），已忽略："
            + "无头进程里没有人能关掉它，加了这个标志等于永久暂停。");
    }

    /// <summary>
    /// Clears the flag on every window currently on the stack. Called once per frame; the guard in
    /// <see cref="HeadlessWindowGuardPatch"/> is what normally does the work, so this almost never fires - it is
    /// here for windows that arrived before the patches were installed or that set the flag late.
    /// </summary>
    internal static void Enforce()
    {
        if (!HeadlessRuntime.Active) return;
        var stack = Find.WindowStack;
        if (stack is null) return;
        foreach (var window in stack.Windows)
        {
            if (window is null || !window.forcePause) continue;
            // Same rule as `Clear`: a question that is waiting for an answer is allowed to hold the timeline.
            if (HeadlessDialogQueue.HoldsPause(window)) continue;
            var name = window.GetType().FullName ?? window.GetType().Name;
            if (Reported.Add(name))
                Log.Warning("[TurboTick headless] 窗口 " + name + " 仍在暂停世界，已在帧循环里清除标志："
                    + "无头进程里没有人能关掉它。");
            window.forcePause = false;
        }
    }
}
