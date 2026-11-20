using System.Globalization;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Remembers the last tick each pawn ran a main think pass, so a wander-parity divergence names a tick and not
/// only a pawn.
///
/// <c>JobGiver_Wander.TryGiveJob</c> flips <c>mindState.nextMoveOrderIsWait</c> on every pass and then chooses
/// between "wait" (no RNG draw) and "walk somewhere" (three draws) from it
/// (<c>Verse.AI/JobGiver_Wander.cs:48-58</c>). So one extra or one missing pass permanently inverts the parity
/// and, three draws later, the whole stream. Measured at tick 10380: parity 0 against 1, draw counts 271251
/// against 271254 - exactly the three draws of the walk branch, which makes the draws a symptom rather than the
/// cause.
///
/// The pass itself is <c>Pawn_JobTracker.DetermineNextJob</c>, reached from
/// <c>TryFindAndStartJob</c> (<c>Verse.AI/Pawn_JobTracker.cs:601-620</c>) and from
/// <c>JobTrackerTickInterval</c> for the constant tree. Recording the tick is enough: two peers that agree on
/// "when did this pawn last think" cannot disagree about the parity that pass produced.
/// </summary>
internal static class ThinkPassAudit
{
    private static readonly Dictionary<int, int> LastPass = new();

    /// <summary>Which think node produced the job the pawn is now running.
    ///
    /// The job name alone does not say where it came from: `Ingest` can come from the food giver or from a
    /// drafted order, and one peer choosing `Ingest` while the other chooses a work job is a difference in the
    /// <em>giver</em>, not in the job. Recording the node makes that distinction readable instead of inferred.</summary>
    private static readonly Dictionary<int, string> LastSource = new();

    internal static void Reset()
    {
        lock (LastPass)
        {
            LastPass.Clear();
            LastSource.Clear();
        }
    }

    internal static void Record(Pawn pawn, Verse.AI.ThinkNode? source)
    {
        if (pawn is null) return;
        lock (LastPass)
        {
            LastPass[pawn.thingIDNumber] = GenTicks.TicksGame;
            var name = source is null ? "-" : source.GetType().Name;
            if (name.StartsWith("JobGiver_", StringComparison.Ordinal)) name = name["JobGiver_".Length..];
            LastSource[pawn.thingIDNumber] = name;
        }
    }

    /// <summary>The giver that produced the pawn's current job, or "-" when it has not thought since the load.</summary>
    internal static string Source(int pawnId)
    {
        lock (LastPass) return LastSource.TryGetValue(pawnId, out var name) ? name : "-";
    }

    /// <summary>The tick of each pawn's last main think pass, or -1 when it has not thought since the load.</summary>
    internal static string Describe(int pawnId)
    {
        lock (LastPass) return LastPass.TryGetValue(pawnId, out var tick)
            ? tick.ToString(CultureInfo.InvariantCulture)
            : "-1";
    }
}

[HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
internal static class ThinkPassPatch
{
    // Pawn_JobTracker.pawn is protected, so the field is read through Harmony's accessor cache.
    private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

    private static void Postfix(Pawn_JobTracker __instance, Verse.AI.ThinkResult __result) =>
        ThinkPassAudit.Record(Owner(__instance), __result.SourceNode);
}



