using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Ticks deferred spawn requests in a deterministic order.
///
/// <c>DeferredSpawner</c> (<c>RimWorld/DeferredSpawner.cs:14-28</c>) holds its work in a <c>HashSet&lt;SpawnRequest&gt;</c>
/// and ticks it by iterating that set:
/// <code>
/// private HashSet&lt;SpawnRequest&gt; requests = new HashSet&lt;SpawnRequest&gt;();
/// public void DeferredSpawnerTick() { requests.RemoveWhere(r =&gt; r.done); foreach (var r in requests) TickRequest(r); }
/// </code>
/// <c>SpawnRequest</c> does not override <c>GetHashCode</c>, so the set's iteration order follows object identity -
/// which differs between processes. Two requests in flight can therefore be ticked in the opposite order, and a
/// request whose batch is throttled by <c>intervalTicks</c> spawns its items a tick later than on the other peer.
///
/// That is the measured residual: on one tick five `MealSurvivalPack` stacks inside a 3x3 block were already
/// flagged forbidden on one peer and not on the other, with identical random stream. A drop pod's contents arrive
/// through this spawner, and <c>CompForbiddable</c> sets the flag as each stack spawns - so a one-tick difference in
/// spawn order produces exactly that reading.
///
/// This is the third instance of one family: a `HashSet` (or a stable sort over load order) whose iteration order
/// leaks into the simulation. The other two were <c>DefDatabase</c> load order and
/// <c>WorkTypeDef.workGiversByPriority</c>.
/// </summary>
internal static class DeferredSpawnOrder
{
    internal static int Reordered;

    internal static string Describe() => "requests=" + Reordered;
}

[HarmonyPatch(typeof(DeferredSpawner), nameof(DeferredSpawner.DeferredSpawnerTick))]
internal static class DeferredSpawnOrderPatch
{
    private static readonly AccessTools.FieldRef<DeferredSpawner, HashSet<SpawnRequest>> Requests =
        AccessTools.FieldRefAccess<DeferredSpawner, HashSet<SpawnRequest>>("requests");

    private static readonly System.Reflection.MethodInfo? TickRequest =
        AccessTools.Method(typeof(DeferredSpawner), "TickRequest");

    /// <summary>Runs the original loop with a stable order, and lets the original body do nothing.
    ///
    /// Re-implemented rather than wrapped because the order <em>is</em> the iteration: there is no seam to patch
    /// inside a <c>foreach</c>. The body is three lines, and everything that actually spawns stays in
    /// <c>TickRequest</c>, so the behaviour per request is unchanged - only the sequence of requests is fixed.
    /// A stable key exists: <c>startedTick</c>, then the first thing's id. Both are saved or derived, never identity.
    /// </summary>
    private static bool Prefix(DeferredSpawner __instance)
    {
        if (!SharedColony.HasSharedWorld) return true;
        if (TickRequest is null) return true;
        try
        {
            var requests = Requests(__instance);
            if (requests is null || requests.Count < 2) return true;
            requests.RemoveWhere(static r => r.done);
            var ordered = requests.ToList();
            ordered.Sort(static (a, b) =>
            {
                var byTick = a.startedTick.CompareTo(b.startedTick);
                if (byTick != 0) return byTick;
                return FirstThingId(a).CompareTo(FirstThingId(b));
            });
            DeferredSpawnOrder.Reordered = ordered.Count;
            foreach (var request in ordered) TickRequest.Invoke(__instance, [request]);
            return false;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 延迟生成排序失败，回退原逻辑：" + error.Message);
            return true;
        }
    }

    private static int FirstThingId(SpawnRequest request)
    {
        try
        {
            var list = request.thingsToSpawn;
            if (list is { Count: > 0 } && list[0] is not null) return list[0].thingIDNumber;
        }
        catch (Exception) { }
        return int.MaxValue;
    }
}

