using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Keeps a visitor from paying a trade ship with the host settlement's goods.
///
/// <para><b>The hole this closes.</b> `TradeUtility.AllLaunchableThingsForTrade(Map, ITrader)` (`:608612`) enumerates
/// everything lying in a powered orbital trade beacon's cells - <i>whoever owns it</i>. Vanilla then spends from that list
/// when a deal is accepted, so on a shared colony a guest negotiating at the host's beacon pays with the host's silver and
/// the host discovers the loss afterwards. The ownership layer already refuses `Take` on those stacks, but nothing in the
/// trade path ever asked it: `SettlementTradeGate` asks whether the pawn may trade *here*, not whose goods may be spent.
/// ColonyTogether closes the same hole twice over - a Postfix on this very method (`SettlementLaunchablePatch`,
/// ct:353103) and a Prefix that reimplements `LaunchThingsOfType` with the filter applied (`SettlementPaymentPatch`,
/// ct:353120).</para>
///
/// <para><b>Why a filter on the enumeration and not a reimplementation of payment.</b> CT has to reimplement because its
/// authority needs to run the removal itself. For us the enumeration is the whole surface: filter it, and vanilla's own
/// payment loop then spends only what the negotiator may take - and when that is not enough, vanilla's existing
/// "insufficient launchable resources" refusal fires, which is the honest answer rather than a silent host-side loss.
/// Reimplementing payment would put a second copy of `SplitOff`/`GiveSoldThingToTrader` in the mod, which is the kind of
/// duplication that drifts.</para>
///
/// <para><b>When it stays silent.</b> No game, no settlements, or no trade session - outside a session there is no
/// negotiator to judge, and filtering would change unrelated readings (the silver a window displays) with nobody to blame
/// for it.</para></summary>
[HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.AllLaunchableThingsForTrade))]
internal static class SettlementLaunchableGate
{
    internal static long Dropped { get; private set; }
    internal static long Survived { get; private set; }

    internal static void Reset()
    {
        Dropped = 0;
        Survived = 0;
    }

    /// <summary>Whether this pawn may spend that thing as payment.</summary>
    internal static bool Keep(Pawn? negotiator, Thing? thing)
    {
        if (negotiator is null || thing is null) return true;
        // `Take` is the act that describes "carry it off to pay with it". A stack the pawn may not take is not theirs to
        // spend, whatever the beacon radius says.
        return SettlementGates.Can(negotiator, SettlementAct.Take, thing);
    }

    /// <summary>The filter, over any sequence. Separated from the patch so the probe and the test can read it.</summary>
    internal static IEnumerable<Thing> Filter(IEnumerable<Thing>? things, Pawn? negotiator)
    {
        if (things is null) yield break;
        if (negotiator is null)
        {
            foreach (var thing in things) yield return thing;
            yield break;
        }
        foreach (var thing in things)
        {
            if (!Keep(negotiator, thing))
            {
                Dropped++;
                continue;
            }
            Survived++;
            yield return thing;
        }
    }

    private static void Postfix(ref IEnumerable<Thing> __result)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null || !data.Active) return;
            var negotiator = TradeSession.playerNegotiator;
            if (negotiator is null) return;
            __result = Filter(__result, negotiator);
        }
        catch (Exception error)
        {
            // A trade that cannot be paid is a refusal vanilla already knows how to report; a failed filter must not
            // change what the game does with the list it asked for.
            Log.Warning("[TurboTick] 贸易付款过滤失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static string Summary() => $"放行 {Survived} / 拦下 {Dropped}";
}
