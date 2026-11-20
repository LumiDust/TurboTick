using System;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Classifies a live <c>Thing</c> into the vocabulary the rule table speaks, and answers whether a player may
/// act on it.
///
/// <para><b>Why this is separate from the table.</b> The table is policy - 24 rows that can be checked exhaustively
/// without a game running (see <see cref="SettlementRuleTable"/>). This half is a mapping from engine types to that
/// vocabulary, which needs the game assembly and can only be spot-checked. Keeping them apart is what lets the policy be
/// verified and the mapping be reviewed.</para>
internal static class SettlementRule
{
    /// <summary>Classifies a thing into the kind the table speaks in.
    ///
    /// <para>Order is the whole content of this method. A nutrient paste dispenser is a <c>Building</c>, a
    /// <c>Building_WorkTable</c> and an <c>IBillGiver</c> at once, and which one a rule means depends on what is being
    /// done - so the specific checks come before the general ones, and a bench is a bench before it is a building.
    /// </para>
    ///
    /// <para><b>Blueprint and Frame come first.</b> A blueprint's <c>def</c> is a <i>blueprint</i> def, and reading
    /// anything else off it first is how a display replica ended up dereferencing a null
    /// <c>entityDefToBuild</c> (`[Ref 3CAD3752]`). Testing the type before touching the def keeps that class of mistake
    /// out of this file too.</para></summary>
    /// <summary>Which kind a bare cell is, for the acts that care about what stands on it.
    ///
    /// <para><b>Why a cell needs a kind at all (round 139).</b> The reservation gate's cell branch asked `Place` for
    /// every cell, and `Place` is stated on `Land` - the act of painting a zone or laying a blueprint. But the reason a
    /// pawn reserves a *cell* in practice is hauling into a stockpile, and that is `Store`, which the table states on
    /// `Storage`. So the wrong grant governed it: a visitor holding `BuildArea` could haul into somebody's stockpile,
    /// and one holding `Storage` could not - measured with `script-zone` and `settlement-chokepoint`. The zone on the
    /// cell is what makes it storage rather than ground.</para>
    /// </summary>
    internal static SettlementKind KindOfCell(Map? map, IntVec3 cell)
    {
        if (map is null) return SettlementKind.None;
        // `ZoneAt` walks the zone grid; a null zone manager means no zones exist, which is land.
        return map.zoneManager?.ZoneAt(cell) is Zone_Stockpile ? SettlementKind.Storage : SettlementKind.Land;
    }

