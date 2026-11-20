using System.Globalization;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Puts a colonist on a bill job on the authority so the frame path that carries `Job.bill`, `Job.count` and
/// `Job.targetQueueB` is exercised, off unless <c>TURBOTICK_PROBE_BILL</c> names a start tick.
///
/// <para>Why this exists: the counters that prove the sync worked (`配方接线 N 次`, `队列目标 N 个`) only move when
/// a host pawn is actually doing a bill. The lab quicktest colony has three colonists and no workbench, so
/// `bill-add` skipped with "no workbench with a bill stack on this map" and every reading was legitimately 0 - a
/// zero that proves nothing either way.</para>
///
/// <para>Two dead ends are recorded here because both looked like "the sync is broken" from the readings:
/// <list type="number">
/// <item>Picking a cell by radius from the map centre gave `DoBill 0 人` for six minutes: a standable cell inside a
/// sealed pocket is still a cell nobody can walk to, and the work giver answers "no reachable giver" with silence.
/// Placement is now anchored to a colonist and checked with `reachability.CanReach` on the interaction cell.</item>
/// <item>Leaving the job to `WorkGiver_DoBill` produced a bench that stayed present with six chunks beside it and a
/// bill that vanished and was re-added thousands of times (`重挂 7860 次` in 9800 ticks), so no job was ever issued
/// and the counters stayed 0 for a reason that has nothing to do with the sync. The probe now asks the giver for the
/// job directly - `WorkGiver_DoBill.TryStartNewDoBillJob` is the same function the giver calls, so the queue is still
/// filled by vanilla code and the thing under test (carrying that queue across the wire) is unchanged.</item>
/// </list></para>
/// </summary>
internal static class BillProbe
{
    private static readonly int StartTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_BILL"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var start) ? start : -1;

    private static bool built;
    private static int reported;
    private static int issued;
    private static int failures;
    private static Building_WorkTable? bench;
    private static readonly List<Thing> chunks = new();

    internal static void Tick()
    {
        if (StartTick < 0) return;
        if (!HeadlessRuntime.ServerMode) return;
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        var now = manager.TicksGame;
        if (now < StartTick) return;
        if (!built)
        {
            built = true;
            try { Build(); }
            catch (Exception error)
            {
                Log.Warning("[TurboTick probe] 配方探针搭建失败：" + error.GetType().Name + " " + error.Message);
                return;
            }
        }
        Issue(now);
        // One line every 600 ticks: the point is whether a bill job crosses the wire, not a per-tick trace.
        if (now % 600 == 0 && reported != now)
        {
            reported = now;
            Report(now);
        }
    }

