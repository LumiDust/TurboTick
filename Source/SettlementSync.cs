using System;
using System.Collections.Generic;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>Sends the settlement table to replicas, so the overlay and the gates agree on every peer.
///
/// <para><b>Why this is the prerequisite for the grant UI, and not a nice-to-have.</b> <c>SettlementData</c> lives in
/// <c>TurboGameComponent</c> and reaches a second peer only through the Scribe channel - which carries the world once, at
/// snapshot time. Any settlement declared after that snapshot exists on the authority alone: the replica's
/// <see cref="SettlementGates.Data"/> is empty, which means every gate short-circuits to "allowed" on the replica while the
/// authority refuses, and <c>SettlementAreas</c> has nothing to draw. A player would see no territory, no refusals, and a
/// build order that fails for no visible reason - the exact confusion the overlay was built to remove, from the other
/// side.</para>
///
/// <para><b>Why the whole table rather than a delta.</b> The table is small and bounded: a handful of settlements, a
/// membership entry per player, a point grant per (settlement, player) pair that somebody edited. A delta protocol would
/// need per-record identities, tombstones and a resync path - three ways to end up with a replica whose grants differ from
/// the authority's, which is the one outcome that must not happen because the two sides would then disagree about whether
/// an action is legal. Sending everything when a version counter moves is one comparison per frame in the common case.</para>
/// </summary>
internal static class SettlementSync
{
    /// <summary>Incremented by every mutation. The frame carries it; a replica compares it with the version it last
    /// applied and asks for nothing when they match.</summary>
    internal static long Version { get; private set; }

    /// <summary>Bumped whenever the table is edited. Called from every mutation site rather than from a diff, because a
    /// diff over a list of records would have to compare them field by field every frame for a table that changes a handful
    /// of times a session.</summary>
    internal static void Touch() => Version++;

    /// <summary>Drops membership entries for pawns that no longer exist on this peer, and returns how many went.
    ///
    /// <para><b>Why "no longer exists" is asked of the whole game, not of one map.</b> A colonist in a caravan is not on
    /// any map but is still alive, and a pawn in a gravship under way is not spawned either - deleting those would make a
    /// returning colonist an unaffiliated visitor in its own base. So the test is
    /// <c>Find.WorldPawns</c> plus every spawned pawn: only an id that is nowhere is dropped. A corpse is not a pawn
    /// here either, and that is correct - a dead colonist's membership has no reader once it is dead.</para>
    ///
    /// <para>Deliberately conservative in the other direction too: a pawn that cannot be found is kept when the world is
    /// not fully loaded (<c>Find.WorldPawns</c> is null during load), because pruning then would empty the table and the
    /// replica would see every colonist as unaffiliated for one frame.</para></summary>
    private static int PruneDepartedPawns(SettlementData data)
    {
        if (data.PawnHome.Count == 0) return 0;
        var alive = new HashSet<int>();
        try
        {
            foreach (var map in Find.Maps)
            {
                if (map?.mapPawns is null) continue;
                foreach (var pawn in map.mapPawns.AllPawns)
                    if (pawn is not null && !pawn.Destroyed) alive.Add(pawn.thingIDNumber);
            }
            // Passed pawns and caravan members: `WorldPawns` holds everything off-map that still exists.
            var worldPawns = Find.WorldPawns;
            if (worldPawns is null) return 0;   // not loaded: pruning now would empty the table
            foreach (var pawn in worldPawns.AllPawnsAliveOrDead)
                if (pawn is not null && !pawn.Destroyed && !pawn.Dead) alive.Add(pawn.thingIDNumber);
        }
        catch (Exception) { return 0; }
        // A table with entries but no live pawns anywhere means the scan failed, not that every colonist left.
        if (alive.Count == 0) return 0;
        var gone = 0;
        foreach (var id in data.PawnHome.Keys.Where(id => !alive.Contains(id)).ToList())
            if (data.PawnHome.Remove(id)) gone++;
        return gone;
    }

