using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Networking;

/// <summary>How far the administration page is allowed to reach.</summary>
public enum AdminBindKind
{
    /// <summary>No listener at all: port 0, or a configuration that was refused.</summary>
    Disabled,
    /// <summary>Only this machine can connect. Writes still need the management password.</summary>
    Loopback,
    /// <summary>Reachable from the network. Refused unless a management password exists.</summary>
    External
}

/// <summary>
/// The resolved form of <c>-turbotick-admin-bind</c>.
///
/// The rule this type exists to enforce: a panel that can save, download a game or quit a server must never be
/// reachable from another machine without a password. Widening the bind is therefore allowed, but widening it
/// without <c>TURBOTICK_ADMIN_PASSWORD</c> produces a <see cref="Refusal"/> instead of a listener.
/// </summary>
public sealed record AdminBindPlan(AdminBindKind Kind, string Address, string Spec)
{
    public bool Listening => Kind != AdminBindKind.Disabled;
    public bool External => Kind == AdminBindKind.External;
    /// <summary>Why no listener was started, or null when the plan is usable.</summary>
    public string? Refusal { get; init; }
    public bool RequiresLogin => Kind == AdminBindKind.External;

    public static AdminBindPlan Disabled(string spec, string? refusal) =>
        new(AdminBindKind.Disabled, "127.0.0.1", spec) { Refusal = refusal };
}

/// <summary>Parsing and policy for the administration bind address. No sockets, no Verse: unit-testable.</summary>
public static class AdminBinding
{
    public const string LoopbackText = "127.0.0.1";
    public const string AnyText = "0.0.0.0";

    /// <summary>
    /// Resolves a bind spec into a plan.
    ///
    /// Accepted spellings: empty (loopback), <c>localhost</c>, a literal IPv4/IPv6 address, <c>*</c>, <c>any</c>.
    /// A host name is deliberately not resolved here: the caller would have to re-resolve it on every start and a
    /// DNS answer changing under a running server is exactly the kind of surprise an admin panel cannot afford.
    /// </summary>
    public static AdminBindPlan Resolve(string? bindSpec, int port, string? password)
    {
        var spec = string.IsNullOrWhiteSpace(bindSpec) ? LoopbackText : bindSpec.Trim();
        if (port <= 0) return AdminBindPlan.Disabled(spec, "端口 0：管理页未启用");
        if (port > 65535) return AdminBindPlan.Disabled(spec, "端口超出范围：" + port);

        IPAddress address;
        var lowered = spec.ToLowerInvariant();
        if (lowered is "*" or "any" or "all") address = IPAddress.Any;
        else if (lowered is "localhost") address = IPAddress.Loopback;
        else
        {
            var text = spec.StartsWith('[') && spec.EndsWith(']') ? spec[1..^1] : spec;
            if (!IPAddress.TryParse(text, out var parsed))
                return AdminBindPlan.Disabled(spec, "不是 IP 地址：" + spec + "（只接受字面 IP、localhost、*）");
            address = parsed;
        }

        var loopback = IPAddress.IsLoopback(address);
        if (!loopback && string.IsNullOrEmpty(password))
            return AdminBindPlan.Disabled(spec,
                "拒绝把管理页绑到 " + spec + "：绑定非回环地址时必须设置 TURBOTICK_ADMIN_PASSWORD"
                + "（否则任何能访问该端口的人都能下载游戏、载入存档或关掉服务器）");

        return new AdminBindPlan(loopback ? AdminBindKind.Loopback : AdminBindKind.External,
            address.ToString(), spec);
    }

    /// <summary>Convenience for logging: what an operator should type into a browser.</summary>
    public static string Url(AdminBindPlan plan, int port) =>
        "http://" + (plan.Address == AnyText ? "<本机 IP>" : plan.Address) + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
}

/// <summary>
/// In-process session table for the login page.
///
/// A password typed on a login form must not be replayed on every request: the form exchange yields a random
/// token, the token lives behind an HttpOnly cookie, and it expires. Nothing is persisted - a restart invalidates
/// every session, which is the correct behaviour for a management surface.
/// </summary>
public sealed class AdminSessions
{
    public const int TokenBytes = 32;

