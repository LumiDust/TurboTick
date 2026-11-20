using System;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Presents another settlement's things as forbidden to a pawn who may not take them, so vanilla AI avoids them
/// on its own.
///
/// <para><b>Why this is a reader and not another refusal.</b> `SettlementGates` refuses actions at the places the engine
/// asks "may this pawn do that" (`ReservationManager.CanReserve`, the designators, the scanners, the door). That covers
/// orders and work, and it leaves a gap the work gate's own probe showed in round 135: a hungry guest still <i>tries</i>
/// to eat from a neighbour's fridge, and every attempt is refused one at a time. `ForbidUtility.IsForbidden`
/// (`:538640-538671`) is the question vanilla asks <b>before</b> any of that - the job givers, the float menus and the
/// haul scanners all consult it - so answering it is one edit instead of a refusal per attempt. ColonyTogether reaches
/// the same conclusion with `SettlementForbidPatch` (ct:352751), a Postfix that sets `__result = true` when
/// `OwnershipContext.Allowed` says no.</para>
///
/// <para><b>When it stays silent.</b> `IsForbidden` already returns false without calling anything else when
/// `CaresAboutForbidden` is false (drafted, prisoner in a secure cell, ...), so the patch does not need that check. It
/// returns immediately when the layer is off, when the thing belongs to nobody, when the pawn is acting inside its own
/// settlement, or when the pawn holds any of the three grants that would let it touch the thing (`Take` to haul it,
/// `Use` to operate it, `Store` to put something away). Only then does it present the item as forbidden.</para>
///
/// <para><b>Why three grants and not one.</b> A grant table is per action: a guest allowed `Store` may put goods into the
/// stockpile and still not take them out. Marking an item forbidden for a pawn who holds one of those grants would make
/// the permission invisible - the player granted it, the gate allows it, and vanilla never offers the job.</para>
/// </summary>
internal static class SettlementForbidVisibility
{
    /// <summary>Things presented as forbidden to a pawn, and how many queries reached the ownership check. Both are
    /// needed: "nothing hidden" and "the patch never ran" read the same from one number.</summary>
    internal static long Hidden { get; private set; }
    internal static long Queries { get; private set; }

    /// <summary>Whether this pawn should see the thing as forbidden for ownership reasons.</summary>
    internal static bool ShouldHide(Thing? thing, Pawn? pawn, out int home)
    {
        home = 0;
        var data = SettlementGates.Data;
        if (data is null || !data.Active) return false;
        if (thing is null || pawn is null) return false;
        // **Contents count (round 157).** The first version required `thing.Spawned`, on the reasoning that only spawned
        // things live on a map. That excluded exactly the case worth protecting: a stack inside a casket or a pod is
        // `Spawned=false`, yet `MapHeld`/`PositionHeld` resolve to the container, so `HomeOf` already answers the
        // container's settlement (`SettlementRule.cs:193-199`) - the gate refused the guest's `Take` and this reader still
        // said "not forbidden", because of that guard.
        //
        // A thing carried by a **pawn** is the opposite case: `PositionHeld` follows the carrier, so judging it by
        // position would mark a visitor's own meal as the host's goods and hide it from the visitor. So the parent chain
        // is walked, and anything a pawn is holding keeps its carrier's context.
        for (var holder = thing.ParentHolder; holder is not null; holder = holder.ParentHolder)
            if (holder is Pawn) return false;
        home = SettlementRule.HomeOf(data, thing);
        if (home == 0) return false;
        var pawnHome = data.HomeOfPawn(pawn.thingIDNumber);
        if (pawnHome != 0 && pawnHome == home) return false;
        Queries++;
        // At war everything of theirs is fair to hide, whatever the grant table says.
        if (SettlementWar.AtWar(data, pawnHome, home)) return true;
        // **Which acts count (round 154, measured).** The first version asked three fixed acts - Take, Use, Store - and
        // the probe caught the hole it made: a tree is acted on with `Gather` (the table's row is
        // `Gathering`/`Gather`/`Plant`), so a guest granted `Gathering` passed the gate and was still shown the tree as
        // forbidden, because none of the three named acts matched. The question that actually belongs here is the one the
        // table already answers: "did the owner grant this visitor *any* action that this kind of thing takes part in".
        // That is `PermitsAnyOn`, the same predicate the reservation gate falls back to (round 136/138) - one source of
        // truth instead of a second, hand-written list that can drift.
        var kind = SettlementRule.KindOf(thing);
        // A thing the table cannot classify is not hidden: an unknown kind is a bug in `KindOf`, and hiding things it
        // cannot name would break vanilla jobs far from anything ownership-related (the live gate permits `None` kinds
        // for the same reason - see the note in `SettlementRule` about the deleted player-keyed pair).
        if (kind == SettlementKind.None) return false;
        return !SettlementRuleTable.PermitsAnyOn(data.GrantsForPawn(pawnHome, home), kind);
    }

    internal static void NoteHidden() => Hidden++;

    internal static string Summary() => $"隐藏 {Hidden} / 查询 {Queries}";
}

/// <summary>Turns the ownership answer into vanilla's own "forbidden" flag for that pawn.</summary>
[HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), typeof(Thing), typeof(Pawn))]
internal static class SettlementForbidVisibilityPatch
{
    private static void Postfix(Thing t, Pawn pawn, ref bool __result)
    {
        if (__result) return;
        try
        {
            if (!SettlementForbidVisibility.ShouldHide(t, pawn, out _)) return;
            __result = true;
            SettlementForbidVisibility.NoteHidden();
        }
        catch (Exception error)
        {
            // A visibility patch must never change a decision on its own failure: `__result` stays whatever vanilla said.
            Log.Warning("[TurboTick] 据点禁取失败：" + error.GetType().Name + ": " + error.Message);
        }
    }
}