    private static void Build()
    {
        var map = Find.CurrentMap;
        if (map is null) { Log.Warning("[TurboTick probe] 配方探针：没有当前地图"); return; }
        var benchDef = DefDatabase<ThingDef>.GetNamedSilentFail("TableStonecutter");
        var chunkDef = DefDatabase<ThingDef>.GetNamedSilentFail("ChunkGranite");
        if (benchDef is null || chunkDef is null)
        {
            Log.Warning("[TurboTick probe] 配方探针：缺 TableStonecutter / ChunkGranite");
            return;
        }
        var anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        if (anchor is null) { Log.Warning("[TurboTick probe] 配方探针：没有殖民者"); return; }

        var stuff = benchDef.MadeFromStuff ? GenStuff.DefaultStuffFor(benchDef) : null;
        if (benchDef.MadeFromStuff && stuff is null)
        {
            Log.Warning("[TurboTick probe] 配方探针：工作台需要材料但没有默认材料");
            return;
        }
        var traversable = TraverseParms.For(anchor);
        IntVec3 cell = IntVec3.Invalid;
        foreach (var candidate in GenRadial.RadialCellsAround(anchor.Position, 9f, true))
        {
            if (!candidate.InBounds(map)) continue;
            // Not the cell the anchor is standing in: the first run spawned the bench exactly under the pawn
            // (`TableStonecutter 在 (113, 0, 121)，锚点 Hicklin 在 (113, 0, 121)`), and a workbench whose interaction
            // cell is occupied is a giver nobody can reserve.
            if (candidate.DistanceTo(anchor.Position) < 2.5f) continue;
            if (!candidate.Walkable(map) || candidate.GetFirstItem(map) is not null) continue;
            if (candidate.GetEdifice(map) is not null) continue;
            if (!GenConstruct.CanPlaceBlueprintAt(benchDef, candidate, Rot4.North, map, false, null, null, stuff).Accepted) continue;
            var workCell = candidate + benchDef.interactionCellOffset.RotatedBy(Rot4.North);
            if (!workCell.InBounds(map) || !workCell.Walkable(map) || workCell.GetEdifice(map) is not null) continue;
            if (workCell.GetFirstItem(map) is not null) continue;
            if (!map.reachability.CanReach(anchor.Position, workCell, PathEndMode.OnCell, traversable)) continue;
            cell = candidate;
            break;
        }
        if (!cell.IsValid) { Log.Warning("[TurboTick probe] 配方探针：殖民者附近放不下一张够得着的工作台"); return; }

        var thing = ThingMaker.MakeThing(benchDef, stuff);
        GenSpawn.Spawn(thing, cell, map, Rot4.North);
        // No faction means the building is invisible to `allBuildingsColonist`, so the work giver never sees it -
        // the same trap the building probe documented.
        thing.SetFaction(Faction.OfPlayer);
        if (thing is not Building_WorkTable table)
        {
            Log.Warning("[TurboTick probe] 配方探针：工作台不是 Building_WorkTable（" + thing.GetType().Name + "）");
            return;
        }
        bench = table;

        // Chunks next to the table. Without ingredients `TryFindBestBillIngredients` fails and the giver returns
        // nothing, so the counters would read zero for a reason unrelated to the sync path.
        foreach (var candidate in GenRadial.RadialCellsAround(cell, 5f, true))
        {
            if (chunks.Count >= 6) break;
            if (!candidate.InBounds(map) || !candidate.Walkable(map)) continue;
            if (candidate.GetEdifice(map) is not null || candidate.GetFirstItem(map) is not null) continue;
            if (!map.reachability.CanReach(anchor.Position, candidate, PathEndMode.Touch, traversable)) continue;
            var chunk = ThingMaker.MakeThing(chunkDef);
            chunk.stackCount = 1;
            GenSpawn.Spawn(chunk, candidate, map);
            chunk.SetForbidden(false, false);
            chunks.Add(chunk);
        }

        // Crafting has to be enabled for somebody; a fresh quicktest colony has it at priority 3 already, but the
        // real save may have it switched off for everyone.
        var enabled = 0;
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
        {
            if (pawn.workSettings is null) continue;
            pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
            if (!pawn.workSettings.WorkIsActive(WorkTypeDefOf.Crafting))
            {
                pawn.workSettings.SetPriority(WorkTypeDefOf.Crafting, 2);
                enabled++;
            }
        }
        Log.Message($"[TurboTick probe] 配方探针已搭建：{benchDef.defName} 在 {cell}（锚点 {anchor.LabelShort} 在 {anchor.Position}），"
            + $"石料 {chunks.Count} 堆，开 Crafting 的殖民者 {enabled} 人。");
    }

