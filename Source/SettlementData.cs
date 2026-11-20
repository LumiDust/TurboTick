using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>Multiple settlements inside one shared world, each with an owner and its own access rules.
///
/// <para><b>What this is not.</b> It is not "one colony per player" - that is RimWorld Together's shape, where each
/// player runs their own map and visits the others. This keeps <i>one</i> shared world and adds an ownership layer on
/// top of it: several settlements coexist, each belongs to somebody, and whether a pawn may do a particular thing in a
/// settlement is decided by a grant table. Vanilla AI keeps running unchanged; the decisions are gated where they are
/// made (<c>ReservationManager.CanReserve</c>, the designators, the job givers).</para>
///
/// <para><b>Why the layer and not a rewrite.</b> A shared colony already works: one map, one faction, everyone acts in
/// it. Splitting it into per-player simulations would reintroduce exactly the divergence problem the authoritative
/// headless design was built to eliminate. Ownership only needs to answer "may this pawn touch that", which is a
/// question the engine already asks at every action - so it can be answered from one table instead of being baked into
/// each system.</para>
///
/// <para><b>Why everything short-circuits on <see cref="Active"/>.</b> The failure mode of a permission layer is not a
/// crash: it is a colonist that quietly stops being allowed to build, and the player discovers it an hour later with a
/// half-finished wall. A save with no settlements must behave exactly as it does today, and the only way to be sure of
/// that is for every gate to return "allowed" before it looks at anything else. ColonyTogether reaches the same
/// conclusion with <c>SettlementData.Active => Sites.Count &gt; 0</c> (ct045.decompiled.cs:369277).</para>
public sealed class SettlementData : IExposable
{
    /// <summary>Every settlement in the world. Empty means the ownership layer is off, which is the shipped default:
    /// an old save has no node for this field and loads as empty.</summary>
    public List<SettlementSite> Sites = [];

    /// <summary>Which settlement each player belongs to, by the session's own player id. A player with no entry is
    /// unaffiliated and is governed by <see cref="Unaffiliated"/>.</summary>
    public Dictionary<int, int> Members = [];

    /// <summary>Which settlement each <i>pawn</i> belongs to, by <c>thingIDNumber</c>.
    ///
    /// <para>This is what the gates actually consult, and the reason is structural rather than a preference: the
    /// authority runs the AI for <b>every</b> player's pawns, and on the authority there is no local player at all
    /// (<c>RoomConnection.LocalPeerId</c> is 0). A gate that asked "what may the local player do here" would classify
    /// every pawn on the server as an unaffiliated visitor - including the owner's own colonists, which would then be
    /// refused permission to build in their own base.</para>
    ///
    /// <para>So the question the gates ask is "does this pawn belong to the settlement whose land it is standing on",
    /// which is answerable on any peer, for any pawn, without knowing who is looking. <see cref="Members"/> stays
    /// because it is what a UI edits ("give player 2 this settlement") and what a player's own grants are looked up
    /// by; the pawn map is the projection of that membership onto the pawns that are actually moving.</para></summary>
    public Dictionary<int, int> PawnHome = [];

    /// <summary>The settlement a pawn belongs to, or 0 when it belongs to none.
    ///
    /// <para><b>The promised fallback did not exist, and the obvious replacement is wrong (round 93).</b> This used to
    /// document "a pawn with no entry falls back to the land it stands on" while the body returned 0. The claim was not
    /// harmless: membership is written **only** when a settlement is founded (`ScriptedSettlement.Place` records the
    /// pawns inside the rectangle at that moment), so every colonist born, recruited or arrived afterwards has no entry,
    /// reads `mine == 0`, and is handed the visitor's four grants on its own land.</para>
    ///
    /// <para><b>Why "fall back to the land" cannot be the fix.</b> It was tried first and reverted the same hour: on a
    /// shared colony every player's pawns are the <i>same faction</i>, so "stands on this settlement's ground" is true of
    /// a stranger's colonist too. Treating that as membership would give every visitor `All` - destroying the one case
    /// the layer exists for. The land says nothing about whose pawn this is; only something outside the pawn does.</para>
    ///
    /// <para><b>Nothing else can answer it, and that was measured rather than assumed.</b> Two fallbacks were written and
    /// reverted in the same hour: "the land it stands on", and "the faction the settlement records"
    /// (<see cref="SettlementSite.FactionId"/> is scribed and read nowhere). Both fail for the same structural reason -
    /// on a shared colony every player's pawns are **one faction**, so neither the ground nor the faction separates the
    /// owner's colonist from a neighbour's visitor. ColonyTogether can key on faction (`ColonyFactions.ColonyOf`,
    /// `ct045:83138`) only because a colony there *is* a faction. So this table stays the sole authority, and the gap it
    /// leaves - members joining after founding - is counted by `SettlementRule.UnrecordedColonists` and closed by the
    /// `settlement-claim` command rather than by a guess that would hand a stranger `All`.</para></summary>
    public int HomeOfPawn(int pawnId) => PawnHome.TryGetValue(pawnId, out var home) ? home : 0;



