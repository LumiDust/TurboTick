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
        return (int)Math.Clamp((long)progress.AppliedTick + SimulationLeadTicks - Tick, 0L, int.MaxValue);
    }

    /// <summary>Applied tick reported by each joined peer. A room with nobody able to simulate must not
    /// advance, so an empty roster yields zero rather than a free-running clock.</summary>
    private readonly ConcurrentDictionary<long, int> _peerAppliedTick = new();

    private void ReportPeerProgress(Peer peer, int appliedTick)
    {
        if (appliedTick >= 0) _peerAppliedTick[peer.Id] = appliedTick;
    }

    /// <summary>The lowest tick any joined peer has actually executed, or null when one has not reported.
    ///
    /// A command must be stamped at a tick no peer has already run. Peers are allowed to sit up to
    /// <see cref="NetworkProtocol.SimulationLeadTicks"/> ahead of the slowest one, so stamping at the
    /// server's own tick put arrivals in the past for whoever was ahead: the command then came back with a
    /// higher sequence but an earlier tick, which the ordered intake rejects as a regression, and only the
    /// peers that were behind applied it. That is the observed split where one peer shows commands 9..14 and
    /// the other also shows 15..19.</summary>
    private int? SlowestAppliedTick()
    {
        var slowest = int.MaxValue;
        var any = false;
        foreach (var peer in _peers.Values)
        {
            if (!peer.Joined || peer.Token.IsCancellationRequested || peer.RoomGeneration != _roomGeneration) continue;
            if (!_peerAppliedTick.TryGetValue(peer.Id, out var applied)) return null;
            any = true;
            if (applied < slowest) slowest = applied;
        }
        return any ? slowest : null;
    }

    private int LockstepTickBudget()
    {
        var slowest = int.MaxValue;
        var any = false;
        foreach (var peer in _peers.Values)
        {
            if (!peer.Joined || peer.Token.IsCancellationRequested || peer.RoomGeneration != _roomGeneration) continue;
            if (!_peerAppliedTick.TryGetValue(peer.Id, out var applied)) return 0; // a peer that never reported holds the line
            any = true;
            if (applied < slowest) slowest = applied;
        }
        if (!any) return 0;
        return (int)Math.Clamp((long)slowest + SimulationLeadTicks - Tick, 0L, int.MaxValue);
    }

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
            return $"tick={Tick} [{string.Join(", ", parts)}]";
        }
    }

    /// <summary>Diagnostic mirror of the budget that gates the published timeline.</summary>
    public int SimulationTickBudgetForDiagnostics => SimulationTickBudget();
}
