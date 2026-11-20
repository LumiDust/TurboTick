using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Carries the four `Policy` databases (outfit / drug / food / reading) so a policy a player creates on one peer
/// exists on the other (round 160).
///
/// <para><b>The defect.</b> The pawn side already travelled: `PolicyCapturePatch` scribes `pawn.outfits`,
/// `pawn.drugs`, `pawn.foodRestriction`, `pawn.timetable` and `pawn.inventoryStock`, and `CommandCodec.PolicyCommand`
/// ships whichever changed. The <i>definitions</i> never did - `grep -l 'OutfitDatabase\|DrugPolicyDatabase\|
/// FoodRestrictionDatabase\|ReadingPolicyDatabase' Source/*.cs` returned nothing. A player who clicked "new outfit" in
/// `Dialog_ManageApparelPolicies` (`:641618`), "new drug policy" (`:641697`), "new food policy" (`:641907`) or "new
/// reading policy" (`:642246`) therefore created the policy on one peer only, and the tracker fragment that names it -
/// which scribes the policy as a reference - resolved to nothing on the other peer, silently, because a failed reference
/// falls back to the tracker's default rather than throwing.</para>
///
/// <para><b>Why the whole table and not a per-policy diff.</b> Twenty-odd policies of a few hundred bytes; a diff needs a
/// stable per-policy "what changed" record, and the only cheap signature (`label`, filter counts) misses a filter edit
/// that swaps one allowed def for another without changing the count. Sending the table when it differs and nothing
/// otherwise keeps the frame quiet while making the answer exact.</para>
///
/// <para><b>Why a poll every <see cref="PollTicks"/> instead of a per-frame comparison.</b> Deciding "did the table
/// change" means scribing every policy, which is the expensive half. A policy is edited by a person through a dialog, so
/// the honest trade is: compare at most once per <see cref="PollTicks"/> ticks, let an edit take up to that long to
/// reach the other end. The alternative - a cheap signature - cannot see a same-count filter swap at all.</para>
///
/// <para><b>Order matters on apply.</b> A policy that does not exist is created first and then loaded from its fragment,
/// because `ExposeData` in load mode writes `id`/`label`/filters into the object it is given. Loading into a fresh
/// `new …(id, label)` also runs the constructor's "allow everything" defaults, which the fragment then overwrites.</para>
/// </summary>
internal static class PolicyTableSync
{
    /// <summary>How often the table is compared. 250 ticks is the game's own "rare" cadence (~4 s at 1x).</summary>
    internal const int PollTicks = 250;

    private static string? sentSignature;
    private static int lastPollTick = int.MinValue;

