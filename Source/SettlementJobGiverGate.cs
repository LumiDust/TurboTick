using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Keeps a pawn from being <i>issued</i> a job whose target we already present as forbidden to them.
///
/// <para><b>The family this belongs to.</b> Every gate so far answers a question the engine asks <i>before</i> a job
/// exists (`WorkGiver_Scanner.HasJobOnThing`/`HasJobOnCell`, `CanReserve`, the designators) or after one is chosen but
/// before it starts (`Pawn_JobTracker.StartJob`, which we do not patch). Think nodes - eat, sleep, wear, equip, haul a
/// specific stack - build a `Job` and hand it to the tracker; the reservation gate refuses it one step later, so the pawn
/// walks over and gives up. That is the shape round 135 named and round 155 fixed for cells. ColonyTogether closes it with
/// `SettlementJobGiverPatch` (ct:352918) on `ThinkNode_JobGiver.TryIssueJobPackage`, plus `SettlementJobPatch`
/// (ct:352953) and `SettlementOrderedJobPatch` (ct:352904) on the tracker.</para>
///
/// <para><b>Why the predicate is the visibility reader and not a job→act table.</b> A `JobDef` names a verb
/// (`Eat`, `Sleep`, `Wear`, `Equip`, `HaulToCell`, a hundred more, and mods add their own) and mapping each to a
/// `(act, kind)` pair is a second copy of the rule table that would drift exactly like the one round 155 deleted. The
/// question that actually matters here is narrower: "is this target something we already tell this pawn is forbidden" -
/// and that is `SettlementForbidVisibility.ShouldHide`, the same answer vanilla's own AI consults. One source of truth:
/// a thing the guest may take is never refused, and a thing the reader hides is never worth issuing a job for.</para>
///
/// <para><b>Cell targets are left alone.</b> `targetA.HasThing == false` means the job is about a position, which
/// `SettlementWorkCellGate`/`CanReserve` already decide; refusing here would also swallow vanilla's own "no job" answer
/// for unreachable cells, which is not this layer's business.</para></summary>
[HarmonyPatch(typeof(ThinkNode_JobGiver), nameof(ThinkNode_JobGiver.TryIssueJobPackage))]
internal static class SettlementJobGiverGate
{
    internal static long Refused { get; private set; }

    internal static void Reset() => Refused = 0;

    /// <summary>Whether this job should not be issued at all.</summary>
    internal static bool Refuse(Pawn? pawn, Job? job)
    {
        if (pawn is null || job is null) return false;
        if (!job.targetA.HasThing) return false;
        return SettlementForbidVisibility.ShouldHide(job.targetA.Thing, pawn, out _);
    }

    private static void Postfix(Pawn pawn, ref ThinkResult __result)
    {
        try
        {
            if (!Refuse(pawn, __result.Job)) return;
            Refused++;
            __result = ThinkResult.NoJob;
        }
        catch (Exception error)
        {
            // A failed job gate must leave the chosen job alone: vanilla already knows how to abandon a job it cannot
            // reserve, and swallowing the job here would turn a permission bug into "this pawn does nothing".
            Log.Warning("[TurboTick] 工作 giver 闸门失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static string Summary() => $"拦下 {Refused}";
}
