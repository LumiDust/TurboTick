using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The counters that live on a <see cref="QuestPart"/>, for a replica that never ticks a quest.
///
/// <para><b>The defect.</b> <c>Quest.QuestTick</c> (`:363425`) walks every enabled part and calls
/// <c>QuestPartActivable.QuestPartTick</c>; it is reached only from <c>QuestManager.QuestManagerTick</c> (`:363923`),
/// which is one of the components of <c>Map.MapPostTick</c> (`:52988`) - and a display replica is held at the tick gate
/// before that. So every counter inside a quest part stays at the value the save was loaded with.</para>
///
/// <para><b>What reads them.</b> The quest tab itself: <c>Window_Quests</c> prints
/// <c>tmpQuestParts[num].DescriptionPart</c> for every enabled part (`:672966`), and those descriptions are literally
/// "<c>PlantsHarvested: 5 / 20</c>" and its relatives - built from the private counters. A replica therefore shows a quest
/// whose text never moves while the host's does.</para>
///
/// <para><b>Why the parts travel as one Scribe fragment rather than as named counters.</b> There are fifteen part types
/// with counters in this gap list and dozens more in total, each with its own field names, and the set grows with every
/// DLC. <c>Quest.ExposeData</c> already writes all of them (<c>Scribe_Collections.Look(ref parts, "parts", LookMode.Deep)</c>,
/// `:363700`) and each part's own <c>ExposeData</c> is where the developers maintain that list - so reusing it means this
/// channel never needs to learn a new field name. Naming the counters would be the hand-written table failure again: green
/// over the fields it knows and silent over the rest.</para>
///
/// <para><b>Why a whole-quest fragment and not a per-part one.</b> Parts are addressed by their index inside
/// <c>parts</c>, and that index is stable across peers (both sides loaded the same save). Re-serialising the whole quest
/// also covers the fields on <c>Quest</c> itself, so the change key catches a part being added or removed for free - which a
/// per-part scheme would have to detect separately to avoid pairing a new part with an old row.</para>
///
/// <para><b>Why only the parts list is compared.</b> <c>Quest.ExposeData</c> also writes acceptance and cleanup stamps,
/// but those are <see cref="QuestSync"/>'s job; sending the same fact down two channels lets them disagree. The fragment
/// here is filtered down to the <c>parts</c> element so the two channels cannot fight.</para></summary>
internal static class QuestPartSync
{
    /// <summary>Last `<parts>` fragment per quest id, so an unchanged quest costs nothing.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static long PartsSent { get; private set; }
    internal static long PartsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>The last fragment published, so the census can print the encoded shape rather than only a count.</summary>
    internal static string? LastSample { get; private set; }
    /// <summary>Why the last publish produced no fragment, or null when it produced one. A channel that silently sends
    /// nothing is the one failure mode a count cannot show: "0 sent" reads the same whether there was nothing to send or
    /// the sender could not build a payload.</summary>
    internal static string? LastDiagnostic { get; private set; }
    /// <summary>How many quest fragments were actually built, and how many of those had an empty parts list. Printed so the
    /// three ways of sending nothing - no quest, no fragment, empty list - cannot be mistaken for one another.
    ///
    /// <para>Also incremented by the census, which builds one fragment for its sample. So this is "fragments built", not
    /// "fragments sent" - `PartsSent` is the number that means the second thing.</para></summary>
    internal static long FragmentsBuilt { get; private set; }
    internal static long EmptyLists { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        PartsSent = 0;
        PartsApplied = 0;
        ApplyFailures = 0;
        LastSample = null;
        LastDiagnostic = null;
        FragmentsBuilt = 0;
        EmptyLists = 0;
    }