    /// <summary>Optional log sink: this assembly must not depend on Verse, so the host supplies one.</summary>
    public Action<string>? Note { get; set; }
    public const int DefaultLifetimeMinutes = 12 * 60;
    public const int DefaultMaximum = 64;

    private readonly Dictionary<string, long> sessions = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly long lifetimeMilliseconds;
    private readonly int maximum;
    private readonly Func<long> now;

    public AdminSessions(int lifetimeMinutes = DefaultLifetimeMinutes, int maximum = DefaultMaximum, Func<long>? clock = null)
    {
        if (lifetimeMinutes is < 1 or > 30 * 24 * 60) throw new ArgumentOutOfRangeException(nameof(lifetimeMinutes));
        if (maximum is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximum));
        this.lifetimeMilliseconds = lifetimeMinutes * 60_000L;
        this.maximum = maximum;
        now = clock ?? (() => Environment.TickCount64);
    }

    public int Count { get { lock (gate) { Prune(now()); return sessions.Count; } } }

    /// <summary>Creates a session. Returns the token and its absolute expiry in milliseconds of <see cref="Environment.TickCount64"/>.</summary>
    public string Issue(out long expiresAt)
    {
        var token = NewToken();
        lock (gate)
        {
            var current = now();
            Prune(current);
            // Bounded: an operator who logs in from many tabs must not grow this table without limit.
            if (sessions.Count >= maximum) EvictOldest();
            expiresAt = current + lifetimeMilliseconds;
            sessions[token] = expiresAt;
        }
        return token;
    }

    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return false;
        lock (gate)
        {
            var current = now();
            Prune(current);
            return sessions.TryGetValue(token, out var expires) && expires > current;
        }
    }

    public bool Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (gate)
        {
            Prune(now());
            return sessions.Remove(token);
        }
    }

    public void Clear() { lock (gate) sessions.Clear(); }

    /// <summary>
    /// Persists the session table so an already-authenticated browser stays authenticated across a game-process
    /// restart (a world switch needs one: reloading a world in-process segfaults the engine).
    ///
    /// Expiry is stored as <b>absolute UTC</b>, not as the in-memory <see cref="Environment.TickCount64"/> value:
    /// that counter is relative to a process, so a naive dump would be meaningless to the next one - tokens would
    /// either expire instantly or live forever. Tokens themselves stay opaque random strings.
    /// </summary>
    public void Save(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            List<object> rows;
            lock (gate)
            {
                var current = now();
                Prune(current);
                rows = sessions.Select(pair => (object)new
                {
                    token = pair.Key,
                    expiresUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + (pair.Value - current)
                }).ToList();
            }
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary,
                System.Text.Json.JsonSerializer.Serialize(new { sessions = rows }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A session store that cannot be written costs one re-login after a switch; it must never break login.
            Note?.Invoke("会话表持久化失败（不影响本次登录）：" + error.GetType().Name);
        }
    }

    /// <summary>Restores a saved session table, dropping anything already expired. See <see cref="Save"/>.</summary>
    public void Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("sessions", out var rows)) return;
            var current = now();
            var utcNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (gate)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("token", out var token) || !row.TryGetProperty("expiresUtc", out var expiry))
                        continue;
                    var value = token.GetString();
                    if (string.IsNullOrEmpty(value) || value.Length > 256) continue;
                    var remaining = expiry.GetInt64() - utcNow;
                    // Already expired, or absurdly far in the future (a clock change or a hand-edited file): drop it.
                    if (remaining <= 0 || remaining > lifetimeMilliseconds) continue;
                    if (sessions.Count >= maximum) break;
                    sessions[value] = current + remaining;
                }
            }
            Note?.Invoke("已恢复 " + Count + " 个管理会话（进程重启后无需重新登录）");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or FormatException)
        {
            Note?.Invoke("会话表读取失败（按未登录处理）：" + error.GetType().Name);
        }
    }

    private void Prune(long current)
    {
        List<string>? dead = null;
        foreach (var pair in sessions)
            if (pair.Value <= current) (dead ??= []).Add(pair.Key);
        if (dead is null) return;
        foreach (var token in dead) sessions.Remove(token);
    }

    private void EvictOldest()
    {
        string? oldest = null;
        var earliest = long.MaxValue;
        foreach (var pair in sessions)
            if (pair.Value < earliest) { earliest = pair.Value; oldest = pair.Key; }
        if (oldest is not null) sessions.Remove(oldest);
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));
}

