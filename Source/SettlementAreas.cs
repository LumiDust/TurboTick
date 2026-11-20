using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>Draws each settlement's land as a vanilla <c>Area</c>, so the player can see whose ground they are on.
///
/// <para><b>Why the multi-settlement plan needed this.</b> The gates decide whether an action is allowed, but nothing tells
/// the player <i>where</i> the boundary is. The result is a colony where a build order silently fails in one place and
/// works two cells over, which reads as a bug rather than as a rule. A settlement you cannot see is a settlement whose
/// refusals look arbitrary.</para>
///
/// <para><b>Why a vanilla <c>Area</c> and not custom drawing.</b> <c>Area</c> already has everything a territory overlay
/// needs: a per-cell bit grid, a colour, a label, a list entry, the map's own overlay toggle, and a <c>CellBoolDrawer</c>
/// that re-uploads only the sections it dirtied. Custom drawing would mean a section layer, a shader, its own dirty
/// tracking and the overlay UI to switch it on - several hundred lines to reach what the game already renders. It is also
/// the shape ColonyTogether converged on: its per-colony territory is an <c>Area</c>, and its own code only patches
/// <c>CellBoolDrawer.CreateMaterialIfNeeded</c> for the colour, not the drawing itself (ct045.decompiled.cs:93873).</para>
///
/// <para><b>Why <c>Area_Allowed</c> rather than a new subclass.</b> A subclass has to be saved by type name, which means the
/// type has to exist before the save loads - a load-order dependency on this assembly for a cosmetic overlay.
/// <c>Area_Allowed</c> is the one vanilla area that is <c>Mutable</c> and has a settable <c>RenamableLabel</c>, so it can
/// carry a settlement's name without a new type.</para>
///
/// <para><b>The one real cost of using <c>Area_Allowed</c>.</b> It is a <em>work-restriction</em> area, so a settlement
/// overlay would also act as an allow-list for every work giver that reads an allowed area - colonists would refuse to
/// clean or fight fires outside their own settlement, which is a rule nobody asked for and which the gates do not enforce.
/// <c>VanillaAreaIsWorkRestrictionGuard</c> below removes that: an area whose label carries the settlement marker is
/// reported as not-a-work-restriction, so <c>WorkGiver</c>s treat colonists as unrestricted while the overlay still
/// draws.</para>
internal static class SettlementAreas
{
    /// <summary>Marker in the area label. The label is the only thing that survives a save/load round trip for an
    /// <c>Area_Allowed</c> created at runtime, so it is what identifies these areas after a reload.
    ///
    /// <para><b>Internal, not private (round 84).</b> `AreaAllowedSync` needs the same marker to exclude these overlays
    /// from the player's own area list, and it had a second copy of the literal. Two copies of a string that decides
    /// <em>object identity</em> is the same defect as two copies of a decision: editing one and not the other makes the
    /// replica treat every settlement overlay as a player area and create a duplicate of it - with the marker missing, so
    /// it also becomes work-restrictable. Neither copy has any compiler-visible link to the other; the drift would be
    /// silent and would only show up as duplicated areas on a peer.</para></summary>
    internal const string Marker = "◈ ";

    /// <summary>The area per settlement, by settlement id. Kept so a resize or rename finds its own area instead of
    /// scanning the map's list by label - labels are player-editable and therefore not an identity.</summary>
    private static readonly Dictionary<int, Area_Allowed> areas = [];

    internal static string? LastFailure { get; private set; }

    /// <summary>Colours chosen to stay apart from each other on the overlay rather than to look pretty: the overlay draws
    /// over terrain and under roofs, so telling two settlements apart matters more than either looking nice.</summary>
    private static readonly Color[] palette =
    [
        new(0.35f, 0.75f, 1f),      // blue
        new(1f, 0.72f, 0.30f),      // amber
        new(0.62f, 0.92f, 0.55f),   // green
        new(0.92f, 0.55f, 0.90f),   // violet
        new(1f, 0.55f, 0.50f),      // coral
        new(0.60f, 0.90f, 0.95f),   // teal
    ];

