using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TurboTick.Networking;

/// <summary>一个玩家「在看什么」：哪张地图、选中了谁、相机在哪。
///
/// 这是**会话状态**，不是世界状态：它不进存档，也不改变任何模拟结果。恢复它只是让「退出再进来的人」
/// 回到自己离开时的画面（`Find.CurrentMap` / `Selector` / `CameraDriver`），不涉及谁能指挥谁。
/// 用户已明确：不做权限隔离，所有玩家仍然可以指挥任何小人。</summary>
public sealed record SessionViewSnapshot(int MapId, IReadOnlyList<int> Selected, float X, float Z, float Size)
{
    /// <summary>选中列表上限。协议里写明 64：一个 Group 再多也点不出更多，而长达数千的列表
    /// 会让「恢复视图」在逐个人找 Pawn 时卡住游戏线程。</summary>
    public const int MaximumSelected = 64;

    /// <summary>没有记录时的占位：地图 -1 表示「不知道」，客户端据此不跳图。</summary>
    public static readonly SessionViewSnapshot Empty = new(-1, Array.Empty<int>(), 0f, 0f, 0f);

    /// <summary>把上报内容收敛成可存的形式。服务端不因为个别坏值断开整个会话——
    /// 一个 NaN 坐标不该让玩家掉线，只该被忽略。</summary>
    public static SessionViewSnapshot Sanitize(SessionViewSnapshot? view)
    {
        if (view is null) return Empty;
        var mapId = view.MapId is >= -1 and <= 1_000_000 ? view.MapId : -1;
        var selected = (view.Selected ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .Take(MaximumSelected)
            .ToArray();
        return new SessionViewSnapshot(mapId, selected, Finite(view.X), Finite(view.Z), Finite(view.Size));
    }

    // 地图坐标是 float，非有限值会让 TryJump 的 IntVec3 转换得到 int.MinValue。
    private static float Finite(float value) => float.IsFinite(value) && Math.Abs(value) < 1_000_000f ? value : 0f;

    public bool HasSelection => Selected is { Count: > 0 };
}

/// <summary>稳定身份 token：服务端首次见到某个客户端时用加密随机取出 32 字节，之后按它认人。
///
/// 不用 Steam ID：headless 客户端不一定有 Steam 会话，而 token 只要求「同一台机器上同一个 profile 不变」。</summary>
public static class SessionTokens
{
    public const int ByteCount = 32;

    /// <summary>32 字节 → 64 个小写十六进制字符。</summary>
    public const int HexLength = ByteCount * 2;

    /// <summary>新的随机 token。用 <see cref="RandomNumberGenerator"/> 而不是 <see cref="Guid"/>：
    /// token 是可以被别人看到就要作废的东西，没有理由让它可预测。</summary>
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(ByteCount));

    /// <summary>恰好 64 位小写十六进制。大小写混写不接受：文件里写什么就发什么，
    /// 放宽只会让「同一个 token 两种写法」在服务端变成两个玩家。</summary>
    public static bool IsValid(string? token)
    {
        if (token is not { Length: HexLength }) return false;
        foreach (var c in token)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    /// <summary>日志里只出现前 8 位：整份 token 是身份凭据，不该整条写进日志。</summary>
    public static string Short(string? token) =>
        token is { Length: >= 8 } ? token[..8] : (token ?? "");
}

public sealed record SessionCamera(float X, float Z, float Size);

/// <summary>会话文件里一个玩家的记录。字段名与 `docs/server-plan.md` 阶段 8 的 JSON 一一对应。</summary>
public sealed record SessionPlayerView(string Name, int MapId, IReadOnlyList<int> Selected, SessionCamera Camera, string UpdatedUtc);

/// <summary>`/data/session/&lt;存档 sha256 前 16&gt;.json` 的落盘形状。</summary>
public sealed class SessionViewFile
{
    public string SaveHash { get; set; } = "";
    public Dictionary<string, SessionPlayerView> Players { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>每个存档一份的「玩家看什么」文件。每玩家只保留一条最新记录（覆盖写，不追加）。
///
/// 为什么不写进存档：存档是「世界状态」，视图是「会话状态」。基线下发直接把 `.rws` 推给客户端，
/// 存档里多一个玩家的相机坐标毫无意义，反而会在两端来回覆盖。
///
/// 这个类不引用 Verse，所以能被单测直接覆盖（`Tests/TurboTick.Tests.csproj` 引用 Networking）。</summary>
public sealed class SessionViewStore
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, SessionPlayerView>> cache = new(StringComparer.Ordinal);
    private readonly Action<string>? warn;

    public SessionViewStore(string directory, Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        this.warn = warn;
    }

    public string Directory { get; }

    /// <summary>存档文件名里的那 16 位。取法与 `Source/ContentProbe.cs` 的 `modHash` 相同：
    /// **SHA256 → 十六进制 → 前 16 位小写**，不另造一套编号。
    /// 传进来的通常已经是那份 digest（`SnapshotManifest.Id`），那就直接取前 16 位。</summary>
    public static string Hash16(string saveHash)
    {
        ArgumentNullException.ThrowIfNull(saveHash);
        if (saveHash.Length >= 16 && saveHash.All(Uri.IsHexDigit)) return saveHash[..16].ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(saveHash));
        return Convert.ToHexStringLower(digest)[..16];
    }

