using System.Security.Cryptography;

namespace TurboTick.Networking;

/// <summary>把服务端基线下发（<c>SaveStart</c> + N × <c>SaveChunk</c>）的分块拼成一份存档文件。
///
/// 这一段刻意放在 Networking 里、**不引用 Verse**：
/// - 网络线程只写文件与算哈希，不碰游戏状态（跨线程规则）；
/// - 序号/总数/长度/sha256 的校验可以脱离游戏单测——这类逻辑写错不会红字，只会让客户端
///   载入一份半截存档，然后画面错位，看起来像「同步没做」而不是「拼装写错」。
///
/// 传输顺序由 TCP 保证，所以「序号跳号」不是丢包，而是协议错或服务端 bug：**报错不重试**。
/// 静默跳过才是灾难——那份存档会少一段字节，sha256 也只会在最后才说话。</summary>
public sealed class BaselineReceiver : IDisposable
{
    public enum ChunkResult
    {
        /// <summary>按序收到并已写入。</summary>
        Accepted,
        /// <summary>同一序号的重复投递：内容由整份 sha256 兜底，忽略即可，不毁会话。</summary>
        Duplicate,
        /// <summary>协议错（跳号、越界、长度不对）：这份基线作废，必须报错。</summary>
        Fatal
    }

    private readonly FileStream _file;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _received;
    private long _written;
    private bool _disposed;

    public BaselineReceiver(string saveId, long length, int chunkCount, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "A baseline save must not be empty.");
        if (chunkCount <= 0) throw new ArgumentOutOfRangeException(nameof(chunkCount), "A baseline save must have at least one chunk.");
        SaveId = saveId;
        ExpectedLength = length;
        ExpectedCount = chunkCount;
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
    }

    public string SaveId { get; }
    public long ExpectedLength { get; }
    public int ExpectedCount { get; }
    public string Path { get; }
    public int ReceivedChunks => _received;
    public long ReceivedBytes => _written;

    /// <summary>Progress in whole percent, for a log line that can be compared against the wall clock.</summary>
    public int Percent => ExpectedLength == 0 ? 0 : (int)Math.Min(100, _written * 100 / ExpectedLength);

    public ChunkResult Accept(int index, byte[]? bytes, out string? error)
    {
        error = null;
        if (_disposed) { error = "基线下发已经结束"; return ChunkResult.Fatal; }
        if (index < 0 || index >= ExpectedCount)
        {
            error = $"分块序号越界：index={index} total={ExpectedCount}";
            return ChunkResult.Fatal;
        }
        if (bytes is null || bytes.Length == 0 || bytes.Length > NetworkProtocol.SaveChunkSize)
        {
            error = $"分块长度非法：index={index} bytes={bytes?.Length ?? -1} limit={NetworkProtocol.SaveChunkSize}";
            return ChunkResult.Fatal;
        }
        if (index < _received) return ChunkResult.Duplicate;
        if (index > _received)
        {
            // TCP 不丢包：跳号只可能是服务端漏发或协议错。少一块的存档一定会错位，
            // 所以这里必须让整份基线失败，而不是继续往下拼。
            error = $"分块序号不连续：期望 {_received}，收到 {index}（共 {ExpectedCount} 块）";
            return ChunkResult.Fatal;
        }
        _file.Write(bytes, 0, bytes.Length);
        _hash.AppendData(bytes);
        _received++;
        _written += bytes.Length;
        if (_written > ExpectedLength)
        {
            error = $"基线下发字节数超出声明：{_written} > {ExpectedLength}";
            return ChunkResult.Fatal;
        }
        return ChunkResult.Accepted;
    }

    /// <summary>集齐后校验整份 sha256 与长度。返回 null 表示可以载入；否则是要显示给玩家的原因。</summary>
    public string? Finish()
    {
        if (_disposed) return "基线下发已经结束";
        try
        {
            _file.Flush(flushToDisk: true);
            if (_received != ExpectedCount) return $"基线不完整：收到 {_received}/{ExpectedCount} 块";
            if (_written != ExpectedLength) return $"基线长度不符：{_written} != {ExpectedLength} 字节";
            // 哈希必须在 Dispose 之前取：IncrementalHash 被释放后再 GetHashAndReset 会抛
            // ObjectDisposedException，而那是「校验步骤自己炸掉」，看起来像传输失败。
            var actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
            if (!string.Equals(actual, SaveId, StringComparison.Ordinal))
                return $"基线 sha256 不符：收到 {actual}，服务端声明 {SaveId}（{ExpectedLength} 字节）。"
                    + "存档在传输或落盘过程中被改动了，拒绝加载以免画面错位。";
            return null;
        }
        catch (Exception error) { return "基线存档落盘失败：" + error.Message; }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
        _hash.Dispose();
    }
}
