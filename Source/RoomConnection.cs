using System.Collections.Concurrent;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Own network implementation; socket callbacks only enqueue work for the game thread.</summary>
internal static class RoomConnection
{
    private static readonly ConcurrentQueue<Action> gameActions = new();
    private static readonly List<Task> operations = [];
    private static DedicatedClient? client;
    private static CancellationTokenSource? lifetime;
    private static int generation;
    private static TurboTickSettings? reconnectTarget;
    public static string Status { get; private set; } = "未连接";
    public static string Fingerprint { get; private set; } = "尚未生成";
    public static string SnapshotStatus { get; private set; } = "尚未下载；此阶段只缓存快照，不自动载入游戏";
    public static bool Busy { get; private set; }
    internal static DedicatedClient? Client => client;
    public static bool CanEdit => !TurboState.Authority.SessionRequested
        || (TurboState.Authority.SessionConnected && client?.CanEdit == true);

    /// <summary>Every user-visible status also lands in Player.log so a run can be reviewed afterwards.</summary>
    private static void SetStatus(string text)
    {
        Status = text;
        Log.Message("[TurboTick] " + text);
    }

    internal static int SessionGeneration => generation;
    internal static event Action? SessionChanging;
    public static bool IsHost => !Busy && client?.IsConnected == true && client.IsHost && TurboState.Authority.SessionConnected;
    public static bool HostRequested { get; private set; }
    public static string Endpoint { get; private set; } = "";

