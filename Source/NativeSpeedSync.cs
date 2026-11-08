using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>The vanilla controls remain the only speed UI. Requests wait for the server, then all peers
/// update the same native field. ForcePaused/TimeSlower still belong to the game.</summary>
public static class NativeSpeedSync
{
    private static readonly AccessTools.FieldRef<TickManager, TimeSpeed> CurrentSpeed =
        AccessTools.FieldRefAccess<TickManager, TimeSpeed>("curTimeSpeed");
    private static int? pendingSpeed;
    private static long intent;
    private static int controlsDepth;
    private static TickManager? appliedManager;
    private static long appliedRevision = -1;
    public static void EnterControls() => ++controlsDepth;
    public static void ExitControls() => --controlsDepth;
    public static string Label => T((Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Normal) switch
    {
        TimeSpeed.Paused => "TurboTick_NativePaused",
        TimeSpeed.Fast => "TurboTick_NativeFast",
        TimeSpeed.Superfast => "TurboTick_NativeSuperfast",
        TimeSpeed.Ultrafast => "TurboTick_NativeUltrafast",
        _ => "TurboTick_NativeNormal"
    });

    public static bool BeforeSet(TickManager manager, TimeSpeed speed)
    {
        if (!Intercept(manager)) return true;
        Request(manager, (int)speed);
        return false;
    }

    public static bool BeforeToggle(TickManager manager)
    {
        if (!Intercept(manager)) return true;
        var current = pendingSpeed ?? (int)manager.CurTimeSpeed;
        var resume = (int)manager.prePauseTimeSpeed;
        Request(manager, current == 0 ? (resume is >= 1 and <= 4 ? resume : 1) : 0);
        return false;
    }

    private static bool Intercept(TickManager manager) =>
        controlsDepth > 0 && Current.ProgramState == ProgramState.Playing && Scribe.mode == LoadSaveMode.Inactive
        && ReferenceEquals(Find.TickManager, manager) && !manager.ForcePaused
        && TurboState.Authority.SessionRequested;

    private static void Request(TickManager manager, int speed)
    {
        SpeedPolicy.ValidateTimeSpeed(speed);
        if (!TurboState.Authority.SessionConnected || !RoomConnection.CanEdit) return;
        if (pendingSpeed == speed) return;
        // Identical selection needs no write, unless an older selection is still queued.
        if (pendingSpeed is null && TurboState.Authority.Current.TimeSpeed == speed) return;
        pendingSpeed = speed;
        Record($"Native speed requested: {speed}, pid {Environment.ProcessId}");
        var id = ++intent;
        RoomConnection.RequestTimeSpeed(speed, () => { if (id == intent) pendingSpeed = null; });
    }

    /// <summary>Asks the room for a pause. A divergence is a room-wide event: leaving the other players
    /// running only widens the gap while one peer sits in a dialog, and the lockstep gate would freeze them
    /// anyway without telling them why.</summary>
    public static void RequestRoomPause()
    {
        if (Find.TickManager is not { } manager) return;
        Request(manager, (int)TimeSpeed.Paused);
    }

    public static void ApplyConfirmed()
    {
        if (!TurboState.Authority.SessionConnected || Current.ProgramState != ProgramState.Playing || Current.Game is null
            || Scribe.mode != LoadSaveMode.Inactive || Find.TickManager is not { } manager) return;
        var policy = TurboState.Authority.Current;
        var selected = (TimeSpeed)policy.TimeSpeed;
        // Compare the live speed, never just the revision. A revision is applied once, but the manager can
        // be paused afterwards by a load, a long event or a local dialog; keying on the revision alone left
        // the resident frozen at a checkpoint with the server policy asking for normal speed.
        if (ReferenceEquals(appliedManager, manager) && manager.CurTimeSpeed == selected)
        {
            appliedRevision = policy.Revision;
            return;
        }
        appliedManager = manager;
        appliedRevision = policy.Revision;
        if (manager.CurTimeSpeed == selected) return;
        if (selected == TimeSpeed.Paused && manager.CurTimeSpeed != TimeSpeed.Paused)
            manager.prePauseTimeSpeed = manager.CurTimeSpeed;
        // The original setter can reject writes while a local dialog is open. The server selects the
        // common underlying speed; the game's ForcePaused property still pauses that local dialog.
        CurrentSpeed(manager) = selected;
        Record($"Native speed applied: {selected}, revision {policy.Revision}, pid {Environment.ProcessId}");
    }

    private static void Record(string text)
    {
        Log.Message("[TurboTick] " + text);
        // Waylith's Player.log is shared between processes even with different savedatafolder options.
        // Keep this small event journal in the actual profile so dual-client evidence cannot overwrite itself.
        try
        {
            var directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTick");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "native-speed.log"), $"{DateTime.UtcNow:O} {text}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log.Warning("[TurboTick] Native speed journal: " + error.Message); }
    }

    public static void Reset()
    {
        pendingSpeed = null;
        ++intent;
        appliedManager = null;
        appliedRevision = -1;
    }
}

// Map and world views, mouse buttons and key bindings all use this native method. Limit interception
// to its execution so loading, letters, cutscenes and threat slowdowns keep their vanilla behavior.
[HarmonyPatch(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))]
internal static class NativeControlsPatch
{
    private static void Prefix() => NativeSpeedSync.EnterControls();
    private static Exception? Finalizer(Exception? __exception)
    {
        NativeSpeedSync.ExitControls();
        return __exception;
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
internal static class NativeSpeedSetterPatch
{
    private static bool Prefix(TickManager __instance, TimeSpeed __0) => NativeSpeedSync.BeforeSet(__instance, __0);
}

// TogglePaused writes curTimeSpeed directly in 1.7, so both the pause icon and the pause hotkey need this hook.
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TogglePaused))]
internal static class NativePausePatch
{
    private static bool Prefix(TickManager __instance) => NativeSpeedSync.BeforeToggle(__instance);
}
