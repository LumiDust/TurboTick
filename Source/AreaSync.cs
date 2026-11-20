using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

[HarmonyLib.HarmonyPatch(typeof(RimWorld.AutoHomeAreaMaker), nameof(RimWorld.AutoHomeAreaMaker.MarkHomeAroundThing))]
internal static class ReplicaHomeAreaSuppressPatch
{
    /// <summary>Stops a replica from growing its own home area.
    ///
    /// <para><b>Why this is needed now and was not before.</b> <c>AutoHomeAreaMaker.ShouldAdd</c> (`:685417-685424`) tests
    /// <c>Find.PlaySettings.autoHomeArea</c> and <c>Current.ProgramState == ProgramState.Playing</c> - and nothing else.
    /// Until `PlaySettingsSync` existed, a replica had whatever value its own save carried, so this could disagree with
    /// the authority by one setting. Now the setting is synchronized, which means the replica satisfies the check too:
    /// every building it spawns from a frame stamps a 4-cell border into its local home area.</para>
    ///
    /// <para>Those stamps are not the authority's cells. `AreaSync` sends the authority's grid, and the apply is a diff -
    /// it would clear the locally invented cells on the next frame and the replica would flap: a border appears, then
    /// disappears, then appears again as the next building arrives. Worse, between frames the replica's temperature and
    /// "indoors" queries read a home area the authority never had.</para>
    ///
    /// <para><b>Suppressing the leaf rather than the three callers.</b> <c>Notify_BuildingSpawned</c>,
    /// <c>Notify_BuildingClaimed</c> and <c>Notify_ZoneCellAdded</c> all funnel into <c>MarkHomeAroundThing</c>, which is
    /// also public and called directly; patching the leaf covers every entry point, including a mod that calls it.</para>
    ///
    /// <para>Gated on `Frozen`, so the predictive path keeps vanilla behaviour: there the local simulation owns the area
    /// because there is no other authority to own it.</para></summary>
    private static bool Prefix() => !AuthoritativeReplica.Frozen;
}

/// <summary>Carries the built-in areas' cell grids, which nothing else in this mod touches.
///
/// <para><b>Why this file exists.</b> Areas are not zones and they are not things. They live in
/// <c>Map.areaManager.AllAreas</c> (<c>:64076</c>), their content is one <c>BoolGrid</c> per area
/// (<c>Area.ExposeData</c> `:63989` scribes only <c>ID</c> and <c>innerGrid</c>), and no frame enumerates them.
/// `ZoneSync` carries <c>Zone</c>; a stockpile and a home area look identical in the UI and travel by completely
/// different routes. So on a replica the player drew stockpiles correctly and the home area was empty.</para>
///
/// <para><b>What an empty home area changes, none of which is cosmetic.</b>
/// <list type="number">
/// <item><c>Area_Home</c> is the fallback every temperature and "indoors" query reads, and <c>autoHomeArea</c>
/// (now synced by <see cref="PlaySettingsSync"/>) decides whether a new building joins it.</item>
/// <item><c>Area_SnowOrSandClear</c> is where pawns clear snow; with no cells nobody is ever assigned the work, so the
/// replica shows snow that the authority has already cleared.</item>
/// <item><c>Area_BuildRoof</c>/<c>Area_NoRoof</c> decide where roof construction is ordered and where it is forbidden.</item>
/// <item><c>Pawn_PlayerSettings.area</c> (`:103386`, scribed as a reference under <c>"areaAllowed"</c> `:477417`) is a
/// restriction on a pawn. With the areas empty, a restricted colonist reads as "allowed nowhere", which is the opposite
/// of what the player set.</item>
/// </list></para>
///
/// <para><b>Publishing whole grids, not diffs.</b> A grid is <c>Size.x * Size.z</c> bits; the wire form here is a
/// <c>;</c>-joined list of the set cells, the same encoding `ReplicaZone.Cells` uses. A 250x250 map whose home area
/// covers the whole base is a few thousand cells - tens of kilobytes, once, and only when the set changes. A per-cell
/// diff would be smaller per frame but needs a baseline the replica is guaranteed to share, and a replica that joined
/// late does not have one. Whole-grid is the version that cannot silently diverge.</para>
///
/// <para><b>Why the built-in areas and not <c>Area_Allowed</c>.</b> Allowed areas belong to a settlement and are rebuilt
/// by <see cref="SettlementAreas"/> from the settlement table; they have their own creator and lifecycle. This class
/// carries only the four (five with Biotech) areas vanilla creates in <c>AddStartingAreas</c> (`:64100-64112`),
/// identified by type rather than by id: the home area's id comes from <c>Find.UniqueIDsManager</c> and is not the same
/// number on two peers, so an id-keyed payload would write one area's grid into another.</para></summary>
internal static class AreaSync
{
    /// <summary>The four built-in areas, in a fixed order so the payload's shape does not depend on list order.</summary>
    private static readonly string[] Kinds = ["home", "buildRoof", "noRoof", "snowClear"];

    /// <summary>Last payload sent per (map, kind). Absent means "this peer has never been told", which is different from
    /// "unchanged" and must produce a row - see the comment in <see cref="Publish"/>.</summary>
    private static readonly Dictionary<string, string> lastSent = [];

    internal static long AreasSent { get; private set; }
    internal static long AreasApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int UnknownKinds { get; private set; }

