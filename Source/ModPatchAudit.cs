using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Names the third-party mods that patched a method the shared timeline depends on.
///
/// A shared colony needs two peers to run the same simulation, so any patch that changes when or how often
/// a tick, an update loop or the random stream runs breaks that without being a bug in TurboTick. Those
/// failures are otherwise indistinguishable from a TurboTick defect: the report says only "位置,工作 differ".
/// Harmony already knows every patch and who installed it, so the audit can put a mod name next to the
/// symptom instead of leaving the player to bisect their mod list.
///
/// The audit only reads and logs; it never blocks a mod, because most patches are harmless and refusing
/// them would be worse than the occasional desync.
/// </summary>
internal static class ModPatchAudit
{
    private static bool ran;

    /// <summary>Methods whose patching changes the simulation itself rather than its presentation.</summary>
    private static IEnumerable<(string Label, MethodBase? Method)> CriticalTargets()
    {
        yield return ("TickManager.DoSingleTick", AccessTools.Method(typeof(TickManager), "DoSingleTick"));
        yield return ("TickManager.TickManagerUpdate", AccessTools.Method(typeof(TickManager), "TickManagerUpdate"));
        yield return ("Game.UpdatePlay", AccessTools.Method(typeof(Game), "UpdatePlay"));
        yield return ("Map.MapUpdate", AccessTools.Method(typeof(Map), "MapUpdate"));
        yield return ("TickList.Tick", AccessTools.Method(typeof(TickList), "Tick"));
        yield return ("GenTicks.GetCameraUpdateRate", AccessTools.Method(typeof(GenTicks), "GetCameraUpdateRate", [typeof(Thing)]));
        yield return ("GameComponentUtility.GameComponentUpdate",
            AccessTools.Method(typeof(GameComponentUtility), "GameComponentUpdate"));
        yield return ("MapComponentUtility.MapComponentUpdate",
            AccessTools.Method(typeof(MapComponentUtility), "MapComponentUpdate", [typeof(Map)]));
        yield return ("WorldComponentUtility.WorldComponentUpdate",
            AccessTools.Method(typeof(RimWorld.Planet.WorldComponentUtility), "WorldComponentUpdate",
                [typeof(RimWorld.Planet.World)]));
        yield return ("Rand.Value", AccessTools.PropertyGetter(typeof(Rand), "Value"));
        yield return ("Rand.Range(int,int)", AccessTools.Method(typeof(Rand), "Range", [typeof(int), typeof(int)]));
        yield return ("Rand.StateCompressed", AccessTools.PropertyGetter(typeof(Rand), "StateCompressed"));
    }

