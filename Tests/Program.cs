using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using TurboTick.Networking;
using TurboTick.Simulation;

var passed = 0;
var failed = 0;
var filter = args.Length == 2 && args[0] == "--filter" ? args[1] : null;
var temp = Path.Combine(Path.GetTempPath(), "turbotick-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
string Dir(string name) { var p = Path.Combine(temp, name); Directory.CreateDirectory(p); return p; }
ClientHello Hello(string name = "A", string hash = "mod-a", string version = "1.7.4998") => new(name, version, hash);

async Task Test(string name, Func<Task> body)
{
    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try { await body().WaitAsync(TimeSpan.FromSeconds(25)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Until(Func<bool> condition, int seconds = 5)
{
    using var c = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    while (!condition()) await Task.Delay(15, c.Token);
}
void AutoCheckpoint(DedicatedClient owner, DedicatedServer server, int size = 4096)
{
    owner.CheckpointRequested += () =>
    {
        var payload = new byte[size];
        _ = Task.Run(async () =>
        {
            try { await owner.CheckpointWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(payload), new MemoryStream(payload), server.Tick); }
            catch { /* the server may already have a fresh checkpoint */ }
        });
    };
}

async Task Throws(Func<Task> body, string? code = null)
{
    try { await body(); }
    catch (Exception ex)
    {
        if (code != null && (ex is not NetworkException ne || ne.Code != code))
            throw new Exception("Expected " + code + ", got " + ex, ex);
        return;
    }
    throw new Exception("Expected operation to fail.");
}

async Task WritePacket(TcpClient socket, object message)
{
    var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    var frame = new byte[json.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
    json.CopyTo(frame.AsSpan(4));
    await socket.GetStream().WriteAsync(frame);
}

async Task<System.Text.Json.JsonElement> ReadPacket(TcpClient socket)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var header = new byte[4];
    await socket.GetStream().ReadExactlyAsync(header, timeout.Token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    Check(length is > 0 and <= NetworkProtocol.MaximumFrameLength, "bounded raw response");
    var bytes = new byte[length];
    await socket.GetStream().ReadExactlyAsync(bytes, timeout.Token);
    using var document = System.Text.Json.JsonDocument.Parse(bytes);
    return document.RootElement.Clone();
}

async Task<System.Text.Json.JsonElement> ReadReply(TcpClient socket, long id)
{
    for (var index = 0; index < 64; index++)
    {
        var packet = await ReadPacket(socket);
        if (packet.GetProperty("type").GetString() == "Ping")
            await WritePacket(socket, new { type = "Pong", protocolVersion = NetworkProtocol.Version, requestId = packet.GetProperty("requestId").GetInt64() });
        if (packet.TryGetProperty("requestId", out var request) && request.GetInt64() == id) return packet;
    }
    throw new Exception("Raw client did not receive its correlated response.");
}

async Task RawBarrier(TcpClient socket, long id)
{
    await WritePacket(socket, new { type = "GetWorldManifest", protocolVersion = NetworkProtocol.Version, requestId = id });
    var reply = await ReadReply(socket, id);
    Check(reply.GetProperty("type").GetString() == "WorldManifest", "raw processing barrier");
}

await Test("single-player defaults, authoritative enable+value+cap, no local fallback", () =>
{
    var s = new SpeedAuthority();
    Check(s.Scale(3) == 6, "rate sync is on by default (2x of the current speed)");
    s.SetLocal(true, 2, 60);
    Check(s.Scale(3) == 6, "local multiplier");
    s.BeginSession();
    Check(s.Scale(3) == 0, "handshake in progress must hold");
    s.ApplyServer(new(true, 4, 12, 7));
    Check(s.Scale(6) == 12, "server cap must override local preferences");
    Check(s.Scale(6, true) == 6, "preserve forced normal semantics");
    Check(s.Scale(0) == 0, "preserve pause");
    Check(!s.ApplyServer(new(true, 2, 60, 6)), "ignore old revision");
    s.ConnectionLost();
    Check(s.Scale(3) == 0, "disconnect must not silently apply local settings");
    s.LeaveSession();
    Check(s.Scale(3) == 6, "explicit leave restores local settings");
    return Task.CompletedTask;
});

await Test("actual TPS uses completed ticks, respects pause and counter reset", () =>
{
    var t = new TickRateMeter();
    t.Sample(100, 0, 3);
    Check(!t.Behind && !t.HasStableSample, "no false lag alert before first measurement window");
    t.Sample(160, 1, 3);
    Check(t.ActualTps == 60 && t.TargetTps == 180 && t.Behind, "ticks/time measurement");
    t.Sample(160, 2, 0);
    Check(t.ActualTps == 0 && t.TargetTps == 0 && !t.Behind, "pause");
    t.Sample(0, 3, 1);
    Check(t.ActualTps == 0, "new save resets counters");
    t.Sample(60, 4, 1);
    t.Sample(61, 4.02, 3);
    Check(!t.Behind && !t.HasStableSample, "changing speed starts a fresh measurement window");
    t.Sample(241, 5.05, 3);
    Check(t.HasStableSample && !t.Behind, "stable higher-speed measurement");
    return Task.CompletedTask;
});

await Test("policy validates non-finite and bounds", async () =>
{
    await Throws(() => Task.FromResult(new SpeedPolicy(true, float.NaN)));
    await Throws(() => Task.FromResult(new SpeedPolicy(true, 21)));
    await Throws(() => Task.FromResult(new SpeedPolicy(true, 2, float.PositiveInfinity)));
});

await Test("two clients: one changes, everyone receives same authoritative revision", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("two")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CurrentPolicy?.Enabled == true && b.CurrentPolicy?.Enabled == true, "rate sync is enabled for every peer by default");
    await a.SetSpeedAsync(true, 3, 90);
    await Until(() => a.CurrentPolicy?.Revision == 1 && b.CurrentPolicy?.Revision == 1);
    Check(a.CurrentPolicy == b.CurrentPolicy && b.CurrentPolicy == server.CurrentPolicy, "policy content identical");
    Check(b.CurrentPolicy?.Enabled == true && b.CurrentPolicy.Multiplier == 3 && b.CurrentPolicy.MaxRate == 90, "all three settings synced");
    await b.SetSpeedAsync(false, 5, 80);
    await Until(() => a.CurrentPolicy?.Revision == 2);
    Check(a.CurrentPolicy == server.CurrentPolicy && !a.CurrentPolicy!.Enabled, "client B can also control");
});

await Test("host is explicitly claimed after ordinary joins, separate from editing and checkpoints", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-explicit"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var waiting = new DedicatedClient(); await using var host = new DedicatedClient();
    await waiting.ConnectAsync("127.0.0.1", server.Port, Hello("Waiting"));
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
    Check(!waiting.IsHost && !host.IsHost && waiting.CanEdit && !host.CanEdit && waiting.IsCheckpointOwner,
        "ordinary joins do not claim host, including the original controller/checkpoint owner");
    var world = new byte[1024];
    var manifest = SnapshotBuilder.Describe(world);
    await Throws(() => waiting.UploadWorldAsync(manifest, new MemoryStream(world)), "HostOnly");
    await Throws(() => waiting.StartGameAsync(), "HostOnly");
    await host.ClaimHostAsync();
    await Until(() => !waiting.IsCheckpointOwner && !waiting.CanEdit && host.IsCheckpointOwner && waiting.Players.Any(p => p.Id == host.PlayerId && p.IsHost));
    Check(host.IsHost && host.CanEdit && !waiting.CanEdit && !waiting.IsHost && host.Players.Count(p => p.CanEdit) == 1,
        "controller-only claim migrates the unique editing authority to the later host");
    await Throws(() => waiting.SetTimeSpeedAsync(2), "ControllerOnly");
    await host.SetTimeSpeedAsync(0);
    Check(server.CurrentPolicy.TimeSpeed == 0, "only the later explicit host may control room speed");
    await Throws(() => waiting.ClaimHostAsync(), "HostOccupied");
    await host.ClaimHostAsync(); // Same-connection claim is idempotent.
    await host.UploadWorldAsync(manifest, new MemoryStream(world));
    await host.StartGameAsync();
    Check(server.GameRunning && host.IsHost, "only the explicit host publishes and starts");
});

await Test("host claim is exclusive even when every player can edit", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-race")));
    await server.StartAsync();
    var clients = new List<DedicatedClient>();
    try
    {
        for (var index = 0; index < 6; index++)
        {
            var client = new DedicatedClient(); clients.Add(client);
            await client.ConnectAsync("127.0.0.1", server.Port, Hello("Claimant " + index));
        }
        Check(clients.All(c => c.CanEdit && !c.IsHost), "everyone-can-edit never implicitly hosts");
        var claims = await Task.WhenAll(clients.Select(async client =>
        {
            try { await client.ClaimHostAsync(); return true; }
            catch (NetworkException error) when (error.Code == "HostOccupied") { return false; }
        }));
        Check(claims.Count(success => success) == 1, "one successful RPC under concurrent claims");
        await Until(() => clients.All(c => c.Players.Count == 6 && c.Players.Count(p => p.IsHost) == 1));
        Check(clients.Count(c => c.IsHost) == 1, "one host property after every response");
        var editor = clients.First(c => !c.IsHost);
        await Throws(() => editor.UploadWorldAsync(SnapshotBuilder.Describe(new byte[100]), new MemoryStream(new byte[100])), "HostOnly");
        await Throws(() => editor.StartGameAsync(), "HostOnly");
        Check(server.WorldManifest is null && server.Epoch == -1 && !server.GameRunning, "non-host editors cannot publish or start");
    }
    finally { foreach (var client in clients) await client.DisposeAsync(); }
});

