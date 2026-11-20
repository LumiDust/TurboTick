using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The colony's history-event table, applied on a replica so the faction dialog's "recent events" is not frozen.
///
/// <para><b>Why a replica needs this.</b> <c>HistoryEventsManager.RecordEvent</c>
/// (<c>Assembly-CSharp.decompiled.cs:362688</c>) appends to two private tables - <c>colonistEvents</c> (one row per
/// <c>HistoryEventDef</c>, gated on the doer being a colonist) and <c>eventsAffectingFaction</c> (one map per faction,
/// gated on an <c>AffectedFaction</c> argument). It is called from about forty gameplay sites: cutting a tree
/// (<c>:248502</c>), eating human meat (<c>:158180</c>), a death (<c>:111374</c>), a ritual (<c>:264012</c>). A replica
/// runs none of them, so both tables stay exactly as the save loaded - and the one reader that shows them to a player,
/// <c>FactionDialogMaker.GetRecentEvents</c> (<c>:644122</c>), keeps printing the same list forever.</para>
///
/// <para><b>What the reader actually asks.</b> Three queries, all public:
/// <c>GetRecentCountWithinTicks(def, 3600000, faction)</c>, <c>GetRecent(def, duration, outTicks, outGoodwill,
/// faction)</c> (<c>:362845</c>, <c>:362862</c>) and <c>Any</c> (<c>:362815</c>). The dialog then sums the goodwill
/// column and prints one line per def whose sum is non-zero. So the payload has to carry the <i>pairs</i>, not just a
/// count: a count alone would print "x3" with the wrong sign.</para>
///
/// <para><b>Why the rows are the window, not an append log.</b> The authority trims each list to
/// <c>def.maxRemembered</c> (<c>:362713-362717</c>) and <c>CheckRemoveOldEvents</c> (<c>:362755</c>) drops the whole row
/// again every 10000 ticks. An append log would therefore have to replay both policies to stay identical, and any drift
/// would be permanent. Sending the current window per changed row makes the replica's lists <i>equal</i> by
/// construction, which is also what lets an emptied row travel as an empty string - the same "empty is a real state"
/// rule <c>RoofSync</c> uses, because a def that stopped being recent must be cleared, not merely left alone.</para>
///
/// <para><b>Why dirty marks and not a full scan.</b> <c>DefDatabase&lt;HistoryEventDef&gt;</c> holds thousands of defs;
/// walking both tables every frame to find what changed would cost more than the channel is worth. <c>RecordEvent</c> is
/// the only writer, so a postfix on it marks the exact row. The first publish still has to seed from the save - a colony
/// that has been running for a year carries a populated table and no hook has fired for any of it - which is what
/// <see cref="SeedOnce"/> is for, with a cap so an unusual save cannot produce a single enormous frame.</para>
///
/// <para><b>Why there is no command.</b> This is state, not input: the authority records events because its own
/// simulation produced them, exactly like weather or plant growth. Nothing a player clicks needs to travel for this, so
/// there is no <c>CommandCodec</c> kind and no server whitelist entry.</para></summary>
internal static class HistorySync
{
    /// <summary>Only the rows that changed, keyed by <c>defName|factionLoadId</c> (<c>-1</c> for the colonist table).</summary>
    private static readonly HashSet<string> dirty = [];

    /// <summary>The encoded window last sent per key, so an unchanged row does not travel again. Comparing the encoded
    /// form keeps "what changed" and "what was sent" the same fact, the same choice <c>FactionSync</c> makes.</summary>
    private static readonly Dictionary<string, string> sent = [];

    private static bool seeded;

    internal static long RowsSent { get; private set; }
    internal static long RowsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Rows the one-time seed refused to send because <see cref="SeedCap"/> was reached. Printed next to the
    /// send count: "seeded 400" would otherwise look complete on a table that holds 900.</summary>
    internal static int SeedSkipped { get; private set; }

    private const int SeedCap = 400;

