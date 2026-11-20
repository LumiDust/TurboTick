using System.Collections.Generic;

namespace TurboTick.Portal;

/// <summary>Log levels. Names match <c>Verse.LogMessageType</c> so the page's <c>/warn/i</c> filter keeps working.</summary>
internal enum PortalLogType
{
    Message,
    Warning,
    Error
}

/// <summary>One log line as the page renders it: lowercase field names on purpose (the page reads type/text/repeats).</summary>
internal sealed class PortalLogMessage
{
    public PortalLogType type;
    public string? text;
    public int repeats = 1;

    internal PortalLogMessage(PortalLogType type, string? text) { this.type = type; this.text = text; }
}

/// <summary>
/// The portal's own log. Named <c>Log</c> on purpose: the five moved <c>AdminPage*</c> files say
/// <c>Log.Message</c> / <c>Log.Warning</c> / <c>Log.Error</c> in about sixty places, and those calls are
/// genuinely portal-side (upload accepted, backup failed, request threw). Declaring the type here means the
/// moved code compiles untouched and every one of those lines lands in the portal's ring buffer — which is
/// also what <c>/api/status</c> shows, because after the split the game's <c>Log</c> is a different process.
///
/// The game's own log is read separately from <c>game-stdout.log</c>; this buffer only explains the panel.
/// </summary>
internal static class Log
{
    private const int Capacity = 400;

    private static readonly object Gate = new();
    private static readonly List<PortalLogMessage> Buffer = new(Capacity);

    internal static IReadOnlyList<PortalLogMessage> Messages
    {
        get { lock (Gate) return Buffer.ToArray(); }
    }

    /// <summary>Mirrors <c>Verse.Log.LockMessages</c>: the page takes it while copying the buffer.</summary>
    internal static IDisposable LockMessages() => new Guard();

    private sealed class Guard : IDisposable
    {
        internal Guard() { Monitor.Enter(Gate); }
        public void Dispose() { Monitor.Exit(Gate); }
    }

    internal static void Message(string text) => Append(PortalLogType.Message, text);
    internal static void Warning(string text) => Append(PortalLogType.Warning, text);
    internal static void Error(string text) => Append(PortalLogType.Error, text);

    /// <summary>Also echoes to stdout so <c>docker logs</c> keeps working exactly as before the split.</summary>
    private static void Append(PortalLogType type, string text)
    {
        lock (Gate)
        {
            if (Buffer.Count > 0)
            {
                var last = Buffer[^1];
                if (last.type == type && string.Equals(last.text, text, StringComparison.Ordinal))
                {
                    last.repeats++;
                    return;
                }
            }

            Buffer.Add(new PortalLogMessage(type, text));
            if (Buffer.Count > Capacity) Buffer.RemoveRange(0, Buffer.Count - Capacity);
        }

        Console.WriteLine("[TurboTick portal] " + type switch
        {
            PortalLogType.Error => "ERR ",
            PortalLogType.Warning => "WRN ",
            _ => "    "
        } + text);
    }
}