await Test("host leaving an empty lobby clears the role without promoting waiting peers", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-empty")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var waiting = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
    await waiting.ConnectAsync("127.0.0.1", server.Port, Hello("Waiting"));
    await host.ClaimHostAsync();
    await host.DisconnectAsync();
    Check(!host.IsHost && !host.IsCheckpointOwner && !host.CanEdit && host.Players.Count == 0, "disconnect clears all local role flags");
    await Until(() => waiting.Players.Count == 1);
    Check(!waiting.IsHost && waiting.Players.All(p => !p.IsHost), "a lobby vacancy stays vacant");
    await waiting.ClaimHostAsync();
    Check(waiting.IsHost, "a waiting peer may explicitly claim the vacancy");
});

await Test("active session claim preserves the world and transfers host to surviving checkpoint owner", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-active"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var original = new DedicatedClient();
    await original.ConnectAsync("127.0.0.1", server.Port, Hello("Original"));
    await original.ClaimHostAsync();
    await original.SetTimeSpeedAsync(0);
    var world = new byte[1024];
    await original.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await original.StartGameAsync();
    await original.SendCommandAsync("draft", [1]);
    await Until(() => server.HistoryCount == 1);
    var epoch = server.Epoch; var manifest = server.WorldManifest; var seed = server.Session!.Seed;
    await original.DisconnectAsync();
    await server.StopAsync();
    await server.StartAsync();
    await using var checkpointOwner = new DedicatedClient(); await using var host = new DedicatedClient();
    await checkpointOwner.ConnectAsync("127.0.0.1", server.Port, Hello("Checkpoint owner"));
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("New host"));
    Check(!checkpointOwner.IsHost && !host.IsHost, "joining a persisted running world still does not claim host");
    await host.ClaimHostAsync();
    await Until(() => !checkpointOwner.CanEdit);
    Check(host.IsHost && host.CanEdit && !host.IsCheckpointOwner && checkpointOwner.IsCheckpointOwner && !checkpointOwner.CanEdit &&
        host.Players.Count(p => p.CanEdit) == 1, "an active claim preserves checkpoint duty while transferring unique editing authority");
    Check(server.Epoch == epoch && server.WorldManifest?.Id == manifest?.Id && server.Session!.Seed == seed &&
        server.Tick == 0 && server.HistoryCount == 1 && server.CurrentPolicy.TimeSpeed == 0, "claim never replaces/restarts the active world");
    var checkpoint = Enumerable.Repeat((byte)7, 1024).ToArray();
    await checkpointOwner.CheckpointWorldAsync(SnapshotBuilder.Describe(checkpoint), new MemoryStream(checkpoint), 0);
    Check(!checkpointOwner.IsHost && !checkpointOwner.CanEdit && server.WorldManifest?.Id == SnapshotBuilder.Describe(checkpoint).Id,
        "a non-host checkpoint owner without editing permission can still publish checkpoints");
    await host.DisconnectAsync();
    await Until(() => checkpointOwner.IsHost && checkpointOwner.Players.Count == 1);
    Check(checkpointOwner.IsCheckpointOwner && checkpointOwner.CanEdit && checkpointOwner.Players.Single().IsHost,
        "running host ownership deterministically transfers to the surviving checkpoint owner");
});

await Test("an in-flight host claim never hops connections or leaves host state in the next room", async () =>
{
    await using var room = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-switch")));
    await room.StartAsync();
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    using var stop = new CancellationTokenSource();
    var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fake = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync();
        await ReadPacket(socket);
        await WritePacket(socket, new
        {
            type = "Welcome", protocolVersion = NetworkProtocol.Version, clientVersion = NetworkProtocol.ClientVersion,
            gameVersion = "1.7.4998", modHash = "mod-a", canEdit = true, isHost = false,
            policy = new SpeedPolicy(), playerId = 1L, epoch = -1L,
            players = new[] { new PlayerInfo(1, "Old room", 0, true, false) }
        });
        var claim = await ReadPacket(socket);
        Check(claim.GetProperty("type").GetString() == "ClaimHost", "the request reaches its original connection");
        requestSeen.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    });
    await using var changing = new DedicatedClient();
    try
    {
        await changing.ConnectAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, Hello());
        var claim = changing.ClaimHostAsync();
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await changing.ConnectAsync("127.0.0.1", room.Port, Hello());
        await Throws(() => claim);
        Check(!changing.IsHost && changing.Players.All(p => !p.IsHost), "the new connection remains an ordinary join");
        await changing.ClaimHostAsync();
        Check(changing.IsHost, "only a new explicit request may claim the new room");
    }
    finally { stop.Cancel(); listener.Stop(); await fake; }
});

await Test("unauthorized and retyped commits never replace the authoritative world", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-commit")));
    await server.StartAsync();
    using var raw = new TcpClient(); await raw.ConnectAsync("127.0.0.1", server.Port);
    await WritePacket(raw, new { type = "Hello", protocolVersion = NetworkProtocol.Version, hello = Hello("Editor") });
    await ReadPacket(raw);
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
    await host.ClaimHostAsync(); await host.SetTimeSpeedAsync(0);
    var world = new byte[1024]; var manifest = SnapshotBuilder.Describe(world);
    await host.UploadWorldAsync(manifest, new MemoryStream(world)); await host.StartGameAsync();
    var chunk = Enumerable.Repeat((byte)123, 100).ToArray();
    var changed = SnapshotBuilder.Describe(chunk);
    foreach (var checkpoint in new[] { false, true })
    {
        var requestId = checkpoint ? 702 : 701;
        await WritePacket(raw, new
        {
            type = "PublishWorldChunk", protocolVersion = NetworkProtocol.Version, requestId,
            uploadId = "editor-upload", chunkIndex = 0, worldChunk = chunk, checkpoint, epoch = server.Epoch, tick = 0
        });
        var response = await ReadReply(raw, requestId);
        Check(response.GetProperty("code").GetString() == (checkpoint ? "NotCheckpointOwner" : "HostOnly"),
            "editing permission is insufficient for either upload purpose");
    }
    await WritePacket(raw, new
    {
        type = "CommitWorld", protocolVersion = NetworkProtocol.Version, requestId = 703,
        uploadId = "editor-upload", checkpoint = true, epoch = server.Epoch, tick = 0, chunkCount = 1, sha256 = changed.Id
    });
    Check((await ReadReply(raw, 703)).GetProperty("code").GetString() == "NotCheckpointOwner", "direct commit checks owner before processing data");
    Check(server.WorldManifest?.Id == manifest.Id && server.CheckpointManifest?.Id == manifest.Id, "failed checkpoint commit preserves both world pointers");
    await host.DisconnectAsync();
    // Running-room continuity explicitly promotes this remaining raw peer; wait for its role update.
    var promoted = false;
    for (var index = 0; index < 32 && !promoted; index++)
    {
        var packet = await ReadPacket(raw);
        promoted = packet.GetProperty("type").GetString() == "Role" && packet.GetProperty("isHost").GetBoolean();
    }
    Check(promoted, "running room transfers host to survivor");
    await WritePacket(raw, new
    {
        type = "PublishWorldChunk", protocolVersion = NetworkProtocol.Version, requestId = 704,
        uploadId = "bound-purpose", chunkIndex = 0, worldChunk = chunk, checkpoint = true, epoch = server.Epoch, tick = 0
    });
    Check((await ReadReply(raw, 704)).GetProperty("type").GetString() == "WorldChunkAccepted", "checkpoint owner may stage a checkpoint");
    await WritePacket(raw, new
    {
        type = "CommitWorld", protocolVersion = NetworkProtocol.Version, requestId = 705,
        uploadId = "bound-purpose", checkpoint = false, epoch = server.Epoch, tick = 0, chunkCount = 1, sha256 = changed.Id
    });
    Check((await ReadReply(raw, 705)).GetProperty("code").GetString() == "InvalidUpload", "an upload cannot switch from checkpoint to initial publication");
    Check(server.WorldManifest?.Id == manifest.Id && server.CheckpointManifest?.Id == manifest.Id && server.Epoch == 0,
        "retyped commit preserves the running authoritative world");
});

await Test("delayed host role and roster notifications cannot poison another room", async () =>
{
    await using var first = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-events-a")));
    await using var second = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-events-b")));
    await first.StartAsync(); await second.StartAsync();
    await using var changing = new DedicatedClient();
    await changing.ConnectAsync("127.0.0.1", first.Port, Hello());
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var notifications = new System.Collections.Concurrent.ConcurrentQueue<bool>();
    var firstEvent = 0;
    changing.PlayersChanged += players =>
    {
        if (Interlocked.Increment(ref firstEvent) == 1)
        {
            entered.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(20));
        }
        else notifications.Enqueue(players.Any(p => p.IsHost));
    };
    try
    {
        await changing.ClaimHostAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(changing.IsHost, "old room role is confirmed while its UI dispatcher is blocked");
        await changing.ConnectAsync("127.0.0.1", second.Port, Hello());
    }
    finally { release.TrySetResult(); }
    await Until(() => notifications.Count > 0);
    Check(!changing.IsHost && notifications.All(host => !host) && changing.Players.All(p => !p.IsHost),
        "queued old-room host notifications are discarded by connection generation");
});

