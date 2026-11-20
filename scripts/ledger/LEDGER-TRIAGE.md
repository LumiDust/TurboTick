> 这份表与同目录的 `triage_ledger.py` 是**钉在测试里的判据**，必须随代码进版本库：`docs/` 是本地忽略目录，
> 放那里会让 CI 上的判据静默跳过。日常改判词时在本目录改（或改完 `_lab/` 副本后同步过来），
> 提交前一并更新本文件与脚本。

# CT 账本定性结果（419 条）

生成器：`_lab/triage_ledger.py`（幂等，连跑两次输出一致）。本文件由 `_lab/make_report.py` 从
`_lab/ledger-triage-final.txt` 生成，改分类请改生成器，不要手改本文件。

## 汇总

| 状态 | 含义 | 条数 |
|---|---|---|
| `[x]` | 已由通道/补丁覆盖，或副本根本不走这条路 | 220 |
| `[-]` | 不适用 | 98 |
| `[~]` | 机制成立但输入未验证（载体为零，或入口未逐条测） | 101 |

**`[x]` 与 `[~]` 是两种不同的断言。** `[x]` 说"这条已经有人管"或"副本不走这条路"；
`[~]` 说"机制成立，但输入没验证"—— 载体为零时，一个能工作的通道和一个坏掉的通道读数完全一样，
所以零载体既不能记账为已覆盖，也不能记账为缺口。

## `[~]` 的 104 条按原因分布

| 条数 | 原因 |
|---|---|
| 29 | 副本自行计算 |
| 15 | Notify_* 回调 |
| 14 | gizmo/浮动菜单提供者 |
| 11 | 玩家动作入口/权威端 tick |
| 7 | 玩家动作入口 |
| 6 | 入口要能到权威端 |
| 5 | 事件/档案 |
| 5 | 派生值 |
| 4 | 待载体:royalty-count 实测有头衔 0 个 |
| 4 | 发射走世界对象+创建销毁 |
| 1 | 派系已同步 => 触发条件成立；但补调会新建空 tracker 抹掉权威端内容，而内容本身还没有通道 |

## 分类

- Z-OTHER（127）：127
- A-UI（52）：52
- F-WORLD（52）：52
- H-ENV（51）：51
- E-WORK（34）：34
- C-BUILD（31）：31
- D-HAUL（22）：22
- G-SOCIAL（20）：20
- B-HEALTH（10）：10
- K-ZONE（10）：10
- I-RESEARCH（6）：6
- J-ANIMAL（4）：4

## 全表

