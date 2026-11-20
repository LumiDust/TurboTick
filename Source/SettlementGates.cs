using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>The gates that make the ownership layer real: the three places the engine asks "may this pawn do this".
///
/// <para><b>Why three gates and not four hundred patches.</b> A settlement's rules are about what a <i>pawn</i> may do,
/// and every pawn action is decided at one of three moments: may I reserve that (<c>ReservationManager.CanReserve</c>),
/// does my work giver see work there (<c>WorkGiver_Scanner.HasJobOnThing</c>), and may I place this designator
/// (<c>Designator.CanDesignateCell</c> / <c>CanDesignateThing</c>). ColonyTogether's 405 settlement-mode patch points
/// are mostly those same three questions asked once per action type; asking them once at the chokepoints covers the
/// same ground and cannot go stale when a new designator is added.</para>
///
/// <para><b>Every gate returns true first when the layer is off.</b> <see cref="SettlementData.Active"/> is
/// <c>Sites.Count &gt; 0</c>, so a save that never declared a settlement runs exactly the code it ran before this file
/// existed. That single line is what makes the feature additive rather than a behaviour change with an off switch.</para>
///
/// <para><b>Why refusals are counted, not logged per call.</b> A gate is evaluated hundreds of times per second per
/// pawn - <c>HasJobOnThing</c> runs for every candidate target of every work giver. One log line per refusal is the
/// per-frame storm this project has already been through twice. Counters let the status line report the total, and a
/// one-time line per (act, kind) pair names each distinct refusal.</para>
internal static class SettlementGates
{
    internal static long ReservationsRefused;
    internal static long WorkRefused;
    internal static long DesignationsRefused;
    /// <summary>Doors a pawn was not allowed to open. Its own counter because the door gate is asked by the pathfinder
    /// rather than by a job: a refusal here does not appear as a failed job, it appears as a route that goes around.</summary>
    internal static long PassRefused;
    /// <summary>Recreation jobs dropped because the parcel of land belongs to somebody who did not grant `Amenities`.
    /// Its own counter for the same reason as the door: a dropped joy job never surfaces as a failed job - the pawn
    /// simply picks a different way to spend the hour.</summary>
    internal static long RelaxRefused;
    /// <summary>Trades refused before the window opened. Its own counter because a refused trade is a
    /// `TradeSession` that never started: nothing is logged, no job fails, the window simply does not appear.</summary>
    internal static long TradeRefused;
    internal static long Allowed;

    /// <summary>Pairs already reported, so the log names each kind of refusal once rather than once per call.</summary>
    private static readonly HashSet<(SettlementAct, SettlementKind)> reported = [];

    /// <summary>Set while a probe is calling the gates to read their answers, so its own questions are not counted.
    ///
    /// <para><b>Why this has to exist (round 95).</b> `settlement-probe` asks `Can` twelve times per sampled object to
    /// print twelve act rows, and `settlement-counters` reported that as real traffic: measured
    /// 「放行 370 / … 门 1 … 战争 62」 with 「按动作：Use/Plant=4 Take/Plant=4 …」 - the 12×4 table was the probe's own
    /// 48 questions about one plant, and the 370 allows were its samples. A probe that is measured by the counters it is
    /// supposed to validate is worse than no probe: the numbers look large and meaningful while containing nothing about
    /// the game. The counters now answer "what did real pawns get refused", which is the question they were added for.
    /// </para>
    ///
    /// <para>`IDisposable` rather than a bare set/reset pair so an exception in a probe cannot leave the scope open and
    /// silence the counters forever - which would restore exactly the blindness this fixes.</para></summary>
    internal static bool Counting { get; private set; } = true;

    internal readonly struct ProbeScope : IDisposable
    {
        public void Dispose() => Counting = true;
    }

    /// <summary>Runs gate reads without counting them. Usage: <c>using (SettlementGates.Uncounted()) { … }</c>.</summary>
    internal static ProbeScope Uncounted()
    {
        Counting = false;
        return default;
    }

    /// <summary>Takes one gate decision into the counters, unless a probe is reading them.
    ///
    /// <para>One entry point rather than a dozen `X++` sites, because the guard has to be impossible to forget: the probe
    /// asks the gate twelve times per sampled thing, so a counter that skips the check reports the probe's own questions
    /// as gameplay. Named arguments rather than positional so a call site reads as the counter it means -
    /// <c>Count(pass: 1)</c> cannot be mistaken for the allow counter, which the old <c>PassRefused++</c> /
    /// <c>Allowed++</c> pair could when they sat on adjacent lines.</para></summary>
    internal static void Count(long allowed = 0, long reservations = 0, long work = 0,
        long designations = 0, long pass = 0, long relax = 0, long trade = 0, long war = 0)
    {
        if (!Counting) return;
        Allowed += allowed;
        ReservationsRefused += reservations;
        WorkRefused += work;
        DesignationsRefused += designations;
        PassRefused += pass;
        RelaxRefused += relax;
        TradeRefused += trade;
        WarRefused += war;
    }