    /// <summary>Per-settlement default grants, as <see cref="SettlementGrant"/> flags. A settlement with no entry uses
    /// <see cref="SettlementGrant.Default"/>, which is the permissive four (doors, care, benches, items) - the set a
    /// visitor needs to survive without being able to take the colony apart.</summary>
    public Dictionary<int, int> Defaults = [];

    /// <summary>Point grants: settlement id to player id to flags. This is where "you may use my workbenches but not
    /// my storage" lives.</summary>
    public List<SettlementGrantRecord> Grants = [];

    /// <summary>Wars and pacts are not modelled yet - the plan puts them last on purpose, because they are the only
    /// part that needs a wealth comparison and a tribute transfer. The fields exist so an older build reading a newer
    /// save leaves them alone instead of dropping the whole node.</summary>
    public List<string> PendingWars = [];

    /// <summary>Wars and alliances, one record per pair. Replaces <see cref="PendingWars"/>, which was a placeholder list of
    /// strings that nothing read: a pair is a record, not a sentence, because the gates have to answer "are these two at war"
    /// thousands of times a second and parsing a string for that would be absurd.</summary>
    public List<SettlementPact> Pacts = [];

    /// <summary>The ownership layer only governs anything once at least one settlement exists.
    ///
    /// <para>Every gate in this mod starts with <c>if (!Active) return true;</c>. That single line is what makes this
    /// whole feature additive: a save that never declares a settlement runs exactly the code it ran before.</para>
    /// </summary>
    public bool Active => Sites.Count > 0;

    /// <summary>What a player with no settlement may do. Deliberately the same four as
    /// <see cref="SettlementGrant.Default"/>: an unaffiliated visitor can walk through doors, sleep in a bed, use a
    /// workbench and eat from the larder, and cannot deconstruct anything or carry loose goods away.</summary>
    public const SettlementGrant Unaffiliated = SettlementGrant.Default;

    public const int DataSchema = 1;

    /// <summary>Every settlement a player owns, in site-id order.
    ///
    /// <para><b>Why this has to exist.</b> `Members` is <c>player → one site</c>, so a player who owns two settlements
    /// keeps only the last one written: `ScriptedSettlement.Place` does <c>data.Members[owner] = site.Id</c> with no
    /// check, and `SettlementSync.Apply` writes the wire's pairs the same way. Measured on this save with two sites both
    /// owned by player 1: <c>据点 2 个（成员 1，授权 0）；本端玩家 1（属于 2）</c> - Gamma was #1 and Delta #2, and player 1
    /// "belonged to" Delta only. The consequence is not cosmetic: `GrantsFor` projects a player onto `SettlementOf`, so
    /// player 1's grants in <em>Gamma</em> were answered from Delta's row (`All` there, `Default` in Gamma, as reported).</para>
    ///
    /// <para><b>Why the owner field, not a new table.</b> `SettlementSite.OwnerPlayer` is already scribed, already
    /// travels in `SettlementSiteWire`, and is already the field `Place` sets from its <c>owner</c> argument. The
    /// ownership of a site is a property of the site, and duplicating it into a second map would be a second thing to
    /// keep in sync on every frame - exactly the class of defect this round is about.</para>
    ///
    /// <para><b>Why this is a real multi-settlement need rather than a synthetic one.</b> Vanilla gives a player one
    /// colony, so `Members`' single-entry shape is never wrong there. The moment a colony spans two settlements - which
    /// is what this mode adds - a player owning both is the normal case, not an edge case.</para></summary>
    public List<int> SitesOwnedBy(int playerId)
    {
        var owned = new List<int>(2);
        if (playerId == 0) return owned;
        for (var i = 0; i < Sites.Count; i++)
        {
            var site = Sites[i];
            if (site is null || site.Retired || site.OwnerPlayer != playerId) continue;
            owned.Add(site.Id);
        }
        owned.Sort();
        return owned;
    }

