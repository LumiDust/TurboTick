using System.Text.RegularExpressions;

namespace TurboTick.Networking;

/// <summary>Values shared by this standalone protocol, with no game assembly dependencies.</summary>
public static class NetworkProtocol
{
    public const int Version = 5;
    public const string ClientVersion = "0.7.5";
    public const int MaximumFrameLength = 4 * 1024 * 1024;
    public const int SnapshotChunkSize = 256 * 1024;

    /// <summary>基线下发每块 48 KB 原始字节。
    ///
    /// 传输层是 JSON：`byte[]` 走 base64（×4/3），一块 48 KB 上线路 64 KB，加上帧头仍比
    /// <see cref="MaximumFrameLength"/>（4 MB）低两个数量级，所以**不需要**改帧上限——
    /// 改上限会把「一条坏长度前缀能要求 64 MB 缓冲」变成现实。取 48 KB 是因为 18 MB 的存档
    /// 切成 388 块，单块仍能进一次 TCP write，而块数又不至于多到把有界发送队列（4096）顶满。</summary>
    public const int SaveChunkSize = 48 * 1024;

    /// <summary>基线下发的总超时。18 MB 在百兆链路上约两分钟，留到五分钟；
    /// 超过就断开这个 peer，让客户端重连重走一遍，而不是留一个永远没有画面的「已连接」。</summary>
    public const int BaselineTimeoutMilliseconds = 5 * 60 * 1000;

    /// <summary>How many ticks the published timeline may run ahead of the slowest peer. This is pipeline
    /// depth, not a backlog: a peer sitting inside it is keeping up, and must not try to close it, or it
    /// simulates several times faster than real time.</summary>
    public const int SimulationLeadTicks = 6;

    /// <summary>That same pipeline depth expressed in time. Six ticks is 100 ms at vanilla speed - longer than a
    /// report round trip - but only 16 ms at <c>Superfast</c> (rate 6), which is shorter than the round trip, so a
    /// fast room could never be paced correctly: the budget collapsed to 1-2 ticks per loop iteration and a 360 t/s
    /// room published about 119 (measured 2026-11-16, `预算=1` on a 62.5 Hz loop).</summary>
    public const double SimulationLeadMilliseconds = 100d;

    /// <summary>Pipeline depth in ticks for a room running at <paramref name="rate"/> times vanilla speed.
    /// Never below <see cref="SimulationLeadTicks"/>, so a slow room is not tightened.
    ///
    /// <para>The conversion is `seconds × 60 ticks/s × rate`, not `milliseconds / 1000 × rate`: a rate of 6 is six
    /// simulation ticks per rendered tick, so 100 ms is 36 simulation ticks, not 0.6.</para></summary>
    public static int LeadTicksAt(double rate) =>
        double.IsFinite(rate) && rate > 0
            ? Math.Max(SimulationLeadTicks, (int)Math.Ceiling(SimulationLeadMilliseconds / 1000d * 60d * rate))
            : SimulationLeadTicks;
}

/// <summary>Server-authoritative native time selection. Joining a room applies it by default.
/// Revision zero is the initial policy; legacy extra-rate fields remain for persisted-data compatibility.</summary>
public sealed record SpeedPolicy(bool Enabled = true, float Multiplier = 2f, float MaxRate = 60f, long Revision = 0,
    int TimeSpeed = 1)
{
    private float _multiplier = ValidateMultiplier(Multiplier);
    private float _maxRate = ValidateMaxRate(MaxRate);
    private long _revision = ValidateRevision(Revision);
    private int _timeSpeed = ValidateTimeSpeed(TimeSpeed);

    public float Multiplier { get => _multiplier; init => _multiplier = ValidateMultiplier(value); }
    public float MaxRate { get => _maxRate; init => _maxRate = ValidateMaxRate(value); }
    public long Revision { get => _revision; init => _revision = ValidateRevision(value); }
    /// <summary>Native Verse.TimeSpeed: 0 paused, 1 normal, 2 fast, 3 superfast, 4 developer ultrafast.
    /// Legacy extra-multiplier fields remain readable for older server state files; the game UI uses this value.</summary>
    public int TimeSpeed { get => _timeSpeed; init => _timeSpeed = ValidateTimeSpeed(value); }

    public static int ValidateTimeSpeed(int value) => value is >= 0 and <= 4
        ? value : throw new ArgumentOutOfRangeException(nameof(TimeSpeed), "Native time speed must be between 0 and 4.");

    private static float ValidateMultiplier(float value) => float.IsFinite(value) && value is >= 1f and <= 20f
        ? value : throw new ArgumentOutOfRangeException(nameof(Multiplier), "Multiplier must be finite and between 1 and 20.");
    private static float ValidateMaxRate(float value) => float.IsFinite(value) && value is >= 1f and <= 240f
        ? value : throw new ArgumentOutOfRangeException(nameof(MaxRate), "MaxRate must be finite and between 1 and 240.");
    private static long ValidateRevision(long value) => value >= 0
        ? value : throw new ArgumentOutOfRangeException(nameof(Revision), "Revision must be non-negative.");
}

public sealed record ModFingerprint(string PackageId, string ContentHash);
public sealed record ClientHello(string PlayerName, string GameVersion, string ModHash, string ClientVersion = NetworkProtocol.ClientVersion,
    // The active mod list in load order. Optional so an older client still handshakes; without it a mod mismatch can
    // only be reported, not explained, and the player has nothing to act on.
    string? Mods = null)
{
    /// <summary>Process-private simulation admission credential, supplied only by ConnectSimulationAsync.</summary>
    public string? SimulationKey { get; init; }

    public override string ToString() => $"ClientHello {{ PlayerName = {PlayerName}, GameVersion = {GameVersion}, ModHash = {ModHash}, ClientVersion = {ClientVersion} }}";
}

/// <summary>Transient, server-assigned room identity. Color is deterministic for the same player name,
/// including reconnects; it never consumes the simulation random stream.</summary>
public sealed record PlayerInfo(long Id, string Name, int ColorIndex, bool CanEdit, bool IsCheckpointOwner,
    int LatencyMilliseconds = -1, bool IsHost = false);

public enum PlayerActivity { Lobby, Synchronizing, Playing, Faulted }

/// <summary>Presentation only. MapId is Map.uniqueID, not the mutable map-list index.
/// MapId -1 means outside a map; Visible false retains map/activity but hides the pointer.</summary>
public sealed record PresenceUpdate(long Epoch, int MapId = -1, float X = 0, float Z = 0,
    bool Visible = false, PlayerActivity Activity = PlayerActivity.Lobby);

/// <summary>The relay binds PlayerId to the socket. UpdatedAtMilliseconds is replaced with local
/// monotonic receive time by the client; it is never a synchronized simulation timestamp.</summary>
public sealed record PlayerPresence(long PlayerId, PresenceUpdate State, long UpdatedAtMilliseconds = 0);

public static class PresenceRules
{
    public const int SendIntervalMilliseconds = 100;
    public const int KeepAliveMilliseconds = 1000;
    public const int StaleMilliseconds = 3500;
    public const int ColorCount = 12;

    public static int ColorForName(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name) hash = unchecked((hash ^ c) * 16777619);
        return (int)(hash % ColorCount);
    }

    public static bool IsValid(PresenceUpdate? state) => state is not null && state.Epoch >= -1 &&
        state.MapId is >= -1 and <= 1_000_000 && float.IsFinite(state.X) && float.IsFinite(state.Z) &&
        state.X is >= 0 and <= 1_000_000 && state.Z is >= 0 and <= 1_000_000 &&
        (!state.Visible || state.MapId >= 0) && Enum.IsDefined(state.Activity);
}

/// <summary>Session identity published at start; the seed aligns random streams across peers.</summary>
public sealed record SessionInfo(long Epoch, int Tick, int Seed, string WorldId, int SnapshotTick = 0,
    bool Authoritative = false, bool RoomAuthoritative = false, bool Lockstep = false);

/// <summary>One pawn exactly as the authoritative host simulates it. Display replicas apply this instead
/// of simulating. A predictive peer also needs the current job and its targets: two simulations that only
/// agree on position still walk to different places, because the AI underneath them chose differently.</summary>
public sealed record ReplicaPawn(int Id, int Map, float X, float Z, int Rotation, string? Job, float Health,
    int JobACell = -1, int JobAThing = -1, int JobBCell = -1, int JobBThing = -1,
    // Which toil of the current job the host is in. The job *name* alone cannot show progress: two pawns hauling
    // look identical whether they set off or are one step from done. This is the cheapest thing the host can send
    // that a replica can display without rebuilding the toil queue - and rebuilding the queue is exactly what a
    // display replica must not do.
    int Toil = -1,
    // 面板上看得见的三个数值。副本不 tick，需求不会自己变，所以必须由权威下发，否则选择面板永远显示载入值。
    float Mood = -1f, float Food = -1f, float Rest = -1f,
    // 想法列表：`defName#stage` 用 `;` 连接。只发「列表长什么样」，不发 Thought 对象——
    // 那是模拟状态，在副本上重建会牵动 Mood/想法衰减等一串逻辑。玩家展开看到的是这份列表。
    string? Thoughts = null,
    // 第三目标。`DoBill` 用它放材料（`IngredientPlaceCellInd`）、`HaulToContainer` 用它记主目的地、
    // `RecolorApparel` 用它指工作台，而 `JobDriver.ReportStringProcessed` 把它填进 `{2}`——
    // 缺了它，这些工作的头顶文字与进度条都指向「未知」。
    int JobCCell = -1, int JobCThing = -1,
    // `Job.count`：手上有几个/要几个。`RecolorApparel` 在运行期用它改写自己，`StartCarryThing` 用它决定搬几件。
    int JobCount = -1,
    // `Job.bill` 的身份，取 `Bill.GetUniqueLoadID()`。配方看板上的文字来自 `bill.recipe.jobString`，
    // 而 `bill` 为 null 时 `DoBill.GetReport()` 只能退回 `job.def.reportString`——那正是副本上
    // 「做一件事」显示不出具体配方的机制。Bill 对象本身随建筑的 Scribe XML 一起到，这里只补那个指针。
    string? JobBill = null,
    // `Job.targetQueueA/B`：`cell:thingId;...`（与位置同一套编码，`thingId` 为 -1 表示格目标）。
    // 队列不是可选装饰：`JobDriver.ReportStringProcessed` 在 `targetA` 无效时读队列首个元素，
    // 搬运/建造/清洁这类「先定第一个、其余排队」的工作，缺了队列就会显示成单目标版本。
    // 另外 `MakeNewToils` 有分支依赖队列（如 `DoBill` 的 `AnyIngredientsQueued`），
    // 队列必须在 `SetupToils` 之前填好，否则副本的 toil 条数与权威端不一致。
    string? JobQueueA = null, string? JobQueueB = null,
    // 手上拿着什么。`Pawn_CarryTracker.innerContainer` 把拿着的东西从地图网格上摘掉，所以副本既没有那个
    // `Thing` 也画不出它 —— 玩家看到的是「小人走到料堆旁、蹲下、然后空手走到蓝图边、蓝图还是 0/5」。
    // 用 `defName` 加数量而不是 id：这是显示，权威端随时可以把手里那份放回地上再拿另一份，
    // 追 id 会让副本每帧重建一次对象。真正需要 id 的场合（job 的 targetA）走 `JobAThing`。
    string? CarriedDef = null, int CarriedCount = -1,
    // `Pawn_MindState.lastJobTag`，取 `(int)JobTag`。-1 = 权威端没发（旧包）。
    //
    // 这个字段只由 `Pawn_JobTracker.StartJob` 写入（`Verse.AI/Pawn_JobTracker.cs:357`，取自
    // `thinkTree`/`jobGiver` 的 `tag`），而副本的 `SyncJob` 是手工拼 `Job` 再塞进 `curJob`，
    // **不经过 StartJob**——于是副本上它永远停在存档载入值。实测存档里 78 个角色全是 `Idle`，
    // 客户端因此对三个正在搬木头/造墙的殖民者报「3位殖民者无事可做」：告警读的是
    // `pawn.mindState.IsIdle`（`Verse.AI/Pawn_MindState.cs:232` 就是 `lastJobTag == JobTag.Idle`），
    // 与画面上的实际动作无关。同一个字段还驱动 `ThinkNode_ConditionalShouldFollowMaster`
    // （`RimWorld/ThinkNode_ConditionalShouldFollowMaster.cs:30`）。
    int JobTag = -1,
    // `PawnUtility.GetPosture()` 的整数值。这是**渲染角度**的来源，不是装饰字段。
    //
    // `PawnRenderer.GetDrawParms` 里 `bodyFacing` 在 `posture != Standing` 时走 `LayingFacing()`
    // （`Verse/PawnRenderer.cs:342`），而 `BodyAngle()` 对躺着的躯体返回 `LayingFacing().AsAngle`
    // ——按 `thingIDNumber % 4` 给出南/东/西，也就是 0/90/270 度。所以一个姿势不对的 pawn 会被整个人
    // 旋转着画出来，看起来就是「头朝下 / 倒着走」。
    //
    // 副本上这个字段永远不会自己变：`JobDriver.SetInitialPosture()` 在 `SyncJob` 里被**故意跳过**
    // （它强制 Standing，而 `LayDown` 的 initAction 会覆盖它，副本不跑 initAction 就会让睡着的人站起来），
    // 而唯一另一处写它的地方是 `Pawn_PathFollower.StartPath`（`Verse.AI/Pawn_PathFollower.cs:196`），
    // 副本根本不跑 pather。于是它永远停在存档载入时的值：存档里那一刻在睡觉的人，之后一直以躺姿渲染，
    // 哪怕帧里他正在走路。玩家看到的就是「人和动物全都头朝下」。
    // 技能。`SkillRecord.levelInt` / `xpSinceLastLevel` 由 `Learn` 写，而 `Learn` 只在工作 toil 里被调——
    // 副本一个都不跑 ⇒ 技能页永远显示加载时的等级，工作页的「相关技能平均」永不变化，每条技能下面的进度条永不涨。
    // 编码 `defName=等级:进度百分比:热情`，`;` 连接。百分比量化到整点：那条进度条约 100 px，逐帧的浮点只会让
    // 字符串不停变化而看不出区别。热情也要发——「有燃烧热情却一直不动」和「只是没在用这个技能」是两种不同的反馈。
    int Posture = -1,
    string? Skills = null,
    // 全部需求，编码 `defName=百分比;...`。
    //
    // 需求卡不是只画三个数：`NeedsCardUtility` 枚举 `pawn.needs.AllNeeds` 并保留 `ShowOnNeedList` 的那些
    // （`RimWorld/NeedsCardUtility.cs:91`）。原版殖民者上就是心情/食物/休息——也就是原先帧里那三个——
    // 但快乐、美观、舒适、户外、室内、药物依赖以及任何 DLC/mod 加的需求都在那张表里，且**在副本上全部冻结**。
    //
    // 按 defName 编码而不是加字段：这个集合是开放的（DLC 与 mod 会往里加），固定字段表会静默漏掉每一个没点名的需求
    // —— 正是这个通道要修的那种失败。
    //
    // 只发 `CurLevel`：它是存储值且有公有 setter；`CurInstantLevel` 是**只读虚拟 getter 没有 setter**，
    // 而画「条上一根标记」的舒适/美观正是覆写它、每次读都按周围环境现算的。它是推导值，不是要同步的状态——
    // 副本用自己那份房间数据推（地形/屋顶/建筑通道已经在保持它同步）。
    string? Needs = null,
    // 派系。`Thing.ExposeData` 确实 scribe 了它（`:157390-157391` 的
    // `Scribe_Values.Look(ref facID, "faction", "null")`），但 `AuthoritativeFrame.PawnState` 一直是 null
    // （逐帧发 71 个小人的完整片段太贵），所以**小人这一层从来没有过派系通道**：
    // 载入时正确，之后权威端一改（招募、驯服、奴役、放生、异种胚注入、囚犯转化）副本就永远停在旧值。
    //
    // 后果不是装饰：`Pawn.Faction` 决定殖民者条、颜色、`IsColonistPlayerControlled`（门禁、区域、工作）、
    // `Thing.Faction` 决定敌对判定，而 `MapPawns` 的 `pawnsInFactionSpawned` 表按派系索引 ——
    // 派系错了，那张表就是错的，"这个派系有几个殖民者"这类查询全部答错。
    //
    // 发 `Faction.loadID`（`Faction.GetUniqueLoadID()` 是 `"Faction_" + loadID`，`:564` 已确认它是稳定标识），
    // -1 表示无派系。**不**发 `def.defName`：两个临时派系可以有同一个 def。
    int FactionLoadId = -1,
    // `Pawn_HealthTracker.healthState`, as the raw enum value (0 Dead / 1 Down / 2 Mobile).
    //
    // **Why one byte matters.** `Pawn.Dead` is `health.Dead` (`:127019`) and `Pawn.Downed` is `health.Downed`
    // (`:126989`); both ultimately read `healthState` (`:136077`, `:136079`) - the enum, not the hediff list. A
    // replica never runs `HealthTrackerTick`, which is what calls `CheckStateChange` (`:136206`), so this field stays
    // at whatever the save was loaded with. Two visible consequences, both measured from the game's own reading path:
    //
    //  * `CharacterCardUtility.DrawCharacterCard` (`:642497`) skips the whole button row - banish, romance, and every
    //    colonist action - behind `else if (!pawn.health.Dead)`. On a replica a pawn who died on the authority after
    //    the save keeps that row; one who was downed keeps running actions the authority would refuse.
    //  * `PawnBanishUtility.ShowBanishPawnConfirmationDialog`'s caller refuses a downed pawn with
    //    "MessageCantBanishDownedPawn" (`:642510`) from `pawn.Downed`, so the dialog's own precondition is stale too.
    //
    // Sent as `int` with `-1` = "not sent" so an older peer's row still decodes; the health *percentage* was already
    // carried by `Health` and is filled from a derived value, which is why this is a separate field rather than a
    // reinterpretation of it.
    int HealthState = -1);

