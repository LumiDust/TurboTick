using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// The light grid is a cache derived from the light sources, and vanilla refreshes it from
/// <see cref="Map.MapUpdate"/>, which runs once per FRAME. Plants read it while ticking
/// (<c>Plant.GrowthRateFactor_Light</c> and the rotting rule both call
/// <see cref="GlowGrid.GroundGlowAt"/>), so at the same tick two machines with different frame rates can
/// read different light: one plant grows, the other rots, and the colony hashes disagree.
///
/// The same is true of the other simulation work in <see cref="Map.MapUpdate"/>: power nets, pathfinding
/// regions, room rebuilds and lord (group AI) decisions. Those put colonists in the divergence report,
/// because AI that re-decides a different number of times per second makes different choices.
///
/// Both halves are pinned to the tick below, so they are a function of the colony rather than of the
/// machine's frame rate.
/// </summary>
internal static class SharedLightGrid
{
    private static bool flushing;

    /// <summary>True while this peer is inside the deterministic refresh and vanilla work must proceed.</summary>
    internal static bool Flushing => flushing;

    internal static bool Gated => SharedColony.HasSharedWorld && !flushing;

    /// <summary>Called once per shared tick, before anything reads light or asks the AI to decide. Both
    /// maps are walked in index order so the two peers do the same work in the same sequence.</summary>
    internal static void Flush()
    {
        if (!SharedColony.HasSharedWorld || flushing) return;
        flushing = true;
        try
        {
            // Game and world level first, mirroring Game.UpdatePlay: components at these levels decide
            // incidents, quests, world pawns and caravans, and vanilla drives all of them per frame.
            if (Current.Game is not null) GameComponentUtility.GameComponentUpdate();
            if (Find.World is { } world) RimWorld.Planet.WorldComponentUtility.WorldComponentUpdate(world);
            var maps = Find.Maps;
            for (var i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                if (map is null || map.Disposed) continue;
                // Sky first: its glow feeds the light grid.
                map.skyManager?.SkyManagerUpdate();
                // Weather decides rain, snow and temperature drift, which feed plant growth, fire spread and
                // pawn comfort. It is frame driven in vanilla, so leaving it out let the two peers run
                // different weather and then take different AI decisions from it.
                map.weatherManager?.WeatherManagerUpdate();
                map.glowGrid?.GlowGridUpdate_First();
                // Structures AI and pathing depend on.
                map.powerNetManager?.UpdatePowerNetsAndConnections_First();
                map.regionGrid?.UpdateClean();
                map.regionAndRoomUpdater?.TryRebuildDirtyRegionsAndRooms();
                map.lordManager?.LordManagerUpdate();
                map.areaManager?.AreaManagerUpdate();
                // Every MapComponent updates here in vanilla - snow depth, temperature cache, wind, water.
                // Those are simulation inputs, not decoration, so they belong on the tick as well.
                MapComponentUtility.MapComponentUpdate(map);
            }
        }
        finally { flushing = false; }
    }
}

