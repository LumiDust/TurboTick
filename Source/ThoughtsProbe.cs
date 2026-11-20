using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Reports the two halves of `ThoughtHandler`, which are produced by completely different code and fail
/// independently.
///
/// <para><b>The two halves.</b> `ThoughtHandler.GetAllMoodThoughts` (`:482064`) clears the output list, copies every
/// `memories.Memories` entry whose `MoodOffset() != 0`, and then calls
/// `situational.AppendMoodThoughts(outThoughts)`. The first half is data the replica receives - `ApplyThoughts`
/// (`Source/AuthoritativeReplica.cs:2612`) rebuilds it from `ReplicaPawn.Thoughts`. The second half is <em>derived</em>:
/// `AppendMoodThoughts` (`:481793`) recomputes `cachedThoughts` through `TryCreateThought` ->
/// `def.Worker.CurrentState(pawn)` whenever `thoughtsDirty`, and otherwise yields whatever it cached last time.</para>
///
/// <para><b>Why the second half can go stale on a replica.</b> `thoughtsDirty` starts `true` and is only ever set again
/// by `Notify_SituationalThoughtsDirty()` (`:482004`), whose callers are all reactions to something changing
/// (a hediff added, a precept gained, a pawn died). `UpdateAllMoodThoughts` sets it `false` at its first line. On a
/// replica nothing ticks: `ThoughtHandler.ThoughtInterval` (`:482044`, from `Need_Mood.NeedInterval` at `:466955`) is
/// the scheduler that would recompute every `RecalculateStateEveryTicks`, and it never runs. So after the first read
/// the cache is frozen at whatever the pawn looked like then.</para>
///
/// <para><b>What this probe therefore has to separate.</b> "Zero situational thoughts" has three causes with three
/// different fixes: nothing is dirty and the cache is empty (never computed), the cache holds entries that are all
/// `Active == false` (computed, genuinely none), or `thoughtsDirty` is still true (every reader will recompute, which
/// is correct but means the cache is not being used at all). Reporting only the count would conflate them.</para>
///
/// <para>The mood number itself is <em>not</em> derived here: `ApplyNeeds` writes `mood.CurLevel` straight from the
/// frame, so a reader that shows a different mood is showing the authority's value and the thought list is only an
/// explanation of it.</para></summary>
internal static class ThoughtsProbe
{
    private static readonly AccessTools.FieldRef<SituationalThoughtHandler, List<Thought_Situational>> Cached =
        AccessTools.FieldRefAccess<SituationalThoughtHandler, List<Thought_Situational>>("cachedThoughts");

    private static readonly AccessTools.FieldRef<SituationalThoughtHandler, bool> Dirty =
        AccessTools.FieldRefAccess<SituationalThoughtHandler, bool>("thoughtsDirty");

