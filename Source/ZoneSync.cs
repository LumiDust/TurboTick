using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Sends the host's zones, and rebuilds them on a display replica.
///
/// <para><b>Why this file exists.</b> Zones are not <c>Thing</c>s. They live in <c>Map.zoneManager.AllZones</c>, they
/// have no cell in the thing grid, and <c>AuthoritativeFrame</c> enumerates only things - so a frame could never carry
/// one. The consequence was visible and reported: the player drew a stockpile and a growing zone, the info card named
/// them and counted their cells (156 / 169 - those numbers came from the snapshot, so they were true), and the ground
/// showed no colour. <c>SectionLayer_Zones.Regenerate()</c> draws exactly the cells where
/// <c>zoneManager.ZoneAt(cell)</c> returns a zone (<c>Verse/SectionLayer_Zones.cs</c>), so "no colour" means "no zone
/// object here", not "the overlay is off" - <c>PlaySettings.showZones</c> defaults to true
/// (<c>RimWorld/PlaySettings.cs:12</c>) and the toggle in the screenshot was on.</para>
///
/// <para><b>Shape taken from ColonyTogether.</b> <c>ProjectZoneTemplate</c>
/// (<c>docs/mp-colonytogether/_decomp/ColonyTogether.Prototype.decompiled.cs:27987</c>) sends identity, kind, label,
/// the cell list, and then the per-kind fields that actually change behaviour: stockpile <c>Priority</c> and
/// <c>AllowedThingDefs</c>; growing <c>PlantDefToGrow</c>, <c>allowSow</c>, <c>allowCut</c>. Its rebuild side
/// (<c>:27664</c>) constructs the concrete <c>Zone_Stockpile</c>/<c>Zone_Growing</c>, calls
/// <c>Map.zoneManager.RegisterZone</c>, then <c>AddCell</c> per cell - which is also what this does, because
/// <c>AddCell</c> is what keeps the zone grid and the section mesh in step
/// (<c>Verse/Zone.cs:231</c> <c>Map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Zone)</c>). Registering without
/// adding cells would produce a zone that exists, has a label, and draws nothing.</para>
///
/// <para><b>Revision, not a dirty flag.</b> A zone has no change event a replica can see and no tick to poll on, so the
/// host digests the fields it would send and re-sends only when the digest moves. That makes a settings-only edit
/// (allowing one more item in a stockpile) observable, which a "cells changed" test would miss.</para></summary>
internal static class ZoneSync
{
    private static readonly Dictionary<int, long> sentRevision = [];
    private static readonly Dictionary<int, int> zoneMaps = [];

    internal static int ZonesSent { get; private set; }
    internal static int ZonesApplied { get; private set; }
    internal static int ZonesRemovedApplied { get; private set; }
    internal static int ZoneApplyFailures { get; private set; }

    /// <summary>Fishing-zone setting blobs applied (round 145). Counted apart from the zone count because a row that
    /// arrives with `Fishing` null - an older host - would otherwise look identical to settings that never changed.
    /// </summary>
    internal static int FishingSettingsApplied { get; private set; }
    internal static int ZoneRebuildMisses { get; private set; }

    /// <summary>Collects every zone whose digest changed since the last frame, plus the removals.
    ///
    /// <para>Called once per frame on the authority. Everything it touches is a small list:
    /// <c>AllZones</c> holds a handful of entries even in a mature colony, and the per-zone work is one string join over
    /// its own cells.</para></summary>
    internal static (List<ReplicaZone>? Zones, List<ReplicaZoneRemoval>? Removed) Publish()
    {
        List<ReplicaZone>? zones = null;
        List<ReplicaZoneRemoval>? removed = null;
        var live = new HashSet<int>();
        foreach (var map in Find.Maps)
        {
            var manager = map.zoneManager;
            if (manager is null) continue;
            var all = manager.AllZones;
            if (all is null) continue;
            for (var i = 0; i < all.Count; i++)
            {
                var zone = all[i];
                if (zone is null) continue;
                live.Add(zone.ID);
                zoneMaps[zone.ID] = map.uniqueID;
                var projection = Describe(zone, map.uniqueID);
                if (projection is null) continue;
                if (sentRevision.TryGetValue(zone.ID, out var previous) && previous == projection.Revision) continue;
                sentRevision[zone.ID] = projection.Revision;
                (zones ??= []).Add(projection);
                ZonesSent++;
            }
        }
        // A zone that vanished from every map was deleted. `sentRevision` is the only record of what the replica was
        // told, so it is also the only way to know a removal has to be sent.
        foreach (var id in sentRevision.Keys.ToArray())
        {
            if (live.Contains(id)) continue;
            sentRevision.Remove(id);
            var map = zoneMaps.Remove(id, out var previousMap) ? previousMap : 0;
            (removed ??= []).Add(new ReplicaZoneRemoval(id, map));
        }
        return (zones, removed);
    }

