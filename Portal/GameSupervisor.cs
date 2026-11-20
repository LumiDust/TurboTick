using System.Diagnostics;
using System.Text;

namespace TurboTick.Portal;

/// <summary>What the supervisor knows about the game process right now, safe to read from any thread.</summary>
internal sealed record SupervisorSnapshot(
    string State,
    bool Running,
    bool Restarting,
    int? Pid,
    double UptimeSeconds,
    int? LastExitCode,
    string? LastExitUtc,
    string? LastExitDetail,
    string Detail);

/// <summary>
/// Owns the game process: starts it, watches it, replaces it, and stops it.
///
/// This exists because of one measured fact: calling <c>GameDataSaveLoader.LoadGame</c> a second time inside one
/// process crashes the engine with SIGSEGV (exit 139). A world switch therefore has to be a process switch, and
/// something has to be the parent. Previously that something was the container entrypoint, which meant the whole
/// container - and with it the admin page, its port and every login session - went away for ~20 seconds on every
/// switch. Keeping the parent inside the same container but outside the game process is what makes the page stay
/// up: this class is the only thing that restarts, and it restarts one child at a time.
///
/// Three deliberate policies:
/// <list type="number">
/// <item><b>No automatic restart on an unexpected exit.</b> A crash loop is worse than a stopped game: it hides the
/// first error and pins a core. The exit code and the last lines are recorded and shown instead, and an operator
/// presses start. (The deployment this replaces did auto-restart and reached RestartCount=8 on a refused start.)</item>
/// <item><b>Restart is a request, not a kill.</b> The child is asked to quit through the same command path a button
/// uses, so it checkpoints and persists first; only if it is still alive after the grace period is it killed.</item>
/// <item><b>The arguments come from the caller.</b> The entrypoint already builds the game's command line (data
/// directory, ports, steam paths, preflight results) and duplicating that knowledge here would let the two drift.</item>
/// </list>
/// </summary>
internal sealed class GameSupervisor
{
    /// <summary>
    /// How long the child is given to checkpoint and exit before it is killed.
    ///
    /// Measured: a 77 MB colony save took 148 seconds from "quit" to a clean exit, because the quit path writes a
    /// checkpoint and empties the room before it returns. The previous 45 seconds was therefore shorter than the
    /// work it was waiting for, so any real switch killed the outgoing process in the middle of saving its
    /// checkpoint — the one thing this grace period exists to prevent.
    /// </summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(180);

    /// <summary>How long the child gets to pick the quit command out of its inbox.</summary>
    /// <remarks>
    /// This is not the same wait as <see cref="ExitGrace"/>. Picking the file up takes one 250 ms poll; writing a
    /// 77 MB checkpoint takes minutes. Binding both to one number either kills a saving process or makes an
    /// unresponsive one look patient.
    /// </remarks>
    private static readonly TimeSpan QuitAckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a caller of <see cref="Shutdown"/> should wait for the child, given <see cref="ExitGrace"/>.
    ///
    /// Exposed so the caller and the kill deadline cannot drift apart. Docker's <c>stop_grace_period</c> has to be
    /// larger still, or the runtime sends SIGKILL while this wait is still in progress.
    /// </summary>
    internal static TimeSpan ShutdownTimeout => ExitGrace + TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly StringBuilder tail = new();

    private string executable = "";
    private string gameDirectory = "";
    private string dataDirectory = "";
    private string[] arguments = [];

    private Process? child;
    private DateTime startedUtc;
    private DateTime? exitedUtc;
    private int? lastExitCode;
    private string? lastExitDetail;
    private double lastUptime;
    private string detail = "尚未启动";
    private bool exitAsked;
    private StreamWriter? transcript;

    private volatile bool stopping;
    // Set only by Shutdown(). `stopping` deliberately does not end the monitor: the operator can press "stop"
    // and then "start" again, and a monitor thread that ended on the first stop would make the second press a
    // no-op that reports success.
    private volatile bool shutdown;
    private volatile bool restartPending;
    private volatile bool startPending;
    private string? currentSave;

    /// <summary>True once <see cref="Configure"/> has been called; without it, nothing may be started.</summary>
    internal bool Configured { get; private set; }

