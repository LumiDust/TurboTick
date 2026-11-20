using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboTick.Networking;

namespace TurboTick.Simulation;

/// <summary>
/// Wire format for player actions. Encodes every player-issued action into a compact payload:
/// encode → server order → decode and replay at a fixed tick on all peers.
/// </summary>
public static class CommandCodec
{
    public const string Designation = "designation";
    public const string RemoveDesignation = "remove-designation";
    public const string Blueprint = "blueprint";
    public const string CancelThing = "cancel-thing";
    public const string Draft = "draft";
    public const string Order = "order";

    /// <summary>Ending a pawn's current job because the *player* asked it to (round 127).
    ///
    /// <para><b>The gap.</b> Orders travel as <see cref="Order"/>: `OrderCapturePatch` sits on
    /// `Pawn_JobTracker.TryTakeOrderedJob` (`:2456`), which is where `PawnGotoAction` (`:657768`) hands a "go here"
    /// click over. But `PawnGotoAction` has an earlier branch (`:657745-657751`) that never reaches that method:
    /// when the clicked cell is the cell the pawn already stands on and its current job is `Goto`, it calls
    /// `pawn.jobs.EndCurrentJob(JobCondition.Succeeded)` - "stop where you are". Nothing captured that, so on a
    /// replica the pawn stopped and on the side that actually moves it never did. `EndCurrentJob` is also what the
    /// "cancel" gizmo of an ordered job reaches, so the same hole is wider than the one click that found it.</para>
    ///
    /// <para>Payload is <c>pawnId|condition</c>. The condition travels as its integer for the same reason the
    /// hostility mode does: the wire should not depend on enum ordering. It is not always `Succeeded` - a forced
    /// cancel arrives as `InterruptForced`, and the two leave the job system in different states.</para></summary>
    public const string Stop = "stop";
    public const string Transport = "transport";
    public const string Forbid = "forbid";
    /// <summary>Loading a `MapPortal` (odyssey's cross-map gate). Not `Transport`: the receiver is a
    /// different class with a same-named method and its own `leftToLoad`.</summary>
    public const string Portal = "portal";

    /// <summary>Many forbid flips in one command.
    ///
    /// Measured: one bulk "allow/forbid" gesture on a full stockpile published a separate <see cref="Forbid"/> per
    /// stack, the server's per-peer budget (400 commands per second) refused the tail, and because the capture had
    /// already cancelled each local assignment the player saw the whole operation do nothing at all. The budget is
    /// there so one client cannot flood the relay, and that part is right; what was wrong is that a single gesture
    /// became hundreds of commands. A bulk edit is one action, so it travels as one command.
    ///
    /// Payload is <c>flag|id,id,id</c>. Ids are capped by <see cref="MaxBatchIds"/> so the payload stays far below
    /// both the sender's 4096-byte decode guard and the server's 64 KB payload ceiling.</summary>
    public const string ForbidBatch = "forbid-batch";

    /// <summary>512 ids fit in about 3.6 KB of payload. A larger set is several gestures, not one.</summary>
    public const int MaxBatchIds = 512;

    public const string Priority = "priority";
    public const string ZoneCell = "zone-cell";
    public const string ZoneDelete = "zone-delete";

    /// <summary>One paint stroke on a player-created allowed area. Its own kind rather than riding on
    /// <see cref="Designator"/>, which is an entity-lifecycle kind and is skipped by a display replica
    /// (<see cref="IsEntityLifecycle"/>, <c>AuthoritativeReplica.AcceptsCommand</c>).
    ///
    /// <para>The stroke sets or clears bits on an area that already exists, so it creates no object and allocates no id
    /// - exactly the situation <see cref="CancelDrag"/> was moved out of the lifecycle set for. Leaving it on
    /// <see cref="Designator"/> would have published the command, logged nothing, and been dropped by every replica.</para>
    ///
    /// <para>Payload is <c>label|add|map|x,z|x,z…</c>. Deliberately <em>not</em> in the lifecycle set below: the
    /// stroke only sets bits on an area that already exists, so a replica must apply it - and nothing else carries those
    /// bits, because `Designator_AreaAllowedExpand`/`Clear` publish no other command.</para></summary>
    public const string AreaPaint = "area-paint";

    /// <summary>One paint stroke: <c>label|add|map|x,z|x,z…</c>.
    ///
    /// <para>The label is carried raw rather than escaped. Vanilla names an area from a text field
    /// (<c>Dialog_Rename.MaxNameLength</c>), and a player can type a <c>|</c> into it - which would split the payload.
    /// The stroke is therefore refused at the source (<see cref="DesignatorSync.EncodeArea"/>) rather than corrupted on
    /// the wire, and the decoder checks the same condition so the two peers agree about what is legal.</para></summary>
    public static GameCommand AreaPaintCommand(string label, bool add, int map, IReadOnlyList<(int X, int Z)> cells, long sequence)
    {
        var text = new StringBuilder(label.Length + 16 + cells.Count * 8);
        text.Append(label).Append('|').Append(add ? '1' : '0').Append('|').Append(map);
        foreach (var (x, z) in cells) text.Append('|').Append(x).Append(',').Append(z);
        return new(sequence, 0, AreaPaint, Encoding.UTF8.GetBytes(text.ToString()));
    }

