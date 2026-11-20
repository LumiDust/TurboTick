using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Installs each patch independently. A RimWorld build that moved one method should cost exactly one
/// feature, not silently disable every later patch in the same assembly.
/// </summary>
public static class PatchInstaller
{
    private static readonly Dictionary<string, bool> status = new(StringComparer.Ordinal);

    public static bool AllCritical { get; private set; }

    public static IReadOnlyDictionary<string, bool> Status => status;

    public static void InstallAll(Harmony harmony)
    {
        status.Clear();
        try { SimulationRandom.Validate(); status["simulation-rng"] = true; }
        catch (Exception error) { status["simulation-rng"] = false; Log.Warning("[TurboTick] RNG adapter: " + error.Message); }
        try { TurboEnvironmentState.ValidateBindings(); status["environment-state"] = true; }
        catch (Exception error) { status["environment-state"] = false; Log.Warning("[TurboTick] Environment adapter: " + error.Message); }
        try { TurboPawnPathState.ValidateBindings(); status["pawn-path-state"] = true; }
        catch (Exception error) { status["pawn-path-state"] = false; Log.Warning("[TurboTick] Pawn path adapter: " + error.Message); }
        // A wrong constant in the deterministic maths is not a "small numeric difference": `Log` was off by 52*ln2, so
        // `Pow(0.88, 1.7/95)` returned 0.274 instead of 0.9977, the construction failure threshold became 0.73 instead
        // of 0.002, and every wall was destroyed a few ticks after its first plank went in. Nothing else in the log
        // said so - the deposit probe proved the materials arrived and the outcome probe counted the failures without
        // naming a cause. Checked against definitional identities, because the cross-machine fingerprint agrees on a
        // wrong answer.
        try
        {
            var mathProblem = DeterministicMath.SelfCheck();
            status["deterministic-math-check"] = mathProblem is null;
            if (mathProblem is not null)
                Log.Error("[TurboTick] 确定性数学自检失败：" + mathProblem
                    + " —— 所有 Pow/Log/Sin/Cos 结果都会偏，建筑、命中、生长等一切概率判定都会失真。");
        }
        catch (Exception error) { status["deterministic-math-check"] = false; Log.Warning("[TurboTick] 数学自检异常：" + error.Message); }
        // The designator as a whole: the client must not allocate thing ids, and cancelling here does not touch the
        // drag rectangle (that is drawn before the commit).
        Try(harmony, "designator-multi", typeof(DesignatorMultiCellPatch));
        Try(harmony, "designator-single", typeof(DesignatorSingleCellPatch));
        Try(harmony, "designation", typeof(DesignationCapturePatch));
        Try(harmony, "designation-remove", typeof(DesignationRemoveCapturePatch));
        Try(harmony, "blueprint", typeof(BlueprintCapturePatch));
        Try(harmony, "blueprint-cancel", typeof(BlueprintCancelCapturePatch));
        Try(harmony, "frame-cancel", typeof(FrameCancelCapturePatch));
        Try(harmony, "draft", typeof(DraftCapturePatch));
        Try(harmony, "order", typeof(OrderCapturePatch));
        Try(harmony, "goto-stop", typeof(GotoStopCapturePatch));
        Try(harmony, "forbid", typeof(ForbidCapturePatch));
        // The same change made through the ``allow/forbid`` gizmo: `CompForbiddable.Forbidden` is a settable
        // property and its setter does not call SetForbidden, so the patch above never saw it.
        Try(harmony, "forbid-property", typeof(ForbidPropertyCapturePatch));
        // Three decisions that run from a confirmation dialog's callback instead of a gizmo's `ProcessInput`:
        // banish, recruit, release-to-wild. Missing one leaves that action local to the peer that clicked.
        // Owner assignment: both writes are dialog buttons, and the virtual entry point covers the three subclasses
        // that delegate to `pawn.ownership`.
        Try(harmony, "assignable", typeof(AssignableCapturePatch));
        Try(harmony, "assignable-unassign", typeof(AssignableUnassignCapturePatch));
        // The dryad caste change: a dialog's confirmation callback writes the comp, not a gizmo's ProcessInput.
        Try(harmony, "dryad-caste", typeof(DryadCasteCapturePatch));
        Try(harmony, "banish", typeof(BanishCapturePatch));
        Try(harmony, "recruit", typeof(RecruitCapturePatch));
        Try(harmony, "release-animal", typeof(ReleaseAnimalCapturePatch));
        Try(harmony, "research", typeof(ResearchCapturePatch));
        Try(harmony, "research-stop", typeof(ResearchStopCapturePatch));
        Try(harmony, "priority", typeof(PriorityCapturePatch));
        Try(harmony, "zone-add", typeof(ZoneAddCellCapturePatch));
        Try(harmony, "zone-remove", typeof(ZoneRemoveCellCapturePatch));
        Try(harmony, "zone-delete", typeof(ZoneDeleteCapturePatch));
        // Whether a zone cell add actually grew the cell count: a zone that exists with zero cells is the
        // symptom the player reports as "creating a work area does nothing".
        Try(harmony, "zone-cell-probe", typeof(ZoneCellProbePatch));
        // A shared world needs one cell order per zone: vanilla shuffles it on first read using Rand and
        // does not save that fact, so the order depends on who drew the zone first.
        Try(harmony, "shared-zone-cells", typeof(SharedZoneCellOrderPatch));
        // The platform maths library rounds sin/cos/log/log10/pow differently on Windows and Linux - measured,
        // not assumed (tools/mathprobe). Rand.Gaussian goes through two of them, so a single draw can land on
        // the other side of a threshold and everything downstream drifts.
        Try(harmony, "deterministic-math-sin", typeof(DeterministicSinPatch));
        Try(harmony, "deterministic-math-cos", typeof(DeterministicCosPatch));
        Try(harmony, "deterministic-math-log", typeof(DeterministicLogPatch));
        Try(harmony, "deterministic-math-log10", typeof(DeterministicLog10Patch));
        Try(harmony, "deterministic-math-pow", typeof(DeterministicPowPatch));
        // Which wild-plant spawns actually happened: the counts alone cannot say which guard stopped one peer.
        Try(harmony, "shared-ecology-spawn", typeof(SharedEcologySpawnPatch));
        // When each pawn last ran a main think pass: one extra pass inverts the wander parity for good.
        Try(harmony, "think-pass-audit", typeof(ThinkPassPatch));
        // The herd centre behind a WanderHerd target, and the lord that owns the pawn.
        Try(harmony, "herd-root", typeof(HerdRootPatch));
        // Where the food search succeeded: measured as the point where two peers with identical hunger parted.
        Try(harmony, "food-give-job", typeof(FoodGiveJobPatch));
        Try(harmony, "food-search", typeof(FoodSearchPatch));
        // The storage search behind a haul destination.
        Try(harmony, "haul-search", typeof(HaulSearchPatch));
        // When a forbidden flag flips, and from where.
        Try(harmony, "forbid-trace", typeof(ForbidTracePatch));
        // Deferred spawn requests live in a HashSet and are ticked by iterating it; SpawnRequest has no
        // GetHashCode override, so the order follows object identity and differs between processes.
        Try(harmony, "deferred-spawn-order", typeof(DeferredSpawnOrderPatch));
        // Draw counts at fixed points of one tick: names the stage when the streams differ but no entity does.
        Try(harmony, "tick-stage-boundary", typeof(TickStageBoundaryPatch));
        Try(harmony, "tick-stage-things", typeof(TickStageThingListsPatch));
        Try(harmony, "tick-stage-storyteller", typeof(TickStageStorytellerPatch));
        Try(harmony, "tick-stage-mappost", typeof(TickStageMapPostPatch));
        // Inside MapPostTick: nine marks, one per component.
        try { MapPostStagePatches.Install(harmony); status["mappost-stages"] = true; }
        catch (Exception error) { status["mappost-stages"] = false; Log.Warning("[TurboTick] mappost-stages 未挂载：" + error.Message); }
        Try(harmony, "tick-stage-gamecomponents", typeof(TickStageGameComponentPatch));
        // Which individual thing drew from the shared stream (diagnostic only, off by default).
        Try(harmony, "thing-draw-trace", typeof(ThingDrawTracePatch));
        // Which pawn subsystem drew, when one pawn visible state agrees but its draw count does not.
        Try(harmony, "pawn-draw-jobs", typeof(PawnJobsDrawPatch));
        Try(harmony, "pawn-draw-health", typeof(PawnHealthDrawPatch));
        Try(harmony, "pawn-draw-mind", typeof(PawnMindDrawPatch));
        Try(harmony, "pawn-draw-needs", typeof(PawnNeedsDrawPatch));
        Try(harmony, "pawn-draw-social", typeof(PawnSocialDrawPatch));
        Try(harmony, "pawn-draw-age", typeof(PawnAgeDrawPatch));
        // Inside one social attempt: list size, filter call counts and total draws, measured not re-derived.
        Try(harmony, "social-attempt", typeof(SocialAttemptPatch));
        // Def load order comes from directory enumeration, which differs between NTFS and ext4; a weighted pick
        // over that list then lands on a different def for the same random value.

        Try(harmony, "social-can-interact", typeof(SocialCanInteractPatch));
        Try(harmony, "social-can-receive", typeof(SocialCanReceivePatch));
        // A thing without an id yet must not be bucketed by its object identity: that value differs between
        // processes, which changes which things tick together on each peer.
        Try(harmony, "shared-tick-bucket", typeof(SharedTickBucketPatch));
        Try(harmony, "bill-add", typeof(BillAddCapturePatch));
        Try(harmony, "bill-delete", typeof(BillDeleteCapturePatch));
        Try(harmony, "area-assign", typeof(AreaAssignCapturePatch));
        // The training tab's checkbox: a static widget, so no gizmo patch can see it, and `GizmoStatePatch` only
        // snapshots the selected object anyway.
        Try(harmony, "training-checkbox", typeof(TrainingCheckboxCapturePatch));
        // Surgery bills travel whole, because their body part is assigned after `AddBill` - a prefix on `AddBill`
        // cancels the local call and then the part setter refuses to run (`:442400-442402`).
        Try(harmony, "surgery-bill", typeof(SurgeryBillCapturePatch));
        // Re-targeting a xenogerm implant assigns `bill.xenogerm` *after* `CreateSurgeryBill` returns, so the bill's own
        // fragment carried a null gene set. The letter call is the last statement of that callback, hence the first
        // moment a complete (bill, xenogerm) pair exists.
        Try(harmony, "xenogerm-retarget", typeof(XenogermRetargetCapturePatch));
        // A dialog button that writes the selected building. `GizmoStatePatch` only hooks `Command.ProcessInput`, so
        // this reuses its snapshot pair rather than adding a second, divergent implementation.
        Try(harmony, "gene-assembler-start", typeof(GeneAssemblerStartCapturePatch));
        // The three `Dialog_ManageAreas` buttons. Drawing an area is a designator and already covered; these are not.
        Try(harmony, "area-invert", typeof(AreaInvertCapturePatch));
        Try(harmony, "area-clear", typeof(AreaClearCapturePatch));
        Try(harmony, "area-delete", typeof(AreaDeleteCapturePatch));
        // The four assignment panels (apparel / drugs / food / timetable). They write tracker properties, never a
        // gizmo, so `gizmo-state` below cannot see them.
        Try(harmony, "policy-assign", typeof(PolicyCapturePatch));
        // The bill settings dialog. `Dialog_BillConfig` writes thirteen fields and three methods, so it is sampled as a
        // whole `BillStack` fragment rather than field by field; `Window.PreClose` is the one place both dialogs pass.
        Try(harmony, "bill-dialog", typeof(BillDialogCapturePatch));
        // The storyteller page: its difficulty sliders and threat checkboxes are simulation inputs.
        Try(harmony, "storyteller", typeof(StorytellerCapturePatch));
        // Choice letters: capturing the option list when the letter opens, and reporting the click instead of running
        // the option here. One pair of points covers all eleven `ChoiceLetter` subclasses.
        Try(harmony, "letter-choice-open", typeof(LetterOpenPatch));
        Try(harmony, "letter-choice-activate", typeof(LetterOptionActivatePatch));
        // `ChoiceLetter_GrowthMoment` is NOT a `ChoiceLetter` (it derives from `LetterWithTimeout`), so it needs its own
        // point: `MakeChoices` is where its effect happens and it has no `DiaOption` list to intercept.
        Try(harmony, "letter-growth-choice", typeof(GrowthMomentChoicePatch));
        Try(harmony, "transport", typeof(TransportCapturePatch));
        // A portal is not a transporter: MapPortal.AddToTheToLoadList (:571926) is its own method on its own class
        // with its own leftToLoad (:571789). The patch above is attached to CompTransporter, so it never saw
        // Dialog_EnterPortal.TryAccept (:633225) and loading a gravship gate never reached the colony that simulates.
        Try(harmony, "portal-load", typeof(PortalCapturePatch));
        Try(harmony, "portal-cancel", typeof(PortalCancelCapturePatch));
        // Incidents a player's own action queued. A replica runs no `IncidentQueueTick` (`:384088`), so a requested
        // trader or a call for aid would otherwise exist only in the clicking player's process.
        Try(harmony, "incident-queue", typeof(IncidentQueueCapturePatch));
        Try(harmony, "history-record", typeof(HistoryRecordCapturePatch));
        // The fallback for every gizmo that has no dedicated command above. Without it a click on a filter,
        // a flick switch or a bed setting changes only the clicking player's world.
        Try(harmony, "gizmo-state", typeof(GizmoStatePatch));
        // Settlement automation (round 153): auto-rebuild and auto-roofing have no pawn to gate, so they get their own
        // hooks. Registered here because a `[HarmonyPatch]` class nobody installs is dead code - the same wiring gap
        // round 140 found for `Command_CallBossgroup`.
        Try(harmony, "settlement-auto-rebuild", typeof(SettlementAutoRebuildGate));
        Try(harmony, "settlement-auto-rebuild-terrain", typeof(SettlementAutoRebuildTerrainGate));
        Try(harmony, "settlement-auto-roof", typeof(SettlementAutoRoofGate));
        // Another settlement's goods presented as forbidden, so vanilla AI avoids them instead of being refused one
        // attempt at a time (round 154). CT does the same at ct:352751.
        Try(harmony, "settlement-forbid-visibility", typeof(SettlementForbidVisibilityPatch));
        // The cell half of the work gate: twelve cell work givers, of which `GrowerHarvest` reserves nothing (round 155).
        Try(harmony, "settlement-work-cell-gate", typeof(SettlementWorkCellGate));
        // Whose goods may pay a trade ship (round 156): vanilla enumerates everything in the beacon's cells.
        Try(harmony, "settlement-launchable-gate", typeof(SettlementLaunchableGate));
        // A job whose target we already present as forbidden is not issued at all (round 158); CT's
        // `SettlementJobGiverPatch` at ct:352918 does the same on this think node.
        Try(harmony, "settlement-job-giver-gate", typeof(SettlementJobGiverGate));
        // The right-click chokepoint, separate from the gizmo one: `FloatMenuOption.Chosen` runs the option's own closure,
        // while `Command.ProcessInput` runs a gizmo's. Nothing patched the former, so every right-click order (haul, equip,
        // wear, arrest, rescue, strip, ingest, capture, load a caravan, start a ritual, trade) stayed on the clicking peer.
        Try(harmony, "float-menu", typeof(FloatMenuChosenCapturePatch));
        Try(harmony, "tradeguard", typeof(TradeGuardPatch));
        Try(harmony, "caravanguard", typeof(CaravanGuardPatch));
        // The `historical` flag of `Messages.Message` is a parameter, so the message channel cannot read it back off
        // the object; this capture is what lets the replica archive exactly what the authority archived (round 163).
        Try(harmony, "message-history-capture", typeof(MessageHistoryCapturePatch));
        Try(harmony, "gate", typeof(TickGatePatch));
        Try(harmony, "apply", typeof(TickApplyPatch));
        Try(harmony, "shared-snapshot-ids", typeof(SharedCompressionPatch));
        Try(harmony, "shared-respawn", typeof(SharedRespawnScopePatch));
        Try(harmony, "shared-filth", typeof(SharedSavedFilthPatch));
        Try(harmony, "pawn-path-resume", typeof(PawnPathResumePatch));
        Try(harmony, "world-pawns-sort", typeof(WorldPawnsDeterministicTickPatch));
        Try(harmony, "wander-herd", typeof(HerdWanderRootDeterministicPatch));
        Try(harmony, "wander-dest", typeof(RandomWanderDestDeterministicPatch));
        Try(harmony, "effect-isolation", typeof(TurboEffectIsolation));
        Try(harmony, "shared-tick-cadence", typeof(SharedTickCadence));
        Try(harmony, "shared-sky-cadence", typeof(SharedSkyCadence));
        Try(harmony, "shared-glow-cadence", typeof(SharedGlowCadence));
        Try(harmony, "shared-power-cadence", typeof(SharedPowerCadence));
        Try(harmony, "shared-region-cadence", typeof(SharedRegionCadence));
        Try(harmony, "shared-room-cadence", typeof(SharedRoomCadence));
        Try(harmony, "shared-lord-cadence", typeof(SharedLordCadence));
        // The remaining simulation work that vanilla drives per frame. Leaving any of these out means the
        // two peers run different weather, snow, temperature or allowed areas at the same tick, and then
        // take different AI decisions - which is what surfaced as colonists differing in position and job.
        Try(harmony, "shared-weather-cadence", typeof(SharedWeatherCadence));
        Try(harmony, "shared-area-cadence", typeof(SharedAreaCadence));
        Try(harmony, "shared-mapcomponent-cadence", typeof(SharedMapComponentCadence));
        // The same omission one level up: GameComponents and WorldComponents are per frame in UpdatePlay.
        // They own the storyteller's incident clock, quests, world pawns and caravans.
        Try(harmony, "shared-gamecomponent-cadence", typeof(SharedGameComponentCadence));
        Try(harmony, "shared-worldcomponent-cadence", typeof(SharedWorldComponentCadence));
        Try(harmony, "shared-projectile-cadence", typeof(SharedProjectileCadence));
        Try(harmony, "shared-worldobject-cadence", typeof(SharedWorldObjectCadence));
        Try(harmony, "shared-path-requests", typeof(SharedPathRequests));
        // Display-replica presentation: interpolated motion and a clock that follows the host.
        Try(harmony, "replica-draw-pos", typeof(ReplicaDrawPosPatch));
        // 渲染器读的是 Pawn_DrawTracker.DrawPos，不是 Pawn.DrawPos。只补后者等于补了一个没人问的属性。
        Try(harmony, "replica-draw-tracker-pos", typeof(ReplicaDrawTrackerPosPatch));
        // 健康是面板上直接看到的数，副本不 tick 不会自己变，直接投影权威值。
        Try(harmony, "replica-health", typeof(ReplicaHealthPatch));
        Try(harmony, "replica-plant-guard", typeof(ReplicaPlantPrintGuard));
        // Same shape as the plant guard, for the one render path a broken pawn can take down: a pawn whose def went
        // missing throws from PawnRenderer.EnsureGraphicsInitialized every frame (measured 4263 lines in 3 minutes).
        Try(harmony, "replica-pawn-render-guard", typeof(ReplicaPawnRenderGuard));
        // A blueprint whose def lost `entityDefToBuild` throws out of `BuildCopyCommandUtility.FindAllowedDesignator`
        // (`Dictionary.ContainsKey(null)`), and the throw abandons the whole gizmo grid rather than one button.
        Try(harmony, "replica-find-designator-null", typeof(ReplicaFindDesignatorNullGuard));
        // Names why a replica's right-click menu is empty. "无法下达强制任务" is a menu with no options, and without
        // this line that failure is invisible: nothing throws, nothing is logged, the menu simply has nothing in it.
        Try(harmony, "replica-float-menu-probe", typeof(ReplicaFloatMenuProbe));
        // The home-area stamp. `AutoHomeAreaMaker.MarkHomeAroundThing` writes into `areaManager.Home` based only on
        // `PlaySettings.autoHomeArea`, which is now synchronized - so a replica would grow its own area from frame
        // spawns and `AreaSync` would flap it back off on the next frame.
        Try(harmony, "replica-home-area-suppress", typeof(ReplicaHomeAreaSuppressPatch));
        // The drag box selects by `ColonistBar.DrawLocs`, which is a cache only rebuilt when vanilla's own spawn/death
        // events mark it dirty - events a non-ticking replica never fires, so selection used the load-time positions.
        Try(harmony, "replica-colonist-bar-refresh", typeof(ReplicaColonistBarRefresh));
        // A replica raises no letter of its own (it ticks nothing), so any letter that appears there came from a
        // third-party mod's code running locally. Its sound is a false alert - measured: the replica played "Raiders
        // are attacking" during a checkpoint load for a raid that was already over. Only the sound is suppressed;
        // blocking the letter itself would break that mod's feature silently.
        Try(harmony, "replica-letter-guard", typeof(ReplicaLetterGuard));
        // Terrain is not a Thing: it lives in `TerrainGrid`'s three flat arrays and the frame's thing enumeration can
        // never carry it. Every path that changes ground - mining, flooring, smoothing, a genstep - ends in one of
        // these four methods, so they are the only hooks needed. Without them a replica draws its load-time ground
        // forever: a mined-out cave keeps drawing as rock and a pawn appears to walk through a wall.
        Try(harmony, "replica-terrain-top", typeof(ReplicaTerrainTopPatch));
        Try(harmony, "replica-terrain-under", typeof(ReplicaTerrainUnderPatch));
        Try(harmony, "replica-terrain-foundation", typeof(ReplicaTerrainFoundationPatch));
        Try(harmony, "replica-terrain-removed", typeof(ReplicaTerrainRemovedPatch));
        // Roofs. Not a Thing either: `RoofGrid` is one flat array, and a player-drawn roof area never reaches a replica
        // without this. `SetRoof` is the only public writer that notifies - `RemoveRoofUnsafe` is the grid's own rebuild
        // path and is deliberately left alone.
        Try(harmony, "replica-roof-touched", typeof(ReplicaRoofTouchedPatch));
        // Snow depth. `AddDepth` writes the array directly after its own clamp without calling `SetDepth`, so both have
        // to be hooked - `SetDepth` alone would miss every snowfall.
        Try(harmony, "replica-snow-add", typeof(ReplicaSnowAddPatch));
        Try(harmony, "replica-snow-set", typeof(ReplicaSnowSetPatch));
        // 室内温度：`EqualizeTemperature` 每 120 tick 才动一次，副本不跑 ⇒ 冷库读出室温、供暖卧室永不升温。
        // 钩的是均衡函数而不是 `Room.Temperature` 的 setter——后者正是本端写入用的，钩它会把副本自己的写入回灌成脏格。
        Try(harmony, "replica-room-temp", typeof(RoomTempTrackerEqualizePatch));
        // 世界物件位置：商队/据点/飞船靠自己的间隔 tick 移动，副本不跑 ⇒ 走了一整季的商队仍画在出发地。
        // 钩 `WorldObject.Tile` 的 setter —— 所有子类（商队路径跟随、据点生成、飞船降落）都走这一个属性，
        // 而它的 setter 正是渲染器依赖的地方（标脏新旧格的 fast finder、通知静态绘制器）。
        // `WorldObject.ExposeData` 写的是字段（`Scribe_Values.Look(ref tile, "tile")`）⇒ 载入时不触发，不会每读档重发全世界。
        Try(harmony, "replica-world-object-tile", typeof(WorldObjectTilePatch));
        // Fog of war. `Unfog` is the walking-pawn path, `Refog` is a player re-fogging or a blocker being rebuilt;
        // `UnfogWorker` and `ClearAllFog` are primitives that would resend the whole map.
        Try(harmony, "replica-fog-lift", typeof(ReplicaFogLiftPatch));
        Try(harmony, "replica-fog-restore", typeof(ReplicaFogRestorePatch));
        // A replica's display apparel has no id, so it is not part of the world and cannot be dropped. Without this a
        // change of clothes would try to spawn the old garment on the map and log an error every time.
        Try(harmony, "replica-apparel-drop-guard", typeof(ReplicaApparelDropGuard));
        // The ownership layer's three chokepoints. Each returns "allowed" immediately when no settlement exists, so a
        // save that never declared one behaves exactly as before this file existed.
        Try(harmony, "settlement-reservation-gate", typeof(SettlementReservationGate));
        Try(harmony, "settlement-work-gate", typeof(SettlementWorkGate));
        // The fourth settlement gate: `Pass`/`Door` had a table row and no reader, because doors are asked about by the
        // pathfinder rather than by a reservation, a work giver or a designator.
        Try(harmony, "settlement-door-gate", typeof(SettlementDoorGate));
        // The fifth settlement gate: `Amenities`/`Relax` had two table rows and no producer, and the flag had no
        // checkbox either - unreachable from both ends at once.
        Try(harmony, "settlement-joy-gate", typeof(SettlementJoyGate));
        // The `Traders`/`Trade` row: `TradeGuard` refuses *completing* a trade, which left the window itself open - a
        // visitor who may not trade could still open it, read the caravan's stock and price everything.
        Try(harmony, "settlement-trade-gate", typeof(SettlementTradeGate));
        // Discovered rather than named: `CanDesignateCell` is abstract and every subclass overrides it without calling
        // base, so a patch on the base class is never reached for a build order or a stockpile drag.
        Try(harmony, "settlement-designator-gate", typeof(SettlementDesignatorGate));
        // Territory overlay. `Area_Allowed` is also the type vanilla uses to restrict where a colonist may work, so the
        // overlay has to answer "allowed" for work or it would silently become a work order nobody asked for.
        Try(harmony, "settlement-area-guard", typeof(VanillaAreaIsWorkRestrictionGuard));
        // The grant editor's entry point. A settlement is a rectangle, not a Thing, so the button hangs off the buildings the
        // player already clicks. Postfix that yields, not a prefix: vanilla's own gizmos must come through untouched.
        Try(harmony, "settlement-grant-gizmo", typeof(SettlementGrantGizmo));
        // A shared timeline must not stop when the window loses focus.
        Try(harmony, "background-run", typeof(BackgroundRunPatch));
        Try(harmony, "background-prefs", typeof(BackgroundPrefsPatch));
        // Vanilla autosave's LongEventHandler freezes the shared timeline once per in-game day.
        Try(harmony, "shared-autosave", typeof(SharedAutosaveSkip));
        // A replica has no local path, so vanilla would draw its path line to the origin.
        Try(harmony, "replica-path-line", typeof(ReplicaPathLineSkip));
        Try(harmony, "replica-clock", typeof(ReplicaClockPatch));
        // The calendar is a second clock (TicksAbs), so the sun and the date need the same treatment as the tick.
        Try(harmony, "replica-abs-clock", typeof(ReplicaAbsClockPatch));
        // 副本重建权威 job 时，某些 toil 的初始化依赖本端 AI（预订、可达性），会抛异常；原版的处理是
        // 结束 job 去等待，等于让副本放弃权威端正在做的事。这里保留 job、只把步骤置 -1。
        Try(harmony, "replica-job-guard", typeof(ReplicaJobGuardPatch));
        if (SimulationDiagnostics.Enabled)
        {
            foreach (var type in ConditionalTracePatches) Try(harmony, TraceKey(type), type);
        }
        else
        {
            // Opt-in diagnostics. Recording them here keeps the "declared but never installed" self-check
            // honest: they are deliberately skipped unless tracing is on, not forgotten.
            foreach (var type in ConditionalTracePatches) registered.Add(type);
        }
        Try(harmony, "map-drawer-empty-dispose", typeof(MapDrawerEmptyDisposePatch));
        Try(harmony, "world-load", typeof(WorldLoadScope));
        Try(harmony, "world-load-failure", typeof(SharedLoadFailurePatch));
        Try(harmony, "native-controls", typeof(NativeControlsPatch));
        Try(harmony, "native-speed", typeof(NativeSpeedSetterPatch));
        Try(harmony, "native-pause", typeof(NativePausePatch));
        Try(harmony, "multiplayer-menu-scope", typeof(MultiplayerMenuScope));
        Try(harmony, "multiplayer-menu-option", typeof(MultiplayerMenuOption));
        // A headless world must never be paused by a dialog nobody can dismiss. Measured: a real save sat at
        // serverTick=7 / tps=0 for a whole session because `forcePauseWindows` named Verse.Dialog_MessageBox, and
        // Dialog_MessageBox's constructor sets forcePause = true unconditionally.
        Try(harmony, "headless-window-guard", typeof(HeadlessWindowGuardPatch));
        // Reading the speed and the pause flag from outside the tick manager cannot explain a frozen world: our
        // own repair runs after the decision, so the status file always shows the state after it. This records
        // the decision itself, and whether the tick manager was entered at all.
        Try(harmony, "tick-decision-audit", typeof(TickDecisionAuditPatch));
        // Pause() and TogglePaused write curTimeSpeed directly, so the property patch above never sees them; this
        // names whichever caller pauses the world at frame rate (vanilla LetterStack.ReceiveLetter does it for any
        // letter whose pauseMode is at or below Prefs.AutomaticPauseMode).
        Try(harmony, "pause-audit", typeof(PauseAuditPausePatch));
        Try(harmony, "pause-audit-toggle", typeof(PauseAuditTogglePatch));
        Try(harmony, "multiplayer-entry-update", typeof(MultiplayerEntryUpdate));
        Try(harmony, "multiplayer-return", typeof(MultiplayerReturnToMenu));
        Try(harmony, "multiplayer-shutdown", typeof(MultiplayerShutdown));
        // Not a feature: one line per haul into a construction frame, carrying the value
        // `TryTransferToContainer` actually returned. "Wood is carried onto the wall and the count does not go up"
        // has two possible owners - the deposit, or the replica's picture of it - and vanilla logs nothing for
        // either, so the number has to be read where it is produced.
        Try(harmony, "construction-deposit-probe", typeof(ConstructionDepositProbePatch));
        // Counts how each construction ends. The deposit probe proved the materials arrive (`投递后 容器内 5`), the
        // wall count stayed at 509, and the blueprint's id changed every couple of minutes - so the frame is being
        // destroyed and replaced rather than waiting. `FailConstruction` is the only path that does that and vanilla
        // logs nothing for it, which is why "the materials arrive and nothing is built" had no readable cause.
        Try(harmony, "construction-outcome-probe", typeof(FrameFailedPatch));
        Try(harmony, "construction-complete-probe", typeof(FrameCompletedPatch));
        // Who destroys a blueprint/frame, and in which mode. The two outcome probes above both read zero while the
        // blueprint id changed on every report, so the destruction is not "completed" and not "failed" - it comes from
        // a third path that vanilla never logs.
        Try(harmony, "construction-destroy-probe", typeof(ConstructionDestroyPatch));
        // The value that decides every wall. `Rand.Value` is observed through a postfix on its getter inside a window
        // opened only while a construct-finish driver ticks, so the probe cannot shift the shared stream it measures.
        Try(harmony, "construct-rand-value-probe", typeof(RandValueObservePatch));
        Try(harmony, "construct-rand-window", typeof(ConstructDriverWindowPatch));
        Try(harmony, "construct-rand-window-interval", typeof(ConstructDriverIntervalWindowPatch));
        // The delta the construct toil was ticked with, and the threshold that produces. `Rand.Value` alone cannot
        // explain a failure storm: with success chance 0.85 the threshold is 0.003 at delta=1 and approaches 1 as delta
        // grows, so the same "impossible" run of failures is ordinary if the toil is being ticked with a large delta.
        Try(harmony, "construct-delta-probe", typeof(ConstructToilDeltaPatch));
        // The three entry points of the job pump. A colonist stuck in `Wait` for 30 000 ticks with
        // `ticksLeftThisToil = -30616` means the driver is ticked and the job is never expired; only these counters say
        // which of the two vanilla paths stopped.
        Try(harmony, "job-pump-tick", typeof(JobTrackerTickCountPatch));
        Try(harmony, "job-pump-interval", typeof(JobTrackerIntervalCountPatch));
        Try(harmony, "job-pump-driver", typeof(JobDriverTickCountPatch));
        // Absolute tick stamps poisoned by an earlier clock rewind. Refusing new rewinds is not enough: the stamps are
        // already in the save, and a job whose `startTick` is in the future never expires.
        Try(harmony, "tick-stamp-repair", typeof(TickStampRepairPatch));
        // Without the gate a peer could simulate past the server and diverge; without capture nothing syncs.
        AllCritical = status.GetValueOrDefault("simulation-rng")
            && status.GetValueOrDefault("environment-state")
            && status.GetValueOrDefault("pawn-path-state")
            && status.GetValueOrDefault("designation")
            && status.GetValueOrDefault("blueprint")
            && status.GetValueOrDefault("draft")
            && status.GetValueOrDefault("order")
            && status.GetValueOrDefault("gate")
            && status.GetValueOrDefault("apply")
            && status.GetValueOrDefault("shared-snapshot-ids")
            && status.GetValueOrDefault("shared-respawn")
            && status.GetValueOrDefault("shared-filth")
            && status.GetValueOrDefault("pawn-path-resume")
            && status.GetValueOrDefault("world-pawns-sort")
            && status.GetValueOrDefault("wander-herd")
            && status.GetValueOrDefault("wander-dest")
            && status.GetValueOrDefault("effect-isolation")
            && status.GetValueOrDefault("shared-tick-cadence")
            && status.GetValueOrDefault("shared-sky-cadence")
            && status.GetValueOrDefault("shared-glow-cadence")
            && status.GetValueOrDefault("shared-projectile-cadence")
            && status.GetValueOrDefault("shared-worldobject-cadence")
            && status.GetValueOrDefault("shared-path-requests")
            && status.GetValueOrDefault("world-load")
            && status.GetValueOrDefault("world-load-failure")
            && status.GetValueOrDefault("native-controls")
            && status.GetValueOrDefault("native-speed")
            && status.GetValueOrDefault("native-pause");
        var summary = string.Join(" ", status.Select(pair => pair.Key + (pair.Value ? "✓" : "✗")));
        if (AllCritical) Log.Message("[TurboTick] 补丁已就绪：" + summary);
        else Log.Warning("[TurboTick] 部分补丁未挂载，共享殖民地将被禁用：" + summary);
        // The designator patches are discovered at runtime, so "installed" and "installed over every subclass that
        // matters" are different claims. A patch count of 1 (the base class only) is installed, green in the audit and
        // captures nothing the player does - which is exactly how the wall blueprints went unrecorded for a whole
        // session. Naming the target count turns that into a visible number.
        Log.Message("[TurboTick] 设计器补丁：" + DesignatorSingleCellPatch.Describe());
        // Log the digest of the deterministic maths so two peers' logs can be compared directly: a mismatch
        // here means they replaced a different set of functions and nothing else will line up.
        Log.Message("[TurboTick] 确定性数学指纹：" + DeterministicMath.Fingerprint());
        // What actually loaded. Two peers must print the same line: the rig found one machine with the four
        // expansions on disk and the other with only Core while ModsConfig listed the same mods on both, which
        // makes any comparison of "installed content" meaningless unless it comes from DefDatabase.
        Log.Message("[TurboTick] 内容指纹：" + ContentProbe.Describe());
        ReportUnregisteredPatches();
    }

