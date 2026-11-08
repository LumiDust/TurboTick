using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HarmonyLib;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

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
    private static bool finalized;
    private static int stopRequested;
    private static int exitCode;
    private static float nextStatus;
    private static float nextPersist;
    private static float nextConnect;
    private static float startedAt;
    private static string state = "starting";
    private static string? failure;
    internal static bool StopRequested => Volatile.Read(ref stopRequested) != 0;

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
    internal static bool HoldLocalTicks => ServerMode && (!ResidentSimulation || !SharedColony.Gating || stopping);

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
            var port = int.Parse(Option("port") ?? "30572", NumberStyles.None, CultureInfo.InvariantCulture);
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException("port");
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
                    candidate = new DedicatedServer(new ServerOptions(host, port, dataDirectory, hello.GameVersion,
                        hello.ModHash, !controllerOnly, AdminPassword: adminPassword)
                    { RequireSimulation = ResidentSimulation, Authoritative = ResidentSimulation, Lockstep = Lockstep });
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
            Log.Message($"[TurboTick headless] participant phase={session.Phase} status=\"{RoomConnection.Status}\" "
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
    private static int lastFrameLogTick = -1;
    private static int framesSent;
    private static int lastFrameTick = -1;

    private static readonly Dictionary<int, (int Map, int X, int Z, int Rotation, int Stack, int Hits)> lastThings = [];

    /// <summary>Cell packed into one int so a job target travels without a nested object per pawn.</summary>
    private static int EncodeCell(LocalTargetInfo? target) =>
        target is { } value && value.IsValid ? value.Cell.x * 1000 + value.Cell.z : -1;

    /// <summary>Only objects that moved, changed stack, or took damage since the previous frame. The first
    /// frame after a load establishes the baseline and sends nothing, because the replica already holds
    /// the identical snapshot. Later frames also report objects that disappeared.</summary>
    private static (List<ReplicaThing>? Changed, List<int>? Removed, List<ReplicaCreation>? Created) DiffThings()
    {
        List<ReplicaThing>? changed = null;
        List<ReplicaCreation>? created = null;
        var seen = new HashSet<int>();
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is Pawn || thing.def.category is ThingCategory.Mote or ThingCategory.Ethereal) continue;
                var id = thing.thingIDNumber;
                seen.Add(id);
                var current = (map.uniqueID, thing.Position.x, thing.Position.z, thing.Rotation.AsInt,
                    thing.stackCount, thing.HitPoints);
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
                        current.Item2, current.Item3, current.Item4, current.Item5, state));
                    continue;
                }
                (changed ??= []).Add(new ReplicaThing(id, current.Item1, current.Item2,
                    current.Item3, current.Item4, current.Item5, current.Item6, state));
            }
        }
        if (!baselineReady)
        {
            baselineReady = true;
            return (null, null, null);
        }
        List<int>? removed = null;
        foreach (var id in lastThings.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            lastThings.Remove(id);
            (removed ??= []).Add(id);
        }
        return (changed, removed, created);
    }

    private static bool baselineReady;

    /// <summary>Every peer simulates; the server paces ticks and orders commands. Off unless asked for,
    /// because the authoritative display path is the one that has been through acceptance.</summary>
    internal static readonly bool Lockstep =
        string.Equals(Environment.GetEnvironmentVariable("TURBOTICK_LOCKSTEP"), "1", StringComparison.Ordinal);

    internal static void PublishFrame()
    {
        if (server is null || !SharedColony.Gating || Lockstep) return;
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
                        EncodeCell(pawn.CurJob?.targetB), pawn.CurJob?.targetB.Thing?.thingIDNumber ?? -1));
                }
            var (changed, removed, created) = DiffThings();
            var sent = server.BroadcastFrame(new AuthoritativeFrame(tick, pawns, changed, removed, null, created));
            Interlocked.Add(ref framesSent, sent);
            Volatile.Write(ref lastFrameTick, tick);
            if (sent > 0 && (lastFrameLogTick < 0 || tick - lastFrameLogTick >= 600))
            {
                lastFrameLogTick = tick;
                Log.Message($"[TurboTick headless] 权威帧 tick={tick}：{pawns.Count} 个角色 -> {sent} 个显示副本。");
            }
        }
        catch (Exception error) { Log.Warning("[TurboTick headless] frame publish: " + error.Message); }
    }

    internal static void Update()
    {
        if (!Active) return;
        // A participant has no listener and no status file, so without this a stalled acceptance run is
        // invisible: the process keeps burning CPU and the log simply stops. One line every five seconds
        // answers "which phase is it stuck in" without a debugger.
        if (!ServerMode) ReportParticipant();
        while (pending.TryDequeue(out var action))
            try { action(); } catch (Exception error) { Fail("Game thread", error); }
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
        if (RoomConnection.Client?.IsConnected == true)
        {
            attached = true;
            // Only a resident simulator loads a colony of its own. A coordinator holds no world state: the
            // host player publishes it and the server stores the bytes.
            if (ResidentSimulation && !server.GameRunning && initialSave is not null && !initialLoadRequested
                && !RoomConnection.Busy && !LongEventHandler.AnyEventNowOrWaiting)
            {
                initialLoadRequested = true;
                if (!File.Exists(GenFilePaths.FilePathForSavedGame(initialSave)))
                    Fail("Initial save", new FileNotFoundException("Configured save does not exist under Saves/."));
                else GameDataSaveLoader.LoadGame(initialSave);
            }
        }
        else if (!RoomConnection.Busy && Clock.Now >= nextConnect)
        {
            if (!attached && Clock.Now - startedAt > 60)
                Fail("Simulation attach", new InvalidOperationException(RoomConnection.Status));
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
        else state = initialLoadRequested ? "loading-initial-save" : "lobby";
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
            && Scribe.mode == LoadSaveMode.Inactive) BeginStop();
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
        if (SharedColony.Gating && Find.TickManager is { } manager)
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
    private static void WriteStatus()
    {
        if (dataDirectory.Length == 0) return;
        try
        {
            var path = Path.Combine(dataDirectory, "headless-status.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, pid = Environment.ProcessId, state, failure,
                port = server?.Port, protocol = NetworkProtocol.Version, clientVersion = NetworkProtocol.ClientVersion,
                epoch = server?.Epoch, serverTick = server?.Tick, appliedTick = SharedColony.Session.Barrier.AppliedTick,
                gameTick = Current.ProgramState == ProgramState.Playing && Current.Game is not null ? Find.TickManager?.TicksGame : null,
                phase = SharedColony.Session.Phase.ToString(), checkpointTick = server?.CheckpointTick,
                connected = RoomConnection.Client?.IsConnected == true, modHash = server?.LockedModHash,
                authoritative = server is { } running && running.Session?.Authoritative == true,
                lastFrameTick = Volatile.Read(ref lastFrameTick),
                framesSent = Volatile.Read(ref framesSent),
                peers = server?.PeerCount, joinedPeers = server?.JoinedPeerCount,
                replicas = server?.DisplayReplicaCount,
                held = TurboState.Authority.IsHeld, frozen = AuthoritativeReplica.Frozen,
                holdLocal = HoldLocalTicks, timeSpeed = TurboState.Authority.Current.TimeSpeed,
                budget = server?.SimulationTickBudgetForDiagnostics,
                lockstep = server?.LockstepDiagnostics,
                // Find.TickManager dereferences Current.Game in 1.7, so it must not be read without one.
                paused = Current.Game is null ? (bool?)null : Find.TickManager?.Paused,
                tickBusy = SharedColony.TickExecuting,
                slow = Current.Game is null ? (bool?)null : Find.TickManager?.slower?.ForcedNormalSpeed,
                // Which of the ForcePaused sources is holding the timeline, so a freeze is diagnosable.
                longEvent = LongEventHandler.ForcePause,
                forcePauseWindow = Find.WindowStack?.WindowsForcePause == true
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
        if (!HeadlessRuntime.Active) return;
        TurboState.Update();
        SharedColony.Update();
        HeadlessRuntime.Update();
    }
}
