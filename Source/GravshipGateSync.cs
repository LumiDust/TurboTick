using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>The two gravship flags that decide whether <b>the whole game</b> is paused and whether a player may act at all.
///
/// <para><b>Why this is not decoration.</b> `TickManager.ForcePaused` (`:52680-52694`) returns true whenever
/// `Find.GravshipController?.LandingAreaConfirmationInProgress` is true, and `TickManager.PlayerCanControl`
/// (`:52696-52711`) refuses every speed change and every unpause with the message
/// 「MessageConfirmLandingAreaFirst」. `LandingAreaConfirmationInProgress` is exactly `landingMarker != null`
/// (`:26616`), and `cutsceneInProgress` is a static bool read by `ForcePaused`, `SuppressDraw`,
/// `SectionLayer` batching (`:30520`, `:49942`, `:78442`, `:81427`, `:83058`).</para>
///
/// <para><b>The defect.</b> All of it is world-component state, and no channel carried it. A replica therefore ran with
/// the values the save was loaded with: when the authority entered a landing confirmation the real game stopped and
/// every replica kept ticking, and when the authority left it the replicas that had loaded mid-confirmation stayed
/// frozen forever. That is the one class of divergence where the two screens disagree about whether the game is running
/// - it cannot be seen by looking at the colony, only by watching the clock.</para>
///
/// <para><b>Why the flags and not `ForcePaused`.</b> Sending the derived boolean would lose the difference between the
/// two inputs, and `PlayerCanControl` needs `LandingAreaConfirmationInProgress` specifically to produce its message.
/// The two fields are what `ExposeData` already scribes (`:27152`, `:27164-27171`), so the replica is being given the
/// same state a save load would give it.</para>
///
/// <para><b>What is not sent.</b> `landingMarker` itself. It is a `GravshipLandingMarker` on a map and travels on the
/// object channel like any other thing; what the replica cannot derive is the controller's pointer, and that pointer is
/// only ever non-null on the peer that showed the confirmation.</para></summary>
internal static class GravshipGateSync
{
    private static string last = "";

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static bool ObservedCutscene { get; private set; }
    internal static bool ObservedConfirmation { get; private set; }

    internal static void Reset()
    {
        last = "";
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
        ObservedCutscene = false;
        ObservedConfirmation = false;
    }

    // **No `FieldRefAccess` here.** `cutsceneInProgress` is `static` (`:26542`) and `landingMarker` is an instance
    // field, so `FieldRefAccess<T, …>` builds an accessor for an instance field of the *wrong* shape on a static class -
    // and because these were `static readonly` initialisers, the failure surfaced as
    // `TypeInitializationException: The type initializer for 'TurboTick.GravshipGateSync' threw an exception` at the
    // first command that touched the type, i.e. the whole census. Plain `AccessTools.Field` is resolved on use instead,
    // and a null is reported rather than thrown.

    /// <summary>Encoded state, or null when there is no controller (pre-Odyssey worlds have none).</summary>
    internal static string? Publish()
    {
        var controller = Find.GravshipController;
        if (controller is null) return null;
        bool cutscene;
        bool confirmation;
        try
        {
            cutscene = WorldComponent_GravshipController.CutsceneInProgress;
            confirmation = controller.LandingAreaConfirmationInProgress;
        }
        catch (Exception) { return null; }
        ObservedCutscene |= cutscene;
        ObservedConfirmation |= confirmation;
        var encoded = (confirmation ? "1" : "0") + (cutscene ? "1" : "0");
        if (encoded == last) return null;
        last = encoded;
        StatesSent++;
        return encoded;
    }

    internal static void Apply(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length < 2) return;
        var controller = Find.GravshipController;
        if (controller is null) { ApplyFailures++; return; }
        try
        {
            var confirmation = payload[0] == '1';
            var cutscene = payload[1] == '1';
            // `cutsceneInProgress` is `static`; writing it through the instance FieldRef would write an instance field
            // that does not exist, so the static field is taken from the type instead.
            var cutsceneField = AccessTools.Field(typeof(WorldComponent_GravshipController), "cutsceneInProgress");
            cutsceneField?.SetValue(null, cutscene);
            // `LandingAreaConfirmationInProgress` is a null check, so the only way to move it is to move the pointer.
            // A sentinel object is not an option - other readers cast it (`:27094` calls `landingMarker.BeginLanding`),
            // so a replica that receives "confirmation in progress" with no marker would throw on the next confirm.
            // Instead the pointer is left alone and the *observable* answer is restored by clearing it when the
            // authority says no confirmation is pending, which is the only transition a replica can act on.
            if (!confirmation && controller.LandingAreaConfirmationInProgress)
                AccessTools.Field(typeof(WorldComponent_GravshipController), "landingMarker")?.SetValue(controller, null);
            last = payload;
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    internal static string Summary()
    {
        var controller = Find.GravshipController;
        if (controller is null) return "无 gravship 控制器（未装奥德赛或世界未就绪）";
        return $"发射 {StatesSent} 次 / 应用 {StatesApplied} 次 / 失败 {ApplyFailures} 次；"
            + $"本端 cutscene={WorldComponent_GravshipController.CutsceneInProgress}、"
            + $"确认中={controller.LandingAreaConfirmationInProgress}；"
            + $"历史观察 cutscene={ObservedCutscene}、确认={ObservedConfirmation}";
    }
}
