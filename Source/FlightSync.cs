using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Whether a pawn is on the ground, taking off, flying or landing - and how far into that it is.
///
/// <para><b>The defect.</b> <c>Pawn_FlightTracker.FlightTick</c>
/// (<c>_lab/asm/Assembly-CSharp.decompiled.cs:729158-729207</c>) is the only writer of all five fields, and it is called
/// from <c>Pawn.Tick</c> on the authority. A display replica runs no pawn tick, so every flying pawn stays in whatever
/// state the save was loaded with:</para>
///
/// <list type="bullet">
/// <item><c>FlightState.Grounded</c> → the replica draws the pawn walking while the host's copy is airborne, which is
/// also where the host's copy cannot be shot at and the replica's can be.</item>
/// <item><c>FlightState.Flying</c> → the replica keeps the pawn hovering forever, because <c>flyingTicks</c> never
/// reaches <c>MaxFlightTicks</c> (<c>:729193</c>) and the landing transition never runs.</item>
/// <item><c>flightCooldownTicks</c> never decrements (<c>:729201</c>), so <c>CanFlyNow</c> (<c>:729135-729140</c>) stays
/// false forever after one flight: the ability is permanently unavailable on the replica even after the host's cooldown
/// expired.</item>
/// </list>
///
/// <para><b>Why the state and not the derived booleans.</b> <c>Flying</c> is <c>flightState != Grounded</c>
/// (<c>:729105</c>) and <c>CanFlyNow</c> is three conditions over <c>Flying</c> and <c>flightCooldownTicks</c>. Sending
/// either boolean would lose the difference between taking off and landing - both read as "flying" but
/// <c>PositionOffsetFactor</c> (<c>:729145-729151</c>) evaluates a different curve from a different counter, so the
/// drawn altitude would jump. The four-value enum plus its two counters is the whole state, and it is what
/// <c>ExposeData</c> already writes (<c>:729301-729307</c>).</para>
///
/// <para><b>What is not sent.</b> <c>playing</c> is an <c>AnimationDef</c> derived from the state by
/// <c>GetBestFlyAnimation</c> (<c>:729273</c>) and written by <c>CheckFlyAnimation</c>, which reads <c>Flying</c> - the
/// same shape as sending the numerator rather than a percentage, so the replica recomputes it from the state it was
/// given. <c>pawn</c> is the owner and arrives with the tracker.</para>
///
/// <para><b>Why the cooldown travels quantised.</b> It is decremented once per tick (<c>:729201</c>) and the longest
/// cooldown is measured in hundreds of ticks, so sending every tick would put a row on the wire per flying pawn per
/// second to describe a countdown the replica could not act on anyway - it acts only when the value reaches zero, and
/// that transition is still reported exactly because zero is a fixed point of the quantiser.</para></summary>
internal static class FlightSync
{
    /// <summary>Last encoded state sent per pawn, so an unchanged pawn costs one comparison.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>
    /// <para>Cooldown quantum. The cooldown decides one thing - whether <c>CanFlyNow</c> is true - so the value only has to
    /// be exact at zero. 15 ticks is a quarter second at normal speed: fine enough that the ability reappears when the
    /// host's does, coarse enough that a 600-tick cooldown is 40 frames instead of 600.</para>
    /// </summary>
    private const int CooldownQuantum = 15;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int PawnsFlying { get; private set; }
    internal static int PawnsSeen { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
        PawnsFlying = 0;
        PawnsSeen = 0;
    }

    /// <summary>The four state names, in the enum's own order. Sent by name rather than by number: an enum's numeric
    /// value is not part of the save format, and a channel that puts it on the wire breaks silently if the enum is ever
    /// reordered.</summary>
    private static readonly string[] StateNames = ["grounded", "takingoff", "flying", "landing"];