    internal static SettlementKind KindOf(Thing? thing, SettlementAct act = SettlementAct.None)
    {
        if (thing is null) return SettlementKind.None;
        if (thing is Blueprint) return SettlementKind.Blueprint;
        if (thing is Frame) return SettlementKind.Frame;
        if (thing is Pawn pawn)
        {
            // An animal that belongs to somebody is handled; one that belongs to nobody is hunted. The two are
            // different grants (`AnimalHandling` vs `Hunting`) because taming somebody's muffalo and shooting a wild
            // one are not the same permission.
            if (pawn.RaceProps?.Animal == true)
                return pawn.Faction is null ? SettlementKind.WildAnimal : SettlementKind.Animal;
            return SettlementKind.Pawn;
        }
        if (thing is Plant) return SettlementKind.Plant;
        if (thing is Filth) return SettlementKind.Filth;
        if (thing is Fire) return SettlementKind.Fire;
        if (thing is Apparel) return SettlementKind.Equipment;
        if (thing is ThingWithComps weapon && weapon.def?.IsWeapon == true) return SettlementKind.Equipment;
        // `ThingDef` has no `IsFood`; an item is food when it carries ingestible properties. Testing the property
        // rather than the def's category is what makes a modded meal count without being listed anywhere.
        if (thing.def?.ingestible is not null) return SettlementKind.Food;
        if (thing is Building_Door) return SettlementKind.Door;
        if (thing is Building_Bed) return SettlementKind.Bed;
        // **Enterable care structures (round 87).** `SettlementKind.Pod` had four table rows - `Care`/`Use`,
        // `Construction`/`TakeApart`, `Construction`/`Uninstall`, `Repairs`/`Repair` - and **no classifier arm ever
        // produced it**, so those four rows were unreachable. A cryptosleep casket, a biosculpter pod and a gene
        // extractor all fell through to `SettlementKind.Building`, and `Use`/`Building` is not a row: **`Care` could not
        // let a visitor into a pod, no matter what the owner granted.** The take-apart and repair rows happened to keep
        // working because they are stated on `Building` too, which is exactly why the gap was invisible.
        //
        // Classified by what the player can do with it (climb in, be treated) rather than by base class alone: the
        // biosculpter is a plain `Building` with a comp, and the gene extractor is not a casket.
        if (thing is Building_Casket || thing is Building_GeneExtractor
            || (thing is ThingWithComps pod && pod.TryGetComp<CompBiosculpterPod>() is not null))
            return SettlementKind.Pod;
        if (thing is Building building)
        {
            // Order inside `Building` matters and mirrors the engine's own specialisations rather than def flags: a
            // workbench is also a building and often also a container, and a rule that treated a bench as "a building"
            // would let a visitor take it apart with `Construction` when the design says that needs `Benches`+`Gathering`.
            if (thing is Building_MechCharger || thing is IBillGiver) return SettlementKind.Bench;
            if (thing is Building_Storage) return SettlementKind.Storage;
            if (building.def?.building?.isSittable == true) return SettlementKind.Seat;
            if (building.def?.mineable == true) return SettlementKind.Rock;
            return SettlementKind.Building;
        }
        if (thing.def is { } def)
        {
            if (def.IsWorkTable) return SettlementKind.Bench;
            if (def.building?.isSittable == true) return SettlementKind.Seat;
            if (def.building is { isResourceRock: true } or { isNaturalRock: true }) return SettlementKind.Rock;
            if (def.building is not null) return SettlementKind.Building;
            // Haulable and none of the above: a loose stack. This is the fallback because it is what "supply" means -
            // something lying on the ground that somebody could carry away.
            if (def.EverHaulable) return SettlementKind.Supply;
        }
        return SettlementKind.None;
    }

    // **Two decision methods used to live here and were deleted in round 83.** `Can(data, playerId, act, thing)` (both
    // overloads) and `CanCell(data, playerId, act, map, cell)` had no caller: the live gates are `SettlementGates.Can`,
    // which takes a *pawn* rather than a player, because on the authority there is no local player and a pawn's
    // membership is what decides. Rather than being harmless, the dead pair had drifted into **disagreeing with the live
    // implementation in two ways that both reintroduce fixed bugs**:
    //
    //   1. `kind == SettlementKind.None` returned **false** - a refusal. The live gate returns true, with a comment
    //      explaining why: the layer has nothing to say about a mote or a projectile, and refusing those stops combat
    //      animation. Anyone reaching for the "cleaner, game-free" version here would have shipped that.
    //   2. `home == 0` only returned true when `thing?.Map is null`. The live gate permits unclaimed ground
    //      unconditionally - it must, or nobody could build anywhere outside a settlement.
    //
    // A second copy of a decision, in a place that looks more testable, is worse than no copy: it is the version a
    // future reader trusts. The parts that *are* shared and game-free stay: `KindOf`, `HomeOf`, `Describe` and the whole
    // of `SettlementRuleTable`, which is what the unit tests exercise.

