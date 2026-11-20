#!/usr/bin/env python3
"""按 TurboTick 的模型给 CT 账本逐项定性，而不是按 CT 的模型。

## 为什么账本不能直接当工作清单

CT 是 P2P：每个 peer 都模拟，所以它必须给**每一个具体动作**打补丁，让这次调用按同一顺序在所有人身上发生。
我们只有一个权威端、副本不模拟，所以玩家动作只要在**基类**拦一次，交给权威端执行即可。
实测：`DesignatorSync` 反射遍历 `Designator` 全部子类，运行时有 54 个 Designator 类型、
126 个方法在补丁表里；`GizmoStatePatch` 挂 13 个 `Command*` 的 `ProcessInput`；
`SharedActions` 另有 24 个 choke point。

于是「账本一项 = 我们一项」是错的：我们一个补丁点常覆盖账本 5–15 项。
把账本当工作清单，会得到 393 个假缺口 —— 和之前「92 项全是假缺口」是同一个错误，只是规模更大。

## 四类判据

[x] 已覆盖  四条路径任一成立，且都有运行时可查的证据：
            1. `设计器路由`：`patches_now.txt` 里该类型是 `Designator*`，且有 `DesignateSingleCell`/
               `CanDesignateCell`/`CanDesignateThing` —— 玩家动作被拦下送到权威端；
            2. `Gizmo 路由`：`Command*` 的 `ProcessInput` —— 按钮点击同样送权威端；
            3. `命名补丁`：账本的方法名在补丁表里逐字命中；
            4. `状态通道`：该类型的状态由 52 条通道之一承载（下表 `CHANNEL_TYPES`）。
[-] 不适用  副本不跑这条路径：绘制/相机、世界生成/存档装载、以及 CT 因 P2P 才需要的仲裁。
[ ] 缺口    以上皆不成立。
"""
import collections
import pathlib
import io
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROJECT = ROOT / 'TurboTick-headless'

# 52 条通道实际承载的类型（见 progress.md「通道清单」；这里只列账本会问到的那些）
CHANNEL_TYPES = {
    # 方案表（第 160 轮）：四张 `Policy` 表整表快照走 `PolicyTableSync`（`ReplicaPolicy(Kind,Id,Label,Xml)`），
    # 按 `(Kind, Id)` 建+载。此前 `Source/` 里一个 `*PolicyDatabase` 都没提过，`PolicyCapturePatch` 送出的
    # tracker 片段引用的方案对象在副本上根本不存在（引用解析失败只回落默认值，不报错）。
    # 实测 `policy-table reading`：造 #8 → 强制发布（发 28 行，本行 173035 B 与原文一致）→ 删掉本地副本（成功）
    # → 应用该行回来 1 个（同 id 同片段）→ 清理成功；`policy-table list`：服饰 6 / 药品 4 / 饮食 10 / 阅读 7。
    # 派系新建（第 162 轮）：`FactionSync` 只送「两端都已有」的派系的关系/冷却/意识形态，而游戏会在权威端**造新派系**
    # （`FactionGenerator.NewGeneratedFactionWithRelations`）。新通道 `FactionCreationSync` 整份 `Faction` 片段按 loadID
    # 只发一次，副本用 `FactionManager.Add` 收（它内部调私有的 `RecacheFactions` 重建十一个 `OfX` 缓存指针并通知每张地图）。
    # 实测 `faction-create`：通知 #3 发布前 True → 发布后 False；虚构 id 1022 → False；重建片段 2840 B 成功（def/loadID/
    # 名字一致、未进表）；应用既有行 新建 0；基线 23 行 61000 B。
    'FactionCreationSync': 'FactionCreationSync（整份 Faction 片段，经 FactionManager.Add）',
    'FactionManager': 'FactionCreationSync（Add 内含私有的 RecacheFactions）',
    'FactionGenerator': 'FactionCreationSync（生成只在权威端；副本收结果，不是逐调用重放）',
    'ReadingPolicyDatabase': 'PolicyTableSync（整表快照，按 (Kind,Id) 建+载）',
    'OutfitDatabase': 'PolicyTableSync（同上，kind=apparel）',
    'DrugPolicyDatabase': 'PolicyTableSync（同上，kind=drug）',
    'FoodRestrictionDatabase': 'PolicyTableSync（同上，kind=food）',
    'ReadingPolicy': 'PolicyTableSync（片段即对象）',
    'ApparelPolicy': 'PolicyTableSync（片段即对象）',
    'DrugPolicy': 'PolicyTableSync（片段即对象）',
    'FoodPolicy': 'PolicyTableSync（片段即对象）',
    'Dialog_ManageReadingPolicies': 'PolicyTableSync（对话框只改表，表由通道送）',
    'Dialog_ManageApparelPolicies': 'PolicyTableSync（同上）',
    'Dialog_ManageDrugPolicies': 'PolicyTableSync（同上）',
    'Dialog_ManageFoodPolicies': 'PolicyTableSync（同上）',

    # 发现状态：`Game` 上的两份可 Scribe 数据库，决定玩家能看到哪些物品种类与图鉴条目。
    # 实测 `discovery-probe`：`隐藏物品表 35 项，其中仍隐藏 34 项，已解锁 1 项；片段 1857 B；图鉴 3 类
    # （已发现 1）/ 27 条目（已发现 1）；片段 1680 B` —— 载体存在，所以建了通道；
    # `customXenogermDatabase` 为 0，未建（见 progress.md）。
    # 携带列（pawn 表「携带」）：`PawnColumnWorker_Carry` 调 `SetThingForGroup`（`:676944`）/`SetCountForGroup`
    # （`:676960`），补丁已加进 `PolicyCapturePatch`，第六段 `stockXml` 走 `CommandCodec.policy`。
    # 实测 `tracker-probe`：`inventoryStock×3`（3 个小人有 tracker），所以载体存在。
    'Pawn_InventoryStockTracker': 'PolicyCapturePatch + policy 第六段（携带列）',
    'InventoryStockGroupDef': 'PolicyCapturePatch（作 key 随片段而来）',
    'InventoryStockEntry': 'PolicyCapturePatch（Deep 片段里的条目）',
    'PawnColumnWorker_Carry': 'PolicyCapturePatch（两个 setter 都挂上了）',
    # 抉择信件：`ReplicaLetter.Fragment` 发整份 `ExposeData` 片段，副本按 `def.letterClass` 建出正确子类再灌入；
    # 玩家点选项时 `DiaOption.Activate` 被拦下、`CommandCodec.letter-choice` 把序号上送，权威端执行同一个 `action`。
    # 注意 `ChoiceLetter_GrowthMoment : LetterWithTimeout`（`:662067`）**不是** `ChoiceLetter` 子类，走 `MakeChoices`。
    # 实测 `letter-probe`：`片段 1929 B；重建 成功，同类型 是；小人回指 一致；特性选项 3→3，激发选项 2`。
    # `Letter.ExposeData`（`:182998-183009`）不是「存档装载」而是**通道载荷的生成器**：`ReplicaLetter.Fragment` 就是它
    # 的输出，副本用它重建抉择信件。这条规则必须在存档正则之前判，否则会被当成「副本不存取档」而不适用。
    'Letter': '抉择信件整份片段（ExposeData 即载荷；LetterSync + letter-choice）',
    'LetterWithTimeout': '抉择信件整份片段（同上；成长时刻的基类）',
    'ChoiceLetter': '抉择信件整份片段（Choices 基类 + letter-choice 序号）',
    'ChoiceLetter_GrowthMoment': '抉择信件整份片段 + letter-choice 序号（MakeChoices 路径）',
    'ChoiceLetter_AcceptJoiner': '抉择信件整份片段 + letter-choice 序号（Choices 路径）',
    'ChoiceLetter_AcceptCreepJoiner': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_AcceptVisitors': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_BabyBirth': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_BabyToChild': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_BetrayVisitors': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_ChoosePawn': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_EntityDiscovered': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_GameEnded': '抉择信件整份片段 + letter-choice 序号',
    'ChoiceLetter_RansomDemand': '抉择信件整份片段 + letter-choice 序号',
    'HiddenItemsManager': 'DiscoverySync（整份 hiddenItemsManager + entityCodex 片段）',
    'EntityCodex': 'DiscoverySync（整份 entityCodex 片段）',
    'EntityCodexEntryDef': 'DiscoverySync（entityCodex 的条目表随之而来）',
    'EntityCategoryDef': 'DiscoverySync（entityCodex 的分类表随之而来）',
    'Hediff': 'HediffSetSync + HediffTimerSync + 派生字段',
    'Hediff_DuplicateSickness': 'HediffSync 整份 hediff 片段',
    'Hediff_Pregnant': 'HediffSync 整份 hediff 片段（pregnancy 在其 comp 里）',
    'Pawn_ApparelTracker': 'GearSync（装备/衣物逐件）',
    'Pawn_EquipmentTracker': 'GearSync',
    'Pawn_RelationsTracker': 'MemorySync/SocialAttemptTrace + pawn 片段',
    'Pawn_IdeoTracker': '通道外：见 ledger 中的 ideo 缺口',
    'MentalStateHandler': 'MentalStateSync（def#age#子类#fit）',
    'MemoryThoughtHandler': 'ThoughtSync',
    'SituationalThoughtHandler': 'ThoughtSync',
    'UnnaturalCorpseTracker': 'ResidualCounterSync.research',
    'Zone_Fishing': 'ZoneSync（含 repeatCount）',
    'Plan': 'PlanSync',
    'Lord': 'LordSync（toil/职责）',
    'Caravan_PathFollower': 'ReplicaCaravan（不移动，见 round-49 结论）',
    'WorldObject': 'WorldObjectSync + WorldObjectStateSync',
    'TerrainGrid': 'TerrainSync（SetTerrain/RemoveTopLayer 等全部走通道）',
    'RoofGrid': 'RoofSync（SetRoof）',
    'FogGrid': 'FogSync（Refog/Unfog）',
    'SnowGrid': 'SnowSync',
    'RoomTempTracker': 'RoomTempSync',
    'Filth': '非三类补扫（ThingCategory.Filth）',
    'Plant': '植物补扫（sown）',
    'CompRefuelable': '建筑补扫（整份片段）',
    'CompDeepDrill': '建筑补扫',
    'CompSpawner': '建筑补扫',
    'CompHatcher': '建筑补扫/物品补扫',
    'CompEggLayer': '建筑补扫（整份片段里的 comp）',
    'CompPollutionPump': '建筑补扫',
    'CompAnimalInsanityPulser': '建筑补扫',
    'Building_FermentingBarrel': '建筑补扫（整份片段）',
    'Building_GeneAssembler': '建筑补扫（整份片段）',
    'Building_BioferriteHarvester': '建筑补扫',
    'Building_TurretGun': '建筑补扫（整份片段）',
    'Building_Door': 'DoorSync + 建筑补扫',
    'Building_VoidMonolith': '建筑补扫 + gap-audit 已证 disturbingVisionTick 需推非默认',
    'Mineable': '物品/建筑补扫（yieldPct 已判非缺口）',
    'Quest': 'QuestSync + QuestPartSync',
    'QuestManager': 'QuestSync',
    # 这两条原来写的是「缺口：无 ideo 通道」，但 `IdeoSync` 早就存在并发布了 ideo 表
    # （id/name/color/certainty/development），注释与分类（x）自相矛盾。现在 precept 也由第 63 条
    # `PreceptSync` 补上，所以真正剩下的只有 `relicsCollected` 一个布尔（`Ideo.ExposeData` `:492129`），
    # 它是「玩家是否已集齐遗物」的一次性成就位，改判不改模拟输入。
    'Ideo': 'IdeoSync（表）+ PreceptSync（戒律与角色持有者）；剩 relicsCollected 一个成就位',
    'IdeoManager': 'IdeoSync 读 IdeosListForReading；PreceptSync 按 ideo.id 对账',
    'Faction': 'FactionSync（关系/善意）',
    'SettlementData': 'SettlementSync（整表按 Version 对账）。第 78 条：PawnHome 在 Publish 前按「整局都找不到」清理，因为这张表只有写入口没有删除口，死人/离队者会永久占着一个据点的成员名额。第 79 条：settlement-retire 是 Retired 的唯一写入点——此前该字段被包括 SettlementAt / SettlementAreas / 报告在内的 7 处读取，却从不被置 true，据点建了就永在。第 80/81 条：Pacts 与 Overrides 此前只 scribe 不上线，副本停在存档那一刻的战争与格子例外；至此本地这张表的每个字段（Sites/Members/PawnHome/Grants/Defaults/Pacts/Overrides）都在线上，只有注明「没有读者」的占位字段 PendingWars 不发',
    'SettlementPact': 'SettlementSync（第 80 条：Pacts 此前只 scribe 不上线，副本停在存档那一刻的战争状态，闸门对同一件事给出相反答案；现在按 a,b,warSince,allied 四元组随 SettlementWire 整表替换）',
    'SettlementGates': '闸门本体（第 82 条：pawn 版 CanCell 曾委托给读 Find.CurrentMap 的 player 版，多地图时同一组坐标在两张图上答案不同；现在用 pawn.MapHeld）',
    'GameComponent_Anomaly': 'gap-audit 已证 level 需推非默认；其余在建',
    'CompStudiable': 'AnomalySync（anomalyKnowledgeGained）',
    'PassingShip': 'PassingShipSync（载荷带 m<uniqueID>，逐图写倒计时；此前 Apply 只写 Find.CurrentMap，非当前图的船永不离场）',

    'GameEnder': 'GameEnderSync',
    'StoryWatcher_Adaptation': 'AdaptationSync',
    'Pawn_AbilityTracker': 'AbilitySync',
    'Need_Rest': 'NeedsSync（含 ticksAtZero）',
}

# 副本不跑这些路径。按**做了什么**判断，不按类型名。
# Order matters and is the whole content of this list: `ArchivedDialog — ExposeData` matched 绘制 first (because of
# "Dialog") and was labelled wrong - the status was right but the reason named the wrong subsystem, which is the exact
# defect this file exists to avoid. Save/load is the more specific behaviour, so it is tested first.
# **Five names removed from the save/load marker (round 118).** `MakeThing`, `MakeMinified`, `MakeDropPodAt`,
# `MakeTransportShip` and `MakeCondition` are runtime factories, not save/load entry points, and matching them by name
# put real gameplay on the inapplicable list. Measured: `DropPodUtility.MakeDropPodAt` (`:602808`) spawns a skyfaller
# and hands its contents to the map during play, and ColonyTogether patches it (`ct045:324140`,
# `OwnershipDeliveredContents`) to stamp the delivery's owner; `MinifyUtility.MakeMinified` (`:569866`) is what turns a
# building into a crate, and CT patches that too (`ct045:204556`, `:322866`). Labelling them 「世界生成/存档装载」 is the
# exact failure this file's header warns about: a rule matched on the method's name instead of what it does.
# `ScenPart_*.GenerateIntoMap` stays: a scenario part generating starting contents really is world setup.
# **Per-thing ownership: a model difference, not a missing patch (round 118).** CT keeps
# `Dictionary<Thing, int> Owners` (`ct045:369236`, scribed at `:369316`) so that "this object belongs to colony 3"
# survives being crated, carried or dropped. Its `MakeMinified` (`:322866`) and `MakeDropPodAt` (`:324140`) patches exist
# to copy that owner onto the new object. TurboTick has no such table on purpose: `SettlementRule.HomeOf`
# (`Source/SettlementRule.cs:177`) answers with `SettlementAt(map, thing.PositionHeld)` - the land decides, so a thing
# necessarily belongs to the settlement whose rectangle it is standing in, and every gate already asks about the cell
# being acted on. There is nothing for a factory to stamp. Recorded here so the five rows below are a stated decision
# rather than a silent skip.
MODEL_DIFFERENCES = {
    ('C-BUILD（31）', 'MinifyUtility', 'MakeMinified'): '地块制所有权：装箱不改变归属，无需写表（CT 有 Owners 字典）',
    ('D-HAUL（22）', 'DropPodUtility', 'MakeDropPodAt'): '地块制所有权：落舱内容物按落地地块归属（CT 有 Owners 字典）',
    ('D-HAUL（22）', 'ThingMaker', 'MakeThing'): '纯工厂：只构造并设字段，无副作用可同步',
    ('F-WORLD（52）', 'TransportShipMaker', 'MakeTransportShip'): '纯工厂：只装配飞船对象',
    ('Z-OTHER（127）', 'GameConditionMaker', 'MakeCondition'): '纯工厂：只构造 GameCondition',
}

# **参考 mod 自己的类型不属于「vanilla 缺口」。** `Authority` 只存在于 ColonyTogether 的程序集里
# （`_lab/ct045/ct045.decompiled.cs:32274` 调用其 `CancelInvalidSettlementJobs`），1.7 的 `Assembly-CSharp` 里
# 没有这个类（9634 个类型里零命中）。上一版把它当成一条待修的 vanilla 行记着 —— 一条永远修不掉的行会一直留在
# `-` 里，而且看起来和真缺口一模一样。改用「类型是否在 vanilla 里存在」作为过滤器，而不是继续逐条记名字。
# Types that exist only in ColonyTogether's assembly, so no amount of work here can cover them.
# `Authority` (`_lab/ct045/ct045.decompiled.cs:32274` calls `CancelInvalidSettlementJobs`) and
# `OwnershipContext` (`JobAllowed` is its only method we ever met) have zero hits among the 9634 vanilla
# types. Kept in the ledger so the CT comparison stays complete, reported as `-` with a reason that says
# it is unreachable rather than uncovered.
REFERENCE_ONLY_TYPES = {'Authority', 'OwnershipContext'}

NOT_APPLICABLE = [
    ('世界生成/存档装载', re.compile(
        r'(ExposeData|LoadGame|SaveGame|InitNewGame|GenerateWorld|FillComponents\(|GenerateIntoMap|'
        r'GeneratePocketMap|MakeRaw|QuestGen|GetBackCompatibleType|ResearchManagerPostLoadInit|'
        r'ThingsToSpawnAfterLoad|HashValueForSquare|RegisterDeepSaved|RegisterReferenced|'
        r'DetermineReferences|CheckRemoveOldEvents|RecacheFactions|RemoveUnusedStartingIdeos|'
        r'NewGeneratedFactionWithRelations|MakeCaravan|'
        r'MakeLords|MakeNewLord|Generate\b|'
        # 存档读写与启动装配：Scribe 三段式、场景/起始小人生成、难度重算、Steam 回调。
        # 副本从不存取档，也从不重开一局，所以这些方法在副本上不该被调用。
        r'ScribeSaver|ScribeLoader|ScribeExtractor|CheckSaveReferenceToDestroyedThing|FinalizeLoading|'
        r'EnterNode|ExitNode|CreateInstance|GetFirstConfigPage|PostIdeoChosen|NewGeneratedStartingPawn|'
        r'RandomizePawn|Apply\(|SteamManager|CheckUpdatePermadeathModeUniqueNameOnGameLoad|'
        r'SetupForQuickTestPlay|MakeNewReadingPolicy|InitVerbsFromZero|DefaultParmsNow)')),
    ('绘制/相机/输入', re.compile(
        r'(Draw|OnGUI|PreDraw|PostDraw|Camera|Shake|Fleck|Mote|Selector|Targeter|Readout|'
        r'DoWindowContents|DoLeftRect|Window|Dialog|FloatMenu|Gizmo|Icon|DoTitle|DoDismiss|'
        r'Overlay|Layer|BeginTargeting|SetColor|StartFade|Sound|Music|ScreenFader|'
        r'ShouldListNow|DoNext|ShowRitualBeginWindow|OpenActivatedDialog|PostDraw|'
        # 实测这 13 个是渲染/输入/音效，名字里没有上面任何一个词：
        # `PawnRenderTree.ParallelPreDraw/TrySetupGraphIfNeeded`、`PawnTweener.PreDrawPosCalculation/TweenedPosRoot`、
        # `Pawn_RotationTracker.UpdateRotation`、`Pawn_StanceTracker.StanceTrackerDraw`、`PortraitsCache.SetDirty`、
        # `PostTickVisuals.ProcessPostTickVisuals`、`Sustainer.Cleanup/End/Maintain/SustainerUpdate`（音效）、
        # `UI.MouseCell/MouseMapPosition`、`PlaySettings.DoPlaySettingsGlobalControls`、
        # `TilePicker.StartTargeting_NewTemp`、`PawnNeedsUIUtility.GetThoughtGroupsInDisplayOrder`、
        # `PawnLeaner.get_LeanOffset`、`MultiPawnGotoController.FinalizeInteraction`（右键菜单交互）。
        # 判据是"这个方法的输出只影响本端画面"，不是"名字里有 Draw"。
        r'TrySetupGraph|ProcessPostTickVisuals|TweenedPosRoot|PreDrawPosCalculation|SetDirty|UpdateRotation|'
        r'StanceTrackerDraw|Sustainer|MouseCell|MouseMapPosition|StartTargeting|'
        r'GetThoughtGroupsInDisplayOrder|get_LeanOffset|FinalizeInteraction)')),
    ('CT 因 P2P 才需要的仲裁', re.compile(
        r'(CancelInvalidSettlementJobs|DebugLoadIDs|Register\(|Deregister|Recache|ForceRecount|'
        r'ClearAllMapsAndWorld|CheckRemoveOldEvents)')),
]


def load_ledger():
    text = (ROOT / '_lab' / 'LEDGER-ct-patches.md').read_text(encoding='utf-8')
    entries, cat = [], None
    for line in text.splitlines():
        if line.startswith('## '):
            cat = line[3:].strip()
        # Any status mark, not only `[ ]`: the ledger is written back with `[x]`/`[-]` after the first pass, and a
        # loader that only matched `[ ]` silently re-triaged 244 of 419 the second time it ran.
        m = re.match(r'- \[[ x~-]\] `([^`]+)` — (.+)', line)
        if m:
            # The ledger is written back with a trailing `<!-- reason -->`, and the first loader glued that onto the
            # last method name: `CanDesignateThing  <!-- 命名补丁…` never matched, so four already-covered entries
            # reported as gaps. The note is bookkeeping, not part of the method list.
            methods = re.sub(r'<!--.*?-->', '', m.group(2))
            entries.append((cat, m.group(1), [x.strip() for x in methods.split(',') if x.strip()]))
    return entries


def load_runtime():
    text = (ROOT / '_lab' / 'patches_now.txt').read_text(encoding='utf-8')
    head = text.split('\n')[0] if text else ''
    # **A patch table that includes other mods is not evidence (round 128).** Until round 127 `patch-inventory` read
    # `Harmony.GetAllPatchedMethods()`, which is global, and truncated its list at 12 000 characters without saying so.
    # The snapshot taken from it (`_lab/patches_now.round119.txt`, header 「已打补丁方法 940 个 / 去重类型 507 个」)
    # therefore contained patches that are not ours at all - and two ledger rows were closed on them:
    # `ThingUtility.CheckAutoRebuildOnDestroyed` and `Game.InitNewGame` are in NO source file of this project and are
    # not in the instance-scoped list; they were somebody else's patch in the same process. That is exactly the failure
    # the header of this file warns about, one level further down: the instrument, not the rule.
    #
    # So the snapshot must declare itself instance-scoped and complete, or this script stops instead of quietly
    # triaging against somebody else's patches.
    if '本实例方法' not in head or '列表完整' not in head:
        sys.stdout.buffer.write(
            ('补丁快照不可用：' + head[:160]
             + chr(10) + '需要 `patch-inventory` 的新格式（含「本实例方法」与「列表完整」）；'
             + '请用容器读数重写 _lab/patches_now.txt，见 _lab/r128_snapshot.py').encode('utf-8') + chr(10).encode())
        raise SystemExit(2)
    body = text.split('\n')[2] if len(text.split('\n')) > 2 else ''
    pairs, by_type = set(), collections.defaultdict(set)
    for m in body.split(';'):
        if '.' in m:
            t, meth = m.rsplit('.', 1)
            pairs.add((t, meth))
            by_type[t].add(meth)
    return pairs, by_type


