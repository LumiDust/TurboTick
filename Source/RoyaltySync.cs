using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Every pawn's titles, favor and permits, for a replica whose royalty tracker never changes.
///
/// <para><b>The gap this closes.</b> `Pawn_RoyaltyTracker` reaches a replica exactly once, from the save file:
/// `Pawn.ExposeData` scribes `royalty` (`:126836`), and `Pawn_RoyaltyTracker.ExposeData` (`:475905`) writes `titles`,
/// `favor`, `highestTitles`, `heirs`, `permits` and `abilities`. After that nothing on the replica moves it, and
/// <c>AuthoritativeFrame.PawnState</c> stays <c>null</c> - measured: `pawn-payload-size` reports 71 pawns at
/// 636386 B, 8963 B each, so a whole Scribe fragment cannot be sent per frame.</para>
///
/// <para><b>What reads it.</b> Four UI readers, all of which the ledger had listed as "no carrier":</para>
/// <list type="bullet">
/// <item>`CharacterCardUtility.DrawCharacterCard` prints the title line, and
/// `Pawn_RoyaltyTracker.AllTitlesInEffectForReading` is what it reads.</item>
/// <item>`PermitsCardUtility.DoLeftRect` lists `AllFactionPermits` - the permits the player **bought**, not the ones a
/// title grants.</item>
/// <item>`RoyalTitle.RoomRequirementGracePeriodActive` (`:474536` region) is what turns a title's room requirements on
/// and off; it is read by the room-stats overlay and by `RoyalTitleUtility`.</item>
/// <item>Every `RoyalTitlePermitWorker_*` (drop resources, call shuttle, call troops) checks
/// `AllFactionPermits.Any(p => p.Permit == def && p.Faction == faction)` before running, so without the list those
/// permit gizmos are permanently greyed on a replica.</item>
/// </list>
///
/// <para><b>Why titles travel as `faction#defName` and not as `SetTitle` calls.</b> `SetTitle` (`:475015`) is a
/// <em>transition</em>: it fires `OnPreTitleChanged`, calls `ApplyRewardsForTitle`, resets favor to zero, and calls
/// `OnPostTitleChanged` which grants and removes abilities and sends letters. A replica receiving "the host now has
/// Knight of the Empire" must not replay any of that - it must end up with the same list. So the list is written
/// directly, exactly as `PawnTraitSync` writes traits rather than replaying `GainTrait`'s rewards. This is the same
/// distinction `AuthoritativeReplica.ApplyFaction` documents for `SetFaction` vs `SetFactionDirect`.</para>
///
/// <para><b>Why the permits list carries a flag for "granted by the title".</b> `HasPermit` (`:475560` region) falls
/// back to `currentTitle.permits.Contains(permit)` when `factionPermits` has no row, so an empty
/// `AllFactionPermits` does not mean "no permits usable" - it means "none bought". Both readings are needed and they
/// disagree, which is why the payload carries only `factionPermits` and the replica re-derives the title-granted half
/// from the title it just received. Sending both would be two sources for one answer.</para>
/// </summary>
internal static class RoyaltySync
{
    /// <summary>Last payload sent per pawn. A royalty tracker changes on a scale of days, so the channel costs
    /// nothing on almost every frame - and a `null` baseline means "never sent", which must publish rather than
    /// compare against an empty world. That exact mistake cost a round on `AreaSync`/`PlaySettingsSync`.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static long PawnsSent { get; private set; }
    internal static long TitlesSent { get; private set; }
    internal static long PermitsSent { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>The last record published, so the encoded shape - not just the count - can be read from the census.</summary>
    internal static ReplicaRoyalty? LastSample { get; private set; }

    internal static int TrackedCount => sent.Count;

    internal static void Reset()
    {
        sent.Clear();
        PawnsSent = 0;
        TitlesSent = 0;
        PermitsSent = 0;
        ApplyCount = 0;
        ApplyFailures = 0;
        LastSample = null;
    }

    /// <summary>Encodes one pawn's royalty state.
    ///
    /// <para>Shape: <c>titles|permits</c>, where titles are <c>factionLoadId:defName</c> and permits are
    /// <c>factionLoadId:permitDefName</c>, each list <c>;</c>-separated. Three empty fields mean "a tracker with
    /// nothing in it", which is a real state and must overwrite a replica that still has last session's title.</para>
    ///
    /// <para>Titles are sorted by <c>(faction, defName)</c> and permits by <c>(faction, def)</c> so the encoding is
    /// stable: `AllTitlesForReading` is the tracker's own list and two peers that reached the same state by different
    /// routes would otherwise produce different strings and resend forever. Permits are <em>not</em> deduplicated -
    /// `FactionPermit` allows the same permit twice for two factions and the list is what the card shows.</para></summary>
    private static string Encode(Pawn_RoyaltyTracker royalty)
    {
        var titles = new List<(int Faction, string Def)>();
        foreach (var title in royalty.AllTitlesForReading)
        {
            if (title?.def?.defName is not { Length: > 0 } name) continue;
            titles.Add((title.faction?.loadID ?? -1, name));
        }
        titles.Sort((a, b) => a.Faction != b.Faction ? a.Faction.CompareTo(b.Faction)
            : string.CompareOrdinal(a.Def, b.Def));
        var permits = new List<(int Faction, string Def)>();
        foreach (var permit in royalty.AllFactionPermits)
        {
            if (permit?.Permit?.defName is not { Length: > 0 } name) continue;
            permits.Add((permit.Faction?.loadID ?? -1, name));
        }
        permits.Sort((a, b) => a.Faction != b.Faction ? a.Faction.CompareTo(b.Faction)
            : string.CompareOrdinal(a.Def, b.Def));
        var builder = new StringBuilder();
        for (var i = 0; i < titles.Count; i++)
        {
            if (i > 0) builder.Append(';');
            builder.Append(titles[i].Faction.ToString(CultureInfo.InvariantCulture)).Append(':').Append(titles[i].Def);
        }
        builder.Append('|');
        for (var i = 0; i < permits.Count; i++)
        {
            if (i > 0) builder.Append(';');
            builder.Append(permits[i].Faction.ToString(CultureInfo.InvariantCulture)).Append(':').Append(permits[i].Def);
        }
        return builder.ToString();
    }

    internal static List<ReplicaRoyalty>? Publish()
    {
        List<ReplicaRoyalty>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.royalty is not { } royalty) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var encoded = Encode(royalty);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    LastSample = new ReplicaRoyalty(id, encoded);
                    (changed ??= []).Add(LastSample);
                    PawnsSent++;
                    TitlesSent += royalty.AllTitlesForReading.Count;
                    PermitsSent += royalty.AllFactionPermits.Count;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] royalty publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>What the channel would send right now, without recording it.
    ///
    /// <para><b>Why not just call <see cref="Publish"/>.</b> `Publish` writes the `sent` baseline as it goes, so calling
    /// it from a probe consumes the change: the next real frame then compares against the value the probe already
    /// recorded and sends nothing, and the replica never receives that title. The same hazard is why
    /// `WireRoundTripProbe` reuses the frame that really went out instead of building one. This walks the same pawns
    /// and compares against the same baseline without mutating either.</para>
    ///
    /// <para><b>Why it is needed at all.</b> Measured: with no replica in the room the server reports
    /// `副本=0 未推进原因=没有观众(PauseWithoutReplicas)` and builds no frames, so `TitlesSent` stays at whatever the
    /// last frame carried - a title granted afterwards is invisible in the census through no fault of the channel.
    /// `Pending` answers the question the census cannot: would the next frame carry it.</para></summary>
    internal static string Pending()
    {
        var changes = 0;
        var titles = 0;
        var permits = 0;
        var samples = new List<string>();
        try
        {
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.royalty is not { } royalty) continue;
                    var encoded = Encode(royalty);
                    if (sent.TryGetValue(pawn.thingIDNumber, out var previous) && previous == encoded) continue;
                    changes++;
                    titles += royalty.AllTitlesForReading.Count;
                    permits += royalty.AllFactionPermits.Count;
                    if (samples.Count < 3)
                        samples.Add($"#{pawn.thingIDNumber}「{(encoded.Length <= 60 ? encoded : encoded[..60] + "…")}」");
                }
        }
        catch (Exception error)
        {
            return "待发统计失败：" + error.GetType().Name + ": " + error.Message;
        }
        return changes == 0
            ? "下一帧待发 0 个（基线已是当前状态）"
            : $"下一帧待发 {changes} 个（头衔 {titles} 条 / 已购许可 {permits} 条）" + string.Join("，", samples);
    }

    /// <summary>What the census prints. `TrackedCount` alone cannot distinguish "no pawn has a tracker" from "the
    /// channel never publishes", and this save has both: 71 pawns, 4 with a tracker, 0 with a title before
    /// `royalty-grant` created one.</summary>
    internal static string Summary()
    {
        var withTracker = 0;
        var withTitle = 0;
        var withPermit = 0;
        var titled = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.royalty is not { } royalty) continue;
                withTracker++;
                titled += royalty.AllTitlesForReading.Count;
                if (royalty.AllTitlesForReading.Count > 0) withTitle++;
                if (royalty.AllFactionPermits.Count > 0) withPermit++;
            }
        return $"追踪 {TrackedCount} 个 / 存档内 {withTracker} 个带 tracker（有头衔 {withTitle} 个共 {titled} 条，"
            + $"有已购许可 {withPermit} 个）；发出 {PawnsSent} 次（头衔 {TitlesSent} / 许可 {PermitsSent}），"
            + $"应用 {ApplyCount} 次，失败 {ApplyFailures} 次";
    }

    /// <summary>Idempotent round trip for one pawn: encode, apply the same string back, re-encode.
    ///
    /// <para>A count cannot tell a correct reconcile from one that churns: `Apply` clears `titles` and refills it, so
    /// a bug that rebuilt the list every frame would still report `应用 N` with the right-looking numbers. Re-encoding
    /// after the apply compares the two strings the wire carries.</para>
    ///
    /// <para>Same shape as `PawnTraitSync.Probe` and `GeneSync.Probe`; `-1`/no argument picks the first pawn with a
    /// tracker, because the authority has no player to select one.</para></summary>
    internal static string Probe(string? argument)
    {
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
            .FirstOrDefault(p => p?.royalty is not null
                && (string.IsNullOrEmpty(argument) || p.thingIDNumber.ToString(CultureInfo.InvariantCulture) == argument));
        if (pawn?.royalty is not { } royalty) return "没有带 royalty tracker 的小人";
        var before = Encode(royalty);
        Apply(new ReplicaRoyalty(pawn.thingIDNumber, before));
        var after = Encode(royalty);
        return $"小人 #{pawn.thingIDNumber}：往返" + (before == after ? "一致" : "不一致")
            + $"（前 {before.Length} 字符 / 后 {after.Length} 字符）；"
            + $"头衔 {royalty.AllTitlesForReading.Count} 条，已购许可 {royalty.AllFactionPermits.Count} 条；"
            + $"前「{Trim(before)}」后「{Trim(after)}」；{Pending()}";
    }

    private static string Trim(string text) => text.Length <= 90 ? text : text[..90] + "…";

    /// <summary>Writes the list onto the replica's tracker.
    ///
    /// <para><b>Writes <c>titles</c> rather than calling <c>SetTitle</c>.</b> See the class note: `SetTitle` is the
    /// transition the authority already ran, including rewards and letters. What the replica needs is the resulting
    /// list, and the list is a public `List&lt;RoyalTitle&gt;` behind `AllTitlesForReading`.</para>
    ///
    /// <para><b><c>RoyalTitle</c> is rebuilt rather than reused.</b> `RoyalTitle` has public fields (`:474520`:
    /// `faction`, `def`, `pawn`, `receivedTick`, `wasInherited`, `conceited`) and a parameterless constructor, so a
    /// fresh instance per title is exact. Reusing the old object would keep `receivedTick` from a previous session,
    /// which `RoyalTitleUtility` reads for the "held for" line.</para>
    ///
    /// <para><b>Abilities are not written here.</b> `AllAbilitiesForReading` is a separate list on the same tracker and
    /// `AbilitySync` already carries charges and cooldowns by def name. Two channels writing one list would disagree
    /// during the window between them; the ability <em>set</em> is derived from the title by the game's own
    /// `OnPostTitleChanged`, which the replica does not run - so a title granted after the save still leaves the
    /// replica without those abilities until `AbilitySync` sees them, and that is a known and separate gap
    /// (`royalty-count` reports `皇家能力合计` to make it visible).</para></summary>
    internal static void Apply(ReplicaRoyalty state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var royalty = pawn?.royalty;
            if (royalty is null) return;
            var wanted = Parse(state.State, out var permits);
            var current = royalty.AllTitlesForReading;
            var matches = current.Count == wanted.Count;
            if (matches)
                for (var i = 0; i < wanted.Count; i++)
                {
                    var have = current[i];
                    if (have?.def is null || have.def.defName != wanted[i].Def
                        || (have.faction?.loadID ?? -1) != wanted[i].Faction)
                    {
                        matches = false;
                        break;
                    }
                }
            if (!matches)
            {
                current.Clear();
                foreach (var (faction, def) in wanted)
                    current.Add(new RoyalTitle
                    {
                        faction = FactionOf(faction),
                        def = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(def),
                        pawn = pawn,
                        // `-1` is `RoyalTitle.receivedTick`'s own "unknown" value. Using the replica's clock here would
                        // make "held for 3 days" depend on how long this peer has been running.
                        receivedTick = -1,
                        wasInherited = false,
                        conceited = false
                    });
                // Nothing to invalidate: `AllTitlesInEffectForReading` is a computed predicate over `titles`
                // (it filters on the faction being non-null and the title still being held), not a cached list, so
                // writing `titles` is immediately visible to every reader.
            }
            // Permits: same "the result, not the purchase" rule. `AddPermit` (`:475470`) calls `ApplyPermit` and fires
            // `SendPermitLetter`, so it is a transition; the replica wants the list.
            var factionPermits = royalty.AllFactionPermits;
            factionPermits.Clear();
            foreach (var (faction, def) in permits)
            {
                var permitDef = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(def);
                var owner = FactionOf(faction);
                if (permitDef is null || owner is null) continue;
                // Constructor is `(Faction, RoyalTitleDef title, RoyalTitlePermitDef permit)` (`:454616`) - the title
                // is which title granted it, and a bought permit may come from a title the pawn no longer holds, so
                // `null` is legal there.
                factionPermits.Add(new FactionPermit(owner, null, permitDef));
            }
            ApplyCount++;
        }
        catch (Exception error)
        {
            if (ApplyFailures++ < 8)
                Log.Warning("[TurboTick] 恢复爵位失败 id=" + state.Pawn + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static Faction? FactionOf(int loadId)
    {
        if (loadId < 0) return null;
        foreach (var faction in Find.FactionManager.AllFactionsListForReading)
            if (faction.loadID == loadId) return faction;
        return null;
    }

    private static List<(int Faction, string Def)> Parse(string encoded, out List<(int Faction, string Def)> permits)
    {
        permits = [];
        var bar = encoded.IndexOf('|');
        var titlePart = bar < 0 ? encoded : encoded[..bar];
        var permitPart = bar < 0 ? "" : encoded[(bar + 1)..];
        return Split(titlePart, ref permits, permitPart);
    }

    private static List<(int Faction, string Def)> Split(string titlePart, ref List<(int Faction, string Def)> permits,
        string permitPart)
    {
        var titles = new List<(int, string)>();
        foreach (var entry in titlePart.Split(';'))
        {
            if (entry.Length == 0) continue;
            var colon = entry.IndexOf(':');
            if (colon <= 0) continue;
            if (int.TryParse(entry[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var faction))
                titles.Add((faction, entry[(colon + 1)..]));
        }
        foreach (var entry in permitPart.Split(';'))
        {
            if (entry.Length == 0) continue;
            var colon = entry.IndexOf(':');
            if (colon <= 0) continue;
            if (int.TryParse(entry[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var faction))
                permits.Add((faction, entry[(colon + 1)..]));
        }
        return titles;
    }
}