/// <summary>One known game version: the key an operator picks, and the steamcmd branch that produces it.</summary>
public sealed record GameVersionEntry(string Key, string Label, string SteamAppId, string? BetaBranch, string Note);

/// <summary>
/// The version table. Exactly one entry today, and deliberately not a multi-version machine: the schema exists so
/// that adding <c>1.8</c> later is a data change, not a rewrite of the download path.
///
/// <see cref="GameVersionEntry.BetaBranch"/> was measured, not guessed: on 2026-11-12 the deployment host
/// (<c>192.168.0.29</c>) had <c>steamapps/appmanifest_294100.acf</c> with <c>UserConfig { BetaKey unstable }</c>
/// pointing at <c>1.7.5005 rev1082</c> / buildid <c>26571351</c>. <c>1.7.5000 rev1275</c> is what the same host
/// had before the branch moved. When the 1.7 line ships on the default branch, set <c>BetaBranch</c> to null:
/// the command builder then omits <c>-beta</c> entirely.
/// </summary>
public static class GameVersions
{
    public const string SteamAppId = "294100";
    public const string DefaultKey = "1.7";

    public static IReadOnlyList<GameVersionEntry> All { get; } =
    [
        new(DefaultKey, "1.7 · RimWorld 1.7（Waylith）", SteamAppId, "unstable",
            "steamcmd -beta unstable；实测 192.168.0.29 得到 1.7.5005 rev1082 / buildid 26571351。"
            + "若上游把 1.7 移到默认分支，把 BetaBranch 改成 null 即可（命令里就不再有 -beta）")
    ];

    public static GameVersionEntry? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return All[0];
        var wanted = key.Trim();
        foreach (var entry in All)
            if (string.Equals(entry.Key, wanted, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }

    public static IReadOnlyList<string> Keys
    {
        get
        {
            var keys = new string[All.Count];
            for (var index = 0; index < All.Count; index++) keys[index] = All[index].Key;
            return keys;
        }
    }
}

/// <summary>A steamcmd invocation, built so that the password can never be part of it.</summary>
public sealed record SteamCommandLine(string FileName, IReadOnlyList<string> Arguments)
{
    public string Display => FileName + " " + string.Join(' ', Arguments);
}

/// <summary>Where a steamcmd run has got to. Immutable: the download worker publishes a new one per line.</summary>
public sealed record SteamProgress(
    string Phase,
    double Percent,
    long BytesDone,
    long BytesTotal,
    string? Detail,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    bool Ok)
{
    public bool Running => FinishedUtc is null;
    public static SteamProgress Idle { get; } =
        new("idle", 0, 0, 0, "尚未开始", DateTime.MinValue, null, false);

    public SteamProgress With(string phase, double percent, long done, long total, string? detail) =>
        this with { Phase = phase, Percent = percent, BytesDone = done, BytesTotal = total, Detail = detail };
}

/// <summary>
/// Pure parts of driving steamcmd: argument construction, progress recognition, and the classification of the
/// lines that mean "this will not succeed".
///
/// The password is never an argument. Valve's client prompts for it on stdin, and a password on a command line is
/// readable from <c>ps</c> by every user on the host and lands in shell history and process accounting.
/// </summary>
public static class SteamCmd
{
    public const string DefaultExecutable = "/usr/games/steamcmd";
    public const string Anonymous = "anonymous";
    private const int MaximumAccountLength = 64;