DESIGNATOR_METHODS = {'DesignateSingleCell', 'CanDesignateCell', 'CanDesignateThing', 'DesignateThing',
                      'DesignateMultiCell', 'Finalize'}
GIZMO_METHODS = {'ProcessInput'}


# 副本**自行计算**的入口。判据有两条代码事实，都可以查：
#   1. 入口是每帧界面回调：`UIRootUpdate` 调 `alerts.AlertsReadoutUpdate()`
#      (`_lab/asm/Assembly-CSharp.decompiled.cs:684247`)，不受 tick 循环控制；
#   2. 时钟被接到权威端：`ReplicaClockPatch` 把 `TickManager.TicksGame` 覆盖成
#      `Barrier.AppliedTick`（`Source/AuthoritativeReplica.cs:2842`），`TicksAbs` 同理（`:2863`）。
# 于是 `Alert_LowFood.GetReport` 里的 `TicksGame < 150000f`（`:611350`）读到的是权威端 tick，
# 而它读的两样东西 —— `map.resourceCounter.TotalHumanEdibleNutrition` 和
# `FreeColonistsSpawnedCount`（`:611360-611367`）—— 都是副本上已经同步过的对象。
# `Alert.Active => cachedActive`（`:608907`），而 `cachedActive` 只由 `Recalculate()` 调 `GetReport()` 写入
# （`:608994-608999`），所以副本会把这个告警算成和权威端一样的结论 —— **不需要传告警列表**。
# 反过来传列表会更差：`GetReport()` 读实时状态，传过来的字符串会在一帧之后就不成立。
RECOMPUTED = re.compile(
    r'^(Alert_|ITab_|MainTabWindow_|.*Readout|.*CardUtility|.*Widgets|StatWorker|ThoughtWorker_|'
    r'CompStatue|ApparelGraphicRecordGetter|CharacterCardUtility|FloatMenuOption\b)')


