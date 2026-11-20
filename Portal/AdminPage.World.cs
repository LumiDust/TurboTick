
using System.Globalization;
using TurboTick.Networking;

namespace TurboTick.Portal;

/// <summary>
/// Stopping and switching worlds <b>without losing the administration page</b>.
///
/// The page now lives in its own process, so the two operations finally have the costs they always deserved:
///
/// <list type="number">
/// <item><b>Stop</b> freezes the loaded world: the game checkpoints, persists, empties the room and stops ticking,
/// while the process, the listener and this page stay exactly where they are. Nothing restarts, so nothing can go
/// dark. Implemented as a command the game executes on its own thread.</item>
/// <item><b>Switch</b> checkpoints the current world, then replaces the game <em>process</em>. That is not a
/// stylistic choice: <b>measured</b>, calling <c>GameDataSaveLoader.LoadGame</c> a second time inside one process
/// crashes the engine with SIGSEGV (exit 139). The difference from the previous design is that the supervisor owns
/// the restart, so the page - its sessions, its listener, its open websocket-free polling - never goes away. The
/// operator sees "restarting" for a few seconds instead of losing the panel and having to log in again.</item>
/// </list>
///
/// Order is the safety argument in both cases: checkpoint (durable, verified) → persist → then act. Every failure
/// lands in "the old world is still there and nothing changed", never in a half-torn-down state.
/// </summary>
internal static partial class AdminPage
{
    private static volatile AdminSwitch? lastSwitch;

    /// <summary>
    /// True when a world is loaded but deliberately frozen by "stop", so a tick must not advance.
    ///
    /// Read from the game's published state rather than kept as a second copy here: two flags that can disagree is
    /// exactly the bug that made "start again after stop" report success while nothing happened.
    /// </summary>
    internal static bool WorldStopped => CurrentStatus() is { State: "lobby" or "idle" };

    /// <summary>What the last stop/switch did, so the page can show it instead of a bare "200".</summary>
    internal sealed record AdminSwitch(string Kind, string? From, string? To, string Phase, string Detail, string AtUtc,
        bool Ok);

    internal static AdminSwitch? LastSwitch => lastSwitch;

    /// <summary>
    /// Why the load funnel is (not) running, as far as this process can tell.
    ///
    /// The funnel itself runs inside the game, and its preconditions are published there; what this side can add is
    /// the half the game cannot see - whether a child process exists at all, and what the supervisor is doing with
    /// it. When one precondition stays false the symptom is identical for all of them ("start" returns 200 and
    /// nothing loads), so publishing them turns that into a readable answer.
    /// </summary>
    internal static object LoadGate()
    {
        var game = CurrentStatus();
        var process = _supervisor?.Snapshot;
        return new
        {
            stopped = WorldStopped,
            initialLoadRequested = game.State is "loading-initial-save" or "playing",
            initialSave = game.Save,
            busy = game.State is "loading-initial-save",
            longEvent = game.State is "starting",
            programState = game.State,
            game = game.GameRunning,
            serverRunning = game.GameRunning,
            process = process?.State ?? "unsupervised",
            restarting = process?.Restarting ?? false,
            supervise = _supervisor is not null
        };
    }

