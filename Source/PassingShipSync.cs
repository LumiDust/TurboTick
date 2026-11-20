using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>How long each orbiting ship stays in comms range, for a replica whose ships never leave.
///
/// <para><b>Why a replica needs this.</b> <c>PassingShip.ticksUntilDeparture</c> is decremented only inside
/// <c>PassingShipTick</c> (<c>:606172</c>), which <c>PassingShipManager.PassingShipManagerTick</c> (<c>:606289</c>) calls
/// from the map tick - a display replica does not run it. At zero the ship calls <c>Depart</c> (<c>:606181</c>), which
/// removes it from <c>Map.passingShipManager</c> and, if the colony has a comms console, posts
/// <c>MessageShipHasLeftCommsRange</c>. So on a replica a ship that has long since gone still appears in the comms
/// console's contact list, and a player who calls it is talking to nothing.</para>
///
/// <para><b>Why not a whole-object fragment.</b> A ship is not a map thing: it lives in <c>Map.passingShipManager.passingShips</c>
/// (<c>:606256</c>), which no sweep visits, and is scribed with <c>LookMode.Deep</c> (<c>:606267</c>). Sending the whole
/// list would re-send the trade ship's stock, its trader kind and every `Thing` it holds on every countdown tick. The only
/// field that changes with time is this countdown, so this channel carries an id, the name and the count.</para>
///
/// <para><b>Why the name travels.</b> A `PassingShip`'s id is its `loadID` (<c>"PassingShip_" + loadID</c>, <c>:606249</c>),
/// which is stable across peers, but the comms list is drawn from the live objects - so the replica needs enough identity to
/// tell "the ship I have" from "the ship that left". A ship that is not in the payload is one whose countdown has ended.</para>
internal static class PassingShipSync
{
    /// <summary>Quantised to 250 ticks: the only readers are `Departed` (a comparison against zero) and the message that
    /// fires when it reaches zero, so four in-game minutes of precision moves nothing a player can see.</summary>
    private const int TickQuantum = 250;

    private static string? sent;
    private static string? lastSample;

