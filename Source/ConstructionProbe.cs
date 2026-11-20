using System.Globalization;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Asks the game why a <c>Frame</c> is not being built, on the authority, every few hundred ticks. Off unless
/// <c>TURBOTICK_PROBE_CONSTRUCT</c> names a start tick.
///
/// <para>Why this exists: 52 <c>Frame_Wall</c> stood on the map with loose wood beside them, three colonists existed,
/// and every colonist's job was <c>GotoWander</c>/<c>Wait_Wander</c>. The frame log said `工作类 0/空闲类 3`, which
/// says only "nobody has work" - it cannot distinguish "no giver offered a job" from "a giver offered one and the
/// job was refused", and those have entirely different fixes. Guessing at that boundary already cost this project a
/// whole stage once (the blueprint that existed on one peer and was filtered out of the diff by
/// <c>ThingCategory.Ethereal</c>).</para>
///
/// <para>So the answer is taken from the game's own decision procedure, in the same order the givers use, with
/// <see cref="JobFailReason.Reason"/> read after each refusal: that string is the sentence vanilla writes inside
/// <c>GenConstruct.CanConstruct</c> and <c>CanGetResources_NewTemp</c>, so it names the exact clause that said no
/// ("NotAssignedToWorkType", "NoPath", "ReservedBy", a skill threshold...).</para>
///
/// <para>The checks are duplicated here rather than called through the giver's <c>JobOnThing</c>... because
/// <c>JobOnThing</c> answers the final question but not which clause refused, and the clauses are short, public and
/// stable - <c>Frame.IsCompleted</c>, <c>GenConstruct.CanTouchTargetFromValidCell</c>,
/// <c>FirstBlockingThing</c>, <c>CanConstruct</c>, <c>CanGetResources_NewTemp</c>. Each one is reported separately, so
/// a frame that fails the fourth clause and one that fails the fifth cannot read the same.</para>
/// </summary>
internal static class ConstructionProbe
{
    private static readonly int StartTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_CONSTRUCT"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var start) ? start : -1;

    private static readonly int Gap = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_CONSTRUCT_GAP"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var gap) && gap > 0 ? gap : 300;

    private static int nextTick = -1;
    private static int rounds;

