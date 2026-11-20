using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Probe for the policy-table channel (round 160).
///
/// <para><b>What it proves.</b> A policy a player creates must exist on the other peer with the same id, label and
/// filters, because pawn trackers reference it. The probe therefore creates a real policy through the game's own
/// `MakeNewReadingPolicy`, force-publishes the table, then <b>deletes the policy locally</b> (the state a replica that
/// never received it is in) and calls the channel's `Apply` with the captured rows. If the policy comes back with the
/// same identity and the same fragment, the round trip is closed; if `Apply` creates it but the fragment differs, the
/// filters did not travel.</para>
///
/// <para><b>Why reading policies and not outfits.</b> Same shape, but `ReadingPolicy` has two filters (`defFilter`,
/// `effectFilter`) instead of one, so a fragment that silently drops a field shows up here and not on an outfit.</para>
///
/// <para><b>This is a channel reading, not a two-peer reading.</b> The replica half is simulated by deleting the object
/// and applying the captured rows in the same game - the same trick `ideo-lock-probe` uses. It cannot show what a second
/// peer *drew*; it shows that the row that would reach it is complete.</para></summary>
internal static class PolicyTableProbe
{
    internal static string Run(string? argument)
    {
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "reading";
        var game = Current.Game;
        if (game is null) return "没有运行中的游戏";
        var database = game.readingPolicyDatabase;
        if (database is null) return "本局没有阅读方案表";
        if (kind != "reading") return "这一轮只做阅读方案（其它三张表同一通道，形状相同）";
        var list = database.AllReadingPolicies;
        var before = list.Count;
        // A policy the game itself allocates: id = max(id) + 1 (`:361223`), label = "ReadingPolicy N".
        var created = database.MakeNewReadingPolicy();
        if (created is null || list.Count != before + 1) return "没有新建出方案（MakeNewReadingPolicy 返回空）";
        var id = created.id;
        var label = created.label ?? string.Empty;
        // `defFilter` starts as allow-everything and `effectFilter` as the BookEffects special filter; a policy whose
        // filters never travelled would still look "created" with these defaults, so disallow one def and compare the
        // whole fragment rather than a flag.
        created.defFilter.SetAllowAll(null, includeNonStorable: true);
        var marker = DefDatabase<ThingDef>.AllDefsListForReading
            .FirstOrDefault(def => def?.thingClass is not null && def.thingClass == typeof(Book));
        if (marker is not null) created.defFilter.SetAllow(marker, allow: false);
        var fragment = ScribeBridge.SaveOrNull(created) ?? string.Empty;
        PolicyTableSync.Reset();
        var rows = PolicyTableSync.Publish();
        var carried = rows?.FirstOrDefault(row => row.Kind == "reading" && row.Id == id);
        var carriedBytes = carried?.Xml.Length ?? 0;
        var carriedIdentical = carried is not null && string.Equals(carried.Xml, fragment, StringComparison.Ordinal);
        // The replica's state: the policy was never received, so it does not exist.
        var deleted = database.TryDelete(created);
        var gone = list.Count == before;
        var applied = 0;
        if (carried is not null)
        {
            PolicyTableSync.Apply([carried]);
            applied = list.Count - before;
        }
        var back = list.FirstOrDefault(policy => policy.id == id);
        var backFragment = back is null ? string.Empty : ScribeBridge.SaveOrNull(back) ?? string.Empty;
        var sameFragment = back is not null && string.Equals(backFragment, fragment, StringComparison.Ordinal);
        var backLabel = back?.label ?? "(没有)";
        var message = new StringBuilder();
        message.Append($"阅读方案 {before} 个，新建 #{id}「{label}」：");
        message.Append($"发 {rows?.Count ?? 0} 行，本行 {(carried is null ? "未带上" : carriedBytes + " B")}、与原文{(carriedIdentical ? "一致" : "不一致")}；");
        message.Append($"删掉本地副本（{(deleted.Accepted ? "成功" : "失败：" + deleted.Reason)}，表{(gone ? "回到 " + before : "仍有 " + list.Count)} 个）；");
        message.Append($"应用该行 回来了 {applied} 个：#{id}「{backLabel}」片段{(sameFragment ? "一致" : "不一致")}");
        // The fixture must not survive the probe: a policy left behind changes the table every later reading counts.
        if (back is not null)
        {
            var removed = database.TryDelete(back);
            message.Append($"；清理{(removed.Accepted ? "成功" : "失败：" + removed.Reason)}（表 {list.Count} 个）");
        }
        message.Append('；').Append(PolicyTableSync.Summary());
        return message.ToString();
    }

    /// <summary>Row sizes, largest first, with the total. Answers "can the whole table travel" with a number instead
    /// of a guess: `reading`'s single row measured 173035 B, but that row was built by the probe's own `SetAllowAll`,
    /// so the game's own policies have to be measured on their own.</summary>
    internal static string Size()
    {
        var rows = PolicyTableSync.Sizes();
        if (rows.Count == 0) return "本局没有任何方案片段（Scribe 忙或四张表都空）";
        long total = 0;
        var perKind = new Dictionary<string, (int Rows, long Bytes)>();
        foreach (var row in rows)
        {
            total += row.Bytes;
            perKind.TryGetValue(row.Kind, out var sum);
            perKind[row.Kind] = (sum.Rows + 1, sum.Bytes + row.Bytes);
        }
        var message = new StringBuilder();
        message.Append($" {rows.Count} 行合计 {total} B（{total / 1024} KiB）：");
        foreach (var pair in perKind)
            message.Append($"{pair.Key} {pair.Value.Rows} 行 {pair.Value.Bytes} B；");
        message.Append("最大的五行：");
        foreach (var row in rows.Take(5))
            message.Append($"{row.Kind}#{row.Id}「{row.Label}」{row.Bytes} B；");
        var wire = PolicyTableSync.TableBytes();
        message.Append($"线上代价：原文 {wire.Raw} B / Brotli 后 {wire.Brotli} B；");
        message.Append(PolicyTableSync.Summary());
        return message.ToString();
    }
    /// <summary>The table as it stands, one line per kind - used to see what a peer would be told without creating
    /// anything.</summary>
    internal static string List()
    {
        var game = Current.Game;
        if (game is null) return "没有运行中的游戏";
        var message = new StringBuilder();
        void Row(string kind, string title, IEnumerable<(int Id, string Label)> rows)
        {
            message.Append(message.Length == 0 ? string.Empty : "；").Append(title).Append(' ')
                .Append(string.Join(",", rows.Select(row => "#" + row.Id + "「" + row.Label + "」")));
        }
        Row("apparel", "服饰", (game.outfitDatabase?.AllOutfits ?? []).Select(p => (p.id, p.label ?? "")));
        Row("drug", "药品", (game.drugPolicyDatabase?.AllPolicies ?? []).Select(p => (p.id, p.label ?? "")));
        Row("food", "饮食", (game.foodRestrictionDatabase?.AllFoodRestrictions ?? []).Select(p => (p.id, p.label ?? "")));
        Row("reading", "阅读", (game.readingPolicyDatabase?.AllReadingPolicies ?? []).Select(p => (p.id, p.label ?? "")));
        message.Append('；').Append(PolicyTableSync.Summary());
        return message.ToString();
    }
}