    /// <summary>Rebuilds every settlement's area to match the data, and is idempotent on purpose: the caller does not have
    /// to know whether a settlement was added, removed or resized. A partial update that missed one case would leave an
    /// overlay showing land that belongs to nobody - worse than no overlay, because the player would trust it.</summary>
    internal static void Rebuild(SettlementData? data)
    {
        LastFailure = null;
        try
        {
            // **Every** map, not just the current one. Until this loop existed the overlay was rebuilt for
            // `Find.CurrentMap` only, so a second settlement on a second map had no area at all - measured:
            // `据点 2 个 … 据点领地 绘制 1 块`, two sites on maps 0 and 1, one drawn. The gate reads the table and would
            // have refused a build there, while the player saw no territory and no reason for the refusal.
            //
            // The bug had a worse half: the removal loop below keyed `live` to the current map only, so every area on
            // the *other* maps was torn out of its `AreaManager` on each rebuild. With two maps that is a rebuild that
            // deletes and re-creates the neighbour's overlay continuously.
            var maps = Find.Maps;
            if (maps is null || data is null || !data.Active)
            {
                if (maps is not null) foreach (var each in maps) Clear(each?.areaManager);
                return;
            }
            // Grouped by map first: `Ensure` needs the map the area belongs to, and a site carries only its id.
            foreach (var map in maps)
            {
                if (map is null) continue;
                var live = new HashSet<int>();
                foreach (var site in data.Sites)
                {
                    if (site is null || site.Retired || site.MapId != map.uniqueID) continue;
                    live.Add(site.Id);
                    var area = Ensure(map, site);
                    if (area is null) continue;
                    Fill(area, site);
                }
                // Areas for settlements that no longer exist **on this map**. Removed from the manager's list as well as
                // from the table: a registered area stays in the overlay UI, where the player can switch it on and see
                // land that belongs to nobody. Scoped to this map by the manager comparison - a global sweep here is
                // what erased the other map's overlay.
                foreach (var (id, area) in areas.ToList())
                {
                    if (area.areaManager != map.areaManager) continue;
                    if (live.Contains(id)) continue;
                    areas.Remove(id);
                    area.areaManager?.AllAreas.Remove(area);
                }
            }
        }
        catch (Exception error)
        {
            LastFailure = error.GetType().Name + ": " + error.Message;
            Log.Warning("[TurboTick] settlement areas: " + LastFailure);
        }
    }

    /// <summary>Finds or creates the area for one settlement. Also re-adopts an area that already exists with the right
    /// label: <c>areas</c> is process state and does not survive a reload, while the area itself was written into the save
    /// by <c>AreaManager.ExposeData</c> - so after a load there is an area on the map with no entry in the table, and
    /// creating a second one would leave the first drawn forever.</summary>
    private static Area_Allowed? Ensure(Map map, SettlementSite site)
    {
        if (areas.TryGetValue(site.Id, out var existing) && existing.areaManager == map.areaManager)
        {
            Align(existing, site);
            return existing;
        }
        var label = Label(site);
        foreach (var candidate in map.areaManager.AllAreas)
            if (candidate is Area_Allowed allowed && allowed.RenamableLabel == label
                && !areas.ContainsValue(allowed))
            {
                areas[site.Id] = allowed;
                Align(allowed, site);
                return allowed;
            }
        var created = new Area_Allowed(map.areaManager, label);
        ColorField(created) = palette[Math.Abs(site.Id) % palette.Length];
        map.areaManager.AllAreas.Add(created);
        areas[site.Id] = created;
        return created;
    }

    private static void Align(Area_Allowed area, SettlementSite site)
    {
        var wanted = Label(site);
        if (area.RenamableLabel != wanted) area.RenamableLabel = wanted;
        var colour = palette[Math.Abs(site.Id) % palette.Length];
        if (area.Color != colour) ColorField(area) = colour;
    }

    /// <summary>The label the player sees in the area list. Carries the marker (so the guard can recognise it) and the
    /// owner (so two settlements with the same name are still tellable apart).</summary>
    private static string Label(SettlementSite site) =>
        site.OwnerPlayer == 0
            ? Marker + site.Name
            : $"{Marker}{site.Name}（玩家 {site.OwnerPlayer}）";

