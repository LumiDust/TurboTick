using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Creates and destroys world objects, which neither position nor state can do.
///
/// <para><b>The defect.</b> <see cref="WorldObjectSync"/> sends where an object is and
/// <see cref="WorldObjectStateSync"/> sends what it carries, and both start by looking the object up by <c>ID</c>.
/// A world object that did not exist when the save was loaded is therefore never found: a new caravan, a gravship that
/// launched, a settlement the authority destroyed, a quest site that appeared. The replica draws the world as it stood
/// at load and never learns otherwise. The harmless-looking half is a missing object; the visible half is a caravan the
/// host has already disbanded that still sits on the planet here, and - worse - a destroyed settlement that still
/// exists, which every gate and every caravan route reads as a real place.</para>
///
/// <para><b>Why this is the same shape as <see cref="MapLifecycleSync"/>.</b> A map's id is allocated once at creation
/// and creation itself had no channel, so a map the authority generated was simply absent here. Same for world objects:
/// `WorldObjectMaker.MakeWorldObject` assigns `ID = GetNextWorldObjectID()` (`:784113`) and nothing on the wire ever
/// said "this id now exists". The two channels differ only in what they construct.</para>
///
/// <para><b>Why the whole object travels as a Scribe fragment on creation.</b> A caravan is a `ThingOwner` of pawns
/// plus a pather, a forage tracker and a trader; a settlement is a set of inhabitants and a timer; a site is a
/// `Quest`. Reconstructing any of them field by field would be a hand-written table that goes stale and, worse, would
/// have to guess at the parts vanilla fills in during `PostMake`/`PostAdd`. `ScribeBridge.Save` runs the game's own
/// writer, which is the list the developers maintain, and `ScribeBridge.Load` runs its reader - the same round trip
/// `WorldObjectStateSync` already uses for live objects. A deep fragment is bigger than a position, which is why it is
/// only sent once per object rather than per frame.</para>
///
/// <para><b>Why a destruction is its own list and not the absence of a creation.</b> The replica has to tell "the
/// authority has not told me about this yet" from "the authority removed it", and an absence cannot. Carrying
/// `Destroyed = true` on the same row would work, but then a frame that dropped the row would resurrect an object the
/// authority had destroyed - and this project's event channels all learned that lesson already (see
/// <see cref="EventUnion"/>): a removal has to be an event, not a missing entry.</para>
///
/// <para><b>Why the destruction is not `DeSpawn`.</b> `WorldObject.Destroy` (`:798778`) is the vanilla path: it calls
/// `Find.WorldObjects.Remove` (which runs `PostRemove`), sets `destroyed`, notifies the faction manager and every comp's
/// `PostDestroy`, and fires the quest target signals. Only `Find.WorldObjects.Remove` would leave the object in the
/// caches and the faction notifications unfired; only setting `destroyed` would leave it on the planet. The whole
/// method is what "this settlement is gone" means.</para></summary>
internal static class WorldObjectLifecycleSync
{
    /// <summary>Every world object id this peer has told a replica about. Seeded on the first pass with the whole
    /// world, because a replica that loaded the same save already has those objects - announcing them again would
    /// reconstruct two hundred and fifty objects on the first frame for no change.</summary>
    private static readonly HashSet<int> known = [];

    /// <summary>Ids this peer has destroyed, so a later frame cannot re-announce them.</summary>
    private static readonly HashSet<int> destroyed = [];

    private static bool seeded;

    internal static long Created { get; private set; }
    internal static long DestroyedApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static string? LastCreateSample { get; private set; }
    internal static string? LastDestroySample { get; private set; }