    /// <summary>One zone as it should look on a replica, or null for a kind that cannot be rebuilt faithfully.
    ///
    /// <para>Unsupported kinds are skipped rather than approximated: a zone whose settings a replica cannot reproduce
    /// would draw the right colour and then store the wrong things, which is worse than not drawing it. ColonyTogether
    /// makes the same choice with its <c>"unsupported"</c> kind string (<c>:28030</c>).</para></summary>
    private static ReplicaZone? Describe(Zone zone, int mapId)
    {
        var cells = new StringBuilder(zone.cells.Count * 8);
        foreach (var cell in zone.cells)
        {
            if (cells.Length > 0) cells.Append(';');
            cells.Append(cell.x).Append(',').Append(cell.z);
        }
        var label = zone.label ?? zone.GetType().Name;
        // Stockpiles and growing zones are the two the player draws constantly and the two whose settings change
        // behaviour. Everything else (fishing in 1.7, and whatever a mod adds) keeps its snapshot-era state.
        switch (zone)
        {
            case Zone_Stockpile stockpile:
            {
                var allowed = new List<string>();
                foreach (var def in stockpile.settings?.filter?.AllowedThingDefs ?? [])
                    if (def?.defName is { Length: > 0 } name) allowed.Add(name);
                allowed.Sort(StringComparer.Ordinal);
                var priority = (int)(stockpile.settings?.Priority ?? StoragePriority.Unstored);
                var joined = string.Join("|", allowed);
                return new ReplicaZone(zone.ID, mapId, "stockpile", label, cells.ToString(),
                    Digest("stockpile", cells, label, priority, joined, null, false, false),
                    priority, joined);
            }
            case Zone_Growing growing:
            {
                var plant = growing.PlantDefToGrow?.defName;
                return new ReplicaZone(zone.ID, mapId, "growing", label, cells.ToString(),
                    Digest("growing", cells, label, -1, null, plant, growing.allowSow, growing.allowCut),
                    -1, null, plant, growing.allowSow, growing.allowCut);
            }
            // Fishing (round 145). `repeatCount` is decremented once per catch (`:431893`), so a replica that never
            // catches keeps whatever the save loaded; and the five settings below are what `ShouldFishNow` - the
            // property the work giver branches on (`:312825`, `:312835`) - actually reads:
            //   `Allowed && OverTargetPopulation && !PausedDueToResourceCount && UnderTargetResourceCount`
            // where `RecheckPausedDueToResourceCount` (`:431891-431903`) derives the pause from `repeatMode`,
            // `pauseWhenSatisfied`, `targetCount` and `unpauseAtCount`, and `OverTargetPopulation`
            // (`:431905-431913`) reads `targetPopulationPct`. Every one of them is set from the zone's own tab
            // (`ITab_Fishing`: `:665021` resets, `:665042` steps), i.e. a player click on this peer. Measured before
            // this round: the fishing row carried `repeatCount` alone, so the authority fished on its own numbers
            // while the replica's tab showed the player's.
            case Zone_Fishing fishing:
            {
                var settings = string.Join(";",
                    ((int)fishing.repeatMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    fishing.targetCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    fishing.unpauseAtCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    fishing.pauseWhenSatisfied ? "1" : "0",
                    fishing.targetPopulationPct.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                return new ReplicaZone(zone.ID, mapId, "fishing", label, cells.ToString(),
                    Digest("fishing", cells, label, -1, null, null, false, false, fishing.repeatCount, settings),
                    -1, null, null, false, false, fishing.repeatCount, settings);
            }
            default:
                return null;
        }
    }

    private static long Digest(string kind, StringBuilder cells, string label, int priority, string? allowed,
        string? plant, bool sow, bool cut, int repeat = -1, string? settings = null)
    {
        // A stable FNV-1a over the text that would be sent. Not cryptographic and not meant to be: it only has to make
        // "nothing the replica cares about changed" cheap to decide.
        unchecked
        {
            var hash = 1469598103934665603UL;
            void Mix(string? text)
            {
                if (text is null) { hash ^= 0xFF; hash *= 1099511628211UL; return; }
                for (var i = 0; i < text.Length; i++) { hash ^= text[i]; hash *= 1099511628211UL; }
                hash ^= 0xFE;
                hash *= 1099511628211UL;
            }
            Mix(kind); Mix(cells.ToString()); Mix(label); Mix(priority.ToString());
            Mix(allowed); Mix(plant); Mix(sow ? "1" : "0"); Mix(cut ? "1" : "0");
            // Only the fishing kind passes a non-negative value, so every other kind hashes exactly as it did before.
            if (repeat >= 0) Mix(repeat.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Only the fishing kind passes this, so every other kind hashes exactly as it did before.
            if (settings is not null) Mix(settings);
            return (long)hash;
        }
    }

    // ---- replica side ---------------------------------------------------------------------------------------------

    private static readonly Dictionary<int, int> appliedZones = [];

    /// <summary>Applies one zone on a display replica: create it if absent, otherwise move its cells and settings.
    ///
    /// <para>The cell list is applied as a diff instead of "clear then add", because <c>Zone.RemoveCell</c> deregisters
    /// the zone when the last cell goes (<c>Verse/Zone.cs:248</c>) - clearing first would destroy the object mid-update
    /// and the subsequent <c>AddCell</c> calls would write into a deregistered zone.</para></summary>
    internal static void Apply(ReplicaZone state)
    {
        var map = FindMap(state.Map);
        if (map?.zoneManager is null) { ZoneApplyFailures++; return; }
        var manager = map.zoneManager;
        var zone = FindZone(manager, state.Id);
        if (zone is null)
        {
            zone = Create(state, manager);
            if (zone is null) { ZoneApplyFailures++; return; }
            try { manager.RegisterZone(zone); }
            catch (Exception error)
            {
                if (ZoneApplyFailures++ < 8)
                    Log.Warning("[TurboTick] 区域注册失败 " + state.Kind + " id=" + state.Id + "：" + error.Message);
                return;
            }
            appliedZones[state.Id] = state.Map;
        }
        ApplyCells(zone, state.Cells);
        ApplySettings(zone, state);
        ZonesApplied++;
    }

    private static Zone? Create(ReplicaZone state, ZoneManager manager)
    {
        try
        {
            Zone zone = state.Kind switch
            {
                "stockpile" => new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, manager),
                "growing" => new Zone_Growing(manager),
                // Fishing zones were simply absent from every replica: this switch had two cases and the publisher's
                // filter had two, so a fishing zone's cells were never drawn and its own settings never existed.
                "fishing" => new Zone_Fishing(manager),
                _ => null!
            };
            if (zone is null) return null;
            // The host's identity has to be preserved or the next settings-only frame addresses a different zone.
            zone.ID = state.Id;
            if (state.Label is { Length: > 0 } label) zone.label = label;
            return zone;
        }
        catch (Exception error)
        {
            if (ZoneApplyFailures++ < 8)
                Log.Warning("[TurboTick] 区域创建失败 " + state.Kind + " id=" + state.Id + "：" + error.Message);
            return null;
        }
    }

    private static void ApplyCells(Zone zone, string cells)
    {
        try
        {
            var wanted = new HashSet<IntVec3>();
            if (cells.Length > 0)
                foreach (var pair in cells.Split(';'))
                {
                    var comma = pair.IndexOf(',');
                    if (comma <= 0) continue;
                    if (!int.TryParse(pair.AsSpan(0, comma), out var x)) continue;
                    if (!int.TryParse(pair.AsSpan(comma + 1), out var z)) continue;
                    wanted.Add(new IntVec3(x, 0, z));
                }
            // Removal first: `RemoveCell` deregisters on the last cell, and doing it while the new cells are already
            // present would never reach zero. Remove-then-add also keeps the zone's own cell list from oscillating.
            if (zone.cells is { Count: > 0 } existing)
                foreach (var cell in existing.ToArray())
                    if (!wanted.Contains(cell)) zone.RemoveCell(cell);
            foreach (var cell in wanted)
                if (!zone.cells.Contains(cell)) zone.AddCell(cell);
        }
        catch (Exception error)
        {
            if (ZoneRebuildMisses++ < 8)
                Log.Warning("[TurboTick] 区域格子重建失败 id=" + zone.ID + "：" + error.Message);
        }
    }

    private static void ApplySettings(Zone zone, ReplicaZone state)
    {
        try
        {
            if (zone is Zone_Stockpile stockpile && state.Kind == "stockpile")
            {
                if (state.Priority >= 0) stockpile.settings.Priority = (StoragePriority)state.Priority;
                if (state.Allowed is { } allowed)
                {
                    // Same order ColonyTogether uses (`:27682`): disallow everything, then allow the host's set. Doing
                    // it the other way round would union the replica's defaults with the host's and quietly store more
                    // than the player permitted.
                    stockpile.settings.filter.SetDisallowAll(null, null);
                    foreach (var name in allowed.Split('|'))
                    {
                        if (name.Length == 0) continue;
                        var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                        if (def is not null) stockpile.settings.filter.SetAllow(def, true);
                    }
                }
            }
            else if (zone is Zone_Growing growing && state.Kind == "growing")
            {
                if (state.Plant is { Length: > 0 } plant)
                {
                    // `PlantDefToGrow` is a read-only property with a private backing field, and the public way in is
                    // `SetPlantDefToGrow` (`RimWorld/Zone_Growing.cs:213`) - which also raises the "plant changed" path
                    // that recomputes what is already sown.
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(plant);
                    if (def is not null) growing.SetPlantDefToGrow(def);
                }
                growing.allowSow = state.AllowSow;
                growing.allowCut = state.AllowCut;
            }
            else if (zone is Zone_Fishing fishing && state.Kind == "fishing")
            {
                // All six are public fields (`:431675-431687`), so these are plain writes. The cache
                // `pausedDueToResourceCount` is derived, and `PausedDueToResourceCount` only recomputes it once every
                // 60 ticks (`:431783-431790`) - so after writing the settings the cache is refreshed explicitly rather
                // than leaving the replica's zone on a stale answer for up to a second.
                if (state.RepeatCount >= 0) fishing.repeatCount = state.RepeatCount;
                if (state.Fishing is { Length: > 0 } fishingSettings)
                {
                    var parts = fishingSettings.Split(';');
                    if (parts.Length >= 5
                        && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var mode)
                        && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var target)
                        && int.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var unpause)
                        && float.TryParse(parts[4], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var population))
                    {
                        // Enum values travel as integers only so the wire does not depend on declaration order; a mode
                        // the replica's build does not know is clamped to the enum's own range instead of cast blind.
                        fishing.repeatMode = mode is >= 0 and <= (int)FishRepeatMode.DoForever
                            ? (FishRepeatMode)mode
                            : FishRepeatMode.TargetCount;
                        fishing.targetCount = target;
                        fishing.unpauseAtCount = unpause;
                        fishing.pauseWhenSatisfied = parts[3] == "1";
                        fishing.targetPopulationPct = population;
                        fishing.RecheckPausedDueToResourceCount();
                        FishingSettingsApplied++;
                    }
                }
            }
        }
        catch (Exception error)
        {
            if (ZoneApplyFailures++ < 8)
                Log.Warning("[TurboTick] 区域设置应用失败 id=" + zone.ID + "：" + error.Message);
        }
    }

    /// <summary>Removes a zone the host deleted. <c>DeregisterZone</c> also clears the selection if the player was
    /// looking at it (<c>Verse/ZoneManager.cs:58</c>), which is exactly what a local delete would do.</summary>
    internal static void Remove(ReplicaZoneRemoval state)
    {
        var map = FindMap(state.Map) ?? Find.CurrentMap;
        if (map?.zoneManager is null) return;
        var zone = FindZone(map.zoneManager, state.Id);
        if (zone is null) return;
        try
        {
            map.zoneManager.DeregisterZone(zone);
            appliedZones.Remove(state.Id);
            ZonesRemovedApplied++;
        }
        catch (Exception error)
        {
            if (ZoneApplyFailures++ < 8)
                Log.Warning("[TurboTick] 区域删除失败 id=" + state.Id + "：" + error.Message);
        }
    }

    private static Zone? FindZone(ZoneManager manager, int id)
    {
        var all = manager.AllZones;
        if (all is null) return null;
        for (var i = 0; i < all.Count; i++) if (all[i]?.ID == id) return all[i];
        return null;
    }

    private static Map? FindMap(int id)
    {
        foreach (var map in Find.Maps) if (map.uniqueID == id) return map;
        return null;
    }

    /// <summary>One line for the diagnostics block: how many zones this peer holds versus how many it was told
    /// about. A mismatch means the channel is not landing, which is otherwise invisible - a missing zone draws
    /// nothing and logs nothing.</summary>
    internal static string Summary()
    {
        var local = 0;
        foreach (var map in Find.Maps)
        {
            var all = map.zoneManager?.AllZones;
            if (all is null) continue;
            local += all.Count;
        }
        return $"区域同步 发送={ZonesSent} 应用={ZonesApplied} 删除={ZonesRemovedApplied}"
            + $" 失败={ZoneApplyFailures} 格子重建失败={ZoneRebuildMisses} 本端区域={local} 跟踪={appliedZones.Count}";
    }

    /// <summary>Forgets everything this peer believes about zones. Called when the world is replaced, because ids
    /// from the previous colony mean nothing in the new one and a stale digest would suppress the first send.</summary>
    internal static void ResetForNewWorld()
    {
        sentRevision.Clear();
        zoneMaps.Clear();
        appliedZones.Clear();
    }
}
