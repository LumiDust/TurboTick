using System.Globalization;
using Verse;

namespace TurboTick;

/// <summary>
/// Names the branch that refused to run the vanilla tick loop, and proves whether the refusal reached the loop.
///
/// Everything measured from a postfix is a report on what the engine decided to do *if it ran at all*: Harmony
/// still runs postfixes after a prefix has skipped the original method, so a skipped frame and an idle frame are
/// indistinguishable from outside - both show <c>ticksThisFrame = 0</c> and a stale <c>realTimeToTickThrough</c>.
/// Measured on a real save: 9 231 entries into <c>TickManagerUpdate</c> in 5 s, 8 931 of them producing nothing,
/// <c>TicksGame</c> frozen at 2499, while <c>appliedTick = 2499</c>, <c>serverTick = 2505</c> and
/// <c>CanAdvance = true</c>. The accumulator grew by exactly one <c>Time.deltaTime</c> in that window, which is
/// the signature of the method body never running: only a refusing prefix produces "entered N times, accumulated
/// once". <see cref="TickGatePatch"/> is the only prefix on this method, so it is the gate, and only the gate can
/// say which of its conditions fired.
/// </summary>
internal static class TickGateAudit
{
    private const long IntervalMilliseconds = 5_000;

    private static long lastMilliseconds = Environment.TickCount64;
    private static readonly Dictionary<string, int> Reasons = new(StringComparer.Ordinal);
    private static int allowed;
    private static int refused;
    private static int suppressed;

    /// <summary>Called once per gate decision, before the answer is returned. Cheap on the allow path: one
    /// increment per frame and no line unless the window closes with something to report.
    ///
    /// <para>Not gated on <see cref="HeadlessRuntime.Active"/> any more. A display replica is a GUI process, so that
    /// flag is false there - which meant the one peer a player actually watches was the only one that never wrote a
    /// gate line. Measured: the client sat at `引擎tick=18000` while the authority's `ticksGame` climbed past 32000,
    /// and the client log contained not one refusal, so "the pawns stopped moving" had no recorded cause at all.</para>
    ///
    /// <para>Still gated on a shared world, because a plain single-player session has no gate to explain and the audit
    /// would otherwise sprinkle counters into logs that have nothing to do with multiplayer.</para></summary>
    internal static void Note(bool allowedNow, string reason)
    {
        if (!SharedColony.HasSharedWorld) return;
        if (allowedNow)
        {
            ++allowed;
        }
        else
        {
            ++refused;
            Reasons.TryGetValue(reason, out var count);
            Reasons[reason] = count + 1;
        }
        Close();
    }

    /// <summary>
    /// Called from the postfix with Harmony's <c>__runOriginal</c>. When the prefix skipped the method this is the
    /// only place that can still see it, and the count must match <see cref="Note"/>'s refusals exactly - a
    /// mismatch would mean a second prefix (another mod) is also refusing ticks.
    /// </summary>
    internal static void ObserveRan(bool ranOriginal)
    {
        if (!SharedColony.HasSharedWorld || ranOriginal) return;
        ++suppressed;
    }

    private static void Close()
    {
        var now = Environment.TickCount64;
        if (now - lastMilliseconds < IntervalMilliseconds) return;
        // A display replica refusing its own tick is not a refusal - it is the definition of a display replica: the host
        // simulates, this peer follows frames. Reporting it made the client write
        // `tick 闸门：放行 0 次，拒绝 720 次（frozen=720）` as a WARNING every five seconds for the whole session, which
        // buries the one warning that matters ("the world stopped advancing") under thousands of lines describing
        // normal operation. Measured on a replica: 720 refusals per 5 s for hours, and `frozen` is the only reason in
        // the list.
        //
        // The subtraction is per reason, not a blanket "if frozen then stay quiet": a replica can be frozen *and* be
        // refused for a genuine reason at the same time - `barrier` or `hold` alongside `frozen` is exactly the state
        // that means something is wrong - and only the frozen share is subtracted.
        if (Reasons.TryGetValue("frozen", out var frozen))
        {
            allowed += frozen;
            refused -= frozen;
            suppressed -= frozen;
            Reasons.Remove("frozen");
        }
        if (allowed == 0 && refused == 0 && Reasons.Count == 0)
        {
            allowed = 0;
            suppressed = 0;
            lastMilliseconds = now;
            return;
        }
        if (refused <= 0)
        {
            // A healthy loop must never write here, or the log becomes the next thing to drown in.
            allowed = 0;
            suppressed = 0;
            lastMilliseconds = now;
            return;
        }
        var parts = new List<string>(Reasons.Count);
        foreach (var pair in Reasons) parts.Add(pair.Key + "=" + pair.Value);
        parts.Sort(StringComparer.Ordinal);
        Log.Warning("[TurboTick] tick 闸门：5 秒内放行 " + allowed + " 次，拒绝 " + refused
            + " 次（" + string.Join("，", parts) + "）；harmony 报告原方法未执行 " + suppressed
            + " 次；" + Describe());
        allowed = 0;
        refused = 0;
        suppressed = 0;
        Reasons.Clear();
        lastMilliseconds = now;
    }

    /// <summary>The full condition list as one string, so a single line explains why the gate said no.</summary>
    internal static string Describe()
    {
        var barrier = SharedColony.Session.Barrier;
        return "gating=" + SharedColony.Gating
            + " held=" + TurboState.Authority.IsHeld
            + " predictive=" + (AuthoritativeReplica.Predictive && AuthoritativeReplica.Enabled)
            + " frozen=" + AuthoritativeReplica.Frozen
            + " canAdvance=" + barrier.CanAdvance
            + " applied=" + barrier.AppliedTick.ToString(CultureInfo.InvariantCulture)
            + " server=" + barrier.ServerTick.ToString(CultureInfo.InvariantCulture)
            + " guardBusy=" + SharedColony.TickExecuting
            + " phase=" + SharedColony.Session.Phase
            + " ticksGame=" + (Find.TickManager?.TicksGame.ToString(CultureInfo.InvariantCulture) ?? "?");
    }
}
