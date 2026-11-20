using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Storyteller adaptation, which decides how large a raid is - and which a replica never advances.
///
/// <para><b>Why a replica needs this.</b> Both counters grow inside <c>StoryWatcher.StoryWatcherTick</c>
/// (<c>:392454</c>), which is reached only from <c>MapPostTick</c> (<c>:52940</c>, <c>:165791</c>) - the same gate a display
/// replica stops at. <c>adaptDays</c> therefore stays at the value the save was loaded with, and it is not a display
/// number: <c>StorytellerUtility.DefaultThreatPointsNow</c> multiplies the whole raid budget by
/// <c>Mathf.Lerp(1f, watcherAdaptation.TotalThreatPointsFactor, difficulty.adaptationEffectFactor)</c>
/// (<c>:391870-391872</c>), so a long-running colony's raids are the wrong size on every replica. It is also plotted: the
/// history graph pulls <c>AdaptDays</c> through <c>HistoryAutoRecorderWorker_Adaptation.PullRecord</c> (<c>:362916</c>).</para>
///
/// <para><b>Why percent-of-max is not enough.</b> `pointsFactorFromAdaptDays` is an arbitrary curve
/// (<c>:392483</c>) and <c>adaptDaysMax</c>/<c>adaptDaysMin</c> come from the storyteller def, so a replica can only
/// reproduce the factor by evaluating the same curve at the same input. The value itself is what travels.</para>
///
/// <para><b>What the <em>second</em> counter feeds, which is not the same thing.</b>
/// <c>WatcherPopAdaptation.adaptDays</c> (`:392573`) reaches gameplay through
/// <c>StorytellerUtilityPopulation.CalculatePopulationIntent</c> (`:392188`):
/// <c>populationIntentFactorFromPopCurve.Evaluate(curPop) * populationIntentFactorFromPopDaysCurve.Evaluate(popAdaptation)</c>.
/// That figure is asked in 31 places, and not only decoration - <c>DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve</c>
/// (`:112851`, read by `HealthUtility` at `:136565`) decides whether a downed enemy dies instead of being capturable, and
/// `NonRecruitableChanceOverPopulationIntentCurve` (`:112871`) decides whether a prisoner can be recruited at all. A replica
/// holding a stale value therefore shows a different *rule* on the health/recruitment UI than the authority applies, and
/// the two counters would look identical in a single number - which is why the record carries both rather than a sum.</para>
///
/// <para><b>Why the pair is one record.</b> They are two counters of the same subsystem, ticked by the same call from the
/// same place, and neither is meaningful without knowing which one it is - so sending them as one row per save keeps the
/// frame to a single entry.</para>
internal static class AdaptationSync
{
    /// <summary>Quantised to a tenth of a day: the counters move by fractions of a day per 30000-tick step
    /// (<c>:392542</c>), and the factor is a curve over days, so a tenth is finer than the curve can express. The encoding
    /// doubles as the change key, so without this the row would resend on every frame a counter creeps.</summary>
    private const float Quantum = 0.1f;

    private static string? sent;
    private static float adaptDays;
    private static float popAdaptDays;

    internal static long Sends { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static string? LastSample { get; private set; }

    /// <summary>The current values, for the census and the probe. `adaptDays` has no public setter - it is advanced only
    /// inside `AdaptationWatcherTick` - so a probe cannot move it and has to read it instead.</summary>
    internal static float AdaptDays => adaptDays;
    internal static float PopAdaptDays => popAdaptDays;

    /// <summary>One row when either counter moved past the quantum, otherwise null.</summary>
    internal static List<ReplicaAdaptation>? Publish()
    {
        try
        {
            var watcher = Find.StoryWatcher;
            if (watcher?.watcherAdaptation is null || watcher.watcherPopAdaptation is null) return null;
            adaptDays = watcher.watcherAdaptation.AdaptDays;
            popAdaptDays = watcher.watcherPopAdaptation.AdaptDays;
            var encoded = Quantise(adaptDays).ToString("F1", CultureInfo.InvariantCulture)
                + "#" + Quantise(popAdaptDays).ToString("F1", CultureInfo.InvariantCulture);
            if (sent == encoded) return null;
            sent = encoded;
            LastSample = encoded;
            Sends++;
            return [new ReplicaAdaptation(adaptDays, popAdaptDays)];
        }
        catch (Exception) { return null; }
    }

    private static float Quantise(float days) => (float)Math.Round(days / Quantum) * Quantum;

    /// <summary>Writes the counters back through the private fields. `Debug_OffsetAdaptDays` exists (<c>:392557</c>,
    /// <c>:392608</c>) but adds rather than sets, so it cannot be used to place a value.</summary>
    internal static void Apply(ReplicaAdaptation state)
    {
        try
        {
            var watcher = Find.StoryWatcher;
            if (watcher?.watcherAdaptation is null || watcher.watcherPopAdaptation is null) return;
            SetField(watcher.watcherAdaptation, state.AdaptDays);
            SetField(watcher.watcherPopAdaptation, state.PopAdaptDays);
            adaptDays = state.AdaptDays;
            popAdaptDays = state.PopAdaptDays;
            ApplyCount++;
        }
        catch (Exception)
        {
            if (ApplyFailures++ < 8)
                Log.Warning("[TurboTick] 恢复适应度失败：" + state.AdaptDays.ToString("F1", CultureInfo.InvariantCulture));
        }
    }

    private static void SetField(object tracker, float days)
    {
        try { HarmonyLib.AccessTools.Field(tracker.GetType(), "adaptDays")?.SetValue(tracker, days); }
        catch (Exception) { }
    }

    /// <summary>Whether the colony has passed the grace period, for the census. Zero on both counters before that is a
    /// young colony, not a broken channel - and the two read identically in a single number.</summary>
    internal static string Grace => GenDate.DaysPassedSinceSettle < (Find.Storyteller?.def?.adaptDaysGameStartGraceDays ?? 0f)
        ? "宽限期内" : "已过宽限期";

    /// <summary>Clears the remembered value so a save reload does not compare against a world that no longer exists.</summary>
    internal static void Reset() => sent = null;
}
