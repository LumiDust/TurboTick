using TurboTick.Networking;

namespace TurboTick.Portal;

/// <summary>
/// The portal's entry point: build a configuration from the command line and the environment, start the supervisor,
/// start the admin listener, then sit in a timer loop until asked to stop.
///
/// This executable exists so that the administration page is not a tenant of the game's lifetime. Everything the
/// page shows is either read from the game's published state file or answered from this process's own knowledge
/// (files, ports, child-process state), which is what makes the page survive a world switch - the operation that
/// previously took it down for ~20 seconds and invalidated every login.
///
/// The whole loop is a <see cref="PeriodicTimer"/> rather than a game frame: there is no game here to hang, and a
/// timer cannot be starved by a colony that is loading.
/// </summary>
internal static class PortalProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (PortalOptions.WantsHelp(args))
        {
            Console.Out.WriteLine(PortalOptions.Usage);
            return 0;
        }

        var options = PortalOptions.Parse(args, out var problem);
        if (problem is not null)
        {
            Console.Error.WriteLine("[TurboTick portal] " + problem);
            Console.Error.WriteLine(PortalOptions.Usage);
            return 2;
        }

        Console.WriteLine("[TurboTick portal] 启动：数据目录 " + Show(options.DataDirectory)
            + "，游戏目录 " + Show(options.GameDirectory) + "，端口 " + options.Port);
        try { Directory.CreateDirectory(options.DataDirectory); }
        catch (Exception error)
        {
            Console.Error.WriteLine("[TurboTick portal] 无法创建数据目录：" + error.GetType().Name + ": " + error.Message);
            return 2;
        }

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            Console.WriteLine("[TurboTick portal] 收到中断信号，开始停止");
            try { lifetime.Cancel(); } catch (Exception) { }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { AdminPage.Stop(); } catch (Exception) { }
        };

        var supervisor = new GameSupervisor();
        if (options.Supervise)
        {
            supervisor.Configure(options.GameExecutable, options.GameDirectory, options.GameArguments,
                options.DataDirectory);
            supervisor.Start();
            AdminPage.AttachSupervisor(supervisor);
            if (options.AutoStart) supervisor.StartGame(options.InitialSave);
        }
        else
        {
            Console.WriteLine("[TurboTick portal] supervisor 已关闭（--no-supervise）：只提供管理页，不启动游戏进程");
        }

        CommandInbox.Configure(options.DataDirectory);
        CommandInbox.Sweep(TimeSpan.FromMinutes(10));
        AdminPage.Start(options.Page, options.SteamAccount);

        // The page's status is rebuilt from files, so a slow tick costs nothing but latency. 500 ms is fast enough
        // that a restart reads as a short flicker and slow enough to keep a container's CPU flat while idle.
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                AdminPage.Tick();
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("[TurboTick portal] 停止管理页");
        try { AdminPage.Stop(); } catch (Exception error)
        {
            Console.WriteLine("[TurboTick portal] 停止管理页失败：" + error.Message);
        }
        try { supervisor.Shutdown(GameSupervisor.ShutdownTimeout); }
        catch (Exception error)
        {
            Console.WriteLine("[TurboTick portal] 停止游戏进程失败：" + error.Message);
        }
        return 0;
    }

    private static string Show(string value) => string.IsNullOrEmpty(value) ? "(未设置)" : value;
}

