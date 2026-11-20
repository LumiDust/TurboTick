using System.Net.Sockets;
using System.Text.Json;
using TurboTick.Networking;

namespace TurboTick.Portal;

/// <summary>
/// The portal's side of "start this save, with the mods it was made with".
///
/// The decision logic lives in <see cref="SaveModsSync"/> (Networking), because the game process needs the same
/// verdict to gate the world load and two copies would be two answers. What stays here is what is genuinely
/// host-specific:
///
/// - <b>Where the files are.</b> The portal knows the directories because it was given them on the command line;
///   the game would have to ask Verse.
/// - <b>How steamcmd is invoked.</b> A workshop fetch from this container has three measured requirements
///   (persistent <c>HOME</c>, an explicit proxy, and not sharing a process with the game), so it goes through
///   <see cref="SteamCmdRunner"/> rather than through the shared planner.
///
/// A green "start" from the portal therefore means the same thing it means inside the game, and a refusal is
/// written to <c>preflight.json</c> by the same code.
/// </summary>
internal static partial class AdminPage
{
    private static readonly object AutoApplyGate = new();
    private static volatile bool autoApplyMods = true;
    private static volatile SaveModsReport? lastSaveMods;

    internal const string AutoApplyOption = "auto-apply-mods";

    /// <summary>Turns the "apply the save's mods automatically" switch from the deployment config.</summary>
    private static void ConfigureAutoApply(AdminPageConfig options)
    {
        autoApplyMods = options.AutoApplyMods;
        if (!autoApplyMods)
            Log.Message("[TurboTick portal] auto-apply-mods=off：启动存档只检查 mod，不写 ModsConfig.xml");
    }

    /// <summary>The directories this deployment uses, in the shape the shared planner wants.</summary>
    private static SaveModsOptions SaveModsOptions => new(
        SavesDirectory: config.SavesDirectory,
        DataDirectory: config.DataDirectory,
        GameDirectory: config.GameDirectory,
        ModsDirectory: config.ModsDirectory,
        ModsConfigPath: config.ModsConfigPath,
        AutoApply: autoApplyMods);

    /// <summary>
    /// Computes - and optionally applies - the mods a save needs.
    ///
    /// <paramref name="download"/> lets the caller ask for workshop fetches; <paramref name="write"/> is the
    /// actual ModsConfig write. Both are separate from the planning so that "what would happen" can be shown
    /// without changing anything.
    /// </summary>
    internal static Task<SaveModsReport> SyncSaveModsAsync(string saveName, bool download, bool write,
        CancellationToken ct) =>
        SaveModsSync.RunAsync(saveName, SaveModsOptions, CurrentStatus().InstalledVersion, download, write,
            download ? Fetch : null,
            message => Log.Message("[TurboTick portal] " + message),
            message => Log.Warning("[TurboTick portal] " + message),
            ct);

    /// <summary>Fetches one workshop item with the invocation this container actually needs.</summary>
    private static async Task<WorkshopFetch> Fetch(long workshopId, CancellationToken ct)
    {
        var result = await SteamCmdRunner.DownloadWorkshopItemAsync(GameVersions.SteamAppId, workshopId, ct)
            .ConfigureAwait(false);
        return new WorkshopFetch(result.Ok, result.Path, result.Detail);
    }

    /// <summary>
    /// Called from the start path before the game is launched, so that a save whose mods cannot be satisfied is
    /// never started at all. Returns the refusal sentence, or null when the save may be started.
    /// </summary>
    internal static Task<SaveModsReport> PrepareSaveAsync(string saveName, CancellationToken ct) =>
        SyncSaveModsAsync(saveName, download: true, write: autoApplyMods, ct);

    /// <summary>
    /// Checks (and writes) the mod config for a save named on the command line, and records whether the game may
    /// be started at all.
    ///
    /// This is the twin of the game's own gate. The game checks again before it loads the world - it has to,
    /// because the config it reads was written before its code existed - but the portal checks first so that a
    /// refusal never costs a process start, and so the answer is already visible on the page.
    /// </summary>
    internal static bool VerifyConfiguredSaveMods(string? saveName)
    {
        var refusal = SaveModsSync.Verify(saveName, SaveModsOptions, CurrentStatus().InstalledVersion,
            download: false, fetch: null,
            message => Log.Message("[TurboTick portal] " + message),
            message => Log.Warning("[TurboTick portal] " + message));
        saveModsRefusal = refusal;
        return refusal is null;
    }