    /// <summary>Sets the cells inside the rectangle and clears the ones outside it.
    ///
    /// <para>Clearing matters as much as setting: a settlement that shrinks must stop drawing where it no longer reaches,
    /// or the overlay shows a boundary the gates do not enforce. The existing cells are walked for the clear pass rather
    /// than diffing the map - an area holds one rectangle, so its own cell list is the smaller set.</para></summary>
    private static void Fill(Area_Allowed area, SettlementSite site)
    {
        var map = area.Map;
        foreach (var cell in area.ActiveCells.ToList())
            if (cell.x < site.MinX || cell.x > site.MaxX || cell.z < site.MinZ || cell.z > site.MaxZ)
                area[cell] = false;
        for (var x = Math.Max(0, site.MinX); x <= Math.Min(map.Size.x - 1, site.MaxX); x++)
            for (var z = Math.Max(0, site.MinZ); z <= Math.Min(map.Size.z - 1, site.MaxZ); z++)
            {
                var cell = new IntVec3(x, 0, z);
                if (!area[cell]) area[cell] = true;
            }
    }

    private static void Clear(AreaManager? manager)
    {
        if (manager is null) { areas.Clear(); return; }
        foreach (var (id, area) in areas.ToList())
        {
            areas.Remove(id);
            if (area.areaManager == manager) manager.AllAreas.Remove(area);
        }
    }

    /// <summary>Whether this area is a settlement overlay rather than a player-drawn work restriction.</summary>
    internal static bool IsOverlay(Area? area) =>
        area is Area_Allowed allowed && allowed.RenamableLabel is { } label
        && label.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary><c>Area_Allowed.colorInt</c> is private with no setter, so the overlay colour is reached through a cached
    /// field reference. Cached because <see cref="Rebuild"/> runs on every settlement edit.</summary>
    private static AccessTools.FieldRef<Area_Allowed, Color>? colorField;

    private static AccessTools.FieldRef<Area_Allowed, Color> ColorField =>
        colorField ??= AccessTools.FieldRefAccess<Area_Allowed, Color>("colorInt");

    /// <summary>How many overlays exist, and on which maps. The per-map breakdown is the point: "绘制 2 块" on a
    /// two-map colony used to be indistinguishable from two overlays on one map, and the failure it hid was a site on a
    /// map that had none.</summary>
    internal static string Summary()
    {
        if (areas.Count == 0) return "据点领地 未绘制";
        var perMap = new Dictionary<int, int>();
        foreach (var (_, area) in areas)
        {
            var map = area.areaManager?.map;
            var key = map?.uniqueID ?? -1;
            perMap[key] = perMap.GetValueOrDefault(key) + 1;
        }
        var parts = new List<string>(perMap.Count);
        foreach (var pair in perMap) parts.Add($"图{pair.Key}={pair.Value}");
        return $"据点领地 绘制 {areas.Count} 块（{string.Join("，", parts)}）";
    }
}

/// <summary>Keeps a settlement overlay out of the work-restriction list entirely.
///
/// <para><b>Why this guard is required, not a nicety.</b> <c>Area_Allowed</c> is the type vanilla uses to answer "where may
/// this colonist work", and <c>Area_Allowed.AssignableAsAllowed()</c> returns true - that is the predicate the pawn
/// assignment UI filters on. So without this guard a settlement overlay appears in every colonist's allowed-area dropdown,
/// and the moment a player picks it, colonists stop cleaning, stop fighting fires and stop snow-clearing outside their own
/// settlement. Nobody asked for that rule and the gates do not enforce it: the overlay is a picture of ownership, not a
/// work order.</para>
///
/// <para><b>Why <c>AssignableAsAllowed</c> and not the per-cell query.</b> Intercepting the cell predicate would leave the
/// overlay selectable and then quietly ignore it, which is worse: the pawn's player settings would show an area that does
/// nothing, and the player would conclude the work system is broken. Refusing the assignment means the overlay simply never
/// offers itself. It is also the narrower patch - one bool on a predicate with no arguments, rather than a hot per-cell
/// path that every work giver walks in a loop.</para>
///
/// <para><b>Why a prefix returning false rather than a new area type.</b> The alternative is a custom <c>Area</c> subclass,
/// which has to be saved by type name and therefore has to exist before the save loads - a load-order dependency on this
/// assembly for a cosmetic overlay.</para></summary>
[HarmonyPatch(typeof(Area), nameof(Area.AssignableAsAllowed))]
internal static class VanillaAreaIsWorkRestrictionGuard
{
    private static bool Prefix(Area __instance, ref bool __result)
    {
        if (!SettlementAreas.IsOverlay(__instance)) return true;
        // Not assignable as a work area. The overlay still draws and still appears in the area overlay list.
        __result = false;
        return false;
    }
}