    internal static long Sends { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static int DepartedApplied { get; private set; }
    internal static string? LastSample => lastSample;

    /// <summary>Ships whose quantised remaining time changed. A ship that is no longer present simply stops being sent; the
    /// replica removes it when its own recorded countdown reaches zero, which is the same rule the authority applies.</summary>
    internal static List<ReplicaPassingShips>? Publish()
    {
        try
        {
            var parts = new List<string>();
            foreach (var map in Find.Maps)
            {
                var ships = map.passingShipManager?.passingShips;
                if (ships is null) continue;
                foreach (var ship in ships)
                {
                    if (ship is null) continue;
                    // `loadID` is `protected int` with no accessor (`PassingShip :606143`), so it is read through the field
                    // rather than through a property that does not exist.
                    var id = ReadLoadId(ship);
                    if (id < 0) continue;
                    var remaining = Math.Max(0, ship.ticksUntilDeparture);
                    // The map comes first and is marked with `m`, because the list below merges every map's manager into
                    // one flat payload while the apply side has to put each ship back on the manager that owns it. Without
                    // the map, a ship on a map nobody is looking at never had its countdown written - and since the
                    // replica's own countdown is what makes it depart, that ship sat there forever until someone opened
                    // the map. The `m` prefix is what makes an old-format entry (plain numeric id) fail to parse instead
                    // of being read as a map id: every field is an integer, so a positional change alone would silently
                    // write the wrong number.
                    parts.Add("m" + map.uniqueID.ToString(CultureInfo.InvariantCulture)
                        + ":" + id.ToString(CultureInfo.InvariantCulture)
                        + ":" + (remaining / TickQuantum * TickQuantum).ToString(CultureInfo.InvariantCulture)
                        + ":" + (ship.name ?? string.Empty));
                }
            }
            parts.Sort(StringComparer.Ordinal);
            var encoded = string.Join(";", parts);
            if (sent == encoded) return null;
            sent = encoded;
            lastSample = encoded.Length == 0 ? "(无船)" : encoded;
            Sends++;
            return [new ReplicaPassingShips(encoded)];
        }
        catch (Exception) { return null; }
    }

    /// <summary>Applies the list onto the local map's ship list.
    ///
    /// <para>Ships the payload does not mention are **departed**, not deleted: `RemoveShip` (<c>:606187</c>) is what the
    /// game itself calls at zero, and calling it here keeps every other reference (the comms console's cached target, the
    /// trader's stock) consistent with a real departure.</para></summary>
    internal static void Apply(ReplicaPassingShips state)
    {
        try
        {
            // Per map, because the publish side merges every map's manager into this one flat list. Reading
            // `Find.CurrentMap` here meant a ship on any other map kept the countdown it was loaded with: it never
            // reached zero, so it never departed, and the removal pass below never even looked at that manager.
            if (state.Ships is not { Length: > 0 }) return;
            var seen = new Dictionary<int, HashSet<int>>();
            foreach (var entry in state.Ships.Split(';'))
            {
                var fields = entry.Split(':');
                if (fields.Length < 3) continue;
                // `m<mapId>`: see the publish side. A pre-map entry starts with a digit and is skipped here rather than
                // being read as "map = the ship's own loadID".
                if (!fields[0].StartsWith("m", StringComparison.Ordinal)) continue;
                if (!int.TryParse(fields[0].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId)) continue;
                if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)) continue;
                var manager = FindMapByUniqueId(mapId)?.passingShipManager;
                if (manager?.passingShips is null) continue;
                if (!seen.TryGetValue(mapId, out var here)) seen[mapId] = here = new HashSet<int>();
                foreach (var ship in manager.passingShips)
                {
                    if (ship is null || ReadLoadId(ship) != id) continue;
                    here.Add(id);
                    ship.ticksUntilDeparture = remaining;
                    break;
                }
            }
            // Sweep every map, not just the maps this payload mentioned: a manager whose list emptied on the authority
            // stops appearing in the payload entirely, and that is exactly the case the removal exists for.
            foreach (var map in Find.Maps)
            {
                var manager = map?.passingShipManager;
                if (manager?.passingShips is null) continue;
                seen.TryGetValue(map.uniqueID, out var here);
                for (var i = manager.passingShips.Count - 1; i >= 0; i--)
                {
                    var ship = manager.passingShips[i];
                    if (ship is null) continue;
                    if (here is not null && here.Contains(ReadLoadId(ship))) continue;
                    // Only a countdown that has already reached zero. A truncated frame must not mass-delete ships.
                    if (ship.ticksUntilDeparture > 0) continue;
                    try { manager.RemoveShip(ship); DepartedApplied++; }
                    catch (Exception) { }
                }
            }
            ApplyCount++;
        }
        catch (Exception) { }
    }

    /// <summary>`Map.uniqueID`, not `Map.Index`: the index is the position in `Find.Maps` (`:76452`), so removing a map
    /// shifts every later one and a ship's countdown would then be written onto a different map. `uniqueID` is assigned
    /// once (`GetNextMapID`, `:85222`) and scribed (`Map.ExposeData`, `:76818`).</summary>
    private static Map? FindMapByUniqueId(int mapId)
    {
        foreach (var map in Find.Maps)
            if (map is not null && map.uniqueID == mapId) return map;
        return null;
    }

    /// <summary>A ship's `loadID`: `protected int loadID = -1` (`:606143`) with no property, and `GetUniqueLoadID()`
    /// (`:606247`) formats it as a string - so the field is read rather than parsed back out of the string.</summary>
    private static int ReadLoadId(PassingShip ship) =>
        HarmonyLib.AccessTools.Field(typeof(PassingShip), "loadID")?.GetValue(ship) as int? ?? -1;

    internal static string Summary() =>
        "发 " + Sends.ToString(CultureInfo.InvariantCulture)
        + " / 应用 " + ApplyCount.ToString(CultureInfo.InvariantCulture)
        + " / 移走 " + DepartedApplied.ToString(CultureInfo.InvariantCulture)
        + " / 样本 " + (lastSample ?? "(无)");

    internal static void Reset()
    {
        sent = null;
        lastSample = null;
    }
}