    /// <summary>Objects created on the authority since the last call, and objects no longer present.
    ///
    /// <para>The removal list is computed by diffing against <see cref="known"/> rather than from a hook, because
    /// destruction has no single entry point worth patching: `WorldObject.Destroy` is `virtual` and subclasses override
    /// it (`Settlement` does, to fire its own signals) without calling the base in some paths. Diffing the live set
    /// catches every route including ones added by a future DLC.</para></summary>
    internal static (List<ReplicaWorldObjectCreation>? Created, List<int>? Removed) Publish()
    {
        try
        {
            var live = new HashSet<int>();
            foreach (var each in Find.WorldObjects.AllWorldObjects)
                if (each is not null && !each.Destroyed) live.Add(each.ID);
            if (!seeded)
            {
                // First pass: everything that exists was loaded from the same save the replica has. Marking them known
                // rather than sending them is what keeps the first frame from reconstructing the whole planet.
                foreach (var id in live) known.Add(id);
                seeded = true;
                return (null, null);
            }
            List<ReplicaWorldObjectCreation>? created = null;
            List<int>? removed = null;
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                if (each is null || each.Destroyed) continue;
                if (known.Contains(each.ID)) continue;
                // The fragment is the creation. `ScribeBridge.Save` returns null when the object cannot be written
                // yet (a caravan mid-formation holds a pawn that is being moved); it is left unknown so the next frame
                // retries instead of announcing an object with no body.
                var body = ScribeBridge.Save(each);
                if (body is not { Length: > 0 }) continue;
                var tile = each.Tile;
                (created ??= []).Add(new ReplicaWorldObjectCreation(
                    each.ID, each.def?.defName ?? string.Empty, tile.Valid ? tile.tileId : -1,
                    tile.Valid ? tile.Layer.LayerID : 0, body));
                known.Add(each.ID);
                Created++;
                LastCreateSample = (each.def?.defName ?? "?") + "#" + each.ID.ToString(CultureInfo.InvariantCulture)
                    + "(" + body.Length.ToString(CultureInfo.InvariantCulture) + " B)";
            }
            // Removals: everything this peer has announced or seeded that the authority no longer has. Objects a
            // replica created for another player's benefit but that were never announced are not in `known`.
            foreach (var id in known.Where(id => !live.Contains(id)).ToList())
            {
                known.Remove(id);
                if (destroyed.Add(id)) (removed ??= []).Add(id);
                DestroyedApplied++;
                LastDestroySample = "#" + id.ToString(CultureInfo.InvariantCulture);
            }
            return (created, removed);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] world object lifecycle publish: " + error.GetType().Name + ": " + error.Message);
            return (null, null);
        }
    }

    /// <summary>Rebuilds one object from the authority's fragment.
    ///
    /// <para>The id is forced back onto the object after `MakeWorldObject` allocates one here: the authority is the
    /// only allocator of an identity that both peers must agree on, exactly as with `thingIDNumber`. The counter is
    /// then advanced past it so anything this peer creates later cannot collide.</para>
    ///
    /// <para>Loaded **before** `Add`, because `WorldObjectsHolder.Add` (`:784291`) runs `SpawnSetup` and `PostAdd` -
    /// and a caravan whose `PostAdd` runs before its `pather` was deserialised throws inside `Notify_CaravanSpawned`.
    /// The fragment's own `PostLoadInit` pass also expects to be running on an object that is not yet on the planet, so
    /// this ordering is what both sides want.</para></summary>
    internal static void Apply(ReplicaWorldObjectCreation state)
    {
        try
        {
            foreach (var each in Find.WorldObjects.AllWorldObjects)
                if (each is not null && each.ID == state.Id) return;   // already here: nothing to do
            var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail(state.Def);
            if (def is null)
            {
                if (ApplyFailures++ < 6)
                    Log.Warning("[TurboTick] 世界物件创建失败：找不到 def " + state.Def + "（id " + state.Id + "）");
                return;
            }
            var made = WorldObjectMaker.MakeWorldObject(def);
            if (made is null) return;
            // Identity first: the fragment scribes `ID` too, and loading it would overwrite whatever the local
            // allocator handed out. `ScribeBridge.Load` restores identity by itself (see its own remarks), but the
            // value it restores has to be the authority's, which is what `state.Id` is.
            made.ID = state.Id;
            if (!ScribeBridge.Load(made, state.Body))
            {
                ApplyFailures++;
                return;
            }
            if (state.Tile >= 0)
            {
                // Tile after the load: `WorldObject.ExposeData` writes `tile` against the field, so the fragment
                // already carries it - but a fragment produced before the object had a valid tile would leave the
                // object unplaceable, and `Add` then logs an error and puts it on tile 0.
                //
                // The two-argument constructor takes the layer id directly (`PlanetTile(int, int)`, `:772659`), which
                // is the same form `WorldObjectSync.Apply` uses; resolving the layer object first would need a search
                // and would still have to decide what to do with an id this planet does not have.
                var tile = new PlanetTile(state.Tile, state.Layer);
                if (tile.Valid) made.Tile = tile;
            }
            if (!made.Tile.Valid && made is not PocketMapParent)
            {
                ApplyFailures++;
                if (ApplyFailures <= 6)
                    Log.Warning("[TurboTick] 世界物件创建失败：" + state.Def + "#" + state.Id + " 没有有效地块");
                return;
            }
            Find.WorldObjects.Add(made);
            AdvanceNextWorldObjectId(state.Id);
            Created++;
            // **A `MapParent` that arrives after its map has to be bound to it.** `Map.IsPlayerHome` (`:76546`) reads
            // `info.parent.Faction`, and a replica map whose parent is null answers false - which its 132 readers treat
            // as foreign land. The two channels are independent, so the binding is attempted from both sides.
            if (made is MapParent) MapLifecycleSync.BindParents();
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] world object create failed ({state.Id} {state.Def}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Destroys one object by the vanilla path, so every cache and notification agrees.</summary>
    internal static void ApplyRemoval(int id)
    {
        try
        {
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                if (each is null || each.ID != id) continue;
                // `Destroy` already checks `Spawned`, but a replica may hold an object it never spawned; `Remove`
                // alone would then log「not here」. Guarding is cheaper than a log line per removal.
                if (each.Spawned) Find.WorldObjects.Remove(each);
                each.Destroy();
                DestroyedApplied++;
                return;
            }
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] world object remove failed (" + id + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Keeps this peer's `nextWorldObjectID` above an id the authority handed out. `UniqueIDsManager` scribes
    /// the counter, so both ends load the same value - but only the authority calls `GetNextWorldObjectID`, and without
    /// this the next object created here reuses an id that already means something. Same rule as
    /// <see cref="WorldObjectLifecycleSync"/>'s counterpart in `MapLifecycleSync` and `SharedActions.SetNextThingId`.</summary>
    private static void AdvanceNextWorldObjectId(int used)
    {
        var ids = Find.UniqueIDsManager;
        if (ids is null) return;
        nextWorldObjectIdField ??= AccessTools.Field(ids.GetType(), "nextWorldObjectID");
        if (nextWorldObjectIdField is null) return;
        if (nextWorldObjectIdField.GetValue(ids) is not int current) return;
        if (current > used) return;
        nextWorldObjectIdField.SetValue(ids, used + 1);
    }

    private static System.Reflection.FieldInfo? nextWorldObjectIdField;

    internal static void Reset()
    {
        known.Clear();
        destroyed.Clear();
        seeded = false;
        Created = 0;
        DestroyedApplied = 0;
        ApplyFailures = 0;
        LastCreateSample = null;
        LastDestroySample = null;
    }

    internal static string Summary()
    {
        var objects = 0;
        try { objects = Find.WorldObjects?.AllWorldObjects?.Count ?? 0; } catch (Exception) { }
        var text = new StringBuilder(96);
        text.Append("本端 ").Append(objects).Append(" 个 / 建 ").Append(Created).Append("，删 ").Append(DestroyedApplied)
            .Append("（失败 ").Append(ApplyFailures).Append("）");
        if (LastCreateSample is { Length: > 0 }) text.Append(" / 建样本 ").Append(LastCreateSample);
        if (LastDestroySample is { Length: > 0 }) text.Append(" / 删样本 ").Append(LastDestroySample);
        return text.ToString();
    }
}
