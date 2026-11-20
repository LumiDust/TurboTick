using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Networking;

public sealed partial class DedicatedServer
{
    private readonly string _simulationKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private Peer? _simulationPeer;
    private SimulationProgress? _simulationProgress;
    private string? _requiredSimulationWorldId;
    public string? RequiredSimulationWorldId => Volatile.Read(ref _requiredSimulationWorldId);
    private sealed record SimulationProgress(Peer? Peer, long Epoch, int AppliedTick, bool Ready);
    private const int SimulationLeadTicks = NetworkProtocol.SimulationLeadTicks;

    /// <summary>Admits the resident lockstep simulation through the same TCP and compatibility handshake
    /// as players. Its credential never leaves this process except on the loopback connection.</summary>
    public async Task ConnectSimulationAsync(DedicatedClient client, ClientHello hello, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(hello);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_run is null || _run.IsCancellationRequested || _listener is null)
                throw new InvalidOperationException("Start the server before connecting its simulation.");
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            var address = endpoint.Address;
            if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
            else if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
            if (!IPAddress.IsLoopback(address))
                throw new InvalidOperationException("The simulation requires a loopback or wildcard listener address.");
            await client.ConnectAsync(address.ToString(), endpoint.Port, hello with { SimulationKey = _simulationKey }, ct)
                .ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Publishes an immutable frame sample from the process's game thread. This grants no
    /// network role and an old epoch or disconnected simulation sample never opens the tick gate.</summary>
    public void ReportSimulationProgress(long epoch, int appliedTick, bool ready)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(epoch, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(appliedTick);
        Volatile.Write(ref _simulationProgress, new SimulationProgress(Volatile.Read(ref _simulationPeer), epoch, appliedTick, ready));
    }

    /// <summary>Persists the lockstep epoch, tick, snapshot and command history for periodic or exit storage.</summary>
    public Task PersistAsync(CancellationToken ct = default) => PersistSessionAsync(ct);