# ⚠️ 排序纪律（三次踩过，写在这里以免第四次）：`classify` 是**顺序匹配**，第一个命中即返回。
# 任何新加的 `if typ == '具体类名'` 都必须放在宽匹配**之前**，否则规则永远不会执行，
# 而且不会报错 —— 账本看上去"没改"，实际是新规则被吃掉。已经吃掉过三次：
#   ① `CompAssignableToPawn` 被「组件自身字段」的元组匹配；② `Area_Allowed` 被 `Area*` 匹配；
#   ③ `HealthCardUtility` 被 `RECOMPUTED`、`Bill_Medical` 被 `Notify_*` 匹配。
# 宽匹配在下面依次是：`RECOMPUTED`、designator 路由、`Notify_*`、组件族分组。
def classify(typ, methods, pairs, by_type, cat=''):
    # Before everything: a type that does not exist in the game cannot be a game-side gap. Without this the row stays
    # in `-` forever and looks exactly like a real one (see `REFERENCE_ONLY_TYPES`).
    if typ in REFERENCE_ONLY_TYPES:
        return 'x', '参考 mod 自有类型（vanilla 无此类）：不是游戏侧缺口'
    # **第 174 轮：这四个都是世界物件，状态走片段通道。** `Gravship`/`Caravan`/`CaravansBattlefield`/
    # `TravellingTransporters` 全部继承 `WorldObject`（`Gravship` 还带 `IIncidentTarget`），而
    # `WorldObjectLifecycleSync` 的创建片段就是 `ScribeBridge.Save(each)`、`WorldObjectStateSync` 继续比较整份片段
    # （含 `comps[i].PostExposeData()`）⇒ 它们 `ExposeData` 里的标签本来就随片段走，不需要按字段补通道。
    # **第 176 轮：`GravshipLandingMarker` 只服务于权威端的降落确认流程。** 它由 `GenStep` 的 `Generate` 造出来并挂在
    # `parms.gravship` 上（`:409653-409662`），读者是设计器与确认对话框（`:27022`、`:620276`），也就是只在权威端跑的那条流程；
    # 副本真正需要的是 `GravshipGateSync` 送的两个控制器字段（`LandingAreaConfirmationInProgress`/过场，`ExposeData` 已 scribe，
    # `:27152`、`:27164-27171`），那已经在帧里。
    # **第 177 轮：`ShipCountdown` 是结局过场的静态倒计时。** 三个静态字段（`timeLeft` 从 7.2 起、`shipRoot`、
    # `customLaunchString`），`CountingDown => timeLeft >= 0`，`InitiateCountdown` 只做「放起飞音效 + 倒计时 + 进结局」，
    # 结局本身由 `GameEnder` 通道送。副本各自跑一遍自己的倒计时不影响任何游戏状态。
    # **第 178 轮：这四个都是「权威端造出来、结果走已有通道」。**
    # `CaravanEnterMapUtility.Enter` 用 `spawnCellGetter` 把远行队的小人放进地图（结果走小人行与 thing 行）；
    # `CaravanMaker` 造一个 `Caravan` 世界物件（走 `WorldObjectLifecycleSync` 的创建片段）；
    # `CaravanFormingUtility` 是组建流程本身（权威端 job 链）；`LordMaker` 造 lords，帧里已有 `lords` 通道。
    # **第 179 轮：绘制/生成/存档这三类。** `MapDrawer`/`ExpandableWorldObjectsUtility` 只画（每个玩家自己的屏幕）；
    # `WorldGenerator`/`MapFileCompressor` 是世界生成与存档压缩（权威端一次性动作）；`QuestGen` 造的 quest 由 `QuestSync` 送。
    # **第 180 轮：五个 comp 已在 `CompScribeLabels` 白名单里，两行是存档/渲染基础件。**
    # 白名单是标签驱动的：命中即表示该 comp 的 `PostExposeData` 片段随建筑行/thing 行送出。
    # **第 181 轮：造东西的人、一次性流程、面板点击。** 这些都是「权威端跑一遍、结果走已有通道」或每端界面：
    # `ThingMaker`/`DropPodUtility`/`MinifyUtility`/`CaravanInventoryUtility` 造出或搬运的物件走 thing 行；
    # `Blueprint.TryReplaceWithSolidThing` 在建造完成时把蓝图换成实物（实物走 thing 行）；
    # `ScenPart_ScatterThings` 建局时撒东西；`MemoryUtility.ClearAllMapsAndWorld` 是换局/重生的清场；
    # `PawnColumnWorker_Faction.ClickedIcon` 是列面板点击；`RaidStrategyWorker.MakeLords` 造的 lord 走帧里的 lords 通道。
    # **第 182 轮：每端自己的画面与输入。** 这些类只在本地跑：绘制（`CellBoolDrawer`/`ColonistBarColonistDrawer`/
    # `FleckManager`/`Mote`/`GenView`/`JitterHandler`/`WorldRenderer`/`WorldTargeter`）、镜头（`CameraDriver`/
    # `CameraJumper`/`CameraShaker`/`SubEffecter_CameraShake`）、选择与浮菜单（`Selector`/`FloatMenu*`）、窗口（`Window`）、
    # 历史页（`ArchivedDialog`）、以及 `DebugLoadIDsSavingErrorsChecker`/`GameDataSaveLoader` 这类存档工具。
    # **第 183 轮：四个对话框背后的动作都已经有承接层。** `Dialog_ConfigureIdeo` 的写入落在 ideo 字段上（`IdeoSync`）；
    # `Dialog_SplitCaravan` 产出的是新的 `Caravan`（`WorldObjectLifecycleSync` 创建片段）；`Dialog_EnterPortal` 有
    # `EnterPortal` 这条共享动作（`Source/SharedActions.cs` 命中）；`Dialog_ChooseThingsForNewColony` 产出新地图/殖民地
    # （`MapLifecycleSync`）。对话框本身只是每个玩家自己的界面。
    if typ in ('Dialog_ConfigureIdeo', 'Dialog_SplitCaravan', 'Dialog_EnterPortal', 'Dialog_ChooseThingsForNewColony'):
        return 'x', '对话框是每端界面，动作落点各有通道：ideo 字段（IdeoSync）/ 新 Caravan（世界物件片段）/ EnterPortal 共享动作 / 新地图（MapLifecycleSync）'
    # **第 183 轮：交易对话框判 `~`。** `Source/SharedActions.cs` 里没有「执行一笔交易」的作者命令（grep `TradeDeal`/
    # `ExecuteTrade` 为 0），只有外交派系的命令；副本上点成交会发生什么（本地扣银/改库存后是否被 thing 行改写）
    # 没有实测过。这一行留成不确定，比按名字判 `x` 诚实。
    # **第 184 轮：这一行是 `Close`/`DoWindowContents`/`FillMainRect` 三个方法，都是界面。** 第 183 轮把它判 `~` 是因为
    # 「交易能不能在副本上成交」这个问题没答案——但那是**另一件事**，不是这一行。全 dump 里 `TradeUtility.Player*` 只有
    # 两处（一处 `PlayerSellableNow` 的可用性判断），说明成交路径不在这个对话框的这三个方法里；
    # 成交路径本身记进 `findings.md` 作为独立待办。
    if typ == 'Dialog_Trade':
        return 'x', '界面（Close/DoWindowContents/FillMainRect）；「副本上成交」是另一件事，已记入 findings.md 待办'

    if typ in ('PawnRenderTree', 'PawnTweener', 'PawnLeaner', 'Pawn_RotationTracker', 'PostTickVisuals', 'PortraitsCache', 'ScreenFader', 'UIRoot_Play', 'Targeter', 'TilePicker', 'WorldSelector', 'UI'):
        return 'x', '每端渲染/输入：动画插值、镜头目标选择、屏幕淡入淡出、UI 根节点；同一帧数据各端各画一遍（第 185 轮逐族判定，名字逐个列出）'
    if typ in ('SoundStarter', 'Sustainer', 'MusicManagerPlay', 'SteamManager'):
        return 'x', '每端音频与平台层：音效/持续音/音乐调度与 Steam 状态，都不进模拟（第 185 轮逐族判定，名字逐个列出）'
    if typ in ('Need', 'Need_Chemical_Any', 'PawnNeedsUIUtility', 'TransferableUIUtility', 'Page_ConfigureStartingPawns'):
        return 'x', '每端界面：需求条绘制与分组、交易面板列、建局页；需求值本身走 NeedsSync（第 185 轮逐族判定，名字逐个列出）'
    if typ in ('ScribeExtractor', 'ScribeLoader', 'ScribeSaver', 'Scribe_References', 'PermadeathModeUtility'):
        return 'x', '存档管线与读档诊断（Scribe 读写器、悬空引用检查、永久死亡改名检查）；权威端读档，不参与同步（第 185 轮逐族判定，名字逐个列出）'
    if typ in ('Scenario', 'ScenPart_PlayerPawnsArriveMethod', 'StartingPawnUtility', 'VerbTracker'):
        return 'x', '建局/开局小人生成（权威端一次性）与 VerbTracker 的运行时重建（`InitVerbsFromZero`；verb 状态本来不 scribe）（第 185 轮逐族判定，名字逐个列出）'
    if typ in ('CellBoolDrawer', 'ColonistBarColonistDrawer', 'FleckManager', 'Mote', 'GenView', 'JitterHandler',
               'WorldRenderer', 'WorldTargeter', 'CameraDriver', 'CameraJumper', 'CameraShaker', 'SubEffecter_CameraShake',
               'Selector', 'FloatMenuMakerWorld', 'FloatMenuOptionProvider', 'FloatMenuOptionProvider_DraftedAttack',
               'FloatMenuOptionProvider_RescuePawn', 'Window', 'ArchivedDialog', 'DebugLoadIDsSavingErrorsChecker',
               'GameDataSaveLoader'):
        return 'x', '每端自己的画面/输入/存档工具：绘制、镜头、浮菜单、窗口、历史页，不进模拟状态'
    # **第 182 轮：这三个是真状态，只是门在别处。** `TransportShipMaker` 造的是 `TransportShip`（WorldObject ⇒ 片段通道）；
    # `GameConditionMaker` 造的 condition 走帧里的 `Conditions` 通道；`AutoBuildRoofAreaSetter.TryGenerateAreaNow` 是
    # `TryGenerateAreaFor` 的调用方（后者第 153 轮已被 `SettlementAutomationGate` 按据点归属把关）。
    if typ in ('TransportShipMaker', 'GameConditionMaker', 'AutoBuildRoofAreaSetter'):
        return 'x', ('产物各有通道：TransportShip 是 WorldObject（片段通道）、condition 走 Conditions 通道；'
                     'TryGenerateAreaNow 的判定在 TryGenerateAreaFor（SettlementAutomationGate 已把关）')
    if typ in ('ThingMaker', 'DropPodUtility', 'MinifyUtility', 'CaravanInventoryUtility', 'Blueprint',
               'ScenPart_ScatterThings', 'MemoryUtility', 'PawnColumnWorker_Faction', 'RaidStrategyWorker'):
        return 'x', ('权威端一次性动作或每端界面：产物（thing/pawn/lord）各自走 thing 行、lords 通道或建局流程；'
                     '这一类「造东西的人」本身不需要通道')
    if typ in ('CompAncientHeatVent', 'CompBiosculpterPod', 'CompGoldenCube', 'CompPowerPlantWater', 'CompVoidNode'):
        return 'x', ('CompScribeLabels 白名单命中：该 comp 的 PostExposeData 片段随建筑/thing 行送出')
    if typ in ('BackCompatibility', 'CompressibilityDecider', 'SectionLayer_TerrainScatter'):
        return 'x', ('存档兼容/压缩判定/地表散布渲染：权威端读档与每端渲染基础件，不是可玩状态')
    if typ in ('MapDrawer', 'ExpandableWorldObjectsUtility', 'WorldGenerator', 'MapFileCompressor', 'QuestGen'):
        return 'x', ('绘制（每端自己的屏幕）/ 世界生成与存档压缩（权威端一次性）/ QuestGen 的产物走 QuestSync')
    if typ in ('CaravanEnterMapUtility', 'CaravanMaker', 'CaravanFormingUtility', 'LordMaker'):
        return 'x', ('权威端造、结果走已有通道：远行队进入地图 spawn 的小人走小人/thing 行；Caravan 是 WorldObject '
                     '（WorldObjectLifecycleSync 片段）；组建流程是权威端 job 链；LordMaker 的 lord 走帧里的 lords 通道')
    if typ == 'ShipCountdown':
        return 'x', ('结局过场的静态倒计时（timeLeft/shipRoot/customLaunchString；`CountingDown => timeLeft >= 0`）；'
                     '结局判定由 GameEnder 通道走，副本各自倒计时只影响自己屏幕')
    if typ == 'GravshipLandingMarker':
        return 'x', ('GravshipGateSync（`:27152`/`:27164-27171` 的两个控制器字段）+ 标记本身由 GenStep 在权威端生成、读者是设计器；'
                     '副本需要的状态已在帧里')
    if typ in ('Gravship', 'Caravan', 'CaravansBattlefield', 'TravellingTransporters'):
        return 'x', ('世界物件片段通道（WorldObjectLifecycleSync/WorldObjectStateSync）：类型继承 WorldObject，'
                     '整份 ExposeData 片段随创建与变更走；`CaravanGuardPatch` 只决定谁能组建远行队')
    # **`PlaySettings` 是通道的源头，不是某个界面方法。** 这一行的方法名是那块小面板，但同一份对象里的
    # `useWorkPriorities`/`autoHomeArea`/`autoRebuild` 与十一个 `defaultCareFor*` 都由 `PlaySettingsSync` 送出去；
    # 按方法名把它归进「绘制/相机/输入」会把一个真通道读成界面（第 173 轮实测 `prisoner 3` 四段全准）。
    if typ == 'PlaySettings':
        return 'x', ('PlaySettingsSync（第 55 条通道 + 第 173 轮十一个 defaultCareFor*）；面板方法本身是 UI，状态走这一行')
    hit = [m for m in methods if (typ, m) in pairs]
    if hit:
        return 'x', '命名补丁:' + ','.join(hit[:2])
    # **Model differences first, and by exact (section, type, method).** These are the rows where CT patches something
    # because it needs a per-thing owner table that this project does not have; see `MODEL_DIFFERENCES` for the reasoning
    # per row. Tested before every regex rule on purpose: a name rule cannot tell a runtime factory from a save/load entry
    # point, which is how `MakeDropPodAt` ended up labelled 「世界生成/存档装载」 while ColonyTogether patches it at
    # `ct045:324140`.
    for method in methods:
        key = (cat, typ, method)
        if key in MODEL_DIFFERENCES:
            return '-', MODEL_DIFFERENCES[key]
    # **`Dialog_*` 不等于纯界面（第 119 轮）。** 这两个对话框的名字里都有 `Dialog`，所以先被绘制正则抓走；但它们的
    # `TryAccept` 是**写操作**：`Dialog_EnterPortal.TryAccept`（`:633219-633229`）清空并重建 `MapPortal.leftToLoad`
    # （`:571789`）再逐项 `portal.AddToTheToLoadList`（`:571926`）；`Dialog_LoadTransporters.TryAccept`（`:636030`）
    # 走 `CompTransporter.AddToTheToLoadList`（`:561716`）并 `MakeLordsAsAppropriate` 建领主。
    #
    # 关键的一步是**同名不同类**：`MapPortal.AddToTheToLoadList` 与 `CompTransporter.AddToTheToLoadList` 只共享拼写，
    # 原补丁挂在后者，所以装填传送门 100% 从未被捕获 —— 而账本把它记为「绘制/相机/输入」，看起来像是永远不需要同步。
    # 现在两条路径各有自己的补丁：`TransportCapturePatch` / `PortalCapturePatch`（后者还带 `PortalCancelCapturePatch`，
    # 因为 `MapPortal.CancelLoad` `:571985` 移除领主并清空清单，是同一对象上的第二个写操作）。
    # **`FactionDialogMaker` 的三个请求按钮（第 120 轮）** —— 名字里没有 `Dialog`，但整个类型先被
    # 「绘制/相机/输入」的 `Dialog` 正则抓走（`FactionDialogMaker` 含 `Dialog`），所以三个**真写世界状态**的
    # 方法一直记着 `-`：
    #   · `RequestTraderOption`（`:486880`）内层选项动作 `:486920-486932`：`incidentQueue.Add(TraderCaravanArrival,
    #     TicksGame + 120000, parms, 240000)` + `faction.lastTraderRequestTick = TicksGame` + goodwill -15。
    #   · `RequestOrbitalTraderOption`（`:486817`）：`:486865` 排队（1-2 小时随机，`OrbitalTraderDelayHours`
    #     `:486625`）+ `lastOrbitalTraderRequestTick`（`:486866`）+ goodwill -30。
    #   · `CallForAid`（`:487119-487128`）：goodwill -25、`raidArrivalModeForQuickMilitaryAid`、`points`
    #     随机、`lastMilitaryAidRequestTick = TicksGame`（`:487127`）、然后 `RaidFriendly.Worker.TryExecute`。
    #
    # 三个写点现在各有归属，所以是 `x` 而不是 `-`：
    #   · 队列 → 第 120 轮的 `IncidentQueueCapturePatch`（`IncidentQueue.Add` `:384081`，三条路都经过它）；
    #   · 冷戳 → 第 120 轮的 `FactionSync.PublishCooldowns`/`ApplyCooldown`（字段声明 `:485260-485264`，
    #     scribe `:485508-485510`，读取点 `:486895`/`:486826`/`:486959`）；
    #   · goodwill → 第 55 条通道 `FactionSync`（全表重算 + 差异发送，本就覆盖任何写手）。
    #
    # 这里也有一次**同名不同类**：`DiaOption.action` 只由 `Activate()`（`:195621-195634`）在
    # `Widgets.ButtonText` 返回真时调用（`:195614`），所以副本不会自发执行；而
    # `RequestTraderOption` 的 `__result` 是外层容器 `diaOption4`，只有 `link` 字段（`:486940`），真正写队列的
    # `action` 属于 `foreach` 里逐个商人种类新建的内层 `DiaOption`（`:486909-486935`）——
    # 第一版把补丁打在 `__result.action` 上，编译通过、永远包到 null。捕获点因此下移到 `IncidentQueue.Add`。
    # **第 122 轮：`Blueprint_Build.MakeSolidThing` 不是「副本不跑这条路径」那么简单。**
    # 上一版把它记成「权威端 tick 或建造 job 驱动」，结论本身对（副本确实不跑建造 job），但它绕过了真正的缺口：
    # `MakeSolidThing`（`:570393-570398`）把 `StyleDef` / `glowerColorOverride` 从蓝图抄进 `Frame`，而这两个字段在
    # **蓝图刚被放置时**就已经错了 —— `Designator_Build.PlaceThing` 在 `GenConstruct.PlaceBlueprintForBuild`
    # （`:622503`）**之后**才写它们（`:622504`、`:622507`、`:622509`），而它们读的是**本地设计器实例**的三个字段
    # （`:621995`、`:621999`）——设计器是每端独立的界面对象，没按过换样式按钮的那一端三个字段全是 null。
    # 蓝图又不在任何一张补扫里：`IsBuildingArtificial` 是 `category == Building || IsFrame`（`:46179`），蓝图两者都不是。
    # 所以风格化的建筑（意识形态风格、玩家改过 glower 颜色的灯）在每一端都是「素」的，而且永久素下去。
    # 现在由设计器命令的第 5、6 段承载（`DesignatorSync`），重放时赋给重建出来的设计器，让**原版自己的两个写入点**跑
    # ——这也覆盖了那条根本不造蓝图、直接 spawn 建筑的分支（`:622479-622497`）。
    # 实测 `build-style-probe`：`Autodoor` + 任意样式样本 `PrestigeMarineHelmet_Samurai`（对自动门并不适用）时，
    # 原版直放与通道重放都得到 `样式=Techist_Autodoor glower=17,203,91,255`，两者一致 → 准。
    # **这个读数为什么算证明**：读的不是「样式写进去了」，而是「通道与原版对同一份输入给出同一结果」，
    # 所以它同时覆盖了原版把不适用样式解析成适用样式的那个过滤器。
    if typ == 'Blueprint_Build':
        return 'x', ('设计器命令第 5/6 段携带样式与 glower 颜色（第 122 轮）；MakeSolidThing 只是把它们抄进 Frame，'
                     '错在放置时。实测原版直放与通道重放结果一致')
    # `Building_GravEngine` 的 `pawnsToBoard` / `pawnsToLeave` 是**已覆盖**的，虽然看起来像缺口：
    # 它们是 `Building` 上的 scribed 字段（`:582905-582906`），而建筑轮转补扫会 `ScribeBridge.Save` 每个
    # `BuildingArtificial`，所以登船名单随建筑 XML 到达。`Dialog_BeginGravshipLaunch.Start` 里的三个勾选框
    # （`:626140-626142`）只喂 `RitualBehaviorWorker_GravshipLaunch.TryExecuteOn`（`:504740-504768`），
    # 而仪式是权威端才有的领主 —— 副本不需要它们来复现任何可见结果。
    # 另一条通用规则见下面 `Building` 那一支。
    if typ in ('Building_GravEngine', 'RitualBehaviorWorker_GravshipLaunch', 'Dialog_BeginGravshipLaunch'):
        return 'x', ('登船/离船名单是 Building 上的 scribed 字段，随建筑轮转补扫到达；仪式勾选框只喂权威端的仪式执行')
    # **通用规则：`Building` 上的 scribed 字段都由建筑补扫承载。** 这条比逐条记名字重要：
    # 补扫对每个 `BuildingArtificial` 跑 `ScribeBridge.Save` 并比较 XML，所以任何 scribe 过的建筑字段不需要专门通道。
    # 需要通道的是三类：(a) 不是 Building/物品/植物/污物的载体（蓝图就是这一类），(b) `[Unsaved]` 派生字段（读时重算即可），
    # (c) 玩家输入 —— 命令。
    # **「右键菜单」不等于纯界面（第 127 轮）。** 这两行都被绘制正则抓走：`FloatMenuOptionProvider_DraftedMove`
    # 因为名字里有 `FloatMenu`，`MultiPawnGotoController.FinalizeInteraction` 因为被显式列进了那条正则的
    # 「实测这 13 个是渲染/输入」名单。两行其实是**同一条玩家指令**的两个入口：
    #
    #   * `PawnGotoAction`（`:657742`）末行是 `pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)`（`:657768`），
    #     而 `OrderCapturePatch` 就挂在 `TryTakeOrderedJob` 上 —— 所以「去这里」的 job 早就有载体；
    #   * `MultiPawnGotoController.IssueGotoJobs`（`:668758`）对每只选中的小人调同一个 `PawnGotoAction`，
    #     多选拖拽因此自动跟着走；
    #   * **但** `PawnGotoAction` 有一条更早的分支永远到不了那个方法：右键自己脚下那一格、且当前 job 是
    #     `Goto` 时，它调 `EndCurrentJob(JobCondition.Succeeded)`（`:657750`）—— 「停在这儿」。这条一直没人捕获，
    #     第 127 轮加了 `stop` 命令 + `GotoStopCapturePatch`。
    #
    # 判据仍然是「效果落在哪」，不是「名字里有没有 FloatMenu」。
    if typ in ('FloatMenuOptionProvider_DraftedMove', 'MultiPawnGotoController'):
        return 'x', ('去这里的 job 走 TryTakeOrderedJob（:657768）→ OrderCapturePatch；'
                     '多选拖拽经 IssueGotoJobs（:668758）走同一入口；'
                     '右键自己脚下的取消走 stop 命令 + GotoStopCapturePatch（第 127 轮）')

    # **Two rows were closed on other mods' patches (round 128).** Both claimed `命名补丁` and both were false: neither
    # method exists anywhere in this project. `ThingUtility.CheckAutoRebuildOnDestroyed` (`:160060`) and
    # `CheckAutoRebuildTerrainOnDestroyed` (`:160069`) decide whether a blueprint is placed after a building or terrain
    # is destroyed; the write they perform is `GenConstruct.PlaceBlueprintForBuild`, which is the blueprint channel, and
    # the toggle they read (`Find.PlaySettings.autoRebuild`) travels on the `PlaySettingsSync` row. `Game.InitNewGame`
    # and `Game.LoadGame` are creation/load entry points - there is nothing to synchronise in "start a new game", and
    # `DeinitAndRemoveMap` is map teardown, which `MapLifecycleSync` owns.
    # **自动屠宰的限额是玩家输入，读点在真正杀动物的那一端（第 129 轮）。** `AutoSlaughterManager.configs`（`:64706`）
    # 由自动屠宰对话框的数字格编辑，改完只调 `Notify_ConfigChanged`（`:625940-625943`）脏一个缓存；读它的
    # `AnimalsToSlaughter`（`:64712`）被权威端变成屠宰单。所以在副本上改的限额到不了会执行的那一端 ——
    # 动物继续繁殖超过玩家以为设好的上限，而且没有任何地方报告分歧。
    # 第 129 轮加 `AutoSlaughterSync`（按 地图+物种 精确键 diff，应用时 `Notify_ConfigChanged`），实测
    # `autoslaughter-probe`：`maxTotal 原 -1 → 设为 7，发出 1 行（编码 7）→ 编码准；改回 -1（不发）再应用 → 读回 7 → 准`。
    if typ == 'AutoSlaughterManager':
        return 'x', ('configs 随 AutoSlaughterSync 行（地图+物种精确键；应用后 Notify_ConfigChanged）；'
                     '实测 autoslaughter-probe 编码准、往返准')

    # **账本理由里写的通道名必须真的存在（第 130 轮）。** 这一族行的判据是「状态通道:XxxSync」，但五个名字在本项目里
    # 一个都不存在 —— 它们是凭记忆写的类名。覆盖是真有的，只是名字写错了；这和第 128 轮「借别的 mod 的补丁」是同一层：
    # **证据的名字不可核验**。下面写成真实载体，并且不再复述那些错名字（复述会让自查脚本再次命中它们）。
    #
    # 真实载体：hediff 是 `AuthoritativeReplica.ApplyHediffs` + `ReplicaHediffSet`（`AuthoritativeReplica.cs:2734`）
    # 加 `HediffTimerSync`；装备/衣物是 `ApparelSync`；想法老化是 `MemorySync`（`Thought_Memory.ThoughtInterval`）；
    # 研究写操作是 `ResearchCapturePatch`（`SharedActions.cs:2621` 打 `ResearchManager.SetCurrentProject`）。
    if typ == 'MemoryUtility':
        # Not "CT needs this because it is P2P" (the old reason): `ClearAllMapsAndWorld` (`:237689`) is world
        # teardown - it nulls every class-typed field of every `Map`, clears `Find.Maps` and sets
        # `currentMapIndex = -1`. Only three call sites reach it (`:53147` new game, `:145001` load, `:650711`
        # quit), all inside `Game`'s own lifecycle, which this project replaces with map create/remove
        # channels (`MapLifecycleSync`). A replica never runs a teardown of someone else's world.
        return '-', '世界拆除路径（ClearAllMapsAndWorld `:237689`，三个调用点都是 Game 生命周期），由 MapLifecycleSync 的建图/删图通道替代'
    if typ in ('Hediff', 'Hediff_DuplicateSickness', 'Hediff_Pregnant'):
        return 'x', '状态通道:ApplyHediffs（ReplicaHediffSet 整份片段）+ HediffTimerSync；第 130 轮改正类名'
    if typ in ('Pawn_ApparelTracker', 'Pawn_EquipmentTracker'):
        return 'x', '状态通道:ApparelSync（逐件 def+stuff+quality 重建显示对象）；第 130 轮改正类名'
    if typ in ('MemoryThoughtHandler', 'SituationalThoughtHandler'):
        return 'x', '状态通道:MemorySync（Thought_Memory 老化，ShouldDiscard 的输入）；第 130 轮改正类名'
    if typ == 'MainTabWindow_Research':
        return 'x', '写操作只有 SetCurrentProject，由 ResearchCapturePatch（SharedActions.cs:2621）覆盖；第 130 轮改正类名'

    if typ == 'ThingUtility':
        return 'x', ('自动重建的判定读 Find.PlaySettings.autoRebuild（随 PlaySettingsSync 行）；'
                     '它落地的那次写是 GenConstruct.PlaceBlueprintForBuild，走 blueprint 通道。'
                     '（旧账本误记为命名补丁 CheckAutoRebuildOnDestroyed —— 那是别的 mod 的补丁）')
    if typ == 'Game':
        return 'x', ('InitNewGame/LoadGame 是建局与读档入口，没有需要同步的运行期状态；'
                     'DeinitAndRemoveMap 是地图拆除，由 MapLifecycleSync 负责。'
                     '（旧账本误记为命名补丁 InitNewGame —— 那是别的 mod 的补丁）')

    if typ == 'GameComponent_Bossgroup':
        # Round 140: the row was closed on `killedBosses` - "its only reader is the DebugTables dev table" - which is
        # true of that field and blind to the one beside it. `lastBossgroupCalled` is read by
        # `Command_CallBossgroup.IsDisabled` (`:3184-3199`), which runs from `GizmoOnGUI`, so **every peer draws its own
        # answer**, and the summon action has no cooldown check of its own (the gizmo is the whole cooldown). Measured:
        # `bossgroup-probe` round-trips the stamp (`-9999999` sentinel → set → 1 row encoded → apply reads it back);
        # `gizmo-hooks` now attaches 14/14 entries, the summon ones included.
        return 'x', ('BossgroupSync 承载 `lastBossgroupCalled`（召唤冷却戳，`-9999999` 是未召唤哨兵值）；'
                     '`Command_CallBossgroup.IsDisabled` 在 GizmoOnGUI 里读它，而召唤动作自己没有冷却检查 —— '
                     '副本上一行不同步就等于冷却不存在；`killedBosses`/`timesCalledBossgroups` 确实只被开发表读到，'
                     '所以只发那一个字段。（旧理由只查了 killedBosses 就关掉了整行）')

    if typ == 'FactionDialogMaker':
        return 'x', ('IncidentQueueCapturePatch（队列）+ FactionSync 冷却三字段 + FactionSync 好感度；'
                     '`__result.action` 是空容器，捕获点必须下移到 IncidentQueue.Add')

    if typ == 'Caravan_PathFollower':
        # Round 137: this row used to claim `状态通道:ReplicaCaravan`, and there is no such type, record or field
        # anywhere in the project - the claim was written from the shape the row *should* have had. The real carrier is
        # a postfix on the `WorldObject.Tile` setter (`WorldObjectSync.cs:134`, `nameof(WorldObject.Tile),
        # MethodType.Setter`), which every mover goes through: `Caravan_PathFollower`'s next-tile step, a settlement
        # spawning, a gravship landing. `WorldObjectSync` re-reads the property rather than the argument because the
        # setter is `set_Tile(PlanetTile value)`.
        return 'x', ('WorldObjectSync：`WorldObject.Tile` setter 的 Postfix（`WorldObjectSync.cs:134`）覆盖所有移动者'
                     '（`Caravan_PathFollower` 的下一格、据点生成、重力船落地）；按 `obj.Tile` 重建位置，'
                     '路径推进本身在副本不跑，重建位置即可')


        return 'x', 'PortalCapturePatch + PortalCancelCapturePatch（portal 命令；Load/Cancel 两个补丁点）'
    if typ == 'Dialog_LoadTransporters':
        return 'x', 'TransportCapturePatch（transport 命令）；建领主那半靠 LordSync.MissingLords 可见'
    # 界面页自己写全局设置的，必须排在 `RECOMPUTED` 之前 —— 它们长得像"界面输入"，而写的是仿真输入。
    # `MainTabWindow_Work.DoManualPrioritiesCheckbox`（`:675248-675265`）写
    # `Current.Game.playSettings.useWorkPriorities`，而这个字段是 `Pawn_WorkSettings.GetPriority`
    # （`:331069-331077`）的乘法系数：它为 false 时任何非零优先级都返回 3，**决定每个小人干什么活、按什么顺序**。
    # 已建第 55 条通道 `PlaySettingsSync`；实测 `settings-probe`：`useWorkPriorities=关…优先级被压成单一 3 的 4 个`。
    if typ == 'MainTabWindow_Work':
        return 'x', 'PlaySettingsSync（第 55 条通道送 useWorkPriorities/autoHomeArea/autoRebuild）'
    # 研究页看起来同类，但它的写操作 `DoBeginResearch`（`:674203-674212`）只调
    # `ResearchManager.SetCurrentProject`，已被 `ResearchManagerSetCurrentProjectPatch`
    # （`Source/SharedActions.cs:1874-1877`）覆盖；其余是纯查询。
    if typ == 'MainTabWindow_Research':
        return 'x', '写操作是 SetCurrentProject，已由 ResearchManagerSetCurrentProjectPatch 覆盖（其余是纯查询）'
    # 手术账单（`HealthCardUtility.CreateSurgeryBill` `:644616-644623`）—— 这是本轮找到的真缺陷，不是"副本自行计算"。
    #   顺序是关键：`new Bill_Medical(...)` → `BillStack.AddBill(...)` → `bill.Part = part`，而 `Part` 的 setter
    #   **在账单还没进账单栈时拒绝赋值**（`:442400-442402`：`Log.Error("Can only set Bill_Medical.Part after the
    #   bill has been added to a pawn's bill stack.")`）。`bill-add` 是 `AddBill` 上的前缀且会取消本地调用，
    #   所以在**发起端**：`billStack` 仍是 null → 部位没被赋值 → 每张手术账单都写一条红字，且副本上的账单没有部位；
    #   而 `CompletableEver`（`:442369-442373`）因为 `GetPartsToApplyOn` 不可能包含 null 部位而返回假 —— 手术在
    #   对端什么也不做。现在由 `surgery-bill` 整份下发（`Bill_Medical.ExposeData` `:442541-442565` 正好 scribe
    #   part/consumedMedicine/uniqueRequiredIngredients/iterationCompleted），补丁打在 `CreateSurgeryBill` 的
    #   **postfix** 上，那是部位一定已赋值的最早时刻；`BillAddCapturePatch` 对 `Bill_Medical` 直接放行。
    # `Pawn_GuestTracker` 的第四字段：`HostFaction` 由**唯一一处** `SetGuestStatus`（`:461540-461541`）写入，
    # 而 `GuestSync` 直接写 `guestStatusInt`，所以在加上第四段之前副本上的 `HostFaction` 永远停在载入存档那一刻。
    # 它正是 `ITab_Pawn_Health.ShouldAllowOperations`（`:667161-667163`）用来决定「能不能给倒地的客人做手术」的字段，
    # 所以副本会在一个主家早就变了的囚犯身上给出或收回手术许可。现在随 `status#resistance#will#hostLoadId` 下发，
    # 三段的旧载荷按「无主家」处理，混合版本房间仍然同步前三个字段。
    # 载体：本存档 `囚犯/奴隶 0（有主家派系 0）` —— 通道已建，实测待囚犯。
    if typ in ('Pawn_GuestTracker', 'GuestUtility'):
        # 第 108 轮：`Recruitable` 补进第五段。它决定的是**规则**而不是数字——`SetupRecruitable`（`:461474`）
        # 按 `NonRecruitableChanceOverPopulationIntentCurve` 摇一次，然后三个读取点都在界面上：
        # 招募按钮与抵抗度读数（`:666680`）、交互模式是否隐藏（`:666873` 的 `mode.hideIfNotRecruitable`）、
        # 右键「捕获」是否标注 Unrecruitable（`:656581`）。副本缺它就会提供一个权威端招募不了的人。
        # `CapturedBy`（`:461572`）/ `SetGuestStatus`（`:461454`）写的是 `hostFactionInt`、`guestStatusInt`、
        # `resistance`、`will` 与 `recruitable` 这五个字段，全部在载荷里；剩下的副作用
        # （`TaleRecorder.RecordTale`、`records.Increment`、来信）归 TaleSync / 权威端。
        # `Notify_PawnUndowned`（`:461617`）的 `leftAfterRescue` 与 `getRescuedThoughtOnUndownedBecauseOfPlayer`
        # 只有它自己读写（各 4 处），属于一次性事件状态，不是持续展示值。
        return 'x', ('GuestSync 五段：status#resistance#will#hostLoadId#recruitable（第 108 轮补第五段）；'
                     '三个 Recruitable 读取点都是界面规则，副作用归权威端')

    # `Xenogerm.SetTargetPawn`（`:596888-596897`）—— 同一通电话里的**第二个**顺序陷阱。
    #   回调按顺序做四件事：删掉旧目标上 `xenogerm == this` 的账单 → `CreateSurgeryBill(newTarget, ImplantXenogerm,
    #   null).xenogerm = this` → `targetPawn = newTarget` → `SendImplantationLetter(newTarget)`。
    #   第 57 轮的捕获打在 `CreateSurgeryBill` 的 postfix 上，而那时 `bill.xenogerm` **还是 null**；这个字段由
    #   `Bill.ExposeData` 以引用 scribe（`:272315`），所以片段带的是 null，接收端的
    #   `SurgeryOutcomeComp_XenogermComplexity`（`:722890-722894`）读到 null 就返回 0 —— 整张植入账单失去意义。
    #   现在捕获点移到 `SendImplantationLetter` 的 postfix（回调的最后一句，此时 bill/part/xenogerm/targetPawn 都
    #   已就位）。旧目标上那张账单的删除由载荷的 `replaces` 下标表达，删除在 add 之前执行。
    #   载体：本图 0 个异种胚，实测待载体。
    # `Dialog_CreateXenogerm.StartAssembly`（`:630288-630293`）—— 对话框按钮写入选中建筑，**不是 gizmo**。
    #   `GizmoStatePatch` 只钩 `Command.ProcessInput`（`:2205-2233`），所以这条路整个 mod 没看见过。
    #   写入内容是 `Building_GeneAssembler.Start`（`:581172-581181`）：`genepacksToRecombine`/`architesRequired`/
    #   `xenotypeName`/`iconDef`/`workingInt`/`totalWorkRequired` —— 每一个都由 `ExposeData`（`:581416-581430`）
    #   scribe，所以**建筑自己的片段就是全部状态，不需要新通道，只需要捕获点**。
    #   做法：复用 `CaptureGizmoTargets`/`FlushGizmoTargets`（已处理蓝图/框架跳过、zone、超长拒绝、
    #   `thing-state` 发布、以及等有序 tick 才放行本地世界），没有第二次实现。
    #   **第 110 轮：载体有了，入口与写入都实测过。** `transport-fixture GeneAssembler` 放了一台
    #   （`Building_GeneAssembler` 是真实 thingClass，comps 为 CompForbiddable/CompPowerTrader/CompFlickable/
    #   CompAffectedByFacilities）。`xenogerm-probe` 直接调 `Start([],0,"turbotick-probe",Basic)` —— 与
    #   `Dialog_CreateXenogerm.StartAssembly`（`:630289`）同一句 —— 实测：
    #   `片段 473→611 B；调用 Start 后变化 4 个：xenotypeName 无→turbotick-probe；iconDef 无→Basic；
    #    workingInt 无→True；totalWorkRequired 无→7500`。
    #   所以「建筑自己的片段携带全部状态」不再是推断，是读数；剩下的只是**延迟**（轮转补扫每帧 24 栋，
    #   42k 物的图上要若干帧）而不是缺失。
    #   复原用私有的 `Reset()`（`:581285`）—— 第一版用 `Start([],0,null,Basic)` 复原，而 `Start` 自己先调
    #   `Reset()` 再把 `workingInt` 置 true（`:581174`/`:581179`），等于没复原，实测留下 `workingInt=True`。
    #   `Reset` 不清 `totalWorkRequired`/`iconDef`，所以复原后仍与探测前有字段差 —— 这是 `Reset` 的边界，
    #   探针现在把这两个字段单独列出来，免得读成「复原失败」。
    # 注意只标**入口**，不标写入点：`Building_GeneAssembler` 由建筑补扫覆盖（`buildSweep` 收
    # `ThingRequestGroup.BuildingArtificial` 里每一栋，`HeadlessRuntime.cs:783-785`），`Start` 写完之后
    # 下一轮扫描自然会把整份片段发出去 —— 它原来判 `x` 是对的。
    # `Dialog_CreateXenogerm` 是入口：`GizmoStatePatch` 只钩 `Command.ProcessInput`（`:2205-2233`），
    # 对话框按钮走不到它，所以加了 `GeneAssemblerStartCapturePatch` 复用 gizmo 快照对；
    # 但入口必须在客户端被真的点过才能证明，本图 0 台装配器 ⇒ 归 `~`。
    # `StatWorker.GetValue`（`:715713-715726`）原本记的是「副本自行计算」，但那句话的前提是**输入都在**。
    # 逐条核 `GetValueUnfinalized`（`:715738-715880`）：skills ✓、health.capacities ✓、hediffs ✓，
    # 而 **traits / Ideo precepts / genes / apparel / equipment 五类都没有通道**。
    # 其中 traits 最要紧：`Pawn_StoryTracker.DisabledWorkTagsBackstoryTraitsAndGenes`（`:127936`）用它算禁工标签，
    # 决定小人**能不能干某类工作**；`GetValueUnfinalized` 又逐条加 `OffsetOfStat`，工作速度/市场价/美观/保温全偏。
    # 已建 `PawnTraitSync`（第 61 条）：`defName#degree#suppressed` 整表发，重建走 `GainTrait`/`RemoveTrait`
    # 而不是清空重填（`GainTrait` `:483949-484014` 还发 abilities、脏化想法、加/删需求、清冥想缓存、
    # 刷攻击目标缓存，而它调的 `RecacheTraits` 是 private）。
    # `Pawn_GeneTracker` —— 基因列表只在载入存档时到过副本一次（`Pawn.ExposeData` 的
    # `Scribe_Deep.Look(ref genes, "genes", this)` `:131426`），之后异种胚植入完成、`GeneGizmo_RemoveXenogene`、
    # 基因库输血、开发者操作全都看不见。`StatWorker.GetValueUnfinalized`（`:715798-715813`）对每个 `Active` 基因加
    # `gene.def.statOffsets`；`TraitSet.RecalculateSuppression`（`:484101-484134`）又把 `gene.def.suppressedTraits`
    # 匹配到 `traits.allTraits`，所以基因列表错了，特质通道的压制标志也跟着错。
    # 第 62 条 `GeneSync`：`x:def,def|e:def,def` 整表发，重建走 `AddGene`/`RemoveGene` —— 这两个的私有重载会
    # 授予/收回 abilities、并调用 `GainTrait(..., suppressConflicts: true)` 挂 `forcedTraits` 且设 `sourceGene`
    # （`:471368-471400`）以及 `traits.Notify_GeneRemoved`、回卷 `passionMod`、`gene.PostRemove()`（`:471429-471474`）。
    # 直接写 `xenogenes`/`endogenes` 会跳过这些，让基因通道和特质通道每帧互相删对方挂上去的 forced trait。
    # 注意：`Gene.Notify_PawnDied` 是**死亡回调**，不是基因列表同步 —— 规则只对列表类方法成立。
    # 账本里实际命中这条的是 `Gene.Notify_PawnDied`，理由要分开写，否则又变成"用一条理由吃掉了另一件事"。
    if typ in ('Pawn_GeneTracker', 'GeneUtility'):
        return '~', ('基因整表随 GeneSync（x:…|e:…）下发，重建走 AddGene/RemoveGene；'
                     '本图 4 个小人 14 条内源基因往返一致，异种基因 0 条待载体')

    # `Ideo` 的 precept 列表：`IdeoSync` 的表只带 id/name/color/development，precept 从来不在里面，所以副本的
    # 戒律停在载入存档那一刻。改判、增删戒律都从改革仪式/开发者操作/meme 变更走
    # `AddPrecept`（`:492639-492677`）/`RemovePrecept`（`:492723-492748`）。
    # 第 63 条 `PreceptSync`：`def@holder` 整表发；删除走 `replacing: true`（否则 `RemovePrecept` 会按
    # `defaultSelectionWeight` **随机补一条**，副本上凭空长出一条权威端没选过的戒律）；新增走 `AddPrecept(init: true)`
    # （`Init` 才让角色 precept `active` 并填 abilities）。仪式/建筑类 precept 无 pattern 与房间要求，按「未重建」计数。
    if typ in ('Ideo', 'IdeoManager'):
        # 归 `x` 而不是 `~`：ideo 表由 `IdeoSync`、precept 表由 `PreceptSync` 覆盖，两条都实测过
        # （16 个 ideoligion / 868 条 precept 逐个往返一致）。剩下的是 `relicsCollected` 一个成就位，
        # 以及仪式/建筑类 precept 的 pattern 与房间要求——后者只影响「玩家被要求盖什么」的显示，
        # 不进 `GetValueUnfinalized`，所以不构成 `StatWorker` 那类模拟缺口。
        return 'x', ('IdeoSync（表）+ PreceptSync（戒律 def@holder，16 个 ideoligion/868 条往返一致）；'
                     '剩 relicsCollected 与仪式 pattern 未重建')

    # 四个 precept 的 ThoughtWorker 里，两个读**世界级历史戳**，而这两个戳各只有一个写入点、副本从不推进：
    #   `History.lastTickPlayerRaidedSomeone`（`:362270`，scribe `:362328`）只由 `Notify_PlayerRaidedSomeone`
    #     （`:362353-362356`）写；`ThoughtWorker_Precept_RecentConquest`（`:334466-334495`）经 `DaysSinceLastRaid`
    #     （`:334473`/`:334481`）把它变成四段心情，而"突袭别人"是世界地图动作，副本永远不做。
    #   `Faction.lastExecutionTick`（`:485266`，scribe `:485511`）只在执行路径 `:719495` 写；
    #     `ThoughtWorker_Precept_NoRecentExecution`（`:335217-335226`）比较 `TicksGame - lastExecutionTick > 1800000`。
    # 两个都是**绝对 tick 戳**，而副本时钟已对齐，所以原值发。已加进 `ResidualCounterSync` 的键表
    # （`History.lastTickPlayerRaidedSomeone` 用 Owner 0 单例，`Faction.lastExecutionTick` 用 faction.loadID），
    # 实测 `lastTickPlayerRaidedSomeone×1,lastExecutionTick×1` 都在载体表里。
    # 另外两个：`BloodfeederPresent` 读 `otherPawn.IsBloodfeeder()`，`GrowthVatInColony` 读
    # `listerBuildings.AllBuildingsColonistOfDef(GrowthVat).Any()` —— 都是已同步的世界查询，属 `x`。
    # gizmo/浮动菜单这一簇的根因在**第 64 轮查清**，不是"输入待核"：
    #   `FloatMenuOption.Chosen(bool, FloatMenu)`（`:179550-179571`）是右键菜单唯一的执行口，方法体是
    #   `floatMenu?.PreOptionChosen(this)` 然后直接 `action()` —— 全项目**没有任何补丁**挂在这里。
    #   1.7 把老式 `AddUndraftedOrders` 拆成约五十个 `FloatMenuOptionProvider`（`:656278` 起），
    #   每个 provider 用闭包现造 option；逐 provider 打补丁等于每加一个 mod 就漏一个。
    #   第 65 条 `FloatMenuChosenCapturePatch` 挂在这一个入口上，用**前后 Scribe 片段**（不是逐选项命令：
    #   closure 没有可解码的形状），采样对象是「选中对象 + `option.revalidateClickTarget`」——
    #   后者必需，因为右键一堆物资时选中的是**小人**不是那堆物资，只采选中会漏掉最常见的右键。
    # 账本与 `floatmenu-probe` 里原来引的 `:179121-179131` 是 `FloatMenuGridOption.Chosen()`（网格图标），**指错了类**。
    # 第 70 轮：世界对象的 gizmo 采样也补上了 —— `CaptureGizmoTargets` 原来只认 `Thing`/`Zone`，
    # 而星球层的选中项一个 `Thing` 都没有。世界对象生成的是 `Command_Action`，**早就在 `GizmoTargetNames` 里**，
    # 所以缺的从来不是补丁，是采样器认不出对象类型。加了 `'w'` 分支 + `FindWorldObjectById` + `ApplyThingState` 的
    # `case 'w'`，和浮动菜单那条路用的是同一套。
    # 剩下的这簇真正的问题是「点击之后的效果由哪条通道承载」，逐条写清楚，不再用一句"根因未明"糊过去。
    # 第 71 条：`FactionIdeosTracker` 是「哪个意识形态属于哪个派系」，和意识形态表（名字/颜色/发展点）是两件事。
    # 两个字段都以**引用** scribe（`:487657-487666`），指针只在 `SetPrimary`（`:487569-487572`，纯字段写）与
    # `RecalculateIdeosBasedOnPlayerPawns`（`:487516-487567`）里移动，而这两个都由权威端自己的 UI
    # （`Page_ChooseIdeoPreset.MakeIdeoPrimary` `:630189`、`Page_ConfigureIdeo` `:630121`）和
    # `SetIdeo`/`Notify_MemberDied`/`Notify_MemberCaptured` 触发 —— 副本一个都不跑。
    # 读者：`XenotypesAvailableFor`（`:133652-133665`）走 `PrimaryIdeo.memes` 决定该派系小人的异种分布；
    # `TraderKindCommonality`（`:389493-389500`）在 `AllIdeos` 里任一不赞成奴隶制时拒掉所有 Slaver 商人。
    # 必须排在任何 `Ideo`/`Faction` 宽匹配之前。
    # 第 72 条：地图生命周期。之前没有任何通道携带「有哪些地图」。所有 map-keyed 通道都按 `Map.uniqueID` 走并
    # 在解析失败时静默丢弃，所以权威端新建一张图之后，副本上那张图**不存在**（不是不同步），而两端的图列表
    # 不一致且无任何报错。`uniqueID` 由 `GetNextMapID()`（`:85222`）在**创建时**分配一次，创建本身没人携带。
    if typ == 'MapGenerator' or typ == 'PocketMapUtility' or typ == 'Map':
        return 'x', ('通道：`maps`（`ReplicaMap`，`uniqueID`+尺寸+父对象；零尺寸为墓碑）；'
                     '副本走 `Map.ConstructComponents` 建容器，不跑 gen step')
    if typ == 'FactionIdeosTracker':
        return 'x', '通道：`factionIdeos`（`ReplicaFactionIdeo`，`primary;minor,minor`，按 `Ideo.id` 引用）'
    if typ in ('Settlement', 'MapParent'):
        # 第 110 轮：原判「派生效果在世界对象通道之外，未逐条测」把两件事混成一件。逐条读下来：
        #
        # ① `Settlement.GetTransportersFloatMenuOptions`（`:786670`）只是 `base` + 一个 `GiveToCaravan`
        #    的选项；`TransportersArrivalActionUtility.GetFloatMenuOptions`（`:797253`）产出的是**绑好了
        #    `launchAction` 闭包的选项**，点击后走 `launchAction(tile, arrivalAction)`，也就是
        #    `CompLaunchable.TryLaunch`（`:543651`）—— 本轮已实测真正发射（见上面发射那条）。
        #    选项列表本身在权威端枚举，副本不需要：它只影响「权威端显示哪些目的地」。
        # ② `Settlement.GetGizmos`（`:782350`）读的是 `planetLayers`（随层定义）和
        #    `WorldGizmoUtility.TryGetCaravanGizmo`（`:793289`），后者读 `Find.WorldSelector.SelectedObjects`
        #    —— 那是**每个副本自己的选择状态**，不该同步 —— 以及 `FormCaravanComp.CanReformNow()`。
        #    `FormCaravanComp` 是 world object comp，`WorldObject.ExposeData`（`:798523-798556`）把 comp 字段
        #    平铺进对象自己的节点，`WorldObjectStateSync` 整片携带（本图实测 `世界物件状态：254 个 / 发 254`）。
        # ③ 唯一真正依赖模拟的是 `SendToTileGizmo` 里的 `Find.WorldReachability.CanReach`（`:793379`），
        #    而它只在**权威端**被求值（gizmo 在权威端生成），所以副本不模拟并不会让它算错。
        #
        # 结论：入口与派生效果都在已有通道里，剩下的「未逐条测」现在逐条给出了去向。
        return 'x', ('gizmo 读本地选择 + `FormCaravanComp`（随 WorldObjectStateSync 整片传递）；'
                     '浮动菜单只产出绑好 launchAction 的选项，点击走已实测的 TryLaunch；'
                     '`SendToTileGizmo` 的 WorldReachability 只在权威端求值')
    if typ in ('LordJob_BestowingCeremony', 'LordToil_BestowingCeremony_Wait'):
        # 第 104 轮：按反编译里的真实名字重判。「点开始」就是 `Command_BestowerCeremony.ProcessInput`
        # （`:318155`），而 `Command_BestowerCeremony` 早在 `GizmoTargetNames` 里（`SharedActions.cs:2761`），
        # 所以原判「无独立命令、只能靠快照」是错的：它是个普通 gizmo，`GizmoStatePatch` 直接看得见。
        return 'x', '「点开始」= `Command_BestowerCeremony.ProcessInput`（`:318155`），已在 GizmoTargetNames；仪式状态走 LordSync'
    # **第 104 轮：这七条的方法名是 CT（ColonyTogether）里的，部分是 1.6 的** —— grep 搜不到不等于没有：
    # `CompTreeConnection.CompGetGizmosExtra` 在 `:562654`、`MonumentMarker.GetGizmos` 在 `:592620`、
    # `CompPlantPreventCutting.CompGetGizmosExtra` 在 `:550058`、`MinifiedThing.GetGizmos` 在 `:569833`、
    # `StorageSettingsClipboard.CopyPasteGizmosFor` 在 `:576999`、`Building_OutfitStand.GetFloatMenuOptions`
    # 在 `:575509`、`Building_Crate.GetMultiSelectFloatMenuOptions` 在 `:579418`。
    if typ == 'CompTreeConnection':
        # **真缺口**：`CompGetGizmosExtra` 只开对话框，写点是 `Dialog_ChangeDryadCaste.StartChange`（`:627750`），
        # 由 `Dialog_MessageBox.CreateConfirmation` 的回调调用 —— 与第 101 轮 banish/recruit/release 同形。
        return 'x', '第 104 轮补了 DryadCasteCapturePatch（`:562654` 只开对话框，写点在 `:627750` 的 MessageBox 回调）'
    if typ in ('Building_OutfitStand', 'Building_Crate'):
        return 'x', '穿脱/开门走 `TryTakeOrderedJob`（`:575586` / `:579467`），由 OrderCapturePatch 盖住；Building_Crate 不可 Scribe 故无快照，但动作是 job'
    if typ == 'StorageSettingsClipboard':
        return 'x', '静态类不可 Scribe；Copy/Paste 只写 `StorageSettings`，而该对象随所属物件/区域的片段同步'
    if typ in ('MonumentMarker', 'CompPlantPreventCutting', 'MinifiedThing'):
        return 'x', '方法在反编译里有（1.6 名搜不到不等于没有）；快照对可 Scribe 类型成立，且 CompPlantPreventCutting 是 Command_Toggle，已在 GizmoTargetNames'
    if typ in ('Command_Toggle', 'GizmoGridDrawer') or 'GizmoOnGUI' in methods:
        # 归 `x`：gizmo 由 `GizmoStatePatch`（`ProcessInput`，14 个入口）、右键菜单由第 65 条
        # `FloatMenuChosenCapturePatch`（`FloatMenuOption.Chosen(bool, FloatMenu)`，实测前缀 1/后缀 1）覆盖。
        # 原来的理由写「副本空浮动菜单的根因未明」—— 根因在 `:179550-179571` 那个裸 `action()` 无人拦截，
        # 已补。剩下的只是**仪器限制**：专服上 `PlayerActing` 恒假，点击路径不执行，所以计数恒为 0；
        # 这不是功能缺失，`floatmenu-hook` 会明确报「这是闸门拒绝，不是采样失败」。
        return 'x', ('GizmoStatePatch（ProcessInput）+ FloatMenuChosenCapturePatch（Chosen(bool,FloatMenu)，实测已挂载）；'
                     '专服 PlayerActing 恒假故点击不可测')

    if typ == 'Building' and any('Gizmo' in m for m in methods):
        # 第 110 轮结案。`Building.GetGizmos`（`:150157-150206`）本身**不写任何状态**，它按当前状态拼出一组
        # 命令，逐条读的输入都已在通道里：
        # ① `Gizmo_RoomStats`（`:150444`）—— `Room.GetStat` 由房间里的建筑/地形/温度算出，三者都有通道
        #    （建筑轮转补扫、`TerrainSync`、`RoomTempSync`）。`GizmoOnGUI` 里 `room == null` 时返回 `Clear`
        #    （`:150466`），也就是「没房间就不显示」，这在副本上同样是正确行为。
        # ② `InstallationDesignatorDatabase.DesignatorFor(def)`（`:150174`）—— 纯 def 驱动。
        # ③ `BuildCopyCommandUtility.BuildCopyCommand`（`:150184`）/ `BuildRelatedCommandUtility`（`:150192`）—— 纯 def 驱动。
        # ④ `lord.CurLordToil.GetBuildingGizmos`（`:150202`）—— 读 lord 状态，`LordSync` 覆盖。
        # ⑤ `QuestUtility.GetSelectMonumentMarkerGizmo`（`:150167`）—— 读 `MonumentMarker`，第 104 轮已判 `x`。
        # 于是「入口本端每帧跑」这件事成立，且没有留下未接的输入；`DeSpawn`/`DoDestroyEffects`/`DeconstructibleBy`
        # 走 removed 通道与设计器路由（见下面生命周期规则的注释）。
        # 不可测的部分与 `Command_Toggle` 那条同一个仪器限制：专服上 `PlayerActing` 恒假，点击路径不执行。
        return 'x', ('GetGizmos 只读已同步输入（房间统计/def 驱动的复制与相关建造/lord 状态/MonumentMarker）；'
                     '生命周期走 removed 通道；专服 PlayerActing 恒假故点击不可测')

    if typ.startswith('ThoughtWorker_Precept_'):
        return 'x', ('输入已同步：RecentConquest/NoRecentExecution 读的两个世界历史戳已进 ResidualCounterSync 键表'
                     '（实测 lastTickPlayerRaidedSomeone×1 / lastExecutionTick×1）；BloodfeederPresent 与 '
                     'GrowthVatInColony 读的是已同步的小人属性与建筑列表')

    if typ == 'Gene':
        # 第 108 轮：`Notify_PawnDied`（`:111370`）的 body 只有一行
        # `Find.HistoryEventsManager.RecordEvent(new HistoryEvent(def.deathHistoryEvent))`，
        # 而 `HistoryEventsManager` 已在第 106 轮判为 `-`（唯一读者 `FactionUIUtility.GetRecentEvents` `:644122`）。
        # 唯一 override 它的 `Gene_Deathrest`（`:727506`）只清三个缓存字段（`boundBuildings`/`cachedBoundComps`/
        # `cachedDeathrestNeed`），而 `boundBuildings` 本身在 `ExposeData`（`:727646`）里。死亡本身由帧里的
        # `HealthState` 传播，缓存随对象重建自然失效。
        return 'x', ('列表由 GeneSync 提供；基因实例上的玩家开关随每小人追踪器行'
                     '（`x:hemogenPacksAllowed` 段，第 142 轮实测：发 1 行、应用计数 +1）；'
                     '`Notify_PawnDied` 只写 HistoryEventsManager（已判 `-`），'
                     '唯一 override `Gene_Deathrest` 清的是随片段重建的缓存')

    if typ in ('StatWorker',):
        # 更正上一轮的判断：**apparel/equipment 是有通道的**。`ApparelSync` 发 `def#stuff#quality`
        # （`Source/ApparelSync.cs:76-100`），`ApplyEquipment`/`ApplyApparel` 用 `Make(entry)` 重建对象，
        # 材质和品质都在，所以 `StatOffsetFromGear`（`:716803-716823`）读的三样
        # （`gear.def.equippedStatOffsets`、`CompBladelinkWeapon` 的 trait 偏移、`stat.parts` 变换）里前两样是对的。
        # 唯一真缺的是 **`CompBladelinkWeapon.TraitsListForReading`**（语刃武器词条），它不在 `Describe` 里。
        # 所以真正还缺的是两类：**precepts** 和 **genes**。
        # precepts 已由 63 条 `PreceptSync` 补齐（16 个 ideoligion / 868 条 precept，逐个往返一致）。
        # `GetValueUnfinalized` 对这个列表做两件事：加 `precept.def.statOffsets`，以及加
        # `conditionalStatAffecter.Applies(req)` 的偏移和 `RoleEffect_PawnStatOffset.modifier`（按 `Ideo.GetRole(pawn)`
        # 找到这个小人担任的角色）。所以角色**持有者**也随载荷走（`def@thingIDNumber`，多任的记 `@m`）。
        # 剩下的只有 `CompBladelinkWeapon.TraitsListForReading`（语刃武器词条）一个字段。
        # `CompBladelinkWeapon.TraitsListForReading`（`:532217`，由 `PostExposeData` 以
        # `Scribe_Collections.Look(ref traits, "traits", LookMode.Def)` `:532418` 保存）也补上了：
        # `ApparelSync.Describe` 的第四个字段带逗号连接的词条名，`Matches` 也比词条集合（不比的后果是——保留下来的
        # 对象**永不重建**，一把增减过词条的语刃武器会永远错），`Make` 直接写 `TraitsListForReading` 那个活列表
        # （不走 `GainTrait`，那会复发绑定消息和存档期 biosignature）。
        # 到这里 `GetValueUnfinalized` 的七类输入全部有载体。
        return 'x', ('traits / genes / precepts / apparel / equipment（含语刃词条）全覆盖；'
                     '七类输入均有载体，`GetValue` 的缺口关闭')

    if typ == 'Dialog_CreateXenogerm':
        # 第 110 轮结案。两件事分别有证据：
        # ① 入口：`Dialog_CreateXenogerm.Accept`（`:630275`）→ `StartAssembly`（`:630287`）→
        #    `GeneAssembler.Start`（`:581172`）。原先的 `GeneAssemblerStartCapturePatch` 复用 gizmo 快照对，
        #    这是正确的做法——按钮不在 `Command.ProcessInput` 上，但写入落在**选中建筑**上，
        #    而建筑补扫本来就整份重发（`buildSweep` 收 `ThingRequestGroup.BuildingArtificial` 每栋，
        #    `HeadlessRuntime.cs:838`），所以写入路径不依赖这个捕获点也不会丢。
        # ② 写入：`xenogerm-probe` 放了一台真装配器后直接调同一句 `Start` 并读片段，实测
        #    `473→611 B；变化 4 个：xenotypeName/iconDef/workingInt/totalWorkRequired`。
        #    所以「建筑自己的片段就是全部状态」是读数而不是推断。
        # 余下的是延迟而非缺失：轮转补扫每帧 24 栋，42k 物的图上要若干帧才轮到这一栋。
        return 'x', ('对话框按钮写入选中建筑，`Building_GeneAssembler.ExposeData`（`:581416`）scribe 全部六个字段，'
                     '建筑轮转补扫整份重发；实测 `xenogerm-probe`：片段 473→611 B，Start 后 4 个字段变化')

    if typ == 'Xenogerm':
        # 第 110 轮结案。`SetTargetPawn`（`:596878-596899`）在确认回调里写两样东西，各走各的通道：
        # ① 小人侧：删旧 bill → `CreateSurgeryBill(newTarget, ImplantXenogerm, null).xenogerm = this`。
        #    `XenogermRetargetCapturePatch` 挂在 `SendImplantationLetter`（`:596902`，回调最后一句）上，
        #    那时 bill、`bill.xenogerm`、`targetPawn` 都已写好，是一次纯读。
        # ② 物品侧：`targetPawn = newTarget`，由 `Xenogerm.ExposeData`（`:597090`）scribe。
        #    载体用 `transport-fixture Xenogerm` 放出来（`Item` 类，`SweepItemStates` 收），
        #    实测 `xenogerm-target`：`片段 339→349 B；写入前有 targetPawn 元素 True，写入后有 True（值 Thing_Human873）；
        #    复原片段逐字节一致`。**所以「赋值在 CreateSurgeryBill 之后」不再是风险**：两个对象都在。
        # 注意 `ExposeData` 不 scribe `geneSet` —— 它由 `Initialize`（`:596918`）在装配完成时写一次，
        # 之后不变；换目标不改它，所以缺它不影响这条规则。
        return 'x', ('小人侧经 SendImplantationLetter postfix（回调末句，bill 与 xenogerm 均已写好）；'
                     '物品侧 targetPawn 由 ExposeData（`:597090`）scribe，实测 339→349 B、值 Thing_Human873、复原逐字节一致')

    if typ == 'HealthCardUtility':
        return 'x', ('手术账单由 surgery-bill 整份下发（CreateSurgeryBill postfix）；'
                     'bill-add 对 Bill_Medical 放行，否则部位赋值会被 setter 拒绝')
    if typ == 'Bill_Medical':
        return 'x', '账单本体随 surgery-bill 的 Scribe 片段到达（part/consumedMedicine/uniqueRequiredIngredients）'

    # **`Notify_*` 回调整类判据化（第 99 轮）。** 原先这 11 条的理由是「副本不产生事件；后果由
    # Created/Removed/各通道承载，逐条待核」—— 和「入口未测」是同一个毛病：读起来像缺口，什么也不阻塞。
    # 逐条读过后果落在哪里（`_lab/asm/…:645488` 等），三类，每类都有答案：
    #
    # ① **仪式义务**（`RitualObligationTrigger_MemberDied` / `_MemberCorpseDestroyed` 的 `AddObligation`）——
    #    已有载体：`ResidualCounterSync.RitualObligations` 反射读 `Precept_Ritual.activeObligations` 逐个下发。
    #    注意它按 `RitualObligation` 的 `GetHashCode() & 0x7FFFFFF` 认身份，因为义务没有稳定 id。
    # ② **历史事件**（`IdeoUtility.Notify_PlayerRaidedSomeone` / `Notify_HistoryEvent` / `Precept_Relic.Notify_ThingLost`
    #    的 `RecordEvent`）—— 后果是 `HistoryEventsManager` 里的累积，进的是 ideo 的叙事统计，不进 UI 主流程；
    #    由 `IdeoSync` 覆盖。`Precept_Relic.Notify_ThingLost` 还附带「传闻扩散」，那是 `RecordEvent` 的派生。
    # ③ **纯显示缓存**（`WorldRenderer.Notify_TilePollutionChanged`）：方法体只是把区域标脏、入队重建网格
    #    （`:774923` `regionsToRegenerate.Enqueue`），**没有任何模拟后果**，副本上污渍层晚一帧无所谓。
    #    `GameComponent_Bossgroup.Notify_PawnKilled` 的 `killedBosses.Add` 唯一读取点是
    #    `DebugTables.MakeTablesDialog`（`:172243` 开发表）别的读者是 `IsDefeated`（`:723931`，只被自己的
    #    `CanReserve` 用）—— 也只在开发者界面可见。
    #
    # `Precept_RoleSingle.Notify_MemberChangedFaction` 与 `LifeStageWorker_HumanlikeAdult.Notify_LifeStageStarted`
    # 是**权威端的反应**（`Assign(null)` / 换体型 + `SetAllGraphicsDirty`），触发条件由已同步的
    # `pawn.Faction` / `pawn.ageTracker` 决定：权威端在自己身上做了，副本读结果（`GizmoStatePatch` 的片段）。
    if typ in ('RitualObligationTrigger_MemberDied', 'RitualObligationTrigger_MemberCorpseDestroyed'):
        return 'x', '仪式义务随 ResidualCounterSync 下发（反射读 Precept_Ritual.activeObligations，按义务哈希认身份）'
    if typ in ('IdeoUtility', 'Precept_Relic'):
        return 'x', 'RecordEvent 的后果是历史事件累积，由 IdeoSync 覆盖；不进 UI 主流程'
    if typ == 'WorldRenderer':
        return '-', '方法体只把坏区标脏并排队重建网格（:774923），没有模拟后果'
    if typ == 'GameComponent_Bossgroup':
        return '-', 'killedBosses 的唯一读者是 DebugTables 开发表（:172243），只在开发者界面可见'
    if typ in ('Precept_RoleSingle', 'LifeStageWorker_HumanlikeAdult'):
        return 'x', '权威端的反应（Assign/换体型），触发条件来自已同步的 faction 与 ageTracker；副本读结果'

    # **第 102 轮：`pawn.royalty` 建了通道。** 六条 `~` 的共同理由都是「没有载体」，而
    # `CharacterCardUtility`、`PermitsCardUtility`、`RoyalTitle.RoomRequirementGracePeriodActive`
    # 与全部 `RoyalTitlePermitWorker_*` 读的都是它。`Pawn_RoyaltyTracker.ExposeData`（`:475905`）本该承载，
    # 但 `AuthoritativeFrame.PawnState` 恒为 null（实测 71 个小人 636386 B，8963 B/个，
    # 是位置帧的 224 倍），所以按名通道是唯一办法。
    #
    # `royalty-channel` 实例：`追踪 4 个 / 存档内 4 个`（授衔前 有头衔 0）→ `royalty-grant` →
    # `有头衔 1 个共 1 条`；`probe` 往返一致 `10:DMS_Sergeant|` → `下一帧待发 1 个`。
    # 房间无副本时服务端 `副本=0 未推进原因=没有观众`，所以 `发出` 停在上一帧的值——`Pending()` 才是
    # 能回答「下一帧会不会带」的那个读数（也是非破坏性的，`Publish` 会把基线写掉）。
    if typ in ('CharacterCardUtility', 'PermitsCardUtility', 'RoyalTitle',
               'RoyalTitlePermitWorker_DropResources', 'RoyalTitlePermitWorker_CallShuttle', 'Pawn_RoyaltyTracker'):
        # `CharacterCardUtility` also reads `pawn.health`, which still has no carrier - say so rather than claiming the
        # entry is finished. `PermitsCardUtility` is fully covered: it reads only `pawn.royalty`.
        if typ == 'CharacterCardUtility':
            # 第 103 轮：pawn.health 那一半也有了。DrawCharacterCard（:642497）对 health 的唯一引用是
            # `else if (!pawn.health.Dead)`，而 Pawn.Dead = health.Dead = healthState == Dead（:136079）。
            # 所以需要的不是 hediff 内容，而是那个一字节枚举；ReplicaPawn.HealthState 承载它。
            # 实测 health-state probe：改 Down -> Downed=True，写回 Mobile -> Downed=False，往返一致。
            return 'x', 'RoyaltySync（头衔/已购许可）+ ReplicaPawn.HealthState（第 103 轮）；动作行由 healthState 决定，实测写回后 Downed 跟着变'
        return 'x', '第 102 轮建了 RoyaltySync 通道；royalty-channel 实测 往返一致、下一帧待发 1'

    # **第 101 轮：玩家动作入口这一组逐条查过。** 15 条里只有 3 条是**真的工具钩子缺口**，其余都能定性：
    #
    # 有捕获（本轮补的，见 `Source/SharedActions.cs` 的三个 prefix）：
    #   PawnBanishUtility.Banish              ← 角色卡/商队的确认框回调 → `BanishCapturePatch`
    #   RecruitUtility.Recruit                ← 囚犯页招募 gizmo → `RecruitCapturePatch`
    #   ReleaseAnimalToWildUtility.DoReleaseAnimal ← 动物放归 gizmo → `ReleaseAnimalCapturePatch`
    # 这三条此前被判成「入口已覆盖」是**错在观察点**：打开确认框的那个 gizmo 是 `Command`，确实被
    # `GizmoStatePatch` 盖住；真正写状态的是 `Dialog_MessageBox` 的 `onConfirm` 回调，它从不经过
    # `Command.ProcessInput`。「按钮能点到」和「按钮的效果被发布」是两件事。
    #
    # 非入口（权威端 tick 或纯计算，`_lab/chokepoints.py` 判 NOT-AN-ENTRY）：
    #   AutoBuildRoofAreaSetter.TryGenerateAreaNow  ← 自己的 `AutoBuildRoofAreaSetterUpdate` 间隔 tick
    #   Blueprint.TryReplaceWithSolidThing / Blueprint_Build.MakeSolidThing ← `Toils_Construct`，由建造 job 驱动
    #   Gravship.TickInterval / Building_GravEngine.* / GravshipLandingMarker.BeginLanding
    #   SettlementProximityGoodwillUtility.* / SettlementUtility.AffectRelationsOnAttacked
    #   IdeoDevelopmentUtility.ApplyChangesToIdeo ← `Dialog_ReformIdeo.DoWindowContents`（对话框，PreClose 采样）
    #   Precept_Ritual.AddObligation ← 六个 `RitualObligationTrigger_*` 回调，最后一条经 `Recache`
    #   Pawn_MutantTracker.Turn ← 三个 hediff/utility 内部调用
    #   SettleUtility.AddNewHome ← `GravshipUtility.SettleTile` / `MoveColonyUtility`：世界对象通道已覆盖
    #     （`worldobject-lifecycle` 实测 `建行 1（片段 405 B），删行 0`）
    if typ in ('PawnBanishUtility', 'RecruitUtility', 'ReleaseAnimalToWildUtility'):
        return 'x', ('玩家确认框回调入口，第 101 轮补了捕获（pawn-leave 命令）+ 服务端白名单 + 分类 + 安装点；'
                     '权威端用游戏自己的方法重放')
    if typ in ('AutoBuildRoofAreaSetter', 'Blueprint', 'Blueprint_Build', 'Gravship', 'GravshipLandingMarker',
               'Building_GravEngine'):
        return '-', '权威端 tick 或建造 job 驱动（chokepoints.py 判 NOT-AN-ENTRY）；副本不跑这条路径'
    if typ in ('SettlementProximityGoodwillUtility', 'SettlementUtility', 'IdeoDevelopmentUtility'):
        return 'x', '世界对象/对话框侧：派系好感随 FactionSync，改 ideo 在 Dialog_ReformIdeo 里由 PreClose 采样'
    if typ in ('Precept_Ritual', 'Pawn_MutantTracker', 'SettleUtility'):
        return 'x', '义务随 ResidualCounterSync、变身由 hediff 通道、定居点由 WorldObjectCreations（实测建行 405 B）'

    if typ == 'StorytellerComp_Triggered':
        # `Notify_PawnEvent` 读 `p.IsColonist` / `p.Dead` / `Downed` / `DeathRefusal` 决定要不要放事件，然后调
        # `StorytellerUtility.DefaultParmsNow` + `IncidentWorker.TryExecute`。**触发与执行都在权威端**：副本不跑
        # storyteller，玩家也不会在副本上「触发」一次死亡。副本要看的是结果（袭击/信件），由 `Letters` 通道承载。
        return 'x', '故事叙述者的判断与执行都在权威端；副本看结果（Letters 通道），这条不是入口'
    if typ == 'ScenPart_PursuingMechanoids':
        # `Notify_QuestCompleted` 的方法体只有一行 `questCompleted = true;`，唯一读者是本类的 `Disabled`，
        # 而 `Disabled` 只被自己的 `CanDo`/`PreventedBy` 用——又是「场景条件在权威端判」的那一类。
        # `GetAlerts`/`Tick` 是场景自己的每 tick 判断，副本不跑 tick。
        return 'x', '场景条件在权威端判（Notify_QuestCompleted 只置一个本类读的 bool）；副本不跑 tick'
    if typ == 'WorldComponent_GravshipController':
        # 第 110 轮结案。原判只看到 `WorldComponentOnGUI`（`:27064` 画确认框）与 `PlayerCanControl`
        # （`:52701`）两个读者，把整类归给「世界对象通道 + 飞船自己的 gizmo」—— 这不够，因为那两个读者
        # **决定的是全局暂停**，而不是某一艘船的显示：
        # ① `TickManager.ForcePaused`（`:52680-52694`）在 `LandingAreaConfirmationInProgress` 为真时返回 true，
        #    也就是整个世界暂停。
        # ② `TickManager.PlayerCanControl`（`:52696-52711`）在同一个条件下拒绝每一次改速/取消暂停，并弹
        #    「MessageConfirmLandingAreaFirst」。
        # ③ `cutsceneInProgress`（`static`，`:26542`）被 `ForcePaused`/`SuppressDraw`/`SectionLayer` 分组
        #    （`:30520`/`:49942`/`:78442`/`:81427`/`:83058`）读。
        # 三者都是 world component 状态，没有任何通道携带 —— 于是权威端暂停时副本继续跑，载入时正好卡在确认中的
        # 副本永远冻结。这是唯一一类「两块屏幕对『游戏是否在运行』意见不同」的偏差，看殖民地看不出来，只能看时钟。
        # 新通道 `GravshipGateSync`（第 n 条）：`"确认中"+"cutscene"` 两个布尔，只在变化时发，
        # 应用端写 `cutsceneInProgress`（静态字段）并在权威端说「无确认」时清 `landingMarker` 指针。
        # `landingMarker` 本身随物件通道走，不发。
        # 实测：`sync-census` 的「飞船闸门」行 —— `发射 1 次 / 应用 0 次 / 失败 0 次；本端 cutscene=False、
        # 确认中=False`。本存档 0 艘飞船，所以两个值都是 false：这是能在本环境测到的全部内容，
        # 与「一发射就看见飞行器」不同，**不断言有飞船时的行为**。
        # 踩过的坑：第一版用 `AccessTools.FieldRefAccess` 做 `static readonly` 初始化，
        # `FieldRef` 给静态类造实例字段访问器 → `TypeInitializationException`，整个 `sync-census` 直接失败。
        # 已改为用时反射，并加测试禁止该写法（断言按代码行过滤注释 —— 注释里提到这个名字不算）。
        return 'x', ('通道 GravshipGateSync：`ForcePaused`(52680)/`PlayerCanControl`(52696) 两个全局暂停输入随帧发；'
                     '应用写静态 cutsceneInProgress 并清 landingMarker；实测发射 1 次、两值皆 False（本存档 0 艘船）')

    if typ == 'NeedsCardUtility':
        # 第 99 轮：`DrawThoughtListing`（`:645488`）读 `GetThoughtGroupsInDisplayOrder(pawn.needs.mood, …)` 再逐组
        # `GetMoodThoughts(group, …)` —— 读的是**内存里的 Thought 对象**，不是 `ReplicaPawn` 上任何一个数字。
        # 这些对象由 `ApplyThoughts` 用 `ThoughtMaker.MakeThought` + 手填 `pawn` 重建（`MakeThought` 不填归属人，
        # `AddThought` 才填；跳过后者会让 `Thought_Memory.MoodOffset` 的 `ThoughtNullified(pawn, def)` 拿到 null
        # 然后 NRE —— 那是第 62 轮修掉的刷屏）。
        #
        # 载体是 `ReplicaPawn.Thoughts`。第 97 轮把这条判成 `GAP`（理由「`NeedsSync` 不含 `thoughts`」）——
        # 那个理由是真的，结论是错的：想法不走 `NeedsSync`，走 `Thoughts` 字段。
        # 真正的缺陷是**条数**：`EncodeThoughts` 当时按 `|MoodOffset|` 只发 8 条，而面板读全部，
        # 于是第 9 条往后的想法在副本上不存在。实测 `MemorySync` 发 9 条、`EncodeThoughts` 发 8 条 ——
        # 两个通道对同一张列表给出不同长度。第 99 轮去掉截断。
        return 'x', ('想法对象由 ApplyThoughts 从 ReplicaPawn.Thoughts 重建（MakeThought + 手填 pawn）；'
                     '第 99 轮去掉了「只发 8 条」的截断，与 MemorySync 逐条发的长度一致')

    if RECOMPUTED.match(typ):
        # 第 97 轮用 `_lab/verdicts.py` 逐条追过读源（`CHANNEL` 表把已同步来源映射到通道，`LOCAL` 集合是
        # `Find.Selector`/`Find.WindowStack`/`Find.CurrentMap`/`Current.ProgramState`），结果 9 `OK-DIRECT` /
        # 15 `NEEDS-FOLLOW` / 5 `GAP` / 4 `LOCAL-ONLY`。`NEEDS-FOLLOW` 的 15 条追一层后基本全收敛（只读
        # `ProgramState` / `TicksGame` / 纯布局）。
        #
        # 但那轮的映射表本身错了两处，把 GAP 掩盖成 3 条：`pawn.royalty` 与 `pawn.needs.thoughts` 被标成「有通道」。
        # 改对后是 4 条具名 GAP，其中 `NeedsCardUtility` 已在本轮判成 `x`（载在 `Thoughts` 而非 `NeedsSync`），
        # 剩下三条留在这里：
        #   `CharacterCardUtility.DrawCharacterCard`  → pawn.health + pawn.royalty
        #   `PermitsCardUtility.DoLeftRect`           → pawn.royalty
        #   `CompStatue`                              → pawn.Faction + pawn.relations
        # 它们仍是 `~`，但理由从「待核」改成**缺哪个字段** —— 「未测」不是判据。
        # **第 107 轮：`CompStatue` 移出这条。** 旧判据把「生成时的输入」当成了「生成后的读源」。
        # `pawn.Faction` 与 `pawn.relations` 只出现在 `InitializeAsFactionLeaderStatue`（`:558134`）与
        # `GetAllPossiblePawnsToSelectFrom`（`:558110`）里，而两者都只在生成期跑
        # （`PostPostGeneratedForTrader` `:558181`、`DebugAction.SpawnStatueCraftedBy` `:172125`）。
        # 生成之后 `PostExposeData`（`:558373-558406`）scribe 了重建 `fakePawn` 需要的全部字段
        # ——bodyType/headType/hairDef/xenotype/beard/apparel/apparelStyles/name/gender/lifestageIndex/
        # hairColor/descriptionSeed/nonXenotypeGenes/hediffsWhichAffectRendering——并在
        # `LoadSaveMode.PostLoadInit` 里只做一件事：`LongEventHandler.ExecuteWhenFinished(InitFakePawn)`。
        # 也就是说整份 comp 片段（`GizmoStatePatch` 已有的那一种）就是完整载体。
        if typ == 'CompStatue':
            return 'x', ('生成期读 pawn.Faction/relations（`:558134`/`:558110`，只在生成时跑）；生成后全部渲染字段在 '
                         '`PostExposeData`（`:558373`）里，整份 comp 片段即完整载体')
        if typ in ('CharacterCardUtility', 'PermitsCardUtility'):
            fields = {'CharacterCardUtility': 'pawn.health 与 pawn.royalty 都没有通道',
                      'PermitsCardUtility': 'pawn.royalty 没有通道（头衔只能靠 royalty-grant 在权威端授予，'
                                            '副本读不到）'}[typ]
            return '~', f'读源确认缺载体：{fields}'
        # 其余 `RECOMPUTED` 条目：入口每帧在副本上跑，读源逐条追过且收敛到已同步通道或本机状态。
        # `_lab/verdicts.py` 可重跑（`python3 _lab/verdicts.py 类型|方法` → 一行判定）。
        return 'x', ('副本自行计算，读源已判据化（_lab/verdicts.py）：'
                     'OK-DIRECT 直读已同步通道 / LOCAL-ONLY 只读本机选择或窗口栈 / NEEDS-FOLLOW 追一层后收敛')
    # 设计器路由。Two entry shapes, and the second one was missed for a round:
    #   - `DesignateSingleCell`/`CanDesignateCell`/`CanDesignateThing` are patched by reflection over every
    #     `Designator` subclass (measured: 54 types / 126 methods);
    #   - `ProcessInput` is **not** patched, and does not need to be: `Designator_Build.ProcessInput`
    #     (`_lab/asm/…:622379`) does one thing before delegating - it opens a float menu of materials, built from
    #     `Map.resourceCounter.AllCountedAmounts` and `Map.listerThings.ThingsOfDef(item).Count`, both of which a
    #     replica holds. The actual designation still goes through `DesignateSingleCell`, which is captured. So the
    #     menu runs locally on a replica and the gesture is routed as usual.
    if typ.startswith('Designator'):
        return 'x', '设计器路由（基类反射覆盖 54 个 Designator 类型 / 126 方法；ProcessInput 的材料菜单读已同步状态）'
    # Gizmo 路由
    if typ.startswith('Command') and any(m in GIZMO_METHODS for m in methods):
        return 'x', 'Gizmo 路由（Command*.ProcessInput，实测 13 个类型）'
    # 账单设置对话框。三个方法（`Bill.SetPawnRestriction`、`Bill_Production.SetIncludeGroup`/`SetStoreMode`）
    # 从来不是全部表面：`Dialog_BillConfig` 还直接写十个字段（`recipe`/`targetCount`/`repeatMode`/`repeatCount`/
    # `hpRange`/`qualityRange`/`ingredientSearchRadius`/`ingredientFilter`/`style`/`globalStyle`/`suspended`/
    # `pauseWhenSatisfied`/`unpauseWhenYouHave`）。所以按**整份 BillStack 片段**捕获，而不是逐字段：
    # `BillStack.ExposeData` 是 `LookMode.Deep`（`:272715`），`Bill.ExposeData` scribes 全部十三项（`:272111`+）。
    # 补丁点在 `Window.PreClose`（虚方法，两个对话框都没有覆盖），命令类型 `bills`。
    # 实测 `bill-probe`：`限制者 无→JB；采样 变化（1236→1304 B）；回灌 成功；过信道 一致（1746 B）`
    # —— 引用字段（`Scribe_References`）也随行，不只是浮点。
    if typ in ('Bill', 'Bill_Production', 'Bill_Autonomous', 'Bill_Mech', 'BillStack', 'Dialog_BillConfig',
               'Dialog_MechBillConfig', 'ITab_Bills'):
        return 'x', '整份账单片段（Window.PreClose 采样，CommandCodec.bills；含三个方法与十三字段）'
    # 叙事者页面。三个 `ref` 参数（`def`/`difficultyDef`/`difficulty`，`:654972`）都是仿真输入：威胁倍率与
    # 事件开关直接决定副本上会不会来袭击。补丁点在 `Page_SelectStorytellerInGame.PreClose`（它自己覆盖了
    # `Page.PreClose`），命令类型 `storyteller`，载荷是整份 `Storyteller` 片段。
    # 实测 `storyteller-probe`：`叙事者 Cinder_EmberWarden，困难 Easy（非自定义，difficulty 不在片段内，
    # 滑块本就不绘制）；片段 310 B；采样 变化（311 B）；过信道 一致（416 B）；回灌 成功…档位恢复 Easy`
    # —— 非自定义难度下 `difficulty` 根本不进片段（`:390071` `if (difficultyDef.isCustom)`），
    # 所以变的是 `difficultyDef`；副本上那些滑块本来也不绘制（`:655130`）。这条区分必须写在理由里，
    # 否则「采样未变」会被误读成比较器坏了。
    if typ.startswith(('Page_SelectStorytellerInGame', 'StorytellerUI')):
        return 'x', '叙事者整份片段（PreClose 采样，CommandCodec.storyteller；非自定义时 difficulty 不在片段内）'
    # 通道路由
    if typ in CHANNEL_TYPES:
        return 'x', '状态通道:' + CHANNEL_TYPES[typ]
    # 皇家子系统：`Pawn_RoyaltyTracker` 是第二份能力列表，`AbilitySync` 读的是 `Pawn_AbilityTracker`。
    # 实测载体为 0（`royalty-count`：有 tracker 4 个、有头衔 0 个），所以标记为「待载体」而不是缺口 ——
    # 通道在零载体上无法与坏通道区分，这条区分是 `ResidualCounterSync` 教出来的。
    if typ.startswith(('Pawn_RoyaltyTracker', 'RoyalTitle', 'FactionPermit', 'RoyalTitleDef')):
        return '~', '待载体:royalty-count 实测有头衔 0 个（第二份能力列表，与 AbilitySync 不同源）'
    # QuestPart_*：21 个 `Notify_QuestSignalReceived` 全是同一个入口。`QuestPartSync` 已经把 quest 及其
    # parts 的整份片段发过去（含 `state`/`flags`/派生的 `CachedAlert`），所以副本上这些 part 的状态由通道决定，
    # **不需要**在信号到达时逐 part 打补丁 —— 信号是权威端的事，结果经通道到达。
    if typ.startswith('QuestPart_'):
        return 'x', '状态通道:QuestPartSync（整份 part 片段，信号在权威端处理）'
    # 纯工具/纯谓词/纯字符串：`Def.LabelCap`、`GrammarResolverSimple.Formatted`、`GenTypes.IsSealedWithCache`、
    # `Log.Error/Warning`、`ParseHelper.*`、`DiaOption.Activate`。它们不持有状态也不改变状态，副本调用它们时
    # 读的是本端已经同步过的 def/字符串，答案自然一致。**判据是"无状态"**，不是"名字像工具"。
    stateless = ('Def', 'GrammarResolverSimple', 'GenTypes', 'Log', 'ParseHelper', 'DiaOption', 'KeyBindingDef',
                 'PawnNameColorUtility', 'GenMath', 'GenText', 'Translator', 'TaggedString')
    if typ in stateless or typ.startswith(('GenText', 'GenMath', 'ParseHelper')):
        return 'x', '无状态工具（只读本端已同步的 def/文本/输入）'
    # 目标选择与寻路：`AttackTargetFinder`/`AttackTargetsCache`/`CastPositionFinder`/`GenAI.RandomRaidDest`。
    # 副本不跑 AI，也不选目标；权威端选完的结果经 `Jobs`（`DriverStateSync`）与位置帧到达。
    if typ.startswith(('AttackTargetFinder', 'AttackTargetsCache', 'CastPositionFinder', 'GenAI', 'Verb_',
                       'PawnPath', 'PathFinder', 'RegionGrid')):
        return 'x', '目标选择/寻路（权威端算；结果经 DriverStateSync 与位置帧）'
    # 剧情/历史/档案：`HistoryEventsManager`、`Archive`、`Tale*`、`PlayLog`、`BattleLog`。
    # 副本不产生新事件，已发生的事件由 `TaleSync`/`MemorySync` 与 `Archive` 的整份片段承载。
    #
    # **第 106 轮：逐条核过，分三类。**
    #
    # 1. `StoryWatcher_PopAdaptation` 两个方法都有载体：`adaptDays` 是私有字段（`:392573`），
    #    `ReplicaAdaptation` 同时送 `AdaptDays` 与 `PopAdaptDays`，`AdaptationSync.SetField` 用
    #    `tracker.GetType()` 找同名字段——两个类的字段名相同、类型不同，所以必须按运行类型查，
    #    而 `watcherAdaptation`（`:392450`）与 `watcherPopAdaptation`（`:392452`）确实是两个不同的类型。
    #    `Notify_PawnEvent` 只在 `GainedColonist` 时把 `adaptDays` 归零，那个结果本身随通道到达；
    #    `PopAdaptationWatcherTick` 每 30000 tick 加 0.5，也是同一个字段。
    # 2. `TaleRecorder.RecordTale` 的产物就是 `TaleSync` 送的两字段（`uses` / `date`）；`Add` 本身不重建。
    # 3. `Archive` / `HistoryEventsManager` / `ArchivedDialog` **是真的没有载体**，但读端极少且全是界面：
    #    `Archive.ArchivablesListForReading`（`:361915`）的读者只有 `QuestPart` 补描述那三处
    #    （`:200178` 等，取 `ChoiceLetter` 的文本）；`HistoryEventsManager` 的唯一读者
    #    `FactionUIUtility.GetRecentEvents`（`:644122`）把近 3600000 tick 的事件数写进派系信息卡。
    #    两者都只在打开某个面板时读一次，不影响任何模拟，归 `-`（副本无此输入）而不是 `~`。
    if typ == 'StoryWatcher_PopAdaptation':
        return 'x', 'adaptDays / popAdaptDays 由 ReplicaAdaptation 送（AdaptationSync 按运行类型找同名字段）；两个方法都只写这两个字段'
    if typ.startswith(('Tale', 'PlayLog', 'BattleLog')):
        return 'x', '产物 = TaleSync 的 uses/date 两字段（第 45 轮）；RecordTale 本身不重建'
    # **第 120 轮：`HistoryEventsManager` 改为 `x`。** 上一版的推理是「唯一读者是一张信息卡，所以不影响模拟」，
    # 那条推理是对的，但它答的是错的问题：账本问的是「这个状态副本上对不对」，不是「它影不影响模拟」。
    # 玩家打开派系对话框看到的「最近事件」块（`FactionDialogMaker.GetRecentEvents` `:644122`，走
    # `GetRecentCountWithinTicks`/`GetRecent` `:362845`/`:362862`）在副本上永远打印载入那一刻的窗口，整个会话不变。
    # 写入点只有 `RecordEvent`（`:362688`，约四十个调用点：砍树 `:248502`、吃人肉 `:158180`、死亡 `:111374`、
    # 仪式 `:264012`），副本一个都不跑 ⇒ 现在由 `HistorySync` 承载：`RecordEvent` 的 postfix 标脏那一行，
    # 行按 `def|faction` 编码（同一个 def 在小人表里是 -1、在每个派系表里各一套），空窗口作为真实状态发送
    # （`CheckRemoveOldEvents` `:362755` 会清空行，发不出去就一直显示已经不近的事件）。
    # 实测 `history-probe`：写入前 `近期条数=0/有过=False` → 写入后 `近期条数=1/有过=True/好感合计=13`，
    # 近窗口 1 条（两条里只有一条落在窗口内），记录一次事件后发出 1 行且末条 tick 正确。
    # `Archive` / `ArchivedDialog` 仍是 `-`，理由不变：`Archive.ArchivablesListForReading`（`:361915`）的读者
    # 只有 `QuestPart` 取来信文本（`:200178`），而 `Archivable` 集合是载入时的深拷贝，副本从不新增条目。
    if typ == 'HistoryEventsManager':
        return 'x', ('HistorySync（第 120 轮）：RecordEvent postfix 标脏，行按 def|faction 编码，空窗口是真实状态；'
                     '实测原版查询 GetRecentCountWithinTicks/GetRecent 读到写入的行')
    # **第 163 轮：`Archive` 改为 `x`。** 上一版的推理（「读者只有界面，不影响模拟」）和第 120 轮
    # `HistoryEventsManager` 那次一样，答的是错的问题：账本问「副本上的状态对不对」。
    # `Messages.Message(Message, bool historical)`（`:185729`）的 `historical` 分支就是 `Archive` 的写入点，
    # 而副本一直写死 `historical: false` ⇒ **历史档案页少了权威端归档的每一条消息**；两端实时列表看起来一样，
    # 所以差异看不见。现在 `MessageHistoryCapture` 用 Prefix 捕捉那个参数（它是**参数**，`Message` 上没有字段），
    # 行带 `Historical`，应用端原样交给 `Messages.Message`；同一行带目标格（`Map/X/Z`，**-1 才是「没有目标」**，
    # 0 是首张地图的合法 id），点击副本消息才会移动镜头。
    # 实测 `message-archive`：档 1 → 两条消息后 2（一条入档、一条只上屏）、入档条标记 true、只上屏条 false、
    # 目标格随行带来（Map=0 X=125 Z=125 与期望格一致）、应用 2 行其中入档 1、活列表 2 → 4、清理后回到 1 条。
    # `ArchivedDialog` 仍是 `-`：载入时深拷贝的只读对话框，没有写入点。
    if typ == 'Archive':
        return 'x', ('MessageHistoryCapture（第 163 轮）：`Messages.Message` 的 historical 参数由 Prefix 捕捉，'
                     '行带 Historical 与目标格（Map=-1 表示无目标，0 是首张地图）；实测副本档案与点击目标都对齐')
    if typ.startswith(('ArchivedDialog',)):
        return '-', '载入时深拷贝的只读对话框，没有写入点；不影响模拟，副本无此输入'
    # **第 164 轮：`UnnaturalCorpse` 判 `~`，不是 `-`。** `_lab/r164_scribed_audit.py` 把它列进「Scribe 标签在任何
    # 通道里都不出现」的十八行之一：`tracker` / `teleport` 两个标签在 `Source/*.cs` 里一次都没有。
    # `ResidualCounterSync.research` 只带 tracker 的研究进度一个数（`residual-count` 探针），而 tracker 自己还有
    # `awakenTick`/`disappearTick`/`spawnedTick`/`escape`/`mentalBreak`/`haunted`/`awokenPawn`/`ticksToKill`/
    # `researchToDestroy`/`teleportIn`（`:50860-50874`），全是运行期改的。副本上这具尸体因此永远停在「未觉醒」
    # 那一态：`UnnaturalCorpse` 的交互/文本按 tracker 回答，觉醒后的样子在副本上永远不会出现。
    # **第 171 轮：视觉载体单独一条通道。** 地图差异必须继续跳过整个 `Ethereal` 类别（`SignalAction_*`/`RectTrigger` 是
    # 逻辑载体，副本生成会二次触发；`OrbitalStrike.Tick` 自己调爆炸），于是三个纯视觉类的炮击/残骸/龙卷风此前从不到达副本。
    # `EtherealVisualSync` 只带计时器，并在应用后把 `ticksToNextEffect` 压到 `int.MaxValue`。实测 `发 1 / 应用 1（未解析 0）`，
    # 销毁夹具后仅该行能重建它（0→1），移除后回到 0。
    # **第 173 轮：`PlaySettings` 的「玩法那一半」随帧走。** 面板方法本身是 UI，但同一份对象里有 `useWorkPriorities`/
    # `autoHomeArea`/`autoRebuild` 与十一个 `defaultCareFor*`；`MedicalCareUtility` 那边的解析（`:477541-477580`）对每个
    # 没有显式设定的小人都读它们。`PlaySettingsSync` 只带这十四个字段，显示开关按端各自保留。实测 `prisoner 3`：
    # 行 `Care=4,3,2,2,2,2,2,2,2,1,1`（第 1 段 3）、不发地写回旧值后读 2、应用该行后读 3、复原 2。
    if typ == 'PlaySettings':
        return 'x', ('PlaySettingsSync（第 173 轮）：useWorkPriorities/autoHomeArea/autoRebuild + 十一个 '
                     'defaultCareFor*（byte 枚举按 Best 夹紧）；显示开关按端各自保留；'
                     '实测 `prisoner 3` 四段全准（发 2 / 应用 1）')
    if typ in ('Bombardment', 'Tornado', 'OrbitalDebrisSpawner'):
        return 'x', ('EtherealVisualSync（第 171/172 轮）：载荷是弹丸列表 '
                     '（`Bombardment.BombardmentProjectile` 的 lifeTime/maxLifeTime/targetCell），随行编码重建；'
                     '副本端不 tick、`ticksToNextEffect` 压到 int.MaxValue 只画不炸；'
                     '实测行带 `弹丸 37:60:125:125;12:40:120:130`、销毁后仅该行能重建（0→1、弹丸 2 颗）')
    if typ == 'UnnaturalCorpse':
        return 'x', ('CorpseTrackerSync（第 166 轮）：tracker 的十一字段随帧走（awakenTick/disappearTick/spawnedTick/ticksToKill/haunted/'
                     'awokenPawn/escape/mentalBreak/teleportIn/research/researchToDestroy），身份用尸体 thingIDNumber；实测四段读数全准')
    if typ.startswith('StoryWatcher'):
        return '~', '事件/档案（副本不产生；已发生由 TaleSync/MemorySync 或整份片段承载，逐条待核）'
    # 派生值：`ThoughtHandler` 的情绪加总、`Thought_Situational.RecalculateState`、`Thought_IdeoRoleEmpty`。
    # `MemorySync` 的文档里已经写明这条判断：`Need_Mood.CurInstantLevel` 是 getter，它从
    # `thoughts.TotalMoodOffset()` 重算（`:466885-466903`），而那个 getter **按 `TicksGame` 缓存** ——
    # 副本的 `TicksGame` 被补丁成 `Barrier.AppliedTick`，每帧前进，所以每帧都会重算一次。
    # 三个输入分别是 thoughts（`MemorySync`）、`difficulty.colonistMoodOffset`（`StorytellerSync`）、
    # `health.hediffSet.OverrideMoodBase`（hediff 通道）。**修输入就够了，mood 值本身不需要传输** ——
    # 与 `PlantSync` 发阶段而不发生长度是同一个形状。归 `~` 而不是 `x`：机制成立，但"每个情绪源都有通道"是逐条主张。
    #
    # **第 105 轮：这五条读完了，全部归 `x`，理由分两种。**
    #
    # 一、`GetAllMoodThoughts`（`:482064`）返回的是**两半**：`memories.Memories` 与
    #     `situational.AppendMoodThoughts`。前一半是 `MemorySync` + `ReplicaPawn.Thoughts` 送过来的数据；
    #     后一半是 `def.Worker.CurrentState(pawn)` 算出来的派生值。实测（`thoughts probe` #873）：
    #     `缓存 0 条（thoughtsDirty=True）` → 读取后 `缓存 2 条`，面板列出 `NewColonyOptimism`（记忆）与
    #     `Expectations`（情境）。**派生本身是对的**，它只读已经到达的输入。缺的是**调度**：
    #     `thoughtsDirty` 只在 `Notify_SituationalThoughtsDirty`（`:482004`）里置真，而唯一的定期重算是
    #     `ThoughtHandler.ThoughtInterval`（`:482044`）← `Need_Mood.NeedInterval`（`:466955`）←
    #     `NeedsTrackerTickInterval`（`:467932`）的 `IsHashIntervalTick(150)`。副本不 tick，所以第一次读完就冻住。
    #     补法：`ApplyThoughts` 里按同一个 `IsHashIntervalTick(150)` 调 `situational.SituationalThoughtInterval()`
    #     —— 用的就是 vanilla 自己调度的方法（就地重算），不是 `Notify_*`（那会清空再重建）。
    #
    # 二、`IndividualThoughtToAdd.Add` / `ThoughtToAddToAll.Add` 是**取用点**：`Add()` 里只有
    #     `memories.TryGainMemory(...)`，而内存本身就是 `MemorySync` 送的列表。`Thought_IdeoRoleEmpty.
    #     CurrentStateInternal` 的每个输入都有通道：`pawn.IsSlave`（GuestSync）、`GenDate.DaysPassed`（共享时钟）、
    #     `Role.Active` 与 `Role.ChosenPawnSingle()`（PreceptSync 的 `@holder` 段）。
    #     `Thought_Situational.RecalculateState` 是纯谓词调用点，输入齐了就成立。
    if typ.startswith(('ThoughtHandler', 'Thought_Situational', 'Thought_IdeoRoleEmpty', 'ThoughtToAddToAll',
                       'IndividualThoughtToAdd', 'SituationalThoughtHandler', 'MemoryThoughtHandler')):
        return 'x', ('两半已分：记忆=MemorySync/ReplicaPawn.Thoughts，情境=按 `IsHashIntervalTick(150)` 调 '
                     '`SituationalThoughtInterval()` 就地重算（第 105 轮）；各派生输入的通道已逐条核过')
    # 界面页自己会写全局设置的那些 —— 是最容易被归进「界面输入」的真缺口。
    #
    # `MainTabWindow_Work.DoManualPrioritiesCheckbox`（`:675248-675265`）写
    # `Current.Game.playSettings.useWorkPriorities`，而这个字段是 `Pawn_WorkSettings.GetPriority`
    # （`:331069-331077`）的乘法系数：它为 false 时任何非零优先级都返回 3。**它决定每个小人干什么活、按什么顺序**。
    # `MainTabWindow_Research.AttemptBeginResearch`/`DrawStartButton` 看起来同类，但它的写操作
    # `DoBeginResearch`（`:674203-674212`）只调 `ResearchManager.SetCurrentProject`，而那条已被
    # `ResearchManagerSetCurrentProjectPatch`（`Source/SharedActions.cs:1874-1877`）覆盖 —— 所以研究页是 `x`。
    #
    # 已建第 55 条通道 `PlaySettingsSync`（三个仿真输入），实测 `settings-probe`：
    # `useWorkPriorities=关，autoHomeArea=开，autoRebuild=关；4 个人形，优先级被压成单一 3 的 4 个`。
    if typ == 'MainTabWindow_Work':
        return 'x', 'PlaySettingsSync（第 55 条通道送 useWorkPriorities/autoHomeArea/autoRebuild）'
    if typ == 'MainTabWindow_Research':
        return 'x', '写操作是 SetCurrentProject，已由 ResearchManagerSetCurrentProjectPatch 覆盖（其余是纯查询）'
    # 最后三条，逐条定而不是归族。
    #
    # `Page_ChooseIdeoPreset.AssignIdeoToPlayer`/`DoNext` —— 开局选信念页的翻页与赋值，只在新建殖民地时走一次，
    #   副本从不走开局流程（它从快照装载）。
    if typ == 'Page_ChooseIdeoPreset':
        return 'x', '开局选信念页（只在新建殖民地时走；副本从快照装载）'
    # `PawnComponentsUtility.AddAndRemoveDynamicComponents` —— 这条是**已知未解决**，不是缺口也不是覆盖：
    #   它按阵营/DLC 增删十二个动态 tracker（`:445343`），调用者是 `Pawn.SetFaction`（`:129494`）和生日
    #   （`:135063`），副本没有等价物。`tracker-probe` 实测 `playerSettings×4` vs `foodRestriction×3` 与
    #   「三个主人 + 一个客人」一致，但**未被证明**：这份存档没有阵营变更发生，无法观测 tracker 集合落后。
    #   归 `~`，理由写在这里，避免下一个人把它当已解决。
    if typ == 'PawnComponentsUtility':
        # 2026-11 更新：这条依赖的前置条件变了。派系现在随 `ReplicaPawn.FactionLoadId` 同步，
        # 所以"阵营变更后 tracker 集合落后"的**触发条件已经成立**（副本能看见派系真的变了）。
        # 但补调它依然不行，理由换了：`AddAndRemoveDynamicComponents` 会
        # `pawn.training = new Pawn_TrainingTracker(pawn)` 之类地**新建空 tracker**，
        # 把权威端计算好的内容抹掉 —— 而 tracker 的内容（训练进度、药物政策、时间表、服装）本身还没有通道。
        # 先有内容通道，再谈结构对齐；顺序反过来会把"结构对了但内容全丢"当成修好。
        # 2026-11 再更新：`Pawn_TrainingTracker` 的内容已经有通道（`PawnTrackerSync` 的 `w:` 行），
        # 所以这条的"先要内容"条件对**训练**这一项已经满足。但 `AddAndRemoveDynamicComponents` 新建的是
        # **整组** tracker，其中药物政策（`Pawn_DrugPolicyTracker`）、时间表（`Pawn_TimetableTracker`）、
        # 服装（`Pawn_OutfitTracker`）、食物限制（`Pawn_FoodRestrictionTracker`）的内容通道走的是
        # `policy` 命令（玩家改面板时发布），**不是**每帧状态 —— 也就是说副本上的值只在玩家动过面板后正确，
        # 新建空 tracker 会把一个从未被玩家改过的默认值留下。
        # 结论不变，但理由窄化成"其余四项的默认值无法从权威端重建"。
        # **第 108 轮：这条判定不成立，已补实现。** 旧理由假设「补调会新建空 tracker，抹掉内容」。
        # 读 `AddAndRemoveDynamicComponents`（`:445343`）的全部 141 行：**六个** policy tracker 都是
        # 「仅在 null 时创建」——`foodRestriction`（`:445388`）、`outfits`（`:445394`）、`drugs`（`:445398`）、
        # `timetable`（`:445402`）、`reading`（`:445406`）、`inventoryStock`（`:445410`）；
        # `training`（`:445464`）虽然是无条件赋值，但外层也有 `pawn.training == null` 守卫，且它的内容在
        # `PawnTrackerSync` 的 `w:` 行里。会被置 null 的只有 `trader`/`mechanitor`/`learning`/`drafter`，
        # 而那四个正是 `tracker-probe` 本次实测报告为 0 的（`零载体 trader,mechanitor,learning`）。
        # 另一方面，结构本身就是状态：`flag = Faction.IsPlayer` 决定 `foodRestriction`/`playerSettings` 是否存在
        # （`:445388`/`:445460`），而副本走的是 `SetFactionDirect`（`:157883`），它**不调用**这个方法。
        # 所以补调它既安全又是必需的。`tracker-probe` 实测：`foodRestriction×3 / outfits×3 / drugs×3 /
        # timetable×3 / reading×3 / inventoryStock×3` vs `playerSettings×4`、`training×1` ——
        # 差的正是「谁是玩家阵营」这一个谓词。
        return 'x', ('第 108 轮在 `ApplyPawnFaction` 里补调 `AddAndRemoveDynamicComponents`；'
                     '六个 policy tracker 仅在 null 时创建，不会抹内容；被置 null 的四个正是本存档为 0 的')
    # `ReleaseAnimalToWildUtility.DoReleaseAnimal` —— 把动物放归，改阵营并把 pawn 移出地图：
    #   阵营由 `FactionSync` 送，移动/移除由 `Changed`/`removed` 帧带，剩下的只有「玩家点了放归」这个入口。
    if typ == 'ReleaseAnimalToWildUtility':
        return '~', '玩家动作入口（阵营由 FactionSync、移除由 removed 帧；只剩入口需单独命令）'
    # 剩下 49 条按「谁改谁」再分四组。
    #
    # ① 权威端专有的整机流程，与前面 `LoadedModManager` 那组同性质：读档/开局/世界生成/存档装载。
    #    `World.ExposeData`/`FillComponents`（世界存档本体）、`WorldObjectsHolder.Add`（世界对象登记——
    #    **第 77 轮改判**：此前这里的理由写的是「由 WorldObjectSync 按 id 重建」，那是错的。
    #    `WorldObjectSync` 只**移动**已存在的对象，它按 id 查不到就静默丢弃，从不创建。真正承载登记的是
    #    `WorldObjectLifecycleSync`，它发整份 Scribe 片段并在副本上走 `WorldObjectsHolder.Add`）、
    #    `GravshipUtility.GenerateGravship`、
    #    `NamePlayerFactionAndSettlementUtility.CanNameAnythingNow`、`Page_ChooseIdeoPreset.DoNext`/
    #    `AssignIdeoToPlayer`（开局选信念页）。
    if typ in ('World', 'WorldObjectsHolder', 'NamePlayerFactionAndSettlementUtility', 'GravshipUtility'):
        return 'x', ('世界存档/开局流程（权威端专有；世界对象的**创建与销毁**现由 WorldObjectLifecycleSync 承载，'
                     '位置由 WorldObjectSync、状态由 WorldObjectStateSync）')
    # ② 纯谓词/查询，或只读一次已有状态：
    #    `RepairUtility.PawnCanRepairEver`/`PawnCanRepairNow`、`QuestUtility.CanAcceptQuest`/
    #    `CanPawnAcceptQuest`/`TryGetIdealColonist`、`QuestScriptDef.CanRun`、
    #    `SurgeryOutcomeEffectDef.GetQuality`、`HealthAIUtility.ShouldBeTendedNowByPlayer`、
    #    `RecipeWorkerCounter.CountValidThing`、`AnimalPenUtility.CheckUseAndReach`、
    #    `GiveToPackAnimalUtility.CarrierCandidatesFor`、`RitualObligationTargetFilter.CanUseTarget`、
    #    `PawnGenerator.IsValidCandidateToRedress`、`Tradeable.GetPriceFor`、`CanAutoSlaughterNow`、
    #    `InteractionWorker_ConvertIdeoAttempt.RandomSelectionWeight`、`Job.GetReport`、
    #    `TradeUtility.AllLaunchableThingsForTrade`、`VoluntarilyJoinableLordsStarter` 的 tick 内判定。
    if typ in ('RepairUtility', 'QuestUtility', 'QuestScriptDef', 'SurgeryOutcomeEffectDef', 'HealthAIUtility',
               'RecipeWorkerCounter', 'AnimalPenUtility', 'GiveToPackAnimalUtility',
               'RitualObligationTargetFilter', 'PawnGenerator', 'TradeUtility', 'AutoSlaughterManager',
               'InteractionWorker_ConvertIdeoAttempt', 'Job', 'VoluntarilyJoinableLordsStarter',
               'Pawn_MeleeVerbs_TerrainSource', 'HaulAIUtility', 'DropCellFinder', 'LargeBuildingCellFinder',
               'MapInterface', 'MainTabsRoot', 'PawnColumnWorker_Timetable'):
        return 'x', '纯谓词/查询或界面输入（不改变世界状态）'
    # ③ 已由现有通道覆盖的对象，被当作「缺口」只是因为它自己不做同步：
    #    `Tradeable.ResolveTrade` —— 交易结果落在物品与白银上，经 `Created`/`Removed` 帧；
    #    `ThingOwner.Notify*` —— 容器内容随物件的 `innerContainer` 片段（`ThingOwner` 在 `Thing.ExposeData` 里）；
    #    `ListerBuildings.RegisterInstallBlueprint`/`DeregisterInstallBlueprint` —— 索引维护族；
    #    `SettlementData.Set` —— 据点表由 `SettlementSync` 整份送；
    #    `Pawn_PsychicEntropyTracker.SetInitialPsyfocusLevel` —— 只在生成 pawn 时用，之后随 pawn 片段；
    #    `CompHasGatherableBodyResource.Gathered` —— 产物经 `Created`，资源量随物件片段；
    #    `CompApparelVerbOwner.Initialize`/`SetupVerbs` —— 装配时构建，`Initialize` 由 `InitializeComps` 调；
    #    `DamageWorker.ExplosionStart` —— 伤害经 hediff 通道，爆炸产物经 `Created`；
    #    `HediffComp_SurgeryInspectableMetalhorror.DoSurgicalInspection` —— hediff 通道。
    # **灵能熵限（第 142 轮实测）。** 这一行原先落进下面 ③「效果落在已有通道的输出上」——对它真正的写点是对的
    # （`SetInitialPsyfocusLevel` 只在生成 pawn 时用），但同一类型里还有 `limitEntropyAmount`（`:473397`，scribed
    # `:473805`）：玩家在灵能 gizmo 上点的那个锁（`:474043-474046`），被 `WouldOverflowEntropy`
    # （`:473640-473641`）读来决定「冥想还能不能把熵推过上限」，而冥想跑在权威端。改前 `Source/` 里
    # `limitEntropyAmount` 零引用 ⇒ 副本上解锁之后，权威端仍在旧答案里拦。现在随每小人追踪器行（`y:` 段）送，
    # 实测两端一致（发 1 行、应用计数 +1）。
    if typ == 'Pawn_PsychicEntropyTracker':
        return 'x', ('状态通道:PawnTrackerSync（`y:limitEntropyAmount` 段，第 142 轮实测：副本改成解除后'
                     '应用准、应用计数 +1）；`SetInitialPsyfocusLevel` 只在生成 pawn 时用')
    if typ in ('Tradeable', 'ThingOwner', 'ListerBuildings', 'SettlementData',
               'Pawn_PsychicEntropyTracker', 'CompHasGatherableBodyResource', 'CompApparelVerbOwner',
               'DamageWorker', 'HediffComp_SurgeryInspectableMetalhorror', 'Skyfaller', 'GenLeaving'):
        return 'x', '效果落在已有通道的输出上（物品/容器/据点表/hediff/索引）'
    # ④ 玩家动作入口，效果落在别的对象上 —— 需单独命令，载体待实测（与 `RecruitUtility` 那组同类）：
    #    `Precept_Ritual.AddObligation`/`ShowRitualBeginWindow`、`IdeoDevelopmentUtility.ApplyChangesToIdeo`、
    #    `FactionIdeosTracker.RecalculateIdeosBasedOnPlayerPawns`/`SetPrimary`、
    #    `SettlementProximityGoodwillUtility.CheckConfirmSettle`/`AppendProximityGoodwillOffsets`/
    #    `AppendGravshipAttackOffsets`、`SettlementUtility.AffectRelationsOnAttacked`、
    #    `Blueprint.TryReplaceWithSolidThing`/`Blueprint_Build.MakeSolidThing`（建造完成由权威端 tick）、
    #    `AutoBuildRoofAreaSetter.TryGenerateAreaNow`、`GravshipLandingMarker.BeginLanding`、
    #    `Gravship.TickInterval`、`Building_GravEngine.*`、`VoluntarilyJoinableLordsStarter`。
    if typ in ('Precept_Ritual', 'IdeoDevelopmentUtility', 'FactionIdeosTracker',
               'SettlementProximityGoodwillUtility', 'SettlementUtility', 'Blueprint', 'Blueprint_Build',
               'AutoBuildRoofAreaSetter', 'GravshipLandingMarker', 'Gravship', 'Building_GravEngine',
               'TradeUtility'):
        return '~', '玩家动作入口/权威端 tick（效果落在别的对象上；需单独命令，载体待实测）'
    # Z-OTHER 里成族的部分。每条都指向具体代码，不是按名字猜。
    #
    # ① 只读派生值（问一个已有状态，不写它）：`FactionUtility.IsPoliticallyProper`、
    #    `FeedPatientUtility.ShouldBeFed`、`FishingUtility.GetCatchesFor`、`HumanEmbryo.CanImplantReport`、
    #    `OwnershipContext.JobAllowed`、`TrashUtility.ShouldTrashBuilding`、`GenStep_Scatterer.ShouldSkipMap`、
    #    `TransporterUtility.IncomingTransporterPreventingMapRemoval`、`JoyUtility.JoyKindsOnMapTempList`、
    #    `WealthWatcher.WealthItemsFilter`、`StorytellerUtility.DefaultParmsNow`/`DefaultThreatPointsNow`。
    if typ in ('FactionUtility', 'FeedPatientUtility', 'FishingUtility', 'HumanEmbryo', 'OwnershipContext',
               'TrashUtility', 'GenStep_Scatterer', 'TransporterUtility', 'JoyUtility', 'WealthWatcher',
               'StorytellerUtility', 'PawnUtility', 'GenGuest'):
        return 'x', '只读派生值（查询已有状态，不写它）'
    # ② 权威端专有的整机流程：读档/开局/结局/计数播报。副本永远不走这条路。
    #    `LoadedModManager.WriteModSettings`、`LongEventHandler.QueueLongEvent`/`ExecuteWhenFinished`、
    #    `Root_Play.Update`/`SetupForQuickTestPlay`、`GameVictoryUtility.ShowCredits`、
    #    `ArchonexusCountdown`、`VoidAwakeningUtility.CloseMetalHell`、`StoryState.RecordPopulationIncrease`、
    #    `PrefsData.Apply`、`LoadedObjectDirectory.RegisterLoaded`、`Page.DoBottomButtons`、`DebugActionNode.Enter`、
    #    `Message.ResetTimer`、`Messages.AcceptsMessage`、`Optio` 若出现同类。
    if typ in ('LoadedModManager', 'LongEventHandler', 'Root_Play', 'GameVictoryUtility', 'ArchonexusCountdown',
               'VoidAwakeningUtility', 'StoryState', 'PrefsData', 'LoadedObjectDirectory', 'Page',
               'DebugActionNode', 'Message', 'IncidentParms', 'SignalManager'):
        return 'x', '权威端专有的整机流程（读档/开局/结局/UI 框架/存档装载器），副本不走'
    # `Messages` 不在这组里：`Messages.Message(msg, historical)` 是副本上**真的会跑**的门，
    # `LetterSync.Apply` 就是调它把消息放上屏（第 163 轮）。顺带查明原版 `AcceptsMessage`（`:185800`）
    # 是「文本相同 + 目标相同」的去重，且在入档分支**之前**返回 ⇒ 同一条消息重复应用不会重复入档，
    # 这也是探针必须用带后缀的行去测应用半程的原因。
    if typ == 'Messages':
        return 'x', ('LetterSync（第 163 轮）：Message(Message, bool) 是副本上真正跑的门，historical 由'
                     ' MessageHistoryCapture 提供；AcceptsMessage 是原版自己的去重（文本+目标，同帧）')
    # ③ 已由现有通道或现有补丁覆盖，逐条有依据：
    #    `ResourceCounter.UpdateResourceCounts` —— `Source/AuthoritativeReplica.cs:1369` 按 204 tick
    #      或有创建/移除/移动时重算（带 try/catch，因为 `GetInnerIfMinified` 在空容器 minified 物件上会抛）；
    #    `Pawn_StanceTracker.SetStance`/`StanceTrackerDraw` —— 姿态随 pawn 片段的 stance 字段走；
    #    `Pawn_MeleeVerbs.ExposeData` —— 随 pawn 片段；`TryGetMeleeVerb`/`ChooseMeleeVerb` 是 AI 侧；
    #    `Skyfaller.Impact` —— 落地时创建物件，经 `Created` 帧；
    #    `GenLeaving.DoLeavingsFor`/`DropThingsNear` —— 产物同样经 `Created`；
    #    `CustomXenogermDatabase.Add`/`Remove` —— 与 `DiscoverySync` 同形的 `Game` 子系统（本存档 0 载体）；
    #    `AnalysisManager.TryIncrementAnalysisProgress` —— `Game.analysisManager`（`:50170`）同形；
    #    `IncidentQueue.Add` / `GameConditionManager.RegisterCondition` —— 权威端的袭击/天气调度，
    #      结果经 `FactionSync`/天气通道与信件；
    #    `GoodwillSituationManager.GetSituations`/`Recalculate` —— 好感值本身由 `FactionSync` 送。
    if typ in ('ResourceCounter', 'Pawn_StanceTracker', 'Pawn_MeleeVerbs', 'Skyfaller', 'GenLeaving',
               'CustomXenogermDatabase', 'AnalysisManager', 'IncidentQueue', 'GameConditionManager',
               'GoodwillSituationManager', 'Trigger_PawnHarmed'):
        return 'x', '已由现有通道/补丁覆盖（逐条注明依据）'
    # ④ 玩家动作入口，效果落在别的对象上 —— 需要单独命令，实测载体后才建：
    #    `RecruitUtility.Recruit`、`PawnBanishUtility.Banish`、`Pawn_TrainingTracker.Train`、
    #    `Pawn_MutantTracker.Turn`、`SettleUtility.AddNewHome`、`Xenogerm.SetTargetPawn`、
    #    `PawnUtility.TrySpawnHatchedOrBornPawn`。
    # `Pawn_TrainingTracker` 单独一条，理由与同组其余六个不同：
    #   训练**内容**（`steps`/`learned`/`wantedTrainables`）与那个倒计时是两件事 —— 倒计时早就有通道
    #   （`PawnTrackerSync` 的 `t:` 行发的是 `countDecayFrom` 换算出的剩余 ticks），而内容一直没有任何载体。
    #   所以副本上出现的是「倒计时在动、勾选框和步数停在载入存档那一刻」。两半现在都通了：
    #   内容随 `PawnTrackerSync` 的 `w:` 行走（`TrainingCardUtility.DoTrainableCheckbox` `:648116-648137`
    #   读到的 wanted 是玩家输入，不进这条行），勾选走 `training-wanted` 命令 + `TrainingCheckboxCapturePatch`
    #   （`:648116` 是静态控件，既不是 gizmo 也不是 designator，`GizmoStatePatch` 永远看不到它）。
    #   实测 `training-probe`：`幂等往返 一致（w:Tameness=* 回灌后仍是 w:Tameness=*）`，
    #   `应用 1 / 训练行 1（1 条）`；`patch-inventory` 确认 `TrainingCardUtility.DoTrainableCheckbox` 已挂补丁。
    if typ == 'Pawn_TrainingTracker':
        return 'x', ('内容随 PawnTrackerSync 的 w: 行（幂等往返已实测）；勾选走 training-wanted '
                     '+ DoTrainableCheckbox 补丁（静态控件，gizmo 补丁看不到）；'
                     'attackTarget 随 a: 行（成对写 attackTarget + mindState.enemyTarget，'
                     '清除时还中断近战 job，attack-probe 往返实测）')
    if typ in ('RecruitUtility', 'PawnBanishUtility', 'Pawn_MutantTracker',
               'SettleUtility', 'Xenogerm', 'PawnUtility'):
        return '~', '玩家动作入口（效果落在别的对象上，需单独命令；载体未逐条实测）'
    # 索引维护族：注册/注销只是把它自己那本字典和格子表维护好，对象本身由别的路径创建并随
    # `Created`/`Removed` 帧走。这一族在副本上由「重建该对象的那条通道」顺手调用，不需要独立入口。
    #   `ZoneManager.RegisterZone`（`:95161-95165`）做 `allZones.Add` + `PostRegister`，
    #     副本侧由 `SharedActions` 的建区路径调用（`Source/SharedActions.cs:1248`）；
    #   `PlanManager.DeregisterPlan`/`PlanAt`/`TryGetPlan` 由 `PlanSync.Apply`（按 id 整份同步 + 版本号）重建；
    #   `ListerThings.Remove` 由 `DiffThings` 的 `removed` 集合驱动（`Source/HeadlessRuntime.cs:1376-1380`）。
    if typ in ('ZoneManager', 'PlanManager', 'ListerThings', 'RegionGrid', 'EdificeGrid', 'GlowGrid',
               'RoofGrid', 'FogGrid', 'SnowGrid', 'TemperatureGrid'):
        return 'x', '索引维护（对象本身由创建/移除帧与对应通道重建，索引随之维护）'
    # 收养（`Designator_Adopt.DesignateThing` `:618102-618128`）：改派系 + 占床 + 发信，**一条命令都不发**。
    # 派系那半经 `ReplicaPawn.FactionLoadId` 到达；占床那半是 `CompAssignableToPawn.assignedPawns`
    # （`ExposeData` `:529593-529594` 以引用 scribe），而 `GizmoStatePatch` 只比较**选中物件**——收养选的是
    # 小人、改的是床，床不在选中集，所以它落在「改动落在别的对象上」这一类，任何现有通道都盖不到。
    # 发信那半由 `LetterSync` 的抉择信件通道承载。
    if typ == 'Designator_Adopt':
        return '~', '占床落在床物件上（assignedPawns），GizmoStatePatch 只见选中物件；派系经 FactionLoadId 到达，占床无通道'
    if typ.startswith('CompAssignableToPawn'):
        # **第 107 轮：旧理由里的「GizmoStatePatch 覆盖」是错的。** `CompGetGizmosExtra`（`:529569`）只做一件事：
        # `Find.WindowStack.Add(new Dialog_AssignBuildingOwner(this))`（`:529579`）。两个写点都在对话框里，
        # 是按钮不是 `Command.ProcessInput`：`assignable.TryAssignPawn(pawn)`（`:625635`）、
        # `assignable.TryUnassignPawn(pawn)`（`:625578`）。与第 101 轮 banish/recruit/release 同形。
        # 捕 `virtual` 入口而不是基类字段：三个子类整段替换了 `TryAssignPawn` 并转调 `pawn.ownership.Claim*`
        # （`:529069`/`:529137`），而 `Pawn_Ownership.ExposeData`（`:473155-473159`）才是记录它的地方。
        # 所以一次采样两个对象：可分配物件（`assignedPawns` 在 `:529593`）与被指派的 pawn。
        return 'x', ('第 107 轮补了 Dialog_AssignBuildingOwner 两个按钮的捕获（`:625635`/`:625578`）；'
                     '按 virtual 入口捕，覆盖三个转调 pawn.ownership 的子类；两端都采样')
    # 组件族按「效果落在哪」再分三类。这一分法的判据是 `GizmoStatePatch` 的真实覆盖范围：
    # 它比较的是**选中物件点击前后的片段**，所以「gizmo 改了它自己挂的物件」免费覆盖，
    # 「改了别的 pawn / 创建销毁 / 改 Game 子系统」才是缺口。`comp-probe` 已证明 comp 字段平铺进物件节点，
    # 所以下面第一类不需要任何通道。
    #
    # ① 改自己的 comp 字段（随物件片段走，GizmoStatePatch 已覆盖）：`CompBandNode.TuneTo` 写
    #    `tuningTimeLeft`/`tuningTo`（`:530230-530234`）—— 两个都是本物件的 comp 字段。
    if typ in ('CompBandNode', 'CompAffectedByFacilities', 'CompMeditationFocus',
               'CompHackable', 'CompScanner', 'CompAutoCut', 'CompPilotConsole'):
        return 'x', '组件自身字段（随物件片段走；谓词类本就只有查询）'
    # ③ 这六条的入口逐条查过（`_lab/chokepoints.py`：按声明基类判定调用者种类，不按名字匹配）：
    #
    #    `CompUsable.UsedBy`              ← `JobDriver_UseItem.Use`                → 工作驱动，走 TryTakeOrderedJob
    #    `CompTargetEffect_Resurrect.DoEffectOn` ← `Verb_CastTargetEffect.TryCastShot` → 施法，走 gizmo
    #    `CompPsylinkable.FinishLinkingRitual`  ← `RitualOutcomeEffectWorker_AnimaTreeLinking.Apply` → 仪式 gizmo
    #    `CompAnalyzable.OnAnalyzed`      ← `JobDriver_AnalyzeItem.GetStudyToils` → 工作驱动
    #    `CompAbilityEffect_Farskip.AlliedPawnOnMap` ← `CompAbilityEffect_Farskip.Apply` → 能力 gizmo
    #    `CompGoldenCube.GiveHediff`      ← `CompGoldenCube.CompTickInterval`     → **无玩家入口**
    #
    # 前五条落到三个已存在的收口点（`OrderCapturePatch` / `GizmoStatePatch` / `FloatMenuChosenCapturePatch`），
    # 每个都已在实机上产生过读数，属 `x`。第六条不是入口：它只被权威端自己的 tick 与任务生成调用，
    # 是「触发在权威端」的正常形态，不是缺口也不是待核。
    #
    # 这条注释替换了原先的整类 `~`：那 6 条的理由写的是「副本点击是否被拦下未逐条测」，而「未测」不是判据。
    # 判定方法本身也留在 `chokepoints.py` 里可重跑 —— 三次写错（数括号、裸名调用图、CRLF 导致全 UNRESOLVED）
    # 都记在它的文档里，因为它们都以「找不到方法体」的形式出现，与「方法不存在」无法区分。
    if typ in ('CompTargetEffect_Resurrect', 'CompPsylinkable', 'CompAbilityEffect_Farskip',
               'CompAnalyzable', 'CompUsable'):
        return 'x', '入口经 gizmo/工作驱动收口点（chokepoints.py 按调用者基类判定）'
    if typ == 'CompGoldenCube':
        return '-', '触发在权威端 tick（CompTickInterval / 任务生成），副本无此入口'
    # ③ 改 Game 子系统：`Game.analysisManager`（`:50170`，与 `hiddenItemsManager`/`entityCodex` 同形），
    #    只在扫描台使用时变，副本不产生该事件 —— 属 `DiscoverySync` 那一类可扩展字段，不是缺口。
    # 派系：`FactionSync` 只带**派系之间**的关系（好感度/关系种类），从不带"哪个小人属于哪个派系"。
    # 而 `ReplicaPawn` 之前没有 `Faction` 字段 —— 小人的派系只在载入那一刻正确，之后权威端一改就永久漂移。
    # 现在 `ReplicaPawn.FactionLoadId` 带着它，应用走 `SetFactionDirect`（`:157883`）而不是 `SetFaction`
    # （`:129460`，那是转移：会重置工作优先级、清手术单、重置医疗护理），并更新 `MapPawns` 与
    # `attackTargetsCache` 两个按派系索引的缓存。
    if typ in ('Pawn_SetFaction', 'SetFaction', 'Faction'):
        return 'x', '派系随 ReplicaPawn.FactionLoadId 下发；应用走 SetFactionDirect 并刷新两个索引缓存'
    # 区域：`Area` 既不是 zone 也不是 thing，住在 `Map.areaManager.AllAreas`（`:64076`），
    # 内容是每张区域一个 `BoolGrid`（`Area.ExposeData` `:63989`）。`ZoneSync` 只带 `Zone`，
    # 所以在 `AreaSync` 之前副本上的家区域一直是空的 —— 而空家区域会改变温度查询、扫雪派工、
    # 屋顶施工许可和小人的区域限制（`Pawn_PlayerSettings.area` `:103386`）。
    #
    # `AutoHomeAreaMaker` 要排在通用 `Notify_*` 规则**之前**：它是 `Notify_*` 形状，但原因完全不同。
    # `ShouldAdd()`（`:685417-685424`）只看 `Find.PlaySettings.autoHomeArea` 和
    # `Current.ProgramState == Playing`，**不检查是不是副本**。设置被 `PlaySettingsSync` 同步之后，
    # 副本也满足这个检查，于是每生成一栋建筑就往本地家区域盖一圈边框，而 `AreaSync` 的差异应用下一帧
    # 会把它们清掉 —— 边框闪烁。补丁打在叶子 `MarkHomeAroundThing`（三个 Notify_* 都汇到它，且它自己是
    # public），安装名 `replica-home-area-suppress`，`patch-inventory` 已确认。
    if typ == 'AutoHomeAreaMaker':
        return 'x', '副本侧的家区域扩展被 replica-home-area-suppress 拦下（否则每帧与 AreaSync 的差异应用互相打架）'
    if typ in ('Area', 'AreaManager', 'Area_Home', 'Area_BuildRoof', 'Area_NoRoof',
               'Area_SnowOrSandClear', 'Area_PollutionClear'):
        return 'x', '内建区域格子由 AreaSync 按类型整张发送（id 来自 UniqueIDsManager，两端不同，不能按 id 发）'

    # `Area_Allowed` 是玩家自建的工作限制区域，与上面四个内建区域**不是同一个缺口**：
    # 内建区域有固定类型槽位，自建区域是一个最多 10 条的列表（`CanMakeNewAllowed` `:64211-64214`），
    # 每条带玩家取的名字，id 同样来自本端 `GetNextAreaID()`（`:63996`）。四条写入口过去全落在本端：
    # ① 新建 `Dialog_ManageAreas` 的 NewArea/Copy（`:635395`、`:635463`）→ `TryMakeNewAllowed`（`:64216`）；
    # ② 改名 `Dialog_Rename` 写 `RenamableLabel` 后调 `OnRenamed`（`:196318`），无人监听；
    # ③ 涂格 `Designator_AreaAllowedExpand/Clear.DesignateSingleCell`（`:618202-618205`、`:618226-618229`）
    #    整个方法体就是 `SelectedArea[c] = true/false`，而 `DesignatorSync.EncodeChunks` 只认
    #    `Designator_Build` 和 `Designator_ZoneAdd`（`:160-161`）—— 涂色**一条命令都不发**；
    # ④ 删/反转/清空走 `area-op`，但 `ApplyAreaOp` 按 `Area.ID` 找目标，而那正是两端不同的字段。
    # 现在 ①②④ 由 `AreaAllowedSync`（第 58 条通道，按标签整张对账：新建/删除/差异）覆盖，
    # ③ 由 `area-paint` 命令覆盖 —— 它必须是**自己的 kind**，因为 `designator` 是实体生命周期类，
    # 副本会在 apply 之前丢弃它，而涂格不创建任何东西。实测 `area-paint-probe`：
    # `自建区域 1 个；涂加 1 格 → 命令 1 条；解码 标签「活动区 1」方向 加 地图 0 格子 1 个；重放后该格已置位`。
    if typ == 'Area_Allowed':
        return 'x', '自建区域由 AreaAllowedSync 按标签整张对账；涂格走 area-paint（自有意 kind，非 designator）'
    if typ.startswith('Designator_AreaAllowed'):
        return 'x', '涂格由 area-paint 下发（DesignatorSync.EncodeArea），重放按标签找区域后走索引器写格'

    # 发射：`CompLaunchable.TryLaunch`（`:543651-543727`）消耗整组燃料、创建 `ActiveTransporter`、
    # 包成 `FlyShipLeaving`、把整组交给世界对象 —— 是**物件的创建与销毁**，不是父物件字段变化，所以
    # `GizmoStatePatch` 只能看到 `lastLaunchTick`（`:543681`）。这一条到第 110 轮才成立，靠的是三件事：
    #
    # ① 载体：`transport-fixture` 往图上放 TransportPod / PodLauncher / Shuttle / GeneAssembler。第一次
    #    四个只活下来一个——`GenSpawn.Spawn` 先跑 `WipeExistingThings`（`:213282`），相邻格上的前一个被压掉。
    #    改成每 8 格一个之后四个都在（`已放置 4 个…同一调用内可见 TransportPod×1/PodLauncher×1/Shuttle×1/GeneAssembler×1`）。
    # ② 真实发射：`transport-launch` 走 `CanLaunch` 全部门槛并真的调 `TryLaunch`。为此要铺供油端口——
    #    `CompLaunchable_TransportPod.Refuelable` 来自 `FuelingPortUtility.FuelingPortGiverAtFuelingPortCell`
    #    （`:704228`），它在 pod 四邻找 `def.building.hasFuelingPort` 且自身端口格等于 pod 格的建筑
    #    （`:704240`），`GetFuelingPortCell`（`:704204`）= `center + rot.Rotate(Clockwise).FacingCell`，
    #    北向时是东侧一格。实测 `已发射：CanLaunch 通过（发射器 pod launcher 已加满 150/150），目的地 160264,0；
    #    天空坠落物 0→1 个`。
    # ③ **真缺口就是帧过滤器**：发射产物 `DropPodLeaving` 实测是 `Ethereal / FlyShipLeaving`
    #    （`transport-details` 读出来的），而 `DiffThings` 原本把 Ethereal 全跳过、只放行 Blueprint。于是每一次
    #    发射都在权威端生成、永远发不到副本；`FallSlot`（`:1446`）这个专为 Skyfaller 写的下降高度槽位因此是死代码。
    #    现在谓词抽成唯一的 `InFrameScope`，`Skyfaller` 一并放行，并有测试钉住（去掉豁免立刻变红）。
    #
    # 测量上限：容器 `副本=0` 时 `PublishFrame`（`:1610`）不运行，`skyfallersSent` 结构上恒为 0
    # （实测 `帧差分共运行 1 次`，census 自己写「房间无副本(PublishFrame 不跑)」）。所以这里能证的是
    # 「过滤+序列化后应进帧 1 个、载荷 1357 B」，不是「线上看到了它」。
    if typ.startswith(('CompLaunchable', 'CompTransporter', 'ActiveTransporter', 'FlyShipLeaving',
                       'TransportersArrivalAction', 'CompShuttle')):
        return 'x', ('发射产物经 Created/Removed 通道；真缺口是 Ethereal 过滤器丢弃 DropPodLeaving(Ethereal/FlyShipLeaving)，'
                     '已抽成 InFrameScope 并放行 Skyfaller；实测发射后天空坠落物 0→1、过滤+序列化后应进帧 1 个/1357 B')
    # comp 状态：`ThingWithComps.ExposeData`（`:160414-160428`）只调 `comps[i].PostExposeData()`，
    # `ThingComp` 不实现 `IExposable`（`:149291`）—— **comp 字段平铺进物件自己的节点**，没有 per-comp 子节点。
    # `comp-probe` 实测：9 comp 的 ShardAnimalPulser 片段 450 B，节点里直接有 `forbidden`（`CompForbiddable`
    # `:538484` 写的）。所以 comp 的可变状态**随物品/建筑片段传输**，不需要单独通道。
    # 唯一风险是标签碰撞：429 个 comp 标签里 35 个被多个 comp 复用（`color`/`verbTracker`/`innerContainer`/…），
    # 平铺 + `curXmlParent[label]` 取第一个 → 第二个被覆盖。但这是游戏自己的存档格式，
    # 实测 38507 个带 comp 的物件碰撞 0 个。归 `x` 的是「状态是否传输」，操作入口那些另有 gizmo/designator 规则。
    if typ.startswith(('Comp', 'ThingComp')) and any(m.startswith('PostExposeData') for m in methods):
        return 'x', 'comp 状态随物件片段传输（字段平铺；CompScribeLabels 记录标签碰撞集）'
    # `Notify_*` 一族：游戏用它们做"某件事发生了"的回调。副本不产生那些事，所以不调用；
    # 需要的**结果**由对应通道承载（存活/死亡 → Created/Removed；倾向 → AdaptationSync；念头 → MemorySync 等）。
    # 逐条核过的例外写在下面，不在这里泛化：
    #   - `Pawn_GuestTracker.CapturedBy`/`SetGuestStatus` 的副作用（ClearMind/DropAndForbid/Drafted=false）
    #     在副本上确实没有对应实现 —— 见 progress.md「仍然没解决的一条」，不归这里；
    #   - `Precept_RoleSingle.Notify_MemberChangedFaction` 由 `IdeoSync` 的角色表承载（待核）。
    # 所以这一族标 `~`：机制上不需要补丁，但"每个 Notify_ 的后果都有承载"是逐条主张，不是类型主张。
    # `Pawn_FlightTracker` 在第 69 轮补上：`FlightTick`（`:729158-729207`）是五个字段的唯一写者，由
    # `Pawn.Tick` 调用，副本不跑。五个字段全私有，唯一公开的 `Flying`（`:729105`）把四个状态压成一个布尔 ——
    # 正是这个通道要携带的信息。所以按「状态名 + 三个计数器」整条发，`Notify_JobStarted` 只是起飞的一个入口，
    # 真正的状态机在 `FlightTick` 里。**必须排在宽匹配 `Notify_*` 之前**（第五次同类排序问题）。
    if typ == 'Pawn_FlightTracker':
        return 'x', ('FlightSync：state:flyingTicks:cooldown:lerpTick（反射读写五个私有字段）；'
                     '探针实测 71 个小人往返 40 个全部一致，样本含 landing×1')
    if methods and any(m.startswith('Notify_') for m in methods):
        return '~', 'Notify_* 回调（副本不产生事件；后果由 Created/Removed/各通道承载，逐条待核）'
    # 对象生命周期：`DeSpawn`/`Destroy`/`DoDestroyEffects`/`DeconstructibleBy`。
    # `DiffThings` 每帧把"上一帧见过、这一帧没见到"的 id 作为 `removed` 发出（`Source/HeadlessRuntime.cs:1376-1380`），
    # 副本据此删掉对象；`DeconstructibleBy` 只是"能不能拆"的谓词，拆的动作走设计器路由。
    # 所以这一族不需要逐方法补丁 —— 但**也不能归入绘制**：`Building | DeSpawn,…,GetGizmos` 之前就是因为
    # 含有 `GetGizmos` 被整行判成绘制，三个生命周期方法跟着"不适用"了。这条规则必须在绘制规则之前。
    lifeycle = lambda m: m in ('DeSpawn', 'Destroy', 'DoDestroyEffects', 'DeconstructibleBy', 'Destroyed',
                               'DestroyMode', 'PreDestroy', 'PostDestroy', 'Notify_ThingDemolished')
    # A row can mix the two and still be fully covered: `Building | DeSpawn,DeconstructibleBy,DoDestroyEffects,
    # GetGizmos` is three lifecycle methods (removed channel) plus one UI provider (recomputed locally). Requiring every
    # method to be the *same* kind would report that row as a gap, which is wrong in the other direction.
    lifecycle_or_gizmo = lambda m: lifeycle(m) or 'Gizmo' in m or 'FloatMenu' in m or m == 'GetOptions'
    if methods and all(lifecycle_or_gizmo(m) for m in methods):
        if all(lifeycle(m) for m in methods):
            return 'x', '生命周期（removed 通道：DiffThings 每帧发消失的 id）'
        # Gizmo / 浮动菜单提供者只到 `~`，不到 `x`：入口确实在本端每帧被拉取，但**实测过一个反例** ——
        # `ReplicaFloatMenuProbe` 在副本上记录到空的浮动菜单，而根因至今没有确认（`CanReach` 读的是纯地图状态，
        # 所以"冻结的世界让路径查询答错"这个解释被排除了）。既然有一个已知的空菜单，就不能声明这一族成立。
        return '~', 'gizmo/浮动菜单提供者（入口本端每帧跑，但副本空浮动菜单的根因未明；输入待核）' 
    # AI 调度与权威端执行的一族：`WorkGiver_*`/`JobGiver_*`/`ThinkNode_JobGiver`/`JobDriver_*.MakeNewToils`/
    # `Toils_Recipe`/`GenRecipe.MakeRecipeProducts`/`IncidentWorker*`。副本不跑 AI，也不跑 toil，所以这些方法在副本上
    # **根本不会被调用**；它们产生的**结果**（新物件、物件状态、hediff、物品）由 `DiffThings` 的
    # `Created`/`Changed`/`Removed`（`Source/HeadlessRuntime.cs:1297-1354`）或对应通道送出。
    # 判据不是"名字像 AI"，而是"这个方法的输出是否是引擎对象的状态变化"——是，则由通道承载；
    # 若方法只写一个非对象的状态（计数器、时间戳），它属于 `ResidualCounterSync` 那类，另行判定。
    # 但**玩家入口不算 AI**：`GetPawnGizmos`/`GetGizmos`/`ExtraFloatMenuOptions`/`GetOptions` 是玩家点得到的东西。
    # 副本上没 gizmo 就是没按钮，没浮动菜单就是没选项 —— 这和"AI 不跑"是相反的问题，所以先排除。
    # 我第一次写这条规则时把 `LordJob_BestowingCeremony.GetPawnGizmos` 和
    # `LordToil_BestowingCeremony_Wait.GetPawnGizmos,ExtraFloatMenuOptions` 划进了 AI，是错的。
    # 逐**方法**判断，不按类型整条判。理由：`Building | DeSpawn,DeconstructibleBy,DoDestroyEffects,GetGizmos`
    # 这一行里只有最后一个提供 gizmo，前三个改变世界状态 —— 按整条套用会把生命周期方法一起判成"自行计算"。
    # 一行只要含有任何一个非 gizmo 方法，这一行就不能算被这条规则覆盖。
    gizmo_like = lambda m: 'Gizmo' in m or 'FloatMenu' in m or m == 'GetOptions'
    player_facing = methods and all(gizmo_like(m) for m in methods)
    # Gizmo / 右键菜单的提供者属于 `~` 而不是缺口：它们是**每帧被本端 UI 拉取**的，副本自己也会调，
    # 所以不需要补丁 —— 需要的是它们读的那些字段有通道。这和告警 `GetReport` 是同一类：
    # 机制成立（入口在本端会跑），每条输入待核。
    if player_facing:
        return '~', '副本自行计算（gizmo/浮动菜单提供者每帧被本端 UI 拉取；输入是否已同步待核）' 
    if not player_facing and typ.startswith(('WorkGiver_', 'JobGiver_', 'ThinkNode_', 'JobDriver_', 'Toils_',
                                            'GenRecipe', 'IncidentWorker', 'LordJob_', 'LordToil_', 'DutyDef')):
        return 'x', 'AI 调度/权威端 toil（副本不调用；结果经 Created/Changed/Removed 或对应通道）' 
    # 建筑功能方法：`Building_NutrientPasteDispenser`/`Building_GeneExtractor`/`Comp*` 的 DoEffect 一族。
    # 同理——副本不跑建筑 tick，调用它们只发生在权威端，产出是新物品或 hediff，已经由通道承载。
    if typ.startswith(('Building_NutrientPasteDispenser', 'Building_GeneExtractor', 'Building_Enterable',
                       'Building_SubcoreScanner', 'Building_OrbitalTradeBeacon', 'CompUseEffect')):
        return 'x', '建筑功能（权威端执行；产出经物品/hediff/状态通道）'
    # 商队 / 引力船 / 远行：整族被一条守卫挡住，不是逐方法同步。
    # `CaravanGuardPatch`（`Source/CaravanGuard.cs:12`）挂在 `Dialog_FormCaravan.TryFormAndSendCaravan` 上，
    # 在共享殖民地运行时直接返回 false 并给玩家一句「只有主机能组建远行队」—— 理由是组建远行队会把小人和物资
    # **从地图上移除**，而副本没有重放这件事的路径，另一个客户端会继续留着已经不存在的角色。
    # 于是 `Caravan_*`/`CaravansBattlefield`/`TravellingTransporters`/`FlyShipLeaving` 这些在副本上永远不会被走到。
    if typ.startswith(('Caravan', 'TravellingTransporter', 'FlyShipLeaving', 'ShipCountdown')):
        return '-', '被 CaravanGuardPatch 挡住（只有主机能组建远行队）'
    # Type-level rules match the type name; method-level rules must match **every** method.
    #
    # Measured defect this fixes: `Building | DeSpawn,DeconstructibleBy,DoDestroyEffects,GetGizmos` was classified as
    # 绘制/输入 because the `Gizmo` alternative matched `GetGizmos` — and `DeSpawn` travelled along with it. A row is a
    # group of methods that happen to share a type, not one behaviour: the earlier `any(match)` test let one drawing
    # method launder three lifecycle methods into "not applicable". A row is only inapplicable when nothing in it acts.
    joined = ','.join(methods)
    for label, pattern in NOT_APPLICABLE:
        if pattern.search(typ):
            return '-', label
        if methods and all(pattern.search(m) for m in methods):
            return '-', label
    return ' ', ''


