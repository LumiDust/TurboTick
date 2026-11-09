using System.Text.RegularExpressions;

namespace TurboTick.Networking;

/// <summary>Values shared by this standalone protocol, with no game assembly dependencies.</summary>
public static class NetworkProtocol
{
    public const int Version = 5;
    public const string ClientVersion = "0.6.2";
    public const int MaximumFrameLength = 4 * 1024 * 1024;
    public const int SnapshotChunkSize = 256 * 1024;

    /// <summary>How many ticks the published timeline may run ahead of the slowest peer. This is pipeline
    /// depth, not a backlog: a peer sitting inside it is keeping up, and must not try to close it, or it
    /// simulates several times faster than real time.</summary>
    public const int SimulationLeadTicks = 6;
}

/// <summary>Server-authoritative native time selection. Joining a room applies it by default.
/// Revision zero is the initial policy; legacy extra-rate fields remain for persisted-data compatibility.</summary>
public sealed record SpeedPolicy(bool Enabled = true, float Multiplier = 2f, float MaxRate = 60f, long Revision = 0,
    int TimeSpeed = 1)
{
    private float _multiplier = ValidateMultiplier(Multiplier);
    private float _maxRate = ValidateMaxRate(MaxRate);
    private long _revision = ValidateRevision(Revision);
    private int _timeSpeed = ValidateTimeSpeed(TimeSpeed);

    public float Multiplier { get => _multiplier; init => _multiplier = ValidateMultiplier(value); }
    public float MaxRate { get => _maxRate; init => _maxRate = ValidateMaxRate(value); }
    public long Revision { get => _revision; init => _revision = ValidateRevision(value); }
    /// <summary>Native Verse.TimeSpeed: 0 paused, 1 normal, 2 fast, 3 superfast, 4 developer ultrafast.
    /// Legacy extra-multiplier fields remain readable for older server state files; the game UI uses this value.</summary>
    public int TimeSpeed { get => _timeSpeed; init => _timeSpeed = ValidateTimeSpeed(value); }

    public static int ValidateTimeSpeed(int value) => value is >= 0 and <= 4
        ? value : throw new ArgumentOutOfRangeException(nameof(TimeSpeed), "Native time speed must be between 0 and 4.");

    private static float ValidateMultiplier(float value) => float.IsFinite(value) && value is >= 1f and <= 20f
        ? value : throw new ArgumentOutOfRangeException(nameof(Multiplier), "Multiplier must be finite and between 1 and 20.");
    private static float ValidateMaxRate(float value) => float.IsFinite(value) && value is >= 1f and <= 240f
        ? value : throw new ArgumentOutOfRangeException(nameof(MaxRate), "MaxRate must be finite and between 1 and 240.");
    private static long ValidateRevision(long value) => value >= 0
        ? value : throw new ArgumentOutOfRangeException(nameof(Revision), "Revision must be non-negative.");
}

public sealed record ModFingerprint(string PackageId, string ContentHash);
public sealed record ClientHello(string PlayerName, string GameVersion, string ModHash, string ClientVersion = NetworkProtocol.ClientVersion)
{
    /// <summary>Process-private simulation admission credential, supplied only by ConnectSimulationAsync.</summary>
    public string? SimulationKey { get; init; }

    public override string ToString() => $"ClientHello {{ PlayerName = {PlayerName}, GameVersion = {GameVersion}, ModHash = {ModHash}, ClientVersion = {ClientVersion} }}";
}

/// <summary>Transient, server-assigned room identity. Color is deterministic for the same player name,
/// including reconnects; it never consumes the simulation random stream.</summary>
public sealed record PlayerInfo(long Id, string Name, int ColorIndex, bool CanEdit, bool IsCheckpointOwner,
    int LatencyMilliseconds = -1, bool IsHost = false);

public enum PlayerActivity { Lobby, Synchronizing, Playing, Faulted }

/// <summary>Presentation only. MapId is Map.uniqueID, not the mutable map-list index.
/// MapId -1 means outside a map; Visible false retains map/activity but hides the pointer.</summary>
public sealed record PresenceUpdate(long Epoch, int MapId = -1, float X = 0, float Z = 0,
    bool Visible = false, PlayerActivity Activity = PlayerActivity.Lobby);

