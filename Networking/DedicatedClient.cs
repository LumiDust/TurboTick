using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TurboTick.Networking;

/// <summary>Reusable TCP client. Policy changes are published only after the server accepts them.</summary>
public sealed class DedicatedClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _speedGate = new(1, 1);
    private readonly object _stateSync = new();
    /// <summary>The server's mod list, kept when the handshake was refused over a mod mismatch so a dialog can
    /// show what to install and in what order. Null when the server did not send one.</summary>
    private string? _serverMods;
    /// <summary>The room's game build, sent beside the list so "版本不同" can name both values.</summary>
    private string? _serverGameVersion;

    /// <summary>Set when the last handshake was refused over a mod mismatch; cleared on the next successful one.</summary>
    public string? ServerMods => Volatile.Read(ref _serverMods);

    /// <summary>The build the room runs, as reported by the refusal or the Welcome. Null when unknown.</summary>
    public string? ServerGameVersion => Volatile.Read(ref _serverGameVersion);
    private long _notificationGeneration;
    /// <summary>Per-connection suppression window for player-visible refusals. Replaced on every connect so a new
    /// room starts clean instead of inheriting the previous room's folded repeats.</summary>
    private NoticeBudget _notices = new();
    private readonly Channel<Action> _notifications = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    { SingleReader = true, SingleWriter = false });
    private readonly AsyncLocal<bool> _inNotification = new();
    private readonly Task _notificationTask;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private CancellationTokenSource? _connectionAttempt;
    private Session? _session;
    private SpeedPolicy? _currentPolicy;
    private string _status = "Disconnected";
    private int _connected;
    private int _canEdit;
    private int _checkpointOwner;
    private int _isHost;
    private int _disposed;
    private long _nextRequest;
    private long _playerId;
    /// <summary>服务端发放的稳定身份（见 <see cref="SessionTokens"/>）。`PlayerId` 是会话内分配的 long，
    /// 重连即变；这个 token 才是「还是同一个人」。null = 服务端没发（老服务端）。</summary>
    private string? _playerToken;
    private IReadOnlyList<PlayerInfo> _players = Array.Empty<PlayerInfo>();
    private IReadOnlyList<PlayerPresence> _presences = Array.Empty<PlayerPresence>();
    private int _latencyMilliseconds = -1;

    public event Action<SpeedPolicy>? PolicyChanged;
    public event Action<string>? StatusChanged;
    public event Action<SessionInfo, SnapshotManifest>? WorldStarted;
    public event Action<int>? Ticked;
    public event Action<IReadOnlyList<GameCommand>>? CommandsReceived;
    public event Action<AuthoritativeFrame>? FrameReceived;
    public event Action<int, string>? DesyncDetected;
    public event Action<int, SnapshotManifest>? CheckpointPublished;
    public event Action? CatchUpIncomplete;
    public event Action? CheckpointRequested;

    /// <summary>Raised when the coordinator refuses something and no request was waiting for the answer.
    ///
    /// This is the player-facing half of a refusal. Every command the relay will not publish already comes back as
    /// a structured `Error` frame; before this event that frame only completed a pending request, so a refused
    /// command produced a server log line and nothing else. The event fires on the notification thread (the same
    /// serialized path as <see cref="StatusChanged"/>), already rate limited by <see cref="PublishNotice"/>.</summary>
    public event Action<ServerNotice>? NoticeReceived;
    /// <summary>压缩帧解不开时触发。网络层不引用 Verse，所以不能直接写游戏日志。</summary>
    public event Action<string>? FrameUnpackFailed;
    /// <summary>基线下发开始：服务端把权威存档整份推过来。参数是 (<see cref="SessionInfo"/>, 存档清单, 存档名)。
    /// 这是「客户端本地没有任何存档」时唯一的入口，所以它必须比 Started 更早到达，且早于任何帧。</summary>
    public event Action<SessionInfo, SnapshotManifest, string>? BaselineStarted;
    /// <summary>基线集齐并通过 sha256 校验：给的是本地文件路径。落盘/载入由游戏侧做（网络层不碰 Verse）。</summary>
    public event Action<string, SnapshotManifest, SessionInfo>? BaselineReady;
    /// <summary>基线失败（缺块、长度不符、sha256 不符、写盘失败）。带可直接显示给玩家的原因。</summary>
    public event Action<string>? BaselineFailed;
    /// <summary>服务端在执行恢复：把这个 token 上次留下的地图/选中/相机下发一次。
    /// 没有记录时不会触发（新玩家、换了 token 的人停在默认视图）。游戏线程负责实际执行。</summary>
    public event Action<SessionViewSnapshot>? ViewRestoreReceived;
    public SpeedPolicy? CurrentPolicy => Volatile.Read(ref _currentPolicy);
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <summary>Milliseconds since the server last sent anything, or -1 when there is no session.
    ///
    /// A half-closed TCP connection accepts writes without error: the kernel buffers them, no exception is raised,
    /// and the far end is not reading. A write-side check therefore cannot detect it, so the read side has to be
    /// watched instead.</summary>
    /// <summary>Identity of the connection a command would travel on right now: session object, socket endpoint and
    /// whether the socket still reports itself open. Two different values across one burst means the writes were
    /// split between connections, which is the only explanation that fits "write completed, server saw nothing".</summary>
    public string DescribeSession()
    {
        var session = Volatile.Read(ref _session);
        if (session is null) return "no-session";
        try
        {
            var connection = session.Connection;
            return "sess=" + session.GetHashCode().ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                + " remote=" + connection.Endpoint + " open=" + connection.IsOpen;
        }
        catch (Exception) { return "session-error"; }
    }

    public long MillisecondsSinceLastReceived
    {
        get
        {
            var session = Volatile.Read(ref _session);
            if (session is null) return -1;
            try { return Environment.TickCount64 - session.Connection.LastReceived; }
            catch (Exception) { return -1; }
        }
    }
    public bool CanEdit => Volatile.Read(ref _canEdit) != 0;
    public bool IsCheckpointOwner => Volatile.Read(ref _checkpointOwner) != 0;
    /// <summary>Explicit server-confirmed room host, independent of editing and checkpoint duties.</summary>
    public bool IsHost => Volatile.Read(ref _isHost) != 0;
    public string Status => Volatile.Read(ref _status);
    public long PlayerId => Interlocked.Read(ref _playerId);
    /// <summary>服务端在 `Welcome` 里下发的稳定身份 token；null 表示服务端没发（老服务端）。
    /// 客户端应当把它写进 `<profile>\Config\TurboTick\player-token.txt`，下次握手带回来。</summary>
    public string? PlayerToken => Volatile.Read(ref _playerToken);
    /// <summary>Immutable snapshots, safe to inspect on the game thread without subscribing to events.</summary>
    public IReadOnlyList<PlayerInfo> Players => Volatile.Read(ref _players);
    public IReadOnlyList<PlayerPresence> Presences => Volatile.Read(ref _presences);
    public int LatencyMilliseconds => Volatile.Read(ref _latencyMilliseconds);
    public long PresenceEpoch
    {
        get
        {
            var session = Volatile.Read(ref _session);
            return session is null ? -1 : Interlocked.Read(ref session.Epoch);
        }
    }
    public event Action<IReadOnlyList<PlayerInfo>>? PlayersChanged;
    public event Action<PlayerPresence>? PresenceChanged;

    public DedicatedClient() => _notificationTask = Task.Run(NotificationLoopAsync);

    /// <summary>基线下发的落地目录（客户端传 <c>WorldFiles.Directory</c>）。为空时不接受基线推送——
    /// 服务端自己的发布/仿真连接不需要这条路。</summary>
    public DedicatedClient(string? baselineDirectory) : this() => _baselineDirectory = baselineDirectory;

    private readonly string? _baselineDirectory;
    private BaselineReceiver? _baseline;
    private SessionInfo? _baselineSession;
    private SnapshotManifest? _baselineManifest;
    private int _baselineStage;

    /// <summary>Returns only after the complete compatibility handshake and authoritative initial policy.
    ///
    /// <paramref name="playerToken"/> 是上次服务端发放的身份 token（读自
    /// `<profile>\Config\TurboTick\player-token.txt`）。它放在最后一个可选参数上：已有的调用点全部不用改，
    /// 不传就等于「新玩家」，服务端会发一个新的回来。</summary>
    public async Task ConnectAsync(string host, int port, ClientHello hello, CancellationToken ct = default,
        string? playerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Validation.Hello(hello);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            long generation;
            lock (_stateSync)
            {
                generation = ++_notificationGeneration;
                Volatile.Write(ref _notices, new NoticeBudget());
                SetStatus("Connecting", generation);
            }
            TcpClient? socket = new();
            FramedConnection? connection = null;
            using var attempt = new CancellationTokenSource();
            Volatile.Write(ref _connectionAttempt, attempt);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token, _disposeCancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                connection = new FramedConnection(socket);
                await connection.WriteAsync(new WireMessage
                {
                    Type = "Hello", Hello = hello,
                    // 稳定身份：服务端据此认出「还是那个人」。缺失/不认识时服务端当新玩家并回一个新 token。
                    PlayerToken = SessionTokens.IsValid(playerToken) ? playerToken : null
                }, timeout.Token).ConfigureAwait(false);
                var welcome = await connection.ReadAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new NetworkException("HandshakeClosed", "The server closed the connection before the handshake completed.");
                if (welcome.Type == "Error")
                {
                    // Keep the room's list before refusing. It arrives on the refusal itself, because `ServerMods` is
                    // otherwise only written from Welcome - a message a refused join never receives. Measured: the
                    // client then had nothing to show but a paragraph of prose naming no mod at all.
                    if (welcome.Mods is { Length: > 0 })
                    {
                        Volatile.Write(ref _serverMods, welcome.Mods);
                        Volatile.Write(ref _serverGameVersion, welcome.GameVersion);
                    }
                    throw ServerError(welcome);
                }
                if (welcome.Type != "Welcome" || welcome.Policy is null || welcome.CanEdit is null || welcome.IsHost is null ||
                    welcome.PlayerId <= 0 || !ValidPlayers(welcome.Players) ||
                    !welcome.Players!.Any(player => player.Id == welcome.PlayerId))
                    throw new NetworkException("InvalidWelcome", "The server did not return a valid Welcome and speed policy.");
                if (welcome.ProtocolVersion != NetworkProtocol.Version || welcome.ClientVersion != NetworkProtocol.ClientVersion)
                    throw new NetworkException("ProtocolMismatch", "The server protocol or client version differs from this client.");
                if (!string.Equals(welcome.GameVersion, hello.GameVersion, StringComparison.Ordinal))
                    throw new NetworkException("GameVersionMismatch", "The server Welcome contains a different game version.");
                if (!string.Equals(welcome.ModHash, hello.ModHash, StringComparison.Ordinal))
                {
                    // Keep the server's list before refusing: "hashes differ" is not something a player can act on,
                    // but "you are missing these five, in this order" is. The dialogs read this afterwards.
                    Volatile.Write(ref _serverMods, welcome.Mods);
                    Volatile.Write(ref _serverGameVersion, welcome.GameVersion);
                    throw new NetworkException("ModHashMismatch", "The server Welcome contains a different simulation mod hash.");
                }
                // Joined cleanly: the previous refusal's list is stale now, and leaving it would show an old verdict
                // table the next time a window asks.
                Volatile.Write(ref _serverMods, null);
                Volatile.Write(ref _serverGameVersion, null);
                timeout.Token.ThrowIfCancellationRequested();
                var session = new Session(this, connection, welcome.Policy.Revision, generation) { Epoch = welcome.Epoch };
                lock (_stateSync)
                {
                    Volatile.Write(ref _session, session);
                    Volatile.Write(ref _currentPolicy, welcome.Policy);
                    Volatile.Write(ref _canEdit, welcome.CanEdit.Value ? 1 : 0);
                    Volatile.Write(ref _checkpointOwner, welcome.CheckpointOwner == true ? 1 : 0);
                    Volatile.Write(ref _isHost, welcome.IsHost.Value ? 1 : 0);
                    Interlocked.Exchange(ref _playerId, welcome.PlayerId);
                    Volatile.Write(ref _playerToken, welcome.PlayerToken);
                    Volatile.Write(ref _players, Array.AsReadOnly(welcome.Players!.ToArray()));
                    Volatile.Write(ref _presences, Array.Empty<PlayerPresence>());
                    Volatile.Write(ref _latencyMilliseconds, -1);
                    Volatile.Write(ref _connected, 1);
                    Publish(session, () => PlayersChanged?.Invoke(Players));
                    PublishPolicy(welcome.Policy, session);
                    SetStatus(welcome.CanEdit.Value ? "Connected (controller)" : "Connected (read-only)", generation);
                }
                session.Reader = Task.Run(() => ReadLoopAsync(session));
                session.Heartbeat = Task.Run(() => HeartbeatLoopAsync(session));
                session.Completion = Task.WhenAll(session.Reader, session.Heartbeat);
                connection = null;
                socket = null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !attempt.IsCancellationRequested && !_disposeCancellation.IsCancellationRequested)
            {
                var error = new NetworkException("HandshakeTimedOut", "The server handshake timed out.");
                SetStatus(error.Message, generation);
                throw error;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Connection canceled", generation);
                throw;
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, generation);
                throw;
            }
            finally
            {
                Interlocked.CompareExchange(ref _connectionAttempt, null, attempt);
                connection?.Dispose();
                socket?.Dispose();
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Serializes local edits and awaits the persisted, broadcast server revision.</summary>
    public async Task SetSpeedAsync(bool enabled, float multiplier, float maxRate, CancellationToken ct = default)
    {
        _ = new SpeedPolicy(enabled, multiplier, maxRate);
        var session = ConnectedSession();
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        try { await _speedGate.WaitAsync(queued.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new NetworkException("SessionChanged", "The original server connection closed while this edit was queued."); }
        try
        {
            var reply = await RequestAsync(new WireMessage
            { Type = "SetSpeed", Speed = new SpeedRequest(enabled, multiplier, maxRate) }, ct, session).ConfigureAwait(false);
            if (reply.Type != "Policy" || reply.Policy is null)
                throw new NetworkException("InvalidResponse", "The server did not acknowledge the policy change.");
        }
        finally { _speedGate.Release(); }
    }

    /// <summary>Requests a native pause/normal/fast/superfast/ultrafast selection. No optimistic local policy.
    /// The session is captured before waiting, so a queued click never changes a different room.</summary>
    public async Task SetTimeSpeedAsync(int timeSpeed, CancellationToken ct = default)
    {
        SpeedPolicy.ValidateTimeSpeed(timeSpeed);
        var session = ConnectedSession();
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        try { await _speedGate.WaitAsync(queued.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new NetworkException("SessionChanged", "The original connection closed while the native speed edit was queued."); }
        try
        {
            var reply = await RequestAsync(new WireMessage { Type = "SetTimeSpeed", TimeSpeed = timeSpeed }, ct, session)
                .ConfigureAwait(false);
            if (reply.Type != "Policy" || reply.Policy is null)
                throw new NetworkException("InvalidResponse", "The server did not acknowledge the native time speed.");
        }
        finally { _speedGate.Release(); }
    }

    public async Task<SnapshotManifest?> GetSnapshotManifestAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(new WireMessage { Type = "GetSnapshotManifest" }, ct).ConfigureAwait(false);
        if (reply.Type != "SnapshotManifest")
            throw new NetworkException("InvalidResponse", "The server did not return a snapshot manifest.");
        if (reply.Manifest is not null) Validation.Manifest(reply.Manifest);
        return reply.Manifest;
    }

    /// <summary>Verifies every reusable .part chunk, downloads missing chunks, then verifies the complete SHA256.</summary>
    public async Task DownloadSnapshotAsync(SnapshotManifest manifest, string destination, Action<long, long>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var downloadSession = Volatile.Read(ref _session);
        Validation.Manifest(manifest);
        // Freeze caller-owned lists while an asynchronous download is running.
        manifest = manifest with { ChunkHashes = manifest.ChunkHashes.ToArray() };
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(destination))
        {
            await using var cached = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (cached.Length == manifest.Length && string.Equals(
                Convert.ToHexStringLower(await SHA256.HashDataAsync(cached, ct).ConfigureAwait(false)), manifest.Id, StringComparison.Ordinal))
            {
                progress?.Invoke(manifest.Length, manifest.Length);
                ct.ThrowIfCancellationRequested();
                return;
            }
        }
        if (downloadSession is null)
            throw new NetworkException("NotConnected", "Connect to the server before downloading missing snapshot chunks.");
        if (!ReferenceEquals(Volatile.Read(ref _session), downloadSession) || downloadSession.IsClosed || !IsConnected)
            throw new NetworkException("SessionChanged", "The snapshot download's original server connection has closed or changed.");
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadSession.Token);
        ct = downloadCancellation.Token;
        var partial = destination + ".part";
        var metadata = partial + ".json";
        var resetPartial = false;
        if (File.Exists(metadata))
        {
            try
            {
                var previous = await AtomicFile.ReadJsonAsync<SnapshotManifest>(metadata, ct).ConfigureAwait(false);
                resetPartial = previous is null || previous.Id != manifest.Id || previous.Length != manifest.Length ||
                    previous.ChunkSize != manifest.ChunkSize || previous.ChunkHashes is null ||
                    !previous.ChunkHashes.SequenceEqual(manifest.ChunkHashes, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Text.Json.JsonException)
            { resetPartial = true; }
        }
        await AtomicFile.WriteJsonAsync(metadata, manifest, ct).ConfigureAwait(false);
        await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            if (resetPartial) file.SetLength(0);
            var existingLength = file.Length;
            file.SetLength(manifest.Length);
            var buffer = new byte[manifest.ChunkSize];
            long done = 0;
            progress?.Invoke(0, manifest.Length);
            for (var index = 0; index < manifest.ChunkHashes.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var expectedLength = Validation.ChunkLength(manifest, index);
                var offset = (long)index * manifest.ChunkSize;
                var reusable = false;
                if (offset + expectedLength <= existingLength)
                {
                    file.Position = offset;
                    await file.ReadExactlyAsync(buffer.AsMemory(0, expectedLength), ct).ConfigureAwait(false);
                    reusable = string.Equals(Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, expectedLength))),
                        manifest.ChunkHashes[index], StringComparison.Ordinal);
                }
                if (!reusable)
                {
                    var reply = await RequestAsync(new WireMessage
                    { Type = "GetSnapshotChunk", SnapshotId = manifest.Id, ChunkIndex = index }, ct, downloadSession).ConfigureAwait(false);
                    var chunk = reply.Chunk;
                    if (reply.Type != "SnapshotChunk" || chunk is null || chunk.Id != manifest.Id || chunk.Index != index ||
                        chunk.Data is null || chunk.Data.Length != expectedLength)
                        throw new NetworkException("InvalidSnapshotChunk", "The server returned an unexpected snapshot chunk.");
                    var actualHash = Convert.ToHexStringLower(SHA256.HashData(chunk.Data));
                    if (!string.Equals(actualHash, chunk.Sha256, StringComparison.Ordinal) ||
                        !string.Equals(actualHash, manifest.ChunkHashes[index], StringComparison.Ordinal))
                        throw new NetworkException("SnapshotHashMismatch", "The downloaded snapshot chunk failed SHA256 verification.");
                    file.Position = offset;
                    await file.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                }
                done += expectedLength;
                progress?.Invoke(done, manifest.Length);
            }
            ct.ThrowIfCancellationRequested();
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            file.Position = 0;
            var wholeHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!string.Equals(wholeHash, manifest.Id, StringComparison.Ordinal))
                throw new NetworkException("SnapshotHashMismatch", "The complete snapshot failed SHA256 verification.");
        }
        ct.ThrowIfCancellationRequested();
        File.Move(partial, destination, overwrite: true);
        File.Delete(metadata);
    }

    /// <summary>Latest world snapshot the server holds for the shared colony, if any.</summary>
    public async Task<SnapshotManifest?> GetWorldManifestAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(new WireMessage { Type = "GetWorldManifest" }, ct).ConfigureAwait(false);
        if (reply.Type != "WorldManifest")
            throw new NetworkException("InvalidResponse", "The server did not return a world manifest.");
        if (reply.Manifest is not null) Validation.Manifest(reply.Manifest);
        return reply.Manifest;
    }

    /// <summary>Explicitly claim the room host role. Joining alone never claims it. The request and
    /// response stay bound to this connection; an occupied room returns HostOccupied.</summary>
    public async Task ClaimHostAsync(CancellationToken ct = default)
    {
        var session = ConnectedSession();
        var reply = await RequestAsync(new WireMessage { Type = "ClaimHost" }, ct, session).ConfigureAwait(false);
        lock (_stateSync)
        {
            if (!ReferenceEquals(_session, session) || session.IsClosed || !IsConnected)
                throw new NetworkException("SessionChanged", "The host request's original room connection changed.");
            if (reply.Type != "HostClaimed" || reply.IsHost != true || !IsHost)
                throw new NetworkException("InvalidResponse", "The server did not confirm the host role.");
        }
    }

    /// <summary>Checkpoint-owner only: upload a periodic checkpoint taken at <paramref name="checkpointTick"/>.</summary>
    public Task CheckpointWorldAsync(SnapshotManifest manifest, Stream source, int checkpointTick, CancellationToken ct = default) =>
        UploadWorldAsync(manifest, source, ct, checkpointTick);

    /// <summary>Host-only initial world publication, or checkpoint-owner-only checkpoint upload.</summary>
    public async Task UploadWorldAsync(SnapshotManifest manifest, Stream source, CancellationToken ct = default, int? checkpointTick = null)
    {
        Validation.Manifest(manifest);
        ArgumentNullException.ThrowIfNull(source);
        var session = ConnectedSession();
        var uploadId = Guid.NewGuid().ToString("N");
        var epoch = Interlocked.Read(ref session.Epoch);
        for (var index = 0; index < manifest.ChunkHashes.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var length = Validation.ChunkLength(manifest, index);
            var chunk = GC.AllocateUninitializedArray<byte>(length);
            await source.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(chunk)), manifest.ChunkHashes[index], StringComparison.Ordinal))
                throw new NetworkException("WorldHashMismatch", "A world chunk does not match its manifest hash.");
            await RequestAsync(new WireMessage
            {
                Type = "PublishWorldChunk", UploadId = uploadId, ChunkIndex = index, WorldChunk = chunk,
                Checkpoint = checkpointTick.HasValue, Tick = checkpointTick ?? 0, Epoch = epoch
            }, ct, session).ConfigureAwait(false);
        }
        var reply = await RequestAsync(new WireMessage
        {
            Type = "CommitWorld", UploadId = uploadId, ChunkCount = manifest.ChunkHashes.Count, Sha256 = manifest.Id,
            Checkpoint = checkpointTick.HasValue, Tick = checkpointTick ?? 0, Epoch = epoch
        }, ct, session).ConfigureAwait(false);
        if (reply.Type != "WorldCommitted" || reply.Manifest?.Id != manifest.Id)
            throw new NetworkException("WorldUploadRejected", "The server did not accept the world snapshot.");
    }

    /// <summary>Host-only: explicitly start (or restart) the shared simulation for everyone in the room.</summary>
    public async Task StartGameAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(new WireMessage { Type = "StartGame" }, ct).ConfigureAwait(false);
        if (reply.Type != "StartedAccepted")
            throw new NetworkException("StartRejected", "The server did not start the shared colony.");
    }

    /// <summary>Queue a gameplay command; the server assigns its order and tick and echoes it to everyone.</summary>
    public async Task SendCommandAsync(string kind, byte[] payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);
        var session = ConnectedSession();
        await session.Connection.WriteAsync(new WireMessage
        {
            Type = "Command", Command = new GameCommand(0, 0, kind, payload, PlayerId)
        }, session.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Best-effort presentation update, capped at 10 Hz with one write in flight. No request,
    /// task queue, command sequence, history entry or simulation tick is created. A dropped update is
    /// replaced by the next sample. The captured connection is never redirected to another room.</summary>
    public async Task<bool> SendPresenceAsync(PresenceUpdate state)
    {
        if (!PresenceRules.IsValid(state)) throw new ArgumentException("Invalid map presence.", nameof(state));
        var session = ConnectedSession();
        if (state.Epoch != Interlocked.Read(ref session.Epoch)) return false;
        if (Interlocked.CompareExchange(ref session.PresenceWriteInFlight, 1, 0) != 0) return false;
        try
        {
            var now = Environment.TickCount64;
            if (now - session.LastPresenceSent < PresenceRules.SendIntervalMilliseconds) return false;
            session.LastPresenceSent = now;
            await session.Connection.WriteAsync(new WireMessage { Type = "Presence", Presence = state }, session.Token)
                .ConfigureAwait(false);
            return true;
        }
        finally { Volatile.Write(ref session.PresenceWriteInFlight, 0); }
    }

    /// <summary>上报「我在看什么」（地图 / 选中 / 相机）。
    ///
    /// fire-and-forget：它是**状态覆盖**，不是事件——丢一条，下一拍防抖窗口补上就是了。为它排队等回包只会
    /// 把 500 ms 的防抖窗口再拉长一个 RTT，而服务端那边本来就是「覆盖写」。未连接时静默返回。</summary>
    public async Task SendViewStateAsync(SessionViewSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var session = Volatile.Read(ref _session);
        if (session is null || session.IsClosed || !IsConnected) return;
        await session.Connection.WriteAsync(
            new WireMessage { Type = "ViewState", View = SessionViewSnapshot.Sanitize(view) }, session.Token)
            .ConfigureAwait(false);
    }

    /// <summary>Lockstep pacing: tells the server how far this peer has actually simulated. Without it the
    /// timeline would run at the fastest machine's speed and the slowest would fall behind forever.</summary>
    public async Task ReportProgressAsync(int tick, CancellationToken ct = default)
    {
        var session = ConnectedSession();
        await session.Connection.WriteAsync(new WireMessage { Type = "ReportProgress", Tick = tick }, session.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Reports the local state hash for a tick so the server can detect divergence.</summary>
    public async Task ReportHashAsync(int tick, string sha256, IReadOnlyList<string>? categories = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64) throw new ArgumentException("A full SHA256 hex string is required.", nameof(sha256));
        var session = ConnectedSession();
        await session.Connection.WriteAsync(new WireMessage { Type = "ReportHash", Epoch = Interlocked.Read(ref session.Epoch), Tick = tick, Sha256 = sha256, Categories = categories?.ToArray() }, session.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public async Task DisconnectAsync()
    {
        CancelConnectionAttempt();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        _notifications.Writer.TryComplete();
        _disposeCancellation.Dispose();
        // Event handlers run on their own ordered dispatcher and may synchronously dispose this client.
        if (!_inNotification.Value) await _notificationTask.ConfigureAwait(false);
    }

    private void CancelConnectionAttempt()
    {
        try { Volatile.Read(ref _connectionAttempt)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task DisconnectCoreAsync()
    {
        // 断线时半截的基线文件必须收掉：句柄不释放会让下一次落盘撞上同一个 `.part`。
        _baseline?.Dispose();
        _baseline = null;
        _baselinePath = null;
        Interlocked.Exchange(ref _baselineStage, 0);
        Session? session;
        lock (_stateSync)
        {
            var generation = ++_notificationGeneration;
            session = Interlocked.Exchange(ref _session, null);
            Volatile.Write(ref _connected, 0);
            Volatile.Write(ref _canEdit, 0);
            ClearPresence(generation);
            SetStatus("Disconnected", generation);
        }
        if (session is not null)
        {
            session.Close("Disconnected");
            await session.Completion.ConfigureAwait(false);
            session.DisposeToken();
        }
    }

    private Session ConnectedSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = Volatile.Read(ref _session);
        if (session is null || session.IsClosed || !IsConnected)
            throw new NetworkException("NotConnected", "Connect to the server and complete the handshake first.");
        return session;
    }

    private async Task<WireMessage> RequestAsync(WireMessage request, CancellationToken ct, Session? expectedSession = null)
    {
        var session = expectedSession ?? ConnectedSession();
        if (!ReferenceEquals(Volatile.Read(ref _session), session) || session.IsClosed || !IsConnected)
            throw new NetworkException("SessionChanged", "This request belongs to a server connection that has closed or changed.");
        var id = Interlocked.Increment(ref _nextRequest);
        if (id < 1) throw new InvalidOperationException("The request id space has been exhausted.");
        var response = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A caller canceling its wait must not leave an unobserved fault during a simultaneous disconnect.
        _ = response.Task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!session.Pending.TryAdd(id, response)) throw new InvalidOperationException("Duplicate request id.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await session.Connection.WriteAsync(request with { RequestId = id }, timeout.Token).ConfigureAwait(false);
            return await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NetworkException(session.IsClosed ? "Disconnected" : "RequestTimedOut",
                session.IsClosed ? "The server connection closed before the request completed." : "The server request timed out.");
        }
        catch (Exception ex) when ((ex is IOException or SocketException or ObjectDisposedException) && ex is not NetworkException)
        {
            session.Close("The server connection closed.");
            throw new NetworkException("TransportError", "The server connection closed before the request completed.", ex);
        }
        finally { session.Pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync(Session session)
    {
        var reason = "The server closed the connection.";
        try
        {
            while (!session.Token.IsCancellationRequested)
            {
                var message = await session.Connection.ReadAsync(session.Token).ConfigureAwait(false);
                if (message is null) break;
                if (message.ProtocolVersion != NetworkProtocol.Version)
                    throw new NetworkException("ProtocolMismatch", "The received protocol version is incompatible.");
                switch (message.Type)
                {
                    case "Ping":
                        await session.Connection.WriteAsync(new WireMessage { Type = "Pong", RequestId = message.RequestId }, session.Token).ConfigureAwait(false);
                        break;
                    case "Pong":
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_session, session) && !session.IsClosed && message.RequestId > 0 &&
                                message.RequestId == Interlocked.Read(ref session.LastPingSent))
                                Volatile.Write(ref _latencyMilliseconds, (int)Math.Clamp(Environment.TickCount64 - message.RequestId, 0, 60_000));
                        }
                        break;
                    case "Players":
                        if (!ValidPlayers(message.Players)) throw new NetworkException("InvalidPlayers", "The room roster is invalid.");
                        lock (_stateSync)
                        {
                            if (!ReferenceEquals(_session, session) || session.IsClosed) break;
                            var players = Array.AsReadOnly(message.Players!.ToArray());
                            Volatile.Write(ref _players, players);
                            Volatile.Write(ref _presences, Array.AsReadOnly(_presences.Where(p => players.Any(player => player.Id == p.PlayerId)).ToArray()));
                            Publish(session, () => PlayersChanged?.Invoke(players));
                        }
                        break;
                    case "Presence":
                        var incoming = message.PlayerPresence;
                        if (incoming is null || !PresenceRules.IsValid(incoming.State))
                            throw new NetworkException("InvalidPresence", "The room presence is invalid.");
                        lock (_stateSync)
                        {
                            if (!ReferenceEquals(_session, session) || session.IsClosed || incoming.State.Epoch != session.Epoch ||
                                !_players.Any(p => p.Id == incoming.PlayerId)) break;
                            var presence = incoming with { UpdatedAtMilliseconds = Environment.TickCount64 };
                            Volatile.Write(ref _presences, Array.AsReadOnly(_presences.Where(p => p.PlayerId != presence.PlayerId).Append(presence).ToArray()));
                            Publish(session, () => PresenceChanged?.Invoke(presence));
                        }
                        break;
                    case "Policy":
                        if (message.Policy is null || message.Policy.Revision < session.LastRevision)
                            throw new NetworkException("InvalidPolicy", "The server sent an invalid or decreasing policy revision.");
                        session.LastRevision = message.Policy.Revision;
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_session, session) && !session.IsClosed)
                            {
                                Volatile.Write(ref _currentPolicy, message.Policy);
                                PublishPolicy(message.Policy, session);
                            }
                        }
                        CompleteResponse(session, message);
                        break;
                    case "Role":
                        if (message.CanEdit is null || message.IsHost is null) throw new NetworkException("InvalidRole", "The server role update is incomplete.");
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_session, session) && !session.IsClosed)
                            {
                                Volatile.Write(ref _canEdit, message.CanEdit.Value ? 1 : 0);
                                Volatile.Write(ref _isHost, message.IsHost.Value ? 1 : 0);
                                SetStatus(message.IsHost.Value ? "Connected (host)" : message.CanEdit.Value ? "Connected (controller)" : "Connected (read-only)", session.Generation);
                            }
                        }
                        break;
                    case "HostClaimed":
                        if (message.IsHost != true) throw new NetworkException("InvalidRole", "The host acknowledgement is incomplete.");
                        // Role/roster updates precede this response on the same ordered connection.
                        CompleteResponse(session, message);
                        break;
                    case "SnapshotManifest":
                    case "SnapshotChunk":
                    case "WorldManifest":
                    case "WorldChunkAccepted":
                    case "WorldCommitted":
                    case "StartedAccepted":
                        CompleteResponse(session, message);
                        break;
                    case "World":
                        if (message.Manifest is not null) Validation.Manifest(message.Manifest);
                        break;
                    case "Started":
                        lock (_stateSync)
                        {
                            if (!ReferenceEquals(_session, session) || session.IsClosed) break;
                            Interlocked.Exchange(ref session.Epoch, message.Epoch);
                            Volatile.Write(ref _presences, Array.Empty<PlayerPresence>());
                        }
                        if (message.Manifest is null) throw new NetworkException("InvalidStart", "The shared colony start is incomplete.");
                        Validation.Manifest(message.Manifest);
                        var started = message;
                        Publish(session, () => WorldStarted?.Invoke(new SessionInfo(started.Epoch, started.Tick, started.Seed, started.WorldId ?? "", started.SnapshotTick, started.Authoritative, started.RoomAuthoritative, started.Lockstep), started.Manifest!));
                        break;
                    case "SaveStart":
                        StartBaseline(message);
                        break;
                    case "SaveChunk":
                        AcceptBaselineChunk(message);
                        break;
                    case "Frame":
                        // Frames are coalesced state, not ordered events. Routing each one through the
                        // notification queue made the queue drain at game-frame rate while frames arrived
                        // at twice that, so the backlog grew without bound and the replica sat a constant
                        // ~2000 ticks (33 seconds) behind. Receive is thread-safe and keeps only the newest.
                        // 解压：FrameZip 与 Frame 互斥。解不开必须报出来——静默丢帧的副本看起来和"已同步"一模一样。
        if (!FrameCodec.TryUnpack(message, out var unpacked, out var unpackError))
            FrameUnpackFailed?.Invoke(unpackError ?? "unknown");
        else if (unpacked.Frame is { } frame) FrameReceived?.Invoke(frame);
                        break;
                    case "Tick":
                        var tick = message.Tick;
                        Publish(session, () => Ticked?.Invoke(tick));
                        break;
                    case "Desync":
                        var desyncTick = message.Tick;
                        var detail = message.Message ?? "state";
                        Publish(session, () => DesyncDetected?.Invoke(desyncTick, detail));
                        break;
                    case "CheckpointNeeded":
                        Publish(session, () => CheckpointRequested?.Invoke());
                        break;
                    case "ViewRestore":
                        // 服务端只在 WorldReady 之后发一次，且只在有记录时发。
                        if (message.View is { } restoreView)
                        {
                            var snapshot = SessionViewSnapshot.Sanitize(restoreView);
                            Publish(session, () => ViewRestoreReceived?.Invoke(snapshot));
                        }
                        break;
                    case "Checkpoint":
                        if (message.Manifest is not null) Validation.Manifest(message.Manifest);
                        var checkpointTick = message.Tick;
                        var checkpointManifest = message.Manifest;
                        if (checkpointManifest is not null)
                            Publish(session, () => CheckpointPublished?.Invoke(checkpointTick, checkpointManifest));
                        break;
                    case "CheckpointRole":
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_session, session) && !session.IsClosed)
                                Volatile.Write(ref _checkpointOwner, message.CheckpointOwner == true ? 1 : 0);
                        }
                        break;
                    case "CatchUpTruncated":
                        // The replay we can offer is incomplete; applying it would silently diverge, so say so.
                        Publish(session, () =>
                        {
                            CatchUpIncomplete?.Invoke();
                            StatusChanged?.Invoke("Catch-up history was truncated; the server needs a checkpoint.");
                        });
                        break;
                    case "Commands":
                        if (message.Commands is null) throw new NetworkException("InvalidCommand", "The server sent an empty command batch.");
                        var commands = message.Commands.ToArray();
                        // Commands from a previous epoch belong to a colony that has been restarted.
                        if (message.Epoch >= 0 && message.Epoch != session.Epoch) break;
                        Publish(session, () => CommandsReceived?.Invoke(commands));
                        break;
                    case "Error":
                        var error = ServerError(message);
                        var awaited = false;
                        if (message.RequestId > 0 && session.Pending.TryGetValue(message.RequestId, out var pending))
                        {
                            pending.TrySetException(error);
                            awaited = true;
                        }
                        if (message.Fatal) { reason = error.Message; return; }
                        // Nobody was waiting on this frame. That is the refusal case - the answer to a command the
                        // relay dropped - and it used to end here with no trace the player could see.
                        if (!awaited) PublishNotice(session, message.Code, message.Message);
                        break;
                    default: throw new NetworkException("UnknownMessage", "The server sent an unsupported message.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; }
        finally { session.Close(reason); }
    }

    /// <summary>SaveStart：建立接收器。
    ///
    /// 清单先过 <see cref="Validation.Manifest"/>：一份坏清单会让客户端拼出「长度看着对、内容错」的存档，
    /// 而那种存档要等到 LoadGame 之后才会以「画面错位」的形式暴露出来。</summary>
    private void StartBaseline(WireMessage message)
    {
        // No directory means this client cannot hold a world at all - a pure ordering host, or a probe that only
        // wanted to claim a role. Saying so once is required: the server has already committed 77 MB to the wire,
        // and silence here looks exactly like "the baseline never arrived", which sends the next reader looking for
        // a network fault that does not exist.
        if (_baselineDirectory is null)
        {
            FailBaseline("客户端没有基线目录（DedicatedClient 构造时未提供），无法接收世界");
            return;
        }
        try
        {
            if (message.Manifest is not { } manifest || string.IsNullOrEmpty(message.SaveId))
                throw new InvalidDataException("基线缺少清单或内容 id。");
            Validation.Manifest(manifest);
            if (!string.Equals(manifest.Id, message.SaveId, StringComparison.Ordinal))
                throw new InvalidDataException("基线内容 id 与清单不一致。");
            if (message.SaveCount <= 0
                || message.SaveCount != (int)((manifest.Length + NetworkProtocol.SaveChunkSize - 1) / NetworkProtocol.SaveChunkSize))
                throw new InvalidDataException(
                    $"基线块数与长度不匹配：{message.SaveCount} 块 × {NetworkProtocol.SaveChunkSize} 字节 != {manifest.Length} 字节");
            var path = Path.Combine(_baselineDirectory, manifest.Id + ".baseline.rws.part");
            Directory.CreateDirectory(_baselineDirectory);
            _baseline?.Dispose();
            _baseline = new BaselineReceiver(manifest.Id, manifest.Length, message.SaveCount, path);
            _baselineManifest = manifest;
            _baselineSession = new SessionInfo(message.Epoch, message.Tick, message.Seed, manifest.Id,
                message.SnapshotTick, message.Authoritative, message.RoomAuthoritative, message.Lockstep);
            _baselinePath = path;
            Interlocked.Exchange(ref _baselineStage, 1);
            BaselineStarted?.Invoke(_baselineSession, manifest, message.SaveName ?? manifest.Id[..16]);
        }
        catch (Exception error)
        {
            FailBaseline("基线下发无法开始：" + error.Message);
        }
    }

    private string? _baselinePath;
    private int _baselinePercent;

    private void AcceptBaselineChunk(WireMessage message)
    {
        var receiver = _baseline;
        // 没有 SaveStart 的块直接丢弃：这不是错误路径，只是「这条消息不属于当前会话」。
        if (receiver is null) return;
        if (message.SaveId is { Length: > 0 } id && !string.Equals(id, receiver.SaveId, StringComparison.Ordinal))
        {
            FailBaseline($"基线内容 id 变了：{id[..Math.Min(8, id.Length)]} != {receiver.SaveId[..Math.Min(8, receiver.SaveId.Length)]}");
            return;
        }
        var result = receiver.Accept(message.SaveIndex, message.SaveBytes, out var chunkError);
        if (result == BaselineReceiver.ChunkResult.Fatal) { FailBaseline(chunkError ?? "基线下发分块非法。"); return; }
        if (result == BaselineReceiver.ChunkResult.Duplicate) return;
        var percent = receiver.Percent;
        if (percent >= _baselinePercent + 25 && percent < 100)
        {
            _baselinePercent = percent / 25 * 25;
            // 进度要能被「载入花了多久」这个判据引用：没有它的日志里，18 MB 的静默和「卡死」无法区分。
            Console.WriteLine($"[TurboTick] 基线下发 {percent}%：{receiver.ReceivedBytes / 1024} KiB / "
                + $"{receiver.ReceivedChunks}/{receiver.ExpectedCount} 块");
        }
        if (receiver.ReceivedChunks != receiver.ExpectedCount) return;
        var manifest = _baselineManifest;
        var session = _baselineSession;
        var path = _baselinePath;
        _baseline = null;
        var failure = receiver.Finish();
        if (failure is not null) { FailBaseline(failure); return; }
        if (manifest is null || session is null || path is null) { FailBaseline("基线状态丢失。"); return; }
        // 这一行是验收判据：分块数、字节数、sha256 三者同时出现，才能和「收到一半就载入」区分开。
        Console.WriteLine($"[TurboTick] 基线下发 100%：{receiver.ExpectedLength} 字节 / {receiver.ExpectedCount} 块，"
            + $"sha256 校验通过 {manifest.Id[..16]}");
        try
        {
            // .part 只是写盘中间态；改成 .rws 后由游戏侧复制进 Saves/ 再 LoadGame。
            var final = path[..^".part".Length];
            File.Move(path, final, overwrite: true);
            _baselinePath = null;
            Interlocked.Exchange(ref _baselineStage, 2);
            BaselineReady?.Invoke(final, manifest, session);
        }
        catch (Exception error) { FailBaseline("基线落盘失败：" + error.Message); }
    }

    /// <summary>基线失败：清掉接收器与半截文件，并把原因交给游戏侧显示。
    /// 半截文件必须删掉——留在磁盘上的 `.part` 下一次会被当成一份存档的候选。</summary>
    private void FailBaseline(string reason)
    {
        _baseline?.Dispose();
        _baseline = null;
        Interlocked.Exchange(ref _baselineStage, 3);
        var path = _baselinePath;
        _baselinePath = null;
        if (path is not null)
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        BaselineFailed?.Invoke(reason);
    }

    /// <summary>告诉服务端基线已载入、可以开始发帧。
    ///
    /// 单向发送：帧能不能来由服务端决定，客户端不该为这件事阻塞游戏线程。发送失败不走异常路径——
    /// 连接断了会由心跳/读循环发现，届时客户端重新走一遍基线即可。</summary>
    public async Task SendWorldReadyAsync(string saveId, long epoch, int loadedTick = 0, int nextThingId = 0)
    {
        var session = Volatile.Read(ref _session);
        if (session is null) return;
        try
        {
            await session.Connection.WriteAsync(new WireMessage
            {
                // Tick / NextThingId 是副本载入后的实际水位，权威端据此补齐「存档点 -> 载入完成」之间
                // 新建/销毁的物体——增量帧永远不会再发它们。
                Type = "WorldReady", SaveId = saveId, Epoch = epoch, Tick = loadedTick, NextThingId = nextThingId
            }, session.Token).ConfigureAwait(false);
        }
        catch (Exception error) { SetStatus("WorldReady 发送失败：" + error.Message, Volatile.Read(ref _notificationGeneration)); }
    }

    private static void CompleteResponse(Session session, WireMessage message)
    {
        if (message.RequestId > 0 && session.Pending.TryGetValue(message.RequestId, out var pending))
            pending.TrySetResult(message);
    }

    private static async Task HeartbeatLoopAsync(Session session)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(session.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - session.Connection.LastReceived > 20_000)
                { session.Close("The server heartbeat timed out."); return; }
                var sent = Environment.TickCount64;
                Interlocked.Exchange(ref session.LastPingSent, sent);
                await session.Connection.WriteAsync(new WireMessage { Type = "Ping", RequestId = sent }, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { session.Close(ex.Message); }
    }

    private void OnSessionClosed(Session session, string reason)
    {
        lock (_stateSync)
        {
            if (!ReferenceEquals(_session, session)) return;
            Volatile.Write(ref _connected, 0);
            Volatile.Write(ref _canEdit, 0);
            ClearPresence(session.Generation);
            SetStatus(reason, session.Generation);
        }
    }

    private static bool ValidPlayers(IReadOnlyList<PlayerInfo>? players) => players is not null && players.Count <= 4096 &&
        players.All(p => p is not null && p.Id > 0 && p.Name is { Length: > 0 and <= 80 } &&
            !p.Name.Any(char.IsControl) && p.ColorIndex is >= 0 and < PresenceRules.ColorCount &&
            p.LatencyMilliseconds is >= -1 and <= 60_000) && players.Select(p => p.Id).Distinct().Count() == players.Count;

    // Called under _stateSync. The generation check also covers delayed disconnect notifications.
    private void ClearPresence(long generation)
    {
        Interlocked.Exchange(ref _playerId, 0);
        Volatile.Write(ref _checkpointOwner, 0);
        Volatile.Write(ref _isHost, 0);
        Volatile.Write(ref _latencyMilliseconds, -1);
        Volatile.Write(ref _players, Array.Empty<PlayerInfo>());
        Volatile.Write(ref _presences, Array.Empty<PlayerPresence>());
        _notifications.Writer.TryWrite(() =>
        {
            if (generation == Volatile.Read(ref _notificationGeneration)) PlayersChanged?.Invoke(Array.Empty<PlayerInfo>());
        });
    }

    /// <summary>Orders observer callbacks and drops anything belonging to a superseded connection.</summary>
    private void Publish(Session session, Action action)    {
        _notifications.Writer.TryWrite(() =>
        {
            if (session.Generation != Volatile.Read(ref _notificationGeneration) || session.IsClosed ||
                !ReferenceEquals(Volatile.Read(ref _session), session)) return;
            try { action(); } catch (Exception) { }
        });
    }

    /// <summary>Hands a refusal to the UI, at most once per code per window.
    ///
    /// Filtering happens here rather than in the UI so that what arrives at <see cref="NoticeReceived"/> is what a
    /// player should be told, already folded: a refused burst arrives as one notice carrying the repeat count, not
    /// as a wall of identical text.
    /// </summary>
    private void PublishNotice(Session session, string? code, string? message)
    {
        var handlers = NoticeReceived;
        if (handlers is null) return;
        var admission = Volatile.Read(ref _notices).Admit(code, Environment.TickCount64);
        if (!admission.Show) return;
        var notice = NoticeRules.Classify(code, message) with { Folded = admission.Suppressed };
        Publish(session, () =>
        {
            foreach (Action<ServerNotice> handler in handlers.GetInvocationList())
            {
                try { handler(notice); } catch (Exception) { }
            }
        });
    }

    private void PublishPolicy(SpeedPolicy policy, Session session)
    {
        var handlers = PolicyChanged;
        if (handlers is null) return;
        _notifications.Writer.TryWrite(() =>
        {
            foreach (Action<SpeedPolicy> handler in handlers.GetInvocationList())
            {
                // A delayed observer must never apply an earlier room's revision to a new room.
                if (session.Generation != Volatile.Read(ref _notificationGeneration) || session.IsClosed ||
                    !ReferenceEquals(Volatile.Read(ref _session), session)) return;
                try { handler(policy); } catch (Exception) { }
            }
        });
    }

    private void SetStatus(string status, long generation)
    {
        lock (_stateSync)
        {
            if (generation != _notificationGeneration || Interlocked.Exchange(ref _status, status) == status) return;
            var handlers = StatusChanged;
            if (handlers is null) return;
            _notifications.Writer.TryWrite(() =>
            {
                foreach (Action<string> handler in handlers.GetInvocationList())
                {
                    // Applies to connection attempts, disconnects, failures, and controller role changes.
                    if (generation != Volatile.Read(ref _notificationGeneration)) return;
                    try { handler(status); } catch (Exception) { }
                }
            });
        }
    }

    private async Task NotificationLoopAsync()
    {
        _inNotification.Value = true;
        await foreach (var notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
            try { notification(); } catch (Exception) { }
    }

    private static NetworkException ServerError(WireMessage message) =>
        new(message.Code ?? "ServerError", message.Message ?? "The server reported an unspecified error.");

    private sealed class Session
    {
        private readonly DedicatedClient _owner;
        private readonly CancellationTokenSource _lifetime = new();
        private int _closed;
        internal readonly FramedConnection Connection;
        internal readonly ConcurrentDictionary<long, TaskCompletionSource<WireMessage>> Pending = new();
        internal Task Reader = Task.CompletedTask;
        internal Task Heartbeat = Task.CompletedTask;
        internal Task Completion = Task.CompletedTask;
        internal long LastRevision;
        internal long Epoch = -1;
        internal int PresenceWriteInFlight;
        internal long LastPresenceSent = long.MinValue / 2;
        internal long LastPingSent;
        internal readonly long Generation;
        internal readonly CancellationToken Token;
        internal bool IsClosed => Volatile.Read(ref _closed) != 0;

        internal Session(DedicatedClient owner, FramedConnection connection, long revision, long generation)
        { _owner = owner; Connection = connection; LastRevision = revision; Generation = generation; Token = _lifetime.Token; }

        internal void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _lifetime.Cancel();
            Connection.Dispose();
            foreach (var pending in Pending.Values)
                pending.TrySetException(new NetworkException("Disconnected", reason));
            _owner.OnSessionClosed(this, reason);
        }

        internal void DisposeToken() => _lifetime.Dispose();
    }
}





