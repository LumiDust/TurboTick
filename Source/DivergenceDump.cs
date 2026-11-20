using System.Globalization;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Writes a compact, comparable snapshot of the colony when a divergence is detected.
///
/// Two things made the first real investigations expensive, and both are addressed here:
///
/// 1. The full <see cref="SimulationDiagnostics"/> journal needs <c>-turbotick-trace-ticks</c> and writes a row
///    per sampled tick (347 MB in 13 minutes), so the run that actually diverged was the one without it. This
///    costs nothing until a divergence is reported.
/// 2. A dump taken "now" is not the state of the tick that diverged. The first pair that was cross-examined had
///    <c>gameTick 6601</c> against <c>6602</c>, and every difference it showed - a pawn one cell further along
///    its path, a one-tick <c>Wait_MaintainPosture</c>, one newly grown plant - was sampling skew. So the hash
///    inputs of the last few hashed ticks are kept in a small ring (<see cref="Remember"/>), and a report for tick
///    T is filled from that ring.
///
/// The projection mirrors what the state hash hashes (<see cref="Simulation.StateHash.Pawn"/> and
/// <see cref="Simulation.StateHash.Thing"/>), so a differing line is a differing hash input, not a guess.
/// </summary>
internal static class DivergenceDump
{
    private static readonly Queue<(int Tick, List<string> Rows, string Counts, long GameTick, ulong Rng)> Recent = new();

    /// <summary>Pawn-only projections for the per-tick probe, which cannot afford a full colony traversal. Kept
    /// separately so a probe run still answers "which pawn at which tick" from the ring.</summary>
    private static readonly Queue<(int Tick, List<string> Rows, long GameTick, ulong Rng)> RecentPawns = new();

    internal static void RememberPawns(int tick, List<string> rows, long gameTick, ulong rng)
    {
        lock (RecentPawns)
        {
            RecentPawns.Enqueue((tick, rows, gameTick, rng));
            while (RecentPawns.Count > 8) RecentPawns.Dequeue();
        }
    }

    // The tick being compared, not the tick the file was written on: those differ by one and reading the file
    // header made that look like a simulation difference.
    private static (List<string> Rows, long GameTick, ulong Rng)? RecallPawns(int tick)
    {
        lock (RecentPawns)
        {
            foreach (var entry in RecentPawns)
                if (entry.Tick == tick) return (entry.Rows, entry.GameTick, entry.Rng);
        }
        return null;
    }

    /// <summary>Keeps the projection of a hashed tick so a late report can be answered with that tick's state.</summary>
    internal static void Remember(int tick, List<string> rows, string counts, long gameTick, ulong rng)
    {
        lock (Recent)
        {
            Recent.Enqueue((tick, rows, counts, gameTick, rng));
            while (Recent.Count > 4) Recent.Dequeue();
        }
    }

    internal static void Forget()
    {
        lock (Recent) Recent.Clear();
        lock (RecentPawns) RecentPawns.Clear();
    }

    private static (List<string> Rows, string Counts, long GameTick, ulong Rng)? Recall(int tick)
    {
        lock (Recent)
        {
            foreach (var entry in Recent)
                if (entry.Tick == tick) return (entry.Rows, entry.Counts, entry.GameTick, entry.Rng);
        }
        return null;
    }