    public string PathFor(string saveHash) => Path.Combine(Directory, Hash16(saveHash) + ".json");

    /// <summary>读某个 token 的记录。文件不存在 / 坏 JSON / 权限不足都按「没有记录」处理并告警一次。</summary>
    public bool TryGet(string saveHash, string token, out SessionPlayerView view)
    {
        view = null!;
        if (!SessionTokens.IsValid(token)) return false;
        var players = Load(saveHash);
        lock (gate) return players.TryGetValue(token, out view!);
    }

    /// <summary>这个存档下有多少条记录。日志用，不作判据。</summary>
    public int Count(string saveHash)
    {
        var players = Load(saveHash);
        lock (gate) return players.Count;
    }

    /// <summary>覆盖写一条记录并落盘。token 非法时返回 null（调用方应当已经发放过合法 token）。</summary>
    public SessionPlayerView? Save(string saveHash, string token, string name, SessionViewSnapshot view)
    {
        if (!SessionTokens.IsValid(token)) return null;
        var snapshot = SessionViewSnapshot.Sanitize(view);
        var record = new SessionPlayerView(
            (name ?? "").Trim() is { Length: > 0 and <= 80 } trimmed ? trimmed : "player",
            snapshot.MapId, snapshot.Selected,
            new SessionCamera(snapshot.X, snapshot.Z, snapshot.Size),
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        var players = Load(saveHash);
        lock (gate) players[token] = record;
        Write(saveHash, players);
        return record;
    }

    /// <summary>只让测试与诊断用：丢掉内存缓存，强制下次从磁盘读。</summary>
    public void ForgetCached() { lock (gate) cache.Clear(); }

    private Dictionary<string, SessionPlayerView> Load(string saveHash)
    {
        var key = Hash16(saveHash);
        lock (gate)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            var players = new Dictionary<string, SessionPlayerView>(StringComparer.Ordinal);
            var path = Path.Combine(Directory, key + ".json");
            try
            {
                if (File.Exists(path))
                {
                    var file = JsonSerializer.Deserialize<SessionViewFile>(File.ReadAllBytes(path), FileOptions);
                    if (file?.Players is not null)
                        foreach (var pair in file.Players)
                            // 文件被人手改过也必须能读：只收合法 token 与完整记录，其余跳过。
                            if (SessionTokens.IsValid(pair.Key) && pair.Value is not null)
                                players[pair.Key] = pair.Value;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                // 半截 JSON 不能阻断进服，但必须留下痕迹：静默丢弃会让「视图没恢复」看起来像代码没跑。
                Warn($"读不动 {path}（{error.GetType().Name}: {error.Message}），按没有记录处理");
                players.Clear();
            }
            cache[key] = players;
            return players;
        }
    }

    private void Write(string saveHash, Dictionary<string, SessionPlayerView> players)
    {
        var key = Hash16(saveHash);
        var path = Path.Combine(Directory, key + ".json");
        // 先写同目录 `.tmp` 再 Move：进程被强杀时留在磁盘上的要么是旧文件，要么是新文件，
        // 不会出现「读出来一半」的 JSON —— 那种文件正是「视图恢复时好时坏」的来源。
        var temporary = path + ".tmp";
        SessionViewFile file;
        lock (gate)
            file = new SessionViewFile
            {
                SaveHash = saveHash,
                Players = new Dictionary<string, SessionPlayerView>(players, StringComparer.Ordinal)
            };
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(file, FileOptions);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            Warn($"写不动 {path}（{error.GetType().Name}: {error.Message}），本条视图状态只留在内存");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Warn(string message)
    {
        try { warn?.Invoke(message); }
        catch (Exception) { /* 告警通道自己出问题不能再把调用方拖下去 */ }
    }
}