/// <summary>
/// Everything this executable can be told, from arguments or environment.
///
/// Both spellings exist for one reason: the entrypoint builds arguments (so the container stays readable) while the
/// deployment sets environment variables (so a value can be changed without editing a script). Arguments win.
/// </summary>
internal sealed record PortalOptions
{
    internal const string Usage = """
        用法：TurboTick.Portal [选项]
          --port <端口>              管理页端口（默认 30573；0 表示不启用）
          --bind <地址>              绑定地址（默认 127.0.0.1；非回环地址必须设置密码）
          --data <目录>              状态目录，读写 headless-status.json 等（默认 /data/state）
          --game <目录>              游戏目录，用于扫描 Data/<DLC> 与 Mods（默认 /data/game）
          --saves <目录>             存档目录（默认 /data/saves）
          --mods <目录>              mod 目录（默认 <游戏目录>/Mods）
          --config <文件>            ModsConfig.xml（默认 <存档数据目录>/Config/ModsConfig.xml）
          --backups <目录>           备份目录（默认 <状态目录>/Backups）
          --checkpoint-minutes <n>   定时检查点间隔，0 关闭（默认 0）
          --auto-apply-mods <on|off> 启动存档时按存档写 ModsConfig.xml（默认 on）
          --cohesion-dlc <on|off>    Cohesion DLC 模式（默认 on）
          --game-exe <路径>          游戏可执行文件（默认 <游戏目录>/RimWorld）
          --game-arg <参数>          追加给游戏进程的参数，可重复
          --no-supervise             不启动/不管理游戏进程，只提供管理页
          --no-auto-start            启动时不自动拉起游戏进程
          --save <名字>              首选的启动存档
        环境变量：TURBOTICK_ADMIN_PASSWORD[_FILE]、TURBOTICK_ADMIN_PORT、TURBOTICK_DATA、TURBOTICK_GAME_DIR…（与参数同名，大写，横线换下划线）
        """;

    internal required AdminPageConfig Page { get; init; }
    internal required string DataDirectory { get; init; }
    internal required string GameDirectory { get; init; }
    internal required string GameExecutable { get; init; }
    internal required IReadOnlyList<string> GameArguments { get; init; }
    internal bool Supervise { get; init; }
    internal bool AutoStart { get; init; }
    internal string? InitialSave { get; init; }
    internal string? SteamAccount { get; init; }
    internal int Port => Page.Port;

