using System;

namespace TurboTick.Networking;

/// <summary>The rule table that decides whether a grant permits an action on a kind of thing - pure policy, no game
/// state.
///
/// <para><b>Why a table and not a chain of ifs.</b> The rules are not a hierarchy: "may use a workbench" and "may use a
/// bed" are both single flags, but "may build" covers blueprints, frames <i>and</i> roofs while "may take apart" covers
/// every standing structure. Written as conditionals that is a dozen <c>||</c> chains nobody can check against the
/// design; written as a table it is something you read down the page. Copied entry for entry from ColonyTogether's
/// <c>OwnershipRule.Table</c> (ct045.decompiled.cs:324277), so a player who has used either mod gets the same answers.
/// </para>
///
/// <para><b>Why this half is in <c>Networking</c>.</b> The table is the only part of the ownership layer whose
/// correctness can be established without a running game: every pair either has a row or does not, and every row names
/// exactly one flag. The other half - deciding which <see cref="SettlementKind"/> a live <c>Thing</c> is - needs the
/// game assembly and stays in <c>Source</c> as <c>SettlementRule</c>. Splitting them lets the test project check the
/// policy exhaustively instead of spot-checking it.</para>
///
/// <para><b>Why an unlisted pair is refused.</b> Two defaults are possible and both are wrong in some way: default-deny
/// means an action nobody wrote down silently stops working and looks like a pathing bug, default-allow means a grant
/// forgets to protect something and a player watches their stockpile emptied. Refusing is chosen because it fails
/// visibly - <see cref="Required"/> can name the flag the player would have needed - while an unnoticed takeover leaves
/// no trace to diagnose.</para>
public static class SettlementRuleTable
{
    /// <summary>One row: the flag that permits <paramref name="Act"/> on an object of kind
    /// <paramref name="On"/>.</summary>
    internal readonly record struct Row(SettlementGrant Grant, SettlementAct Act, SettlementKind On);

