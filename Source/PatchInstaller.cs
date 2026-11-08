using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Installs each patch independently. A RimWorld build that moved one method should cost exactly one
/// feature, not silently disable every later patch in the same assembly.
/// </summary>
public static class PatchInstaller
{
    private static readonly Dictionary<string, bool> status = new(StringComparer.Ordinal);

    public static bool AllCritical { get; private set; }

    public static IReadOnlyDictionary<string, bool> Status => status;

    public static void InstallAll(Harmony harmony)
    {
        status.Clear();
        try { SimulationRandom.Validate(); status["simulation-rng"] = true; }
        catch (Exception error) { status["simulation-rng"] = false; Log.Warning("[TurboTick] RNG adapter: " + error.Message); }
        try { TurboEnvironmentState.ValidateBindings(); status["environment-state"] = true; }
        catch (Exception error) { status["environment-state"] = false; Log.Warning("[TurboTick] Environment adapter: " + error.Message); }
        try { TurboPawnPathState.ValidateBindings(); status["pawn-path-state"] = true; }
        catch (Exception error) { status["pawn-path-state"] = false; Log.Warning("[TurboTick] Pawn path adapter: " + error.Message); }
        Try(harmony, "designation", typeof(DesignationCapturePatch));
        Try(harmony, "designation-remove", typeof(DesignationRemoveCapturePatch));
        Try(harmony, "blueprint", typeof(BlueprintCapturePatch));
        Try(harmony, "blueprint-cancel", typeof(BlueprintCancelCapturePatch));
        Try(harmony, "frame-cancel", typeof(FrameCancelCapturePatch));
        Try(harmony, "draft", typeof(DraftCapturePatch));
        Try(harmony, "order", typeof(OrderCapturePatch));
        Try(harmony, "forbid", typeof(ForbidCapturePatch));
        Try(harmony, "priority", typeof(PriorityCapturePatch));
        Try(harmony, "zone-add", typeof(ZoneAddCellCapturePatch));
        Try(harmony, "zone-remove", typeof(ZoneRemoveCellCapturePatch));
        Try(harmony, "zone-delete", typeof(ZoneDeleteCapturePatch));
        Try(harmony, "bill-add", typeof(BillAddCapturePatch));
        Try(harmony, "bill-delete", typeof(BillDeleteCapturePatch));
        Try(harmony, "area-assign", typeof(AreaAssignCapturePatch));
        Try(harmony, "transport", typeof(TransportCapturePatch));
        // The fallback for every gizmo that has no dedicated command above. Without it a click on a filter,
        // a flick switch or a bed setting changes only the clicking player's world.
        Try(harmony, "gizmo-state", typeof(GizmoStatePatch));
        Try(harmony, "tradeguard", typeof(TradeGuardPatch));
        Try(harmony, "caravanguard", typeof(CaravanGuardPatch));
        Try(harmony, "gate", typeof(TickGatePatch));
        Try(harmony, "apply", typeof(TickApplyPatch));
        Try(harmony, "shared-snapshot-ids", typeof(SharedCompressionPatch));
        Try(harmony, "shared-respawn", typeof(SharedRespawnScopePatch));
        Try(harmony, "shared-filth", typeof(SharedSavedFilthPatch));
        Try(harmony, "pawn-path-resume", typeof(PawnPathResumePatch));
        Try(harmony, "world-pawns-sort", typeof(WorldPawnsDeterministicTickPatch));
        Try(harmony, "wander-herd", typeof(HerdWanderRootDeterministicPatch));
        Try(harmony, "wander-dest", typeof(RandomWanderDestDeterministicPatch));
        Try(harmony, "effect-isolation", typeof(TurboEffectIsolation));
        Try(harmony, "shared-tick-cadence", typeof(SharedTickCadence));
        Try(harmony, "shared-sky-cadence", typeof(SharedSkyCadence));
        Try(harmony, "shared-glow-cadence", typeof(SharedGlowCadence));
        Try(harmony, "shared-power-cadence", typeof(SharedPowerCadence));
        Try(harmony, "shared-region-cadence", typeof(SharedRegionCadence));
        Try(harmony, "shared-room-cadence", typeof(SharedRoomCadence));
        Try(harmony, "shared-lord-cadence", typeof(SharedLordCadence));
        // The remaining simulation work that vanilla drives per frame. Leaving any of these out means the
        // two peers run different weather, snow, temperature or allowed areas at the same tick, and then
        // take different AI decisions - which is what surfaced as colonists differing in position and job.
        Try(harmony, "shared-weather-cadence", typeof(SharedWeatherCadence));
        Try(harmony, "shared-area-cadence", typeof(SharedAreaCadence));
        Try(harmony, "shared-mapcomponent-cadence", typeof(SharedMapComponentCadence));
        // The same omission one level up: GameComponents and WorldComponents are per frame in UpdatePlay.
        // They own the storyteller's incident clock, quests, world pawns and caravans.
        Try(harmony, "shared-gamecomponent-cadence", typeof(SharedGameComponentCadence));
        Try(harmony, "shared-worldcomponent-cadence", typeof(SharedWorldComponentCadence));
        Try(harmony, "shared-projectile-cadence", typeof(SharedProjectileCadence));
        Try(harmony, "shared-worldobject-cadence", typeof(SharedWorldObjectCadence));
        Try(harmony, "shared-path-requests", typeof(SharedPathRequests));
        // Display-replica presentation: interpolated motion and a clock that follows the host.
        Try(harmony, "replica-draw-pos", typeof(ReplicaDrawPosPatch));
        Try(harmony, "replica-plant-guard", typeof(ReplicaPlantPrintGuard));
        // A shared timeline must not stop when the window loses focus.
        Try(harmony, "background-run", typeof(BackgroundRunPatch));
        Try(harmony, "background-prefs", typeof(BackgroundPrefsPatch));
        // Vanilla autosave's LongEventHandler freezes the shared timeline once per in-game day.
        Try(harmony, "shared-autosave", typeof(SharedAutosaveSkip));
        // A replica has no local path, so vanilla would draw its path line to the origin.
        Try(harmony, "replica-path-line", typeof(ReplicaPathLineSkip));
        Try(harmony, "replica-clock", typeof(ReplicaClockPatch));
        if (SimulationDiagnostics.Enabled)
        {
            foreach (var type in ConditionalTracePatches) Try(harmony, TraceKey(type), type);
        }
        else
        {
            // Opt-in diagnostics. Recording them here keeps the "declared but never installed" self-check
            // honest: they are deliberately skipped unless tracing is on, not forgotten.
            foreach (var type in ConditionalTracePatches) registered.Add(type);
        }
        Try(harmony, "world-load", typeof(WorldLoadScope));
        Try(harmony, "world-load-failure", typeof(SharedLoadFailurePatch));
        Try(harmony, "native-controls", typeof(NativeControlsPatch));
        Try(harmony, "native-speed", typeof(NativeSpeedSetterPatch));
        Try(harmony, "native-pause", typeof(NativePausePatch));
        Try(harmony, "multiplayer-menu-scope", typeof(MultiplayerMenuScope));
        Try(harmony, "multiplayer-menu-option", typeof(MultiplayerMenuOption));
        Try(harmony, "multiplayer-entry-update", typeof(MultiplayerEntryUpdate));
        Try(harmony, "multiplayer-return", typeof(MultiplayerReturnToMenu));
        Try(harmony, "multiplayer-shutdown", typeof(MultiplayerShutdown));
        // Without the gate a peer could simulate past the server and diverge; without capture nothing syncs.
        AllCritical = status.GetValueOrDefault("simulation-rng")
            && status.GetValueOrDefault("environment-state")
            && status.GetValueOrDefault("pawn-path-state")
            && status.GetValueOrDefault("designation")
            && status.GetValueOrDefault("blueprint")
            && status.GetValueOrDefault("draft")
            && status.GetValueOrDefault("order")
            && status.GetValueOrDefault("gate")
            && status.GetValueOrDefault("apply")
            && status.GetValueOrDefault("shared-snapshot-ids")
            && status.GetValueOrDefault("shared-respawn")
            && status.GetValueOrDefault("shared-filth")
            && status.GetValueOrDefault("pawn-path-resume")
            && status.GetValueOrDefault("world-pawns-sort")
            && status.GetValueOrDefault("wander-herd")
            && status.GetValueOrDefault("wander-dest")
            && status.GetValueOrDefault("effect-isolation")
            && status.GetValueOrDefault("shared-tick-cadence")
            && status.GetValueOrDefault("shared-sky-cadence")
            && status.GetValueOrDefault("shared-glow-cadence")
            && status.GetValueOrDefault("shared-projectile-cadence")
            && status.GetValueOrDefault("shared-worldobject-cadence")
            && status.GetValueOrDefault("shared-path-requests")
            && status.GetValueOrDefault("world-load")
            && status.GetValueOrDefault("world-load-failure")
            && status.GetValueOrDefault("native-controls")
            && status.GetValueOrDefault("native-speed")
            && status.GetValueOrDefault("native-pause");
        var summary = string.Join(" ", status.Select(pair => pair.Key + (pair.Value ? "✓" : "✗")));
        if (AllCritical) Log.Message("[TurboTick] 补丁已就绪：" + summary);
        else Log.Warning("[TurboTick] 部分补丁未挂载，共享殖民地将被禁用：" + summary);
        ReportUnregisteredPatches();
    }