    internal static string Summary()
    {
        var pawns = 0;
        var memories = 0;
        var situational = 0;
        var active = 0;
        var dirtyPawns = 0;
        var threw = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                var thoughts = pawn?.needs?.mood?.thoughts;
                if (thoughts is null) continue;
                pawns++;
                memories += thoughts.memories?.Memories?.Count ?? 0;
                var handler = thoughts.situational;
                if (handler is null) continue;
                try
                {
                    if (Dirty(handler)) dirtyPawns++;
                    var list = Cached(handler);
                    if (list is null) continue;
                    situational += list.Count;
                    for (var i = 0; i < list.Count; i++)
                        if (list[i]?.Active == true) active++;
                }
                catch (Exception) { threw++; }
            }
        return "本图 " + pawns + " 个有心情追踪的小人；记忆条目 " + memories + " 条；"
            + "情境缓存 " + situational + " 条（其中 Active " + active + " 条）；"
            + "缓存待重算的小人 " + dirtyPawns + " 个" + (threw > 0 ? "；读取失败 " + threw + " 个" : "");
    }

    /// <summary>One pawn, both halves, plus what the panel would actually list.
    ///
    /// <para>Three numbers have to be reported together because each alone is misleading: the raw memory count (what the
    /// channel sent), the situational cache (what was derived and when), and `GetAllMoodThoughts` (what the needs tab
    /// iterates). A pawn can show a full memory list and an empty panel, or the reverse.</para>
    ///
    /// <para>`argument` is a pawn id or empty for the first colonist.</para></summary>
    internal static string Probe(string? argument)
    {
        Pawn? pawn = null;
        if (!string.IsNullOrEmpty(argument)
            && int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == id);
        pawn ??= Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).FirstOrDefault();
        var thoughts = pawn?.needs?.mood?.thoughts;
        if (pawn is null || thoughts is null) return "没有可用的小人";

        var report = new StringBuilder();
        report.Append("小人 #").Append(pawn.thingIDNumber).Append('「').Append(pawn.LabelShortCap).Append("」：");

        var memoryList = thoughts.memories?.Memories;
        report.Append("记忆 ").Append(memoryList?.Count ?? 0).Append(" 条（面板计入 ")
            .Append(memoryList is null ? 0 : memoryList.Count(m => m is not null && m.MoodOffset() != 0f)).Append(" 条）");

        var handler = thoughts.situational;
        if (handler is null) return report.Append("；无情境处理器").ToString();
        var wasDirty = Dirty(handler);
        var cached = Cached(handler);
        var before = cached?.Count ?? 0;
        var beforeActive = cached is null ? 0 : cached.Count(t => t?.Active == true);
        report.Append("；情境缓存 ").Append(before).Append(" 条（Active ").Append(beforeActive)
            .Append(" 条，读取前 thoughtsDirty=").Append(wasDirty).Append(')');

        // What the needs tab would iterate *right now*. This is the only number that matters to a player, and it is the
        // one that differs from the two above whenever the two halves disagree.
        var listed = new List<Thought>();
        thoughts.GetAllMoodThoughts(listed);
        report.Append("；面板会列出 ").Append(listed.Count).Append(" 条：")
            .Append(listed.Count == 0 ? "无" : string.Join("、", listed.Take(8).Select(t => t.def?.defName ?? "?")));
        if (listed.Count > 8) report.Append(" …");

        // The write is what separates "the cache is stale" from "the cache is being kept current": a second read after
        // a forced invalidation must agree with the first only if nothing changed on this peer since.
        var afterDirty = Dirty(handler);
        var after = Cached(handler)?.Count ?? 0;
        report.Append("；读取后 thoughtsDirty=").Append(afterDirty).Append("、缓存 ").Append(after).Append(" 条");
        if (wasDirty && !afterDirty) report.Append("（本次读取触发了重算）");
        if (!wasDirty && afterDirty) report.Append("（读取没有改变脏标记，缓存仍是旧的）");

        // `MoodOffsetOfGroup` is what the tooltip sums; `CurLevel` is what the bar shows. Reporting both makes a
        // thought-list divergence visible without having to open the panel.
        report.Append("；mood.CurLevel=").Append(pawn.needs.mood.CurLevel.ToString("0.00", CultureInfo.InvariantCulture))
            .Append("、TotalMoodOffset=").Append(thoughts.TotalMoodOffset().ToString("0.00", CultureInfo.InvariantCulture));
        return report.ToString();
    }

    /// <summary>Marks every spawned pawn's situational thoughts for recomputation, the way `NeedInterval` would on a
    /// peer that ticks.
    ///
    /// <para><b>Why this is not a hack.</b> `Notify_SituationalThoughtsDirty` (`:482004`) has exactly one job: drop the
    /// cache so the next reader recomputes. On a ticking peer `ThoughtHandler.ThoughtInterval` (`:482044`) does it as
    /// part of `Need_Mood.NeedInterval`; the replica has no such schedule, so the same invalidation has to come from the
    /// frame path. The cost is one `List.Clear` per pawn plus the recompute that the next reader would have done
    /// anyway.</para>
    ///
    /// <para>Exposed as a command so the effect is measurable: run it, read `thoughts`, and the cached count must move
    /// on a pawn whose situational inputs changed since the last read.</para></summary>
    internal static string Refresh(string? argument)
    {
        var pawns = 0;
        var dirty = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                var handler = pawn?.needs?.mood?.thoughts?.situational;
                if (handler is null) continue;
                pawns++;
                try { handler.Notify_SituationalThoughtsDirty(); dirty++; }
                catch (Exception) { }
            }
        return "已标记重算：遍历 " + pawns + " 个，标脏 " + dirty + " 个"
            + (string.IsNullOrEmpty(argument) ? "" : "（参数 " + argument + " 忽略：情境想法按小人独立缓存）");
    }
}
