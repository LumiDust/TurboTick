using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Two probes around the forbidden flag, both off unless an environment variable names a start tick.
///
/// <para><c>TURBOTICK_PROBE_FORBID</c> (authority) writes the backing field on a handful of hauled items so the frame
/// path that carries the flag can be measured end to end. It runs on the authority on purpose: the flag only proves
/// anything if it travels through a frame, and a flip done on the replica would test the replica against itself.</para>
///
/// <para><c>TURBOTICK_PROBE_UNFORBID</c> (replica) does the opposite and is the interesting one: it calls the real
/// <c>ForbidUtility.SetForbidden(false)</c>, which is the same entry point the U key uses. That path is patched by the
/// capture prefix, so the call is either turned into a command (and the flag changes only after the authority sends it
/// back) or refused by the gate. Either outcome is a measurement; what it removes is the need for a human to press a
/// key at a known tick while a log is being watched.</para>
///
/// <para>The backing field is written directly in the authority probe instead of assigning
/// <c>CompForbiddable.Forbidden</c>. That setter is Harmony-patched by the capture path, and with a live session
/// attached the probe's write would be classified as a player gesture: it would be cancelled locally and shipped as a
/// command, so the probe would measure the command path (already covered) instead of the frame path (the one nobody
/// checked). Writing the field is also exactly what loading a save does, which is the other way this flag can
/// legitimately change without a command.</para>
///
/// <para>Why a probe at all: "the flag rides in the frame" reads fine and can be false in a dozen ways - the tuple may
/// not be recomputed, the object may never be revisited, the receiver may load the flag and still draw the old marker.
/// </summary>
internal static class ForbidProbe
{
    private static readonly int StartTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_FORBID"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var start) ? start : -1;

    private static readonly int Gap = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_FORBID_GAP"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var gap) && gap > 0 ? gap : 120;

    private static readonly int UnforbidStart = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_UNFORBID"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var unforbidStart) ? unforbidStart : -1;

    /// <summary>How many stacks one round touches. Small enough to stay off the budget, large enough that "one item
    /// happened to update for another reason" cannot explain the count.</summary>
    private const int Batch = 12;

    private static int nextTick = -1;
    private static int nextUnforbidTick = -1;

    /// <summary>Ids this peer asked to allow and has not yet seen come back allowed. Carried across rounds so one run
    /// answers "did the command round trip", which a per-round fresh batch can never do.</summary>
    private static readonly List<int> tracked = new();
    private static int cleared;

    private static Thing? FindById(Map map, int id)
    {
        foreach (var thing in map.listerThings.AllThings)
            if (thing.thingIDNumber == id && thing.Spawned && !thing.Destroyed) return thing;
        return null;
    }

    private static bool IsForbidden(Thing thing) =>
        thing is ThingWithComps withComps && withComps.GetComp<CompForbiddable>() is { Forbidden: true };

    /// <summary>True when the red X is actually being drawn. Read from the comp's private overlay handle rather than
    /// inferred from the flag, because the two disagree exactly in the case the user reported.</summary>
    private static bool MarkerShown(Thing thing)
    {
        if (thing is not ThingWithComps withComps) return false;
        if (withComps.GetComp<CompForbiddable>() is not { } comp) return false;
        try
        {
            overlayHandle ??= AccessTools.Field(typeof(CompForbiddable), "overlayHandle");
            return overlayHandle?.GetValue(comp) is not null;
        }
        catch (Exception) { return false; }
    }

    private static FieldInfo? overlayHandle;
    private static int markerStale;

    internal static void Tick()
    {
        FlipOnAuthority();
    }

    /// <summary>
    /// Replica entry point. Called from the GUI/participant pump instead of <see cref="Tick"/> because a display
    /// replica is not a <see cref="HeadlessRuntime.Active"/> process: its per-frame work runs from
    /// <c>TurboGameComponent.GameComponentUpdate</c>, which returns early for the authority. Putting both probes in
    /// one guarded hook would either never run on the replica or run twice on the authority.
    /// </summary>
    internal static void TickReplica()
    {
        UnforbidOnReplica();
    }