| 状态 | 分类 | 类型 | 方法 | 依据 |
|---|---|---|---|---|
| ~ | A-UI（52） | Alert_AnimaLinkingReady | GetTargets | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | Alert_FireInHomeArea | GetReport | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | Alert_LowFood | GetExplanation | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | Alert_LowMedicine | GetExplanation | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | Alert_NeedBatteries | NeedBatteries | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | Alert_NeedSlaveBeds | CheckSlaveBeds | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | AlertsReadout | AlertsReadoutOnGUI,AlertsReadoutUpdate | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | ArchivedDialog | ExposeData | 事件/档案（副本不产生；已发生由 TaleSync/MemorySync 或整份片段承载，逐条待核） |
| - | A-UI（52） | CameraDriver | Update | 绘制/相机/输入 |
| - | A-UI（52） | CameraJumper | TryHideWorld,TryJump | 绘制/相机/输入 |
| - | A-UI（52） | CameraShaker | DoShake,Update | 绘制/相机/输入 |
| ~ | A-UI（52） | CharacterCardUtility | DrawCharacterCard | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | A-UI（52） | ChoiceLetter_GrowthMoment | MakeChoices,TrySetChoices | 状态通道:抉择信件整份片段 + letter-choice 序号（MakeChoices 路径） |
| ~ | A-UI（52） | Command_Toggle | GizmoOnGUI | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| ~ | A-UI（52） | DateReadout | DateOnGUI | 副本自行计算（机制成立；每条输入是否已同步待核） |
| - | A-UI（52） | Dialog_BeginGravshipLaunch | Start | 绘制/相机/输入 |
| x | A-UI（52） | Dialog_BillConfig | FillSlotGroupOptions,GeneratePawnRestrictionOptions | 整份账单片段（Window.PreClose 采样，CommandCodec.bills；含三个方法与十三字段） |
| - | A-UI（52） | Dialog_ChooseThingsForNewColony | ConfirmArchonexusSettlementConsequences | 绘制/相机/输入 |
| - | A-UI（52） | Dialog_ConfigureIdeo | DoWindowContents | 绘制/相机/输入 |
| - | A-UI（52） | Dialog_CreateXenogerm | Accept,StartAssembly | 绘制/相机/输入 |
| - | A-UI（52） | Dialog_EnterPortal | AddItemsToTransferables,TryAccept | 绘制/相机/输入 |
| - | A-UI（52） | Dialog_LoadTransporters | AddItemsToTransferables,TryAccept | 绘制/相机/输入 |
| - | A-UI（52） | Dialog_SplitCaravan | TrySplitCaravan | 绘制/相机/输入 |
| - | A-UI（52） | Dialog_Trade | Close,DoWindowContents,FillMainRect | 绘制/相机/输入 |
| - | A-UI（52） | FactionDialogMaker | RequestMilitaryAidOption,RequestOrbitalTraderOption,RequestTraderOption | 绘制/相机/输入 |
| - | A-UI（52） | FloatMenuMakerWorld | ChoicesAtFor | 绘制/相机/输入 |
| ~ | A-UI（52） | FloatMenuOption | Chosen | 副本自行计算（机制成立；每条输入是否已同步待核） |
| - | A-UI（52） | FloatMenuOptionProvider | Applies | 绘制/相机/输入 |
| - | A-UI（52） | FloatMenuOptionProvider_DraftedAttack | GetMultiselectAttackOption | 绘制/相机/输入 |
| - | A-UI（52） | FloatMenuOptionProvider_DraftedMove | PawnGotoAction | 绘制/相机/输入 |
| - | A-UI（52） | FloatMenuOptionProvider_RescuePawn | GetSingleOptionFor | 绘制/相机/输入 |
| ~ | A-UI（52） | GizmoGridDrawer | DrawGizmoGridFor | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| ~ | A-UI（52） | HealthCardUtility | CreateSurgeryBill,DrawPawnHealthCard | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | A-UI（52） | HediffComp_SurgeryInspectableMetalhorror | DoSurgicalInspection | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| ~ | A-UI（52） | ITab_Bills | FillTab | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | ITab_Pawn_Health | FillTab,ShouldAllowOperations | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | ITab_Pawn_Needs | FillTab | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | ITab_Storage | FillTab | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | MainTabWindow_History | DoArchivableRow,DoWindowContents | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | MainTabWindow_Inspect | DoInspectPaneButtons,DoPaneContents,DoWindowContents,GetLabel | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | MainTabWindow_Quests | DoDismissButton,DoTitle,DoWindowContents,Select,ShouldListNow | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | A-UI（52） | MainTabWindow_Research | AttemptBeginResearch,DoWindowContents,DrawStartButton | 写操作是 SetCurrentProject，已由 ResearchManagerSetCurrentProjectPatch 覆盖（其余是纯查询） |
| x | A-UI（52） | MainTabWindow_Work | DoManualPrioritiesCheckbox | PlaySettingsSync（第 55 条通道送 useWorkPriorities/autoHomeArea/autoRebuild） |
| x | A-UI（52） | MainTabsRoot | ToggleTab | 纯谓词/查询或界面输入（不改变世界状态） |
| ~ | A-UI（52） | NeedsCardUtility | DrawThoughtListing | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | A-UI（52） | Page_SelectStorytellerInGame | * | 叙事者整份片段（PreClose 采样，CommandCodec.storyteller；非自定义时 difficulty 不在片段内） |
| ~ | A-UI（52） | PermitsCardUtility | DoLeftRect | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | A-UI（52） | ResourceReadout | ResourceReadoutOnGUI | 副本自行计算（机制成立；每条输入是否已同步待核） |
| - | A-UI（52） | Selector | HandleMultiselectGoto | 绘制/相机/输入 |
| - | A-UI（52） | SubEffecter_CameraShake | DoShake | 绘制/相机/输入 |
| - | A-UI（52） | Window | Close,InnerWindowOnGUI,OnAcceptKeyPressed,PostClose | 绘制/相机/输入 |
| - | A-UI（52） | WorldSelector | AutoOrderToTile | 绘制/相机/输入 |
| ~ | B-HEALTH（10） | Bill_Medical | Notify_IterationCompleted | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| x | B-HEALTH（10） | CompHasGatherableBodyResource | Gathered | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | B-HEALTH（10） | DamageWorker | ExplosionStart | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | B-HEALTH（10） | HealthAIUtility | ShouldBeTendedNowByPlayer | 纯谓词/查询或界面输入（不改变世界状态） |
| x | B-HEALTH（10） | Hediff | ExposeData | 状态通道:HediffSetSync + HediffTimerSync + 派生字段 |
| x | B-HEALTH（10） | Hediff_DuplicateSickness | PostTickInterval | 状态通道:HediffSync 整份 hediff 片段 |
| x | B-HEALTH（10） | Hediff_Pregnant | NotifyPlayerOfTrimesterPassing | 状态通道:HediffSync 整份 hediff 片段（pregnancy 在其 comp 里） |
| x | B-HEALTH（10） | IncidentWorker_Disease | PotentialVictims | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | B-HEALTH（10） | SurgeryOutcomeEffectDef | GetQuality | 纯谓词/查询或界面输入（不改变世界状态） |
| x | B-HEALTH（10） | WorkGiver_GatherAnimalBodyResources | ShouldSkip | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| ~ | C-BUILD（31） | AutoBuildRoofAreaSetter | TryGenerateAreaNow | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| ~ | C-BUILD（31） | Blueprint | TryReplaceWithSolidThing | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| ~ | C-BUILD（31） | Blueprint_Build | MakeSolidThing | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| ~ | C-BUILD（31） | Building | DeSpawn,DeconstructibleBy,DoDestroyEffects,GetGizmos | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | C-BUILD（31） | Building_BioferriteHarvester | TakeOutBioferrite | 状态通道:建筑补扫 |
| ~ | C-BUILD（31） | Building_Crate | GetMultiSelectFloatMenuOptions | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | C-BUILD（31） | Building_Door | CheckClearReachabilityCacheBecauseOpenedOrClosed,DoorPreDraw,DrawMovers,PawnCanOpen,SnapshotWillCloseSoonForJobs | 命名补丁:PawnCanOpen |
| x | C-BUILD（31） | Building_Enterable | SelectPawn | 建筑功能（权威端执行；产出经物品/hediff/状态通道） |
| x | C-BUILD（31） | Building_FermentingBarrel | TakeOutBeer | 状态通道:建筑补扫（整份片段） |
| x | C-BUILD（31） | Building_GeneAssembler | ExposeData,Finish,GetGenepacks,Start | 状态通道:建筑补扫（整份片段） |
| x | C-BUILD（31） | Building_GeneExtractor | Finish | 建筑功能（权威端执行；产出经物品/hediff/状态通道） |
| ~ | C-BUILD（31） | Building_GravEngine | ConsumeFuel,GetFloatMenuOptions,GetGizmos,Inspect,SpawnSetup | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| x | C-BUILD（31） | Building_NutrientPasteDispenser | FindFeedInAnyHopper,HasEnoughFeedstockInHoppers,TryDispenseFood | 建筑功能（权威端执行；产出经物品/hediff/状态通道） |
| x | C-BUILD（31） | Building_OrbitalTradeBeacon | AllPowered | 建筑功能（权威端执行；产出经物品/hediff/状态通道） |
| ~ | C-BUILD（31） | Building_OutfitStand | GetFloatMenuOptions | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | C-BUILD（31） | Building_SubcoreScanner | Tick | 建筑功能（权威端执行；产出经物品/hediff/状态通道） |
| x | C-BUILD（31） | Building_TurretGun | ExtractShell | 状态通道:建筑补扫（整份片段） |
| x | C-BUILD（31） | Building_VoidMonolith | Activate,GetGizmos,OpenActivatedDialog,UpdateAttachments | 状态通道:建筑补扫 + gap-audit 已证 disturbingVisionTick 需推非默认 |
| x | C-BUILD（31） | Designator_AreaBuildRoof | CanDesignateCell,DesignateSingleCell | 命名补丁:CanDesignateCell,DesignateSingleCell |
| x | C-BUILD（31） | Designator_Build | ProcessInput | 设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态） |
| x | C-BUILD（31） | Designator_Install | ProcessInput | 设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态） |
| x | C-BUILD（31） | LargeBuildingCellFinder | TryFindCell | 纯谓词/查询或界面输入（不改变世界状态） |
| x | C-BUILD（31） | ListerBuildings | DeregisterInstallBlueprint,RegisterInstallBlueprint | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | C-BUILD（31） | Mineable | DestroyMined | 状态通道:物品/建筑补扫（yieldPct 已判非缺口） |
| ~ | C-BUILD（31） | MinifiedThing | GetGizmos | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| - | C-BUILD（31） | MinifyUtility | MakeMinified | 世界生成/存档装载 |
| x | C-BUILD（31） | RepairUtility | PawnCanRepairEver,PawnCanRepairNow | 纯谓词/查询或界面输入（不改变世界状态） |
| x | C-BUILD（31） | WorkGiver_ConstructDeliverResourcesToBlueprints | HasJobOnThing,JobOnThing | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | C-BUILD（31） | WorkGiver_ConstructDeliverResourcesToFrames | HasJobOnThing,JobOnThing | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | C-BUILD（31） | WorkGiver_ConstructFinishFrames | JobOnThing | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | C-BUILD（31） | WorkGiver_Repair | PotentialWorkThingsGlobal,ShouldSkip | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| ~ | D-HAUL（22） | ApparelGraphicRecordGetter | TryGetGraphicApparel | 副本自行计算（机制成立；每条输入是否已同步待核） |
| - | D-HAUL（22） | CaravanInventoryUtility | AllInventoryItems | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| x | D-HAUL（22） | CompApparelVerbOwner | Initialize,SetupVerbs | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | D-HAUL（22） | DropCellFinder | RandomDropSpot,TradeDropSpot | 纯谓词/查询或界面输入（不改变世界状态） |
| - | D-HAUL（22） | DropPodUtility | MakeDropPodAt | 世界生成/存档装载 |
| x | D-HAUL（22） | HaulAIUtility | HaulToCellStorageJob | 纯谓词/查询或界面输入（不改变世界状态） |
| x | D-HAUL（22） | HiddenItemsManager | Hidden,SetDiscovered | 状态通道:DiscoverySync（整份 hiddenItemsManager + entityCodex 片段） |
| x | D-HAUL（22） | JobDriver_UseItem | MakeNewToils | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | D-HAUL（22） | JobGiver_OptimizeApparel | TryGiveJob | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | D-HAUL（22） | ListerThings | Remove | 索引维护（对象本身由创建/移除帧与对应通道重建，索引随之维护） |
| x | D-HAUL（22） | Pawn_ApparelTracker | ExposeData,Wear | 状态通道:GearSync（装备/衣物逐件） |
| x | D-HAUL（22） | Pawn_EquipmentTracker | AddEquipment | 状态通道:GearSync |
| x | D-HAUL（22） | Pawn_InventoryStockTracker | ExposeData | 状态通道:PolicyCapturePatch + policy 第六段（携带列） |
| x | D-HAUL（22） | QuestPart_DropPods | GetRandomDropSpot,Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| ~ | D-HAUL（22） | RitualObligationTrigger_MemberCorpseDestroyed | Notify_MemberCorpseDestroyed | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| ~ | D-HAUL（22） | RoyalTitlePermitWorker_DropResources | CallResources | 待载体:royalty-count 实测有头衔 0 个（第二份能力列表，与 AbilitySync 不同源） |
| - | D-HAUL（22） | ScenPart_ScatterThings | GenerateIntoMap | 世界生成/存档装载 |
| - | D-HAUL（22） | ThingMaker | MakeThing | 世界生成/存档装载 |
| x | D-HAUL（22） | ThingOwner | *,NotifyAdded,NotifyAddedAndMergedWith,NotifyRemoved | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | D-HAUL（22） | ThingUtility | CheckAutoRebuildOnDestroyed,CheckAutoRebuildTerrainOnDestroyed | 命名补丁:CheckAutoRebuildOnDestroyed |
| - | D-HAUL（22） | UnnaturalCorpse | ExposeData | 世界生成/存档装载 |
| x | D-HAUL（22） | UnnaturalCorpseTracker | ExposeData | 状态通道:ResidualCounterSync.research |
| x | E-WORK（34） | Bill | SetPawnRestriction | 整份账单片段（Window.PreClose 采样，CommandCodec.bills；含三个方法与十三字段） |
| x | E-WORK（34） | Bill_Production | SetIncludeGroup,SetStoreMode | 整份账单片段（Window.PreClose 采样，CommandCodec.bills；含三个方法与十三字段） |
| x | E-WORK（34） | GenRecipe | MakeRecipeProducts,PostProcessProduct | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | IncidentWorker | CanFireNow,TryExecute | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | IncidentWorker_AggressiveAnimals | TryExecuteWorker | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | IncidentWorker_AnimalInsanityMass | TryExecuteWorker | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | IncidentWorker_Raid | TryGenerateRaidInfo | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | InteractionWorker_ConvertIdeoAttempt | RandomSelectionWeight | 纯谓词/查询或界面输入（不改变世界状态） |
| x | E-WORK（34） | Job | ExposeData,GetReport | 纯谓词/查询或界面输入（不改变世界状态） |
| x | E-WORK（34） | JobDriver_ExtractBioferrite | MakeNewToils | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | JobDriver_PlantSow | MakeNewToils | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | JobDriver_PlantWork | MakeNewToils | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | JobDriver_Resurrect | MakeNewToils,Resurrect | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | JobDriver_RopeToPen | ShouldOpportunisticallyRopeAnimal | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | JobGiver_WanderColony | GetWanderRoot | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| ~ | E-WORK（34） | LifeStageWorker_HumanlikeAdult | Notify_LifeStageStarted | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| ~ | E-WORK（34） | LordJob_BestowingCeremony | GetPawnGizmos | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| ~ | E-WORK（34） | LordToil_BestowingCeremony_Wait | ExtraFloatMenuOptions,GetPawnGizmos | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| - | E-WORK（34） | PawnColumnWorker_Faction | ClickedIcon | 绘制/相机/输入 |
| x | E-WORK（34） | PawnColumnWorker_Timetable | DoTimeAssignment | 纯谓词/查询或界面输入（不改变世界状态） |
| - | E-WORK（34） | RaidStrategyWorker | MakeLords | 世界生成/存档装载 |
| x | E-WORK（34） | RecipeWorkerCounter | CountValidThing | 纯谓词/查询或界面输入（不改变世界状态） |
| ~ | E-WORK（34） | RoyalTitlePermitWorker_CallShuttle | CallShuttle | 待载体:royalty-count 实测有头衔 0 个（第二份能力列表，与 AbilitySync 不同源） |
| ~ | E-WORK（34） | StatWorker | GetValue | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | E-WORK（34） | ThinkNode_JobGiver | TryIssueJobPackage | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| ~ | E-WORK（34） | ThoughtWorker_Precept_BloodfeederPresent | ShouldHaveThought | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | E-WORK（34） | ThoughtWorker_Precept_GrowthVatInColony | ShouldHaveThought | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | E-WORK（34） | ThoughtWorker_Precept_NoRecentExecution | ShouldHaveThought | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | E-WORK（34） | ThoughtWorker_Precept_RecentConquest | ShouldHaveThought | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | E-WORK（34） | Toils_Recipe | FinishRecipeAndStartStoringProduct,MakeUnfinishedThingIfNeeded | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | WorkGiver_DoBill | TryFindBestBillIngredients | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | WorkGiver_Fish | NonScanJob | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | WorkGiver_Tame | JobOnThing | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| x | E-WORK（34） | WorkGiver_Uninstall | HasJobOnThing | AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道） |
| - | F-WORLD（52） | Caravan | AddPawn,GetInspectString | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| - | F-WORLD（52） | CaravanEnterMapUtility | Enter | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| - | F-WORLD（52） | CaravanFormingUtility | StartFormingCaravan | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| - | F-WORLD（52） | CaravanMaker | MakeCaravan | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| x | F-WORLD（52） | Caravan_PathFollower | PatherArrived,PatherTickInterval,StartPath,TryEnterNextPathTile | 状态通道:ReplicaCaravan（不移动，见 round-49 结论） |
| - | F-WORLD（52） | CaravansBattlefield | CheckWonBattle | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| x | F-WORLD（52） | Designator_MoveGravship | CanDesignateCell | 命名补丁:CanDesignateCell |
| - | F-WORLD（52） | ExpandableWorldObjectsUtility | ExpandableWorldObjectsOnGUI | 绘制/相机/输入 |
| ~ | F-WORLD（52） | FlyShipLeaving | LeaveMap | 发射走世界对象+创建销毁（GizmoStatePatch 只见 lastLaunchTick）；本存档 0 个运输舱，待载体 |
| ~ | F-WORLD（52） | Gravship | TickInterval | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| ~ | F-WORLD（52） | GravshipLandingMarker | BeginLanding | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| x | F-WORLD（52） | GravshipUtility | ArriveExistingMap,ArriveNewMap,ConsideredKidnapped,GenerateGravship,PreLaunchConfirmation | 世界存档/开局流程（权威端专有；世界对象由 WorldObjectSync 按 id 重建） |
| x | F-WORLD（52） | Lord | ExposeData,LordTick | 状态通道:LordSync（toil/职责） |
| - | F-WORLD（52） | LordMaker | MakeNewLord | 世界生成/存档装载 |
| - | F-WORLD（52） | MapDrawer | MapMeshDirty | 绘制/相机/输入 |
| - | F-WORLD（52） | MapFileCompressor | HashValueForSquare,ThingsToSpawnAfterLoad | 世界生成/存档装载 |
| x | F-WORLD（52） | MapInterface | HandleMapClicks,MapInterfaceUpdate | 纯谓词/查询或界面输入（不改变世界状态） |
| ~ | F-WORLD（52） | MapParent | GetShuttleFloatMenuOptions,GetTransportersFloatMenuOptions | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | F-WORLD（52） | NamePlayerFactionAndSettlementUtility | CanNameAnythingNow | 世界存档/开局流程（权威端专有；世界对象由 WorldObjectSync 按 id 重建） |
| - | F-WORLD（52） | PocketMapUtility | GeneratePocketMap | 世界生成/存档装载 |
| x | F-WORLD（52） | Quest | Accept,End,MakeRaw,Notify_SignalReceived,QuestTick | 状态通道:QuestSync + QuestPartSync |
| - | F-WORLD（52） | QuestGen | Generate | 世界生成/存档装载 |
| x | F-WORLD（52） | QuestManager | Add,Notify_PawnKilled,Notify_PlantHarvested | 状态通道:QuestSync |
| x | F-WORLD（52） | QuestPart_Choice | Choose | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_EndGame | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_FactionGoodwillChange | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_GiveRoyalFavor | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_GiveToCaravan | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_NewColony | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_PawnsArrive | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_RecordHistoryEvent | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_SetFaction | Notify_QuestSignalReceived | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestPart_SetQuestNotYetAccepted | ExposeData | 状态通道:QuestPartSync（整份 part 片段，信号在权威端处理） |
| x | F-WORLD（52） | QuestScriptDef | CanRun | 纯谓词/查询或界面输入（不改变世界状态） |
| x | F-WORLD（52） | QuestUtility | CanAcceptQuest,CanPawnAcceptQuest,TryGetIdealColonist | 纯谓词/查询或界面输入（不改变世界状态） |
| ~ | F-WORLD（52） | Settlement | GetCaravanGizmos,GetFloatMenuOptions,GetGizmos | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | F-WORLD（52） | SettlementData | Set | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| ~ | F-WORLD（52） | SettlementProximityGoodwillUtility | AppendGravshipAttackOffsets,AppendProximityGoodwillOffsets,CheckConfirmSettle | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| ~ | F-WORLD（52） | SettlementUtility | AffectRelationsOnAttacked | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| - | F-WORLD（52） | ShipCountdown | CancelCountdown,CountdownEnded,InitiateCountdown | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| x | F-WORLD（52） | TradeUtility | AllLaunchableThingsForTrade,LaunchThingsOfType | 纯谓词/查询或界面输入（不改变世界状态） |
| x | F-WORLD（52） | Tradeable | GetPriceFor,ResolveTrade | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| - | F-WORLD（52） | TransportShipMaker | MakeTransportShip | 世界生成/存档装载 |
| - | F-WORLD（52） | TravellingTransporters | DoArrivalAction | 被 CaravanGuardPatch 挡住（只有主机能组建远行队） |
| x | F-WORLD（52） | VoluntarilyJoinableLordsStarter | VoluntarilyJoinableLordsStarterTick | 纯谓词/查询或界面输入（不改变世界状态） |
| x | F-WORLD（52） | World | ExposeData,FillComponents | 世界存档/开局流程（权威端专有；世界对象由 WorldObjectSync 按 id 重建） |
| ~ | F-WORLD（52） | WorldComponent_GravshipController | <ExposeData>b__74_0,AbortLanding,BeginLandingCutscene,BeginTakeoffCutscene,DestroyTreesAroundSubstructure,FinalizeInit,InitiateLanding,InitiateTakeoff,LandingEnded,Notify_LandingAreaConfirmationStarted,TakeoffEnded,WorldComponentOnGUI,WorldComponentUpdate | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| - | F-WORLD（52） | WorldGenerator | GenerateWorld | 世界生成/存档装载 |
| x | F-WORLD（52） | WorldObject | DoTick,GetInspectString,InitializeComps | 状态通道:WorldObjectSync + WorldObjectStateSync |
| x | F-WORLD（52） | WorldObjectsHolder | Add | 世界存档/开局流程（权威端专有；世界对象由 WorldObjectSync 按 id 重建） |
| ~ | F-WORLD（52） | WorldRenderer | Notify_TilePollutionChanged | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| - | F-WORLD（52） | WorldTargeter | BeginTargeting | 绘制/相机/输入 |
| ~ | G-SOCIAL（20） | FactionIdeosTracker | RecalculateIdeosBasedOnPlayerPawns,SetPrimary | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| x | G-SOCIAL（20） | Ideo | ExposeData,IdeoTick,Notify_RelicSeenByPlayer | 状态通道:缺口：无 ideo 通道 |
| ~ | G-SOCIAL（20） | IdeoDevelopmentUtility | ApplyChangesToIdeo | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| x | G-SOCIAL（20） | IdeoManager | ExposeData,IdeoManagerTick,Remove,RemoveUnusedStartingIdeos | 状态通道:缺口：无 ideo 通道 |
| ~ | G-SOCIAL（20） | IdeoUtility | Notify_HistoryEvent,Notify_PlayerRaidedSomeone,Notify_QuestCleanedUp | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| ~ | G-SOCIAL（20） | IndividualThoughtToAdd | Add | 派生值（输入由 MemorySync/StorytellerSync/hediff 通道承载；mood 本身不传输） |
| x | G-SOCIAL（20） | MemoryThoughtHandler | ExposeData | 状态通道:ThoughtSync |
| - | G-SOCIAL（20） | MemoryUtility | ClearAllMapsAndWorld | CT 因 P2P 才需要的仲裁 |
| x | G-SOCIAL（20） | MentalStateHandler | TryStartMentalState | 状态通道:MentalStateSync（def#age#子类#fit） |
| x | G-SOCIAL（20） | Page_ChooseIdeoPreset | AssignIdeoToPlayer,DoNext | 开局选信念页（只在新建殖民地时走；副本从快照装载） |
| x | G-SOCIAL（20） | Pawn_IdeoTracker | IdeoConversionAttempt,SetIdeo | 状态通道:通道外：见 ledger 中的 ideo 缺口 |
| x | G-SOCIAL（20） | Pawn_RelationsTracker | ExposeData | 状态通道:MemorySync/SocialAttemptTrace + pawn 片段 |
| ~ | G-SOCIAL（20） | Precept_Relic | Notify_ThingLost | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| ~ | G-SOCIAL（20） | Precept_Ritual | AddObligation,ShowRitualBeginWindow | 玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测） |
| ~ | G-SOCIAL（20） | Precept_RoleSingle | Notify_MemberChangedFaction | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| x | G-SOCIAL（20） | SituationalThoughtHandler | AppendMoodThoughts,AppendSocialThoughts,CheckRecalculateSocialThoughts,Notify_SituationalThoughtsDirty,UpdateAllMoodThoughts | 状态通道:ThoughtSync |
| ~ | G-SOCIAL（20） | ThoughtHandler | GetAllMoodThoughts,GetDistinctMoodThoughtGroups,GetMoodThoughts,GetMoodThoughtsFor,MoodOffsetOfGroup,TotalMoodOffset | 派生值（输入由 MemorySync/StorytellerSync/hediff 通道承载；mood 本身不传输） |
| ~ | G-SOCIAL（20） | ThoughtToAddToAll | Add | 派生值（输入由 MemorySync/StorytellerSync/hediff 通道承载；mood 本身不传输） |
| ~ | G-SOCIAL（20） | Thought_IdeoRoleEmpty | CurrentStateInternal | 派生值（输入由 MemorySync/StorytellerSync/hediff 通道承载；mood 本身不传输） |
| ~ | G-SOCIAL（20） | Thought_Situational | RecalculateState | 派生值（输入由 MemorySync/StorytellerSync/hediff 通道承载；mood 本身不传输） |
| - | H-ENV（51） | BackCompatibility | GetBackCompatibleType,ResearchManagerPostLoadInit | 世界生成/存档装载 |
| ~ | H-ENV（51） | CompAbilityEffect_Farskip | AlliedPawnOnMap,PawnsToSkip | 入口要能到权威端（效果本身经 hediff/物品/信件通道）；副本点击是否被拦下未逐条测 |
| x | H-ENV（51） | CompAffectedByFacilities | CanLinkTo,IsFacilityActive | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| ~ | H-ENV（51） | CompAnalyzable | CanInteract,OnAnalyzed | 入口要能到权威端（效果本身经 hediff/物品/信件通道）；副本点击是否被拦下未逐条测 |
| - | H-ENV（51） | CompAncientHeatVent | PostDraw | 绘制/相机/输入 |
| x | H-ENV（51） | CompAnimalInsanityPulser | DoAnimalInsanityPulse | 状态通道:建筑补扫 |
| x | H-ENV（51） | CompAssignableToPawn | ForceRemovePawn | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| x | H-ENV（51） | CompAssignableToPawn_Grave | TryAssignPawn | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| x | H-ENV（51） | CompAutoCut | DesignatePlantsToCut | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| x | H-ENV（51） | CompBandNode | TuneTo | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| - | H-ENV（51） | CompBiosculpterPod | PostDraw | 绘制/相机/输入 |
| x | H-ENV（51） | CompColorable | PostExposeData | comp 状态随物件片段传输（字段平铺；CompScribeLabels 记录标签碰撞集） |
| x | H-ENV（51） | CompDeepDrill | TryProducePortion | 状态通道:建筑补扫 |
| x | H-ENV（51） | CompEggLayer | ProduceEgg | 状态通道:建筑补扫（整份片段里的 comp） |
| x | H-ENV（51） | CompExplosive | PostExposeData | comp 状态随物件片段传输（字段平铺；CompScribeLabels 记录标签碰撞集） |
| ~ | H-ENV（51） | CompGoldenCube | AnyPawnHasHediff,CompTickInterval,GiveHediff,OnInteracted,OrderDeactivation,ValidatePawn | 入口要能到权威端（效果本身经 hediff/物品/信件通道）；副本点击是否被拦下未逐条测 |
| x | H-ENV（51） | CompHackable | CanHackNow,ValidateHacker | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| x | H-ENV（51） | CompHatcher | CompTick,Hatch | 状态通道:建筑补扫/物品补扫 |
| x | H-ENV（51） | CompHitchingSpot | PostExposeData | comp 状态随物件片段传输（字段平铺；CompScribeLabels 记录标签碰撞集） |
| ~ | H-ENV（51） | CompLaunchable | CanLaunch,CompGetGizmosExtra,CompInspectStringExtra,TryLaunch | 发射走世界对象+创建销毁（GizmoStatePatch 只见 lastLaunchTick）；本存档 0 个运输舱，待载体 |
| x | H-ENV（51） | CompMeditationFocus | CanPawnUse | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| x | H-ENV（51） | CompPilotConsole | StartChoosingDestination_NewTemp | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| ~ | H-ENV（51） | CompPlantPreventCutting | CompGetGizmosExtra | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | H-ENV（51） | CompPollutionPump | Pump | 状态通道:建筑补扫 |
| - | H-ENV（51） | CompPowerPlantWater | PostDraw | 绘制/相机/输入 |
| ~ | H-ENV（51） | CompPsylinkable | CanPsylink,FinishLinkingRitual | 入口要能到权威端（效果本身经 hediff/物品/信件通道）；副本点击是否被拦下未逐条测 |
| x | H-ENV（51） | CompRefuelable | Refuel | 状态通道:建筑补扫（整份片段） |
| x | H-ENV（51） | CompScanner | Used | 组件自身字段（随物件片段走；谓词类本就只有查询） |
| ~ | H-ENV（51） | CompShuttle | CompGetGizmosExtra | 发射走世界对象+创建销毁（GizmoStatePatch 只见 lastLaunchTick）；本存档 0 个运输舱，待载体 |
| x | H-ENV（51） | CompSpawner | TryDoSpawn | 状态通道:建筑补扫 |
| ~ | H-ENV（51） | CompStatue | * | 副本自行计算（机制成立；每条输入是否已同步待核） |
| x | H-ENV（51） | CompStyleable | PostExposeData | comp 状态随物件片段传输（字段平铺；CompScribeLabels 记录标签碰撞集） |
| ~ | H-ENV（51） | CompTargetEffect_Resurrect | DoEffectOn | 入口要能到权威端（效果本身经 hediff/物品/信件通道）；副本点击是否被拦下未逐条测 |
| ~ | H-ENV（51） | CompTreeConnection | CompGetGizmosExtra,PostDestroy | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| ~ | H-ENV（51） | CompUsable | CompFloatMenuOptions,UsedBy | 入口要能到权威端（效果本身经 hediff/物品/信件通道）；副本点击是否被拦下未逐条测 |
| x | H-ENV（51） | CompUseEffect | DoEffect | 建筑功能（权威端执行；产出经物品/hediff/状态通道） |
| - | H-ENV（51） | CompVoidNode | OpenDialog | 绘制/相机/输入 |
| - | H-ENV（51） | CompressibilityDecider | DetermineReferences | 世界生成/存档装载 |
| x | H-ENV（51） | Designator_AreaIgnoreRoof | DesignateSingleCell | 命名补丁:DesignateSingleCell |
| x | H-ENV（51） | Designator_AreaNoRoof | CanDesignateCell,FinalizeDesignationSucceeded | 命名补丁:CanDesignateCell |
| x | H-ENV（51） | Filth | * | 状态通道:非三类补扫（ThingCategory.Filth） |
| x | H-ENV（51） | FogGrid | ClearAllFog,Notify_PawnEnteringDoor,Refog,Unfog | 命名补丁:Refog |
| x | H-ENV（51） | GameComponent_Anomaly | GameComponentTick,IsPawnHypnotized,Notify_MapRemoved,PawnHasUnnaturalCorpse,VoidAwakeningActive | 状态通道:gap-audit 已证 level 需推非默认；其余在建筑/物品片段里 |
| ~ | H-ENV（51） | GameComponent_Bossgroup | Notify_PawnKilled | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| ~ | H-ENV（51） | PawnComponentsUtility | AddAndRemoveDynamicComponents | 派系已同步 => 触发条件成立；但补调会新建空 tracker 抹掉权威端内容，而内容本身还没有通道（先要内容，再谈结构） |
| x | H-ENV（51） | Pawn_MeleeVerbs_TerrainSource | Create | 纯谓词/查询或界面输入（不改变世界状态） |
| x | H-ENV（51） | RoofGrid | SetRoof | 命名补丁:SetRoof |
| - | H-ENV（51） | SectionLayer_TerrainScatter | Regenerate | 绘制/相机/输入 |
| ~ | H-ENV（51） | StorytellerComp_Triggered | Notify_PawnEvent | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| x | H-ENV（51） | TerrainGrid | RemoveFoundation,RemoveTempTerrain,RemoveTopLayer,SetFoundation,SetTempTerrain,SetTerrain,SetTerrainColor,SetUnderTerrain | 命名补丁:RemoveTopLayer,SetFoundation |
| x | H-ENV（51） | Verb_PowerBeam | TryCastShot | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| - | I-RESEARCH（6） | FactionGenerator | NewGeneratedFactionWithRelations | 世界生成/存档装载 |
| ~ | I-RESEARCH（6） | Gene | Notify_PawnDied | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| x | I-RESEARCH（6） | PawnGenerator | IsValidCandidateToRedress | 纯谓词/查询或界面输入（不改变世界状态） |
| x | I-RESEARCH（6） | Pawn_PsychicEntropyTracker | SetInitialPsyfocusLevel | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | I-RESEARCH（6） | RitualObligationTargetFilter | CanUseTarget | 纯谓词/查询或界面输入（不改变世界状态） |
| ~ | I-RESEARCH（6） | RitualObligationTrigger_MemberDied | Notify_MemberDied | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| x | J-ANIMAL（4） | AnimalPenUtility | CheckUseAndReach | 纯谓词/查询或界面输入（不改变世界状态） |
| x | J-ANIMAL（4） | AutoSlaughterManager | CanAutoSlaughterNow,get_AnimalsToSlaughter | 纯谓词/查询或界面输入（不改变世界状态） |
| x | J-ANIMAL（4） | GiveToPackAnimalUtility | CarrierCandidatesFor | 纯谓词/查询或界面输入（不改变世界状态） |
| ~ | J-ANIMAL（4） | ReleaseAnimalToWildUtility | DoReleaseAnimal | 玩家动作入口（阵营由 FactionSync、移除由 removed 帧；只剩入口需单独命令） |
| x | K-ZONE（10） | AutoHomeAreaMaker | MarkHomeAroundThing,Notify_BuildingClaimed,Notify_BuildingSpawned,Notify_ZoneCellAdded | 副本侧的家区域扩展被 replica-home-area-suppress 拦下（否则每帧与 AreaSync 的差异应用互相打架） |
| x | K-ZONE（10） | Designator | Finalize | 设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态） |
| x | K-ZONE（10） | DesignatorManager | Deselect,Select | 设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态） |
| x | K-ZONE（10） | Designator_Cancel | CanDesignateThing | 命名补丁:CanDesignateThing |
| x | K-ZONE（10） | Designator_Uninstall | DesignateThing | 设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态） |
| x | K-ZONE（10） | Designator_ZoneAdd | DesignateMultiCell | 设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态） |
| x | K-ZONE（10） | Plan | GetGizmos,GetInspectString | 状态通道:PlanSync |
| x | K-ZONE（10） | PlanManager | DeregisterPlan,PlanAt,TryGetPlan | 索引维护（对象本身由创建/移除帧与对应通道重建，索引随之维护） |
| x | K-ZONE（10） | ZoneManager | DeregisterZone,RegisterZone | 索引维护（对象本身由创建/移除帧与对应通道重建，索引随之维护） |
| x | K-ZONE（10） | Zone_Fishing | GetFloatMenuOptions | 状态通道:ZoneSync（含 repeatCount） |
| ~ | Z-OTHER（127） | ActiveTransporter | PodOpen | 发射走世界对象+创建销毁（GizmoStatePatch 只见 lastLaunchTick）；本存档 0 个运输舱，待载体 |
| x | Z-OTHER（127） | AnalysisManager | TryIncrementAnalysisProgress | 已由现有通道/补丁覆盖（逐条注明依据） |
| ~ | Z-OTHER（127） | Archive | Add,get_ArchivablesListForReading | 事件/档案（副本不产生；已发生由 TaleSync/MemorySync 或整份片段承载，逐条待核） |
| x | Z-OTHER（127） | ArchonexusCountdown | ArchonexusCountdownUpdate,InitiateCountdown | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | AttackTargetFinder | BestAttackTarget | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| x | Z-OTHER（127） | AttackTargetsCache | GetPotentialTargetsFor | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| - | Z-OTHER（127） | Authority | CancelInvalidSettlementJobs | CT 因 P2P 才需要的仲裁 |
| - | Z-OTHER（127） | Bombardment | DrawAt | 绘制/相机/输入 |
| x | Z-OTHER（127） | CastPositionFinder | TryFindCastPosition | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| - | Z-OTHER（127） | CellBoolDrawer | CreateMaterialIfNeeded | 绘制/相机/输入 |
| - | Z-OTHER（127） | ColonistBarColonistDrawer | DrawColonist | 绘制/相机/输入 |
| x | Z-OTHER（127） | CustomXenogermDatabase | Add,Remove | 已由现有通道/补丁覆盖（逐条注明依据） |
| x | Z-OTHER（127） | DebugActionNode | Enter | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| - | Z-OTHER（127） | DebugLoadIDsSavingErrorsChecker | RegisterDeepSaved,RegisterReferenced | 世界生成/存档装载 |
| x | Z-OTHER（127） | Def | get_LabelCap | 无状态工具（只读本端已同步的 def/文本/输入） |
| x | Z-OTHER（127） | DiaOption | Activate | 无状态工具（只读本端已同步的 def/文本/输入） |
| x | Z-OTHER（127） | Faction | CanChangeGoodwillFor,CheckReachNaturalGoodwill,Notify_BuildingTookDamage,Notify_MemberTookDamage,TryAffectGoodwillWith | 状态通道:FactionSync（关系/善意） |
| - | Z-OTHER（127） | FactionManager | RecacheFactions | 世界生成/存档装载 |
| x | Z-OTHER（127） | FactionUtility | IsPoliticallyProper | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | FeedPatientUtility | ShouldBeFed | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | FishingUtility | GetCatchesFor | 只读派生值（查询已有状态，不写它） |
| - | Z-OTHER（127） | FleckManager | CreateFleck,FleckManagerTick | 绘制/相机/输入 |
| x | Z-OTHER（127） | Game | DeinitAndRemoveMap,Dispose,ExposeData,FillComponents,InitNewGame,LoadGame | 命名补丁:InitNewGame |
| - | Z-OTHER（127） | GameConditionMaker | MakeCondition | 世界生成/存档装载 |
| x | Z-OTHER（127） | GameConditionManager | GameConditionManagerTick,RegisterCondition | 已由现有通道/补丁覆盖（逐条注明依据） |
| - | Z-OTHER（127） | GameDataSaveLoader | SaveGame | 世界生成/存档装载 |
| x | Z-OTHER（127） | GameEnder | CheckOrUpdateGameOver | 状态通道:GameEnderSync |
| x | Z-OTHER（127） | GameVictoryUtility | ShowCredits | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | GenAI | RandomRaidDest | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| x | Z-OTHER（127） | GenGuest | AddHealthyPrisonerReleasedThoughts | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | GenLeaving | DoLeavingsFor,DropThingsNear | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| x | Z-OTHER（127） | GenStep_Scatterer | ShouldSkipMap | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | GenTypes | IsSealedWithCache | 无状态工具（只读本端已同步的 def/文本/输入） |
| - | Z-OTHER（127） | GenView | ShouldSpawnMotesAt | 绘制/相机/输入 |
| x | Z-OTHER（127） | GoodwillSituationManager | GetSituations,Recalculate | 已由现有通道/补丁覆盖（逐条注明依据） |
| x | Z-OTHER（127） | GrammarResolverSimple | Formatted | 无状态工具（只读本端已同步的 def/文本/输入） |
| ~ | Z-OTHER（127） | HistoryEventsManager | CheckRemoveOldEvents,ExposeData,RecordEvent | 事件/档案（副本不产生；已发生由 TaleSync/MemorySync 或整份片段承载，逐条待核） |
| x | Z-OTHER（127） | HumanEmbryo | CanImplantReport | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | IncidentParms | ExposeData,ShallowCopy | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | IncidentQueue | Add | 已由现有通道/补丁覆盖（逐条注明依据） |
| - | Z-OTHER（127） | JitterHandler | ProcessPostTickVisuals | 绘制/相机/输入 |
| x | Z-OTHER（127） | JoyUtility | JoyKindsOnMapTempList | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | KeyBindingDef | get_IsDownEvent | 无状态工具（只读本端已同步的 def/文本/输入） |
| x | Z-OTHER（127） | Letter | ExposeData | 状态通道:抉择信件整份片段（ExposeData 即载荷；LetterSync + letter-choice） |
| x | Z-OTHER（127） | LetterStack | LettersOnGUI,ReceiveLetter,RemoveLetter | 命名补丁:ReceiveLetter |
| x | Z-OTHER（127） | LoadedModManager | WriteModSettings | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | LoadedObjectDirectory | RegisterLoaded | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | Log | Error,Warning | 无状态工具（只读本端已同步的 def/文本/输入） |
| x | Z-OTHER（127） | LongEventHandler | ExecuteWhenFinished,QueueLongEvent | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | Message | ExposeData,ResetTimer | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | Messages | AcceptsMessage,Message | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| ~ | Z-OTHER（127） | MonumentMarker | GetGizmos | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| - | Z-OTHER（127） | Mote | SpawnSetup | 绘制/相机/输入 |
| - | Z-OTHER（127） | MultiPawnGotoController | FinalizeInteraction | 绘制/相机/输入 |
| - | Z-OTHER（127） | MusicManagerPlay | MusicUpdate | 绘制/相机/输入 |
| - | Z-OTHER（127） | Need | DrawOnGUI | 绘制/相机/输入 |
| - | Z-OTHER（127） | Need_Chemical_Any | DrawOnGUI | 绘制/相机/输入 |
| x | Z-OTHER（127） | OwnershipContext | JobAllowed | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | Page | DoBottomButtons | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| - | Z-OTHER（127） | Page_ConfigureStartingPawns | DoWindowContents | 绘制/相机/输入 |
| x | Z-OTHER（127） | ParseHelper | ParseDouble,ParseFloat | 无状态工具（只读本端已同步的 def/文本/输入） |
| ~ | Z-OTHER（127） | PawnBanishUtility | Banish | 玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测） |
| - | Z-OTHER（127） | PawnLeaner | get_LeanOffset | 绘制/相机/输入 |
| x | Z-OTHER（127） | PawnNameColorUtility | PawnNameColorOf | 无状态工具（只读本端已同步的 def/文本/输入） |
| - | Z-OTHER（127） | PawnNeedsUIUtility | GetThoughtGroupsInDisplayOrder | 绘制/相机/输入 |
| - | Z-OTHER（127） | PawnRenderTree | ParallelPreDraw,TrySetupGraphIfNeeded | 绘制/相机/输入 |
| - | Z-OTHER（127） | PawnTweener | PreDrawPosCalculation,TweenedPosRoot | 绘制/相机/输入 |
| x | Z-OTHER（127） | PawnUtility | TrySpawnHatchedOrBornPawn | 只读派生值（查询已有状态，不写它） |
| ~ | Z-OTHER（127） | Pawn_FlightTracker | Notify_JobStarted | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| ~ | Z-OTHER（127） | Pawn_GuestTracker | CapturedBy,Notify_PawnUndowned,SetGuestStatus | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| x | Z-OTHER（127） | Pawn_MeleeVerbs | ChooseMeleeVerb,ExposeData,TryGetMeleeVerb | 已由现有通道/补丁覆盖（逐条注明依据） |
| ~ | Z-OTHER（127） | Pawn_MutantTracker | Turn | 玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测） |
| - | Z-OTHER（127） | Pawn_RotationTracker | UpdateRotation | 绘制/相机/输入 |
| ~ | Z-OTHER（127） | Pawn_RoyaltyTracker | RefundPermits,ResetPermitsAndPoints,SetTitle,UpdateAvailableAbilities | 待载体:royalty-count 实测有头衔 0 个（第二份能力列表，与 AbilitySync 不同源） |
| x | Z-OTHER（127） | Pawn_StanceTracker | SetStance,StanceTrackerDraw | 已由现有通道/补丁覆盖（逐条注明依据） |
| ~ | Z-OTHER（127） | Pawn_TrainingTracker | Train | 玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测） |
| - | Z-OTHER（127） | PermadeathModeUtility | CheckUpdatePermadeathModeUniqueNameOnGameLoad | 世界生成/存档装载 |
| - | Z-OTHER（127） | PlaySettings | DoPlaySettingsGlobalControls | 绘制/相机/输入 |
| - | Z-OTHER（127） | PortraitsCache | SetDirty | 绘制/相机/输入 |
| - | Z-OTHER（127） | PostTickVisuals | ProcessPostTickVisuals | 绘制/相机/输入 |
| x | Z-OTHER（127） | PrefsData | Apply | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | ReadingPolicyDatabase | MakeNewReadingPolicy | 方案表通道（第 160 轮）：四张 `Policy` 表整表快照，按 `(Kind,Id)` 建+载；实测 `policy-table reading` 回环（删本地副本→应用行→同 id 同片段回来） |
| ~ | Z-OTHER（127） | RecruitUtility | Recruit | 玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测） |
| x | Z-OTHER（127） | ReservationManager | CanReserve | 命名补丁:CanReserve |
| x | Z-OTHER（127） | ResourceCounter | UpdateResourceCounts | 已由现有通道/补丁覆盖（逐条注明依据） |
| x | Z-OTHER（127） | Root_Play | SetupForQuickTestPlay,Update | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| ~ | Z-OTHER（127） | RoyalTitle | RoomRequirementGracePeriodActive | 待载体:royalty-count 实测有头衔 0 个（第二份能力列表，与 AbilitySync 不同源） |
| - | Z-OTHER（127） | ScenPart_PlayerPawnsArriveMethod | GenerateIntoMap | 世界生成/存档装载 |
| ~ | Z-OTHER（127） | ScenPart_PursuingMechanoids | GetAlerts,Notify_QuestCompleted,Tick | Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核） |
| - | Z-OTHER（127） | Scenario | GetFirstConfigPage,PostIdeoChosen | 世界生成/存档装载 |
| - | Z-OTHER（127） | ScreenFader | SetColor,StartFade | 绘制/相机/输入 |
| - | Z-OTHER（127） | ScribeExtractor | CreateInstance | 世界生成/存档装载 |
| - | Z-OTHER（127） | ScribeLoader | FinalizeLoading | 世界生成/存档装载 |
| - | Z-OTHER（127） | ScribeSaver | EnterNode,ExitNode | 世界生成/存档装载 |
| - | Z-OTHER（127） | Scribe_References | CheckSaveReferenceToDestroyedThing | 世界生成/存档装载 |
| ~ | Z-OTHER（127） | SettleUtility | AddNewHome | 玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测） |
| x | Z-OTHER（127） | SignalManager | SendSignal | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | Skyfaller | Impact | 效果落在已有通道的输出上（物品/容器/据点表/hediff/索引） |
| - | Z-OTHER（127） | SoundStarter | PlayOneShot,PlayOneShotOnCamera,TrySpawnSustainer | 绘制/相机/输入 |
| - | Z-OTHER（127） | StartingPawnUtility | NewGeneratedStartingPawn,RandomizePawn | 世界生成/存档装载 |
| - | Z-OTHER（127） | SteamManager | Update | 世界生成/存档装载 |
| ~ | Z-OTHER（127） | StorageSettingsClipboard | CopyPasteGizmosFor | gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核） |
| x | Z-OTHER（127） | StoryState | RecordPopulationIncrease | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | StoryWatcher_Adaptation | AdaptationWatcherTick,Notify_PawnEvent,get_Population | 状态通道:AdaptationSync |
| ~ | Z-OTHER（127） | StoryWatcher_PopAdaptation | Notify_PawnEvent,PopAdaptationWatcherTick | 事件/档案（副本不产生；已发生由 TaleSync/MemorySync 或整份片段承载，逐条待核） |
| x | Z-OTHER（127） | StorytellerUtility | DefaultParmsNow,DefaultThreatPointsNow | 只读派生值（查询已有状态，不写它） |
| - | Z-OTHER（127） | Sustainer | Cleanup,End,Maintain,SustainerUpdate | 绘制/相机/输入 |
| x | Z-OTHER（127） | TaggedString | Resolve | 无状态工具（只读本端已同步的 def/文本/输入） |
| ~ | Z-OTHER（127） | TaleRecorder | RecordTale | 事件/档案（副本不产生；已发生由 TaleSync/MemorySync 或整份片段承载，逐条待核） |
| - | Z-OTHER（127） | Targeter | BeginTargeting | 绘制/相机/输入 |
| - | Z-OTHER（127） | TilePicker | StartTargeting_NewTemp | 绘制/相机/输入 |
| - | Z-OTHER（127） | TransferableUIUtility | DrawCaptiveTradeInfo | 绘制/相机/输入 |
| x | Z-OTHER（127） | Translator | Translate | 无状态工具（只读本端已同步的 def/文本/输入） |
| x | Z-OTHER（127） | TransporterUtility | IncomingTransporterPreventingMapRemoval | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | TrashUtility | ShouldTrashBuilding | 只读派生值（查询已有状态，不写它） |
| x | Z-OTHER（127） | Trigger_PawnHarmed | ActivateOn | 已由现有通道/补丁覆盖（逐条注明依据） |
| - | Z-OTHER（127） | UI | MouseCell,MouseMapPosition | 绘制/相机/输入 |
| - | Z-OTHER（127） | UIRoot_Play | UIRootOnGUI | 绘制/相机/输入 |
| - | Z-OTHER（127） | VerbTracker | InitVerbsFromZero | 世界生成/存档装载 |
| x | Z-OTHER（127） | Verb_Bombardment | TryCastShot | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| x | Z-OTHER（127） | Verb_CastAbility | ValidateTarget | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| x | Z-OTHER（127） | Verb_CastTargetEffect | TryCastShot | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| x | Z-OTHER（127） | Verb_Spawn | TryCastShot | 目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧） |
| x | Z-OTHER（127） | VoidAwakeningUtility | CloseMetalHell | 权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走 |
| x | Z-OTHER（127） | WealthWatcher | ForceRecount,WealthItemsFilter | 只读派生值（查询已有状态，不写它） |
| ~ | Z-OTHER（127） | Widgets | EndScrollView | 副本自行计算（机制成立；每条输入是否已同步待核） |
| ~ | Z-OTHER（127） | Xenogerm | SetTargetPawn | 玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测） |

