using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Round-trips the state a `Dialog_CreateXenogerm` writes, without opening the dialog.
///
/// <para><b>The entry this exists for.</b> `Dialog_CreateXenogerm.Accept` (`:630275`) is a button in a modal window, so
/// `GizmoStatePatch` - which samples the selected thing around a gizmo's `ProcessInput` - never sees it. The chain is
/// `Accept` → `StartAssembly` (`:630287`) → `GeneAssembler.Start` (`:581172`), which writes, in order:
/// `genepacksToRecombine`, `architesRequired`, `xenotypeName`, `iconDef`, `workingInt = true`, `totalWorkRequired`
/// (`:581174-581180`).</para>
///
/// <para><b>Why the answer is probably "the building's own fragment carries it".</b>
/// `Building_GeneAssembler.ExposeData` (`:581416-581432`) scribes every one of those fields, and the building sweep
/// (`Source/HeadlessRuntime.cs` `SweepBuildingStates`) re-serialises every `BuildingArtificial` in slices of 24 and sends
/// whatever differs. So the channel exists; the open question is only whether those fields really appear in the fragment
/// and really change - which is what this measures, and what a "the dialog is not hooked" note cannot tell you.</para>
///
/// <para><b>What it cannot show.</b> The sweep is rotating, so the *latency* between the button and the frame is one sweep
/// cycle, not one frame. On a map with this many buildings that is a real delay and this probe does not quantify it -
/// saying so is better than reporting a green tick that means "eventually".</para></summary>
internal static class XenogermProbe
{
    private static readonly string[] Fields =
        ["genepacksToRecombine", "architesRequired", "xenotypeName", "iconDef", "workingInt", "totalWorkRequired"];

    internal static string Probe(string? argument)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            Building_GeneAssembler? assembler = null;
            foreach (var thing in map.listerThings.AllThings)
                if (thing is Building_GeneAssembler found) { assembler = found; break; }
            if (assembler is null) return "本图没有异种胚装配器；先跑 transport-fixture";

            var before = ScribeBridge.Save(assembler) ?? "";
            var beforeHits = new List<string>();
            foreach (var field in Fields) if (before.Contains("<" + field, StringComparison.Ordinal)) beforeHits.Add(field);

            // The same call the dialog's Accept makes, with the smallest legal arguments: no packs, no archites, a name
            // and the default icon. `Start` calls `Reset()` first, so a previously finished assembly cannot mask this.
            var wasWorking = assembler.Working;
            assembler.Start([], 0, "turbotick-probe", XenotypeIconDefOf.Basic);
            var after = ScribeBridge.Save(assembler) ?? "";
            var afterHits = new List<string>();
            foreach (var field in Fields) if (after.Contains("<" + field, StringComparison.Ordinal)) afterHits.Add(field);

            // A field that appears in both fragments but with the same text did not travel a change; only `workingInt`
            // and `totalWorkRequired` are expected to move on an empty pack list.
            var changed = new List<string>();
            foreach (var field in Fields)
            {
                var a = Extract(before, field);
                var b = Extract(after, field);
                if (a != b) changed.Add(field + " " + (a ?? "无") + "→" + (b ?? "无"));
            }
            // **Restore with `Reset()`, not with another `Start`.** The first version "undid" the probe by calling
            // `Start([], 0, null, Basic)` - which runs `Reset()` (`:581285`) and then sets `workingInt = true` again
            // (`:581179`), so it left the assembler working with an empty pack list. Measured: `复原后 workingInt=True`
            // on a machine that had been idle. `Reset` is private, so it is invoked reflectively; the restore is then
            // verified by comparing the fragment with the one captured before the probe ran.
            var restoreMethod = HarmonyLib.AccessTools.Method(typeof(Building_GeneAssembler), "Reset");
            restoreMethod?.Invoke(assembler, null);
            var restore = ScribeBridge.Save(assembler) ?? "";
            var exact = restore == before;
            var restored = Extract(restore, "workingInt");
            // **What `Reset` does not undo, named.** It clears `workingInt`, `genepacksToRecombine`, `xenotypeName`,
            // `workDone`, `lastWorkedTick` and `architesRequired` (`:581285-581295`) but leaves `totalWorkRequired` and
            // `iconDef` alone, so a probe that only compares whole fragments reports "differs" without saying why - and
            // the residue is then indistinguishable from a probe that failed to restore at all.
            var residue = new List<string>();
            foreach (var field in Fields)
            {
                var a = Extract(before, field);
                var b = Extract(restore, field);
                if (a != b) residue.Add(field + " " + (a ?? "无") + "→" + (b ?? "无"));
            }

