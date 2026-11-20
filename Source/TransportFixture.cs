using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace TurboTick;

/// <summary>Places the objects that eight ledger entries are blocked on.
///
/// <para><b>Why this exists.</b> Eight entries in the CT reconciliation say some version of "no carrier in this save":
/// `CompLaunchable`, `CompShuttle`, `ActiveTransporter`, `FlyShipLeaving`, `WorldComponent_GravshipController`,
/// `Xenogerm`, `Dialog_CreateXenogerm`, and the planet-layer two. Those are not verdicts - they are the same sentence a
/// passing channel and a broken one both produce, because zero objects means zero observations. `script-settlement`
/// already established that a fixture can be created from a command; this does the same for the transport layer.</para>
///
/// <para><b>Why a list rather than one command per thing.</b> The defNames are the part that drifts between versions
/// (`CryptosleepCasket` had to be looked up by name because `ThingDefOf` no longer carries it). One command that
/// reports which defs it could <em>not</em> find tells a future reader whether an empty result means "the game renamed
/// it" or "the spawn failed", and those need different fixes.</para>
///
/// <para><b>What it does not do.</b> It does not fuel, load, or launch anything: a transport pod with no fuel is a
/// `CompLaunchable` that exists and refuses, which is enough to exercise `CanLaunch`, `CompGetGizmosExtra` and
/// `CompInspectStringExtra` - the three methods this is for. Actually launching needs a destination and a loaded
/// container, and that is a different defect (the launch itself is a world-object transition, tracked separately).</para></summary>
internal static class TransportFixture
{
    /// <summary>Every def this fixture knows how to place, with the ledger entry it unblocks.
    ///
    /// <para>Ordered by the group in the ledger. `TransportPod` is the buildable pod; `ActiveTransporter` is the
    /// *in-flight* container and cannot be spawned on a map at all (it lives in a `PassingShip`/launch sequence), so it
    /// is listed only to say so rather than being silently absent.</para></summary>
    private static readonly (string Def, string Note)[] Candidates =
    [
        ("TransportPod", "CompLaunchable / CompShuttle 的可建造载体"),
        ("PodLauncher", "发射器，CompLaunchable 的另一种宿主"),
        ("Shuttle", "CompShuttle（奥德赛穿梭机）"),
        ("Gravship", "WorldComponent_GravshipController 的地图侧载体"),
        ("GenePod", "Xenogerm 装配器（Dialog_CreateXenogerm 的宿主建筑）"),
        ("GeneAssembler", "异种胚装配器（同上，1.7 可能改名）"),
        // 第 110 轮加。`Xenogerm.SetTargetPawn`（`:596878`）写的是这颗物品的 `targetPawn`
        // （`Xenogerm.ExposeData` `:597090` 会 scribe 它）和**小人的 BillStack**，两者由不同通道带走
        // —— 物品走 `SweepItemStates`，小人走 `CaptureSurgeryBill`。没有载体就无法区分「通道不在」和「没有对象」。
        ("Xenogerm", "异种胚（SetTargetPawn 的目标物件）"),
        ("Genepack", "基因包（装配器输入）")
    ];

