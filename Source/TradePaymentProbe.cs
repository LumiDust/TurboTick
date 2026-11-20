using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Round 156: whose goods may pay a trade ship.
///
/// <para><b>What is measured, and what is not.</b> `TradeUtility.AllLaunchableThingsForTrade` (`:608612`) only returns
/// anything inside a <b>powered</b> orbital trade beacon, and it needs `pos.GetRegion(map)` to be non-null
/// (`:585536-585558`). Building that fixture inside one tick is not something this probe can do honestly: a spawned power
/// net settles on the next tick, and the region grid answers `null` for a freshly spawned building, so a beacon placed by
/// a command reports <b>zero</b> tradeable cells - a `0` that has nothing to do with ownership. So the probe reports the
/// beacon count (so the gap is stated, not hidden) and then measures the <b>filter</b> on real silver spawned inside a real
/// settlement, for a guest and for the owner. That is the two-sided reading that matters: the same three stacks, one
/// negotiator allowed to take them and one not.</para></summary>
internal static class TradePaymentProbe
{
    internal static string Run(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "用法：<小人 id> #地图（例：873 0）";
        if (!int.TryParse(parts[0], out var pawnId)) return "小人 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1].TrimStart('#'), out var mapId)) return "地图 id 不是整数：" + parts[1];
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
            .FirstOrDefault(p => p?.thingIDNumber == pawnId);
        if (pawn is null) return $"本端没有小人 #{pawnId}";

        var report = new StringBuilder();
        report.Append($"据点层 {(data.Active ? "开" : "关")}；小人 #{pawnId} 属据点 {data.HomeOfPawn(pawnId)}");
        var sites = data.Sites.Where(site => !site.Retired && site.MapId == map.uniqueID).ToList();
        report.Append($"；本端地图 {mapId} 的活据点 {sites.Count} 个");
        var powered = Building_OrbitalTradeBeacon.AllPowered(map).Count();
        report.Append($"；通电轨道贸易信标 {powered} 个"
                      + (powered == 0 ? "（枚举层面未测：没有信标时 `AllLaunchableThingsForTrade` 本来就是空）" : ""));
        if (sites.Count == 0) return report.Append("；没有据点，货没有归属（这一半未测）").ToString();

        var site = sites[0];
        var silver = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
        if (silver is null) return report.Append("；缺少 Silver def（这一半未测）").ToString();
        var spawned = new List<Thing>();
        try
        {
            // Three stacks inside the settlement, spawned where a hauling job would actually leave them.
            var centre = new IntVec3((site.MinX + site.MaxX) / 2, 0, (site.MinZ + site.MaxZ) / 2);
            var offsets = new[] { new IntVec3(0, 0, 0), new IntVec3(1, 0, 0), new IntVec3(0, 0, 1) };
            foreach (var offset in offsets)
            {
                var cell = centre + offset;
                if (!cell.InBounds(map)) continue;
                var stack = ThingMaker.MakeThing(silver);
                stack.stackCount = 10 + spawned.Count * 5;
                spawned.Add(GenSpawn.Spawn(stack, cell, map));
            }
            if (spawned.Count == 0) return report.Append("；银堆没能落地（这一半未测）").ToString();
            report.Append($"；夹具 {spawned.Count} 堆银在据点 {site.Id} 里："
                          + string.Join(", ", spawned.Select(t =>
                              $"{t.def?.defName}×{t.stackCount}@({t.PositionHeld.x},{t.PositionHeld.z})属据点 {SettlementRule.HomeOf(data, t)}")));

            SettlementLaunchableGate.Reset();
            var guestKept = SettlementLaunchableGate.Filter(spawned, pawn).ToList();
            report.Append($"；访客 #{pawnId} 过滤后 {guestKept.Count}/{spawned.Count} 件；{SettlementLaunchableGate.Summary()}");

            var ownerPawn = map.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p?.thingIDNumber is { } id && data.HomeOfPawn(id) == site.Id);
            if (ownerPawn is null) report.Append("；本端没有已认领到该据点的小人（业主一侧未测）");
            else
            {
                SettlementLaunchableGate.Reset();
                var ownerKept = SettlementLaunchableGate.Filter(spawned, ownerPawn).ToList();
                report.Append($"；业主 #{ownerPawn.thingIDNumber} 过滤后 {ownerKept.Count}/{spawned.Count} 件；"
                              + $"{SettlementLaunchableGate.Summary()}");
            }
        }
        finally
        {
            for (var i = spawned.Count - 1; i >= 0; i--)
                if (!spawned[i].Destroyed) spawned[i].Destroy(DestroyMode.Vanish);
            report.Append(spawned.Count == 0 ? "；没有夹具需要清理" : $"；夹具已移除 {spawned.Count} 件");
        }
        return report.ToString();
    }
}