            return $"装配器 {assembler.LabelShort}：片段 {before.Length}→{after.Length} B；"
                + $"字段在片段中 {beforeHits.Count}/{Fields.Length}（{string.Join(",", beforeHits)}）；"
                + $"调用 Start 后变化 {changed.Count} 个：{(changed.Count == 0 ? "无" : string.Join("；", changed))}；"
                + $"原先 working={wasWorking}，复原后 workingInt={restored ?? "无"}"
                + (exact
                    ? "，复原片段与探测前逐字节一致"
                    : "，复原后仍有差异：" + (residue.Count == 0 ? "无字段差异（长度不同）" : string.Join("；", residue))
                      + " —— `Reset` 不清 `totalWorkRequired`/`iconDef`，这是它的边界而不是探针失败");
        }
        catch (Exception error) { return "探针抛异常：" + error.GetType().Name + " " + error.Message; }
    }

    /// <summary>Whether the xenogerm item's own fragment carries `targetPawn`, and whether it moves.
    ///
    /// <para><b>Why this is the other half of the same entry.</b> `Xenogerm.SetTargetPawn` (`:596878-596899`) writes
    /// three things: it deletes the old target's bill, it creates a surgery bill on the new target and assigns
    /// `bill.xenogerm = this`, and it sets `targetPawn = newTarget` on the item. The bill lives on the pawn and is
    /// covered by `XenogermRetargetCapturePatch`; `targetPawn` lives on the item and is scribed by
    /// `Xenogerm.ExposeData` (`:597090`). "The item and the pawn are two objects" is the same shape as the assignable
    /// case: sampling only one leaves the other pointing at the old value.</para>
    ///
    /// <para><b>Why it sets the field instead of calling `SetTargetPawn`.</b> That method opens
    /// `Dialog_MessageBox.CreateConfirmation` (`:596889`) and performs every write inside the confirm callback - a
    /// headless probe cannot press it, and pressing it would be a different test. This measures the channel (does the
    /// fragment carry the field), not the dialog.</para></summary>
    internal static string Target(string? argument)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            Xenogerm? item = null;
            foreach (var thing in map.listerThings.AllThings)
                if (thing is Xenogerm found) { item = found; break; }
            if (item is null) return "本图没有异种胚；先跑 transport-fixture Xenogerm";
            Pawn? colonist = null;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) { colonist = pawn; break; }
            if (colonist is null) return "本图没有殖民者，无法指定植入目标";

            var field = HarmonyLib.AccessTools.Field(typeof(Xenogerm), "targetPawn");
            if (field is null) return "找不到 Xenogerm.targetPawn 字段";
            var before = ScribeBridge.Save(item) ?? "";
            var original = field.GetValue(item) as Pawn;
            field.SetValue(item, colonist);
            var after = ScribeBridge.Save(item) ?? "";
            field.SetValue(item, original);
            var restored = ScribeBridge.Save(item) ?? "";

            var hadBefore = before.Contains("<targetPawn", StringComparison.Ordinal);
            var hasAfter = after.Contains("<targetPawn", StringComparison.Ordinal);
            var afterId = Extract(after, "targetPawn");
            var restoreId = Extract(restored, "targetPawn");
            return $"异种胚 {item.LabelShort}：片段 {before.Length}→{after.Length} B；"
                + $"写入前有 targetPawn 元素 {hadBefore}，写入后有 {hasAfter}（值 {afterId ?? "无"}）；"
                + $"指向殖民者 #{colonist.thingIDNumber}；复原后 targetPawn {restoreId ?? "无"}"
                + $"（原值 {original?.thingIDNumber.ToString() ?? "null"}）"
                + (restored == before ? "，复原片段逐字节一致" : "，复原片段与写入前不同");
        }
        catch (Exception error) { return "探针抛异常：" + error.GetType().Name + " " + error.Message; }
    }

    /// <summary>The text of one top-level element in a Scribe fragment, or null when the element is absent.
    ///
    /// <para>Deliberately a substring scan rather than XML parsing: the fragments here come from `ScribeBridge.Save`, and
    /// a parser would be a second thing that can be wrong about the same bytes.</para></summary>
    private static string? Extract(string fragment, string field)
    {
        var open = "<" + field;
        var start = fragment.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        var valueStart = fragment.IndexOf('>', start);
        if (valueStart < 0) return null;
        var selfClosed = valueStart > 0 && fragment[valueStart - 1] == '/';
        if (selfClosed) return "";
        var end = fragment.IndexOf("</" + field + ">", valueStart, StringComparison.Ordinal);
        if (end < 0) return null;
        return fragment.Substring(valueStart + 1, end - valueStart - 1);
    }
}
