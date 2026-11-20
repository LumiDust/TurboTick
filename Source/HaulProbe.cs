using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Records the storage search that decides a haul destination, because that is where the divergence moved after
/// the work-giver order was fixed.
///
/// Measured (tick 9904): the same pawn, the same giver (`Work`), the same job (`HaulToCell`), the same hunger and
/// the same think tick - only the destination differed, `(134,126)` against `(136,122)`. Fixing
/// <c>workGiversByPriority</c> removed the previous layer (a different giver answering first) and exposed this one.
///
/// <c>StoreUtility.TryFindBestBetterStoreCellForWorker</c> (<c>RimWorld/StoreUtility.cs:215-240</c>) walks
/// <c>slotGroup.CellsList</c> in stored order and keeps the last cell whose distance is <c>&lt;=</c> the best so far,
/// so which cell wins depends on that order and on a sampling cutoff drawn from <c>Rand</c>:
/// <code>
/// int num = needAccurateResult ? Mathf.FloorToInt(count * Rand.Range(0.005f, 0.018f)) : 0;
/// for (i...) { if (!(d2 &gt; best) &amp;&amp; Ok(cells[i])) { best = cell; if (i &gt;= num) break; } }
/// </code>
/// Recording the group count, the cell count, a digest of the cell order and the chosen cell separates
/// "different groups", "different cell order" and "different cutoff".
/// </summary>
internal static class HaulProbe
{
    private const int Capacity = 24;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    private static void Add(string line)
    {
        lock (Recent)
        {
            Recent.Add(line);
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static void Result(Pawn carrier, Map map, IntVec3 cell, bool needAccurate)
    {
        try
        {
            var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            var chosenGroup = cell.IsValid ? cell.GetSlotGroup(map) : null;
            var cells = chosenGroup?.CellsList;
            var order = cells is null
                ? "-"
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join(";", cells.Select(c => c.x + "," + c.z)))))[..12].ToLowerInvariant();
            Add($"haul {carrier.thingIDNumber} "
                + $"cell={(cell.IsValid ? cell.x + "," + cell.z : "-")} "
                + $"groups={groups.Count} "
                + $"gidx={(chosenGroup is null ? -1 : groups.IndexOf(chosenGroup))} "
                + $"cells={cells?.Count ?? -1} "
                + $"order={order} accurate={needAccurate}");
        }
        catch (Exception error) { Add("haul error " + error.GetType().Name); }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

[HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
internal static class HaulSearchPatch
{
    private static void Postfix(Thing t, Pawn carrier, Map map, IntVec3 foundCell, bool needAccurateResult,
        bool __result)
    {
        if (!ThingDrawTrace.Enabled || !__result) return;
        try
        {
            if (carrier is null || map is null) return;
            // Only hauling to a cell is interesting here: the count of these per tick is small, and a partial
            // record of every storage query would drown the probe.
            _ = t;
            _ = CultureInfo.InvariantCulture;
            HaulProbe.Result(carrier, map, foundCell, needAccurateResult);
        }
        catch (Exception) { }
    }
}
