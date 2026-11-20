using System.Collections.Generic;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Adds the "据点授权" button to any building inside a settlement that the local player owns.
///
/// <para><b>Why a gizmo on buildings rather than a settlement object.</b> A settlement is a rectangle, not a <c>Thing</c>, so
/// there is nothing to select. The player's mental model is "I am standing in my base", and every building in it is a
/// handle to that concept - so the button appears wherever the player already clicked. Attaching it to a new invisible
/// thing would need a spawn protocol, a save round trip and its own selection rules for a button.</para>
///
/// <para><b>Why only the owner sees it.</b> A visitor opening the grant editor for somebody else's settlement would be a
/// permission escalation with a friendly label. The check is the same owner test the gates use, so a button that appears is a
/// button that can actually write.</para>
///
/// <para><b>Why the settlement is looked up from the building's own cell.</b> A building spans several cells and can sit on a
/// boundary - the corner of a wall might be in two settlements. The lookup uses the building's interaction cell where one
/// exists and its position otherwise, which is the cell the player is pointing at when they select it.</para></summary>
[HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
internal static class SettlementGrantGizmo
{
    [UsedImplicitly]
    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Building __instance)
    {
        foreach (var gizmo in values) yield return gizmo;
        // A settlement with no owner declared is not editable by anybody: there is no player whose grants they would be.
        var data = SettlementGates.Data;
        if (data is null || !data.Active || __instance?.Map is null) yield break;
        var player = SettlementGates.LocalPlayer;
        if (player == 0) yield break;
        var cell = __instance.InteractionCell.IsValid ? __instance.InteractionCell : __instance.Position;
        var settlement = data.SettlementAt(__instance.Map.uniqueID, cell);
        if (settlement == 0) yield break;
        var site = data.Site(settlement);
        if (site is null || site.Retired || site.OwnerPlayer != player) yield break;
        yield return new Command_Action
        {
            defaultLabel = "据点授权",
            defaultDesc = $"调整谁能在这个据点（{site.Name}）里做什么。\n\n"
                + "改动立即生效，并会同步给其他玩家——拒绝他的操作时，他会看到缺的是哪一项。",
            icon = ContentFinder<UnityEngine.Texture2D>.Get("UI/Commands/RenameZone", reportFailure: false),
            action = () => Find.WindowStack.Add(new SettlementGrantWindow(data, settlement)),
        };
    }
}
