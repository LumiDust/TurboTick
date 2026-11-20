using System.Globalization;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Shared-colony session: the controller publishes the world, everyone loads the same save, and player
/// actions travel through the server. The server owns the tick, so peers only advance inside the ticks it
/// published; actions are applied at the tick they were ordered for.
/// </summary>
public static class SharedColony
{
    public static SharedSession Session { get; } = new();
    private static long nextSequence;
    private static readonly SimulationTickGuard tickGuard = new(Session.Barrier);
    internal static bool HasSharedWorld => Session.Phase != SharedPhase.Offline;

    public static bool Gating => Session.Phase == SharedPhase.Playing;

    public static long NextSequence() => ++nextSequence;

    public static void Reset()
    {
        Session.Leave();
        SharedActions.ResetTickWork();
        tickGuard.Cancel();
        holdUntilMs = -1;
        reportingArmed = false;
        loadPending = false;
        SimulationRandom.Reset();
    }

    private static int lastCheckpointTick;
    /// <summary>How far apart checkpoints are published.
    ///
    /// This is periodic, and it costs the room: the owner saves the whole colony on the game thread, and
    /// because the timeline is paced by the slowest peer every other peer stalls with it. Measured on a
    /// 15 MB quicktest colony: 286/300 ticks in the five seconds around tick 6000 (≈0.23 s) and 268/300
    /// around tick 12000 (≈0.53 s). Publishing only when a joiner asks would remove the stall but make late
    /// joins replay from the initial snapshot; moving the save to a paused or idle moment is the follow-up
    /// that keeps both.</summary>
    private const int CheckpointInterval = 6000;

    public static void OnWorldStarted(SessionInfo info, SnapshotManifest manifest)
    {
        reportingArmed = false;
        loadPending = false;
        expectedSaveName = null;
        tickGuard.Cancel();
        // Tell the server this peer is about to stop simulating. A loader executes no ticks, so it cannot
        // pace the room; without this the whole room froze for the 25-40 s a checkpoint reload takes.
        RoomConnection.ReportProgress(-1);
        Session.BeginLoading(info.Epoch, manifest.Id);
        // Barrier starts from the snapshot's tick, which is what the incoming world *is*. It deliberately does not
        // adopt the tick the currently-resident (about to be discarded) colony happens to be at: those are different
        // worlds, and mixing their clocks is how every stamp in the new one lands in the future.
        Session.Barrier.Reset(Math.Max(0, info.SnapshotTick));
        // The published tick is the room's, and a peer must never claim to be ahead of it - claiming ahead is what
        // made `CanAdvance` false forever (`appliedTick=30000, serverTick=0`). The load path corrects the engine clock
        // downwards instead, together with the stamps (`SharedGameClock.Align` + `ClockStampShift`).
        Session.Barrier.Publish(info.Tick);
        lastCheckpointTick = info.SnapshotTick;
        // Stay in LoadingWorld until the snapshot is actually in the game; commands must not apply to the
        // previous colony, which is still resident while the download runs.
        Log.Message($"[TurboTick] 共享殖民地已开始：epoch={info.Epoch} tick={info.Tick} world={manifest.Id}");
    }

    public static void OnTick(int tick) => Session.Barrier.Publish(tick);

    public static void OnCommands(IReadOnlyList<GameCommand> commands)
    {
        // A command is stamped at a tick every peer is still going to reach. One that arrives after this peer
        // already simulated its tick can only be applied late, and applying it late is how a single peer
        // drifts. Counting them turns that class of divergence into a number instead of a mystery.
        var applied = Session.Barrier.AppliedTick;
        foreach (var command in commands)
        {
            if (command.Tick >= applied) continue;
            lateCommands++;
            if (lateCommands == 1 || lateCommands % 100 == 0)
                Log.Warning($"[TurboTick cmd] 迟到命令：seq={command.Sequence} tick={command.Tick} 已过 {applied - command.Tick} tick"
                    + $"（累计 {lateCommands} 条）。迟到命令只能在本端边界补执行，是分叉源。");
        }
        // Diagnostic: distinguishes "the command never arrived" from "it arrived and was rejected". The
        // acceptance run showed one peer applying sequences 17..20 and the other 17..25, and those two
        // causes need completely different fixes.
        if (TraceCommands && commands.Count > 0)
        {
            var accepted = 0;
            foreach (var command in commands) if (Session.Commands.Accept(command)) accepted++;
            if (accepted != commands.Count)
                Log.Warning($"[TurboTick cmd] 收到 {commands.Count} 条，仅接受 {accepted} 条（重复或序号回退）");
            else Log.Message($"[TurboTick cmd] 收到 {commands.Count} 条 seq {commands[0].Sequence}..{commands[^1].Sequence}");
            return;
        }
        foreach (var command in commands) Session.Commands.Accept(command);
    }

    private static long lateCommands;

    private static readonly bool TraceCommands = Environment.GetCommandLineArgs()
        .Any(a => a.Equals("-turbotick-trace-commands", StringComparison.OrdinalIgnoreCase));

    /// <summary>Publishes a captured action.
    ///
    /// The failure callback matters: a captured action has already cancelled its local effect, so a command that
    /// cannot be sent would leave the player with nothing happening at all - measured as "砍伐 and 搬运 do not work"
    /// and "creating a zone reports a desync". Re-applying locally keeps the action real and makes the divergence
    /// visible instead of silently swallowing the input.
    /// </summary>
    /// <summary>Local actions not yet ordered by the coordinator, as a wall-clock deadline.
    ///
    /// The coordinator suppresses hash reports below the tick a command was stamped for, but that only starts once the
    /// command *arrives*. The origin has already applied the effect locally - a blueprint has to exist for the
    /// building designator to keep dragging - and it reports the hash for the very next tick, before the coordinator
    /// has heard about the command. Those first ticks compared a colony that already had the building against one
    /// that did not, and the room paused on it. Bounded on purpose: a lost command must not hide a real divergence
    /// for longer than the ordering lead plus one round trip.</summary>
    private static long suppressHashUntilMs = -1;

    /// <summary>Two ordering leads (8 ticks at 60 TPS is 133 ms) plus a round trip, rounded up.</summary>
    private const int HashSuppressMs = 400;

    private static long hashReportsSuppressed;

    internal static string SuppressionCounters() => "hashSuppressed=" + Interlocked.Read(ref hashReportsSuppressed);

    internal static void Send(GameCommand command)
    {
        Volatile.Write(ref suppressHashUntilMs, Environment.TickCount64 + HashSuppressMs);
        _ = RoomConnection.SendCommand(command.Kind, command.Payload, command.Sequence,
            () => ApplyLocally(command));
    }

    /// <summary>The fallback for a command that could not be delivered. It has to obey the same rule as
    /// <see cref="ApplyDue"/>: on a display replica, applying a creation command here would make an object with a
    /// local id and leave the counters offset - which is exactly the drift the frame's id check looks for.</summary>
    private static void ApplyLocally(GameCommand command)
    {
        if (AuthoritativeReplica.AcceptsCommand(command.Kind)) SharedActions.Apply(command);
    }

    /// <summary>True when the local game may run another tick. Prevents one slow machine from racing ahead.</summary>
    public static bool MayAdvanceTick() => MayAdvanceTick(out _);

