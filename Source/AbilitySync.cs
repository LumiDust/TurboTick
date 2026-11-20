using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Charges and cooldown for every ability a pawn has, for a replica whose powers never recharge.
///
/// <para><b>Why a whole channel instead of a field.</b> <c>AuthoritativeFrame.PawnState</c> exists and would carry this:
/// <c>Pawn_AbilityTracker.ExposeData</c> scribes <c>abilities</c> with <c>LookMode.Deep</c> (<c>:468414</c>) and
/// <c>Ability.ExposeData</c> writes <c>charges</c>, <c>inCooldown</c>, <c>cooldownEndTick</c>, <c>cooldownDuration</c> and
/// <c>lastCastTick</c> (<c>:433725-433733</c>). That parameter is passed as <c>null</c>
/// (<see cref="HeadlessRuntime"/> line ~1752), so nothing of the sort arrives. Rather than send a pawn's entire Scribe
/// fragment - needs, health, gear, relations, work priorities - just to move six integers, this carries the six.</para>
///
/// <para><b>What a replica shows without it.</b> <c>GizmoDisabled</c> refuses on <c>OnCooldown</c> and on
/// <c>UsesCharges &amp;&amp; charges &lt;= 0</c> (<c>:433138</c>, <c>:433143</c>), and <c>GizmoExtraLabel</c> prints
/// <c>$"{charges} / {maxCharges}"</c> (<c>:432544</c>). A psycaster who spent their last charge therefore still reads
/// full, and one whose power is recharging still reads ready - the icon is what the player looks at before deciding
/// whether to fire it.</para>
///
/// <para><b>Why the cooldown is sent as a remaining count, not a tick stamp.</b> <c>CooldownTicksRemaining</c> is
/// <c>cooldownEndTick - GenTicks.TicksGame</c> (<c>:432514</c>), which is a derived value: sending the stamp would make
/// the replica's own clock part of the answer, and the two clocks are deliberately offset. Sending the remaining count
/// and re-deriving the stamp on arrival keeps the authority's clock as the only one that matters - the same reason
/// `PawnTrackerSync` converts a training-decay stamp to a remaining count.</para>
public static class AbilitySync
{
    /// <summary>Quantisation for a cooldown, in ticks. Ten in-game seconds: the cooldown label is printed as a period
    /// ("1.2 hours"), and the shortest cooldown in the game is longer than this, so nothing readable moves.</summary>
    private const int CooldownQuantum = 250;