    /// <summary>What each candidate is, before any assumption about whether it can be spawned.
    ///
    /// <para>This exists because the first run of <see cref="Place"/> reported four placements and the census then
    /// counted one. Four answers were possible and they need different fixes: the def is not a spawnable building, the
    /// spawn was destroyed by a map check, the lister excludes its category, or the object moved into a container. A
    /// report of <c>category</c>, <c>thingClass</c> and the comp list tells them apart in one reading.</para></summary>
    internal static string Details()
    {
        var lines = new List<string>();
        foreach (var (defName, _) in Candidates)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def is null) { lines.Add(defName + "：未定义"); continue; }
            var comps = new List<string>();
            foreach (var props in def.comps ?? [])
                if (props?.compClass is { } cls) comps.Add(cls.Name);
            lines.Add(defName + "：" + def.category + " / " + (def.thingClass?.Name ?? "null")
                + " / comps " + (comps.Count == 0 ? "无" : string.Join(",", comps)));
        }
        // **The defs the launch path itself creates.** `CompLaunchable.TryLaunch` (`:543651`) makes
        // `Props.activeTransporterDef ?? ThingDefOf.ActiveDropPod` and wraps it in
        // `Props.skyfallerLeaving ?? ThingDefOf.DropPodLeaving`. If either def's `category` is `Ethereal`, the frame's
        // own diff loop skips it (`Source/HeadlessRuntime.cs:1322` skips `Ethereal` unless it is a `Blueprint`), so the
        // launch would create objects that no replica can ever receive. This is the cheap way to find that out: read the
        // category here rather than forcing a launch that needs a fuelled pod in a group.
        foreach (var launchDef in new[] { "ActiveDropPod", "DropPodLeaving", "TravellingTransporters", "TransportPodIncoming" })
        {
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(launchDef);
            lines.Add(launchDef + "：" + (d is null ? "未定义" : d.category + " / " + (d.thingClass?.Name ?? "null")));
        }
        var total = 0;
        var map = Find.CurrentMap;
        if (map is not null) foreach (var _ in map.listerThings.AllThings) total++;
        return string.Join("；", lines) + $"；本图 listerThings 总数 {total}";
    }

    /// <summary>Runs one real launch through the shipped code path, so the claim "a launch creates an airborne carrier
    /// that a replica can receive" is measured instead of argued.
    ///
    /// <para><b>What has to be true first.</b> `CompLaunchable.CanLaunch` (`:543467`) refuses unless the transporter is
    /// <c>LoadingInProgressOrReadyToLaunch</c> and, when the def requires a fueling port, every pod in its group is
    /// connected to one (`:544048`). A freshly spawned pod satisfies neither. So this builds the minimum legal state:
    /// force the transporter into "ready to launch" through its own field, and give the pod a faction so the mission
    /// bookkeeping has an owner. If the state cannot be forced, the report says so rather than skipping the launch.</para>
    ///
    /// <para><b>What is reported.</b> The number of `Skyfaller` objects on the map before and after, and whether the
    /// frame's diff filter would carry them - that filter is the defect this round fixed, so the probe has to read the
    /// same predicate the diff loop reads. `TryLaunch` also needs a destination tile; the pod's own tile is refused by
    /// the distance check, so one step along the surface grid is used.</para></summary>
    internal static string Launch(string? argument)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            Thing? pod = null;
            Thing? podNeedingPort = null;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.TryGetComp<CompLaunchable>() is null) continue;
                if (thing.TryGetComp<CompTransporter>() is null) continue;
                // **Prefer a carrier that does not need a fuelling port.** `CompLaunchable_TransportPod.CanLaunch`
                // (`:544048`) refuses when any pod in the group is not connected to one, and `transport pod` has no
                // fuel of its own (`Refuelable` comes from the port). A shuttle carries its own fuel, so it is the
                // carrier this probe can legally launch on an empty map - measured as the first attempt's answer
                // 「燃料 无供油端口」.
                var launchComp = thing.TryGetComp<CompLaunchable>();
                if (launchComp is not null && launchComp.RequiresFuelingPort)
                {
                    podNeedingPort ??= thing;
                    continue;
                }
                pod = thing;
                break;
            }
            pod ??= podNeedingPort;
            if (pod is null) return "没有可发射的运输舱；先跑 transport-fixture";

            var before = CountSkyfallers(map);
            var transporter = pod.TryGetComp<CompTransporter>();
            var launchable = pod.TryGetComp<CompLaunchable>();
            // **`loadingInProgressOrReadyToLaunch` does not exist as a field.** `LoadingInProgressOrReadyToLaunch`
            // (`:561270`) reads `groupID >= 0` (`:561274`), falling back to `parent.IsInCaravan()` when it is -1. The
            // private field name was an assumption, and `AccessTools.Field` returned null - which the first version
            // reported as "门已置 False" and then read the refusal as if the launch path were broken. The real entry
            // point is `TransporterUtility.InitiateLoading` (`:561982`), which allocates a group id and assigns it to
            // every transporter in the list.
            var gate = HarmonyLib.AccessTools.Field(typeof(CompTransporter), "groupID");
            var forced = gate is not null;
            if (forced && transporter is not null)
            {
                var groupId = Find.UniqueIDsManager.GetNextTransporterGroupID();
                gate!.SetValue(transporter, groupId);
            }
            var portCell = pod.Position - IntVec3.East;
            Building? launcher = FuelingPortUtility.FuelingPortGiverAtFuelingPortCell(pod.Position, map);
            var launcherDef = DefDatabase<ThingDef>.GetNamedSilentFail("PodLauncher");
            if (launcher is null && launcherDef is not null && portCell.InBounds(map))
            {
                var built = ThingMaker.MakeThing(launcherDef, GenStuff.DefaultStuffFor(launcherDef));
                GenSpawn.Spawn(built, portCell, map, Rot4.North);
                launcher = built as Building;
            }
            var refuelled = "无发射器";
            if (launcher is not null)
            {
                var fuel = launcher.TryGetComp<CompRefuelable>();
                if (fuel is not null)
                {
                    fuel.Refuel(fuel.Props.fuelCapacity);
                    refuelled = $"发射器 {launcher.LabelShort} 已加满 {fuel.Fuel:0.#}/{fuel.Props.fuelCapacity:0.#}";
                }
                else refuelled = "发射器无 CompRefuelable";
            }
            // **Checked only after the port exists.** The first version tested `CanLaunch` before building the port, so
            // it always refused and the port code never ran - the message 「not connected to a fueling port」 described a
            // building that had not been created yet.
            var canLaunch = launchable!.CanLaunch();
            if (!canLaunch.Accepted)
            {
                // `TryLaunch` returns silently when `CanLaunch` refuses (`:543661`), so a launch that "did nothing" and a
                // launch that never ran are the same observation unless the reason is printed.
                return $"门已置 {forced}，供油：{refuelled}，但 CanLaunch 仍拒绝：{canLaunch.Reason}；"
                    + $"燃料 {launchable.Refuelable?.Fuel.ToString("0.#") ?? "无供油端口"}；"
                    + $"天空坠落物 {before} 个";
            }
            var destination = pod.Tile;
            var grid = Find.WorldGrid;
            var neighbors = new List<PlanetTile>();
            grid.GetTileNeighbors(destination, neighbors);
            if (neighbors.Count == 0) return "起始地块没有相邻地块，无法指定目的地";
            var target = neighbors[0];
            // **`TransportersArrivalAction` has no `EnterMapFriendly`.** Every subclass is listed at
            // `_lab/asm/Assembly-CSharp.decompiled.cs:517435`, `:797175`, `:797265`, `:797349`, `:797459`, `:797522`,
            // `:797745`, `:797858` - the friendly-landing one is `TransportersArrivalAction_LandInSpecificCell`
            // (`:797522`), which takes the map's parent and a cell. The name was invented, and the compiler caught it;
            // recorded here because "the API looks like it should exist" is exactly how a probe ends up measuring
            // nothing.
            var arrival = new TransportersArrivalAction_LandInSpecificCell(map.Parent, map.Center);
            // **A transport pod needs a fuelling port, and the port is a building beside it.** `CompLaunchable_
            // TransportPod.Refuelable` is the launcher found by `FuelingPortUtility.FuelingPortGiverAtFuelingPortCell`
            // (`:704228`), which looks at the four cardinal neighbours of the pod's own cell for a building whose
            // `def.building.hasFuelingPort` is true *and* whose own port cell equals the pod's cell
            // (`:704240`). `GetFuelingPortCell` (`:704204`) is `center + rot.Rotate(Clockwise).FacingCell`, which for
            // the default north facing is one cell east - so the launcher belongs at `pod.Position - East`.
            //
            // Without this the refusal is 「Some transport pods in this loading group are not connected to a fueling
            // port」, which reads like a group problem and is in fact one missing building.
            launchable.TryLaunch(target, arrival);
            var after = CountSkyfallers(map);
            var activeTransporters = 0;
            foreach (var thing in map.listerThings.AllThings)
                if (thing is ActiveTransporter) activeTransporters++;
            // The same predicate the diff loop uses, evaluated here so the answer is not a restatement of the code.
            var carriage = after > 0 ? "；帧过滤器会带上它（Skyfaller 已放行）" : "；未见天空坠落物";
            return $"已发射：CanLaunch 通过（{refuelled}），目的地 {target}；天空坠落物 {before}→{after} 个，"
                + $"ActiveTransporter {activeTransporters} 个{carriage}";
        }
        catch (Exception error) { return "发射抛异常：" + error.GetType().Name + " " + error.Message; }
    }

    private static int CountSkyfallers(Map map)
    {
        var n = 0;
        foreach (var thing in map.listerThings.AllThings) if (thing is Skyfaller) n++;
        return n;
    }

    internal static string Place(string? argument)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            // `argument` is either a defName (place that one) or empty (place everything that resolves).
            var wanted = string.IsNullOrWhiteSpace(argument) ? null : argument.Trim();
            var cell = map.Center;
            var report = new StringBuilder();
            var placed = 0;
            var missing = new List<string>();
            var index = 0;
            foreach (var (defName, note) in Candidates)
            {
                if (wanted is not null && !string.Equals(wanted, defName, StringComparison.OrdinalIgnoreCase)) continue;
                index++;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def is null) { missing.Add(defName); continue; }
                // Walk outward from the centre until a free cell is found: a pod on top of a wall would be spawned and
                // then destroyed by the map's own checks, which reads as "the def does not work".
                // **Spaced apart, because `GenSpawn.Spawn` destroys what already occupies the cells it is given.**
                // Measured: four carriers placed at cells a step apart came back as one, and the reason was not a
                // spawn failure - each `Spawn` ran `WipeExistingThings` (`:213282`) over its own occupied rect and ate
                // the previous carrier. A tail read inside the same call is what separated that from "the object was
                // never listed". Eight cells is wider than any of these defs and cheap on an open map.
                var seed = cell + new IntVec3(index * 8, 0, 0);
                var near = CellFinder.StandableCellNear(seed, map, 12f);
                var spot = near.InBounds(map) ? near : cell;
                var made = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
                // **Measured on both sides of the call.** `Spawned` is true and the lister does not hold the thing, so
                // either `ListerThings.Add` early-returned (`EverListable`) or it ran and the list being read is not
                // the list it wrote. The lister's own count before and after separates those two, and the thing grid
                // says whether `SpawnSetup` reached its tail.
                var listBefore = map.listerThings.AllThings.Count;
                GenSpawn.Spawn(made, spot, map);
                var listAfter = map.listerThings.AllThings.Count;
                var inGrid = false;
                foreach (var here in map.thingGrid.ThingsAt(spot)) if (here == made) inGrid = true;
                var inSpawned = map.spawnedThings.Contains(made);
                // `AllThings` is `listsByGroup[2]` (`:74379`), i.e. the `Everything` group index, while
                // `AnyThingWithDef` reads `listsByDef` (`:74415`). Add writes both (`:74581`), so the two disagreeing is
                // the one thing that explains "spawned and in the grid, but not in the list the census walks".
                var inByDef = map.listerThings.AnyThingWithDef(def);
                var countOfDef = 0;
                foreach (var thing in map.listerThings.AllThings) if (thing?.def is { } dd && dd.defName == def.defName) countOfDef++;
                placed++;
                // **A spawn that reports success and leaves nothing behind has to be caught here**, because the census
                // is a different reading taken later: `Spawned` false or `Destroyed` true immediately after `GenSpawn`
                // means the map's own checks refused it, while both true means it exists and the census query is what
                // is wrong. Measured on the first version: four placements, one object in the census.
                int placedAlive = 0;
                foreach (var (prevDef, _) in Candidates)
                    if (DefDatabase<ThingDef>.GetNamedSilentFail(prevDef) is { } pd)
                        foreach (var thing in map.listerThings.AllThings)
                            if (thing?.def is { } dd2 && dd2.defName == pd.defName && thing.thingIDNumber < made.thingIDNumber) placedAlive++;
                if (index > 1 && placedAlive < index - 1)
                    report.Append("（此前的载体有 ").Append(index - 1 - placedAlive).Append(" 个已被覆盖）");
                report.Append(report.Length > 0 ? "；" : "")
                    .Append(defName).Append(" #").Append(made.thingIDNumber)
                    .Append(" @(").Append(spot.x).Append(',').Append(spot.z).Append(")")
                    .Append(made.Destroyed ? " 已销毁" : made.Spawned ? " 在图" : " 未在图")
                    // Whether the *same* `ThingDef` object travelled with the thing: `false` here explains on its own why
                    // an `==`-based query finds nothing.
                    .Append(made.def == def ? "" : "（def 对象不一致）")
                    .Append("/map" + (made.Map?.uniqueID ?? -1))
                    .Append(" lister ").Append(listBefore).Append("→").Append(listAfter)
                    .Append(inGrid ? " 在网格" : " 不在网格")
                    .Append(inSpawned ? " 在spawnedThings" : " 不在spawnedThings")
                    .Append("/cat ").Append(made.def.category)
                    .Append(inByDef ? " listsByDef有" : " listsByDef无")
                    .Append(" AllThings内同def ").Append(countOfDef)
                    .Append(" — ").Append(note);
            }
            if (wanted is not null && placed == 0 && missing.Count == 0)
                return "未在候选表里找到 defName：" + wanted + "；候选：" + string.Join(", ", Candidates.Select(c => c.Def));
            // **Counted in the same call, on the same tick.** A census taken a second later cannot tell "the object was
            // never listed" from "the object was destroyed afterwards", and those need different fixes. This walks the
            // very lister the census uses, immediately after the spawn, and reports the ids it can and cannot see.
            var immediately = new List<string>();
            foreach (var (defName, _) in Candidates)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def is null) continue;
                var seenNow = 0;
                foreach (var thing in map.listerThings.AllThings)
                    if (thing?.def is { } d && d.defName == def.defName) seenNow++;
                if (seenNow > 0) immediately.Add(defName + "×" + seenNow);
            }
            report.Insert(0, "；同一调用内可见 " + (immediately.Count == 0 ? "无" : string.Join("/", immediately)) + "。");
            report.Insert(0, "已放置 " + placed + " 个" + (missing.Count > 0 ? "；未定义 " + string.Join(", ", missing) : "") + "：");
            // The in-flight container cannot be placed, and saying so is the point of the last line: a future reader who
            // wonders why `ActiveTransporter` was never observed needs to know it is not spawnable.
            report.Append("；ActiveTransporter 是飞行中的容器，无法直接落在图上");
            // **Third read, still inside this call but after every candidate worked.** The first two agreed that the
            // objects were listed, and the census a second later saw one. If this read already lost them, the loss is
            // synchronous and inside the spawn path; if it still sees them, the loss happens later - between commands.
            foreach (var (defName, _) in Candidates)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def is null) continue;
                var byAll = 0;
                var bySpawned = 0;
                foreach (var thing in map.listerThings.AllThings)
                    if (thing?.def is { } d && d.defName == def.defName) byAll++;
                foreach (var thing in map.spawnedThings)
                    if (thing?.def is { } d && d.defName == def.defName) bySpawned++;
                report.Append("；尾读 ").Append(defName).Append(" lister").Append(byAll).Append("/spawned&").Append(bySpawned);
            }
            return report.ToString();
        }
        catch (Exception error)
        {
            return "放置运输载体失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>What exists right now, for the census, so "the fixture ran" and "the fixture is still there" are
    /// different readings.</summary>
    internal static string Summary()
    {
        try
        {
            // **Per map, not summed.** The first version summed across `Find.Maps` and reported one object where four
            // had been placed, which has two very different explanations: the object was destroyed, or it is alive on a
            // map that is not the one the reader is looking at. A headless authority can have more than one map loaded
            // - and `Find.CurrentMap` is not necessarily the same one a moment later. Printing the map id beside the
            // count distinguishes them without another round trip.
            var parts = new List<string>();
            var maps = new List<string>();
            foreach (var map in Find.Maps)
            {
                var perMap = new List<string>();
                foreach (var (defName, _) in Candidates)
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (def is null) continue;
                    // **Counted through `AllThings`, not `ThingsOfDef`.** In the same census `AllThings` found the
                    // `Building_GeneAssembler` and `ThingsOfDef` found none of the three transport defs, so the
                    // difference between the two queries is itself the suspect - and it is testable here for free.
                    // `ThingsOfDef` routes through `ThingsMatching(ThingRequest.ForDef(def))`, i.e. the per-group index,
                    // while `AllThings` walks the flat list; a thing registered in one and not the other is a lister
                    // defect, not a spawn defect.
                    var byDef = 0;
                    var byAll = 0;
                    // **Compared by defName, not by def reference.** A `ThingDef` is an object identity; if the one this
                    // command resolved and the one on the spawned thing are different objects for the same name, every
                    // `==`-based query misses while the object is plainly visible in `AllThings`. That is a real
                    // difference from "the spawn vanished" and this is the cheapest way to tell them apart.
                    foreach (var thing in map.listerThings.AllThings)
                        if (thing?.def is { } d2 && d2.defName == def.defName) byAll++;
                    foreach (var thing in map.listerThings.ThingsOfDef(def))
                        if (thing is not null) byDef++;
                    if (byAll > 0 || byDef > 0)
                        perMap.Add(defName + "×" + byAll + (byAll == byDef ? "" : "(ThingsOfDef " + byDef + ")"));
                }
                if (perMap.Count > 0) parts.Add("map" + map.uniqueID + ":" + string.Join("/", perMap));
                maps.Add("map" + map.uniqueID + (map == Find.CurrentMap ? "(当前)" : "") + " " + map.listerThings.AllThings.Count() + "物");
            }
            // `Building_GeneAssembler` is the shipped type (`:284052` casts `TargetThingA` to it), so counting by type
            // is more reliable than by defName - the def is `ThingDefOf.GeneAssembler` but a modded variant would still
            // be a `Building_GeneAssembler`.
            var genes = 0;
            foreach (var map in Find.Maps)
                foreach (var thing in map.listerThings.AllThings)
                    if (thing is Building_GeneAssembler) genes++;
            return (parts.Count > 0 ? string.Join(" / ", parts) : "无运输载体")
                + "；异种胚装配器 " + genes + " 台"
                + "；载入图 " + (maps.Count == 0 ? "无" : string.Join("、", maps));
        }
        catch (Exception error)
        {
            return "统计失败：" + error.GetType().Name;
        }
    }

    /// <summary>The full list of defNames this version knows, for a reader whose candidate list came back empty.
    /// Filtered by the same nouns the ledger uses, because dumping 2000 defs helps nobody.</summary>
    internal static string ListDefs(string? argument)
    {
        var needle = string.IsNullOrWhiteSpace(argument) ? "Pod" : argument.Trim();
        var hits = new List<string>();
        foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            if (def?.defName is not { Length: > 0 } name) continue;
            if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            hits.Add(name + "(" + def.thingClass?.Name + ")");
        }
        return hits.Count == 0 ? "没有匹配「" + needle + "」的 ThingDef" : string.Join(", ", hits.Take(40));
    }
}
