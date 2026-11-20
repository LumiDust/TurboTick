using System.Globalization;
using System.Text.Json;

namespace TurboTick.Portal;

/// <summary>One expansion as the game reported it: whether its defs actually loaded, not just whether a folder exists.</summary>
internal sealed record DlcState(string PackageId, bool Loaded, string Status, string Label, string DefName);

/// <summary>
/// A dialog the game is waiting on. A headless process has nobody to click a button, so a dialog with a real choice
/// in it stops the world until this page answers - which is the mod author's intent, and used to be invisible.
/// </summary>
internal sealed record DialogState(int Id, string Type, string Title, string Text, string? ButtonA, string? ButtonB,
    string? ButtonC, bool Destructive, bool AutoClosed, string? Answer, int Repeats);

/// <summary>
/// The game process's own status file, read from disk.
///
/// This replaces every "<c>HeadlessRuntime.X</c>" access the page used to make. The game writes the file
/// atomically (tmp + rename) every two seconds from its own thread; the portal is a different process, so a
/// plain file read is the only correct way to ask "what is the authority doing". Missing or half-written files
/// degrade to <see cref="Empty"/> instead of throwing: a page that 500s because the game is mid-restart would
/// defeat the entire point of moving the page out of the game.
/// </summary>
internal sealed record GameStatus(
    bool Present,
    DateTime StampUtc,
    string State,
    string? Failure,
    string? Save,
    int? Pid,
    double UptimeSeconds,
    int? Tick,
    int? GameTick,
    int? Replicas,
    int? Players,
    int? JoinedPeers,
    int? CheckpointTick,
    string Phase,
    bool Connected,
    bool GameRunning,
    bool? Paused,
    bool Held,
    bool Frozen,
    bool HoldLocal,
    bool Authoritative,
    double Tps,
    double TargetTps,
    bool PatchCritical,
    IReadOnlyList<string> PatchFailed,
    string? InstalledVersion,
    string? InstalledModHash,
    string? RoomGameVersion,
    string? RoomModHash,
    long WireOutBytes,
    long WireOutFrames,
    long WireInBytes,
    long WireInFrames,
    IReadOnlyList<DlcState> Dlc,
    IReadOnlyList<DialogState> Dialogs)
{
    internal static GameStatus Empty { get; } = new(
        Present: false, StampUtc: DateTime.MinValue, State: "starting", Failure: null, Save: null, Pid: null,
        UptimeSeconds: 0, Tick: null, GameTick: null, Replicas: null, Players: null, JoinedPeers: null,
        CheckpointTick: null, Phase: "-", Connected: false, GameRunning: false, Paused: null, Held: false,
        Frozen: false, HoldLocal: false, Authoritative: false, Tps: 0, TargetTps: 0, PatchCritical: false,
        PatchFailed: [], InstalledVersion: null, InstalledModHash: null, RoomGameVersion: null, RoomModHash: null,
        WireOutBytes: 0, WireOutFrames: 0, WireInBytes: 0, WireInFrames: 0, Dlc: [], Dialogs: []);

    /// <summary>True when the file is older than a few seconds: the game died without cleaning up after itself.</summary>
    internal bool Stale => Present && DateTime.UtcNow - StampUtc > TimeSpan.FromSeconds(30);

    internal static GameStatus Read(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Empty;
            var stamp = File.GetLastWriteTimeUtc(path);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            var dlc = new List<DlcState>(8);
            if (root.TryGetProperty("dlc", out var dlcElement) && dlcElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dlcElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var package = Text(item, "package");
                    if (string.IsNullOrEmpty(package)) continue;
                    dlc.Add(new DlcState(package, Flag(item, "active") == true, Text(item, "status") ?? "Unknown",
                        Text(item, "label") ?? package, Text(item, "defName") ?? package));
                }
            }

            var failed = new List<string>(4);
            if (root.TryGetProperty("patchFailed", out var failedElement) && failedElement.ValueKind == JsonValueKind.Array)
                foreach (var item in failedElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name) failed.Add(name);

            var dialogs = new List<DialogState>(4);
            if (root.TryGetProperty("dialogs", out var dialogElement) && dialogElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dialogElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (Int(item, "id") is not { } id) continue;
                    dialogs.Add(new DialogState(id, Text(item, "type") ?? "?", Text(item, "title") ?? "",
                        Text(item, "text") ?? "", Text(item, "buttonA"), Text(item, "buttonB"), Text(item, "buttonC"),
                        Flag(item, "destructive") == true, Flag(item, "autoClosed") == true, Text(item, "answer"),
                        Int(item, "repeats") ?? 1));
                }
            }

            return new GameStatus(
                Present: true,
                StampUtc: stamp,
                State: Text(root, "state") ?? "unknown",
                Failure: Text(root, "failure"),
                Save: Text(root, "save"),
                Pid: Number(root, "pid") is { } pid ? (int)pid : null,
                UptimeSeconds: Decimal(root, "uptime") ?? 0,
                Tick: Int(root, "serverTick"),
                GameTick: Int(root, "gameTick"),
                Replicas: Int(root, "replicas"),
                Players: Int(root, "players"),
                JoinedPeers: Int(root, "joinedPeers"),
                CheckpointTick: Int(root, "checkpointTick"),
                Phase: Text(root, "phase") ?? "-",
                Connected: Flag(root, "connected") == true,
                GameRunning: Flag(root, "gameRunning") == true,
                Paused: Flag(root, "paused"),
                Held: Flag(root, "held") == true,
                Frozen: Flag(root, "frozen") == true,
                HoldLocal: Flag(root, "holdLocal") == true,
                Authoritative: Flag(root, "authoritative") == true,
                Tps: Decimal(root, "tps") ?? 0,
                TargetTps: Decimal(root, "targetTps") ?? 0,
                PatchCritical: Flag(root, "patchCritical") == true,
                PatchFailed: failed,
                InstalledVersion: Text(root, "installedVersion"),
                InstalledModHash: Text(root, "installedModHash"),
                RoomGameVersion: Text(root, "lockedGameVersion"),
                RoomModHash: Text(root, "modHash"),
                WireOutBytes: Number(root, "wireOutBytes") ?? 0,
                WireOutFrames: Number(root, "wireOutFrames") ?? 0,
                WireInBytes: Number(root, "wireInBytes") ?? 0,
                WireInFrames: Number(root, "wireInFrames") ?? 0,
                Dlc: dlc,
                Dialogs: dialogs);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // A torn read while the game is rewriting the file is expected, not exceptional.
            return Empty;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? Int(JsonElement element, string property) =>
        Number(element, property) is { } number ? (int)number : null;

    private static double? Decimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool? Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    internal string DescribeStamp() =>
        StampUtc == DateTime.MinValue ? "无状态文件" : StampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