    /// <summary>The player this process acts for. 0 means nobody in particular, which
    /// <see cref="SettlementData.GrantsFor"/> answers with <see cref="SettlementData.Unaffiliated"/>.
    ///
    /// <para>Read from the room connection rather than from a faction: the point is that the <i>human</i> is the actor,
    /// and a shared colony has one faction for everybody. A single-player game has no room, no player id, and - since
    /// nothing declared a settlement - no ownership layer either.</para></summary>
    internal static int LocalPlayer
    {
        get
        {
            try { return (int)RoomConnection.LocalPeerId; }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>The colonist the local player is acting through: the selected pawn when one is selected, otherwise the
    /// first spawned colonist. Used only to compose refusal text, so a wrong answer produces a slightly wrong sentence and
    /// never a wrong decision - which is why it returns null rather than guessing a settlement when there is no pawn.
    ///
    /// <para>Exists because <see cref="ExplainFor"/> was writing 「需要「通行」授权」 for refusals that had nothing to do
    /// with any grant (round 94): `Can` refuses both on the table and on war, and a message naming the table sends the
    /// player looking for a checkbox that is already ticked.</para></summary>
    internal static Pawn? LocalPlayerPawn()
    {
        try
        {
            // **The selection first, then every map - not `Find.CurrentMap`.**
            //
            // The old version scanned only the current map, which is right while a colony has one settlement and wrong
            // the moment it has two: `Find.CurrentMap` is whatever the client last looked at, and a dedicated server or a
            // peer sitting on the world map has a current map that is not where the player's pawn stands. The fallback
            // then returned null, `LocalPlayer` fell back to a fixed id, and every gate decided for the wrong player.
            //
            // `SingleSelectedThing` is checked first because it is the only signal that names a pawn the user actually
            // picked; the scan is a heuristic for the case where nothing is selected, and under several maps it should
            // see the whole colony rather than one map's slice of it. Ordering by `Find.Maps` keeps the answer stable
            // between calls, so a counter that ticks twice cannot come from two different pawns.
            var selected = Find.Selector?.SingleSelectedThing as Pawn;
            if (selected is { IsColonist: true } && selected.Spawned) return selected;
            var maps = Find.Maps;
            if (maps is null) return null;
            foreach (var map in maps)
            {
                if (map?.mapPawns is null) continue;
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    if (pawn is { IsColonist: true }) return pawn;
            }
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The settlement data for the running game, or null before one exists.</summary>
    internal static SettlementData? Data
    {
        get
        {
            try { return Current.Game?.GetComponent<TurboGameComponent>()?.Settlements; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Whether a pawn may act on a thing.
    ///
    /// <para>A pawn that is not the player's is not governed: the layer decides what <i>players</i> may do in each
    /// other's settlements, and applying it to raiders would stop them attacking - which is not a permission
    /// question.</para>
    ///
    /// <para><b>The actor is the pawn, not the local player.</b> The authority runs the AI for every player's pawns and
    /// has no local player at all, so a gate that asked "what may the local player do" would classify every pawn on the
    /// server as an unaffiliated visitor - including the owner's own colonists, refused permission to build in their own
    /// base. The question asked instead is "does this pawn belong to the settlement whose land it is on", which is
    /// answerable on any peer, for any pawn, without knowing who is looking.</para></summary>
    internal static bool Can(Pawn? pawn, SettlementAct act, Thing? target)
    {
        if (pawn is null) return true;
        if (pawn.Faction != Faction.OfPlayer && pawn.Faction?.IsPlayer != true) return true;
        var data = Data;
        if (data is null || !data.Active) return true;
        var kind = SettlementRule.KindOf(target, act);
        // A kind the table does not know is not a permission decision: the layer has nothing to say about a mote or a
        // projectile, and refusing those would stop combat animation.
        if (kind == SettlementKind.None) return true;
        // Anywhere outside every settlement is nobody's business, including one's own.
        var home = SettlementRule.HomeOf(data, target);
        if (home == 0) return true;
        // Whose pawn this is decides whose side of the rule applies.
        //
        // **The table is the only source, and that is a limitation, not an oversight (round 93).** Membership is recorded
        // when a settlement is founded (`ScriptedSettlement.Place`), so a colonist born, recruited or arrived afterwards
        // has no entry and is treated as a visitor on its own land. Two candidate fallbacks were tried and both were
        // **wrong in the dangerous direction**:
        //   - "the land it stands on": on a shared colony every player's pawns are the same faction, so a stranger
        //     standing on a neighbour's ground would become the owner - destroying the one case this layer exists for.
        //   - "the faction the settlement records": same collapse, one step later. ColonyTogether can key on faction
        //     (`ColonyFactions.ColonyOf`, `ct045:83138`) only because a colony there *is* a faction.
        // So the gap is not guessed at; it is made visible (`SettlementRule.UnrecordedColonists`) and fixed explicitly
        // (`settlement-claim`). A wrong fallback here reads as "the ownership layer does not work" and is much harder to
        // diagnose than a counted number in the report.
        var mine = data.HomeOfPawn(pawn.thingIDNumber);
        if (mine != 0 && home == mine) { Count(allowed: 1); return true; }
        // War overrides the table entirely. This is the one place it has to be checked: every gate asks "what grants does
        // this pawn hold here", so answering "none" for an enemy makes all of them refuse without a second condition
        // threaded through each. A gate that forgot the check would let an enemy build a wall while the others refused.
        if (SettlementWar.AtWar(data, mine, home)) { Count(war: 1); return false; }
        // A pawn that belongs to no settlement and stands on somebody's land is a visitor: it gets the unaffiliated
        // grants. This is the case the layer exists for.
        //
        // **`GrantsForPawn(0, home)`, not the `Unaffiliated` constant (round 86).** The two were written as if they said
        // the same thing, and they do not: `GrantsForPawn` first looks for a record whose `To` is this home (so
        // `settlement-grant 1 0 Doors,Care` can speak for *all visitors*), then falls back to the settlement's own
        // `Defaults`, and only then to `SettlementGrant.Default`. Substituting the constant skipped both tables, which
        // made them **dead for exactly the case they exist for**: a settlement could set its default to "no doors" in
        // the grant window, the value would be scribed, sent over the wire, printed by the report - and every visitor
        // would still be handed the permissive four. Measured: `settlement-grant 1 0 Care,Benches,Items`（Doors removed）
        // and the door still answered 「访客开门=可(表=准)」.
        var grants = data.GrantsForPawn(mine, home);
        if (Permitted(grants, act, target, kind)) { Count(allowed: 1); return true; }
        Note(act, kind);
        return false;
    }

    /// <summary>Whether the local player may act on a thing. Used by the designator gate, where the actor really is the
    /// human at the keyboard rather than a pawn the engine is driving.</summary>
    internal static bool Can(int player, SettlementAct act, Thing? target)
    {
        var data = Data;
        if (data is null || !data.Active) return true;
        var kind = SettlementRule.KindOf(target, act);
        if (kind == SettlementKind.None) return true;
        var home = SettlementRule.HomeOf(data, target);
        if (home == 0) return true;
        if (data.Owns(player, home)) { Count(allowed: 1); return true; }
        if (Permitted(data.GrantsFor(player, home), act, target, kind)) { Count(allowed: 1); return true; }
        Note(act, kind);
        return false;
    }

    /// <summary>The current-map form of <see cref="CanCell(int, SettlementAct, Map, IntVec3)"/>. Designators mean the
    /// map being drawn, so this is what they call; anything that names a map - a probe, a future cross-map caller - uses
    /// the map overload directly.</summary>
    internal static bool CanCell(int player, SettlementAct act, IntVec3 cell)
        => CanCell(player, act, Find.CurrentMap, cell);

    /// <summary>Whether a pawn may act on a cell, on **the map the pawn is on**.
    ///
    /// <para><b>The defect this signature exists to fix (round 82).</b> The pawn overload used to call the player
    /// overload, which reads <c>Find.CurrentMap</c>. But a pawn's cell is only meaningful on the pawn's own map, and on a
    /// multi-map colony those differ the moment the AI paths a colonist in the second settlement: the gate then asked
    /// the land table about a cell number on the wrong map. With `250x250` and `80x80` maps that is two different
    /// answers for the same three numbers - usually a refusal of ground nobody claims, which is the harmless direction,
    /// but equally often a **permit on land somebody does own**, because the same coordinate on the smaller map can sit
    /// inside a settlement there.</para>
    ///
    /// <para>The thing overload never had this problem: <c>SettlementRule.HomeOf</c> uses <c>thing.MapHeld</c>. This is
    /// the cell-side counterpart, and it is a separate method rather than a map argument on the player overload because
    /// the player overload's callers (`Designator` validation) genuinely mean the map being drawn.</para>
    ///
    /// <para>The act is a parameter because the same cell means different things: placing a zone is <c>Place</c> and
    /// building on it is <c>Build</c>. Ownership begins at a settlement's rectangle - unclaimed ground has nothing to
    /// check, and gating it would stop every player building anywhere.</para>
    /// </summary>
    internal static bool CanCell(Pawn? pawn, SettlementAct act, IntVec3 cell)
    {
        if (pawn is not null && pawn.Faction != Faction.OfPlayer && pawn.Faction?.IsPlayer != true) return true;
        var data = Data;
        if (data is null || !data.Active) return true;
        // `MapHeld`, not `Map`: a pawn in a caravan or a gravship under way has no `Map` at all, and `MapHeld` is what
        // every other gate in this file uses for the same reason (`SettlementRule.HomeOf` is `thing.MapHeld`).
        var map = pawn?.MapHeld ?? Find.CurrentMap;
        if (map is null) return true;
        var home = data.SettlementAt(map.uniqueID, cell);
        if (home == 0) return true;
        // A `Store` question about a cell is a question about a stockpile cell, so the kind comes from what is on the
        // ground; every other act on a cell is land work (`Place`/`Build`). See `SettlementRule.KindOfCell`.
        var cellKind = act == SettlementAct.Store ? SettlementRule.KindOfCell(map, cell) : SettlementKind.Land;
        // **The pawn overload must be keyed by the pawn (round 139).** It used `LocalPlayer` - the human at the keyboard -
        // which on the authority is whoever the server considers local, so `Owns(player, home)` short-circuited to
        // "permitted" for *every* cell a visitor reserved in the owner's settlement. Measured on the container: a visitor
        // holding only `BuildArea` answered `CanReserve=⌈放行⌉` for a stockpile cell it had no `Storage` grant for, and
        // the answer did not change when the grant was `Storage` either - the grant table was never consulted, because
        // the owner's `Owns` short-circuit fired first. The thing overload has always been pawn-keyed (`HomeOfPawn`);
        // this one now mirrors it, and only the player-keyed overload keeps `LocalPlayer`.
        if (pawn is not null)
        {
            var mine = data.HomeOfPawn(pawn.thingIDNumber);
            if (mine != 0 && home == mine) { Count(allowed: 1); return true; }
            if (SettlementWar.AtWar(data, mine, home)) { Count(war: 1); return false; }
            if (SettlementRuleTable.Permits(data.GrantsForPawn(mine, home), act, cellKind))
            { Count(allowed: 1); return true; }
            Note(act, cellKind);
            return false;
        }
        var player = LocalPlayer;
        // **`Owns`, not `SettlementOf`.** A player who owns two settlements keeps only the last one in `Members`, so the
        // single-entry comparison refused a player's permission on their *own* second settlement. Measured before this:
        // two sites owned by player 1, `本端玩家 1（属于 2）` - standing in settlement #1 read `home != mine` and fell
        // through to the grant table, which had no row for the player, so `Default` decided whether they could build in
        // their own base.
        if (data.Owns(player, home)) { Count(allowed: 1); return true; }
        if (SettlementRuleTable.Permits(data.GrantsFor(player, home), act, cellKind)) { Count(allowed: 1); return true; }
        Note(act, cellKind);
        return false;
    }

    /// <summary>Whether a player may act on a cell, using `Find.CurrentMap`.
    ///
    /// <para><b>Why `Find.CurrentMap` is correct here and nowhere else.</b> Both callers of this overload are the
    /// designator path, and `Designator.Map` is literally `Find.CurrentMap` (`:162502`), so the cell being drawn is
    /// always a cell on that map. Checked while looking for a cross-map defect: the reservation gate does *not* use this
    /// overload — it uses the `Pawn` one, which resolves `pawn.MapHeld`, and a pawn cannot haul across maps. So there is
    /// no caller left whose cell belongs to a map other than the one this reads.</para></summary>
    /// <summary>Whether a player may act on a cell **on the map named**, not on whatever map is being drawn.
    ///
    /// <para><b>Why this overload exists now (round 132).</b> The ownership rules have been measured on their allow side
    /// for twenty rounds and on their deny side only through a visitor pawn - never for "player 2 on player 1's land",
    /// because the only player-keyed cell gate reads <c>Find.CurrentMap</c> and a probe has no way to say which map it
    /// is asking about. Without that answer the deny path is untested, and an untested refuse path looks exactly like a
    /// working permit path: both produce "the pawn did something".
    ///
    /// <para>Round 116 nearly shipped this shape with **zero call sites** and it was reverted on purpose; the difference
    /// now is that the caller is real - <c>ScriptedSettlement.GateProbe</c> asks about a named map so a two-map colony
    /// can be measured. If that caller ever goes away, this overload goes with it.</para></summary>
    internal static bool CanCell(int player, SettlementAct act, Map? map, IntVec3 cell)
    {
        var data = Data;
        if (data is null || !data.Active) return true;
        if (map is null) return true;
        var home = data.SettlementAt(map.uniqueID, cell);
        if (home == 0) return true;
        var cellKind = act == SettlementAct.Store ? SettlementRule.KindOfCell(map, cell) : SettlementKind.Land;
        if (data.Owns(player, home)) { Count(allowed: 1); return true; }
        if (SettlementRuleTable.Permits(data.GrantsFor(player, home), act, cellKind)) { Count(allowed: 1); return true; }
        Note(act, cellKind);
        return false;
    }

    /// <summary>The core decision, shared by every gate so they cannot drift apart.</summary>
    private static bool Permitted(SettlementGrant grants, SettlementAct act, Thing? target, SettlementKind kind)
    {
        if (SettlementRuleTable.Permits(grants, act, kind)) return true;
        // Removing or repairing one's own damaged property is not taking somebody's building. A roof the colony built
        // stands on somebody else's land only when settlements overlap, and refusing the repair would leave a ruin
        // nobody can fix. ColonyTogether carves out the same case with `ColonyBuildingTools.Removing` (ct045:370291);
        // here it is one explicit exception rather than a separate rule row, because it is not a grant but a fact about
        // who built the thing.
        if (act is SettlementAct.Repair or SettlementAct.TakeApart or SettlementAct.Uninstall
            && target is not null && BuiltByColony(target))
            return true;
        // Reserving to *use* something is how the engine asks about almost everything, including taking. A target that
        // is food or supply is still permitted when the player holds the narrower `Take` grant, so the reservation gate
        // does not refuse an action the take-path would allow.
        if (act == SettlementAct.Use && kind is SettlementKind.Food or SettlementKind.Supply or SettlementKind.Equipment)
            return SettlementRuleTable.Permits(grants, SettlementAct.Take, kind);
        // **Reserving is asked as `Use`, and some kinds have no `Use` row at all (round 136).** `CanReserve` is the
        // engine's generic "I am about to touch this" - its caller is any work giver, so the act is not knowable there.
        // Filth, Fire, Plant, WildAnimal and Animal are stated in the table only as `Clean`, `PutOut`, `Gather`, `Hunt`
        // and `Handle`, so `Use` has no row for them: a visitor granted `Cleaning` was refused the *reservation*, and
        // `WorkGiver_CleanFilth.HasJobOnThing` checks `CanReserve` (`:311032`) before it checks anything else. Measured:
        // a visitor holding `All` (1048575) still answered `工作闸门放行 0 个 … 访客独缺：CleanFilth` while the owner
        // permitted it - the work gate would have allowed `Clean`, so the grant was being killed one gate earlier and the
        // player-visible result is "my cleaning permission does nothing".
        // Narrow on purpose: only for a kind the table states no `Use` row for, and only when the visitor holds *some*
        // grant that applies to that kind. A bench or a bed keeps its own `Use` row and is unaffected.
        if (act == SettlementAct.Use && !SettlementRuleTable.HasRow(SettlementAct.Use, kind)
            && SettlementRuleTable.PermitsAnyOn(grants, kind))
            return true;
        return false;
    }

    /// <summary>Whether a building was built by the colony rather than found there. A natural rock or a
    /// world-generated ruin is not the colony's, so taking it apart in somebody's settlement still needs their grant.
    ///
    /// <para><b>Must be a construct.</b> The first version of this only asked "is it the player's faction and not
    /// mineable", which is true of every colony <i>animal</i> - so a visitor could deconstruct somebody's muffalo. The
    /// probe caught it: <c>Animal #3849 TakeApart=准 Repair=准</c>. Requiring a <c>Building</c> (or a frame, which is a
    /// building under construction) is what makes the exception mean "the colony put this here".</para></summary>
    private static bool BuiltByColony(Thing thing) =>
        thing is Building or Frame
        && thing.Faction == Faction.OfPlayer
        && !(thing.def?.mineable ?? false);

    /// <summary>The text a designator shows on the cursor when it refuses.
    ///
    /// <para>Returns a sentence rather than a translation key because the sentence is composed at runtime from the
    /// settlement's own name and the specific permission missing - and the name is player-editable, so it cannot be baked
    /// into a keyed string. The wording is deliberately two clauses: where, then what.</para>
    ///
    /// <para>Never returns an empty string on a refusal: an empty <c>AcceptanceReport</c> message renders as a bare red
    /// cursor with no reason, which is the same dead end as silence.</para></summary>
    internal static string ExplainFor(SettlementAct act, object target)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null) return "此处需要据点授权。";
            var settlement = target switch
            {
                Thing thing => SettlementRule.HomeOf(data, thing),
                IntVec3 cell => data.SettlementAt(Find.CurrentMap?.uniqueID ?? 0, cell),
                _ => 0,
            };
            // A thing target has a kind, so the message can name the exact permission missing. A cell target does not -
            // there is no flag that would let a visitor dig somebody's rock - so it names the act instead.
            var required = target is Thing targetThing
                ? SettlementRuleTable.Required(act, SettlementRule.KindOf(targetThing, act))
                : SettlementGrant.None;
            var name = data.Site(settlement)?.Name is { Length: > 0 } given ? given : $"#{settlement}";
            // **War is not a missing permission (round 94).** `Can` refuses on two unrelated grounds - the grant table says
            // no, or these two settlements are at war - and both printed 「需要「通行」授权」. A player who owned both
            // settlements (or had granted everything) would go looking for a checkbox already ticked, and no reading
            // could tell which path refused: measured with #1 at war with #3, the door line printed
            // `访客开门=不可(表=拒)` where the table was permissive and the war was what refused.
            //
            // The actor here is the local player: this method builds the cursor text for a designator, which is the
            // local player's own hand. `Can` is asked with the same pawn, so text and decision cannot disagree.
            var actor = LocalPlayerPawn();
            if (actor is not null && SettlementWar.AtWar(data, data.HomeOfPawn(actor.thingIDNumber), settlement))
                return $"此处属于据点「{name}」，而你所属的据点正与它交战——不会放行任何动作，与授权无关。";
            return required == SettlementGrant.None
                ? $"此处属于据点「{name}」，你不能{ActLabel(act)}。"
                : $"此处属于据点「{name}」，需要「{PermissionLabel(required)}」授权。";
        }
        catch (Exception)
        {
            // A refusal must never become an exception on a cursor-move path: that would spam the log every frame the
            // cursor is over a forbidden cell.
            return "此处需要据点授权。";
        }
    }

    private static string ActLabel(SettlementAct act) => act switch
    {
        SettlementAct.Take => "取走",
        SettlementAct.Store => "存入",
        SettlementAct.Build => "建造",
        SettlementAct.TakeApart => "拆除",
        SettlementAct.Repair => "维修",
        SettlementAct.Gather => "采集",
        SettlementAct.Clean => "清洁",
        _ => "使用",
    };

    private static void Note(SettlementAct act, SettlementKind kind)
    {
        // **Nothing here runs for a probe's question (round 95).** The guard has to cover the log as well as the counts.
        // Measured: 118 「据点权限拒绝」 lines, and every one of them was `settlement-probe` enumerating one sample per
        // kind and asking `Can` about it - `Use/Plant`, `Tend/Animal`, `Use/Filth` and so on. No pawn had ever tried any
        // of those. The line is the only evidence that a gate is refusing in the real game, so a version that fires for
        // the probe's own questions is worse than no line at all: searching the log for a refusal found a hundred and
        // eighteen of them, describing nothing, and the grep that should answer "is the door closed in play" answered
        // "yes" on a map where nobody had walked anywhere.
        if (!Counting) return;
        // Counted per (act, kind) in the table, and logged only once per pair. The two serve different questions: the log
        // answers "has this code path ever run" and must not spam, while the count answers "which permission is actually
        // missing" and must not collapse - a single total of refusals cannot tell a closed door from blocked work.
        byAct[(act, kind)] = byAct.GetValueOrDefault((act, kind)) + 1;
        if (!reported.Add((act, kind))) return;
        var required = SettlementRuleTable.Required(act, kind);
        Log.Message($"[TurboTick] 据点权限拒绝 {act}/{kind}：需要授权 {required}（后续同类不再逐条记录）");
    }

    /// <summary>Refusal counts keyed by the pair that was refused. Never cleared by <see cref="Note"/>'s throttle, and
    /// reset only by <see cref="ResetCounters"/>.</summary>
    private static readonly Dictionary<(SettlementAct Act, SettlementKind Kind), int> byAct = [];

    /// <summary>Tells the player, at the moment they tried, why the thing they just did was refused.
    ///
    /// <para><b>Why a message and not just a log line.</b> A gate that returns false produces silence. The designator
    /// does nothing, the pawn does not move, and nothing on screen changes - so the player concludes the tool is broken,
    /// not that a rule exists. ColonyTogether reaches the same conclusion from the other direction: its refusal path
    /// builds a <c>PlayerTextException</c> carrying <c>CT.Refuse.NotAllowed</c> and the name of the missing permission
    /// (ct045.decompiled.cs:324915), so the player reads "you do not have this permission" rather than watching a keystroke
    /// disappear.</para>
    ///
    /// <para><b>Why only the designator path speaks up.</b> The AI asks the same questions hundreds of times a second, so
    /// a message per refusal would bury the screen. A designator is a human command: one message per click is the right
    /// volume, and the throttling below keeps a drag over fifty cells to one message.</para>
    ///
    /// <para><b>Why the message names the settlement and the permission.</b> "Not allowed" leaves the player guessing which
    /// of the two things to change - where they are standing, or who granted them what. Naming both turns a dead end into
    /// an instruction.</para></summary>
    internal static void Explain(SettlementAct act, SettlementKind kind, int settlement, SettlementGrant required)
    {
        try
        {
            // One message per (settlement, act, kind) per few seconds: a drag across a boundary produces a refusal per
            // cell, and the same sentence fifty times is noise rather than information.
            var key = (settlement, (int)act, (int)kind);
            var now = Find.TickManager?.TicksGame ?? 0;
            if (announced.TryGetValue(key, out var last) && now - last < 260) return;
            announced[key] = now;
            var site = SettlementGates.Data?.Site(settlement);
            var name = site?.Name is { Length: > 0 } given ? given : $"#{settlement}";
            Messages.Message($"此处属于据点「{name}」，你没有「{PermissionLabel(required)}」授权。",
                MessageTypeDefOf.RejectInput, historical: false);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 据点权限提示失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Throttling state for <see cref="Explain"/>, keyed by (settlement, act, kind).</summary>
    private static readonly Dictionary<(int, int, int), int> announced = [];

    /// <summary>The permission name the player is missing, in their language rather than the flag name.
    ///
    /// <para>Composed from the flags so a refusal that needs several permissions names all of them - a construction refusal
    /// usually needs both <c>Construction</c> and <c>Storage</c>, and naming only the first sends the player back to the
    /// owner twice.</para></summary>
    private static string PermissionLabel(SettlementGrant required)
    {
        var parts = new List<string>(4);
        foreach (var (flag, label) in new[]
                 {
                     (SettlementGrant.Doors, "通行"), (SettlementGrant.Care, "使用床位"),
                     (SettlementGrant.Benches, "使用工作台"), (SettlementGrant.Items, "取用物品"),
                     (SettlementGrant.Equipment, "取用装备"), (SettlementGrant.Food, "取用食物"),
                     (SettlementGrant.Storage, "存入仓库"), (SettlementGrant.Construction, "建造与拆除"),
                     (SettlementGrant.Repairs, "维修"), (SettlementGrant.Cleaning, "清洁"),
                     (SettlementGrant.Alerts, "查看警报"),
                 })
            if (required.HasFlag(flag)) parts.Add(label);
        return parts.Count == 0 ? required.ToString() : string.Join("、", parts);
    }

    /// <summary>Refusals caused by a war rather than by a missing grant. Counted apart because the two need different
    /// fixes: a missing grant is an edit, a war is a decision.
    ///
    /// <para>A plain field like its siblings rather than a get-only property. It was a property with a private setter,
    /// which is why <see cref="Count"/> could not write it and the compiler pointed at the sum instead of the concept -
    /// seven counters that look identical should be declared identically.</para></summary>
    internal static long WarRefused;

    internal static string Summary()
    {
        var text = $"据点权限 放行 {Allowed} / 拒绝 预订 {ReservationsRefused} 工作 {WorkRefused} 规划 {DesignationsRefused}"
            + $" 门 {PassRefused} 娱乐 {RelaxRefused} 交易 {TradeRefused} 战争 {WarRefused}，{SettlementWar.Summary(Data)}";
        // **The blind spot, named where it would be read.** With a settlement's map absent, `SettlementAt` answers 0 for
        // every cell of its land, so the gate permits everything and the line above reads `放行 N / 拒绝 0` - which is
        // also what a correctly-permissioned colony looks like. Printing the count makes the difference visible.
        var absent = Data?.AbsentMapSites ?? 0;
        return absent == 0 ? text : text + $"；**本端缺 {absent} 个据点的地图（这些地现在被当作无主，闸门全放行）**";
    }

    /// <summary>Same counters, split by act, so a reading can say *which* permission was missing rather than only how many
    /// refusals happened. A total of "7 refusals" cannot distinguish "the door is closed" from "work is blocked", and those
    /// need opposite fixes.
    ///
    /// <para>The per-act table is not maintained by hand: <see cref="Note"/> already records the act and kind of every
    /// refusal for the throttled chat message, so this reads that record instead of introducing a second set of counters
    /// that could drift from the first.</para></summary>
    internal static string Breakdown()
    {
        if (byAct.Count == 0) return "按动作：无拒绝";
        var parts = new List<string>(byAct.Count);
        foreach (var pair in byAct.OrderByDescending(each => each.Value).Take(12))
            parts.Add($"{pair.Key.Act}/{pair.Key.Kind}={pair.Value}");
        return "按动作：" + string.Join(" ", parts);
    }
    internal static void ResetCounters()
    {
        ReservationsRefused = 0;
        WorkRefused = 0;
        DesignationsRefused = 0;
        PassRefused = 0;
        RelaxRefused = 0;
        TradeRefused = 0;
        WarRefused = 0;
        Allowed = 0;
        reported.Clear();
        byAct.Clear();
    }

    /// <summary>Which act a work giver's own verb is, for the candidate-list gate.
    ///
    /// <para>See `SettlementWorkGate`'s remarks for why this exists: asking `Use` for every work giver made the table
    /// refuse cleaning, firefighting, hunting, gathering and animal handling outright, because `Use` has no row for those
    /// kinds and an unlisted pair is a refusal. The set below is closed on purpose - a work giver not listed keeps `Use`,
    /// which is correct for bills, beds, benches and seats and is what the table was written around.</para>
    ///
    /// <para>`Trend`/`Operate` are included for the medical and surgical work givers because the table has rows for them
    /// (`Tend` on `Pawn`, `Operate` on `Pawn`) and asking `Use` would refuse a doctor treating a visitor - which the
    /// visitor's `Care` grant is supposed to allow. `WorkGiver_TendOther` derives from `WorkGiver_Tend`, so both names are
    /// matched by the base check.</para></summary>
    /// <summary>Which act a <i>cell</i> work giver's verb is (round 155).
    ///
    /// <para>`WorkGiver_Scanner.HasJobOnCell` has twelve overrides in 1.7 (`BuildRoof`, `ConstructAffectFloor`,
    /// `ConstructRemoveFloor`, `ConstructRemoveFoundation`, `ConstructSmoothWall`, `RemoveRoof`, `ClearPollution`,
    /// `ClearSnowOrSand`, `Fish`, `GrowerHarvest`, `PaintFloor`, `RemovePaintFloor`), and until this round none of them was
    /// gated. Most of them reserve the cell themselves - `BuildRoof` calls `pawn.CanReserve(c, ..., Ceiling)` - so the
    /// reservation gate refuses them one step later; the pawn still walks over before failing.
    /// `WorkGiver_GrowerHarvest.HasJobOnCell` (`:313051`) does <b>not</b> reserve anything: it only inspects the plant, so a
    /// visitor without `Gathering` was offered harvest work inside somebody's growing zone and failed only at the job.</para>
    ///
    /// <para>The three names matched by string are the ones the game assembly marks `internal`; everything else is a type
    /// the compiler checks. As with `WorkActOf`, a rename of a string arm is pinned by a test.</para></summary>
    internal static SettlementAct CellWorkAct(WorkGiver_Scanner? giver) => giver switch
    {
        null => SettlementAct.Place,
        WorkGiver_GrowerHarvest => SettlementAct.Gather,
        WorkGiver_BuildRoof or WorkGiver_ConstructAffectFloor or WorkGiver_ConstructRemoveFloor
            or WorkGiver_ConstructRemoveFoundation or WorkGiver_ConstructSmoothWall or WorkGiver_RemoveRoof
            or WorkGiver_PaintFloor or WorkGiver_RemovePaintFloor => SettlementAct.Build,
        _ when giver.GetType().Name is "WorkGiver_ClearSnowOrSand" or "WorkGiver_ClearPollution" or "WorkGiver_Fish"
            => SettlementAct.Clean,
        _ => SettlementAct.Place,
    };

    /// <summary>The thing a cell-work giver is really acting on, when there is one.
    ///
    /// <para>A cell's own kind is only `Land` or `Storage`, so a cell-only gate can never ask `Gather`/`Plant` - and
    /// harvesting somebody's crops is exactly the case that has no other gate. When the act is `Gather` the plant standing
    /// in that cell is the thing the permission is about; for `Clean` it is the filth lying there. Everything else is
    /// genuinely about the ground, and falls through to the cell.</para></summary>
    internal static Thing? CellWorkTarget(Map? map, IntVec3 cell, SettlementAct act)
    {
        if (map is null || !cell.InBounds(map)) return null;
        if (act == SettlementAct.Gather) return cell.GetPlant(map);
        if (act != SettlementAct.Clean) return null;
        // `ThingsAt` returns `IEnumerable<Thing>` in 1.7, not a list: indexing it does not compile.
        foreach (var thing in map.thingGrid.ThingsAt(cell))
            if (thing is Filth) return thing;
        return null;
    }

    internal static SettlementAct WorkActOf(WorkGiver_Scanner? giver, Thing? target = null) => giver switch
    {
        null => SettlementAct.Use,
        WorkGiver_CleanFilth => SettlementAct.Clean,
        WorkGiver_HunterHunt => SettlementAct.Hunt,
        WorkGiver_GrowerHarvest or WorkGiver_PlantsCut or WorkGiver_Miner => SettlementAct.Gather,
        // Animal work: taming, training, milking, shearing and roping are all `Handle` in the table.
        WorkGiver_Tame or WorkGiver_Train or WorkGiver_Milk or WorkGiver_Shear
            or WorkGiver_GatherAnimalBodyResources or WorkGiver_InteractAnimal => SettlementAct.Handle,
        WorkGiver_Tend => SettlementAct.Tend,
        // **A bill is `Use` on a bench and `Operate` on a patient (round 89).** `WorkGiver_DoBill` is the one work giver
        // whose act depends on the *target* rather than on the giver: a bill giver can be a workbench (`IBillGiver`) or a
        // wounded pawn (surgery sits on the patient's own `BillStack`), and the table has two different rows for those
        // (`Benches`/`Use`/`Bench` and `Operations`/`Operate`/`Pawn`). This method used to take only the giver, so the
        // surgery side asked `Use` - and `Use`/`Pawn` is not a row, so **a visitor could not be operated on even with
        // `Care` granted**, and `Operations` could never be exercised at all. ColonyTogether takes both arguments for
        // exactly this reason (`WorkGrantOn(giver, target)`, `ct045:57273`).
        WorkGiver_DoBill when target is Pawn => SettlementAct.Operate,
        // `WorkGiver_FightFires` is `internal` in the game assembly and cannot be named here, so it is matched by type
        // name. Deliberately the last arm: everything above is a type the compiler checks, and a rename would be caught
        // at build time. This one would not - so the test below pins the string.
        _ when giver.GetType().Name == "WorkGiver_FightFires" => SettlementAct.PutOut,
        _ => SettlementAct.Use,
    };
}

/// <summary>May this pawn reserve that object.
///
/// <para>The earliest and cheapest gate. Reservation is how the job system says "I am going to use this", and every AI
/// action goes through it, so a refusal here stops the pawn before it starts rather than halfway there. It is also the
/// gate that empties a work giver's candidate list: without it a colonist walks across the map to a stockpile it may not
/// touch and gives up on arrival.</para>
///
/// <para><b>Why the full signature.</b> <c>CanReserve</c> declares six parameters - the last four carry defaults, but
/// the compiled method takes all six, and Harmony matches on the declared parameter list. Naming four finds nothing and
/// reports "no targets matched", which is silent at runtime.</para></summary>
[HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.CanReserve),
    [typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(int), typeof(ReservationLayerDef), typeof(bool)])]
internal static class SettlementReservationGate
{
    private static bool Prefix(Pawn claimant, LocalTargetInfo target, ref bool __result)
    {
        // **`Store` for storage, `Use` for everything else (round 139).** Reserving a shelf or a stockpile cell is
        // hauling into somebody's storage, and the table states that as `Storage`/`Store`/`Storage`. Asking `Use` meant
        // the `Store` row had no reader at all - the same shape as the `Pod` rows before round 87 - and the cell branch
        // asked `Place`, so `BuildArea` was the grant that governed hauling into a floor stockpile and `Storage` was the
        // one that could not. Measured with `script-zone` + `settlement-chokepoint`.
        var storageTarget = target.HasThing
            ? target.Thing is Building_Storage
            : SettlementRule.KindOfCell(claimant.MapHeld, target.Cell) == SettlementKind.Storage;
        var ok = target.HasThing
            ? SettlementGates.Can(claimant, storageTarget ? SettlementAct.Store : SettlementAct.Use, target.Thing)
            // A cell target has no thing to classify, and reserving a cell is what hauling into a stockpile does. The
            // cell is checked as land, which is the only thing a bare position can be.
            // The Pawn overload, not the int one: it resolves the map from `pawn.MapHeld`. A reservation's claimant and
            // its target are on the same map - a pawn cannot haul across maps - so `MapHeld` is the map the cell belongs
            // to, while `Find.CurrentMap` is only the map the player happens to be looking at.
            : SettlementGates.CanCell(claimant, storageTarget ? SettlementAct.Store : SettlementAct.Place, target.Cell);
        if (ok) return true;
        SettlementGates.Count(reservations: 1);
        __result = false;
        return false;
    }
}

/// <summary>Does this pawn's work giver see any work on that object.
///
/// <para>This is the gate that stops a colonist <i>choosing</i> a job it would then be refused. Without it the behaviour
/// is worse than a refusal: the pawn commits, walks over, and only then fails, which the player reads as broken
/// pathing. <c>WorkGiver_Scanner.HasJobOnThing</c> is the base every work giver overrides, so one patch covers hauling,
/// constructing, mining, cooking and whatever a mod adds.</para>
///
/// <para><b>Which act this gate asks for.</b> It used to ask for <c>Use</c> unconditionally, on the reasoning that a
/// work giver's own verb is not known at this call site. That reasoning was wrong in a way that silently disabled a third
/// of the rule table: `Use` has a row only for `Bench`, `Bed`, `Pod` and `Seat`, and <c>SettlementRuleTable.Permits</c>
/// **refuses an unlisted pair**. So for `Filth`, `Fire`, `Plant`, `WildAnimal` and `Animal` every work giver was refused
/// no matter what grants the pawn held - a visitor with `All` could not clean, fight a fire, harvest, hunt or rope an
/// animal in somebody else's settlement, and the refusal was indistinguishable from the owner having denied it.</para>
///
/// <para>The verb <i>is</i> knowable here: `HasJobOnThing` is called on a specific work giver instance, and the work
/// givers whose verb differs from `Use` are a small closed set of engine types - cleaning, firefighting, hunting,
/// gathering and animal handling. Everything else keeps `Use`, which is what the table is written around (bills, beds,
/// benches, seats). The list is by type rather than by `def.workType` because the work type is a def a mod can add and
/// the table's rows are about what the object <i>is</i>: `WorkGiver_CleanFilth` on filth is `Clean` whether the work
/// type is called Cleaning or something a mod renamed.</para>
///
/// <para>Work that is really about taking is still gated at the reservation step, which runs as soon as a job is chosen;
/// the point of this gate is to keep forbidden work out of the candidate list, not to be the last word.</para></summary>
[HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing),
    [typeof(Pawn), typeof(Thing), typeof(bool)])]
internal static class SettlementWorkGate
{
    private static bool Prefix(WorkGiver_Scanner __instance, Pawn pawn, Thing t, ref bool __result)
    {
        if (SettlementGates.Can(pawn, SettlementGates.WorkActOf(__instance, t), t)) return true;
        SettlementGates.Count(work: 1);
        __result = false;
        return false;
    }
}

/// <summary>Does this pawn's cell-work giver see any work on that cell.
///
/// <para>The cell half of <see cref="SettlementWorkGate"/>. Twelve work givers in 1.7 work on cells rather than things;
/// `WorkGiver_GrowerHarvest` is the one that matters most, because it inspects the plant and never reserves anything, so
/// before this round a visitor without `Gathering` was offered harvest work in somebody else's growing zone (round 155's
/// probe reads both sides).</para>
///
/// <para><b>No invented refusals.</b> A cell's kind is only `Land` or `Storage`, and the table states few acts on those.
/// When the act has no row for the cell's kind the gate permits and says nothing: refusing a pair the table does not
/// describe would make every unlisted flavour of cell work impossible for visitors - the failure mode round 136 found
/// with `Use`, where five kinds had no row and every work giver was refused. The act-specific gates still decide.</para></summary>
[HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnCell),
    [typeof(Pawn), typeof(IntVec3), typeof(bool)])]
