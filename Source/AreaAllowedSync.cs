using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Carries every player-created <c>Area_Allowed</c> - which exist, what they are called, and which cells they hold.
///
/// <para><b>Why this is not part of <see cref="AreaSync"/>.</b> That channel carries the four (five with Biotech) areas
/// vanilla creates in <c>AreaManager.AddStartingAreas</c> (`:64100-64113`) and identifies each by <em>type</em>. There is
/// exactly one `Home`, one `BuildRoof`, one `NoRoof`, one `SnowOrSandClear`, so a type is an identity. A player-created
/// allowed area has no type slot: the manager holds a list of up to ten (`CanMakeNewAllowed` `:64211-64214`), each with a
/// label the player chose and an `ID` from `Find.UniqueIDsManager.GetNextAreaID()` (`:63996`) - a <em>per-peer</em>
/// counter that is only equal across two peers because the save happened to be loaded on both.</para>
///
/// <para><b>What was broken without it.</b> Every write to an allowed area reached the map's own object table and stopped
/// there. Measured, the four ways a player changes one:</para>
/// <list type="number">
/// <item><b>Create</b> - <c>Dialog_ManageAreas.DoWindowContents</c> "NewArea" (`:635395`) and the Copy button
/// (`:635463`) both call <c>AreaManager.TryMakeNewAllowed</c> (`:64216-64226`). The new area exists on one peer's map
/// and in nobody else's list, so the area list has ten rows on one screen and nine on the other.</item>
/// <item><b>Rename</b> - <c>Dialog_Rename</c> writes <c>RenamableLabel</c> and then calls the virtual <c>OnRenamed</c>
/// (`:196318`). Nothing listened, so the other peer kept the old name - and because <see cref="ReplicaAllowedArea"/> is
/// keyed by label, a rename that did not travel would be read as one area deleted and another created.</item>
/// <item><b>Paint cells</b> - <c>Designator_AreaAllowedExpand.DesignateSingleCell</c> (`:618202-618205`) is literally
/// <c>SelectedArea[c] = true</c>, and the Clear designator is the same with <c>false</c>. Both are
/// `Designator_Cells` subclasses, and <see cref="DesignatorSync.EncodeChunks"/> only knows
/// `Designator_Build` and `Designator_ZoneAdd` - so the drag publishes <b>nothing at all</b>. This is the one that
/// matters most: an allowed area decides where a colonist may walk, so two peers disagree about reachability and the
/// symptom is a colonist that refuses to go somewhere on one screen and goes there on the other.</item>
/// <item><b>Delete / invert / clear</b> - <c>Area.Delete</c> (`:64037-64040`), <c>Area.Invert</c> (`:64056`) and
/// <c>Area.Clear</c> (`:64061`). `AreaOp` already carries these, and `ApplyAreaOp` looks the area up by
/// <c>Area.ID</c> - the one field that is not the same number on two peers. So the command was published, decoded and
/// then silently missed for every area that was not created by the same save load.</item>
/// </list>
///
/// <para><b>Design: the authority's list is the list.</b> One row per area per map, sent whole when the set changes,
/// carrying label and cells. The replica's allowed areas are reconciled to match: missing ones are created, extra ones
/// removed, existing ones have their cells diffed. Sending all ten labels is a few hundred bytes; a diff would need a
/// shared baseline that a replica joining late does not have - the same conclusion <see cref="AreaSync"/> reached.</para>
///
/// <para><b>What this deliberately does not carry: the colour.</b> <c>Area_Allowed.colorInt</c> is drawn from
/// <c>Rand.Value</c> in the constructor (`:392893-392894`), so two peers creating the same area independently get two
/// different colours, and the colour has no effect on behaviour - it is the icon tint in the manage-areas list. Sending it
/// would make the channel look like it carries more than it does.</para></summary>
internal static class AreaAllowedSync
{
    /// <summary>Prefix on the label of every area this mod creates, from <see cref="SettlementAreas"/>. Those are a
    /// settlement overlay, not a work restriction, and they have their own creator - carrying them here would have two
    /// channels writing the same objects with different rules about who wins.
    ///
    /// <para>Read from <see cref="SettlementAreas.Marker"/> rather than repeated as a literal: this constant decides
    /// whether an area is "ours" or "the player's", and two copies of such a value drift silently. The failure would be
    /// visible only as duplicated overlays on a peer - the settlement's own area plus a copy created by this channel
    /// from a list that no longer recognised it.</para></summary>
    private const string SettlementMarker = SettlementAreas.Marker;

