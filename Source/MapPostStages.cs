using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace TurboTick;

/// <summary>
/// Splits <c>MapPostTick</c> into its components, because the draw counter said the difference lives inside it.
///
/// The third long run diverged at tick 81271 with every stage up to <c>afterStoryteller</c> identical and
/// <c>afterMapPost0</c> 136 draws apart (one peer +212, the other +76). No <c>draw</c> row was recorded that tick,
/// so the values were not spent by any <c>Thing.DoTick</c> - they were spent by something
/// <c>Map.MapPostTick</c> calls (<c>Verse/Map.cs:966+</c>):
/// <code>
/// wildAnimalSpawner.WildAnimalSpawnerTick()
/// wildPlantSpawner.WildPlantSpawnerTick()
/// powerNetManager.PowerNetsTick()
/// steadyEnvironmentEffects.SteadyEnvironmentEffectsTick()
/// tempTerrain.Tick() / gasGrid.Tick() / pollutionGrid.PollutionTick()
/// deferredSpawner.DeferredSpawnerTick()
/// lordManager.LordManagerTick()
/// passingShipManager.PassingShipManagerTick()
/// </code>
/// One mark per component names the one that drew differently.
/// </summary>
internal static class MapPostStages
{
    private static readonly List<(string Name, uint Draws)> Steps = new(9);

    internal static void Reset()
    {
        lock (Steps) Steps.Clear();
    }

    internal static void Mark(string name)
    {
        if (!SharedColony.HasSharedWorld) return;
        try
        {
            var draws = SimulationRandom.Iterations();
            lock (Steps) Steps.Add((name, draws));
        }
        catch (Exception) { }
    }

    internal static string Describe()
    {
        lock (Steps)
        {
            if (Steps.Count == 0) return "mappost=none";
            var parts = new List<string>(Steps.Count);
            for (var i = 0; i < Steps.Count; i++) parts.Add(Steps[i].Name + "=" + Steps[i].Draws);
            return "mappost=" + string.Join(",", parts);
        }
    }
}

/// <summary>Installs one postfix per component of <c>MapPostTick</c>.</summary>
internal static class MapPostStagePatches
{
    private static readonly (Type Type, string Method, string Name)[] Specs =
    [
        (typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick), "mpAnimal"),
        (typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick), "mpPlant"),
        (typeof(PowerNetManager), nameof(PowerNetManager.PowerNetsTick), "mpPower"),
        (typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick), "mpEnv"),
        (typeof(DeferredSpawner), nameof(DeferredSpawner.DeferredSpawnerTick), "mpDeferred"),
        (typeof(LordManager), nameof(LordManager.LordManagerTick), "mpLord"),
        (typeof(PassingShipManager), nameof(PassingShipManager.PassingShipManagerTick), "mpShip"),
    ];

    private static readonly Dictionary<MethodBase, string> Names = new();

    internal static void Install(Harmony harmony)
    {
        var postfix = new HarmonyMethod(typeof(MapPostStagePatches)
            .GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
        foreach (var (type, method, name) in Specs)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target is null)
                {
                    Log.Warning($"[TurboTick] 找不到 {type.Name}.{method}，MapPost 分段缺 {name}。");
                    continue;
                }
                Names[target] = name;
                harmony.Patch(target, postfix: postfix);
            }
            catch (Exception error)
            {
                Log.Warning($"[TurboTick] MapPost 分段 {name} 未挂载：{error.Message}");
            }
        }
    }

    private static void Postfix(MethodBase __originalMethod) =>
        MapPostStages.Mark(Names.TryGetValue(__originalMethod, out var name) ? name : "mp?");
}