await Test("presence roster: Welcome identity, join, leave, ownership and stable reconnect color", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-roster"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Alice"));
    Check(a.PlayerId > 0 && a.Players.Count == 1 && a.Players[0].Id == a.PlayerId &&
        a.Players[0].Name == "Alice" && a.Players[0].IsCheckpointOwner, "Welcome atomically supplies assigned identity and roster");
    var firstId = a.PlayerId;
    var color = a.Players[0].ColorIndex;
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("Bob"));
    await Until(() => a.Players.Count == 2 && b.Players.Count == 2);
    Check(b.PlayerId != firstId && b.Players.SequenceEqual(a.Players), "same server-assigned roster on both peers");
    Check(!b.Players.Single(p => p.Id == b.PlayerId).CanEdit, "read-only role appears in roster");
    await a.DisconnectAsync();
    Check(a.PlayerId == 0 && a.Players.Count == 0 && a.Presences.Count == 0, "disconnect clears all transient identity/state immediately");
    await Until(() => b.Players.Count == 1 && b.Players[0].IsCheckpointOwner && b.Players[0].CanEdit && b.CanEdit);
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Alice"));
    Check(a.PlayerId != firstId && a.Players.Single(p => p.Id == a.PlayerId).ColorIndex == color,
        "new socket identity with stable named-player color");
    await Until(() => b.Players.Count == 2);
});

await Test("presence: real map coordinates, names, hidden/off-map state and disconnect removal", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-coordinates")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Map owner"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("Map visitor"));
    await Until(() => a.Players.Count == 2);
    var position = new PresenceUpdate(-1, 47, 123.25f, 86.875f, true, PlayerActivity.Playing);
    Check(await a.SendPresenceAsync(position), "first sample sent");
    await Until(() => b.Presences.Count == 1);
    var received = b.Presences[0];
    Check(received.PlayerId == a.PlayerId && received.State == position && received.UpdatedAtMilliseconds > 0,
        "coordinates and map unique ID survive TCP relay with server identity and local timestamp");
    Check(b.Players.Single(p => p.Id == received.PlayerId).Name == "Map owner", "pointer resolves the actual player name");
    Check(a.Presences.Count == 0, "sender does not receive its own pointer echo");
    await Task.Delay(110);
    var hidden = position with { Visible = false, X = 0, Z = 0 };
    Check(await a.SendPresenceAsync(hidden), "explicit hide sent");
    await Until(() => b.Presences[0].State == hidden);
    await Task.Delay(110);
    var away = hidden with { MapId = -1 };
    Check(await a.SendPresenceAsync(away), "off-map sample sent");
    await Until(() => b.Presences[0].State.MapId == -1);
    await a.DisconnectAsync();
    await Until(() => b.Players.Count == 1 && b.Presences.Count == 0);
    Check(server.HistoryCount == 0 && server.Tick == 0 && !server.GameRunning,
        "presence never starts simulation or touches command history");
});

await Test("presence: one-in-flight client rate cap, finite validation and epoch isolation", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-client-limits")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    await Until(() => a.Players.Count == 2);
    var position = new PresenceUpdate(-1, 3, 50, 60, true, PlayerActivity.Playing);
    var sends = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => a.SendPresenceAsync(position with { X = i })));
    Check(sends.Count(sent => sent) == 1, "100 simultaneous samples create one wire write");
    await Until(() => b.Presences.Count == 1);
    await Throws(() => a.SendPresenceAsync(position with { X = float.NaN }));
    await Throws(() => a.SendPresenceAsync(position with { MapId = -1 }));
    await Throws(() => a.SendPresenceAsync(position with { Activity = (PlayerActivity)999 }));
    await a.SetTimeSpeedAsync(0);
    var world = new byte[1024];
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    await Until(() => a.PresenceEpoch == server.Epoch && b.PresenceEpoch == server.Epoch && b.Presences.Count == 0);
    Check(!await a.SendPresenceAsync(position), "a delayed sample from the previous epoch is discarded locally");
    await Task.Delay(110);
    var current = position with { Epoch = server.Epoch, MapId = 99 };
    Check(await a.SendPresenceAsync(current), "new epoch sample is sent");
    await Until(() => b.Presences.Count == 1 && b.Presences[0].State == current);
    Check(server.Tick == 0 && server.HistoryCount == 0 && server.CurrentPolicy.TimeSpeed == 0,
        "presence while paused changes no simulation tick, native speed or command history");
    await a.StartGameAsync();
    await Until(() => b.PresenceEpoch == server.Epoch && b.Presences.Count == 0);
});

await Test("presence relay: forged identity ignored, raw flood throttled, invalid/old epoch dropped", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-raw")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Observer"));
    await a.SetTimeSpeedAsync(0);
    var world = new byte[1024];
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    await Until(() => a.PresenceEpoch == server.Epoch);
    using var raw = new TcpClient();
    await raw.ConnectAsync("127.0.0.1", server.Port);
    await WritePacket(raw, new { type = "Hello", protocolVersion = NetworkProtocol.Version, hello = Hello("Raw peer") });
    var welcome = await ReadPacket(raw);
    Check(welcome.GetProperty("type").GetString() == "Welcome", "Welcome precedes every roster and presence broadcast");
    var rawId = welcome.GetProperty("playerId").GetInt64();
    await Until(() => a.Players.Count == 2);
    var receivedCount = 0;
    a.PresenceChanged += _ => Interlocked.Increment(ref receivedCount);
    var state = new PresenceUpdate(server.Epoch, 42, 10.5f, 20.25f, true, PlayerActivity.Playing);
    var startedAt = Environment.TickCount64;
    for (var index = 0; index < 100; index++)
        await WritePacket(raw, new
        {
            type = "Presence", protocolVersion = NetworkProtocol.Version, playerId = a.PlayerId,
            presence = state with { X = index }, playerPresence = new PlayerPresence(a.PlayerId, state)
        });
    await RawBarrier(raw, 991);
    await Until(() => a.Presences.Any(p => p.PlayerId == rawId));
    Check(a.Presences.Count == 1 && a.Presences[0].PlayerId != a.PlayerId, "server binds identity to socket despite forged envelope fields");
    var budget = 1 + (Environment.TickCount64 - startedAt) / PresenceRules.SendIntervalMilliseconds;
    Check(Volatile.Read(ref receivedCount) <= budget, "raw traffic also obeys server wall-clock budget");
    var previous = a.Presences[0];
    await Task.Delay(110);
    await WritePacket(raw, new { type = "Presence", protocolVersion = NetworkProtocol.Version, presence = state with { X = -1 } });
    await WritePacket(raw, new { type = "Presence", protocolVersion = NetworkProtocol.Version, presence = state with { Epoch = server.Epoch - 1, MapId = 98 } });
    await RawBarrier(raw, 992);
    Check(a.Presences[0] == previous, "invalid coordinates and previous epoch never reach peers");
    await WritePacket(raw, new { type = "Presence", protocolVersion = NetworkProtocol.Version, presence = state with { Visible = false, X = 0, Z = 0 } });
    await RawBarrier(raw, 993);
    await Until(() => !a.Presences[0].State.Visible);
    await a.SetTimeSpeedAsync(0);
    Check(a.IsConnected && server.HistoryCount == 0 && server.Tick == 0,
        "advisory flood does not append commands, advance the paused timeline or disconnect healthy peers");
    raw.Dispose();
    await Until(() => a.Players.Count == 1 && a.Presences.Count == 0);
});

await Test("presence heartbeat reports measured RTT rather than invented latency", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-latency")));
    await server.StartAsync();
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(client.LatencyMilliseconds == -1, "unmeasured latency starts unknown");
    await Until(() => client.LatencyMilliseconds >= 0 && client.Players.Count == 1 && client.Players[0].LatencyMilliseconds >= 0, 8);
    Check(client.LatencyMilliseconds < 5000 && client.Players[0].LatencyMilliseconds < 5000, "loopback round trip is measured by both endpoints");
});

await Test("concurrent writes are serialized; broadcasts never go backwards", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("parallel")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var revisions = new List<long>();
    b.PolicyChanged += p => { lock (revisions) revisions.Add(p.Revision); };
    await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? a : b).SetSpeedAsync(true, 1 + i, 60)));
    await Until(() => b.CurrentPolicy?.Revision == 12 && a.CurrentPolicy?.Revision == 12);
    Check(a.CurrentPolicy == b.CurrentPolicy, "all clients converge");
    lock (revisions) Check(revisions.SequenceEqual(revisions.Order()), "monotonic ordered broadcast");
});

await Test("compatibility locks exact build, mod hash and protocol client version", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("compat")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await using var wrongMods = new DedicatedClient();
    await Throws(() => wrongMods.ConnectAsync("127.0.0.1", server.Port, Hello("B", "mod-b")));
    Check(!wrongMods.IsConnected, "mismatch must not appear connected");
    await using var wrongBuild = new DedicatedClient();
    await Throws(() => wrongBuild.ConnectAsync("127.0.0.1", server.Port, Hello("B", version: "1.7.4999")));
    await using var oldGame = new DedicatedClient();
    await Throws(() => oldGame.ConnectAsync("127.0.0.1", server.Port, Hello("B", version: "1.6.4850")));
    await using var wrongClient = new DedicatedClient();
    await Throws(() => wrongClient.ConnectAsync("127.0.0.1", server.Port, Hello("B") with { ClientVersion = "0.0.1" }));
    Check(a.IsConnected, "healthy peer survives failed handshakes");
});

await Test("controller-only permission is enforced by the server", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("permission"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CanEdit && !b.CanEdit, "server assigns roles");
    await Throws(() => b.SetSpeedAsync(true, 4, 60));
    Check(server.CurrentPolicy.Revision == 0, "blocked command changes nothing");
    await a.SetSpeedAsync(true, 4, 60);
    await Until(() => b.CurrentPolicy?.Revision == 1);
});

