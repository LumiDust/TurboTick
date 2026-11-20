using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Round 158: the think-node job gate, read on a real target.
///
/// <para>Two things have to be shown side by side, and neither alone is evidence: the <b>predicate</b> ("would this job be
/// refused") and the <b>wiring</b> ("is the postfix actually on `TryIssueJobPackage`"). A predicate that says `false` with
/// no patch installed reads exactly like a gate that works.</para></summary>
internal static class SettlementJobProbe
{
    internal static string Run(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return "用法：<小人 id> #地图 x z [defName]（例：873 0 116 138 Silver）";
        if (!int.TryParse(parts[0], out var pawnId)) return "小人 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1].TrimStart('#'), out var mapId)) return "地图 id 不是整数：" + parts[1];
        if (!int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var z))
            return "坐标不是整数：" + parts[2] + " " + parts[3];
        var wantedDef = parts.Length > 4 ? parts[4] : null;
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
            .FirstOrDefault(p => p?.thingIDNumber == pawnId);
        if (pawn is null) return $"本端没有小人 #{pawnId}";
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return $"({x},{z}) 不在地图内";

        var report = new StringBuilder();
        var installed = HarmonyLib.Harmony.GetPatchInfo(
            typeof(ThinkNode_JobGiver).GetMethod("TryIssueJobPackage",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        report.Append($"据点层 {(data.Active ? "开" : "关")}；小人 #{pawnId} 属据点 {data.HomeOfPawn(pawnId)}"
                      + $"；TryIssueJobPackage 已挂 {(installed is null ? 0 : installed.Postfixes.Count)} 个 Postfix");

        var thing = map.thingGrid.ThingsAt(cell)
            .FirstOrDefault(t => t.Spawned && !(t is Mote)
                                 && (wantedDef is null || string.Equals(t.def?.defName, wantedDef, StringComparison.OrdinalIgnoreCase)));
        if (thing is null) return report.Append($"；({x},{z}) 没有符合条件的物件（这一半未测）").ToString();
        var home = SettlementRule.HomeOf(data, thing);
        report.Append($"；物件 {thing.def?.defName}#{thing.thingIDNumber} 属据点 {home}"
                      + $" 分类 {SettlementRule.KindOf(thing)}");

        // A synthetic job with this thing as its target: the predicate is what the postfix would ask.
        var job = JobMaker.MakeJob(JobDefOf.Goto, thing);
        var guestRefuse = SettlementJobGiverGate.Refuse(pawn, job);
        report.Append($"；访客 该物件被呈现为禁止={SettlementForbidVisibility.ShouldHide(thing, pawn, out _)}"
                      + $" → 派活会被拦={guestRefuse}");
        var ownerPawn = map.mapPawns.AllPawnsSpawned
            .FirstOrDefault(p => p?.thingIDNumber is { } id && data.HomeOfPawn(id) != 0
                                 && data.HomeOfPawn(id) != data.HomeOfPawn(pawnId));
        if (ownerPawn is null) report.Append("；没有另一个已认领的小人（业主一侧未测）");
        else report.Append($"；业主 #{ownerPawn.thingIDNumber} 派活会被拦={SettlementJobGiverGate.Refuse(ownerPawn, job)}");
        report.Append($"；{SettlementJobGiverGate.Summary()}");
        return report.ToString();
    }
}
