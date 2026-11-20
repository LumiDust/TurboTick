using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>A pawn's mental break, shown on a replica that never breaks down.
///
/// <para><b>Why a replica needs this.</b> A mental state is started by a mood check inside
/// <c>MentalStateHandler.MentalStateHandlerTickInterval</c>, which runs from <c>Pawn_MindState</c>'s interval tick - a
/// tick a replica does not run. So a colonist who has a breakdown on the host keeps whatever state the save was loaded
/// with: standing calmly, working, while the authority's copy is wandering in a daze or attacking a wall. The player sees
/// a colony that is not in trouble.</para>
///
/// <para><b>Why the def is sent and the object is rebuilt.</b> A <c>MentalState</c> is a live object with behaviour -
/// <c>MentalState_Wander</c> picks a destination, <c>MentalState_SocialFighting</c> holds the other pawn - and its
/// <c>ExposeData</c> is deep. Rebuilding it on a replica through <c>TryStartMentalState</c> would run that behaviour
/// against a pawn whose AI is not running. What the player sees, though, is entirely decided by the def: the state
/// label in the inspect pane, the icon over the pawn, and <c>Pawn.InMentalState</c> being true. So the def travels and the
/// replica installs the state through the handler's own field, without running <c>PreStart</c> or any behaviour.</para>
///
/// <para><b>Why a change channel and not a per-frame field.</b> Mental breaks happen a few times a season; a per-pawn
/// field on every frame would cost a string per pawn per frame to describe a state that almost never changes. The
/// per-pawn <c>PawnHome</c>-style dictionary records the last def sent.</para>
internal static class MentalStateSync
{
    /// <summary>Last def name sent per pawn, or an empty string for "not in a mental state".</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>Cached access to <c>MentalStateHandler.curStateInt</c>, which is private.</summary>
    private static AccessTools.FieldRef<MentalStateHandler, MentalState>? currentField;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    private static AccessTools.FieldRef<MentalStateHandler, MentalState> Current =>
        currentField ??= AccessTools.FieldRefAccess<MentalStateHandler, MentalState>("curStateInt");