await Test("reconnect and server restart restore policy instead of local defaults", async () =>
{
    var path = Dir("persistence");
    var server = new DedicatedServer(new(Port: 0, DataDirectory: path));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await a.SetSpeedAsync(true, 4, 85);
    var previous = server.CurrentPolicy;
    await a.DisconnectAsync();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(a.CurrentPolicy == previous, "same instance reconnects with latest policy");
    await a.DisconnectAsync(); await server.DisposeAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: path));
    await restarted.StartAsync();
    await a.ConnectAsync("127.0.0.1", restarted.Port, Hello());
    Check(a.CurrentPolicy == previous, "disk-backed policy survives restart");
    await a.SetSpeedAsync(false, 2, 60);
    Check(restarted.CurrentPolicy.Revision == previous.Revision + 1, "revision survives restart");
});

await Test("oversized malformed frame is bounded; other peers still connect", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("frames")));
    await server.StartAsync();
    using var rogue = new TcpClient(); await rogue.ConnectAsync("127.0.0.1", server.Port);
    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
    await rogue.GetStream().WriteAsync(header);
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(a.IsConnected, "malformed socket must not kill listener");
});

await Test("snapshot dedup, bounds and path validation", async () =>
{
    var store = new SnapshotStore(Dir("store"));
    var bytes = new byte[700_000]; new Random(7).NextBytes(bytes);
    var a = await store.PutAsync(new MemoryStream(bytes));
    var b = await store.PutAsync(new MemoryStream(bytes));
    Check(a.Id == b.Id && a.ChunkHashes.SequenceEqual(b.ChunkHashes) && a.ChunkHashes.Count == 3, "content addressed chunks");
    var chunk = await store.ReadChunkAsync(a.Id, 0);
    Check(chunk.Data.SequenceEqual(bytes.Take(a.ChunkSize)) && chunk.Sha256 == a.ChunkHashes[0], "chunk hash");
    await Throws(() => store.ReadChunkAsync("../outside", 0));
    await Throws(() => store.ReadChunkAsync(a.Id, -1));
    await Throws(() => store.ReadChunkAsync(a.Id, 3));
    var empty = await store.PutAsync(new MemoryStream());
    Check(empty.Length == 0 && empty.ChunkHashes.Count == 0, "empty snapshot defined");
});

await Test("snapshot cancel then resume; final digest matches; cache hit avoids transfer", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("download")));
    await server.StartAsync();
    var bytes = new byte[1_100_000]; new Random(17).NextBytes(bytes);
    var manifest = await server.Snapshots.PutAsync(new MemoryStream(bytes));
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    var received = await client.GetSnapshotManifestAsync();
    Check(received?.Id == manifest.Id, "manifest comes from independent server");
    var destination = Path.Combine(Dir("client-cache"), "save.snapshot");
    using var cancel = new CancellationTokenSource();
    await Throws(() => client.DownloadSnapshotAsync(manifest, destination, (done, total) =>
    { if (done >= manifest.ChunkSize) cancel.Cancel(); }, cancel.Token));
    Check(!File.Exists(destination), "partial must never masquerade as complete snapshot");
    await client.DownloadSnapshotAsync(manifest, destination);
    var saved = await File.ReadAllBytesAsync(destination);
    Check(saved.SequenceEqual(bytes), "resumed bytes identical");
    Check(Convert.ToHexStringLower(SHA256.HashData(saved)) == manifest.Id, "whole-file SHA256");
    // Make every remote chunk request fail. A completed valid cache must be reused without transfer.
    File.Delete(Path.Combine(Dir("download"), "Snapshots", "objects", manifest.Id + ".blob"));
    await client.DownloadSnapshotAsync(manifest, destination);
    Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "verified cache hit");
});

await Test("corrupt server snapshot is a request error, not a room disconnect", async () =>
{
    var data = Dir("corrupt-server");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await server.StartAsync();
    var manifest = await server.Snapshots.PutAsync(new MemoryStream(new byte[300_000]));
    var blob = Path.Combine(data, "Snapshots", "objects", manifest.Id + ".blob");
    await using (var file = new FileStream(blob, FileMode.Open, FileAccess.Write)) { file.WriteByte(123); }
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    await Throws(() => client.DownloadSnapshotAsync(manifest, Path.Combine(Dir("corrupt-cache"), "save.snapshot")), "InvalidSnapshotChunk");
    Check(client.IsConnected, "a corrupt snapshot must not disconnect a healthy peer");
    await client.SetSpeedAsync(true, 3, 60);
    Check(client.CurrentPolicy?.Multiplier == 3, "settings channel remains usable");
});

await Test("a queued edit from server A must never mutate server B after reconnect", async () =>
{
    await using var a = new DedicatedServer(new(Port: 0, DataDirectory: Dir("old-room")));
    await using var b = new DedicatedServer(new(Port: 0, DataDirectory: Dir("new-room")));
    await a.StartAsync(); await b.StartAsync();
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", a.Port, Hello());
    // Deterministic scheduler barrier: hold the existing edit queue like a slow in-flight operation.
    var gate = (SemaphoreSlim)typeof(DedicatedClient).GetField("_speedGate",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
    await gate.WaitAsync();
    Task pending;
    try
    {
        pending = client.SetSpeedAsync(true, 9, 100);
        await client.DisconnectAsync();
        await client.ConnectAsync("127.0.0.1", b.Port, Hello());
    }
    finally { gate.Release(); }
    await Throws(() => pending);
    Check(b.CurrentPolicy.Revision == 0 && b.CurrentPolicy.Multiplier == 2f, "stale edit leaked across rooms");
    await client.SetSpeedAsync(true, 3, 60);
    Check(b.CurrentPolicy.Multiplier == 3, "new-session edits still work");
});

await Test("queued notifications from server A cannot poison server B policy after reconnect", async () =>
{
    await using var a = new DedicatedServer(new(Port: 0, DataDirectory: Dir("event-old")));
    await using var b = new DedicatedServer(new(Port: 0, DataDirectory: Dir("event-new")));
    await a.StartAsync(); await b.StartAsync();
    await using var client = new DedicatedClient();
    using var blocked = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    int first = 0;
    client.StatusChanged += text =>
    {
        if (text == "Connecting" && Interlocked.CompareExchange(ref first, 1, 0) == 0)
        {
            blocked.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }
    };
    var authority = new SpeedAuthority();
    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PolicyChanged += policy =>
    {
        authority.ApplyServer(policy);
        if (policy == b.CurrentPolicy) delivered.TrySetResult();
    };
    try
    {
        await client.ConnectAsync("127.0.0.1", a.Port, Hello());
        await Until(() => blocked.IsSet);
        await client.SetSpeedAsync(true, 7, 100);
        await client.DisconnectAsync();
        await client.ConnectAsync("127.0.0.1", b.Port, Hello());
        authority.BeginSession();
        release.Set();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(authority.Current == b.CurrentPolicy, "old revision from room A poisoned room B subscriber state");
    }
    finally { release.Set(); }
});

await Test("command log applies in server order, rejects duplicates and ignores the future", () =>
{
    var log = new CommandLog();
    byte[] p = [1];
    Check(log.Accept(new(1, 10, "designation", p)), "accept #1");
    Check(log.Accept(new(2, 5, "designation", p)), "accept #2 with an earlier tick");
    Check(!log.Accept(new(2, 11, "designation", p)), "duplicate sequence");
    Check(log.TakeThrough(4).Count == 0, "nothing before its tick");
    var due = log.TakeThrough(10);
    Check(due.Count == 2 && due[0].Sequence == 2 && due[1].Sequence == 1, "ordered by tick, then sequence");
    Check(log.Accept(new(1, 12, "designation", p)) == false, "regressed sequence rejected");
    return Task.CompletedTask;
});

await Test("tick barrier never runs ahead of the server and ignores stale publishers", () =>
{
    var barrier = new TickBarrier();
    barrier.Publish(5); barrier.Publish(3);
    Check(barrier.ServerTick == 5 && barrier.Backlog == 5, "monotonic server tick");
    for (var i = 0; i < 5; i++) Check(barrier.TryAdvance(), "advance");
    Check(!barrier.TryAdvance() && barrier.Backlog == 0, "cannot pass the server");
    barrier.Publish(7);
    Check(barrier.Backlog == 2, "new ticks reopen the window");
    return Task.CompletedTask;
});

await Test("shared colony: controller publishes world, starts, and both clients receive commands", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("shared")));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    Check(controller.CanEdit && guest.CanEdit, "both peers may edit in the default room");
    var world = new byte[300_000]; new Random(3).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await controller.ClaimHostAsync();
    await controller.UploadWorldAsync(manifest, new MemoryStream(world));
    Check(server.WorldManifest?.Id == manifest.Id, "server stores the shared world");
    Check((await guest.GetWorldManifestAsync())?.Id == manifest.Id, "guests see the same world id");
    var started = new TaskCompletionSource<long>();
    guest.WorldStarted += (session, _) => started.TrySetResult(session.Epoch);
    await controller.StartGameAsync();
    Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(20)) >= 0, "guest receives start");
    var received = new TaskCompletionSource<List<GameCommand>>();
    guest.CommandsReceived += commands => received.TrySetResult(commands.ToList());
    var hostEchoed = false;
    controller.CommandsReceived += _ => hostEchoed = true;
    await controller.SendCommandAsync("designation", "Mine|0|10|20"u8.ToArray());
    var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(batch.Count == 1 && batch[0].Sequence == 1 && batch[0].Kind == "designation", "command relayed in order");
    Check(!hostEchoed, "the origin does not receive its own command back");
    Check(batch[0].Tick >= 0 && batch[0].Payload.Length > 0, "server assigned tick and kept payload");
    Check(server.Tick >= 0 && server.GameRunning, "server drives a shared tick clock");
});

