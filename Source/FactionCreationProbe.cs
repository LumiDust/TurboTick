using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Probe for the faction-creation channel (round 162).
///
/// <para><b>Three readings, none of which creates a permanent faction.</b> A faction cannot be removed from
/// `FactionManager` unless it is `temporary` (`Remove` `:487789` errors otherwise), so a probe that really added one
/// would pollute the save forever - and a fixture that changes the faction screen is not a fixture. What the channel
/// does is therefore read in three separate halves:</para>
///
/// <list type="number">
/// <item>`notice`: would the next publish carry this faction? False for one already on the wire, true for an id the
/// channel has not sent yet - read through <see cref="FactionCreationSync.WouldSend"/>, no faction created.</item>
/// <item>`build`: a real faction's fragment is rebuilt through `CreateFromFragment` and reported with its def, load id,
/// name and relation count. This is the half that can fail silently: a fragment that parses but leaves `def` null would
/// be added to `allFactions` and then throw inside `RecacheFactions`.</item>
/// <item>`apply`: the row for a faction that already exists is handed to `Apply`, which must count it and create
/// nothing.</item>
/// </list>
///
/// <para><b>What it cannot show.</b> That a newly created faction really arrives on a second peer - that needs two
/// processes. It shows the row that would reach it is complete, and that both ends of the local path accept it.</para>
/// </summary>
internal static class FactionCreationProbe
{
    internal static string Run(string? argument)
    {
        var manager = Find.FactionManager;
        if (manager?.AllFactionsListForReading is not { Count: > 0 } factions)
            return "本端没有派系表";
        var wanted = (argument ?? string.Empty).Trim();
        // Pick a faction with relations if possible: relations are the field a fragment most often gets wrong.
        var faction = wanted.Length > 0
            ? factions.FirstOrDefault(each => each?.def?.defName == wanted)
            : factions.Where(each => each?.def is not null)
                .OrderByDescending(each => factions.Count(other => other is not null && other != each
                    && each.RelationKindWith(other) != FactionRelationKind.Neutral)).FirstOrDefault();
        if (faction?.def is null) return wanted.Length > 0 ? $"没有 defName={wanted} 的派系" : "没有可用的派系";
        var message = new StringBuilder();
        // 1. notice half — the runtime publishes every frame, so by the time a probe runs everything is already on the
        // wire and both readings would be false. Reset first: then "nothing sent yet" is the state being measured.
        FactionCreationSync.Reset();
        var beforePublish = FactionCreationSync.WouldSend(faction.loadID);
        FactionCreationSync.Publish();
        var afterPublish = FactionCreationSync.WouldSend(faction.loadID);
        var fictional = factions.Max(each => each?.loadID ?? 0) + 1000;
        var fictionalSend = FactionCreationSync.WouldSend(fictional);
        message.Append($"通知：#{faction.loadID}「{faction.Name}」发布前 {beforePublish} → 发布后 {afterPublish}；");
        message.Append($"虚构 id {fictional} 会被发 {fictionalSend}（应为 否，表里没有它）；");
        // 2. build half — rebuild the fragment into a faction that is NOT added anywhere.
        var xml = ScribeBridge.SaveOrNull(faction) ?? string.Empty;
        var rebuilt = FactionCreationSync.CreateFromFragment(xml);
        var rebuiltInTable = rebuilt is not null && factions.Any(each => each is not null && each.loadID == rebuilt.loadID);
        message.Append($"重建：片段 {xml.Length} B → {(rebuilt is null ? "失败" : "成功")}");
        if (rebuilt is not null)
            message.Append($"（def {(rebuilt.def?.defName ?? "空")}、#{rebuilt.loadID}「{rebuilt.Name}」、非中立关系 {factions.Count(other => other is not null && other != rebuilt && rebuilt.RelationKindWith(other) != FactionRelationKind.Neutral)} 条、");
        if (rebuilt is not null)
            message.Append($"同名 {(string.Equals(rebuilt.Name, faction.Name, StringComparison.Ordinal) ? "一致" : "不一致")}、");
        if (rebuilt is not null)
            message.Append($"未进表 {(rebuiltInTable ? "否（进了！）" : "是")}）；");
        // 3. apply half — the row for an existing faction must be a no-op.
        var createdBefore = FactionCreationSync.Created;
        FactionCreationSync.Apply([new Networking.ReplicaFactionCreation(faction.loadID, faction.def.defName, xml)]);
        message.Append($"应用既有行：新建 {FactionCreationSync.Created - createdBefore} 个（应为 0）；");
        var sizes = FactionCreationSync.Sizes();
        long total = 0;
        foreach (var row in sizes) total += row.Bytes;
        message.Append($"基线 {sizes.Count} 行 {total} B（最大 {sizes.FirstOrDefault().Def} {sizes.FirstOrDefault().Bytes} B）；");
        message.Append(FactionCreationSync.Summary());
        return message.ToString();
    }
}