    internal static long ChangesSent { get; private set; }
    /// <summary>Rows and bytes of the last table that actually went out. Recorded because the first measurement of
    /// one policy read 173035 B - the size question decides whether the whole-table shape can stand, and a frame that
    /// large would be the largest single payload in the project (a whole pawn fragment is 8963 B).</summary>
    internal static int LastRows { get; private set; }
    internal static long LastBytes { get; private set; }
    internal static long Created { get; private set; }
    internal static long Loaded { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int UnresolvedKind { get; private set; }

    /// <summary>The four kinds, in the order a person sees them in the menus.</summary>
    internal static readonly string[] Kinds = ["apparel", "drug", "food", "reading"];

    /// <summary>Every policy of every kind, or null when the table has not changed since the last publish.
    ///
    /// <para>Returns the full table on the first call (so a late joiner gets it) and after any change. Null is the answer
    /// for the overwhelming majority of frames, which is what keeps a channel of twenty fragments out of the wire.</para>
    /// </summary>
    internal static IReadOnlyList<ReplicaPolicy>? Publish()
    {
        try
        {
            var game = Current.Game;
            if (game is null) return null;
            var tick = Find.TickManager?.TicksGame ?? 0;
            var first = sentSignature is null;
            if (!first && tick - lastPollTick < PollTicks) return null;
            lastPollTick = tick;
            var rows = new List<ReplicaPolicy>();
            var signature = new StringBuilder(512);
            foreach (var kind in Kinds)
            {
                foreach (var (id, label, xml) in Entries(kind))
                {
                    signature.Append(kind).Append(':').Append(id).Append(':').Append(label)
                        .Append(':').Append(StableHash(xml)).Append('\n');
                    rows.Add(new ReplicaPolicy(kind, id, label, xml));
                }
            }
            var text = signature.ToString();
            if (!first && string.Equals(text, sentSignature, StringComparison.Ordinal)) return null;
            sentSignature = text;
            LastRows = rows.Count;
            LastBytes = 0;
            foreach (var row in rows) LastBytes += row.Xml.Length + row.Label.Length + row.Kind.Length + 16;
            ChangesSent++;
            return rows;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 方案表发布失败：" + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes one table snapshot: create what is missing, load the fragment into what exists.
    ///
    /// <para>An existing policy whose fragment already matches is not loaded again - `ScribeBridge.Load` re-runs the whole
    /// `ExposeData`, and doing that every time the table is sent would rewrite objects nothing changed.</para></summary>
    internal static void Apply(IReadOnlyList<ReplicaPolicy> rows)
    {
        try
        {
            foreach (var row in rows)
            {
                if (row is null || row.Xml.Length == 0) continue;
                var list = List(row.Kind);
                if (list is null) { UnresolvedKind++; continue; }
                var existing = FindPolicy(list, row.Id);
                if (existing is null)
                {
                    existing = Create(row.Kind, row.Id, row.Label);
                    if (existing is null) { UnresolvedKind++; continue; }
                    list.Add(existing);
                    Created++;
                }
                if (!string.Equals(existing.label, row.Label, StringComparison.Ordinal)) existing.label = row.Label;
                var current = ScribeBridge.SaveOrNull(existing);
                if (string.Equals(current, row.Xml, StringComparison.Ordinal)) continue;
                if (ScribeBridge.Load(existing, row.Xml)) Loaded++;
                else ApplyFailures++;
            }
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] 方案表应用失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        sentSignature = null;
        lastPollTick = int.MinValue;
    }

    /// <summary>Rows for one kind, as `(id, label, xml)`. Skips a policy whose fragment cannot be produced: an empty
    /// fragment would read on the other end as "this policy is empty" rather than "not sent".</summary>
    private static IEnumerable<(int Id, string Label, string Xml)> Entries(string kind)
    {
        var list = List(kind);
        if (list is null) yield break;
        foreach (var item in list)
        {
            if (item is not Policy policy) continue;
            var xml = ScribeBridge.SaveOrNull(policy);
            if (xml is null || xml.Length == 0) continue;
            yield return (policy.id, policy.label ?? string.Empty, xml);
        }
    }

    /// <summary>The live list behind one kind, as the concrete `List<T>` the database exposes.</summary>
    private static System.Collections.IList? List(string kind)
    {
        var game = Current.Game;
        if (game is null) return null;
        switch (kind)
        {
            case "apparel": return game.outfitDatabase?.AllOutfits;
            case "drug": return game.drugPolicyDatabase?.AllPolicies;
            case "food": return game.foodRestrictionDatabase?.AllFoodRestrictions;
            case "reading": return game.readingPolicyDatabase?.AllReadingPolicies;
            default: return null;
        }
    }

    /// <summary>Per-row sizes, largest first. The probe prints these because the whole-table shape only makes sense
    /// if the total stays small: one policy measured 173035 B, and 27 of those would be the largest payload in the
    /// project by two orders of magnitude.</summary>
    /// <summary>What the table costs on the wire: the UTF-8 bytes of every fragment, before and after Brotli.
    ///
    /// <para>The transport compresses the whole frame (`HeadlessRuntime` logs both numbers), and these fragments are
    /// repetitive XML - so the raw size overstates the cost by however much the compressor wins. Measured here rather
    /// than assumed: "whole table" is only defensible with the compressed number in hand.</para></summary>
    internal static (long Raw, long Brotli) TableBytes()
    {
        long raw = 0;
        long compressed = 0;
        foreach (var kind in Kinds)
        {
            foreach (var (_, _, xml) in Entries(kind))
            {
                var bytes = Encoding.UTF8.GetBytes(xml);
                raw += bytes.Length;
                try
                {
                    using var buffer = new System.IO.MemoryStream();
                    using (var brotli = new System.IO.Compression.BrotliStream(buffer,
                        System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                        brotli.Write(bytes, 0, bytes.Length);
                    compressed += buffer.Length;
                }
                catch (Exception) { compressed += bytes.Length; }
            }
        }
        return (raw, compressed);
    }
    internal static IReadOnlyList<(string Kind, int Id, string Label, int Bytes)> Sizes()
    {
        var rows = new List<(string, int, string, int)>();
        foreach (var kind in Kinds)
            foreach (var (id, label, xml) in Entries(kind))
                rows.Add((kind, id, label, xml.Length));
        rows.Sort((left, right) => right.Item4.CompareTo(left.Item4));
        return rows;
    }
    private static Policy? FindPolicy(System.Collections.IList list, int id)
    {
        foreach (var candidate in list)
            if (candidate is Policy found && found.id == id) return found;
        return null;
    }

    /// <summary>Constructs the empty policy of the wanted kind with the wanted id.
    ///
    /// <para>Constructed rather than through `MakeNew…`, which allocates `max(id) + 1`: on a replica that is missing a
    /// policy in the middle of the table, `MakeNew…` would hand back a different id and the row would attach to the wrong
    /// identity - the same mistake as guessing a settlement id by its position (round 159).</para></summary>
    private static Policy? Create(string kind, int id, string label) => kind switch
    {
        "apparel" => new ApparelPolicy(id, label),
        "drug" => new DrugPolicy(id, label),
        "food" => new FoodPolicy(id, label),
        "reading" => new ReadingPolicy(id, label),
        _ => null,
    };

    /// <summary>A short stable hash, so the signature can be compared without holding every fragment in memory.</summary>
    private static string StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in text) hash = (hash ^ character) * 16777619u;
            return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal static string Summary()
    {
        var counts = new StringBuilder();
        foreach (var kind in Kinds)
            counts.Append(counts.Length == 0 ? string.Empty : " / ").Append(TableName(kind)).Append(' ')
                .Append(List(kind)?.Count ?? 0);
        return $"{counts}（发 {ChangesSent} 段 / 应用 新建 {Created} 载入 {Loaded}；上次整表 {LastRows} 行 {LastBytes} B"
            + $"；未知种类 {UnresolvedKind}、失败 {ApplyFailures}）";
    }

    private static string TableName(string kind) => kind switch
    {
        "apparel" => "服饰",
        "drug" => "药品",
        "food" => "饮食",
        "reading" => "阅读",
        _ => kind,
    };
}