internal static class SettlementWorkCellGate
{
    private static bool Prefix(WorkGiver_Scanner __instance, Pawn pawn, IntVec3 c, ref bool __result)
    {
        var act = SettlementGates.CellWorkAct(__instance);
        var thing = SettlementGates.CellWorkTarget(pawn.MapHeld, c, act);
        if (thing is not null)
        {
            if (SettlementGates.Can(pawn, act, thing)) return true;
        }
        else
        {
            var kind = SettlementRule.KindOfCell(pawn.MapHeld, c);
            if (!SettlementRuleTable.HasRow(act, kind)) return true;
            if (SettlementGates.CanCell(pawn, act, c)) return true;
        }
        SettlementGates.Count(work: 1);
        __result = false;
        return false;
    }
}

/// <summary>May this designation be placed.
///
/// <para><b>Why the targets are discovered instead of named.</b> <c>Designator.CanDesignateCell</c> is <c>abstract</c>
/// and <c>CanDesignateThing</c> is <c>virtual</c>, and the subclasses override them without calling base - so a patch on
/// the base class is never reached for a build order or a stockpile drag. Listing every subclass by hand would be
/// dozens of entries that go stale with each game update and miss every modded designator.
/// <c>TargetMethods</c> enumerates the family once at install time, which is what ColonyTogether does with its
/// <c>OwnershipTargets</c> scan (ct045.decompiled.cs:328306).</para>
///
/// <para>A postfix, not a prefix: the designator's own check has already decided whether the target is acceptable at
/// all, and this only narrows it. Refusing before the vanilla check would report "forbidden" for a cell the designator
/// would have rejected anyway, teaching the player the wrong reason.</para></summary>
[HarmonyPatch]
internal static class SettlementDesignatorGate
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        var targets = new List<System.Reflection.MethodBase>();
        var family = new HashSet<Type>();
        try
        {
            foreach (var type in typeof(Designator).AllSubclasses())
                if (type is { IsAbstract: false } && !type.ContainsGenericParameters) family.Add(type);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 据点权限：枚举 Designator 子类失败：" + error.GetType().Name);
            return targets;
        }
        foreach (var type in family)
        {
            if (AccessTools.DeclaredMethod(type, nameof(Designator.CanDesignateCell))
                is { ReturnType: var cellReturn } cell && cellReturn == typeof(AcceptanceReport))
                targets.Add(cell);
            if (AccessTools.DeclaredMethod(type, nameof(Designator.CanDesignateThing))
                is { ReturnType: var thingReturn } thing && thingReturn == typeof(AcceptanceReport))
                targets.Add(thing);
        }
        return targets;
    }

    private static void Postfix(Designator __instance, object[] __args, ref AcceptanceReport __result)
    {
        if (__result.Accepted is false) return;
        var data = SettlementGates.Data;
        if (data is null || !data.Active) return;
        var act = ActOf(__instance);
        var player = SettlementGates.LocalPlayer;
        foreach (var argument in __args)
        {
            var ok = argument switch
            {
                IntVec3 cell => SettlementGates.CanCell(player, act, cell),
                // A thing target is the object being designated, which is what the rule table classifies.
                Thing thing => SettlementGates.Can(player, act, thing),
                _ => true,
            };
            if (ok) continue;
            SettlementGates.Count(designations: 1);
            // The display string is what the player reads: `AcceptanceReport` renders on the cursor while a designator is
            // held, so this is the one refusal path that already speaks. Naming the settlement and the missing permission
            // turns "this cell is red" into "you need storage rights here".
            __result = new AcceptanceReport(SettlementGates.ExplainFor(act, argument));
            return;
        }
    }

    /// <summary>Which act a designator performs. Read from its type rather than a table keyed by class name: the base
    /// classes the game ships are stable, and a modded designator inherits one of them, so the classification carries
    /// over to content this file has never seen.
    ///
    /// <para>There is no <c>Designator_Repair</c> in this game version - repair is a work type, not a designator - so
    /// the switch deliberately has no case for it. Listing a class that does not exist fails the build, which is the
    /// right failure: a mistyped name here would otherwise silently fall through to `Use`.</para></summary>
    private static SettlementAct ActOf(Designator designator) => designator switch
    {
        Designator_Deconstruct => SettlementAct.TakeApart,
        Designator_Uninstall => SettlementAct.Uninstall,
        Designator_Hunt => SettlementAct.Hunt,
        Designator_PlantsHarvest or Designator_PlantsCut or Designator_Mine => SettlementAct.Gather,
        Designator_Build or Designator_Install => SettlementAct.Build,
        // Zone and area drags are the common `Place`; anything unrecognised is `Use`, the permissive reading.
        Designator_ZoneAdd or Designator_AreaAllowed or Designator_AreaBuildRoof or Designator_AreaNoRoof
            or Designator_AreaIgnoreRoof => SettlementAct.Place,
        _ => SettlementAct.Use,
    };
}

