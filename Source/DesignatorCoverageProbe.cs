using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Reports, for every loaded <c>Designator</c> type, whether committing a cell would publish a command.
///
/// <para><b>The gap this answers.</b> `DesignatorSync.EncodeChunks` covers two shapes - a building blueprint and a zone
/// drag - and returns an empty chunk list for everything else. There are 82 <c>Designator</c> types in this build
/// (`grep 'class Designator.* : Designator'` over the 1.7 dump), so "the other 80 publish nothing" is either a
/// deliberate decision per type or an unnoticed hole, and from the outside the two look identical. Two mechanisms make
/// the ambiguity worse:</para>
/// <list type="bullet">
/// <item><c>DesignationManager.AddDesignation</c> is patched (<c>DesignationCapturePatch</c>), so any designator whose
/// body calls it is covered even though `EncodeChunks` never sees it - Deconstruct, Hunt, Haul, ExtractTree and the
/// rest. Their absence from <see cref="DesignatorSync"/> is correct.</item>
/// <item>A designator whose body writes a field directly (Forbid through <c>ForbidUtility.SetForbidden</c>, Adopt
/// through <c>SetFaction</c>) is covered only if that writer has its own patch. Forbid does
/// (<c>forbid-batch</c>); Adopt does not, so it changes a pawn's faction, claims a bed and sends a letter on one peer
/// only.</item>
/// </list>
///
/// <para><b>How this measures rather than guesses.</b> For each discovered type it builds the designator, picks a cell
/// that its own <c>CanDesignateCell</c> accepts, records the publish counter, commits one cell, and reports the delta.
/// Nothing is inferred from a method name: a designator that publishes is counted as publishing whether or not this mod
/// was expected to cover it.</para>
///
/// <para><b>What a zero does not prove.</b> A type may publish nothing because it is display-only
/// (<c>Designator_Eyedropper</c>, <c>Designator_Cancel</c>), because every candidate cell was refused, or because the
/// gate refused the capture. Each row therefore also reports the refusal count, and a type with no accepted cell is
/// reported as untested rather than as a hole.</para></summary>
internal static class DesignatorCoverageProbe
{
    /// <summary>Reports which designator commit methods carry a Harmony patch, read from Harmony's own patch table.
    ///
    /// <para><b>Why this does not try to commit a cell.</b> The first version did, and it answered "有产出 0 个" for every
    /// type. That is uninformative rather than alarming: this command runs on the dedicated server, where
    /// <c>SharedActions.PlayerActing</c> is false by construction - <c>SimulationPeerProcess</c> is true because the
    /// server refuses every command a simulation peer sends - so no capture path executes here at all. Zero would have
    /// been the reading for a perfectly covered designator on a real client, and printing it beside real numbers made it
    /// look like evidence. Same failure as `floatmenu-probe` and `comp-probe`: an instrument that answers confidently
    /// from insufficient information.</para>
    ///
    /// <para><b>What is measurable from here.</b> Whether a type's own commit method carries a prefix/postfix. A
    /// designator with a patched commit is captured by definition, because the prefix runs before the body. One without
    /// may still be covered through a patched callee - <c>DesignationManager.AddDesignation</c> and
    /// <c>ForbidUtility.SetForbidden</c> are both patched - so this reports those two separately instead of guessing per
    /// type which body calls what.</para></summary>
    internal static string Probe(string? argument)
    {
        if (Find.CurrentMap is null) return "没有当前地图";
        var text = new StringBuilder();
        text.Append($"本进程 simulationPeer={SharedActions.SimulationPeerProcess}"
            + (SharedActions.SimulationPeerProcess
                ? "（为真：捕获路径不执行，只读补丁表）"
                : "（为假：捕获路径会执行）") + "；");

        var types = DesignatorSingleCellPatch.DesignatorSubclasses();
        var patched = new List<string>();
        var bare = new List<string>();
        foreach (var type in types)
        {
            if (type == typeof(Designator_Build) || type == typeof(Designator_Cancel)) continue;
            var names = new List<string>();
            foreach (var (name, parameters) in new (string, Type[])[]
                     {
                         ("DesignateSingleCell", [typeof(IntVec3)]),
                         ("DesignateThing", [typeof(Thing)]),
                         ("DesignateMultiCell", [typeof(IEnumerable<IntVec3>)]),
                     })
            {
                System.Reflection.MethodInfo? method;
                try { method = AccessTools.DeclaredMethod(type, name, parameters); }
                catch (Exception) { continue; }
                if (method is null || method.IsAbstract) continue;
                HarmonyLib.Patches? info;
                try { info = Harmony.GetPatchInfo(method); }
                catch (Exception) { continue; }
                if (info is null) continue;
                if (info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count > 0) names.Add(name);
            }
            if (names.Count > 0) patched.Add($"{type.Name}({string.Join("+", names)})");
            else bare.Add(type.Name);
        }

        text.Append($"Designator 类型 {types.Count} 个；自有补丁 {patched.Count} 个：{string.Join(" ", patched)}；");
        text.Append($"自身无补丁 {bare.Count} 个：{string.Join(" ", bare)}；");
        // The two callees that route most of the bare ones. Read from the same table, so this is a measurement rather
        // than a claim about which body calls what.
        text.Append("经 AddDesignation 中转的补丁：" + PatchCounts(typeof(DesignationManager), "AddDesignation",
            [typeof(Designation)]) + "；");
        text.Append("经 SetForbidden 中转的补丁：" + PatchCounts(typeof(ForbidUtility), "SetForbidden",
            [typeof(Thing), typeof(bool), typeof(bool)]) + "。");
        // Three further buckets, each established by reading the body, so a bare type is not read as a hole by default.
        // ① paint designators write `Map.areaManager.BuildRoof/NoRoof/SnowOrSandClear/PollutionClear[c]`
        //    (`:618264-618267`, `:618410-618413`, `:618461`, `:618514`, `:618591`) - the built-in areas, carried whole by
        //    `AreaSync`. Their body is one indexer write, so no command is needed and none exists.
        // ② the allowed-area paint carries `AreaAllowedSync` + `area-paint`.
        // ③ `Designator_Adopt` writes `t.SetFaction` and `CompAssignableToPawn_Bed.TryAssignPawn` (`:618102-618128`) and
        //    publishes nothing; the faction half now travels through `ReplicaPawn.FactionLoadId`, the bed assignment has
        //    no channel at all.
        text.Append("按区域整表覆盖（自身无需命令）：AreaHomeExpand/Clear、AreaBuildRoof、AreaNoRoof、AreaIgnoreRoof、"
            + "AreaSnowClearExpand/Clear、AreaPollutionClearExpand/Clear（写 areaManager 内建区域，由 AreaSync 整张送）；");
        text.Append("按 region 表覆盖：AreaAllowedExpand/Clear（AreaAllowedSync + area-paint）；");
        text.Append("真实缺口：Adopt（占床无通路，派系那半经 FactionLoadId 到达）。");
        return text.ToString();
    }

    private static string PatchCounts(Type owner, string name, Type[] parameters)
    {
        System.Reflection.MethodInfo? method;
        try { method = AccessTools.Method(owner, name, parameters); }
        catch (Exception error) { return "查不到方法(" + error.GetType().Name + ")"; }
        if (method is null) return "查不到方法";
        HarmonyLib.Patches? info;
        try { info = Harmony.GetPatchInfo(method); }
        catch (Exception error) { return "查不到补丁(" + error.GetType().Name + ")"; }
        if (info is null) return "无";
        return $"前缀{info.Prefixes.Count}/后缀{info.Postfixes.Count}/转译{info.Transpilers.Count}";
    }
}
