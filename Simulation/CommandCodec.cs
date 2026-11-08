using System.Text;
using TurboTick.Networking;

namespace TurboTick.Simulation;

/// <summary>
/// Wire format for player actions. Encodes every player-issued action into a compact payload:
/// encode → server order → decode and replay at a fixed tick on all peers.
/// </summary>
public static class CommandCodec
{
    public const string Designation = "designation";
    public const string RemoveDesignation = "remove-designation";
    public const string Blueprint = "blueprint";
    public const string CancelThing = "cancel-thing";
    public const string Draft = "draft";
    public const string Order = "order";
    public const string Transport = "transport";
    public const string Forbid = "forbid";
    public const string Priority = "priority";
    public const string ZoneCell = "zone-cell";
    public const string ZoneDelete = "zone-delete";
    public const string BillAdd = "bill-add";
    public const string BillDelete = "bill-delete";
    public const string AreaAssign = "area-assign";
    /// <summary>Generic fallback for any gizmo button. The listed operations each get their own compact
    /// command, but 1.7 has no single chokepoint for the rest: every gizmo action is a delegate field
    /// (<c>Command_Action.action</c>, <c>Command_Toggle.toggleAction</c>), and the only shared code is
    /// <c>ProcessInput</c>. So the capture takes the target's Scribe state before and after the click and
    /// ships the result, which covers flicking, filters, bed owner, plant-to-grow and anything a third-party
    /// mod adds, without a per-type codec.</summary>
    public const string ThingState = "thing-state";

    /// <summary>The target thing id is part of the payload because several designations (HarvestPlant,
    /// Hunt, Tame, Mine) are indexed by Thing only. Sending just a cell made the receiver look a location
    /// up in a Thing-keyed index, which throws and faulted the whole simulation.</summary>
    public static GameCommand DesignationCommand(string defName, int map, int x, int z, int targetThingId, long sequence) =>
        new(sequence, 0, Designation, Encoding.UTF8.GetBytes($"{defName}|{map}|{x}|{z}|{targetThingId}"));

    public static GameCommand RemoveDesignationCommand(string defName, int map, int x, int z, int targetThingId, long sequence) =>
        new(sequence, 0, RemoveDesignation, Encoding.UTF8.GetBytes($"{defName}|{map}|{x}|{z}|{targetThingId}"));

    public static GameCommand BlueprintCommand(string buildable, string stuff, int map, int x, int z, int rot, long sequence) =>
        new(sequence, 0, Blueprint, Encoding.UTF8.GetBytes($"{buildable}|{stuff}|{map}|{x}|{z}|{rot}"));

    public static GameCommand CancelThingCommand(int thingId, long sequence) =>
        new(sequence, 0, CancelThing, Encoding.UTF8.GetBytes($"{thingId}"));

    public static GameCommand DraftCommand(int pawnId, bool drafted, long sequence) =>
        new(sequence, 0, Draft, Encoding.UTF8.GetBytes($"{pawnId}|{(drafted ? 1 : 0)}"));

    public static GameCommand OrderCommand(int pawnId, string jobDef, int targetACell, int targetAThing, int targetBCell, int targetBThing, int count, long sequence) =>
        new(sequence, 0, Order, Encoding.UTF8.GetBytes($"{pawnId}|{jobDef}|{targetACell}|{targetAThing}|{targetBCell}|{targetBThing}|{count}"));

    public static GameCommand TransportCommand(int transporterId, int thingId, int count, long sequence) =>
        new(sequence, 0, Transport, Encoding.UTF8.GetBytes($"{transporterId}|{thingId}|{count}"));

    public static GameCommand ForbidCommand(int thingId, bool forbidden, long sequence) =>
        new(sequence, 0, Forbid, Encoding.UTF8.GetBytes($"{thingId}|{(forbidden ? 1 : 0)}"));

    public static GameCommand PriorityCommand(int pawnId, string workDef, int priority, long sequence) =>
        new(sequence, 0, Priority, Encoding.UTF8.GetBytes($"{pawnId}|{workDef}|{priority}"));

