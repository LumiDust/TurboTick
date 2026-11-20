using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Shared-colony action layer. Every player-issued action passes through choke points:
///   DesignationManager.AddDesignation / RemoveDesignation - mine / deconstruct / hunt / harvest / cut / tame / cancels
///   GenConstruct.PlaceBlueprintForBuild                   - building and furniture orders
///   ThingWithComps.Destroy (Blueprint) / Frame.Destroy    - cancel construction
///   Pawn_DraftController.set_Drafted                      - draft toggle
///   Pawn_JobTracker.TryTakeOrderedJob                     - right-click move / haul / attack / interact orders
///   ForbidUtility.SetForbidden                            - item forbid / unforbid
///   Pawn_WorkSettings.SetPriority                         - colonist work priorities
///   Zone.AddCell / RemoveCell / Delete                    - stockpile / growing zone management
///   BillStack.AddBill / Delete                            - workbench crafting bills
///   Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap   - allowed area assignments
/// Captured actions are encoded, ordered by the server, and replayed on every peer at the same tick.
/// </summary>
public static class SharedActions
{
    internal static bool Applying;

    /// <summary>Depth of the "this write comes from the agreed state, not from a player" scope.
    ///
    /// The capture patches cancel a local assignment and turn it into a command, which is right for input and wrong
    /// for a frame: a replica writing the authoritative forbid flag hit <c>ForbidPropertyCapturePatch</c>, had its
    /// write cancelled and sent the frame's own value back to the server as a player gesture. The server relayed it,
    /// the authority applied it to its own items, and the replica's screen never changed - the loop fed itself.
    /// Measured: 1459 commands in one 300-second run, 791 of them <c>forbid-batch</c> with <c>value=True</c>, while
    /// the replica's own counter read <c>翻转 0 次</c> and <c>本端物品 2081 件其中禁止 1296 件</c> kept climbing.
    ///
    /// ColonyTogether has no equivalent bug because it never patches the setter: it captures at the designator and
    /// replays the vanilla one. Until our patches are narrowed the same way, every state write has to say so.
    /// A counter rather than a bool because these writes nest (a thing write can call another thing write).</summary>
    [ThreadStatic] private static int stateWrites;

    internal static bool WritingState => stateWrites > 0;

    /// <summary>Marks a write that reproduces agreed state. Everything inside is exempt from capture.</summary>
    internal static IDisposable StateWrite() => new StateWriteScope();

    private sealed class StateWriteScope : IDisposable
    {
        private bool disposed;

        public StateWriteScope() { stateWrites++; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stateWrites--;
        }
    }
    /// <summary>Diagnostic: one line per applied command with the shared random state in front of it. The
    /// server stamps the tick, so two peers must show identical tick, order and state; the first line that
    /// differs is the exact place the command path stops being deterministic.</summary>
    private static readonly bool TraceCommands = Environment.GetCommandLineArgs()
        .Any(a => a.Equals("-turbotick-trace-commands", StringComparison.OrdinalIgnoreCase));
    public static void ResetTickWork()
    {
        Applying = false;
    }

    // ---------- Capture ----------

    /// <summary>True when this call is the player acting, rather than the simulation calling the same method.
    ///
    /// That distinction decides whether a capture patch may cancel the call. Inside a tick the AI and vanilla
    /// use these methods constantly (forbidding, drafting, designating); cancelling those would break the
    /// colony on every peer. Outside a tick it is input, and it is safe to defer to the agreed tick so that
    /// every peer applies it at the same moment.</summary>
    /// <summary>Whether a user action should be turned into an ordered command.
    ///
    /// <c>CanDeliver</c> is part of this on purpose. Capturing an action cancels its local effect and relies on the
    /// command coming back; if the command cannot be delivered, the player is left with nothing happening at all.
    /// Measured: a stockpile drag published 24 commands with no gate drop and no send failure, and the server
    /// received none - so "the session accepts input" is not the same as "a command can reach another peer".
    ///
    /// Zen behaviour, matching what the Zetrith Multiplayer mod does: when the transport is not usable, the action
    /// stays local (`PlayerActing` false means the vanilla path runs untouched) instead of being cancelled and lost.
    /// A local-only change is visible, detectable and recoverable; a vanished click is none of those.
    ///
    /// <para><b>Not on the authority.</b> A dedicated server connects to its own room as a <c>simulation</c> peer
    /// (<see cref="HeadlessRuntime.Active"/> makes <see cref="RoomConnection"/> pass <c>simulation: true</c>), and
    /// <c>DedicatedServer.RelayCommandAsync</c> answers every such command with
    /// <c>SimulationReadOnly</c>: "The process simulation does not issue player commands." So on that process the
    /// capture could only ever cancel the call and lose it - there is no path back. Measured on a live authority:
    /// a scripted four-cell wall drag logged <c>acting=True 捕获 前[gestures=0] 后[gestures=4 commands=4]</c>, the
    /// server rejected all four, and `蓝图 前0/后0` stayed zero. Four commands went on the wire and the world did
    /// not change, which is the exact signature of "the capture is the bug, not the designator".
    ///
    /// <para>The authority does not need the round trip: it *is* the simulation, so the vanilla call is already the
    /// agreed outcome, and the replicas receive the result through the frame. Skipping capture there is not a
    /// shortcut - it is what keeps a gesture from being turned into a command that nothing will ever execute.</para>
    /// </summary>
    internal static bool PlayerActing => !Applying && !WritingState && SharedColony.Session.AcceptsInput
        && !SharedColony.TickExecuting && RoomConnection.CanDeliver && !SimulationPeerProcess;

    /// <summary>This process joined the room as the <c>simulation</c> peer, which the server treats as read-only.
    ///
    /// <para>One flag decides it on the wire: <c>RoomConnection.Connect(..., simulation: HeadlessRuntime.Active)</c>.
    /// <c>HeadlessRuntime.Active</c> is true for both the dedicated server and a headless participant, so this is not
    /// "is the authority" - it is "the server will refuse every command this process sends".</para>
    ///
    /// <para>Derived from the mode flags rather than from a session field, because the decision to capture has to be
    /// right before a room exists: a scripted gesture can fire while the world is still loading, and
    /// <c>Session.AcceptsInput</c> is already true by then.</para></summary>
    internal static bool SimulationPeerProcess => HeadlessRuntime.Active;

    /// <summary>The four conditions of <see cref="PlayerActing"/>, spelled out, for a diagnostic that has to say
    /// <em>which</em> one refused. Reading them again here instead of returning a tuple from the property keeps the hot
    /// path (every designator cell, every forbid, every gizmo) free of an allocation.</summary>
    internal static string GateDescription() =>
        $"applying={Applying} writing={WritingState} acceptsInput={SharedColony.Session.AcceptsInput}"
        + $" ticking={SharedColony.TickExecuting} canDeliver={RoomConnection.CanDeliver}"
        + $" simulationPeer={SimulationPeerProcess}";

    private static float lastGateNote;

    /// <summary>Opt-in input tracing: one line per captured action. Off by default because Waylith attaches a
    /// full call stack to every log line, and a 240-action acceptance run produced 618 KB of log
    /// (<c>192.168.0.29:/data/rimworld/det/peerA/console.log</c>).</summary>
    private static readonly bool TraceInput = Environment.GetCommandLineArgs()
        .Any(a => a.Equals("-turbotick-trace-input", StringComparison.OrdinalIgnoreCase))
        || Environment.GetEnvironmentVariable("TURBOTICK_TRACE_INPUT") == "1";

    /// <summary>Records why a click was not captured.
    ///
    /// "I clicked it and nothing happened" has three indistinguishable causes from the player's side: the
    /// patch never fired, a gate refused it, or the command was sent and dropped. This names the gate, and
    /// is rate limited so a held-down gizmo cannot flood the log.
    ///
    /// A replay of a command reaches the same capture patch with <see cref="Applying"/> set. That is not a
    /// lost click, it is the command being applied - reporting it produced one WARN plus a call stack per
    /// command and buried the real divergence lines.</summary>
    internal static void NoteGateBlocked(string what)
    {
        if (Applying) return;
        // A write that reproduces agreed state is not a refused gesture. Reporting it as one turned every frame into a
        // "forbid 未捕获" warning, which is the same shape as the real symptom and would have hidden it.
        if (WritingState) return;
        var now = Clock.Now;
        if (now - lastGateNote < 5f) return;
        lastGateNote = now;
        // The gate has four conditions and the old line named three of them. A refused click therefore could not be
        // told apart from a half-closed socket, and that ambiguity is exactly what the last round of "I pressed F and
        // nothing happened" ran into: acceptsInput and tickExecuting both read normal while the action still never
        // left the machine. `DeliveryState` is the same string SendCommand's own precondition uses.
        Log.Warning($"[TurboTick input] {what} 未捕获：applying={Applying} "
            + $"acceptsInput={SharedColony.Session.AcceptsInput} tickExecuting={SharedColony.TickExecuting} "
            + $"phase={SharedColony.Session.Phase} {RoomConnection.DeliveryState()}");
    }

    /// <summary>Why <see cref="TryCaptureDirectForbid"/> declined, in the same shape <see cref="NoteGateBlocked"/>
    /// uses. The gizmo path and the designator path are separate patches, and only the designator one reported
    /// anything: a click on the allow/forbid gizmo that was refused left no trace at all.</summary>
    internal static void NoteForbidGateBlocked(bool forbidden)
    {
        if (Applying) return;
        if (WritingState) return;
        var now = Clock.Now;
        if (now - lastForbidGateNote < 5f) return;
        lastForbidGateNote = now;
        Log.Warning($"[TurboTick input] forbid(gizmo) 未捕获：value={forbidden} applying={Applying} "
            + $"acceptsInput={SharedColony.Session.AcceptsInput} tickExecuting={SharedColony.TickExecuting} "
            + $"phase={SharedColony.Session.Phase} {RoomConnection.DeliveryState()}");
    }

    private static float lastForbidGateNote;

    /// <summary>One line per captured action, only when input tracing was asked for.</summary>
    internal static void TraceCapture(string text)
    {
        if (TraceInput) Log.Message("[TurboTick input] " + text);
    }

    /// <summary>Publishes a designation. Returns false when nothing was published.
    ///
    /// The caller cancels the vanilla call with this value, so it must be exact: cancelling without publishing loses
    /// the player's click entirely. That is what happened for cutting and mining - measured as "I dragged a mining
    /// selection and no marks appear at all". `Designation.designationManager` is assigned by
    /// `DesignationManager.AddDesignation` itself, so at this point it is still null; requiring it sent every
    /// designation down the "nothing to capture" path *and* cancelled the add.</summary>
    public static bool CaptureDesignation(Designation designation)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return false;
        if (designation?.def == null || !designation.target.IsValid) return false;
        var map = designation.UsesManager() ?? Find.CurrentMap;
        if (map == null) return false;
        // Thing-keyed designations must carry their target, not the cell it happens to occupy.
        var thingId = designation.target.HasThing ? designation.target.Thing.thingIDNumber : -1;
        // `uniqueID`, not `Index`: `Map.Index` is `Find.Maps.IndexOf(this)` (`:76452`) - a position, and one that
        // shifts for every later map the moment an earlier one is removed. `Map.uniqueID` (`:76213`) is scribed
        // (`Map.ExposeData`, `:76818`), so two peers loaded from the same save agree on it by construction.
        Publish(CommandCodec.DesignationCommand(designation.def.defName, map.uniqueID,
            designation.target.Cell.x, designation.target.Cell.z, thingId, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>The map a not-yet-registered designation belongs to. At capture time the manager field is null, so the
    /// target's thing is the only reliable source; a cell target falls back to the current map.</summary>
    private static Map? UsesManager(this Designation designation) =>
        designation.designationManager?.map ?? designation.target.Thing?.Map;

    /// <summary>Publishes a removal. Returns false when nothing was published, for the same reason as above.
    ///
    /// <para>Suppressed while a cancel drag is committing: the drag already travelled as one `designator` command
    /// carrying every cell, and the replay removes the same marks there. Publishing one `remove-designation` per mark
    /// on top of it turned a single gesture into dozens of commands, which is what the drag command exists to avoid.
    /// </para></summary>
    public static bool CaptureRemoveDesignation(Designation designation)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || designation?.def == null) return false;
        if (DesignatorSync.SuppressingPerThingCancel) return false;
        var map = designation.UsesManager() ?? Find.CurrentMap;
        if (map == null) return false;
        var cell = designation.target.Cell;
        var thingId = designation.target.HasThing ? designation.target.Thing.thingIDNumber : -1;
        Publish(CommandCodec.RemoveDesignationCommand(designation.def.defName, map.uniqueID,
            cell.x, cell.z, thingId, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>Publishes a whole designator action. Returns false when it was not published, so the hook can leave the
    /// vanilla call alone.
    ///
    /// See <see cref="DesignatorSync"/>: the client must not allocate thing ids, and the designator is the level the
    /// drag commits on, so cancelling here costs nothing in interaction.
    ///
    /// <para><b>Cancel is the exception and must run locally.</b> Placement is cancelled because the resulting object
    /// gets its id from <c>UniqueIDsManager</c>, which only the host may advance. A cancel drag creates nothing - it
    /// destroys blueprints and clears marks - so running it here cannot offset any counter, and the player watching
    /// the rectangle disappear is the whole point of the gesture. Measured the other way: with the local call
    /// cancelled, a 40-cell drag left every blueprint on screen until the frame caught up, and any cell the relay or
    /// the replay missed stayed cancelled on the host and visible here - reported as "only the first couple of cells
    /// cancel". The replay on the other peers is idempotent (a cell with nothing to cancel does nothing), so applying
    /// it twice is harmless.</para></summary>
    public static bool CaptureDesignator(Designator designator, IReadOnlyList<IntVec3> cells)
    {
        if (Applying || designator is null)
        {
            DesignatorSync.NoteCapture([], suppressed: true);
            return false;
        }
        if (!SharedColony.Session.AcceptsInput)
        {
            // Counted rather than dropped silently: a peer whose session says "no input" would otherwise look identical
            // to a peer whose gesture produced no command, and the two need opposite fixes.
            DesignatorSync.NoteCapture([], suppressed: true);
            return false;
        }
        var chunks = DesignatorSync.EncodeChunks(designator, cells);
        DesignatorSync.NoteCapture(chunks, suppressed: false);
        if (chunks.Count == 0) return false;
        foreach (var chunk in chunks)
            Publish(chunk with { Sequence = SharedColony.NextSequence() });
        // Cancelling the local call is how a placement avoids allocating a thing id before the host does. A cancel
        // drag allocates nothing, so it runs locally and the replay on every peer only confirms the result.
        return designator is not Designator_Cancel;
    }
    /// <summary>Blueprints that arrived with a style or a glower colour. Printed because "0" has two causes worth
    /// telling apart: nobody placed a styled building, or the style segments never decoded.</summary>
    internal static long blueprintStylesApplied;

    public static bool CaptureBlueprint(BuildableDef buildable, ThingDef stuff, IntVec3 cell, Map map, Rot4 rot,
        int thingId = 0, ThingStyleDef? style = null, ColorInt? glower = null)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || buildable == null || map == null) return false;
        // Refuse at the source as well: a stuffable building with no stuff produces a blueprint that every
        // construction work giver throws on, which stalls the colony on both peers.
        if (buildable.MadeFromStuff && stuff is null) return false;
        Publish(CommandCodec.BlueprintCommand(buildable.defName, stuff?.defName ?? "",
            map.uniqueID, cell.x, cell.z, rot.AsInt, SharedColony.NextSequence(), thingId,
            style?.defName ?? string.Empty, EncodeColor(glower)));
        return true;
    }

    /// <summary>`ColorInt?` as <c>r,g,b,a</c>, or empty for "no override". Empty is a real state here: it means the
    /// player did not recolour this glower, which is different from a colour that happens to be black.</summary>
    internal static string EncodeColor(ColorInt? color) =>
        color is { } value ? $"{value.r},{value.g},{value.b},{value.a}" : string.Empty;

    /// <summary>The inverse of <see cref="EncodeColor"/>. Returns null for empty, and null for anything unparseable -
    /// a malformed colour must not become black, which would recolour a glower the player never touched.</summary>
    internal static ColorInt? DecodeColor(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return null;
        var parts = encoded.Split(',');
        if (parts.Length != 4) return null;
        if (!int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var g)
            || !int.TryParse(parts[2], out var b) || !int.TryParse(parts[3], out var a)) return null;
        return new ColorInt(r, g, b, a);
    }