    /// <summary>Asks the giver for a real bill job and hands it to the pawn.
    ///
    /// <para>`TryStartNewDoBillJob` is the same function `JobOnThing` calls, so `targetQueueB` / `countQueue` are
    /// still built by vanilla (`job.targetQueueB.Add(chosenIngThings[i].Thing)`), which is the data the frame has to
    /// carry. The scan itself is what fails to pick the bench up, and that is a question about reachability and bill
    /// bookkeeping, not about the wire.</para></summary>
    private static void Issue(int now)
    {
        if (bench is null) { Why("没有工作台对象"); return; }
        if (bench.Destroyed) { Why("工作台已销毁"); return; }
        if (!bench.Spawned) { Why("工作台已离图"); return; }
        if (bench.Map is null) { Why("工作台没有地图"); return; }
        var map = bench.Map;
        // Once per *tick*, not once per frame: `Tick()` runs from the frame pump, so `now - lastIssued < 120` still
        // let three issues through at tick 402/405 (`已下发 145 次` inside three ticks). The workspace clock is the
        // only thing that distinguishes frames from ticks here.
        if (now == lastIssuedTick) return;
        // Only re-issue when nobody is already on a bill: one pawn is enough to exercise the path, and a second job
        // would compete for the same six chunks (the reservations are real on the authority).
        foreach (var other in map.mapPawns.FreeColonistsSpawned)
            if (other.CurJobDef == JobDefOf.DoBill) { lastIssuedTick = now; return; }
        if (now - lastIssued < 120) return;

        var candidates = map.mapPawns.FreeColonistsSpawned
            .Where(p => p.workSettings is not null && !p.Downed && !p.Dead)
            .OrderBy(p => p.Position.DistanceToSquared(bench.Position))
            .ToList();
        if (candidates.Count == 0) { Why("没有可用的殖民者"); return; }
        var pawn2 = candidates[0];
        var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("Make_StoneBlocksAny");
        if (recipe is null) { Why("找不到 Make_StoneBlocksAny"); return; }
        if (bench.BillStack is not { } stack) { Why("工作台没有 BillStack"); return; }
        // Find *our* recipe and put it back if it is gone. The lab's own `bill-add`/`bill-delete` steps run on the
        // replica and reach this bench through the command channel, so they legitimately delete or replace the bill:
        // measured `BillStack 里没有这条配方（1 条：Make_Wort）` - the stack was not empty, it held somebody else's
        // recipe. Guarding on `Count == 0` therefore left the probe permanently without a bill while every reading
        // still looked "configured".
        var bill = stack.Bills.FirstOrDefault(b => !b.deleted && b.recipe == recipe);
        if (bill is null)
        {
            bill = recipe.MakeNewBill();
            if (bill is Bill_Production production) production.repeatMode = BillRepeatModeDefOf.Forever;
            AddBillLocally(stack, bill);
        }
        var available = new List<ThingCount>();
        foreach (var chunk in chunks)
        {
            if (chunk.Destroyed || !chunk.Spawned) continue;
            available.Add(new ThingCount(chunk, chunk.stackCount));
            if (available.Count >= 6) break;
        }
        // 补料。一批 20 块石头要吃掉 1 堆石料，6 堆只够跑三批：实测 `DoBill 1 人` 出现在 tick 600，
        // 到 2400 tick 就是 `DoBill 0 人` + `已下发 42 次`——job 被下发又被完成，副本能看到的窗口只有
        // 400–640 这 240 tick，正好落在两次 600 tick 日志之间。补料让这条 job 一直有人在做，窗口才连续。
        if (available.Count < 3) { Replenish(map); available.Clear(); foreach (var chunk in chunks) { if (chunk.Destroyed || !chunk.Spawned) continue; available.Add(new ThingCount(chunk, chunk.stackCount)); if (available.Count >= 6) break; } }
        if (available.Count == 0)
        {
            if (failures++ < 5)
                Log.Warning($"[TurboTick probe] 配方探针：没有可用材料（登记 {chunks.Count} 堆，"
                    + $"销毁 {chunks.Count(c => c.Destroyed)} 堆，离图 {chunks.Count(c => !c.Spawned)} 堆）");
            return;
        }
        try
        {
            var job = WorkGiver_DoBill.TryStartNewDoBillJob(pawn2, bill, bench, available, out var haulOff);
            // `TryStartNewDoBillJob` returns a haul-off job when something is sitting on the bench, and that job is
            // `HaulToCell` with no queue and no bill - handing it over exercises nothing this probe is about, and
            // measured `已下发 1975 次` of `HaulToCell ... bill=无` while `DoBill 0 人` stayed flat. It also kept
            // interrupting the pawn every 120 ticks, so the bench never got cleared either.
            if (job is null && haulOff is not null)
            {
                Why("工作台上有东西要先搬走（TryStartNewDoBillJob 返回了 haul-off）");
                lastIssued = now;
                return;
            }
            var chosen = job;
            if (chosen is null)
            {
                if (failures++ < 5) Log.Warning("[TurboTick probe] 配方探针：TryStartNewDoBillJob 没有给出 job");
                lastIssued = now;
                return;
            }
            if (chosen.def != JobDefOf.DoBill)
            {
                Why("TryStartNewDoBillJob 给的不是 DoBill：" + chosen.def.defName);
                lastIssued = now;
                return;
            }
            if (!pawn2.jobs.TryTakeOrderedJob(chosen, JobTag.Misc, requestQueueing: false))
            {
                if (failures++ < 5) Log.Warning("[TurboTick probe] 配方探针：TryTakeOrderedJob 拒绝 " + chosen.def.defName);
                lastIssued = now;
                return;
            }
            // 成功之后必须记账，否则每帧都会重新下发一次：`now - lastIssued < 120` 永远成立不了。
            lastIssued = now;
            issued++;
            if (issued <= 3)
                Log.Message($"[TurboTick probe] 配方探针 tick={now} 下发 {chosen.def.defName} 给 {pawn2.LabelShort}："
                    + $"targetA={chosen.targetA.Thing?.def.defName ?? "-"}，队列 B {chosen.GetTargetQueue(TargetIndex.B)?.Count ?? 0} 个，"
                    + $"countQueue {chosen.countQueue?.Count ?? 0} 个，bill={(chosen.bill is null ? "无" : chosen.bill.GetUniqueLoadID())}");
        }
        catch (Exception error)
        {
            lastIssued = now;
            if (failures++ < 5)
                Log.Warning("[TurboTick probe] 配方探针下发失败：" + error.GetType().Name + " " + error.Message);
        }
    }

