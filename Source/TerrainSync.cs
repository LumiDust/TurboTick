using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Terrain the host changed, applied on a replica by calling the same <c>TerrainGrid</c> method the host did.
///
/// <para><b>Why a replica needs this at all.</b> Terrain is not a <c>Thing</c>: it lives in
/// <c>TerrainGrid.topGrid</c> / <c>underGrid</c> / <c>foundationGrid</c>, three flat arrays indexed by cell, and
/// <c>AuthoritativeFrame</c> enumerates things. A replica that misses a terrain change draws the wrong ground forever.
/// Measured consequences of the gap: a mined-out cave keeps drawing as rock (so a pawn appears to walk through a
/// wall), a floor the colony built keeps drawing as dirt, a moat keeps drawing as grass. None of it throws - the
/// picture is simply stale, which is why it went unnoticed through every earlier test.</para>
///
/// <para><b>Why the dirty-cell model rather than a field diff.</b> A map is 250×250 = 62 500 cells and terrain
/// changes a few dozen times an hour. Diffing the arrays would scan 62 500 entries per frame to find nothing; hooking
/// the setter records only the cell that changed. ColonyTogether reached the same design
/// (<c>NativeCells.Dirty</c>, ct045.decompiled.cs:269838) and for the same reason.</para>
///
/// <para><b>Why the setter is the hook, not a per-tick scan.</b> Every path that changes terrain - mining, flooring,
/// smoothing, terraforming, a genstep, a mod - ends in one of the four <c>TerrainGrid</c> methods below. There is no
/// other way in: the grids are private or public arrays written only by these methods and by
/// <c>ResetGrids</c>/<c>ExposeData</c>, which the replica reaches through its own save load.</para>
internal static class TerrainSync
{
    /// <summary>Cells whose terrain changed on this side since the last frame, keyed by map. Each entry is the cell
    /// index; the value sent is the terrain the host ended up with, read at publish time rather than at hook time so
    /// several writes to one cell inside a frame cost one entry.</summary>
    private static readonly Dictionary<int, HashSet<int>> dirty = [];

    /// <summary>Cells whose under-terrain changed. Kept apart from <see cref="dirty"/> because a cell can carry both a
    /// top layer and an under layer, and applying the wrong one to the wrong array leaves the cell permanently wrong.
    /// </summary>
    private static readonly Dictionary<int, HashSet<int>> dirtyUnder = [];

    private static readonly Dictionary<int, HashSet<int>> dirtyFoundation = [];

    internal static long TerrainChanges { get; private set; }
    internal static long TerrainApplied { get; private set; }
    internal static int TerrainFailures { get; private set; }

    internal static void Touched(Map? map, IntVec3 cell, int layer = 0)
    {
        if (map is null || !cell.InBounds(map)) return;
        var index = map.cellIndices.CellToIndex(cell);
        var bucket = layer switch { 1 => dirtyUnder, 2 => dirtyFoundation, _ => dirty };
        if (!bucket.TryGetValue(map.uniqueID, out var cells))
            bucket[map.uniqueID] = cells = [];
        cells.Add(index);
    }

    /// <summary>Collects every cell whose terrain changed since the last frame.
    ///
    /// <para>Empty in the overwhelming majority of frames, and then returns null for each of the three arrays so the
    /// frame carries nothing - the same rule the zone and letter channels use.</para></summary>
    internal static (List<ReplicaTerrain>? Top, List<ReplicaTerrain>? Under, List<ReplicaTerrain>? Foundation) Publish()
    {
        var top = Drain(dirty, 0);
        var under = Drain(dirtyUnder, 1);
        var foundation = Drain(dirtyFoundation, 2);
        return (top, under, foundation);
    }

