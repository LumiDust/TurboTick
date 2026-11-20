using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TurboTick.Networking;

/// <summary>
/// "Start this save, with the mods it was made with."
///
/// The rule this file exists for: <b>a save declares its own mods, and the server obeys that list</b> - it does
/// not ask the operator to reproduce it by hand, and it does not start anyway when something is missing.
/// Starting with a missing third-party mod is the quiet failure this whole path is built against: defs vanish,
/// objects turn into "unknown", and the colony loses content that nobody notices until much later.
///
/// Three decisions worth stating:
///
/// 1. <b>The save's order wins.</b> Load order is behaviour (a patch must come after what it patches), so the
///    list is written exactly as the save recorded it, with Core forced first because the game needs it there.
/// 2. <b>A refused start is a start that never happened.</b> The plan is computed before anything is written;
///    if a mod cannot be obtained, no ModsConfig is written and no world is loaded.
/// 3. <b>Nothing here loads a world.</b> It prepares the config and reports; the caller's start path does the
///    rest, so there is still exactly one loader.
///
/// This lives in <c>Networking</c>, not in either host, because <b>both</b> hosts need the same verdict: the
/// portal uses it to decide whether to start the game at all, and the game uses it to gate the world load. Two
/// copies of this logic would be two answers to "is this save startable", and the one that matters is whichever
/// ran last.
/// </summary>
public sealed record SaveModsOptions(string SavesDirectory, string DataDirectory, string GameDirectory,
    string ModsDirectory, string ModsConfigPath, bool AutoApply)
{
    public string ModsRoot => string.IsNullOrEmpty(ModsDirectory)
        ? (string.IsNullOrEmpty(GameDirectory) ? "" : Path.Combine(GameDirectory, "Mods"))
        : ModsDirectory;
}

/// <summary>The outcome of one workshop fetch, as much of it as the planner cares about.</summary>
public sealed record WorkshopFetch(bool Ok, string? Path, string Detail);

/// <summary>What the save's mod list needed and what was done about it. Serialised into the page and preflight.</summary>
public sealed record SaveModsReport(string Save, string? GameVersion, IReadOnlyList<SaveModPlanEntry> Required,
    IReadOnlyList<string> ActiveMods, IReadOnlyList<string> Changed, IReadOnlyList<string> Missing,
    IReadOnlyList<SaveModPlanEntry> ManualRequired, IReadOnlyList<string> AutoDownloaded, bool Written,
    string? Backup, string? Refusal, IReadOnlyList<string> Notes, bool Applied, string AtUtc)
{
    /// <summary>
    /// Manual entries a workshop search can still chase, and the ones it cannot. Published on the report because
    /// the page has to say which of the two it is looking at: "must be put in by hand" and "will be searched for"
    /// are different promises, and a save whose entries carry <c>modSteamId = 0</c> - every third-party entry in a
    /// real 1.7 colony - only ever lands in the first bucket by id and the second by name.
    /// </summary>
    public IReadOnlyList<SaveModPlanEntry> Resolvable { get; init; } = [];

    /// <inheritdoc cref="Resolvable"/>
    public IReadOnlyList<SaveModPlanEntry> UnresolvableRequired { get; init; } = [];
}

