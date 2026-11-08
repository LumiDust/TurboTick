using System.Security.Cryptography;

namespace TurboTick.Networking;

public sealed partial class DedicatedServer
{
    private readonly byte[] _adminSalt = RandomNumberGenerator.GetBytes(AdminAuthentication.SaltLength);
    private readonly byte[]? _adminKey;
    private readonly SemaphoreSlim _adminHandshakes = new(4, 4);
    private readonly object _adminAttemptGate = new();
    private long _adminWindowStart;
    private int _adminAttempts;
    private int _archiveInProgress;

    private async Task AuthenticateAdminAsync(Peer peer, WireMessage hello, CancellationToken ct)
    {
        if (hello.ProtocolVersion != NetworkProtocol.Version)
            throw new NetworkException("ProtocolMismatch", $"Protocol version must be {NetworkProtocol.Version}.");
        if (_adminKey is null) throw new NetworkException("AdminDisabled", "Server management is disabled.");
        if (!_adminHandshakes.Wait(0)) throw new NetworkException("AdminRateLimited", "Too many management authentication attempts. Try again later.");
        try
        {
            lock (_adminAttemptGate)
            {
                var now = Environment.TickCount64;
                if (now - _adminWindowStart >= 10_000) { _adminWindowStart = now; _adminAttempts = 0; }
                if (++_adminAttempts > 16)
                    throw new NetworkException("AdminRateLimited", "Too many management authentication attempts. Try again later.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var challenge = new AdminChallenge(_adminSalt, RandomNumberGenerator.GetBytes(AdminAuthentication.NonceLength), AdminAuthentication.Iterations);
            await peer.Connection.WriteAsync(new WireMessage { Type = "AdminChallenge", AdminChallenge = challenge }, timeout.Token).ConfigureAwait(false);
            // This local challenge is consumed once, including an invalid proof. No retry on the same socket.
            var reply = await peer.Connection.ReadAsync(timeout.Token).ConfigureAwait(false);
            var expected = AdminAuthentication.CreateProof(_adminKey, challenge);
            var valid = false;
            try
            {
                if (reply is { Type: "AdminProof", ProtocolVersion: NetworkProtocol.Version, AdminProof: { } proof } &&
                    proof.Nonce is { Length: AdminAuthentication.NonceLength } && proof.Proof is { Length: AdminAuthentication.KeyLength })
                {
                    var nonceMatches = CryptographicOperations.FixedTimeEquals(challenge.Nonce, proof.Nonce);
                    var proofMatches = CryptographicOperations.FixedTimeEquals(expected, proof.Proof);
                    valid = nonceMatches & proofMatches;
                }
            }
            finally { CryptographicOperations.ZeroMemory(expected); }
            if (!valid)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token).ConfigureAwait(false);
                throw new NetworkException("AdminAuthenticationFailed", "Management authentication failed.");
            }
            peer.AuthenticatedAdmin = true;
            peer.Queue(new WireMessage { Type = "AdminWelcome" });
        }
        catch (OperationCanceledException) when (!peer.Token.IsCancellationRequested)
        { throw new NetworkException("AdminAuthenticationTimedOut", "Management authentication timed out."); }
        finally { _adminHandshakes.Release(); }
    }

    private string SnapshotDirectory(string? storeId) => storeId is null
        ? Path.Combine(_options.DataDirectory, "Snapshots") : Path.Combine(_options.DataDirectory, "RoomSnapshots", storeId);

    // Only called while holding _stateGate (or before opening the listener during load).
    private void ClearRoomRuntime()
    {
        Volatile.Write(ref _gameRunning, 0);
        Volatile.Write(ref _worldManifest, null);
        Volatile.Write(ref _checkpointManifest, null);
        Volatile.Write(ref _checkpointTick, 0);
        Interlocked.Exchange(ref _epoch, -1);
        Interlocked.Exchange(ref _tick, 0);
        Interlocked.Exchange(ref _commandSequence, 0);
        Volatile.Write(ref _seed, 0);
        _commandsSincePersist = 0;
        _worldUploads.Clear();
        _hostId = null;
        _controllerId = null;
        _checkpointOwnerId = null;
        ClearSimulationState();
        lock (_pendingJoiners) _pendingJoiners.Clear();
        lock (_history) { _history.Clear(); _historyTruncated = false; }
        lock (_hashGate)
        {
            _firstHashAtTick.Clear();
            _firstCategoriesAtTick.Clear();
            _hashByPeerTick.Clear();
            _firstPeerAtTick.Clear();
            _reportedDesyncTicks.Clear();
            _hashWindowFloor = 0;
        }
    }