    /// <summary>A silent `return` in `Issue` produced `已下发 0 次/失败 0 次` - two readings that agree on "nothing
    /// happened" while disagreeing about why. Every early exit names itself instead.</summary>
    private static void Why(string reason)
    {
        if (whyCount++ >= 6) return;
        Log.Warning("[TurboTick probe] 配方探针未下发：" + reason);
    }

    private static int whyCount;
    private static int lastIssued = -1000;
    private static int lastIssuedTick = -1;

    /// <summary>补料。一个 `Make_StoneBlocksAny` 批次吃掉 20 块石头，也就是 1 堆；6 堆只够跑三批，实测
    /// `DoBill 1 人` 出现在 tick 600、到 2400 就 `DoBill 0 人` 且 `已下发` 停在 42 次——job 的生命只有 240
    /// tick，正好落在两次 600 tick 采样之间，副本能看见 bill 的窗口短得像一次巧合。补料让这条 job 一直有人做。</summary>
    private static void Replenish(Map map)
    {
        var chunkDef = DefDatabase<ThingDef>.GetNamedSilentFail("ChunkGranite");
        if (chunkDef is null || bench is null) return;
        chunks.RemoveAll(c => c.Destroyed || !c.Spawned);
        var placed = 0;
        foreach (var candidate in GenRadial.RadialCellsAround(bench.Position, 5f, true))
        {
            if (chunks.Count >= 6) break;
            if (!candidate.InBounds(map) || !candidate.Walkable(map)) continue;
            if (candidate.GetEdifice(map) is not null || candidate.GetFirstItem(map) is not null) continue;
            var chunk = ThingMaker.MakeThing(chunkDef);
            chunk.stackCount = 10;
            GenSpawn.Spawn(chunk, candidate, map);
            chunk.SetForbidden(false, false);
            chunks.Add(chunk);
            placed++;
        }
        if (placed > 0 && replenishes++ < 5)
            Log.Message($"[TurboTick probe] 配方探针：补料 {placed} 堆（现有 {chunks.Count} 堆）。");
    }

    private static int replenishes;

    /// <summary>`BillStack.AddBill` is Harmony-prefixed by `BillAddCapturePatch` whenever the authority accepts
    /// input: the prefix publishes a command and returns `false`, so the bill is *not* added locally - the add is
    /// supposed to happen when that command comes back through `ApplyBillAdd`.
    ///
    /// <para>That makes a probe-authored bill impossible through the public method: it publishes a command, and on
    /// the authority the command is applied on the next barrier, where `Deserialize` finds a bill whose
    /// `ruleString`… no, it finds the same recipe and adds a second bill. Either way the reading was
    /// `配方 0 条` with `重挂 3701 次`, because each re-add published another command that never added anything in
    /// this frame. `Applying` is the documented bypass (`SharedActions.Applying`), and a probe is not a player
    /// action - it must not travel the command channel at all.</para></summary>
    private static void AddBillLocally(BillStack stack, Bill bill)
    {
        var previous = SharedActions.Applying;
        SharedActions.Applying = true;
        try { stack.AddBill(bill); }
        finally { SharedActions.Applying = previous; }
    }

    private static void Report(int now)
    {
        var doing = 0;
        var withQueue = 0;
        var withBill = 0;
        var maxQueue = 0;
        var shapes = new List<string>();
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.CurJob is not { } job || job.def != JobDefOf.DoBill) continue;
                doing++;
                var queue = job.GetTargetQueue(TargetIndex.B);
                if (queue is { Count: > 0 })
                {
                    withQueue++;
                    if (queue.Count > maxQueue) maxQueue = queue.Count;
                }
                if (job.bill is not null) withBill++;
                var report = pawn.jobs.curDriver?.GetReport();
                if (!string.IsNullOrEmpty(report) && shapes.Count < 3) shapes.Add(report!);
            }
        var benchState = bench is null ? "无"
            : bench.Destroyed ? "已销毁"
            : !bench.Spawned ? "已离图"
            : bench.Map is null ? "无地图"
            : "在场";
        var billState = bench?.BillStack is { } stack
            ? $"{stack.Count} 条/可做 {(stack.AnyShouldDoNow ? "是" : "否")}"
            : "-";
        Log.Message($"[TurboTick probe] 配方探针 tick={now}：DoBill {doing} 人（带队列 {withQueue}，最长队列 {maxQueue}，"
            + $"有 bill 对象 {withBill}）；工作台 {benchState}，配方 {billState}；已下发 {issued} 次/失败 {failures} 次；"
            + $"报告样本 {string.Join(" / ", shapes)}。");
    }
}