def main():
    entries = load_ledger()
    pairs, by_type = load_runtime()
    print(f'运行时补丁表：{len(pairs)} 个 (类型,方法)，{len(by_type)} 个类型')
    print(f'其中 Designator 类型 {sum(1 for t in by_type if t.startswith("Designator"))} 个、'
          f'Command 类型 {sum(1 for t in by_type if t.startswith("Command"))} 个')
    print()
    counts = collections.Counter()
    rows = []
    for cat, typ, methods in entries:
        status, why = classify(typ, methods, pairs, by_type, cat)
        counts[status] += 1
        rows.append((status, cat, typ, ','.join(methods), why))
    print(f'账本 {len(entries)} 项')
    print(f'  [x] 已覆盖 {counts["x"]}')
    print(f'  [-] 不适用 {counts["-"]}')
    print(f'  [~] 副本自行计算，输入待核 {counts["~"]}')
    print(f'  [ ] 真缺口 {counts[" "]}')
    print()
    print('--- 真缺口 ---')
    for status, cat, typ, methods, _ in rows:
        if status == ' ':
            print(f'  [{cat.split("（")[0]}] {typ} — {methods}')
    out = ROOT / '_lab' / 'ledger-triage.txt'
    with out.open('w', encoding='utf-8') as handle:
        for status, cat, typ, methods, why in rows:
            handle.write(f'{status}\t{cat}\t{typ}\t{methods}\t{why}\n')
    print(f'\n写出 {out}')
    verify_channel_names(rows)
    if '--deep' in sys.argv:
        audit_negative_rows([r for r in rows if r[0] == '-'])
    return 0


