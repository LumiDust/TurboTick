using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Records the <c>delta</c> and the failure threshold the construct-finish toil actually used.
///
/// <para>Why: on the live authority every wall failed construction at <c>workDone</c> 0–7 of 95 with a success chance
/// of 0.85. Vanilla's branch is <c>Rand.Value &lt; 1 - Pow(chance, num / workToBuild)</c> with
/// <c>num = ConstructionSpeed * 1.7 * delta</c>, so the threshold is a function of <c>delta</c> and of nothing else
/// that changes: at delta=1 it is 0.003, at delta=60 it is 0.19, and once <c>num</c> reaches a few times
/// <c>workToBuild</c> it approaches 1 - making a failure certain rather than unlucky. Six consecutive failures at 0.3%
/// is not luck, and the only number that explains it is delta.</para>
///
/// <para>Wraps <c>tickIntervalAction</c> and calls the original unchanged: this probe observes the argument, it does
/// not draw from the shared stream and does not reorder anything. That distinction is why the earlier attempt at
/// sampling <c>Rand.Value</c> inside the toil was discarded - it consumed a draw and shifted every later one.</para></summary>
internal static class ConstructDeltaProbe
{
    private static long intervalCalls;
    private static int worstDelta;
    private static float worstThreshold;
    private static readonly List<string> recent = [];

    internal static void Note(int delta, float threshold, float workDone, float workToBuild, float chance)
    {
        var count = Interlocked.Increment(ref intervalCalls);
        if (delta > worstDelta) worstDelta = delta;
        if (threshold > worstThreshold) worstThreshold = threshold;
        // Logged for the first calls of every process and for anything with a delta above one, because a delta above
        // one is itself the anomaly: vanilla ticks a driver once per tick.
        if (count > 8 && delta <= 1) return;
        var line = $"delta={delta} 阈值={threshold:0.0000} 进度={workDone:0}/{workToBuild:0} 成功率={chance:0.00}";
        lock (recent)
        {
            recent.Add(line);
            if (recent.Count > 20) recent.RemoveAt(0);
        }
        Log.Warning($"[TurboTick 建造] 建造间隔 #{count}：{line}");
    }

    internal static string Summary()
    {
        var total = Interlocked.Read(ref intervalCalls);
        if (total == 0) return "建造间隔 无（建造 toil 没跑过）";
        var text = $"建造间隔 {total} 次 最大delta={worstDelta} 最大阈值={worstThreshold:0.0000}";
        lock (recent)
            if (recent.Count > 0) text += " 最近=" + recent[^1];
        return text;
    }
}

[HarmonyPatch(typeof(JobDriver_ConstructFinishFrame), "MakeNewToils")]
internal static class ConstructToilDeltaPatch
{
    private static readonly AccessTools.FieldRef<JobDriver, Pawn> PawnOf =
        AccessTools.FieldRefAccess<JobDriver, Pawn>("pawn");

    private static void Postfix(ref IEnumerable<Toil> __result, JobDriver_ConstructFinishFrame __instance)
    {
        var original = __result;
        __result = Wrap(original, __instance);
    }

    private static IEnumerable<Toil> Wrap(IEnumerable<Toil> toils, JobDriver_ConstructFinishFrame driver)
    {
        foreach (var toil in toils)
        {
            if (toil?.tickIntervalAction is not { } inner) { yield return toil; continue; }
            toil.tickIntervalAction = delta =>
            {
                try
                {
                    var pawn = PawnOf(driver);
                    // `Frame` is a private property on the driver (RimWorld/JobDriver_ConstructFinishFrame.cs:14:
                    // `private Frame Frame => (Frame)job.GetTarget(TargetIndex.A).Thing;`), so the target is read the
                    // same way vanilla reads it rather than through reflection.
                    var frame = driver.job?.GetTarget(TargetIndex.A).Thing as Frame;
                    var work = frame?.WorkToBuild ?? 95f;
                    var chance = pawn?.GetStatValue(StatDefOf.ConstructSuccessChance) ?? 0.85f;
                    // Verbatim vanilla arithmetic (RimWorld/JobDriver_ConstructFinishFrame.cs:53-61):
                    //   num = ConstructionSpeed * 1.7f * delta;  num *= Stuff.ConstructionSpeedFactor
                    //   if (Rand.Value < 1 - Pow(chance, num / workToBuild)) -> FailConstruction
                    // `ConstructionSpeed`, not `WorkSpeedGlobal`: using the global work speed here produced a threshold
                    // that no observed draw could ever fall below while failures were still being counted, which is a
                    // probe that quietly disagrees with the code it claims to describe.
                    var num = (pawn?.GetStatValue(StatDefOf.ConstructionSpeed) ?? 1f) * 1.7f * delta;
                    if (frame?.Stuff is { } stuff) num *= stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor);
                    var threshold = 1f - MathF.Pow(chance, num / MathF.Max(work, 0.01f));
                    ConstructDeltaProbe.Note(delta, threshold, frame?.workDone ?? -1f, work, chance);
                }
                catch (Exception) { }
                inner(delta);
            };
            yield return toil;
        }
    }
}