    public static void Connect(TurboTickSettings settings, bool asHost = false, bool publishCurrentOnConnect = false,
        bool simulation = false)
    {
        SetStatus("开始连接 " + settings.ServerHost.Trim() + ":" + settings.ServerPort);
        // Must run on the game thread before starting the background hash/connection operation.
        ManifestInputs inputs;
        try { inputs = CompatibilityManifest.Capture(); }
        catch (Exception error) { SetStatus("兼容性指纹失败：" + error.Message); return; }
        var host = settings.ServerHost.Trim();
        if (!int.TryParse(settings.ServerPort, out var port) || port is < 1 or > 65535)
        {
            SetStatus("端口须在 1..65535");
            return;
        }
        var name = settings.PlayerName.Trim();
        if (name.Length is < 1 or > 32 || name.Any(char.IsControl)) { SetStatus(TurboTickSettings.T("TurboTick_InvalidName")); return; }
        reconnectTarget = new TurboTickSettings { ServerHost = host, ServerPort = port.ToString(), PlayerName = name };
        Leave();
        // Automatic publication is an explicit one-connection developer option; manual/reset joins never inherit it.
        publishOnConnect = publishCurrentOnConnect;
        publishedOnce = false;
        HostRequested = asHost;
        Endpoint = (host.Contains(':') ? "[" + host + "]" : host) + ":" + port;
        var id = ++generation;
        var roleConfirmed = !asHost;
        string? connectionFailure = null;
        var pendingWorldEvents = new List<Action>();
        void PostWorld(Action action) => Post(id, () =>
        {
            if (connectionFailure != null) return;
            if (roleConfirmed) action();
            else pendingWorldEvents.Add(action);
        });
        var cancel = lifetime = new CancellationTokenSource();
        var socket = client = new DedicatedClient();
        TurboState.Authority.BeginSession();
        Status = "计算兼容性指纹…（程序集、Def/Patch、模组顺序与配置）";
        Busy = true;
        var exportFile = Path.Combine(GenFilePaths.ConfigFolderPath, "TurboTick", "compatibility-manifest.json");
        socket.PolicyChanged += policy => Post(id, () =>
        {
            TurboState.Authority.ApplyServer(policy);
            NativeSpeedSync.ApplyConfirmed();
            SetStatus("房间已连接 · 服务端设置修订 " + policy.Revision);
        });
        socket.StatusChanged += text => Post(id, () =>
        {
            SetStatus(connectionFailure ?? text);
            if (!socket.IsConnected) TurboState.Authority.ConnectionLost();
        });
        socket.WorldStarted += (session, manifest) => PostWorld(() =>
        {
            Status = "共享殖民地已开始，正在同步存档…";
            AuthoritativeRoom = session.RoomAuthoritative;
            LockstepRoom = session.Lockstep;
            AuthoritativeReplica.OnWorldStarted(session);
            // Reloading on every Started message loops load -> publish checkpoint -> Started forever and
            // stalls the timeline. This applies to a player peer as much as to the resident: the second
            // catch-up the server used to send made the colony reload itself about every ten seconds, which
            // a player sees as being thrown back into the world after leaving it.
            if (SharedColony.Session.Phase == Simulation.SharedPhase.Playing
                && SharedColony.Session.WorldId == manifest.Id && SharedColony.Session.Epoch == session.Epoch)
            {
                Status = "已在此世界运行";
                return;
            }
            // Even the publisher loads the accepted snapshot; origin does not prove its live state still
            // equals the bytes saved before the asynchronous upload.
            SharedColony.OnWorldStarted(session, manifest);
            DownloadAndLoad(manifest, id, SharedColony.Session.LoadGeneration);
        });
        socket.FrameReceived += frame => AuthoritativeReplica.Receive(frame);
        socket.Ticked += tick => PostWorld(() => SharedColony.OnTick(tick));
        socket.CommandsReceived += commands => PostWorld(() => SharedColony.OnCommands(commands));
        socket.DesyncDetected += (tick, detail) => PostWorld(() => SharedColony.OnDesync(tick, detail));
        socket.CheckpointRequested += () => PostWorld(() => checkpointRequested = true);
        socket.CatchUpIncomplete += () => PostWorld(() =>
        {
            const string reason = "服务器命令历史已截断，无法完整追平；等待控制者检查点后重连";
            SharedColony.OnWorldLoadFailed(reason);
            Status = reason;
        });
        Track(Task.Run(async () =>
        {
            try
            {
                var hello = CompatibilityManifest.Build(inputs, name, exportFile, cancel.Token);
                Post(id, () => { Fingerprint = hello.ModHash; Status = "指纹已生成，连接服务器…"; });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                if (simulation) await HeadlessRuntime.ConnectSimulationAsync(socket, hello, timeout.Token).ConfigureAwait(false);
                else await socket.ConnectAsync(host, port, hello, timeout.Token).ConfigureAwait(false);
                if (asHost) await socket.ClaimHostAsync(timeout.Token).ConfigureAwait(false);
                Post(id, () =>
                {
                    roleConfirmed = true;
                    Busy = false;
                    if (socket.CurrentPolicy != null) TurboState.Authority.ApplyServer(socket.CurrentPolicy);
                    Status = TurboTickSettings.T(asHost ? "TurboTick_HostRoleReady" : "TurboTick_WaitForHost");
                    // Preserve Started/Commands/Tick ordering while the host claim was in flight. A failed
                    // host request must never start loading another room's world behind the error dialog.
                    foreach (var action in pendingWorldEvents)
                    {
                        if (id != generation) break;
                        action();
                    }
                    pendingWorldEvents.Clear();
                    if (id == generation && publishOnConnect && !publishedOnce && IsHost && socket.PresenceEpoch < 0)
                    { publishedOnce = true; StartSharedColony(); }
                });
            }
            catch (Exception error)
            {
                try { await socket.DisconnectAsync().ConfigureAwait(false); } catch (Exception) { }
                Post(id, () =>
                {
                    pendingWorldEvents.Clear();
                    TurboState.Authority.ConnectionLost();
                    connectionFailure = error is NetworkException { Code: "HostOccupied" }
                        ? TurboTickSettings.T("TurboTick_HostOccupied")
                        : TurboTickSettings.T("TurboTick_ConnectionFailed", error.Message);
                    SetStatus(connectionFailure);
                });
            }
            finally { Post(id, () => Busy = false); }
        }));
    }