    /// <summary>One row per built-in area whose set cells moved since the last frame.</summary>
    internal static List<ReplicaArea>? Publish()
    {
        List<ReplicaArea>? rows = null;
        try
        {
            foreach (var map in Find.Maps)
            {
                var manager = map.areaManager;
                if (manager?.AllAreas is null) continue;
                foreach (var kind in Kinds)
                {
                    var area = Resolve(manager, kind);
                    if (area is null) continue;
                    var cells = Encode(area);
                    var key = map.uniqueID.ToString(CultureInfo.InvariantCulture) + "|" + kind;
                    // No baseline means SEND. The first version of this file primed instead - it recorded the grid and
                    // emitted nothing, so the second call found `before == cells` and also emitted nothing: the grid was
                    // never sent at all, and the census showed an empty channel that looked like an idle one. A peer
                    // joining an existing colony has to receive the grid even though nothing changed while it connected,
                    // which is why `sent == value` (not `primed`) is the guard, the same shape as `AdaptationSync`.
                    if (lastSent.TryGetValue(key, out var before) && before == cells) continue;
                    lastSent[key] = cells;
                    (rows ??= []).Add(new ReplicaArea(map.uniqueID, kind, cells));
                    AreasSent++;
                }
            }
        }
        catch (System.Exception error)
        {
            Log.Warning("[TurboTick] area publish: " + error.GetType().Name + ": " + error.Message);
        }
        return rows;
    }

    /// <summary>Writes one area's grid on a replica.
    ///
    /// <para>The set is replaced by diffs rather than cleared first, for the same reason `ZoneSync.ApplyCells` does:
    /// <c>Area.Set</c> only touches cells whose value actually changes, and clearing a grid to refill it makes every cell
    /// flip twice, which dirties the whole section mesh for a frame. On a large home area that is the difference between
    /// a repaint and a stutter.</para></summary>
    internal static void Apply(ReplicaArea state)
    {
        try
        {
            var map = FindMap(state.Map);
            if (map?.areaManager is null) { ApplyFailures++; return; }
            var area = Resolve(map.areaManager, state.Kind);
            if (area is null)
            {
                // A kind this build does not have (an older game without Biotech, or a future area type). Counted rather
                // than guessed at: writing a home grid into whatever area happened to be first would be silent damage.
                if (UnknownKinds++ < 4)
                    Log.Warning("[TurboTick] area apply: unknown kind " + state.Kind);
                return;
            }
            var wanted = Decode(state.Cells, map);
            // Remove first: `Area.Set(cell, false)` is the only way to clear a bit, and doing adds first would leave the
            // transient union of both sets visible for a frame.
            var toClear = new List<IntVec3>();
            foreach (var cell in area.ActiveCells)
                if (!wanted.Contains(cell)) toClear.Add(cell);
            // `Area.Set(IntVec3, bool)` is `protected virtual` (`:64020`); the public indexer setter
            // (`:63960-63966`) forwards to it, so the grid is writable without reflection. Writing the indexer also
            // keeps a subclass that overrides `Set` (to dirty its own overlay) in the loop.
            foreach (var cell in toClear) area[cell] = false;
            foreach (var cell in wanted)
                if (!area[cell]) area[cell] = true;
            lastSent[state.Map.ToString(CultureInfo.InvariantCulture) + "|" + state.Kind] = state.Cells;
            AreasApplied++;
        }
        catch (System.Exception error)
        {
            ApplyFailures++;
            Log.Warning("[TurboTick] area apply (" + state.Kind + "): " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Maps a wire kind onto the area object. Type-keyed on purpose — see the class remarks about ids.</summary>
    private static Area? Resolve(AreaManager manager, string kind) => kind switch
    {
        "home" => manager.Home,
        "buildRoof" => manager.BuildRoof,
        "noRoof" => manager.NoRoof,
        "snowClear" => manager.SnowOrSandClear,
        _ => null,
    };

    private static string Encode(Area area)
    {
        var text = new StringBuilder();
        foreach (var cell in area.ActiveCells)
        {
            if (text.Length > 0) text.Append(';');
            text.Append(cell.x.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(cell.z.ToString(CultureInfo.InvariantCulture));
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
            if (!int.TryParse(pair.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x))
                continue;
            if (!int.TryParse(pair.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
                continue;
            var cell = new IntVec3(x, 0, z);
            // A grid that came from a differently sized map would place cells outside this one; dropping them here keeps
            // the count honest instead of letting `Area.Set` throw mid-apply with half the grid written.
            if (!cell.InBounds(map)) continue;
            wanted.Add(cell);
        }
        return wanted;
    }

    private static Map? FindMap(int uniqueId)
    {
        foreach (var map in Find.Maps)
            if (map.uniqueID == uniqueId) return map;
        return null;
    }

    /// <summary>Forgets the last-sent digests so a replica starting from a new baseline is told every grid again.</summary>
    internal static void Reset() => lastSent.Clear();

    internal static string Summary()
    {
        var text = $"区域 发 {AreasSent} / 应用 {AreasApplied}（失败 {ApplyFailures}";
        if (UnknownKinds > 0) text += $"，未知类型 {UnknownKinds}";
        text += "）";
        // The count of areas actually published says whether the channel has a carrier at all; without it "0 sent" reads
        // the same for "no home area exists" and "the publisher never ran".
        var areas = 0;
        var cells = 0;
        foreach (var map in Find.Maps)
        {
            var manager = map.areaManager;
            if (manager?.AllAreas is null) continue;
            foreach (var kind in Kinds)
            {
                var area = Resolve(manager, kind);
                if (area is null) continue;
                areas++;
                cells += area.TrueCount;
            }
        }
        return text + $"，本端 {areas} 张内建区域共 {cells} 格";
    }
}