    public static bool CaptureCancelThing(Thing thing)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || thing == null) return false;
        // A cancel *drag* already travelled as one `designator` command carrying every cell, and that replay destroys
        // these same objects on every peer. Publishing one `cancel-thing` per object on top of it doubled the work and
        // - worse - cancelled the local Destroy, so the rectangle stayed on screen while the commands were in flight.
        if (DesignatorSync.SuppressingPerThingCancel) return false;
        Publish(CommandCodec.CancelThingCommand(thing.thingIDNumber, SharedColony.NextSequence()));
        return true;
    }

    public static bool CaptureDraft(Pawn pawn, bool drafted)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null) return false;
        Publish(CommandCodec.DraftCommand(pawn.thingIDNumber, drafted, SharedColony.NextSequence()));
        return true;
    }

    public static void CaptureOrder(Pawn pawn, bool result)
    {
        if (!result || Applying || !SharedColony.Session.AcceptsInput || pawn?.jobs?.curJob is not { } job) return;
        CaptureOrderJob(pawn, job);
    }

    /// <summary>Encodes an order from the job itself, so it can be captured before vanilla runs. A deferred
    /// capture must not read <c>curJob</c>, because the job has not been taken yet.
    ///
    /// <para>The whole job travels as its own Scribe XML, not as a hand-picked field list. The list version lost
    /// <c>targetC</c>, which is <c>JobDriver_HaulToContainer.PrimaryDestIndex</c> - i.e. the pile or blueprint a
    /// colonist is carrying materials to - so on the replica the pawn took the job and had no destination. Rather than
    /// extend the list and risk the next omission, whatever vanilla's <c>Job.ExposeData</c> writes is what travels.</para></summary>
    public static void CaptureOrderJob(Pawn pawn, Job job)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn is null || job?.def is null) return;
        var xml = ScribeBridge.Save(job);
        if (xml is null || xml.Length == 0)
        {
            // Scribe is re-entrant-hostile: this can only happen if a capture fires from inside another save/load.
            // Reported rather than silently dropped, because a dropped order is invisible to the player.
            if (orderSaveFailures++ < 8)
                Log.Warning($"[TurboTick] 无法序列化工作指令（{job.def.defName}），该指令未发送。");
            return;
        }
        Publish(CommandCodec.OrderCommand(pawn.thingIDNumber, xml, SharedColony.NextSequence()));
    }

    private static int orderSaveFailures;

    /// <summary>Captures a forbid change that did not go through <c>ForbidUtility.SetForbidden</c>.
    ///
    /// <c>CompForbiddable.Forbidden</c> (<c>RimWorld/CompForbiddable.cs:14-37</c>) is a settable property that writes
    /// <c>forbiddenInt</c> and notifies the listers itself; the "allow/forbid" gizmo assigns it directly. A capture
    /// patch on <c>SetForbidden</c> alone therefore has a hole exactly where a player clicks that gizmo: the change
    /// stays local and no command is published, so the other peers never learn about it. Measured: one peer applied a
    /// multi-select allow at tick 9880 for five stacks and the other peer recorded nothing.
    ///
    /// Returns true when the change was captured, in which case the caller must cancel the local assignment and let
    /// the command apply it on every peer at the agreed tick - the same contract <c>ForbidCapturePatch</c> uses.
    /// </summary>
    public static bool TryCaptureDirectForbid(Thing? thing, bool forbidden)
    {
        // Record why the decision went the way it did. Without this, the only observable outcome of a refused
        // capture is "the change stayed local", which looks identical to "the patch is not installed at all" -
        // and that ambiguity already cost a round.
        DirectForbidGate.Record(thing?.thingIDNumber ?? -1, forbidden,
            Applying ? "applying" : PlayerActing ? "captured" : "not-acting",
            DirectForbidGate.CallerChain());
        if (Applying || thing is null) return false;
        if (!PlayerActing) { NoteForbidGateBlocked(forbidden); return false; }
        CaptureForbid(thing, forbidden);
        return true;
    }

    public static bool CaptureForbid(Thing thing, bool forbidden)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || thing == null) return false;
        // A click that never reaches this line and a command that never arrives look identical from the
        // outside; this line separates them.
        TraceCapture($"forbid thing={thing.thingIDNumber} value={forbidden} def={thing.def?.defName}");
        QueueForbid(thing.thingIDNumber, forbidden);
        return true;
    }

    // ---------- Bulk forbid batching ----------
    //
    // Why: one "forbid/allow" gesture over a stockpile calls the setter once per stack. Each call captured its own
    // command, so a single gesture became hundreds of commands and the server's per-peer budget (400 per second)
    // refused the tail. Because capturing cancels the local assignment, every refused one left the item visibly
    // unchanged: the player pressed the key and watched nothing happen, with a "server is throttling" banner as the
    // only clue. Measured: 98 refused out of 1030 in one session, all `kind=forbid reason=RateLimited`.
    //
    // The budget is not the bug - a client must not be able to flood the relay. The bug is that one gesture was
    // hundreds of commands. So a gesture accumulates here and leaves as one (or a few) commands at the end of the
    // frame. Capture still cancels the local assignment, which is why the flush runs every frame and never waits
    // for a better moment: a queued flip that never leaves is the same invisible no-op this replaced.
    private static readonly List<int> pendingForbid = [];
    private static readonly List<int> pendingAllow = [];
    /// <summary>Ids seen this frame, so a gesture that flips the same stack twice sends the final value once.</summary>
    private static readonly HashSet<int> pendingForbidSeen = [];
    private static readonly HashSet<int> pendingAllowSeen = [];

    private static void QueueForbid(int thingId, bool forbidden)
    {
        if (thingId <= 0) return;
        // A later flip of the same thing in the same frame replaces the earlier one: RimWorld calls the setter per
        // cell during a drag, and a cell can be visited twice.
        if (forbidden)
        {
            pendingAllow.Remove(thingId);
            pendingAllowSeen.Remove(thingId);
            if (pendingForbidSeen.Add(thingId)) pendingForbid.Add(thingId);
        }
        else
        {
            pendingForbid.Remove(thingId);
            pendingForbidSeen.Remove(thingId);
            if (pendingAllowSeen.Add(thingId)) pendingAllow.Add(thingId);
        }
    }

    /// <summary>How many forbid/allow commands this peer has actually queued for delivery. Read by the replica probe:
    /// "the gesture was captured" and "a command left the machine" are different claims, and only the second one
    /// explains a flag that never changes on the other side.</summary>
    internal static int ForbidCommandsSent { get; private set; }

    /// <summary>Ids waiting to be flushed this frame. A non-zero value after a flush means the command never left.</summary>
    internal static int FlushPendingForbidCount() => pendingForbid.Count + pendingAllow.Count;

    /// <summary>Ships everything a gesture accumulated during this frame. Called once per frame, before and
    /// regardless of the tick, because capture already cancelled the local effect.</summary>
    public static void FlushForbidBatches()
    {
        if (pendingForbid.Count > 0) Flush(CommandCodec.ForbidBatch, pendingForbid, forbidden: true);
        if (pendingAllow.Count > 0) Flush(CommandCodec.ForbidBatch, pendingAllow, forbidden: false);
    }

    private static void Flush(string kind, List<int> ids, bool forbidden)
    {
        ids.Sort();
        // Small sets stay on the single-thing command: one flip is the common case, and it keeps the plain path
        // (and its tests and trace lines) exactly as it was.
        for (var offset = 0; offset < ids.Count; offset += CommandCodec.MaxBatchIds)
        {
            var count = Math.Min(CommandCodec.MaxBatchIds, ids.Count - offset);
            var slice = ids.GetRange(offset, count);
            Publish(count == 1
                ? CommandCodec.ForbidCommand(slice[0], forbidden, SharedColony.NextSequence())
                : CommandCodec.ForbidBatchCommand(forbidden, slice, SharedColony.NextSequence()));
            ForbidCommandsSent++;
        }
        ids.Clear();
        if (forbidden) pendingForbidSeen.Clear(); else pendingAllowSeen.Clear();
    }

    // ---------- Generic gizmo capture ----------
    //
    // Enumerating every player operation does not scale: each one needs its own codec, apply path and
    // divergence surface, and a third-party mod's gizmo can never be enumerated at all. But every gizmo
    // press funnels through Command.ProcessInput, and every game object already knows how to describe its
    // own state through ExposeData. So the target's state is sampled around the click and shipped whole.
    //
    // It also composes with the dedicated commands above instead of duplicating them: those cancel the
    // inner method, so the click leaves the local state untouched and the before/after comparison here sees
    // no change.

    internal sealed record GizmoSnapshot(char Kind, int Id, string? Before);

    internal static List<GizmoSnapshot>? CaptureGizmoTargets()
    {
        if (!PlayerActing) return null;
        var selector = Find.Selector;
        if (selector?.SelectedObjects is not { } selected) return null;
        List<GizmoSnapshot>? snapshots = null;
        foreach (var entry in selected)
        {
            // Zones carry their own gizmos - a stockpile's allow/deny buttons act on the zone, not on any
            // thing - so capturing only Things silently dropped every storage filter change.
            if (entry is Zone zone)
            {
                var zoneBefore = ScribeBridge.Save(zone);
                if (zoneBefore is null) continue;
                (snapshots ??= []).Add(new GizmoSnapshot('z', zone.ID, zoneBefore));
                continue;
            }
            // World objects carry gizmos too - a caravan's "settle here", a settlement's "attack", a gravship's launch
            // buttons - and their `ProcessInput` is the same method this patch targets. Without this branch every one of
            // them was sampled to nothing and the click stayed local, which is the same defect the float-menu path had:
            // a selection on the planet layer holds no `Thing` at all.
            if (entry is RimWorld.Planet.WorldObject worldObject)
            {
                if (worldObject.ID < 0) continue;
                var worldBefore = ScribeBridge.Save(worldObject);
                if (worldBefore is null) continue;
                (snapshots ??= []).Add(new GizmoSnapshot('w', worldObject.ID, worldBefore));
                continue;
            }
            if (entry is not Thing thing) continue;
            // A blueprint or a frame is a construction target, and its state is produced by the colony's own
            // simulation on both peers - it is not player input. Worse, applying a state fragment to one takes it off
            // the grid and puts it back: `Thing.ExposeData` clears the map index while loading, so `ScribeBridge.Load`
            // has to despawn and respawn it. Measured from a player: "a placed blueprint suddenly becomes unplaced and
            // appears elsewhere while a colonist carries materials to it, then the game reports a desync" - the pawn's
            // haul target was invalidated for a frame and it re-targeted a different blueprint. Skip them: nothing
            // here is information the other peers do not already have.
            if (thing is Blueprint || thing is Frame) continue;
            var before = ScribeBridge.Save(thing);
            // A null fragment means Scribe is busy (we are inside a save). Nothing to compare against.
            if (before is null) continue;
            (snapshots ??= []).Add(new GizmoSnapshot('t', thing.thingIDNumber, before));
        }
        return snapshots;
    }

    /// <summary>Snapshot for an owner assignment: the assignable thing, plus the pawn whose `Pawn_Ownership` records the
    /// claim.
    ///
    /// <para><b>Two objects, because the write lands in two places.</b> `CompAssignableToPawn_Bed.TryAssignPawn`
    /// (`:529069`) calls `pawn.ownership.ClaimMeditationSpot` / `ClaimBedIfNonMedical`, and `TryUnassignPawn`
    /// (`:529074`) calls the matching `Unclaim*`. The comp's own `assignedPawns` is scribed
    /// (`:529593`), and so is the pawn's `ownedBed`/`assignedGrave`/`assignedThrone` (`:473155-473159`). Sending only the
    /// thing would move the bed's list and leave the pawn pointing at the old one; the needs tab, the colonist bar and
    /// `Pawn_Ownership.ExposeData`'s repair pass (`:473168-473197`) all read the pawn's side.</para>
    ///
    /// <para><b>A pawn fragment is small here.</b> `ScribeBridge.Save(pawn)` is the same helper the gizmo path uses for
    /// things, and `Pawn` is `IExposable`; the ledger's `pawn-payload-size` measurement (8963 B/pawn) was for the
    /// *frame* payload, which carries every subsystem. This is one pawn per assignment click.</para></summary>
    internal static List<GizmoSnapshot>? CaptureCompOwner(CompAssignableToPawn? assignable, Pawn? pawn)
    {
        if (!PlayerActing) return null;
        List<GizmoSnapshot>? snapshots = null;
        var parent = assignable?.parent;
        if (parent is not null)
        {
            var before = ScribeBridge.Save(parent);
            if (before is { Length: > 0 }) (snapshots ??= []).Add(new GizmoSnapshot('t', parent.thingIDNumber, before));
        }
        // The pawn's ownership is a separate `IExposable`. A dead pawn being assigned to a grave is still a spawned
        // Thing (`Corpse.InnerPawn`), so the id resolves through the same lookup the flush path uses.
        if (pawn is not null)
        {
            var before = ScribeBridge.Save(pawn);
            if (before is { Length: > 0 }) (snapshots ??= []).Add(new GizmoSnapshot('t', pawn.thingIDNumber, before));
        }
        return snapshots;
    }

    internal static void FlushGizmoTargets(List<GizmoSnapshot>? snapshots)
    {
        if (snapshots is null || snapshots.Count == 0 || !PlayerActing) return;
        foreach (var snapshot in snapshots)
        {
            var target = snapshot.Kind switch
            {
                'z' => (IExposable?)FindZoneById(snapshot.Id),
                'w' => FindWorldObjectById(snapshot.Id),
                _ => FindThingById(snapshot.Id)
            };
            if (target is null) continue;
            var after = ScribeBridge.Save(target);
            if (after is null || string.Equals(after, snapshot.Before, StringComparison.Ordinal)) continue;
            // The server drops a command whose payload exceeds 64 KB, so an oversized fragment would fail
            // the whole action silently on the other peers. Refuse it here where the reason is visible.
            // Counted in bytes, not characters: the server checks Payload.Length (UTF-8), and a label with
            // three-byte characters passed this check at 60 K characters and was rejected at ~180 KB.
            var payloadBytes = Encoding.UTF8.GetByteCount(after);
            if (payloadBytes > MaxThingStatePayload)
            {
                Log.Warning($"[TurboTick] 状态过大（{payloadBytes} 字节），未同步：{snapshot.Kind}#{snapshot.Id}");
                continue;
            }
            Publish(CommandCodec.StateCommand(snapshot.Kind, snapshot.Id, after, SharedColony.NextSequence()));
            // Both callers need this number: the gizmo path counts it for its own summary, and the float-menu path uses it
            // to tell "the option changed something" from "there was nothing to sample" - two cases that both look like
            // zero commands from outside.
            GizmoStatesSent++;
            // The click already changed this world; hold until the ordered tick arrives (C-04).
            SharedColony.HoldAfterLocalAction();
        }
    }

    /// <summary>Server-side command payload ceiling is 64 KB; leave room for the id and line separator.</summary>
    private const int MaxThingStatePayload = 60 * 1024;

    /// <summary>How many `thing-state` fragments the gizmo and float-menu paths have published. One counter for both
    /// because they share <see cref="FlushGizmoTargets"/>; the float-menu path reads the delta.</summary>
    internal static long GizmoStatesSent { get; private set; }

    internal static long FloatMenuChoicesSent { get; private set; }
    internal static long FloatMenuChoicesEmpty { get; private set; }
    internal static string? LastFloatMenuNote { get; private set; }

    /// <summary>Samples the world around a right-click before the option's action runs, and publishes whatever the
    /// action changed.
    ///
    /// <para><b>Why a snapshot rather than a per-option command.</b> `FloatMenuOption.Chosen(bool, FloatMenu)`
    /// (`:179550-179571`) runs a bare <c>Action</c> delegate. There is no shared shape to decode: a provider may call
    /// `WorkGiver` machinery, open a dialog, set a faction, start a ritual, or write three fields on a comp. The gizmo path
    /// already solved the same problem with before/after Scribe fragments (<see cref="CaptureGizmoTargets"/> +
    /// <see cref="FlushGizmoTargets"/>), and `thing-state` already carries any `IExposable` - so the only thing missing was
    /// a capture point, not a channel.</para>
    ///
    /// <para><b>What is sampled.</b> The selection first, because that is what the gizmo path samples and most options act
    /// on a selected object. Then the option's own <c>revalidateClickTarget</c> (`:179283`, set by the constructor at
    /// `:179482`), which is the thing the menu was opened on and is very often the actual subject - right-clicking a
    /// stack to haul it selects the <em>pawn</em>, not the stack. Sampling only the selection would therefore publish
    /// nothing for the most common right-click in the game, and "nothing was published" is indistinguishable from "the
    /// menu was empty".</para>
    ///
    /// <para>Returns null when nothing is selected and the option names no target, so the caller can count that case
    /// instead of silently sending zero commands.</para></summary>
    internal static List<GizmoSnapshot>? CaptureFloatMenuTargets(Thing? clickTarget,
        RimWorld.Planet.WorldObject? clickWorldTarget = null)
    {
        if (!PlayerActing) return null;
        List<GizmoSnapshot>? snapshots = null;
        // Three id spaces share this list, and they are keyed separately because a world object and a thing can hold the
        // same number: `Zone.ID` comes from `UniqueIDsManager.nextZoneID` (`:192` region), a thing id from `nextThingID`,
        // and a `WorldObject.ID` from its own counter. A single `HashSet<int>` deduplicating across all three would
        // silently drop one of the pair.
        var seenThings = new HashSet<int>();
        var seenZones = new HashSet<int>();
        var seenWorld = new HashSet<int>();
        void Take(Thing? thing)
        {
            if (thing is null) return;
            // Blueprints and frames are excluded for the same reason the gizmo path excludes them: their state is produced
            // by the colony's own simulation on every peer, and applying a fragment to one despawns and respawns it,
            // invalidating a hauler's target mid-carry.
            if (thing is Blueprint || thing is Frame) return;
            if (!seenThings.Add(thing.thingIDNumber)) return;
            var before = ScribeBridge.Save(thing);
            if (before is null) return;
            (snapshots ??= []).Add(new GizmoSnapshot('t', thing.thingIDNumber, before));
        }
        void TakeWorld(RimWorld.Planet.WorldObject? worldObject)
        {
            if (worldObject is null || worldObject.ID < 0) return;
            if (!seenWorld.Add(worldObject.ID)) return;
            var before = ScribeBridge.Save(worldObject);
            if (before is null) return;
            (snapshots ??= []).Add(new GizmoSnapshot('w', worldObject.ID, before));
        }
        if (Find.Selector?.SelectedObjects is { } selected)
            foreach (var entry in selected)
            {
                if (entry is Zone zone)
                {
                    if (!seenZones.Add(zone.ID)) continue;
                    if (ScribeBridge.Save(zone) is { } zoneBefore)
                        (snapshots ??= []).Add(new GizmoSnapshot('z', zone.ID, zoneBefore));
                    continue;
                }
                // A `Settlement`, `MapParent` or caravan is a world object: its float menu is built on the planet layer, and
                // a selection there holds no `Thing` at all - so without this branch every world-map right-click would
                // sample nothing, which reads identically to "the capture is broken".
                if (entry is RimWorld.Planet.WorldObject worldObject) { TakeWorld(worldObject); continue; }
                if (entry is Thing thing) Take(thing);
            }
        Take(clickTarget);
        TakeWorld(clickWorldTarget);
        return snapshots;
    }

    /// <summary>Publishes what the chosen option changed, and counts the case where there was nothing to compare.
    ///
    /// <para><b>Why the "nothing sampled" count matters.</b> A right-click option can legitimately change nothing on any
    /// sampled object - "prioritise hauling" queues a job on the <em>pawn</em>, and the pawn's job is carried by the job
    /// channel, not by a Scribe fragment. So an empty flush is often correct. But it is also the shape of a broken capture,
    /// and from outside the two are identical; counting it separately is what makes the census readable.</para></summary>
    internal static void FlushFloatMenuTargets(List<GizmoSnapshot>? snapshots)
    {
        if (!PlayerActing) return;
        if (snapshots is null || snapshots.Count == 0) { FloatMenuChoicesEmpty++; return; }
        var before = GizmoStatesSent;
        FlushGizmoTargets(snapshots);
        if (GizmoStatesSent > before)
        {
            FloatMenuChoicesSent += GizmoStatesSent - before;
            LastFloatMenuNote = snapshots.Count + " 个采样对象";
        }
        else FloatMenuChoicesEmpty++;
    }

    /// <summary>A world object by its own `ID` (`WorldObject.ID`, `:798297`). Scans `AllWorldObjects` for the same reason
    /// <see cref="FindZoneById"/> scans zones: there is no id-keyed index, and the world holds tens of objects, not
    /// thousands.</summary>
    private static RimWorld.Planet.WorldObject? FindWorldObjectById(int id)
    {
        var objects = Find.WorldObjects?.AllWorldObjects;
        if (objects is null) return null;
        for (var i = 0; i < objects.Count; i++)
            if (objects[i] is { } worldObject && worldObject.ID == id) return worldObject;
        return null;
    }

    private static Zone? FindZoneById(int id)
    {
        foreach (var map in Find.Maps)
            foreach (var zone in map.zoneManager.AllZones)
                if (zone.ID == id) return zone;
        return null;
    }

    private static Thing? FindThingById(int id)
    {
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing.thingIDNumber == id) return thing;
        return null;
    }

    public static void ApplyState(byte[] payload)
    {
        if (!CommandCodec.TryDecodeState(payload, out var kind, out var id, out var xml)) return;
        if (kind == 'z')
        {
            var zone = FindZoneById(id);
            if (zone is null) return;
            ApplyZoneState(zone, xml);
            return;
        }
        if (kind == 'w')
        {
            var worldObject = FindWorldObjectById(id);
            if (worldObject is null) return;
            if (!ScribeBridge.Load(worldObject, xml))
                Log.Warning($"[TurboTick] 应用状态失败：w#{id}");
            return;
        }
        var thing = FindThingById(id);
        if (thing is null) return;
        if (!ScribeBridge.Load(thing, xml))
            Log.Warning($"[TurboTick] 应用状态失败：{kind}#{id}");
    }

    /// <summary>A zone's gizmo state is its storage settings; its cells are changed by the zone-cell command,
    /// which goes through <see cref="Zone.AddCell"/>. Zone.ExposeData, on the other hand, assigns the cells
    /// list wholesale, which bypasses the zone grid and the slot group - so a fragment that carries a
    /// different cell set is rejected instead of being applied corruptly.</summary>
    private static void ApplyZoneState(Zone zone, string xml)
    {
        var cellsBefore = zone.CellCount;
        if (!ScribeBridge.Load(zone, xml))
        {
            Log.Warning($"[TurboTick] 应用区域状态失败：z#{zone.ID}");
            return;
        }
        if (zone.CellCount != cellsBefore)
        {
            Log.Error($"[TurboTick] 区域状态片段改动了格子数（{cellsBefore} → {zone.CellCount}），z#{zone.ID}；"
                + "格子变化必须走 zone-cell 命令，本次改动已忽略其格子部分。");
            return;
        }
        zone.PostRegister();
    }

    public static bool CaptureResearch(ResearchProjectDef? project)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return false;
        TraceCapture($"research {project?.defName ?? "<stop>"}");
        Publish(CommandCodec.ResearchCommand(project?.defName, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>Captures a player decision that takes a pawn out of (or into) the player's faction.
    ///
    /// <para><b>The gap this closes.</b> Three actions end with the same kind of write and none of them had a capture
    /// point: `PawnBanishUtility.Banish` (`:444883`), `RecruitUtility.Recruit` (`:456919`) and
    /// `ReleaseAnimalToWildUtility.DoReleaseAnimal`. All three are reached from a **confirmation dialog** — the ban
    /// button on the character card (`:642515`), the caravan's abandon menu (`:786802`), the prisoner tab's recruit
    /// gizmo — and the generic gizmo capture patches `Command.ProcessInput`, which never sees the callback a
    /// `Dialog_MessageBox` runs. So a click on those buttons changed the clicking peer only.</para>
    ///
    /// <para><b>Not a channel.</b> `ReplicaPawn.FactionLoadId` carries the resulting faction, and the `removed`
    /// frame carries a despawn. What it cannot carry is the <em>decision</em>: the authority would learn that a pawn
    /// changed faction without knowing who changed it, and banishment also fires thoughts, records, a tale and quest
    /// signals (`:444883` body) that the faction field alone never reproduces. So the decision travels and the
    /// authority re-runs the game's own method.</para></summary>
    /// <summary>Same as <see cref="CapturePawnLeave"/> with the gate folded in, for a prefix that has nothing else to
    /// decide. Kept separate so the banish path - which also has to count a refused gate - can call the other one.</summary>
    internal static void CapturePawnLeaveIfActing(string op, Pawn? pawn)
    {
        if (PlayerActing) CapturePawnLeave(op, pawn);
    }

    public static bool CapturePawnLeave(string op, Pawn? pawn)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn is null) return false;
        TraceCapture($"pawn-leave {op} pawn={pawn.thingIDNumber}");
        PawnLeaveSent++;
        var faction = op == "recruit" ? (pawn.Faction?.loadID ?? -1) : -1;
        Publish(CommandCodec.PawnLeaveCommand(op, pawn.thingIDNumber, faction, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>How many decisions were published, and how many could not be replayed. A count of zero after a
    /// player pressed one of those buttons is the observable form of "the patch is not installed".</summary>
    internal static long PawnLeaveSent { get; private set; }
    internal static long PawnLeaveApplied { get; private set; }
    internal static long PawnLeaveMissed { get; private set; }
    internal static string? LastPawnLeave { get; private set; }

    /// <summary>Re-runs the decision on the authority. `Applying` is set around it because each of these methods
    /// writes state a capture patch could otherwise observe and publish again.</summary>
    private static void ApplyPawnLeave(byte[] payload)
    {
        if (!CommandCodec.TryDecodePawnLeave(payload, out var op, out var pawnId, out _)) { PawnLeaveMissed++; return; }
        Pawn? pawn = null;
        foreach (var map in Find.Maps)
            foreach (var each in map.mapPawns.AllPawnsSpawned)
                if (each.thingIDNumber == pawnId) { pawn = each; break; }
        // A banished pawn may already be off-map on this peer; `AllPawns` covers that without the spawn filter.
        pawn ??= Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn is null)
        {
            PawnLeaveMissed++;
            LastPawnLeave = $"找不到 pawn={pawnId}（{op}）";
            return;
        }
        var previous = Applying;
        Applying = true;
        try
        {
            switch (op)
            {
                // `giveThoughts: false` because the thoughts belong to the pawns that stay, and those already got
                // them on the peer that clicked - this is the authority applying the same decision, not a second one.
                case "banish": PawnBanishUtility.Banish(pawn, giveThoughts: false); break;
                case "recruit": RecruitUtility.Recruit(pawn, Faction.OfPlayer, null); break;
                // `releasedBy` is required non-null: the body calls `releasedBy.interactions.TryInteractWith`
                // (`:620966`). Any colonist is correct - the interaction is a social log entry, not a decision.
                case "release":
                    var releaser = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).FirstOrDefault()
                        ?? pawn;
                    ReleaseAnimalToWildUtility.DoReleaseAnimal(pawn, releaser);
                    break;
                default: PawnLeaveMissed++; return;
            }
            PawnLeaveApplied++;
            LastPawnLeave = op + " " + pawnId;
        }
        catch (Exception error)
        {
            PawnLeaveMissed++;
            LastPawnLeave = $"{op} {pawnId} 失败 {error.GetType().Name}: {error.Message}";
        }
        finally { Applying = previous; }
    }

    public static void ApplyResearch(byte[] payload)
    {
        if (!CommandCodec.TryDecodeResearch(payload, out var defName)) return;
        if (defName is null)
        {
            var current = Find.ResearchManager.GetProject(null);
            if (current is not null) Find.ResearchManager.StopProject(current);
            return;
        }
        var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName);
        if (project is not null) Find.ResearchManager.SetCurrentProject(project);
    }

    public static bool CapturePriority(Pawn pawn, WorkTypeDef w, int priority)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null || w == null) return false;
        Publish(CommandCodec.PriorityCommand(pawn.thingIDNumber, w.defName, priority, SharedColony.NextSequence()));
        return true;
    }

    public static bool CaptureZoneCell(Zone zone, IntVec3 cell, bool add)
    {
        // Record the guard values before deciding. A capture that returns here leaves the change local and publishes
        // nothing, and from the outside that is indistinguishable from a missing patch - the same ambiguity that
        // already cost a round on the forbid path. Measured need: a stockpile zone with 100 cells appeared on one
        // peer only, `zone-add` was never refused by the gate, and the patch was installed, so the drop has to be
        // one of these guards.
        ZoneGate.Record("cell " + (zone?.ID ?? -1) + " " + cell.x + "|" + cell.z + (add ? " +" : " -")
            + " applying=" + Applying
            + " accepts=" + SharedColony.Session.AcceptsInput
            + " zoneMap=" + (zone?.Map is not null),
            SharedActions.PlayerActing, ZoneGate.CallerChain());
        if (Applying || !SharedColony.Session.AcceptsInput || zone?.Map == null) return false;
        var type = zone is Zone_Growing ? "growing" : "stockpile";
        // `pub:begin` and `pub:send` bracket the publish chain. Measured need: the capture recorded all three guards
        // as passing, the send gate recorded no drop, no "send failed" status appeared, and the server saw nothing -
        // so the command vanished between capture and send, and neither existing instrument covers that interval.
        // A step that is meant to produce output has to prove it was reached, not only that it did not complain.
        PublishGate.Note("begin zone-cell");
        try
        {
            Publish(CommandCodec.ZoneCellCommand(type, zone.ID, zone.Map.uniqueID, cell.x, cell.z, add, SharedColony.NextSequence(), zone.label));
            PublishGate.Note("sent zone-cell");
        }
        catch (Exception error)
        {
            PublishGate.Note("throw zone-cell " + error.GetType().Name + ":" + error.Message);
            throw;
        }
        // The cell is already in this world; hold until the ordered tick arrives (C-04).
        SharedColony.HoldAfterLocalAction();
        return true;
    }

    public static bool CaptureZoneDelete(Zone zone)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || zone?.Map == null) return false;
        Publish(CommandCodec.ZoneDeleteCommand(zone.Map.uniqueID, zone.ID, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>Samples a pawn's four policy trackers, so a panel click can be replayed as one command.
    ///
    /// <para>These four are the assignment panels and they have no gizmo, so <see cref="GizmoStatePatch"/> never saw
    /// them: apparel (<c>Pawn_OutfitTracker.CurrentApparelPolicy</c>), drugs
    /// (<c>Pawn_DrugPolicyTracker.CurrentPolicy</c>), food (<c>Pawn_FoodRestrictionTracker.CurrentFoodPolicy</c>) and
    /// the hour-by-hour timetable (<c>Pawn_TimetableTracker.SetAssignment</c>). All four are <c>IExposable</c>, and
    /// their <c>ExposeData</c> writes just the policy pointer and the timetable list, so the whole tracker travels as
    /// its own Scribe XML - which is also what makes a modded field travel.</para>
    ///
    /// <para>Returns null when nothing is selected or Scribe is busy, exactly like the gizmo sampler.</para>
    /// </summary>
    /// <para><c>Stock</c> is the "carry" column's tracker, added after `tracker-probe` measured
    /// <c>inventoryStock</c> on three pawns with no channel covering it. It rides in the same snapshot rather than a
    /// sixth channel because it has exactly the same shape: a pawn-owned `IExposable` written by a method.</para>
    internal sealed record PolicySnapshot(int PawnId, string? Outfit, string? Drug, string? Food, string? Timetable,
        string? Stock = null);

    internal static PolicySnapshot? CapturePolices(Pawn pawn)
    {
        if (!PlayerActing || pawn is null) return null;
        var outfit = ScribeBridge.SaveOrNull(pawn.outfits);
        var drug = ScribeBridge.SaveOrNull(pawn.drugs);
        var food = ScribeBridge.SaveOrNull(pawn.foodRestriction);
        var timetable = ScribeBridge.SaveOrNull(pawn.timetable);
        var stock = ScribeBridge.SaveOrNull(pawn.inventoryStock);
        if (outfit is null && drug is null && food is null && timetable is null && stock is null) return null;
        return new PolicySnapshot(pawn.thingIDNumber, outfit, drug, food, timetable, stock);
    }

    /// <summary>Publishes only the trackers that actually changed. A panel click rewrites one of the four, and sending
    /// all four would make an outfit change able to overwrite a timetable edit that landed first.</summary>
    internal static void FlushPolicies(PolicySnapshot? before, Pawn pawn)
    {
        if (before is null || pawn is null || !PlayerActing) return;
        if (pawn.thingIDNumber != before.PawnId) return;
        var outfit = Changed(before.Outfit, ScribeBridge.SaveOrNull(pawn.outfits));
        var drug = Changed(before.Drug, ScribeBridge.SaveOrNull(pawn.drugs));
        var food = Changed(before.Food, ScribeBridge.SaveOrNull(pawn.foodRestriction));
        var timetable = Changed(before.Timetable, ScribeBridge.SaveOrNull(pawn.timetable));
        var stock = Changed(before.Stock, ScribeBridge.SaveOrNull(pawn.inventoryStock));
        if (outfit is null && drug is null && food is null && timetable is null && stock is null) return;
        var bytes = (outfit?.Length ?? 0) + (drug?.Length ?? 0) + (food?.Length ?? 0) + (timetable?.Length ?? 0)
            + (stock?.Length ?? 0);
        if (bytes > MaxThingStatePayload)
        {
            Log.Warning($"[TurboTick] 策略状态过大（{bytes} 字符），未同步：pawn#{before.PawnId}");
            return;
        }
        Publish(CommandCodec.PolicyCommand(before.PawnId, outfit, drug, food, timetable, SharedColony.NextSequence(), stock));
        SharedColony.HoldAfterLocalAction();

        static string? Changed(string? was, string? now) =>
            now is null || string.Equals(was, now, StringComparison.Ordinal) ? null : now;
    }

    /// <summary>Publishes a surgery bill whole, because `bill-add` cannot carry its body part.
    ///
    /// <para>Called after `CreateSurgeryBill` returns, not before: the part is assigned *after* `AddBill`
    /// (`:644616-644623`), so a capture inside the `AddBill` prefix sees `Part == null` and a capture that runs before
    /// the method returns cannot exist. `HealthCardUtility.CreateSurgeryBill` is the single place `Bill_Medical` is
    /// constructed (`:644618`, the only `new Bill_Medical` in the assembly), so one postfix covers all eleven call
    /// sites - including the ones that assign `<c>xenogerm</c>` afterwards (`:596896`) or pass unique ingredients
    /// (`:594799`).</para>
    ///
    /// <para><b>The local bill must survive.</b> Unlike every other capture in this file this one does **not** cancel
    /// the call: the bill is already in the stack and the part already assigned by the time the postfix runs, so there
    /// is nothing left to defer. The receiver adds its own copy from the fragment, which means the two peers each hold a
    /// bill with the same recipe and part - the same end state a cancelled-and-replayed command produces, without the
    /// window in which `Part` could not be set.</para></summary>
    internal static void CaptureSurgeryBill(Pawn? medPawn, Bill_Medical? bill, int replaces = -1)
    {
        if (bill is null || medPawn is null || !PlayerActing) return;
        var xml = ScribeBridge.SaveOrNull(bill);
        if (xml is null) return;
        // `Bill_Medical` fragments are small (a part name and an ingredient list), but the same 60 KB ceiling the
        // server enforces applies; refuse here where the reason is visible rather than losing the whole action.
        if (xml.Length > MaxThingStatePayload)
        {
            Log.Warning($"[TurboTick] 手术账单过大（{xml.Length} 字符），未同步：pawn#{medPawn.thingIDNumber}");
            return;
        }
        Publish(CommandCodec.SurgeryBillCommand(medPawn.thingIDNumber, bill.recipe.defName, xml,
            SharedColony.NextSequence(), replaces));
    }

    private static void ApplySurgeryBill(byte[] payload)
    {
        if (!CommandCodec.TryDecodeSurgeryBill(payload, out var pawnId, out var recipeDefName, out var xml, out var replaces)
            || xml is null) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.BillStack is null) return;
        var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
        if (recipe is null)
        {
            if (surgeryLoadFailures++ < 8) Log.Warning($"[TurboTick] 手术账单的配方不存在：{recipeDefName}");
            return;
        }
        // A `Bill_Medical` has to exist before its fragment can be loaded, and constructing one needs the recipe - which
        // is why the recipe travels beside the fragment rather than being parsed back out of it.
        var bill = new Bill_Medical(recipe, null);
        if (!ScribeBridge.Load(bill, xml))
        {
            if (surgeryLoadFailures++ < 8) Log.Warning($"[TurboTick] 手术账单无法应用（pawn={pawnId}，配方 {recipeDefName}）");
            return;
        }
        // Replacement first, while the index still means what the sender meant: deleting later could hit a different
        // bill if this add changed the ordering.
        if (replaces >= 0 && replaces < pawn.BillStack.Count) pawn.BillStack.Delete(pawn.BillStack[replaces]);
        // Added after loading, so the part is already set: the `Part` setter refuses to run before the bill is in a
        // stack (`:442400-442402`), and `Scribe_BodyParts.Look` writes the field directly, which is the only order that
        // works on both sides. `CaptureBillAdd` returns immediately while `Applying`, so this does not re-publish.
        pawn.BillStack.AddBill(bill);
        surgeryApplied++;
    }

    private static int surgeryApplied;
    private static int surgeryLoadFailures;

    /// <summary>The apply counter, read-only, for `surgery-probe` to diff around a real `CreateSurgeryBill` call.</summary>
    internal static int SurgeryAppliedForProbe => surgeryApplied;

    /// <summary>Says whether the capture paths execute in this process, so a probe cannot present a reading taken where
    /// nothing can be captured as evidence about the capture.
    ///
    /// <para><b>Why this exists.</b> Four probes in this project have now answered confidently from a path that could
    /// not run: `floatmenu-probe`, `comp-probe`, `designator-coverage` and `surgery-probe` all measure clicks, and every
    /// click-capture in this file starts with <see cref="PlayerActing"/>. On the dedicated server that is false by
    /// construction (<see cref="SimulationPeerProcess"/> is true because the server refuses every command a simulation
    /// peer sends), so the correct reading there is "not measurable", not zero.</para></summary>
    internal static string PlayerActingNote() =>
        SimulationPeerProcess
            ? "simulationPeer=True（专服：PlayerActing 恒为假，捕获路径不执行，任何 0 都是「测不到」而不是「没发生」）"
            : "simulationPeer=False（客户端：PlayerActing 可为真，捕获路径会执行）";

    /// <summary>Census row for the surgery-bill channel. Prints the medical bills this peer holds, because "applied 0"
    /// on a colony that has no surgery queued is the correct reading and looks identical to a broken channel.</summary>
    internal static string SurgerySummary()
    {
        var bills = 0;
        var withPart = 0;
        var pawns = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawns)
            {
                if (pawn?.BillStack is null) continue;
                pawns++;
                foreach (var bill in pawn.BillStack.Bills)
                {
                    if (bill is not Bill_Medical medical) continue;
                    bills++;
                    if (medical.Part is not null) withPart++;
                }
            }
        return $"本端 {pawns} 个小人有账单栈，手术账单 {bills} 张（其中 {withPart} 张有部位）"
            + $" / 应用 {surgeryApplied} / 失败 {surgeryLoadFailures}";
    }

    public static bool CaptureBillAdd(BillStack stack, Bill bill)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || stack?.billGiver is not Thing building || bill?.recipe == null) return false;
        var repeat = bill is Bill_Production prod ? prod.repeatCount : 1;
        Publish(CommandCodec.BillAddCommand(building.thingIDNumber, bill.recipe.defName, repeat, SharedColony.NextSequence()));
        return true;
    }

    public static bool CaptureBillDelete(BillStack stack, Bill bill)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || stack?.billGiver is not Thing building || bill == null) return false;
        var index = stack.IndexOf(bill);
        if (index >= 0)
        {
            Publish(CommandCodec.BillDeleteCommand(building.thingIDNumber, index, SharedColony.NextSequence()));
            return true;
        }
        return false;
    }

    /// <summary>Reports a training-tab checkbox to the host, which runs the recursion there.
    ///
    /// <para><see cref="Pawn_TrainingTracker.SetWantedRecursive"/> is not a plain field write: checking a skill also
    /// checks every prerequisite, and unchecking one unchecks every dependent (`:476801-476819`). Encoding that rule
    /// here would drift the first time vanilla changes the tree, so the commander is sent and the host runs the game's
    /// own method.</para></summary>
    public static bool CaptureTrainingWanted(Pawn pawn, TrainableDef? def, bool wanted)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn?.training is null || def?.defName is null) return false;
        Publish(CommandCodec.TrainingWantedCommand(pawn.thingIDNumber, def.defName, wanted, SharedColony.NextSequence()));
        return true;
    }

    public static void ApplyTrainingWanted(byte[] payload)
    {
        if (!CommandCodec.TryDecodeTrainingWanted(payload, out var pawnId, out var defName, out var wanted)) return;
        var def = DefDatabase<TrainableDef>.GetNamedSilentFail(defName);
        if (def is null) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        pawn?.training?.SetWantedRecursive(def, wanted);
    }

    public static bool CaptureAreaAssign(Pawn pawn, Area area)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null) return false;
        Publish(CommandCodec.AreaAssignCommand(pawn.thingIDNumber, area?.ID ?? -1, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>Captures one of `Dialog_ManageAreas`' three buttons. Returns true when the command was published, in
    /// which case the caller cancels the local call and lets the ordered tick apply it on every peer - the same
    /// contract every other capture uses, so the grid cannot end up inverted twice.</summary>
    public static bool CaptureAreaOp(Area area, string op)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || area is null || area.ID < 0) return false;
        if (!area.Mutable && op == "delete") return false; // vanilla refuses this itself; no command needed
        Publish(CommandCodec.AreaOpCommand(op, area.ID, SharedColony.NextSequence()));
        SharedColony.HoldAfterLocalAction();
        return true;
    }

    public static bool CaptureTransport(CompTransporter transporter, TransferableOneWay transferable, int count)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return false;
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return false;
        if (transporter?.parent is not { } pod || transferable?.AnyThing is not { } thing || count <= 0) return false;
        Publish(CommandCodec.TransportCommand(pod.thingIDNumber, thing.thingIDNumber, count, SharedColony.NextSequence()));
        return true;
    }

    /// <summary>The same capture for a `MapPortal`, which is a **different class with a same-named method**.
    ///
    /// <para><b>Measured gap.</b> `MapPortal.AddToTheToLoadList` (`:571926`) has nothing to do with
    /// `CompTransporter.AddToTheToLoadList` (`:561716`) except its name, and the existing patch is attached to the
    /// latter. So `Dialog_EnterPortal.TryAccept` (`:633219-633229`) - the dialog that loads a gravship gate - reached
    /// nothing: its loop calls `portal.AddToTheToLoadList`, the prefix never fired, and the colony that simulates never
    /// learned what was being loaded. Every other operation in that dialog is presentation, which is why the ledger
    /// classified the whole type as 「绘制/相机/输入」; the classification was right about everything except the one
    /// call that writes.</para></summary>
    /// <summary>The cancel half. Counted separately from a load because the two are different writes on the same
    /// object, and a load command that silently became a cancel would empty the plan instead of filling it.</summary>
    /// <summary>Reports an incident that a player's own action queued.
    ///
    /// <para><b>Why the guard is `PlayerActing` and nothing more specific.</b> A storyteller- or quest-driven
    /// `IncidentQueue.Add` happens inside `IncidentQueueTick` (`:384088`), which runs in the tick the replica never
    /// executes - so `PlayerActing` (which excludes `TickExecuting`) already separates "a player caused this" from "the
    /// simulation caused this" without inspecting the call stack.</para></summary>
    public static bool CaptureIncident(IncidentDef? def, int fireTick, IncidentParms? parms)
    {
        if (!PlayerActing) return false;
        // `target` is an `IIncidentTarget`: a map is one, a caravan is another. Something that is not a map has nowhere
        // to receive the traders, so it is refused rather than sent with `uniqueID` of -1.
        if (def?.defName is not { Length: > 0 } name || parms?.target is not Map map) return false;
        Publish(CommandCodec.DiplomacyCommand(name, parms.faction?.loadID ?? -1, parms.traderKind?.defName ?? string.Empty,
            map.uniqueID, parms.raidArrivalModeForQuickMilitaryAid ? 0 : 240000, parms.raidArrivalModeForQuickMilitaryAid,
            SharedColony.NextSequence()));
        return true;
    }

    public static bool CapturePortalCancel(MapPortal? portal)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return false;
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return false;
        if (portal is null || portal.Destroyed) return false;
        Publish(CommandCodec.PortalCancelCommand(portal.thingIDNumber, SharedColony.NextSequence()));
        return true;
    }

    public static bool CapturePortal(MapPortal? portal, TransferableOneWay transferable, int count)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return false;
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return false;
        if (portal is null || portal.Destroyed || transferable?.AnyThing is not { } thing || count <= 0) return false;
        Publish(CommandCodec.PortalCommand(portal.thingIDNumber, thing.thingIDNumber, count, SharedColony.NextSequence()));
        return true;
    }

    private static void Publish(GameCommand command) => SharedColony.Send(command);

    /// <summary>Reports a choice-letter click to the host, which runs the option there.
    ///
    /// <para>Public inside the assembly because the reporter lives in `LetterChoiceState`: the patch points are there,
    /// the command encoding is here, and duplicating the publish path would mean a second place to get the sequence
    /// wrong.</para></summary>
    internal static void PublishGrowthChoice(int letterId, List<string> passions, string? traitDef, int traitDegree)
    {
        Publish(CommandCodec.GrowthChoiceCommand(letterId, passions, traitDef, traitDegree, SharedColony.NextSequence()));
        SharedColony.HoldAfterLocalAction();
    }

    internal static void PublishLetterChoice(int letterId, int option)
    {
        Publish(CommandCodec.LetterChoiceCommand(letterId, option, SharedColony.NextSequence()));
        SharedColony.HoldAfterLocalAction();
    }

    // ---------- Apply ----------

    public static void Apply(GameCommand command)
    {
        Applying = true;
        try
        {
            if (TraceCommands)
                Log.Message($"[TurboTick cmd] tick={command.Tick} seq={command.Sequence} kind={command.Kind} "
                    + $"before={SimulationRandom.Capture():x16} local={SharedColony.Session.Barrier.AppliedTick}");
            switch (command.Kind)
            {
                case CommandCodec.Designation:
                    ApplyDesignation(command.Payload);
                    break;
                case CommandCodec.RemoveDesignation:
                    ApplyRemoveDesignation(command.Payload);
                    break;
                case CommandCodec.Blueprint:
                    ApplyBlueprint(command.Payload);
                    break;
                case CommandCodec.CancelThing:
                    ApplyCancelThing(command.Payload);
                    break;
                case CommandCodec.Draft:
                    ApplyDraft(command.Payload);
                    break;
                case CommandCodec.Order:
                    ApplyOrder(command.Payload);
                    break;
                case CommandCodec.Stop:
                    ApplyStop(command.Payload);
                    break;
                case CommandCodec.Forbid:
                    ApplyForbid(command.Payload);
                    break;
                case CommandCodec.ForbidBatch:
                    ApplyForbidBatch(command.Payload);
                    break;
                case CommandCodec.Priority:
                    ApplyPriority(command.Payload);
                    break;
                case CommandCodec.ZoneCell:
                    ApplyZoneCell(command.Payload);
                    break;
            case DesignatorSync.CommandKind:
                DesignatorSync.Apply(System.Text.Encoding.UTF8.GetString(command.Payload));
                break;
            case DesignatorSync.CancelCommandKind:
                DesignatorSync.ApplyCancel(command.Payload);
                break;
            case CommandCodec.ZoneDelete:
                    ApplyZoneDelete(command.Payload);
                    break;
                case CommandCodec.BillAdd:
                    ApplyBillAdd(command.Payload);
                    break;
                case CommandCodec.BillDelete:
                    ApplyBillDelete(command.Payload);
                    break;
                case CommandCodec.AreaAssign:
                    ApplyAreaAssign(command.Payload);
                    break;
                case CommandCodec.TrainingWanted:
                    ApplyTrainingWanted(command.Payload);
                    break;
                case CommandCodec.Policy:
                    ApplyPolicy(command.Payload);
                    break;
                case CommandCodec.Bills:
                    ApplyBills(command.Payload);
                    break;
                case CommandCodec.SurgeryBill:
                    ApplySurgeryBill(command.Payload);
                    break;
                case CommandCodec.Storyteller:
                    ApplyStoryteller(command.Payload);
                    break;
                case CommandCodec.LetterChoice:
                    ApplyLetterChoice(command.Payload);
                    break;
                case CommandCodec.GrowthChoice:
                    ApplyGrowthChoice(command.Payload);
                    break;
                case CommandCodec.AreaOp:
                    ApplyAreaOp(command.Payload);
                    break;
                case CommandCodec.AreaPaint:
                    DesignatorSync.ApplyPaint(Encoding.UTF8.GetString(command.Payload));
                    break;
                case CommandCodec.Transport:
                    ApplyTransport(command.Payload);
                    break;
                case CommandCodec.Portal:
                    ApplyPortal(command.Payload);
                    break;
                case CommandCodec.Diplomacy:
                    ApplyDiplomacy(command.Payload);
                    break;
                case CommandCodec.ThingState:
                    ApplyState(command.Payload);
                    break;
                case CommandCodec.Research:
                    ApplyResearch(command.Payload);
                    break;
                case CommandCodec.PawnLeave:
                    ApplyPawnLeave(command.Payload);
                    break;
                default:
                    Log.Warning("[TurboTick] 未知共享命令：" + command.Kind);
                    break;
            }
        }
        finally { Applying = false; }
    }

    private static void ApplyDesignation(byte[] payload)
    {
        if (!CommandCodec.TryDecodeDesignation(payload, out var defName, out var mapIndex, out var x, out var z, out var thingId)) return;
        var def = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
        var map = FindMapByUniqueId(mapIndex);
        if (def == null || map == null) return;
        // A Thing-keyed designation cannot be queried by cell; ask the manager through the same index it
        // uses internally, otherwise the lookup throws and the whole shared tick is cancelled.
        if (thingId > 0)
        {
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing is null || map.designationManager.DesignationOn(thing, def) != null) return;
            map.designationManager.AddDesignation(new Designation(thing, def));
            return;
        }
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map) || map.designationManager.DesignationAt(cell, def) != null) return;
        map.designationManager.AddDesignation(new Designation(new LocalTargetInfo(cell), def));
    }

    private static void ApplyRemoveDesignation(byte[] payload)
    {
        if (!CommandCodec.TryDecodeRemoveDesignation(payload, out var defName, out var mapIndex, out var x, out var z, out var thingId)) return;
        var map = FindMapByUniqueId(mapIndex);
        if (map == null) return;
        var def = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
        if (thingId > 0)
        {
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing != null) map.designationManager.RemoveAllDesignationsOn(thing);
        }
        else if (def != null)
        {
            var cell = new IntVec3(x, 0, z);
            if (cell.InBounds(map)) map.designationManager.TryRemoveDesignation(cell, def);
        }
    }

    private static void ApplyBlueprint(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBlueprint(payload, out var buildableName, out var stuffName, out var mapIndex,
                out var x, out var z, out var rot, out var requestedId, out var styleName, out var glowerText)) return;
        var buildable = DefDatabase<ThingDef>.GetNamedSilentFail(buildableName);
        var stuff = string.IsNullOrEmpty(stuffName) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
        var map = FindMapByUniqueId(mapIndex);
        if (buildable == null || map == null) return;
        // A stuffable building with no stuff produces a blueprint that every construction work giver throws on:
        // measured, `WorkGiver_ConstructDeliverResourcesToBlueprints.HasJobOnThing` spammed the log every tick, the
        // colony stopped advancing, and the coordinator reported both peers as not pacing. The sender may be a
        // third-party mod or a caller that passed null, so the receiving side refuses the broken blueprint instead of
        // trusting it.
        if (buildable.MadeFromStuff && stuff is null)
        {
            Log.Warning($"[TurboTick] 忽略缺少材质的建造蓝图：{buildable.defName}（会破坏建造工作扫描）");
            return;
        }
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return;
        // The origin already placed this blueprint locally - a building designator's drag needs that to keep going, so
        // the capture no longer cancels it. Applying the echoed command blindly would put a second blueprint in the
        // same cell, which then fights over the cell's edifice slot. Same rule as `Zone.AddCell`: skip when this peer
        // already has it.
        foreach (var existing in cell.GetThingList(map))
        {
            if (existing is not Blueprint blueprint || blueprint.def.entityDefToBuild != buildable) continue;
            // Same object on every peer: the origin's own copy already carries the agreed id.
            if (requestedId <= 0 || blueprint.thingIDNumber == requestedId) return;
            // A different blueprint of the same kind is here - the prediction from an earlier attempt. Remove it so
            // the authoritative one can take the cell.
            blueprint.Destroy(DestroyMode.Cancel);
            break;
        }
        // Allocate the agreed id rather than whatever this peer's counter happens to be at. Without this the two
        // colonies hold blueprints with different ids and the thing digest differs even though the buildings match.
        if (requestedId > 0) AdvanceThingIdCounter(requestedId);
        var style = string.IsNullOrEmpty(styleName) ? null : DefDatabase<ThingStyleDef>.GetNamedSilentFail(styleName);
        var placed = GenConstruct.PlaceBlueprintForBuild(buildable, cell, map, new Rot4(rot), Faction.OfPlayer, stuff);
        // The two style fields the designator writes after placing (`:622504-622509`), applied here because the
        // capture point sits on `PlaceBlueprintForBuild` and therefore runs before the origin's designator writes them.
        //
        // `InheritStyle(null, style)` is what vanilla does when there is no precept (`:570249-570256`): it assigns the
        // rendered style. `StyleSourcePrecept` is left alone on purpose - carrying it would need a precept-by-id lookup
        // that does not exist in this project, and the precept affects the style's label and an ownership notification,
        // not the object the player sees. Sending the resolved style keeps the visible result identical without
        // inventing an identity channel.
        if (placed is not null)
        {
            // Same condition as the designator path: `SetStyleDef` needs `CompStyleable` and otherwise only logs.
            if (style is not null && (placed as ThingWithComps)?.compStyleable is not null) placed.InheritStyle(null, style);
            var glower = DecodeColor(glowerText);
            if (glower is { } colour) placed.glowerColorOverride = colour;
            if (style is not null || glower is not null) blueprintStylesApplied++;
        }
        if (placed is not null && requestedId > 0 && placed.thingIDNumber != requestedId)
        {
            // The counter was already past the agreed id (this peer spawned something else meanwhile). Assigning it
            // keeps the identity stable; the id this one had is simply retired.
            Log.WarningOnce($"[TurboTick] 蓝图 ID 已对齐为 {requestedId}（原 {placed.thingIDNumber}）",
                requestedId.GetHashCode());
            placed.thingIDNumber = requestedId;
        }
    }

    private static void ApplyCancelThing(byte[] payload)
    {
        if (!CommandCodec.TryDecodeCancelThing(payload, out var thingId)) return;
        var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
        if (thing is Blueprint or Frame) thing.Destroy(DestroyMode.Cancel);
    }

    private static void ApplyDraft(byte[] payload)
    {
        if (!CommandCodec.TryDecodeDraft(payload, out var pawnId, out var drafted)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.drafter == null || pawn.drafter.Drafted == drafted) return;
        pawn.drafter.Drafted = drafted;
    }

    /// <summary>Applies an order: rebuild the job from its Scribe XML and hand it to the pawn.
    ///
    /// <para>`TryTakeOrderedJob` is deliberate: it is the entry point a right-click uses, so reservations and the
    /// "not now" rejection behave the same on every peer. The local call is what `OrderCapturePatch` would
    /// normally intercept, so the caller runs this inside a write scope.</para>
    ///
    /// <para>The job's own references need no pre-registration here: <see cref="ScribeBridge.Load"/> owns the whole
    /// two-pass protocol (register for resolve, resolve, PostLoadInit) and registering targets from outside would only
    /// duplicate work it already does. What it cannot invent is an object this peer has never seen - that case is
    /// reported by the reference-residue counter in `ScribeBridge.ResolveReferences`, not silently patched over.</para>
    /// </summary>
    private static void ApplyOrder(byte[] payload)
    {
        if (!CommandCodec.TryDecodeOrder(payload, out var pawnId, out var jobXml)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.jobs is null || pawn.Dead) return;
        // `Job` has a parameterless constructor, so this instance allocates no load id; the serialised job already
        // carries the host's, which is what every peer must agree on.
        var job = new Job();
        if (!ScribeBridge.Load(job, jobXml) || job.def is null)
        {
            if (orderLoadFailures++ < 8)
                Log.Warning($"[TurboTick] 工作指令无法还原（pawn={pawnId}），本帧跳过。");
            return;
        }
        pawn.jobs.TryTakeOrderedJob(job);
    }

    private static int orderLoadFailures;

    /// <summary>Captures a player-ordered job cancellation (round 127).
    ///
    /// <para>Called from the two places a click reaches `EndCurrentJob`: `PawnGotoAction`'s "already standing here"
    /// branch (`:657745-657751`) and any gizmo whose action cancels an ordered job. The signature takes the pawn
    /// explicitly - `Pawn_JobTracker`'s own `pawn` field is private and the caller always has the pawn in hand.</para>
    ///
    /// <para>Returns true when the cancellation was captured, so the caller must skip its local call and let the
    /// command end the job on every peer. Returning false means vanilla should run locally - a job system that ends
    /// a job for its own reasons is not a player decision.</para></summary>
    internal static bool CaptureStop(Pawn pawn, JobCondition condition)
    {
        if (!PlayerActing || pawn?.jobs is null) return false;
        // **Nothing to stop is not an event.** Vanilla's branch is guarded by `pawn.CurJobDef == JobDefOf.Goto`, but a
        // gizmo cancel can fire with no job at all; publishing then would send a no-op that looks, in the counters,
        // exactly like a delivered cancellation.
        if (pawn.jobs.curJob is null) return false;
        Publish(CommandCodec.StopCommand(pawn.thingIDNumber, (int)condition, SharedColony.NextSequence()));
        stopsCaptured++;
        return true;
    }

    private static void ApplyStop(byte[] payload)
    {
        if (!CommandCodec.TryDecodeStop(payload, out var pawnId, out var rawCondition)) { stopsMissed++; return; }
        if (!AuthoritativeReplica.FindPawnAny(pawnId, out var pawn) || pawn?.jobs is null || pawn.Dead)
        {
            // Counted separately from a decode failure: an id this peer cannot resolve and a malformed payload are
            // different faults, and both used to look like "the pawn ignored the order".
            stopsMissed++;
            return;
        }
        var condition = (JobCondition)rawCondition;
        // `EndCurrentJob` on a tracker with no job is a no-op in vanilla as well, so this needs no guard; what it does
        // need is the same condition the click carried, because `Succeeded` and `InterruptForced` end differently.
        pawn.jobs.EndCurrentJob(condition);
        stopsApplied++;
    }

    internal static long stopsCaptured;
    internal static long stopsApplied;
    internal static long stopsMissed;

    /// <summary>The forbidden X on the ground is an overlay handle, and vanilla only rebuilds it inside the private
    /// <c>CompForbiddable.UpdateOverlayHandle</c>. The <c>Forbidden</c> setter returns before reaching it whenever
    /// the value is already what the caller asked for, and <c>PostExposeData</c> never touches the overlay at all,
    /// so a peer whose state is right can still draw the X forever. Measured: the receiving client kept every item
    /// crossed out after an unforbid had been applied and logged as applied.</summary>
    private static AccessTools.FieldRef<CompForbiddable, bool>? forbiddenField;

    /// <summary>Batches applied on this peer. Only used to thin out the per-gesture log line.</summary>
    private static int batchApplied;

    /// <summary>Times a lookup by id returned a second object under a number already resolved. Zero is the
    /// expected reading: every conclusion below assumes one number means one thing on both peers.</summary>
    private static int duplicates;
    private static readonly System.Text.StringBuilder dupSamples = new();

    /// <summary>Flags this peer set, to be read again a few hundred milliseconds later.
    ///
    /// Reading a flag back immediately after the setter only proves the setter ran. Measured: the authority
    /// logged `应用 9 件，未生效 0 件` for fifteen batches in a row, and every frame it then sent for those same
    /// ids still carried Forbidden=true, so the receiving peer had nothing to correct - the world had already
    /// put the flag back. Without this second look, "the setter ran" and "the value stuck" are the same reading.</summary>
    private static readonly List<(int Id, bool Value, long At)> forbidFollowUps = new();
    private static int revertedForbids;
    private static readonly System.Text.StringBuilder revertSamples = new();

    /// <summary>Re-reads recently written forbid flags. Called once per frame by both peers; costs one lookup
    /// per id still inside the 400 ms window.</summary>
    internal static void VerifyForbidFollowUps()
    {
        if (forbidFollowUps.Count == 0) return;
        var now = Environment.TickCount64;
        var revertedThisRound = 0;
        for (var i = forbidFollowUps.Count - 1; i >= 0; i--)
        {
            var (id, value, at) = forbidFollowUps[i];
            // Long enough for a tick or two to pass: whatever reverts the flag does it while the world runs.
            if (now - at < 400) continue;
            forbidFollowUps.RemoveAt(i);
            var thing = FindThingById(id);
            if (thing is null) continue;
            if (thing is not ThingWithComps withComps) continue;
            if (withComps.GetComp<CompForbiddable>() is not { } comp) continue;
            if (comp.Forbidden == value) continue;
            revertedForbids++;
            revertedThisRound++;
            if (revertSamples.Length < 200)
                revertSamples.Append(id).Append(':').Append(thing.def?.defName)
                    .Append("设为").Append(value ? "T" : "F").Append("现为").Append(comp.Forbidden ? "T" : "F").Append(' ');
        }
        if (revertedThisRound > 0)
            Log.Warning($"[TurboTick cmd] 禁止位没能留住：本轮 {revertedThisRound} 件被改回（累计 {revertedForbids} 件）；"
                + $"样本 {revertSamples}");
    }

    /// <summary>Forces <c>CompForbiddable.Forbidden</c> to <paramref name="forbidden"/> so that the vanilla setter runs
    /// all of its side effects, not just the overlay refresh.
    ///
    /// The setter's first line is <c>if (value == forbiddenInt) return;</c>, so writing the same value twice is a
    /// no-op: the haulable/mergeable lister notifications and the door reachability flush never happen, and a peer
    /// whose flag is already right keeps drawing the X. The fix is the one ColonyTogether uses in its
    /// <c>GenericRefresh.After</c>: write the backing field to the opposite value first, then assign the property, so
    /// the guard passes and vanilla does the whole job itself - no reflection into a private method, and no second
    /// code path that can drift from what the game does.</summary>
    internal static void SetForbiddenThroughSetter(Thing thing, bool forbidden)
    {
        if (thing is not ThingWithComps withComps) return;
        if (withComps.GetComp<CompForbiddable>() is not { } comp) return;
        try
        {
            // Marked as a state write first: the assignment below goes through the property, which is patched, and
            // an unmarked write is captured as player input and cancelled. That is the loop this whole method was
            // supposed to end, not repeat.
            using var scope = StateWrite();
            // Measured around the write, because after it the marker always agrees: "the setter ran" and "the marker
            // was wrong and is now right" are different claims, and only the second one explains a screen that kept
            // its X through forty batches of "applied 9, 未生效 0".
            var markerWasWrong = !ForbidMarkerMatches(comp);
            forbiddenField ??= AccessTools.FieldRefAccess<CompForbiddable, bool>("forbiddenInt");
            if (forbiddenField(comp) == forbidden) forbiddenField(comp) = !forbidden;
            comp.Forbidden = forbidden;
            if (markerWasWrong) forbidSetterRepairs++;
            if (!ForbidMarkerMatches(comp) && comp.Forbidden == forbidden) forbidSetterMisses++;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 写禁止位失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>True when the drawn X agrees with the flag. The X is an overlay handle, so a null handle with the flag
    /// set (or the reverse) is exactly the state that draws a wrong marker.</summary>
    private static bool ForbidMarkerMatches(CompForbiddable comp)
    {
        try
        {
            overlayHandle ??= AccessTools.Field(typeof(CompForbiddable), "overlayHandle");
            if (overlayHandle is null) return true;
            return (overlayHandle.GetValue(comp) is not null) == comp.Forbidden;
        }
        catch (Exception) { return true; }
    }

    private static FieldInfo? overlayHandle;

    /// <summary>Writes that found the marker already wrong, and writes after which it is still wrong with the flag
    /// correct. The first number is the proof the bug is real and this path fixes it; the second staying above zero
    /// would mean the setter is not enough and something else owns the handle.</summary>
    internal static int ForbidSetterRepairs => forbidSetterRepairs;
    internal static int ForbidSetterMisses => forbidSetterMisses;
    private static int forbidSetterRepairs;
    private static int forbidSetterMisses;

    private static void ApplyForbid(byte[] payload)
    {
        if (!CommandCodec.TryDecodeForbid(payload, out var thingId, out var forbidden)) return;
        var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
        if (thing is null)
        {
            // The command arrived but the receiver has no such thing - a carried or contained item is not in
            // the lister. Saying so is what separates a lost command from a lost target.
            Log.Warning($"[TurboTick cmd] 禁止状态无法应用：本端找不到 thing={thingId}");
            return;
        }
        SetForbiddenThroughSetter(thing, forbidden);
        if (TraceCommands) TraceCapture($"forbid applied thing={thingId} value={forbidden}");
    }

    /// <summary>Applies one bulk forbid/unforbid. A target this peer does not have is skipped silently: a carried
    /// or contained item is not in the lister, and this is one gesture's worth of them, so a line per miss would
    /// turn one gesture into a wall of log noise. The count of misses is reported once instead.</summary>
    private static void ApplyForbidBatch(byte[] payload)
    {
        if (!CommandCodec.TryDecodeForbidBatch(payload, out var forbidden, out var thingIds)) return;
        var missing = 0;
        var applied = 0;
        var stuck = 0;
        var notForbiddable = 0;
        var foundIds = new System.Text.StringBuilder();
        var shapes = new System.Text.StringBuilder();
        var missingIds = new System.Text.StringBuilder();
        // Ids are comparable only if both peers hold the same objects. Printing the misses next to the peer's own
        // list is what turns "找不到 12/12" into either "these ids were never in this world" or "this world stores
        // them somewhere the lookup does not reach" - two very different bugs, indistinguishable from a count.
        var itemsHere = 0;
        var forbiddenHere = 0;
        var itemSample = new System.Text.StringBuilder();
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing.def?.category == ThingCategory.Item && thing.Spawned && !thing.Destroyed)
                {
                    itemsHere++;
                    if (thing is ThingWithComps counted && counted.GetComp<CompForbiddable>() is { Forbidden: true })
                        forbiddenHere++;
                    if (itemSample.Length < 160) itemSample.Append(thing.thingIDNumber).Append(':').Append(thing.def.defName).Append(' ');
                }
        foreach (var thingId in thingIds)
        {
            var thing = FindThingById(thingId);
            if (thing is null)
            {
                missing++;
                if (missingIds.Length < 160) missingIds.Append(thingId).Append(' ');
                continue;
            }
            // Counted separately because "applied" is a lie for anything without the comp: SetForbidden with
            // warnOnFail:false returns silently, so a MinifiedThing or a building scores as a success while nothing
            // changed. Measured: 15 batches each reported "应用 8 件，未生效 0 件" and the peer's flag never moved.
            if (thing is not ThingWithComps foundComps || foundComps.GetComp<CompForbiddable>() is null)
            {
                notForbiddable++;
                if (shapes.Length < 200) shapes.Append(thingId).Append('=').Append(thing.GetType().Name).Append(' ');
                continue;
            }
            // Captured before the setter: afterwards it is always the requested value, which is exactly the
            // information-free reading this log line exists to avoid.
            var before = thing is ThingWithComps pre && pre.GetComp<CompForbiddable>() is { } preComp && preComp.Forbidden;
            SetForbiddenThroughSetter(thing, forbidden);
            forbidFollowUps.Add((thingId, forbidden, Environment.TickCount64));
            applied++;
            // id AND def, because the peer's own probe prints the same pair: if the two lines disagree about what
            // id 38014 is, the worlds are not talking about the same objects and every later conclusion is void.
            if (foundIds.Length < 200)
                foundIds.Append(thingId).Append(':').Append(thing.def?.defName).Append(before ? "(T)" : "(F)").Append(' ');
            // Reading the flag back is the difference between "the setter ran" and "the value is now what the
            // command asked for". A comp that refuses the value (a building, a minified thing) leaves the setter
            // looking successful from the caller's side.
            if (thing is ThingWithComps withComps && withComps.GetComp<CompForbiddable>() is { } comp
                && comp.Forbidden != forbidden) stuck++;
            // Count every object carrying this number instead of re-running the lookup: a second FindThingById
            // returns the same first match by construction, so that comparison is vacuously true and proves nothing.
            // If the map really holds two things under one number, the sweep can ship one of them while the command
            // writes the other - which is exactly the shape of "applied 9, nothing ever changes".
            var sameId = 0;
            Thing? other = null;
            foreach (var map in Find.Maps)
                foreach (var candidate in map.listerThings.AllThings)
                    if (candidate.thingIDNumber == thingId)
                    {
                        sameId++;
                        if (!ReferenceEquals(candidate, thing)) other ??= candidate;
                    }
            if (sameId > 1)
            {
                duplicates++;
                if (dupSamples.Length < 200)
                    dupSamples.Append(thingId).Append('×').Append(sameId).Append('/')
                        .Append(thing.GetType().Name).Append(thing.Spawned ? "(S)" : "(-)").Append(" vs ")
                        .Append(other?.GetType().Name).Append(other?.Spawned == true ? "(S)" : "(-)").Append(' ');
            }
        }
        // Printed on every batch, not behind TraceCommands. Measured: 55 forbid-batch commands were relayed and the
        // authority logged taking each one, yet the peer that requested them never saw a single flag change - and
        // from outside that is indistinguishable from "applied to nothing" and "applied to the wrong objects".
        // One line per gesture is a few lines a minute; that is the cheapest way to close the question.
        if (missing > 0 || stuck > 0 || notForbiddable > 0 || duplicates > 0 || batchApplied < 20 || batchApplied % 50 == 0)
        {
            if (missing > 0)
                Log.Warning($"[TurboTick cmd] 批量禁止：本端找不到 {missing}/{thingIds.Count} 个物体（value={forbidden}，本端物品 {itemsHere} 件）"
                    + $"；应用 {applied} 件，未生效 {stuck} 件，不可禁止 {notForbiddable} 件；找到 {foundIds}；缺少 {missingIds}；本端物品 {itemsHere} 件其中禁止 {forbiddenHere} 件，样本 {itemSample}"
                    + $"；标记修复 {ForbidSetterRepairs} 次/修复后仍错 {ForbidSetterMisses} 次");
            else if (stuck > 0 || notForbiddable > 0 || duplicates > 0)
                Log.Warning($"[TurboTick cmd] 批量禁止：应用 {applied}/{thingIds.Count} 件，但 {stuck} 件的值没变成 {forbidden}，"
                    + $"{notForbiddable} 件没有 CompForbiddable，{duplicates} 次同一 id 查到不同对象；形状 {shapes} 重复 {dupSamples}");
            else
                Log.Message($"[TurboTick cmd] 批量禁止：应用 {applied}/{thingIds.Count} 个物体（value={forbidden}）"
                    + $"；标记修复 {ForbidSetterRepairs} 次/修复后仍错 {ForbidSetterMisses} 次");
        }
        batchApplied++;
        if (TraceCommands) TraceCapture($"forbid-batch applied={applied} missing={missing} value={forbidden}");
    }

    private static void ApplyPriority(byte[] payload)
    {
        if (!CommandCodec.TryDecodePriority(payload, out var pawnId, out var workDefName, out var priority)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workDefName);
        if (pawn?.workSettings != null && def != null)
        {
            pawn.workSettings.SetPriority(def, priority);
        }
    }

    private static void ApplyZoneCell(byte[] payload)
    {
        if (!CommandCodec.TryDecodeZoneCell(payload, out var zoneType, out var zoneId, out var mapIndex, out var x, out var z, out var add, out var label)) return;
        var map = FindMapByUniqueId(mapIndex);
        if (map == null) return;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return;
        var zone = map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        if (add)
        {
            // The origin already added the cell locally - the capture no longer cancels the call, because
            // vanilla's designator needs the cell to be there. Every peer applies every command, so the
            // origin would otherwise add it twice and the stockpile's slot group would be registered twice,
            // which is exactly the "overwriting slot group ... of itself" flood in the log.
            //
            // ContainsCell, not Cells.Contains: Zone.Cells shuffles the list on first access and consumes
            // the engine's Rand (decompiled Verse/Zone.cs:98-109 -> GenList.Shuffle), so reading it from a
            // command path reorders cells and burns randomness on only that peer. SelectionDrawer also
            // triggers it while drawing, which is why this must never be the deciding read.
            if (zone is not null && zone.ContainsCell(cell)) return;
            if (zone == null)
            {
                if (zoneType == "growing") zone = new Zone_Growing(map.zoneManager);
                else zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                zone.ID = zoneId;
                // The receiving peer did not allocate this id, so UniqueIDsManager still sits below it.
                // Vanilla hands out ids from that counter (Verse/Zone.cs:192), which is saved with the game:
                // leaving it behind makes the next zone created on this peer take an id the sender already
                // used, and every later zone-cell/zone-delete command then lands on a different object.
                AdvanceZoneIdCounter(zoneId);
                map.zoneManager.RegisterZone(zone);
                    // The label is part of the zone. Measured: both peers ended a 25-cell drag with identical cells
                    // and still reported a divergence, because the origin called it "scripted" while this side kept
                    // the vanilla default "Stockpile zone 1". Everything else about the colonies was byte-identical.
                    if (!string.IsNullOrEmpty(label) && zone.label != label) zone.label = label;
            }
            if (!zone.ContainsCell(cell)) zone.AddCell(cell);
        }
        else
        {
            if (zone is null || !zone.ContainsCell(cell)) return;
            zone.RemoveCell(cell);
        }
    }

    /// <summary>Resolves the stable map id every map-keyed command now carries.
    ///
    /// <para><b>`Map.uniqueID`, not `Map.Index`.</b> `Index` is `Find.Maps.IndexOf(this)` (`:76452`) - a list position, so
    /// removing one map shifts every later map's index down and a command still in flight resolves to a different map.
    /// `uniqueID` (`:76213`) is assigned once, is scribed by `Map.ExposeData` (`:76818`), and is therefore identical on
    /// every peer loaded from the same save. `AreaSync`, `ZoneSync`, `FogSync` and `AreaAllowedSync` already travel by it;
    /// the command channels were the remaining holdouts.</para>
    ///
    /// <para>Both are small integers, so a single-map save cannot tell them apart. That is precisely why the choice has to
    /// come from the field's definition rather than from a passing test.</para></summary>
    private static Map? FindMapByUniqueId(int uniqueId)
    {
        if (uniqueId < 0) return null;
        foreach (var map in Find.Maps)
            if (map.uniqueID == uniqueId) return map;
        return null;
    }

    private static FieldInfo? nextZoneIdField;

    /// <summary>Moves the shared zone-id counter past <paramref name="zoneId"/> so both peers keep allocating
    /// the same ids. Setting the field is cheaper and exact compared with calling GetNextZoneID in a loop,
    /// which would consume an extra id.</summary>
        /// <summary>Moves the thing-id counter to the agreed value so the next spawn takes it.
    ///
    /// The blueprint is placed on the origin the moment the player drags, and on every other peer at the stamped tick.
    /// Each side would otherwise allocate its own id from its own counter, so the same building ends up with two
    /// different ids and the thing digest differs - measured as equal thing counts with a different `tdig`.</summary>
    private static void AdvanceThingIdCounter(int thingId)
    {
        var ids = Find.UniqueIDsManager;
        if (ids is null || thingId <= 0) return;
        try
        {
            nextThingIdField ??= AccessTools.Field(ids.GetType(), "nextThingID");
            if (nextThingIdField is null) return;
            var current = (int)(nextThingIdField.GetValue(ids) ?? 0);
            if (current <= thingId) nextThingIdField.SetValue(ids, thingId + 1);
        }
        catch (Exception error) { Log.Warning("[TurboTick] 无法推进物体 ID 计数器：" + error.Message); }
    }

    private static System.Reflection.FieldInfo? nextThingIdField;

    /// <summary>The id the host will hand out next. Published in every frame so a display replica has something to
    /// compare its own copy against - the only way that peer can notice an object was created locally, because a
    /// replica never reports a state hash and therefore cannot notice id drift through the normal desync path.</summary>
    internal static int NextThingId()
    {
        var ids = Find.UniqueIDsManager;
        if (ids is null) return 0;
        try
        {
            nextThingIdField ??= AccessTools.Field(ids.GetType(), "nextThingID");
            return nextThingIdField is null ? 0 : (int)(nextThingIdField.GetValue(ids) ?? 0);
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Overwrites the counter with the host's value. Only a display replica calls this: it allocates no
    /// identity of its own, so its counter is a copy of the host's and never a source.</summary>
    internal static bool SetNextThingId(int thingId)
    {
        var ids = Find.UniqueIDsManager;
        if (ids is null || thingId <= 0) return false;
        try
        {
            nextThingIdField ??= AccessTools.Field(ids.GetType(), "nextThingID");
            if (nextThingIdField is null) return false;
            nextThingIdField.SetValue(ids, thingId);
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 无法写回物体 ID 计数器：" + error.Message);
            return false;
        }
    }

private static void AdvanceZoneIdCounter(int zoneId)
    {
        var ids = Find.UniqueIDsManager;
        if (ids is null) return;
        try
        {
            nextZoneIdField ??= AccessTools.Field(ids.GetType(), "nextZoneID");
            if (nextZoneIdField is null) return;
            var current = (int)(nextZoneIdField.GetValue(ids) ?? 0);
            if (current <= zoneId) nextZoneIdField.SetValue(ids, zoneId + 1);
        }
        catch (Exception error) { Log.Warning("[TurboTick] 无法推进区域 ID 计数器：" + error.Message); }
    }

    private static void ApplyZoneDelete(byte[] payload)
    {
        if (!CommandCodec.TryDecodeZoneDelete(payload, out var mapIndex, out var zoneId)) return;
        var map = FindMapByUniqueId(mapIndex);
        var zone = map?.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        zone?.Delete();
    }

    private static void ApplyBillAdd(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBillAdd(payload, out var buildingId, out var recipeDefName, out var repeatCount)) return;
        var table = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == buildingId) as IBillGiver;
        var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
        if (table?.BillStack != null && recipe != null)
        {
            var bill = recipe.MakeNewBill(null);
            if (bill is Bill_Production prod && repeatCount > 0) prod.repeatCount = repeatCount;
            table.BillStack.AddBill(bill);
        }
    }

    private static void ApplyBillDelete(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBillDelete(payload, out var buildingId, out var billIndex)) return;
        var table = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == buildingId) as IBillGiver;
        if (table?.BillStack != null && billIndex >= 0 && billIndex < table.BillStack.Count)
        {
            table.BillStack.Delete(table.BillStack[billIndex]);
        }
    }

    /// <summary>Applies an area-button action (invert / clear / delete) from `Dialog_ManageAreas`.
    ///
    /// <para>These three are the only area mutations that do not pass through a `Designator`, so they were the only
    /// ones that stayed local. The window's buttons call <c>area.Invert()</c> / <c>area.Clear()</c> /
    /// <c>area.Delete()</c> directly (`RimWorld/Dialog_ManageAreas.cs:89,111`).</para>
    ///
    /// <para>Every peer starts from the same grid because the drawing path is itself synchronised, so replaying the
    /// operation reproduces the same result without sending the cells.</para></summary>
    private static void ApplyAreaOp(byte[] payload)
    {
        if (!CommandCodec.TryDecodeAreaOp(payload, out var op, out var areaId)) return;
        foreach (var map in Find.Maps)
        {
            var area = map.areaManager?.AllAreas?.FirstOrDefault(a => a.ID == areaId);
            if (area is null) continue;
            switch (op)
            {
                case "invert": area.Invert(); break;
                case "clear": area.Clear(); break;
                // `Delete` refuses a non-mutable area and logs; that check belongs to vanilla, not to us.
                case "delete": if (area.Mutable) area.Delete(); break;
            }
            return;
        }
        if (areaOpMisses++ < 4)
            Log.Warning($"[TurboTick] 区域操作找不到目标：{op} id={areaId}");
    }

    private static int areaOpMisses;

    private static void ApplyAreaAssign(byte[] payload)
    {
        if (!CommandCodec.TryDecodeAreaAssign(payload, out var pawnId, out var areaId)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.playerSettings == null) return;
        var area = areaId < 0 ? null : pawn.Map?.areaManager?.AllAreas?.FirstOrDefault(a => a.ID == areaId);
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
    }

    /// <summary>Applies a policy change: each present tracker is loaded from its own Scribe XML.
    ///
    /// <para>Only the trackers the payload carries are touched - see <see cref="FlushPolicies"/>, which sends just the
    /// one that changed. Applying all four unconditionally would let an outfit change revert a timetable edit.</para>
    ///
    /// <para>The tracker objects themselves already exist (they are created with the pawn), and their
    /// <c>ExposeData</c> only rewrites the policy pointer and the timetable list, so this is a plain state write with
    /// no grid or lister involvement - which is why it is safe on a display replica.</para></summary>
    private static void ApplyPolicy(byte[] payload)
    {
        if (!CommandCodec.TryDecodePolicy(payload, out var pawnId, out var outfit, out var drug, out var food, out var timetable,
            out var stock)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn is null || pawn.Dead) return;
        var applied = 0;
        if (outfit is not null && pawn.outfits is not null && ScribeBridge.Load(pawn.outfits, outfit)) applied++;
        if (drug is not null && pawn.drugs is not null && ScribeBridge.Load(pawn.drugs, drug)) applied++;
        if (food is not null && pawn.foodRestriction is not null && ScribeBridge.Load(pawn.foodRestriction, food)) applied++;
        if (timetable is not null && pawn.timetable is not null && ScribeBridge.Load(pawn.timetable, timetable)) applied++;
        if (stock is not null && pawn.inventoryStock is not null && ScribeBridge.Load(pawn.inventoryStock, stock)) applied++;
        if (applied == 0 && policyLoadFailures++ < 8)
            Log.Warning($"[TurboTick] 策略状态无法应用（pawn={pawnId}）：本端没有对应的 tracker。");
    }

    private static int policyLoadFailures;

    /// <summary>Samples a bill giver's whole <c>BillStack</c>, so a dialog session can be replayed as one command.
    ///
    /// <para>Nothing in this mod patched the bill settings dialog before: `Bill.SetPawnRestriction`,
    /// `Bill_Production.SetIncludeGroup` and `SetStoreMode` are ledger entries, but the dialog also writes ten plain
    /// fields, so the three methods were never the whole surface. `Dialog_BillConfig` is opened from
    /// `Bill.DoConfigInterface` (`:588571`) and has no `PostClose` override, so sampling wraps `PreClose`.</para></summary>
    internal static string? CaptureBills(IBillGiver giver) =>
        giver?.BillStack is null ? null : ScribeBridge.SaveOrNull(giver.BillStack);

    /// <summary>Sends the stack when it differs from the sample taken when the dialog opened.</summary>
    internal static void FlushBills(string? before, IBillGiver giver)
    {
        if (before is null || giver?.BillStack is null || !PlayerActing) return;
        var now = ScribeBridge.SaveOrNull(giver.BillStack);
        if (now is null || string.Equals(before, now, StringComparison.Ordinal)) return;
        if (now.Length > MaxThingStatePayload)
        {
            Log.Warning($"[TurboTick] 账单状态过大（{now.Length} 字符），未同步：{(giver as Thing)?.thingIDNumber}");
            return;
        }
        if (giver is not Thing owner) return;
        Publish(CommandCodec.BillsCommand(owner.thingIDNumber, now, SharedColony.NextSequence()));
        SharedColony.HoldAfterLocalAction();
    }

    /// <summary>Loads a whole <c>BillStack</c> fragment.
    ///
    /// <para>Same shape as <see cref="ApplyPolicy"/>: the giver is found by thing id and the fragment goes through
    /// `ScribeBridge.Load`, which runs the game's own `ExposeData` in load mode - including the
    /// `LoadSaveMode.ResolvingCrossRefs` block that re-points every bill's `billStack` field (`:272717-272727`), so
    /// the replaced bills are attached to this stack rather than orphaned.</para></summary>
    private static void ApplyBills(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBills(payload, out var giverId, out var xml) || xml is null) return;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.thingIDNumber != giverId || thing is not IBillGiver giver) continue;
                if (giver.BillStack is null) return;
                if (!ScribeBridge.Load(giver.BillStack, xml) && billLoadFailures++ < 8)
                    Log.Warning($"[TurboTick] 账单状态无法应用（giver={giverId}）");
                else BillApply++;
                return;
            }
        if (billMisses++ < 8) Log.Warning($"[TurboTick] 账单目标找不到：{giverId}");
    }

    /// <summary>Samples and flushes the bill settings dialog. `Window.PreClose` is virtual (`:196929`) and neither
    /// `Dialog_BillConfig` nor `Dialog_MechBillConfig` (`:590322`, its subclass) overrides it, so the base method is the
    /// single place both dialogs pass through.
    ///
    /// <para>The giver is read off `bill.billStack.billGiver` rather than off a field, because the dialog keeps only
    /// the bill and its on-screen position (`:589700-589705`). `Bill.billStack` is set by
    /// `BillStack.ExposeData`'s resolve pass (`:272725`) and by the constructor, so it is always present for a bill
    /// that is being configured.</para></summary>
    internal static bool TryBillDialog(object window, out IBillGiver? giver)
    {
        giver = null;
        if (window is not Dialog_BillConfig dialog) return false;
        var bill = AccessTools.Field(typeof(Dialog_BillConfig), "bill")?.GetValue(dialog) as Bill;
        if (bill?.billStack?.billGiver is not IBillGiver target) return false;
        giver = target;
        return true;
    }

    private static int billLoadFailures;
    private static int billMisses;
    internal static long BillApply { get; private set; }

    /// <summary>Samples the game's storyteller, so a session in `Page_SelectStorytellerInGame` can be replayed.</summary>
    internal static string? CaptureStoryteller() =>
        Find.Storyteller is null ? null : ScribeBridge.SaveOrNull(Find.Storyteller);

    internal static void FlushStoryteller(string? before)
    {
        if (before is null || Find.Storyteller is null || !PlayerActing) return;
        var now = ScribeBridge.SaveOrNull(Find.Storyteller);
        if (now is null || string.Equals(before, now, StringComparison.Ordinal)) return;
        if (now.Length > MaxThingStatePayload)
        {
            Log.Warning($"[TurboTick] 叙事者状态过大（{now.Length} 字符），未同步");
            return;
        }
        Publish(CommandCodec.StorytellerCommand(now, SharedColony.NextSequence()));
        SharedColony.HoldAfterLocalAction();
    }

    /// <summary>Loads the whole storyteller fragment, then redoes the two things the page itself does on close.
    ///
    /// <para>`Page_SelectStorytellerInGame.PreClose` (`:654988-654996`) re-caches `ThingDef.costListForDifficulty` and
    /// calls `RecipeDefGenerator.ResetRecipeIngredientsForDifficulty()`. Those caches are derived from
    /// `Find.Storyteller.difficulty` (see `CostListForDifficulty.RecacheApplies` at `:32860`, which reads
    /// `Find.Storyteller.difficulty` directly), so loading the fragment without re-caching would leave every building
    /// cost and recipe ingredient list on the replica computed from the previous difficulty — the numbers would be
    /// right in the storyteller page and wrong everywhere the player actually looks.</para>
    ///
    /// <para>`Notify_DefChanged` (`:390235`) runs `InitializeStorytellerComps()`, which rebuilds the comp list from the
    /// new def; the load already restores the comps, so calling it again would append a second set.</para></summary>
    private static void ApplyStoryteller(byte[] payload)
    {
        if (!CommandCodec.TryDecodeStoryteller(payload, out var xml) || xml is null) return;
        if (Find.Storyteller is null) return;
        if (!ScribeBridge.Load(Find.Storyteller, xml))
        {
            if (storytellerLoadFailures++ < 8) Log.Warning("[TurboTick] 叙事者状态无法应用");
            return;
        }
        try
        {
            foreach (var thingDef in DefDatabase<ThingDef>.AllDefs)
                thingDef.costListForDifficulty?.RecacheApplies();
            RecipeDefGenerator.ResetRecipeIngredientsForDifficulty();
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 叙事者改动后重算缓存失败：" + error.GetType().Name + ": " + error.Message);
        }
        StorytellerApply++;
    }

    private static int storytellerLoadFailures;
    internal static long StorytellerApply { get; private set; }

    /// <summary>Upper bound on how many options a letter may present. `ChoiceLetter_AcceptVisitors` yields four and the
    /// growth moment two or three; a larger number means the replica and the host disagree about the letter, and the
    /// refusal is more useful than running an option the host never offered.</summary>
    private const int MaxLetterOptions = 32;

    internal static long GrowthChoicesApplied { get; private set; }
    internal static long LetterChoicesApplied { get; private set; }
    internal static long LetterChoicesRefused { get; private set; }
    internal static string? LastLetterRefusal { get; private set; }

    /// <summary>Applies a growth-moment answer: the skills to gain passion in and the trait.
    ///
    /// <para>Goes through the letter's own `MakeChoices` (`_lab/asm/Assembly-CSharp.decompiled.cs:662198`) rather than
    /// writing the fields here, because that method is the only place that knows the rest of the effect: it increments
    /// each `SkillRecord.passion`, keeps a `Gene.passionPreAdd` bookkeeping value in step for genes that drop passions,
    /// applies the trait's own skill gains, rolls a sexuality trait at age 13, and resets `growthPoints` /
    /// `canGainGrowthPoints`. Reimplementing that list would drift the moment a DLC adds a case.</para>
    ///
    /// <para>The letter is removed afterwards, as its own dialog does (`Dialog_GrowthMomentChoices`, `:662457-662459`:
    /// `MakeChoices`, `Close`, `RemoveLetter`).</para></summary>
    private static void ApplyGrowthChoice(byte[] payload)
    {
        if (!CommandCodec.TryDecodeGrowthChoice(payload, out var letterId, out var passionNames, out var traitDefName,
            out var traitDegree))
        {
            LetterChoicesRefused++;
            LastLetterRefusal = "成长时刻载荷无法解码";
            return;
        }
        var live = Find.LetterStack?.LettersListForReading;
        if (live is null) { LetterChoicesRefused++; LastLetterRefusal = "没有 LetterStack"; return; }
        ChoiceLetter_GrowthMoment? target = null;
        for (var i = 0; i < live.Count; i++)
            if (live[i] is ChoiceLetter_GrowthMoment each && each.ID == letterId) { target = each; break; }
        if (target is null)
        {
            LetterChoicesRefused++;
            LastLetterRefusal = $"成长时刻信件 #{letterId} 不在栈上";
            return;
        }
        var skills = new List<SkillDef>();
        foreach (var name in passionNames)
        {
            var skill = DefDatabase<SkillDef>.GetNamedSilentFail(name);
            if (skill is null)
            {
                LetterChoicesRefused++;
                LastLetterRefusal = "未知技能 " + name;
                return;
            }
            skills.Add(skill);
        }
        Trait? trait = null;
        if (traitDefName is not null)
        {
            var def = DefDatabase<TraitDef>.GetNamedSilentFail(traitDefName);
            if (def is null)
            {
                LetterChoicesRefused++;
                LastLetterRefusal = "未知特性 " + traitDefName;
                return;
            }
            trait = new Trait(def, traitDegree);
        }
        // `MakeChoices` expects `null` for "no trait"; the letter's own `NoTrait` sentinel is a `Trait` with no def and
        // is what the dialog passes when the player picked that option.
        target.MakeChoices(skills, trait ?? ChoiceLetter_GrowthMoment.NoTrait);
        Find.LetterStack?.RemoveLetter(target);
        GrowthChoicesApplied++;
    }

    /// <summary>Runs the option the replica reported.
    ///
    /// <para>Only `action` is invoked, not `DiaOption.Activate`: `Activate` also closes the owning dialog and navigates
    /// to a link (`:195621-195636`), and there is no dialog on the host. What matters is the side effect, and that is
    /// the delegate.</para>
    ///
    /// <para>The letter is found by id in `LetterStack.LettersListForReading`, which is the same list the player sees
    /// on the host. A letter that has already been answered or expired is simply absent, so a stale click is a
    /// refusal rather than an exception.</para></summary>
    private static void ApplyLetterChoice(byte[] payload)
    {
        if (!CommandCodec.TryDecodeLetterChoice(payload, out var letterId, out var option))
        {
            LetterChoicesRefused++;
            LastLetterRefusal = "载荷无法解码";
            return;
        }
        var live = Find.LetterStack?.LettersListForReading;
        if (live is null) { LetterChoicesRefused++; LastLetterRefusal = "没有 LetterStack"; return; }
        ChoiceLetter? target = null;
        for (var i = 0; i < live.Count; i++)
            if (live[i] is ChoiceLetter choice && choice.ID == letterId) { target = choice; break; }
        if (target is null)
        {
            LetterChoicesRefused++;
            LastLetterRefusal = $"信件 #{letterId} 不在栈上（已作答或已过期）";
            return;
        }
        var options = new List<DiaOption>();
        foreach (var each in target.Choices)
        {
            options.Add(each);
            if (options.Count > MaxLetterOptions) break;
        }
        if (option >= options.Count || option < 0)
        {
            LetterChoicesRefused++;
            LastLetterRefusal = $"信件 #{letterId} 只有 {options.Count} 个选项，收到 {option}";
            return;
        }
        var chosen = options[option];
        if (chosen.disabled)
        {
            LetterChoicesRefused++;
            LastLetterRefusal = $"信件 #{letterId} 的选项 {option} 在主机端不可用：{chosen.disabledReason}";
            return;
        }
        chosen.action?.Invoke();
        LetterChoicesApplied++;
    }

    /// <summary>Replays a portal load on the simulating side.
    ///
    /// <para>Deliberately the same three steps as `ApplyTransport`, on the other type: look the portal up by id across
    /// every map, build a `TransferableOneWay` around the one thing, and call vanilla's own method. Reimplementing what
    /// `leftToLoad` means here is how the two sides would drift apart on the next game version.</para></summary>
    private static void ApplyPortal(byte[] payload)
    {
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return;
        if (!CommandCodec.TryDecodePortal(payload, out var portalId, out var thingId, out var count)) return;
        // `thingId <= 0` is the cancel form - see `CommandCodec.PortalCancelCommand`. A real load always carries a
        // positive thing and a positive count, so the two cannot be confused by a malformed payload either.
        var cancelling = thingId <= 0 && count <= 0;
        MapPortal? portal = null;
        foreach (var map in Find.Maps)
        {
            if (map?.listerThings is null) continue;
            foreach (var thing in map.listerThings.AllThings)
                if (thing is MapPortal candidate && candidate.thingIDNumber == portalId) { portal = candidate; break; }
            if (portal is not null) break;
        }
        if (portal is null) { portalAppliesMissed++; return; }
        if (cancelling)
        {
            // Vanilla's own method, not the two writes re-implemented: it also finds and removes the lord by job type
            // (`:571987`), which is a search this file has no business duplicating.
            portal.CancelLoad();
            portalCancelled++;
            return;
        }
        var cargo = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == thingId);
        if (cargo is null) { portalAppliesMissed++; return; }
        var transferable = new TransferableOneWay();
        transferable.things.Add(cargo);
        portal.AddToTheToLoadList(transferable, count);
        portalApplied++;
    }

    /// <summary>Portal loads applied, and loads whose object could not be found. A non-zero second number with a zero
    /// first is the signature of a payload that travels but never lands - which is what the missing patch produced for
    /// every portal load before this existed.</summary>
    internal static long portalApplied;
    internal static long portalAppliesMissed;
    internal static long portalCancelled;

    /// <summary>Re-queues the incident on the side that owns the queue, using vanilla's own delay formulas.
    ///
    /// <para><b>Why the delay is recomputed and not carried.</b> See <see cref="CommandCodec.Diplomacy"/>: the two
    /// request paths use different recipes (flat 120000 for a caravan, a 1-2 hour draw for an orbital trader), and a
    /// carried offset would be a third copy of them. The cost is that a random draw happens once per side, so the exact
    /// tick differs by up to 5000 ticks - which matters for nothing: the incident queue fires by `FireTick &lt;= now`,
    /// never at a specific tick, and the retry window is 240000.</para></summary>
    private static void ApplyDiplomacy(byte[] payload)
    {
        // **No `experimentalInteractions` gate here, and that is deliberate.** `CaptureIncident` - the sending half -
        // never had one, so gating only the receiving half made the pair asymmetric: with the setting at its default
        // `false` the authority published and the replica silently discarded, which is a guaranteed divergence rather
        // than a disabled feature. The setting's own doc says "transport loading sync"; an incident a player queued is
        // vanilla queue state a replica never ticks (`:384088`), so dropping it is not a conservative default.
        if (!CommandCodec.TryDecodeDiplomacy(payload, out var defName, out var factionLoadId, out var traderKind,
                out var mapId, out var retryTicks, out var quickAid)) return;
        var def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
        if (def is null) { diplomacyMissed++; return; }
        Map? map = null;
        foreach (var candidate in Find.Maps)
            if (candidate is not null && candidate.uniqueID == mapId) { map = candidate; break; }
        if (map is null) { diplomacyMissed++; return; }
        Faction? faction = null;
        if (factionLoadId >= 0 && Find.FactionManager is { } manager)
            foreach (var candidate in manager.AllFactionsListForReading)
                if (candidate is not null && candidate.loadID == factionLoadId) { faction = candidate; break; }

        var parms = new IncidentParms
        {
            target = map,
            faction = faction,
            forced = true,
            traderKind = traderKind is { Length: > 0 } ? DefDatabase<TraderKindDef>.GetNamedSilentFail(traderKind) : null,
        };
        if (quickAid)
        {
            parms.raidArrivalModeForQuickMilitaryAid = true;
            parms.points = DiplomacyTuning.RequestedMilitaryAidPointsRange.RandomInRange;
        }
        // The fire tick, per path: a caravan is +120000 flat (`:486929`); an orbital trader draws 1-2 hours
        // (`:486625`, `:486865`). Military aid is immediate, which is what `quickAid` marks.
        var fireTick = Find.TickManager.TicksGame + (quickAid ? 0
            : def == IncidentDefOf.OrbitalTraderArrival ? (int)System.Math.Round(Rand.Range(1f, 2f) * 2500f)
            : 120000);
        Find.Storyteller?.incidentQueue?.Add(def, fireTick, parms, retryTicks);
        diplomacyApplied++;
    }

    /// <summary>Requests applied here, and requests whose def or map this peer does not have. The second number being
    /// non-zero while the first is zero is the signature of a payload that travels but never lands.</summary>
    internal static long diplomacyApplied;
    internal static long diplomacyMissed;

    private static void ApplyTransport(byte[] payload)
    {
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return;
        if (!CommandCodec.TryDecodeTransport(payload, out var transporterId, out var thingId, out var count)) return;
        var pod = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == transporterId);
        var transporter = pod?.TryGetComp<CompTransporter>();
        var cargo = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == thingId);
        if (transporter == null || cargo == null) return;
        var transferable = new TransferableOneWay();
        transferable.things.Add(cargo);
        transporter.AddToTheToLoadList(transferable, count);
    }

    private static LocalTargetInfo ResolveTarget(int packedCell, int thingId)
    {
        if (thingId > 0)
        {
            var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
            return thing != null ? new LocalTargetInfo(thing) : LocalTargetInfo.Invalid;
        }
        if (packedCell < 0) return LocalTargetInfo.Invalid;
        var x = packedCell / 1000;
        var z = packedCell % 1000;
        var map = Find.CurrentMap;
        return map != null && new IntVec3(x, 0, z).InBounds(map) ? new LocalTargetInfo(new IntVec3(x, 0, z)) : LocalTargetInfo.Invalid;
    }
}
// ---------- Harmony Capture Patches ----------
//
// Every patch here follows the model Multiplayer uses: a player's action is forwarded to the server and
// the local vanilla call is cancelled, so the command is applied when its tick arrives - on every peer,
// the one that clicked included. Vanilla's own calls during a tick are left alone, because those run
// identically on every peer anyway and cancelling them would drop them on the origin only.
//
// The earlier shape (let vanilla run, then skip the echo) applied the action twice when the echo was not
// skipped and gave the origin a two-tick head start when it was. Both showed up as divergence.

