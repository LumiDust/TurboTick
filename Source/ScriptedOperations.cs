using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Runs one of every player operation the shared-colony command set covers, at scripted ticks, so a dual-end run can
/// answer "does any operation produce a divergence" instead of "does the one operation we happened to script".
///
/// Why this exists: the rig could only trigger a stockpile drag and a mining designation. Every other command kind -
/// draft, order, forbid, work priority, blueprint, cancel, bill add/delete, area assign, research, zone delete - was
/// covered by unit tests that only prove the codec round-trips, not that the operation survives the round trip through
/// the coordinator into the other peer's colony. "Various operations do not diverge" is a claim about the game, so it
/// has to be measured in the game.
///
/// Each step names itself and reports what it actually changed. A step that cannot run (no bench, no research project)
/// is reported as <c>skipped</c> rather than silently passing: "did not fire" must never be indistinguishable from
/// "did not diverge".
///
/// Off unless <c>TURBOTICK_SCRIPT_OPS</c> is set to the first tick. Steps are spaced <c>TURBOTICK_SCRIPT_OPS_GAP</c>
/// ticks apart (default 40) so every command lands on its agreed tick before the next one starts; running them back to
/// back would let one failure hide behind the next.
///
/// Fires outside the tick on purpose: <see cref="SharedActions.PlayerActing"/> is false while a tick is executing, so a
/// trigger fired from inside the tick would be classified as simulation and would not exercise the capture path.
/// </summary>
internal static class ScriptedOperations
{
    private static readonly int StartTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_OPS"), out var start) ? start : -1;

    private static readonly int Gap = Math.Max(10, ReadInt("TURBOTICK_SCRIPT_OPS_GAP", 40));

    private static readonly int Limit = ReadInt("TURBOTICK_SCRIPT_OPS_LIMIT", int.MaxValue);

