using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>A pawn's inspiration, shown on a replica that never has one.
///
/// <para><b>Why a replica needs this.</b> Inspirations are rolled inside
/// <c>InspirationHandler.InspirationHandlerTickInterval</c> (a 100-tick interval, <c>StartInspirationMTBDaysAtMaxMood</c>
/// 10 days, gated on mood above 0.5), which a replica does not run. So a colonist who becomes inspired on the host shows
/// nothing on the replica: no icon in the needs card, no line in the inspect pane, and - because
/// <c>InspirationHandler.Inspired</c> drives work givers - the replica's own copy of that pawn would never take the
/// inspired work even if it were simulating. The player reads "no inspiration anywhere in the colony".</para>
///
/// <para><b>Why the def and the age are sent, and not the object.</b> An <c>Inspiration</c> is behaviour: its
/// <c>InspirationTick</c> expires it, its <c>PostStart</c> sends a letter, its <c>PostEnd</c> may grant a reward. Rebuilding
/// it through <c>TryStartInspiration</c> would fire all of that against a pawn whose simulation is not running - the
/// letter in particular, which the letter channel already carries separately and would then arrive twice. What the player
/// sees is the def (which icon, which label) and the age (how much of the duration is left); both are values, so both
/// travel.</para>
///
/// <para><b>Why the age is quantised to whole days.</b> The needs card shows a progress bar over the inspiration's
/// duration, measured in days. Sending raw ticks would put an entry per inspired pawn on every frame for a value that
/// changes by 1/60000 of a day per tick; one day of granularity is what the bar can actually show, and an inspiration
/// lasts several days.</para>
internal static class InspirationSync
{
    /// <summary>Last state sent per pawn, as `def#ageDays`. An empty string means "not inspired".</summary>
    private static readonly Dictionary<int, string> sent = [];

    private static AccessTools.FieldRef<InspirationHandler, Inspiration>? currentField;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    private static AccessTools.FieldRef<InspirationHandler, Inspiration> Current =>
        currentField ??= AccessTools.FieldRefAccess<InspirationHandler, Inspiration>("curState");

    internal static List<ReplicaInspiration>? Publish()
    {
        List<ReplicaInspiration>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.mindState?.inspirationHandler is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var handler = pawn.mindState.inspirationHandler;
                    var encoded = handler.CurStateDef is { } def
                        // AgeDays is a float over 60000 ticks per day; the card shows a bar in days, so that is the unit.
                        ? def.defName + "#" + (int)handler.CurState.AgeDays
                        : string.Empty;
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaInspiration(id, encoded));
                    StatesSent++;
                }
            // Entries for pawns that left the map would accumulate for the life of the process.
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] inspiration publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaInspiration state)
    {
        try
        {
            var pawn = FindPawnAny(state.Pawn);
            if (pawn?.mindState?.inspirationHandler is null || pawn.Dead) return;
            var handler = pawn.mindState.inspirationHandler;
            var (name, days) = Split(state.State);
            var want = name.Length > 0 ? DefDatabase<InspirationDef>.GetNamedSilentFail(name) : null;
            if (name.Length > 0 && want is null) { ApplyFailures++; return; }
            var current = Current(handler);
            if (current?.def == want && (want is null || (int)current.AgeDays == days)) return;
            if (want is null)
            {
                // Recovery. `EndInspiration` runs `PostEnd`, which can grant a reward or apply a thought - a side effect
                // on a simulation that is not running. Clearing the field is the whole visible effect.
                Current(handler) = null;
                StatesApplied++;
                return;
            }
            if (current is null)
            {
                // Built by hand, not through `TryStartInspiration`: that calls `PostStart`, which sends a letter the
                // letter channel already delivers separately - it would arrive twice.
                var made = (Inspiration)Activator.CreateInstance(want.inspirationClass);
                made.def = want;
                made.pawn = pawn;
                Current(handler) = made;
            }
            // The age is written through the private field because `Age` is a getter over it. Rebuilding the object to
            // change the age would lose the def and re-run construction for a number the bar reads once.
            AgeField(handler.CurState) = days * 60000;
            StatesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] inspiration apply failed (pawn " + state.Pawn + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    private static AccessTools.FieldRef<Inspiration, int>? ageField;

    private static AccessTools.FieldRef<Inspiration, int> AgeField =>
        ageField ??= AccessTools.FieldRefAccess<Inspiration, int>("age");

    private static (string Name, int Days) Split(string state)
    {
        if (state.Length == 0) return (string.Empty, 0);
        var hash = state.IndexOf('#');
        if (hash < 0) return (state, 0);
        return (state[..hash], int.TryParse(state[(hash + 1)..], out var days) ? days : 0);
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

    internal static string Summary() =>
        $"灵感 发出 {StatesSent} / 应用 {StatesApplied}（失败 {ApplyFailures}）";
}
