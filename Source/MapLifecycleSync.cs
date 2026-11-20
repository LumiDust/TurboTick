using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TurboTick.Networking;
using HarmonyLib;
using Verse;
using RimWorld;
using RimWorld.Planet;

namespace TurboTick;

/// <summary>Which maps exist, so a replica that loads a save with one map can still show the second one the authority
/// generated afterwards.
///
/// <para><b>Why this is a hole and not a nicety.</b> Every place that carries a map identity - the designator replay,
/// the plan channel, the zone channel, the terrain/roof/temperature channels, the shared-state hash - keys on
/// `Map.uniqueID` (`:76213`), which is fine and is what those channels were rewritten to do over rounds 66-68. But
/// <c>uniqueID</c> is assigned once from <c>Find.UniqueIDsManager.GetNextMapID()</c> (`:85222`) when a map is
/// <i>created</i>, and nothing carries the creation itself. A replica loads one map, the authority generates a second
/// (a gravship landing site, a pocket map from an ancient complex, a second colony), and from then on every frame the
/// replica receives names a map id it has never heard of. Every consumer skips it silently - `FindMapByUniqueId`
/// returns null and the command is dropped - so the second colony is not "out of sync", it is <b>absent</b>, while the
/// map list on the two screens disagrees with no error anywhere.</para>
///
/// <para><b>Why the whole list and not a create/destroy event.</b> A map can be created and destroyed between two
/// frames of a slow peer (a pocket map that is entered and left inside one long tick), and an event pair that arrives
/// out of order leaves a permanent phantom. Recomputation cannot miss a creation any more than `FactionSync` can miss a
/// goodwill write, and the population is one to five entries - the comparison is free.</para>
///
/// <para><b>What travels.</b> Only `uniqueID`, the size and which map is current. The terrain is already carried
/// per-cell by `TerrainSync`, the things by the object sweep, and the fog by `FogSync`; a generated map is a normal map
/// and those channels find it as soon as it exists here. What has to be rebuilt is the map object itself - its
/// `info.Size`, its `mapPawns`/`listerThings`/`zoneManager`/`terrainGrid` containers - because those are allocated in
/// `Map.ConstructComponents` and nothing else creates them.</para>
///
/// <para><b>Why the id counter is pushed, not just the map.</b> The authority consumed an id from `nextMapID` to make
/// the new map. If the replica's counter is left alone, the next map it creates locally (or the next `uniqueID` any
/// other code reads) collides with one that already exists. Same rule as `SetNextThingId`: the counter that allocates
/// an identity is part of the identity.</para></summary>
internal static class MapLifecycleSync
{
    /// <summary>`uniqueID` of every map last sent, so only a change travels. A `HashSet` rather than a dictionary -
    /// the only thing that can change about an existing map is nothing, since size and id are fixed at creation
    /// (`Map.info.Size` is written once in `GenerateMap` `:85229`).</summary>
    private static readonly HashSet<int> sent = [];

    /// <summary>Maps the authority reported that this peer could not build, with the reason. Printed rather than logged
    /// per frame: a map that fails to construct would otherwise be a silent absence, which is the exact failure this
    /// file exists to remove.</summary>
    private static readonly Dictionary<int, string> failures = [];

    private static FieldInfo? nextMapIdField;

    internal static long MapsCreated { get; private set; }
    internal static long MapsRemoved { get; private set; }
    internal static long CreateFailures { get; private set; }
    internal static int LastSent { get; private set; }