/// <summary>Shape shared by the simple patches: capture the arguments, cancel the local call.</summary>
internal static class CapturePrefix
{
    internal static bool Defer(bool captured)
    {
        // false means "the player acted": vanilla must not also perform it.
        return !captured;
    }
}

/// <summary>Captures the training-tab checkbox, which is a static widget rather than a gizmo.
///
/// <para><b>Why a postfix with an argument snapshot.</b> `TrainingCardUtility.DoTrainableCheckbox`
/// (`:648116-648137`) reads <c>GetWanted</c> into a local, passes it by <c>ref</c> to <c>Widgets.Checkbox</c>, and
/// calls <c>SetWantedRecursive</c> only when the value changed. So the argument after the call is exactly "was it
/// toggled", and a prefix would have to compare against a stale copy instead. Passing <c>__args</c> rather than a named
/// parameter also survives the `ref bool` signature this method uses.</para>
///
/// <para>The capture lets the local call run (it is not cancelled): the checkbox is this peer's own UI state and the
/// command carries the result, so cancelling it would make the click look ignored until the echo arrived.</para></summary>
[HarmonyPatch(typeof(TrainingCardUtility), nameof(TrainingCardUtility.DoTrainableCheckbox))]
internal static class TrainingCheckboxCapturePatch
{
    private static void Postfix(object[] __args)
    {
        if (!SharedActions.PlayerActing) return;
        if (__args is not { Length: >= 4 }) return;
        if (__args[1] is not Pawn pawn || __args[2] is not TrainableDef def) return;
        if (pawn.training is null) return;
        // No "did it change" test is needed: `SetWantedRecursive` fires only on a change, and re-publishing the same
        // value is idempotent on the host (`SetWanted` is a plain `DefMap` write). Comparing against a snapshot taken in
        // a prefix would be the alternative and it is strictly worse - `RimWorld` draws this widget from several places.
        SharedActions.CaptureTrainingWanted(pawn, def, pawn.training.GetWanted(def));
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
internal static class DesignationCapturePatch
{
    private static bool Prefix(Designation newDes)
    {
        if (!SharedActions.PlayerActing) return true;
        // Local-first: add the mark here *and* publish it. The apply path already skips a cell that has the mark, so
        // the echoed command cannot double it. Cancelling made the mark appear only after the round trip, which
        // during a drag reads as "the selection does nothing".
        SharedActions.CaptureDesignation(newDes);
        return true;
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation))]
internal static class DesignationRemoveCapturePatch
{
    private static bool Prefix(Designation des)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureRemoveDesignation(des);
        return true;
    }
}