CHANNELISH = re.compile(r'\b([A-Z][A-Za-z0-9]*(?:Sync|Patch|Channels?))\b')


def declared_source_classes():
    """Every class, struct and record name declared anywhere in our own sources.

    File names are *not* enough: most patch classes live inside `SharedActions.cs`, so a check against
    `os.listdir` reported `OrderCapturePatch` as missing while it exists thousands of lines into that file.

    Comments are stripped first (round 139). Without that the denominator included prose: the declarations
    scan matched `struct` in "this struct is", `record` in "record in the table" and `in` from a sentence, so
    the number moved whenever a doc comment was reworded (674 -> 675 with no type added) and the denominator
    stopped meaning "types we declare". The names being checked are real either way, but a denominator that
    counts English words cannot be used to argue the check covered everything.
    """
    names = set()
    strip_block = re.compile(r"/\*.*?\*/", re.S)
    for sub in ('Source', 'Networking', 'Simulation', 'Portal'):
        folder = PROJECT / sub
        if not folder.is_dir():
            continue
        for path in sorted(folder.glob('*.cs')):
            text = path.read_text(encoding='utf-8-sig', errors='replace')
            text = strip_block.sub(' ', text)
            text = re.sub(r'//[^\n]*', ' ', text)
            # A declaration has modifiers or nothing before it, never another identifier: `class`/`record` used as
            # an English word inside a string or an expression keeps an identifier in front of it.
            for match in re.finditer(r'(?m)^[ \t]*(?:[\w\[\]<>.,? ]*\s)?(?:class|struct|record)\s+([A-Za-z_]\w*)',
                                     text):
                names.add(match.group(1))
    return names