/// <summary>One non-pawn object as the host simulates it. Only objects that changed since the previous
/// frame are sent, so a colony with tens of thousands of plants does not stream every tick.
/// <paramref name="State"/> is the object's own Scribe XML, so any third-party Comp travels with it.</summary>
public sealed record ReplicaThing(int Id, int Map, int X, int Z, int Rotation, int Stack, int HitPoints,
    string? State = null,
    // The forbidden flag travels outside `State` on purpose: an authoritative frame must be able to repair a
    // forbid that a lost or refused command left wrong forever. Measured: 98 forbid commands were refused by the
    // per-peer budget in one session and those stacks stayed crossed out on every peer with no repair path.
    bool Forbidden = false);

/// <summary>A tick of authoritative colony state. Pawns are always sent in full because there are few;
/// things travel as a delta plus explicit removals. The host's own simulation peer never receives its
/// own frame.
///
/// <paramref name="NextThingId"/> and <paramref name="Weather"/> are the two facts a replica cannot derive:
/// object identity (the host is the only allocator) and weather (chosen on a tick the replica never runs).
/// Both are optional, so a peer that does not send them still decodes - it simply keeps its own counter and
/// its loaded weather.</summary>
public sealed record AuthoritativeFrame(int Tick, IReadOnlyList<ReplicaPawn> Pawns,
    IReadOnlyList<ReplicaThing>? Changed = null, IReadOnlyList<int>? Removed = null,
    IReadOnlyList<ReplicaPawnState>? PawnState = null, IReadOnlyList<ReplicaCreation>? Created = null,
    int NextThingId = 0, IReadOnlyList<ReplicaWeather>? Weather = null,
    // Zones are a separate channel from `Changed`/`Created`: a zone has no `thingIDNumber`, is not in any map's
    // thing list, and is rebuilt by `ZoneManager.RegisterZone` rather than `GenSpawn`. `Zones` carries only the
    // ones whose `Revision` moved, so a colony with thirty zones costs nothing per frame.
    IReadOnlyList<ReplicaZone>? Zones = null, IReadOnlyList<ReplicaZoneRemoval>? ZonesRemoved = null,
    // Conditions (hediffs) travel on their own on-change channel rather than inside `Pawns`.
    //
    // `Pawns` is sent in full every frame because a colony has only a few dozen of them, but a hediff list is not the
    // same kind of data: one raid fills a hundred-and-fifty entries, most of which never change again for the rest of
    // their life. Sending them with every frame would multiply the frame size for values that move once.
    //
    // The reason it has to travel at all: a replica runs no tick, so `Hediff.Severity` never advances, nothing is ever
    // added or removed, and the health tab shows the state the save was loaded with. Measured on the player's own
    // colony: a colonist took damage and nothing appeared in the log on the replica, because the replica never ran the
    // code that would have logged it.
    IReadOnlyList<ReplicaHediffSet>? Hediffs = null,
    // Letters and messages the host raised. Separate channel for the same reason conditions have one: a letter is an
    // event, not state - a colony raises a handful per hour, and `Changed`/`Pawns` are rebuilt every frame.
    //
    // Without this a display replica shows a colony under attack with no letter and no red text, because
    // `LetterStack.ReceiveLetter` is only ever called inside a tick and a replica runs none. The player sees the raid
    // and is told nothing about it.
    IReadOnlyList<ReplicaLetter>? Letters = null, IReadOnlyList<ReplicaMessage>? Messages = null,
    // Terrain cells the host changed. Same separate-channel rule: a map has 62 500 cells and terrain moves a few dozen
    // times an hour, so this is a dirty-cell set, not a diff.
    IReadOnlyList<ReplicaTerrain>? TerrainTop = null, IReadOnlyList<ReplicaTerrain>? TerrainUnder = null,
    IReadOnlyList<ReplicaTerrain>? TerrainFoundation = null,
    // Equipment and apparel. Not in the thing list (they live in the pawn's own trackers), so the thing enumeration
    // cannot carry them, and a replica that never equips anything draws an armed colonist empty-handed.
    IReadOnlyList<ReplicaGear>? Gear = null,
    // Doors. `openInt` is driven by the door's own Tick, which a replica never runs, so the whole colony's doors stay
    // frozen at their load-time state.
    IReadOnlyList<ReplicaDoor>? Doors = null,
    // Roofs. Not a Thing either: `RoofGrid` is one flat array, and a player-built roof never reaches a replica.
    IReadOnlyList<ReplicaRoof>? Roofs = null,
    // Snow depth per cell. Not a Thing and not terrain either: `SnowGrid.depthGrid` is one float per cell, filled by
    // weather on a tick the replica never runs.
    IReadOnlyList<ReplicaSnow>? Snow = null,
    // Fog of war. A bit array in `FogGrid`, lifted by pawn movement on a tick the replica never runs. Worst channel to
    // get wrong: fog hides terrain, things, roofs and snow, so everything else is wasted on a cell nobody can see.
    IReadOnlyList<ReplicaFog>? Fog = null,
    // Mental breaks. Started by a mood check inside a tick interval a replica never runs; without this a colonist who
    // breaks down on the host keeps loading-time state on the replica.
    IReadOnlyList<ReplicaMental>? Mental = null,
    // Inspirations. Rolled inside a 100-tick interval a replica never runs, so an inspired colonist shows nothing on the
    // replica - no icon in the needs card, no line in the inspect pane.
    IReadOnlyList<ReplicaInspiration>? Inspirations = null,
    // Skills are carried per pawn inside `ReplicaPawn.Skills` rather than as their own channel: they are the same "this
    // pawn's numbers" family as mood and health, always wanted together, and a change channel would need a string per
    // pawn per frame anyway.
    // Guest / prisoner / slave status and the recruit bar. Set by gameplay on ticks a replica does not have, so a captured
    // raider looks like an ordinary colonist to every query the UI makes.
    IReadOnlyList<ReplicaGuest>? Guests = null,
    // 据点表。`SettlementData` 只在 `TurboGameComponent` 里，靠 Scribe 只随快照走一次 ⇒ 快照之后建立的据点只存在于权威端：
    // 副本的 `SettlementGates.Data` 是空的，于是**副本上每个闸门都放行而权威端拒绝**，覆盖层也无从画起。
    // 整表而不是增量：表很小且有界，增量需要记录身份、墓碑和重同步路径——三种让两端授权不一致的方式。
    SettlementWire? Settlement = null,
    // 规划（Plan）。不是 `Thing`：住在 `PlanManager.allPlans` 里，物体枚举永远看不到 ⇒ 副本上玩家画的规划完全不存在。
    // 规划是玩家在「还没建」之前讨论建造的方式（「冷冻库放这儿」）——一个人的规划看不见，两个人就会从两头挖同一条走廊。
    // 每条编码 `label\n颜色def\nloadId\n格子索引...`，标签与颜色用换行分隔（玩家能在名字里打任何字符）。
    (long Version, IReadOnlyList<string>? Plans)? PlanList = null,
    // 研究进度。由 `AddProgress` 从工作 toil 里累加，副本不跑 ⇒ 研究页永远显示载入时的百分比，
    // host 早已完成的项目在副本上还是 30%，进度条永不移动。
    IReadOnlyList<ReplicaResearch>? Research = null,
    // 室内温度。`RoomTempTracker.temperatureInt` 只由 `EqualizeTemperature`（每 120 tick）写，副本不跑 ⇒ 每间室内房间永远
    // 停在载入值：冷库读出室温、供暖卧室永不升温、温度覆盖层一直画着载入那一刻的颜色。
    IReadOnlyList<ReplicaRoomTemp>? RoomTemps = null,
    // 植物生长。`growthInt` 由 `Plant.TickLong` 累加，副本不跑；而主循环只比对位置/旋转/堆叠/血量，
    // 生长中的植物正是「状态变了但没动」的那一类——所以它既不在主循环的增量里，也不在任何补扫表里。
    IReadOnlyList<ReplicaPlant>? Plants = null,
    // 派系关系。`Faction.TryAffectGoodwillWith` 由赠礼/任务/事件/社交斗殴调用，副本不跑 ⇒ 派系页永远显示载入时的好感度，
    // 而 `kind` 是**由好感度推导**的（`CheckKindThresholds`：-75 敌对 / +75 同盟 / 0 回中立），于是一个中途翻脸的同盟
    // 在副本上仍读作同盟，商队、贸易、袭击全跟着这个过期的答案走。
    IReadOnlyList<ReplicaFactionRelation>? Factions = null,
    // 世界物件位置。商队/据点/飞船/遗迹住在 `Find.WorldObjects` 里，不在任何地图的物件网格里，靠自己的间隔 tick 移动
    // （`Caravan_PathFollower.TryEnterNextPathTile` 里 `caravan.Tile = nextTile`）⇒ 副本不跑 ⇒ 一个走了一整季的
    // 商队仍然画在出发地。
    IReadOnlyList<ReplicaWorldObject>? WorldObjects = null,
    // 任务进度。`Quest.Accept` 与 `Quest.End` 都在权威端发生，副本不跑 ⇒ 一季前接受的任务仍显示待接受，
    // 完成的任务仍显示进行中，连同它会给出的奖励或失败提示。
    IReadOnlyList<ReplicaQuest>? Quests = null,
    // 记忆年龄。`Thought_Memory.age` 只由 `ThoughtInterval` 累加（`age += 150`），而 `ShouldDiscard` 看
    // `age > DurationTicks` ⇒ 副本上**没有任何记忆会过期**：一季前朋友去世的悲痛仍在，好饭的加成永不消退。
    IReadOnlyList<ReplicaMemory>? Memories = null,
    // 传说（殖民地故事日志）。`TaleRecorder.RecordTale` 只在权威端跑，`TaleManager.Add` 才把它放进历史页与艺术品取值读的列表
    // ⇒ 副本只持有存档里带来的那几条。
    IReadOnlyList<ReplicaTale>? Tales = null,
    // How far through its current toil each working pawn is. The bar over a working pawn's head is
    // `1f - ticksLeftThisToil / defaultDuration`, only `JobDriver.DriverTick` decrements the counter, and a replica never
    // ticks a driver - so the bar stands still while the job name keeps changing. The denominator is derivable from the
    // toil the replica already rebuilt, so only the numerator travels.
    IReadOnlyList<ReplicaToilProgress>? ToilProgress = null,
    // Time left on timed conditions. The hediff channel rebuilds every condition from def/part/severity, which re-runs
    // `CompPostPostAdd` and so resets `ticksToDisappear` / `ticksLeft` to the full duration: an anaesthetic with 400
    // ticks left reads as a fresh dose forever. Tend quality is lost the same way, so it rides along.
    IReadOnlyList<ReplicaHediffTimers>? HediffTimers = null,
    // The map's active weather conditions. `GameConditionManagerTick` advances and expires them and
    // `AggregateTemperatureOffset()` sums them into the outdoor reading - a replica runs neither, so a cold snap that
    // started after the save was loaded simply does not exist there and every indoor room is equalised against the
    // wrong outdoor number.
    IReadOnlyList<ReplicaGameCondition>? Conditions = null,
    IReadOnlyList<ReplicaGameConditionRemoval>? ConditionsRemoved = null,
    // How old each pawn is. `AgeTickInterval` advances the counter inside the pawn's own tick, which a replica never
    // runs, so every pawn keeps the age the save was loaded with - and the age is what the character card, the age text
    // rule and the capacity curves all read.
    IReadOnlyList<ReplicaPawnAge>? Ages = null,
    // How long each pawn has been below each mental-break threshold. `MentalBreakerTickInterval` is the only writer and a
    // replica never runs it, so the "about to break" flags - and the head glow they draw - are decided from stale counters.
    IReadOnlyList<ReplicaMentalBreaker>? Breaking = null,
    // How far each disease has been fought off. `ImmunityHandlerTickInterval` is the only writer and a replica never runs
    // it, so the immunity label, the immunity percentage and - because `FullyImmune` switches `SeverityPerDay` - the
    // direction the disease progresses are all decided from the value the save held.
    IReadOnlyList<ReplicaImmunity>? Immunity = null,
    // Every driver's own counters, encoded. `JobDriver.ExposeData` writes only the ten base fields, so the subclass
    // counters a custom progress bar reads - `workDone`, `sowWorkDone`, `paintingTime`, `totalCleaningWorkDone`,
    // `gatherProgress` and about fifteen more - never enter any payload. The bar getter is evaluated from
    // `AddPreTickIntervalAction`, which a replica never runs, so every such bar is frozen at its first frame.
    IReadOnlyList<ReplicaDriverState>? Drivers = null,
    // How far a sanguophage has got through a deathrest, and how many buildings it may bind. `TickDeathresting` is the
    // only writer and it runs from a tick a replica does not run, so `Hediff_Deathrest.LabelInBrackets` - which is
    // literally this percentage - never moves, and the "can wake safely" alert never fires.
    IReadOnlyList<ReplicaDeathrest>? Deathrest = null,
    // A busy stance, stun and stagger. `Pawn_StanceTracker.curStance` starts as `Stance_Mobile` and only its own
    // tick replaces it, so a replica never aims, never faces its target and never shows the stun line.
    IReadOnlyList<ReplicaStance>? Stance = null,
    // Lord counters. `LordManager.LordManagerTick` sits inside `MapPostTick` (`:77229`), which a display replica
    // holds at the tick gate, so `ticksInToil` (`:268596`) never advances here and every lord-driven activity stays
    // at whatever the save held.
    IReadOnlyList<ReplicaLord>? Lords = null,
    // Quest parts, as the `<parts>` slice of the quest's own Scribe fragment. `Quest.QuestTick` is reached only from
    // `QuestManagerTick` inside `MapPostTick` (`:52988`), so a replica's parts never advance - and the counters inside
    // them are what the quest tab prints as `<DescriptionPart>` (`:672966`).
    IReadOnlyList<ReplicaQuestParts>? QuestParts = null,
    // The pawn-owned trackers whose counters only their own tick advance: guilt, training decay, learning desire and
    // flight. All four are scribed by `Pawn.ExposeData` (`:131381-131431`) and advanced from `Pawn.TickInterval`, so a
    // replica keeps the loaded value forever.
    IReadOnlyList<ReplicaPawnTracker>? PawnTrackers = null,
    // 世界物件的**状态**（不是位置）。`WorldObject.ExposeData`（`:798523`）先写自己的字段、再逐个调
    // `comps[i].PostExposeData()`，全在同一个元素里 —— 没有 `<comps>` 包裹。世界物件不在任何地图的物件网格里，
    // 四条补扫一条都不遍历它 ⇒ 据点自己的袭击倒计时、飞船的航程百分比全都冻在载入值上。
    // Appended at the end on purpose: every other argument is positional, so inserting it beside `WorldObjects` shifted
    // thirty-one parameters by one and the compiler reported it as three unrelated type mismatches.
    IReadOnlyList<ReplicaWorldObjectState>? WorldObjectStates = null,
    // Abilities are their own channel for the same reason hediff timers are: `AuthoritativeFrame.PawnState` is never
    // sent, so a pawn's own Scribe fragment does not reach a replica and every subsystem needs a named one.
    IReadOnlyList<ReplicaAbilityCharges>? AbilityCharges = null,
    // Storyteller adaptation. One row for the whole save, because both counters belong to the same subsystem and neither
    // means anything without the other.
    IReadOnlyList<ReplicaAdaptation>? Adaptation = null,
    // The colony's death countdown. A single row for the whole save, like adaptation.
    IReadOnlyList<ReplicaGameEnder>? GameEnder = null,
    IReadOnlyList<ReplicaPassingShips>? PassingShips = null,
    IReadOnlyList<ReplicaResidualCounters>? ResidualCounters = null,
    IReadOnlyList<ReplicaPawnIdeo>? Ideos = null,
    // Which thing kinds and codex entries the game treats as discovered. One row for the whole save, like adaptation.
    IReadOnlyList<ReplicaDiscovery>? Discoveries = null,
    // The three `PlaySettings` fields that are simulation inputs rather than display toggles.
    IReadOnlyList<ReplicaPlaySettings>? PlaySettings = null,
    // The built-in areas' cell grids. Areas are neither zones nor things: `AreaManager.ExposeData` (`:64119`) scribes
    // them as a deep list and no frame enumerates them, so `ZoneSync` never covered them and a replica's home area was
    // empty. See `ReplicaArea`.
    IReadOnlyList<ReplicaArea>? Areas = null,
    // The player-created `Area_Allowed` list: which ones exist, what they are called and which cells they cover.
    // Distinct from `Areas` above, which carries only the four built-in areas by type; an allowed area has no fixed
    // type slot and its id comes from each peer's own UniqueIDsManager, so it travels by label. See `ReplicaAllowedArea`.
    IReadOnlyList<ReplicaAllowedArea>? AllowedAreas = null,
    IReadOnlyList<ReplicaPawnTraits>? Traits = null,
    // 爵位（头衔 / 已购买许可 / 好感）。第 102 轮：账本里「pawn.royalty 没有通道」是 6 条 `~` 的共同根因，
    // 而 `CharacterCardUtility`、`PermitsCardUtility`、`RoyalTitle.RoomRequirementGracePeriodActive` 与
    // 所有 `RoyalTitlePermitWorker_*` 都读它。
    IReadOnlyList<ReplicaRoyalty>? Royalty = null,
    IReadOnlyList<ReplicaPawnGenes>? Genes = null,
    IReadOnlyList<ReplicaPreceptTable>? Precepts = null,
    IReadOnlyList<ReplicaFlight>? Flights = null,
    // Which ideoligion each faction treats as its own. `FactionIdeosTracker.ExposeData` (`:487657-487666`) scribes
    // `primaryIdeo` and `ideosMinor` as REFERENCES, so both ends load the same pointers from the same save - but the
    // pointers only move through `SetPrimary` (`:487569-487572`, a bare field write) and
    // `RecalculateIdeosBasedOnPlayerPawns` (`:487516-487567`), and both are reached from the authority's own UI
    // (`Page_ChooseIdeoPreset.MakeIdeoPrimary` `:630189`, `Page_ConfigureIdeo` `:630121`) and from
    // `SetIdeo` / `Notify_MemberDied` / `Notify_MemberCaptured` - none of which a replica runs.
    //
    // <para>What reads it: `XenotypesAvailableFor` (`:133652-133665`) walks `PrimaryIdeo.memes` to decide which
    // xenotypes a faction's pawns are generated with, so a replica rolls a different distribution when it generates a
    // pawn; `TraderKindCommonality` (`:389493-389500`) refuses every Slaver trader when any of `AllIdeos` disapproves
    // of slavery, so a stale set changes which traders a caravan offers. The ideoligion table channel
    // (`IdeoSync`) carries names, colours and development points but never says which of them a faction believes.</para></summary>
    IReadOnlyList<ReplicaFactionIdeo>? FactionIdeos = null,
    // Which maps exist. Every other map-keyed channel carries a `Map.uniqueID` and drops what it cannot resolve, so a
    // map the authority generated after the save was loaded is not "out of sync" on a replica - it is absent, and the
    // map lists on the two screens disagree with no error anywhere. See `MapLifecycleSync`.
    // World objects the authority created, and ids it destroyed. Appended at the end for the same reason every other
    // field is: the record's parameters are positional, so inserting beside `WorldObjects` would shift every later
    // argument. A creation carries a whole Scribe fragment and is sent once per object; a removal is a bare id and an
    // event rather than the absence of a row, because an absence cannot be told from "not told yet".
    IReadOnlyList<ReplicaWorldObjectCreation>? WorldObjectCreations = null,
    IReadOnlyList<int>? WorldObjectsRemoved = null,
    // Two gravship flags that pause the **whole game on every peer**: `TickManager.ForcePaused` (`:52680`) returns true
    // while a landing confirmation is pending, and `PlayerCanControl` (`:52696`) refuses every unpause with a message.
    // Appended last for the same positional reason as everything above. `"01"` = no confirmation / cutscene on.
    string? GravshipGate = null,
    IReadOnlyList<ReplicaMap>? Maps = null,
    // The three diplomacy cooldowns, per faction. Appended last for the same reason every other field is.
    IReadOnlyList<ReplicaFactionCooldown>? FactionCooldowns = null,
    // The history-event rows the faction dialog reads. Appended after the cooldowns for the same reason.
    IReadOnlyList<ReplicaHistoryEvent>? HistoryEvents = null,
    // Auto-slaughter limits, per map and per animal species. Appended last for the same positional reason.
    IReadOnlyList<ReplicaAutoSlaughterConfig>? AutoSlaughterConfigs = null,
    // The bossgroup summon cooldown stamp. `Command_CallBossgroup.IsDisabled` reads it from `GizmoOnGUI`, so each peer
    // draws its own answer while the cooldown lives in the host's UI only - and the summon action has no cooldown
    // check, so the click really fires. See `ReplicaBossgroup` and `Source/BossgroupSync.cs`.
    ReplicaBossgroup? Bossgroup = null,
    // Precept names and their lock flag (round 147). Sparse: only a precept whose name or lock moved.
    IReadOnlyList<ReplicaPreceptName>? PreceptNames = null,
    // Per-precept payload (round 148): the def a thing / apparel / weapon precept was pointed at. Sparse like the names.
    IReadOnlyList<ReplicaPreceptPayload>? PreceptPayloads = null,
    // The anomaly difficulty the settings dialog writes. One record per game, appended last for the positional reason.
    ReplicaAnomalyDifficulty? AnomalyDifficulty = null,
    // Policy definitions for all four `Policy` databases (round 160). Sparse in time, complete in content: sent as one
    // whole-table snapshot when the table changed, never as a per-policy diff. Appended last for the positional reason.
    IReadOnlyList<ReplicaPolicy>? Policies = null,
    // Factions the authority created after the save was loaded (round 162). An event list: a faction exists from its
    // creation until the save ends, so an older frame's creations must survive a merge. Appended last for the
    // positional reason.
    IReadOnlyList<ReplicaFactionCreation>? FactionCreations = null,
    ReplicaCorpseTracker? CorpseTracker = null,
    // Visual-only `Ethereal` carriers (bombardments, orbital debris, tornadoes). The map diff skips the whole category
    // because `SignalAction_*`/`RectTrigger` are logic carriers and `OrbitalStrike.Tick` explodes - so these never reach
    // a replica and a player watching the colony sees no strike at all. Timers only; see `EtherealVisualSync`.
    IReadOnlyList<ReplicaLivingEffect>? LivingEffects = null, IReadOnlyList<int>? LivingEffectsRemoved = null);

