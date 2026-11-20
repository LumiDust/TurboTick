using System;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Carries the anomaly half of `Difficulty`, which the anomaly settings dialog writes.
///
/// <para><b>The defect (round 150).</b> `Dialog_AnomalySettings.DoWindowContents` (`:625367-625386`) writes five fields
/// of `Find.Storyteller.difficulty` on Accept: `overrideAnomalyThreatsFraction`, `anomalyThreatsInactiveFraction`,
/// `anomalyThreatsActiveFraction`, `studyEfficiencyFactor` and the `AnomalyPlaystyleDef` property. Nothing in
/// `Source/` mentioned `difficulty` at all, so the edit stayed on whichever peer opened the dialog.</para>
///
/// <para><b>Who reads them.</b> `GameComponent_Anomaly.AnomalyThreatFractionNow` (`:723349-723372`) is the whole
/// downstream path: it returns 0 when the playstyle disables anomaly content, the override when the playstyle uses one,
/// and otherwise `anomalyThreatsInactiveFraction` / `anomalyThreatsActiveFraction` scaled by the current monolith
/// level's factor. `Storyteller.AnomalyIncidentChanceNow` (`:390002`) is its only caller, and incident selection is the
/// host's job - so a stale replica does not pick the wrong incidents, it draws a wrong threat bar and answers every
/// "when will the next anomaly event fire" question differently. That is the same class of divergence as the bossgroup
/// cooldown: one peer's screen disagreeing with the simulation the other peer is running.</para>
///
/// <para><b>Why four floats and a def, and not the whole `Difficulty`.</b> `Difficulty` scribes dozens of fields
/// (`:380440` onward) that are set once at new-game time and never edited by a dialog; only the anomaly page has a
/// player-facing writer. Carrying the rest would grow every frame and pretend coverage the save already provides: both
/// peers load the same difficulty table at join, and only *edits* can diverge.</para>
///
/// <para><b>Why a signature string and not a field-by-field diff.</b> The row is one record per game, so the question is
/// only "did any of the five move". Formatting them into one string answers that in a line and keeps the counter honest
/// (`ChangesSent` counts real edits, not frames).</para></summary>
internal static class DifficultySync
{
    private static string? sent;

    internal static long ChangesSent { get; private set; }
    internal static long ChangesApplied { get; private set; }
    internal static int Unresolved { get; private set; }
    internal static int ApplyFailures { get; private set; }

    private static Difficulty? Current
    {
        get
        {
            try { return Find.Storyteller?.difficulty; }
            catch { return null; }
        }
    }

    /// <summary>One row when any of the five values moved, otherwise null. Returns null on the first call once the frame
    /// carries it - `sent` starts unset so the first publish always describes the state.</summary>
    internal static ReplicaAnomalyDifficulty? Publish()
    {
        try
        {
            var difficulty = Current;
            if (difficulty is null) return null;
            var playstyle = difficulty.AnomalyPlaystyleDef?.defName ?? "-";
            var signature = playstyle
                + "|" + Float(difficulty.overrideAnomalyThreatsFraction)
                + "|" + Float(difficulty.anomalyThreatsInactiveFraction)
                + "|" + Float(difficulty.anomalyThreatsActiveFraction)
                + "|" + Float(difficulty.studyEfficiencyFactor);
            if (sent == signature) return null;
            sent = signature;
            ChangesSent++;
            return new ReplicaAnomalyDifficulty(playstyle, difficulty.overrideAnomalyThreatsFraction,
                difficulty.anomalyThreatsInactiveFraction, difficulty.anomalyThreatsActiveFraction,
                difficulty.studyEfficiencyFactor);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 异象难度发布失败：" + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes the five values the dialog writes. The playstyle goes through the property (`:380225-380233`),
    /// because `anomalyPlaystyleDef` is private and the getter substitutes `Standard` when it is null.</summary>
    internal static void Apply(ReplicaAnomalyDifficulty state)
    {
        try
        {
            var difficulty = Current;
            if (difficulty is null) { ApplyFailures++; return; }
            if (state.Playstyle is { Length: > 0 } name && name != "-")
            {
                var playstyle = DefDatabase<AnomalyPlaystyleDef>.GetNamedSilentFail(name);
                if (playstyle is null) Unresolved++;
                else if (difficulty.AnomalyPlaystyleDef != playstyle) difficulty.AnomalyPlaystyleDef = playstyle;
            }
            if (difficulty.overrideAnomalyThreatsFraction != state.Override)
                difficulty.overrideAnomalyThreatsFraction = state.Override;
            if (difficulty.anomalyThreatsInactiveFraction != state.Inactive)
                difficulty.anomalyThreatsInactiveFraction = state.Inactive;
            if (difficulty.anomalyThreatsActiveFraction != state.Active)
                difficulty.anomalyThreatsActiveFraction = state.Active;
            if (difficulty.studyEfficiencyFactor != state.Study)
                difficulty.studyEfficiencyFactor = state.Study;
            ChangesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] 异象难度应用失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => sent = null;

    /// <summary>`GenDate`-independent invariant formatting: an `override` that is null prints `null`, so "no override" and
    /// "override of 0" stay distinguishable in the signature.</summary>
    private static string Float(float? value) => value?.ToString("0.####") ?? "null";

    internal static string Summary()
    {
        var difficulty = Current;
        if (difficulty is null) return $"发 {ChangesSent} / 应用 {ChangesApplied}（未解析 {Unresolved}、失败 {ApplyFailures}）";
        return $"发 {ChangesSent} / 应用 {ChangesApplied}"
            + $"（未解析 {Unresolved}、失败 {ApplyFailures}、当前 {difficulty.AnomalyPlaystyleDef?.defName ?? "无"}"
            + $"/{Float(difficulty.anomalyThreatsInactiveFraction)}/{Float(difficulty.anomalyThreatsActiveFraction)}"
            + $"/{Float(difficulty.overrideAnomalyThreatsFraction)}/{Float(difficulty.studyEfficiencyFactor)}）";
    }
}
