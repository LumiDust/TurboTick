using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Indoor temperature the host accumulated, applied on a replica through <c>Room.Temperature</c>.
///
/// <para><b>Why a replica needs this.</b> A room's temperature is a single float in <c>RoomTempTracker.temperatureInt</c>
/// and it only ever moves inside <c>EqualizeTemperature</c>, which <c>MapTemperatureTick</c> calls every 120 ticks. A replica
/// does not tick, so every indoor room keeps the temperature the save was loaded with - forever. The visible results are a
/// freezer that reads room temperature and never gets cold, a heated bedroom that never warms up, and a temperature overlay
/// painted with the values from the moment the save was loaded.</para>
///
/// <para><b>Why the outdoors needs nothing.</b> Outdoor temperature is a computed getter over
/// <c>Find.World.tileTemperatures.GetOutdoorTemp(map.Tile)</c>, which is derived from <c>absTick</c> - and
/// <see cref="ReplicaAbsClockPatch"/> already makes <c>TicksAbs</c> the host's. So an outdoor room's temperature is already
/// correct on a replica and sending it would be noise. Only the accumulated indoor value is frozen.</para>
///
/// <para><b>Why a room cannot be identified by <c>Room.ID</c>.</b> It is a process-local counter (<c>Room.MakeNewRoom</c>
/// assigns <c>nextRoomID++</c>) and it is never scribed, so the host and a replica that loaded the same save will number
/// their rooms differently - and rooms keep merging and splitting as walls go up. ColonyTogether solves the same problem the
/// same way (<c>ReceiveOutdoor</c>, ct045:142307): carry a representative cell and resolve the room with
/// <c>GetRoom(cell)</c> on the far end. That also makes a room that no longer exists at that cell a no-op instead of an
/// error.</para>
///
/// <para><b>Why the first frame is the whole set.</b> Same reason as snow: a save loaded in winter already has indoor
/// temperatures on every room, and no hook fired for any of them. A dirty-only channel would be correct and would send
/// nothing on exactly the saves that need it most.</para>
internal static class RoomTempSync
{
    /// <summary>Representative cells whose room temperature changed, keyed by map id then cell index. The room is resolved
    /// at publish time rather than at hook time so several writes inside one frame cost one entry.</summary>
    private static readonly Dictionary<int, HashSet<int>> dirty = [];

    /// <summary>Maps whose full temperature set has not been sent yet. A save loaded mid-winter has an accumulated value on
    /// every room and no hook ever fired for any of them.</summary>
    private static readonly HashSet<int> unsent = [];

    /// <summary>A tenth of a degree. <c>EqualizeTemperature</c> adds fractions around 1e-4 per 120-tick interval, so
    /// unquantised this would be an entry per room per frame forever; a tenth is also finer than the tooltip shows.</summary>
    private const float Quantum = 10f;

    internal static long RoomsSent { get; private set; }
    internal static long RoomsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Marks one room as needing a send, given any cell it occupies.</summary>
    internal static void Touched(Map? map, IntVec3 cell)
    {
        if (map is null || !cell.InBounds(map)) return;
        if (!dirty.TryGetValue(map.uniqueID, out var cells)) dirty[map.uniqueID] = cells = [];
        cells.Add(map.cellIndices.CellToIndex(cell));
    }

