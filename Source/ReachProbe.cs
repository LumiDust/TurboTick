using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// A one-variable experiment: clear the reachability cache every tick and see whether the divergences go away.
///
/// Five measured divergences, three of them on paths that call <c>CanReach</c>:
/// <code>
/// tick 29003  food:res - with reach=False on one peer and True on the other, reservation and forbidden equal
/// tick 71030  GetFood succeeded on one peer, failed on the other, identical hunger
/// tick 73571  a WanderHerd target differed by two cells with every random value identical
/// </code>
/// <c>FoodUtility.BestFoodSourceOnMap</c> contains no randomness of its own, so a different answer with equal
/// inputs points at the reachability answer itself. That cache is invalidated by dirty-region bookkeeping, which is
/// a plausible place for peers to drift apart by a tick.
///
/// This is deliberately an experiment rather than a fix. Clearing the cache is always safe - it only forces a
/// recomputation - so the only expected cost is performance. If the divergence rate drops while it is on, the stale
/// cache hypothesis is supported and the real fix is to find which side misses an invalidation. If it does not, the
/// hypothesis is dead and the difference is in the region graph itself, which is a much more serious finding.
///
/// Off unless <c>TURBOTICK_REACH_CLEAR_EVERY=1</c>.
/// </summary>
internal static class ReachProbe
{
    internal static readonly bool ClearEveryTick =
        Environment.GetEnvironmentVariable("TURBOTICK_REACH_CLEAR_EVERY") == "1";

    internal static void ClearAll()
    {
        var maps = Find.Maps;
        for (var i = 0; i < maps.Count; i++)
        {
            try { maps[i]?.reachability?.ClearCache(); }
            catch (Exception) { }
        }
    }

    /// <summary>Region graph size and dirty-region count, to tell "different graph" from "different timing".</summary>
    internal static string Describe(Map map)
    {
        try
        {
            // DirectGrid is read directly: AllRegions rebuilds dirty regions as a side effect, which would make
            // the instrument change the thing it is measuring.
            var count = 0;
            var grid = map.regionGrid?.DirectGrid;
            if (grid is not null) for (var i = 0; i < grid.Length; i++) if (grid[i] is not null) count++;
            var regions = grid is null ? -1 : count;
            var dirty = map.regionDirtyer?.DirtyCells?.Count ?? -1;
            return $"rg:{map.Index}:regions={regions}:dirty={dirty}";
        }
        catch (Exception) { return $"rg:{map.Index}:?"; }
    }
}


