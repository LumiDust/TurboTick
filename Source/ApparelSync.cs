using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>What a pawn is wearing and holding, rebuilt on a replica that will never equip anything itself.
///
/// <para><b>Why this channel has to exist.</b> Equipment and apparel are not part of the `Thing` enumeration: they live
/// in <c>Pawn_EquipmentTracker.equipment</c> and <c>Pawn_ApparelTracker.wornApparel</c>, two <c>ThingOwner</c>s reached
/// through the pawn. A display replica runs no tick, so nothing ever calls <c>AddEquipment</c> or <c>Wear</c> on it -
/// the pawn keeps whatever the save was loaded with. The visible result is the most obvious kind of wrong: a colonist
/// holding a rifle is drawn empty-handed, a colonist in flak armour is drawn in their shirt.</para>
///
/// <para><b>Why the identity is a def, a stuff and a quality rather than the object.</b> The host's rifle is a real
/// <c>ThingWithComps</c> with its own id, comps and hit points, and it already travels on the ordinary thing channel
/// (it is spawned inside a container). Sending it again as equipment would either duplicate it or fight the container
/// channel for ownership. What the player sees is decided by three values - which def, made of what, at what quality -
/// so those are what travel; the replica builds its own display object through <c>ThingMaker</c>.</para>
///
/// <para><b>Why the display objects are not on the deck.</b> <c>Wear</c> calls <c>TryDrop</c> for every garment the new
/// one conflicts with, and dropping spawns a thing on the map - on a replica that would put an object into the world
/// that the host does not have, and it would then be drawn, selectable and countable. <c>dropReplacedApparel: false</c>
/// makes the same call take the remove path instead, which only touches the tracker.</para>
internal static class ApparelSync
{
    /// <summary>Last state sent per pawn, as an encoded string. An unchanged pawn costs one string compare.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static int SetsSent { get; private set; }
    internal static int SetsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int UnknownDefs { get; private set; }