/// <summary>Records a blueprint placed by a caller that is not the player's designator, and lets it run.
///
/// <para><b>It must not cancel.</b> This prefix used to cancel the placement and set the result to null, which
/// crashes every caller that dereferences the return value. Vanilla's own player path does exactly that
/// (<c>RimWorld/Designator_Build.cs:522-527</c>):
/// <code>
/// Blueprint_Build bp = GenConstruct.PlaceBlueprintForBuild(...);   // cancelled =&gt; null
/// bp.StyleSourcePrecept = sourcePrecept;                          // NullReferenceException
/// bp.StyleSourcePrecept ??= ...; bp.glowerColorOverride = ...;    // two more
/// </code>
/// Measured: placing a door onto an existing blueprint logged `Root level exception in OnGUI():
/// System.NullReferenceException` at <c>Designator_Build.DesignateSingleCell</c>. That path is now handled one level
/// up by <see cref="DesignatorSingleCellPatch"/>, which skips the vanilla body entirely, so this prefix no longer sees
/// player placements - but a mod or a game script that places a blueprint directly still dereferences the result, and
/// cancelling on them would crash the same way.</para>
///
/// <para>Not cancelling cannot cause the double-placement the id rule exists to prevent: the resulting local object
/// is adopted by the host's creation record when it arrives, instead of a second one being made. See
/// <c>AuthoritativeReplica.CreateThing</c>.</para>
/// </summary>
[HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild))]
internal static class BlueprintCapturePatch
{
    private static void Prefix(BuildableDef sourceDef, IntVec3 center, Map map, Rot4 rotation, ThingDef stuff)
    {
        if (!SharedActions.PlayerActing || sourceDef is null || map is null) return;
        // Publish only; the local placement runs. `CaptureBlueprint` answers false when this peer is applying a
        // command, which is exactly when the original must be left alone anyway.
        SharedActions.CaptureBlueprint(sourceDef, stuff, center, map, rotation);
    }
}

