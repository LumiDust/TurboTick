using System.Globalization;
using TurboTick.Networking;

return await RunAsync(args);

// A coordinator: it orders commands, paces the timeline and stores snapshots, but runs no colony of
// its own. Every joined player simulates the same world from the same snapshot and command log, which is
// the arrangement that gives working animation and pathfinding for free. A resident simulator would add
// one more copy of the colony to keep in sync - N players become N+1 - and buy nothing back.
//
// Because it never loads a colony it does not reference the game assemblies at all, so it runs on a host
// with no RimWorld installation.
static async Task<int> RunAsync(string[] arguments)
{
    var options = new ServerOptions
    {
        // Every peer simulates; the server paces from the slowest report.
        RequireSimulation = false,
        Authoritative = false,
        Lockstep = true
    };
    var hostEnv = Environment.GetEnvironmentVariable("TURBOTICK_HOST");
    var portEnv = Environment.GetEnvironmentVariable("TURBOTICK_PORT");
    var dataEnv = Environment.GetEnvironmentVariable("TURBOTICK_DATA");
    var gameVerEnv = Environment.GetEnvironmentVariable("TURBOTICK_GAME_VERSION");
    var modHashEnv = Environment.GetEnvironmentVariable("TURBOTICK_MOD_HASH");
    var ctrlOnlyEnv = Environment.GetEnvironmentVariable("TURBOTICK_CONTROLLER_ONLY");

    if (!string.IsNullOrWhiteSpace(hostEnv)) options = options with { Address = hostEnv };
    if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)) options = options with { Port = parsedPort };
    if (!string.IsNullOrWhiteSpace(dataEnv)) options = options with { DataDirectory = dataEnv };
    if (!string.IsNullOrWhiteSpace(gameVerEnv)) options = options with { GameVersion = gameVerEnv };
    if (!string.IsNullOrWhiteSpace(modHashEnv)) options = options with { ModHash = modHashEnv };
    if (string.Equals(ctrlOnlyEnv, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(ctrlOnlyEnv, "1")) options = options with { EveryoneCanEdit = false };

    string? importPath = null;
    try
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "--help" or "-h")
            {
                Console.WriteLine("TurboTick coordinator server (.NET 10; no game installation or colony simulation)");
                Console.WriteLine("--host ADDRESS       Bind address (default 127.0.0.1)");
                Console.WriteLine("--port PORT          TCP port (default 30572; 0 selects a free port)");
                Console.WriteLine("--data DIRECTORY     Persistent state and snapshots (default Saved)");
                Console.WriteLine("--game-version VER   Initial exact version, or 1.7 locks to first complete 1.7.x peer");
                Console.WriteLine("--mod-hash HASH      Initial exact simulation hash; development locks to first peer");
                Console.WriteLine("Persisted room compatibility locks take precedence over these initial values.");
                Console.WriteLine("--controller-only    One server-assigned controller; transfers to the host when claimed");
                Console.WriteLine("--import FILE        Import a local snapshot before accepting players");
                Console.WriteLine("Management (same TCP port; independent password authentication):");
                Console.WriteLine("TURBOTICK_ADMIN_PASSWORD_FILE  Password file, preferred over the password environment variable");
                Console.WriteLine("TURBOTICK_ADMIN_PASSWORD       Password; unset/empty disables management");
                Console.WriteLine("Password files remove terminal CR/LF only; unreadable/empty files fail startup.");
                return 0;
            }
            if (argument == "--controller-only") { options = options with { EveryoneCanEdit = false }; continue; }
            if (++index >= arguments.Length) throw new ArgumentException($"Missing value for {argument}.");
            var value = arguments[index];
            switch (argument)
            {
                case "--host": options = options with { Address = value }; break;
                case "--port": options = options with { Port = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) }; break;
                case "--data": options = options with { DataDirectory = value }; break;
                case "--game-version": options = options with { GameVersion = value }; break;
                case "--mod-hash": options = options with { ModHash = value }; break;
                case "--import":
                case "--import-snapshot": importPath = value; break;
                default: throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        var passwordFile = Environment.GetEnvironmentVariable("TURBOTICK_ADMIN_PASSWORD_FILE");
        string? adminPassword;
        if (passwordFile is not null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(passwordFile) || new FileInfo(passwordFile).Length > 16 * 1024)
                    throw new IOException("Invalid management password file.");
                adminPassword = (await File.ReadAllTextAsync(passwordFile)).TrimEnd('\r', '\n');
                if (string.IsNullOrEmpty(adminPassword) || adminPassword.Length > 4096)
                    throw new IOException("Invalid management password file.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"Management password file could not be read or was invalid ({ex.GetType().Name}); startup stopped.");
                return 2;
            }
        }
        else adminPassword = Environment.GetEnvironmentVariable("TURBOTICK_ADMIN_PASSWORD");
        options = options with { AdminPassword = adminPassword };

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var server = new DedicatedServer(options);
            if (importPath is not null)
            {
                await using var source = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var manifest = await server.Snapshots.PutAsync(source, shutdown.Token);
                Console.WriteLine($"Imported snapshot {manifest.Id}: {manifest.Length} bytes, {manifest.ChunkHashes.Count} chunks.");
            }
            await server.StartAsync(shutdown.Token);
            Console.WriteLine($"TurboTick protocol {NetworkProtocol.Version}, client {NetworkProtocol.ClientVersion}; listening on {options.Address}:{server.Port}.");
            Console.WriteLine(options.EveryoneCanEdit ? "Policy editing: every joined peer." : "Policy editing: one server-assigned controller; transfers to a claimed host.");
            Console.WriteLine("Role: coordinator. The server holds no colony; every player simulates and the slowest report paces the timeline.");
            Console.WriteLine("Hosting: connect and claim the host role before publishing a colony; other players join the same dedicated server.");
            string? loggedVersion = null;
            string? loggedHash = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                var version = server.LockedGameVersion;
                var hash = server.LockedModHash;
                if (version != loggedVersion || hash != loggedHash)
                {
                    Console.WriteLine($"Compatibility lock: game={version ?? "waiting for first peer"}; modHash={hash ?? "waiting for first peer"}");
                    loggedVersion = version;
                    loggedHash = hash;
                }
            } while (await timer.WaitForNextTickAsync(shutdown.Token));
            await server.StopAsync();
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        finally { Console.CancelKeyPress -= cancel; }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
    {
        Console.Error.WriteLine($"Configuration error: {ex.Message}");
        return 2;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
    {
        // Neither the full command line nor file contents are logged.
        Console.Error.WriteLine($"Server startup or storage failed ({ex.GetType().Name}).");
        return 1;
    }
}
