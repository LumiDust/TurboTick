using System;

namespace TurboTick.Networking;

/// <summary>The vocabulary of the multi-settlement ownership layer: what a player may do, to what, and what kind of
/// thing they are doing it to.
///
/// <para>These three enums live in `Networking` rather than beside the game-side model because they are the part that
/// can be verified without a running game. The rule table that maps them (game side, `SettlementRule`) is 24 rows of
/// policy and the only way to be sure it is right is to check every pair; the test project cannot reference the game
/// assembly, so the vocabulary and the table's truth-value surface have to be reachable from here. `SettlementRule`
/// itself stays where the `Thing` classification lives.</para>
///
/// <para>The names and numeric values are copied from ColonyTogether (`ColonyGrant`, ct045.decompiled.cs:369140 and
/// `Kind`, :324225) so a player who has used either mod reads the same settings, and a save written by one is legible
/// to the other.</para></summary>
[Flags]
public enum SettlementGrant
{
    None = 0,
    /// <summary>Walk through the settlement's doors.</summary>
    Doors = 1,
    /// <summary>Sleep in its beds, use its medical pods.</summary>
    Care = 2,
    /// <summary>Use its workbenches.</summary>
    Benches = 4,
    /// <summary>Pick up loose supplies.</summary>
    Items = 8,
    /// <summary>Take its weapons and armour.</summary>
    Equipment = 0x10,
    /// <summary>Take its food.</summary>
    Food = 0x20,
    /// <summary>Put things into its stockpiles.</summary>
    Storage = 0x40,
    /// <summary>See its alerts. Separate from the rest because an alert is information, not a resource.</summary>
    Alerts = 0x80,
    /// <summary>Build, deconstruct, uninstall.</summary>
    Construction = 0x100,
    /// <summary>Repair its buildings.</summary>
    Repairs = 0x200,
    /// <summary>Clean it.</summary>
    Cleaning = 0x400,
    /// <summary>Put out its fires.</summary>
    Firefighting = 0x800,
    /// <summary>Hunt on its land.</summary>
    Hunting = 0x1000,
    /// <summary>Harvest its plants and mine its rock.</summary>
    Gathering = 0x2000,
    /// <summary>Designate zones and buildings on its land.</summary>
    BuildArea = 0x4000,
    /// <summary>Use its furniture.</summary>
    Furniture = 0x8000,
    /// <summary>Operate on its pawns.</summary>
    Operations = 0x10000,
    /// <summary>Trade with its traders.</summary>
    Traders = 0x20000,
    /// <summary>Handle and tame its animals.</summary>
    AnimalHandling = 0x40000,
    /// <summary>Use its recreation and comfort buildings.</summary>
    Amenities = 0x80000,
    /// <summary>What a visitor gets by default: enough to survive, not enough to take the colony apart.
    ///
    /// <para><b>`Food`, not `Items` (round 156).</b> `Items` is the row `Take`/`Supply` (`SettlementRuleTable.cs:46`) -
    /// "carry off whatever is lying around" - which is precisely taking the colony apart. Round 154's forbid-visibility
    /// reading showed what it meant in practice (a guest saw the host's silver as permitted), and round 156 showed the
    /// consequence that matters: `TradeUtility.AllLaunchableThingsForTrade` hands the payment loop everything inside a
    /// beacon's cells, and with `Items` granted by default a visitor's trade was paid with the host's silver. `Food` is the
    /// row `Take`/`Food`: eat from the host's larder, which is what "enough to survive" actually requires.</para></summary>
    Default = Doors | Care | Benches | Food,
    /// <summary>Everything. The owner's own grants.</summary>
    All = Doors | Care | Benches | Items | Equipment | Food | Storage | Alerts | Construction | Repairs
        | Cleaning | Firefighting | Hunting | Gathering | BuildArea | Furniture | Operations | Traders
        | AnimalHandling | Amenities,
}

/// <summary>The kind of thing an action is aimed at.
///
/// <para>Copied from ColonyTogether's <c>Kind</c> (ct045.decompiled.cs:324225). The point of a kind is that a grant is
/// stated in terms a player uses - "food", "a workbench", "its land" - rather than in engine types, so the same grant
/// covers a simple meal, a fine meal and a nutrient paste dispenser without listing any of them.</para></summary>
public enum SettlementKind
{
    None,
    Door,
    Bed,
    Bench,
    Seat,
    Storage,
    Building,
    Rock,
    Blueprint,
    Frame,
    Supply,
    Equipment,
    Food,
    Pawn,
    Animal,
    WildAnimal,
    Plant,
    Filth,
    Fire,
    Land,
    Colony,
    Pod,
    Roof,
}

/// <summary>The actions a grant can permit.</summary>
public enum SettlementAct
{
    None,
    Pass,
    Use,
    Tend,
    Operate,
    Relax,
    Take,
    Store,
    Build,
    TakeApart,
    Uninstall,
    Repair,
    Place,
    Clean,
    PutOut,
    Hunt,
    Gather,
    Handle,
    Trade,
}

/// <summary>A mirror of <c>RimWorld.GuestStatus</c> so the wire form can be validated without the game assembly.
///
/// <para>The numeric values are the game's own (<c>Guest: 0, Prisoner: 1, Slave: 2</c>) and must stay in step with it: the
/// frame carries the status as an integer, and a decoder that wrote an out-of-range value into
/// <c>Pawn_GuestTracker.guestStatusInt</c> would make every <c>IsPrisoner</c>/<c>IsSlave</c> comparison false in a way no
/// log records. The replica side checks against the game's enum; this copy exists so a test can check the same values
/// without a running game.</para>
///
/// <para>Deliberately a separate type rather than a shared one: `Networking` cannot reference the game assembly, and the
/// two must be compared by value, not by identity - which is exactly what the test does.</para></summary>
public enum GuestStatusWire
{
    Guest = 0,
    Prisoner = 1,
    Slave = 2,
}

/// <summary>A mirror of <c>RimWorld.Passion</c>, for the same reason as <see cref="GuestStatusWire"/>: the wire carries
/// the value as an integer and a test cannot reference the game assembly.
///
/// <para>The numeric values are the game's own (<c>None: 0, Minor: 1, Major: 2</c>) and must stay in step with it.
/// <c>SkillRecord.passion</c> is a <c>byte</c>, so an out-of-range value would make every <c>Passion.Major</c> comparison
/// false and the skill would silently stop granting its larger learning rate - no error, no log, just a colonist who stops
/// improving. `SkillSync` checks the two sides against each other once at startup.</para></summary>
public enum PassionWire
{
    None = 0,
    Minor = 1,
    Major = 2,
}

/// <summary>A mirror of <c>RimWorld.FactionRelationKind</c> for the same reason as <see cref="GuestStatusWire"/>.
///
/// <para>The wire carries it as a <c>byte</c>, so a value from a bad frame would compare unequal to every game-side member
/// and leave the faction painted with whatever kind it loaded with - silently. <see cref="TurboTick.FactionSync"/> checks the
/// two sides against each other once at startup and refuses to apply an out-of-range value.</para></summary>
public enum FactionRelationKindWire
{
    Hostile = 0,
    Neutral = 1,
    Ally = 2,
}
