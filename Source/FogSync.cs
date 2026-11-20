using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Fog of war the host lifted or restored, applied on a replica through the same <c>FogGrid</c> methods.
///
/// <para><b>Why a replica needs this.</b> Fog is not a <c>Thing</c> - it is a bit array in <c>FogGrid.fogGrid</c>, one
/// bit per cell - and it lifts when a pawn walks somewhere on a tick the replica does not run. Without this channel the
/// replica keeps the fog it loaded with forever: a colonist who explores a new wing of the base walks into a black
/// rectangle that never clears, while the host shows the room. It is also the only channel here where being wrong is
/// invisible-but-total: fog hides terrain, things, roofs and snow, so every other map channel's work is wasted on a cell
/// nobody can see.</para>
///
/// <para><b>Why the two public writers and nothing else.</b> <c>Unfog(IntVec3)</c> and <c>Refog(CellRect)</c> are the
/// only player-driven changes: the first is called by <c>Pawn.DoFogReveal</c>-style paths as pawns move, the second when
/// the player re-fogs an area or a fog blocker is rebuilt. <c>UnfogWorker</c> is the private primitive both use, and
/// <c>ClearAllFog</c> is a map-init call - hooking either would resend the whole map.</para>
///
/// <para><b>Why the whole state is sent on the first frame.</b> A map loaded from a save has fog on every cell outside
/// the explored area - thousands of them - and no hook fired for any of them. A dirty-cell channel alone would leave the
/// replica one frame ahead of the save file and never correct. The baseline pass sends the set cells once; after that
/// only cells that move.</para>
internal static class FogSync
{
    /// <summary>Cells whose fog changed, keyed by map id.</summary>
    private static readonly Dictionary<int, HashSet<int>> dirty = [];

    /// <summary>Maps whose full fog state has not been sent yet.</summary>
    private static readonly HashSet<int> unsent = [];

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

    internal static List<ReplicaFog>? Publish()
    {
        if (dirty.Count == 0 && unsent.Count == 0) return null;
        foreach (var map in Find.Maps) unsent.Add(map.uniqueID);
        List<ReplicaFog>? result = null;
        foreach (var map in Find.Maps)
        {
            if (map.fogGrid is null) continue;
            var first = unsent.Contains(map.uniqueID);
            if (!first && !dirty.ContainsKey(map.uniqueID)) continue;
            var cells = dirty.TryGetValue(map.uniqueID, out var pending) ? pending : [];
            if (first)
            {
                // Every fogged cell, because a save's fog is state no hook ever reported.
                for (var index = 0; index < map.cellIndices.NumGridCells; index++)
                {
                    if (!map.fogGrid.IsFogged(index)) continue;
                    (result ??= []).Add(new ReplicaFog(map.uniqueID, index, true));
                    CellsSent++;
                }
            }
            foreach (var index in cells)
            {
                if (index < 0 || index >= map.cellIndices.NumGridCells) continue;
                // Read at publish time, not at hook time: one frame can unfog a cell and re-fog it, and only the final
                // state is worth sending.
                var fogged = map.fogGrid.IsFogged(index);
                if (first && fogged) continue;      // already covered by the baseline pass
                (result ??= []).Add(new ReplicaFog(map.uniqueID, index, fogged));
                CellsSent++;
            }
            unsent.Remove(map.uniqueID);
        }
        dirty.Clear();
        return result;
    }

    /// <summary>Applies one cell.
    ///
    /// <para>Lifting fog goes through <c>Unfog</c> rather than the bit array, because <c>UnfogWorker</c> also dirties the
    /// fog-of-war and things meshes, re-dirties the roof and temperature overlays, deletes a mine designation whose
    /// rock is gone, and notifies each thing in the cell that it is no longer hidden. Restoring fog goes through
    /// <c>Refog</c> for the same reason. Writing the bits directly would leave a cell that answers
    /// <c>IsFogged</c> correctly and draws the old picture.</para></summary>
    internal static void Apply(ReplicaFog state)
    {
        try
        {
            var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == state.Map);
            if (map?.fogGrid is null) return;
            if (state.Index < 0 || state.Index >= map.cellIndices.NumGridCells) return;
            if (map.fogGrid.IsFogged(state.Index) == state.Fogged) return;
            var cell = map.cellIndices.IndexToCell(state.Index);
            if (state.Fogged) map.fogGrid.Refog(new CellRect(cell.x, cell.z, 1, 1));
            else map.fogGrid.Unfog(cell);
            CellsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] fog apply failed (cell " + state.Index + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => dirty.Clear();

    internal static string Summary() =>
        $"迷雾 发出 {CellsSent} / 应用 {CellsApplied}（失败 {ApplyFailures}）";
}

/// <summary><c>UnfogWorker</c> is the private primitive every reveal funnels through.
///
/// <para><b>Why not <c>Unfog</c>.</b> <c>Unfog(c)</c> calls <c>UnfogWorker(c)</c> for its anchor cell, and then - for
/// every thing in that cell whose <c>Fillage</c> is <c>Full</c> - calls <c>UnfogWorker(otherCell)</c> directly for each
/// cell the thing occupies. Hooking <c>Unfog</c> therefore records only the anchor: a 3×3 building revealed by one pawn
/// would send one cell and leave the other eight fogged on the replica. Hooking the primitive covers both call sites,
/// which is the whole reason the game factors it out.</para>
///
/// <para>A private method with one overload, so it is resolved by hand - <c>nameof</c> cannot reach it and there is no
/// signature ambiguity to worry about.</para></summary>
[HarmonyPatch]
internal static class ReplicaFogLiftPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(FogGrid), "UnfogWorker");

    private static AccessTools.FieldRef<FogGrid, Map>? mapField;

    private static void Prefix(FogGrid __instance, IntVec3 c) =>
        FogSync.Touched((mapField ??= AccessTools.FieldRefAccess<FogGrid, Map>("map"))(__instance), c);
}

/// <summary><c>Refog</c> is the player re-fogging an area, or a fog blocker being rebuilt over revealed ground.</summary>
[HarmonyPatch(typeof(FogGrid), nameof(FogGrid.Refog))]
internal static class ReplicaFogRestorePatch
{
    private static AccessTools.FieldRef<FogGrid, Map>? mapField;

    private static void Prefix(FogGrid __instance, CellRect rect)
    {
        var map = (mapField ??= AccessTools.FieldRefAccess<FogGrid, Map>("map"))(__instance);
        if (map is null) return;
        // The rectangle is small in practice (a fog blocker's footprint, or a drag), so visiting its cells here is
        // cheaper than recording the rectangle and having the publish step re-derive it.
        foreach (var cell in rect) FogSync.Touched(map, cell);
    }
}