    private async Task ResetRoomAsync(Peer peer, WireMessage message)
    {
        try { Validation.Hello(message.Hello!); }
        catch (Exception ex) when (ex is NetworkException or ArgumentException)
        {
            peer.Queue(WireMessage.Error(ex is NetworkException network ? network.Code : "InvalidCompatibility",
                "Reset requires a valid complete client compatibility fingerprint.", message.RequestId));
            return;
        }
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        Volatile.Write(ref _archiveInProgress, 1);
        try
        {
            var hello = message.Hello!;
            if (_options.RequireSimulation && (hello.GameVersion != LockedGameVersion || hello.ModHash != LockedModHash))
            {
                peer.Queue(WireMessage.Error("ResidentConfigurationMismatch",
                    "The resident simulation must use the installed game and modpack; update the service before changing configuration.", message.RequestId));
                return;
            }
            var backupId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var archiveRoot = Path.Combine(_options.DataDirectory, "Archives");
            // Staging lives beside the data files, not inside Archives: a failed archival must never leave
            // something in the archive directory that looks like an archive to an operator. It also means a
            // cleanup that cannot delete the tree (a transient Windows sharing violation, an indexer holding
            // a handle) degrades to one stray dot-directory instead of a bogus archived room.
            var preparing = Path.Combine(_options.DataDirectory, ".preparing-" + backupId);
            var archive = Path.Combine(archiveRoot, backupId);
            var storeId = Guid.NewGuid().ToString("N");
            var committed = false;
            try
            {
                Directory.CreateDirectory(preparing);
                // Network mutation, tick advancement and every server-state write are paused by this gate.
                // Copy all durable files except Archives, including every snapshot object and manifest.
                await CopyArchiveAsync(_options.DataDirectory, preparing, true, peer.Token).ConfigureAwait(false);
                await AtomicFile.WriteJsonAsync(Path.Combine(preparing, "server-state.json"),
                    CaptureState(CurrentPolicy, LockedGameVersion, LockedModHash), peer.Token).ConfigureAwait(false);
                peer.Token.ThrowIfCancellationRequested();
                // Created last, so a failed archival does not even add an empty Archives directory.
                Directory.CreateDirectory(archiveRoot);
                Directory.Move(preparing, archive);
                var freshStore = new SnapshotStore(SnapshotDirectory(storeId));
                var freshPolicy = new SpeedPolicy();
                var emptyState = new SavedState(freshPolicy, hello.GameVersion, hello.ModHash, SnapshotStoreId: storeId);
                // Cancellation is honored before the commit. Once started, commit and in-memory swap finish together.
                peer.Token.ThrowIfCancellationRequested();
                await AtomicFile.WriteJsonAsync(_statePath, emptyState, CancellationToken.None).ConfigureAwait(false);
                committed = true;
                Interlocked.Increment(ref _roomGeneration);
                ClearRoomRuntime();
                Snapshots = freshStore;
                _snapshotStoreId = storeId;
                Volatile.Write(ref _policy, freshPolicy);
                Volatile.Write(ref _lockedGameVersion, hello.GameVersion);
                Volatile.Write(ref _lockedModHash, hello.ModHash);
                foreach (var old in _peers.Values)
                {
                    // Include incomplete ordinary handshakes; new accepts carry the incremented generation.
                    if (old.IsAdminConnection || old.RoomGeneration == _roomGeneration) continue;
                    old.Joined = false;
                    old.CanEdit = false;
                    old.Close();
                }
                peer.Queue(new WireMessage { Type = "AdminResetResult", RequestId = message.RequestId,
                    AdminResetResult = new ServerResetResult(backupId) });
            }
            catch (Exception ex) when (!committed && ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                peer.Queue(WireMessage.Error("ArchiveFailed", "The archive or new room state could not be prepared; the current room remains active.", message.RequestId));
            }
            finally
            {
                // These are exclusively server-generated staging paths. A completed archive is kept even if commit failed.
                if (!committed) CleanupStaging(preparing);
            }
        }
        finally
        {
            Volatile.Write(ref _archiveInProgress, 0);
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Removes a failed archival's staging tree. Windows can transiently refuse to delete a directory right
    /// after its handles close, so a single attempt silently left a `.preparing-*` tree behind; retry briefly
    /// and, if it still fails, say so instead of losing the fact.
    /// </summary>
    private static void CleanupStaging(string preparing)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(preparing)) return;
                Directory.Delete(preparing, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    Console.WriteLine($"[server] staging directory could not be removed, delete it manually: {preparing} ({ex.GetType().Name})");
                else Thread.Sleep(50);
            }
        }
    }

    private static async Task CopyArchiveAsync(string source, string destination, bool root, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (root && (string.Equals(name, "Archives", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".preparing-", StringComparison.OrdinalIgnoreCase)
                || name is "headless.lock" or "deployment.lock" or "headless-status.json" or "headless-status.json.tmp")) continue;
            var attributes = File.GetAttributes(entry);
            // Never follow local symlinks out of the configured persistent data directory.
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Archive source contains a link.");
            var target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                await CopyArchiveAsync(entry, target, false, ct).ConfigureAwait(false);
            }
            else
            {
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
        }
    }

    private async Task DispatchAdminAsync(Peer peer, WireMessage message)
    {
        if (message.Type == "Ping") { peer.Queue(new WireMessage { Type = "Pong", RequestId = message.RequestId }); return; }
        if (message.Type == "Pong") return;
        RequireRequestId(message);
        if (message.Type == "AdminGetStatus")
        {
            await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
            try
            {
                peer.Queue(new WireMessage { Type = "AdminStatus", RequestId = message.RequestId,
                    AdminStatus = new ServerAdminStatus(GameRunning, Tick, CapturePlayers().Length, LockedGameVersion, LockedModHash) });
            }
            finally { _stateGate.Release(); }
            return;
        }
        if (message.Type == "AdminResetRoom")
        {
            await ResetRoomAsync(peer, message).ConfigureAwait(false);
            return;
        }
        throw new NetworkException("UnknownMessage", "This management message is not supported.");
    }
}
