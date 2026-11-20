using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>A live census of the state a replica cannot derive, cross-referenced against the counters each channel keeps.
///
/// <para><b>The problem this solves.</b> Every gap found so far was found the same way: by asking "which visible state is
/// driven by a tick the replica does not run". That works, but it depends on somebody thinking of the right question - and
/// the failures are silent. A frozen <c>FogGrid</c> throws nothing; a colony where plans never appear logs nothing. So a
/// whole class of bugs lives where nothing reports it, and the only detector is a player noticing the screen is wrong.</para>
///
/// <para><b>What a census can and cannot do.</b> It cannot decide which fields matter. What it can do is put two numbers next
/// to each other that are never next to each other otherwise: <i>how much state exists</i> and <i>how much the channel has
/// moved</i>. A container with entries and a channel that has never counted anything is a channel that does not fire; a
/// channel that has counted a great deal while the container is empty is a channel wired to the wrong thing. Both are
/// invisible in a log that only speaks when something throws.</para>
///
/// <para><b>Why the counters and not a fresh scan.</b> The counters are already maintained by the channels themselves, so the
/// census cannot disagree with reality - it reports what the channels did, not what a second implementation thinks they
/// should have done. The cost is that a channel must expose one; the alternative is a census that re-derives each channel's
/// state and can therefore be wrong in a new way.</para>
internal static class SyncCensus
{
    private readonly record struct Row(string Container, string Amount, string Channel);

