using System.Reflection;
using System.Globalization;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Synchronises a whole <see cref="Designator"/> action instead of the low-level mutation it performs.
///
/// Why this level, and why the local call is cancelled:
///
/// A blueprint is a *thing*, so its id comes from <c>UniqueIDsManager</c> at spawn time. Placing it locally on the
/// origin while the other peers place it at the stamped tick advances that peer's counter by one early - and every
/// thing created afterwards (frames from construction, drops, corpses) then gets an id with a permanent offset that
/// never converges. Measured on the same tick: `things` equal, `bdig` equal, `tdig` different, and it stayed different
/// for the rest of the run. Local-first placement and an id-based state digest cannot coexist.
///
/// The concern that cancelling would break the building designator's drag is wrong: the drag rectangle is drawn by
/// <c>DesignationManagerOnGUI</c> / <c>DrawMouseAttachments</c>, and placement only happens when the player releases,
/// through <c>DesignateMultiCell</c>. Cancelling there leaves the drag untouched. (Verified against ColonyTogether's
/// source, see docs/mp-colonytogether/README.md; Zetrith Multiplayer does the same in
/// Source/Client/Patches/Designators.cs:26-69.)
///
/// Cost: the building appears after the round trip (~8 ticks, about 0.13 s), not instantly.
/// </summary>
internal static class DesignatorSync
{
    internal const string CommandKind = CommandCodec.Designator;

    /// <summary>The kind a cancel drag travels under: <see cref="CommandCodec.CancelDrag"/>.
    ///
    /// <para>Not <see cref="CommandCodec.Designator"/>, which is an entity-lifecycle kind and is therefore skipped by
    /// a display replica (<c>AuthoritativeReplica.AcceptsCommand</c>). A cancel changes no object identity - it
    /// destroys blueprints and clears marks - and no frame carries marks, so a replica that skipped it simply kept the
    /// cancelled markers on screen.</para>
    ///
    /// <para>Why the drag needs its own kind at all rather than riding on the per-thing path: `Designator_Cancel`
    /// destroys <c>Blueprint</c>/<c>Frame</c> one per cell, and each of those used to travel as its own `cancel-thing`
    /// command with its own round trip - and each cancelled the local <c>Destroy</c>, so this peer showed nothing
    /// until the reply came back. What the player sees when any of them is missed is "only the first couple of cells
    /// cancel". A drag is one gesture, so it travels as one command and every peer replays vanilla's own loop.</para>
    ///
    /// <para>The cells are carried rather than recomputed: <c>Designator_Cancel.DesignateSingleCell</c> removes
    /// <i>every</i> cancelable designation under a cell and also destroys the blueprint there, so "the cells the
    /// player dragged over" is the whole input. Payload shape lives in <see
    /// cref="CommandCodec.CancelDragCommand"/>.</para></summary>
    internal const string CancelCommandKind = CommandCodec.CancelDrag;

    /// <summary>True while the player's own cancel drag is running its vanilla loop.
    ///
    /// <para>`Designator_Cancel.DesignateSingleCell` destroys each blueprint through
    /// <c>Thing.Destroy(DestroyMode.Cancel)</c>, which has its own capture patch. That patch would publish one
    /// `cancel-thing` per object - the exact fan-out this class exists to replace - and it would cancel the local
    /// destroy as well, leaving the rectangle on screen. The flag makes the per-thing patch stand down for the
    /// duration; the drag's own command already carries every cell.</para>
    ///
    /// <para>Set before the vanilla commit and cleared in a postfix, so a designator that throws cannot leave the
    /// suppression on and silently disable blueprint cancellation for the rest of the session.</para></summary>
    [ThreadStatic] internal static bool SuppressingPerThingCancel;

    private const string BuildKind = "build";

    /// <summary>A zone drag: <c>zone|stockpile|cells…</c>. The kind string is the zone's type, not its label, because
    /// the label is cosmetic and the type decides which <c>Zone</c> subclass the authority constructs.</summary>
    private const string ZoneKind = "zone";

    /// <summary>An allowed-area paint stroke. Deliberately its own kind rather than a <see cref="ZoneKind"/>:
    /// a zone drag names a zone kind and creates if missing, while this names an existing area by label and only ever
    /// sets or clears bits. The payload is <c>label|add|map|x,z|x,z…</c>.
    ///
    /// <para>And its own kind rather than <see cref="CommandKind"/> for a second reason: that one is an
    /// entity-lifecycle kind (<see cref="CommandCodec.IsEntityLifecycle"/>), so a display replica drops it before the
    /// apply runs - and a stroke creates nothing. See <see cref="CommandCodec.AreaPaint"/>.</para></summary>
    private const string AreaKind = CommandCodec.AreaPaint;

    /// <summary>Which <c>Zone</c> subclass this designator creates.
    ///
    /// <para>Read from <c>Designator_ZoneAdd.zoneTypeToPlace</c> (<c>RimWorld/Designator_ZoneAdd.cs:10</c>,
    /// <c>protected Type</c>) rather than from the designator's own type name: the concrete designators are one per zone
    /// kind and a mod can add more, while the field is the actual contract - <c>MakeNewZone</c> returns an instance of
    /// exactly this type.</para></summary>
    private static string? ZoneAddKind(Designator_ZoneAdd designator)
    {
        try
        {
            zoneTypeField ??= AccessTools.Field(typeof(Designator_ZoneAdd), "zoneTypeToPlace");
            if (zoneTypeField?.GetValue(designator) is not Type type) return null;
            if (type == typeof(Zone_Stockpile)) return "stockpile";
            if (type == typeof(Zone_Growing)) return "growing";
            return null;
        }
        catch (Exception) { return null; }
    }

    private static System.Reflection.FieldInfo? zoneTypeField;

    private static List<GameCommand> EncodeZone(Designator_ZoneAdd designator, IReadOnlyList<IntVec3> cells)
    {
        var chunks = new List<GameCommand>();
        var kind = ZoneAddKind(designator);
        if (kind is null)
        {
            // Counted, not dropped silently: a zone kind this build does not know how to rebuild is a real gap and
            // must be visible rather than look like a gesture the player never made.
            skippedZoneKind++;
            return chunks;
        }
        for (var start = 0; start < cells.Count; start += MaxCells)
        {
            var end = Math.Min(start + MaxCells, cells.Count);
            var text = new System.Text.StringBuilder(16 + (end - start) * 8);
            // Map index third, for the same reason as the build payload: the designator paints on `Find.CurrentMap`.
            text.Append(ZoneKind).Append('|').Append(kind).Append('|')
                .Append(designator.Map?.uniqueID ?? -1);
            for (var i = start; i < end; i++) text.Append('|').Append(cells[i].x).Append(',').Append(cells[i].z);
            chunks.Add(new GameCommand(0, 0, CommandKind, System.Text.Encoding.UTF8.GetBytes(text.ToString())));
        }
        return chunks;
    }

    internal static int skippedZoneKind;
    internal static int zonesReplayed;
    internal static int zoneReplayFailures;

