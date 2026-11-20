using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>The per-pawn sub-trackers whose counters only their own tick advances, for a replica that never ticks a pawn.
///
/// <para><b>The defect, one shape four times.</b> Each of these is an <c>IExposable</c> owned by the pawn, scribed by
/// <c>Pawn.ExposeData</c> (<c>:131381-131431</c>), and advanced only from <c>Pawn.TickInterval</c>:</para>
///
/// <list type="bullet">
/// <item><c>Pawn_GuiltTracker.guiltyTicksLeft</c> - decremented in <c>GuiltTrackerTick</c> (`:479267`). Read by
/// <c>IsGuilty</c> (`:479231`), which decides whether a colonist may be arrested (`:130845`), whether a prisoner may be
/// executed as guilty or innocent (`:309906`/`:309910`), whether recruiting is blocked (`:291121`), and the tooltip that
/// prints the remaining time (`:479245`, drawn at `:642469` and `:671766`). A replica therefore offers an arrest on a pawn
/// who has long been innocent, and shows a countdown that never moves.</item>
/// <item><c>Pawn_TrainingTracker.countDecayFrom</c> - pushed forward by <c>TrainingTrackerTickRare</c> (`:476821`), which is
/// called from <c>Pawn.TickInterval</c> (`:128297`). <c>TrainingTrackerTickRare</c> is also what actually removes a
/// training step once <c>countDecayFrom + DegradationPeriodTicks</c> has passed (`:476837`), so a frozen stamp means an
/// animal never loses its training here.</item>
/// <item><c>Pawn_LearningTracker.newLearningDesireTicksLeft</c> - decremented in <c>LearningTickInterval</c> (`:472552`),
/// called from <c>Pawn.TickInterval</c> (`:128477`). When it reaches zero the pawn gains a new learning desire, which is
/// what the learning readout lists (`:466678`).</item>
/// <item><c>Pawn_FlightTracker</c> - <c>flyingTicks</c>, <c>flightCooldownTicks</c> and <c>lerpTick</c> are advanced by
/// <c>FlightTick</c> (`:729159`), called from <c>Pawn.TickInterval</c> (`:128306`). <c>PositionOffsetFactor</c>
/// (`:729145`) is built from <c>flightState</c> and <c>lerpTick</c> and is what lifts a flying pawn's graphic off the
/// ground (`:105362`, `:105371`, `:106603`), and <c>CanFlyNow</c> (`:729131`) gates starting a flight (`:729223`).</item>
/// </list>
///
/// <para><b>Why they share a channel.</b> They are four different concepts but one mechanism: a counter on a
/// `<c>*Tracker` owned by a pawn, scribed but never re-run. Sending them separately would mean four near-identical
/// publish/apply pairs and four separate chances to forget one. The payload is keyed by pawn and carries one row per
/// tracker that has something to say, so a pawn with no guilt, no training decay and no flight costs nothing.</para>
///
/// <para><b>Why the values are read from the fields rather than from the properties.</b> <c>TicksUntilInnocent</c> is
/// public, but <c>countDecayFrom</c>, <c>newLearningDesireTicksLeft</c>, <c>flyingTicks</c>, <c>flightCooldownTicks</c> and
/// <c>lerpTick</c> are all private, and <c>FlightState</c> is a private enum - so reflection is the only way to read the
/// whole set without six near-identical special cases. Each lookup is cached once per type.</para></summary>
internal static class PawnTrackerSync
{
    /// <summary>Last encoded row per pawn, so an unchanged pawn costs nothing.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static long TrackersSent { get; private set; }
    internal static long TrackersApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Training progress written through <see cref="Apply"/>: pawns touched and steps changed. Counted apart
    /// from <see cref="TrackersApplied"/> because the two halves fail differently - a timer row that does not arrive
    /// freezes a countdown, while a training row that does not arrive means an animal never learns on one peer and the
    /// player sees the checkbox already ticked.</summary>
    /// <summary>Hostility-response rows applied. Counted apart because "0" has two causes with opposite fixes: nobody
    /// changed a response mode this session, or the row never decoded.</summary>
    internal static long HostilityRows { get; private set; }

    /// <summary>Medical-care rows applied, counted apart for the same reason as <see cref="HostilityRows"/>.</summary>
    internal static long MedicalCareRows { get; private set; }

    /// <summary>Rows whose `limitEntropyAmount` was written by an applied row (round 142).</summary>
    internal static long EntropyLimitRows { get; private set; }

    /// <summary>Rows whose `hemogenPacksAllowed` was written by an applied row (round 142).</summary>
    internal static long HemogenPackRows { get; private set; }

    /// <summary>Rows that wrote `guilt.awaitingExecution` (round 144).</summary>
    internal static long GuiltExecutionRows { get; private set; }

    /// <summary>Rows that carried a baby naming deadline (round 151).</summary>
    internal static long NamingDeadlineRows { get; private set; }

    /// <summary>Control-group SEGMENTS written by an applied row - one per group, so a mechanitor with two
    /// groups counts twice for a single row (round 143).</summary>
    internal static long MechSettingsRows { get; private set; }

    /// <summary>Control-group rows carrying a `MechWorkModeDef` this peer does not have (round 143).</summary>
    internal static long MechWorkModeUnresolved { get; private set; }

    /// <summary>Animal follow rows applied (`followDrafted`).</summary>
    internal static long AnimalFollowRows { get; private set; }

    /// <summary>Master references that named a pawn this peer does not have. Counted because the visible result - an
    /// animal that does not follow - is otherwise indistinguishable from "the row never arrived".</summary>
    internal static int MasterUnresolved { get; private set; }

    /// <summary>Ordered attack targets applied from the channel (round 126).</summary>
    internal static int AttackTargetsApplied { get; private set; }

    /// <summary>Clears that arrived as `a:0` and actually had something to clear.</summary>
    internal static int AttackTargetsCleared { get; private set; }

