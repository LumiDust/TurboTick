using Verse;

namespace TurboTick;

/// <summary>Reads the shared random stream's position so a "this should almost never happen" event can be told from a
/// "the stream is not advancing" one.
///
/// <para>Why: six constructions in a row failed at <c>workDone</c> 0–7 of 95 with a success chance of 0.85
/// (<c>JobDriver_ConstructFinishFrame</c>: the failure branch is taken when
/// <c>Rand.Value &lt; 1 - Pow(chance, num/workToBuild)</c>, which for a normal construction speed is a 0.3% event per
/// tick). Six consecutive failures at those odds is not bad luck.</para>
///
/// <para>Deliberately does not read <c>Rand.Value</c>: reading it consumes a draw from the shared stream, which would
/// shift every later draw on this peer and make the probe itself a source of divergence. <c>StateCompressed</c> is
/// <c>seed | (iterations &lt;&lt; 32)</c> (<c>Verse/Rand.cs:94-104</c>), so it answers both halves of the question
/// without touching the stream: the same seed with the same iteration count on two failures means the stream is being
/// rewound to a fixed point every tick, and a climbing iteration count means the stream is fine and the value really is
/// that unlucky.</para></summary>
internal static class RandPositionProbe
{
    private static readonly List<string> seen = [];

    internal static string Sample()
    {
        try
        {
            var state = SimulationRandom.Capture();
            var text = $"seed=0x{(state & 0xFFFFFFFF):x8} 抽数={state >> 32}";
            lock (seen)
            {
                if (seen.Count < 12) seen.Add(text);
                // The interesting part is not one position but whether it repeats: a stream that returns to the same
                // point every tick produces the same first draw every tick, and that is what a permanent failure looks
                // like from the game's side.
                var repeats = seen.Count(v => v == text);
                if (repeats > 1) text += $"（同一位置第 {repeats} 次）";
            }
            return text;
        }
        catch (Exception error)
        {
            return "读不到随机流位置：" + error.GetType().Name;
        }
    }
}
