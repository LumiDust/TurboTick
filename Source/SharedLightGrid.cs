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

    /// <summary>Simulation work: everything below that decides state another rule reads back - power nets, pathing
    /// regions, rooms, lord AI, areas, map/game/world components. It may only run inside the shared tick, so both
    /// peers run it the same number of times for the same logical tick.</summary>
    internal static bool Gated => SharedColony.HasSharedWorld && !flushing;

    /// <summary>Presentation work: the sky's sun angle and glow, the light grid derived from them, and the weather
    /// ambience that vanilla also refreshes per frame. Vanilla drives all three from <see cref="Map.MapUpdate"/>.
    ///
    /// A display replica must keep computing them, because nothing else carries them. `Gated` froze them too (its
    /// `flushing` window only opens inside `Flush()`, which is called from `DoSingleTick`, which a frozen replica
    /// never reaches), and the result was a replica standing in the checkpoint's day forever - measured 2026-11-11:
    /// 3000 frames / 50 s of live frames and the sky never moved. It was the most visible defect of the whole
    /// display-replica path, and unlike the missing animation it has no other channel to arrive through.
    ///
    /// These are not simulation: on a frozen replica no rule runs at all, so nothing reads the values back. Weather
    /// *selection* is deliberately not here - it happens in `WeatherDecider` on a tick, and the chosen definition,
    /// its predecessor and the transition age arrive in the frame (`ReplicaWeather`), so a replica follows the host's
    /// weather instead of inventing its own.</summary>
    internal static bool PresentationGated => Gated && !AuthoritativeReplica.Frozen;

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

/// <summary>Frame-driven sky refresh is skipped in a shared world on a simulator; <see cref="SharedLightGrid.Flush"/>
/// runs it on the tick instead. A display replica keeps vanilla's per-frame cadence (see
/// <see cref="SharedLightGrid.PresentationGated"/>).</summary>
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
internal static class SharedSkyCadence
{
    private static bool Prefix() => !SharedLightGrid.PresentationGated;
}

/// <summary>Frame-driven light grid refresh is skipped on a simulator for the same reason, and left alone on a
/// display replica, whose sky is the only source of its light.</summary>
[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GlowGridUpdate_First))]
internal static class SharedGlowCadence
{
    private static bool Prefix() => !SharedLightGrid.PresentationGated;
}

/// <summary>Power nets rebuilt per frame; the result feeds turrets and anything that reads power.</summary>
[HarmonyPatch(typeof(RimWorld.PowerNetManager), nameof(RimWorld.PowerNetManager.UpdatePowerNetsAndConnections_First))]
internal static class SharedPowerCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Pathfinding regions rebuilt per frame; a different rebuild timing yields different paths.
///
/// <para>`PresentationGated`, not `Gated`: on a frozen display replica nothing else ever rebuilds them. `Gated` only
/// opens its window inside <see cref="Flush"/>, which is called from <c>DoSingleTick</c> - and a replica never runs a
/// tick (<c>SharedColony.TryBeginTick</c> returns false). Measured consequence: the client logged
/// <c>Checking prison cell status of (134, 0, 107) which is not in or adjacent to a room</c> 138 times in one
/// second while the player moved the mouse over a cell that *is* inside a room on the host, because
/// <c>IntVec3.GetRoomOrAdjacent</c> resolves through this grid and every cell the frame had touched was still
/// unassigned. The room lookup is a UI query, so it has to keep working on the replica.</para></summary>
[HarmonyPatch(typeof(RegionGrid), nameof(RegionGrid.UpdateClean))]
internal static class SharedRegionCadence
{
    private static bool Prefix() => !SharedLightGrid.PresentationGated;
}

