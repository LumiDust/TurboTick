using System.Buffers.Binary;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text.Json;

namespace TurboTick.Networking;

internal sealed record SpeedRequest(bool Enabled, float Multiplier, float MaxRate);

// One envelope keeps framing, request correlation, and error handling private to the networking module.
internal sealed record WireMessage
{
    public string Type { get; init; } = "";
    public int ProtocolVersion { get; init; } = NetworkProtocol.Version;
    public long RequestId { get; init; }
    public ClientHello? Hello { get; init; }
    public AdminChallenge? AdminChallenge { get; init; }
    public AdminProof? AdminProof { get; init; }
    public ServerAdminStatus? AdminStatus { get; init; }
    public ServerResetResult? AdminResetResult { get; init; }
    public long PlayerId { get; init; }
    public IReadOnlyList<PlayerInfo>? Players { get; init; }
    public PresenceUpdate? Presence { get; init; }
    public PlayerPresence? PlayerPresence { get; init; }
    public SpeedPolicy? Policy { get; init; }
    public bool? CanEdit { get; init; }
    public bool? IsHost { get; init; }
    public string? GameVersion { get; init; }
    public string? ModHash { get; init; }
    /// <summary>The sender's active mod list, in load order. Additive: a peer that does not send it simply cannot be
    /// told what to install or how to order it.</summary>
    public string? Mods { get; init; }
    public string? ClientVersion { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public bool Fatal { get; init; }
    public SpeedRequest? Speed { get; init; }
    public int? TimeSpeed { get; init; }
    public SnapshotManifest? Manifest { get; init; }
    public SnapshotChunk? Chunk { get; init; }
    public string? SnapshotId { get; init; }
    public int ChunkIndex { get; init; }

    // 基线下发：客户端本地**没有任何存档**也能加入。整份存档按 SaveIndex 顺序推送，
    // 每块带总块数与整份 sha256；客户端集齐校验后 LoadGame，加载完成回 WorldReady，
    // 服务端收到 WorldReady 才开始给这个 peer 发帧。全是可选字段：老客户端不认这些
    // 消息类型，也就不会走到这条路上（服务端只对 authoritative 房的显示副本推）。
    public string? SaveId { get; init; }
    public string? SaveName { get; init; }
    public int SaveIndex { get; init; } = -1;
    public int SaveCount { get; init; }
    public byte[]? SaveBytes { get; init; }

    /// <summary>WorldReady 里带上副本载入后的物体 id 水位（<c>UniqueIDsManager</c> 的当前值）。
    /// 帧是增量，所以「存档点 -> 副本载入完成」这段时间里新建的物体不会被任何一帧再发一次；
    /// 权威端用它补齐这段间隙：id 大于水位的物体都是间隙里新建的。0 表示老客户端没带。</summary>
    public int NextThingId { get; init; }

    // Shared-colony session fields (additive; older peers never send or require them).
    public long Epoch { get; init; } = -1;
    public int Tick { get; init; }
    public int Seed { get; init; }
    public int SnapshotTick { get; init; }
    public bool Checkpoint { get; init; }
    public bool? CheckpointOwner { get; init; }
    public string[]? Categories { get; init; }
    public string? WorldId { get; init; }
    public string? UploadId { get; init; }
    public int ChunkCount { get; init; }
    public byte[]? WorldChunk { get; init; }
    public string? Sha256 { get; init; }
    public GameCommand? Command { get; init; }
    public List<GameCommand>? Commands { get; init; }
    public AuthoritativeFrame? Frame { get; init; }
    /// <summary>Brotli 压缩后的帧。与 <see cref="Frame"/> 互斥：设置了这个就不设 Frame。
    /// 实测一帧 11 KB → 3.5 KB，是 host 上行带宽的主要手段。</summary>
    public byte[]? FrameZip { get; init; }
    /// <summary>Present on Started: joining peers are display replicas, not simulators.</summary>
    public bool Authoritative { get; init; }
    /// <summary>Present on Started: the room itself runs one authoritative simulator, so replicas apply
    /// frames and no peer performs cross-peer hash comparison.</summary>
    public bool RoomAuthoritative { get; init; }

    /// <summary>Every peer simulates and the server only orders commands and paces ticks.</summary>
    public bool Lockstep { get; init; }

    // 阶段 8：稳定身份与视图状态。两样都是**可选字段**，旧端不发也不读，握手与帧语义都不变。
    /// <summary>客户端在 `Hello` 里带上自己存的 token，服务端在 `Welcome` 里回最终使用的那个
    /// （认不出来就发一个新的）。缺失 = 老客户端/首次加入 ⇒ 服务端当新玩家。
    /// 64 位小写十六进制、32 字节随机，见 <see cref="SessionTokens"/>。</summary>
    public string? PlayerToken { get; init; }
    /// <summary>`ViewState`（客户端 → 服务端，防抖上报）与 `ViewRestore`（服务端 → 客户端，载入完成后下发一次）
    /// 共用这个字段。不含 `Frame` 的任何语义：它不参与模拟，也不进存档。</summary>
    public SessionViewSnapshot? View { get; init; }

    internal static WireMessage Error(string code, string message, long requestId = 0, bool fatal = false) =>
        new() { Type = "Error", Code = code, Message = message, RequestId = requestId, Fatal = fatal };
}

internal static class JsonWire
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>实际写上线缆的字节数。发布文档里的带宽一直是「序列化估算」，
/// 因为有帧头、压缩、以及每条连接各写一次；这里把它变成真数。</summary>
public static class WireStats
{
    private static long bytesOut;
    private static long framesOut;
    // Downstream used to be implicit: a server's outbound traffic is a replica's inbound one, so this side
    // only ever needed one counter. The administration page shows both directions of *this* process, and a
    // replica's inbound is not the server's, so the read path counts separately.
    private static long bytesIn;
    private static long framesIn;
    // Lifetime totals, independent of Reset(). The periodic frame log resets the interval counters every
    // 600 ticks; a page that showed those would report the traffic of the last ten seconds as if it were the
    // session's, and go backwards every time the log line fired.
    private static long lifetimeOut;
    private static long lifetimeIn;
    private static long lifetimeOutFrames;
    private static long lifetimeInFrames;