    /// <summary>Whether <paramref name="playerId"/> owns the settlement, by either record.
    ///
    /// <para>`Members` first because it is what the UI edits and what an older save carries; `OwnerPlayer` second
    /// because it survives a player owning several. A player who owns nothing answers false.</para></summary>
    public bool Owns(int playerId, int settlement)
    {
        if (playerId == 0 || settlement == 0) return false;
        if (SettlementOf(playerId) == settlement) return true;
        var site = Site(settlement);
        return site is not null && !site.Retired && site.OwnerPlayer == playerId;
    }

    /// <summary>The settlement a player acts for, or 0 when they have none.</summary>
    /// <summary>The single settlement this player is recorded against, or 0.
    ///
    /// <para><b>Not the answer to "does this player own this land"</b> - see <see cref="Owns"/>. `Members` holds one
    /// entry per player, so a player who owns two settlements keeps whichever was written last. This accessor stays
    /// because a UI row ("which settlement am I in") and the wire both need the scalar; every <i>decision</i> goes
    /// through `Owns`.</para></summary>
    public int SettlementOf(int playerId) => Members.TryGetValue(playerId, out var site) ? site : 0;

    public SettlementSite? Site(int id)
    {
        for (var i = 0; i < Sites.Count; i++)
            if (Sites[i] is { } site && site.Id == id) return site;
        return null;
    }

    /// <summary>The grants a <i>player</i> holds in a settlement, by resolving the player to the settlement whose pawns
    /// act for them.
    ///
    /// <para><b>`To` means a settlement, not a player (round 88).</b> The field was read as a player id here and as a
    /// settlement id in <see cref="GrantsForPawn"/>, and both readings were live - so a grant written by the grant window
    /// for player 2 became a grant to settlement 2 the moment the numbers coincided, and the gates (which ask
    /// <see cref="GrantsForPawn"/>) acted on it. ColonyTogether has only the other shape: every grant is
    /// `Granted(ownerColony, groupColony, flags)` (`ct045:352100`), because on its authority there is no player either.
    /// Reading a player's membership to find their settlement is the same question asked one step earlier.</para>
    ///
    /// <para>A player with no membership gets <see cref="Unaffiliated"/>: there is no settlement whose pawns speak for
    /// them, so there is nothing to look up. That is the honest answer rather than a fallback to the local player's
    /// own home, which would let a stranger inherit another player's permissions.</para></summary>
    public SettlementGrant GrantsFor(int playerId, int settlement)
    {
        if (settlement == 0) return Unaffiliated;
        // Player 0 is "no player in particular" - the authority, and any read that means "what does a visitor get".
        // Answering from the defaults would then disagree with the gate, which asks `GrantsForPawn(0, …)` and does see
        // a `To = 0` settlement-level record. Measured before this line: `settlement-grant 1 0 Benches,Items` printed
        // 「该玩家此后的实际授权 = Default（15）」 while the door in settlement 1 was already refusing.
        if (playerId == 0) return GrantsForPawn(0, settlement);
        var asSettlement = SettlementOf(playerId);
        if (asSettlement != 0) return GrantsForPawn(asSettlement, settlement);
        // A player-address record is still honoured for they-alone checks that have no pawn (the UI printing "what may
        // player 5 do here"). It cannot be the authority's answer - the authority has no player - so it is consulted
        // only after the settlement projection.
        for (var i = 0; i < Grants.Count; i++)
        {
            var record = Grants[i];
            if (record is null || record.From != settlement || record.To != playerId || playerId == 0) continue;
            return (SettlementGrant)record.Flags;
        }
        return Defaults.TryGetValue(settlement, out var fallback) ? (SettlementGrant)fallback : SettlementGrant.Default;
    }

