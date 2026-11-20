using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Records who destroys a construction blueprint or frame, and under which <see cref="DestroyMode"/>.
///
/// <para>Why this exists: on the live authority the deposit probe proved material really arrives
/// (<c>投递后 容器内 5</c> on <c>Frame_Wall#58222</c>), the wall count stayed pinned at 509, and the blueprint's
/// <c>thingIDNumber</c> was different on every single report (55993 → 56961 → 58168 → 58688 → 59847 → 60190). A new id
/// means a new object: the old one was destroyed. Neither <c>Frame.CompleteConstruction</c> nor
/// <c>Frame.FailConstruction</c> ran (both counted, both zero), so the destruction comes from somewhere else
/// entirely - and vanilla logs nothing at all for a canceled blueprint.</para>
///
/// <para>Prints the mode plus a trimmed stack, because the mode alone does not identify the caller: <c>Cancel</c> is
/// what a player cancel uses, but it is also what <c>GenConstruct</c> uses when a target becomes invalid, and what a
/// lost reservation cleanup uses. The top three frames name the actual branch.</para></summary>
internal static class ConstructionDestroyProbe
{
    private static long total;
    private static long echoed;

    internal static string Summary() =>
        $"建造物销毁 已记录={Interlocked.Read(ref total)}";

    internal static void Note(Thing thing, DestroyMode mode)
    {
        var count = Interlocked.Increment(ref total);
        // The first handful carry the stack; after that only the count moves. A destroy storm is not itself the
        // finding - the finding is which call site does it, and that does not change with repetition.
        if (count > 8) return;
        var stack = "";
        try
        {
            var frames = new StackTrace(2, false).GetFrames();
            if (frames is { Length: > 0 })
                stack = string.Join(" <- ", frames.Take(4)
                    .Select(f => (f.GetMethod()?.DeclaringType?.Name ?? "?") + "." + (f.GetMethod()?.Name ?? "?")));
        }
        catch (Exception) { }
        Log.Warning($"[TurboTick 建造] 建造物被销毁 #{count}：{thing.def?.defName ?? "?"}#{thing.thingIDNumber}"
            + $" @{thing.PositionHeld.x},{thing.PositionHeld.z} 模式={mode}"
            + (thing is Frame frame ? $" 已做工作={frame.workDone:0}/{frame.WorkToBuild:0}" : "")
            + $" 栈={stack}");
    }

    /// <summary>Called from the periodic construction report so the count is readable without a log grep.</summary>
    internal static void Echo()
    {
        var total2 = Interlocked.Read(ref total);
        if (total2 == echoed) return;
        echoed = total2;
        Log.Warning("[TurboTick 建造] " + Summary());
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.Destroy), new[] { typeof(DestroyMode) })]
internal static class ConstructionDestroyPatch
{
    // Prefix: `Destroy` removes the thing from the lister and clears `def` on some paths, so reading it afterwards
    // would print "?#0" - which is how a destroy probe ends up proving nothing.
    private static void Prefix(Thing __instance, DestroyMode mode)
    {
        if (__instance is Blueprint or Frame) ConstructionDestroyProbe.Note(__instance, mode);
    }
}
