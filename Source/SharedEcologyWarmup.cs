using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// The wild-plant scan's cycle is <em>already</em> part of the save, so a load restores the same phase on every
/// peer - but the density cache behind the plant-choice weights is not trustworthy across a load:
/// `hasWholeMapNumDesiredPlantsCalculated` is scribed with <c>defaultValue: true</c>
/// (<c>RimWorld/WildPlantSpawner.cs:266</c>) and <c>Scribe_Values.Look</c> skips writing a value equal to its
/// default (<c>Verse/Scribe_Values.cs:29-32</c>), so a peer can load with the flag set and the numbers next to
/// it stale. The measured symptom was identical RNG, one plant, two different defs
/// (`Plant_SaguaroCactus` on one peer, `Plant_Grass` on the other, same cell, same random value).
///
/// This class therefore only forces the density cache to be recomputed from the loaded map, which is a pure
/// function of that map and so lands on the same numbers everywhere.
///
/// It deliberately does <b>not</b> reset <c>cycleIndex</c> or the running totals. An earlier version did, and
/// two peers that finish loading on different ticks then reset at different points of the shared timeline: the
/// dump showed <c>cycleIndex</c> 3150 against 3157, exactly one scan's worth (7 cells on a 250x250 map), with
/// identical density values. One extra scan also consumes up to two RNG draws per cell, so the divergence moved
/// from `物体` to `位置,随机`. The cycle is already serialised; leaving it alone keeps the phase shared.
/// </summary>
internal static class SharedEcologyWarmup
{
    private static readonly AccessTools.FieldRef<WildPlantSpawner, bool> Calculated =
        AccessTools.FieldRefAccess<WildPlantSpawner, bool>("hasWholeMapNumDesiredPlantsCalculated");

    internal static void Run()
    {
        if (Find.Maps is not { Count: > 0 } maps) return;
        var touched = 0;
        try
        {
            for (var i = 0; i < maps.Count; i++)
            {
                var spawner = maps[i]?.wildPlantSpawner;
                if (spawner is null) continue;
                // Recompute the density from the map that was just loaded instead of trusting a flag that may
                // have been restored with a stale value next to it.
                Calculated(spawner) = false;
                touched++;
            }
            if (touched > 0)
                Log.Message($"[TurboTick] 野生植物密度缓存在 {touched} 张地图上将于下一个生态 tick 重算（扫描相位保持存档值）");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 野生植物密度缓存复位失败：" + error.Message);
        }
    }
}
