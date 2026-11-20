using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The scribed state of a world object, minus the four fields that already have their own channel.
///
/// <para><b>Why this channel exists.</b> <see cref="WorldObjectSync"/> sends where an object is: <c>ID</c>, tile, layer
/// and def name. Everything else a world object carries was never sent at all, because a world object is not a map thing:
/// the building sweep iterates <c>listerThings</c>, the item sweep the item category, the pawn sweep <c>mapPawns</c>, and
/// the plant/filth sweep only those two categories. A settlement's own <c>timedDetectionRaids</c> comp, a gravship's
/// <c>traveledPct</c> and a travelling transporter's progress therefore all sat frozen on every replica.</para>
///
/// <para>Measured on this server with <c>script-worldcomp</c>: <c>Settlement #0</c>'s own fragment is 554 B and carries
/// <c>&lt;ticksLeftToForceExitAndRemoveMap&gt;7200&lt;/ticksLeftToForceExitAndRemoveMap&gt;</c> and
/// <c>&lt;ticksLeftTillNotifyPlayer&gt;3600&lt;/ticksLeftTillNotifyPlayer&gt;</c> inline at the top level - <b>there is no
/// <c>&lt;comps&gt;</c> wrapper</b>, because <c>WorldObject.ExposeData</c> writes its own fields and then calls
/// <c>comps[i].PostExposeData()</c> in the same element (<c>:798523-798556</c>). That is why this channel compares whole
/// fragments minus a strip list rather than slicing out a subtree the way <see cref="QuestPartSync"/> can.</para>
///
/// <para><b>Why the strip list.</b> The fragment repeats fields that already travel and would otherwise make this channel
/// churn or fight: <c>def</c>, <c>tile</c>, <c>ID</c> belong to <see cref="WorldObjectSync"/>; <c>tickDelta</c> is a frame
/// counter that <c>WorldObject.DoTick</c> increments every tick (<c>:798651</c>) and would make every object differ from
/// itself forever; <c>faction</c> and <c>questTags</c> are set at generation and never move. Everything else is kept.</para>
///
/// <para><b>Why quantising is not needed.</b> Nothing in the remaining set is a per-tick counter: the countdowns step on an
/// interval tick and by deltas of ten or more, and <c>traveledPct</c> is a fraction. That was the lesson of <c>ageInt</c>
/// and <c>countDecayFrom</c> - quantise only where a tick-scale number would otherwise be compared every frame, and here
/// there is none.</para></summary>
internal static class WorldObjectStateSync
{
    /// <summary>Element names whose value is already on the wire through another channel, or that churn on their own.
    /// Stripped before the fragment is used as a change key.</summary>
    private static readonly string[] Stripped =
    [
        "def",          // WorldObjectSync
        "tile",         // WorldObjectSync, and it is the whole point of that channel
        "ID",           // WorldObjectSync
        "tickDelta",    // `WorldObject.DoTick` increments this every tick (`:798651`)
        "faction",      // set at generation, never moves
        "questTags",    // ditto
    ];

    /// <summary>Last stripped fragment per world object id. An object whose fragment did not change costs nothing.</summary>
    private static readonly Dictionary<int, string> lastState = [];

    /// <summary>The tick of the last pass. A world object's state moves on an interval tick of its own - `WorldObject.DoTick`
    /// only calls `TickInterval` when `tickDelta > UpdateRateTicks` (`:798651`), and `UpdateRateTicks` is
    /// `GenTicks.GetCameraUpdateRate(this)`, i.e. hundreds of ticks - so re-serialising all 254 objects on every published
    /// frame is pure waste. Measured before this gate: 76 454 fragments per 600 ticks, of which 495 were ever sent.</summary>
    private static int lastPassTick = -1;

    /// <summary>How often a pass runs. One in-game minute: finer than any countdown this channel carries steps, and coarse
    /// enough that the serialisation cost stays measurable rather than dominant.</summary>
    private const int PassInterval = 60;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>How many fragments were built, and how many of those came back empty after stripping. Printed so the
    /// census can tell "no world object has state" from "the strip list removed everything".</summary>
    internal static long FragmentsBuilt { get; private set; }
    internal static long EmptyAfterStrip { get; private set; }

    /// <summary>One sample fragment, flattened. A channel whose payload cannot be read is a channel nobody can check.</summary>
    internal static string? LastSample { get; private set; }

    /// <summary>The first fragment actually sent that carried a countdown element, and how many such fragments were sent.
    ///
    /// <para>The last-sent sample is not enough to make this claim: on this save 495 fragments were sent and the one that
    /// happened to go last was a settlement with no countdown in it, so the census read "no countdown" while the channel was
    /// carrying them. Counting the ones that contain the element is the only reading that answers "does this channel carry
    /// world-object comp state" rather than "what did the last object look like".</para></summary>
    internal static string? CountdownSample { get; private set; }
    internal static long CountdownFragments { get; private set; }

    /// <summary>The element names that come from a <c>WorldObjectComp</c> rather than from the object itself. Used only to
    /// make the census able to tell the two apart, never to filter the payload.</summary>
    private static readonly string[] CompMarkers =
    [
        "ticksLeftToForceExitAndRemoveMap",   // TimedDetectionRaids.ticksLeftToSendRaid (`:783488`)
        "ticksLeftTillNotifyPlayer",          // TimedDetectionRaids (`:783489`)
        "raidsSentCount",                     // TimedDetectionRaids (`:783490`)
        "ticksLeftToForceExitAndRemoveMap2",  // placeholder, never matches; keeps the array obviously a marker list
    ];
    internal static string? LastDiagnostic { get; private set; }