    /// <summary>Builds the census for the current map and game. Returns a multi-line report.</summary>
    internal static string Report()
    {
        var map = Find.CurrentMap;
        var game = Current.Game;
        if (map is null || game is null) return "同步普查：没有运行中的地图";

        var rows = new List<Row>(24);
        var data = SettlementGates.Data;

        // Map-wide grids and lists. None of these is reachable from the thing enumeration, and each needs its own channel.
        rows.Add(new Row("地形改动", TerrainSync.TerrainChanges.ToString(), "TerrainSync"));
        rows.Add(new Row("屋顶改动", RoofSync.RoofChanges.ToString(), "RoofSync"));
        rows.Add(new Row("积雪格", SnowSync.CellsSent.ToString(), "SnowSync"));
        rows.Add(new Row("迷雾格", FogSync.CellsSent.ToString(), "FogSync"));
        rows.Add(new Row("规划", (map.planManager?.AllPlans?.Count ?? 0).ToString(), "PlanSync"));
        // Two rows, two different things. `AllAreas.Count` counts the *objects* a map has - vanilla creates four
        // (five with Biotech) and `SettlementAreas` adds one per settlement. `AreaSync` carries the *cells inside* the
        // built-in ones, which is a different question: a map can have all its areas and every one of them empty, which
        // is exactly what a replica looked like before the grid channel existed.
        rows.Add(new Row("区域", (map.areaManager?.AllAreas?.Count ?? 0).ToString(), "SettlementAreas"));
        rows.Add(new Row("区域格子", AreaSync.Summary(), "AreaSync"));
        // Separate row from the built-in grids: those are keyed by type and these by label, and the two fail in
        // different ways. `新建 0，删除 0` on every frame with a non-zero count means the reconcile is running and the
        // authority agrees; `新建 4，删除 4` repeating is a rename loop.
        rows.Add(new Row("自建区域", AreaAllowedSync.Summary(), "AreaAllowedSync"));
        // Traits have no `*Sync.cs`-per-file entry in this table's completeness test, so the row is written by hand the
        // same way the pawn-faction and surgery-bill rows are.
        rows.Add(new Row("特质", PawnTraitSync.Summary(), "PawnTraitSync"));
        // Royalty: six ledger entries read `pawn.royalty` and none of them had a carrier before this row existed.
        rows.Add(new Row("爵位", RoyaltySync.Summary(), "RoyaltySync"));
        rows.Add(new Row("健康状态", HealthStateProbe.Summary(), "HealthStateProbe"));
        rows.Add(new Row("运输载体", TransportFixture.Summary(), "TransportFixture"));
        rows.Add(new Row("坠落物进帧", HeadlessRuntime.SkyfallerSummary(), "DiffThings"));
        rows.Add(new Row("坠落物可进帧", HeadlessRuntime.SkyfallerCarriage(), "DiffThings 过滤+序列化"));
        rows.Add(new Row("飞船闸门", GravshipGateSync.Summary(), "GravshipGateSync"));
        rows.Add(new Row("外交冷却", FactionSync.CooldownSummary(), "FactionSync"));
        rows.Add(new Row("历史事件", HistorySync.Summary(), "HistoryEventsManager"));
        rows.Add(new Row("想法", ThoughtsProbe.Summary(), "ThoughtsProbe"));
        // No `GeneSync.cs`-shaped per-file row exists in this table either, so this is written by hand.
        rows.Add(new Row("基因", GeneSync.Summary(), "GeneSync"));
        // Written by hand for the same reason as the two above: there is no per-file row generator for a channel that
        // was added without a matching name in the completeness table.
        rows.Add(new Row("戒律", PreceptSync.Summary(), "PreceptSync"));
        // The right-click chokepoint, separate from the gizmo path: they are two different capture points with different
        // failure modes, and "gizmo sync works" says nothing about whether a right-click order reaches the authority.
        rows.Add(new Row("右键菜单", FloatMenuHookProbe.Summary(), "FloatMenuHookProbe"));
        // Per map, because the channel's two historic defects (per-peer id keying, current-map-only publish) are both
        // invisible on a single-map colony and both only matter once a second map exists.
        rows.Add(new Row("规划标记", PlanProbe.Summary(), "PlanProbe"));
        // The designator replay's map identity: `Designator.Map` is `Find.CurrentMap`, so with more than one map every
        // replay kind has to say which map it was drawn on or it lands on whichever one the authority happens to show.
        rows.Add(new Row("设计器地图", DesignatorMapProbe.Summary(), "DesignatorMapProbe"));
        // Flight state. `Pawn_FlightTracker.FlightTick` is the only writer and it runs from `Pawn.Tick`, so without this
        // channel a flying pawn is drawn walking, a landed one hovers, and the ability stays on cooldown forever.
        rows.Add(new Row("飞行", FlightSync.Preview(), "FlightSync"));
        // **A retired site is not a settlement (round 139).** This counted `Sites.Count`, so after `settlement-retire`
        // the report said "剩余据点 0 个" while the census still said `据点：2` - two instruments disagreeing about the
        // same table, and the census one counting tombstones. Retired rows are kept deliberately (the land must stop
        // being claimed, not vanish), so the count has to exclude them and say how many it excluded.
        var liveSites = data?.Sites?.Count(site => !site.Retired) ?? 0;
        var retiredSites = (data?.Sites?.Count ?? 0) - liveSites;
        rows.Add(new Row("据点", liveSites + (retiredSites > 0 ? $"（另有已废弃 {retiredSites}）" : ""), "SettlementSync"));
        // `LettersSent/MessagesSent` is two numbers about the sending side and says nothing about what arrived or what
        // had to be downgraded. A letter type that needs local choice state cannot be rebuilt on the replica
        // (`LetterSync.Apply` turns it into a `StandardLetter` and logs 「选项请到主机端操作」), and that count is the
        // only visible sign a player is being shown a read-only copy of something they are supposed to answer.
        rows.Add(new Row("信/消息", LetterSync.Summary(), "LetterSync"));
        rows.Add(new Row("抉择", LetterChoiceState.Summary() + $"；成长时刻应用 {SharedActions.GrowthChoicesApplied}", "LetterChoicePatch"));
        // Per kind, because "0 zones" and "every zone is a kind the publisher does not know" read identically in a total.
        // Fishing was the second of those for the whole life of this channel: the publisher and the create switch each
        // had two cases, and `Zone_Fishing` was in neither.
        {
            var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var countedZone in map.zoneManager?.AllZones ?? [])
            {
                var kindName = countedZone switch
                {
                    Zone_Stockpile => "stockpile",
                    Zone_Growing => "growing",
                    Zone_Fishing => "fishing",
                    _ => countedZone.GetType().Name
                };
                byKind[kindName] = byKind.TryGetValue(kindName, out var kn) ? kn + 1 : 1;
            }
            rows.Add(new Row("区域格子", $"{map.zoneManager?.AllZones?.Count ?? 0} / 发 {ZoneSync.ZonesSent}"
                + " / 分类 " + (byKind.Count == 0 ? "（无）" : string.Join(",", byKind.Select(kv => kv.Key + "×" + kv.Value))),
                "ZoneSync"));
        }
        // Equipment and apparel live in two `ThingOwner`s on the pawn, so the thing enumeration never carries them: a colonist
        // holding a gun shows empty-handed on the replica.
        // The whole summary, not just the send count: the fourth payload field (a persona weapon's bladelink traits) is
        // only non-empty for a weapon that has one, and `StatOffsetFromGear`'s middle term is exactly that field. A count of
        // sends says the channel ran; it cannot say whether that field ever carried anything.
        rows.Add(new Row("装备/服装", ApparelSync.Summary(), "ApparelSync"));
        // Designator actions are replayed from the command rather than synchronised as state, so the interesting number is
        // not how much exists but how many replays failed: a refusal there means the replica saw a different world.
        rows.Add(new Row("设计器重放", DesignatorSync.Summary(), "DesignatorSync"));
        // Needs travel inside `ReplicaPawn` rather than through a channel of their own, so the count is the number of needs one
        // pawn actually has - a colonist with no needs would mean the encoding had nothing to walk.
        rows.Add(new Row("需求（每小人）", (map.mapPawns.AllPawnsSpawned.FirstOrDefault()?.needs?.AllNeeds?.Count ?? 0)
            .ToString(), "NeedsSync"));
        // The level of a need and the counter a need keeps are different channels in one string: `ticksAtZero` decides the
        // involuntary-sleep MTB and only moves while the level sits at zero, so a pawn can read "exhausted" forever on a
        // replica and never collapse. Its own row, because it only has content once somebody has actually hit zero.
        rows.Add(new Row("休息归零", NeedsSync.RestPreview(), "NeedsSync(ticksAtZero)"));
        // These two are not state channels: they carry the operator's speed command and the peer's identity. They appear
        // here because the census doubles as the list of everything that crosses the wire, and a datapath nobody wrote down
        // is how a divergence starts.
        rows.Add(new Row("时间倍率", $"{(int)Find.TickManager.CurTimeSpeed}", "NativeSpeedSync"));
        rows.Add(new Row("会话视图", SessionViewSync.Describe(), "SessionViewSync"));
        rows.Add(new Row("研究项目", (DefDatabase<ResearchProjectDef>.AllDefsListForReading?.Count ?? 0).ToString(),
            "ResearchSync"));
        // Indoor rooms only. An outdoor room's temperature is a computed getter over the world tile temperature, which follows
        // `absTick` and is therefore already the host's on both ends.
        rows.Add(new Row("房间温度", RoomTempCounts(), "RoomTempSync"));
        // Plants grow in `TickLong` (`growthInt += GrowthPerTick * 2000f`), and `growthInt` is scribed - so a plant's growth
        // is a real player-visible state that no sweep table carries: `SweepItemStates` only walks `ThingCategory.Item`,
        // and the main loop skips an object whose position/rotation/stack/hitpoints did not change, which is precisely the
        // case a growing plant is in. Counted first, because grass may cover the whole map and the answer decides whether a
        // sweep is affordable at all.
        var plants = 0;
        var crops = 0;
        try
        {
            foreach (var each in Find.Maps)
                foreach (var thing in each.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
                {
                    if (thing is not Plant plant) continue;
                    plants++;
                    if (plant.IsCrop) crops++;
                }
        }
        catch (Exception) { plants = -1; }
        rows.Add(new Row("植物", $"{plants} 株（作物 {crops}）/ 发 {PlantSync.PlantsSent}", "PlantSync"));
        rows.Add(new Row("派系关系", FactionSync.Summary(), "FactionSync"));
        // Which ideoligion each faction believes. Separate row from the ideoligion table because the two answer different
        // questions and can fail independently: the table carries names/colours/points, this carries the assignment.
        rows.Add(new Row("派系信念", FactionIdeoSummary(), "FactionSync"));
        // Which maps exist. Every other map-keyed row below says "图0" or "图1" - this row is where those ids come from,
        // and a replica missing a map shows up here first.
        rows.Add(new Row("地图", MapLifecycleSync.Summary(), "MapLifecycleSync"));
        // How many pawns this peer had to move between maps because the host had them elsewhere. Zero on a single-map
        // colony; on a multi-settlement one a non-zero number is the evidence that map transitions travel at all. A
        // permanent zero next to pawns the player cannot find is the failure this row exists to name.
        // Read on the **replica**, not here: the transfer happens inside `AuthoritativeReplica`, and this census runs
        // on the dedicated server, which IS the authority and never relocates anything. Printing a bare `0` here would
        // read as "no pawn ever changed maps" - the same trap as `应用 0` in the flight row, which needed the probe's
        // own count beside it to mean anything. So the line says which side the number describes.
        rows.Add(new Row("跨图角色", SharedActions.PlayerActingNote() is { Length: > 0 } note && note.StartsWith("simulationPeer=True", StringComparison.Ordinal)
                ? "专服（权威端）：跨图转移只在副本上发生，这里恒为 0，此行测不到"
                : $"转移 {AuthoritativeReplica.MapRelocations} 次（失败 {AuthoritativeReplica.MapRelocateFailures}）",
            "AuthoritativeReplica.RelocateAcrossMaps"));
        rows.Add(new Row("任务", QuestSync.Summary(), "QuestSync"));
        // The counters inside a quest part. `QuestSync` above reports the standing of the quest itself (accepted, ended,
        // expiry); this row reports the parts list, whose per-part counters are what the quest tab actually prints.
        rows.Add(new Row("任务部件", QuestPartSync.Summary(), "QuestPartSync"));
        // Guilt / training decay / learning desire / flight, one row because they are one channel and one mechanism: a
        // counter on a `*Tracker` owned by a pawn, scribed but advanced only by that pawn's own tick.
        rows.Add(new Row("子追踪器", PawnTrackerSync.Summary(), "PawnTrackerSync"));
        rows.Add(new Row("手术账单", SharedActions.SurgerySummary(), "surgery-bill"));
        rows.Add(new Row("记忆", MemorySync.Summary(), "MemorySync"));
        // Tales are the colony's story log - the "art" descriptions and the history page draw from them. `TaleManager.Add`
        // runs only on the authority, and `InterestLevel` (which picks what an art piece is about) derives from `uses` and
        // `AgeTicks`; the age already follows `ReplicaAbsClockPatch`, so counting them says whether a channel is owed.
        var tales = 0;
        var unusedTales = 0;
        try
        {
            var manager = Find.TaleManager;
            foreach (var tale in manager?.AllTalesListForReading ?? [])
            {
                if (tale is null) continue;
                tales++;
                if (tale.Unused) unusedTales++;
            }
        }
        catch (Exception) { tales = -1; }
        rows.Add(new Row("传说", $"{tales} 条（未使用 {unusedTales}）/ 发 {TaleSync.TalesSent}", "TaleSync"));
        // How many pawns are mid-toil, next to how many entries the channel has sent. The first number is what tells "no
        // colonist is working" from "the channel is not producing"; on an idle colony zero is correct and only the pair
        // of numbers says which one it is.
        var working = 0;
        foreach (var toilMap in Find.Maps)
            foreach (var toiling in toilMap.mapPawns.AllPawnsSpawned)
                if (toiling?.jobs?.curDriver is not null) working++;
        // The other progress-bar family: the getter divides a counter the driver owns, and no payload carries one.
        // Reported with the counter names, because "0 with counters" and "the filter matched nothing" look identical.
        rows.Add(new Row("驱动计数", DriverStateSync.Preview() + $" / 发 {DriverStateSync.StatesSent}",
            "DriverStateSync"));
        rows.Add(new Row("工作进度", $"{working} 人在做活 / 发 {ToilProgressSync.StatesSent} 条", "ToilProgressSync"));
        // How many conditions carry a countdown, beside how many timer blocks have been sent. A zero here is correct when
        // nothing timed is running, and only the pair says whether the channel is idle or broken.
        var timed = 0;
        // Which comp the timed conditions actually carry, because the channel now covers seven countdowns and "3 timed" no
        // longer says whether the ones with a player-visible effect are present at all. A save with none of these comps is
        // the normal case in an early colony, so this line is often legitimately empty.
        var timeKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // Conditions whose age is past the bleeding-stop threshold, and the largest age seen. The bleeding case is the
        // one with a player-visible consequence (`BleedRate` returns 0 above it), and it is the only way to read
        // whether the age is actually travelling: "N timed" alone cannot tell a condition with an age from one
        // without.
        var agedConditions = 0;
        var oldestAge = 0;
        var agedField = HarmonyLib.AccessTools.Field(typeof(Hediff), "ageTicks");
        foreach (var timedMap in Find.Maps)
            foreach (var timedPawn in timedMap.mapPawns.AllPawnsSpawned)
                if (timedPawn?.health?.hediffSet?.hediffs is { Count: > 0 } conditions)
                    foreach (var condition in conditions)
                    {
                        if (agedField?.GetValue(condition) is int conditionAge && conditionAge > 0)
                        {
                            agedConditions++;
                            if (conditionAge > oldestAge) oldestAge = conditionAge;
                        }
                        if (condition?.def is null) continue;
                        // Every comp in `HediffTimerSync.Timers`, plus the float-charge one, counted the same way so the
                        // census row and the wire cannot drift apart.
                        foreach (var (compName, _, _, _) in HediffTimerSync.Timers)
                            if (HediffTimerSync.HasComp(condition, compName))
                            {
                                timed++;
                                timeKinds[compName] = timeKinds.TryGetValue(compName, out var n) ? n + 1 : 1;
                            }
                        if (HediffTimerSync.HasComp(condition, "HediffComp_Chargeable"))
                        {
                            timed++;
                            timeKinds["HediffComp_Chargeable"] = timeKinds.TryGetValue("HediffComp_Chargeable", out var c)
                                ? c + 1 : 1;
                        }
                    }
        // Which kinds of thing no sweep visits at all. The four sweeps (building, item, pawn, sown plant) are the only
        // mechanism that carries any scribed field, so a type outside them has fields nobody sends no matter how completely
        // its own `ExposeData` writes them. The comp row below answers this for comps; this one answers it for carriers, and
        // the gap list is full of types that are neither a building nor an item nor a pawn - `Fire`, `GameCondition`,
        // `HarbingerTree`, `Caravan_ForageTracker`. Naming them here is what turns "112 scribed fields" into a work list.
        var uncoveredThings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var coveredThingKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var deliberateThings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var carrierMap in Find.Maps)
            foreach (var carrier in carrierMap.listerThings.AllThings)
            {
                if (carrier is null || carrier.Destroyed || !carrier.Spawned) continue;
                // Must match the sweeps themselves, or this row keeps naming a carrier that is already carried and the
                // "fixed" reading looks like a regression. `Ethereal` was the one that fell through all four.
                // Wild plants are **deliberately** not swept: 35551 of them on this save, whose only reader is the inspect
                // pane's growth string, and a full cycle at 24 a frame would take an hour to come round. They belong in a
                // separate bucket from "nobody thought of this carrier", or the row can never reach zero and stops being a
                // work list - the same failure as a counter that is always red.
                var carrierDeliberate = carrier is Plant wild && !wild.sown;
                var carrierCovered = carrier is Pawn || carrier is Building
                    || carrier.def?.category is ThingCategory.Item or ThingCategory.Filth
                        or ThingCategory.Ethereal or ThingCategory.Attachment
                    || carrier is Plant { sown: true };
                var carrierKind = carrier.GetType().Name;
                var carrierBucket = carrierCovered ? coveredThingKinds
                    : carrierDeliberate ? deliberateThings : uncoveredThings;
                carrierBucket[carrierKind] = carrierBucket.TryGetValue(carrierKind, out var cn) ? cn + 1 : 1;
            }
        rows.Add(new Row("载体覆盖", $"{coveredThingKinds.Count} 种在补扫里 / {uncoveredThings.Count} 种在补扫外"
            + (uncoveredThings.Count == 0 ? ""
                : "：" + string.Join(",", uncoveredThings.Select(kv => kv.Key + "×" + kv.Value)))
            // Named separately so the row can reach zero: an intended exclusion that reads as a gap makes every later
            // round re-investigate the same 35551 wild plants.
            + (deliberateThings.Count == 0 ? ""
                : $"（刻意不扫 {deliberateThings.Count} 种：" + string.Join(",", deliberateThings.Select(kv => kv.Key + "×" + kv.Value)) + "）"),
            "HeadlessRuntime"));