    /// <summary>The grants a <i>pawn</i> holds in a settlement, by the settlement it belongs to.
    ///
    /// <para>Uses the settlement-to-settlement grant rather than the player grant: on the authority there is no player,
    /// and a pawn's membership is what the gates know. A settlement that has granted nothing to another settlement
    /// therefore answers with <see cref="SettlementGrant.Default"/>, which is the visitor's survival four.</para>
    /// </summary>
    public SettlementGrant GrantsForPawn(int pawnHome, int settlement)
    {
        if (settlement == 0) return Unaffiliated;
        if (pawnHome == settlement) return SettlementGrant.All;
        // **`To` is a settlement id, and this now honours `To == pawnHome` as well as `To == 0`.**
        //
        // The old body read `Target == 0` only, and the reason given was that `To` had two meanings: the grant window and
        // `settlement-grant` wrote a *player* id while this method read a settlement id, so a grant to player 2 silently
        // became a grant to settlement 2 whenever the numbers coincided.
        //
        // Round 88 fixed the writer, not this reader: `ScriptedSettlement.Grant` now stores
        // `target = data.SettlementOf(player)` (`Source/ScriptedSettlement.cs`), i.e. a **settlement** id. So every record
        // a player configures today has `To` = the visitor's home settlement - and the `To != 0` filter threw all of them
        // away. Measured in round 132, with `settlement-grant 1 2 BuildArea` on a two-settlement map:
        // `据点 #1 → 据点 2（玩家 2） 授权 16384（BuildArea）；该玩家此后的实际授权 = Default（15）`, and the land gate
        // still answered `玩家 2：拒绝（归属=False，授权=…+Default）`. The permit-by-grant branch of every gate was
        // therefore unreachable for any visitor who owns a settlement - the feature looked configurable and did nothing,
        // which is exactly the shape of defect that reads as "the layer refuses everything" to a player.
        //
        // `To` now means one thing: the settlement whose members the grant is addressed to, with 0 meaning "all visitors".
        // A record addressed to a *player* would be the double meaning again, and nothing writes one today: the grant
        // window's default row writes 0, and `Grant` writes `SettlementOf(player)`. If a player-addressed form is ever
        // wanted, it needs a field of its own rather than a second interpretation of this one.
        for (var i = 0; i < Grants.Count; i++)
        {
            var record = Grants[i];
            if (record is null || record.From != settlement) continue;
            if (record.To != 0 && record.To != pawnHome) continue;
            return (SettlementGrant)record.Flags;
        }
        return Defaults.TryGetValue(settlement, out var fallback) ? (SettlementGrant)fallback : SettlementGrant.Default;
    }

    /// <summary>The settlement whose land a cell belongs to, or 0 for nobody's.
    ///
    /// <para>Deliberately computed from each settlement's own rectangle rather than from a per-cell table: a settlement
    /// is a rectangle on one map, and a table of 62 500 entries per map would be saved, sent and kept in step for a
    /// value that is one arithmetic test. If a later version needs concave borders, this is the one method that
    /// changes.</para>
    ///
    /// <para>When two rectangles overlap - which happens while a player is still laying out a new settlement inside an
    /// old one - the <b>smallest</b> containing rectangle wins. Without a rule the answer would depend on list order,
    /// so the same cell could belong to different settlements on two peers that added them in a different order, and the
    /// gates would disagree about who may build there.</para>
    ///
    /// <para><b>And the tie is broken by id, not by list order.</b> Smallest-area alone is not a rule for two settlements
    /// of the same size, which is not a corner case but the common one: a player who founds a settlement with the same
    /// dialog twice gets two identical squares. Measured with `Alpha|1|40|40|100,100;Beta|2|40|40|130,130`, whose rects
    /// are `(97,119)-(136,158)` and `(110,110)-(149,149)`: both are 1600 cells, so the overlap region `x 110..136,
    /// z 119..149` was awarded to whichever happened to be first in the list. The previous comparison was
    /// <c>area &gt;= bestArea</c>, which keeps the earlier one - so adding Beta before Alpha would have flipped every
    /// cell in the overlap, and two peers whose `Sites` lists were built in different orders would have disagreed about
    /// who may build there. Comparing <c>(area, id)</c> is order-independent because ids are assigned once and never
    /// reused (<see cref="SettlementSite.Retired"/> exists for exactly that reason).</para></summary>
    public int SettlementAt(int mapId, IntVec3 cell)
    {
        RecheckAbsentMaps();
        // The rule itself lives in `TurboTick.Simulation.SettlementOwnership`, where it can be unit-tested without a
        // running game. This method only projects the sites into the flat claims that function takes - building the
        // projection on every call would allocate, so a reusable buffer is refilled instead. `SettlementAt` runs thousands
        // of times a second from the reservation gate, and an `IntVec3` cannot cross into the simulation assembly.
        // An explicit exception wins over geometry, and is checked first because that is the whole point of having it:
        // a player kept this cell for a settlement the rectangles do not name. Zero overrides - the normal case - costs
        // one `Count` read.
        if (Overrides.Count > 0)
        {
            var kept = Overrides.OwnerAt(mapId, cell.x, cell.z);
            if (kept != 0) return kept;
        }
        if (Sites.Count == 0) return 0;
        if (claimBuffer.Length < Sites.Count) claimBuffer = new SettlementOwnership.Claim[Sites.Count];
        var count = 0;
        for (var i = 0; i < Sites.Count; i++)
        {
            var site = Sites[i];
            if (site is null || site.MapId != mapId || site.Retired) continue;
            claimBuffer[count++] = new SettlementOwnership.Claim(site.Id, site.MapId, site.MinX, site.MinZ, site.MaxX, site.MaxZ);
        }
        if (count == 0) return 0;
        return SettlementOwnership.Owner(claimBuffer, mapId, cell.x, cell.z, count);
    }

