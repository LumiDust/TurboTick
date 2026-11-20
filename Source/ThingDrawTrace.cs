using System.Globalization;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Attributes shared-random draws to the individual thing that made them, so "the Normal tick list drew five more
/// on one peer" narrows to one def.
///
/// The staged snapshot got as far as it can: at tick 14618 one peer was four draws ahead at
/// <c>afterThings</c>, the storyteller drew nothing on either side, <c>MapPostTick</c> drew the same twelve on both,
/// and all 59 pawns were identical. So the difference is inside <c>TickList.Tick</c> and belongs to a non-pawn
/// thing - about 44 000 candidates.
///
/// Off unless <c>TURBOTICK_TRACE_THING_DRAWS=1</c>. It costs two reads of the shared counter per thing per tick,
/// which is acceptable for a diagnostic run and not acceptable for play.
/// </summary>
internal static class ThingDrawTrace
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TURBOTICK_TRACE_THING_DRAWS") == "1";

    private const int Capacity = 64;

    private static readonly List<string> Recent = new(Capacity);

    internal static void Reset()
    {
        lock (Recent) Recent.Clear();
    }

    internal static void Record(Thing thing, uint draws)
    {
        lock (Recent)
        {
            Recent.Add(string.Join(" ",
                "draw",
                thing.def?.defName ?? "?",
                thing.thingIDNumber.ToString(CultureInfo.InvariantCulture),
                draws.ToString(CultureInfo.InvariantCulture)));
            if (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    /// <summary>Every thing that drew during the last tick, with how many values it took.</summary>
    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }
}

/// <summary>
/// Wraps a thing's tick to count what it drew.
///
/// Patch point note: <c>Pawn</c> and <c>ThingWithComps</c> override <see cref="Thing.DoTick"/>, but their
/// overrides call <c>base.DoTick()</c>, so a patch on the base method still observes them. The cost is that a
/// nested tick (an inner container) would overwrite the pending counter, so the previous value is saved and
/// restored around the call - the outermost thing still gets an honest delta.
/// </summary>
[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class ThingDrawTracePatch
{
    private static uint before;
    private static uint saved;

    private static void Prefix()
    {
        if (!ThingDrawTrace.Enabled) return;
        saved = before;
        before = SimulationRandom.Iterations();
    }

    private static void Postfix(Thing __instance)
    {
        if (!ThingDrawTrace.Enabled) return;
        var after = SimulationRandom.Iterations();
        var drew = after - before;
        before = saved;
        if (drew > 0) ThingDrawTrace.Record(__instance, drew);
    }
}