    /// <summary>Last payload sent per (map, label). Absent means "never told", which must produce a row - see
    /// <see cref="Publish"/>.</summary>
    private static readonly Dictionary<string, string> lastSent = [];

    internal static long AreasSent { get; private set; }
    internal static long AreasApplied { get; private set; }
    internal static long AreasCreated { get; private set; }
    internal static long AreasRemoved { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>One row per allowed area whose label or cell set moved since the last frame, plus one row per area that
    /// disappeared (sent with an empty label list is not expressible, so departures are carried by the absence of a row:
    /// the publish sends the <em>complete</em> list whenever it changed at all).
    ///
    /// <para>That is the reason this method returns the whole list rather than the changed rows: a removal cannot be
    /// expressed as a per-row delta. A list that shrank is a different list, and the replica reconciles against it.</para>
    /// </summary>
    internal static List<ReplicaAllowedArea>? Publish()
    {
        List<ReplicaAllowedArea>? rows = null;
        try
        {
            var signature = new StringBuilder();
            foreach (var map in Find.Maps)
            {
                var manager = map.areaManager;
                if (manager?.AllAreas is null) continue;
                foreach (var area in Playable(manager))
                {
                    var cells = Encode(area);
                    signature.Append(map.uniqueID.ToString(CultureInfo.InvariantCulture))
                        .Append('|').Append(area.Label).Append('=').Append(cells).Append('\n');
                }
            }
            // The whole list is the unit, not one row. A removal cannot be expressed as a per-row delta - a list that
            // shrank is a different list - so a digest decides whether to send at all and the rows are then emitted in
            // full. Per-row `lastSent` is kept underneath for the census, not for the decision.
            //
            // No baseline means SEND. The first version of `AreaSync` primed instead - it recorded the value and emitted
            // nothing, so the second call found `before == cells` and also emitted nothing, and the value was never sent
            // at all while the census showed an idle channel. A digest recorded only after a send cannot produce that
            // state, because absent != unchanged.
            var digest = signature.ToString();
            if (lastSent.TryGetValue(DigestKey, out var previous) && previous == digest) return null;
            lastSent[DigestKey] = digest;
            // Allocated before the loop, not on the first row. The empty list is a payload: "the authority has no allowed
            // areas" is how a replica learns to delete the ones it has, and returning null for it would make deletion
            // impossible to express - a shape that has already hidden a channel in this codebase once.
            rows = [];
            foreach (var map in Find.Maps)
            {
                var manager = map.areaManager;
                if (manager?.AllAreas is null) continue;
                foreach (var area in Playable(manager))
                {
                    var cells = Encode(area);
                    lastSent[map.uniqueID.ToString(CultureInfo.InvariantCulture) + "|" + area.Label] = cells;
                    rows.Add(new ReplicaAllowedArea(map.uniqueID, area.Label, cells));
                    AreasSent++;
                }
            }
        }
        catch (System.Exception error)
        {
            Log.Warning("[TurboTick] allowed area publish: " + error.GetType().Name + ": " + error.Message);
        }
        return rows;
    }

    /// <summary>Marker key for the whole-list digest. Cannot collide with a map|label key because it starts with a
    /// character no label can start with after the map number and separator.</summary>
    private const string DigestKey = "#list";

    /// <summary>The areas a player created and can still edit: `Area_Allowed`, minus the settlement overlays.</summary>
    private static IEnumerable<Area_Allowed> Playable(AreaManager manager)
    {
        foreach (var area in manager.AllAreas)
        {
            if (area is not Area_Allowed allowed) continue;
            if (allowed.RenamableLabel is { } label && label.StartsWith(SettlementMarker, System.StringComparison.Ordinal))
                continue;
            yield return allowed;
        }
    }

    /// <summary>Reconciles one map's allowed-area list against the authority's.
    ///
    /// <para>The list arrives whole, so this is a three-way reconciliation and each part is separately counted: create
    /// what is missing, delete what is extra, diff the cells of what is in both. Counting them apart is what makes a
    /// stubborn divergence diagnosable - "created 0, removed 0, diffed 3" is a cell problem, and "created 4, removed 4"
    /// on every frame is a rename loop.</para></summary>
    internal static void Apply(IReadOnlyList<ReplicaAllowedArea> states)
    {
        try
        {
            var wanted = new Dictionary<int, List<ReplicaAllowedArea>>();
            foreach (var state in states)
            {
                if (!wanted.TryGetValue(state.Map, out var list)) wanted[state.Map] = list = [];
                list.Add(state);
            }
            foreach (var map in Find.Maps)
            {
                var manager = map.areaManager;
                if (manager?.AllAreas is null) continue;
                var rows = wanted.TryGetValue(map.uniqueID, out var list) ? list : [];
                var byLabel = new Dictionary<string, ReplicaAllowedArea>();
                foreach (var row in rows) byLabel[row.Label] = row;
                // Remove first. An area the authority does not have must not survive a frame, because the whole point of
                // the list being authoritative is that the replica's set is the same set.
                foreach (var area in Playable(manager).ToList())
                {
                    if (byLabel.ContainsKey(area.Label)) continue;
                    manager.AllAreas.Remove(area);
                    // The selected-area pointer outlives the area it points at otherwise, and `Designator_AreaAllowed`'s
                    // CanDesignateCell dereferences it (`:618196`) - measured as a null-guard, not a crash, but the
                    // designator then refuses every cell with no explanation the player can see.
                    if (Designator_AreaAllowed.SelectedArea == area) Designator_AreaAllowed.ClearSelectedArea();
                    AreasRemoved++;
                }
                foreach (var (label, row) in byLabel)
                {
                    var area = Playable(manager).FirstOrDefault(a => a.Label == label);
                    if (area is null)
                    {
                        if (!manager.CanMakeNewAllowed()) { ApplyFailures++; continue; }
                        // Constructed with the label so the area never exists under a default name: creating it and
                        // renaming it would emit two visual states for one arrival.
                        var created = new Area_Allowed(manager, label);
                        manager.AllAreas.Add(created);
                        area = created;
                        AreasCreated++;
                    }
                    ApplyCells(area, row.Cells, map);
                    AreasApplied++;
                }
            }
        }
        catch (System.Exception error)
        {
            ApplyFailures++;
            Log.Warning("[TurboTick] allowed area apply: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Diffs one grid onto the area, clearing first for the same reason <see cref="AreaSync"/> does: setting
    /// additions before removals would leave the transient union visible for a frame, and <c>Area.Set</c> only touches
    /// cells whose value actually changes so the diff costs nothing on the unchanged majority.</summary>
    private static void ApplyCells(Area_Allowed area, string cells, Map map)
    {
        var target = Decode(cells, map);
        foreach (var cell in area.ActiveCells.ToList())
            if (!target.Contains(cell)) area[cell] = false;
        foreach (var cell in target)
            if (!area[cell]) area[cell] = true;
    }

    private static string Encode(Area area)    {
        var text = new StringBuilder();
        foreach (var cell in area.ActiveCells)
        {
            if (text.Length > 0) text.Append(';');
            text.Append(cell.x.ToString(CultureInfo.InvariantCulture)).Append(',').Append(cell.z.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private static HashSet<IntVec3> Decode(string cells, Map map)
    {
        var wanted = new HashSet<IntVec3>();
        if (cells.Length == 0) return wanted;
        foreach (var pair in cells.Split(';'))
        {
            var comma = pair.IndexOf(',');
            if (comma <= 0) continue;
            if (!int.TryParse(pair.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) continue;
            if (!int.TryParse(pair.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)) continue;
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) continue;
            wanted.Add(cell);
        }
        return wanted;
    }

    internal static void Reset() => lastSent.Clear();

    internal static string Summary()
    {
        var areas = 0;
        var cells = 0;
        foreach (var map in Find.Maps)
        {
            var manager = map.areaManager;
            if (manager?.AllAreas is null) continue;
            foreach (var area in Playable(manager))
            {
                areas++;
                cells += area.TrueCount;
            }
        }
        return $"发 {AreasSent} / 应用 {AreasApplied}（新建 {AreasCreated}，删除 {AreasRemoved}，失败 {ApplyFailures}），"
            + $"本端 {areas} 个共 {cells} 格";
    }
}