    /// <summary>
    /// <c>--help</c> and <c>--version</c>. Without this the process treated them as unknown options,
    /// started the supervisor and sat in its loop forever: run inside a container with no mounted game
    /// it printed two errors and then looked like a silent hang, which is how this was found.
    /// </summary>
    internal static bool WantsHelp(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg is "--help" or "-h" or "-?" or "--usage") return true;
        }
        return false;
    }

    internal static PortalOptions Parse(string[] args, out string? problem)
    {
        problem = null;
        string? Value(string name) => Option(args, name)
            ?? Environment.GetEnvironmentVariable("TURBOTICK_" + name.Replace('-', '_').ToUpperInvariant());

        var data = Value("data") ?? Value("state-dir") ?? "/data/state";
        var game = Value("game") ?? Value("game-dir") ?? "/data/game";
        var saves = Value("saves") ?? "/data/saves";
        // The portal is the *admin* endpoint, so it reads the admin port and bind. Falling back to
        // TURBOTICK_PORT would make it fight the game for 30572 the moment both run in one container -
        // which is exactly the deployment this exists for. The names are the ones the old in-page admin
        // server used, so an unchanged compose file still points the portal at the right place.
        var port = AdminPort(args, out var portProblem);
        if (portProblem is not null) problem = portProblem;
        var bind = Option(args, "bind") ?? Env("ADMIN_BIND") ?? Env("BIND") ?? AdminBinding.LoopbackText;
        var password = ReadPassword(out var passwordProblem);
        if (passwordProblem is not null) problem = passwordProblem;
        var checkpoint = Int(Value("checkpoint-minutes"), 0);
        var mods = Value("mods") ?? Path.Combine(game, "Mods");
        var config = Value("config") ?? Value("mods-config")
            ?? Path.Combine(data, "profile", "Config", "ModsConfig.xml");
        var backups = Value("backups") ?? Value("backup-dir") ?? Path.Combine(data, "Backups");
        var exe = Value("game-exe") ?? Path.Combine(game, "RimWorld");
        var arguments = new List<string>(16);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is not "--game-arg" || index + 1 >= args.Length) continue;
            arguments.Add(args[++index]);
        }

        var page = new AdminPageConfig(
            Password: password,
            Port: port,
            BindSpec: bind,
            CheckpointMinutes: checkpoint,
            SavesDirectory: saves,
            DataDirectory: data,
            SteamcmdPath: Value("steamcmd") ?? SteamCmd.DefaultExecutable,
            SteamcmdRoot: Value("steam-root") ?? Path.Combine(data, "steam"),
            GameDirectory: game,
            CohesionDlc: Flag(Value("cohesion-dlc"), true),
            SteamAccount: Value("steam-account"),
            BackupDirectory: backups,
            ModsConfigPath: config,
            AutoApplyMods: Flag(Value("auto-apply-mods"), true),
            ModsDirectory: mods,
            SteamProxy: Value("steam-proxy"));

        return new PortalOptions
        {
            Page = page,
            DataDirectory = data,
            GameDirectory = game,
            GameExecutable = exe,
            GameArguments = arguments,
            Supervise = !Mentions(args, "--no-supervise") && Flag(Value("supervise"), true),
            AutoStart = !Mentions(args, "--no-auto-start") && Flag(Value("auto-start"), true),
            InitialSave = Value("save"),
            SteamAccount = Value("steam-account")
        };
    }

    /// <summary>
    /// The admin port: <c>--port</c>, else <c>TURBOTICK_ADMIN_PORT</c>, else 30573.
    ///
    /// <c>TURBOTICK_PORT</c> is deliberately not consulted. It names the *game's* wire port in every
    /// other part of this deployment, and reading it here silently put the portal on 30572 - the port
    /// the supervised child is about to bind. Measured in the container: the page printed "端口 30572"
    /// and would have failed to listen as soon as the game came up.
    /// </summary>
    private static int AdminPort(string[] args, out string? problem)
    {
        problem = null;
        var text = Option(args, "port") ?? Env("ADMIN_PORT");
        if (text is null) return 30573;
        if (int.TryParse(text.Trim(), out var port) && port is >= 0 and <= 65535) return port;
        problem = "管理页端口不合法：" + text;
        return 30573;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable("TURBOTICK_" + name);

    /// <summary>
    /// The admin password, from a file when one is given.
    ///
    /// A file is the preferred form because a password in a process argument is visible to every other process in the
    /// container via <c>/proc</c>. The size ceiling is the same as the game's: a "password file" that is a mounted
    /// secret directory or a stray binary would otherwise be used verbatim as the password.
    /// </summary>
    private static string? ReadPassword(out string? problem)
    {
        problem = null;
        var path = Environment.GetEnvironmentVariable("TURBOTICK_ADMIN_PASSWORD_FILE");
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { problem = "密码文件不存在：" + path; return null; }
                if (info.Length is 0 or > 16 * 1024) { problem = "密码文件大小不合理：" + info.Length + " 字节"; return null; }
                var text = File.ReadAllText(path).Trim();
                if (text.Length is 0 or > 4096) { problem = "密码文件内容不是合理长度的密码"; return null; }
                return text;
            }
            catch (Exception error)
            {
                problem = "读取密码文件失败：" + error.GetType().Name + ": " + error.Message;
                return null;
            }
        }
        var direct = Environment.GetEnvironmentVariable("TURBOTICK_ADMIN_PASSWORD");
        return string.IsNullOrWhiteSpace(direct) ? null : direct;
    }

    private static bool Mentions(string[] args, string name)
    {
        foreach (var argument in args)
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The value of <c>--name value</c> or <c>--name=value</c>; null when the flag is absent.</summary>
    private static string? Option(string[] args, string name)
    {
        var flag = "--" + name;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
                return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[index + 1] : null;
            if (argument.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return argument[(flag.Length + 1)..];
        }
        return null;
    }

    private static int Int(string? text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    /// <summary>"off" is spelled several ways in the wild; anything unparseable keeps the caller's default.</summary>
    private static bool Flag(string? text, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return text.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" or "no" => false,
            "on" or "1" or "true" or "yes" => true,
            _ => fallback
        };
    }
}
