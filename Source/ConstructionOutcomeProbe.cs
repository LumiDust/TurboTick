using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Counts construction outcomes and records the one stat that decides them.
///
/// <para>Why: four wall blueprints stood for tens of thousands of ticks while colonists carried two, then five, then
/// sixteen wood onto them, the deposit probe confirming `投递后 容器内 5` each time - and the map's wall count never
/// moved off 509. The blueprint's own id changed every couple of minutes (55993 → 56961 → 58168 → 58688), which means
/// the object was being destroyed and replaced, not merely waiting.</para>
///
/// <para>Only one path destroys a satisfied frame without building anything: <c>Frame.FailConstruction</c>, reached from
/// <c>JobDriver_ConstructFinishFrame</c> when <c>Rand.Value &lt; 1 - ConstructSuccessChance^(work/workToBuild)</c>.
/// With a success chance of 1 that expression is <c>&lt; 0</c> and can never fire; with a chance of 0 it is
/// <c>&lt; 1</c> and always fires. So a zero chance means every frame ever built is destroyed - a permanent
/// construction loop that looks exactly like "the materials arrive and nothing happens". Nothing in vanilla logs the
/// failure, so the only way to see it is to count it.</para></summary>
internal static class ConstructionOutcomeProbe
{
    private static long completed;
    private static long failed;
    private static long started;

    internal static void NoteStarted() => Interlocked.Increment(ref started);
    internal static void NoteCompleted() => Interlocked.Increment(ref completed);

    internal static void NoteFailed(Frame frame, Pawn? worker)
    {
        var count = Interlocked.Increment(ref failed);
        // Printed on every failure, not just the first few. The first version capped at six and the cap *hid* the
        // finding: the loop kept running, the counter stopped moving because `failed` was already above the cap, and
        // `建造结果 失败=0` on a fresh process read like "no failures at all". A construction loop is a few lines a
        // second while colonists are working, which is affordable; a silent one is not.
        var chance = -1f;
        var skill = -1;
        try { chance = worker?.GetStatValue(StatDefOf.ConstructSuccessChance) ?? -1f; } catch (Exception) { }
        try { skill = worker?.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? -1; } catch (Exception) { }
        Log.Warning($"[TurboTick 建造] 建造失败 #{count}：{frame.def.defName}#{frame.thingIDNumber}"
            + $" @{frame.Position.x},{frame.Position.z} 工人={worker?.LabelShort ?? "无"}"
            + $" 建筑技能={skill} 成功率={chance:0.00} 已完成工作={frame.workDone:0}/{frame.WorkToBuild:0}"
            // The random position is what separates "unlucky" from "impossible": this branch fires when
            // Rand.Value < 1 - chance^(num/work), and with chance 0.85 that is 0.3% per tick. Repetition means the
            // stream is not advancing, and the seed and draw count beside the failure are the only way to see that.
            + $" 随机流={RandPositionProbe.Sample()}"
            + "（失败后原地重建蓝图，材料按 50% 掉落，于是又有小人来搬 —— 这就是玩家看到的循环）");
    }

    internal static string Summary() =>
        $"建造结果 开工={Interlocked.Read(ref started)} 完成={Interlocked.Read(ref completed)}"
        + $" 失败={Interlocked.Read(ref failed)}";
}

[HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
internal static class FrameCompletedPatch
{
    // `CompleteConstruction` also covers work that finished without a worker, so the pair of patches bounds the
    // count from both ends: every frame that reaches the map passed one of the two methods, and a frame that reaches
    // neither is a frame nobody ever worked on.
    private static void Prefix(Frame __instance, Pawn worker) => ConstructionOutcomeProbe.NoteStarted();
    private static void Postfix(Frame __instance, Pawn worker) => ConstructionOutcomeProbe.NoteCompleted();
}

[HarmonyPatch(typeof(Frame), nameof(Frame.FailConstruction))]
internal static class FrameFailedPatch
{
    // Prefix, because the original destroys the frame: after it runs there is nothing left to describe.
    private static void Prefix(Frame __instance, Pawn worker) => ConstructionOutcomeProbe.NoteFailed(__instance, worker);
}