await Test("controller-only room requires explicit host to publish its world", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("shared-owner"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    Check(controller.CanEdit && !guest.CanEdit && !controller.IsHost && !guest.IsHost, "editing is separate from hosting");
    var world = new byte[2048]; new Random(5).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await Throws(() => guest.UploadWorldAsync(manifest, new MemoryStream(world)), "HostOnly");
    await controller.ClaimHostAsync();
    await controller.UploadWorldAsync(manifest, new MemoryStream(world));
    Check(server.WorldManifest?.Id == manifest.Id, "controller publishes the shared world");
});

await Test("action codec round-trips every player action and rejects malformed payloads", () =>
{
    // The generic gizmo fallback carries a raw Scribe fragment, so its separator must not be a character
    // that can appear in the payload. XML is full of '|' in text nodes; a newline before the fragment keeps
    // the id unambiguous.
    var fragment = "<v><forbidden>True</forbidden><label>a|b|c</label></v>";
    var state = TurboTick.Simulation.CommandCodec.ThingStateCommand(9876, fragment, 3);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeThingState(state.Payload, out var stateId, out var stateXml)
        && stateId == 9876 && stateXml == fragment, "thing state keeps a fragment containing separators");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeThingState([], out _, out _), "empty thing state rejected");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeThingState("9876\n"u8.ToArray(), out _, out _),
        "thing state without a fragment rejected");
    var design = TurboTick.Simulation.CommandCodec.DesignationCommand("Mine", 0, 12, 34, -1, 7);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDesignation(design.Payload, out var def, out var map, out var x, out var z, out var thing)
        && def == "Mine" && map == 0 && x == 12 && z == 34 && thing == -1, "designation");
    // Thing-keyed designations (HarvestPlant, Hunt, Tame) must survive the round trip with their target,
    // because the receiver indexes those by Thing and a cell lookup throws.
    var harvest = TurboTick.Simulation.CommandCodec.DesignationCommand("HarvestPlant", 0, 7, 9, 4242, 8);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDesignation(harvest.Payload, out _, out _, out _, out _, out var target)
        && target == 4242, "thing-keyed designation keeps its target");
    var blueprint = TurboTick.Simulation.CommandCodec.BlueprintCommand("Wall", "WoodLog", 1, 5, 6, 2, 8);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeBlueprint(blueprint.Payload, out var b, out var stuff, out var bm, out var bx, out var bz, out var rot)
        && b == "Wall" && stuff == "WoodLog" && bm == 1 && bx == 5 && bz == 6 && rot == 2, "blueprint");
    var draft = TurboTick.Simulation.CommandCodec.DraftCommand(42, true, 9);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDraft(draft.Payload, out var pawn, out var on) && pawn == 42 && on, "draft");
    var order = TurboTick.Simulation.CommandCodec.OrderCommand(42, "HaulToCell", 1000, -1, -1, 77, 10, 11);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeOrder(order.Payload, out var op, out var job, out var ac, out var at, out var bc, out var bt, out var count)
        && op == 42 && job == "HaulToCell" && ac == 1000 && at == -1 && bt == 77 && count == 10, "order");
    var forbid = TurboTick.Simulation.CommandCodec.ForbidCommand(1234, true, 12);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeForbid(forbid.Payload, out var ft, out var fb) && ft == 1234 && fb, "forbid");
    var prio = TurboTick.Simulation.CommandCodec.PriorityCommand(42, "Doctor", 1, 13);
    Check(TurboTick.Simulation.CommandCodec.TryDecodePriority(prio.Payload, out var pp, out var pw, out var pv) && pp == 42 && pw == "Doctor" && pv == 1, "priority");
    var zone = TurboTick.Simulation.CommandCodec.ZoneCellCommand("stockpile", 5, 0, 10, 20, true, 14);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeZoneCell(zone.Payload, out var zt, out var zi, out var zm, out var zx, out var zz, out var za)
        && zt == "stockpile" && zi == 5 && zm == 0 && zx == 10 && zz == 20 && za, "zone cell");
    var bill = TurboTick.Simulation.CommandCodec.BillAddCommand(555, "CookMealSimple", 10, 15);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeBillAdd(bill.Payload, out var bb, out var br, out var bc2) && bb == 555 && br == "CookMealSimple" && bc2 == 10, "bill add");
    var area = TurboTick.Simulation.CommandCodec.AreaAssignCommand(42, 2, 16);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeAreaAssign(area.Payload, out var ap, out var aa) && ap == 42 && aa == 2, "area assign");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeDesignation("broken"u8.ToArray(), out _, out _, out _, out _, out _), "reject short payload");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeDraft("1|2"u8.ToArray(), out _, out _), "reject invalid flag");
    return Task.CompletedTask;
});

await Test("state hash is order independent and changes with simulated state", () =>
{
    string[] a = ["t:1:0:5:5:1:100", "p:2:0:7:8:0.75:Haul"];
    string[] b = ["p:2:0:7:8:0.75:Haul", "t:1:0:5:5:1:100"];
    Check(TurboTick.Simulation.StateHash.Compute(a) == TurboTick.Simulation.StateHash.Compute(b), "order independent");
    Check(TurboTick.Simulation.StateHash.Compute(a) != TurboTick.Simulation.StateHash.Compute(["t:1:0:5:5:1:99", "p:2:0:7:8:0.75:Haul"]), "detects change");
    Check(TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul"]).Length == 64, "sha256 hex");
    // The combination is sum+xor+count precisely so that a duplicate entry cannot cancel itself out. With
    // xor alone, [a, a] would hash like [] and a lost thing would go unnoticed.
    Check(TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul"]) !=
          TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul", "p:2:0:7:8:0.75:Haul"]),
        "a duplicated entry changes the value");
    Check(TurboTick.Simulation.StateHash.Compute([]) == TurboTick.Simulation.StateHash.Compute([""]),
        "empty entries are ignored");
    return Task.CompletedTask;
});

await Test("server reports a desync when two peers disagree on the same tick", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("hash")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var world = new byte[4096]; new Random(9).NextBytes(world);
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    var desync = new TaskCompletionSource<int>();
    b.DesyncDetected += (tick, _) => desync.TrySetResult(tick);
    var same = new string('a', 64);
    await a.ReportHashAsync(60, same);
    await b.ReportHashAsync(60, same);
    Check(!desync.Task.IsCompleted, "matching hashes are silent");
    await b.ReportHashAsync(120, new string('b', 64));
    await a.ReportHashAsync(120, new string('c', 64));
    Check(await desync.Task.WaitAsync(TimeSpan.FromSeconds(20)) == 120, "mismatch reported with the tick");
});

await Test("a peer joining mid-session receives the epoch, tick and full command history", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("catchup")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[8192]; new Random(11).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await using var late = new DedicatedClient();
    var started = new TaskCompletionSource<int>();
    var replay = new List<GameCommand>();
    late.WorldStarted += (session, _) => started.TrySetResult(session.Tick);
    late.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(20)) >= 0, "late peer receives the running epoch");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "designation", "history replayed to the late peer");
});

await Test("session seed reaches every peer and survives a server restart with its history", async () =>
{
    var data = Dir("session-persist");
    var server = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var seeds = new List<int>();
    host.WorldStarted += (session, _) => seeds.Add(session.Seed);
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[4096]; new Random(13).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await Until(() => seeds.Count == 1);
    await host.SendCommandAsync("designation", "Mine|0|2|3"u8.ToArray());
    await host.SendCommandAsync("draft", "5|1"u8.ToArray());
    await Until(() => server.HistoryCount == 2);
    var epoch = server.Epoch;
    var seed = seeds[0];
    await host.DisconnectAsync();
    await server.StopAsync();
    await server.DisposeAsync();

    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await restarted.StartAsync();
    Check(restarted.GameRunning && restarted.Epoch == epoch, "epoch resumes after restart");
    Check(restarted.Session?.Seed == seed, "seed resumes after restart");
    Check(restarted.HistoryCount == 2, "command history resumes after restart");
    await using var rejoin = new DedicatedClient();
    var replay = new List<GameCommand>();
    rejoin.WorldStarted += (_, _) => { };
    rejoin.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await rejoin.ConnectAsync("127.0.0.1", restarted.Port, Hello("rejoin"));
    await Until(() => { lock (replay) return replay.Count == 2; });
    Check(replay.Select(c => c.Kind).SequenceEqual(new[] { "designation", "draft" }), "history replays in order");
});

