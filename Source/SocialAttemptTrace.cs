using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Breaks a social interaction attempt into the numbers that explain how many values it draws, by measuring the
/// attempt rather than re-deriving it.
///
/// The chain was narrowed from the outside in: <c>draw Human 91 23</c> against <c>18</c> with an identical pawn
/// projection, then <c>draw social 91 21</c> against <c>16</c>, with both <c>pf:</c> and <c>pfc:</c> equal - so
/// neither faction membership nor the <c>MapPawns</c> cache is responsible. What remains is inside
/// <c>Pawn_InteractionsTracker.TryInteractRandomly</c> (<c>RimWorld/Pawn_InteractionsTracker.cs:300+</c>).
///
/// An earlier version recomputed the filter predicates instead of observing them, which risks reporting a
/// difference that the game itself never saw. This one counts what actually happened: the size of the list the
/// shuffle ran over, how many times the game asked <see cref="Pawn_InteractionsTracker.CanInteractNowWith"/>, how
/// many times it asked <see cref="SocialInteractionUtility.CanReceiveRandomInteraction"/>, and the draws the whole
/// attempt consumed.
/// </summary>
internal static class SocialAttemptTrace
{
    private const int Capacity = 96;

    private static readonly List<string> Recent = new(Capacity);

    private static int listCount;
    private static int canInteractCalls;
    private static int canReceiveCalls;
    private static int pawnId;
    private static uint drawsBefore;

    internal static bool Tracking { get; private set; }

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    internal static void Begin(Pawn pawn, int list)
    {
        listCount = list;
        canInteractCalls = 0;
        canReceiveCalls = 0;
        recorded = 0;
        pawnId = pawn.thingIDNumber;
        drawsBefore = SimulationRandom.Iterations();
        Tracking = true;
    }

    private static int recorded;

    /// <summary>Records the first few decisions verbatim, so a difference names the pair and the interaction def
    /// rather than only a call count. `CanInteractNowWith` is the filter that decided whether the weighted pick had
    /// to resample - measured at tick 169: 32 calls on one peer against 26 on the other for the same pawn and the
    /// same candidate list.</summary>
    internal static void NoteCanInteract(Pawn? initiator, Pawn? recipient, InteractionDef? def, bool result)
    {
        if (!Tracking) return;
        canInteractCalls++;
        if (recorded >= 8) return;
        recorded++;
        lock (Recent)
        {
            Recent.Add($"cani {initiator?.thingIDNumber ?? -1} {recipient?.thingIDNumber ?? -1} "
                + $"{(def?.defName ?? "-")} {(result ? 1 : 0)}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static void NoteCanReceive() { if (Tracking) canReceiveCalls++; }

    internal static void End()
    {
        if (!Tracking) return;
        Tracking = false;
        // Self-check: the filter chain is `p == pawn || !CanInteractNowWith(p) || !CanReceiveRandomInteraction(p)
        // || HostileTo(p)`, so reaching the receive predicate without passing the interact predicate is
        // impossible. If that shows up, the counters are wrong and must not be used to reason about the game.
        if (canReceiveCalls > 0 && canInteractCalls == 0)
        {
            lock (Recent)
            {
                Recent.Add($"TraceSelfCheckFailed k=0 r={canReceiveCalls} pawn={pawnId}");
                while (Recent.Count > Capacity) Recent.RemoveAt(0);
            }
        }
        var draws = SimulationRandom.Iterations() - drawsBefore;
        lock (Recent)
        {
            Recent.Add($"socialn {pawnId} {listCount}");
            Recent.Add($"socialk {pawnId} {canInteractCalls}");
            Recent.Add($"socialr {pawnId} {canReceiveCalls}");
            Recent.Add($"socialt {pawnId} {draws}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

[HarmonyPatch(typeof(Pawn_InteractionsTracker), "TryInteractRandomly")]
internal static class SocialAttemptPatch
{
    private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");

    private static void Prefix(Pawn_InteractionsTracker __instance)
    {
        if (!ThingDrawTrace.Enabled) return;
        try
        {
            var pawn = Owner(__instance);
            if (pawn?.Map is null || pawn.Faction is null) return;
            SocialAttemptTrace.Begin(pawn, pawn.Map.mapPawns.SpawnedPawnsInFaction(pawn.Faction).Count);
        }
        catch (Exception) { }
    }

    private static void Postfix() => SocialAttemptTrace.End();
}

[HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.CanInteractNowWith))]
internal static class SocialCanInteractPatch
{
    // Parameter names must match the target exactly: `CanInteractNowWith(Pawn recipient, InteractionDef
    // interactionDef = null)` (Pawn_InteractionsTracker.cs:152). An earlier version named the first one
    // `otherPawn`, Harmony could not bind it, and the counter silently stayed at zero - which then produced a
    // "socialk=0 while socialr=1" reading that is impossible under the short-circuit filter chain.
    private static void Postfix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef interactionDef, bool __result)
    {
        if (!ThingDrawTrace.Enabled) return;
        SocialAttemptTrace.NoteCanInteract(Owner(__instance), recipient, interactionDef, __result);
    }

    private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> Owner =
        AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");
}

[HarmonyPatch(typeof(SocialInteractionUtility), nameof(SocialInteractionUtility.CanReceiveRandomInteraction))]
internal static class SocialCanReceivePatch
{
    private static void Prefix() => SocialAttemptTrace.NoteCanReceive();
}



