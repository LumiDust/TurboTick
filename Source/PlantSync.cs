using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Plant growth the host accumulated, applied on a replica through <c>Plant.Growth</c>.
///
/// <para><b>Why a replica needs this.</b> Growth advances in <c>Plant.TickLong</c> (<c>growthInt += GrowthPerTick * 2000f</c>),
/// a tick a replica never runs, so every plant keeps the growth the save was loaded with. <c>growthInt</c> is scribed, so the
/// value itself is not the problem - the problem is that no channel ever re-sends it: <c>SweepItemStates</c> walks only
/// <c>ThingCategory.Item</c>, and the frame's object scan skips any object whose position/rotation/stack/hitpoints did not
/// change, which is exactly the state a growing plant is in. A crop therefore reads the same "23% grown" all season, and a
/// wild bush never becomes harvestable.</para>
///
/// <para><b>Why this sends a stage and not the growth float.</b> Growth is continuous and only three facts are read from it:
/// <c>LifeStage</c> (below 0.0001 = sowing, above 0.999 = mature), <c>HarvestableNow</c> (above
/// <c>def.plant.harvestMinGrowth</c>), and the mesh's growth tier. Two of those are one-bit question and the third changes
/// a handful of times over a plant's life. Sending the raw float would put every one of the 36 181 plants on the wire on
/// every frame - measured on this save - to describe a number the game only ever compares against thresholds.</para>
///
/// <para><b>Why the thresholds are sent with the value.</b> <c>harvestMinGrowth</c> is per-def and the replica has the same
/// def database, so sending it would be repetition - but the *stage* is what carries the bit, and computing it here means the
/// replica never has to reimplement the comparison. A def whose threshold differs from vanilla's still gets the right
/// answer because the answer travels, not the inputs.</para>
///
/// <para><b>Why a sweep and not a hook.</b> There is no single writer to hook: <c>TickLong</c> is called per plant by the
/// long-tick scheduler, so a postfix would fire for thousands of plants in the same tick and flood one frame. The sweep
/// spreads them across frames the same way the building and item sweeps do, and its cursor means "plant 3 000 of 36 181"
/// rather than "everything, now".</para>
internal static class PlantSync
{
    /// <summary>Stage bits: bit 0 = mature, bit 1 = harvestable, bits 8..15 = the tenth-of-growth tier the mesh uses.
    /// Packed into one byte because that is what the three questions fit in.</summary>
    internal static byte Stage(Plant plant)
    {
        var growth = plant.Growth;
        var stage = (byte)((int)Math.Round(Math.Clamp(growth, 0f, 1f) * 10f) & 0xF);
        if (growth > 0.999f) stage |= 1;
        if (plant.HarvestableNow) stage |= 2;
        return stage;
    }

    private static readonly Dictionary<int, byte> sent = [];
    private static readonly List<Plant> sweep = [];
    private static int cursor;
    private static int roundDone;
    private static int listBuilds;

    internal static long PlantsSent { get; private set; }
    internal static long PlantsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int Scanned { get; private set; }

