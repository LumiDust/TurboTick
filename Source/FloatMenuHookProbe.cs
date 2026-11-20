using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Reports whether the right-click chokepoint is attached, and what it would sample.
///
/// <para><b>What this probe deliberately does not claim.</b> A right-click cannot be synthesised from a console command:
/// `FloatMenuOption.Chosen` runs a closure that only exists while a real `FloatMenu` is on the stack, hit-tested against
/// a real cursor. So this probe never reports "N clicks synced". What it can measure is the two halves that fail
/// independently:</para>
/// <list type="number">
/// <item><b>Is the patch attached?</b> `Harmony.GetPatchInfo` answers that. A patch that silently failed to install and a
/// player who has not right-clicked yet produce the same zero in any click counter, and they need completely different
/// fixes.</item>
/// <item><b>Would the capture pick anything up?</b> Running the real sampler against the current selection plus a named
/// target measures the sampling shape without needing a click - and "the sampler returns nothing" is a real defect that
/// no click would ever fix.</item>
/// </list>
///
/// <para>The third half - whether the published fragment reaches the authority - is already covered by the `thing-state`
/// channel, which the gizmo path exercises every time a player flicks a filter. This probe does not re-test it.</para>
///
/// <para><b>Where it ran.</b> `SharedActions.PlayerActingNote()` is printed unconditionally. On a dedicated server
/// `PlayerActing` is always false, so the sampler returns null by design; a probe that hid that would be reporting its own
/// gate as if it were the feature's state. That mistake has been made five times in this project.</para></summary>
internal static class FloatMenuHookProbe
{
    internal static string Probe(string? argument)
    {
        // The instance id, read off the live Harmony object rather than duplicated as a literal: a second copy of this
        // string had already drifted once in `ModPatchAudit`.
        var ours = TurboTickMod.Harmony?.Id ?? string.Empty;
        var report = new StringBuilder();
        report.Append("右键菜单捕获：");

        // 1. Attached?
        var method = AccessTools.Method(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen),
            [typeof(bool), typeof(FloatMenu)]);
        if (method is null)
        {
            report.Append("`FloatMenuOption.Chosen(bool, FloatMenu)` 不存在 —— 捕获无法挂载");
            return report.ToString();
        }
        var info = Harmony.GetPatchInfo(method);
        var prefixes = info?.Prefixes.Count(p => string.Equals(p.owner, ours, StringComparison.Ordinal)) ?? 0;
        var postfixes = info?.Postfixes.Count(p => string.Equals(p.owner, ours, StringComparison.Ordinal)) ?? 0;
        report.Append(prefixes > 0 && postfixes > 0
            ? $"已挂载（前缀 {prefixes} / 后缀 {postfixes}）"
            : $"**未挂载**（前缀 {prefixes} / 后缀 {postfixes}）");

        // 2. What would the sampler pick up?
        var selected = Find.Selector?.SelectedObjects;
        var selectedThings = 0;
        var selectedZones = 0;
        var skipped = 0;
        if (selected is not null)
            foreach (var entry in selected)
            {
                if (entry is Zone) selectedZones++;
                else if (entry is Blueprint or Frame) skipped++;
                else if (entry is Thing) selectedThings++;
            }
        report.Append($"；当前选中 {selectedThings} 个物体 / {selectedZones} 个区域");
        if (skipped > 0) report.Append($"（跳过蓝图/框架 {skipped} 个）");

        // A named click target, so the "right-clicking a stack selects the pawn instead" case is visible: the sampler has
        // to pick up the stack through `revalidateClickTarget`, not through the selection.
        Thing? target = null;
        if (argument is { Length: > 0 } && int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var id))
            target = FindAnyThing(id);
        if (target is not null)
            report.Append($"，指定目标 #{target.thingIDNumber}({target.def?.defName})");

        // Run the real sampler. It returns null when `PlayerActing` is false, which is the gate and not the feature.
        var snapshots = SharedActions.CaptureFloatMenuTargets(target);
        report.Append(snapshots is null
            ? "；采样返回 null（**这是闸门拒绝，不是采样失败**）"
            : $"；采样得到 {snapshots.Count} 个对象"
              + (snapshots.Count > 0
                  ? "（" + string.Join(",", snapshots.Select(s => s.Kind + "#" + s.Id)) + "）"
                  : ""));

        // 3. Counters so far, and where this ran.
        report.Append($";累计发布 {SharedActions.GizmoStatesSent} 份状态片段"
            + $"，右键选项改动了 {SharedActions.FloatMenuChoicesSent} 次 / 无可采样 {SharedActions.FloatMenuChoicesEmpty} 次"
            + (SharedActions.LastFloatMenuNote is { Length: > 0 } note ? $"（最近：{note}）" : ""));
        report.Append("；").Append(SharedActions.PlayerActingNote());
        return report.ToString();
    }

    private static Thing? FindAnyThing(int id)
    {
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing.thingIDNumber == id) return thing;
        return null;
    }

    internal static string Summary()
    {
        var method = AccessTools.Method(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen),
            [typeof(bool), typeof(FloatMenu)]);
        var info = method is null ? null : Harmony.GetPatchInfo(method);
        var attached = info is not null && info.Prefixes.Count > 0;
        return (attached ? "已挂载" : "**未挂载**")
            + $" / 改了东西 {SharedActions.FloatMenuChoicesSent} 次，无可采样 {SharedActions.FloatMenuChoicesEmpty} 次"
            + $" / `thing-state` 共发 {SharedActions.GizmoStatesSent} 份";
    }
}
