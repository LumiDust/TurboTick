using RimWorld;
using TurboTick.Networking;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>Turns a coordinator refusal into something a player can see and a log can be searched for.
///
/// Why this exists: the relay already answers every command it will not publish with a structured refusal, and until
/// now that answer only ended a pending request. A refused gameplay command therefore produced a line in the
/// coordinator's console and *nothing* on the player's screen - the shape of "I dragged a stockpile and it just did
/// not happen, twice". The refusal is now a vanilla message (transient, no layout change), a `Player.log` line, and a
/// short-lived HUD marker.
///
/// Deliberately not written into <see cref="RoomConnection.Status"/>: that string is event text that never expires, so
/// a single throttle would replace "In sync · tick N" for the rest of the session. The HUD marker expires instead.
internal static class RoomNotices
{
    /// <summary>How long the HUD keeps showing the last refusal. Long enough to be read, short enough that it does
    /// not become the permanent status.</summary>
    private const long VisibleMilliseconds = 5_000;

    private static string last = "";
    private static long lastAt = long.MinValue / 2;

    /// <summary>One refusal, already folded by the client's suppression window.</summary>
    internal static void OnNotice(ServerNotice notice)
    {
        var key = KeyFor(notice.Code);
        var text = key is null ? T("TurboTick_NoticeOther", notice.Message) : T(key);
        last = key is null ? notice.Message : text;
        lastAt = Environment.TickCount64;
        if (Current.ProgramState == ProgramState.Playing)
        {
            var suffix = notice.Folded > 0 ? " " + T("TurboTick_NoticeFolded", notice.Folded) : "";
            Messages.Message(text + suffix,
                notice.Severity == NoticeSeverity.Caution ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.NeutralEvent,
                false);
        }
        // Warning rather than Message on purpose: a refusal is an anomaly, and this project reviews runs by grepping
        // Player.log. The server's own wording is kept verbatim next to the code so both ends can be matched.
        Log.Warning($"[TurboTick] 服务端拒绝 code={notice.Code} folded={notice.Folded} server=\"{notice.Message}\"");
    }

    /// <summary>Short text for the HUD status slot, empty once the window has passed.</summary>
    internal static string HudText =>
        Recent ? T("TurboTick_HudRefused", last) : "";

    /// <summary>Whether a refusal is recent enough to override the HUD's phase line.</summary>
    internal static bool Recent => last.Length > 0 && Environment.TickCount64 - lastAt < VisibleMilliseconds;

    // Literal keys on purpose: the packaging test asserts that every "TurboTick_…" literal in Source/ has a
    // translation in both languages, so a typo here fails the build instead of shipping an untranslated string.
    private static string? KeyFor(string code) => code switch
    {
        "RateLimited" => "TurboTick_NoticeRateLimited",
        "UnknownCommand" => "TurboTick_NoticeUnknownCommand",
        "NotPlaying" => "TurboTick_NoticeNotPlaying",
        "InvalidCommand" => "TurboTick_NoticeInvalidCommand",
        "SimulationReadOnly" => "TurboTick_NoticeSimulationReadOnly",
        _ => null
    };
}
