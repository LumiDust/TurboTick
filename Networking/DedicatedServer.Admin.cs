using System.Security.Cryptography;

namespace TurboTick.Networking;

public sealed partial class DedicatedServer
{
    /// <summary>Joined players, counted exactly as the in-game management window counts them (it sends
    /// <c>CapturePlayers().Length</c> in <c>AdminStatus</c>). The loopback page must not invent a second
    /// definition of "how many players are online", or the page and the window can disagree about the room.</summary>
    public int PlayerCount => CapturePlayers().Length;

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

    /// <summary>
    /// Releases the world without stopping the service: the listener keeps its port, the room keeps its compatibility
    /// locks, but <see cref="GameRunning"/> goes false so a new save may be loaded.
    ///
    /// This is the in-process equivalent of "unload the colony". It exists because <c>stop</c> used to mean "exit the
    /// process", which took the administration page with it - and the page is where an operator starts the next world.
    ///
    /// Called from the game thread while no tick is in flight (the caller unloads the game first). Serializable state
    /// on disk is untouched: that is what makes this recoverable rather than destructive.
    /// </summary>
    public void ResetForNewWorld(string reason)
    {
        // Blocking acquire on purpose: the caller is the game thread and the gate is only ever held for the short
        // async sections above, never across a tick. Failing to take it would silently leave a running world.
        _stateGate.Wait(TimeSpan.FromSeconds(10));
        try { ClearRoomRuntime(); }
        finally { _stateGate.Release(); }
    }

    private async Task ResetRoomAsync(Peer peer, WireMessage message)    {
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
                // Copy all durable files except Archives, and only the snapshot store this room still references.
                await CopyArchiveAsync(_options.DataDirectory, preparing, true, _snapshotStoreId, peer.Token)
                    .ConfigureAwait(false);
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
                // Name the reason on the server side too: "ArchiveFailed" alone sent an operator looking at disk space
                // when the actual cause was one unreadable file.
                Console.WriteLine("[server] 归档失败（房间保持运行）：" + preparing + "：" + ex.GetType().Name + ": " + ex.Message);
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

    /// <summary>Copies the durable room into <paramref name="destination"/>.
    ///
    /// <paramref name="liveStore"/> is the snapshot store the room being archived actually references. Everything
    /// under <c>RoomSnapshots</c> that is not that store is debris from earlier rooms: each `AdminResetRoom` and each
    /// automatic pack-change archival allocates a fresh store id and abandons the previous one, and only the newest
    /// is ever read again. Copying the whole directory made an archive 5.1 GB when the room it described was 148 MB,
    /// and 16 such archives reached 25 GB on a 469 GB disk - the archive ends up larger than the thing it protects,
    /// which is how a safety net turns into an outage. Deleting the abandoned stores instead of copying them is safe
    /// because no state file references them any more (the id is validated on load).</summary>
    private static async Task CopyArchiveAsync(string source, string destination, bool root, string? liveStore,
        CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (root && (string.Equals(name, "Archives", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".preparing-", StringComparison.OrdinalIgnoreCase)
                || name is "headless.lock" or "deployment.lock" or "headless-status.json" or "headless-status.json.tmp")) continue;
            if (root && string.Equals(name, "RoomSnapshots", StringComparison.OrdinalIgnoreCase))
            {
                await CopyLiveStoreAsync(entry, Path.Combine(destination, name), liveStore, ct).ConfigureAwait(false);
                continue;
            }
            // The room is the state file plus the snapshot store. `profile` is the game's own save/profile folder
            // and holds `Mods` / `Saves` links into mounted volumes, `Steam` is steamcmd's credential cache and the
            // dot-folders are engine logs. None of it belongs to a room, and copying 250 MB of it per archive made
            // the archive fail on the first link it met instead of preserving the world.
            if (root && name is "profile" or "Steam" or ".cache" or ".config") continue;
            var attributes = File.GetAttributes(entry);
            // Never follow local symlinks out of the configured persistent data directory. A link cannot be
            // archived honestly anyway - copying its target would drag a mounted volume in, and copying the link
            // itself would produce an archive that dangles once the volume moves. Record nothing, keep going:
            // refusing the whole archive because one link exists loses the world the archive was made for.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Console.WriteLine($"[server] 归档跳过符号链接 {entry}（链接不属于房间，也不该被复制）");
                continue;
            }
            var target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                await CopyArchiveAsync(entry, target, false, liveStore, ct).ConfigureAwait(false);
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

    /// <summary>Copies only the store this room points at, so the archive's size matches the room's size.
    ///
    /// When the room has no store id yet (a room that never published a checkpoint), there is nothing worth keeping
    /// and the directory is skipped entirely. When the id names a directory that does not exist, that is a real
    /// inconsistency in the room being archived and it is reported - an archive that silently omits the world is
    /// worse than one that says it could not be made.</summary>
    private static async Task CopyLiveStoreAsync(string source, string destination, string? liveStore, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(liveStore)) return;
        var live = Path.Combine(source, liveStore);
        if (!Directory.Exists(live))
        {
            Console.WriteLine($"[server] 归档：房间指向的快照库 {liveStore} 不存在，快照不入归档");
            return;
        }
        var skipped = 0;
        var bytes = 0L;
        foreach (var other in Directory.EnumerateDirectories(source))
        {
            if (string.Equals(Path.GetFileName(other), liveStore, StringComparison.Ordinal)) continue;
            skipped++;
            bytes += await DirectoryBytesAsync(other, ct).ConfigureAwait(false);
        }
        Directory.CreateDirectory(destination);
        var target = Path.Combine(destination, liveStore);
        Directory.CreateDirectory(target);
        await CopyArchiveAsync(live, target, false, null, ct).ConfigureAwait(false);
        if (skipped > 0)
            Console.WriteLine($"[server] 归档：跳过 {skipped} 个不再被引用的旧快照库（{bytes / (1024 * 1024)} MB 未复制）");
    }

