using HarmonyLib;
using TurboTick.Networking;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>稳定身份 token 的本地副本：`&lt;profile&gt;\Config\TurboTick\player-token.txt`。
///
/// 服务端第一次见到这个 profile 时下发 32 字节随机 token（见 <see cref="SessionTokens"/>），客户端写在这里；
/// 之后每次握手带回去，服务端据此认出「还是那个人」。`DedicatedClient.PlayerId` 是服务端**会话内**分配的
/// long，重连就变，认不出人——这正是需要 token 的原因。
///
/// 不用 Steam ID：headless 客户端不一定有 Steam 会话。</summary>
internal static class SessionIdentity
{
    private static string? cachedPath;

    /// <summary>路径必须在游戏线程上算（`GenFilePaths` 是游戏侧的东西）。</summary>
    internal static string FilePath =>
        cachedPath ??= Path.Combine(GenFilePaths.ConfigFolderPath, "TurboTick", "player-token.txt");

    /// <summary>读本地 token；没有、坏了、读不动都返回 null（= 新玩家，服务端会发一个新的）。</summary>
    internal static string? Read()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            if (SessionTokens.IsValid(text)) return text;
            // 手改坏了也不能崩：当作没有，服务端会发新的回来。
            Log.Warning($"[TurboTick] player-token.txt 不是合法 token（长度 {text.Length}），当作没有：{path}");
            return null;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 读 player-token.txt 失败，当作没有：" + error.Message);
            return null;
        }
    }

    /// <summary>首次写入；内容和服务端下发的不同也写——那说明服务端没认出我们，继续带旧 token
    /// 只会每次都被当成新玩家（也就永远恢复不了上一次的视图）。返回是否真的写了盘。</summary>
    internal static bool Adopt(string? token)
    {
        if (!SessionTokens.IsValid(token)) return false;
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var existing = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            if (string.Equals(existing, token, StringComparison.Ordinal)) return false;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, token);
            File.Move(temporary, path, overwrite: true);
            Log.Message(existing is null
                ? $"[TurboTick] 已在服务端登记身份：token={SessionTokens.Short(token)} -> {path}"
                : $"[TurboTick] 服务端没认出本地 token（{SessionTokens.Short(existing)}），改用新 token={SessionTokens.Short(token)} -> {path}");
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 写 player-token.txt 失败：" + error.Message);
            return false;
        }
    }
}

/// <summary>会话视图：他在哪张地图、选中了谁、相机在哪。
///
/// **只恢复视图，不做权限**：所有玩家仍然可以指挥任何小人（用户已明确）。这里也不碰任何模拟状态——
/// 相机和选中只在游戏线程上读写，网络线程只投递一条消息（经 `RoomConnection.Post`）。
///
/// 上报是**防抖**的：地图切换 / 选中变化 / 相机停稳 500 ms 之后各报一次，退出前再报一次。
/// 每帧发会把上行带宽浪费在「相机正被拖动的每一毫秒」上，而服务端本来就只留最新一条。</summary>
internal static class SessionViewSync
{
    private const int DebounceMilliseconds = 500;

    private static SessionViewSnapshot? pendingRestore;
    private static SessionViewSnapshot? lastObserved;
    private static SessionViewSnapshot? lastReported;
    private static long changedAt;
    private static long lastReportAt;
    private static long playingSince;
    private static int reports;
    private static int restores;
    private static int skippedSelections;
    private static string? reportFailure;
    private static bool cameraWarned;
    private static bool probeRan;
    /// <summary>待补写的缩放。切地图之后 <c>CameraDriver</c> 会在随后的帧里把 size 重置回该图的默认值，
    /// 所以只写一次会在 1 秒内被打回去（实测：写入 13.50 → 1 秒后读回 24.00）。</summary>
    private static float pendingSize;
    private static long pendingSizeUntil;

    /// <summary>进服之后先等一小会儿再上报：服务端的恢复消息在本端 WorldReady 之后才发出，
    /// 抢先上报会把「默认视图」写进服务端，然后才有恢复——那一小段时间里断线，记录就是错的。</summary>
    private const int RestoreGraceMilliseconds = 2000;

    /// <summary>供 HUD/日志用的单行读数。不参与判定。</summary>
    internal static string Describe() =>
        $"视图上报 {reports} 次 / 恢复 {restores} 次 / 跳过选中 {skippedSelections} 个"
        + (pendingRestore is null ? "" : " / 待恢复 1")
        + (reportFailure is null ? "" : " / 上报失败：" + reportFailure);

