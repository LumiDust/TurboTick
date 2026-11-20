using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TurboTick.Networking;

internal static class HeadlessVerification
{
    internal static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        await check("resident simulation admission uses a private key, complete compatibility and a unique hidden role", () => VerifyAdmissionAsync(root));
        await check("resident progress gates real TCP timelines, caps lead, pauses and survives external disconnects", () => VerifyProgressAsync(root));
        await check("resident startup restoration waits for matching fresh progress and rejects earlier epochs", () => VerifyRestoreAsync(root));
        await check("archive reset clears resident identity and progress so the runtime can reconnect", () => VerifyResetAsync(root));
        await check("joining a resident world rebases all replicas on one canonical checkpoint and retains the delta", () => VerifyCanonicalJoinAsync(root));
        await check("a room with no replica attached holds the timeline and resumes when one arrives", () => VerifyAudienceAsync(root));
    }

    private static ClientHello Hello(string name = "Player") => new(name, "1.7.4910", "headless-test-hash");
    private static ServerOptions Options(string root, string directory) => new(Port: 0,
        DataDirectory: Path.Combine(root, directory), EveryoneCanEdit: false) { RequireSimulation = true };

    private static async Task VerifyAdmissionAsync(string root)
    {
        var options = Options(root, "headless-admission") with { Address = "0.0.0.0", AdminPassword = "status-test" };
        await using var server = new DedicatedServer(options);
        await server.StartAsync();
        var supplied = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Assert(!Hello().ToString().Contains(supplied) && !(Hello() with { SimulationKey = supplied }).ToString().Contains(supplied),
            "ClientHello text exposed a simulation credential.");
        foreach (var key in new[] { "", "simulation", supplied })
        {
            var error = await RejectedHelloAsync(server.Port, Hello("Resident") with { SimulationKey = key });
            Assert(error == "SimulationAuthenticationFailed", "A forged simulation key was not rejected explicitly.");
        }
        Assert(server.LockedModHash is null, "A forged simulation locked room compatibility.");
        await using var player = new DedicatedClient();
        await player.ConnectAsync("127.0.0.1", server.Port, Hello("Resident"));
        Assert(player.CanEdit && !player.IsHost && !player.IsCheckpointOwner,
            "An ordinary no-key client acquired simulation duties or lost the ordinary controller role.");
        await using var simulation = new DedicatedClient();
        var mismatch = await Throws<NetworkException>(() => server.ConnectSimulationAsync(simulation, Hello() with { ModHash = "wrong" }));
        Assert(mismatch.Code == "ModHashMismatch", "Simulation skipped the mod compatibility check.");
        mismatch = await Throws<NetworkException>(() => server.ConnectSimulationAsync(simulation, Hello() with { GameVersion = "1.7.4911" }));
        Assert(mismatch.Code == "GameVersionMismatch", "Simulation skipped the game version check.");
        Assert(await RejectedHelloAsync(server.Port, Hello() with { ClientVersion = "other", SimulationKey = supplied }) == "ClientVersionMismatch",
            "Simulation admission skipped the client version check.");
        var original = Hello("Process simulation");
        await server.ConnectSimulationAsync(simulation, original);
        Assert(original.SimulationKey is null, "ConnectSimulationAsync modified its caller's Hello.");
        await Until(() => simulation.IsCheckpointOwner && simulation.Players.All(p => p.Id != simulation.PlayerId));
        Assert(!simulation.CanEdit && !simulation.IsHost && player.CanEdit && !player.IsCheckpointOwner,
            "Simulation consumed the player controller or host role.");
        Assert(player.Players.Count == 1 && player.Players.All(p => p.Id != simulation.PlayerId), "Simulation appeared in the player roster.");
        await using var duplicate = new DedicatedClient();
        var occupied = await Throws<NetworkException>(() => server.ConnectSimulationAsync(duplicate, Hello("Duplicate")));
        Assert(occupied.Code == "SimulationOccupied" && simulation.IsConnected, "Two simultaneous simulation peers were admitted.");
        await player.ClaimHostAsync();
        Assert(player.IsHost && !player.IsCheckpointOwner && simulation.IsCheckpointOwner, "ClaimHost moved resident checkpoint duties to a player.");
        Assert((await Throws<NetworkException>(() => simulation.ClaimHostAsync())).Code == "SimulationReadOnly", "Simulation acquired the host role.");
        Assert((await Throws<NetworkException>(() => simulation.SetTimeSpeedAsync(4))).Code == "ControllerOnly", "Simulation acquired time control.");
        var world = "resident role verification world"u8.ToArray();
        var manifest = SnapshotBuilder.Describe(world);
        Assert((await Throws<NetworkException>(() => simulation.UploadWorldAsync(manifest, new MemoryStream(world)))).Code == "HostOnly",
            "Simulation acquired initial-world publication rights.");
        await player.UploadWorldAsync(manifest, new MemoryStream(world));
        await player.StartGameAsync();
        Assert((await Throws<NetworkException>(() => player.CheckpointWorldAsync(manifest, new MemoryStream(world), 0))).Code == "NotCheckpointOwner",
            "An ordinary host acquired simulation checkpoint rights.");
        await simulation.SendCommandAsync("draft", [1]);
        await simulation.GetWorldManifestAsync(); // Ordered TCP barrier after the fire-and-forget command.
        Assert(server.HistoryCount == 0, "Simulation issued a player gameplay command.");
        await simulation.CheckpointWorldAsync(manifest, new MemoryStream(world), 0);
        await using var admin = new DedicatedAdminClient();
        await admin.ConnectAsync("127.0.0.1", server.Port, "status-test");
        Assert((await admin.GetStatusAsync()).PlayerCount == 1, "Management counted the hidden simulation as a player.");
    }

    private static async Task VerifyProgressAsync(string root)
    {
        await using var server = new DedicatedServer(Options(root, "headless-progress"));
        await server.StartAsync();
        await using var host = new DedicatedClient();
        await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
        await host.ClaimHostAsync();
        var world = "resident progress world"u8.ToArray();
        await host.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
        await host.StartGameAsync();
        var epoch = server.Epoch;
        await StableTick(server, 0, "A room without a resident advanced.");
        server.ReportSimulationProgress(epoch, 500, true);
        await StableTick(server, 0, "A progress report without a simulation socket advanced the timeline.");
        await using var simulation = new DedicatedClient();
        var caughtUp = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        simulation.WorldStarted += (session, _) => caughtUp.TrySetResult(session);
        await server.ConnectSimulationAsync(simulation, Hello("Simulation"));
        Assert((await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(2))).Epoch == epoch,
            "Simulation join waited for a checkpoint from itself.");
        await StableTick(server, 0, "Missing resident readiness advanced the timeline.");
        server.ReportSimulationProgress(epoch, 100, false);
        await StableTick(server, 0, "A not-ready simulation advanced the timeline.");
        server.ReportSimulationProgress(epoch - 1, 100, true);
        await StableTick(server, 0, "An earlier epoch's progress advanced the timeline.");
        server.ReportSimulationProgress(epoch, 0, true);
        await Until(() => server.Tick == 6);
        await StableTick(server, 6, "The timeline exceeded appliedTick + 6.");
        server.ReportSimulationProgress(epoch, 2, true);
        await Until(() => server.Tick == 8);
        await StableTick(server, 8, "A capped clock accumulated simulation debt.");
        server.ReportSimulationProgress(epoch, 2, false);
        await StableTick(server, 8, "Readiness loss left the tick gate open.");
        await host.SetTimeSpeedAsync(4);
        server.ReportSimulationProgress(epoch, 8, true);
        await Until(() => server.Tick == 14);
        await StableTick(server, 14, "A high-speed clock burst exceeded the six-tick resident lead.");
        server.ReportSimulationProgress(epoch, 9, true);
        await Until(() => server.Tick == 15);
        await StableTick(server, 15, "High-speed clamping retained excess tick debt.");
        await host.SetTimeSpeedAsync(0);
        var paused = server.Tick;
        server.ReportSimulationProgress(epoch, 500, true);
        await StableTick(server, paused, "Native pause advanced a ready resident.");
        await host.SetTimeSpeedAsync(1);
        await Until(() => server.Tick >= paused + 3);
        Assert(server.Tick < paused + 15, "Resume repaid blocked or paused wall-clock time as debt.");
        await using var successor = new DedicatedClient();
        await successor.ConnectAsync("127.0.0.1", server.Port, Hello("Successor"));
        await host.DisconnectAsync();
        await Until(() => successor.IsHost && successor.CanEdit);
        Assert(!simulation.IsHost && !simulation.CanEdit, "Host departure promoted the simulation to a player role.");
        var beforePlayersLeft = server.Tick;
        await successor.DisconnectAsync();
        await Until(() => server.Tick >= beforePlayersLeft + 6);
        Assert(simulation.IsConnected && !simulation.IsHost, "The last external disconnect stopped or promoted the resident.");
        await simulation.DisconnectAsync();
        await Task.Delay(80); // Let the actual TCP EOF reach the peer reader.
        var disconnected = server.Tick;
        server.ReportSimulationProgress(epoch, 1000, true);
        await StableTick(server, disconnected, "A disconnected resident left the tick gate open.");
        await using var remaining = new DedicatedClient();
        await remaining.ConnectAsync("127.0.0.1", server.Port, Hello("External"));
        Assert(!remaining.IsCheckpointOwner, "Resident disconnect handed checkpoints to an external client.");
        await remaining.ClaimHostAsync();
        Assert(!remaining.IsCheckpointOwner, "A new host claimed resident checkpoint duties after disconnect.");
        var again = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        simulation.WorldStarted += (session, _) => again.TrySetResult(session);
        await server.ConnectSimulationAsync(simulation, Hello("Simulation"));
        await again.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StableTick(server, disconnected, "Reconnecting inherited the disconnected simulation's readiness.");
        server.ReportSimulationProgress(epoch, disconnected, true);
        await Until(() => server.Tick == disconnected + 6);
    }

    private static async Task VerifyRestoreAsync(string root)
    {
        var options = Options(root, "headless-restore");
        int savedTick;
        long savedEpoch;
        var world = "restored simulation checkpoint"u8.ToArray();
        var manifest = SnapshotBuilder.Describe(world);
        await using (var server = new DedicatedServer(options))
        {
            await server.StartAsync();
            await using var simulation = new DedicatedClient();
            await server.ConnectSimulationAsync(simulation, Hello("Resident first"));
            await using var host = new DedicatedClient();
            await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
            Assert(host.CanEdit && !simulation.CanEdit, "The first simulation consumed the player controller slot.");
            await host.ClaimHostAsync();
            await host.UploadWorldAsync(manifest, new MemoryStream(world));
            var started = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            simulation.WorldStarted += (session, _) => started.TrySetResult(session);
            await host.StartGameAsync();
            Assert((await started.Task.WaitAsync(TimeSpan.FromSeconds(2))).Tick == 0, "A fresh world's Started frame skipped the resident.");
            savedEpoch = server.Epoch;
            server.ReportSimulationProgress(savedEpoch, 0, true);
            await Until(() => server.Tick == 6);
            await simulation.CheckpointWorldAsync(manifest, new MemoryStream(world), 4);
            await host.SendCommandAsync("draft", [7]);
            await host.GetWorldManifestAsync();
            await server.PersistAsync();
            savedTick = server.Tick;
            Assert(server.CheckpointTick == 4 && server.HistoryCount == 1, "Persist test did not establish checkpoint and command history.");
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.DataDirectory, "server-state.json")));
            Assert(persisted.RootElement.GetProperty("session").GetProperty("tick").GetInt32() == savedTick &&
                persisted.RootElement.GetProperty("checkpointTick").GetInt32() == 4 &&
                persisted.RootElement.GetProperty("history").GetArrayLength() == 1,
                "PersistAsync returned before writing the active session, checkpoint and history.");
        }
        await using var restored = new DedicatedServer(options);
        restored.ReportSimulationProgress(savedEpoch, 1000, true);
        await restored.StartAsync();
        Assert(restored.Epoch == savedEpoch && restored.Tick == savedTick && restored.CheckpointTick == 4 && restored.HistoryCount == 1,
            "Public persistence lost the lockstep session.");
        await StableTick(restored, savedTick, "Startup resumed without a resident.");
        await using var resident = new DedicatedClient();
        var catchUp = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replay = new TaskCompletionSource<GameCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        resident.WorldStarted += (session, _) => catchUp.TrySetResult(session);
        resident.CommandsReceived += commands => { if (commands.Count != 0) replay.TrySetResult(commands[0]); };
        await restored.ConnectSimulationAsync(resident, Hello("Restored resident"));
        var restoredSession = await catchUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(restoredSession.Tick == savedTick && restoredSession.SnapshotTick == 4 && restoredSession.Epoch == savedEpoch &&
            (await replay.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind == "draft", "Resident restoration omitted its immediate snapshot or command delta.");
        await StableTick(restored, savedTick, "Startup retained a previous process's readiness.");
        restored.ReportSimulationProgress(savedEpoch - 1, 1000, true);
        await StableTick(restored, savedTick, "Restored timeline accepted an older epoch sample.");
        restored.ReportSimulationProgress(savedEpoch, savedTick, true);
        await Until(() => restored.Tick == savedTick + 6);
        await using var hostAgain = new DedicatedClient();
        await hostAgain.ConnectAsync("127.0.0.1", restored.Port, Hello("New host"));
        await hostAgain.ClaimHostAsync();
        await hostAgain.StartGameAsync();
        Assert(restored.Epoch == savedEpoch + 1, "World restart did not advance epoch.");
        await StableTick(restored, 0, "World restart reused prior epoch readiness.");
        restored.ReportSimulationProgress(savedEpoch, 1000, true);
        await StableTick(restored, 0, "A late old-epoch frame reopened the gate.");
        restored.ReportSimulationProgress(restored.Epoch, 0, true);
        await Until(() => restored.Tick == 6);
    }

    private static async Task VerifyResetAsync(string root)
    {
        var options = Options(root, "headless-reset") with { AdminPassword = "headless-reset-password" };
        await using var server = new DedicatedServer(options);
        await server.StartAsync();
        await using var simulation = new DedicatedClient();
        await server.ConnectSimulationAsync(simulation, Hello("Old resident"));
        await using var host = new DedicatedClient();
        await host.ConnectAsync("127.0.0.1", server.Port, Hello("Old host"));
        await host.ClaimHostAsync();
        var world = "reset resident world"u8.ToArray();
        var manifest = SnapshotBuilder.Describe(world);
        await host.UploadWorldAsync(manifest, new MemoryStream(world));
        await host.StartGameAsync();
        server.ReportSimulationProgress(server.Epoch, 0, true);
        await Until(() => server.Tick == 6);
        await using var admin = new DedicatedAdminClient();
        await admin.ConnectAsync("127.0.0.1", server.Port, "headless-reset-password");
        var mismatch = await Throws<NetworkException>(() => admin.ResetRoomAsync(Hello() with { ModHash = "other-pack" }));
        Assert(mismatch.Code == "ResidentConfigurationMismatch" && simulation.IsConnected && server.GameRunning,
            "A reset changed the resident's installed modpack or disturbed its existing world.");
        using var directoryLock = new FileStream(Path.Combine(options.DataDirectory, "headless.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deploymentLock = new FileStream(Path.Combine(options.DataDirectory, "deployment.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var nextHello = Hello("New host");
        await admin.ResetRoomAsync(nextHello);
        await Until(() => !simulation.IsConnected && !host.IsConnected);
        Assert(!server.GameRunning && server.Epoch == -1 && server.Tick == 0, "Reset retained resident room state.");
        await server.ConnectSimulationAsync(simulation, nextHello with { PlayerName = "New resident" });
        await host.ConnectAsync("127.0.0.1", server.Port, nextHello);
        await host.ClaimHostAsync();
        Assert(host.CanEdit && !host.IsCheckpointOwner && simulation.IsCheckpointOwner && !simulation.IsHost,
            "Reset did not release and reassign the resident identity independently of player roles.");
        await host.UploadWorldAsync(manifest, new MemoryStream(world));
        await host.StartGameAsync();
        await StableTick(server, 0, "Reset carried old resident progress into the replacement world.");
        server.ReportSimulationProgress(server.Epoch, 0, true);
        await Until(() => server.Tick == 6);
        Assert((await admin.GetStatusAsync()).PlayerCount == 1, "Reset exposed the resident in the player count.");
    }

    private static async Task VerifyCanonicalJoinAsync(string root)
    {
        await using var server = new DedicatedServer(Options(root, "headless-canonical-join"));
        await server.StartAsync();
        await using var resident = new DedicatedClient();
        await server.ConnectSimulationAsync(resident, Hello("Resident"));
        await using var host = new DedicatedClient();
        await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
        await host.ClaimHostAsync();
        var first = "initial canonical world"u8.ToArray();
        await host.UploadWorldAsync(SnapshotBuilder.Describe(first), new MemoryStream(first));
        await host.StartGameAsync();
        server.ReportSimulationProgress(server.Epoch, 0, true);
        await Until(() => server.Tick == 6);
        await host.SendCommandAsync("draft", [4]);
        await host.GetWorldManifestAsync();
        var residentReload = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostReload = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinerReload = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new TaskCompletionSource<GameCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        resident.WorldStarted += (session, _) => residentReload.TrySetResult(session);
        host.WorldStarted += (session, _) => hostReload.TrySetResult(session);
        resident.CommandsReceived += items => { if (items.Count > 0) commands.TrySetResult(items[0]); };
        await using var joiner = new DedicatedClient();
        joiner.WorldStarted += (session, _) => joinerReload.TrySetResult(session);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resident.CheckpointRequested += () => requested.TrySetResult();
        await joiner.ConnectAsync("127.0.0.1", server.Port, Hello("Joiner"));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var checkpoint = "canonical join checkpoint"u8.ToArray();
        var manifest = SnapshotBuilder.Describe(checkpoint);
        await resident.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), 4);
        foreach (var task in new[] { residentReload.Task, hostReload.Task, joinerReload.Task })
        {
            var session = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(session.SnapshotTick == 4 && session.WorldId == manifest.Id && session.Epoch == server.Epoch,
                "A replica continued with a different live-world load point.");
        }
        Assert((await commands.Task.WaitAsync(TimeSpan.FromSeconds(2))).Tick == 8 && server.HistoryCount == 1,
            "Canonical reload lost the command delta after its checkpoint.");
        Assert(server.RequiredSimulationWorldId == manifest.Id, "Runtime was not told which snapshot must be loaded.");
        await StableTick(server, 6, "Canonical reload reused resident readiness before loading.");
        server.ReportSimulationProgress(server.Epoch, 6, true);
        await Until(() => server.Tick == 12);
        var reloadedAgain = false;
        resident.WorldStarted += (_, _) => reloadedAgain = true;
        await resident.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), 10);
        await Task.Delay(60);
        Assert(!reloadedAgain, "A periodic checkpoint unnecessarily reloaded the resident world.");
    }

    private static async Task<string?> RejectedHelloAsync(int port, ClientHello hello)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "Hello", protocolVersion = NetworkProtocol.Version, hello },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await socket.GetStream().WriteAsync(prefix);
        await socket.GetStream().WriteAsync(bytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await socket.GetStream().ReadExactlyAsync(prefix, deadline.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        Assert(length is > 0 and <= NetworkProtocol.MaximumFrameLength, "Invalid authentication response framing.");
        var response = new byte[length];
        await socket.GetStream().ReadExactlyAsync(response, deadline.Token);
        var error = JsonSerializer.Deserialize<JsonElement>(response);
        Assert(error.GetProperty("type").GetString() == "Error" && error.GetProperty("fatal").GetBoolean(), "Forged admission did not fail fatally.");
        return error.GetProperty("code").GetString();
    }

    /// <summary>A dedicated room must not invent a colony while nobody is watching it. The resident is a peer,
    /// not an audience: with `PauseWithoutReplicas` on, the timeline holds at whatever tick it reached and resumes
    /// at the operator's chosen speed the moment a display replica joins.</summary>
    private static async Task VerifyAudienceAsync(string root)
    {
        await using var server = new DedicatedServer(Options(root, "headless-audience") with { PauseWithoutReplicas = true });
        await server.StartAsync();
        await using var host = new DedicatedClient();
        await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
        await host.ClaimHostAsync();
        var world = "audience hold world"u8.ToArray();
        await host.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
        await host.StartGameAsync();
        var epoch = server.Epoch;
        await using var simulation = new DedicatedClient();
        var caughtUp = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        simulation.WorldStarted += (session, _) => caughtUp.TrySetResult(session);
        await server.ConnectSimulationAsync(simulation, Hello("Simulation"));
        await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(server.DisplayReplicaCount == 1 && !server.HeldForAudience, "A room with an attached replica claimed to be holding.");

        // The only audience leaves. The simulation peer keeps reporting progress, so a clock that still advanced
        // could only be doing it because it ignored the empty audience.
        await host.DisconnectAsync();
        await Until(() => server.DisplayReplicaCount == 0);
        var held = server.Tick;
        server.ReportSimulationProgress(epoch, held + 500, true);
        await StableTick(server, held, "An empty room advanced without an audience.");
        Assert(server.HeldForAudience, "An empty room did not report that it was holding for an audience.");
        Assert(server.CurrentPolicy.TimeSpeed == 1, "Holding for an audience rewrote the operator's speed policy.");

        await using var returning = new DedicatedClient();
        await returning.ConnectAsync("127.0.0.1", server.Port, Hello("Returning"));
        await Until(() => server.DisplayReplicaCount == 1);
        server.ReportSimulationProgress(epoch, held + 500, true);
        await Until(() => server.Tick > held + 3);
        Assert(!server.HeldForAudience, "A room with an audience still reported holding.");
        Assert(server.CurrentPolicy.TimeSpeed == 1, "Resuming after an audience arrived changed the speed policy.");
    }

    private static async Task StableTick(DedicatedServer server, int expected, string failure)
    {
        await Task.Delay(220);
        Assert(server.Tick == expected, failure + $" Expected {expected}, actual {server.Tick}.");
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