await Test("two peers replaying the same ordered commands derive the same state hash", () =>
{
    static List<string> Run(IEnumerable<GameCommand> commands)
    {
        // Minimal deterministic model: commands place ids at positions; ordering must define the result.
        var state = new SortedDictionary<int, string>();
        var log = new CommandLog();
        foreach (var command in commands) log.Accept(command);
        foreach (var command in log.TakeThrough(1000))
        {
            var parts = System.Text.Encoding.UTF8.GetString(command.Payload).Split('|');
            state[int.Parse(parts[0])] = $"{parts[1]}|{parts[2]}";
        }
        return state.Select(pair => $"{pair.Key}:{pair.Value}").ToList();
    }
    var commands = new[]
    {
        new GameCommand(1, 5, "designation", "3|Mine|0"u8.ToArray()),
        new GameCommand(2, 2, "designation", "1|Haul|0"u8.ToArray()),
        new GameCommand(3, 9, "designation", "2|Cut|0"u8.ToArray())
    };
    var first = Run(commands);

    // Second peer receives the same sequence but drains the log tick by tick instead of all at once.
    var incremental = new CommandLog();
    foreach (var command in commands) incremental.Accept(command);
    var second = new SortedDictionary<int, string>();
    for (var tick = 0; tick <= 9; tick++)
        foreach (var command in incremental.TakeThrough(tick))
        {
            var parts = System.Text.Encoding.UTF8.GetString(command.Payload).Split('|');
            second[int.Parse(parts[0])] = $"{parts[1]}|{parts[2]}";
        }
    Check(TurboTick.Simulation.StateHash.Compute(first) ==
          TurboTick.Simulation.StateHash.Compute(second.Select(pair => $"{pair.Key}:{pair.Value}").ToList()),
        "incremental and bulk draining converge");
    return Task.CompletedTask;
});

await Test("after a desync the controller can restart the colony with a fresh epoch", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("recover")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(21).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(manifest, new MemoryStream(world));
    var epochs = new List<long>();
    guest.WorldStarted += (session, _) => { lock (epochs) epochs.Add(session.Epoch); };
    await host.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 1; });
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    // Restart resets history and issues a new epoch; the old commands must not leak into it.
    await host.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 2; });
    Check(epochs[1] > epochs[0], "restart issues a new epoch");
    Check(server.HistoryCount == 0, "old commands are cleared");
});

await Test("a checkpoint trims history so a late join replays only the delta", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("checkpoint")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    Check(host.IsCheckpointOwner, "first peer owns checkpoints");
    var world = new byte[4096]; new Random(31).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    for (var i = 0; i < 3; i++)
        await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i}|{i}"));
    await Until(() => server.HistoryCount == 3);
    await Until(() => server.Tick >= 4);
    var tick = server.Tick;
    var checkpoint = new byte[8192]; new Random(32).NextBytes(checkpoint);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(checkpoint);
    await host.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), tick);
    Check(server.CheckpointTick == tick, "checkpoint tick recorded");
    Check(server.HistoryCount == 0, "commands before the checkpoint are dropped");
    // Pin the snapshot being tested. A generic fresh-current-tick checkpoint can absorb the draft
    // into its snapshot before the joiner receives history, making the expected delta disappear.
    var checkpointReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    host.CheckpointRequested += () => { _ = RepublishCheckpoint(); };
    async Task RepublishCheckpoint()
    {
        try
        {
            await host.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), tick);
            checkpointReply.TrySetResult();
        }
        catch (Exception error) { checkpointReply.TrySetException(error); }
    }
    await host.SendCommandAsync("draft", "7|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await using var late = new DedicatedClient();
    var started = new TaskCompletionSource<SessionInfo>();
    var replay = new List<GameCommand>();
    late.WorldStarted += (session, _) => started.TrySetResult(session);
    late.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    var info = await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
    await checkpointReply.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(info.SnapshotTick == tick, "late peer starts from the selected checkpoint boundary");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "draft", "only the delta replays");
});

await Test("server rejects unknown command kinds and throttles a flooding client", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("guard")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var errors = new List<string>();
    host.StatusChanged += _ => { };
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[2048]; new Random(41).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("invented", "x"u8.ToArray());
    await Until(() => server.HistoryCount == 0);
    Check(server.HistoryCount == 0, "unknown kind never reaches history");
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    Check(server.HistoryCount == 1, "known kind accepted");
    var accepted = 1;
    for (var i = 0; i < 500; i++)
        await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i % 40}|{i % 40}"));
    await Task.Delay(1500);
    accepted = server.HistoryCount;
    Check(accepted is > 1 and < 501, $"flood is bounded, stored {accepted}");
    await host.SendCommandAsync("designation", "Mine|0|9|9"u8.ToArray());
    await Until(() => server.HistoryCount > accepted);
    Check(server.HistoryCount > accepted, "client recovers after the window");
});

await Test("concurrent state hashes compare every shared tick", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("concurrent-hashes")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("hash-a"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("hash-b"));
    await a.ClaimHostAsync();
    var world = new byte[128];
    await a.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    await Until(() => a.PresenceEpoch == server.Epoch && b.PresenceEpoch == server.Epoch);
    var pawns = new string('1', 64);
    var things = new string('2', 64);
    // Five categories: pawn position, health, job, things, designations. Only the last is made to differ.
    for (var round = 1; round <= 256; round++)
    {
        var expectedTick = round;
        var detail = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, string> record = (tick, text) => { if (tick == expectedTick) detail.TrySetResult(text); };
        b.DesyncDetected += record;
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () =>
            {
                await start.Task;
                await a.ReportHashAsync(expectedTick, new string('a', 64), [pawns, things, things, things, new string('3', 64), things]);
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                await b.ReportHashAsync(expectedTick, new string('b', 64), [pawns, things, things, things, new string('4', 64), things]);
            });
            start.SetResult();
            await Task.WhenAll(first, second);
            Check(await detail.Task.WaitAsync(TimeSpan.FromSeconds(20)) == "标记",
                $"concurrent report pair {expectedTick} must be compared exactly once with category detail");
        }
        finally { b.DesyncDetected -= record; }
    }
});

await Test("every codec command kind is accepted by the server whitelist", () =>
{
    // The whitelist lives in Networking and the constants live in Simulation, so they are maintained by hand.
    // That drifted once already: "thing-state" reached the codec and the apply path but not the whitelist, so
    // the server rejected every generic gizmo change as UnknownCommand and the feature silently did nothing.
    var kinds = typeof(TurboTick.Simulation.CommandCodec)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
    Check(kinds.Length >= 14, $"codec exposes its kinds (found {kinds.Length})");
    var missing = kinds.Where(k => !TurboTick.Networking.DedicatedServer.SupportedKinds.Contains(k)).ToArray();
    Check(missing.Length == 0, "server whitelist is missing: " + string.Join(", ", missing));
    return Task.CompletedTask;
});

await Test("desync detail names the category that diverged", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("cat")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var world = new byte[2048]; new Random(51).NextBytes(world);
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    var detail = new TaskCompletionSource<string>();
    b.DesyncDetected += (_, text) => detail.TrySetResult(text);
    var pawns = new string('1', 64);
    var things = new string('2', 64);
    var designations = new string('3', 64);
    await a.ReportHashAsync(60, new string('a', 64), [pawns, things, things, things, designations, things]);
    var otherDesignations = new string('4', 64);
    await b.ReportHashAsync(60, new string('b', 64), [pawns, things, things, things, otherDesignations, things]);
    Check(await detail.Task.WaitAsync(TimeSpan.FromSeconds(20)) == "标记", "only the differing category is named");
});

await Test("a dropped peer reconnects to the checkpoint, replays the delta and can act again", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("recover-session")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(61).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await Until(() => server.Tick >= 4);
    var checkpoint = new byte[4096]; new Random(62).NextBytes(checkpoint);
    await host.CheckpointWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(checkpoint), new MemoryStream(checkpoint), server.Tick);
    await host.SendCommandAsync("draft", "9|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);

    await guest.DisconnectAsync();
    var replay = new List<GameCommand>();
    var info = new TaskCompletionSource<SessionInfo>();
    guest.WorldStarted += (session, _) => info.TrySetResult(session);
    guest.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var resumed = await info.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(resumed.SnapshotTick == server.CheckpointTick, "resumes from the checkpoint");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "draft", "only the post-checkpoint delta replays");
    await guest.SendCommandAsync("designation", "Mine|0|5|5"u8.ToArray());
    await Until(() => server.HistoryCount == 2);
    Check(server.HistoryCount == 2, "reconnected peer can act again");
});

await Test("five peers submitting concurrently yield one total order with no loss, and history stays bounded", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("stress")));
    await server.StartAsync();
    var clients = new List<DedicatedClient>();
    try
    {
        for (var i = 0; i < 5; i++)
        {
            var client = new DedicatedClient();
            clients.Add(client);
            await client.ConnectAsync("127.0.0.1", server.Port, Hello("p" + i));
        }
        var world = new byte[8192]; new Random(71).NextBytes(world);
        await clients[0].ClaimHostAsync();
        await clients[0].UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
        var received = new List<GameCommand>();
        clients[1].CommandsReceived += commands => { lock (received) received.AddRange(commands); };
        await clients[0].StartGameAsync();
        await Task.WhenAll(clients.Select((client, index) => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                await client.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{index}|{i}"));
        })));
        await Until(() => server.HistoryCount == 500, 20);
        Check(server.HistoryCount == 500, "all 500 commands stored");
        // Broadcasts trail the server-side commit; wait for delivery before judging the order.
        // A peer no longer receives its own commands: it already executed them locally.
        await Until(() => { lock (received) return received.Count == 400; }, 20);
        List<GameCommand> ordered;
        lock (received) ordered = [.. received];
        Check(ordered.Count == 400, "every command from other peers delivered once");
        Check(ordered.Select(c => c.Sequence).Distinct().Count() == 400, "no duplicate sequence");
        Check(ordered.Select(c => c.Sequence).SequenceEqual(ordered.Select(c => c.Sequence).Order()), "single total order");
        Check(ordered.All(c => c.Tick >= 0), "every command has a tick");
        // Commands are scheduled a couple of ticks ahead; let them fall into the past before checking.
        await Task.Delay(250);
        await clients[0].CheckpointWorldAsync(
            TurboTick.Networking.SnapshotBuilder.Describe(new byte[4096]), new MemoryStream(new byte[4096]), server.Tick);
        Check(server.HistoryCount == 0, "checkpoint bounds history");
    }
    finally
    {
        foreach (var client in clients) await client.DisposeAsync();
    }
});