        // Which comps live on things the three scribe sweeps never visit. Those sweeps are the only mechanism that
        // carries a comp field at all - each one re-runs `ScribeBridge.Save` on its object class and compares - so a comp
        // on anything outside them is a field nobody ever sends, no matter how completely its own `PostExposeData` writes
        // it. That is the distinction this row exists for: "comp with no ExposeData" and "comp the sweep never reaches"
        // look identical in the assembly listing and need completely different fixes.
        var CompsField = HarmonyLib.AccessTools.Field(typeof(ThingWithComps), "comps");
        var coveredComps = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var orphanComps = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var orphanThings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var sweepMap in Find.Maps)
            foreach (var sweepThing in sweepMap.listerThings.AllThings)
            {
                // `comps` is private on `ThingWithComps` and has no accessor, so this goes through the field. It is the
                // same list `ThingWithComps.PostExposeData` walks, which is why a comp here implies a scribed field.
                if (sweepThing is not ThingWithComps withComps) continue;
                if (CompsField?.GetValue(withComps) is not System.Collections.ICollection { Count: > 0 } compList) continue;
                // Same three classes the sweeps use, in the same order they test them.
                var covered = sweepThing is Pawn
                    || sweepThing.def?.category is ThingCategory.Item or ThingCategory.Filth
                        or ThingCategory.Ethereal or ThingCategory.Attachment
                    || sweepThing is Building or Plant;
                var bucket = covered ? coveredComps : orphanComps;
                foreach (var comp in compList)
                {
                    if (comp is not ThingComp liveComp) continue;
                    var compName = liveComp.GetType().Name;
                    bucket[compName] = bucket.TryGetValue(compName, out var n) ? n + 1 : 1;
                }
                if (!covered)
                {
                    var kind = sweepThing.GetType().Name;
                    orphanThings[kind] = orphanThings.TryGetValue(kind, out var m) ? m + 1 : 1;
                }
            }
        rows.Add(new Row("组件覆盖", $"{coveredComps.Count} 种在补扫里 / {orphanComps.Count} 种在补扫外"
            + (orphanComps.Count == 0 ? "（所有组件的载体都会被重新序列化）"
                : "：" + string.Join(",", orphanComps.Select(kv => kv.Key + "×" + kv.Value)))
            + (orphanThings.Count == 0 ? "" : " / 载体：" + string.Join(",", orphanThings.Select(kv => kv.Key + "×" + kv.Value)))
            // The plant sweep is what closes that gap, so it needs its own reading: scanned and sent are separate because
            // "0 scanned" is the map having no plants at all and "0 sent" is the payload never changing.
            + $" / 非三类补扫 扫 {HeadlessRuntime.PlantSweepScanned} 建 {HeadlessRuntime.PlantSweepSent}"
            + $"（有旧值可比 {HeadlessRuntime.PlantCompared}，表里 {HeadlessRuntime.PlantStateCount} 株）"
            // Sent and unchanged per carrier family, because the coverage row above only proves the filter admits a
            // category. A family that never appears in the "sent" list is a filter that matches but a payload that is
            // never produced, which reads identically as "covered" without this line.
            + $" / 分类发出 {HeadlessRuntime.PlantSweepByCarrier()}"
            + $" / 分类未变 {HeadlessRuntime.PlantSweepUnchangedByCarrier()}"
            // The raw XML beside the counters. Without it "扫 N 建 N" cannot be told apart from "the quantiser matches
            // nothing and every plant differs from itself every tick".
            // Flattened: the payload is multi-line XML and a census row is one line, so only the first line ever showed -
            // which read as "<saveable Class=\"Plant\">" and proved nothing at all.
            + (HeadlessRuntime.PlantSample is { Length: > 0 } plantWire
                ? " / 样本 " + plantWire.Replace("\r", " ").Replace("\n", " ") : ""),
            "HeadlessRuntime"));

        // Pawn whole-fragment cost, beside the channels that exist because it is not sent. Without this number a reader
        // finds `AuthoritativeFrame.PawnState = null` and reasonably concludes the pawn state is simply missing.
        {
            var fragmentMap = Find.CurrentMap;
            long bytes = 0;
            var pawns = 0;
            if (fragmentMap is not null)
                foreach (var pawn in fragmentMap.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is null) continue;
                    var fragment = ScribeBridge.Save(pawn);
                    if (fragment is null) continue;
                    bytes += fragment.Length;
                    pawns++;
                }
            rows.Add(new Row("整份小人片段", $"{pawns} 个小人 / 合计 {bytes} B / 平均 {(pawns == 0 ? 0 : bytes / pawns)} B"
                + $" / 位置帧约 {pawns * 40} B（倍数 {(pawns == 0 ? 0 : (double)bytes / (pawns * 40)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}）"
                + " / `PawnState` 因此传 null",
                "HeadlessRuntime"));
        }

        // Adaptation. The grace period is printed because both counters legitimately read 0.0 before it ends, which in a
        // single number is indistinguishable from a channel that never sends.
        rows.Add(new Row("剧情适应", $"适应 {AdaptationSync.AdaptDays.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} 天"
            + $" / 人口适应 {AdaptationSync.PopAdaptDays.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} 天"
            + $" / 发 {AdaptationSync.Sends} / 应用 {AdaptationSync.ApplyCount}（失败 {AdaptationSync.ApplyFailures}）"
            + $" / {AdaptationSync.Grace}",
            "AdaptationSync"));

        rows.Add(new Row("殖民地终局", GameEnderSync.Summary(), "GameEnderSync"));
        // Passing ships, beside the game-end row: both are global state that no sweep reaches. The ship count is printed
        // separately from the payload sample because `发 0` with ships present means the channel is stuck, while `发 0` with
        // no ships means there was nothing to send.
        {
            var shipCount = 0;
            foreach (var shipMap in Find.Maps)
                shipCount += shipMap.passingShipManager?.passingShips?.Count ?? 0;
            rows.Add(new Row("过境飞船", shipCount + " 艘 / " + PassingShipSync.Summary(), "PassingShipSync"));
        }
        // The leftover counters, one row: the census is a work list, so it has to show which of the nine have a carrier on
        // this save and which do not - "发 0" alone cannot tell "nothing changed" from "no such object exists".
        rows.Add(new Row("余量计数", ResidualCounterSync.Summary(), "ResidualCounterSync"));
        // A pawn's belief. Two numbers, because a channel that carries the table and no pawn reads the same as one that
        // carries nothing: "3 ideoligions / 0 pawns" is a real state (nobody converted) and "0 / 0" is broken.
        rows.Add(new Row("信念", IdeoSync.Summary(), "IdeoSync"));
        rows.Add(new Row("发现", DiscoverySync.Summary(), "DiscoverySync"));
        rows.Add(new Row("游戏设置", PlaySettingsSync.Summary(), "PlaySettingsSync"));
        // Drawn locally by every peer, decided by a stamp only the host writes: the census has to show whether the
        // stamp ever travelled, because "gizmo enabled" looks identical whether or not it did.
        rows.Add(new Row("首领召唤", BossgroupSync.Summary(), "BossgroupSync"));
        // The anomaly settings dialog writes five `Difficulty` fields; this row makes "nobody edited it" and "the edit
        // never travelled" different readings.
        rows.Add(new Row("异象难度", DifficultySync.Summary(), "DifficultySync"));
        rows.Add(new Row("尸体追踪", CorpseTrackerSync.Summary(), "CorpseTrackerSync"));
        // Visual-only Ethereal carriers (bombardments, debris, tornadoes). Without this row "no strike happened" and
        // "strikes happen but never reach a replica" read the same.
        rows.Add(new Row("视觉载体", EtherealVisualSync.Summary(), "EtherealVisualSync"));
        // 自动化也要受据点层约束：这条读数把「没跑过」与「全被拒」分开。
        rows.Add(new Row("自动重建", SettlementAutomationGate.Summary(), "SettlementAutomationGate"));
        // 别人的货被当成「禁止」的次数：一条读者通道，藏了多少与问了多少要分开看。
        rows.Add(new Row("据点禁取", SettlementForbidVisibility.Summary(), "SettlementForbidVisibility"));
        // 同一个读者在「派活」这一步的读数：拦下多少、以及它是不是一次都没跑过。
        rows.Add(new Row("据点派活", SettlementJobGiverGate.Summary(), "SettlementJobGiverGate"));
        // Auto-slaughter limits: a player-authored table the authority turns into slaughter orders.
        rows.Add(new Row("自动屠宰", AutoSlaughterSync.Summary(), "AutoSlaughterSync"));
        // 小人派系跟着 `ReplicaPawn` 走，没有自己的通道文件，所以普查行在这里手写。
        // 读数分三部分：本端有多少小人带派系、写进去多少次、失败多少次 —— 只报"写了 N 次"的话，
        // 「一个都没写」与「写了但全部失败」看起来一样。
        var factionPawns = 0;
        var factionNone = 0;
        var factionDistinct = new HashSet<int>();
        foreach (var eachMap in Find.Maps)
            foreach (var eachPawn in eachMap.mapPawns.AllPawnsSpawned)
            {
                if (eachPawn.Faction is null) factionNone++;
                else { factionPawns++; factionDistinct.Add(eachPawn.Faction.loadID); }
            }
        rows.Add(new Row("小人派系",
            $"带派系 {factionPawns} / 无派系 {factionNone}（{factionDistinct.Count} 个派系）"
            + $" / 下发写入 {AuthoritativeReplica.factionsApplied}（失败 {AuthoritativeReplica.factionApplyFailures}）",
            "ReplicaPawn.FactionLoadId"));



        // Abilities. `PawnState` is never sent, so nothing carries `charges`/cooldown at all - and the count alone
        // cannot show whether the encoding is one the replica parses, so the last payload is printed beside it.
        rows.Add(new Row("能力次数", $"{AbilitySync.TrackedCount} 个小人有载荷 / 发 {AbilitySync.StatesSent} 份（{AbilitySync.AbilitiesSent} 条能力）"
            + $" / 应用 {AbilitySync.ApplyCount}（失败 {AbilitySync.ApplyFailures}）"
            + (AbilitySync.LastSample is { Length: > 0 } abilityWire ? " / 样本 " + abilityWire : " / 样本（无）"),
            "AbilitySync"));

        rows.Add(new Row("伤势计时", $"{timed} 条带时限 / {agedConditions} 条带年龄（最老 {oldestAge} tick）"
            + $" / 发 {HediffTimerSync.StatesSent} 块"
            + (HediffTimerSync.LastSample is { Length: > 0 } timerSample ? " / 样本 " + timerSample : "")
            + (timeKinds.Count == 0 ? " / 七种计时 comp 一个都没有"
                : " / " + string.Join(",", timeKinds.Select(kv => kv.Key.Replace("HediffComp_", "") + "×"
                    + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            "HediffTimerSync"));
        // Active weather conditions per map, with their labels: "0 conditions" is a legitimate reading on a calm map and
        // says nothing about whether the channel works, so the labels are what make the number interpretable.
        var conditionText = new System.Text.StringBuilder();
        var conditionCount = 0;
        foreach (var conditionMap in Find.Maps)
        {
            var manager = conditionMap.gameConditionManager;
            if (manager is null) continue;
            foreach (var condition in manager.ActiveConditions)
            {
                conditionCount++;
                if (conditionText.Length > 0) conditionText.Append('/');
                conditionText.Append(condition?.def?.defName ?? "?");
            }
        }
        rows.Add(new Row("天气状况", $"{conditionCount} 个{(conditionText.Length > 0 ? "（" + conditionText + "）" : "")} / "
            + $"发 {GameConditionSync.ConditionsSent} / 建 {GameConditionSync.ConditionsCreated} / 撤 {GameConditionSync.ConditionsRemoved}",
            "GameConditionSync"));
        rows.Add(new Row("天气载荷", GameConditionSync.Preview(), "GameConditionSync(只读编码)"));
        // Ages, with the range so a frozen world reads differently from a broken channel: on a save that was loaded and
        // then paused, every pawn is at the age it was loaded with and the only visible fact is that none of them moved.
        var ageText = new System.Text.StringBuilder();
        var ageCount = 0;
        long ageMin = long.MaxValue, ageMax = long.MinValue;
        foreach (var ageMap in Find.Maps)
            foreach (var aged in ageMap.mapPawns.AllPawnsSpawned)
            {
                var tracker = aged?.ageTracker;
                if (tracker is null) continue;
                var ticks = tracker.AgeBiologicalTicks;
                if (ticks <= 0) continue;
                ageCount++;
                if (ticks < ageMin) ageMin = ticks;
                if (ticks > ageMax) ageMax = ticks;
            }
        if (ageCount > 0) ageText.Append($"{ageMin / 3600000f:0.0}–{ageMax / 3600000f:0.0} 岁");
        rows.Add(new Row("年龄", $"{ageCount} 个小人{(ageCount > 0 ? "（" + ageText + "）" : "")} / 发 {PawnAgeSync.AgesSent}",
            "PawnAgeSync"));
        // The two counters that ride in the same record, printed separately because the age line cannot show them: a pawn
        // whose growth bar is stuck and one whose age reversal deadline has already passed look identical under "N pawns".
        try
        {
            var withPoints = 0;
            var demanding = 0;
            var overdue = 0;
            // `growthPoints` has no public accessor and `AgeReversalDemandedDeadlineTicks` reports a huge number when there
            // is no demand at all, so both are read the way the channel itself reads them.
            var pointsField = HarmonyLib.AccessTools.Field(typeof(Pawn_AgeTracker), "growthPoints");
            var demandField = HarmonyLib.AccessTools.Field(typeof(Pawn_AgeTracker), "ageReversalDemandedAtAgeTicks");
            foreach (var growthMap in Find.Maps)
                foreach (var growing in growthMap.mapPawns.AllPawnsSpawned)
                {
                    var tracker = growing?.ageTracker;
                    if (tracker is null) continue;
                    if (pointsField?.GetValue(tracker) is float points && points > 0f) withPoints++;
                    if (demandField?.GetValue(tracker) is not long demandAt || demandAt == long.MaxValue) continue;
                    demanding++;
                    if (demandAt - tracker.AgeBiologicalTicks <= 0L) overdue++;
                }
            // "0 with growth points" is unreadable on its own, and `canGainGrowthPoints` does not separate the two cases:
            // it defaults to true and only turns false once a growth moment letter has been sent (`:135172`), so every
            // adult reports true while gaining nothing. The actual gate is `growthPoints >= 0` - the game scribes it with a
            // default of -1 (`:134897`) and only a growing child ever has the `Need_Learning` that advances it
            // (`:466702`). Counting that separates "channel fine, nobody can grow" from "channel dead".
            var eligible = 0;
            foreach (var growthMap in Find.Maps)
                foreach (var growing in growthMap.mapPawns.AllPawnsSpawned)
                    if (pointsField?.GetValue(growing?.ageTracker) is float points2 && points2 >= 0f) eligible++;
            var sample = PawnAgeSync.LastSample is { } s
                ? $"{s.Pawn}：{s.AgeTicks / 60000L}天 成长{s.Growth:0.00}/{s.GrowthPoints} 逆转剩{s.Reversal}"
                : "（本次无记录）";
            rows.Add(new Row("成长与逆转",
                $"{withPoints} 个有成长点（可成长 {eligible} 个）/ {demanding} 个有逆转要求（已过期 {overdue}）/ 样本 {sample}",
                "PawnAgeSync(growthPoints/逆转倒计时)"));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 成长普查失败：" + error.GetType().Name); }
        // Misery counters, with how many pawns are past the 2000-tick gate that arms a random break. "0 armed" on a happy
        // colony is correct and says nothing about the channel; the armed count is the number that would move.
        var armed = 0;
        var misery = 0;
        var minorField = HarmonyLib.AccessTools.Field(typeof(MentalBreaker), "ticksBelowMinor");
        var majorField = HarmonyLib.AccessTools.Field(typeof(MentalBreaker), "ticksBelowMajor");
        var extremeField = HarmonyLib.AccessTools.Field(typeof(MentalBreaker), "ticksBelowExtreme");
        foreach (var miseryMap in Find.Maps)
            foreach (var miserable in miseryMap.mapPawns.AllPawnsSpawned)
            {
                var breaker = miserable?.mindState?.mentalBreaker;
                if (breaker is null) continue;
                var minor = minorField?.GetValue(breaker) is int a ? a : 0;
                var major = majorField?.GetValue(breaker) is int b ? b : 0;
                var extreme = extremeField?.GetValue(breaker) is int c ? c : 0;
                if (minor > 0 || major > 0 || extreme > 0) misery++;
                if (minor >= 2000 || major >= 2000 || extreme >= 2000) armed++;
            }
        // The mood reading is included because the counter alone cannot be interpreted: "0 below threshold" is the correct
        // reading on a content colony and, identically, the reading for a counter that is never written. `CurMood` next to
        // the thresholds makes the pair readable, and the minimum mood says whether any pawn was even close.
        var lowestMood = float.MaxValue;
        var belowMinor = 0;
        foreach (var miseryMap2 in Find.Maps)
            foreach (var miserable2 in miseryMap2.mapPawns.AllPawnsSpawned)
            {
                var breaker2 = miserable2?.mindState?.mentalBreaker;
                if (breaker2 is null) continue;
                var mood = breaker2.CurMood;
                if (mood < lowestMood) lowestMood = mood;
                if (mood < breaker2.BreakThresholdMinor) belowMinor++;
            }
        var moodText = lowestMood < float.MaxValue ? $"最低心情 {lowestMood:0.00}，低于阈值 {belowMinor} 人" : "无";
        // Lords. `LordManager.LordManagerTick` lives inside `MapPostTick` (`:77229`) and is what advances
        // `Lord.ticksInToil` (`:268596`), so a replica that never advances the tick has every lord frozen at zero. The
        // row prints the toils and the counters so "no lords" can be told from "lords that are not moving".
        try
        {
            var lordCount = 0;
            var lordSample = "(无)";
            foreach (var lordMap in Find.Maps)
            {
                var manager = lordMap.lordManager;
                if (manager?.lords is not { Count: > 0 } lords) continue;
                lordCount += lords.Count;
                if (lordSample != "(无)") continue;
                var lord = lords[0];
                if (lord is null) continue;
                lordSample = $"{lord.LordJob?.GetType().Name ?? "?"}"
                    + $" 小人{lord.ownedPawns?.Count ?? 0}"
                    + $" 到点{lord.CurLordToil?.GetType().Name ?? "?"}"
                    + $" ticksInToil={lord.ticksInToil}"
                    + $" 得{lord.numPawnsEverGained} 损{lord.numPawnsLostViolently}"
                    // The ritual progress sum is the number the on-ground bar draws, and it is the one a frozen
                    // lord hides best: a ritual can be visibly under way while its bar sits at zero.
                    + (lord.LordJob is LordJob_Ritual ritual
                        ? $" 仪式 {ritual.Progress:0.000}（{ritual.TicksLeft}/{ritual.DurationTicks}）"
                        : string.Empty)
                    // The encoded string itself, not only the send count: a count that stays at its initial value
                    // while the live reading moves is exactly the reading that cannot tell "nothing changed" from
                    // "the publish path never ran".
                    + $" / 发 {LordSync.LordsSent} 载荷 {LordSync.LastSample?.Values ?? "(无)"}"
                    + $" 应用 {LordSync.LordsApplied}"
                    // **Lords the replica has never had.** A raid's lord is created after load (`MakeNewLord` `:269413`)
                    // and `Lord.ExposeData` runs once, so the replica genuinely lacks it. Silence here was
                    // indistinguishable from an idle channel.
                    + (LordSync.MissingLords > 0
                        ? $"；**本端没有的领主 {LordSync.MissingLords} 个**（权威端载入后新建：袭击/仪式/访客；本通道只发计数，不建领主）"
                        : string.Empty);
            }
            rows.Add(new Row("领主", lordCount == 0
                ? "0 个领主（全图没有集会 / 仪式 / 围攻）"
                : $"{lordCount} 个领主 / 样本 {lordSample}", "LordManager.lords"));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 领主普查失败：" + error.GetType().Name); }

        // `Pawn_StanceTracker.curStance` starts as `Stance_Mobile` and only its own tick replaces it, so a replica never
        // aims its weapon and never faces its target. Printed with the remaining ticks and the focus target, because
        // "N busy" cannot tell a stance that travels from one that is stuck at its first value.
        rows.Add(new Row("姿态", StanceSync.Preview(), "StanceSync"));
        // `Hediff_Deathrest.LabelInBrackets` returns this percentage directly, so it is the reading to print.
        rows.Add(new Row("死眠进度", DeathrestSync.Preview(), "DeathrestSync"));
        // The exact string that goes on the wire, beside the live reading. A value that is right in the first and absent
        // in the second is a channel that never fires; the reverse would be a payload built from something else.
        rows.Add(new Row("死眠载荷", DeathrestSync.LastSent(), "DeathrestSync(只读编码)"));
        // The state's own age, not the break counters above: `MentalState_Roaming.ShouldExitMapNow` decides from it
        // whether a roaming pawn has been out long enough to go home.
        rows.Add(new Row("精神状态", MentalStateSync.Preview(), "MentalStateSync"));
        rows.Add(new Row("崩溃累积", $"{misery} 人心情偏低（已达阈值 {armed} 人，{moodText}）"
            + $" / 发 {MentalBreakerSync.StatesSent}", "MentalBreakerSync"));
        // Immunity records beside the disease that needs them. A record exists per disease a pawn has met, so the pair
        // (records, diseases in progress) is what makes the number readable: zero records on a colony with no illness is
        // correct, zero records on a colony full of infections is a broken channel.
        var records = 0;
        var infected = 0;
        var immune = 0;
        foreach (var immunityMap in Find.Maps)
            foreach (var sick in immunityMap.mapPawns.AllPawnsSpawned)
            {
                if (sick?.health?.immunity?.ImmunityListForReading is { Count: > 0 } list) records += list.Count;
                if (sick?.health?.hediffSet?.hediffs is not { Count: > 0 } hediffs) continue;
                foreach (var hediff in hediffs)
                {
                    if (hediff?.def is null) continue;
                    var immunizable = hediff.TryGetComp<HediffComp_Immunizable>();
                    if (immunizable is null) continue;
                    infected++;
                    if (immunizable.FullyImmune) immune++;
                }
            }
        rows.Add(new Row("免疫", $"{records} 条记录 / {infected} 个可免疫病症（已免疫 {immune}）/ "
            + $"发 {ImmunitySync.StatesSent}", "ImmunitySync"));
        rows.Add(new Row("免疫载荷", ImmunitySync.Preview(), "ImmunitySync(只读编码)"));
        // Fires carry their size in a scribed field the thing-change tuple cannot see, so the only way to tell
        // "no fire is burning" from "fires exist and their size never travels" is to count them and show the
        // sizes. Without this row both cases read as the same absent line.
        {
            var fires = 0;
            var biggest = 0f;
            var grown = 0;
            try
            {
                foreach (var burningMap in Find.Maps)
                    foreach (var thing in burningMap.listerThings.ThingsInGroup(ThingRequestGroup.Fire))
                    {
                        if (thing is not Fire fire) continue;
                        fires++;
                        if (fire.fireSize > biggest) biggest = fire.fireSize;
                        if (fire.fireSize > 0.1001f) grown++;
                    }
            }
            catch (Exception error) { Log.Warning("[TurboTick] 火情普查失败：" + error.GetType().Name); }
            rows.Add(new Row("火情", $"{fires} 处燃烧（超出初始大小 {grown} 处，最大 {biggest:0.00}）",
                "DiffThings.FireSlot"));
            // The count above proves a fire exists; this proves the number that matters is inside the payload that
            // would travel. They are different claims: `DiffThings` compares `FireSlot`, but what actually reaches the
            // replica is `ScribeBridge.Save(fire)` - and if `ExposeData` did not write `fireSize`, the slot would be
            // change detection over a field that is never carried.
            var firePayload = "(无)";
            try
            {
                foreach (var burningMap in Find.Maps)
                    foreach (var thing in burningMap.listerThings.ThingsInGroup(ThingRequestGroup.Fire))
                    {
                        if (thing is not Fire sample || sample is not IExposable exposable) continue;
                        var xml = ScribeBridge.Save(exposable) ?? "(空)";
                        var at = xml.IndexOf("fireSize", StringComparison.Ordinal);
                        firePayload = at < 0
                            ? $"{sample.fireSize:0.00} 的载荷里没有 fireSize（{xml.Length} B）"
                            : xml.Substring(at, Math.Min(40, xml.Length - at));
                        break;
                    }
            }
            catch (Exception error) { firePayload = "读取失败：" + error.GetType().Name; }
            rows.Add(new Row("火情载荷", firePayload, "ScribeBridge.Save(Fire)"));
        // A worktable's bill stack rides inside the building sweep's Scribe payload, so the three
        // `Bill_Production` settings that appear in the gap list may already be travelling. Printing the saved
        // fragment is the only way to tell "not sent" from "sent and never looked at".
        try
        {
            var tables = 0;
            var withBills = 0;
            var billSample = "(无)";
            foreach (var billMap in Find.Maps)
                foreach (var candidate in billMap.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                {
                    if (candidate is not Building_WorkTable table) continue;
                    tables++;
                    var count = table.billStack?.Count ?? 0;
                    if (count == 0) continue;
                    withBills++;
                    if (billSample != "(无)") continue;
                    var xml = ScribeBridge.Save(table) ?? "";
                    var at = xml.IndexOf("repeatCount", StringComparison.Ordinal);
                    billSample = at < 0
                        ? $"{table.def.defName} 有 {count} 条配方，但载荷里没有 repeatCount（{xml.Length} B）"
                        : xml.Substring(at, Math.Min(60, xml.Length - at));
                }
            // The write path is a separate claim from the read path: `BillStack` riding inside the bench's fragment
            // says the authority's bills reach a replica, and the new dialog capture says a replica's click reaches the
            // authority. Printing only the first would report a covered feature while clicks still went nowhere.
            rows.Add(new Row("工作台配方", $"{tables} 台工作台 / {withBills} 台带配方 / 样本 {billSample}"
                + $" / 副本写入 {SharedActions.BillApply} 次（本端读数为 0 属正常：这是接收端计数）",
                "ScribeBridge.Save(Building_WorkTable)"));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 配方普查失败：" + error.GetType().Name); }
        }

        // Skyfallers, with the number that decides how high they are drawn. `Position` is the landing cell and never
        // moves, so a census that only counted them would say nothing about whether they descend on a replica.
        try
        {
            var falling = 0;
            var fallSample = "(无)";
            var fallField = HarmonyLib.AccessTools.Field(typeof(Skyfaller), "ticksToImpact");
            foreach (var fallMap in Find.Maps)
                foreach (var thing in fallMap.listerThings.AllThings)
                {
                    if (thing is not Skyfaller skyfaller) continue;
                    falling++;
                    if (fallSample != "(无)") continue;
                    var left = fallField?.GetValue(skyfaller) as int?;
                    fallSample = $"{skyfaller.def.defName} 剩 {left?.ToString() ?? "?"} tick"
                        + $"，槽 {HeadlessRuntime.FallSlot(skyfaller)}"
                        + $"，载荷 {ScribeBridge.Save(skyfaller)?.Length ?? 0} B";
                }
            rows.Add(new Row("坠落物", falling == 0
                ? "0 个在坠落（没有空投舱或残骸在下落）"
                : $"{falling} 个在坠落 / 样本 {fallSample}", "DiffThings.FallSlot"));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 坠落物普查失败：" + error.GetType().Name); }

        // Projectiles, with the numbers that decide where they are drawn. `DrawPos` is `ExactPosition`, computed from
        // origin, destination and ticksToImpact - not from `Position` - so a census that only counted projectiles would
        // say nothing about whether the bolt moves on a replica.
        try
        {
            var inFlight = 0;
            var sample = "(无)";
            var ticksField = HarmonyLib.AccessTools.Field(typeof(Projectile), "ticksToImpact");
            foreach (var flyingMap in Find.Maps)
                foreach (var thing in flyingMap.listerThings.AllThings)
                {
                    if (thing is not Projectile projectile) continue;
                    inFlight++;
                    if (sample != "(无)" || ticksField is null) continue;
                    var left = ticksField.GetValue(projectile) as int?;
                    sample = $"{projectile.def.defName} 剩 {left?.ToString() ?? "?"} tick"
                        + $"，槽 {HeadlessRuntime.FlightSlot(projectile)}"
                        + $"，载荷 {ScribeBridge.Save(projectile)?.Length ?? 0} B";
                }
            rows.Add(new Row("飞行物", inFlight == 0
                ? "0 个在飞（没有投射物在空中）"
                : $"{inFlight} 个在飞 / 样本 {sample}", "DiffThings.FlightSlot"));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 飞行物普查失败：" + error.GetType().Name); }
        // World objects are not map things: they live in `Find.WorldObjects`, have a `Tile` and move through
        // `Caravan_PathFollower.TryEnterNextPathTile` on an interval tick a replica never runs. Counted before deciding the
        // shape, because a caravan's position is what the world map draws and the colony panel lists.
        var caravans = 0;
        var worldObjects = 0;
        try
        {
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                worldObjects++;
                if (each is Caravan) caravans++;
            }
        }
        catch (Exception) { worldObjects = -1; }
        rows.Add(new Row("世界物件", $"{worldObjects} 个（商队 {caravans}）/ 发 {WorldObjectSync.ObjectsSent}",
            "WorldObjectSync"));
        // The position channel above carries four numbers per object; this one carries everything else the object scribes.
        // Kept apart because they fail for different reasons: a missing tile means the mover hook never fired, a missing
        // fragment means the strip list removed the whole document or Scribe refused to run.
        // Creation and destruction, which neither row above can report: both start by looking the id up, so an object
        // the authority created is invisible here and one it destroyed stays visible. Zero on a colony where nothing
        // came or went, which is why the sample column is printed beside the count.
        rows.Add(new Row("世界物件增删", WorldObjectLifecycleSync.Summary(), "WorldObjectLifecycleSync"));
        rows.Add(new Row("世界物件状态", WorldObjectStateSync.Summary()
            // The comp-bearing sample, not the last-sent one: on this save the last object sent had no countdown and the
            // row read "no comp state" while 495 fragments had been sent.
            + (WorldObjectStateSync.CountdownSample is { Length: > 0 } compSample
                ? " / 组件样本 " + compSample
                : WorldObjectStateSync.LastSample is { Length: > 0 } worldSample ? " / 样本 " + worldSample : ""),
            "WorldObjectStateSync"));
        // The building sweep is the only path that carries `workDone`, so a blueprint's construction progress depends on it
        // being called at all - and its summary only prints when a frame is sent, which never happens with no replica
        // connected. Reporting it here makes it observable without one.
        // The sweep iterates `allBuildingsColonist` only. Reporting that list's own size next to the door count is what
        // tells "the sweep found nothing" apart from "the sweep looks at a list that does not contain the doors I counted".
        // Over *all* maps, not just `Find.CurrentMap`: the sweep iterates `Find.Maps`, so a census that reported one map
        // would show "0 buildings swept" on a server whose colony lives on another map - and that reads as "the sweep is
        // broken" when it is doing its job elsewhere.
        var colonistBuildings = 0;
        var artificialBuildings = 0;
        var allDoors = 0;
        try
        {
            foreach (var each in Find.Maps)
            {
                colonistBuildings += each.listerBuildings.allBuildingsColonist?.Count ?? 0;
                artificialBuildings += each.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).Count;
                allDoors += CountDoors(each);
            }
        }
        catch (Exception) { }
        rows.Add(new Row("门", allDoors.ToString(), "DoorSync"));
        rows.Add(new Row("建筑补扫", $"{HeadlessRuntime.SweepSummary()}；建筑 {artificialBuildings} 栋（玩家 {colonistBuildings}）",
            "ReplicaThing"));
        rows.Add(new Row("框架（建造中）", CountFrames(map).ToString(), "ReplicaThing(workDone)"));

        // Per-pawn state. The pawn itself travels, because it is a Thing; what does not travel is the sub-object a tick
        // would have written. Counting pawns that actually hold such a state is exactly the question "has this channel
        // anything to carry".
        var spawned = 0;
        var mental = 0;
        var inspired = 0;
        var guests = 0;
        var skilled = 0;
        // `HostFaction` is written only by `Pawn_GuestTracker.SetGuestStatus` (`:461540-461541`), so a replica that writes
        // `guestStatusInt` alone kept its load-time host forever. It is the field `ITab_Pawn_Health.ShouldAllowOperations`
        // (`:667161-667163`) reads to allow surgery on a downed guest, so "how many pawns even have a host" is the
        // question that says whether the fourth guest field has anything to carry.
        var withHost = 0;
        // Round 146: the three fields of the prisoner management panel. Counted here, next to `withHost`, because the
        // applied counters live on the receiving side and are structurally 0 on this one - so "no prisoner uses a mode"
        // and "the mode never travelled" would look the same in this table.
        var withMode = 0;
        var withConversion = 0;
        var withNonExclusive = 0;
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is null) continue;
            spawned++;
            if (pawn.mindState?.mentalStateHandler?.InMentalState == true) mental++;
            if (pawn.mindState?.inspirationHandler?.Inspired == true) inspired++;
            if (pawn.guest is { } guest && guest.GuestStatus != GuestStatus.Guest) guests++;
            if (pawn.guest?.HostFaction is not null) withHost++;
            if (pawn.guest is { } guestMode)
            {
                if (guestMode.ExclusiveInteractionMode != PrisonerInteractionModeDefOf.MaintainOnly) withMode++;
                if (guestMode.ideoForConversion is not null) withConversion++;
                foreach (var def in DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading)
                    if (def is { isNonExclusiveInteraction: true } && guestMode.IsInteractionEnabled(def))
                    {
                        withNonExclusive++;
                        break;
                    }
            }
            if (pawn.skills?.skills is { Count: > 0 }) skilled++;
        }
        rows.Add(new Row("小人", spawned.ToString(), "ReplicaPawn"));
        rows.Add(new Row("　其中崩溃", mental.ToString(), "MentalStateSync"));
        rows.Add(new Row("　其中灵感", inspired.ToString(), "InspirationSync"));
        rows.Add(new Row("　其中囚犯/奴隶",
            $"{guests}（有主家派系 {withHost}） / 互动模式 {withMode} 个非默认 / 皈依目标 {withConversion} 个"
            + $" / 非排他互动 {withNonExclusive} 个开启", "GuestSync"));
        rows.Add(new Row("　其中有技能", skilled.ToString(), "SkillSync"));

        // Global databases. These live on the Game rather than a map, so a save round trip carries them once and a plan
        // created afterwards reaches nobody. The count is the only way to notice a policy that never travelled.
        rows.Add(new Row("服装方案", (game.outfitDatabase?.AllOutfits?.Count ?? 0).ToString(), "SharedActions(outfits)"));
        rows.Add(new Row("药品方案", (game.drugPolicyDatabase?.AllPolicies?.Count ?? 0).ToString(),
            "SharedActions(drugs)"));
        // The four tables themselves (round 160). Before this row existed, "方案 3 个" was indistinguishable from
        // "方案从没送出去": the counts above come from the local database, which a replica fills on its own.
        // Factions the authority added at runtime (round 162). "19 factions" on both ends used to be the only
        // reading, and it cannot tell "nothing new was created" from "one was created and never travelled".
        rows.Add(new Row("派系新建", FactionCreationSync.Summary(), "FactionCreationSync"));
        rows.Add(new Row("方案表", PolicyTableSync.Summary(), "PolicyTableSync"));
        rows.Add(new Row("食物方案", (game.foodRestrictionDatabase?.AllFoodRestrictions?.Count ?? 0).ToString(),
            "SharedActions(food)"));

        var text = new StringBuilder(1024);
        // The pause banner is the difference between "this channel is broken" and "no frame has been published since the
        // first one". Every counter below is maintained by a channel that only moves inside `PublishFrame`, which
        // `SharedColony.OnLocalTick` drives - and a room with no replicas is paused by design, so on an idle server every
        // count freezes at the first frame. Measured: `植物 … 发 64` unchanged across three samples four seconds apart,
        // which reads as a stalled channel and is actually a server nobody is watching.
        // Two independent reasons the counters can stand still, and they are worth separating: `ReplicaCount == 0` means no
        // frame is published at all (`PublishFrame` runs for an audience), while `TicksGame` not advancing means the world
        // itself is paused - a paused world with a replica still publishes, it just never moves. Measured together they read
        // `速度=Paused` with `副本=0`, which is exactly the combination that made every channel look dead.
        var why = new StringBuilder(96);
        if (HeadlessRuntime.ReplicaCount <= 0) why.Append("房间无副本(PublishFrame 不跑)");
        if (Find.TickManager is { Paused: true }) why.Append(why.Length > 0 ? " + 世界暂停" : "世界暂停");
        text.Append(why.Length > 0
            ? "同步普查（容器 / 通道计数）— " + why + "，以下计数停在首帧"
            : "同步普查（容器 / 通道计数）");
        // One line about every `应用` counter in this table, instead of a caveat on each of the forty rows.
        //
        // This census runs on the dedicated server, which *is* the authority: it publishes frames and never applies
        // one, so every `应用 N` here is structurally zero and says nothing about whether the channel works. That
        // reading has already been mistaken for "the channel is dead" twice - once on the flight row (`应用 0` beside
        // the probe's `应用 40`) and once on the faction belief row. The receive-side numbers are only meaningful from
        // a replica's own census, which this command cannot produce.
        if (SharedActions.SimulationPeerProcess)
            text.Append("\n  注：本端是专服（权威端），下表所有「应用 N」结构性为 0 —— 它表示「本端不应用别人的帧」，"
                + "不表示通道没工作。接收侧要看副本自己的普查。");
        foreach (var row in rows)
            text.Append('\n').Append("  ").Append(row.Container).Append("：").Append(row.Amount)
                .Append("  ← ").Append(row.Channel);
        return text.ToString();
    }

    /// <summary>Blueprints and frames under construction. `Frame.workDone` is accumulated by
    /// `JobDriver_ConstructFinishFrame` - the build work toil - so it is one of the numbers a replica freezes; counting them
    /// says whether the sweep has anything to carry.</summary>
    /// <summary>Indoor rooms across every map, alongside the total and any failure.
    ///
    /// <para>Three numbers rather than one because "0 indoor rooms" is the reading for three very different situations: a map
    /// with no rooms at all, a map whose rooms all count as outdoors because they are unroofed, and this method throwing.
    /// A `catch { return 0; }` makes the third indistinguishable from the first two - which is the exact shape of failure
    /// this project keeps running into.</para></summary>
    private static string RoomTempCounts()
    {
        var indoor = 0;
        var total = 0;
        try
        {
            foreach (var each in Find.Maps)
            {
                if (each.regionGrid is null) continue;
                foreach (var room in each.regionGrid.AllRooms)
                {
                    if (room is null) continue;
                    total++;
                    if (!room.UsesOutdoorTemperature) indoor++;
                }
            }
        }
        catch (Exception error)
        {
            return $"读取失败（{error.GetType().Name}）/ 已数 室内 {indoor} 共 {total} / 发 {RoomTempSync.RoomsSent}";
        }
        // The district count is what separates "no rooms because nothing has been built into rooms yet" from "no rooms
        // because the grid was never built": regions produce districts, districts produce rooms, and a grid with districts
        // but no rooms has a different bug from a grid with neither.
        var districts = 0;
        try
        {
            foreach (var each in Find.Maps)
                districts += each.regionGrid?.allDistricts?.Count ?? 0;
        }
        catch (Exception) { districts = -1; }
        return $"{indoor} 间室内 / 共 {total} 间 / 区 {districts} / 发 {RoomTempSync.RoomsSent}";
    }

    private static int CountFrames(Map map)
    {
        try
        {
            var count = 0;
            foreach (var building in map.listerBuildings.allBuildingsColonist)
                if (building is Frame) count++;
            return count;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Faction beliefs, counted on this end. Reports how many factions actually carry a primary ideoligion
    /// plus the largest minor list, because "0 sent" has two causes: every belief already matched, or nothing here has
    /// one at all. The detail counts come from the authority's own last publish; the local numbers say whether there was
    /// anything to describe.</summary>
    private static string FactionIdeoSummary()
    {
        var withPrimary = 0;
        var minorRows = 0;
        var largest = 0;
        var total = 0;
        try
        {
            var factions = Find.FactionManager?.AllFactionsListForReading;
            if (factions is not null)
            {
                foreach (var faction in factions)
                {
                    if (faction?.ideos is null) continue;
                    total++;
                    if (faction.ideos.PrimaryIdeo is not null) withPrimary++;
                    var minors = faction.ideos.IdeosMinorListForReading;
                    if (minors is null) continue;
                    minorRows += minors.Count;
                    if (minors.Count > largest) largest = minors.Count;
                }
            }
        }
        catch (Exception) { return "读取失败"; }
        return $"本端 {total} 个派系有 tracker，{withPrimary} 个有主信念（次信念共 {minorRows} 条，最长 {largest}）"
            + $" / 发 {FactionSync.IdeoSends}（{FactionSync.IdeoWithPrimary} 个有主，{FactionSync.IdeoMinorRows} 条次）"
            + $" / 应用 {FactionSync.IdeoApplies}（失败 {FactionSync.IdeoFailures}）";
    }

    private static int CountDoors(Map map)
    {
        try
        {
            var count = 0;
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                if (thing is Building_Door) count++;
            return count;
        }
        catch (Exception) { return 0; }
    }
}