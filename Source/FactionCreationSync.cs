using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Carries factions the authority adds to `FactionManager` after the shared save was loaded (round 162).
///
/// <para><b>The defect.</b> `FactionSync` carries relations, cooldowns and ideoligion per faction - all of them keyed by a
/// faction both ends already have, because they came from the same save. But the game also **creates** factions during
/// play: `FactionGenerator.NewGeneratedFactionWithRelations` (`:487xxx` area, the ledger row that was uncovered) runs on
/// the authority when a quest or incident needs a new faction. Nothing carried the faction itself, so on a replica it
/// simply does not exist: its pawns resolve to no faction (`ReplicaPawn.FactionLoadId` counts them as unfiled), the
/// faction screen does not list it, and every relation the authority holds about it points at an id the replica cannot
/// name.</para>
///
/// <para><b>Why `FactionManager.Add` and not a list mutation.** `Add` (`:487771`) is the game's own door: it appends to
/// `allFactions`, calls the **private** `RecacheFactions` (`:487982`) that rebuilds eleven cached pointers -
/// `OfPlayer`, `OfMechanoids`, `OfInsects`, `OfAncients`, `OfAncientsHostile`, `OfEmpire`, `OfPirates`, `OfHoraxCult`,
/// `OfEntities`, `OfTradersGuild`, `OfSalvagers` - and notifies every map's `events`. Writing `allFactions` directly
/// would leave all eleven stale, and `Faction.OfPlayer` (used by thousands of call sites to answer "is this ours?")
/// would keep pointing at whatever it pointed at before. The ledger names `RecacheFactions` as a patch point precisely
/// because it is private: the right move is to call the public method that already contains it.</para>
///
/// <para><b>Why a whole Scribe fragment per faction.** `Faction.ExposeData` (`:485495`) writes def, name, loadID,
/// relations, kidnapped tracker, ideoligion, predator threats, the defeat flag and the five cooldown stamps. A field list
/// would silently miss every field a mod adds; the fragment is exactly the set the game itself saves.</para>
///
/// <para><b>Sent once per faction, never repeated.</b> A faction exists from its creation until the save ends, so the
/// channel is an event list: `EventUnion.ById` keeps earlier creations alive across frame merges. The first publish
/// after a world load sends the whole table once (baseline) - a handful of kilobytes, measured by
/// `faction-create size`.</para></summary>
internal static class FactionCreationSync
{
    /// <summary>Load ids already on the wire. Populated by the baseline publish as well, so an ordinary faction that
    /// came from the shared save is never sent.</summary>
    private static readonly HashSet<int> sent = [];

    internal static long Sent { get; private set; }
    internal static long Applied { get; private set; }
    internal static long Created { get; private set; }
    internal static int UnresolvedDef { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int LastRows { get; private set; }
    internal static long LastBytes { get; private set; }

    /// <summary>Factions that appeared since the last publish, or null when nothing is new.</summary>
    internal static IReadOnlyList<ReplicaFactionCreation>? Publish()
    {
        try
        {
            var manager = Find.FactionManager;
            if (manager?.AllFactionsListForReading is not { } factions) return null;
            List<ReplicaFactionCreation>? rows = null;
            foreach (var faction in factions)
            {
                if (faction?.def is null) continue;
                if (!sent.Add(faction.loadID)) continue;
                var xml = ScribeBridge.SaveOrNull(faction);
                if (xml is null)
                {
                    // Scribe was busy. Put the id back so the next publish retries instead of losing it silently.
                    sent.Remove(faction.loadID);
                    continue;
                }
                (rows ??= []).Add(new ReplicaFactionCreation(faction.loadID, faction.def.defName, xml));
                Sent++;
            }
            if (rows is not null)
            {
                LastRows = rows.Count;
                LastBytes = 0;
                foreach (var row in rows) LastBytes += row.Xml.Length + row.Def.Length + 16;
            }
            return rows;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 派系新建发布失败：" + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Adds every faction the replica does not have. Idempotent: a faction that already exists is counted and
    /// skipped - the baseline and a merged older frame both contain the same rows.</summary>
    internal static void Apply(IReadOnlyList<ReplicaFactionCreation> rows)
    {
        foreach (var row in rows)
        {
            if (row is null || row.Xml.Length == 0) continue;
            try
            {
                var manager = Find.FactionManager;
                if (manager?.AllFactionsListForReading is not { } factions) return;
                if (factions.Any(faction => faction is not null && faction.loadID == row.LoadID))
                {
                    Applied++;
                    continue;
                }
                var faction = CreateFromFragment(row.Xml);
                if (faction is null) { ApplyFailures++; continue; }
                if (faction.def is null) { UnresolvedDef++; continue; }
                // The game's own door: appends, rebuilds the eleven cached pointers and notifies every map.
                manager.Add(faction);
                Created++;
            }
            catch (Exception error)
            {
                ApplyFailures++;
                if (ApplyFailures <= 6)
                    Log.Warning("[TurboTick] 派系新建应用失败：" + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    /// <summary>Builds a faction from a fragment <b>without</b> adding it to any manager.
    ///
    /// <para>Separated from <see cref="Apply"/> so a probe can read the construction half on its own: a fragment that
    /// parses but produces a faction with a null `def` would be added to `allFactions` and then throw inside
    /// `RecacheFactions` ("Some factions were null after loading"), which is a manager-wide failure rather than one bad
    /// row.</para></summary>
    internal static Faction? CreateFromFragment(string xml)
    {
        var faction = new Faction();
        return ScribeBridge.Load(faction, xml) ? faction : null;
    }

    /// <summary>Would the next publish carry this faction? False for one already on the wire, false for an id that is not
    /// in the manager at all - the notice half of the channel, readable without creating anything.</summary>
    internal static bool WouldSend(int loadId)
    {
        var manager = Find.FactionManager;
        if (manager?.AllFactionsListForReading is not { } factions) return false;
        var exists = factions.Any(faction => faction is not null && faction.loadID == loadId);
        return exists && !sent.Contains(loadId);
    }

    /// <summary>Bytes of every faction's fragment, largest first - the baseline a fresh world sends once.</summary>
    internal static IReadOnlyList<(int LoadId, string Def, int Bytes)> Sizes()
    {
        var rows = new List<(int, string, int)>();
        var manager = Find.FactionManager;
        if (manager?.AllFactionsListForReading is not { } factions) return rows;
        foreach (var faction in factions)
        {
            if (faction?.def is null) continue;
            var xml = ScribeBridge.SaveOrNull(faction);
            if (xml is null) continue;
            rows.Add((faction.loadID, faction.def.defName, xml.Length));
        }
        rows.Sort((left, right) => right.Item3.CompareTo(left.Item3));
        return rows;
    }

    internal static void Reset() => sent.Clear();

    internal static string Summary()
    {
        var manager = Find.FactionManager;
        var total = manager?.AllFactionsListForReading?.Count ?? 0;
        return $"本端 {total} 个派系（发 {Sent} 个 / 应用 {Applied}、新建 {Created}；"
            + $"上次 {LastRows} 行 {LastBytes} B；缺 def {UnresolvedDef}、失败 {ApplyFailures}）";
    }
}
