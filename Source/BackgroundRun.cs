using HarmonyLib;
using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>Vanilla stops the whole update loop when the window loses focus and RunInBackground is off.
/// For a shared colony that is wrong: the timeline belongs to the server, so losing focus must not pause
/// a display replica. The flag is only forced while a session is active and the original value is put
/// back when the session ends.</summary>
[HarmonyPatch(typeof(Root), nameof(Root.Update))]
internal static class BackgroundRunPatch
{
    private static bool? original;

    private static void Postfix()
    {
        var wanted = Sharing();
        if (!wanted)
        {
            // Session ended: put back what the user actually selected. The old code reassigned `original`
            // from the forced value before restoring, so a player who had RunInBackground off ended up with
            // it permanently on after their first multiplayer session.
            if (original is { } restore) Application.runInBackground = restore;
            original = null;
            return;
        }
        if (Application.runInBackground) return;
        original ??= Application.runInBackground;
        Application.runInBackground = true;
    }

    private static bool Sharing() =>
        Current.ProgramState == ProgramState.Playing && TurboState.Authority.SessionRequested
        && TurboTickMod.Settings is not null;
}

/// <summary>Prefs.runInBackground is the same switch seen by the options menu. Writing it through the
/// property keeps the checkbox honest instead of leaving the UI showing a value the game ignores.</summary>
[HarmonyPatch(typeof(Prefs), nameof(Prefs.RunInBackground), MethodType.Setter)]
internal static class BackgroundPrefsPatch
{
    private static bool Prefix(ref bool __0)
    {
        if (!TurboState.Authority.SessionRequested) return true;
        // A shared session owns the timeline; a local toggle cannot be allowed to stall it.
        __0 = true;
        return true;
    }
}