    internal void Configure(string gameExecutable, string workingDirectory, IReadOnlyList<string> gameArguments,
        string stateDirectory)
    {
        lock (gate)
        {
            executable = gameExecutable;
            gameDirectory = workingDirectory;
            dataDirectory = stateDirectory;
            // Any save baked into the command line is remembered separately so a restart can replace it instead of
            // adding a second -turbotick-save that the game would resolve by whichever it reads last.
            var kept = new List<string>(gameArguments.Count);
            foreach (var argument in gameArguments)
            {
                const string prefix = "-turbotick-save=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = argument[prefix.Length..].Trim();
                    if (value.Length > 0) currentSave = value;
                    continue;
                }
                kept.Add(argument);
            }
            arguments = kept.ToArray();
            Configured = executable.Length > 0;
        }
    }

    /// <summary>Starts the monitor thread. Safe to call once; the thread lives as long as this process does.</summary>
    internal void Start()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "turbotick-supervisor"
        };
        thread.Start();
    }

    /// <summary>Current state, as an immutable snapshot (the page reads this from HTTP threads).</summary>
    internal SupervisorSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var running = child is { HasExited: false };
                var uptime = running ? Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds) : lastUptime;
                // "failed" is a claim about a non-zero exit, and it is the only way this side can tell a crash from a
                // deliberate stop. Anything else that is not running is reported as "stopped", including the moment
                // between Configure() and the first child: guessing "failed" there would paint the page red on boot.
                var state = running
                    ? (restartPending || exitAsked ? "restarting" : "running")
                    : startPending || restartPending ? "starting"
                    : stopping ? "stopped"
                    : lastExitCode is not null and not 0 ? "failed"
                    : "stopped";
                return new SupervisorSnapshot(
                    State: state,
                    Running: running,
                    Restarting: restartPending || exitAsked,
                    Pid: running ? SafePid(child) : null,
                    UptimeSeconds: Math.Round(uptime, 1),
                    LastExitCode: lastExitCode,
                    LastExitUtc: exitedUtc?.ToString("O"),
                    LastExitDetail: lastExitDetail,
                    Detail: detail);
            }
        }
    }

    /// <summary>Starts the child if it is not running. Returns false when there is nothing to start with.</summary>
    internal bool StartGame(string? saveName)
    {
        if (!Configured) return false;
        lock (gate)
        {
            if (saveName is not null) currentSave = saveName;
            if (child is { HasExited: false }) return true;
            startPending = true;
            stopping = false;
            detail = "正在启动游戏进程";
        }
        return true;
    }

    /// <summary>
    /// Replaces the child, loading <paramref name="saveName"/> when one is given.
    ///
    /// Returns as soon as the request is recorded: the checkpoint that makes this safe happens in the child before
    /// it exits, and blocking an HTTP request for that long would make the page look broken while it is working.
    /// </summary>
    internal bool Restart(string? saveName)
    {
        if (!Configured) return false;
        lock (gate)
        {
            if (shutdown) return false;
            if (saveName is not null) currentSave = saveName;
            stopping = false;
            restartPending = true;
            if (child is not { HasExited: false }) startPending = true;
            WriteRestartMarker();
        }
        return true;
    }

    /// <summary>Stops the child and keeps it stopped. The portal itself keeps running.</summary>
    internal void RequestStop()
    {
        lock (gate)
        {
            stopping = true;
            restartPending = false;
            startPending = false;
            detail = "已请求停止游戏进程";
        }
    }

    /// <summary>Asks the child to stop and waits (bounded) for it, so shutdown does not race the process table.</summary>
    /// <remarks>
    /// The wait must outlast <see cref="ExitGrace"/>, otherwise a checkpoint that takes longer than this timeout is
    /// still being written when the caller disposes the process. Callers pass a value derived from ExitGrace for
    /// that reason; the parameter stays for tests that want a short one.
    /// </remarks>
    internal void Shutdown(TimeSpan timeout)
    {
        shutdown = true;
        RequestStop();
        AskChildToStop();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
                if (child is not { HasExited: false }) return;
            Thread.Sleep(100);
        }
        Log.Warning("[TurboTick portal] 等待游戏进程退出超时（" + Math.Round(timeout.TotalSeconds)
            + " 秒）：检查点可能没写完，容器将结束该进程");
    }

    // ------------------------------------------------------------------ monitor thread

    private void Loop()
    {
        while (!shutdown)
        {
            Process? running;
            lock (gate) running = child;
            if (running is not null)
            {
                if (HasExited(running)) { HandleExit(running); continue; }
                var wanted = restartPending || startPending || stopping;
                if (wanted && !exitAsked)
                {
                    lock (gate) exitAsked = true;
                    detail = restartPending ? "正在重启游戏进程（先让游戏写检查点）" : "正在停止游戏进程";
                    AskChildToStop();
                }
                Thread.Sleep(200);
                continue;
            }

            if (shutdown) break;
            if (restartPending || startPending)
            {
                lock (gate)
                {
                    restartPending = false;
                    startPending = false;
                    exitAsked = false;
                }
                StartChild();
                continue;
            }
            Thread.Sleep(200);
        }
    }

    private void StartChild()
    {
        string save;
        string[] args;
        string exe;
        string cwd;
        lock (gate)
        {
            save = currentSave ?? "";
            exe = executable;
            cwd = gameDirectory;
            var list = new List<string>(arguments.Length + 1);
            list.AddRange(arguments);
            if (save.Length > 0) list.Add("-turbotick-save=" + save);
            args = list.ToArray();
        }
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = cwd.Length > 0 ? cwd : Path.GetDirectoryName(exe) ?? ".",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            var started = new Process { StartInfo = start, EnableRaisingEvents = true };
            started.OutputDataReceived += (_, line) => Pump(line.Data);
            started.ErrorDataReceived += (_, line) => Pump(line.Data);
            OpenTranscript();
            started.Start();
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            lock (gate)
            {
                child = started;
                startedUtc = DateTime.UtcNow;
                exitedUtc = null;
                lastExitCode = null;
                lastExitDetail = null;
                detail = "游戏进程已启动（pid " + SafePid(started) + "）";
                File.Delete(RestartMarker());
            }
            Log.Message("[TurboTick portal] 游戏进程已启动：" + exe + " " + string.Join(' ', args));
        }
        catch (Exception error)
        {
            lock (gate)
            {
                child = null;
                lastExitCode = null;
                lastExitDetail = error.GetType().Name + ": " + error.Message;
                detail = "游戏进程启动失败：" + lastExitDetail;
            }
            Log.Error("[TurboTick portal] 游戏进程启动失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private void HandleExit(Process exited)
    {
        int code;
        try { code = exited.ExitCode; } catch (Exception) { code = -1; }
        var ran = Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
        var wasRestart = restartPending;
        lock (gate)
        {
            child = null;
            lastExitCode = code;
            lastUptime = ran;
            exitedUtc = DateTime.UtcNow;
            exitAsked = false;
            lastExitDetail = code == 0
                ? "正常退出（运行 " + Math.Round(ran) + " 秒）"
                : "退出码 " + code + "（运行 " + Math.Round(ran) + " 秒）";
            detail = wasRestart ? "游戏进程已退出，准备重启" : lastExitDetail;
        }
        try { exited.Dispose(); } catch (Exception) { }
        CloseTranscript();

        // The reason an operator needs this line is that a refusal and a crash look identical from the page: both
        // are "the world is not there". The exit code plus the tail of stdout is the difference.
        Log.Message("[TurboTick portal] 游戏进程退出：" + lastExitDetail);
        foreach (var line in Tail(6)) Log.Warning("[TurboTick portal] 游戏输出：" + line);

        if (code == 0 || wasRestart) return;
        // Deliberately no auto-restart: see the class comment. Record it and let a person decide.
        lock (gate) detail = "游戏进程已停止（" + lastExitDetail + "）：不会自动重启，可从本页重新启动";
    }

    private void AskChildToStop()
    {
        Process? running;
        string quitSave;
        lock (gate)
        {
            running = child;
            quitSave = currentSave ?? "";
        }
        if (running is null) return;
        // Ask nicely first: the child's quit path checkpoints, persists and empties the room. A kill would skip all
        // of that, and a switch that loses the last minutes of a colony is indistinguishable from a crash.
        //
        // The deadline belongs to *this* process, not to "whatever is running now". The old child needs minutes to
        // checkpoint, the monitor starts the replacement the moment it exits, and the replacement then inherits the
        // remaining grace of a timer that was never about it: a real switch was observed killing the new world at
        // 40 seconds old with exit code 137, logged as "45 秒内没有退出", while the process it was waiting for had
        // already exited cleanly 148 seconds in. Capture the target and only ever touch that one.
        var target = running;
        _ = Task.Run(async () =>
        {
            // The ack and the exit are two different waits. The ack only says the inbox file was picked up; the
            // exit is the checkpoint being written. Sharing one timeout is what made the original bug reachable:
            // the send timed out, the deadline restarted, and by then `child` pointed at the replacement.
            try { await CommandInbox.SendAsync("quit", null, QuitAckTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            var deadline = DateTime.UtcNow + ExitGrace;
            while (DateTime.UtcNow < deadline)
            {
                if (HasExited(target)) return;
                lock (gate)
                {
                    // Someone already replaced it: this stop request is finished, and the replacement is not a
                    // stubborn child that ignored it.
                    if (!ReferenceEquals(child, target)) return;
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
            if (HasExited(target)) return;
            lock (gate)
            {
                if (!ReferenceEquals(child, target)) return;
            }
            Log.Warning("[TurboTick portal] 游戏进程 " + ExitGrace.TotalSeconds + " 秒内没有退出，强制结束（存档 " + quitSave + "）");
            try { target.Kill(entireProcessTree: true); }
            catch (Exception error) { Log.Warning("[TurboTick portal] 结束游戏进程失败：" + error.Message); }
        });
    }

    // ------------------------------------------------------------------ helpers

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception) { return true; }
    }

    private static int? SafePid(Process? process)
    {
        try { return process?.Id; }
        catch (Exception) { return null; }
    }

    private string RestartMarker() => Path.Combine(dataDirectory, "restart-requested");

    /// <summary>
    /// Leaves the same marker the entrypoint used to read, so an operator (or a wrapper script) can still see that
    /// a restart was requested and for which save. The supervisor does not depend on it: it is a diagnostic.
    /// </summary>
    private void WriteRestartMarker()
    {
        if (dataDirectory.Length == 0) return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (currentSave is { Length: > 0 } save)
                File.WriteAllText(Path.Combine(dataDirectory, "restart-target"), save);
            File.WriteAllText(RestartMarker(), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick portal] 写重启标记失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Keeps the last few lines the child printed, for the exit report and for <see cref="Tail"/>.</summary>
    private void Pump(string? line)
    {
        if (line is null) return;
        lock (gate)
        {
            tail.AppendLine(line);
            if (tail.Length > 24_000) tail.Remove(0, tail.Length - 16_000);
            // The transcript is the page's and the healthcheck's view of the game; the in-memory ring is only for
            // the exit report. Written here rather than by a second `tee` in the entrypoint because two writers to
            // one file interleave their own buffering, and the file is what an operator reads when the page is down.
            try { transcript?.WriteLine(line); } catch (Exception) { transcript = null; }
        }
    }

    /// <summary>
    /// Opens <c>game-stdout.log</c> for the child that is about to start.
    ///
    /// Truncated per launch: the file must describe *this* game process. An appended file made a crash from two
    /// restarts ago look like the current one, and the page shows the newest lines first.
    /// </summary>
    private void OpenTranscript()
    {
        if (dataDirectory.Length == 0) return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, "game-stdout.log");
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            transcript = new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception error)
        {
            transcript = null;
            Log.Warning("[TurboTick portal] 无法写游戏日志：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private void CloseTranscript()
    {
        lock (gate)
        {
            try { transcript?.Dispose(); } catch (Exception) { }
            transcript = null;
        }
    }

    /// <summary>The last <paramref name="count"/> lines the child printed, newest last.</summary>
    internal IReadOnlyList<string> Tail(int count)
    {
        lock (gate)
        {
            var text = tail.ToString().Replace("\r\n", "\n");
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var start = Math.Max(0, lines.Length - count);
            var result = new List<string>(count);
            for (var index = start; index < lines.Length; index++) result.Add(lines[index]);
            return result;
        }
    }
}