    /// <summary>Every map whose `uniqueID` has not been sent yet, plus the current map when it changed.
    ///
    /// <para>The size is read from `map.Size` rather than remembered because a map's size can be changed by a mod
    /// after creation (`Map.info.Size` is a plain field) and sending a stale size would build the replica's copy with
    /// the wrong grid dimensions - the one mistake that makes every cell-index in every other channel wrong at once.
    /// </para></summary>
    internal static List<ReplicaMap>? Publish()
    {
        try
        {
            var maps = Find.Maps;
            if (maps is null || maps.Count == 0) return null;
            List<ReplicaMap>? result = null;
            var alive = new HashSet<int>();
            foreach (var map in maps)
            {
                if (map is null) continue;
                alive.Add(map.uniqueID);
                // Remember which world object the authority named, because the replica cannot recover it later:
                // `MapParent.Map` is `FindMap(this)` and `FindMap` matches on `info.parent`, which is exactly the field
                // that is null until the binding succeeds.
                // A parent owns at most one map (`Game.FindMap` returns the first match), so a hint is only recorded
                // when the authority's own parent is not already claimed by a different live map. The bind path repeats
                // the same guard, because the hint can also arrive from a probe.
                // **The hint tracks the authority's current answer, including "no parent".** Keeping it as a one-way
                // memory is a defect, measured: the probe cleared an owning map's parent so a second map could be bound,
                // and `BindParents` immediately bound the first map back from the stale hint - the probe reported
                // `本次绑 1 个` while its target ended with no parent at all. A map whose parent was destroyed or
                // abandoned must lose its hint the same way.
                if (map.Parent is not null && map.Parent.ID > 0 && !parentClaimed(map.Parent.ID, map.uniqueID))
                    parentOf[map.uniqueID] = map.Parent.ID;
                else if (map.Parent is null)
                    parentOf.Remove(map.uniqueID);
                if (!sent.Add(map.uniqueID)) continue;
                (result ??= []).Add(new ReplicaMap(map.uniqueID, map.Size.x, map.Size.z,
                    map.Parent?.ID ?? 0, map.generatorDef?.defName ?? string.Empty));
            }
            // A map that disappeared on the authority is removed here too, but only if this peer actually had it -
            // otherwise the tombstone is a phantom and every later frame would repeat it.
            foreach (var stale in sent.Where(id => !alive.Contains(id)).ToList())
            {
                sent.Remove(stale);
                (result ??= []).Add(new ReplicaMap(stale, 0, 0, 0));
            }
            LastSent = result?.Count ?? 0;
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] map publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Builds a map the authority has and this peer does not, or destroys one it no longer has.
    ///
    /// <para><b>Why `MapGenerator.GenerateMap` is not used.</b> It runs every gen step - terrain, plants, geysers,
    /// ancient dangers, faction bases - and all of that already arrives through the per-cell and per-object channels.
    /// Running the generator here would produce a second, different map and then the channels would fight it: the
    /// replica would hold terrain the authority never had until the next full terrain frame overwrote it, and any
    /// object the generator spawned would exist only on this peer. `Map.ConstructComponents` is the part that has to
    /// happen locally because it allocates the containers every other channel writes into.</para>
    ///
    /// <para><b>Width and height of 0 mean a tombstone.</b> `GenerateMap` never produces a zero-sized map and
    /// `Map.Size` cannot be zero (the grid index arithmetic would divide by zero), so the encoding is unambiguous.
    /// </para></summary>
    internal static void Apply(ReplicaMap state)
    {
        try
        {
            if (state.Width <= 0 || state.Height <= 0)
            {
                Destroy(state.Id);
                return;
            }
            if (FindMapByUniqueId(state.Id) is not null) return;
            Create(state);
            // The world object that owns this map may already have arrived on an earlier frame, in which case nothing
            // else will ever call `BindParents` for it - the object channel only fires on creation.
            BindParents();
        }
        catch (Exception error)
        {
            CreateFailures++;
            failures[state.Id] = error.GetType().Name + ": " + error.Message;
            if (CreateFailures <= 6)
                Log.Warning($"[TurboTick] map create failed ({state.Id}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Allocates one map and its component containers, the way `MapGenerator.GenerateMap` does
    /// (`:85218-85232`) minus every gen step.
    ///
    /// <para>The order matters: `uniqueID` and `info.Size` are written before `ConstructComponents`, because the
    /// component constructors size their grids from `Size` and index them with `cellIndices`, which is itself built
    /// from `Size`. A map constructed with a default 1x1 size has every subsequent cell write land out of bounds.</para>
    ///
    /// <para>The parent is deliberately left null. `Map.info.parent` is a world object, and a pocket map's parent is
    /// created together with the map (`PocketMapUtility.GeneratePocketMap` `:86239-86248`); reconstructing that
    /// relationship here would duplicate a world object that `WorldObjectSync` already carries. A replica map with no
    /// parent reads as an ordinary second colony, which is the case that matters and the case that is correct.</para>
    ///</summary>
    private static void Create(ReplicaMap state)
    {
        var map = new Map
        {
            uniqueID = state.Id,
            generationTick = Find.TickManager?.TicksGame ?? 0,
        };
        map.events = new MapEvents(map);
        map.info.Size = new IntVec3(state.Width, 1, state.Height);
        // `GenerateMap` writes this at `:85238` and four readers dereference it without a null check - fog reveal
        // (`:68785`), the under-grid default (`:72954`), item decay (`:91489`) and pocket temperature (`:93120`). A map
        // without it throws the first time anything is revealed on it.
        if (state.Generator.Length > 0)
            map.generatorDef = DefDatabase<MapGeneratorDef>.GetNamedSilentFail(state.Generator);
        // A generator def this build does not have would leave the field null and reintroduce the same crash on the
        // first reveal, so fall back to the player base generator rather than leaving it unset.
        if (map.generatorDef is null) map.generatorDef = MapGeneratorDefOf.Base_Player;
        // `Current.Game.Maps` is the list `Find.Maps` returns (`:207624`), and `Map.ConstructComponents` is what
        // allocates `mapPawns`, `listerThings`, `zoneManager`, `terrainGrid`, `roofGrid`, `fogGrid`, `designationManager`
        // and the rest. Without it every one of those is null and the first channel to touch the map throws.
        map.ConstructComponents();
        Current.Game.Maps.Add(map);
        AdvanceNextMapId(state.Id);
        MapsCreated++;
    }

    /// <summary>Binds a replica map to the `MapParent` that owns it, once that parent exists here.
    ///
    /// <para><b>The defect this closes.</b> `Create` deliberately leaves `info.parent` null, and that was justified as
    /// 「a replica map with no parent reads as an ordinary second colony」. It does not. `Map.IsPlayerHome`
    /// (`:76546-76556`) is:</para>
    ///
    /// <code>if (!wasSpawnedViaGravShipLanding &amp;&amp; (info?.parent == null || info.parent.Faction != Faction.OfPlayer
    ///     || !info.parent.def.canBePlayerHome)) return GravshipUtility.PlayerHasGravEngine(this);
    /// return true;</code>
    ///
    /// <para>With a null parent the first clause is true, so a second colony of the player's own comes back
    /// <c>false</c> - and `IsPlayerHome` has <b>132 readers</b>: threat points (`:76562`), danger rating (`:52810`),
    /// rope-managed animal checks (`:62344`), roamer ticks (`:253421`), item decay, plant spawning. Every multi-settlement
    /// colony reads its own second map as foreign land. On a single-map colony the bug is invisible, which is why it
    /// took a multi-settlement question to surface it.</para>
    ///
    /// <para><b>Why the match cannot use `parent.Map`.</b> The obvious loop - "for each `MapParent`, if `parent.Map`
    /// is this map then bind" - <b>can never fire</b>. `MapParent.Map` is `Current.Game.FindMap(this)` (`:793588`), and
    /// `FindMap` matches on `maps[i].info.parent == mapParent` (`:50125`). With the parent null, `parent.Map` is null, so
    /// the guard rejects every candidate and the binding is dead code. That is not a hypothesis: the first version did
    /// exactly this and the probe reported `本次绑 0，累计 0；无父 1 张` with two maps present. The link has to come from
    /// the field that actually travels - `ReplicaMap.Parent`, which the authority fills from `map.Parent?.ID`
    /// (`Publish` below) - and be matched against `WorldObject.ID`.</para>
    ///
    /// <para><b>Why this runs after the world object exists, not inside `Create`.</b> The two channels are independent:
    /// `MapLifecycleSync` carries maps and `WorldObjectLifecycleSync` carries world objects, and a frame can deliver
    /// either one first. Two attempts - one at creation, one whenever a world object arrives - means whichever order
    /// the frame uses, the binding happens. It is idempotent and cheap: the scan is over one to five maps.</para>
    ///
    /// <para><b>What is not bound.</b> `wasSpawnedViaGravShipLanding` is left alone: it is a local flag set by
    /// `GravshipUtility` on the peer that performed the landing, and forcing it true on a replica would claim a landing
    /// that never happened here. The parent is the input that travels.</para></summary>
    internal static int BindParents()
    {
        var bound = 0;
        try
        {
            var maps = Find.Maps;
            if (maps is null) return 0;
            var objects = Find.WorldObjects?.AllWorldObjects;
            if (objects is null) return 0;
            foreach (var map in maps)
            {
                if (map?.info is null || map.info.parent is not null) continue;
                if (!parentOf.TryGetValue(map.uniqueID, out var parentId) || parentId <= 0) continue;
                foreach (var candidate in objects)
                {
                    if (candidate is not MapParent parent || parent.ID != parentId) continue;
                    // **One parent cannot own two maps.** `Game.FindMap(MapParent)` (`:50121-50131`) returns the
                    // *first* map whose `info.parent` matches, so binding a second map to a parent that already has one
                    // makes `parent.Map` answer the other map for every reader - and `MapParent.HasMap` then says the
                    // new map does not exist while it plainly does. Measured: with a test hint pointing two maps at
                    // `Settlement#253`, `map-probe` reported both maps `→Settlement#253(...,HasMap=True)`, which is a
                    // state vanilla cannot produce and every `FindMap` caller would resolve to the wrong map.
                    if (parent.Map is not null) continue;
                    map.info.parent = parent;
                    bound++;
                    break;
                }
            }
            BoundParents += bound;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] map parent bind: " + error.GetType().Name + ": " + error.Message);
        }
        return bound;
    }

    internal static long BoundParents { get; private set; }

    /// <summary>Records the parent the authority reported for a map, so `BindParents` has something to match on.
    ///
    /// <para>Exposed for `map-bind-test`, which needs to reproduce the replica situation on a peer that is its own
    /// authority: `second-map` creates a map the way `Create` does - with no parent - and the binding can only be
    /// observed if the map id → world object id link exists. In production that link comes from `Publish`; here it comes
    /// from the probe, so the measurement is of the binding path and not of the publish path.</para></summary>
    internal static void SetParentHint(int mapId, int parentId)
    {
        if (parentId <= 0) parentOf.Remove(mapId);
        else parentOf[mapId] = parentId;
    }

    internal static int ParentHintCount => parentOf.Count;

    /// <summary>Forgets every hint. Used by `map-bind-test`, which has to remove the authority's own link for a map it
    /// is deliberately unbinding - otherwise the next `BindParents` restores it and the measurement describes the
    /// hint rather than the binding.</summary>
    internal static void ClearParentHints() => parentOf.Clear();

    /// <summary>Whether some other live map already points at this parent id.
    ///
    /// <para>`Game.FindMap(MapParent)` (`:50123-50128`) returns the first map whose `info.parent` matches, so two maps
    /// sharing one parent makes `parent.Map` answer whichever comes first - for `MapParent.HasMap`, for
    /// `GravshipUtility.TryGetNameOfGravshipOnMap` (`:793890`) and for every `MapParent.Map` reader. Vanilla never
    /// produces that state; a channel can, by carrying a stale hint.</para></summary>
    private static bool parentClaimed(int parentId, int exceptMapId)
    {
        var maps = Find.Maps;
        if (maps is null) return false;
        foreach (var map in maps)
        {
            if (map?.info?.parent is null || map.uniqueID == exceptMapId) continue;
            if (map.info.parent.ID == parentId) return true;
        }
        return false;
    }

    /// <summary>Map id → the world object id the authority named as its parent, recorded the first time each map was
    /// published. Kept rather than re-read from `map.info.parent`, which is null on a replica until the binding
    /// succeeds - that is the whole problem.</summary>
    private static readonly Dictionary<int, int> parentOf = [];

    /// <summary>Removes a map the authority no longer has.
    ///
    /// <para>`DeinitAndRemoveMap` rather than `Maps.Remove`: it clears the pawns' `Map` references, deregisters the
    /// lister things, and disposes the grids. A raw list removal leaves every pawn that was on that map pointing at an
    /// object nothing owns, and `Thing.Map` is read all over the frame.</para>
    ///
    /// <para><b>Vanilla's own `NullReferenceException`, and why a replica triggers it.</b> `MapDrawer` allocates its
    /// `sections` array lazily - both `RegenerateLayerNow` (`:79332-79335`) and the all-layers path (`:79364-79367`)
    /// start with `if (sections == null) sections = new Section[...]` - and its constructor (`:79177-79180`) only stores
    /// the map. `MapDrawer.Dispose` (`:79421-79435`) then does `sections[i, j].Dispose()` with <b>no null check</b>. A
    /// map that is disposed before it was ever drawn therefore throws inside `Map.Dispose`, after
    /// `DeinitAndRemoveMap` has already removed it from `Maps`. On a rendering peer that cannot happen: the map is drawn
    /// the moment it is shown. On a replica it is the normal case - the map is built by `ConstructComponents` and shown,
    /// but a replica whose camera never went there never regenerates a section. Measured: `抛出点 MapDrawer.Dispose；
    /// 栈 at Verse.MapDrawer.Dispose() ← at Verse.Map.Dispose()`, with the map already out of the list.
    ///
    /// <para>The fix is to make the field non-null before the removal, which is exactly what the first draw would have
    /// done. It is not a patch of vanilla behaviour: `Section[,]` of nulls is what `Dispose` expects to walk (each
    /// `Section.Dispose` is its own call), and allocating it costs two small arrays. Skipping `Dispose` instead would
    /// leak the global draw layers, which are allocated in `EnsureGlobalLayersInitialized` regardless of drawing.</para></summary>
    private static void Destroy(int id)
    {
        var map = FindMapByUniqueId(id);
        if (map is null) return;
        // Nothing to do here any more: `MapDrawerEmptyDisposePatch` handles the empty-dispose case at the call site.
        // Kept as a statement of intent so the ordering reads the same as before.
        Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
        MapsRemoved++;
    }

    /// <summary>Whether a map's `MapDrawer` would throw on `Dispose`, as a one-line report.
    ///
    /// <para><b>The defect it reports.</b> `MapDrawer`'s constructor (`:79177-79180`) only stores the map: both
    /// `sections` (`:79146`) and `global` (`:79148`) start null, and both are filled lazily - `sections` in
    /// `RegenerateLayerNow` (`:79332`) and the all-layers path (`:79364`), `global` in `EnsureGlobalLayersInitialized`
    /// (`:79393-79399`). `MapDrawer.Dispose` (`:79421-79435`) dereferences <b>both without a null check</b>:</para>
    ///
    /// <code>for (int i = 0; i &lt; SectionCount.x; i++) for (int j = 0; j &lt; SectionCount.z; j++) sections[i, j].Dispose();
    /// sections = null;
    /// foreach (MapDrawLayer item in global) item.Dispose();</code>
    ///
    /// <para>So a map disposed before it was drawn throws `NullReferenceException` inside `Map.Dispose` - measured:
    /// `抛出点 MapDrawer.Dispose；栈 at Verse.MapDrawer.Dispose() ← at Verse.Map.Dispose()`. On a rendering peer that
    /// cannot happen; on a replica built by `ConstructComponents` whose camera never went there, it is the normal
    /// case.</para>
    ///
    /// <para><b>Why the fix is a skip, not a repair.</b> An array of nulls is not enough (`Dispose` dereferences each
    /// element), and constructing real `Section`s needs the map to have a biome - `Section`'s constructor opens with
    /// `map.info.disableSunShadows || map.Biome.disableShadows` (`:79659`). <b>Correction to an earlier claim:</b> the
    /// `ArgumentOutOfRangeException` first seen there was attributed to "the drawing pipeline needs a renderer". It was
    /// not. `MapInfo` has no tile of its own (`Tile =&gt; parent?.Tile ?? PlanetTile.Invalid`, `:83462`), so on a
    /// parentless map `Map.Biome` reaches `WorldGrid[PlanetTile(-1)]` and throws - the same missing-parent defect
    /// `BindParents` fixes, not a renderer requirement. Measured after binding: `biome=TropicalRainforest` and `tile=160265`.
    /// The skip still stands on its own argument - a drawer that was never drawn holds nothing to release - but its
    /// "constructing sections is impossible" leg was false and must not be cited again.</para></summary>
    internal static bool DrawerIsEmpty(MapDrawer drawer)
    {
        try
        {
            var sectionsField = AccessTools.Field(typeof(MapDrawer), "sections");
            var globalField = AccessTools.Field(typeof(MapDrawer), "global");
            if (sectionsField is null || globalField is null) return false;
            return sectionsField.GetValue(drawer) is null && globalField.GetValue(drawer) is null;
        }
        catch { return false; }
    }

    internal static long PreparedDrawers { get; private set; }

    /// <summary>How many times an empty `MapDrawer.Dispose` was skipped. Reported by the probe so the patch is
    /// observable rather than assumed.</summary>
    internal static long EmptyDisposeSkipped { get; set; }

    /// <summary>The map with this `uniqueID`, or null. `Find.Maps` holds one to five entries, so a linear scan is the
    /// right shape here.</summary>
    internal static Map? FindMapByUniqueId(int id)
    {
        var maps = Find.Maps;
        if (maps is null) return null;
        foreach (var map in maps)
            if (map is not null && map.uniqueID == id) return map;
        return null;
    }

    /// <summary>Makes sure this peer's `nextMapID` is above an id the authority already handed out.
    ///
    /// <para>`nextMapID` is scribed (`UniqueIDsManager.ExposeData` `:516161`) so both ends load the same value, but only
    /// the authority calls `GetNextMapID`. Without this the counter here stays below the ids in use, and the next map
    /// this peer creates - or the next id any other code reads - collides with one that exists. Same rule as
    /// `SharedActions.SetNextThingId`.</para></summary>
    private static void AdvanceNextMapId(int used)
    {
        var ids = Find.UniqueIDsManager;
        if (ids is null) return;
        nextMapIdField ??= AccessTools.Field(ids.GetType(), "nextMapID");
        if (nextMapIdField is null) return;
        if (nextMapIdField.GetValue(ids) is not int current) return;
        if (current > used) return;
        nextMapIdField.SetValue(ids, used + 1);
    }

    internal static void Reset()
    {
        sent.Clear();
        failures.Clear();
        parentOf.Clear();
        BoundParents = 0;
        PreparedDrawers = 0;
        EmptyDisposeSkipped = 0;
    }

    internal static string Summary()
    {
        var maps = 0;
        var current = -1;
        var ids = new List<string>();
        try
        {
            var list = Find.Maps;
            if (list is not null)
            {
                maps = list.Count;
                current = Find.CurrentMap?.uniqueID ?? -1;
                foreach (var map in list)
                    if (map is not null) ids.Add(map.uniqueID.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception) { }
        var failed = failures.Count == 0 ? string.Empty
            : "；构建失败 " + string.Join(", ", failures.Select(pair => pair.Key + "=" + pair.Value));
        return $"本端 {maps} 张地图（当前 {current}，id {string.Join("/", ids)}）"
            + $" / 发 {LastSent} / 建 {MapsCreated}，删 {MapsRemoved}（失败 {CreateFailures}）"
            + (maps < 2 ? "（**单图：多据点那半测不到**）" : string.Empty) + failed;
    }
}

/// <summary>Skips `MapDrawer.Dispose` when the drawer provably holds nothing.
///
/// <para><b>The vanilla defect.</b> `MapDrawer`'s constructor (`:79177-79180`) only stores the map. Both `sections`
/// (`:79146`) and `global` (`:79148`) start null and are filled lazily - `sections` in `RegenerateLayerNow` (`:79332`)
/// and the all-layers path (`:79364`), `global` in `EnsureGlobalLayersInitialized` (`:79393-79399`). `MapDrawer.Dispose`
/// (`:79421-79435`) dereferences both with no null check, so a map disposed before it was ever drawn throws
/// `NullReferenceException` inside `Map.Dispose`. Measured: `抛出点 MapDrawer.Dispose；栈 at Verse.MapDrawer.Dispose()
/// ← at Verse.Map.Dispose()（地图已不在列表）`.</para>
///
/// <para><b>Why a replica hits it every time.</b> A rendering peer draws a map the moment it is shown, so `sections` is
/// populated before any removal. A replica map is built by `ConstructComponents` (`:76702-76710`, which does construct
/// the `MapDrawer` at `:76710`), and a headless replica whose camera never went there never regenerates a section - so
/// the removal throws every single time.</para>
///
/// <para><b>Why skip rather than repair.</b> Allocating the array is not enough (`Dispose` dereferences each element).
/// Constructing real `Section`s looked impossible at first - `ArgumentOutOfRangeException` from inside the constructor -
/// but that was misattributed: `Section` opens with `map.Biome.disableShadows` (`:79659`) and on a parentless map
/// `Map.Biome` reaches `WorldGrid[PlanetTile(-1)]`, which is the missing-parent defect, not a renderer requirement. The
/// skip is still the right shape here: a drawer that was never drawn holds nothing to release, and skipping is
/// independent of whether the sections could be built.</para>
///
/// <para><b>Why skipping loses nothing here.</b> With both fields null there is provably nothing to release: no
/// `Section` was ever constructed, and no global draw layer list exists. The `?.` in `mapDrawer?.Dispose()` (`:77592`) is
/// vanilla's own - this prefix does the same check one level deeper. A peer that did draw the map has a non-null
/// `sections` and takes the normal path untouched.</para>
///
/// <para><b>What the throw actually cost.</b> `mapDrawer?.Dispose()` is the second-to-last statement in `Map.Dispose`
/// (`:77592`), followed only by `Resources.UnloadUnusedAssets()` (`:77593`) and `MapGenerator.ClearDebugMode()`
/// (`:77594`). The map is already out of `Find.Maps` (`:50493`), and the grids, regions, pathing and listers are already
/// disposed (`:77580-77591`). So the damage is a skipped asset-unload hint and a skipped debug flag - cosmetic in effect,
/// but it fires on every replica map removal and fills the log with an exception nobody can act on.</para></summary>
[HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.Dispose))]
internal static class MapDrawerEmptyDisposePatch
{
    /// <summary>True to skip the original. Only for a drawer that was never drawn.</summary>
    internal static bool Prefix(MapDrawer __instance)
    {
        if (!MapLifecycleSync.DrawerIsEmpty(__instance)) return true;
        MapLifecycleSync.EmptyDisposeSkipped++;
        return false;
    }
}
