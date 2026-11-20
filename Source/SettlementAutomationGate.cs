using System;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Keeps the colony's automation inside its own territory: auto-rebuild and auto-roofing, which have no actor
/// pawn to gate.
///
/// <para><b>Why a separate gate.</b> Every other ownership decision is a pawn asking about a thing, so
/// `SettlementGates.Can` answers it. `ThingUtility.CheckAutoRebuildOnDestroyed` (`:160060-160068`) and
/// `CheckAutoRebuildTerrainOnDestroyed` (`:160069-160075`) are not that: they run inside `Destroy` with no pawn, and they
/// place blueprints on the colony's behalf. `AutoBuildRoofAreaSetter.TryGenerateAreaFor` (`:91537`) is the same shape.
/// ColonyTogether gates exactly these three with an ownership scope (`SettlementAutoRebuild` at ct:220787,
/// `SettlementAutoRebuildFloor` at ct:220817, `SettlementAutoRoof` at ct:220912) - the difference is only that it needs a
/// scope object because its authority holds per-player settlement data, while ours reads the table directly.</para>
///
/// <para><b>What "outside" means here.</b> A cell nobody claimed is fine: that is today's behaviour and the layer
/// short-circuits on <see cref="SettlementData.Active"/> anyway. A cell inside a settlement this process does not own is
/// refused - the automation would otherwise spend the colony's materials repairing somebody else's wall, and the owner
/// would see their base rebuilt by a decision they never made. This is the same rule
/// <see cref="SettlementGates"/> applies to a pawn's build order, just without a pawn.</para>
///
/// <para><b>Roofing is decided per room.</b> `AutoBuildRoofAreaSetter` works on a `Room`, and a room can straddle two
/// settlements. It is allowed only when every cell that has a settlement belongs to this process: a room half in a
/// neighbour's base must not be roofed, because the roof would appear over their floor.</para>
/// </summary>
internal static class SettlementAutomationGate
{
    /// <summary>Calls answered by this gate, and refusals, so the census can separate "the automation never ran" from
    /// "it was refused everywhere".</summary>
    internal static long Calls { get; private set; }
    internal static long Refusals { get; private set; }

    /// <summary>Whether an autonomous build action may touch this cell.</summary>
    internal static bool Allowed(Map? map, IntVec3 cell)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null || !data.Active) return true;
            Calls++;
            if (map is null) return true;
            var home = data.SettlementAt(map.uniqueID, cell);
            if (home == 0) return true;
            if (data.Owns(SettlementGates.LocalPlayer, home)) return true;
            Refusals++;
            return false;
        }
        catch (Exception error)
        {
            // A gate that throws must not stop a build the player expects; the ownership layer is the extra condition,
            // not the reason a wall gets rebuilt.
            Log.Warning("[TurboTick] 自动重建闸门失败：" + error.GetType().Name + ": " + error.Message);
            return true;
        }
    }

    /// <summary>No label: the census row already prints one, and a summary that repeats it reads `自动重建：自动重建 …`.
    /// The project's own invariant (see the census-row test) refuses that shape.</summary>
    internal static string Summary() => $"问 {Calls} / 拒绝 {Refusals}";
}

/// <summary>Auto-rebuild of a destroyed building: refused outside this process's own settlement.</summary>
[HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildOnDestroyed))]
internal static class SettlementAutoRebuildGate
{
    private static bool Prefix(Thing thing, Map map) => SettlementAutomationGate.Allowed(map, thing.Position);
}

/// <summary>Auto-rebuild of destroyed terrain: the floor variant of the same rule.</summary>
[HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildTerrainOnDestroyed))]
internal static class SettlementAutoRebuildTerrainGate
{
    private static bool Prefix(IntVec3 pos, Map map) => SettlementAutomationGate.Allowed(map, pos);
}

/// <summary>Auto-roofing: refused when any claimed cell of the room belongs to somebody else.</summary>
[HarmonyPatch(typeof(AutoBuildRoofAreaSetter), nameof(AutoBuildRoofAreaSetter.TryGenerateAreaFor))]
internal static class SettlementAutoRoofGate
{
    private static bool Prefix(Room room)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null || !data.Active) return true;
            var map = room?.Map;
            if (map is null) return true;
            foreach (var cell in room.Cells)
                if (!SettlementAutomationGate.Allowed(map, cell)) return false;
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 自动铺顶闸门失败：" + error.GetType().Name + ": " + error.Message);
            return true;
        }
    }
}