    /// <summary>The table. Order is irrelevant to the lookup; grouping is for reading.</summary>
    private static readonly Row[] Table =
    [
        // Entry and shelter - what a visitor needs to survive in somebody else's settlement.
        new(SettlementGrant.Doors, SettlementAct.Pass, SettlementKind.Door),
        new(SettlementGrant.Care, SettlementAct.Use, SettlementKind.Bed),
        new(SettlementGrant.Care, SettlementAct.Use, SettlementKind.Pod),
        new(SettlementGrant.Care, SettlementAct.Tend, SettlementKind.Pawn),
        new(SettlementGrant.Operations, SettlementAct.Operate, SettlementKind.Pawn),
        new(SettlementGrant.Benches, SettlementAct.Use, SettlementKind.Bench),
        new(SettlementGrant.Furniture, SettlementAct.Use, SettlementKind.Seat),
        new(SettlementGrant.Amenities, SettlementAct.Relax, SettlementKind.Building),
        new(SettlementGrant.Amenities, SettlementAct.Relax, SettlementKind.Seat),
        // Taking things out.
        new(SettlementGrant.Items, SettlementAct.Take, SettlementKind.Supply),
        new(SettlementGrant.Equipment, SettlementAct.Take, SettlementKind.Equipment),
        new(SettlementGrant.Food, SettlementAct.Take, SettlementKind.Food),
        // Putting things in.
        new(SettlementGrant.Storage, SettlementAct.Store, SettlementKind.Storage),
        // Building up.
        new(SettlementGrant.Construction, SettlementAct.Build, SettlementKind.Blueprint),
        new(SettlementGrant.Construction, SettlementAct.Build, SettlementKind.Frame),
        new(SettlementGrant.Construction, SettlementAct.Build, SettlementKind.Roof),
        // Taking down. Every standing structure is listed rather than derived from one "is a building" row: a door and a
        // rock are both deconstructible but neither is a `Building` in the engine's sense, so a derived row would miss
        // one or the other.
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Building),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Door),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Bed),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Bench),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Seat),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Storage),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Rock),
        new(SettlementGrant.Construction, SettlementAct.TakeApart, SettlementKind.Pod),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Building),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Door),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Bed),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Bench),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Seat),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Storage),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Rock),
        new(SettlementGrant.Construction, SettlementAct.Uninstall, SettlementKind.Pod),
        // Upkeep.
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Door),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Bed),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Bench),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Seat),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Storage),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Building),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Rock),
        new(SettlementGrant.Repairs, SettlementAct.Repair, SettlementKind.Pod),
        // Land.
        new(SettlementGrant.BuildArea, SettlementAct.Place, SettlementKind.Land),
        new(SettlementGrant.BuildArea, SettlementAct.Build, SettlementKind.Land),
        // Everything else.
        new(SettlementGrant.Cleaning, SettlementAct.Clean, SettlementKind.Filth),
        new(SettlementGrant.Firefighting, SettlementAct.PutOut, SettlementKind.Fire),
        new(SettlementGrant.Hunting, SettlementAct.Hunt, SettlementKind.WildAnimal),
        new(SettlementGrant.Gathering, SettlementAct.Gather, SettlementKind.Plant),
        new(SettlementGrant.Gathering, SettlementAct.Gather, SettlementKind.Rock),
        new(SettlementGrant.AnimalHandling, SettlementAct.Handle, SettlementKind.Animal),
        new(SettlementGrant.AnimalHandling, SettlementAct.Handle, SettlementKind.WildAnimal),
        new(SettlementGrant.Traders, SettlementAct.Trade, SettlementKind.Building),
    ];

    /// <summary>Whether a set of grants permits an action on a kind of thing.</summary>
    public static bool Permits(SettlementGrant grants, SettlementAct act, SettlementKind kind)
    {
        if (act == SettlementAct.None || kind == SettlementKind.None) return false;
        for (var i = 0; i < Table.Length; i++)
        {
            var row = Table[i];
            if (row.Act != act || row.On != kind) continue;
            return (grants & row.Grant) != 0;
        }
        return false;
    }

    /// <summary>Whether the table states this action on this kind of thing at all.
    ///
    /// <para><b>Why the reservation gate needs to ask (round 136).</b> `ReservationManager.CanReserve` is the engine's
    /// generic "I am about to touch this" and its caller is every work giver, so the act is not knowable there - the
    /// gate asks `Use`. For Filth, Fire, Plant, WildAnimal and Animal the table has **no** `Use` row: it states
    /// `Cleaning`/`Clean`, `Firefighting`/`PutOut`, `Gathering`/`Gather`, `Hunting`/`Hunt`, `AnimalHandling`/`Handle`.
    /// So a visitor granted `Cleaning` was refused permission to *reserve* filth, `WorkGiver_CleanFilth.HasJobOnThing`
    /// calls `CanReserve` (`:311032`) before anything else, and the grant could never be exercised: measured with the
    /// work gate probe, `业主 #870 有 1 个，访客独缺：CleanFilth` on a visitor holding `All` (1048575) - the work gate
    /// itself would have permitted `Clean`, because `Clean`/`Filth` is a row; the refusal happened one gate earlier and
    /// the answer the player sees is "my cleaning permission does nothing".</para></summary>
    public static bool HasRow(SettlementAct act, SettlementKind kind)
    {
        for (var i = 0; i < Table.Length; i++)
            if (Table[i].Act == act && Table[i].On == kind) return true;
        return false;
    }

    /// <summary>Whether the grants permit <i>any</i> action the table states on this kind of thing.
    ///
    /// <para>Used only as the reservation gate's fallback for a kind that has no row for the asking act: it asks "did the
    /// owner grant this visitor something that applies to this kind", which is the question a reservation can honestly
    /// answer. The act-specific gates (work giver, designator) still make the real decision.</para></summary>
    public static bool PermitsAnyOn(SettlementGrant grants, SettlementKind kind)
    {
        for (var i = 0; i < Table.Length; i++)
        {
            var row = Table[i];
            if (row.On != kind) continue;
            if ((grants & row.Grant) != 0) return true;
        }
        return false;
    }

    /// <summary>Which flag governs a pair, or <see cref="SettlementGrant.None"/> when the pair is not in the table.
    /// Lets a refusal name the grant the player would have needed instead of only saying "no".</summary>
    public static SettlementGrant Required(SettlementAct act, SettlementKind kind)
    {
        for (var i = 0; i < Table.Length; i++)
            if (Table[i].Act == act && Table[i].On == kind) return Table[i].Grant;
        return SettlementGrant.None;
    }

    /// <summary>How many rows the table holds. A row silently lost in an edit is a permission silently removed, and the
    /// symptom would look like a pathing bug - so a test pins this number and checks every row is reachable.</summary>
    public static int RowCount => Table.Length;

    /// <summary>Every row as (grant, act, kind), for the tests that check the table's invariants.</summary>
    public static (SettlementGrant Grant, SettlementAct Act, SettlementKind Kind)[] Rows()
    {
        var rows = new (SettlementGrant, SettlementAct, SettlementKind)[Table.Length];
        for (var i = 0; i < Table.Length; i++) rows[i] = (Table[i].Grant, Table[i].Act, Table[i].On);
        return rows;
    }
}
