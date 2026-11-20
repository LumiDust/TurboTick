using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>
/// Records what the tick manager actually saw when it decided to skip a frame, because the status file cannot.
///
/// Measured failure this exists for: a real save sat at <c>serverTick=2505</c> for minutes while
/// <c>headless-status.json</c> kept reporting <c>paused=false</c>, <c>timeSpeed=1</c>,
/// <c>tickRateMultiplier=1</c>, <c>playerHasControl=true</c> and <c>ticksThisFrame=0</c>.
/// <see cref="NativeSpeedSync.ApplyConfirmed"/> is a postfix on <c>Root.Update</c>, so it runs after the tick
/// manager has already decided; every reading taken from outside the decision therefore describes the state
/// after any repair, never the fault.
///
/// The first run of this hook already ruled out the obvious answers, and its numbers only make sense if the
/// manager being entered is not the one the status file reads, or if its clock is being written back:
/// 7 810 entries in 5 s (≈1 560/s - the frame loop is spinning, not stalling), 4 230 of them with
/// <c>ticksThisFrame &gt; 0</c>, yet <c>realTimeToTickThrough</c> grew by 0.01 and <c>TicksGame</c> never moved.
/// A tick that ran must increment <c>ticksGameInt</c>, so either the identity differs or
/// <see cref="SharedGameClock.Align"/> is rewriting it every tick. Both are one field away from being visible,
/// which is why this reports identity, the manager's own tick, and <c>Time.deltaTime</c>.
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
internal static class TickDecisionAuditPatch
{
    // __runOriginal is Harmony's own answer to "did the body run". Without it this hook cannot tell a body
    // that ran and did nothing from a prefix that skipped it - both leave ticksThisFrame at 0 and the
    // accumulator where it was, which is the ambiguity that made the previous round inconclusive.
    [HarmonyPostfix]
    public static void Postfix(TickManager __instance, bool __runOriginal) =>
        TickDecisionAudit.Observe(__instance, __runOriginal);
}

internal static class TickDecisionAudit
{
    private const long IntervalMilliseconds = 5_000;

    private static readonly AccessTools.FieldRef<TickManager, float> RealTimeToTickThrough =
        AccessTools.FieldRefAccess<TickManager, float>("realTimeToTickThrough");

    private static long lastMilliseconds = Environment.TickCount64;
    private static long calls;
    private static long skipped;
    private static long stalled;
    private static long produced;
    private static long foreign;
    private static long suppressedBody;
    private static int repeats;
    private static float lastAccumulator = float.NaN;
    private static int lastTick = -1;

    /// <summary>How many times the tick manager has been entered since the last report. Zero means the frame
    /// never reached it, which points at the early return in <c>Root_Play.Update</c> rather than at the loop.</summary>
    internal static long Calls => Interlocked.Read(ref calls);

    internal static void Observe(TickManager manager, bool runOriginal)
    {
        if (!HeadlessRuntime.Active) return;
        Interlocked.Increment(ref calls);
        if (!runOriginal) Interlocked.Increment(ref suppressedBody);
        var paused = manager.Paused;
        var ticks = manager.TicksThisFrame;
        var self = manager.TicksGame;
        var accumulated = RealTimeToTickThrough(manager);
        // Identity first: a status file reads Find.TickManager, and a second instance (the game that was disposed
        // after the room baseline replaced it) would explain "ticks ran but the clock never moved" completely.
        var current = ReferenceEquals(manager, Find.TickManager);
        if (!current) Interlocked.Increment(ref foreign);
        if (paused) Interlocked.Increment(ref skipped);
        else if (ticks == 0) Interlocked.Increment(ref stalled);
        else Interlocked.Increment(ref produced);
        var now = Environment.TickCount64;
        if (now - lastMilliseconds < IntervalMilliseconds)
        {
            ++repeats;
            lastAccumulator = accumulated;
            lastTick = self;
            return;
        }
        var windowCalls = Interlocked.Exchange(ref calls, 0);
        var windowSkipped = Interlocked.Exchange(ref skipped, 0);
        var windowStalled = Interlocked.Exchange(ref stalled, 0);
        var windowProduced = Interlocked.Exchange(ref produced, 0);
        var windowForeign = Interlocked.Exchange(ref foreign, 0);
        var windowSkippedBody = Interlocked.Exchange(ref suppressedBody, 0);
        var suppressed = repeats;
        repeats = 0;
        lastMilliseconds = now;
        var grew = float.IsNaN(lastAccumulator)
            ? "?" : (accumulated - lastAccumulator).ToString("+0.######;-0.######", CultureInfo.InvariantCulture);
        var tickMoved = lastTick < 0 ? "?" : (self - lastTick).ToString(CultureInfo.InvariantCulture);
        lastAccumulator = accumulated;
        lastTick = self;
        Log.Warning("[TurboTick headless] tick 决策：5 秒内进入 " + windowCalls + " 次"
            + "（暂停 " + windowSkipped + "，空转 " + windowStalled + "，出 tick " + windowProduced
            + "，不是当前 manager " + windowForeign
            + "，方法体未执行 " + windowSkippedBody + "）"
            + " paused=" + paused
            + " speed=" + manager.CurTimeSpeed
            + " mult=" + manager.TickRateMultiplier.ToString("0.###", CultureInfo.InvariantCulture)
            + " forcedNormal=" + manager.slower.ForcedNormalSpeed
            + " ticks=" + ticks
            + " TicksGame=" + self + "（较上次 " + tickMoved + "）"
            + " deltaTime=" + Time.deltaTime.ToString("0.#######", CultureInfo.InvariantCulture)
            + " 累积=" + accumulated.ToString("0.######", CultureInfo.InvariantCulture)
            + "（较上次 " + grew + "）"
            + " 窗口内重复=" + suppressed
            + " longEvent=" + LongEventHandler.AnyEventNowOrWaiting);
    }
}