/// <summary>The relay binds PlayerId to the socket. UpdatedAtMilliseconds is replaced with local
/// monotonic receive time by the client; it is never a synchronized simulation timestamp.</summary>
public sealed record PlayerPresence(long PlayerId, PresenceUpdate State, long UpdatedAtMilliseconds = 0);

public static class PresenceRules
{
    public const int SendIntervalMilliseconds = 100;
    public const int KeepAliveMilliseconds = 1000;
    public const int StaleMilliseconds = 3500;
    public const int ColorCount = 12;

    public static int ColorForName(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name) hash = unchecked((hash ^ c) * 16777619);
        return (int)(hash % ColorCount);
    }

    public static bool IsValid(PresenceUpdate? state) => state is not null && state.Epoch >= -1 &&
        state.MapId is >= -1 and <= 1_000_000 && float.IsFinite(state.X) && float.IsFinite(state.Z) &&
        state.X is >= 0 and <= 1_000_000 && state.Z is >= 0 and <= 1_000_000 &&
        (!state.Visible || state.MapId >= 0) && Enum.IsDefined(state.Activity);
}

/// <summary>Session identity published at start; the seed aligns random streams across peers.</summary>
public sealed record SessionInfo(long Epoch, int Tick, int Seed, string WorldId, int SnapshotTick = 0,
    bool Authoritative = false, bool RoomAuthoritative = false, bool Lockstep = false);

/// <summary>One pawn exactly as the authoritative host simulates it. Display replicas apply this instead
/// of simulating. A predictive peer also needs the current job and its targets: two simulations that only
/// agree on position still walk to different places, because the AI underneath them chose differently.</summary>
public sealed record ReplicaPawn(int Id, int Map, float X, float Z, int Rotation, string? Job, float Health,
    int JobACell = -1, int JobAThing = -1, int JobBCell = -1, int JobBThing = -1);

/// <summary>One non-pawn object as the host simulates it. Only objects that changed since the previous
/// frame are sent, so a colony with tens of thousands of plants does not stream every tick.
/// <paramref name="State"/> is the object's own Scribe XML, so any third-party Comp travels with it.</summary>
public sealed record ReplicaThing(int Id, int Map, int X, int Z, int Rotation, int Stack, int HitPoints,
    string? State = null);

/// <summary>A tick of authoritative colony state. Pawns are always sent in full because there are few;
/// things travel as a delta plus explicit removals. The host's own simulation peer never receives its
/// own frame.</summary>
public sealed record AuthoritativeFrame(int Tick, IReadOnlyList<ReplicaPawn> Pawns,
    IReadOnlyList<ReplicaThing>? Changed = null, IReadOnlyList<int>? Removed = null,
    IReadOnlyList<ReplicaPawnState>? PawnState = null, IReadOnlyList<ReplicaCreation>? Created = null);

/// <summary>A pawn's full Scribe state, sent less often than its position. This is what carries needs,
/// injuries, hediffs, equipment and any third-party Comp without the mod author writing anything.</summary>
public sealed record ReplicaPawnState(int Id, string State);

/// <summary>An object the host created after the replica loaded its snapshot: a finished wall, a dropped
/// item, a tamed animal. The replica cannot invent it, so the definition, position and full Scribe state
/// travel together.</summary>
public sealed record ReplicaCreation(int Id, int Map, string Def, int X, int Z, int Rotation, int Stack,
    string? State = null);
public sealed record ServerOptions(string Address = "127.0.0.1", int Port = 30572, string DataDirectory = "Saved",
    string GameVersion = "1.7", string ModHash = "development", bool EveryoneCanEdit = true, int MaxHistory = 200_000,
    string? AdminPassword = null)
{
    /// <summary>Keep the lockstep timeline behind the resident process simulation's reported progress.</summary>
    public bool RequireSimulation { get; init; } = false;

    /// <summary>The resident is the only simulator; joining clients are display replicas.</summary>
    public bool Authoritative { get; init; } = false;

    /// <summary>Every peer simulates and the timeline is gated by the slowest one. The server only orders
    /// commands and paces ticks; it never owns the colony state.</summary>
    public bool Lockstep { get; init; } = false;

    // Options are routinely printed by hosts; never include the credential in the generated record text.
    public override string ToString() => $"ServerOptions {{ Address = {Address}, Port = {Port}, AdminEnabled = {!string.IsNullOrEmpty(AdminPassword)} }}";
}
public sealed record ServerAdminStatus(bool GameRunning, int Tick, int PlayerCount, string? GameVersion, string? ModHash);
public sealed record ServerResetResult(string BackupId);
public sealed record SnapshotManifest(string Id, long Length, int ChunkSize, IReadOnlyList<string> ChunkHashes);
public sealed record SnapshotChunk(string Id, int Index, byte[] Data, string Sha256);

