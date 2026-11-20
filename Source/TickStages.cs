using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Records how many values the shared random stream has drawn at fixed points of one tick, so a divergence can
/// be pinned to a stage instead of to "somewhere in this tick".
///
/// The per-tick probe proved the shape of the remaining problem: at tick 5593 one peer was two draws ahead, and
/// all 66 pawns' projections were byte-identical. Two draws, once, with no pawn visible in them - which rules out
/// anything that runs every tick (its difference would grow) and rules out the pawn path entirely.
///
/// Stage boundaries come from the vanilla pipeline (<c>Verse/TickManager.cs:361-472</c>):
/// <code>
/// MapPreTick -> MapUpdate-derived work -> tickListNormal/Rare/Long -> DateNotifier -> Scenario -> World
///   -> StoryWatcher -> GameEnder -> Storyteller -> TaleManager -> QuestManager -> WorldPostTick
///   -> MapPostTick (wild animal + wild plant + ...) -> History -> GameComponentTick -> LetterStack
/// </code>
/// Comparing the same stage between two peers names the subsystem that drew differently, which is worth more than
/// another field on a pawn.
/// </summary>
internal static class TickStages
{
    private static readonly List<(string Name, uint Draws)> Steps = new();

    internal static void Reset()
    {
        lock (Steps) Steps.Clear();
    }

    internal static void Mark(string name)
    {
        try
        {
            var draws = SimulationRandom.Iterations();
            lock (Steps) Steps.Add((name, draws));
        }
        catch (Exception) { }
    }

    /// <summary>The stages of the tick that was last sampled, as "stage=draws" tokens. Two peers must produce the
    /// same numbers; the first pair that differs names the stage.</summary>
    internal static string Describe()
    {
        lock (Steps)
        {
            if (Steps.Count == 0) return "stages=none";
            var parts = new List<string>(Steps.Count);
            for (var i = 0; i < Steps.Count; i++) parts.Add(Steps[i].Name + "=" + Steps[i].Draws);
            return "stages=" + string.Join(",", parts);
        }
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class TickStageBoundaryPatch
{
    private static void Prefix()
    {
        // One-variable experiment: with TURBOTICK_REACH_CLEAR_EVERY=1 the reachability cache is cleared before every
        // tick. Safe by construction (it only forces recomputation), so a drop in divergence rate is evidence for
        // the stale-cache hypothesis and no change is evidence against it.
        if (ReachProbe.ClearEveryTick) ReachProbe.ClearAll();
        TickStages.Reset();
        ThingDrawTrace.Reset();
        PawnSubsystemTrace.Reset();
        SocialAttemptTrace.Reset();
        MapPostStages.Reset();
        HerdProbe.Reset();
        FoodProbe.Reset();
        HaulProbe.Reset();
        SharedStructureProbe.Reset();
    }
}

/// <summary>After the thing tick lists have run: this is where pawns, animals and plants tick.</summary>
[HarmonyPatch(typeof(TickList), nameof(TickList.Tick))]
internal static class TickStageThingListsPatch
{
    private static void Postfix(TickList __instance)
    {
        if (!SharedColony.HasSharedWorld) return;
        // Only the normal list is reported: three marks per tick would be noise, and the normal list is the one
        // everything with pawn-level behaviour registers in.
        if (__instance.TicksIntervalOrDefault() == 1) TickStages.Mark("afterThings");
    }
}

/// <summary>Immediately before the storyteller decides what happens next.</summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.StorytellerTick))]
internal static class TickStageStorytellerPatch
{
    private static void Prefix() => TickStages.Mark("beforeStoryteller");

    private static void Postfix() => TickStages.Mark("afterStoryteller");
}

/// <summary>After <c>MapPostTick</c>, which is where the wild animal and wild plant spawners run.</summary>
[HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
internal static class TickStageMapPostPatch
{
    private static void Postfix(Map __instance)
    {
        if (!SharedColony.HasSharedWorld) return;
        TickStages.Mark("afterMapPost" + __instance.Index);
    }
}

/// <summary>The last stage that can draw before the tick ends.</summary>
[HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.GameComponentTick))]
internal static class TickStageGameComponentPatch
{
    private static void Postfix() => TickStages.Mark("afterGameComponents");
}

internal static class TickListExtensions
{
    /// <summary>The bucket count of a tick list, which equals its tick interval (1, 250 or 2000).</summary>
    internal static int TicksIntervalOrDefault(this TickList list)
    {
        try
        {
            var field = AccessTools.Field(typeof(TickList), "thingLists");
            if (field?.GetValue(list) is List<List<Thing>> buckets) return buckets.Count;
        }
        catch (Exception) { }
        return -1;
    }
}











