using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Which thing kinds and codex entries the game treats as discovered.
///
/// <para><b>Why a replica needs this.</b> Three `Game`-owned databases decide what the player is allowed to see, and all
/// three only ever change from code a replica does not run. <c>Game.hiddenItemsManager</c> (`:49837`, scribed at
/// `:50193`) holds <c>Dictionary&lt;ThingDef, bool&gt;</c>; <c>HiddenItemsManager.Hidden</c> (`:361859`) is read by four
/// enumeration sites that build the player's item lists, and <c>SetDiscovered</c> (`:361851`) is called from
/// <c>Thing.SpawnSetup</c> (`:157062`) and <c>Pawn.EverVisible</c> (`:118708`) — spawn events a display replica does not
/// generate. So a thing kind the authority has unlocked stays hidden on the replica forever: the item simply never appears
/// in a stockpile filter or a bill's ingredient list, and the player has no way to tell that it exists.</para>
///
/// <para><b>Why the whole dictionary and not the one unlocked def.</b> <c>hiddenItemDefs</c> starts with every
/// <c>hiddenWhileUndiscovered</c> def set to true (`:361867-361876`), and <c>SetDiscovered</c> flips one entry to false.
/// The list of keys is therefore determined by the def database, not by the save: sending only the flipped defs would mean
/// a replica had to reconstruct the initial set itself, and any def added by a mod after the save was written would land on
/// the wrong side. Sending the whole table is 1857 B measured, once, and it cannot disagree about the key set.</para>
///
/// <para><b>Why `entityCodex` rides along.</b> `Game.entityCodex` (`:49839`, scribed `:50194`) is the same shape — three
/// collections plus a debug flag (`:361571-361574`) — and the same cause: entries are discovered by
/// <c>EntityCodex.SetDiscovered</c> from spawn and visibility events. Measured on the authority: 3 categories (1
/// discovered), 27 entries (1 discovered). Those counts drive the codex tab, so a replica shows every entry as unknown
/// while the authority has one open.</para>
///
/// <para><b>Why `customXenogermDatabase` does not.</b> It is the third database on the same object (`:49831`) and it is
/// scribed the same way (`:50190`), but it measured zero carriers (`自定义异种胚 0 个`) — Biotech is not active here, and a
/// channel with no carriers cannot be told apart from a broken one. Recorded in `progress.md` instead of built.</para>
internal static class DiscoverySync
{
    /// <summary>Last payload sent, so an unchanged table costs nothing.</summary>
    private static string? sent;

    internal static long Sends { get; private set; }
    internal static long Applies { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static string? LastSample { get; private set; }

    /// <summary>How many entries the authority considers still hidden, so the census can say whether the table is
    /// exercised at all. A table of all-true entries produces a payload identical to a fresh save's, which would make
    /// "the channel works" and "nothing has ever been unlocked" the same reading.</summary>
    internal static int HiddenRemaining { get; private set; }

    internal static int HiddenTotal { get; private set; }

    /// <summary>The payload: two whole Scribe fragments, joined by a newline.
    ///
    /// <para>XML rather than a def-name list, for the same reason every other channel uses one: `ExposeData` is the
    /// game's own writer, so a modded field inside either database travels without this file knowing about it. The two
    /// fragments are separated by a newline because an XML fragment never contains a bare newline outside a string
    /// value, and string values are escaped by the writer.</para></summary>
    internal static List<ReplicaDiscovery>? Publish()
    {
        try
        {
            var game = Current.Game;
            if (game is null) return null;
            var hidden = ScribeBridge.SaveOrNull(game.hiddenItemsManager);
            var codex = ScribeBridge.SaveOrNull(game.entityCodex);
            if (hidden is null && codex is null) return null;
            var payload = (hidden ?? string.Empty) + "\n" + (codex ?? string.Empty);
            if (string.Equals(payload, sent, StringComparison.Ordinal)) return null;
            sent = payload;
            LastSample = payload.Length.ToString(CultureInfo.InvariantCulture) + " B";
            CountHidden(game);
            Sends++;
            return [new ReplicaDiscovery(payload)];
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 发现状态采样失败：" + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    private static void CountHidden(Game game)
    {
        HiddenTotal = 0;
        HiddenRemaining = 0;
        var field = HarmonyLib.AccessTools.Field(typeof(HiddenItemsManager), "hiddenItemDefs");
        if (field?.GetValue(game.hiddenItemsManager) is System.Collections.IDictionary table)
        {
            HiddenTotal = table.Count;
            foreach (System.Collections.DictionaryEntry entry in table)
                if (entry.Value is true) HiddenRemaining++;
        }
    }

    internal static void Apply(ReplicaDiscovery state)
    {
        try
        {
            var game = Current.Game;
            if (game is null || string.IsNullOrEmpty(state.Payload)) return;
            var split = state.Payload.IndexOf('\n');
            if (split < 0) { ApplyFailures++; return; }
            var hidden = state.Payload[..split];
            var codex = state.Payload[(split + 1)..];
            var wrote = 0;
            // Each fragment is loaded only when non-empty: a save without an `entityCodex` (before Anomaly was
            // installed) would otherwise send an empty string and `Load` would refuse, turning a legitimate
            // "this database does not exist here" into a failure count.
            if (hidden.Length > 0 && ScribeBridge.Load(game.hiddenItemsManager, hidden)) wrote++;
            if (codex.Length > 0 && ScribeBridge.Load(game.entityCodex, codex)) wrote++;
            if (wrote == 0) { ApplyFailures++; return; }
            Applies++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            Log.Warning("[TurboTick] 发现状态应用失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static string Summary() =>
        $"隐藏物品表 {HiddenTotal} 项（仍隐藏 {HiddenRemaining}）/ 发 {Sends} / 应用 {Applies}（失败 {ApplyFailures}）"
        + $" / 载荷 {LastSample ?? "(无)"}";
}
