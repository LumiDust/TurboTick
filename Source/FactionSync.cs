using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboTick.Networking;
using Verse;
using RimWorld;

namespace TurboTick;

/// <summary>Faction standing - goodwill and relation kind - applied on a replica onto the same <c>FactionRelation</c>.
///
/// <para><b>Why a replica needs this.</b> Goodwill changes in <c>Faction.TryAffectGoodwillWith</c>, which is called from
/// gifting, quests, incidents and social fights - every one of them a tick a replica does not run. The faction page therefore
/// shows the goodwill the save was loaded with forever, and because <c>kind</c> is derived from it, an ally that turned
/// hostile mid-session still reads as an ally: caravans, trade and raids all follow that stale answer.</para>
///
/// <para><b>Why both the number and the kind are sent.</b> They are <i>not</i> the same fact. <c>CheckKindThresholds</c>
/// derives <c>kind</c> from goodwill (-75 = hostile, +75 = ally, back to neutral at 0) but only for factions where
/// <c>HasGoodwill</c> holds; a permanent enemy is set straight through <c>SetRelationDirect</c> and its kind is independent of
/// any number. Sending only goodwill would leave those factions wrong, and sending only the kind would lose the number the
/// player is actually adjusting.</para>
///
/// <para><b>Why the pair is directed.</b> Vanilla writes both sides in <c>TryAffectGoodwillWith</c>, but <c>SetRelation</c>
/// replaces the record on the other side with a fresh <c>FactionRelation</c> whose <c>baseGoodwill</c> is the default 100 -
/// so the two directions can legitimately disagree, and a symmetric channel would silently pick one of them.</para>
///
/// <para><b>Why there is no hook.</b> Goodwill is written from a dozen unrelated call sites and a missed one is a stale number
/// nobody notices. The population is tiny instead - a dozen factions, on the order of a hundred ordered pairs - so this
/// recomputes the whole table each frame and sends only what differs from last time. Recomputation cannot miss a writer the
/// way an incomplete patch set can, and the cost is a hundred integer comparisons.</para>
internal static class FactionSync
{
    private static readonly Dictionary<long, FactionStanding> sent = [];

    /// <summary>Checked once against the game's enum. The wire carries the kind as a byte, and an out-of-range value would
    /// compare unequal to every member on the far end - leaving the faction with whatever kind it loaded with, silently.</summary>
    private static bool mirrored;

    private static void VerifyMirror()
    {
        if (mirrored) return;
        mirrored = true;
        if ((byte)FactionRelationKind.Hostile != (byte)FactionRelationKindWire.Hostile
            || (byte)FactionRelationKind.Neutral != (byte)FactionRelationKindWire.Neutral
            || (byte)FactionRelationKind.Ally != (byte)FactionRelationKindWire.Ally)
            Log.Error("[TurboTick] Networking.FactionRelationKindWire 与 RimWorld.FactionRelationKind 不一致："
                + "请同步 Networking/SettlementGrants.cs 的 FactionRelationKindWire。");
    }

    /// <summary>Directed pairs with no relation record at all. Printed next to the sent count because 23 factions make 506
    /// ordered pairs, and "sent 506" would otherwise look complete even if a quarter of them had silently been skipped.</summary>
    internal static int MissingPairs { get; private set; }

    internal static long PairsSent { get; private set; }
    internal static long PairsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Last id set sent per faction, so only a change travels. Keyed by the faction's scribed <c>loadID</c>.
    /// The value is the same `primary;minor,minor` string that goes on the wire - comparing the encoded form rather than
    /// a struct keeps "what changed" and "what was sent" the same fact.</summary>
    private static readonly Dictionary<int, string> sentIdeos = [];

    internal static long IdeoSends { get; private set; }
    internal static long IdeoApplies { get; private set; }
    internal static int IdeoFailures { get; private set; }

    /// <summary>Factions that actually carry a primary ideoligion. Printed beside the send count because "0 sent" has two
    /// causes - every faction's belief already matched, or no faction has one - and they need different numbers.</summary>
    internal static int IdeoWithPrimary { get; private set; }

    /// <summary>Total minor ideoligions across all factions. A faction can carry several (`AllIdeos` is primary plus
    /// this list), and a count of zero here is not the same as a missing list.</summary>
    internal static int IdeoMinorRows { get; private set; }

    /// <summary>Packs a directed pair into one key. Faction load ids are scribed (`Faction.GetUniqueLoadID` is
    /// <c>"Faction_" + loadID</c>) so they are the same on both ends - unlike a room id, which is a process counter.</summary>
    private static long Key(int faction, int other) => ((long)faction << 32) | (uint)other;