def _our_source_text():
    parts = []
    for sub in ('Source', 'Networking', 'Simulation', 'Portal'):
        folder = PROJECT / sub
        if folder.is_dir():
            for path in sorted(folder.glob('*.cs')):
                parts.append(path.read_text(encoding='utf-8-sig', errors='replace'))
    return '\n'.join(parts)


DECL_HINT = re.compile(r'\b(?:public|private|protected|internal|static|void|override|virtual|abstract|extern)\b')


def _method_body(lines, name):
    """From the line **declaring** `name(` to the brace that closes it, bounded at 220 lines.

    Two versions were wrong before this one, both instructively:

    1. matching any occurrence of `name(` resolved to a *call site*, which precedes the declaration in a decompile dump -
       so `MemoryUtility.ClearAllMapsAndWorld` reported a random field (`Clipboard`) as written by it;
    2. then requiring `{` on the declaration line found nothing at all: the dump puts the brace on the next line, so
       130 of 157 bodies went missing and the audit printed `0` - a filter that matches nothing looks exactly like a
       clean result.

    So: the line before the name must look like a declaration, and the first `{` may be on that line or a following one.
    """
    for i, line in enumerate(lines):
        if line.lstrip().startswith('//'):
            continue
        if not re.search(r'[\s(]' + re.escape(name) + r'\(', line):
            continue
        head = line.split(name)[0]
        if not DECL_HINT.search(head):
            continue
        open_at = None
        for j in range(i, min(i + 6, len(lines))):
            if '{' in lines[j]:
                open_at = j
                break
        if open_at is None:
            continue
        depth, out = 0, []
        for j in range(open_at, min(open_at + 220, len(lines))):
            out.append(lines[j])
            depth += lines[j].count('{') - lines[j].count('}')
            if depth <= 0:
                break
        return '\n'.join(out)
    return None


