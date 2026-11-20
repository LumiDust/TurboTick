using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Stops <c>Zone.Cells</c> from shuffling inside a shared world.
///
/// Vanilla shuffles the cell list on first read and remembers that it did
/// (<c>_decomp/acs/Verse/Zone.cs:98-109</c>). Two things go wrong with that here:
/// the shuffle draws from the engine's own <c>Rand</c> (<c>GenList.Shuffle</c> → <c>Rand.RangeInclusive</c>,
/// <c>Verse/GenList.cs:94</c>), and <c>cellsShuffled</c> is not serialised - so <em>when</em> a peer shuffles
/// depends on who first draws the zone (the GUI draws selection brackets, a headless peer only reads it from
/// AI code). After that the two peers hold the same cells in different orders.
///
/// Cell order matters: <c>Zone.Position</c> is <c>cells[0]</c>, <c>CheckContiguous</c> floods from
/// <c>cells[0]</c>, and stockpile slot selection walks the list. Nothing in that needs a random order - it
/// needs a <em>shared</em> order - and the insertion order produced by <c>AddCell</c> is already identical on
/// every peer because the zone-cell command is applied in the same sequence everywhere.
///
/// So in a shared world the getter returns the list as stored and marks it as "already shuffled" so vanilla
/// never shuffles it later either.
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.Cells), MethodType.Getter)]
internal static class SharedZoneCellOrderPatch
{
    private static readonly AccessTools.FieldRef<Zone, bool> Shuffled =
        AccessTools.FieldRefAccess<Zone, bool>("cellsShuffled");

    private static bool Prefix(Zone __instance, ref List<IntVec3> __result)
    {
        // Outside a shared world the vanilla shuffle is harmless and may be what another mod expects.
        if (!SharedColony.HasSharedWorld || __instance is null) return true;
        try
        {
            __result = __instance.cells;
            // Without this the vanilla getter would shuffle the next time something reads it.
            Shuffled(__instance) = true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 区域格子顺序补丁失败，回退原版行为：" + error.Message);
            return true;
        }
        return false;
    }
}