    public static void Written(int bytes)
    {
        Interlocked.Add(ref bytesOut, bytes);
        Interlocked.Increment(ref framesOut);
        Interlocked.Add(ref lifetimeOut, bytes);
        Interlocked.Increment(ref lifetimeOutFrames);
    }

    public static void Received(int bytes)
    {
        Interlocked.Add(ref bytesIn, bytes);
        Interlocked.Increment(ref framesIn);
        Interlocked.Add(ref lifetimeIn, bytes);
        Interlocked.Increment(ref lifetimeInFrames);
    }

    public static (long Bytes, long Frames) Snapshot() =>
        (Interlocked.Read(ref bytesOut), Interlocked.Read(ref framesOut));

    /// <summary>Monotonic totals since process start; unaffected by <see cref="Reset"/>.</summary>
    public static (long OutBytes, long OutFrames, long InBytes, long InFrames) Totals() =>
        (Interlocked.Read(ref lifetimeOut), Interlocked.Read(ref lifetimeOutFrames),
         Interlocked.Read(ref lifetimeIn), Interlocked.Read(ref lifetimeInFrames));

    /// <summary>Interval totals since the last <see cref="Reset"/> (what the frame log reports).</summary>
    public static (long OutBytes, long OutFrames, long InBytes, long InFrames) Interval() =>
        (Interlocked.Read(ref bytesOut), Interlocked.Read(ref framesOut),
         Interlocked.Read(ref bytesIn), Interlocked.Read(ref framesIn));

    public static string Reset()
    {
        var (bytes, frames) = (Interlocked.Exchange(ref bytesOut, 0), Interlocked.Exchange(ref framesOut, 0));
        return $"{bytes} B / {frames} 帧";
    }
}
internal sealed class FramedConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _closed;
    private long _lastReceived = Environment.TickCount64;
    internal long LastReceived => Interlocked.Read(ref _lastReceived);

    /// <summary>Stable identity for this socket, for logs that must tell two connections apart.</summary>
    internal string Endpoint
    {
        get
        {
            try { return _client?.Client?.RemoteEndPoint?.ToString() ?? "?"; }
            catch (Exception) { return "?"; }
        }
    }

