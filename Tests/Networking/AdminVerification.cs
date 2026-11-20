using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TurboTick.Networking;

internal static class AdminVerification
{
    internal static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        await check("admin uses independent password authentication without locking compatibility or claiming player roles", async () =>
        {
            var secret = Guid.NewGuid().ToString("N");
            await using var server = new DedicatedServer(new ServerOptions(Port: 0,
                DataDirectory: Path.Combine(root, "admin-auth"), EveryoneCanEdit: false, AdminPassword: secret));
            await server.StartAsync();
            await using var admin = new DedicatedAdminClient();
            var rejected = await Throws<NetworkException>(() => admin.ConnectAsync("127.0.0.1", server.Port, secret + "x"));
            Assert(rejected.Code == "AdminAuthenticationFailed" && !admin.IsConnected, "Wrong password authenticated.");
            await admin.ConnectAsync("127.0.0.1", server.Port, secret);
            var status = await admin.GetStatusAsync();
            Assert(admin.IsConnected && status == new ServerAdminStatus(false, 0, 0, null, null), "Admin changed an empty room.");
            Assert(server.LockedGameVersion is null && server.LockedModHash is null, "Admin acquired compatibility lock.");
            await using var player = new DedicatedClient();
            await player.ConnectAsync("127.0.0.1", server.Port, new ClientHello("Player", "1.7.4910", "old-hash"));
            Assert(player.CanEdit && player.IsCheckpointOwner && !player.IsHost && player.Players.Count == 1, "Admin occupied a player role.");
            var populated = await admin.GetStatusAsync();
            Assert(populated.PlayerCount == 1 && populated.ModHash == "old-hash", "Admin status did not report the room.");
        });
        await check("archive reset preserves the full old running room, evicts old players, and permits a new compatible world across restart", () => VerifyResetAsync(root));
        await check("an archive keeps only the snapshot store the room still references", () => VerifyArchiveSlimmingAsync(root));
        await check("a room persisted for one save does not answer a launch that asks for another", () => VerifyStartupSaveAsync(root));
        await check("forged and replayed admin proofs fail and management never accepts player identity as authentication", () => VerifyProofsAsync(root));
        await check("admin authentication attempts are bounded and abandoned challenges time out", () => VerifyLimitsAsync(root));
        await check("archive preparation failure and invalid compatibility leave the running room intact", () => VerifyArchiveFailureAsync(root));
        await check("admin handshake cancellation, timeout and disposal permit reuse without stale requests", () => VerifyCancellationAsync(root));
        await check("admin pending requests cancel and remain bound to their original connection during reconnect", () => VerifyPendingRequestsAsync(root));
        await check("admin disabled is a structured error", async () =>
        {
            await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "admin-disabled")));
            await server.StartAsync();
            await using var admin = new DedicatedAdminClient();
            var error = await Throws<NetworkException>(() => admin.ConnectAsync("127.0.0.1", server.Port, "unused"));
            Assert(error.Code == "AdminDisabled" && !admin.IsConnected && server.LockedModHash is null, "Disabled management accepted or changed room.");
        });
    }

    private static async Task VerifyProofsAsync(string root)
    {
        var secret = Guid.NewGuid().ToString("N");
        await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "admin-proofs"), AdminPassword: secret));
        await server.StartAsync();
        using (var raw = await OpenAsync(server.Port))
        {
            await WriteAsync(raw, new { type = "AdminResetRoom", requestId = 1, hello = new ClientHello("Pretender", "1.7.4910", "fake") });
            Assert((await ReadAsync(raw)).GetProperty("code").GetString() == "HelloRequired", "Unauthenticated reset reached management.");
        }
        using var first = await OpenAsync(server.Port);
        var initial = await ChallengeAsync(first);
        var nonce = initial.GetProperty("nonce").GetBytesFromBase64();
        var proof = Proof(secret, initial);
        await WriteAsync(first, new { type = "AdminProof", adminProof = new { nonce, proof } });
        Assert((await ReadAsync(first)).GetProperty("type").GetString() == "AdminWelcome", "Independent proof implementation did not authenticate.");
        using (var forged = await OpenAsync(server.Port))
        {
            var challenge = await ChallengeAsync(forged);
            await WriteAsync(forged, new { type = "AdminProof", adminProof = new { nonce = RandomNumberGenerator.GetBytes(32), proof = Proof(secret, challenge) } });
            Assert((await ReadAsync(forged)).GetProperty("code").GetString() == "AdminAuthenticationFailed", "Forged nonce authenticated.");
        }
        using (var replayed = await OpenAsync(server.Port))
        {
            var challenge = await ChallengeAsync(replayed);
            Assert(!challenge.GetProperty("nonce").GetBytesFromBase64().SequenceEqual(nonce), "Server reused a nonce.");
            await WriteAsync(replayed, new { type = "AdminProof", adminProof = new { nonce, proof } });
            Assert((await ReadAsync(replayed)).GetProperty("code").GetString() == "AdminAuthenticationFailed", "Captured proof replay authenticated.");
        }
        using (var replacedNonce = await OpenAsync(server.Port))
        {
            var challenge = await ChallengeAsync(replacedNonce);
            await WriteAsync(replacedNonce, new { type = "AdminProof", adminProof = new { nonce = challenge.GetProperty("nonce").GetBytesFromBase64(), proof } });
            Assert((await ReadAsync(replacedNonce)).GetProperty("code").GetString() == "AdminAuthenticationFailed", "Old HMAC authenticated with a fresh nonce.");
        }
        await WriteAsync(first, new { type = "AdminProof", requestId = 9, adminProof = new { nonce, proof } });
        Assert((await ReadAsync(first)).GetProperty("code").GetString() == "UnknownMessage", "A challenge was consumed more than once.");
        Assert(server.LockedModHash is null && server.LockedGameVersion is null && !server.GameRunning, "Proof attempts changed the compatibility lock.");
        await using var player = new DedicatedClient();
        await player.ConnectAsync("127.0.0.1", server.Port, new ClientHello("SteamID:76561190000000000", "1.7.4910", "proof-hash"));
        using var ordinary = await OpenAsync(server.Port);
        await WriteAsync(ordinary, new { type = "Hello", hello = new ClientHello("Another SteamID", "1.7.4910", "proof-hash") });
        await ReadAsync(ordinary);
        await WriteAsync(ordinary, new { type = "AdminResetRoom", requestId = 1, hello = new ClientHello("Reset", "1.7.4911", "other") });
        JsonElement rejection;
        do { rejection = await ReadAsync(ordinary); } while (rejection.GetProperty("type").GetString() != "Error");
        Assert(rejection.GetProperty("code").GetString() == "UnknownMessage" && server.LockedModHash == "proof-hash", "Player identity granted management.");
    }

    private static async Task VerifyLimitsAsync(string root)
    {
        await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "admin-limits"), AdminPassword: Guid.NewGuid().ToString("N")));
        await server.StartAsync();
        var pending = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var raw = await OpenAsync(server.Port);
                pending.Add(raw);
                await ChallengeAsync(raw);
            }
            using var excess = await OpenAsync(server.Port);
            await WriteAsync(excess, new { type = "AdminHello" });
            Assert((await ReadAsync(excess)).GetProperty("code").GetString() == "AdminRateLimited", "Concurrent authentication attempts are unbounded.");
            var timeout = await ReadAsync(pending[0], TimeSpan.FromSeconds(7));
            Assert(timeout.GetProperty("code").GetString() == "AdminAuthenticationTimedOut", "Abandoned challenge did not expire.");
        }
        finally { foreach (var raw in pending) raw.Dispose(); }
        Assert(server.LockedModHash is null, "Abandoned challenges acquired a compatibility lock.");
    }

    private static async Task VerifyArchiveFailureAsync(string root)
    {
        var directory = Path.Combine(root, "admin-failure");
        var secret = Guid.NewGuid().ToString("N");
        await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: directory, AdminPassword: secret));
        await server.StartAsync();
        var hello = new ClientHello("Host", "1.7.4910", "still-running");
        await using var host = new DedicatedClient();
        await host.ConnectAsync("127.0.0.1", server.Port, hello);
        await host.ClaimHostAsync();
        var bytes = "world must survive failed archival"u8.ToArray();
        await host.UploadWorldAsync(SnapshotBuilder.Describe(bytes), new MemoryStream(bytes));
        await host.StartGameAsync();
        await host.SetTimeSpeedAsync(0);
        var epoch = server.Epoch;
        var state = await File.ReadAllBytesAsync(Path.Combine(directory, "server-state.json"));
        await using var admin = new DedicatedAdminClient();
        await admin.ConnectAsync("127.0.0.1", server.Port, secret);
        NetworkException error;
        // The injection has to make the archival copy fail on every platform, and the two platforms enforce
        // different things. Windows honours FileShare.None, so an exclusively opened file blocks the copy.
        // Unix happily reads a file that another handle has open, so the same injection would let the
        // archive succeed and the assertions below would check nothing - there an unreadable file fails the
        // copy instead, and both surfaces arrive as ArchiveFailed.
        var blockingPath = Path.Combine(directory, "locked-persistent.dat");
        await File.WriteAllTextAsync(blockingPath, "a durable file held exclusively by another operation").ConfigureAwait(false);
        FileStream? exclusive = null;
        if (OperatingSystem.IsWindows())
            exclusive = new FileStream(blockingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        else
            File.SetUnixFileMode(blockingPath, UnixFileMode.None);
        try
        {
            error = await Throws<NetworkException>(() => admin.ResetRoomAsync(hello with { ModHash = "replacement" })).ConfigureAwait(false);
        }
        finally
        {
            exclusive?.Dispose();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(blockingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var archiveDir = Path.Combine(directory, "Archives");
        // A failed archival must not even create the archive directory, so absence is the expected state.
        var leftovers = Directory.Exists(archiveDir)
            ? Directory.EnumerateFileSystemEntries(archiveDir).Select(Path.GetFileName).ToArray()
            : [];
        Assert(leftovers.Length == 0, "Failed partial archive was exposed or retained staging data: " + string.Join(",", leftovers) + " | code=" + error.Code);
        Assert(error.Code == "ArchiveFailed" && admin.IsConnected && host.IsConnected && server.GameRunning && server.Epoch == epoch &&
            server.LockedModHash == hello.ModHash && server.WorldManifest?.Id == SnapshotBuilder.Describe(bytes).Id, "Failed archival damaged the old room.");
        Assert((await File.ReadAllBytesAsync(Path.Combine(directory, "server-state.json"))).SequenceEqual(state), "Failed archive modified the authoritative state.");
        await host.SendCommandAsync("draft", new byte[] { 1 });
        Assert((await host.GetWorldManifestAsync())?.Id == server.WorldManifest?.Id, "Old room stopped serving players after archival failure.");
        using var raw = await OpenAsync(server.Port);
        var challenge = await ChallengeAsync(raw);
        await WriteAsync(raw, new { type = "AdminProof", adminProof = new { nonce = challenge.GetProperty("nonce").GetBytesFromBase64(), proof = Proof(secret, challenge) } });
        await ReadAsync(raw);
        await WriteAsync(raw, new { type = "AdminResetRoom", requestId = 5, hello = hello with { GameVersion = "1.7" } });
        Assert((await ReadAsync(raw)).GetProperty("code").GetString() == "GameVersionMismatch", "Server reset bypassed Validation.Hello.");
        Assert((await admin.GetStatusAsync()).ModHash == hello.ModHash, "Invalid reset changed the room lock.");
    }

    private static async Task VerifyCancellationAsync(string root)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var admin = new DedicatedAdminClient();
        try
        {
            var accepted = listener.AcceptTcpClientAsync();
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                var connecting = admin.ConnectAsync("127.0.0.1", port, "cancel-test", cancel.Token);
                using var silent = await accepted;
                await Throws<OperationCanceledException>(() => connecting);
                Assert(!admin.IsConnected, "Canceled admin handshake remained connected.");
            }
            accepted = listener.AcceptTcpClientAsync();
            var timedOut = admin.ConnectAsync("127.0.0.1", port, "timeout-test");
            using (var silent = await accepted)
                Assert((await Throws<NetworkException>(() => timedOut)).Code == "HandshakeTimedOut", "Admin handshake had no bounded timeout.");
            await using var disposing = new DedicatedAdminClient();
            accepted = listener.AcceptTcpClientAsync();
            var unfinished = disposing.ConnectAsync("127.0.0.1", port, "dispose-test");
            using (var silent = await accepted)
            {
                await disposing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                await Throws<OperationCanceledException>(() => unfinished);
            }
        }
        finally { listener.Stop(); }
        var secret = Guid.NewGuid().ToString("N");
        await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "admin-cancel"), AdminPassword: secret));
        await server.StartAsync();
        await admin.ConnectAsync("127.0.0.1", server.Port, secret);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => admin.ResetRoomAsync(new ClientHello("Cancel", "1.7.4910", "new"), canceled.Token));
        Assert((await admin.GetStatusAsync()).ModHash is null && !Directory.Exists(Path.Combine(root, "admin-cancel", "Archives")), "Pre-canceled reset reached the server.");
    }

    private static async Task VerifyPendingRequestsAsync(string root)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = Task.Run(async () =>
        {
            using var raw = await listener.AcceptTcpClientAsync();
            await ReadAsync(raw);
            await WriteAsync(raw, new { type = "AdminChallenge", adminChallenge = new { salt = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(32), iterations = 100_000 } });
            await ReadAsync(raw);
            await WriteAsync(raw, new { type = "AdminWelcome" });
            var canceledRequest = await ReadAsync(raw);
            firstSeen.TrySetResult();
            var disconnectedRequest = await ReadAsync(raw);
            secondSeen.TrySetResult();
            await release.Task;
            try
            {
                foreach (var request in new[] { canceledRequest, disconnectedRequest })
                    await WriteAsync(raw, new { type = "AdminStatus", requestId = request.GetProperty("requestId").GetInt64(),
                        adminStatus = new ServerAdminStatus(true, 999, 99, "1.7.9999", "obsolete") });
            }
            catch (Exception ex) when (ex is IOException or SocketException) { }
        });
        await using var admin = new DedicatedAdminClient();
        try
        {
            await admin.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "pending-request-test");
            using var cancel = new CancellationTokenSource();
            var canceled = admin.GetStatusAsync(cancel.Token);
            await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => canceled);
            Assert(admin.IsConnected, "Canceling a read-only request closed the management session.");
            var pending = admin.GetStatusAsync();
            await secondSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secret = Guid.NewGuid().ToString("N");
            await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "admin-pending"), AdminPassword: secret));
            await server.StartAsync();
            await admin.ConnectAsync("127.0.0.1", server.Port, secret);
            await Throws<NetworkException>(() => pending);
            release.TrySetResult();
            await fake;
            Assert((await admin.GetStatusAsync()) == new ServerAdminStatus(false, 0, 0, null, null), "An old management reply affected the replacement connection.");
        }
        finally { release.TrySetResult(); listener.Stop(); await fake; }
    }

    private static byte[] Proof(string secret, JsonElement challenge)
    {
        var salt = challenge.GetProperty("salt").GetBytesFromBase64();
        var nonce = challenge.GetProperty("nonce").GetBytesFromBase64();
        var iterations = challenge.GetProperty("iterations").GetInt32();
        Assert(salt.Length == 32 && nonce.Length == 32 && iterations >= 100_000, "Weak challenge parameters.");
        var key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32);
        byte[] transcript = [.. "TurboTick.Admin.v4\0"u8, .. salt, .. nonce];
        try { return HMACSHA256.HashData(key, transcript); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static async Task<TcpClient> OpenAsync(int port)
    {
        var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        return raw;
    }
    private static async Task<JsonElement> ChallengeAsync(TcpClient raw)
    {
        await WriteAsync(raw, new { type = "AdminHello" });
        var challenge = await ReadAsync(raw);
        Assert(challenge.GetProperty("type").GetString() == "AdminChallenge", "Missing independent management challenge.");
        return challenge.GetProperty("adminChallenge");
    }
    private static async Task WriteAsync(TcpClient raw, object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
        if (!json.ContainsKey("protocolVersion")) json["protocolVersion"] = JsonSerializer.SerializeToElement(NetworkProtocol.Version);
        body = JsonSerializer.SerializeToUtf8Bytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await raw.GetStream().WriteAsync(header);
        await raw.GetStream().WriteAsync(body);
    }
    private static async Task<JsonElement> ReadAsync(TcpClient raw, TimeSpan? duration = null)
    {
        using var timeout = new CancellationTokenSource(duration ?? TimeSpan.FromSeconds(5));
        var header = new byte[4];
        await raw.GetStream().ReadExactlyAsync(header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert(length > 0 && length <= NetworkProtocol.MaximumFrameLength, "Invalid admin response frame.");
        var body = new byte[length];
        await raw.GetStream().ReadExactlyAsync(body, timeout.Token);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task VerifyResetAsync(string root)
    {
        var directory = Path.Combine(root, "admin-reset");
        var secret = Guid.NewGuid().ToString("N");
        var options = new ServerOptions(Port: 0, DataDirectory: directory, GameVersion: "1.7.4910", ModHash: "old-world-hash", AdminPassword: secret);
        var oldHello = new ClientHello("Old host", "1.7.4910", "old-world-hash");
        var nextHello = new ClientHello("New host", "1.7.4920", "new-world-hash");
        var oldBytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize + 11);
        var oldManifest = SnapshotBuilder.Describe(oldBytes);
        await using var server = new DedicatedServer(options);
        await server.StartAsync();
        await using var host = new DedicatedClient();
        await host.ConnectAsync("127.0.0.1", server.Port, oldHello);
        await host.ClaimHostAsync();
        await host.UploadWorldAsync(oldManifest, new MemoryStream(oldBytes));
        await host.StartGameAsync();
        await using var waiting = new DedicatedClient();
        await waiting.ConnectAsync("127.0.0.1", server.Port, oldHello with { PlayerName = "Waiting joiner" });
        for (var i = 0; i < 205; i++) await host.SendCommandAsync("draft", new byte[] { 1 });
        await host.GetWorldManifestAsync(); // ordered socket barrier after all commands
        // A tick at or below the last stamped command is not comparable: the origin already applied the action while
    // the other peers are still waiting for the agreed tick, so those ticks are skipped by design (see
    // DedicatedServer._committedThroughTick). Report above the stamp to keep this test about the room, not the gate.
    await host.ReportHashAsync(100, new string('a', 64));
        await host.GetWorldManifestAsync();
        Assert(server.HistoryCount == 205 && server.TrackedHashTicks == 1, "Old running room was not established.");
        await using var next = new DedicatedClient();
        var hashMismatch = await Throws<NetworkException>(() => next.ConnectAsync("127.0.0.1", server.Port, nextHello with { GameVersion = oldHello.GameVersion }));
        Assert(hashMismatch.Code == "ModHashMismatch", "New-hash player entered the actual running old world.");
        var mismatch = await Throws<NetworkException>(() => next.ConnectAsync("127.0.0.1", server.Port, nextHello));
        Assert(mismatch.Code == "GameVersionMismatch", "Incompatible player entered the running room.");
        await using var admin = new DedicatedAdminClient();
        await admin.ConnectAsync("127.0.0.1", server.Port, secret);
        var before = await admin.GetStatusAsync();
        Assert(before.GameRunning && before.PlayerCount == 2 && before.ModHash == oldHello.ModHash, "Admin handshake altered running compatibility.");
        var uploadBytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize + 13);
        await using var blocked = new PausedUpload(uploadBytes);
        var oldUpload = host.CheckpointWorldAsync(SnapshotBuilder.Describe(uploadBytes), blocked, 0);
        await blocked.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(directory, "extra-persistent.dat"), "archive me");
        Directory.CreateDirectory(Path.Combine(directory, "Archives", "previous"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Archives", "previous", "sentinel"), "no recursive archive");
        var reset = await admin.ResetRoomAsync(nextHello);
        blocked.Release.TrySetResult();
        await Throws<NetworkException>(() => oldUpload);
        await Until(() => !host.IsConnected && !waiting.IsConnected);
        Assert(admin.IsConnected && (await admin.GetStatusAsync()) == new ServerAdminStatus(false, 0, 0, nextHello.GameVersion, nextHello.ModHash), "Reset damaged management or retained room state.");
        Assert(server.WorldManifest is null && server.CheckpointManifest is null && server.CheckpointTick == 0 && server.Epoch == -1 &&
            server.HistoryCount == 0 && !server.HistoryTruncated && server.TrackedHashTicks == 0, "Reset retained old simulation state.");
        var archive = Path.Combine(directory, "Archives", reset.BackupId);
        Assert(Path.GetFileName(reset.BackupId) == reset.BackupId && Directory.Exists(archive), "Reset returned an invalid archive id.");
        Assert(!Directory.Exists(Path.Combine(archive, "Archives")) && await File.ReadAllTextAsync(Path.Combine(archive, "extra-persistent.dat")) == "archive me", "Archive omitted persistent data or copied archives recursively.");
        var archivedState = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(archive, "server-state.json")));
        Assert(archivedState.GetProperty("gameVersion").GetString() == oldHello.GameVersion &&
            archivedState.GetProperty("modHash").GetString() == oldHello.ModHash && archivedState.GetProperty("session").GetProperty("epoch").GetInt64() >= 0 &&
            archivedState.GetProperty("history").GetArrayLength() == 205, "Archive did not capture complete running state.");
        var archivedStore = new SnapshotStore(Path.Combine(archive, "Snapshots"));
        Assert((await archivedStore.GetLatestAsync())?.Id == oldManifest.Id, "Archive snapshot pointer changed.");
        var archivedBytes = new MemoryStream();
        for (var i = 0; i < oldManifest.ChunkHashes.Count; i++) await archivedBytes.WriteAsync((await archivedStore.ReadChunkAsync(oldManifest.Id, i)).Data);
        Assert(archivedBytes.ToArray().SequenceEqual(oldBytes), "Archive snapshot bytes differ.");
        // Keep this process alive past the old joiner's ten-second fallback deadline. The new room
        // deliberately starts again at epoch zero, so a stale epoch check alone would be insufficient.
        await next.ConnectAsync("127.0.0.1", server.Port, nextHello);
        await next.ClaimHostAsync();
        var transient = "new room before the empty-room restart"u8.ToArray();
        await next.UploadWorldAsync(SnapshotBuilder.Describe(transient), new MemoryStream(transient));
        await next.StartGameAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(10_200));
        Assert(next.IsConnected && next.IsHost && next.Players.Count == 1 && server.HistoryCount == 0 && server.TrackedHashTicks == 0 &&
            server.WorldManifest?.Id == SnapshotBuilder.Describe(transient).Id && (await admin.GetStatusAsync()).ModHash == nextHello.ModHash,
            "Old pending work contaminated the new room after its original deadline.");
        var secondReset = await admin.ResetRoomAsync(nextHello);
        Assert(secondReset.BackupId != reset.BackupId && (await admin.GetStatusAsync()).PlayerCount == 0, "Repeated resets reused an archive or retained old roles.");
        await server.StopAsync();
        await using (var emptyRestart = new DedicatedServer(options))
        {
            await emptyRestart.StartAsync();
            Assert(!emptyRestart.GameRunning && emptyRestart.WorldManifest is null && emptyRestart.LockedModHash == nextHello.ModHash &&
                await emptyRestart.Snapshots.GetLatestAsync() is null, "Restart restored the archived world into the new room.");
            await next.ConnectAsync("127.0.0.1", emptyRestart.Port, nextHello);
            Assert(!next.IsHost && next.IsCheckpointOwner && next.Players.Count == 1 && await next.GetSnapshotManifestAsync() is null, "Old roles or snapshots survived reset.");
            await next.ClaimHostAsync();
            var newBytes = "a completely new colony"u8.ToArray();
            await next.UploadWorldAsync(SnapshotBuilder.Describe(newBytes), new MemoryStream(newBytes));
            await next.StartGameAsync();
            Assert(emptyRestart.GameRunning && emptyRestart.WorldManifest?.Id == SnapshotBuilder.Describe(newBytes).Id, "New host could not publish a fresh world.");
        }
        await using var runningRestart = new DedicatedServer(options);
        await runningRestart.StartAsync();
        Assert(runningRestart.GameRunning && runningRestart.LockedModHash == nextHello.ModHash && runningRestart.HistoryCount == 0 &&
            runningRestart.WorldManifest?.Id == SnapshotBuilder.Describe("a completely new colony"u8).Id, "Restart lost the new room or restored old work.");
    }

    /// <summary>
    /// A swap restarts the process with a different save name while the room on disk still describes the world that
    /// was running before. Restored naively, that session answers "the world is already up", the load funnel stands
    /// down and the requested save never opens: measured on the deployment, asking for 84a0f22c ran 8ee6381e.
    /// </summary>
    /// <summary>
    /// Every reset and every automatic pack-change archival allocates a fresh snapshot store id and abandons the
    /// previous one, but only the newest is ever read again. Copying all of them made an archive 5.1 GB while the
    /// room it described was 148 MB, and 16 such archives reached 25 GB on a 469 GB disk. This pins the two rules that
    /// stop it: the archive holds exactly the store the state file names, and old archives are pruned to a bound.
    /// </summary>
    private static async Task VerifyArchiveSlimmingAsync(string root)
    {
        var secret = Guid.NewGuid().ToString("N");
        var hello = new ClientHello("Host", "1.7.4910", "slim-hash");
        var bytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize + 11);
        var world = SnapshotBuilder.Describe(bytes);
        var directory = Path.Combine(root, "admin-archive-slim");
        Directory.CreateDirectory(Path.Combine(directory, "RoomSnapshots"));
        await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: directory,
            GameVersion: hello.GameVersion, ModHash: hello.ModHash, AdminPassword: secret));
        await server.StartAsync();
        await using var admin = new DedicatedAdminClient();
        await admin.ConnectAsync("127.0.0.1", server.Port, secret);

        // A room only gets a store id once it has been reset, which is also the only time an archive is made in a
        // long-lived deployment. Do the first reset so the second one has something to be slim about.
        async Task PlayAsync()
        {
            await using var host = new DedicatedClient();
            await host.ConnectAsync("127.0.0.1", server.Port, hello);
            await host.ClaimHostAsync();
            await host.UploadWorldAsync(world, new MemoryStream(bytes));
            await host.StartGameAsync();
        }

        await PlayAsync();
        var first = await admin.ResetRoomAsync(hello);
        Assert(Directory.Exists(Path.Combine(directory, "Archives", first.BackupId)), "First reset made no archive.");
        await PlayAsync();
        // The state file is the authority for which store is live: that is the thing the archival path reads.
        var liveState = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(directory, "server-state.json")));
        var liveId = liveState.GetProperty("snapshotStoreId").GetString()
            ?? throw new Exception("Reset did not persist a snapshot store id.");
        // Debris from the room the first reset abandoned: real bytes under a directory nothing references.
        var debris = Path.Combine(directory, "RoomSnapshots", new string('a', 32));
        Directory.CreateDirectory(debris);
        await File.WriteAllBytesAsync(Path.Combine(debris, new string('b', 64) + ".blob"), new byte[4096]);

        var second = await admin.ResetRoomAsync(hello);
        var archive = Path.Combine(directory, "Archives", second.BackupId);
        var copies = Directory.EnumerateDirectories(Path.Combine(archive, "RoomSnapshots")).Select(Path.GetFileName).ToArray();
        Assert(copies.Length == 1 && copies[0] == liveId,
            "Archive copied snapshot stores the room no longer references: " + string.Join(",", copies));
        var archivedStore = new SnapshotStore(Path.Combine(archive, "RoomSnapshots", liveId));
        Assert((await archivedStore.GetLatestAsync())?.Id == world.Id, "Archived live store lost the world.");

        // Pruning: five survive, the oldest goes. Names come from the generator, so they sort chronologically.
        var archiveRoot = Path.Combine(directory, "Archives");
        foreach (var stamp in new[] { "20200101T0000000000000Z-", "20200102T0000000000000Z-", "20200103T0000000000000Z-",
                     "20200104T0000000000000Z-", "20200105T0000000000000Z-", "20200106T0000000000000Z-" })
        {
            var path = Path.Combine(archiveRoot, stamp + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "archived.json"), stamp);
        }
        // A hand-made directory must not be pruned: it is not named like something we generate.
        var manual = Path.Combine(archiveRoot, "my-own-backup");
        Directory.CreateDirectory(manual);
        await File.WriteAllTextAsync(Path.Combine(manual, "notes.txt"), "keep me");

        // The room must hold a world for a pack change to be worth archiving: a room with no world simply adopts the
        // new fingerprint (nothing is lost), which is the behaviour the empty-room case above already pinned.
        await PlayAsync();
        await server.StopAsync();
        // Re-read: the second reset gave the room a fresh store, so the id captured before it is already retired.
        var beforeStartup = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Combine(directory, "server-state.json")));
        var liveAtStartup = beforeStartup.GetProperty("snapshotStoreId").GetString()
            ?? throw new Exception("Reset did not persist a snapshot store id.");

        await using (var restart = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: directory,
            GameVersion: hello.GameVersion, ModHash: "changed-hash", AdminPassword: secret) { RequireSimulation = true }))
            await restart.StartAsync();   // the pack changed, so startup archives and prunes once

        var remaining = Directory.EnumerateDirectories(archiveRoot).Select(Path.GetFileName).ToArray();
        Assert(remaining.Contains("my-own-backup"), "Pruning deleted a directory it did not generate.");
        var generated = remaining.Where(name => name != "my-own-backup").ToArray();
        Assert(generated.Length == 5, "Pruning kept " + generated.Length + " archives instead of 5: " + string.Join(",", generated));
        // Match by prefix: a generated name is "<stamp>-<guid>", so an equality test could never hit.
        Assert(!generated.Any(name => name!.StartsWith("20200101T0000000000000Z-", StringComparison.Ordinal)),
            "Pruning kept the oldest archive: " + string.Join(",", generated));
        Assert(generated.Any(name => name!.StartsWith("20200106T0000000000000Z-", StringComparison.Ordinal)),
            "Pruning deleted the newest archive: " + string.Join(",", generated));

        // The startup archive must carry the world it is protecting. It is made *before* the state is adopted, so
        // anything that reads the live-store field at that moment reads null and copies no store at all.
        var startupArchive = Directory.EnumerateDirectories(archiveRoot)
            .Where(path => LooksGenerated(Path.GetFileName(path)!))
            .OrderByDescending(path => Path.GetFileName(path)!, StringComparer.Ordinal)
            .First();
        var startupStores = Directory.Exists(Path.Combine(startupArchive, "RoomSnapshots"))
            ? Directory.EnumerateDirectories(Path.Combine(startupArchive, "RoomSnapshots")).Select(Path.GetFileName).ToArray()
            : [];
        Assert(startupStores.Length == 1 && startupStores[0] == liveAtStartup,
            "The startup archive kept no snapshot store for the world it protects: " + string.Join(",", startupStores)
            + "（期望 " + liveAtStartup + "）");
        Assert((await new SnapshotStore(Path.Combine(startupArchive, "RoomSnapshots", liveAtStartup)).GetLatestAsync())?.Id == world.Id,
            "The startup archive's store lost the world.");

        // The archived store was the last copy in the room; the process minted a fresh one, so the old one is now
        // referenced by nothing. Left behind it accumulates one dead store per deploy — that is how RoomSnapshots
        // reached 6.6 GB beside a 74 MB live store.
        var storesLeft = Directory.EnumerateDirectories(Path.Combine(directory, "RoomSnapshots")).Select(Path.GetFileName).ToArray();
        Assert(storesLeft.Length == 1 && storesLeft[0] != liveAtStartup,
            "Unreferenced snapshot stores were left in the room: " + string.Join(",", storesLeft));
    }

    /// <summary>Same naming rule the server prunes by, so a test can tell its own directories from hand-made ones.</summary>
    private static bool LooksGenerated(string name) =>
        name.Length == 56 && name[8] == 'T' && name[22] == 'Z' && name[23] == '-' && name[24..].All(char.IsAsciiHexDigitLower);

    private static async Task VerifyStartupSaveAsync(string root)
    {
        var secret = Guid.NewGuid().ToString("N");
        var hello = new ClientHello("Host", "1.7.4910", "startup-hash");
        var bytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize + 7);
        var manifest = SnapshotBuilder.Describe(bytes);
        var directory = Path.Combine(root, "admin-startup-save");
        var runningBase = new ServerOptions(Port: 0, DataDirectory: directory, GameVersion: hello.GameVersion,
            ModHash: hello.ModHash, AdminPassword: secret) { StartupSave = "save-a" };
        await using (var server = new DedicatedServer(runningBase))
        {
            await server.StartAsync();
            await using var host = new DedicatedClient();
            await host.ConnectAsync("127.0.0.1", server.Port, hello);
            await host.ClaimHostAsync();
            await host.UploadWorldAsync(manifest, new MemoryStream(bytes));
            await host.StartGameAsync();
            Assert(server.GameRunning && server.WorldManifest?.Id == manifest.Id, "The room never started running.");
        }
        // Relaunching into the same save is a resume, not a swap: the funnel must stay down and keep the world.
        await using (var resume = new DedicatedServer(runningBase))
        {
            await resume.StartAsync();
            Assert(resume.GameRunning && resume.WorldManifest?.Id == manifest.Id && resume.Session?.Tick >= 0,
                "Restarting into the room's own save lost it.");
        }
        // Asking for a different save drops the session so the load funnel can take the requested file. The snapshot
        // stays on disk: it is the only copy of the checkpoint the operator may still want.
        var secondBytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize + 5);
        var secondManifest = SnapshotBuilder.Describe(secondBytes);
        await using (var swapped = new DedicatedServer(runningBase with { StartupSave = "save-b" }))
        {
            await swapped.StartAsync();
            Assert(!swapped.GameRunning && swapped.WorldManifest is null && swapped.Session is null,
                "A launch into a different save restored the previous world instead of waiting for the requested one.");
            Assert(await swapped.Snapshots.GetLatestAsync() is not null, "Dropping the session also dropped the checkpoint.");
            // The funnel then loads save-b and publishes it, exactly like the resident does after a swap.
            await using var host = new DedicatedClient();
            await host.ConnectAsync("127.0.0.1", swapped.Port, hello);
            await host.ClaimHostAsync();
            await host.UploadWorldAsync(secondManifest, new MemoryStream(secondBytes));
            await host.StartGameAsync();
            Assert(swapped.GameRunning && swapped.WorldManifest?.Id == secondManifest.Id, "The swapped-in world never ran.");
        }
        // The room now belongs to save-b: relaunching into it resumes the new world, and relaunching into save-a is a
        // fresh swap that must drop it rather than quietly run the world the operator left.
        await using (var settled = new DedicatedServer(runningBase with { StartupSave = "save-b" }))
        {
            await settled.StartAsync();
            Assert(settled.GameRunning && settled.WorldManifest?.Id == secondManifest.Id, "The swapped-in world did not survive a restart.");
        }
        await using (var back = new DedicatedServer(runningBase))
        {
            await back.StartAsync();
            Assert(!back.GameRunning && back.WorldManifest is null, "A later launch resurrected the world the operator swapped away from.");
        }
        // A launch that names no save (lobby, or state written before this field existed) keeps the old behaviour.
        var lobbyDirectory = Path.Combine(root, "admin-startup-save-lobby");
        await using (var first = new DedicatedServer(runningBase with { DataDirectory = lobbyDirectory }))
        {
            await first.StartAsync();
            await using var host = new DedicatedClient();
            await host.ConnectAsync("127.0.0.1", first.Port, hello);
            await host.ClaimHostAsync();
            await host.UploadWorldAsync(manifest, new MemoryStream(bytes));
            await host.StartGameAsync();
            Assert(first.GameRunning, "The lobby room never started running.");
        }
        await using (var unnamed = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: lobbyDirectory,
            GameVersion: hello.GameVersion, ModHash: hello.ModHash, AdminPassword: secret)))
        {
            await unnamed.StartAsync();
            Assert(unnamed.GameRunning && unnamed.WorldManifest?.Id == manifest.Id,
                "A launch without a save name dropped a resumable room.");
        }
    }

    private sealed class PausedUpload(byte[] bytes) : MemoryStream(bytes)
    {
        internal readonly TaskCompletionSource SecondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) { SecondRead.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