/// <summary>One deterministic gameplay command. Sequence and tick are assigned by the server.</summary>
public sealed record GameCommand(long Sequence, int Tick, string Kind, byte[] Payload, long OriginPeer = 0);

/// <summary>Builds the same manifest the server would compute, so a client can verify before upload.</summary>
public static class SnapshotBuilder
{
    public static SnapshotManifest Describe(ReadOnlySpan<byte> data)
    {
        var hashes = new List<string>();
        using var whole = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        whole.AppendData(data);
        for (var offset = 0; offset < data.Length; offset += NetworkProtocol.SnapshotChunkSize)
        {
            var length = Math.Min(NetworkProtocol.SnapshotChunkSize, data.Length - offset);
            hashes.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data.Slice(offset, length))));
        }
        return new SnapshotManifest(Convert.ToHexStringLower(whole.GetHashAndReset()), data.Length,
            NetworkProtocol.SnapshotChunkSize, hashes);
    }
}

/// <summary>A structured server or transport error suitable for displaying to a player.</summary>
public sealed class NetworkException : IOException
{
    public string Code { get; }
    public NetworkException(string code, string message) : base(message) => Code = code;
    public NetworkException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;
}

internal static partial class Validation
{
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdPattern();

    internal static void ContentId(string id)
    {
        if (id is null || !ContentIdPattern().IsMatch(id))
            throw new ArgumentException("Snapshot id must be exactly 64 lowercase hexadecimal characters.", nameof(id));
    }

    internal static bool IsFullGameVersion(string? version) => version is not null &&
        System.Version.TryParse(version, out var parsed) && parsed.Major == 1 && parsed.Minor == 7 && parsed.Build >= 0;

    internal static void Hello(ClientHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (string.IsNullOrWhiteSpace(hello.PlayerName) || hello.PlayerName.Length > 80 || hello.PlayerName.Any(char.IsControl))
            throw new NetworkException("InvalidPlayerName", "Player name must contain 1 to 80 printable characters.");
        if (!IsFullGameVersion(hello.GameVersion))
            throw new NetworkException("GameVersionMismatch", "A complete RimWorld 1.7.x game version is required.");
        if (string.IsNullOrWhiteSpace(hello.ModHash) || hello.ModHash.Length > 512 || hello.ModHash.Any(char.IsControl))
            throw new NetworkException("InvalidModHash", "A non-empty simulation mod/configuration hash is required.");
        if (!string.Equals(hello.ClientVersion, NetworkProtocol.ClientVersion, StringComparison.Ordinal))
            throw new NetworkException("ClientVersionMismatch", $"Client version must be exactly {NetworkProtocol.ClientVersion}.");
    }

    internal static void Manifest(SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ContentId(manifest.Id);
        if (manifest.Length < 0 || manifest.ChunkSize != NetworkProtocol.SnapshotChunkSize || manifest.ChunkHashes is null)
            throw new InvalidDataException("Invalid snapshot length, chunk size or chunk hashes.");
        var expected = manifest.Length / manifest.ChunkSize + (manifest.Length % manifest.ChunkSize == 0 ? 0 : 1);
        if (expected != manifest.ChunkHashes.Count)
            throw new InvalidDataException("Snapshot chunk count does not match its length.");
        foreach (var hash in manifest.ChunkHashes)
            ContentId(hash);
    }

    internal static int ChunkLength(SnapshotManifest manifest, int index)
    {
        if (index < 0 || index >= manifest.ChunkHashes.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Snapshot chunk index is out of bounds.");
        return (int)Math.Min(manifest.ChunkSize, manifest.Length - (long)index * manifest.ChunkSize);
    }
}
