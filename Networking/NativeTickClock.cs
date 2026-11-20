namespace TurboTick.Networking;

/// <summary>Wall-clock budget for the native speed selection. Paused time never becomes catch-up debt.</summary>
public sealed class NativeTickClock
{
    /// <summary>Largest fraction carried between frames, in ticks. A quarter second at normal speed: enough to
    /// smooth a late frame, far too small to replay a stall as a burst.</summary>
    private const double MaxRemainderTicks = 15d;

    private int speed = -1;
    private double remainder;

    /// <summary>The fraction of a tick carried into the next call. Read by the status line so "the fraction is
    /// being carried" is a number instead of an inference.</summary>
    public double Remainder => remainder;

    public static double Rate(int timeSpeed) => SpeedPolicy.ValidateTimeSpeed(timeSpeed) switch
    {
        0 => 0d,
        1 => 1d,
        2 => 3d,
        3 => 6d,
        4 => 15d,
        _ => throw new ArgumentOutOfRangeException(nameof(timeSpeed))
    };

    public int Advance(double elapsedSeconds, int timeSpeed)
    {
        var rate = Rate(timeSpeed);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        // Only a real change of speed resets the fraction. It used to be reset whenever the caller had nothing to
        // spend, and the caller expressed back-pressure by passing speed 0, so one blocked sample out of four threw
        // away ~0.96 of a tick each time: a 20 minute room published 45.0 tick/s while the loop ran 62.5 times a
        // second and produced ticks on 74% of them. Back-pressure belongs to the caller, as a clamp on the result.
        if (speed != timeSpeed) { speed = timeSpeed; remainder = 0; }
        if (rate == 0) { remainder = 0; return 0; }
        // A stalled process should not publish minutes of ticks in one network burst.
        remainder += Math.Min(elapsedSeconds, 0.25d) * 60d * rate;
        if (remainder > MaxRemainderTicks) remainder = MaxRemainderTicks;
        var ticks = (int)Math.Floor(remainder + 1e-9d);
        remainder -= ticks;
        return ticks;
    }

    /// <summary>Hand back whole ticks the caller could not spend this iteration.
    ///
    /// A tick that the clock earned is real time that already passed. Dropping it because the pacing budget was
    /// momentarily zero does not create back-pressure, it makes the timeline permanently slower than the wall
    /// clock: measured on a 40-pawn room, 1800 ticks were earned per 30 s and only 1500 were published, with the
    /// 271-tick gap matching the iterations whose budget was zero. Returning them keeps the debt bounded by
    /// <see cref="MaxRemainderTicks"/> instead - a burst is still impossible, but nothing is silently lost.</summary>
    public void Unspend(int ticks)
    {
        if (ticks <= 0) return;
        remainder += ticks;
        if (remainder > MaxRemainderTicks) remainder = MaxRemainderTicks;
    }
}