/// <summary>One visual `Ethereal` carrier: `lifeTime`/`maxLifeTime` are what the drawing reads.</summary>
/// <summary>One visual `Ethereal` carrier. `Projectiles` is `<life>:<max>:<x>:<z>` joined by `;` - the shells a
/// `Bombardment` is drawing (`Bombardment.BombardmentProjectile`, a nested type whose `lifeTime`/`maxLifeTime` are
/// private and whose `targetCell` is the cell it falls on). No ticking: the replica only draws them.</summary>
public sealed record ReplicaLivingEffect(int Id, string Def, int Map, int X, int Z, int Rotation, string Projectiles);

/// <summary>One policy definition (outfit / drug / food / reading), carried as its own Scribe fragment (round 160).
///
/// <para><b>The defect.</b> The pawn side of policies travels: `PolicyCapturePatch` scribes `pawn.outfits`,
/// `pawn.drugs`, `pawn.foodRestriction` and `CommandCodec.PolicyCommand` ships them. The **definitions** never did - no
/// file in `Source/` mentioned `OutfitDatabase`, `DrugPolicyDatabase`, `FoodRestrictionDatabase` or
/// `ReadingPolicyDatabase` at all. So a player who clicked "new outfit" / "new drug policy" / "new food restriction" /
/// "new reading policy" (`Dialog_ManagePolicies<T>` and its four subclasses, `:641618`/`:641697`/`:641907`/`:642246`)
/// created a policy on one peer only, and every pawn tracker fragment that names it then resolved to nothing on the
/// other peer - silently, because the tracker's own `ExposeData` scribes the policy as a reference.</para>
///
/// <para><b>Why one channel for four kinds.</b> `ApparelPolicy`, `DrugPolicy`, `FoodPolicy` and `ReadingPolicy` all
/// derive from `Policy : IExposable` with the same `(id, label)` shape and their own `ExposeData`, and each database
/// exposes a `List<T> All…` property. A per-kind channel would be four copies of the same twenty lines; the kind is a
/// field on the row instead.</para>
///
/// <para><b>Identity is `(Kind, Id)`, not the label.</b> `Policy.id` is allocated as `max(id) + 1` inside each database
/// (`MakeNewReadingPolicy` `:361223`, and the same line in the other three), and `GetUniqueLoadID()` appends the
/// <i>label</i> - so renaming a policy changes its load id while keeping its identity. Keying on the id keeps a rename
/// an edit rather than a delete plus a create.</para></summary>
public sealed record ReplicaPolicy(string Kind, int Id, string Label, string Xml);

/// <summary>One faction the authority added at runtime, carried as its whole Scribe fragment (round 162).
///
/// <para><b>The defect.</b> `FactionSync` carries relations, cooldowns and ideoligion, all keyed by a faction both ends
/// already hold. The game also creates factions during play - `FactionGenerator.NewGeneratedFactionWithRelations` runs
/// on the authority when a quest or incident needs one - and nothing carried the faction itself, so on a replica its
/// pawns belong to no faction and the faction screen does not list it.</para>
///
/// <para><b>Why the whole fragment.</b> `Faction.ExposeData` (`:485495`) writes def, name, loadID, relations, the
/// kidnapped tracker, ideoligion, predator threats, `defeated` and five cooldown stamps. A field list would miss
/// whatever a mod adds.</para>
///
/// <para><b>Identity is `loadID`.</b> The fragment carries it, so `CreateFromFragment` reproduces the same id and
/// `Apply` can tell "already here" from "missing" without trusting the def or the name - two pirate factions are
/// both `def = Pirate`.</para></summary>
public sealed record ReplicaFactionCreation(int LoadID, string Def, string Xml);

/// <summary>One precept's display name and whether the player locked it against regeneration (round 147).
///
/// <para><b>The defect.</b> `PreceptSync` carries which precept defs an ideoligion has and who holds a role - not the
/// names. A replica therefore builds each precept itself, and `Precept` generates its own name on construction
/// (`RegenerateName` `:499725`, `SetName` `:499729`), so the same precept reads differently on every peer: the ideo
/// screen, the ritual list and every thought that prints the precept's label. `nameLocked` (`:499254`) is the second
/// half: `Ideo.RegenerateAllPreceptNames` (`:493051-493066`) skips a locked precept, so a player who typed a name and
/// locked it on a replica would have it regenerated on the authority at the next regeneration pass - and then never
/// learn that the name they chose is gone.</para>
///
/// <para><b>Why a separate row and not part of the table payload.</b> The table payload is
/// `ideoId:def|def@holder` and is parsed by splitting on `|` and `@`; a name is free text the player types
/// (`:631988`, 32 characters, `ValidSymbolRegex`) so folding it into that format would need escaping and would break
/// every older peer's parse. One row per changed precept costs a few bytes and keeps both shapes honest.</para></summary>
public sealed record ReplicaPreceptName(int Ideo, string Def, int Index, string Name, bool Locked);