    /// <summary>`a:` rows whose thing id this peer cannot resolve. A distinct number from `AttackTargetsApplied` on
    /// purpose: "the order did not arrive" and "the order arrived for a thing this peer has never seen" look identical
    /// from the player's side - the animal does nothing either way.</summary>
    internal static int AttackTargetUnresolved { get; private set; }

    internal static long TrainingRows { get; private set; }
    internal static long TrainingSteps { get; private set; }

    /// <summary>The last row published, printed by the census so the encoded shape can be read.</summary>
    internal static string? LastSample { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        TrackersSent = 0;
        TrackersApplied = 0;
        ApplyFailures = 0;
        TrainingRows = 0;
        TrainingSteps = 0;
        LastSample = null;
    }

    /// <summary>Counters are quantised to this. All four are "ticks until something happens" measured against thresholds of
    /// tens of thousands, and every one of them moves every tick - without a step this channel would re-send every frame
    /// for a difference no one can see. One game hour resolves `countDecayFrom`'s 250-tick steps and the 50-tick flight
    /// lerp is handled separately below.</summary>
    private const int TickQuantum = 300;

    internal static List<ReplicaPawnTracker>? Publish()
    {
        List<ReplicaPawnTracker>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is null) continue;
                    var id = pawn.thingIDNumber;
                    var encoded = Encode(pawn);
                    if (encoded is null)
                    {
                        if (sent.Remove(id)) (changed ??= []).Add(new ReplicaPawnTracker(id, string.Empty));
                        continue;
                    }
                    live.Add(id);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaPawnTracker(id, encoded));
                    TrackersSent++;
                    LastSample = encoded;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 子追踪器发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>`g:guilt;t:decayFrom;l:desireTicks;f:state:lerp:cooldown:flying`, with absent trackers simply omitted.
    ///
    /// <para>Each field carries its own letter so a decoder never has to count position: the trackers are independent and a
    /// pawn may have guilt without flight or training without learning.</para></summary>
    private static string? Encode(Pawn pawn)
    {
        StringBuilder? builder = null;
        void Add(string text)
        {
            builder ??= new StringBuilder(48);
            if (builder.Length > 0) builder.Append(';');
            builder.Append(text);
        }
        // Guilt: the countdown the tooltip prints and `IsGuilty` branches on, plus the "mark for execution" flag.
        if (pawn.guilt is { } guilt)
        {
            // **`awaitingExecution` (round 144).** The red execute button in a guilty colonist's inspect pane
            // (`LeftRectSection`, `:642584-642600`) flips this bool; nothing else changes. It is read by the execution
            // job's own goto toil (`:284801`, `FailOn(... !awaitingExecution)`) and by the work giver that decides
            // whether a warden may start that job (`:309850`), i.e. only where the simulation runs. So flipping it on a
            // replica left the authority with no order at all, and `guilt.awaitingExecution` had no carrier - the row
            // carried `g:guiltyTicksLeft` beside it since round 121 and never this.
            //
            // Sent whenever the pawn has a guilt tracker: `false` is the scribe default and the button is a toggle, so
            // omission cannot be told apart from "still false".
            Add("o:" + ((ReadBool(guilt, "awaitingExecution") ?? false) ? "1" : "0"));
            var left = ReadInt(guilt, "guiltyTicksLeft") ?? 0;
            // Only a pawn who is guilty at all needs a row - `IsGuilty` is a pure function of this one number plus the
            // mental state, so zero is the default and sending it would be noise.
            if (left > 0) Add("g:" + Quantise(left).ToString(CultureInfo.InvariantCulture));
        }
        // Baby naming deadline (round 151). `Pawn.babyNamingDeadline` (`:126902`, scribed at `:131365` with default -1)
        // is an ABSOLUTE stamp set when a baby is born (`:133215`, `TicksGame + 60000`), cleared to -1 by the naming
        // dialog's Accept (`:196111`) and read by the naming gizmo (`:449758`), the inspect pane (`:618114`) and the
        // "name the baby" float-menu options (`:661585-661616`, where `disabled` is decided by comparing it with the
        // clock). Nothing carried it, so a replica keeps whatever its own copy holds: the option either never appears or
        // never goes away. Sent unconditionally because -1 is the "nothing pending" default, and omission cannot be told
        // apart from it - the apply half has to be able to CLEAR a deadline the authority already cleared.
        Add("b:" + pawn.babyNamingDeadline.ToString(CultureInfo.InvariantCulture));
        // Training decay. `countDecayFrom` is an ABSOLUTE tick stamp, not a countdown, and the game compares it against the
        // clock (`Find.TickManager.TicksGame < countDecayFrom + DegradationPeriodTicks(pawn)`, `:476837`). Quantising it
        // would truncate the stamp itself - measured: a stamp of 300-ish quantised to `0`, which on a replica means the
        // animal decays on the very next rare tick. So the wire carries the REMAINING ticks instead, which is both
        // meaningful and safe to quantise.
        if (pawn.training is { } training && (ReadInt(training, "countDecayFrom") ?? 0) > 0)
        {
            var period = TrainableUtility.DegradationPeriodTicks(pawn);
            var remaining = (ReadInt(training, "countDecayFrom") ?? 0) + period - Find.TickManager.TicksGame;
            // Only while the window is open. An expired stamp means `TrainingTrackerTickRare` is about to remove the step
            // anyway, and `remaining` keeps falling every tick - sending it would keep the channel churning for a number
            // whose only consequence is already decided.
            if (remaining > 0) Add("t:" + Quantise(remaining).ToString(CultureInfo.InvariantCulture));
        }
        // Learning desire: the countdown that produces a new desire at zero.
        if (pawn.learning is { } learning)
        {
            var left = ReadInt(learning, "newLearningDesireTicksLeft") ?? 0;
            if (left > 0) Add("l:" + Quantise(left).ToString(CultureInfo.InvariantCulture));
        }
        // Flight. `flightState` is a private enum, so it travels as its integer; `lerpTick` is NOT quantised - it runs to
        // 50 over the whole takeoff, so a 300-tick step would collapse the entire animation into two frames.
        if (pawn.flight is { } flight)
        {
            var state = ReadInt(flight, "flightState") ?? 0;
            var lerp = ReadInt(flight, "lerpTick") ?? 0;
            var cooldown = ReadInt(flight, "flightCooldownTicks") ?? 0;
            // `flyingTicks` is deliberately NOT sent. It has no reader other than `flyingTicks >= MaxFlightTicks` (`:729193`),
            // which only decides when to switch `flightState` - and a replica never runs `FlightTick`, so this peer's copy
            // would never be compared. Sending it also made the change key churn every frame: measured, a grounded pawn
            // keeps its last value (1367) and one still flying increments it once per tick, so `发` climbed 175 → 891 →
            // 1701 while nothing a player can see was changing.
            if (state != 0 || lerp != 0 || cooldown > 0)
                Add("f:" + state.ToString(CultureInfo.InvariantCulture)
                    + ":" + lerp.ToString(CultureInfo.InvariantCulture)
                    + ":" + Quantise(cooldown).ToString(CultureInfo.InvariantCulture));
        }
        // **Hostility response (round 123).** `Pawn_PlayerSettings.hostilityResponse` (`:477218`, scribed at `:477397`)
        // is the flee/attack/ignore toggle on every colonist's action menu. It is written in exactly one place: the
        // dropdown's option action (`:477059`), i.e. a player click. The authority runs all pawn AI, so a click on a
        // replica never reaches it and the pawn keeps obeying the old mode on the side that simulates:
        // `JobGiver_ConfigurableHostilityResponse` returns `null`/attack/flee from this one field (`:294061-294067`), and
        // a second reader suppresses fleeing whenever the mode is not Flee (`:302333`).
        //
        // Deliberately NOT a command even though it is player input: it is a scribed value on a pawn-owned object, and
        // this channel already compares the whole per-pawn row every frame. A diff scan cannot miss a writer - not the
        // dropdown, not `Pawn_PlayerSettings.ExposeData`, not a mod that assigns the field directly - whereas a patch on
        // the dropdown's generating method would fire on menu *construction*, not on the click.
        //
        // Sent unconditionally rather than only when non-default: `Flee` is the default and the field always exists, so
        // "no row" would be indistinguishable from "still Flee" - and a player who sets a pawn back to Flee has made a
        // real change that has to travel.
        if (pawn.playerSettings is { } playerSettings)
        {
            Add("h:" + ((int)playerSettings.hostilityResponse).ToString(CultureInfo.InvariantCulture));
            // **Medical care level (round 124).** `Pawn_PlayerSettings.medCare` (`:477216`, scribed `:477392`) is the
            // per-pawn None/Doctor/Herbal/…/Best dropdown on the health tab, written in exactly one place - the
            // dropdown's option action (`:477171`), i.e. a player click - and it decides which medicine is used and
            // whether a tend job is refused:
            //   * `Toils_Recipe`'s ingredient search reads it to pick the medicine a surgery bill may consume
            //     (`:311991-311995`), which is why a doctor uses herbal on the authority;
            //   * `AllowsMedicine` cancels a tend job whose medicine is above the allowed tier (`:280667`, `:280796`).
            // Same shape as the hostility response above and the same defect: the click happens on one peer, the
            // consumption happens where the simulation runs. Measured before this row existed: zero references to
            // `medCare` anywhere in `Source/`.
            Add("m:" + ((int)playerSettings.medCare).ToString(CultureInfo.InvariantCulture));
            // **The rest of `Pawn_PlayerSettings` that a player can change (round 125).** The class has twelve scribed
            // fields (`:477390-477401`); `medCare` and `hostilityResponse` are above, `allowedAreas` is `AreaSync`, and
            // these three are what remained:
            //
            //   * `followDrafted` (`:477210`) and `followFieldwork` (`:477212`) are the "follow drafted" / "follow
            //     fieldwork" checkboxes on an animal's training tab - `Widgets.CheckboxLabeled` inside the listing at
            //     `:648005-648011`, i.e. a player click. Both are read by
            //     `ThinkNode_ConditionalShouldFollowMaster.ShouldFollowMaster`, which decides whether the animal walks
            //     with its master at all.
            //   * `master` (`:477208`) is the master reference, chosen from a float menu
            //     (`TrainableUtility.MasterSelectButton`, `:647996`). It travels as a thing id - **the first reference on
            //     this row**, and the pattern the ledger note asked for.
            //
            // Sent unconditionally for the same reason as `h:`/`m:`: `false` is the scribe default and "no segment"
            // would be indistinguishable from "still false", while clearing a checkbox is a real change.
            Add("d:" + (playerSettings.followDrafted ? "1" : "0"));
            Add("e:" + (playerSettings.followFieldwork ? "1" : "0"));
            Add("r:" + (playerSettings.Master?.thingIDNumber ?? 0).ToString(CultureInfo.InvariantCulture));
        }
        // **Psychic entropy limit (round 142).** `Pawn_PsychicEntropyTracker.limitEntropyAmount` (`:473397`, scribed
        // `:473805`) is the lock button in the top-right of a psycaster's entropy gizmo (`:474043-474046`): the click
        // flips the bool and plays a tick sound, nothing else. It is read by `WouldOverflowEntropy`
        // (`:473640-473641`), which refuses ANY entropy gain while the limit is on, i.e. it decides whether meditation
        // may push neural heat past the cap - and meditation runs where the simulation runs. Toggling it on a replica
        // therefore left the authority clamping on the old answer.
        //
        // Sent unconditionally: the field initialiser is `true`, the scribe default is `false`, and both directions are
        // real player changes, so "no segment" could not be told apart from "still whatever it was".
        if (pawn.psychicEntropy is { } entropy)
            Add("y:" + (entropy.limitEntropyAmount ? "1" : "0"));
        // **Hemogen packs allowed (round 142).** `Gene_Hemogen.hemogenPacksAllowed` (`:727831`, scribed `:727919`) is the
        // checkbox drawn over the hemogen pack icon in the gene gizmo header (`:728189-728198`), flipped by a click.
        // It is read by the hemogen job giver that decides whether a hemogen-dependent pawn may take a pack
        // (`:300783-300800`) and by two ingestion paths (`:310132`, `:310748`) - all on the simulating side.
        //
        // Only sent when the pawn actually carries the gene: a pawn without it has nothing to toggle, so absence means
        // "no such gene" rather than "still true" - the one case where omission is unambiguous.
        if (pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>() is { } hemogen)
            Add("x:" + (hemogen.hemogenPacksAllowed ? "1" : "0"));
        // **Mechanitor control groups (round 143).** A mechanitor's groups carry two player-set values that the
        // authority reads while driving mechs:
        //   * `MechanitorControlGroup.mechRechargeThresholds` (`:463683`, scribed `:463819`) - the two-handle slider in
        //     `Dialog_RechargeSettings` (`:196195` loads it, `:196231` writes it on OK, `:196219` resets it). Read by
        //     `JobGiver_GetEnergy.GetMinAutorechargeThreshold` (`:300438-300442`) and `GetMaxRechargeLimit`
        //     (`:300449-300453`): the energy level a mech goes to charge at, and the level it charges up to.
        //   * `MechanitorControlGroup.WorkMode` (`:463715`, private field `:463714`) - the work mode chosen for the group
        //     (`SetWorkMode`, `:463744-463749`, called from the gizmo's setter at `:462958`). It is not a label: the
        //     setter pushes it onto every assigned mech (`SetWorkModeForPawn`), and `ThinkNode_ConditionalWorkMode`
        //     branches the mech's whole job-giver tree on it. It also gates the recharge slider above through
        //     `UseGroupRechargeLimits` (`:300456-300463`), which reads `WorkMode.ignoreGroupChargeLimits`.
        //
        // Both are per-group and per-mechanitor, so they travel as `k:<组序号>:<workModeDefName|->:<min>:<max>` on the
        // mechanitor's own row - the same row that already carries his player settings. The mechs read the group
        // through `pawn.GetMechControlGroup()` (`:2439`), so nothing has to be sent per mech.
        if (pawn.mechanitor is { } mechanitor)
        {
            for (var groupIndex = 0; groupIndex < mechanitor.controlGroups.Count; groupIndex++)
            {
                var group = mechanitor.controlGroups[groupIndex];
                if (group is null) continue;
                var mode = group.WorkMode?.defName;
                Add("k:" + groupIndex.ToString(CultureInfo.InvariantCulture)
                    + ":" + (string.IsNullOrEmpty(mode) ? "-" : mode)
                    + ":" + group.mechRechargeThresholds.min.ToString("0.####", CultureInfo.InvariantCulture)
                    + ":" + group.mechRechargeThresholds.max.ToString("0.####", CultureInfo.InvariantCulture));
            }
        }
        // Training content: `w:defIndex=steps,defIndex=steps,…` for every trainable whose progress is non-zero, plus
        // `w:defIndex=*` for a completed one. Read through the public API on `Pawn_TrainingTracker` where it exists -
        // `GetWanted`/`HasLearned` are public - and through `steps` only via reflection, because `GetSteps` is
        // `internal` (`:476692-476695`) and therefore invisible to another assembly.
        //
        // This is the half the ledger called 「内容本身还没有通道」: the timers above carry `countDecayFrom` and
        // nothing else, so a replica showed the training *countdown* moving correctly while the ticked boxes and the
        // step counters stayed at whatever the save was loaded with.
        if (pawn.training is { } trainables)
        {
            var progress = new StringBuilder();
            foreach (var def in DefDatabase<TrainableDef>.AllDefsListForReading)
            {
                var steps = ReadMapInt(trainables, "steps", def);
                var learned = trainables.HasLearned(def);
                if (steps <= 0 && !learned) continue;
                if (progress.Length > 0) progress.Append(',');
                // A learned step always travels as its maximum, so the reader does not have to know `td.steps`.
                progress.Append(def.defName).Append('=')
                    .Append(learned ? "*" : steps.ToString(CultureInfo.InvariantCulture));
            }
            if (progress.Length > 0) Add("w:" + progress);
            // **Ordered attack target (round 126).** `Pawn_TrainingTracker.attackTarget` (`:476623`, scribed at `:476644`)
            // is what the two "attack this" gizmos write: the animal's own `AnimalAttackTarget` (`:476929`) and the
            // colonist's `AnimalsAttackTarget`, which fans out over every mastered animal in range (`:477507`). Both are
            // player input - a `Command_Target` action - so on the authority the animal keeps standing there while the
            // clicking peer believes it was ordered. Vanilla's own write is a **pair**: `attackTarget` plus
            // `mindState.enemyTarget` (`:476929-476930`, `:477507-477508`), and its cancel path clears the pair and ends
            // the job (`:476966-476967`) - so the apply side has to reproduce all three, not just the field.
            //
            // Sent only when non-null: 0 is "no order", which is also the state of every pawn that never had one, and
            // unlike the `d:`/`e:`/`h:` rows there is no non-default direction to preserve - clearing it travels as the
            // same row with `a:0`, because the row itself is diffed.
            if (trainables.attackTarget is { } ordered)
                Add("a:" + ordered.thingIDNumber.ToString(CultureInfo.InvariantCulture));
        }
        return builder?.ToString();
    }

    /// <summary>Reads `DefMap&lt;TrainableDef,int&gt;[def]` by name.
    ///
    /// <para>`Pawn_TrainingTracker.GetSteps` is `internal` (`:476692-476695`), so calling it from this assembly is not
    /// possible; the indexer on `DefMap` (`:202967-202979`) is public but the map itself is a private field. Reading the
    /// field and then using the public indexer keeps the arithmetic in the game's own code instead of reimplementing
    /// `values[def.index]` here.</para></summary>
    private static int ReadMapInt(object owner, string name, TrainableDef def)
    {
        var map = Field(owner, name)?.GetValue(owner);
        if (map is null) return 0;
        try
        {
            var indexer = map.GetType().GetProperty("Item", [typeof(TrainableDef)]);
            if (indexer?.GetValue(map, [def]) is int value) return value;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] PawnTrackerSync 读 DefMap 失败：" + error.GetType().Name + ": " + error.Message);
        }
        return 0;
    }

    private static int Quantise(int ticks) => ticks / TickQuantum * TickQuantum;

    internal static void Apply(ReplicaPawnTracker state)
    {
        try
        {
            if (state.Trackers.Length == 0)
            {
                // "This pawn has nothing left to say": the counters are already zeros on this side, since the replica never
                // advanced them either. Counted so the report can show the clear was delivered.
                TrackersApplied++;
                return;
            }
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn) || pawn is null) return;
            foreach (var field in state.Trackers.Split(';'))
            {
                if (field.Length < 3) continue;
                var body = field[2..].Split(':');
                switch (field[0])
                {
                    case 'g':
                        if (pawn.guilt is not { } guilt) break;
                        if (int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var guilty))
                            WriteInt(guilt, "guiltyTicksLeft", guilty);
                        break;
                    case 't':
                        if (pawn.training is not { } training) break;
                        // Back to an absolute stamp, computed against this peer's own clock: the two clocks are the same
                        // logical tick, so `remaining` means the same thing on both sides.
                        if (int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
                            WriteInt(training, "countDecayFrom",
                                Find.TickManager.TicksGame - TrainableUtility.DegradationPeriodTicks(pawn) + remaining);
                        break;
                    case 'l':
                        if (pawn.learning is not { } learning) break;
                        if (int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var desire))
                            WriteInt(learning, "newLearningDesireTicksLeft", desire);
                        break;
                    case 'd':
                        if (pawn.playerSettings is not { } followSettings) break;
                        followSettings.followDrafted = body[0] == "1";
                        AnimalFollowRows++;
                        break;
                    case 'e':
                        if (pawn.playerSettings is not { } fieldworkSettings) break;
                        fieldworkSettings.followFieldwork = body[0] == "1";
                        break;
                    case 'r':
                        if (pawn.playerSettings is not { } masterSettings) break;
                        if (!int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var masterId))
                            break;
                        // Through the property, not the field: the setter refuses a non-obedient pawn and ends a job the
                        // old master's follow was driving (`:477232-477256`). Writing the field would skip both.
                        if (masterId <= 0)
                        {
                            masterSettings.Master = null;
                            break;
                        }
                        // **`pawn.training` must exist before the setter may be used.** `Pawn_PlayerSettings.Master`'s
                        // setter calls `pawn.training.HasLearned(TrainableDefOf.Obedience)` (`:477244`) and a human pawn
                        // has no `Pawn_TrainingTracker` at all - so assigning a master to a colonist throws
                        // `NullReferenceException`. Measured: the first version of `follow-probe` set a master on a
                        // colonist and the whole command died. A human never has a master in vanilla either, so the
                        // guard is the same rule the game applies rather than a workaround.
                        if (pawn.training is null)
                        {
                            MasterUnresolved++;
                            break;
                        }
                        if (AuthoritativeReplica.FindPawnAny(masterId, out var master) && master is not null
                            && master != pawn)
                            masterSettings.Master = master;
                        else
                            // Counted, not silent: a master id this peer has never seen is a resolution failure, and
                            // "the animal does not follow" is exactly what it looks like from the player's side.
                            MasterUnresolved++;
                        break;
                    case 'm':
                        if (pawn.playerSettings is not { } careSettings) break;
                        if (!int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var care)) break;
                        careSettings.medCare = (MedicalCareCategory)care;
                        MedicalCareRows++;
                        break;
                    case 'h':
                        if (pawn.playerSettings is not { } settings) break;
                        if (!int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode)) break;
                        // `HostilityResponseMode` is a plain enum and the field is public, so no reflection: the value
                        // travels as its integer only because the wire should not depend on enum ordering.
                        settings.hostilityResponse = (HostilityResponseMode)mode;
                        HostilityRows++;
                        break;
                    case 'o':
                        if (pawn.guilt is not { } guiltOrder) break;
                        // A pure bool flip: the button itself plays a message on the clicking peer only
                        // (`:642593-642596`), and that message belongs to whoever clicked.
                        WriteBool(guiltOrder, "awaitingExecution", body[0] == "1");
                        GuiltExecutionRows++;
                        break;
                    case 'b':
                        if (body.Length < 1 || !int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out var namingDeadline)) break;
                        if (pawn.babyNamingDeadline == namingDeadline) break;
                        pawn.babyNamingDeadline = namingDeadline;
                        NamingDeadlineRows++;
                        break;
                    case 'y':
                        if (pawn.psychicEntropy is not { } entropySettings) break;
                        // Nothing to reproduce beyond the bool: the click handler only flips it and plays a tick sound
                        // (`:474043-474046`), and the sound belongs to the peer that clicked.
                        entropySettings.limitEntropyAmount = body[0] == "1";
                        EntropyLimitRows++;
                        break;
                    case 'x':
                        if (pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>() is not { } hemogenGene) break;
                        // Same shape: a pure bool flip (`:728192-728198`).
                        hemogenGene.hemogenPacksAllowed = body[0] == "1";
                        HemogenPackRows++;
                        break;
                    case 'k':
                        if (pawn.mechanitor?.controlGroups is not { } controlGroups) break;
                        if (body.Length < 4) break;
                        if (!int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupAt)) break;
                        if (groupAt < 0 || groupAt >= controlGroups.Count) break;
                        var mechGroup = controlGroups[groupAt];
                        if (mechGroup is null) break;
                        // Through the vanilla setter, not the field: it is what pushes the mode onto every assigned mech
                        // and clears the cached tag list (`:463749-463763`). Writing `workMode` directly would leave each
                        // mech's think tree on the old mode.
                        if (body[1] != "-")
                        {
                            var modeDef = DefDatabase<MechWorkModeDef>.GetNamedSilentFail(body[1]);
                            if (modeDef is null) MechWorkModeUnresolved++;
                            else mechGroup.SetWorkMode(modeDef);
                        }
                        if (float.TryParse(body[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var rechargeMin)
                            && float.TryParse(body[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rechargeMax))
                            mechGroup.mechRechargeThresholds = new FloatRange(rechargeMin, rechargeMax);
                        MechSettingsRows++;
                        break;
                    case 'f':
                        if (pawn.flight is not { } flight || body.Length < 3) break;
                        if (int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flightState))
                            WriteInt(flight, "flightState", flightState);
                        if (int.TryParse(body[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lerp))
                            WriteInt(flight, "lerpTick", lerp);
                        if (int.TryParse(body[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown))
                            WriteInt(flight, "flightCooldownTicks", cooldown);
                        break;
                    case 'a':
                        if (pawn.training is not { } orderedTracker) break;
                        if (!int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId))
                            break;
                        if (targetId <= 0)
                        {
                            if (orderedTracker.attackTarget is null) break;
                            orderedTracker.attackTarget = null;
                            // The cancel gizmo also interrupts the melee job (`:476967`). Without it the animal keeps
                            // walking to a target its own side no longer records.
                            pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                            AttackTargetsCleared++;
                            break;
                        }
                        if (AuthoritativeReplica.FindThing(targetId) is not { } orderedTarget) { AttackTargetUnresolved++; break; }
                        orderedTracker.attackTarget = orderedTarget;
                        // The second half of vanilla's pair: without `enemyTarget` the melee job the order starts has
                        // nothing to chase, because `Pawn_MindState.enemyTarget` is what the job giver reads.
                        if (pawn.mindState is { } mind) mind.enemyTarget = orderedTarget;
                        AttackTargetsApplied++;
                        break;
                    case 'w':
                        if (pawn.training is not { } trainable) break;
                        // Deliberately writes `steps`/`learned` and NOT `wantedTrainables`: the wanted flags are the
                        // player's checkbox, they travel as their own command (`training-wanted`) because a checkbox
                        // click has no other capture, and writing them from a per-frame timer row would undo a click the
                        // player made a moment earlier on this peer.
                        foreach (var entry in field[2..].Split(','))
                        {
                            var split = entry.IndexOf('=');
                            if (split <= 0) continue;
                            var def = DefDatabase<TrainableDef>.GetNamedSilentFail(entry[..split]);
                            if (def is null) continue;
                            var text = entry[(split + 1)..];
                            var steps = text is "*" ? def.steps : int.TryParse(text, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                            WriteMapInt(trainable, "steps", def, steps);
                            WriteMapBool(trainable, "learned", def, text is "*");
                            TrainingSteps++;
                        }
                        TrainingRows++;
                        break;
                }
            }
            TrackersApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    /// <summary>One reflection cache for every counter above, keyed by declaring type and field name. A renamed field fails
    /// once here - with a warning naming it - rather than silently reading nothing forever.</summary>
    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> fields = [];

    private static System.Reflection.FieldInfo? Field(object owner, string name)
    {
        var type = owner.GetType();
        if (fields.TryGetValue((type, name), out var known)) return known;
        var found = HarmonyLib.AccessTools.Field(type, name)
            // `AccessTools.Field` only searches the declared type; these are all declared on the tracker itself, so a miss
            // means the field really is gone.
            ;
        fields[(type, name)] = found;
        if (found is null) Log.Warning($"[TurboTick] PawnTrackerSync 找不到字段 {type.Name}.{name}");
        return found;
    }

    private static int? ReadInt(object? owner, string name) =>
        owner is not null && Field(owner, name)?.GetValue(owner) is int value ? value : null;

    /// <summary>Bool counterpart of <see cref="ReadInt"/>. Needed because `awaitingExecution` is a `bool` field: reading it
    /// through `ReadInt` returned null (so the segment always encoded 0) and writing it through `WriteInt` threw
    /// `ArgumentException` inside `Apply`'s catch-all - which would have swallowed the rest of the row (round 144).</summary>
    private static bool? ReadBool(object? owner, string name) =>
        owner is not null && Field(owner, name)?.GetValue(owner) is bool value ? value : null;

    /// <summary>Fixture hook: sets the guilt countdown through the same reflection cache the channel reads it with.
    ///
    /// <para>`guiltyTicksLeft` is private, and `IsGuilty` (`:479231`) is a pure function of it, so a probe that wants to
    /// measure the `o:` segment on a pawn who is actually guilty (`:642584` - the execute button only exists then) has
    /// no public way in. Going through <see cref="WriteInt"/> means the fixture and the channel agree on the field by
    /// construction; a renamed field fails in the same warning.</para></summary>
    internal static bool SetGuiltyTicksLeft(Pawn? pawn, int ticks)
    {
        try
        {
            if (pawn?.guilt is not { } guilt) return false;
            WriteInt(guilt, "guiltyTicksLeft", ticks);
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 设置有罪倒计时失败：" + error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    private static void WriteBool(object? owner, string name, bool value)
    {
        if (owner is not null) Field(owner, name)?.SetValue(owner, value);
    }

    private static void WriteInt(object? owner, string name, int value)
    {
        if (owner is not null) Field(owner, name)?.SetValue(owner, value);
    }

    /// <summary>Writes `DefMap&lt;TrainableDef,int&gt;[def]` through the public indexer setter (`:202967-202979`).</summary>
    private static void WriteMapInt(object owner, string name, TrainableDef def, int value)
    {
        var map = Field(owner, name)?.GetValue(owner);
        if (map is null) return;
        try { map.GetType().GetProperty("Item", [typeof(TrainableDef)])?.SetValue(map, value, [def]); }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] PawnTrackerSync 写 DefMap 失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void WriteMapBool(object owner, string name, TrainableDef def, bool value)
    {
        var map = Field(owner, name)?.GetValue(owner);
        if (map is null) return;
        try { map.GetType().GetProperty("Item", [typeof(TrainableDef)])?.SetValue(map, value, [def]); }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] PawnTrackerSync 写 DefMap 失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Reports what the training half actually encodes, for every animal in the colony.
    ///
    /// <para><b>Why a probe and not just the census line.</b> `训练 0 个有进度、0 条` has two causes that read
    /// identically: no animal has any training, or the `w:` encoder never produces anything. The second is what the
    /// ledger called 「内容本身还没有通道」, and it would look like a quiet, healthy colony. So this prints the encoded
    /// text itself, per animal, beside the values read straight off the tracker - the two must agree, and the comparison
    /// is what proves the encoder rather than the colony.</para></summary>
    internal static string Probe(string? argument)
    {
        var animals = 0;
        var withRows = 0;
        var lines = new List<string>();
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.training is null) continue;
                animals++;
                var encoded = Encode(pawn);
                var training = encoded is null ? null
                    : string.Join(";", encoded.Split(';').Where(f => f.StartsWith("w:", StringComparison.Ordinal)));
                if (training is not null) withRows++;
                // Only animals with something to say are listed; a colony of 200 wild animals would otherwise bury the
                // few rows that matter.
                if (training is not null && lines.Count < 12)
                    lines.Add($"{pawn.LabelShort}#{pawn.thingIDNumber} {training}（HasLearned 真值 "
                        + CountLearned(pawn) + " 条，steps 合计 " + CountSteps(pawn) + "）");
            }
        // Round-trip the first animal that has a row: apply its own encoding back onto it and re-encode. Applying a
        // value it already holds must be a no-op, so a mismatch means the writer and the reader disagree - which is the
        // one failure a census count can never show, because `应用 N` only proves Apply ran.
        var roundTrip = "未测（没有带训练进度的动物）";
        foreach (var map in Find.Maps)
        {
            var done = false;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.training is null) continue;
                var encoded = Encode(pawn);
                var row = encoded is null ? null
                    : string.Join(";", encoded.Split(';').Where(f => f.StartsWith("w:", StringComparison.Ordinal)));
                if (row is null) continue;
                var before = row;
                Apply(new ReplicaPawnTracker(pawn.thingIDNumber, row));
                var after = Encode(pawn);
                var again = after is null ? null
                    : string.Join(";", after.Split(';').Where(f => f.StartsWith("w:", StringComparison.Ordinal)));
                roundTrip = before == again
                    ? $"一致（{before} 回灌后仍是 {again}）"
                    : $"**不一致**（写入 {before}，读回 {again}）";
                done = true;
                break;
            }
            if (done) break;
        }

        var text = $"有 training 组件的小人 {animals} 个，其中 {withRows} 个编码出 w: 行；幂等往返 {roundTrip}";
        if (lines.Count == 0)
            // Said plainly rather than as "0 rows": this is the reading that must not be mistaken for a working encoder.
            text += "；**没有任何动物有训练进度**（新殖民地或全是未驯化野生动物时如此，需驯化/训练后重测）";
        else text += "；样本：" + string.Join(" | ", lines);
        text += $"；发 {TrackersSent} / 应用 {TrackersApplied} / 训练行 {TrainingRows}（{TrainingSteps} 条）";
        return text;
    }

    private static int CountLearned(Pawn pawn)
    {
        var count = 0;
        foreach (var def in DefDatabase<TrainableDef>.AllDefsListForReading)
            if (pawn.training.HasLearned(def)) count++;
        return count;
    }

    private static int CountSteps(Pawn pawn)
    {
        var count = 0;
        foreach (var def in DefDatabase<TrainableDef>.AllDefsListForReading)
            count += ReadMapInt(pawn.training, "steps", def);
        return count;
    }

    internal static string Summary()
    {
        try
        {
            var guilty = 0;
            var decaying = 0;
            var desiring = 0;
            var airborne = 0;
            var hostilityOther = 0;
            var careOther = 0;
            var followAny = 0;
            var mastered = 0;
            var ordered = 0;
            // Two gizmo toggles whose interesting reading is "how many are NOT at the default": the entropy lock and the
            // hemogen pack checkbox both default to true, so counting trues would be a constant.
            var awaitingExecution = 0;
            var namingPending = 0;
            var mechGroups = 0;
            var mechanitors = 0;
            var entropyOpen = 0;
            var hemogenOff = 0;
            var total = 0;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is null) continue;
                    total++;
                    if ((ReadInt(pawn.guilt, "guiltyTicksLeft") ?? 0) > 0) guilty++;
                    // The row is only meaningful while the period has not elapsed; afterwards `TrainingTrackerTickRare` is due to
            // fire, so counting those would report an animal that has already lost the training.
            if ((ReadInt(pawn.training, "countDecayFrom") ?? 0) > 0
                && (ReadInt(pawn.training, "countDecayFrom") ?? 0)
                    + TrainableUtility.DegradationPeriodTicks(pawn) > Find.TickManager.TicksGame) decaying++;
                    if ((ReadInt(pawn.learning, "newLearningDesireTicksLeft") ?? 0) > 0) desiring++;
                    // A pending naming deadline is a state a player asked for (or a birth asked for) that no other
                    // census column can show.
                    if (pawn.babyNamingDeadline >= 0) namingPending++;
                    if ((ReadInt(pawn.flight, "flightState") ?? 0) != 0) airborne++;
                    // Hostility response, counted by mode: `Flee` is the default so the two non-defaults are what a
                    // player deliberately set, and this line is the only way to see it without pressing the dropdown.
                    if (pawn.playerSettings is { } settings && settings.hostilityResponse != HostilityResponseMode.Flee)
                        hostilityOther++;
                    // `NoMeds` is the field initialiser (`:477216`) and `NoCare` the scribe default (`:477392`); neither
                    // is a level a doctor acts on, so anything else is a level the player chose.
                    if (pawn.playerSettings is { } settings2
                        && settings2.medCare != MedicalCareCategory.NoCare
                        && settings2.medCare != MedicalCareCategory.NoMeds)
                        careOther++;
                    // The animal-follow flags and masters. Counted because a colony with no trained animals and one
                    // with thirty look identical in every other census row.
                    if (pawn.playerSettings is { } followSettings
                        && (followSettings.followDrafted || followSettings.followFieldwork))
                        followAny++;
                    if (pawn.playerSettings?.Master is not null) mastered++;
                    // Ordered attack targets: a state that only exists because someone clicked a gizmo, and the one
                    // thing on this row that has an observable consequence on the other side (the animal walks off).
                    if (pawn.training?.attackTarget is not null) ordered++;
                    // The two gizmo toggles added this round. `limitEntropyAmount` defaults to true, so "how many are
                    // LIMITING" is the interesting count; the hemogen checkbox also defaults to true.
                    if (pawn.psychicEntropy is { limitEntropyAmount: false }) entropyOpen++;
                    if (ReadBool(pawn.guilt, "awaitingExecution") ?? false) awaitingExecution++;
                    if (pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>() is { hemogenPacksAllowed: false }) hemogenOff++;
                    // Mechanitor groups: how many groups exist at all, so a colony with a mechanitor whose settings
                    // never travel is not mistaken for a colony with no mechanitor.
                    if (pawn.mechanitor?.controlGroups is { Count: > 0 } groups)
                    {
                        mechanitors++;
                        mechGroups += groups.Count;
                    }
                }
            // Training CONTENT, not just its countdown: how many animals have any ticked box or any progress. Without
            // this line the channel read as healthy while `w:` was absent - the four timers can all be quiet on a
            // perfectly working colony, and that reading is identical to "the content half never encodes".
            var training = 0;
            var progress = 0;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.training is null) continue;
                    var any = false;
                    foreach (var def in DefDatabase<TrainableDef>.AllDefsListForReading)
                    {
                        if (ReadMapInt(pawn.training, "steps", def) <= 0 && !pawn.training.HasLearned(def)) continue;
                        any = true;
                        progress++;
                    }
                    if (any) training++;
                }
            if (guilty == 0 && decaying == 0 && desiring == 0 && airborne == 0)
                return $"0 个有罪 / 0 个训练倒计时 / 0 个学习欲 / 0 个在飞（{total} 个小人全部静止）"
                    + $" / 训练 {training} 个有进度、{progress} 条 / 敌意反应 {hostilityOther} 个非默认"
                    + $"（应用 {HostilityRows} 行）/ 医疗等级 {careOther} 个非默认（应用 {MedicalCareRows} 行）"
                    + $" / 跟随 {followAny} 个开、主人 {mastered} 个（跟随应用 {AnimalFollowRows} 行，主人未解析 {MasterUnresolved}）"
                    + $" / 熵限 {entropyOpen} 个解除（应用 {EntropyLimitRows} 行）/ 血原包 {hemogenOff} 个不许（应用 {HemogenPackRows} 行）"
                    + $" / 机械组 {mechGroups} 组（{mechanitors} 个机械师，写入 {MechSettingsRows} 段，work mode 未解析 {MechWorkModeUnresolved}）"
                    + $" / 待处决 {awaitingExecution} 个（应用 {GuiltExecutionRows} 行）"
                    + $" / 待命名 {namingPending} 个（应用 {NamingDeadlineRows} 行）"
                    + $" / 攻击目标 {ordered} 个（应用 {AttackTargetsApplied}、清除 {AttackTargetsCleared}、未解析 {AttackTargetUnresolved}）"
                    + $" / 发 {TrackersSent} / 训练行 {TrainingRows}";
            return $"{guilty} 个有罪 / {decaying} 个训练倒计时 / {desiring} 个学习欲 / {airborne} 个在飞"
                + $"（共 {total} 个）/ 训练 {training} 个有进度、{progress} 条 / 敌意反应 {hostilityOther} 个非默认"
                + $"（应用 {HostilityRows} 行）/ 医疗等级 {careOther} 个非默认（应用 {MedicalCareRows} 行）"
                + $" / 跟随 {followAny} 个开、主人 {mastered} 个（跟随应用 {AnimalFollowRows} 行，主人未解析 {MasterUnresolved}）"
                + $" / 熵限 {entropyOpen} 个解除（应用 {EntropyLimitRows} 行）/ 血原包 {hemogenOff} 个不许（应用 {HemogenPackRows} 行）"
                + $" / 机械组 {mechGroups} 组（{mechanitors} 个机械师，写入 {MechSettingsRows} 段，work mode 未解析 {MechWorkModeUnresolved}）"
                + $" / 待处决 {awaitingExecution} 个（应用 {GuiltExecutionRows} 行）"
                    + $" / 待命名 {namingPending} 个（应用 {NamingDeadlineRows} 行）"
                + $" / 攻击目标 {ordered} 个（应用 {AttackTargetsApplied}、清除 {AttackTargetsCleared}、未解析 {AttackTargetUnresolved}）"
                + $" / 发 {TrackersSent}"
                + $" / 训练行 {TrainingRows}（{TrainingSteps} 条）/ 样本 {LastSample ?? "(无)"}";
        }
        catch (Exception error) { return "读取失败（" + error.GetType().Name + "）"; }
    }
}
