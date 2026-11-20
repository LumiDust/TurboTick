using System;
using System.Collections.Generic;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>War and alliance between settlements, and what each does to the grant table.
///
/// <para><b>Why war is a grant modifier rather than a separate rule.</b> The gates already ask one question - "what grants
/// does this pawn hold here" - and every answer goes through <c>GrantsForPawn</c>. War is the statement "no grants at all,
/// regardless of the table"; expressing it anywhere else would mean a second condition threaded through every gate, and a
/// gate that forgot it would let an enemy build a wall while the others refused. ColonyTogether puts it in the same place:
/// <c>ColonyWar.Allied</c> is consulted from the ownership context, and its grant check asks for the alliance before it
/// asks for the permission (ct045.decompiled.cs:116984).</para>
///
/// <para><b>Why an alliance is required rather than assumed.</b> Two settlements in the same world are not allies by
/// default: a neutral neighbour is exactly the visitor the default grants describe. Requiring an explicit pact is what makes
/// "we are friends" a decision somebody made, which is the whole point of having the record.</para>
///
/// <para><b>Where the notice period belongs (round 94, correcting round 80).</b> Round 80 put <c>NoticeTicks = 60000</c>
/// between "declare" and "in effect", and wrote in the comment that ColonyTogether does the same. <b>It does not.</b> CT's
/// <c>AtWar</c> is <c>Between(data, a, b) != null</c> - the war is live the instant the record exists
/// (<c>ct045:116984-116991</c>). <c>NoticeTicks</c> sits on a different edge: dissolving an alliance sets
/// <c>pact.NoticeUntil = now + 60000</c> (<c>ct045:117294</c>), and declaring war is refused while that countdown runs
/// (<c>ct045:117184</c>: <c>NoticeLeft(data, own, other) &gt; 0</c>). The delay is on <i>going to war with a former ally</i>,
/// not on <i>war taking effect</i>.
///
/// <para>That difference is not cosmetic. With the notice on the declaration edge, a war is unreachable for the first
/// 60 000 ticks of a world - and an observed headless world was at tick 358. Every reading printed 「通知中」 and the gates
/// never saw a single refusal, so the war path was never measured at all. The correct edge is measurable immediately and
/// says something a player can act on: you cannot attack the ally you just left, instantly.</para></summary>
/// <summary>A pact between two settlements.
///
/// <para><see cref="Allied"/> and <see cref="AtWar"/> are mutually exclusive by construction: declaring war clears the
/// alliance, and making peace clears the war. Two flags on one record rather than two lists, because "at war and allied"
/// is a state the gates would have to resolve arbitrarily.</para></summary>
public sealed class SettlementPact : IExposable
{
    public int A;
    public int B;
    /// <summary>Set while the two are at war; -1 means no war. This is <b>not</b> a countdown: war is live as soon as it
    /// is set. It is kept because the report says how long the war has run, and because a peace offer that arrives
    /// years later should be readable as such.</summary>
    public int WarSince = -1;
    public bool Allied;

    /// <summary>The tick a dissolved alliance stops blocking a declaration of war, or 0 when nothing is counting down.
    /// Mirrors ColonyTogether's <c>ColonyPactRecord.NoticeUntil</c> (<c>ct045:369113</c>), which is scribed as
    /// <c>"noticeUntil"</c> and means exactly this.</summary>
    public int NoticeUntil;

    public bool Between(int x, int y) => (A == x && B == y) || (A == y && B == x);

    /// <summary>One day at normal speed. Same value and same meaning as ColonyTogether's <c>NoticeTicks</c>
    /// (<c>ct045:116951</c>) and <c>60000</c> at <c>ct045:117294</c>: the wait before a former ally may be attacked.</summary>
    public const int Notice = 60000;

    /// <summary>Whether a war is live. No countdown: see the class comment for the round-80 mistake this replaces.</summary>
    public bool WarActive() => WarSince >= 0;

    /// <summary>Ticks left before war may be declared against a settlement this one just left an alliance with.</summary>
    public int NoticeLeft(int now) => NoticeUntil > now ? NoticeUntil - now : 0;