/// <summary>Frame-driven sky refresh is skipped in a shared world; <see cref="SharedLightGrid.Flush"/>
/// runs it on the tick instead.</summary>
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
internal static class SharedSkyCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Frame-driven light grid refresh is skipped in a shared world for the same reason.</summary>
[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GlowGridUpdate_First))]
internal static class SharedGlowCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Power nets rebuilt per frame; the result feeds turrets and anything that reads power.</summary>
[HarmonyPatch(typeof(RimWorld.PowerNetManager), nameof(RimWorld.PowerNetManager.UpdatePowerNetsAndConnections_First))]
internal static class SharedPowerCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Pathfinding regions rebuilt per frame; a different rebuild timing yields different paths.</summary>
[HarmonyPatch(typeof(RegionGrid), nameof(RegionGrid.UpdateClean))]
internal static class SharedRegionCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Rooms drive room stats, temperature and territory.</summary>
[HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
internal static class SharedRoomCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Lord decisions are group AI: a different number of updates per second changes what pawns do.</summary>
[HarmonyPatch(typeof(Verse.AI.Group.LordManager), nameof(Verse.AI.Group.LordManager.LordManagerUpdate))]
internal static class SharedLordCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Weather is a simulation input (rain, snow, temperature) even though vanilla drives it per frame.</summary>
[HarmonyPatch(typeof(RimWorld.WeatherManager), nameof(RimWorld.WeatherManager.WeatherManagerUpdate))]
internal static class SharedWeatherCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Areas feed allowed-area restrictions, which decide where pawns are permitted to walk.</summary>
[HarmonyPatch(typeof(AreaManager), nameof(AreaManager.AreaManagerUpdate))]
internal static class SharedAreaCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>MapComponents carry snow depth, the temperature cache, wind and water - all simulation inputs.</summary>
[HarmonyPatch(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentUpdate))]
internal static class SharedMapComponentCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>
/// GameComponents are the game-level counterpart: the storyteller's incident clock, quests, research and
/// tale tracking. <see cref="Verse.Game.UpdatePlay"/> updates them every frame, so two peers ran different
/// incident timing from the same save.
///
/// TurboTick's own component must keep running even while the rest are held: it is the pump that processes
/// network messages, so skipping it stops the peer from ever hearing the server. That mistake froze a peer
/// immediately after it published a colony - the phase moved to LoadingWorld, the gate closed, and the
/// message that would have finished the load was never read.
/// </summary>
[HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.GameComponentUpdate))]
internal static class SharedGameComponentCadence
{
    private static bool Prefix()
    {
        if (!SharedLightGrid.Gated) return true;
        if (Current.Game?.components is { } components)
            for (var i = 0; i < components.Count; i++)
                if (components[i] is TurboGameComponent) components[i].GameComponentUpdate();
        return false;
    }
}

/// <summary>WorldComponents keep world pawns, caravans and tile data current, also once per frame.</summary>
[HarmonyPatch(typeof(RimWorld.Planet.WorldComponentUtility), nameof(RimWorld.Planet.WorldComponentUtility.WorldComponentUpdate))]
internal static class SharedWorldComponentCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>
/// Dev-mode switches that change the tick a shared world is allowed to run on.
///
/// <c>DebugSettings.fastEcology</c> makes <see cref="TickManager.DoSingleTick"/> advance
/// <c>ticksGameInt</c> by 2000 instead of 1. TicksGame is what <see cref="TickList"/> uses to choose its
/// bucket, so one player with that switch on would run every long tick on every tick and drag the colony
/// away from everyone else within seconds. It is a debugging aid, not a gameplay option, so a shared world
/// turns it off instead of trying to reconcile the two.
/// </summary>
internal static class SharedDeterminismGuard
{
    private static bool warned;

    internal static void Enforce()
    {
        if (!SharedColony.HasSharedWorld) return;
        // Public static field, so no reflection is needed and the per-tick cost is one bool read.
        if (!DebugSettings.fastEcology) return;
        DebugSettings.fastEcology = false;
        if (warned) return;
        warned = true;
        Log.Warning("[TurboTick] 已关闭「快速生态」开发选项：它让每个 tick 前进 2000，会直接破坏共享时间线。");
    }
}

/// <summary>
/// Lockstep needs one absolute clock, not merely one tick rate. Vanilla buckets long ticks by
/// <c>TicksGame % 2000</c> (<see cref="TickList.Tick"/>), so two peers whose <c>TicksGame</c> differ by an
/// offset run <c>Plant.TickLong</c> on different ticks: growth and rotting then disagree by exactly one
/// long tick, which is what the colony hash reported.
/// </summary>
internal static class SharedGameClock
{
    private static FieldInfo? ticksGame;

    internal static void Align(int logicalTick)
    {
        if (!SharedColony.HasSharedWorld) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        try
        {
            ticksGame ??= AccessTools.Field(typeof(TickManager), "ticksGameInt");
            if (ticksGame is null) return;
            if ((int)(ticksGame.GetValue(manager) ?? 0) == logicalTick) return;
            ticksGame.SetValue(manager, logicalTick);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 无法对齐游戏时钟：" + error.Message);
        }
    }
}
