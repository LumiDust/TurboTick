using System.Text.Json;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Opt-in real-game invariant journal. Independent profiles have independent files, unlike Waylith's
/// shared Player.log. Enable with -turbotick-trace-ticks; ordinary play does no journal I/O.</summary>
internal static class SimulationDiagnostics
{
    private static readonly bool enabled = GenCommandLine.CommandLineArgPassed("turbotick-trace-ticks");
    private static readonly bool hashes = enabled || GenCommandLine.CommandLineArgPassed("turbotick-trace-hashes");
    private static int faults;
    private static int removals;
    private static int detailedThrough = -1;
    internal static bool Enabled => enabled;
    internal static bool RecordingStages => enabled && SharedColony.Gating && SimulationRandom.Active
        && SharedColony.Session.Barrier.AppliedTick < detailedThrough;
    internal readonly record struct Observation(bool Active, int GameTick, int AppliedTick, long Epoch);

    internal static Observation Before(TickManager manager) => enabled && SharedColony.Gating
        ? new(true, manager.TicksGame, SharedColony.Session.Barrier.AppliedTick, SharedColony.Session.Epoch)
        : default;

    internal static void After(TickManager manager, Observation before)
    {
        if (!before.Active || before.Epoch != SharedColony.Session.Epoch) return;
        var gameDelta = manager.TicksGame - before.GameTick;
        var logicalDelta = SharedColony.Session.Barrier.AppliedTick - before.AppliedTick;
        if (gameDelta != logicalDelta)
        {
            if (++faults <= 12) Write(new { kind = "tick-drift", gameDelta, logicalDelta,
                gameTick = manager.TicksGame, logicalTick = SharedColony.Session.Barrier.AppliedTick,
                serverTick = SharedColony.Session.Barrier.ServerTick, epoch = before.Epoch });
        }
        else if (SharedColony.Session.Barrier.AppliedTick % 60 == 0)
            Write(new { kind = "tick-sample", gameDelta, logicalDelta,
                gameTick = manager.TicksGame, logicalTick = SharedColony.Session.Barrier.AppliedTick,
                serverTick = SharedColony.Session.Barrier.ServerTick, epoch = before.Epoch });
        if (SharedColony.Session.Barrier.AppliedTick <= detailedThrough)
            Boundary("early-tick", SharedColony.Session.WorldId, arm: false);
    }

    internal static void Hash(int tick, string hash, IReadOnlyList<string> categories, IReadOnlyList<string>? pawns = null)
    {
        if (!hashes) return;
        Write(new { kind = "hash", logicalTick = tick, gameTick = Find.TickManager?.TicksGame,
            epoch = SharedColony.Session.Epoch, randomState = SimulationRandom.Capture().ToString("x16"), hash, categories, pawns });
    }

