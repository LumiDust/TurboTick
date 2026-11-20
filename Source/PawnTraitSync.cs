using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Every pawn's trait list, for a replica that never re-derives it.
///
/// <para><b>The gap this closes.</b> Traits reach a replica exactly once, from the save file, through
/// `Pawn.ExposeData`'s `Scribe_Deep.Look(ref story, "story", this)` (`:131390`). After that nothing on the replica changes
/// them, so every trait the authority grants, removes or re-degrees afterwards is invisible. Two things read that list:</para>
/// <list type="bullet">
/// <item>`Pawn_StoryTracker.DisabledWorkTags` (`:460027-460031`) unions `trait.def.disabledWorkTags` over the
/// non-suppressed traits. That set decides which work types the pawn may ever be assigned to, so a stale list makes the
/// work tab offer jobs the pawn cannot do and hide jobs it can.</item>
/// <item>`StatWorker.GetValueUnfinalized` (`:715774-715781`) adds `OffsetOfStat` for every non-suppressed trait, and that
/// feeds work speed, market value, beauty, insulation and more.</item>
/// </list>
///
/// <para><b>Why the def and degree, not the trait's own scribe form.</b> `Trait.ExposeData` (`:483418-483428`) scribes
/// `def`, `degree`, `scenForced`, `suppressedByTrait` and the *references* `sourceGene`/`suppressedByGene`. Then
/// `TraitSet.ExposeData`'s PostLoadInit pass (`:483790-483803`) removes every trait whose `sourceGene.def.forcedTraits`
/// no longer lists it - and a reference that resolved to null on the receiver would take a gene-granted trait with it.
/// `def` and `degree` are what the two readers above actually consume, so those are the payload;
/// `suppressedByTrait` travels as a flag because `Trait.Suppressed` (`:483388-483402`) reads it directly.</para>
///
/// <para><b>Why the whole list and not a delta.</b> Same reason as the area and zone channels: a delta needs the replica
/// to share the authority's baseline, and a replica that joined late has none. A pawn has a handful of traits and they
/// change on a scale of days, so the whole list is the cheaper correct thing.</para>
/// </summary>
internal static class PawnTraitSync
{
    /// <summary>Last list sent per pawn. Cleared by `Reset`, not set to a "primed" flag - a baseline that is recorded
    /// before anything has ever been sent means the second frame compares an empty world against itself and never
    /// publishes. That exact mistake cost a round on `AreaSync`/`PlaySettingsSync`.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static long TraitsSent { get; private set; }
    internal static long TraitsApplied { get; private set; }
    internal static long TraitsRemoved { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>The last record published, so the encoded shape - not just the count - can be read from the census.</summary>
    internal static ReplicaPawnTraits? LastSample { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        TraitsSent = 0;
        TraitsApplied = 0;
        TraitsRemoved = 0;
        ApplyFailures = 0;
        LastSample = null;
    }

    /// <summary>Encodes `defName#degree#s` entries, semicolon separated. Order follows `allTraits` because
    /// `TraitSet.ExposeData` preserves it and duplicate defs at different degrees are legal (a trait can appear twice
    /// when a gene grants a second degree), so the list must not be sorted into a set.</summary>
    private static string Encode(TraitSet traits)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < traits.allTraits.Count; i++)
        {
            var trait = traits.allTraits[i];
            if (trait?.def is null) continue;
            if (builder.Length > 0) builder.Append(';');
            builder.Append(trait.def.defName)
                .Append('#').Append(trait.Degree.ToString(CultureInfo.InvariantCulture))
                .Append('#').Append(trait.suppressedByTrait ? '1' : '0');
        }
        return builder.ToString();
    }

    internal static List<ReplicaPawnTraits>? Publish()
    {
        List<ReplicaPawnTraits>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.story?.traits is not { } traits) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var encoded = Encode(traits);
                    // No baseline means "never sent", which is not the same as "unchanged": compare only when a previous
                    // value exists, so the first frame always publishes.
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    LastSample = new ReplicaPawnTraits(id, encoded);
                    (changed ??= []).Add(LastSample);
                    TraitsSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] trait publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaPawnTraits state)
    {
        try
        {
            var pawn = FindPawnAny(state.Pawn);
            if (pawn?.story?.traits is not { } traits) return;
            var wanted = Parse(state.Traits);
            // Rebuild in place rather than clearing: `TraitSet.allTraits` is public and `Trait` has a public
            // `(TraitDef, int, bool)` constructor (`:483411-483416`), while assigning the list wholesale would lose the
            // `pawn` back-reference that `TraitSet.ExposeData` sets in LoadingVars.
            var matches = wanted.Count == traits.allTraits.Count;
            if (matches)
                for (var i = 0; i < wanted.Count; i++)
                {
                    var have = traits.allTraits[i];
                    if (have?.def is null || have.def != wanted[i].Def || have.Degree != wanted[i].Degree
                        || have.suppressedByTrait != wanted[i].Suppressed)
                    {
                        matches = false;
                        break;
                    }
                }
            if (matches)
            {
                TraitsApplied++;
                return;
            }
            // Reconcile through `RemoveTrait`/`GainTrait` rather than clearing and refilling. `GainTrait`
            // (`:483949-484014`) is not just a list append: it grants the trait's abilities through
            // `pawn.abilities.GainAbility`, marks situational thoughts dirty, adds or removes needs, clears
            // `MeditationFocusTypeAvailabilityCache`, refreshes `attackTargetsCache` for a hostility-disabling trait, and
            // calls `RecacheTraits` - which is `private` (`:484239`). Repeating that list by hand would be a second
            // implementation to keep in step; `RemoveTrait` (`:484016+`) is its mirror.
            var removed = 0;
            for (var i = traits.allTraits.Count - 1; i >= 0; i--)
            {
                var have = traits.allTraits[i];
                if (have?.def is null) { traits.allTraits.RemoveAt(i); continue; }
                var keep = false;
                for (var j = 0; j < wanted.Count; j++)
                    if (wanted[j].Def == have.def && wanted[j].Degree == have.Degree) { keep = true; break; }
                if (keep) continue;
                traits.RemoveTrait(have);
                removed++;
            }
            for (var i = 0; i < wanted.Count; i++)
            {
                var want = wanted[i];
                Trait? have = null;
                for (var j = 0; j < traits.allTraits.Count; j++)
                    if (traits.allTraits[j]?.def == want.Def && traits.allTraits[j].Degree == want.Degree)
                    {
                        have = traits.allTraits[j];
                        break;
                    }
                if (have is null)
                {
                    traits.GainTrait(new Trait(want.Def, want.Degree), suppressConflicts: true);
                    have = FindByDegree(traits, want.Def, want.Degree);
                }
                // `Suppressed` is derived (`:483388-483402`) from `suppressedByTrait`, `suppressedByGene` and the
                // source gene's override, so only the flag the trait itself owns is written here. `suppressConflicts`
                // above may have set it as a side effect of the conflict rules; the authority's answer wins.
                if (have is not null && have.suppressedByTrait != want.Suppressed) have.suppressedByTrait = want.Suppressed;
            }
            pawn.Notify_DisabledWorkTypesChanged();
            TraitsApplied++;
            if (removed > 0) TraitsRemoved += removed;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] trait apply failed (pawn " + state.Pawn + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    private readonly struct Wanted
    {
        internal readonly TraitDef Def;
        internal readonly int Degree;
        internal readonly bool Suppressed;
        internal Wanted(TraitDef def, int degree, bool suppressed)
        {
            Def = def;
            Degree = degree;
            Suppressed = suppressed;
        }
    }

    private static List<Wanted> Parse(string encoded)
    {
        var result = new List<Wanted>();
        if (string.IsNullOrEmpty(encoded)) return result;
        foreach (var entry in encoded.Split(';'))
        {
            if (entry.Length == 0) continue;
            var parts = entry.Split('#');
            var def = DefDatabase<TraitDef>.GetNamedSilentFail(parts[0]);
            // A def this peer does not have is skipped, not fatal: a trait added by a mod the other peer lacks would
            // otherwise make every subsequent apply fail and freeze the whole list.
            if (def is null) continue;
            var degree = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var d) ? d : 0;
            var suppressed = parts.Length > 2 && parts[2] == "1";
            result.Add(new Wanted(def, degree, suppressed));
        }
        return result;
    }

    private static Trait? FindByDegree(TraitSet traits, TraitDef def, int degree)
    {
        for (var i = 0; i < traits.allTraits.Count; i++)
            if (traits.allTraits[i]?.def == def && traits.allTraits[i].Degree == degree) return traits.allTraits[i];
        return null;
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

    /// <summary>Idempotent round-trip for one pawn's trait list, because a count cannot tell a correct apply from a
    /// wrong one.
    ///
    /// <para><b>What this can and cannot prove.</b> Feeding a pawn its own encoded list back through `Apply` and
    /// re-encoding must reproduce the same string. That catches a decode that drops a degree, a rebuild that reorders the
    /// list, a `suppressedByTrait` flag written from the wrong place, and a reconcile loop that removes and re-adds in a
    /// different order. It cannot prove the *authority* side: a trait the authority grants still has to travel to get
    /// here. The two halves are reported separately so neither borrows the other's credibility.</para>
    ///
    /// <para>It also reports whether `GainTrait` granted an ability in the round trip that the encode does not name: that
    /// would mean the reconcile is adding something the sender never asked for.</para></summary>
    internal static string Probe(string? argument)
    {
        Pawn? subject = null;
        foreach (var map in Find.Maps)
        {
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.story?.traits is not { } t || t.allTraits.Count == 0) continue;
                if (argument is { Length: > 0 } && !pawn.thingIDNumber.ToString(CultureInfo.InvariantCulture).Equals(argument))
                    continue;
                subject = pawn;
                break;
            }
            if (subject is not null) break;
        }
        if (subject?.story?.traits is not { } traits)
            return "本图没有带特质的小人，特质往返测不到（通道已建，载体待实测）";
        var before = Encode(traits);
        var countBefore = traits.allTraits.Count;
        var abilitiesBefore = subject.abilities?.AllAbilitiesForReading?.Count ?? -1;
        Apply(new ReplicaPawnTraits(subject.thingIDNumber, before));
        var after = Encode(subject.story.traits);
        var countAfter = subject.story.traits.allTraits.Count;
        var abilitiesAfter = subject.abilities?.AllAbilitiesForReading?.Count ?? -1;
        return "特质往返：" + (before == after ? "一致" : "不一致")
            + $"（{before} → {after}）；条目 {countBefore} → {countAfter}；能力 {abilitiesBefore} → {abilitiesAfter}"
            + $"；样本 pawn{subject.thingIDNumber} {before}";
    }

    /// <summary>Census row. Counts the pawns that actually hold traits, because "sent 0" on a colony of animals is the
    /// correct reading and looks identical to a channel that never encodes.</summary>
    internal static string Summary()
    {
        var withTraits = 0;
        var total = 0;
        var suppressed = 0;
        var workTags = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.story?.traits is not { } traits || traits.allTraits.Count == 0) continue;
                withTraits++;
                total += traits.allTraits.Count;
                foreach (var trait in traits.allTraits)
                    if (trait is { suppressedByTrait: true }) suppressed++;
                // `Pawn_StoryTracker.DisabledWorkTagsBackstoryTraitsAndGenes` (`:127936`) is the trait-and-gene part;
                // `Pawn.CombinedDisabledWorkTags` (`:127932`) adds the role's tags on top. Counting the story part is
                // what says whether traits alone are blocking anything.
                if (pawn.story.DisabledWorkTagsBackstoryTraitsAndGenes != WorkTags.None) workTags++;
            }
        return $"{withTraits} 个小人有特质（共 {total} 条，其中 {suppressed} 条被 trait 压制），"
            + $"{workTags} 个因此禁掉某类工作 / 发 {TraitsSent} / 应用 {TraitsApplied}（删 {TraitsRemoved} 条，失败 {ApplyFailures}）"
            + $" / 样本 {LastSample?.Traits switch { null => "（无）", "" => "（空特质表）", var t => "pawn" + LastSample!.Pawn + " " + t }}";
    }
}
