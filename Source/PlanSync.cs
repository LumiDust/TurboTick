using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Planning marks, which live in <c>PlanManager</c> rather than in the thing list.
///
/// <para><b>Why a replica needs this.</b> A <c>Plan</c> is not a <c>Thing</c>: it is a coloured rectangle of cells that
/// <c>PlanManager</c> holds in its own list (<c>Verse/PlanManager.cs</c>, <c>allPlans</c>), so the frame's thing enumeration
/// never sees it. On a replica the marks a player drew do not exist at all - and a plan is how players <i>talk</i> about a
/// build before it exists: "the freezer goes here". A colony where one player's plans are invisible is one where two players
/// dig the same corridor from opposite ends.</para>
///
/// <para><b>Why the whole plan and not a cell diff.</b> A plan is created, renamed, recoloured and deleted far more often
/// than it is extended, and its identity is what makes it a shared reference ("the red one by the door"). Sending cells
/// without the plan's id and colour would put the marks on the replica but leave the two players unable to say which plan
/// they mean. The list is small - a colony has a handful - so whole-state on a version counter is the honest shape.</para>
///
/// <para><b>Why not reuse <c>Plan.ExposeData</c> through the Scribe channel.</b> That channel carries the world once, at
/// snapshot time. A plan drawn afterwards exists on its author's peer alone, which is the same gap the settlement table had.
/// It would also need the plan's <c>ILoadReferenceable</c> id to line up across peers, and plans created independently on
/// two peers would collide.</para>
internal static class PlanSync
{
    /// <summary>Last state sent, keyed by plan id, so an unchanged plan costs one comparison.</summary>
    private static readonly Dictionary<string, string> sent = [];

    /// <summary>Version for the whole list, because plans are added and removed as well as edited.</summary>
    internal static long Version { get; private set; }

    internal static void Touch() => Version++;

    internal static long PlansSent { get; private set; }
    internal static long PlansApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Encodes one plan: <c>mapIndex\nlabel\ncolourDef\ncellId...</c>.
    ///
    /// <para><b>Keyed by label, not by <c>Plan.GetUniqueLoadID()</c>.</b> That method returns <c>"Plan_" + ID</c>
    /// (`:86133-86136`) and `ID` comes from `Find.UniqueIDsManager.GetNextPlanID()` (`:85883`) - a <em>per-peer</em> counter.
    /// Two peers holding the same save number their plans differently, so an id-keyed payload makes every plan look new on
    /// the receiver: each reconcile either duplicates it or deletes it. The label is the field that survives a save round
    /// trip, and vanilla already keeps it unique across one map's list - `PlanManager.NewPlanName` walks "NewPlan 1..1000"
    /// and skips any name already taken (`:86220-86232`). Same reasoning as `ReplicaAllowedArea`, and the same cost: a
    /// rename travels as a delete plus a create.</para>
    ///
    /// <para><b>Why the map id leads the record.</b> A plan belongs to `planManager.map` (`Plan.Map`, `:85787`), and the
    /// cell indices further down are offsets into <em>that</em> map's `cellIndices`. With more than one map loaded - which
    /// is exactly what a multi-settlement game is - applying a cell index computed on one map to another writes the plan
    /// into a different place, or out of bounds, with no error. The field is <c>Map.uniqueID</c> rather than
    /// <c>Map.Index</c>, because `Index` is `Find.Maps.IndexOf(this)` (`:76452`) and shifts whenever an earlier map is
    /// removed, while `uniqueID` (`:76213`) is scribed (`:76818`) and therefore identical on both peers.</para>
    ///
    /// <para>The label may contain any character, so fields are separated by newlines rather than a printable delimiter - a
    /// player can type <c>|</c> into a plan name and that must not shift every field after it.</para></summary>
    private static string Encode(Plan plan)
    {
        var builder = new System.Text.StringBuilder(64);
        builder.Append(plan.Map?.uniqueID ?? -1).Append('\n')
            .Append(plan.label ?? string.Empty).Append('\n')
            .Append(plan.Color?.defName ?? string.Empty);
        foreach (var cell in plan.Cells)
            builder.Append('\n').Append(plan.Map.cellIndices.CellToIndex(cell));
        return builder.ToString();
    }

    /// <summary>The key two peers agree on: map index plus label. An empty label is not a key - `NewPlanName` never
    /// produces one, and a blank would collide with every other blank.</summary>
    private static string KeyOf(Plan plan) => (plan.Map?.uniqueID ?? -1).ToString(CultureInfo.InvariantCulture)
        + "/" + (plan.label ?? string.Empty);

    private static Map? FindMapByUniqueId(int uniqueId)
    {
        if (uniqueId < 0) return null;
        foreach (var map in Find.Maps)
            if (map.uniqueID == uniqueId) return map;
        return null;
    }

