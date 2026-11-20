using System;
using System.Diagnostics;
using System.Threading;

namespace TurboTick;

/// <summary>
/// Caps the headless main loop, which the engine runs with no frame limit at all.
///
/// `Waylith.EngineApplication.RunHeadlessLoop` is a bare `while (shutdown not requested)` that calls
/// `OnUpdate` and nothing else: no sleep, no vsync, no `TargetFrameRate` (those live in `RunFrames`, the
/// rendering path, which headless never enters). Measured on the Linux container with the world held
/// (no replica attached, `gameTick` not advancing): main thread pinned at 100% of a core for 12 minutes of
/// CPU time, process total 202%, `Time.deltaTime` around 0.0005 s. So roughly 1800 fps of pure spinning.
///
/// A frame cap is not a cosmetic change here. `TickManager.TickManagerUpdate` accumulates
/// `realTimeToTickThrough` by `deltaTime` whenever a frame is much shorter than a tick
/// (`|deltaTime - curTimePerTick| >= curTimePerTick * 0.1`), then subtracts a whole `curTimePerTick` per
/// tick it runs. At 1800 fps that produces a sawtooth: the accumulator climbs in 0.5 ms steps, one tick
/// drains 16.6 ms, and the remainder goes deeply negative - which is exactly the negative accumulator we
/// kept measuring while diagnosing the frozen world. At 60 fps the two branches coincide and one frame
/// is one tick, which is the arithmetic the vanilla loop was written for.
///
/// Default 60 fps to match what a rendering client gets. `TURBOTICK_HEADLESS_FPS=0` disables the cap,
/// any other value is honoured up to 1000.
/// </summary>
internal static class HeadlessFrameThrottle
{
    private const int DefaultFramesPerSecond = 60;
    private const int MaximumFramesPerSecond = 1000;

    private static readonly long Interval = ResolveInterval();
    private static long next;

    /// <summary>Called once per frame from the update patch, after all the per-frame work is done.</summary>
    internal static void Wait()
    {
        if (Interval <= 0) return;
        var now = Stopwatch.GetTimestamp();
        var remaining = next - now;
        next += Interval;
        if (remaining <= 0)
        {
            // Behind schedule. Reset rather than catch up: a stall must not be repaid as a burst of
            // zero-length frames, which would put the tick accumulator straight back into the sawtooth.
            if (next < now) next = now + Interval;
            return;
        }
        var milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
        if (milliseconds >= 1.0) Thread.Sleep((int)milliseconds);
    }

    private static long ResolveInterval()
    {
        var raw = Environment.GetEnvironmentVariable("TURBOTICK_HEADLESS_FPS");
        var fps = int.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : DefaultFramesPerSecond;
        if (fps == 0) return 0;
        if (fps > MaximumFramesPerSecond) fps = MaximumFramesPerSecond;
        return Stopwatch.Frequency / fps;
    }
}