    private SettlementOwnership.Claim[] claimBuffer = [];

    /// <summary>Settlements whose `MapId` names a map this peer does not have, re-derived a few times a second.
    ///
    /// <para><b>Why this is a real hole and not a cosmetic one.</b> `SettlementAt` skips a site whose map is not the one
    /// being asked about (`site.MapId != mapId`), which is correct while both maps are present - but it is
    /// <b>indistinguishable from "no settlement here"</b> when the map is missing. On a replica that is the normal
    /// intermediate state: the settlement table and the map channel are separate, and the table can arrive first. During
    /// that window every cell of a settlement reads as unclaimed ground and every gate permits everything. Nothing throws,
    /// nothing is logged, and `SettlementGates.Summary` says `放行 N / 拒绝 0`, which reads exactly like a well-behaved
    /// colony.
    ///
    /// <para><b>Why a counter in the summary rather than a fix.</b> The gate cannot refuse on a map it cannot index - the
    /// cell numbers would have to be guessed. What it can do is stop being silent, so a permission that should have been
    /// denied is traceable to the missing map instead of looking like a grant. `settlement-report` already prints
    /// 「本端没有这张地图」 per site; this is the same fact where a refusal would be read from.</para>
    ///
    /// <para><b>Why it is throttled.</b> `SettlementAt` runs thousands of times a second from the reservation gate, so the
    /// scan cannot be per call. Once every 120 ticks is far more often than maps arrive and far cheaper than the
    /// projection the call already does.</para></summary>
    public int AbsentMapSites { get; private set; }

    private int absentMapCheckTick = -1000;

    /// <summary>Recomputes `AbsentMapSites` now, ignoring the tick throttle.
    ///
    /// <para>Needed by probes that rewrite a site's `MapId` and want to read the counter in the same command: the throttled
    /// path is 120 ticks wide, so a measurement taken inside a command would otherwise report the value from before the
    /// edit. Measured: `settlement-absent-map` printed `缺图据点数=0` after making a site point at nothing, which reads as
    /// "the counter does not work" rather than "the counter has not run yet".</para></summary>
    internal void RefreshAbsentMaps()
    {
        absentMapCheckTick = -1000;
        RecheckAbsentMaps();
    }

    private void RecheckAbsentMaps()
    {
        var tick = Find.TickManager?.TicksGame ?? 0;
        if (tick - absentMapCheckTick < 120 && tick >= absentMapCheckTick) return;
        absentMapCheckTick = tick;
        var absent = 0;
        try
        {
            var maps = Find.Maps;
            if (maps is null) return;
            foreach (var site in Sites)
            {
                if (site is null || site.Retired) continue;
                var found = false;
                foreach (var map in maps)
                    if (map is not null && map.uniqueID == site.MapId) { found = true; break; }
                if (!found) absent++;
            }
        }
        catch (Exception)
        {
            // A half-loaded game must not turn a readout into an exception: `SettlementAt` is on the hot path of every
            // reservation, and throwing here would refuse work for a reason nothing can report.
            return;
        }
        AbsentMapSites = absent;
    }