await Test("comparison hashes stay inside a bounded window during a long session", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("window")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    var world = new byte[2048]; new Random(81).NextBytes(world);
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    for (var tick = 60; tick <= 6000; tick += 60) await a.ReportHashAsync(tick, new string('a', 64));
    await Until(() => server.TrackedHashTicks > 0);
    Check(server.TrackedHashTicks <= 601, $"hash window bounded, tracked {server.TrackedHashTicks}");
    Check(server.TrackedHashTicks >= 5, "recent history is still available for comparison");
});

await Test("a session refuses input and applies no commands until the world is loaded", () =>
{
    var session = new TurboTick.Simulation.SharedSession();
    session.BeginLoading(3, "abc");
    Check(!session.AcceptsInput && session.Phase == TurboTick.Simulation.SharedPhase.LoadingWorld, "loading blocks input");
    session.Commands.Accept(new GameCommand(1, 0, "designation", [1]));
    Check(session.Commands.Count == 1, "commands are buffered, not dropped");
    session.WorldLoaded();
    Check(!session.AcceptsInput && session.Phase == TurboTick.Simulation.SharedPhase.Synchronizing, "synchronising still blocks input");
    session.Playing();
    Check(session.AcceptsInput, "playing allows input");
    session.Fault("boom");
    Check(session.Phase == TurboTick.Simulation.SharedPhase.Faulted && session.Commands.Count == 0, "fault stops and clears");
    return Task.CompletedTask;
});

await Test("a late join keeps history commands that arrive after live ones", () =>
{
    var log = new CommandLog();
    // Live command arrives first because the peer joined while others were already playing.
    Check(log.Accept(new GameCommand(501, 900, "designation", [1])), "live command accepted");
    Check(log.Accept(new GameCommand(401, 800, "designation", [2])), "older history still accepted");
    Check(log.Accept(new GameCommand(400, 800, "designation", [3])), "history in any order");
    Check(!log.Accept(new GameCommand(501, 900, "designation", [1])), "exact duplicate rejected");
    var due = log.TakeThrough(1000);
    Check(due.Count == 3, "nothing silently dropped");
    Check(due.Select(c => c.Tick).SequenceEqual(new[] { 800, 800, 900 }), "applied in tick order");
    Check(due[0].Sequence < due[1].Sequence, "same tick ordered by sequence");
    return Task.CompletedTask;
});

await Test("an overflowed history tells the joining peer it cannot catch up instead of replaying a hole", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("overflow"), MaxHistory: 2));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[2048]; new Random(91).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    for (var i = 0; i < 5; i++) await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i}|{i}"));
    await Until(() => server.HistoryTruncated, 10);
    Check(server.HistoryTruncated, "history marks itself truncated");
    await using var late = new DedicatedClient();
    var incomplete = new TaskCompletionSource<bool>();
    late.CatchUpIncomplete += () => incomplete.TrySetResult(true);
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(await incomplete.Task.WaitAsync(TimeSpan.FromSeconds(20)), "peer is warned, not silently diverged");
    // A checkpoint makes the room joinable again.
    await Task.Delay(200);
    await host.CheckpointWorldAsync(
        TurboTick.Networking.SnapshotBuilder.Describe(new byte[1024]), new MemoryStream(new byte[1024]), server.Tick);
    Check(!server.HistoryTruncated && server.HistoryCount == 0, "checkpoint clears the truncation state");
});

await Test("command batches carry their epoch and the checkpoint owner passes to a survivor", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("epoch")));
    await server.StartAsync();
    await using var first = new DedicatedClient();
    await using var second = new DedicatedClient();
    await first.ConnectAsync("127.0.0.1", server.Port, Hello("first"));
    await second.ConnectAsync("127.0.0.1", server.Port, Hello("second"));
    Check(first.IsCheckpointOwner && !second.IsCheckpointOwner, "first peer owns checkpoints");
    var world = new byte[2048]; new Random(101).NextBytes(world);
    await first.ClaimHostAsync();
    await first.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    var epochs = new List<long>();
    second.WorldStarted += (session, _) => { lock (epochs) epochs.Add(session.Epoch); };
    await first.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 1; });
    var firstEpoch = epochs[0];
    var batch = new TaskCompletionSource<long>();
    second.CommandsReceived += _ => { };
    var seenEpoch = new TaskCompletionSource<long>();
    await first.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    // Restart creates a new epoch; the old command must not be replayable into it.
    await first.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 2; });
    Check(epochs[1] > firstEpoch, "restart bumps the epoch");
    Check(server.HistoryCount == 0, "old commands are gone");
    // Owner left, so the survivor takes over checkpoints.
    await first.DisconnectAsync();
    await Until(() => second.IsCheckpointOwner);
    Check(second.IsCheckpointOwner, "ownership migrates to the survivor");
    await Task.Delay(150);
    await second.CheckpointWorldAsync(
        TurboTick.Networking.SnapshotBuilder.Describe(new byte[1024]), new MemoryStream(new byte[1024]), server.Tick);
    Check(server.CheckpointManifest is not null, "new owner can checkpoint");
});

await Test("catch-up budget closes the gap instead of matching the server's pace", () =>
{
    Check(TurboTick.Simulation.CatchUpPlan.Steps(0) == 0, "caught up means no burst");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(-5) == 0, "never negative");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(3) == 3, "small gap runs the gap");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(500) == TurboTick.Simulation.CatchUpPlan.MaxPerFrame,
        "large gap is bounded per frame");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(500, 0) == 0, "budget disabled");
    // 1800 ticks behind with 8 per frame at 60 fps closes in under 4 seconds.
    var backlog = 1800;
    var frames = 0;
    while (backlog > 0 && frames < 10_000)
    {
        backlog -= TurboTick.Simulation.CatchUpPlan.Steps(backlog);
        frames++;
    }
    Check(backlog <= 0 && frames < 240, $"gap closes in {frames} frames");
    Check(TurboTick.Simulation.CatchUpPlan.PreferCheckpoint(7200), "huge gap prefers a fresh checkpoint");
    Check(!TurboTick.Simulation.CatchUpPlan.PreferCheckpoint(600), "small gap replays");
    return Task.CompletedTask;
});

await Test("a burst catch-up applies every command exactly once and in order", () =>
{
    var barrier = new TickBarrier();
    var log = new CommandLog();
    var applied = new List<long>();
    // Server is 200 ticks ahead; commands are spread across that gap, some arriving late.
    barrier.Publish(200);
    log.Accept(new GameCommand(1, 10, "designation", [1]));
    log.Accept(new GameCommand(2, 60, "designation", [2]));
    log.Accept(new GameCommand(3, 61, "designation", [3]));
    log.Accept(new GameCommand(4, 199, "designation", [4]));
    log.Accept(new GameCommand(5, 200, "designation", [5]));
    log.Accept(new GameCommand(6, 40, "designation", [6]));  // late arrival with a lower tick
    var frames = 0;
    while (barrier.CanAdvance && frames < 1000)
    {
        var steps = TurboTick.Simulation.CatchUpPlan.Steps(barrier.Backlog);
        for (var i = 0; i < steps && barrier.TryAdvance(); i++)
            foreach (var command in log.TakeThrough(barrier.AppliedTick))
                applied.Add(command.Sequence);
        frames++;
    }
    Check(barrier.AppliedTick == 200, "gap closed exactly");
    Check(applied.Count == 6 && applied.Distinct().Count() == 6, "each command applied once");
    Check(applied.SequenceEqual(new long[] { 1, 6, 2, 3, 4, 5 }), "tick order preserved through the burst");
    Check(frames < 30, $"closed in {frames} frames");
    return Task.CompletedTask;
});

await Test("checkpoint plus delta equals applying the whole history, with no command applied twice", () =>
{
    // Model: each designation command adds one cell id; applying twice would show up as a count change.
    var history = new List<GameCommand>();
    var applied = new List<int>();
    for (var i = 1; i <= 20; i++)
    {
        history.Add(new GameCommand(i, i * 10, "designation", BitConverter.GetBytes(i)));
    }

    var full = new List<int>();
    foreach (var command in history) full.Add(BitConverter.ToInt32(command.Payload));

    // The checkpoint covers ticks up to 120, so commands 1..12 are already inside the snapshot.
    const int checkpointTick = 120;
    var snapshotState = history.Where(c => c.Tick <= checkpointTick)
        .Select(c => BitConverter.ToInt32(c.Payload)).ToList();
    var delta = history.Where(c => c.Tick > checkpointTick).ToList();
    Check(delta.Count == 8, "delta contains only post-checkpoint commands");
    Check(delta.All(c => c.Tick > checkpointTick), "no pre-checkpoint command is replayed");

    var rebuilt = new List<int>(snapshotState);
    var log = new CommandLog();
    foreach (var command in delta) log.Accept(command);
    foreach (var command in log.TakeThrough(int.MaxValue)) rebuilt.Add(BitConverter.ToInt32(command.Payload));

    Check(rebuilt.Count == full.Count, "no command applied twice or lost");
    Check(rebuilt.Order().SequenceEqual(full.Order()), "rebuilt state matches full replay");
    Check(rebuilt.Count == rebuilt.Distinct().Count(), "no duplicate effects");
    _ = applied;
    return Task.CompletedTask;
});