    private static void Try(Harmony harmony, string name, Type patchClass)
    {
        try
        {
            registered.Add(patchClass);
            var patched = harmony.CreateClassProcessor(patchClass).Patch();
            status[name] = patched.Count > 0;
            if (patched.Count == 0) Log.Warning($"[TurboTick] 补丁 {name} 没有匹配到目标方法");
        }
        catch (Exception error)
        {
            status[name] = false;
            Log.Warning($"[TurboTick] 补丁 {name} 失败：{error.Message}");
        }
    }

    /// <summary>Everything this installer has been asked to patch.</summary>
    private static readonly HashSet<Type> registered = [];

    /// <summary>Tracing patches, installed only when <see cref="SimulationDiagnostics.Enabled"/>.</summary>
    private static readonly Type[] ConditionalTracePatches =
    [
        typeof(SharedFilthRemovalTracePatch), typeof(MapStageTracePatch), typeof(TickListStageTracePatch),
        typeof(EnvironmentStageTracePatch), typeof(CameraRateTracePatch), typeof(PawnTickTracePatch),
        typeof(PawnPathTracePatch), typeof(PawnJobTracePatch), typeof(RandValueTracePatch), typeof(RandIntTracePatch),
    ];

    /// <summary>Status key for a trace patch: its type name without the suffix, in kebab-case.</summary>
    private static string TraceKey(Type type)
    {
        var name = type.Name.Replace("TracePatch", "", StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.Append("-trace").ToString();
    }

    /// <summary>Patched from their own entry points rather than from InstallAll, so they are expected to be
    /// absent here: the headless pump, the tick-rate replacement, and the tracing helpers.</summary>
    private static readonly HashSet<string> installedElsewhere = new(StringComparer.Ordinal)
    {
        "HeadlessUpdatePatch", "TickRatePatch", "PawnTickTracePatch", "PawnPathTracePatch",
        "BackgroundRunPatch", "BackgroundPrefsPatch", "CaravanGuardPatch",
        "NativeControlsPatch", "NativeSpeedSetterPatch", "NativePausePatch",
        "ReplicaDrawPosPatch", "ReplicaClockPatch",
    };

    /// <summary>
    /// A patch class that nobody installs is silent: the feature it carries simply does not exist, and the
    /// symptom looks like a logic bug elsewhere. This project installs patch classes by name instead of
    /// calling PatchAll, so forgetting a registration is the most likely way to lose a feature. Naming the
    /// omission in the log turns that into a one-line diagnosis.
    /// </summary>
    private static void ReportUnregisteredPatches()
    {
        try
        {
            var orphans = typeof(PatchInstaller).Assembly.GetTypes()
                .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                .Where(type => !registered.Contains(type) && !installedElsewhere.Contains(type.Name))
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (orphans.Length > 0)
                Log.Warning("[TurboTick] 以下补丁类已声明但未安装（对应功能不会生效）：" + string.Join(", ", orphans));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 补丁登记自检失败：" + error.Message); }
    }
}