    public static GameCommand ZoneCellCommand(string zoneType, int zoneId, int map, int x, int z, bool add, long sequence) =>
        new(sequence, 0, ZoneCell, Encoding.UTF8.GetBytes($"{zoneType}|{zoneId}|{map}|{x}|{z}|{(add ? 1 : 0)}"));

    public static GameCommand ZoneDeleteCommand(int map, int zoneId, long sequence) =>
        new(sequence, 0, ZoneDelete, Encoding.UTF8.GetBytes($"{map}|{zoneId}"));

    public static GameCommand BillAddCommand(int buildingId, string recipeDef, int repeatCount, long sequence) =>
        new(sequence, 0, BillAdd, Encoding.UTF8.GetBytes($"{buildingId}|{recipeDef}|{repeatCount}"));

    public static GameCommand BillDeleteCommand(int buildingId, int billIndex, long sequence) =>
        new(sequence, 0, BillDelete, Encoding.UTF8.GetBytes($"{buildingId}|{billIndex}"));

    public static GameCommand AreaAssignCommand(int pawnId, int areaId, long sequence) =>
        new(sequence, 0, AreaAssign, Encoding.UTF8.GetBytes($"{pawnId}|{areaId}"));

    /// <summary>Payload is the thing id, a newline, then the Scribe fragment. A separator other than '|' is
    /// required because the fragment is XML and may contain any punctuation.</summary>
    public static GameCommand ThingStateCommand(int thingId, string xml, long sequence) =>
        new(sequence, 0, ThingState, Encoding.UTF8.GetBytes($"{thingId}\n{xml}"));

    public static bool TryDecodeThingState(byte[] payload, out int thingId, out string xml)
    {
        thingId = 0;
        xml = "";
        if (payload is null || payload.Length < 3 || payload.Length > 64 * 1024) return false;
        var text = Encoding.UTF8.GetString(payload);
        var split = text.IndexOf('\n');
        if (split <= 0 || split == text.Length - 1) return false;
        if (!int.TryParse(text[..split], out thingId)) return false;
        xml = text[(split + 1)..];
        return xml.Length > 0;
    }

    // ---------- Decoders ----------

    public static bool TryDecodeDesignation(byte[] payload, out string defName, out int map, out int x, out int z, out int thingId)
    {
        defName = "";
        map = x = z = thingId = 0;
        if (!TryDecode(payload, 5, out var p)) return false;
        defName = p[0];
        return defName.Length > 0 && int.TryParse(p[1], out map) && int.TryParse(p[2], out x)
            && int.TryParse(p[3], out z) && int.TryParse(p[4], out thingId);
    }

    public static bool TryDecodeRemoveDesignation(byte[] payload, out string defName, out int map, out int x, out int z, out int thingId)
    {
        defName = "";
        map = x = z = thingId = 0;
        if (!TryDecode(payload, 5, out var p)) return false;
        defName = p[0];
        return defName.Length > 0 && int.TryParse(p[1], out map) && int.TryParse(p[2], out x)
            && int.TryParse(p[3], out z) && int.TryParse(p[4], out thingId);
    }

    public static bool TryDecodeBlueprint(byte[] payload, out string buildable, out string stuff, out int map, out int x, out int z, out int rot)
    {
        buildable = stuff = "";
        map = x = z = rot = 0;
        if (!TryDecode(payload, 6, out var p)) return false;
        buildable = p[0];
        stuff = p[1];
        return buildable.Length > 0 && int.TryParse(p[2], out map) && int.TryParse(p[3], out x)
            && int.TryParse(p[4], out z) && int.TryParse(p[5], out rot);
    }

    public static bool TryDecodeCancelThing(byte[] payload, out int thingId)
    {
        thingId = 0;
        return TryDecode(payload, 1, out var p) && int.TryParse(p[0], out thingId);
    }