    internal static List<ReplicaFlight>? Publish()
    {
        List<ReplicaFlight>? changed = null;
        var flying = 0;
        var seen = 0;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var tracker = pawn?.flight;
                    if (tracker is null) continue;
                    seen++;
                    // A pawn that has never flown is the common case: `flightState` is Grounded, both counters are at their
                    // defaults, and there is nothing to say. Skipping it keeps a battle map's worth of walkers off the wire.
                    var state = StateName(tracker);
                    if (state is null) continue;
                    // All five fields are private (`:729077-729085`), so the read is by reflection too. There is no public
                    // accessor for any of them: `Flying` is the only one exposed and it collapses four states into one
                    // boolean, which is exactly the information this channel exists to carry.
                    var flyingTicks = ReadInt(tracker, ref FlyingTicksField, "flyingTicks");
                    var cooldown = ReadInt(tracker, ref CooldownField, "flightCooldownTicks");
                    var lerpTick = ReadInt(tracker, ref LerpField, "lerpTick");
                    if (state != "grounded" || cooldown > 0) flying++;
                    var id = pawn!.thingIDNumber;
                    live.Add(id);
                    var encoded = state + ":" + flyingTicks.ToString(CultureInfo.InvariantCulture)
                        + ":" + Quantise(cooldown).ToString(CultureInfo.InvariantCulture)
                        + ":" + lerpTick.ToString(CultureInfo.InvariantCulture);
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaFlight(id, encoded));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 飞行发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        PawnsFlying = flying;
        PawnsSeen = seen;
        return changed;
    }

    /// <summary>Rounds the cooldown down to a multiple of <see cref="CooldownQuantum"/>, except that anything already at or
    /// below zero stays zero - zero is the value <c>CanFlyNow</c> compares against, so it must survive exact.</summary>
    private static int Quantise(int cooldown) => cooldown <= 0 ? 0 : cooldown / CooldownQuantum * CooldownQuantum;

    /// <summary>Reads one of the tracker's private int fields. A missing field yields -1 rather than throwing: a game
    /// update that renames one should degrade to "this number is not carried" instead of taking the whole channel down, and
    /// the census prints the encoded value so the degradation is visible.</summary>
    private static int ReadInt(Pawn_FlightTracker tracker, ref System.Reflection.FieldInfo? cache, string name)
    {
        cache ??= HarmonyLib.AccessTools.Field(typeof(Pawn_FlightTracker), name);
        return cache?.GetValue(tracker) is int value ? value : -1;
    }

    private static string? StateName(Pawn_FlightTracker tracker)
    {
        // `flightState` is a private enum, so it is read as its `ToString()` name and matched against the four names. Reading
        // the declared field type by reflection and switching on it would need the enum type itself, which is private.
        var field = StateField ??= HarmonyLib.AccessTools.Field(typeof(Pawn_FlightTracker), "flightState");
        if (field?.GetValue(tracker) is not { } value) return null;
        var name = value.ToString()?.ToLowerInvariant() ?? "";
        return name is "grounded" or "takingoff" or "flying" or "landing" ? name : null;
    }

    private static string? StateGlyph(Pawn_FlightTracker tracker) => StateName(tracker);

    private static System.Reflection.FieldInfo? StateField;
    private static System.Reflection.FieldInfo? FlyingTicksField;
    private static System.Reflection.FieldInfo? CooldownField;
    private static System.Reflection.FieldInfo? LerpField;

    internal static void Apply(ReplicaFlight state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var tracker = pawn?.flight;
            if (tracker is null) { ApplyFailures++; return; }
            var fields = state.Values.Split(':');
            if (fields.Length != 4) { ApplyFailures++; return; }
            var index = Array.IndexOf(StateNames, fields[0]);
            if (index < 0) { ApplyFailures++; return; }
            // All four fields are private with no setter, so they are written by reflection. Going through `FlightTick`
            // instead is not an option: it advances a state machine by one tick and would need hundreds of calls to reach
            // the state the authority is already in, and each call has side effects (`CheckFlyAnimation` swaps the drawing
            // animation, and the Landing branch can `Kill` the pawn at `:729183`).
            StateField ??= HarmonyLib.AccessTools.Field(typeof(Pawn_FlightTracker), "flightState");
            FlyingTicksField ??= HarmonyLib.AccessTools.Field(typeof(Pawn_FlightTracker), "flyingTicks");
            CooldownField ??= HarmonyLib.AccessTools.Field(typeof(Pawn_FlightTracker), "flightCooldownTicks");
            LerpField ??= HarmonyLib.AccessTools.Field(typeof(Pawn_FlightTracker), "lerpTick");
            if (StateField is null || FlyingTicksField is null || CooldownField is null || LerpField is null)
            {
                ApplyFailures++;
                return;
            }
            // The field is typed as the private enum, so the value has to be produced from the enum type rather than from
            // an int: `FieldInfo.SetValue` boxes and would throw on a mismatched underlying type only at runtime.
            StateField.SetValue(tracker, Enum.ToObject(StateField.FieldType, index));
            if (int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flyingTicks))
                FlyingTicksField.SetValue(tracker, flyingTicks);
            if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown))
                CooldownField.SetValue(tracker, cooldown);
            if (int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lerp))
                LerpField.SetValue(tracker, lerp);
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    /// <summary>Census line: how many pawns this peer holds a tracker for, how many are not on the ground, and the last
    /// encoded state. The "last encoded" part answers a different question from the live count - "the channel decided to say
    /// something" versus "the game's own fields say something" - and a value that is right in the first and absent in the
    /// second is a channel that never fires.</summary>
    internal static string Preview()
    {
        try
        {
            if (PawnsSeen == 0) return "0 个带飞行追踪器的小人 / 发 " + StatesSent;
            var sample = new List<string>();
            foreach (var pair in sent)
            {
                sample.Add(pair.Key + "=" + pair.Value);
                if (sample.Count >= 3) break;
            }
            return $"{PawnsSeen} 个带飞行追踪器 / {PawnsFlying} 个在空中或冷却中"
                + $" / 发 {StatesSent} / 应用 {StatesApplied}"
                + (ApplyFailures > 0 ? $" / 失败 {ApplyFailures}" : "")
                + (sample.Count > 0 ? " / 样本 " + string.Join("，", sample) : "");
        }
        catch (Exception error) { return "读取失败：" + error.GetType().Name; }
    }

    internal static string LastSent()
    {
        if (sent.Count == 0) return "（无）";
        var parts = new List<string>();
        foreach (var pair in sent)
        {
            parts.Add(pair.Key + "=" + pair.Value);
            if (parts.Count >= 4) break;
        }
        return string.Join("，", parts);
    }

    /// <summary>One tracker's live state, read the same way <see cref="Publish"/> reads it - for a probe that has to compare
    /// "what the game says" against "what the channel decided to send".</summary>
    internal static string? Describe(Pawn? pawn)
    {
        var tracker = pawn?.flight;
        if (tracker is null) return null;
        var state = StateGlyph(tracker);
        if (state is null) return null;
        // Raw (unquantised) cooldown on purpose: this is the *game's* reading, printed beside what the channel decided to
        // send, and quantising here would make the two agree by construction.
        return state + ":" + ReadInt(tracker, ref FlyingTicksField, "flyingTicks").ToString(CultureInfo.InvariantCulture)
            + ":" + ReadInt(tracker, ref CooldownField, "flightCooldownTicks").ToString(CultureInfo.InvariantCulture)
            + ":" + ReadInt(tracker, ref LerpField, "lerpTick").ToString(CultureInfo.InvariantCulture);
    }
}
