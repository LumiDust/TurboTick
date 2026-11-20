using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace TurboTick.Networking;

/// <summary>A standalone authority for compatibility, roles, rate policies, and snapshot downloads.</summary>
public sealed partial class DedicatedServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly string _statePath;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    /// <summary>Highest tick a command has been stamped for. Hash reports below it are not comparable: the origin
    /// already applied the action locally while the others are still waiting for the agreed tick.</summary>
    private int _committedThroughTick;

    private long _inFlightTicksSkipped;

    /// <summary>Diagnostics for the published timeline's own production rate.
    ///
    /// Measured failure this exists for: a 20 minute room ran at 45.0 tick/s end to end - the authority frame
    /// log, the server's own `tick=` status line and both replicas agreed to within 0.1 tick/s - while the
    /// engine entered `TickManagerUpdate` about 57 times a second and the replicas held only ~16 of those
    /// behind `barrier`. Everything about the fault is inside `GameLoopAsync`: `NativeTickClock.Advance` turns
    /// elapsed wall time into ticks and its floor/remainder arithmetic does not drop ticks, so either the loop
    /// iterates too rarely (a `PeriodicTimer` that is late is never compensated) or it throws ticks away in
    /// `Math.Clamp(count, 0, budget)`. Those two have opposite fixes, and `tick=` alone cannot tell them
    /// apart, so both numbers are reported.</summary>
    private long _loopIterations;
    private double _loopElapsedSeconds;
    private long _loopTicksProduced;
    private long _loopTicksClamped;
    private long _loopIdleIterations;
    private double lastLoopElapsed;
    private long lastLoopIterations;
    private long lastLoopTicks;
    private long lastLoopClamped;
    private long lastLoopIdle;
    private long lastLoopPublishedTick;
    private readonly ConcurrentDictionary<long, Peer> _peers = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _run;
    private CancellationTokenRegistration _shutdownRegistration;
    private Task _acceptTask = Task.CompletedTask;
    private SpeedPolicy _policy = new();
    private int _heldForAudience;
    private string? _lockedGameVersion;
    private string? _lockedModHash;
    /// <summary>The mod list the room was locked to, echoed to every joiner so a mismatch names what to install.</summary>
    private string? _lockedMods;
    private long? _controllerId;
    private long? _hostId; // A room host exists only after an explicit successful ClaimHost.
    private long _nextPeer;
    private long _roomGeneration;
    private string? _snapshotStoreId;
    private int _port;
    private int _disposed;

    // Shared-colony session: the server owns epoch, tick and command sequencing.
    private sealed record WorldUpload(bool Checkpoint, long Epoch, int Tick)
    {
        internal readonly Dictionary<int, byte[]> Chunks = [];
    }
    private readonly ConcurrentDictionary<(long Peer, string Id), WorldUpload> _worldUploads = new();
    private SnapshotManifest? _worldManifest;
    private long _epoch = -1;
    private int _tick;
    private long _commandSequence;
    private int _gameRunning;
    /// <summary>The save this room belongs to, as the launcher named it. Written into the persisted state so the
    /// next launch can recognise a swap instead of resuming a world the operator replaced.</summary>
    private string? _startupSave;
    private Task _tickTask = Task.CompletedTask;

    // Command history lets a peer that joins mid-session replay the same decisions from the same snapshot.
    private readonly List<GameCommand> _history = [];
    private readonly ConcurrentDictionary<int, string> _firstHashAtTick = new();
    /// <summary>Sampling shapes already reported as incomparable, so the warning is one line per pair rather
    /// than one per tick. A peer using TURBOTICK_HASH_EVERY=1 hashes a pawn-only projection, which cannot be
    /// compared with the full-colony hash of a peer using the default.</summary>
    private readonly HashSet<(int Theirs, int Mine)> _incomparableShapes = [];
    private readonly ConcurrentDictionary<int, string[]> _firstCategoriesAtTick = new();
    private readonly ConcurrentDictionary<(long Peer, int Tick), string> _hashByPeerTick = new();
    private readonly ConcurrentDictionary<int, long> _firstPeerAtTick = new();
    private readonly Dictionary<long, long> _pendingJoiners = new(); // peer -> ticks when it started waiting
    private readonly HashSet<int> _reportedDesyncTicks = [];
    private long? _checkpointOwnerId;
    private SnapshotManifest? _checkpointManifest;
    private int _checkpointTick;

    public int CheckpointTick => Volatile.Read(ref _checkpointTick);
    public SnapshotManifest? CheckpointManifest => Volatile.Read(ref _checkpointManifest);
    private bool _historyTruncated;
    private int _seed;
    private int MaxHistory => _options.MaxHistory;
    private int _commandsSincePersist;
    private readonly object _commandGate = new();
    private readonly object _hashGate = new();
    private int _hashWindowFloor;
    private const int HashWindow = 600;
    public int TrackedHashTicks => _firstHashAtTick.Count;

    /// <summary>Diagnostics for authoritative frames: who is connected and who should receive them.</summary>
    public int PeerCount => _peers.Count;
    public int JoinedPeerCount => _peers.Values.Count(peer => peer.Joined);
    public int DisplayReplicaCount => _peers.Values.Count(peer => peer.Joined && !peer.IsSimulation
        // 还没载入权威存档的副本一帧都没收到，把它算成「正在跟随的副本」会让管理页在载入期间说谎。
        && (!_options.Authoritative || peer.WorldReady));

    public SessionInfo? Session
    {
        get
        {
            var world = WorldManifest;
            return world is null || !GameRunning ? null : new SessionInfo(Epoch, Tick, Volatile.Read(ref _seed), world.Id, CheckpointTick, _options.Authoritative, _options.Authoritative, _options.Lockstep);
        }
    }

    public int HistoryCount { get { lock (_history) return _history.Count; } }
    public bool HistoryTruncated { get { lock (_history) return _historyTruncated; } }

    public SnapshotManifest? WorldManifest => Volatile.Read(ref _worldManifest);
    public long Epoch => Interlocked.Read(ref _epoch);
    public int Tick => Volatile.Read(ref _tick);
    public bool GameRunning => Volatile.Read(ref _gameRunning) != 0;

    /// <summary>True while the timeline is held only because no display replica is attached. Distinct from the
    /// operator pausing: the policy still carries the speed the room will resume at.</summary>
    public bool HeldForAudience => Volatile.Read(ref _heldForAudience) != 0;

    private sealed record SavedState(SpeedPolicy Policy, string? GameVersion, string? ModHash,
        SessionInfo? Session = null, List<GameCommand>? History = null, bool HistoryTruncated = false, SnapshotManifest? World = null,
        string? SnapshotStoreId = null, SnapshotManifest? Checkpoint = null, int CheckpointTick = 0,
        // Which save this room was started into. The resident process decides whether to run its load funnel by
        // asking `GameRunning`, and a restored room answers "yes, a world is running" - so without this the next
        // launch after a swap would restore the *previous* world into the room and never load the requested save.
        // Measured: a swap to 84a0f22c logged "载入存档 84a0f22c" and then loaded 8ee6381e for nine minutes.
        string? StartupSave = null,
        // The room's own active mod list, in load order.
        //
        // Measured: without it, a restart leaves `_lockedMods` null, so a refused handshake answers with the bare
        // `ModHashMismatch` code and nothing else - the client then has no list to render and falls back to a
        // paragraph of prose naming no mod at all. That is the exact symptom the verification table was built to
        // remove, and it came back on the first restart because the field was only ever held in memory.
        string? Mods = null);

    public int Port => Volatile.Read(ref _port);
    public SpeedPolicy CurrentPolicy => Volatile.Read(ref _policy);
    public string? LockedGameVersion => Volatile.Read(ref _lockedGameVersion);
    public string? LockedModHash => Volatile.Read(ref _lockedModHash);

    /// <summary>The room's active mod list, in load order, or null when no peer has described the room yet.
    ///
    /// <para>Exposed so a test can assert the two properties that make the verification table usable at all: it
    /// survives a restart, and it is never overwritten by a peer that disagrees with the room.</para></summary>
    public string? LockedMods => Volatile.Read(ref _lockedMods);
    public SnapshotStore Snapshots { get; private set; }

    /// <summary>`<DataDirectory>/session/<存档 hash 前 16>.json`：每个玩家一条最新视图记录。
    /// 见 `docs/server-plan.md` 阶段 8。</summary>
    public SessionViewStore Views { get; }

    /// <summary>诊断用：会话视图当前按哪份存档在存。房间还没有世界时是 `no-world`。</summary>
    public string SessionSaveHash => CurrentSaveHash;

    /// <summary>诊断用：这条房间的会话视图文件在哪。</summary>
    public string SessionViewPath => Views.PathFor(CurrentSaveHash);

    /// <summary>本进程里发放过的 token（`hash|token`）。它只解决一件事：房间还没有世界（没有存档 hash）时
    /// 连接过的人，在世界发布之后不该被当成新玩家重新发一个 token。</summary>
    private readonly HashSet<string> _issuedTokens = new(StringComparer.Ordinal);

    public DedicatedServer(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Address);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModHash);
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 0 and 65535.");
        if (options.GameVersion != "1.7" && !Validation.IsFullGameVersion(options.GameVersion))
            throw new ArgumentException("GameVersion must be 1.7 or a complete 1.7.x version.", nameof(options));
        if (options.MaxHistory is < 1 or > 5_000_000) throw new ArgumentOutOfRangeException(nameof(options), "MaxHistory must be between 1 and 5,000,000.");
        if (options.ModHash.Length > 512 || options.ModHash.Any(char.IsControl))
            throw new ArgumentException("ModHash must contain at most 512 printable characters.", nameof(options));
        _adminKey = string.IsNullOrEmpty(options.AdminPassword) ? null :
            AdminAuthentication.DeriveKey(options.AdminPassword, _adminSalt, AdminAuthentication.Iterations);
        _options = options with { DataDirectory = Path.GetFullPath(options.DataDirectory), AdminPassword = null };
        Directory.CreateDirectory(_options.DataDirectory);
        _statePath = Path.Combine(_options.DataDirectory, "server-state.json");
        Snapshots = new SnapshotStore(Path.Combine(_options.DataDirectory, "Snapshots"));
        // 阶段 8：会话视图按存档 hash 分文件，落在 `<DataDirectory>/session/<hash16>.json`。
        // 只有玩家会写它（常驻仿真端不写），所以它天然是「玩家看什么」，不是世界状态。
        Views = new SessionViewStore(Path.Combine(_options.DataDirectory, "session"),
            message => Console.WriteLine("[server] 会话视图: " + message));
        _port = options.Port;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_run is not null) throw new InvalidOperationException("The server is already started.");
            await LoadStateAsync(ct).ConfigureAwait(false);
            var addresses = IPAddress.TryParse(_options.Address, out var address)
                ? new[] { address } : await Dns.GetHostAddressesAsync(_options.Address, ct).ConfigureAwait(false);
            address = addresses.FirstOrDefault() ?? throw new ArgumentException("The bind host resolved to no addresses.");
            _listener = new TcpListener(address, _options.Port);
            _listener.Start();
            Volatile.Write(ref _port, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _shutdownRegistration = _run.Token.Register(() =>
            {
                _listener?.Stop();
                foreach (var peer in _peers.Values) peer.Close();
            });
            _acceptTask = AcceptLoopAsync(_listener, _run.Token);
            _tickTask = GameLoopAsync(_run.Token);
        }
        catch
        {
            _listener?.Stop();
            _listener = null;
            _shutdownRegistration.Dispose();
            _run?.Dispose();
            _run = null;
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_run is null) return;
            // Persist before shutdown so a restart resumes the same session, not an empty one.
            try { await PersistSessionAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await _run.CancelAsync().ConfigureAwait(false);
            _listener?.Stop();
            foreach (var peer in _peers.Values) peer.Close();
            await _acceptTask.ConfigureAwait(false);
            await _tickTask.ConfigureAwait(false);
            await Task.WhenAll(_handlers.Values.ToArray()).ConfigureAwait(false);
            _shutdownRegistration.Dispose();
            _run.Dispose();
            _run = null;
            _listener = null;
            _controllerId = null;
            _hostId = null;
            _checkpointOwnerId = null;
            ClearSimulationState();
            _worldUploads.Clear();
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync().ConfigureAwait(false); }
        finally { if (_adminKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_adminKey); }
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        SavedState? saved = null;
        if (File.Exists(_statePath))
            saved = await AtomicFile.ReadJsonAsync<SavedState>(_statePath, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The persisted server state is empty.");
        // What this process would lock if it started from nothing. "1.7" and "development" are the two spellings
        // that mean "do not lock anything", so they cannot be compared against a persisted fingerprint.
        var wantedVersion = _options.GameVersion == "1.7" ? null : _options.GameVersion;
        var wantedModHash = _options.ModHash == "development" ? null : _options.ModHash;
        saved = await ArchiveRoomIfPackChangedAsync(saved, wantedVersion, wantedModHash, ct).ConfigureAwait(false);
        var policy = saved?.Policy ?? new SpeedPolicy();
        // Reconstructing also validates a persisted policy before any socket is opened.
        policy = new SpeedPolicy(policy.Enabled, policy.Multiplier, policy.MaxRate, policy.Revision, policy.TimeSpeed);
        // Startup values initialize a room; a persisted lock (including an admin reset) is authoritative.
        var version = saved?.GameVersion ?? wantedVersion;
        var modHash = saved?.ModHash ?? wantedModHash;
        if (version is not null && !Validation.IsFullGameVersion(version))
            throw new InvalidDataException("The persisted compatibility game version is invalid.");
        if (modHash is not null && (string.IsNullOrWhiteSpace(modHash) || modHash.Length > 512 || modHash.Any(char.IsControl)))
            throw new InvalidDataException("The persisted compatibility mod hash is invalid.");
        Volatile.Write(ref _policy, policy);
        Volatile.Write(ref _lockedGameVersion, version);
        Volatile.Write(ref _lockedModHash, modHash);

        _snapshotStoreId = saved?.SnapshotStoreId;
        if (_snapshotStoreId is not null && (_snapshotStoreId.Length != 32 || _snapshotStoreId.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("The persisted snapshot store id is invalid.");
        Snapshots = new SnapshotStore(SnapshotDirectory(_snapshotStoreId));
        ClearRoomRuntime();

        // A room is about one world. If this process was told to start into a different save than the room was
        // persisted with, the persisted world/session/checkpoint describe a world nobody asked for any more, and
        // restoring them makes the resident believe it is already running - so its load funnel stands down and the
        // requested save is never read. Measured: a swap request for 84a0f22c logged the right save name at
        // startup and then loaded 8ee6381e for the whole run.
        //
        // Only a *known* mismatch resets. An older state file has no StartupSave, and a room started without one
        // (lobby) must keep restoring exactly as before.
        var startingElsewhere = saved?.StartupSave is { Length: > 0 } persistedSave
            && _options.StartupSave is { Length: > 0 } wantedSave
            && !string.Equals(persistedSave, wantedSave, StringComparison.Ordinal);
        if (startingElsewhere)
        {
            Console.WriteLine($"[server] 房间属于存档 {Short(saved!.StartupSave!)}，本次启动要求 {Short(_options.StartupSave!)}："
                + "丢弃持久世界的会话状态，等新世界载入后重新发布");
            Volatile.Write(ref _startupSave, _options.StartupSave);
            // The snapshot store is left on disk on purpose: it is the only copy of a checkpoint the operator may
            // still want, and nothing here references it any more.
            return;
        }

        Volatile.Write(ref _checkpointManifest, saved?.Checkpoint);
        Volatile.Write(ref _checkpointTick, saved?.CheckpointTick ?? 0);
        Volatile.Write(ref _startupSave, saved?.StartupSave ?? _options.StartupSave);
        // Restore the room's mod list *before* any peer can handshake. Without this a restart left `_lockedMods` null
        // and every refusal answered with the bare code - the client then had no list to render, which is precisely
        // the "wall of text naming no mod" that the verification table exists to remove.
        if (saved?.Mods is { Length: > 0 } persistedMods) Volatile.Write(ref _lockedMods, persistedMods);

        // Resume the previous session so reconnecting players replay the same decisions.
        if (saved?.World is { } persistedWorld) Volatile.Write(ref _worldManifest, persistedWorld);
        if (saved?.Session is { } session && session.Epoch >= 0 && session.Tick >= 0)
        {
            Volatile.Write(ref _epoch, session.Epoch);
            Volatile.Write(ref _tick, session.Tick);
            Volatile.Write(ref _seed, session.Seed);
            lock (_history)
            {
                _history.Clear();
                if (saved.History is { Count: > 0 }) _history.AddRange(saved.History);
                _historyTruncated = saved.HistoryTruncated;
            }
            Interlocked.Exchange(ref _commandSequence, _history.Count == 0 ? 0 : _history.Max(c => c.Sequence));
            Volatile.Write(ref _gameRunning, 1);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (_peers.Count >= 256 || _peers.Values.Count(p => !p.Joined && !p.AuthenticatedAdmin) >= 64)
                { socket.Dispose(); continue; }
                var id = Interlocked.Increment(ref _nextPeer);
                var peer = new Peer(id, socket, ct, Interlocked.Read(ref _roomGeneration));
                _peers[id] = peer;
                var handler = RunPeerAsync(peer);
                _handlers[id] = handler;
                _ = handler.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    _handlers.TryRemove(id, out _);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (SocketException)
        {
            // Listener failure shuts down established sockets too; StopAsync still observes every task.
            foreach (var peer in _peers.Values) peer.Close();
        }
    }

    private async Task RunPeerAsync(Peer peer)
    {
        var writer = WriteLoopAsync(peer);
        var heartbeat = HeartbeatLoopAsync(peer);
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await peer.Connection.ReadAsync(handshake.Token).ConfigureAwait(false);
            if (hello is null) return;
            if (hello.Type == "AdminHello")
            {
                peer.IsAdminConnection = true;
                await AuthenticateAdminAsync(peer, hello, handshake.Token).ConfigureAwait(false);
            }
            else
            {
                if (hello.Type != "Hello" || hello.Hello is null)
                    throw new NetworkException("HelloRequired", "The first message must be Hello or AdminHello.");
                await JoinAsync(peer, hello, handshake.Token).ConfigureAwait(false);
            }
            while (!peer.Token.IsCancellationRequested)
            {
                var message = await peer.Connection.ReadAsync(peer.Token).ConfigureAwait(false);
                if (message is null) break;
                if (message.ProtocolVersion != NetworkProtocol.Version)
                    throw new NetworkException("ProtocolMismatch", $"Protocol version must be {NetworkProtocol.Version}.");
                // One bad message must not end the session.
                //
                // Measured: a player dragged a 25-cell stockpile, the client completed 18 writes with no gate drop
                // and no send failure, and the server recorded exactly one inbound Command. A dispatch exception
                // used to leave this loop entirely and close the peer, after which the client kept writing into a
                // half-closed socket - exactly "the write succeeded, nothing arrived, nobody logged an error".
                // Recoverable message faults are now reported to the peer and the loop continues; authentication
                // and cancellation still end the session.
                try
                {
                    if (peer.AuthenticatedAdmin) await DispatchAdminAsync(peer, message).ConfigureAwait(false);
                    else await DispatchAsync(peer, message).ConfigureAwait(false);
                }
                catch (NetworkException fault) when (IsRecoverable(fault))
                {
                    NoteRecoveredFault(peer, fault);
                    peer.TryQueue(WireMessage.Error(fault.Code, fault.Message));
                }
            }
        }
        catch (NetworkException ex)
        {
            // A refusal has to carry the room's own list, not just the word "mismatch".
            //
            // Measured: the client used to receive only `ModHashMismatch` and fall back to a paragraph of prose,
            // because its `ServerMods` is populated from Welcome - a message a refused join never receives. The
            // result was a wall of text naming no mod at all, which is exactly what the player cannot act on.
            // Sending the list beside the code turns the same refusal into "these 3 are missing, this one is a
            // different build, these 2 load in another order".
            await SendFatalAsync(peer, ex.Code, ex.Message, VerdictPayload(ex.Code)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        catch (Exception) { await SendFatalAsync(peer, "ServerError", "The server could not process this connection.").ConfigureAwait(false); }
        finally
        {
            peer.Close();
            await Task.WhenAll(writer, heartbeat).ConfigureAwait(false);
            await LeaveAsync(peer).ConfigureAwait(false);
            _peers.TryRemove(peer.Id, out _);
            // A departed peer must not keep holding the lockstep timeline at its last reported tick.
            _peerAppliedTick.TryRemove(peer.Id, out _);
            peer.DisposeToken();
        }
    }

    private async Task JoinAsync(Peer peer, WireMessage message, CancellationToken ct)
    {
        if (message.ProtocolVersion != NetworkProtocol.Version)
            throw new NetworkException("ProtocolMismatch", $"Protocol version must be {NetworkProtocol.Version}.");
        var hello = message.Hello!;
        Validation.Hello(hello);
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (peer.RoomGeneration != _roomGeneration || peer.Token.IsCancellationRequested)
                throw new NetworkException("SessionChanged", "The room changed before this player joined.");
            var isSimulation = ValidateSimulationAdmission(peer, hello);
            var version = _lockedGameVersion ?? hello.GameVersion;
            var modHash = _lockedModHash ?? hello.ModHash;
            // Record the room's mod list before any comparison, not after it.
            //
            // Measured: the write used to sit inside the "first join" branch below, i.e. it only ran for a peer whose
            // hashes already matched. A room that came up and was *immediately* refused therefore kept `_lockedMods`
            // null, and `VerdictPayload` had nothing to attach - the client fell back to prose naming no mod, which
            // is the exact symptom the verification table was built to remove.
            //
            // Only a peer that agrees with the room describes it: when nothing is locked yet the first handshake
            // *becomes* the room's definition (which is what the branch below already does), and when something is
            // locked only a matching peer may be recorded. A mismatching peer's list is exactly the wrong list, and
            // storing it would make the next refusal point at the victim's own mods.
            if (hello.Mods is { Length: > 0 } reported && Volatile.Read(ref _lockedMods) is null
                && string.Equals(modHash, hello.ModHash, StringComparison.Ordinal))
                Volatile.Write(ref _lockedMods, reported);
            if (!string.Equals(version, hello.GameVersion, StringComparison.Ordinal))
            {
                Console.WriteLine($"[server] rejected {Describe(hello)}: game version is {hello.GameVersion}, room requires {version}");
                throw new NetworkException("GameVersionMismatch", $"Game version mismatch: server requires exactly {version}.");
            }
            if (!string.Equals(modHash, hello.ModHash, StringComparison.Ordinal))
            {
                // A refusal used to leave no trace, so "someone cannot join" could not be diagnosed from the
                // server side at all. Name the peer and both fingerprints; the client writes the per-file
                // breakdown next to its own copy, so these two lines plus that file identify the culprit.
                Console.WriteLine($"[server] rejected {Describe(hello)}: mod fingerprint is {hello.ModHash}, room requires {modHash}");
                throw new NetworkException("ModHashMismatch", "Simulation assemblies, XML, mod order or configuration differ from the server.");
            }
            if (_lockedGameVersion is null || _lockedModHash is null)
            {
                await SaveStateAsync(_policy, version, modHash, ct).ConfigureAwait(false);
                Volatile.Write(ref _lockedGameVersion, version);
                Volatile.Write(ref _lockedModHash, modHash);
            }
            peer.IsSimulation = isSimulation;
            peer.CanEdit = !isSimulation && (_options.EveryoneCanEdit || _controllerId is null);
            if (!isSimulation && !_options.EveryoneCanEdit && _controllerId is null) _controllerId = peer.Id;
            peer.PlayerName = hello.PlayerName;
            peer.ColorIndex = PresenceRules.ColorForName(hello.PlayerName);
            // 稳定身份：服务端按 token 认人。缺失（老客户端/首次加入）或没见过 ⇒ 当新玩家，发 32 字节随机
            // token（`RandomNumberGenerator`）。不用 Steam ID：headless 客户端不一定有 Steam 会话。
            // 常驻仿真端不是玩家：它不占玩家记录，也不发 token。
            if (!isSimulation)
            {
                var offered = message.PlayerToken;
                var saveHash = CurrentSaveHash;
                var recognized = SessionTokens.IsValid(offered)
                    && (_issuedTokens.Contains(saveHash + "|" + offered) || Views.TryGet(saveHash, offered!, out _));
                peer.PlayerToken = recognized ? offered! : SessionTokens.New();
                if (!recognized) _issuedTokens.Add(saveHash + "|" + peer.PlayerToken);
                Console.WriteLine(recognized
                    ? $"[server] peer {peer.Id} '{hello.PlayerName}'：认出老玩家 token={SessionTokens.Short(peer.PlayerToken)}"
                        + $"（存档 {Short(saveHash)} 下共 {Views.Count(saveHash)} 条视图记录）"
                    : $"[server] peer {peer.Id} '{hello.PlayerName}'：新玩家，发放 token={SessionTokens.Short(peer.PlayerToken)}"
                        + $"（客户端带来的 token：{(string.IsNullOrEmpty(offered) ? "无" : SessionTokens.Short(offered))}）");
            };
            if (isSimulation)
            {
                Volatile.Write(ref _simulationProgress, null);
                Volatile.Write(ref _simulationPeer, peer);
                _checkpointOwnerId = peer.Id;
            }
            else if (_checkpointOwnerId is null && !_options.RequireSimulation) _checkpointOwnerId = peer.Id;
            // A coordinator has no colony to save, so the checkpoint duty has to live on a player. The host
            // is the natural owner: it is the peer that published the world and the one that keeps editing
            // authority. Without this the room would run with nobody able to checkpoint, and a restart would
            // resume from the initial publication only.
            else if (_checkpointOwnerId is null && _options.Lockstep && !_options.RequireSimulation && _hostId == peer.Id)
                _checkpointOwnerId = peer.Id;
            peer.Queue(new WireMessage
            {
                Type = "Welcome", Policy = _policy, CanEdit = peer.CanEdit, IsHost = _hostId == peer.Id,
                GameVersion = version, ModHash = modHash, ClientVersion = NetworkProtocol.ClientVersion,
                // The client cannot act on "hashes differ" alone. Sending the list is what turns the refusal into
                // something the player can fix: install these, order them like this.
                Mods = Volatile.Read(ref _lockedMods),
                CheckpointOwner = _checkpointOwnerId == peer.Id,
                PlayerId = peer.Id, Players = CapturePlayers(peer), Epoch = Epoch,
                // 客户端把它写进 `<profile>\Config\TurboTick\player-token.txt`，下次握手带回来。
                PlayerToken = peer.PlayerToken
            });
            peer.Joined = true; // No other broadcast can overtake Welcome on this peer's FIFO.
            // Lockstep must not wait for a report that predates the join. Seed at the tick the peer will
            // actually load, not at the current tick: seeding at Tick let the timeline run on while the
            // newcomer was still reloading the checkpoint, so it then had to replay thousands of ticks at
            // several hundred per second - the visible fast-forward. Seeding at the checkpoint keeps the
            // backlog at the handful of ticks that accrued after it.
            if (_options.Lockstep)
                _peerAppliedTick[peer.Id] = _checkpointManifest is not null && CheckpointTick > 0
                    ? Math.Min(Tick, CheckpointTick)
                    : Tick;
            if (isSimulation) BroadcastRoles();
            else BroadcastPlayers();
            // Do not replay pointer history to a joiner. Each peer sends a one-second keepalive.
            // A peer arriving after the start waits for a fresh checkpoint: replaying thousands of ticks
            // would re-simulate the colony and diverge. The owner is asked for a snapshot at its current tick.
            if (GameRunning && isSimulation)
                SendCatchUp(peer); // The resident simulation must not wait for a checkpoint from itself.
            else if (GameRunning)
            {
                if (_options.Authoritative)
                {
                    // 权威房里的显示副本靠这一份存档起来：本地没有任何存档的客户端也必须能加入。
                    // 基线走推送而不是「等检查点」——检查点要权威端在游戏线程上存一次全图（18 MB / 几百毫秒），
                    // 而房间本来就持有那份存档的字节。WorldReady 之前这个 peer 一帧都收不到（见 BroadcastFrame），
                    // 否则它会一边下载一边把增量帧应用到一张还没载入的地图上，于是永久丢掉
                    // 「存档点 -> 现在」之间所有**只发生一次**的创建（增量帧只在首次出现时发 Created）。
                    await SendBaselineLockedAsync(peer).ConfigureAwait(false);
                }
                else
                {
                    lock (_pendingJoiners) _pendingJoiners[peer.Id] = Environment.TickCount64;
                    Console.WriteLine($"[server] peer {peer.Id} waiting for a fresh checkpoint");
                    if (_checkpointOwnerId is { } owner && owner != peer.Id
                        && _peers.TryGetValue(owner, out var ownerPeer) && ownerPeer.Joined && !HistoryTruncated)
                        ownerPeer.TryQueue(new WireMessage { Type = "CheckpointNeeded" });
                    else
                    {
                        // SendCatchUp is delivered here and now, so the peer must not also stay on the waiting
                        // list: ExpirePendingJoiners would resend it ten seconds later and the client would
                        // reload the colony a second time. That second load is what made a room look like it
                        // kept re-entering itself.
                        lock (_pendingJoiners) _pendingJoiners.Remove(peer.Id);
                        SendCatchUp(peer); // first peer, no owner, or an incomplete history it must be told about
                    }
                }
            }
        }
        finally { _stateGate.Release(); }
    }

    private async Task DispatchAsync(Peer peer, WireMessage message)
    {
        switch (message.Type)
        {
            case "Ping": peer.Queue(new WireMessage { Type = "Pong", RequestId = message.RequestId }); return;
            case "Pong":
                if (message.RequestId > 0 && message.RequestId == Interlocked.Read(ref peer.LastPingSent))
                {
                    await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
                    try
                    {
                        RequireCurrentPeer(peer);
                        peer.LatencyMilliseconds = (int)Math.Clamp(Environment.TickCount64 - message.RequestId, 0, 60_000);
                        BroadcastPlayers();
                    }
                    finally { _stateGate.Release(); }
                }
                return;
            case "Presence":
                await WithRoomGateAsync(peer, () => RelayPresence(peer, message)).ConfigureAwait(false);
                return;
            case "ViewState":
                // 会话视图：不是命令、不进命令历史、不影响模拟。它只是「这个人现在在看哪里」的一条最新记录。
                await WithRoomGateAsync(peer, () => StoreView(peer, message)).ConfigureAwait(false);
                return;
            case "ClaimHost":
                RequireRequestId(message);
                await ClaimHostAsync(peer, message).ConfigureAwait(false);
                return;
            case "SetSpeed":
                RequireRequestId(message);
                await SetSpeedAsync(peer, message).ConfigureAwait(false);
                return;
            case "SetTimeSpeed":
                RequireRequestId(message);
                await SetTimeSpeedAsync(peer, message).ConfigureAwait(false);
                return;
            case "GetSnapshotManifest":
                RequireRequestId(message);
                try
                {
                    var manifest = await Snapshots.GetLatestAsync(peer.Token).ConfigureAwait(false);
                    peer.Queue(new WireMessage { Type = "SnapshotManifest", RequestId = message.RequestId, Manifest = manifest });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { peer.Queue(WireMessage.Error("SnapshotUnavailable", "The server snapshot is missing or invalid.", message.RequestId)); }
                return;
            case "GetSnapshotChunk":
                RequireRequestId(message);
                try
                {
                    var chunk = await Snapshots.ReadChunkAsync(message.SnapshotId!, message.ChunkIndex, peer.Token).ConfigureAwait(false);
                    peer.Queue(new WireMessage { Type = "SnapshotChunk", RequestId = message.RequestId, Chunk = chunk });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { peer.Queue(WireMessage.Error("InvalidSnapshotChunk", "The requested snapshot id or chunk is missing, out of bounds, or corrupt.", message.RequestId)); }
                return;
            case "GetWorldManifest":
                RequireRequestId(message);
                peer.Queue(new WireMessage { Type = "WorldManifest", RequestId = message.RequestId, Manifest = WorldManifest });
                return;
            case "PublishWorldChunk":
                RequireRequestId(message);
                await PublishWorldChunkAsync(peer, message).ConfigureAwait(false);
                return;
            case "CommitWorld":
                RequireRequestId(message);
                await CommitWorldAsync(peer, message).ConfigureAwait(false);
                return;
            case "StartGame":
                RequireRequestId(message);
                await StartGameAsync(peer, message).ConfigureAwait(false);
                return;
            case "Command":
                NoteReceived(peer, "Command");
                await RelayCommandAsync(peer, message).ConfigureAwait(false);
                return;
            case "ReportHash":
                await WithRoomGateAsync(peer, () => ReportHash(peer, message)).ConfigureAwait(false);
                return;
            case "ReportProgress":
                // -1 is the sentinel for "loading a world", not a tick: a peer that is loading must not pace the room.
                if (peer.Joined && message.Tick >= -1) ReportPeerProgress(peer, message.Tick);
                return;
            case "WorldReady":
                await WorldReadyAsync(peer, message).ConfigureAwait(false);
                return;
            default: throw new NetworkException("UnknownMessage", "This message is not supported.");
        }
    }

    // The simulation's own Welcome includes its identity to satisfy DedicatedClient's handshake;
    // every public roster, including the immediately following Players frame, hides it.
    private PlayerInfo[] CapturePlayers(Peer? joining = null) => _peers.Values.Where(p =>
        (p.Joined || ReferenceEquals(p, joining)) && (!p.IsSimulation || ReferenceEquals(p, joining)) && !p.Token.IsCancellationRequested)
        .OrderBy(p => p.Id).Select(p => new PlayerInfo(p.Id, p.PlayerName, p.ColorIndex, p.CanEdit,
            _checkpointOwnerId == p.Id, p.LatencyMilliseconds, _hostId == p.Id)).ToArray();

    /// <summary>Display replicas follow the host's simulation instead of running their own. The frame is
    /// never echoed back to the simulation peer, which already owns that state.
    ///
    /// 权威房里还要等 <c>WorldReady</c>：帧是**增量**（只发变化量 + 首次出现时的 Created），
    /// 一个还没载入基线的副本收到增量帧，会把它应用到一张旧地图上，并且永久丢掉
    /// 「存档点 -> 现在」之间所有只发生一次的创建。基线没完成就发帧，画面必然错位。</summary>
    /// <summary>一个刚载入完基线的 peer 需要的补发内容：间隙里新建的物体与销毁的物体 id。</summary>
    public readonly record struct FrameCatchUp(IReadOnlyList<ReplicaCreation>? Created, IReadOnlyList<int>? Removed);

    public int BroadcastFrame(AuthoritativeFrame frame, IReadOnlyDictionary<long, FrameCatchUp>? catchUps = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var message = FrameCodec.Pack(new WireMessage { Type = "Frame", Frame = frame, Epoch = Epoch, Tick = frame.Tick });
        var sent = 0;
        foreach (var recipient in _peers.Values)
        {
            if (!recipient.Joined || recipient.IsSimulation) continue;
            if (_options.Authoritative && !recipient.WorldReady) continue;
            // 刚载入完基线的 peer 多拿一份「间隙补发」。必须**合进同一帧**而不是再发一帧：
            // 副本按 tick 去重（`frame.Tick <= LastFrameTick` 丢弃），同 tick 的第二帧会被丢掉，
            // 于是补发和主帧恰好二选一失败。
            if (catchUps is not null && catchUps.TryGetValue(recipient.Id, out var extra)
                && (extra.Created is { Count: > 0 } || extra.Removed is { Count: > 0 }))
            {
                var merged = frame with
                {
                    Created = Merge(frame.Created, extra.Created),
                    Removed = Merge(frame.Removed, extra.Removed)
                };
                recipient.Queue(FrameCodec.Pack(new WireMessage { Type = "Frame", Frame = merged, Epoch = Epoch, Tick = merged.Tick }));
                sent++;
                continue;
            }
            recipient.Queue(message);
            sent++;
        }
        return sent;
    }

    private static IReadOnlyList<T>? Merge<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second)
    {
        if (second is not { Count: > 0 }) return first;
        if (first is not { Count: > 0 }) return second;
        var merged = new List<T>(first.Count + second.Count);
        merged.AddRange(first);
        merged.AddRange(second);
        return merged;
    }

    /// <summary>一个 peer 载入基线后需要的补发：从哪个 id 水位、从哪个 tick 开始。</summary>
    public readonly record struct CatchUpRequest(long PeerId, int FromThingId, int FromTick);

    private readonly object _catchUpGate = new();
    private readonly List<CatchUpRequest> _catchUps = new();

    /// <summary>游戏线程取走待补发请求。取走即消费：补发清单由权威端在下一帧内构建。</summary>
    public CatchUpRequest[] TakeCatchUps()
    {
        lock (_catchUpGate)
        {
            if (_catchUps.Count == 0) return [];
            var requests = _catchUps.ToArray();
            _catchUps.Clear();
            return requests;
        }
    }

    private void EnqueueCatchUp(long peerId, int fromThingId, int fromTick)
    {
        lock (_catchUpGate)
        {
            _catchUps.Add(new CatchUpRequest(peerId, fromThingId, fromTick));
            // 有界：一条载入失败的连接不该在游戏线程上留垃圾。
            if (_catchUps.Count > 32) _catchUps.RemoveAt(0);
        }
    }

    private readonly object _authorityGate = new();
    private readonly List<GameCommand> _authorityCommands = new();

    /// <summary>游戏线程取走需要权威进程自己应用的中继命令。取走即消费。</summary>
    /// <remarks>
    /// 权威房间里没有任何 peer 会执行发起者的命令：发起端的本地赋值已被捕获取消（改成命令），而中继刻意
    /// 跳过发起者（<c>recipient.Id != peer.Id</c>），权威进程自己又不调用 <c>SharedActions.Apply</c>。
    /// 单人房间就是这条链的极端情形——命令被接受、被记录、被计入 <c>commands=…</c>，然后没有任何执行者。
    /// 实测：客户端探针连续 21 轮各发出 1 条 <c>forbid-batch</c>，服务端 <c>commands=forbid-batch:21/0</c>
    /// 零拒绝，副本的禁止标记一次都没变。
    /// </remarks>
    public GameCommand[] TakeAuthorityCommands()
    {
        lock (_authorityGate)
        {
            if (_authorityCommands.Count == 0) return [];
            var commands = _authorityCommands.ToArray();
            _authorityCommands.Clear();
            return commands;
        }
    }

    private void EnqueueAuthorityCommand(GameCommand command)
    {
        lock (_authorityGate)
        {
            _authorityCommands.Add(command);
            // 有界：游戏线程卡在一段长载入里时，中继线程不该把内存吃光。
            if (_authorityCommands.Count > 4096)
            {
                _authorityCommands.RemoveAt(0);
                AuthorityDropped++;
            }
        }
    }

    /// <summary>被上限挤掉的命令数。非零意味着权威端曾经落后到无法执行每一个手势。</summary>
    public static int AuthorityDropped { get; private set; }

    /// <summary>把房间的权威存档整份推给一个刚连上的显示副本。
    ///
    /// 用存档文件而不是逐格地形：<c>.rws</c> 就是完整的地图序列化（地形/屋顶/区域/山体/世界对象），
    /// 由游戏自己的 <c>GameDataSaveLoader</c> 载入，一致性由 Scribe 保证——自造地形协议既会漏字段，
    /// 重建 Map 在 Waylith 下也极容易炸。
    ///
    /// 调用者持有 <c>_stateGate</c>；这里只排队消息，真正的写出由 peer 自己的 writer 任务做。</summary>
    private async Task SendBaselineLockedAsync(Peer peer)
    {
        var world = CheckpointManifest ?? WorldManifest;
        if (world is null)
        {
            peer.Queue(WireMessage.Error("WorldMissing", "服务器还没有权威存档：权威端尚未发布世界，请稍后重连。"));
            Console.WriteLine($"[server] 基线下发失败 -> peer {peer.Id}: 房间没有世界快照");
            return;
        }
        byte[] data;
        try { data = await SaveBytesAsync(world, peer.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            peer.Queue(WireMessage.Error("SnapshotUnavailable", "服务器上的权威存档读不出来：" + ex.Message));
            Console.WriteLine($"[server] 基线下发失败 -> peer {peer.Id}: {ex.GetType().Name} {ex.Message}");
            return;
        }
        var count = (data.Length + NetworkProtocol.SaveChunkSize - 1) / NetworkProtocol.SaveChunkSize;
        peer.BaselineId = world.Id;
        peer.BaselineManifest = world;
        peer.BaselineTick = CheckpointTick;
        Interlocked.Exchange(ref peer.BaselineStartedAt, Environment.TickCount64);
        peer.Queue(new WireMessage
        {
            Type = "SaveStart", SaveId = world.Id, SaveName = Short(world.Id), Manifest = world,
            // Tick 给的是**存档点**而不是当前 tick：副本的 Barrier 从这一 tick 起算，
            // 后面靠帧推进。给当前 tick 会让副本以为自己已经追平（其实落后几百 tick）。
            Epoch = Epoch, Tick = CheckpointTick, SnapshotTick = CheckpointTick, SaveCount = count,
            Seed = Volatile.Read(ref _seed), Authoritative = true,
            RoomAuthoritative = _options.Authoritative, Lockstep = _options.Lockstep
        });
        for (var index = 0; index < count; index++)
        {
            var offset = index * NetworkProtocol.SaveChunkSize;
            var length = Math.Min(NetworkProtocol.SaveChunkSize, data.Length - offset);
            var slice = new byte[length];
            Buffer.BlockCopy(data, offset, slice, 0, length);
            peer.Queue(new WireMessage
            {
                Type = "SaveChunk", SaveId = world.Id, SaveIndex = index, SaveCount = count,
                Sha256 = world.Id, SaveBytes = slice
            });
        }
        Console.WriteLine($"[server] 基线下发 -> peer {peer.Id} '{peer.PlayerName}': {data.Length} 字节 / {count} 块 "
            + $"（{NetworkProtocol.SaveChunkSize / 1024} KB/块）/ sha={Short(world.Id)} / 存档点 tick={CheckpointTick}");
    }

    /// <summary>同一份存档会被每个新副本各要一次；按内容 id 缓存，房间换代时旧 id 自然不会被问到。</summary>
    private async Task<byte[]> SaveBytesAsync(SnapshotManifest manifest, CancellationToken ct)
    {
        lock (_saveCacheGate)
            if (_saveCache is { } cached && string.Equals(cached.Id, manifest.Id, StringComparison.Ordinal))
                return cached.Bytes;
        var bytes = await Snapshots.ReadAllAsync(manifest.Id, ct).ConfigureAwait(false);
        lock (_saveCacheGate) _saveCache = (manifest.Id, bytes);
        return bytes;
    }

    private readonly object _saveCacheGate = new();
    private (string Id, byte[] Bytes)? _saveCache;

    /// <summary>The client finished loading the baseline. Only now may it receive frames.</summary>
    private async Task WorldReadyAsync(Peer peer, WireMessage message)
    {
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (peer.IsSimulation) return;
            var expected = peer.BaselineId ?? CheckpointManifest?.Id ?? WorldManifest?.Id;
            if (message.SaveId is { Length: > 0 } claimed && expected is { } want
                && !string.Equals(claimed, want, StringComparison.Ordinal))
            {
                // 副本载入的不是本房间的存档：它看到的画面和权威不是同一个世界，发帧只会让它更错。
                var text = $"副本载入的世界 {Short(claimed)} 不是本房间的 {Short(want)}，拒绝开帧。";
                peer.Queue(WireMessage.Error("BaselineMismatch", text));
                Console.WriteLine($"[server] WorldReady 拒绝 -> peer {peer.Id}: {text}");
                return;
            }
            var first = !peer.WorldReady;
            peer.WorldReady = true;
            // 帧是增量：「存档点 -> 现在」之间新建的物体不会被任何一帧再发一次（Created 只在首次出现时发），
            // 销毁的物体同理。记下副本的水位，由权威端在下一帧里只给这个 peer 补一份。
            if (message.NextThingId > 0) EnqueueCatchUp(peer.Id, message.NextThingId, message.Tick);
            if (!first) return;
            var elapsed = Interlocked.Read(ref peer.BaselineStartedAt) is var started and > 0
                ? Environment.TickCount64 - started : -1;
            Console.WriteLine($"[server] WorldReady -> peer {peer.Id} '{peer.PlayerName}': 世界 {Short(expected ?? "")} 已载入"
                + (elapsed >= 0 ? $"（基线 {elapsed} ms）" : "（由 Started 路径载入）") + "；开始发帧");
            // 载入完成之后才补 epoch/命令历史：副本的 Barrier 从存档点起算，只补存档点之后的命令。
            if (!peer.CatchUpSent) { peer.CatchUpSent = true; SendCatchUp(peer); }
            // 此刻客户端已经载入完基线与世界，才轮到「他在哪张地图/选了谁/相机在哪」。
            SendViewRestoreLocked(peer);
        }
        finally { _stateGate.Release(); }
    }

    private static string Short(string id) => id.Length <= 16 ? id : id[..16];

    /// <summary>当前房间的会话键（sha256，64 位小写十六进制）。
    ///
    /// 用**这一局的初始世界**（`WorldManifest`）而不是最新检查点：检查点每次发布都是一个新存档
    /// （实测同一局里 01bb… → 3975…），拿它当键会让会话记录每几分钟就换一个文件、玩家一重连就「没有记录」。
    /// 换一局（host 重新发布世界）才是真的换键——那本来就该按新的一局另起一份。</summary>
    private string CurrentSaveHash => WorldManifest?.Id ?? CheckpointManifest?.Id ?? "no-world";

    /// <summary>客户端防抖后的视图上报：哪张地图、选中了谁、相机在哪。每玩家只留最新一条（覆盖写）。</summary>
    private void StoreView(Peer peer, WireMessage message)
    {
        if (message.View is not { } reported) return;
        if (peer.IsSimulation || peer.PlayerToken is not { } token) return;
        var view = SessionViewSnapshot.Sanitize(reported);
        var hash = CurrentSaveHash;
        var record = Views.Save(hash, token, peer.PlayerName, view);
        if (record is null) return;
        var previous = peer.LastView;
        peer.LastView = view;
        // 客户端已经把上报防抖到 500 ms；这里再按「实质变化」过滤一次，长局里不会把日志刷成噪声。
        if (previous is not null && SameView(previous, view)) return;
        Console.WriteLine($"[server] 视图状态 <- peer {peer.Id} '{peer.PlayerName}' token={SessionTokens.Short(token)}"
            + $"：mapId={view.MapId} selected=[{string.Join(",", view.Selected)}]"
            + $" camera=({view.X:0.00},{view.Z:0.00}) size={view.Size:0.00}"
            + $" -> {Path.GetFileName(Views.PathFor(hash))}");
    }

    /// <summary>相机抖动容差：半格、0.05 缩放以内算同一个视图。</summary>
    private static bool SameView(SessionViewSnapshot left, SessionViewSnapshot right) =>
        left.MapId == right.MapId
        && left.Selected.SequenceEqual(right.Selected)
        && Math.Abs(left.X - right.X) < 0.5f
        && Math.Abs(left.Z - right.Z) < 0.5f
        && Math.Abs(left.Size - right.Size) < 0.05f;

    /// <summary>`WorldReady` 之后下发这个 token 的记录**一次**。
    ///
    /// 没有记录就不发：换了 token（删掉文件就是另一个人）的人必须停在默认视图，而不是被带到别人的相机位置。
    /// 这里只排队一条消息，真正的切图/选中/相机在客户端的游戏线程上做。</summary>
    private void SendViewRestoreLocked(Peer peer)
    {
        if (peer.PlayerToken is not { } token) return;
        var hash = CurrentSaveHash;
        if (!Views.TryGet(hash, token, out var record))
        {
            Console.WriteLine($"[server] 视图恢复 -> peer {peer.Id} '{peer.PlayerName}' token={SessionTokens.Short(token)}："
                + "没有记录，停在默认视图（新玩家或换了 token）");
            return;
        }
        var view = new SessionViewSnapshot(record.MapId, record.Selected, record.Camera.X, record.Camera.Z, record.Camera.Size);
        peer.Queue(new WireMessage { Type = "ViewRestore", View = view });
        Console.WriteLine($"[server] 视图恢复 -> peer {peer.Id} '{peer.PlayerName}' token={SessionTokens.Short(token)}："
            + $"mapId={record.MapId} selected=[{string.Join(",", record.Selected)}]"
            + $" camera=({record.Camera.X:0.00},{record.Camera.Z:0.00}) size={record.Camera.Size:0.00}"
            + $"（记录于 {record.UpdatedUtc}，文件 {Path.GetFileName(Views.PathFor(hash))}）");
    }


    // Roster changes are serialized by _stateGate; pointer packets never enter _commandGate/history.
    private void BroadcastPlayers()    {
        var message = new WireMessage { Type = "Players", Players = CapturePlayers() };
        foreach (var recipient in _peers.Values)
            if (recipient.Joined) recipient.Queue(message);
    }

    private void RefreshEditingRoles()
    {
        foreach (var peer in _peers.Values)
            if (peer.Joined) peer.CanEdit = !peer.IsSimulation && (_options.EveryoneCanEdit || _controllerId == peer.Id);
    }

    // Caller holds _stateGate. Role acknowledgements precede the corresponding roster/RPC response.
    private void BroadcastRoles()
    {
        foreach (var recipient in _peers.Values)
            if (recipient.Joined)
            {
                recipient.Queue(new WireMessage { Type = "Role", CanEdit = recipient.CanEdit, IsHost = _hostId == recipient.Id });
                recipient.Queue(new WireMessage { Type = "CheckpointRole", CheckpointOwner = _checkpointOwnerId == recipient.Id });
            }
        BroadcastPlayers();
    }

    private async Task ClaimHostAsync(Peer peer, WireMessage message)
    {
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (peer.IsSimulation)
            {
                peer.Queue(WireMessage.Error("SimulationReadOnly", "The process simulation has no player hosting role.", message.RequestId));
                return;
            }
            if (_hostId is not null && _hostId != peer.Id)
            {
                peer.Queue(WireMessage.Error("HostOccupied", "Another player already hosts this room.", message.RequestId));
                return;
            }
            _hostId = peer.Id;
            // Hosting and checkpoint duties remain distinct. Controller-only rooms have one editor:
            // an explicit host claim transfers that editing authority instead of creating a second one.
            if (!_options.EveryoneCanEdit) _controllerId = peer.Id;
            RefreshEditingRoles();
            if (!GameRunning && !_options.RequireSimulation && _simulationPeer is null) _checkpointOwnerId = peer.Id;
            // Claiming the host role also takes the checkpoint duty in a coordinator room, so the peer that
            // owns the world is the one that keeps saving it.
            else if (_checkpointOwnerId is null && _options.Lockstep && !_options.RequireSimulation) _checkpointOwnerId = peer.Id;
            BroadcastRoles();
            peer.Queue(new WireMessage { Type = "HostClaimed", RequestId = message.RequestId, IsHost = true });
        }
        finally { _stateGate.Release(); }
    }

    private bool CanUpload(Peer peer, WireMessage message)
    {
        if (message.Checkpoint ? _checkpointOwnerId != peer.Id : _hostId != peer.Id)
        {
            peer.Queue(WireMessage.Error(message.Checkpoint ? "NotCheckpointOwner" : "HostOnly",
                message.Checkpoint ? "Only the checkpoint owner may publish checkpoints." : "Only the room host may publish the initial world.", message.RequestId));
            return false;
        }
        if (message.Epoch != Epoch)
        {
            peer.Queue(WireMessage.Error("SessionChanged", "The upload belongs to an earlier world epoch.", message.RequestId));
            return false;
        }
        if (message.Checkpoint && (!GameRunning || message.Tick < 0 || message.Tick > Tick))
        {
            peer.Queue(WireMessage.Error("InvalidCheckpoint", "The checkpoint tick must be inside the running session.", message.RequestId));
            return false;
        }
        return true;
    }

    private void RelayPresence(Peer peer, WireMessage message)
    {
        if (peer.IsSimulation) return;
        var state = message.Presence;
        if (!PresenceRules.IsValid(state))
        {
            peer.TryQueue(WireMessage.Error("InvalidPresence", "Presence requires a finite map position and known activity."));
            return;
        }
        // Epoch and identity come from the live connection. A stale world sample is simply discarded.
        if (state!.Epoch != Epoch || !peer.TryConsumePresenceBudget()) return;
        var relayed = new WireMessage { Type = "Presence", PlayerPresence = new PlayerPresence(peer.Id, state) };
        foreach (var recipient in _peers.Values)
            if (recipient.Joined && recipient.Id != peer.Id) recipient.TryQueue(relayed);
    }

    private async Task PublishWorldChunkAsync(Peer peer, WireMessage message)
    {
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (!CanUpload(peer, message)) return;
            var uploadId = message.UploadId;
            if (string.IsNullOrWhiteSpace(uploadId) || uploadId.Length > 64 || uploadId.Any(char.IsControl))
            { peer.Queue(WireMessage.Error("InvalidUpload", "A printable upload id is required.", message.RequestId)); return; }
            var data = message.WorldChunk;
            if (message.ChunkIndex < 0 || data is null || data.Length is < 1 or > NetworkProtocol.SnapshotChunkSize)
            { peer.Queue(WireMessage.Error("InvalidUpload", "The world chunk index or size is invalid.", message.RequestId)); return; }
            var upload = _worldUploads.GetOrAdd((peer.Id, uploadId), _ => new WorldUpload(message.Checkpoint, message.Epoch, message.Tick));
            if (upload.Checkpoint != message.Checkpoint || upload.Epoch != message.Epoch || upload.Tick != message.Tick)
            { peer.Queue(WireMessage.Error("InvalidUpload", "An upload cannot change its purpose, epoch or checkpoint tick.", message.RequestId)); return; }
            upload.Chunks[message.ChunkIndex] = data;
            peer.Queue(new WireMessage { Type = "WorldChunkAccepted", RequestId = message.RequestId, ChunkIndex = message.ChunkIndex });
        }
        finally { _stateGate.Release(); }
    }

    private async Task CommitWorldAsync(Peer peer, WireMessage message)
    {
        // Keep permission checks and publication atomic with role/epoch changes, including disk work.
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (!CanUpload(peer, message)) return;
            if (string.IsNullOrWhiteSpace(message.UploadId) || !_worldUploads.TryRemove((peer.Id, message.UploadId), out var upload) || upload.Chunks.Count == 0)
            { peer.Queue(WireMessage.Error("WorldMissing", "There is no matching upload owned by this connection.", message.RequestId)); return; }
            if (upload.Checkpoint != message.Checkpoint || upload.Epoch != message.Epoch || upload.Tick != message.Tick ||
                message.ChunkCount != upload.Chunks.Count)
            { peer.Queue(WireMessage.Error("InvalidUpload", "The upload purpose, epoch, tick or chunk count changed.", message.RequestId)); return; }
            var chunks = upload.Chunks;
            using var buffer = new MemoryStream();
            for (var index = 0; index < chunks.Count; index++)
            {
                if (!chunks.TryGetValue(index, out var chunk))
                { peer.Queue(WireMessage.Error("InvalidUpload", "The world upload has a gap.", message.RequestId)); return; }
                buffer.Write(chunk);
            }
            buffer.Position = 0;
            var manifest = await Snapshots.PutAsync(buffer, peer.Token).ConfigureAwait(false);
            if (message.Sha256 is { Length: > 0 } expected && !string.Equals(expected, manifest.Id, StringComparison.Ordinal))
            { peer.Queue(WireMessage.Error("WorldHashMismatch", "The server computed a different world hash.", message.RequestId)); return; }
            if (message.Checkpoint)
            {
                await ApplyCheckpointAsync(peer, message, manifest).ConfigureAwait(false);
                return;
            }
            Volatile.Write(ref _worldManifest, manifest);
            foreach (var recipient in _peers.Values)
                if (recipient.Joined) recipient.Queue(new WireMessage { Type = "World", Manifest = manifest });
            peer.Queue(new WireMessage { Type = "WorldCommitted", RequestId = message.RequestId, Manifest = manifest });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { peer.Queue(WireMessage.Error("WorldStoreFailed", "The world snapshot could not be stored.", message.RequestId)); }
        finally { _stateGate.Release(); }
    }

    /// <summary>
    /// A checkpoint is a fresh snapshot taken at a known tick by the designated owner. Everything older
    /// than it is dropped from the history, so joins replay only the delta instead of the whole session.
    /// </summary>
    private async Task ApplyCheckpointAsync(Peer peer, WireMessage message, SnapshotManifest manifest)
    {
        if (_checkpointOwnerId != peer.Id)
        {
            peer.Queue(WireMessage.Error("NotCheckpointOwner", "Only the checkpoint owner may publish checkpoints.", message.RequestId));
            return;
        }
        if (!GameRunning || message.Tick < 0 || message.Tick > Tick)
        {
            peer.Queue(WireMessage.Error("InvalidCheckpoint", "The checkpoint tick must be inside the running session.", message.RequestId));
            return;
        }
        // Trim inside the command gate so a command cannot slip in between the trim and the broadcast.
        lock (_commandGate)
        {
            Volatile.Write(ref _worldManifest, manifest);
            Volatile.Write(ref _checkpointManifest, manifest);
            Volatile.Write(ref _checkpointTick, message.Tick);
            Console.WriteLine($"[server] checkpoint accepted at tick {message.Tick} ({manifest.Id[..8]})");
            lock (_history)
            {
                _history.RemoveAll(command => command.Tick <= message.Tick);
                _historyTruncated = false;
            }
        }
        foreach (var recipient in _peers.Values)
            if (recipient.Joined) recipient.Queue(new WireMessage { Type = "Checkpoint", Tick = message.Tick, Manifest = manifest });
        peer.Queue(new WireMessage { Type = "WorldCommitted", RequestId = message.RequestId, Manifest = manifest });
        // Anyone waiting to join can now start from this snapshot instead of replaying the gap.
        List<Peer> waiting;
        lock (_pendingJoiners)
        {
            waiting = [];
            foreach (var id in _pendingJoiners.Keys)
                if (_peers.TryGetValue(id, out var joiner) && joiner.Joined) waiting.Add(joiner);
            _pendingJoiners.Clear();
        }
        // A display replica loads its own copy of the snapshot and then follows frames; it never reports
        // progress, so holding the resident for it would stall the world forever.
        //
        // A lockstep room needs the same canonical load point even though no resident simulation exists.
        // Verse snapshots do not serialize every transient cache, so a host that has been simulating since
        // world creation and a peer that just deserialized the checkpoint disagree even with matching RNG
        // and IDs. Without this the two drift apart within a few hundred ticks of a join - observed at tick
        // 960 against a checkpoint at 795 - with nobody operating anything.
        if (!_options.Authoritative && waiting.Count > 0 && (_options.RequireSimulation || _options.Lockstep))
        {
            Volatile.Write(ref _simulationProgress, null);
            lock (_hashGate)
            {
                _firstHashAtTick.Clear();
                _firstCategoriesAtTick.Clear();
                _hashByPeerTick.Clear();
                _firstPeerAtTick.Clear();
                _reportedDesyncTicks.Clear();
                _hashWindowFloor = message.Tick;
            }
            Volatile.Write(ref _requiredSimulationWorldId, manifest.Id);
            // Everyone is about to jump back to the checkpoint, so nobody is at the current tick any more.
            // Leaving their old reports in place let the timeline keep running during the reload; the peers
            // then had to replay the whole gap at several hundred ticks per second, which the players saw
            // as the world fast-forwarding. Rewinding the reports holds the line at the checkpoint instead.
            foreach (var replica in _peers.Values)
                if (replica.Joined) _peerAppliedTick[replica.Id] = message.Tick;
            Console.WriteLine($"[server] canonical reload at tick {message.Tick}; every peer reloads so none keeps a live state");
            foreach (var replica in _peers.Values)
                if (replica.Joined) SendCatchUp(replica);
        }
        else foreach (var joiner in waiting) SendCatchUp(joiner);
        await SaveCurrentStateLockedAsync(peer.Token).ConfigureAwait(false);
    }

    private async Task StartGameAsync(Peer peer, WireMessage message)
    {
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (_hostId != peer.Id) { peer.Queue(WireMessage.Error("HostOnly", "Only the room host may start the shared colony.", message.RequestId)); return; }
            var world = WorldManifest;
            if (world is null) { peer.Queue(WireMessage.Error("WorldMissing", "Publish the world snapshot before starting.", message.RequestId)); return; }
            var epoch = Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _simulationProgress, null);
            Volatile.Write(ref _requiredSimulationWorldId, null);
            _worldUploads.Clear();
            Interlocked.Exchange(ref _tick, 0);
            Interlocked.Exchange(ref _commandSequence, 0);
            Volatile.Write(ref _seed, Random.Shared.Next());
            lock (_history) { _history.Clear(); _historyTruncated = false; }
            Volatile.Write(ref _checkpointManifest, world);
            Volatile.Write(ref _checkpointTick, 0);
            lock (_hashGate)
            {
                _firstHashAtTick.Clear();
                _firstCategoriesAtTick.Clear();
                _hashByPeerTick.Clear();
                _firstPeerAtTick.Clear();
                _hashWindowFloor = 0;
                _reportedDesyncTicks.Clear();
            }
            Volatile.Write(ref _gameRunning, 1);
            var seed = Volatile.Read(ref _seed);
            foreach (var recipient in _peers.Values)
                if (recipient.Joined)
                    recipient.Queue(new WireMessage { Type = "Started", Epoch = epoch, Tick = 0, WorldId = world.Id, Manifest = world, Seed = seed,
                        Authoritative = _options.Authoritative && !recipient.IsSimulation,
                        RoomAuthoritative = _options.Authoritative, Lockstep = _options.Lockstep });
            peer.Queue(new WireMessage { Type = "StartedAccepted", RequestId = message.RequestId, Epoch = epoch });
            await SaveCurrentStateLockedAsync(peer.Token).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    /// <summary>One line that identifies a refused peer without leaking anything: who, which build, which fingerprint.</summary>
    private static string Describe(ClientHello hello)
        => $"peer '{hello.PlayerName}' (client {hello.ClientVersion}, game {hello.GameVersion})";
    /// <summary>The command kinds a client is allowed to send.
    ///
    /// Networking cannot reference the Simulation project that owns the codec constants, so this list is
    /// duplicated by hand - and it silently drifted: "thing-state" was added to the codec and to the apply
    /// path but not here, so every generic gizmo change was rejected as UnknownCommand and the feature never
    /// worked. A test enumerates the codec constants against this set to keep that from happening again.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedKinds => KnownKinds;

    private static readonly HashSet<string> KnownKinds =
        new(StringComparer.Ordinal)
        {
            "designation", "remove-designation", "blueprint", "cancel-thing",
            "draft", "order", "stop", "transport", "forbid", "priority",
            // Loading a MapPortal. Separate from "transport" because the receiver is a different class with a
            // same-named method; the whitelist is what makes the server relay it at all.
            "portal",
            // An incident a player's own action queued (a requested trader, an orbital trader, military aid).
            // A replica runs no `IncidentQueueTick`, so without this the queue entry exists only on the clicker.
            "diplomacy",
            // A bulk forbid/unforbid: one gesture, one command. Without it a full-stockpile drag published one
            // `forbid` per stack and the per-peer budget refused the tail.
            "forbid-batch",
            "zone-cell", "zone-delete", "bill-add", "bill-delete", "area-assign",
            // A whole designator action: the level the building drag commits on, the unit Zetrith Multiplayer syncs,
            // and the only one where the client never allocates a thing id. Listed once - the literal was duplicated
            // here with two different comments, which a HashSet silently absorbs and which made the entry look like
            // two separate kinds when reading the list.
            "designator",
            // A whole cancel drag. Separate from "designator" because that kind is entity-lifecycle and is skipped by
            // a display replica, while a cancel only removes objects and clears marks no frame carries.
            "cancel-drag",
            // The generic gizmo fallback: a whole serialised thing or zone state.
            "thing-state",
            // The player's research choice; progress is simulated, the selection is input.
            "research",
            // A player decision that takes a pawn out of (or into) the faction: banish, recruit, release-to-wild.
            // These run from a confirmation dialog's callback, not from `Command.ProcessInput`, so the gizmo capture
            // never saw them - the gizmo that opens the dialog is what was covered for fifteen rounds.
            "pawn-leave",
            // A pawn's policy assignment (apparel / drugs / food / timetable). These panels have no gizmo, so the
            // generic fallback above never sees them.
            "policy",
            // One training-tab checkbox. Not a gizmo and not a designator: the widget is static
            // (`TrainingCardUtility.DoTrainableCheckbox` `:648116`), and its write is `SetWantedRecursive`, which also
            // walks the prerequisite tree - so the host runs the game's own method rather than receiving a field value.
            "training-wanted",
            // One surgery bill, whole. Its body part is assigned after `BillStack.AddBill` (`:644616-644623`), so a
            // deferred `bill-add` would arrive without the part and `CompletableEver` would be false on every peer.
            "surgery-bill",
            // The three `Dialog_ManageAreas` buttons (invert / clear / delete). These three are plain method calls from a
            // window. Painting cells is NOT a `designator`: `Designator_AreaAllowedExpand`/`Clear` are `Designator_Cells`
            // subclasses and `DesignatorSync.EncodeChunks` only ever encoded `Designator_Build` and `Designator_ZoneAdd`,
            // so a stroke on an allowed area published nothing at all until "area-paint" existed.
            "area-op",
            // One paint stroke on a player-created allowed area. Separate from "designator" for two reasons: the payload
            // names a label rather than a def, and "designator" is entity-lifecycle so a replica drops it - while a
            // stroke creates nothing and its bits are carried by no frame.
            "area-paint",
            // A whole `BillStack` from the bill settings dialog. State-only like "policy": the benches already exist and
            // the fragment rewrites their bills, so a replica can apply it.
            "bills",
            // The single `Storyteller` object from the storyteller page. It carries no identity of its own and there is
            // exactly one, so a replica applies it in place.
            "storyteller",
            // Which option the player clicked on a choice letter. The host runs the letter's own `DiaOption.action`,
            // so one kind covers all eleven `ChoiceLetter` subclasses.
            "letter-choice",
            // What the player picked in a growth-moment letter. A separate kind from "letter-choice" because
            // `ChoiceLetter_GrowthMoment` is not a `ChoiceLetter` and answers with skills and a trait, not an index.
            "growth-choice"
        };

    /// <summary>Per-kind counters for relayed and rejected commands.
    ///
    /// Measured need: a 190-cell stockpile drawn on one peer produced captured commands on that peer and nothing at
    /// all on the other, and there was no way to tell whether the server never received them, refused them, or
    /// relayed them into a peer that dropped them. These counters answer that from the server side, and are printed
    /// whenever a rejection happens so the first one is never silent.
    /// </summary>
    private static readonly Dictionary<string, (long Relayed, long Rejected)> KindCounters = new(StringComparer.Ordinal);

    /// <summary>Counts every inbound message type per peer.
    ///
    /// Measured need: a peer published 24 `zone-cell` commands, the send gate raised no drop, no send exception was
    /// thrown, and the server recorded nothing at all. "The server saw no command" and "the server saw a frame it did
    /// not understand" look identical without this counter, and so does "the frame never arrived".
    /// </summary>
    private readonly Dictionary<string, long> Received = new(StringComparer.Ordinal);

    private static bool firstCommandLogged;

    /// <summary>Faults that describe one bad message rather than a broken session.</summary>
    private static bool IsRecoverable(NetworkException fault) =>
        fault.Code is "UnknownMessage" or "InvalidCommand" or "InvalidFrameLength" or "UnknownRequest";

    private readonly Dictionary<string, long> Recovered = new(StringComparer.Ordinal);

    private void NoteRecoveredFault(Peer peer, NetworkException fault)
    {
        long count;
        lock (Recovered)
        {
            count = Recovered.TryGetValue(fault.Code, out var current) ? current + 1 : 1;
            Recovered[fault.Code] = count;
        }
        Console.WriteLine($"[server] 消息级故障已忽略 player#{peer.Id} code={fault.Code} 第{count}次：{fault.Message}");
    }

    /// <summary>Counts of message-level faults that were recovered from instead of ending the session.</summary>
    public string RecoveredCounters()
    {
        lock (Recovered)
        {
            if (Recovered.Count == 0) return "recovered=none";
            return "recovered=" + string.Join(",", Recovered.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value));
        }
    }
    private void NoteReceived(Peer peer, string type)
    {
        long count;
        lock (Received)
        {
            count = Received.TryGetValue(type, out var current) ? current + 1 : 1;
            Received[type] = count;
        }
        // The first command of a session is worth one line: it proves the inbound path works at all, and names which
        // peer sent it. Without it "the server received no commands" and "the server received no frames" are the same
        // observation.
        if (type is "Command" && !firstCommandLogged)
        {
            firstCommandLogged = true;
            // The count on this line is 1 by construction - it fires on the first one. It is a "the inbound path works
            // at all" marker, never a total. The running totals live on the periodic 状态 line, because reading a
            // by-construction 1 as "24 of 25 were lost" sent a whole round chasing a transport bug that is not there.
            Console.WriteLine($"[server] 首条 Command 到达 player#{peer.Id} joined={peer.Joined} "
                + $"remote={peer.Connection.Endpoint} running={GameRunning} tick={Tick} "
                + $"{FrameTrace.Counters()} {FrameTrace.Last(12)}");
        }
    }

    /// <summary>One line of inbound message counts, for the periodic status and for a manual check.</summary>
    public string ReceivedCounters()
    {
        lock (Received)
        {
            if (Received.Count == 0) return "received=none";
            return "received=" + string.Join(",", Received.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value));
        }
    }
    private static void NoteRelayed(string kind)
    {
        lock (KindCounters)
            KindCounters[kind] = KindCounters.TryGetValue(kind, out var pair)
                ? (pair.Relayed + 1, pair.Rejected) : (1, 0);
    }

    private static void NoteRejected(Peer peer, string kind, string reason)
    {
        long rejected;
        lock (KindCounters)
        {
            var pair = KindCounters.TryGetValue(kind, out var current) ? current : (0, 0);
            rejected = pair.Rejected + 1;
            KindCounters[kind] = (pair.Relayed, rejected);
        }
        Console.WriteLine($"[server] 命令被拒 kind={kind} reason={reason} player#{peer.Id} "
            + $"第{rejected}次");
    }

    /// <summary>One line per kind, for the periodic status. Empty when nothing has been relayed yet.</summary>
    public static string CommandCounters()
    {
        lock (KindCounters)
        {
            if (KindCounters.Count == 0) return "commands=none";
            return "commands=" + string.Join(",", KindCounters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value.Relayed + "/" + pair.Value.Rejected));
        }
    }
    private async Task RelayCommandAsync(Peer peer, WireMessage message)
    {
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (!GameRunning || !peer.Joined) { peer.Queue(WireMessage.Error("NotPlaying", "The shared colony is not running.")); return; }
            if (peer.IsSimulation) { peer.Queue(WireMessage.Error("SimulationReadOnly", "The process simulation does not issue player commands.")); return; }
            var command = message.Command;
            if (command is null || string.IsNullOrWhiteSpace(command.Kind) || command.Kind.Length > 64 || command.Kind.Any(char.IsControl)
                || command.Payload is null || command.Payload.Length > 64 * 1024)
            { peer.Queue(WireMessage.Error("InvalidCommand", "The command kind or payload is invalid.")); return; }
            // Only the known action classes are relayed; a client cannot invent a new command the game won't understand.
            if (!KnownKinds.Contains(command.Kind))
            { NoteRejected(peer, command.Kind, "UnknownCommand"); peer.TryQueue(WireMessage.Error("UnknownCommand", "This command kind is not part of the shared action set.")); return; }
            if (!peer.TryConsumeCommandBudget())
            { NoteRejected(peer, command.Kind, "RateLimited"); peer.TryQueue(WireMessage.Error("RateLimited", "Too many commands; the server is throttling this client.")); return; }
            // Sequence assignment, history and broadcast must be one critical section: otherwise two peers can
            // interleave and a peer receives a lower sequence after a higher one, which it must reject as a
            // regression. That would silently drop a player's command.
            var batch = new List<GameCommand>(1);
            lock (_commandGate)
            {
                // Stamp a tick no peer has reached yet. Tolerance: the slowest peer is at `slowest`, and the
                // budget lets the fastest sit SimulationLeadTicks ahead of it, so slowest + lead is the highest
                // tick that may already be running somewhere. Two more keeps the published timeline ahead of it.
                //
                // While the room is paused nobody advances at all, so the lead is not pipeline depth but a
                // dead wait: the command used to be stamped eight ticks ahead, and neither side could ever
                // reach that tick until play resumed - "I clicked forbid while paused and nothing happened,
                // then it all arrived at once a moment after unpausing". Stamping at the slowest applied tick
                // instead is applied immediately by every paused peer (their applied tick is >= slowest) and
                // is still far in the future for a peer that is behind.
                var stampBase = Volatile.Read(ref _tick);
                var paused = CurrentPolicy.TimeSpeed == 0;
                // Same pipeline depth the pacing budget uses, for the same reason: a constant six ticks is 100 ms at
                // vanilla speed but 16 ms at `Superfast`. If the budget may run 36 ticks ahead, a command stamped at
                // `slowest + 6` would land in the past for whoever is ahead, which is exactly the regression the
                // ordering intake rejects.
                var commandLead = NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(CurrentPolicy.TimeSpeed));
                if (SlowestAppliedTick() is { } slowest)
                    stampBase = paused ? slowest : Math.Max(stampBase, slowest + commandLead);
                var assigned = new GameCommand(Interlocked.Increment(ref _commandSequence), paused ? stampBase : stampBase + 2,
                    command.Kind, command.Payload, command.OriginPeer);
                // Everything below the stamped tick is now in flight. Recorded so the hash comparison can refuse to
                // call an ordering lead a divergence; the value only ever moves forward.
                if (assigned.Tick > Volatile.Read(ref _committedThroughTick))
                    Volatile.Write(ref _committedThroughTick, assigned.Tick);
                lock (_history)
                {
                    if (_history.Count >= MaxHistory)
                    {
                        // Keeping the contract honest: once history is dropped, mid-session joins cannot replay it.
                        _history.Clear();
                        _historyTruncated = true;
                    }
                    _history.Add(assigned);
                }
                batch.Add(assigned);
                // The authority is a participant too, and in a room with one display replica it is the *only*
                // participant that can run this command: the origin cancelled its own assignment on purpose (the
                // capture prefix turns the gesture into this command) and the relay above skips it. Handing the
                // command to the game thread is therefore not an optimisation, it is the missing executor.
                if (_options.Authoritative) EnqueueAuthorityCommand(assigned);
                // Every peer applies every command, the origin included - the rule ColonyTogether follows and the
                // one SharedColony.ApplyDue already documents. The old exception ("the origin already ran it
                // locally") is false for exactly the commands that matter: the capture prefixes cancel the local
                // write and turn the gesture into this command, so the origin is the one peer that provably has not
                // applied it. Measured: a replica pressed unforbid 46 times, every command was relayed and refused
                // zero, and its own screen kept every X because it was the only peer that never received them.
                // Applying it twice is not possible here: a display replica skips the entity-lifecycle kinds
                // (see AuthoritativeReplica.AcceptsCommand) because the frame carries those, so only state-only
                // kinds - forbid, draft, orders, priorities, zones, bills, research - reach ApplyDue on the origin.
                foreach (var recipient in _peers.Values)
                    if (recipient.Joined)
                        recipient.Queue(new WireMessage { Type = "Commands", Commands = batch, Epoch = Epoch });
            }
            foreach (var relayed in batch) NoteRelayed(relayed.Kind);
            // A bulk edit (a stockpile drag produces one command per cell) is where a relay problem hides: many
            // commands at once, and if the tail of them is lost the result is a smaller zone on one peer only.
            // `batch` is built with exactly one command, so the old `batch.Count > 20` gate could never fire. A
            // burst is many frames arriving close together, and the periodic 状态 line now reports that.
            if (++_commandsSincePersist >= 200)
            {
                _commandsSincePersist = 0;
                await SaveCurrentStateLockedAsync(peer.Token).ConfigureAwait(false);
            }
        }
        finally { _stateGate.Release(); }
    }

    /// <summary>Compares each peer's reported state hash for a tick; the first mismatch is a desync.</summary>
    private void ReportHash(Peer peer, WireMessage message)
    {
        // The baseline spans several dictionaries. Two peer readers must not both observe a missing
        // first report and return without ever comparing, or expose a half-written category baseline.
        // Pruning and a world reset use this same boundary.
        lock (_hashGate)
        {
            if (!GameRunning || !peer.Joined || message.Epoch != Epoch) return;
            if (message.Tick < _hashWindowFloor || string.IsNullOrWhiteSpace(message.Sha256) || message.Sha256.Length != 64) return;
            // A tick a command has already been stamped for is not comparable yet.
            //
            // The origin applies the action the moment the player does it - for a zone cell the local AddCell
            // cannot be cancelled, because the vanilla designator needs the cell present to keep dragging. The
            // command is then stamped `slowest + SimulationLeadTicks` ahead, so for that many ticks the origin has a
            // cell the others do not. Comparing those ticks reports a divergence that is only the ordering lead
            // working as designed, and the room pauses on it. Measured: a 25-cell stockpile drag diverged at tick
            // 4001, one tick after the drag, in every run.
            if (message.Tick < Volatile.Read(ref _committedThroughTick))
            {
                Interlocked.Increment(ref _inFlightTicksSkipped);
                return;
            }
            var hash = message.Sha256!;
            PruneHashWindow(message.Tick);
            // Only distinct peers are comparable: a resend from the same peer is not a conflict.
            var key = (peer.Id, message.Tick);
            if (!_hashByPeerTick.TryAdd(key, hash)) return;
            if (!_firstPeerAtTick.TryGetValue(message.Tick, out var firstPeer))
            {
                _firstPeerAtTick[message.Tick] = peer.Id;
                _firstHashAtTick[message.Tick] = hash;
                if (message.Categories is { Length: > 0 }) _firstCategoriesAtTick[message.Tick] = message.Categories;
                return;
            }
            if (firstPeer == peer.Id) return;
            var first = _firstHashAtTick[message.Tick];
            if (string.Equals(first, hash, StringComparison.Ordinal) || !_reportedDesyncTicks.Add(message.Tick)) return;
            // Name the divergent categories so a player can tell pawns from buildings from designations.
            var differing = "state";
            if (message.Categories is { Length: > 0 } mine && _firstCategoriesAtTick.TryGetValue(message.Tick, out var theirs)
                && theirs.Length != mine.Length)
            {
                // Different numbers of categories means the two peers did not sample the same projection at all
                // (TurboTick's diagnostic switches, e.g. TURBOTICK_HASH_EVERY=1, hash a pawn-only projection
                // instead of the full colony). Their hashes are then incomparable by construction, and calling
                // that a desync pauses a healthy room. Say so once instead of reporting a fake divergence.
                if (_incomparableShapes.Add((theirs.Length, mine.Length)))
                    Console.Error.WriteLine($"[server] hash samples are not comparable: one peer sent {theirs.Length} "
                        + $"categories, another {mine.Length}. Same sampling settings are required on every peer.");
                return;
            }
            if (message.Categories is { Length: > 0 } mine2 && _firstCategoriesAtTick.TryGetValue(message.Tick, out var theirs2)
                && theirs2.Length == mine2.Length)
            {
                // Pawn state is split into position, health and job so the report names the field that
                // actually differed instead of only saying "colonists". "随机" names the shared random
                // stream: when it diverges the others follow.
                // Shared with the mod so the list cannot drift: an added category used to shift every label
                // after it, and "随机" ended up naming the ecology hash.
                // A single-category sample is the per-tick probe: it hashes one combined pawn projection, so it
                // cannot claim which field differed and must not borrow the name of the first full-scan category.
                var names = mine2.Length == 1 ? ["逐tick探针(位置+血量+工作+目标)"] : StateCategories.Names;
                var hits = new List<string>();
                for (var i = 0; i < mine2.Length; i++)
                    if (!string.Equals(mine2[i], theirs2[i], StringComparison.Ordinal))
                        hits.Add(i < names.Length ? names[i] : "category" + i);
                if (hits.Count > 0) differing = string.Join(",", hits);
            }
            foreach (var recipient in _peers.Values)
                if (recipient.Joined) recipient.Queue(new WireMessage { Type = "Desync", Tick = message.Tick, Message = differing });
        }
    }

    /// <summary>
    /// Keeps only a recent window of comparison hashes. Without this the dictionaries grow with play time,
    /// which shows up as slowly increasing memory on a long-running server.
    /// </summary>
    /// <summary>If the owner never answers CheckpointNeeded, fall back to the existing snapshot.</summary>
    private void ExpirePendingJoiners()
    {
        List<Peer> stale = [];
        lock (_pendingJoiners)
        {
            foreach (var pair in _pendingJoiners.ToList())
            {
                if (Environment.TickCount64 - pair.Value < 10_000) continue;
                _pendingJoiners.Remove(pair.Key);
                if (_peers.TryGetValue(pair.Key, out var peer) && peer.Joined) stale.Add(peer);
            }
        }
        if (stale.Count == 0) return;
        foreach (var peer in stale) SendCatchUp(peer);
    }

    private void PruneHashWindow(int newestTick)
    {
        if (newestTick - _hashWindowFloor < HashWindow) return;
        var floor = newestTick - HashWindow;
        if (floor <= _hashWindowFloor) return;
        _hashWindowFloor = floor;
        foreach (var tick in _firstHashAtTick.Keys)
            if (tick < floor) _firstHashAtTick.TryRemove(tick, out _);
        foreach (var tick in _firstCategoriesAtTick.Keys)
            if (tick < floor) _firstCategoriesAtTick.TryRemove(tick, out _);
        foreach (var key in _hashByPeerTick.Keys)
            if (key.Tick < floor) _hashByPeerTick.TryRemove(key, out _);
        foreach (var tick in _firstPeerAtTick.Keys)
            if (tick < floor) _firstPeerAtTick.TryRemove(tick, out _);
        _reportedDesyncTicks.RemoveWhere(tick => tick < floor);
    }

    /// <summary>Sends the current epoch and the full command history to a peer that arrives mid-session.</summary>
    private void SendCatchUp(Peer peer)
    {
        // 刚载入基线的副本要按**它载入的那份**世界起算，而不是此刻最新的检查点：
        // 检查点每 6000 tick 换一次，若在它下载/载入期间正好换了，用新 id 发 Started 会让它
        // 判定「世界不一致」并再下载一次——一次多余的整份重载。
        var world = peer.BaselineManifest ?? CheckpointManifest ?? WorldManifest;
        var snapshotTick = peer.BaselineManifest is not null ? peer.BaselineTick : CheckpointTick;
        var epoch = Epoch;
        if (world is null || epoch < 0) return;
        peer.CatchUpSent = true;
        Console.WriteLine($"[server] catch-up -> peer {peer.Id}: tick {Tick}, snapshot {snapshotTick}, history {HistoryCount}");
        peer.Queue(new WireMessage
        {
            Type = "Started", Epoch = epoch, Tick = Tick, SnapshotTick = snapshotTick,
            WorldId = world.Id, Manifest = world, Seed = Volatile.Read(ref _seed),
            // Only a real player peer becomes a display replica. The resident simulation connection
            // must keep simulating, otherwise the authoritative host would freeze itself.
            Authoritative = _options.Authoritative && !peer.IsSimulation,
            RoomAuthoritative = _options.Authoritative,
            Lockstep = _options.Lockstep
        });
        List<GameCommand> snapshot;
        lock (_history) snapshot = [.. _history];
        for (var offset = 0; offset < snapshot.Count; offset += 200)
            peer.Queue(new WireMessage { Type = "Commands", Commands = snapshot.GetRange(offset, Math.Min(200, snapshot.Count - offset)), Epoch = epoch });
        if (HistoryTruncated) peer.Queue(new WireMessage { Type = "CatchUpTruncated" });
    }

    private async Task GameLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            var clock = new NativeTickClock();
            var previous = System.Diagnostics.Stopwatch.GetTimestamp();
            var lastBroadcast = -1;
            var lastBroadcastAt = 0L;
            var lastStatusAt = Environment.TickCount64;            var generation = Interlocked.Read(ref _roomGeneration);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var elapsed = (now - previous) / (double)System.Diagnostics.Stopwatch.Frequency;
                previous = now;
                await _stateGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (generation != _roomGeneration)
                    {
                        generation = _roomGeneration;
                        clock = new NativeTickClock();
                        lastBroadcast = -1;
                        previous = System.Diagnostics.Stopwatch.GetTimestamp();
                        elapsed = 0;
                    }
                    if (!GameRunning) continue;
                    ExpirePendingJoiners();
                    // The room's speed has to be resolved before the budget, because the lead is pipeline depth in
                    // time: the same 100 ms is 6 ticks at vanilla speed and 36 at 6x. Asking for the budget first
                    // measured the lead at whatever speed the loop last saw.
                    var heldForNoAudience = _options.PauseWithoutReplicas && DisplayReplicaCount == 0;
                    speedInRoom = heldForNoAudience ? 0 : CurrentPolicy.TimeSpeed;
                    var budget = _options.Lockstep ? LockstepTickBudget()
                        : _options.RequireSimulation ? SimulationTickBudget() : int.MaxValue;
                    // A room with nobody watching does not advance. The resident is a peer, not an audience, so
                    // `DisplayReplicaCount` counts only peers that are not the embedded simulation and have told us
                    // their world finished loading - a replica still downloading the baseline is not yet following.
                    // This is a runtime gate on purpose: `CurrentPolicy.TimeSpeed` stays whatever the operator chose,
                    // so the first replica to arrive resumes the room at that speed instead of at whatever a
                    // persisted "auto paused" flag had overwritten.
                    var held = heldForNoAudience;
                    Volatile.Write(ref _heldForAudience, held ? 1 : 0);
                    var speed = speedInRoom;
                    // Always consume this frame's clock output before clamping it. Blocked or capped time
                    // creates no debt for a later progress sample; only a real pause resets fractions.
                    //
                    // The speed handed to the clock is the room's speed, never a stand-in for back-pressure.
                    // Passing 0 when the budget was 0 used to reset the sub-tick fraction on every blocked sample,
                    // so the clock re-earned the same 0.96 of a tick forever: the loop ran 62.5 times a second and
                    // published 45.0 tick/s. A blocked sample now just loses the whole ticks it cannot spend.
                    var raw = clock.Advance(elapsed, speed);
                    var count = Math.Min(raw, budget);
                    // Back-pressure trims this iteration, it does not confiscate the time. The budget closes and
                    // reopens every broadcast window (the published tick climbs six ticks between messages while
                    // the slowest peer's applied tick only learns about it at the next one), so a discarded
                    // remainder was being discarded on that schedule, not on the peer's: measured on a 40-pawn
                    // room, 1800 ticks earned per 30 s, 1500 published, and the 271-tick gap matched the clamped
                    // iterations exactly. Handing them back keeps the burst bound (MaxRemainderTicks) while the
                    // budget stays the hard clamp.
                    if (count < raw) clock.Unspend(raw - count);
                    // Counted separately from the published tick: a clamp means the clock wanted to publish more
                    // ticks than the slowest peer allows, which is back-pressure and clears itself. An iteration
                    // that the loop never got to - or that advanced by less than one tick - is the other failure
                    // mode and is invisible in `tick=`, because the clock is only ever asked about the time it is
                    // handed. These fields are written only by this loop; the status writer reads them.
                    _loopIterations++;
                    _loopElapsedSeconds += elapsed;
                    _loopTicksProduced += raw;
                    if (raw > count) _loopTicksClamped++;
                    if (raw == 0 && count == 0) _loopIdleIterations++;
                    var tick = count == 0 ? Tick : Interlocked.Add(ref _tick, count);
                    // Pacing visibility. The tick loop used to be completely silent: an operator watching a
                    // stall saw only "tick=N is not moving" and had to guess which peer held the line. This
                    // names the applied tick of every peer plus anyone skipped for going silent (see
                    // LockstepDiagnostics / SilentPeers), written when that set changes and every 30 s after.
                    if (_options.Lockstep) LogPacingChanges();
                    // Keep paused clients alive at the same watermark. Zero speed publishes no simulation debt.
                    // Broadcast tightly in lockstep: the peer may only tick up to the last published tick, so
                    // a six-tick gap between messages made it block mid-frame and permanently drop those
                    // ticks, which showed up as the colony running at 47 t/s instead of 60.
                    var stride = _options.Lockstep ? 2 : 6;
                    // A paused or gated room does not move the watermark. Re-sending the same tick every loop
                    // iteration meant ~62 messages per peer per second for no information; one heartbeat per
                    // second is enough to keep a reconnecting peer in step.
                    if (count == 0 && tick == lastBroadcast)
                    {
                        if (Environment.TickCount64 - lastBroadcastAt < 1000) continue;
                    }
                    else if (count != 0 && tick - lastBroadcast < stride) continue;
                    lastBroadcastAt = Environment.TickCount64;
                    lastBroadcast = tick;
            // Periodic cumulative counters.
            //
            // The first-Command line could only ever print `received=Command:1`: it fires on the first one, so the
            // count is 1 by construction. Reading that as "24 of 25 were lost" sent a whole round chasing a
            // transport bug that does not exist. The `批量命令` line was equally unusable - `batch` holds exactly one
            // command, so `batch.Count > 20` is never true. Report running totals instead, where the number can be
            // the number.
            if (Environment.TickCount64 - lastStatusAt >= 30_000)
            {
                lastStatusAt = Environment.TickCount64;
                // The loop's own rate next to the timeline's rate. `tick=` alone cannot separate "the clock was
                // asked for less time than the wall clock" from "the clock produced ticks and the budget ate
                // them": both print the same tick sequence. Measured basis for this line: a 20 minute room
                // published 45.0 tick/s while the engine entered TickManagerUpdate 57/s.
                var elapsedWindow = _loopElapsedSeconds - lastLoopElapsed;
                var iterationsWindow = _loopIterations - lastLoopIterations;
                var producedWindow = _loopTicksProduced - lastLoopTicks;
                var clampedWindow = _loopTicksClamped - lastLoopClamped;
                var idleWindow = _loopIdleIterations - lastLoopIdle;
                // Published ticks are measured from the watermark itself, not from `produced - clamped`: a clamp
                // trims one iteration's excess and the clocks are not required to agree tick for tick.
                var publishedWindow = tick - lastLoopPublishedTick;
                lastLoopElapsed = _loopElapsedSeconds;
                lastLoopIterations = _loopIterations;
                lastLoopTicks = _loopTicksProduced;
                lastLoopClamped = _loopTicksClamped;
                lastLoopIdle = _loopIdleIterations;
                lastLoopPublishedTick = tick;
                Console.WriteLine($"[server] 状态 tick={tick} peers={_peers.Count} {ReceivedCounters()} "
                    + $"{CommandCounters()} {FrameTrace.Counters()} "
                    + $"committedThrough={Volatile.Read(ref _committedThroughTick)} "
                    + $"inFlightSkipped={Interlocked.Read(ref _inFlightTicksSkipped)} "
                    + $"loop={iterationsWindow}次/{elapsedWindow:0.0}s={iterationsWindow / Math.Max(elapsedWindow, 0.001d):0.0}/s "
                    + $"生产={producedWindow} 发布={publishedWindow} "
                    + $"被budget截断={clampedWindow} 空转={idleWindow} 余数={clock.Remainder:0.00} "
                    + $"预算={budget} 速度={speed} 领先={NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(speed))}tick held={held}");
            }
            foreach (var peer in _peers.Values)
                if (peer.Joined) peer.TryQueue(new WireMessage { Type = "Tick", Tick = tick });
                }
                finally { _stateGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception error)
        {
            // A dead tick loop used to look exactly like a paused room: connections were still accepted,
            // /status still answered, and only the tick stopped moving. Say so instead.
            Console.Error.WriteLine("[server] tick loop stopped: " + error);
        }
    }

    private async Task SetSpeedAsync(Peer peer, WireMessage message)
    {
        SpeedPolicy request;
        try
        {
            if (message.Speed is null) throw new ArgumentException("A speed policy is required.");
            request = new SpeedPolicy(message.Speed.Enabled, message.Speed.Multiplier, message.Speed.MaxRate);
        }
        catch (ArgumentException ex)
        {
            peer.Queue(WireMessage.Error("InvalidPolicy", ex.Message, message.RequestId));
            return;
        }
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (!peer.CanEdit)
            {
                peer.Queue(WireMessage.Error("ControllerOnly", "Only the server-assigned controller may change the speed policy.", message.RequestId));
                return;
            }
            if (_policy.Revision == long.MaxValue)
            {
                peer.Queue(WireMessage.Error("RevisionExhausted", "The policy revision has reached its maximum value.", message.RequestId));
                return;
            }
            var policy = request with { Revision = _policy.Revision + 1, TimeSpeed = _policy.TimeSpeed };
            try
            {
                await SaveStateAsync(policy, _lockedGameVersion, _lockedModHash, _run!.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                peer.Queue(WireMessage.Error("PersistenceFailed", "The server could not persist this policy; the previous revision remains active.", message.RequestId));
                return;
            }
            Volatile.Write(ref _policy, policy);
            foreach (var recipient in _peers.Values)
                if (recipient.Joined)
                    recipient.Queue(new WireMessage { Type = "Policy", Policy = policy, RequestId = recipient.Id == peer.Id ? message.RequestId : 0 });
        }
        finally { _stateGate.Release(); }
    }

    private async Task SetTimeSpeedAsync(Peer peer, WireMessage message)
    {
        if (message.TimeSpeed is not { } speed || speed is < 0 or > 4)
        {
            peer.Queue(WireMessage.Error("InvalidTimeSpeed", "Native time speed must be 0..4.", message.RequestId));
            return;
        }
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            RequireCurrentPeer(peer);
            if (!peer.CanEdit)
            {
                peer.Queue(WireMessage.Error("ControllerOnly", "Only the controller may change native time speed.", message.RequestId));
                return;
            }
            if (_policy.Revision == long.MaxValue)
            {
                peer.Queue(WireMessage.Error("RevisionExhausted", "The policy revision has reached its maximum value.", message.RequestId));
                return;
            }
            var policy = _policy with { TimeSpeed = speed, Revision = _policy.Revision + 1 };
            try { await SaveStateAsync(policy, _lockedGameVersion, _lockedModHash, _run!.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                peer.Queue(WireMessage.Error("PersistenceFailed", "The previous native speed remains active.", message.RequestId));
                return;
            }
            Volatile.Write(ref _policy, policy);
            foreach (var recipient in _peers.Values)
                if (recipient.Joined)
                    recipient.Queue(new WireMessage { Type = "Policy", Policy = policy,
                        RequestId = recipient.Id == peer.Id ? message.RequestId : 0 });
        }
        finally { _stateGate.Release(); }
    }

    private async Task LeaveAsync(Peer peer)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!peer.Joined || peer.RoomGeneration != _roomGeneration) return;
            peer.Joined = false;
            if (ReferenceEquals(Volatile.Read(ref _simulationPeer), peer)) ClearSimulationState();
            var survivors = _peers.Values.Where(candidate => candidate.Joined && !candidate.IsSimulation && !candidate.Token.IsCancellationRequested)
                .OrderBy(candidate => candidate.Id).ToArray();
            if (_checkpointOwnerId == peer.Id)
                _checkpointOwnerId = _simulationPeer is { Joined: true } simulation && !simulation.Token.IsCancellationRequested
                    ? simulation.Id : _options.RequireSimulation ? null : survivors.FirstOrDefault()?.Id;
            if (_hostId == peer.Id)
            {
                // A running colony retains continuity; an empty lobby requires another explicit claim.
                var successor = GameRunning ? survivors.OrderByDescending(candidate => candidate.Id == _checkpointOwnerId)
                    .ThenBy(candidate => candidate.Id).FirstOrDefault() : null;
                _hostId = successor?.Id;
            }
            if (!_options.EveryoneCanEdit)
                _controllerId = _hostId ?? (survivors.Any(candidate => candidate.Id == _controllerId)
                    ? _controllerId : survivors.FirstOrDefault()?.Id);
            RefreshEditingRoles();
            foreach (var upload in _worldUploads.Keys)
                if (upload.Peer == peer.Id) _worldUploads.TryRemove(upload, out _);
            lock (_pendingJoiners) _pendingJoiners.Remove(peer.Id);
            BroadcastRoles();
        }
        finally { _stateGate.Release(); }
    }

    private Task SaveStateAsync(SpeedPolicy policy, string? version, string? modHash, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(_statePath, CaptureState(policy, version, modHash), ct);

    private SavedState CaptureState(SpeedPolicy policy, string? version, string? modHash)
    {
        lock (_history)
            return new SavedState(policy, version, modHash, Session, [.. _history], _historyTruncated, WorldManifest,
                _snapshotStoreId, CheckpointManifest, CheckpointTick, _startupSave, Volatile.Read(ref _lockedMods));
    }

    /// <summary>Persists the running session so a restart resumes the same epoch, tick and history.</summary>
    private Task SaveCurrentStateLockedAsync(CancellationToken ct) =>
        SaveStateAsync(CurrentPolicy, LockedGameVersion, LockedModHash, ct);

    private async Task PersistSessionAsync(CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try { await SaveCurrentStateLockedAsync(ct).ConfigureAwait(false); }
        finally { _stateGate.Release(); }
    }

    private void RequireCurrentPeer(Peer peer)
    {
        if (!peer.Joined || peer.RoomGeneration != _roomGeneration || peer.Token.IsCancellationRequested)
            throw new NetworkException("SessionChanged", "This player connection belongs to an earlier room.");
    }

    private async Task WithRoomGateAsync(Peer peer, Action action)
    {
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try { RequireCurrentPeer(peer); action(); }
        finally { _stateGate.Release(); }
    }

    private static void RequireRequestId(WireMessage message)
    {
        if (message.RequestId == 0) throw new NetworkException("InvalidRequestId", "Requests require a positive request id.");
    }

    /// <summary>The room's mod list and version, attached to refusals that are <i>about</i> the mod list.
    ///
    /// <para>Only these two codes get it. Everything else (bad name, wrong protocol, room changed) would be noise:
    /// a player refused for their player name does not want a forty-row table.</para>
    ///
    /// <para>The payload is a string, not a new message type, so an older peer reads it as an unknown field and
    /// still gets the refusal it already handles.</para>
    /// </summary>
    private string? VerdictPayload(string code) => code is "ModHashMismatch" or "GameVersionMismatch"
        ? Volatile.Read(ref _lockedMods)
        : null;

    private async Task SendFatalAsync(Peer peer, string code, string message, string? mods = null)
    {
        try
        {
            await peer.Connection.WriteAsync(WireMessage.Error(code, message, fatal: true) with
            {
                Mods = mods,
                // Which build the room actually runs: without it "version mismatch" cannot say both values.
                GameVersion = mods is null ? null : LockedGameVersion
            }, peer.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private static async Task WriteLoopAsync(Peer peer)
    {
        try
        {
            await foreach (var message in peer.Outbound.Reader.ReadAllAsync(peer.Token).ConfigureAwait(false))
                await peer.Connection.WriteAsync(message, peer.Token).ConfigureAwait(false);
        }
        catch (Exception) { peer.Close(); }
    }

    private async Task HeartbeatLoopAsync(Peer peer)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var lastGateBusy = Environment.TickCount64;
            while (await timer.WaitForNextTickAsync(peer.Token).ConfigureAwait(false))
            {
                if (!peer.Joined && !peer.AuthenticatedAdmin) continue;
                // A long archive holds the room gate and may delay this peer's reader. Continue sending
                // heartbeats, but grant receive grace while server work prevents processing its replies.
                if (Volatile.Read(ref _archiveInProgress) != 0) lastGateBusy = Environment.TickCount64;
                if (Environment.TickCount64 - Math.Max(peer.Connection.LastReceived, lastGateBusy) > 20_000) { peer.Close(); return; }
                // 一个永远不 WorldReady 的副本会安静地挂在房间里：服务端不发帧，它自己也看不到画面。
                // 与其留着占位，不如在明确说出来之后断开——客户端会重连并重走一遍基线。
                var baselineStarted = Interlocked.Read(ref peer.BaselineStartedAt);
                if (peer.Joined && !peer.IsSimulation && !peer.WorldReady && baselineStarted > 0
                    && Environment.TickCount64 - baselineStarted > NetworkProtocol.BaselineTimeoutMilliseconds)
                {
                    Console.WriteLine($"[server] 基线下发超时（{(Environment.TickCount64 - baselineStarted) / 1000} 秒）-> 断开 peer {peer.Id}");
                    peer.Close();
                    return;
                }
                var sent = Environment.TickCount64;
                Interlocked.Exchange(ref peer.LastPingSent, sent);
                peer.Queue(new WireMessage { Type = "Ping", RequestId = sent });
            }
        }
        catch (Exception) { peer.Close(); }
    }

    private sealed class Peer
    {
        private readonly CancellationTokenSource _lifetime;
        private int _closed;
        internal readonly long Id;
        internal readonly long RoomGeneration;
        internal readonly FramedConnection Connection;
        internal readonly Channel<WireMessage> Outbound = Channel.CreateBounded<WireMessage>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        internal readonly CancellationToken Token;
        internal volatile bool Joined;
        internal volatile bool AuthenticatedAdmin;
        /// <summary>这个显示副本已经载入权威存档（客户端发了 WorldReady）。帧只在它为真时发出去。</summary>
        internal volatile bool WorldReady;
        /// <summary>已收到 Started + 命令历史；避免 WorldReady 之后再补一次导致副本重载。</summary>
        internal volatile bool CatchUpSent;
        /// <summary>推给这个 peer 的基线存档内容 id（sha256），用于核对 WorldReady 说的世界是不是本房间的。</summary>
        internal volatile string? BaselineId;
        /// <summary>推给这个 peer 的那份清单本身：载入完成后按它补 epoch/命令历史，
        /// 而不是按此刻最新的检查点（否则中间换代会让副本重载一次）。</summary>
        internal volatile SnapshotManifest? BaselineManifest;
        internal int BaselineTick;
        /// <summary>开始推基线的时刻，用来给「一直不 WorldReady」的 peer 設超时（Environment.TickCount64）。</summary>
        internal long BaselineStartedAt;
        internal volatile bool IsAdminConnection;
        internal bool IsSimulation;
        internal readonly bool IsLoopback;
        internal bool CanEdit;
        internal string PlayerName = "";
        /// <summary>服务端发放的稳定身份：32 字节随机、64 位小写十六进制。重连时客户端带回来，服务端按它认人。</summary>
        internal string? PlayerToken;
        /// <summary>上一条已落盘的视图（地图/选中/相机），只用来抑制重复日志。</summary>
        internal SessionViewSnapshot? LastView;
        internal int ColorIndex;
        internal int LatencyMilliseconds = -1;
        internal long LastPingSent;
        private long _lastPresenceReceived = long.MinValue / 2;

        internal bool TryConsumePresenceBudget()
        {
            var now = Environment.TickCount64;
            // Drop excess advisory packets without an error storm or a gameplay disconnect.
            if (now - _lastPresenceReceived < PresenceRules.SendIntervalMilliseconds) return false;
            _lastPresenceReceived = now;
            return true;
        }

        // Simple sliding window: a client may issue at most 400 commands per second of wall clock.
        private long _commandWindowStart = Environment.TickCount64;
        private int _commandsInWindow;

        internal bool TryConsumeCommandBudget()
        {
            var now = Environment.TickCount64;
            if (now - _commandWindowStart >= 1000)
            {
                _commandWindowStart = now;
                _commandsInWindow = 0;
            }
            return ++_commandsInWindow <= 400;
        }

        internal Peer(long id, TcpClient socket, CancellationToken serverToken, long roomGeneration)
        {
            Id = id;
            RoomGeneration = roomGeneration;
            IsLoopback = socket.Client.RemoteEndPoint is IPEndPoint remote && IPAddress.IsLoopback(remote.Address);
            Connection = new FramedConnection(socket, countsInbound: true);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            Token = _lifetime.Token;
        }

        internal void Queue(WireMessage message)
        {
            if (!Outbound.Writer.TryWrite(message)) Close();
        }

        /// <summary>Advisory traffic (errors, ticks). Dropping it is preferable to killing the session.</summary>
        internal void TryQueue(WireMessage message) => Outbound.Writer.TryWrite(message);

        internal void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _lifetime.Cancel();
            Outbound.Writer.TryComplete();
            Connection.Dispose();
        }

        internal void DisposeToken() => _lifetime.Dispose();
    }
}