    public void ExposeData()
    {
        Scribe_Values.Look(ref A, "a", 0);
        Scribe_Values.Look(ref B, "b", 0);
        Scribe_Values.Look(ref WarSince, "warSince", -1);
        Scribe_Values.Look(ref Allied, "allied", false);
        Scribe_Values.Look(ref NoticeUntil, "noticeUntil", 0);
    }
}

internal static class SettlementWar
{
    /// <summary>One day at normal speed. Long enough that leaving an alliance is a decision with a cost, and aligned with
    /// ColonyTogether's value so a player who has used either mod reads the same number.</summary>
    internal const int NoticeTicks = SettlementPact.Notice;

    /// <summary>Whether two settlements are allied. Used by the gates to decide whether a grant applies at all.</summary>
    internal static bool Allied(SettlementData? data, int a, int b)
    {
        if (data is null || !data.Active || a == 0 || b == 0 || a == b) return false;
        for (var i = 0; i < data.Pacts.Count; i++)
            if (data.Pacts[i] is { } pact && pact.Between(a, b)) return pact.Allied;
        return false;
    }

    /// <summary>Whether two settlements are at war. Live from the declaration, like ColonyTogether's
    /// <c>Between(...) != null</c>.</summary>
    internal static bool AtWar(SettlementData? data, int a, int b)
    {
        if (data is null || !data.Active || a == 0 || b == 0 || a == b) return false;
        for (var i = 0; i < data.Pacts.Count; i++)
            if (data.Pacts[i] is { } pact && pact.Between(a, b)) return pact.WarActive();
        return false;
    }

    /// <summary>Ticks before war may be declared, counting down from a dissolved alliance. Zero is the normal answer: two
    /// settlements that were never allied can fight at once.</summary>
    internal static int NoticeLeft(SettlementData? data, int a, int b)
    {
        if (data is null || !data.Active || a == 0 || b == 0 || a == b) return 0;
        var now = Find.TickManager?.TicksGame ?? 0;
        for (var i = 0; i < data.Pacts.Count; i++)
            if (data.Pacts[i] is { } pact && pact.Between(a, b)) return pact.NoticeLeft(now);
        return 0;
    }

    /// <summary>Declares war. Takes effect immediately, unless this pair just dissolved an alliance and the notice has not
    /// run out - which is ColonyTogether's rule and its reason (<c>ct045:117184</c>): the point of an alliance is that
    /// leaving it costs you a day's warning to the other side, not that your own attack is delayed when there was no
    /// alliance to leave.</summary>
    internal static string Declare(SettlementData? data, int a, int b)
    {
        if (data is null || !data.Active) return "没有据点";
        if (a == 0 || b == 0) return "需要指定两方";
        if (a == b) return "不能对自己宣战";
        var pact = Ensure(data, a, b);
        if (pact.WarSince >= 0) return $"已经在战争中（自 tick {pact.WarSince} 起）";
        var left = NoticeLeft(data, a, b);
        if (left > 0)
            return $"刚解除同盟，{left} tick 内不能宣战"
                + "（与 ColonyTogether 同一条规则：退出同盟要给对方一天的预警）";
        pact.WarSince = Find.TickManager?.TicksGame ?? 0;
        // Declaring war ends an alliance: the two states are mutually exclusive by construction, and a record that was both
        // would make every gate resolve arbitrarily.
        pact.Allied = false;
        // A war also clears any countdown, so a later peace cannot leave a stale block behind.
        pact.NoticeUntil = 0;
        SettlementSync.Touch();
        return $"据点 #{a} 向 #{b} 宣战，立即生效";
    }

    /// <summary>Makes peace. Clears the war but, unlike before round 94, does <b>not</b> start a notice: the notice belongs
    /// to leaving an alliance, and a pair that never allied has nothing to wait for.</summary>
    internal static string Peace(SettlementData? data, int a, int b)
    {
        if (data is null || !data.Active) return "没有据点";
        for (var i = 0; i < data.Pacts.Count; i++)
        {
            if (data.Pacts[i] is not { } pact || !pact.Between(a, b)) continue;
            if (pact.WarSince < 0) return "本来就没有战争";
            pact.WarSince = -1;
            SettlementSync.Touch();
            return $"据点 #{a} 与 #{b} 停战";
        }
        return "本来就没有战争";
    }