    /// <summary>One paint stroke on a player-created allowed area, addressed by label.
    ///
    /// <para><b>Why the label and not the id.</b> `Area.ID` comes from `Find.UniqueIDsManager.GetNextAreaID()`
    /// (`:63996`), a per-peer counter; the label is what `Area_Allowed` actually saves (`:392903-392909`) and vanilla
    /// keeps unique per map (`Dialog_RenameArea.NameIsValid` `:196418-196429`). A stroke that arrives after the area was
    /// renamed would then miss - which is correct, because the rename travels through
    /// <see cref="AreaAllowedSync"/>'s whole-list reconcile and the label is exactly what it keys on.</para>
    ///
    /// <para><b>Add versus clear is read off the concrete type, because the base throws the mode away.</b>
    /// `Designator_AreaAllowed`'s constructor takes a <c>DesignateMode</c> and then never assigns it
    /// (`:618139-618144` - the body only sets the two sounds), unlike `Designator_AreaHome` which stores it in a field
    /// (`:618297`). There is therefore no property to read, and the two vanilla subclasses are the only evidence of
    /// direction: Expand sets and Clear clears (`:618202-618205`, `:618226-618229`).</para></summary>
    private static List<GameCommand> EncodeArea(Designator designator, IReadOnlyList<IntVec3> cells)
    {
        var chunks = new List<GameCommand>();
        var area = Designator_AreaAllowed.SelectedArea;
        if (area is null)
        {
            // Nothing selected is not a gap in the channel: vanilla's own `CanDesignateCell` refuses every cell in this
            // state (`:618196`), so the player cannot draw either and there is no gesture to carry.
            skippedAreaTarget++;
            return chunks;
        }
        var add = designator is not Designator_AreaAllowedClear;
        var map = Find.CurrentMap;
        if (map is null) { skippedMap++; return chunks; }
        // A `|` in the label would split the payload, and the label comes from a text field the player types into.
        // Refused at the source rather than escaped: there is no other command in this codec that escapes a value, and a
        // stroke is recoverable (the player can rename the area) while a corrupted payload is not.
        var label = area.Label;
        if (label is null || label.Length == 0 || label.Contains('|'))
        {
            skippedAreaLabel++;
            Note("自建区域标签含分隔符", label ?? "<null>");
            return chunks;
        }
        for (var start = 0; start < cells.Count; start += MaxCells)
        {
            var end = Math.Min(start + MaxCells, cells.Count);
            var slice = new List<(int X, int Z)>(end - start);
            for (var i = start; i < end; i++) slice.Add((cells[i].x, cells[i].z));
            chunks.Add(CommandCodec.AreaPaintCommand(label, add, map.uniqueID, slice, 0));
        }
        return chunks;
    }

    /// <summary>Strokes that found no `SelectedArea`. Counted rather than silently dropped: the same reading is
    /// produced by "the player never opened the area list", which is harmless, and "the capture reads the wrong static",
    /// which loses every stroke.</summary>
    internal static int skippedAreaTarget;
    internal static int skippedAreaLabel;
    internal static int areasPainted;
    internal static int areaCellsPainted;
    internal static int areaPaintMisses;

    /// <summary>A drag is bounded by the wire, not just by the map.
    ///
    /// <para>Each cell costs at most <c>|249,249</c> = 8 bytes, and the server refuses any command payload over
    /// 64 KiB (<c>DedicatedServer.RelayCommandAsync</c>), so the ceiling is 6000 cells ≈ 48 KiB with room for the
    /// prefix. A drag larger than that is split by <see cref="CaptureDesignator"/> into several commands rather than
    /// dropped: the designator's commit loop and the wire limit are different constraints.</para></summary>
    private const int MaxCells = 6000;

    /// <summary>Every command this gesture has to travel as.
    ///
    /// <para>Splitting here rather than in the apply path keeps the receiver dumb: it replays whatever cells a payload
    /// carries, and a 200-cell drag and two 100-cell drags are the same thing to it. A drag bigger than
    /// <see cref="MaxCells"/> is the only case that produces more than one, and it is bounded by the map (250×250).
    /// </para>
    ///
    /// <para>Bytes rather than a string because the two kinds are shaped differently: a build payload is a
    /// human-readable field list this class owns, a cancel drag is a coordinate list <see
    /// cref="CommandCodec.CancelDragCommand"/> owns and the tests can round-trip without the game assembly.</para></summary>
    internal static List<GameCommand> EncodeChunks(Designator designator, IReadOnlyList<IntVec3> cells)
    {
        var chunks = new List<GameCommand>();
        if (cells.Count == 0) return chunks;
        if (designator is Designator_Cancel)
        {
            var map = Find.CurrentMap;
            if (map is null) return chunks;
            for (var start = 0; start < cells.Count; start += MaxCells)
            {
                var end = Math.Min(start + MaxCells, cells.Count);
                var pairs = new List<(int X, int Z)>(end - start);
                for (var i = start; i < end; i++) pairs.Add((cells[i].x, cells[i].z));
                chunks.Add(CommandCodec.CancelDragCommand(map.uniqueID, pairs, 0));
            }
            return chunks;
        }
        if (designator is not Designator_Build)
        {
            // Zone drags are captured here for the same reason the blueprint path exists: without it the gesture is
            // purely local. Measured with a stockpile (156 cells) and a growing zone (169 cells) drawn on a replica:
            // the info card named them and counted their cells, the ground drew no colour, and the authority reported
            // `本端区域=0` - i.e. the zones never existed on the side that simulates. `SectionLayer_Zones.Regenerate`
            // draws only cells where `zoneManager.ZoneAt` returns a zone, so no zone object means no overlay.
            if (designator is Designator_ZoneAdd zoneAdd) return EncodeZone(zoneAdd, cells);
            // Painting an allowed area. Both `Designator_AreaAllowedExpand` and `Designator_AreaAllowedClear` are
            // `Designator_Cells` subclasses whose whole body is `SelectedArea[c] = true/false`
            // (`:618202-618205`, `:618226-618229`), so until this branch existed the stroke published nothing at all -
            // and an allowed area is where a colonist may walk, so the two peers disagreed about reachability.
            if (designator is Designator_AreaAllowed) return EncodeArea(designator, cells);
            return chunks;
        }
        for (var start = 0; start < cells.Count; start += MaxCells)
        {
            var end = Math.Min(start + MaxCells, cells.Count);
            var slice = new IntVec3[end - start];
            for (var i = start; i < end; i++) slice[i - start] = cells[i];
            var payload = EncodeOne(designator, slice);
            if (payload is not null)
                chunks.Add(new GameCommand(0, 0, CommandKind, System.Text.Encoding.UTF8.GetBytes(payload)));
        }
        return chunks;
    }

