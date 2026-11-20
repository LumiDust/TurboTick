using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Digests of the shared structures that the "find something reachable" searches walk.
///
/// Four of the eight measured divergences have the same shape: a giver found a target on one peer and reported
/// nothing on the other, with hunger, position and think tick identical. Work-giver order is already fixed and
/// provably equal on both peers, so what remains is the search itself - and both paths go through
/// <c>GenClosest.ClosestThingReachable</c> over a list kept by a lister:
/// <code>
/// WorkGiver_Haul.ShouldSkip     : map.listerHaulables.ThingsPotentiallyNeedingHauling().Count == 0   // Verse.AI
/// WorkGiver_Haul.PotentialWork  : map.listerHaulables.ThingsPotentiallyNeedingHauling()
/// WorkGiver_Haul.JobOnThing     : HaulAIUtility.HaulToStorageJob -> StoreUtility.TryFindBestBetterStorageFor
/// JobGiver_GetFood             : FoodUtility.TryFindBestFoodSourceFor -> BestFoodSourceOnMap
/// </code>
/// A one-item difference in such a list is enough to make one peer skip the work type entirely, so comparing a
/// digest of the list is the cheapest way to tell "the list differs" from "the list is equal and the search is".
///
/// This is the "shared structure snapshot" approach: rather than one more probe on one more pawn, hash the inputs
/// that several failing paths have in common.
/// </summary>
internal static class SharedStructureProbe
{
    private static string last = "?";

    internal static void Reset() => last = "?";

    internal static string Describe()
    {
        try
        {
            var text = new StringBuilder();
            var maps = Find.Maps;
            for (var i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                if (map is null || map.Disposed) continue;
                // Haulables: a count difference makes WorkGiver_Haul skip entirely on one side.
                ICollection<Thing> haulables;
                try { haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling(); }
                catch (Exception) { haulables = Array.Empty<Thing>(); }
                // The decisive inputs of ListerHaulables.ShouldBeHaulable (RimWorld/ListerHaulables.cs:194-220):
                // forbidden state and haul designations. With no storage groups on the map (measured: groups=0), an
                // item is only haulable when its def is alwaysHaulable or it carries a haul designation, so a
                // different forbidden flag or a missing designation is exactly what empties the list on one peer.
                var forbidden = new List<int>();
                var designated = new List<int>();
                foreach (var thing in map.listerThings.AllThings)
                {
                    try
                    {
                        if (thing is null) continue;
                        if (thing.IsForbidden(Faction.OfPlayer)) forbidden.Add(thing.thingIDNumber);
                        if (map.designationManager.DesignationOn(thing, DesignationDefOf.Haul) is not null)
                            designated.Add(thing.thingIDNumber);
                    }
                    catch (Exception) { }
                }
                text.Append("m").Append(map.Index)
                    .Append(":haul=").Append(haulables.Count)
                    .Append(":hdig=").Append(Digest(haulables.Select(t => t.thingIDNumber)))
                    .Append(":things=").Append(map.listerThings.AllThings.Count)
                    // Sorted, unlike `hdig`: `AllThings` is in spawn order, spawn order is not simulation state, and a
            // blueprint the origin places locally while the others place at the stamped tick lands at a different
            // point in that list on each peer. Measured: two peers held the same 9028 things with the same ids and
            // still reported a divergence, because only the order differed. A real difference still shows up - a
            // missing, extra or renumbered thing changes the sorted set.
            // A blueprint's id is arbitrary: the origin places it the moment the player drags (the building
            // designator needs that to keep dragging) while the other peers place it at the stamped tick, so each side
            // draws a different id from its own counter. Comparing those ids reported a divergence on a colony whose
            // buildings were identical. Blueprints are compared by what they *are* - def and cell - and the count
            // still includes them, so a missing, extra or moved blueprint is still caught.
            .Append(":tdig=").Append(Digest(map.listerThings.AllThings
                .Where(t => t is not Blueprint)
                .Select(t => t.thingIDNumber)
                .OrderBy(id => id)))
            .Append(":bdig=").Append(DigestText(map.listerThings.AllThings
                .OfType<Blueprint>()
                .Select(b => (b.def.entityDefToBuild?.defName ?? "?") + "@" + b.Position.x + "," + b.Position.z)
                .OrderBy(text => text, StringComparer.Ordinal)))
                    // Storage groups and zones spelled out. Measured need: one round ended with a single
                    // difference, a haul destination group existing on one peer and not the other, while the thing
                    // set was identical - so it is not a building. Zones are not in the per-tick probe at all, which
                    // made "a stockpile exists on one side" invisible to every other field.
                    .Append(":groups=").Append(GroupsDetail(map))
                    .Append(":zones=").Append(ZonesDetail(map))
                    .Append(":forb=").Append(forbidden.Count)
                    .Append(":fdig=").Append(Digest(forbidden))
                    // The candidate set spelled out: every item whose def is EverHaulable, with its forbidden
                    // flag. The counts said "five items are forbidden on one peer and not the other", which is a
                    // real state difference; this names them so a dump answers which five without another run.
                    .Append(":hstate=").Append(HaulState(map))
                    // Designations as state, not just a count. Found missing: the dump only counted them
                    // (`counts["designation"]`), so two peers with different designation sets but the same count
                    // hashed equal. Measured lead: one peer applied `Designator_Plants.DesignateThing` at tick 2707
                    // and the other did not - invisible to the hash, visible later as a forbidden flag.
                    .Append(":des=").Append(DesignationDigest(map))
                    // Items in a pawn's hands. `IsForbidden` on a carried thing reports the carried state, so a
                    // stack that left the forbidden set may simply have been picked up rather than unforbidden -
                    // these two cases must be told apart before calling it a flag difference.
                    .Append(":holding=").Append(Holding(map))
                    .Append(":haulDes=").Append(designated.Count)
                    .Append(":ddig=").Append(Digest(designated))
                    .Append(';');
            }
            last = "shared=" + Digest(text.ToString()) + " " + text;
        }
        catch (Exception error) { last = "shared=error:" + error.GetType().Name; }
        return last;
    }