    /// <summary>Steam account names are restricted to what Valve accepts, which also keeps argv printable.</summary>
    public static bool IsValidAccount(string? account, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(account)) { reason = "账号不能为空"; return false; }
        var value = account.Trim();
        if (value.Length > MaximumAccountLength) { reason = "账号过长"; return false; }
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '@')) { reason = "账号含非法字符"; return false; }
        return true;
    }

    /// <summary>
    /// <c>+force_install_dir DIR +@sSteamCmdForcePlatformType linux +login ACCOUNT +app_update 294100 [-beta BRANCH]
    /// validate +quit</c>.
    ///
    /// <c>+@sSteamCmdForcePlatformType linux</c> is what makes a Linux host download the Linux depot even when it
    /// would otherwise guess; without it a container can end up with Windows depots.
    /// </summary>
    public static SteamCommandLine GameUpdate(string executable, string installDirectory, string? account,
        string appId, string? betaBranch, bool validate)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)) throw new ArgumentException("install directory required", nameof(installDirectory));
        var arguments = new List<string>(16)
        {
            "+force_install_dir", installDirectory,
            "+@sSteamCmdForcePlatformType", "linux",
            "+login", string.IsNullOrWhiteSpace(account) ? Anonymous : account.Trim(),
            "+app_update", appId
        };
        if (!string.IsNullOrWhiteSpace(betaBranch)) { arguments.Add("-beta"); arguments.Add(betaBranch.Trim()); }
        if (validate) arguments.Add("validate");
        arguments.Add("+quit");
        return new SteamCommandLine(string.IsNullOrWhiteSpace(executable) ? DefaultExecutable : executable, arguments);
    }

    /// <summary>Workshop download for one item (Harmony is the only one the server needs).</summary>
    /// <summary>
    /// Workshop download for one item.
    ///
    /// <b>Always anonymous.</b> Workshop content is public: Valve serves it to <c>+login anonymous</c>, and measured
    /// on the deployment host, ten items (190 MB) downloaded with no Steam account at all. Requiring a login here
    /// was my error - only <c>app_update</c> (the game itself, depot-licensed) needs a real account.
    ///
    /// The <c>installDirectory</c> is what makes the content land somewhere the game can find it: steamcmd writes to
    /// <c>&lt;install&gt;/steamapps/workshop/content/&lt;appId&gt;/&lt;id&gt;</c>.
    /// </summary>
    public static SteamCommandLine WorkshopItem(string executable, string installDirectory, string appId,
        string publishedFileId, bool validate = true)
    {
        var arguments = new List<string>(12)
        {
            "+force_install_dir", installDirectory,
            "+login", Anonymous,
            "+workshop_download_item", appId, publishedFileId
        };
        if (validate) arguments.Add("validate");
        arguments.Add("+quit");
        return new SteamCommandLine(string.IsNullOrWhiteSpace(executable) ? DefaultExecutable : executable, arguments);
    }

    /// <summary>
    /// Recognises steamcmd's progress lines, e.g.
    /// <c>Update state (0x61) downloading, progress: 42.31 (3145728 / 7340032)</c>.
    /// Returns false for lines that carry no progress, so the caller can keep the last known value.
    /// </summary>
    public static bool TryReadProgress(string? line, out SteamProgress? progress)
    {
        progress = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var marker = line.IndexOf("progress:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        var rest = line[(marker + 9)..].Trim();
        var percentEnd = 0;
        while (percentEnd < rest.Length && (char.IsAsciiDigit(rest[percentEnd]) || rest[percentEnd] == '.')) percentEnd++;
        if (percentEnd == 0) return false;
        if (!double.TryParse(rest[..percentEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return false;
        long done = 0, total = 0;
        var open = rest.IndexOf('(');
        var close = rest.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var halves = rest[(open + 1)..close].Split('/');
            if (halves.Length == 2)
            {
                long.TryParse(halves[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out done);
                long.TryParse(halves[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
            }
        }
        // The phase is what the operator reads while the percentage sits at 0 for ten minutes: "preallocating"
        // and "verifying" both look like a hang if only a number is shown.
        var phase = "downloading";
        var stateAt = line.IndexOf("state (", StringComparison.OrdinalIgnoreCase);
        if (stateAt >= 0)
        {
            var after = line.IndexOf(')', stateAt + 7);
            if (after > 0)
            {
                var words = line[(after + 1)..marker].Trim().TrimEnd(',').Trim();
                if (words.Length > 0 && words.Length < 40) phase = words;
            }
        }
        progress = new SteamProgress(phase, Math.Clamp(percent, 0, 100), done, total, null,
            DateTime.UtcNow, null, false);
        return true;
    }

    /// <summary>
    /// Lines after which the run cannot succeed. Matched case-insensitively because steamcmd's own casing is not
    /// stable across versions, and returned with the reason so the page can show it instead of "failed".
    /// </summary>
    public static bool TryReadFailure(string? line, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        var text = line.Trim();
        if (text.Contains("Login Failure", StringComparison.OrdinalIgnoreCase)) { reason = "Steam 登录失败（账号或密码不对）"; return true; }
        if (text.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase)) { reason = "Steam 密码不正确"; return true; }
        if (text.Contains("Account Logon Denied", StringComparison.OrdinalIgnoreCase)) { reason = "Steam 拒绝登录（可能需要先在本机完成 Steam Guard）"; return true; }
        if (text.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase)) { reason = "Steam 限流，稍后重试"; return true; }
        if (text.Contains("Two-factor code", StringComparison.OrdinalIgnoreCase)) { reason = "需要两步验证码：请在能交互的终端里跑一次 steamcmd 完成 Steam Guard"; return true; }
        if (text.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase)) { reason = "steamcmd 报错：" + text; return true; }
        if (text.Contains("Failure:", StringComparison.OrdinalIgnoreCase) && text.Contains("app", StringComparison.OrdinalIgnoreCase))
        { reason = "下载失败：" + text; return true; }
        return false;
    }

    /// <summary>The one line that means the update finished, so a worker does not have to guess from exit code alone.</summary>
    public static bool ReportsSuccess(string? line) =>
        line is not null && line.Contains("fully installed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One save file under the profile's Saves directory.</summary>
public sealed record SaveEntry(string Name, string FileName, long Bytes, DateTime ModifiedUtc);

/// <summary>
/// Save names as the authority accepts them. The name travels into <c>-turbotick-save=</c> and is resolved against
/// the Saves directory, so it must not be able to name a path: this is the only place that decides that.
/// </summary>
public static class SaveLibrary
{
    public const string Extension = ".rws";
    public const int MaximumNameLength = 96;

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var value = name.Trim();
        if (value.Length > MaximumNameLength) return false;
        if (value is "." or "..") return false;
        if (value.Contains('/') || value.Contains('\\') || value.Contains(':')) return false;
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        // A leading dot would hide the file and a trailing dot is unrepresentable on Windows clients.
        return value[0] != '.' && value[^1] != '.';
    }

    /// <summary>Accepts <c>name</c> or <c>name.rws</c>, returns <c>name</c>, or null when the name is unusable.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) value = value[..^Extension.Length];
        return IsValidName(value) ? value : null;
    }

    public static string PathFor(string directory, string name) => Path.Combine(directory, name + Extension);

    /// <summary>Newest first. A missing directory is an empty list, not an error: a fresh profile has no Saves yet.</summary>
    public static IReadOnlyList<SaveEntry> List(string? directory)
    {
        var entries = new List<SaveEntry>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return entries;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly))
            {
                var file = new FileInfo(path);
                var name = Path.GetFileNameWithoutExtension(file.Name);
                if (!IsValidName(name)) continue;
                entries.Add(new SaveEntry(name, file.Name, file.Length, file.LastWriteTimeUtc));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return entries;
        }
        entries.Sort((left, right) => right.ModifiedUtc.CompareTo(left.ModifiedUtc));
        return entries;
    }
}