    public static void Request(bool enabled, float multiplier, float maxRate)
    {
        if (!TurboState.Authority.SessionRequested)
        {
            try
            {
                TurboState.Authority.SetLocal(enabled, multiplier, maxRate);
                var s = TurboTickMod.Settings;
                s.enabled = enabled;
                s.multiplier = multiplier;
                s.maxRateCap = maxRate;
            }
            catch (Exception error) { Status = error.Message; }
            return;
        }
        if (client?.IsConnected != true || !CanEdit) { Status = "等待服务端连接/控制权限；本地设置保持不变"; return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        Track(Task.Run(async () =>
        {
            try { await socket.SetSpeedAsync(enabled, multiplier, maxRate, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => Status = "设置未确认：" + error.Message); }
        }));
    }

    public static void RequestTimeSpeed(int speed, Action completed)
    {
        if (client?.IsConnected != true || !CanEdit) { completed(); return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        // Start immediately on the caller's thread. Task.Run per click would reorder fast key presses
        // before the SDK's ordered speed queue ever sees them.
        Track(SendAsync());
        async Task SendAsync()
        {
            try { await socket.SetTimeSpeedAsync(speed, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => SetStatus("原生速度未确认：" + error.Message)); }
            finally { Post(id, completed); }
        }
    }

    /// <summary>Dedicated-server host action: publish the chosen colony over the existing connection.</summary>
    public static void StartSharedColony()
    {
        if (client?.IsConnected != true) { Status = "先连接服务器"; return; }
        if (!PatchInstaller.AllCritical)
        {
            Status = "本机补丁不完整，无法开启共享殖民地（见诊断信息）";
            return;
        }
        if (!IsHost) { Status = TurboTickSettings.T("TurboTick_HostConnectFirst"); return; }
        if (client.PresenceEpoch >= 0) { Status = TurboTickSettings.T("TurboTick_ServerWorldContinues"); return; }
        if (Current.ProgramState != ProgramState.Playing) { Status = TurboTickSettings.T("TurboTick_ChooseSave"); return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        Status = "正在保存当前殖民地…";
        SharedColony.HoldForPublication();
        byte[] bytes;
        SnapshotManifest manifest;
        try
        {
            var path = WorldFiles.SaveCurrentGame();
            bytes = File.ReadAllBytes(path);
            manifest = WorldFiles.Describe(bytes);
        }
        catch (Exception error) { SharedColony.OnWorldLoadFailed(error.Message); Status = "保存失败：" + error.Message; return; }
        SimulationDiagnostics.Boundary("snapshot-created", manifest.Id);
        Status = "正在上传存档（" + bytes.Length / 1024 + " KiB）…";
        Track(Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                await socket.UploadWorldAsync(manifest, stream, token).ConfigureAwait(false);

                await socket.StartGameAsync(token).ConfigureAwait(false);
                Post(id, () => Status = "已发布并开始共享殖民地");
            }
            catch (Exception error) { Post(id, () => { SharedColony.OnWorldLoadFailed(error.Message); Status = "启动共享殖民地失败：" + error.Message; }); }
        }));
    }

    private static void DownloadAndLoad(SnapshotManifest manifest, int id, long loadGeneration)
    {
        var socket = client;
        var token = lifetime!.Token;
        var path = WorldFiles.DownloadPath(manifest.Id);
        Track(Task.Run(async () =>
        {
            try
            {
                await socket!.DownloadSnapshotAsync(manifest, path, null, token).ConfigureAwait(false);
                Post(id, () =>
                {
                    if (id != generation || loadGeneration != SharedColony.Session.LoadGeneration || SharedColony.Session.WorldId != manifest.Id) return;
                    try
                    {
                        var saveName = WorldFiles.PrepareLoad(path);
                        SetStatus("正在载入共享存档：" + saveName);
                        SharedColony.BeginWorldLoad(saveName);
                        GameDataSaveLoader.LoadGame(saveName);
                        // Loading has consumed the copy; keep Saves/ and the cache bounded so a long session
                        // does not fill the drive with one opaque file per checkpoint.
                        WorldFiles.PruneSaves();
                        WorldFiles.PruneCache();
                    }
                    catch (Exception error) { SharedColony.OnWorldLoadFailed(error.Message); SetStatus("载入共享存档失败：" + error.Message); }
                });
            }
            catch (Exception error)
            {
                Post(id, () =>
                {
                    if (loadGeneration != SharedColony.Session.LoadGeneration || SharedColony.Session.WorldId != manifest.Id) return;
                    SharedColony.OnWorldLoadFailed(error.Message);
                    SetStatus("下载共享存档失败：" + error.Message);
                });
            }
        }));
    }

