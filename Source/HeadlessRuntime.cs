using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Owns the network authority and one permanent simulation peer. Verse work stays on the game
/// thread; the protocol, snapshots and lockstep adapter are shared with the regular client.</summary>
internal static class HeadlessRuntime
{
    /// <summary>Hosts a listener and a room. This is the dedicated server.</summary>
    internal static bool ServerMode { get; } = Environment.GetCommandLineArgs()
        .Any(a => a.Equals("-turbotick-server", StringComparison.OrdinalIgnoreCase));

    /// <summary>A headless player: no listener, no room of its own, it joins someone else's room exactly
    /// like a normal client. It exists so a two-peer determinism check can run without a display, which is
    /// otherwise the only way to tell whether the simulation is actually deterministic.</summary>
    internal static bool ParticipantMode { get; } = Environment.GetCommandLineArgs()
        .Any(a => a.Equals("-turbotick-participant", StringComparison.OrdinalIgnoreCase));

    internal static bool Active { get; } = ServerMode || ParticipantMode;

    /// <summary>Whether this process runs the engine's headless loop at all — `-headless` is what decides
    /// that, not our own mode flags.
    ///
    /// A display replica is started as `-headless -quicktest -turbotick-connect=HOST:PORT` and sets neither
    /// <see cref="ServerMode"/> nor <see cref="ParticipantMode"/>. Reading only the two mode flags therefore
    /// said "this is an ordinary windowed client" about a process that never renders anything and never
    /// sleeps: `Waylith.EngineApplication.RunHeadlessLoop` is a bare `while` with no sleep, no vsync and no
    /// `TargetFrameRate`. Measured on a three-replica run: `停顿期本端循环 1-1190 次` — up to 1190 pumps
    /// between two frames (about 1900 fps, one pinned core each) and as few as one during a hitch, so every
    /// replica was both burning a core and being starved of it.</summary>
    internal static bool HeadlessProcess { get; } = GenCommandLine.CommandLineArgPassed("headless");
    private static readonly ConcurrentQueue<Action> pending = new();
    private static DedicatedServer? server;
    private static Task? startup;
    private static Task? persistence;
    private static Task? shutdown;
    private static FileStream? directoryLock;
    private static PosixSignalRegistration? terminate;
    private static PosixSignalRegistration? interrupt;
    private static string dataDirectory = "";
    private static string? initialSave;
    private static bool initialLoadRequested;
    private static bool bootstrapStarted;
    private static bool attached;
    private static bool stopping;
    private static float lastAttachWarn = float.MinValue;
    /// <summary>The world is loaded but deliberately out of service ("stop" without exiting). See <see cref="MarkWorldStopped"/>.</summary>
    private static volatile bool worldStopped;
    private static bool finalized;
    /// <summary>The configured save was refused because the server lacks a mod it needs. Sticky: this process
    /// will not load it later either, so the state stays <c>failed</c> until an operator fixes the mods and
    /// restarts.</summary>
    private static bool saveModsBlocked;
    private static int stopRequested;
    private static int exitCode;
    private static float nextStatus;
    private static float nextPersist;
    private static float nextConnect;
    private static float startedAt;
    private static string state = "starting";
    private static string? failure;
    internal static bool StopRequested => Volatile.Read(ref stopRequested) != 0;

    // Read-only views published for the administration portal (Portal/, a separate process). It reads these
    // through <data>/headless-status.json rather than through this assembly, so these stay plain reads of
    // state this class already owns.
    internal static DedicatedServer? Server => server;
    internal static string State => state;
    internal static string? Failure => failure;
    /// <summary>The save this authority loaded or was configured with; what an operator means by "当前存档".</summary>
    internal static string? CurrentSave => server?.GameRunning == true ? initialSave ?? "（未命名世界）" : initialSave;
    internal static double UptimeSeconds => Math.Max(0, Clock.Now - startedAt);
    private static volatile string? installedVersion;
    private static volatile string? installedModHash;
    internal static string? InstalledVersion => installedVersion;
    internal static string? InstalledModHash => installedModHash;

    /// <summary>Graceful termination on request (the administration page's <c>quit</c>).
    ///
    /// Deliberately does not exit here: the stop flag is consumed on the game thread, which checkpoints,
    /// persists and only then lets the process leave. Exiting from a request handler would drop a world that
    /// players are still connected to.</summary>
    internal static bool RequestStop(string reason)
    {
        if (Volatile.Read(ref stopRequested) != 0) return false;
        Log.Message("[TurboTick headless] 收到退出请求（" + reason + "）：先检查点，再持久化，最后退出。");
        Interlocked.Exchange(ref stopRequested, 1);
        return true;
    }

    /// <summary>What an admin-requested save load did, so the page can say something true.</summary>
    internal enum SaveLoadRequest
    {
        /// <summary>Recorded; the existing load funnel picks it up on the game thread.</summary>
        Accepted,
        /// <summary>That save is already the running world.</summary>
        AlreadyRunning,
        /// <summary>A different world is running. <c>autoSwitch</c> decides whether the caller wants it replaced.</summary>
        RestartRequired,
        /// <summary>The process is already shutting down.</summary>
        Stopping
    }

    /// <summary>
    /// The web page's "start with this save". Deliberately not a second loader: it points
    /// <see cref="initialSave"/> at the requested name and clears <see cref="initialLoadRequested"/>, so the same
    /// <c>GameDataSaveLoader.LoadGame</c> call that <c>-turbotick-save=</c> uses performs the load.
    ///
    /// <paramref name="autoSwitch"/> is what "switch, don't stop" means: when it is set and a different world is
    /// running, this returns <see cref="SaveLoadRequest.RestartRequired"/> <em>only as information</em> - the caller
    /// unloads the old world itself and calls <see cref="BeginSwitchTo"/> before loading the new one. With it unset
    /// (the old behaviour) the request is refused outright, which is what a caller that cannot unload should get.
    /// </summary>
    internal static SaveLoadRequest RequestSaveLoad(string name, bool autoSwitch = false)
    {
        if (stopping || Volatile.Read(ref stopRequested) != 0) return SaveLoadRequest.Stopping;
        var running = !worldStopped && Current.ProgramState == ProgramState.Playing && Current.Game is not null;
        if (string.Equals(initialSave, name, StringComparison.Ordinal))
        {
            if (running) return SaveLoadRequest.AlreadyRunning;
            // Same save, no world. If a Game object is still mounted (a stopped world), the reload has to go through a
            // fresh process: calling LoadGame a second time in one process segfaults the engine (measured, exit 139).
            // Only a genuinely empty slate - nobody ever loaded anything - is safe to load in place.
            if (Current.Game is not null) return SaveLoadRequest.RestartRequired;
            worldStopped = false;
            initialLoadRequested = false;
            bootstrapStarted = false;
            saveModsBlocked = false;
            Log.Message("[TurboTick headless] 重新载入存档 " + name + "（此前未载入过任何世界）");
            return SaveLoadRequest.Accepted;
        }
        if (running)
        {
            // The decision is the caller's; this only reports the state. `autoSwitch` exists so an accidental
            // caller cannot get a silent replacement of a world players are attached to.
            return SaveLoadRequest.RestartRequired;
        }
        initialSave = name;
        initialLoadRequested = false;
        Log.Message("[TurboTick headless] 管理台请求载入存档：" + name + "（游戏线程按启动路径载入）");
        return SaveLoadRequest.Accepted;
    }

    /// <summary>
    /// Prepares the load funnel for a replacement world: points <see cref="initialSave"/> at the target and rearms
    /// <see cref="initialLoadRequested"/>, so the next game-thread pass performs the load through the one existing
    /// path.
    /// </summary>
    internal static void BeginSwitchTo(string name)
    {
        initialSave = name;
        initialLoadRequested = false;
        bootstrapStarted = false;
        saveModsBlocked = false;
        // A switch *is* the release of the stop: the fluent order is stop (freeze) → aim at the target → load.
        worldStopped = false;
        Log.Message("[TurboTick headless] 换档：下一次游戏线程推进将载入 " + name);
    }

    /// <summary>Abandons a switch whose unload was refused: the old world is still loaded, so nothing changes.</summary>
    internal static void CancelSwitch() => Log.Warning("[TurboTick headless] 换档已取消，继续使用当前世界");

    /// <summary>
    /// Marks the loaded world as "stopped": ticks stop, the room is emptied, and the status says <c>lobby</c>.
    ///
    /// <b>Why not unload the game object:</b> measured on the deployment host, clearing <c>Current.Game</c> makes
    /// <see cref="Verse.Root_Play"/> throw a <c>NullReferenceException</c> every frame - the engine dereferences it
    /// unconditionally inside <c>Update</c>. A game object therefore has to stay, so "stopped" is expressed as
    /// "frozen": no ticks, no room world, and a status that admits there is no running colony. Loading the next save
    /// replaces the object through <c>GameDataSaveLoader</c>, which is the engine's own path.
    /// </summary>
    internal static void MarkWorldStopped(string reason)
    {
        worldStopped = true;
        // The load funnel had already claimed this pass (it sets `initialLoadRequested` before checking anything
        // else). Clearing it here is what makes "stopped" mean stopped instead of "loading the same save again".
        initialLoadRequested = false;
        // The room session belongs to the world being stopped, and `Busy` would otherwise stay true for the next
        // load (measured: it blocked the funnel forever). `Leave()` is the existing teardown; it does not touch the
        // admin session, the listener or the port.
        try { RoomConnection.Leave(); }
        catch (Exception error)
        {
            Log.Warning("[TurboTick headless] 停止世界时断开房间失败（继续）：" + error.GetType().Name);
        }
        attached = false;
        // A stop is a deliberate state, not a failure: this deadline is a start-up promise, and it must not be
        // re-armed here (doing so would also zero the reported uptime, which is a different lie).
        var owner = server;
        if (owner is not null)
        {
            try { owner.ResetForNewWorld(reason); }
            catch (Exception error)
            {
                Log.Warning("[TurboTick headless] 清空房间失败（世界仍已停止）：" + error.GetType().Name + ": " + error.Message);
            }
        }
        state = "lobby";
        WriteStatus();
        Log.Message("[TurboTick headless] 房间已清空（" + reason + "）：端口 " + (owner?.Port.ToString() ?? "?")
            + " 保持监听，兼容性锁保留，服务端回到 lobby");
    }

    /// <summary>True when the pending exit is a requested restart, not a shutdown: the checkpoint is already committed.</summary>
    private static bool restartRequested;

    /// <summary>Releases the stop: called when a world is about to be loaded, because a new game is a new world.</summary>
    internal static void ClearWorldStopped() => worldStopped = false;

