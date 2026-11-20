using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The quest acceptance and ending the host accumulated, applied on a replica's own <c>Quest</c> objects.
///
/// <para><b>Why a replica needs this.</b> A quest is accepted (<c>Quest.Accept</c> sets <c>acceptanceTick = TicksGame</c> and
/// calls <c>Initiate</c>) and ends (<c>Quest.End</c> sets <c>ended</c>/<c>endOutcome</c> and runs <c>CleanupQuestParts</c>) on
/// the host. Neither is a tick a replica runs, so its quest page keeps showing whatever the save was loaded with - an offer
/// that was accepted a season ago still reads as pending, and a finished quest still reads as ongoing, with every reward and
/// failure the quest log would have shown.</para>
///
/// <para><b>Why the state is sent as its inputs rather than as <c>QuestState</c>.</b> <c>Quest.State</c> is a getter over
/// <c>ended</c>, <c>endOutcome</c>, <c>acceptanceTick</c> and <c>TicksUntilExpiry</c> - five branches, and the expiry one moves
/// on its own as the clock advances. Sending the enum would bake in one reading and go stale on the next tick; sending the
/// inputs lets the replica's own getter answer the same question the same way.</para>
///
/// <para><b>Why the quest is never rebuilt here.</b> <c>QuestManager.Add</c> calls <c>PostQuestAdded</c>, <c>Initiate</c>,
/// registers signal receivers and notifies every <c>QuestPart</c>. Those are gameplay consequences, and the quest object
/// already exists on the replica because it came in with the save. This only writes the four fields the passing of time - not
/// the game rules - would have written.</para>
///
/// <para><b>Why <c>id</c>.</b> <c>Quest.id</c> is scribed (<c>Scribe_Values.Look(ref id, "id", 0)</c>) and quests are
/// addressed by it throughout (<c>Quest{id}.Added</c>, <c>Quest{id}.Initiate</c> signals), so it is the same on both ends
/// unlike, say, a room id.</para></summary>
internal static class QuestSync
{
    /// <summary>Last standing sent per quest id, so a frame carries only what moved.</summary>
    private static readonly Dictionary<int, QuestStanding> sent = [];

    internal static long QuestsSent { get; private set; }
    internal static long QuestsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    private static readonly AccessTools.FieldRef<Quest, bool> endedField =
        AccessTools.FieldRefAccess<Quest, bool>("ended");
    private static readonly AccessTools.FieldRef<Quest, QuestEndOutcome> outcomeField =
        AccessTools.FieldRefAccess<Quest, QuestEndOutcome>("endOutcome");
    private static readonly AccessTools.FieldRef<Quest, int> expireField =
        AccessTools.FieldRefAccess<Quest, int>("acceptanceExpireTick");
    private static QuestStanding Standing(Quest quest) => new(
        quest.acceptanceTick,
        endedField(quest) ? (byte)1 : (byte)0,
        (byte)outcomeField(quest),
        expireField(quest),
        quest.dismissed ? (byte)1 : (byte)0);

    /// <summary>Every quest whose standing differs from the last one sent.</summary>
    internal static List<ReplicaQuest>? Publish()
    {
        try
        {
            var manager = Find.QuestManager;
            if (manager is null) return null;
            var quests = manager.QuestsListForReading;
            if (quests is null || quests.Count == 0) return null;
            List<ReplicaQuest>? result = null;
            var alive = new HashSet<int>();
            foreach (var quest in quests)
            {
                if (quest is null || quest.Historical) continue;
                alive.Add(quest.id);
                var standing = Standing(quest);
                if (sent.TryGetValue(quest.id, out var previous) && previous == standing) continue;
                sent[quest.id] = standing;
                (result ??= []).Add(new ReplicaQuest(quest.id, standing.AcceptTick, standing.Ended,
                    standing.Outcome, standing.ExpireTick, standing.Dismissed));
                QuestsSent++;
            }
            // A quest removed from the manager is one the player can no longer open; keeping the entry only makes the
            // dictionary grow for the life of the process.
            foreach (var stale in sent.Keys.Where(id => !alive.Contains(id)).ToList()) sent.Remove(stale);
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] quest publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes one quest's standing onto the replica's own quest object.</summary>
    internal static void Apply(ReplicaQuest state)
    {
        try
        {
            var manager = Find.QuestManager;
            if (manager is null) return;
            Quest? quest = null;
            foreach (var each in manager.QuestsListForReading)
            {
                if (each is null || each.id != state.Id) continue;
                quest = each;
                break;
            }
            // A quest the replica does not have came from a later world state; rebuilding it here would replay every
            // QuestPart, so it is left alone and the next full state carries it like any other saved object.
            if (quest is null || quest.Historical) return;
            var changed = false;
            // `acceptanceTick` is public, so this needs no reflection.
            if (quest.acceptanceTick != state.AcceptTick && state.AcceptTick >= -1)
            {
                quest.acceptanceTick = state.AcceptTick;
                changed = true;
            }
            if (endedField(quest) != (state.Ended != 0))
            {
                endedField(quest) = state.Ended != 0;
                changed = true;
            }
            if (state.Outcome <= (byte)QuestEndOutcome.InvalidPreAcceptance
                && (byte)outcomeField(quest) != state.Outcome)
            {
                outcomeField(quest) = (QuestEndOutcome)state.Outcome;
                changed = true;
            }
            if (expireField(quest) != state.ExpireTick && state.ExpireTick >= -1)
            {
                expireField(quest) = state.ExpireTick;
                changed = true;
            }
            // `dismissed` is a public field, so this needs no reflection either.
            if (quest.dismissed != (state.Dismissed != 0))
            {
                quest.dismissed = state.Dismissed != 0;
                changed = true;
            }
            if (changed) QuestsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] quest apply failed ({state.Id}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => sent.Clear();

    internal static string Summary()
    {
        var total = 0;
        var active = 0;
        var displayed = 0;
        var historical = 0;
        var failure = string.Empty;
        try
        {
            var manager = Find.QuestManager;
            total = manager?.QuestsListForReading?.Count ?? 0;
            active = manager?.ActiveQuestsListForReading?.Count ?? 0;
            displayed = manager?.questsInDisplayOrder?.Count ?? 0;
            // `historicalQuests` is private, but every quest knows its own flag; counting through `QuestsListForReading`
            // would report the same number as `total` and prove nothing.
            foreach (var quest in manager?.QuestsListForReading ?? [])
                if (quest is not null && quest.Historical) historical++;
        }
        catch (Exception error) { failure = "读取失败（" + error.GetType().Name + "）"; }
        // Four counts, because "0 quests" is the reading for a save that never generated one and for a wrong list. The
        // display order is what the quest tab actually draws, so it is the one that matters: an empty one with a non-empty
        // total would mean quests exist that the player cannot see.
        return $"{total} 个（进行中 {active}，历史 {historical}，显示序 {displayed}）"
            + $"{failure}/ 发 {QuestsSent} / 应用 {QuestsApplied}（失败 {ApplyFailures}）";
    }
}

/// <summary>One quest's time-driven standing, kept for comparison so only real changes reach the wire.</summary>
internal readonly record struct QuestStanding(int AcceptTick, byte Ended, byte Outcome, int ExpireTick, byte Dismissed);
