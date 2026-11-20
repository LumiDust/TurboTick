using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Where a world object sits on the planet, applied on a replica through the same <c>WorldObject.Tile</c> setter.
///
/// <para><b>Why a replica needs this.</b> World objects - caravans, settlements, ships, sites - live in
/// <c>Find.WorldObjects</c>, not in any map's thing grid. They move through their own interval ticks
/// (<c>Caravan_PathFollower.TryEnterNextPathTile</c> assigns <c>caravan.Tile = nextTile</c>), which a replica never runs, so
/// every object stays on the tile the save was loaded with. A caravan that has been travelling for a season still draws at
/// its origin, and the colony panel's caravan list never updates its destination either.</para>
///
/// <para><b>Why one hook covers every kind.</b> <c>WorldObject.Tile</c> is the single setter every subclass goes through -
/// a caravan's path follower, a settlement's spawn, a gravship's landing all assign that property. Its setter also does the
/// work the renderer needs (dirty the old and new tiles' fast finders, notify the static drawer), so writing through it is not
/// just convenient, it is the only correct way to move one.</para>
///
/// <para><b>Why loading does not dirty anything.</b> <c>WorldObject.ExposeData</c> scribes <c>Scribe_Values.Look(ref tile,
/// "tile")</c> against the field, not the property, so the setter never runs on load and this hook never sees a save being
/// read. That matters here more than usual: the save carries all 254 world objects on this server, and a hook that fired for
/// each of them would republish the whole world on every load.</para>
///
/// <para><b>Why <c>ID</c>.</b> <c>WorldObject.ID</c> is scribed (<c>Scribe_Values.Look(ref ID, "ID", -1)</c>), so unlike a
/// room id it survives the trip - and unlike a <c>Thing</c>'s <c>thingIDNumber</c> it is assigned by the world object itself,
/// not by the map's allocator.</para></summary>
internal static class WorldObjectSync
{
    /// <summary>Objects whose tile changed, keyed by <c>ID</c>. The tile is re-read at publish time so several moves inside
    /// one frame cost one entry.</summary>
    private static readonly HashSet<int> dirty = [];

    /// <summary>Whether the full set has been sent. A save carries every world object on the planet, and none of them fires a
    /// hook on load.</summary>
    private static bool unsent = true;

    internal static long ObjectsSent { get; private set; }
    internal static long ObjectsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Touched(WorldObject? object_)
    {
        if (object_ is null || !object_.Spawned) return;
        dirty.Add(object_.ID);
    }

    /// <summary>Collects what moved. On the first call, every spawned world object.</summary>
    internal static List<ReplicaWorldObject>? Publish()
    {
        try
        {
            if (dirty.Count == 0 && !unsent) return null;
            List<ReplicaWorldObject>? result = null;
            var seen = new HashSet<int>();
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                if (each is null || !each.Spawned) continue;
                seen.Add(each.ID);
                if (!unsent && !dirty.Contains(each.ID)) continue;
                var tile = each.Tile;
                if (!tile.Valid) continue;
                (result ??= []).Add(new ReplicaWorldObject(each.ID, tile.tileId, tile.Layer.LayerID, each.def?.defName));
                ObjectsSent++;
            }
            unsent = false;
            dirty.Clear();
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] world object publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Moves one object.
    ///
    /// <para>Writes the property, not the field: its setter dirties both tiles' fast finders and notifies the static drawer,
    /// which is what makes the world map redraw. Writing <c>tile</c> directly would leave the object at the right place with
    /// the wrong picture.</para>
    ///
    /// <para>Skips an object whose def name does not match. <c>ID</c> is the only identity on the wire and a save that grew
    /// between the two peers could reuse one, so a wrong pair would otherwise teleport the wrong thing across the planet.</para>
    /// </summary>
    internal static void Apply(ReplicaWorldObject state)
    {
        try
        {
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                if (each is null || each.ID != state.Id) continue;
                if (state.Def is { Length: > 0 } expected && each.def?.defName != expected) return;
                // The layer is part of the coordinate: `new PlanetTile(id)` assumes layer 0, which on a surface-plus-orbit
                // planet is a different world. Two-argument construction is the only one that keeps them together.
                var tile = new PlanetTile(state.Tile, state.Layer);
                if (!tile.Valid) return;
                if (each.Tile == tile) return;
                each.Tile = tile;
                ObjectsApplied++;
                return;
            }
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] world object apply failed ({state.Id} -> {state.Tile}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        dirty.Clear();
        unsent = true;
    }

    internal static string Summary()
    {
        var count = 0;
        try { count = Find.WorldObjects?.AllWorldObjects?.Count ?? 0; } catch (Exception) { }
        return $"{count} 个 / 发 {ObjectsSent} / 应用 {ObjectsApplied}（失败 {ApplyFailures}）";
    }
}

/// <summary>Marks a world object as moved whenever its tile is assigned.
///
/// <para>Postfix on the <c>WorldObject.Tile</c> setter. Every mover goes through it - <c>Caravan_PathFollower</c>'s next-tile
/// step, a settlement spawning, a gravship landing - so one hook covers the whole family, and the setter is the place the
/// renderer already depends on.</para>
///
/// <para>Reads the property rather than <c>__0</c>: the setter is `set_Tile(PlanetTile value)`, so a postfix receives the
/// instance but not the assigned value in a form worth trusting, and re-reading costs one field access.</para></summary>
[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Tile), MethodType.Setter)]
internal static class WorldObjectTilePatch
{
    private static void Postfix(WorldObject __instance)
    {
        try { WorldObjectSync.Touched(__instance); }
        catch (Exception)
        {
            // A throwing postfix would fire on every world tick; the channel simply misses this move.
        }
    }
}