    /// <summary>Collects every pawn whose equipment or apparel differs from what was last sent.
    ///
    /// <para>Change-only on purpose: a colony has tens of pawns and equipment changes a few times an hour, so sending
    /// the full set every frame would cost far more than the events it describes. The same rule hediffs use - and for
    /// the same reason, an empty set after unequipping has to be sent as a real change rather than "nothing to say".
    /// </para></summary>
    internal static List<ReplicaGear>? Publish()
    {
        List<ReplicaGear>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is null || pawn.Dead) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var encoded = Encode(pawn);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaGear(id, encoded));
                    SetsSent++;
                }
            // A pawn that left the map never needs its entry again, and leaving it would make the dictionary grow with
            // every death and every departure for the life of the process.
            foreach (var id in sent.Keys.ToArray())
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] gear publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>`def#stuff#quality` per entry, equipment first, then apparel, joined by `;`. Ordinal-sorted inside each
    /// group so the string is stable: a set the player did not change must not look changed because the tracker
    /// enumerated it in a different order, or every frame would carry every pawn's wardrobe again.</summary>
    private static string Encode(Pawn pawn)
    {
        var equipment = new List<string>(4);
        if (pawn.equipment?.AllEquipmentListForReading is { } held)
            for (var i = 0; i < held.Count; i++)
                if (Describe(held[i]) is { Length: > 0 } one) equipment.Add(one);
        var apparel = new List<string>(8);
        if (pawn.apparel?.WornApparel is { } worn)
            for (var i = 0; i < worn.Count; i++)
                if (Describe(worn[i]) is { Length: > 0 } one) apparel.Add(one);
        // Ordinal sort inside each group: a set the player did not change must not look changed because the tracker
        // enumerated it in a different order, or every frame would carry every pawn's wardrobe again.
        equipment.Sort(StringComparer.Ordinal);
        apparel.Sort(StringComparer.Ordinal);
        return string.Join(";", equipment) + "|" + string.Join(";", apparel);
    }

    /// <summary>`def#stuff#quality#traits`, where the fourth field is a comma-joined, ordinal-sorted list of the weapon's
    /// bladelink traits (`CompBladelinkWeapon.traits`, scribed as `Scribe_Collections.Look(..., LookMode.Def)`
    /// `:532418`) or empty for everything that is not a bladelink weapon.
    ///
    /// <para><b>Why the fourth field exists.</b> `StatWorker.StatOffsetFromGear` (`:716803-716823`) sums three things: the
    /// def's <c>equippedStatOffsets</c>, then <c>compBladelinkWeapon.TraitsListForReading[i].equippedStatOffsets</c>, then
    /// the <c>stat.parts</c> transforms. The first and third follow from the def, which the first three fields already pin
    /// down - so before this field the replica held a persona weapon with the right def, the right material and the right
    /// quality and **none of its traits**, which is exactly the set of modifiers that makes a persona weapon better than its
    /// base def. The failure is silent: the weapon looks right and reads low on every stat it was named for.</para>
    ///
    /// <para>Sorted ordinally so an unchanged set does not look changed by enumeration order; the traits are a def list, so
    /// the order carries no meaning. Comma-joined inside the field because the outer separators are `;` (entries) and `#`
    /// (fields).</para></summary>
    private static string? Describe(Thing? thing)
    {
        if (thing?.def?.defName is not { Length: > 0 } def) return null;
        var stuff = thing.Stuff?.defName ?? string.Empty;
        var quality = thing.TryGetComp<CompQuality>() is { } comp
            ? ((int)comp.Quality).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        var traits = string.Empty;
        if (thing.TryGetComp<CompBladelinkWeapon>() is { } bladelink)
        {
            var names = new List<string>(bladelink.TraitsListForReading.Count);
            for (var i = 0; i < bladelink.TraitsListForReading.Count; i++)
                if (bladelink.TraitsListForReading[i]?.defName is { Length: > 0 } name) names.Add(name);
            names.Sort(StringComparer.Ordinal);
            traits = string.Join(",", names);
        }
        return def + "#" + stuff + "#" + quality + "#" + traits;
    }

    /// <summary>Rebuilds one pawn's equipment and apparel. Called from the frame loop on the main thread.</summary>
    internal static void Apply(ReplicaGear state)
    {
        try
        {
            var pawn = FindPawnAny(state.Pawn);
            if (pawn is null || pawn.Dead) return;
            var (equipment, apparel) = Parse(state.State);
            ApplyEquipment(pawn, equipment);
            ApplyApparel(pawn, apparel);
            SetsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] gear apply failed (pawn " + state.Pawn + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    private static (List<Entry> Equipment, List<Entry> Apparel) Parse(string encoded)
    {
        var equipment = new List<Entry>(4);
        var apparel = new List<Entry>(8);
        var bar = encoded.IndexOf('|');
        if (bar < 0) return (equipment, apparel);
        Read(encoded[..bar], equipment);
        Read(encoded[(bar + 1)..], apparel);
        return (equipment, apparel);
    }

    /// <summary>One worn or held thing. <paramref name="Traits"/> is the bladelink trait def list, empty for everything
    /// else; it travels because `StatWorker.StatOffsetFromGear` (`:716803-716823`) adds those traits' own
    /// `equippedStatOffsets` on top of the def's, and a persona weapon without them reads low on everything it was named
    /// for.</summary>
    private readonly record struct Entry(ThingDef Def, ThingDef? Stuff, QualityCategory? Quality,
        List<WeaponTraitDef> Traits);

    private static void Read(string section, List<Entry> into)
    {
        if (section.Length == 0) return;
        foreach (var entry in section.Split(';'))
        {
            if (entry.Length == 0) continue;
            var parts = entry.Split('#');
            if (parts.Length < 1 || parts[0].Length == 0) continue;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(parts[0]);
            if (def is null) { UnknownDefs++; continue; }
            var stuff = parts.Length > 1 && parts[1].Length > 0
                ? DefDatabase<ThingDef>.GetNamedSilentFail(parts[1]) : null;
            QualityCategory? quality = parts.Length > 2 && parts[2].Length > 0
                && int.TryParse(parts[2], out var value) && Enum.IsDefined(typeof(QualityCategory), value)
                    ? (QualityCategory)value : null;
            // Fourth field: comma-joined bladelink trait defs. A def this peer lacks is skipped rather than dropping the
            // whole entry - a persona weapon with two of its three traits is far closer than no weapon at all.
            var traits = new List<WeaponTraitDef>(2);
            if (parts.Length > 3 && parts[3].Length > 0)
                foreach (var name in parts[3].Split(','))
                {
                    if (name.Length == 0) continue;
                    if (DefDatabase<WeaponTraitDef>.GetNamedSilentFail(name) is { } trait) traits.Add(trait);
                }
            into.Add(new Entry(def, stuff, quality, traits));
        }
    }

    /// <summary>Replaces the pawn's equipment with the host's, keeping only entries that are already correct.
    ///
    /// <para>The comparison is by def, stuff and quality rather than by object identity: the display object is this
    /// peer's own creation and will never be the host's object, so an identity comparison would rebuild everything on
    /// every frame.</para></summary>
    private static void ApplyEquipment(Pawn pawn, List<Entry> wanted)
    {
        var tracker = pawn.equipment;
        if (tracker is null) return;
        var existing = tracker.AllEquipmentListForReading;
        // Pair each wanted entry with an existing object that already has the right def/stuff/quality. The comparison
        // cannot be by object identity: the display object is this peer's own creation and will never be the host's.
        var keep = MatchUp(existing, wanted, out var missing);
        if (missing.Count == 0 && keep.Count == existing.Count) return;
        var keepSet = new HashSet<Thing>(keep);
        for (var i = existing.Count - 1; i >= 0; i--)
            if (existing[i] is { } gone && !keepSet.Contains(gone)) tracker.Remove(gone);
        foreach (var index in missing)
            if (Make(wanted[index]) is ThingWithComps made) tracker.AddEquipment(made);
    }

    private static void ApplyApparel(Pawn pawn, List<Entry> wanted)
    {
        var tracker = pawn.apparel;
        if (tracker is null) return;
        var existing = tracker.WornApparel;
        var keep = MatchUp(existing, wanted, out var missing);
        if (missing.Count == 0 && keep.Count == existing.Count) return;
        var keepSet = new HashSet<Thing>(keep);
        for (var i = existing.Count - 1; i >= 0; i--)
            if (existing[i] is { } gone && !keepSet.Contains(gone)) tracker.Remove(gone);
        foreach (var index in missing)
            if (Make(wanted[index]) is Apparel made)
                // `dropReplacedApparel: false`: the default spawns the replaced garment through `TryDrop`, which on a
                // replica puts an object into the world that the host does not have.
                tracker.Wear(made, dropReplacedApparel: false);
    }

    /// <summary>Pairs each wanted entry with a distinct existing object, returning what to keep and which wanted
    /// indices still have to be built.</summary>
    private static List<Thing> MatchUp<T>(IReadOnlyList<T>? existing, List<Entry> wanted, out List<int> missing)
        where T : Thing
    {
        var keep = new List<Thing>(wanted.Count);
        missing = [];
        var taken = new bool[existing?.Count ?? 0];
        for (var i = 0; i < wanted.Count; i++)
        {
            var found = -1;
            for (var j = 0; j < (existing?.Count ?? 0); j++)
            {
                if (taken[j]) continue;
                if (!Matches(existing![j], wanted[i])) continue;
                found = j;
                break;
            }
            if (found >= 0) { taken[found] = true; keep.Add(existing![found]); }
            else missing.Add(i);
        }
        return keep;
    }

    /// <summary>Def, material and quality, plus the bladelink trait set.
    ///
    /// <para>The trait comparison is not cosmetic: a kept object is never rebuilt, so a persona weapon that gained or lost a
    /// trait would stay wrong forever if this said "same". It compares the *set* rather than the order, because the payload
    /// sorts ordinally while the comp appends in whatever order it was granted.</para></summary>
    private static bool Matches(Thing thing, Entry wanted)
    {
        if (thing.def != wanted.Def) return false;
        if ((thing.Stuff ?? null) != (wanted.Stuff ?? null)) return false;
        if (wanted.Quality is { } quality
            && !(thing.TryGetComp<CompQuality>() is { } comp && comp.Quality == quality)) return false;
        var live = thing.TryGetComp<CompBladelinkWeapon>()?.TraitsListForReading;
        var liveCount = live?.Count ?? 0;
        if (liveCount != wanted.Traits.Count) return false;
        for (var i = 0; i < wanted.Traits.Count; i++)
        {
            var found = false;
            for (var j = 0; j < liveCount; j++)
                if (live![j] == wanted.Traits[i]) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private static Thing? Make(Entry entry)
    {
        try
        {
            var thing = ThingMaker.MakeThing(entry.Def, entry.Stuff);
            if (thing is null) return null;
            // A negative id keeps the display object out of `UniqueIDsManager`'s space, exactly as the carried-item
            // phantom does, and `overrideGraphicIndex` keeps `Graphic_Random` off a negative modulus.
            thing.thingIDNumber = -1;
            thing.overrideGraphicIndex = 0;
            if (entry.Quality is { } quality && thing.TryGetComp<CompQuality>() is { } comp)
                comp.SetQuality(quality, ArtGenerationContext.Outsider);
            // The bladelink traits are written through the comp's own list, which is the same field `PostExposeData`
            // scribes (`:532418`). There is no public setter for `traits`, but `TraitsListForReading` returns the live list
            // (`:532217`), so mutating it is a direct write and cannot double-grant anything: `GainTrait` on this comp
            // also fires bond messages and a save-time biosignature, none of which a display copy should replay.
            if (entry.Traits.Count > 0 && thing.TryGetComp<CompBladelinkWeapon>() is { } bladelink)
            {
                var live = bladelink.TraitsListForReading;
                live.Clear();
                for (var i = 0; i < entry.Traits.Count; i++) live.Add(entry.Traits[i]);
            }
            return thing;
        }
        catch (Exception error)
        {
            if (ApplyFailures++ <= 4)
                Log.Warning("[TurboTick] gear make failed (" + entry.Def.defName + "): "
                    + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Finds a pawn anywhere, including one that is off every map. A dead pawn's gear is not drawn, and a
    /// pawn in a caravan has no map, so the map walk is not enough on its own.</summary>
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

    /// <summary>Census row. The bladelink count is separate from the send count because "sent 4" says the channel ran; it
    /// says nothing about whether any of the four carried a persona weapon, which is the only case where the fourth payload
    /// field is non-empty and the only case `StatOffsetFromGear`'s middle term contributes.</summary>
    internal static string Summary()
    {
        var bladelink = 0;
        var traitCount = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn is null) continue;
                CountBladelink(pawn.equipment?.AllEquipmentListForReading, ref bladelink, ref traitCount);
                CountBladelink(pawn.apparel?.WornApparel, ref bladelink, ref traitCount);
            }
        return $"装备 发出 {SetsSent} / 应用 {SetsApplied}（失败 {ApplyFailures}，未知Def {UnknownDefs}）"
            + $"；带语刃词条的武器 {bladelink} 件（共 {traitCount} 条词条）";
    }

    private static void CountBladelink<T>(IReadOnlyList<T>? things, ref int weapons, ref int traits) where T : Thing
    {
        if (things is null) return;
        for (var i = 0; i < things.Count; i++)
            if (things[i].TryGetComp<CompBladelinkWeapon>() is { } comp && comp.TraitsListForReading.Count > 0)
            {
                weapons++;
                traits += comp.TraitsListForReading.Count;
            }
    }
}

/// <summary>The display object a replica builds is not a real spawn and must never be destroyed with leavings.
///
/// <para><c>Apparel.Remove</c> and <c>Pawn_EquipmentTracker.Remove</c> only detach from the tracker, so this guard is
/// about the other direction: a vanilla call that tries to drop or destroy a display object on a replica would either
/// spawn an object the host does not have or log an error every time a pawn changed clothes.</para>
///
/// <para><b>Why the target is resolved by hand.</b> <c>TryDrop</c> has three overloads
/// (<c>(Apparel)</c>, <c>(Apparel, out Apparel)</c>, <c>(Apparel, out Apparel, IntVec3, bool)</c>) and patching it by
/// name alone throws <c>AmbiguousMatchException</c> at install time - which the "every gameplay patch must install"
/// check escalates into a fatal startup error. Measured on 192.168.0.29: the server would not start at all. Attribute
/// arguments have to be compile-time constants, so the by-ref type cannot be spelled there; <c>TargetMethod</c> runs at
/// patch time and can. The four-argument overload is the one the other two funnel into, so it covers all of them.</para>
/// </summary>
[HarmonyPatch]
internal static class ReplicaApparelDropGuard
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Pawn_ApparelTracker), "TryDrop",
            [typeof(Apparel), typeof(Apparel).MakeByRefType(), typeof(IntVec3), typeof(bool)]);

    private static bool Prefix(Apparel ap)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        // A display garment has no id (see `ApparelSync.Make`), so it is not part of the world and cannot be dropped.
        return ap?.thingIDNumber >= 0;
    }
}