    /// <summary>Runs the diagnostic right now and returns its text, for the portal's <c>construct-report</c>.
    ///
    /// <para>Separate from <see cref="Tick"/> because the two callers have opposite requirements: the periodic probe
    /// wants an env-var gate and a gap, while the portal command must answer the question as it stands *now* - and,
    /// critically, without a container restart. A restart reloads from the checkpoint and throws away the colony
    /// state the question is about, which is how the first attempt at this measurement produced nothing.</para>
    /// </summary>
    internal static (bool Ok, string Code, string Detail) DescribeNow()
    {
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing)
            return (false, "no-game", "还没有载入存档");
        var map = Find.CurrentMap;
        if (map is null) return (false, "no-map", "没有当前地图");
        try
        {
            var text = new StringBuilder();
            var frames = new List<Thing>();
            var blueprints = new List<Thing>();
            // Every map, not just `Find.CurrentMap`: a headless resident can have the report asked while the
            // current map is not the colony (an encounter map, or none yet). A probe that silently looks at the
            // wrong map answers "0 blueprints" about a colony full of them, which is exactly the kind of green
            // number that hid this defect for days.
            var blueprintDefs = new Dictionary<string, int>(StringComparer.Ordinal);
            var mapsScanned = 0;
            foreach (var scanned in Find.Maps)
            {
                if (scanned is null) continue;
                mapsScanned++;
                foreach (var thing in scanned.listerThings.AllThings)
                {
                    if (thing.Destroyed || !thing.Spawned) continue;
                    if (thing is Frame frame2) { frames.Add(frame2); continue; }
                    if (thing is not Blueprint blueprint2) continue;
                    blueprints.Add(blueprint2);
                    var name = blueprint2.def?.defName ?? "?";
                    blueprintDefs[name] = blueprintDefs.TryGetValue(name, out var seen) ? seen + 1 : 1;
                }
            }
            // Blueprints first: they are the state the player's screenshot shows, and a frame only exists after the
            // materials arrived. A probe that reads only frames reports "nothing to explain" on exactly the colony
            // that is stuck, which is how the first attempt produced no output at all.
            text.Append($"建造诊断 tick={Find.TickManager?.TicksGame ?? -1}：扫描 {mapsScanned} 张地图，"
                + $"{blueprints.Count} 个建造蓝图、{frames.Count} 个建造架，殖民者 "
                + $"{map.mapPawns.FreeColonistsSpawned.Count()} 人（"
                + string.Join(",", map.mapPawns.FreeColonistsSpawned
                    .Select(p => p.LabelShort + ":" + (p.CurJobDef?.defName ?? "无job")))
                + $")，原木 地面上{map.resourceCounter.GetCount(ThingDefOf.WoodLog)} 个。");
            // The def breakdown is what distinguishes "the colony has no construction at all" from "the probe is
            // looking at the wrong object type": both read as a count of zero otherwise.
            if (blueprintDefs.Count > 0)
                text.Append(" 蓝图种类：" + string.Join("，",
                    blueprintDefs.OrderByDescending(pair => pair.Value).Select(pair => pair.Key + "×" + pair.Value)));
            // 地图指纹。`tick=0` 与「地上 0 个原木」同时出现时，最可能的解释不是「东西被销毁了」，而是
            // **这一端模拟的根本不是玩家看到的那张地图**（新开局 tick 从 0 起、地图上没有玩家放的木头）。
            // 没有这串数字，两种情形在报告里长得一模一样，而它们的修法完全不同。
            var tickManager = Find.TickManager;
            text.Append(" 地图指纹：").Append("ticksGame=").Append(tickManager?.TicksGame ?? -1)
                .Append("/ticksAbs=").Append(tickManager?.TicksAbs ?? -1)
                .Append("/速度=").Append(tickManager?.CurTimeSpeed.ToString() ?? "无")
                .Append("/uniqueID=").Append(map.uniqueID)
                .Append("/物体=").Append(map.listerThings.AllThings.Count)
                .Append("/墙=").Append(map.listerThings.ThingsOfDef(ThingDefOf.Wall).Count)
                .Append("/蓝图总数=").Append(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count)
                .Append("/建造架总数=").Append(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count)
                .Append('。');
            for (var i = 0; i < Math.Min(2, blueprints.Count); i++)
                text.Append(Environment.NewLine).Append(Explain(blueprints[i], map));
            for (var i = 0; i < Math.Min(2, frames.Count); i++)
                text.Append(Environment.NewLine).Append(Explain(frames[i], map));
            // The giver-level question is asked separately: `Explain` walks the clauses of one giver and answers
            // "would this frame be accepted"; this answers "did any work type even get considered", which is where
            // `EverWork` and priorities live.
            text.Append(Environment.NewLine).Append(DescribeWorkTypes(map));
            Log.Message("[TurboTick 建造] " + text);
            return (true, "ok", text.ToString());
        }
        catch (Exception error)
        {
            return (false, "failed", error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Per-colonist work settings and the first work type vanilla would consider.</summary>
    private static string DescribeWorkTypes(Map map)
    {
        var text = new StringBuilder("  工作设置：");
        foreach (var pawn in mappawns(map))
        {
            text.Append(pawn.LabelShort).Append('=');
            if (pawn.workSettings is null) { text.Append("无workSettings；"); continue; }
            if (!pawn.workSettings.EverWork) { text.Append("未初始化(EverWork=false)；"); continue; }
            text.Append(pawn.workSettings.WorkGiversInOrderNormal.Count).Append("个giver");
            text.Append(",建筑=").Append(pawn.workSettings.GetPriority(WorkTypeDefOf.Construction));
            text.Append(",指派=").Append(pawn.timetable?.CurrentAssignment?.defName ?? "无");
            // `lastJobTag` decides whether the idle alert fires (`Verse.AI/Pawn_MindState.cs:232`) and is written
            // only by `Pawn_JobTracker.StartJob`. Printing it here is what makes "the alert says idle while the pawn
            // works" diagnosable without a screenshot: the authority's own value is the one a replica must mirror.
            text.Append(",tag=").Append(pawn.mindState?.lastJobTag.ToString() ?? "空");
            text.Append(",空闲=").Append(pawn.mindState?.IsIdle == true ? "是" : "否");
            text.Append('；');
        }
        return text.ToString();
    }

    private static IEnumerable<Pawn> mappawns(Map map) => map.mapPawns.FreeColonistsSpawned;

    internal static void Tick()
    {
        if (StartTick < 0) return;
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        var now = manager.TicksGame;
        if (now < StartTick) return;
        if (nextTick < 0) nextTick = StartTick;
        if (now < nextTick) return;
        nextTick = now + Gap;

        var map = Find.CurrentMap;
        if (map is null) { Log.Warning("[TurboTick probe] 建造探针：没有当前地图"); return; }

        var frames = new List<Frame>();
        foreach (var thing in map.listerThings.AllThings)
            if (thing is Frame frame && !frame.Destroyed && frame.Spawned) frames.Add(frame);

        Log.Message($"[TurboTick probe] 建造探针 #{++rounds} tick={now}：地图 {frames.Count} 个建造架。"
            + Environment.NewLine + $"  地图形势：殖民者 {map.mapPawns.FreeColonistsSpawned.Count()} 人（"
            + string.Join(",", map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort + ":" + (p.CurJobDef?.defName ?? "无job")))
            + $")，原木 {map.resourceCounter.GetCount(ThingDefOf.WoodLog)} 个。");
        if (frames.Count == 0) return;

        // One frame is enough to explain the class of failure: the population is homogeneous (same def, same stuff,
        // same wall ring), so reporting every one of 52 would only bury the answer.
        for (var i = 0; i < Math.Min(3, frames.Count); i++)
            Log.Message(Explain(frames[i], map));
    }

    /// <summary>Walks the givers in vanilla's order and returns the first clause that refuses, for each colonist.
    ///
    /// <para>Takes <see cref="IConstructible"/> rather than <c>Frame</c>: a blueprint and a frame are the same question
    /// to a work giver - the only difference is which giver applies - and the player's report is about blueprints.
    /// Both expose <c>TotalMaterialCost</c>, <c>IsCompleted</c>, <c>ThingCountNeeded</c> and
    /// <c>EntityToBuildStuff</c> through that interface, so one path explains both.</para></summary>
    private static string Explain(Thing thing, Map map)
    {
        if (thing is not IConstructible constructible)
            return $"[TurboTick probe]   {thing.def.defName} 不是 IConstructible，跳过";
        var text = new StringBuilder();
        var stuff = constructible.EntityToBuildStuff();
        text.Append($"[TurboTick probe]   {thing.def.defName} id={thing.thingIDNumber} @{thing.Position.x},{thing.Position.z}"
            + $" stuff={stuff?.defName ?? "无"} faction={thing.Faction?.Name ?? "无"}"
            + $" 已完成={constructible.IsCompleted()}");
        if (thing is Frame workFrame)
            text.Append($" workDone={workFrame.workDone:0.#}/{workFrame.WorkToBuild:0.#}");
        text.Append(Environment.NewLine).Append("      材料账：");
        var container = thing.TryGetInnerInteractableThingOwner();
        if (container is null)
        {
            // A blueprint has no container at all - `resourceContainer` is a field of `Frame` alone
            // (`RimWorld/Frame.cs:13`), which is what the player's "原木 0/5" overlay shows once the blueprint has
            // become a frame. So "no container" is the normal state of a blueprint and says nothing about why it is
            // stuck. The first version of this method returned here, which threw away the whole per-colonist walk
            // below - the only part that can answer the question - for every blueprint in the colony. Measured: the
            // report read `材料账：没有容器` and then jumped straight to the work settings, on four blueprints that had
            // been standing for twenty thousand ticks.
            text.Append(thing is Frame ? "没有容器（异常）" : "蓝图无容器（正常，材料由搬运者直接带入建造架）");
        }
        else foreach (var cost in constructible.TotalMaterialCost())
        {
            int neededNow = -1, enroute = -1, space = -1, held = -1;
            try { neededNow = constructible.ThingCountNeeded(cost.thingDef); } catch (Exception) { }
            try { held = container.GetCountCanAccept(cost.thingDef is null ? null! : thing) ; } catch (Exception) { }
            try
            {
                held = 0;
                for (var i = 0; i < container.Count; i++)
                    if (container[i]?.def == cost.thingDef) held += container[i].stackCount;
            }
            catch (Exception) { held = -1; }
            if (thing is IHaulEnroute enrouteHolder)
            {
                try { enroute = map.enrouteManager.GetEnroute(enrouteHolder, cost.thingDef, null); } catch (Exception) { }
                try { space = EnrouteUtility.GetSpaceRemainingWithEnroute(enrouteHolder, cost.thingDef, null); }
                catch (Exception) { }
            }
            text.Append($"{cost.thingDef.defName} 需{cost.count} 容器内{held}"
                + $" 尚缺{neededNow} 途中{enroute} 可放{space}；");
        }
        // `ThingCountNeededWithEnroute` subtracts the enroute manager's promises from what the frame still wants, and
        // `DepositHauledThingInContainer` clamps the deposit to `GetSpaceRemainingWithEnroute`. A stale enroute (a haul
        // promised here and never delivered, or one released on another peer) therefore makes the target look full:
        // the colonist walks over, deposits zero, drops the wood where it stands, and the next colonist picks it up.
        // That is the loop the player is watching, and these numbers are what tell it apart from "no giver".

        foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
        {
            text.Append(Environment.NewLine).Append("    ").Append(pawn.LabelShort).Append("：");
            // 0. work settings. `EverWork` false makes JobGiver_Work return priority 0, so no work type is even
            //    considered - measured on this colony as three colonists wandering with full priorities on disk.
            if (pawn.workSettings is null || !pawn.workSettings.EverWork)
            {
                text.Append("workSettings 未初始化（EverWork=false）→ JobGiver_Work 优先级 0，不会考虑任何工作");
                continue;
            }
            var construction = pawn.workSettings.GetPriority(WorkTypeDefOf.Construction);
            text.Append($"建筑优先级={construction}");

            // 1. the target's own shape, clause by clause in the giver's own order.
            if (thing.Faction != pawn.Faction)
            {
                text.Append($"｜阵营不符（目标={thing.Faction?.Name ?? "无"}，小人={pawn.Faction?.Name ?? "无"}）"
                    + "→ 两个 giver 的第一个 if 就返回 null");
                continue;
            }
            // A blueprint takes the deliver giver; a frame whose material is complete takes the finish giver. Both
            // matter here: the player's colony has shown the loop at the blueprint stage too, and reading only frames
            // is how a probe ends up reporting "nothing to explain" on the colony that is stuck.
            var isFrame = thing is Frame;
            var deliverName = isFrame ? "ConstructDeliverResourcesToFrames" : "ConstructDeliverResourcesToBlueprints";
            if (!constructible.IsCompleted())
            {
                var deliver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(deliverName);
                text.Append($"｜材料未齐 → 走 {deliverName}。");
                text.Append(WalkDeliver(thing, pawn, deliver));
                continue;
            }
            if (!isFrame)
            {
                var deliver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(deliverName);
                text.Append($"｜材料已齐的蓝图 → 仍走 {deliverName} 才会换成建造架。");
                text.Append(WalkDeliver(thing, pawn, deliver));
                continue;
            }

            // 2. the finish giver, clause by clause, in its own order.
            JobFailReason.Clear();
            if (!GenConstruct.CanTouchTargetFromValidCell(thing, pawn))
            {
                text.Append("｜CanTouchTargetFromValidCell=false");
                AppendReason(text);
                continue;
            }
            var blocking = GenConstruct.FirstBlockingThing(thing, pawn);
            if (blocking is not null)
            {
                text.Append($"｜FirstBlockingThing={blocking.def.defName}（会转去搬走它，而不是修）");
                continue;
            }
            var canConstruct = GenConstruct.CanConstruct(thing, pawn, checkSkills: true);
            text.Append($"，CanConstruct={canConstruct}");
            AppendReason(text);
            if (!canConstruct) continue;
            var reachable = pawn.CanReach(thing, PathEndMode.Touch, pawn.NormalMaxDanger());
            var reservable = pawn.CanReserveAndReach(thing, PathEndMode.Touch, pawn.NormalMaxDanger(), 1, -1, null, false);
            text.Append($"，CanReach={reachable}，CanReserveAndReach={reservable}");
            AppendReason(text);
            // The giver's own answer, which is the only thing that matters at the end of the chain.
            var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail("ConstructFinishFrames")?.Worker as WorkGiver_Scanner;
            var job = giver?.JobOnThing(pawn, thing);
            text.Append($"，giver.JobOnThing={(job is null ? "null" : job.def.defName)}");
        }
        return text.ToString();
    }

    /// <summary>The deliver giver's own clauses: it needs the target reachable *and* the material available somewhere.</summary>
    private static string WalkDeliver(Thing thing, Pawn pawn, WorkGiverDef giverDef)
    {
        var text = new StringBuilder();
        if (giverDef is null) return "（找不到对应的 WorkGiverDef）";
        JobFailReason.Clear();
        var canConstruct = GenConstruct.CanConstruct(thing, pawn, WorkTypeDefOf.Construction, false, JobDefOf.HaulToContainer);
        text.Append($" CanConstruct(workType)={canConstruct}");
        AppendReason(text);
        if (!canConstruct) return text.ToString();
        var canGet = GenConstruct.CanGetResources_NewTemp(thing, pawn);
        text.Append($"，CanGetResources={canGet}");
        AppendReason(text);
        if (!canGet) return text.ToString();
        var job = (giverDef.Worker as WorkGiver_Scanner)?.JobOnThing(pawn, thing);
        text.Append($"，giver.JobOnThing={(job is null ? "null" : job.def.defName)}");
        return text.ToString();
    }

    private static void AppendReason(StringBuilder text)
    {
        if (JobFailReason.HaveReason) text.Append("（理由：").Append(JobFailReason.Reason).Append('）');
    }

    /// <summary>Total material the frame still wants, walls included. Read from the frame itself so a cost change in
    /// a def or a mod is reflected instead of being hardcoded to 3 wood.</summary>
    private static int TotalNeeded(Frame frame)
    {
        try
        {
            var total = 0;
            foreach (var cost in frame.TotalMaterialCost()) total += cost.count;
            return total;
        }
        catch (Exception) { return -1; }
    }
}
