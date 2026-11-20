using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>How much time is left on a timed condition, for a replica whose conditions are rebuilt from scratch.
///
/// <para><b>Why this is needed even though severity travels.</b> The hediff channel rebuilds a pawn's conditions by removing
/// every one and calling <c>HediffMaker.MakeHediff</c> for each entry
/// (<see cref="AuthoritativeReplica.ApplyHediffs"/>) - it has to, because a def, a body part and a severity are the only
/// things that survive as text. A rebuild runs <c>CompPostPostAdd</c>, which is where the timed comps <em>set</em> their
/// counters: <c>HediffComp_Disappears.ticksToDisappear</c> and <c>HediffComp_KillAfterDays.ticksLeft</c> both start again
/// from their def's full duration. So an anaesthetic that has 400 ticks left on the host reads as a fresh full dose on the
/// replica forever - the bar never moves because the only thing that decrements it is
/// <c>CompPostTick</c>, which a replica never reaches.</para>
///
/// <para><b>One channel for three comps, deliberately.</b> All three carry the same fact - an integer countdown that the game
/// decrements - so the wire format holds a name and a number per condition and the apply step writes it onto whichever comp
/// is present. Three channels would be three chances to forget one, and the player-visible consequence is identical.</para>
///
/// <para><b>Tend quality is in here too.</b> <c>HediffComp_TendDuration.totalTendQuality</c> is not a countdown but it has
/// exactly the same failure: <c>CompPostPostAdd</c> leaves it at zero, so a tended wound reads as untended on the replica -
/// the health tab shows no tend mark and the tend-quality number is gone. It is carried as the same name/number pair.</para>
/// </summary>
internal static class HediffTimerSync
{
    /// <summary>The comps that carry a countdown the game decrements, the field that holds it, and how coarsely to send it.
    ///
    /// <para><b>Why six more comps are in this table.</b> Same defect, one level deeper: every one of these is written only by
    /// that comp's <c>CompPostTickInterval</c> and scribed by its <c>CompExposeData</c>, so a rebuild runs
    /// <c>CompPostPostAdd</c> and starts the counter over from the def's value. Nothing else moves it, so on a replica each of
    /// these stays frozen at its initial value forever. The player-visible ones are worth naming:
    /// <c>HediffComp_Chargeable</c> gates <c>CanActivate</c> (charge &gt;= minChargeToActivate) and prints the charge in the
    /// health tab (<c>CompLabelInBracketsExtra</c>), so a partially drained implant reads as full and an ability that should
    /// be unavailable looks ready; <c>HediffComp_MessageAfterTicks</c> is the only thing that ever produces its letter;
    /// <c>HediffComp_HealPermanentWounds</c> and <c>HediffComp_SelfHeal</c> are the only writers of the health change they
    /// exist for; <c>HediffComp_Infecter</c> decides whether a wound goes septic; <c>HediffComp_ChanceToRemove</c> decides
    /// when the condition ends.</para>
    ///
    /// <para><b>Quantum is per entry, not global.</b> The countdowns are re-encoded every frame and the encoding is also the
    /// change key, so a raw per-tick integer turns this channel into the busiest one on the wire. Rounding to the nearest
    /// multiple of 250 ticks (four in-game minutes) bounds the error at two minutes, which cannot move any of these decisions
    /// across. The two entries that were already shipped keep quantum 1: they are verified, and a save with none of these comps
    /// sends nothing either way.</para>
    /// </summary>
    internal static readonly (string Comp, string Field, string Wire, int Quantum)[] Timers =
    [
        ("HediffComp_Disappears", "ticksToDisappear", "disappears", 1),
        ("HediffComp_KillAfterDays", "ticksLeft", "kill", 1),
        ("HediffComp_ChanceToRemove", "currentInterval", "chance", 250),
        ("HediffComp_HealPermanentWounds", "ticksToHeal", "wounds", 250),
        ("HediffComp_Infecter", "ticksUntilInfect", "infect", 250),
        ("HediffComp_MessageAfterTicks", "ticksUntilMessage", "message", 250),
        ("HediffComp_SelfHeal", "ticksSinceHeal", "selfheal", 250),
    ];

    /// <summary>The one comp counter that is a float rather than an int: <c>HediffComp_Chargeable.charge</c>.</summary>
    private const string ChargeComp = "HediffComp_Chargeable";

    /// <summary>The last block actually handed to the wire, kept for the census. The sent counter alone proves the publish
    /// path runs; only the text proves what shape it carries - and every one of these channels has at some point read green
    /// while shipping the wrong thing.</summary>
    internal static string? LastSample { get; private set; }

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Last encoded timer block sent per pawn, so an unchanged set costs nothing.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static void Reset()
    {
        sent.Clear();
        LastSample = null;
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
    }