    public static bool TryDecodeDraft(byte[] payload, out int pawnId, out bool drafted)
    {
        pawnId = 0;
        drafted = false;
        if (!TryDecode(payload, 2, out var p) || !int.TryParse(p[0], out pawnId) || !int.TryParse(p[1], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        drafted = flag == 1;
        return true;
    }

    public static bool TryDecodeOrder(byte[] payload, out int pawnId, out string jobDef, out int aCell, out int aThing, out int bCell, out int bThing, out int count)
    {
        pawnId = aCell = aThing = bCell = bThing = count = 0;
        jobDef = "";
        if (!TryDecode(payload, 7, out var p) || !int.TryParse(p[0], out pawnId)) return false;
        jobDef = p[1];
        return jobDef.Length > 0 && int.TryParse(p[2], out aCell) && int.TryParse(p[3], out aThing)
            && int.TryParse(p[4], out bCell) && int.TryParse(p[5], out bThing) && int.TryParse(p[6], out count);
    }

    public static bool TryDecodeTransport(byte[] payload, out int transporterId, out int thingId, out int count)
    {
        transporterId = thingId = count = 0;
        return TryDecode(payload, 3, out var p)
            && int.TryParse(p[0], out transporterId) && int.TryParse(p[1], out thingId)
            && int.TryParse(p[2], out count) && count > 0;
    }

    public static bool TryDecodeForbid(byte[] payload, out int thingId, out bool forbidden)
    {
        thingId = 0;
        forbidden = false;
        if (!TryDecode(payload, 2, out var p) || !int.TryParse(p[0], out thingId) || !int.TryParse(p[1], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        forbidden = flag == 1;
        return true;
    }

    public static bool TryDecodePriority(byte[] payload, out int pawnId, out string workDef, out int priority)
    {
        pawnId = priority = 0;
        workDef = "";
        if (!TryDecode(payload, 3, out var p) || !int.TryParse(p[0], out pawnId) || !int.TryParse(p[2], out priority)) return false;
        workDef = p[1];
        return workDef.Length > 0 && priority is >= 0 and <= 4;
    }

    public static bool TryDecodeZoneCell(byte[] payload, out string zoneType, out int zoneId, out int map, out int x, out int z, out bool add)
    {
        zoneType = "";
        zoneId = map = x = z = 0;
        add = false;
        if (!TryDecode(payload, 6, out var p) || !int.TryParse(p[1], out zoneId) || !int.TryParse(p[2], out map)
            || !int.TryParse(p[3], out x) || !int.TryParse(p[4], out z) || !int.TryParse(p[5], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        zoneType = p[0];
        add = flag == 1;
        return true;
    }

    public static bool TryDecodeZoneDelete(byte[] payload, out int map, out int zoneId)
    {
        map = zoneId = 0;
        return TryDecode(payload, 2, out var p) && int.TryParse(p[0], out map) && int.TryParse(p[1], out zoneId);
    }

    public static bool TryDecodeBillAdd(byte[] payload, out int buildingId, out string recipeDef, out int repeatCount)
    {
        buildingId = repeatCount = 0;
        recipeDef = "";
        if (!TryDecode(payload, 3, out var p) || !int.TryParse(p[0], out buildingId) || !int.TryParse(p[2], out repeatCount)) return false;
        recipeDef = p[1];
        return recipeDef.Length > 0;
    }

    public static bool TryDecodeBillDelete(byte[] payload, out int buildingId, out int billIndex)
    {
        buildingId = billIndex = 0;
        return TryDecode(payload, 2, out var p) && int.TryParse(p[0], out buildingId) && int.TryParse(p[1], out billIndex) && billIndex >= 0;
    }

    public static bool TryDecodeAreaAssign(byte[] payload, out int pawnId, out int areaId)
    {
        pawnId = areaId = 0;
        return TryDecode(payload, 2, out var p) && int.TryParse(p[0], out pawnId) && int.TryParse(p[1], out areaId);
    }

    private static bool TryDecode(byte[] payload, int fields, out string[] parts)
    {
        parts = [];
        if (payload is null || payload.Length == 0 || payload.Length > 4096) return false;
        var text = Encoding.UTF8.GetString(payload);
        parts = text.Split('|');
        return parts.Length == fields;
    }
}