    public static bool TryDecodeAreaPaint(byte[] payload, out string label, out bool add, out int map,
        out List<(int X, int Z)> cells)
    {
        label = "";
        add = false;
        map = 0;
        cells = [];
        // Not `TryDecodeAtLeast`: that helper caps the payload at 4 KiB, which is right for the value-shaped commands it
        // was written for and wrong here. A stroke carries `DesignatorSync.MaxCells` = 6000 cells at up to 8 bytes each,
        // so a legal payload reaches ~48 KiB - the same ceiling `TryDecodeThingState` had to raise to 64 KiB. Using the
        // 4 KiB helper would have made every large stroke decode as `false` and, if it were ever wired into a validating
        // path, adaptively reject exactly the drags worth carrying.
        if (payload is null || payload.Length < 5 || payload.Length > 64 * 1024) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length < 4) return false;
        if (parts[0].Length == 0) return false;
        if (parts[1] is not ("0" or "1")) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out map)) return false;
        add = parts[1] is "1";
        for (var i = 3; i < parts.Length; i++)
        {
            var comma = parts[i].IndexOf(',');
            if (comma <= 0) continue;
            if (!int.TryParse(parts[i].AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) continue;
            if (!int.TryParse(parts[i].AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)) continue;
            cells.Add((x, z));
        }
        label = parts[0];
        return cells.Count > 0;
    }
    public const string BillAdd = "bill-add";

    /// <summary>One surgery bill, as a whole `Bill_Medical`.
    ///
    /// <para><b>Why `bill-add` cannot carry this.</b> `HealthCardUtility.CreateSurgeryBill` (`:644616-644623`) does
    /// three things in a fixed order: `new Bill_Medical(recipe, ingredients)`, then `BillStack.AddBill`, then
    /// <c>Bill.Part = part</c> - and the setter **refuses** to run before the bill is in a stack
    /// (<c>:442400-442402</c>: <c>if (billStack == null &amp;&amp; part != null) Log.Error("Can only set
    /// Bill_Medical.Part after the bill has been added to a pawn's bill stack.")</c>). `bill-add` is a prefix on
    /// `AddBill` that cancels the local call, so on the peer that clicked, `billStack` stays null, the part is never
    /// assigned, and every surgery bill logs that error and arrives with no body part. Measured consequence: the
    /// replica's bill says "手术" with no target, and `CompletableEver` (`:442369-442373`) returns false because
    /// <c>GetPartsToApplyOn</c> cannot contain a null part.</para>
    ///
    /// <para>The fragment is `Bill_Medical.ExposeData` (`:442541-442565`), which scribes exactly `part`,
    /// `consumedMedicine`, `uniqueRequiredIngredients` and `iterationCompleted` on top of `Bill`'s own fields - so a
    /// whole fragment also carries the ingredients a mod added, and does not need a field list kept in sync. Payload is
    /// <c>pawnThingId|base64(billXml)</c>, and the receiver runs the game's own `Scribe` load.</para></summary>
    public const string SurgeryBill = "surgery-bill";

    public static GameCommand SurgeryBillCommand(int pawnId, string recipeDefName, string billXml, long sequence,
        int replaces = -1) =>
        new(sequence, 0, SurgeryBill,
            Encoding.UTF8.GetBytes($"{pawnId}|{recipeDefName}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(billXml))}"
                + $"|{replaces.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>`replaces` is the index of an existing medical bill this one supersedes, or -1 for "add".
    ///
    /// <para><b>Why a surgery bill can replace rather than only add.</b> `Xenogerm.SetTargetPawn` (`:596888-596897`)
    /// re-targets an implant: it finds the bill whose <c>xenogerm</c> is this item on the old target and deletes it,
    /// then creates one on the new target. Deleting locally alone would leave the old target still holding a bill on
    /// every other peer, so the replacement has to be expressible as one ordered command - delete-by-index first, then
    /// add - rather than as two commands a reordering could invert.</para></summary>
    public static bool TryDecodeSurgeryBill(byte[] payload, out int pawnId, out string recipeDefName,
        out string? billXml, out int replaces)
    {
        pawnId = 0;
        recipeDefName = "";
        billXml = null;
        replaces = -1;
        // `TryDecodeAtLeast`, not `TryDecode`: the fourth field is optional, and a three-part payload is an older peer
        // that must still decode (rejecting it loses the whole bill).
        //
        // And NOT the shared helper's 4 KiB ceiling: a `Bill_Medical` fragment carries `consumedMedicine`,
        // `uniqueRequiredIngredients` and everything `Bill.ExposeData` writes (`:272306-272340`), including the whole
        // `ingredientFilter`. The same mistake was fixed for `area-paint` in the previous round - a legal payload was
        // being rejected by a limit that belonged to the value-shaped commands.
        if (payload is null || payload.Length < 6 || payload.Length > 64 * 1024) return false;
        var p = Encoding.UTF8.GetString(payload).Split('|');
        if (p.Length < 3) return false;
        if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pawnId)) return false;
        if (p[1].Length == 0 || p[2].Length == 0) return false;
        if (p.Length > 3 && !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out replaces))
            return false;
        try { billXml = Encoding.UTF8.GetString(Convert.FromBase64String(p[2])); }
        catch (FormatException) { return false; }
        recipeDefName = p[1];
        return pawnId > 0;
    }
    public const string BillDelete = "bill-delete";
    public const string AreaAssign = "area-assign";

    /// <summary>One training-tab checkbox: an animal should (or should no longer) be trained in a skill.
    ///
    /// <para><b>Why this needs its own command.</b> The checkbox is `TrainingCardUtility.DoTrainableCheckbox`
    /// (`:648116-648137`) writing <c>Pawn_TrainingTracker.SetWantedRecursive</c> (`:476801`) - a static widget, not a
    /// gizmo and not a designator, so `GizmoStatePatch` never sees it. Before this kind existed the click was purely
    /// local: the authority kept hunting with a handler the player had unticked, or refused to train one they had
    /// ticked, and the only visible sign was a checkbox that looked right on the peer that was not simulating.</para>
    ///
    /// <para>Payload is <c>pawnId|trainableDefName|1|0</c>. The def name rather than an index: `DefMap` is indexed by
    /// `def.index`, which depends on the loaded mod set.</para></summary>
    public const string TrainingWanted = "training-wanted";

    public static GameCommand TrainingWantedCommand(int pawnId, string trainableDef, bool wanted, long sequence) =>
        new(sequence, 0, TrainingWanted,
            Encoding.UTF8.GetBytes($"{pawnId}|{trainableDef}|{(wanted ? 1 : 0)}"));

    public static bool TryDecodeTrainingWanted(byte[] payload, out int pawnId, out string trainableDef, out bool wanted)
    {
        pawnId = 0;
        trainableDef = "";
        wanted = false;
        if (!TryDecode(payload, 3, out var p) || !int.TryParse(p[0], out pawnId)) return false;
        if (p[2] is not ("0" or "1")) return false;
        trainableDef = p[1];
        wanted = p[2] is "1";
        return pawnId > 0 && trainableDef.Length > 0;
    }

    /// <summary>A pawn's policy assignment: apparel, drugs, food or timetable. One kind covers all four because the
    /// payload is the pawn's whole policy bundle rather than one field.
    ///
    /// <para>These are the panels with no gizmo: the assign buttons live in <c>ITab_Pawn_Gear</c> /
    /// <c>MainTabWindow_Assign</c>, they write <c>Pawn_OutfitTracker.CurrentApparelPolicy</c>,
    /// <c>Pawn_DrugPolicyTracker.CurrentPolicy</c>, <c>Pawn_FoodRestrictionTracker.CurrentFoodPolicy</c> and
    /// <c>Pawn_TimetableTracker.SetAssignment</c> - four different shapes, none of which any gizmo patch can see.
    /// ColonyTogether covers the same ground with <c>SyncDictRimWorld</c> entries for each tracker; we send the four
    /// trackers' own Scribe XML, so a modded policy field travels too.</para></summary>
    public const string Policy = "policy";
    public const string Bills = "bills";
    public const string Storyteller = "storyteller";
    public const string LetterChoice = "letter-choice";
    public const string GrowthChoice = "growth-choice";

    /// <summary>One of the three buttons in <c>Dialog_ManageAreas</c>: invert, clear or delete an area.
    ///
    /// <para>Drawing an area goes through <c>Designator_AreaAllowed</c>, which is a <c>Designator</c> and therefore
    /// already covered. These three are not: they are plain method calls from a window
    /// (<c>RimWorld/Dialog_ManageAreas.cs:89</c> <c>area.Invert()</c>, <c>:111</c> <c>area.Delete()</c>), and a player
    /// who uses them changes only their own map while everybody else keeps the old shape.</para>
    ///
    /// <para>Payload is <c>op|areaId</c>, op ∈ <c>invert</c> / <c>clear</c> / <c>delete</c>. The cells are not carried:
    /// invert and clear are defined as operations on the existing grid, and recomputing them on every peer from the same
    /// starting state is both smaller and exactly reproducible.</para></summary>
    public const string AreaOp = "area-op";

    public static GameCommand AreaOpCommand(string op, int areaId, long sequence) =>
        new(sequence, 0, AreaOp, Encoding.UTF8.GetBytes($"{op}|{areaId}"));

    public static bool TryDecodeAreaOp(byte[] payload, out string op, out int areaId)
    {
        op = "";
        areaId = 0;
        if (!TryDecode(payload, 2, out var p) || !int.TryParse(p[1], out areaId) || areaId < 0) return false;
        op = p[0];
        return op is "invert" or "clear" or "delete";
    }
    /// <summary>Generic fallback for any gizmo button. The listed operations each get their own compact
    /// command, but 1.7 has no single chokepoint for the rest: every gizmo action is a delegate field
    /// (<c>Command_Action.action</c>, <c>Command_Toggle.toggleAction</c>), and the only shared code is
    /// <c>ProcessInput</c>. So the capture takes the target's Scribe state before and after the click and
    /// ships the result, which covers flicking, filters, bed owner, plant-to-grow and anything a third-party
    /// mod adds, without a per-type codec.</summary>
    public const string ThingState = "thing-state";

    /// <summary>A whole <c>Designator</c> action instead of the mutation it performs. Today only the building
    /// designator is encoded, and its payload carries no id, so replaying it on the receiver allocates one
    /// from that receiver's own counter.</summary>
    public const string Designator = "designator";

    /// <summary>A whole cancel drag.
    ///
    /// <para>Separate from <see cref="Designator"/> because of <see cref="IsEntityLifecycle"/>: a buildings drag
    /// creates objects and must be skipped by a display replica, while a cancel drag only removes things that already
    /// exist and clears marks the frame never carries. Sharing one kind meant the replica skipped cancels entirely, so
    /// a mark cleared on the host stayed on the replica until the object itself disappeared.</para>
    ///
    /// <para>Payload is <c>cancel|mapIndex|x,z|x,z|…</c>. The cells are carried because a cell can hold several
    /// cancelable marks and one blueprint at once, and the set of cells the player dragged over is the entire input
    /// to <c>Designator_Cancel</c>.</para></summary>
    public const string CancelDrag = "cancel-drag";

    /// <summary>Kinds whose apply path creates or destroys a <c>Thing</c>, and therefore either draws
    /// an id from <c>UniqueIDsManager</c> or takes an object off the grid.
    ///
    /// A display replica must not run these. It holds no simulation, so an object it creates locally gets an id
    /// from its own counter, while the authoritative frame later delivers the same object under the host's id;
    /// the two counters are then offset, silently, and every later id-based command (cancel, forbid, a job
    /// target) can resolve to a different object on the replica than on the host.
    ///
    /// <para>Everything else in this codec only writes state onto objects that already exist - designations, draft,
    /// orders, forbid, priority, zone cells, bills, areas, research, thing state - and a replica must keep
    /// applying those, because no frame carries them. <see cref="CancelDrag"/> is in that second group despite
    /// looking like a lifecycle action: it destroys nothing the frame does not also report, and it clears the
    /// designations a frame never carries.</para></summary>
    public static bool IsEntityLifecycle(string kind) =>
        kind is Blueprint or CancelThing or Transport or Designator;

    /// <summary>The target thing id is part of the payload because several designations (HarvestPlant,
    /// Hunt, Tame, Mine) are indexed by Thing only. Sending just a cell made the receiver look a location
    /// up in a Thing-keyed index, which throws and faulted the whole simulation.</summary>
    public static GameCommand DesignationCommand(string defName, int map, int x, int z, int targetThingId, long sequence) =>
        new(sequence, 0, Designation, Encoding.UTF8.GetBytes($"{defName}|{map}|{x}|{z}|{targetThingId}"));

    public static GameCommand RemoveDesignationCommand(string defName, int map, int x, int z, int targetThingId, long sequence) =>
        new(sequence, 0, RemoveDesignation, Encoding.UTF8.GetBytes($"{defName}|{map}|{x}|{z}|{targetThingId}"));

    /// <summary>A build placement, including the two style fields the designator writes after placing.
    ///
    /// <para><b>Why the style is in here at all.</b> `Designator_Build.PlaceThing` calls
    /// `GenConstruct.PlaceBlueprintForBuild` (`Assembly-CSharp.decompiled.cs:622503`) and then writes
    /// `blueprint_Build.StyleSourcePrecept` (`:622504`), `blueprint_Build.StyleDef` (`:622507`) and
    /// `blueprint_Build.glowerColorOverride` (`:622509`) - <i>after</i> the call this command is captured on. The
    /// prefix therefore could not see them, and the replica placed a plain blueprint that later became a plain
    /// building through `Blueprint_Build.MakeSolidThing` (`:570393-570398`). Blueprints are not in any of the three
    /// sweeps (`IsBuildingArtificial` is `category == Building || IsFrame`, `:46179`, and a blueprint is neither), so
    /// nothing corrected it afterwards.</para>
    ///
    /// <para>Appended, not inserted: the first seven segments keep their positions so that a peer on the previous
    /// build still parses them and simply sees an unstyled blueprint, which is what it did before.</para>
    ///
    /// <para>`styleOverridden` is deliberately not sent. It records whether the player overrode the precept's style
    /// when copying a building (`BuildCopyCommandUtility.BuildCommand`, `:686326`); it changes the copy gizmo's
    /// label behaviour, not the rendered object. `StyleSourcePrecept` is also not sent - see `ApplyBlueprint`.</para></summary>
    public static GameCommand BlueprintCommand(string buildable, string stuff, int map, int x, int z, int rot, long sequence,
        int thingId = 0, string style = "", string glower = "") =>
        new(sequence, 0, Blueprint,
            Encoding.UTF8.GetBytes($"{buildable}|{stuff}|{map}|{x}|{z}|{rot}|{thingId}|{style}|{glower}"));

    public static GameCommand CancelThingCommand(int thingId, long sequence) =>
        new(sequence, 0, CancelThing, Encoding.UTF8.GetBytes($"{thingId}"));

    /// <summary>A whole cancel drag: the map and every cell the rectangle covered.
    ///
    /// <para>Payload is <c>mapIndex|x,z|x,z|…</c>; the kind already says what to do, so there is no op field. Cells are
    /// plain coordinate pairs rather than <c>IntVec3</c> because this assembly deliberately has no reference to the
    /// game - that is what lets the codec be tested without RimWorld loaded.</para>
    ///
    /// <para>The decoder is separate from <see cref="TryDecode"/>, whose 4096-byte ceiling is for fixed-shape
    /// commands: a rectangle is a list, and a 60-cell drag already exceeds it.</para></summary>
    public static GameCommand CancelDragCommand(int map, IReadOnlyList<(int X, int Z)> cells, long sequence)
    {
        var text = new StringBuilder();
        text.Append(map);
        foreach (var (x, z) in cells) text.Append('|').Append(x).Append(',').Append(z);
        return new(sequence, 0, CancelDrag, Encoding.UTF8.GetBytes(text.ToString()));
    }

    public static bool TryDecodeCancelDrag(byte[] payload, out int map, out List<(int X, int Z)> cells)
    {
        map = 0;
        cells = [];
        if (payload is null || payload.Length is 0 or > MaxDragPayload) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length < 2 || !int.TryParse(parts[0], out map) || map < 0) return false;
        for (var i = 1; i < parts.Length; i++)
        {
            var pair = parts[i].Split(',');
            if (pair.Length != 2) return false;
            if (!int.TryParse(pair[0], out var x) || !int.TryParse(pair[1], out var z)) return false;
            cells.Add((x, z));
        }
        return cells.Count > 0;
    }

    /// <summary>Under the server's own 64 KiB payload ceiling, so this only ever refuses a payload that could not have
    /// travelled anyway.</summary>
    private const int MaxDragPayload = 60000;

    public static GameCommand DraftCommand(int pawnId, bool drafted, long sequence) =>
        new(sequence, 0, Draft, Encoding.UTF8.GetBytes($"{pawnId}|{(drafted ? 1 : 0)}"));

    /// <summary>A job order, carried as the job's own Scribe XML.
    ///
    /// <para>This used to be a hand-written field list (pawn, def, two targets, count) and it kept losing fields
    /// because <c>Job</c> has far more than that: <c>Job.ExposeData</c> (<c>Verse.AI/Job.cs:496-540</c>) writes
    /// <c>targetA</c>, <c>targetB</c>, <c>targetC</c>, <c>globalTarget</c>, <c>targetQueueA</c>,
    /// <c>targetQueueB</c>, <c>count</c>, <c>countQueue</c>, <c>haulMode</c>, <c>locomotionUrgency</c>,
    /// <c>plantDefToSow</c>, <c>thingDefToCarry</c>, <c>bill</c>, <c>placedThings</c>, <c>verbToUse</c> and a dozen
    /// flags. Every omission looked identical on screen ("the colonist takes the order and then does the wrong
    /// thing"), and the worst one was <c>targetC</c>: <see cref="JobDriver_HaulToContainer"/> declares
    /// <c>PrimaryDestIndex = TargetIndex.C</c>, so with C absent the replica rebuilt "bring the stack to B" with no
    /// destination - the pawn carried the materials around and the blueprint never received a single unit.</para>
    ///
    /// <para>Serialising the job itself can never lose a field again: whatever vanilla's <c>ExposeData</c> writes
    /// travels. It also carries <c>loadID</c>, which is right - the host allocated it, and every peer must agree on
    /// it, exactly like a thing id.</para></summary>
    public static GameCommand OrderCommand(int pawnId, string jobXml, long sequence) =>
        new(sequence, 0, Order, Encoding.UTF8.GetBytes($"{pawnId}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(jobXml))}"));

    public static GameCommand StopCommand(int pawnId, int condition, long sequence) =>
        new(sequence, 0, Stop, Encoding.UTF8.GetBytes($"{pawnId}|{condition}"));

    public static GameCommand TransportCommand(int transporterId, int thingId, int count, long sequence) =>
        new(sequence, 0, Transport, Encoding.UTF8.GetBytes($"{transporterId}|{thingId}|{count}"));


    /// <summary>Loading a **portal**, which is a different object from a transporter and a different method.
    ///
    /// <para><b>The defect this fixes.</b> `MapPortal` declares its own `AddToTheToLoadList` (`:571926`) and its own
    /// `leftToLoad` (`:571789`), unrelated to `CompTransporter`'s same-named pair at `:561716` / `:561234`.
    /// `TransportCapturePatch` is attached to `typeof(CompTransporter)`, so `Dialog_EnterPortal.TryAccept`
    /// (`:633225`, which calls `portal.AddToTheToLoadList`) was never captured at all: a player loading a gravship
    /// portal saw the dialog close, the load plan exist on their own machine, and nothing happen on the colony that
    /// simulates. The ledger had this row marked 「绘制/相机/输入」 because the type is named `Dialog_*`, which is the
    /// same name-versus-behaviour trap as `MakeDropPodAt` in round 118.</para>
    ///
    /// <para><b>Same shape as `TransportCommand` on purpose.</b> The receiver looks the thing up by id and calls the
    /// vanilla method, so what travels is exactly one (object, thing, count) triple and the replay is one call. A
    /// separate kind is needed rather than a flag: the two receivers are different classes, and one id space cannot
    /// tell them apart - a portal's id and a transporter's id are both `thingIDNumber`.</para></summary>
    public static GameCommand PortalCommand(int portalId, int thingId, int count, long sequence) =>
        new(sequence, 0, Portal, Encoding.UTF8.GetBytes($"{portalId}|{thingId}|{count}"));

    /// <summary>Cancelling a portal load. `MapPortal.CancelLoad` (`:571985`) is two writes, not one: it removes the
    /// `LordJob_LoadAndEnterPortal` lord (`:571990`) and clears `leftToLoad` (`:571992`).
    ///
    /// <para><b>Why it rides the same kind.</b> A cancel is "this portal, nothing to load, count zero" - the decoder
    /// already rejects `count <= 0` for a real load, so the shape cannot be confused with one. Giving it a fourth
    /// top-level kind would mean another whitelist entry, another classification row and another dispatch arm for the
    /// same object and the same verb family; Zetrith registers `MapPortal.CancelLoad` as its own sync method only
    /// because its model has no command kinds at all.</para></summary>
    public static GameCommand PortalCancelCommand(int portalId, long sequence) =>
        new(sequence, 0, Portal, Encoding.UTF8.GetBytes($"{portalId}|0|0"));

    /// <summary>An incident a player's own action queued, for the colony that actually runs the queue.
    ///
    /// <para><b>The defect.</b> Asking an ally for a trader, an orbital trader or military aid runs three writes inside
    /// the clicking player's process: `IncidentQueue.Add` (`:486929`, `:486865`, `:487128`), a cooldown stamp on the
    /// faction (`lastTraderRequestTick` `:486930`, `lastOrbitalTraderRequestTick` `:486866`,
    /// `lastMilitaryAidRequestTick` `:487127`), and a goodwill charge (`TryAffectGoodwillWith`, -15 / -30 / -25). A
    /// display replica has no `IncidentQueueTick` (`:384088` is reached from the storyteller's tick, which a replica never
    /// runs), so nothing it queues ever fires: the player is told 「商队已派出」, loses the goodwill and waits forever.</para>
    ///
    /// <para><b>Where it is captured.</b> At `IncidentQueue.Add` itself, not at the three dialog options. Two of those
    /// options do not have an `action` at all: `RequestTraderOption` returns an outer container whose only field is
    /// `link` (`:486940`) and builds one `DiaOption` per requestable trader kind inside a loop (`:486909-486935`). The
    /// first version of this file wrapped `__result.action` and therefore wrapped nothing - the same "compiles, never
    /// fires" failure this round is about. `Add` is the one place all three arrive at.</para>
    ///
    /// <para><b>What travels.</b> `def|faction|traderKind|map|retry`. The fire tick is recomputed on the receiving side
    /// from the same formula vanilla used, because the delay is per-path and not a free parameter: a caravan is
    /// `+120000` flat (`:486929`), an orbital trader is `RandomInRange(1,2 hours) * 2500` (`:486865`). Sending an offset
    /// would mean the sender draws the random once and the number the player sees is the one that travels, but it also
    /// means a payload that disagrees with the recipe. The recipe is cheaper and cannot drift.</para>
    ///
    /// <para><b>Goodwill is deliberately not in here.</b> The option writes it locally on whichever machine clicked, and
    /// `FactionSync` already publishes goodwill as state - so the clicker's charge reconciles through the frame it
    /// already had. Carrying it would charge twice on the authority.</para></summary>
    public const string Diplomacy = "diplomacy";

    public static GameCommand DiplomacyCommand(string def, int factionLoadId, string traderKind, int mapId,
        int retryTicks, bool quickAid, long sequence) =>
        new(sequence, 0, Diplomacy,
            Encoding.UTF8.GetBytes($"{def}|{factionLoadId}|{traderKind}|{mapId}|{retryTicks}|{(quickAid ? 1 : 0)}"));

    public static bool TryDecodeDiplomacy(byte[] payload, out string def, out int factionLoadId,
        out string traderKind, out int mapId, out int retryTicks, out bool quickAid)
    {
        def = traderKind = string.Empty;
        factionLoadId = mapId = retryTicks = 0;
        quickAid = false;
        if (payload is null || payload.Length == 0) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 6) return false;
        def = parts[0];
        traderKind = parts[2];
        quickAid = parts[5] == "1";
        return def.Length > 0
            && int.TryParse(parts[1], out factionLoadId)
            && int.TryParse(parts[3], out mapId)
            && int.TryParse(parts[4], out retryTicks);
    }

    public static bool TryDecodePortal(byte[] payload, out int portalId, out int thingId, out int count)
    {
        portalId = thingId = count = 0;
        if (payload is null || payload.Length == 0) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 3) return false;
        return int.TryParse(parts[0], out portalId)
            && int.TryParse(parts[1], out thingId)
            && int.TryParse(parts[2], out count);
    }

    public static GameCommand ForbidCommand(int thingId, bool forbidden, long sequence) =>
        new(sequence, 0, Forbid, Encoding.UTF8.GetBytes($"{thingId}|{(forbidden ? 1 : 0)}"));

    /// <summary>One command for a bulk forbid/unforbid. See <see cref="ForbidBatch"/> for why this exists.</summary>
    public static GameCommand ForbidBatchCommand(bool forbidden, IReadOnlyList<int> thingIds, long sequence)
    {
        ArgumentNullException.ThrowIfNull(thingIds);
        var builder = new StringBuilder(thingIds.Count * 8);
        builder.Append(forbidden ? '1' : '0').Append('|');
        var written = 0;
        for (var i = 0; i < thingIds.Count && written < MaxBatchIds; i++)
        {
            if (thingIds[i] <= 0) continue;
            if (written > 0) builder.Append(',');
            builder.Append(thingIds[i].ToString(CultureInfo.InvariantCulture));
            written++;
        }
        return new(sequence, 0, ForbidBatch, Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>Decodes a batch. Rejects ids that are not positive so a malformed payload cannot forbid "nothing".</summary>
    public static bool TryDecodeForbidBatch(byte[] payload, out bool forbidden, out List<int> thingIds)
    {
        forbidden = false;
        thingIds = [];
        // This one is deliberately larger than `TryDecode`'s 4096: a legitimate batch is over that bound, and the
        // ceiling that matters is the server's 64 KB payload limit.
        if (payload is null || payload.Length == 0 || payload.Length > MaxBatchPayload) return false;
        var text = Encoding.UTF8.GetString(payload);
        var split = text.IndexOf('|');
        if (split is not (1 or 2)) return false;
        var flag = text[..split];
        if (flag is not ("0" or "1")) return false;
        forbidden = flag == "1";
        var ids = text[(split + 1)..];
        if (ids.Length == 0) return false;
        foreach (var part in ids.Split(','))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) return false;
            thingIds.Add(id);
            // Refuse rather than truncate: a silently shortened batch leaves half the selection unchanged, which is
            // the exact symptom this command exists to remove.
            if (thingIds.Count > MaxBatchIds) return false;
        }
        return thingIds.Count > 0;
    }

    /// <summary>Senders never emit more than about 3.6 KB; anything past this is not one gesture.</summary>
    private const int MaxBatchPayload = 32 * 1024;

    public static GameCommand PriorityCommand(int pawnId, string workDef, int priority, long sequence) =>
        new(sequence, 0, Priority, Encoding.UTF8.GetBytes($"{pawnId}|{workDef}|{priority}"));

    public static GameCommand ZoneCellCommand(string zoneType, int zoneId, int map, int x, int z, bool add, long sequence,
        string? label = null) =>
        new(sequence, 0, ZoneCell, Encoding.UTF8.GetBytes(
            $"{zoneType}|{zoneId}|{map}|{x}|{z}|{(add ? 1 : 0)}|{label ?? ""}"));

    public static GameCommand ZoneDeleteCommand(int map, int zoneId, long sequence) =>
        new(sequence, 0, ZoneDelete, Encoding.UTF8.GetBytes($"{map}|{zoneId}"));

    public static GameCommand BillAddCommand(int buildingId, string recipeDef, int repeatCount, long sequence) =>
        new(sequence, 0, BillAdd, Encoding.UTF8.GetBytes($"{buildingId}|{recipeDef}|{repeatCount}"));

    public static GameCommand BillDeleteCommand(int buildingId, int billIndex, long sequence) =>
        new(sequence, 0, BillDelete, Encoding.UTF8.GetBytes($"{buildingId}|{billIndex}"));

    public static GameCommand AreaAssignCommand(int pawnId, int areaId, long sequence) =>
        new(sequence, 0, AreaAssign, Encoding.UTF8.GetBytes($"{pawnId}|{areaId}"));

    /// <summary>Payload is the thing id, a newline, then the Scribe fragment. A separator other than '|' is
    /// required because the fragment is XML and may contain any punctuation.</summary>
    public static GameCommand ThingStateCommand(int thingId, string xml, long sequence) =>
        new(sequence, 0, ThingState, Encoding.UTF8.GetBytes($"{thingId}\n{xml}"));

    public static bool TryDecodeThingState(byte[] payload, out int thingId, out string xml)
    {
        thingId = 0;
        xml = "";
        if (payload is null || payload.Length < 3 || payload.Length > 64 * 1024) return false;
        var text = Encoding.UTF8.GetString(payload);
        var split = text.IndexOf('\n');
        if (split <= 0 || split == text.Length - 1) return false;
        if (!int.TryParse(text[..split], out thingId)) return false;
        xml = text[(split + 1)..];
        return xml.Length > 0;
    }

    /// <summary>Research target, or <c>null</c> to stop. A def name carries no separator, so a single field is enough.</summary>
    public const string Research = "research";

    public static GameCommand ResearchCommand(string? projectDef, long sequence) =>
        new(sequence, 0, Research, Encoding.UTF8.GetBytes(projectDef ?? ""));

    /// <summary>A policy change for one pawn, as the four trackers' own Scribe XML.
    ///
    /// <para>Payload is <c>pawnId|base64(outfitXml)|base64(drugXml)|base64(foodXml)|base64(timetableXml)</c>. Every
    /// field is base64'd rather than escaped: the XML contains newlines and quotes, and a policy label is player text
    /// (a renamed outfit can contain `|`). An empty field means "that tracker is not part of this change", which is
    /// what the panel sends - changing an outfit must not rewrite a pawn's timetable.</para></summary>
    public static GameCommand PolicyCommand(int pawnId, string? outfitXml, string? drugXml, string? foodXml, string? timetableXml,
        long sequence, string? stockXml = null)
    {
        static string Encode(string? xml) => string.IsNullOrEmpty(xml) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        return new(sequence, 0, Policy,
            Encoding.UTF8.GetBytes($"{pawnId}|{Encode(outfitXml)}|{Encode(drugXml)}|{Encode(foodXml)}|{Encode(timetableXml)}|{Encode(stockXml)}"));
    }

    /// <summary>A whole <c>BillStack</c> for one bill giver, as its own Scribe XML.
    ///
    /// <para>Payload is <c>giverThingId|base64(billStackXml)</c>. The stack travels whole rather than as a diff of
    /// the four settings the ledger names, because `Dialog_BillConfig` writes **thirteen** distinct fields
    /// (`recipe`/`targetCount`/`repeatMode`/`repeatCount`/`hpRange`/`qualityRange`/`ingredientSearchRadius`/
    /// `ingredientFilter`/`style`/`globalStyle`/`suspended`/`pauseWhenSatisfied`/`unpauseWhenYouHave`) plus three
    /// methods (`SetPawnRestriction`, `SetIncludeGroup`, `SetStoreMode`), and a field list would silently miss every
    /// field a mod adds. `BillStack.ExposeData` is `Scribe_Collections.Look(ref bills, "bills", LookMode.Deep)`
    /// (`:272715`) and `Bill.ExposeData` scribes all thirteen (`:272111`+), so the fragment is exactly the set.</para>
    ///
    /// <para>The whole stack, not one bill: a bill being edited is identified by its index in the stack, and two
    /// players reordering the list at once would make an index point at the wrong bill. The stack holds at most
    /// `MaxCount = 15` bills (`:272612`), so the fragment is small.</para></summary>
    public static GameCommand BillsCommand(int giverThingId, string billStackXml, long sequence) =>
        new(sequence, 0, Bills,
            Encoding.UTF8.GetBytes($"{giverThingId}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(billStackXml))}"));

    /// <summary>What the player picked in a growth-moment letter: the skills to gain passion in, and the trait.
    ///
    /// <para><b>Why this cannot reuse `letter-choice`.</b> `ChoiceLetter_GrowthMoment` derives from
    /// `LetterWithTimeout`, not from `ChoiceLetter` (`_lab/asm/Assembly-CSharp.decompiled.cs:662067`), and it has no
    /// `Choices` property at all: its answer is `MakeChoices(List&lt;SkillDef&gt;, Trait)` (`:662198`), reached from the
    /// OK button of `Dialog_GrowthMomentChoices` (`:662457`). There is no option list to index.</para>
    ///
    /// <para><b>Why skills are sent by defName and the trait by defName + degree.</b> A `Trait` is not a def: the same
    /// `TraitDef` at a different degree is a different trait (`Trait(Degree)`), and an option offers one specific
    /// degree. Sending only the def would let the host apply a different degree than the one the replica showed.</para>
    ///
    /// <para>Payload: <c>letterId#passion,p passion,…#traitDef|degree</c>. An empty trait section means the player took
    /// the "no trait" option, which the letter offers 35% of the time (`:662277-662281`) — that is different from
    /// "the field was not sent", so the separator is always present.</para></summary>
    public static GameCommand GrowthChoiceCommand(int letterId, IReadOnlyList<string> passions, string? traitDef, int traitDegree,
        long sequence) =>
        new(sequence, 0, GrowthChoice, Encoding.UTF8.GetBytes(
            letterId.ToString(CultureInfo.InvariantCulture) + "#" + string.Join(",", passions) + "#"
            + (traitDef is null ? "" : traitDef + "|" + traitDegree.ToString(CultureInfo.InvariantCulture))));

    public static bool TryDecodeGrowthChoice(byte[] payload, out int letterId, out string[] passions,
        out string? traitDef, out int traitDegree)
    {
        letterId = 0;
        passions = [];
        traitDef = null;
        traitDegree = 0;
        if (payload is null || payload.Length is 0 or > 512) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('#');
        if (parts.Length != 3 || !int.TryParse(parts[0], out letterId) || letterId <= 0) return false;
        passions = parts[1].Length == 0 ? [] : parts[1].Split(',');
        if (passions.Length > 8) return false;
        if (parts[2].Length == 0) return true;
        var trait = parts[2].Split('|');
        if (trait.Length != 2 || trait[0].Length == 0 || !int.TryParse(trait[1], out traitDegree)) return false;
        traitDef = trait[0];
        return true;
    }

    /// <summary>Which of a choice letter's options the player clicked, as an index.
    ///
    /// <para><b>Why an index and not the option's text.</b> Localisation: the wording is the replica's own translation,
    /// so the text it shows may not match the host's. The order is the letter's — `ChoiceLetter.Choices`
    /// (`:182745`) is an `IEnumerable&lt;DiaOption&gt;` built by each subclass, and both sides build it from the same
    /// letter state, so index 0 is the same option on both.</para>
    ///
    /// <para><b>Why one command for eleven letter types.</b> All eleven subclasses override the same abstract
    /// `Choices` property and every option's effect lives in `DiaOption.action` (`:195541`, invoked by `Activate` at
    /// `:195621-195636`). The host therefore only has to enumerate the same property and run the same delegate — the
    /// same reason the designator route is one patch point for 54 designator types.</para>
    ///
    /// <para><b>Why the index is validated on the host.</b> A replica running a different mod set can hold a letter
    /// with fewer options; an out-of-range index must be refused there, not throw inside the UI thread. The count is
    /// sent back in the reply so the prober can see the mismatch instead of guessing.</para></summary>
    public static GameCommand LetterChoiceCommand(int letterId, int option, long sequence) =>
        new(sequence, 0, LetterChoice,
            Encoding.UTF8.GetBytes(letterId.ToString(CultureInfo.InvariantCulture) + "#" + option.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecodeLetterChoice(byte[] payload, out int letterId, out int option)
    {
        letterId = 0;
        option = -1;
        if (payload is null || payload.Length is 0 or > 64) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('#');
        return parts.Length == 2 && int.TryParse(parts[0], out letterId) && letterId > 0
            && int.TryParse(parts[1], out option) && option >= 0;
    }

    /// <summary>The whole <c>Storyteller</c> object, as its own Scribe XML.
    ///
    /// <para>Payload is just <c>base64(storytellerXml)</c> — there is exactly one storyteller per game, so unlike the
    /// bill stack there is no owner id to carry. `Storyteller.ExposeData` scribes `def`/`difficulty`/`incidentQueue`/
    /// `lastIncidentTick` and, for a custom difficulty, the whole <c>Difficulty</c> object (`:389930`+, whose own
    /// `ExposeData` writes `threatScale`/`allowBigThreats`/`cropYieldFactor`/… at `:380394-380413`).</para>
    ///
    /// <para>Why the whole object and not the difficulty page's fields: `Page_SelectStorytellerInGame`
    /// (`:654966-654985`) passes `ref storyteller.def`, `ref storyteller.difficultyDef` and `ref storyteller.difficulty`
    /// into `StorytellerUI.DrawStorytellerSelectionInterface`, so a page session can replace any of the three. Sending
    /// only `difficulty` would leave a changed storyteller def behind on every replica.</para></summary>
    public static GameCommand StorytellerCommand(string storytellerXml, long sequence) =>
        new(sequence, 0, Storyteller, Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(storytellerXml))));

    public static bool TryDecodeStoryteller(byte[] payload, out string? storytellerXml)
    {
        storytellerXml = null;
        if (payload is null || payload.Length is 0 or > 262144) return false;
        try
        {
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(payload)));
            if (xml.Length is 0 or > 196608) return false;
            storytellerXml = xml;
            return true;
        }
        catch (FormatException) { return false; }
    }

    public static bool TryDecodeBills(byte[] payload, out int giverThingId, out string? billStackXml)
    {
        giverThingId = 0;
        billStackXml = null;
        if (payload is null || payload.Length is 0 or > 262144) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out giverThingId) || giverThingId <= 0) return false;
        if (parts[1].Length == 0) return false;
        try
        {
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            if (xml.Length is 0 or > 196608) return false;
            billStackXml = xml;
            return true;
        }
        catch (FormatException) { return false; }
    }

    public static bool TryDecodePolicy(byte[] payload, out int pawnId, out string? outfitXml, out string? drugXml,
        out string? foodXml, out string? timetableXml, out string? stockXml)
    {
        pawnId = 0;
        outfitXml = drugXml = foodXml = timetableXml = stockXml = null;
        if (payload is null || payload.Length is 0 or > 262144) return false;
        // Five fields before the "carry" column was added, six after. Accepting both shapes rather than bumping a
        // command kind keeps an older client's policy change working: a frame in flight during an upgrade would
        // otherwise be rejected, and the panel that sent it would look like it had no effect.
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length is not (5 or 6) || !int.TryParse(parts[0], out pawnId) || pawnId <= 0) return false;
        outfitXml = Decode(parts[1]);
        drugXml = Decode(parts[2]);
        foodXml = Decode(parts[3]);
        timetableXml = Decode(parts[4]);
        if (parts.Length > 5) stockXml = Decode(parts[5]);
        // At least one tracker must be present, or applying this would be a no-op command that costs a round trip.
        return outfitXml is not null || drugXml is not null || foodXml is not null || timetableXml is not null
            || stockXml is not null;

        static string? Decode(string text)
        {
            if (text.Length == 0) return null;
            try
            {
                var xml = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                return xml.Length is > 0 and <= 196608 ? xml : null;
            }
            catch (FormatException) { return null; }
        }
    }

    public static bool TryDecodeResearch(byte[] payload, out string? projectDef)
    {
        projectDef = null;
        if (payload is null || payload.Length > 256) return false;
        var text = Encoding.UTF8.GetString(payload);
        projectDef = text.Length == 0 ? null : text;
        return true;
    }

    /// <summary>Scribe state of any target: <c>kind</c> is 'z' for a zone, anything else for a thing.
    /// The id is followed by a newline so the XML fragment can contain any punctuation.</summary>
    public static GameCommand StateCommand(char kind, int id, string xml, long sequence) =>
        new(sequence, 0, ThingState, Encoding.UTF8.GetBytes($"{kind}\n{id}\n{xml}"));

    public static bool TryDecodeState(byte[] payload, out char kind, out int id, out string xml)
    {
        kind = '\0';
        id = 0;
        xml = "";
        if (payload is null || payload.Length < 5 || payload.Length > 256 * 1024) return false;
        var text = Encoding.UTF8.GetString(payload);
        var first = text.IndexOf('\n');
        if (first != 1) return false;
        var second = text.IndexOf('\n', first + 1);
        if (second < 0 || second == text.Length - 1) return false;
        if (!int.TryParse(text[(first + 1)..second], out id)) return false;
        kind = text[0];
        xml = text[(second + 1)..];
        return xml.Length > 0;
    }

    // ---------- Decoders ----------

    public static bool TryDecodeDesignation(byte[] payload, out string defName, out int map, out int x, out int z, out int thingId)
    {
        defName = "";
        map = x = z = thingId = 0;
        if (!TryDecode(payload, 5, out var p)) return false;
        defName = p[0];
        return defName.Length > 0 && int.TryParse(p[1], out map) && int.TryParse(p[2], out x)
            && int.TryParse(p[3], out z) && int.TryParse(p[4], out thingId);
    }

    public static bool TryDecodeRemoveDesignation(byte[] payload, out string defName, out int map, out int x, out int z, out int thingId)
    {
        defName = "";
        map = x = z = thingId = 0;
        if (!TryDecode(payload, 5, out var p)) return false;
        defName = p[0];
        return defName.Length > 0 && int.TryParse(p[1], out map) && int.TryParse(p[2], out x)
            && int.TryParse(p[3], out z) && int.TryParse(p[4], out thingId);
    }

    public static bool TryDecodeBlueprint(byte[] payload, out string buildable, out string stuff, out int map, out int x,
        out int z, out int rot, out int thingId) =>
        TryDecodeBlueprint(payload, out buildable, out stuff, out map, out x, out z, out rot, out thingId,
            out _, out _);

    /// <summary>The same decode, with the two style segments. `style` and `glower` come back empty when the sender is
    /// an older build that only wrote seven segments, and empty means "no style", which is what that build produced.</summary>
    public static bool TryDecodeBlueprint(byte[] payload, out string buildable, out string stuff, out int map, out int x,
        out int z, out int rot, out int thingId, out string style, out string glower)
    {
        buildable = stuff = style = glower = "";
        map = x = z = rot = thingId = 0;
        // Six fields plus the optional thing id. The id is what makes the blueprint the same object on every peer:
        // the origin places it the moment the player drags, the others place it at the stamped tick, and without the
        // id each side allocates its own - measured as the same 9028 things carrying different ids.
        if (!TryDecodeAtLeast(payload, 6, out var p)) return false;
        buildable = p[0];
        stuff = p[1];
        if (p.Length > 6 && !int.TryParse(p[6], out thingId)) return false;
        if (p.Length > 7) style = p[7];
        if (p.Length > 8) glower = p[8];
        return buildable.Length > 0 && int.TryParse(p[2], out map) && int.TryParse(p[3], out x)
            && int.TryParse(p[4], out z) && int.TryParse(p[5], out rot);
    }

    public static bool TryDecodeCancelThing(byte[] payload, out int thingId)
    {
        thingId = 0;
        return TryDecode(payload, 1, out var p) && int.TryParse(p[0], out thingId);
    }

    public static bool TryDecodeDraft(byte[] payload, out int pawnId, out bool drafted)
    {
        pawnId = 0;
        drafted = false;
        if (!TryDecode(payload, 2, out var p) || !int.TryParse(p[0], out pawnId) || !int.TryParse(p[1], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        drafted = flag == 1;
        return true;
    }

    /// <summary>Decodes an order into the pawn id and the job's own Scribe XML.
    ///
    /// <para>Base64 in the middle of a `|`-separated payload: the XML contains newlines, quotes and arbitrary
    /// player-authored strings (a bill's name rides inside a job), so it is encoded rather than escaped.</para>
    ///
    /// <para>Not routed through <see cref="TryDecode"/>, which refuses anything over 4096 bytes: a job XML is larger
    /// than that as soon as it carries a target queue (the delivery job for a big blueprint has up to fifteen), and
    /// the 4096 limit exists to bound the *fixed-shape* commands. The bound here is on the decoded XML instead, which
    /// is the thing an attacker would actually inflate.</para></summary>
    public static bool TryDecodeOrder(byte[] payload, out int pawnId, out string jobXml)
    {
        pawnId = 0;
        jobXml = "";
        if (payload is null || payload.Length is 0 or > 262144) return false;
        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out pawnId) || pawnId <= 0) return false;
        if (parts[1].Length == 0) return false;
        try { jobXml = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); }
        catch (FormatException) { return false; }
        return jobXml.Length > 0 && jobXml.Length <= 196608;
    }

    public static bool TryDecodeStop(byte[] payload, out int pawnId, out int condition)
    {
        pawnId = condition = 0;
        return TryDecodeAtLeast(payload, 2, out var parts)
            && int.TryParse(parts[0], out pawnId) && pawnId > 0
            && int.TryParse(parts[1], out condition);
    }

    public static bool TryDecodeTransport(byte[] payload, out int transporterId, out int thingId, out int count)
    {
        transporterId = thingId = count = 0;
        return TryDecode(payload, 3, out var p)
            && int.TryParse(p[0], out transporterId) && int.TryParse(p[1], out thingId)
            && int.TryParse(p[2], out count) && count > 0;
    }

    public static bool TryDecodeForbid(byte[] payload, out int thingId, out bool forbidden)
    {
        thingId = 0;
        forbidden = false;
        if (!TryDecode(payload, 2, out var p) || !int.TryParse(p[0], out thingId) || !int.TryParse(p[1], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        forbidden = flag == 1;
        return true;
    }

    public static bool TryDecodePriority(byte[] payload, out int pawnId, out string workDef, out int priority)
    {
        pawnId = priority = 0;
        workDef = "";
        if (!TryDecode(payload, 3, out var p) || !int.TryParse(p[0], out pawnId) || !int.TryParse(p[2], out priority)) return false;
        workDef = p[1];
        return workDef.Length > 0 && priority is >= 0 and <= 4;
    }

    public static bool TryDecodeZoneCell(byte[] payload, out string zoneType, out int zoneId, out int map, out int x, out int z, out bool add,
        out string? label)
    {
        zoneType = "";
        zoneId = map = x = z = 0;
        add = false;
        label = null;
        // Six fields plus an optional label. `TryDecode` enforces an exact count, so the trailing field needs the
        // lenient split: a six-field payload from a peer that predates the label must still decode, because a
        // rejected command loses the cell, and the cell is the part that matters.
        if (!TryDecodeAtLeast(payload, 6, out var p) || !int.TryParse(p[1], out zoneId) || !int.TryParse(p[2], out map)
            || !int.TryParse(p[3], out x) || !int.TryParse(p[4], out z) || !int.TryParse(p[5], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        zoneType = p[0];
        add = flag == 1;
        // The label is part of the zone. Measured: both peers ended a 25-cell drag with byte-identical cells and still
        // reported a divergence, because the origin called the zone "scripted" while the other side kept the vanilla
        // default "Stockpile zone 1". Every other row was identical.
        label = p.Length > 6 && p[6].Length > 0 ? p[6] : null;
        return true;
    }

    public static bool TryDecodeZoneDelete(byte[] payload, out int map, out int zoneId)
    {
        map = zoneId = 0;
        return TryDecode(payload, 2, out var p) && int.TryParse(p[0], out map) && int.TryParse(p[1], out zoneId);
    }

    public static bool TryDecodeBillAdd(byte[] payload, out int buildingId, out string recipeDef, out int repeatCount)
    {
        buildingId = repeatCount = 0;
        recipeDef = "";
        if (!TryDecode(payload, 3, out var p) || !int.TryParse(p[0], out buildingId) || !int.TryParse(p[2], out repeatCount)) return false;
        recipeDef = p[1];
        return recipeDef.Length > 0;
    }

    public static bool TryDecodeBillDelete(byte[] payload, out int buildingId, out int billIndex)
    {
        buildingId = billIndex = 0;
        return TryDecode(payload, 2, out var p) && int.TryParse(p[0], out buildingId) && int.TryParse(p[1], out billIndex) && billIndex >= 0;
    }

    public static bool TryDecodeAreaAssign(byte[] payload, out int pawnId, out int areaId)
    {
        pawnId = areaId = 0;
        return TryDecode(payload, 2, out var p) && int.TryParse(p[0], out pawnId) && int.TryParse(p[1], out areaId);
    }

    private static bool TryDecode(byte[] payload, int fields, out string[] parts)
    {
        parts = [];
        if (payload is null || payload.Length == 0 || payload.Length > 4096) return false;
        var text = Encoding.UTF8.GetString(payload);
        parts = text.Split('|');
        return parts.Length == fields;
    }

    /// <summary>A pawn leaving the player's faction by a <em>player decision</em>, with the identity of who decided.
    ///
    /// <para><b>Why this needs its own command.</b> Three separate player actions end in the same place —
    /// `PawnBanishUtility.Banish` (`:444883`, from the character card's ban button or a caravan's abandon menu),
    /// `RecruitUtility.Recruit` (`:456919` via `InteractionWorker_RecruitAttempt.DoRecruit`, from the prisoner tab's
    /// recruit gizmo) and `ReleaseAnimalToWildUtility.DoReleaseAnimal` (from the animal's release gizmo). None of them
    /// has a chokepoint this mod patches: the gizmo capture only sees `Command.ProcessInput`, and all three run from a
    /// **confirmation dialog's** callback, which is a `Dialog_MessageBox` — a window whose buttons the gizmo patch
    /// never observes. Looking only at `Command.ProcessInput` made all three read as covered.</para>
    ///
    /// <para>Payload is `<op>|<pawnId>|<factionLoadId>`. Only the identity of the decision travels; the effect is
    /// re-run through the game's own method on the authority, because banishment also fires thoughts, records, tale
    /// entries and quest signals — a field list would drift the moment a DLC adds one.</para></summary>
    public const string PawnLeave = "pawn-leave";

    /// <summary>ops: <c>banish</c> / <c>recruit</c> / <c>release</c>. <paramref name="factionLoadId"/> is only read by
    /// <c>recruit</c> (which faction to join); the other two ignore it.</summary>
    public static GameCommand PawnLeaveCommand(string op, int pawnId, int factionLoadId, long sequence) =>
        new(sequence, 0, PawnLeave,
            Encoding.UTF8.GetBytes($"{op}|{pawnId}|{factionLoadId}"));

    public static bool TryDecodePawnLeave(byte[] payload, out string op, out int pawnId, out int factionLoadId)
    {
        op = "";
        pawnId = 0;
        factionLoadId = 0;
        if (!TryDecode(payload, 3, out var p)) return false;
        if (!int.TryParse(p[1], out pawnId) || !int.TryParse(p[2], out factionLoadId)) return false;
        op = p[0];
        return op is "banish" or "recruit" or "release";
    }

    /// <summary>Splits at least <paramref name="fields"/> parts, for a payload with a trailing optional field.
    /// A sender that predates the field must still decode: rejecting the command would lose the effect, and the effect
    /// is the part that matters.</summary>
    private static bool TryDecodeAtLeast(byte[] payload, int fields, out string[] parts)
    {
        parts = [];
        if (payload is null || payload.Length == 0 || payload.Length > 4096) return false;
        var text = Encoding.UTF8.GetString(payload);
        parts = text.Split('|');
        return parts.Length >= fields;
    }
}