public static class SaveModsSync
{
    /// <summary>
    /// Computes - and optionally applies - the mods a save needs.
    ///
    /// <paramref name="download"/> lets the caller ask for workshop fetches; <paramref name="write"/> is the
    /// actual ModsConfig write. Both are separate from the planning so that "what would happen" can be shown
    /// without changing anything. <paramref name="fetch"/> is supplied by the host because steamcmd is invoked
    /// differently in the container entrypoint and in the game process, and the working invocation was measured
    /// the hard way (persistent HOME, explicit proxy) - see each host's SteamCmdRunner.
    ///
    /// <paramref name="search"/> is the workshop title search, injectable for the same reason and one more: the
    /// resolve loop is the only place in this file that opens a socket, so a test that supplies its own search
    /// exercises the whole path - plan, resolve, install, re-plan, refusal text - without a network.
    /// </summary>
    public static async Task<SaveModsReport> RunAsync(string saveName, SaveModsOptions options, string? gameVersion,
        bool download, bool write, Func<long, CancellationToken, Task<WorkshopFetch>>? fetch,
        Action<string>? note, Action<string>? warn, CancellationToken ct,
        Func<string?, string?, CancellationToken, Task<WorkshopLookup>>? search = null)
    {
        var path = SaveLibrary.PathFor(options.SavesDirectory, saveName);
        var manifest = SaveManifest.Read(path);
        var installedMods = ModDirectory.Scan(options.ModsRoot);
        // Two different questions, and conflating them wrote a config that the game could not honour:
        //   `visible`  = under the mods root, so RimWorld will actually scan it;
        //   `available`= visible, plus already-downloaded workshop content and DLC content.
        // Measured: keeping only one set made a downloaded mod look "already installed", so it was never linked
        // into the mods root, yet it still went into activeMods - the game then fails to find a package it was
        // told to load.
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installedMods)
            if (mod.PackageId.Length > 0) visible.Add(mod.PackageId);
        var available = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
        // Already-downloaded workshop content counts as available. Without this a mod that is sitting in
        // <game>/steamapps/workshop/content/294100/<id>/ was reported as "downloadable" and re-downloaded on every
        // start, which looks exactly like a download that never worked.
        var workshopMods = ModDirectory.ScanWorkshop(WorkshopContentRoot(options.GameDirectory));
        foreach (var mod in workshopMods)
        {
            if (mod.PackageId.Length == 0) continue;
            // A workshop snapshot of the server's own mod is not an acceptable substitute for the server's own
            // mod. Counting it as "available" is worse than useless: the plan would report the save's TurboTick
            // requirement as satisfied while the copy the game can actually see is the one it refuses to load.
            // Left out, a missing self surfaces as SelfMissing and the start is refused with a sentence that
            // points at the deployment instead of at the workshop.
            if (SaveManifest.IsSelf(mod.PackageId)) continue;
            available.Add(mod.PackageId);
        }
        // DLC content counts as installed for the config, and separately as "DLC present" for the preflight.
        var dataIds = ModDirectory.DataPackageIds(options.GameDirectory);
        var dlcPresent = new HashSet<string>(dataIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in dataIds) available.Add(id);

        var currentConfig = ModsConfigFile.Read(options.ModsConfigPath);
        var currentActive = new HashSet<string>(currentConfig.ActiveMods, StringComparer.OrdinalIgnoreCase);
        // Content that is already downloaded but not yet visible to the game has to be exposed before the plan is
        // computed, otherwise every start re-downloads it and the config names a package the game cannot see.
        // This is a plain link operation: no network, no write to the save or the config.
        var exposed = ExposeDownloadedWorkshop(manifest, visible, options, warn);
        if (exposed.Count > 0)
        {
            note?.Invoke("已把 " + exposed.Count + " 个已下载的工坊 mod 接入 mods 目录："
                + string.Join(", ", exposed));
            foreach (var id in exposed) available.Add(id);
        }
        var plan = SaveModPlanner.Plan(manifest, available, dlcPresent, currentActive);

