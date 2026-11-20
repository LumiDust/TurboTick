using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Records what a haul into a construction frame actually deposited, on whichever peer is authoritative.
///
/// <para>Why: the player reported colonists walking a stack onto a wall frame, the "3 wood" never going down, and the
/// same stack being picked up and carried again - a loop. Two very different defects produce that picture and they
/// cannot be told apart from the frame log:</para>
/// <list type="number">
/// <item>the deposit itself returns 0, so the authority never gains the material, and the loop is the authority's;</item>
/// <item>the deposit succeeds on the authority while the replica keeps drawing an empty container, so the loop is
/// only on screen.</item>
/// </list>
///
/// <para><c>Toils_Haul.DepositHauledThingInContainer</c> already contains the answer - it computes
/// <c>num = Mathf.Min(haulEnroute.GetSpaceRemainingWithEnroute(def, actor), carried)</c> and then
/// <c>num2 = innerContainer.TryTransferToContainer(carriedThing, thingOwner, num)</c>. A zero there with
/// <c>num</c> also zero means the frame reports no space (a stale enroute promise, or a full container); a zero
/// <c>num2</c> with a positive <c>num</c> means the owner refused the thing it was asked to accept. Vanilla logs
/// nothing for either - the only branch it reports is the "no ThingOwner at all" one.</para>
/// </summary>
[HarmonyPatch(typeof(Toils_Haul), nameof(Toils_Haul.DepositHauledThingInContainer))]
internal static class ConstructionDepositProbePatch
{
    private const string LogTag = "[TurboTick 建造]";

    private static void Postfix(Toil __result, TargetIndex containerInd)
    {
        // Postfix, not Prefix: the toil does not exist until the original body returns, and reading `__result` in a
        // prefix yields null - which is exactly how this probe first reported nothing at all while colonists were
        // hauling. The toil is built before it ever runs, so the interesting moment is its initAction.
        // `AddPreInitAction` runs ahead of the original body, so the before-picture is taken there.
        var toil = __result;
        if (toil is null) return;
        toil.AddPreInitAction(() => Snapshot(toil, containerInd, "投递前"));
        toil.AddFinishAction(() => Snapshot(toil, containerInd, "投递后"));
    }

    private static void Snapshot(Toil toil, TargetIndex containerInd, string when)
    {
        var actor = toil?.actor;
        if (actor is null) return;
        try
        {
            var job = actor.jobs?.curJob;
            var container = job?.GetTarget(containerInd).Thing;
            if (container is not Frame && container is not Blueprint) return;
            if (container is not IHaulEnroute enroute) return;
            var carried = actor.carryTracker?.CarriedThing;
            var owner = container.TryGetInnerInteractableThingOwner();
            var def = carried?.def;
            var containerCount = def is null ? -1 : FrameStock(container, def);
            var space = def is null ? -1 : enroute.SpaceRemainingFor(def);
            var withEnroute = def is null ? -1 : EnrouteUtility.GetSpaceRemainingWithEnroute(enroute, def, null);
            var canAccept = owner is null || carried is null ? -1 : owner.GetCountCanAccept(carried);
            Log.Message($"{LogTag} {when} {actor.LabelShort} -> {container.def.defName}#{container.thingIDNumber}"
                + $"：携带 {carried?.stackCount.ToString() ?? "无"} 个 {def?.defName ?? "?"}"
                + $"，容器内 {containerCount}，SpaceRemainingFor={space}，含途中={withEnroute}"
                + $"，GetCountCanAccept={canAccept}"
                + $"，job={job?.def.defName ?? "?"}");
        }
        catch (Exception error)
        {
            Log.Warning($"{LogTag} 投递埋点失败：{error.GetType().Name} {error.Message}");
        }
    }

    /// <summary>How many of <paramref name="def"/> the frame's container holds. Frames and blueprints both store it in
    /// <c>resourceContainer</c>, reached through the generic interface so one path covers both.</summary>
    private static int FrameStock(Thing thing, ThingDef def)
    {
        var owner = thing.TryGetInnerInteractableThingOwner();
        if (owner is null) return -1;
        var total = 0;
        for (var i = 0; i < owner.Count; i++)
        {
            var held = owner[i];
            if (held?.def == def) total += held.stackCount;
        }
        return total;
    }
}
