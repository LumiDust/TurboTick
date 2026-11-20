using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Door open/closed state, which a replica otherwise freezes at whatever the save was loaded with.
///
/// <para><b>Why doors need their own channel.</b> <c>Building_Door.openInt</c> is a protected field driven by
/// <c>Building_Door.Tick</c>: a pawn walks up, the ownertick sets <c>openInt = true</c>, a close delay counts down, and
/// it flips back. A display replica runs no tick, so nothing ever moves that field - a door that was open when the save
/// loaded stays open for the session, and one that was closed stays closed even while the host's colonists file through
/// it every few seconds.</para>
///
/// <para>The visible result is not subtle: the whole colony's doors are drawn in the wrong state, and because
/// <c>Open</c> also drives the door's glow and its reachability contribution, a replica's pathing preview disagrees with
/// what the host is doing.</para>
///
/// <para><b>Why not send the whole door.</b> Everything a door needs to be drawn correctly is <c>openInt</c> plus, for a
/// powered door, whether the power is on - both already boolean. Sending the object through the Scribe channel would
/// re-send its hit points, stuff, quality and comps every time a colonist walked through, which is several times a
/// minute per door.</para>
internal static class DoorSync
{
    /// <summary>Last state sent per door, so an unchanged door costs one comparison.</summary>
    private static readonly Dictionary<int, bool> sent = [];

    private static AccessTools.FieldRef<Building_Door, bool>? openField;

    internal static int ChangesSent { get; private set; }
    internal static int ChangesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Collects every door whose open state differs from what was last sent.</summary>
    internal static List<ReplicaDoor>? Publish()
    {
        List<ReplicaDoor>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
            {
                var doors = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
                for (var i = 0; i < doors.Count; i++)
                {
                    if (doors[i] is not Building_Door door) continue;
                    var id = door.thingIDNumber;
                    live.Add(id);
                    var open = door.Open;
                    if (sent.TryGetValue(id, out var previous) && previous == open) continue;
                    sent[id] = open;
                    (changed ??= []).Add(new ReplicaDoor(id, open));
                    ChangesSent++;
                }
            }
            // A door that was destroyed or deconstructed never needs an entry again, and keeping it would make the
            // dictionary grow for the life of the process.
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] door publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>Applies one door's state.
    ///
    /// <para>Writes <c>openInt</c> through a cached field reference rather than calling <c>Open()</c>: the field is
    /// protected and there is no setter - the only public way to move it is <c>StartManualOpenBy</c> and friends, which
    /// also register the pawn as an opener and start a close timer. A replica is not opening the door, it is being told
    /// what the authority already decided.</para>
    /// </summary>
    internal static void Apply(ReplicaDoor state)
    {
        try
        {
            var door = FindThing(state.Id) as Building_Door;
            if (door is null) return;
            if (door.Open == state.Open) return;
            // `OpenField` is a delegate: the assignment form is `OpenField(door) = value`, not a property setter.
            OpenField(door) = state.Open;
            ChangesApplied++;
            // The door's own reachability footprint changes with the state. Vanilla does this from
            // `CheckClearReachabilityCacheBecauseOpenedOrClosed` (`RimWorld/Building_Door.cs:603`), which calls
            // `Map.reachability.ClearCacheForHostile(this)` - only the entries that involve this door, not the whole
            // map. The same call is made here for the same reason: a door flips several times a minute and a colony has
            // dozens of them, so clearing the whole cache each time would be a measurable regression.
            if (door.Spawned) door.Map.reachability.ClearCacheForHostile(door);
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] door apply failed (" + state.Id + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary><c>Building_Door.openInt</c> is protected and has no setter, so it is reached through a cached
    /// <c>FieldRef</c>. Built once per process; a missing field throws here rather than per frame.
    ///
    /// <para>The field reference is a <i>delegate</i>, not a property: <c>fieldRef(door) = value</c> is the assignment
    /// form, <c>OpenField(door) = value</c> does not compile (CS0131). Assigning to the result of a method call would
    /// have been a silent no-op in a language that allowed it, which is exactly the kind of failure that leaves a door
    /// frozen with no error anywhere.</para></summary>
    private static AccessTools.FieldRef<Building_Door, bool> OpenField =>
        openField ??= AccessTools.FieldRefAccess<Building_Door, bool>("openInt");

    private static Thing? FindThing(int id)
    {
        foreach (var map in Find.Maps)
        {
            var things = map.listerThings.AllThings;
            for (var i = 0; i < things.Count; i++)
                if (things[i] is { } thing && thing.thingIDNumber == id) return thing;
        }
        return null;
    }

    internal static void Reset() => sent.Clear();

    internal static string Summary() =>
        $"门 发出 {ChangesSent} / 应用 {ChangesApplied}（失败 {ApplyFailures}）";
}
