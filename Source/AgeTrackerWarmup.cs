using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Warms the life-stage cache after a load, deterministically.
///
/// <c>Pawn_AgeTracker.ExposeData</c> sets <c>cachedLifeStageIndex = -1</c> in PostLoadInit (Verse/Pawn_AgeTracker.cs:418),
/// and <c>CurLifeStageIndex</c> recomputes it on first read. <c>RecalculateLifeStageIndex</c> treats "cached was
/// negative" as a change: it sets <c>lifeStageChange = true</c>, dirties graphics and calls
/// <c>CurLifeStage.Worker.Notify_LifeStageStarted</c> (:566-578).
///
/// That turns a cache invalidation into a state change, and the moment it happens depends on who first reads
/// <c>CurLifeStageIndex</c> - a GUI peer reads it while drawing, a headless peer only from AI code. Two peers
/// therefore resolve "the same" pawn at different ticks, `CurLifeStage.Worker` gates work types, and the
/// divergence shows up as the "工作" category. Measured on two same-tick checkpoints: one peer had 6 pawns
/// with <c>lifeStageChange</c> pending and the other 71, the 6 being a subset of the 71
/// (artifacts/audit/snapshot-diff: 884a403e vs 5b25fb9f).
///
/// Reading every spawned pawn's stage index once, in a fixed order, right after the load makes the timing a
/// property of the colony instead of of the frame. The flag is then put back to what the snapshot said, so the
/// pending resolutions the save actually carried still run on their own age tick.
/// </summary>
internal static class AgeTrackerWarmup
{
    private static FieldInfo? lifeStageChangeField;

    internal static void Run()
    {
        if (Find.Maps is not { Count: > 0 } maps) return;
        var changed = 0;
        var warmed = 0;
        var restored = 0;
        try
        {
            lifeStageChangeField = AccessTools.Field(typeof(Pawn_AgeTracker), "lifeStageChange");
            for (var m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                if (map is null || map.Disposed) continue;
                var pawns = map.mapPawns?.AllPawnsSpawned;
                if (pawns is null) continue;
                // Fixed order: thingIDNumber is the same on every peer.
                var ordered = new List<Pawn>(pawns);
                ordered.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
                for (var i = 0; i < ordered.Count; i++)
                {
                    var tracker = ordered[i]?.ageTracker;
                    if (tracker is null) continue;
                    var before = ReadFlag(tracker);
                    // Forces RecalculateLifeStageIndex if the cache is cold.
                    _ = tracker.CurLifeStageIndex;
                    warmed++;
                    var after = ReadFlag(tracker);
                    if (before || !after) { if (after != before) changed++; continue; }
                    // The flag went true only because the cache was cold; the snapshot did not have it.
                    if (lifeStageChangeField is not null)
                    {
                        lifeStageChangeField.SetValue(tracker, false);
                        restored++;
                    }
                }
            }
            if (changed > 0 || restored > 0)
                Log.Message($"[TurboTick] 年龄阶段缓存预热：{warmed} 个 pawn，撤销载入产生的待决议标记 {restored} 个");
            else
                Log.Message($"[TurboTick] 年龄阶段缓存预热：{warmed} 个 pawn，无载入产生的待决议标记");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 年龄阶段缓存预热失败：" + error.Message);
        }
    }

    private static bool ReadFlag(Pawn_AgeTracker tracker) =>
        lifeStageChangeField is not null && lifeStageChangeField.GetValue(tracker) is true;
}
