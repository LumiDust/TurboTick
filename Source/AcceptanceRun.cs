using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Scripted player actions for the headless acceptance run.
///
/// The two-peer determinism check proves that two peers agree when nobody touches anything. That is the
/// easy half: every remaining defect the project has actually shipped was in the command path - an action
/// applied twice, applied at a different tick, or not applied at all. Those only appear when somebody acts,
/// and a human tester can only report "it rolled back". So the participant drives the same capture helpers
/// the UI calls, and a divergence in the two logs then points at the command path instead.
///
/// Enabled with <c>-turbotick-acceptance</c>; the actions are deliberately ordinary (draft, work priority,
/// forbid, a serialised thing edit) so they work on any generated colony.
/// </summary>
internal static class AcceptanceRun
{
    private static readonly bool enabled = Environment.GetCommandLineArgs()
        .Any(a => a.Equals("-turbotick-acceptance", StringComparison.OrdinalIgnoreCase));

    private static float nextAction;
    private static int step;
    private static int issued;
    private static int gizmoSamples;

    internal static int Issued => issued;

    /// <summary>One action per second, cycling so a long run keeps exercising the path.</summary>
    internal static void Update()
    {
        if (!enabled || !SharedColony.Gating) return;
        if (Clock.Now < nextAction) return;
        nextAction = Clock.Now + 1f;
        try { Act(step++ % 5); }
        catch (Exception error) { Log.Warning("[TurboTick] 验收脚本动作失败：" + error.Message); }
    }

    private static void Act(int which)
    {
        var map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
        if (map is null) return;
        var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        switch (which)
        {
            case 0 when pawn is not null:
                // Draft toggles a single pawn flag; the cheapest possible command round trip.
                SharedActions.CaptureDraft(pawn, !pawn.Drafted);
                break;
            case 1 when pawn?.workSettings is not null:
            {
                var work = DefDatabase<WorkTypeDef>.AllDefsListForReading
                    .FirstOrDefault(def => pawn.workSettings.GetPriority(def) > 0);
                if (work is null) break;
                SharedActions.CapturePriority(pawn, work, pawn.workSettings.GetPriority(work) == 1 ? 2 : 1);
                break;
            }
            case 2:
            {
                // Go through the real entry point, not SharedActions.CaptureForbid. Calling the helper
                // directly proved only that the command path works, while the layer that has actually broken
                // - the patch that intercepts the game's own call - was never exercised.
                var item = map.listerThings.AllThings
                    .FirstOrDefault(t => t.def.category == ThingCategory.Item && t.Spawned);
                if (item is not null) ForbidUtility.SetForbidden(item, !item.IsForbidden(Faction.OfPlayer));
                break;
            }
            case 3:
                RunGizmoSample(map);
                break;
            case 4:
                RunRealGizmoClick(map);
                break;
        }
        issued++;
        if (issued % 30 == 0) Log.Message($"[TurboTick] 验收脚本已发出 {issued} 个操作");
    }

    /// <summary>
    /// The generic gizmo path: sample a live thing's Scribe state, change it the way a gizmo would, then ship
    /// the difference. This is the only part of the sync that carries a whole serialised object, so a defect
    /// here corrupts a live thing instead of merely dropping an action - it has to be exercised, not assumed.
    ///
    /// A switch is preferred because its boolean is unambiguously part of the fragment; a hit-point edit is
    /// only used when the colony has no switch, and either way the run logs whether the fragment actually
    /// changed, which is what decides if the transport was reached at all.
    /// </summary>
    private static void RunGizmoSample(Map map)
    {
        var candidates = map.listerThings.AllThings.OfType<ThingWithComps>()
            .Where(t => t.Spawned && !t.Destroyed).ToArray();
        var target = candidates.FirstOrDefault(t => t.GetComp<CompFlickable>() is not null)
                     ?? candidates.FirstOrDefault(t => t.def.useHitPoints && t.HitPoints > 1);
        if (target is null)
        {
            if (gizmoSamples == 0) Log.Message("[TurboTick] 验收脚本：地图上没有可写的带组件目标");
            return;
        }
        Find.Selector.ClearSelection();
        Find.Selector.Select(target);
        var snapshots = SharedActions.CaptureGizmoTargets();
        if (snapshots is null or { Count: 0 })
        {
            if (gizmoSamples == 0) Log.Warning("[TurboTick] 验收脚本：gizmo 采样为空（选择或输入状态不允许）");
            return;
        }
        var flickable = target.GetComp<CompFlickable>();
        if (flickable is not null) flickable.DoFlick();
        else target.HitPoints = Math.Max(1, target.HitPoints - 1);
        var changed = !string.Equals(ScribeBridge.Save(target), snapshots[0].Before, StringComparison.Ordinal);
        SharedActions.FlushGizmoTargets(snapshots);
        gizmoSamples++;
        if (gizmoSamples <= 3)
            Log.Message($"[TurboTick] 验收脚本：gizmo #{gizmoSamples} {target.def.defName} "
                + $"switch={flickable is not null} 片段变化={changed}");
    }