    /// <summary>Recursive size, used only for the one log line above. Failures are not fatal: this is telemetry.</summary>
    private static async Task<long> DirectoryBytesAsync(string directory, CancellationToken ct)
    {
        await Task.Yield();
        var total = 0L;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                total += new FileInfo(file).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Written into an archive made because the installed pack no longer matches the room, so an operator
    /// reading `<data>/Archives/<id>/` learns why it is there without grepping a log.</summary>
    private sealed record ArchivedRoom(string Previous, string Wanted, string Reason);

    /// <summary>
    /// Archives a persisted room whose locked game version or mod fingerprint no longer matches what this process
    /// would lock, then starts the process from an empty room.
    ///
    /// Before this, changing the installed pack made startup throw "Persisted room differs from the installed
    /// game/modpack", which killed the container and left an operator to move `state/` aside by hand. Every deploy
    /// of a new build changes the content fingerprint, so that was the normal path, not an edge case: the supervisor
    /// is the right place to decide it. Nobody wants the old room simulated, and the archive keeps it recoverable.
    ///
    /// Only a *known* mismatch archives. A room that locked nothing, or a process that asks to lock nothing ("1.7",
    /// "development"), keeps the previous resume behaviour - that is what makes a client-only or coordinator launch
    /// still able to reuse a room.
    /// </summary>
    private async Task<SavedState?> ArchiveRoomIfPackChangedAsync(SavedState? saved, string? version, string? modHash,
        CancellationToken ct)
    {
        if (saved is null) return null;
        if (saved.GameVersion is null || saved.ModHash is null || version is null || modHash is null) return saved;
        // Only a resident process has an opinion about the pack: it *is* the world, so it can only run the world it
        // was built from. Every other room takes whichever client turned up, and its persisted lock stays
        // authoritative over the command-line defaults (a stale `--mod-hash` argument is not a pack change).
        if (!_options.RequireSimulation) return saved;
        if (string.Equals(saved.GameVersion, version, StringComparison.Ordinal)
            && string.Equals(saved.ModHash, modHash, StringComparison.Ordinal)) return saved;
        // A different fingerprint on an empty room is not a pack change either: there is no world, no session and
        // no history to lose, so nothing gets archived. Adopt what this process is, because nothing else can.
        if (saved.Session is null && saved.World is null && saved.Checkpoint is null && (saved.History?.Count ?? 0) == 0)
            return saved with { GameVersion = version, ModHash = modHash };

        var previous = saved.GameVersion + "/" + Short(saved.ModHash);
        var wanted = version + "/" + Short(modHash);
        var backupId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N");
        var archiveRoot = Path.Combine(_options.DataDirectory, "Archives");
        // Staging beside the data files, never inside Archives: a failed archival must not leave something in the
        // archive directory that looks like an archive to an operator (same rule as ResetRoomAsync).
        var preparing = Path.Combine(_options.DataDirectory, ".preparing-" + backupId);
        var archive = Path.Combine(archiveRoot, backupId);
        var storeId = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(preparing);
            // `saved.SnapshotStoreId`, not `_snapshotStoreId`: this runs before the state is adopted, so the field is
            // still null here and the copy silently kept no snapshot store at all — the archive was left holding
            // root-level files and no world. The store the *archived room* pointed at is what has to travel with it.
            await CopyArchiveAsync(_options.DataDirectory, preparing, true, saved.SnapshotStoreId, ct).ConfigureAwait(false);
            await AtomicFile.WriteJsonAsync(Path.Combine(preparing, "archived.json"),
                new ArchivedRoom(previous, wanted, "modpack-changed"), ct).ConfigureAwait(false);
            Directory.CreateDirectory(archiveRoot);
            Directory.Move(preparing, archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanupStaging(preparing);
            // Refusing to start is still better than discarding a room whose checkpoint an operator may need: name
            // what is in the way instead of overwriting it.
            throw new InvalidDataException("Persisted room differs from the installed game/modpack (" + previous
                + " -> " + wanted + ") and could not be archived to " + archive + ": " + ex.Message, ex);
        }

        Console.WriteLine($"[server] 安装的游戏/mod 指纹已变化（{previous} -> {wanted}）："
            + $"旧房间已归档到 {archive}，从空房间启动");
        PruneArchives(archiveRoot, keep: 5);
        var fresh = new SavedState(new SpeedPolicy(), version, modHash, SnapshotStoreId: storeId);
        await AtomicFile.WriteJsonAsync(_statePath, fresh, ct).ConfigureAwait(false);
        // The archived room's store has just been copied into the archive and this process minted a fresh one, so the
        // original is now referenced by nothing. Left in place it accumulates exactly like the archives did: one dead
        // store per deploy, measured at 16 stores / 6.6 GB beside a 74 MB live one. Keep the *new* id: keeping the old
        // one would preserve the copy that is already in the archive and delete the room's actual store.
        PruneUnreferencedStores(storeId);
        return fresh;
    }

    /// <summary>Deletes every snapshot store under `<data>/RoomSnapshots` except <paramref name="liveStore"/>.
    ///
    /// Only call this after the store has been copied into an archive or the room itself was discarded: it is the
    /// last copy of a world. Stores whose id is not the live one are unreachable by any code path (the id comes from
    /// `server-state.json`), so keeping them keeps nothing recoverable. Failures are logged, never thrown - the room
    /// is already archived, and refusing to start over leftover bytes would be a new outage.</summary>
    private void PruneUnreferencedStores(string? liveStore)
    {
        var root = Path.Combine(_options.DataDirectory, "RoomSnapshots");
        try
        {
            if (!Directory.Exists(root)) return;
            var removed = 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, liveStore, StringComparison.Ordinal)) continue;
                try
                {
                    Directory.Delete(directory, recursive: true);
                    ++removed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine("[server] 快照库清理失败：" + directory + "：" + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (removed > 0)
                Console.WriteLine($"[server] 快照库清理：删除 {removed} 个不再被引用的快照库（在用 {liveStore ?? "无"}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("[server] 快照库清理：列举失败 " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> archives and deletes the rest.
    ///
    /// Automatic archival runs on every deploy (the content fingerprint changes with every build), so without a bound
    /// the directory grows until the disk is full - measured at 16 archives / 25 GB in one afternoon on a 469 GB disk.
    /// Only directories whose *name* has the shape both archival paths generate are touched, so a hand-placed backup
    /// or a renamed archive in the same parent is left alone. Deletion failure is logged rather than thrown: the room
    /// has already been archived by this point, and refusing to start because an old archive could not be pruned
    /// would be the same outage in a new place.</summary>
    private static void PruneArchives(string archiveRoot, int keep)
    {
        List<string> archives;
        try
        {
            if (!Directory.Exists(archiveRoot)) return;
            archives = Directory.EnumerateDirectories(archiveRoot)
                .Where(directory => LooksLikeArchive(Path.GetFileName(directory)))
                .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("[server] 归档清理：列举失败 " + ex.GetType().Name + ": " + ex.Message);
            return;
        }
        foreach (var directory in archives.Skip(keep))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                Console.WriteLine($"[server] 归档清理：删除 {Path.GetFileName(directory)}（只保留最近 {keep} 份）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("[server] 归档清理失败：" + directory + "：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>`&lt;yyyyMMdd&gt;T&lt;HHmmssfffffff&gt;Z-&lt;32 lowercase hex&gt;`, the only shape we generate.</summary>
    private static bool LooksLikeArchive(string name)
    {
        const int length = 8 + 1 + 13 + 1 + 1 + 32;
        if (name.Length != length || name[8] != 'T' || name[22] != 'Z' || name[23] != '-') return false;
        for (var index = 0; index < 22; index++)
        {
            if (index is 8) continue;
            if (!char.IsAsciiDigit(name[index])) return false;
        }
        for (var index = 24; index < length; index++)
        {
            var character = name[index];
            if (character is not (>= '0' and <= '9') && character is not (>= 'a' and <= 'f')) return false;
        }
        return true;
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