    /// <summary>Which settlement a thing stands in, or 0 when it stands in nobody's.
    ///
    /// <para>An item has no owner record in this design - ownership is by land, which is why a chest of steel in
    /// somebody's base is theirs and the same steel dropped outside is nobody's. That is the model ColonyTogether uses
    /// too (<c>KeptCells</c> keyed by cell, ct045.decompiled.cs:369290), and it is what makes the layer cheap: no
    /// per-object bookkeeping, and moving a thing across a border is one position test rather than an ownership
    /// transfer.</para>
    ///
    /// <para><c>MapHeld</c>, not <c>Map</c>: a thing in a caravan or in transit is on no map but is still held
    /// somewhere, and reading `Map` would answer 0 for it - the "nobody's business" answer for something that is
    /// somebody's. The pawn cell gate takes the same shortcut for the same reason (round 82).</para></summary>
    /// <summary>Colonists that belong to no settlement on record, by the faction their settlement is on.
    ///
    /// <para><b>What this counts.</b> `SettlementData.PawnHome` is written only when a settlement is founded, so every
    /// colonist born, recruited, bought or arrived afterwards has no entry. Those pawns are treated as visitors **on
    /// their own land** - refused building, food and storage by the very gates that exist to keep strangers out - and
    /// nothing in the game says so. Measured on the two-settlement save: `settlement-report` printed 「小人归属 无」 for a
    /// map with four colonists.</para>
    ///
    /// <para><b>Why a count and not an automatic fix.</b> Two automatic answers were written and reverted (round 93): both
    /// "the land it stands on" and "the settlement's faction" cannot separate an owner's colonist from a neighbour's
    /// visitor on a shared colony, because all of them are one faction. Anything that does separate them is a guess about
    /// which human each pawn belongs to, and a wrong guess grants `All` to a stranger. A number the report prints is the
    /// honest intermediate: it says exactly how many pawns are in this state, and `settlement-claim` closes it with a
    /// decision somebody made.</para></summary>
    internal static int UnrecordedColonists(SettlementData? data, Map? map)
    {
        if (data is null || !data.Active || map is null) return 0;
        return CountUnrecorded(data, map);
    }

    /// <summary>Every map, because a colony can span several and this number is what tells a player to run
    /// `settlement-claim`.
    ///
    /// <para><b>The gap the single-map overload left.</b> Measured on this save with two sites: `settlement-report`
    /// printed `未登记小人 0 个` while `Find.CurrentMap` was map 0 and both settlements were on map 0 - correct there, but
    /// the same call on a two-map colony counts only the map the player happens to be looking at. A colonist left
    /// unrecorded in the <em>other</em> settlement is refused building, food and storage by the gates and nothing in the
    /// report says so. The gates themselves are map-correct (`HomeOf` is `thing.MapHeld`, `SettlementAt` takes a mapId);
    /// only the count was pinned.</para>
    ///
    /// <para>`CountGhostMembers` next door already walks `Find.Maps` for the same reason, so this makes the two halves of
    /// the membership picture agree about scope.</para></summary>
    internal static int UnrecordedColonists(SettlementData? data)
    {
        if (data is null || !data.Active) return 0;
        var maps = Find.Maps;
        if (maps is null) return 0;
        var count = 0;
        foreach (var map in maps)
        {
            if (map?.mapPawns is null) continue;
            count += CountUnrecorded(data, map);
        }
        return count;
    }

    private static int CountUnrecorded(SettlementData data, Map map)
    {
        var count = 0;
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is null || pawn.RaceProps?.Humanlike != true) continue;
            if (!pawn.IsColonist && !pawn.IsSlave && !pawn.IsPrisoner) continue;
            if (data.HomeOfPawn(pawn.thingIDNumber) != 0) continue;
            // Only counted when it is standing on somebody's land: a pawn outside every settlement is governed by nothing
            // and its missing entry costs nothing.
            if (data.SettlementAt(map.uniqueID, pawn.PositionHeld) == 0) continue;
            count++;
        }
        return count;
    }

    internal static int HomeOf(SettlementData? data, Thing? thing)
    {
        if (data is null || !data.Active || thing is null) return 0;
        var map = thing.MapHeld;
        if (map is null) return 0;
        return data.SettlementAt(map.uniqueID, thing.PositionHeld);
    }

    /// <summary>A line for the status log: how many settlements, and whether the layer is on at all. Without this the
    /// difference between "no ownership" and "ownership that allows everything" is invisible in a log.</summary>
    internal static string Describe(SettlementData? data)
    {
        if (data is null) return "据点 未载入";
        if (!data.Active) return "据点 0 个（所有权层关闭）";
        var live = 0;
        for (var i = 0; i < data.Sites.Count; i++)
            if (data.Sites[i] is { Retired: false }) live++;
        return $"据点 {live} 个（成员 {data.Members.Count}，授权 {data.Grants.Count}）";
    }
}