/// <summary>One precept's chosen defs (round 148): what a thing, apparel or weapon precept was pointed at.
///
/// <para><b>The defect.</b> `Precept_ThingDef.ThingDef` (`:500759-500773`), `Precept_Apparel.apparelDef`
/// (`:500134`) and `Precept_Weapon.noble` / `.despised` (`:500946-500948`) are the <b>payload</b> the player picks in
/// the ideoligion editor - `StyleCatOverride.AddWeaponPreceptOption` (`:496677-496688`) writes the pair, `:496618-496630`
/// picks an apparel or building def. `PreceptSync` carries the def *list* and who holds a role, so a replica builds each
/// precept from its `PreceptDef` and `Init` picks its own thing out of `def.Worker.ThingDefs` - the same precept reads
/// "bow" on one peer and "rifle" on another, and a weapon precept's mood penalty lands on a different weapon class.
/// `ThingDef.set` also runs `Notify_ThingDefSet()` (`:500542-500546`, `:500836-500843`), which resets the ideoligion's
/// style for that building and rolls a random style category when none is set - so the payload has to travel before the
/// name, or the generated name would be overwritten by the sync a frame later.</para>
///
/// <para><b>Why the index.</b> A `PreceptDef` with `allowMultiplePrecepts` produces several instances of the same def, so
/// `(ideo, def)` is not an identity - round 147's name rows were ambiguous for exactly that reason and one of the two
/// apparel precepts would never receive its name. The ordinal among same-def precepts in `PreceptsListForReading` order is
/// stable here: the table channel creates them in that order (`PreceptSync.Apply`), so both peers count identically.</para>
///
/// <para><b>Nulls.</b> A field is null when the precept is not that kind, and the apply halves check the type before
/// writing, so a payload row cannot force one precept class's fields onto another.</para></summary>
public sealed record ReplicaPreceptPayload(int Ideo, string Def, int Index, string? Thing, string? Apparel,
    string? Noble, string? Despised,
    // `<StyleCategoryDef defName>/<ThingStyleDef defName|->` for the `Thing` above (round 149).
    //
    // <para>`Precept_ThingDef.ThingDef`'s setter runs `Notify_ThingDefSet` (`:500836-500843`), which first clears the
    // ideoligion's cached style for that building and then, when the building is style-editable and no category claims
    // it, **rolls** `AllPossibleStylesForBuilding(ThingDef).RandomElement()` and stores that category. A replica applies
    // the payload without simulating, so its `Rand` stream is not the host's: the same edit would give the two peers
    // different art and a different `ThingStyleDef` in every bill that asks `StyleForThingDef`. The style therefore has
    // to travel with the payload and be written after the `ThingDef` - the setter's roll happens first, and the synced
    // pair has to be the last word.</para>
    string? Style = null);

/// <summary>The anomaly half of `Difficulty` (round 150): the five values `Dialog_AnomalySettings` writes on Accept.
///
/// <para>`GameComponent_Anomaly.AnomalyThreatFractionNow` (`:723349-723372`) reads them through
/// `Storyteller.AnomalyIncidentChanceNow` (`:390002`), so a replica that never receives them draws a threat bar - and
/// answers every "when does the next anomaly event fire" question - from the values the save was loaded with.</para>
///
/// <para>`Override` is nullable on purpose: `null` means "the playstyle's own fraction decides", `0` means "override with
/// zero", and the two are different answers inside `AnomalyThreatFractionNow`.</para></summary>
public sealed record ReplicaAnomalyDifficulty(string Playstyle, float? Override, float Inactive, float Active,
    float Study);

/// <summary>The `PlaySettings` fields that change what the colony does.
///
/// <para><b>Why only three of twenty-one.</b> `PlaySettings.ExposeData` (`_lab/asm/Assembly-CSharp.decompiled.cs:724516`)
/// scribes every field, and most are per-player display choices: which overlays are on, whether the colonist bar shows,
/// whether the map is locked north-up. Those belong to the person looking at the screen, and copying one peer's
/// preference onto another is not synchronization, it is interference. Three change behaviour:</para>
///
/// <para><c>useWorkPriorities</c> (`:724492`) is read by <c>Pawn_WorkSettings.GetPriority</c> (`:331069-331077`),
/// which returns 3 for EVERY non-zero priority when it is off. So the same stored number means "do this whenever" on one
/// side and "do this second" on the other - the one class of divergence a player cannot see, because both screens show
/// a number.</para>
///
/// <para><c>autoHomeArea</c> (`:724474`) decides whether new construction joins the home area, which is what every
/// temperature and "inside" query reads.</para>
///
/// <para><c>autoRebuild</c> (`:724476`) decides whether a destroyed player building re-queues its own blueprint
/// (`:160062`, `:160071`) - a colony that rebuilds on one peer and does not on the other diverges by construction,
/// permanently.</para></summary>
/// <summary>The bossgroup summon cooldown, as one tick stamp (`GameComponent_Bossgroup.lastBossgroupCalled`).
///
/// <para><b>Why a stamp and not a remaining time.</b> The reader is `Find.TickManager.TicksGame - lastBossgroupCalled`
/// (`:3185`); the tick count itself is shared, so sending the stamp reproduces the host's arithmetic exactly, while a
/// remaining-duration would drift by the frame delay and would need re-sending every frame to stay honest.</para></summary>
public sealed record ReplicaBossgroup(int LastCalled);

/// <summary>The gameplay half of `PlaySettings` (the display toggles stay per-client).
/// `Care` is the eleven `defaultCareFor*` enums as integers joined by `,` in the order
/// colonist, prisoner, slave, tamedAnimal, friendly, neutral, hostile, none, wildlife, entities, ghouls -
/// `MedicalCareUtility`-side code reads them for every pawn that has no explicit setting.</summary>
public sealed record ReplicaPlaySettings(bool UseWorkPriorities, bool AutoHomeArea, bool AutoRebuild, string? Care = null);

/// <summary>The auto-slaughter limits for one species on one map.
///
/// <para><b>The gap this closes.</b> `AutoSlaughterManager.configs` (`:64706`) is a list of `AutoSlaughterConfig`
/// (`:64636`) - five limits, two allowances and a pair of UI buffers per animal def - and it is edited from the
/// auto-slaughter dialog's number cells, where a changed value calls `map.autoSlaughterManager.Notify_ConfigChanged()`
/// (`:625940-625943`). That is a player click, and the reader is `AnimalsToSlaughter` (`:64712`), whose result the
/// authority turns into slaughter orders. So a limit typed on a replica never reaches the side that kills the animals:
/// the colony keeps breeding past a cap the player believes they set, and nothing anywhere reports a disagreement.</para>
///
/// <para>The UI buffer strings (`uiMaxTotalBuffer` etc.) deliberately do NOT travel: they are the text being typed in a
/// text field on one screen, and copying them would fight the peer's own cursor.</para></summary>
public sealed record ReplicaAutoSlaughterConfig(
    int Map,
    string Animal,
    int MaxTotal,
    int MaxMales,
    int MaxMalesYoung,
    int MaxFemales,
    int MaxFemalesYoung,
    bool AllowPregnant,
    bool AllowBonded);

/// <summary>One built-in area's set cells.
///
/// <para><b>Keyed by kind, not by id.</b> <c>Area.ID</c> comes from <c>Find.UniqueIDsManager.GetNextAreaID()</c>
/// (`:63999`), so the home area is not the same number on two peers; an id-keyed payload would write one area's grid into
/// another. The kind is one of `home`/`buildRoof`/`noRoof`/`snowClear`, which are the areas
/// <c>AreaManager.AddStartingAreas</c> (`:64100-64112`) creates on every map.</para>
///
/// <para><b>Cells as a `;`-joined list of set cells</b> - the same encoding `ReplicaZone.Cells` uses, so an operator
/// reading a log knows both. The alternative, one byte per cell, would be `Size.x * Size.z` bytes for a grid that is
/// empty everywhere except the base.</para>
///
/// <para><c>Area_PollutionClear</c> is not carried: it only exists when Biotech is active (`:64106`) and it is a display
/// overlay for pollution rather than a work assignment. When Biotech is on the area exists but stays empty, which is
/// what it looks like before any pollution is cleaned anyway.</para></summary>
public sealed record ReplicaArea(int Map, string Kind, string Cells);

/// <summary>One player-created allowed area: which one it is, what it is called, and every set cell.
///
/// <para><b>Why this is a separate channel from <see cref="ReplicaArea"/>.</b> `AreaSync` carries the four built-in areas
/// and identifies them by <em>type</em> (`AreaManager.Home` / `BuildRoof` / `NoRoof` / `SnowOrSandClear`), which works
/// because vanilla makes exactly one of each. A player-created `Area_Allowed` has no such slot: the map holds a list of
/// up to 10 (`AreaManager.CanMakeNewAllowed` `:64211-64214`), each with a player-chosen label, and its `ID` comes from
/// `Find.UniqueIDsManager.GetNextAreaID()` (`:63996`) - a <em>per-peer</em> counter. Two peers that both created an area
/// on their own screen hold two objects, with different ids, and neither list is complete.</para>
///
/// <para><b>Identified by label, not by id.</b> The label is the only field that survives a save round trip for an area
/// created at runtime (`Area_Allowed.ExposeData` `:392903-392909` scribes `label` and `color`; the id is scribed by the
/// base `Area.ExposeData` `:63999-64003` but is meaningless across peers). Vanilla already enforces uniqueness across one
/// map's area list (`Dialog_RenameArea.NameIsValid` `:196418-196429` rejects a name already in use), which is what makes
/// the label usable as a key. The cost is that a rename has to travel as delete-plus-create, which is exactly how the
/// apply treats it.</para>
///
/// <para><b>Why the cells travel whole.</b> Same reason as <see cref="ReplicaArea"/>: a per-cell diff needs a baseline
/// both peers share, and a replica that connected after the area was drawn does not have one. `Cells` is a `;`-joined
/// list of set cells.</para></summary>
public sealed record ReplicaAllowedArea(int Map, string Label, string Cells);

/// <summary>One letter as plain values, rebuilt on the replica through <c>LetterMaker</c>.
///
/// <para><b>Why not the letter's own Scribe fragment.</b> <c>Letter.ExposeData</c> writes <c>lookTargets</c> as
/// <c>LookMode.GlobalTargetInfo</c>, which names things by load id and resolves them against the receiving peer's
/// object table. The letter and the thing it points at arrive in different frames (a raid's letter and the raiders),
/// so a decode would half-succeed and leave a letter that cannot be clicked. The cell and map are carried instead:
/// they are values, they are what the "jump to" button needs, and they cannot be wrong.</para>
///
/// <para><b>Why <paramref name="Choices"/> is a flag rather than a payload.</b> A <c>ChoiceLetter</c> subclass holds
/// behaviour and the state that behaviour needs (<c>ChoiceLetter_AcceptJoiner</c> knows its pawn and what accepting
/// does). Rebuilding one would need that state, and the decision would have to travel back as a command - a feature of
/// its own. Those letters arrive as a read-only <c>StandardLetter</c> with the same words, and the choice stays on the
/// host where the simulation is. Silently dropping them would be worse: the player would never learn the raid
/// happened.</para>
/// <para><b><c>Fragment</c> is what makes a choice letter answerable on a replica.</b> The plain-value projection above
/// is enough for a letter that only has to be read, but eleven letter types inherit `ChoiceLetter` and the player is
/// expected to pick from them: `ChoiceLetter_GrowthMoment` alone writes `skill.passion`, a trait, a sexuality trait and
/// two age-tracker fields inside `MakeChoices` (`:662198-662265`). Until this field existed `LetterSync.Apply` turned
/// every one of them into a `StandardLetter` and logged 「选项请到主机端操作」 — the player was shown a read-only copy
/// of a decision they were supposed to make.
///
/// <para>The fragment is `Letter.ExposeData` (`:182998-183009`) plus `ChoiceLetter.ExposeData` (`:182856-182864`) plus
/// the subclass's own: ten of the eleven measured subclasses scribe between one and eight fields, and only
/// `ChoiceLetter_GameEnded` scribes none. `LetterMaker.MakeLetter(LetterDef)` (`:182441`) instantiates by
/// `def.letterClass`, so the replica builds the *right* subclass and loads into it — no per-type switch, and a mod's
/// new choice letter travels without a code change.</para>
///
/// <para>Sending the whole fragment rather than the choices is deliberate: `traitChoices` is `LookMode.Deep` (a trait
/// has a degree and possibly a gene), `passionChoices` is a def list, and `enabledWorkTypes` is a label list. The
/// player must pick from exactly the options the host generated, not from options the replica re-rolls — the host
/// consumed `Rand` to produce them.</para>
public sealed record ReplicaLetter(int Id, string Def, string Label, string Text, int ArrivalTick,
    bool Choices = false, bool HasTarget = false, int X = 0, int Z = 0, int Map = -1, string? Fragment = null);

/// <summary>One on-screen message, as the host showed it. No id exists on a message, so the pair (tick, text) is the
/// identity; the replica does not need it - it only has to show the line once.</summary>
/// <summary>One on-screen message. `Map`/`X`/`Z` are the look target; `Historical` is what
/// `Messages.Message(Message, bool)` (`:185729`) decides: true puts the message in `Find.Archive`, false keeps it on the
/// live list only. Both were missing: the replica showed the text with a dead click target and never archived it, so
/// the two ends' history tabs disagreed by every message either end ever received (round 163).</summary>
/// <summary>State of the game's unnatural corpse (anomaly DLC). `UnnaturalCorpseTracker` (`:50860-50874`) is the
/// state machine behind the corpse's awakening, its teleporting and the research that destroys it; `ResidualCounterSync`
/// carried only `research` out of it, so on a replica the corpse sat in whatever state it had when the session started.
/// `Corpse` is the thing's `thingIDNumber`, not the tracker's own `loadID`, because the thing is what the replica has.</summary>
public sealed record ReplicaCorpseTracker(int Corpse, int AwakenTick, int DisappearTick, int SpawnedTick,
    int TicksToKill, int Haunted, int AwokenPawn, byte Escape, byte MentalBreak, byte TeleportIn,
    float Research, float ResearchToDestroy);

public sealed record ReplicaMessage(string Text, string Def, bool HasTarget = false,
    // -1 and not 0: `Map.uniqueID` is legitimately 0 for a game's first map, so 0 cannot mean "no map".
    int Map = -1, int X = 0, int Z = 0, bool Historical = true);

/// <summary>One cell whose terrain changed.
///
/// <para>Terrain is not a <c>Thing</c>: it lives in <c>TerrainGrid</c>'s three flat arrays and has no cell in the thing
/// grid, so <c>AuthoritativeFrame</c>'s thing enumeration can never carry it. Without this channel a replica draws the
/// ground it loaded, forever: a mined-out cave keeps drawing as rock, a built floor keeps drawing as dirt.</para>
///
/// <para><paramref name="Layer"/> is part of the identity, not a detail: the same cell holds a top layer, an under
/// layer and a foundation, and applying a value to the wrong array leaves the cell permanently wrong with no error.</para>
public sealed record ReplicaTerrain(int Map, int Index, string Def, int Layer = 0);