    private static void FlipOnAuthority()
    {
        if (StartTick < 0) return;
        // Authority only. The replica runs the same code path, and a replica that flipped its own items would be
        // measuring itself: the flag would change locally and the frame would be credited with a correction it never
        // delivered.
        if (!HeadlessRuntime.ServerMode) return;
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        var now = manager.TicksGame;
        if (now < StartTick) return;
        if (nextTick < 0) nextTick = StartTick;
        if (now < nextTick) return;
        nextTick = now + Gap;

        var map = Find.Maps.FirstOrDefault();
        if (map is null) return;
        try
        {
            var flipped = 0;
            var nowForbidden = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (flipped >= Batch) break;
                // Same shape a player drags over: loose stacks on the ground, not buildings or pawns.
                if (thing.def?.category != ThingCategory.Item || !thing.Spawned || thing.Destroyed) continue;
                if (thing is not ThingWithComps withComps) continue;
                if (withComps.GetComp<CompForbiddable>() is not { } comp) continue;
                var value = !comp.Forbidden;
                // Through the setter, not the field: writing the field alone leaves the marker (an overlay handle)
                // exactly as it was, which is the bug this round exists to fix, and measuring the bug with the bug
                // in place would report success every time.
                SharedActions.SetForbiddenThroughSetter(thing, value);
                flipped++;
                if (value) nowForbidden++;
            }

            Log.Message($"[TurboTick probe] 禁止探针 tick={now} 翻转 {flipped} 件（其中改为禁止 {nowForbidden} 件）。"
                + "下一帧的物体变化里应能看到它们；副本日志的「禁止修复」应同步增长。");
        }
        catch (Exception error) { Log.Warning("[TurboTick probe] 禁止探针失败：" + error.GetType().Name + ": " + error.Message); }
    }

    /// <summary>
    /// Replica side: calls the entry point the U key uses, so the capture gate and the command round trip are both
    /// exercised without a human. Reports how many calls the gate accepted and how many it refused - a run where the
    /// gate refuses every one is the same symptom as "I pressed U and nothing happened", but with the reason attached.
    /// </summary>
    private static void UnforbidOnReplica()
    {
        if (UnforbidStart < 0) return;
        if (HeadlessRuntime.ServerMode) return;
        if (!AuthoritativeReplica.Enabled) return;
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        var now = manager.TicksGame;
        if (now < UnforbidStart) return;
        if (nextUnforbidTick < 0) nextUnforbidTick = UnforbidStart;
        if (now < nextUnforbidTick) return;
        nextUnforbidTick = now + Gap;

        var map = Find.Maps.FirstOrDefault();
        if (map is null) return;
        try
        {
            var sentBefore = SharedActions.ForbidCommandsSent;
            var forbiddenTotal = 0;
            var itemTotal = 0;
            var picked = new System.Text.StringBuilder();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.def?.category != ThingCategory.Item || !thing.Spawned || thing.Destroyed) continue;
                if (thing is not ThingWithComps withComps) continue;
                if (withComps.GetComp<CompForbiddable>() is not { } comp) continue;
                // Counted before any limit, so the two totals describe the whole map rather than the slice this round
                // touched. Without them "本端仍禁止 12 件" cannot be told apart from "本端有 3000 件被禁止，只是每轮
                // 只看前 12 件".
                itemTotal++;
                if (comp.Forbidden) forbiddenTotal++;
            }

            // The same ids are tracked across rounds instead of a fresh batch each time. Every earlier reading was
            // untraceable for exactly that reason: "本轮请求解除 12 件" named twelve items that were never looked at
            // again, so "did the answer ever come back" could not be asked at all - 翻转 0 次 was compatible with both
            // a broken round trip and a working one. Here the tracked set only shrinks when an id actually turns
            // allowed, which makes a single number answer the whole question.
            for (var i = tracked.Count - 1; i >= 0; i--)
            {
                var trackedThing = FindById(map, tracked[i]);
                if (trackedThing is null) { tracked.RemoveAt(i); continue; }
                if (!IsForbidden(trackedThing))
                {
                    tracked.RemoveAt(i);
                    cleared++;
                    // The flag turning false is only half the claim. The red X the player actually complains about is
                    // an overlay handle, and it is rebuilt only by the vanilla setter - so "cleared" on its own would
                    // stay green even if every X were still on screen. Sampled here, right after the flip, because
                    // this is the moment the marker has to be gone.
                    if (MarkerShown(trackedThing)) markerStale++;
                }
            }
            var stillForbidden = tracked.Count;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (tracked.Count >= Batch) break;
                if (thing.def?.category != ThingCategory.Item || !thing.Spawned || thing.Destroyed) continue;
                if (thing is not ThingWithComps withComps) continue;
                if (withComps.GetComp<CompForbiddable>() is not { } comp) continue;
                if (!comp.Forbidden) continue;
                if (tracked.Contains(thing.thingIDNumber)) continue;
                tracked.Add(thing.thingIDNumber);
            }

            var asked = 0;
            var unchanged = 0;
            foreach (var id in tracked)
            {
                var thing = FindById(map, id);
                if (thing is null) continue;
                if (picked.Length < 160) picked.Append(id).Append(':').Append(thing.def.defName).Append(' ');
                // Exactly what Designator_Unforbid.DesignateThing does. Two outcomes, and the flag right after the call
                // tells them apart: still true means the original was skipped, either because the capture prefix turned
                // this into a command or because the gate refused it outright.
                thing.SetForbidden(false, warnOnFail: false);
                asked++;
                if (IsForbidden(thing)) unchanged++;
            }

            // Flushed here rather than left to the frame's own flush, so the count below answers "did a command leave
            // this machine" in this same round instead of a frame later.
            SharedActions.FlushForbidBatches();
            var sent = SharedActions.ForbidCommandsSent - sentBefore;
            var queued = SharedActions.FlushPendingForbidCount();
            // Four numbers, and the first is the only one that can prove success: cleared > 0 means a command left this
            // peer and the flag came back allowed - the whole round trip, marker included. unchanged > 0 means the gate
            // refused the call (the WARN above says which condition). asked-with-nothing-sent means it was captured
            // but never delivered.
            Log.Message($"[TurboTick probe] 解除探针 tick={now} 本端物品 {itemTotal} 件其中禁止 {forbiddenTotal} 件，"
                + $"跟踪 {tracked.Count} 件（累计已变允许 {cleared} 件，仍禁止 {stillForbidden} 件，变允许后标记仍画 {markerStale} 件），"
                + $"本轮请求解除 {asked} 件（本地未变 {unchanged} 件），本轮发出命令 {sent} 条（仍排队 {queued}），"
                + $"累计发出 {SharedActions.ForbidCommandsSent} 条。目标 id:def = {picked}");
        }
        catch (Exception error) { Log.Warning("[TurboTick probe] 解除探针失败：" + error.GetType().Name + ": " + error.Message); }
    }
}