    /// <summary>Cells whose owner is not what the rectangles say.
    ///
    /// <para>The rectangles answer "which claim contains this cell", which is purely geometric: with two settlements,
    /// a patch of ground inside Alpha's rectangle cannot belong to Beta. A player who keeps ground for the second
    /// settlement, or whose neighbour expanded over an old boundary, needs exactly that, and CT carries the same thing
    /// as `KeptCells` (`_lab/ct045/ct045.decompiled.cs:369264`), consulted before its distance rule at `:369713`.</para>
    ///
    /// <para>Measured on this save: zero. The common path is therefore one `Count == 0` test.</para></summary>
    public SettlementOverrides Overrides { get; } = new();

    /// <summary>Flat `mapId, x, z, owner` quads used only while scribing. Null when there are no exceptions, which is
    /// the usual case and keeps the node out of the save entirely.</summary>
    private List<int>? overrideRows;

    public void ExposeData()
    {
        Scribe_Values.Look(ref DataSchema_, "settlementSchema", DataSchema);
        Scribe_Collections.Look(ref Sites, "sites", LookMode.Deep);
        // Triples rather than a dictionary: a cell exception is (map, x, z) -> owner, all four are ints, and a flat list
        // of quads is what survives a schema change without a custom `Scribe_Deep`. Empty on almost every save, and an
        // empty list costs one node.
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            overrideRows = [];
            foreach (var entry in Overrides.ForScribing())
            {
                overrideRows.Add(entry.MapId);
                overrideRows.Add(entry.X);
                overrideRows.Add(entry.Z);
                overrideRows.Add(entry.Owner);
            }
            if (overrideRows.Count == 0) overrideRows = null;
        }
        Scribe_Collections.Look(ref overrideRows, "keptCells", LookMode.Value);
        if (Scribe.mode == LoadSaveMode.LoadingVars && overrideRows is { Count: >= 4 })
        {
            var loaded = new List<SettlementOverrides.Entry>(overrideRows.Count / 4);
            for (var i = 0; i + 3 < overrideRows.Count; i += 4)
                loaded.Add(new SettlementOverrides.Entry(overrideRows[i], overrideRows[i + 1], overrideRows[i + 2], overrideRows[i + 3]));
            Overrides.Load(loaded);
            overrideRows = null;
        }
        Scribe_Collections.Look(ref Members, "members", LookMode.Value, LookMode.Value);
        Scribe_Collections.Look(ref PawnHome, "pawnHome", LookMode.Value, LookMode.Value);
        Scribe_Collections.Look(ref Defaults, "defaults", LookMode.Value, LookMode.Value);
        Scribe_Collections.Look(ref Grants, "grants", LookMode.Deep);
        // `PendingWars` was a placeholder list of strings that nothing read. It is still written so an older build opening a
        // newer save finds the node it expects; nothing produces new entries and the real pair records live in `Pacts`.
        Scribe_Collections.Look(ref PendingWars, "pendingWars", LookMode.Value);
        Scribe_Collections.Look(ref Pacts, "pacts", LookMode.Deep);
        // A save written by an older build has no nodes at all, and `Scribe_Collections.Look` assigns null in that
        // case. Every accessor here iterates the field, so leaving it null would turn "no settlements" into a
        // NullReferenceException on the first gate that ran.
        Sites ??= [];
        Members ??= [];
        PawnHome ??= [];
        Defaults ??= [];
        Grants ??= [];
        PendingWars ??= [];
        Pacts ??= [];
    }

    /// <summary>A private copy of the schema number so `Scribe_Values.Look` has something to write into. Reading a
    /// constant is not possible for a by-ref parameter.</summary>
    private int DataSchema_ = DataSchema;
}