    /// <summary>Forms or dissolves an alliance.
    ///
    /// <para><b>Dissolving starts the notice.</b> That is the only place in ColonyTogether where <c>NoticeTicks</c> is
    /// written (<c>ct045:117294</c>), and the asymmetry is the whole rule: you may ally whenever both agree, but leaving
    /// gives the other side a day before you can attack them.</para></summary>
    internal static string SetAllied(SettlementData? data, int a, int b, bool allied)
    {
        if (data is null || !data.Active) return "没有据点";
        if (a == 0 || b == 0) return "需要指定两方";
        if (a == b) return "不能与自己结盟";
        var pact = Ensure(data, a, b);
        if (allied && pact.WarSince >= 0) return "战争中不能结盟，先停战";
        if (pact.Allied == allied) return allied ? "已经是盟友" : "本来就不是盟友";
        pact.Allied = allied;
        if (allied)
        {
            // An alliance makes no sense with an outstanding attack ban, and keeping it would silently block a war that has
            // nothing to do with this pact any more.
            pact.NoticeUntil = 0;
        }
        else
        {
            pact.NoticeUntil = (Find.TickManager?.TicksGame ?? 0) + NoticeTicks;
        }
        SettlementSync.Touch();
        return allied
            ? $"据点 #{a} 与 #{b} 结为盟友"
            : $"据点 #{a} 与 #{b} 解除同盟，{NoticeTicks} tick 内不能互相宣战";
    }

    /// <summary>Runs the notice down to nothing, so the block can be observed without waiting 60 000 ticks. Writes the value
    /// the clock would have produced, which is what makes the reading real: the gate path is the ordinary one, only the
    /// input is backdated.</summary>
    internal static string Expire(SettlementData? data, int a, int b)
    {
        if (data is null || !data.Active) return "没有据点";
        for (var i = 0; i < data.Pacts.Count; i++)
        {
            if (data.Pacts[i] is not { } pact || !pact.Between(a, b)) continue;
            var now = Find.TickManager?.TicksGame ?? 0;
            if (pact.NoticeUntil <= now)
                return $"据点 #{a} 与 #{b} 没有正在计时的宣战禁令（NoticeUntil={pact.NoticeUntil}，现在 {now}）";
            var target = now - 1;
            if (target < 0)
                return $"世界时钟只有 {now} tick，回填到 {target} 会变成负数（0 是「没有禁令」的哨兵值）。"
                    + $"先 advance-ticks 至少 {now + 1} tick，或直接结盟再解盟拿一份新的倒计时";
            pact.NoticeUntil = target;
            SettlementSync.Touch();
            return $"据点 #{a}-{b} 的宣战禁令已回填到 {target}（现在 {now}），禁令到期";
        }
        return $"据点 #{a}-{b} 之间没有协议";
    }

    private static SettlementPact Ensure(SettlementData data, int a, int b)
    {
        for (var i = 0; i < data.Pacts.Count; i++)
            if (data.Pacts[i] is { } existing && existing.Between(a, b)) return existing;
        var created = new SettlementPact { A = a, B = b };
        data.Pacts.Add(created);
        return created;
    }

    internal static string Summary(SettlementData? data)
    {
        if (data is null || !data.Active || data.Pacts.Count == 0) return "战争 无";
        var now = Find.TickManager?.TicksGame ?? 0;
        var wars = 0;
        var allies = 0;
        var barred = 0;
        foreach (var pact in data.Pacts)
        {
            if (pact is null) continue;
            if (pact.Allied) allies++;
            else if (pact.WarSince >= 0) wars++;
            // Counted separately: a pact that is neither allied nor at war can still be barring a declaration, and a
            // summary that printed only 「战争 0 / 同盟 0」 would read as "nothing here".
            if (pact.NoticeLeft(now) > 0) barred++;
        }
        return $"战争 {wars} / 同盟 {allies}"
            + (barred > 0 ? $" / 宣战禁令 {barred}（{NoticeTicks} tick 预警）" : "");
    }
}