    internal static List<ReplicaWorldObjectState>? Publish()
    {
        try
        {
            var now = Find.TickManager?.TicksGame ?? -1;
            // The first pass always runs, so a server that starts paused still publishes its objects once. After that the
            // interval applies, and a frame that falls between passes simply carries nothing on this channel.
            if (lastPassTick >= 0 && now >= 0 && now - lastPassTick < PassInterval) return null;
            lastPassTick = now;
            List<ReplicaWorldObjectState>? changed = null;
            var seen = new HashSet<int>();
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                if (each is null || !each.Spawned) continue;
                seen.Add(each.ID);
                var body = Fragment(each);
                if (body is null) continue;
                if (lastState.TryGetValue(each.ID, out var before) && before == body) continue;
                lastState[each.ID] = body;
                LastSample = body.Replace("\r", " ").Replace("\n", " ");
                StatesSent++;
                foreach (var marker in CompMarkers)
                {
                    if (!body.Contains(marker, StringComparison.Ordinal)) continue;
                    CountdownFragments++;
                    CountdownSample ??= body.Replace("\r", " ").Replace("\n", " ");
                    break;
                }
                (changed ??= []).Add(new ReplicaWorldObjectState(each.ID, each.def?.defName, body));
            }
            // Objects that left the world are forgotten here rather than in Apply: the id is the only key, and a settlement
            // destroyed on the authority must not keep a stale fragment around to be re-sent if the id is reused.
            if (lastState.Count > seen.Count)
                foreach (var stale in lastState.Keys.Where(id => !seen.Contains(id)).ToList())
                    lastState.Remove(stale);
            return changed;
        }
        catch (Exception error)
        {
            LastDiagnostic = error.GetType().Name + ": " + error.Message;
            Log.Warning("[TurboTick] world object state publish: " + LastDiagnostic);
            return null;
        }
    }

    /// <summary>The fragment this channel would send for one object, for the probe. Exposed because the only way to prove
    /// the strip list does not remove comp fields is to run it against an object whose comp fields are known to be
    /// non-default - and on this save no world object has one.</summary>
    internal static string? FragmentFor(WorldObject worldObject) => Fragment(worldObject);

    /// <summary>The object's Scribe fragment with the fields another channel owns removed, or null when there is nothing
    /// left to send.</summary>
    private static string? Fragment(WorldObject worldObject)
    {
        var xml = ScribeBridge.Save(worldObject);
        if (xml is not { Length: > 0 }) { LastDiagnostic = "保存返回空"; return null; }
        FragmentsBuilt++;
        foreach (var tag in Stripped)
            xml = StripElement(xml, tag);
        if (xml.Length == 0) { EmptyAfterStrip++; return null; }
        return xml;
    }

    /// <summary>Removes the first <c>&lt;tag&gt;…&lt;/tag&gt;</c> (self-closing or not) from a fragment.
    ///
    /// <para>Written against the text because the fields live in private members the game only writes through
    /// <c>ExposeData</c>; building a filtered copy of the object would mean writing to the running colony.</para></summary>
    private static string StripElement(string xml, string tag)
    {
        var open = xml.IndexOf("<" + tag, StringComparison.Ordinal);
        if (open < 0) return xml;
        // The opening tag may carry attributes (`<def>`, `<tile>`, `<questTags IsNull="True" />`), so the end of the tag is
        // located rather than assuming a bare form.
        var tagEnd = xml.IndexOf('>', open);
        if (tagEnd < 0) return xml;
        // Self-closing: nothing between the brackets to remove.
        if (xml[tagEnd - 1] == '/') return xml.Remove(open, tagEnd - open + 1);
        var close = xml.IndexOf("</" + tag + ">", tagEnd, StringComparison.Ordinal);
        if (close < 0) return xml;
        return xml.Remove(open, close + tag.Length + 3 - open);
    }

    /// <summary>Applies one fragment. Loads through <see cref="ScribeBridge.Load"/>, which is the same path the quest and
    /// hediff channels use, so identity (def, id, map, position) is preserved by the bridge rather than by this method.
    /// </summary>
    internal static void Apply(ReplicaWorldObjectState state)
    {
        try
        {
            if (state.Body is not { Length: > 0 })
            {
                StatesApplied++;
                return;
            }
            foreach (var each in Find.WorldObjects.AllWorldObjects)
            {
                if (each is null || each.ID != state.Id) continue;
                // The def name is the only guard against id reuse across a save that grew between peers - the same reason
                // `WorldObjectSync.Apply` checks it before moving anything.
                if (state.Def is { Length: > 0 } expected && each.def?.defName != expected) return;
                if (!ScribeBridge.Load(each, state.Body))
                {
                    ApplyFailures++;
                    return;
                }
                StatesApplied++;
                return;
            }
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] world object state apply failed ({state.Id}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        lastState.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
        FragmentsBuilt = 0;
        lastPassTick = -1;
        EmptyAfterStrip = 0;
        LastSample = null;
        CountdownSample = null;
        CountdownFragments = 0;
        LastDiagnostic = null;
    }

    internal static string Summary()
    {
        var objects = 0;
        try { objects = Find.WorldObjects?.AllWorldObjects?.Count ?? 0; } catch (Exception) { }
        return $"{objects} 个 / 建 {FragmentsBuilt} 片段（去字段后空 {EmptyAfterStrip}）"
            + $" / 发 {StatesSent} / 应用 {StatesApplied}（失败 {ApplyFailures}）"
            + $" / 带组件字段 {CountdownFragments}"
            + (LastDiagnostic is { Length: > 0 } trouble ? " / 诊断 " + trouble : "");
    }
}