    /// <summary>
    /// Asks the game to checkpoint and verifies that it did.
    ///
    /// Shared by stop and switch so there is exactly one definition of "we may safely touch the world". The answer
    /// comes back through the file inbox, so a game that is wedged produces a refusal instead of a silent success.
    /// </summary>
    private static async Task<CommandOutcome> CheckpointAsync(CancellationToken token)
    {
        var process = _supervisor?.Snapshot;
        if (process is not { Running: true })
            return CommandOutcome.Bad("no-server", "游戏进程没有在运行");
        return await CommandInbox.SendAsync("save", null, TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the <em>world</em>: checkpoint, then freeze. The game process, the listener and this page keep running.
    ///
    /// Separate from shutdown on purpose: an operator who wants the world to stop should not lose the page they would
    /// use to start it again.
    /// </summary>
    private static async Task<CommandOutcome> StopWorldAsync(CancellationToken token)
    {
        var previous = CurrentStatus().Save;
        var check = await CheckpointAsync(token).ConfigureAwait(false);
        if (!check.Ok)
        {
            var refused = new AdminSwitch("stop", previous, null, "checkpoint-failed",
                "停止前" + check.Detail + "，已保留当前世界，未做任何改动", DateTime.UtcNow.ToString("O"), false);
            lastSwitch = refused;
            return CommandOutcome.Bad(refused.Phase, refused.Detail);
        }
        var stop = await CommandInbox.SendAsync("server-stop", null, TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
        if (!stop.Ok) return stop;
        var detail = "已保存并停止当前世界（检查点已提交）；服务端回到 lobby，管理页保持在线";
        lastSwitch = new AdminSwitch("stop", previous, null, "lobby", detail, DateTime.UtcNow.ToString("O"), true);
        return CommandOutcome.Good("lobby", detail);
    }

    /// <summary>
    /// Replaces the loaded world with another save, by replacing the game process under it.
    ///
    /// The checkpoint is not optional even though the process is about to die: a restart that loses the last minutes
    /// of a colony is indistinguishable, from the operator's chair, from a crash. So the sequence is
    /// checkpoint → verify → backup the target → ask the supervisor to restart against it, and each step refuses
    /// before the next one runs.
    /// </summary>
    private static async Task<CommandOutcome> StartWorldAsync(string? argument, CancellationToken token)
    {
        var name = SaveLibrary.Normalize(argument);
        if (name is null)
            return CommandOutcome.Bad("save-invalid", "存档名不合法：只能是不带路径的文件名");
        var path = SaveLibrary.PathFor(config.SavesDirectory, name);
        if (!File.Exists(path))
            return CommandOutcome.Bad("save-missing", "找不到存档 " + name + SaveLibrary.Extension
                + "（在 " + config.SavesDirectory + "）");
        if (_supervisor is null)
            return CommandOutcome.Bad("no-supervisor",
                "本进程不是由 supervisor 启动的，无法启动游戏进程；请用 TURBOTICK_SUPERVISOR=portal 启动容器");

        var busy = CurrentStatus();
        if (busy.State is "loading-initial-save") return CommandOutcome.Bad("busy", "正在载入存档，稍后重试");

        // The mod list of the target save decides whether the game can start at all. This runs in the portal because
        // it is pure file work and it may download workshop items - the game process must not be doing that while it
        // is also being asked to shut down.
        SaveModsReport report;
        using (var sync = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            sync.CancelAfter(TimeSpan.FromMinutes(20));
            try
            {
                report = await PrepareSaveAsync(name, sync.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Log.Warning("[TurboTick portal] 读取存档 mod 清单失败：" + error.GetType().Name + ": " + error.Message);
                return CommandOutcome.Bad("mods-unreadable", "无法读取存档的 mod 清单：" + error.Message);
            }
        }
        if (report.Refusal is { } refusal) return CommandOutcome.Bad("mods-missing", refusal);

        var previous = CurrentStatus().Save;
        var backup = BackupSave(name);
        // A checkpoint only protects something if a world is actually live in a child we can still talk to. When the
        // game is already stopped there is nothing to lose, and demanding a checkpoint would refuse the single most
        // useful case: "the process died, start it on a different save". `<see cref="CurrentStatus"/>` keeps naming
        // the last loaded save after the process is gone, so `previous is not null` is not the right test.
        var live = _supervisor.Snapshot.Running;
        if (live && previous is not null && !string.Equals(previous, name, StringComparison.OrdinalIgnoreCase))
        {
            var check = await CheckpointAsync(token).ConfigureAwait(false);
            if (!check.Ok)
            {
                var refused = new AdminSwitch("switch", previous, name, "checkpoint-failed",
                    "换档前" + check.Detail + "，已保留当前世界（" + previous + "），未切换",
                    DateTime.UtcNow.ToString("O"), false);
                lastSwitch = refused;
                return CommandOutcome.Bad(refused.Phase, refused.Detail);
            }
        }

        var detail = (live
                ? "已保存当前世界并从 " + (previous ?? "?") + " 切换到 " + name
                : "游戏进程当时没有在运行，直接从 " + (previous ?? "无") + " 切换到 " + name)
            + "；游戏进程重启以载入目标存档，本页不受影响（保持在线，登录状态不变）"
            + (backup is null ? "（备份失败，见日志）" : "；备份 " + Path.GetFileName(backup));
        if (!_supervisor.Restart(name))
        {
            var refused = new AdminSwitch("switch", previous, name, "restart-refused",
                "已保存当前世界，但重启游戏进程被拒绝（supervisor 正在停止）", DateTime.UtcNow.ToString("O"), false);
            lastSwitch = refused;
            return CommandOutcome.Bad(refused.Phase, refused.Detail);
        }
        lastSwitch = new AdminSwitch("switch", previous, name, "switching", detail, DateTime.UtcNow.ToString("O"), true);
        Log.Message("[TurboTick portal] " + detail);
        return CommandOutcome.Good("switching", detail);
    }

    /// <summary>
    /// Copies the save aside before a switch, keeping the ten newest backups.
    ///
    /// This runs here rather than in the game because it is pure file work and because the game is about to be
    /// replaced - a backup that depends on the process being healthy is not a backup.
    /// </summary>
    private static string? BackupSave(string name)
    {
        if (string.IsNullOrWhiteSpace(config.BackupDirectory)) return null;
        try
        {
            Directory.CreateDirectory(config.BackupDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var target = Path.Combine(config.BackupDirectory, name + "-" + stamp + SaveLibrary.Extension);
            var source = SaveLibrary.PathFor(config.SavesDirectory, name);
            File.Copy(source, target, overwrite: false);
            foreach (var old in SaveLibrary.List(config.BackupDirectory).Skip(10))
            {
                try { File.Delete(SaveLibrary.PathFor(config.BackupDirectory, old.Name)); }
                catch (Exception error) { Log.Warning("[TurboTick portal] 清理旧备份失败：" + error.Message); }
            }
            return target;
        }
        catch (Exception error)
        {
            // A failed backup must not block the switch: the checkpoint already made the current world durable, and
            // refusing here would leave the operator unable to start the save they asked for.
            Log.Warning("[TurboTick portal] 备份存档失败（继续加载）：" + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>
    /// Graceful service shutdown: the game checkpoints, persists and exits; the supervisor does not bring it back.
    ///
    /// Deliberately <b>not</b> "kill the portal": the page is the only way back in, and it costs nothing to keep it
    /// up. The button says "关闭游戏进程", and that is exactly what happens.
    /// </summary>
    private static async Task<CommandOutcome> ShutdownServiceAsync(CancellationToken token)
    {
        if (_supervisor is null)
            return CommandOutcome.Bad("no-supervisor", "本进程不是由 supervisor 启动的：游戏进程的生命周期不归它管");
        var quit = await CommandInbox.SendAsync("quit", null, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        _supervisor.RequestStop();
        if (!quit.Ok && quit.Code is not ("command-timeout" or "cancelled"))
            return quit;
        var detail = "已请求优雅退出：检查点 → 持久化 → 进程退出。本页保持在线，可以随时再启动游戏进程。";
        lastSwitch = new AdminSwitch("shutdown", CurrentStatus().Save, null, "stopping", detail,
            DateTime.UtcNow.ToString("O"), true);
        return CommandOutcome.Good("stopping", detail);
    }
}
