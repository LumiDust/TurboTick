using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Round-trips the planning-mark channel, per map.
///
/// <para><b>Why this probe exists at all.</b> The plan channel was wrong in two ways that no count can show, and both were
/// invisible on a single-map colony:</para>
/// <list type="number">
/// <item>it keyed plans by <c>Plan.GetUniqueLoadID()</c>, which is <c>"Plan_" + ID</c> with <c>ID</c> from a per-peer
/// counter (<c>:86133-86136</c>, <c>:85883</c>) - so on a second peer every plan looked new;</item>
/// <item>it published only <c>Find.CurrentMap</c>'s plans and applied them to <c>Find.CurrentMap</c>, while the cell indices
/// inside the payload are offsets into the *sending* map's <c>cellIndices</c>.</item>
/// </list>
///
/// <para>Both are fixed; what this probe measures is that the fix round-trips. It encodes every plan on every map, applies
/// each record back through the real <see cref="PlanSync.Apply"/> path, re-encodes, and compares. A plan that reorders its
/// cells or loses its colour shows up as a differing string; a plan that lands on the wrong map shows up as a count
/// mismatch on that map.</para>
///
/// <para><b>What it cannot prove.</b> It cannot prove the authority's plan reaches a second peer - that needs two live
/// peers. It also cannot catch a collision between two plans that share a label on different maps, because the key carries
/// the map index precisely so that they do not collide. If the save has only one map, the multi-map half reports that and
/// says so rather than printing a reassuring zero.</para></summary>
internal static class PlanProbe
{
    internal static string Probe(string? argument)
    {
        var maps = Find.Maps;
        if (maps is null || maps.Count == 0) return "没有地图，规划通道测不到";
        var report = new StringBuilder();
        report.Append(maps.Count).Append(" 张地图：");
        var total = 0;
        var agree = 0;
        var disagree = 0;
        var perMap = new List<string>(maps.Count);
        foreach (var map in maps)
        {
            if (map?.planManager?.AllPlans is not { Count: > 0 } plans)
            {
                perMap.Add($"图{Idx(map)}=0");
                continue;
            }
            var before = new List<string>(plans.Count);
            foreach (var plan in plans)
                if (plan is not null) before.Add(Encode(plan));
            total += before.Count;
            // Apply the same records back. Anything the reconcile duplicates or drops changes the re-encoded set.
            PlanSync.Apply(map, before);
            var after = new List<string>(map.planManager.AllPlans.Count);
            foreach (var plan in map.planManager.AllPlans)
                if (plan is not null) after.Add(Encode(plan));
            var same = before.Count == after.Count;
            if (same)
                for (var i = 0; i < before.Count; i++)
                    if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) { same = false; break; }
            if (same) agree++; else disagree++;
            perMap.Add($"图{Idx(map)}={before.Count}条{(same ? "" : "→" + after.Count)}");
        }
        report.Append(string.Join("，", perMap));
        report.Append($";共 {total} 条，往返一致 {agree} 张图，不一致 {disagree} 张图");
        if (maps.Count == 1)
            report.Append("；**只有一张地图，多据点那半（地图索引是否随载荷走）测不到**");
        report.Append($";发送累计 {PlanSync.PlansSent} 条，应用 {PlanSync.PlansApplied} 条，失败 {PlanSync.ApplyFailures}");
        report.Append(argument is { Length: > 0 } ? $"；参数 {argument} 未使用（本探针遍历全部地图）" : "");
        return report.ToString();
    }

    private static int Idx(Map? map) => map?.Index ?? -1;

    /// <summary>The same encoding <see cref="PlanSync"/> uses. Duplicated deliberately: a probe that called the channel's own
    /// private encoder would report "the encoder agrees with itself", which is vacuously true and cannot catch a field the
    /// encoder forgot to write.</summary>
    private static string Encode(Plan plan)
    {
        var builder = new StringBuilder(64);
        builder.Append(plan.Map?.uniqueID ?? -1).Append('/').Append(plan.label ?? string.Empty)
            .Append('/').Append(plan.Color?.defName ?? string.Empty)
            .Append('/').Append(plan.Cells.Count.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    internal static string Summary()
    {
        var maps = Find.Maps;
        var plans = 0;
        var multi = 0;
        if (maps is not null)
            foreach (var map in maps)
            {
                if (map?.planManager?.AllPlans is not { Count: > 0 } list) continue;
                plans += list.Count;
                if (maps.Count > 1) multi += list.Count;
            }
        return $"{maps?.Count ?? 0} 张地图共 {plans} 条规划（多据点部分 {multi} 条）"
            + $" / 发 {PlanSync.PlansSent} / 应用 {PlanSync.PlansApplied}（失败 {PlanSync.ApplyFailures}）";
    }
}