/// <summary>May this pawn walk through that door - the gate for <see cref="SettlementAct.Pass"/>.
///
/// <para><b>Why this is a fourth gate and not part of the other three.</b> Doors are not reserved, worked or
/// designated. The engine asks about them from the pathfinder, through <c>Building_Door.PawnCanOpen</c>, and a refusal
/// there does not surface as a failed job: the pawn simply routes around, or - when the door is the only way in -
/// stands still. Neither the reservation gate nor the work gate is ever called for a doorway, so the table row
/// `Doors`/`Pass`/`Door` had no reader at all and the grant it guards did nothing.</para>
///
/// <para><b>Postfix, not prefix.</b> Vanilla's answer already accounts for things this layer knows nothing about -
/// forbidden doors, prisoner rules, faction hostility, a door held open. This gate only ever narrows: if vanilla said
/// no, the answer stays no; if vanilla said yes and the settlement rules say no, the answer becomes no. ColonyTogether
/// reaches the same shape as a postfix on the same method (ct045.decompiled.cs:83117).</para>
///
/// <para><b>What this does not cover.</b> <c>FreePassage</c> doors (always-open, e.g. autodoors with no power
/// interaction) never consult <c>PawnCanOpen</c> at all, so a settlement's doors that are permanently open cannot be
/// gated this way. That is a property of the engine's own call graph, not a choice made here, and it is written down
/// rather than papered over: what the grant actually buys is "may open a closed door", which is the action a player
/// recognises.</para></summary>
[HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
internal static class SettlementDoorGate
{
    private static void Postfix(Building_Door __instance, Pawn p, ref bool __result)
    {
        // Already refused by vanilla: nothing to add, and asking the table would only produce a second reason for the
        // same answer.
        if (!__result) return;
        if (SettlementGates.Can(p, SettlementAct.Pass, __instance)) return;
        SettlementGates.Count(pass: 1);
        __result = false;
    }
}

/// <summary>May this pawn take its recreation on that object - the gate for <see cref="SettlementAct.Relax"/>.
///
/// <para><b>The last unreachable pair in the table.</b> `Amenities`/`Relax` had two rows - one on `Building` and one
/// on `Seat` - and nothing produced `Relax` at all: no gate, no work giver, no designator. The flag could not even be
/// granted, because it was absent from <see cref="SettlementGrantWindow"/>'s editable list. So the pair was unreachable
/// from both ends at once: no gate asked it, and no UI could set it.</para>
///
/// <para><b>Why the JoyGiver is the right place.</b> Recreation is not a work giver and not a reservation: a colonist
/// idles, `JoyGiver.TryGiveJob` picks a billiards table, and only then does it walk over. The reservation gate would
/// classify the table as `Use`, and `Use`/`Building` is not a row - so without this patch a settlement a visitor may not
/// build in is still a settlement where they may play billiards on the owner's table, and `Amenities` says nothing
/// about it.</para>
///
/// <para><b>Postfix that nulls the job, not a prefix that skips the giver.</b> Vanilla's answer already accounts for
/// whether the pawn can reach the table, whether it is reserved, and whether the giver applies at all; this layer only
/// narrows. Nulling the result is the same answer vanilla gives when no joy is available, so the pawn picks another
/// giver or goes for a walk - there is always a fallback, which is why this is safe here and would not be in a work
/// giver.</para>
///
/// <para><b>Only building-directed joy.</b> `GoForWalk`, `Skygaze` and `Meditate` target no object, so there is nothing
/// to classify and they stay ungated - walking across somebody's land is not using their furniture.</para></summary>
/// <para><b>Why `TargetMethods` and not `[HarmonyPatch(typeof(JoyGiver), ...)]`.</b> `JoyGiver.TryGiveJob(Pawn)` is
/// **abstract**, and Harmony cannot patch an abstract method - it has no body to wrap. The first version of this class
/// did exactly that and the installer logged
/// 「补丁 settlement-joy-gate 失败：Patching exception in method abstract virtual Verse.AI.Job
/// RimWorld.JoyGiver::TryGiveJob(Verse.Pawn pawn)」, then refused to start the server because every gameplay patch must
/// install. The concrete methods live on the subclasses (`JoyGiver_InteractBuilding` and its twelve siblings), so the
/// patch has to enumerate them. ColonyTogether uses the same shape for the same reason
/// (`GenTypes.AllSubclasses(typeof(RecipeWorker))`, `ct045:57214`).</para>
[HarmonyPatch]
internal static class SettlementJoyGate
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Every concrete `TryGiveJob` in the hierarchy. `GenTypes.AllSubclasses` is the engine's own enumeration of loaded
        // types, so a modded joy giver is included without being named here.
        var found = 0;
        foreach (var type in GenTypes.AllSubclasses(typeof(JoyGiver)))
        {
            var method = AccessTools.DeclaredMethod(type, nameof(JoyGiver.TryGiveJob), [typeof(Pawn)]);
            // `DeclaredMethod` returns the override declared by this type or null; a subclass that does not override keeps
            // the inherited body, and patching the same base method once is enough.
            if (method is null || method.IsAbstract) continue;
            found++;
            yield return method;
        }
        // A reading that found nothing would install zero patches and say "installed" - which is the failure this project
        // has hit before with `TargetMethods`. Logged rather than thrown: an empty scan is a fact about the assembly, and
        // the installer's own "every gameplay patch must install" check would then fail on nothing at all.
        if (found == 0)
            Log.Warning("[TurboTick] 娱乐闸门：没有找到任何具体的 JoyGiver.TryGiveJob，补丁未安装");
    }

    private static void Postfix(Pawn pawn, ref Job? __result)
    {
        if (__result is null) return;
        // A joy job with no thing target (a walk, a meditation spot) has nothing to ask.
        if (!__result.targetA.HasThing) return;
        var thing = __result.targetA.Thing;
        if (thing is null) return;
        if (SettlementGates.Can(pawn, SettlementAct.Relax, thing)) return;
        SettlementGates.Count(relax: 1);
        __result = null;
    }
}
