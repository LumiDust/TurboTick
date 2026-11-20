using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Whether a pawn is a guest, a prisoner or a slave, and how close the prisoner is to joining.
///
/// <para><b>Why a replica needs this.</b> Guest status is set by gameplay that runs on ticks a replica does not have:
/// downing a raider and capturing them (<c>Pawn_GuestTracker.SetGuestStatus</c> from a job), a prisoner being recruited
/// (<c>RecruitUtility.Recruit</c>), a slave rebelling, a guest deciding to join. On a replica the field keeps whatever the
/// save was loaded with, and everything the player reads follows it: the prisoner tab, the "Prisoner" tag on the inspect
/// pane, the colour of the colonist bar entry, whether the pawn appears under the prisoner filter, the resistance bar,
/// and - most visibly - whether the game treats them as hostile at all. A captured raider would sit in a cell looking like
/// an ordinary colonist to every query the UI makes.</para>
///
/// <para><b>Why resistance and will travel too.</b> Recruiting is a slow number, not an event: a prisoner's resistance
/// falls a point at a time over many attempts, and the player watches that bar to decide whether to keep trying. A
/// replica that showed the load-time resistance would show a bar that never moves while the host's prisoner is nearly
/// converted - the exact "is this working?" question the panel exists to answer.</para>
///
/// <para><b>Why nothing is rebuilt through the game's own setters.</b> <c>SetGuestStatus</c> fires
/// <c>Notify_GuestStatusChanged</c>, which re-evaluates the pawn's faction, its lord, its job and the map's prisoner
/// list - all of which assume a simulation that is stepping. Writing the field is what a replica needs: every reader
/// (the inspect string, the tab, the bar, the filters) goes through <c>GuestStatus</c>.</para>
internal static class GuestSync
{
    /// <summary>Last state sent per pawn. Empty means "not a guest, prisoner or slave".</summary>
    private static readonly Dictionary<int, string> sent = [];

    private static AccessTools.FieldRef<Pawn_GuestTracker, GuestStatus>? statusField;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Exclusive interaction modes written (round 146). Counted apart from the row count for the same reason
    /// the other per-field counters exist: a row that arrives without the field and a row whose mode already matched
    /// both leave this at zero, and only the second is healthy.</summary>
    internal static long ModeRows { get; private set; }

    /// <summary>Non-exclusive interaction toggles written (round 146) - one per def whose enabled state differed.</summary>
    internal static long NonExclusiveRows { get; private set; }

    /// <summary>Conversion ideologies written (round 146).</summary>
    internal static long IdeoConversionRows { get; private set; }

    /// <summary>Mode defs this peer does not have, or that arrived on the exclusive half while being non-exclusive.</summary>
    internal static int ModeUnresolved { get; private set; }

    /// <summary>Conversion ideo ids this peer does not have.</summary>
    internal static int IdeoUnresolved { get; private set; }

    private static AccessTools.FieldRef<Pawn_GuestTracker, GuestStatus> Status =>
        statusField ??= AccessTools.FieldRefAccess<Pawn_GuestTracker, GuestStatus>("guestStatusInt");

    /// <summary>`Pawn_GuestTracker.hostFactionInt` (`:461030`), read-only through the `HostFaction` property
    /// (`:461088`) but with no setter, so the mirror has to write the field.
    ///
    /// <para>Resolved lazily for the same reason as <see cref="Status"/>: a static initializer that runs at type-load time
    /// and throws takes the whole class down, and `FieldRefAccess` fails hard if the field is ever renamed. Resolved on
    /// first use, the failure is a single `TypeInitializationException`-free miss that shows up as "nothing applied".</para></summary>
    private static AccessTools.FieldRef<Pawn_GuestTracker, Faction?>? hostFactionRef;

    private static AccessTools.FieldRef<Pawn_GuestTracker, Faction?> HostFactionField =>
        hostFactionRef ??= AccessTools.FieldRefAccess<Pawn_GuestTracker, Faction?>("hostFactionInt");

    /// <summary>Finds a faction by `loadID`, the identifier that is stable across peers.
    ///
    /// <para>`Faction.loadID` is `public int` (`:485244`) and scribed at `:485500`; `GetUniqueLoadID()` is
    /// `"Faction_" + loadID` (`:486605-486608`). The defName would not do: two temporary factions can share a def.</para>
    /// </summary>
    private static Faction? FindFaction(int loadId)
    {
        var factions = Find.FactionManager?.AllFactionsListForReading;
        if (factions is null) return null;
        for (var i = 0; i < factions.Count; i++)
            if (factions[i] is { } faction && faction.loadID == loadId) return faction;
        return null;
    }

