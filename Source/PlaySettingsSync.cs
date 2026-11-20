using System.Collections.Generic;
using TurboTick.Networking;
using Var = Verse;

namespace TurboTick;

/// <summary>Carries the three `PlaySettings` fields that are simulation inputs, and nothing else.
///
/// <para>See <see cref="ReplicaPlaySettings"/> for why those three and not the display toggles. This side publishes
/// only when one of them changes: they are three booleans behind checkboxes a player touches a handful of times per
/// session, so a per-frame comparison costs three field reads and a frame carries them once.</para>
///
/// <para><b>Applying is not just writing the field.</b> `MainTabWindow_Work.DoManualPrioritiesCheckbox`
/// (`_lab/asm/Assembly-CSharp.decompiled.cs:675248-675265`) does two things after the checkbox changes: it writes the
/// field and then calls <c>workSettings.Notify_UseWorkPrioritiesChanged()</c> on every player colonist, which is what
/// rebuilds the per-pawn priority comparator that the work giver list is sorted by. Writing the field alone leaves every
/// pawn's cached comparison in the previous mode, so the work tab would draw the new mode over the old ordering.</para></summary>
internal static class PlaySettingsSync
{
    private static bool lastWorkPriorities;
    private static bool lastAutoHomeArea;
    private static bool lastAutoRebuild;
    /// <summary>Whether the three values above describe anything. False means "never sent", which must produce a row:
    /// with the `primed`-and-skip shape this file first used, frame one recorded the values and frame two found them
    /// equal, so the settings were never sent at all and the census read `无变更` forever.</summary>
    private static bool sent;

    internal static long ChangesSent { get; private set; }
    internal static long ChangesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    private static string lastCare = string.Empty;

    /// <summary>One row when a simulation-input setting moved since the last frame, otherwise null.</summary>
    internal static List<ReplicaPlaySettings>? Publish()
    {
        try
        {
            var settings = Verse.Current.Game?.playSettings;
            if (settings is null) return null;
            var work = settings.useWorkPriorities;
            var home = settings.autoHomeArea;
            var rebuild = settings.autoRebuild;
            // `sent`, not `primed`: a peer joining an existing colony has to receive the current values even though
            // nothing changed while it connected. Guarding on "already primed" instead would swallow exactly that frame.
            // The care defaults belong to the same statement: a replica that shows and uses a different default
            // for a prisoner is showing a setting its player never made, and the med-care resolution reads them
            // (`:477541-477580`). A hand-made check that only compared the three booleans missed all eleven.
            var care = CareOf(settings);
            if (sent && work == lastWorkPriorities && home == lastAutoHomeArea && rebuild == lastAutoRebuild && care == lastCare)
                return null;
            sent = true;
            lastWorkPriorities = work;
            lastAutoHomeArea = home;
            lastAutoRebuild = rebuild;
            lastCare = care;
            ChangesSent++;
            return [new ReplicaPlaySettings(work, home, rebuild, care)];
        }
        catch (System.Exception error)
        {
            Var.Log.Warning("[TurboTick] play settings publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes the three fields and repeats what vanilla does when the work-priority checkbox moves.</summary>
    /// <summary>The eleven defaults, in a fixed order so a probe can name a position and a test can name the list.</summary>
    internal static readonly string[] CareFields =
    [
        "defaultCareForColonist", "defaultCareForPrisoner", "defaultCareForSlave", "defaultCareForTamedAnimal",
        "defaultCareForFriendlyFaction", "defaultCareForNeutralFaction", "defaultCareForHostileFaction",
        "defaultCareForNoFaction", "defaultCareForWildlife", "defaultCareForEntities", "defaultCareForGhouls",
    ];

    internal static string CareOf(RimWorld.PlaySettings settings)
    {
        var parts = new string[CareFields.Length];
        for (var i = 0; i < CareFields.Length; i++)
            parts[i] = typeof(RimWorld.PlaySettings).GetField(CareFields[i])?.GetValue(settings)
                       is RimWorld.MedicalCareCategory care ? ((int)care).ToString() : "-";
        return string.Join(",", parts);
    }

    internal static void Apply(ReplicaPlaySettings state)
    {
        try
        {
            var settings = Verse.Current.Game?.playSettings;
            if (settings is null) return;
            var workMoved = settings.useWorkPriorities != state.UseWorkPriorities;
            settings.useWorkPriorities = state.UseWorkPriorities;
            settings.autoHomeArea = state.AutoHomeArea;
            settings.autoRebuild = state.AutoRebuild;
            lastWorkPriorities = state.UseWorkPriorities;
            lastAutoHomeArea = state.AutoHomeArea;
            lastAutoRebuild = state.AutoRebuild;
            sent = true;
            // Only when all eleven are present: a frame written before this field existed must not reset the colony to
            // `NoMeds` for everyone.
            if (state.Care is { } careText)
            {
                var parts = careText.Split(',');
                if (parts.Length == CareFields.Length)
                    for (var i = 0; i < CareFields.Length; i++)
                    {
                        if (!int.TryParse(parts[i], out var value)) continue;
                        // `MedicalCareCategory` is a **byte enum**: `Enum.IsDefined(type, int)` throws for those, so the
                        // range is checked against the enum's own last member instead.
                        if (value < 0 || value > (int)RimWorld.MedicalCareCategory.Best) continue;
                        typeof(RimWorld.PlaySettings).GetField(CareFields[i])
                            ?.SetValue(settings, (RimWorld.MedicalCareCategory)value);
                    }
                lastCare = careText;
            }
            if (workMoved)
            {
                // Same call the checkbox makes (`:675258-675264`): it is what rebuilds each pawn's cached work-giver
                // ordering, and the work tab reads that ordering rather than the raw number.
                foreach (var pawn in RimWorld.PawnsFinder.AllMapsWorldAndTemporary_Alive)
                {
                    if (pawn?.Faction != RimWorld.Faction.OfPlayer) continue;
                    pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
                }
            }
            ChangesApplied++;
        }
        catch (System.Exception error)
        {
            ApplyFailures++;
            Var.Log.Warning("[TurboTick] play settings apply: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Forgets what was sent, so a replica that starts over from a new baseline is told the values again.</summary>
    internal static void Reset() => sent = false;

    internal static string Summary() =>
        ChangesApplied > 0 || ChangesSent > 0
            ? $"发出 {ChangesSent} / 应用 {ChangesApplied}（失败 {ApplyFailures}）"
            : "无变更";
}