    /// <summary>Captures why two replicas disagree. The category hash only names a bucket; this records
    /// the fields a fix would have to explain, and both peers write it for the same tick.</summary>
    internal static void Divergence(int tick, string detail)
    {
        if (!hashes) return;
        var rows = new List<object>();
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings.OrderBy(t => t.thingIDNumber))
            {
                if (thing.def.category == ThingCategory.Mote) continue;
                // 蓝图是 Ethereal；跳过它会让「两端蓝图不一致」在诊断行里无法出现。
                if (thing.def.category == ThingCategory.Ethereal && thing is not Blueprint) continue;
                var pawn = thing as Pawn;
                // A plant's HP is the one thing about it the hash covers, and it is only ever changed by
                // rotting. Growth, age, unlit ticks and the light reading are what decide whether rotting
                // starts, so they have to travel with the row to explain a mismatch.
                var plant = thing as RimWorld.Plant;
                rows.Add(new
                {
                    id = thing.thingIDNumber, def = thing.def.defName, map = map.uniqueID,
                    x = thing.Position.x, z = thing.Position.z, hp = thing.HitPoints,
                    job = pawn?.CurJobDef?.defName, health = pawn?.health?.summaryHealth?.SummaryHealthPercent,
                    growth = plant?.Growth, age = plant?.Age,
                    called = plant is null ? (bool?)null : plant.IsCrop,
                    light = plant is null ? (float?)null : map.glowGrid.GroundGlowAt(thing.Position),
                    dying = plant?.Dying,
                    needs = pawn?.needs?.AllNeeds?.Select(n => new { def = n.def.defName, level = n.CurLevel }).ToList(),
                    hediffs = pawn?.health?.hediffSet?.hediffs?
                        .Select(h => new { def = h.def.defName, part = h.Part?.def?.defName, severity = h.Severity }).ToList()
                });
            }
        }
        var (all, categories, _) = SharedColony.DescribeColony();
        Write(new { kind = "divergence", detail, logicalTick = tick, gameTick = Find.TickManager?.TicksGame,
            world = SharedColony.Session.WorldId,
            randomState = SimulationRandom.Capture().ToString("x16"),
            hash = TurboTick.Simulation.StateHash.Compute(all), categories, entities = rows });
    }

    internal static void Event(string kind)    {
        if (!enabled) return;
        Write(new { kind, phase = SharedColony.Session.Phase.ToString(), scribe = Scribe.mode.ToString(),
            gameTick = Find.TickManager?.TicksGame, logicalTick = SharedColony.Session.Barrier.AppliedTick,
            randomState = SimulationRandom.Capture().ToString("x16"), epoch = SharedColony.Session.Epoch });
        if (kind == "world-ready") Boundary("world-ready-baseline", SharedColony.Session.WorldId);
    }

    internal static void Boundary(string kind, string? worldId, bool arm = true)
    {
        if (!enabled) return;
        if (arm) detailedThrough = SharedColony.Session.Barrier.AppliedTick + 8;
        var rows = new List<object>();
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings.OrderBy(t => t.thingIDNumber))
            {
                if (thing.def.category == ThingCategory.Mote) continue;
                // 蓝图是 Ethereal；跳过它会让「两端蓝图不一致」在诊断行里无法出现。
                if (thing.def.category == ThingCategory.Ethereal && thing is not Blueprint) continue;
                var pawn = thing as Pawn;
                rows.Add(new { id = thing.thingIDNumber, def = thing.def.defName,
                    category = thing.def.category.ToString(), map = map.uniqueID, x = thing.Position.x, z = thing.Position.z,
                    count = thing.stackCount, hp = thing.HitPoints, job = pawn?.CurJobDef?.defName,
                    health = pawn?.health?.summaryHealth?.SummaryHealthPercent });
            }
        }
        var (all, categories, _) = SharedColony.DescribeColony();
        Write(new { kind, worldId, epoch = SharedColony.Session.Epoch,
            logicalTick = SharedColony.Session.Barrier.AppliedTick, gameTick = Find.TickManager?.TicksGame,
            randomState = SimulationRandom.Capture().ToString("x16"),
            hash = TurboTick.Simulation.StateHash.Compute(all), categories, entities = rows });
    }

    internal static void PathWarmup(int pending, int ready, int cancelled)
    {
        if (!enabled) return;
        Write(new { kind = "path-warmup", pending, ready, cancelled, epoch = SharedColony.Session.Epoch,
            logicalTick = SharedColony.Session.Barrier.AppliedTick, gameTick = Find.TickManager?.TicksGame,
            randomState = SimulationRandom.Capture().ToString("x16") });
    }

    internal static void Stage(string stage, int mapId, object? extra = null)
    {
        if (!RecordingStages) return;
        Write(new { kind = "simulation-stage", stage, mapId, epoch = SharedColony.Session.Epoch,
            logicalTick = SharedColony.Session.Barrier.AppliedTick + 1, gameTick = Find.TickManager?.TicksGame,
            randomState = SimulationRandom.Capture().ToString("x16"), extra });
    }

    internal static void Removal(Thing thing)
    {
        if (!enabled || !SharedColony.LoadPending || WorldLoadScope.CurrentSave is null
            || thing.def.defName != "Filth_RubbleRock" || removals++ >= 8) return;
        var callers = new System.Diagnostics.StackTrace(1, false).GetFrames().Take(12)
            .Select(frame => frame.GetMethod()).Where(method => method is not null)
            .Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray();
        Write(new { kind = "load-filth-removal", id = thing.thingIDNumber,
            x = thing.Position.x, z = thing.Position.z, source = WorldLoadScope.CurrentSave, callers });
    }

    private static void Write(object row)
    {
        try
        {
            var directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTick");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "simulation-trace.jsonl"),
                JsonSerializer.Serialize(new { utc = DateTime.UtcNow, pid = Environment.ProcessId, data = row }) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log.Warning("[TurboTick] Simulation trace: " + error.Message); }
    }
}
