using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Runs one complete building sweep on demand and reports what it found.
///
/// <para><b>Why this is needed and not just a debugging convenience.</b> The sweep runs inside <c>PublishFrame</c>, which
/// <c>SharedColony.OnLocalTick</c> drives - and a room with no replicas is paused by design
/// (<c>PauseWithoutReplicas</c> in <c>Networking/DedicatedServer.cs</c>), so on an idle server the sweep never runs at all.
/// That makes the one thing its own log can report unanswerable: "swept 0 buildings" means either "it ran and found
/// nothing" or "it never ran", and on a paused server it is always the second. Measured on this save, the census showed
/// <c>扫 0 栋</c> next to <c>建筑 506 栋</c>.</para>
///
/// <para><b>What a full pass proves.</b> That every building in the list can be serialised, and how many of them differ from
/// what was last sent. A save failure on an unusual building is the failure this exists to surface: the sweep catches the
/// exception and only counts it, so a building type that cannot be serialised simply never reaches a replica and reports
/// nothing anywhere.</para>
///
/// <para><b>Why it mutates the send state.</b> The pass writes <c>lastBuildingState</c>, which is what a later real frame
/// compares against. That is deliberate and is what makes the answer meaningful: the number of "changed" buildings here is
/// exactly the number the next frame will carry. Running it while a replica is connected would therefore deliver those
/// changes in the next frame rather than the one after - which is sooner, not wrong.</para>
internal static class SweepProbe
{
    internal static string RunOnce()
    {
        var map = Find.CurrentMap;
        if (map is null) return "补扫一次：没有运行中的地图";
        // Build the region/room topology first if it has never been built. The grid is lazy: the constructor only allocates
        // an array, and regions appear when something asks for one (`GetValidRegionAt` calls
        // `TryRebuildDirtyRegionsAndRooms`). On a paused server nothing asks, so `AllRooms` is empty and - since every
        // temperature probe reads through a room - so is any room question. Measured: `区 0 / 共 0 间` next to
        // 506 artificial buildings on the same map.
        var roomsBefore = 0;
        try { roomsBefore = map.regionGrid?.AllRooms?.Count ?? 0; } catch (Exception) { }
        // `RebuildAllRegionsAndRooms` logs a warning and `TryRebuildDirtyRegionsAndRooms` returns immediately when the
        // updater is disabled, so calling it and reporting "rebuilt" would be a claim the method never made. Report the
        // flag that decides it, and say only what actually happened.
        var updaterEnabled = map.regionAndRoomUpdater?.Enabled ?? false;
        var attempted = false;
        if (roomsBefore == 0 && updaterEnabled)
        {
            attempted = true;
            try { map.regionAndRoomUpdater?.RebuildAllRegionsAndRooms(); }
            catch (Exception error)
            {
                return "补扫一次：区域网格重建失败 " + error.GetType().Name + ": " + error.Message;
            }
        }
        var roomsAfter = 0;
        try { roomsAfter = map.regionGrid?.AllRooms?.Count ?? 0; } catch (Exception) { }
        var gridNote = !updaterEnabled
            ? "（更新器已禁用，无法重建）"
            : attempted ? "（已尝试重建）" : "";
        var listed = 0;
        var serialised = 0;
        var changed = 0;
        var failed = 0;
        var byDef = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                // Two separate tests: a combined `is not X or not Y z` cannot declare `z` (CS8780), because the declaration
                // would be reachable through a branch where the pattern did not match.
                if (thing is not Building) continue;
                if (thing is not IExposable exposable) continue;
                listed++;
                string? state;
                try { state = ScribeBridge.Save(exposable); }
                catch (Exception error)
                {
                    failed++;
                    // One message per def, not per object: a type that cannot be serialised fails for every instance, and a
                    // hundred identical lines would bury the one fact worth reading.
                    var name = thing.def?.defName ?? "?";
                    if (!failures.ContainsKey(name))
                        failures[name] = error.GetType().Name + ": " + error.Message;
                    continue;
                }
                if (state is null) continue;
                serialised++;
                if (HeadlessRuntime.LastSentBuildingState(thing.thingIDNumber) == state) continue;
                changed++;
                // Recorded, not just compared: a probe that only read the table would report every building as changed on
                // every run, which says nothing about whether the comparison works - and the comparison is the mechanism the
                // real sweep depends on.
                HeadlessRuntime.RecordBuildingState(thing.thingIDNumber, state);
                byDef[thing.def?.defName ?? "?"] = byDef.TryGetValue(thing.def?.defName ?? "?", out var seen) ? seen + 1 : 1;
            }
        }
        catch (Exception error)
        {
            return "补扫一次：遍历失败 " + error.GetType().Name + ": " + error.Message;
        }
        var text = $"补扫一次：列出 {listed} 栋，可序列化 {serialised} 栋，与上次不同 {changed} 栋，保存失败 {failed} 栋"
            + $"；区域网格 {roomsBefore} → {roomsAfter} 间{gridNote}";
        if (byDef.Count > 0)
            text += "；有变化的类型 " + string.Join(",", byDef.OrderByDescending(pair => pair.Value).Take(6)
                .Select(pair => pair.Key + "=" + pair.Value));
        foreach (var (def, message) in failures)
            text += $"\n  失败 {def}：{message}";
        return text;
    }

    /// <summary>Changes one field on one building, then runs the sweep to see whether it is noticed.
    ///
    /// <para><b>Why the existing probe is not enough.</b> <c>RunOnce</c> reports "how many differ from last time", and its
    /// second pass reads 0 - which is the reading for a working comparison and, identically, for a comparison that was never
    /// made. This perturbs a real field and reports three numbers: the sweep before the change, after it, and the type of the
    /// building that was touched.</para>
    ///
    /// <para>Targets <c>CompExplosive.wickTicksLeft</c>, which the census measured at 13 instances on this save (there is
    /// not a single <c>CompRefuelable</c> anywhere on it) and which is scribed through <c>PostExposeData</c>. A map without
    /// one is reported rather than passed off as a successful nudge.</para></summary>
    internal static string NudgeAndSweep()
    {
        Building? target = null;
        CompExplosive? explosive = null;
        // Which comps exist at all, printed when nothing could be nudged: "no CompRefuelable on this map" and "the comp
        // lookup is wrong" are both an empty result, and on a save of ruins only the former is true.
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                if (thing is not Building building || building.Destroyed) continue;
                if (building is ThingWithComps withComps)
                    foreach (var comp in withComps.AllComps)
                    {
                        if (comp is null) continue;
                        var name = comp.GetType().Name;
                        available[name] = available.TryGetValue(name, out var seen) ? seen + 1 : 1;
                    }
                if (explosive is not null) continue;
                var found = building.GetComp<CompExplosive>();
                if (found is null) continue;
                target = building;
                explosive = found;
            }
        }
        // The sweep has to be told what it last saw before it can report a difference; without a warm pass the first read
        // would report every building as changed and prove nothing.
        var warm = RunOnce();
        if (target is null || explosive is null)
        {
            var comps = available.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(12).Select(pair => pair.Key + "=" + pair.Value).ToArray();
            return "扰动探针：这张图上没有带 CompExplosive 的建筑，无法扰动（" + FirstLine(warm) + "）；"
                + $"现有组件 {available.Count} 种：{(comps.Length == 0 ? "无" : string.Join(", ", comps))}";
        }
        // `wickTicksLeft` is a plain public int that `CompExplosive.PostExposeData` scribes, so a change to it must appear
        // in the fragment the sweep compares - which is exactly the claim under test.
        var before = explosive.wickTicksLeft;
        explosive.wickTicksLeft = before + 1234;
        var after = explosive.wickTicksLeft;
        var nudged = RunOnce();
        var again = RunOnce();
        return $"扰动探针：扰动 {target.def?.defName ?? "?"} #{target.thingIDNumber} 的 wickTicksLeft {before} → {after}；"
            + $"扰动前一轮：{FirstLine(warm)}；扰动后：{FirstLine(nudged)}；再一轮：{FirstLine(again)}。"
            + (after == before ? " **字段没变，这次扰动什么也没证明**" : "");
    }

    private static string FirstLine(string report)
    {
        var index = report.IndexOf('；');
        return index > 0 ? report[..index] : report;
    }
}
