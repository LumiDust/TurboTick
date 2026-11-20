using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Round 157: does a container's contents inherit the container's owner.
///
/// <para><b>Why this needed measuring.</b> ColonyTogether records ownership <i>per thing</i> (`data.Set(item, owner)`) and
/// therefore needs two patches to keep contents in step - `SettlementContainerPatch` (ct:353278) sets an added item's owner
/// from the container's, `SettlementContainerAddedPatch` (ct:353302) does the same on `ThingOwner.NotifyAdded` with its loot
/// rule. This project records ownership <i>by position</i> instead: `SettlementRule.HomeOf` asks
/// `data.SettlementAt(map.uniqueID, thing.PositionHeld)` (`SettlementRule.cs:193-199`). "Contents inherit" is therefore not
/// an assumption to repeat in a comment - it is a claim that `MapHeld`/`PositionHeld` walk up through
/// `SpawnedParentOrMe`, and the reading below is what makes it evidence.</para>
///
/// <para>The probe builds a casket with two silver stacks inside it, then reads three things per stack: whether the thing
/// itself is spawned, what `MapHeld`/`PositionHeld` resolve to, and what the gate says for a guest and for the owner. A
/// stack that says `Spawned=false` but still answers a home is the whole question.</para></summary>
internal static class ContainerOwnershipProbe
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
        if (sites.Count == 0) return report.Append("；没有据点，内容物也谈不上归属（这一半未测）").ToString();
        var site = sites[0];
        var casketDef = DefDatabase<ThingDef>.GetNamedSilentFail("CryptosleepCasket");
        var silver = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
        if (casketDef is null || silver is null) return report.Append("；缺少休眠舱/银的 def（这一半未测）").ToString();

        var spawned = new List<Thing>();
        try
        {
            var centre = new IntVec3((site.MinX + site.MaxX) / 2, 0, (site.MinZ + site.MaxZ) / 2);
            var casket = GenSpawn.Spawn(ThingMaker.MakeThing(casketDef), centre, map);
            spawned.Add(casket);
            var holder = (IThingHolder)casket;
            var contents = holder.GetDirectlyHeldThings();
            for (var i = 0; i < 2; i++)
            {
                var stack = ThingMaker.MakeThing(silver);
                stack.stackCount = 10 + i * 5;
                if (!contents.TryAdd(stack)) { report.Append($"；第 {i + 1} 堆银没进容器"); continue; }
            }
            report.Append($"；夹具：{casket.def?.defName}#{casket.thingIDNumber} 于 ({centre.x},{centre.z})，"
                          + $"容器内容 {contents.Count} 件，容器属据点 {SettlementRule.HomeOf(data, casket)}");

            var ownerPawn = map.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p?.thingIDNumber is { } id && data.HomeOfPawn(id) == site.Id);
            foreach (var thing in contents.ToList())
            {
                var home = SettlementRule.HomeOf(data, thing);
                var mapHeld = thing.MapHeld;
                report.Append($"；内容 {thing.def?.defName}×{thing.stackCount}：Spawned={thing.Spawned}"
                              + $" MapHeld={(mapHeld is null ? "null" : mapHeld.uniqueID.ToString())}"
                              + $" PositionHeld=({thing.PositionHeld.x},{thing.PositionHeld.z}) 属据点 {home}"
                              + $" → 访客 Can(Take)={SettlementGates.Can(pawn, SettlementAct.Take, thing)}"
                              + $"/IsForbidden={ForbidUtility.IsForbidden(thing, pawn)}"
                              + (ownerPawn is null ? ""
                                  : $"；业主 #{ownerPawn.thingIDNumber} Can(Take)={SettlementGates.Can(ownerPawn, SettlementAct.Take, thing)}"
                                    + $"/IsForbidden={ForbidUtility.IsForbidden(thing, ownerPawn)}"));
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