/// <summary>Rooms drive room stats, temperature and territory - and, for the replica, every prison/outdoors question
/// the float menu asks. Same predicate and same reason as <see cref="SharedRegionCadence"/>.</summary>
[HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
internal static class SharedRoomCadence
{
    private static bool Prefix() => !SharedLightGrid.PresentationGated;
}

/// <summary>Lord decisions are group AI: a different number of updates per second changes what pawns do.</summary>
[HarmonyPatch(typeof(Verse.AI.Group.LordManager), nameof(Verse.AI.Group.LordManager.LordManagerUpdate))]
internal static class SharedLordCadence
{
    private static bool Prefix() => !SharedLightGrid.Gated;
}

/// <summary>Weather is a simulation input (rain, snow, temperature) even though vanilla drives it per frame, so a
/// simulator runs it on the tick. On a display replica the same call is the cross-fade only - which weather is
/// current comes from the frame - and must keep running or the replica would never reach the weather the host
/// chose.</summary>
[HarmonyPatch(typeof(RimWorld.WeatherManager), nameof(RimWorld.WeatherManager.WeatherManagerUpdate))]
internal static class SharedWeatherCadence
{
    private static bool Prefix() => !SharedLightGrid.PresentationGated;
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
    internal static int RewindsAccepted { get; private set; }
    internal static int RewindsSkipped { get; private set; }

    /// <summary>Moves the engine clock to the shared timeline, and moves the colony's "when" stamps with it.
    ///
    /// <para>A decreasing <c>TicksGame</c> used to be poisonous because nothing moved with it. Vanilla keeps absolute
    /// ticks in dozens of places that are never revisited - <c>Job.startTick</c>, <c>Pawn_PathFollower.lastMovedTick</c>,
    /// <c>lastEnteredCellTick</c>, hediff durations - and every consumer subtracts them from "now". Rewind the clock
    /// alone and those stamps are in the future, so each subtraction yields a negative age and work stops for good.
    /// Measured: <c>Price: job=LayDown 开始=100391 到期于=100390 当前=3702 时钟条款=不满足</c> - the job's own expiry
    /// check (<c>Verse.AI/Pawn_JobTracker.cs:168</c>) can never be true again, while the pump itself was healthy
    /// (<c>JobTrackerTick=285054 DriverTick=273948</c>).</para>
    ///
    /// <para><b>The first attempt at this fix refused the rewind instead</b>, which was worse: the barrier then stayed
    /// below the engine clock, <c>CanAdvance</c> was false forever, and the world never ticked again
    /// (<c>appliedTick=30000, serverTick=0, paused=True</c>). ColonyTogether solves it the other way around - accept the
    /// correction and move the stamps (<c>ColonyClockRepair</c>,
    /// <c>docs/mp-colonytogether/_decomp/ColonyTogether.Prototype.decompiled.cs:75928</c>, tolerance
    /// <c>1000</c> at <c>:75930</c>, bulk shift at <c>:75990</c>). This follows that: a small drift is ignored, a
    /// large one is applied together with <see cref="ClockStampShift"/>.</para></summary>
    internal static void Align(int logicalTick)
    {
        if (!SharedColony.HasSharedWorld) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        try
        {
            ticksGame ??= AccessTools.Field(typeof(TickManager), "ticksGameInt");
            if (ticksGame is null) return;
            var current = (int)(ticksGame.GetValue(manager) ?? 0);
            if (current == logicalTick) return;
            var delta = logicalTick - current;
            if (delta < 0)
            {
                // Backwards: the correction is real (an older checkpoint), so accept it - but only if the stamps can go
                // with it, otherwise the colony stops working. Below the tolerance the drift is left in place: moving
                // hundreds of thousands of stamps to fix a few ticks of skew is the worse trade.
                if (-delta < ClockStampShift.Tolerance) { ClockStampShift.NoteSmallDrift(); RewindsSkipped++; return; }
                RewindsAccepted++;
                if (RewindsAccepted <= 5)
                    Log.Warning($"[TurboTick] 游戏时钟回拨 {current} → {logicalTick}（{-delta} tick）："
                        + "所有 last*/startTick/spawnedTick 戳同步回拨，否则它们会落在未来、让工作与移动永久停止。");
                ShiftColonyStamps(delta);
            }
            ticksGame.SetValue(manager, logicalTick);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 无法对齐游戏时钟：" + error.Message);
        }
    }

    /// <summary>Applies the same delta to every live object that can hold a stamp. `Game` is walked rather than the raw
    /// object graph: it reaches maps, pawns, things and comps without crawling the whole heap.</summary>
    private static void ShiftColonyStamps(int delta)
    {
        try
        {
            if (Current.Game is { } game) ClockStampShift.Shift(game, delta);
            foreach (var map in Find.Maps) ClockStampShift.Shift(map, delta, maxDepth: 3);
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    ClockStampShift.Shift(pawn.jobs, delta, maxDepth: 2);
                    ClockStampShift.Shift(pawn.pather, delta, maxDepth: 1);
                }
        }
        catch (Exception error) { Log.Warning("[TurboTick] 时钟戳平移失败：" + error.Message); }
    }
}
