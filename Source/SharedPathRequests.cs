using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Resolve shared requests at a deterministic game-thread point. The vanilla batched worker
/// captures pawn/provider costs across frames; a headless replica and a rendered replica can therefore
/// calculate a different route even for the same start, target and random state.
///
/// <para><b>It counts its own outcomes, because skipping vanilla is not the same as producing a path.</b> Every
/// branch below returns <c>false</c>, which cancels <c>PathFinder.PushRequest</c> - so a request this prefix does not
/// resolve is never queued anywhere either, and the pawn waits on it forever. That is invisible from the outside: the
/// job tracker keeps the job, the toil index stays on the "goto" step, no error is logged, and the pawn simply stands
/// there. Measured on a live authority: all 72 pawns and animals reported <c>移动=否 路径节点=0</c> while their
/// positions drifted by a cell every few seconds - i.e. the movement the player sees is not path following at all.
/// Four numbers make the four possible causes distinguishable.</para></summary>
[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PushRequest), new[] { typeof(PathRequest) })]
internal static class SharedPathRequests
{
    private static long resolved;
    private static long notFound;
    private static long invalid;
    private static long skipped;
    private static long alreadyReady;
    private static int lastReportTick = -1000;

    internal static string Counters() =>
        $"路径请求 已解析={resolved} 无路={notFound} 校验失败={invalid} 已就绪跳过={alreadyReady} 未接管={skipped}";

    private static bool Prefix(PathFinder __instance, PathRequest request)
    {
        if (!SharedColony.HasSharedWorld) { skipped++; return true; }
        if (request.ResultIsReady || request.Cancelled)
        {
            // Already settled by someone else. Skipping vanilla is right, and counting it matters: a request that is
            // ready but was never consumed would otherwise look like a resolved request here.
            alreadyReady++;
            return false;
        }
        if (!request.Validate())
        {
            // The dangerous branch: vanilla is skipped, so this request leaves the queue unqueued and unresolved.
            // If this number is non-zero, every one of those pawns is standing still permanently.
            invalid++;
            if (invalid <= 5)
                Log.Warning($"[TurboTick] 路径请求校验失败，未被接管也未被排队（该 pawn 将原地不动）："
                    + $"start={request.Start} target={request.Target} endMode={request.EndMode}");
            return false;
        }
        // Preserve the captured traversal permissions, cost tuning, end mode and customizer. The Pawn
        // overload recreates TraverseParms and would silently drop caller-specific permissions.
        var path = __instance.FindPathNow(request.Start, request.Target, request.TraverseParms,
            request.Tuning, request.EndMode, request.customizer);
        request.Resolve(path);
        // `Found`, not "non-null": `PawnPath.NotFound` is a real object with `Found == false`, so a null check would
        // report every failure as a success.
        if (path is not null && path.Found) resolved++;
        else
        {
            notFound++;
            if (notFound <= 5)
                Log.Warning($"[TurboTick] 同步寻路无结果：start={request.Start} target={request.Target} "
                    + $"endMode={request.EndMode} 结果={path?.GetType().Name ?? "null"} Found={path?.Found}");
        }
        return false;
    }

    /// <summary>Called from the periodic status line. Printed only when something is off, so a healthy colony does not
    /// add a line per frame.</summary>
    internal static void ReportIfStalled()
    {
        var now = Find.TickManager?.TicksGame ?? 0;
        if (now - lastReportTick < 600) return;
        lastReportTick = now;
        if (notFound == 0 && invalid == 0) return;
        Log.Warning("[TurboTick] " + Counters());
    }
}