    /// <summary>Collects every plan whose encoded form changed. Null when nothing moved, which is the common frame.</summary>
    internal static (long Version, List<string>? Plans)? Publish(List<Plan>? plans)
    {
        if (plans is null) return null;
        // The version is part of the payload so a replica can tell "the list is unchanged" from "the list was replaced by one
        // with the same content" - the latter happens when a plan is deleted and another created in the same frame.
        if (plans.Count == 0 && sent.Count == 0 && Version == 0) return null;
        List<string>? changed = null;
        var live = new HashSet<string>();
        foreach (var plan in plans)
        {
            if (plan is null || plan.Map is null) continue;
            var key = KeyOf(plan);
            live.Add(key);
            var encoded = Encode(plan);
            if (sent.TryGetValue(key, out var previous) && previous == encoded) continue;
            sent[key] = encoded;
            (changed ??= []).Add(encoded);
            PlansSent++;
        }
        // Plans that no longer exist. The replica needs to be told, and the only way to say "delete id 7" in a payload made
        // of plan descriptions is to send the whole list - which is why the version moves on any removal.
        var removed = sent.Keys.Count(id => !live.Contains(id));
        if (removed > 0)
        {
            foreach (var id in sent.Keys.Where(id => !live.Contains(id)).ToList()) sent.Remove(id);
            Version++;
            return (Version, plans.Select(Encode).ToList());
        }
        if (changed is null) return null;
        // The version moves on any change, not only on a removal: the replica applies only when the version differs, so a
        // version that stood still would make every new plan and every edit invisible - the exact gap this channel exists to
        // close. Removal still needs the whole list, which is why that branch above sends everything.
        Version++;
        return (Version, changed);
    }

    /// <summary>Applies one encoded plan, creating it if the replica does not have it.
    ///
    /// <para>The plan is built through <c>PlanManager.RegisterPlan</c> and <c>Plan.AddCell</c> rather than by writing the
    /// private cell list, because <c>AddCell</c> also rebuilds the manager's cell grid and dirties the mesh - without it the
    /// plan exists in the list and draws nothing.</para></summary>
    internal static void Apply(Map? fallback, IEnumerable<string> encoded)
    {
        if (fallback?.planManager is null) return;
        try
        {
            foreach (var entry in encoded)
            {
                var lines = entry.Split('\n');
                if (lines.Length < 3) continue;
                // Field 0 is the map index. A payload from before this field existed cannot be recognised, and the fallback
                // map is what the caller passed - the replica's current map. Falling back rather than skipping keeps a
                // single-map session working exactly as before.
                var map = fallback;
                if (int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapIndex))
                    map = FindMapByUniqueId(mapIndex) ?? fallback;
                else continue;
                var label = lines[1];
                var colourName = lines[2];
                if (map?.planManager is null) continue;
                // Keyed by label, which is the only field both peers agree on: `Plan.GetUniqueLoadID()` is "Plan_"+ID and
                // ID is a per-peer counter (`:86133-86136`, `:85883`).
                if (label.Length == 0) continue;
                var existing = map.planManager.AllPlans.FirstOrDefault(plan => plan is not null && plan.label == label);
                if (existing is null)
                {
                    var plan = new Plan(DefDatabase<ColorDef>.GetNamedSilentFail(colourName), map.planManager)
                    {
                        label = label,
                    };
                    map.planManager.RegisterPlan(plan);
                    existing = plan;
                }
                else
                {
                    if (existing.label != label) existing.label = label;
                    var colour = DefDatabase<ColorDef>.GetNamedSilentFail(colourName);
                    // `Plan.Color` has a private setter, so a recolour is written through the backing field. The setter also
                    // rebuilds the material and dirties the mesh, which the field write skips - hence `Dirty()` afterwards,
                    // or the replica keeps drawing the old colour.
                    if (colour is not null && existing.Color != colour)
                    {
                        ColorField(existing) = colour;
                        existing.Dirty();
                    }
                }
                // Cells: add the missing, drop the extra. Walking the wanted set and then the existing set is what makes a
                // plan that shrank stop drawing where it no longer reaches.
                var wanted = new HashSet<int>();
                // Cells start at field 3: 0 = map index, 1 = label, 2 = colour. The old layout started them at 3 as well,
                // but with label at 0 - a stale peer's payload would therefore be read as a map index, fail to parse, and
                // be skipped by the `continue` above rather than writing a plan in the wrong place.
                for (var i = 3; i < lines.Length; i++)
                    if (int.TryParse(lines[i], out var index) && index >= 0 && index < map.cellIndices.NumGridCells)
                        wanted.Add(index);
                foreach (var cell in existing.Cells.ToList())
                    if (!wanted.Contains(map.cellIndices.CellToIndex(cell))) existing.RemoveCell(cell);
                foreach (var index in wanted)
                {
                    var cell = map.cellIndices.IndexToCell(index);
                    if (!existing.Cells.Contains(cell)) existing.AddCell(cell);
                }
                PlansApplied++;
            }
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] plan apply: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private static AccessTools.FieldRef<Plan, ColorDef>? colorField;

    private static AccessTools.FieldRef<Plan, ColorDef> ColorField =>
        colorField ??= AccessTools.FieldRefAccess<Plan, ColorDef>("color");

    internal static void Reset()
    {
        sent.Clear();
        Version = 0;
    }

    internal static string Summary() =>
        $"规划 发出 {PlansSent} / 应用 {PlansApplied}（失败 {ApplyFailures}）";
}