    /// <summary>Every directed pair whose goodwill or kind differs from what was last sent.</summary>
    internal static List<ReplicaFactionRelation>? Publish()
    {
        try
        {
            var manager = Find.FactionManager;
            if (manager is null) return null;
            VerifyMirror();
            var factions = manager.AllFactionsListForReading;
            if (factions is null || factions.Count == 0) return null;
            List<ReplicaFactionRelation>? result = null;
            var alive = new HashSet<long>();
            var missing = 0;
            foreach (var faction in factions)
            {
                if (faction is null || faction.def is null) continue;
                foreach (var other in factions)
                {
                    if (other is null || other.def is null || other == faction) continue;
                    // `allowNull: true` because a pair can legitimately have no record; the default path logs an error.
                    var relation = faction.RelationWith(other, true);
                    // A pair with no record is skipped rather than invented: `SetRelation` only ever writes both sides, so a
                    // one-sided record means the pair has never mattered yet - and counting it keeps "sent 506 of 506" from
                    // quietly meaning "sent 506 of 480 plus 26 that do not exist".
                    if (relation is null) { missing++; continue; }
                    var key = Key(faction.loadID, other.loadID);
                    alive.Add(key);
                    var standing = new FactionStanding(relation.baseGoodwill, (byte)relation.kind);
                    if (sent.TryGetValue(key, out var previous) && previous == standing) continue;
                    sent[key] = standing;
                    (result ??= []).Add(new ReplicaFactionRelation(faction.loadID, other.loadID,
                        standing.Goodwill, standing.Kind));
                    PairsSent++;
                }
            }
            foreach (var stale in sent.Keys.Where(key => !alive.Contains(key)).ToList()) sent.Remove(stale);
            MissingPairs = missing;
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] faction publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>The three diplomacy cooldown stamps last sent, per faction.
    ///
    /// <para>Same shape as the goodwill table: recompute every faction and send only what differs. Three integers over a
    /// dozen factions is cheaper than a patch on each of the three write sites, and - unlike a patch - it cannot miss a
    /// writer. The stamps are public fields on <c>Faction</c> (<c>:485260-485264</c>), scribed (<c>:485508-485510</c>),
    /// so both ends hold the same values after a load and only live changes need to travel.</para>
    ///
    /// <para>Nothing here is a patch: the authority's value is the truth, and a stamp that is too low only means a
    /// button is offered early on a replica - which the authority then refuses, because its own stamp is correct. A
    /// stamp that is too <i>high</i> would hide an available request, so the value is copied, never recomputed.</para></summary>
    private static readonly Dictionary<int, (int Trader, int Orbital, int Military)> sentCooldowns = [];

    /// <summary>Factions whose three stamps differ from what was last sent. Null when nothing changed.</summary>
    internal static List<ReplicaFactionCooldown>? PublishCooldowns()
    {
        try
        {
            var manager = Find.FactionManager;
            if (manager is null) return null;
            var factions = manager.AllFactionsListForReading;
            if (factions is null || factions.Count == 0) return null;
            List<ReplicaFactionCooldown>? result = null;
            var alive = new HashSet<int>();
            foreach (var faction in factions)
            {
                if (faction is null || faction.def is null) continue;
                var key = faction.loadID;
                alive.Add(key);
                var stamps = (faction.lastTraderRequestTick, faction.lastOrbitalTraderRequestTick,
                    faction.lastMilitaryAidRequestTick);
                if (sentCooldowns.TryGetValue(key, out var previous) && previous == stamps) continue;
                sentCooldowns[key] = stamps;
                (result ??= []).Add(new ReplicaFactionCooldown(key, stamps.lastTraderRequestTick,
                    stamps.lastOrbitalTraderRequestTick, stamps.lastMilitaryAidRequestTick));
                CooldownsSent++;
            }
            foreach (var stale in sentCooldowns.Keys.Where(key => !alive.Contains(key)).ToList()) sentCooldowns.Remove(stale);
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] cooldown publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    internal static long CooldownsSent { get; private set; }
    internal static long CooldownsApplied { get; private set; }
    internal static int CooldownFailures { get; private set; }

    /// <summary>Copies one faction's stamps. Writes the fields directly rather than calling anything: the game exposes no
    /// setter, and a request that already happened on the authority is not a transition to re-derive - the numbers the
    /// dialog subtracts from are the whole fact.</summary>
    internal static void ApplyCooldown(ReplicaFactionCooldown state)
    {
        try
        {
            var manager = Find.FactionManager;
            if (manager is null) { CooldownFailures++; return; }
            Faction? faction = null;
            foreach (var candidate in manager.AllFactionsListForReading)
                if (candidate is not null && candidate.loadID == state.Faction) { faction = candidate; break; }
            if (faction is null) { CooldownFailures++; return; }
            faction.lastTraderRequestTick = state.Trader;
            faction.lastOrbitalTraderRequestTick = state.Orbital;
            faction.lastMilitaryAidRequestTick = state.Military;
            CooldownsApplied++;
        }
        catch (Exception error)
        {
            CooldownFailures++;
            Log.Warning("[TurboTick] cooldown apply: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>One line for the census: sent / applied / failed, plus how many factions have a stamp that is not the
    /// load default. The last number is what makes "0 applied" readable - with every faction still at -9999999 the
    /// channel has nothing to carry, and that is a different state from a channel that is silently dropping rows.</summary>
    internal static string CooldownSummary()
    {
        var manager = Find.FactionManager;
        var stamped = 0;
        if (manager?.AllFactionsListForReading is { } factions)
            foreach (var faction in factions)
                if (faction is not null && faction.lastTraderRequestTick != -9999999) stamped++;
        return $"发 {CooldownsSent} / 应用 {CooldownsApplied} / 失败 {CooldownFailures}"
            + $"，已请求过的派系 {stamped} 个";
    }

    /// <summary>Applies one directed pair's standing.
    ///
    /// <para>Writes the fields rather than calling <c>SetRelation</c>: that method replaces the record on the other side with a
    /// bare <c>FactionRelation</c> and throws away the other direction's goodwill, and <c>Notify_RelationKindChanged</c> -
    /// which <c>CheckKindThresholds</c> would call - turns prisoners into guests, fires quest signals and deletes hostile
    /// sites. Neither is a state write; both are gameplay consequences of a transition that already happened on the authority
    /// and was already reported through the letter channel.</para></summary>
    internal static void Apply(ReplicaFactionRelation state)
    {
        try
        {
            var manager = Find.FactionManager;
            if (manager is null) return;
            var factions = manager.AllFactionsListForReading;
            Faction? faction = null;
            Faction? other = null;
            foreach (var each in factions)
            {
                if (each is null) continue;
                if (each.loadID == state.Faction) faction = each;
                if (each.loadID == state.Other) other = each;
            }
            if (faction is null || other is null || faction == other) return;
            var relation = faction.RelationWith(other, true);
            if (relation is null) return;
            var changed = false;
            if (relation.baseGoodwill != state.Goodwill && state.Goodwill is >= -100 and <= 100)
            {
                relation.baseGoodwill = state.Goodwill;
                changed = true;
            }
            if ((byte)relation.kind != state.Kind && state.Kind <= (byte)FactionRelationKind.Ally)
            {
                relation.kind = (FactionRelationKind)state.Kind;
                changed = true;
            }
            if (changed)
            {
                PairsApplied++;
                // The faction page draws its colours from a cached palette keyed by relation kind; vanilla clears it when a
                // kind changes, and a replica reaches that path only through this write.
                ColoredText.ClearCache();
            }
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] faction apply failed ({state.Faction}->{state.Other}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Which ideoligion each faction believes, sent only when it changes.
    ///
    /// <para><b>Why this is not part of the relation table.</b> The two have nothing in common but the faction key: a
    /// relation is a directed pair, a belief is one per faction. Folding them into one record would mean re-sending every
    /// pair whenever a single faction changed its mind.</para>
    ///
    /// <para><b>Why the whole set is recomputed each frame.</b> Same reason as the relation table: `SetPrimary` is
    /// reached from world generation, from the ideoligion page, from `SetIdeo` and from three `Notify_Member*` paths, and
    /// a missed hook is a belief that silently never moves. There are a couple of dozen factions and up to ten
    /// ideoligions each, so the comparison costs nothing.</para></summary>
    internal static List<ReplicaFactionIdeo>? PublishIdeos()
    {
        try
        {
            var manager = Find.FactionManager;
            if (manager is null) return null;
            var factions = manager.AllFactionsListForReading;
            if (factions is null || factions.Count == 0) return null;
            List<ReplicaFactionIdeo>? result = null;
            var alive = new HashSet<int>();
            var withPrimary = 0;
            var minorRows = 0;
            foreach (var faction in factions)
            {
                if (faction is null || faction.def is null) continue;
                var tracker = faction.ideos;
                // `IdeologyActive` off means the field exists but is never populated (`Faction.ExposeData`
                // `:485505` still scribes it). A null tracker on both ends is already equal, so nothing to send.
                if (tracker is null) continue;
                var primary = tracker.PrimaryIdeo;
                var minors = tracker.IdeosMinorListForReading;
                var primaryId = primary?.id ?? 0;
                if (primary is not null) withPrimary++;
                var minorIds = new List<int>(minors?.Count ?? 0);
                if (minors is not null)
                {
                    foreach (var minor in minors)
                    {
                        if (minor is null) continue;
                        // The primary can also appear in the minor list (`RecalculateIdeosBasedOnPlayerPawns` adds every
                        // ideoligion its colonists believe, then picks one of them as primary). Sending it twice would
                        // make the far end re-add it, so the primary is carried by its own field only.
                        if (minor == primary) continue;
                        minorIds.Add(minor.id);
                    }
                }
                minorRows += minorIds.Count;
                minorIds.Sort();
                var encoded = primaryId.ToString(CultureInfo.InvariantCulture) + ";"
                    + string.Join(",", minorIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                alive.Add(faction.loadID);
                if (sentIdeos.TryGetValue(faction.loadID, out var previous) && previous == encoded) continue;
                sentIdeos[faction.loadID] = encoded;
                (result ??= []).Add(new ReplicaFactionIdeo(faction.loadID, primaryId, string.Join(",", minorIds)));
                IdeoSends++;
            }
            foreach (var stale in sentIdeos.Keys.Where(id => !alive.Contains(id)).ToList()) sentIdeos.Remove(stale);
            IdeoWithPrimary = withPrimary;
            IdeoMinorRows = minorRows;
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] faction ideo publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes one faction's belief set.
    ///
    /// <para>The two fields are written directly rather than through `SetPrimary` or `RecalculateIdeosBasedOnPlayerPawns`.
    /// `SetPrimary` (`:487569-487572`) is a bare field write and would be equivalent, but the recalculate path is not: it
    /// clears the minor list, recounts believers and fires the "new primary ideoligion" and "obligations activated"
    /// letters (`:487550-487556`). Those letters already arrived through the letter channel when the change happened on
    /// the authority; running the recalculation here would deliver them a second time on every peer.</para>
    ///
    /// <para>`primaryIdeo` and `ideosMinor` are private, and `ideology` is read-only - reflection is the only route. The
    /// list is replaced rather than mutated so that a replica cannot end up sharing the authority's list object.</para></summary>
    internal static void ApplyIdeo(ReplicaFactionIdeo state)
    {
        try
        {
            var faction = Find.FactionManager?.AllFactionsListForReading?
                .FirstOrDefault(f => f is not null && f.loadID == state.Faction);
            if (faction?.ideos is null) { IdeoFailures++; return; }
            var tracker = faction.ideos;
            var primaryField = HarmonyLib.AccessTools.Field(typeof(FactionIdeosTracker), "primaryIdeo");
            var minorsField = HarmonyLib.AccessTools.Field(typeof(FactionIdeosTracker), "ideosMinor");
            if (primaryField is null || minorsField is null) { IdeoFailures++; return; }
            var wanted = state.Primary == 0 ? null : IdeoById(state.Primary);
            // A primary id that resolves to nothing means the ideoligion table has not caught up yet. Applying the empty
            // answer would blank the faction's belief and every downsteam reader would see "no ideoligion" until the next
            // frame - so the record is skipped instead, and the next frame retries.
            if (state.Primary != 0 && wanted is null) { IdeoFailures++; return; }
            primaryField.SetValue(tracker, wanted);
            var list = new List<Ideo>();
            if (state.Minors.Length > 0)
            {
                foreach (var text in state.Minors.Split(','))
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                    if (IdeoById(id) is { } minor && minor != wanted) list.Add(minor);
                }
            }
            minorsField.SetValue(tracker, list);
            IdeoApplies++;
        }
        catch (Exception error)
        {
            IdeoFailures++;
            if (IdeoFailures <= 6)
                Log.Warning($"[TurboTick] faction ideo apply failed ({state.Faction}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>An ideoligion by `Ideo.id`. `IdeoManager` has no lookup by id, so this scans the world list - which holds
    /// one to three entries in practice.</summary>
    private static Ideo? IdeoById(int id)
    {
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return null;
        foreach (var ideo in ideos)
            if (ideo is not null && ideo.id == id) return ideo;
        return null;
    }

    internal static void Reset()
    {
        sent.Clear();
        sentIdeos.Clear();
    }

    internal static string Summary()
    {
        var count = 0;
        try { count = Find.FactionManager?.AllFactionsListForReading?.Count ?? 0; } catch (Exception) { }
        return $"派系 {count} 个 / 发 {PairsSent} 对 / 应用 {PairsApplied}（无记录 {MissingPairs}，失败 {ApplyFailures}）"
            + $" / 信念 发 {IdeoSends}（{IdeoWithPrimary} 个有主信念，{IdeoMinorRows} 条次信念）/ 应用 {IdeoApplies}"
            + $"（失败 {IdeoFailures}）";
    }
}

/// <summary>One directed standing, kept for comparison so only real changes reach the wire.</summary>
internal readonly record struct FactionStanding(int Goodwill, byte Kind);