    /// <summary>Whether the socket is still usable. A write can succeed into a socket the far end has closed, so this
    /// is worth reporting next to any send.</summary>
    internal bool IsOpen
    {
        get
        {
            try { return _client?.Client is { Connected: true }; }
            catch (Exception) { return false; }
        }
    }

    internal FramedConnection(TcpClient client, bool countsInbound = false)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _countsInbound = countsInbound;
    }
    // Only the server side counts: a resident simulation peer lives in the same process as the authority and
    // reads its own frame stream over loopback. Counting every read made the administration page report 63 MB
    // of "replica -> authority" traffic that never crossed a cable.
    private readonly bool _countsInbound;

    internal async Task<WireMessage?> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        var read = await _stream.ReadAsync(header, ct).ConfigureAwait(false);
        if (read == 0) return null;
        await _stream.ReadExactlyAsync(header.AsMemory(read), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > NetworkProtocol.MaximumFrameLength)
        {
            // Record the residue instead of only throwing: measured, a peer completed 21 writes and the far end
            // parsed exactly one frame, and there was no way to tell "frames were merged" from "the length prefix was
            // misread". The head bytes are enough to tell those apart.
            FrameTrace.Residue(length, header);
            throw new NetworkException("InvalidFrameLength", $"Frame length must be between 1 and {NetworkProtocol.MaximumFrameLength} bytes.");
        }
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        await _stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        FrameTrace.Read(length);
        WireMessage message;
        try
        {
            message = JsonSerializer.Deserialize<WireMessage>(bytes, JsonWire.Options)
                ?? throw new JsonException("Empty JSON message.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new NetworkException("InvalidMessage", "The received JSON message is invalid.", ex);
        }
        if (string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 64 || message.RequestId < 0)
            throw new NetworkException("InvalidMessage", "The message type or request id is invalid.");
        Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
        if (_countsInbound) WireStats.Received(header.Length + length);
        return message;
    }

    internal async Task WriteAsync(WireMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonWire.Options);
        if (bytes.Length is < 1 or > NetworkProtocol.MaximumFrameLength)
            throw new NetworkException("FrameTooLarge", "The outgoing frame exceeds the protocol limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _writeGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await _stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            // 记实际线缆字节：带宽此前一直是估算，这里把它变成真数（含 4 字节帧头）。
            WireStats.Written(header.Length + bytes.Length);
        }
        catch
        {
            // A canceled/partial write cannot be retried on the same byte stream.
            Dispose();
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _client.Dispose();
        // The semaphore has no unmanaged resources; in-flight writers may still release it.
    }
}

internal static class AtomicFile
{
    internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonWire.Options, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonWire.Options, ct).ConfigureAwait(false);
    }
}


/// <summary>Counts frames read and residual bytes, so the inbound path can be judged instead of inferred.
///
/// Measured: a peer completed 21 writes with no gate drop and no send failure, and the far end parsed exactly one
/// frame. Everything before this point was already proven healthy, so the remaining question is whether frames were
/// merged, mis-split, or never flushed - and those three look identical from the outside.
/// </summary>
internal static class FrameTrace
{
    private const int Capacity = 64;

    private static readonly List<string> Recent = new(Capacity);

    private static long frames;

    private static long residues;

    internal static void Read(int length)
    {
        Interlocked.Increment(ref frames);
        lock (Recent)
        {
            Recent.Add($"frame len={length}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static void Residue(int length, byte[] header)
    {
        Interlocked.Increment(ref residues);
        lock (Recent)
        {
            Recent.Add($"residue len={length} head={Convert.ToHexString(header)}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    internal static string Counters() => $"frames={Interlocked.Read(ref frames)} residues={Interlocked.Read(ref residues)}";

    /// <summary>The most recent entries, for the log at a moment that matters.
    ///
    /// `Counters()` alone cannot answer "did 25 cells arrive as 25 frames or as one merged frame": the count is
    /// cumulative for the whole session, so a jump of 25 and a jump of 1 are indistinguishable in the total. The
    /// recent entries carry the lengths, and the lengths are the answer.
    /// </summary>
    internal static string Last(int take)
    {
        lock (Recent)
        {
            var from = Math.Max(0, Recent.Count - take);
            return Recent.Count == 0 ? "frames-recent=none" : "frames-recent=" + string.Join("|", Recent.Skip(from));
        }
    }
}