    /// <summary>
    /// Asks for a fresh game process that loads <paramref name="name"/>.
    ///
    /// A marker file next to the game state is the whole mechanism: the entrypoint supervises the game process, sees
    /// the marker after a clean exit, and starts the game again with <c>TURBOTICK_SAVE</c> pointing at the new save.
    /// The container - and therefore the published admin port - never goes away, which is the only reason this is
    /// acceptable: reloading a world in place segfaults the engine (measured, exit 139).
    ///
    /// The save name is written too, so the next process does not depend on the operator remembering a flag.
    /// </summary>
    internal static void RequestRestart(string name)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "restart-target"), name);
            File.WriteAllText(Path.Combine(dataDirectory, "restart-requested"),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            Log.Message("[TurboTick headless] 已请求重启游戏进程以载入 " + name + "（容器与管理页端口保持）");
        }
        catch (Exception error)
        {
            Log.Error("[TurboTick headless] 写重启请求失败：" + error.GetType().Name + ": " + error.Message);
        }
        // Clean exit: the entrypoint only restarts on 0, so an error path can never be mistaken for a requested swap.
        restartRequested = true;
        exitCode = 0;
        Interlocked.Exchange(ref stopRequested, 1);
    }

    /// <summary>Diagnostics for the page: what the load funnel currently believes.</summary>
    internal static bool WorldStoppedForDiagnostics => worldStopped;
    internal static bool LoadRequestedForDiagnostics => initialLoadRequested;
    internal static string? ConfiguredSaveForDiagnostics => initialSave;

    /// <summary>Whether this process also simulates the colony.
    ///
    /// False in a coordinator room (TURBOTICK_LOCKSTEP=1): the players simulate and the server only
    /// orders commands and paces the timeline. A resident simulator there would be one more copy of the
    /// colony to keep in sync - N players become N+1 - without adding any capability, because rendering
    /// works fine on a peer that ticks.
    ///
    /// Kept switchable because the display-replica path still needs it, and because retiring the resident
    /// simulator is the change under test.</summary>
    internal static bool ResidentSimulation => !Lockstep;

    /// <summary>A coordinator holds its own colony forever; only a resident simulator may tick one.
    ///
    /// Keyed on <see cref="ServerMode"/> rather than <see cref="Active"/>: a headless participant is a player
    /// and must tick its own colony, otherwise it would sit at the checkpoint while the room waits for it.</summary>
    /// <summary>The room is holding the timeline only because no display replica is attached.</summary>
    internal static bool HeldForAudience => server?.HeldForAudience == true;

    /// <summary>How many display replicas the room counts as attached. 0 means every tick is held.</summary>
    internal static int ReplicaCount => server?.DisplayReplicaCount ?? -1;

    internal static bool HoldLocalTicks => worldStopped
        || ServerMode && (!ResidentSimulation || !SharedColony.Gating || stopping);

    internal static void Start()
    {
        if (!Active || startup is not null) return;
        startedAt = Clock.Now;
        try
        {
            if (!GenCommandLine.CommandLineArgPassed("headless"))
                throw new ArgumentException("-turbotick-server requires -headless.");
            // A participant owns no room, no state directory and no listener: it joins through the normal
            // client path, so none of the server bootstrap below applies to it.
            if (!ServerMode)
            {
                state = "participant";
                Log.Message("[TurboTick headless] participant mode: no listener; joining through -turbotick-connect.");
                return;
            }
            // A coordinator never runs a colony, so the gameplay patches are irrelevant to it; requiring
            // them would fail a host that is only relaying and pacing.
            if (ResidentSimulation && (!PatchInstaller.AllCritical || PatchInstaller.Status.Any(p => !p.Value)))
                throw new InvalidOperationException("Dedicated simulation requires every gameplay patch to be installed.");
            dataDirectory = Path.GetFullPath(Option("data") ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTickServer"));
            Directory.CreateDirectory(dataDirectory);
            directoryLock = new FileStream(Path.Combine(dataDirectory, "headless.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            initialSave = Option("save");
            if (initialSave is not null && (initialSave.Length == 0 || initialSave != Path.GetFileNameWithoutExtension(initialSave)
                || initialSave.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || initialSave.Contains('/') || initialSave.Contains('\\')))
                throw new ArgumentException("TURBOTICK_SAVE must be a save name under the selected profile's Saves directory, without extension.");
            // 续跑只在「没人指定存档」时接管；显式 `-turbotick-save` 永远优先。
            //
            // 这里原来是反的：`if (WorldFiles.ResumeSave is { } resume) initialSave = resume;` 无条件覆盖，
            // 于是操作者在管理页换档、入口脚本也确实传了 `-turbotick-save=import-xxx`，进程却照样载入
            // `TurboTickCurrent`。实测取证：日志先打 `启动：载入存档 import-20261115T194217Z-a1558f`，
            // 下一行却是 `载入存档 TurboTickCurrent`，玩家看到的殖民地与刚上传的那份完全不是一个存档
            // （导入档里的角色是 Jan-Hendrik/Jackalope，跑起来的是 Price/Austin/Kelvin）。
            //
            // 续跑本身仍然必要——上一次干净退出前写下的「当前世界」才是应该接着玩的那一局，缺了它每次
            // 容器重启都会把殖民地清回导入档（实测 `import-20261114T112230Z-9ae814` tick=0，而关闭时已提交
            // tick=28316 的检查点，玩家看到「建好的墙全没了」）。区别只在于：那是**默认值**，不是**强制值**。
            if (initialSave is null && WorldFiles.ResumeSave is { } resume) initialSave = resume;
            var port = int.Parse(Option("port") ?? "30572", NumberStyles.None, CultureInfo.InvariantCulture);
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException("port");
            // The administration page lives in Portal/ and owns its own listener, bind decision and port
            // validation; -turbotick-admin-* is accepted and ignored here so an unchanged compose file keeps
            // working.
            var inputs = CompatibilityManifest.Capture();
            var export = Path.Combine(dataDirectory, "compatibility-manifest.json");
            var host = Option("host") ?? "0.0.0.0";
            var controllerOnly = Option("controller-only") is "true" or "1";
            // Management uses the configured shared password independently of the player hosting role.
            string? adminPassword = null;
            var passwordFile = Environment.GetEnvironmentVariable("TURBOTICK_ADMIN_PASSWORD_FILE");
            if (passwordFile is not null)
            {
                if (string.IsNullOrWhiteSpace(passwordFile) || new FileInfo(passwordFile).Length > 16 * 1024)
                    throw new IOException("Invalid management password file.");
                adminPassword = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
                if (string.IsNullOrEmpty(adminPassword) || adminPassword.Length > 4096)
                    throw new IOException("Invalid management password file.");
            }
            else adminPassword = Environment.GetEnvironmentVariable("TURBOTICK_ADMIN_PASSWORD");
            // Started before the room so the page is already up (showing "starting") while the world loads.
            // The same password protects the web writes and the TCP management protocol; when it is unset the
            // page stays read-only and every write is refused.
            //
            // The administration portal is its own executable now (Portal/), so the only thing this
            // process still owes it is the data it cannot compute for itself: where the saves, mods and
            // config live, and whether the save's mod list may be applied automatically.
            //
            // The mod verdict itself is shared code (Networking/SaveModsSync.cs) rather than a message:
            // ModsConfig.xml is read before this mod's code exists, so the portal can decide whether to
            // start a process but only this process can decide whether a world may be loaded into it.
            PortalCommands.Configure(
                dataDirectory: dataDirectory,
                savesDirectory: GenFilePaths.SavedGamesFolderPath,
                gameDirectory: Option("game-dir") ?? dataDirectory,
                // ModsFolderPath, not <savedatafolder>/Mods: the game only ever scans the former (GenFilePaths
                // resolves it to <exe parent>/Mods), and in the container that path is a bind mount of /data/mods.
                // The old value pointed at profile/Mods, which used to be a symlink to the same place and is now
                // simply gone - a path that exists only for a linker is a path that lies the moment it moves.
                modsDirectory: GenFilePaths.ModsFolderPath,
                modsConfigPath: Path.Combine(GenFilePaths.ConfigFolderPath, "ModsConfig.xml"),
                autoApply: (Option("auto-apply-mods") ?? "on").Trim().ToLowerInvariant()
                    is not ("off" or "0" or "false" or "no"));
            // The split portal talks to this process through two files, not through the in-process queue:
            // `<data>/portal-commands/<id>.json` in, `<id>.done.json` back. Wiring it above is what makes the
            // standalone management page able to command a running world at all - the queue that used to live in
            // this assembly belonged to the HTTP listener, and that listener is now in another process.
            // A configured `-turbotick-save=` must also be started with *its own* mod list, not with whatever the
            // container wrote. This runs before the room boots because mods are loaded once, at process start:
            // nothing written here can affect the current session, so the honest outcome when the config is wrong
            // is "write it and ask for one restart", never "start the world anyway and lose the missing mods'
            // content".
            PortalCommands.VerifyConfiguredSaveMods(initialSave);
            // A refusal must stop the world from loading, but it must not become a crash loop: the container's
            // restart policy would restart this process every few seconds and the operator would see "starting"
            // forever instead of the sentence that explains what is missing. The gate inside Update() publishes
            // `state = failed` and keeps the process up so the page can show the reason.
            if (PortalCommands.SaveModsRefusal is { } saveProblem && initialSave is not null)
                Log.Error("[TurboTick headless] 拒绝载入 " + initialSave + "：" + saveProblem
                    + "（进程保持运行以便管理页显示原因；补齐 mod 后重启即可）");
            // Lockstep: one diverged peer must reload the checkpoint and rejoin like anyone else, otherwise a
            // single differing plant stops the whole room. Authoritative rooms keep holding for diagnosis,
            // because there the resident is the only copy of the truth.
            TurboTickMod.Settings.autoRecover = Lockstep;
            Waylith.EngineApplication.Instance.OnShutdown += Shutdown;
            Console.CancelKeyPress += OnCancel;
            if (!OperatingSystem.IsWindows())
            {
                terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
                interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            }
            startup = Task.Run(async () =>
            {
                DedicatedServer? candidate = null;
                try
                {
                    var hello = CompatibilityManifest.Build(inputs, "DedicatedSimulation", export, CancellationToken.None);
                    // What this process is running, as opposed to what the persisted room locked in. The page
                    // reports the comparison as comp== / comp!=, which is the one-line answer to "did someone
                    // swap the modpack under a running room".
                    installedVersion = hello.GameVersion;
                    installedModHash = hello.ModHash;
                    candidate = new DedicatedServer(new ServerOptions(host, port, dataDirectory, hello.GameVersion,
                        hello.ModHash, !controllerOnly, AdminPassword: adminPassword)
                    {
                        RequireSimulation = ResidentSimulation, Authoritative = ResidentSimulation, Lockstep = Lockstep,
                        // A dedicated room left with nobody attached must not simulate: the operator asking for a
                        // world at 21:00 and finding it at day 40 the next morning is not "it kept running", it is
                        // a server that spent the night inventing a colony. Only resident rooms gate on this; the
                        // coordinator branch has no world to hold.
                        PauseWithoutReplicas = ResidentSimulation,
                        // The room has to know which save this process is for: a persisted room otherwise restores
                        // the previous world and the load funnel never fires (see DedicatedServer.LoadStateAsync).
                        StartupSave = initialSave
                    });
                    await candidate.StartAsync().ConfigureAwait(false);
                    // A persisted room from another modpack must not be silently overwritten or simulated.
                    if (candidate.LockedGameVersion != hello.GameVersion || candidate.LockedModHash != hello.ModHash)
                        throw new InvalidDataException("Persisted room differs from the installed game/modpack. Restore the matching pack or archive the room before starting.");
                    var ready = candidate;
                    pending.Enqueue(() =>
                    {
                        server = ready;
                        state = ready.GameRunning ? "restoring" : "lobby";
                        Log.Message($"[TurboTick headless] listening on {host}:{ready.Port}; protocol={NetworkProtocol.Version}; game={hello.GameVersion}");
                        ConnectSimulation();
                    });
                }
                catch (Exception error)
                {
                    if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
                    pending.Enqueue(() => Fail("Startup", error));
                }
            });
        }
        catch (Exception error) { Fail("Configuration", error); }
    }

    private static float nextParticipantLog;

    /// <summary>One line per five seconds describing what a headless participant is doing. Without it a
    /// stalled acceptance run looks like a live process with a log that simply stops.</summary>
    private static void ReportParticipant()
    {
        // TurboGameComponent is the pump for a normal client, but it deliberately stands aside in a headless
        // process because Root.Update pumps instead. Nothing else would fire the command-line auto-connect,
        // so a headless participant has to drive it here.
        //
        // The connect has to wait for a real colony: the publish path refuses to start while the game is not
        // Playing, and AutoConnectOnce only ever runs once, so connecting during world generation would burn
        // the single attempt and leave the room without a world.
        if (Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting)
            RoomConnection.AutoConnectOnce();
        // Only the host drives the scripted actions; two peers issuing the same commands would mask a
        // command-path defect by producing identical (but wrong) state on both sides.
        if (RoomConnection.IsHost) AcceptanceRun.Update();
        if (Clock.Now < nextParticipantLog) return;
        nextParticipantLog = Clock.Now + 5f;
        try
        {
            var session = SharedColony.Session;
            Log.Message($"[TurboTick headless] participant phase={session.Phase} status=\"{RoomConnection.LiveStatus}\" "
                + $"connected={RoomConnection.Client?.IsConnected} busy={RoomConnection.Busy} "
                + $"tick={session.Barrier.AppliedTick} server={session.Barrier.ServerTick} backlog={session.Barrier.Backlog} "
                // The fingerprint is what a rejected handshake is about, so printing it turns "differ from the
                // server" into two values that can be compared instead of a guess. Sliced defensively: before
                // the first capture it is null or shorter than the prefix, and [..12] on that throws.
                + $"fp={Short(RoomConnection.Fingerprint)} "
                + $"gating={SharedColony.Gating} game={Current.ProgramState}");
        }
        catch (Exception error) { Log.Warning("[TurboTick headless] participant status: " + error.GetType().Name); }
    }

    /// <summary>First twelve characters, or "-" when the value is absent or shorter than that.</summary>
    private static string Short(string? value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length <= 12 ? value : value[..12];

    internal static Task ConnectSimulationAsync(DedicatedClient client, ClientHello hello, CancellationToken ct) =>
        (server ?? throw new InvalidOperationException("The embedded server has not started.")).ConnectSimulationAsync(client, hello, ct);

    private static void ConnectSimulation()
    {
        // A coordinator owns no colony, so it has nothing to simulate and must not register as a peer:
        // the resulting N+1'th copy would be exactly the state the room would then have to keep in sync.
        if (!ResidentSimulation || server is null || stopping) return;
        // Note on the guard that used to be here: `server.GameRunning` means "a world has been published", which is
        // *not* the same as "this process holds a session to it". Skipping the connect in that case left the room at
        // `peers=0` with the status stuck on `restoring` - measured after every stop/restart. The session is what
        // makes the loaded world live, so it has to be (re)established whenever it is missing.
        if (RoomConnection.Client?.IsConnected == true) return;
        Log.Message("[TurboTick headless] 尝试接入仿真会话（" + RoomConnection.Status + "）");
        RoomConnection.Connect(new TurboTickSettings
        {
            ServerHost = "127.0.0.1", ServerPort = server.Port.ToString(CultureInfo.InvariantCulture),
            PlayerName = "DedicatedSimulation", showHud = false, showPlayerCursors = false, autoConnect = false
        }, simulation: true);
        nextConnect = Clock.Now + 10;
    }

    /// <summary>Publishes the host's pawns so display replicas can follow without simulating. Two ticks per
    /// frame is enough for smooth rendering and halves the payload.</summary>
    private const int FrameInterval = 2;
    private static int lastPublishedTick = -1;

    /// <summary>The last frame handed to <c>BroadcastFrame</c>. Read by <see cref="WireRoundTripProbe"/>.</summary>
    internal static AuthoritativeFrame? LastPublishedFrame { get; private set; }
    private static int lastFrameLogTick = -1;
    private static int framesSent;
    private static int lastFrameTick = -1;

    /// <summary>How many blueprints this peer currently holds, and how many it ever sent as a creation.
    ///
    /// Counted because "the designator replays fine" and "the frame carries the blueprint" are two different claims,
    /// and the first one being green is exactly what made this defect unreadable: the replay probe reported
    /// `重放3/有蓝图3/方块无0` while the receiving client stayed empty. Two numbers separate the remaining causes:
    /// holding 0 means the object never existed here (the replay landed in a different map or was wiped at once);
    /// holding N while sent 0 means it exists here and never crosses the wire, which is a DiffThings problem, not a
    /// designator one.</summary>
    private static int blueprintsInWorld;
    private static int blueprintsSent;

    /// <summary>How many `Skyfaller`s the frame actually carried, and how many this peer has.
    ///
    /// <para>「the carrier is on the map」 and 「the carrier is in the frame」 are different claims, and only the second
    /// one makes a launch visible to a replica. Measured before the tweak: the `census` row printed `坠落物：1 个在坠落`,
    /// which is a reading of this peer's map and says nothing about the wire. These two counters are written inside the
    /// same loop that assembles the frame, so a non-zero `skyfallersSent` means the object reached `Changed`/`Created`.</para></summary>
    private static int skyfallersInWorld;
    private static int skyfallersSent;

    /// <summary>How many times the frame diff has run at all.
    ///
    /// <para>**Without this number the two counters above cannot be read.** `PublishFrame` returns early unless
    /// `tick &gt; lastPublishedTick` (`:1615`), and on a container with no replica the tick may not advance at all - so
    /// `skyfallersInWorld == 0` would mean either "the filter dropped it" or "the diff has not run since the launch",
    /// which are opposite conclusions. Measured after a successful launch: `0 / 0` with no way to tell which.</para></summary>
    private static int diffRuns;

    /// <summary>Whether the frame's own filter and serialisation would carry each airborne carrier, measured without
    /// a replica.
    ///
    /// <para><b>Why this and not the frame counters.</b> `PublishFrame` (`:1610`) returns unless a replica is attached and
    /// the tick advances, so `skyfallersSent` is structurally zero on an empty container - measured as
    /// 「帧差分共运行 1 次」 and the census's own 「房间无副本(PublishFrame 不跑)」. A counter that can only be non-zero in
    /// a situation this container cannot produce measures nothing. What *is* measurable here is the two things that
    /// actually broke: the filter predicate (`:1322`) and whether the object serialises to a non-empty payload. This
    /// evaluates the same two steps the loop does, and says so rather than implying the wire was observed.</para>
    ///
    /// <para>It does not touch `lastThings`, so running it cannot consume a change that the real frame still owes.</para>
    /// </summary>
    internal static string SkyfallerCarriage()
    {
        var total = 0;
        var wouldCarry = 0;
        var samples = new List<string>();
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not Skyfaller faller) continue;
                total++;
                // **The loop's own predicate, not a copy.** See `InFrameScope`.
                if (!InFrameScope(thing)) continue;
                var xml = faller is IExposable exposable ? ScribeBridge.Save(exposable) : null;
                if (xml is null) { samples.Add(faller.def.defName + " 无载荷"); continue; }
                wouldCarry++;
                samples.Add(faller.def.defName + " " + xml.Length + " B / 槽 " + FallSlot(faller));
            }
        return $"在坠 {total} 个 / 过滤与序列化后应进帧 {wouldCarry} 个"
            + (samples.Count == 0 ? "" : "；" + string.Join("；", samples))
            + "（此读数不经过 BroadcastFrame：无副本时 PublishFrame 不运行）";
    }

    internal static string SkyfallerSummary() =>
        $"本端天空坠落物 {skyfallersInWorld} 个 / 作为新建或变化发出 {skyfallersSent} 个"
        + $"（帧差分共运行 {diffRuns} 次）";

    /// <summary>The two blueprint numbers, readable without a display replica: the frame log only prints when one is
    /// connected, and this defect has to be measurable on an idle authority too.
    ///
    /// The first number is counted live rather than read from <see cref="DiffThings"/>: that counter only advances
    /// when a frame is published, and a paused authority publishes nothing, so a probe reading it back reported
    /// `重放3/有蓝图3` and `本端蓝图 0 个` at the same instant - the replay had verified three blueprints on the grid
    /// while the "how many do I hold" number was simply stale. Counting the map answers the real question: is the
    /// object still there a frame later.</summary>
    internal static string BlueprintSummary()
    {
        var live = 0;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing is Blueprint) live++;
        return $"本端蓝图 {live} 个（上一帧扫描 {blueprintsInWorld}）/ 作为新建发出 {blueprintsSent} 个";
    }

    /// <summary>The forbidden flag belongs here even though a command normally carries it: a refused or lost
    /// command leaves one peer's items crossed out forever, and nothing else in the frame ever revisits them.
    /// Measured: 98 forbid commands were refused by the per-peer budget in one session, and the affected stacks
    /// stayed forbidden on every peer with no repair path.</summary>
    /// <summary>Comparing eight numbers was not enough for a plant: growth is the only field that changes after it is
    /// sown, and it changes nothing else. Measured consequence: a growing zone showed a bare field of
    /// `GraphicSowing` forever, because the object never entered `Changed` again after its creation frame.
    /// <para>`Growth` is quantised, not raw. `growthInt` moves by ~1e-5 per tick and a frame is sent every ~1/30 s,
    /// so including it verbatim would put every plant in every frame for the life of the colony. A plant grows from
    /// 0 to 1 over `growDays` game days, so a 1/256 step is a visible size change at 30 fps and still bounds the
    /// traffic: the field is only re-sent ~256 times over an entire growth cycle.</para>
    /// <para>`Grown` is a separate slot rather than part of the tuple's `Rotation` so the tuple's shape stays one
    /// int per concern; `-1` means "not a plant". `Burnt` is the same arrangement for `Fire.fireSize`, and
    /// `Flight` for a projectile's remaining ticks.</para>
    ///
    /// <para><b>Why a projectile needs its own slot.</b> A `Projectile`'s drawn position is not `Position` - it is
    /// `ExactPosition`, computed from `origin`, `destination` and `ticksToImpact`. `TickInterval` does assign
    /// `Position = ExactPosition.ToIntVec3()`, so the first of those numbers does change, but only when the projectile
    /// crosses a cell boundary; between crossings the tuple is constant and the replica draws the bolt standing still in
    /// the middle of a cell. Measured on the lab colony's turret: a shot spends most of its flight inside one cell at
    /// the speeds involved, so "moves once per cell" is the visible behaviour, not "moves smoothly".</para></summary>
    private static readonly Dictionary<int, (int Map, int X, int Z, int Rotation, int Stack, int Hits, bool Forbidden,
        int Contents, int Grown, int Burnt, int Flight, int Fall)> lastThings = [];

    /// <summary>Last encoded conditions per pawn, so only a real difference becomes a frame entry.
    ///
    /// <para>A null value means "no conditions", which is distinct from "not in the dictionary": the first frame a
    /// pawn appears it has no entry at all and must be sent, and after being cured it has an entry holding null and
    /// must be sent exactly once more.</para></summary>
    private static readonly Dictionary<int, string?> lastHediffs = [];

    /// <summary>Monotonic so a replica can tell a later empty list from an older non-empty one.</summary>
    private static int hediffVersion;

    /// <summary>Cell packed into one int so a job target travels without a nested object per pawn.</summary>
    private static int EncodeCell(LocalTargetInfo? target) =>
        target is { } value && value.IsValid ? value.Cell.x * 1000 + value.Cell.z : -1;

    /// <summary>`cell:thingId;...` for <see cref="Verse.AI.Job.targetQueueA"/>-style lists.
    ///
    /// The queue is not decoration: `JobDriver.ReportStringProcessed` falls back to its first valid element when
    /// `targetA` is invalid, so a hauler with three queued stacks shows the same text as a hauler with one; and
    /// several drivers build a different number of toils depending on whether the queue is empty
    /// (`JobDriver_DoBill.AnyIngredientsQueued`). Both read it, so it has to arrive before `SetupToils` runs.
    ///
    /// Capped at <see cref="MaximumQueuedTargets"/>: a cleaning job near a mess can carry fifteen filth entries,
    /// and the head of the queue is what the player sees. The cap is applied on the host so the wire format has a
    /// bound the receiving side can rely on.
    /// </summary>
    private static string? EncodeQueue(List<LocalTargetInfo>? queue)
    {
        if (queue is null || queue.Count == 0) return null;
        var count = Math.Min(queue.Count, MaximumQueuedTargets);
        var text = new System.Text.StringBuilder(count * 12);
        for (var i = 0; i < count; i++)
        {
            var target = queue[i];
            if (!target.IsValid) continue;
            if (text.Length > 0) text.Append(';');
            text.Append(EncodeCell(target)).Append(':').Append(target.Thing?.thingIDNumber ?? -1);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    /// <summary>How many queued targets travel per pawn per frame. 15 is the largest queue vanilla builds for a
    /// single job (`WorkGiver_CleanFilth`), so in practice this only trims pathological cases.</summary>
    private const int MaximumQueuedTargets = 15;

    /// <summary>The flag the red X on the ground is drawn from. It rides in the frame's change tuple so a peer
    /// whose command was refused still gets corrected by the next frame that touches the object.</summary>
    private static bool Forbidden(Thing thing) =>
        thing is ThingWithComps withComps && withComps.GetComp<CompForbiddable>() is { } comp && comp.Forbidden;

    /// <summary>Only objects that moved, changed stack, or took damage since the previous frame. The first
    /// frame after a load establishes the baseline and sends nothing, because the replica already holds
    /// the identical snapshot. Later frames also report objects that disappeared.</summary>
    /// <summary>每帧重序列化一小批建筑，把 Comp 内部变化也发出去。
    ///
    /// 六元组（位置/旋转/堆叠/血量）捕捉不到电量、门开关、施工进度、温度，而这些恰恰是玩家看得见的东西。
    /// 全图每帧重序列化太贵，所以轮转：一次 24 个，游标跨帧前进，一轮约 0.13 秒。</summary>
    private static void SweepBuildingStates(List<ReplicaThing> changed)
    {
        if (!baselineReady) return;
        // 入口自证：没有这行就无法判断「补扫没发」是因为没建筑、还是根本没被调用。
        if (sweepEntered++ == 0)
            Log.Message($"[TurboTick headless] 补扫入口 probe={Environment.GetEnvironmentVariable("TURBOTICK_PROBE_BUILDING") ?? "null"} baseline={baselineReady}");
        const int budget = 24;
        if (sweepCursor >= buildSweep.Count) { buildSweep.Clear(); sweepCursor = 0; }
        // 测试钩子：权威端自己造一栋「状态会变」的建筑，用来验证轮转补扫。
        // 必须放在权威端：ScriptedOperations 只在副本上跑，而 GenSpawn 不是被捕获的命令，
        // 所以从副本 spawn 的东西权威端根本不存在——上一轮就是这么把「补扫没发」误读成「补扫在工作」的。
        if (!probeBuildingSpawned && Environment.GetEnvironmentVariable("TURBOTICK_PROBE_BUILDING") == "1")
        {
            probeBuildingSpawned = true;
            try
            {
                var map0 = Find.Maps.FirstOrDefault();
                var def0 = DefDatabase<ThingDef>.GetNamedSilentFail("TorchLamp") ?? DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                if (map0 is not null && def0 is not null)
                {
                    var probe = ThingMaker.MakeThing(def0);
                    GenSpawn.Spawn(probe, map0.Center, map0);
                    probeThing = probe;
                    // 再放一个电池：CompPowerBattery 的电量是纯 Comp 字段，不改六元组，
                    // 正好用来验证「电量这类面板数值能不能流过去」。
                    var batteryDef = DefDatabase<ThingDef>.GetNamedSilentFail("Battery");
                    if (batteryDef is not null)
                    {
                        var battery = ThingMaker.MakeThing(batteryDef);
                        GenSpawn.Spawn(battery, map0.Center + IntVec3.East * 2, map0);
                        // 没有阵营的建筑不会出现在 allBuildingsColonist 里，补扫就永远看不到它。
                        battery.SetFaction(RimWorld.Faction.OfPlayer);
                        probeBattery = battery;
                        // 再放一台燃木发电机：CompRefuelable 的燃料是另一种 Comp 类，
                        // 和电池的 CompPowerBattery 不同类，用来扩大 Comp 覆盖。
                        var genDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
                        if (genDef is not null)
                        {
                            var generator = ThingMaker.MakeThing(genDef);
                            GenSpawn.Spawn(generator, map0.Center + IntVec3.East * 4, map0);
                            generator.SetFaction(RimWorld.Faction.OfPlayer);
                            probeGenerator = generator;
                            Log.Message("[TurboTick headless] 测试发电机已生成");
                        // 门：`Building_Door.Open` 只有 getter，状态在私有字段里。
                        // 用反射翻转它——这是第四种 Comp 类（门开关）唯一不依赖小人经过的制造方式。
                        var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                        if (doorDef is not null)
                        {
                            var door = ThingMaker.MakeThing(doorDef);
                            GenSpawn.Spawn(door, map0.Center + IntVec3.East * 6, map0);
                            door.SetFaction(RimWorld.Faction.OfPlayer);
                            probeDoor = door;
                            Log.Message("[TurboTick headless] 测试门已生成");
                        }
                        }
                        Log.Message("[TurboTick headless] 测试电池已生成");
                    }
                    Log.Message($"[TurboTick headless] 测试建筑已生成：{def0.defName} at 中心");
                }
            }
            catch (Exception error) { Log.Warning("[TurboTick] 测试建筑生成失败：" + error.Message); }
        }
        // 每 20 次调用烧掉 1 点燃料：这是唯一只改 Comp 内部状态、不动六元组的变化，
        // 用来确定判定「补扫是否真的能把它发出去」。不靠小人、不靠 AI。
        if (probeDoor is not null && ++probeTicks % 20 == 0)
        {
            try
            {
                doorOpenField ??= AccessTools.Field(probeDoor.GetType(), "openInt");
                if (doorOpenField?.GetValue(probeDoor) is bool open) doorOpenField.SetValue(probeDoor, !open);
            }
            catch (Exception) { }
        }
        if (probeGenerator is not null && ++probeTicks % 20 == 0)
        {
            try { probeGenerator.TryGetComp<RimWorld.CompRefuelable>()?.ConsumeFuel(1f); }
            catch (Exception) { }
        }
        if (probeBattery is not null && ++probeTicks % 20 == 0)
        {
            try { probeBattery.TryGetComp<RimWorld.CompPowerBattery>()?.AddEnergy(50f); }
            catch (Exception) { }
        }
        if (probeThing is not null && ++probeTicks % 20 == 0)
        {
            try { (probeThing.TryGetComp<RimWorld.CompRefuelable>())?.ConsumeFuel(1f); }
            catch (Exception) { }
        }
        // 测试钩子：让权威端定期弄伤一个殖民者，制造「健康值会变」的场景。
        // 不先造这个场景，健康投影的读数和之前的 `忙碌 0` 一样是空测量。
        if (Environment.GetEnvironmentVariable("TURBOTICK_PROBE_INJURY") == "1" && ++injuryTicks % 120 == 0)
        {
            try
            {
                foreach (var map1 in Find.Maps)
                {
                    foreach (var colonist in map1.mapPawns.FreeColonistsSpawned)
                    {
                        colonist.TakeDamage(new DamageInfo(RimWorld.DamageDefOf.Blunt, 3f));
                        Log.Message($"[TurboTick headless] 测试致伤 {colonist.LabelShort} 健康 {colonist.health?.summaryHealth?.SummaryHealthPercent:0.000}");
                        goto doneInjury;
                    }
                }
                doneInjury: ;
            }
            catch (Exception error) { Log.Warning("[TurboTick] 测试致伤失败：" + error.Message); }
        }
        // 测试钩子：让一个殖民者真的离图再回来，用来验证副本的名单对账两端都成立。
        //
        // 名单缩短在真实游戏里是常事（远行队、空投舱、任务、死亡），但实验局不会自己发生：一轮 4 小时长跑里
        // 权威端从 31 掉到 26，副本却一直报 `有job的副本 30/31`，多出来的 5 个小人站在原地永远不动，而没有任何
        // 一行读数说明它们已经不存在了。所以这里主动制造两端：`DeSpawn` 让权威端名单少一个（副本该清掉），
        // 再把同一个 pawn `GenSpawn` 回原位（副本该把它从 parkedPawns 里放回来）。
        // 只对权威端开（`-turbotick-server`），副本上由 `HeadlessRuntime.Active` 之外的路径统一关掉。
        if (Environment.GetEnvironmentVariable("TURBOTICK_PROBE_DEPART") == "1" && ++departTicks % 600 == 0)
        {
            try
            {
                if (departed is null || departed.Destroyed)
                {
                    foreach (var map1 in Find.Maps)
                    {
                        var subject = map1.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                        if (subject is null) continue;
                        departed = subject;
                        departedCell = subject.PositionHeld;
                        departedMap = map1;
                        subject.DeSpawn(DestroyMode.Vanish);
                        Log.Message($"[TurboTick headless] 离图探针：{subject.LabelShort} 已离图"
                            + $"（权威名单将少一个，副本应在两帧后清掉它）");
                        break;
                    }
                }
                else if (departedMap is not null && !departed.Spawned)
                {
                    var landed = GenSpawn.Spawn(departed, departedCell, departedMap);
                    Log.Message($"[TurboTick headless] 离图探针：{departed.LabelShort} 回到原位"
                        + $"（落地={landed is not null}；副本应把它从 parkedPawns 放回来）");
                    departed = null;
                }
            }
            catch (Exception error) { Log.Warning("[TurboTick] 离图探针失败：" + error.Message); }
        }
        // 列表每轮重建，不是只在为空时建一次。
        //
        // 原来的写法只在 `buildSweep.Count == 0` 时抓一次 `allBuildingsColonist`，于是**之后建成的建筑永远进不了
        // 这张表**：它们的 Comp 状态（电量、燃料、门开关，以及 `Building_WorkTable.billStack`）就此一次都不会被
        // 补发。实测判据是 `补扫访问`（访问次数，不看有无变化）：一个 tick 402 才由探针建成的石匠台，在 200 秒
        // 里访问次数始终是 0，而 `补扫类型` 只显示 Battery/WoodFiredGenerator/Door 三类——两行读数放在一起才
        // 看得出「它根本没被扫过」，单看任何一行都会以为这栋楼状态没变。
        //
        // 重建放在一轮结束（游标归零）时做，成本是每轮一次 lister 遍历；把 `sweepCursor` 一起归零，否则新列表
        // 短于旧游标时后面的建筑会被跳过整整一轮。
        var rebuild = buildSweep.Count == 0 || sweepRoundDone;
        if (rebuild)
        {
            sweepRoundDone = false;
            buildSweep.Clear();
            // **All** artificial buildings, not just the player's. `allBuildingsColonist` is the list the colony's own
            // buildings go in, and iterating only it left every other building's Comp state frozen: a turret's ammo, a
            // cryo pod's charge, an ancient danger's door. Measured on this save: `门：9` but `殖民者建筑 0 栋` - all nine
            // doors are world-generated and therefore had never been re-serialised even once.
            //
            // The budget below is what keeps this affordable: the list is walked in slices of 24, so a map with hundreds of
            // ruins costs the same per frame as a map with one. What grows is the cycle length, which is the honest
            // trade - a ruin's state changes far less often than a working colony's.
            foreach (var map in Find.Maps)
                foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                    if (thing is Building building) buildSweep.Add(building);
            sweepCursor = 0;
            sweepListBuilds++;
            if (buildSweep.Count == 0) return;
        }
        var end = Math.Min(sweepCursor + budget, buildSweep.Count);
        for (var i = sweepCursor; i < end; i++)
        {
            var thing = buildSweep[i];
            if (thing is null || thing.Destroyed || !thing.Spawned) continue;
            if (thing is not IExposable exposable) continue;
            string? state;
            try { state = ScribeBridge.Save(exposable); }
            catch (Exception error)
            {
                // 静默 `continue` 让「这栋楼从来没被补扫过」和「这栋楼的状态一直没变」在日志里完全一样。
                // 实测：石匠台（probe 建的）一次都没进过 `补扫类型`，而它的 billStack 明明在变——
                // 正是这行把「保存抛异常」从一个隐形状态变成一个可数的读数。
                if (sweepSaveFailures++ < 3)
                    Log.Warning($"[TurboTick headless] 补扫保存失败 {thing.def?.defName ?? "?"} id={thing.thingIDNumber}："
                        + error.GetType().Name + " " + error.Message);
                continue;
            }
            if (state is null) continue;
            var id = thing.thingIDNumber;
            if (lastBuildingState.TryGetValue(id, out var before) && before == state) continue;
            lastBuildingState[id] = state;
            changed.Add(new ReplicaThing(id, thing.Map.uniqueID, thing.Position.x, thing.Position.z,
                thing.Rotation.AsInt, thing.stackCount, thing.HitPoints, state, Forbidden(thing)));
            var defName = thing.def?.defName ?? "?";
            sweepDefCounts[defName] = sweepDefCounts.TryGetValue(defName, out var seenCount) ? seenCount + 1 : 1;
        }
        // 「扫到了但状态没变」和「根本没进过这轮扫描」在只统计变化次数的读数里完全一样。
        // 实测就踩在这里：石匠台在第 402 tick 才生成，而 `补扫类型` 里从来没有它——分不清是
        // 「它没被扫」还是「它被扫了但 billStack 没变化」，而这两件事的改法完全不同。
        for (var i = sweepCursor; i < end; i++)
        {
            var thing = buildSweep[i];
            if (thing is null || thing.Destroyed || !thing.Spawned) continue;
            var name = thing.def?.defName ?? "?";
            sweepDefSeen[name] = sweepDefSeen.TryGetValue(name, out var visits) ? visits + 1 : 1;
        }
        sweepCursor = end >= buildSweep.Count ? 0 : end;
        if (end >= buildSweep.Count) sweepRoundDone = true;
    }

    /// <summary>每帧重序列化一小批散落物品，把只存在于 Comp 里的变化（禁止位、堆叠状态、mod 自己的 Comp）也发出去。
    ///
    /// 建筑有轮转补扫，物品一直没有，而物品恰恰是唯一能出现「权威端从未变过、副本却是错的」这一类：
    /// 副本上的禁止位只有帧和命令两条更新路径，命令被拒或丢失之后没有任何补扫会重发它。
    /// 实测：副本探针连续五轮报告同一批 12 件仍然禁止，权威端每轮都能找到其中 8–10 件并「应用成功」，
    /// 但那些物品的七元组从来没变过，所以它们一次都没进过 Changed —— 副本的错值就这样永久留着。
    /// 首次遇到某个 id 直接发一次：不和副本对账就无法知道它已经错了，而一次全量物品 XML 只付一次。</summary>

    /// <summary>The things that live outside all three existing sweeps: sown plants and filth.
    ///
    /// <para><b>Why this exists.</b> Those sweeps are the only thing that carries a scribed field at all: each re-runs
    /// <c>ScribeBridge.Save</c> on its class of object and compares the XML. Anything the three of them never visit therefore
    /// has fields nobody sends, however completely its own <c>ExposeData</c> writes them - the assembly listing cannot tell
    /// that apart from "the thing forgot to scribe". Measured on this save with the <c>载体覆盖</c> census row: 16 kinds inside
    /// the sweeps, 4 outside, and the two that matter are <c>Plant</c> (×36181, of which the sown ones are sent) and
    /// <c>Filth</c> (×2454, entirely unsent). The comps follow the same split: 48 comp types inside the sweeps, 9 outside, and
    /// every one of the nine hangs on a <c>Plant</c> - <c>CompSelfhealHitpoints</c> and <c>CompSpawnSubplant</c> are on the
    /// gap list for exactly this reason.</para>
    ///
    /// <para><b>Slices, not a full pass.</b> A mature map has thousands of plants, so this walks a budget per frame like the
    /// other two. The cursor lives with theirs and the state table is separate, because a plant is neither an item nor a
    /// building and the ids would otherwise collide on nothing.</para>
    /// </summary>

    /// <summary>Rounds the two per-tick numbers in a plant's scribed XML so the sweep's comparison is about the plant rather
    /// than about the clock.
    ///
    /// <para><c>ageInt</c> advances every tick and <c>growthInt</c> every few hundred, which makes an unquantised payload
    /// differ from itself on every pass - measured, that turned the plant sweep into 扫 3600 建 3600. Everything else in
    /// there (<c>unlitTicks</c>, <c>madeLeaflessTick</c>, <c>sown</c>) moves rarely enough to send raw.</para>
    /// </summary>
    private static string QuantisePlantState(string xml)
    {
        for (var i = 0; i < PlantQuantised.Length; i++)
            xml = QuantiseElement(xml, PlantQuantised[i].Tag, PlantQuantised[i].Quantum);
        return xml;
    }

    /// <summary>The numbers in a plant's XML worth rounding, and how far.</summary>
    private static readonly (string Tag, float Quantum)[] PlantQuantised =
    [
        ("age", 2500f),
        ("growth", 0.01f),
        ("unlitTicks", 2500f),
    ];

    /// <summary>Replaces the body of the first <c>&lt;tag&gt;N&lt;/tag&gt;</c> with N rounded to the nearest multiple of
    /// <paramref name="quantum"/>. Written against the text rather than by re-scribing because the value lives in a private
    /// field the game only writes through <c>ExposeData</c>, and mutating it would change the running colony.</summary>
    private static string QuantiseElement(string xml, string tag, float quantum)
    {
        var open = "<" + tag + ">";
        var start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return xml;
        start += open.Length;
        var end = xml.IndexOf("</" + tag + ">", start, StringComparison.Ordinal);
        if (end < 0) return xml;
        var body = xml.Substring(start, end - start);
        if (!float.TryParse(body, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return xml;
        var rounded = (float)(Math.Round(value / quantum) * quantum);
        // Fixed-point, so the same plant always renders the same text: `ToString` of a float that came back from a division is
        // not stable enough to compare, and a payload that churns on formatting is the same defect as one that churns on age.
        var text = rounded.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        return xml.Substring(0, start) + text + xml.Substring(end);
    }

    /// <summary>What the plant-and-filth sweep has done, for the census. Scanned and sent are counted separately because "scanned 0"
    /// is a map with no plants and "sent 0" is a payload that never changed - the two read identically in any single number.
    /// </summary>
    internal static long PlantSweepScanned => plantSweepScanned;
    internal static long PlantSweepSent => plantSweepSent;

    private static void SweepPlantStates(List<ReplicaThing> changed)
    {
        if (!baselineReady) return;
        const int budget = 24;
        if (plantCursor >= plantSweep.Count) { plantSweep.Clear(); plantCursor = 0; }
        if (plantSweep.Count == 0)
        {
            foreach (var map in Find.Maps)
                // Filth is its own `ThingCategory`, not an item: `ThingRequestGroup.Plant` never yields it and
                // `ThingCategory.Item` excludes it, so a stain's `thickness` was never re-serialised even once. That is not
                // cosmetic - `thickness` drives `Label` ("Dirt x3"), `ThinFilth` and `CanFilthAttachNow`, and
                // `disappearAfterTicks` is the deadline `Map` compares against `TicksSinceThickened` before destroying it
                // (`:718694`). 2454 instances on this save.
                foreach (var thing in map.listerThings.AllThings)
                {
                    // **Sown plants only.** Measured on this save: 36181 plants, of which the vast majority are wild grass
                    // and bushes nobody will ever look at, while a full cycle at 24 a frame would take tens of thousands of
                    // ticks to come round. Sending them all would multiply the frame size by an order of magnitude for fields
                    // whose only reader is `GrowthPercentString` in the inspect pane - and a colony only ever plants, harvests
                    // and fails a harvest over what it sowed itself.
                    //
                    // `sown`, not `IsCrop`: the latter resolves to
                    // `def == WorkGiver_Grower.CalculateWantedPlantDef(Position, Map)` (`:600801`), which is a per-cell
                    // grower calculation run for every plant on the map on every relist - measured, that turned one 24-plant
                    // slice into 3900 plants examined. `sown` is a plain bool the sower sets.
                    if (thing is null || thing.Destroyed || !thing.Spawned) continue;
                    // **`Ethereal` is the fourth carrier and nobody was sweeping it.** `Tornado`, `Bombardment`,
                    // `OrbitalDebrisSpawner`, `RectTrigger` and every `SignalAction_*` descend from the abstract
                    // `EtherealThingBase` def, which is where `<category>Ethereal</category>` is set
                    // (`Data/Core/Defs/ThingDefs_Misc/Ethereal_Various.xml:5`); their own defs carry no category at all, so
                    // they are neither `Item` nor `Building` nor `Plant` nor `Filth` and all three existing sweeps skip them.
                    // The `载体覆盖` census row has been naming them for several rounds (`RectTrigger×2,SignalAction_Letter×2`).
                    //
                    // `Attachment` is the last carrier and it is `Fire`. A fire's `fireSize` is printed in its inspect string
                    // (`InspectStringAddon`, `:595592`, "Burning (64%)") and `fireSize > 1f` is what starts it spreading
                    // (`:595707`); both are scribed (`Fire.ExposeData` `:595614-595619`) and both are advanced only inside
                    // `Fire.Tick` (`ticksSinceSpawn += delta` `:595682`). A replica therefore draws a fire that never grows
                    // and never spreads. Cheap to sweep - a burning map has tens of them, not thousands.
                    if (thing is Plant plant && plant.sown) plantSweep.Add(plant);
                    else if (thing.def?.category is ThingCategory.Filth or ThingCategory.Ethereal or ThingCategory.Attachment)
                        plantSweep.Add(thing);
                }
            if (plantSweep.Count == 0) return;
            var alive = new HashSet<int>(plantSweep.Select(swept => swept.thingIDNumber));
            foreach (var stale in lastPlantState.Keys.Where(id => !alive.Contains(id)).ToList())
                lastPlantState.Remove(stale);
        }
        var end = Math.Min(plantCursor + budget, plantSweep.Count);
        for (var i = plantCursor; i < end; i++)
        {
            var thing = plantSweep[i];
            if (thing is null || thing.Destroyed || !thing.Spawned) continue;
            if (thing is not IExposable exposable) continue;
            plantSweepScanned++;
            string? saved;
            try { saved = ScribeBridge.Save(exposable); }
            catch (Exception) { continue; }
            if (saved is null) continue;
            // `Plant.ExposeData` scribes `ageInt`, which `Plant.Tick` advances every tick, so the raw XML of every plant on the
            // map differs from itself every tick: measured 扫 3600 建 3600, i.e. an unfiltered sweep that sends the whole
            // flora every cycle. The payload is quantised on the way out and the comparison uses the quantised text, so an
            // unchanged-looking plant costs nothing. Four in-game minutes for the age and 1% for the growth - the readers are
            // `GrowthPercentString` (prints two decimals) and the harvest thresholds, neither of which an hour can move.
            var state = QuantisePlantState(saved);
            // One sample, once: the plant sweep's only failing mode so far is "the quantiser does not match the tags that
            // are actually in the XML", which no counter can show. Printed for the first plant only.
            // The QUANTISED text, not the raw one: the raw text differs on every pass by construction, so showing it proved
            // nothing about whether the comparison key is stable. Recorded with its counters so two census calls can be
            // diffed element by element.
            plantSample = state;
            var id = thing.thingIDNumber;
            var hadBefore = lastPlantState.TryGetValue(id, out var before);
            if (hadBefore) plantCompared++;
            // Per-carrier count of what actually went out. "0 outside the sweep" only says the filter admits the carrier;
            // it does not say a payload for one was ever produced - a carrier whose Scribe fragment happens to be
            // byte-identical to the previous one, or one that throws inside `ScribeBridge.Save`, would read the same.
            var carrierName = thing.def?.category is ThingCategory.Filth or ThingCategory.Ethereal
                or ThingCategory.Attachment ? thing.def.category.ToString() : "Plant";
            if (hadBefore && before == state)
            {
                plantSweepUnchanged[carrierName] = plantSweepUnchanged.TryGetValue(carrierName, out var u) ? u + 1 : 1;
                continue;
            }
            lastPlantState[id] = state;
            plantSweepSent++;
            plantSweepByCarrier[carrierName] = plantSweepByCarrier.TryGetValue(carrierName, out var c) ? c + 1 : 1;
            // Position, hit points and the scribed payload only: a plant has no rotation to speak of and its stack count is
            // always 1, so sending them would be four columns of constants.
            changed.Add(new ReplicaThing(id, thing.Map.uniqueID, thing.Position.x, thing.Position.z,
                thing.Rotation.AsInt, 1, thing.HitPoints, state, Forbidden(thing)));
        }
        plantCursor = end >= plantSweep.Count ? 0 : end;
    }

    private static void SweepItemStates(List<ReplicaThing> changed)
    {
        if (!baselineReady) return;
        const int budget = 24;
        if (itemCursor >= itemSweep.Count) { itemSweep.Clear(); itemCursor = 0; }
        if (itemSweep.Count == 0)
        {
            foreach (var map in Find.Maps)
                foreach (var thing in map.listerThings.AllThings)
                    if (thing.def?.category == ThingCategory.Item && thing.Spawned && !thing.Destroyed)
                        itemSweep.Add(thing);
            if (itemSweep.Count == 0) return;
            // 列表重建时同步删掉已不存在的 id，否则这份缓存会随会话长度无界增长。
            var alive = new HashSet<int>(itemSweep.Select(thing => thing.thingIDNumber));
            foreach (var stale in lastItemState.Keys.Where(id => !alive.Contains(id)).ToList())
                lastItemState.Remove(stale);
        }
        var end = Math.Min(itemCursor + budget, itemSweep.Count);
        for (var i = itemCursor; i < end; i++)
        {
            var thing = itemSweep[i];
            if (thing is null || thing.Destroyed || !thing.Spawned) continue;
            if (thing is not IExposable exposable) continue;
            itemSweepScanned++;
            string? state;
            try { state = ScribeBridge.Save(exposable); }
            catch (Exception) { continue; }
            if (state is null) continue;
            var id = thing.thingIDNumber;
            if (lastItemState.TryGetValue(id, out var before) && before == state) continue;
            lastItemState[id] = state;
            changed.Add(new ReplicaThing(id, thing.Map.uniqueID, thing.Position.x, thing.Position.z,
                thing.Rotation.AsInt, thing.stackCount, thing.HitPoints, state, Forbidden(thing)));
            itemSweepSent++;
        }
        itemCursor = end >= itemSweep.Count ? 0 : end;
    }

    private static readonly List<Thing> itemSweep = new();
    private static readonly Dictionary<int, string> lastItemState = new();
    private static int itemCursor;
    private static int itemSweepScanned;
    private static int itemSweepSent;
    private static readonly List<Thing> plantSweep = new();
    private static readonly Dictionary<string, long> plantSweepByCarrier = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> plantSweepUnchanged = new(StringComparer.Ordinal);

    /// <summary>What each carrier family has actually sent, e.g. <c>Ethereal=14,Filth=2450</c>. The coverage row proves the
    /// filter admits these categories, which is a different fact from a payload ever being produced for one.</summary>
    internal static string PlantSweepByCarrier()
    {
        if (plantSweepByCarrier.Count == 0) return "（无）";
        var parts = new List<string>(plantSweepByCarrier.Count);
        foreach (var pair in plantSweepByCarrier) parts.Add(pair.Key + "=" + pair.Value);
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    /// <summary>How many comparisons per carrier family came back identical. A family that only ever appears here is being
    /// visited but never changes - which for a counter that ticks is itself a finding.</summary>
    internal static string PlantSweepUnchangedByCarrier()
    {
        if (plantSweepUnchanged.Count == 0) return "（无）";
        var parts = new List<string>(plantSweepUnchanged.Count);
        foreach (var pair in plantSweepUnchanged) parts.Add(pair.Key + "=" + pair.Value);
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static readonly Dictionary<int, string> lastPlantState = new();
    private static int plantCursor;
    private static long plantSweepScanned;
    private static long plantSweepSent;

    /// <summary>One raw plant payload, for the census. See <see cref="QuantisePlantState"/> - a quantiser that silently
    /// matches nothing reads exactly like a sweep that works.</summary>
    private static string? plantSample;
    internal static string? PlantSample => plantSample;

    /// <summary>How many of the plants seen this pass already had a recorded state, i.e. how many comparisons actually
    /// happened. Zero means the table is being rebuilt every pass and no comparison can ever succeed.</summary>
    private static long plantCompared;
    internal static long PlantCompared => plantCompared;

    /// <summary>How many plants have a recorded state. If this stays at the slice size while the scan counter climbs, the
    /// table is being rebuilt rather than compared, and no filter can ever succeed.</summary>
    internal static int PlantStateCount => lastPlantState.Count;
    private static readonly List<Building> buildSweep = new();
    private static readonly Dictionary<int, string> lastBuildingState = new();

    /// <summary>Read-only view of what was last sent for one building, for the on-demand sweep probe. The probe exists because
    /// the sweep runs only inside `PublishFrame`, which a paused server never calls - so `扫 0 栋` is the normal reading
    /// there and the log cannot tell it apart from a failure.</summary>
    internal static string? LastSentBuildingState(int id) =>
        lastBuildingState.TryGetValue(id, out var state) ? state : null;

    /// <summary>Records what a probe pass produced. Without this the probe could only ever read the table, so a second run
    /// would report every building as changed and say nothing about whether the comparison works.</summary>
    internal static void RecordBuildingState(int id, string state) => lastBuildingState[id] = state;
    private static int sweepCursor;
    private static bool probeBuildingSpawned;
    private static Thing? probeThing;
    private static int probeTicks;
    private static Thing? probeBattery;
    private static Thing? probeGenerator;
    private static Thing? probeDoor;
    private static System.Reflection.FieldInfo? doorOpenField;
    private static int injuryTicks;
    private static int departTicks;
    private static Pawn? departed;
    private static IntVec3 departedCell;
    private static Map? departedMap;
    private static int sweepEntered;
    private static int sweepSaveFailures;
    /// <summary>一轮补扫是否走完。走完就在下一次调用时重建列表，好让「之后建成的建筑」进得来。</summary>
    private static bool sweepRoundDone;
    private static int sweepListBuilds;
    /// <summary>按 defName 统计轮转补扫实际发出过多少次变化。逐类验证 Comp 投影靠它——
    /// 「机制在跑」不等于「这一类 Comp 的具体字段在流」。</summary>
    internal static readonly Dictionary<string, int> sweepDefCounts = new();
    /// <summary>按 defName 统计轮转补扫**访问过**多少次（不看有没有变化）。用来区分「没进扫描」和
    /// 「扫了但状态没变」——两者在 `sweepDefCounts` 里都是 0。</summary>
    internal static readonly Dictionary<string, int> sweepDefSeen = new();
    internal static string SweepSummary() => (sweepDefCounts.Count == 0
        // 空结果也要能自证：扫了多少栋、游标在哪、探测电池有没有生成。
        // 只写「无」的话，分不清「没建筑可扫」和「扫了但没变化」——这两种情况的处理完全不同。
        ? $"补扫类型 无（扫 {buildSweep.Count} 栋 / 游标 {sweepCursor} / 保存失败 {sweepSaveFailures} / 电池 {(probeBattery is null ? "无" : "有")}）"
        : "补扫类型 " + string.Join(",", sweepDefCounts.OrderByDescending(pair => pair.Value).Take(4)
            .Select(pair => pair.Key + "=" + pair.Value)))
        // 访问次数单列：`补扫类型` 只统计「发出过变化」的类，工作台这类状态没变的建筑在那里永远是 0，
        // 与「根本没进过扫描」无法区分。两个数一起看才能判断「这栋楼到底被扫过没有」。
        + (sweepDefSeen.Count == 0 ? "" : $"；补扫访问 {string.Join(",", sweepDefSeen
            .OrderByDescending(pair => pair.Value).Take(4).Select(pair => pair.Key + "=" + pair.Value))}"
            + $"（建表 {sweepListBuilds} 次）")
        // 物品补扫单列，因为它和建筑补扫的读数含义完全不同：建筑那张图恒为空，物品这张图有几千件。
        // 只写「扫了 N 件」不够——真正要回答的是「有没有东西被发出去」，所以两个数一起打。
        + $"；物品补扫 {itemSweepScanned} 件/发出 {itemSweepSent} 次";
    /// <summary>把殖民者的想法列表编成 `defName#stage;...`。
    ///
    /// <para><b>不再截断到 8 条。</b>上一版按 `|MoodOffset|` 取前 8，理由是「面板上排在前面的就是这些」——但面板
    /// 不是这样读的：<c>NeedsCardUtility.DrawThoughtListing</c>（<c>:645488</c>）调
    /// <c>PawnNeedsUIUtility.GetThoughtGroupsInDisplayOrder(pawn.needs.mood, thoughtGroupsPresent)</c> 拿**全部**
    /// 分组，再对每组 <c>GetMoodThoughts(group, …)</c> 取全部条目。也就是说第 9 条往后的想法在副本上根本不存在，
    /// 而它们**未必**是影响小的那些：<c>GetThoughtGroupsInDisplayOrder</c> 按分组定义排序，不是按心情影响。
    /// 实测同一时刻：`MemorySync` 发 9 条（它逐条发，不截断），`EncodeThoughts` 发 8 条 —— 两个通道对
    /// 同一个列表给出不同的长度，而它们描述的是同一个东西。</para>
    ///
    /// <para>条数有界，所以不必截断：一个殖民者的记忆列表是「吃过的饭 + 最近的社交 + 长期状态」，实测 3 个殖民者
    /// 共 9 条。真正的界在发送端之上——帧本身按 512 字节以上压缩，一条 <c>defName#stage</c> 约 30 字节。</para></summary>
    private static string? EncodeThoughts(Pawn pawn)
    {
        try
        {
            var memories = pawn.needs?.mood?.thoughts?.memories?.Memories;
            if (memories is null || memories.Count == 0) return null;
            var parts = new List<string>(memories.Count);
            foreach (var thought in memories)
            {
                if (thought?.def?.defName is not { Length: > 0 } name) continue;
                parts.Add(name + "#" + thought.CurStageIndex);
            }
            return parts.Count == 0 ? null : string.Join(Convert.ToChar(59).ToString(), parts);
        }
        catch (Exception) { return null; }
    }
    /// <summary>Encodes one pawn's conditions for a replica that never runs the code that would create them.
    ///
    /// <para>A replica does not tick, so nothing ever calls <c>Pawn_HealthTracker.HealthTick</c>: injuries never heal,
    /// diseases never progress, and a wound that happened after the snapshot never appears at all. The player's own
    /// report was "已经受伤没有任何日志" - not merely a cosmetic gap, the health tab was showing the state the save
    /// was loaded with.</para>
    ///
    /// <para>Format is <c>defName#partDefName#severity</c> per entry, joined by <c>;</c>. The part is carried because
    /// an injury without its body part is a different condition (a cut on the arm and a cut on the head are both
    /// <c>Cut</c>), and severity is carried because it is what makes the bar move and what stages a disease. Nothing
    /// else is sent: comps are rebuilt by <c>HediffMaker</c> from the def, so serialising them would duplicate work the
    /// game already does.</para>
    ///
    /// <para>Ordered by def then part so two peers with the same conditions produce the same string - the version
    /// counter on the receiving side compares strings, and an unstable order would resend forever.</para></summary>
    /// <summary>The per-type value a hediff rolls in <c>PostAdd</c> and consumes in <c>TickInterval</c>.
    ///
    /// <para><b>Why these two.</b> A rebuild via <c>HediffMaker.MakeHediff</c> + <c>AddHediff</c> runs <c>PostAdd</c>, which
    /// is exactly where both values are decided - and both decide it with a random roll, so the replica's number is a
    /// different number, not a missing one:</para>
    /// <list type="bullet">
    /// <item><c>Hediff_BloodRage.adjustedSeverityRaisePerTick</c> (<c>:121734</c>) = <c>8.8E-05f *
    /// SeverityRaiseRange.RandomInRange</c>, halved for Wimp and Kind, then times PsychicSensitivity
    /// (<c>:121756-121770</c>). <c>TickInterval</c> adds it to <c>Severity</c> on every tick spent in blood rain
    /// (<c>:121790</c>) and <c>Severity &gt;= 1f</c> is what drives the berserk / manhunter trigger (<c>:121777</c>) - so a
    /// replica whose roll came out low shows a pawn who never goes berserk.</item>
    /// <item><c>Hediff_MetalhorrorImplant.filthTimer</c> (<c>:123537</c>) = <c>InitialFilthDelayDays.RandomInRange *
    /// 60000f</c> (<c>:123601</c>, range 1.5-2.5 days, <c>:123551</c>), re-rolled to 3-10 days after each deposit
    /// (<c>:123869</c>). <c>FilthTick</c> places the grey-flesh evidence when it reaches zero (<c>:123861</c>), which is the
    /// only in-game hint that an implant exists.</item>
    /// </list>
    ///
    /// <para><b>Why not <c>Hediff_BandNode.cachedTunedBandNodesCount</c>:</b> that one is *derived*, not rolled -
    /// <c>PostAdd</c> calls <c>RecacheBandNodes</c> (<c>:121699</c>), which counts the powered, tuned BandNode buildings
    /// (<c>:121706</c>). A replica that has the buildings therefore computes the right number itself, so sending it would be
    /// a value the receiver can already prove.</para></summary>
    /// <summary>The fourth hediff field for the probe. Exposed because the value is invisible on screen: a replica with a
    /// wrongly-rolled `adjustedSeverityRaisePerTick` draws the identical pawn, and the only observable difference is whether it
    /// eventually goes berserk.</summary>
    internal static string HediffDerivedFor(Hediff hediff) => HediffDerived(hediff);

    /// <summary>The hediff set exactly as this channel would send it, for the probe. `Hediffs` is a delta channel, so by the
    /// time a round-trip is captured the change has long been sent and the frame carries nothing - the payload has to be read
    /// at the source.</summary>
    internal static string? HediffSetFor(Pawn pawn) => EncodeHediffs(pawn);

    private static string HediffDerived(Hediff hediff)
    {
        switch (hediff)
        {
            case Hediff_BloodRage bloodRage:
            {
                var info = HarmonyLib.AccessTools.Field(typeof(Hediff_BloodRage), "adjustedSeverityRaisePerTick");
                if (info?.GetValue(bloodRage) is not float raise) return string.Empty;
                // Eight significant digits: the value is ~1e-4 and multiplies the delta directly, so rounding it coarsely
                // changes how fast severity climbs. `R` keeps it exact.
                return "b" + raise.ToString("R", CultureInfo.InvariantCulture);
            }
            case Hediff_MetalhorrorImplant implant:
            {
                var info = HarmonyLib.AccessTools.Field(typeof(Hediff_MetalhorrorImplant), "filthTimer");
                if (info?.GetValue(implant) is not int timer) return string.Empty;
                // Quantised to 2500 ticks (an hour): the only reader is `filthTimer <= 0`, so an hour of error moves the
                // deposit by at most an hour out of a multi-day delay.
                return "m" + (timer / 2500 * 2500).ToString(CultureInfo.InvariantCulture);
            }
            default:
                return string.Empty;
        }
    }

    private static string? EncodeHediffs(Pawn pawn)
    {
        try
        {
            var hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs is null || hediffs.Count == 0) return null;
            var parts = new List<string>(hediffs.Count);
            foreach (var hediff in hediffs)
            {
                if (hediff?.def?.defName is not { Length: > 0 } name) continue;
                var part = hediff.Part?.def?.defName ?? "";
                var severity = hediff.Severity.ToString("R", CultureInfo.InvariantCulture);
                // Fourth field, optional: the value a hediff's own `PostAdd` rolls at random and its own `TickInterval`
                // then uses. Both are unreachable on a replica - `PostAdd` runs on the rebuild with a *fresh* roll, and the
                // tick never runs - so a hediff that should escalate stays frozen at whatever the rebuild happened to roll.
                // Only the two types that qualify are listed; see `HediffDerived`.
                var derived = HediffDerived(hediff);
                parts.Add(derived.Length == 0
                    ? name + "#" + part + "#" + severity
                    : name + "#" + part + "#" + severity + "#" + derived);
            }
            if (parts.Count == 0) return null;
            parts.Sort(StringComparer.Ordinal);
            return string.Join(Convert.ToChar(59).ToString(), parts);
        }
        catch (Exception) { return null; }
    }

    private static List<ReplicaCreation>? DescribeContents(Thing thing)
    {
        if (thing is not IThingHolder holder) return null;
        List<ReplicaCreation>? contents = null;
        var held = holder.GetDirectlyHeldThings();
        if (held is null) return null;
        for (var i = 0; i < held.Count; i++)
        {
            if (held[i] is not Thing inner || inner.def is null) continue;
            var xml = inner is IExposable exposable ? ScribeBridge.Save(exposable) : null;
            (contents ??= []).Add(new ReplicaCreation(inner.thingIDNumber, thing.Map?.uniqueID ?? 0,
                inner.def.defName, inner.Position.x, inner.Position.z, inner.Rotation.AsInt, inner.stackCount,
                xml, null, Forbidden(inner)));
        }
        return contents;
    }

    /// <summary>A short digest of what a thing holds, for change detection.
    ///
    /// <para>Only for containers whose contents the player sees and which no other sweep covers: a construction site
    /// (blueprint or frame, both `IThingHolder` with a `resourceContainer`) and anything minified or installed. The
    /// building state sweep does not reach either - a frame is a `Building` but a blueprint is `Ethereal`
    /// `ThingWithComps`, and a blueprint never enters `listerBuildings` at all.</para>
    ///
    /// <para>Returns 0 for everything else, so this cannot become an expensive per-thing walk: the walk only runs for
    /// the few def categories where the contents are the point.</para></summary>
    private static int ContainerSignature(Thing thing)
    {
        if (thing is not IConstructible && thing is not MinifiedThing) return 0;
        if (thing is not IThingHolder holder) return 0;
        var held = holder.GetDirectlyHeldThings();
        if (held is null || held.Count == 0) return 0;
        var hash = 17;
        for (var i = 0; i < held.Count; i++)
        {
            var inner = held[i];
            if (inner?.def is null) continue;
            hash = hash * 31 + inner.def.shortHash;
            hash = hash * 31 + inner.stackCount;
        }
        return hash;
    }

    /// <summary>Whether a spawned thing is in scope for the frame at all.
    ///
    /// <para><b>One definition, because a probe that copies a predicate measures the copy.</b> The first version of
    /// <see cref="SkyfallerCarriage"/> restated this condition inline and reported 「应进帧 1 个」 - which is a statement
    /// about the probe, not about the loop. If the loop's own condition changes, a copied one keeps answering the old
    /// question forever and reads as a green tick.</para></summary>
    private static bool InFrameScope(Thing thing)
    {
        if (thing is Pawn || thing.def.category == ThingCategory.Mote) return false;
        // 蓝图是 Ethereal。`Verse.ThingDef.IsBlueprint` 的定义就是
        // `entityDefToBuild != null && category == ThingCategory.Ethereal`
        // （RimWorld.ThingDefGenerator_Buildings 里 BaseBlueprintDef 写死 category = Ethereal），
        // 所以「跳过 Ethereal」这条过滤把玩家放下的每一个蓝图都丢掉了：
        // 实测权威端 117 格全部落地、下一轮探针仍能拿到它们且都在 lister 里，
        // 但帧扫描数恒为 0，副本永远收不到 — 玩家看到的就是「放蓝图没反应」。
        // Ethereal 里真正的特效类（轨道打击、天降残骸、信号）仍然跳过。
        //
        // **`Skyfaller` 也必须放行，因为「发射」本身就是一次天空坠落。** `CompLaunchable.TryLaunch`
        // （`:543651`）把 `ActiveTransporter` 交给 `Props.skyfallerLeaving ?? ThingDefOf.DropPodLeaving` ——
        // 实测这个 def 是 `Ethereal / FlyShipLeaving`（`transport-details` 直接读出来的）。于是每一个发射出去
        // 的运输舱、穿梭机、空投舱，在权威端生成后都被这一行丢掉，副本上永远看不到它飞走。而 `FallSlot`
        // （`:1446`）本来就是为了让 Skyfaller 的下降高度可见才写的——那个槽位此前是死代码，因为它服务的
        // 对象从来没走到这里。
        //
        // 判据用类型而不是 defName：Skyfaller 是一个基类，`DropPodLeaving`、`ShuttleLeaving`、
        // `GravshipLeaving` 及其 mod 派生都是它的子类，按 defName 放行等于每加一种载具就要改一次这里。
        if (thing.def.category == ThingCategory.Ethereal && thing is not Blueprint && thing is not Skyfaller) return false;
        return true;
    }

    private static (List<ReplicaThing>? Changed, List<int>? Removed, List<ReplicaCreation>? Created) DiffThings(
        List<ReplicaCreation>? catchUp = null, int catchUpWatermark = int.MaxValue)
    {
        List<ReplicaThing>? changed = null;
        List<ReplicaCreation>? created = null;
        var seen = new HashSet<int>();
        blueprintsInWorld = 0;
        skyfallersInWorld = 0;
        diffRuns++;
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!InFrameScope(thing)) continue;
                var id = thing.thingIDNumber;
                seen.Add(id);
                // Counted separately because "the designator replays fine" and "the frame carries the blueprint" are
                // two different claims and the first one being green is exactly what made this defect unreadable.
                // A blueprint is created by the replay and never moves afterwards, so if these two numbers stay at
                // zero while `设计器重放 有蓝图` climbs, the object exists on this peer and never leaves it.
                if (thing is Blueprint) blueprintsInWorld++;
                if (thing is Skyfaller) skyfallersInWorld++;
                var current = (map.uniqueID, thing.Position.x, thing.Position.z, thing.Rotation.AsInt,
                    thing.stackCount, thing.HitPoints, Forbidden(thing), ContainerSignature(thing),
                    GrowthSlot(thing), FireSlot(thing), FlightSlot(thing), FallSlot(thing));
                // `ContainerSignature` is part of the comparison because the seven numbers above cannot see a
                // construction site's materials. Measured: the authority's `Frame_Wall` held `WoodLog x2` in its
                // `resourceContainer` while its position, rotation, stack count and hit points were unchanged - so
                // `previous == current` held and the frame was never re-sent. The player watched wood carried onto a
                // wall and the "3" never move, which reads as a deposit bug and is in fact a change-detection bug.
                //
                // A blueprint is worse off than a frame: it is `ThingWithComps` in the Ethereal category, so it is in
                // neither `listerBuildings` nor the building sweep, and this tuple is the only place its materials
                // could ever be noticed.
                // 间隙补发要在「没变化就跳过」之前收集：一个在副本载入期间被生成、之后一动不动的物体
                // 正是那种永远不会再出现在增量帧里的东西。
                if (catchUp is not null && id > catchUpWatermark)
                {
                    var catchUpState = thing is IExposable catchUpExposable ? ScribeBridge.Save(catchUpExposable) : null;
                    catchUp.Add(new ReplicaCreation(id, current.Item1, thing.def.defName, current.Item2,
                        current.Item3, current.Item4, current.Item5, catchUpState, null, current.Item7));
                    if (thing is Blueprint) blueprintsSent++;
                    if (thing is Skyfaller) skyfallersSent++;
                }
                var isNew = !lastThings.ContainsKey(id);
                if (!isNew && lastThings.TryGetValue(id, out var previous) && previous == current) continue;
                lastThings[id] = current;
                if (!baselineReady) continue;
                // The object's own ExposeData carries every Comp, including third-party ones, so no
                // per-mod knowledge is needed here.
                var state = thing is IExposable exposable ? ScribeBridge.Save(exposable) : null;
                if (isNew && !(thing is Pawn))
                {
                    (created ??= []).Add(new ReplicaCreation(id, current.Item1, thing.def.defName,
                        current.Item2, current.Item3, current.Item4, current.Item5, state,
                        DescribeContents(thing), current.Item7));
                    if (thing is Blueprint) blueprintsSent++;
                    if (thing is Skyfaller) skyfallersSent++;
                    continue;
                }
                if (thing is Skyfaller) skyfallersSent++;
                (changed ??= []).Add(new ReplicaThing(id, current.Item1, current.Item2,
                    current.Item3, current.Item4, current.Item5, current.Item6, state, current.Item7));
            }
        }
        if (!baselineReady)
        {
            baselineReady = true;
            return (null, null, null);
        }
        // Comp 内部状态（电量、门开关、施工进度、温度）不改六元组，所以上面那条早退会把它们永远挡在外面——
        // 副本上的建筑因此全部停在载入值。轮转补扫，每帧只重序列化一小批并只发真正变了的。
        SweepBuildingStates(changed ??= []);
        SweepItemStates(changed);
        // Plants last: they are the only carrier outside the other two sweeps, and nine comp types live only there.
        SweepPlantStates(changed);
        List<int>? removed = null;
        foreach (var id in lastThings.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            lastThings.Remove(id);
            (removed ??= []).Add(id);
            NoteRemoval(id);
        }
        return (changed, removed, created);
    }

    /// <summary>How much of its growth cycle a plant has finished, quantised to 1/256, or -1 for anything else.
    ///
    /// <para>This is the field that makes a crop or a tree drawable. `Plant.Print` computes
    /// `num = Mathf.CeilToInt(growthInt * def.plant.maxMeshCount)` and
    /// `num2 = def.plant.visualSizeRange.LerpThroughRange(growthInt)`
    /// (<c>acs/RimWorld/Plant.cs</c> <c>Print</c>), and `Plant.Graphic` returns <c>GraphicSowing</c> whenever
    /// <c>LifeStage == Sowing</c>, which is <c>growthInt &lt; 0.0001</c>. None of the other fields in the frame's
    /// change tuple move after sowing, so without this slot the object was only ever sent once, as a seed, and
    /// every plot on the receiving screen stayed a bare field.</para>
    /// <para>Quantising is what keeps this affordable: a colony holds thousands of plants, and `growthInt` moves
    /// every tick. A 1/256 step is a size change large enough to see at 30 fps and bounds re-sends to about 256
    /// over a plant's whole life instead of one per frame.</para></summary>
    /// <summary>How big a fire has grown, quantised to 1/100 of the range the inspector shows, or -1 for a non-fire.
    ///
    /// <para>`Fire.fireSize` moves only in the 150-tick hash interval
    /// (<c>fireSize += 0.00055f * flammabilityMax * 150f * (1f - effectiveVacuumForFire)</c>, capped at 1.75) and is
    /// serialised by `Fire.ExposeData` as <c>"fireSize"</c>. None of the tuple's other slots move with it, so a fire
    /// was only ever sent once, at spawn, at its initial 0.1 - and everything read off that number stayed frozen:
    /// the inspector's <c>Burning (FireSize 10%)</c>, the heat it pushes into the room
    /// (<c>GenTemperature.PushHeat(Position, Map, fireSize * 160f)</c>), the spread interval, and the
    /// <c>fireSize &gt; 0.4f</c> test for whether it can ignite a neighbour. A blaze that visually grew on the
    /// authority kept reading as a fresh spark on the replica and never warmed the room it was in.</para>
    /// <para>One percent is the resolution the inspector prints, so a step that does not change the shown percentage
    /// is not worth a re-serialisation; a fire's whole life is then at most ~165 re-sends rather than one per frame.</para></summary>
    private static int FireSlot(Thing thing)
    {
        if (thing is not Fire fire) return -1;
        var slot = (int)Math.Round(fire.fireSize * 100.0, MidpointRounding.AwayFromZero);
        return slot < 0 ? 0 : slot > 175 ? 175 : slot;
    }

    /// <summary>How many ticks a projectile has left, quantised to a twelfth of its flight.
    ///
    /// <para>A fraction of `ticksToImpact` rather than the raw count, because the same slot has to serve a thrown rock
    /// (thirty ticks) and a mortar shell (minutes): a fixed quantum is one frame per tick for the slow one or no motion
    /// at all for the fast one. A twelfth is one visible step of travel for either.</para>
    ///
    /// <para>The denominator is `StartingTicksToImpact`, which is derived from `origin`, `destination` and the def's
    /// speed - all three identical on both peers - so the bucket is stable across the wire. `-1` means "not a
    /// projectile".</para></summary>
    /// <summary>How far a skyfaller has left to fall, in a twelfth of its descent.
    ///
    /// <para>The same defect as <see cref="FlightSlot"/> and the same fix, for a different object. A `Skyfaller`'s
    /// `Position` never moves - it is the cell the thing will land in - while `DrawPos` is
    /// `SkyfallerDrawPosUtility.DrawPos_Accelerate(base.DrawPos, ticksToImpact, ...)` (`:603256`). So the change tuple
    /// sees a constant position, a constant rotation and constant hit points for the entire descent, and a replica draws
    /// a pod frozen high above the ground until it lands.</para>
    ///
    /// <para>`Skyfaller.ExposeData` already carries `ticksToImpact`, `ageTicks` and `ticksToImpactMax` (`:603368`), so
    /// nothing new has to be invented for the payload - only something has to make the frame notice the change.</para>
    ///
    /// <para>The denominator is `ticksToImpactMax` rather than the remaining ticks, because a descent is short (220 ticks
    /// by default) and the visible travel is the whole point: twelve buckets is one visible step each.</para></summary>
    internal static int FallSlot(Thing thing)
    {
        if (thing is not Skyfaller faller) return -1;
        ticksToImpactField ??= HarmonyLib.AccessTools.Field(typeof(Skyfaller), "ticksToImpact");
        ticksToImpactMaxField ??= HarmonyLib.AccessTools.Field(typeof(Skyfaller), "ticksToImpactMax");
        if (ticksToImpactField?.GetValue(faller) is not int left) return -1;
        if (left <= 0) return 0;
        var total = ticksToImpactMaxField?.GetValue(faller) as int? ?? 0;
        if (total <= 0) return left;
        var fraction = left / (float)total;
        if (fraction > 1f) fraction = 1f;
        var bucket = (int)Math.Round(fraction * 12.0, MidpointRounding.AwayFromZero);
        return bucket < 0 ? 0 : bucket > 12 ? 12 : bucket;
    }

    private static System.Reflection.FieldInfo? ticksToImpactMaxField;

    private static System.Reflection.FieldInfo? ticksToImpactField;
    private static System.Reflection.FieldInfo? originField;
    private static System.Reflection.FieldInfo? destinationField;

    internal static int FlightSlot(Thing thing)
    {
        if (thing is not Projectile projectile) return -1;
        // `ticksToImpact` is `protected` on `Projectile` - the class treats it as subclass state, not public API - so it
        // is read by reflection, the same way `curToilIndex` and `MentalState.age` are read elsewhere in this project.
        ticksToImpactField ??= HarmonyLib.AccessTools.Field(typeof(Projectile), "ticksToImpact");
        if (ticksToImpactField?.GetValue(projectile) is not int left) return -1;
        if (left <= 0) return 0;
        // The span the whole flight covers, rebuilt from the two endpoints the Scribe fragment already carries. Both
        // `destination` and `origin` are protected, and `StartingTicksToImpact` - which is
        // `(origin - destination).magnitude / SpeedTilesPerTick` - needs them too, so the three are fetched by reflection
        // once and cached.
        originField ??= HarmonyLib.AccessTools.Field(typeof(Projectile), "origin");
        destinationField ??= HarmonyLib.AccessTools.Field(typeof(Projectile), "destination");
        if (originField?.GetValue(projectile) is not UnityEngine.Vector3 start
            || destinationField?.GetValue(projectile) is not UnityEngine.Vector3 end) return left;
        var span = (end - start).Yto0().magnitude / Math.Max(projectile.def.projectile.SpeedTilesPerTick, 0.001f);
        // Twelve buckets over the flight rather than a fixed quantum: the same slot serves a thirty-tick rock and a
        // minute-long mortar shell, and a fixed step would be one frame per tick for the slow one and no motion at all
        // for the fast one.
        var fraction = span <= 0f ? 1f : left / span;
        if (fraction > 1f) fraction = 1f;
        var bucket = (int)Math.Round(fraction * 12.0, MidpointRounding.AwayFromZero);
        return bucket < 0 ? 0 : bucket > 12 ? 12 : bucket;
    }

    private static int GrowthSlot(Thing thing)
    {
        if (thing is not RimWorld.Plant plant || plant.def?.plant is null) return -1;
        // Integer rounding keeps this file free of `Mathf`: the only thing that needs to be deterministic here is
        // the bucket, and `Math.Round` is exact for the range a clamped 0..1 growth produces.
        var slot = (int)Math.Round(plant.Growth * 256.0, MidpointRounding.AwayFromZero);
        return slot < 0 ? 0 : slot > 256 ? 256 : slot;
    }

    /// <summary>「存档点之后被销毁的物体」没有别的通道能告诉新副本：增量帧只在那一次转化时发 Removed。
    /// 留一个有限的滚动窗口，载入基线后的第一帧按客户端载入的 tick 补发。</summary>
    private static readonly List<(int Id, int Tick)> recentRemovals = new();
    private const int RemovalWindowTicks = 12_000;
    private const int RemovalCapacity = 8192;

    private static void NoteRemoval(int id)
    {
        var tick = Find.TickManager?.TicksGame ?? 0;
        recentRemovals.Add((id, tick));
        if (recentRemovals.Count > RemovalCapacity) recentRemovals.RemoveRange(0, recentRemovals.Count - RemovalCapacity);
    }

    private static List<int>? RemovalsAfter(int tick)
    {
        List<int>? ids = null;
        var floor = tick - RemovalWindowTicks;
        foreach (var (id, at) in recentRemovals)
            if (at > tick && at >= floor) (ids ??= []).Add(id);
        return ids;
    }

    private static bool baselineReady;

    /// <summary>Clears every channel's "what I already sent" bookkeeping, so the next frame is a first frame again.
    ///
    /// <para><b>Why this has to exist.</b> Twenty-one channels carry a <c>Reset</c> for exactly this, and before this method
    /// only two of them were ever called (from the disconnect path). The failure is silent and severe: a channel remembers the
    /// last state it sent, so after a world switch - or a replica reconnecting to a different save - the new session's first
    /// frame omits everything it believes is already known, and the replica keeps the previous world's buildings, doors,
    /// zones, snow, plans, research and quests. Rows that are "change-only" never send their baseline at all.</para>
    ///
    /// <para><b>Why every channel, by reflection.</b> Listing thirty names here is thirty chances to forget one, and the
    /// omission has no symptom until somebody switches worlds. Any type with an <c>internal static void Reset()</c> is a
    /// channel by construction, so the set is discovered rather than maintained.</para></summary>
    internal static void ResetChannels()
    {
        var names = new List<string>();
        foreach (var type in typeof(HeadlessRuntime).Assembly.GetTypes())
        {
            // `*Sync` by name, not "anything with a Reset". Several non-channel types carry a parameterless static `Reset`
            // that is emphatically not a baseline reset: `SharedColony.Reset()` calls `Session.Leave()` and cancels the tick
            // guard - invoked from `ScheduleWorldLoaded` (phase `Synchronizing`) it tears down the load it was called from,
            // and the other hits are probes and gates whose state is per-investigation, not per-world. Reflection on the
            // suffix is what makes the set self-maintaining without that risk.
            if (!type.Name.EndsWith("Sync", StringComparison.Ordinal)) continue;
            var method = type.GetMethod("Reset", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                Type.EmptyTypes);
            if (method is null || method.ReturnType != typeof(void)) continue;
            try
            {
                method.Invoke(null, null);
                names.Add(type.Name);
            }
            catch (Exception error)
            {
                Log.Warning($"[TurboTick] {type.Name}.Reset 失败：{error.GetType().Name}: {error.Message}");
            }
        }
        baselineReady = false;
        // Named, because "the channels were cleared" and "the channels were cleared and there are thirty of them" are
        // different claims and only the second one rules out a reflection filter that silently matched nothing.
        names.Sort(StringComparer.Ordinal);
        // The claim is "every Sync channel was reset", and it is only true if the list is non-empty and contains nothing
        // else. Both halves are asserted here rather than in `Tests`, which cannot see this assembly.
        var suspicious = names.Where(name => !name.EndsWith("Sync", StringComparison.Ordinal)).ToArray();
        if (suspicious.Length > 0)
            Log.Error("[TurboTick headless] 通道清空扫到了非通道类型，请检查过滤器：" + string.Join(", ", suspicious));
        if (names.Count == 0)
            Log.Error("[TurboTick headless] 通道清空扫到 0 个 —— 过滤器没匹配到任何通道，下一帧不会重发首帧。");
        Log.Message($"[TurboTick headless] 通道基线已清空：{names.Count} 个，下一帧重新发首帧：{string.Join(", ", names)}");
    }

    /// <summary>Every peer simulates; the server paces ticks and orders commands. Off unless asked for,
    /// because the authoritative display path is the one that has been through acceptance.</summary>
    internal static readonly bool Lockstep =
        string.Equals(Environment.GetEnvironmentVariable("TURBOTICK_LOCKSTEP"), "1", StringComparison.Ordinal);

    internal static void PublishFrame()
    {
        if (server is null || !SharedColony.Gating || Lockstep) return;
        // The world can be absent on purpose now: stopping a world unloads it while the process (and this Update
        // loop) keeps running. Without this guard `Find.Maps` dereferences a null game and Verse logs
        // "Root_Play.Update threw: NullReferenceException" every frame - measured, and it buries the real logs.
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null) return;
        var tick = Find.TickManager?.TicksGame ?? -1;
        if (tick <= lastPublishedTick || tick % FrameInterval != 0) return;
        lastPublishedTick = tick;
        try
        {
            var pawns = new List<ReplicaPawn>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    // World position, not the integer cell: the replica interpolates between frames and a
                    // cell-granular value would step one square at a time.
                    var pos = pawn.DrawPos;
                    pawns.Add(new ReplicaPawn(pawn.thingIDNumber, map.uniqueID, pos.x, pos.z,
                        pawn.Rotation.AsInt, pawn.CurJobDef?.defName,
                        pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f,
                        EncodeCell(pawn.CurJob?.targetA), pawn.CurJob?.targetA.Thing?.thingIDNumber ?? -1,
                        EncodeCell(pawn.CurJob?.targetB), pawn.CurJob?.targetB.Thing?.thingIDNumber ?? -1,
            pawn.jobs?.curDriver?.CurToilIndex ?? -1,
            pawn.needs?.mood?.CurLevel ?? -1f,
            pawn.needs?.food?.CurLevel ?? -1f,
            pawn.needs?.rest?.CurLevel ?? -1f,
            EncodeThoughts(pawn),
            EncodeCell(pawn.CurJob?.targetC), pawn.CurJob?.targetC.Thing?.thingIDNumber ?? -1,
            pawn.CurJob?.count ?? -1,
            pawn.CurJob?.bill?.GetUniqueLoadID(),
            EncodeQueue(pawn.CurJob?.targetQueueA), EncodeQueue(pawn.CurJob?.targetQueueB),
                        pawn.carryTracker?.CarriedThing?.def?.defName ?? null,
                        pawn.carryTracker?.CarriedThing?.stackCount ?? -1,
                        (int)(pawn.mindState?.lastJobTag ?? JobTag.Misc),
                        // The renderer's rotation comes from this, not from `Rotation` alone: a pawn whose posture is
                        // not `Standing` is drawn with `LayingFacing().AsAngle`, i.e. 0/90/270 degrees
                        // (`Verse/PawnRenderer.cs:342` and `BodyAngle`). Sent as the raw enum value so the replica
                        // can mirror it exactly instead of re-deriving it from a job it never ran.
                        (int)pawn.GetPosture(),
                        // Skills. `Learn` is only called from work toils, which a replica never runs, so every
                        // colonist's skill page would show the level the session started with.
                        SkillSync.Encode(pawn),
                        // Every need the card would show. The three fixed numbers above cover a base-game colonist; joy,
                        // comfort, beauty, drug desire and any DLC or mod need are frozen without this.
                        NeedsSync.Encode(pawn),
                        // Last on purpose: the record's parameters are positional, so a field added anywhere but the
                        // end silently shifts every argument after it. `FactionLoadId` and `HealthState` are declared
                        // last in `ReplicaPawn` and passed last here, in that order.
                        pawn.Faction?.loadID ?? -1,
                        // `Pawn_HealthTracker.healthState` as the raw enum. A replica never runs `HealthTrackerTick`,
                        // so without this the value is whatever the save was loaded with - and `Pawn.Dead`/`Pawn.Downed`
                        // read it (`:127019`/`:126989`), which is what gates the entire action row in
                        // `CharacterCardUtility.DrawCharacterCard` (`:642497`) and the downed check in front of the
                        // banish dialog (`:642510`).
                        pawn.health is null ? -1 : (int)pawn.health.State));
                }
            // 刚载入完基线的副本要一份「存档点 -> 载入完成」的间隙补发：帧是增量，这段时间新建的物体
            // 已经错过了唯一一次 Created。画廊按 id 水位收，销毁按 tick 窗口收，两者都只发给那个 peer。
            var pending = server.TakeCatchUps();
            List<ReplicaCreation>? catchUpCreations = null;
            Dictionary<long, DedicatedServer.FrameCatchUp>? catchUps = null;
            if (pending.Length > 0)
            {
                catchUpCreations = [];
                catchUps = new Dictionary<long, DedicatedServer.FrameCatchUp>(pending.Length);
                foreach (var request in pending)
                    catchUps[request.PeerId] = new DedicatedServer.FrameCatchUp(catchUpCreations, RemovalsAfter(request.FromTick));
                Log.Message($"[TurboTick headless] 基线补发：{pending.Length} 个副本从 id 水位 "
                    + $"{pending.Min(r => r.FromThingId)} / tick {pending.Min(r => r.FromTick)} 起补齐间隙。");
            }
            var (changed, removed, created) = DiffThings(catchUpCreations, pending.Length > 0 ? pending.Min(r => r.FromThingId) : int.MaxValue);
            // Zones travel on their own channel: they have no `thingIDNumber` and no cell in the thing grid, so the
            // diff above cannot see them. Without this a stockpile the player draws after the snapshot never reaches
            // any replica and the ground shows no colour at all.
            var (zones, zonesRemoved) = ZoneSync.Publish();
            // Letters and messages. Vanilla raises both inside `LetterStack.ReceiveLetter` / `Messages.Message`, which
            // only ever run from a tick - and a replica runs none, so it shows a colony under attack with no letter and
            // no red line. This is the only channel by which a player learns that anything happened.
            var (letters, messages) = LetterSync.Publish();
            // Terrain travels on the same dirty-cell rule. A map is 62 500 cells and terrain moves a few dozen times
            // an hour, so this is a hook-maintained set of touched cells, not a diff of the grids.
            var (terrainTop, terrainUnder, terrainFoundation) = TerrainSync.Publish();
            // What each pawn is holding and wearing. Lives in the pawn's own trackers, so nothing else can carry it.
            var gear = ApparelSync.Publish();
            // Door open state. `openInt` is moved by the door's own Tick, which only the authority runs.
            var doors = DoorSync.Publish();
            // Roofs. Not a Thing - `RoofGrid` is one flat array - so the thing enumeration can never carry them.
            var roofs = RoofSync.Publish();
            // Snow depth. `SnowGrid.depthGrid` is one float per cell, filled by weather on a tick only the authority
            // runs. The first publish carries every cell that has snow; after that only cells that moved.
            var snow = SnowSync.Publish();
            // Fog of war. Like snow, the first publish carries the whole state: a save has fog on thousands of cells
            // that no hook ever reported.
            var fog = FogSync.Publish();
            // Mental breaks. Change-only: a breakdown is a few times a season, and a per-frame field would cost a string
            // per pawn per frame to describe a state that almost never moves.
            var mental = MentalStateSync.Publish();
            // Inspirations. Also change-only: an inspiration is a few times a season per colonist at best.
            var inspirations = InspirationSync.Publish();
            // Guest / prisoner status and the recruit bar. Change-only: a status changes a handful of times a day at most.
            var guests = GuestSync.Publish();
            // The settlement table. A whole-state copy, because a replica whose grants differ from the authority's would
            // disagree about whether an action is legal.
            var settlement = SettlementSync.Publish();
            // Planning marks. Not Things - they live in `PlanManager.allPlans`, so the thing enumeration never carries them.
            // Every map, not just `Find.CurrentMap`: each map owns its own `PlanManager` (`:86138-86140`), and a
            // multi-settlement colony keeps plans on several. Publishing only the current one meant that switching the
            // authority's view silently stopped syncing the other maps' plans - and the payload now carries the map index,
            // so the receiver puts each plan back where it came from.
            List<Plan>? allPlans = null;
            foreach (var planMap in Find.Maps)
                if (planMap?.planManager?.AllPlans is { Count: > 0 } mapPlans)
                    (allPlans ??= []).AddRange(mapPlans);
            var plans = PlanSync.Publish(allPlans);
            // Flight state. `Pawn_FlightTracker.FlightTick` (`:729158-729207`) is the only writer of all five fields and it
            // runs from `Pawn.Tick`, which a replica never runs - so a flying pawn is drawn walking, one that took off is
            // drawn hovering forever, and the ability's cooldown never reaches zero on the replica.
            var flights = FlightSync.Publish();
            // Research progress. Accumulated by work toils a replica never runs, so the tab would show the load-time
            // percentage forever.
            var research = ResearchSync.Publish();
            // Indoor temperature. `RoomTempTracker.temperatureInt` only moves inside `EqualizeTemperature`, which
            // `MapTemperatureTick` calls every 120 ticks - ticks a replica never runs. Outdoor rooms need nothing: their
            // value is a computed getter over the world tile temperature, which follows `absTick`.
            var roomTemps = RoomTempSync.Publish();
            // Plant growth. `growthInt` only advances in `TickLong`, and the object scan above skips an object whose
            // position/rotation/stack/hitpoints did not change - which is exactly a growing plant.
            var plants = PlantSync.Publish();
            // Faction standing. Goodwill is written from a dozen unrelated call sites, so instead of hooking each one this
            // recomputes the whole (tiny) table and sends only what differs - a missed writer is a stale number nobody
            // notices, while a hundred recomputed comparisons cannot miss one.
            var factions = FactionSync.Publish();
            // Which ideoligion each faction believes. Separate from the ideoligion table above (names/colours/points)
            // because it answers a different question: not "what ideoligions exist" but "which one is this faction's".
            var factionIdeos = FactionSync.PublishIdeos();
            var factionCooldowns = FactionSync.PublishCooldowns();
            var historyEvents = HistorySync.Publish();
            // Auto-slaughter limits, per map and species. The dialog's number cells write them and only dirty a cache
            // (`:625940-625943`), so without this a cap typed on a replica never reaches the side that kills animals.
            var autoSlaughter = AutoSlaughterSync.Publish();
            // Which maps exist. Every other map-keyed channel drops what it cannot resolve, so without this a map the
            // authority generated after the save was loaded is absent on the replica rather than merely stale.
            var maps = MapLifecycleSync.Publish();
            // Whether the whole game is paused for a gravship landing confirmation. `TickManager.ForcePaused` (`:52680`)
            // and `PlayerCanControl` (`:52696`) both read it on every peer, so without this the authority freezes and
            // the replicas keep ticking.
            var gravshipGate = GravshipGateSync.Publish();
            // Where world objects sit. They live in `Find.WorldObjects` and move on their own interval ticks, so a caravan
            // stays on the tile the save was loaded with unless something re-sends it.
            var worldObjects = WorldObjectSync.Publish();
            // World objects that did not exist when the save was loaded, and ones the authority destroyed. Both
            // position and state channels look their object up by `ID`, so neither can create or remove one - the same
            // shape as a map's id having no channel before `MapLifecycleSync`.
            var (worldObjectCreations, worldObjectsRemoved) = WorldObjectLifecycleSync.Publish();
            // Quest standing. Accepting and ending both happen on the authority, and `Quest.State` is a getter over the
            // fields they write - so the inputs travel and the replica re-derives the same answer.
            var quests = QuestSync.Publish();
            // Memory ages. `age += 150` in `Thought_Memory.ThoughtInterval` is the only writer, and `ShouldDiscard` compares
            // it against the duration - so a replica keeps every thought forever.
            var memories = MemorySync.Publish();
            // The story log. `TaleRecorder.RecordTale` only runs here, and `TaleManager.Add` is what makes a tale visible to
            // the history page and to art generation. Measured 4 tales, so the list travels whole.
            var tales = TaleSync.Publish();
            // Two facts the replica cannot derive and has no channel for: object identity (the host is the only
            // allocator, so its counter is what the replica's must equal) and the current weather (decided on a
            // tick the replica never runs). Both are a handful of bytes per frame.
            List<ReplicaWeather>? weather = null;
            foreach (var map in Find.Maps)
            {
                var manager = map.weatherManager;
                if (manager?.curWeather?.defName is not { Length: > 0 } weatherName) continue;
                (weather ??= []).Add(new ReplicaWeather(map.uniqueID, weatherName,
                    manager.lastWeather?.defName, manager.curWeatherAge));
            }
            // Conditions. Unlike `pawns`, this is a change-only channel: `lastHediffs` remembers the encoded string
            // per pawn and only a difference produces an entry. A cured pawn sends an empty list, which is a real
            // version bump rather than "nothing to say" - see `ReplicaHediffSet.Version`.
            List<ReplicaHediffSet>? hediffs = null;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var id = pawn.thingIDNumber;
                    var encoded = EncodeHediffs(pawn);
                    if (lastHediffs.TryGetValue(id, out var previousHediffs) && previousHediffs == encoded) continue;
                    lastHediffs[id] = encoded;
                    hediffs ??= [];
                    hediffs.Add(encoded is null
                        ? ReplicaHediffSet.Empty(id, ++hediffVersion)
                        : new ReplicaHediffSet(id, ++hediffVersion, encoded.Split(Convert.ToChar(59))));
                }
        var toilProgress = ToilProgressSync.Publish();
        // Custom progress bars divide a counter the driver owns, and no payload carries one: `JobDriver.ExposeData`
        // writes ten base fields and not one of the twenty subclass counters a `WithProgressBar` getter reads.
        // The generic toil-delay bar is covered by the channel above; this is the other family.
        var drivers = DriverStateSync.Publish();
        // A sanguophage's deathrest percentage is `TickDeathresting`'s counter, and the health tab prints it
        // directly - a replica that never runs that tick shows the value the save was loaded with forever.
        var deathrest = DeathrestSync.Publish();
        // Busy stance, stun and stagger. A replica's stance tracker never leaves `Stance_Mobile`, and the renderer reads
        // that cast to decide whether to aim the weapon or face the target.
        var stance = StanceSync.Publish();
        // Lord counters. `LordManagerTick` is inside `MapPostTick`, which a display replica never reaches, so
        // `ticksInToil` and the ritual progress sum stay at the loaded value.
        var lords = LordSync.Publish();
        // Quest parts. `Quest.QuestTick` is reached only from `QuestManagerTick`, which sits inside `MapPostTick`,
        // so every counter inside a quest part stays at the value the save was loaded with.
        var questParts = QuestPartSync.Publish();
        // World object state. A world object is not a map thing, so none of the four sweeps visit it and nothing but its tile
        // has ever been sent. Measured before building this: a settlement's own fragment carries its raid countdowns inline.
        var worldObjectStates = WorldObjectStateSync.Publish();
        // Pawn sub-trackers: guilt, training decay, learning desire and flight. Each is advanced only from the
        // pawn's own `TickInterval`, which a replica never runs.
        var pawnTrackers = PawnTrackerSync.Publish();
        var hediffTimers = HediffTimerSync.Publish();
        // Ability charges and cooldowns. `AuthoritativeFrame.PawnState` would carry them - `Pawn_AbilityTracker.ExposeData`
        // scribes the whole list - but that parameter is passed as null below, so a pawn's own fragment never arrives and
        // every subsystem needs its own channel. A replica's abilities therefore keep the counts they were loaded with.
        var abilityCharges = AbilitySync.Publish();
        // Storyteller adaptation. Both counters grow in `StoryWatcherTick`, inside `MapPostTick`, and `adaptDays` scales
        // every future raid budget (`:391870`) - so a replica's hostiles arrive at the wrong size, not just the wrong number.
        var adaptation = AdaptationSync.Publish();
        // The colony's death countdown. `GameEnder.GameEndTick` is the only writer and it sits in the game tick, so a
        // replica never learns that the colony ended - the map simply empties and nothing says so.
        var gameEnder = GameEnderSync.Publish();
        // Orbiting ships' remaining comms time. They live in `Map.passingShipManager`, which no sweep visits, and they
        // delete themselves only from `PassingShipTick` - so a replica keeps a departed ship in the comms contact list.
        var passingShips = PassingShipSync.Publish();
        // The counters left after every subsystem with a channel of its own: a pawn's drug records, an ideoligion's reform
        // count, a growth letter's trait options, a ritual toil's wait step, a corpse tracker's research. One channel
        // because they share one shape and none of them is worth a frame of its own.
        var residualCounters = ResidualCounterSync.Publish();
        // A pawn's ideoligion. `Pawn_IdeoTracker.ExposeData` scribes it, but it is reached through `Pawn.ExposeData` and
        // `PawnState` is `null`, so nothing arrives: a replica keeps whatever belief the save was loaded with, while the
        // authority's copy converts, drifts in certainty and changes role holders.
        var ideos = IdeoSync.Publish();
        // Whether a thing kind is unlocked and whether a codex entry is discovered. Both change from spawn and
        // visibility events, which only the authority generates, so a replica keeps the save's answer forever.
        var discoveries = DiscoverySync.Publish();
        // The three `PlaySettings` fields that change behaviour rather than drawing. `useWorkPriorities` is the one that
        // matters: `Pawn_WorkSettings.GetPriority` (`:331073`) returns 3 for every non-zero priority when it is off, so
        // two peers that disagree about the switch read the same stored number as two different orders of work.
        var playSettings = PlaySettingsSync.Publish();
        // The bossgroup summon cooldown. One int, and it is the only thing standing between a replica's gizmo and the
        // host's two-day cooldown - see `BossgroupSync` for why the click would otherwise really fire.
        var bossgroup = BossgroupSync.Publish();
        // Areas last: `SettlementAreas.Rebuild` adds and removes `Area_Allowed` entries on the same manager, and running
        // the built-in grid pass after it means the two do not fight over the list during one frame.
        var areas = AreaSync.Publish();
        // The player-created allowed areas, which `AreaSync` does not cover: it keys on type, and these have none.
        // Read after `AreaSync` for the same reason - both walk the same manager's list.
        var allowedAreas = AreaAllowedSync.Publish();
        var conditions = GameConditionSync.Publish(out var conditionsRemoved);
        var ages = PawnAgeSync.Publish();
        // Traits reach a replica once, from the save file, and never again: `Pawn.ExposeData` scribes `story` deep
        // (`:131390`) but `PawnState` is null, so a trait the authority grants or removes afterwards stays invisible -
        // and `Pawn_StoryTracker.DisabledWorkTags` (`:460027-460031`) turns that list into which work types the pawn may
        // take. Read after `ages` because both walk `mapPawns.AllPawnsSpawned`.
        var traits = PawnTraitSync.Publish();
        // Royalty: titles and the permits the player bought. `Pawn_RoyaltyTracker.ExposeData` (`:475905`) would carry
        // all of it but `PawnState` is null, so `CharacterCardUtility`, `PermitsCardUtility` and every
        // `RoyalTitlePermitWorker_*` read the state the save was loaded with. Read after traits because a title change
        // and a trait change never interact, but this keeps the pawn-level channels together.
        var royalty = RoyaltySync.Publish();
        // Genes are the other half of the same stat sum, and they drive traits through `forcedTraits`/`suppressedTraits`.
        // Read after traits so a gene-forced trait's `sourceGene` is already attached when the suppression pass runs.
        var genes = GeneSync.Publish();
        // Precepts last of the pawn-affecting groups: a precept's role assignment reads the pawn list, and `AddPrecept`
        // sorts the ideoligion's list with `RecachePrecepts`, so it wants the ideo table already applied.
        var precepts = PreceptSync.Publish();
        // Names are a separate row set from the table above (round 147): the table says which precepts exist and
        // who holds a role, this says what each one is called and whether the player locked that name. They move on
        // different edits, so one channel would re-send the other's unchanged half.
        var preceptNames = PreceptSync.PublishNames();
        var preceptPayloads = PreceptSync.PublishPayloads();
        var anomalyDifficulty = DifficultySync.Publish();
        var corpseTracker = CorpseTrackerSync.Publish();
        // Visual-only Ethereal carriers: see `EtherealVisualSync` for why the thing sweep must keep skipping this
        // category and why these three classes are the exception.
        var (livingEffects, livingEffectsRemoved) = EtherealVisualSync.Publish();
        var policies = PolicyTableSync.Publish();
        var factionCreations = FactionCreationSync.Publish();
        var breaking = MentalBreakerSync.Publish();
        var immunity = ImmunitySync.Publish();
        // `PawnState` stays null, and that is a measurement rather than an omission: `pawn-payload-size` reports 71
        // pawns at 636386 B of Scribe fragment - 8963 B each, 224 times the per-tick position frame (2840 B). A whole
        // fragment can therefore only be sent when something *changes*, and nothing here decides that per pawn; sending
        // it on any poll instead would multiply the frame by two orders of magnitude. The named channels (hediff timers,
        // abilities, trackers, needs) exist for exactly the fields that would otherwise be stuck at their load-time value,
        // and each of them sends only the numbers that move.
        var frame = new AuthoritativeFrame(tick, pawns, changed, removed, null, created,
            SharedActions.NextThingId(), weather, zones, zonesRemoved, hediffs, letters, messages,
            terrainTop, terrainUnder, terrainFoundation, gear, doors, roofs, snow, fog, mental, inspirations, guests, settlement,
            plans, research, roomTemps, plants, factions, worldObjects, quests, memories, tales, toilProgress, hediffTimers, conditions, conditionsRemoved, ages, breaking, immunity, drivers, deathrest, stance, lords, questParts, pawnTrackers,
            worldObjectStates, abilityCharges, adaptation, gameEnder, passingShips, residualCounters, ideos, discoveries,
            playSettings, areas, allowedAreas, traits, royalty, genes, precepts, flights, factionIdeos,
            worldObjectCreations, worldObjectsRemoved, gravshipGate, maps, factionCooldowns, historyEvents,
                autoSlaughter, bossgroup, preceptNames, preceptPayloads, anomalyDifficulty, policies, factionCreations,
                corpseTracker, livingEffects, livingEffectsRemoved);
        // Kept so `WireRoundTripProbe` can round-trip the frame that really went out. Rebuilding one for the probe would
        // advance every channel's "already sent" record and turn the next real frame into a delta against a frame nobody
        // received - the probe would break the thing it measures.
        LastPublishedFrame = frame;
        var sent = server.BroadcastFrame(frame, catchUps);
            Interlocked.Add(ref framesSent, sent);
            Volatile.Write(ref lastFrameTick, tick);
            if (sent > 0 && (lastFrameLogTick < 0 || tick - lastFrameLogTick >= 600))
            {
                lastFrameLogTick = tick;
                // 只在日志点序列化一次，不进入每帧热路径。
            var frameKb = 0.0;
            var frameKbBrotli = 0.0;
            try
            {
                var raw = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frame);
                frameKb = raw.Length / 1024.0;
                // 同一份负载压一次，只为了回答「压缩值不值得改传输层」。不改协议、不发热路径。
                using var buffer = new System.IO.MemoryStream();
                using (var brotli = new System.IO.Compression.BrotliStream(buffer,
                    System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    brotli.Write(raw, 0, raw.Length);
                frameKbBrotli = buffer.Length / 1024.0;
            }
            catch (Exception) { }
            Log.Message($"[TurboTick headless] 权威帧 tick={tick}：{pawns.Count} 个角色 -> {sent} 个显示副本。"
                + $"一帧约 {frameKb:0.0} KB / Brotli 后 {frameKbBrotli:0.0} KB（{sent} 个副本**实际**合计 {frameKbBrotli * sent:0.0} KB/帧，按 60 帧/秒约 {frameKbBrotli * sent * 60 / 1024.0:0.0} MB/s；未压缩则为 {frameKb * sent * 60 / 1024.0:0.0} MB/s）。线缆实测 {WireStats.Reset()}。{SweepSummary()}；{DesignatorSync.Summary()}。"
                + $"本端蓝图 {blueprintsInWorld} 个 / 累计作为新建发出 {blueprintsSent} 个");
            }
        }
        catch (Exception error) { Log.Warning("[TurboTick headless] frame publish: " + error.Message); }
    }

    internal static void Update()
    {
        if (!Active) return;
        // A dialog nobody can dismiss must not be able to pause the colony. The Harmony patch on WindowStack.Add
        // catches new windows; this sweep covers the ones that were already open, or that set forcePause after
        // adding themselves. Measured: serverTick frozen at 7 with tps=0 while forcePauseWindows was
        // ["Verse.Dialog_MessageBox"] - Dialog_MessageBox's constructor sets forcePause unconditionally.
        HeadlessWindowGuard.Enforce();
        // Answers queued from the administration page (and announcements with no callback) are applied here, on the
        // game thread, because the callbacks belong to the mod that opened the dialog.
        // Guarded per hook: this runs once per frame, and a hook that throws takes the whole update with it. Measured
        // the cost of not guarding: a probe that read a null TickManager on the title screen wrote a stack trace
        // every frame and grew game-stdout.log to 1.78 GB in about two minutes.
        Guard("对话框队列", HeadlessDialogQueue.Pump);
        // The measurement for the line above: a real Dialog_MessageBox with real callbacks, so "the world stops and
        // the page can answer it" is observed rather than assumed. Off unless TURBOTICK_PROBE_DIALOG is set.
        Guard("对话框探针", DialogProbe.Tick);
        // The forbid flag is the one field whose repair path was never observed, only argued: it rides in the frame
        // tuple, but nothing measured that the receiver applies it. Guarded like every other hook because it runs
        // every frame and a throw takes the whole update with it.
        Guard("禁止探针", ForbidProbe.Tick);
        // The replica-side half of the same measurement. Both entry points are needed because `TurboGameComponent`
        // returns early for a headless process while this pump only runs for one: a GUI replica reaches the probe
        // through the component, a `-headless` participant through here. The probe itself ignores the authority.
        Guard("解除探针", ForbidProbe.TickReplica);
        // The blueprint path had the same shape as the forbid flag before it was fixed: the command arrived, the
        // authority logged taking it, nothing threw, and the world stayed empty. This probe sends the same command the
        // client sends, from a known tick, so the per-cell answers in `设计器重放` can be read without a human clicking.
        Guard("蓝图探针", BlueprintProbe.Tick);
        // 52 frames standing with wood beside them and three colonists idle is not a sync defect until it is proven
        // to be one: the probe reads vanilla's own refusal reason (`JobFailReason.Reason`) per frame per colonist, so
        // "no giver offered a job" and "a giver refused this frame" stop looking the same in the log.
        Guard("建造探针", ConstructionProbe.Tick);
        // The bill path is the one the frame protocol just grew fields for (`targetQueueB`, `bill`, `count`), and
        // the lab colony has no workbench at all - every reading was 0 for a reason unrelated to the sync. This
        // builds a stonecutter's table with chunks and a Forever bill so `WorkGiver_DoBill` produces the real job.
        Guard("配方探针", BillProbe.Tick);
        // A participant has no listener and no status file, so without this a stalled acceptance run is
        // invisible: the process keeps burning CPU and the log simply stops. One line every five seconds
        // answers "which phase is it stuck in" without a debugger.
        if (!ServerMode) ReportParticipant();
        while (pending.TryDequeue(out var action))
            try { action(); } catch (Exception error) { Fail("Game thread", error); }
        // `RoomConnection` posts its game-thread work through its own queue, drained by `Pump()`. Without a GUI that
        // pump only ran from the settings window and the multiplayer menu, so in headless mode nothing was ever
        // drained: `Busy` stayed true from the first `Connect`, and every later connect was refused for the life of
        // the process. Measured: after a stop, restarting a world sat in `restoring` forever with the log saying
        // only "还没连上房间（未连接）".
        //
        // Guarded to a settled world on purpose: those actions assume a live game and a mounted play scene, and
        // draining them while a save is mid-load or a world has been stopped is how a native crash gets invited
        // (measured: exit 139 while a restart and its reconnect overlapped).
        if (Current.ProgramState == ProgramState.Playing && Current.Game is not null
            && !worldStopped && Scribe.mode == LoadSaveMode.Inactive && !LongEventHandler.AnyEventNowOrWaiting)
        {
            try { RoomConnection.Pump(); }
            catch (Exception error) { Log.Warning("[TurboTick headless] RoomConnection: " + error.Message); }
        }
        // The portal's inbox: commands arriving as files, because the listener lives in another process.
        // Guarded because a broken inbox must never be able to stop the authority.
        try { PortalCommands.Tick(); } catch (Exception error) { Log.Warning("[TurboTick portal] " + error.Message); }
        // Commands this authority relayed have no other executor. The origin cancelled its own assignment (that
        // is what the capture prefix does) and the relay deliberately skips the origin, so in a room with one
        // display replica a forbid/unforbid would be accepted, logged, counted and then applied by nobody.
        // Fed through the same entry point a peer uses, so the late-command accounting applies here too.
        if (server is not null && SharedColony.Gating)
        {
            var relayed = server.TakeAuthorityCommands();
            if (relayed.Length > 0)
            {
                try
                {
                    SharedColony.OnCommands(relayed);
                    // Printed unconditionally and kept short: this path runs a few times a minute, and without it
                    // "the authority took the command" and "the authority never saw it" look identical from the
                    // outside - both leave the world unchanged and the counter `commands=k/n` already satisfied.
                    Log.Message($"[TurboTick headless] 权威端取到 {relayed.Length} 条待执行命令："
                        + string.Join(",", relayed.Select(c => c.Kind + "#" + c.Sequence + "@" + c.Tick))
                        + $"，本端已应用 {SharedColony.Session.Barrier.AppliedTick}");
                }
                catch (Exception error) { Log.Warning("[TurboTick headless] 权威端应用命令失败：" + error.Message); }
            }
        }
        if (shutdown?.IsCompleted == true)
        {
            if (shutdown.IsFaulted) { failure = "Shutdown persistence failed"; exitCode = 1; Log.Error("[TurboTick headless] " + shutdown.Exception); }
            FinishExit();
            return;
        }
        if (server is null)
        {
            if (Volatile.Read(ref stopRequested) != 0 && startup?.IsCompleted != false) FinishExit();
            return;
        }
        if (stopping) return;
        PublishFrame();
        // The load funnel is deliberately *outside* the "session connected" branch.
        //
        // Measured: stopping a world ends its room session (it must - the room no longer has a world), and the
        // funnel used to live inside `if (RoomConnection.Client?.IsConnected == true)`. So after a stop, the next
        // `start` reported Accepted and never loaded anything, because the branch that would have loaded it was
        // waiting for a connection that only comes from a load. Loading a save is the authority's own operation; it
        // reconnects on the next pass.
        if (ResidentSimulation && !server.GameRunning && initialSave is not null && !initialLoadRequested
            && !RoomConnection.Busy && !LongEventHandler.AnyEventNowOrWaiting
            && !worldStopped)
        {
            initialLoadRequested = true;
            Log.Message("[TurboTick headless] 漏斗取件 " + initialSave);
            // The load gate. A save whose mods this server does not have must not be loaded at all: doing so
            // drops every def those mods contributed and the world is silently incomplete. Measured: without
            // this gate the process reached state=playing on exactly such a save, so the check has to be
            // here - at the one place a world is loaded - and not only in the page.
            if (PortalCommands.SaveModsRefusal is { } refusal)
            {
                saveModsBlocked = true;
                state = "failed";
                failure = "Save mods: " + refusal;
                Log.Error("[TurboTick headless] 拒绝载入 " + initialSave + "：" + refusal
                    + "（进程保持运行以便管理页显示原因；补齐 mod 后重启即可）");
                WriteStatus();
                // Not Fail(): that sets the stop flag and exits, and the restart policy turns that into an
                // endless crash loop in which the operator only ever sees "starting".
                return;
            }
            if (!File.Exists(GenFilePaths.FilePathForSavedGame(initialSave)))
                Fail("Initial save", new FileNotFoundException("Configured save does not exist under Saves/."));
            else
            {
                // A load is a new world: the "stopped" marker belongs to the one being replaced.
                ClearWorldStopped();
                Log.Message("[TurboTick headless] 载入存档 " + initialSave + "（检查点已提交，开始读盘）");
                GameDataSaveLoader.LoadGame(initialSave);
            }
        }
        if (RoomConnection.Client?.IsConnected == true)
        {
            attached = true;
        }
        else if (!RoomConnection.Busy && Clock.Now >= nextConnect && !worldStopped)
        {
            // The 60-second attach deadline used to be fatal. It is now a warning, and that is a deliberate
            // weakening: measured twice, a world could be stopped and started again from the page and the re-attach
            // legitimately took longer than a minute (the whole point of stopping is that the room session ended).
            // Exiting the process there is exactly the bug this change set exists to remove - the page goes away and
            // the operator can never see why. `state` already says "not attached"; a stall is visible and harmless,
            // a dead listener is neither.
            if (!attached && Clock.Now - startedAt > 60) AttachStallWarned();
            else ConnectSimulation();
        }
        ReportProgress();
        var phase = SharedColony.Session.Phase;
        if (phase == SharedPhase.Faulted)
        {
            state = "faulted";
            // Never run a local fallback or publish a divergent world after a failed load/hash.
            failure = SharedColony.Session.LastError;
        }
        else if (server.GameRunning) state = phase == SharedPhase.Playing ? "playing" : "restoring";
        else state = saveModsBlocked ? "failed" : initialLoadRequested ? "loading-initial-save" : "lobby";
        // A stopped world keeps its Game object (the engine requires one) but must report as lobby, otherwise the
        // page says "playing" while nothing advances. This is checked *after* the line above because a stop also
        // clears `initialLoadRequested`, and the two facts together used to read as "loading" forever.
        if (worldStopped && !saveModsBlocked) state = "lobby";
        if (Clock.Now >= nextStatus)
        {
            nextStatus = Clock.Now + 2;
            WriteStatus();
        }
        if (Clock.Now >= nextPersist && persistence?.IsCompleted != false)
        {
            if (persistence?.IsFaulted == true) Fail("Periodic persistence", persistence.Exception!);
            nextPersist = Clock.Now + 10;
            persistence = server.PersistAsync();
        }
        if (Volatile.Read(ref stopRequested) != 0 && !RoomConnection.CheckpointBusy && !LongEventHandler.AnyEventNowOrWaiting
            && Scribe.mode == LoadSaveMode.Inactive)
        {
            // A requested restart already checkpointed and persisted in the same game-thread call that asked for it.
            // Running BeginStop again would checkpoint a second time and could fail on a frozen world, turning a
            // deliberate swap into "the process refused to exit".
            if (restartRequested) FinishExit();
            else BeginStop();
        }
    }

    /// <summary>At most one warning per stall window, so a long lobby cannot flood the log.</summary>
    private static void AttachStallWarned()
    {
        if (Clock.Now - lastAttachWarn < 60) return;
        lastAttachWarn = Clock.Now;
        Log.Warning("[TurboTick headless] 还没连上房间（" + RoomConnection.Status
            + "）。这不再是致命错误：管理页会一直在线，state 会如实显示未连接。");
    }

    internal static void ReportProgress()
    {
        if (!Active || server is null || !ResidentSimulation) return;
        server.ReportSimulationProgress(SharedColony.Session.Epoch, SharedColony.Session.Barrier.AppliedTick,
            !stopping && Volatile.Read(ref stopRequested) == 0 && SharedColony.Gating
            && TurboState.Authority.SessionConnected && RoomConnection.Client?.IsConnected == true
            && (server.RequiredSimulationWorldId is not { } required || SharedColony.Session.WorldId == required));
    }

    internal static void GameReady(Game game, string? saveName, bool newGame)
    {
        if (!Active || server is null || server.GameRunning || bootstrapStarted) return;
        // -quicktest is an explicit local acceptance fixture; normal servers wait for a client publication.
        if (!(initialLoadRequested && saveName == initialSave) && !(newGame && GenCommandLine.CommandLineArgPassed("quicktest"))) return;
        bootstrapStarted = true;
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (!ReferenceEquals(Current.Game, game) || server is null || server.GameRunning) return;
            try
            {
                state = "publishing-initial-world";
                var bytes = File.ReadAllBytes(WorldFiles.SaveCurrentGame());
                var manifest = WorldFiles.Describe(bytes);
                var inputs = CompatibilityManifest.Capture();
                var owner = server;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var hello = CompatibilityManifest.Build(inputs, "ServerBootstrap",
                            Path.Combine(dataDirectory, "bootstrap-manifest.json"), CancellationToken.None);
                        await using var publisher = new DedicatedClient();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await publisher.ConnectAsync("127.0.0.1", owner.Port, hello, timeout.Token).ConfigureAwait(false);
                        await publisher.ClaimHostAsync(timeout.Token).ConfigureAwait(false);
                        using var stream = new MemoryStream(bytes, writable: false);
                        await publisher.UploadWorldAsync(manifest, stream, timeout.Token).ConfigureAwait(false);
                        await publisher.StartGameAsync(timeout.Token).ConfigureAwait(false);
                        pending.Enqueue(() => Log.Message("[TurboTick headless] initial world accepted; loading the canonical snapshot."));
                    }
                    catch (Exception error) { pending.Enqueue(() => Fail("Initial publication", error)); }
                });
            }
            catch (Exception error) { Fail("Initial snapshot", error); }
        });
    }

    private static void BeginStop()
    {
        stopping = true;
        state = "stopping";
        ReportProgress();
        // Stop granting new timeline ticks, then consume its last small watermark before checkpointing.
        // `Find.TickManager` dereferences Current.Game in 1.7, and shutdown is now reachable from `lobby` (a world may
        // have been stopped first), so the world has to be checked before it is read.
        if (SharedColony.Gating && Current.Game is not null && Find.TickManager is { } manager)
        {
            SharedColony.OnTick(server!.Tick);
            var budget = 64;
            while (SharedColony.Session.Barrier.CanAdvance && budget-- > 0 && !manager.Paused)
                manager.DoSingleTick();
            SharedColony.ApplyDue();
        }
        var checkpoint = SharedColony.Gating && RoomConnection.IsCheckpointOwner
            ? RoomConnection.PublishCheckpointAsync(SharedColony.Session.Barrier.AppliedTick) : Task.FromResult(true);
        var owner = server!;
        shutdown = Task.Run(async () =>
        {
            try
            {
                if (!await checkpoint.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                    throw new IOException("Final world checkpoint failed; previous durable checkpoint retained.");
                await owner.PersistAsync().ConfigureAwait(false);
            }
            finally { await owner.DisposeAsync().ConfigureAwait(false); }
        });
        WriteStatus();
    }

    internal static void Shutdown()
    {
        if (!Active || finalized) return;
        finalized = true;
        // Normal termination is requested via SIGTERM/Ctrl+C and completed on the game thread above.
        // Root.Shutdown is also called by the engine; do not perform Verse I/O on a signal thread.
        if (server is not null && shutdown is null)
        {
            Interlocked.Exchange(ref stopRequested, 1);
            ReportProgress();
            try { RoomConnection.PendingCheckpoint.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
            catch (Exception error) { exitCode = 1; Log.Error("[TurboTick headless] pending checkpoint: " + error); }
            BeginStop();
        }
        // The engine also handles SIGTERM. Its shutdown callback must join a flush that Update may
        // already have started, otherwise socket cancellation races the final checkpoint commit.
        try { shutdown?.WaitAsync(TimeSpan.FromSeconds(40)).GetAwaiter().GetResult(); }
        catch (Exception error) { exitCode = 1; Log.Error("[TurboTick headless] " + error); }
        state = exitCode == 0 ? "stopped" : "failed";
        WriteStatus();
        Waylith.EngineApplication.Instance.ExitCode = exitCode;
        ConsoleCancelCleanup();
        RoomConnection.Leave();
        directoryLock?.Dispose();
    }

    private static void FinishExit()
    {
        state = exitCode == 0 ? "stopped" : "failed";
        WriteStatus();
        Log.Message("[TurboTick headless] " + state);
        terminate?.Dispose();
        interrupt?.Dispose();
        Console.CancelKeyPress -= OnCancel;
        directoryLock?.Dispose();
        // A complete final checkpoint and protocol flush precede process exit. No UI prompt in a service.
        Environment.Exit(exitCode);
    }

    private static void ConsoleCancelCleanup()
    {
        Console.CancelKeyPress -= OnCancel;
        terminate?.Dispose();
        interrupt?.Dispose();
    }

    private static void OnCancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; Interlocked.Exchange(ref stopRequested, 1); }
    private static void OnSignal(PosixSignalContext context) { context.Cancel = true; Interlocked.Exchange(ref stopRequested, 1); }
    private static void Fail(string stage, Exception error)
    {
        failure = stage + ": " + error.GetType().Name + ": " + error.Message;
        exitCode = 1;
        state = "failed";
        Log.Error("[TurboTick headless] " + failure);
        Interlocked.Exchange(ref stopRequested, 1);
    }
    private static string? Option(string name)
    {
        if (GenCommandLine.TryGetCommandLineArg("turbotick-" + name, out var value)) return value;
        var environment = Environment.GetEnvironmentVariable("TURBOTICK_" + name.Replace('-', '_').ToUpperInvariant());
        // Compose represents an unset optional setting as an empty environment value.
        return string.IsNullOrEmpty(environment) ? null : environment;
    }
    /// <summary>Names of the Harmony patches that failed to install, so the page can list them by name.</summary>
    private static string[] FailedPatchNames()
    {
        var failed = new List<string>();
        foreach (var pair in PatchInstaller.Status)
            if (!pair.Value) failed.Add(pair.Key);
        failed.Sort(StringComparer.Ordinal);
        return failed.ToArray();
    }

    /// <summary>Which windows are holding the timeline with <c>forcePause</c>. A boolean cannot be acted on: on a
    /// headless server nothing can dismiss a window, so a single stray dialog stops the world for good and the only
    /// visible symptom is "tick is not moving". Naming the type is the whole point.</summary>
    private static string[] ForcePauseWindowNames()
    {
        var stack = Find.WindowStack;
        if (stack is null) return [];        var names = new List<string>();
        foreach (var window in stack.Windows)
        {
            if (window is null || !window.forcePause) continue;
            var name = window.GetType().FullName ?? window.GetType().Name;
            if (!names.Contains(name)) names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    /// <summary>
    /// Expansion defs as this process sees them. The page keeps three facts apart on purpose — content on disk,
    /// authorization, and whether the defs actually loaded — and only the game process can answer the third one.
    /// </summary>
    /// <summary>
    /// Open dialogs as the page must show them. Anonymous objects rather than the record, because this file's status
    /// payload is a single flat object literal and every other list here is projected the same way - the portal reads
    /// property names, not types.
    /// </summary>
    private static object[] DialogStates()
    {
        var dialogs = HeadlessDialogQueue.Snapshots();
        if (dialogs.Length == 0) return [];
        var rows = new List<object>(dialogs.Length);
        foreach (var dialog in dialogs)
            rows.Add(new
            {
                id = dialog.Id,
                type = dialog.Type,
                title = dialog.Title,
                text = dialog.Text,
                buttonA = dialog.ButtonA,
                buttonB = dialog.ButtonB,
                buttonC = dialog.ButtonC,
                destructive = dialog.Destructive,
                autoClosed = dialog.AutoClosed,
                answer = dialog.Answer,
                repeats = dialog.Repeats
            });
        return [.. rows];
    }

    private static object[] DlcStates()
    {
        var defs = DefDatabase<RimWorld.ExpansionDef>.AllDefsListForReading;
        var rows = new List<object>(defs.Count);
        foreach (var def in defs)
            rows.Add(new
            {
                package = def.linkedMod ?? "",
                active = def.Status == RimWorld.ExpansionStatus.Active,
                status = def.Status.ToString(),
                label = def.label ?? def.defName ?? "",
                defName = def.defName ?? ""
            });
        return rows.ToArray();
    }

    /// <summary>
    /// Reads one diagnostic value, substituting <paramref name="fallback"/> if the read throws.
    ///
    /// The status file is the only input the administration page has: the page lives in a separate process now and
    /// can learn nothing it is not told here. So a single unreadable field must not be able to take the whole file
    /// down - measured: three <c>status: NullReferenceException</c> lines, each one costing a full status write,
    /// because <c>Find.TilePicker</c> and <c>Find.GravshipController</c> dereference through <c>Find.World</c>, which
    /// is null on the entry screen (the stack bottom was <c>Verse.Root_Entry.Update</c>).
    /// </summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    /// <summary>
    /// One line per unguarded frame hook, first failure only, then silence.
    ///
    /// <c>Update</c> runs every frame, and an exception escaping it goes to the engine's own handler - which logs the
    /// full stack. A hook that fails on every frame therefore writes ~1 MB/s: measured 1.78 GB of log in about two
    /// minutes from a single null read. Nothing about that failure mode is visible until the disk fills, so the hook
    /// is failed once by name and then skipped.
    /// </summary>
    private static void Guard(string name, Action hook)
    {
        if (failedHooks.Contains(name)) return;
        try { hook(); }
        catch (Exception error)
        {
            failedHooks.Add(name);
            Log.Error("[TurboTick headless] " + name + " 抛出异常，本进程不再运行它：" + error.GetType().Name + ": "
                + error.Message);
        }
    }

    /// <summary>Hooks that threw once. Skipped from then on; see <see cref="Guard"/> for why this is not optional.</summary>
    private static readonly HashSet<string> failedHooks = new(StringComparer.Ordinal);

    private static void WriteStatus()
    {
        if (dataDirectory.Length == 0) return;
        try
        {
            var path = Path.Combine(dataDirectory, "headless-status.json");
            // Every World-derived getter here dereferences through `Find.World`, which falls back to
            // `Current.CreatingWorld` when there is no game - and that is null on the entry screen. Measured:
            // three `status: NullReferenceException` lines from `WriteStatus`, all with
            // `Verse.Root_Entry.Update` at the bottom of the stack, i.e. before a game existed at all.
            // `Find.TilePicker`, `Find.GravshipController` and `Find.TickManager` are all unsafe without a world.
            var hasGame = Current.Game is not null;
            var hasWorld = hasGame && Current.Game!.World is not null;
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, pid = Environment.ProcessId, state, failure,
                port = server?.Port, protocol = NetworkProtocol.Version, clientVersion = NetworkProtocol.ClientVersion,
                epoch = server?.Epoch, serverTick = server?.Tick, appliedTick = SharedColony.Session.Barrier.AppliedTick,
                gameTick = hasGame ? Safe(() => Find.TickManager?.TicksGame, (int?)null) : null,
                phase = SharedColony.Session.Phase.ToString(), checkpointTick = server?.CheckpointTick,
                connected = RoomConnection.Client?.IsConnected == true, modHash = server?.LockedModHash,
                authoritative = server is { } running && running.Session?.Authoritative == true,
                lastFrameTick = Volatile.Read(ref lastFrameTick),
                framesSent = Volatile.Read(ref framesSent),
                peers = server?.PeerCount, joinedPeers = server?.JoinedPeerCount,
                replicas = server?.DisplayReplicaCount,
                // Distinguishes "waiting for the first replica" from "stuck": both stop the tick, only one is normal.
                heldForReplicas = HeldForAudience,
                held = TurboState.Authority.IsHeld, frozen = AuthoritativeReplica.Frozen,
                holdLocal = HoldLocalTicks, timeSpeed = TurboState.Authority.Current.TimeSpeed,
                budget = server?.SimulationTickBudgetForDiagnostics,
                // The lead the room is pacing with. Paired with `budget` because a budget of 1-2 at `Superfast` is
                // exactly "the lead is 6 ticks but 6 ticks is only 16 ms there", and neither field shows that alone.
                lead = server?.SimulationLeadTicksForDiagnostics,
                lockstep = server?.LockstepDiagnostics,
                // Find.TickManager dereferences Current.Game in 1.7, so it must not be read without one.
                paused = hasGame ? Safe(() => Find.TickManager?.Paused, (bool?)null) : null,
                tickBusy = SharedColony.TickExecuting,
                slow = hasGame ? Safe(() => Find.TickManager?.slower?.ForcedNormalSpeed, (bool?)null) : null,                // Which of the ForcePaused sources is holding the timeline, so a freeze is diagnosable.
                longEvent = LongEventHandler.ForcePause,
                // `Safe` on each: these read through `Find.World`, and the status file must survive whatever the
                // game's own getters do on the entry screen. A wrong `false` costs one diagnostic line; a throw
                // costs the page every number on it.
                forcePauseWindow = Safe(() => hasGame && Find.WindowStack?.WindowsForcePause == true, false),
                // A boolean is not diagnosable: the timeline stops and nothing says which input did it. A headless
                // server has no one to click a dialog away, so the name is the whole diagnosis. All six inputs of
                // TickManager.ForcePaused are reported, because "paused" alone cannot tell them apart.
                forcePauseWindows = Safe(ForcePauseWindowNames, []),
                // A headless process cannot dismiss a dialog, so a dialog with a real choice in it stops the world
                // until somebody answers. That is the correct behaviour - the mod is waiting - but it used to be
                // invisible. These are the questions the page has to show.
                dialogs = Safe(DialogStates, []),
                forcePauseLongEvent = LongEventHandler.ForcePause,
                forcePauseTilePicker = Safe(() => hasWorld && Find.TilePicker?.Active == true, false),
                forcePauseCutscene = WorldComponent_GravshipController.CutsceneInProgress,
                forcePauseLanding = Safe(() => hasWorld && Find.GravshipController?.LandingAreaConfirmationInProgress == true, false),
                // Why the timeline is or is not moving. `paused` alone answers nothing when it is already false:
                // measured, a real save sat at serverTick=2505 for 100 s with paused=false, and every other flag
                // here reading normal. These are the remaining inputs of TickManager.TickManagerUpdate: the
                // per-frame tick budget it consumed, whether it ran out of wall-clock inside the frame, and the
                // two reasons it can decline to advance a tick at all.
                ticksThisFrame = hasGame ? Safe(() => Find.TickManager?.TicksThisFrame, (int?)null) : null,
                tickRateMultiplier = hasGame ? Safe(() => (float?)Find.TickManager?.TickRateMultiplier, null) : null,
                forceNormalSpeed = hasGame ? Safe(() => Find.TickManager?.slower?.ForcedNormalSpeed, (bool?)null) : null,
                // `TickManager.PlayerCanControl` is private in 1.7; it is exactly `Current.Game.PlayerHasControl`
                // plus a gravship landing check, so the public half is what can be reported. A settings write is
                // rejected outright when this is false, which would explain a speed that never takes effect.
                playerHasControl = hasGame ? Safe(() => Current.Game?.PlayerHasControl, (bool?)null) : null,
                debugMode = MapGenerator.debugMode,
                // The administration page lives in its own process now, so everything it used to read off this
                // one has to be written down here: identity, throughput, patch state and which DLC defs are live.
                save = CurrentSave,
                uptime = Math.Round(UptimeSeconds, 1),
                installedVersion,
                installedModHash,
                lockedGameVersion = server?.LockedGameVersion,
                gameRunning = server is { GameRunning: true },
                tps = TurboState.MeasuredTps,
                targetTps = TurboState.TargetTps,
                patchCritical = PatchInstaller.AllCritical,
                patchFailed = FailedPatchNames(),
                wireOutBytes = WireStats.Totals().OutBytes,
                wireOutFrames = WireStats.Totals().OutFrames,
                wireInBytes = WireStats.Totals().InBytes,
                wireInFrames = WireStats.Totals().InFrames,
                dlc = DlcStates()
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) { Log.Warning("[TurboTick headless] status: " + error.GetType().Name); }
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Update))]
internal static class HeadlessUpdatePatch
{
    private static void Postfix()
    {
        if (HeadlessRuntime.Active)
        {
            TurboState.Update();
            SharedColony.Update();
            HeadlessRuntime.Update();
        }
        // Last, after every per-frame job, and unconditionally: this patch class is installed for every
        // `-headless` process (see TurboTickMod.Init), and that is exactly the set of processes whose
        // engine loop has no frame limit of its own - the rendering path never reaches here. Measured
        // before the fix, on a display replica: `停顿期本端循环 1-1190 次`, about 1900 fps on a pinned core.
        HeadlessFrameThrottle.Wait();
    }
}


