    /// <summary>Each storage group as "prefix:id:type@cell", sorted. A group that exists on one peer and not the
    /// other is an entity-existence difference, not a timing one, so it cannot be explained by the random stream.
    /// </summary>
    private static string GroupsDetail(Map map)
    {
        try
        {
            var rows = new List<string>();
            foreach (var group in map.haulDestinationManager.AllGroupsListInPriorityOrder)
            {
                if (group is null) continue;
                var parent = group.parent as Thing;
                var where = group.CellsList is { Count: > 0 } cells
                    ? cells[0].x + "|" + cells[0].z : "-";
                rows.Add((parent is null ? "zone" : "thing:" + parent.thingIDNumber)
                    + ":" + (parent?.def?.defName ?? group.parent?.GetType().Name ?? "?")
                    + ":" + group.Settings?.Priority + "@" + where
                    + "#" + (group.CellsList?.Count ?? -1));
            }
            rows.Sort(StringComparer.Ordinal);
            return rows.Count + ":" + Digest(rows.Select(r => r.GetHashCode()))
                + (rows.Count is > 0 and <= 8 ? ":" + string.Join(",", rows) : string.Empty);
        }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }

    /// <summary>Zones by id, type, cell count and label. Zones are absent from the per-tick probe entirely, so a
    /// stockpile that exists on one peer only produced no other evidence at all.
    /// </summary>
    private static string ZonesDetail(Map map)
    {
        try
        {
            var rows = new List<string>();
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone is null) continue;
                rows.Add(zone.ID + ":" + zone.GetType().Name + ":" + zone.CellCount + ":" + (zone.label ?? "-"));
            }
            rows.Sort(StringComparer.Ordinal);
            return rows.Count + ":" + Digest(rows.Select(r => r.GetHashCode()))
                + (rows.Count is > 0 and <= 8 ? ":" + string.Join(",", rows) : string.Empty);
        }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }

    /// <summary>Items carried by a pawn, as `<pawnId>&lt;<thingId&gt;:&lt;defName&gt;`, sorted.</summary>
    private static string Holding(Map map)
    {
        try
        {
            var rows = new List<string>();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                var holder = pawn?.inventory?.innerContainer;
                if (holder is null) continue;
                foreach (var thing in holder)
                {
                    if (thing is null) continue;
                    rows.Add(pawn!.thingIDNumber + "<" + thing.thingIDNumber + ":" + (thing.def?.defName ?? "?"));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            if (rows.Count > 48) return "n=" + rows.Count + ":" + Digest(rows.Select(r => r.GetHashCode()));
            return "n=" + rows.Count + ":" + string.Join(",", rows);
        }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }

    /// <summary>All designations as "defName@target", sorted, with a digest. This is state: two peers must
    /// agree on it, and until now only the count was checked.</summary>
    private static string DesignationDigest(Map map)
    {
        try
        {
            var rows = new List<string>();
            foreach (var designation in map.designationManager.AllDesignations)
            {
                if (designation is null) continue;
                var def = designation.def?.defName ?? "?";
                var target = designation.target.HasThing
                    ? "T" + designation.target.Thing?.thingIDNumber
                    : "C" + designation.target.Cell.x + "," + designation.target.Cell.z;
                rows.Add(def + "@" + target);
            }
            rows.Sort(StringComparer.Ordinal);
            var digest = Digest(rows.Select(r => r.GetHashCode()));
            if (rows.Count > 24) return rows.Count + ":" + digest;
            return rows.Count + ":" + digest + ":" + string.Join("|", rows);
        }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }

    /// <summary>Every item that could enter the haulable list, with its forbidden flag, sorted by id.
    ///
    /// Bounded: beyond 40 entries it degrades to a digest, because a big colony would otherwise put a thousand ids
    /// on one line and the line would stop being readable exactly when it matters.
    /// </summary>
    private static string HaulState(Map map)
    {
        try
        {
            // Only the forbidden items, not every haulable one: a colony has ~1400 EverHaulable things, which
            // exceeded the readability bound and degraded to a digest - so the list could not name the handful that
            // the counts said differed. The forbidden set is ~50 entries, which is exactly the size that stays
            // readable.
            var rows = new List<string>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is null) continue;
                if (!thing.IsForbidden(Faction.OfPlayer)) continue;
                var storage = thing.IsInAnyStorage() ? 1 : 0;
                // defName and position too: five ids that differ say "something happened to five things"; a def
                // name and a cell say whether it was one stack split or five unrelated flags. Four of the measured
                // ids were consecutive (36155-36158), which points at a single split or unload event - this field
                // is what turns that guess into a fact.
                // Pipe-separated, not comma: the entry list is already comma-separated, so `@127,136` split into
                // two entries and the parser silently read the z as the next id. The data was right; the format made
                // it unreadable.
                var where = thing.Spawned ? thing.Position.x + "|" + thing.Position.z : "-";
                rows.Add(thing.thingIDNumber + ":" + (thing.def?.EverHaulable == true ? "H" : "-") + storage
                    + ":" + (thing.def?.defName ?? "?") + "@" + where);
            }
            rows.Sort(StringComparer.Ordinal);
            if (rows.Count > 64) return "n=" + rows.Count + ":" + Digest(rows.Select(r => r.GetHashCode()));
            return "n=" + rows.Count + ":" + string.Join(",", rows);
        }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }

    private static string Digest(IEnumerable<int> ids)
    {
        var text = string.Join(",", ids);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10].ToLowerInvariant();
    }

    private static string DigestText(IEnumerable<string> parts) => Digest(string.Join(";", parts));
    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
}