/// <summary>One settlement: where it is, who owns it, and what it looks like on the map.
///
/// <para>Everything here is a value so the record can be sent to a replica as-is. The rectangle is part of the record
/// rather than derived from the buildings because a settlement's land exists before it has walls.</para></summary>
public sealed class SettlementSite : IExposable
{
    public int Id;
    public int MapId;
    /// <summary>The player who owns the settlement. 0 means nobody in particular - used for settlements the world
    /// generated, which the ownership layer treats as owned by everyone (see <see cref="SettlementData.GrantsFor"/>
    /// with a settlement of 0).</summary>
    public int OwnerPlayer;
    public string Name = "";
    public int MinX, MinZ, MaxX, MaxZ;
    /// <summary>Set when a settlement stops existing. Kept rather than removed so the id is never reused and an
    /// in-flight grant record can still name it without resolving to the wrong place.</summary>
    public bool Retired;
    public int FoundedTick;
    /// <summary>The settlement's faction as the engine sees it. A settlement with no faction is one the ownership
    /// layer governs by grants alone - which is the common case and needs no faction plumbing at all.</summary>
    public int FactionId = -1;

    /// <summary>Whether this settlement's rectangle covers the cell - <b>on this settlement's own map</b>.
    ///
    /// <para><b>The defect this closes.</b> The geometric test was `Contains(IntVec3)` with no map argument. Two
    /// settlements on different maps have unrelated coordinate spaces: a 60x60 second colony and a 250x250 home map both
    /// run 0..59 on x and z, so a cell at (30, 30) is inside <em>both</em> rectangles. Measured consequence:
    /// `settlement-claim 2 47` on map B would accept a pawn standing at (30, 30) on map A, because the only check was
    /// `site.Contains(pawn.PositionHeld)`. `SettlementAt` (`:215`) was already correct - it filters `site.MapId != mapId` -
    /// so the gate was right and the claim path was not, which is why the ownership layer could record a membership row
    /// pointing at a pawn who had never been near the settlement.</para>
    ///
    /// <para><b>Why the map argument is explicit rather than read from `Map.Index`.</b> `MapIndex` is
    /// <c>Map.uniqueID</c> (scribed, equal on both peers), not <c>Map.Index</c> (<c>Find.Maps.IndexOf(this)</c>, shifts
    /// whenever an earlier map is removed). Same choice as `SettlementAt` and `PlanSync`, for the same reason.</para>
    ///
    /// <para>The parameterless overload is kept for the one case where the caller has already established the map - the
    /// rectangle arithmetic itself - and is named so a reader cannot mistake it for a full containment test.</para></summary>
    public bool Contains(int mapId, IntVec3 cell) =>
        MapId == mapId && cell.x >= MinX && cell.x <= MaxX && cell.z >= MinZ && cell.z <= MaxZ;

    /// <summary>Rectangle arithmetic only. Use <see cref="Contains(int, IntVec3)"/> for a real containment test.</summary>
    public bool RectContains(IntVec3 cell) =>
        cell.x >= MinX && cell.x <= MaxX && cell.z >= MinZ && cell.z <= MaxZ;

    public IntVec3 Centre => new((MinX + MaxX) / 2, 0, (MinZ + MaxZ) / 2);

    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id", 0);
        Scribe_Values.Look(ref MapId, "map", 0);
        Scribe_Values.Look(ref OwnerPlayer, "owner", 0);
        Scribe_Values.Look(ref Name, "name", "");
        Scribe_Values.Look(ref MinX, "minX", 0);
        Scribe_Values.Look(ref MinZ, "minZ", 0);
        Scribe_Values.Look(ref MaxX, "maxX", 0);
        Scribe_Values.Look(ref MaxZ, "maxZ", 0);
        Scribe_Values.Look(ref Retired, "retired", false);
        Scribe_Values.Look(ref FoundedTick, "foundedTick", 0);
        Scribe_Values.Look(ref FactionId, "faction", -1);
        Name ??= "";
    }
}

/// <summary>One point grant: "<paramref name="To"/> may do <paramref name="Flags"/> in settlement
/// <paramref name="From"/>".</summary>
public sealed class SettlementGrantRecord : IExposable
{
    public int From;
    public int To;
    public int Flags;

    public void ExposeData()
    {
        Scribe_Values.Look(ref From, "from", 0);
        Scribe_Values.Look(ref To, "to", 0);
        Scribe_Values.Look(ref Flags, "flags", 0);
    }
}

/// <summary>What somebody is allowed to do in a settlement, as flags.
///
/// <para>Copied value-for-value from ColonyTogether's <c>ColonyGrant</c> (ct045.decompiled.cs:369140) so the two mods
/// describe the same world the same way: a player who has used one can read the other's settings, and the numbers do
/// not need translation. The names are the actions a player recognises, not the engine calls behind them.</para></summary>
