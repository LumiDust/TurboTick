using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Heals absolute tick stamps that a backwards clock jump already pushed into the future.
///
/// <para>Refusing further rewinds (see <see cref="SharedGameClock"/>) stops new damage; it does not undo the damage
/// already written into the colony. Vanilla compares these fields against <c>TicksGame</c> as "how long ago", so a
/// stamp above the current tick makes every consumer read a negative age forever:</para>
/// <list type="bullet">
/// <item><c>Job.startTick</c> - <c>Pawn_JobTracker.JobTrackerTickInterval</c> expires a job with
/// <c>startTick + expiryInterval &lt;= GenTicks.TicksGame</c> (<c>Verse.AI/Pawn_JobTracker.cs:168</c>). A start tick in
/// the future means the job never expires: the colonist sits in <c>Wait</c> for the rest of the session, which is
/// exactly what the player reported.</item>
/// <item><c>Pawn_PathFollower.lastMovedTick</c> - <c>MovedRecently(ticks)</c> is
/// <c>TicksGame - lastMovedTick &lt;= ticks</c> (<c>Verse.AI/Pawn_PathFollower.cs:524</c>), read by
/// <c>PawnUtility</c>'s crowd handling and by enemy-stuck givers.</item>
/// <item><c>Pawn_PathFollower.lastEnteredCellTick</c> - the same shape through <c>ChangedCellRecently</c>.</item>
/// </list>
///
/// <para>Clamping to "now" rather than to zero is deliberate: these fields only ever mean "at or before now", and
/// zero would claim the pawn last moved at the very start of the game, which reads as "has not moved in 30 000 ticks"
/// and triggers stuck-handling paths. Now is the truthful value for "we cannot know when, but not in the future".</para>
///
/// <para>Runs once per world load and again whenever <see cref="Scan"/> is asked, so a save written by an affected
/// build is healed on the next load instead of staying broken.</para></summary>
internal static class TickStampRepair
{
    internal static int RepairedJobs { get; private set; }
    internal static int RepairedPathers { get; private set; }
    internal static int WorstFutureStamp { get; private set; }

    private static readonly AccessTools.FieldRef<Job, int> JobStartTick =
        AccessTools.FieldRefAccess<Job, int>("startTick");

    private static readonly AccessTools.FieldRef<Pawn_PathFollower, int> LastMoved =
        AccessTools.FieldRefAccess<Pawn_PathFollower, int>("lastMovedTick");

    private static readonly AccessTools.FieldRef<Pawn_PathFollower, int> LastEnteredCell =
        AccessTools.FieldRefAccess<Pawn_PathFollower, int>("lastEnteredCellTick");

    /// <summary>Repairs every spawned pawn on every map. Cheap enough to run on demand: one field read per pawn plus one
    /// per active job.</summary>
    internal static void Scan()
    {
        if (!SharedColony.HasSharedWorld) return;
        var now = Find.TickManager?.TicksGame ?? 0;
        if (now <= 0) return;
        try
        {
            foreach (var map in Find.Maps)
            {
                if (map is null) continue;
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is null) continue;
                    RepairPather(pawn.pather, now);
                    RepairJob(pawn.jobs?.curJob, now);
                    // Queued jobs carry the same absolute stamp and expire the same way once they become current, so a
                    // poisoned queue would resurrect the freeze later.
                    var queue = pawn.jobs?.jobQueue;
                    if (queue is null) continue;
                    for (var i = 0; i < queue.Count; i++) RepairJob(queue[i]?.job, now);
                }
            }
            if (RepairedJobs > 0 || RepairedPathers > 0)
                Log.Warning($"[TurboTick] 时间戳修复：作业 {RepairedJobs} 个、路径 {RepairedPathers} 个，"
                    + $"最远的未来戳比当前多 {WorstFutureStamp} tick（当前 {now}）。"
                    + "此前它们让工作永不结束、移动永久停止。");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 时间戳修复失败：" + error.Message);
        }
    }

    private static void RepairJob(Job? job, int now)
    {
        if (job is null) return;
        int start;
        try { start = JobStartTick(job); } catch (Exception) { return; }
        if (start <= now) return;
        if (start - now > WorstFutureStamp) WorstFutureStamp = start - now;
        JobStartTick(job) = now;
        RepairedJobs++;
    }

    private static void RepairPather(Pawn_PathFollower? pather, int now)
    {
        if (pather is null) return;
        var repaired = false;
        try
        {
            if (LastMoved(pather) > now)
            {
                if (LastMoved(pather) - now > WorstFutureStamp) WorstFutureStamp = LastMoved(pather) - now;
                LastMoved(pather) = now;
                repaired = true;
            }
        }
        catch (Exception) { }
        try
        {
            if (LastEnteredCell(pather) > now)
            {
                if (LastEnteredCell(pather) - now > WorstFutureStamp)
                    WorstFutureStamp = LastEnteredCell(pather) - now;
                LastEnteredCell(pather) = now;
                repaired = true;
            }
        }
        catch (Exception) { }
        if (repaired) RepairedPathers++;
    }

    internal static string Summary() =>
        $"时间戳修复 作业={RepairedJobs} 路径={RepairedPathers} 最远超前={WorstFutureStamp} tick"
        + $" ｜ {ClockStampShift.Summary()}";
}

/// <summary>Runs the repair once the world is in the game, on the game thread, before anything reads those stamps.
///
/// <para>Hooked on <c>Root.Update</c>'s existing pump rather than on load, because a load can finish in a frame where
/// no other TurboTick component is scheduled yet, and the freeze it prevents is invisible until several hundred ticks
/// later.</para></summary>
[HarmonyPatch(typeof(Root), nameof(Root.Update))]
internal static class TickStampRepairPatch
{
    private static bool done;

    private static void Postfix()
    {
        if (done) return;
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null) return;
        done = true;
        TickStampRepair.Scan();
    }
}