    /// <summary>Marks the row a recorded event landed in. Called from a postfix on <c>RecordEvent</c>, which is the only
    /// writer of either table.</summary>
    internal static void Touched(HistoryEvent historyEvent)
    {
        try
        {
            // `HistoryEvent` is a **struct** (`Assembly-CSharp.decompiled.cs:362572`), not a class: `HistoryEvent?` is
            // `Nullable<HistoryEvent>`, whose only members are `HasValue`/`Value`, so `event.def` does not compile. The
            // null check that matters is the one on `def`, which is a reference field and can legitimately be null on a
            // default-constructed value.
            if (historyEvent.def is null) return;
            Faction? faction = null;
            if (historyEvent.args.TryGetArg(HistoryEventArgsNames.AffectedFaction, out Faction affected)) faction = affected;
            Mark(historyEvent.def.defName, faction?.loadID ?? -1);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] history touched: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Mark(string defName, int faction)
    {
        if (string.IsNullOrEmpty(defName)) return;
        dirty.Add(defName + "|" + faction.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Seeds the dirty set from the save on the first publish. Bounded by <see cref="SeedCap"/>: a long-running
    /// colony can hold a row for every def it has ever touched, and one frame carrying all of them would be megabytes.
    /// The cap is reported rather than silent.</summary>
    private static void SeedOnce(HistoryEventsManager manager)
    {
        seeded = true;
        var added = 0;
        foreach (var key in Keys(manager))
        {
            if (added >= SeedCap) { SeedSkipped++; continue; }
            dirty.Add(key);
            added++;
        }
    }

    /// <summary>Every non-empty row currently in the manager, as <c>defName|faction</c> keys.</summary>
    private static IEnumerable<string> Keys(HistoryEventsManager manager)
    {
        var colonist = ColonistTable(manager);
        if (colonist is IEnumerable rows)
        {
            foreach (var row in rows)
            {
                var def = DefOf(row);
                if (def is null) continue;
                if (!HasTicks(RecordOf(row))) continue;
                yield return def.defName + "|-1";
            }
        }
        var factionTables = FactionTables(manager);
        if (factionTables is null) yield break;
        foreach (DictionaryEntry entry in factionTables)
        {
            if (entry.Key is not Faction faction) continue;
            if (entry.Value is not IEnumerable factionRows) continue;
            foreach (var row in factionRows)
            {
                var def = DefOf(row);
                if (def is null) continue;
                if (!HasTicks(RecordOf(row))) continue;
                yield return def.defName + "|" + faction.loadID.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static HistoryEventDef? DefOf(object? pair)
    {
        if (pair is null) return null;
        var key = pair.GetType().GetProperty("Key");
        return key?.GetValue(pair) as HistoryEventDef;
    }

    private static object? RecordOf(object? pair) => pair?.GetType().GetProperty("Value")?.GetValue(pair);

    private static bool HasTicks(object? record)
    {
        if (record is null) return false;
        return record.GetType().GetField("ticksGame")?.GetValue(record) is ICollection ticks && ticks.Count > 0;
    }

    private static object? ColonistTable(HistoryEventsManager manager) =>
        AccessTools.Field(typeof(HistoryEventsManager), "colonistEvents")?.GetValue(manager);

    private static IDictionary? FactionTables(HistoryEventsManager manager) =>
        AccessTools.Field(typeof(HistoryEventsManager), "eventsAffectingFaction")?.GetValue(manager) as IDictionary;

    /// <summary>Collects the changed rows. A row that no longer exists or is empty travels as two empty strings, which is
    /// how the replica learns to clear it.</summary>
    internal static List<ReplicaHistoryEvent>? Publish()
    {
        try
        {
            var manager = Find.HistoryEventsManager;
            if (manager is null) return null;
            if (!seeded) SeedOnce(manager);
            if (dirty.Count == 0) return null;
            List<ReplicaHistoryEvent>? result = null;
            foreach (var key in dirty.ToList())
            {
                var split = key.LastIndexOf('|');
                if (split <= 0) { dirty.Remove(key); continue; }
                var defName = key[..split];
                if (!int.TryParse(key[(split + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var factionId))
                {
                    dirty.Remove(key);
                    continue;
                }
                var def = DefDatabase<HistoryEventDef>.GetNamedSilentFail(defName);
                if (def is null) { dirty.Remove(key); continue; }
                var record = RecordFor(manager, def, factionId);
                var ticks = Join(record, "ticksGame");
                var goodwills = Join(record, "customGoodwill");
                var encoded = ticks + "/" + goodwills;
                if (sent.TryGetValue(key, out var previous) && previous == encoded) { dirty.Remove(key); continue; }
                sent[key] = encoded;
                dirty.Remove(key);
                (result ??= []).Add(new ReplicaHistoryEvent(defName, factionId, ticks, goodwills));
                RowsSent++;
            }
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] history publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    private static string Join(object? record, string field)
    {
        if (record?.GetType().GetField(field)?.GetValue(record) is not IEnumerable list) return string.Empty;
        var parts = new List<string>();
        foreach (var value in list) parts.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0");
        return string.Join(",", parts);
    }

    /// <summary>The record currently stored for one (def, faction), or null when the row does not exist.</summary>
    private static object? RecordFor(HistoryEventsManager manager, HistoryEventDef def, int factionId)
    {
        var table = TableFor(manager, factionId, create: false);
        return table is null ? null : ReadRecord(table, def);
    }

    private static object? TableFor(HistoryEventsManager manager, int factionId, bool create)
    {
        if (factionId < 0) return ColonistTable(manager);
        var tables = FactionTables(manager);
        if (tables is null) return null;
        foreach (DictionaryEntry entry in tables)
        {
            if (entry.Key is Faction faction && faction.loadID == factionId) return entry.Value;
        }
        if (!create) return null;
        var prototype = ColonistTable(manager);
        if (prototype is null) return null;
        Faction? target = null;
        foreach (var candidate in Find.FactionManager.AllFactionsListForReading)
            if (candidate is not null && candidate.loadID == factionId) { target = candidate; break; }
        if (target is null) return null;
        var made = Activator.CreateInstance(prototype.GetType());
        if (made is null) return null;
        tables[target] = made;
        return made;
    }

    private static object? ReadRecord(object table, HistoryEventDef def)
    {
        var indexer = table.GetType().GetProperty("Item", [typeof(HistoryEventDef)]);
        return indexer?.GetValue(table, [def]);
    }

    private static void WriteRecord(object table, HistoryEventDef def, object record)
    {
        var indexer = table.GetType().GetProperty("Item", [typeof(HistoryEventDef)]);
        indexer?.SetValue(table, record, [def]);
    }

    /// <summary>Writes one row. Reads the existing record, replaces its two lists, writes it back - the same
    /// read-mutate-write shape <c>RecordEvent</c> uses (<c>:362704-362710</c>), because the record is a struct in a
    /// <c>List&lt;V&gt;</c> and a getter hands out a copy.</summary>
    internal static void Apply(ReplicaHistoryEvent state)
    {
        try
        {
            var manager = Find.HistoryEventsManager;
            if (manager is null) { ApplyFailures++; return; }
            var def = DefDatabase<HistoryEventDef>.GetNamedSilentFail(state.Def);
            if (def is null) { ApplyFailures++; return; }
            var table = TableFor(manager, state.Faction, create: state.Ticks.Length > 0);
            if (table is null) { ApplyFailures++; return; }
            var recordType = table.GetType().GetGenericArguments()[1];
            var record = ReadRecord(table, def) ?? Activator.CreateInstance(recordType);
            if (record is null) { ApplyFailures++; return; }
            recordType.GetField("ticksGame")?.SetValue(record, Parse(state.Ticks));
            recordType.GetField("customGoodwill")?.SetValue(record, Parse(state.Goodwills));
            WriteRecord(table, def, record);
            RowsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] history apply failed ({state.Def}|{state.Faction}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    private static List<int> Parse(string encoded)
    {
        var list = new List<int>();
        if (string.IsNullOrEmpty(encoded)) return list;
        foreach (var part in encoded.Split(','))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) list.Add(value);
        return list;
    }

    internal static void Reset()
    {
        dirty.Clear();
        sent.Clear();
        seeded = false;
        SeedSkipped = 0;
    }

    /// <summary>Sent / applied / failed, plus how many rows the local table currently holds. The last number is what
    /// makes "0 sent" readable: a fresh save has an empty table and there is nothing to carry.</summary>
    internal static string Summary()
    {
        var rows = 0;
        try
        {
            var manager = Find.HistoryEventsManager;
            if (manager is not null) rows = Keys(manager).Count();
        }
        catch (Exception) { }
        return $"发 {RowsSent} / 应用 {RowsApplied} / 失败 {ApplyFailures}"
            + $"，本端非空行 {rows} 个" + (SeedSkipped > 0 ? $"（首帧超上限跳过 {SeedSkipped} 行）" : string.Empty);
    }
}

/// <summary>Marks the row a recorded event landed in.
///
/// <para>Postfix on <c>HistoryEventsManager.RecordEvent</c> (<c>:362688</c>), the single writer of both tables. A postfix
/// rather than a prefix on purpose: the row does not exist until the method has written it, so reading the faction
/// argument before the call would mark a row that may still be dropped by the <c>IsColonist</c> test.</para></summary>
[HarmonyPatch(typeof(HistoryEventsManager), nameof(HistoryEventsManager.RecordEvent))]
internal static class HistoryRecordCapturePatch
{
    // `ref` because the argument is a struct: without it Harmony hands the postfix a copy of a value the method has
    // already finished with, which happens to be identical here but would silently stop tracking if `RecordEvent`
    // ever mutated its own parameter.
    private static void Postfix(ref HistoryEvent historyEvent) => HistorySync.Touched(historyEvent);
}
