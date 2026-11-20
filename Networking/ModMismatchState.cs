using System;

namespace TurboTick.Networking;

/// <summary>The last mod mismatch the server refused a join for, kept where the status writer can see it.
///
/// <para><b>Why a static and not a field on the connection.</b> A refusal is per peer and the peer is closed a few lines
/// later; what the operator needs is the line that survives - "Harmony: server 3.0.1.0 &lt; client 3.0.4.0, the server
/// is the one to update" - next to the server's own status. The status file is written from the game loop, so this is
/// the only shared place both can reach without threading a reference through the whole handshake.</para></summary>
public static class ModMismatchState
{
    /// <summary>The explanation, or an empty string when the last handshake agreed.</summary>
    public static string Last { get; private set; } = "";

    /// <summary>Unix seconds of the last refusal.</summary>
    public static long AtUtc { get; private set; }

    /// <summary>How many peers have been refused since the process started.</summary>
    public static long Refused { get; private set; }

    public static void Set(string text)
    {
        Last = text ?? "";
        AtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Refused++;
    }

    /// <summary>A join that agreed clears the stale line: a page that keeps showing yesterday's mismatch is worse than
    /// one that shows nothing.</summary>
    public static void Clear()
    {
        Last = "";
        AtUtc = 0;
    }
}