    /// <summary>A build payload. Cancel drags do not come through here: theirs is a coordinate list owned by
    /// <see cref="CommandCodec.CancelDragCommand"/> so it can be decoded without the game assembly.</summary>
    private static string? EncodeOne(Designator designator, IReadOnlyList<IntVec3> cells)
    {
        if (designator is not Designator_Build build) return null;
        if (build.PlacingDef is not ThingDef def || def.defName is null or "") return null;
        var stuff = build.StuffDef?.defName ?? "";
        var rot = PlacingRot(build);
        // The map travels as field 2, before the cells: `Designator.Map` is `Find.CurrentMap` (`:162502`), the cell loop
        // below runs on that map, and with two maps loaded the authority may be looking at a different one. Without the
        // index the replay would build at the same coordinates on whichever map happened to be current - valid
        // coordinates, wrong map, no error.
        // **Style and glower colour, fields 5 and 6 (round 122).**
        //
        // `Designator_Build.DesignateSingleCell` -> `PlaceThing` writes three things the player chose *after* the
        // blueprint exists: `blueprint.StyleSourcePrecept` (`:622504`), `blueprint.StyleDef` (`:622507`) and
        // `blueprint.glowerColorOverride` (`:622509`). All three are read off the **local designator instance**
        // (`styleDef`, `sourcePrecept`, `glowerColorOverride` - `:621993-621999`), and a designator is per-peer UI state:
        // the peer that did not press the change-style gizmo has null in all three. So a styled building - the normal
        // way to build anything an ideoligion styles, or any glower the player recoloured - arrived on every other peer
        // as a plain blueprint, and stayed plain: `Blueprint_Build.MakeSolidThing` (`:570393-570398`) copies those
        // fields into the Frame, which becomes the Building. Blueprints are in none of the three sweeps
        // (`IsBuildingArtificial` is `category == Building || IsFrame`, `:46179`), so nothing corrected it later.
        //
        // Carried on the designator command rather than written onto the blueprint afterwards, because setting the
        // fields on the reconstructed designator makes vanilla's own two write sites run - including the direct-spawn
        // branch (`:622482-622496`) that never creates a blueprint at all, which a post-hoc blueprint write would miss.
        var text = new System.Text.StringBuilder();
        text.Append(BuildKind).Append('|').Append(def.defName).Append('|').Append(stuff).Append('|').Append(rot)
            .Append('|').Append(designator.Map?.uniqueID ?? -1)
            .Append('|').Append(build.styleDef?.defName ?? string.Empty)
            .Append('|').Append(SharedActions.EncodeColor(build.glowerColorOverride));
        foreach (var cell in cells) text.Append('|').Append(cell.x).Append(',').Append(cell.z);
        return text.ToString();
    }

    /// <summary>Replays a captured designator action. Runs inside `Applying`, so the per-cell captures stay out of the
    /// way and the mutations happen exactly once on this peer.</summary>
    /// <remarks>
    /// Every early return here used to be silent, and that is the whole reason "the blueprint never appears" could not
    /// be diagnosed: two commands were relayed (`commands=designator:2/0`), the authority logged taking them, nothing
    /// threw, and the world stayed empty. "No exception" only proves the call returned, not that a blueprint exists -
    /// <c>Designator_Build.DesignateSingleCell</c> exits quietly on a failed <c>CanPlaceBlueprintAt</c>, and the tutor
    /// guard returns before touching the map at all. So each cell now reports the refusal reason and whether a
    /// <see cref="Blueprint"/> is actually sitting on that cell afterwards.
    /// </remarks>
    /// <summary>The blueprints this replay just placed, kept so the next probe can report what became of them.
    /// <para>The cell check below proves a blueprint existed at that instant; it says nothing about the next tick, and
    /// "本端蓝图 0" next to "有蓝图 117" is exactly that gap. A reference is the only way to ask the object itself
    /// whether it was destroyed, whether it left the lister, or whether it is still sitting there uncounted.</para>
    /// </summary>
    internal static readonly List<Blueprint> LastPlaced = [];

    /// <summary>Replays that carried a style or a glower colour. Printed beside the replay count because `0` has two
    /// causes with opposite fixes: nobody placed a styled building this session, or the two new fields never decoded.</summary>
    internal static int stylesCarried;

    /// <summary>Rebuilds a zone drag on this peer by running vanilla's own commit loop.
    ///
    /// <para>Deliberately not a hand-written "make a zone, add these cells": vanilla's
    /// <c>Designator_ZoneAdd.DesignateMultiCell</c> (<c>RimWorld/Designator_ZoneAdd.cs:117</c>) does four things that
    /// matter and none of them are obvious - it reuses or creates <c>SelectedZone</c>, it only grows the zone through
    /// <em>cardinally adjacent</em> cells so a disconnected drag becomes several zones, it calls <c>CheckContiguous</c>,
    /// and for a stockpile it drops haul designations on what is already stored. Re-implementing that would drift from
    /// vanilla the first time either side changes.</para>
    ///
    /// <para>So this reconstructs the same concrete designator the player used and lets it commit. The designator is
    /// found through the zone type rather than instantiated directly, because <c>Designator_ZoneAdd</c> is abstract and
    /// the concrete type is also what carries the correct icon, sound and <c>zoneTypeToPlace</c>.</para></summary>
    /// <summary>Entry point for a paint stroke, called from the shared command switch.
    ///
    /// <para><b>No create path here on purpose.</b> An area that does not exist on this peer is not this command's
    /// problem: <see cref="AreaAllowedSync"/> reconciles the whole list by label, so a stroke naming a missing area means
    /// that list is a frame behind, and creating one here would place it under an id this peer allocated and then race
    /// the reconcile. Counted as a miss, so a permanent version of that state is visible in the census.</para>
    ///
    /// <para>Separate from <see cref="Apply"/> because that one is reached only under the <c>designator</c> kind, which is
    /// entity-lifecycle: a replica drops it before any apply runs. See <see cref="CommandCodec.AreaPaint"/>.</para></summary>
    internal static void ApplyPaint(string payload)
    {
        requests++;
        LastPlaced.Clear();
        if (!CommandCodec.TryDecodeAreaPaint(Encoding.UTF8.GetBytes(payload), out var label, out var add, out var mapIndex,
                out var cells))
        {
            skippedShape++;
            Note("自建区域形状", payload);
            return;
        }
        ApplyArea(label, add, mapIndex, cells);
    }

