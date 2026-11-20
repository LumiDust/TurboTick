using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Records whether the food search succeeded and what it returned, because that is where the fourth measured
/// divergence landed.
///
/// Measured (tick 71030): two peers had the same hunger (0.186), the same position and the same think tick, but one
/// pawn chose `Ingest` from `GetFood` and the other fell through to `WanderColony`, spending 143 draws against 54.
/// Equal hunger rules out the priority check (<c>JobGiver_GetFood.GetPriority</c>,
/// <c>RimWorld/JobGiver_GetFood.cs:25-49</c>) and the think tree is static, so the difference is the search itself
/// returning a source on one peer and nothing on the other.
///
/// <c>FoodUtility.BestFoodSourceOnMap</c> (<c>RimWorld/FoodUtility.cs:381+</c>) has no randomness of its own, so a
/// different answer means a different <em>input</em>: reservations, forbidden flags, freshness, reachability, or
/// which things the search visits. Recording the result and its reservation state separates those cases without
/// re-implementing the predicate - re-implementing it would report differences the game itself never saw, which has
/// already produced one false lead in this project.
/// </summary>
internal static class FoodProbe
{
    private const int Capacity = 48;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    private static void Add(string line)
    {
        lock (Recent)
        {
            Recent.Add(line);
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static void Enter(Pawn pawn, bool animalOrWildMan, bool starving, float level)
    {
        Add($"food:try {pawn.thingIDNumber} wild={animalOrWildMan} starving={starving} level="
            + level.ToString("0.###", CultureInfo.InvariantCulture));
    }

    internal static void Result(Pawn getter, Pawn eater, Thing? source, string detail)
    {
        var text = $"food:res {getter.thingIDNumber} "
            + $"{(source is null ? "-" : source.thingIDNumber.ToString(CultureInfo.InvariantCulture) + ":" + source.def.defName)} "
            + $"eater={eater.thingIDNumber} {detail}";
        Add(text.Trim());
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

/// <summary>Wraps <c>JobGiver_GetFood.TryGiveJob</c>: the point where the two peers parted.</summary>
[HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
internal static class FoodGiveJobPatch
{
    private static void Prefix(Pawn pawn)
    {
        if (!ThingDrawTrace.Enabled) return;
        try
        {
            FoodProbe.Enter(pawn, pawn.AnimalOrWildMan(),
                pawn.needs?.food?.CurCategory == HungerCategory.Starving,
                pawn.needs?.food?.CurLevel ?? -1f);
        }
        catch (Exception) { }
    }

    private static void Postfix(Pawn pawn, Job? __result)
    {
        if (!ThingDrawTrace.Enabled) return;
        try
        {
            if (__result is not null)
                FoodProbe.Result(pawn, pawn, __result.targetA.Thing, "job=" + __result.def.defName);
        }
        catch (Exception) { }
    }
}

/// <summary>
/// Records each <c>BestFoodSourceOnMap</c> answer together with the state that most plausibly changes it: whether
/// the source is reserved, forbidden, and whether the eater is the getter.
/// </summary>
[HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.BestFoodSourceOnMap))]
internal static class FoodSearchPatch
{
    private static void Postfix(Pawn getter, Pawn eater, bool desperate, Thing __result, ThingDef foodDef)
    {
        if (!ThingDrawTrace.Enabled) return;
        try
        {
            var map = getter.Map;
            var reserved = __result is not null && map is not null
                && map.reservationManager.IsReservedByAnyoneOf(__result, getter.Faction);
            var forbidden = __result is not null && map is not null && __result.IsForbidden(getter);
            var reachable = __result is not null && map is not null
                && map.reachability.CanReach(getter.Position, __result, PathEndMode.OnCell,
                    TraverseParms.For(getter, Danger.Some));
            FoodProbe.Result(getter, eater, __result,
                $"desperate={desperate} def={foodDef?.defName ?? "-"} "
                + $"reserved={reserved} forbidden={forbidden} reach={reachable}");
        }
        catch (Exception) { }
    }
}