    /// <summary>Same decision, but names the condition that refused. The name goes into the tick-gate audit;
    /// without it a frozen world logs "ticksThisFrame = 0" and nothing else, which is what cost a full
    /// diagnostic round on a real save (serverTick 2505, appliedTick 2499, CanAdvance true, no ticks).</summary>
    public static bool MayAdvanceTick(out string reason)
    {
        if (!HasSharedWorld) { reason = "no-shared-world"; return true; }
        // A predictive peer runs its own simulation and lets frames correct it, so the barrier must not
        // gate its tick. Only a pure display replica is frozen.
        if (AuthoritativeReplica.Predictive && AuthoritativeReplica.Enabled) { reason = "predictive"; return true; }
        // A display replica has no local simulation at all; frames carry the state instead.
        if (AuthoritativeReplica.Frozen) { reason = "frozen"; return false; }
        if (!Gating) { reason = "not-gating"; return false; }
        if (TurboState.Authority.IsHeld) { reason = "held"; return false; }
        // A pause is normal; vanilla Update handles it. No timeout may release a shared world locally.
        if (TurboState.Authority.Current.TimeSpeed == 0) { reason = "authority-paused"; return true; }
        if (Find.TickManager?.Paused == true) { reason = "paused"; return true; }
        // A gizmo or zone edit was just executed locally, before the server ordered it. Holding this peer
        // for the ordering lead keeps it from deciding anything on a world the others cannot see yet - the
        // window that produces the intermittent "工作"/"物体" divergences (C-04).
        if (HoldActive()) { reason = "hold"; return false; }
        var canAdvance = Session.Barrier.CanAdvance;
        reason = canAdvance ? "ok" : "barrier";
        return canAdvance;
    }

    /// <summary>Set only while `advance-ticks` runs, so a paused headless world can be stepped for observation.
    ///
    /// <para>Every counter channel in this project is measured by comparing two readings, and a paused world produces the
    /// same reading twice. Clearing it in a `finally` matters more than setting it: a leaked flag would let a server with
    /// no audience advance on its own, which is exactly what the hold exists to prevent.</para></summary>
    internal static bool ObservableTicks { get; set; }

    /// <summary>Deadline of the local-first hold, on the monotonic clock. It must not be measured in applied
    /// ticks: that counter only moves by ticking, so a tick-based deadline would be a deadlock.</summary>
    private static long holdUntilMs = -1;

    private static bool HoldActive()
    {
        if (holdUntilMs < 0) return false;
        if (Environment.TickCount64 < holdUntilMs) return true;
        holdUntilMs = -1;
        return false;
    }

