using System;
using System.Collections.Generic;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>How long a pawn has been miserable, for a replica whose misery never accumulates.
///
/// <para><b>Why a replica needs this.</b> <c>MentalBreaker.MentalBreakerTickInterval</c> runs on a 150-tick interval: while mood
/// is below each threshold it adds 150 to <c>ticksBelowMinor</c>/<c>Major</c>/<c>Extreme</c>, and it resets the counter the
/// moment mood recovers. Nothing else writes them. A replica ticks no pawns, so all three sit at whatever the save held - which
/// means <c>BreakExtremeIsImminent</c> / <c>BreakMajorIsImminent</c> / <c>BreakMinorIsImminent</c>, and therefore the flashing
/// head glow <c>PawnRenderUtility.RenderStatusOverlays</c> draws over a colonist about to break, are decided from stale
/// counters. The player reads "no warning, then a break" - or the opposite, a permanent red glow on someone who is fine.</para>
///
/// <para><b>Why this cannot be derived.</b> The counters are not a function of current mood: they are the integral of mood over
/// time, and the integral resets at zero on every recovery. Two pawns at the same mood have different counters, and the same pawn
/// at the same mood has a different counter depending on how long it has been there. Unlike the age channel - where the setter
/// recomputes the life stage - there is nothing here a replica could recompute from.</para>
///
/// <para><b>Why the whole triple travels and not just the largest.</b> <c>BreakMinorIsImminent</c> is false while
/// <c>BreakMajorIsImminent</c> is true, and <c>BreakExtremeIsApproaching</c> additionally checks a mood window. Sending only the
/// highest counter would make the three flags agree only in the case that never needed a channel.</para>
/// </summary>
internal static class MentalBreakerSync
{
    /// <summary>Last state sent per pawn: the three counters quantised to whole 150-tick steps.</summary>
    private static readonly Dictionary<int, (int Minor, int Major, int Extreme)> sent = [];

    /// <summary>The game's own interval; a counter can only ever hold a multiple of it.</summary>
    private const int Step = 150;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
    }

    internal static List<ReplicaMentalBreaker>? Publish()
    {
        List<ReplicaMentalBreaker>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var breaker = pawn?.mindState?.mentalBreaker;
                    if (breaker is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var minor = Round(Read(minorField, breaker));
                    var major = Round(Read(majorField, breaker));
                    var extreme = Round(Read(extremeField, breaker));
                    // All zero is the overwhelmingly common state (a colony at healthy mood). Skipping it entirely keeps the
                    // channel free, and a recovery is still observed because the transition *away* from a non-zero value
                    // produces a frame - see the removal below.
                    if (minor == 0 && major == 0 && extreme == 0)
                    {
                        if (sent.Remove(id)) (changed ??= []).Add(new ReplicaMentalBreaker(id, 0, 0, 0));
                        continue;
                    }
                    if (sent.TryGetValue(id, out var last) && last == (minor, major, extreme)) continue;
                    sent[id] = (minor, major, extreme);
                    (changed ??= []).Add(new ReplicaMentalBreaker(id, minor, major, extreme));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    /// <summary>Rounds down to the game's own step. A counter mid-interval is not a state the game itself can observe.</summary>
    private static int Round(int ticks) => ticks <= 0 ? 0 : ticks - ticks % Step;

    // The three counters are private with no accessor of any kind - `Reset()` only zeroes them and nothing reads them outside
    // this class - so they are reached by field. Cached once: this runs per pawn per frame on a colony of any size.
    private static readonly System.Reflection.FieldInfo? minorField =
        HarmonyLib.AccessTools.Field(typeof(MentalBreaker), "ticksBelowMinor");
    private static readonly System.Reflection.FieldInfo? majorField =
        HarmonyLib.AccessTools.Field(typeof(MentalBreaker), "ticksBelowMajor");
    private static readonly System.Reflection.FieldInfo? extremeField =
        HarmonyLib.AccessTools.Field(typeof(MentalBreaker), "ticksBelowExtreme");

    private static int Read(System.Reflection.FieldInfo? field, object target)
        => field?.GetValue(target) is int value ? value : 0;

    private static void Write(System.Reflection.FieldInfo? field, object target, int value)
        => field?.SetValue(target, value);

    internal static void Apply(ReplicaMentalBreaker state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var breaker = pawn.mindState?.mentalBreaker;
            if (breaker is null) return;
            Write(extremeField, breaker, state.Extreme);
            Write(majorField, breaker, state.Major);
            Write(minorField, breaker, state.Minor);
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }
}