    internal static List<ReplicaQuestParts>? Publish()
    {
        List<ReplicaQuestParts>? changed = null;
        try
        {
            var manager = Find.QuestManager;
            if (manager?.QuestsListForReading is not { Count: > 0 } quests) return null;
            var live = new HashSet<int>();
            foreach (var quest in quests)
            {
                if (quest is null) continue;
                var id = quest.id;
                live.Add(id);
                LastDiagnostic = null;
                var fragment = Fragment(quest);
                if (fragment is null)
                {
                    if (sent.Remove(id)) (changed ??= []).Add(new ReplicaQuestParts(id, string.Empty));
                    continue;
                }
                if (sent.TryGetValue(id, out var previous) && previous == fragment) continue;
                sent[id] = fragment;
                (changed ??= []).Add(new ReplicaQuestParts(id, fragment));
                PartsSent++;
                LastSample = fragment;
            }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 任务部件发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>The `<parts>…</parts>` slice of the quest's own Scribe fragment, or null when there is nothing to send.
    ///
    /// <para>Cut by tag rather than by re-serialising the list: `Scribe_Collections.Look` writes one element per part and
    /// the element name depends on the mode, so parsing the string is the only way to lift it out without rebuilding the
    /// list - and rebuilding is exactly what a display replica must not do.</para></summary>
    private static string? Fragment(Quest quest)
    {
        var xml = ScribeBridge.Save(quest, "quest");
        if (xml is not { Length: > 0 }) { LastDiagnostic = "保存返回空"; return null; }
        // The opening tag may or may not carry attributes depending on the mode, so the name is located and the end of the
        // tag is then found rather than assuming a bare `<parts>`.
        var open = xml.IndexOf("<parts", StringComparison.Ordinal);
        if (open < 0)
        {
            LastDiagnostic = "片段里没有 parts 节点：" + xml.Substring(0, Math.Min(160, xml.Length)).Replace('\n', ' ');
            return null;
        }
        var tagEnd = xml.IndexOf('>', open);
        if (tagEnd < 0) { LastDiagnostic = "parts 开标签没有闭合"; return null; }
        var close = xml.LastIndexOf("</parts>", StringComparison.Ordinal);
        if (close < tagEnd) { LastDiagnostic = "parts 没有闭标签"; return null; }
        var slice = xml.Substring(open, close + "</parts>".Length - open);
        FragmentsBuilt++;
        if (slice.Length <= "<parts></parts>".Length) EmptyLists++;
        // An empty list is a real state - a quest whose parts were all consumed - and has to be told once, or the replica
        // keeps drawing the last description it saw.
        return slice;
    }

    internal static void Apply(ReplicaQuestParts state)
    {
        try
        {
            var manager = Find.QuestManager;
            if (manager?.QuestsListForReading is not { Count: > 0 } quests) return;
            Quest? quest = null;
            foreach (var candidate in quests)
                if (candidate?.id == state.Quest) { quest = candidate; break; }
            if (quest is null) return;
            if (state.Parts.Length == 0)
            {
                // Nothing to restore: the parts are the authority's, and this peer only mirrors their counters. An empty
                // row exists so the frame's `hadEvents` fires; the values are already gone with the parts themselves.
                PartsApplied++;
                return;
            }
            if (!ScribeBridge.Load(quest, "<quest>" + state.Parts + "</quest>"))
            {
                ApplyFailures++;
                return;
            }
            // `Quest.ExposeData`'s LoadingVars branch normally re-links each part to its quest (`:363703`); the load above
            // runs the same branch, so nothing is needed here - but a part whose `quest` is null would throw on its next
            // `QuestPartTick`, so it is checked rather than assumed.
            foreach (var part in quest.PartsListForReading)
                if (part is not null && part.quest is null) part.quest = quest;
            PartsApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    /// <summary>How many counters actually differ from the last frame, so the census can show the channel is live rather
    /// than merely present.</summary>
    internal static string Summary()
    {
        try
        {
            var manager = Find.QuestManager;
            var quests = manager?.QuestsListForReading;
            if (quests is not { Count: > 0 }) return "0 个任务 / 发 0";
            var withParts = 0;
            var totalParts = 0;
            foreach (var quest in quests)
            {
                if (quest?.PartsListForReading is not { Count: > 0 } parts) continue;
                withParts++;
                totalParts += parts.Count;
            }
            // The sample is taken from the live list, not from `LastSample`: that field only remembers the last quest that
            // CHANGED, so on a frame where nothing changed the report would print no payload at all and look like a
            // channel that stopped sending. What the reader needs to see is that the counters are in the fragment.
            var sample = Fragment(quests.Find(quest => quest?.PartsListForReading is { Count: > 0 })) is { Length: > 0 } now
                ? " / 当前 " + Condense(now)
                : LastDiagnostic is { Length: > 0 } why ? " / **未发出：" + why + "**" : string.Empty;
            return $"{quests.Count} 个任务（{withParts} 个带部件，共 {totalParts} 个）"
                + $" / 建 {FragmentsBuilt} 片段（空表 {EmptyLists}）"
                + $" / 发 {PartsSent} / 应用 {PartsApplied}（失败 {ApplyFailures}）{sample}";
        }
        catch (Exception error) { return "读取失败（" + error.GetType().Name + "）"; }
    }

    /// <summary>One line of the fragment, truncated: the point is to show that the counters are in there, not to dump a
    /// kilobyte of XML into the report.</summary>
    private static string Condense(string xml)
    {
        var builder = new StringBuilder(Math.Min(xml.Length, 160));
        foreach (var line in xml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("<saveable", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("<loadID", StringComparison.Ordinal)) continue;
            if (builder.Length + trimmed.Length + 1 > 150) break;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(trimmed);
        }
        return builder.Length == 0 ? "(空)" : builder.ToString();
    }
}