    private static void Try(Harmony harmony, string name, Type patchClass)
    {
        try
        {
            registered.Add(patchClass);
            var patched = harmony.CreateClassProcessor(patchClass).Patch();
            status[name] = patched.Count > 0;
            if (patched.Count == 0) Log.Warning($"[TurboTick] 补丁 {name} 没有匹配到目标方法");
        }
        catch (Exception error)
        {
            status[name] = false;
            // `AmbiguousMatchException` is the one install failure the compiler cannot catch and whose own message names
            // nothing actionable: `nameof(PatchTarget)` is a plain string, so a name with overloads resolves at runtime
            // and throws here. Measured on 192.168.0.29: `Pawn_ApparelTracker.TryDrop` has three overloads, and this
            // failure was escalated by the "every gameplay patch must be installed" check into a fatal startup error -
            // the server would not run at all. Saying what to do turns a dead server into a one-line edit.
            var hint = error is System.Reflection.AmbiguousMatchException
                ? "（目标方法有重载：改用 [HarmonyPatch] + TargetMethod() 显式给出签名，或给特性传 Type[] 参数表）"
                : string.Empty;
            Log.Warning($"[TurboTick] 补丁 {name} 失败：{error.Message}{hint}");
        }
    }

    /// <summary>Everything this installer has been asked to patch.</summary>
    private static readonly HashSet<Type> registered = [];