    /// <summary>One line per entity, sorted by id, plus the ecology cache of each map.</summary>
    internal static (List<string> Rows, string Counts) Build()
    {
        var rows = new List<string>(1 << 15);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var maps = Find.Maps;
        for (var m = 0; m < maps.Count; m++)
        {
            var map = maps[m];
            if (map is null || map.Disposed) continue;
            var pawns = map.mapPawns?.AllPawnsSpawned;
            if (pawns is not null)
            {
                var ordered = new List<Pawn>(pawns);
                ordered.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
                for (var i = 0; i < ordered.Count; i++)
                {
                    var pawn = ordered[i];
                    var job = pawn.CurJob;
                    rows.Add(string.Join(" ",
                        "p",
                        Int(pawn.thingIDNumber),
                        Int(map.uniqueID),
                        Int(pawn.Position.x),
                        Int(pawn.Position.z),
                        F(pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f),
                        pawn.CurJobDef?.defName ?? "",
                        Int(job?.targetA.HasThing == true ? job.targetA.Thing.thingIDNumber : -1),
                        Int(job?.targetB.HasThing == true ? job.targetB.Thing.thingIDNumber : -1),
                        Int(job?.targetA.IsValid == true ? job.targetA.Cell.x : -1),
                        Int(job?.targetA.IsValid == true ? job.targetA.Cell.z : -1),
                        Int(job?.startTick ?? -1),
                        pawn.pather?.Moving == true ? "1" : "0",
                        // Path progress, not just "is it moving". Two peers on the same path one cell apart
                        // differ here first, and that is a different bug from choosing a different target.
                        Int(NextCellOf(pawn).x),
                        Int(NextCellOf(pawn).z),
                        // JobGiver_Wander flips this on every call and branches on it
                        // (JobGiver_Wander.cs:48-58): one extra think-tree pass inverts it permanently, which
                        // looks like "wanders differently from then on" rather than like an RNG problem.
                        pawn.mindState is { } mind && mind.nextMoveOrderIsWait ? "1" : "0",
                        ThinkPassAudit.Describe(pawn.thingIDNumber),
                        ThinkPassAudit.Source(pawn.thingIDNumber),
                        (pawn.needs?.food?.CurLevel ?? -1f).ToString("0.####", CultureInfo.InvariantCulture),
                        // Wait_Wander expires through IsHashIntervalTick(expiryInterval, delta)
                        // (Verse.AI/Pawn_JobTracker.cs:166-169), and that interval is itself a random draw
                        // (JobGiver_Wander.GetWaitJob -> ticksBetweenWandersRange.RandomInRange). Reporting the
                        // interval and the tick it should expire on turns "the job never ended" into either
                        // "different interval" or "same interval, expiry check never fired".
                        Int(job?.expiryInterval ?? -1),
                        Int((job?.startTick ?? -1) + (job?.expiryInterval ?? -1)),
                        // The expiry check is sparse: Pawn_JobTracker.cs:168 also requires
                        // `|(TicksGame + thingIDNumber.HashOffset()) % expiryInterval| < delta`, and delta is
                        // pinned to 1 by the shared tick cadence, so the job only ends on a tick that lands on
                        // that grid. "expiryTick has passed" therefore does not mean "should have ended", and
                        // comparing only that number invited exactly that misreading: a 300-tick "difference"
                        // that was really 300 being a multiple of the interval.
                        Int(NextExpiryCheck(pawn, job)),
                        // The hash-grid offset itself, so "same nextCheck" can be trusted rather than inferred.
                        Int(pawn.thingIDNumber.HashOffset()),
                        pawn.jobs?.curDriver is null ? "0" : "1",
                        // The tick the driver last ran. A pawn whose job should have expired but whose driver is
                        // not ticking looks identical in every other column.
                        Int(pawn.jobs?.curDriver?.ticksLeftThisToil ?? -1)));
                    counts["pawn"] = counts.GetValueOrDefault("pawn") + 1;
                }
            }
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing?.def is null) continue;
                if (thing.def.category is ThingCategory.Pawn or ThingCategory.Mote) continue;
                // 蓝图是 Ethereal（`ThingDef.IsBlueprint` 就是这么定义的），跳过 Ethereal 会让蓝图在分叉
                // 转储里隐形 —— 而「两个副本的蓝图不一样」正是最该被转储抓到的一种不一致。
                if (thing.def.category == ThingCategory.Ethereal && thing is not Blueprint) continue;
                rows.Add(string.Join(" ",
                    "t",
                    Int(thing.thingIDNumber),
                    Int(map.uniqueID),
                    Int(thing.Position.x),
                    Int(thing.Position.z),
                    Int(thing.stackCount),
                    Int(thing.HitPoints),
                    thing.def.defName));
                counts["thing"] = counts.GetValueOrDefault("thing") + 1;
            }
            foreach (var zone in map.zoneManager.AllZones)
            {
                rows.Add(string.Join(" ", "z", Int(zone.ID), Int(map.uniqueID), Int(zone.CellCount), zone.label));
                counts["zone"] = counts.GetValueOrDefault("zone") + 1;
            }
            foreach (var _ in map.designationManager.AllDesignations)
                counts["designation"] = counts.GetValueOrDefault("designation") + 1;
            // The ecology scan is where the first reproducible 物体 divergence landed: identical rng, one plant,
            // two different defs. Its cache is the first thing to compare.
            var ecology = Ecology(map, counts);
            if (ecology is not null) rows.Add(ecology);
            // The same strings the 生态 category hashes. Without them a `detail=生态` report says a category
            // differed but not which entry, and the entries are already built - this is nearly free.
            foreach (var entry in SharedColony.EcologyEntries(map)) rows.Add(entry);
            foreach (var spawn in SharedEcologyProbe.Entries()) rows.Add(spawn);
        }
        var summary = "# counts " + string.Join(" ", counts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value));
        return (rows, summary);
    }

    internal static void Write(int tick, string detail)
    {
        try
        {
            var directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTick");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"divergence-{tick}.txt");
            var builder = new StringBuilder(1 << 21);
            builder.Append("# TurboTick divergence dump\n");
            // Every header line is '#'-prefixed so the comparison script treats them uniformly; the first
            // version wrote this one without it and the script's header stayed empty.
            // gameTick and rng come from the ring when available. Reading them live made the two peers' headers
            // differ by one tick even when the rows were perfectly aligned, which reads as a failed alignment.
            var recalled = Recall(tick);
            builder.Append("# tick=").Append(Int(tick))
                .Append(" detail=").Append(detail)
                .Append(" pid=").Append(Int(Environment.ProcessId))
                .Append(" gameTick=").Append(Int(recalled?.GameTick ?? RecallPawns(tick)?.GameTick ?? Find.TickManager?.TicksGame ?? -1))
                .Append(" epoch=").Append(Int(SharedColony.Session.Epoch))
                .Append(" rng=").Append((recalled?.Rng ?? RecallPawns(tick)?.Rng ?? SimulationRandom.Capture())
                    .ToString("x16", CultureInfo.InvariantCulture))
                // Same seed with a different count means one peer drew extra values; the count alone answers
                // "how many" without decoding the packed state by hand.
                // Taken from the ring when available: a live read here reports "the moment the file was written",
                // which differs by up to a tick and reads as a much larger draw gap than actually exists.
                // Same source as iter=: the ring entry for the compared tick. Reading the live counter here made
                // `draws` and `iter` disagree inside one dump whenever the full-colony ring had no entry.
                .Append(" draws=").Append(Int(TickDraws(recalled?.Rng ?? RecallPawns(tick)?.Rng)))
                .Append(" seed=").Append((SimulationRandom.Capture() & 0xFFFFFFFF)
                    .ToString("x8", CultureInfo.InvariantCulture))
                .Append(" content=").Append(ContentProbe.Describe().Split(' ')[0])
                .Append(" world=").Append(SharedColony.Session.WorldId)
                // A probe dump answers for the tick from the pawn ring; saying "live" there would claim it is a
            // snapshot of the wrong moment.
            .Append(recalled is not null ? " source=ring"
                : RecallPawns(tick) is not null ? " source=probe" : " source=live").Append('\n');
            List<string> rows;
            string counts;
            if (recalled is { } snapshot) { rows = snapshot.Rows; counts = snapshot.Counts; }
            else if (RecallPawns(tick) is { } probe)
            {
                rows = probe.Rows;
                // The rows include iter= and stages=, which are not pawns.
                var pawnRowsOnly = probe.Rows.Count(r => r.StartsWith("p:", StringComparison.Ordinal));
                // Traces travel in the dump but are not part of the hash, so they are counted separately: their
                // contents depend on how much history fits in a fixed buffer and can differ between peers whose
                // state is identical.
                var traceRows = probe.Rows.Count(IsTrace);
                counts = "# counts pawn=" + pawnRowsOnly
                    + " state=" + (probe.Rows.Count - pawnRowsOnly - traceRows)
                    + " trace=" + traceRows + " (per-tick probe; trace rows are diagnostic and excluded from the hash)"
                    // Publish counters belong here, not on the periodic probe line: a drag diverges within one tick and
                    // the harness stops, so the next periodic sample never happens and the counters that matter would
                    // never be recorded. The dump is written at the divergence tick.
                    + " " + PublishGate.Counters() + " " + SendFailureGate.Counters() + " " + ZoneCellProbe.Counters() + " " + SocketSendGate.Counters();
            }
            else { (rows, counts) = Build(); }
            foreach (var row in rows) builder.Append(row).Append('\n');
            builder.Append(counts).Append('\n');
            File.WriteAllText(path, builder.ToString());
            Log.Message($"[TurboTick] 分叉快照已写入（{rows.Count} 行，{(recalled is null ? "现场" : "与 tick 对齐")}）：{path}");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 分叉快照写入失败：" + error.Message);
        }
    }

    private static string? Ecology(Map map, Dictionary<string, int> counts)
    {
        var spawner = map.wildPlantSpawner;
        if (spawner is null) return null;
        try
        {
            var type = typeof(WildPlantSpawner);
            counts["ecology"] = counts.GetValueOrDefault("ecology") + 1;
            // The species counts and the commonality sum ride along: PlantChoiceWeight multiplies them together,
            // so a different count or a different float sum explains a different species from the same draw.
            return string.Join(" ",
                "w",
                Int(map.uniqueID),
                Int(Field<int>(type, spawner, "cycleIndex")),
                Field<bool>(type, spawner, "hasWholeMapNumDesiredPlantsCalculated") ? "1" : "0",
                F(Field<float>(type, spawner, "calculatedWholeMapNumDesiredPlants")),
                F(Field<float>(type, spawner, "calculatedWholeMapNumDesiredPlantsTmp")),
                Int(Field<int>(type, spawner, "calculatedWholeMapNumNonZeroFertilityCells")),
                Int(Field<int>(type, spawner, "calculatedWholeMapNumNonZeroFertilityCellsTmp")),
                "sum=" + F(spawner.PlantsCommonalitiesSum) + "/" + F(spawner.CavePlantsCommonalitiesSum),
                "order=" + (map.BiomeAt(IntVec3.Zero)?.AllWildPlants?.Count ?? -1)
                    + "/" + (spawner.MutatorWildPlants?.Count ?? -1));
        }
        catch (Exception error)
        {
            return "w " + Int(map.uniqueID) + " unreadable " + error.GetType().Name;
        }
    }

    /// <summary>The first tick at or after <c>startTick + expiryInterval</c> that passes the hash-grid test.
    /// Duplicates the game's arithmetic on purpose: it is the only way to compare "when should this have ended"
    /// between peers instead of "when could it have".</summary>
    // One implementation, shared with the hash projection: two copies of this formula is exactly the kind of
    // drift that made the probe and the full hash disagree about a pawn earlier.
    private static int NextExpiryCheck(Pawn pawn, Verse.AI.Job? job) => SharedColony.NextExpiryTick(pawn, job);

    private static IntVec3 NextCellOf(Pawn pawn)
    {
        try { return pawn.pather?.nextCell ?? pawn.Position; }
        catch (Exception) { return pawn.Position; }
    }

    /// <summary>Rows produced by a diagnostic ring rather than by the simulation state.</summary>
    private static bool IsTrace(string row) =>
        row.StartsWith("draw ", StringComparison.Ordinal)
        || row.StartsWith("forb ", StringComparison.Ordinal)
        || row.StartsWith("food:", StringComparison.Ordinal)
        || row.StartsWith("haul ", StringComparison.Ordinal)
        || row.StartsWith("socialn ", StringComparison.Ordinal)
        || row.StartsWith("socialk ", StringComparison.Ordinal)
        || row.StartsWith("socialr ", StringComparison.Ordinal)
        || row.StartsWith("socialt ", StringComparison.Ordinal)
        || row.StartsWith("cani ", StringComparison.Ordinal)
        // Added after a round in which these were counted as state: `state=56` instead of 8, and the trace rows
        // showed up as `onlyA/onlyB`, which reads like a missing state row.
        || row.StartsWith("forbidgate ", StringComparison.Ordinal)
        || row.StartsWith("zonegate ", StringComparison.Ordinal)
        || row.StartsWith("senddrop ", StringComparison.Ordinal)
        || row.StartsWith("pubgate ", StringComparison.Ordinal)
        || row.StartsWith("zonecell ", StringComparison.Ordinal)
        || row.StartsWith("sendconn ", StringComparison.Ordinal);

    private static uint TickDraws(ulong? rng) =>
        rng is { } value ? (uint)(value >> 32) : SimulationRandom.Iterations();

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static T Field<T>(Type type, object instance, string name)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field is null ? default! : (T)(field.GetValue(instance) ?? default(T)!);
    }
}






































