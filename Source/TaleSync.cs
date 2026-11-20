using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TurboTick.Networking;
using Verse;
using RimWorld;

namespace TurboTick;

/// <summary>The colony's story log, applied on a replica's own <c>Tale</c> objects.
///
/// <para><b>Why a replica needs this.</b> <c>TaleRecorder.RecordTale</c> only runs on the authority, and
/// <c>TaleManager.Add</c> is what puts the result in the list the history page and <c>ArtGenerationContext</c> read. A replica
/// therefore holds exactly the tales the save was loaded with: the "art" descriptions never mention anything that happened
/// since, and the history page is frozen. <c>TaleManager.RemoveExpiredTales</c> is also authority-only, so an expirable tale
/// that has aged out still sits there.</para>
///
/// <para><b>Why the population is small enough to send whole.</b> Measured 4 tales on this save. A tale is a def name, an
/// interest figure and the pawn it is about - and it is the authority's own story, so a replica cannot derive one. This sends
/// the list every time it changes rather than inventing a diff protocol for four rows.</para>
///
/// <para><b>Why only <c>uses</c> and the age are needed for interest.</b> <c>Tale.InterestLevel</c> is a getter over
/// <c>def.baseInterest</c>, <c>uses</c> and <c>AgeTicks</c> - and <c>AgeTicks</c> is <c>TicksAbs - date</c>, which already
/// matches on both ends because <see cref="ReplicaAbsClockPatch"/> makes the replica's clock the host's. So the derived figure
/// needs no channel; only the counter the authority increments does.</para>
///
/// <para><b>Why a tale is never reconstructed here.</b> <c>TaleManager.Add</c> runs <c>CheckCullTales</c> and can drop other
/// tales to make room, and a created tale fires the tale's own def workers. A replica already has every tale that came with the
/// save, and this only writes the two fields the passing of time changes.</para></summary>
internal static class TaleSync
{
    /// <summary>Last standing sent per tale id.</summary>
    private static readonly Dictionary<int, TaleStanding> sent = [];

    internal static long TalesSent { get; private set; }
    internal static long TalesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    private static readonly AccessTools.FieldRef<Tale, int> usesField =
        AccessTools.FieldRefAccess<Tale, int>("uses");

    private static TaleStanding Standing(Tale tale) => new(usesField(tale), tale.date);

    /// <summary>Every tale whose standing differs from the last one sent, plus any tale the replica does not have yet.</summary>
    internal static List<ReplicaTale>? Publish()
    {
        try
        {
            var manager = Find.TaleManager;
            if (manager is null) return null;
            List<ReplicaTale>? result = null;
            var alive = new HashSet<int>();
            foreach (var tale in manager.AllTalesListForReading)
            {
                if (tale?.def is null) continue;
                alive.Add(tale.id);
                var standing = Standing(tale);
                if (sent.TryGetValue(tale.id, out var previous) && previous == standing) continue;
                sent[tale.id] = standing;
                (result ??= []).Add(new ReplicaTale(tale.id, tale.def.defName, standing.Uses, standing.Date,
                    tale.customLabel, tale.hidden ? (byte)1 : (byte)0));
                TalesSent++;
            }
            // A culled tale never needs its entry again.
            foreach (var stale in sent.Keys.Where(id => !alive.Contains(id)).ToList()) sent.Remove(stale);
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] tale publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes one tale's counter onto the replica's own copy.</summary>
    internal static void Apply(ReplicaTale state)
    {
        try
        {
            var manager = Find.TaleManager;
            if (manager is null) return;
            foreach (var tale in manager.AllTalesListForReading)
            {
                if (tale is null || tale.id != state.Id) continue;
                // A reused id is the one failure mode with no symptom: the two peers' story logs would silently start
                // describing each other's events.
                if (state.Def is { Length: > 0 } expected && tale.def?.defName != expected)
                {
                    ApplyFailures++;
                    if (ApplyFailures <= 6)
                        Log.Warning($"[TurboTick] tale apply id reuse ({state.Id}): replica holds "
                            + $"{tale.def?.defName ?? "?"}, frame says {expected}");
                    return;
                }
                var changed = false;
                if (state.Uses >= 0 && usesField(tale) != state.Uses)
                {
                    usesField(tale) = state.Uses;
                    changed = true;
                }
                if (state.Date >= 0 && tale.date != state.Date)
                {
                    tale.date = state.Date;
                    changed = true;
                }
                // Both are public fields on `Tale`.
                if (tale.customLabel != state.Label)
                {
                    tale.customLabel = state.Label;
                    changed = true;
                }
                if ((tale.hidden ? (byte)1 : (byte)0) != state.Hidden)
                {
                    tale.hidden = state.Hidden != 0;
                    changed = true;
                }
                if (changed) TalesApplied++;
                return;
            }
            // Not present on the replica. Rebuilding it would run `CheckCullTales` (which can evict other tales) and the
            // tale's own def workers, so it is left for the next save to carry like any other saved object.
            if (ApplyFailures++ <= 6)
                Log.Warning($"[TurboTick] tale apply missed ({state.Id}, {state.Def}): replica has no such tale");
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] tale apply failed ({state.Id}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => sent.Clear();

    internal static string Summary()
    {
        var total = 0;
        var unused = 0;
        try
        {
            foreach (var tale in Find.TaleManager?.AllTalesListForReading ?? [])
            {
                if (tale is null) continue;
                total++;
                if (tale.Unused) unused++;
            }
        }
        catch (Exception) { total = -1; }
        return $"{total} 条（未使用 {unused}）/ 发 {TalesSent} / 应用 {TalesApplied}（失败 {ApplyFailures}）";
    }
}

/// <summary>One tale's time-driven standing, kept for comparison so only real changes reach the wire.</summary>
internal readonly record struct TaleStanding(int Uses, int Date);
