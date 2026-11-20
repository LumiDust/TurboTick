using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TurboTick.Networking;

namespace TurboTick.Portal;

/// <summary>Everything the portal needs to know about the deployment. Built on the game thread at startup.</summary>
internal sealed record AdminPageConfig(
    string? Password,
    int Port,
    string? BindSpec,
    int CheckpointMinutes,
    string SavesDirectory,
    string DataDirectory,
    string SteamcmdPath,
    string SteamcmdRoot,
    string GameDirectory,
    bool CohesionDlc,
    string? SteamAccount,
    string BackupDirectory,
    string ModsConfigPath,
    bool AutoApplyMods,
    string ModsDirectory,
    string? SteamProxy)
{
    internal static AdminPageConfig Default { get; } = new(
        Password: null, Port: 0, BindSpec: AdminBinding.LoopbackText, CheckpointMinutes: 0,
        SavesDirectory: "", DataDirectory: "", SteamcmdPath: SteamCmd.DefaultExecutable,
        SteamcmdRoot: "", GameDirectory: "", CohesionDlc: false, SteamAccount: null, BackupDirectory: "",
        ModsConfigPath: "", AutoApplyMods: true, ModsDirectory: "", SteamProxy: null);
}

/// <summary>
/// The administration page of a headless authority: status, save/quit/checkpoint, Steam login, game download,
/// server start/stop and the save list.
///
/// Four rules shape this file:
///
/// 1. <b>The bind is a decision, not a default.</b> <c>127.0.0.1</c> unless the operator asks otherwise, and an
///    external bind without <c>TURBOTICK_ADMIN_PASSWORD</c> is refused outright - a panel that can download a game,
///    load a save or quit a server has no business being unauthenticated on a LAN.
/// 2. <b>HTTP threads never touch Verse.</b> Game actions are published on a <see cref="ConcurrentQueue{T}"/> and
///    consumed on the game thread; everything read back comes from an immutable snapshot published by the game
///    thread. The one exception is deliberate: steamcmd runs on its own task and owns nothing the game thread holds.
/// 3. <b>Secrets do not travel in URLs</b>, and a password typed into the login form becomes a random, expiring,
///    HttpOnly + SameSite=Strict session cookie instead of being replayed on every request.
/// 4. <b>Every failure is a <see cref="Log.Warning"/> plus an HTTP status.</b> The page exists to watch a running
///    server; it must never be the reason one stops.
/// </summary>
internal static partial class AdminPage
{
    internal const string TokenHeader = "X-TurboTick-Token";
    internal const string CsrfHeader = "X-TurboTick-Csrf";
    internal const string CookieName = "turbotick_admin";
    internal const string SessionCookieName = "turbotick_admin_session";

    private const int MaximumConnections = 32;
    private const int MaximumLogLines = 24;
    private const int AttemptWindowMilliseconds = 10_000;
    private const int MaximumAttemptsPerWindow = 12;
    private const int MaximumBodyBytes = 64 * 1024;
    /// <summary>A real 1.7 colony is 10-25 MB; 256 MB is "obviously not a save" territory, not a budget.</summary>
    internal const long MaximumUploadBytes = SaveImport.MaximumBytes;
    private const int UploadChunkBytes = 64 * 1024;
    private const int CatalogRefreshMilliseconds = 30_000;

    private static readonly object StartGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The primary consumer of this JSON is a person with curl. Escaping every CJK character as \uXXXX made
        // the response unreadable in a terminal for no gain: the content type is application/json with
        // nosniff, so '<' in a log line cannot become markup here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Enums as names: "Manual" is a verdict a human acts on, "5" is a number nobody can read. The page also
        // keys its badges off these strings.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static TcpListener? listener;
    private static CancellationTokenSource? lifetime;
    private static Task? accepting;
    private static int boundPort;
    private static string bindAddress = AdminBinding.LoopbackText;
    private static AdminBindPlan plan = AdminBindPlan.Disabled(AdminBinding.LoopbackText, null);
    private static AdminPageConfig config = AdminPageConfig.Default;
    private static string? password;
    private static AdminSessions sessions = new();