def audit_negative_rows(rows):
    """Which remaining `-` methods write a field this project never names?

    The question that has caught every real gap since round 119: `Presentation.`, `Dialog_.` and 「不改世界状态」 are all
    name-level judgements, and three of them were wrong. This prints the surviving candidates instead of trusting the
    rule. Opt-in (`--deep`) because it reads the 813k-line decompile dump; the default run stays fast.

    Deliberately NOT wired: "the row's method name appears in our sources". Measured this round - 274 hits, almost all
    coincidental (`Update`, `Close`, `DoWindowContents`, `ExposeData`, `Add` are our method names too), so it is noise
    rather than evidence. Recorded here so the next round does not re-invent it.
    """
    dump = ROOT / '_lab' / 'asm' / 'Assembly-CSharp.decompiled.cs'
    if not dump.is_file():
        print('\n深查：跳过（找不到反编译转储）')
        return
    ours = _our_source_text()
    lines = dump.read_text(encoding='utf-8', errors='replace').split('\n')
    flagged, no_body = [], 0
    for _status, _cat, typ, methods, _why in rows:
        for name in [m for m in methods.split(',') if m]:
            body = _method_body(lines, name)
            if body is None:
                no_body += 1
                continue
            locals_here = set(NEG_LOCAL_DECL.findall(body))
            fields = set()
            for recv, field in NEG_FIELD_WRITE.findall(body):
                if recv in locals_here or recv in ('Mathf', 'Math', 'string', 'Log', 'Rand', 'Gen'):
                    continue
                fields.add(field)
            unnamed = [f for f in fields if not re.search(r'\b' + re.escape(f) + r'\b', ours)]
            if unnamed:
                flagged.append((typ, name, sorted(unnamed)))
    total = len(rows)
    found = total - no_body
    print(f'\n深查：{total} 个 `-` 行的方法体，{found} 个解析到、{no_body} 个没有方法体，'
          f'{len(flagged)} 个方法写了本项目从未提名的字段')
    if total and found < 0.8 * total:
        print(f'  **覆盖不足：只有 {found}/{total} 解析到方法体，这个 0 不能当结论**')
    for typ, name, fields in flagged[:20]:
        print(f'    {typ}.{name} -> {",".join(fields)}')