    internal static List<ReplicaMental>? Publish()
    {
        List<ReplicaMental>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.mindState?.mentalStateHandler is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    // The def and the age ride in one string, the shape the skill and inspiration channels already use.
                    // The age is not decoration: `MentalState_Roaming.ShouldExitMapNow` answers "has this pawn been
                    // roaming for more than 60000 ticks" from it, and a replica that always reports zero keeps a pawn on
                    // the map that the host has already sent home.
                    var handler = pawn.mindState.mentalStateHandler;
                    var def = handler.CurStateDef?.defName ?? string.Empty;
                    // The state object is read once: `Current` is a field ref and reading it twice inside one conditional
                    // expression leaves `state` unassigned on the branch where the pattern did not match.
                    var currentState = Current(handler);
                    // Four segments: `def#age#subclass#fit`. The fit cooldown is its own segment because it outlives the
                    // state: `MentalFitGenerator.Notify_RecoveredFromMentalState` sets it to 600 (`:251281`) precisely
                    // when the state *ends*, so a payload that only travelled while a state existed would always report
                    // it as zero at the moment it matters.
                    var fit = FitFor(pawn);
                    var encoded = def.Length == 0 && fit <= 0
                        ? string.Empty
                        : def + "#" + (currentState is null ? 0 : Quantise(currentState.Age))
                          + "#" + (currentState is null ? string.Empty : SubclassPayload(currentState))
                          + "#" + (fit <= 0 ? string.Empty : "fit=" + fit);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaMental(id, encoded));
                    StatesSent++;
                }
            // A pawn that left the map never needs its entry again; leaving it would grow the dictionary for the life
            // of the process, one entry per death and departure.
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] mental state publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaMental state)
    {
        try
        {
            var pawn = FindPawnAny(state.Pawn);
            if (pawn?.mindState?.mentalStateHandler is null || pawn.Dead) return;
            var handler = pawn.mindState.mentalStateHandler;
            // `def#age`, matching the publisher. A bare def is accepted too, so a frame from before this field existed
            // still applies the state instead of being dropped as unparseable.
            var encoded = state.Def ?? string.Empty;
            var hash = encoded.IndexOf('#');
            var name = hash < 0 ? encoded : encoded[..hash];
            var age = hash < 0 ? 0 : ParseAge(encoded, hash);
            // `def#age#payload`, with the payload optional so a frame sent before this field existed still applies.
            var payloadStart = hash < 0 ? -1 : encoded.IndexOf('#', hash + 1);
            var payload = payloadStart < 0 ? string.Empty : encoded[(payloadStart + 1)..];
            // The fit cooldown is the fourth segment; it is read here and applied at the end, because the branches above
            // return early (recovery, unknown def) and the pawn still needs its cooldown in every one of them.
            var fitStart = payload.IndexOf('#');
            var fitText = fitStart < 0 ? string.Empty : payload[(fitStart + 1)..];
            if (fitStart >= 0) payload = payload[..fitStart];
            var want = name is { Length: > 0 } ? DefDatabase<MentalStateDef>.GetNamedSilentFail(name) : null;
            if (name is { Length: > 0 } && want is null)
            {
                ApplyFailures++;
                return;
            }
            ApplyFit(pawn, fitText);
            var current = Current(handler);
            if (current?.def == want)
            {
                // Same state, newer age: write it and leave. `Age` has only a getter, so this is the same reflection
                // `AuthoritativeReplica.ForceToil` uses for `curToilIndex`; nothing else about the state is touched.
                SetAge(current, age);
                WriteSubclassPayload(current, payload);
                return;
            }
            if (want is null)
            {
                // Recovery. `RecoverFromState` is what the host's own tick would call, but it runs the state's
                // `PostEnd` - which for a social fight re-evaluates relations between two live pawns. Clearing the
                // field is what a display replica needs: the label and the icon disappear, which is the whole visible
                // effect of recovery.
                Current(handler) = null;
                StatesApplied++;
                return;
            }
            // Built by hand rather than through `TryStartMentalState`, for the reason in the class summary: that method
            // runs `PreStart`, records a tale, un-drafts, un-drafts mechs and calls the mood need. On a replica every one
            // of those is a side effect on a simulation that is not running.
            var made = (MentalState)Activator.CreateInstance(want.stateClass);
            made.pawn = pawn;
            made.def = want;
            SetAge(made, age);
            WriteSubclassPayload(made, payload);
            Current(handler) = made;
            StatesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] mental state apply failed (pawn " + state.Pawn + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>What the census shows: how many pawns are in a state, and each one's encoded payload.
    ///
    /// <para>The encoded string is printed rather than a count, because the whole point of this round is that the age is
    /// part of it - "1 in a state" cannot tell a payload of <c>Roaming#12000</c> from <c>Roaming#0</c>, and the second one
    /// is the defect.</para></summary>
    internal static string Preview()
    {
        try
        {
            var states = 0;
            var withAge = 0;
            var encoded = new List<string>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var handler = pawn?.mindState?.mentalStateHandler;
                    var state = handler?.CurState;
                    if (state?.def is null) continue;
                    states++;
                    if (state.Age > 0) withAge++;
                    // The raw age here, not the quantised one: the census exists to show whether the value is moving,
                    // and printing the value that gets rounded down to zero would hide exactly that.
                    // The whole payload, subclass part included: printing only `def#age` made the census unable to show the
                    // very field this round added, so `hits=0` and "no subclass payload at all" read the same.
                    if (encoded.Count < 4)
                        encoded.Add(pawn!.thingIDNumber + ":" + state.def.defName + "#" + state.Age
                            + "#" + SubclassPayload(state));
                }
            // The cooldown is counted separately from the states, because it outlives them: a pawn with no mental state at
            // all can still be blocked from another fit, and "0 个精神异常" alone reads as "nothing here is missing".
            var cooling = 0;
            var longest = 0;
            foreach (var map in Find.Maps)
                foreach (var coolingPawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (coolingPawn?.mindState?.mentalFitGenerator is null) continue;
                    var left = AccessTools.Field(typeof(MentalFitGenerator), "ticksUntilCanDoMentalFit")
                        ?.GetValue(coolingPawn.mindState.mentalFitGenerator) as int? ?? 0;
                    if (left <= 0) continue;
                    cooling++;
                    if (left > longest) longest = left;
                }
            var fitText = cooling == 0 ? "无冷却" : cooling + " 人在冷却（最长 " + longest + " tick）";
            if (states == 0) return "0 个精神异常（没有崩溃中的小人） / " + fitText + " / 发 " + StatesSent;
            return $"{states} 个精神异常 / {withAge} 个 Age>0"
                + " / " + string.Join("，", encoded) + $" / 发 {StatesSent}";
        }
        catch (Exception error) { return "读取失败：" + error.GetType().Name; }
    }

    /// <summary>The quantum applied to the age.
    ///
    /// <para>Measured: 12000 was too coarse. `ShouldExitMapNow` compares against 60000, so five steps cover the whole
    /// life of a roaming state - but a census that printed the quantised value showed <c>Roaming#0</c> next to
    /// "1 with Age&gt;0", which reads as a contradiction and says nothing about whether the age is moving. 3000 is a
    /// twentieth of a day: twenty steps to the exit threshold, still far coarser than the 30-tick interval the game
    /// itself advances this counter on.</para></summary>
    private const int AgeQuantum = 3000;

    private static long Quantise(int age) => age <= 0 ? 0 : (age / AgeQuantum) * (long)AgeQuantum;

    /// <summary>The exact string this channel would send for one pawn, for the probe.
    ///
    /// <para>Exposed because the counters are invisible: a replica that drops `hits` renders the same pawn, the same label and
    /// the same icon as one that carries it, and the only observable difference is how soon the state ends. Reading the
    /// payload is the only way to see it on a paused server.</para></summary>
    internal static string? PayloadFor(Pawn pawn)
    {
        var handler = pawn?.mindState?.mentalStateHandler;
        var def = handler?.CurStateDef?.defName;
        if (handler is null) return null;
        var current = Current(handler);
        var fit = FitFor(pawn);
        if (def is not { Length: > 0 } && fit <= 0) return null;
        return (def ?? string.Empty) + "#" + (current is null ? 0 : Quantise(current.Age))
            + "#" + (current is null ? string.Empty : SubclassPayload(current))
            + "#" + (fit <= 0 ? string.Empty : "fit=" + fit);
    }

    /// <summary>Which counter each mental-state subclass keeps that its own `ExposeData` writes and its own
    /// `MentalStateTick` advances.
    ///
    /// <para>Why these and not the base fields: the base <c>MentalState.ExposeData</c> (<c>:251653</c>) writes
    /// <c>def</c>/<c>age</c>/<c>causedBy*</c>/<c>forceRecoverAfterTicks</c>, and the first two already travel. The subclasses
    /// add their own, and each is the number that ends the state or counts what it has done:
    /// <c>MentalState_HumanityBreak.ticksToRamble</c> (<c>:252274</c>) and
    /// <c>MentalState_InhumanRambling.ticksToRamble</c> (<c>:252321</c>) count down to the ramble;
    /// <c>MentalState_SadisticRageTantrum.hits</c> (<c>:252903</c>) counts hits delivered;
    /// <c>MentalState_SlaughterThing.thingsSlaughtered</c> (<c>:252796</c>) stops the state at
    /// <c>MaxThingsSlaughtered</c> (<c>:252840</c>). A replica that rebuilds the state without them shows a rambling colonist
    /// who will never leave and a slaughterer who has killed nothing.</para>
    ///
    /// <para>Reflection rather than four typed branches because the field is <c>protected</c> on two of the four and the
    /// subclass may be absent (a DLC the peer does not run) - a missing field must send nothing, not throw.</para>
    /// </summary>
    private static readonly (string Type, string Field)[] SubclassFields =
    [
        ("MentalState_HumanityBreak", "ticksToRamble"),
        ("MentalState_InhumanRambling", "ticksToRamble"),
        ("MentalState_SadisticRageTantrum", "hits"),
        ("MentalState_SlaughterThing", "thingsSlaughtered"),
    ];

    /// <summary>`field=value`, or an empty string when this state has no counter worth sending.</summary>
    private static string SubclassPayload(MentalState state)
    {
        var typeName = state.GetType().Name;
        foreach (var entry in SubclassFields)
        {
            if (typeName != entry.Type) continue;
            var info = AccessTools.Field(state.GetType(), entry.Field);
            if (info?.GetValue(state) is int value)
                // Quantised to 300 ticks: these are countdowns read by a comparison against a time span, so three in-game
                // minutes of error cannot change any outcome, and an unquantised per-tick counter would make this channel
                // churn every frame - the same defect the plant sweep and `countDecayFrom` both hit.
                return entry.Field + "=" + (entry.Field == "ticksToRamble" ? Quantise(value) : value);
        }
        return string.Empty;
    }

    private static void WriteSubclassPayload(MentalState state, string payload)
    {
        if (payload is not { Length: > 0 }) return;
        var equals = payload.IndexOf('=');
        if (equals <= 0) return;
        var name = payload[..equals];
        if (!int.TryParse(payload.AsSpan(equals + 1), out var value)) return;
        var info = AccessTools.Field(state.GetType(), name);
        // Only `int`: every field in the table is one, and a wrong type here would mean the table and the game disagree.
        if (info?.FieldType == typeof(int)) info.SetValue(state, value);
    }

    /// <summary>How many ticks this pawn still has to wait before another random mental fit may fire.
    ///
    /// <para><b>Why it travels.</b> <c>MentalFitGenerator.TickInterval</c> (<c>:251254</c>) only decrements the counter when
    /// it is positive, and the branch that actually starts a fit is the else of that same test - so a replica that keeps the
    /// counter at its load-time value never lets a fit happen, and a pawn on the authority that has just recovered is being
    /// held back by a cooldown the replica does not know about. <c>Notify_RecoveredFromMentalState</c> (<c>:251279</c>) is
    /// the only writer, and it runs when a state ends - i.e. exactly when this channel's def segment goes empty, which is
    /// why the cooldown has its own segment.</para>
    ///
    /// <para>Quantised to 150: the counter is only ever compared against zero, and 150 is the interval the generator itself
    /// is stepped on (<c>pawn.IsHashIntervalTick(150, delta)</c> `<c>:251265</c>).</para></summary>
    private const int FitQuantum = 150;

    private static long FitFor(Pawn pawn)
    {
        var generator = pawn.mindState?.mentalFitGenerator;
        if (generator is null) return 0;
        if (AccessTools.Field(typeof(MentalFitGenerator), "ticksUntilCanDoMentalFit")?.GetValue(generator) is not int ticks)
            return 0;
        return ticks <= 0 ? 0 : (ticks / FitQuantum) * (long)FitQuantum;
    }

    private static void ApplyFit(Pawn pawn, string text)
    {
        if (text is not { Length: > 3 } || !text.StartsWith("fit=", StringComparison.Ordinal)) return;
        if (!int.TryParse(text.AsSpan(4), out var ticks) || ticks <= 0) return;
        var generator = pawn.mindState?.mentalFitGenerator;
        if (generator is null) return;
        AccessTools.Field(typeof(MentalFitGenerator), "ticksUntilCanDoMentalFit")?.SetValue(generator, ticks);
    }

    private static int ParseAge(string encoded, int hash)
    {
        var tail = encoded[(hash + 1)..];
        // The tail is the quantised tick count; a negative or unparseable one is treated as "no age known" rather than
        // refusing the state, because the def is what draws the pawn.
        return int.TryParse(tail, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private static System.Reflection.FieldInfo? ageField;

    private static void SetAge(MentalState state, int age)
    {
        if (age <= 0) return;
        try
        {
            ageField ??= AccessTools.Field(typeof(MentalState), "age");
            if (ageField?.GetValue(state) is int current && current == age) return;
            ageField?.SetValue(state, age);
        }
        catch (Exception) { /* a state whose field moved keeps the age it has; the def still draws */ }
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
        $"精神崩溃 发出 {StatesSent} / 应用 {StatesApplied}（失败 {ApplyFailures}）";
}
