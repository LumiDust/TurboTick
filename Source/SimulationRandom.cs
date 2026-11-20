using System.Reflection;
using Verse;

namespace TurboTick;

/// <summary>1.7 Root.Update empties Rand's stack every frame. Keep our simulation stream as a value,
/// not as a long-lived PushState. Only one synchronous tick/command transaction owns the stack.</summary>
internal static class SimulationRandom
{
    private static readonly PropertyInfo property = typeof(Rand).GetProperty("StateCompressed", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMemberException("Verse.Rand.StateCompressed");
    private static readonly Func<ulong> read = (property.GetGetMethod(true)
        ?? throw new MissingMethodException("Rand.StateCompressed getter")).CreateDelegate<Func<ulong>>();
    private static readonly Action<ulong> write = (property.GetSetMethod(true)
        ?? throw new MissingMethodException("Rand.StateCompressed setter")).CreateDelegate<Action<ulong>>();
    private static bool initialized;
    private static ulong state;
    private static int depth;

    internal static bool Active => depth > 0;
    internal static void Validate() { _ = read; _ = write; }
    internal static ulong Capture() => depth > 0 || !initialized ? read() : state;

    /// <summary>How many values have been drawn from the shared stream.
    ///
    /// <c>Rand.StateCompressed</c> is <c>seed | (iterations &lt;&lt; 32)</c> (<c>Verse/Rand.cs:94-104</c>), so the
    /// captured state already carries the count - but only as a side effect. Exposing it makes the difference
    /// legible: at tick 10260 the two peers had the same seed and differed by exactly 13 draws, which says one
    /// peer ran a code path that drew from the stream and the other did not.</summary>
    internal static uint Iterations() => (uint)(Capture() >> 32);
    internal static void Restore(ulong savedState)
    {
        if (depth != 0) throw new InvalidOperationException("Cannot replace RNG during a simulation transaction.");
        state = savedState;
        initialized = true;
    }
    internal static void Reset()
    {
        if (depth != 0) throw new InvalidOperationException("Cannot leave during a simulation transaction.");
        initialized = false;
        state = 0;
    }

    internal static IDisposable Enter()
    {
        if (!initialized) throw new InvalidOperationException("Shared RNG has not been restored from the snapshot.");
        if (depth == 0)
        {
            Rand.PushState();
            try { write(state); }
            catch { Rand.PopState(); throw; }
        }
        ++depth;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (--depth != 0) return;
            try { state = read(); }
            finally { Rand.PopState(); }
        }
    }
}