    /// <summary>Comma-separated step names to leave out, for isolating which operation a divergence comes from.
    /// Without it "the run diverged" cannot name the operation responsible.</summary>
    private static readonly HashSet<string> Skip = new(
        (Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_OPS_SKIP") ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    private static readonly List<Step> Steps = Build();

    private static int next;

    /// <summary>Why the last step declined. A skipped step must never read the same as a passing one.</summary>
    private static string skipReason = "";

    private static bool done;

    /// <summary>One scripted operation. <c>Run</c> returns false when the map cannot support it.</summary>
    private sealed record Step(string Name, Func<Map, bool> Run);

    internal static bool Pending => StartTick >= 0 && !done;

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static List<Step> Build()
    {
        var all = BuildAll();
        return Skip.Count == 0 ? all : all.Where(step => !Skip.Contains(step.Name)).ToList();
    }

    private static List<Step> BuildAll() =>
    [
        new("designation", map =>
        {
            var def = DefDatabase<DesignationDef>.GetNamedSilentFail("Mine");
            if (def is null) return false;
            map.designationManager.AddDesignation(new Designation(TestCell(map), def));
            return true;
        }),
        // 批量砍伐：单个 Mine 标记不足以让小人进入"多 toil 的长工作"，而验证副本的姿态/驱动力
        // 恰恰需要一个持续在干活的目标。没有这个步骤，测出来的 `忙碌` 永远是 0，读数作废。
        // 直接给殖民者下多 toil 的工作，不依赖「打标记 → AI 自己挑活」那条不可控的链。
        // 验证副本的姿态/驱动力必须先有一个确实在干活的殖民者，否则 toil 对比永远是 0 对 0。
        // 造一个**状态会自己变**的建筑，用来验证 Comp 轮转补扫。
        // 火把带燃料，燃料每 tick 递减 ⇒ XML 必变 ⇒ 补扫必须把它发出去。
        // 没有这个步骤，`物体变化` 恒为 0 只是因为地图上没有玩家建筑，读数作废。
        // 门有开关状态，是第二个「状态会自己变」的建筑类型。逐类验证 Comp 投影时，每种类型都要有自己的场景。
        new("spawn-door", map =>
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
            if (def is null) return false;
            var cell = TestCell(map) + IntVec3.North;
            if (!cell.InBounds(map)) return false;
            GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map);
            Log.Message($"[TurboTick ops] spawn-door at {cell.x},{cell.z}");
            return true;
        }),        new("spawn-torch", map =>
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("TorchLamp")
                      ?? DefDatabase<ThingDef>.GetNamedSilentFail("Torch");
            if (def is null) return false;
            var cell = TestCell(map);
            if (!cell.InBounds(map)) return false;
            var torch = ThingMaker.MakeThing(def);
            GenSpawn.Spawn(torch, cell, map);
            Log.Message($"[TurboTick ops] spawn-torch {def.defName} at {cell.x},{cell.z}");
            return true;
        }),        new("force-work", map =>
        {
            Pawn? colonist = null;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) { colonist = pawn; break; }
            if (colonist is null) return false;
            // 找一件可搬运的东西，交给最近的库存区；没有库存区就丢在原地（HaulToCell 也认）。
            Thing? item = null;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.def?.EverHaulable != true || !thing.Spawned || thing.IsForbidden(Faction.OfPlayer)) continue;
                if (thing.def.category != ThingCategory.Item) continue;
                item = thing;
                break;
            }
            if (item is null) return false;
            // 落到殖民者旁边一格：不需要库存区存在，HaulToCell 一样会跑完整套 toil。
            colonist.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.HaulToCell, item, colonist.Position), JobTag.Misc);
            Log.Message($"[TurboTick ops] force-work 给 {colonist.LabelShort} 下搬运 {item.def.defName}");
            return true;
        }),        new("harvest-batch", map =>
        {
            var def = DefDatabase<DesignationDef>.GetNamedSilentFail("HarvestPlant");
            if (def is null) return false;
            var planted = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not Plant plant || plant.Destroyed || !plant.Spawned) continue;
                if (map.designationManager.DesignationOn(plant, def) is not null) continue;
                map.designationManager.AddDesignation(new Designation(plant, def));
                if (++planted >= 40) break;
            }
            Log.Message($"[TurboTick ops] harvest-batch 标记 {planted} 株");
            return planted > 0;
        }),        new("remove-designation", map =>
        {
            var def = DefDatabase<DesignationDef>.GetNamedSilentFail("Mine");
            foreach (var designation in map.designationManager.AllDesignations)
            {
                if (designation.def != def) continue;
                map.designationManager.RemoveDesignation(designation);
                return true;
            }
            return false;
        }),
        new("draft", map =>
        {
            var pawn = FreeColonist(map);
            if (pawn?.drafter is null) return false;
            pawn.drafter.Drafted = !pawn.drafter.Drafted;
            return true;
        }),
        new("order", map =>
        {
            var pawn = FreeColonist(map);
            var cell = TestCell(map);
            if (pawn is null || !cell.InBounds(map)) return false;
            // 搬运而不是 Goto：Goto 只有一次 toil、几 tick 就结束，采样根本抓不到；
            // 验证副本的姿态/驱动力必须先有一个持续在干活的殖民者。
            Thing? cargo = null;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing?.def?.category != ThingCategory.Item || !thing.Spawned || thing.Destroyed) continue;
                if (thing.IsForbidden(Faction.OfPlayer)) continue;
                cargo = thing;
                break;
            }
            var job = cargo is not null
                ? JobMaker.MakeJob(JobDefOf.HaulToCell, cargo, pawn.Position)
                : JobMaker.MakeJob(JobDefOf.Goto, cell);
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Log.Message($"[TurboTick ops] order -> {(cargo is null ? "Goto" : "HaulToCell " + cargo.def.defName)}");
            return true;
        }),
        new("forbid", map =>
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing?.def?.stackLimit is null or < 2 || !thing.Spawned) continue;
                thing.SetForbidden(!thing.IsForbidden(Faction.OfPlayer), false);
                return true;
            }
            return false;
        }),
        new("priority", map =>
        {
            var pawn = FreeColonist(map);
            var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Doctor")
                ?? DefDatabase<WorkTypeDef>.AllDefsListForReading.FirstOrDefault();
            if (pawn?.workSettings is null || work is null) return false;
            if (!pawn.workSettings.EverWork) return false;
            pawn.workSettings.SetPriority(work, 1);
            return true;
        }),
        new("blueprint", map =>
        {
            var wall = DefDatabase<ThingDef>.GetNamedSilentFail("Wall") ?? DefDatabase<ThingDef>.AllDefsListForReading
                .FirstOrDefault(d => d is { category: ThingCategory.Building, building: not null });
            if (wall is null) return false;
            // A stuffable building needs a stuff: passing null made every placement fail, which read as "the map
            // cannot build" rather than "the call was wrong".
            var stuff = wall.MadeFromStuff ? GenStuff.DefaultStuffFor(wall) : null;
            if (wall.MadeFromStuff && stuff is null) return false;
            // Place a RUN of cells, not one: the reported defect was "dragging a wall only places the first cell", and
            // a single placement cannot see that. A drag is a sequence of placements that must all land locally.
            var placed = 0;
            foreach (var cell in GenRadial.RadialCellsAround(map.Center, 12f, true))
            {
                if (!cell.InBounds(map)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(wall, cell, Rot4.North, map, false, null, null, stuff).Accepted) continue;
                GenConstruct.PlaceBlueprintForBuild(wall, cell, map, Rot4.North, Faction.OfPlayer, stuff);
                placed++;
                var next = cell + IntVec3.East;
                if (!next.InBounds(map)) break;
                if (GenConstruct.CanPlaceBlueprintAt(wall, next, Rot4.North, map, false, null, null, stuff).Accepted)
                    GenConstruct.PlaceBlueprintForBuild(wall, next, map, Rot4.North, Faction.OfPlayer, stuff);
                if (next.InBounds(map) && GenConstruct.CanPlaceBlueprintAt(wall, next + IntVec3.East, Rot4.North, map, false, null, null, stuff).Accepted)
                    GenConstruct.PlaceBlueprintForBuild(wall, next + IntVec3.East, map, Rot4.North, Faction.OfPlayer, stuff);
                break;
            }
            // Report how many blueprints the map actually holds: a run that published but did not place is the defect.
            var present = map.listerThings.AllThings.OfType<Blueprint>()
                .Count(b => b.def.entityDefToBuild == wall);
            // `blueprintsOnMap` is 0 on the origin under cancel-first: the capture cancels the local placement and the
            // blueprint arrives from the coordinator. Reporting that as a failure would hide the real signal, so the
            // step passes when a placement was *published*, and the count is printed for the dump comparison.
            Log.Message($"[TurboTick ops] blueprint published={placed} blueprintsOnMap={present}");
            return placed > 0;
        }),
        new("cancel-thing", map =>
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not Blueprint blueprint) continue;
                blueprint.Destroy(DestroyMode.Cancel);
                return true;
            }
            return false;
        }),
        new("zone-cell-add", map =>
        {
            var zone = ScriptedOperationsZone.Ensure(map);
            if (zone is null) return false;
            // `Zone.AddCell` silently does nothing on a cell that is not standable or already belongs to a zone, and a
            // zone that stays empty is deleted by vanilla - which made the next step report "skipped" for a path that
            // works. Search for a cell the zone will actually accept.
            foreach (var cell in GenRadial.RadialCellsAround(map.Center, 10f, true))
            {
                if (!cell.InBounds(map) || zone.ContainsCell(cell)) continue;
                if (!cell.Standable(map)) continue;
                var before = zone.CellCount;
                zone.AddCell(cell);
                if (zone.CellCount > before) return true;
            }
            return false;
        }),
        new("zone-cell-remove", map =>
        {
            var zone = ScriptedOperationsZone.Ensure(map);
            if (zone is null) return false;
            if (zone.CellCount == 0)
            {
                foreach (var cell in GenRadial.RadialCellsAround(map.Center, 10f, true))
                {
                    if (cell.InBounds(map) && !zone.ContainsCell(cell)) { zone.AddCell(cell); break; }
                }
            }
            if (zone.CellCount == 0) return false;
            IntVec3 victim = default;
            foreach (var cell in zone.Cells) { victim = cell; break; }
            zone.RemoveCell(victim);
            return true;
        }),
        new("area-assign", map =>
        {
            var pawn = FreeColonist(map);
            if (pawn?.playerSettings is null) return false;
            var area = map.areaManager.AllAreas.FirstOrDefault(a => a.AssignableAsAllowed());
            if (area is null) return false;
            pawn.playerSettings.AreaRestrictionInPawnCurrentMap =
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap == area ? null : area;
            return true;
        }),
        new("research", map =>
        {
            var manager = Find.ResearchManager;
            if (manager is null) return false;
            var project = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .FirstOrDefault(p => p is { CanStartNow: true, IsFinished: false });
            if (project is null) return false;
            manager.SetCurrentProject(project);
            return true;
        }),
        new("bill-add", map =>
        {
            var bench = map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()
                .FirstOrDefault(b => b is IBillGiver { BillStack: not null });
            var recipe = DefDatabase<RecipeDef>.AllDefsListForReading
                .FirstOrDefault(r => r?.ProducedThingDef is not null && r.workSkill is not null && r.ingredients is { Count: > 0 });
            if (bench is null) { skipReason = "no workbench with a bill stack on this map"; return false; }
            if (recipe is null) { skipReason = "no recipe with a produced thing, a skill and ingredients"; return false; }
            if (!recipe.AvailableNow) { skipReason = "recipe " + recipe.defName + " is not available now"; return false; }
            var bill = recipe.MakeNewBill();
            bench.BillStack.AddBill(bill);
            return true;
        }),
        new("bill-delete", map =>
        {
            var bench = map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()
                .FirstOrDefault(b => b?.BillStack is { Count: > 0 });
            if (bench is null) return false;
            bench.BillStack.Delete(bench.BillStack.Bills[0]);
            return true;
        }),
        new("zone-delete", map =>
        {
            // Create one if the shared zone is already gone: an empty zone is deleted by vanilla, so relying on the
            // add/remove steps above would report "skipped" for a path that works.
            var zone = ScriptedOperationsZone.Ensure(map);
            if (zone is null) return false;
            zone.Delete();
            return true;
        })
    ];

    /// <summary>Called once per frame from the game component, outside any tick.</summary>
    internal static void Tick(Map map)
    {
        if (!Pending) return;
        if (!SharedColony.HasSharedWorld) return;
        if (Find.TickManager is null) return;
        var now = Find.TickManager.TicksGame;
        if (now < StartTick) return;
        if (next >= Steps.Count || next >= Limit) { Finish(); return; }
        // Space the steps out: a command applies at its stamped tick, so starting the next step immediately would
        // overlap two in-flight commands and make a failure ambiguous.
        if (now < StartTick + (next * Gap)) return;
        var step = Steps[next];
        next++;
        skipReason = "";
        var outcome = "failed";
        try { outcome = step.Run(map) ? "ran" : "skipped(" + (skipReason.Length == 0 ? "precondition" : skipReason) + ")"; }
        catch (Exception error) { outcome = "threw:" + error.GetType().Name; }
        Log.Message($"[TurboTick ops] {next}/{Steps.Count} {step.Name} {outcome} tick={now}");
        if (next >= Steps.Count) Finish();
    }

    private static void Finish()
    {
        done = true;
        Log.Message($"[TurboTick ops] 完成：共 {next} 步，间隔 {Gap} tick");
    }

    /// <summary>A cell near the colony centre that is inside the map.</summary>
    private static IntVec3 TestCell(Map map) => map.Center;

    private static Pawn? FreeColonist(Map map) =>
        map.mapPawns?.FreeColonistsSpawned?.FirstOrDefault(p => p?.jobs is not null && !p.Downed);
}

/// <summary>Holds the zone the scripted operations share, so add/remove/delete act on one object.</summary>
internal static class ScriptedOperationsZone
{
    private static Zone? zone;

    internal static Zone? Existing(Map map) => zone is { } z && map.zoneManager.AllZones.Contains(z) ? z : null;

    internal static Zone? Ensure(Map map)
    {
        if (Existing(map) is { } existing) return existing;
        try
        {
            var created = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(created);
            created.label = "ops";
            zone = created;
            return created;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick ops] 建立区域失败：" + error);
            return null;
        }
    }
}