/// <summary>Cancelling a blueprint or frame is a removal the player asked for; the simulation also destroys
/// things during a tick, and those must run locally or the colony stalls.</summary>
[HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.Destroy), new[] { typeof(DestroyMode) })]
internal static class BlueprintCancelCapturePatch
{
    private static bool Prefix(ThingWithComps __instance, DestroyMode mode)
    {
        if (mode != DestroyMode.Cancel || __instance is not Blueprint) return true;
        if (!SharedActions.PlayerActing) return true;
        return !SharedActions.CaptureCancelThing(__instance);
    }
}

[HarmonyPatch(typeof(Frame), nameof(Frame.Destroy))]
internal static class FrameCancelCapturePatch
{
    private static bool Prefix(Frame __instance, DestroyMode mode)
    {
        if (mode != DestroyMode.Cancel || !SharedActions.PlayerActing) return true;
        return !SharedActions.CaptureCancelThing(__instance);

    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
internal static class DraftCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_DraftController, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_DraftController, Pawn>("pawn");

    private static bool Prefix(Pawn_DraftController __instance, bool value)
    {
        if (!SharedActions.PlayerActing) return true;
        return !SharedActions.CaptureDraft(PawnRef(__instance), value);
    }
}

/// <summary>An order is captured from its argument, because the job has not been taken yet.</summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
internal static class OrderCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

