using System.Collections.Concurrent;
using TurboTick.Networking;
using TurboTick.Simulation;
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
    /// <summary>Idle status text, resolved on read rather than at type load.
    ///
    /// A field initializer would call `Translate()` from `RoomConnection`'s static constructor, which can run
    /// before the language database is ready (the mod installs its Harmony patches early). The backing field
    /// stays empty for "nothing is running" and the getter fills in the translated text, so the value is always
    /// resolved against the language that is actually active.
    ///
    /// It is a property rather than a literal because the literal used to be the hard-coded Chinese "未连接"
    /// while every other string went through `T()` - that is how an English client ended up showing one
    /// Chinese word in the status line.</summary>
    private static string status = "";

    public static string Status
    {
        get => status.Length == 0 ? NotConnected : status;
        private set => status = value;
    }

    private static string NotConnected => TurboTickSettings.T("TurboTick_NotConnected");

    /// <summary>The status string as it should be shown right now.
    ///
    /// <see cref="Status"/> is an event log - it is only written when something happens, so a peer that
    /// finished loading and has been ticking for an hour still says "正在载入共享存档：&lt;hash&gt;". HUD headers,
    /// tooltips and the headless status line read this instead: the phase and the applied tick are live
    /// values, so the text follows them. The measured symptom was `In sync · tick 0` for a whole session.
    /// </summary>
    public static string LiveStatus
    {
        get
        {
            var session = SharedColony.Session;
            if (session.Phase is not (SharedPhase.Playing or SharedPhase.Synchronizing or SharedPhase.LoadingWorld))
                return Status;
            if (!string.Equals(Status, NotConnected, StringComparison.Ordinal) && !IsPhaseText(Status)) return Status;
            return session.Phase switch
            {
                SharedPhase.LoadingWorld or SharedPhase.Synchronizing => TurboTickSettings.T("TurboTick_HudSyncing"),
                SharedPhase.Playing => TurboTickSettings.T("TurboTick_HudSynced", session.Barrier.AppliedTick),
                _ => Status
            };
        }
    }

    /// <summary>True for the strings this class generates from a phase, i.e. ones that may be recomputed.
    /// Anything else is event text (a failure reason, "waiting for the host") and must not be overwritten.</summary>
    private static bool IsPhaseText(string text)
    {
        var syncing = TurboTickSettings.T("TurboTick_HudSyncing");
        if (string.Equals(text, syncing, StringComparison.Ordinal)) return true;
        // "In sync · tick N" differs only in the trailing number.
        var synced = TurboTickSettings.T("TurboTick_HudSynced", 0);
        var head = synced[..Math.Max(0, synced.LastIndexOf('0'))];
        return head.Length > 0 && text.StartsWith(head, StringComparison.Ordinal);
    }
    public static string Fingerprint { get; private set; } = "尚未生成";
    public static string SnapshotStatus { get; private set; } = "尚未下载；此阶段只缓存快照，不自动载入游戏";
    public static bool Busy { get; private set; }

    // 基线下发状态（服务端把权威存档整份推过来 -> 载入 -> 回 WorldReady）。
    // `baselineExpected` 只在这个会话真的走基线时置位：协调器房走的是检查点/Started 老路，
    // 那时帧路径必须保持原样，不能因为这几个开关多丢一帧。
    private static bool baselineExpected;
    private static bool baselineLoaded;
    private static int preBaselineFrames;
    private static bool worldReadySent;
    internal static DedicatedClient? Client => client;
    public static bool CanEdit => !TurboState.Authority.SessionRequested
        || (TurboState.Authority.SessionConnected && client?.CanEdit == true);

    /// <summary>Whether a captured action could actually be delivered right now.
    ///
    /// This is the same condition <see cref="SendCommand"/> uses to decide whether it can write. It exists as a
    /// property so the input gate can require it: if the capture runs while this is false, the local effect has
    /// already been cancelled and the command goes nowhere, which the player experiences as "砍伐 does nothing".
    /// Measured: a stockpile drag produced 24 published commands, zero gate drops, zero send failures, and the
    /// server received none of them - so "the capture ran" is not the same as "the command can be delivered".
    /// </summary>
    public static bool CanDeliver => client?.IsConnected == true && TurboState.Authority.SessionConnected;

    /// <summary>Milliseconds since anything was last received from the server. A half-closed TCP connection still
    /// accepts writes without error, so this is the only way to notice that the peer is talking to nobody.</summary>
    public static long SinceLastReceived => client?.MillisecondsSinceLastReceived ?? -1;

    /// <summary>Every user-visible status also lands in Player.log so a run can be reviewed afterwards.</summary>
    /// <summary>Turns a mod mismatch into something actionable. Opening the dialog is the fix; the returned text is
    /// only what the status line says if the dialog cannot be shown (no window stack yet, or no list from the server).
    /// </summary>
    private static string OpenModSync(string exportFile)
    {
        var summary = ModSyncDialog.Describe();
        if (summary.Length == 0) return TurboTickSettings.T("TurboTick_FingerprintMismatch", Fingerprint, exportFile);
        try { ModSyncDialog.ShowForCurrentMismatch(); }
        catch (Exception error) { Log.Warning("[TurboTick] 模组同步窗口打开失败：" + error.Message); }
        return TurboTickSettings.T("TurboTick_ModMismatchDetail", summary);
    }
    private static void SetStatus(string text)
    {
        Status = text;
        Log.Message("[TurboTick] " + text);
    }

    /// <summary>Lets the session publish a status the HUD can show. Without this the last thing written was
    /// "正在载入共享存档：<snapshot>" (DownloadAndLoad), so a peer that had finished loading and was ticking
    /// still displayed the loading message - visible in
    /// 192.168.0.29:/data/rimworld/det/peerA/console.log as status="正在载入…" next to phase=Playing.</summary>
    internal static void ReportStatus(string text) => SetStatus(text);

    /// <summary>One line describing whether commands can be delivered and how fresh the read side is. Printed when a
    /// capture is refused so "local only" is explained rather than mysterious.</summary>
    internal static string DeliveryState() =>
        $"connected={client?.IsConnected == true} session={TurboState.Authority.SessionConnected} "
        + $"lastRecvMs={SinceLastReceived}";

    internal static int SessionGeneration => generation;
    internal static event Action? SessionChanging;
    public static bool IsHost => !Busy && client?.IsConnected == true && client.IsHost && TurboState.Authority.SessionConnected;
    public static bool HostRequested { get; private set; }
    public static string Endpoint { get; private set; } = "";

    public static void Connect(TurboTickSettings settings, bool asHost = false, bool publishCurrentOnConnect = false,
        bool simulation = false)
    {
        SetStatus("开始连接 " + settings.ServerHost.Trim() + ":" + settings.ServerPort);
        // Sweep leftovers from any previous session before pulling another snapshot. A crash mid-load skips
        // the post-load prune, so without this the transport copies accumulate one per attempt.
        WorldFiles.PruneSaves();
        WorldFiles.PruneCache();
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
        // 稳定身份：这个 profile 上次从服务端拿到的 token（`<profile>\Config\TurboTick\player-token.txt`）。
        // 只在玩家连接时使用——常驻仿真端不是玩家，不该占用一条玩家记录。
        var playerToken = simulation ? null : SessionIdentity.Read();
        if (playerToken is not null)
            Log.Message($"[TurboTick] 带上本地身份 token={SessionTokens.Short(playerToken)}（{SessionIdentity.FilePath}）");
        // 基线落盘的目录：客户端本地没有任何存档时，服务端推来的整份存档先落到这里，
        // 校验通过后再复制进 Saves/ 交给 GameDataSaveLoader。
        var socket = client = new DedicatedClient(WorldFiles.Directory);
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
        socket.FrameReceived += frame =>
        {
            // 基线没载入完之前收到的帧一律丢弃：帧是增量，应用到一张旧地图上会永久错位，
            // 而且服务端在 WorldReady 之前本来就不该发（这里是双保险）。
            if (baselineExpected && !baselineLoaded) { Interlocked.Increment(ref preBaselineFrames); return; }
            AuthoritativeReplica.Receive(frame);
        };
        // 基线下发：本地完全没有存档的客户端靠这条路起来。
        // 网络线程只负责拼块+校验+落盘，落盘后的载入一定回到游戏线程（Post）。
        socket.BaselineStarted += (session, manifest, saveName) => Post(id, () =>
        {
            baselineExpected = true;
            baselineLoaded = false;
            AuthoritativeRoom = session.RoomAuthoritative;
            LockstepRoom = session.Lockstep;
            SetStatus($"收到权威存档 {saveName}（{manifest.Length / 1024} KiB），正在接收分块…");
        });
        socket.BaselineReady += (path, manifest, session) => Post(id, () => ApplyBaseline(path, manifest, session, id));
        socket.BaselineFailed += reason => Post(id, () =>
        {
            // 失败必须说出来并且断开：一个"连上了但画面是旧的"客户端比连不上更难查。
            SharedColony.OnWorldLoadFailed(reason);
            SetStatus("基线下发失败：" + reason);
            Leave();
        });
        socket.Ticked += tick => PostWorld(() => SharedColony.OnTick(tick));
        // 会话视图恢复：服务端在 WorldReady 之后下发一次，这里转投到游戏线程执行（切图/选中/相机都只能在游戏线程动）。
        socket.ViewRestoreReceived += view => Post(id, () => SessionViewSync.OnRestore(view));
        socket.CommandsReceived += commands => PostWorld(() => SharedColony.OnCommands(commands));
        socket.DesyncDetected += (tick, detail) => PostWorld(() => SharedColony.OnDesync(tick, detail));
        // A refused command used to exist only in the coordinator's console. This is the player-facing half.
        socket.NoticeReceived += notice => Post(id, () => RoomNotices.OnNotice(notice));
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
                else await socket.ConnectAsync(host, port, hello, timeout.Token, playerToken).ConfigureAwait(false);
                if (asHost) await socket.ClaimHostAsync(timeout.Token).ConfigureAwait(false);
                Post(id, () =>
                {
                    roleConfirmed = true;
                    Busy = false;
                    // 服务端下发的稳定身份：首次写入；服务端没认出本地 token 时在这里换成新的。
                    if (!simulation) SessionIdentity.Adopt(socket.PlayerToken);
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
                    connectionFailure = error switch
                    {
                        NetworkException { Code: "HostOccupied" } => TurboTickSettings.T("TurboTick_HostOccupied"),
                        // A bare "fingerprints differ" is a dead end: the player has no way to learn which mod
                        // to fix. Point at the per-file manifest this client just wrote - diffing it against
                        // the host's names the exact file - and show the fingerprint so the two sides can be
                        // compared in one glance. When the server sent its mod list, open the dialog that can
                        // actually fix it: install these, order them like this.
                        NetworkException { Code: "ModHashMismatch" } => OpenModSync(exportFile),
                        _ => TurboTickSettings.T("TurboTick_ConnectionFailed", error.Message),
                    };
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
            var confirmed = false;
            try
            {
                await socket.SetTimeSpeedAsync(speed, token).ConfigureAwait(false);
                confirmed = true;
            }
            catch (Exception error) { Post(id, () => SetStatus("原生速度未确认：" + error.Message)); }
            finally
            {
                // Only clear the pending selection when the server actually confirmed it. Reporting success
                // after a failed request left the client believing the room had changed speed while the
                // server still published the old one - the local UI showed a speed nobody else was running.
                if (confirmed) Post(id, completed);
            }
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

    /// <summary>基线存档已落盘并通过 sha256：接上会话状态、复制进 Saves/、交给游戏自己的载入器。
    ///
    /// 和 <see cref="DownloadAndLoad"/> 的唯一区别是字节从哪来（服务端推送 vs 客户端拉取），
    /// 后面完全一样：<c>.rws</c> 就是完整的地图序列化，用 <c>GameDataSaveLoader</c> 载入，
    /// 一致性由 Scribe 保证。载入完成由 <see cref="SharedColony.ScheduleWorldLoaded"/> 收尾，
    /// 这里随后在 <see cref="SendWorldReadyIfLoaded"/> 里回 WorldReady。</summary>
    private static void ApplyBaseline(string path, SnapshotManifest manifest, SessionInfo session, int id)
    {
        if (id != generation) return;
        try
        {
            AuthoritativeReplica.OnWorldStarted(session);
            SharedColony.OnWorldStarted(session, manifest);
            var saveName = WorldFiles.PrepareLoad(path, WorldFiles.BaselineSaveName(manifest.Id));
            SetStatus($"正在载入权威存档：{saveName}（{manifest.Length / 1024} KiB）");
            Log.Message($"[TurboTick] 基线存档校验通过，开始载入：{saveName}（{manifest.Length} 字节 / sha={manifest.Id[..16]}）");
            SharedColony.BeginWorldLoad(saveName);
            GameDataSaveLoader.LoadGame(saveName);
        }
        catch (Exception error)
        {
            // 载入前就失败（文件系统、Saves 目录、Scribe 头）——原因要说出来，并且断开。
            var detail = ModSyncDialog.Describe();
            SharedColony.OnWorldLoadFailed(error.Message);
            SetStatus("载入权威存档失败：" + error.Message + (detail.Length > 0 ? "（" + detail + "）" : ""));
            Leave();
        }
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
                        // The copy is deleted once the load finishes, in SharedColony.ScheduleWorldLoaded:
                        // deleting here would race the long event and silently leave the file behind.
                        GameDataSaveLoader.LoadGame(saveName);
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
        // A headless server owns its room and must not join itself; a headless participant is a player.
        if (HeadlessRuntime.ServerMode || autoConnectTried) return;
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
                // Every committed checkpoint also becomes the world the next process starts from. Doing it here
                // rather than only on the shutdown path matters for the case that actually happened: a container
                // stopped with SIGKILL never reaches the shutdown path, and the resumed colony was then the import
                // at tick 0 while the room's own snapshot store held a complete world nobody loaded.
                //
                // Off the game thread: it is a 20 MB copy, and a tick stall of that size is visible in the pacing
                // numbers. `CaptureResume` swallows its own failures - the checkpoint above is already durable.
                WorldFiles.CaptureResume();
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

    public static Task SendCommand(string kind, byte[] payload, long sequence, Action? onUnsent = null)
    {
        // A command dropped here leaves the change local and never reaches another peer, and until now nothing
        // recorded that it happened: no log line, no counter, no trace. Measured: a 190-cell stockpile drawn on one
        // peer produced 48 captured zone-cell commands and zero on the other side.
        if (client?.IsConnected != true || !TurboState.Authority.SessionConnected)
        {
            SendGate.Record(kind, sequence,
                client?.IsConnected != true ? "no-client" : "session-offline");
            // The local action was already applied when it was captured, and the command was supposed to carry it to
            // the other peers. If it cannot be sent, the caller is told so it can apply the action locally instead of
            // losing it - "I pressed 砍伐 and nothing happened anywhere" is worse than a visible desync.
            TryOnUnsent(onUnsent, kind);
            return Task.CompletedTask;
        }
        var socket = client;
        var token = lifetime!.Token;
        var id = generation;
        return Task.Run(async () =>
        {
                try
                {
                    await socket.SendCommandAsync(kind, payload, token).ConfigureAwait(false);
                    // Count completed writes, not issued ones. `pubgate sent` proves the call was made; the write
                    // completes later on a worker thread. Measured: 24 publishes, zero gate drops, zero failures,
                    // and the server parsed exactly one frame - so "issued" and "written" must be told apart.
                    PublishGate.Note("written " + kind);
                    // Which connection did this write reach? Measured: 25 writes completed, the far end parsed exactly
                    // one Command, there were no residual frames and no long merged frame - so the writes went to a
                    // socket the server was not reading. Two connections can exist at once (a stale session plus the
                    // live one) and the identity is the only way to see it.
                    SocketSendGate.Note(kind, sequence, socket.DescribeSession());
                }
                catch (Exception error)
                {
                    // The status string alone was invisible: measured, a peer sent 25 zone-cell commands, the
                    // server received none, and no log line anywhere recorded a failure because this message only
                    // ever went to the UI. A transport failure has to be findable in the log.
                    SendFailureGate.Record(kind, sequence, error);
                    Post(id, () => Status = "命令发送失败：" + error.Message);
                    TryOnUnsent(onUnsent, kind);
                }
            });
    }

    private static void TryOnUnsent(Action? onUnsent, string kind)
    {
        if (onUnsent is null) return;
        try { onUnsent(); }
        catch (Exception error) { Post(generation, () => Status = $"本地补应用失败（{kind}）：" + error.Message); }
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
        // 退出前最后上报一次视图：这是「我走的时候在看哪」的唯一可靠时机，断开之后就没有相机了。
        SessionViewSync.ReportBeforeLeave();
        SessionChanging?.Invoke();
        AuthoritativeRoom = false;
        LockstepRoom = false;
        publishOnConnect = false;
        publishedOnce = false;
        checkpointRequested = false;
        baselineExpected = false;
        baselineLoaded = false;
        worldReadySent = false;
        preBaselineFrames = 0;
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
        SessionViewSync.Reset();
        Status = NotConnected;
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
            && Scribe.mode == LoadSaveMode.Inactive && !LongEventHandler.AnyEventNowOrWaiting
            // Never at the tick the world was loaded on: that save would carry a world which has not simulated
            // a single tick, while the manifest claims the current timeline tick.
            && SharedColony.Session.Barrier.AppliedTick > SharedColony.LoadedAtTick)
        {
            checkpointRequested = false;
            PublishCheckpoint(SharedColony.Session.Barrier.AppliedTick);
        }
        if (!HeadlessRuntime.Active) PlayerPresence.Update();
        SendWorldReadyIfLoaded();
        // 会话视图：防抖上报 + 执行服务端下发的恢复。只在游戏线程跑（相机/选中不能在别处碰）。
        SessionViewSync.Update();
    }

    /// <summary>存档载入完成后回 WorldReady —— 服务端收到它才开始给本端发帧。
    ///
    /// 判断条件故意用「Phase 已经是 Playing 且没有长事件/没有 Scribe 交互」而不是某个回调：
    /// 两条载入路径（服务端推送基线 / 老的 Started+拉取）都会走到同一个终态，
    /// 这样帧门控对两条路都成立，也不依赖载入回调的调用顺序。
    ///
    /// `LongEventHandler.Busy` 期间不处理帧（见 FrameReceived 的过滤），这里同理不在事件中途发。</summary>
    private static void SendWorldReadyIfLoaded()
    {
        if (worldReadySent || client?.IsConnected != true) return;
        if (SharedColony.Session.Phase != Simulation.SharedPhase.Playing) return;
        if (LongEventHandler.AnyEventNowOrWaiting || Scribe.mode != LoadSaveMode.Inactive) return;
        if (Current.ProgramState != ProgramState.Playing) return;
        var worldId = SharedColony.Session.WorldId;
        if (string.IsNullOrEmpty(worldId)) return;
        var id = generation;
        var socket = client;
        var epoch = SharedColony.Session.Epoch;
        var loadedTick = SharedColony.Session.Barrier.AppliedTick;
        // 载入后的物体 id 水位：权威端会把 id 大于它的物体作为「间隙新建」补发给本端。
        var nextThingId = SharedActions.NextThingId();
        worldReadySent = true;
        baselineLoaded = true;
        var dropped = Volatile.Read(ref preBaselineFrames);
        Log.Message($"[TurboTick] WorldReady：世界 {worldId[..Math.Min(16, worldId.Length)]} 已载入（epoch={epoch}，"
            + $"载入 tick={loadedTick}，id 水位={nextThingId}），等服务端开帧；基线未就绪期间丢弃的帧 {dropped} 个。");
        Track(Task.Run(async () =>
        {
            try { await socket.SendWorldReadyAsync(worldId, epoch, loadedTick, nextThingId).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => SetStatus("WorldReady 发送失败：" + error.Message)); }
        }));
    }

    private static void Post(int id, Action action) => gameActions.Enqueue(() => { if (id == generation) action(); });

    /// <summary>Observes a fire-and-forget report. Dropping the task meant a failed hash send surfaced only as
    /// an unobserved-exception at the next GC, i.e. never.</summary>
    internal static void TrackReport(Task task) => Track(task);
    private static void Track(Task task)
    {
        // All task bodies handle their errors; this extra observer also covers Dispose failures.
        operations.Add(task.ContinueWith(t => { if (t.Exception != null) _ = t.Exception; }, TaskScheduler.Default));
    }
}









/// <summary>Records which connection each successful write used.
///
/// Measured: 25 zone-cell writes returned successfully, the server parsed exactly one Command, `residues` stayed at
/// zero and no long merged frame appeared. Those three together rule out the read side and the framing, and leave
/// "the writes went to a socket nobody is reading" - which happens when a stale session object survives next to the
/// live one. Comparing the identities is what turns that from a guess into an observation.
/// </summary>
internal static class SocketSendGate
{
    private const int Capacity = 64;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Note(string kind, long sequence, string connection)
    {
        lock (Recent)
        {
            Recent.Add($"sendconn {kind} seq={sequence} {connection}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    /// <summary>How many distinct connection identities were used, and how many writes each carried.</summary>
    internal static string Counters()
    {
        lock (Recent)
        {
            if (Recent.Count == 0) return "sendconn=none";
            var groups = Recent
                .Select(line => line[(line.IndexOf(" sess=", StringComparison.Ordinal) is var i and >= 0
                    ? i + 1 : 0)..])
                .GroupBy(text => text, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Count() + "x{" + g.Key + "}");
            return "sendconn=" + string.Join(",", groups);
        }
    }
}