    private static void ApplyArea(string label, bool add, int mapIndex, IReadOnlyList<(int X, int Z)> cells)
    {
        var map = FindMapByUniqueId(mapIndex);
        if (map is null) { skippedMap++; Note("无地图", mapIndex.ToString(CultureInfo.InvariantCulture)); return; }
        var manager = map.areaManager;
        if (manager?.AllAreas is null) { areaPaintMisses++; return; }
        Area_Allowed? area = null;
        foreach (var candidate in manager.AllAreas)
            if (candidate is Area_Allowed allowed && allowed.Label == label) { area = allowed; break; }
        if (area is null) { areaPaintMisses++; Note("无自建区域:" + label, label); return; }
        var painted = 0;
        foreach (var (x, z) in cells)
        {
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) continue;
            // The indexer, not `Set`: `Area.Set(IntVec3, bool)` is `protected virtual` (`:64020`). This also keeps a
            // subclass that overrides `Set` to dirty its own overlay in the loop.
            if (area[cell] == add) continue;
            area[cell] = add;
            painted++;
        }
        areasPainted++;
        areaCellsPainted += painted;
    }

    /// <summary>The map id a payload carries at <paramref name="index"/>, or -1 when the field is absent or not a number -
    /// which is what a payload written before the field existed looks like, and which makes the caller fall back to the
    /// current map.</summary>
    private static int ParsedMapIndex(string[] parts, int index) =>
        index < parts.Length && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;

    /// <summary>Resolves the id every map-keyed payload carries to a map.
    ///
    /// <para><b>It is <c>Map.uniqueID</c>, not <c>Map.Index</c>.</b> `Index` is `Find.Maps.IndexOf(this)`
    /// (`:76452`) - a <em>position in a list</em>, not an identity: when one map is removed every later map's index
    /// shifts down, so a command still in flight (or a frame in the relay queue) resolves to a different map. That is the
    /// same class of mistake as keying a plan by `Plan.ID`, and it is worse here because the wrong map still accepts the
    /// cells. `uniqueID` (`:76213`) is assigned once and never reused, and `AreaSync` / `ZoneSync` / `FogSync` /
    /// `AreaAllowedSync` already travel by it.</para>
    ///
    /// <para>Both ids are small integers, so a peer holding only one map cannot tell them apart - and that is exactly why
    /// this has to be decided from the field's definition rather than from a passing test.</para></summary>
    private static Map? FindMapByUniqueId(int uniqueId)
    {
        if (uniqueId < 0) return null;
        foreach (var map in Find.Maps)
            if (map.uniqueID == uniqueId) return map;
        return null;
    }

    private static void ApplyZone(string[] parts)
    {
        if (parts.Length < 3) { skippedShape++; Note("区域形状", string.Join("|", parts)); return; }
        var kind = parts[1];
        // Field 2 is the map the drag was drawn on. `-1` (or an unparseable value) means a payload from a build that did not
        // carry one; falling back to the current map keeps that case working exactly as before.
        var map = FindMapByUniqueId(ParsedMapIndex(parts, 2)) ?? Find.CurrentMap;
        if (map is null) { skippedMap++; Note("无当前地图", string.Join("|", parts)); return; }
        var designator = FindZoneDesignator(kind);
        if (designator is null) { skippedZoneKind++; Note("无区域设计器:" + kind, string.Join("|", parts)); return; }
        using var mapScope = CurrentMapScope.Enter(map);
        if (designator.Map != map)
        {
            // The designator's own map is the current one; if the scope could not move it, painting would create the zone on
            // the wrong map. Refusing and counting beats a zone that exists somewhere the player never drew.
            wrongMap++;
            Note("区域重放地图不符", kind + "@" + map.Index.ToString(CultureInfo.InvariantCulture));
            return;
        }
        var cells = new List<IntVec3>(parts.Length - 3);
        for (var i = 3; i < parts.Length; i++)
        {
            var pair = parts[i].Split(',');
            if (pair.Length != 2) continue;
            if (!int.TryParse(pair[0], out var x) || !int.TryParse(pair[1], out var z)) continue;
            cells.Add(new IntVec3(x, 0, z));
        }
        if (cells.Count == 0) { skippedShape++; return; }
        try
        {
            designator.DesignateMultiCell(cells);
            zonesReplayed++;
        }
        catch (Exception error)
        {
            zoneReplayFailures++;
            Note("区域重放异常:" + error.GetType().Name, string.Join("|", parts));
        }
    }

    /// <summary>The live designator instance for a zone kind, taken from the architect menu.
    ///
    /// <para>Built through <c>DesignatorUtility</c> rather than <c>new</c>: each concrete designator sets its own
    /// <c>zoneTypeToPlace</c>, sounds and default label in its constructor, and instantiating the abstract base is not
    /// possible at all. Falls back to reflection over the known concrete types so a build where the designator is not
    /// in any category still works.</para></summary>
    private static Designator_ZoneAdd? FindZoneDesignator(string kind)
    {
        var wanted = kind switch
        {
            "stockpile" => typeof(Zone_Stockpile),
            "growing" => typeof(Zone_Growing),
            _ => null
        };
        if (wanted is null) return null;
        try
        {
            // `ResolvedAllowedDesignators` (`Verse/DesignationCategoryDef.cs:57`), not a `resolvedDesignators` list -
            // 1.7 resolves them lazily and filters by research along the way, so there is no plain list to walk.
            foreach (var def in DefDatabase<DesignationCategoryDef>.AllDefs)
            {
                if (def is null) continue;
                foreach (var candidate in def.ResolvedAllowedDesignators)
                    if (candidate is Designator_ZoneAdd zoneAdd && ZoneAddKind(zoneAdd) == kind) return zoneAdd;
            }
        }
        catch (Exception) { }
        // Fallback: the concrete designator types are public and constructible with no arguments.
        foreach (var type in typeof(Designator_ZoneAdd).AllSubclasses())
        {
            if (type.IsAbstract) continue;
            try
            {
                if (Activator.CreateInstance(type) is Designator_ZoneAdd zoneAdd && ZoneAddKind(zoneAdd) == kind) return zoneAdd;
            }
            catch (Exception) { }
        }
        return null;
    }

    internal static void Apply(string payload)
    {
        requests++;
        LastPlaced.Clear();
        var parts = payload.Split('|');
        // Zone drags share this entry point because they are the same kind of thing - one gesture, many cells - and
        // because the authority has to create the zone object for the replica's overlay to have anything to draw.
        if (parts.Length >= 2 && parts[0] == ZoneKind) { ApplyZone(parts); return; }
        // Eight, not six: fields 5 and 6 are the style and the glower colour, and a one-cell placement is
        // `build|def|stuff|rot|map|style|glower|x,z` (round 122).
        if (parts.Length < 8 || parts[0] != BuildKind) { skippedShape++; Note("形状", payload); return; }
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(parts[1]);
        if (def is null || def.defName is null) { skippedDef++; Note("缺Def", payload); return; }
        var stuff = parts[2].Length == 0 ? null : DefDatabase<ThingDef>.GetNamedSilentFail(parts[2]);
        // A stuffable building with no stuff produces a blueprint every construction work giver throws on: measured as
        // a colony that stopped advancing while the log filled with HasJobOnThing exceptions.
        if (def.MadeFromStuff && stuff is null) { skippedStuff++; Note("缺材料", payload); return; }
        if (!int.TryParse(parts[3], out var rot)) rot = 0;
        // Field 4 is the map the drag was drawn on (`Designator.Map`, which is `Find.CurrentMap` on the sender).
        var map = FindMapByUniqueId(ParsedMapIndex(parts, 4)) ?? Find.CurrentMap;
        if (map is null) { skippedMap++; Note("无当前地图", payload); return; }
        // Point the designator at that map for this replay only. Without it a wall drawn on map 3 while the authority looks
        // at map 0 lands on map 0: same coordinates, valid there, silently wrong.
        using var mapScope = CurrentMapScope.Enter(map);
        var style = string.IsNullOrEmpty(parts[5]) ? null : DefDatabase<ThingStyleDef>.GetNamedSilentFail(parts[5]);
        var glower = SharedActions.DecodeColor(parts[6]);
        if (style is not null || glower is not null) stylesCarried++;
        var designator = BuildDesignator(def, stuff, rot, style, glower);
        if (designator.Map != map)
        {
            wrongMap++;
            Note("重放地图不符", payload);
            return;
        }
        // Read once per request: TutorialMode is the guard inside DesignateSingleCell that returns without touching the
        // map, so a save carrying tutorial state would swallow every replay while looking perfectly healthy.
        noteTutor = noteTutor || TutorSystem.TutorialMode;
        for (var i = 7; i < parts.Length; i++)
        {
            var pair = parts[i].Split(',');
            if (pair.Length != 2) continue;
            if (!int.TryParse(pair[0], out var x) || !int.TryParse(pair[1], out var z)) continue;
            var cell = new IntVec3(x, 0, z);
            // Ask the designator itself, not `GenConstruct`: the designator's own `CanDesignateCell` is what vanilla
            // consults, and a hand-rolled `GenConstruct.CanPlaceBlueprintAt` call is a second opinion that can disagree
            // with the code that will actually run (measured: a build whose own `CanDesignateCell` accepted the cell
            // while our probe's `CanPlaceBlueprintAt` refused it, so the replay skipped a placement vanilla would have
            // made). It is also the check that carries the player-readable refusal reason.
            var placement = designator.CanDesignateCell(cell);
            if (!placement.Accepted)
            {
                refused++;
                if (refusals.Length < 200) refusals.Append(def.defName).Append('@').Append(cell.x).Append(',').Append(cell.z)
                    .Append('=').Append(placement.Reason ?? "无理由").Append("; ");
                continue;
            }
            try { designator.DesignateSingleCell(cell); }
            catch (Exception error) { failed++; Note("异常:" + error.GetType().Name, payload); continue; }
            replayed++;
            // The cell is the evidence: a Blueprint there means the vanilla path really ran, and its absence means the
            // designator returned early for a reason it does not log (tutor guard, zero work, god mode building).
            if (cell.GetThingList(map).Any(thing => thing is Blueprint)) placed++;
            else { missing++; if (misses.Length < 200) misses.Append(def.defName).Append('@').Append(cell.x).Append(',').Append(cell.z).Append(' '); }
            foreach (var candidate in cell.GetThingList(map))
                if (candidate is Blueprint blueprint) { LastPlaced.Add(blueprint); break; }
        }
        if (requests <= 8 || requests % 50 == 0)
            Log.Message($"[TurboTick] 重放建筑蓝图 {requests} 次：放置 {placed} 格，方块仍无蓝图 {missing} 格，"
                + $"被拒 {refused} 格；跳过 形状{skippedShape}/缺Def{skippedDef}/缺材料{skippedStuff}/无地图{skippedMap}，"
                + $"异常 {failed}，教程模式={noteTutor}，当前地图={(map?.Index.ToString(CultureInfo.InvariantCulture) ?? "无")}");
    }

    /// <summary>Points `Find.CurrentMap` at a specific map for the duration of one replay, then puts it back.
    ///
    /// <para><b>Why this is needed at all.</b> `Designator.Map` is a getter over <c>Find.CurrentMap</c>
    /// (`Designator.cs:162502`), and every `DesignateSingleCell` / `DesignateMultiCell` reads it. On a single-map colony
    /// that is always the right map. With two maps loaded - which is what a multi-settlement game is - the authority may
    /// be looking at map 0 while a player on map 3 places a wall, and the replay would then build on map 0: silently, at
    /// the same coordinates, with no error, because the coordinates are valid on both maps.</para>
    ///
    /// <para><b>Why the field and not the property.</b> `Game.CurrentMap`'s setter (`:49881-49902`) calls
    /// <c>Find.MapUI.Notify_SwitchedMap()</c> and <c>AmbientSoundManager.Notify_SwitchedMap()</c> on every change. Those
    /// are user-interface and audio notifications: running them once per replayed command would tear the local player's
    /// camera and soundscape around while the authority replays somebody else's gesture. The backing field
    /// <c>public sbyte currentMapIndex</c> (`:49769`) is public and is what the getter actually reads, so writing it and
    /// restoring it changes the answer to `Designator.Map` without announcing anything.</para>
    ///
    /// <para><b>Scope.</b> One command. The restore runs in a <c>finally</c>, so an exception inside a replay cannot leave
    /// the authority pointed at the wrong map - which would corrupt every later replay and every read that goes through
    /// <c>Find.CurrentMap</c>.</para>
    ///
    /// <para>If the target map is already current this is a no-op with a `true` guard flag, so the common single-map case
    /// pays one comparison.</para></summary>
    internal readonly struct CurrentMapScope : IDisposable
    {
        private readonly sbyte _previous;
        private readonly bool _changed;

        private CurrentMapScope(sbyte previous, bool changed)
        {
            _previous = previous;
            _changed = changed;
        }

        internal static CurrentMapScope Enter(Map? target)
        {
            var game = Current.Game;
            if (game is null || target is null) return default;
            // `Game.maps` is private (`:49781`); `Find.Maps` is the public `Current.Game?.Maps` (`:207624`) and is the
            // same list, so the index it returns is the index `currentMapIndex` expects.
            var wanted = (sbyte)Find.Maps.IndexOf(target);
            if (wanted < 0 || wanted == game.currentMapIndex) return default;
            var previous = game.currentMapIndex;
            game.currentMapIndex = wanted;
            return new CurrentMapScope(previous, true);
        }

        public void Dispose()
        {
            if (!_changed) return;
            var game = Current.Game;
            if (game is not null) game.currentMapIndex = _previous;
        }
    }

    /// <summary>Replays a whole cancel drag by running vanilla's own designator over the same cells.
    ///
    /// <para>Deliberately not re-implemented: <c>Designator_Cancel.DesignateSingleCell</c> is the single place that
    /// knows a cell can hold several cancelable designations, that a mine vein cancels contiguously
    /// (<c>Designator_MineVein.RemoveContiguousDesignations</c>), that a smoothable wall has its own mark, and that a
    /// <c>Blueprint</c>/<c>Frame</c> is <c>Destroy(Cancel)</c> while anything else is <c>RemoveAllDesignationsOn</c>.
    /// Copying that logic into the apply path is how the two sides drift apart on the next game version.</para>
    ///
    /// <para>The designator is constructed rather than looked up: it has a parameterless constructor and holds no
    /// state that the drag depends on (<c>Designator_Cancel</c> only owns a static <c>seenThings</c> used for
    /// highlighting). Calling it inside <c>Applying</c> means the per-thing capture patches leave it alone, so each
    /// cell mutates exactly once.</para></summary>
    internal static void ApplyCancel(byte[] payload)
    {
        if (!CommandCodec.TryDecodeCancelDrag(payload, out var mapIndex, out var cells))
        {
            skippedShape++;
            Note("取消载荷", "");
            return;
        }
        var map = FindMapByUniqueId(mapIndex);
        if (map is null) { skippedMap++; return; }
        // `Designator.Map` is `Find.CurrentMap` (`:162502`), so a replay pointed at another map used to be refused
        // outright. That was correct but incomplete: the gesture was then lost for every player not standing on the map
        // they were editing, which is the normal case in a multi-settlement colony. The scope below points
        // `Find.CurrentMap` at the target for exactly this replay and restores it afterwards, through the backing field so
        // the UI's map-switched notifications do not fire once per command.
        using var mapScope = CurrentMapScope.Enter(map);
        if (Find.CurrentMap != map)
        {
            // Still possible when the target map is not in `Find.Maps` at the index the field would need, or when there is
            // no game. Refusing and counting is the honest answer here: the alternative is a cancel that removes marks from
            // a map the player never pointed at.
            wrongMap++;
            if (wrongMap <= 8)
                Log.Warning($"[TurboTick] 重放取消跳过：当前地图 {Find.CurrentMap?.Index.ToString(CultureInfo.InvariantCulture) ?? "无"}，"
                    + $"命令指向 {mapIndex}");
            return;
        }
        var designator = new Designator_Cancel();
        var visited = 0;
        var destroyed = 0;
        var marked = 0;
        foreach (var (x, z) in cells)
        {
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) continue;
            visited++;
            var before = CountCancelable(cell, map);
            var blueprints = CountBlueprints(cell, map);
            try { designator.DesignateSingleCell(cell); }
            catch (Exception error)
            {
                failed++;
                if (failures.Length < 200)
                    failures.Append(cell.x).Append(',').Append(cell.z).Append('=').Append(error.GetType().Name).Append("; ");
                continue;
            }
            destroyed += Math.Max(0, blueprints - CountBlueprints(cell, map));
            marked += Math.Max(0, before - CountCancelable(cell, map));
        }
        cancelledCells += visited;
        cancelledBlueprints += destroyed;
        cancelledMarks += marked;
        cancelRequests++;
        if (cancelRequests <= 8 || cancelRequests % 25 == 0)
            Log.Message($"[TurboTick] 重放取消 {cancelRequests} 次：{visited} 格，拆蓝图 {destroyed} 个，"
                + $"清标记 {marked} 条，累计 {cancelledCells} 格；异常 {failed}"
                + (wrongMap > 0 ? $"，地图不符 {wrongMap} 次" : ""));
    }

    /// <summary>How many things on this cell the cancel designator would destroy. Read before and after so the log
    /// answers "did the replay do anything" instead of only "it did not throw".</summary>
    private static int CountBlueprints(IntVec3 cell, Map map)
    {
        var list = cell.GetThingList(map);
        var count = 0;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is Blueprint or Frame) count++;
        return count;
    }

    /// <summary>Everything under the cell that the cancel designator is allowed to remove.</summary>
    private static int CountCancelable(IntVec3 cell, Map map)
    {
        var all = map.designationManager.AllDesignationsAt(cell);
        var count = 0;
        foreach (var designation in all)
            if (designation.def?.designateCancelable == true) count++;
        return count;
    }

    /// <summary>One line per skip, capped so a pathological stream cannot fill the log.</summary>
    private static void Note(string reason, string payload)
    {
        if (notes++ >= 8) return;
        Log.Warning($"[TurboTick] 重放建筑蓝图跳过（{reason}）：{(payload.Length <= 120 ? payload : payload[..120])}");
    }

    /// <summary>Just the half of <see cref="Summary"/> that answers "did the player's gesture turn into commands?".
    ///
    /// <para>Separate from <see cref="Summary"/> because the two are read at different moments. <c>Summary</c> describes
    /// replay - what arrived and what it did - and is written once per frame by the authority. This one is read
    /// immediately after a capture, on the peer that captured, where replay has not happened yet: it is the only way to
    /// tell "the drag produced nothing" from "it produced a command that the authority then dropped".</para>
    ///
    /// <para>Three numbers, because a single total hides the interesting failure: <c>gestures</c> counts patch entries,
    /// <c>commands</c> counts chunks put on the wire (one drag over 6000 cells is more than one), and
    /// <c>noPayload</c> counts gestures that were captured but encoded to nothing - the <c>Designator_Build</c>-only
    /// case in <see cref="EncodeOne"/> that silently returns null for a null <c>PlacingDef</c>.</para>
    /// </summary>
    internal static string CaptureCounters() =>
        $"gestures={capturedGestures} commands={capturedCommands}"
        + (capturedEmpty > 0 ? $" noPayload={capturedEmpty}" : "")
        + (captureSuppressed > 0 ? $" suppressed={captureSuppressed}" : "")
        // 单格补丁的入口计数。`gestures` 是「捕获函数被调用」的次数，而这里数的是「补丁本身被执行」的次数：
        // 前者在闸门拒绝时也会加一，后者不会。两者之差就是「补丁绑到了但闸门挡下」，同为 0 就是「补丁没绑到」。
        + $" 入口={EntryCalls}({LastEntryType}/{LastEntryGate})"
        // Vanilla's own verdict on the last build click. `拒绝:…` with a reason means the click never had a chance and
        // the capture is irrelevant; `可放置` with `commands=0` means the capture path is the one that dropped it.
        + (LastPlacement.Length > 0 ? $" 落点={LastPlacement}" : "");

    /// <summary>Vanilla's verdict on the most recent build click, taken by re-running the same
    /// <c>CanDesignateCell</c> the click path itself runs (<c>Verse/DesignatorManager.cs:98</c>).</summary>
    internal static string LastPlacement = "";

    /// <summary>How many times the single-cell prefix ran at all, and what it saw. Counted before the gate so that
    /// "the patch never bound" and "the gate refused" are different numbers.</summary>
    internal static int EntryCalls;
    internal static string LastEntryType = "-";
    internal static string LastEntryGate = "-";

    /// <summary>Called by the capture prefix for every gesture it saw, before deciding anything. Kept as a separate
    /// method so the counters advance even when the encode returns nothing: a gesture that produces no command is the
    /// failure this probe exists for, and it would leave no trace if counting happened after the encode.</summary>
    internal static void NoteCapture(List<GameCommand> chunks, bool suppressed)
    {
        capturedGestures++;
        if (suppressed) { captureSuppressed++; return; }
        capturedCommands += chunks.Count;
        if (chunks.Count == 0) capturedEmpty++;
    }

    private static int capturedGestures;
    private static int capturedCommands;
    private static int capturedEmpty;
    private static int captureSuppressed;

    /// <summary>Read into the authority's frame log, so "the command arrived and did nothing" is visible without
    /// attaching a debugger to a headless container.</summary>
    internal static string Summary() =>
        $"请求{requests}/重放{replayed}/有蓝图{placed}/方块无{missing}/被拒{refused}"
        + (skippedShape + skippedDef + skippedStuff + skippedMap + failed > 0
            ? $"/跳过{skippedShape + skippedDef + skippedStuff + skippedMap}/异常{failed}" : "")
        + (noteTutor ? "（教程模式）" : "")
        // Printed for every replay kind, not only cancel: since the map index started travelling with the payload, a
        // mismatch means the scope could not move `Find.CurrentMap` and the gesture was refused. Behind the cancel
        // branch it would have been invisible on a build or a zone drag, which are the two paths that carry a map now.
        + (wrongMap > 0 ? $"/错图{wrongMap}" : "")
        + (cancelRequests > 0
            ? $"/取消{cancelRequests}次:{cancelledCells}格拆{cancelledBlueprints}/清{cancelledMarks}" : "")
        + (refusals.Length > 0 ? " 拒绝原因 " + refusals : "")
        + (misses.Length > 0 ? " 无蓝图格 " + misses : "")
        // Zone drags are counted apart from blueprints because the two failures look unrelated from the outside: a
        // stockpile whose cells never left the client reported `本端区域=0` on the authority and drew nothing on
        // either side, with no exception and no warning.
        + (zonesReplayed + zoneReplayFailures + skippedZoneKind > 0
            ? $"/区域{zonesReplayed}次/失败{zoneReplayFailures}/不支持{skippedZoneKind}" : "")
        + (failures.Length > 0 ? " 取消异常 " + failures : "");

    private static int requests;
    internal static int replayed;
    internal static int placed;
    private static int missing;
    private static int refused;
    private static int failed;
    private static int skippedShape;
    private static int skippedDef;
    private static int skippedStuff;
    private static int skippedMap;
    private static int notes;
    private static bool noteTutor;
    // Cancel-drag counters. Kept apart from the build counters because the two answer different questions and the
    // frame summary is read as one line per peer.
    private static int cancelRequests;
    private static int cancelledCells;
    private static int cancelledBlueprints;
    private static int cancelledMarks;
    private static int wrongMap;
    private static readonly System.Text.StringBuilder failures = new();
    private static readonly System.Text.StringBuilder refusals = new();
    private static readonly System.Text.StringBuilder misses = new();

    private static int PlacingRot(Designator_Build build)
    {
        try
        {
            placingRotField ??= AccessTools.Field(typeof(Designator_Place), "placingRot");
            return placingRotField?.GetValue(build) is Rot4 rot ? rot.AsInt : 0;
        }
        catch (Exception) { return 0; }
    }

    private static void SetPlacingRot(Designator_Build build, Rot4 rot)
    {
        try
        {
            placingRotField ??= AccessTools.Field(typeof(Designator_Place), "placingRot");
            placingRotField?.SetValue(build, rot);
        }
        catch (Exception) { }
    }

    /// <summary>Whether a def's things carry <c>CompStyleable</c>, i.e. whether a style can be written at all.
    ///
    /// <para>Read from the def's comp properties rather than from a spawned instance: this runs before the blueprint
    /// exists, and a blueprint's comps come from the buildable (<c>blueprintDef</c> is generated with them). A def that
    /// cannot be styled gets no style assigned, which is exactly what vanilla's own `SetStyleDef` would conclude - only
    /// without the warning it would log.</para></summary>
    private static bool Styleable(ThingDef def)
    {
        try
        {
            var props = def.GetCompProperties<CompProperties_Styleable>();
            if (props is not null) return true;
            // A blueprint's own def carries the comps; ask it too, because the write lands on the blueprint.
            return def.blueprintDef?.GetCompProperties<CompProperties_Styleable>() is not null;
        }
        catch (Exception) { return false; }
    }

    private static void SetStuff(Designator_Build build, ThingDef stuff)
    {
        try
        {
            stuffField ??= AccessTools.Field(typeof(Designator_Build), "stuffDef");
            stuffField?.SetValue(build, stuff);
        }
        catch (Exception) { }
    }

    /// <summary>A designator that will really place a blueprint, for replaying the host's own gesture.
    ///
    /// <para>Built the way ColonyTogether builds its <c>RemoteBuildDesignator</c>
    /// (<c>docs/mp-colonytogether/_decomp/ColonyTogether.Prototype.decompiled.cs:220679</c>): the constructor takes the
    /// buildable, then <c>SetStuffDef</c> and <c>placingRot</c> are written directly. Those are the two fields
    /// <c>Designator_Build.DesignateSingleCell</c> reads; going through the public setters instead would run
    /// <c>ProcessInput</c>'s stuff-selection float menu, which is UI the host game thread must not open.</para>
    ///
    /// <para><c>isOrder</c> is set because this is not the local player placing a building: it is an order being
    /// applied. Vanilla uses that flag for tutor suppression and for the "not enough material" hint, and a replay that
    /// leaves it false can pop a message on a peer that never asked for anything.</para></summary>
    /// <param name="style">The style the sender's designator held, or null. Assigned so that
    /// <c>Designator_Build.PlaceThing</c> performs its own two writes (`:622504-622509`) instead of this method doing
    /// them on the object afterwards - that is what also covers the branch that spawns a building directly and never
    /// makes a blueprint (`:622479-622497`).</param>
    /// <param name="glower">The sender's glower colour override, or null for "the player did not recolour it".</param>
    internal static Designator_Build BuildDesignator(ThingDef def, ThingDef? stuff, int rot,
        ThingStyleDef? style = null, ColorInt? glower = null)
    {
        var designator = new Designator_Build(def);
        if (stuff is not null) SetStuff(designator, stuff);
        // Both are plain public fields on `Designator_Build` (`:621995`, `:621999`), so no reflection is needed.
        //
        // The style is only assigned when the thing being built can actually hold one: `Thing.SetStyleDef`
        // (`:559509-559523`) needs `CompStyleable` and otherwise logs `Tried setting ThingStyleDef to a thing without
        // CompStyleable` and does nothing. A lamp is such a thing, so assigning blindly would put a warning in the log
        // for every ordinary placement and change nothing on screen.
        if (style is not null && Styleable(def)) designator.styleDef = style;
        if (glower is { } colour) designator.glowerColorOverride = colour;
        SetPlacingRot(designator, new Rot4(rot));
        designator.isOrder = true;
        return designator;
    }

    private static System.Reflection.FieldInfo? placingRotField;

    private static System.Reflection.FieldInfo? stuffField;
}