    /// <summary>Encodes the table into the frame's wire form. Null when there is nothing to send - no settlements at all,
    /// which is the shipped default and must cost zero bytes.</summary>
    internal static SettlementWire? Publish()
    {
        var data = SettlementGates.Data;
        if (data is null || !data.Active) return null;
        var sites = new List<SettlementSiteWire>(data.Sites.Count);
        foreach (var site in data.Sites)
        {
            if (site is null) continue;
            sites.Add(new SettlementSiteWire(site.Id, site.MapId, site.OwnerPlayer, site.Name,
                site.MinX, site.MinZ, site.MaxX, site.MaxZ, site.Retired, site.FoundedTick));
        }
        var members = new List<int>(data.Members.Count * 2);
        foreach (var (player, settlement) in data.Members)
        {
            members.Add(player);
            members.Add(settlement);
        }
        // Reconcile before encoding, because `PawnHome` has no removal path: it is written when a settlement is founded
        // (`ScriptedSettlement.Place`) and nothing ever takes an entry out. A pawn that died, was carried off in a
        // caravan, or was sold keeps its membership forever, so a settlement's reported member count only ever grows -
        // and `HomeOfPawn` answers "belongs to #1" for a pawn that no longer exists anywhere. That is invisible on a
        // single settlement and actively wrong on a multi-settlement colony, where the number is what the operator reads
        // to decide whether the split worked.
        //
        // Reconciled here rather than through a death hook because there is no single one: `Pawn.Kill` is what a death
        // goes through, but a pawn also leaves the world through a caravan, a transporter, a quest and a sell, and a hook
        // per path is a list that silently misses the next one. Asking the world once per publish is bounded by the
        // number of settlements and costs nothing when the table is empty.
        var stale = PruneDepartedPawns(data);
        if (stale > 0) Version++;
        var pawnHomes = new List<int>(data.PawnHome.Count * 2);
        foreach (var (pawn, settlement) in data.PawnHome)
        {
            pawnHomes.Add(pawn);
            pawnHomes.Add(settlement);
        }
        var grants = new List<int>(data.Grants.Count * 3);
        foreach (var record in data.Grants)
        {
            if (record is null) continue;
            grants.Add(record.From);
            grants.Add(record.To);
            grants.Add(record.Flags);
        }
        var defaults = new List<int>(data.Defaults.Count * 2);
        foreach (var (settlement, flags) in data.Defaults)
        {
            defaults.Add(settlement);
            defaults.Add(flags);
        }
        var pacts = new List<int>(data.Pacts.Count * 5);
        foreach (var pact in data.Pacts)
        {
            if (pact is null) continue;
            pacts.Add(pact.A);
            pacts.Add(pact.B);
            pacts.Add(pact.WarSince);
            pacts.Add(pact.Allied ? 1 : 0);
            pacts.Add(pact.NoticeUntil);
        }
        var keptCells = new List<int>(data.Overrides.Count * 4);
        foreach (var entry in data.Overrides.ForScribing())
        {
            keptCells.Add(entry.MapId);
            keptCells.Add(entry.X);
            keptCells.Add(entry.Z);
            keptCells.Add(entry.Owner);
        }
        return new SettlementWire(Version, sites, members, pawnHomes, grants, defaults, pacts, keptCells);
    }

