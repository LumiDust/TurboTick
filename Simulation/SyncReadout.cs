using TurboTick.Networking;

namespace TurboTick.Simulation;

/// <summary>How far this peer is behind the published timeline, in the unit that actually means something.
///
/// <see cref="TickBarrier.Backlog"/> is the raw difference and is not the answer: a lockstep room publishes
/// deliberately ahead of the slowest simulator (<see cref="NetworkProtocol.SimulationLeadTicks"/>), so a peer that is
/// keeping up perfectly still shows a few ticks of backlog. Treating that lead as lag is the same mistake that once
/// made every peer simulate about seven times faster than real time - <c>SharedColony.CatchUpTick</c> subtracts the
/// lead for exactly this reason, and this type does the same subtraction so a display cannot disagree with the
/// catch-up budget.
///
/// The second thing it settles is "too far behind to replay". <see cref="CatchUpPlan.PreferCheckpoint"/> answered that
/// question and had no caller outside the tests, so the answer never reached anybody. It does now, as a readout - the
/// decision to reload is still the loader's to make.</summary>
public readonly record struct SyncReadout(int ServerTick, int AppliedTick, int PipelineLeadTicks, int LagTicks,
    bool Behind, bool NeedsCheckpoint)
{
    /// <summary>Lag worth telling a player about. One eighth of a second at 60 ticks per second; below this the
    /// indicator would flicker on every frame.</summary>
    public const int BehindTicks = 8;

    /// <summary>Raw difference, kept because the two numbers are routinely confused with each other.</summary>
    public int Backlog => Math.Max(0, ServerTick - AppliedTick);

    public static SyncReadout Of(int serverTick, int appliedTick, int pipelineLeadTicks = 0,
        int behindTicks = BehindTicks)
    {
        var backlog = Math.Max(0, serverTick - appliedTick);
        var lag = Math.Max(0, backlog - Math.Max(0, pipelineLeadTicks));
        return new SyncReadout(serverTick, appliedTick, pipelineLeadTicks, lag, lag >= behindTicks,
            CatchUpPlan.PreferCheckpoint(lag));
    }

    /// <summary>For display: the same readout for a local barrier, applying the room's own lead.
    ///
    /// <para>The lead depends on the room's speed, so it is passed in rather than read from the constant: at
    /// <c>Superfast</c> the room legitimately runs 36 ticks ahead, and subtracting the vanilla 6 would report 30
    /// ticks of lag on a peer that is keeping up perfectly - which is the same mistake that once made every peer
    /// simulate about seven times faster than real time, only in the other direction.</para></summary>
    public static SyncReadout Of(TickBarrier barrier, bool lockstep, int leadTicks = -1)
    {
        ArgumentNullException.ThrowIfNull(barrier);
        if (!lockstep) return Of(barrier.ServerTick, barrier.AppliedTick, 0);
        var lead = leadTicks >= 0 ? leadTicks : NetworkProtocol.SimulationLeadTicks;
        return Of(barrier.ServerTick, barrier.AppliedTick, lead);
    }
}