    /// <summary>换房间、断开时清账：上一个房间的相机位置不能当成这个房间的起始状态。</summary>
    internal static void Reset()
    {
        pendingRestore = null;
        lastObserved = null;
        lastReported = null;
        changedAt = 0;
        lastReportAt = 0;
        playingSince = 0;
        reports = 0;
        restores = 0;
        skippedSelections = 0;
        reportFailure = null;
        pendingSize = 0f;
        pendingSizeUntil = 0;
    }

    /// <summary>服务端下发的恢复目标。由 `RoomConnection` 在游戏线程上调用。</summary>
    internal static void OnRestore(SessionViewSnapshot view) => pendingRestore = view;

    /// <summary>游戏线程每帧一次（`RoomConnection.Pump`）。</summary>
    internal static void Update()
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null) return;
        // 权威端自己不需要「恢复自己的视图」：headless 服务器没有相机，也没有人看。
        if (HeadlessRuntime.ServerMode) return;
        var client = RoomConnection.Client;
        if (client?.IsConnected != true) return;
        if (LongEventHandler.AnyEventNowOrWaiting || Scribe.mode != LoadSaveMode.Inactive) return;

        var now = Environment.TickCount64;
        // 只在这个房间的世界真的载入完之后才上报：载入期间 Find.CurrentMap 还是本地那张临时地图。
        if (SharedColony.Session.Phase != Simulation.SharedPhase.Playing) return;
        if (playingSince == 0) playingSince = now;
        // 1) 服务端要求的恢复先执行：它必须发生在「用户自己动之前」，否则会被下一拍上报覆盖掉。
        if (pendingRestore is { } restore)
        {
            pendingRestore = null;
            Apply(restore, out var applied);
            if (applied is not null)
            {
                // 立刻回写一次：否则下一次防抖上报之前退出，文件里可能还留着「默认视图」。
                Remember(applied);
                Report(applied, "恢复后回写");
            }
        }
        // 2) 观察当前视图，变化即重置防抖窗口。
        if (pendingSizeUntil > 0)
        {
            if (now <= pendingSizeUntil) Find.CameraDriver?.SetRootSize(pendingSize);
            else
            {
                pendingSizeUntil = 0;
                // 补写窗口结束时把「实际留下多少」写下来：这是唯一能证明尺寸到底还原没有的读数。
                Log.Message($"[TurboTick] 缩放还原结束：目标 {pendingSize:0.00}，实际 {Find.CameraDriver?.RootSize ?? 0f:0.00}");
            }
        }
        var view = Capture();
        if (view is null) return;
        if (lastObserved is null || !Same(lastObserved, view))
        {
            lastObserved = view;
            changedAt = now;
        }
        // 3) 停稳 500 ms 且和上一次上报不同 → 报一次。
        if (now - changedAt < DebounceMilliseconds) return;
        if (lastReported is not null && Same(lastReported, view)) return;
        if (now - lastReportAt < DebounceMilliseconds) return;
        // 恢复窗口：服务端有记录的话，恢复消息就在这之后就绪。没记录的人等满窗口再报，只多 2 秒。
        if (restores == 0 && now - playingSince < RestoreGraceMilliseconds) return;
        Remember(view);
        Report(view, "防抖");
        Probe(view);
    }

    /// <summary>退出/断开前最后上报一次。用有限的等待收尾：socket 写只有几百字节，
    /// 但连接可能已经半死，250 ms 之后就不再等——离开不能卡在游戏线程上。</summary>
    internal static void ReportBeforeLeave()
    {
        if (HeadlessRuntime.ServerMode) return;
        var view = Capture();
        if (view is null) return;
        var task = Report(view, "退出前");
        if (task is null) return;
        try { task.Wait(250); }
        catch (Exception error) { Log.Warning("[TurboTick] 退出前上报视图失败：" + error.Message); }
    }

    /// <summary>当前视图的不可变快照。读不到地图时返回 null（还没进游戏）。</summary>
    internal static SessionViewSnapshot? Capture()
    {
        var map = Find.CurrentMap;
        if (map is null) return null;
        var selected = new List<int>(SessionViewSnapshot.MaximumSelected);
        var selector = Find.Selector;
        if (selector is not null)
        {
            // 选中列表在小人死后可能仍然含已销毁对象：取 thingIDNumber 之前必须判 Destroyed。
            foreach (var pawn in selector.SelectedPawns)
            {
                if (pawn is null || pawn.Destroyed) continue;
                selected.Add(pawn.thingIDNumber);
                if (selected.Count >= SessionViewSnapshot.MaximumSelected) break;
            }
        }
        TryReadCamera(out var x, out var z, out var size);
        return SessionViewSnapshot.Sanitize(new SessionViewSnapshot(map.uniqueID, selected, x, z, size));
    }

    /// <summary>把服务端下发的记录执行到界面上。找不到的东西一律**静默跳过**：
    /// 小人可能已经死了、被带走了，报错中断只会让重连失败。</summary>
    internal static void Apply(SessionViewSnapshot view, out SessionViewSnapshot? applied)
    {
        applied = null;
        restores++;
        if (view.MapId < 0)
        {
            Log.Message("[TurboTick] 视图恢复：记录里没有地图，保持当前视图");
            return;
        }
        Map? map = null;
        foreach (var candidate in Find.Maps)
            if (candidate.uniqueID == view.MapId) { map = candidate; break; }
        if (map is null)
        {
            Log.Message($"[TurboTick] 视图恢复：地图 uniqueID={view.MapId} 不在这份存档里（可能已被带走/销毁），跳过切图");
        }
        else
        {
            var hasCamera = view.Size > 0.01f && (view.X > 0.01f || view.Z > 0.01f);
            var cell = new IntVec3((int)view.X, 0, (int)view.Z);
            // 没有相机数据（无界面客户端上报的 0）时只切地图，不把镜头塞到地图角落。
            if (!hasCamera || !cell.InBounds(map)) cell = map.Center;
            try
            {
                CameraJumper.TryJump(cell, map);
                // 缩放单独还原：TryJump 只保证「跳到这个格子」，尺寸还是本地上一局的。
                // 用 SetRootSize（游戏自己在缩放键上用的那个入口），不用 SetRootPosAndSize——
                // 后者在 1.7 里是读档路径用的，实测写入不生效。
                // 而且**要在接下来的两秒里反复写**：切图之后 CameraDriver 会把 size 打回该图默认值。
                if (hasCamera)
                {
                    Find.CameraDriver?.SetRootSize(view.Size);
                    pendingSize = view.Size;
                    pendingSizeUntil = Environment.TickCount64 + 2000;
                }
            }
            catch (Exception error)
            {
                Log.Warning("[TurboTick] 视图恢复：相机定位失败（只恢复选中）：" + error.Message);
            }
        }
        var restored = Select(view.Selected);
        applied = Capture();
        Log.Message($"[TurboTick] 视图恢复：目标 mapId={view.MapId} 选中=[{string.Join(",", view.Selected)}]"
            + $" 相机=({view.X:0.00},{view.Z:0.00}) size={view.Size:0.00} -> 实际 mapId={applied?.MapId}"
            + $" 选中=[{string.Join(",", applied?.Selected ?? Array.Empty<int>())}]"
            + $" 相机=({applied?.X ?? 0:0.00},{applied?.Z ?? 0:0.00}) size={applied?.Size ?? 0:0.00}"
            + $"（找到 {restored} 个，跳过 {view.Selected.Count - restored} 个）");
    }

    /// <summary>按 thingIDNumber 逐个选中。返回找到的数量。</summary>
    private static int Select(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return 0;
        var selector = Find.Selector;
        if (selector is null) return 0;
        // 一次遍历建索引：64 个 id × 每张地图所有角色 的朴素写法在 64 殖民者 + 两张图上要几万次比较。
        var index = new Dictionary<int, Pawn>(ids.Count);
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (ids.Contains(pawn.thingIDNumber) && !pawn.Destroyed) index[pawn.thingIDNumber] = pawn;
        selector.ClearSelection();
        var found = 0;
        foreach (var id in ids)
        {
            if (!index.TryGetValue(id, out var pawn)) { skippedSelections++; continue; }
            try
            {
                // 与用户在一个小人上点一下等价：不播选择音（headless 没有声音设备），设计器照常取消。
                selector.Select(pawn, playSound: false);
                found++;
            }
            catch (Exception error)
            {
                skippedSelections++;
                Log.Warning($"[TurboTick] 视图恢复：选中 id={id} 失败，跳过：" + error.Message);
            }
        }
        return found;
    }

    private static void Remember(SessionViewSnapshot view) => lastReported = view;

    private static Task? Report(SessionViewSnapshot view, string reason)
    {
        var client = RoomConnection.Client;
        if (client?.IsConnected != true) return null;
        lastReportAt = Environment.TickCount64;
        reports++;
        var task = client.SendViewStateAsync(view);
        task.ContinueWith(completed =>
        {
            var error = completed.Exception?.GetBaseException();
            reportFailure = error?.Message;
            if (error is not null) Log.Warning("[TurboTick] 视图上报失败：" + error.Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    private static bool Same(SessionViewSnapshot left, SessionViewSnapshot right) =>
        left.MapId == right.MapId
        && left.Selected.SequenceEqual(right.Selected)
        && Math.Abs(left.X - right.X) < 0.01f
        && Math.Abs(left.Z - right.Z) < 0.01f
        && Math.Abs(left.Size - right.Size) < 0.01f;

    private static AccessTools.FieldRef<CameraDriver, Vector3>? rootPos;
    private static bool rootPosTried;

    /// <summary>读相机。<c>CameraDriver.rootPos</c> 在 1.7 是**私有字段**（没有公开 RootPos 属性），
    /// 所以先走 Harmony 的字段引用；拿不到就退化成 <c>MapPosition</c> 的整格值。</summary>
    private static void TryReadCamera(out float x, out float z, out float size)
    {
        x = 0f;
        z = 0f;
        size = 0f;
        var driver = Find.CameraDriver;
        if (driver is null)
        {
            if (!cameraWarned) { cameraWarned = true; Log.Message("[TurboTick] 没有 CameraDriver（无界面模式）：相机项按 0 上报"); }
            return;
        }
        try
        {
            size = driver.RootSize;
            if (!rootPosTried)
            {
                rootPosTried = true;
                rootPos = AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");
            }
            if (rootPos is not null)
            {
                var position = rootPos(driver);
                x = position.x;
                z = position.z;
                return;
            }
        }
        catch (Exception error)
        {
            if (!cameraWarned) { cameraWarned = true; Log.Warning("[TurboTick] 读 CameraDriver.rootPos 失败，改用 MapPosition：" + error.Message); }
        }
        var cell = driver.MapPosition;
        x = cell.x;
        z = cell.z;
    }

    /// <summary>实验台探针：`TURBOTICK_VIEW_PROBE=select` 在第一次可操作时选中一个小人并把相机挪到
    /// 一个可辨认的位置；`=verify` 在重连后只记录观测量，不改任何东西。不加这个变量时完全不生效。</summary>
    private static void Probe(SessionViewSnapshot view)
    {
        var mode = Environment.GetEnvironmentVariable("TURBOTICK_VIEW_PROBE")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode) || probeRan) return;
        if (mode == "verify")
        {
            probeRan = true;
            Log.Message($"[TurboTick] 视图探针 verify：地图={view.MapId} 选中=[{string.Join(",", view.Selected)}]"
                + $" 相机=({view.X:0.00},{view.Z:0.00}) size={view.Size:0.00} 恢复次数={restores}");
            return;
        }
        if (mode != "select") return;
        var map = Find.CurrentMap;
        if (map is null) return;
        var target = map.mapPawns.FreeColonistsSpawned.OrderBy(pawn => pawn.thingIDNumber).LastOrDefault();
        if (target is null) return;
        probeRan = true;
        var cell = target.PositionHeld;
        var x = cell.x + 9.5f;
        var z = cell.z + 7.25f;
        try
        {
            var selector = Find.Selector;
            var driver = Find.CameraDriver;
            Log.Message($"[TurboTick] 视图探针 env：selector={(selector is null ? "null" : "ok")}"
                + $" driver={(driver is null ? "null" : "ok")} state={Current.ProgramState} map={map.uniqueID}"
                + $" 选前 NumSelected={selector?.NumSelected} RootSize={driver?.RootSize:0.00}");
            // 缩放不能随便挑：CameraMapConfig.sizeRange 之外的值会被钳住，那会让「尺寸也还原了」变成假象。
            var size = 13.5f;
            if (driver?.config is { } config)
            {
                size = config.sizeRange.ClampToRange(size);
                if (Math.Abs(size - driver.RootSize) < 0.05f) size = config.sizeRange.ClampToRange(driver.RootSize * 1.6f);
            }
            selector?.ClearSelection();
            selector?.Select(target, playSound: false);
            // 用游戏自己的两个入口：跳格（缩放键/双击小人走的就是它）与改缩放。
            // SetRootPosAndSize 在 1.7 里实测写进去不生效，别用它做「恢复相机」。
            driver?.JumpToCurrentMapLoc(new IntVec3((int)x, 0, (int)z));
            driver?.SetRootSize(size);
            TryReadCamera(out var nx, out var nz, out var nsize);
            Log.Message($"[TurboTick] 视图探针 select：选中 id={target.thingIDNumber} '{target.LabelShort}'"
                + $" 相机=({x:0.00},{z:0.00}) size={size:0.00} 地图={map.uniqueID}"
                + $" -> 立即读回 选中数={selector?.NumSelected} 相机=({nx:0.00},{nz:0.00}) size={nsize:0.00}");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 视图探针 select 失败：" + error.Message);
        }
    }
}