    /// <summary>Replaces the replica's table with the authority's.
    ///
    /// <para>Replaces rather than merges, deliberately. A merge cannot express a <i>removal</i> - a grant that the owner
    /// revoked, a pawn that was moved to another settlement - without tombstone records, and getting that wrong leaves a
    /// replica granting something the authority stopped granting. Replacing makes the two sides equal by construction.</para>
    ///
    /// <para>The version is applied last: if the table were marked current and then an exception left it half-written, the
    /// replica would never retry. Applying it last means a failure simply re-applies next frame.</para>
    /// </summary>
    internal static void Apply(SettlementWire wire)
    {
        var data = SettlementGates.Data;
        if (data is null) return;
        try
        {
            data.Sites.Clear();
            foreach (var site in wire.Sites)
                data.Sites.Add(new SettlementSite
                {
                    Id = site.Id,
                    MapId = site.Map,
                    OwnerPlayer = site.Owner,
                    Name = site.Name,
                    MinX = site.MinX, MinZ = site.MinZ, MaxX = site.MaxX, MaxZ = site.MaxZ,
                    Retired = site.Retired,
                    FoundedTick = site.FoundedTick,
                });
            data.Members.Clear();
            for (var i = 0; i + 1 < wire.Members.Count; i += 2)
                // One entry per player by construction on the wire, and `OwnerPlayer` on each site carries the rest. A
                // replica that only had `Members` would refuse a player their own second settlement, which is what the
                // authority did before `Owns` existed.
                data.Members[wire.Members[i]] = wire.Members[i + 1];
            data.PawnHome.Clear();
            for (var i = 0; i + 1 < wire.PawnHomes.Count; i += 2)
                data.PawnHome[wire.PawnHomes[i]] = wire.PawnHomes[i + 1];
            data.Grants.Clear();
            for (var i = 0; i + 2 < wire.Grants.Count; i += 3)
                data.Grants.Add(new SettlementGrantRecord
                {
                    From = wire.Grants[i], To = wire.Grants[i + 1], Flags = wire.Grants[i + 2],
                });
            data.Defaults.Clear();
            for (var i = 0; i + 1 < wire.Defaults.Count; i += 2)
                data.Defaults[wire.Defaults[i]] = wire.Defaults[i + 1];
            // `WarSince` and `NoticeUntil` are absolute ticks and travel unshifted, like
            // `History.lastTickPlayerRaidedSomeone`: the replica's `TicksGame` is overridden to the authority's
            // (`ReplicaClockPatch`), so `AtWar` and `NoticeLeft` produce the same answer on both sides.
            data.Pacts.Clear();
            for (var i = 0; i + 4 < wire.Pacts.Count; i += 5)
            {
                data.Pacts.Add(new SettlementPact
                {
                    A = wire.Pacts[i],
                    B = wire.Pacts[i + 1],
                    WarSince = wire.Pacts[i + 2],
                    Allied = wire.Pacts[i + 3] != 0,
                    NoticeUntil = wire.Pacts[i + 4],
                });
            }
            // The overlay reads the table, so it has to be rebuilt whenever the table changes - otherwise the drawn
            // territory would describe the previous version of the grants it is supposed to explain.
            // Whole replacement, for the same reason as every other collection above: a cell exception the owner
            // removed has to disappear on the replica, and a merge cannot express that without a tombstone.
            // `Load` is the one path that rebuilds the index as well as the list - writing `Overrides` field by field
            // would leave `OwnerAt` answering from a stale dictionary, which is worse than not syncing it at all.
            var kept = new List<SettlementOverrides.Entry>(wire.KeptCells.Count / 4);
            for (var i = 0; i + 3 < wire.KeptCells.Count; i += 4)
                kept.Add(new SettlementOverrides.Entry(
                    wire.KeptCells[i], wire.KeptCells[i + 1], wire.KeptCells[i + 2], wire.KeptCells[i + 3]));
            data.Overrides.Load(kept);
            SettlementAreas.Rebuild(data);
            applied = wire.Version;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] settlement apply: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Last version this peer applied. Kept separate from <see cref="Version"/> so the authority's counter and
    /// the replica's bookkeeping cannot interfere.</summary>
    private static long applied = -1;

    internal static bool NeedsApply(SettlementWire? wire) => wire is not null && wire.Version != applied;

    internal static string Summary() =>
        applied < 0 ? "据点表 未同步" : $"据点表 版本 {applied}";
}
