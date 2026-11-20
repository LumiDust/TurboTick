using System;
using System.Collections.Generic;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>How old a pawn is, for a replica whose age never advances.
///
/// <para><b>Why a replica needs this.</b> <c>Pawn_AgeTracker.TickBiologicalAge</c> is the only thing that advances
/// <c>ageBiologicalTicksInt</c>, and it is reached from <c>AgeTickInterval</c> inside the pawn's own tick. A replica ticks no
/// pawns, so every pawn keeps the age the save was loaded with - and because the character card, the "age" text rule, the
/// age-based capacity curves and the breeding/age checks all read <c>AgeBiologicalYears</c> straight off that counter, the
/// whole colony reads as one long tick older than it was.</para>
///
/// <para><b>Why only the counter travels.</b> <c>AgeBiologicalTicks</c> has a setter that also runs
/// <c>CalculateInitialGrowth()</c> and <c>RecalculateLifeStageIndex()</c>, and the life stage is a pure function of the age
/// and the def's <c>lifeStageAges</c>. So sending the age realigns the life stage for free, and sending the life stage
/// separately would be a second chance for the two to disagree. <c>growth</c> is not derivable that way - it comes from a
/// randomised <c>PlaceholderForGrowth</c> pass - so it is carried too, but only when it is meaningful (a growth-capable
/// pawn), because for an adult it is a constant the save already had.</para>
///
/// <para><b>Why not shift the stamps instead.</b> <c>ClockStampShift</c> already moves every "when did this happen" field by
/// the load delta, and it deliberately excludes lengths and counters (<c>ageTicks</c>, <c>growthTicks</c> are on its exclusion
/// list). Age is a length: shifting it would make the pawn older by however long the save sat on disk, which is the opposite
/// of correct.</para>
/// </summary>
internal static class PawnAgeSync
{
    /// <summary>Last age sent per pawn, so an unchanged age costs nothing. Ticks, not years: the year the player reads is a
    /// division of this, and a pawn can cross a birthday between two frames.</summary>
    private static readonly Dictionary<int, long> sent = [];

    internal static long AgesSent { get; private set; }
    /// <summary>The last record published, printed by the census so the encoded shape - not just the count - can be
    /// read. A count of 71 says the publish path ran; it says nothing about whether `Reversal` survived encoding.</summary>
    internal static ReplicaPawnAge? LastSample { get; private set; }
    internal static long AgesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        AgesSent = 0;
        LastSample = null;
        AgesApplied = 0;
        ApplyFailures = 0;
    }

    /// <summary>One entry per pawn whose age moved. Quantised to whole days: the player reads years, the capacity curves move
    /// by fractions of a year, and a day is already 1/60 of what a year can show.</summary>
    private const long Quantum = 60000;

    internal static List<ReplicaPawnAge>? Publish()
    {
        List<ReplicaPawnAge>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var tracker = pawn?.ageTracker;
                    if (tracker is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var age = tracker.AgeBiologicalTicks;
                    if (age <= 0) continue;
                    var quantised = age - age % Quantum;
                    // `growth` only means something for a pawn that grows; -1 is the game's own "not applicable" and is sent
                    // as such rather than as a zero, which would read as "no growth at all".
                    var growth = tracker.Growth >= 0f ? tracker.Growth : -1f;
                    // `growthPoints` decides `GrowthTier` and `PercentToNextGrowthTier` (`:134758-134785`), and only
                    // `CalculateGrowth` writes it - from the pawn's own tick. A replica therefore shows the tier the
                    // save was loaded with and a bar that never fills. Quantised to whole points: the requirements are
                    // in the tens and the bar is a few hundred pixels wide.
                    var points = growthPointsField?.GetValue(tracker) as float? ?? -1f;
                    var pointsWhole = points < 0f ? -1L : (long)Math.Round(points);
                    // The reversal deadline is an absolute age stamp that the game compares against the biological
                    // age, and `long.MaxValue` means "never demanded". Converted to remaining ticks here so the value
                    // survives the age being quantised to whole days.
                    var demandAt = reversalField?.GetValue(tracker) as long? ?? long.MaxValue;
                    var reversal = demandAt == long.MaxValue ? -1L : demandAt - tracker.AgeBiologicalTicks;
                    // The change key has to cover all of them or a growth point arriving on the same day as nothing
                    // else would never be sent. Multiplication keeps the three fields from colliding.
                    var key = quantised * 4
                        + (growth < 0f ? 0 : Math.Clamp((int)Math.Round(growth * 3f), 1, 3))
                        + pointsWhole * 16
                        + reversal;
                    if (sent.TryGetValue(id, out var last) && last == key) continue;
                    sent[id] = key;
                    (changed ??= []).Add(new ReplicaPawnAge(id, quantised, growth, points, reversal));
                    AgesSent++;
                    if (LastSample is null) LastSample = new ReplicaPawnAge(id, quantised, growth, points, reversal);
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    private static readonly System.Reflection.FieldInfo? growthField =
        HarmonyLib.AccessTools.Field(typeof(Pawn_AgeTracker), "growth");
    /// <summary>`growthPoints` is public, but it is fetched through the same cached helper as the private fields so
    /// a rename in either place fails the same silent way instead of half the channel working.</summary>
    private static readonly System.Reflection.FieldInfo? growthPointsField =
        HarmonyLib.AccessTools.Field(typeof(Pawn_AgeTracker), "growthPoints");
    /// <summary>The absolute age stamp at which an age reversal is demanded - `private long`, no accessor.</summary>
    private static readonly System.Reflection.FieldInfo? reversalField =
        HarmonyLib.AccessTools.Field(typeof(Pawn_AgeTracker), "ageReversalDemandedAtAgeTicks");

    internal static void Apply(ReplicaPawnAge state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var tracker = pawn.ageTracker;
            if (tracker is null) return;
            // The setter, not the backing field: it runs `CalculateInitialGrowth` and `RecalculateLifeStageIndex`, which is
            // exactly what turns a new age into a new life stage and a new set of capacity curves.
            if (state.AgeTicks > 0 && tracker.AgeBiologicalTicks != state.AgeTicks)
                tracker.AgeBiologicalTicks = state.AgeTicks;
            // `Growth` is a getter over a private field with no public setter, and there is no method that sets it from a
            // value - the game only ever computes it. Written by reflection, cached, and silently skipped if the field ever
            // moves: a wrong growth is a cosmetic mismatch, a thrown exception here would stop the whole frame.
            if (state.Growth >= 0f) growthField?.SetValue(tracker, state.Growth);
            if (state.GrowthPoints >= 0f) growthPointsField?.SetValue(tracker, state.GrowthPoints);
            // Back to an absolute stamp, because that is what the game compares against. `-1` means the authority has no
            // demand, and in that case the field is left alone rather than set to `long.MaxValue`: the tracker's own
            // `ExposeData` treats `long.MaxValue` as "uninitialised" and calls `ResetAgeReversalDemand` on it
            // (`:134911`), which would invent a demand that the authority does not have. A negative deadline would be
            // just as wrong in the other direction - it reads as "past deadline" and sends the pawn to a biosculpter.
            if (state.Reversal >= 0L)
                reversalField?.SetValue(tracker, tracker.AgeBiologicalTicks + state.Reversal);
            AgesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }
}
