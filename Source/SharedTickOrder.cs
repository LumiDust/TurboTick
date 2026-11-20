using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Reports the order in which things will tick, and stabilises the one part of that order the game leaves to
/// object identity. It does not reorder anything else.
///
/// <c>TickList.Tick</c> iterates <c>thingLists[TicksGame % TickInterval]</c> (<c>Verse/TickList.cs:90-113</c>).
/// <c>TickerType.Normal</c> has interval 1, so every normally ticking thing shares one bucket whose order is the
/// order things were registered - which is why two peers with identical <c>rng</c>, identical <c>draws</c> and the
/// same total draws could still hand two pawns each other's <c>expiryInterval</c> (222/149 against 150/197).
///
/// An earlier revision forced id order inside every bucket. The next run diverged <em>sooner and wider</em>
/// (3480 against 7080, 22 differing entities against 1), so that was reverted: a change that worsens the symptom
/// is not evidence for the theory. What remains is the observation, plus the one defect the source makes
/// unambiguous.
///
/// That defect: <c>BucketOf</c> computes <c>t.GetHashCode() % TickInterval</c>, and <c>Thing.GetHashCode</c>
/// returns <c>base.GetHashCode()</c> - a per-process object identity - when <c>thingIDNumber</c> is -1
/// (<c>Verse/Thing.cs:1912</c>). Things registered before an id is assigned therefore land in <em>different
/// buckets on different peers</em>, which changes which things tick together. Making that one case deterministic
/// is strictly better than leaving it to the runtime: it cannot reorder anything that was already stable, and it
/// removes a genuine source of divergence.
/// </summary>
internal static class SharedTickOrder
{
    private static readonly AccessTools.FieldRef<TickList, List<List<Thing>>> Buckets =
        AccessTools.FieldRefAccess<TickList, List<List<Thing>>>("thingLists");
    private static readonly AccessTools.FieldRef<TickList, List<Thing>> Pending =
        AccessTools.FieldRefAccess<TickList, List<Thing>>("thingsToRegister");
    private static readonly AccessTools.FieldRef<TickList, TickerType> Kind =
        AccessTools.FieldRefAccess<TickList, TickerType>("tickType");

    internal static int Interval(TickList list) => Kind(list) switch
    {
        TickerType.Normal => 1,
        TickerType.Rare => 250,
        TickerType.Long => 2000,
        _ => -1
    };

    /// <summary>The bucket an id-less thing goes to. The game would use its object identity here, which differs
    /// between processes; 0 is the only bucket that exists for every tick type.</summary>
    internal static List<Thing>? FirstBucket(TickList list)
    {
        try
        {
            var buckets = Buckets(list);
            return buckets.Count > 0 ? buckets[0] : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The ids that will tick next, in game order, plus the ones still queued. Hashed as its own
    /// category: identical content in a different order is precisely the failure this is looking for, and a
    /// content hash cannot see it.</summary>
    internal static List<string> Describe()
    {
        var entries = new List<string>();
        var manager = Find.TickManager;
        if (manager is null) return entries;
        foreach (var name in new[] { "tickListNormal", "tickListRare", "tickListLong" })
        {
            TickList? list = null;
            try { list = AccessTools.Field(typeof(TickManager), name)?.GetValue(manager) as TickList; }
            catch (Exception) { }
            if (list is null) continue;
            try
            {
                var interval = Interval(list);
                if (interval <= 0) continue;
                var buckets = Buckets(list);
                var index = manager.TicksGame % interval;
                if (index < 0 || index >= buckets.Count) continue;
                var bucket = buckets[index];
                var order = new System.Text.StringBuilder();
                for (var i = 0; i < bucket.Count; i++) order.Append(bucket[i].thingIDNumber).Append(',');
                entries.Add($"ord:{name}:{index}:{bucket.Count}:{order}");
                var queued = new System.Text.StringBuilder();
                var pending = Pending(list);
                for (var i = 0; i < pending.Count; i++) queued.Append(pending[i]?.thingIDNumber ?? -2).Append(',');
                entries.Add($"ordq:{name}:{pending.Count}:{queued}");
            }
            catch (Exception error)
            {
                entries.Add($"ord:{name}:error:{error.GetType().Name}");
            }
        }
        return entries;
    }
}

/// <summary>
/// Sends a thing that has no id yet to a fixed bucket instead of one derived from its object identity.
///
/// <c>BucketOf</c> is private and returns a list, so the index is computed here and the result supplied directly.
/// Only the <c>thingIDNumber == -1</c> case is intercepted; everything else runs the game's own code.
/// </summary>
[HarmonyPatch(typeof(TickList), "BucketOf")]
internal static class SharedTickBucketPatch
{
    private static bool Prefix(TickList __instance, Thing t, ref List<Thing>? __result)
    {
        if (!SharedColony.HasSharedWorld) return true;
        if (t is null || t.thingIDNumber != -1) return true;
        var bucket = SharedTickOrder.FirstBucket(__instance);
        if (bucket is null) return true;
        __result = bucket;
        return false;
    }
}