    private static List<ReplicaTerrain>? Drain(Dictionary<int, HashSet<int>> bucket, int layer)
    {
        if (bucket.Count == 0) return null;
        List<ReplicaTerrain>? result = null;
        foreach (var (mapId, cells) in bucket)
        {
            var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == mapId);
            if (map?.terrainGrid is null) continue;
            foreach (var index in cells)
            {
                if (index < 0 || index >= map.cellIndices.NumGridCells) continue;
                var def = layer switch
                {
                    1 => map.terrainGrid.UnderTerrainAt(index),
                    2 => map.terrainGrid.FoundationAt(index),
                    _ => map.terrainGrid.TopTerrainAt(index),
                };
                if (def?.defName is not { Length: > 0 } name) continue;
                (result ??= []).Add(new ReplicaTerrain(mapId, index, name, layer));
                TerrainChanges++;
            }
        }
        bucket.Clear();
        return result;
    }

    /// <summary>Applies one cell by calling the host's own method, so everything that method does on the host -
    /// glow registration, the water set, the terrain-changed event, mesh invalidation - happens here too. Writing
    /// the grid arrays directly would skip all of it and leave a cell that looks right but lights wrong.</summary>
    internal static void Apply(ReplicaTerrain state)
    {
        try
        {
            var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == state.Map);
            // `cellIndices` is a struct field on `Map`, never null - only the map can be missing.
            if (map is null || map.terrainGrid is null) return;
            if (state.Index < 0 || state.Index >= map.cellIndices.NumGridCells) return;
            var def = DefDatabase<TerrainDef>.GetNamedSilentFail(state.Def);
            if (def is null)
            {
                TerrainFailures++;
                return;
            }
            var cell = map.cellIndices.IndexToCell(state.Index);
            switch (state.Layer)
            {
                case 1: map.terrainGrid.SetUnderTerrain(cell, def); break;
                case 2: map.terrainGrid.SetFoundation(cell, def); break;
                default: map.terrainGrid.SetTerrain(cell, def); break;
            }
            TerrainApplied++;
        }
        catch (Exception error)
        {
            TerrainFailures++;
            Log.Warning("[TurboTick] terrain apply failed (" + state.Def + " at " + state.Index + "): "
                + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        dirty.Clear();
        dirtyUnder.Clear();
        dirtyFoundation.Clear();
    }

    internal static string Summary() =>
        $"地形 变化 {TerrainChanges} / 应用 {TerrainApplied}（失败 {TerrainFailures}）";
}

/// <summary>The four ways terrain changes. Each one records the cell; <see cref="TerrainSync.Publish"/> reads the
/// resulting definition later, in the frame loop.
///
/// <para><c>SetTerrain</c> is not enough on its own: it forwards to <c>SetFoundation</c> for a foundation def and to
/// <c>SetTempTerrain</c> for a temporary one, so a foundation laid through <c>SetTerrain</c> would be recorded as a top
/// layer and applied to the wrong array. Hooking the specific method that actually wrote the array, and marking the
/// layer it wrote, is what keeps the three arrays apart.</para></summary>
[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
internal static class ReplicaTerrainTopPatch
{
    private static void Postfix(IntVec3 c, Map ___map)
    {
        if (!HeadlessRuntime.Active) return;
        TerrainSync.Touched(___map, c, 0);
    }
}

/// <summary>A temporary terrain (a bridge being built, a roof collapse) is not one of the three permanent layers: it
/// lives in <c>tempGrid</c> and is removed later. It is sent as a top-layer write only for the removal case - the
/// visible cell is what the player sees, and a replica that keeps drawing the temporary state after it was removed
/// shows a bridge that is gone.
[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetUnderTerrain))]
internal static class ReplicaTerrainUnderPatch
{
    private static void Postfix(IntVec3 c, Map ___map)
    {
        if (!HeadlessRuntime.Active) return;
        TerrainSync.Touched(___map, c, 1);
    }
}

[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetFoundation))]
internal static class ReplicaTerrainFoundationPatch
{
    private static void Postfix(IntVec3 c, Map ___map)
    {
        if (!HeadlessRuntime.Active) return;
        TerrainSync.Touched(___map, c, 2);
    }
}

/// <summary>Removing the top layer (a mined-out rock, a deconstructed floor) changes the visible cell without writing
/// a new def through <c>SetTerrain</c>, so it needs its own hook or the replica keeps drawing the removed layer.
[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveTopLayer))]
internal static class ReplicaTerrainRemovedPatch
{
    private static void Postfix(IntVec3 c, Map ___map)
    {
        if (!HeadlessRuntime.Active) return;
        // The resulting def is whatever the underneath layer now is, so the top layer is re-read at publish time and
        // sent as-is. A cell left with no terrain at all is skipped by `Publish`, which is correct: the replica's own
        // grid already holds whatever the cell reverts to.
        TerrainSync.Touched(___map, c, 0);
    }
}