    /// <summary>Tracing patches, installed only when <see cref="SimulationDiagnostics.Enabled"/>.</summary>
    private static readonly Type[] ConditionalTracePatches =
    [
        typeof(SharedFilthRemovalTracePatch), typeof(MapStageTracePatch), typeof(TickListStageTracePatch),
        typeof(EnvironmentStageTracePatch), typeof(CameraRateTracePatch), typeof(PawnTickTracePatch),
        typeof(PawnPathTracePatch), typeof(PawnJobTracePatch), typeof(RandValueTracePatch), typeof(RandIntTracePatch),
    ];

    /// <summary>Status key for a trace patch: its type name without the suffix, in kebab-case.</summary>
    private static string TraceKey(Type type)
    {
        var name = type.Name.Replace("TracePatch", "", StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.Append("-trace").ToString();
    }

    /// <summary>Patched from their own entry points rather than from InstallAll, so they are expected to be
    /// absent here: the headless pump, the tick-rate replacement, and the tracing helpers.</summary>
    private static readonly HashSet<string> installedElsewhere = new(StringComparer.Ordinal)
    {
        "HeadlessUpdatePatch", "TickRatePatch", "PawnTickTracePatch", "PawnPathTracePatch",
        "BackgroundRunPatch", "BackgroundPrefsPatch", "CaravanGuardPatch",
        "NativeControlsPatch", "NativeSpeedSetterPatch", "NativePausePatch",
        "ReplicaDrawPosPatch", "ReplicaClockPatch",
    };

    /// <summary>
    /// A patch class that nobody installs is silent: the feature it carries simply does not exist, and the
    /// symptom looks like a logic bug elsewhere. This project installs patch classes by name instead of
    /// calling PatchAll, so forgetting a registration is the most likely way to lose a feature. Naming the
    /// omission in the log turns that into a one-line diagnosis.
    /// </summary>
    private static void ReportUnregisteredPatches()
    {
        try
        {
            var orphans = typeof(PatchInstaller).Assembly.GetTypes()
                .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                .Where(type => !registered.Contains(type) && !installedElsewhere.Contains(type.Name))
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (orphans.Length > 0)
                Log.Warning("[TurboTick] 以下补丁类已声明但未安装（对应功能不会生效）：" + string.Join(", ", orphans));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 补丁登记自检失败：" + error.Message); }
    }
}





























