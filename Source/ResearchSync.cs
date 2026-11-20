using System;
using System.Collections.Generic;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Research progress, which a replica freezes at the load-time value.
///
/// <para><b>Why a replica needs this.</b> Progress is accumulated by <c>ResearchManager.AddProgress</c>, called from the
/// <c>Study</c> and <c>Research</c> work toils - ticks a replica does not run. So the research tab shows whatever percentage
/// the save was loaded with, forever: a project the host finished is still at 30% on the replica, and the little progress
/// bar next to the project name never moves. Two players looking at the same colony see different answers to "how much
/// longer", which is worse than no bar at all because both of them are looking at something.</para>
///
/// <para><b>Why the current project and the progress are separate facts.</b> Switching a project is an event the command
/// channel already carries (<c>SetCurrentProject</c> is patched), but progress is a number that only ever grows, and a
/// project can be finished by a techprint without any work at all. Sending them together would mean a project switch had to
/// recompute progress for a project the replica does not have yet.</para>
///
/// <para><b>Why a per-project dictionary and not one current value.</b> Research is cumulative: a colony accumulates partial
/// progress on several projects, and the tab shows a bar for each one the player has started. Sending only the current
/// project's progress would leave every other started project frozen - and a player switching back to one of them would see
/// the bar jump backwards.</para>
internal static class ResearchSync
{
    /// <summary>Last progress sent, keyed by project def name. A colony researches a few dozen projects over its life, so this
    /// dictionary is bounded by the project count rather than by time.</summary>
    private static readonly Dictionary<string, int> sent = [];

    internal static long ProgressSent { get; private set; }
    internal static long ProgressApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Collects projects whose whole-percent progress changed.
    ///
    /// <para>Quantised to whole percent because that is what the tab shows: a bar a few hundred pixels wide cannot display
    /// more, and a raw float would put an entry per started project on every frame - research accrues continuously, so
    /// <i>every</i> frame would differ.</para></summary>
    internal static List<ReplicaResearch>? Publish()
    {
        List<ReplicaResearch>? changed = null;
        try
        {
            var manager = Find.ResearchManager;
            if (manager is null) return null;
            foreach (var project in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
            {
                if (project?.defName is not { Length: > 0 } name) continue;
                var progress = manager.GetProgress(project);
                // A project at zero has never been touched. It is also the state the replica starts in, so sending it would be
                // a large first frame for nothing - and `sent` not having an entry is what marks "never sent".
                var percent = progress <= 0f ? 0 : (int)Math.Round(progress / Math.Max(1f, project.baseCost) * 100f);
                if (percent == 0 && !sent.ContainsKey(name)) continue;
                if (sent.TryGetValue(name, out var previous) && previous == percent) continue;
                sent[name] = percent;
                (changed ??= []).Add(new ReplicaResearch(name, percent));
                ProgressSent++;
            }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] research publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>Applies one project's progress.
    ///
    /// <para>Writes through <c>AddProgress</c> rather than the private dictionary because that method also fires the
    /// completion path once a project reaches its cost: a replica that received 100% would otherwise show a finished bar and
    /// leave the project uncompleted, so the tech would be invisible in the tree and unavailable in the bills.</para>
    ///
    /// <para><c>AddProgress</c> only adds, so the delta is computed against the replica's own reading. A negative delta is
    /// skipped rather than applied: progress in this game never goes backwards, and a replica whose number is somehow ahead
    /// of the authority must not be pulled back through a method that cannot express it.</para></summary>
    internal static void Apply(ReplicaResearch state)
    {
        try
        {
            var manager = Find.ResearchManager;
            if (manager is null) return;
            var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(state.Project);
            if (project is null) { ApplyFailures++; return; }
            if (project.IsFinished) return;
            var target = state.Percent / 100f * Math.Max(1f, project.baseCost);
            var delta = target - manager.GetProgress(project);
            // A tenth of a percent is the smallest change a bar can show; applying anything smaller would call into the
            // completion path for nothing on every frame.
            if (delta < project.baseCost * 0.001f) return;
            manager.AddProgress(project, delta);
            ProgressApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] research apply failed (" + state.Project + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => sent.Clear();

    internal static string Summary() =>
        $"研究进度 发出 {ProgressSent} / 应用 {ProgressApplied}（失败 {ApplyFailures}）";
}