/// <summary>Captures a whole designator action at the level the drag actually commits on.
///
/// <para>Same two hazards as <see cref="DesignatorSingleCellPatch"/>, and both are handled the same way: a patch on
/// the base virtual does not run for an override (`Designator_Plan_Add`, `Designator_Plan_CopySelection`,
/// `Designator_ZoneAdd`, `Designator_ZoneAdd_Fishing` all override this one), and the value parameter is taken through
/// <c>__args</c> so a differently-named parameter cannot break the binding.</para>
///
/// <para>Discovered the same way as the single-cell side, and for the same measured reason: the four-entry override
/// list here was the other half of "nothing was ever captured". A drag is the gesture a player actually makes - a
/// hand-placed wall is one cell, a wall *ring* is a drag - so an incomplete list on this side means the common case is
/// the one that silently does nothing.</para>
/// </summary>
[HarmonyPatch]
internal static class DesignatorMultiCellPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in DesignatorSingleCellPatch.DesignatorSubclasses())
        {
            MethodInfo? method;
            try
            {
                method = AccessTools.DeclaredMethod(type, nameof(Designator.DesignateMultiCell),
                    [typeof(IEnumerable<IntVec3>)]);
            }
            catch (Exception) { continue; }
            if (method is null || method.IsAbstract) continue;
            yield return method;
        }
    }

    private static bool Prefix(Designator __instance, object[] __args, out bool __state)
    {
        __state = false;
        if (!SharedActions.PlayerActing) return true;
        if (__args is not { Length: > 0 } || __args[0] is not IEnumerable<IntVec3> cells) return true;
        var list = cells as IReadOnlyList<IntVec3> ?? cells.ToList();
        if (list.Count == 0) return true;
        if (__instance is Designator_Cancel) { DesignatorSync.SuppressingPerThingCancel = true; __state = true; }
        return !SharedActions.CaptureDesignator(__instance, list);
    }

    private static void Postfix(bool __state) { if (__state) DesignatorSync.SuppressingPerThingCancel = false; }
}