/// <summary>What one pawn is holding and wearing.
///
/// <para>Equipment and apparel live in <c>Pawn_EquipmentTracker</c> / <c>Pawn_ApparelTracker</c>, not in the map's thing
/// list, so the frame's thing enumeration can never carry them. A display replica runs no tick, so nothing ever equips
/// anything on it and the pawn keeps what the save was loaded with: a colonist holding a rifle is drawn empty-handed.</para>
///
/// <para><paramref name="State"/> is <c>def#stuff#quality</c> per entry, equipment then apparel, joined by <c>;</c> with
/// a <c>|</c> between the two groups. Three values decide what the player sees, and all three are values the replica can
/// rebuild from through <c>ThingMaker</c> - unlike the host's actual object, which already travels on the container
/// channel and would be duplicated if it were sent here as well.</para>
public sealed record ReplicaGear(int Pawn, string State);

/// <summary>One door's open/closed state.
///
/// <para><c>Building_Door.openInt</c> is a protected field driven by <c>Building_Door.Tick</c>. A display replica runs no
/// tick, so the field never moves: a door open at save time stays open for the session, a closed one stays closed while
/// the host's colonists file through it. The state also drives the door's glow and its reachability contribution, so the
/// wrong value makes a replica's pathing preview disagree with the host.</para>
///
/// <para>Only the boolean travels. Everything else a door needs to be drawn is already on the object from the save.</para>
/// </summary>
public sealed record ReplicaDoor(int Id, bool Open);

/// <summary>One cell's roof.
///
/// <para>A roof is not a <c>Thing</c>: it lives in <c>RoofGrid.roofGrid</c>, one flat array indexed by cell. A replica
/// that misses a roof change draws no roof over a colony that built one, and - because <c>Roofed()</c> feeds room stats -
/// its temperature and light model disagree with the authority's.</para>
///
/// <para><paramref name="Def"/> is empty for "no roof here", which is a real state: removing a roof is exactly as
/// visible as adding one, so an empty name must travel rather than being omitted.</para>
/// </summary>
public sealed record ReplicaRoof(int Map, int Index, string Def);

/// <summary>One cell's snow depth.
///
/// <para>Snow is neither a <c>Thing</c> nor terrain: it lives in <c>SnowGrid.depthGrid</c>, one float per cell, and
/// accumulates from weather on a tick a replica never runs. A replica therefore keeps whatever depth the save was loaded
/// with - a colony in a blizzard that looks like midsummer - and because depth also feeds the cell's path cost and its
/// <c>WeatherBuildupCategory</c>, movement and snow-clearing work diverge as well.</para>
///
/// <para><paramref name="Depth"/> is quantised to 1/64 by the sender. Full floats would put an entry per cell per frame
/// on the wire forever for a change no mesh can show.</para></summary>
public sealed record ReplicaSnow(int Map, int Index, float Depth);

/// <summary>One room's indoor temperature, identified by a representative cell rather than by <c>Room.ID</c>.
///
/// <para>A room's temperature only moves inside <c>RoomTempTracker.EqualizeTemperature</c>, which <c>MapTemperatureTick</c>
/// calls every 120 ticks - ticks a replica never runs - so every indoor room keeps the value the save was loaded with. A
/// freezer reads room temperature forever and a heated bedroom never warms up.</para>
///
/// <para><b>Why a cell and not <c>Room.ID</c>.</b> That id is a process-local counter (<c>Room.MakeNewRoom</c> assigns
/// <c>nextRoomID++</c>) and is never scribed, so two peers holding the same save number their rooms differently - and rooms
/// keep merging and splitting as walls go up. ColonyTogether solves it the same way (<c>ReceiveOutdoor</c>,
/// ct045:142307): carry a cell, resolve with <c>GetRoom(cell)</c> on the far end. A room that no longer covers that cell
/// becomes a no-op instead of an error.</para>
///
/// <para>Outdoor rooms need nothing: their temperature is a computed getter over the world tile temperature, which is
/// derived from <c>absTick</c>, and <see cref="TurboTick.ReplicaAbsClockPatch"/> already makes that the host's.</para>
/// </summary>
public sealed record ReplicaRoomTemp(int Map, int X, int Z, float Temp);

/// <summary>One plant's growth stage, packed into a byte.
///
/// <para>Growth advances in <c>Plant.TickLong</c> (<c>growthInt += GrowthPerTick * 2000f</c>), which a replica never runs,
/// so every plant keeps the growth the save was loaded with - a crop reads the same "23% grown" all season and a bush never
/// becomes harvestable. <c>growthInt</c> is scribed, so the value is not the problem: no channel ever re-sends it. The frame's
/// object scan skips an object whose position/rotation/stack/hitpoints did not change, which is exactly a growing plant, and
/// <c>SweepItemStates</c> only walks <c>ThingCategory.Item</c>.</para>
///
/// <para>Bit 0 = mature (<c>growthInt > 0.999</c>), bit 1 = harvestable (<c>growthInt > def.plant.harvestMinGrowth</c>),
/// low nibble of the upper byte = the tenth-of-growth tier the mesh draws. Only three facts are ever read from the float, and
/// two of them are one-bit questions - sending the raw value would put all 36 181 plants on this save on the wire every frame
/// to describe a number the game only compares against thresholds.</para>
/// </summary>
public sealed record ReplicaPlant(int Map, int Thing, byte Stage);

/// <summary>One directed faction standing: what <c>faction</c> thinks of <c>other</c>.
///
/// <para>Goodwill changes in <c>Faction.TryAffectGoodwillWith</c> - gifting, quests, incidents, social fights - on ticks a
/// replica never runs, so the faction page shows the load-time number forever. And because <c>kind</c> is derived from the
/// number, an ally that turned hostile mid-session still reads as an ally: caravans, trade and raids all follow it.</para>
///
/// <para><b>Both facts are needed.</b> <c>CheckKindThresholds</c> derives <c>kind</c> from goodwill (-75 hostile, +75 ally,
/// 0 back to neutral) but only where <c>HasGoodwill</c> holds; a permanent enemy is set through <c>SetRelationDirect</c> and
/// its kind is independent of any number.</para>
///
/// <para><b>Directed, not symmetric.</b> Vanilla writes both sides in <c>TryAffectGoodwillWith</c>, but <c>SetRelation</c>
/// replaces the other side's record with a fresh one whose <c>baseGoodwill</c> is the default 100 - so the two directions can
/// genuinely disagree and a symmetric channel would silently pick one.</para>
///
/// <para><c>loadID</c> is scribed (<c>Faction.GetUniqueLoadID</c> is <c>"Faction_" + loadID</c>), so unlike a room id it is the
/// same on both ends.</para>
/// </summary>
public sealed record ReplicaFactionRelation(int Faction, int Other, int Goodwill, byte Kind);

/// <summary>Where one world object sits on the planet.
///
/// <para>Caravans, settlements, ships and sites live in <c>Find.WorldObjects</c>, not in any map's thing grid, and they move
/// through their own interval ticks (<c>Caravan_PathFollower.TryEnterNextPathTile</c> assigns <c>caravan.Tile = nextTile</c>).
/// A replica never runs those, so a caravan that has been travelling for a season still draws at its origin.</para>
///
/// <para><b><c>Layer</c> is carried.</b> A <c>PlanetTile</c> is a tile id <i>and</i> a layer id, and <c>new PlanetTile(id)</c>
/// silently assumes layer 0 - on a multi-layer planet (surface plus orbit) that resolves to the wrong tile, so the layer has
/// to travel too or the object lands on a different world.</para>
///
/// <para><b><c>Def</c> is carried as a guard.</b> <c>ID</c> is the only identity on the wire, and a save that grew between the
/// two peers could reuse one; a wrong pair would otherwise teleport the wrong object across the planet.</para></summary>
public sealed record ReplicaWorldObject(int Id, int Tile, int Layer, string? Def);

/// <summary>The scribed state of one world object, minus what <see cref="ReplicaWorldObject"/> already carries.
///
/// <para><b>Body</b> is the object's own Scribe fragment with <c>def</c>, <c>tile</c>, <c>ID</c>, <c>tickDelta</c>,
/// <c>faction</c> and <c>questTags</c> removed. The first three belong to <see cref="ReplicaWorldObject"/>; <c>tickDelta</c>
/// is a frame counter <c>WorldObject.DoTick</c> increments every tick (<c>:798651</c>) and would otherwise make every object
/// differ from itself forever, which is the same defect that made the plant sweep send all 3600 plants every pass.</para>
///
/// <para><b>A whole fragment rather than named counters</b> because a world object's comps are written inline at the same
/// level as its own fields: measured, <c>Settlement #0</c>'s fragment carries
/// <c>&lt;ticksLeftToForceExitAndRemoveMap&gt;7200&lt;/ticksLeftToForceExitAndRemoveMap&gt;</c> at top level with no
/// <c>&lt;comps&gt;</c> wrapper. There is no subtree to slice out the way <see cref="ReplicaQuestParts"/> slices
/// <c>&lt;parts&gt;</c>, so the channel compares the rest of the document instead.</para></summary>
public sealed record ReplicaWorldObjectState(int Id, string? Def, string Body);

/// <summary>A world object the authority created, sent once so the replica can build it.
///
/// <para><b>Why a whole Scribe fragment and not a handful of fields.</b> A caravan is a `ThingOwner` of pawns plus a
/// pather, a forage tracker, a needs tracker and a trader; a settlement carries its inhabitants, its timer comps and its
/// faction; a site carries a `Quest`. Naming those fields here would be a second copy of `WorldObject.ExposeData` and
/// its subclasses' - the list that silently stops matching the code it describes. `ScribeBridge.Save`/`Load` run the
/// game's own writer and reader, which is where the developers maintain it.</para>
///
/// <para><b>Why the tile travels beside the fragment rather than only inside it.</b> `WorldObject.ExposeData` writes
/// `tile` against the field, so the fragment normally carries it - but a fragment produced while the object's tile is
/// still invalid (a caravan being formed) would leave `WorldObjectsHolder.Add` logging an error and dropping the object
/// on tile 0. The two numbers here are the fallback that keeps the object placeable, and `Add` refuses an object with
/// no valid tile anyway.</para></summary>
/// <param name="Id">The authority's `WorldObject.ID`. Both peers must agree on it; this is the identity every later
/// position and state row is addressed by.</param>
/// <param name="Layer">`PlanetTile.layerID`, because an id alone assumes the surface layer.</param>
public sealed record ReplicaWorldObjectCreation(int Id, string Def, int Tile, int Layer, string Body);

/// <summary>A quest's time-driven standing, applied onto the replica's own <c>Quest</c> object.
///
/// <para>A quest is accepted (<c>Quest.Accept</c> sets <c>acceptanceTick = TicksGame</c>, then <c>Initiate</c>) and ends
/// (<c>Quest.End</c> sets <c>ended</c> and <c>endOutcome</c>, then <c>CleanupQuestParts</c>) on the host. A replica runs
/// neither, so an offer accepted a season ago still reads as pending and a finished quest still reads as ongoing.</para>
///
/// <para><b>The inputs travel, not <c>QuestState</c>.</b> That enum is a getter over these four fields plus
/// <c>TicksUntilExpiry</c>, which moves on its own as the clock advances - a sent enum would be stale by the next tick, while
/// the replica's own getter answers the same question the same way from the same inputs.</para>
///
/// <para><b>The quest is never rebuilt.</b> <c>QuestManager.Add</c> calls <c>PostQuestAdded</c>, <c>Initiate</c>, registers
/// signal receivers and notifies every <c>QuestPart</c>; the object already came in with the save, so only the fields the
/// passing of time would have written are written here.</para></summary>
public sealed record ReplicaQuest(int Id, int AcceptTick, byte Ended, byte Outcome, int ExpireTick, byte Dismissed);

/// <summary>How long one memory has been held, applied onto the replica's own <c>Thought_Memory</c>.
///
/// <para>A memory's age moves in exactly one place - <c>Thought_Memory.ThoughtInterval</c> (<c>age += 150</c>), driven by
/// <c>MemoryThoughtHandler.MemoryThoughtInterval</c> - and the age is what decides whether it still hurts (<c>ShouldDiscard</c>
/// is <c>age &gt; DurationTicks</c>). A replica runs neither, so <b>no memory ever expires</b>: a colonist whose friend died a
/// season ago still carries the full grief, and the needs tab lists every thought with the mood it contributed at load.</para>
///
/// <para><b>Mood itself never travels.</b> <c>Need_Mood.CurInstantLevel</c> is a getter that recomputes from
/// <c>thoughts.TotalMoodOffset()</c>, so the mood card is not frozen - it is answering from stale inputs. Fixing the inputs is
/// enough, the same shape as sending a plant's stage rather than its derived growth.</para>
///
/// <para><b><c>Key</c> is a triple, not a def.</b> A pawn holds several memories of one def (one per <c>otherPawn</c>), and the
/// same social memory can repeat, so the key carries def name, the other pawn, and the index inside that group.</para></summary>
public sealed record ReplicaMemory(int Pawn, string Key, int Age);

/// <summary>One entry in the colony's story log.
///
/// <para><c>TaleRecorder.RecordTale</c> only runs on the authority and <c>TaleManager.Add</c> is what puts the result in the
/// list the history page and <c>ArtGenerationContext</c> read - so a replica holds exactly the tales the save came with: "art"
/// descriptions never mention anything that happened since, and an expirable tale that aged out still sits there.</para>
///
/// <para><b>Why the whole list is affordable.</b> Measured 4 tales on this save, and a tale is the authority's own story - a
/// replica cannot derive one. Four rows are cheaper to send whole than to invent a diff protocol for.</para>
///
/// <para><b>Why interest needs only the counter.</b> <c>Tale.InterestLevel</c> is a getter over <c>def.baseInterest</c>,
/// <c>uses</c> and <c>AgeTicks</c>, and <c>AgeTicks</c> is <c>TicksAbs - date</c> - already equal on both ends because the
/// replica's clock follows the host's. So the counter the authority increments is the only input that has to travel.</para>
///
/// <para><b>A tale is never rebuilt.</b> <c>TaleManager.Add</c> runs <c>CheckCullTales</c>, which can evict other tales to make
/// room. Only the fields the passing of time writes are written here.</para></summary>
public sealed record ReplicaTale(int Id, string? Def, int Uses, int Date, string? Label, byte Hidden);

/// <summary>One cell's fog of war.
///
/// <para>Fog is a bit array in <c>FogGrid.fogGrid</c> - not a <c>Thing</c>, not terrain, not a roof - and it lifts when a
/// pawn walks somewhere on a tick a replica never runs. A replica without this keeps the fog it loaded with: a colonist
/// explores a new wing and walks into a black rectangle that never clears.</para>
///
/// <para>It is also the channel where being wrong is worst out of proportion: fog hides terrain, things, roofs and snow,
/// so every other map channel's work is wasted on a cell nobody can see.</para></summary>
public sealed record ReplicaFog(int Map, int Index, bool Fogged);

/// <summary>One pawn's mental break, or the end of one.
///
/// <para>A mental state is started by a mood check inside a tick interval a replica never runs, so a colonist who breaks
/// down on the host keeps loading-time state on the replica: standing calmly while the authority's copy wanders in a daze
/// or attacks a wall. The player sees a colony that is not in trouble.</para>
///
/// <para><paramref name="Def"/> is the <c>MentalStateDef</c> name, or empty for "no longer in a state" - recovery is as
/// visible as the break, and an empty name must travel rather than being omitted.</para></summary>
// `Def` is `defName` or `defName#age`, not a bare def name: `MentalState_Wander.ShouldExitMapNow` sends a pawn home
// after a day of wandering, so the age is part of what the state means. The field kept its name so the JSON shape
// is unchanged; only the string's contents grew.
public sealed record ReplicaMental(int Pawn, string Def);