    /// <summary>Checks that the wire mirror in `Networking` still describes the game's own enum.
    ///
    /// <para>The frame carries the status as an integer, and the test project can only see the mirror - so a game update
    /// that reordered <c>GuestStatus</c> would keep every test green while every prisoner on the wire became a guest.
    /// This compares the two by value once, on the first publish, and says so if they have diverged.</para></summary>
    private static bool mirrorChecked;

    private static void CheckMirror()
    {
        if (mirrorChecked) return;
        mirrorChecked = true;
        foreach (GuestStatusWire wire in Enum.GetValues<GuestStatusWire>())
        {
            var game = (GuestStatus)(int)wire;
            if (Enum.IsDefined(typeof(GuestStatus), game) && game.ToString() == wire.ToString()) continue;
            Log.Error($"[TurboTick] GuestStatus 与线上镜像不一致：游戏侧 {game}，镜像侧 {wire}。"
                + "囚犯/客人状态会传到错误的类型，请同步 Networking/SettlementGrants.cs 的 GuestStatusWire。");
            return;
        }
    }

    internal static List<ReplicaGuest>? Publish()
    {
        List<ReplicaGuest>? changed = null;
        try
        {
            CheckMirror();
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.guest is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    // `status#resistance#will#hostFactionLoadId`, quantised to whole points. Resistance falls a fraction
                    // at a time over many attempts; the bar the player watches has ~10 visible steps, so whole points is
                    // what it shows.
                    //
                    // The host faction is the fourth field because it is written by exactly one method -
                    // `Pawn_GuestTracker.SetGuestStatus` (`:461540-461541`:
                    // `hostFactionInt = ((guestStatus != GuestStatus.Slave) ? newHost : null)`) - and this channel writes
                    // `guestStatusInt` directly, so before this field existed the replica's `HostFaction` stayed at its
                    // load-time value forever. That is the field `ITab_Pawn_Health.ShouldAllowOperations`
                    // (`:667161-667163`) reads to let you operate on a downed guest, so on a replica the health card
                    // offered or withheld surgery on a pawn whose host had changed long ago.
                    var host = pawn.guest.HostFaction?.loadID ?? -1;
                    var status = pawn.guest.GuestStatus;
                    // **The prisoner panel's three remaining settings (round 146).** `Pawn_GuestTracker`'s private
                    // `interactionMode` (`:461024`, default `MaintainOnly`) plus its set of enabled non-exclusive
                    // interactions, and `ideoForConversion` (`:461062`). The whole management panel is these three:
                    // the dropdown calls `SetExclusiveInteraction` (`:461317-461327`) or
                    // `ToggleNonExclusiveInteraction` (`:461331-461349`), and the ideo icon assigns the field
                    // (`:666791`, with `:666976` as the default when Convert is chosen). Every reader runs where the
                    // simulation runs:
                    //   * `:309977`/`:309996` - the warden's convert job giver;
                    //   * `:290876` - reduce-resistance social fight; `:290965` - enslave vs reduce-will choice;
                    //   * `:310262-310263` - the recruit/enslave job gating;
                    //   * `:310427`/`:310446` - interrogate; `:310467` - the release job;
                    //   * `:454337` - whether the execution interaction may start at all;
                    //   * `:456406`/`:456424` - whether an inspired recruitment may be attempted.
                    // Measured before this round: no `Source/` file mentioned `interactionMode` anywhere, so a player
                    // switching a prisoner to "Convert" on a replica left the authority on "Maintain only".
                    var modeName = pawn.guest.ExclusiveInteractionMode?.defName ?? "-";
                    var conversionId = pawn.guest.ideoForConversion?.id ?? -1;
                    var nonExclusive = NonExclusiveNames(pawn.guest);
                    var encoded = status == GuestStatus.Guest && pawn.guest.Resistance <= 0f && pawn.guest.will <= 0f
                            && host < 0 && pawn.guest.Recruitable
                            && pawn.guest.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.MaintainOnly
                            && conversionId < 0 && nonExclusive.Length == 0
                        ? string.Empty
                        : (int)status + "#" + (int)pawn.guest.Resistance + "#" + (int)pawn.guest.will
                            + "#" + host.ToString(CultureInfo.InvariantCulture)
                            + "#" + (pawn.guest.Recruitable ? "1" : "0")
                            + "#" + modeName
                            + "#" + conversionId.ToString(CultureInfo.InvariantCulture)
                            + "#" + nonExclusive;
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaGuest(id, encoded));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] guest publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaGuest state)
    {
        try
        {
            var pawn = FindPawnAny(state.Pawn);
            if (pawn?.guest is null || pawn.Dead) return;
            var parts = state.State.Split('#');
            var status = parts.Length > 0 && int.TryParse(parts[0], out var raw)
                && Enum.IsDefined(typeof(GuestStatus), raw) ? (GuestStatus)raw : GuestStatus.Guest;
            var resistance = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 0f;
            var will = parts.Length > 2 && int.TryParse(parts[2], out var w) ? w : 0f;
            // A three-part payload is a peer that has not been updated; treat its host as "none" rather than refusing the
            // whole row, so a mixed-version room still syncs status, resistance and will.
            var host = parts.Length > 3 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var h) ? h : -1;
            // **`Recruitable` decides a rule, not a number.** `SetupRecruitable` (`:461474`) rolls it once from
            // `NonRecruitableChanceOverPopulationIntentCurve`, and then three readers gate on it:
            // the recruit button and the resistance readout (`:666680`), whether an interaction mode is hidden
            // (`:666873`, `mode.hideIfNotRecruitable`), and whether the right-click "capture" option says
            // "Unrecruitable" (`:656581`). A replica without it offers to recruit a prisoner the authority cannot
            // recruit. A four-part payload is an older peer whose value was never sent; it keeps `true`, which is the
            // field's own default (`:461064`).
            var recruitable = parts.Length <= 4 || parts[4] != "0";
            // The status is the only field that needs reflection; resistance and will are public fields.
            if (Status(pawn.guest) != status) Status(pawn.guest) = status;
            // Written last and by the backing field: `HostFaction` has no setter, and going through `SetGuestStatus`
            // would re-run `AddAndRemoveDynamicComponents`, `health.surgeryBills.Clear()`, `ResetMedicalCare` and
            // `ReachabilityUtility.ClearCacheFor` on a replica that never simulated any of it.
            var hostNow = pawn.guest.HostFaction?.loadID ?? -1;
            if (hostNow != host) HostFactionField(pawn.guest) = host >= 0 ? FindFaction(host) : null;
            if (Math.Abs(pawn.guest.resistance - resistance) > 0.5f) pawn.guest.resistance = resistance;
            if (Math.Abs(pawn.guest.will - will) > 0.5f) pawn.guest.will = will;
            if (pawn.guest.Recruitable != recruitable) pawn.guest.Recruitable = recruitable;
            // Interaction mode. A six-part payload is a peer that does not send these three; then the replica keeps
            // whatever it has rather than being reset to MaintainOnly, which would look like a deliberate change.
            if (parts.Length > 5)
            {
                var modeName = parts[5];
                var modeDef = modeName is "-" or ""
                    ? PrisonerInteractionModeDefOf.MaintainOnly
                    : DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(modeName);
                if (modeDef is null)
                {
                    ModeUnresolved++;
                }
                else if (modeDef.isNonExclusiveInteraction)
                {
                    // Exclusive setter refuses these by design (`:461317-461321` logs an error); the non-exclusive half
                    // is the enabled set below, so a host that sends one here has been misencoded.
                    ModeUnresolved++;
                }
                else if (pawn.guest.ExclusiveInteractionMode != modeDef)
                {
                    // Through the game's own setter: it is the only public way in, and its guard is exactly what makes
                    // this safe on a replica (it writes one field and logs, it does not rebuild components).
                    pawn.guest.SetExclusiveInteraction(modeDef);
                    ModeRows++;
                }
                var nonExclusiveWanted = parts.Length > 7 ? parts[7] : string.Empty;
                ApplyNonExclusive(pawn.guest, nonExclusiveWanted);
                var conversionId = parts.Length > 6
                    && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var conv)
                    ? conv
                    : -1;
                var conversionIdeo = conversionId < 0 ? null : IdeoSync.IdeoById(conversionId);
                if (conversionId >= 0 && conversionIdeo is null) IdeoUnresolved++;
                else if (pawn.guest.ideoForConversion != conversionIdeo)
                {
                    // Public field (`:461062`), so this is a plain write: the icon and the convert job read it
                    // directly, and nothing has to be recomputed.
                    pawn.guest.ideoForConversion = conversionIdeo;
                    IdeoConversionRows++;
                }
            }
            // `IsPrisoner`/`IsSlave` are read from `guestStatusInt` directly, but the UI's tab and the pawn's bar colour
            // go through cached values that a status change would normally invalidate.
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
            StatesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] guest apply failed (pawn " + state.Pawn + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>The enabled non-exclusive interactions as a sorted, comma-joined def-name list.
    ///
    /// <para>Sorted so the change key is stable: the backing list is a `List&lt;PrisonerInteractionModeDef&gt;` whose order is
    /// whatever sequence of toggles produced it, and sending it in list order would make the row look changed when the
    /// player enabled the same two in the other order. Read through reflection because
    /// <c>enabledNonExclusiveInteractions</c> is private and the public API only answers one def at a time
    /// (`IsInteractionEnabled`).</para></summary>
    private static string NonExclusiveNames(Pawn_GuestTracker guest)
    {
        var names = new List<string>();
        try
        {
            if (NonExclusiveField(guest) is { } list)
                foreach (var def in list)
                    if (def?.defName is { Length: > 0 } name) names.Add(name);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 读取非排他互动失败：" + error.GetType().Name + ": " + error.Message);
        }
        names.Sort(StringComparer.Ordinal);
        return string.Join(",", names);
    }

    /// <summary>Diffs the enabled set and toggles only what differs, through the game's own `ToggleNonExclusiveInteraction`
    /// (`:461331`). Written as a diff rather than "clear then set" so a replica that already has the right set causes no
    /// list churn and no change-key movement.</summary>
    private static void ApplyNonExclusive(Pawn_GuestTracker guest, string wanted)
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in wanted.Split(',', StringSplitOptions.RemoveEmptyEntries)) desired.Add(name);
        foreach (var def in DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading)
        {
            if (def is null || !def.isNonExclusiveInteraction || def.defName is not { Length: > 0 } name) continue;
            var has = false;
            try { has = guest.IsInteractionEnabled(def); }
            catch (Exception) { continue; }
            var should = desired.Contains(name);
            if (has == should) continue;
            guest.ToggleNonExclusiveInteraction(def, should);
            NonExclusiveRows++;
        }
    }

    private static AccessTools.FieldRef<Pawn_GuestTracker, List<PrisonerInteractionModeDef>>? nonExclusiveRef;

    private static AccessTools.FieldRef<Pawn_GuestTracker, List<PrisonerInteractionModeDef>> NonExclusiveRef =>
        nonExclusiveRef ??= AccessTools.FieldRefAccess<Pawn_GuestTracker, List<PrisonerInteractionModeDef>>(
            "enabledNonExclusiveInteractions");

    private static List<PrisonerInteractionModeDef>? NonExclusiveField(Pawn_GuestTracker guest)
    {
        try { return NonExclusiveRef(guest); }
        catch (Exception) { return null; }
    }

    private static Pawn? FindPawnAny(int id)
    {
        foreach (var map in Find.Maps)
        {
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (var i = 0; i < pawns.Count; i++)
                if (pawns[i] is { } pawn && pawn.thingIDNumber == id) return pawn;
        }
        var world = Find.WorldPawns?.AllPawnsAliveOrDead;
        if (world is not null)
            for (var i = 0; i < world.Count; i++)
                if (world[i] is { } pawn && pawn.thingIDNumber == id) return pawn;
        return null;
    }

    internal static void Reset() => sent.Clear();

    /// <summary>Census row. Also counts how many pawns actually have a host faction, because "发出 0" on a colony with
    /// no prisoners is the correct reading and looks identical to a channel that never encodes.</summary>
    internal static string Summary()
    {
        var guests = 0;
        var prisoners = 0;
        var withHost = 0;
        var withMode = 0;
        var withConversion = 0;
        var withNonExclusive = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.guest is null) continue;
                if (pawn.guest.GuestStatus != GuestStatus.Guest) guests++;
                if (pawn.guest.IsPrisoner) prisoners++;
                if (pawn.guest.HostFaction is not null) withHost++;
                if (pawn.guest.ExclusiveInteractionMode != PrisonerInteractionModeDefOf.MaintainOnly) withMode++;
                if (pawn.guest.ideoForConversion is not null) withConversion++;
                if (NonExclusiveNames(pawn.guest).Length > 0) withNonExclusive++;
            }
        return $"囚犯/客人 发出 {StatesSent} / 应用 {StatesApplied}（失败 {ApplyFailures}）"
            + $"；本端在场 {guests} 个非客人、{prisoners} 个囚犯，其中 {withHost} 个有主家派系"
            + $" / 互动模式 {withMode} 个非默认（应用 {ModeRows} 行，非排他开关 {NonExclusiveRows} 次，未解析 {ModeUnresolved}）"
            + $" / 皈依目标 {withConversion} 个（应用 {IdeoConversionRows} 行，未解析 {IdeoUnresolved}）"
            + $" / 非排他互动 {withNonExclusive} 个开启";
    }
}