    /// <summary>Collects what changed. For a map never sent, every room that is not outdoors.</summary>
    internal static List<ReplicaRoomTemp>? Publish()
    {
        if (dirty.Count == 0 && unsent.Count == 0) return null;
        List<ReplicaRoomTemp>? result = null;
        try
        {
            foreach (var map in Find.Maps)
            {
                var first = unsent.Add(map.uniqueID);
                HashSet<int>? pending = dirty.TryGetValue(map.uniqueID, out var cells) ? cells : null;
                if (!first && pending is null) continue;
                if (map.regionGrid is null) continue;
                // Reference equality is the right identity here: two distinct rooms are distinct objects, and a room has no
                // stable cross-peer id (see the class comment).
                var seen = new HashSet<Room>();
                foreach (var room in map.regionGrid.AllRooms)
                {
                    if (room is null) continue;
                    // Outdoor rooms read a getter that is already the host's on both ends; deep space has no meaningful
                    // indoor temperature to show either.
                    if (room.UsesOutdoorTemperature) continue;
                    var anchor = Anchor(room, map);
                    if (!anchor.IsValid) continue;
                    if (!first && (pending is null || !pending.Contains(map.cellIndices.CellToIndex(anchor)))) continue;
                    if (!seen.Add(room)) continue;
                    var temperature = room.Temperature;
                    if (!float.IsFinite(temperature)) continue;
                    var quantised = (float)(Math.Round(temperature * Quantum) / Quantum);
                    (result ??= []).Add(new ReplicaRoomTemp(map.uniqueID, anchor.x, anchor.z, quantised));
                    RoomsSent++;
                }
            }
            foreach (var mapId in dirty.Keys.ToArray()) dirty.Remove(mapId);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] room temperature publish: " + error.GetType().Name + ": " + error.Message);
        }
        return result;
    }

    /// <summary>The cell standing in for a room: its first region's first cell, which survives being walked through and is
    /// stable while the room's shape is.</summary>
    private static IntVec3 Anchor(Room room, Map map)
    {
        try
        {
            var region = room.FirstRegion;
            if (region is not null && region.AnyCell.IsValid) return region.AnyCell;
            return IntVec3.Invalid;
        }
        catch (Exception) { return IntVec3.Invalid; }
    }

    /// <summary>Applies one room's temperature.
    ///
    /// <para>Writes the property rather than the private field: its setter clamps to [-273.15, 1000], which is the guard
    /// that keeps a corrupt frame from painting the overlay with a temperature that cannot exist.</para>
    ///
    /// <para>The overlay's colour cache is invalidated afterwards. <c>MapTemperature.Drawer</c> is a
    /// <c>CellBoolDrawer</c> that caches one colour per cell and is only rebuilt when marked dirty - and the thing that
    /// marks it dirty is <c>MapTemperatureTick</c>'s <c>TicksGame % 60</c> branch, which a replica never reaches. Without
    /// this the numbers would update and the map would keep its load-time colours.</para></summary>
    internal static void Apply(ReplicaRoomTemp state)
    {
        try
        {
            var map = Find.Maps.FirstOrDefault(each => each.uniqueID == state.Map);
            if (map is null || map.regionGrid is null) return;
            var cell = new IntVec3(state.X, 0, state.Z);
            if (!cell.InBounds(map)) return;
            var room = cell.GetRoom(map);
            if (room is null || room.UsesOutdoorTemperature) return;
            var target = state.Temp;
            if (!float.IsFinite(target)) return;
            // A tenth of a degree is the smallest step the tooltip shows; writing anything finer would mark the overlay
            // dirty every frame for a number nobody can see.
            if (Math.Abs(room.Temperature - target) < 0.05f) return;
            room.Temperature = target;
            RoomsApplied++;
            map.mapTemperature?.Drawer.SetDirty();
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] room temperature apply failed ({state.Map}:{state.X},{state.Z}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        dirty.Clear();
        unsent.Clear();
    }

    internal static string Summary() =>
        $"房间温度 发出 {RoomsSent} / 应用 {RoomsApplied}（失败 {ApplyFailures}）";
}

/// <summary>Marks a room as needing a send whenever the equalisation pass moves it.
///
/// <para>Postfix on <c>RoomTempTracker.EqualizeTemperature</c> rather than on the <c>Room.Temperature</c> setter: the
/// setter is also what <see cref="RoomTempSync.Apply"/> writes through, so hooking it would echo the replica's own write
/// back into the dirty set. The equalisation pass is the only writer on an authority.</para>
[HarmonyPatch(typeof(RoomTempTracker), nameof(RoomTempTracker.EqualizeTemperature))]
internal static class RoomTempTrackerEqualizePatch
{
    private static readonly AccessTools.FieldRef<RoomTempTracker, Room> roomOf =
        AccessTools.FieldRefAccess<RoomTempTracker, Room>("room");

    private static void Postfix(RoomTempTracker __instance)
    {
        try
        {
            var room = roomOf(__instance);
            if (room?.Map is null) return;
            var anchor = room.FirstRegion?.AnyCell;
            if (anchor is null || !anchor.Value.IsValid) return;
            RoomTempSync.Touched(room.Map, anchor.Value);
        }
        catch (Exception)
        {
            // A postfix that throws every 120 ticks would bury the log; the channel simply misses this interval.
        }
    }
}
