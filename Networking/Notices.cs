namespace TurboTick.Networking;

/// <summary>How a player should read a server refusal.</summary>
public enum NoticeSeverity
{
    /// <summary>True but not the player's fault: the room is not running, or this connection may not act.</summary>
    Information,

    /// <summary>The action was dropped and the player can do something about it.</summary>
    Caution
}

/// <summary>One refusal from the coordinator, in the shape the UI needs it.
///
/// The relay already answers every command it will not publish with a structured `Error` frame
/// (`UnknownCommand`, `RateLimited`, `NotPlaying`, `InvalidCommand`, `SimulationReadOnly` - see
/// <c>DedicatedServer.RelayCommandAsync</c>). That frame used to be consumed only when a pending request was
/// waiting for it; a command refuses nothing, so nothing was waiting, and the frame was dropped. The player
/// experienced "I dragged a selection and nothing happened" while the coordinator had a line in its log naming
/// the exact reason. This type is that frame, preserved.
/// <para><paramref name="Folded"/> counts identical refusals that were dropped by the suppression window before this
/// one, so the UI can say "12 more of these were suppressed" instead of printing twelve lines.</para></summary>
public sealed record ServerNotice(string Code, string Message, NoticeSeverity Severity, int Folded = 0)
{
    /// <summary>True when the coordinator dropped a gameplay action, as opposed to declining to answer.</summary>
    public bool IsCommandRefusal => NoticeRules.IsCommandRefusal(Code);
}

/// <summary>Classification and bounds for <see cref="ServerNotice"/>.
///
/// Pure and game-free on purpose: the same rules are exercised by the networking test harness, which is the only
/// place this project can assert behaviour without a running game.</summary>
public static class NoticeRules
{
    /// <summary>Longest code and message this side will carry. A refusal goes straight into UI text and the log,
    /// so an oversized or control-laden payload must not be able to flood either.</summary>
    public const int MaximumCodeLength = 64;
    public const int MaximumMessageLength = 240;

    /// <summary>
    /// The codes the relay emits for a command it refused to publish. Kept in one place because the UI decides
    /// whether to say "your action was rejected" or "the server said something" from exactly this set.
    /// </summary>
    private static readonly HashSet<string> RefusedCommands = new(StringComparer.Ordinal)
    {
        "UnknownCommand", "RateLimited", "NotPlaying", "InvalidCommand", "SimulationReadOnly"
    };

    public static bool IsCommandRefusal(string? code) => code is not null && RefusedCommands.Contains(code);

    /// <summary>Turns a raw `Error` frame into a notice. Unknown codes are kept, not hidden: a newer coordinator
    /// refusing for a reason this client does not know is exactly the case where silence is worst.</summary>
    public static ServerNotice Classify(string? code, string? message)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "ServerError" : Bound(code.Trim(), MaximumCodeLength);
        var text = string.IsNullOrWhiteSpace(message)
            ? "The server refused this action without a reason."
            : Bound(message.Trim(), MaximumMessageLength);
        var severity = normalized is "NotPlaying" or "SimulationReadOnly"
            ? NoticeSeverity.Information
            : NoticeSeverity.Caution;
        return new ServerNotice(normalized, text, severity);
    }

    private static string Bound(string text, int limit) =>
        text.Length <= limit ? text : text[..limit];
}

/// <summary>One admission decision: whether to render this refusal, and how many identical ones were folded into it.</summary>
public readonly record struct NoticeAdmission(bool Show, int Suppressed);

/// <summary>Per-code rate limit for player-visible refusals.
///
/// A refused burst is not one refusal. A flooding client receives a `RateLimited` frame per command, and a stale
/// designator receives one per cell, so rendering each of them is a wall of identical text - which is how the
/// whole feature gets turned off by the player. The first one is shown, repeats are counted, and the next admitted
/// one reports how many it stands for.</summary>
public sealed class NoticeBudget
{
    public const long DefaultMinimumIntervalMilliseconds = 2_000;
    public const int DefaultMaximumCodes = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastShown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _suppressed = new(StringComparer.Ordinal);
    private readonly long _minimumInterval;
    private readonly int _maximumCodes;

    public NoticeBudget(long minimumIntervalMilliseconds = DefaultMinimumIntervalMilliseconds,
        int maximumCodes = DefaultMaximumCodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumIntervalMilliseconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCodes, 1);
        _minimumInterval = minimumIntervalMilliseconds;
        _maximumCodes = maximumCodes;
    }

    public long MinimumIntervalMilliseconds => _minimumInterval;

    /// <summary>Decides whether this refusal reaches the player. <paramref name="nowMilliseconds"/> is a monotonic
    /// local clock, never a synchronized tick: a rate limit is about how fast a human is clicking.</summary>
    public NoticeAdmission Admit(string? code, long nowMilliseconds)
    {
        var key = string.IsNullOrWhiteSpace(code) ? "ServerError" : code.Trim();
        lock (_gate)
        {
            // A clock that went backwards (or a code carried over from an earlier session) must not lock a refusal
            // out forever, so only a forward interval inside the window suppresses.
            if (_lastShown.TryGetValue(key, out var last) && nowMilliseconds >= last
                && nowMilliseconds - last < _minimumInterval)
            {
                _suppressed[key] = (_suppressed.TryGetValue(key, out var seen) ? seen : 0) + 1;
                return new NoticeAdmission(false, 0);
            }
            if (!_lastShown.ContainsKey(key) && _lastShown.Count >= _maximumCodes) EvictOldest();
            var folded = _suppressed.TryGetValue(key, out var pending) ? pending : 0;
            _suppressed[key] = 0;
            _lastShown[key] = nowMilliseconds;
            return new NoticeAdmission(true, folded);
        }
    }

    /// <summary>How many refusals of this code are currently folded into the next one. Diagnostics only.</summary>
    public int Suppressed(string? code)
    {
        lock (_gate) return _suppressed.TryGetValue(code?.Trim() ?? "ServerError", out var seen) ? seen : 0;
    }

    public int TrackedCodes
    {
        get { lock (_gate) return _lastShown.Count; }
    }

    public void Clear()
    {
        lock (_gate) { _lastShown.Clear(); _suppressed.Clear(); }
    }

    // Bounded memory: a peer cannot grow this dictionary by inventing codes. The oldest entry goes first, and a code
    // that is evicted simply gets to speak again - losing a suppression window is harmless, unbounded growth is not.
    private void EvictOldest()
    {
        string? oldest = null;
        var oldestAt = long.MaxValue;
        foreach (var pair in _lastShown)
            if (pair.Value < oldestAt) { oldestAt = pair.Value; oldest = pair.Key; }
        if (oldest is null) return;
        _lastShown.Remove(oldest);
        _suppressed.Remove(oldest);
    }
}