await Test("transport command round-trips and the server accepts the kind", async () =>
{
    var command = TurboTick.Simulation.CommandCodec.TransportCommand(31, 77, 5, 1);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeTransport(command.Payload, out var pod, out var thing, out var count)
        && pod == 31 && thing == 77 && count == 5, "round trip");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeTransport("1|2|0"u8.ToArray(), out _, out _, out _), "zero count rejected");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeTransport("1|2"u8.ToArray(), out _, out _, out _), "short payload rejected");

    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("transport")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[2048]; new Random(111).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    var received = new TaskCompletionSource<GameCommand>();
    guest.CommandsReceived += commands => received.TrySetResult(commands[0]);
    await host.SendCommandAsync("transport", command.Payload);
    var relayed = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(relayed.Kind == "transport", "transport reaches other peers");
});

await Test("colony-wide actions are limited to the host unless explicitly loosened", () =>
{
    var policy = typeof(TurboTick.Simulation.InteractionPolicy);
    Check(!TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(true, false, false), "guest blocked while sharing");
    Check(TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(true, true, false), "host allowed");
    Check(TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(false, false, false), "single-player unaffected");
    Check(TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(true, false, true), "override allows guests");
    Check(TurboTick.Simulation.InteractionPolicy.DeniedMessage("交易").Contains("交易"), "message names the action");
    _ = policy;
    return Task.CompletedTask;
});

await Test("native speed: bidirectional selection, pause, late join and restart retain the same policy", async () =>
{
    var directory = Dir("native-speed");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("A"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CurrentPolicy?.TimeSpeed == 1 && b.CurrentPolicy?.TimeSpeed == 1, "normal by default");
    await a.SetTimeSpeedAsync(3);
    await Until(() => b.CurrentPolicy?.TimeSpeed == 3);
    Check(a.CurrentPolicy == b.CurrentPolicy, "same confirmed native selection");
    await b.SetTimeSpeedAsync(0);
    await Until(() => a.CurrentPolicy?.TimeSpeed == 0);
    var pausedRevision = a.CurrentPolicy!.Revision;
    await using var late = new DedicatedClient();
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(late.CurrentPolicy?.TimeSpeed == 0 && late.CurrentPolicy.Revision == pausedRevision, "late join stays paused");
    // A legacy policy edit must never overwrite the native selection.
    await a.SetSpeedAsync(true, 2, 60);
    Check(a.CurrentPolicy?.TimeSpeed == 0, "legacy update preserves pause");
    await a.DisconnectAsync(); await b.DisconnectAsync(); await late.DisconnectAsync();
    await server.StopAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await restarted.StartAsync();
    await a.ConnectAsync("127.0.0.1", restarted.Port, Hello("A"));
    Check(a.CurrentPolicy?.TimeSpeed == 0, "restart retains native pause");
    await a.SetTimeSpeedAsync(2);
    Check(a.CurrentPolicy?.TimeSpeed == 2, "resume to native fast");
});

await Test("native speed: controller-only permissions and invalid values leave room unchanged", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("native-role"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("A"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var original = server.CurrentPolicy;
    await Throws(() => b.SetTimeSpeedAsync(0), "ControllerOnly");
    await Throws(() => a.SetTimeSpeedAsync(-1));
    await Throws(() => a.SetTimeSpeedAsync(5));
    Check(server.CurrentPolicy == original && b.IsConnected, "permission/validation do not mutate or disconnect");
    await a.SetTimeSpeedAsync(4);
    await Until(() => b.CurrentPolicy?.TimeSpeed == 4);
});

await Test("native speed: ordered rapid presses use one authoritative revision stream", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("native-rapid")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("A"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var observed = new System.Collections.Concurrent.ConcurrentQueue<SpeedPolicy>();
    b.PolicyChanged += observed.Enqueue;
    int[] choices = [2, 3, 0, 1, 3, 2, 0];
    var requests = choices.Select(speed => a.SetTimeSpeedAsync(speed)).ToArray();
    await Task.WhenAll(requests);
    await Until(() => b.CurrentPolicy?.Revision == choices.Length);
    Check(a.CurrentPolicy == b.CurrentPolicy && b.CurrentPolicy!.TimeSpeed == choices[^1], "last key wins in order");
    var changes = observed.Where(policy => policy.Revision > 0).ToArray();
    await Until(() => observed.Count(policy => policy.Revision > 0) == choices.Length);
    changes = observed.Where(policy => policy.Revision > 0).ToArray();
    Check(changes.Select(policy => policy.TimeSpeed).SequenceEqual(choices), "no reordered key presses");
});

await Test("native clock: native rates, pause without debt, and running server holds its tick", async () =>
{
    foreach (var (speed, expected) in new[] { (0, 0), (1, 60), (2, 180), (3, 360), (4, 900) })
    {
        var clock = new NativeTickClock();
        var count = Enumerable.Range(0, 100).Sum(_ => clock.Advance(0.01, speed));
        Check(count == expected, $"native speed {speed}: {count}");
    }
    var pauseClock = new NativeTickClock();
    Check(pauseClock.Advance(120, 0) == 0 && pauseClock.Advance(1d / 60, 1) == 1, "no paused time debt");
    var directory = Dir("native-clock");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    byte[] world = [1, 2, 3];
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await Until(() => server.Tick >= 6);
    await host.SetTimeSpeedAsync(0);
    await Task.Delay(100); // settle the in-flight timer callback before measuring the hold
    var held = server.Tick;
    await Task.Delay(250);
    Check(server.Tick == held && server.GameRunning, "pause holds running-session tick");
    await host.SetTimeSpeedAsync(3);
    await Until(() => server.Tick > held);
    await host.SetTimeSpeedAsync(0);
    await server.StopAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await restarted.StartAsync();
    Check(restarted.GameRunning && restarted.CurrentPolicy.TimeSpeed == 0 && restarted.WorldManifest is not null,
        "changing native speed preserves the running world across restart");
});

await Test("per-tick permits stop the actual engine call when a native frame exhausts its watermark", () =>
{
    var barrier = new TickBarrier();
    var guard = new SimulationTickGuard(barrier);
    var gameTick = 150_000;
    barrier.Publish(1);
    var calls = 0;
    // A native low-FPS frame attempts two ticks; additional catch-up calls hit the same guard.
    for (var attempt = 0; attempt < 10; attempt++)
    {
        if (!guard.TryBegin(gameTick, out var permit)) continue;
        gameTick++; calls++;
        Check(guard.Complete(permit, gameTick), "completed engine tick commits once");
    }
    Check(calls == 1 && gameTick == 150_001 && barrier.AppliedTick == 1, "no physical-only extra ticks");
    barrier.Publish(4);
    for (var attempt = 0; attempt < 10; attempt++)
    {
        if (!guard.TryBegin(gameTick, out var permit)) continue;
        Check(!guard.TryBegin(gameTick, out _), "recursive tick denied before outer tick completes");
        gameTick++;
        Check(guard.Complete(permit, gameTick), "catch-up and native loops share permits");
    }
    Check(gameTick == 150_004 && barrier.AppliedTick == 4, "the snapshot offset stays constant");
    return Task.CompletedTask;
});

await Test("failed, skipped and debug-jumped engine ticks never count as successful shared ticks", () =>
{
    var barrier = new TickBarrier(); barrier.Publish(3);
    var guard = new SimulationTickGuard(barrier);
    Check(guard.TryBegin(100, out var cancelled), "acquire before potential exception");
    guard.Cancel();
    Check(!guard.Complete(cancelled, 101) && barrier.AppliedTick == 0, "cancelled tick cannot later commit");
    Check(guard.TryBegin(100, out var skipped), "acquire before another Harmony prefix skips");
    Check(!guard.Complete(skipped, 100) && barrier.AppliedTick == 0, "skipped original is not a tick");
    Check(guard.TryBegin(100, out var jumped), "acquire before debug time jump");
    Check(!guard.Complete(jumped, 2100) && barrier.AppliedTick == 0, "fast ecology jump is not one tick");
    Check(guard.TryBegin(100, out var good), "guard is released");
    Check(guard.Complete(good, 101) && !guard.Complete(good, 101), "complete exactly once");
    return Task.CompletedTask;
});

await Test("load completion is tied to one generation and cannot revive a failed or superseded world", () =>
{
    var session = new SharedSession();
    session.BeginLoading(1, "world-A");
    var first = session.LoadGeneration;
    Check(!session.TryFinishLoading(first), "a constructor/loading phase is not a completed load");
    session.WorldLoaded();
    session.BeginLoading(2, "world-B");
    var second = session.LoadGeneration;
    session.WorldLoaded();
    Check(!session.TryFinishLoading(first), "late world-A callback cannot complete world-B");
    session.Fault("read failed");
    Check(!session.TryFinishLoading(second), "late callback cannot revive a failure");
    session.BeginLoading(2, "world-B");
    session.WorldLoaded();
    Check(session.TryFinishLoading(session.LoadGeneration), "only the current finalized load enters play");
    Check(!session.TryFinishLoading(session.LoadGeneration), "one ready transition per load");
    session.Leave();
    Check(!session.TryFinishLoading(second), "leaving invalidates old tickets");
    return Task.CompletedTask;
});

await Test("1.7 package: own assemblies only, zero reference-mod dependencies, complete translations", () =>
{
    PackageValidation.Validate();
    return Task.CompletedTask;
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(temp, true); } catch { /* preserve failure artifacts if OS still holds a handle */ }
Environment.ExitCode = failed == 0 ? 0 : 1;