    /// <summary>Where the admin session table lives; null (persistence off) when there is no data directory.</summary>
    private static string? SessionStorePath()
    {
        var directory = config.DataDirectory;
        return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "admin-sessions.json");
    }
    private static volatile AdminStatus? published;

    /// <summary>The game process, when this executable owns it; null when it was started some other way.</summary>
    private static GameSupervisor? _supervisor;

    // File-driven status cache: the game publishes, this process reads. The stamp is the guard against both
    // re-parsing an unchanged document and trusting a document the game never finished writing.
    private static long statusStamp;
    private static GameStatus? lastStatus;

    // Portal-thread only: rate sampling, status cadence and the checkpoint clock. "Frames" here are portal ticks,
    // not game ticks - after the split this process has no idea how fast the game is drawing, and inventing one
    // would be worse than reporting nothing.
    private static long frameCount;
    private static long fpsFrames;
    private static long fpsStartedMs;
    private static double? fps;
    private static long statusAtMs;
    private static long wireAtMs;
    private static long wireOut;
    private static long wireIn;
    private static double outRate;
    private static double inRate;
    private static long nextCheckpointAtMs;
    private static string? nextCheckpointUtc;
    private static long checkpointsTaken;
    private static volatile AdminPreflight? preflight;
    // DLC/mod catalogs: pure disk work now, so an immutable snapshot refreshed on a timer is all that is needed.
    private static volatile AdminDlc? dlc;
    private static volatile AdminMods? mods;
    private static long catalogAtMs;
    private static long modsAtMs;
    private static DateTime catalogStamp;
    private static readonly Dictionary<string, (DateTime Stamp, string? AppId)> dlcAppIds = new(StringComparer.OrdinalIgnoreCase);

    // Written by HTTP threads, read by HTTP threads (never by the game thread).
    private static readonly object AttemptGate = new();
    private static long attemptWindowStart;
    private static int attempts;

    // Written on the game thread, read by HTTP threads for the control block.
    private static int acceptedCommands;
    private static volatile string? lastCommand;
    private static volatile string? lastResult;
    private static volatile string? lastCommandUtc;

    internal static bool Running => Volatile.Read(ref listener) is not null;
    internal static int Port => Volatile.Read(ref boundPort);
    internal static AdminBindPlan Plan => plan;
    internal static AdminPageConfig Configuration => config;

    /// <summary>
    /// Wires the supervisor in before the listener starts, so the very first status already knows whether a game
    /// process exists. Null is a supported answer: this executable can also be pointed at a game it does not own.
    /// </summary>
    internal static void AttachSupervisor(GameSupervisor supervisor) => _supervisor = supervisor;

    /// <summary>Resolves the bind and starts the listener. Refusals are <see cref="Log.Error"/>: they are the
    /// operator's configuration mistake and must be visible, but the portal keeps running without the page.</summary>
    internal static void Start(AdminPageConfig options, string? steamAccountHint = null)
    {
        var resolved = AdminBinding.Resolve(options.BindSpec, options.Port, options.Password);
        if (!resolved.Listening)
        {
            if (resolved.Refusal is null)
                Log.Message("[TurboTick admin] 管理页未启用（端口 0 或未配置）。");
            else
                Log.Error("[TurboTick admin] " + resolved.Refusal);
            plan = resolved;
            return;
        }
        lock (StartGate)
        {
            if (listener is not null) return;
            try
            {
                var address = IPAddress.Parse(resolved.Address);
                var candidate = new TcpListener(address, options.Port);
                candidate.Start(MaximumConnections);
                listener = candidate;
                config = options;
                password = string.IsNullOrEmpty(options.Password) ? null : options.Password;
                sessions = new AdminSessions
                {
                    // Networking has no logging of its own: it hands the message up so the operator's log keeps a
                    // single format instead of half the lines coming from a library.
                    Note = message => Log.Message("[TurboTick admin] " + message)
                };
                // Sessions are opaque tokens with an absolute expiry, persisted beside the rest of the admin state.
                // The game process restarts often; a login that dies with it would make the page useless exactly
                // when an operator needs it, and this process is the one that stays.
                sessions.Load(SessionStorePath());
                plan = resolved;
                ConfigureAutoApply(options);
                SteamCmdRunner.Configure(options.SteamcmdPath, options.SteamcmdRoot, options.GameDirectory, options.DataDirectory);
                // Deployment config first, then the value the page saved last time: the page is the operator's own
                // decision and outranks a compose file that may not have been touched since.
                SteamCmdRunner.ConfigureProxy(options.SteamProxy);
                SteamCmdRunner.LoadProxy();
                SteamCmdRunner.LoadState();
                CommandInbox.Configure(options.DataDirectory);
                var source = new CancellationTokenSource();
                lifetime = source;
                boundPort = ((IPEndPoint)candidate.LocalEndpoint).Port;
                bindAddress = resolved.Address;
                // Start with a usable status instead of a 503: the page should render "starting" rather than an
                // error while the game is still loading.
                published = EmptyStatus();
                accepting = Task.Run(() => AcceptLoopAsync(candidate, source.Token));
                if (resolved.RequiresLogin)
                    Log.Message("[TurboTick admin] 管理页 " + AdminBinding.Url(resolved, boundPort)
                        + " （已绑非回环地址：必须登录，密码来自 TURBOTICK_ADMIN_PASSWORD）");
                else if (password is null)
                    Log.Message("[TurboTick admin] 管理页 http://" + bindAddress + ":" + boundPort
                        + "/ （只读：未设置 TURBOTICK_ADMIN_PASSWORD，写操作被拒绝）");
                else
                    Log.Message("[TurboTick admin] 管理页 http://" + bindAddress + ":" + boundPort + "/ （写操作需要管理密码）");
                if (options.CheckpointMinutes > 0)
                    Log.Message("[TurboTick admin] 定时检查点：每 " + options.CheckpointMinutes + " 分钟一次（复用 save 命令路径）");
                if (steamAccountHint is not null) Log.Message("[TurboTick admin] Steam 账号：" + steamAccountHint);
            }
            catch (Exception error)
            {
                Log.Error("[TurboTick admin] 管理页启动失败（主循环继续）：" + error.GetType().Name + ": " + error.Message);
                try { listener?.Stop(); } catch (Exception) { }
                listener = null;
            }
        }
    }

    internal static void Stop()
    {
        lock (StartGate)
        {
            var source = lifetime;
            var loop = accepting;
            var socket = listener;
            lifetime = null;
            accepting = null;
            listener = null;
            try { source?.Cancel(); } catch (Exception) { }
            try { socket?.Stop(); } catch (Exception) { }
            try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            try { source?.Dispose(); } catch (Exception) { }
            try { sessions.Clear(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// One portal tick, on a timer (this used to be the game's frame).
    ///
    /// The cadence is no longer borrowed from anything: the page has to keep answering while the game is starting,
    /// dying or stopped, so a 500 ms timer in this process is both simpler and the only thing that works.
    /// </summary>
    internal static void Tick()
    {
        frameCount++;
        if (listener is null) return;
        var now = Environment.TickCount64;
        SampleFrameRate(now);
        RunScheduledCheckpoint(now);
        if (now - statusAtMs < 400) return;
        statusAtMs = now;
        RefreshPreflight();
        RefreshCatalogs(now);
        try { published = BuildStatus(now); }
        catch (Exception error) { Log.Warning("[TurboTick admin] 状态快照失败：" + error.GetType().Name + ": " + error.Message); }
    }

    /// <summary>
    /// Refreshes the DLC and mod catalogs.
    ///
    /// Both are disk work now: <c>Data/&lt;Dlc&gt;</c> for expansions, <c>ModsConfig.xml</c> plus a scan of the mods
    /// roots for mods. That is the point of the split - the answer to "what would the game load" no longer requires
    /// the game to be running, which is exactly when an operator is asking.
    /// </summary>
    private static void RefreshCatalogs(long now)
    {
        // The DLC answer is a mapping of another process's status file, so it follows that file rather than a
        // timer: the first status arrives seconds after this process starts, and a 30 s gate kept showing
        // "0/5 · 不一致 6" - a failure report - for half a minute after the game was already healthy. Measured
        // on the deployed container: page said 0/5 while /api/status said 5/5 for the same instant.
        var statusMoved = CurrentStatus().StampUtc != catalogStamp;
        if (statusMoved || catalogAtMs == 0 || now - catalogAtMs >= CatalogRefreshMilliseconds)
        {
            catalogAtMs = now;
            catalogStamp = CurrentStatus().StampUtc;
            try { dlc = BuildDlc(); }
            catch (Exception error) { Log.Warning("[TurboTick portal] DLC 目录读取失败：" + error.GetType().Name + ": " + error.Message); }
        }
        // The mods scan is pure disk work and genuinely expensive (every About.xml under every root), so unlike the
        // DLC mapping it keeps the timer: nothing about it changes when the game writes its status.
        if (mods is null || modsAtMs == 0 || now - modsAtMs >= CatalogRefreshMilliseconds)
        {
            modsAtMs = now;
            try { mods = BuildMods(); }
            catch (Exception error) { Log.Warning("[TurboTick portal] mod 列表读取失败：" + error.GetType().Name + ": " + error.Message); }
        }
    }

    private static readonly (string PackageId, string Folder, string AppId)[] KnownExpansions =
    [
        ("ludeon.rimworld", "Core", "294100"),
        ("ludeon.rimworld.royalty", "Royalty", "1149640"),
        ("ludeon.rimworld.ideology", "Ideology", "1392840"),
        ("ludeon.rimworld.biotech", "Biotech", "1826140"),
        ("ludeon.rimworld.anomaly", "Anomaly", "2380740"),
        ("ludeon.rimworld.odyssey", "Odyssey", "3022790")
    ];

    private static AdminDlc BuildDlc()
    {
        // The def list is the only trustworthy answer to "did this expansion actually load", and after the split
        // only the game process can read it: it publishes <c>dlc</c> in headless-status.json and this maps it back.
        // Disk content is still checked here rather than there, because "is Data/Biotech on this filesystem" is a
        // fact the page must be able to state while the game is stopped.
        var published = CurrentStatus();
        // "The game has not published anything yet" is not the same fact as "the def did not load". Reporting the
        // second while only the first is true turns a starting process into six red rows.
        var live = published.Present;
        var byPackage = new Dictionary<string, DlcState>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in published.Dlc)
            if (!string.IsNullOrEmpty(def.PackageId)) byPackage[def.PackageId] = def;
        var entries = new List<AdminExpansion>(KnownExpansions.Length);
        var mismatches = new List<string>();
        foreach (var (packageId, folder, appIdFallback) in KnownExpansions)
        {
            byPackage.TryGetValue(packageId, out var state);
            var loaded = state is { Loaded: true };
            var status = state is null ? null : state.Status;
            var label = state is null ? null : state.Label;
            var defName = state is null ? null : state.DefName;
            var diskPath = string.IsNullOrEmpty(config.GameDirectory) ? null : Path.Combine(config.GameDirectory, "Data", folder);
            var onDisk = diskPath is not null && Directory.Exists(diskPath);
            var steamAppId = ReadDlcAppId(diskPath, folder) ?? appIdFallback;
            var note = "";
            if (!live) note = "游戏进程还没有发布状态文件（未运行或正在启动）：只能确认磁盘上有没有内容";
            else if (onDisk && !loaded) note = "磁盘有内容，但 def 没有加载（不在 activeMods，或加载失败）";
            else if (!onDisk && loaded) note = "def 已加载，但 Data/" + folder + " 目录不存在（内容来自别处）";
            else if (onDisk && loaded) note = "磁盘内容与已加载 def 一致";
            if (live && !loaded && onDisk) mismatches.Add(folder + "：有内容未加载");
            if (live && !loaded && !onDisk) mismatches.Add(folder + "：无内容");
            entries.Add(new AdminExpansion(
                Name: defName ?? folder,
                Label: label ?? folder,
                PackageId: packageId,
                Folder: folder,
                Loaded: loaded,
                Status: status ?? (onDisk ? "Unknown" : "NotInstalled"),
                OnDisk: onDisk,
                DiskPath: diskPath,
                SteamAppId: steamAppId,
                Note: note));
        }
        var loadedCount = entries.Count(entry => entry.Loaded);
        // Core is an ExpansionDef too but nobody counts it as a DLC: "6/5" is the kind of number that makes a
        // reader distrust the whole panel.
        var dlcEntries = entries.Count - 1;
        // Keyed on the folder, not on the def's label: Name comes from the published defName, and a def that did not
        // load has no name to match "Core" against - which is how a stopped game produced "DLC 0/5".
        var dlcLoaded = entries.Count(entry => entry.Loaded && !string.Equals(entry.Folder, "Core", StringComparison.OrdinalIgnoreCase));
        var onDiskDlc = entries.Count(entry => entry.OnDisk && !string.Equals(entry.Folder, "Core", StringComparison.OrdinalIgnoreCase));
        var summary = live
            ? "DLC " + dlcLoaded + "/" + dlcEntries + " 已加载"
                + (mismatches.Count > 0 ? " · 不一致 " + mismatches.Count : "")
            : "DLC " + onDiskDlc + "/" + dlcEntries + " 在磁盘上 · 游戏进程未发布状态，是否加载未知";
        return new AdminDlc(entries, loadedCount, config.CohesionDlc, summary, mismatches);
    }

    private static string? ReadDlcAppId(string? diskPath, string folder)
    {
        if (diskPath is null) return null;
        var about = Path.Combine(diskPath, "About", "About.xml");
        try
        {
            if (!File.Exists(about)) return null;
            var stamp = File.GetLastWriteTimeUtc(about);
            if (dlcAppIds.TryGetValue(folder, out var cached) && cached.Stamp == stamp) return cached.AppId;
            var document = System.Xml.Linq.XDocument.Load(about);
            var id = document.Root?.Element("steamAppId")?.Value?.Trim();
            dlcAppIds[folder] = (stamp, string.IsNullOrEmpty(id) ? null : id);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AdminMods BuildMods()
    {
        // Scanned from disk, not from ModLister: this process has no game state, and "what would the game find"
        // is the question that matters when a save is about to be started against the answer. The enabled set
        // comes from Config/ModsConfig.xml, which is the same file the game reads at startup.
        var snapshot = ModsConfigFile.Read(config.ModsConfigPath);
        var roots = new List<string>(4);
        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            var full = Path.GetFullPath(root);
            foreach (var existing in roots)
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(full);
        }
        AddRoot(config.ModsDirectory);
        AddRoot(Path.Combine(config.GameDirectory, "Mods"));
        var installed = new List<InstalledMod>(64);
        foreach (var root in roots)
            foreach (var mod in ModDirectory.Scan(root))
                installed.Add(mod);

        var active = new List<string>(snapshot.ActiveMods);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < active.Count; index++) order.TryAdd(active[index], index);

        var entries = new List<AdminMod>(64);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed)
        {
            var packageId = mod.PackageId ?? "";
            if (packageId.Length == 0) continue;
            if (!seen.Add(packageId)) { duplicates.Add(packageId); continue; }
            // Load order metadata lives in About/About.xml and is only parsed when it exists; a mod without it is
            // still a mod, so a parse failure must not drop the row.
            var about = ModDirectory.Describe(mod.Directory);
            entries.Add(new AdminMod(
                PackageId: packageId,
                Name: mod.Name.Length > 0 ? mod.Name : packageId,
                Version: ReadAboutVersion(mod.Directory),
                Active: order.ContainsKey(packageId),
                Order: order.TryGetValue(packageId, out var position) ? position : -1,
                Source: SourceOf(mod.Directory),
                Directory: mod.Directory,
                Compatible: true,
                LoadAfter: ReadAboutList(mod.Directory, "loadAfter"),
                LoadBefore: ReadAboutList(mod.Directory, "loadBefore"),
                ForceLoadAfter: ReadAboutList(mod.Directory, "forceLoadAfter"),
                ForceLoadBefore: ReadAboutList(mod.Directory, "forceLoadBefore")));
            _ = about;
        }
        // A package the config enables but no directory provides is the failure mode worth showing: the game would
        // silently start without it.
        foreach (var wanted in active)
            if (!seen.Contains(wanted)) duplicates.Add(wanted);
        entries.Sort((left, right) =>
        {
            if (left.Active != right.Active) return left.Active ? -1 : 1;
            if (left.Active && right.Active) return left.Order.CompareTo(right.Order);
            return string.Compare(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase);
        });
        return new AdminMods(
            Active: active,
            Mods: entries,
            Duplicates: duplicates.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            ConfigPath: config.ModsConfigPath,
            ConfigVersion: snapshot.Version,
            KnownExpansions: snapshot.KnownExpansions,
            InstalledCount: entries.Count);
    }

    /// <summary>Reads one string element out of a mod's About.xml; a missing or malformed file yields an empty list.</summary>
    private static IReadOnlyList<string> ReadAboutList(string directory, string element)
    {
        var about = Path.Combine(directory, "About", "About.xml");
        try
        {
            if (!File.Exists(about)) return [];
            var document = System.Xml.Linq.XDocument.Load(about);
            var container = document.Root?.Element(element);
            if (container is null) return [];
            var values = new List<string>(4);
            foreach (var item in container.Elements("li"))
            {
                var text = (item.Value ?? "").Trim();
                if (text.Length > 0) values.Add(text);
            }
            return values;
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ReadAboutVersion(string directory)
    {
        var about = Path.Combine(directory, "About", "About.xml");
        try
        {
            if (!File.Exists(about)) return "";
            var document = System.Xml.Linq.XDocument.Load(about);
            return (document.Root?.Element("modVersion")?.Value ?? "").Trim();
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// Where a mod came from, in the same vocabulary the page already prints. Decided by path because the
    /// distinction that matters operationally is "did steamcmd put this here" (replaceable) versus "did a person
    /// drop this here" (only a person can update it).
    /// </summary>
    private static string SourceOf(string directory)
    {
        var text = directory.Replace('\\', '/');
        if (text.Contains("/steamapps/workshop/content/", StringComparison.OrdinalIgnoreCase)) return "SteamWorkshop";
        if (text.Contains("/Data/", StringComparison.OrdinalIgnoreCase)) return "Official";
        return "Folder";
    }

    private static void RefreshPreflight()
    {
        try
        {
            var path = Path.Combine(config.DataDirectory, "preflight.json");
            if (string.IsNullOrEmpty(config.DataDirectory) || !File.Exists(path)) { preflight = null; return; }
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { preflight = null; return; }
            bool? ok = root.TryGetProperty("ok", out var okElement)
                && okElement.ValueKind is JsonValueKind.True or JsonValueKind.False ? okElement.GetBoolean() : null;
            var missing = new List<string>();
            if (root.TryGetProperty("dlc", out var dlc) && dlc.ValueKind == JsonValueKind.Object
                && dlc.TryGetProperty("missing", out var missingElement) && missingElement.ValueKind == JsonValueKind.Array)
                foreach (var item in missingElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text) missing.Add(text);
            var warnings = new List<string>();
            if (root.TryGetProperty("warnings", out var warningsElement) && warningsElement.ValueKind == JsonValueKind.Array)
                foreach (var item in warningsElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text) warnings.Add(text);
            string? versionFrom = null, versionTo = null;
            if (root.TryGetProperty("versionChange", out var change) && change.ValueKind == JsonValueKind.Object)
            {
                if (change.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String) versionFrom = from.GetString();
                if (change.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String) versionTo = to.GetString();
            }
            preflight = new AdminPreflight(ok, Text(root, "reason"), Text(root, "detail"), Text(root, "version"),
                Text(root, "repatch"), versionFrom, versionTo, missing, warnings, Text(root, "generatedUtc"));
        }
        catch (Exception error)
        {
            // A half-written report must not take the page down; the next tick reads it again.
            preflight = null;
            Log.Warning("[TurboTick admin] 读取 preflight.json 失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The periodic checkpoint. Deliberately not a second persistence mechanism: it sends the same
    /// <c>save</c> command an operator's button sends, so a scheduled checkpoint and a manual one cannot diverge.
    /// </summary>
    private static void RunScheduledCheckpoint(long now)
    {
        if (config.CheckpointMinutes <= 0) return;
        if (nextCheckpointAtMs == 0) { ScheduleNextCheckpoint(now); return; }
        if (now < nextCheckpointAtMs) return;
        ScheduleNextCheckpoint(now);
        // Off the tick thread on purpose: a real colony takes seconds to write and this page must keep answering
        // while it does. The counter is only advanced after the game says it actually saved.
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await CommandInbox.SendAsync("save", null, TimeSpan.FromSeconds(180), CancellationToken.None)
                    .ConfigureAwait(false);
                if (outcome.Ok)
                {
                    Interlocked.Increment(ref checkpointsTaken);
                    Log.Message("[TurboTick admin] 定时检查点（第 " + Interlocked.Read(ref checkpointsTaken) + " 次）：" + outcome.Detail);
                }
                else if (outcome.Code is not ("no-world" or "no-server" or "busy" or "command-timeout"))
                    Log.Warning("[TurboTick admin] 定时检查点失败：" + outcome.Code + " · " + outcome.Detail);
            }
            catch (Exception error)
            {
                Log.Warning("[TurboTick admin] 定时检查点异常：" + error.GetType().Name + ": " + error.Message);
            }
        });
    }

    private static void ScheduleNextCheckpoint(long now)
    {
        var minutes = Math.Max(1, config.CheckpointMinutes);
        nextCheckpointAtMs = now + minutes * 60_000L;
        nextCheckpointUtc = DateTime.UtcNow.AddMinutes(minutes).ToString("O");
    }

    private static void SampleFrameRate(long now)
    {
        if (fpsStartedMs == 0) { fpsStartedMs = now; fpsFrames = frameCount; return; }
        var elapsed = now - fpsStartedMs;
        if (elapsed < 1000) return;
        fps = (frameCount - fpsFrames) * 1000.0 / elapsed;
        fpsFrames = frameCount;
        fpsStartedMs = now;
    }

    // ---------------------------------------------------------------- commands (across a process boundary)

    internal static IReadOnlyList<string> CommandNames =>
        // `tick-report` is the one that answers "is anything flowing": every channel counter freezes when the room has no
        // replica, because `PublishFrame` only runs for an audience. Without it the only way to tell a still channel from a
        // paused server was to read the game log by hand.
        ["save", "quit", "reload-settings", "answer-dialog", "script-settlement", "settlement-report", "settlement-probe", "settlement-gate", "settlement-chokepoint", "settlement-workgate", "settlement-door", "settlement-pod", "settlement-claim", "settlement-forget", "pawn-to-map", "settlement-absent-map", "incident-probe", "cooldown-probe", "history-probe", "build-style-probe", "hostility-probe", "medcare-probe", "toggle-probe", "mech-probe", "execute-probe", "fishing-probe", "add-mechanitor", "remove-mechanitor", "follow-probe", "attack-probe", "goto-probe", "autoslaughter-probe", "area-paint-probe", "designator-coverage", "designator-map", "map-probe", "map-bind-test", "second-map", "second-map-destroy", "faction-ideo-probe", "flight-probe", "floatmenu-hook", "plan-probe", "training-probe", "trait-probe", "gene-probe", "add-gene", "remove-gene", "make-guilty", "make-prisoner", "precept-name-probe", "precept-payload-probe", "ideo-style-probe", "anomaly-difficulty-probe", "name-deadline-probe", "ideo-lock-probe", "auto-rebuild-probe", "policy-table", "faction-create", "message-archive", "corpse-tracker", "visual-effect-probe", "play-settings-probe", "settlement-forbid-probe", "settlement-cellgate", "trade-payment", "settlement-container", "settlement-jobgate", "unmake-prisoner", "prisoner-mode", "guest-probe", "precept-probe", "surgery-probe",
            "settlement-war", "settlement-scenario", "sync-census", "bossgroup-probe", "gizmo-hooks", "sweep-once", "nudge-probe", "script-condition", "script-immunity", "script-fire", "script-job", "advance-ticks", "script-mental", "script-deathrest", "script-projectile", "script-bill", "gap-audit", "pawn-payload-size", "script-lord-lost", "script-game-ender", "script-passing-ship", "passing-ship-probe", "worldobject-lifecycle", "pawnhome-probe", "settlement-retire", "script-mental-fit", "script-residual", "settlement-grant", "royalty-grant", "settlement-counters", "royalty-channel", "health-state", "thoughts", "transport-fixture", "transport-list", "transport-details", "transport-launch", "xenogerm-probe", "xenogerm-target", "pawn-leave-probe", "patch-inventory", "royalty-count", "script-ideo", "ideo-count", "bill-probe", "storyteller-probe", "discovery-probe", "tracker-probe", "letter-probe", "comp-probe", "launch-probe", "settings-probe", "floatmenu-probe", "kept-cell", "alert-probe", "script-zone", "script-crop", "script-worldcomp", "script-condition-derived", "script-mental-subclass", "script-hediff-derived", "script-ethereal", "script-ability", "script-adaptation", "script-stance", "script-skyfaller", "script-lord", "script-quest", "script-tracker", "script-hediff-timers", "tick-report", "wire-round-trip"];

    /// <summary>
    /// Runs one command and reports what actually happened.
    ///
    /// The in-process queue this replaced could not survive the game process exiting, and surviving that is the
    /// whole point of this executable. Everything therefore goes through either the file inbox (the game executes
    /// it on its own thread) or the supervisor (the game process is started, stopped or replaced). Neither path
    /// may report success for work that was merely accepted, so every branch waits for an answer or says why it
    /// did not get one.
    /// </summary>
    private static async Task<CommandOutcome> ExecuteAsync(string name, string? argument, CancellationToken token)
    {
        Interlocked.Increment(ref acceptedCommands);
        lastCommand = name;
        lastCommandUtc = DateTime.UtcNow.ToString("O");
        CommandOutcome outcome;
        try
        {
            outcome = name switch
            {
                "save" => await CommandInbox.SendAsync("save", argument, TimeSpan.FromSeconds(120), token).ConfigureAwait(false),
                "reload-settings" => await CommandInbox.SendAsync("reload-settings", argument, TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "quit" or "server-shutdown" => await ShutdownServiceAsync(token).ConfigureAwait(false),
                "server-stop" => await StopWorldAsync(token).ConfigureAwait(false),
                "server-start" => await StartWorldAsync(argument, token).ConfigureAwait(false),
                // The game is holding a dialog open and waiting; this is the only way anyone can answer it. It goes
                // through the file inbox like every other game-thread action - the callback belongs to the mod that
                // opened the window, so it must run on the game's own thread.
                "answer-dialog" => await CommandInbox.SendAsync("answer-dialog", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                // The ownership layer has no UI yet (stage D of `docs/multi-settlement-plan.md`), and until a settlement
                // exists every gate short-circuits on `SettlementData.Active` - the feature installs and does nothing.
                // These two commands are how it is exercised and explained without one.
                "script-settlement" => await CommandInbox.SendAsync("script-settlement", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-report" => await CommandInbox.SendAsync("settlement-report", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-gate" => await CommandInbox.SendAsync("settlement-gate", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-chokepoint" => await CommandInbox.SendAsync("settlement-chokepoint", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-workgate" => await CommandInbox.SendAsync("settlement-workgate", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-probe" => await CommandInbox.SendAsync("settlement-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-door" => await CommandInbox.SendAsync("settlement-door", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-pod" => await CommandInbox.SendAsync("settlement-pod", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "follow-probe" => await CommandInbox.SendAsync("follow-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "attack-probe" => await CommandInbox.SendAsync("attack-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "goto-probe" => await CommandInbox.SendAsync("goto-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "autoslaughter-probe" => await CommandInbox.SendAsync("autoslaughter-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
"medcare-probe" => await CommandInbox.SendAsync("medcare-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "toggle-probe" => await CommandInbox.SendAsync("toggle-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "mech-probe" => await CommandInbox.SendAsync("mech-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "execute-probe" => await CommandInbox.SendAsync("execute-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "fishing-probe" => await CommandInbox.SendAsync("fishing-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "add-mechanitor" => await CommandInbox.SendAsync("add-mechanitor", argument,
                    TimeSpan.FromSeconds(40), token).ConfigureAwait(false),
                "remove-mechanitor" => await CommandInbox.SendAsync("remove-mechanitor", argument,
                    TimeSpan.FromSeconds(40), token).ConfigureAwait(false),
"hostility-probe" => await CommandInbox.SendAsync("hostility-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
"build-style-probe" => await CommandInbox.SendAsync("build-style-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
"history-probe" => await CommandInbox.SendAsync("history-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
"cooldown-probe" => await CommandInbox.SendAsync("cooldown-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
"incident-probe" => await CommandInbox.SendAsync("incident-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
"settlement-absent-map" => await CommandInbox.SendAsync("settlement-absent-map", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "pawn-to-map" => await CommandInbox.SendAsync("pawn-to-map", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-claim" => await CommandInbox.SendAsync("settlement-claim", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-forget" => await CommandInbox.SendAsync("settlement-forget", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "area-paint-probe" => await CommandInbox.SendAsync("area-paint-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "designator-coverage" => await CommandInbox.SendAsync("designator-coverage", argument,
                    TimeSpan.FromSeconds(60), token).ConfigureAwait(false),
                "floatmenu-hook" => await CommandInbox.SendAsync("floatmenu-hook", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "plan-probe" => await CommandInbox.SendAsync("plan-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "designator-map" => await CommandInbox.SendAsync("designator-map", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "map-bind-test" => await CommandInbox.SendAsync("map-bind-test", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "map-probe" => await CommandInbox.SendAsync("map-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "second-map" => await CommandInbox.SendAsync("second-map", argument,
                    TimeSpan.FromMinutes(2), token).ConfigureAwait(false),
                "second-map-destroy" => await CommandInbox.SendAsync("second-map-destroy", argument,
                    TimeSpan.FromSeconds(60), token).ConfigureAwait(false),
                "faction-ideo-probe" => await CommandInbox.SendAsync("faction-ideo-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "flight-probe" => await CommandInbox.SendAsync("flight-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "training-probe" => await CommandInbox.SendAsync("training-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "trait-probe" => await CommandInbox.SendAsync("trait-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "gene-probe" => await CommandInbox.SendAsync("gene-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "add-gene" => await CommandInbox.SendAsync("add-gene", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "remove-gene" => await CommandInbox.SendAsync("remove-gene", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "make-guilty" => await CommandInbox.SendAsync("make-guilty", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "make-prisoner" => await CommandInbox.SendAsync("make-prisoner", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "precept-name-probe" => await CommandInbox.SendAsync("precept-name-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "precept-payload-probe" => await CommandInbox.SendAsync("precept-payload-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "ideo-style-probe" => await CommandInbox.SendAsync("ideo-style-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "anomaly-difficulty-probe" => await CommandInbox.SendAsync("anomaly-difficulty-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "name-deadline-probe" => await CommandInbox.SendAsync("name-deadline-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "ideo-lock-probe" => await CommandInbox.SendAsync("ideo-lock-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                                                                                "play-settings-probe" => await CommandInbox.SendAsync("play-settings-probe", argument,
                                                                                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                                                                                "visual-effect-probe" => await CommandInbox.SendAsync("visual-effect-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
"corpse-tracker" => await CommandInbox.SendAsync("corpse-tracker", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
"message-archive" => await CommandInbox.SendAsync("message-archive", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
"faction-create" => await CommandInbox.SendAsync("faction-create", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
"policy-table" => await CommandInbox.SendAsync("policy-table", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
"auto-rebuild-probe" => await CommandInbox.SendAsync("auto-rebuild-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "settlement-forbid-probe" => await CommandInbox.SendAsync("settlement-forbid-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "settlement-cellgate" => await CommandInbox.SendAsync("settlement-cellgate", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "trade-payment" => await CommandInbox.SendAsync("trade-payment", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "settlement-container" => await CommandInbox.SendAsync("settlement-container", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "settlement-jobgate" => await CommandInbox.SendAsync("settlement-jobgate", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "unmake-prisoner" => await CommandInbox.SendAsync("unmake-prisoner", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "prisoner-mode" => await CommandInbox.SendAsync("prisoner-mode", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "guest-probe" => await CommandInbox.SendAsync("guest-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "precept-probe" => await CommandInbox.SendAsync("precept-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "surgery-probe" => await CommandInbox.SendAsync("surgery-probe", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "settlement-war" => await CommandInbox.SendAsync("settlement-war", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-scenario" => await CommandInbox.SendAsync("settlement-scenario", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "sync-census" => await CommandInbox.SendAsync("sync-census", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "bossgroup-probe" => await CommandInbox.SendAsync("bossgroup-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "gizmo-hooks" => await CommandInbox.SendAsync("gizmo-hooks", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                // The room's own clock and replica count. Every channel counter freezes when the room has no replica
                // (`PublishFrame` only runs for an audience), so this is what tells a still channel from a paused server.
                "script-immunity" => await CommandInbox.SendAsync("script-immunity", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-fire" => await CommandInbox.SendAsync("script-fire", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-job" => await CommandInbox.SendAsync("script-job", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "advance-ticks" => await CommandInbox.SendAsync("advance-ticks", argument,
                    TimeSpan.FromSeconds(60), token).ConfigureAwait(false),
                "script-hediff-timers" => await CommandInbox.SendAsync("script-hediff-timers", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-mental" => await CommandInbox.SendAsync("script-mental", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-deathrest" => await CommandInbox.SendAsync("script-deathrest", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-projectile" => await CommandInbox.SendAsync("script-projectile", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-adaptation" => await CommandInbox.SendAsync("script-adaptation", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-ability" => await CommandInbox.SendAsync("script-ability", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-ethereal" => await CommandInbox.SendAsync("script-ethereal", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-hediff-derived" => await CommandInbox.SendAsync("script-hediff-derived", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-mental-subclass" => await CommandInbox.SendAsync("script-mental-subclass", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-condition-derived" => await CommandInbox.SendAsync("script-condition-derived", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-worldcomp" => await CommandInbox.SendAsync("script-worldcomp", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                // `script-zone` had a branch in `PortalCommands` and no other wiring, so the zone probe could not be
                // invoked at all - the switch answered "unsupported-command". Adding a command is three edits, not one.
                "alert-probe" => await CommandInbox.SendAsync("alert-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "kept-cell" => await CommandInbox.SendAsync("kept-cell", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "floatmenu-probe" => await CommandInbox.SendAsync("floatmenu-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settings-probe" => await CommandInbox.SendAsync("settings-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "launch-probe" => await CommandInbox.SendAsync("launch-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "comp-probe" => await CommandInbox.SendAsync("comp-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "letter-probe" => await CommandInbox.SendAsync("letter-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "tracker-probe" => await CommandInbox.SendAsync("tracker-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "discovery-probe" => await CommandInbox.SendAsync("discovery-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "storyteller-probe" => await CommandInbox.SendAsync("storyteller-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "bill-probe" => await CommandInbox.SendAsync("bill-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "ideo-count" => await CommandInbox.SendAsync("ideo-count", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-ideo" => await CommandInbox.SendAsync("script-ideo", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "patch-inventory" => await CommandInbox.SendAsync("patch-inventory", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "royalty-count" => await CommandInbox.SendAsync("royalty-count", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "royalty-grant" => await CommandInbox.SendAsync("royalty-grant", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                // **The switch subject is `name`, not `commandName`** - copying an arm from elsewhere would compile
                // against a variable that does not exist in this scope only if it happened to be declared; it is not,
                // so the reminder stays here.
                "transport-fixture" => await CommandInbox.SendAsync("transport-fixture", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "transport-list" => await CommandInbox.SendAsync("transport-list", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "transport-details" => await CommandInbox.SendAsync("transport-details", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "transport-launch" => await CommandInbox.SendAsync("transport-launch", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "xenogerm-probe" => await CommandInbox.SendAsync("xenogerm-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "xenogerm-target" => await CommandInbox.SendAsync("xenogerm-target", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "thoughts" => await CommandInbox.SendAsync("thoughts", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "health-state" => await CommandInbox.SendAsync("health-state", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "royalty-channel" => await CommandInbox.SendAsync("royalty-channel", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "pawn-leave-probe" => await CommandInbox.SendAsync("pawn-leave-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-counters" => await CommandInbox.SendAsync("settlement-counters", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-grant" => await CommandInbox.SendAsync("settlement-grant", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-residual" => await CommandInbox.SendAsync("script-residual", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-mental-fit" => await CommandInbox.SendAsync("script-mental-fit", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "settlement-retire" => await CommandInbox.SendAsync("settlement-retire", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "pawnhome-probe" => await CommandInbox.SendAsync("pawnhome-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "worldobject-lifecycle" => await CommandInbox.SendAsync("worldobject-lifecycle", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "passing-ship-probe" => await CommandInbox.SendAsync("passing-ship-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-passing-ship" => await CommandInbox.SendAsync("script-passing-ship", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-game-ender" => await CommandInbox.SendAsync("script-game-ender", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-lord-lost" => await CommandInbox.SendAsync("script-lord-lost", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "pawn-payload-size" => await CommandInbox.SendAsync("pawn-payload-size", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "gap-audit" => await CommandInbox.SendAsync("gap-audit", argument,
                    TimeSpan.FromSeconds(30), token).ConfigureAwait(false),
                "script-zone" => await CommandInbox.SendAsync("script-zone", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-crop" => await CommandInbox.SendAsync("script-crop", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-bill" => await CommandInbox.SendAsync("script-bill", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-stance" => await CommandInbox.SendAsync("script-stance", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-skyfaller" => await CommandInbox.SendAsync("script-skyfaller", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-lord" => await CommandInbox.SendAsync("script-lord", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-quest" => await CommandInbox.SendAsync("script-quest", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-tracker" => await CommandInbox.SendAsync("script-tracker", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "script-condition" => await CommandInbox.SendAsync("script-condition", argument,

                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),

                "nudge-probe" => await CommandInbox.SendAsync("nudge-probe", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "wire-round-trip" => await CommandInbox.SendAsync("wire-round-trip", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "tick-report" => await CommandInbox.SendAsync("tick-report", argument,
                    TimeSpan.FromSeconds(20), token).ConfigureAwait(false),
                "sweep-once" => await CommandInbox.SendAsync("sweep-once", argument,
                    TimeSpan.FromSeconds(60), token).ConfigureAwait(false),
                _ => CommandOutcome.Bad("unsupported-command", "支持的命令：" + string.Join(", ", CommandNames))
            };
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick portal] 命令 " + name + " 执行失败：" + error.GetType().Name + ": " + error.Message);
            outcome = CommandOutcome.Bad("exception", error.GetType().Name + ": " + error.Message);
        }
        lastResult = outcome.Ok ? outcome.Code : outcome.Code + " · " + outcome.Detail;
        return outcome;
    }

    // ---------------------------------------------------------------- status snapshot (file driven)

    /// <summary>
    /// The game's own last word about itself, cached by file stamp.
    ///
    /// Reading it per request would parse and re-parse the same document dozens of times per page refresh, and a
    /// save that is being written makes a torn read likely - hence the stamp check plus the tolerant parse in
    /// <see cref="GameStatus.Read"/>. A stale document is normal (the game writes every two seconds, and it is
    /// simply not running right now) so staleness is reported rather than hidden.
    /// </summary>
    private static GameStatus CurrentStatus()
    {
        var path = Path.Combine(config.DataDirectory, "headless-status.json");
        var stamp = 0L;
        try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (stamp != 0 && stamp == statusStamp && lastStatus is not null) return lastStatus;
        var read = GameStatus.Read(path);
        statusStamp = stamp;
        lastStatus = read;
        return read;
    }

    private static AdminStatus EmptyStatus() => new(
        Ok: true,
        GeneratedUtc: DateTime.UtcNow.ToString("O"),
        UptimeSeconds: 0,
        State: "starting",
        Failure: null,
        Save: null,
        Tick: null,
        GameTick: null,
        Replicas: null,
        Players: null,
        JoinedPeers: null,
        Fps: null,
        Tps: 0,
        TargetTps: 0,
        Wire: new AdminWire(0, 0, 0, 0, 0, 0, 0),
        Compatibility: new AdminCompatibility(null, null, null, null, null, null, "comp?"),
        Health: new AdminHealth("comp?", "-", false, false, false, null, true, [], false, "comp? · 启动中"),
        Control: ControlBlock(false),
        Portal: PortalBlock(),
        Preflight: null,
        Dlc: null,
        SaveMods: SaveModsBlock(),
        LastSwitch: null,
        Process: ProcessBlock(),
        LoadGate: LoadGate(),
        Log: [],
        Dialogs: []);

    private static AdminControl ControlBlock(bool authenticated) => new(
        WriteEnabled: password is not null,
        Mode: password is not null ? "password" : "read-only",
        Port: Volatile.Read(ref boundPort),
        BindAddress: bindAddress,
        PasswordConfigured: password is not null,
        CommandsAccepted: Volatile.Read(ref acceptedCommands),
        LastCommand: lastCommand,
        LastResult: lastResult,
        LastCommandUtc: lastCommandUtc,
        // Per request, not per process: the snapshot is shared, so "am I logged in" can only be answered for
        // the request that is asking. The page used to sniff document.cookie for the session cookie, which is
        // HttpOnly and therefore invisible to script - every button stayed disabled behind a "需要登录" banner
        // even though the session was valid.
        Authenticated: authenticated);

    private static AdminPortal PortalBlock() => new(
        BindKind: plan.Kind.ToString(),
        LoginRequired: plan.RequiresLogin,
        Sessions: sessions.Count,
        CohesionDlc: config.CohesionDlc,
        CheckpointMinutes: config.CheckpointMinutes,
        NextCheckpointUtc: nextCheckpointUtc,
        CheckpointsTaken: checkpointsTaken,
        SavesDirectory: config.SavesDirectory,
        GameDirectory: config.GameDirectory,
        SteamcmdPath: config.SteamcmdPath);

    private static AdminStatus BuildStatus(long now)
    {
        var game = CurrentStatus();
        var (outBytes, outFrames, inBytes, inFrames) = (game.WireOutBytes, game.WireOutFrames,
            game.WireInBytes, game.WireInFrames);
        if (wireAtMs == 0) { wireAtMs = now; wireOut = outBytes; wireIn = inBytes; }
        var elapsed = now - wireAtMs;
        if (elapsed >= 1000)
        {
            // Counters are lifetime totals published by another process; a decrease therefore means the game was
            // restarted, not that a negative rate is real.
            outRate = (outBytes >= wireOut ? outBytes - wireOut : outBytes) * 1000.0 / elapsed;
            inRate = (inBytes >= wireIn ? inBytes - wireIn : inBytes) * 1000.0 / elapsed;
            wireAtMs = now;
            wireOut = outBytes;
            wireIn = inBytes;
        }

        var installedVersion = game.InstalledVersion ?? ReadManifestText("gameVersion") ?? GameVersions.DefaultKey;
        var installedHash = game.InstalledModHash ?? ReadManifestText("modHash");
        var roomVersion = game.RoomGameVersion;
        var roomHash = game.RoomModHash;
        bool? versionMatches = installedVersion is not null && roomVersion is not null ? installedVersion == roomVersion : null;
        bool? hashMatches = installedHash is not null && roomHash is not null ? installedHash == roomHash : null;
        var comp = versionMatches == false || hashMatches == false ? "comp!="
            : versionMatches == true && hashMatches == true ? "comp==" : "comp?";

        var failedPatches = game.PatchFailed;
        var gameRunning = game.GameRunning;
        var paused = game.Paused;
        var phase = game.Phase ?? "-";
        var process = _supervisor?.Snapshot;
        // A stopped game process is not a degraded server: it is an operator decision, and the page says so with
        // the process block instead of by painting the health line red.
        var degraded = comp != "comp==" || failedPatches.Count > 0 || game.State is "failed" or "faulted";
        var health = new AdminHealth(
            Comp: comp,
            Phase: phase,
            Frozen: game.Frozen,
            Held: game.Held,
            HoldLocal: game.HoldLocal,
            CheckpointTick: game.CheckpointTick,
            CriticalPatchesInstalled: game.PatchCritical,
            FailedPatches: failedPatches,
            Degraded: degraded,
            Summary: comp + " · 补丁失败 " + failedPatches.Count + " · 阶段 " + phase
                + (paused == true ? " · 已暂停" : " · 运行中")
                + (game.Frozen ? " · 冻结" : ""));

        return new AdminStatus(
            Ok: true,
            GeneratedUtc: DateTime.UtcNow.ToString("O"),
            UptimeSeconds: process?.UptimeSeconds ?? game.UptimeSeconds,
            State: game.State,
            Failure: game.Failure,
            Save: game.Save,
            Tick: game.Tick,
            GameTick: game.GameTick,
            Replicas: game.Replicas,
            Players: game.Players,
            JoinedPeers: game.JoinedPeers,
            Fps: fps is null ? null : Math.Round(fps.Value, 1),
            Tps: Math.Round(game.Tps, 1),
            TargetTps: Math.Round(game.TargetTps, 1),
            Wire: new AdminWire(outBytes, outFrames, inBytes, inFrames,
                Math.Round(outRate), Math.Round(inRate), Math.Round(outRate * 8 / 1000.0, 2)),
            Compatibility: new AdminCompatibility(installedVersion, roomVersion, installedHash, roomHash,
                versionMatches, hashMatches, comp),
            Health: health,
            Control: ControlBlock(false),
            Portal: PortalBlock(),
            Preflight: preflight,
            Dlc: dlc,
            SaveMods: SaveModsBlock(),
            LastSwitch: lastSwitch,
            Process: ProcessBlock(),
            LoadGate: LoadGate(),
            Log: RecentLog(),
            Dialogs: game.Dialogs);
    }

    /// <summary>
    /// The game process as the supervisor sees it. This block is the reason the page no longer goes dark during a
    /// world switch: the thing that restarts is described here, by the process that is restarting it.
    /// </summary>
    private static AdminProcess ProcessBlock()
    {
        var snapshot = _supervisor?.Snapshot;
        if (snapshot is null)
            return new AdminProcess("unsupervised", false, false, null, 0, null, null, null,
                "本进程不是由 supervisor 启动的：游戏进程的生命周期不归它管");
        return new AdminProcess(
            State: snapshot.Running ? (snapshot.Restarting ? "restarting" : "running") : "stopped",
            Running: snapshot.Running,
            Restarting: snapshot.Restarting,
            Pid: snapshot.Pid,
            UptimeSeconds: snapshot.UptimeSeconds,
            LastExitCode: snapshot.LastExitCode,
            LastExitUtc: snapshot.LastExitUtc,
            LastExitDetail: snapshot.LastExitDetail,
            Detail: snapshot.Detail);
    }

    /// <summary>Reads one top-level string out of compatibility-manifest.json; this is the game's version identity.</summary>
    private static string? ReadManifestText(string property)
    {
        var path = Path.Combine(config.DataDirectory, "compatibility-manifest.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The last log lines, from this process's own buffer.
    ///
    /// The game's log lives in another process, so it is tailed from the file the supervisor tees it to; both
    /// sources are merged and the newest lines win. Reading the game's log is deliberately best-effort - a page
    /// that throws because a log file is being rotated would be worse than one that shows fewer lines.
    /// </summary>
    private static List<AdminLogLine> RecentLog()
    {
        var lines = new List<AdminLogLine>(MaximumLogLines);
        try
        {
            foreach (var message in GameLogTail.Tail(config.DataDirectory, MaximumLogLines))
                lines.Add(new AdminLogLine(message.Type, message.Text, message.Repeats));
            using var guard = Log.LockMessages();
            var all = new List<PortalLogMessage>();
            foreach (var message in Log.Messages)
                if (message is not null) all.Add(message);
            for (var index = Math.Max(0, all.Count - MaximumLogLines); index < all.Count; index++)
            {
                var message = all[index];
                lines.Add(new AdminLogLine(message.type.ToString(), message.text ?? "", message.repeats));
            }
            if (lines.Count > MaximumLogLines) lines.RemoveRange(0, lines.Count - MaximumLogLines);
        }
        catch (Exception error)
        {
            lines.Add(new AdminLogLine("Warning", "读取日志失败：" + error.GetType().Name + ": " + error.Message, 0));
        }
        return lines;
    }

    // ---------------------------------------------------------------- HTTP (worker threads)

    private static async Task AcceptLoopAsync(TcpListener server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException error)
            {
                Log.Warning("[TurboTick admin] 接受连接失败：" + error.SocketErrorCode);
                try { await Task.Delay(200, token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = HandleAsync(client, token);
        }
    }

    /// <summary>How long one request may take, by route.
    ///
    /// Everything that only reads status or asks the game to save finishes in seconds; 150 s is generous. The routes
    /// below are not in that class, because each of them waits for a process that downloads:
    ///
    /// - <c>/api/server/start</c> and <c>/api/game/start?save=…</c> satisfy a save's whole mod list first, one
    ///   workshop fetch per missing item. Measured on the deployment host: 27 s for a single 1.6 MB item, so a save
    ///   missing six mods is minutes.
    /// - <c>/api/command</c> when the body names a start, for the same reason.
    /// - <c>/api/game/download</c> returns immediately (it only kicks the task off) but is listed for symmetry.
    ///
    /// The old single 150 s constant cut these mid-download: the operator read `TaskCanceledException` for a fetch
    /// that kept running and then succeeded, so the failure looked permanent while the cache was filling up.
    ///
    /// Raising this is not the same as making the operator wait: the page keeps its progress line live, and the work
    /// itself is bounded by the inner budget in `StartWorldAsync` (20 minutes).
    /// </summary>
    private static int ReplyBudgetSeconds(AdminHttpRequest request)
    {
        switch (request.Path)
        {
            case "/api/server/start":
            case "/api/game/download":
                return 1800;
            case "/api/game/start":
                // Starting without a save only spawns the child (no mod resolution); with one it goes through
                // `StartWorldAsync` and may download.
                return string.IsNullOrWhiteSpace(QueryValue(request.Query, "save"))
                    && ReadStringElement(request.Body, "save") is null ? 150 : 1800;
            case "/api/command":
                return LooksLikeStartCommand(request.Body) ? 1800 : 150;
            default:
                return 150;
        }
    }

    /// <summary>True for a `/api/command` body that names a start. The body is JSON or a form, never both.</summary>
    private static bool LooksLikeStartCommand(byte[] body)
    {
        var name = ReadCommandName(body);
        if (name is not null) return name is "server-start" or "game-start";
        // `FormValue` is how the page posts most of these; a body that names a save is a start as well.
        return FormValue(body, "save") is not null || FormValue(body, "command") is "server-start" or "game-start";
    }

    private static async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            client.NoDelay = true;
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                // This buffer only has to hold the request head. It used to be sized head + MaximumBodyBytes and the
                // read loop stopped once it filled, which meant an upload larger than that never got its head parsed:
                // measured, POSTing a 15 MB save answered {"code":"not-a-save","message":"文件是空的"} because the
                // loop exited before TryParseHead ever saw a complete head, and the body path then saw zero bytes.
                // The body is never buffered here anyway - ReceiveUploadAsync streams it, and other routes read into
                // their own array sized by Content-Length.
                var buffer = new byte[AdminHttp.MaximumHeaderBytes];
                var read = 0;
                AdminHttpRequest? request = null;
                var headLength = 0;
                while (read < buffer.Length)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                    // The head is parsed without a body limit so the upload route gets to decide its own; the
                    // limit for every other route is enforced immediately below, before a body byte is read.
                    switch (AdminHttp.TryParseHead(buffer.AsSpan(0, read), int.MaxValue, out var parsed, out var error, out var head))
                    {
                        case AdminHttpParse.Complete:
                            request = parsed;
                            headLength = head;
                            break;
                        case AdminHttpParse.Incomplete:
                            continue;
                        default:
                            await ReplyAsync(stream, 400, JsonError("bad-request", error), timeout.Token).ConfigureAwait(false);
                            return;
                    }
                    break;
                }
                // curl (and RFC 7231 clients generally) send "Expect: 100-continue" for a body over ~1 KB and then
                // WAIT for the server to answer before sending a single body byte. A server that ignores it makes the
                // client time out and reset the connection, which surfaced here as a rejected 15 MB upload while a
                // 126-byte one went through - the small one has no Expect header. Answering "100 Continue" is the
                // whole fix, and it has to happen before the body is read.
                if (request is not null
                    && request.ContentLength > 0
                    && request.Header("Expect") is { Length: > 0 } expect
                    && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await stream.WriteAsync(cont, timeout.Token).ConfigureAwait(false);
                    await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                if (request is null)
                {
                    await ReplyAsync(stream, 400, JsonError("bad-request", "请求不完整"), timeout.Token).ConfigureAwait(false);
                    return;
                }
                // The 10 s deadline above is the read phase only. A save of a real colony writes 17 MB of XML
                // and publishes a checkpoint; reusing the read deadline would cut the response off exactly
                // when the work finally finished.
                //
                // The budget is per route rather than one constant. 150 s is right for a status read or a
                // checkpoint, but "start this save" satisfies the save's mod list first, which downloads workshop
                // items one at a time - measured at 27 s for a single 1.6 MB item on this host. One constant meant
                // the request died mid-download and the operator read `TaskCanceledException` for a fetch that
                // kept running and succeeded, so the failure looked permanent while the cache was filling.
                using var reply = CancellationTokenSource.CreateLinkedTokenSource(token);
                reply.CancelAfter(TimeSpan.FromSeconds(ReplyBudgetSeconds(request)));
                var available = Math.Max(0, read - headLength);
                var upload = request.Path == UploadPath && request.Method == "POST";
                var limit = upload ? MaximumUploadBytes : MaximumBodyBytes;
                if (request.ContentLength > limit)
                {
                    await ReplyAsync(stream, 413, JsonError("body-too-large",
                        "请求体 " + request.ContentLength + " 字节，超过上限 " + limit + " 字节"), reply.Token).ConfigureAwait(false);
                    return;
                }
                if (upload)
                {
                    // The body is streamed to a temp file: buffering 256 MB to validate it would defeat the
                    // point of having a limit at all.
                    await ReceiveUploadAsync(stream, request, buffer, headLength, available, reply.Token).ConfigureAwait(false);
                    return;
                }
                var body = new byte[request.ContentLength];
                var copied = Math.Min(available, request.ContentLength);
                buffer.AsSpan(headLength, copied).CopyTo(body);
                var got = copied;
                while (got < request.ContentLength)
                {
                    var count = await stream.ReadAsync(body.AsMemory(got, request.ContentLength - got), timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    got += count;
                }
                if (got != request.ContentLength)
                {
                    await ReplyAsync(stream, 400, JsonError("short-body",
                        "请求体只收到 " + got + "/" + request.ContentLength + " 字节"), reply.Token).ConfigureAwait(false);
                    return;
                }
                request.AttachBody(body);
                await RouteAsync(stream, request, reply.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* timeout or shutdown: both are normal ends of a request */ }
        catch (Exception error)
        {
            Log.Warning("[TurboTick admin] 请求处理失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static async Task RouteAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        // A page on another origin can make the browser send Host: evil.example while the socket still lands on
        // this listener (DNS rebinding). Requiring the Host to name this bind keeps that request unreachable.
        if (!AdminHttp.HostAccepted(request.Header("Host"), boundPort, bindAddress))
        {
            await ReplyAsync(stream, 403, JsonError("host-not-allowed",
                "只接受 Host 为 " + bindAddress + (plan.External ? " 或所请求地址" : "、127.0.0.1 或 localhost") + " 的请求"), token).ConfigureAwait(false);
            return;
        }
        switch (request.Path)
        {
            case "/login":
                if (request.Method == "GET") { await LoginPageAsync(stream, request, token).ConfigureAwait(false); return; }
                if (request.Method == "POST") { await LoginAsync(stream, request, token).ConfigureAwait(false); return; }
                await MethodNotAllowed(stream, "GET, POST", token).ConfigureAwait(false);
                return;
            case "/logout":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await LogoutAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await ReplyAsync(stream, 200, AdminPageHtml.Raw, "text/html; charset=utf-8", token).ConfigureAwait(false);
                return;
            case "/favicon.ico":
                await ReplyAsync(stream, 204, Array.Empty<byte>(), "image/x-icon", token).ConfigureAwait(false);
                return;
        }

        // Everything below is an API surface. On an external bind nothing is readable without a session: the
        // status snapshot alone names the world, the players and the mod list.
        if (!await AuthorizeAsync(stream, request, write: request.Method == "POST", token).ConfigureAwait(false)) return;

        switch (request.Path)
        {
            case "/api/status":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                // `authenticated` is per request: the snapshot is shared between requests, so this is the one
                // field that has to be applied here rather than published by the game thread. The page needs it
                // because the session cookie is HttpOnly and script therefore cannot see whether it is logged in.
                var status = published ?? EmptyStatus();
                status = status with { Control = status.Control with { Authenticated = IsAuthenticated(request) } };
                await ReplyAsync(stream, 200, JsonSerializer.Serialize(status, Json), token).ConfigureAwait(false);
                return;
            case "/api/saves":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await SavesAsync(stream, token).ConfigureAwait(false);
                return;
            case "/api/saves/delete":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await DeleteSaveAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/mods":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await ModsAsync(stream, token).ConfigureAwait(false);
                return;
            case "/api/mods/apply":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await ModsApplyAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/saves/mods":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await SaveModsInspectAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/saves/mods/apply":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await SaveModsApplyAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/versions":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await VersionsAsync(stream, token).ConfigureAwait(false);
                return;
            case "/api/preflight":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await PreflightAsync(stream, token).ConfigureAwait(false);
                return;
            case "/api/command":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await CommandAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/game/start":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await GameStartAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/game/stop":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await GamePowerAsync(stream, request, "stop", token).ConfigureAwait(false);
                return;
            case "/api/game/restart":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await GamePowerAsync(stream, request, "restart", token).ConfigureAwait(false);
                return;
            case "/api/dialogs/answer":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await CommandRouteAsync(stream, request, "answer-dialog", DialogAnswerArgument(request), 20, token).ConfigureAwait(false);
                return;
            case "/api/server/start":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await ServerStartAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/server/stop":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                // Stops the world, not the service: the page stays up so the operator can start another save.
                await CommandRouteAsync(stream, request, "server-stop", null, 120, token).ConfigureAwait(false);
                return;
            case "/api/server/shutdown":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                // The one that really takes the page away. Separate on purpose, and the page double-confirms it.
                await CommandRouteAsync(stream, request, "server-shutdown", null, 30, token).ConfigureAwait(false);
                return;
            case "/api/steam/login":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await SteamLoginAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/steam/status":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await ReplyAsync(stream, 200, JsonSerializer.Serialize(SteamStatusPayload(), Json), token).ConfigureAwait(false);
                return;
            case "/api/steam/code":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await SteamCodeAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/steam/proxy":
                // GET reads, POST sets. A proxy is deployment state rather than a per-request option: the operator
                // changes it when the host's network changes, not per download.
                if (request.Method is not ("GET" or "POST"))
                {
                    await MethodNotAllowed(stream, "GET, POST", token).ConfigureAwait(false);
                    return;
                }
                if (request.Method == "POST")
                {
                    // An empty string is a real value here ("connect directly"), so it is distinguished from a
                    // missing field: a body without `proxy` is a bad request, not a clear.
                    var value = BodyString(request.Body, "proxy") ?? FormValue(request.Body, "proxy");
                    if (value is null)
                    {
                        await ReplyAsync(stream, 400, JsonError("proxy-required",
                            "请求体应为 {\"proxy\":\"http://host:port\"}；传空串表示直连"), token).ConfigureAwait(false);
                        return;
                    }
                    SteamCmdRunner.SaveProxy(value);
                }
                await ReplyAsync(stream, 200, JsonSerializer.Serialize(new
                {
                    ok = true, proxy = SteamCmdRunner.Proxy, effective = SteamCmdRunner.DescribeProxy(),
                    detail = SteamCmdRunner.Proxy.Length == 0 ? "直连（未使用代理）" : "steamcmd 走 " + SteamCmdRunner.Proxy
                }, Json), token).ConfigureAwait(false);
                return;
            case "/api/steam/probe":
                // One GET to steamcommunity through the same client the workshop search uses. "The proxy is set" and
                // "the search can reach Steam" are different claims; only the second one is worth anything on a host
                // without direct egress, and finding out here beats discovering it when a save import fails.
                if (request.Method != "GET")
                {
                    await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false);
                    return;
                }
                using (var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    probeDeadline.CancelAfter(TimeSpan.FromSeconds(40));
                    var probe = await WorkshopSearch.ProbeAsync(probeDeadline.Token).ConfigureAwait(false);
                    Log.Message("[TurboTick admin] 工坊可达性测试：" + (probe.Ok ? "成功" : "失败")
                        + " route=" + probe.Route + " detail=" + probe.Detail);
                    await ReplyAsync(stream, 200, JsonSerializer.Serialize(new
                    {
                        ok = probe.Ok, status = probe.Status, route = probe.Route, detail = probe.Detail
                    }, Json), token).ConfigureAwait(false);
                }
                return;
            case "/api/game/download":
                if (request.Method != "POST") { await MethodNotAllowed(stream, "POST", token).ConfigureAwait(false); return; }
                await GameDownloadAsync(stream, request, token).ConfigureAwait(false);
                return;
            case "/api/game/progress":
                if (request.Method != "GET") { await MethodNotAllowed(stream, "GET", token).ConfigureAwait(false); return; }
                await ReplyAsync(stream, 200, JsonSerializer.Serialize(new
                {
                    ok = true, progress = SteamCmdRunner.DownloadState, running = SteamCmdRunner.DownloadRunning
                }, Json), token).ConfigureAwait(false);
                return;
            default:
                await ReplyAsync(stream, 404, JsonError("not-found", "没有这个路径：" + request.Path), token).ConfigureAwait(false);
                return;
        }
    }

    // ---------------------------------------------------------------- authentication

    /// <summary>
    /// Decides whether this request may proceed.
    ///
    /// Two credentials exist and both are accepted: the historical <see cref="TokenHeader"/> carrying the password
    /// (what a <c>curl</c> one-liner uses), and a session cookie from <c>/login</c> which additionally needs the
    /// <see cref="CsrfHeader"/> on writes so a cross-site form post cannot ride it.
    /// </summary>
    private static async Task<bool> AuthorizeAsync(NetworkStream stream, AdminHttpRequest request, bool write, CancellationToken token)
    {
        if (HasSecretInUrl(request))
        {
            await ReplyAsync(stream, 400, JsonError("secret-in-url",
                "不要把密码放在 URL 里；请用 " + TokenHeader + " 头或 /login 会话"), token).ConfigureAwait(false);
            return false;
        }
        if (OriginMismatch(request))
        {
            await ReplyAsync(stream, 403, JsonError("cross-origin", "拒绝跨源请求"), token).ConfigureAwait(false);
            return false;
        }
        if (password is null)
        {
            // Read-only mode: the page still renders on loopback, but nothing that writes may pass - and on an
            // external bind not even the status, because there is no authentication to distinguish operators.
            if (write)
            {
                await ReplyAsync(stream, 403, JsonError("admin-disabled",
                    "未设置 TURBOTICK_ADMIN_PASSWORD：写操作被拒绝。页面仍可只读使用。"), token).ConfigureAwait(false);
                return false;
            }
            if (plan.External)
            {
                await ReplyAsync(stream, 403, JsonError("login-required", "绑定非回环地址时必须先设置管理密码并登录"), token).ConfigureAwait(false);
                return false;
            }
            return true;
        }
        var credential = request.Header(TokenHeader);
        if (!string.IsNullOrEmpty(credential))
        {
            if (credential.Length <= 4096 && AdminHttp.SecretEquals(password, credential)) return true;
            if (!TryAcceptAttempt())
            {
                await ReplyAsync(stream, 429, JsonError("rate-limited", "认证尝试过于频繁，请稍后再试"), token).ConfigureAwait(false);
                return false;
            }
            await ReplyAsync(stream, 401, JsonError("token-rejected", "管理密码不正确"), token).ConfigureAwait(false);
            return false;
        }
        var cookie = AdminHttp.CookieValue(request.Header("Cookie"), SessionCookieName);
        if (cookie is not null && sessions.Validate(cookie))
        {
            // A cookie alone would be replayable by any page that can make a form POST; requiring a header a
            // Simple Request cannot set forces a preflight, which this server never answers.
            if (!write || request.Header(CsrfHeader) == "1") return true;
            await ReplyAsync(stream, 401, JsonError("csrf-required",
                "写操作需要 " + CsrfHeader + ": 1（防止跨站表单）"), token).ConfigureAwait(false);
            return false;
        }
        // Legacy cookie carrying the password itself: kept because the shipped page offered it, still gated on
        // the CSRF header exactly as before.
        var legacy = AdminHttp.CookieValue(request.Header("Cookie"), CookieName);
        if (legacy is not null && request.Header(CsrfHeader) == "1")
        {
            var accepted = AdminHttp.SecretEquals(password, legacy);
            if (!accepted)
            {
                try
                {
                    var decoded = Uri.UnescapeDataString(legacy);
                    if (decoded != legacy) accepted = AdminHttp.SecretEquals(password, decoded);
                }
                catch (Exception) { /* a malformed escape simply does not authenticate */ }
            }
            if (accepted) return true;
        }
        if (plan.External || write)
        {
            await ReplyAsync(stream, 401, JsonError(cookie is null ? "token-required" : "session-expired",
                cookie is null
                    ? "需要 " + TokenHeader + " 头，或先 POST /login 拿会话"
                    : "会话已失效，请重新登录 /login"), token).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private static bool HasSecretInUrl(AdminHttpRequest request) =>
        request.QueryHas("token") || request.QueryHas("password") || request.QueryHas("psw");

    /// <summary>
    /// Whether this request carries a session or token that the server accepted.
    ///
    /// Mirrors exactly what <see cref="AuthorizeAsync"/> accepts for a <em>read</em>, because that is the question
    /// the page is really asking: "am I logged in, or am I looking at an anonymous view". It must not require the
    /// CSRF header - a read does not need one, and demanding it here made the page report "not logged in" for a
    /// valid session (measured: /api/status answered 200 with data while this field said false).
    ///
    /// The page always attaches <see cref="CsrfHeader"/> to its writes, so a button enabled by this field is a
    /// button that will actually go through.
    ///
    /// Deliberately does not rate-limit or reply: a status poll is not an authentication attempt.
    /// </summary>
    private static bool IsAuthenticated(AdminHttpRequest request)
    {
        if (password is null) return !plan.External;
        var credential = request.Header(TokenHeader);
        if (!string.IsNullOrEmpty(credential)) return credential.Length <= 4096 && AdminHttp.SecretEquals(password, credential);
        var session = AdminHttp.CookieValue(request.Header("Cookie"), SessionCookieName);
        if (session is not null && sessions.Validate(session)) return true;
        return PasswordCookieAccepted(request) && request.Header(CsrfHeader) == "1";
    }

    /// <summary>The legacy password-in-a-cookie form, still accepted for the page that shipped with it.</summary>
    private static bool PasswordCookieAccepted(AdminHttpRequest request)
    {
        var legacy = AdminHttp.CookieValue(request.Header("Cookie"), CookieName);
        if (legacy is null) return false;
        if (AdminHttp.SecretEquals(password!, legacy)) return true;
        try
        {
            var decoded = Uri.UnescapeDataString(legacy);
            return decoded != legacy && AdminHttp.SecretEquals(password!, decoded);
        }
        catch (Exception) { return false; }
    }

    private static bool OriginMismatch(AdminHttpRequest request)
    {
        var origin = request.Header("Origin");
        if (origin is null) return false;
        var host = request.Header("Host");
        if (string.IsNullOrEmpty(host)) return true;
        // Same-origin means the Origin's authority is exactly the Host the browser used. Comparing against the
        // bind address instead would break every operator who browses with an IP different from the bind.
        var expected = "http://" + host;
        if (origin.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        var https = "https://" + host;
        return !origin.Equals(https, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task LoginPageAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var cookie = AdminHttp.CookieValue(request.Header("Cookie"), SessionCookieName);
        if (cookie is not null && sessions.Validate(cookie))
        {
            await ReplyAsync(stream, 303, "", "text/plain; charset=utf-8", token, ("Location", "/")).ConfigureAwait(false);
            return;
        }
        await ReplyAsync(stream, 200, AdminPageHtml.Login, "text/html; charset=utf-8", token).ConfigureAwait(false);
    }

    private static async Task LoginAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        if (HasSecretInUrl(request))
        {
            await ReplyAsync(stream, 400, JsonError("secret-in-url", "登录也请把密码放在请求体里"), token).ConfigureAwait(false);
            return;
        }
        if (!TryAcceptAttempt())
        {
            await ReplyAsync(stream, 429, JsonError("rate-limited", "认证尝试过于频繁，请稍后再试"), token).ConfigureAwait(false);
            return;
        }
        if (password is null)
        {
            await ReplyAsync(stream, 403, JsonError("admin-disabled",
                "未设置 TURBOTICK_ADMIN_PASSWORD：没有可登录的密码。页面只读。"), token).ConfigureAwait(false);
            return;
        }
        var form = IsForm(request);
        var provided = form ? FormValue(request.Body, "password") : BodyString(request.Body, "password");
        if (string.IsNullOrEmpty(provided) || provided.Length > 4096)
        {
            await ReplyAsync(stream, 400, JsonError("password-required", "请求体需要 password 字段"), token).ConfigureAwait(false);
            return;
        }
        if (!AdminHttp.SecretEquals(password, provided))
        {
            if (form)
            {
                await ReplyAsync(stream, 403, AdminPageHtml.LoginError("密码不正确"), "text/html; charset=utf-8", token).ConfigureAwait(false);
                return;
            }
            await ReplyAsync(stream, 401, JsonError("password-rejected", "管理密码不正确"), token).ConfigureAwait(false);
            return;
        }
        var session = sessions.Issue(out var expiresAt);
        var seconds = Math.Max(60, (expiresAt - Environment.TickCount64) / 1000);
        // Persist immediately: a switch can restart the game process seconds after this login, and the whole point of
        // persisting sessions is that the operator does not have to log in again to see the world come back.
        sessions.Save(SessionStorePath());
        var cookie = SessionCookieName + "=" + session + "; Path=/; HttpOnly; SameSite=Strict; Max-Age="
            + seconds.ToString(CultureInfo.InvariantCulture);
        Log.Message("[TurboTick admin] 管理页登录成功（会话 " + sessions.Count + " 个，有效期 " + seconds + " 秒）");
        if (form)
        {
            await ReplyAsync(stream, 303, "", "text/plain; charset=utf-8", token,
                ("Location", "/"), ("Set-Cookie", cookie)).ConfigureAwait(false);
            return;
        }
        await ReplyAsync(stream, 200, JsonSerializer.Serialize(new
        {
            ok = true, expiresInSeconds = seconds, cookie = SessionCookieName
        }, Compact), token, ("Set-Cookie", cookie)).ConfigureAwait(false);
    }

    private static async Task LogoutAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var cookie = AdminHttp.CookieValue(request.Header("Cookie"), SessionCookieName);
        var revoked = sessions.Revoke(cookie);
        var cleared = SessionCookieName + "=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0";
        await ReplyAsync(stream, 200, JsonSerializer.Serialize(new { ok = true, revoked }, Compact), token,
            ("Set-Cookie", cleared)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- API handlers

    private static async Task SavesAsync(NetworkStream stream, CancellationToken token)
    {
        // Pure file IO on purpose: the HTTP thread must not call GenFilePaths (Verse) to find the directory, so
        // the game thread hands it over once at startup.
        var saves = SaveLibrary.List(config.SavesDirectory);
        currentSaveName = CurrentStatus().Save;
        // The hash is what lets an operator prove the file that arrived is the file they sent; it is cached by
        // (length, mtime) so a 15-second refresh does not re-read 20 MB per save.
        var entries = new List<object>(saves.Count);
        foreach (var save in saves)
            entries.Add(new
            {
                save.Name, save.FileName, save.Bytes, save.ModifiedUtc,
                sha256 = Sha256Of(SaveLibrary.PathFor(config.SavesDirectory, save.Name)),
                current = string.Equals(CurrentStatus().Save, save.Name, StringComparison.OrdinalIgnoreCase)
            });
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            directory = config.SavesDirectory,
            current = CurrentStatus().Save,
            count = saves.Count,
            lastUpload,
            maximumUploadBytes = MaximumUploadBytes,
            saves = entries
        }, Json);
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    private static async Task VersionsAsync(NetworkStream stream, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            defaultKey = GameVersions.DefaultKey,
            appId = GameVersions.SteamAppId,
            installed = CurrentStatus().InstalledVersion,
            versions = GameVersions.All
        }, Json);
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    /// <summary>The launcher's report verbatim, so a failure can be read without finding the container log.</summary>
    private static async Task PreflightAsync(NetworkStream stream, CancellationToken token)
    {
        var path = Path.Combine(config.DataDirectory, "preflight.json");
        string payload;
        try
        {
            payload = !string.IsNullOrEmpty(config.DataDirectory) && File.Exists(path)
                ? File.ReadAllText(path)
                : JsonSerializer.Serialize(new
                {
                    ok = (bool?)null,
                    reason = "no-preflight",
                    detail = "本次进程不是由容器 entrypoint 启动的（没有 preflight.json）：管理台自己不做 DLC 预检"
                }, Json);
        }
        catch (Exception error)
        {
            payload = JsonSerializer.Serialize(new
            {
                ok = (bool?)null, reason = "preflight-unreadable", detail = error.GetType().Name + ": " + error.Message
            }, Json);
        }
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    private static object SteamStatusPayload() => new
    {
        ok = true,
        login = SteamCmdRunner.LoginState,
        // The interactive view: phase, whether a code prompt is waiting, and the cleaned transcript. The old
        // `login` object stays so an older page (or a script) keeps working.
        session = SteamCmdRunner.Session,
        needsCode = SteamCmdRunner.NeedsCode,
        account = SteamCmdRunner.Account,
        steamcmd = new
        {
            path = SteamCmdRunner.Executable,
            present = File.Exists(SteamCmdRunner.Executable),
            root = SteamCmdRunner.SteamRoot
        },
        download = SteamCmdRunner.DownloadState,
        downloadRunning = SteamCmdRunner.DownloadRunning,
        // The proxy belongs in the status payload as well as its own route: "the download failed" and "there is no
        // proxy set" are the same question on a host without egress, and the Steam view already answers the first.
        proxy = SteamCmdRunner.Proxy,
        proxyEffective = SteamCmdRunner.DescribeProxy(),
        // The workshop *search* is our own HttpClient, not steamcmd. Reporting its route separately is what makes
        // "the search failed" diagnosable: the two can be configured differently and the page has to say which one
        // actually ran.
        searchRoute = WorkshopSearch.LastRoute,
        hint = SteamCmdRunner.InteractiveLoginHint
    };

    private static async Task SteamLoginAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        // `account` is what the page actually sends (`AdminPageHtml.cs:1520` posts `{ account, password }`).
        // Reading only `username` made every login from the page fail with `login-refused / 账号不能为空` - the
        // account field arrived, was never looked at, and the empty-string check below fired. Kept `username` as a
        // second name because it is the obvious one for any other client (curl, a script) to guess.
        var user = BodyString(request.Body, "account") ?? FormValue(request.Body, "account")
            ?? BodyString(request.Body, "username") ?? FormValue(request.Body, "username");
        var secret = BodyString(request.Body, "password") ?? FormValue(request.Body, "password");
        if (string.IsNullOrEmpty(secret))
        {
            await ReplyAsync(stream, 400, JsonError("password-required", "请求体需要 account 与 password"), token).ConfigureAwait(false);
            return;
        }
        if (!SteamCmdRunner.TryBeginLogin(user, secret, out var error))
        {
            await ReplyAsync(stream, 409, JsonError("login-refused", error), token).ConfigureAwait(false);
            return;
        }
        // The password never reaches this response, the log, or the status snapshot - only steamcmd's own store
        // under the mounted steam root.
        Log.Message("[TurboTick admin] 已提交 Steam 登录请求（账号 " + (user ?? "").Trim() + "，密码不落日志）");
        SteamCmdRunner.SaveState();
        await ReplyAsync(stream, 202, JsonSerializer.Serialize(new
        {
            ok = true, accepted = true, account = SteamCmdRunner.Account,
            // The page polls this while a Steam Guard prompt is up; saying so here saves a round trip.
            status = SteamStatusUrl, code = CodeUrl
        }, Compact), token).ConfigureAwait(false);
    }

    private const string SteamStatusUrl = "/api/steam/status";
    private const string CodeUrl = "/api/steam/code";

    /// <summary>
    /// Answers the prompt steamcmd is currently blocked on: the Steam Guard code, or an emailed code.
    ///
    /// The login worker owns the child's stdin and writes whatever lands in <see cref="SteamCmdRunner.SubmitCode"/>,
    /// so this handler does not touch the process. It only refuses when nothing is waiting, which keeps a stale page
    /// or a late double-submit from pushing text into a later run.
    /// </summary>
    private static async Task SteamCodeAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var code = BodyString(request.Body, "code") ?? FormValue(request.Body, "code")
            ?? BodyString(request.Body, "value") ?? FormValue(request.Body, "value");
        if (SteamCmdRunner.SubmitCode(code, out var error))
        {
            Log.Message("[TurboTick admin] 已提交 Steam 验证码（验证码不落日志）");
            await ReplyAsync(stream, 200, JsonSerializer.Serialize(new
            {
                ok = true, accepted = true, needsCode = SteamCmdRunner.NeedsCode, session = SteamCmdRunner.Session
            }, Json), token).ConfigureAwait(false);
            return;
        }
        await ReplyAsync(stream, 409, JsonError("code-refused", error), token).ConfigureAwait(false);
    }

    private static async Task GameDownloadAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        // `branch` is what the page sends (`AdminPageHtml.cs:1529` posts `{ branch }`); the endpoint named the
        // field `version` and that mismatch turned every click on 下载/校验 into `unknown-version` for an empty key.
        // Both names are accepted, and so is `version`, because any script written against this API before now
        // used it.
        var key = BodyString(request.Body, "version") ?? FormValue(request.Body, "version")
            ?? BodyString(request.Body, "branch") ?? FormValue(request.Body, "branch");
        var version = GameVersions.Find(key);
        if (version is null)
        {
            await ReplyAsync(stream, 400, JsonError("unknown-version",
                "不支持的版本：" + key + "；可用：" + string.Join(", ", GameVersions.Keys)), token).ConfigureAwait(false);
            return;
        }
        if (!SteamCmdRunner.TryBeginDownload(version, out var error))
        {
            await ReplyAsync(stream, 409, JsonError("download-refused", error), token).ConfigureAwait(false);
            return;
        }
        var command = SteamCmd.GameUpdate(SteamCmdRunner.Executable, SteamCmdRunner.GameDirectory, SteamCmdRunner.Account,
            version.SteamAppId, version.BetaBranch, validate: true);
        Log.Message("[TurboTick admin] 开始下载 " + version.Key + "：" + command.Display);
        await ReplyAsync(stream, 202, JsonSerializer.Serialize(new
        {
            ok = true, accepted = true, version = version.Key, branch = version.BetaBranch,
            command = command.Display, progress = "/api/game/progress"
        }, Compact), token).ConfigureAwait(false);
    }

    private static async Task ServerStartAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var save = BodyString(request.Body, "save") ?? FormValue(request.Body, "save");
        var name = SaveLibrary.Normalize(save);
        if (name is null)
        {
            await ReplyAsync(stream, 400, JsonError("save-required",
                "请求体需要 {\"save\":\"<Saves 目录里的存档名>\"}；可用存档见 /api/saves"), token).ConfigureAwait(false);
            return;
        }
        // Not 30. Starting into a named save first satisfies that save's mod list, and satisfying it means
        // downloading workshop items one at a time. Measured on the deployment host: one 1.6 MB item took 27s, and
        // a save missing six mods is a multi-minute download. The old 30s deadline fired while steamcmd was still
        // running, so the operator saw `TaskCanceledException` for a fetch that then succeeded on its own — the
        // worst shape of bug, because the log says the download failed while the cache quietly fills up.
        // `StartWorldAsync` owns a 20-minute inner budget; this is the outer one, deliberately longer than it.
        await CommandRouteAsync(stream, request, "server-start", name, 1800, token).ConfigureAwait(false);
    }

    private static async Task CommandAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var name = ReadCommandName(request.Body);
        if (name is null)
        {
            await ReplyAsync(stream, 400, JsonError("command-required",
                "请求体应为 {\"command\":\"save|quit|reload-settings\"}"), token).ConfigureAwait(false);
            return;
        }
        if (!CommandNames.Contains(name))
        {
            await ReplyAsync(stream, 400, JsonError("unsupported-command",
                "不支持的命令：" + name + "；可用：" + string.Join(", ", CommandNames)), token).ConfigureAwait(false);
            return;
        }
        // The argument is read from the body rather than passed as null. For `save` it is unused, but the settlement
        // commands take one ("name|owner|width|height") and a caller that sent it would otherwise be silently ignored:
        // the game would create a default settlement and report success for a request nobody made.
        await CommandRouteAsync(stream, request, name, ReadCommandArgument(request.Body),
            name == "save" ? 120 : 20, token).ConfigureAwait(false);
    }

    /// <summary>Runs one command and waits (bounded) for its real outcome.</summary>
    /// <summary>
    /// Starts the game process, optionally on a named save.
    ///
    /// This is the one control that has no game-side equivalent: when the child is not running there is no process
    /// to put a command in front of, so the portal acts directly on its own supervisor. Starting into a save still
    /// goes through <see cref="StartWorldAsync"/>, because that is the path that checks the save's mod set and writes
    /// ModsConfig.xml before the child reads it.
    /// </summary>
    private static async Task GameStartAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var save = QueryValue(request.Query, "save") ?? ReadStringElement(request.Body, "save");
        if (_supervisor is null)
        {
            await ReplyAsync(stream, 503, JsonError("no-supervisor",
                "本进程不是由 supervisor 启动的（--no-supervise）：游戏进程的生命周期不归管理页管"), token).ConfigureAwait(false);
            return;
        }
        CommandOutcome outcome;
        if (string.IsNullOrWhiteSpace(save))
        {
            var started = _supervisor.StartGame(null);
            outcome = started
                ? CommandOutcome.Good("starting", "已请求启动游戏进程（起在 lobby，本页不受影响）")
                : CommandOutcome.Bad("already-running", "游戏进程已经在运行");
        }
        else
        {
            outcome = await StartWorldAsync(save.Trim(), token).ConfigureAwait(false);
        }
        var payload = JsonSerializer.Serialize(new
        {
            ok = outcome.Ok, command = "game-start", code = outcome.Code, detail = outcome.Detail
        }, Compact);
        await ReplyAsync(stream, outcome.Ok ? 200 : 503, payload, token).ConfigureAwait(false);
    }

    /// <summary>Stops or restarts the supervised child. Neither touches this process.</summary>
    private static async Task GamePowerAsync(NetworkStream stream, AdminHttpRequest request, string kind,
        CancellationToken token)
    {
        if (_supervisor is null)
        {
            await ReplyAsync(stream, 503, JsonError("no-supervisor",
                "本进程不是由 supervisor 启动的（--no-supervise）：游戏进程的生命周期不归管理页管"), token).ConfigureAwait(false);
            return;
        }

        CommandOutcome outcome;
        if (kind == "stop")
        {
            // A checkpoint first: stopping without one throws away everything since the last autosave, and the page
            // offers "停止世界" separately for the case where the operator wants exactly that.
            await CheckpointAsync(token).ConfigureAwait(false);
            _supervisor.RequestStop();
            outcome = CommandOutcome.Good("stopping", "已请求停止游戏进程；本页保持在线，可以随时再启动它");
        }
        else
        {
            await CheckpointAsync(token).ConfigureAwait(false);
            outcome = _supervisor.Restart(null)
                ? CommandOutcome.Good("restarting", "已请求重启游戏进程；本页不受影响（保持在线，登录状态不变）")
                : CommandOutcome.Bad("not-running", "游戏进程当前没有在运行：用「启动游戏进程」而不是重启");
        }

        var payload = JsonSerializer.Serialize(new
        {
            ok = outcome.Ok, command = "game-" + kind, code = outcome.Code, detail = outcome.Detail
        }, Compact);
        await ReplyAsync(stream, outcome.Ok ? 200 : 503, payload, token).ConfigureAwait(false);
    }

    /// <summary>
    /// One string member out of a JSON body, or null.
    ///
    /// Deliberately tolerant: these routes are driven by the page and by curl, and a malformed body should produce
    /// "no save given" (which starts the process in the lobby) rather than a 500 from the JSON reader.
    /// </summary>
    private static string? ReadStringElement(byte[] body, string property)
    {
        if (body.Length is 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(property, out var element)) return null;
            if (element.ValueKind != JsonValueKind.String) return null;
            var value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task CommandRouteAsync(NetworkStream stream, AdminHttpRequest request, string name, string? argument,
        int timeoutSeconds, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        CommandOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(name, argument, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ReplyAsync(stream, 503, JsonError("command-timeout",
                "命令 " + name + " 没有在 " + timeoutSeconds + " 秒内完成（游戏进程可能已停止，或被长时间事件阻塞）"),
                token).ConfigureAwait(false);
            return;
        }
        var payload = JsonSerializer.Serialize(new
        {
            ok = outcome.Ok, command = name, code = outcome.Code, detail = outcome.Detail
        }, Compact);
        await ReplyAsync(stream, outcome.Ok ? 200 : 503, payload, token).ConfigureAwait(false);
    }

    /// <summary>The optional <c>argument</c> field of a command request.
    ///
    /// <para>Reads JSON, because that is what <see cref="ReadCommandName"/> reads and both fields arrive in the same
    /// body - using the form parser for one and the JSON parser for the other would accept a body where only half is
    /// legible. The form spelling is still honoured second, since the page posts form-encoded bodies for some routes.
    /// </para></summary>
    private static string? ReadCommandArgument(byte[] body)
    {
        if (body.Length is 0 or > MaximumBodyBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return FormValue(body, "argument");
            if (document.RootElement.TryGetProperty("argument", out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            return null;
        }
        catch (JsonException) { return FormValue(body, "argument"); }
    }

    private static string? ReadCommandName(byte[] body)
    {
        if (body.Length is 0 or > MaximumBodyBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("command", out var element) || element.ValueKind != JsonValueKind.String) return null;
            var name = element.GetString();
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim().ToLowerInvariant();
            return name.Length is > 0 and <= 32 ? name : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// "<c>id:choice</c>" for the game's dialog inbox.
    ///
    /// The id is a number in the page's JSON and a string in the form path, so both spellings are read here: the
    /// game parses one shape, and a mismatch between the two sides would show up as "no such dialog" rather than as
    /// a malformed request - the least debuggable failure available.
    /// </summary>
    private static string? DialogAnswerArgument(AdminHttpRequest request)
    {
        var body = request.Body;
        string? id = null;
        if (body.Length is > 0 and <= MaximumBodyBytes)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("id", out var element))
                    id = element.ValueKind switch
                    {
                        JsonValueKind.Number => element.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        JsonValueKind.String => element.GetString(),
                        _ => null
                    };
            }
            catch (JsonException) { id = null; }
        }
        id ??= BodyString(body, "id") ?? FormValue(body, "id");
        var choice = BodyString(body, "choice") ?? FormValue(body, "choice");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(choice)) return null;
        return id.Trim() + ":" + choice.Trim();
    }

    /// <summary>One string field from a JSON object body. Never logged: the value may be a password.</summary>
    private static string? BodyString(byte[] body, string property)
    {
        if (body.Length is 0 or > MaximumBodyBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return null;
            return element.GetString();
        }
        catch (JsonException) { return null; }
    }

    private static bool IsForm(AdminHttpRequest request)
    {
        var type = request.Header("Content-Type");
        return type is not null && type.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One field from an <c>application/x-www-form-urlencoded</c> body.</summary>
    private static string? FormValue(byte[] body, string name)
    {
        if (body.Length is 0 or > MaximumBodyBytes) return null;
        try
        {
            var text = Encoding.UTF8.GetString(body);
            foreach (var pair in text.Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                if (!pair[..equals].Equals(name, StringComparison.Ordinal)) continue;
                return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            }
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { return null; }
        return null;
    }

    private static bool TryAcceptAttempt()
    {
        lock (AttemptGate)
        {
            var now = Environment.TickCount64;
            if (now - attemptWindowStart >= AttemptWindowMilliseconds) { attemptWindowStart = now; attempts = 0; }
            return ++attempts <= MaximumAttemptsPerWindow;
        }
    }

    private static Task MethodNotAllowed(NetworkStream stream, string allow, CancellationToken token) =>
        ReplyAsync(stream, 405, JsonError("method-not-allowed", "只接受 " + allow),
            token, ("Allow", allow));

    // `detail` is the name the page has read since the first version, while this serializer only ever emitted
    // `message`. Every failure therefore rendered as its machine code ("失败：login-refused") and the sentence
    // that said what to fix was dropped on the floor. Emit both, and keep `code` for scripts.
    private static string JsonError(string code, string message) =>
        JsonSerializer.Serialize(new { ok = false, code, message, detail = message }, Compact);

    private static async Task ReplyAsync(NetworkStream stream, int status, string body, CancellationToken token,
        params (string Name, string Value)[] extra)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response = AdminHttp.Response(status, "application/json; charset=utf-8", bytes, extra);
        await stream.WriteAsync(response, token).ConfigureAwait(false);
    }

    private static async Task ReplyAsync(NetworkStream stream, int status, string body, string contentType, CancellationToken token,
        params (string Name, string Value)[] extra)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response = AdminHttp.Response(status, contentType, bytes, extra);
        await stream.WriteAsync(response, token).ConfigureAwait(false);
    }

    private static async Task ReplyAsync(NetworkStream stream, int status, byte[] body, string contentType, CancellationToken token)
    {
        var response = AdminHttp.Response(status, contentType, body);
        await stream.WriteAsync(response, token).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- JSON shapes

    internal sealed record AdminWire(long OutBytes, long OutFrames, long InBytes, long InFrames,
        double OutBytesPerSecond, double InBytesPerSecond, double OutKilobitsPerSecond);

    internal sealed record AdminCompatibility(string? InstalledGameVersion, string? RoomGameVersion,
        string? InstalledModHash, string? RoomModHash, bool? GameVersionMatches, bool? ModHashMatches, string Comp);

    internal sealed record AdminHealth(string Comp, string Phase, bool Frozen, bool Held, bool HoldLocal,
        int? CheckpointTick, bool CriticalPatchesInstalled, IReadOnlyList<string> FailedPatches, bool Degraded,
        string Summary);

    internal sealed record AdminControl(bool WriteEnabled, string Mode, int Port, string BindAddress,
        bool PasswordConfigured, int CommandsAccepted, string? LastCommand, string? LastResult, string? LastCommandUtc,
        bool Authenticated);

    internal sealed record AdminPortal(string BindKind, bool LoginRequired, int Sessions, bool CohesionDlc,
        int CheckpointMinutes, string? NextCheckpointUtc, long CheckpointsTaken,
        string SavesDirectory, string GameDirectory, string SteamcmdPath);

    /// <summary>One expansion, with the three facts kept apart on purpose: content on disk, authorization, and
    /// whether its defs are actually loaded.</summary>
    internal sealed record AdminExpansion(string Name, string Label, string PackageId, string Folder, bool Loaded,
        string Status, bool OnDisk, string? DiskPath, string? SteamAppId, string Note);

    internal sealed record AdminDlc(IReadOnlyList<AdminExpansion> Expansions, int LoadedCount, bool CohesionDlc,
        string Summary, IReadOnlyList<string> Mismatches);

    internal sealed record AdminMod(string PackageId, string Name, string Version, bool Active, int Order,
        string Source, string Directory, bool Compatible, IReadOnlyList<string> LoadAfter, IReadOnlyList<string> LoadBefore,
        IReadOnlyList<string> ForceLoadAfter, IReadOnlyList<string> ForceLoadBefore);

    internal sealed record AdminMods(IReadOnlyList<string> Active, IReadOnlyList<AdminMod> Mods,
        IReadOnlyList<string> Duplicates, string ConfigPath, string? ConfigVersion,
        IReadOnlyList<string> KnownExpansions, int InstalledCount);

    internal sealed record AdminLogLine(string Type, string Text, int Repeats);

    /// <summary>
    /// The game process, as the portal sees it.
    ///
    /// This is the block that makes a world switch tolerable: the thing being restarted is described by the process
    /// that is restarting it, so "restarting for 6 seconds" is information rather than a dead page.
    /// </summary>
    internal sealed record AdminProcess(string State, bool Running, bool Restarting, int? Pid, double UptimeSeconds,
        int? LastExitCode, string? LastExitUtc, string? LastExitDetail, string Detail);

    /// <summary>The launcher's preflight verdict, as the page shows it.</summary>
    internal sealed record AdminPreflight(bool? Ok, string? Reason, string? Detail, string? Version, string? Repatch,
        string? VersionFrom, string? VersionTo, IReadOnlyList<string> MissingDlc, IReadOnlyList<string> Warnings,
        string? GeneratedUtc);

    internal sealed record AdminStatus(bool Ok, string GeneratedUtc, double UptimeSeconds, string State, string? Failure,
        string? Save, int? Tick, int? GameTick, int? Replicas, int? Players, int? JoinedPeers, double? Fps,
        double Tps, double TargetTps, AdminWire Wire, AdminCompatibility Compatibility, AdminHealth Health,
        AdminControl Control, AdminPortal Portal, AdminPreflight? Preflight, AdminDlc? Dlc,
        AdminSaveMods SaveMods, AdminSwitch? LastSwitch, AdminProcess Process, object? LoadGate,
        IReadOnlyList<AdminLogLine> Log, IReadOnlyList<DialogState> Dialogs);
}