/// <summary>One pawn's inspiration, or the end of one.
///
/// <para>Inspirations are rolled inside a 100-tick interval on the mind state, which a replica never runs, so a colonist who
/// becomes inspired on the host shows nothing on the replica: no icon in the needs card, no line in the inspect pane.</para>
///
/// <para><paramref name="State"/> is <c>defName#ageDays</c>, or empty for "not inspired". The age is quantised to whole
/// days because that is what the needs-card progress bar shows and an inspiration lasts several of them - raw ticks would
/// put an entry per inspired pawn on every frame.</para></summary>
public sealed record ReplicaInspiration(int Pawn, string State);

/// <summary>One working pawn's remaining toil ticks.
///
/// <para><paramref name="TicksLeft"/> of -1 means "this pawn is no longer working" and is never written to a driver. It
/// exists because the frame channel merges a dropped frame's entries into the next one: without an explicit clearing entry
/// a pawn that stopped working would keep the last bar it ever had.</para></summary>
public sealed record ReplicaToilProgress(int Pawn, int TicksLeft);

/// <summary>The subclass counters of one pawn's current <see cref="Verse.AI.JobDriver"/>, encoded as `name=value` pairs.
///
/// <para><b>Why this is a string rather than a list of records.</b> A driver holds between zero and three such counters
/// (a plant harvester has `workDone`, a cleaner has two, a painter one) and the names are type-specific. Encoding them as
/// <c>name=value</c> joined by <c>;</c> keeps the frame one entry per working pawn regardless of job, and the replica
/// looks each name up on its own driver by reflection - the same shape `SkillSync` and `HediffTimerSync` already use.
/// </para>
/// 
/// <para><b>Why not `JobDriver.ExposeData`.</b> It is <c>IExposable</c> and a replica does rebuild drivers, but its
/// <c>ExposeData</c> writes exactly ten base fields (`ended`, `curToilIndex`, `ticksLeftThisToil`, `wantBeginNextToil`,
/// `startTick`, `rotateToFace`, `asleep`, `uninstallWorkLeft`, `nextToilIndex`, `collideWithPawns`). Measured against the
/// twenty custom <c>WithProgressBar</c> getters in the assembly: `workDone / harvestWork`, `sowWorkDone / sowWork`,
/// `paintingTime`, `removalTime`, `totalCleaningWorkDone / totalCleaningWorkRequired`, `gatherProgress / WorkTotal`,
/// `bottleNutrition / initialNutritionNeeded`, `workLeft / BaseWorkAmount`, `workDone / 5000f`, `workDone / 5600f` - not
/// one of those fields is in the payload, so the bar is not stale, it is never derived from anything that travels.</para>
/// 
/// <para><b>Why the values are quantised at the publisher and the fraction is not sent instead.</b> The denominator is a
/// def value or a `const` in the driver, so it is identical on both sides; sending a fraction would let a replica whose
/// denominator disagreed show a bar that is wrong in a way no counter explains. Integers are quantised to the step the
/// bar can show, which also keeps a work tick from producing a frame entry every tick.</para></summary>
// The field is named `Fragment` rather than `Counters` because the payload changed shape: it is now the driver's
// own Scribe fragment, not a name=value list of counters, so a name that says "counters" would describe a payload
// this no longer carries.
public sealed record ReplicaDriverState(int Pawn, string Fragment);

/// <summary>One sanguophage's deathrest progress and capacity limit, as `ticks:capacity`.
///
/// <para>Two values in one string rather than two records: they are written by different code paths but always belong to
/// the same gene, and a frame that carried one without the other would show a percentage against the wrong limit.</para>
///
/// <para>`adjustedDeathrestTicks` is deliberately absent - it has writers and no readers anywhere in the assembly, so it
/// cannot change what a player sees.</para></summary>
public sealed record ReplicaDeathrest(int Pawn, string Values);

/// <summary>One pawn's busy stance, stun and stagger as one encoded string.
///
/// <para>An empty string is a real message: "this pawn is mobile, unstunned and unstaggered". It has to be sent once when
/// a pawn stops being busy, or the replica keeps drawing the aim pose forever.</para></summary>
public sealed record ReplicaStance(int Pawn, string Values);

/// <summary>One lord's counters, keyed by `Lord.loadID` (`:268003`).
///
/// <para>`ticksInToil` is read by toil transitions (`:325375`, `:325394`) and by staged toils that fire on multiples of
/// it (`:326388`, `:326764`, `:328360`), and <c>numPawnsLostViolently == 0</c> is what lets a departing visitor leave a
/// gift (`:270692`). None of them advance on a replica. The ritual progress fields ride along because
/// `LordJob_Ritual.Progress` is `ticksPassedWithProgress / durationTicks` (`:323753`) and that sum is only advanced from
/// a `LordToil` tick action (`:324104`).</para></summary>
public sealed record ReplicaLord(int Lord, string Values);

/// <summary>One quest's `<parts>` fragment, keyed by `Quest.id`.
///
/// <para>An empty string is a real message - the authority has no parts left for this quest - and has to be sent once or
/// the replica keeps printing the last description it received.</para></summary>
public sealed record ReplicaQuestParts(int Quest, string Parts);

/// <summary>One pawn's sub-tracker counters, keyed by `Thing.thingIDNumber`, encoded as
/// `g:guilt;t:decayFrom;l:desireTicks;f:state:lerp:cooldown:flying` with absent trackers omitted.
///
/// <para>An empty string is a real message - this pawn has no guilt, no training decay, no pending learning desire and is
/// not flying - and has to be sent once when that becomes true, or the replica keeps drawing a countdown that ended.</para></summary>
public sealed record ReplicaPawnTracker(int Pawn, string Trackers);

/// <summary>Every timed condition on one pawn, encoded as `hediffKey;name;value` entries joined by `|`.
///
/// <para>The key is `defName#partDefName#severity` - the same triple the hediff channel rebuilds from, because the two
/// have to be matched up on the far side and a comp named any other way cannot find its condition.</para></summary>
public sealed record ReplicaHediffTimers(int Pawn, string Timers);

/// <summary>One pawn's abilities: how many charges are left on each, and how far into its cooldown it is.
///
/// <para><b>Why the pawn payload does not already carry this.</b> It would if `AuthoritativeFrame.PawnState` were used -
/// `Pawn_AbilityTracker.ExposeData` scribes `abilities` with <c>LookMode.Deep</c> (<c>:468414</c>) and
/// <c>Ability.ExposeData</c> writes all six counters (<c>:433725-433733</c>). That field is passed as `null`
/// (`Source/HeadlessRuntime.cs:1752`), so no channel carries a pawn's own scribe fragment at all and each subsystem that
/// needs one has its own: this is the abilities'.</para>
///
/// <para><b>What a replica gets wrong without it.</b> `GizmoDisabled` reads <c>OnCooldown</c> and <c>charges</c> together
/// (<c>:433138</c>, <c>:433143</c>), and the extra label prints <c>$"{charges} / {maxCharges}"</c> (<c>:432544</c>) - so a
/// pawn who spent their last charge still shows the full bar, and a power that is recharging still draws as ready. Neither
/// is cosmetic: the bar is what the player looks at before deciding whether to fire it.</para></summary>
public sealed record ReplicaAbilityCharges(int Pawn, string Charges);

/// <summary>One active weather condition.
///
/// <para><paramref name="StartTick"/> is sent rather than an expiry because <c>GameCondition.Expired</c> is a pure
/// function of <c>StartTick + Duration</c> against the tick - and the replica's tick already equals the authority's.
/// <paramref name="Derived"/> carries only the per-class numbers the replica cannot recompute from the def: the
/// temperature offsets are set from the def in <c>Init()</c> and then moved by the class's own tick.</para></summary>
public sealed record ReplicaGameCondition(int Id, int Map, string Def, int StartTick, bool Permanent, int Duration,
    string? Derived);

public sealed record ReplicaGameConditionRemoval(int Map, int Id);

/// <summary>One pawn's biological age.
///
/// <para>Only the counter and the growth fraction travel: the life stage is a pure function of the age against the
/// def's `lifeStageAges`, and `AgeBiologicalTicks`'s setter recomputes it. <paramref name="Growth"/> of -1 is the
/// game's own "not applicable" for a pawn that does not grow.</para></summary>
public sealed record ReplicaPawnAge(int Pawn, long AgeTicks, float Growth,
    // Ticks of growth accumulated toward the next growth tier, and the deadline by which an age reversal is
    // demanded. Both are counters the pawn's own tick advances, so a replica keeps whatever the save held - and
    // the deadline is read by a mood thought with four stages and by the job giver that sends a pawn to a pod.
    // `Reversal` is the remaining ticks (deadline minus age), -1 meaning "no demand at all"; sending the deadline
    // rather than the absolute stamp keeps the value exact even though the age itself is day-quantised.
    float GrowthPoints = -1f, long Reversal = -1);

/// <summary>The three misery counters behind a mental break, in ticks below each threshold.
///
/// <para>Not derivable from current mood: they are the integral of mood over time and reset on every recovery, so two
/// pawns at the same mood have different counters. All three travel because `BreakMinorIsImminent` is false while
/// `BreakMajorIsImminent` is true.</para></summary>
public sealed record ReplicaMentalBreaker(int Pawn, int Minor, int Major, int Extreme);

/// <summary>One pawn's immunity records, encoded `defName=immunity` joined by `;`.
///
/// <para>The whole list travels rather than a delta: the game drops a record the moment its immunity reaches zero, so a
/// record that is absent from this string is a record that should be gone.</para></summary>
public sealed record ReplicaImmunity(int Pawn, string Records);

/// <summary>One pawn's guest status and, for a prisoner, how close they are to joining.
///
/// <para>Guest status is set by gameplay on ticks a replica does not have - capturing a downed raider, a successful
/// recruit, a slave rebellion - so on a replica the field keeps its load-time value. Everything the player reads follows
/// it: the prisoner tab, the "Prisoner" tag, the bar entry's colour, the prisoner filter.</para>
///
/// <para><paramref name="State"/> is <c>status#resistance#will</c> with the numbers quantised to whole points, or empty for
/// "not a guest". Resistance falls a fraction at a time over many attempts and the bar the player watches has about ten
/// visible steps, so whole points is what the panel can show.</para></summary>
public sealed record ReplicaGuest(int Pawn, string State);

/// <summary>One pawn's trait list, as `defName#degree` entries.
///
/// <para><b>Why traits matter enough to carry.</b> `Pawn_StoryTracker.DisabledWorkTags` (`:460027-460031`) unions
/// <c>trait.def.disabledWorkTags</c> over the non-suppressed traits, and that set decides which work types the pawn may
/// ever be assigned to. A replica whose traits never left the save file keeps the load-time work tags no matter what
/// the authority did afterwards, so the pawn looks assignable to work it cannot do (and vice versa).
/// `StatWorker.GetValueUnfinalized` (`:715774-715781`) also reads every non-suppressed trait's
/// <c>OffsetOfStat</c>, so work speed, market value, beauty and insulation all come out wrong too.</para>
///
/// <para><b>Why not the pawn's whole fragment.</b> `Pawn.ExposeData` scribes `story` (`:131390`) and `genes` (`:131426`)
/// with `Scribe_Deep`, so a full pawn fragment does carry traits - but sending one is what this mod deliberately avoids:
/// the census measures 71 pawn fragments at 636380 B, 224x the position frame. Traits change on a scale of days, so they
/// travel on their own channel and cost nothing in between.</para>
///
/// <para><b>Why `defName#degree` and not the trait's scribe form.</b> `Trait.ExposeData` (`:483418-483428`) scribes
/// `sourceGene` and `suppressedByGene` as *references*, and `TraitSet.ExposeData`'s PostLoadInit pass
/// (`:483790-483803`) then **removes** every trait whose `sourceGene` no longer forces it. A reference that resolves to
/// null on the receiver would therefore delete a gene-granted trait outright. The def and degree are what
/// `OffsetOfStat` and `disabledWorkTags` actually read, so those are what travel; suppression is carried as the two
/// booleans the trait itself owns, and the gene-derived part is left to the gene channel if one is added later.</para></summary>
/// <summary>One pawn's titles and bought permits, as `factionLoadId:defName` lists.
///
/// <para><b>Why a channel and not `PawnState`.</b> `Pawn_RoyaltyTracker.ExposeData` (`:475905`) would carry all of it,
/// but `AuthoritativeFrame.PawnState` is `null` by measurement: a whole pawn fragment is 8963 B
/// (`pawn-payload-size`), 224 times the per-frame position row, so it can only travel when something changes and
/// nothing decides that per pawn. Royalty changes on a scale of days, so it is exactly the kind of field a named
/// channel is for.</para>
///
/// <para>Permits carried here are the <em>bought</em> ones (`AllFactionPermits`), which is what
/// `PermitsCardUtility` lists. The permits a title grants are re-derived from the title by `HasPermit`, so they are
/// deliberately not sent twice.</para></summary>
public sealed record ReplicaRoyalty(int Pawn, string State);

public sealed record ReplicaPawnTraits(int Pawn, string Traits);

/// <summary>One pawn's genes, as `x:def,def|e:def,def`.
///
/// <para><b>Why genes need their own row when traits now have one.</b> They are two independent lists that both feed
/// `StatWorker.GetValueUnfinalized` (`:715798-715813` reads `gene.def.statOffsets`; `:715774-715781` reads
/// `trait.OffsetOfStat`), and they are linked in one direction: `Pawn_GeneTracker.AddGene` adds the gene's
/// `forcedTraits` through `GainTrait` with `sourceGene` set, and `TraitSet.RecalculateSuppression`
/// (`:484101-484134`) turns `gene.def.suppressedTraits` into `Trait.suppressedByGene`. Carrying traits alone would
/// therefore report a gene-granted trait as permanent and a gene-suppressed trait as active.</para>
///
/// <para><b>Why the xenogene/endogene split travels.</b> The two lists differ in how they are inherited, in whether
/// `ClearXenogenes` can touch them, and in how `Gene.Overridden` resolves across the pair; a single merged list could not be
/// rebuilt into the same tracker.</para></summary>
public sealed record ReplicaPawnGenes(int Pawn, string Genes);

/// <summary>One pawn's flight state: on the ground, taking off, flying or landing, and how far into it.
///
/// <para>`Pawn_FlightTracker.FlightTick` (`:729158-729207`) is the only writer of all five fields and it runs from
/// `Pawn.Tick`, which a display replica never runs. The visible consequences are three: a pawn drawn walking while the
/// host's copy is airborne, a pawn that hovers forever because `flyingTicks` never reaches `MaxFlightTicks`
/// (`:729193`), and an ability that stays unavailable forever because `flightCooldownTicks` never decrements
/// (`:729201`) while `CanFlyNow` (`:729135-729140`) reads it.</para>
///
/// <para><b>Payload</b> is `state:flyingTicks:cooldown:lerpTick`, state one of `grounded`/`takingoff`/`flying`/`landing`
/// by name - an enum's numeric value is not part of the save format, so putting it on the wire breaks silently if the enum
/// is ever reordered. The cooldown is quantised to 15 ticks and stays exact at zero, because zero is the only value
/// `CanFlyNow` compares against. See `FlightSync`.</para></summary>
public sealed record ReplicaFlight(int Pawn, string Values);

