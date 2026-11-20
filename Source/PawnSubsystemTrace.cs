using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Attributes a pawn's shared-random draws to the subsystem that made them.
///
/// The per-thing trace narrowed the remaining divergence to one human: at tick 4846 pawn 288 drew 19 values on one
/// peer and 17 on the other, while its projection - position, job, parity, last think tick, target, expiry - was
/// byte-identical. A pawn whose visible state agrees cannot be taking a different decision; it can only roll dice
/// more often inside a subsystem whose state is not in the projection.
///
/// <c>Pawn.TickInterval</c> (<c>Verse/Pawn.cs:1626-1737</c>) calls each subsystem in a fixed order, so wrapping them
/// names the culprit directly: <c>draw mind 288 3</c> says the mind state rolled three times on this peer and fewer
/// on the other. Off unless <c>TURBOTICK_TRACE_THING_DRAWS=1</c>, because this costs two counter reads per subsystem
/// per pawn per tick.
/// </summary>
internal static class PawnSubsystemTrace
{
    private const int Capacity = 128;

    private static readonly List<string> Recent = new(Capacity);

    private static uint before;
    private static uint saved;

    internal static bool On => ThingDrawTrace.Enabled;

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    internal static void Begin()
    {
        saved = before;
        before = SimulationRandom.Iterations();
    }

    internal static void End(string subsystem, Pawn? pawn)
    {
        var after = SimulationRandom.Iterations();
        var drew = after - before;
        before = saved;
        if (pawn is null || drew == 0) return;
        lock (Recent)
        {
            Recent.Add(string.Join(" ",
                "draw",
                subsystem,
                pawn.thingIDNumber.ToString(CultureInfo.InvariantCulture),
                drew.ToString(CultureInfo.InvariantCulture)));
            if (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTickInterval))]
internal static class PawnJobsDrawPatch
{
    private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

    private static void Prefix() { if (PawnSubsystemTrace.On) PawnSubsystemTrace.Begin(); }

    private static void Postfix(Pawn_JobTracker __instance)
    {
        if (PawnSubsystemTrace.On) PawnSubsystemTrace.End("jobs", Owner(__instance));
    }
}

[HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTickInterval))]
internal static class PawnHealthDrawPatch
{
    private static readonly AccessTools.FieldRef<Pawn_HealthTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_HealthTracker, Pawn>("pawn");

    private static void Prefix() { if (PawnSubsystemTrace.On) PawnSubsystemTrace.Begin(); }

    private static void Postfix(Pawn_HealthTracker __instance)
    {
        if (PawnSubsystemTrace.On) PawnSubsystemTrace.End("health", Owner(__instance));
    }
}

[HarmonyPatch(typeof(Pawn_MindState), nameof(Pawn_MindState.MindStateTickInterval))]
internal static class PawnMindDrawPatch
{
    private static readonly AccessTools.FieldRef<Pawn_MindState, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_MindState, Pawn>("pawn");

    private static void Prefix() { if (PawnSubsystemTrace.On) PawnSubsystemTrace.Begin(); }

    private static void Postfix(Pawn_MindState __instance)
    {
        if (PawnSubsystemTrace.On) PawnSubsystemTrace.End("mind", Owner(__instance));
    }
}

[HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval))]
internal static class PawnNeedsDrawPatch
{
    private static readonly AccessTools.FieldRef<Pawn_NeedsTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_NeedsTracker, Pawn>("pawn");

    private static void Prefix() { if (PawnSubsystemTrace.On) PawnSubsystemTrace.Begin(); }

    private static void Postfix(Pawn_NeedsTracker __instance)
    {
        if (PawnSubsystemTrace.On) PawnSubsystemTrace.End("needs", Owner(__instance));
    }
}

[HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.InteractionsTrackerTickInterval))]
internal static class PawnSocialDrawPatch
{
    private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");

    private static void Prefix() { if (PawnSubsystemTrace.On) PawnSubsystemTrace.Begin(); }

    private static void Postfix(Pawn_InteractionsTracker __instance)
    {
        if (PawnSubsystemTrace.On) PawnSubsystemTrace.End("social", Owner(__instance));
    }
}

[HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.AgeTickInterval))]
internal static class PawnAgeDrawPatch
{
    private static readonly AccessTools.FieldRef<Pawn_AgeTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_AgeTracker, Pawn>("pawn");

    private static void Prefix() { if (PawnSubsystemTrace.On) PawnSubsystemTrace.Begin(); }

    private static void Postfix(Pawn_AgeTracker __instance)
    {
        if (PawnSubsystemTrace.On) PawnSubsystemTrace.End("age", Owner(__instance));
    }
}