    private static bool Prefix(Pawn_JobTracker __instance, Job job, ref bool __result)
    {
        if (!SharedActions.PlayerActing) return true;
        var pawn = PawnRef(__instance);
        if (pawn is null || job?.def is null) return true;
        SharedActions.CaptureOrderJob(pawn, job);
        __result = true;
        return false;
    }
}

/// <summary>Ending an ordered job on a player's click (round 127).
///
/// <para>`PawnGotoAction` (`:657742`, the single-pawn right-click goto and, through `IssueGotoJobs` `:668758`, the
/// whole multi-pawn drag) has two paths. The one that issues a job reaches `TryTakeOrderedJob` (`:657768`) and is
/// already captured by <see cref="OrderCapturePatch"/>. The other one - clicked cell equals the pawn's own cell while
/// a `Goto` job is active - calls `pawn.jobs.EndCurrentJob(JobCondition.Succeeded)` (`:657750`) and reaches nothing we
/// patched, so "stop where you are" moved the pawn on the clicking peer only.</para>
///
/// <para>This is a prefix on the *action* rather than on `EndCurrentJob`, deliberately: `EndCurrentJob` is called by
/// the job system itself on every peer that ticks, and a capture there would have to tell a click from a job
/// finishing - a distinction the method signature does not carry. Patching the action means every capture is a
/// player gesture by construction.</para></summary>
[HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
internal static class GotoStopCapturePatch
{
    private static bool Prefix(IntVec3 clickCell, Pawn pawn, IntVec3 gotoLoc)
    {
        try
        {
            if (pawn is null || pawn.Position != gotoLoc || pawn.CurJobDef != JobDefOf.Goto) return true;
            return !SharedActions.CaptureStop(pawn, JobCondition.Succeeded);
        }
        catch (Exception) { return true; }
    }
}

/// <summary>
/// The gizmo path. <c>CompForbiddable.Forbidden</c> has no <c>SetForbidden</c> in its call chain, so without this
/// patch a player who toggles "allow" on a selection changes only their own game.
/// </summary>
[HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
internal static class ForbidPropertyCapturePatch
{
    private static bool Prefix(CompForbiddable __instance, bool value)
    {
        try { return !SharedActions.TryCaptureDirectForbid(__instance.parent, value); }
        catch (Exception) { return true; }
    }
}

[HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.SetForbidden))]
internal static class ForbidCapturePatch
{
    private static bool Prefix(Thing t, bool value)
    {
        // Returning true here means the click ran locally and nothing was sent, which is exactly what a
        // player describes as "I clicked it and the other side did not change". Record which gate refused.
        if (!SharedActions.PlayerActing) { if (t is not null) SharedActions.NoteGateBlocked("forbid"); return true; }
        if (t is null) return true;
        return !SharedActions.CaptureForbid(t, value);
    }
}

