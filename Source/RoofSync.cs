using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Roofs the host built or removed, applied on a replica through the same <c>RooofGrid.SetRoof</c> the host used.
///
/// <para><b>Why this is a separate channel from terrain.</b> A roof is not a <c>Thing</c> either - it lives in
/// <c>RoofGrid.roofGrid</c>, one flat array indexed by cell - but unlike terrain it changes for a different reason and
/// through a different method, and it carries a different consequence. Terrain is what the ground looks like; a roof is
/// what the sky does above it: rain, temperature, light, whether a room counts as indoors, and whether
/// <c>Roofed()</c> is true at all. A replica with the wrong roof draws no roof over a colony that built one, and its
/// room stats disagree with the authority's.</para>
///
/// <para><b>Why the dirty-cell model rather than a diff.</b> Same arithmetic as terrain: 62 500 cells, a roof changes a
/// few dozen times an hour (when the player drags a roof area, or a wall comes down and the auto-roof vanishes). Hooking
/// <c>SetRoof</c> records only what moved.</para>
///
/// <para><b>Why <c>SetRoof</c> and not <c>RemoveRoofUnsafe</c>.</b> <c>RemoveRoofUnsafe</c> writes the array without
/// notifying anything - its name says so, and it is used by the grid's own rebuild path. Every player-driven change goes
/// through <c>SetRoof</c>, which is also the only one that invalidates the mesh, the district and the glow grid. Patching
/// the unsafe one would capture the rebuild as if it were a player edit.</para>
internal static class RoofSync
{
    /// <summary>Cells whose roof changed, keyed by map id. Holds nulls: "no roof here" is a real state that has to be
    /// sent, because removing a roof is exactly as visible as adding one.</summary>
    private static readonly Dictionary<int, HashSet<int>> dirty = [];

    internal static long RoofChanges { get; private set; }
    internal static long RoofApplied { get; private set; }
    internal static int RoofFailures { get; private set; }

    internal static void Touched(Map? map, IntVec3 cell)
    {
        if (map is null || !cell.InBounds(map)) return;
        // Loading a save writes every cell through `SetRoof`: `RoofGrid.ExposeData` reads the grid with
        // `MapExposeUtility.ExposeUshort` and its write closure calls `SetRoof(c, def)` per cell
        // (`Verse/RoofGrid.cs:34-40`). So the hook fires for all 62 500 cells of a 250x250 map, and the first frame after a
        // load would carry the entire roof grid - about 4 MB - to describe roofs the replica already has from the same
        // save. Measured: `屋顶改动：62500`.
        //
        // Note that terrain, snow and fog do NOT have this problem, for a reason worth recording: their own `ExposeData`
        // assigns the array element directly (`grid[i] = def`, `depthGrid[i] = val`, `LookBitArray`) instead of going
        // through the setter, so their hooks never see a save being read. RoofGrid is the only one whose load path routes
        // through its own public writer.
        if (Scribe.mode is LoadSaveMode.LoadingVars or LoadSaveMode.ResolvingCrossRefs or LoadSaveMode.PostLoadInit)
            return;
        var index = map.cellIndices.CellToIndex(cell);
        if (!dirty.TryGetValue(map.uniqueID, out var cells))
            dirty[map.uniqueID] = cells = [];
        cells.Add(index);
    }

    /// <summary>Collects every cell whose roof changed. Null in the overwhelming majority of frames.</summary>
    internal static List<ReplicaRoof>? Publish()
    {
        if (dirty.Count == 0) return null;
        List<ReplicaRoof>? result = null;
        foreach (var (mapId, cells) in dirty)
        {
            var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == mapId);
            if (map?.roofGrid is null) continue;
            foreach (var index in cells)
            {
                if (index < 0 || index >= map.cellIndices.NumGridCells) continue;
                // The def name is read at publish time rather than at hook time, so several writes to one cell inside
                // one frame cost one entry with the final value.
                var def = map.roofGrid.RoofAt(index);
                (result ??= []).Add(new ReplicaRoof(mapId, index, def?.defName ?? string.Empty));
                RoofChanges++;
            }
        }
        dirty.Clear();
        return result;
    }

    /// <summary>Applies one cell. An empty name means "no roof", which is `SetRoof(cell, null)` - not a no-op.
    ///
    /// <para>Calling the host's own method matters here for the same reason it does for terrain:
    /// <c>SetRoof</c> notifies the district, dirties the mesh, dirties the glow grid and raises the roof-changed
    /// event. Writing <c>roofGrid[index]</c> directly would produce a cell that reports the right roof in a query and
    /// draws the wrong one on screen.</para></summary>
    internal static void Apply(ReplicaRoof state)
    {
        try
        {
            var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == state.Map);
            if (map?.roofGrid is null) return;
            if (state.Index < 0 || state.Index >= map.cellIndices.NumGridCells) return;
            var cell = map.cellIndices.IndexToCell(state.Index);
            var wanted = state.Def is { Length: > 0 } name
                ? DefDatabase<RoofDef>.GetNamedSilentFail(name)
                : null;
            if (state.Def is { Length: > 0 } && wanted is null)
            {
                RoofFailures++;
                return;
            }
            if (map.roofGrid.RoofAt(cell) == wanted) return;
            map.roofGrid.SetRoof(cell, wanted);
            RoofApplied++;
        }
        catch (Exception error)
        {
            RoofFailures++;
            if (RoofFailures <= 6)
                Log.Warning("[TurboTick] roof apply failed (" + state.Def + " at " + state.Index + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => dirty.Clear();

    internal static string Summary() =>
        $"屋顶 变化 {RoofChanges} / 应用 {RoofApplied}（失败 {RoofFailures}）";
}

/// <summary>The one way a player changes a roof.
///
/// <para><c>SetRoof</c> is the only public writer that notifies - the roof-area designator, a roof collapsing when its
/// support is destroyed, an auto-roof appearing when a room is enclosed, and the game's own map init all end here.
/// <c>RemoveRoofUnsafe</c> is deliberately not hooked: it exists for the grid's rebuild path, so capturing it would
/// resend every cell the replica already has.</para></summary>
[HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
internal static class ReplicaRoofTouchedPatch
{
    /// <summary><c>RoofGrid.map</c> is private and has no accessor, so it is read through a cached
    /// <c>FieldRef</c>. Cached because the prefix runs for every roofed cell the moment a roof-area drag is
    /// released - a few hundred cells in one frame - and reflection per cell would be visible.</summary>
    private static AccessTools.FieldRef<RoofGrid, Map>? mapField;

    private static void Prefix(RoofGrid __instance, IntVec3 c) =>
        RoofSync.Touched((mapField ??= AccessTools.FieldRefAccess<RoofGrid, Map>("map"))(__instance), c);
}