    /// <summary>The last string sent per pawn, so an idle colony costs nothing. Keyed by `thingIDNumber` because that is
    /// what the replica indexes pawns by; a `Pawn` itself would be the wrong key across a save reload.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>How much was sent, for the census. `StatesSent` counts payloads (a pawn's whole set) while
    /// `AbilitiesSent` counts the individual abilities inside them, because "1 payload" reads the same whether that pawn
    /// had one ability or nine.</summary>
    internal static long StatesSent { get; private set; }
    internal static long AbilitiesSent { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>What the last payload looked like, for the census. A count cannot show whether the format is what the
    /// receiving side parses; an encoded string can.</summary>
    internal static string? LastSample { get; private set; }

    /// <summary>Pawns whose ability set changed, or null when nothing did. Returns null rather than an empty list so the
    /// frame stays small on the many ticks where no ability has been used.</summary>
    internal static List<ReplicaAbilityCharges>? Publish()
    {
        List<ReplicaAbilityCharges>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Verse.Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.abilities is null) continue;
                    var id = pawn.thingIDNumber;
                    var encoded = Encode(pawn);
                    if (encoded is null) { sent.Remove(id); continue; }
                    live.Add(id);
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    LastSample = encoded;
                    AbilitiesSent += CountEntries(encoded);
                    StatesSent++;
                    (changed ??= []).Add(new ReplicaAbilityCharges(id, encoded));
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    /// <summary>Encodes every ability that has something worth sending, or null when none does.
    ///
    /// <para>Entries are <c>defName#charges#cooldownRemaining#cooldownTotal#lastCast</c> joined by <c>;</c>. All five are
    /// sent rather than only a charge count because they answer different questions: `charges` gates whether the gizmo is
    /// pressable at all, `cooldownRemaining` is what the countdown text shows, `cooldownTotal` is the denominator that text
    /// divides by, and `lastCast` is what several comps measure "recently used" against.</para>
    ///
    /// <para>An ability with no charges and no cooldown is skipped: every pawn with a psylink has several passive
    /// abilities, and sending "0 of 0, never cast" for each of them every time anything changes would triple the payload
    /// to say nothing.</para></summary>
    /// <summary>The encoded payload for the probe. Exposed because the value is invisible on the replica: a gizmo that
    /// reads "3 / 3" when the host has 1 looks exactly like one that reads "1 / 3".</summary>
    internal static string? EncodeFor(Pawn pawn) => Encode(pawn);

    private static string? Encode(Pawn pawn)
    {
        try
        {
            var abilities = pawn.abilities?.AllAbilitiesForReading;
            if (abilities is null || abilities.Count == 0) return null;
            var parts = new List<string>(abilities.Count);
            foreach (var ability in abilities)
            {
                if (ability?.def?.defName is not { Length: > 0 } name) continue;
                var charges = ability.RemainingCharges;
                var total = ability.CooldownTicksTotal;
                var remaining = ability.CooldownTicksRemaining;
                var lastCast = LastCastTick(ability);
                // Nothing happened to this one and nothing can happen to it: `UsesCharges` is false and the cooldown is
                // spent. Sending it would only repeat the def's own numbers back.
                if (!ability.UsesCharges && remaining <= 0 && total <= 0) continue;
                parts.Add(string.Join("#", name,
                    charges.ToString(CultureInfo.InvariantCulture),
                    Quantise(remaining).ToString(CultureInfo.InvariantCulture),
                    Quantise(total).ToString(CultureInfo.InvariantCulture),
                    lastCast.ToString(CultureInfo.InvariantCulture)));
            }
            if (parts.Count == 0) return null;
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Rounds a tick count down to <see cref="CooldownQuantum"/>. The encoding doubles as the change key, so a
    /// value that moves every tick would resend every ability of every pawn every frame - measured on the plant sweep as
    /// 扫 3600 建 3600, the same defect.</summary>
    private static int Quantise(int ticks) => ticks <= 0 ? 0 : ticks / CooldownQuantum * CooldownQuantum;

    /// <summary>`lastCastTick` is public on `Ability` (`:432480`), but read through the field so a future rename is a
    /// null-guarded miss rather than a compile error on a field this channel does not strictly need.</summary>
    private static int LastCastTick(Ability ability)
    {
        try
        {
            var info = HarmonyLib.AccessTools.Field(typeof(Ability), "lastCastTick");
            return info?.GetValue(ability) is int tick ? tick : -99999;
        }
        catch (Exception) { return -99999; }
    }

    private static int CountEntries(string encoded)
    {
        var count = 1;
        for (var i = 0; i < encoded.Length; i++)
            if (encoded[i] == ';') count++;
        return count;
    }

    /// <summary>Writes the values back onto the replica's own `Ability` instances.</summary>
    internal static void Apply(ReplicaAbilityCharges state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var abilities = pawn?.abilities?.AllAbilitiesForReading;
            if (abilities is null) return;
            var entries = state.Charges.Split(';');
            var applied = 0;
            foreach (var entry in entries)
            {
                var fields = entry.Split('#');
                if (fields.Length < 5) continue;
                var ability = FindAbility(abilities, fields[0]);
                if (ability is null) continue;
                if (int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var charges))
                    ability.RemainingCharges = charges;
                // The stamp, not the count: everything that reads a cooldown asks `CooldownTicksRemaining`, which
                // subtracts the *local* clock. Writing a remaining count into the stamp would make the cooldown depend on
                // how far apart the two clocks are.
                if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
                {
                    SetField(ability, "cooldownEndTick", GenTicks.TicksGame + remaining);
                    SetField(ability, "inCooldown", remaining > 0);
                }
                if (int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                    SetField(ability, "cooldownDuration", total);
                if (int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastCast))
                    SetField(ability, "lastCastTick", lastCast);
                applied++;
            }
            ApplyCount++;
            if (applied == 0 && entries.Length > 0) ApplyFailures++;
        }
        catch (Exception)
        {
            if (ApplyFailures++ < 8)
                Log.Warning("[TurboTick] 恢复能力次数失败 id=" + state.Pawn);
        }
    }

    private static Ability? FindAbility(List<Ability> abilities, string defName)
    {
        for (var i = 0; i < abilities.Count; i++)
            if (abilities[i]?.def?.defName == defName) return abilities[i];
        return null;
    }

    /// <summary>The three cooldown fields are private and have no setters; `inCooldown`/`cooldownEndTick`/`cooldownDuration`
    /// are only ever written by `Ability.StartCooldown`/`Notify_Cast` (`:432923`), which a replica never calls.</summary>
    private static void SetField(Ability ability, string name, object value)
    {
        try { HarmonyLib.AccessTools.Field(typeof(Ability), name)?.SetValue(ability, value); }
        catch (Exception) { }
    }

    /// <summary>How many pawns currently have an encoded set, for the census. Zero means nothing is being sent at all.</summary>
    internal static int TrackedCount => sent.Count;

    /// <summary>Clears the remembered payloads so a save reload does not compare against a world that no longer exists.</summary>
    internal static void Reset()
    {
        sent.Clear();
        LastSample = null;
    }
}