/// <summary>One patch per concrete subclass, because a Harmony patch on a base virtual method does not run for an
/// override.
///
/// <para>This is the whole reason single-cell placement was never captured: <c>Designator.DesignateSingleCell</c> is
/// `virtual` and <c>Designator_Build</c> overrides it (<c>RimWorld/Designator_Build.cs:455</c>), so patching the base
/// bound nothing on the class the player actually uses. Two separate failures came from that one hole:</para>
///
/// <para>① The vanilla build path ran on the client and allocated a thing id from this peer's own counter; the frame's
/// creation then arrived under the host's id, which is the divergence the class comment above describes.</para>
///
/// <para>② Placing a door onto an existing blueprint threw <c>NullReferenceException</c> at
/// <c>Designator_Build.DesignateSingleCell</c>, because <c>BlueprintCapturePatch</c> was cancelling
/// <c>GenConstruct.PlaceBlueprintForBuild</c> and vanilla dereferences the result three lines later. With the player's
/// path now skipped entirely, that prefix no longer runs for a drag - it only records direct callers and lets them
/// run.</para>
///
/// <para>The prefix takes <c>__args</c> instead of a named parameter. Harmony binds a prefix by parameter name for
/// value types, and the 1.7 subclasses do not agree on one: `Designator_Deconstruct` calls it <c>loc</c>
/// (`RimWorld/Designator_Deconstruct.cs:43`), `Designator_Eyedropper` calls it <c>cell</c>, and the rest use
/// <c>c</c>. Because one class disagreed, the whole patch failed to install with
/// <c>Patching exception in method virtual void RimWorld.Designator_Deconstruct::DesignateSingleCell(Verse.IntVec3 loc)</c>
/// - and `HeadlessRuntime.Start` refuses to launch a dedicated simulation unless every critical patch installed, so the
/// server came up, printed that one warning, and exited 16 seconds later with code 1. <c>__args</c> is unnamed by
/// construction, so no subclass can break it again.</para>
///
/// <para><b>Now discovered rather than listed.</b> A hand-written <c>PatchedTypes</c> array was the reason nothing was
/// ever captured in the field: the list held 19 types, the game loads several dozen `Designator` subclasses, and a
/// subclass added by a DLC or another mod is missed by construction. The absence was invisible - Harmony installs the
/// patches it was given, the install audit reports them all green, and the missing ones simply never fire. Measured:
/// two sides logged zero `重放建筑蓝图` lines across a whole session, i.e. the capture never ran once, and no
/// diagnostic could have said so because there was no code to say it from.</para>
///
/// <para>ColonyTogether does the same thing (<c>NativeDesignatorRelay.TargetMethods</c>,
/// <c>docs/mp-colonytogether/_decomp/ColonyTogether.Prototype.decompiled.cs:262319</c>): walk
/// <c>GenTypes.AllSubclasses(typeof(Designator))</c> and take every *declared* <c>DesignateSingleCell</c> /
/// <c>DesignateMultiCell</c>. Two details are copied deliberately - <c>DeclaredOnly</c> (an inherited method must be
/// patched once, at its declaring type, or the same body gets two prefixes) and skipping abstract declaring types
/// (Harmony cannot patch a method with no body).</para>
/// </summary>
[HarmonyPatch]
internal static class DesignatorSingleCellPatch
{
    /// <summary>Every declared <c>DesignateSingleCell</c> in the game and in every loaded mod's assembly.
    ///
    /// <para>Sorted so the install order is stable across runs: Harmony's patch ids derive from the target method, and
    /// an unordered list makes two otherwise identical launches produce different patch enumerations, which is
    /// unhelpful when comparing an audit log between two peers.</para></summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in DesignatorSubclasses())
        {
            MethodInfo? method;
            try { method = AccessTools.DeclaredMethod(type, nameof(Designator.DesignateSingleCell), [typeof(IntVec3)]); }
            catch (Exception) { continue; }
            if (method is null || method.IsAbstract) continue;
            yield return method;
        }
    }

    /// <summary>Every concrete <see cref="Designator"/> subclass the game knows about, plus the base itself.
    ///
    /// <para><c>GenTypes.AllSubclasses</c> is public in 1.7 (<c>acs17_all.txt:2026</c>) and already filters to loaded
    /// assemblies, so a mod's own designator is covered without naming it. The base is prepended because it declares
    /// the default <c>DesignateSingleCell</c> body that most simple designators inherit
    /// (<c>Verse/Designator.cs:288</c>).</para>
    ///
    /// <para>Wrapped in a try/catch that returns the base alone: a partially loaded mod list can throw from type
    /// enumeration, and losing the whole designator path over one broken assembly is worse than losing its
    /// subclasses.</para></summary>
    internal static List<Type> DesignatorSubclasses()
    {
        List<Type> types;
        try
        {
            types = GenTypes.AllSubclasses(typeof(Designator));
            types.Insert(0, typeof(Designator));
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 枚举 Designator 子类失败，只补丁基类：" + error.GetType().Name + ": " + error.Message);
            return [typeof(Designator)];
        }
        types.RemoveAll(type => !DesignatorTargets.Acceptable(type));
        types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return types;
    }

    private static bool Prefix(Designator __instance, object[] __args, out bool __state)
    {
        __state = false;
        // Counted before the gate, deliberately. `PlayerActing` has four conditions and the click is refused by the
        // first one that is false; a counter placed after it cannot tell "the gate refused" from "the patch never ran",
        // and those are the two causes behind "I cannot place furniture" that need opposite fixes.
        DesignatorSync.EntryCalls++;
        DesignatorSync.LastEntryType = __instance?.GetType().Name ?? "null";
        DesignatorSync.LastEntryGate = SharedActions.PlayerActing ? "开" : SharedActions.GateDescription();
        // The other half of the same question: a click that vanilla itself refuses never reaches the capture at all,
        // because `DesignatorManager.Update` checks `CanDesignateCell` first (`Verse/DesignatorManager.cs:98`). So the
        // two numbers that separate the causes are "did the prefix run" and "did vanilla accept the cell", and only
        // the first one existed before this.
        if (__instance is Designator_Build build && __args is { Length: > 0 } && __args[0] is IntVec3 probe)
        {
            try
            {
                var report = build.CanDesignateCell(probe);
                DesignatorSync.LastPlacement = report.Accepted ? "可放置" : "拒绝:" + (report.Reason ?? "无理由");
            }
            catch (Exception error) { DesignatorSync.LastPlacement = "异常:" + error.GetType().Name; }
        }
        if (!SharedActions.PlayerActing)
        {
            // Say why. "The wall was placed on my screen and never reached the authority" has two causes that look
            // identical from the player's side - the patch never bound, or it bound and the gate refused - and only the
            // first one was ever visible. `NoteGateBlocked` is rate limited to one line per five seconds and names
            // every condition, so this cannot flood while a drag is held.
            if (__instance is Designator_Build) SharedActions.NoteGateBlocked("build-single");
            return true;
        }
        if (__args is not { Length: > 0 } || __args[0] is not IntVec3 cell) return true;
        // Already inside a cancel drag's own commit loop: the drag's command carries every cell of the rectangle, so
        // capturing this one too would publish one single-cell command per cell on top of it.
        if (DesignatorSync.SuppressingPerThingCancel) return true;
        if (__instance is Designator_Cancel) { DesignatorSync.SuppressingPerThingCancel = true; __state = true; }
        return !SharedActions.CaptureDesignator(__instance, [cell]);
    }

    private static void Postfix(bool __state) { if (__state) DesignatorSync.SuppressingPerThingCancel = false; }

    /// <summary>How many methods the two designator patches bound, and over which types. Logged once at startup.
    ///
    /// <para>A single number would be enough to notice the old defect (it would have read `1`), but the type list is
    /// what makes the next one diagnosable: "the designator I care about is not in this list" is a question an operator
    /// can answer, while "the count is 27 instead of 26" is not.</para></summary>
    internal static string Describe()
    {
        var types = DesignatorSubclasses();
        var single = 0;
        var multi = 0;
        foreach (var type in types)
        {
            try
            {
                if (AccessTools.DeclaredMethod(type, nameof(Designator.DesignateSingleCell),
                        [typeof(IntVec3)]) is { IsAbstract: false }) single++;
            }
            catch (Exception) { }
            try
            {
                if (AccessTools.DeclaredMethod(type, nameof(Designator.DesignateMultiCell),
                        [typeof(IEnumerable<IntVec3>)]) is { IsAbstract: false }) multi++;
            }
            catch (Exception) { }
        }
        // The one type the whole construction feature depends on, named explicitly: if this is absent the count above
        // is technically fine and the feature is still dead.
        return DesignatorTargets.Describe(types.Count, single, multi, types.Contains(typeof(Designator_Build)));
    }
}