/// <summary>
/// Merges a client's mod list into the server's ModsConfig: the client's order preserved, deduplicated, and only
/// for mods the server actually has installed.
///
/// Written as a pure function because the interesting cases are all "what does the server do with a list that does
/// not match its disk" - a missing mod must be reported, not silently dropped, and Core must be present, but it is
/// not hoisted to index 0: Harmony ships <c>&lt;loadBefore&gt;&lt;li&gt;Ludeon.RimWorld&lt;/li&gt;&lt;/loadBefore&gt;</c>,
/// so every working client records Harmony at 0 and Core at 1, and rewriting that into Core-first makes
/// <c>ScribeMetaHeaderUtility.ModListsMatch</c> report a mod-list change on every load. Core moves to the front only
/// when the request never named it, because then there is no recorded position to respect.
/// </summary>
public static class ModsConfigPlan
{
    public const string Core = "ludeon.rimworld";

    public static IReadOnlyList<string> CohesionDlc { get; } =
    [
        "ludeon.rimworld.royalty",
        "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech",
        "ludeon.rimworld.anomaly",
        "ludeon.rimworld.odyssey"
    ];

    public sealed record Result(IReadOnlyList<string> ActiveMods, IReadOnlyList<string> Missing, IReadOnlyList<string> Ignored);