## 两条判词落成代码事实（第 167 轮）

第 164 轮的 `Bombardment` 与 `TravellingTransporters` 判词当时停在类别论证（「绘制/相机」「被 CaravanGuardPatch 挡住」），
本轮把两者读到代码行：

### `TravellingTransporters.DoArrivalAction` —— `-`，真正的门是世界物件通道

`TravellingTransporters : WorldObject, IThingHolder`（`:797956`），字段 `destinationTile`/`arrivalAction`/
`transporters`/`arrived`/`initialTile`/`traveledPct`。副本不需要任何专用补丁：
`Source/WorldObjectLifecycleSync.cs:97` 的**创建片段就是 `ScribeBridge.Save(each)`**（整份 `ExposeData` 往返），
`Source/WorldObjectStateSync.cs` 继续比较整份片段（含 `comps[i].PostExposeData()`，`:798523-798556`），
身份用 `WorldObject.ID`。所以目的地、抵达动作、舱内小人、进度百分比全部随片段走。
`DoArrivalAction` 本身只在权威端跑，它造成的后果（落点地图上生成舱体、小人落位）走 thing 行。
原判词点错了门，结论不变：这一行不需要补丁。

### `Bombardment.DrawAt` —— `-`，但这是一处**已知的视觉缺口**

`Bombardment : OrbitalStrike`（`:590674`）自己只带 `lifeTime`/`maxLifeTime`/`targetCell` 三个计时器字段；
它（以及 `Tornado`/`OrbitalDebrisSpawner`/`RectTrigger`/`SignalAction_*`）的 def 都继承 `EtherealThingBase`，
`<category>Ethereal</category>`（`Data/Core/Defs/ThingDefs_Misc/Ethereal_Various.xml:5`），
而 `Source/HeadlessRuntime.cs:1322` 的地图差异循环**跳过 `Ethereal`**（`Blueprint` 除外）⇒ 炮击只在权威端存在。
后果（爆炸伤害、建筑被毁）走 thing 行，所以**状态不发散**；但副本玩家的屏幕上**看不到**这场炮击。
`载体覆盖` 普查行多轮点过这批载体，本轮把结论写成：状态覆盖、视觉未覆盖。

判词格式按第 164 轮的规矩：写点在哪、副本走不走那个方法、读者是谁。
