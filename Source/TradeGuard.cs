using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Stops a non-owner from executing a trade while a shared colony runs. Executing it on one client only
/// would move silver and goods on that client alone, which is a desync the player sees as "my silver
/// vanished" — far worse than a clear "the host handles trading" notice.
/// </summary>
[HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
internal static class TradeGuardPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!SharedColony.Gating) return true;
        var settings = TurboTickMod.Settings;
        if (InteractionPolicy.CanRunColonyWideAction(
                sharedPlaying: true,
                isOwner: RoomConnection.IsHost,
                allowEveryone: settings is { everyoneMayTrade: true }))
            return true;
        __result = false;
        Messages.Message(InteractionPolicy.DeniedMessage("交易"), MessageTypeDefOf.RejectInput, false);
        return false;
    }
}

/// <summary>Refuses to *open* a trade that the settlement would refuse to complete - the `Traders` grant.
///
/// <para><b>Why blocking `TryExecute` alone is not enough.</b> That patch stops a trade from *completing*, which is the
/// expensive half, but `TradeSession.SetupWith` is what opens the window: a visitor who may not trade can still open it,
/// see the caravan's stock, and price everything. On a shared colony that is a leak of the owner's inventory, and the
/// refusal arrives at the moment they press Accept - which reads as a bug rather than as a rule. Worse, the goods list
/// is what a player plans around, so the visit is not harmless.</para>
///
/// <para><b>Both patches stay, because they answer different questions.</b> `TradeGuard` asks "who is allowed to
/// complete a colony-wide trade in this session" - a shared-colony policy with no settlement in it. This one asks "may
/// this pawn trade *here*" - the ownership layer's `Traders`/`Trade`/`Building` row, which is what the rule table has
/// said all along and what no gate ever asked. A settlement that grants `Traders` to an ally's settlement must be able
/// to actually let them trade; without this patch the grant is decorative, exactly like `Pass` and `Relax` were.</para>
///
/// <para><b>Prefix, and it must not run when the layer is off.</b> `SettlementGates.Can` short-circuits on
/// `Active`, so a save with no settlements opens trades exactly as before. The negotiator is the pawn the window is
/// being opened for, which is the pawn the gate has to judge - `TradeSession.playerNegotiator` is not set yet at this
/// point, so the parameter is the only source.</para></summary>
[HarmonyPatch(typeof(TradeSession), nameof(TradeSession.SetupWith))]
internal static class SettlementTradeGate
{
    /// <para><b>Positional parameters, deliberately.</b> Harmony matches a prefix parameter to the target by **name**,
    /// and the declared names here are <c>newTrader</c> and <c>newPlayerNegotiator</c> - not <c>trader</c> and
    /// <c>negotiator</c>. The first version used the short names, compiled, passed every test, and then refused to start
    /// the server with
    /// 「补丁 settlement-trade-gate 失败：Patching exception in method static System.Void
    /// RimWorld.TradeSession::SetupWith(RimWorld.ITrader newTrader, Verse.Pawn newPlayerNegotiator, System.Boolean
    /// giftMode)」. `__0`/`__1` are positional and therefore immune to a rename in the dump's own spelling, which is what
    /// this patch actually depends on.</para>
    private static bool Prefix(ITrader __0, Pawn __1)
    {
        var trader = __0;
        var negotiator = __1;
        var data = SettlementGates.Data;
        if (data is null || !data.Active) return true;
        // The thing being traded *at* is the trader pawn when there is one; a trade terminal or an orbital trader has no
        // cell, and then there is no settlement land to ask about and the answer is "no objection from this layer".
        if (trader is not Thing thing) return true;
        if (SettlementGates.Can(negotiator, SettlementAct.Trade, thing)) return true;
        SettlementGates.Count(trade: 1);
        Messages.Message(SettlementGates.ExplainFor(SettlementAct.Trade, thing),
            MessageTypeDefOf.RejectInput, historical: false);
        return false;
    }
}
