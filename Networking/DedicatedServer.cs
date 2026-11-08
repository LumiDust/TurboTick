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
    private readonly ConcurrentDictionary<long, Peer> _peers = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _run;
    private CancellationTokenRegistration _shutdownRegistration;
    private Task _acceptTask = Task.CompletedTask;
    private SpeedPolicy _policy = new();
    private string? _lockedGameVersion;
    private string? _lockedModHash;
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
    private Task _tickTask = Task.CompletedTask;

    // Command history lets a peer that joins mid-session replay the same decisions from the same snapshot.
    private readonly List<GameCommand> _history = [];
    private readonly ConcurrentDictionary<int, string> _firstHashAtTick = new();
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
    public int DisplayReplicaCount => _peers.Values.Count(peer => peer.Joined && !peer.IsSimulation);

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

    private sealed record SavedState(SpeedPolicy Policy, string? GameVersion, string? ModHash,
        SessionInfo? Session = null, List<GameCommand>? History = null, bool HistoryTruncated = false, SnapshotManifest? World = null,
        string? SnapshotStoreId = null, SnapshotManifest? Checkpoint = null, int CheckpointTick = 0);

    public int Port => Volatile.Read(ref _port);
    public SpeedPolicy CurrentPolicy => Volatile.Read(ref _policy);
    public string? LockedGameVersion => Volatile.Read(ref _lockedGameVersion);
    public string? LockedModHash => Volatile.Read(ref _lockedModHash);
    public SnapshotStore Snapshots { get; private set; }

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
        var policy = saved?.Policy ?? new SpeedPolicy();
        // Reconstructing also validates a persisted policy before any socket is opened.
        policy = new SpeedPolicy(policy.Enabled, policy.Multiplier, policy.MaxRate, policy.Revision, policy.TimeSpeed);
        // Startup values initialize a room; a persisted lock (including an admin reset) is authoritative.
        var version = saved?.GameVersion ?? (_options.GameVersion == "1.7" ? null : _options.GameVersion);
        var modHash = saved?.ModHash ?? (_options.ModHash == "development" ? null : _options.ModHash);
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
        Volatile.Write(ref _checkpointManifest, saved?.Checkpoint);
        Volatile.Write(ref _checkpointTick, saved?.CheckpointTick ?? 0);

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
                if (peer.AuthenticatedAdmin) await DispatchAdminAsync(peer, message).ConfigureAwait(false);
                else await DispatchAsync(peer, message).ConfigureAwait(false);
            }
        }
        catch (NetworkException ex) { await SendFatalAsync(peer, ex.Code, ex.Message).ConfigureAwait(false); }
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
            if (!string.Equals(version, hello.GameVersion, StringComparison.Ordinal))
                throw new NetworkException("GameVersionMismatch", $"Game version mismatch: server requires exactly {version}.");
            if (!string.Equals(modHash, hello.ModHash, StringComparison.Ordinal))
                throw new NetworkException("ModHashMismatch", "Simulation assemblies, XML, mod order or configuration differ from the server.");
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
                CheckpointOwner = _checkpointOwnerId == peer.Id,
                PlayerId = peer.Id, Players = CapturePlayers(peer), Epoch = Epoch
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
                await RelayCommandAsync(peer, message).ConfigureAwait(false);
                return;
            case "ReportHash":
                await WithRoomGateAsync(peer, () => ReportHash(peer, message)).ConfigureAwait(false);
                return;
            case "ReportProgress":
                if (peer.Joined && message.Tick >= 0) ReportPeerProgress(peer, message.Tick);
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
    /// never echoed back to the simulation peer, which already owns that state.</summary>
    public int BroadcastFrame(AuthoritativeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var message = new WireMessage { Type = "Frame", Frame = frame, Epoch = Epoch, Tick = frame.Tick };
        var sent = 0;
        foreach (var recipient in _peers.Values)
            if (recipient.Joined && !recipient.IsSimulation) { recipient.Queue(message); sent++; }
        return sent;
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
            "draft", "order", "transport", "forbid", "priority",
            "zone-cell", "zone-delete", "bill-add", "bill-delete", "area-assign",
            // The generic gizmo fallback: a whole serialised thing state.
            "thing-state"
        };

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
            { peer.TryQueue(WireMessage.Error("UnknownCommand", "This command kind is not part of the shared action set.")); return; }
            if (!peer.TryConsumeCommandBudget())
            { peer.TryQueue(WireMessage.Error("RateLimited", "Too many commands; the server is throttling this client.")); return; }
            // Sequence assignment, history and broadcast must be one critical section: otherwise two peers can
            // interleave and a peer receives a lower sequence after a higher one, which it must reject as a
            // regression. That would silently drop a player's command.
            var batch = new List<GameCommand>(1);
            lock (_commandGate)
            {
                // Stamp a tick no peer has reached yet. Tolerance: the slowest peer is at `slowest`, and the
                // budget lets the fastest sit SimulationLeadTicks ahead of it, so slowest + lead is the highest
                // tick that may already be running somewhere. Two more keeps the published timeline ahead of it.
                var stampBase = Volatile.Read(ref _tick);
                if (SlowestAppliedTick() is { } slowest)
                    stampBase = Math.Max(stampBase, slowest + NetworkProtocol.SimulationLeadTicks);
                var assigned = new GameCommand(Interlocked.Increment(ref _commandSequence), stampBase + 2,
                    command.Kind, command.Payload, command.OriginPeer);
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
                // In an authoritative room the origin already executed the action locally when the player
                // issued it, so echoing it back would apply it twice.
                //
                // A lockstep room is the opposite: every peer, the origin included, applies the command at
                // the agreed tick (see SharedActions.Apply / SharedColony.ApplyDue). Skipping the origin
                // there meant the player who pressed the button was the only one who never did - the
                // acceptance run showed the other peer applying sequences 17..25 while the origin stopped
                // at 20, and the two colonies then differed in pawn position.
                foreach (var recipient in _peers.Values)
                    if (recipient.Joined && (recipient.Id != peer.Id || _options.Lockstep))
                        recipient.Queue(new WireMessage { Type = "Commands", Commands = batch, Epoch = Epoch });
            }
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
                && theirs.Length == mine.Length)
            {
                // Pawn state is split into position, health and job so the report names the field that
                // actually differed instead of only saying "colonists". "随机" names the shared random
                // stream: when it diverges the others follow.
                var names = new[] { "位置", "血量", "工作", "物体", "标记", "随机" };
                var hits = new List<string>();
                for (var i = 0; i < mine.Length; i++)
                    if (!string.Equals(mine[i], theirs[i], StringComparison.Ordinal))
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
        var world = CheckpointManifest ?? WorldManifest;
        var epoch = Epoch;
        if (world is null || epoch < 0) return;
        Console.WriteLine($"[server] catch-up -> peer {peer.Id}: tick {Tick}, snapshot {CheckpointTick}, history {HistoryCount}");
        peer.Queue(new WireMessage
        {
            Type = "Started", Epoch = epoch, Tick = Tick, SnapshotTick = CheckpointTick,
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
            var generation = Interlocked.Read(ref _roomGeneration);
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
                    var budget = _options.Lockstep ? LockstepTickBudget()
                        : _options.RequireSimulation ? SimulationTickBudget() : int.MaxValue;
                    // Always consume this frame's clock output before clamping it. Blocked or capped time
                    // creates no debt for a later progress sample; a closed gate also resets fractions.
                    var count = clock.Advance(elapsed, budget == 0 ? 0 : CurrentPolicy.TimeSpeed);
                    count = Math.Min(count, budget);
                    var tick = count == 0 ? Tick : Interlocked.Add(ref _tick, count);
                    // Keep paused clients alive at the same watermark. Zero speed publishes no simulation debt.
                    // Broadcast tightly in lockstep: the peer may only tick up to the last published tick, so
                    // a six-tick gap between messages made it block mid-frame and permanently drop those
                    // ticks, which showed up as the colony running at 47 t/s instead of 60.
                    var stride = _options.Lockstep ? 2 : 6;
                    if (tick - lastBroadcast < stride && count != 0) continue;
                    lastBroadcast = tick;
                    foreach (var peer in _peers.Values)
                        if (peer.Joined) peer.TryQueue(new WireMessage { Type = "Tick", Tick = tick });
                }
                finally { _stateGate.Release(); }
            }
        }
        catch (Exception) { /* shutdown */ }
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
                _snapshotStoreId, CheckpointManifest, CheckpointTick);
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

    private static async Task SendFatalAsync(Peer peer, string code, string message)
    {
        try { await peer.Connection.WriteAsync(WireMessage.Error(code, message, fatal: true), peer.Token).ConfigureAwait(false); }
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
        internal volatile bool IsAdminConnection;
        internal bool IsSimulation;
        internal readonly bool IsLoopback;
        internal bool CanEdit;
        internal string PlayerName = "";
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
            Connection = new FramedConnection(socket);
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
