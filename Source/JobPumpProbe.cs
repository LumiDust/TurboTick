using HarmonyLib;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Counts the job-tracker interval pump and the job-expiry decision that lives inside it.
///
/// <para>Why this exists at all: on the live authority a colonist sat in `Wait` (expiry 250) for more than 30 000
/// ticks, with `ticksLeftThisToil` at -30616. That single pair rules out the two easy explanations - the driver is
/// being ticked (the counter went far below zero), and the job is not being expired. Vanilla expires a job in
/// <c>Pawn_JobTracker.JobTrackerTickInterval</c> (`Verse.AI/Pawn_JobTracker.cs:138`), with
/// <c>curJob.startTick + curJob.expiryInterval &lt;= GenTicks.TicksGame &amp;&amp; pawn.IsHashIntervalTick(curJob.expiryInterval, delta)</c>,
/// so either that method never runs, or its clock comparison never comes true. Those need opposite fixes and look
/// identical from the outside, so both are counted here instead of inferred.</para>
///
/// <para>Observation only: no state is written and nothing is skipped.</para></summary>
internal static class JobPumpProbe
{
    private static long intervalCalls;
    private static long tickCalls;
    private static long driverTicks;
    private static int lastDelta;
    private static int lastReportTick = -100000;

    internal static void NoteInterval(int delta)
    {
        intervalCalls++;
        lastDelta = delta;
    }

    internal static void NoteTick() => tickCalls++;
    internal static void NoteDriverTick() => driverTicks++;

    /// <summary>One line that makes "the pump stopped" and "the clock comparison is wrong" distinguishable.</summary>
    internal static string Summary()
    {
        var manager = Find.TickManager;
        var game = manager?.TicksGame ?? -1;
        var applied = SharedColony.Session.Barrier.AppliedTick;
        var server = SharedColony.Session.Barrier.ServerTick;
        return $"工作泵 JobTrackerTick={tickCalls} JobTrackerTickInterval={intervalCalls}（最近 delta={lastDelta}）"
            + $" DriverTick={driverTicks} ｜ 时钟 ticksGame={game} 屏障已应用={applied} 屏障服务端={server}"
            + $" 差={game - applied}";
    }

    /// <summary>Which of the two expiry clauses refused, for the pawn the player is looking at.</summary>
    internal static string Explain(Pawn pawn)
    {
        var job = pawn.CurJob;
        if (job is null) return $"{pawn.LabelShort}: 无 job";
        var game = Find.TickManager?.TicksGame ?? -1;
        var startedAt = job.startTick + job.expiryInterval;
        var clockOk = startedAt <= game;
        var hashOk = job.expiryInterval > 0 && pawn.IsHashIntervalTick(job.expiryInterval);
        return $"{pawn.LabelShort}: job={job.def.defName} 开始={job.startTick} 有效期={job.expiryInterval}"
            + $" 到期于={startedAt} 当前={game} 时钟条款={(clockOk ? "满足" : "不满足")}"
            + $" 哈希条款={(hashOk ? "满足" : "不满足")}"
            + $" driver已结束={pawn.jobs?.curDriver?.ended.ToString() ?? "无driver"}";
    }

    internal static void ReportIfStuck(Pawn pawn)
    {
        var game = Find.TickManager?.TicksGame ?? 0;
        if (game - lastReportTick < 1800) return;
        lastReportTick = game;
        Log.Warning("[TurboTick 工作泵] " + Summary() + "；" + Explain(pawn));
    }
}

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick))]
internal static class JobTrackerTickCountPatch
{
    private static void Prefix() => JobPumpProbe.NoteTick();
}

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTickInterval))]
internal static class JobTrackerIntervalCountPatch
{
    private static void Prefix(int delta) => JobPumpProbe.NoteInterval(delta);
}

[HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTick))]
internal static class JobDriverTickCountPatch
{
    private static void Prefix() => JobPumpProbe.NoteDriverTick();
}