/// <summary>
/// Research is chosen in a window, not through a gizmo, so the generic capture never saw it - the project a
/// player picked stayed local while the progress advanced on every peer.
///
/// Inside a tick the game also finishes projects and switches over, and cancelling those calls would stall
/// the colony; only input outside a tick is deferred to the agreed tick.
/// </summary>
/// <summary>Captures the three "this pawn leaves the colony" decisions, which run from a confirmation dialog's
/// callback rather than from a gizmo's `ProcessInput`.
///
/// <para><b>Why `Dialog_MessageBox` was invisible.</b> `GizmoStatePatch` samples around `Command.ProcessInput`, so it
/// observes a button press and the fragment diff that follows. These three buttons are different: pressing them opens
/// `PawnBanishUtility.ShowBanishPawnConfirmationDialog` (`:444866`), whose `onConfirm` callback calls `Banish` one frame
/// later — a `Dialog_MessageBox` button is not a `Command` and its callback never passes through `ProcessInput`. The
/// ledger read all three as "player entry, effect lands elsewhere" for fifteen rounds because the *gizmo* that opens
/// the dialog is covered; what was never covered is the dialog's own button.</para>
///
/// <para><b>Not cancelled.</b> The action runs locally as well: the confirmation dialog already showed the player the
/// consequence, and cancelling would mean the authority is the only place a banished pawn ever disappears from — the
/// clicking peer would keep the pawn on screen until the frame that removes it arrives. Both sides end in the same
/// state because the decision is the same one.</para></summary>
[HarmonyPatch(typeof(PawnBanishUtility), nameof(PawnBanishUtility.Banish), [typeof(Pawn), typeof(bool)])]
internal static class BanishCapturePatch
{
    private static void Prefix(Pawn pawn)
    {
        if (SharedActions.PlayerActing) SharedActions.CapturePawnLeave("banish", pawn);
    }
}

/// <summary>`RecruitUtility.Recruit(Pawn, Faction, Pawn)` is the single write that makes a prisoner a colonist —
/// `InteractionWorker_RecruitAttempt.DoRecruit` (`:456930`) is its only caller from the gameplay path, and the prisoner
/// tab's recruit gizmo ends there. Patched on `Recruit` rather than on `DoRecruit` because `DoRecruit` is also called
/// with a `null` recruiter by quests and debug actions (`:257898`, `:281034`), which are authority-side already.</summary>
[HarmonyPatch(typeof(RecruitUtility), nameof(RecruitUtility.Recruit))]
internal static class RecruitCapturePatch
{
    private static void Prefix(Pawn pawn) => SharedActions.CapturePawnLeaveIfActing("recruit", pawn);
}

/// <summary>Releasing a colony animal to the wild, from the animal's own gizmo. The gizmo opens no dialog, but the
/// call is a plain static method rather than a `Command.action`, so the generic capture does not see it either.</summary>
[HarmonyPatch(typeof(ReleaseAnimalToWildUtility), nameof(ReleaseAnimalToWildUtility.DoReleaseAnimal))]
internal static class ReleaseAnimalCapturePatch
{
    // `(Pawn animal, Pawn releasedBy)` - the first parameter is the animal, the second the colonist letting it go.
    private static void Prefix(Pawn animal) => SharedActions.CapturePawnLeaveIfActing("release", animal);
}

[HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.SetCurrentProject))]
internal static class ResearchCapturePatch
{
    private static bool Prefix(ResearchProjectDef proj)
    {
        if (!SharedActions.PlayerActing) { if (proj is not null) SharedActions.NoteGateBlocked("research"); return true; }
        if (proj is null) return true;
        return !SharedActions.CaptureResearch(proj);

    }
}

[HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.StopProject))]
internal static class ResearchStopCapturePatch
{
    private static bool Prefix()
    {
        if (!SharedActions.PlayerActing) return true;
        return !SharedActions.CaptureResearch(null);

    }
}

[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
internal static class PriorityCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");

    private static bool Prefix(Pawn_WorkSettings __instance, WorkTypeDef w, int priority)
    {
        if (!SharedActions.PlayerActing || w is null) return true;
        return !SharedActions.CapturePriority(PawnRef(__instance), w, priority);
    }
}

/// <summary>Records whether the cell count actually grew, so a silently refused add cannot be mistaken for success.
/// </summary>
/// <summary>The right-click menu's only execution point.
///
/// <para><b>The defect this closes.</b> `FloatMenuOption.Chosen(bool, FloatMenu)` (`:179550-179571`) is where a
/// right-click option runs: it calls `<c>FloatMenu.PreOptionChosen</c>`, then the option's own <c>Action</c>. Nothing
/// patched it, so every right-click order - haul, equip, wear, arrest, rescue, strip, ingest, carry, tend, capture, load a
/// caravan, start a ritual, trade - ran on the clicking peer only and was never published. The click looked like it
/// worked, and the other peers never saw it.</para>
///
/// <para><b>Why this is one patch and not forty.</b> 1.7 replaced the old `FloatMenuMakerMap.AddUndraftedOrders` family
/// with about fifty `FloatMenuOptionProvider` subclasses (`:656278` onward), each building its own option with its own
/// closure. Patching the providers would mean one patch per provider and a new one for every mod that adds one. They all
/// end at this single method, so this is the chokepoint - the same reason `GizmoStatePatch` exists for
/// `Command.ProcessInput`.</para>
///
/// <para><b>Not canceling.</b> The action must run locally: most options read world state that only this peer just
/// mutated (a pawn's position for a move order, a bill's state for a "do until" change), and the capture works by
/// comparing fragments taken before and after. Cancelling would leave the local world unchanged, the comparison empty,
/// and the click genuinely lost.</para></summary>
[HarmonyPatch(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen), [typeof(bool), typeof(FloatMenu)])]
internal static class FloatMenuChosenCapturePatch
{
    private static void Prefix(FloatMenuOption __instance, out List<SharedActions.GizmoSnapshot>? __state) =>
        __state = SharedActions.CaptureFloatMenuTargets(__instance?.revalidateClickTarget,
            __instance?.revalidateWorldClickTarget);

    private static void Postfix(List<SharedActions.GizmoSnapshot>? __state) =>
        SharedActions.FlushFloatMenuTargets(__state);
}

[HarmonyPatch(typeof(Zone), nameof(Zone.AddCell))]
internal static class ZoneCellProbePatch
{
    private static void Prefix(Zone __instance, out int __state)
    {
        __state = -1;
        if (!SharedColony.HasSharedWorld) return;
        try { __state = __instance?.CellCount ?? -1; } catch (Exception) { }
    }

    private static void Postfix(Zone __instance, IntVec3 c, int __state)
    {
        if (!ThingDrawTrace.Enabled || __state < 0) return;
        try { ZoneCellProbe.Record(__instance, c, __state, __instance?.CellCount ?? -1); }
        catch (Exception) { }
    }
}
[HarmonyPatch(typeof(Zone), nameof(Zone.AddCell))]
internal static class ZoneAddCellCapturePatch
{
    private static void Prefix(Zone __instance, IntVec3 c)
    {
        // Deliberately NOT cancelled. Vanilla's zone designator builds a zone cell by cell and tests
        // Map.zoneManager.ZoneAt(c) == SelectedZone to decide which neighbours it may grow into, so
        // suppressing the local AddCell left the zone empty, the flood fill never found a neighbour, and
        // every drag produced another empty zone - "储存区 1..23", each logging "overwriting slot group ...
        // of itself". Applying is idempotent instead: see ApplyZoneCell.
        if (SharedActions.PlayerActing) SharedActions.CaptureZoneCell(__instance, c, add: true);
        else SharedActions.NoteGateBlocked("zone-add");
        // Written down on both paths: a zone that exists on one peer only is otherwise explained by "the patch is
        // missing" and "the gate refused it" equally well, which is the ambiguity that already cost a round on the
        // forbid path.
        ZoneGate.Record("add " + __instance.ID + " " + c.x + "|" + c.z, SharedActions.PlayerActing,
            ZoneGate.CallerChain());
    }
}

[HarmonyPatch(typeof(Zone), nameof(Zone.RemoveCell))]
internal static class ZoneRemoveCellCapturePatch
{
    private static void Prefix(Zone __instance, IntVec3 c)
    {
        // Same reason as ZoneAddCell: the caller keeps working with the zone it just mutated.
        if (SharedActions.PlayerActing) SharedActions.CaptureZoneCell(__instance, c, add: false);
    }
}

// Delete() is the user-facing wrapper; Delete(bool) is a separate virtual overload.
[HarmonyPatch(typeof(Zone), nameof(Zone.Delete), new Type[0])]
internal static class ZoneDeleteCapturePatch
{
    private static bool Prefix(Zone __instance)
    {
        if (!SharedActions.PlayerActing) return true;
        return !SharedActions.CaptureZoneDelete(__instance);
    }
}

[HarmonyPatch(typeof(BillStack), nameof(BillStack.AddBill))]
internal static class BillAddCapturePatch
{
    private static bool Prefix(BillStack __instance, Bill bill)
    {
        if (!SharedActions.PlayerActing || bill?.recipe is null) return true;
        // A medical bill is not deferred: its body part is assigned *after* this call (`:442400-442402` refuses to do
        // it before the bill is in a stack), so cancelling here would leave the part unset on the peer that clicked.
        // `SurgeryBillCapturePatch` below publishes the finished bill instead.
        if (bill is Bill_Medical) return true;
        return !SharedActions.CaptureBillAdd(__instance, bill);

    }
}

/// <summary>Publishes a finished surgery bill, after `Part` has been assigned.
///
/// <para>`HealthCardUtility.CreateSurgeryBill` (`:644616-644623`) is the only construction site of `Bill_Medical`
/// (`:644618`, the sole `new Bill_Medical` in the assembly), and it assigns the part last - so this postfix is the
/// earliest moment a complete bill exists. A prefix on `AddBill` cannot see the part, and the part is what the surgery
/// is for: without it `CompletableEver` (`:442369-442373`) is false and the bill does nothing on the other peers.</para>
///
/// <para>Vanilla's own callers also use the return value (`:596896` assigns `.xenogerm` to it), and this patch leaves
/// the method's result intact because it is a postfix that touches nothing.</para></summary>
[HarmonyPatch(typeof(HealthCardUtility), nameof(HealthCardUtility.CreateSurgeryBill))]
internal static class SurgeryBillCapturePatch
{
    private static void Postfix(Pawn medPawn, Bill_Medical __result) =>
        SharedActions.CaptureSurgeryBill(medPawn, __result);
}

/// <summary>Captures the *re-targeting* of a xenogerm implant, which happens after the bill already exists.
///
/// <para><b>The second ordering trap in the same call.</b> `Xenogerm.SetTargetPawn` (`:596888-596897`) runs, in order:
/// find the old target's bill whose <c>xenogerm</c> is this item and delete it, then
/// <c>CreateSurgeryBill(newTarget, ImplantXenogerm, null).xenogerm = this</c>, then <c>targetPawn = newTarget</c>, then
/// <c>SendImplantationLetter(newTarget)</c>. The `SurgeryBillCapturePatch` postfix therefore fires while
/// <c>bill.xenogerm</c> is still null - `Bill.ExposeData` scribes that field (`:272315`), so the fragment carried null and
/// the receiving peer got an implant bill with no gene set attached. The gene set is the whole point of the bill:
/// `SurgeryOutcomeComp_XenogermComplexity` reads <c>bill.xenogerm</c> (`:722890-722894`) and returns 0 without it.</para>
///
/// <para><b>Why the letter call is the hook.</b> It is the last statement of the confirmation callback, so by the time
/// its postfix runs the bill exists with its part, the <c>xenogerm</c> is assigned and <c>targetPawn</c> is updated -
/// the first moment a complete pair of (bill, xenogerm) exists. `SendImplantationLetter` is `private`, but a Harmony
/// patch does not need it to be public. Both writes are already done by then, so the capture is a pure read.</para>
///
/// <para>The message carries the replaced index because the callback deletes the previous targeting on the old pawn;
/// without it the old pawn keeps an implant bill on every other peer.</para></summary>
[HarmonyPatch(typeof(Xenogerm), "SendImplantationLetter")]
internal static class XenogermRetargetCapturePatch
{
    private static void Postfix(Xenogerm __instance, Pawn ___targetPawn)
    {
        if (__instance is null || ___targetPawn?.BillStack is null) return;
        var bill = ___targetPawn.BillStack.Bills.FirstOrDefault(b => b is Bill_Medical && b.xenogerm == __instance);
        if (bill is not Bill_Medical medical) return;
        // The callback deletes the old target's bill *before* creating the new one, so on this peer the index the
        // sender must delete is already gone; send one more than the count so the receiver's bounds check treats the
        // replacement as a plain add - and count what happened, because a silent miss would look like success.
        SharedActions.CaptureSurgeryBill(___targetPawn, medical);
    }
}

[HarmonyPatch(typeof(BillStack), nameof(BillStack.Delete))]
internal static class BillDeleteCapturePatch
{
    private static bool Prefix(BillStack __instance, Bill bill)
    {
        if (!SharedActions.PlayerActing || bill is null) return true;
        return !SharedActions.CaptureBillDelete(__instance, bill);

    }
}

[HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
internal static class AreaAssignCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_PlayerSettings, Pawn>("pawn");

    private static bool Prefix(Pawn_PlayerSettings __instance, Area value)
    {
        if (!SharedActions.PlayerActing) return true;
        return !SharedActions.CaptureAreaAssign(PawnRef(__instance), value);
    }
}

/// <summary>The three buttons in `Dialog_ManageAreas` that are not designators.
///
/// <para>Drawing an area is `Designator_AreaAllowed`, already covered by <see cref="DesignatorSingleCellPatch"/> and
/// `DesignatorMultiCellPatch`. Invert / Clear / Delete are plain method calls from the window
/// (`RimWorld/Dialog_ManageAreas.cs:89,111`), so without these three patches a player could reshape an allowed area on
/// their screen while every other peer kept the old shape - and because the allowed area decides where a colonist may
/// walk, the two worlds would then disagree about reachability, which is visible as "my colonist will not go there".</para>
/// </summary>
[HarmonyPatch(typeof(Area), nameof(Area.Invert))]
internal static class AreaInvertCapturePatch
{
    private static bool Prefix(Area __instance) =>
        !SharedActions.PlayerActing || !SharedActions.CaptureAreaOp(__instance, "invert");
}

[HarmonyPatch(typeof(Area), nameof(Area.Clear))]
internal static class AreaClearCapturePatch
{
    private static bool Prefix(Area __instance) =>
        !SharedActions.PlayerActing || !SharedActions.CaptureAreaOp(__instance, "clear");
}