        var downloaded = new List<string>();
        foreach (var entry in plan.Downloadable.ToArray())
        {
            if (!download || fetch is null) break;
            var result = await fetch(entry.WorkshopId, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                warn?.Invoke("工坊下载失败 " + entry.Id + " #" + entry.WorkshopId + "：" + result.Detail);
                continue;
            }
            try
            {
                if (result.Path is not null && InstallWorkshopMod(result.Path, entry.Id, options, note, warn))
                    downloaded.Add(entry.Id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke("链接工坊 mod 失败 " + entry.Id + "：" + error.GetType().Name + ": " + error.Message);
            }
        }
        // Path B: the save named a mod, not a workshop id. Measured on a real 77 MB colony: every third-party
        // entry in <modSteamIds> is 0, so the loop above never runs for any of them and the operator is told to
        // copy files by hand for mods that are one search away. Search by the recorded name, download the
        // candidates and keep only the one whose own About.xml declares the packageId the save asked for -
        // trusting the top hit installs translations and forks, which then fail at load under a different name.
        foreach (var entry in plan.Resolvable.ToArray())
        {
            if (!download || fetch is null) break;
            var hit = await WorkshopSearch.ResolveAsync(entry.Id, entry.Name,
                (workshopId, token) => ResolveFetchAsync(fetch, workshopId, token), ct, search).ConfigureAwait(false);
            if (!hit.Ok || string.IsNullOrWhiteSpace(hit.Path))
            {
                warn?.Invoke("按名称搜索工坊失败 " + entry.Id + "（" + entry.Name + "）：" + (hit.Problem ?? "无结果"));
                continue;
            }
            note?.Invoke("已按名称找到工坊 mod " + entry.Id + " → #" + hit.WorkshopId + "（" + hit.PackageId + "）");
            try
            {
                if (InstallWorkshopMod(hit.Path!, entry.Id, options, note, warn)) downloaded.Add(entry.Id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke("安装工坊 mod 失败 " + entry.Id + "：" + error.GetType().Name + ": " + error.Message);
            }
        }
        if (downloaded.Count > 0) plan = SaveModPlanner.WithDownloaded(plan, downloaded);

        var report = new SaveModsReport(
            Save: saveName,
            GameVersion: manifest.GameVersion,
            Required: plan.Required,
            ActiveMods: plan.ActiveMods,
            Changed: plan.Changed,
            Missing: plan.Missing,
            ManualRequired: plan.ManualRequired,
            AutoDownloaded: downloaded,
            Written: false,
            Backup: null,
            Refusal: plan.Refusal,
            Notes: plan.Notes,
            Applied: false,
            AtUtc: DateTime.UtcNow.ToString("O"))
        {
            Resolvable = plan.Resolvable,
            UnresolvableRequired = plan.UnresolvableRequired
        };

        if (!plan.Complete)
        {
            warn?.Invoke("存档 " + saveName + " 需要服务端没有的 mod，拒绝启动：" + string.Join(", ", plan.Missing));
            WriteModsPreflight(report, options);
            return report;
        }
        if (!write)
        {
            // Two different reasons not to write, and saying the wrong one is worse than saying nothing: a preview
            // request from the page is not the same as a deployment that switched auto-apply off. Measured on .29 -
            // AutoApply was on and the page still reported "auto-apply-mods=off", which sends the operator hunting
            // for a setting that is not set.
            var reason = options.AutoApply
                ? "只检查，未写 ModsConfig.xml（这次请求是预演；启动存档时会按 auto-apply-mods 自动写入）"
                : "auto-apply-mods=off：只检查，未写 ModsConfig.xml";
            var dry = report with { Notes = [.. plan.Notes, reason] };
            WriteModsPreflight(dry, options);
            return dry;
        }
        var applied = WriteModsForSave(plan.ActiveMods, currentConfig, options, gameVersion, note, warn);
        report = report with
        {
            Written = true,
            Backup = applied.Backup,
            Applied = applied.Ok,
            Notes = [.. plan.Notes, applied.Detail]
        };
        WriteModsPreflight(report, options);
        return report;
    }

    /// <summary>
    /// Called by the host that owns the start path, before a world is loaded. Returns null when the save may be
    /// loaded, or the sentence explaining why it may not.
    ///
    /// The game reads <c>ModsConfig.xml</c> once, before this code exists - so a config written here cannot
    /// change the *current* process. What it can do is make the next start correct, and say plainly that a
    /// restart is needed. That is the only honest option: pretending the running session has the save's mods
    /// would be exactly the silent content loss this path exists to prevent.
    /// </summary>
    public static string? Verify(string? saveName, SaveModsOptions options, string? gameVersion, bool download,
        Func<long, CancellationToken, Task<WorkshopFetch>>? fetch,
        Action<string>? note, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(saveName)) return null;
        if (SaveLibrary.Normalize(saveName) is not { } name) return null;
        try
        {
            var path = SaveLibrary.PathFor(options.SavesDirectory, name);
            if (!File.Exists(path))
            {
                warn?.Invoke("配置的存档 " + name + " 不存在，跳过 mod 清单检查");
                return null;
            }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var report = RunAsync(name, options, gameVersion, download, options.AutoApply, fetch, note, warn,
                cancellation.Token).GetAwaiter().GetResult();
            if (report.Refusal is { } refusal)
            {
                warn?.Invoke("存档 " + name + " 需要服务端没有的 mod，拒绝载入该世界：" + refusal
                    + "（判定已写入 preflight.json 的 mods 段）");
                return refusal;
            }
            if (report.Written && report.Changed.Count > 0)
                warn?.Invoke("已按存档 " + name + " 重写 ModsConfig.xml（新增 activeMods "
                    + report.Changed.Count + " 项）；本进程已按旧配置加载，需重启一次才生效");
            else if (report.Changed.Count == 0)
                note?.Invoke("存档 " + name + " 的 mod 清单与当前 ModsConfig 一致");
            return null;
        }
        catch (Exception error)
        {
            warn?.Invoke("检查存档 mod 清单失败（按拒绝处理）：" + error.GetType().Name + ": " + error.Message);
            return "无法读取存档的 mod 清单：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>steamcmd's content cache for this game. The layout is steamcmd's, not ours.</summary>
    public static string WorkshopContentRoot(string gameDirectory) =>
        string.IsNullOrEmpty(gameDirectory)
            ? ""
            : Path.Combine(gameDirectory, "steamapps", "workshop", "content", GameVersions.SteamAppId);

    /// <summary>
    /// Adapts the host's workshop fetch to the shape <see cref="WorkshopSearch.ResolveAsync"/> expects.
    ///
    /// The two records carry different fields for a reason: the resolution loop needs the download's path and its
    /// own verdict, and nothing else it does with the attempt list. Keeping the adapter here rather than widening
    /// <c>WorkshopFetch</c> means a host that can only answer "ok + where" stays a valid host.
    /// </summary>
    private static async Task<WorkshopDownload> ResolveFetchAsync(
        Func<long, CancellationToken, Task<WorkshopFetch>> fetch, long workshopId, CancellationToken ct)
    {
        var result = await fetch(workshopId, ct).ConfigureAwait(false);
        return new WorkshopDownload(result.Ok, workshopId, result.Path, result.Detail);
    }

    /// Copies workshop content the save needs into the mods directory, for items that are already downloaded.
    ///
    /// RimWorld only scans the directory next to the executable (<c>GenFilePaths.ModsFolderPath</c>), which this
    /// deployment bind-mounts from the same host directory as <c>/data/mods</c>, so a mod sitting in steamcmd's
    /// cache is invisible to the game even though it is on disk. Copying it here means "downloaded" and
    /// "installed" stop being two different states the operator has to reconcile by hand.
    ///
    /// Returns the packageIds it linked. A failure is a warning, not a refusal: the plan then reports the mod as
    /// downloadable and the normal download path runs.
    /// </summary>
    private static List<string> ExposeDownloadedWorkshop(SaveModManifest manifest, ISet<string> visible,
        SaveModsOptions options, Action<string>? warn)
    {
        var linked = new List<string>();
        if (string.IsNullOrEmpty(options.ModsRoot)) return linked;
        var contentRoot = WorkshopContentRoot(options.GameDirectory);
        if (!Directory.Exists(contentRoot)) return linked;

        // Resolve by packageId, not by the save's `modSteamIds`.
        //
        // Measured on a real 77 MB save: every third-party entry has `modSteamId = 0`; only the five DLCs carry a
        // real id. The old loop keyed on `requirement.SteamId > 0`, so for that save it could never link anything -
        // the content was downloaded and sitting in the cache, the plan called it "present", the config listed it,
        // and the game still could not see it because nothing was under the mods root. Reading the packageId out
        // of the cache is both available and more accurate than trusting an id the save does not have.
        var cached = ModDirectory.ScanWorkshop(contentRoot);
        var byId = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in cached)
        {
            if (mod.PackageId.Length == 0) continue;
            byId.TryAdd(SaveManifest.CanonicalId(mod.PackageId), mod);
        }

        foreach (var requirement in manifest.Mods)
        {
            if (SaveManifest.Classify(requirement.Id) != SaveModSource.ThirdParty) continue;
            var canonical = SaveManifest.CanonicalId(requirement.Id);
            if (visible.Contains(canonical)) continue;
            // The saved steam id is a hint the cache can confirm, never a requirement: 0 is the normal value.
            InstalledMod? source = null;
            if (requirement.SteamId > 0
                && FindWorkshopItem(contentRoot, requirement.SteamId) is { } byItem)
            {
                source = new InstalledMod(canonical, requirement.Name, byItem, requirement.SteamId, null);
            }
            else if (byId.TryGetValue(canonical, out var hit))
            {
                source = hit;
            }
            if (source is null) continue;
            try
            {
                if (InstallWorkshopMod(source.Directory, canonical, options, null, warn))
                {
                    visible.Add(canonical);
                    linked.Add(canonical);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke("接入工坊 mod 失败 " + canonical + "：" + error.GetType().Name + ": " + error.Message);
            }
        }
        return linked;
    }

    /// <summary>Where steamcmd puts one workshop item, if it is there at all.</summary>
    public static string? FindWorkshopItem(string contentRoot, long workshopId)
    {
        if (string.IsNullOrEmpty(contentRoot)) return null;
        var direct = Path.Combine(contentRoot, workshopId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (File.Exists(Path.Combine(direct, "About", "About.xml"))) return direct;
        if (!Directory.Exists(contentRoot)) return null;
        // Older steamcmd builds nested one extra level; accept that rather than reporting "not downloaded".
        foreach (var nested in Directory.EnumerateDirectories(contentRoot))
        {
            var candidate = Path.Combine(nested,
                workshopId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (File.Exists(Path.Combine(candidate, "About", "About.xml"))) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Copies a downloaded workshop item under the mods root, named after the packageId so two items cannot
    /// collide on a folder name.
    ///
    /// A copy, not a link. Links were the first answer and they cost the archive: our room archive refuses a
    /// tree containing a link (copying the target drags a whole mounted volume in; copying the link dangles the
    /// moment that volume moves), so one linked mod meant the room could not be archived at all.
    ///
    /// It is a copy and not a move because steamcmd owns its cache and keeps updating it in place; moving the
    /// item out would silently pin the world to the version that happened to be downloaded first. Instead the
    /// copy records where it came from and when, and <see cref="IsStale"/> refreshes it when the cache moves on.
    /// </summary>
    private static bool InstallWorkshopMod(string source, string packageId, SaveModsOptions options,
        Action<string>? note, Action<string>? warn)
    {
        // The server's own mod is not a download. Copying a workshop snapshot of it beside the running build makes
        // the game find two directories declaring the same packageId, and in a container (no Steam, so both copies
        // report ContentSource.ModsFolder) Verse.ModLister does not resolve that with the "_steam" suffix - it logs
        // an error and drops whichever directory it scanned second. See SaveManifest.TurboTickId.
        if (SaveManifest.IsSelf(packageId))
        {
            warn?.Invoke("拒绝用工坊副本替换服务端自身的 mod：" + packageId
                + "（服务端自带这一份，重复安装会让游戏丢弃其中一份）");
            return false;
        }
        var roots = ModsRoots(options);
        if (roots.Count == 0) return false;
        var name = SafeFolderName(packageId);
        var created = false;
        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, name);
            if (Directory.Exists(target))
            {
                if (!IsStale(source, target)) continue;
                try
                {
                    Directory.Delete(target, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    warn?.Invoke("工坊 mod " + packageId + " 有新版本，但旧副本删不掉：" + error.Message);
                    continue;
                }
                note?.Invoke("工坊 mod " + packageId + " 的缓存比本地副本新，重新复制");
            }
            try
            {
                CopyTree(source, target);
                WriteOrigin(target, source);
                created = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke("复制工坊 mod 失败 " + packageId + "：" + error.GetType().Name + ": " + error.Message);
                continue;
            }
            note?.Invoke("工坊 mod " + packageId + " 已复制到 " + target + "（源 " + source + "）");
        }
        return created;
    }

    private const string OriginFile = ".turbotick-origin";

    /// <summary>Records which cache directory this copy came from, so a later cache update can be noticed.</summary>
    private static void WriteOrigin(string target, string source)
    {
        try
        {
            File.WriteAllText(Path.Combine(target, OriginFile),
                source + "\n" + NewestWriteUtc(source).ToString("O", CultureInfo.InvariantCulture) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Provenance is an optimisation, never a correctness requirement: without it the copy simply is not
            // refreshed automatically, which is exactly the old behaviour.
        }
    }

    /// <summary>True when the workshop cache holds something newer than what was copied into the mods root.</summary>
    private static bool IsStale(string source, string target)
    {
        var stamp = Path.Combine(target, OriginFile);
        if (!File.Exists(stamp)) return false;
        try
        {
            var recorded = NewestWriteUtc(source);
            var lines = File.ReadAllLines(stamp);
            if (lines.Length < 2) return false;
            if (!string.Equals(lines[0], source, StringComparison.Ordinal)) return true;
            return DateTime.TryParse(lines[1], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var previous) && recorded > previous;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime NewestWriteUtc(string directory)
    {
        var newest = Directory.GetLastWriteTimeUtc(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (written > newest) newest = written;
        }
        return newest;
    }

    /// <summary>
    /// Every directory a workshop item has to physically exist in. In the container both names resolve to the
    /// same host directory mounted twice (<c>/data/mods</c> for the operator, <c>&lt;game&gt;/Mods</c> because
    /// the game's own Mods path cannot be configured from the command line); on a bare-metal install they really
    /// are two places, which is why both get filled.
    /// </summary>
    private static List<string> ModsRoots(SaveModsOptions options)
    {
        var roots = new List<string>(2);
        var primary = options.ModsRoot;
        if (!string.IsNullOrEmpty(primary)) roots.Add(primary);
        if (!string.IsNullOrEmpty(options.GameDirectory))
        {
            var mirror = Path.Combine(options.GameDirectory, "Mods");
            if (!roots.Contains(mirror, StringComparer.Ordinal)) roots.Add(mirror);
        }
        return roots;
    }

    private static string SafeFolderName(string packageId)
    {
        var builder = new StringBuilder(packageId.Length);
        foreach (var c in packageId)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        return builder.ToString();
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private sealed record ApplyOutcome(bool Ok, string Backup, string Detail);

    /// <summary>Writes <c>activeMods</c> for these ids, keeping every other section of the file.</summary>
    private static ApplyOutcome WriteModsForSave(IReadOnlyList<string> active, ModsConfigSnapshot current,
        SaveModsOptions options, string? gameVersion, Action<string>? note, Action<string>? warn)
    {
        try
        {
            if (string.IsNullOrEmpty(options.ModsConfigPath))
                return new ApplyOutcome(false, "", "服务端不知道 ModsConfig.xml 的位置");
            var directory = Path.GetDirectoryName(options.ModsConfigPath)!;
            Directory.CreateDirectory(directory);
            var backup = "";
            if (File.Exists(options.ModsConfigPath))
            {
                backup = options.ModsConfigPath + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture);
                File.Copy(options.ModsConfigPath, backup, overwrite: false);
            }
            var version = gameVersion ?? current.Version ?? "1.7";
            var applied = ModsConfigFile.Apply(current, active, version);
            var temporary = options.ModsConfigPath + ".tmp";
            File.WriteAllText(temporary, applied.Xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, options.ModsConfigPath, overwrite: true);
            note?.Invoke("按存档写 activeMods（" + applied.Active.Count + " 项）：" + string.Join(", ", applied.Active));
            return new ApplyOutcome(true, backup, "已写入 activeMods " + applied.Active.Count + " 项（knownExpansions 保留 "
                + applied.PreservedExpansions.Count + " 条）");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("写 ModsConfig.xml 失败：" + error.GetType().Name + ": " + error.Message);
            return new ApplyOutcome(false, "", error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>
    /// Writes the <c>mods</c> section into <c>preflight.json</c>, merging into whatever the container wrote.
    ///
    /// The container's preflight runs before the game starts and cannot know this plan - it is computed from the
    /// save's meta against a mods directory. So this owns one key and leaves every other key alone: the
    /// launcher's DLC verdict and this verdict end up in one file, which is what makes "why did it not start"
    /// answerable in one place.
    /// </summary>
    private static void WriteModsPreflight(SaveModsReport report, SaveModsOptions options)
    {
        try
        {
            if (string.IsNullOrEmpty(options.DataDirectory)) return;
            var path = Path.Combine(options.DataDirectory, "preflight.json");
            Directory.CreateDirectory(options.DataDirectory);
            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                try
                {
                    using var existing = JsonDocument.Parse(File.ReadAllText(path));
                    if (existing.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var property in existing.RootElement.EnumerateObject())
                            root[property.Name] = JsonSerializer.Deserialize<JsonElement>(property.Value.GetRawText());
                }
                catch (Exception error) when (error is JsonException or IOException)
                {
                    // A half-written or foreign file must not stop the mods section from being published.
                    root["previousUnreadable"] = error.GetType().Name;
                }
            }
            root["mods"] = new
            {
                required = report.Required.Select(entry => new
                {
                    id = entry.Id,
                    name = entry.Name,
                    workshopId = entry.WorkshopId == 0 ? null : entry.WorkshopId.ToString(),
                    source = entry.Source.ToString().ToLowerInvariant(),
                    disposition = entry.Disposition.ToString(),
                    dispositionValue = (int)entry.Disposition,
                    // An operator reads this file to answer "why did it not start"; the enum name is the answer
                    // and the number is only there so a script can sort by severity if it wants to.
                    detail = entry.Detail
                }),
                present = report.Required.Where(entry => entry.Disposition is SaveModDisposition.Core
                    or SaveModDisposition.Dlc or SaveModDisposition.Present).Select(entry => entry.Id),
                missing = report.Missing,
                autoDownloaded = report.AutoDownloaded,
                manualRequired = report.ManualRequired.Select(entry => new { id = entry.Id, name = entry.Name }),
                // Which missing mods a search can still get, and which only a human can. Without this the file
                // says "must be copied in by hand" for mods that are one search away, which is what the real
                // save's third-party entries all looked like before the search path existed.
                resolvable = report.Resolvable.Select(entry => new { id = entry.Id, name = entry.Name }),
                unresolvableRequired = report.UnresolvableRequired.Select(entry => new { id = entry.Id, name = entry.Name }),
                activeMods = report.ActiveMods,
                written = report.Written,
                backup = report.Backup,
                refusal = report.Refusal,
                save = report.Save,
                autoApply = options.AutoApply,
                atUtc = report.AtUtc
            };
            lock (typeof(SaveModsSync))
            {
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(root),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporary, path, overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Publishing the verdict is best-effort; a full disk must not turn a refusal into a start.
            _ = error;
        }
    }
}