    /// <summary>
    /// <paramref name="installed"/> are lower-cased packageIds present on the server's disk.
    /// <paramref name="requested"/> is the client's order, and it is kept as given. Core is guaranteed present at
    /// whichever position the request put it; anything not installed is reported in <see cref="Result.Missing"/> and
    /// left out, because writing it would make the game fail to start.
    /// </summary>
    public static Result Merge(IEnumerable<string> requested, ISet<string> installed, bool includeDlc)
    {
        var active = new List<string>(32);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var ignored = new List<string>();
        foreach (var raw in requested)
        {
            var id = raw?.Trim().ToLowerInvariant() ?? "";
            if (id.Length == 0) continue;
            if (!seen.Add(id)) continue;
            if (string.Equals(id, Core, StringComparison.Ordinal)) active.Add(Core);
            else if (installed.Contains(id)) active.Add(id);
            else if (id.Contains('.') && !id.StartsWith("ludeon.", StringComparison.Ordinal)) missing.Add(id);
            else ignored.Add(id);
        }
        // Only when the request never named Core is the position decided here - front, because nothing recorded one.
        if (seen.Add(Core)) active.Insert(0, Core);
        if (includeDlc)
        {
            // Directly after Core, which is where a game-written config puts them, and after Harmony when Harmony
            // is first. Inserting rather than appending keeps the client's relative order for everything else.
            var at = active.IndexOf(Core) + 1;
            foreach (var dlc in CohesionDlc)
                if (installed.Contains(dlc) && seen.Add(dlc)) active.Insert(at++, dlc);
        }
        return new Result(active, missing, ignored);
    }

    /// <summary>The XML RimWorld reads. <c>knownExpansions</c> is written from the same DLC list so the game does
    /// not believe the content is absent.</summary>
    public static string Xml(string gameVersion, IEnumerable<string> activeMods, IEnumerable<string> knownExpansions)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModsConfigData>\n");
        builder.Append("  <version>").Append(Escape(gameVersion)).Append("</version>\n");
        builder.Append("  <activeMods>\n");
        foreach (var id in activeMods) builder.Append("    <li>").Append(Escape(id)).Append("</li>\n");
        builder.Append("  </activeMods>\n");
        var expansions = new List<string>(knownExpansions);
        if (expansions.Count == 0) builder.Append("  <knownExpansions />\n");
        else
        {
            builder.Append("  <knownExpansions>\n");
            foreach (var id in expansions) builder.Append("    <li>").Append(Escape(id)).Append("</li>\n");
            builder.Append("  </knownExpansions>\n");
        }
        builder.Append("</ModsConfigData>\n");
        return builder.ToString();
    }

    private static string Escape(string? value) =>
        (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
