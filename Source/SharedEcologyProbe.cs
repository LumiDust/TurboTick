using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Records which wild-plant spawns succeeded, so a divergence can name the exact cell and species instead of
/// only the count that followed from it.
///
/// The remaining divergence looked like this: every entity matched except one extra <c>Plant_Bush</c> on one
/// peer, with identical RNG, identical ecology weights, identical list order. "This peer grew something there
/// and that one did not" is the only description the counts can give, and the guard that stopped it -
/// <c>CheckSpawnWildPlantAt</c> has half a dozen early returns (<c>RimWorld/WildPlantSpawner.cs:411-429</c>:
/// occupancy, cover, edifice, fertility, snow, sand, the density roll, <c>SaturatedAt</c>) - is invisible after
/// the fact.
///
/// The events are part of the shared hash for the same tick: two peers that spawned the same species at the same
/// cell agree, and one that did not is named immediately.
/// </summary>
internal static class SharedEcologyProbe
{
    private const int Capacity = 512;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    internal static void Record(Map map, IntVec3 cell, ThingDef plant)
    {
        lock (Recent)
        {
            Recent.Add(string.Join(" ",
                "sp",
                map.Index.ToString(CultureInfo.InvariantCulture),
                cell.x.ToString(CultureInfo.InvariantCulture),
                cell.z.ToString(CultureInfo.InvariantCulture),
                plant.defName));
            if (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    /// <summary>The spawns since the last reset, in the order they happened. Both peers must produce the same
    /// sequence, which is what makes this comparable at all.</summary>
    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

/// <summary>Counts a successful wild-plant spawn. Vanilla discovers the spawn through the return value, so a
/// postfix on the same call is the least invasive place to observe it.</summary>
[HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt))]
internal static class SharedEcologySpawnPatch
{
    // WildPlantSpawner.map is private, so the map is taken from the cell the caller asked about instead.
    private static void Postfix(IntVec3 c, bool __result)
    {
        if (!__result || !SharedColony.HasSharedWorld) return;
        try
        {
            foreach (var map in Find.Maps)
            {
                if (map is null || map.Disposed || !c.InBounds(map)) continue;
                // The plant is spawned inside the call, so whatever stands there now is what was chosen.
                var plant = c.GetPlant(map);
                if (plant is null) continue;
                SharedEcologyProbe.Record(map, c, plant.def);
                return;
            }
        }
        catch (Exception) { }
    }
}