    /// <summary>Runs once per process, when a shared colony first becomes active.</summary>
    internal static void Run()
    {
        if (ran) return;
        ran = true;
        try
        {
            var ours = TurboTickMod.Harmony?.Id ?? "lumidust.turbotick";
            var findings = new List<string>();
            foreach (var (label, method) in CriticalTargets())
            {
                if (method is null) continue;
                var info = Harmony.GetPatchInfo(method);
                if (info is null) continue;
                var owners = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var patch in info.Prefixes) if (!string.Equals(patch.owner, ours, StringComparison.Ordinal)) owners.Add(patch.owner);
                foreach (var patch in info.Postfixes) if (!string.Equals(patch.owner, ours, StringComparison.Ordinal)) owners.Add(patch.owner);
                foreach (var patch in info.Transpilers) if (!string.Equals(patch.owner, ours, StringComparison.Ordinal)) owners.Add(patch.owner);
                if (owners.Count > 0) findings.Add($"{label} ← {string.Join(", ", owners)}");
            }
            if (findings.Count == 0)
            {
                Log.Message("[TurboTick] 第三方补丁审计：关键方法没有其他 mod 参与。");
            }
            else
            {
                Log.Warning("[TurboTick] 第三方补丁审计：以下 mod 修改了共享时间线依赖的方法，"
                    + "联机时可能表现为分叉或节拍不一致 —— " + string.Join("；", findings));
            }
            ReportGizmoHooks(ours);
        }
        catch (Exception error) { Log.Warning("[TurboTick] 第三方补丁审计失败：" + error.Message); }
    }

    /// <summary>
    /// Confirms the generic gizmo capture is actually attached, and to how many entry points.
    ///
    /// The click itself cannot be synthesised: the vanilla action is a delegate that only runs when the UI
    /// hit-tests a real click, so no headless test can prove the wiring works. This line at least separates
    /// "the patch never installed" from "the patch is installed and the click did something unexpected",
    /// which are otherwise indistinguishable in a bug report.
    /// </summary>
    /// <summary>The gizmo capture's attached/unresolved counts, as a string a probe can read.
    ///
    /// <para><b>Why this exists (round 140).</b> The unresolved list was only ever a log line, and one name had been
    /// unresolved for months without being noticed - the symptom was "this one gizmo does not sync", which nobody had
    /// looked for because the bossgroup cooldown gap looked like a different bug. An instrument that only writes to a log
    /// cannot be read by the round that needs it; this puts the same facts behind a command.</para></summary>
    internal static string GizmoReport()
    {
        var ours = TurboTickMod.Harmony.Id;
        var attached = 0;
        var unresolved = new System.Collections.Generic.List<string>();
        var byFallback = new System.Collections.Generic.List<string>();
        foreach (var typeName in GizmoStatePatch.GizmoTargetNames)
        {
            var type = TypeResolver.Find(typeName);
            if (type is not null && !TypeResolver.Exact(typeName)) byFallback.Add(typeName + " → " + type.FullName);
            var method = type is null ? null : AccessTools.Method(type, "ProcessInput", [typeof(UnityEngine.Event)]);
            if (method is null || method.IsAbstract) { unresolved.Add(typeName); continue; }
            var info = Harmony.GetPatchInfo(method);
            if (info is not null && info.Prefixes.Any(entry => string.Equals(entry.owner, ours, StringComparison.Ordinal)))
                attached++;
            else unresolved.Add(typeName);
        }
        return $"gizmo 入口 挂上 {attached} / 共 {GizmoStatePatch.GizmoTargetNames.Length}"
               + (unresolved.Count > 0 ? $"；未解析：{string.Join(", ", unresolved)}" : "；全部解析")
               // A name that only resolved by simple name has a wrong namespace: it works today and would stop working
               // if two assemblies ever declared the same simple name, so the report names it instead of hiding it.
               + (byFallback.Count > 0 ? $"；靠简单名回落解析（命名空间写错）：{string.Join(", ", byFallback)}" : "");
    }

    private static void ReportGizmoHooks(string ours)
    {
        var attached = 0;
        var unresolved = new List<string>();
        foreach (var typeName in GizmoStatePatch.GizmoTargetNames)
        {
            var type = TypeResolver.Find(typeName);
            var method = type is null ? null : AccessTools.Method(type, "ProcessInput", [typeof(UnityEngine.Event)]);
            if (method is null || method.IsAbstract) { unresolved.Add(typeName); continue; }
            var info = Harmony.GetPatchInfo(method);
            if (info is not null && info.Prefixes.Any(patch => string.Equals(patch.owner, ours, StringComparison.Ordinal)))
                attached++;
            else unresolved.Add(typeName);
        }
        if (attached == 0)
            Log.Warning("[TurboTick] 通用 gizmo 捕获没有挂上任何入口，物品/属性同步不会生效。");
        else
            Log.Message($"[TurboTick] 通用 gizmo 捕获已挂载 {attached} 个 ProcessInput 入口。");
        // A type that no longer resolves (renamed, made abstract, turned into a nested class) used to be
        // skipped silently, so the only symptom was "this one gizmo does not sync on the other side".
        if (unresolved.Count > 0)
            Log.Warning("[TurboTick] 以下 gizmo 类型没有可挂载的 ProcessInput（原版改名或改成嵌套类型）："
                + string.Join(", ", unresolved));
        ReportFloatMenuHook(ours);
    }

    /// <summary>Confirms the right-click chokepoint is attached.
    ///
    /// <para>Same reasoning as <see cref="ReportGizmoHooks"/>: the click cannot be synthesised headlessly, and a patch that
    /// silently failed to install is indistinguishable from "the player has not right-clicked yet". The method is named
    /// with an explicit signature because `FloatMenuOption` declares two `Chosen` overloads - the no-argument one on
    /// `FloatMenuGridOption` (`:179121`) and the two-argument one on `FloatMenuOption` (`:179550`) - and patching by name
    /// alone would be ambiguous.</para></summary>
    private static void ReportFloatMenuHook(string ours)
    {
        var method = AccessTools.Method(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen),
            [typeof(bool), typeof(FloatMenu)]);
        if (method is null)
        {
            Log.Warning("[TurboTick] FloatMenuOption.Chosen(bool, FloatMenu) 不存在，右键菜单捕获无法挂载。");
            return;
        }
        var info = Harmony.GetPatchInfo(method);
        if (info is not null && info.Prefixes.Any(patch => string.Equals(patch.owner, ours, StringComparison.Ordinal)))
            Log.Message("[TurboTick] 右键菜单捕获已挂载 FloatMenuOption.Chosen（选中对象 + revalidateClickTarget 采样）。");
        else
            Log.Warning("[TurboTick] 右键菜单捕获没有挂上 FloatMenuOption.Chosen，右键下单不会同步。");
    }
}