/// <summary>One ideoligion's precept set, and which pawns hold its roles.
///
/// <para><b>Why this is not part of `ReplicaPawnIdeo`'s table.</b> That table carries an ideoligion's `id`, name, colour and
/// development tracker. Precepts are a separate, much larger list that changes on a different schedule (a reform, not a
/// conversion), and `Precept-Role` carries a *holder*, which is per-pawn state attached to an ideoligion-wide object. One
/// record with a clear owner is easier to read in a census than a fifth field on a semicolon-joined string.</para>
///
/// <para>See `PreceptSync` for why the def list is a simulation input rather than decoration.</para></summary>
public sealed record ReplicaPreceptTable(int Id, string Payload);

/// <summary>One research project's progress, as a whole percent of its cost.
///
/// <para>Progress is accumulated by <c>ResearchManager.AddProgress</c> from work toils a replica never runs, so the research
/// tab shows the load-time percentage forever: a project the host finished still reads 30% on the replica and the bar never
/// moves.</para>
///
/// <para>Per project rather than just the current one: research is cumulative, the tab shows a bar for every project the
/// player has started, and a player switching back to a half-finished one would otherwise see the bar jump backwards.</para>
/// </summary>
public sealed record ReplicaResearch(string Project, int Percent);

/// <summary>Storyteller adaptation: two counters that scale every future raid.
///
/// <para><b>Why they travel at all.</b> Both grow inside `StoryWatcher.StoryWatcherTick` (`:392454`), which sits in
/// `MapPostTick` (`:52940`) - the one gate a display replica never passes. `adaptDays` reaches the raid budget through
/// `StorytellerUtility.DefaultThreatPointsNow` (`:391870-391872`: `Mathf.Lerp(1f, TotalThreatPointsFactor,
/// difficulty.adaptationEffectFactor)`), so a long-running colony's raids are the wrong size everywhere the replica looks -
/// and the history graph plots the raw value (`HistoryAutoRecorderWorker_Adaptation.PullRecord` `:362916`).</para>
///
/// <para><b>Why the raw days and not a percentage.</b> The factor is `pointsFactorFromAdaptDays.Evaluate(adaptDays)`
/// (`:392483`), an arbitrary curve from the storyteller def; a replica can only reproduce it by evaluating the same curve
/// at the same input, so the input is what travels.</para></summary>
public sealed record ReplicaAdaptation(float AdaptDays, float PopAdaptDays);

/// <summary>The colony's death countdown.
///
/// <para>Both fields, not just the number: `gameEnding` is what starts the countdown and `ticksToGameOver` is the
/// remaining time, and `-1` is the field's own "not ending" default (`GameEnder.ticksToGameOver = -1`, `:361619`), so a
/// count alone cannot tell a running countdown from a stale value. Decremented only by `GameEnder.GameEndTick`
/// (`:361748`), which raises the `GameOverEveryoneDead` letter at exactly zero - the one message a player must not
/// miss, and one a display replica can never produce for itself.</para></summary>
public sealed record ReplicaGameEnder(bool Ending, int TicksLeft);

/// <summary>Orbiting ships' remaining time in comms range, as one encoded list.
///
/// <para>`PassingShip.ticksUntilDeparture` is decremented only by `PassingShipTick` (`:606172`) and the ship removes
/// itself at zero (`Depart` `:606181`, which also posts `MessageShipHasLeftCommsRange`). A display replica runs neither, so
/// a ship that has long since gone still sits in the comms console's contact list. Not a whole-object fragment on purpose:
/// the list is scribed with `LookMode.Deep` (`:606267`) and would re-send each trade ship's stock and trader kind on every
/// countdown tick, while the only field that changes with time is this count.</para>
///
/// <para>Wire: `id:剩余:名称` joined by `;`, sorted, quantised to 250 ticks. A ship absent from the payload is one whose
/// countdown has ended, and the replica calls the same `RemoveShip` the game would.</para></summary>
public sealed record ReplicaPassingShips(string Ships);

/// <summary>The counters left over once every subsystem with a channel of its own is accounted for.
///
/// <para>Nine numbers on five different kinds of object - a pawn's drug records, an ideoligion's development tracker, a
/// growth-moment letter, a ritual toil's wait step, a corpse tracker. None is swept by anything and every writer runs only
/// inside a tick a replica does not run. They share one channel because they share one shape: `<c>Class.field#owner#value`</c>
/// joined by <c>;</c>, quantised to 150 ticks, sent only on change. The key is the gap list's own name, so the census row
/// reads straight against that list.</para></summary>
public sealed record ReplicaResidualCounters(string Rows);

/// <summary>Which ideoligion a pawn belongs to, how certain they are, and what each ideoligion is called.
///
/// <para>`Pawn_IdeoTracker.ExposeData` scribes all of it (`:472487-472493`) through `Pawn.ExposeData`, and
/// `AuthoritativeFrame.PawnState` is passed as `null`, so nothing of the sort reaches a replica. Two addresses in one
/// record type: <c>Pawn &gt; 0</c> is a pawn's entry, <c>Pawn == 0</c> is the world's ideoligion table. `thingIDNumber`
/// starts at 1, so zero cannot collide with a pawn.</para>
///
/// <para>Pawn payload is <c>ideoId#certainty#joinTick#previousIds</c>; table payload is <c>id#name#colorDef</c> joined by
/// <c>;</c>. Certainty is quantised to 0.01 because it is drawn as a percentage; `joinTick` is not quantised because it is
/// an absolute stamp, not a countdown.</para></summary>
/// <summary>Two whole `Game`-owned discovery databases: `hiddenItemsManager` and `entityCodex`, as Scribe fragments
/// joined by a newline.
///
/// <para><b>Why both ride in one row.</b> They are the same object (`Game`), the same shape (`Scribe_Deep` of a
/// collection table), and change from the same cause — spawn and visibility events a replica does not generate. Two
/// rows would mean two frame fields for one fact, and either could arrive without the other.</para>
///
/// <para><b>Why the whole tables.</b> `hiddenItemDefs` starts with every `hiddenWhileUndiscovered` def set to true and
/// `SetDiscovered` flips one entry false; the key set comes from the def database rather than the save, so a def added
/// by a mod after the save was written would land on the wrong side of a diff. Sending the whole table measures 1857 B
/// and only travels when it changes.</para></summary>
public sealed record ReplicaDiscovery(string Payload);

public sealed record ReplicaPawnIdeo(int Pawn, string Payload);

/// <summary>Which ideoligion one faction believes, plus the minor ones it tolerates.
///
/// <para><b>Why the ids and not the objects.</b> `Ideo.id` is written by `Ideo.ExposeData` (`:492125`,
/// `Scribe_Values.Look(ref id, "id", 0)`) and assigned once from `Find.UniqueIDsManager.GetNextIdeoID()`
/// (`:493978-494044`), so two peers that loaded the same save agree on it. That is the same id `IdeoSync` already
/// resolves through, which is why the two channels cannot disagree about which ideoligion is which.</para>
///
/// <para><b>Why `Primary` is a separate field rather than the first entry of a list.</b> `ideosMinor` may legitimately
/// contain the primary as well - `RecalculateIdeosBasedOnPlayerPawns` (`:487516-487567`) clears the list and re-adds
/// every ideoligion its colonists believe, then picks the most popular as primary. A single list would lose the
/// distinction between "primary" and "minor that happens to be here".</para>
///
/// <para><b>Why 0 means none.</b> `Ideo.id` is handed out from 1 (`GetNextIdeoID` starts at 0 and pre-increments), so 0
/// is safe as "no primary" - which is a real state: a faction with `ModsConfig.IdeologyActive` off, and every faction
/// during world generation before `ChooseOrGenerateIdeo` runs.</para></summary>
public sealed record ReplicaFactionIdeo(int Faction, int Primary, string Minors);

/// <summary>When each faction last granted a request, so the dialog shows the same wait everywhere.
///
/// <para>The three stamps are written only in the clicking player's own process
/// (<c>Assembly-CSharp.decompiled.cs:486930</c>, <c>:486866</c>, <c>:487127</c>) and read back into the button labels
/// (<c>:486895</c>, <c>:486826</c>, <c>:486959</c>). On a replica they therefore stay at the -9999999 they load with
/// (<c>:485260-485264</c>), every request reads as available, and the same player can request the same caravan from a
/// second colony - the queue on the authority then holds two.</para>
///
/// <para>All three travel in one row because they are three integers on the same <c>Faction</c> and have the same
/// lifetime: sending them as three channels would triple the bookkeeping for one comparison.</para></summary>
public sealed record ReplicaFactionCooldown(int Faction, int Trader, int Orbital, int Military);

/// <summary>One row of the colony's history-event table: the tick/goodwill pairs for one def, optionally per faction.
///
/// <para>`Faction` is -1 for the colonist table and a scribed faction `loadID` for the per-faction table. The two lists
/// are comma-joined because they are parallel arrays of the same length - the reader sums `Goodwills` over `Ticks`
/// (`Assembly-CSharp.decompiled.cs:644133-644138`) and a shape that let them disagree would produce a wrong sign on a
/// line the player reads.</para>
///
/// <para>An <b>empty</b> `Ticks` is a real state, not an absence: `CheckRemoveOldEvents` (`:362755`) empties a row, and
/// the replica has to learn that or a def that stopped being recent keeps printing. This is the same rule `RoofSync`
/// applies to a roof that was removed.</para></summary>
public sealed record ReplicaHistoryEvent(string Def, int Faction, string Ticks, string Goodwills);

/// <summary>One map's identity and grid size, or a tombstone when `Width`/`Height` are zero.
///
/// <para><b>Why the id and not the list position.</b> `Map.uniqueID` (`:76213`) is scribed (`Map.ExposeData` `:76818`)
/// and assigned once from `GetNextMapID` (`:85222`), so two peers that loaded the same save agree on it. `Map.Index`
/// is `Find.Maps.IndexOf(this)` (`:76452`) - a list position that shifts when any earlier map is removed, which is the
/// same defect rounds 67 and 68 removed from the designator, plan, zone and hash channels.</para>
///
/// <para><b>Why the size travels even though it never changes.</b> A map's size is written once, but a replica that
/// builds a map needs it, and defaulting to something is the one mistake that makes every cell index in every other
/// channel wrong at once. Two integers are cheaper than a rule about who knows the size.</para>
///
/// <para><b>Why `Parent` travels but is not used to rebuild the parent.</b> It identifies which world object a pocket
/// map belongs to, which is what a probe needs to say whether a missing map is a missing world object. Rebuilding the
/// parent here would duplicate an object `WorldObjectSync` already carries.</para>
///
/// <para><b>Why the generator def travels.</b> `MapGenerator.GenerateMap` writes `map.generatorDef = mapGenerator`
/// (`:85238`) and four readers dereference it without a null check: `FloodUnfogAdjacent` (`:68785`, reads
/// `ignoreAreaRevealedLetter`), the under-grid default (`:72954`, reads `defaultUnderGridTerrain`), the undropped-item
/// decay rate (`:91489`, reads `isUnderground`) and pocket-map temperature (`:93120`). A map built without it throws a
/// `NullReferenceException` the first time anything is revealed on it - which is the failure the destroy probe hit on
/// its own log line, and the reason this field is on the wire rather than defaulted.</para></summary>
public sealed record ReplicaMap(int Id, int Width, int Height, int Parent, string Generator = "");

/// <summary>Union of two event channels that must not lose entries when one frame overwrites another.
///
/// <para><b>Why this lives in Networking and not with the caller.</b> The authority marks a letter as sent the first
/// time it appears in the stack, so a frame that is overwritten before it is applied takes that letter with it and it
/// is never sent again. A dropped raid letter is not a cosmetic loss: the player never learns the raid happened. The
/// rule has to be identical for every event channel (letters, messages, and the object/zone channels that predate
/// them), and it has to be testable without a RimWorld install - hence a pure function here.</para>
public static class EventUnion
{
    /// <summary>Newest wins per id, then everything only the older frame had is appended. Order is irrelevant to the
    /// receiver, which applies each entry once.</summary>
    public static List<T>? ById<T>(IReadOnlyList<T>? newer, IReadOnlyList<T>? older, Func<T, int> id)
    {
        if (newer is not { Count: > 0 } && older is not { Count: > 0 }) return null;
        var result = newer is { Count: > 0 } ? new List<T>(newer) : [];
        var have = new HashSet<int>();
        foreach (var item in result) have.Add(id(item));
        if (older is { Count: > 0 })
            foreach (var item in older)
                if (have.Add(id(item))) result.Add(item);
        return result;
    }

    /// <summary>Same rule keyed by a string identity, for a record that has no id of its own.</summary>
    public static List<T>? ByKey<T>(IReadOnlyList<T>? newer, IReadOnlyList<T>? older, Func<T, string> key)
    {
        if (newer is not { Count: > 0 } && older is not { Count: > 0 }) return null;
        var result = newer is { Count: > 0 } ? new List<T>(newer) : [];
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in result) have.Add(key(item));
        if (older is { Count: > 0 })
            foreach (var item in older)
                if (have.Add(key(item))) result.Add(item);
        return result;
    }
}

/// <summary>The weather a map is running under, as the host decided it.
///
/// The definition alone is not enough: <c>TransitionLerpFactor</c> is <c>curWeatherAge / 4000</c> and that counter is
/// advanced inside a tick, which a replica never runs - so with only the definition, a replica would blend forever
/// from the weather it left. <paramref name="Previous"/> and <paramref name="Age"/> come along so the replica's rain,
/// snow and wind are the host's, not a local re-invention.</summary>
public sealed record ReplicaWeather(int Map, string Def, string? Previous = null, int Age = 0);