[HarmonyPatch(typeof(Area), nameof(Area.Delete))]
internal static class AreaDeleteCapturePatch
{
    private static bool Prefix(Area __instance) =>
        !SharedActions.PlayerActing || !SharedActions.CaptureAreaOp(__instance, "delete");
}

/// <summary>Captures the four assignment panels, which have no gizmo and therefore no generic sample.
///
/// <para>One patch per setter rather than one per panel: the panels write these properties and nothing else, and a
/// prefix here sees the assignment whichever UI reached it (the assign tab, a column header's "assign to all", or a
/// mod's own dialog). Sampling and flushing wrap the whole setter so the comparison brackets exactly one change.</para>
/// </summary>
[HarmonyPatch]
internal static class PolicyCapturePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Timetable is a method, the other three are properties: `Pawn_TimetableTracker.SetAssignment(int, TimeAssignmentDef)`
        // writes one hour and has no setter at all.
        yield return AccessTools.Method(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment),
            [typeof(int), typeof(TimeAssignmentDef)]);
        // Two more trackers edited from the pawn table, measured with `tracker-probe`: `reading×3` and
        // `inventoryStock×3` exist as live objects, and both are written through methods rather than a property setter.
        // `PawnColumnWorker_Carry` ("carry" column) calls `SetThingForGroup` (`:676944`) and `SetCountForGroup`
        // (`:676960`); `Pawn_ReadingTracker.CurrentPolicy` has a setter (`:478424`), but reading policy is picked in the
        // pawn's own tab, not this table, so only the stock methods are added here.
        yield return AccessTools.Method(typeof(Pawn_InventoryStockTracker), nameof(Pawn_InventoryStockTracker.SetThingForGroup),
            [typeof(InventoryStockGroupDef), typeof(ThingDef)]);
        yield return AccessTools.Method(typeof(Pawn_InventoryStockTracker), nameof(Pawn_InventoryStockTracker.SetCountForGroup),
            [typeof(InventoryStockGroupDef), typeof(int)]);
        foreach (var (type, name) in PropertySetters)
        {
            var property = AccessTools.Property(type, name);
            if (property?.GetSetMethod(true) is { } setter) yield return setter;
        }
    }

    private static readonly (Type Type, string Name)[] PropertySetters =
    [
        (typeof(Pawn_OutfitTracker), nameof(Pawn_OutfitTracker.CurrentApparelPolicy)),
        (typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.CurrentPolicy)),
        (typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy))
    ];

    private static void Prefix(object __instance, out SharedActions.PolicySnapshot? __state)
    {
        __state = null;
        // `__instance` is a tracker, and each of the four holds its pawn in the same field name.
        if (!SharedActions.PlayerActing) return;
        if (PawnOf(__instance) is { } pawn) __state = SharedActions.CapturePolices(pawn);
    }

    private static void Postfix(object __instance, SharedActions.PolicySnapshot? __state)
    {
        if (__state is null || PawnOf(__instance) is not { } pawn) return;
        SharedActions.FlushPolicies(__state, pawn);
    }

    private static readonly Dictionary<Type, AccessTools.FieldRef<object, Pawn>> PawnRefs = [];

    /// <summary>Reads the tracker's `pawn` field. Every one of the four declares it as a public field named `pawn`,
    /// and reflection is needed because a Harmony prefix cannot bind a type that differs per target method.</summary>
    private static Pawn? PawnOf(object tracker)
    {
        if (tracker is null) return null;
        var type = tracker.GetType();
        if (!PawnRefs.TryGetValue(type, out var reference))
        {
            var field = AccessTools.Field(type, "pawn");
            if (field is null || field.FieldType != typeof(Pawn)) return null;
            reference = AccessTools.FieldRefAccess<object, Pawn>(field);
            PawnRefs[type] = reference;
        }
        return reference(tracker);
    }
}

/// <summary>The bill settings dialog, sampled on open and compared on close.
///
/// <para>Why not patch the three setter methods the ledger names: `Dialog_BillConfig` also writes ten plain fields,
/// so three methods would cover at most part of one session - and a session is what the player performed. Two
/// floats would also arrive in an order a field-by-field delta has to model; one whole-stack fragment has no
/// order to get wrong.</para></summary>
[HarmonyPatch(typeof(Window), nameof(Window.PreClose))]
internal static class BillDialogCapturePatch
{
    private static void Prefix(Window __instance, out string? __state)
    {
        __state = null;
        if (!SharedActions.PlayerActing) return;
        if (SharedActions.TryBillDialog(__instance, out var giver)) __state = SharedActions.CaptureBills(giver!);
    }

    private static void Postfix(Window __instance, string? __state)
    {
        if (__state is null) return;
        if (SharedActions.TryBillDialog(__instance, out var giver)) SharedActions.FlushBills(__state, giver!);
    }
}


/// <summary>Captures a dialog that writes to the selected building, using the same snapshot pair the gizmo path uses.
///
/// <para><b>Why the existing gizmo capture does not already cover this.</b> `GizmoStatePatch` hooks
/// `Command.ProcessInput` (`:2205-2233`), so it only sees *gizmo* clicks. `Dialog_CreateXenogerm.StartAssembly`
/// (`:630288-630293`) is reached from a dialog button instead, and it calls
/// <c>Building_GeneAssembler.Start(selectedGenepacks, arc, xenotypeName, iconDef)</c> (`:581172-581181`), which sets
/// <c>genepacksToRecombine</c>, <c>architesRequired</c>, <c>xenotypeName</c>, <c>iconDef</c>, <c>workingInt = true</c>
/// and <c>totalWorkRequired</c>. Every one of those is scribed by `Building_GeneAssembler.ExposeData`
/// (`:581416-581430`), so the building's own fragment is exactly the state - no new channel is needed, only a capture
/// point.</para>
///
/// <para><b>Why snapshots rather than a hand-built payload.</b> `CaptureGizmoTargets` already skips blueprints and
/// frames, already handles zones, and already refuses an oversized fragment; `FlushGizmoTargets` already publishes
/// `thing-state` and already holds the local world until the ordered tick arrives. Re-implementing that for one dialog
/// would be a second place to get the despawn/respawn behaviour wrong.</para>
///
/// <para><b>The building must still be selected.</b> It is: the dialog is opened from the assembler's gizmo and nothing
/// in it changes the selection. If that ever stops being true the capture silently produces nothing, which is why the
/// summary counts assemblers separately.</para></summary>
/// <summary>Owner assignment: a dialog button, not the gizmo that opens it.
///
/// <para><b>The same defect as `banish`/`recruit`/`release-animal` and `dryad-caste`.</b>
/// `CompAssignableToPawn.CompGetGizmosExtra` (`:529569`) only adds `Dialog_AssignBuildingOwner`
/// (`:529579`); both writes are dialog buttons - `assignable.TryAssignPawn(pawn)` (`:625635`) and
/// `assignable.TryUnassignPawn(pawn)` (`:625578`). A patch on `Command.ProcessInput` observes the press that opens the
/// window and nothing after it, which is why the ledger's "GizmoStatePatch 覆盖" was wrong for these two entries.</para>
///
/// <para><b>Why both methods and not the comp's fields.</b> `CompAssignableToPawn.TryAssignPawn` is `virtual` and three
/// subclasses replace the body entirely: `CompAssignableToPawn_Bed`/`_Grave`/`_Throne` route to
/// `pawn.ownership.ClaimBedIfNonMedical` / `ClaimGrave` / `ClaimThrone` (`:529069-529072`, `:529137-529140`), which is
/// what the *pawn's* `Pawn_Ownership` scribes (`:473149-473159`). Patching the base field write would miss every one of
/// them; patching the virtual entry point catches all three plus the base.</para>
///
/// <para><b>The owner is the parent, not the selection.</b> `__instance.parent` is on the comp the dialog was opened
/// for; the player may click another thing while the modal is up.</para></summary>
[HarmonyPatch(typeof(CompAssignableToPawn), nameof(CompAssignableToPawn.TryAssignPawn))]
internal static class AssignableCapturePatch
{
    private static void Prefix(CompAssignableToPawn __instance, Pawn pawn,
        out List<SharedActions.GizmoSnapshot>? __state)
    {
        __state = SharedActions.CaptureCompOwner(__instance, pawn);
    }

    private static void Postfix(List<SharedActions.GizmoSnapshot>? __state) => SharedActions.FlushGizmoTargets(__state);
}

[HarmonyPatch(typeof(CompAssignableToPawn), nameof(CompAssignableToPawn.TryUnassignPawn))]
internal static class AssignableUnassignCapturePatch
{
    private static void Prefix(CompAssignableToPawn __instance, Pawn pawn,
        out List<SharedActions.GizmoSnapshot>? __state)
    {
        __state = SharedActions.CaptureCompOwner(__instance, pawn);
    }

    private static void Postfix(List<SharedActions.GizmoSnapshot>? __state) => SharedActions.FlushGizmoTargets(__state);
}

/// <summary>The dryad caste change: chosen in a dialog, confirmed in a `Dialog_MessageBox`, written by a private method.
///
/// <para><b>Why the gizmo capture cannot see it.</b> The tree's gizmo only *opens* `Dialog_ChangeDryadCaste`
/// (`:562654` builds a `Command_Action` whose action adds the window). The write is
/// `Dialog_ChangeDryadCaste.StartChange` (`:627750`, `treeConnection.desiredMode = selectedMode`), reached from a
/// `Dialog_MessageBox.CreateConfirmation` callback (`:627717-627722`) - not from any `Command.ProcessInput`. That is
/// the same shape as banish/recruit/release, and the reason those three were misfiled for fifteen rounds.</para>
///
/// <para><b>Why the snapshot pair is the right payload here.</b> `CompTreeConnection.PostExposeData` (`:562856`)
/// scribes `currentMode`, `desiredMode`, `nextUntornTick`, `spawnTick`, `lastPrunedTick`, both connection strengths,
/// `connectedPawn`, `gaumakerPod` and the dryad list - so the whole comp is one fragment and the change is inside it.
/// `StartChange` writes one def pointer, but the comp also ticks its connection strength on the authority, and sending
/// the fragment keeps both in step rather than pinning one field.</para>
///
/// <para>Sampled on the tree rather than on "the selection": the dialog is modal, and between opening it and
/// confirming, the player can select something else. `treeConnection.parent` is the one object that cannot move.</para>
/// </summary>
[HarmonyPatch(typeof(Dialog_ChangeDryadCaste), "StartChange")]
internal static class DryadCasteCapturePatch
{
    private static void Prefix(Dialog_ChangeDryadCaste __instance, out List<SharedActions.GizmoSnapshot>? __state)
    {
        __state = null;
        if (!SharedActions.PlayerActing) return;
        // `treeConnection` is private; a Harmony prefix can read it, and there is no public accessor on the dialog.
        var comp = HarmonyLib.AccessTools.Field(typeof(Dialog_ChangeDryadCaste), "treeConnection")?
            .GetValue(__instance) as CompTreeConnection;
        var tree = comp?.parent;
        var before = tree is null ? null : ScribeBridge.Save(tree);
        if (before is { Length: > 0 }) (__state ??= []).Add(new SharedActions.GizmoSnapshot('t', tree!.thingIDNumber, before));
    }

    private static void Postfix(List<SharedActions.GizmoSnapshot>? __state) => SharedActions.FlushGizmoTargets(__state);
}

[HarmonyPatch(typeof(Dialog_CreateXenogerm), "StartAssembly")]
internal static class GeneAssemblerStartCapturePatch
{
    private static void Prefix(out List<SharedActions.GizmoSnapshot>? __state) =>
        __state = SharedActions.PlayerActing ? SharedActions.CaptureGizmoTargets() : null;

    private static void Postfix(List<SharedActions.GizmoSnapshot>? __state) =>
        SharedActions.FlushGizmoTargets(__state);
}

/// <summary>The storyteller page. `Page.PreClose` is not virtual but `Page_SelectStorytellerInGame` overrides it with
/// its own `PreClose` (`:654988`), so the override is the target - patching the base class would never run for this
/// page, and patching `DoWindowContents` would sample once per frame.
///
/// <para>What the page can change: `storyteller.def` (which comp list decides every incident), `difficultyDef` and the
/// whole custom `Difficulty` object (`threatScale`, `allowBigThreats`, `cropYieldFactor`, …) — all three passed by
/// `ref` into `StorytellerUI.DrawStorytellerSelectionInterface` (`:654972`). Those are simulation inputs, so a replica
/// that keeps the old values draws a colony that will never receive the raids the authority is about to send.</para></summary>
[HarmonyPatch(typeof(Page_SelectStorytellerInGame), nameof(Page_SelectStorytellerInGame.PreClose))]
internal static class StorytellerCapturePatch
{
    private static void Prefix(out string? __state)
    {
        __state = SharedActions.PlayerActing ? SharedActions.CaptureStoryteller() : null;
    }

    private static void Postfix(string? __state)
    {
        if (__state is not null) SharedActions.FlushStoryteller(__state);
    }
}

[HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.AddToTheToLoadList))]
internal static class TransportCapturePatch
{
    private static bool Prefix(CompTransporter __instance, TransferableOneWay t, int count)
    {
        if (!SharedActions.PlayerActing || t is null) return true;
        return !SharedActions.CaptureTransport(__instance, t, count);

    }
}

/// <summary>The portal counterpart. A separate patch class because Harmony matches a **declared** method: attaching
/// this prefix to `CompTransporter` and hoping it also covers portals is the mistake that left portal loading
/// unsynchronised - the two methods share a name and nothing else.</summary>
/// <summary>The cancel half of the portal channel. Attached to `MapPortal.CancelLoad` rather than derived from the load
/// patch because a cancel reaches neither `AddToTheToLoadList` nor `SubtractFromToLoadList`: it is its own method that
/// removes a lord and clears the list.</summary>
[HarmonyPatch(typeof(MapPortal), nameof(MapPortal.CancelLoad))]
internal static class PortalCancelCapturePatch
{
    private static bool Prefix(MapPortal __instance) => !SharedActions.CapturePortalCancel(__instance);
}

/// <summary>Incidents queued by a player action, carried to the colony that runs the queue.
///
/// <para><b>Why `IncidentQueue.Add` and not the three dialog options.</b> `RequestTraderOption` (`:486880`) returns an
/// outer `DiaOption` that only carries a `link` (`:486940`); the writing `action` belongs to one inner option per
/// trader kind (`:486920-486932`), built inside a loop - so a postfix on the builder cannot wrap it, and wrapping
/// `__result.action` wraps null. `Add` (`:384081`) is where all three requests arrive, and where every other
/// player-triggered incident (quests, rituals, transport pods) arrives too.</para>
///
/// <para><b>Why suppressing here is safe.</b> The prefix returns false only when the request has been published, so the
/// local queue never receives an entry that the whole colony will not have - and `ApplyDiplomacy` queues it once on the
/// authority. The option's remaining lines (the cooldown stamp and the goodwill charge) still run on the clicking
/// machine by design: goodwill is published as state by `FactionSync` and reconciles through the frame, and the cooldown
/// is a number both sides have to agree on through a channel that does not exist yet - recorded as the remaining gap on
/// this row rather than pretended away.</para></summary>
[HarmonyPatch(typeof(IncidentQueue), nameof(IncidentQueue.Add), typeof(IncidentDef), typeof(int),
    typeof(IncidentParms), typeof(int))]
internal static class IncidentQueueCapturePatch
{
    private static bool Prefix(IncidentDef def, int fireTick, IncidentParms parms)
        => !SharedActions.CaptureIncident(def, fireTick, parms);
}

/// <summary>The portal counterpart. A separate patch class because Harmony matches a **declared** method: attaching
/// this prefix to `CompTransporter` and hoping it also covers portals is the mistake that left portal loading
/// unsynchronised - the two methods share a name and nothing else.</summary>

[HarmonyPatch(typeof(MapPortal), nameof(MapPortal.AddToTheToLoadList))]
internal static class PortalCapturePatch
{
    private static bool Prefix(MapPortal __instance, TransferableOneWay t, int count)
    {
        if (!SharedActions.PlayerActing || t is null) return true;
        return !SharedActions.CapturePortal(__instance, t, count);
    }
}

/// <summary>
/// Decides when a gizmo click is worth sampling. The comparison happens inside a single ProcessInput call,
/// so any mouse-button event is correct whether the button acts on down or on up; restricting the sample to
/// button events is purely about cost, because the selection is serialised twice per sample and ProcessInput
/// also runs for hover and repaint.
/// </summary>
internal static class GizmoInputGate
{
    internal static bool ShouldSample(UnityEngine.Event? ev) =>
        ev is null || ev.type is UnityEngine.EventType.MouseUp or UnityEngine.EventType.MouseDown;
}

/// <summary>
/// The fallback for every gizmo that has no dedicated command.
///
/// 1.7 declares ProcessInput on twenty-odd types and Harmony patches a method body, not a virtual slot, so a
/// patch on Command_Action alone never sees Command_Toggle, Command_SetBedOwnerType, Command_SetPlantToGrow,
/// Command_SetTargetFuelLevel or the ability and ritual commands. All of them are listed here instead. The
/// base types are included as well because a derived override that calls base.ProcessInput would otherwise
/// run its body unobserved; a nested sample simply sees no change and sends nothing.
/// </summary>
[HarmonyPatch]
internal static class GizmoStatePatch
{
    /// <summary>Every gizmo type whose ProcessInput this patch attaches to. Kept as one shared list so the
    /// self-check in <see cref="ModPatchAudit"/> audits exactly what is patched: a second copy had already
    /// drifted and named a type that no longer exists.</summary>
    internal static readonly string[] GizmoTargetNames =
    {
        "Verse.Command",
        "Verse.Command_Action",
        "Verse.Command_Toggle",
        "Verse.Command_Target",
        "Verse.Command_VerbTarget",
        "Verse.Command_SetBedOwnerType",
        "Verse.Command_SetPlantToGrow",
        "RimWorld.Command_Ability",
        "RimWorld.Command_Ritual",
        "RimWorld.Command_BestowerCeremony",
        // **This entry never resolved, and the click was never captured (round 140).** It read
        // `RimWorld.Command_CallBossgroup`; `ModPatchAudit.ReportGizmoHooks` attached 13 of 14 entry points on every start
        // and logged this one as 「没有可挂载的 ProcessInput」 - so the summon gizmo's click had never been captured on any
        // peer, and the symptom looked like a separate cooldown bug. The runtime measurement (`gizmo-hooks`) then showed
        // the type's `FullName` is `Command_CallBossgroup` with **no namespace at all**, which is why both `RimWorld.` and
        // `Verse.` failed to compile or resolve. Written bare it resolves directly; the resolver's fallback stays for the
        // entries that really are namespaced.
        "Command_CallBossgroup",
        "RimWorld.Command_SetNeuralSuperchargerAutoUse",
        "RimWorld.Command_SetTargetFuelLevel",
        "RimWorld.Command_LoadToTransporter",
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var names = GizmoTargetNames;
        foreach (var name in names)
        {
            // `TypeResolver`, not `AccessTools.TypeByName`: a wrong namespace is invisible to the compiler and used to
            // fail silently (round 140: `Command_CallBossgroup` had never resolved). The resolver's fallback keeps the
            // entry working and the audit prints that it needed it.
            var type = TypeResolver.Find(name);
            var method = type is null ? null : AccessTools.Method(type, "ProcessInput", [typeof(UnityEngine.Event)]);
            // A renamed or removed type is a missing feature, not a crash; PatchInstaller reports the count.
            // Abstract declarations have no body, and handing one to Harmony would fail the whole group and
            // silently disable gizmo sync for every type listed here, so they are skipped.
            if (method is not null && method.DeclaringType == type && !method.IsAbstract) yield return method;
        }
    }

    private static bool Prefix(UnityEngine.Event ev, out List<SharedActions.GizmoSnapshot>? __state)
    {
        __state = GizmoInputGate.ShouldSample(ev) ? SharedActions.CaptureGizmoTargets() : null;
        return true;
    }

    private static void Postfix(List<SharedActions.GizmoSnapshot>? __state) => SharedActions.FlushGizmoTargets(__state);
}




