    // Caller holds _stateGate. A name, client-supplied role or loopback address alone grants no privilege.
    private bool ValidateSimulationAdmission(Peer peer, ClientHello hello)
    {
        if (hello.SimulationKey is null) return false;
        var supplied = hello.SimulationKey;
        if (!peer.IsLoopback || supplied.Length != _simulationKey.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_simulationKey)))
            throw new NetworkException("SimulationAuthenticationFailed", "The process simulation credential or loopback connection is invalid.");
        if (_simulationPeer is { Joined: true } existing && !existing.Token.IsCancellationRequested)
            throw new NetworkException("SimulationOccupied", "The process simulation is already connected.");
        return true;
    }

    // Called under _stateGate (or during startup, before the listener exists).
    private void ClearSimulationState()
    {
        Volatile.Write(ref _simulationPeer, null);
        Volatile.Write(ref _simulationProgress, null);
        Volatile.Write(ref _requiredSimulationWorldId, null);
    }

    private int SimulationTickBudget()
    {
        // Lockstep: every peer simulates and the slowest one paces the timeline. The resident process, when
        // present, is just another peer that never disconnects.
        if (_options.Lockstep) return LockstepTickBudget();
        var peer = Volatile.Read(ref _simulationPeer);
        var progress = Volatile.Read(ref _simulationProgress);
        if (peer is null || !peer.Joined || peer.Token.IsCancellationRequested || peer.RoomGeneration != _roomGeneration ||
            progress is null || !ReferenceEquals(progress.Peer, peer) || !progress.Ready || progress.Epoch != Epoch)
            return 0;
        // Same lead as the lockstep branch, and for the same reason: it is pipeline depth in time. A fixed six ticks
        // is 100 ms at vanilla speed but 16 ms at `Superfast`, which is shorter than a progress round trip, so the
        // budget collapsed to 1-2 ticks per iteration and a 360 t/s room published about 119. This is the branch an
        // `authoritative` room actually takes - the lockstep one is only used when `Lockstep` is on.
        var lead = NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(speedInRoom));
        return (int)Math.Clamp((long)progress.AppliedTick + lead - Tick, 0L, int.MaxValue);
    }

    /// <summary>Applied tick reported by each joined peer. A room with nobody able to simulate must not
    /// advance, so an empty roster yields zero rather than a free-running clock.</summary>
    private readonly ConcurrentDictionary<long, int> _peerAppliedTick = new();
    private readonly ConcurrentDictionary<long, long> _peerProgressAt = new();
    /// <summary>Peers that told the server they are loading a snapshot. A loader executes no ticks by
    /// definition, so it cannot pace the room: without this the room froze for the whole 25-40 s a checkpoint
    /// reload takes (measured: `tick=7394 [player#1=7388, player#2=7388]` repeated while both were loading).
    /// The flag is cleared by the peer's first real progress report.</summary>
    private readonly ConcurrentDictionary<long, bool> _peerSynchronizing = new();

    /// <summary>How long a peer that has stopped reporting may hold the timeline.
    ///
    /// A peer that is reloading a checkpoint, sitting in a modal dialog or frozen by its own section 0 stops
    /// sending progress for tens of seconds. Holding the whole room for it is what "everyone freezes while
    /// one player loads" looked like: the joining peer is the slowest by construction, and the room waits for
    /// it with no upper bound. Fifteen seconds is longer than a normal progress report and much shorter than a
    /// checkpoint load, so a load no longer stops the room while a live peer's report jitter still does not
    /// exclude it.</summary>
    private const long GateGraceMilliseconds = 15_000;

    private void ReportPeerProgress(Peer peer, int appliedTick)
    {
        // -1 is the "I am loading a world" sentinel, not a tick.
        if (appliedTick < 0)
        {
            _peerSynchronizing[peer.Id] = true;
            _peerProgressAt[peer.Id] = Environment.TickCount64;
            return;
        }
        _peerSynchronizing.TryRemove(peer.Id, out _);
        _peerAppliedTick[peer.Id] = appliedTick;
        _peerProgressAt[peer.Id] = Environment.TickCount64;
    }

    /// <summary>The lowest tick any joined peer has actually executed, or null when nobody has reported yet.
    ///
    /// A command must be stamped at a tick no peer has already run. Peers are allowed to sit up to
    /// <see cref="NetworkProtocol.SimulationLeadTicks"/> ahead of the slowest one, so stamping at the
    /// server's own tick put arrivals in the past for whoever was ahead: the command then came back with a
    /// higher sequence but an earlier tick, which the ordered intake rejects as a regression, and only the
    /// peers that were behind applied it. That is the observed split where one peer shows commands 9..14 and
    /// the other also shows 15..19.
    ///
    /// A peer that has not reported yet is still downloading or loading the world. It has executed no tick,
    /// so it cannot be "behind" in the sense that matters here, and treating its silence as tick zero would
    /// invite stamping commands in the past.</summary>
    private int? SlowestAppliedTick() => SlowestReportedTick().Slowest;

    private (int? Slowest, bool Any) SlowestReportedTick()
    {
        var slowest = int.MaxValue;
        var any = false;
        var now = Environment.TickCount64;
        foreach (var peer in _peers.Values)
        {
            if (!peer.Joined || peer.Token.IsCancellationRequested || peer.RoomGeneration != _roomGeneration) continue;
            // A peer that says it is loading a world executes no ticks at all; it must never pace the room.
            if (_peerSynchronizing.ContainsKey(peer.Id)) continue;
            if (!_peerAppliedTick.TryGetValue(peer.Id, out var applied)) continue;
            // A peer that stopped reporting is loading, frozen or gone. It is not pacing anyone; waiting for
            // it is what froze the whole room while one player reloaded a checkpoint.
            if (_peerProgressAt.TryGetValue(peer.Id, out var reportedAt) && now - reportedAt > GateGraceMilliseconds)
                continue;
            any = true;
            if (applied < slowest) slowest = applied;
        }
        return any ? (slowest, true) : (null, false);
    }

    /// <summary>Peers that are joined but currently not pacing the room, for the stall diagnostic.</summary>
    private string[] SilentPeers()
    {
        var now = Environment.TickCount64;
        var silent = new List<string>();
        foreach (var peer in _peers.Values)
        {
            if (!peer.Joined) continue;
            if (_peerSynchronizing.ContainsKey(peer.Id)) { silent.Add($"#{peer.Id}:syncing"); continue; }
            if (!_peerProgressAt.TryGetValue(peer.Id, out var reportedAt)) { silent.Add($"#{peer.Id}:never"); continue; }
            var idle = now - reportedAt;
            if (idle > GateGraceMilliseconds) silent.Add($"#{peer.Id}:{(idle / 1000)}s");
        }
        return [.. silent];
    }

    /// <summary>The timeline may only advance as far as the slowest peer that is actually simulating.
    ///
    /// <para>A peer that has joined but never reported is loading its snapshot. The previous version returned 0 in
    /// that case, which froze the whole room for as long as that download took: one joining player stopped
    /// everyone. Multiplayer's server excludes non-Playing players from the pacing clock for the same reason
    /// (docs/audit/MP-FEATURES.md D8 "未追上者不进就绪屏障"). Reporting peers still gate the room, so a
    /// client that stalls mid-session holds the line instead of being silently skipped.</para>
    ///
    /// <para><b>The lead is pipeline depth in time, not a tick count.</b> A peer only learns how far the timeline
    /// got from the next broadcast, and the server only learns how far the peer got from its next report, so the
    /// room has to run further ahead than one round trip or the fastest peer keeps hitting the clamp. Expressed as a
    /// fixed 6 ticks that depth is 100 ms at vanilla speed - enough - but only 16 ms at <c>Superfast</c> (rate 6,
    /// 360 t/s), which is shorter than a report round trip. The budget then settles at 1-2 instead of the rate the
    /// operator selected, and the room publishes about 119 t/s while the HUD asks for 360. Measured 2026-11-16:
    /// `预算=1` at a 62.5 Hz loop equals the observed 119 t/s, and vanilla speed was always correct because there
    /// 6 ticks really is 100 ms.</para>
    ///
    /// <para>So the lead is computed as <c>SimulationLeadMilliseconds</c> worth of ticks at the selected rate,
    /// which keeps a fast room pacing correctly and leaves a vanilla room at exactly the 6 ticks it had.</para>
    private int LockstepTickBudget()
    {
        var (slowest, any) = SlowestReportedTick();
        if (!any || slowest is null) return 0;
        var lead = NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(speedInRoom));
        return (int)Math.Clamp((long)slowest.Value + lead - Tick, 0L, int.MaxValue);
    }

    /// <summary>Room speed for this iteration. Written by the tick loop before it asks for a budget, so the lead
    /// and the clock cannot disagree about the rate.</summary>
    private int speedInRoom = 1;

    /// <summary>Why the lockstep timeline is or is not moving. Without this a stall is only visible as a
    /// stopped tick with no way to tell which peer is holding it.</summary>
    public string LockstepDiagnostics
    {
        get
        {
            if (!_options.Lockstep) return "off";
            var parts = new List<string>();
            foreach (var peer in _peers.Values)
            {
                if (!peer.Joined) continue;
                var reported = _peerAppliedTick.TryGetValue(peer.Id, out var applied) ? applied.ToString() : "none";
                parts.Add($"{(peer.IsSimulation ? "sim" : "player")}#{peer.Id}={reported}");
            }
            return $"tick={Tick} [{string.Join(", ", parts)}]"
                + (SilentPeers() is { Length: > 0 } silent ? $" not-pacing=[{string.Join(", ", silent)}]" : "");
        }
    }

    /// <summary>Diagnostic mirror of the budget that gates the published timeline.</summary>
    public int SimulationTickBudgetForDiagnostics => SimulationTickBudget();

    /// <summary>The pipeline lead this room is pacing with, in ticks. Exposed next to the budget because "the budget is
    /// 1" and "the lead is 6" are the same defect seen twice: at `Superfast` the room needs 36 ticks of depth to fit a
    /// 100 ms round trip, and the state file is the only place an operator can see which figure is in force.</summary>
    public int SimulationLeadTicksForDiagnostics => NetworkProtocol.LeadTicksAt(NativeTickClock.Rate(speedInRoom));

    private string _pacingSnapshot = "";
    private long _pacingLoggedAt;

    /// <summary>Writes the pacing line when the set of silent peers changes, and as a 30 s heartbeat.
    /// Called from the tick loop, so it must never throw into it.</summary>
    private void LogPacingChanges()
    {
        try
        {
            var silent = SilentPeers();
            var snapshot = silent.Length == 0 ? "" : string.Join(",", silent);
            var now = Environment.TickCount64;
            if (snapshot == _pacingSnapshot && now - _pacingLoggedAt < 30_000) return;
            _pacingSnapshot = snapshot;
            _pacingLoggedAt = now;
            Console.WriteLine("[server] " + LockstepDiagnostics);
        }
        catch (Exception) { /* diagnostics must never stop the timeline */ }
    }
}