    /// <summary>Why the configured save must not be started, or null when nothing was refused.</summary>
    internal static string? SaveModsRefusal => saveModsRefusal;

    private static volatile string? saveModsRefusal;

    /// <summary>The status block: what the last start attempt decided, and the switch that decided it.</summary>
    private static AdminSaveMods SaveModsBlock() => new(
        AutoApply: autoApplyMods,
        Last: lastSaveMods);

    // ---------------------------------------------------------------- HTTP

    /// <summary>
    /// "What would starting this save need?" - a dry run. Reads the save's meta, scans the mods directory and
    /// diffs against the current config; writes nothing, downloads nothing.
    /// </summary>
    private static async Task SaveModsInspectAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var name = QueryValue(request.Query, "save");
        if (SaveLibrary.Normalize(name) is not { } save)
        {
            await ReplyAsync(stream, 400, JsonError("save-required",
                "用法：GET /api/saves/mods?save=<存档名>；可用存档见 /api/saves"), token).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(SaveLibrary.PathFor(config.SavesDirectory, save)))
        {
            await ReplyAsync(stream, 404, JsonError("save-missing", "找不到 " + save + ".rws"), token).ConfigureAwait(false);
            return;
        }
        var report = await SyncSaveModsAsync(save, download: false, write: false, token).ConfigureAwait(false);
        lastSaveMods = report;
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            dryRun = true,
            autoApply = autoApplyMods,
            report,
            modsDirectory = SaveModsOptions.ModsRoot,
            detail = report.Missing.Count == 0
                ? "存档需要的 mod 都在；POST /api/saves/mods/apply 会写 ModsConfig.xml 并下载可下载项"
                : "缺 " + report.Missing.Count + " 个 mod：" + string.Join(", ", report.Missing)
        }, Json);
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the save's mod list for real: downloads what the workshop can provide, then writes
    /// <c>activeMods</c>. Refuses - and writes nothing - when something cannot be obtained.
    /// </summary>
    private static async Task SaveModsApplyAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var save = SaveLibrary.Normalize(BodyString(request.Body, "save"));
        if (save is null)
        {
            await ReplyAsync(stream, 400, JsonError("save-required",
                "请求体应为 {\"save\":\"<存档名>\"}"), token).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(SaveLibrary.PathFor(config.SavesDirectory, save)))
        {
            await ReplyAsync(stream, 404, JsonError("save-missing", "找不到 " + save + ".rws"), token).ConfigureAwait(false);
            return;
        }
        // Deliberately long: a workshop fetch of a large mod is minutes, and this call is the whole point.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        work.CancelAfter(TimeSpan.FromMinutes(25));
        var report = await SyncSaveModsAsync(save, download: true, write: true, work.Token).ConfigureAwait(false);
        lastSaveMods = report;
        var payload = JsonSerializer.Serialize(new
        {
            ok = report.Missing.Count == 0 && report.Written,
            autoApply = autoApplyMods,
            report,
            detail = report.Refusal ?? (report.Written
                ? "已写 ModsConfig.xml（activeMods " + report.ActiveMods.Count + " 项，下载 "
                  + report.AutoDownloaded.Count + " 个工坊 mod）；modHash 会变，需重启游戏进程并清 state"
                : "mod 清单已齐，但 ModsConfig.xml 没有写成；看日志里的 mods-config 报错")
        }, Json);
        await ReplyAsync(stream, report.Missing.Count == 0 ? 200 : 409, payload, token).ConfigureAwait(false);
    }

    /// <summary>One decoded query parameter, without pulling in a query parser.</summary>
    private static string? QueryValue(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            if (!Uri.UnescapeDataString(pair[..equals]).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            try { return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')); }
            catch (Exception error) when (error is UriFormatException or ArgumentException) { return null; }
        }
        return null;
    }
}

/// <summary>The status block for the save-driven mod list.</summary>
internal sealed record AdminSaveMods(bool AutoApply, SaveModsReport? Last);