    /// <summary>Called right after a local-first action was executed and published (gizmo click, zone cell).
    ///
    /// Only those two capture paths let vanilla run locally: every other action cancels the call and is
    /// applied at the ordered tick. Holding this peer for one ordering lead (`SimulationLeadTicks + 2` ticks,
    /// ~135 ms at 60 t/s) means the change becomes visible to everyone at the same logical tick instead of up
    /// to eight ticks later. Only the first action of a burst sets the deadline, so dragging a stockpile
    /// cannot hold the room for the length of the drag, and the deadline is wall clock so it always expires.
    ///
    /// The hold is expressed in wall-clock milliseconds on purpose, so it stays the same length of time at any
    /// speed: the lead is computed for the room's current rate rather than taken as the vanilla six ticks.
    /// </summary>
    public static void HoldAfterLocalAction()
    {
        if (!Gating || holdUntilMs >= 0) return;
        if (TurboState.Authority.Current.TimeSpeed == 0 || Find.TickManager?.Paused == true) return;
        var ticks = NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(TurboState.Authority.Current.TimeSpeed)) + 2;
        holdUntilMs = Environment.TickCount64 + (long)Math.Ceiling(ticks * 1000.0 / 60.0);
    }

    private static bool catchingUp;

    /// <summary>
    /// Runs extra ticks while behind the server. Both sides advance at roughly 60 ticks per second, so
    /// without a burst a peer that joined late would stay behind forever.
    /// </summary>
    public static void CatchUpTick()
    {
        if (AuthoritativeReplica.Frozen) return;
        if (HeadlessRuntime.HoldLocalTicks || catchingUp || !Gating || TurboState.Authority.IsHeld
            || TurboState.Authority.Current.TimeSpeed == 0 || Find.TickManager?.Paused == true
            || Find.TickManager?.slower?.ForcedNormalSpeed == true) return;
        var backlog = Session.Barrier.Backlog;
        // The published timeline deliberately runs a few ticks ahead of the slowest peer. That lead is
        // pipeline depth, not a backlog: closing it made every peer simulate about seven times faster than
        // real time, which is what "normal speed runs far too fast" was.
        //
        // The size of that lead is the room's, not a constant: at `Superfast` the server paces 36 ticks ahead so
        // that 100 ms of round trip fits, and subtracting the vanilla 6 would leave 30 ticks that look exactly like
        // a backlog - the replica would then replay them and run several times faster than the room, which is the
        // same defect this subtraction exists to prevent.
        if (RoomConnection.LockstepRoom)
            backlog = Math.Max(0, backlog - NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(TurboState.Authority.Current.TimeSpeed)));
        var steps = CatchUpPlan.Steps(backlog, CatchUpPlan.MaxPerFrameShared);
        if (steps == 0) return;
        catchingUp = true;
        var started = Clock.Now;
        try
        {
            var tickManager = Find.TickManager;
            for (var i = 0; i < steps; i++)
            {
                if (tickManager == null || Session.Phase != SharedPhase.Playing || !Session.Barrier.CanAdvance) break;
                tickManager.DoSingleTick();
                // Late-game ticks are expensive; never spend the whole frame catching up.
                if (Clock.Now - started > 0.025f) break;
            }
        }
        finally { catchingUp = false; }
    }

    internal static bool TryBeginTick(TickManager manager, out SimulationTickGuard.Permit permit)
    {
        permit = default;
        // An observation window opened by the `advance-ticks` command. A headless server with no replica holds the
        // timeline on purpose, so a scripted job stays queued and every counter channel reads its first frame forever -
        // measurement is impossible, not merely inconvenient. The flag is only ever set from a portal command, is
        // cleared in a `finally`, and does not weaken the shared barrier: the permit is still required.
        if (!Gating || TurboState.Authority.IsHeld || TurboState.Authority.Current.TimeSpeed == 0
            || (manager.Paused && !ObservableTicks)) return false;        // Same hold as MayAdvanceTick: catch-up calls DoSingleTick directly, so the gate has to be here too.
        if (HoldActive()) { PauseAudit.NoteRefusal("hold"); return false; }
        // The guard requires the game clock and the shared timeline to advance together, and they do, but
        // only from the value they shared when the session started. If those values differed, the offset is
        // constant forever: vanilla buckets long ticks by TicksGame % 2000, so the two peers run
        // Plant.TickLong on different ticks and growth, rotting and hp drift apart. Aligning here costs one
        // field write and keeps the two equal for every tick that follows.
        if (manager.TicksGame != Session.Barrier.AppliedTick) SharedGameClock.Align(Session.Barrier.AppliedTick);
        if (tickGuard.TryBegin(manager.TicksGame, out permit)) return true;
        // A refused permit is the one refusal that leaves no trace: the tick gate let the loop run, the loop
        // called DoSingleTick, and nothing advanced. Name the exact reason so a frozen world is diagnosable
        // from the log instead of from a 40-minute audit chain.
        PauseAudit.NoteRefusal(tickGuard.Busy ? "guard-busy" : Session.Barrier.CanAdvance ? "guard-clock" : "barrier");
        return false;
    }

    internal static void CompleteTick(TickManager manager, SimulationTickGuard.Permit permit)
    {
        if (!tickGuard.Complete(permit, manager.TicksGame))
        {
            Session.Fault("实际游戏 tick 与共享时间线不一致；模拟已挂起");
            return;
        }
        OnLocalTick();
        HeadlessRuntime.ReportProgress();
    }

    internal static void SkipTick() => tickGuard.Cancel();

    /// <summary>Diagnostic: a permit that never completed would reject every later tick.</summary>
    internal static bool TickExecuting => tickGuard.Busy;

    internal static void CancelTick(Exception error)
    {
        tickGuard.Cancel();
        Session.Fault("共享模拟 tick 异常，已挂起：" + error.Message);
    }

    /// <summary>After a permitted engine tick has completed and the barrier committed exactly once.</summary>
    public static void OnLocalTick()
    {
        if (!Gating) return;
        ApplyDue();
        // Frames are published from the tick, not from the render loop. The headless loop runs at a few
        // hertz, so driving publication from it delivered about five frames per second while the colony
        // advanced sixty ticks, which the client could only show as move-pause-move.
        if (HeadlessRuntime.Active) HeadlessRuntime.PublishFrame();
        // Lockstep keeps the server's timeline behind the slowest simulator, which only works if every
        // peer says how far it got.
        if (RoomConnection.LockstepRoom) RoomConnection.ReportProgress(Session.Barrier.AppliedTick);
        if (Session.Barrier.AppliedTick % HashEvery == 0) ReportHash();
        if (RoomConnection.IsCheckpointOwner && !HeadlessRuntime.StopRequested
            && Session.Barrier.AppliedTick - lastCheckpointTick >= CheckpointInterval)
        {
            lastCheckpointTick = Session.Barrier.AppliedTick;
            RoomConnection.PublishCheckpoint(lastCheckpointTick);
        }
    }

    /// <summary>Periodic canonical hash so the server can prove all peers still simulate the same colony.
    /// In an authoritative room exactly one process simulates, so there is nothing to compare against and
    /// the traversal is skipped: it walks every pawn, thing and designation on the main thread and showed
    /// up in game as the simulation stalling roughly once per second.</summary>
    private static int lastHashedTick = -1;
    private static bool reportingArmed;
    // Diagnostic override: a divergence between two samples is otherwise reported only as a bucket.
    private static readonly int HashEvery = Math.Max(1,
        int.TryParse(Environment.GetEnvironmentVariable("TURBOTICK_HASH_EVERY"), out var every) ? every : 60);
    // With per-tick sampling the always-on probe is pawn-only; this window forces the full colony so a
    // divergence caused by things can be seen at the exact tick it starts.
    private static readonly int FullFrom = int.TryParse(Environment.GetEnvironmentVariable("TURBOTICK_HASH_FULL_FROM"), out var from) ? from : -1;
    private static readonly int FullTo = int.TryParse(Environment.GetEnvironmentVariable("TURBOTICK_HASH_FULL_TO"), out var to) ? to : -1;
    // Instruments that only emit when something goes wrong cannot be validated by a run where nothing does: a
    // clean run and a broken instrument that always returns the same value look identical. These two make the
    // probe self-attesting.
    //   TURBOTICK_PROBE_LOG_EVERY=600   write the stage/draw snapshot to the log every N ticks
    //   TURBOTICK_PROBE_DUMP_AT=600,1200  write a divergence dump at these exact ticks regardless of divergence
    private static readonly int ProbeLogEvery = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_LOG_EVERY"), out var probeEvery) ? probeEvery : 0;
    private static readonly HashSet<int> ProbeDumpAt = new(
        (Environment.GetEnvironmentVariable("TURBOTICK_PROBE_DUMP_AT") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var value) ? value : -1)
            .Where(value => value >= 0));

    private static void ReportHash()
    {
        var tick = Session.Barrier.AppliedTick;
        // One report per tick: a paused or throttled tick would otherwise be re-reported with drifted state
        // and look like a desync to the server.
        // Only report once this peer actually runs the shared colony; a peer still sitting in its own
        // quicktest colony would otherwise publish unrelated hashes and look like a desync.
        if (!reportingArmed || tick <= lastHashedTick) return;
        // A tick whose effect this peer applied before the coordinator ordered it is not comparable yet. See the
        // comment on `suppressHashUntilMs`.
        if (Environment.TickCount64 < Volatile.Read(ref suppressHashUntilMs))
        {
            Interlocked.Increment(ref hashReportsSuppressed);
            return;
        }
        // A display replica never simulated that tick, so its hash would only look like a desync.
        if (AuthoritativeReplica.Frozen) return;
        // Only one simulator exists in an authoritative room, so a canonical hash has nothing to compare
        // against and must not pay for a full colony traversal on the shared timeline.
        if (RoomConnection.AuthoritativeRoom) return;
        lastHashedTick = tick;
        try
        {
            if (HashEvery == 1 && !(tick >= FullFrom && tick <= FullTo))
            {
                // Cheap every-tick probe used only while localising a divergence.
                var pawnOnly = CollectPawns();
                var pawnHash = StateHash.Compute(pawnOnly);
                // Keep the ring on this path too. It is the mode used to localise a divergence, and a dump
                // that cannot answer for the tick that diverged wastes the run.
                try
                {
                    // The ring carries the state plus the traces, so a dump can be read without re-running: the
                    // hash above covers `pawnOnly` only.
                    var dumpRows = new List<string>(pawnOnly);
                    dumpRows.AddRange(CollectTraces());
                    DivergenceDump.RememberPawns(tick, dumpRows,
                        Find.TickManager?.TicksGame ?? -1, SimulationRandom.Capture());
                }
                catch (Exception) { }
                // Self-attesting output: without this, "no divergence" and "the probe is broken and returns one
                // constant on both peers" produce the same evidence.
                if (ProbeLogEvery > 0 && tick % ProbeLogEvery == 0)
                    Log.Message($"[TurboTick probe] tick={tick} {TickStages.Describe()} "
                        + $"iter={SimulationRandom.Iterations()} pawns={pawnOnly.Count(r => r.StartsWith("p:", StringComparison.Ordinal))} "
                        + pawnOnly.FirstOrDefault(r => r.StartsWith("shared=", StringComparison.Ordinal))
                        // Issued vs written vs arrived is the three-way split that located the field failure:
                        // `pubgate sent` only proves the call was made, the write completes later on a worker.
                        + " " + PublishGate.Counters());
                if (ProbeDumpAt.Contains(tick))
                    DivergenceDump.Write(tick, "手动触发（TURBOTICK_PROBE_DUMP_AT）");
                // One category, and the coordinator recognises a single-category sample as the per-tick probe and
                // names it accordingly. Reporting a single slot used to be labelled "位置" whatever had actually
                // differed, and reporting the same hash in every slot was worse - it claimed eight categories
                // disagreed. The probe compares one combined projection, so it says so.
                SimulationDiagnostics.Hash(tick, pawnHash, [pawnHash], pawnOnly);
                RoomConnection.TrackReport(RoomConnection.ReportHash(tick, pawnHash, [pawnHash]));
                return;
            }
            var (entries, categories, pawns) = DescribeColony();
            var hash = StateHash.Compute(entries);
            // Keep this tick's projection so a divergence reported a tick or two later is answered with the
            // state that was actually hashed, not with whatever the world looks like when the message lands.
            try
            {
                var (rows, counts) = DivergenceDump.Build();
                DivergenceDump.Remember(tick, rows, counts,
                    Find.TickManager?.TicksGame ?? -1, SimulationRandom.Capture());
            }
            catch (Exception dumpError)
            {
                Log.Warning("[TurboTick] 分叉快照缓存失败：" + dumpError.Message);
            }
            SimulationDiagnostics.Hash(tick, hash, categories, HashEvery == 1 ? pawns : null);
            RoomConnection.TrackReport(RoomConnection.ReportHash(tick, hash, categories));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 状态哈希计算失败：" + error.Message); }
    }

    /// <summary>The job name used for the shared hash.
    ///
    /// Two vanilla placeholder jobs describe the same thing - "standing here doing nothing for a moment" - and
    /// which one a pawn holds depends on whether the previous job ended with a stopped pather
    /// (<c>Verse.AI/Pawn_JobTracker.cs:461-472</c>) or on the wander parity flip
    /// (<c>Verse.AI/JobGiver_Wander.cs:48-58</c>). Both are replaced within a tick or two, so comparing them
    /// reports a phase difference that the game itself resolves. Measured: at tick 21600 two peers agreed on
    /// position, `nextCell`, `pather.Moving` and the wander parity, and differed only in this def - one holding
    /// Wait_MaintainPosture, the other Wait_Wander.
    ///
    /// Normalising them to one name keeps every real job difference (work, haul, eating, drafting) intact while
    /// removing a false red that no amount of simulation work can fix.</summary>
    private static string NormalizeIdleJob(Pawn pawn)
    {
        var def = pawn.CurJobDef?.defName ?? "";
        return def is "Wait_MaintainPosture" or "Wait_Wander" or "Wait" or "Wait_Downed" ? "Wait" : def;
    }

    /// <summary>Everything about a pawn's current decision that both projections must agree on.
    ///
    /// One function, called from the per-tick probe and from the full colony hash, because the two drifting apart
    /// produced a false divergence that cost a whole round: the probe hashed the raw <c>defName</c> while the full
    /// hash used <see cref="NormalizeIdleJob"/>, so `Wait_MaintainPosture` against `Wait_Wander` - a pair the
    /// normaliser exists to erase - was reported by one projection and not the other at the same tick.
    ///
    /// The parts, and why each is here:
    /// <list type="bullet">
    /// <item>the normalised job name: the actual decision;</item>
    /// <item>the wander parity: <c>JobGiver_Wander</c> flips it every pass and branches on it, so a peer that ran
    /// one extra pass keeps the opposite value forever;</item>
    /// <item>the tick of the last think pass: the parity alone says "they differ", this says when;</item>
    /// <item>the job's target cell: the wander destination comes from
    /// <c>regions.RandomElementByWeight(...).RandomCell</c>, three draws, so two peers can already aim at
    /// different cells while the job name and parity still agree - which used to stay invisible for ~50 ticks
    /// until the pawns walked far enough apart to trip the position category.</item>
    /// </list>
    /// </summary>
    private static string DescribePawnJob(Pawn pawn)
    {
        var job = pawn.CurJob;
        var target = job?.targetA;
        return NormalizeIdleJob(pawn)
            + ":" + (pawn.mindState?.nextMoveOrderIsWait == true ? 1 : 0)
            + ":" + ThinkPassAudit.Describe(pawn.thingIDNumber)
            + ":" + (target is { IsValid: true } t ? t.Cell.x + "," + t.Cell.z : "-")
            // The expiry interval and the tick the sparse grid will actually fire on. A common "wait" job with
            // one peer thinking again and the other not is either a different interval (draws went elsewhere) or
            // the same interval firing on different ticks, and those two have nothing in common.
            + ":" + (job?.expiryInterval ?? -1)
            + ":" + NextExpiryTick(pawn, job)
            // Faction membership, because it decides how many peers a social roll shuffles over. Measured: one
            // pawn drew 18 values on one peer and 16 on the other with every other field identical, and
            // `Pawn_InteractionsTracker.TryInteractRandomly` shuffles `SpawnedPawnsInFaction(pawn.Faction)`, which
            // costs `count - 1` draws. A faction that differs by two pawns explains the whole gap, and faction was
            // not in the projection.
            + ":" + (pawn.Faction?.def?.defName ?? "None")
            + ":" + (pawn.HostFaction?.def?.defName ?? "-")
            // Hunger decides whether the food giver runs at all, so it is the first input to inspect when one peer
            // picks Ingest and the other picks a work job. Formatted with the invariant culture: a locale-aware
            // decimal separator would make every tick differ on a German or French client.
            + ":" + (pawn.needs?.food is { } food
                ? food.CurLevel.ToString("0.####", CultureInfo.InvariantCulture) : "na")
            // The giver that produced the job: two peers holding different jobs says nothing until you know
            // whether they came from different givers or from the same giver picking differently.
            + ":" + ThinkPassAudit.Source(pawn.thingIDNumber)
            // The herd centre a WanderHerd decision used, and the lord that owns the pawn. Measured need: one
            // wander target differed by two cells with every random value identical, so the difference had to
            // be an input - and neither of these was in the projection.
            + ":" + HerdProbe.Root(pawn.thingIDNumber)
            + ":" + HerdProbe.Lord(pawn);
    }

    /// <summary>The first tick at or after <c>startTick + expiryInterval</c> that satisfies the hash grid test
    /// <c>(TicksGame + thingIDNumber.HashOffset()) % expiryInterval == 0</c>
    /// (<c>Verse.AI/Pawn_JobTracker.cs:166-169</c> with delta pinned to 1). The sum alone is only a lower bound:
    /// the job cannot end before it, and may end up to <c>expiryInterval - 1</c> ticks after it.</summary>
    internal static int NextExpiryTick(Pawn pawn, Verse.AI.Job? job)
    {
        if (job is null || job.expiryInterval <= 0) return -1;
        var interval = job.expiryInterval;
        var from = job.startTick + interval;
        var offset = pawn.thingIDNumber.HashOffset();
        var remainder = ((from + offset) % interval + interval) % interval;
        return remainder == 0 ? from : from + (interval - remainder);
    }

    /// <summary>Pawn-only projection, cheap enough to sample every tick while hunting a divergence.</summary>
    internal static List<string> CollectPawns()
    {
        var pawns = new List<string>();
        // `uniqueID`, not `Index`, everywhere in this file. Every string built here is compared between two processes that
        // each derived it locally, so a field that is only equal by coincidence is worse than useless: `Map.Index` is
        // `Find.Maps.IndexOf(this)` (`:76452`), so two peers whose map lists are ordered differently - or one of which
        // removed a map - hash the same world to different values and report a desync that is not there. `uniqueID`
        // (`:76213`) is scribed (`:76818`) and therefore identical on both ends by construction.
        // The map list itself goes in first. Every field below is derived from `Find.Maps`, so two peers with different
        // map sets produce hashes that differ for an unstated reason - and a peer with one map simply has fewer entries,
        // which the comparison reads as "no divergence in the maps it does have" rather than as a missing map. One
        // short string makes the map set itself observable.
        {
            var ids = new List<string>();
            foreach (var map in Find.Maps)
                if (map is not null)
                    ids.Add(map.uniqueID.ToString(CultureInfo.InvariantCulture) + "x"
                        + map.Size.x.ToString(CultureInfo.InvariantCulture) + "z"
                        + map.Size.z.ToString(CultureInfo.InvariantCulture));
            ids.Sort(StringComparer.Ordinal);
            pawns.Add("maps=" + string.Join(",", ids));
        }
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                pawns.Add(StateHash.Pawn(pawn.thingIDNumber, map.uniqueID, pawn.Position.x, pawn.Position.z,
                    pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f, DescribePawnJob(pawn)));
        // The draw counter belongs in this projection. Without it the probe can only report "a pawn differs",
        // and the chain from "some draw went missing" to "this pawn's interval is 128 instead of 209" has to be
        // reconstructed by hand. With it, the first tick on which the two streams are at different positions is
        // reported by the probe itself, at the tick it happens - and that tick is where to look for the extra
        // consumer.
        pawns.Add("iter=" + SimulationRandom.Iterations().ToString(CultureInfo.InvariantCulture));
        // Where inside the tick the draws happened. When the draw counts differ but every pawn matches, the stage
        // is the only way to tell which subsystem is responsible.
        pawns.Add(TickStages.Describe());
        // Only the components of MapPostTick, which the staged snapshot narrowed the remaining difference to.
        pawns.Add(MapPostStages.Describe());
        // Per-faction counts belong in the probe too. Measured need: with `pf:` only in the full colony hash, a
        // run in the per-tick mode produced no `pf:` lines at all, so the most sensitive signal for the remaining
        // divergence was unavailable exactly in the mode used to localise it.
        foreach (var map in Find.Maps)
        {
            if (map is null || map.Disposed) continue;
            pawns.AddRange(FactionEntries(map));
        }
        // Shared structures the failing searches walk: haulable/faction/forbidden counts and their digests. These
        // are state, so they belong in the hash. An earlier version also hashed the diagnostic rings below, and
        // that turned a trace difference into a reported state divergence - the instrument reporting itself.
        pawns.Add(SharedStructureProbe.Describe());
        // Region graph size and dirty-region count: tells "the graph differs" apart from "the timing differs".
        foreach (var map in Find.Maps)
        {
            if (map is null || map.Disposed) continue;
            pawns.Add(ReachProbe.Describe(map));
        }
        return pawns;
    }

    /// <summary>Diagnostic rows that are written to a dump but deliberately kept out of the hash.
    ///
    /// Why this separation exists: these rings record *events* (which thing drew, which social attempt ran, when a
    /// forbidden flag flipped), and their contents depend on how much history fits in a fixed-size buffer. Two peers
    /// can hold entirely different buffers - measured: 62 entries against 60, with an empty intersection - while
    /// their state is identical. Hashing that reported a divergence on every tick while the simulation agreed.
    ///
    /// So a trace is a lens, not a fact. When a phase difference needs to be judged, hash the *state* it describes
    /// (which `shared=` already does: a sorted digest of the forbidden set) rather than the event log that produced
    /// it. Diagnostics still travel in the dump, where a human can read them next to the state they explain.
    /// </summary>
    internal static List<string> CollectTraces()
    {
        var traces = new List<string>();
        if (!ThingDrawTrace.Enabled) return traces;
        traces.AddRange(ThingDrawTrace.Entries());
        traces.AddRange(PawnSubsystemTrace.Entries());
        traces.AddRange(SocialAttemptTrace.Entries());
        traces.AddRange(FoodProbe.Entries());
        traces.AddRange(HaulProbe.Entries());
        traces.AddRange(ForbidTrace.Entries());
        traces.AddRange(DirectForbidGate.Entries());
        traces.AddRange(ZoneGate.Entries());
        traces.AddRange(SendGate.Entries());
        traces.AddRange(SendFailureGate.Entries());
        traces.AddRange(PublishGate.Entries());
        traces.AddRange(ZoneCellProbe.Entries());
        traces.AddRange(SocketSendGate.Entries());
        return traces;
    }

    /// <summary>The inputs of the wild-plant species choice that are not the map's things.
    ///
    /// <c>PlantChoiceWeight</c> (<c>RimWorld/WildPlantSpawner.cs:469-516</c>) multiplies together the biome
    /// commonality, a factor derived from the whole-map count of that species, the sum of all commonalities, and
    /// a cluster term. A peer that computed any of those from a different list order - <c>AllWildPlants</c> and
    /// <c>MutatorWildPlants</c> are cached lists built from <c>DefDatabase</c> and tile mutators - gets a
    /// different float and then picks a different species from the <em>same</em> random draw. That is exactly the
    /// signature that survived every other fix: identical rng, one plant, two defs.
    /// </summary>
    internal static List<string> EcologyEntries(Map map)
    {
        var entries = new List<string>();
        var spawner = map.wildPlantSpawner;
        if (spawner is null) return entries;
        try
        {
            var cached = typeof(WildPlantSpawner).GetField("cachedPlantsCommonalitiesSum",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var cachedCave = typeof(WildPlantSpawner).GetField("cachedCavePlantsCommonalitiesSum",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            entries.Add($"ec:sum:{map.uniqueID}:{Float(spawner.PlantsCommonalitiesSum)}:{Float(spawner.CavePlantsCommonalitiesSum)}");
            entries.Add($"ec:cached:{map.uniqueID}:{CachedFloat(cached, spawner)}:{CachedFloat(cachedCave, spawner)}");
            // Species counts in a fixed order: the count itself feeds back into the next choice.
            var counts = new List<(string Def, int Count)>();
            foreach (var pair in map.listerThings.AllThings
                .Where(t => t.def.category == ThingCategory.Plant && !t.def.IsDeadPlant)
                .GroupBy(t => t.def.defName, StringComparer.Ordinal))
                counts.Add((pair.Key, pair.Count()));
            counts.Sort((x, y) => string.CompareOrdinal(x.Def, y.Def));
            foreach (var (def, count) in counts)
                entries.Add($"ec:n:{map.uniqueID}:{def}:{count.ToString(CultureInfo.InvariantCulture)}");
            // The order the spawner would walk: if this differs, the sum above can differ while the counts match.
            var order = new List<string>();
            try
            {
                foreach (var plant in map.BiomeAt(IntVec3.Zero).AllWildPlants) order.Add(plant.defName);
                foreach (var plant in spawner.MutatorWildPlants) order.Add("m:" + plant.defName);
            }
            catch (Exception) { }
            if (order.Count > 0) entries.Add($"ec:order:{map.uniqueID}:{string.Join(",", order)}");
        }
        catch (Exception error)
        {
            entries.Add($"ec:error:{map.uniqueID}:{error.GetType().Name}");
        }
        return entries;
    }

    /// <summary>A cached <c>float?</c> from the spawner, or "none" while it has not been computed yet. Whether
    /// the cache is warm is itself part of the divergence: it must be warm on both peers at the same tick.</summary>
    private static string CachedFloat(System.Reflection.FieldInfo? field, WildPlantSpawner spawner)
    {
        try
        {
            var value = field?.GetValue(spawner);
            return value is null ? "none" : Float(Convert.ToSingle(value));
        }
        catch (Exception) { return "error"; }
    }

    /// <summary>Pawn counts per faction, which `SpawnedPawnsInFaction` turns into a shuffle size.
    ///
    /// <c>Pawn_InteractionsTracker.TryInteractRandomly</c> shuffles that list and a shuffle costs <c>count - 1</c>
    /// draws, so a faction that holds two more pawns on one peer makes the next social roll draw two more values.
    /// Measured: one pawn drew 18 in `social` on one peer and 16 on the other with every other field identical.
    /// These counts make that visible at the tick it happens instead of at the social roll it later poisons.
    ///
    /// Both projections emit them: the per-tick probe is the mode used to localise, and putting this only in the
    /// full hash meant the reading never appeared in the runs that needed it.
    /// </summary>
    internal static List<string> FactionEntries(Map map)
    {
        var entries = new List<string>();
        try
        {
            var byFaction = new List<(string Faction, int Count)>();
            foreach (var group in map.mapPawns.AllPawnsSpawned
                .GroupBy(p => p.Faction?.def?.defName ?? "None", StringComparer.Ordinal))
                byFaction.Add((group.Key, group.Count()));
            byFaction.Sort((a, b) => string.CompareOrdinal(a.Faction, b.Faction));
            foreach (var (faction, count) in byFaction)
                entries.Add($"pf:{map.uniqueID}:{faction}={count.ToString(CultureInfo.InvariantCulture)}");
            // The cached list the interaction tracker actually reads, which can disagree with the live count if
            // MapPawns did not invalidate it. Reporting both is what tells those two cases apart.
            entries.Add($"pfc:{map.uniqueID}:"
                + map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error)
        {
            entries.Add($"pf:error:{error.GetType().Name}");
        }
        return entries;
    }

    private static string Float(float value) =>
        BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>Splits the colony into categories so a divergence can name what actually differs.</summary>
    internal static (List<string> All, List<string> Categories, List<string> Pawns) DescribeColony()
    {
        var pawns = new List<string>();
        var things = new List<string>();
        var designations = new List<string>();
        // Positions, health and job are hashed separately. A single "colonists" bucket only said that one
        // of the three differed, which is not enough to act on: a position divergence means AI or pathing,
        // health means damage or needs, and job means the decision itself. The categories are compared by
        // name on the server, so a finer split costs nothing and removes a round of guessing.
        var pawnPositions = new List<string>();
        var pawnHealth = new List<string>();
        var pawnJobs = new List<string>();
        var ecology = new List<string>();
        var order = new List<string>();
        foreach (var map in Find.Maps)
        {
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                var id = pawn.thingIDNumber;
                var health = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f;
                var job = NormalizeIdleJob(pawn);
                pawns.Add(StateHash.Pawn(id, map.uniqueID, pawn.Position.x, pawn.Position.z, health, job));
                pawnPositions.Add($"pp:{id}:{map.uniqueID}:{pawn.Position.x}:{pawn.Position.z}");
                pawnHealth.Add($"ph:{id}:{health.ToString("0.##", CultureInfo.InvariantCulture)}");
                // The wander flip is part of the job decision, not presentation: JobGiver_Wander toggles it on
                // every pass and then chooses Wait_Wander or GotoWander from it
                // (Verse.AI/JobGiver_Wander.cs:48-58). A peer that runs one extra think-tree pass keeps the
                // opposite parity from then on, which surfaced as "same rng, same cell, different job" ~200
                // ticks after the fact. Hashing it makes the divergence appear at the pass that caused it.
                // The think-pass tick rides along: the parity bit alone says "they differ", the tick says when,
                // and the pass is the only thing that can flip it.
                // Same description the per-tick probe uses, so the two projections cannot disagree about a pawn.
                pawnJobs.Add($"pj:{id}:{DescribePawnJob(pawn)}");
            }
            foreach (var thing in map.listerThings.AllThings)
                if (thing.def.category is not (ThingCategory.Pawn or ThingCategory.Mote)
                    && (thing.def.category != ThingCategory.Ethereal || thing is Blueprint))
                    things.Add(StateHash.Thing(thing.thingIDNumber, map.uniqueID, thing.Position.x, thing.Position.z,
                        thing.stackCount, thing.HitPoints));
            foreach (var designation in map.designationManager.AllDesignations)
                designations.Add($"d:{designation.def.defName}:{map.uniqueID}:{designation.target.Cell.x}:{designation.target.Cell.z}");
            ecology.AddRange(EcologyEntries(map));
            ecology.AddRange(SharedEcologyProbe.Entries());
            ecology.AddRange(FactionEntries(map));
            // Tick order is shared state: the same draws handed out in a different order land on different
            // things, which is invisible to every other category.
            order.AddRange(SharedTickOrder.Describe());
        }
        var all = new List<string>(pawns.Count + things.Count + designations.Count);
        all.AddRange(pawns);
        all.AddRange(things);
        all.AddRange(designations);
        // The order must match StateCategories.Names, which the coordinator uses to label the difference.
        return (all,
        [
            StateHash.Compute(pawnPositions),
            StateHash.Compute(pawnHealth),
            StateHash.Compute(pawnJobs),
            StateHash.Compute(things),
            StateHash.Compute(designations),
            // Wild-plant regrowth picks a species by weighted draw from `WILDPLANT.PlantChoiceWeight`, and that
            // weight reads whole-map state: per-species counts (`ThingsOfDef(def).Count`), the sum of biome
            // commonalities, and the mutator plant list order. Comparing those as their own category says which
            // one moved, instead of inferring it from a single spawned plant a hash interval later.
            StateHash.Compute(ecology),
            StateHash.Compute(order),
            // The shared random stream is part of the state, not an implementation detail. Pawns that differ
            // only in position and job mean the AI took a different branch, and the first thing to rule out
            // is that the two peers are drawing from different streams. Reporting it as its own category
            // answers that in the divergence line instead of after another round of guessing.
            //
            // The wild-plant scan phase rides in the same category: it consumes up to two Rand draws per cell,
            // so a peer that ran one extra scan shows up here first, and the reported category then says
            // "随机" instead of "位置" - which is the difference between looking at the RNG and looking at AI.
            // The seed and the draw count are hashed separately: an identical seed with a different count means
            // one peer consumed extra draws rather than the two streams diverging, and those are different bugs.
            StateHash.Compute(
            [
                $"rng:{SimulationRandom.Capture():x16}",
                $"iter:{SimulationRandom.Iterations()}",
                .. EcologyPhases(),
            ]),
        ], pawns);
    }

    private static float lastRecoveryAt = -999f;
    private static string pendingDesync = "";
    /// <summary>Per-map wild-plant scan phase. Cheap (one small string per map) and part of the "随机"
    /// category because that scan draws from the shared RNG.</summary>
    private static System.Reflection.FieldInfo? ecologyCycleIndex;
    private static List<string> EcologyPhases()
    {
        var phases = new List<string>();
        var maps = Find.Maps;
        for (var i = 0; i < maps.Count; i++)
        {
            var spawner = maps[i]?.wildPlantSpawner;
            if (spawner is null) continue;
            try
            {
                ecologyCycleIndex ??= typeof(WildPlantSpawner).GetField("cycleIndex",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                phases.Add($"w:{maps[i].Index}:{ecologyCycleIndex?.GetValue(spawner) ?? -1}");
            }
            catch (Exception) { }
        }
        return phases;
    }
    /// <summary>Extra sentence for the desync dialog when this peer could not pause the room itself.</summary>
    internal static string pendingDesyncNote = "";

    public static void OnDesync(int tick, string detail)
    {
        Log.Error($"[TurboTick] 检测到状态分叉（tick {tick}，类别 {detail}）。");
        SimulationDiagnostics.Divergence(tick, detail);
        // Always on, and cheap until it fires: one line per entity so the two peers' files can be diffed and
        // the first differing thing named. The opt-in journal above needs a launch flag and reached 347 MB in
        // 13 minutes, so the run that actually diverged was the one without it.
        DivergenceDump.Write(tick, detail);
        pendingDesync = $"状态分叉：tick {tick}（{detail}）与其他玩家不一致";
        var settings = TurboTickMod.Settings;
        // Pausing first is the whole point: a silent rewind loses everything since the checkpoint and hides
        // which field actually differed. With the world still on screen the players can see the divergence
        // and choose. MU pauses on desync for the same reasons.
        //
        // The session deliberately stays in Playing here. Fault() moves it to Faulted, and Faulted is not an
        // active phase: AcceptsInput turns false, so "stay here for now" would leave the player locked out
        // of a world that keeps running. Only the recovery path faults the session.
        if (settings is null || settings.pauseOnDesync)
        {
            // Only the controller owns the room's speed. A non-controller writing the local field has it
            // reverted by NativeSpeedSync.ApplyConfirmed on the next frame, so the player sees a pause button
            // that does nothing; ask the room and say so when this peer may not.
            if (!NativeSpeedSync.RequestRoomPause())
            {
                pendingDesyncNote = "（本端无控速权限，未暂停；由控制者决定是否暂停）";
                Log.Warning("[TurboTick] 分叉后无法暂停房间：本端不是控制者。");
            }
            // The server reports every divergent tick; without this the dialog would stack once a second.
            if (Clock.Now - lastRecoveryAt < 60f) return;
            lastRecoveryAt = Clock.Now;
            ShowDesyncDialog(tick, detail);
            return;
        }
        Session.Fault(pendingDesync);
        if (settings is null || !settings.autoRecover) return;
        // Reuse the join path: reconnect, reload the checkpoint and replay the delta. Rate-limited so a
        // persistent divergence cannot become a reconnect storm.
        if (Clock.Now - lastRecoveryAt < 60f) return;
        lastRecoveryAt = Clock.Now;
        Log.Message("[TurboTick] 自动恢复：重新连接并从检查点追赶。");
        RoomConnection.ReconnectFromFault();
    }

    private static void ShowDesyncDialog(int tick, string detail)
    {
        try
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                TurboTickSettings.T("TurboTick_DesyncBody", tick, detail) + pendingDesyncNote,
                TurboTickSettings.T("TurboTick_DesyncReload"), () =>
                {
                    if (Clock.Now - lastRecoveryAt < 60f) return;
                    lastRecoveryAt = Clock.Now;
                    Log.Message("[TurboTick] 手动恢复：重新连接并从检查点追赶。");
                    // Leaving Playing here is what makes the reload safe: the old colony stops accepting
                    // input and command intake is dropped before the replacement world arrives.
                    Session.Fault(pendingDesync);
                    RoomConnection.ReconnectFromFault();
                },
                TurboTickSettings.T("TurboTick_DesyncStay"), null,
                TurboTickSettings.T("TurboTick_DesyncTitle")));
        }
        catch (Exception error) { Log.Warning("[TurboTick] 分叉提示窗失败：" + error.Message); }
    }

    private static bool loadPending;
    private static float loadStartedAt;

    public static bool LoadPending => loadPending;
    internal static bool IsExpectedWorldLoad(string? saveName) => loadPending
        && Session.Phase == SharedPhase.Synchronizing && saveName is not null
        && string.Equals(saveName, expectedSaveName, StringComparison.Ordinal);

    private static string? expectedSaveName;

    public static void HoldForPublication()
    {
        reportingArmed = false;
        loadPending = false;
        expectedSaveName = null;
        tickGuard.Cancel();
        Session.BeginLoading(Session.Epoch, "publishing");
        SimulationDiagnostics.Event("publication-held");
    }

    public static void BeginWorldLoad(string saveName)
    {
        loadPending = true;
        expectedSaveName = saveName;
        loadStartedAt = Clock.Now;
        // The long event is about to start; repeat the sentinel so a reload that began without a fresh
        // Started message still tells the server not to pace from this peer.
        RoomConnection.ReportProgress(-1);
        Session.WorldLoaded();
        SimulationDiagnostics.Event("load-requested");
    }

    /// <summary>Only the final deserialized Game receives LoadedGame. Its queued callback runs after
    /// post-load fixups and the vanilla pause-on-load callback, while our tick guard still holds simulation.</summary>
    internal static void ScheduleWorldLoaded(Game owner, string? actualSaveName, ulong? savedRandomState, bool stableSnapshotIds)
    {
        if (!loadPending || !string.Equals(actualSaveName, expectedSaveName, StringComparison.Ordinal)) return;
        var generation = Session.LoadGeneration;
        var world = Session.WorldId;
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (!loadPending || !ReferenceEquals(Current.Game, owner)
                || Current.ProgramState != ProgramState.Playing || Scribe.mode != LoadSaveMode.Inactive
                || Session.WorldId != world || !string.Equals(actualSaveName, expectedSaveName, StringComparison.Ordinal)
                || generation != Session.LoadGeneration || Session.Phase != SharedPhase.Synchronizing) return;
            if (!stableSnapshotIds)
            {
                OnWorldLoadFailed("共享快照未保留稳定对象 ID，请由控制者重新发布殖民地");
                return;
            }
            if (savedRandomState is not { } randomState)
            {
                OnWorldLoadFailed("共享快照缺少完整随机状态，请重新发布殖民地");
                return;
            }
            try { TurboEnvironmentState.RestoreAll(Find.Maps, Find.TickManager.TicksGame); }
            catch (Exception error) { OnWorldLoadFailed(error.Message); return; }
            SimulationRandom.Restore(randomState);
            // Warm the life-stage cache for every pawn in a fixed order before this world ticks. The load
            // invalidates it, and whichever peer reads it first decides when its effects land - see
            // AgeTrackerWarmup for the measurement that pointed at this.
            AgeTrackerWarmup.Run();
            // Same class of problem in the ecology scan: a mid-cycle snapshot plus a flag that is scribed with
            // defaultValue:true leaves the plant-choice weights depending on the phase the host happened to be
            // in. Measured symptom: identical RNG, one plant, two different defs.
            SharedEcologyWarmup.Run();

            // The ring holds projections from the world that was just replaced; a report about the old world
            // must not be answered with them.
            DivergenceDump.Forget();
            SharedEcologyProbe.Reset();
            ThinkPassAudit.Reset();
            // Every channel's "what I already sent" belongs to the world that was just replaced. Without this a channel
            // omits its whole baseline on the first frame of the new world - the replica keeps the previous colony's
            // buildings, doors, zones, snow, plans, research and quests - and the change-only rows never send one at all.
            // Twenty-one channels carried a `Reset` for this and only two were ever called.
            HeadlessRuntime.ResetChannels();
            try { SharedPathWarmup.CompleteLoadedRequests(Find.Maps); }
            catch (Exception error) { OnWorldLoadFailed(error.Message); return; }
            if (!Session.TryFinishLoading(generation)) return;
            // One absolute clock, not just one rate: vanilla buckets long ticks by TicksGame % 2000, so an
            // offset between peers runs Plant.TickLong on different ticks and growth diverges.
            SharedGameClock.Align(Session.Barrier.AppliedTick);
            LoadedAtTick = Session.Barrier.AppliedTick;
            loadPending = false;
            expectedSaveName = null;
            reportingArmed = true;
            lastHashedTick = -1;
            NativeSpeedSync.ApplyConfirmed();
            SetLoadedStatus();
            SimulationDiagnostics.Event("world-ready");
            // Loading has finished and the game has released the file, so the transport copies can be
            // removed now. Doing this at LoadGame time instead left them in place: the load is a long event,
            // the file is still open, and the delete failed silently - which is how three opaque saves
            // reappeared after the previous cleanup.
            WorldFiles.PruneSaves();
            WorldFiles.PruneCache();
        });
    }

    private static void SetLoadedStatus()
    {
        // The HUD reads RoomConnection.Status, so loading completion has to reach it: setting only the log
        // left a peer that was already ticking still showing "正在载入共享存档".
        RoomConnection.ReportStatus(TurboTickSettings.T("TurboTick_HudSynced", Session.Barrier.AppliedTick));
        Log.Message("[TurboTick] 共享殖民地已载入并开始");
    }

    /// <summary>The tick the current world finished loading on. A checkpoint published at this tick would
    /// capture a world that has simulated nothing since it was deserialised, while the publisher claims the
    /// current timeline tick - two peers then publish "the same tick" with different contents (measured:
    /// same ticksGame, 6 vs 71 pawns with a pending life-stage change). Publishing is therefore refused until
    /// at least one tick has run.</summary>
    internal static int LoadedAtTick { get; private set; } = -1;

    public static void OnWorldLoadFailed(string reason)
    {
        loadPending = false;
        reportingArmed = false;
        expectedSaveName = null;
        tickGuard.Cancel();
        Session.Fault("载入共享存档失败：" + reason);
        // The HUD reads RoomConnection.Status, so a failed load has to reach it too; otherwise the player
        // keeps reading "正在载入共享存档" on a session that already faulted.
        RoomConnection.ReportStatus(TurboTickSettings.T("TurboTick_SharedFault", reason));
        Log.Error("[TurboTick] " + reason);
    }

    /// <summary>Applies commands whose tick has arrived. Also used while paused so late commands still land.</summary>
    public static void ApplyDue()
    {
        // Never apply to the previous colony while a replacement snapshot is still loading.
        if (Session.Phase != SharedPhase.Playing) return;
        var due = Session.Commands.TakeThrough(Session.Barrier.AppliedTick);
        if (due.Count == 0) return;
        using var random = SimulationRandom.Enter();
        // Every peer applies every command, the origin included. This is what Multiplayer does: its
        // RunCmds drains commands whose tick has arrived and the "issued by self" flag only decides UI
        // feedback, never whether the command runs. Applying locally at input time and then skipping the
        // echo gives the origin a different starting tick for anything that unfolds over time - a job the
        // pawn has already been walking for two ticks - which is the divergence we kept hitting.
        //
        // A display replica keeps that rule for every kind that only writes state onto objects that already
        // exist (designations, draft, orders, forbid, priority, zones, bills, areas, research, thing state): the
        // frame carries none of those, so somebody has to apply them here. It does not keep it for kinds that
        // create or destroy an object. Those get their identity from the host, and the frame delivers the very
        // same object one or two ticks later; running the command first made the replica allocate an id of its
        // own and then watch `WipeMode.VanishOrMoveAside` replace it with the host's copy. Measured: the objects
        // ended up right and the `UniqueIDsManager` counters stayed offset, silently, because a replica never
        // reports a hash. See `AuthoritativeReplica.AcceptsCommand`.
        foreach (var command in due)
            if (AuthoritativeReplica.AcceptsCommand(command.Kind)) SharedActions.Apply(command);
    }

    public static void Update()
    {
        AuthoritativeReplica.Update();
        // A bulk forbid gesture is accumulated per frame and shipped here, before the gating check: capture already
        // cancelled each local assignment, so a queued flip that does not leave this frame is the exact invisible
        // no-op the batching exists to remove.
        SharedActions.FlushForbidBatches();
        // Read back the flags the last few hundred milliseconds of commands wrote. A setter that ran and a value
        // that stayed are different facts, and only the second one explains why the peer kept its marker.
        SharedActions.VerifyForbidFollowUps();
        if (loadPending && Clock.Now - loadStartedAt > 120f)
        {
            loadPending = false;
            OnWorldLoadFailed("载入超时：120 秒内没有切换到共享存档");
        }
        if (!Gating) return;
        // A display replica still applies the state-only broadcast commands: they are the host's authoritative
        // actions, and no frame carries designations, zones, areas, research or gizmo state. What it no longer does
        // is run a command that creates or destroys an object - see `ApplyDue` and
        // `AuthoritativeReplica.AcceptsCommand`. Skipping the command *capture* path is what keeps a replica from
        // forking in the first place.
        // While paused the game does not tick, so drain anything that became due.
        ApplyDue();
    }
}