    private static int realClicks;

    /// <summary>
    /// The one hop the audit said could not be automated: a gizmo's <c>ProcessInput</c>, which is what the
    /// GUI calls when the player clicks. Running the capture helper directly (case 3 above) proves the command
    /// path works but never touches the patch that intercepts the game's own call.
    ///
    /// It is synthesizable after all: 1.7's implementations are just
    /// <c>Command_Toggle.ProcessInput</c> = <c>base.ProcessInput(ev); toggleAction();</c> and the same for
    /// <c>Command_Action</c> (decompiled Verse/Command_Toggle.cs:33, Verse/Command_Action.cs:16) - no
    /// hit-test, unlike the GUI drawer. So an <c>Event</c> of type MouseUp reaches the patched method.
    /// Everything else stays real: the gizmo comes from <c>Thing.GetGizmos()</c>, the selection is real, and
    /// the action runs whichever delegate the game installed.
    /// </summary>
    private static void RunRealGizmoClick(Map map)
    {
        // Keep clicking the same object so the transition alternates. `WantsFlick()` is
        // `wantSwitchOn != switchOnInt` and both fields start true (RimWorld/CompFlickable.cs:12-13), so the
        // first click is always False→True and only a second click on the same thing gives True→False.
        // Picking a fresh target each call made the log look stuck at False→True.
        if (realGizmoTarget is ThingWithComps { Destroyed: false, Spawned: true } kept
            && kept.GetComp<CompFlickable>() is not null && kept.Map == map)
        {
            ClickRealGizmo(kept);
            return;
        }
        var target = map.listerThings.AllThings.OfType<ThingWithComps>()
            .FirstOrDefault(t => t.Spawned && !t.Destroyed && t.GetComp<CompFlickable>() is not null);
        if (target is null)
        {
            if (realClicks == 0 && ++noFlickableTargets == 1)
                Log.Message("[TurboTick] 验收脚本：地图上没有带 CompFlickable 的物体，真实 gizmo 点击跳过");
            return;
        }
        realGizmoTarget = target;
        ClickRealGizmo(target);
    }

    private static ThingWithComps? realGizmoTarget;

    private static void ClickRealGizmo(ThingWithComps target)
    {
        Find.Selector.ClearSelection();
        Find.Selector.Select(target);
        // Ask the flickable comp for its own gizmos. Reading Thing.GetGizmos() and taking the first toggle
        // picked the wrong one: a door's first Command_Toggle is "hold open", which changes holdOpenInt and
        // never touches WantsFlick - the run then logged False→False and proved nothing.
        var comp = target.GetComp<CompFlickable>();
        if (comp is null) return;
        var toggle = comp.CompGetGizmosExtra().OfType<Command_Toggle>()
            .FirstOrDefault(g => g.toggleAction is not null);
        if (toggle is null)
        {
            // A door without a faction never yields this gizmo (CompFlickable checks Faction.OfPlayer), so
            // "target found, gizmo missing" has to be visible instead of looking like a silent pass.
            if (realClicks == 0) Log.Message($"[TurboTick] 验收脚本：{target.def.defName} 的 flickable 没有可点 gizmo（多半不是玩家阵营）");
            return;
        }
        var ev = new UnityEngine.Event
        {
            type = UnityEngine.EventType.MouseUp,
            button = 0,
            mousePosition = new UnityEngine.Vector2(10f, 10f)
        };
        var before = comp.WantsFlick();
        // Hits GizmoStatePatch: Prefix samples the selection, Postfix ships the difference and sets the hold.
        toggle.ProcessInput(ev);
        var after = comp.WantsFlick();
        realClicks++;
        if (realClicks <= 6)
            Log.Message($"[TurboTick] 验收脚本：真实 gizmo 点击 #{realClicks} {target.def.defName} "
                + $"wantsFlick {before}→{after}");
    }

    private static int noFlickableTargets;
}


