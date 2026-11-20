using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Records when, and from where, a thing's forbidden flag changed - because the measured divergence is a phase
/// difference in that flag rather than a constant one.
///
/// Measured (tick 18739): one peer had 48 forbidden things and 5 haulables, the other 53 and 0 - the same five items.
/// The decisive detail came from the periodic probe: the same peer had printed `forb=53 haul=0` at tick 18600,
/// byte-identical to the other peer's value at 18739. So the flag oscillates on both sides and the two peers are one
/// step apart, which makes "who changed it, and at which tick" the question.
///
/// A digest can only say "different". A tick and a caller say "different by how much, and where the extra step comes
/// from". Two earlier versions of this file got that wrong in ways worth recording:
/// <list type="bullet">
/// <item>the caller came from <c>Environment.StackTrace</c> and printed as <c>[Ref 961843F4]</c> - an unresolved
/// reference that looks like information but is a hash;</item>
/// <item>entries were held "since the last reset", so a dump at tick N showed load-time flips (tick 0) instead of
/// the ones near the divergence.</item>
/// </list>
/// The ring is now keyed by tick and keeps two consecutive ticks, because a one-tick phase difference needs both.
/// </summary>
internal static class ForbidTrace
{
    private const int Capacity = 96;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    /// <summary>Appends one flip. The ring is a rolling window of the most recent flips rather than a per-tick
    /// group: the dump is written at the divergence tick, so what matters is the flips just before it, and each
    /// line carries its own tick so a one-tick offset stays visible.</summary>
    internal static void Record(Thing thing, bool value, int tick, string? caller)
    {
        if (thing is null) return;
        lock (Recent)
        {
            Recent.Add(string.Join(" ",
                "forb",
                thing.thingIDNumber.ToString(CultureInfo.InvariantCulture),
                value ? "1" : "0",
                tick.ToString(CultureInfo.InvariantCulture),
                caller ?? "?"));
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    /// <summary>The first non-TurboTick frame that called SetForbidden. Returns "?" when it cannot be resolved -
    /// never a placeholder that looks like a real value.</summary>
    internal static string Caller()
    {
        try
        {
            var trace = new StackTrace(2, false);
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                if (method is null) continue;
                var type = method.DeclaringType;
                if (type is null) continue;
                if (type.Namespace == "TurboTick") continue;
                return type.Name + "." + method.Name;
            }
        }
        catch (Exception) { }
        return "?";
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

/// <summary>
/// Watches the flag itself, not the helper that usually sets it.
///
/// Measured need: five `MealSurvivalPack` stacks stopped being forbidden on one peer, and no
/// <c>ForbidUtility.SetForbidden</c> call was recorded on either peer at a gameplay tick. That helper is not the only
/// writer - <c>CompForbiddable.Forbidden</c> (<c>RimWorld/CompForbiddable.cs:14-37</c>) assigns <c>forbiddenInt</c>
/// directly and notifies the listers itself. A trace on the helper therefore has a hole exactly where the
/// unexplained change went.
/// </summary>
[HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
internal static class ForbidTracePatch
{
    private static void Postfix(CompForbiddable __instance, bool value)
    {
        if (!ThingDrawTrace.Enabled) return;
        try { ForbidTrace.Record(__instance.parent, value, GenTicks.TicksGame, ForbidTrace.Caller()); }
        catch (Exception) { }
    }
}



/// <summary>Records why a direct forbid change was or was not captured, and the call chain that produced it.
///
/// Two questions had to be answered at once and neither could be seen from the outside: whether the capture was
/// refused because `Applying` or `PlayerActing` said so (a refused capture and a missing patch produce identical
/// evidence - the change stays local - and that ambiguity already cost a round), and who is actually calling the
/// setter. The recorded caller was `CompForbiddable.<CompGetGizmosExtra>b__14_2` on the GUI peer every time and
/// never on the other peer, for exactly five stacks, while nothing was clicking. A compiler-generated name is not
/// proof of a player: the same lambda can be reached through a cached delegate passed to something else. Three
/// frames, not one, because the answer is the frame after the lambda.
/// </summary>
internal static class DirectForbidGate
{
    private const int Capacity = 48;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Record(int thingId, bool value, string gate, string chain)
    {
        lock (Recent)
        {
            Recent.Add($"forbidgate {thingId} {(value ? 1 : 0)} {gate} {chain}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    /// <summary>The first three frames outside TurboTick, as "Type.Method&lt;Type.Method&lt;Type.Method".</summary>
    internal static string CallerChain()
    {
        try
        {
            var trace = new StackTrace(2, false);
            var parts = new List<string>(3);
            for (var i = 0; i < trace.FrameCount && parts.Count < 3; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var type = method?.DeclaringType;
                if (type?.Namespace == "TurboTick") continue;
                parts.Add((type?.Name ?? "?") + "." + (method?.Name ?? "?"));
            }
            return parts.Count == 0 ? "?" : string.Join("<", parts);
        }
        catch (Exception) { return "?"; }
    }
}

/// <summary>Records why a zone cell change was or was not captured, and who asked for it.
///
/// Measured need: a stockpile zone appeared on one peer and not the other, and the capture point is
/// <c>Zone.AddCell</c>, which the designator does call. The only way a call there publishes nothing is the
/// <c>PlayerActing</c> gate refusing it, and a refused capture is indistinguishable from a missing patch unless the
/// gate decision is written down - the same ambiguity that cost a round on the forbid path.
/// </summary>
internal static class ZoneGate
{
    private const int Capacity = 48;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Record(string what, bool acting, string chain)
    {
        lock (Recent)
        {
            Recent.Add($"zonegate {what} {(acting ? "acting" : "not-acting")} {chain}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    internal static string CallerChain() => DirectForbidGate.CallerChain();
}

/// <summary>Records commands that were never sent, and why.
///
/// <c>RoomConnection.SendCommand</c> returned early when the socket or the session was not ready. That is a
/// defensible decision, but it left no trace at all: the local change was already applied, the player sees it, and
/// every other peer never learns about it. Measured: a 190-cell stockpile drawn on one peer produced 48 captured
/// zone-cell commands on that peer and zero on the other, with no log line anywhere.
///
/// A dropped input is worse than a refused one: a refusal can be diagnosed and repeated, a silent drop is
/// indistinguishable from "the game never tried".
/// </summary>
internal static class SendGate
{
    private const int Capacity = 48;

    private static readonly List<string> Recent = new(Capacity);

    private static int dropped;

    /// <summary>Total drops this session. Exposed so a dump header can carry one number instead of a list.</summary>
    internal static int Dropped => dropped;

    internal static void Record(string kind, long sequence, string reason)
    {
        dropped++;
        lock (Recent)
        {
            Recent.Add($"senddrop {kind} seq={sequence} {reason} tick={GenTicks.TicksGame}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
        // Once per reason, so a burst of 48 drops does not bury the log but the first one is impossible to miss.
        Log.WarningOnce($"[TurboTick send] 命令未发送（{reason}）：kind={kind} seq={sequence} "
            + "本地已生效、对端不会收到。", reason.GetHashCode());
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
        dropped = 0;
    }
}

/// <summary>Marks steps in the publish chain that are supposed to produce output.
///
/// Two instruments already watch this path and both stayed silent while a command disappeared: the send gate reports
/// drops at <c>SendCommand</c>, and a failed async send sets a status string. Neither can see the interval between
/// capture and send, so "both silent" read as "nothing wrong" - and the command was gone.
///
/// The rule this exists to enforce: a step that is meant to produce output must prove it was reached, not only that
/// it did not report a failure.
/// </summary>
internal static class PublishGate
{
    private const int Capacity = 48;

    private static readonly List<string> Recent = new(Capacity);

    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);

    internal static void Note(string what)
    {
        lock (Recent)
        {
            Recent.Add($"pubgate {what} tick={GenTicks.TicksGame}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
            Counts[what] = Counts.TryGetValue(what, out var count) ? count + 1 : 1;
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    /// <summary>Totals per step, so "reached begin but not sent" is one line instead of a diff.</summary>
    internal static string Counters()
    {
        lock (Recent)
        {
            if (Counts.Count == 0) return "pub=none";
            return "pub=" + string.Join(",", Counts.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key.Replace(' ', '_') + ":" + p.Value));
        }
    }

    internal static void Reset()
    {
        lock (Recent)
        {
            Recent.Clear();
            Counts.Clear();
        }
    }
}

/// <summary>Counts transport failures separately from gate drops.
///
/// A dropped command that never left the client and a command that failed mid-flight look the same from the server
/// (nothing arrives), but they need different fixes. The failed-send case used to be written only to a UI status
/// string, which never reaches the log, so it read as "nothing went wrong".
/// </summary>
internal static class SendFailureGate
{
    private const int Capacity = 32;

    private static readonly List<string> Recent = new(Capacity);

    internal static int Failed { get; private set; }

    internal static void Record(string kind, long sequence, Exception error)
    {
        Failed++;
        var text = $"sendfail {kind} seq={sequence} {error.GetType().Name}:{error.Message}";
        lock (Recent)
        {
            Recent.Add(text);
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
        Log.WarningOnce("[TurboTick send] " + text, error.GetType().Name.GetHashCode());
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    internal static string Counters() => "failed=" + Failed;
    }
