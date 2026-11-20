using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Records the actual <c>Rand.Value</c> the construction driver compared against, without consuming a draw.
///
/// <para>Why: <c>JobDriver_ConstructFinishFrame</c> fails a frame when
/// <c>Rand.Value &lt; 1 - Pow(ConstructSuccessChance, num / WorkToBuild)</c>. Measured on the live authority: success
/// chance 0.85, <c>WorkToBuild</c> 95, and six frames in a row destroyed at <c>workDone</c> 0–7. For a per-tick work of
/// about 2 that threshold is 0.0034, so six consecutive failures have odds around 1e-14 - which is not luck. Either the
/// value really is that small every time (a rewound or clamped stream) or the threshold is not what the formula says
/// (something in the chain is patched). The number itself decides between them.</para>
///
/// <para>Implemented as a <b>postfix on the getter</b>, so the draw has already happened and is only observed. Reading
/// <c>Rand.Value</c> from a probe would consume an extra draw and shift every later draw on this peer - a probe must not
/// change the stream it measures. The gate is a counter set around the driver's own tick, so unrelated draws from the
/// rest of the colony are not recorded.</para></summary>
internal static class RandValueProbe
{
    [ThreadStatic] internal static int inConstructDriver;
    private static long samples;
    private static readonly List<float> recent = [];
    private static float minimum = 2f;
    private static float maximum = -1f;

    internal static void Note(float value)
    {
        Interlocked.Increment(ref samples);
        if (value < minimum) minimum = value;
        if (value > maximum) maximum = value;
        lock (recent)
        {
            recent.Add(value);
            if (recent.Count > 40) recent.RemoveAt(0);
        }
        // Printed for the first draws of every run, unconditionally for values that are small enough to fail a
        // construction - the interesting ones are exactly those, and a cap would hide the loop the way the first
        // version of the failure log did.
        if (Interlocked.Read(ref samples) <= 12 || value < 0.02f)
            Log.Warning($"[TurboTick 建造] 建造随机值 #{Interlocked.Read(ref samples)}：Rand.Value={value:0.00000000}"
                + $" 随机流={RandPositionProbe.Sample()}");
    }

    internal static string Summary()
    {
        var total = Interlocked.Read(ref samples);
        if (total == 0) return "建造随机值 无采样（建造驱动没跑过）";
        var text = $"建造随机值 {total} 次 最小={minimum:0.00000000} 最大={maximum:0.00000000}";
        lock (recent)
            if (recent.Count > 2)
                text += " 最近=" + string.Join("/", recent.TakeLast(10).Select(v => v.ToString("0.0000")))
                    + $" 不同值={recent.Distinct().Count()}/{recent.Count}";
        return text;
    }
}

[HarmonyPatch(typeof(Rand), nameof(Rand.Value), MethodType.Getter)]
internal static class RandValueObservePatch
{
    private static void Postfix(float __result)
    {
        if (RandValueProbe.inConstructDriver > 0) RandValueProbe.Note(__result);
    }
}

/// <summary>Opens the sampling window while a construct-finish driver ticks, so the recorded values are the ones that
/// decide a wall rather than the whole colony's draws.
///
/// <para>Both entry points are patched, and the first version of this only patched <c>DriverTick</c> - which made the
/// probe report "no samples" on a colony that was failing walls every three seconds. The reason is in vanilla:
/// <c>Toil.tickAction</c> is called from <c>DriverTick</c> (<c>Verse.AI/JobDriver.cs:342</c>) but
/// <c>Toil.tickIntervalAction</c> is called from <c>DriverTickInterval</c> (<c>Verse.AI/JobDriver.cs:428</c>), and the
/// construct-finish toil is built with <c>tickIntervalAction</c> - so the failure branch ran entirely outside the
/// window.</para></summary>
[HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTick))]
internal static class ConstructDriverWindowPatch
{
    private static void Prefix(JobDriver __instance)
    {
        if (__instance is JobDriver_ConstructFinishFrame) RandValueProbe.inConstructDriver++;
    }

    private static void Postfix(JobDriver __instance)
    {
        if (__instance is JobDriver_ConstructFinishFrame && RandValueProbe.inConstructDriver > 0)
            RandValueProbe.inConstructDriver--;
    }

    // A driver that throws would leave the window open and every later draw in the process would be recorded as a
    // construction sample. The finalizer closes it on the exception path for that reason.
    private static Exception? Finalizer(JobDriver __instance, Exception? __exception)
    {
        if (__exception is not null && __instance is JobDriver_ConstructFinishFrame
            && RandValueProbe.inConstructDriver > 0) RandValueProbe.inConstructDriver--;
        return __exception;
    }
}

/// <summary>The other half of the driver pump. See <see cref="ConstructDriverWindowPatch"/> for why both exist.</summary>
[HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTickInterval))]
internal static class ConstructDriverIntervalWindowPatch
{
    private static void Prefix(JobDriver __instance)
    {
        if (__instance is JobDriver_ConstructFinishFrame) RandValueProbe.inConstructDriver++;
    }

    private static void Postfix(JobDriver __instance)
    {
        if (__instance is JobDriver_ConstructFinishFrame && RandValueProbe.inConstructDriver > 0)
            RandValueProbe.inConstructDriver--;
    }

    private static Exception? Finalizer(JobDriver __instance, Exception? __exception)
    {
        if (__exception is not null && __instance is JobDriver_ConstructFinishFrame
            && RandValueProbe.inConstructDriver > 0) RandValueProbe.inConstructDriver--;
        return __exception;
    }
}