    /// <summary>
    /// Encodes every timed condition on a pawn as <c>hediffKey;wireName;value</c> entries joined by <c>|</c>, or null when the
    /// pawn has none.
    ///
    /// <para>The hediff key must be the same one the hediff channel uses to identify a condition, or the two cannot be
    /// matched up on the far side: that channel rebuilds from <c>defName#partDefName#severity</c> and so the comp has to be
    /// named the same way. Severity is part of the key rather than a separate index because two conditions of the same def
    /// on the same part would otherwise be indistinguishable, and that is exactly the case where a wrong timer matters
    /// most.</para>
    /// </summary>
    internal static string? Encode(Pawn pawn)
    {
        try
        {
            var hediffs = pawn?.health?.hediffSet?.hediffs;
            if (hediffs is null || hediffs.Count == 0) return null;
            StringBuilder? text = null;
            foreach (var hediff in hediffs)
            {
                if (hediff?.def?.defName is not { Length: > 0 } name) continue;
                var key = name + "#" + (hediff.Part?.def?.defName ?? "")
                    + "#" + hediff.Severity.ToString("R", CultureInfo.InvariantCulture);
                // The condition's own age, not a comp timer: `Hediff.TickInterval` is the only writer
                // (`ageTicks += delta`) and the rebuild through `HediffMaker.MakeHediff` starts it at zero, so an
                // injury the host has been carrying for two days reads as fresh here. That is not cosmetic:
                // `Hediff_Injury.BleedRate` returns 0 once `ageTicks >= AgeTicksToStopBleeding` (90000-180000 ticks),
                // so a wound that should have stopped bleeding keeps bleeding on the replica forever.
                // Quantised to whole game hours (2500 ticks). `ageTicks` advances every tick, and the encoding is
                // used as a change key - raw, every wounded pawn would be re-sent every frame and this channel,
                // which exists for counters that move a few times an hour, would become the busiest one.
                // The compare thresholds that read it are 1000, 60000 and 90000-180000 ticks, so an hour of
                // resolution cannot move any of them across.
                var age = ReadInt(hediff, "ageTicks");
                if (age is > 0)
                    age = age.Value / 2500 * 2500;
                if (age is > 0)
                    (text ??= new StringBuilder(64)).Append(key).Append(";age;")
                        .Append(age.Value.ToString(CultureInfo.InvariantCulture)).Append('|');
                foreach (var (compName, field, wire, quantum) in Timers)
                {
                    var comp = FindComp(hediff, compName);
                    if (comp is null) continue;
                    if (ReadInt(comp, field) is not { } value) continue;
                    // Round to nearest rather than truncate: the error is +/- half a quantum instead of a full one, and a
                    // live countdown never rounds down to zero while it still has time on it.
                    if (quantum > 1) value = (value + quantum / 2) / quantum * quantum;
                    (text ??= new StringBuilder(64)).Append(key).Append(';').Append(wire)
                        .Append(';').Append(value.ToString(CultureInfo.InvariantCulture)).Append('|');
                }
                // The charge sits in [0, fullChargeAmount] and its two readers are a threshold comparison and a formatted
                // label, so the value is carried as is - a float comparison at this resolution cannot flip `CanActivate` by
                // rounding, and the label prints a percentage.
                if (FindComp(hediff, ChargeComp) is { } chargeable
                    && ReadFloat(chargeable, "charge") is { } charge)
                    (text ??= new StringBuilder(64)).Append(key).Append(";charge;")
                        .Append(charge.ToString("R", CultureInfo.InvariantCulture)).Append('|');
                // Tend quality: same rebuild loss, different shape (a float the tend action writes).
                var tend = FindComp(hediff, "HediffComp_TendDuration");
                if (tend is not null && ReadFloat(tend, "totalTendQuality") is { } quality && quality > 0f)
                    (text ??= new StringBuilder(64)).Append(key).Append(";tend;")
                        .Append(quality.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            }
            return text?.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Walks the pawn's conditions and asks for the encoding of each, for the frame.</summary>
    internal static List<ReplicaHediffTimers>? Publish()
    {
        List<ReplicaHediffTimers>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.health?.hediffSet is null) continue;
                    var id = pawn.thingIDNumber;
                    var encoded = Encode(pawn);
                    if (encoded is null) { sent.Remove(id); continue; }
                    live.Add(id);
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    LastSample = encoded;
                    (changed ??= []).Add(new ReplicaHediffTimers(id, encoded));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    /// <summary>Restores the counters after the hediff channel has rebuilt the condition list.</summary>
    internal static void Apply(ReplicaHediffTimers state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs is null) return;
            // Left to right, because the key is `def#part#severity` and a severity may itself contain no `;`. Splitting on
            // '|' first keeps the key intact even when a def name contains the separator.
            foreach (var entry in state.Timers.Split('|'))
            {
                if (entry.Length == 0) continue;
                var fields = entry.Split(';');
                if (fields.Length != 3) continue;
                var hediff = FindByKey(hediffs, fields[0]);
                if (hediff is null) continue;
                switch (fields[1])
                {
                    // `ageTicks` is a public field on `Hediff`, so `WriteInt` reaches it the same way it reaches the
                    // private comp timers.
                    case "age":
                        if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age))
                            WriteInt(hediff, "ageTicks", age);
                        break;
                    case "disappears":
                        if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
                            && FindComp(hediff, "HediffComp_Disappears") is { } disappears)
                            WriteInt(disappears, "ticksToDisappear", left);
                        break;
                    case "kill":
                        if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kill)
                            && FindComp(hediff, "HediffComp_KillAfterDays") is { } afterDays)
                            WriteInt(afterDays, "ticksLeft", kill);
                        break;
                    case "chance":
                    case "wounds":
                    case "infect":
                    case "message":
                    case "selfheal":
                    {
                        // Same shape for all five: a private int on a comp named by the wire name. Driven from the table
                        // rather than five near-identical cases, so a new countdown only needs a table row.
                        var row = Array.Find(Timers, t => t.Wire == fields[1]);
                        if (row.Comp is null) break;
                        if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                            && FindComp(hediff, row.Comp) is { } timed)
                            WriteInt(timed, row.Field, ticks);
                        break;
                    }
                    case "charge":
                        if (float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var charge)
                            && FindComp(hediff, ChargeComp) is { } charged)
                            WriteFloat(charged, "charge", charge);
                        break;
                    case "tend":
                        if (float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var quality)
                            && FindComp(hediff, "HediffComp_TendDuration") is { } tend)
                            WriteFloat(tend, "totalTendQuality", quality);
                        break;
                }
            }
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    /// <summary>Whether a condition carries a comp by that type name. Exposed so the census counts the same set the channel
    /// sends - a row that re-lists the comps by hand is the fifth way for the two to drift apart.</summary>
    internal static bool HasComp(Hediff hediff, string compName) => FindComp(hediff, compName) is not null;

    /// <summary>Finds the condition the entry was encoded from: same def, same part, same severity.</summary>
    private static Hediff? FindByKey(List<Hediff> hediffs, string key)
    {
        for (var i = 0; i < hediffs.Count; i++)
        {
            var hediff = hediffs[i];
            if (hediff?.def?.defName is not { Length: > 0 } name) continue;
            var candidate = name + "#" + (hediff.Part?.def?.defName ?? "")
                + "#" + hediff.Severity.ToString("R", CultureInfo.InvariantCulture);
            if (string.Equals(candidate, key, StringComparison.Ordinal)) return hediff;
        }
        return null;
    }

    /// <summary>A comp by type name. Looked up by name rather than by generic argument because these three live in different
    /// assemblies' type hierarchies and a missing one is normal - a hediff without the comp simply has no timer.</summary>
    private static HediffComp? FindComp(Hediff hediff, string compName)
    {
        // `comps` lives on `HediffWithComps`, not on `Hediff` - a plain `Hediff` subclass has none at all, and
        // `HediffUtility.TryGetComp` is the game's own answer to that (it returns null rather than throwing).
        if (hediff is not HediffWithComps withComps) return null;
        var comps = withComps.comps;
        if (comps is null) return null;
        for (var i = 0; i < comps.Count; i++)
            if (comps[i]?.GetType().Name == compName) return comps[i];
        return null;
    }

    // Field access is by reflection: the three comps expose no setter for these values and none of them is public on a
    // stable base type. Cached per (type, field) because this runs once per changed condition per frame.
    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> IntFields = [];
    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> FloatFields = [];

    private static int? ReadInt(object comp, string field)
    {
        var info = Field(comp.GetType(), field, IntFields);
        return info?.GetValue(comp) is int value ? value : null;
    }

    private static void WriteInt(object comp, string field, int value)
        => Field(comp.GetType(), field, IntFields)?.SetValue(comp, value);

    private static float? ReadFloat(object comp, string field)
    {
        var info = Field(comp.GetType(), field, FloatFields);
        return info?.GetValue(comp) is float value ? value : null;
    }

    private static void WriteFloat(object comp, string field, float value)
        => Field(comp.GetType(), field, FloatFields)?.SetValue(comp, value);

    private static System.Reflection.FieldInfo? Field(Type type, string name,
        Dictionary<(Type, string), System.Reflection.FieldInfo?> cache)
    {
        var key = (type, name);
        if (cache.TryGetValue(key, out var cached)) return cached;
        var info = HarmonyLib.AccessTools.Field(type, name);
        cache[key] = info;
        return info;
    }
}
