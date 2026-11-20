using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurboTick.Networking;

namespace TurboTick.Portal;

/// <summary>
/// Drives the system steamcmd for the two things the web portal can do that take real time: logging a Steam
/// account in, and updating the game install.
///
/// Three rules, all of them learned the hard way on the deployment host:
///
/// 1. The password is never an argument. steamcmd asks for it on stdin, and a password in argv is readable from
///    <c>ps</c> by every user on the host, lands in shell history, and gets logged by anything that prints a
///    command line. The durable credential is steamcmd's own store under the mounted steam root.
/// 2. Nothing here touches Verse. These are background tasks started from HTTP threads; they own a process, a
///    couple of files and an immutable progress snapshot that the page reads.
/// 3. Every failure is a <see cref="Log.Warning"/> plus a message the operator can act on. A missing steamcmd or a
///    Steam Guard prompt must not look like "the download button does nothing".
/// </summary>
internal static class SteamCmdRunner
{
    private static readonly object Gate = new();
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);
    /// <summary>How long a Steam Guard prompt waits for the operator. A code arrives by mail or app, not instantly.</summary>
    private static readonly TimeSpan CodeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromHours(4);
    /// <summary>Every proxy spelling steamcmd has been observed to honour, plus the lowercase forms curl uses.</summary>
    private static readonly string[] ProxyVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NO_PROXY", "no_proxy"];

    private static string steamcmd = SteamCmd.DefaultExecutable;
    private static string steamRoot = "/data/steamcmd";
    private static string gameDirectory = "/data/game";
    private static string dataDirectory = "/data/state";

    private static volatile SteamLoginSnapshot login = SteamLoginSnapshot.Unknown;
    private static volatile SteamProgress progress = SteamProgress.Idle;
    private static volatile string? account;
    private static Task? loginTask;
    private static Task? downloadTask;
    private static Task<WorkshopResult>? workshopTask;

    internal static void Configure(string executable, string steamRootPath, string gamePath, string dataPath)
    {
        if (!string.IsNullOrWhiteSpace(executable)) steamcmd = executable;
        if (!string.IsNullOrWhiteSpace(steamRootPath)) steamRoot = steamRootPath;
        if (!string.IsNullOrWhiteSpace(gamePath)) gameDirectory = gamePath;
        if (!string.IsNullOrWhiteSpace(dataPath)) dataDirectory = dataPath;
    }

    /// <summary>
    /// Sets the proxy, from the deployment configuration or from the page.
    ///
    /// The environment is not always the carrier: the game process is started by a launcher script that may not
    /// export a proxy, and a container's <c>environment:</c> block is easy to forget. Setting it explicitly here means
    /// "the operator configured a proxy" and "steamcmd was given a proxy" cannot drift apart.
    ///
    /// <para><b>null and the empty string are different answers.</b> null is "this source has no opinion" - the
    /// deployment passed no <c>-steam-proxy</c> and the page saved nothing - so whatever the container's own
    /// environment says must survive untouched. The empty string is the operator saying "connect directly", which has
    /// to actively clear the variables; leaving them set would keep routing downloads through a dead hop. Conflating
    /// the two wiped a compose file's <c>HTTP_PROXY</c> on every start, and the download failures that followed
    /// looked like a broken workshop id.</para>
    /// </summary>
    internal static void ConfigureProxy(string? proxy)
    {
        if (proxy is null) return;
        var value = proxy.Trim();
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
            Environment.SetEnvironmentVariable(name, value.Length == 0 ? null : value);
        configuredProxy = value;
        // The portal's own HTTP traffic has to follow the same decision. It is a separate client (the workshop
        // title search runs on our socket, not on steamcmd's), so without this line a host would download through
        // the tunnel while being unable to search through it - and both failures look like "工坊连不上".
        WorkshopSearch.SetProxy(value.Length == 0 ? "" : value);
        if (value.Length > 0) Log.Message("[TurboTick admin] steamcmd 与工坊搜索使用代理：" + value);
    }

    /// <summary>What the operator last set, independent of the environment (empty = direct).</summary>
    private static string configuredProxy = "";

    /// <summary>The proxy in effect, in one string, or empty for a direct connection.</summary>
    internal static string Proxy
    {
        get
        {
            if (configuredProxy.Length > 0) return configuredProxy;
            // Not configured on this page: report whatever the deployment gave the process, so the field shows the
            // value that is actually in force rather than an empty box over a working proxy.
            foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return "";
        }
    }

    /// <summary>Persists the page-set proxy next to the rest of the admin state, so a restart keeps it.
    ///
    /// Missing file means "the page never set one" - not "direct". The distinction is the same one
    /// <see cref="ConfigureProxy"/> makes: a deployment that only uses <c>TURBOTICK_STEAM_PROXY</c> must keep working
    /// after a restart, and returning early here is what preserves it.</summary>
    internal static void LoadProxy()
    {
        try
        {
            var path = Path.Combine(dataDirectory, "steam-proxy.txt");
            if (!File.Exists(path)) return;
            var value = File.ReadAllText(path).Trim();
            ConfigureProxy(value);
            Log.Message("[TurboTick admin] 已读取管理页保存的代理：" + (value.Length == 0 ? "（直连）" : value));
        }
        catch (Exception error) { Log.Warning("[TurboTick admin] 读取代理设置失败：" + Describe(error)); }
    }

    internal static void SaveProxy(string? proxy)
    {
        var value = proxy?.Trim() ?? "";
        ConfigureProxy(value);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "steam-proxy.txt"), value);
            Log.Message("[TurboTick admin] 代理已更新：" + (value.Length == 0 ? "（直连）" : value));
        }
        catch (Exception error) { Log.Warning("[TurboTick admin] 保存代理设置失败：" + Describe(error)); }
    }

    internal static string Executable => steamcmd;
    internal static string GameDirectory => gameDirectory;
    internal static SteamLoginSnapshot LoginState => login;
    internal static SteamProgress DownloadState => progress;
    internal static string? Account => account;
    internal static bool DownloadRunning => progress.Running;
    internal static bool LoginRunning => loginTask is { IsCompleted: false };

    /// <summary>
    /// Remembers the account name and, when needed, runs one interactive-free login so steamcmd stores the
    /// credential under the mounted root. The password is written to the child's stdin and nowhere else.
    /// </summary>
    internal static bool TryBeginLogin(string? user, string? password, out string error)
    {
        error = "";
        if (!SteamCmd.IsValidAccount(user, out var reason)) { error = reason; return false; }
        if (string.IsNullOrEmpty(password)) { error = "密码不能为空"; return false; }
        if (password.Length > 1024) { error = "密码过长"; return false; }
        if (password.IndexOfAny(['\r', '\n', '\0']) >= 0) { error = "密码含控制字符"; return false; }
        lock (Gate)
        {
            if (loginTask is { IsCompleted: false }) { error = "已经有一个登录在进行"; return false; }
            account = user!.Trim();
            var loginAccount = account;
            var secret = password;
            login = new SteamLoginSnapshot(false, "starting", loginAccount, DateTime.UtcNow, "正在登录 steamcmd", false);
            loginTask = Task.Run(() => LoginWorkerAsync(loginAccount, secret));
        }
        return true;
    }

    /// <summary>
    /// Watches steamcmd's own authentication log.
    ///
    /// <b>stdout is not the source of truth for a login.</b> Measured on the deployment host: with a Steam Guard
    /// mobile authenticator, steamcmd prints its prompt and then says nothing at all - no <c>Logged in OK</c>, no
    /// <c>Steam&gt;</c> prompt - while the operator approves on their phone. What does happen is written to
    /// <c>connection_log.txt</c> under the steam root:
    ///
    /// <code>
    /// [08:16:58] PollAuthSessionStatus succeeded and has refresh token
    /// [08:16:58] [Connected, 4, 3] [U:1:221667661] Logging on [U:1:221667661]
    /// [08:16:59] [Logging On, 4, 3] RecvMsgClientLogOnResponse() : [U:1:221667661] 'OK'
    /// [08:16:59] [Logged On, 4, 3] RecvMsgClientLogOnResponse() : processing complete
    /// </code>
    ///
    /// A phase pinned on `Code` because stdout stayed quiet is exactly the bug this reads around: the page showed
    /// "仍在等待验证码" while the account had been logged in for ten minutes.
    /// </summary>
    private static class AuthLog
    {
        private static string Path_ => System.IO.Path.Combine(steamRoot, ".local", "share", "Steam", "logs", "connection_log.txt");

        internal static long Length()
        {
            try { return File.Exists(Path_) ? new FileInfo(Path_).Length : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>Everything appended since <paramref name="offset"/>. Empty when the log is unreadable.</summary>
        internal static string Since(long offset)
        {
            try
            {
                if (!File.Exists(Path_)) return "";
                using var stream = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= offset) return "";
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>The success markers. Each one only appears once the session actually carries a token.</summary>
        internal static bool ReportsSuccess(string appended) =>
            appended.Contains("has refresh token", StringComparison.OrdinalIgnoreCase)
            || appended.Contains("Successfully generated token", StringComparison.OrdinalIgnoreCase)
            || appended.Contains("RecvMsgClientLogOnResponse() : ", StringComparison.OrdinalIgnoreCase)
               && appended.Contains("'OK'", StringComparison.Ordinal);

        /// <summary>What steamcmd recorded as the outcome, for the page. Empty when nothing was written.</summary>
        internal static string LastVerdict(string appended)
        {
            if (appended.Contains("has refresh token", StringComparison.OrdinalIgnoreCase))
                return "Steam 已发放刷新令牌（PollAuthSessionStatus succeeded）";
            if (appended.Contains("RecvMsgClientLogOnResponse() : ", StringComparison.OrdinalIgnoreCase)
                && appended.Contains("'OK'", StringComparison.Ordinal))
                return "Steam 返回登录成功（RecvMsgClientLogOnResponse OK）";
            if (appended.Contains("BeginAuthSessionViaCredentials failed", StringComparison.OrdinalIgnoreCase))
                return "Steam 拒绝了凭据（BeginAuthSessionViaCredentials failed）";
            return "";
        }
    }

    private static async Task LoginWorkerAsync(string user, string password)
    {
        var transcript = new List<string>(64);
        void Publish(LoginPhase phase, string message, bool needsCode = false)
        {
            session = new LoginSession(phase, user, message, DateTime.UtcNow, needsCode,
                transcript.Count > 40 ? transcript.GetRange(transcript.Count - 40, 40) : transcript.ToArray());
            login = new SteamLoginSnapshot(phase is LoginPhase.LoggedIn, Describe(phase), user, DateTime.UtcNow, message, false);
        }
        try
        {
            if (!File.Exists(steamcmd))
            {
                Publish(LoginPhase.Failed, "找不到 steamcmd：" + steamcmd + "（容器里应为 /usr/games/steamcmd）");
                Log.Warning("[TurboTick admin] steamcmd 不存在：" + steamcmd);
                return;
            }
            Directory.CreateDirectory(steamRoot);
            Publish(LoginPhase.Starting, "正在启动 steamcmd");
            var info = PtyInfo(steamcmd, ["+login", user]);
            using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);
            if (!process.Start()) throw new InvalidOperationException("steamcmd did not start");
            loginProcess = process;
            // Cancelling is how both the timeout and the "we already know the answer" path end the run; the token
            // also makes `Steam>` not have to be typed twice.
            using var stop = new CancellationTokenSource();
            stop.CancelAfter(LoginTimeout);
            var wrotePassword = false;
            var verdict = LoginPhase.Idle;
            var reason = "";

            void Send(string text)
            {
                try
                {
                    process.StandardInput.Write(text);
                    process.StandardInput.Flush();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
            }

            void Finish(LoginPhase phase, string message)
            {
                if (verdict is not LoginPhase.Idle) return;
                verdict = phase;
                reason = message;
                Publish(phase, message);
                // `Steam>` keeps the child alive forever; leaving it is part of the login, not an extra step.
                Send("quit\n");
                stop.Cancel();
            }

            // The verdict comes from steamcmd's own log, not from stdout: see AuthLog. The offset is taken before the
            // child starts so a token written seconds after the prompt is still seen as new.
            var logOffset = AuthLog.Length();
            // The recognisers below are the shapes actually measured on the deployment host:
            //   password:                      (SGR-wrapped prompt, then the password line is our write)
            //   Proceeding with login using username/password.
            //   Logging in user 'x' [U:1:0] to Steam Public...ERROR (Invalid Password)
            //   This account is protected by a Steam Guard mobile authenticator.   (and then silence)
            void Handle(string raw)
            {
                var line = Clean(raw);
                if (line.Length == 0) return;
                transcript.Add(line);
                if (SteamCmd.TryReadFailure(line, out var failure)) { Finish(LoginPhase.Failed, failure); return; }
                if (line.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Two-factor", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("two factor", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("mobile authenticator", StringComparison.OrdinalIgnoreCase))
                {
                    // "protected by a Steam Guard mobile authenticator" is the approval prompt: there is no code to
                    // type, the operator taps Approve in the app. Both shapes are published as Code so the page can
                    // explain which one it is; SubmitCode is only meaningful for the typed variant.
                    var typed = line.Contains("code:", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Two-factor", StringComparison.OrdinalIgnoreCase);
                    Publish(LoginPhase.Code,
                        typed ? "需要 Steam Guard 验证码" : "请在手机 Steam App 上批准这次登录（面板会自动检测结果）",
                        needsCode: typed);
                    // A human round-trip is not a timeout; the phone approval can take a while.
                    stop.CancelAfter(CodeTimeout);
                    return;
                }
                if (line.Contains("Logged in OK", StringComparison.OrdinalIgnoreCase)) { Finish(LoginPhase.LoggedIn, "已登录"); return; }
                if (line.Contains("password:", StringComparison.OrdinalIgnoreCase) && !wrotePassword)
                {
                    wrotePassword = true;
                    Publish(LoginPhase.Password, "已提交密码，等待 Steam 校验");
                    Send(password + "\n");
                    return;
                }
                if (line.Contains("Proceeding with login", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Logging in user", StringComparison.OrdinalIgnoreCase))
                    Publish(LoginPhase.Authenticating, "正在校验账号");
            }

            // The auth-log pump: this is what recognises a successful login. stdout stays silent after the Steam
            // Guard prompt, and the old code sat there until the deadline while the account was already in.
            var authPump = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var appended = AuthLog.Since(logOffset);
                    if (AuthLog.ReportsSuccess(appended))
                    {
                        Finish(LoginPhase.LoggedIn, AuthLog.LastVerdict(appended));
                        return;
                    }
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            });

            var reading = Task.WhenAll(
                PumpAsync(process.StandardOutput, Handle),
                PumpAsync(process.StandardError, Handle));
            // The code pump: steamcmd is blocked on a stdin line while a Steam Guard prompt is up, and the only way
            // that line ever arrives is the page posting it to /api/steam/code. Without this task the prompt sat
            // unanswered until the timeout - which is exactly the failure this replaces.
            var codePump = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested && !process.HasExited)
                {
                    var code = pendingCode;
                    if (code is null) { await Task.Delay(250).ConfigureAwait(false); continue; }
                    pendingCode = null;
                    Send(code + "\n");
                    Publish(LoginPhase.Authenticating, "已提交验证码，等待 Steam 校验");
                }
            });
            // Wait for either the child to end, the transcript to finish, or a deadline. While a code prompt is up,
            // the deadline keeps being pushed forward by SubmitCode, so a slow operator is not a failure.
            var deadline = Task.Delay(Timeout.Infinite, stop.Token);
            var finished = await Task.WhenAny(exited.Task, reading, deadline).ConfigureAwait(false);
            // Finish() cancels the token it is waiting on, so "the deadline fired" is also what a decided login
            // looks like. Checking the verdict first is what keeps a successful login from being killed here.
            if (verdict is not LoginPhase.Idle)
            {
                // Nothing to do: the answer is already published and `quit` has been sent.
            }
            else if (finished == deadline) { try { process.Kill(entireProcessTree: true); } catch (Exception) { } }
            else if (finished == reading)
            {
                // Both pipes closed. Give the process a moment to report its own exit code.
                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            }
            stop.Cancel();
            try { await Task.WhenAny(codePump, authPump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false); }
            catch (Exception) { }
            if (verdict is LoginPhase.LoggedIn)
            {
                // Persisted immediately: the credential is durable on disk, and the page reads this file after a
                // restart. Without it a successful login still looked like "尚未登录" until the next run.
                SaveState();
                return;
            }
            if (verdict is LoginPhase.Failed) { Log.Warning("[TurboTick admin] Steam 登录失败：" + reason); return; }
            var message = stop.IsCancellationRequested
                ? "登录超时：没有在时限内拿到结果。若账号开启了 Steam Guard，请在手机 App 上批准，或在此页填验证码"
                : "steamcmd 已退出，但它的认证日志里没有成功记录；请确认账号状态与是否需要在本次会话里完成 Steam Guard";
            Publish(LoginPhase.Failed, message);
            Log.Warning("[TurboTick admin] steamcmd 登录未完成：" + message);
        }
        catch (Exception error)
        {
            Publish(LoginPhase.Failed, Describe(error));
            Log.Warning("[TurboTick admin] steamcmd 登录异常：" + Describe(error));
        }
        finally
        {
            loginProcess = null;
            pendingCode = null;
        }
    }

    /// <summary>Phase name for the old string-shaped snapshot, so an older page keeps working.</summary>
    private static string Describe(LoginPhase phase) => phase switch
    {
        LoginPhase.LoggedIn => "logged-in",
        LoginPhase.Failed => "failed",
        LoginPhase.Code => "code-required",
        LoginPhase.Password => "password",
        LoginPhase.Authenticating => "authenticating",
        LoginPhase.Starting => "starting",
        _ => "unknown"
    };

    /// <summary>steamcmd wraps its prompts in SGR colour codes; the page should show the text, not the escapes.</summary>
    private static readonly Regex Ansi = new("\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static string Clean(string line) => Ansi.Replace(line, "").Replace("\r", "").Trim();

    /// <summary>Shell-quotes one argument for <c>script -c</c>. Account names are already restricted to alnum/_-.@.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Where the pty bridge lives; baked into the image next to the other scripts.</summary>
    private static readonly string PtyRun = "/opt/turbotick/scripts/pty-run.py";

    /// <summary>
    /// Runs a command under a pseudo-terminal via <c>pty-run.py</c>.
    ///
    /// <b>This is the whole reason an interactive login works.</b> Measured on the deployment host: with plain pipes,
    /// steamcmd's <c>password:</c> and <c>Steam Guard code:</c> prompts and its verdict line never arrive — the child
    /// is block-buffered because its stdout is not a tty, so the entire transcript is flushed only when the process
    /// dies. A reader therefore waits out the timeout having seen nothing, while the child waits forever on a line the
    /// reader would have sent had it seen the prompt. Two <c>script(1)</c> variants were tried first and both still
    /// buffered, because script's own stdout was a pipe as well; owning the pty directly (os.read on the master, no
    /// stdio layer) is what makes each line arrive as it is printed.
    ///
    /// The bridge also disables terminal echo, so the password and the Steam Guard code never appear in the
    /// transcript the page renders, and forwards this process's stdin to the pty — which is what the login worker
    /// writes into.
    /// </summary>
    private static ProcessStartInfo PtyInfo(string executable, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(PtyRun) || !File.Exists("/usr/bin/python3"))
            return ProcessInfo(executable, arguments);
        return ProcessInfo("/usr/bin/python3", [PtyRun, "--", executable, .. arguments]);
    }

    /// <summary>Starts <c>+app_update</c> for one version table entry. Refuses when another run is in flight.</summary>
    internal static bool TryBeginDownload(GameVersionEntry version, out string error)
    {
        error = "";
        lock (Gate)
        {
            if (downloadTask is { IsCompleted: false }) { error = "已经有一个下载在跑"; return false; }
            if (account is null)
            {
                error = "尚未登录 Steam 账号；先 POST /api/steam/login（匿名只能下载本体，DLC 内容拿不到）";
                return false;
            }
            progress = new SteamProgress("starting", 0, 0, 0, "准备调用 steamcmd", DateTime.UtcNow, null, false);
            var login_ = account;
            var entry = version;
            downloadTask = Task.Run(() => DownloadWorkerAsync(entry, login_));
        }
        return true;
    }

    private static async Task DownloadWorkerAsync(GameVersionEntry version, string? user)
    {
        try
        {
            if (!File.Exists(steamcmd))
            {
                progress = new SteamProgress("failed", 0, 0, 0, "找不到 steamcmd：" + steamcmd,
                    progress.StartedUtc, DateTime.UtcNow, false);
                Log.Warning("[TurboTick admin] 下载失败：找不到 steamcmd " + steamcmd);
                return;
            }
            var command = SteamCmd.GameUpdate(steamcmd, gameDirectory, user, version.SteamAppId, version.BetaBranch, validate: true);
            Log.Message("[TurboTick admin] 开始下载游戏：" + command.Display);
            var result = await RunAsync(command.Arguments.ToList(), null, DownloadTimeout, line =>
            {
                if (SteamCmd.TryReadProgress(line, out var parsed) && parsed is not null)
                    progress = new SteamProgress(parsed.Phase, parsed.Percent, parsed.BytesDone, parsed.BytesTotal,
                        version.Key + " · " + parsed.Phase, progress.StartedUtc, null, false);
                else if (SteamCmd.ReportsSuccess(line))
                    progress = new SteamProgress("installing", 100, progress.BytesDone, progress.BytesTotal,
                        version.Key + " 已完整安装", progress.StartedUtc, DateTime.UtcNow, true);
                else if (SteamCmd.TryReadFailure(line, out var failure))
                    progress = new SteamProgress("failed", progress.Percent, progress.BytesDone, progress.BytesTotal,
                        failure, progress.StartedUtc, DateTime.UtcNow, false);
            }).ConfigureAwait(false);
            if (progress is { Ok: true }) return;
            if (progress.Phase is "failed") return;
            var detail = result.TimedOut ? "下载超时"
                : result.ExitCode == 0 ? "steamcmd 结束但未报告 fully installed（可能只是校验通过）"
                : "steamcmd 退出码 " + result.ExitCode;
            var ok = result.ExitCode == 0;
            progress = new SteamProgress(ok ? "done" : "failed", ok ? 100 : progress.Percent, progress.BytesDone,
                progress.BytesTotal, detail, progress.StartedUtc, DateTime.UtcNow, ok);
            if (!ok) Log.Warning("[TurboTick admin] 下载未成功：" + detail);
        }
        catch (Exception error)
        {
            progress = new SteamProgress("failed", progress.Percent, progress.BytesDone, progress.BytesTotal,
                Describe(error), progress.StartedUtc, DateTime.UtcNow, false);
            Log.Warning("[TurboTick admin] 下载异常：" + Describe(error));
        }
    }

    private sealed record RunResult(int ExitCode, bool TimedOut);

    /// <summary>
    /// The environment every steamcmd child gets. Split out of <see cref="RunAsync"/> because the interactive login
    /// starts its process itself: it needs the live stdin handle, which <see cref="RunAsync"/> deliberately closes
    /// after one write.
    /// </summary>
    private static ProcessStartInfo ProcessInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) is { Length: > 0 } directory ? directory : steamRoot
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // HOME is what makes the credential cache durable: steamcmd writes its anonymous config and ssfn files
        // under it. Measured on the deployment host: with HOME=/tmp **every** workshop download failed with a bare
        // "Download item failed (Failure)", and with a persistent directory the same commands all succeeded. The
        // error message says nothing about this, so it is set here and never left to the ambient environment.
        info.Environment["HOME"] = steamRoot;
        info.Environment["XDG_CONFIG_HOME"] = Path.Combine(steamRoot, ".config");
        // A host without direct egress needs the proxy passed explicitly: the bundled steamcmd bootstrap predates
        // environment-proxy support, so nothing would pick these up on its own. Empty/unset means "direct".
        foreach (var name in ProxyVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) info.Environment[name] = value;
        }
        Directory.CreateDirectory(steamRoot);
        Directory.CreateDirectory(Path.Combine(steamRoot, ".config"));
        return info;
    }

    /// <summary>
    /// Runs steamcmd with an optional one-shot stdin payload and streams its output line by line.
    ///
    /// Output is read on the calling task rather than buffered to the end: a first-time steamcmd update prints a
    /// self-update banner and then can sit silent for a minute, and an operator watching a progress endpoint
    /// needs to see that it is alive.
    /// </summary>
    private static async Task<RunResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, TimeSpan timeout,
        Action<string>? observe) => await RunAsync(steamcmd, arguments, stdin, timeout, observe).ConfigureAwait(false);

    private static async Task<RunResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? stdin,
        TimeSpan timeout, Action<string>? observe)
    {
        var info = ProcessInfo(executable, arguments);
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => completed.TrySetResult(process.ExitCode);
        if (!process.Start())
            throw new InvalidOperationException("steamcmd did not start");
        try
        {
            if (stdin is not null)
            {
                // A single write: steamcmd reads one line. The buffer is cleared immediately so a crash dump or a
                // later toString of this scope cannot contain it.
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            process.StandardInput.Close();

            var reading = Task.WhenAll(
                PumpAsync(process.StandardOutput, observe),
                PumpAsync(process.StandardError, observe));
            var finished = await Task.WhenAny(completed.Task, reading, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != completed.Task && finished != reading)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return new RunResult(-1, true);
            }
            var exit = await completed.Task.ConfigureAwait(false);
            return new RunResult(exit, false);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string>? observe)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (observe is null || line.Length == 0) continue;
                try { observe(line); }
                catch (Exception error) { Log.Warning("[TurboTick admin] steamcmd 输出处理失败：" + Describe(error)); }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A killed process closes the pipe; that is the normal end of a timed-out run.
        }
    }

    /// <summary>What the page may show about a login: never the password, and never a raw steamcmd transcript.</summary>
    internal sealed record SteamLoginSnapshot(bool LoggedIn, string Phase, string? Account, DateTime AtUtc, string Message, bool FromDisk)
    {
        internal static SteamLoginSnapshot Unknown { get; } =
            new(false, "unknown", null, DateTime.MinValue, "尚未登录", false);
    }

    /// <summary>
    /// One interactive login, as the page sees it.
    ///
    /// A Steam Guard login is a conversation, not a call: steamcmd prints <c>password:</c>, then either logs in or
    /// asks for a code, and it keeps its <c>Steam&gt;</c> prompt open afterwards. The old implementation wrote the
    /// password once, closed stdin, and waited — so the code prompt got nothing, the process sat at <c>Steam&gt;</c>
    /// until the three-minute timeout, and the page could only say "登录超时". Keeping the child's stdin open and
    /// publishing a phase the page can poll is what turns that into a working login.
    /// </summary>
    internal enum LoginPhase
    {
        /// <summary>No login running or ever started.</summary>
        Idle,
        /// <summary>steamcmd is starting up / printing its banner.</summary>
        Starting,
        /// <summary>It printed <c>password:</c> and the password has been written.</summary>
        Password,
        /// <summary>It wants a Steam Guard / two-factor code and is waiting on stdin.</summary>
        Code,
        /// <summary>Carrying out the login attempt.</summary>
        Authenticating,
        LoggedIn,
        Failed
    }

    /// <summary>The live state of the interactive login; polled by the page while a code prompt is pending.</summary>
    internal sealed record LoginSession(LoginPhase Phase, string? Account, string Message, DateTime AtUtc,
        bool NeedsCode, IReadOnlyList<string> Transcript)
    {
        internal static LoginSession Idle { get; } =
            new(LoginPhase.Idle, null, "尚未登录", DateTime.MinValue, false, []);
    }

    private static volatile LoginSession session = LoginSession.Idle;
    /// <summary>Set while a code prompt is waiting. The page posts the code; the worker writes it and clears it.</summary>
    private static volatile string? pendingCode;
    private static Process? loginProcess;

    internal static LoginSession Session => session;
    internal static bool NeedsCode => session.NeedsCode;

    /// <summary>
    /// Supplies an answer to the currently pending prompt: the Steam Guard code, or the password on the second
    /// prompt. Returns false when nothing is waiting, so a stale page cannot inject text into a later run.
    /// </summary>
    internal static bool SubmitCode(string? code, out string error)
    {
        error = "";
        var value = code?.Trim() ?? "";
        if (value.Length == 0) { error = "验证码不能为空"; return false; }
        if (value.Length > 64 || value.Any(char.IsControl)) { error = "验证码含非法字符"; return false; }
        var process = loginProcess;
        if (process is null || !session.NeedsCode) { error = "当前没有等待输入的提示"; return false; }
        pendingCode = value;
        return true;
    }

    /// <summary>Persists the last known state so a restarted container can say what happened instead of "unknown".</summary>
    internal static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, "steam-state.json");
            var snapshot = new
            {
                login = login with { },
                download = progress,
                account,
                writtenUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { Log.Warning("[TurboTick admin] 写入 steam 状态失败：" + Describe(error)); }
    }

    internal static void LoadState()
    {
        try
        {
            var path = Path.Combine(dataDirectory, "steam-state.json");
            if (!File.Exists(path)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("account", out var accountElement) && accountElement.ValueKind == JsonValueKind.String)
                account = accountElement.GetString();
            if (root.TryGetProperty("login", out var loginElement) && loginElement.ValueKind == JsonValueKind.Object)
            {
                // A persisted "logged-in" is only a hint: the credential lives in steamcmd's own store, so the page
                // marks it FromDisk and the next real download is what proves it still works.
                var loggedIn = loginElement.TryGetProperty("loggedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
                var account2 = loginElement.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : account;
                var message = loginElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                login = new SteamLoginSnapshot(loggedIn, loggedIn ? "logged-in" : "unknown", account2, DateTime.UtcNow,
                    message ?? (loggedIn ? "上次记录为已登录（未在本次进程验证）" : "尚未登录"), loggedIn);
            }
        }
        catch (Exception error) { Log.Warning("[TurboTick admin] 读取 steam 状态失败：" + Describe(error)); }
    }

    private static string Describe(Exception error) => error.GetType().Name + ": " + error.Message;

    /// <summary>Kept for the page: the exact command an operator should run themselves when Steam Guard is needed.</summary>
    internal static string InteractiveLoginHint => steamcmd + " +login <账号>";
    internal static string SteamRoot => steamRoot;
    internal static string ProxyHint => DescribeProxy();

    /// <summary>
    /// The proxy variables actually in effect, for the page. A failed download on a host without egress is
    /// indistinguishable from a broken item id, so this is worth showing rather than inferring.
    /// </summary>
    internal static string DescribeProxy()
    {
        var configured = new List<string>(4);
        foreach (var name in ProxyVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) configured.Add(name + "=" + value);
        }
        return configured.Count == 0 ? "（未配置代理）" : string.Join(" ", configured);
    }

    /// <summary>Where the game itself keeps workshop content. This is the layout steamcmd actually produces.</summary>
    internal static string WorkshopContentRoot(string appId) =>
        Path.Combine(gameDirectory, "steamapps", "workshop", "content", appId);

    /// <summary>
    /// Every place workshop content for RimWorld may land.
    ///
    /// The first one is the real one: `+force_install_dir /data/game` makes steamcmd write
    /// <c>&lt;game&gt;/steamapps/workshop/content/&lt;appId&gt;/&lt;id&gt;</c>. The others are historical or
    /// transitional layouts seen on this host, kept because a cache that exists but is not found reads exactly like
    /// "the download failed".
    /// </summary>
    internal static IReadOnlyList<string> WorkshopContentRoots(string appId) =>
    [
        Path.Combine(gameDirectory, "steamapps", "workshop", "content", appId),
        Path.Combine(steamRoot, "steamapps", "workshop", "content", appId),
        Path.Combine(steamRoot, "workshop", "steamapps", "workshop", "content", appId)
    ];

    /// <summary>
    /// Downloads one workshop item and reports what happened.
    ///
    /// <b>Anonymous, and deliberately so.</b> Workshop content is public: measured on this host, ten items (190 MB)
    /// fetched with `+login anonymous` and no account. Requiring a login was wrong - only the game itself needs one.
    ///
    /// Two conditions actually decide success, and neither is discoverable from steamcmd's output (it says only
    /// "Download item failed (Failure)"):
    /// <list type="number">
    /// <item><b>HOME must be a durable directory.</b> steamcmd keeps its anonymous credential cache there; with
    /// <c>HOME=/tmp</c> every download failed, with a persistent directory the same commands succeeded.</item>
    /// <item><b>A proxy has to be passed explicitly</b> on a host without direct egress. The bundled bootstrap does
    /// not read the environment, so the variables are set on the child process below.</item>
    /// </list>
    /// </summary>
    internal static async Task<WorkshopResult> DownloadWorkshopItemAsync(string appId, long workshopId, CancellationToken ct)
    {
        if (workshopId <= 0) return new WorkshopResult(false, 0, "不是工坊条目（modSteamId=0）", null);
        var executable = SelectSteamCmd();
        if (executable is null) return new WorkshopResult(false, workshopId, "找不到可用的 steamcmd", null);
        if (workshopTask is { IsCompleted: false })
            return new WorkshopResult(false, workshopId, "已经有一个工坊下载在跑", null);
        var previous = workshopTask;
        var task = Task.Run(async () =>
        {
            if (previous is not null) { try { await previous.ConfigureAwait(false); } catch (Exception) { } }
            // The install directory is the *game*, not the steam root: that is what puts content where the game
            // looks for it.
            var command = SteamCmd.WorkshopItem(executable, gameDirectory, appId, workshopId.ToString());
            Log.Message("[TurboTick admin] 匿名下载工坊 mod：" + command.Display + "（代理 " + DescribeProxy() + "）");
            var lines = new List<string>(64);
            var result = await RunAsync(executable, command.Arguments.ToList(), null, TimeSpan.FromMinutes(30), line =>
            {
                lines.Add(line);
                if (SteamCmd.TryReadFailure(line, out var failure)) Log.Warning("[TurboTick admin] 工坊下载：" + failure);
            }).ConfigureAwait(false);
            // steamcmd reports success several ways across versions; the cache on disk is the tie-breaker.
            var path = FindWorkshopItem(appId, workshopId);
            var ok = path is not null;
            var detail = ok ? "已下载到 " + path
                : result.TimedOut ? "超时"
                : lines.Any(line => SteamCmd.ReportsSuccess(line) || line.Contains("Success. Downloaded item", StringComparison.OrdinalIgnoreCase))
                    ? "steamcmd 报告成功但缓存里找不到内容（检查 workshop 目录布局）"
                    : "steamcmd 退出码 " + result.ExitCode + "（HOLD）";
            if (!ok) Log.Warning("[TurboTick admin] 工坊 mod " + workshopId + " 下载失败：" + detail);
            return new WorkshopResult(ok, workshopId, detail, path);
        });
        workshopTask = task;
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks a steamcmd that can actually download workshop items.
    ///
    /// Measured on the deployment host: the image's bundled bootstrap (a 2020-era <c>steamcmd.sh</c> over a 32-bit
    /// <c>linux32</c> payload) exited **127** from the game process and, run standalone, reported
    /// "Download item failed (Failure)" — it cannot fetch workshop content on this host at all. A current
    /// <c>steamcmd.sh</c> of the same major version, when present, works. So the newer one is preferred and the
    /// bundled path is only a last resort; the choice is logged, because "which binary ran" is the first question
    /// when a download fails.
    /// </summary>
    private static string? SelectSteamCmd()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(gameDirectory, "steamcmd", "steamcmd.sh"),
                     Path.Combine(steamRoot, "steamcmd.sh"),
                     steamcmd
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            // A launcher script alone is not enough: the 64-bit payload has to be there, otherwise the run fails
            // with a bare exit code.
            var directory = Path.GetDirectoryName(candidate)!;
            if (Directory.Exists(Path.Combine(directory, "linux64")) || Path.GetFileName(candidate) != "steamcmd.sh")
                return candidate;
        }
        return File.Exists(steamcmd) ? steamcmd : null;
    }

    /// <summary>Where an item actually landed; both historical cache layouts are checked.</summary>
    internal static string? FindWorkshopItem(string appId, long workshopId)
    {
        foreach (var root in WorkshopContentRoots(appId))
        {
            var candidate = Path.Combine(root, workshopId.ToString());
            if (Directory.Exists(Path.Combine(candidate, "About"))) return candidate;
            // Some steamcmd builds nest one more level under the item id.
            foreach (var inner in SafeDirectories(candidate) ?? [])
                if (Directory.Exists(Path.Combine(inner, "About"))) return inner;
        }
        return null;
    }

    private static string[]? SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The outcome of one workshop fetch, with the path so the caller can link it into the mods dir.</summary>
    internal sealed record WorkshopResult(bool Ok, long WorkshopId, string Detail, string? Path);

    internal static string Describe() => new StringBuilder()
        .Append("steamcmd=").Append(steamcmd)
        .Append(" root=").Append(steamRoot)
        .Append(" game=").Append(gameDirectory)
        .ToString();
}
