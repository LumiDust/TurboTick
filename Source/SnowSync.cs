using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Snow depth the host accumulated, applied on a replica through the same <c>SnowGrid.SetDepth</c>.
///
/// <para><b>Why a replica needs this.</b> Snow is not a <c>Thing</c> and is not terrain either: it lives in
/// <c>SnowGrid.depthGrid</c>, one float per cell. It accumulates from weather on a tick the replica does not run, so the
/// ground keeps whatever depth the save was loaded with. The visible result is a colony under a blizzard that looks like
/// midsummer, and because depth also feeds the cell's path cost and its <c>WeatherBuildupCategory</c>, a replica's
/// movement cost and its snow-clear work disagree with the host's.</para>
///
/// <para><b>Why the whole map is sent on the first frame and cells afterwards.</b> Unlike terrain and roofs - which only
/// exist where somebody put them - snow covers whatever the weather reached, and a colony founded before the first
/// snowfall starts at zero everywhere. A dirty-cell channel alone would therefore be correct but would miss nothing on a
/// fresh save and everything on a save loaded mid-winter. The first publish sends every non-zero cell; after that only
/// cells that moved.</para>
///
/// <para><b>Why the granularity is quantised.</b> <c>AddDepth</c> is called per cell per weather tick with fractions
/// around 1e-4, and <c>SetDepth</c> clamps to [0,1]. Sending full floats would be an entry per cell per frame forever;
/// quantising to 1/64 (about 1.5% of full depth, well under what the mesh shows) makes a cell change a handful of times
/// per snowfall.</para>
internal static class SnowSync
{
    /// <summary>Cells whose snow changed, keyed by map id. The value stored is the quantised depth at hook time; the
    /// publish step re-reads the real depth so several writes to one cell inside a frame cost one entry.</summary>
    private static readonly Dictionary<int, HashSet<int>> dirty = [];

    /// <summary>Maps whose full snow state has not been sent yet. A save loaded mid-winter has depth on thousands of
    /// cells and no hook ever fired for any of them, so the first frame has to carry all of it.</summary>
    private static readonly HashSet<int> unsent = [];

    private const float Quantum = 64f;

    /// <summary>Whether the per-map baseline flags have been seeded. See <see cref="Publish"/>.</summary>
    private static bool firstSeed = true;

    internal static long CellsSent { get; private set; }
    internal static long CellsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Touched(Map? map, IntVec3 cell)
    {
        if (map is null || !cell.InBounds(map)) return;
        var index = map.cellIndices.CellToIndex(cell);
        if (!dirty.TryGetValue(map.uniqueID, out var cells))
            dirty[map.uniqueID] = cells = [];
        cells.Add(index);
    }

    /// <summary>Collects what changed. On the first call for a map, every cell with non-zero depth.</summary>
    internal static List<ReplicaSnow>? Publish()
    {
        if (dirty.Count == 0 && unsent.Count == 0) return null;
        List<ReplicaSnow>? result = null;
        // Seeded once, and only once: `unsent` starts empty and is what marks a map as never sent. Re-adding every map on
        // every call (which this used to do, one line above) made `first` permanently true - a whole-map snow resend every
        // single frame. The census read `积雪格：0` throughout, because the pause meant no frame ran at all, so the number
        // that would have shown it was zero for an unrelated reason.
        if (firstSeed)
        {
            firstSeed = false;
            foreach (var map in Find.Maps) unsent.Add(map.uniqueID);
        }
        foreach (var map in Find.Maps)
        {
            var first = unsent.Contains(map.uniqueID);
            if (!first && !dirty.TryGetValue(map.uniqueID, out var pending)) continue;
            if (map.snowGrid is null) continue;
            var indices = first
                ? Enumerable.Range(0, map.cellIndices.NumGridCells)
                : (IEnumerable<int>)(dirty.TryGetValue(map.uniqueID, out var cells) ? cells : []);
            foreach (var index in indices)
            {
                if (index < 0 || index >= map.cellIndices.NumGridCells) continue;
                var cell = map.cellIndices.IndexToCell(index);
                var depth = map.snowGrid.GetDepth(cell);
                // On the baseline pass only cells that carry snow are worth sending: a fresh save is zero everywhere.
                if (first && depth <= 0f) continue;
                var quantised = MathF.Round(depth * Quantum) / Quantum;
                (result ??= []).Add(new ReplicaSnow(map.uniqueID, index, quantised));
                CellsSent++;
            }
            unsent.Remove(map.uniqueID);
        }
        dirty.Clear();
        return result;
    }

    /// <summary>Applies one cell. <c>SetDepth</c> is the host's own method: it clamps, updates <c>totalDepth</c>, marks
    /// the mesh dirty and adjusts the cell's path cost - all of which a direct array write would skip, leaving a cell
    /// that reports the right depth and draws and paths as if it had none.</summary>
    internal static void Apply(ReplicaSnow state)
    {
        try
        {
            var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == state.Map);
            if (map?.snowGrid is null) return;
            if (state.Index < 0 || state.Index >= map.cellIndices.NumGridCells) return;
            var cell = map.cellIndices.IndexToCell(state.Index);
            var current = map.snowGrid.GetDepth(cell);
            // Within half a quantum is the same value as far as the mesh is concerned; re-setting it would mark the
            // mesh dirty for nothing.
            if (MathF.Abs(current - state.Depth) < 0.5f / Quantum) return;
            map.snowGrid.SetDepth(cell, state.Depth);
            CellsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] snow apply failed (cell " + state.Index + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        dirty.Clear();
        // Clearing `unsent` would make the next publish resend the whole map; the baseline is per session, not per
        // reconnect, so it is left alone here. A reconnect that does need the whole map is served by the catch-up path,
        // which re-sends what the save point missed.
    }

    internal static string Summary() =>
        $"积雪 发出 {CellsSent} / 应用 {CellsApplied}（失败 {ApplyFailures}）";
}

/// <summary>The two ways snow depth changes: accumulation from weather and removal by a pawn clearing it.
///
/// <para><c>AddDepth</c> is the accumulation path and does not call <c>SetDepth</c> internally (it writes the array
/// directly after its own clamp), so both have to be hooked. <c>SetDepth</c> alone would miss every snowfall.</para>
/// </summary>
[HarmonyPatch(typeof(SnowGrid), nameof(SnowGrid.AddDepth))]
internal static class ReplicaSnowAddPatch
{
    private static AccessTools.FieldRef<SnowGrid, Map>? mapField;

    private static void Prefix(SnowGrid __instance, IntVec3 c) =>
        SnowSync.Touched((mapField ??= AccessTools.FieldRefAccess<SnowGrid, Map>("map"))(__instance), c);
}

[HarmonyPatch(typeof(SnowGrid), nameof(SnowGrid.SetDepth))]
internal static class ReplicaSnowSetPatch
{
    private static AccessTools.FieldRef<SnowGrid, Map>? mapField;

    private static void Prefix(SnowGrid __instance, IntVec3 c) =>
        SnowSync.Touched((mapField ??= AccessTools.FieldRefAccess<SnowGrid, Map>("map"))(__instance), c);
}