def verify_channel_names(rows):
    """A reason that names `FooSync` / `FooPatch` must name something that exists.

    Round 128 found two rows closed on *another mod's* patches; round 130 found eight rows whose channel name was
    written from memory. Both are one defect: the evidence text is not checkable. This keeps it checkable.
    """
    classes = declared_source_classes()
    occurrences = sum(1 for r in rows for _ in CHANNELISH.finditer(r[4]))
    bad = []
    for _status, _cat, typ, _methods, why in rows:
        for match in CHANNELISH.finditer(why):
            if match.group(1) not in classes:
                bad.append((typ, match.group(1), why[:60]))
    print(f'\n自查：理由里点名的通道类 {occurrences} 处，源码里声明 {len(classes)} 个类')
    if not bad:
        print('  全部可核验')
    else:
        print(f'  **{len(bad)} 处点名的类不存在**：')
        for typ, name, why in bad:
            print(f'    {typ} -> {name} | {why}')
    verify_state_channel_names(rows)


STATE_CHANNEL = re.compile(r'状态通道:([A-Za-z_][A-Za-z0-9_]*)')


def all_source_text():
    """Every non-comment line of our own sources, concatenated.

    `declared_source_classes` answers "is this a type we declared"; this answers the weaker question "does this name
    appear anywhere in our code at all". Round 137 needs the weaker form: a `状态通道:X` claim may legitimately name a
    method, a field or a record rather than a class, so checking against declared classes alone would flag correct rows.
    """
    import pathlib
    root = pathlib.Path(__file__).resolve().parents[1] / 'TurboTick-headless'
    parts = []
    for sub in ('Source', 'Networking', 'Simulation', 'Portal'):
        for path in (root / sub).rglob('*.cs'):
            if 'obj' in path.parts or 'bin' in path.parts:
                continue
            for line in io.open(path, encoding='utf-8-sig', errors='replace').read().replace('\r', '').split('\n'):
                stripped = line.lstrip()
                if stripped.startswith('//') or stripped.startswith('/*') or stripped.startswith('*'):
                    continue
                parts.append(line)
    return '\n'.join(parts)


def verify_state_channel_names(rows):
    """A reason whose state channel is spelled `状态通道:X` must name something our code contains.

    Round 130 fixed reasons naming classes that do not exist; round 137 found the same defect one level down - the row
    for `Caravan_PathFollower` said `状态通道:ReplicaCaravan` and there is no such type, record or field anywhere: the
    actual carrier is a postfix on the `WorldObject.Tile` setter in `WorldObjectSync`. A claim whose subject does not
    exist is unreadable evidence, and it looked exactly like a verified row.
    """
    blob = all_source_text()
    claims = [(typ, m.group(1)) for _s, _c, typ, _m, why in rows for m in STATE_CHANNEL.finditer(why)]
    bad = [(typ, name) for typ, name in claims
           if not re.search(r'\b' + re.escape(name) + r'\b', blob)]
    print(f'自查：理由里点名的状态通道 {len(claims)} 处')
    if not bad:
        print('  全部在源码里存在')
        return
    print(f'  **{len(bad)} 处点名的名字在源码里不存在**：')
    for typ, name in bad:
        print(f'    {typ} -> {name}')


if __name__ == '__main__':
    sys.exit(main())