    /// <summary>Plants whose stage differs from what was last sent, in rotating slices of <paramref name="budget"/>.
    ///
    /// <para>The budget is 64 rather than the building sweep's 24 because a plant's state costs one byte to compare and one
    /// to send - measured 36 181 on this save means a full round is about 565 frames at 64, still under ten seconds, and a
    /// smaller slice would take minutes to notice a crop ripening.</para></summary>
    internal static List<ReplicaPlant>? Publish(int budget = 64)
    {
        try
        {
            if (sweep.Count == 0 || cursor >= sweep.Count)
            {
                sweep.Clear();
                foreach (var map in Find.Maps)
                    foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
                        if (thing is Plant plant && plant.Spawned && !plant.Destroyed) sweep.Add(plant);
                cursor = 0;
                roundDone++;
                listBuilds++;
                // A plant that died never needs its entry again; without this the dictionary grows with every harvest for the
                // life of the process.
                if (sweep.Count == 0) return null;
                var alive = new HashSet<int>(sweep.Select(plant => plant.thingIDNumber));
                foreach (var stale in sent.Keys.Where(id => !alive.Contains(id)).ToList()) sent.Remove(stale);
            }
            List<ReplicaPlant>? result = null;
            var end = Math.Min(cursor + budget, sweep.Count);
            for (var i = cursor; i < end; i++)
            {
                var plant = sweep[i];
                if (plant is null || plant.Destroyed || !plant.Spawned) continue;
                Scanned++;
                var id = plant.thingIDNumber;
                var stage = Stage(plant);
                if (sent.TryGetValue(id, out var previous) && previous == stage) continue;
                sent[id] = stage;
                (result ??= []).Add(new ReplicaPlant(plant.Map.uniqueID, id, stage));
                PlantsSent++;
            }
            cursor = end;
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] plant publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Applies one plant's stage.
    ///
    /// <para>Writes <c>Growth</c> only when the matured-ness of the plant disagrees with the byte, not to replay a fraction:
    /// the replica's own growth is frozen, so the only thing worth writing is the discrete fact. Writing a tenth of the
    /// growth every frame would mark the mesh dirty for a plant that has not visibly changed.</para>
    ///
    /// <para>No mesh dirty call: <c>Plant.TickLong</c> only marks the mesh when <c>CurrentlyCultivated()</c>, and a
    /// cultivated plant is the one case where a tenth of growth changes the picture - so that call is made here only for a
    /// crop, which is what the flag means.</para></summary>
    internal static void Apply(ReplicaPlant state)
    {
        try
        {
            var map = Find.Maps.FirstOrDefault(each => each.uniqueID == state.Map);
            if (map is null) return;
            var thing = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
                .FirstOrDefault(each => each.thingIDNumber == state.Thing);
            if (thing is not Plant plant || plant.Destroyed) return;
            var stage = state.Stage;
            var growth = plant.Growth;
            var shouldBeMature = (stage & 1) != 0;
            var isMature = growth > 0.999f;
            var shouldBeHarvestable = (stage & 2) != 0;
            var isHarvestable = plant.HarvestableNow;
            // Reaching "mature" or becoming harvestable is a discrete step the replica's frozen growth can never take, so it
            // is written as growth rather than as a flag - nothing stores a maturity flag.
            if (shouldBeMature && !isMature) { plant.Growth = 1f; PlantsApplied++; }
            else if (shouldBeMature == isMature && shouldBeHarvestable != isHarvestable)
            {
                // The threshold is per-def. Nudge the growth across it by the smallest step that crosses, without pretending
                // to know the exact value the host holds.
                var threshold = plant.def?.plant?.harvestMinGrowth ?? 0f;
                if (shouldBeHarvestable && growth <= threshold) { plant.Growth = Math.Min(1f, threshold + 0.001f); PlantsApplied++; }
                else if (!shouldBeHarvestable && growth > threshold) { plant.Growth = Math.Max(0f, threshold); PlantsApplied++; }
            }
            // `CurrentlyCultivated` is `protected virtual`, so the only equivalent from outside is the def flag it starts
            // with (`if (!def.plant.Sowable) return false;`) - a sown plant is the one whose tenth-of-growth changes the
            // picture, and a wild bush's mesh is never dirtied by growth at all.
            if (plant.def?.plant?.Sowable == true) plant.Map.mapDrawer.MapMeshDirty(plant.Position, MapMeshFlagDefOf.Things);
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] plant apply failed ({state.Thing}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        sent.Clear();
        sweep.Clear();
        cursor = 0;
    }

    internal static string Summary() =>
        $"植物 发出 {PlantsSent} / 应用 {PlantsApplied}（扫 {Scanned}，建表 {listBuilds}，失败 {ApplyFailures}）";
}