/// <summary>Lockstep gate: the local simulation may not outrun the server's published tick.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
internal static class TickGatePatch
{
    private static bool Prefix()
    {
        if (Current.ProgramState != ProgramState.Playing) return true;
        if (HeadlessRuntime.HoldLocalTicks)
        {
            TickGateAudit.Note(false, "hold-local");
            return false;
        }
        var allowed = SharedColony.MayAdvanceTick(out var reason);
        TickGateAudit.Note(allowed, reason);
        return allowed;
    }

    // One bounded burst per frame, after the native loop returns. Never recurse from a tick postfix.
    private static void Postfix(bool __runOriginal)
    {
        TickGateAudit.ObserveRan(__runOriginal);
        SharedColony.CatchUpTick();
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class TickApplyPatch
{
    internal readonly record struct Execution(bool Shared, SimulationTickGuard.Permit Permit,
        SimulationDiagnostics.Observation Audit, IDisposable? Random);

    private static bool Prefix(TickManager __instance, out Execution __state)
    {
        __state = default;
        if (!SharedColony.HasSharedWorld) return true;
        var audit = SimulationDiagnostics.Before(__instance);
        // Cheap, once-per-tick guard against a dev switch that would move TicksGame by 2000 a tick.
        SharedDeterminismGuard.Enforce();
        // First tick of a shared world: report any third-party patch on a method the timeline relies on,
        // so a later divergence can be traced to a mod by name instead of by bisection.
        ModPatchAudit.Run();
        if (!SharedColony.TryBeginTick(__instance, out var permit)) return false;
        try
        {
            // Enter the synced random stream first. The refresh below runs lord AI, region rebuilds and room
            // updates, and any of those that draws from Rand must draw from the shared stream; outside this
            // scope it would draw from the engine's own unsynced one and the two peers would decide
            // differently, which is what put colonists in the divergence report with no commands involved.
            var random = SimulationRandom.Enter();
            SharedLightGrid.Flush();
            __state = new(true, permit, audit, random);
        }
        catch (Exception error) { SharedColony.CancelTick(error); throw; }
        return true;
    }

    private static void Postfix(TickManager __instance, Execution __state, bool __runOriginal)
    {
        // Harmony also runs postfixes after a prefix skipped the original. A default state commits nothing.
        if (!__state.Shared) return;
        if (!__runOriginal) { SharedColony.SkipTick(); return; }
        SharedColony.CompleteTick(__instance, __state.Permit);
        SimulationDiagnostics.After(__instance, __state.Audit);
    }

    private static Exception? Finalizer(Exception? __exception, Execution __state)
    {
        try
        {
            if (__exception is not null && __state.Shared) SharedColony.CancelTick(__exception);
        }
        finally { __state.Random?.Dispose(); }
        return __exception;
    }
}





























