/// <summary>One zone as the host holds it: tamper-proof enough for a replica to rebuild the object and the overlay.
///
/// <para><b>Why zones need their own channel at all.</b> A zone is not a <c>Thing</c>: it lives in
/// <c>Map.zoneManager.AllZones</c>, has no cell in the thing grid, and is therefore invisible to the frame's
/// object scan. Measured on the player's own colony: they had just drawn a stockpile and a growing zone, the info
/// card showed the right names and sizes (156 / 169 cells, read from the local <c>zone.cells</c> that came in the
/// snapshot), and the ground showed no colour at all - because a zone created after the snapshot never reached the
/// replica, so there was nothing for <c>SectionLayer_Zones</c> to draw.</para>
///
/// <para>The shape follows ColonyTogether's <c>ZoneState</c>
/// (<c>docs/mp-colonytogether/_decomp/ColonyTogether.Prototype.decompiled.cs:28041</c> <c>ProjectZone</c>): identity,
/// a kind string, the label, the cells, and the per-kind settings that change behaviour. <paramref name="Cells"/> is
/// a flat <c>x,z;x,z</c> list rather than a list of objects: a 169-cell zone is ~700 bytes this way and zones change
/// only when the player drags one.</para>
///
/// <para><paramref name="Revision"/> is a digest of everything the replica would rebuild, so an unchanged zone costs
/// nothing per frame and a settings-only change still re-sends.</para></summary>
public sealed record ReplicaZone(int Id, int Map, string Kind, string Label, string Cells, long Revision,
    // Stockpile-only: `StorageSettings.Priority` (0..4 = Lowest..Critical) and the allowed def names.
    int Priority = -1, string? Allowed = null,
    // Growing-only: what to sow, and whether the player allowed sowing/harvesting.
    string? Plant = null, bool AllowSow = false, bool AllowCut = false,
    // Fishing-only, for the same reason: `ZoneSync` only ever created stockpiles and growing zones, so a fishing zone
    // never existed on a replica at all. `RepeatCount` is one of seven scribed settings (`Zone_Fishing.ExposeData`
    // `:432048-432058`) and the one that decides whether the zone is still allowed to fish
    // (`CanFishNow` `:431736`: `repeatCount > 0`), so a replica's zone reads "finished" or "still working" arbitrarily.
    int RepeatCount = -1,
    // Fishing-only, round 145: the five settings that actually gate the work giver, as
    // `repeatMode;targetCount;unpauseAtCount;pauseWhenSatisfied;targetPopulationPct`.
    //
    // **The earlier comment here was wrong.** It said `repeatCount` was "the only one of the seven that gates whether
    // the zone still works"; the work giver branches on `Zone_Fishing.ShouldFishNow` (`:312825`, `:312835`), which is
    // `Allowed && OverTargetPopulation && !PausedDueToResourceCount && UnderTargetResourceCount`. The pause in the middle
    // is computed by `RecheckPausedDueToResourceCount` (`:431891-431903`) from `repeatMode`, `pauseWhenSatisfied`,
    // `targetCount` and `unpauseAtCount`, and the population test reads `targetPopulationPct` (`:431905-431913`). All
    // five are set from the zone's own tab (`ITab_Fishing`, `:665021` reset, `:665042` stepper), i.e. a player click -
    // so a replica that only carried `repeatCount` showed the settings while the authority fished on its own numbers.
    string? Fishing = null);

/// <summary>A zone the host removed. Sent separately from things because a zone id is not a thing id and the
/// replica must deregister it rather than destroy an object.</summary>
public sealed record ReplicaZoneRemoval(int Id, int Map);

/// <summary>A pawn's full Scribe state. Declared and never sent - deliberately, and the reason is a measurement.
///
/// <para><b>What it would carry.</b> Everything: `Pawn.ExposeData` writes needs, hediffs, equipment, abilities, work
/// priorities, relations and any third-party Comp without per-field handling. That is why this record exists and why it
/// is tempting.</para>
///
/// <para><b>Why it is not used.</b> The `pawn-payload-size` command measures one map's pawns at <b>636386 B total, 8963 B
/// each, 19318 B for the largest</b> - <b>224 times</b> the per-tick position frame (2840 B for the same 71 pawns). A whole
/// fragment can only travel when something inside it changes, and deciding that per pawn needs a fingerprint of the
/// fragment, which is the same cost as producing it. The fields that actually go stale are carried instead by the named
/// channels that send only the numbers that move - hediff timers, ability charges, pawn trackers, needs, ages, breakers,
/// immunity - each of which exists because it owns one of those numbers.</para>
///
/// <para>So a channel that is declared but unwired is not automatically a gap. This one is the case where the honest
/// answer is "measured, and the factor is 224".</para></summary>
public sealed record ReplicaPawnState(int Id, string State);

/// <summary>One pawn's conditions, as the authority has them.
///
/// <para>A hediff is not a number that can be copied: <c>HediffMaker.MakeHediff</c> needs the def, the body part and,
/// for injuries, the amount that went into it. Rather than re-deriving all of that, each entry is carried as
/// <c>defName:partDefName:severity</c> and rebuilt on the replica through the game's own factory.</para>
///
/// <para><see cref="Version"/> exists because an empty list and "not sent yet" are different facts. Without it a peer
/// that legitimately cured everything would look identical to one that has never heard about this pawn, and the
/// replica would keep showing the old conditions forever.</para></summary>
public sealed record ReplicaHediffSet(int Pawn, int Version, IReadOnlyList<string> Entries)
{
    /// <summary>An explicit "this pawn has nothing" - a real fact, distinct from the channel being absent.
    ///
    /// <para>Named instead of a bare constructor call so the intent is readable at the call site: a peer that cured
    /// its last condition must send this, or the replica keeps drawing the scar.</para></summary>
    public static ReplicaHediffSet Empty(int pawn, int version) => new(pawn, version, []);
}

/// <summary>An object the host created after the replica loaded its snapshot: a finished wall, a dropped
/// item, a tamed animal. The replica cannot invent it, so the definition, position and full Scribe state
/// travel together.</summary>
/// <remarks><see cref="Contents"/> exists for containers whose contents are saved as cross references: a
/// corpse stores `Thing_Human37571` and nothing else, so a replica that never saw that pawn cannot rebuild the
/// corpse at all (`Corpse.Bugged` stays true and `SpawnSetup` refuses it). Those pawns are deliberately never
/// sent as their own creations - they are not on the map - so they ride along with the container instead.</remarks>
public sealed record ReplicaCreation(int Id, int Map, string Def, int X, int Z, int Rotation, int Stack,
    string? State = null, IReadOnlyList<ReplicaCreation>? Contents = null,
    // Same reason as `ReplicaThing.Forbidden`: a creation must arrive with the marker the host draws, because a
    // peer that never received the forbid command has no other way to learn it.
    bool Forbidden = false);
public sealed record ServerOptions(string Address = "127.0.0.1", int Port = 30572, string DataDirectory = "Saved",
    string GameVersion = "1.7", string ModHash = "development", bool EveryoneCanEdit = true, int MaxHistory = 200_000,
    string? AdminPassword = null)
{
    /// <summary>Keep the lockstep timeline behind the resident process simulation's reported progress.</summary>
    public bool RequireSimulation { get; init; } = false;

    /// <summary>The resident is the only simulator; joining clients are display replicas.</summary>
    public bool Authoritative { get; init; } = false;

    /// <summary>Every peer simulates and the timeline is gated by the slowest one. The server only orders
    /// commands and paces ticks; it never owns the colony state.</summary>
    public bool Lockstep { get; init; } = false;

    /// <summary>Which save the resident process is starting this room into, if it knows. Persisted with the room
    /// so a later launch can tell "the world in here is the save you asked for" from "the world in here is the one
    /// you had before you asked". Without it, a restored room reports <c>GameRunning</c> and the load funnel stands
    /// down, which is how a swap request for one save ends up running another.</summary>
    public string? StartupSave { get; init; }

    /// <summary>Hold the timeline while no display replica is attached. The resident counts as a peer but not as
    /// an audience, so without this a room left running overnight simulates a world nobody is watching: it burns a
    /// core, and the colony comes back several in-game days older than the session that left it. The operator's
    /// chosen speed is kept as-is and resumes the moment the first replica reports its world loaded. Off by default
    /// because tests and pure ordering hosts drive ticks with no replica attached and expect the clock to move.</summary>
    public bool PauseWithoutReplicas { get; init; }

    // Options are routinely printed by hosts; never include the credential in the generated record text.
    public override string ToString() => $"ServerOptions {{ Address = {Address}, Port = {Port}, AdminEnabled = {!string.IsNullOrEmpty(AdminPassword)} }}";
}
public sealed record ServerAdminStatus(bool GameRunning, int Tick, int PlayerCount, string? GameVersion, string? ModHash);
public sealed record ServerResetResult(string BackupId);
public sealed record SnapshotManifest(string Id, long Length, int ChunkSize, IReadOnlyList<string> ChunkHashes);
public sealed record SnapshotChunk(string Id, int Index, byte[] Data, string Sha256);

/// <summary>One deterministic gameplay command. Sequence and tick are assigned by the server.</summary>
public sealed record GameCommand(long Sequence, int Tick, string Kind, byte[] Payload, long OriginPeer = 0);

/// <summary>Builds the same manifest the server would compute, so a client can verify before upload.</summary>
public static class SnapshotBuilder
{
    public static SnapshotManifest Describe(ReadOnlySpan<byte> data)
    {
        var hashes = new List<string>();
        using var whole = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        whole.AppendData(data);
        for (var offset = 0; offset < data.Length; offset += NetworkProtocol.SnapshotChunkSize)
        {
            var length = Math.Min(NetworkProtocol.SnapshotChunkSize, data.Length - offset);
            hashes.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data.Slice(offset, length))));
        }
        return new SnapshotManifest(Convert.ToHexStringLower(whole.GetHashAndReset()), data.Length,
            NetworkProtocol.SnapshotChunkSize, hashes);
    }
}

/// <summary>A structured server or transport error suitable for displaying to a player.</summary>
public sealed class NetworkException : IOException
{
    public string Code { get; }
    public NetworkException(string code, string message) : base(message) => Code = code;
    public NetworkException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;
}

internal static partial class Validation
{
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdPattern();

    internal static void ContentId(string id)
    {
        if (id is null || !ContentIdPattern().IsMatch(id))
            throw new ArgumentException("Snapshot id must be exactly 64 lowercase hexadecimal characters.", nameof(id));
    }

    internal static bool IsFullGameVersion(string? version) => version is not null &&
        System.Version.TryParse(version, out var parsed) && parsed.Major == 1 && parsed.Minor == 7 && parsed.Build >= 0;

    internal static void Hello(ClientHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (string.IsNullOrWhiteSpace(hello.PlayerName) || hello.PlayerName.Length > 80 || hello.PlayerName.Any(char.IsControl))
            throw new NetworkException("InvalidPlayerName", "Player name must contain 1 to 80 printable characters.");
        if (!IsFullGameVersion(hello.GameVersion))
            throw new NetworkException("GameVersionMismatch", "A complete RimWorld 1.7.x game version is required.");
        if (string.IsNullOrWhiteSpace(hello.ModHash) || hello.ModHash.Length > 512 || hello.ModHash.Any(char.IsControl))
            throw new NetworkException("InvalidModHash", "A non-empty simulation mod/configuration hash is required.");
        if (!string.Equals(hello.ClientVersion, NetworkProtocol.ClientVersion, StringComparison.Ordinal))
            throw new NetworkException("ClientVersionMismatch", $"Client version must be exactly {NetworkProtocol.ClientVersion}.");
    }

    internal static void Manifest(SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ContentId(manifest.Id);
        if (manifest.Length < 0 || manifest.ChunkSize != NetworkProtocol.SnapshotChunkSize || manifest.ChunkHashes is null)
            throw new InvalidDataException("Invalid snapshot length, chunk size or chunk hashes.");
        var expected = manifest.Length / manifest.ChunkSize + (manifest.Length % manifest.ChunkSize == 0 ? 0 : 1);
        if (expected != manifest.ChunkHashes.Count)
            throw new InvalidDataException("Snapshot chunk count does not match its length.");
        foreach (var hash in manifest.ChunkHashes)
            ContentId(hash);
    }

    internal static int ChunkLength(SnapshotManifest manifest, int index)
    {
        if (index < 0 || index >= manifest.ChunkHashes.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Snapshot chunk index is out of bounds.");
        return (int)Math.Min(manifest.ChunkSize, manifest.Length - (long)index * manifest.ChunkSize);
    }
}








/// <summary>One settlement, in the form the wire carries it.
///
/// <para>Records rather than the game's own classes: `Networking` cannot reference `Assembly-CSharp`, and the wire form must
/// be stable even if the save form gains fields. The projection is field for field with <c>SettlementSite</c>, and
/// <c>SettlementSync</c> is the only place that converts.</para></summary>
public sealed record SettlementSiteWire(int Id, int Map, int Owner, string Name,
    int MinX, int MinZ, int MaxX, int MaxZ, bool Retired, int FoundedTick);

/// <summary>The whole settlement table.
///
/// <para><b>Why whole and not a delta.</b> The table is small and bounded - a handful of settlements, one membership entry
/// per player, a point grant per edited pair. A delta protocol would need per-record identities, tombstones and a resync
/// path: three ways for a replica's grants to differ from the authority's, which is the one outcome that must not happen
/// because the two sides would then disagree about whether an action is legal.</para>
///
/// <para><b>Why the collections are flat int lists.</b> A pair is two entries, a grant is three; flattening keeps the wire
/// form to one list shape per concept, so a decoder cannot get the field names of a nested object wrong. The lists are
/// always even (or a multiple of three) by construction - `SettlementSync.Publish` is the only writer.</para>
/// </summary>
public sealed record SettlementWire(long Version,
    IReadOnlyList<SettlementSiteWire> Sites,
    IReadOnlyList<int> Members,      // playerId, settlementId, ...
    IReadOnlyList<int> PawnHomes,    // pawnId, settlementId, ...
    IReadOnlyList<int> Grants,       // from, to, flags, ...
    IReadOnlyList<int> Defaults,     // settlementId, flags, ...
    // War, alliance and the declaration ban, as `a, b, warSince, allied ? 1 : 0, noticeUntil` quints.
    //
    // **Added in round 80, and it was the last part of this table with no channel.** `SettlementData.Pacts` is scribed
    // (`:239`) so it survives a save, and `SettlementWar.AtWar` is what the gates consult before every grant - but
    // `SettlementWire` never carried it, so a replica kept whatever pact list the *save* had. Declaring war on the
    // authority therefore left the replica granting a belligerent's pawns the visitor four: an enemy could build a wall
    // in your base on their screen and be refused on yours, which is the one outcome a permission layer must not have.
    //
    // **`NoticeUntil` joined the quad in round 94**, when the countdown moved off the declaration edge and onto leaving
    // an alliance (matching ColonyTogether, `ct045:117294`). It has to travel for the same reason `warSince` does: it is
    // an absolute tick that decides whether a declaration is refused, so a replica without it would let a player declare
    // a war the authority will reject — a disagreement in the *permissive* direction, which is the one that matters.
    //
    // Flat ints rather than a record: the wire is deliberately made of `IReadOnlyList<int>` (see `SettlementWire`'s
    // own remarks on why a plain int list beats a nested record here), and `allied` is a bool encoded as 0/1 the same
    // way every other flag on this wire is.
    IReadOnlyList<int> Pacts,
    // Cells whose owner is not what the rectangles say, as `mapId, x, z, owner` quads.
    //
    // **The same defect as `Pacts` one field over, found by looking for the same shape.** `Overrides` is scribed
    // (`SettlementData.ExposeData`, `:214-229`) and consulted *before* the geometry in `SettlementAt` (`:170-175`), so
    // it can only ever narrow the rectangles - which makes it the one part of the table whose disagreement is silent in
    // the safe-looking direction: the authority answers 0 for a cell it handed back to nobody, the replica answers
    // whatever rectangle contains it, and a player may build a wall on ground the owner kept.
    //
    // Quads rather than a record because this wire is made of int lists throughout, and owner 0 is a valid value only
    // through `Set`'s removal path - `Load` filters it, so a wire carrying it would be inventing a row.
    IReadOnlyList<int> KeptCells);
