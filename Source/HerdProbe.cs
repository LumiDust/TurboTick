using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace TurboTick;

/// <summary>
/// Records the herd root a herd-wander decision was built from, and the lord that owns the pawn.
///
/// The fourth long run diverged with **identical** `rng`, `draws`, `iter` and identical per-subsystem counts:
/// one pawn's wander target was `(77,89)` on one peer and `(77,87)` on the other, while its position, job,
/// parity, last think tick, hunger and giver (`WanderHerd`) all matched. Same random values, different result -
/// which means the difference is in an <em>input</em>, not in the random stream. For a herd wander that input is
/// the centre: <c>JobGiver_WanderHerd.GetWanderRoot</c> returns
/// <c>WanderUtility.GetHerdWanderRoot</c>, so the centre decides the destination.
///
/// That method is also a determinism hazard worth watching on its own:
/// <code>
/// WanderUtility.cs:92-105
/// Predicate&lt;Thing&gt; validator = t =&gt; { ... return !(Rand.Value &lt; 0.5f) &amp;&amp; isHerdValidator(t); };
/// GenClosest.ClosestThingReachable(..., validator, null, 13)
/// </code>
/// A random value inside a search predicate couples the number of draws to how many candidates the search visits.
/// That is fine while the search visits the same candidates in the same order, and it is the reason this is worth
/// recording rather than assuming: identical totals can still be reached along different candidate sequences.
/// </summary>
internal static class HerdProbe
{
    private static readonly Dictionary<int, string> Roots = new();

    internal static void Reset()
    {
        lock (Roots) Roots.Clear();
    }

    internal static void Record(Pawn pawn, IntVec3 root)
    {
        if (pawn is null) return;
        lock (Roots)
            Roots[pawn.thingIDNumber] = root.IsValid
                ? root.x.ToString(CultureInfo.InvariantCulture) + "," + root.z.ToString(CultureInfo.InvariantCulture)
                : "-";
    }

    /// <summary>The herd centre the pawn's wander root resolved to, or "-" when it has not asked for one.</summary>
    internal static string Root(int pawnId)
    {
        lock (Roots) return Roots.TryGetValue(pawnId, out var text) ? text : "-";
    }

    /// <summary>The lord that owns the pawn, as "jobType:ownedPawns:toil". Group state is not part of any other
    /// projection, and a herd wander depends on it.</summary>
    internal static string Lord(Pawn pawn)
    {
        try
        {
            var lord = pawn?.GetLord();
            if (lord is null) return "-";
            var toil = lord.CurLordToil?.GetType().Name;
            if (toil is not null && toil.StartsWith("LordToil_", StringComparison.Ordinal))
                toil = toil["LordToil_".Length..];
            return (lord.LordJob?.GetType().Name ?? "?") + ":"
                + lord.ownedPawns.Count.ToString(CultureInfo.InvariantCulture) + ":"
                + (toil ?? "-");
        }
        catch (Exception) { return "?"; }
    }
}

[HarmonyPatch(typeof(WanderUtility), nameof(WanderUtility.GetHerdWanderRoot))]
internal static class HerdRootPatch
{
    private static void Postfix(Pawn pawn, IntVec3 __result) => HerdProbe.Record(pawn, __result);
}