    /// <summary>Lockstep pacing: how far this peer has actually simulated. Sent fire-and-forget because the
    /// timeline stalls if it is late, so it must never queue behind a slow round trip.</summary>
    public static void ReportProgress(int tick)
    {
        if (client?.IsConnected != true) return;
        var socket = client;
        var token = lifetime?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await socket.ReportProgressAsync(tick, token).ConfigureAwait(false); }
            catch (Exception) { /* a dropped progress report only costs one tick of pacing */ }
        }, token);
    }

    public static Task ReportHash(int tick, string sha256, IReadOnlyList<string>? categories = null)
    {
        if (client?.IsConnected != true) return Task.CompletedTask;
        var socket = client;
        var token = lifetime!.Token;
        var id = generation;
        // Enter the client immediately so its session/epoch is captured before another game-thread
        // reconnect or load can replace it; Task.Run used to let old hash work enter the new epoch.
        return SendAsync();
        async Task SendAsync()
        {
            try { await socket.ReportHashAsync(tick, sha256, categories, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => Status = "状态校验上报失败：" + error.Message); }
        }
    }

    private static bool autoConnectTried;
    private static bool publishOnConnect;
    private static bool publishedOnce;

    /// <summary>Developer aid used by the acceptance run: join once after a colony loads.</summary>
    public static void AutoConnectOnce()
    {
        if (HeadlessRuntime.Active || autoConnectTried) return;
        autoConnectTried = true;
        if (TurboState.Authority.SessionRequested || MultiplayerHosting.Pending || MultiplayerHosting.IsHosting) return;
        // Automated acceptance runs pass -turbotick-connect=host:port on the command line.
        string? argument = null;
        if (GenCommandLine.TryGetCommandLineArg("turbotick-connect", out argument) && !string.IsNullOrWhiteSpace(argument))
        {
            var parts = argument.Split(':');
            var host = parts[0].Trim();
            var port = parts.Length > 1 ? parts[1].Trim() : "30572";
            var commandLineSettings = new TurboTickSettings
            {
                ServerHost = host.Length > 0 ? host : "127.0.0.1",
                ServerPort = port,
                PlayerName = "AcceptanceClient"
            };
            var autoPublish = GenCommandLine.CommandLineArgPassed("turbotick-publish");
            Log.Message("[TurboTick] 命令行自动连接：" + commandLineSettings.ServerHost + ":" + port);
            Connect(commandLineSettings, asHost: autoPublish, publishCurrentOnConnect: autoPublish);
            return;
        }
        var settings = TurboTickMod.Settings;
        if (settings is not { autoConnect: true } || string.IsNullOrWhiteSpace(settings.ServerHost)) return;
        Log.Message("[TurboTick] 开发用自动连接：开始");
        Connect(settings);
    }

    public static bool IsCheckpointOwner => client?.IsCheckpointOwner == true;

    /// <summary>This peer's id in the room, or 0 before the handshake. Used to recognise the echo of a
    /// command this peer already applied locally.</summary>
    public static long LocalPeerId => client?.PlayerId ?? 0;

    /// <summary>True when the server owns the simulation. Replicas follow frames and every peer is frozen,
    /// so cross-peer hash comparison has nothing to compare.</summary>
    public static bool AuthoritativeRoom { get; private set; }

    /// <summary>Every peer simulates. The server paces the timeline from the slowest report, so hashes are
    /// comparable again and progress must be reported each tick.</summary>
    public static bool LockstepRoom { get; private set; }

    /// <summary>Desync recovery: drop the session and join again, which reloads the checkpoint and replays the delta.</summary>
    public static void ReconnectFromFault()
    {
        var settings = reconnectTarget;
        if (settings == null || string.IsNullOrWhiteSpace(settings.ServerHost)) return;
        Connect(settings, asHost: HostRequested && client?.PresenceEpoch == -1, simulation: HeadlessRuntime.Active);
    }
    private static bool checkpointRequested;
    private static int checkpointing;
    private static Task<bool>? checkpointOperation;
    internal static Task<bool> PendingCheckpoint => checkpointOperation ?? Task.FromResult(true);
    internal static bool CheckpointBusy => Volatile.Read(ref checkpointing) != 0;


    /// <summary>Checkpoint owner: saves the colony at the current tick and publishes it so late joins replay less.</summary>
    public static void PublishCheckpoint(int tick) => _ = PublishCheckpointAsync(tick);

    // Called on the game thread. Completion means the server committed the actual snapshot, not just
    // that saving was queued; headless termination waits for this result before flushing the protocol.
    internal static Task<bool> PublishCheckpointAsync(int tick)
    {
        if (client?.IsConnected != true || !IsCheckpointOwner || !SharedColony.Gating
            || Current.ProgramState != ProgramState.Playing || Current.Game is null
            || Scribe.mode != LoadSaveMode.Inactive) return Task.FromResult(false);
        if (Interlocked.Exchange(ref checkpointing, 1) != 0) return Task.FromResult(false);
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        byte[] bytes;
        SnapshotManifest manifest;
        try
        {
            var path = WorldFiles.SaveCurrentGame();
            bytes = File.ReadAllBytes(path);
            manifest = WorldFiles.Describe(bytes);
            SimulationDiagnostics.Boundary("checkpoint-created", manifest.Id);
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref checkpointing, 0);
            SetStatus("检查点保存失败：" + error.Message);
            return Task.FromResult(false);
        }
        var operation = Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                await socket.CheckpointWorldAsync(manifest, stream, tick, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception error)
            {
                Post(id, () => SetStatus("检查点上传失败：" + error.Message));
                return false;
            }
            finally { Interlocked.Exchange(ref checkpointing, 0); }
        });
        checkpointOperation = operation;
        Track(operation);
        return operation;
    }

    public static Task SendCommand(string kind, byte[] payload, long sequence)
    {
        if (client?.IsConnected != true || !TurboState.Authority.SessionConnected) return Task.CompletedTask;
        var socket = client;
        var token = lifetime!.Token;
        var id = generation;
        return Task.Run(async () =>
        {
            try { await socket.SendCommandAsync(kind, payload, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => Status = "命令发送失败：" + error.Message); }
        });
    }

    public static void CacheSnapshot()
    {
        if (client?.IsConnected != true) { SnapshotStatus = "先连接服务器"; return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        var cache = Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTickCache");
        SnapshotStatus = "请求快照清单…";
        Track(Task.Run(async () =>
        {
            try
            {
                var manifest = await socket.GetSnapshotManifestAsync(token).ConfigureAwait(false);
                if (manifest == null) { Post(id, () => SnapshotStatus = "服务器尚未导入快照"); return; }
                var path = Path.Combine(cache, manifest.Id + ".snapshot");
                await socket.DownloadSnapshotAsync(manifest, path, (done, total) =>
                    Post(id, () => SnapshotStatus = $"缓存 {done / 1024}/{total / 1024} KiB"), token).ConfigureAwait(false);
                Post(id, () => SnapshotStatus = "校验完成，已缓存；未替换当前游戏存档");
            }
            catch (Exception error) { Post(id, () => SnapshotStatus = "快照下载中断，可重试续传：" + error.Message); }
        }));
    }

    public static void Leave()
    {
        SessionChanging?.Invoke();
        AuthoritativeRoom = false;
        LockstepRoom = false;
        publishOnConnect = false;
        publishedOnce = false;
        checkpointRequested = false;
        MultiplayerHosting.Cancel();
        HostRequested = false;
        Endpoint = "";
        PlayerPresence.Reset();
        ++generation; // Drop all queued actions from an old socket, including late disconnect events.
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        var old = client;
        client = null;
        if (old != null) Track(Task.Run(async () => await old.DisposeAsync().ConfigureAwait(false)));
        TurboState.Authority.LeaveSession();
        SharedColony.Reset();
        NativeSpeedSync.Reset();
        Status = "未连接";
        Busy = false;
    }

    public static void Pump()
    {
        // Bound per-frame work so a large snapshot's progress reports cannot stall GUI indefinitely.
        for (int n = 0; n < 128 && gameActions.TryDequeue(out var action); n++)
            try { action(); } catch (Exception error) { Log.Warning("[TurboTick] " + error.Message); }
        operations.RemoveAll(task => task.IsCompleted);
        if (checkpointRequested && !HeadlessRuntime.StopRequested && !CheckpointBusy && IsCheckpointOwner
            && SharedColony.Gating && Current.ProgramState == ProgramState.Playing && Current.Game is not null
            && Scribe.mode == LoadSaveMode.Inactive && !LongEventHandler.AnyEventNowOrWaiting)
        {
            checkpointRequested = false;
            PublishCheckpoint(SharedColony.Session.Barrier.AppliedTick);
        }
        if (!HeadlessRuntime.Active) PlayerPresence.Update();
    }

    private static void Post(int id, Action action) => gameActions.Enqueue(() => { if (id == generation) action(); });
    private static void Track(Task task)
    {
        // All task bodies handle their errors; this extra observer also covers Dispose failures.
        operations.Add(task.ContinueWith(t => { if (t.Exception != null) _ = t.Exception; }, TaskScheduler.Default));
    }
}
