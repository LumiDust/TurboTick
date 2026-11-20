using System.Globalization;
using System.Text;
using System.Text.Json;
using RimWorld;
using RimWorld.Planet;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// The game process's half of the split administration page.
///
/// The page now lives in its own process, which means the game can no longer be <em>called</em> - it can only be
/// asked. The whole contract is a directory:
///
/// <code>
///   &lt;data&gt;/portal-commands/&lt;id&gt;.json       {"id","name","argument"}   written by the portal
///   &lt;data&gt;/portal-commands/&lt;id&gt;.done.json  {"ok","code","detail"}     written here
/// </code>
///
/// Two properties matter and both are why it is a file and not a socket:
///
/// 1. <b>The command runs on the game thread.</b> Everything below touches <c>Current</c>, <c>Scribe</c>,
///    <c>WorldFiles</c> and the room. The portal has no business doing any of that, and this process has no
///    business being touched from another thread. Polling from <c>Update</c> gives the same single-threaded
///    execution the old in-process queue had, without sharing an address space.
/// 2. <b>An unanswered command is distinguishable from a successful one.</b> The portal waits for
///    <c>.done.json</c> and reports a timeout when it does not appear. Reporting "accepted" and dying is exactly
///    the failure this shape makes impossible.
///
/// The load gate also lives here (see <see cref="VerifyConfiguredSaveMods"/>) and that is not redundant with the
/// portal's own check: <c>ModsConfig.xml</c> is read before this mod's code exists, so the config a start attempt
/// writes can only take effect on the <em>next</em> start. The portal decides whether to start a process; this
/// decides whether a loaded process may put a world into it.
/// </summary>
internal static class PortalCommands
{
    /// <summary>How often the inbox is listed. Slow enough to be free, fast enough to feel immediate.</summary>
    private const long PollMilliseconds = 250;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string folder = "";
    private static long nextPollMs;
    private static SaveModsOptions? options;

    /// <summary>Problems with the loaded mod config, as a sentence an operator can act on.</summary>
    internal static string? SaveModsRefusal => saveModsRefusal;

    private static volatile string? saveModsRefusal;

    /// <summary>True once a command has been answered, so a second answer cannot overwrite the first.</summary>
    private static readonly HashSet<string> answered = new(StringComparer.Ordinal);

    /// <summary>
    /// Wires the inbox and the mod check to this process's directories.
    ///
    /// Called once from <see cref="HeadlessRuntime.Start"/>, before the room boots, because the mod verdict has to
    /// exist before the load funnel can consult it.
    /// </summary>
    internal static void Configure(string dataDirectory, string savesDirectory, string gameDirectory,
        string modsDirectory, string modsConfigPath, bool autoApply)
    {
        options = new SaveModsOptions(savesDirectory, dataDirectory, gameDirectory, modsDirectory, modsConfigPath,
            autoApply);
        if (string.IsNullOrEmpty(dataDirectory))
        {
            folder = "";
            return;
        }
        folder = Path.Combine(dataDirectory, "portal-commands");
        try
        {
            Directory.CreateDirectory(folder);
            // Anything left over from a previous process outlived the command it was asking for. Answering it now
            // would act on a world that is no longer the one the operator was looking at.
            Sweep(TimeSpan.FromMinutes(10));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning("[TurboTick portal] 无法准备命令收件箱：" + error.GetType().Name + ": " + error.Message);
            folder = "";
        }
    }

    /// <summary>The periodic hook, called from the same place the in-process page used to be ticked.</summary>
    internal static void Tick()
    {
        if (folder.Length == 0) return;
        var now = Environment.TickCount64;
        if (now < nextPollMs) return;
        nextPollMs = now + PollMilliseconds;
        try { Poll(); }
        catch (Exception error)
        {
            // A broken inbox must never be able to stop the authority.
            Log.Warning("[TurboTick portal] 收件箱轮询失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Poll()
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            var name = Path.GetFileName(path);
            // `.done.json` files are ours; the portal removes them after reading.
            if (name.EndsWith(".done.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!answered.Add(name)) continue;
            Dispatch(path, name);
        }
    }

    private static void Dispatch(string path, string file)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            id = Text(root, "id") ?? Path.GetFileNameWithoutExtension(file);
            var command = Text(root, "name") ?? "";
            var argument = Text(root, "argument");
            Execute(id, command, argument);
        }
        catch (Exception error)
        {
            // Answer rather than stay silent: an unanswered request costs the portal its full timeout, and the
            // operator then sees "the game did not respond" for what is really a malformed file.
            var code = error is JsonException ? "request-unreadable" : "request-failed";
            Answer(id ?? Path.GetFileNameWithoutExtension(file), false, code,
                error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Execute(string id, string command, string? argument)
    {
        switch (command)
        {
            case "save":
                RunSave(outcome => Answer(id, outcome.Ok, outcome.Code, outcome.Detail));
                return;
            case "reload-settings":
                var reloaded = ReloadSettings();
                Answer(id, reloaded.Ok, reloaded.Code, reloaded.Detail);
                return;
            case "quit":
            case "server-shutdown":
                if (!HeadlessRuntime.RequestStop("portal"))
                {
                    Answer(id, false, "already-stopping", "已经在退出了");
                    return;
                }
                Answer(id, true, "stopping", "已请求优雅退出：检查点 → 持久化 → 进程退出");
                return;
            case "server-stop":
                var stop = StopWorld();
                Answer(id, stop.Ok, stop.Code, stop.Detail);
                return;
            case "answer-dialog":
                AnswerDialog(id, argument);
                return;
            case "construct-report":
                // Reads the construction gating question on demand, on the game thread, without restarting the
                // process. A restart reloads the world from the checkpoint and throws away the very colony state the
                // question is about, so the probe has to be reachable without one.
                var report = ConstructionProbe.DescribeNow();
                Answer(id, report.Ok, report.Code, report.Detail + Environment.NewLine
                    + ConstructionOutcomeProbe.Summary() + " ｜ " + ConstructionDestroyProbe.Summary()
                    + " ｜ " + RandValueProbe.Summary() + " ｜ " + ConstructDeltaProbe.Summary()
                    // The replica's own map, so "the wall finished here but a blueprint is still drawn there" can be
                    // read as one answer. Without it the two halves live in two processes' logs.
                    + Environment.NewLine + AuthoritativeReplica.WorldTally());
                return;
            case "script-build":
                // Runs the player's own build designator on the authority and answers with the same line it logs.
                // This is the only way to ask "does a drag turn into commands?" without a human at the mouse and
                // without a restart - and after the capture was found dead for a whole session, that question has to
                // be answerable on demand.
                var built = ScriptedZoneDrag.PlaceBuildNow();
                Answer(id, built.Ok, built.Code, built.Detail);
                return;
            case "script-build-at":
                // `argument` is "x,z[,width,height]". The fixed-centre form stops working the moment the map centre is
                // built over - measured: `设计器拒绝了全部 5 格` on a colony that had walls there.
                var placement = ParsePlacement(argument);
                if (placement is null)
                {
                    Answer(id, false, "bad-argument", "参数应为 \"<x>,<z>[,<宽>,<高>]\"，收到：" + (argument ?? "无"));
                    return;
                }
                var placed = ScriptedZoneDrag.PlaceBuildAt(placement.Value.X, placement.Value.Z,
                    placement.Value.Width, placement.Value.Height);
                Answer(id, placed.Ok, placed.Code, placed.Detail);
                return;
            case "capture-counters":
                Answer(id, true, "ok", DesignatorSync.CaptureCounters() + " | " + DesignatorSync.Summary());
                return;
            case "script-settlement":
                // Declares a settlement so the ownership gates can be exercised. Until one exists, every gate
                // short-circuits on `SettlementData.Active` and has never refused anything: the feature installs and does
                // nothing, which is the correct default and also the reason this command is needed to verify it at all.
                var settlement = ScriptedSettlement.Place(argument, 0);
                // Draw the land the gates are about to enforce. Without this the gates and the overlay could disagree -
                // a build order refused in a cell the player cannot tell apart from any other - and the refusal would read
                // as a bug rather than as a rule.
                SettlementAreas.Rebuild(SettlementGates.Data);
                Answer(id, settlement.Ok, settlement.Code, settlement.Detail);
                return;
            case "settlement-report":
                // Answers "why was that refused" without reading code: every settlement, the caller's grants in it, and
                // the acts/kind pairs those grants actually permit.
                Answer(id, true, "ok", ScriptedSettlement.Report(argument));
                return;
            case "nudge-probe":
                // Changes one serialisable field on one building, then runs the sweep twice and reports whether it noticed.
                // The existing `sweep-once` proves the sweep converges (`与上次不同 0 栋` on the second pass) but convergence
                // is also what a sweep that never compares anything produces - both readings are "0 changed". This is the
                // perturbation that tells them apart.
                Answer(id, true, "ok", SweepProbe.NudgeAndSweep());
                return;
            case "sweep-once":
                // Runs one complete building sweep and reports how many buildings changed since the last send. The sweep only
                // runs inside `PublishFrame`, which a paused room never calls, so on an idle server the log's `扫 0 栋` is
                // the normal reading and cannot be told apart from a failure.
                Answer(id, true, "ok", SweepProbe.RunOnce());
                return;
            case "sync-census":
                // A live census of everything a replica cannot derive, next to the counter each channel keeps. Every gap
                // found so far was found by somebody thinking of the right question; this is the instrument that makes the
                // silent ones visible without one.
                Answer(id, true, "ok", SyncCensus.Report());
                return;
            case "settlement-scenario":
                // **The whole sequence in one command (round 141).** Every gate here has been measured alone; what was
                // never measured is the order a player produces - refused, granted, allowed, at war, at peace - and that
                // is where a war check placed after the grant table, or a grant reader that ignores the visitor's own
                // settlement, would show up. Declares its own two sites and retires them; see `ScriptedSettlement.Scenario`.
                Answer(id, true, "ok", ScriptedSettlement.Scenario(argument));
                return;
            case "settlement-war":
                // Declares war, makes peace or forms an alliance between two settlements. Written as one command with an
                // operand rather than three, because the three share the same validation and the same "which pair" parse,
                // and three near-identical cases would drift.
                Answer(id, true, "ok", ScriptedSettlement.War(argument));
                return;
            case "alert-probe":
                // Whether a replica computes its own alerts, and whether the inputs it reads are the authority's.
                //
                // The claim being tested: `AlertsReadoutUpdate` runs from `UIRootUpdate` (`_lab/asm/…:684247`), which is a
                // per-frame UI callback and not part of the tick loop, and `TickManager.TicksGame` is overridden to the
                // authority's clock (`ReplicaClockPatch`, `Source/AuthoritativeReplica.cs:2842`). So an alert whose
                // `GetReport()` reads only synced state must reach the same answer here without any alert channel.
                //
                // This prints the inputs and the verdict, not just the verdict: an alert that reads false on both sides
                // proves nothing, and "the alert is off" is what a broken clock also produces.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var texts = new StringBuilder();
                    var tick = Find.TickManager?.TicksGame ?? -1;
                    texts.Append("tick=").Append(tick)
                        .Append("（冻结=").Append(AuthoritativeReplica.Frozen)
                        .Append(" 引擎tick=").Append(AuthoritativeReplica.EngineTickForProbe())
                        .Append("）；");
                    texts.Append(AlertProbe.Describe(tick)).Append("；");
                    var nutrition = map.resourceCounter?.TotalHumanEdibleNutrition ?? -1f;
                    var colonists = map.mapPawns.FreeColonistsSpawnedCount;
                    texts.Append("食物营养 ").Append(nutrition.ToString("F1", CultureInfo.InvariantCulture))
                        .Append("（阈值 ").Append((4f * colonists).ToString("F1", CultureInfo.InvariantCulture))
                        .Append("，殖民者 ").Append(colonists).Append("）；");
                    // The alert objects themselves live on `AlertsReadout`, which the UI owns. Reflect them out rather than
                    // constructing new ones, so this reads what the player's own readout holds.
                    // `UIRoot` has no `AlertsReadout` property; the readout is a public field on `UIRoot_Play`
                    // (`:684195`) and `AlertsReadout.Alerts` wraps the cast (`:207572`). Read through the wrapper so a
                    // renamed field is a null rather than a compile error in a diagnostic.
                    var readout = Find.UIRoot is UIRoot_Play playRoot ? playRoot.alerts : null;
                    var activeField = HarmonyLib.AccessTools.Field(typeof(AlertsReadout), "activeAlerts");
                    var allField = HarmonyLib.AccessTools.Field(typeof(AlertsReadout), "AllAlerts");
                    if (readout is null) { texts.Append("没有 UIRoot，读不到告警"); }
                    else
                    {
                        var active = activeField?.GetValue(readout) as System.Collections.IList;
                        var all = allField?.GetValue(readout) as System.Collections.IList;
                        texts.Append("告警对象 ").Append(all?.Count ?? -1).Append(" 个，活跃 ").Append(active?.Count ?? -1).Append(" 个");
                        if (all is not null)
                        {
                            // **Count what was left out (round 96).** This loop stops after eight and used to say nothing
                            // about it: 「告警对象 124 个，活跃 0 个：」 followed by eight names reads as "here are the
                            // alerts", when 116 of them were never asked. The four mundo-clock-gated alerts are exactly
                            // the kind that would be missed - they are not the first eight in the readout's order - so the
                            // reading that was supposed to catch them could not have.
                            var examined = 0;
                            var belowFloor = 0;
                            texts.Append("：");
                            foreach (var item in all)
                            {
                                if (item is not Alert alert) continue;
                                examined++;
                                var floor = AlertProbe.FloorOf(alert.GetType().Name);
                                var measurable = floor == 0 || tick >= floor;
                                if (!measurable) belowFloor++;
                                if (examined > 8) continue;
                                string verdict;
                                try { verdict = alert.GetReport().active ? "真" : "假"; }
                                catch (Exception error) { verdict = "抛" + error.GetType().Name; }
                                texts.Append(alert.GetType().Name).Append('=').Append(verdict);
                                // **A false verdict from a world-clock floor is not the same as a false verdict
                                // (round 96).** Three of these alerts open `GetReport()` with
                                // `if (TicksGame < 150000f) return false;` - `Alert_LowFood` (`:611350`),
                                // `Alert_LowBabyFood` (`:611408`), `Alert_LowMedicine` (`:611683`) - and
                                // `Alert_NeedHoldingPlatform` uses 240000 (`:615911`). An observed world was at tick 358,
                                // so all four printed 「假」 and would have printed 「假」 with an empty larder too.
                                //
                                // The floor is printed beside the verdict so the reading cannot be mistaken for evidence:
                                // 「假（时钟 358 < 150000，未到判据）」 says the alert is wired but unanswerable yet, while a
                                // bare 「假」 says the colony has food. Those are opposite conclusions from the same line.
                                if (!measurable)
                                    texts.Append("（时钟 ").Append(tick).Append(" < 判据 ").Append(floor)
                                        .Append("，此读数无意义）");
                                texts.Append(' ');
                            }
                            if (examined > 8) texts.Append($"（省略 {examined - 8} 个未列出，共检查 {examined} 个）");
                            if (belowFloor > 0)
                                texts.Append($"；其中 {belowFloor} 个受世界时钟限制，现在不可判");
                        }
                    }
                    Answer(id, true, "ok", texts.ToString());
                }
                return;

            case "tracker-probe":
                // `PawnComponentsUtility.AddAndRemoveDynamicComponents` (`:445343`) creates and destroys twelve pawn
                // trackers based on faction, trade intent and DLC switches. The authority calls it when that state
                // changes; a replica never does. So the question is not "is the code patched" but "does a replica
                // actually end up missing a tracker it needs" — and that can only be answered by counting which of the
                // twelve exist on the authority's pawns right now.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    // Each entry: name, a predicate for "this pawn has it". Read through the pawn's public fields, which
                    // are exactly what the utility writes.
                    var counts = new (string Name, Func<Pawn, bool> Has)[]
                    {
                        ("trader", p => p.trader is not null),
                        ("infectionVectors", p => p.infectionVectors is not null),
                        ("duplicate", p => p.duplicate is not null),
                        ("foodRestriction", p => p.foodRestriction is not null),
                        ("outfits", p => p.outfits is not null),
                        ("drugs", p => p.drugs is not null),
                        ("timetable", p => p.timetable is not null),
                        ("reading", p => p.reading is not null),
                        ("inventoryStock", p => p.inventoryStock is not null),
                        ("mechanitor", p => p.mechanitor is not null),
                        ("learning", p => p.learning is not null),
                        ("drafter", p => p.drafter is not null),
                        ("playerSettings", p => p.playerSettings is not null),
                        ("training", p => p.training is not null),
                        ("relations", p => p.relations is not null),
                        ("workSettings", p => p.workSettings is not null)
                    };
                    var totals = new int[counts.Length];
                    var spawned = 0;
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn is null) continue;
                        spawned++;
                        for (var i = 0; i < counts.Length; i++)
                            if (counts[i].Has(pawn)) totals[i]++;
                    }
                    // A zero here is a sampling artefact, not a reading: measured once right after a container restart the
                    // probe saw 0 pawns and two reruns seconds later saw 71. `mapPawns.AllPawnsSpawned` is populated as the
                    // map finishes loading, so a census that runs first gets an empty list and reports every tracker as
                    // having zero carriers. Saying which it is keeps a startup race from being read as a finding.
                    if (spawned == 0)
                    {
                        Answer(id, false, "NoPawns",
                            "本图 0 个小人：地图还在装载（重启后首帧常见），不是 tracker 缺失；稍后重跑");
                        return;
                    }
                    var text = new System.Text.StringBuilder();
                    text.Append($"本图 {spawned} 个小人");
                    var empty = new List<string>();
                    for (var i = 0; i < counts.Length; i++)
                    {
                        if (totals[i] == 0) { empty.Add(counts[i].Name); continue; }
                        if (text.Length > 0) text.Append("；");
                        text.Append($"{counts[i].Name}×{totals[i]}");
                    }
                    text.Append("；零载体 " + (empty.Count == 0 ? "无" : string.Join(",", empty)));
                    Answer(id, true, "ok", text.ToString());
                }
                return;

            case "launch-probe":
                // A launch is the clearest case of "the gizmo changes things that are not the gizmo's parent":
                // `CompLaunchable.TryLaunch` (`:543651-543727`) consumes fuel on every transporter in the group,
                // creates an `ActiveTransporter`, wraps it in a `FlyShipLeaving` skyfaller, and hands the whole
                // group to a world object. `GizmoStatePatch` compares the parent's fragment before and after the
                // click, and the only field that moves there is `lastLaunchTick` (`:543681`) - so on a replica the
                // click reports "the fuel number is different" and no launch ever happens.
                //
                // <para>Before building anything: how many launchable things does this save even have, and does the
                // click path reach `TryLaunch` from a replica at all? A channel on zero carriers cannot be told
                // apart from a broken one, which is the same rule that kept the royal title channel unbuilt.</para>
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var launchable = 0;
                    var withFuel = 0;
                    var inGroup = 0;
                    var sampleParts = "";
                    foreach (var thing in map.listerThings.AllThings)
                    {
                        var comp = thing?.TryGetComp<CompLaunchable>();
                        if (comp is null) continue;
                        launchable++;
                        var fuel = comp.Refuelable;
                        if (fuel is not null && fuel.Fuel > 0f) withFuel++;
                        // `CompLaunchable.TransportersInGroup` is protected; the public route is the transporter's own
                        // `TransportersInGroup(Map)` (`:561540`), which returns null unless loading is in progress or the
                        // group is ready to launch - so a null here means "this pod alone is not a group", not "no data".
                        var transporter = thing.TryGetComp<CompTransporter>();
                        var group = transporter?.TransportersInGroup(map);
                        if (group is not null && group.Count > 1) inGroup++;
                        if (sampleParts.Length == 0 && thing is not null)
                        {
                            var capacity = thing.def.GetCompProperties<CompProperties_Refuelable>()?.fuelCapacity ?? 0f;
                            // `CanLaunch()` is public and is the same predicate the gizmo uses to disable itself.
                            sampleParts = $"，样本「{thing.LabelShort}」{thing.def.defName} 燃料 "
                                + (fuel is null ? "无" : $"{fuel.Fuel:0.#}/{capacity:0.#}")
                                + $"，组内 {group?.Count ?? 0} 个，可发射 {(comp.CanLaunch() ? "是" : "否")}";
                        }
                    }
                    // The group is what travels: one pod alone is not a launch, and the fuel check reads the minimum
                    // across the group (`MinFuelLevelInGroup`).
                    var text = $"可发射运输舱 {launchable} 个（有燃料 {withFuel} 个，在多舱编组里 {inGroup} 个）"
                        + sampleParts
                        + "；发射路径 `CompLaunchable.TryLaunch`（`:543651`）消耗整组燃料、创建 `ActiveTransporter`、"
                        + "生成 `FlyShipLeaving` 并把整组交给世界对象 —— 只比较父物件片段只能看到 `lastLaunchTick`";
                    Answer(id, true, "ok", text);
                }
                return;

            case "comp-probe":
                // Does a thing's fragment carry its comps, and do two comps collide on one label?
                //
                // <para>`ThingWithComps.ExposeData` (`:160414-160428`) calls `comps[i].PostExposeData()` and nothing
                // else, and `ThingComp` does not implement `IExposable` (`:149291`) - so comp fields are **flattened
                // into the thing's own node**, not nested under per-comp children. Measured: a nine-comp
                // ShardAnimalPulser produced 450 B with `forbidden` among the children and no comp wrapper at all.
                // The consequence is not "comps are missing" but "two comps can write the same tag": comp scribe
                // labels number 429 and 35 of them are used by more than one comp (`color`, `verbTracker`,
                // `innerContainer`, `traits`, `deactivated`). The saver writes duplicate sibling elements
                // (`EnterNode` `:146944-146956`), and the loader reads `curXmlParent[label]`, which is the FIRST
                // match - so the second comp's value is silently dropped on a round trip.</para>
                //
                // <para>What this measures: how many spawned things in this save actually carry a colliding pair,
                // and whether a sample fragment round-trips unchanged. Zero carriers is a legitimate answer given
                // that `Thing.ExposeData` compresses plain things (`IsSaveCompressible` `:65097`) and the save has
                // 71 pawns but few multi-comp buildings.</para>
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }

                    // Duplicate-label census over every spawned ThingWithComps.
                    var withComps = 0;
                    var colliding = 0;
                    Thing? sample = null;
                    var sampleComps = 0;
                    var sampleDupes = 0;
                    foreach (var thing in map.listerThings.AllThings)
                    {
                        if (thing is not ThingWithComps wc || thing.Destroyed || !thing.Spawned) continue;
                        withComps++;
                        var comps = wc.AllComps;
                        if (comps is null || comps.Count == 0) continue;
                        if (comps.Count > sampleComps) { sample = thing; sampleComps = comps.Count; }
                        var labels = new HashSet<string>(StringComparer.Ordinal);
                        var dup = false;
                        foreach (var comp in comps)
                        {
                            var name = comp.GetType().Name;
                            if (!CompScribeLabels.ByComp.TryGetValue(name, out var own)) continue;
                            foreach (var label in own)
                                if (!labels.Add(label)) dup = true;
                        }
                        if (dup) colliding++;
                    }

                    var text = $"带 comp 的物件 {withComps} 个，其中 comp 标签互相覆盖的 {colliding} 个"
                        + $"；comp 字段是平铺的（`ThingWithComps.ExposeData` 只调 `PostExposeData`，`ThingComp` 不实现 `IExposable`）"
                        + $"；全库 comp 标签 {CompScribeLabels.LabelTotal} 个，其中 {CompScribeLabels.LabelShared} 个被多个 comp 复用"
                        + $"（{CompScribeLabels.ByComp.Count} 个 comp 声明了标签）";

                    if (sample is not null && sample is IExposable exposable)
                    {
                        var fragment = ScribeBridge.SaveOrNull(exposable);
                        if (fragment is null)
                        {
                            text += "；样本采样失败（Scribe 忙）";
                        }
                        else
                        {
                            // A round trip is the only way to see the loss: load the fragment back and compare the same
                            // fields the fragment claims to carry.
                            sampleDupes = 0;
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var comp in ((ThingWithComps)sample).AllComps)
                            {
                                if (!CompScribeLabels.ByComp.TryGetValue(comp.GetType().Name, out var own)) continue;
                                foreach (var label in own)
                                    if (!seen.Add(label)) sampleDupes++;
                            }
                            text += $"\n样本「{sample.LabelShort}」{sample.def.defName}：comp {sampleComps} 个，片段 {fragment.Length} B，"
                                + $"片段内重复标签 {sampleDupes} 个";
                        }
                    }
                    Answer(id, true, "ok", text);
                }
                return;

            case "letter-probe":
                // A choice letter is the one letter type a player is expected to ANSWER, and until `ReplicaLetter.Fragment`
                // existed the replica rebuilt every one of them as a `StandardLetter` with a log line telling the player
                // to go do it on the host. Two claims, measured in order: a real growth-moment letter's whole state can be
                // sampled, and rebuilding it through `LetterMaker.MakeLetter(LetterDef)` gives back the same subclass with
                // the same options. `ChoiceLetter_GrowthMoment` is the case that matters most and the one that is NOT a
                // `ChoiceLetter`: it derives from `LetterWithTimeout` (`:662067`) and answers through `MakeChoices`
                // (`:662198`) rather than through a `DiaOption` list.
                {
                    var map = Find.CurrentMap;
                    var pawn = map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(p => p?.RaceProps?.Humanlike == true && p.IsColonist);
                    if (pawn is null) { Answer(id, false, "NoPawn", "本图没有可用的人形殖民者"); return; }
                    var def = LetterDefOf.ChildBirthday;
                    var made = LetterMaker.MakeLetter(def) as ChoiceLetter_GrowthMoment;
                    if (made is null)
                    {
                        Answer(id, false, "NoLetter", $"LetterDefOf.ChildBirthday 的 letterClass 是 {def.letterClass?.Name ?? "空"}，不是成长时刻信件");
                        return;
                    }
                    // `ConfigureGrowthLetter` is the public entry the age tracker itself uses (`:662145`); the counts are
                    // private fields, so the probe goes through it rather than writing them.
                    made.ConfigureGrowthLetter(pawn, 2, 3, 2, ["Shooting"], pawn.Name);
                    made.Label = "探测用成长时刻";
                    made.StartTimeout(120000);
                    // `TrySetChoices` is private and runs from `OpenLetter` (`:662185-662190`), which also pushes a
                    // window. Calling it by reflection is the only way to get the options generated without opening UI
                    // on the authority, and the options must be generated before sampling or the fragment contains two
                    // empty lists - measured: `特性选项 未生成→-1`, which reads exactly like a broken channel.
                    var trySet = HarmonyLib.AccessTools.Method(typeof(ChoiceLetter_GrowthMoment), "TrySetChoices");
                    trySet?.Invoke(made, null);
                    var fragment = ScribeBridge.SaveOrNull(made);
                    if (fragment is null) { Answer(id, false, "NoFragment", "片段采样失败（Scribe 忙？）"); return; }
                    // Rebuild through the same factory the replica uses, then load: this is the whole path a replica takes.
                    var rebuilt = LetterMaker.MakeLetter(def);
                    // The pawn lives outside the fragment (`Scribe_References.Look`), so it is named explicitly -
                    // this is the same call `LetterSync.Apply` makes.
                    var loaded = ScribeBridge.Load(rebuilt, fragment,
                        map.mapPawns.AllPawns.Cast<Pawn>());
                    var back = rebuilt as ChoiceLetter_GrowthMoment;
                    var samePawn = back?.pawn == pawn;
                    var optionsBack = back?.traitChoices?.Count ?? -1;
                    var optionsMade = made.traitChoices?.Count ?? -1;
                    var passionBack = back?.passionChoices?.Count ?? -1;
                    var choiceTypes = typeof(Letter).Assembly.GetTypes()
                        .Count(t => t.IsClass && !t.IsAbstract && typeof(ChoiceLetter).IsAssignableFrom(t));
                    var text = $"信件「{made.Label}」{def.letterClass?.Name}，片段 {fragment.Length} B"
                        + $"；重建 {(loaded ? "成功" : "失败")}，同类型 {(rebuilt is ChoiceLetter_GrowthMoment ? "是" : "否")}"
                        + $"；小人回指 {(samePawn ? "一致" : "不一致")}"
                        + $"；特性选项 {(optionsMade < 0 ? "未生成" : optionsMade.ToString())}→{optionsBack}，激发选项 {passionBack}"
                        + $"；ChoiceLetter 子类 {choiceTypes} 个（成长时刻不在其中）";
                    Answer(id, loaded && samePawn && rebuilt is ChoiceLetter_GrowthMoment, "ok", text);
                }
                return;

            case "discovery-probe":
                // Three `Game`-owned databases that decide what the player can see: whether a thing kind is unlocked
                // (`Game.hiddenItemsManager` `:49837`), whether a codex entry is discovered (`Game.entityCodex`
                // `:49839`), and which custom xenogerms exist (`Game.customXenogermDatabase` `:49831`). Counted before
                // building a channel: a channel with zero carriers cannot be told apart from a broken one.
                {
                    var game = Current.Game;
                    if (game is null) { Answer(id, false, "NoGame", "没有当前游戏"); return; }
                    var text = new System.Text.StringBuilder();

                    var hidden = game.hiddenItemsManager;
                    var hiddenTotal = 0; var hiddenStill = 0;
                    var hiddenField = HarmonyLib.AccessTools.Field(typeof(HiddenItemsManager), "hiddenItemDefs");
                    if (hiddenField?.GetValue(hidden) is System.Collections.IDictionary table)
                    {
                        hiddenTotal = table.Count;
                        foreach (System.Collections.DictionaryEntry entry in table)
                            if (entry.Value is true) hiddenStill++;
                    }
                    text.Append($"隐藏物品表 {hiddenTotal} 项，其中仍隐藏 {hiddenStill} 项，已解锁 {hiddenTotal - hiddenStill} 项");
                    text.Append($"；片段 {Save(hidden)?.Length ?? 0} B");

                    var codex = game.entityCodex;
                    var categories = DefDatabase<EntityCategoryDef>.AllDefsListForReading;
                    var discoveredCategories = 0;
                    foreach (var category in categories)
                        if (codex.Discovered(category)) discoveredCategories++;
                    var entries = DefDatabase<EntityCodexEntryDef>.AllDefsListForReading;
                    var discoveredEntries = 0;
                    foreach (var entry in entries)
                        if (codex.Discovered(entry)) discoveredEntries++;
                    text.Append($"；图鉴 {categories.Count} 类（已发现 {discoveredCategories}）/ {entries.Count} 条目（已发现 {discoveredEntries}）");
                    text.Append($"；片段 {Save(codex)?.Length ?? 0} B");

                    var xenogerms = game.customXenogermDatabase;
                    var xenoCount = xenogerms?.CustomXenogermsForReading?.Count ?? 0;
                    text.Append($"；自定义异种胚 {xenoCount} 个；片段 {Save(xenogerms)?.Length ?? 0} B");

                    Answer(id, true, "ok", text.ToString());
                }
                return;

            case "storyteller-probe":
                // The storyteller page has no probe of its own, and its sliders are simulation inputs: a replica holding
                // the old `threatScale` cannot predict a raid size. Three claims: the fragment can be sampled, a changed
                // difficulty shows up in it, and the command round-trips byte for byte.
                {
                    var before = SharedActions.CaptureStoryteller();
                    if (before is null)
                    {
                        // Two causes that read the same in a single null: no `Find.Storyteller`, or Scribe already
                        // mid-save/mid-load (so re-entering ExposeData is refused). Printing both is the only way to
                        // tell "the channel cannot sample" from "this moment was not sampleable".
                        Answer(id, false, "NoFragment",
                            $"采样失败：Find.Storyteller {(Find.Storyteller is null ? "为空" : "存在")}"
                            + $"，Scribe.mode={Verse.Scribe.mode}");
                        return;
                    }
                    var storyteller = Find.Storyteller;
                    var wasDef = storyteller.def?.defName ?? "无";
                    var custom = storyteller.difficultyDef?.isCustom == true;
                    // `Storyteller.ExposeData` only scribes `difficulty` inside `if (difficultyDef.isCustom)`
                    // (`:390071-390074`) — for a named difficulty the object is rebuilt from the def on load
                    // (`:390076-390079`), so its fields are not in the fragment at all. And `StorytellerUI` only draws
                    // the sliders under `if (difficulty.isCustom)` (`:655130`). So a probe on a non-custom difficulty
                    // must change a field the fragment *does* carry, or "采样 未变" would be blamed on the comparator.
                    var wasScale = storyteller.difficulty.threatScale;
                    if (custom)
                    {
                        storyteller.difficulty.threatScale = Math.Abs(wasScale - 1.35f) < 0.001f ? 0.7f : 1.35f;
                    }
                    else
                    {
                        // `def` and `difficultyDef` are both `Scribe_Defs.Look` outside the custom branch, so swapping
                        // the difficulty def is visible in the fragment. Restored below with the whole `before` load.
                        storyteller.difficultyDef = ReferenceEquals(storyteller.difficultyDef, DifficultyDefOf.Rough)
                            ? DifficultyDefOf.Easy : DifficultyDefOf.Rough;
                    }
                    var after = SharedActions.CaptureStoryteller();
                    var changed = after is not null && !string.Equals(before, after, StringComparison.Ordinal);
                    // Round-trip on the wire, then load it back so the probe leaves the world as it found it.
                    var encoded = TurboTick.Simulation.CommandCodec.StorytellerCommand(after ?? before, 1);
                    var wireOk = TurboTick.Simulation.CommandCodec.TryDecodeStoryteller(encoded.Payload, out var back)
                        && string.Equals(back, after ?? before, StringComparison.Ordinal);
                    var restored = ScribeBridge.Load(storyteller, before);
                    var text = $"叙事者 {wasDef}，困难 {storyteller.difficultyDef?.defName ?? "无"}"
                        + $"（{(custom ? "自定义，difficulty 在片段内" : "非自定义，difficulty 不在片段内，滑块本就不绘制")}）"
                        + $"；片段 {before.Length} B；"
                        + (custom
                            ? $"改了威胁倍率 {wasScale:0.##}→{storyteller.difficulty.threatScale:0.##}"
                            : $"改了困难档位（非自定义时片段里唯一可变的仿真输入）")
                        + $"；采样 {(changed ? "变化" : "未变")}（{back?.Length ?? 0} B）"
                        + $"；过信道 {(wireOk ? "一致" : "不一致")}（{encoded.Payload.Length} B）"
                        + $"；回灌 {(restored ? "成功" : "失败")}，倍率恢复 {storyteller.difficulty.threatScale:0.##}（原 {wasScale:0.##}）"
                        + $"，档位恢复 {storyteller.difficultyDef?.defName ?? "无"}"
                        + $"；应用计数 {SharedActions.StorytellerApply}";
                    Answer(id, changed && wireOk && restored, changed && wireOk ? "ok" : "Mismatch", text);
                }
                return;

            case "bill-probe":
                // End-to-end proof for the bill settings dialog without opening a window. Three separate claims, printed
                // separately because they fail for different reasons: the fragment can be sampled (Scribe works on a
                // live BillStack), it changes when a bill setting changes (the comparison is sensitive), and the apply
                // path writes it back (the command round-trips).
                {
                    Building_WorkTable? bench = null;
                    foreach (var probeMap in Find.Maps)
                        foreach (var building in probeMap.listerBuildings.allBuildingsColonist)
                            if (building is Building_WorkTable { BillStack.Count: > 0 } table) { bench = table; break; }
                    // This save has no workbench at all (census reads "0 台工作台"), so the probe builds one. Without
                    // that, "no bench with bills" would be indistinguishable from "the channel is broken" — the same
                    // defect the residual-counter probe had, where seven of nine keys had zero carriers.
                    var spawnedBench = false;
                    if (bench is null)
                    {
                        var probeMap = Find.CurrentMap;
                        var tableDef = DefDatabase<ThingDef>.GetNamedSilentFail("TableStonecutter")
                            ?? DefDatabase<ThingDef>.GetNamedSilentFail("TableMachining");
                        var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("Make_StoneBlocks")
                            ?? DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(r => r.ProducedThingDef is not null);
                        if (probeMap is null || tableDef is null || recipe is null)
                        { Answer(id, false, "NoDef", "没有可用的工作台定义或配方"); return; }
                        var cell = probeMap.Center;
                        foreach (var candidate in probeMap.mapPawns.FreeColonistsSpawned)
                            if (candidate?.Position.IsValid == true) { cell = CellFinder.StandableCellNear(candidate.Position, probeMap, 6f); break; }
                        if (!cell.IsValid) cell = probeMap.Center;
                        var made = (Building_WorkTable)GenSpawn.Spawn(tableDef, cell, probeMap);
                        made.SetFaction(RimWorld.Faction.OfPlayer);
                        var bill0 = new Bill_Production(recipe);
                        bill0.repeatMode = BillRepeatModeDefOf.TargetCount;
                        bill0.repeatCount = 7;
                        bill0.targetCount = 42;
                        made.billStack.AddBill(bill0);
                        bench = made;
                        spawnedBench = true;
                    }
                    var before = SharedActions.CaptureBills(bench);
                    if (before is null) { Answer(id, false, "NoFragment", "采样失败（Scribe 忙或没有 BillStack）"); return; }
                    var bill = bench.BillStack[0];
                    var wasRadius = bill.ingredientSearchRadius;
                    var wasRestriction = bill.PawnRestriction;
                    var text = new System.Text.StringBuilder();
                    text.Append($"{(spawnedBench ? "新建" : "现有")}工作台 {bench.def.defName}#{bench.thingIDNumber}，{bench.BillStack.Count} 条配方，片段 {before.Length} B");
                    // Change two settings that travel as different kinds of field: a float and an object reference.
                    bill.ingredientSearchRadius = Math.Abs(wasRadius - 33f) < 0.01f ? 44f : 33f;
                    // A reference field, not just a float: `SetPawnRestriction` scribes `pawnRestriction` with
                    // `Scribe_References.Look` (`:272111`+), which resolves through the cross-reference directory rather
                    // than as inline text. Setting it to a real colonist is the only way to see whether that path works
                    // — assigning null when it is already null changes nothing and would read as "carried".
                    Pawn? restrictee = null;
                    foreach (var candidate in bench.Map.mapPawns.FreeColonistsSpawned)
                        if (candidate != wasRestriction) { restrictee = candidate; break; }
                    bill.SetPawnRestriction(restrictee);
                    var after = SharedActions.CaptureBills(bench);
                    text.Append($"；改了半径 {wasRadius:0.#}→{bill.ingredientSearchRadius:0.#}"
                        + $"、限制者 {(wasRestriction is null ? "无" : wasRestriction.LabelShort)}"
                        + $"→{(bill.PawnRestriction is null ? "无" : bill.PawnRestriction.LabelShort)}");
                    text.Append($"；采样 {(before == after ? "未变" : "变化")}（{before.Length} B → {after?.Length ?? 0} B）");
                    // Apply the sampled fragment to the same stack, which is what the replica side does. It must parse and
                    // land, and it must restore the original values because `before` was taken before the edit.
                    var applied = ScribeBridge.Load(bench.BillStack, before);
                    text.Append($"；回灌 {(applied ? "成功" : "失败")}，半径恢复为 {bench.BillStack[0].ingredientSearchRadius:0.#}"
                        + $"（原 {wasRadius:0.#}）");
                    // The wire, in the same reading: encode the fragment as the command the dialog flush would send and
                    // decode it back. A probe that only calls `ScribeBridge.Load` proves the fragment is well-formed and
                    // says nothing about whether the bytes survive the codec.
                    var encoded = TurboTick.Simulation.CommandCodec.BillsCommand(bench.thingIDNumber, after ?? before, 1);
                    var wireOk = TurboTick.Simulation.CommandCodec.TryDecodeBills(encoded.Payload, out var wireId, out var wireXml)
                        && wireId == bench.thingIDNumber
                        && string.Equals(wireXml, after ?? before, StringComparison.Ordinal);
                    text.Append($"；过信道 {(wireOk ? "一致" : "不一致")}（{encoded.Payload.Length} B）");
                    text.Append($"；写入计数 {SharedActions.BillApply}");
                    Answer(id, applied && before != after && wireOk, applied && wireOk ? "ok" : "LoadFailed", text.ToString());
                }
                return;

            case "kept-cell":
                // The cell-level exception on top of the settlement rectangles.
                //
                // <para>Usage: <c>kept-cell &lt;mapId&gt; &lt;x&gt; &lt;z&gt; &lt;ownerId&gt;</c>. An owner of 0 removes the
                // exception and hands the cell back to the geometry, which is the only way to express "undo" - a stored 0
                // would answer the same as no entry.</para>
                //
                // <para>Answers with the old and new owner as the gates will see them, because the only useful question
                // about this table is whether the gate's answer changed, not whether a row was written.</para>
                {
                    // Comma-separated, matching the other scripted commands in this file (`script-settlement` takes
                    // `name|map|w|h|x,z`), so an operator does not have to remember two separators.
                    var fields = argument.Split(',');
                    if (fields.Length != 4
                        || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId)
                        || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                        || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
                        || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner))
                    {
                        Answer(id, false, "BadArgument", "用法：kept-cell mapId,x,z,ownerSettlementId（owner 为 0 表示撤销）");
                        return;
                    }
                    var data = SettlementGates.Data;
                    if (data is null) { Answer(id, false, "NoData", "据点表还没载入"); return; }
                    var cell = new IntVec3(x, 0, z);
                    var before = data.SettlementAt(mapId, cell);
                    var ownerKnown = owner == 0 || data.Sites.Exists(s => s is { Retired: false } && s.Id == owner);
                    // A refused write must not leave a row behind: `Set` with an unknown owner would answer a settlement
                    // the gate cannot resolve, which is worse than the geometric answer it replaced.
                    var changed = ownerKnown && data.Overrides.Set(mapId, x, z, owner);
                    var after = data.SettlementAt(mapId, cell);
                    // `after` is what the GATE will answer, not what was stored - so for a removal it is the geometric
                    // answer, and printing it without saying so reads as "changed to 1" when the operator asked for "undo".
                    var action = owner == 0 ? "撤销例外（回到矩形规则）" : "写入例外";
                    var text = $"格子 ({x},{z}) 地图 {mapId}：{action}，闸门归属 {before} → {after}"
                        + $"；{(changed ? "已写入" : "没有变化（同值或撤销了不存在的例外）")}"
                        + $"；{data.Overrides.Describe()}"
                        + $"；据点 {data.Sites.Count} 个（未退役 {data.Sites.Count(s => s is { Retired: false })} 个）";
                    // An exception naming a settlement that no longer exists is the failure mode this table can actually
                    // produce: measured after a container restart, the sites were gone (0 settlements) and the cell
                    // still answered 2, so the gate was asking about a settlement the operator could not see. The
                    // override outlives the rectangle it was meant to correct, so the write has to refuse that case
                    // rather than store it.
                    if (owner == 0)
                    {
                        text += before == after
                            ? "；该格本来就没有例外"
                            : $"；撤销后回到矩形规则，归属 {after}";
                    }
                    else if (!ownerKnown)
                    {
                        text += $"；**未写入**：据点 {owner} 不存在或已退役 —— 例外不能指向一个看不见的据点";
                    }
                    else if (after != owner)
                    {
                        text += $"；**未生效**：写入后闸门仍答 {after} —— 检查该格是否在地图 {mapId} 上";
                    }
                    // Persistence is a separate claim from "the write took". `SettlementData.ExposeData` now scribes
                    // `keptCells` as flat quads, and a table that writes in memory but not to the save would look exactly
                    // like this one until the player reloads. So the round trip is measured here, on the real object,
                    // rather than asserted in a comment: save the data, load it into a fresh instance, and read the cell
                    // back through the same `SettlementAt` the gate uses.
                    if (owner != 0 && changed)
                    {
                        var fragment = ScribeBridge.SaveOrNull(data);
                        if (fragment is null)
                        {
                            text += "；持久化自检：Scribe 忙，未能采样";
                        }
                        else
                        {
                            var clone = new SettlementData();
                            var loaded = ScribeBridge.Load(clone, fragment);
                            var survivor = loaded ? clone.Overrides.OwnerAt(mapId, x, z) : -1;
                            text += $"；持久化自检：片段 {fragment.Length} B，回灌 {(loaded ? "成功" : "失败")}，"
                                + $"该格回读 {survivor}（{(survivor == owner ? "一致" : "**不一致**")}）"
                                + $"；回读的例外表 {clone.Overrides.Describe()}";
                        }
                    }
                    Answer(id, changed, changed ? "ok" : "NoChange", text);
                }
                return;

            case "map-probe":
                // What maps exist here, and whether the roster channel's records round-trip. Says "untestable" on a
                // single-map save rather than a green pass: rounds 66-68 all ended that way and a green line would have
                // read as coverage.
                Answer(id, true, "ok", MapLifecycleProbe.Run());
                return;
            case "map-bind-test":
                // **The measurement that proved the first version dead, and then proved the guard right.** Two facts had
                // to be separated:
                // ① `MapParent.Map` is `FindMap(this)` and `FindMap` matches on `maps[i].info.parent` (`:50125`), so
                //    "scan world objects for the one whose Map is this map" can never match a parentless map. Measured
                //    with that first version: `本次绑 0，累计 0；无父 1 张` - a dead branch reading as implemented.
                // ② One parent cannot own two maps, because `FindMap` returns the first match. Measured after switching
                //    to the id link without the guard: `map0` and `map1` both `→Settlement#253(...,HasMap=True)`, a
                //    state vanilla cannot produce.
                // So a successful bind needs a parent that owns nothing, and on this save the only player `MapParent`
                // owns map0. The probe therefore steps aside - clears map0's parent, binds map1, reads `IsPlayerHome`,
                // then restores both pointers. Every step is reversible and reported.
                {
                    var maps = Find.Maps;
                    Map? target = null;
                    foreach (var each in maps) if (each is not null && each.info?.parent is null) { target = each; break; }
                    if (target is null) { Answer(id, true, "ok", "没有无父地图；先跑 second-map"); return; }
                    MapParent? owner = null;
                    foreach (var each in Find.WorldObjects.AllWorldObjects)
                        if (each is MapParent mp && mp.Faction == Faction.OfPlayer && mp.ID > 0) { owner = mp; break; }
                    if (owner is null) { Answer(id, true, "ok", "没有玩家阵营的 MapParent 可当父"); return; }

                    // Who owns that parent right now, if anybody.
                    Map? sitting = null;
                    foreach (var each in maps)
                        if (each is not null && each.info?.parent == owner) { sitting = each; break; }
                    var sittingNote = sitting is null ? "无人占用" : $"现由 map{sitting.uniqueID} 占用（临时让开）";
                    var sittingParent = sitting?.info?.parent;
                    if (sitting is not null)
                    {
                        sitting!.info.parent = null;
                        // **And forget the hint, or the next `BindParents` puts it straight back.** Measured: with the
                        // authority's own hint for map0 still present, the first run reported `本次绑 1 个` while its
                        // target ended with no parent - the binding had gone to map0.
                        MapLifecycleSync.SetParentHint(sitting.uniqueID, 0);
                    }

                    // **The consequence that matters more than `IsPlayerHome`.** `MapInfo.Tile` is
                    // `parent?.Tile ?? PlanetTile.Invalid` (`:83462`), `Invalid` is `new PlanetTile(-1)` (`:772628`), and
                    // `Map.TileInfo` indexes `Find.WorldGrid[Tile]` whose indexer is `tile.Layer.Tiles[tile.tileId]`
                    // (`:781650`). So a parentless map has no tile and `Map.Biome` throws - measured:
                    // `tile=-1,0，biome=**抛 ArgumentOutOfRangeException**`. This reads the same three facts before and
                    // after the bind, so the fix is measured on the property every biome reader depends on.
                    string Terrain()
                    {
                        var bits = new List<string>();
                        try { bits.Add("tile=" + target.Tile); } catch (Exception e) { bits.Add("tile=抛" + e.GetType().Name); }
                        try { bits.Add("biome=" + (target.Biome?.defName ?? "null")); }
                        catch (Exception e) { bits.Add("biome=**抛 " + e.GetType().Name + "**"); }
                        try { bits.Add("weather=" + (target.weatherManager?.curWeather?.defName ?? "null")); }
                        catch (Exception e) { bits.Add("weather=抛" + e.GetType().Name); }
                        return string.Join("，", bits);
                    }
                    var terrainBefore = Terrain();
                    var before = target.IsPlayerHome;
                    MapLifecycleSync.SetParentHint(target.uniqueID, owner.ID);
                    var bound = MapLifecycleSync.BindParents();
                    var after = target.IsPlayerHome;
                    var terrainAfter = Terrain();
                    var afterParent = target.info.parent is null
                        ? "无父"
                        : target.info.parent.def.defName + "#" + target.info.parent.ID;
                    var hasMap = target.info.parent is not null && target.info.parent.HasMap;
                    // The second half of the guard: with map1 now owning the parent, binding map0 to the same one must
                    // be refused. Without this line the guard is only argued, not measured.
                    var refusedAgain = 0;
                    if (sitting is not null)
                    {
                        MapLifecycleSync.SetParentHint(sitting.uniqueID, owner.ID);
                        refusedAgain = MapLifecycleSync.BindParents();
                        if (sitting.info.parent is not null) { sitting.info.parent = null; refusedAgain = -1; }
                        MapLifecycleSync.SetParentHint(sitting.uniqueID, 0);
                    }

                    // Restore everything this probe touched.
                    target.info.parent = null;
                    MapLifecycleSync.SetParentHint(target.uniqueID, 0);
                    if (sitting is not null && sittingParent is not null)
                    {
                        sitting!.info.parent = sittingParent;
                        MapLifecycleSync.SetParentHint(sitting.uniqueID, sittingParent.ID);
                    }

                    Answer(id, true, "ok",
                        $"目标 map{target.uniqueID}（{target.Size.x}x{target.Size.z}）；父指向 {owner.def.defName}#{owner.ID}"
                        + $"（{owner.Faction?.Name ?? "无阵营"}，canBePlayerHome={owner.def.canBePlayerHome}；{sittingNote}）；"
                        + $"绑定前 [{terrainBefore}]、IsPlayerHome={before}、父=无父；本次绑 {bound} 个 → 父={afterParent}、"
                        + $"绑定后 [{terrainAfter}]、"
                        + $"IsPlayerHome={after}、父.HasMap={hasMap}；"
                        + (sitting is null ? "" : $"再绑占用者被拒 {refusedAgain} 个；")
                        + $"已复原（累计绑 {MapLifecycleSync.BoundParents}，提示表 {MapLifecycleSync.ParentHintCount} 条）");
                }
                return;
            case "second-map":
                // Builds a real second map so the construction path can run at all. Separate from the probe on purpose:
                // creating a map mutates the save, and a probe that did it every time would leave a pile of them.
                Answer(id, true, "ok", MapLifecycleProbe.CreateSecond(argument));
                return;
            case "second-map-destroy":
                Answer(id, true, "ok", MapLifecycleProbe.DestroyExtra());
                return;
            case "faction-ideo-probe":
                // Round-trips each faction's belief set. A count of "sent 23, applied 23" proves the records moved and
                // nothing about whether they say the right thing - the encoder and the reader could agree with each
                // other and be wrong together.
                Answer(id, true, "ok", FactionIdeoProbe.Run());
                return;
            case "flight-probe":
                // Round-trips the flight channel. `Pawn_FlightTracker`'s five fields are private and the only public
                // accessor collapses four states into one boolean, so no count can prove this works.
                Answer(id, true, "ok", FlightProbe.Probe(argument));
                return;
            case "designator-map":
                // Whether a replay can be aimed at a map other than the current one. `Designator.Map` is
                // `=> Find.CurrentMap` (`:162502`), so a build drag drawn on map 3 while the authority looks at map 0 used
                // to land on map 0 - same coordinates, valid there, silently wrong. See `DesignatorMapProbe`.
                Answer(id, true, "ok", DesignatorMapProbe.Probe(argument));
                return;
            case "plan-probe":
                // Round-trips the planning-mark channel per map. The channel had two defects a count cannot show: keying by
                // a per-peer id, and publishing/applying only `Find.CurrentMap` while the payload's cell indices are offsets
                // into the sending map. See `PlanProbe`.
                Answer(id, true, "ok", PlanProbe.Probe(argument));
                return;
            case "floatmenu-hook":
                // Whether the right-click chokepoint is attached, and what the sampler would pick up. Cannot test a click:
                // the option's action is a closure that only exists while a real FloatMenu is on the stack. See
                // `FloatMenuHookProbe` for why the three halves are reported separately.
                Answer(id, true, "ok", FloatMenuHookProbe.Probe(argument));
                return;
            case "floatmenu-probe":
                // Why a replica's right-click menu comes up empty. `ReplicaFloatMenuProbe` already logs the empty case;
                // what it cannot say is WHERE the emptiness comes from, because the options are built by
                // `FloatMenuOptionProvider`s that each ask the simulation a feasibility question.
                //
                // <para>`FloatMenuMakerMap.GetOptions` returns the list; every provider contributes through it. The
                // providers are the thing to count, not the options: an empty result with fourteen registered providers
                // means every one of them answered "no", which points at the feasibility questions rather than at a
                // missing registration.</para>
                //
                // <para>This runs on the authority (where a real map and real pawns exist) and reports what the vanilla
                // function returns for the same input a replica would use. If the authority gets options for a
                // right-click the replica does not, the difference is in the answers, not in the code path.</para>
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var pawn = map.mapPawns?.AllPawnsSpawned?.FirstOrDefault(p => p?.IsColonist == true);
                    if (pawn is null) { Answer(id, false, "NoPawn", "本图没有殖民者"); return; }
                    // The public entry takes the click position and hands back the context it built, so the probe does
                    // not assemble a `FloatMenuContext` by hand - its fields are what vanilla fills
                    // (`:656029-656053`: clicked cell, things, pawns, room, zone are all cached from the position).
                    var here = new List<FloatMenuOption>();
                    var next = new List<FloatMenuOption>();
                    FloatMenuContext? hereContext = null;
                    FloatMenuContext? nextContext = null;
                    string hereError = "", nextError = "";
                    try
                    {
                        here = FloatMenuMakerMap.GetOptions([pawn], pawn.Position.ToVector3Shifted(), out hereContext) ?? [];
                    }
                    catch (Exception error) { hereError = error.GetType().Name + ": " + error.Message; }
                    try
                    {
                        var cell = pawn.Position + IntVec3.East;
                        next = FloatMenuMakerMap.GetOptions([pawn], cell.ToVector3Shifted(), out nextContext) ?? [];
                    }
                    catch (Exception error) { nextError = error.GetType().Name + ": " + error.Message; }

                    // Provider count, so "no options" can be read against "no providers".
                    var providers = 0;
                    var providerNames = new List<string>();
                    try
                    {
                        foreach (var type in typeof(FloatMenuOptionProvider).Assembly.GetTypes())
                        {
                            if (!typeof(FloatMenuOptionProvider).IsAssignableFrom(type) || type.IsAbstract) continue;
                            providers++;
                            if (providerNames.Count < 6) providerNames.Add(type.Name);
                        }
                    }
                    catch (Exception) { }

                    var text = $"殖民者「{pawn.LabelShort}」在 ({pawn.Position.x},{pawn.Position.z})"
                        + $"：本格 {here.Count} 个选项{(hereError.Length > 0 ? "（" + hereError + "）" : "")}"
                        + $"，东邻格 {next.Count} 个选项{(nextError.Length > 0 ? "（" + nextError + "）" : "")}"
                        + $"；已注册 provider {providers} 个（{string.Join(", ", providerNames)}…）"
                        + $"；`FloatMenuOption.Chosen`（`:179121-179131`）是唯一执行口，只调 `action?.Invoke()`"
                        + "，所以「点了没反应」与「菜单为空」是两个可分开测的问题";
                    Answer(id, true, "ok", text);
                }
                return;

            case "settings-probe":
                // `PlaySettings` is a `Game` object (`:724454-724492`) and it mixes two kinds of field:
                //
                //   - per-player display toggles (`showZones`, `showBeauty`, `lockNorthUp`, every overlay): each peer
                //     may differ, and copying them around would fight the player's own preference;
                //   - SIMULATION INPUTS: `useWorkPriorities` (`:724492`), `autoHomeArea` (`:724474`),
                //     `autoRebuild` (`:724476`). These change what the colony does, not what is drawn.
                //
                // Measured on the authority: a replica that toggles `useWorkPriorities` writes `playSettings` locally
                // and the host never learns, so `Pawn_WorkSettings.GetPriority` (`:331069-331077`) keeps flattening
                // every non-zero priority to 3 on one side and returns the real number on the other. The same work
                // order then means two different things, which is the one kind of divergence a player cannot see.
                {
                    var settings = Current.Game?.playSettings;
                    if (settings is null) { Answer(id, false, "NoGame", "没有当前游戏"); return; }
                    // Count how many pawns' priority readings would differ between the two modes. A pawn whose work
                    // settings collapse all non-zero priorities to one value is exactly the pawn whose displayed
                    // number is a lie when the two peers disagree about this switch.
                    var humanlike = 0;
                    var withPriorities = 0;
                    var collapsing = 0;
                    foreach (var map in Find.Maps)
                        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                        {
                            if (pawn?.RaceProps?.Humanlike != true) continue;
                            humanlike++;
                            var work = pawn.workSettings;
                            if (work is null) continue;
                            withPriorities++;
                            // In non-priority mode every enabled work type reads 3. If more than one enabled type
                            // exists, the numbers a player set are being ignored.
                            var distinct = new HashSet<int>();
                            foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                            {
                                var value = work.GetPriority(type);
                                if (value > 0) distinct.Add(value);
                            }
                            if (distinct.Count == 1 && distinct.Contains(3)) collapsing++;
                        }
                    var text = $"useWorkPriorities={(settings.useWorkPriorities ? "开" : "关")}"
                        + $"，autoHomeArea={(settings.autoHomeArea ? "开" : "关")}"
                        + $"，autoRebuild={(settings.autoRebuild ? "开" : "关")}"
                        + $"；人形小人 {humanlike} 个（有 workSettings {withPriorities} 个，优先级被压成单一 3 的 {collapsing} 个）"
                        + (settings.useWorkPriorities
                            ? "；当前是手动优先级模式，勾选框一改就会改变每个小人的实际工作顺序"
                            : "；当前是简单模式，所有非零优先级都读作 3（`:331073`）—— 副本若与本端不同，同一个数字表示不同的活")
                        ;
                    Answer(id, true, "ok", text);
                }
                return;

            case "ideo-count":
                // Why the ideo channel counts 4 pawns when the colony has 71. Three numbers that must add up:
                // spawned / has a tracker / has an ideoligion. A gap in the first two is `mapPawns` vs the pawn's own
                // field; a gap in the last is a pawn the game itself treats as belief-less.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var spawned = 0; var withTracker = 0; var withIdeo = 0; var babies = 0;
                    var humans = 0; var animals = 0;
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        spawned++;
                        if (pawn?.RaceProps?.Humanlike == true) humans++; else animals++;
                        if (pawn?.ideo is not null) withTracker++;
                        if (pawn?.ideo?.Ideo is not null) withIdeo++;
                        if (pawn is not null && pawn.DevelopmentalStage.Baby()) babies++;
                    }
                    Answer(id, true, "ok",
                        $"本图小人 {spawned} 个（人形 {humans} / 其它 {animals}，婴儿 {babies}）"
                        + $"；有 ideo tracker {withTracker} 个；有意识形态 {withIdeo} 个");
                }
                return;

            case "script-ideo":
                // Converts a colonist to an ideoligion and reads the channel's payload back.
                //
                // The conversion goes through `SetIdeo` rather than writing the field, because `SetIdeo` is what the game
                // itself calls and it also rolls `Certainty` from `ConversionTuning.InitialCertaintyRange` (`:472295`) -
                // a payload that only mirrors a field write would prove the field is readable, not that a real
                // conversion travels. Two calls with different arguments give two different ids, so "carried" and
                // "happens to equal the default" cannot read the same.
                {
                    var ideoMap = Find.CurrentMap;
                    if (ideoMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var ideoReport = new StringBuilder();
                    var ideos = Find.IdeoManager?.IdeosListForReading;
                    if (ideos is null || ideos.Count == 0)
                    {
                        Answer(id, false, "NoIdeo", "这个世界还没有意识形态（经典模式）");
                        return;
                    }
                    ideoReport.Append("世界 ").Append(ideos.Count).Append(" 个意识形态：");
                    foreach (var one in ideos)
                        ideoReport.Append('#').Append(one.id).Append('「').Append(one.name).Append('」');
                    ideoReport.Append("；");
                    // Which id to convert to: the argument if given, else the ideoligion the pawn is *not* in, so the
                    // call always changes something.
                    var wanted = ideos[0];
                    var converted = 0;
                    foreach (var pawn in ideoMap.mapPawns.FreeColonistsSpawned)
                    {
                        if (converted >= 2) break;
                        if (pawn?.ideo is null) continue;
                        foreach (var one in ideos)
                            if (one != pawn.ideo.Ideo) { wanted = one; break; }
                        if (pawn.ideo.Ideo == wanted) continue;
                        var beforeId = pawn.ideo.Ideo?.id ?? -1;
                        var beforeCertainty = pawn.ideo.Certainty;
                        pawn.ideo.SetIdeo(wanted);
                        var payload = IdeoSync.EncodeFor(pawn);
                        ideoReport.Append(pawn.LabelShort).Append('#').Append(pawn.thingIDNumber)
                            .Append(" 信念 ").Append(beforeId).Append('→').Append(pawn.ideo.Ideo?.id ?? -1)
                            .Append(" 确定度 ").Append(beforeCertainty.ToString("F2", CultureInfo.InvariantCulture))
                            .Append('→').Append(pawn.ideo.Certainty.ToString("F2", CultureInfo.InvariantCulture))
                            .Append("；载荷 ").Append(payload ?? "(无)").Append('；');
                        converted++;
                    }
                    if (converted == 0) ideoReport.Append("没有可转换的殖民者");
                    Answer(id, true, "ok", ideoReport.ToString());
                }
                return;

            case "royalty-count":
                // The royal title tracker is a second, separate ability list. `Pawn_RoyaltyTracker.abilities`
                // (`_lab/asm/…:474597`) is filled by `UpdateAvailableAbilities` (`:475803`) from
                // `RoyalTitleDef.grantedAbilities`, while `AbilitySync` reads `Pawn_AbilityTracker.abilities`
                // (`:468336` `GainAbility`). Two lists, one channel — so before building a second one, measure whether
                // this save has any titled pawn at all: a channel with zero carriers cannot be told apart from a broken
                // one, which is exactly the trap `ResidualCounterSync` fell into with seven of its nine keys.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var titled = 0; var withPermits = 0; var withFavor = 0; var royalAbilities = 0;
                    var samples = new List<string>();
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        var royal = pawn?.royalty;
                        if (royal is null) continue;
                        var titles = royal.AllTitlesForReading;
                        if (titles is { Count: > 0 })
                        {
                            titled++;
                            royalAbilities += royal.AllAbilitiesForReading?.Count ?? 0;
                            if (samples.Count < 4)
                                samples.Add($"{pawn!.LabelShortCap}#{pawn.thingIDNumber}="
                                    + string.Join("+", titles.Select(t => t.def.defName))
                                    + $"（能力 {royal.AllAbilitiesForReading?.Count ?? 0}，许可 {royal.AllFactionPermits?.Count() ?? 0}）");
                        }
                        if (royal.AllFactionPermits is { } permits && permits.Any()) withPermits++;
                        // `favor` is a private `Dictionary<Faction, int>` (`:474589`) with no public list; the
                        // supported way to read it is `GetFavor(faction)` (`:474857`) per faction, so the factions
                        // have to come from `Find.FactionManager`. `AllFactionPermits` (`:474593`) is public but only
                        // says which factions granted a permit, not which track favor.
                        foreach (var faction in Find.FactionManager.AllFactionsListForReading)
                            if (royal.GetFavor(faction) != 0) { withFavor++; break; }
                    }
                    Answer(id, true, "ok",
                        $"本图 {map.mapPawns.AllPawnsSpawned.Count()} 个小人；有 royalty tracker 的 {map.mapPawns.AllPawnsSpawned.Count(p => p?.royalty is not null)} 个"
                        + $"；有头衔 {titled} 个；有许可 {withPermits} 个；有好感记录 {withFavor} 个；皇家能力合计 {royalAbilities}"
                        + (samples.Count > 0 ? "；样本 " + string.Join("；", samples) : "；样本 无"));
                    return;
                }
            case "patch-inventory":
                // **What Harmony actually patched, counted per instance instead of globally (round 127).**
                //
                // The previous version read `Harmony.GetAllPatchedMethods()` and printed three things: a total, a type
                // set, and a `Type.Method;` list truncated at 12 000 characters. All three were **global** - every mod in
                // the process contributes - and the list was truncated without saying so. Measured consequence this
                // round: after adding `PawnGotoAction`, the list appeared to *lose* `Designator_Plan_Remove.CanDesignateCell`
                // and `Designator_PlantsCut.CanDesignateThing`, which reads exactly like two patches having vanished. They
                // had not: the new entry pushed two old ones past the character cut. A reading that cannot distinguish
                // "absent" from "past the cut" is not evidence, and this is the instrument every round uses to decide
                // coverage.
                //
                // So: attach to our own instance only (via `GetPatchInfo` owners), and print the two counts side by side
                // so a global/modded difference stays visible instead of being folded into one number.
                {
                    var self = TurboTickMod.Harmony;
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    // **One instance is the whole truth for this mod.** `TurboTickMod.Harmony` is the only `new
                    // Harmony(...)` in the project (`TurboTickMod.cs:39`) - checked, not assumed, before writing this:
                    // a list of auxiliary ids would have been a second place to drift.
                    if (self is not null) ids.Add(self.Id);

                    var patched = new StringBuilder();
                    var types = new HashSet<string>(StringComparer.Ordinal);
                    var mine = 0;
                    var mineTypes = new HashSet<string>(StringComparer.Ordinal);
                    var global = 0;
                    var omitted = 0;
                    foreach (var method in HarmonyLib.Harmony.GetAllPatchedMethods())
                    {
                        if (method?.DeclaringType is null) continue;
                        global++;
                        var info = HarmonyLib.Harmony.GetPatchInfo(method);
                        var isMine = info is not null
                                     && (info.Prefixes.Any(p => ids.Contains(p.owner))
                                         || info.Postfixes.Any(p => ids.Contains(p.owner))
                                         || info.Transpilers.Any(p => ids.Contains(p.owner))
                                         || info.Finalizers.Any(p => ids.Contains(p.owner)));
                        if (!isMine) continue;
                        mine++;
                        mineTypes.Add(method.DeclaringType.Name);
                        // The list is still bounded - a report is not a database - but the bound is now stated with the
                        // number dropped, so "not in the list" can never be read as "not patched".
                        if (patched.Length < 24000)
                            patched.Append(method.DeclaringType.Name).Append('.').Append(method.Name).Append(';');
                        else
                            omitted++;
                    }
                    foreach (var method in HarmonyLib.Harmony.GetAllPatchedMethods())
                        if (method?.DeclaringType is not null) types.Add(method.DeclaringType.Name);

                    var prefix = self?.Id ?? "(未初始化)";
                    Answer(id, true, "ok",
                        $"补丁实例 {prefix} / 本实例方法 {mine} 个 / 本实例去重类型 {mineTypes.Count} 个"
                        + $" / 进程内全部补丁 {global} 个、{types.Count} 个类型（含其他 mod，勿当作本 mod 覆盖）"
                        + (omitted > 0 ? $" / 列表截断，省略 {omitted} 个" : " / 列表完整")
                        + "\n" + string.Join(",", mineTypes.OrderBy(t => t, StringComparer.Ordinal))
                        + "\n" + patched);
                }
                return;

            case "royalty-grant":
                // Creates the carrier `royalty-count` reports as zero, so the royalty-derived UI has something to read.
                Answer(id, true, "ok", ScriptedSettlement.GrantTitle(argument));
                return;
            case "settlement-counters":
                // **The six gate counters had no reader on the authority (round 95).** `SettlementGates.Summary()` was called
                // from exactly one place: `AuthoritativeReplica`'s frame log, inside `if (AppliedFrames % 300 == 0)`. That
                // block only runs on a *replica*, and the authority reports `副本=0` - so on the machine that decides for
                // every player's pawns, `ReservationsRefused`, `WorkRefused`, `DesignationsRefused`, `PassRefused`,
                // `RelaxRefused`, `TradeRefused` and `WarRefused` were written and never read. A gate that stopped
                // refusing anything, or one that refused everything, produced the same unread numbers.
                //
                // `reset` first, then read: a counter that only ever accumulates cannot say whether the refusals happened
                // during the action just taken or three hours ago. Argument form `<action> [reset]`, where action is one of
                // the six acts, so one reading covers "did the door just refuse" without a second round trip.
                {
                    var parts = (argument ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
                    {
                        SettlementGates.ResetCounters();
                        Answer(id, true, "ok", "据点权限计数器已清零");
                        return;
                    }
                    Answer(id, true, "ok", SettlementGates.Summary() + "\n  " + SettlementGates.Breakdown());
                }
                return;
            case "thoughts":
                // The two halves of `ThoughtHandler`: memories are data the channel carries, situational thoughts are
                // derived on each peer and can freeze because nothing ticks a replica.
                {
                    var arg = (argument ?? string.Empty).Trim();
                    if (arg.Equals("probe", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("probe ", StringComparison.Ordinal))
                        Answer(id, true, "ok", ThoughtsProbe.Probe(arg.Length > 5 ? arg[6..].Trim() : null));
                    else if (arg.Equals("refresh", StringComparison.OrdinalIgnoreCase))
                        Answer(id, true, "ok", ThoughtsProbe.Refresh(null) + "；" + ThoughtsProbe.Summary());
                    else
                        Answer(id, true, "ok", ThoughtsProbe.Summary());
                }
                return;
            case "health-state":
                // The `healthState` byte. Invisible where it is wrong: a replica with a stale `Mobile` for a dead pawn
                // looks normal until somebody opens the character card and sees a banish button for a corpse.
                {
                    var arg = (argument ?? string.Empty).Trim();
                    if (arg.Equals("probe", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("probe ", StringComparison.Ordinal))
                        Answer(id, true, "ok", HealthStateProbe.Probe(arg.Length > 5 ? arg[6..].Trim() : null));
                    else
                        Answer(id, true, "ok", HealthStateProbe.Summary());
                }
                return;
            case "royalty-channel":
                // The channel's own census and round trip. `royalty-count` shows the *state* (titles, permits, favor) and
                // `royalty-grant` creates one; neither can say whether the channel moves what it reads. Without this a
                // replica keeping last session's title looks identical to one receiving the current title.
                {
                    var parts = (argument ?? string.Empty).Trim();
                    if (parts.Equals("probe", StringComparison.OrdinalIgnoreCase) || parts.StartsWith("probe ", StringComparison.Ordinal))
                        Answer(id, true, "ok", RoyaltySync.Probe(parts.Length > 5 ? parts[6..].Trim() : null));
                    else
                        Answer(id, true, "ok", RoyaltySync.Summary());
                }
                return;
            case "pawn-leave-probe":
                // The three actions this command exists for are reached from a *dialog's* callback, which the gizmo
                // capture never sees - so the observable question is "did a click publish anything", and a probe that
                // only exercises the codec would answer a different one. With no argument it reports the counters;
                // with `banish <pawnId>` / `recruit <pawnId>` / `release <pawnId>` it runs the same capture path the
                // UI runs, so the number moving is evidence the patch is installed on the real button.
                {
                    var parts = (argument ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && PawnLeaveProbe.TryRun(parts[0], parts[1], out var outcome))
                    {
                        Answer(id, true, "ok", outcome + "\n  " + PawnLeaveProbe.Summary());
                        return;
                    }
                    Answer(id, true, "ok", PawnLeaveProbe.Summary());
                }
                return;
            case "settlement-grant":
                // The one edit the game only exposes through a window, which a headless authority cannot open: without this
                // every gate answered from the visitor mask for every player, so two settlements with distinct owners were
                // indistinguishable from two settlements with no owners.
                Answer(id, true, "ok", ScriptedSettlement.Grant(argument));
                return;
            case "surgery-probe":
                // Creates a real surgery bill on a real pawn, then reports what the channel saw. A bill added by a probe
                // has to go through the same patch the UI goes through, or the probe proves the codec and nothing about
                // the capture - which is the half that was broken.
                Answer(id, true, "ok", SurgeryProbe.Probe(argument));
                return;
            case "training-probe":
                Answer(id, true, "ok", PawnTrackerSync.Probe(argument));
                return;
            case "precept-probe":
                // Per-ideoligion idempotent round-trip: encode, apply the same string back, re-encode. `AddPrecept` re-sorts
                // the list and `RemovePrecept` can substitute a random precept, so a count cannot tell a correct reconcile
                // from one that churns.
                Answer(id, true, "ok", PreceptSync.Probe(argument));
                return;
            case "add-gene":
                // **Fixture, not a gate.** Round 142's hemogen half had no carrier in the save, which makes the reading
                // "mechanism complete, unmeasured" - true but useless. A xenogene can be added to any pawn through
                // vanilla `Pawn_GeneTracker.AddGene(def, xenogene: true)`, so the fixture is one call, and the toggle
                // can then be measured on a real `Gene_Hemogen`. `GeneDefOf.Hemogenic` is the gene that grants the
                // hemogen metabolism (`GeneDefOf` members: Bloodfeeder, Deathless, XenogermReimplanter, Hemogenic, …).
                {
                    var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var genePawnId))
                    {
                        Answer(id, false, "argument", "用法：add-gene \"<小人 id> <geneDefName>\"");
                        return;
                    }
                    var defName = parts[1];
                    var def = DefDatabase<GeneDef>.GetNamedSilentFail(defName);
                    if (def is null)
                    {
                        Answer(id, false, "missing-def", $"没有这个基因 def：{defName}");
                        return;
                    }
                    if (!AuthoritativeReplica.FindPawnAny(genePawnId, out var genePawn) || genePawn?.genes is not { } tracker)
                    {
                        Answer(id, false, "missing-pawn", $"本端找不到小人 #{genePawnId}（或它没有基因追踪器）");
                        return;
                    }
                    var xenoBefore = tracker.Xenogenes.Count;
                    var endoBefore = tracker.Endogenes.Count;
                    tracker.AddGene(def, xenogene: true);
                    Answer(id, true, "ok",
                        $"小人 #{genePawnId} 加异种基因 {def.defName}：异种 {xenoBefore} → {tracker.Xenogenes.Count}"
                        + $"，内源 {endoBefore} → {tracker.Endogenes.Count}"
                        + $"，血原基因 {(tracker.GetFirstGeneOfType<Gene_Hemogen>() is null ? "仍无" : "已具备")}");
                }
                return;
            case "settlement-jobgate":
                Answer(id, true, "ok", SettlementJobProbe.Run(argument));
                return;
            case "settlement-container":
                Answer(id, true, "ok", ContainerOwnershipProbe.Run(argument));
                return;
            case "trade-payment":
                Answer(id, true, "ok", TradePaymentProbe.Run(argument));
                return;
            case "settlement-cellgate":
                Answer(id, true, "ok", ScriptedSettlement.CellGateProbe(argument));
                return;
            case "settlement-forbid-probe":
                Answer(id, true, "ok", ScriptedSettlement.ForbidVisibilityProbe(argument));
                return;
            case "play-settings-probe":
                // The eleven default-care settings on the play-settings row (round 173).
                Answer(id, true, "ok", PlaySettingsProbe.Run(argument));
                return;
            case "visual-effect-probe":
                // The visual Ethereal carriers (round 171): the map diff must keep skipping the category, so the
                // three visual classes need their own channel.
                Answer(id, true, "ok", EtherealVisualProbe.Run(argument));
                return;
            case "corpse-tracker":
                // The unnatural corpse's state machine (round 164): `UnnaturalCorpseTracker` is persisted state, and
                // the channel used to carry one field of it (`research`).
                Answer(id, true, "ok", CorpseTrackerProbe.Run(argument));
                return;
            case "message-archive":
                // The message half of the letter channel (round 163): `historical` decides whether a message enters
                // `Find.Archive`, and the look target decides whether clicking it does anything. Both were missing.
                Answer(id, true, "ok", MessageArchiveProbe.Run(argument));
                return;
            case "faction-create":
                // The faction-creation channel (round 162). Read as three halves - notice, build, apply - because a
                // faction cannot be removed from `FactionManager` unless it is temporary, so a fixture that really
                // added one would change the faction screen forever.
                Answer(id, true, "ok", FactionCreationProbe.Run(argument));
                return;
            case "policy-table":
                // The four policy tables (round 160). `list` prints what each table holds; anything else creates a
                // reading policy, force-publishes, deletes it locally (the replica state) and applies the captured
                // row - see `PolicyTableProbe` for why the whole fragment is compared rather than a flag.
                {
                    var mode = (argument ?? string.Empty).Trim();
                    Answer(id, true, "ok", mode.Length == 0 || mode.Equals("list", StringComparison.OrdinalIgnoreCase)
                        ? PolicyTableProbe.List()
                        : mode.Equals("size", StringComparison.OrdinalIgnoreCase)
                            ? PolicyTableProbe.Size()
                            : PolicyTableProbe.Run(mode));
                    return;
                }
            case "auto-rebuild-probe":
                Answer(id, true, "ok", ScriptedSettlement.AutomationProbe(argument));
                return;
            case "ideo-lock-probe":
                // The description lock (round 152). `Ideo.RegenerateDescription` (`:493062-493070`) rewrites the description
                // unless `descriptionLocked` is set, so a replica that never receives the flag overwrites the text the
                // player locked. This flips the flag, publishes, asserts the table's trailing field, then flips it back
                // locally without publishing and lets the row put it back.
                {
                    var ideo = Find.IdeoManager?.IdeosListForReading?.FirstOrDefault(i => i is not null);
                    if (ideo is null)
                    {
                        Answer(id, true, "ok", "描述锁：本端没有 ideoligion（这一半未测）");
                        return;
                    }
                    var original = ideo.descriptionLocked;
                    ideo.descriptionLocked = !original;
                    var rows = IdeoSync.Publish();
                    var row = rows?.FirstOrDefault(r => r.Pawn == 0);
                    var entry = row?.Payload?.Split(';')
                        .FirstOrDefault(e => e.StartsWith(ideo.id.ToString() + "#", StringComparison.Ordinal));
                    var fields = entry?.Split('#');
                    var encodedFine = fields is not null && fields.Length > 3 && fields[^1] == (ideo.descriptionLocked ? "1" : "0");
                    // Decoy: the old flag back, without publishing.
                    ideo.descriptionLocked = original;
                    var appliedBefore = IdeoSync.LockApply;
                    if (row is not null) IdeoSync.Apply(row);
                    var readBack = ideo.descriptionLocked;
                    var delta = IdeoSync.LockApply - appliedBefore;
                    var readBackFine = readBack == !original;
                    ideo.descriptionLocked = original;
                    IdeoSync.Publish();
                    Answer(id, true, "ok",
                        $"描述锁 ideo {ideo.id}#{ideo.name}：原 {(original ? "锁" : "未锁")} → 设为 "
                        + $"{(original ? "未锁" : "锁")}，行 "
                        + $"{(entry ?? "无")} → {(encodedFine ? "准" : "**不准**")}；先改回旧值（不发）再应用该行，读回 "
                        + $"{(readBack ? "锁" : "未锁")} → {(readBackFine ? "准" : "**不准**")}（应用 +{delta}）；已复原");
                }
                return;
            case "name-deadline-probe":
                // Baby naming deadline (round 151). Puts a deadline on a pawn, publishes, asserts the encoded segment, then
                // writes -1 locally without publishing and lets the row put it back.
                {
                    Verse.Pawn? subject = null;
                    foreach (var map in Verse.Find.Maps)
                    {
                        foreach (var candidate in map.mapPawns.AllPawnsSpawned)
                        {
                            if (candidate?.def?.race?.Humanlike != true || candidate.Dead || candidate.Destroyed) continue;
                            subject = candidate;
                            break;
                        }
                        if (subject is not null) break;
                    }
                    if (subject is null)
                    {
                        Answer(id, true, "ok", "婴儿命名：本端没有人类小人（这一半未测）");
                        return;
                    }
                    var now = Verse.Find.TickManager?.TicksGame ?? 0;
                    var original = subject.babyNamingDeadline;
                    subject.babyNamingDeadline = now + 60000;
                    var rows = PawnTrackerSync.Publish();
                    var row = rows?.FirstOrDefault(r => r.Pawn == subject.thingIDNumber);
                    var segment = row?.Trackers?.Split(';').FirstOrDefault(part => part.StartsWith("b:", StringComparison.Ordinal));
                    var encodedFine = segment == "b:" + (now + 60000);
                    // Decoy: no deadline locally, without publishing.
                    subject.babyNamingDeadline = -1;
                    var appliedBefore = PawnTrackerSync.NamingDeadlineRows;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readBack = subject.babyNamingDeadline;
                    var delta = PawnTrackerSync.NamingDeadlineRows - appliedBefore;
                    var readBackFine = readBack == now + 60000;
                    subject.babyNamingDeadline = original;
                    PawnTrackerSync.Publish();
                    Answer(id, true, "ok",
                        $"婴儿命名 小人 #{subject.thingIDNumber}：原 {original} → 设为 {now + 60000}，段 {segment ?? "无"} → "
                        + $"{(encodedFine ? "准" : "**不准**")}；先设成本端 -1（不发）再应用该行，读回 {readBack} → "
                        + $"{(readBackFine ? "准" : "**不准**")}（应用 +{delta}）；已复原 {original}");
                }
                return;
            case "anomaly-difficulty-probe":
                // The anomaly half of `Difficulty` (round 150). The settings dialog writes five fields, and nothing carried
                // them: this changes them, publishes, asserts the encoded row, then writes decoys locally without
                // publishing and lets the row overwrite them.
                {
                    var difficulty = Find.Storyteller?.difficulty;
                    if (difficulty is null)
                    {
                        Answer(id, true, "ok", "异象难度：本端没有 Storyteller（这一半未测）");
                        return;
                    }
                    var oldPlaystyle = difficulty.AnomalyPlaystyleDef;
                    var oldOverride = difficulty.overrideAnomalyThreatsFraction;
                    var oldInactive = difficulty.anomalyThreatsInactiveFraction;
                    var oldActive = difficulty.anomalyThreatsActiveFraction;
                    var oldStudy = difficulty.studyEfficiencyFactor;
                    var otherPlaystyle = DefDatabase<AnomalyPlaystyleDef>.AllDefsListForReading
                        .FirstOrDefault(p => p != oldPlaystyle);
                    difficulty.anomalyThreatsInactiveFraction = 0.11f;
                    difficulty.anomalyThreatsActiveFraction = 0.47f;
                    difficulty.overrideAnomalyThreatsFraction = 0.63f;
                    difficulty.studyEfficiencyFactor = 1.25f;
                    if (otherPlaystyle is not null) difficulty.AnomalyPlaystyleDef = otherPlaystyle;
                    var row = DifficultySync.Publish();
                    var encodedFine = row is not null
                        && Math.Abs(row.Inactive - 0.11f) < 0.0001f && Math.Abs(row.Active - 0.47f) < 0.0001f
                        && row.Override is not null && Math.Abs(row.Override.Value - 0.63f) < 0.0001f
                        && Math.Abs(row.Study - 1.25f) < 0.0001f
                        && (otherPlaystyle is null || row.Playstyle == otherPlaystyle.defName);
                    // Decoys: the old values back, without publishing.
                    difficulty.overrideAnomalyThreatsFraction = oldOverride;
                    difficulty.anomalyThreatsInactiveFraction = oldInactive;
                    difficulty.anomalyThreatsActiveFraction = oldActive;
                    difficulty.studyEfficiencyFactor = oldStudy;
                    difficulty.AnomalyPlaystyleDef = oldPlaystyle;
                    var appliedBefore = DifficultySync.ChangesApplied;
                    if (row is not null) DifficultySync.Apply(row);
                    // Read the state NOW: the message below was formatted after the restore, so it printed the
                    // restored playstyle next to "读回" and contradicted the line above it (round 150 self-check).
                    var readPlaystyle = difficulty.AnomalyPlaystyleDef?.defName ?? "无";
                    var readInactive = difficulty.anomalyThreatsInactiveFraction;
                    var readActive = difficulty.anomalyThreatsActiveFraction;
                    var readOverride = difficulty.overrideAnomalyThreatsFraction;
                    var readStudy = difficulty.studyEfficiencyFactor;
                    var readBackFine = Math.Abs(difficulty.anomalyThreatsInactiveFraction - 0.11f) < 0.0001f
                        && Math.Abs(difficulty.anomalyThreatsActiveFraction - 0.47f) < 0.0001f
                        && difficulty.overrideAnomalyThreatsFraction is not null
                        && Math.Abs(difficulty.overrideAnomalyThreatsFraction.Value - 0.63f) < 0.0001f
                        && Math.Abs(difficulty.studyEfficiencyFactor - 1.25f) < 0.0001f
                        && (otherPlaystyle is null || difficulty.AnomalyPlaystyleDef == otherPlaystyle);
                    var delta = DifficultySync.ChangesApplied - appliedBefore;
                    // Restore, and publish so the channel's signature matches the restored state.
                    difficulty.overrideAnomalyThreatsFraction = oldOverride;
                    difficulty.anomalyThreatsInactiveFraction = oldInactive;
                    difficulty.anomalyThreatsActiveFraction = oldActive;
                    difficulty.studyEfficiencyFactor = oldStudy;
                    difficulty.AnomalyPlaystyleDef = oldPlaystyle;
                    DifficultySync.Publish();
                    Answer(id, true, "ok",
                        $"异象难度：原 {oldPlaystyle?.defName ?? "无"}/{oldInactive}/{oldActive}/{oldOverride?.ToString() ?? "null"}/{oldStudy} → "
                        + $"设为 {otherPlaystyle?.defName ?? oldPlaystyle?.defName}/0.11/0.47/0.63/1.25，行 "
                        + $"{(row is null ? "无" : row.Playstyle + "/" + row.Inactive + "/" + row.Active + "/" + (row.Override?.ToString() ?? "null") + "/" + row.Study)} → "
                        + $"{(encodedFine ? "准" : "**不准**")}；先写回旧值（不发）再应用该行，读回 "
                        + $"{readPlaystyle}/{readInactive}/{readActive}/{readOverride?.ToString() ?? "null"}/{readStudy} → "
                        + $"{(readBackFine ? "准" : "**不准**")}（应用 +{delta}）；已复原");
                }
                return;
            case "ideo-style-probe":
                // The style column of a building precept's payload (round 149). `Precept_ThingDef.ThingDef`'s setter rolls a
                // random style category when the building is style-editable and no category claims it, and a replica rolls
                // with its own RNG - so the synced pair has to be written last. This probe points a thing precept at a
                // style-capable building, reads the pair the payload would carry, decoys the local pair, applies the row
                // and reads it back.
                {
                    var ideo = Find.IdeoManager?.IdeosListForReading?.FirstOrDefault(i => i?.PreceptsListForReading is { Count: > 0 });
                    if (ideo?.style is null)
                    {
                        Answer(id, true, "ok", "戒律风格：本端没有 ideoligion（这一半未测）");
                        return;
                    }
                    // A building some style category actually styles, so `AllPossibleStylesForBuilding` is non-empty and the
                    // setter's random branch is the one that would have run.
                    ThingDef? building = null;
                    foreach (var candidate in DefDatabase<ThingDef>.AllDefsListForReading)
                    {
                        if (candidate?.canEditAnyStyle != true) continue;
                        try { if (Precept_ThingDef.AllPossibleStylesForBuilding(candidate).Count > 0) { building = candidate; break; } }
                        catch { }
                    }
                    if (building is null)
                    {
                        Answer(id, true, "ok", "戒律风格：本端没有可由风格类别覆盖的建筑（这一半未测）");
                        return;
                    }
                    Precept_ThingDef? subject = null;
                    var fixture = false;
                    foreach (var precept in ideo.PreceptsListForReading)
                        if (precept is Precept_ThingDef { ThingDef: not null } existing) { subject = existing; break; }
                    var def = DefDatabase<PreceptDef>.AllDefsListForReading
                        .FirstOrDefault(p => p?.preceptClass == typeof(Precept_ThingDef));
                    if (subject is null && def is not null)
                    {
                        subject = (Precept_ThingDef)PreceptMaker.MakePrecept(def);
                        ideo.AddPrecept(subject);
                        subject.Init(ideo);
                        fixture = true;
                    }
                    if (subject is null)
                    {
                        Answer(id, true, "ok", "戒律风格：本端没有建筑类 precept，也没有可造的 PreceptDef（这一半未测）");
                        return;
                    }
                    var ordinal = -1;
                    var seen = 0;
                    foreach (var precept in ideo.PreceptsListForReading)
                    {
                        if (precept?.def?.defName != subject.def.defName) continue;
                        if (ReferenceEquals(precept, subject)) { ordinal = seen; break; }
                        seen++;
                    }
                    var oldThing = subject.ThingDef;
                    var oldPair = subject.ThingDef is null ? null : ideo.style.StyleForThingDef(subject.ThingDef);
                    // Point it at the style-capable building; the setter may roll a category here.
                    subject.ThingDef = building;
                    var pair = ideo.style.StyleForThingDef(building);
                    var rows = PreceptSync.PublishPayloads();
                    var row = rows?.FirstOrDefault(r => r.Ideo == ideo.id && r.Def == subject.def.defName && r.Index == ordinal);
                    var expected = pair?.category is null ? null
                        : pair.category.defName + "/" + (pair.styleDef?.defName ?? "-");
                    var encodedFine = row is not null && row.Style == expected && expected is not null;
                    // Decoy: a different category for the same building, without publishing.
                    var decoy = DefDatabase<StyleCategoryDef>.AllDefsListForReading
                        .FirstOrDefault(c => c != pair?.category && c.GetStyleForThingDef(building) is not null);
                    if (decoy is not null)
                        ideo.style.SetStyleForThingDef(building,
                            new StyleCategoryPair { category = decoy, styleDef = decoy.GetStyleForThingDef(building) });
                    var before = ideo.style.StyleForThingDef(building)?.category?.defName;
                    var appliedBefore = PreceptSync.PayloadsApplied;
                    if (row is not null) PreceptSync.ApplyPayload(row);
                    var after = ideo.style.StyleForThingDef(building);
                    var readBack = after?.category is null ? "无"
                        : after.category.defName + "/" + (after.styleDef?.defName ?? "-");
                    var delta = PreceptSync.PayloadsApplied - appliedBefore;
                    var readBackFine = row is not null && readBack == row.Style;
                    // Restore.
                    if (fixture) ideo.RemovePrecept(subject);
                    else
                    {
                        subject.ThingDef = oldThing;
                        if (oldThing is not null && oldPair?.category is not null)
                            ideo.style.SetStyleForThingDef(oldThing, oldPair);
                        PreceptSync.PublishPayloads();
                    }
                    Answer(id, true, "ok",
                        $"建筑戒律 {ideo.id}#{subject.def.defName}[{ordinal}]{(fixture ? "（临时夹具）" : "")} → "
                        + $"{building.defName}：风格 {expected ?? "无"}，行 {(row?.Style ?? "无")} → "
                        + $"{(encodedFine ? "准" : "**不准**")}；先把本端改成 {before ?? "另一个类别"}（不发）再应用该行，"
                        + $"读回 {readBack} → {(readBackFine ? "准" : "**不准**")}（应用 +{delta}）；"
                        + $"{(fixture ? "夹具已移除" : "已复原")}");
                }
                return;
            case "precept-payload-probe":
                // Per-precept payload (round 148). Points a weapon precept at a different class pair, publishes, asserts
                // the encoded row, then writes a decoy locally without publishing and lets the row overwrite it.
                // A save with no weapon precept gets a fixture made the way the ideoligion editor makes one
                // (`StyleCatOverride.AddWeaponPreceptOption:496677-496688`: make, set the pair, add, init) and removes it
                // again at the end.
                {
                    var pairs = DefDatabase<WeaponClassPairDef>.AllDefsListForReading;
                    var ideo = Find.IdeoManager?.IdeosListForReading?.FirstOrDefault(i => i?.PreceptsListForReading is { Count: > 0 });
                    if (ideo is null || pairs is null || pairs.Count == 0)
                    {
                        Answer(id, true, "ok", "戒律载荷：本端没有 ideoligion 或没有武器类别对（这一半未测）");
                        return;
                    }
                    Precept_Weapon? subject = null;
                    var fixture = false;
                    foreach (var precept in ideo.PreceptsListForReading)
                        if (precept is Precept_Weapon weapon) { subject = weapon; break; }
                    var def = DefDatabase<PreceptDef>.AllDefsListForReading
                        .FirstOrDefault(p => p?.preceptClass == typeof(Precept_Weapon));
                    if (subject is null && def is not null)
                    {
                        subject = (Precept_Weapon)PreceptMaker.MakePrecept(def);
                        subject.noble = pairs[0].first;
                        subject.despised = pairs[0].second;
                        ideo.AddPrecept(subject);
                        subject.Init(ideo);
                        fixture = true;
                    }
                    if (subject is null)
                    {
                        Answer(id, true, "ok", "戒律载荷：本端没有武器 precept，也没有可造的 PreceptDef（这一半未测）");
                        return;
                    }
                    var ordinal = -1;
                    var seen = 0;
                    foreach (var precept in ideo.PreceptsListForReading)
                    {
                        if (precept?.def?.defName != subject.def.defName) continue;
                        if (ReferenceEquals(precept, subject)) { ordinal = seen; break; }
                        seen++;
                    }
                    var oldNoble = subject.noble;
                    var oldDespised = subject.despised;
                    var target = pairs.FirstOrDefault(p => p.first != oldNoble && p.second != oldDespised) ?? pairs[0];
                    subject.noble = target.first;
                    subject.despised = target.second;
                    var rows = PreceptSync.PublishPayloads();
                    var row = rows?.FirstOrDefault(r => r.Ideo == ideo.id && r.Def == subject.def.defName && r.Index == ordinal);
                    var encodedFine = row is not null && row.Noble == target.first.defName && row.Despised == target.second.defName;
                    // Decoy: the old pair back, without publishing.
                    subject.noble = oldNoble;
                    subject.despised = oldDespised;
                    var appliedBefore = PreceptSync.PayloadsApplied;
                    if (row is not null) PreceptSync.ApplyPayload(row);
                    var readNoble = subject.noble?.defName ?? "无";
                    var readDespised = subject.despised?.defName ?? "无";
                    var delta = PreceptSync.PayloadsApplied - appliedBefore;
                    var readBackFine = readNoble == target.first.defName && readDespised == target.second.defName;
                    // Restore, or drop the fixture entirely.
                    if (fixture) ideo.RemovePrecept(subject);
                    else
                    {
                        subject.noble = oldNoble;
                        subject.despised = oldDespised;
                        PreceptSync.PublishPayloads();
                    }
                    Answer(id, true, "ok",
                        $"武器戒律 {ideo.id}#{subject.def.defName}[{ordinal}]{(fixture ? "（临时夹具）" : "")}："
                        + $"原 {oldNoble?.defName ?? "无"}/{oldDespised?.defName ?? "无"} → 设为 "
                        + $"{target.first.defName}/{target.second.defName}，行 "
                        + $"{(row is null ? "无" : row.Noble + "/" + row.Despised)} → {(encodedFine ? "准" : "**不准**")}；"
                        + $"先写回旧值（不发）再应用该行，读回 {readNoble}/{readDespised} → "
                        + $"{(readBackFine ? "准" : "**不准**")}（应用 +{delta}）；{(fixture ? "夹具已移除" : "已复原")}");
                }
                return;
            case "precept-name-probe":
                // Precept names and the lock flag (round 147). Renames one precept and locks it, publishes, asserts the
                // encoded row, then writes the OLD name back as a decoy without publishing and lets the row overwrite it.
                {
                    Ideo? ideo = null;
                    Precept? subject = null;
                    foreach (var candidate in Find.IdeoManager?.IdeosListForReading ?? [])
                    {
                        foreach (var precept in candidate?.PreceptsListForReading ?? [])
                        {
                            if (precept?.def?.defName is not { Length: > 0 }) continue;
                            ideo = candidate;
                            subject = precept;
                            break;
                        }
                        if (subject is not null) break;
                    }
                    if (ideo is null || subject is null)
                    {
                        Answer(id, true, "ok", "戒律名字：本端没有 ideoligion 或没有 precept（这一半未测）");
                        return;
                    }
                    var oldName = subject.Label ?? string.Empty;
                    var oldLock = subject.nameLocked;
                    var newName = "探针戒律名" + ideo.id + subject.def.defName;
                    if (newName.Length > 32) newName = newName.Substring(0, 32);
                    subject.SetName(newName);
                    subject.nameLocked = true;
                    var rows = PreceptSync.PublishNames();
                    var row = rows?.FirstOrDefault(r => r.Ideo == ideo.id && r.Def == subject.def.defName);
                    var encodedFine = row is not null && row.Name == newName && row.Locked;
                    // Decoy: old name and lock back, without publishing.
                    subject.SetName(oldName);
                    subject.nameLocked = oldLock;
                    var appliedBefore = PreceptSync.NamesApplied;
                    if (row is not null) PreceptSync.ApplyName(row);
                    var readName = subject.Label ?? string.Empty;
                    var readLock = subject.nameLocked;
                    var appliedDelta = PreceptSync.NamesApplied - appliedBefore;
                    var readBackFine = readName == newName && readLock;
                    // Restore.
                    subject.SetName(oldName);
                    subject.nameLocked = oldLock;
                    PreceptSync.PublishNames();
                    Answer(id, true, "ok",
                        $"戒律 {ideo.id}#{subject.def.defName}：原「{oldName}」{(oldLock ? "锁" : "未锁")} → 设为「{newName}」锁，"
                        + $"行 {(row is null ? "无" : row.Name + "/" + (row.Locked ? "锁" : "未锁"))} → "
                        + $"{(encodedFine ? "准" : "**不准**")}；先写回旧名（不发）再应用该行，读回「{readName}」"
                        + $"{(readLock ? "锁" : "未锁")} → {(readBackFine ? "准" : "**不准**")}（应用 +{appliedDelta}）；已复原");
                }
                return;
            case "make-prisoner":
                // Fixture for the prisoner-panel channel (round 146): this save has no prisoners, and the interaction
                // mode, the conversion ideology and the enabled non-exclusive interactions only exist on one. Reversible
                // with `unmake-prisoner` (status back to Guest, host cleared).
                {
                    var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts[0].StartsWith('#')) parts[0] = parts[0][1..];
                    if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var prisonerId))
                    {
                        Answer(id, false, "argument", "用法：make-prisoner \"<小人 id>\"");
                        return;
                    }
                    if (!AuthoritativeReplica.FindPawnAny(prisonerId, out var prisoner) || prisoner?.guest is null)
                    {
                        Answer(id, false, "missing-pawn", $"没有这个小人的 guest 追踪器：#{prisonerId}");
                        return;
                    }
                    var was = $"{prisoner.guest.GuestStatus}/{prisoner.guest.ExclusiveInteractionMode?.defName ?? "-"}";
                    prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                    PawnTrackerSync.Publish();
                    GuestSync.Publish();
                    Answer(id, true, "ok",
                        $"小人 #{prisonerId}：{was} → {prisoner.guest.GuestStatus}/{prisoner.guest.ExclusiveInteractionMode?.defName ?? "-"}"
                        + $"（IsPrisoner {(prisoner.guest.IsPrisoner ? "是" : "否")}，主家 {prisoner.guest.HostFaction?.Name ?? "无"}）；已发该行");
                }
                return;
            case "prisoner-mode":
                // Fixture for the census reading (round 146): sets the prisoner-panel fields WITHOUT restoring, so the
                // census row can be read while they are non-default. `guest-probe` proves the channel; this proves the
                // census counts are live rather than constants. Revert with `prisoner-mode "<id>" MaintainOnly`.
                {
                    var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts[0].StartsWith('#')) parts[0] = parts[0][1..];
                    if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var modePawnId))
                    {
                        Answer(id, false, "argument", "用法：prisoner-mode \"<小人 id> <modeDefName> [ideoId|primary|none]\"");
                        return;
                    }
                    if (!AuthoritativeReplica.FindPawnAny(modePawnId, out var modePawn) || modePawn?.guest is null)
                    {
                        Answer(id, false, "missing-pawn", $"没有这个小人的 guest 追踪器：#{modePawnId}");
                        return;
                    }
                    var modeDef = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(parts[1]);
                    if (modeDef is null || modeDef.isNonExclusiveInteraction)
                    {
                        Answer(id, false, "argument",
                            $"没有这个排他互动模式：{parts[1]}（非排他模式要走开关，不是这一条）");
                        return;
                    }
                    modePawn.guest.SetExclusiveInteraction(modeDef);
                    var ideoText = "未改";
                    if (parts.Length >= 3)
                    {
                        Ideo? target = parts[2] is "none"
                            ? null
                            : parts[2] is "primary"
                            ? Faction.OfPlayer?.ideos?.PrimaryIdeo
                            : int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ideoId)
                                ? IdeoSync.IdeoById(ideoId)
                                : null;
                        if (parts[2] is "none") { modePawn.guest.ideoForConversion = null; ideoText = "已清空"; }
                        else if (target is null) ideoText = $"皈依目标未解析（{parts[2]}）";
                        else { modePawn.guest.ideoForConversion = target; ideoText = target.name; }
                    }
                    GuestSync.Publish();
                    Answer(id, true, "ok",
                        $"囚犯 #{modePawnId}：模式 {modePawn.guest.ExclusiveInteractionMode?.defName ?? "-"}，"
                        + $"皈依目标 {modePawn.guest.ideoForConversion?.name ?? "无"}（{ideoText}），已发该行");
                }
                return;
            case "unmake-prisoner":
                {
                    var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts[0].StartsWith('#')) parts[0] = parts[0][1..];
                    if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var releasedId))
                    {
                        Answer(id, false, "argument", "用法：unmake-prisoner \"<小人 id>\"");
                        return;
                    }
                    if (!AuthoritativeReplica.FindPawnAny(releasedId, out var released) || released?.guest is null)
                    {
                        Answer(id, false, "missing-pawn", $"没有这个小人的 guest 追踪器：#{releasedId}");
                        return;
                    }
                    var was = released.guest.GuestStatus;
                    released.guest.SetGuestStatus(null, GuestStatus.Guest);
                    PawnTrackerSync.Publish();
                    GuestSync.Publish();
                    Answer(id, true, "ok", $"小人 #{releasedId}：{was} → {released.guest.GuestStatus}（夹具已撤）");
                }
                return;
            case "guest-probe":
                // The prisoner panel's three settings (round 146). Changes them here, publishes, reads the encoded row
                // back, then writes the OLD values as a decoy without publishing and lets the row write over it.
                {
                    var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts[0].StartsWith('#')) parts[0] = parts[0][1..];
                    Pawn? subject = null;
                    if (parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var wantedId))
                        AuthoritativeReplica.FindPawnAny(wantedId, out subject);
                    subject ??= Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p?.guest is { IsPrisoner: true });
                    if (subject?.guest is null)
                    {
                        Answer(id, true, "ok", "囚犯面板：本存档没有囚犯（先用 make-prisoner 造一个；这一半未测）");
                        return;
                    }
                    if (!subject.guest.IsPrisoner)
                    {
                        Answer(id, false, "not-prisoner",
                            $"小人 #{subject.thingIDNumber} 不是囚犯（状态 {subject.guest.GuestStatus}）：先用 make-prisoner");
                        return;
                    }
                    var guest = subject.guest;
                    var oldMode = guest.ExclusiveInteractionMode;
                    var oldIdeo = guest.ideoForConversion;
                    var oldNonExclusive = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var def in DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading)
                        if (def is { isNonExclusiveInteraction: true } && guest.IsInteractionEnabled(def))
                            oldNonExclusive.Add(def.defName);
                    // Pick a new exclusive mode (anything but the current one), a conversion ideo, and enable every
                    // non-exclusive def this build has - so the encoding of all three halves is exercised at once.
                    PrisonerInteractionModeDef? newMode = null;
                    foreach (var def in DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading)
                        if (def is { isNonExclusiveInteraction: false } && def != oldMode) { newMode = def; break; }
                    var newIdeo = oldIdeo is null
                        ? Faction.OfPlayer?.ideos?.PrimaryIdeo ?? Find.IdeoManager?.IdeosListForReading?.FirstOrDefault()
                        : Find.IdeoManager?.IdeosListForReading?.FirstOrDefault(i => i != oldIdeo);
                    var nonExclusiveDefs = DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading
                        .Where(d => d is { isNonExclusiveInteraction: true }).ToList();
                    if (newMode is null)
                    {
                        Answer(id, true, "ok", $"囚犯 #{subject.thingIDNumber}：本 build 没有第二个排他互动模式（这一半未测）");
                        return;
                    }
                    guest.SetExclusiveInteraction(newMode);
                    if (newIdeo is not null) guest.ideoForConversion = newIdeo;
                    foreach (var def in nonExclusiveDefs) guest.ToggleNonExclusiveInteraction(def, true);
                    var rows = GuestSync.Publish();
                    var row = rows?.FirstOrDefault(r => r.Pawn == subject.thingIDNumber);
                    var fields = row?.State.Split('#') ?? [];
                    var wantedNonExclusive = string.Join(",", nonExclusiveDefs.Select(d => d.defName).OrderBy(
                        n => n, StringComparer.Ordinal));
                    var encodedFine = fields.Length > 7
                        && fields[5] == newMode.defName
                        && fields[6] == (newIdeo?.id ?? -1).ToString(CultureInfo.InvariantCulture)
                        && fields[7] == wantedNonExclusive;
                    // Decoy: old values back, without publishing.
                    guest.SetExclusiveInteraction(oldMode ?? PrisonerInteractionModeDefOf.MaintainOnly);
                    guest.ideoForConversion = oldIdeo;
                    foreach (var def in nonExclusiveDefs)
                        guest.ToggleNonExclusiveInteraction(def, oldNonExclusive.Contains(def.defName));
                    var modesBefore = GuestSync.ModeRows;
                    var ideoBefore = GuestSync.IdeoConversionRows;
                    var toggleBefore = GuestSync.NonExclusiveRows;
                    if (row is not null) GuestSync.Apply(row);
                    var readMode = guest.ExclusiveInteractionMode?.defName ?? "-";
                    var readIdeo = guest.ideoForConversion?.id ?? -1;
                    var readNonExclusive = new List<string>();
                    foreach (var def in nonExclusiveDefs)
                        if (guest.IsInteractionEnabled(def)) readNonExclusive.Add(def.defName);
                    readNonExclusive.Sort(StringComparer.Ordinal);
                    var readBackFine = readMode == newMode.defName
                        && readIdeo == (newIdeo?.id ?? -1)
                        && string.Join(",", readNonExclusive) == wantedNonExclusive;
                    // Restore.
                    guest.SetExclusiveInteraction(oldMode ?? PrisonerInteractionModeDefOf.MaintainOnly);
                    guest.ideoForConversion = oldIdeo;
                    foreach (var def in nonExclusiveDefs)
                        guest.ToggleNonExclusiveInteraction(def, oldNonExclusive.Contains(def.defName));
                    GuestSync.Publish();
                    Answer(id, true, "ok",
                        $"囚犯 #{subject.thingIDNumber}：原 {oldMode?.defName ?? "-"}/皈依 {oldIdeo?.name ?? "无"}/"
                        + $"非排他 {oldNonExclusive.Count} 个 → 设为 {newMode.defName}/{(newIdeo?.name ?? "无")}/"
                        + $"{nonExclusiveDefs.Count} 个，行 {row?.State ?? "无"} → {(encodedFine ? "准" : "**不准**")}；"
                        + $"先写回旧值（不发）再应用该行，读回 {readMode}/{readIdeo}/{readNonExclusive.Count} 个 → "
                        + $"{(readBackFine ? "准" : "**不准**")}"
                        + $"（模式 +{GuestSync.ModeRows - modesBefore}、皈依 +{GuestSync.IdeoConversionRows - ideoBefore}、"
                        + $"开关 +{GuestSync.NonExclusiveRows - toggleBefore}）；已复原");
                }
                return;
            case "make-guilty":
                // Fixture for the execute-flag reading (round 144). `guiltyTicksLeft` is what `IsGuilty` branches on
                // (`:479231`), and the red "mark for execution" button only exists for a guilty colonist (`:642584`), so
                // the `o:` segment has to be measured on a pawn that is actually guilty - otherwise the probe proves the
                // flag travels but not the state the flag is used in. Reversible: call it again with 0.
                {
                    var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts[0].StartsWith('#')) parts[0] = parts[0][1..];
                    if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var guiltPawnId))
                    {
                        Answer(id, false, "argument", "用法：make-guilty \"<小人 id> <ticks>\"（0 清除）");
                        return;
                    }
                    var ticks = 0;
                    if (parts.Length >= 2)
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
                    if (!AuthoritativeReplica.FindPawnAny(guiltPawnId, out var guiltyPawn) || guiltyPawn?.guilt is null)
                    {
                        Answer(id, false, "missing-pawn", $"没有这个小人的 guilt 追踪器：#{guiltPawnId}");
                        return;
                    }
                    var wasGuilty = guiltyPawn.guilt.IsGuilty;
                    if (!PawnTrackerSync.SetGuiltyTicksLeft(guiltyPawn, ticks))
                    {
                        Answer(id, false, "missing-field", $"小人 #{guiltPawnId} 的 guilt 追踪器写不进 guiltyTicksLeft");
                        return;
                    }
                    PawnTrackerSync.Publish();
                    Answer(id, true, "ok",
                        $"小人 #{guiltPawnId}：guiltyTicksLeft 设为 {ticks}，IsGuilty {wasGuilty} → "
                        + $"{guiltyPawn.guilt.IsGuilty}（IsFreeColonist {(guiltyPawn.IsFreeColonist ? "是" : "否")}）；"
                        + $"已发出该行，待处决 {guiltyPawn.guilt.awaitingExecution}");
                }
                return;
            case "remove-gene":
                // The other half of `add-gene`: a fixture that cannot be undone is not a fixture. `RemoveGene`
                // (`:471429`) drops the instance from `xenogenes`/`endogenes`, invalidates the cached list and takes the
                // granted abilities back, so remove-then-add returns the pawn to the state it was in.
                {
                    var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dropPawnId))
                    {
                        Answer(id, false, "argument", "用法：remove-gene \"<小人 id> <geneDefName>\"");
                        return;
                    }
                    var dropName = parts[1];
                    if (!AuthoritativeReplica.FindPawnAny(dropPawnId, out var dropPawn) || dropPawn?.genes is not { } dropTracker)
                    {
                        Answer(id, false, "missing-pawn", $"本端找不到小人 #{dropPawnId}（或它没有基因追踪器）");
                        return;
                    }
                    var match = dropTracker.Xenogenes.Concat(dropTracker.Endogenes)
                        .FirstOrDefault(g => g?.def?.defName == dropName);
                    if (match is null)
                    {
                        Answer(id, false, "missing-gene", $"小人 #{dropPawnId} 身上没有基因 {dropName}"
                            + $"（异种 {dropTracker.Xenogenes.Count} / 内源 {dropTracker.Endogenes.Count}）");
                        return;
                    }
                    var xenoBeforeDrop = dropTracker.Xenogenes.Count;
                    var endoBeforeDrop = dropTracker.Endogenes.Count;
                    dropTracker.RemoveGene(match);
                    Answer(id, true, "ok",
                        $"小人 #{dropPawnId} 去掉基因 {dropName}：异种 {xenoBeforeDrop} → {dropTracker.Xenogenes.Count}"
                        + $"，内源 {endoBeforeDrop} → {dropTracker.Endogenes.Count}"
                        + $"，血原基因 {(dropTracker.GetFirstGeneOfType<Gene_Hemogen>() is null ? "已无" : "仍有")}");
                }
                return;
            case "add-mechanitor":
                // **Fixture, not a gate (round 143).** This save has no mechanitor, so the control-group channel can only
                // report "mechanism complete, unmeasured" - the reading round 142 refused to leave standing. A mechanitor
                // in vanilla is a player-faction pawn carrying `HediffDefOf.MechlinkImplant`
                // (`MechanitorUtility.ShouldBeMechanitor`, `:2416-2423`), whose tracker the game creates in
                // `Pawn.PostLoad`-time component init (`:445418`). Both steps are public, so the fixture is the same two
                // steps the game takes; `remove-mechanitor` undoes them.
                {
                    var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mechPawnId))
                    {
                        Answer(id, false, "argument", "用法：add-mechanitor \"<小人 id> [组数]\"");
                        return;
                    }
                    var wanted = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
                    if (!AuthoritativeReplica.FindPawnAny(mechPawnId, out var mechPawn) || mechPawn is null)
                    {
                        Answer(id, false, "missing-pawn", $"本端找不到小人 #{mechPawnId}");
                        return;
                    }
                    mechPawn.health.AddHediff(HediffDefOf.MechlinkImplant);
                    mechPawn.mechanitor ??= new Pawn_MechanitorTracker(mechPawn);
                    var added = 0;
                    while (mechPawn.mechanitor.controlGroups.Count < wanted)
                    {
                        mechPawn.mechanitor.controlGroups.Add(new MechanitorControlGroup(mechPawn.mechanitor));
                        added++;
                    }
                    var firstGroup = mechPawn.mechanitor.controlGroups.Count > 0 ? mechPawn.mechanitor.controlGroups[0] : null;
                    Answer(id, true, "ok",
                        $"小人 #{mechPawnId} 加机械师：MechanitorUtility 判定 "
                        + $"{(MechanitorUtility.ShouldBeMechanitor(mechPawn) ? "是" : "否")}，"
                        + $"控制组 {mechPawn.mechanitor.controlGroups.Count - added} → {mechPawn.mechanitor.controlGroups.Count}"
                        + $"（可用上限 {mechPawn.mechanitor.TotalAvailableControlGroups}），"
                        + $"第 1 组 work mode {firstGroup?.WorkMode?.defName ?? "无"}、"
                        + $"充能区间 {firstGroup?.mechRechargeThresholds.min:0.##}–{firstGroup?.mechRechargeThresholds.max:0.##}");
                }
                return;
            case "remove-mechanitor":
                {
                    var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dropMechId))
                    {
                        Answer(id, false, "argument", "用法：remove-mechanitor \"<小人 id>\"");
                        return;
                    }
                    if (!AuthoritativeReplica.FindPawnAny(dropMechId, out var dropMech) || dropMech?.health is not { } health)
                    {
                        Answer(id, false, "missing-pawn", $"本端找不到小人 #{dropMechId}");
                        return;
                    }
                    var groupsBefore = dropMech.mechanitor?.controlGroups.Count ?? 0;
                    var implant = health.hediffSet.GetFirstHediffOfDef(HediffDefOf.MechlinkImplant);
                    if (implant is not null) health.RemoveHediff(implant);
                    dropMech.mechanitor = null;
                    Answer(id, true, "ok",
                        $"小人 #{dropMechId} 去掉机械师：控制组 {groupsBefore} → 0，机械肢 "
                        + $"{(health.hediffSet.HasHediff(HediffDefOf.MechlinkImplant) ? "仍在" : "已移除")}，"
                        + $"判定 {(MechanitorUtility.ShouldBeMechanitor(dropMech) ? "仍是" : "不再是")}机械师");
                }
                return;
            case "fishing-probe":
                // The fishing zone's five player settings (round 145). Same oracle as the other probes: change them here,
                // publish, read the encoded blob back, then write a DECOY on this peer without publishing and let the row
                // write over it. The zone may not exist in this save; then it is created on real water and removed again,
                // so the reading is available without leaving a fixture behind.
                {
                    Zone_Fishing? zone = null;
                    var created = false;
                    foreach (var map in Find.Maps)
                        foreach (var candidate in map.zoneManager.AllZones)
                            if (candidate is Zone_Fishing found) { zone = found; break; }
                    if (zone is null)
                    {
                        foreach (var map in Find.Maps)
                        {
                            for (var x = 0; x < map.Size.x && zone is null; x++)
                                for (var z = 0; z < map.Size.z && zone is null; z++)
                                {
                                    var cell = new IntVec3(x, 0, z);
                                    if (!cell.InBounds(map)) continue;
                                    var terrain = cell.GetTerrain(map);
                                    if (terrain?.IsWater != true) continue;
                                    zone = new Zone_Fishing(map.zoneManager);
                                    map.zoneManager.RegisterZone(zone);
                                    zone.AddCell(cell);
                                    created = true;
                                }
                            if (zone is not null) break;
                        }
                    }
                    if (zone is null)
                    {
                        Answer(id, true, "ok", "钓鱼区：本存档没有钓鱼区，也没有任何水域格（这一半未测）");
                        return;
                    }
                    var zoneMap = zone.Map;
                    var before = $"{zone.repeatMode}/{zone.repeatCount}/{zone.targetCount}/{zone.unpauseAtCount}/"
                        + $"{(zone.pauseWhenSatisfied ? "暂停" : "不停")}/{zone.targetPopulationPct:0.##}";
                    var oldMode = zone.repeatMode;
                    var oldTarget = zone.targetCount;
                    var oldUnpause = zone.unpauseAtCount;
                    var oldPause = zone.pauseWhenSatisfied;
                    var oldPop = zone.targetPopulationPct;
                    var newMode = oldMode == FishRepeatMode.RepeatCount ? FishRepeatMode.TargetCount
                        : oldMode == FishRepeatMode.TargetCount ? FishRepeatMode.DoForever : FishRepeatMode.RepeatCount;
                    var newTarget = oldTarget + 7;
                    var newUnpause = oldUnpause + 3;
                    var newPause = !oldPause;
                    var newPop = Math.Min(0.9f, oldPop + 0.05f);
                    zone.repeatMode = newMode;
                    zone.targetCount = newTarget;
                    zone.unpauseAtCount = newUnpause;
                    zone.pauseWhenSatisfied = newPause;
                    zone.targetPopulationPct = newPop;
                    zone.RecheckPausedDueToResourceCount();
                    var (zones, _) = ZoneSync.Publish();
                    var row = zones?.FirstOrDefault(r => r.Id == zone.ID);
                    var segment = row?.Fishing;
                    var wanted = string.Join(";",
                        ((int)newMode).ToString(CultureInfo.InvariantCulture),
                        newTarget.ToString(CultureInfo.InvariantCulture),
                        newUnpause.ToString(CultureInfo.InvariantCulture),
                        newPause ? "1" : "0",
                        newPop.ToString("0.####", CultureInfo.InvariantCulture));
                    var encodedFine = segment == wanted;
                    // Decoy: put the OLD values back without publishing, then apply the row.
                    zone.repeatMode = oldMode;
                    zone.targetCount = oldTarget;
                    zone.unpauseAtCount = oldUnpause;
                    zone.pauseWhenSatisfied = oldPause;
                    zone.targetPopulationPct = oldPop;
                    zone.RecheckPausedDueToResourceCount();
                    var appliedBefore = ZoneSync.FishingSettingsApplied;
                    if (row is not null) ZoneSync.Apply(row);
                    var readBack = $"{zone.repeatMode}/{zone.targetCount}/{zone.unpauseAtCount}/"
                        + $"{(zone.pauseWhenSatisfied ? "暂停" : "不停")}/{zone.targetPopulationPct:0.##}";
                    var expected = $"{newMode}/{newTarget}/{newUnpause}/{(newPause ? "暂停" : "不停")}/{newPop:0.##}";
                    var appliedDelta = ZoneSync.FishingSettingsApplied - appliedBefore;
                    // Restore, or remove the fixture zone.
                    var restored = "已复原";
                    if (created)
                    {
                        zoneMap.zoneManager.DeregisterZone(zone);
                        restored = "夹具区已移除";
                    }
                    else
                    {
                        zone.repeatMode = oldMode;
                        zone.targetCount = oldTarget;
                        zone.unpauseAtCount = oldUnpause;
                        zone.pauseWhenSatisfied = oldPause;
                        zone.targetPopulationPct = oldPop;
                        zone.RecheckPausedDueToResourceCount();
                        ZoneSync.Publish();
                    }
                    Answer(id, true, "ok",
                        $"钓鱼区 #{zone.ID}（{(created ? "本轮新建的夹具区" : "存档原有")}，{zoneMap?.Parent?.LabelShort ?? "地图"}）："
                        + $"原 {before} → 设为 {expected}，段 {(segment ?? "无")} → {(encodedFine ? "准" : "**不准**")}；"
                        + $"先写回旧值（不发）再应用该行，读回 {readBack} → "
                        + $"{(readBack == expected ? "准" : "**不准**")}（应用 +{appliedDelta}）；{restored}");
                }
                return;
            case "execute-probe":
                // The "mark for execution" flag (round 144). Same oracle as `medcare-probe`: publish a real row, read the
                // `o:` segment back, then write a DECOY on this peer without publishing and let the row write over it.
                // Guilt is a bool on the tracker, so this also proves the bool accessors: an int read/write pair would
                // encode a constant 0 and throw inside `Apply`, which the catch-all would swallow.
                {
                    var subject = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p?.guilt is not null && p.IsFreeColonist);
                    if (subject?.guilt is null)
                    {
                        Answer(id, true, "ok", "待处决：本存档没有自由殖民者（这一半未测）");
                        return;
                    }
                    var oldFlag = subject.guilt.awaitingExecution;
                    var recorded = !oldFlag;
                    PawnTrackerSync.Publish();
                    subject.guilt.awaitingExecution = recorded;
                    var published = PawnTrackerSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Pawn == subject.thingIDNumber);
                    var segment = row?.Trackers.Split(';').FirstOrDefault(f => f.StartsWith("o:", StringComparison.Ordinal));
                    var decoy = !recorded;
                    subject.guilt.awaitingExecution = decoy;
                    var beforeRows = PawnTrackerSync.GuiltExecutionRows;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readBack = subject.guilt.awaitingExecution;
                    var applied = PawnTrackerSync.GuiltExecutionRows - beforeRows;
                    subject.guilt.awaitingExecution = oldFlag;
                    PawnTrackerSync.Publish();
                    Answer(id, true, "ok",
                        $"小人 #{subject.thingIDNumber}（IsGuilty {(subject.guilt.IsGuilty ? "是" : "否")}）："
                        + $"原 awaitingExecution {oldFlag} → 设为 {recorded}，发出 {published.Count} 行、段 {segment ?? "无"} → "
                        + $"{(segment == (recorded ? "o:1" : "o:0") ? "准" : "**不准**")}；"
                        + $"先改成 {decoy}（不发），应用该行后读回 {readBack} → "
                        + $"{(readBack == recorded && decoy != recorded ? "准" : "**不准**")}"
                        + $"（应用计数 +{applied}）；已复原 {subject.guilt.awaitingExecution}");
                }
                return;
            case "mech-probe":
                // Round 143 measured the mechanitor group settings. Same oracle as `medcare-probe`: publish a real row,
                // read the `k:` segment back, then write a DECOY on this peer without publishing and let the row write
                // over it. Both halves are checked in one pass because they are one row and one object: the work mode
                // (which branches the mech's job giver tree) and the recharge thresholds (which the energy job giver
                // reads). A colony with no mechanitor gets the "unmeasured" answer rather than a zero.
                {
                    var mechanitor = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p?.mechanitor?.controlGroups is { Count: > 0 });
                    if (mechanitor is null)
                    {
                        Answer(id, true, "ok", "机械组：本存档没有带控制组的机械师（这一半未测）");
                        return;
                    }
                    var group = mechanitor.mechanitor.controlGroups[0];
                    if (group is null)
                    {
                        Answer(id, true, "ok", $"机械组：小人 #{mechanitor.thingIDNumber} 的第 1 组是空的（未测）");
                        return;
                    }
                    var oldMode = group.WorkMode?.defName;
                    var oldMin = group.mechRechargeThresholds.min;
                    var oldMax = group.mechRechargeThresholds.max;
                    var otherMode = DefDatabase<MechWorkModeDef>.AllDefsListForReading
                        .FirstOrDefault(d => d?.defName is { Length: > 0 } n && n != oldMode);
                    var recordedMin = Math.Abs(oldMin - 0.2f) < 0.001f ? 0.35f : 0.2f;
                    var recordedMax = Math.Abs(oldMax - 0.9f) < 0.001f ? 0.75f : 0.9f;

                    PawnTrackerSync.Publish();
                    if (otherMode is not null) group.SetWorkMode(otherMode);
                    group.mechRechargeThresholds = new FloatRange(recordedMin, recordedMax);
                    var published = PawnTrackerSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Pawn == mechanitor.thingIDNumber);
                    var segment = row?.Trackers.Split(';').FirstOrDefault(f => f.StartsWith("k:0:", StringComparison.Ordinal));
                    var expectedMode = otherMode?.defName ?? oldMode ?? "-";
                    var carries = segment is not null
                                  && segment.StartsWith("k:0:" + expectedMode + ":", StringComparison.Ordinal);

                    // decoy: put both back to the originals on this peer, then let the row overwrite them
                    if (oldMode is { Length: > 0 } && DefDatabase<MechWorkModeDef>.GetNamedSilentFail(oldMode) is { } oldModeDef)
                        group.SetWorkMode(oldModeDef);
                    group.mechRechargeThresholds = new FloatRange(oldMin, oldMax);
                    var beforeRows = PawnTrackerSync.MechSettingsRows;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readMode = group.WorkMode?.defName;
                    var readMin = group.mechRechargeThresholds.min;
                    var readMax = group.mechRechargeThresholds.max;
                    var applied = PawnTrackerSync.MechSettingsRows - beforeRows;

                    // restore
                    if (oldMode is { Length: > 0 } && DefDatabase<MechWorkModeDef>.GetNamedSilentFail(oldMode) is { } restoreMode)
                        group.SetWorkMode(restoreMode);
                    group.mechRechargeThresholds = new FloatRange(oldMin, oldMax);
                    PawnTrackerSync.Publish();

                    Answer(id, true, "ok",
                        $"机械师 #{mechanitor.thingIDNumber} 第 1 组（{mechanitor.mechanitor.controlGroups.Count} 组）："
                        + $"原 work mode {oldMode ?? "无"}、充能区间 {oldMin:0.##}–{oldMax:0.##}"
                        + $" → 设为 {expectedMode}、{recordedMin:0.##}–{recordedMax:0.##}，"
                        + $"发出 {published.Count} 行、段 {segment ?? "无"} → {(carries ? "准" : "**不准**")}；"
                        + $"先改回 {oldMode ?? "无"} / {oldMin:0.##}–{oldMax:0.##}（不发），应用该行后读回 "
                        + $"{readMode ?? "无"} / {readMin:0.##}–{readMax:0.##} → "
                        + $"{(readMode == expectedMode && Math.Abs(readMin - recordedMin) < 0.001f && Math.Abs(readMax - recordedMax) < 0.001f ? "准" : "**不准**")}"
                        + $"（组写入 +{applied}，每行每组合一段）；已复原 {group.WorkMode?.defName ?? "无"} / "
                        + $"{group.mechRechargeThresholds.min:0.##}–{group.mechRechargeThresholds.max:0.##}");
                }
                return;
            case "gene-probe":
                // Same idempotent round-trip as the trait probe, and for the same reason: `AddGene`'s private overload
                // also grants abilities and forced traits, so a count cannot tell a correct apply from a re-grant.
                // Runs on a peer where `ModsConfig.BiotechActive` is false by reporting that rather than a zero.
                Answer(id, true, "ok", GeneSync.Probe(argument));
                return;
            case "trait-probe":
                // Idempotent round-trip: feed a pawn its own encoded trait list back and re-encode. A count cannot tell a
                // correct apply from a wrong one, and `TraitSet` is rebuilt through `GainTrait`/`RemoveTrait`, whose
                // side effects are exactly what could reorder or re-grant something.
                Answer(id, true, "ok", PawnTraitSync.Probe(argument));
                return;
            case "designator-coverage":
                // Commits one cell through every loaded designator and reports which ones published a command. The
                // question "is this type covered" has no answer from reading the code: `AddDesignation` is patched, so a
                // designator that calls it is covered without appearing in `DesignatorSync` at all.
                Answer(id, true, "ok", DesignatorCoverageProbe.Probe(argument));
                return;
            case "area-paint-probe":
                // Commits one real stroke through the real encoder. A paint path that is installed and never fired is
                // indistinguishable from a working one in every log line, and the defect being checked for here (an
                // empty chunk list for a `Designator_Cells` subclass) produces no output at all.
                Answer(id, true, "ok", AreaPaintProbe.Probe(argument));
                return;
            case "settlement-forget":
                // Drops a settlement's membership records without touching the pawns, which is the only way to reach the
                // "a colonist joined after founding" state - no command can spawn a colonist, so the counter that reports
                // it would otherwise ship unverified and always zero.
                Answer(id, true, "ok", ScriptedSettlement.Forget(argument));
                return;
            case "gizmo-hooks":
                // **Where the gizmo capture stands (round 140).** The same counts `ModPatchAudit` logs at startup, which
                // is how a name that never resolved went unnoticed: a log line is not an instrument a later round reads.
                Answer(id, true, "ok", ModPatchAudit.GizmoReport());
                return;
            case "bossgroup-probe":
                // **Round-trips the bossgroup summon cooldown (round 140).** `Command_CallBossgroup.IsDisabled` reads
                // `Find.BossgroupManager.lastBossgroupCalled` from `GizmoOnGUI`, so every peer draws its own answer while
                // only the summoning side writes the stamp. The click is captured and re-run on the authority, and the
                // summon action has no cooldown of its own - so the gizmo is the only cooldown there is.
                Answer(id, true, "ok", BossgroupSync.Probe());
                return;
            case "autoslaughter-probe":
                // **Round-trips an auto-slaughter limit (`AutoSlaughterManager.configs`, round 129).**
                //
                // The reading has to be the *number read back*, not "the apply ran": this channel writes seven fields on
                // an object that already existed in the save, so an apply that found the config and then wrote nothing is
                // exactly what a working apply looks like from a counter. The decoy therefore sets a different value
                // locally, without publishing, so the row cannot be satisfied by what is already in the field.
                {
                    Map? map = null;
                    AutoSlaughterManager? manager = null;
                    foreach (var candidate in Find.Maps)
                    {
                        if (candidate?.autoSlaughterManager?.configs is not { Count: > 0 }) continue;
                        map = candidate;
                        manager = candidate.autoSlaughterManager;
                        break;
                    }
                    if (manager is null)
                    {
                        Answer(id, true, "ok", "本端没有带配置的 autoSlaughterManager，这一半测不到");
                        return;
                    }
                    var config = manager.configs.FirstOrDefault(c => c?.animal is not null);
                    if (config is null) { Answer(id, true, "ok", "配置里没有带 animal 的行，这一半测不到"); return; }

                    var beforeApplied = AutoSlaughterSync.Applied;
                    var oldTotal = config.maxTotal;
                    var wanted = oldTotal == -1 ? 7 : oldTotal + 3;

                    AutoSlaughterSync.Publish();          // drain whatever was pending
                    config.maxTotal = wanted;
                    // Vanilla's own side effect after an edit (`:625940-625943`) - a real click runs it too.
                    manager.Notify_ConfigChanged();
                    var published = AutoSlaughterSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Map == map!.uniqueID && r.Animal == config.animal.defName);
                    var rowsTotal = published.Count;

                    // Decoy: put the old value back directly, WITHOUT publishing.
                    config.maxTotal = oldTotal;
                    var decoyValue = config.maxTotal;
                    var beforeDecoyApplied = AutoSlaughterSync.Applied;
                    if (row is not null) AutoSlaughterSync.Apply(row);
                    var readTotal = config.maxTotal;
                    var applied = AutoSlaughterSync.Applied - beforeDecoyApplied;

                    // Restore and publish so the frame is not left holding the probe's edit.
                    config.maxTotal = oldTotal;
                    manager.Notify_ConfigChanged();
                    AutoSlaughterSync.Publish();

                    var encodedOk = row is not null && row.MaxTotal == wanted;
                    var roundTripOk = row is not null && readTotal == wanted && decoyValue == oldTotal;
                    Answer(id, true, "ok",
                        $"地图 {map!.uniqueID} · {config.animal.defName}：maxTotal 原 {oldTotal} → 设为 {wanted}，"
                        + $"发出 {rowsTotal} 行" + (row is null ? "（其中没有这一行）" : $"（这一行编码 {row.MaxTotal}）")
                        + $" → 编码{(encodedOk ? "准" : "不准")}；"
                        + $"先直接改回 {decoyValue}（不发）再应用该行 → 读回 {readTotal} → {(roundTripOk ? "准" : "不准")}；"
                        + $"应用 +{applied}（累计 {AutoSlaughterSync.Applied - beforeApplied}）；已复原 {config.maxTotal}");
                    return;
                }
            case "goto-probe":
                // **Round-trips the "stop where you are" cancellation (`CommandCodec.Stop`, round 127).**
                //
                // The capture half cannot be measured in this process by construction: `PlayerActing` is false on a
                // simulation peer, so `GotoStopCapturePatch` returns true and vanilla ends the job locally - a reading
                // that would "pass" while proving nothing about the channel. So this probe measures the **apply** half
                // end to end through the real dispatcher (`SharedActions.Apply`), and reports the capture gate
                // separately instead of hiding it.
                //
                // **Two readings had to be thrown away before this one was honest, and both were the same mistake.**
                // (1) A `TryTakeOrderedJob(Goto)` returns `True` while `CurJobDef` stays `Wait_Wander` - it queues, and
                // the queue drains on a tick - so the probe blamed the channel for its own setup. (2) Even without any
                // injection, an idle pawn re-takes `Wait_Wander` inside `EndCurrentJob(startNewJob: true)`, so "same
                // defName before and after" is what a *working* stop looks like on an idle pawn. Both readings were
                // `不准` against code that had done nothing wrong.
                //
                // So: a non-idle job is required for the comparison to mean anything, and the job identity is reported
                // alongside the name because a name cannot distinguish "nothing happened" from "the same job again".
                {
                    var candidates = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .Where(p => p?.jobs?.curJob is not null && !p.Dead && p.Spawned).ToList();
                    if (candidates.Count == 0) { Answer(id, true, "ok", "本端没有带 job 的小人，这一半测不到"); return; }
                    var busy = candidates.Where(p => p.CurJob?.def.isIdle == false).ToList();
                    var subject = busy.FirstOrDefault(p => p.IsColonistPlayerControlled) ?? busy.FirstOrDefault();
                    if (subject is null)
                    {
                        Answer(id, true, "ok",
                            $"本端 {candidates.Count} 个带 job 的小人全是空闲 job（如 {candidates[0].CurJobDef?.defName}），"
                            + "空闲小人会在 EndCurrentJob 里立刻重新拿到同样的空闲 job，名字前后一样 —— "
                            + "**这一半未测**，不是不准；捕获半本进程测不到：" + SharedActions.PlayerActingNote());
                        return;
                    }

                    var beforeApplied = SharedActions.stopsApplied;
                    var beforeMissed = SharedActions.stopsMissed;
                    var jobBefore = subject.CurJobDef?.defName ?? "(无)";
                    var loadBefore = subject.CurJob?.loadID ?? -1;
                    var driverBefore = subject.jobs.curDriver;

                    var stopCommand = TurboTick.Simulation.CommandCodec.StopCommand(
                        subject.thingIDNumber, (int)JobCondition.InterruptForced, 0);
                    SharedActions.Apply(stopCommand);

                    var jobAfter = subject.CurJobDef?.defName ?? "(无)";
                    var loadAfter = subject.CurJob?.loadID ?? -1;
                    var driverChanged = !ReferenceEquals(driverBefore, subject.jobs.curDriver);
                    var applied = SharedActions.stopsApplied - beforeApplied;
                    var missed = SharedActions.stopsMissed - beforeMissed;

                    // "Ended" means either nothing is running any more, or the driver behind it is a different one -
                    // a fresh job of the same def is still a different job, and the defName alone cannot say that.
                    var ended = jobAfter == "(无)" || driverChanged || loadAfter != loadBefore;
                    var payloadOk = TurboTick.Simulation.CommandCodec.TryDecodeStop(
                                    stopCommand.Payload, out var decodedPawn, out var decodedCond)
                                    && decodedPawn == subject.thingIDNumber
                                    && decodedCond == (int)JobCondition.InterruptForced;
                    Answer(id, true, "ok",
                        $"小人 #{subject.thingIDNumber}：当前 {jobBefore}（loadID {loadBefore}）→ 应用 stop 后 "
                        + $"{jobAfter}（loadID {loadAfter}、驱动{(driverChanged ? "换了" : "没换")}）→ {(ended ? "准" : "不准")}；"
                        + $"应用 +{applied}、失败/未解析 +{missed}；载荷解码 {(payloadOk ? "准" : "不准")}"
                        + $"（pawn={subject.thingIDNumber}, condition={(int)JobCondition.InterruptForced}）；"
                        + "捕获半本进程测不到：" + SharedActions.PlayerActingNote());
                    return;
                }
            case "attack-probe":
                // **Round-trips the ordered attack target (`Pawn_TrainingTracker.attackTarget`, `:476623`).**
                //
                // This is the first row segment that reproduces a *pair* of writes rather than one field: vanilla's two
                // gizmos set `attackTarget` AND `mindState.enemyTarget` together (`:476929-476930`, `:477507-477508`), and
                // its cancel path clears the first and interrupts the job (`:476966-476967`). Carrying only the field
                // would leave the animal recording an order the job giver cannot act on, so the reading has to check both
                // halves and the decoy has to clear both.
                //
                // The decoy is a *clear*, not a different value, because there is usually nothing else in range that the
                // game will accept - and because "the field was already what we asked for" is exactly the way a round-trip
                // reading lies.
                {
                    var animals = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .Where(p => p?.training is not null).ToList();
                    if (animals.Count == 0) { Answer(id, true, "ok", "本端没有带 training 的小人，这一半测不到"); return; }
                    var subject = animals[0];
                    // Any spawned humanlike is a legal target for `TargetingParameters.ForAttackAny`; the probe only needs
                    // something that resolves by id on both sides, and a colonist always does.
                    var target = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p != subject && p.RaceProps.Humanlike);
                    if (target is null) { Answer(id, true, "ok", "本端没有第二个小人可当目标"); return; }

                    var oldTarget = subject.training.attackTarget;
                    var oldEnemy = subject.mindState?.enemyTarget;

                    PawnTrackerSync.Publish();
                    // Vanilla's own pair, in vanilla's own order.
                    subject.training.attackTarget = target;
                    if (subject.mindState is { } mind) mind.enemyTarget = target;

                    var published = PawnTrackerSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Pawn == subject.thingIDNumber);
                    var segments = row?.Trackers.Split(';') ?? [];
                    var attackSegment = segments.FirstOrDefault(f => f.StartsWith("a:", StringComparison.Ordinal));
                    var hasA = attackSegment is not null;
                    var encoded = hasA && int.TryParse(attackSegment![2..], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

                    // Decoy: clear the pair directly, WITHOUT publishing - so the row cannot be satisfied by what is
                    // already in the field.
                    subject.training.attackTarget = null;
                    if (subject.mindState is { } mind2) mind2.enemyTarget = null;
                    var decoyCleared = subject.training.attackTarget is null;

                    var beforeApplied = PawnTrackerSync.AttackTargetsApplied;
                    var beforeUnresolved = PawnTrackerSync.AttackTargetUnresolved;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readTarget = subject.training.attackTarget;
                    var readEnemy = subject.mindState?.enemyTarget;
                    var applied = PawnTrackerSync.AttackTargetsApplied - beforeApplied;
                    var unresolved = PawnTrackerSync.AttackTargetUnresolved - beforeUnresolved;

                    // Restore: the target, the enemy target, and publish so the frame is not left holding the probe.
                    subject.training.attackTarget = oldTarget;
                    if (subject.mindState is { } mind3) mind3.enemyTarget = oldEnemy;
                    PawnTrackerSync.Publish();

                    var encodedOk = encoded == target.thingIDNumber;
                    var targetOk = readTarget == target && decoyCleared;
                    var enemyOk = readEnemy == target;
                    Answer(id, true, "ok",
                        $"小人 #{subject.thingIDNumber}（带 training）：目标 小人 #{target.thingIDNumber}；"
                        + $"原记录 {oldTarget?.LabelShort ?? "无"} → 设为该目标，发出 1 行"
                        + (hasA ? $"含 a: 段（编码 {encoded}）" : "**但没有 a: 段**")
                        + $" → 编码{(encodedOk ? "准" : "不准")}；"
                        + $"先直接清空（不发）再应用该行 → 记录={readTarget?.LabelShort ?? "无"}、"
                        + $"敌情目标={readEnemy?.LabelShort ?? "无"} → 攻击目标{(targetOk ? "准" : "不准")}、"
                        + $"配对{(enemyOk ? "准" : "不准")}，应用 +{applied}、未解析 +{unresolved}；"
                        + $"已复原 记录={subject.training.attackTarget?.LabelShort ?? "无"}");
                    return;
                }
            case "follow-probe":
                // **Round-trips the animal-follow flags and the master reference on one pawn.**
                //
                // Two things this measures that the earlier probes could not:
                //   1. the two booleans travel unconditionally (a cleared checkbox is a real change, so `false` must be
                //      distinguishable from "no segment");
                //   2. `r:` is the first REFERENCE on this row - it travels as a thing id and is resolved back to a pawn.
                //      So the reading has to be the resolved pawn, not the id: an id that decodes but does not resolve is
                //      exactly the failure that looks like "the animal simply does not follow".
                //
                // The decoy for the booleans is the opposite value, and for the master it is a *different* pawn, so no
                // read-back can be satisfied by the field's own prior contents.
                {
                    var pawns = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .Where(p => p?.playerSettings is not null).ToList();
                    if (pawns.Count < 2) { Answer(id, true, "ok", $"只有 {pawns.Count} 个可用小人"); return; }
                    // **The subject needs `training`, the second pawn must be a legal master.** A colonist has no
                    // `Pawn_TrainingTracker`, and `Pawn_PlayerSettings.Master`'s setter dereferences it (`:477244`) - so
                    // choosing a colonist as the subject throws `NullReferenceException` before the probe can report
                    // anything. Vanilla only shows the master selector for animals for the same reason.
                    // Obedience first: `Pawn_PlayerSettings.Master`'s setter refuses an animal that has not learned it
                    // (`:477244`), and a rejected assignment leaves nothing to round-trip. Preferring an obedient
                    // animal makes the master half actually measurable instead of reporting a verdict on a request the
                    // game itself threw away.
                    var subject = pawns.FirstOrDefault(p => p.training is not null
                                                           && p.training.HasLearned(TrainableDefOf.Obedience))
                                  ?? pawns.FirstOrDefault(p => p.training is not null)
                                  ?? pawns[0];
                    // **Fixture: make the animal obedient if nothing in this colony is.** Without it the master half is
                    // permanently unmeasurable here - measured: `本小人没学过服从 … 这一半未测`, i.e. a mechanism nobody
                    // ever exercised. `Pawn_TrainingTracker.Train(td, trainer, complete: true)` (`:476781-476799`) sets
                    // the steps to the maximum and marks it learned; passing `trainer: null` skips the automatic master
                    // assignment that a real trainer would trigger (`:476794`), so the fixture does not pre-set the very
                    // field being measured. The training change itself travels on the `w:` segment, which is already
                    // carried, so this does not invent an unsynced state.
                    var trainedForProbe = false;
                    if (subject.training is not null && !subject.training.HasLearned(TrainableDefOf.Obedience))
                        try
                        {
                            subject.training.Train(TrainableDefOf.Obedience, null, complete: true);
                            trainedForProbe = subject.training.HasLearned(TrainableDefOf.Obedience);
                        }
                        catch (Exception) { trainedForProbe = false; }
                    var otherMaster = pawns.FirstOrDefault(p => p != subject && p.RaceProps.Humanlike) ?? pawns[1];
                    // **The diagnostic that separates the two causes of `未测`.** "No animal exists here" and "an animal
                    // exists but the obedience fixture failed" are different problems, and without these two numbers
                    // the probe's `未测` is a reading with no cause - the same complaint this project keeps making about
                    // silent early returns.
                    var animalsSeen = pawns.Count(p => p.RaceProps.Animal);
                    var animalsWithTraining = pawns.Count(p => p.RaceProps.Animal && p.training is not null);

                    var oldDrafted = subject.playerSettings.followDrafted;
                    var oldField = subject.playerSettings.followFieldwork;
                    var oldMaster = subject.playerSettings.Master;
                    var beforeMasterUnresolved = PawnTrackerSync.MasterUnresolved;

                    PawnTrackerSync.Publish();
                    subject.playerSettings.followDrafted = !oldDrafted;
                    subject.playerSettings.followFieldwork = !oldField;
                    // Through the property: the setter refuses a non-obedient pawn, which is vanilla's own rule and the
                    // reason the probe reports what it got rather than what it asked for.
                    var masterMeasurable = subject.training is not null
                                           && subject.training.HasLearned(TrainableDefOf.Obedience);
                    var masterRequested = masterMeasurable && otherMaster != oldMaster;
                    if (masterRequested) subject.playerSettings.Master = otherMaster;
                    var wroteMaster = masterMeasurable && subject.playerSettings.Master == otherMaster;

                    var published = PawnTrackerSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Pawn == subject.thingIDNumber);
                    var segments = row?.Trackers.Split(';') ?? [];
                    var hasD = segments.Any(f => f.StartsWith("d:", StringComparison.Ordinal));
                    var hasE = segments.Any(f => f.StartsWith("e:", StringComparison.Ordinal));
                    var hasR = segments.Any(f => f.StartsWith("r:", StringComparison.Ordinal));

                    // Decoy: put everything back to the old values *without* publishing, so the row cannot be satisfied
                    // by what is already there.
                    subject.playerSettings.followDrafted = oldDrafted;
                    subject.playerSettings.followFieldwork = oldField;
                    if (oldMaster is not null) subject.playerSettings.Master = oldMaster;
                    var decoyMaster = subject.playerSettings.Master;
                    var beforeFollowRows = PawnTrackerSync.AnimalFollowRows;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readDrafted = subject.playerSettings.followDrafted;
                    var readField = subject.playerSettings.followFieldwork;
                    var readMaster = subject.playerSettings.Master;
                    var followRows = PawnTrackerSync.AnimalFollowRows - beforeFollowRows;
                    var unresolved = PawnTrackerSync.MasterUnresolved - beforeMasterUnresolved;

                    subject.playerSettings.followDrafted = oldDrafted;
                    subject.playerSettings.followFieldwork = oldField;
                    if (oldMaster is not null && subject.playerSettings.Master != oldMaster) subject.playerSettings.Master = oldMaster;
                    PawnTrackerSync.Publish();

                    var draftedOk = readDrafted == !oldDrafted && readDrafted != oldDrafted;
                    var fieldOk = readField == !oldField && readField != oldField;
                    // **A rejected request is "not measured", not "wrong".** The first version compared the read-back
                    // against the value it asked for even when vanilla refused the assignment, so an animal without
                    // Obedience produced `主人 不准` while the channel had done nothing wrong - the same class of error as
                    // measuring the style channel on a lamp, and the same rule: only claim a verdict on a state the game
                    // actually produced.
                    var masterOk = !masterMeasurable
                        || !wroteMaster
                        || (!masterRequested
                            ? readMaster == oldMaster
                            : readMaster == otherMaster && decoyMaster != otherMaster);

                    Answer(id, true, "ok",
                        $"小人 #{subject.thingIDNumber}：d/e 原 {oldDrafted}/{oldField} → 设为 {!oldDrafted}/{!oldField}；"
                        + $"（本图动物 {animalsSeen} 个、带 training {animalsWithTraining} 个）"
                        + (trainedForProbe ? "（本次为测主人先给它补了服从）" : string.Empty)
                        + (masterMeasurable
                            ? $"主人 原 {oldMaster?.LabelShort ?? "无"} → 请求 {otherMaster.LabelShort}"
                              + (masterRequested
                                  ? (wroteMaster ? "（游戏接受）" : "（**游戏拒绝**，这一半未测）")
                                  : "（与原相同，跳过）")
                            : "主人：本小人没学过服从或没有 training 组件，这一半未测")
                        + "；"
                        + $"发出 {published.Count} 行，含 d/e/r 段 {hasD}/{hasE}/{hasR}；"
                        + $"先改回原值再应用该行 → 读回 d={readDrafted} e={readField} 主人={readMaster?.LabelShort ?? "无"}"
                        + $" → 跟随 {(draftedOk && fieldOk ? "准" : "**不准**")}，"
                        + $"主人 {(masterMeasurable && wroteMaster ? (masterOk ? "准" : "**不准**") : "未测")}，"
                        + $"跟随应用 +{followRows}，主人未解析 +{unresolved}；"
                        + $"已复原 d={subject.playerSettings.followDrafted} e={subject.playerSettings.followFieldwork}"
                        + $" 主人={subject.playerSettings.Master?.LabelShort ?? "无"}");
                }
                return;
            case "medcare-probe":
                // **Round-trips one pawn's medical-care level through the per-pawn row.**
                //
                // Same oracle as `hostility-probe`, and the same trap it fell into: the row is keyed by pawn, so it can
                // only be tested on the pawn it was encoded from. The decoy is a *third* value, so a read-back cannot
                // be satisfied by the field's own prior contents.
                //
                // `Best` is used as the decoy whenever possible because it is the one level that would change what a
                // doctor consumes - if it were left in place, a failure would look like success on a colony that keeps
                // using glitterworld medicine.
                {
                    var source = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p?.playerSettings is not null && !p.RaceProps.Animal);
                    if (source is null) { Answer(id, true, "ok", "没有可用小人"); return; }
                    var oldCare = source.playerSettings.medCare;
                    var recorded = oldCare == MedicalCareCategory.HerbalOrWorse
                        ? MedicalCareCategory.NormalOrWorse
                        : MedicalCareCategory.HerbalOrWorse;
                    var decoy = recorded == MedicalCareCategory.Best
                        ? MedicalCareCategory.NoCare
                        : MedicalCareCategory.Best;

                    PawnTrackerSync.Publish();
                    source.playerSettings.medCare = recorded;
                    var published = PawnTrackerSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Pawn == source.thingIDNumber);
                    var carries = row is not null
                                  && row.Trackers.Split(';').Any(f => f.StartsWith("m:", StringComparison.Ordinal));

                    source.playerSettings.medCare = decoy;
                    var beforeApply = source.playerSettings.medCare;
                    var beforeRows = PawnTrackerSync.MedicalCareRows;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readBack = source.playerSettings.medCare;
                    var applied = PawnTrackerSync.MedicalCareRows - beforeRows;

                    source.playerSettings.medCare = oldCare;
                    PawnTrackerSync.Publish();

                    Answer(id, true, "ok",
                        $"小人 #{source.thingIDNumber}：原 {oldCare} → 设为 {recorded}，发出 {published.Count} 行，"
                        + $"含 m: 段 → {(carries ? "准" : "**不准**")}；"
                        + $"应用前先改成 {beforeApply}，应用该行后读回 {readBack} → "
                        + $"{(readBack == recorded && beforeApply != recorded ? "准" : "**不准**")}"
                        + $"（应为 {recorded}，且与改前的 {beforeApply} 不同），应用计数 +{applied}；"
                        + $"已复原 {source.playerSettings.medCare}");
                }
                return;
            case "toggle-probe":
                // Two gizmo toggles that ride the per-pawn tracker row instead of a command (round 142): the psycaster's
                // entropy limit (`y:`) and the hemogen gene's "may take packs" checkbox (`x:`). Same oracle as
                // `medcare-probe`: publish a real row, read the segment back, then write a DECOY on this peer without
                // publishing and let the row write over it. The decoy is the opposite value, so a read-back cannot be
                // satisfied by what was already there.
                {
                    var parts = new List<string>();
                    // -- y: entropy limit ----------------------------------------------------------------
                    var caster = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p?.psychicEntropy is not null);
                    if (caster is null)
                    {
                        parts.Add("熵限：本存档没有带灵能的单位（这一半未测）");
                    }
                    else
                    {
                        var oldLimit = caster.psychicEntropy.limitEntropyAmount;
                        var recorded = !oldLimit;
                        PawnTrackerSync.Publish();
                        caster.psychicEntropy.limitEntropyAmount = recorded;
                        var published = PawnTrackerSync.Publish() ?? [];
                        var row = published.FirstOrDefault(r => r.Pawn == caster.thingIDNumber);
                        var segment = row?.Trackers.Split(';').FirstOrDefault(f => f.StartsWith("y:", StringComparison.Ordinal));
                        var decoy = !recorded;
                        caster.psychicEntropy.limitEntropyAmount = decoy;
                        var beforeRows = PawnTrackerSync.EntropyLimitRows;
                        if (row is not null) PawnTrackerSync.Apply(row);
                        var readBack = caster.psychicEntropy.limitEntropyAmount;
                        var applied = PawnTrackerSync.EntropyLimitRows - beforeRows;
                        caster.psychicEntropy.limitEntropyAmount = oldLimit;
                        PawnTrackerSync.Publish();
                        parts.Add($"熵限 小人 #{caster.thingIDNumber}：原 {oldLimit} → 设为 {recorded}，"
                                  + $"发出 {published.Count} 行、段 {segment ?? "无"} → "
                                  + $"{(segment == (recorded ? "y:1" : "y:0") ? "准" : "**不准**")}；"
                                  + $"先改成 {decoy}（不发），应用该行后读回 {readBack} → "
                                  + $"{(readBack == recorded && decoy != recorded ? "准" : "**不准**")}"
                                  + $"（应用计数 +{applied}）；已复原 {caster.psychicEntropy.limitEntropyAmount}");
                    }
                    // -- x: hemogen packs allowed ---------------------------------------------------------
                    var hemogen = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .Select(p => (Pawn: p, Gene: p?.genes?.GetFirstGeneOfType<Gene_Hemogen>()))
                        .FirstOrDefault(t => t.Pawn is not null && t.Gene is not null);
                    if (hemogen.Gene is null)
                    {
                        parts.Add("血原包：本存档没有带血原基因的小人（这一半未测）");
                    }
                    else
                    {
                        var oldAllowed = hemogen.Gene.hemogenPacksAllowed;
                        var recorded = !oldAllowed;
                        PawnTrackerSync.Publish();
                        hemogen.Gene.hemogenPacksAllowed = recorded;
                        var published = PawnTrackerSync.Publish() ?? [];
                        var row = published.FirstOrDefault(r => r.Pawn == hemogen.Pawn.thingIDNumber);
                        var segment = row?.Trackers.Split(';').FirstOrDefault(f => f.StartsWith("x:", StringComparison.Ordinal));
                        var decoy = !recorded;
                        hemogen.Gene.hemogenPacksAllowed = decoy;
                        var beforeRows = PawnTrackerSync.HemogenPackRows;
                        if (row is not null) PawnTrackerSync.Apply(row);
                        var readBack = hemogen.Gene.hemogenPacksAllowed;
                        var applied = PawnTrackerSync.HemogenPackRows - beforeRows;
                        hemogen.Gene.hemogenPacksAllowed = oldAllowed;
                        PawnTrackerSync.Publish();
                        parts.Add($"血原包 小人 #{hemogen.Pawn.thingIDNumber}：原 {oldAllowed} → 设为 {recorded}，"
                                  + $"发出 {published.Count} 行、段 {segment ?? "无"} → "
                                  + $"{(segment == (recorded ? "x:1" : "x:0") ? "准" : "**不准**")}；"
                                  + $"先改成 {decoy}（不发），应用该行后读回 {readBack} → "
                                  + $"{(readBack == recorded && decoy != recorded ? "准" : "**不准**")}"
                                  + $"（应用计数 +{applied}）；已复原 {hemogen.Gene.hemogenPacksAllowed}");
                    }
                    Answer(id, true, "ok", string.Join("；\n", parts));
                }
                return;
            case "hostility-probe":
                // **Round-trips one pawn's row through the channel.**
                //
                // The first version applied the row onto a *different* pawn and read it back. That can never work: the
                // row is keyed by pawn id, so `Apply` resolves the sender's pawn and writes it there - the reading came
                // back `Flee` on the target while the write had actually landed on the source, and `应用计数 +1` made it
                // look like it had worked. A per-pawn row cannot be tested on another pawn.
                //
                // So the round trip is on the same pawn: set the mode, publish (one row), then set the pawn back
                // *directly without publishing*, then apply the captured row. If the pawn returns to the recorded mode,
                // the encode/decode/write path is intact. Nothing here can be satisfied by the pawn's own prior value,
                // because it was deliberately changed away before the apply.
                {
                    var source = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
                        .FirstOrDefault(p => p?.playerSettings is not null);
                    if (source is null) { Answer(id, true, "ok", "没有可用小人"); return; }
                    var oldMode = source.playerSettings.hostilityResponse;
                    // A mode different from both the current one and `Flee`, so neither "it did not change" nor "it
                    // defaulted" can produce the expected value.
                    var recorded = oldMode == HostilityResponseMode.Attack
                        ? HostilityResponseMode.Ignore
                        : HostilityResponseMode.Attack;
                    var decoy = recorded == HostilityResponseMode.Attack
                        ? HostilityResponseMode.Ignore
                        : HostilityResponseMode.Attack;

                    PawnTrackerSync.Publish();
                    source.playerSettings.hostilityResponse = recorded;
                    var published = PawnTrackerSync.Publish() ?? [];
                    var row = published.FirstOrDefault(r => r.Pawn == source.thingIDNumber);
                    var carries = row is not null
                                  && row.Trackers.Split(';').Any(f => f.StartsWith("h:", StringComparison.Ordinal));

                    // Move the pawn to a third value *without* publishing, so the field cannot already hold the
                    // expected answer when the row is applied.
                    source.playerSettings.hostilityResponse = decoy;
                    var beforeApply = source.playerSettings.hostilityResponse;
                    var beforeRows = PawnTrackerSync.HostilityRows;
                    if (row is not null) PawnTrackerSync.Apply(row);
                    var readBack = source.playerSettings.hostilityResponse;
                    var applied = PawnTrackerSync.HostilityRows - beforeRows;

                    source.playerSettings.hostilityResponse = oldMode;
                    PawnTrackerSync.Publish();

                    Answer(id, true, "ok",
                        $"小人 #{source.thingIDNumber}：原 {oldMode} → 设为 {recorded}，发出 {published.Count} 行，"
                        + $"含 h: 段 → {(carries ? "准" : "**不准**")}；"
                        + $"应用前先改成 {beforeApply}，应用该行后读回 {readBack} → "
                        + $"{(readBack == recorded && beforeApply != recorded ? "准" : "**不准**")}"
                        + $"（应为 {recorded}，且与改前的 {beforeApply} 不同），应用计数 +{applied}；"
                        + $"已复原 {source.playerSettings.hostilityResponse}");
                }
                return;
            case "build-style-probe":
                // **Compares the channel against vanilla on identical inputs.**
                //
                // The earlier version asserted "the blueprint has the style the designator held", which is the wrong
                // oracle: `Thing.SetStyleDef` (`:559509-559523`) needs `CompStyleable`, and whether a given
                // `ThingStyleDef` applies to a given building is decided by vanilla's `StyleCategory` resolution. Both
                // filters can legitimately reject, so "the style did not stick" says nothing about the channel.
                //
                // What the channel must guarantee is *equality with vanilla*: same def, same style, same glower, one
                // placement through `DesignatorSync.Apply` and one through the designator directly. If both objects end
                // up identical, the replay reproduced the sender's write sites - including whichever branch vanilla
                // took. That holds whether or not the style applies, which is exactly the property that has to survive
                // a DLC the profile does not load.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, true, "ok", "没有当前地图"); return; }
                    var styleDefs = DefDatabase<ThingStyleDef>.AllDefsListForReading.Count;
                    var styleDef = styleDefs > 0 ? DefDatabase<ThingStyleDef>.AllDefsListForReading[0] : null;
                    // A def that can hold a style if any exists, else a glower-bearer: the equality test needs a def
                    // whose placement vanilla accepts, and either class does that.
                    var def = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d =>
                                  d is not null && d.blueprintDef is not null
                                  && d.GetCompProperties<CompProperties_Styleable>() is not null)
                              ?? DefDatabase<ThingDef>.GetNamedSilentFail("StandingLamp");
                    if (def?.blueprintDef is null) { Answer(id, true, "ok", "没有可放置的 def"); return; }
                    var colour = new ColorInt(17, 203, 91, 255);

                    // Two empty cells, adjacent, both accepted.
                    var cells = new List<IntVec3>();
                    for (var radius = 2; radius < 40 && cells.Count < 2; radius++)
                        foreach (var candidate in GenRadial.RadialCellsAround(map.Center, radius, true))
                        {
                            if (cells.Contains(candidate)) continue;
                            if (!candidate.InBounds(map)) continue;
                            if (candidate.GetThingList(map).Count != 0) continue;
                            if (map.terrainGrid.TerrainAt(candidate).IsWater) continue;
                            cells.Add(candidate);
                            if (cells.Count == 2) break;
                        }
                    if (cells.Count < 2) { Answer(id, true, "ok", "找不到两个空格"); return; }
                    var vanillaCell = cells[0];
                    var channelCell = cells[1];

                    Designator_Build MakeDesignator()
                    {
                        var made = new Designator_Build(def);
                        made.styleDef = styleDef;
                        made.glowerColorOverride = colour;
                        return made;
                    }

                    var wasApplying = SharedActions.Applying;
                    SharedActions.Applying = true;
                    try
                    {
                        // Reference: vanilla's own body, no channel involved.
                        var reference = MakeDesignator();
                        reference.DesignateSingleCell(vanillaCell);

                        // Channel: encode and replay the same inputs.
                        var beforeStyles = DesignatorSync.stylesCarried;
                        var chunks = DesignatorSync.EncodeChunks(MakeDesignator(), [channelCell]);
                        var payloadText = chunks.Count > 0
                            ? System.Text.Encoding.UTF8.GetString(chunks[0].Payload)
                            : string.Empty;
                        var fieldCount = payloadText.Length == 0 ? 0 : payloadText.Split('|').Length;
                        foreach (var chunk in chunks)
                            DesignatorSync.Apply(System.Text.Encoding.UTF8.GetString(chunk.Payload));
                        var carried = DesignatorSync.stylesCarried - beforeStyles;

                        var referenceBlueprint = vanillaCell.GetThingList(map).OfType<Blueprint_Build>().FirstOrDefault();
                        var channelBlueprint = channelCell.GetThingList(map).OfType<Blueprint_Build>().FirstOrDefault();

                        string Describe(Blueprint_Build? b) => b is null
                            ? "无"
                            : $"样式={b.StyleDef?.defName ?? "无"}"
                              + $" glower={(b.glowerColorOverride is { } c ? $"{c.r},{c.g},{c.b},{c.a}" : "无")}";

                        var bothPlaced = referenceBlueprint is not null && channelBlueprint is not null;
                        var equal = bothPlaced
                                    && referenceBlueprint!.StyleDef?.defName == channelBlueprint!.StyleDef?.defName
                                    && Nullable.Equals(referenceBlueprint.glowerColorOverride,
                                        channelBlueprint.glowerColorOverride);

                        Answer(id, true, "ok",
                            $"def {def.defName}，样式样本 {(styleDef?.defName ?? "无（本 profile 无 ThingStyleDef）")}；"
                            + $"载荷 {fieldCount} 段；"
                            + $"原版直放@{vanillaCell.x},{vanillaCell.z} {Describe(referenceBlueprint)}；"
                            + $"通道重放@{channelCell.x},{channelCell.z} {Describe(channelBlueprint)}；"
                            + $"两者一致 → {(equal ? "准" : "**不准**")}"
                            + (bothPlaced ? string.Empty : "（有蓝图没放成，这一行是空测量）")
                            + $"；本次携带样式/颜色的重放 +{carried}（累计 {DesignatorSync.stylesCarried}）");
                    }
                    finally { SharedActions.Applying = wasApplying; }
                }
                return;
            case "history-probe":
                // **Measures the history-event channel through the reader the player actually sees.**
                //
                // The interesting reading is not "the table changed" but "the query the faction dialog runs now answers
                // with it". `FactionDialogMaker.GetRecentEvents` (`:644122`) calls `GetRecentCountWithinTicks` and
                // `GetRecent` (`:362845`, `:362862`), so those two are the oracle: a write that leaves them answering the
                // pre-load window is exactly the bug this channel exists to fix, and a write that moves them but not the
                // sum would print a line with the wrong sign.
                //
                // Uses a faction that exists on this map so the per-faction table is exercised too; the colonist table is
                // exercised in the same call (Faction = -1).
                {
                    var manager = Find.HistoryEventsManager;
                    if (manager is null) { Answer(id, true, "ok", "没有 HistoryEventsManager"); return; }
                    var target = DefDatabase<HistoryEventDef>.AllDefsListForReading
                        .FirstOrDefault(d => d is not null && d.maxRemembered > 4);
                    if (target is null) { Answer(id, true, "ok", "没有可用的 HistoryEventDef"); return; }
                    var faction = Find.FactionManager.AllFactionsListForReading
                        .FirstOrDefault(f => f is not null && f.def?.isPlayer != true);
                    var factionId = faction?.loadID ?? -1;
                    var now = Find.TickManager.TicksGame;

                    // The oracle before anything is written.
                    var beforeCount = manager.GetRecentCountWithinTicks(target, 3600000, faction);
                    var beforeLast = manager.GetLastTicksGame(target, faction);
                    var beforeAny = manager.Any(target, faction);

                    // A window nothing could have produced by accident: two entries, one of them decades ago (outside the
                    // 3600000-tick query window), with goodwill sums that are not symmetric.
                    var ticks = "-5000000," + (now - 1000).ToString(CultureInfo.InvariantCulture);
                    var goodwills = "-7,13";
                    var beforeApplied = HistorySync.RowsApplied;
                    HistorySync.Apply(new ReplicaHistoryEvent(target.defName, factionId, ticks, goodwills));

                    var afterCount = manager.GetRecentCountWithinTicks(target, 3600000, faction);
                    var afterLast = manager.GetLastTicksGame(target, faction);
                    var afterAny = manager.Any(target, faction);
                    var recentTicks = new List<int>();
                    var recentGoodwill = new List<int>();
                    manager.GetRecent(target, 3600000, recentTicks, recentGoodwill, faction);
                    var sum = recentGoodwill.Sum();

                    // The send half: a recorded event must mark exactly one row, and publishing it must emit one row whose
                    // tick list ends with the current tick.
                    HistorySync.Publish();
                    var beforeSent = HistorySync.RowsSent;
                    // `Named` is `Named(this object arg, string label)` - one label, not a name plus a label. The
                    // argument name is the label, so a pawn passed here lands in `HistoryEventArgsNames.Doer` and the
                    // colonist table is the one `RecordEvent` writes.
                    var doer = Find.Maps.SelectMany(m => m.mapPawns.FreeColonists).FirstOrDefault();
                    manager.RecordEvent(doer is null
                        ? new HistoryEvent(target)
                        : new HistoryEvent(target, doer.Named(HistoryEventArgsNames.Doer)));
                    var published = HistorySync.Publish();
                    var emitted = published?.Where(row => row.Def == target.defName).ToList() ?? [];

                    var readerMoved = afterCount > beforeCount || (!beforeAny && afterAny);
                    var sumMatches = sum == 13;
                    Answer(id, true, "ok",
                        $"def {target.defName} 派系 #{factionId}：写入前 近期条数={beforeCount} 最近tick={beforeLast} 有过={beforeAny}；"
                        + $"写入后 近期条数={afterCount} 最近tick={afterLast} 有过={afterAny} 近窗口={recentTicks.Count}条 好感合计={sum}"
                        + $" → {(readerMoved ? "原版查询读到了" : "**原版查询没读到**")}，"
                        + $"好感合计应为 13 → {(sumMatches ? "准" : "**不准**")}，应用计数 +{HistorySync.RowsApplied - beforeApplied}；"
                        + $"记录一次事件后发出 {emitted.Count} 行（共 {HistorySync.RowsSent - beforeSent} 行）"
                        + $" → {(emitted.Count >= 1 ? "准" : "**记录没有标记行**")}，"
                        + $"末条 tick 应为 {now} → "
                        + $"{(emitted.Count > 0 && emitted[0].Ticks.EndsWith(now.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? "准" : "**不准**")}；"
                        + HistorySync.Summary());
                }
                return;
            case "cooldown-probe":
                // **Measures the send half and the apply half of the cooldown channel in one process.**
                //
                // The send half is observable here because `PublishCooldowns` is a pure comparison against its own
                // `sentCooldowns` table: stamping a faction and calling it directly must produce exactly one row. The
                // apply half is observable because `ApplyCooldown` writes public fields, so the field can be read back.
                //
                // Neither half can be observed the other way round: this process is the authority, `PublishFrame` does
                // not run, and a replica would need a second peer. The readings below therefore prove the channel is
                // wired and encode/decode-correct; they do not prove a second peer shows the same button state.
                {
                    var parts = (argument ?? string.Empty)
                        .Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var manager = Find.FactionManager;
                    var factions = manager?.AllFactionsListForReading;
                    if (factions is null || factions.Count == 0)
                    {
                        Answer(id, true, "ok", "没有派系");
                        return;
                    }
                    var faction = parts.Length > 0 && int.TryParse(parts[0], out var wanted)
                        ? factions.FirstOrDefault(f => f is not null && f.loadID == wanted)
                        : factions.FirstOrDefault(f => f is not null && f.def?.isPlayer != true);
                    if (faction is null)
                    {
                        Answer(id, true, "ok", $"没有这个派系：{parts[0]}");
                        return;
                    }
                    var now = Find.TickManager.TicksGame;
                    var oldTrader = faction.lastTraderRequestTick;
                    var oldOrbital = faction.lastOrbitalTraderRequestTick;
                    var oldMilitary = faction.lastMilitaryAidRequestTick;
                    var before = FactionSync.CooldownsApplied;
                    // Drain whatever the comparison already had pending, so the next call measures only the change this
                    // probe just made - a leftover row from an earlier tick would look identical.
                    FactionSync.PublishCooldowns();

                    faction.lastTraderRequestTick = now;
                    var published = FactionSync.PublishCooldowns();
                    var emitted = published?.Where(row => row.Faction == faction.loadID).ToList() ?? [];
                    var sentOne = emitted.Count == 1 && emitted[0].Trader == now;

                    // A value that cannot have come from anywhere else, so "the field moved" is the field moving and not
                    // a coincidence with the current tick.
                    const int marker = 1234567;
                    FactionSync.ApplyCooldown(new ReplicaFactionCooldown(faction.loadID, marker, marker, marker));
                    var applied = faction.lastTraderRequestTick == marker
                        && faction.lastOrbitalTraderRequestTick == marker
                        && faction.lastMilitaryAidRequestTick == marker;

                    faction.lastTraderRequestTick = oldTrader;
                    faction.lastOrbitalTraderRequestTick = oldOrbital;
                    faction.lastMilitaryAidRequestTick = oldMilitary;
                    FactionSync.PublishCooldowns();

                    Answer(id, true, "ok",
                        $"派系 #{faction.loadID}「{faction.Name}」原冷却 商队={oldTrader} 轨道={oldOrbital} 军事={oldMilitary}；"
                        + $"标记后发出 {emitted.Count} 行（{(emitted.Count > 0 ? emitted[0].Trader.ToString() : "无")}）"
                        + $"，应为 1 行且等于 {now} → {(sentOne ? "准" : "**不准**")}；"
                        + $"应用标记 {marker} → {(applied ? "三字段全部写入" : "**未写入**")}，"
                        + $"应用计数 +{FactionSync.CooldownsApplied - before}；已复原 "
                        + $"商队={faction.lastTraderRequestTick} 轨道={faction.lastOrbitalTraderRequestTick} "
                        + $"军事={faction.lastMilitaryAidRequestTick}");
                }
                return;
            case "incident-probe":
                // **Exercises the IncidentQueue.Add capture with a real incident.** Without this the only way to observe
                // the channel is for a player to ask an ally for a trader and wait 120000 ticks, which cannot be
                // measured in a session. Queueing a harmless incident through the patched entry point shows whether the
                // prefix fires, whether it publishes, and what it published - and the apply side then has to land it.
                //
                // Argument: `<defName> [delayTicks]`. Defaults to `TraderCaravanArrival` at +120000, which is vanilla's
                // own caravan recipe (`Assembly-CSharp.decompiled.cs:486929`).
                {
                    var parts = (argument ?? string.Empty)
                        .Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var defName = parts.Length > 0 ? parts[0] : "TraderCaravanArrival";
                    var delay = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 120000;
                    var def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
                    if (def is null)
                    {
                        Answer(id, true, "ok", $"没有这个事件 def：{defName}");
                        return;
                    }
                    var map = Find.CurrentMap;
                    var faction = Find.FactionManager?.AllFactionsListForReading
                        ?.FirstOrDefault(f => f is not null && f.def?.isPlayer != true && f.HostileTo(Faction.OfPlayer) == false);
                    if (map is null)
                    {
                        Answer(id, true, "ok", "没有当前地图");
                        return;
                    }
                    var before = SharedActions.diplomacyApplied;
                    var beforeMissed = SharedActions.diplomacyMissed;
                    // **Two modes, because the two halves of the channel are observable for different reasons.**
                    //
                    // `queue` calls vanilla's own `IncidentQueue.Add`, which is what a player's dialog click reaches.
                    // On the authority `PlayerActing` is false (this process sends no commands), so the prefix declines
                    // and the incident lands locally - that is correct behaviour, and it means this mode measures the
                    // patch is installed rather than that the capture fires.
                    //
                    // `apply` feeds a real command through `SharedActions.Apply`, which is exactly what a peer's
                    // payload becomes. That is the only way to observe the receiving half on a single process, and it
                    // is the half that was missing before this round: a replica had no way to put anything in its own
                    // `incidentQueue`, because it never ticks one.
                    var mode = parts.Length > 2 ? parts[2] : "queue";
                    string result;
                    if (mode == "apply")
                    {
                        var sequence = SharedColony.NextSequence();
                        var injected = TurboTick.Simulation.CommandCodec.DiplomacyCommand(def.defName,
                            faction?.loadID ?? -1, string.Empty, map.uniqueID, 240000, false, sequence);
                        SharedActions.Apply(injected);
                        result = $"注入 {def.defName} 到应用侧；本端应用 {SharedActions.diplomacyApplied - before} 次、"
                            + $"缺图/缺 def {SharedActions.diplomacyMissed - beforeMissed} 次";
                    }
                    else
                    {
                        var parms = new IncidentParms { target = map, faction = faction, forced = true };
                        var queued = Find.Storyteller?.incidentQueue?.Add(def, Find.TickManager.TicksGame + delay, parms);
                        result = $"已排队 {def.defName}（+{delay} tick）返回 {queued}；"
                            + $"本端应用 {SharedActions.diplomacyApplied - before} 次、"
                            + $"缺图/缺 def {SharedActions.diplomacyMissed - beforeMissed} 次；"
                            + $"玩家操作中={SharedActions.PlayerActing}";
                    }
                    var queueNow = Find.Storyteller?.incidentQueue;
                    var queuedNow = queueNow?.Count ?? -1;
                    // The list is private (`:384039`) and `Count` is the only public read, so the entries are reached
                    // through the field - the same choice `CompScribeLabels` and the settlement probes make for state
                    // the game does not expose. Reading it is what turns "the queue grew" into "the queue grew by
                    // exactly the incident that was sent".
                    var entries = string.Empty;
                    if (queuedNow > 0 && HarmonyLib.AccessTools
                            .Field(typeof(IncidentQueue), "queuedIncidents")?.GetValue(queueNow) is System.Collections.IEnumerable list)
                    {
                        // `QueuedIncident` is public and exposes `FiringIncident` (`:384002`) and `FireTick` (`:384000`),
                        // so a cast is enough. The first version reached for fields named `firingIncident` and `fireTick`:
                        // the real field is `firingInc` (`:383990`) and `fireTick` is private, so the reader printed `?`
                        // for every entry - a wrong field name looks exactly like an empty queue.
                        var names = new List<string>();
                        foreach (var entry in list)
                        {
                            if (entry is not QueuedIncident queued) continue;
                            names.Add($"{queued.FiringIncident?.def?.defName ?? "?"}@{queued.FireTick}");
                            if (names.Count >= 4) break;
                        }
                        if (names.Count > 0) entries = "（" + string.Join("、", names) + "）";
                    }
                    Answer(id, true, "ok",
                        $"{result}；目标图 {map.uniqueID}，派系 {(faction is null ? "无" : faction.Name)}；"
                        + $"本端事件队列现有 {queuedNow} 条{entries}");
                }
                return;
            case "settlement-absent-map":
                // **Reproduces the replica's intermediate state on the authority.** The settlement table and the map
                // channel are separate, so a replica can hold a site whose map it does not have yet. In that window
                // `SettlementAt` answers 0 for every cell of that land - indistinguishable from "nobody owns this" - and
                // every gate permits everything. No other command can put a site into that state, so it could not be
                // measured at all.
                //
                // Rewrites one site's `MapId` to an id that resolves to nothing, reads the gate answer before and after,
                // then puts the original id back.
                {
                    var data = SettlementGates.Data;
                    if (data is null || !data.Active) { Answer(id, true, "ok", "所有权层未启用"); return; }
                    var site = data.Sites.FirstOrDefault(each => each is not null && !each.Retired);
                    if (site is null) { Answer(id, true, "ok", "没有可用据点；先跑 script-settlement"); return; }
                    var originalMap = site.MapId;
                    var originalSiteMap = MapLifecycleSync.FindMapByUniqueId(originalMap);
                    var centre = new IntVec3((site.MinX + site.MaxX) / 2, 0, (site.MinZ + site.MaxZ) / 2);
                    var before = originalSiteMap is null ? -2 : data.SettlementAt(originalMap, centre);
                    // An id no map can have: `nextMapID` only grows, so a large one never collides with a live map.
                    var ghost = 900000 + site.Id;
                    site.MapId = ghost;
                    // The counter is throttled to the tick clock, so it has to be poked past its interval.
                    var after = data.SettlementAt(originalMap, centre);
                    var ghostAnswer = data.SettlementAt(ghost, centre);
                    data.RefreshAbsentMaps();
                    var absent = data.AbsentMapSites;
                    var gateLine = SettlementGates.Summary();
                    site.MapId = originalMap;
                    data.RefreshAbsentMaps();
                    var absentRestored = data.AbsentMapSites;
                    data.SettlementAt(originalMap, centre);
                    Answer(id, true, "ok",
                        $"据点 #{site.Id}「{site.Name}」原图 {originalMap}"
                        + $"（{(originalSiteMap is null ? "本端没有" : "在图内")}）；"
                        + $"改指幽灵图 {ghost} 后：原名坐标归属={after}（0 = 被当成无主地）、幽灵图同格归属={ghostAnswer}、"
                        + $"缺图据点数={absent}（复原后 {absentRestored}）；权限摘要：{gateLine}；已复原 MapId={site.MapId}"
                        + $"（改前原名坐标归属={before}）");
                }
                return;
            case "pawn-to-map":
                // **Moves a colonist to another map, so the cross-map half of a two-settlement colony can be measured.**
                //
                // Every gate in `SettlementGates` resolves its map from `MapHeld`, and `UnrecordedColonists` now walks
                // `Find.Maps` - but neither could be observed: this save has one colonist spawned on map 0 and no command
                // could put anybody on map 1. A reading with no pawn on the second map proves the code compiles, not that
                // it counts the right map.
                //
                // Argument: `[mapId] [x] [z]` or `[mapId]` alone (lands at the map's centre). Uses `TransferTo`, which is
                // the same entry the game uses for gravship landing and caravan arrival; `SpawnSetup` on the target map is
                // its job, so nothing here touches `position` directly - setting `Position` on a pawn registered to
                // another map is the mistake that leaves two listers claiming one thing.
                {
                    var parts = (argument ?? string.Empty)
                        .Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var mapField = parts.Length > 0 && parts[0].StartsWith('#')
                        ? parts.ElementAtOrDefault(1)
                        : parts.FirstOrDefault();
                    if (!int.TryParse(mapField, out var wantedMap))
                    {
                        Answer(id, true, "ok", "用法：pawn-to-map [#pawnId] <mapId> [x z]");
                        return;
                    }
                    var destination = MapLifecycleSync.FindMapByUniqueId(wantedMap);
                    if (destination is null)
                    {
                        Answer(id, true, "ok", $"地图 uniqueID={wantedMap} 不在本端列表"
                            + $"（{string.Join("/", Find.Maps.Select(m => m.uniqueID))}）");
                        return;
                    }
                    // **A leading `#id` names the pawn.** Without it the command always took the first colonist in
                    // `Find.Maps` order, so a two-colonist colony could only ever move the same one - measured: the
                    // second call reported `小人 #870` while the pawn in question was #873. Composing measurements then
                    // means "whichever pawn the probe happens to pick", which is not a measurement.
                    Pawn? subject = null;
                    if (parts.Length > 0 && parts[0].StartsWith('#')
                        && int.TryParse(parts[0][1..], out var wantedPawn))
                    {
                        foreach (var map in Find.Maps)
                        {
                            if (map?.mapPawns is null) continue;
                            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                                if (pawn is not null && pawn.thingIDNumber == wantedPawn) { subject = pawn; break; }
                            if (subject is not null) break;
                        }
                        if (subject is null)
                        {
                            Answer(id, true, "ok", $"找不到小人 #{wantedPawn}（不在任何地图的已生成列表里）");
                            return;
                        }
                    }
                    subject ??= Find.Selector?.SingleSelectedThing as Pawn;
                    if (subject is null)
                    {
                        foreach (var map in Find.Maps)
                        {
                            if (map?.mapPawns is null) continue;
                            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                                if (pawn is { IsColonist: true }) { subject = pawn; break; }
                            if (subject is not null) break;
                        }
                    }
                    if (subject is null) { Answer(id, true, "ok", "本端没有可移动的殖民者"); return; }

                    var fromMap = subject.MapHeld?.uniqueID ?? -1;
                    // Field layout shifts by one when the pawn is named: `#873 1 10 10` vs `1 10 10`. Reading the
                    // last two fields as coordinates works for both without a second branch.
                    var cell = parts.Length >= 2
                        && int.TryParse(parts[^2], out var px) && int.TryParse(parts[^1], out var pz)
                            ? new IntVec3(px, 0, pz)
                            : destination.Center;
                    if (!cell.InBounds(destination))
                    {
                        Answer(id, true, "ok", $"格 {cell} 不在地图 {destination.uniqueID}"
                            + $"（{destination.Size.x}x{destination.Size.z}）");
                        return;
                    }
                    // **No `TransferTo` on `Pawn`** (the first version assumed one and failed to compile). Vanilla moves a
                    // pawn between maps by deinit-and-respawn: `MapDeiniter.PassPawnsToWorld` (`:78852-78895`) calls
                    // `pawn.DeSpawnOrDeselect()` and then hands the pawn to the world or a caravan. For a measurement
                    // that keeps the pawn spawned, the matching pair is `DeSpawn` on the old map and `SpawnSetup` on the
                    // new one - which is what `GenSpawn` does internally. Setting `Position` on a pawn still registered to
                    // the old map is the mistake that leaves two listers holding one thing.
                    subject.DeSpawnOrDeselect();
                    GenSpawn.Spawn(subject, cell, destination, Rot4.South);
                    // Read back from the map's own pawn list, not from the pawn's field: a transfer that failed halfway
                    // leaves `MapHeld` set while the lister still holds the pawn on the old map, and only the lister
                    // answers what the gates will see.
                    var onTarget = destination.mapPawns.AllPawnsSpawned.Any(p => p == subject);
                    var stillOnSource = fromMap >= 0
                        && (MapLifecycleSync.FindMapByUniqueId(fromMap)?.mapPawns.AllPawnsSpawned.Any(p => p == subject) ?? false);
                    Answer(id, true, "ok",
                        $"小人 #{subject.thingIDNumber}：地图 {fromMap} → {subject.MapHeld?.uniqueID ?? -1}，位置 {subject.PositionHeld}；"
                        + $"目标图登记={onTarget}，原图仍登记={stillOnSource}");
                }
                return;
            case "settlement-claim":
                // **The explicit answer to the gap `settlement-report` now counts (round 93).** Every automatic fallback
                // was wrong: on a shared colony all players' pawns are one faction, so neither the ground a pawn stands on
                // nor the settlement's faction separates an owner's colonist from a neighbour's visitor. Claiming is
                // therefore a decision someone makes, and this command is how they make it without a UI.
                // Argument: `<settlement>` or `<settlement> <pawnId>`; with no pawn id it claims every unrecorded colonist
                // standing on that settlement's land.
                Answer(id, true, "ok", ScriptedSettlement.Claim(argument));
                return;
            case "transport-fixture":
                // Eight ledger entries say "no carrier in this save". Zero objects means zero observations, so the
                // sentence is the same whether the channel works or not. This places the carriers.
                Answer(id, true, "ok", TransportFixture.Place(argument));
                return;
            case "xenogerm-target":
                // The item half of `SetTargetPawn`: does the xenogerm's own fragment carry `targetPawn`.
                Answer(id, true, "ok", XenogermProbe.Target(argument));
                return;
            case "xenogerm-probe":
                // `Dialog_CreateXenogerm.Accept` is a modal button, so `GizmoStatePatch` cannot see it. What it writes
                // lives on the assembling building and is scribed by `ExposeData`; this measures whether the fragment
                // really carries it and really moves.
                Answer(id, true, "ok", XenogermProbe.Probe(argument));
                return;
            case "transport-launch":
                // A real launch through the shipped path, so "the carrier reaches a replica" is measured.
                Answer(id, true, "ok", TransportFixture.Launch(argument));
                return;
            case "transport-details":
                // A placement that reports success and is then absent has four explanations; this names which.
                Answer(id, true, "ok", TransportFixture.Details());
                return;
            case "transport-list":
                // The defName is the part that drifts; this answers "what is it called in this version".
                Answer(id, true, "ok", TransportFixture.ListDefs(argument));
                return;
            case "settlement-pod":
                // Places a cryptosleep casket so the `Pod` kind has a real object. That kind had four table rows and no
                // classifier arm, so `Care` could not admit a visitor to a pod no matter what the owner granted - and
                // nothing in the game would say so, because the take-apart rows also exist on `Building`.
                Answer(id, true, "ok", ScriptedSettlement.PlacePod(argument));
                return;
            case "settlement-door":
                // Puts a real door on the map so the `Pass`/`Door` gate has an object to be asked about. The first
                // reading of that gate was 「据点里没有门；闸门无从测起」 - honest, and useless: a base with no doors
                // produces no refusals, so the gate could ship broken and never be caught.
                Answer(id, true, "ok", ScriptedSettlement.PlaceDoor(argument));
                return;
            case "settlement-workgate":
                // The third chokepoint, and the one whose act is derived per work giver (round 135): "which work givers
                // still see work" is a different question from "does the gate answer", and only the first one can show
                // that an unlisted pair is being refused for everything.
                Answer(id, true, "ok", ScriptedSettlement.WorkGateProbe(argument));
                return;
            case "settlement-chokepoint":
                // The installed patch, not the decision function (round 134). A gate whose Harmony signature never
                // matched leaves the decision correct and unused, and both read identically from outside.
                Answer(id, true, "ok", ScriptedSettlement.ChokepointProbe(argument));
                return;
            case "settlement-gate":
                // One question, one named player, one named map (round 132). The deny half of the ownership layer had no
                // reading for twenty rounds because the only player-keyed cell gate reads `Find.CurrentMap`; a probe that
                // cannot name the map cannot ask "player 2 on player 1's land".
                Answer(id, true, "ok", ScriptedSettlement.GateProbe(argument));
                return;
            case "settlement-probe":
                // Asks the gate directly about real objects. A gate that is installed and never refuses looks exactly
                // like one that does not work, and a colony whose pawns stay inside their own settlement produces no
                // refusals at all - so the refusal path needs a way to be exercised on demand.
                Answer(id, true, "ok", ScriptedSettlement.Probe(argument));
                return;
            case "script-immunity":
                // Gives a colonist a real immunity record with a non-zero value, so the immunity channel can be observed
                // carrying something. Without it a colony with no illness reads "0 records / sent 0", and that number
                // says nothing about whether the channel works - the same trap `nudge-probe` exists to avoid.
                {
                    var def = DefDatabase<HediffDef>.GetNamedSilentFail(
                        string.IsNullOrWhiteSpace(argument) ? "Flu" : argument.Trim());
                    if (def is null || def.CompProps<HediffCompProperties_Immunizable>() is null)
                    {
                        Answer(id, false, "NotFound", "不是可免疫的病症定义：" + argument);
                        return;
                    }
                    Pawn? target = null;
                    foreach (var map in Find.Maps)
                        foreach (var candidate in map.mapPawns.AllPawnsSpawned)
                        {
                            if (candidate?.health?.immunity is null || !candidate.RaceProps.Humanlike) continue;
                            target = candidate;
                            break;
                        }
                    if (target is null) { Answer(id, false, "NoPawn", "没有可用的类人小人"); return; }
                    target.health.immunity.TryAddImmunityRecord(def, def);
                    var record = target.health.immunity.GetImmunityRecord(def);
                    if (record is not null) record.immunity = 0.42f;
                    Answer(id, true, "ok", $"已给 {target.LabelShort} #{target.thingIDNumber} 加免疫记录 "
                        + $"{def.defName} = {record?.immunity ?? -1f:0.00}");
                }
                return;
            case "script-tracker":
                // Makes a colonist guilty and pushes an animal's training decay stamp into the past, so both sub-tracker
                // paths have something to carry. Without this the census can only ever read "0 guilty", which is the same
                // reading for a working channel over a peaceful colony and for a dead one.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var trackerReport = new System.Text.StringBuilder();
                    Pawn? guilty = null;
                    Pawn? animal = null;
                    foreach (var candidate in map.mapPawns.AllPawnsSpawned)
                    {
                        if (candidate is null) continue;
                        if (guilty is null && candidate.IsColonist) guilty = candidate;
                        if (animal is null && candidate.training is not null && !candidate.IsColonist) animal = candidate;
                    }
                    if (guilty is not null)
                    {
                        // Through the game's own entry point: it also writes the thought and the history event, which is
                        // exactly the state a real guilty verdict leaves behind.
                        guilty.guilt.Notify_Guilty(60000);
                        trackerReport.Append($"已判 {guilty.LabelShort} 有罪（剩 {guilty.guilt.TicksUntilInnocent} tick）");
                    }
                    else trackerReport.Append("没有殖民者");
                    if (animal is not null)
                    {
                        var stamp = HarmonyLib.AccessTools.Field(typeof(Pawn_TrainingTracker), "countDecayFrom");
                        var before = stamp?.GetValue(animal.training) as int? ?? 0;
                        // The debug action is the game's own way to say "decay now"; using it keeps the stamp consistent
                        // with what `TrainingTrackerTickRare` expects to compare against.
                        animal.training.Debug_MakeDegradeHappenSoon();
                        var after = stamp?.GetValue(animal.training) as int? ?? 0;
                        trackerReport.Append($"，训练衰减 {animal.LabelShort} {before}→{after}");
                    }
                    else trackerReport.Append("，没有可训练动物");
                    Answer(id, true, "ok", trackerReport.ToString());
                }
                return;
            case "script-quest":
                // Generates a quest through the game's own entry point, so the parts list and its counters are the ones the
                // real quest system builds. Without a quest the quest-part channel has nothing to be read against.
                {
                    // `QuestScriptDef` has no `questType` and there is no `QuestType` enum in 1.7; the field that decides
                    // whether a script may be picked at random is `randomlySelectable` (`:356163`), weighted by
                    // `rootSelectionWeight` (`:356159`).
                    var script = string.IsNullOrWhiteSpace(argument)
                        ? DefDatabase<QuestScriptDef>.AllDefsListForReading
                            .Where(def => def.randomlySelectable && def.root is not null && def.rootSelectionWeight > 0f)
                            .RandomElementByWeightWithFallback(def => def.rootSelectionWeight, null)
                        : DefDatabase<QuestScriptDef>.GetNamedSilentFail(argument.Trim());
                    if (script is null) { Answer(id, false, "NotFound", "没有可用的任务脚本：" + argument); return; }
                    var quest = QuestUtility.GenerateQuestAndMakeAvailable(script, StorytellerUtility.DefaultThreatPointsNow(Find.World));
                    if (quest is null) { Answer(id, false, "GenerateFailed", "任务脚本没有产出任务：" + script.defName); return; }
                    QuestUtility.SendLetterQuestAvailable(quest);
                    var parts = quest.PartsListForReading;
                    var described = 0;
                    foreach (var part in parts)
                        if (part is not null && !part.DescriptionPart.NullOrEmpty()) described++;
                    Answer(id, true, "ok", $"已生成任务 {quest.id} {script.defName}"
                        + $"（{parts.Count} 个部件，其中 {described} 个有描述）"
                        + $"，状态 {quest.State}，隐藏 {quest.hidden}");
                }
                return;
            case "script-lord":
                // Advances nothing itself; it reports the lords and their counters so a reader can tell a frozen counter
                // from a world with no lords. The interesting number is `ticksInToil`, which only `LordTick` moves.
                {
                    var lordReport = new System.Text.StringBuilder();
                    var total = 0;
                    foreach (var lordMap in Find.Maps)
                    {
                        var lords = lordMap.lordManager?.lords;
                        if (lords is null || lords.Count == 0) continue;
                        foreach (var lord in lords)
                        {
                            if (lord is null) continue;
                            total++;
                            lordReport.Append(lord.loadID).Append(':')
                                .Append(lord.LordJob?.GetType().Name ?? "?").Append(" 小人")
                                .Append(lord.ownedPawns?.Count ?? 0).Append(" 到点")
                                .Append(lord.CurLordToil?.GetType().Name ?? "?").Append(" ticksInToil=")
                                .Append(lord.ticksInToil);
                            if (lord.LordJob is LordJob_Ritual ritual)
                                lordReport.Append(" 仪式").Append(ritual.Progress.ToString("0.000"))
                                    .Append('(').Append(ritual.TicksLeft).Append('/')
                                    .Append(ritual.DurationTicks).Append(')');
                            lordReport.Append(" | ");
                        }
                    }
                    Answer(id, true, "ok", total == 0
                        ? "0 个领主（全图没有集会 / 仪式 / 围攻）"
                        : $"{total} 个领主：{lordReport}".TrimEnd(' ', '|'));
                }
                return;
            case "script-skyfaller":
                // Drops a real skyfaller so the descent slot can be observed. `Skyfaller`'s Position is its landing cell
                // and never moves, so without this probe the only evidence available is that the class exists.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(
                        string.IsNullOrWhiteSpace(argument) ? "DropPodIncoming" : argument.Trim());
                    if (def?.skyfaller is null)
                    {
                        Answer(id, false, "NotFound", "没有这个坠落物定义：" + argument);
                        return;
                    }
                    var center = map.Center;
                    var cell = CellFinder.RandomCell(map);
                    _ = center;
                    var faller = (Skyfaller)GenSpawn.Spawn(def, cell, map);
                    Answer(id, true, "ok", $"已在 {cell} 生成 {def.defName}"
                        + $"，槽 {HeadlessRuntime.FallSlot(faller)}"
                        + $"，剩余 {HarmonyLib.AccessTools.Field(typeof(Skyfaller), "ticksToImpact")?.GetValue(faller)} tick");
                }
                return;
            case "script-stance":
                // Puts a pawn into a real busy stance and staggers it, so the stance channel has something to carry. The
                // stance is a *vanilla* one (`Stance_Warmup`), not the replica's own class: the probe has to observe what
                // the authority actually produces, and installing our own class would make the reading circular.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    Pawn? shooter = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (candidate?.stances is not null && candidate.equipment?.Primary is not null)
                        { shooter = candidate; break; }
                    // Fall back to any colonist with a weapon, then to any colonist at all, and say which happened.
                    if (shooter is null)
                        foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                            if (candidate?.stances is not null && candidate.equipment?.Primary is not null)
                            { shooter = candidate; break; }
                    if (shooter is null)
                        foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                            if (candidate?.stances is not null) { shooter = candidate; break; }
                    if (shooter is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    var target = new IntVec3(shooter.Position.x + 6, 0, shooter.Position.z);
                    if (!target.InBounds(map)) target = new IntVec3(shooter.Position.x - 6, 0, shooter.Position.z);
                    var verb = shooter.equipment?.PrimaryEq?.PrimaryVerb;
                    // `Stance_Cooldown`, not `Stance_Warmup`. Warmup's own `StanceTick` dereferences `verb.Caster` via
                    // `effecter?.EffectTick(verb.Caster, ...)` - and `?.` on `effecter` does not protect the argument
                    // expression - so with an unarmed pawn it throws inside the tick and `base.StanceTick()` never runs:
                    // measured as a live stance stuck at 90 ticks while the stagger beside it counted down to zero, with
                    // `Exception ticking JB ... NullReferenceException` in the server log. Cooldown has no override at all
                    // (`:138675`), so the base countdown is exactly what this probe needs to observe.
                    var stance = new Stance_Cooldown(90, new LocalTargetInfo(target), verb);
                    // `SetStance`, not the field: it is what wires `newStance.stanceTracker`, and `Stance_Busy.StanceTick`
                    // dereferences that on its first line.
                    shooter.stances.SetStance(stance);
                    // A stagger as well, through the game's own entry point so the countdown and speed factor are the
                    // ones the authority would really produce.
                    shooter.stances.stagger.StaggerFor(60);
                    // The class name is read off the installed stance rather than written into the message: an earlier
                    // version of this line still said "Stance_Warmup" after the probe had been switched to Cooldown, which
                    // is exactly the kind of stale label that makes a report describe something other than what it ran.
                    Answer(id, true, "ok", $"已让 {shooter.LabelShort} #{shooter.thingIDNumber} 进入 "
                        + $"{shooter.stances.curStance.GetType().Name}"
                        + $"（剩 {stance.ticksLeft} tick，目标 {target}，是否有武器 {verb is not null}）"
                        + $"，失衡 {shooter.stances.stagger.StaggerTicksLeft} tick");
                }
                return;
            case "script-residual":
                // Lists every counter the residual channel found a carrier for on this save, then pushes one of them so the
                // channel has something to encode. Which ones exist is a fact about the colony, so they are printed
                // individually instead of as a total.
                {
                    var residual = new StringBuilder();
                    var before = ResidualCounterSync.Publish();
                    residual.Append("首次发布 ").Append(before?.Count ?? 0).Append(" 行；");
                    // Drug records are the one carrier every colony has, so they are the one this probe moves: the field is
                    // `lastTakenTicks` and `TakeDrug` writes the current tick into it.
                    var moved = 0;
                    foreach (var pawn in Find.CurrentMap?.mapPawns?.FreeColonistsSpawned ?? [])
                    {
                        if (moved >= 2) break;
                        var tracker = pawn?.drugs;
                        if (tracker is null) continue;
                        if (HarmonyLib.AccessTools.Field(typeof(Pawn_DrugPolicyTracker), "drugTakeRecords")?.GetValue(tracker)
                            is not System.Collections.IEnumerable records) continue;
                        foreach (var record in records)
                        {
                            if (record is null) continue;
                            var field = HarmonyLib.AccessTools.Field(record.GetType(), "lastTakenTicks");
                            var was = field?.GetValue(record) as int? ?? 0;
                            field?.SetValue(record, Find.TickManager.TicksGame);
                            residual.Append("药品记录 ").Append(was).Append("→").Append(Find.TickManager.TicksGame).Append("；");
                            moved++;
                            break;
                        }
                    }
                    var after = ResidualCounterSync.Publish();
                    residual.Append("改后发布 ").Append(after?.Count ?? 0).Append(" 行； ")
                        .Append(ResidualCounterSync.Summary());
                    Answer(id, true, "ok", residual.ToString());
                }
                return;

            case "script-mental-fit":
                // Sets the fit cooldown through the game's own notifier and reads the channel's payload back. Two calls tell
                // "the field travelled" from "it happens to be zero": the first raises it, the second reports the countdown
                // the authority now holds.
                {
                    var fitMap = Find.CurrentMap;
                    if (fitMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var fitReport = new StringBuilder();
                    var touched = 0;
                    foreach (var pawn in fitMap.mapPawns.FreeColonistsSpawned)
                    {
                        if (pawn?.mindState?.mentalFitGenerator is null) continue;
                        if (touched++ >= 3) break;
                        var before = HarmonyLib.AccessTools
                            .Field(typeof(MentalFitGenerator), "ticksUntilCanDoMentalFit")?.GetValue(pawn.mindState.mentalFitGenerator) as int? ?? -1;
                        // `Notify_RecoveredFromMentalState` is what the game itself calls at 600, so the probe uses it rather
                        // than writing the field: a field the notifier does not own would prove nothing about the notifier.
                        pawn.mindState.mentalFitGenerator.Notify_RecoveredFromMentalState();
                        var payload = MentalStateSync.PayloadFor(pawn);
                        fitReport.Append(pawn.LabelShort).Append('#').Append(pawn.thingIDNumber)
                            .Append(" 冷却 ").Append(before).Append("→600；载荷 ")
                            .Append(payload ?? "(无)").Append(';');
                    }
                    if (touched == 0) fitReport.Append("没有带 mentalFitGenerator 的殖民者");
                    Answer(id, true, "ok", fitReport.ToString());
                }
                return;

            case "script-passing-ship":
                // Counts a real orbiting ship down and prints what the channel would encode. Two calls: the first reports
                // the before/after, the second confirms the countdown moved on its own between them.
                {
                    var shipMap = Find.CurrentMap;
                    var ships = shipMap?.passingShipManager?.passingShips;
                    if (ships is null || ships.Count == 0)
                    { Answer(id, false, "NoShip", "本图没有过境飞船"); return; }
                    var text = new StringBuilder();
                    foreach (var ship in ships)
                    {
                        var shipId = HarmonyLib.AccessTools.Field(typeof(PassingShip), "loadID")?.GetValue(ship) as int? ?? -1;
                        var before = ship.ticksUntilDeparture;
                        // A real decrement: the field is what `PassingShipTick` writes, and 600 ticks is the channel's
                        // quantum — writing less than one quantum would legitimately produce no frame.
                        ship.ticksUntilDeparture = Math.Max(0, before - 600);
                        var published = PassingShipSync.Publish();
                        text.Append("船 #").Append(shipId).Append('（').Append(ship.name ?? "?").Append("）剩余 ")
                            .Append(before).Append('→').Append(ship.ticksUntilDeparture)
                            .Append("；通道本次发布 ").Append(published?.Count ?? 0).Append(" 行；");
                        ship.ticksUntilDeparture = before;
                    }
                    PassingShipSync.Publish();
                    text.Append(Environment.NewLine).Append(PassingShipSync.Summary());
                    Answer(id, true, "ok", text.ToString());
                }
                return;

            case "settlement-retire":
                // Disbands one settlement. The four-part sweep (flag, `PawnHome`, `Grants`/`Defaults`, overlay) lives in
                // `ScriptedSettlement.Retire` since round 141, because the acceptance scenario needs the same sequence and
                // a second copy is how such a set drifts.
                {
                    var retired = ScriptedSettlement.Retire(argument);
                    Answer(id, retired.Ok, retired.Code, retired.Detail);
                }
                return;

            case "pawnhome-probe":
                // Makes a pawn that exists nowhere and checks that the membership table stops attributing it.
                //
                // The table has no removal path: `ScriptedSettlement.Place` writes an entry when a settlement is
                // founded and nothing ever takes one out, so a colonist that died or was carried off keeps its
                // membership for the rest of the save and `settlement-report`'s member count only ever grows. The
                // repair is a reconcile inside `SettlementSync.Publish`; this makes the input that reconcile exists for.
                //
                // The pawn is destroyed rather than killed on purpose: a corpse is still a pawn in `WorldPawns`, so a
                // kill would leave the entry legitimately alive and the probe would prove nothing. Destruction is also
                // what actually removes a pawn from every collection - and it cannot be undone, so the pawn chosen is
                // one the report names before it is destroyed.
                {
                    var data = SettlementGates.Data;
                    if (data is null || !data.Active)
                    { Answer(id, false, "NoSettlement", "据点层未启用：先 settlement-report 看，或跑 script-settlement"); return; }
                    if (data.PawnHome.Count == 0)
                    { Answer(id, false, "NoMembers", "没有小人归属记录：先建立据点（会认领矩形内的小人）"); return; }
                    var text = new StringBuilder();
                    var before = data.PawnHome.Count;
                    // Exactly one entry, picked deterministically so a rerun destroys the next one and the reading is
                    // repeatable: the highest pawn id, which is never the settlement's own founder on a fresh split.
                    var victim = -1;
                    foreach (var key in data.PawnHome.Keys) if (key > victim) victim = key;
                    Pawn? target = null;
                    foreach (var map in Find.Maps)
                        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                            if (pawn is not null && pawn.thingIDNumber == victim) target = pawn;
                    text.Append("归属记录 ").Append(before).Append(" 条，选中 #").Append(victim)
                        .Append(target is null ? "（不在任何地图上，无法销毁）" : "（" + (target.Name?.ToStringShort ?? "?") + "）");
                    if (target is null)
                    {
                        // Still useful: publish and report whether the reconcile already considers it gone.
                        var wire = SettlementSync.Publish();
                        text.Append("；发布后归属 ").Append(data.PawnHome.Count).Append(" 条");
                        Answer(id, true, "ok", text.ToString());
                        return;
                    }
                    try
                    {
                        target.Destroy(DestroyMode.Vanish);
                        text.Append("；已销毁");
                    }
                    catch (Exception error)
                    {
                        Answer(id, false, "DestroyFailed", error.GetType().Name + ": " + error.Message);
                        return;
                    }
                    // Publish is where the reconcile lives. A replica would see this through the frame; here the
                    // point is whether the table shrank.
                    var afterWire = SettlementSync.Publish();
                    var after = data.PawnHome.Count;
                    text.Append("；发布后归属 ").Append(after).Append(" 条（")
                        .Append(before == after ? "**没变，对账没生效**" : "少了 " + (before - after).ToString(CultureInfo.InvariantCulture) + " 条")
                        .Append("）")
                        .Append("；线上小人归属 ").Append((afterWire?.PawnHomes?.Count ?? 0) / 2).Append(" 条");
                    // And HomeOfPawn must now answer 0, which is what every gate reads.
                    text.Append("；HomeOfPawn(#").Append(victim).Append(")=").Append(data.HomeOfPawn(victim));
                    Answer(id, true, "ok", text.ToString());
                }
                return;

            case "worldobject-lifecycle":
                // Creates a real world object, publishes, destroys it, publishes again. Reports whether the id produced
                // a creation row and the destruction produced a removal row - the two facts that a silently empty list
                // cannot distinguish from "nothing happened".
                { Answer(id, true, "ok", WorldObjectLifecycleProbe.Report()); }
                return;

            case "passing-ship-probe":
                // Round-trips the real encoder through the real applier, per map. The channel used to encode every
                // map's ships into one flat list while applying only onto `Find.CurrentMap`, so a ship on a map nobody
                // was looking at kept its loaded countdown forever and never departed.
                { Answer(id, true, "ok", PassingShipProbe.Report()); }
                return;

            case "script-game-ender":
                // Starts and stops the death countdown through the fields the game itself writes, and prints what the
                // channel would encode. Restored afterwards: a probe must not end a colony.
                {
                    var ender = Find.GameEnder;
                    if (ender is null) { Answer(id, false, "NoGame", "还没有载入存档"); return; }
                    var endingField = HarmonyLib.AccessTools.Field(typeof(GameEnder), "gameEnding");
                    var ticksField = HarmonyLib.AccessTools.Field(typeof(GameEnder), "ticksToGameOver");
                    var beforeEnding = endingField?.GetValue(ender) as bool? ?? false;
                    var beforeTicks = ticksField?.GetValue(ender) as int? ?? -1;
                    endingField?.SetValue(ender, true);
                    ticksField?.SetValue(ender, 137);
                    var published = GameEnderSync.Publish();
                    var text = "终局 " + (beforeEnding ? "结束中" : "未结束") + "/" + beforeTicks
                        + " → 结束中/137；通道本次发布 " + (published?.Count ?? 0) + " 行"
                        + "； " + GameEnderSync.Summary();
                    endingField?.SetValue(ender, beforeEnding);
                    ticksField?.SetValue(ender, beforeTicks);
                    GameEnderSync.Publish();
                    Answer(id, true, "ok", text);
                }
                break;

            case "script-lord-lost":
                // Exercises the one lord counter that no other channel carries: `LordJob_HateChant.lostPawns`, the trigger
                // for the chant becoming an assault (`:319355`). Reported through the channel's own encoder so the number
                // in the report is the number that would go on the wire.
                {
                    var lordMap = Find.CurrentMap;
                    if (lordMap?.lordManager?.lords is not { Count: > 0 } lords)
                    { Answer(id, false, "NoLord", "没有领主"); return; }
                    var lordReport = new System.Text.StringBuilder();
                    var found = 0;
                    foreach (var lord in lords)
                    {
                        if (lord?.LordJob is not LordJob_HateChant) continue;
                        found++;
                        var field = HarmonyLib.AccessTools.Field(typeof(LordJob_HateChant), "lostPawns");
                        var before = field?.GetValue(lord.LordJob) as int? ?? -1;
                        field?.SetValue(lord.LordJob, before + 1);
                        var publish = LordSync.Publish();
                        var sample = publish?.FirstOrDefault(row => row.Lord == lord.loadID)?.Values
                            ?? LordSync.LastSample?.Values;
                        lordReport.Append("lord ").Append(lord.loadID).Append(" 失败计数 ")
                            .Append(before).Append("→").Append(before + 1)
                            .Append("；通道 ").Append(sample ?? "(本次未变)").Append(';');
                        // Put it back so the measurement does not advance a real chant.
                        field?.SetValue(lord.LordJob, before);
                        LordSync.Publish();
                    }
                    if (found == 0)
                    {
                        lordReport.Append("本图没有 HateChant 领主（").Append(lords.Count).Append(" 个领主：");
                        lordReport.Append(string.Join(",", lords.Select(l => l?.LordJob?.GetType().Name ?? "?")));
                        lordReport.Append("）");
                    }
                    Answer(id, true, "ok", lordReport.ToString());
                }
                break;

            case "pawn-payload-size":
                // Measures what `AuthoritativeFrame.PawnState` would cost, before deciding whether to wire a channel the
                // frame has declared since the first version and passed as null ever since.
                //
                // The decision this answers: a pawn's whole Scribe fragment carries needs, hediffs, abilities and every
                // third-party comp without per-field handling - which sounds like it should replace four named channels.
                // It is only worth it if the fragment is small enough to send for a change that moves every tick.
                {
                    var sizeReport = new System.Text.StringBuilder();
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    long total = 0;
                    var count = 0;
                    var largest = 0;
                    string? largestWho = null;
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn is null) continue;
                        var fragment = Save(pawn);
                        if (fragment is null) continue;
                        total += fragment.Length;
                        count++;
                        if (fragment.Length > largest) { largest = fragment.Length; largestWho = pawn.LabelShort + "#" + pawn.thingIDNumber; }
                    }
                    sizeReport.Append("本图 ").Append(count).Append(" 个小人，整份片段合计 ").Append(total).Append(" B")
                        .Append("，平均 ").Append(count == 0 ? 0 : total / count).Append(" B")
                        .Append("，最大 ").Append(largest).Append(" B（").Append(largestWho ?? "无").Append("）");
                    // The comparison that decides it: a position frame costs about forty bytes per pawn, so a whole
                    // fragment means this channel can only be sent when something *changes*, never per tick.
                    if (count > 0)
                        sizeReport.Append(Environment.NewLine)
                            .Append("对比：每 tick 的位置帧约 ").Append(count * 40).Append(" B，整份片段是它的 ")
                            .Append((double)total / (count * 40)).Append(" 倍");
                    Answer(id, true, "ok", sizeReport.ToString());
                }
                return;

            case "gap-audit":
                // Answers what is left of the gap list from the running world instead of from the assembly listing.
                //
                // <para>The listing cannot tell "this field is not scribed" from "this field is scribed by a carrier no
                // sweep visits" - and after four rounds that distinction is what every remaining entry turns on. This
                // saves each live object's own Scribe fragment and greps it for the tag, so the answer is a fact about a
                // payload rather than a claim about code. `Scribe_Values.Look(ref x, "tag")` writes `<tag>`, so a hit
                // means the value is inside a fragment some channel already carries.</para>
                Answer(id, true, "ok", GapAudit());
                return;

            case "script-adaptation":
                // Moves the counters through the game's own debug entry point and prints what the channel would encode.
                // `Debug_OffsetAdaptDays` adds, so this reports the delta it asked for rather than assuming a target.
                {
                    var watcher = Find.StoryWatcher;
                    if (watcher?.watcherAdaptation is null) { Answer(id, false, "NoWatcher", "没有剧情观察器"); return; }
                    var beforeAdapt = watcher.watcherAdaptation.AdaptDays;
                    var beforePop = watcher.watcherPopAdaptation.AdaptDays;
                    watcher.watcherAdaptation.Debug_OffsetAdaptDays(2f);
                    watcher.watcherPopAdaptation.Debug_OffsetAdaptDays(1f);
                    var publish = AdaptationSync.Publish();
                    Answer(id, true, "ok",
                        $"适应 {beforeAdapt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}→"
                        + $"{watcher.watcherAdaptation.AdaptDays.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}，"
                        + $"人口适应 {beforePop.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}→"
                        + $"{watcher.watcherPopAdaptation.AdaptDays.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}"
                        + $"；通道本次发布 {publish?.Count ?? 0} 行"
                        + $"； ThreatenedPointsFactor {watcher.watcherAdaptation.TotalThreatPointsFactor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
                        + $"； {AdaptationSync.Grace}");
                }
                break;

            case "script-ability":
                // Grants a charged ability and spends a charge through the game's own path, then prints what the channel
                // would encode. Reading the ability back afterwards proves nothing: the value the authority has is the value
                // the probe just caused, whether or not the encoding carries it.
                {
                    var abilityMap = Find.CurrentMap;
                    if (abilityMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    Pawn? caster = null;
                    foreach (var candidate in abilityMap.mapPawns.FreeColonistsSpawned)
                        if (candidate?.abilities is not null && !candidate.Dead) { caster = candidate; break; }
                    if (caster is null) { Answer(id, false, "NoPawn", "没有殖民者"); return; }
                    var abilityReport = new System.Text.StringBuilder();
                    // A charged ability has to come from a def that declares `<charges>`; the psylink abilities most pawns
                    // have are free to cast, and an ability with `UsesCharges == false` sends nothing (`Encode` skips it),
                    // which would read as "the channel does not work". `FoamSpray` (Biotech) is the shortest such def.
                    Ability? target = null;
                    foreach (var wanted in new[] { "FoamSpray", "AnimalWarcall", "LongjumpMechLauncher" })
                    {
                        var ad = DefDatabase<AbilityDef>.GetNamedSilentFail(wanted);
                        if (ad is null) { abilityReport.Append(wanted).Append(":无 def;"); continue; }
                        try
                        {
                            caster.abilities.GainAbility(ad);
                            foreach (var candidate in caster.abilities.AllAbilitiesForReading)
                                if (candidate.def == ad) { target = candidate; break; }
                        }
                        catch (Exception e) { abilityReport.Append(wanted).Append(":授予失败 ").Append(e.GetType().Name).Append(';'); }
                        if (target is not null) { abilityReport.Append("授予 ").Append(ad.defName).Append(';'); break; }
                    }
                    if (target is null)
                    {
                        // Fall back to whatever the pawn already has, so the row still says something about a save with no
                        // Biotech - but say so, because a free ability proves nothing about charges.
                        foreach (var candidate in caster.abilities.AllAbilitiesForReading)
                            if (candidate.def?.verbProperties is not null) { target = candidate; break; }
                        abilityReport.Append("无带次数能力，退回免费能力;");
                    }
                    if (target is null) { Answer(id, true, "ok", abilityReport + "无可施放能力"); return; }
                    var before = target.RemainingCharges;
                    // `Activate`, not `StartCooldown`: only `PreActivate` consumes a charge (`:432925`), and it is reached
                    // from `Activate` (`:432979`). `StartCooldown` alone moves the cooldown and leaves `charges` where it
                    // was - measured as 次数 3→3, which read like a broken channel and was a wrong probe.
                    try { target.Activate(new LocalTargetInfo(caster.Position), LocalTargetInfo.Invalid); }
                    catch (Exception e) { abilityReport.Append("Activate 失败 ").Append(e.GetType().Name).Append(';'); }
                    abilityReport.Append(caster.LabelShort).Append(" #").Append(caster.thingIDNumber)
                        .Append(" 目标 ").Append(target.def.defName)
                        .Append(" 次数 ").Append(before).Append("→").Append(target.RemainingCharges)
                        .Append(" 上限 ").Append(target.UsesCharges ? "有" : "无")
                        .Append(" 冷却 ").Append(target.CooldownTicksRemaining).Append('/').Append(target.CooldownTicksTotal);
                    abilityReport.Append("\n通道载荷：").Append(AbilitySync.EncodeFor(caster) ?? "(空)");
                    Answer(id, true, "ok", abilityReport.ToString());
                }
                break;

            case "script-ethereal":
                // Spawns the two Ethereal things that carry a counter, so the new carrier can be watched moving.
                // `Tornado` is the interesting one: its `Tick` advances a Perlin `direction`, moves `realPosition` and writes
                // the result back into `Position` (`:594300`), so on a replica that never ticks it stands still and keeps its
                // spawn heading forever - the funnel is a static drawing of a tornado.
                {
                    var etherealMap = Find.CurrentMap;
                    if (etherealMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var cell = etherealMap.Center;
                    var etherealReport = new System.Text.StringBuilder();
                    foreach (var wanted in new[] { "Tornado", "Bombardment" })
                    {
                        var td = DefDatabase<ThingDef>.GetNamedSilentFail(wanted);
                        if (td?.thingClass is null) { etherealReport.Append(wanted).Append(":无 def;"); continue; }
                        try
                        {
                            var made = GenSpawn.Spawn(td, cell, etherealMap);
                            etherealReport.Append(wanted).Append('(').Append(made.GetType().Name).Append(")@")
                                .Append(made.Position.x).Append(',').Append(made.Position.z);
                            if (made is IExposable ex && ScribeBridge.Save(ex) is { Length: > 0 } wire)
                                etherealReport.Append(" 片段 ").Append(wire.Length).Append(" B");
                            etherealReport.Append(';');
                        }
                        catch (Exception e) { etherealReport.Append(wanted).Append(":造不出 ").Append(e.GetType().Name).Append(';'); }
                    }
                    Answer(id, true, "ok", $"在 {cell.x},{cell.z} 造了 Ethereal 物体：" + etherealReport);
                }
                break;

            case "script-hediff-derived":
                // Reads the fourth hediff field for both qualifying types. Both values are rolled in `PostAdd` and consumed
                // only inside `TickInterval`, so a replica's rebuild gives them a *different* number rather than none -
                // which is why the payload has to be printed rather than counted.
                {
                    var derivedMap = Find.CurrentMap;
                    if (derivedMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    Pawn? target = null;
                    foreach (var candidate in derivedMap.mapPawns.FreeColonistsSpawned)
                        if (candidate?.health?.hediffSet is not null && !candidate.Dead) { target = candidate; break; }
                    if (target is null) { Answer(id, false, "NoPawn", "没有殖民者"); return; }
                    var derivedReport = new System.Text.StringBuilder();
                    foreach (var wanted in new[] { "BloodRage", "MetalhorrorImplant" })
                    {
                        var hdDef = DefDatabase<HediffDef>.GetNamedSilentFail(wanted);
                        if (hdDef?.hediffClass is null)
                        {
                            // Located by class when the defName differs, the same way the condition probe does it: a wrong
                            // name reads exactly like "the DLC that defines this is not active".
                            foreach (var candidate in DefDatabase<HediffDef>.AllDefs)
                                if (candidate?.hediffClass?.Name == "Hediff_" + wanted) { hdDef = candidate; break; }
                        }
                        if (hdDef?.hediffClass is null) { derivedReport.Append(wanted).Append(":无 def;"); continue; }
                        Hediff made;
                        try { made = HediffMaker.MakeHediff(hdDef, target); }
                        catch (Exception e) { derivedReport.Append(wanted).Append(":造不出 ").Append(e.GetType().Name).Append(';'); continue; }
                        target.health.AddHediff(made);
                        derivedReport.Append(hdDef.defName).Append("(").Append(made.GetType().Name).Append(")=")
                            .Append(HeadlessRuntime.HediffDerivedFor(made)).Append(';');
                    }
                    // The set payload, not the frame: `Hediffs` only carries a pawn when it changed, so a round-trip taken a
                    // few hundred ticks later shows an empty channel and proves nothing either way.
                    var setWire = HeadlessRuntime.HediffSetFor(target) ?? "(空)";
                    Answer(id, true, "ok", $"对 {target.LabelShort} #{target.thingIDNumber} 造了两种 hediff，通道派生值："
                        + derivedReport + "\n集合载荷：" + setWire);
                }
                break;

            case "script-mental-subclass":
                // Starts a mental break whose subclass keeps its own counter, so the payload this round added has something
                // to carry. The colony is calm on this save - measured `0 个精神异常` - so without this the channel reads
                // identical whether the subclass field travels or not.
                {
                    var mentalMap = Find.CurrentMap;
                    if (mentalMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    // Chosen by probing the def's own class rather than by name: which subclass a def uses is a def detail,
                    // and `MentalState_HumanityBreak` / `_InhumanRambling` only exist when Anomaly is active.
                    var candidates = new List<string>();
                    MentalStateDef? target = null;
                    foreach (var def in DefDatabase<MentalStateDef>.AllDefs)
                    {
                        var className = def?.stateClass?.Name;
                        if (className is not ("MentalState_HumanityBreak" or "MentalState_InhumanRambling"
                            or "MentalState_SadisticRageTantrum" or "MentalState_SlaughterThing")) continue;
                        candidates.Add(def.defName + "=" + className);
                        // Prefer the one whose counter is a *countdown*: `ticksToRamble` is non-zero at start, so the payload
                        // proves the field carried a value rather than merely a zero. Measured first version: the preference
                        // below was written as `target ??= def` on the same line as the check, which locked onto the first
                        // candidate (`SadisticRage`, `hits=0`) and left the meaningful case unreachable.
                        if (target is null || def.stateClass == typeof(MentalState_HumanityBreak)) target = def;
                        if (def.stateClass == typeof(MentalState_HumanityBreak)) break;
                    }
                    if (target is null)
                    {
                        Answer(id, false, "NoDef", "没有带子类计数器的精神异常 def，候选：" + string.Join(",", candidates));
                        return;
                    }
                    Pawn? subject = null;
                    foreach (var pawn in mentalMap.mapPawns.FreeColonistsSpawned)
                        if (pawn?.mindState?.mentalStateHandler is not null && !pawn.Dead)
                        { subject = pawn; break; }
                    if (subject is null) { Answer(id, false, "NoPawn", "没有殖民者"); return; }
                    var started = subject.mindState.mentalStateHandler.TryStartMentalState(
                        target, "TurboTickProbe", forced: true, forceWake: true, transitionSilently: true);
                    var handlerNow = subject.mindState.mentalStateHandler;
                    var current = handlerNow.CurState;
                    Answer(id, true, "ok", $"对 {subject.LabelShort} #{subject.thingIDNumber} 起 {target.defName}"
                        + $"（{target.stateClass?.Name}）→ {started}；当前 "
                        + (current is null ? "(无状态)" : current.GetType().Name + "#" + current.Age)
                        + "；通道载荷 " + (MentalStateSync.PayloadFor(subject) ?? "(空)")
                        + "；候选 " + string.Join(",", candidates));
                }
                break;

            case "script-condition-derived":
                // Starts an aurora and a volcanic shower and prints what the condition channel encodes for each. The three
                // aurora inputs are picked with `Rand.Range` in `Init` and only move inside `GameConditionTick`, so a replica
                // that merely re-registers the condition rolls a different aurora and tints the map the wrong colour.
                {
                    var condMap = Find.CurrentMap;
                    if (condMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var madeReport = new System.Text.StringBuilder();
                    foreach (var wanted in new[] { "Aurora", "VolcanicDebrisShower" })
                    {
                        // Located by class rather than by name: the defName of a DLC condition is not derivable from its type
                        // name (`Aurora` happens to match, `VolcanicDebrisShower` does not), and a wrong name reads exactly
                        // like "this DLC is not active".
                        GameConditionDef? condDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(wanted);
                        var condById = condDef is null;
                        if (condDef is null)
                            foreach (var candidate in DefDatabase<GameConditionDef>.AllDefs)
                                if (candidate?.conditionClass?.Name == "GameCondition_" + wanted) { condDef = candidate; break; }
                        if (condDef is null) { madeReport.Append(wanted).Append(":无 def;"); continue; }
                        GameCondition made;
                        try { made = GameConditionMaker.MakeCondition(condDef, 60000); }
                        catch (Exception e) { madeReport.Append(wanted).Append(":造不出 ").Append(e.GetType().Name).Append(';'); continue; }
                        condMap.gameConditionManager.RegisterCondition(made);
                        // RegisterCondition runs `Init()`, which is exactly what sets the random colour indices - so the
                        // reading below is the same state a loaded save would carry, not a hand-written one.
                        madeReport.Append(condById ? condDef.defName + "(按类找到)" : wanted)
                            .Append('#').Append(made.uniqueID).Append('=');
                        madeReport.Append(GameConditionSync.DerivedFor(made) ?? "(空)").Append(';');
                    }
                    Answer(id, true, "ok", "已起条件并读编码：" + madeReport);
                }
                break;

            case "script-worldcomp":
                // What a world object's own Scribe fragment actually contains. `WorldObject.ExposeData` (`:798523`) writes its
                // fields and then calls `comps[i].PostExposeData()` for each comp inline - there is no `<comps>` wrapper - so
                // the shape has to be measured before any filter can be written against it.
                {
                    var worldMap = Find.CurrentMap;
                    if (worldMap is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    WorldObject? found = null;
                    WorldObjectComp? foundComp = null;
                    var scanned = 0;
                    foreach (var each in Find.WorldObjects.AllWorldObjects)
                    {
                        if (each is null) continue;
                        scanned++;
                        var comps = each.AllComps;
                        if (comps is null) continue;
                        foreach (var comp in comps)
                            if (comp is TimedDetectionRaids) { found = each; foundComp = comp; break; }
                        if (found is not null) break;
                    }
                    if (found is null || foundComp is not TimedDetectionRaids raids)
                    {
                        Answer(id, true, "ok", $"扫了 {scanned} 个世界物件，没有带 TimedDetectionRaids 的");
                        return;
                    }
                    // Force both countdowns so the fragment carries recognisable numbers rather than whatever the save had.
                    // `StartDetectionCountdown(ticks, notifyTicks)`, not a setter: the countdown pair is private and only
                    // this method and `ResetCountdown` write it. Note the scribe key for `ticksLeftToSendRaid` is
                    // "ticksLeftToForceExitAndRemoveMap" (`:783488`) - a copy-paste in the game, but it is the key that will
                    // appear in the fragment.
                    raids.StartDetectionCountdown(7200, 3600);
                    var xml = ScribeBridge.Save(found) ?? "(保存返回空)";
                    // What the channel itself would send, not what Scribe produces. The strip list removes def/tile/ID/
                    // tickDelta/faction/questTags and must not touch a comp field - and this save has no world object whose
                    // comp fields are non-default (`Settlement #0` has no map, so `CompTickInterval`'s else branch calls
                    // `ResetCountdown()` and `Scribe_Values.Look(..., -1)` then omits the element), so without forcing the
                    // countdown here there is nothing to check the strip against.
                    var channelled = WorldObjectStateSync.FragmentFor(found) ?? "(空)";
                    var carried = channelled.Contains("ticksLeftToForceExitAndRemoveMap", StringComparison.Ordinal);
                    Answer(id, true, "ok", $"世界物件 {found.def?.defName} #{found.ID}（扫 {scanned} 个找到）"
                        + $" 片段 {xml.Length} B，通道 {channelled.Length} B，含倒计时 {carried}：\n"
                        + channelled.Replace("\r", "").Replace("\n", ""));
                }
                break;

            case "script-crop":
                // Plants a growing zone with a real crop, because the plant sweep skips volunteers and wild flora by design and
                // this colony has neither. Without a seeded zone "扫 0 建 0" cannot be told apart from "no crop ever changes".
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var plantDef = DefDatabase<ThingDef>.GetNamedSilentFail(
                        string.IsNullOrWhiteSpace(argument) ? "Plant_Potato" : argument.Trim());
                    if (plantDef?.plant is null) { Answer(id, false, "NoDef", "没有这个植物：" + argument); return; }
                    // Same construction the scripted-zone path uses: `RegisterZone` is the public door (`AddZone` and
                    // `AddCell` on the manager/grid are internal), and it is what assigns the id and dirties the section.
                    var centre = map.Center;
                    var zone = new Zone_Growing(map.zoneManager);
                    zone.SetPlantDefToGrow(plantDef);
                    map.zoneManager.RegisterZone(zone);
                    zone.label = "scripted-crop";
                    var added = 0;
                    var rect = CellRect.CenteredOn(centre, 5, 5).ClipInsideMap(map);
                    foreach (var c in rect)
                    {
                        if (zone.ContainsCell(c)) continue;
                        zone.AddCell(c);
                        added++;
                    }
                    if (added == 0) { Answer(id, false, "NoCell", "地图中心 5x5 一格都放不下"); return; }
                    // Spawn the plants separately: a growing zone does not plant anything by itself, only a colonist's
                    // `JobDriver_PlantSow` does, and that needs an unpaused tick.
                    var sown = 0;
                    foreach (var c in zone.Cells)
                    {
                        if (!c.InBounds(map)) continue;
                        if (c.GetPlant(map) is not null) continue;
                        if (plantDef.plant.interferesWithRoof && c.Roofed(map)) continue;

                        if (ThingMaker.MakeThing(plantDef) is not Plant plant) continue;
                        try
                        {
                            GenSpawn.Spawn(plant, c, map);
                            sown++;
                        }
                        catch (Exception) { }
                    }
                    var seeded = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
                        .Count(p => p is Plant { sown: true });
                    Answer(id, true, "ok", $"已在 ({centre.x}, 0, {centre.z}) 建 {zone.CellCount} 格种植区种 "
                        + $"{plantDef.defName}（尝试 {added} 格），种下 {sown} 株；全图已播种 {seeded} 株");
                }
                break;

            case "script-bill":
                // Builds a worktable and puts a real bill on it, whose three `Bill_Production` settings the gap list
                // flags. The three are inside `Bill_Production.ExposeData` and the table scribes the whole stack with
                // `Scribe_Deep.Look(ref billStack, ...)`, so the expectation is that they already travel inside the
                // building sweep's payload - this probe is what turns that expectation into a reading.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var tableDef = DefDatabase<ThingDef>.GetNamedSilentFail("TableStonecutter")
                        ?? DefDatabase<ThingDef>.GetNamedSilentFail("TableMachining");
                    if (tableDef is null) { Answer(id, false, "NotFound", "没有可用的工作台定义"); return; }
                    var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("Make_StoneBlocks")
                        ?? DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(r => r.ProducedThingDef is not null);
                    if (recipe is null) { Answer(id, false, "NoRecipe", "没有可用的配方"); return; }
                    Pawn? builder = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (candidate?.Position.IsValid == true) { builder = candidate; break; }
                    if (builder is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    var cell = CellFinder.StandableCellNear(builder.Position, map, 6f);
                    if (!cell.IsValid) cell = builder.Position;
                    var table = (Building_WorkTable)GenSpawn.Spawn(tableDef, cell, map);
                    table.SetFaction(RimWorld.Faction.OfPlayer);
                    var bill = new Bill_Production(recipe);
                    // Non-default values on all three, so a payload that carried the def's defaults would be
                    // distinguishable from one that carried these.
                    bill.repeatMode = BillRepeatModeDefOf.TargetCount;
                    bill.repeatCount = 7;
                    bill.targetCount = 42;
                    bill.pauseWhenSatisfied = true;
                    bill.unpauseWhenYouHave = 11;
                    table.billStack.AddBill(bill);
                    var xml = ScribeBridge.Save(table) ?? "";
                    var hasRepeat = xml.Contains("repeatCount", StringComparison.Ordinal);
                    var hasTarget = xml.Contains("targetCount", StringComparison.Ordinal);
                    var hasUnpause = xml.Contains("unpauseWhenYouHave", StringComparison.Ordinal);
                    Answer(id, true, "ok", $"已在 {cell} 建 {tableDef.defName} 并加 1 条 {recipe.defName} 配方"
                        + $"（次数 {bill.repeatCount}，目标 {bill.targetCount}，恢复阈值 {bill.unpauseWhenYouHave}），"
                        + $"载荷 {xml.Length} B，repeatCount {hasRepeat}，targetCount {hasTarget}，"
                        + $"unpauseWhenYouHave {hasUnpause}");
                }
                return;
            case "script-projectile":
                // Puts a real projectile in the air so the change-detection slot can be observed moving. `Launch` is the
                // game's own entry point - it is what `Verb_LaunchProjectileStatic` calls - so the object, its origin,
                // its destination and its `ticksToImpact` are all produced by vanilla code.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var projectileDef = DefDatabase<ThingDef>.GetNamedSilentFail(
                        string.IsNullOrWhiteSpace(argument) ? "Bullet_Revolver" : argument.Trim());
                    if (projectileDef?.projectile is null)
                    {
                        Answer(id, false, "NotFound", "没有这个投射物定义：" + argument);
                        return;
                    }
                    Pawn? shooter = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (candidate?.pather is not null) { shooter = candidate; break; }
                    if (shooter is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    var from = shooter.Position;
                    var to = from + new IntVec3(12, 0, 0);
                    if (!to.InBounds(map)) to = from + new IntVec3(-12, 0, 0);
                    var projectile = (Projectile)GenSpawn.Spawn(projectileDef, from, map);
                    projectile.Launch(shooter, from.ToVector3Shifted(), to, to, ProjectileHitFlags.None);
                    Answer(id, true, "ok", $"已在 {from} 发射 {projectileDef.defName} 飞向 {to}"
                        + $"，槽 {HeadlessRuntime.FlightSlot(projectile)}");
                }
                return;
            case "script-deathrest":
                // Gives a colonist the Deathrest gene with real progress so the channel can be observed. Without it a
                // colony with no sanguophage reads "0 carriers", which is indistinguishable from a broken channel - the
                // same unreadable zero every other probe in this file exists to avoid.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var geneDef = DefDatabase<GeneDef>.GetNamedSilentFail("Deathrest");
                    if (geneDef is null) { Answer(id, false, "NotFound", "没有 Deathrest 基因（缺 Biotech）"); return; }
                    Pawn? patient = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (candidate?.genes is not null && !candidate.Dead) { patient = candidate; break; }
                    if (patient is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    var gene = patient.genes.GetFirstGeneOfType<Gene_Deathrest>()
                        ?? patient.genes.AddGene(geneDef, xenogene: true) as Gene_Deathrest;
                    if (gene is null) { Answer(id, false, "NoGene", "植入基因失败"); return; }
                    // The field `TickDeathresting` writes, set directly so the census has a non-zero reading to show. It
                    // does NOT advance on its own from here: `Hediff_Deathrest.PostTickInterval` is the only caller, so
                    // progress needs the pawn to have the deathrest hediff and actually be resting. Measured: 32000
                    // ticks of `advance-ticks` left this value at exactly 24000, which is the honest reading for a pawn
                    // standing up.
                    gene.deathrestTicks = 24000;
                    Answer(id, true, "ok", $"已让 {patient.LabelShort} #{patient.thingIDNumber} 带死眠基因"
                        + $"，进度 {gene.DeathrestPercent.ToStringPercent("F0")}"
                        + $"（{gene.deathrestTicks}/{gene.MinDeathrestTicks}），上限 {gene.DeathrestCapacity}");
                }
                return;
            case "script-hediff-timers":
                // Installs real conditions carrying the six countdown comps and the charge comp, so the `伤势计时` row can
                // show one of each. A colony usually has none of them - they live on DLC hediffs and rare implants - so
                // "0 条带时限" is the normal reading and says nothing about whether the channel works. The comps are attached
                // to one existing def and then handed to the game: `HediffMaker.MakeHediff` plus `AddHediff` run the normal
                // `CompPostPostAdd`, which is what actually initialises each counter. A counter set by hand would prove
                // nothing about it travelling.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    Pawn? patient = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (!candidate.Dead && candidate.health?.hediffSet is not null)
                        {
                            patient = candidate;
                            break;
                        }
                    if (patient is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    var baseDef = DefDatabase<HediffDef>.GetNamedSilentFail("Cut");
                    if (baseDef?.comps is null) { Answer(id, false, "NoDef", "没有 Cut 这个 hediff def"); return; }

                    // `compClass` is filled in by the XML loader, not by the properties type, so a hand-built props object
                    // leaves it null and `HediffWithComps` then dereferences it. Set it on every entry.
                    // The comps go onto the shared `Cut` def rather than six new defs: `DefDatabase<T>.Add` needs fields
                    // the XML loader normally fills (`ResolveDefNameHash`, stage lists) and throws without them, while the
                    // def-prefix is not needed for uniqueness - the channel's key is `defName#partDefName#severity`, and
                    // six distinct body parts already make six distinct keys.
                    if (baseDef.comps is List<HediffCompProperties> compList
                        && !compList.Any(c => c?.compClass == typeof(HediffComp_ChanceToRemove)))
                    {
                        compList.Add(new HediffCompProperties_ChanceToRemove
                            { compClass = typeof(HediffComp_ChanceToRemove), intervalTicks = 6000, chance = 1f });
                        compList.Add(new HediffCompProperties_HealPermanentWounds
                            { compClass = typeof(HediffComp_HealPermanentWounds) });
                        compList.Add(new HediffCompProperties_Infecter
                            { compClass = typeof(HediffComp_Infecter) });
                        compList.Add(new HediffCompProperties_MessageAfterTicks
                            { compClass = typeof(HediffComp_MessageAfterTicks), ticks = 5000 });
                        compList.Add(new HediffCompProperties_SelfHeal
                            { compClass = typeof(HediffComp_SelfHeal), healIntervalTicksStanding = 50, healAmount = 1f });
                        compList.Add(new HediffCompProperties_Chargeable
                        {
                            compClass = typeof(HediffComp_Chargeable),
                            initialCharge = 0.25f, fullChargeAmount = 1f, minChargeToActivate = 0.2f,
                        });
                    }

                    var timerReport = new System.Text.StringBuilder();
                    var parts = patient.health.hediffSet.GetNotMissingParts().ToList();
                    var names = new[] { "chance", "wounds", "infect", "message", "selfheal", "charge" };
                    for (var i = 0; i < names.Length && i < parts.Count; i++)
                    {
                        timerReport.Append(names[i]).Append('=');
                        try
                        {
                            var made = HediffMaker.MakeHediff(baseDef, patient, parts[i]);
                            if (made is null) { timerReport.Append("建不出;"); continue; }
                            // Distinct severities as well as distinct parts: two conditions of one def on one part would
                            // share a key, and that is exactly the case where a wrong timer matters most.
                            made.Severity = 0.11f + 0.03f * i;
                            patient.health.AddHediff(made);
                            timerReport.Append(parts[i].def?.defName ?? "?").Append(';');
                        }
                        catch (Exception e) { timerReport.Append("异常:").Append(e.GetType().Name).Append(';'); }
                    }
                    // The count alone cannot tell "six new conditions" from "the six that were already there" - both read 6.
                    // So list what actually carries a timer comp, and encode the block right here: waiting for the next census
                    // does not work for these six. They live on `Cut`, which is a `Hediff_Injury` and heals within a few
                    // hundred ticks, so by the time a census runs the conditions are gone and the row reads exactly like the
                    // baseline. Measured: 42 timed right after the probe, back to the baseline 6 after 300 ticks.
                    var timerSummary = new System.Text.StringBuilder();
                    timerSummary.Append($"已在 {patient.LabelShort} #{patient.thingIDNumber} 上造 {names.Length} 条带计时 comp 的伤口：");
                    timerSummary.Append(timerReport);
                    timerSummary.Append($" 现有条件 {patient.health.hediffSet.hediffs.Count} 条 / ");
                    timerSummary.Append(string.Join(",", patient.health.hediffSet.hediffs
                        .Where(hd => hd is HediffWithComps hwc && hwc.comps.Any(c => c is HediffComp_Disappears
                            || c is HediffComp_KillAfterDays || c is HediffComp_ChanceToRemove
                            || c is HediffComp_HealPermanentWounds || c is HediffComp_Infecter
                            || c is HediffComp_MessageAfterTicks || c is HediffComp_SelfHeal
                            || c is HediffComp_Chargeable))
                        .Select(hd => (hd.Part?.def?.defName ?? "-") + ":"
                            + string.Join("+", ((HediffWithComps)hd).comps
                                .Where(c => c is HediffComp_ChanceToRemove || c is HediffComp_HealPermanentWounds
                                    || c is HediffComp_Infecter || c is HediffComp_MessageAfterTicks
                                    || c is HediffComp_SelfHeal || c is HediffComp_Chargeable
                                    || c is HediffComp_Disappears || c is HediffComp_KillAfterDays)
                                .Select(c => c.GetType().Name.Replace("HediffComp_", ""))))));
                    timerSummary.Append("\n载荷：").Append(HediffTimerSync.Encode(patient) ?? "(空)");
                    Answer(id, true, "ok", timerSummary.ToString());
                    break;
                }

            case "script-mental":
                // Starts a real mental state so the age half of that channel can be observed. Without it a colony can go
                // thousands of ticks without a break, and "0 with an age" would be indistinguishable from "the age is
                // never written".
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var wantDef = DefDatabase<MentalStateDef>.GetNamedSilentFail(
                        string.IsNullOrWhiteSpace(argument) ? "Roaming" : argument.Trim());
                    if (wantDef is null) { Answer(id, false, "NotFound", "没有这个精神状态：" + argument); return; }
                    Pawn? patient = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (candidate?.mindState?.mentalStateHandler is not null && !candidate.Dead)
                        {
                            patient = candidate;
                            break;
                        }
                    if (patient is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    // `TryStartMentalState` is the same entry the mood check uses, so the state it installs is a real one -
                    // including whatever `PreStart` does. The probe wants a real one: a hand-built state would also have
                    // a fresh age, and would prove nothing about the age travelling.
                    // `forced: true` only skips the "may a break start now" gates (`MentalBreaksBlocked`,
                    // `StateCanOccur`). Everything else - the state object, `PreStart`, the handler field - is the same
                    // path the mood check takes, which is what the probe needs: a hand-built state would also have a
                    // fresh age and would prove nothing about the age travelling.
                    var handler = patient.mindState.mentalStateHandler;
                    var unforced = handler.TryStartMentalState(wantDef, "probe", forceWake: true);
                    var started = unforced
                        || handler.TryStartMentalState(wantDef, "probe", forced: true, forceWake: true);
                    var live = handler.CurState;
                    Answer(id, true, "ok", $"已让 {patient.LabelShort} #{patient.thingIDNumber} 进入 {wantDef.defName}"
                        + $"，未强制 {unforced}，启动 {started}，"
                        + $"当前状态 {live?.def?.defName ?? "无"}，Age {live?.Age ?? -1}，"
                        + $"清醒 {patient.Awake()}，情绪崩溃被阻断 {handler.MentalBreaksBlocked()}");
                }
                return;
            case "advance-ticks":
                // Steps the world forward with no replica attached. A paused headless server advances nothing - the tick
                // guard holds the timeline while no audience is watching - so a scripted job sits in the queue with a
                // `Wait` driver forever and every counter channel stays at its first frame. This is the observation
                // primitive those readings need, and it goes through `DoSingleTick` so the tick is the real one.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var count = 0;
                    if (!string.IsNullOrWhiteSpace(argument)
                        && int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        count = Math.Clamp(parsed, 1, 20000);
                    if (count == 0) count = 60;
                    var manager = Find.TickManager;
                    if (manager is null) { Answer(id, false, "NoTickManager", "没有 time manager"); return; }
                    var before = manager.TicksGame;
                    var paused = manager.Paused;
                    var reason = "ok";
                    try
                    {
                        // A paused world refuses every tick at the gate, so the observation window has to be open before
                        // the first call. `MayAdvanceTick` is consulted for the refusal reason only: it is the one place
                        // that names which of the six inputs is holding the timeline.
                        SharedColony.ObservableTicks = true;
                        if (!SharedColony.MayAdvanceTick(out reason) && reason is not ("paused" or "authority-paused"))
                        {
                            Answer(id, false, "Held", "时间线被持有，无法推进：" + reason);
                            return;
                        }
                        // The permit needs `barrier.CanAdvance`, and with no room session nothing publishes a newer
                        // server tick - so each step has to move the barrier itself. `Publish` raises only the server
                        // side and leaves `CanAdvance` false; writing both is what `Observe` does, and a host with no
                        // peers is exactly the case where the two ends of the barrier are the same process.
                        for (var i = 0; i < count; i++)
                        {
                            SharedColony.Session.Barrier.Publish(manager.TicksGame + 1);
                            manager.DoSingleTick();
                        }
                    }
                    finally { SharedColony.ObservableTicks = false; }
                    Answer(id, true, "ok", $"已推进 {manager.TicksGame - before} tick（{before} → {manager.TicksGame}），"
                        + $"原暂停 {paused}，闸门原因 {reason}");
                }
                return;
            case "script-job":
                // Gives a colonist a real work job so a counter-driven progress bar can be observed at all. Without this
                // the colony's drivers are all `JobDriver_Wait` and the driver-counter channel reads "0 with counters" -
                // a number that cannot distinguish "nothing to carry" from "the channel never fires".
                {
                    var jobDef = DefDatabase<JobDef>.GetNamedSilentFail(
                        string.IsNullOrWhiteSpace(argument) ? "Clean" : argument.Trim());
                    var map = Find.CurrentMap;
                    if (jobDef is null || map is null)
                    {
                        Answer(id, false, "NotFound", "没有这个任务定义或当前地图不可用：" + argument);
                        return;
                    }
                    Pawn? worker = null;
                    foreach (var candidate in map.mapPawns.FreeColonistsSpawned)
                        if (candidate?.jobs is not null && !candidate.Dead)
                        {
                            worker = candidate;
                            break;
                        }
                    if (worker is null) { Answer(id, false, "NoPawn", "没有可用的自由殖民者"); return; }
                    // Filth is spawned on the worker's own cell: a cleaning job whose target is three rooms away is a
                    // job about pathing, and what this probe needs is a driver with a counter running immediately.
                    var filthDef = DefDatabase<ThingDef>.GetNamedSilentFail("Filth_Dirt");
                    Thing? target = null;
                    if (filthDef is not null)
                    {
                        target = ThingMaker.MakeThing(filthDef);
                        // `Filth.thickness` is a public int field, not a setter. One is the thinnest layer and the
                        // smallest `cleaningWorkToReduceThickness`, which is what makes the job short enough to watch.
                        if (target is Filth filth) filth.thickness = 1;
                        GenSpawn.Spawn(target, worker.Position, map);
                    }
                    if (target is null) { Answer(id, false, "NoTarget", "没有可用的活儿目标（Filth_Dirt）"); return; }
                    // A driver started outside a tick has no toil list yet, and `StartJob` walks it immediately. Reported
                    // as a code rather than an exception so the next reader knows which step failed: a bare
                    // NullReferenceException from inside `StartJob` says nothing about which of the seven steps above
                    // it came from.
                    var job = JobMaker.MakeJob(jobDef, target);
                    job.playerForced = true;
                    var taken = worker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    // `TryTakeOrderedJob` only *queues* the job, and the promotion from queue to driver is gated on
                    // `!Find.TickManager.Paused` inside `StartJob`. A headless world is paused by design, so the queue
                    // never drains and the driver stays `Wait` forever - measured: accepted=True, driver unchanged
                    // across 2100 ticks. Starting the job directly is what makes the probe observe a driver at all.
                    var started = false;
                    if (taken)
                    {
                        try { worker.jobs.StartJob(job, JobCondition.InterruptForced); started = true; }
                        catch (Exception error)
                        {
                            Answer(id, false, "StartFailed",
                                $"任务已排队但启动失败：{error.GetType().Name}: {error.Message}");
                            return;
                        }
                    }
                    // The answer reports three things, because "the job was refused" and "the job was taken and then
                    // abandoned on its first tick" are different failures and only the second one leaves a Wait driver.
                    // `WorkTags` is included because a manual order still fails when the work type is switched off in
                    // the work tab, which is the usual reason a scripted job never shows up as a driver.
                    // `JobDef` carries no work type - the link lives on the WorkGiver that issues the job - so the
                    // cleaning work type is named directly. It is the one this command defaults to.
                    var allowed = worker.workSettings?.WorkIsActive(WorkTypeDefOf.Cleaning) ?? false;
                    Answer(id, true, "ok", $"已让 {worker.LabelShort} #{worker.thingIDNumber} 做 {jobDef.defName}"
                        + $"，目标 {target.def.defName} @{target.Position}，受理 {taken}，启动 {started}，"
                        + $"工作类型已启用 {allowed}，当前驱动 {worker.jobs?.curDriver?.GetType().Name ?? "无"}");
                }
                return;
            case "script-fire":
                // Lights a real fire, so the fire slot can be observed carrying a size that actually grew. A colony with
                // nothing burning reads "0 处燃烧" and that number says nothing about whether the slot works - the same
                // trap `nudge-probe` exists to avoid for the building sweep.
                {
                    var map = Find.CurrentMap;
                    if (map is null) { Answer(id, false, "NoMap", "没有当前地图"); return; }
                    var size = 0.1f;
                    if (!string.IsNullOrWhiteSpace(argument)
                        && float.TryParse(argument.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        size = Math.Clamp(parsed, 0.05f, 1.75f);
                    // A cell that is already burning would make the reading ambiguous, so the first free outdoor cell
                    // near the colony centre is used and the answer names it.
                    var centre = map.Center;
                    var cell = IntVec3.Invalid;
                    for (var radius = 0; radius < 40 && !cell.IsValid; radius++)
                        foreach (var candidate in GenRadial.RadialCellsAround(centre, radius, true))
                        {
                            if (!candidate.InBounds(map) || !candidate.Standable(map)) continue;
                            if (candidate.GetFirstThing<Fire>(map) is not null) continue;
                            cell = candidate;
                            break;
                        }
                    if (!cell.IsValid) { Answer(id, false, "NoCell", "找不到可点燃的空地"); return; }
                    var started = FireUtility.TryStartFireIn(cell, map, size, null);
                    var fire = cell.GetFirstThing<Fire>(map);
                    Answer(id, true, "ok", started
                        ? $"已在 {cell} 点火，fireSize = {fire?.fireSize ?? -1f:0.00}"
                        : $"在 {cell} 点火失败（地面不可燃）");
                }
                return;
            case "script-condition":
                // Creates a real weather condition on the authority, so the condition channel can be observed carrying
                // something. Without this a calm map reads "0 conditions / sent 0", and that number says nothing about
                // whether the channel works - the same trap `nudge-probe` exists to avoid for the building sweep.
                if (string.IsNullOrWhiteSpace(argument))
                {
                    Answer(id, false, "ArgumentRequired", "用法：script-condition <defName> [天数]");
                    return;
                }
                {
                    var parts = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var conditionDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(parts[0]);
                    var targetMap = Find.CurrentMap;
                    if (conditionDef is null || targetMap?.gameConditionManager is null)
                    {
                        Answer(id, false, "NotFound", "没有这个天气状况定义或当前地图不可用：" + parts[0]);
                        return;
                    }
                    // Parsed rather than defaulted, so "how many days" is an explicit choice at every call site.
                    var days = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsed) ? parsed : 1f;
                    var condition = GameConditionMaker.MakeCondition(conditionDef, (int)(days * 60000f));
                    targetMap.gameConditionManager.RegisterCondition(condition);
                    Answer(id, true, "ok", $"已生成天气状况 {conditionDef.defName} #{condition.uniqueID}，"
                        + $"持续 {days:0.##} 天，起始 tick {condition.startTick}，偏移 {condition.TemperatureOffset():0.##}");
                }
                return;
            case "script-zone":
                // Creates a real zone on the authority so the whole zone channel can be exercised without a human
                // dragging one: `ZoneSync.Publish` → frame → `ZoneSync.Apply` on every replica. A zone that exists only
                // on the authority and draws nowhere is the failure this is here to catch.
                var zoneResult = ScriptedZoneDrag.PlaceZoneAt(argument);
                Answer(id, zoneResult.Ok, zoneResult.Code, zoneResult.Detail);
                return;
            case "faction-report":
                // The client's log filled with `Faction  has null relation with X. Returning dummy relation.` - 7256
                // lines, 22 per spawned thing - from `AttackTargetsCache.RegisterTarget` walking
                // `AllFactionsListForReading` and calling `HostileTo` on every pair (`Verse.AI/AttackTargetsCache.cs:181`).
                // `HostileTo` returns false for a null faction, so the offender is a *real* Faction whose `relations`
                // list is empty (`RimWorld/Faction.cs:503`). Which one it is cannot be read from the message, because
                // `Faction.Name` falls back to `def.LabelCap` and that is empty here - hence this dump.
                Answer(id, true, "ok", FactionReport());
                return;
            case "job-report":
                // "The pawn never does anything" is answered by which pump ran, not by the job name. One line per
                // colonist, each naming the clause of the expiry check that refused.
                var jobText = new StringBuilder(JobPumpProbe.Summary());
                if (Find.CurrentMap is { } jobMap)
                    foreach (var colonist in jobMap.mapPawns.FreeColonistsSpawned)
                        jobText.Append(Environment.NewLine).Append("  ").Append(JobPumpProbe.Explain(colonist));
                jobText.Append(Environment.NewLine).Append(TickStampRepair.Summary());
                jobText.Append(Environment.NewLine).Append(ZoneSync.Summary());
                Answer(id, true, "ok", jobText.ToString());
                return;
            case "pawn-report":
            case "pawn-report-again":
                // Two calls, not one: "the pawns do not move" is a statement about a delta, and a single roster cannot
                // tell a still frame from a still world. The second call diffs against the first.
                var motion = PawnMotionProbe.Report(command == "pawn-report-again");
                Answer(id, motion.Ok, motion.Code, motion.Detail + Environment.NewLine
                    + SharedPathRequests.Counters());
                return;
            case "wire-round-trip":
                // One real frame, packed and unpacked by the same codec the peers use, with every channel count diffed.
                // It cannot prove another process applied it - there is no replica here - but it does rule out the two
                // failures that produce a silent empty channel: a frame that never carried the channel, and a codec that
                // dropped it.
                Answer(id, true, "ok", WireRoundTripProbe.Report());
                return;
            case "tick-report":
                // "The colony never advances" is the parent of every other symptom here: no construction, no
                // hauling, no alerts that mean anything. This answers why the clock in the *world* stands still,
                // which is a different question from why the room's own tick counter does.
                Answer(id, true, "ok", ClockReport());
                return;
            case "server-start":
                // Starting a world means replacing this process (a second LoadGame in one process is a SIGSEGV,
                // measured). That belongs to the supervisor, which the portal talks to directly - so this branch
                // exists only to answer honestly instead of hanging.
                Answer(id, false, "use-supervisor",
                    "换档由 portal 的 supervisor 负责（游戏进程必须重启才能载入另一个世界），不要发给游戏进程");
                return;
            default:
                Answer(id, false, "unsupported-command",
                    "游戏进程只执行 save / reload-settings / quit / server-shutdown / server-stop / answer-dialog / construct-report / faction-report / script-build / script-build-at / script-zone / capture-counters / job-report / pawn-report / pawn-report-again / tick-report，收到：" + command);
                return;
        }
    }

    // ---------------------------------------------------------------- the commands

    /// <summary>Why the world's own clock is or is not advancing.
    ///
    /// <para>`ticksGame=0` with the room reporting `serverTick=24939` is not a contradiction, it is two clocks: the
    /// room's counter is driven by `DedicatedServer`'s loop, while `TickManager.ticksGameInt` is driven by the game's
    /// own `TickManagerUpdate`, which a resident that never got an unpaused tick will keep at 0 forever. The room can
    /// therefore look busy while the colony has not simulated a single step - and every symptom that follows
    /// ("nothing gets built", "the alert says idle", "the pawns do not move") is downstream of this one number.</para>
    ///
    /// <para>`Scribe_Values.Look(ref ticksGameInt, "ticksGame", 0)` (`Verse/TickManager.cs:290`) writes the default
    /// 0 as an *absent* element, which is why the saved XML has no `&lt;ticksGame&gt;` at all and reads back as 0.</para></summary>
    /// <summary>Every faction this peer holds, with the numbers that decide whether it can answer
    /// <c>RelationWith</c>.
    ///
    /// <para>The failure this exists for: `Faction  has null relation with X. Returning dummy relation.` repeated
    /// 7256 times in one client log, 22 lines per spawned object - `AttackTargetsCache.RegisterTarget` walks
    /// `AllFactionsListForReading` and asks `thing.HostileTo(faction)` for each one
    /// (<c>Verse.AI/AttackTargetsCache.cs:181</c>), and `Faction.RelationWith` logs when no relation matches
    /// (<c>RimWorld/Faction.cs:503</c>). The offending faction's name prints as two spaces because
    /// <c>Faction.Name</c> falls back to <c>def.LabelCap</c> and that is empty for it, so the message alone cannot
    /// identify it.</para></summary>
    /// <summary>Checks each remaining gap tag against a live object's own Scribe fragment.
    ///
    /// <para><b>Why this exists.</b> Four rounds of this task have gone into deciding, entry by entry, whether a field
    /// reaches a replica. The assembly listing answers the wrong question: every remaining entry <em>is</em> scribed
    /// (<c>已 scribe 47 / 真缺口 0</c>), so what decides it is whether the carrier is one of the objects a sweep
    /// re-serialises. That is a property of the running world, not of the source, and guessing it has produced three wrong
    /// conclusions already - <c>AuthoritativeFrame.PawnState</c> was the worst (passed as null while its type suggested
    /// every pawn field travelled).</para>
    ///
    /// <para><b>One query per tag, first carrier wins.</b> The first version walked every thing on every map and asked
    /// each of them about all twenty tags, which printed 529075 lines of "this object does not carry it" and buried the
    /// seventy-eight that mattered. Each tag now searches for its own carrier and stops, so the output is one line per
    /// gap and nothing else.</para>
    ///
    /// <para><b>Three outcomes, deliberately distinct.</b> "In the payload" is settled. "No carrier in this save" is not a
    /// defect either - a fermenting barrel only exists once one is built. "Carrier exists but the tag is absent" is the
    /// only outcome that is a finding, and it is the one a single red/green counter could never have isolated.</para></summary>
    private static string GapAudit()
    {
        var inPayload = new List<string>();
        var absent = new List<string>();
        var noCarrier = new List<string>();
        // A *fourth* outcome, because three existed and one of them was lying. `PassingShip.ticksUntilDeparture` was
        // reported under 「没有载体」 for rounds after `PassingShipSync` had started carrying it: the tag is genuinely
        // absent from the object's Scribe fragment (the whole point of that channel is that it does not travel that way),
        // so the fragment check can never see it. Reading 「没有载体」 as "this field never reaches a replica" is wrong,
        // and it is wrong in the direction that hides working features behind a work item.
        //
        // The list is explicit rather than derived: a channel that carries a field *outside* the Scribe path is exactly
        // the case no test over the source can discover, so it has to be named - and naming it here is also the reminder
        // to keep it in step when a channel is added.
        var byChannel = new List<string>();
        // The eight `ResidualCounterSync` entries are read from that channel's own table rather than repeated here:
        // a second copy of "what travels" is a list that stops matching the code it describes, and this probe already
        // carried a stale one (it named six of them by hand and reported the other two as `没有载体`).
        var residualKeys = new HashSet<string>(ResidualCounterSync.Keys(), StringComparer.Ordinal);
        string? ChannelFor(string tag) => tag switch
        {
            "PassingShip.ticksUntilDeparture" => "PassingShipSync（逐图 m<uniqueID> 载荷）",
            "GameEnder.ticksToGameOver" => "GameEnderSync",
            "LordJob_HateChant.lostPawns" => "LordSync",
            "MentalFitGenerator.ticksUntilCanDoMentalFit" => "MentalStateSync",
            _ => residualKeys.Contains(tag) ? "ResidualCounterSync" : null,
        };
        void Check(string tag, Func<IExposable?> locate)
        {
            IExposable? carrier;
            try { carrier = locate(); }
            catch (Exception error) { noCarrier.Add(tag + "（读取失败 " + error.GetType().Name + "）"); return; }
            if (carrier is null)
            {
                // No live instance. If a dedicated channel carries the field anyway, this is not a gap at all.
                if (ChannelFor(tag) is { Length: > 0 } named) byChannel.Add(tag + "（" + named + "；本存档无实例）");
                else noCarrier.Add(tag);
                return;
            }
            var payload = Save(carrier);
            if (payload is null || payload.Length == 0)
            {
                if (ChannelFor(tag) is { Length: > 0 } namedEmpty) byChannel.Add(tag + "（" + namedEmpty + "；片段取证失败）");
                else noCarrier.Add(tag);
                return;
            }
            // The element name is the field name alone: `Scribe_Values.Look(ref adaptDays, "adaptDays")` writes
            // `<adaptDays>`, never `<StoryWatcher_Adaptation.adaptDays>`. The first version searched for the qualified
            // name and therefore reported every single tag as missing - a probe that can only fail is the same defect as a
            // counter that is always red, and it cost two deploy cycles to notice because the "missing" answer was the
            // one I expected.
            var needle = "<" + ElementOf(tag) + ">";
            var at = payload.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
            {
                // **The second false negative, and the one that made the first version useless.** `Scribe_Values.Look`
                // omits the element when the value equals the default it was given, so an absent tag usually means "this
                // value is currently the default" rather than "this value does not travel". Measured on all eight of the
                // first run's "not in payload" rows: `disturbingVisionTick` 0, `anomalyKnowledgeGained` 0f,
                // `naturalGoodwillTimer` 0, both `adaptDays` 0f, `level` 0 - every one of them at its default, so the
                // omission was correct in each case. Reported as its own outcome so the two can never be confused again.
                // A channel-carried field is legitimately absent from the fragment even when non-default; send it to
                // the channel bucket so it cannot be read as a defect.
                if (ChannelFor(tag) is { Length: > 0 } namedAbsent)
                    byChannel.Add(tag + "（" + namedAbsent + "；片段里没有，正是不走 Scribe 的那半个）");
                else
                    absent.Add(tag + "（片段 " + payload.Length + " B，标签缺失 - 值等于默认值时 Scribe 不写该元素）");
                return;
            }
            var end = payload.IndexOf("</" + tag + ">", at, StringComparison.Ordinal);
            var value = end > at ? payload.Substring(at + needle.Length, Math.Min(end - at - needle.Length, 32)) : "?";
            inPayload.Add(tag + "=" + value);
        }

        // Things: one pass to find one instance of each wanted kind, then stop - `First<T>` does the searching.
        Check("Building_FermentingBarrel.wortCount", () => FirstThing<Building_FermentingBarrel>());
        Check("Building_FleshmassHeart.bpm", () => FirstThing<Building_FleshmassHeart>());
        Check("Building_GeneAssembler.workDone", () => FirstThing<Building_GeneAssembler>());
        Check("Building_GrowthVat.containedNutrition", () => FirstThing<Building_GrowthVat>());
        Check("Building_MechCharger.wasteProduced", () => FirstThing<Building_MechCharger>());
        Check("Building_VoidMonolith.disturbingVisionTick", () => FirstThing<Building_VoidMonolith>());
        Check("CompDeepDrill.portionYieldPct", () => FirstHost<CompDeepDrill>());
        Check("CompStudiable.anomalyKnowledgeGained", () => FirstHost<CompStudiable>());
        Check("CompTemperatureRuinable.ruinedPercent", () => FirstHost<CompTemperatureRuinable>());
        Check("CompTerrainPump.progressTicks", () => FirstHost<CompTerrainPump>());
        Check("CompToxifier.pollutingProgressTicks", () => FirstHost<CompToxifier>());
        Check("MonumentMarker.ticksSinceDisallowedBuilding", () => FirstThing<MonumentMarker>());
        Check("PawnFlyer.ticksFlying", () => FirstThing<PawnFlyer>());
        Check("ActiveTransporter.age", () => FirstThing<ActiveTransporter>());
        Check("Book.generations", () => FirstThing<Book>());
        Check("Flood.floodedTileCount", () => FirstThing<Flood>());
        Check("MinifiedTree.ticksTillDeath", () => FirstThing<MinifiedTree>());
        Check("PitGate.numIncidentsFired", () => FirstThing<PitGate>());
        Check("HarbingerTree.nutrition", () => FirstThing<HarbingerTree>());
        // Bills ride inside their bench's own fragment (`Scribe_Deep.Look(ref billStack, "billStack")` `:589364`).
        Check("Bill_Production.repeatCount", () => FirstBillGiver("repeatCount"));
        Check("Bill_Production.targetCount", () => FirstBillGiver("targetCount"));
        Check("Bill_Production.unpauseWhenYouHave", () => FirstBillGiver("unpauseWhenYouHave"));
        Check("Bill_Autonomous.formingTicks", () => FirstBillGiver("formingTicks"));
        Check("Bill_Mech.gestationCycles", () => FirstBillGiver("gestationCycles"));

        // Globals: exactly one instance each and no sweep reaches them, so a miss here is a real defect.
        Check("StoryWatcher_Adaptation.adaptDays", () => Find.StoryWatcher?.watcherAdaptation);
        Check("StoryWatcher_PopAdaptation.adaptDays", () => Find.StoryWatcher?.watcherPopAdaptation);
        Check("GameComponent_Anomaly.level", () => Find.Anomaly);
        Check("Faction.naturalGoodwillTimer", () => Find.FactionManager?.AllFactionsListForReading?.FirstOrDefault());
        Check("IdeoDevelopmentTracker.reformCount", () =>
            Find.IdeoManager?.IdeosListForReading?.Select(ideo => ideo?.development).FirstOrDefault(development => development is not null));
        Check("Caravan_ForageTracker.progress", () => Find.WorldObjects?.Caravans?.Select(caravan => caravan?.forage)
            .FirstOrDefault(forage => forage is not null));
        Check("PassingShip.ticksUntilDeparture", () => Find.WorldObjects?.AllWorldObjects?
            .OfType<PassingShip>().FirstOrDefault());
        // Both collections are private on their owners (`GameComponent_Anomaly.corpseTrackers` `:723199`,
        // `Precept_Ritual.activeObligations` `:501224`), so they are reached through the field the same way the census does.
        Check("UnnaturalCorpseTracker.research", () =>
            HarmonyLib.AccessTools.Field(typeof(GameComponent_Anomaly), "looseCorpseTrackers")?.GetValue(Find.Anomaly)
                is System.Collections.IEnumerable loose
                ? loose.Cast<object>().OfType<UnnaturalCorpseTracker>().FirstOrDefault()
                : null);
        Check("RitualObligation.triggeredTick", () =>
        {
            var ideos = Find.IdeoManager?.IdeosListForReading;
            if (ideos is null) return null;
            foreach (var ideo in ideos)
                foreach (var precept in ideo?.PreceptsListForReading ?? [])
                    if (precept is Precept_Ritual ritual
                        && HarmonyLib.AccessTools.Field(typeof(Precept_Ritual), "activeObligations")?.GetValue(ritual)
                            is System.Collections.IEnumerable obligations)
                        foreach (var obligation in obligations)
                            if (obligation is RitualObligation wanted) return wanted;
            return null;
        });
        Check("InfectionPathway.ageTicks", () => FirstPawnPart(pawn => pawn?.infectionVectors?.Pathways?
            .FirstOrDefault(pathway => pathway is not null)));
        Check("Pawn_MindState.lastHumanMeatIngestedTick", () => FirstPawnPart(pawn => pawn?.mindState));
        Check("LordJob_HateChant.lostPawns", () => FirstLordJob<LordJob_HateChant>());
        Check("RitualBehaviorWorker_Trial.ticksSinceLastInteraction", () => null);
        Check("PsychicRitualToil_Wait.durationTicks", () => null);
        Check("JobSyncTracker.initialCount", () => null);
        Check("MentalFitGenerator.ticksUntilCanDoMentalFit", () => null);
        Check("Gene_Healing.ticksToHeal", () => null);
        Check("GameEnder.ticksToGameOver", () => null);
        Check("ChoiceLetter_GrowthMoment.traitChoiceCount", () => null);
        Check("DrugTakeRecord.lastTakenTicks", () => null);
        Check("Reward_Goodwill.amount", () => null);
        Check("Reward_Items.lastTotalMarketValue", () => null);
        Check("ScenPart_CreateIncident.occurTick", () => null);
        Check("ScenPart_StartingAnimal.count", () => null);
        Check("ScenPart_ThingCount.count", () => null);

        // **The decisive second pass.** For every tag the first pass did not find, move the value off its default through
        // the private field and save again. The tag cannot appear for any other reason: `Scribe_Values.Look(ref x, "tag",
        // def)` writes the element exactly when the value differs from `def`, so "absent, then present after one write"
        // proves the field travels with the carrier. It also undoes itself - the original value is put back before the
        // next entry - so a running colony is not altered by the measurement.
        var forced = new List<string>();
        var stillAbsent = new List<string>();
        void Force(string tag, IExposable? carrier, string field, object nonDefault, object original)
        {
            if (carrier is null) return;
            var info = HarmonyLib.AccessTools.Field(carrier.GetType(), field);
            if (info is null) { stillAbsent.Add(tag + "（找不到字段 " + field + "）"); return; }
            try
            {
                info.SetValue(carrier, nonDefault);
                var payload = Save(carrier);
                info.SetValue(carrier, original);
                // "Save returned null" and "the tag is missing from a fragment" are different answers and the first
                // version printed both as 「仍无标签」 - which is exactly the conflation this whole probe exists to stop.
                if (payload is null)
                    stillAbsent.Add(tag + "（写入 " + field + " 后 Scribe 忙碌，未能取证）");
                else if (payload.Contains("<" + ElementOf(tag) + ">", StringComparison.Ordinal))
                    forced.Add(tag);
                else
                    stillAbsent.Add(tag + "（写入 " + field + " 后片段 " + payload.Length + " B 里仍无标签）");
            }
            catch (Exception error)
            {
                stillAbsent.Add(tag + "（" + error.GetType().Name + "）");
                try { info.SetValue(carrier, original); } catch (Exception) { }
            }
        }
        if (Find.StoryWatcher?.watcherAdaptation is { } liveAdaptation)
            Force("StoryWatcher_Adaptation.adaptDays", liveAdaptation, "adaptDays", 3.5f, liveAdaptation.AdaptDays);
        if (Find.StoryWatcher?.watcherPopAdaptation is { } livePopAdaptation)
            Force("StoryWatcher_PopAdaptation.adaptDays", livePopAdaptation, "adaptDays", 3.5f, livePopAdaptation.AdaptDays);
        if (Find.Anomaly is { } liveAnomaly)
        {
            var levelField = HarmonyLib.AccessTools.Field(typeof(GameComponent_Anomaly), "level");
            if (levelField?.GetValue(liveAnomaly) is int levelValue)
                Force("GameComponent_Anomaly.level", liveAnomaly, "level", levelValue == 3 ? 4 : 3, levelValue);
        }
        if (Find.FactionManager?.AllFactionsListForReading?.FirstOrDefault() is { } liveFaction)
        {
            var timerField = HarmonyLib.AccessTools.Field(typeof(Faction), "naturalGoodwillTimer");
            if (timerField?.GetValue(liveFaction) is int timerValue)
                Force("Faction.naturalGoodwillTimer", liveFaction, "naturalGoodwillTimer", timerValue + 1, timerValue);
        }
        if (FirstThing<Building_VoidMonolith>() is { } liveMonolith)
        {
            var visionField = HarmonyLib.AccessTools.Field(typeof(Building_VoidMonolith), "disturbingVisionTick");
            if (visionField?.GetValue(liveMonolith) is int visionValue)
                Force("Building_VoidMonolith.disturbingVisionTick", liveMonolith, "disturbingVisionTick",
                    visionValue + 15000, visionValue);
        }
        if (FirstHost<CompStudiable>() is ThingWithComps { } studiableHost
            && studiableHost.TryGetComp<CompStudiable>() is { } studiable)
        {
            // The field is on the comp, not on the host: `Force` reflects on `carrier.GetType()`, so passing the host
            // made it look for `anomalyKnowledgeGained` on a `ThingWithComps` and report 「找不到字段」 - which read as
            // "the field does not travel" for one whole deploy cycle. The fragment still has to come from the host,
            // because `ThingWithComps.PostExposeData` is what writes the comp's fields into the host's element.
            var knowledgeField = HarmonyLib.AccessTools.Field(typeof(CompStudiable), "anomalyKnowledgeGained");
            if (knowledgeField?.GetValue(studiable) is float knowledge)
            {
                try
                {
                    knowledgeField.SetValue(studiable, knowledge + 1f);
                    var hostPayload = Save(studiableHost);
                    knowledgeField.SetValue(studiable, knowledge);
                    if (hostPayload is not null && hostPayload.Contains("<anomalyKnowledgeGained>", StringComparison.Ordinal))
                        forced.Add("CompStudiable.anomalyKnowledgeGained");
                    else
                        stillAbsent.Add("CompStudiable.anomalyKnowledgeGained（写入后"
                            + (hostPayload is null ? "取证失败" : "片段仍无标签") + "）");
                }
                catch (Exception error) { stillAbsent.Add("CompStudiable.anomalyKnowledgeGained（" + error.GetType().Name + "）"); }
            }
        }
        if (FirstPawnPart(pawn => pawn?.mindState) is Pawn_MindState liveMindState)
        {
            var meatField = HarmonyLib.AccessTools.Field(typeof(Pawn_MindState), "lastHumanMeatIngestedTick");
            if (meatField?.GetValue(liveMindState) is int meatTick)
                Force("Pawn_MindState.lastHumanMeatIngestedTick", liveMindState, "lastHumanMeatIngestedTick",
                    meatTick + 30000, meatTick);
        }
        if (FirstPawnPart(pawn => pawn?.infectionVectors?.Pathways?.FirstOrDefault(pathway => pathway is not null))
            is InfectionPathway livePathway)
        {
            var ageField = HarmonyLib.AccessTools.Field(typeof(InfectionPathway), "ageTicks");
            if (ageField?.GetValue(livePathway) is int pathwayAge)
                Force("InfectionPathway.ageTicks", livePathway, "ageTicks", pathwayAge + 1000, pathwayAge);
        }

        // **Third pass: decide the ones with no live instance by def, not by instance.** 41 entries had no carrier on
        // this save, which is a fact about the colony rather than about the channel - a fermenting barrel only exists once
        // one is built. What actually decides whether a field travels in that case is the carrier's `ThingDef.category`,
        // because the four sweeps select by category (Item / Building / Filth / Ethereal / Attachment / sown Plant) and
        // anything else needs a named channel. This walks the def database, which always knows its categories.
        var byCategory = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        void Categorise(string tag, string defName)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def is null) { byCategory.TryAdd("无此 def", []); byCategory["无此 def"].Add(tag); return; }
            var kind = def.category.ToString();
            // A sown plant and a wild one are different answers: the sweep sends only `sown`, and a `HarbingerTree` is
            // wild by construction, so it lands in the wild bucket rather than being called covered.
            if (def.category == ThingCategory.Plant) kind += def.thingClass == typeof(HarbingerTree) ? "/野生（不扫）" : "/补扫";
            else if (def.category is ThingCategory.Item or ThingCategory.Building or ThingCategory.Filth
                     or ThingCategory.Ethereal or ThingCategory.Attachment) kind += "/补扫";
            else kind += "/需专用通道";
            byCategory.TryAdd(kind, []);
            byCategory[kind].Add(tag);
        }
        Categorise("Building_FermentingBarrel.wortCount", "FermentingBarrel");
        Categorise("Building_FleshmassHeart.bpm", "FleshmassHeart");
        Categorise("Building_GeneAssembler.workDone", "GeneAssembler");
        Categorise("Building_GrowthVat.containedNutrition", "GrowthVat");
        Categorise("Building_MechCharger.wasteProduced", "BasicRecharger");
        Categorise("CompDeepDrill.portionYieldPct", "DeepDrill");
        Categorise("CompTemperatureRuinable.ruinedPercent", "MealSimple");   // 最典型的温度腐坏载体
        Categorise("CompTerrainPump.progressTicks", "MoisturePump");
        Categorise("CompToxifier.pollutingProgressTicks", "ToxifierGenerator");
        Categorise("MonumentMarker.ticksSinceDisallowedBuilding", "MonumentMarker");
        Categorise("PawnFlyer.ticksFlying", "PawnFlyer");
        Categorise("ActiveTransporter.age", "ActiveDropPod");
        Categorise("Book.generations", "Novel");
        Categorise("Flood.floodedTileCount", "SeasonalFlood");
        Categorise("MinifiedTree.ticksTillDeath", "MinifiedTree");
        Categorise("PitGate.numIncidentsFired", "PitGate");
        Categorise("HarbingerTree.nutrition", "Plant_TreeHarbinger");

        // **The eleven that `Categorise` cannot answer, because their carrier is not a ThingDef at all.**
        //
        // Each of these lives inside a deep-scribed child of an object another channel already re-serialises whole, so
        // the field does travel - what decides it is whether the *owner* exists and reaches that channel. Listed by
        // owner rather than by def because `Bill_Production.repeatCount` has no def of its own; the bench that holds it
        // does, and the bench is what the building sweep carries (`Building_WorkTable.ExposeData` scribes `billStack`
        // with `Scribe_Deep.Look`, which is exactly what `script-bill` demonstrated).
        //
        // `ScenPart_*` is the one group with no owner channel: `Find.Scenario.parts` is scribed once at load
        // (`Scenario.ExposeData`, `:517622`) and no sweep touches it. Its only runtime counter is
        // `ScenPart_CreateIncident.occurTick`, written and read inside `ScenPart.Tick` (`:520222`, `:520237`) - and
        // `Tick` is reached only from `Scenario.TickScenario` (`:517904`) inside `TickManager.DoSingleTick` (`:52924`),
        // which is the gate a replica is held before. A replica therefore never runs the only reader, so a stale
        // `occurTick` there changes nothing a player can see. Named rather than silently bucketed as covered.
        var nested = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        void Nested(string group, params string[] tags)
        {
            nested.TryAdd(group, []);
            nested[group].AddRange(tags);
        }
        Nested("工作台 billStack（建筑补扫深 scribe）",
            "Bill_Production.repeatCount", "Bill_Production.targetCount", "Bill_Production.unpauseWhenYouHave",
            "Bill_Autonomous.formingTicks", "Bill_Mech.gestationCycles");
        Nested("商队 forage（WorldObjectStateSync 整对象片段）", "Caravan_ForageTracker.progress");
        Nested("任务奖励（QuestPartSync 整任务片段）",
            "Reward_Goodwill.amount", "Reward_Items.lastTotalMarketValue");
        Nested("Scenario.parts（无通道、但副本不跑唯一读者）",
            "ScenPart_CreateIncident.occurTick", "ScenPart_StartingAnimal.count", "ScenPart_ThingCount.count");
        var nestedTotal = 0;
        foreach (var group in nested) nestedTotal += group.Value.Count;

        // 35 「没有载体」 is not 35 gaps. Most of them are fields on objects this save does not contain, and the four
        // sweeps select by `ThingDef.category` - so if such an object existed, the field would travel with it. Only the
        // remainder needs a channel of its own, and that split is the number a work list should be built from.
        // Counted from `byCategory` rather than by hand, so it cannot drift from the list printed below.
        var sweptIfPresent = 0;
        var needsChannel = 0;
        foreach (var group in byCategory)
        {
            if (group.Key.EndsWith("/补扫", StringComparison.Ordinal)) sweptIfPresent += group.Value.Count;
            else needsChannel += group.Value.Count;
        }
        // The remainder matters as much as the split: `byCategory` only covers the entries that were handed to it,
        // and on this save that left 18 of the 35 in neither bucket. Printing only the two counts let the summary look
        // like it had accounted for all 35 when it had accounted for 17 - the same shape of error as a counter whose
        // parts do not add up to its total.
        var remainder = noCarrier.Count - sweptIfPresent - needsChannel - nestedTotal;
        var summary = $"缺口取证：在载荷里 {inPayload.Count} / 不在载荷里 {absent.Count} / 没有载体 {noCarrier.Count}"
            + $"（按 def 会被补扫覆盖 {sweptIfPresent} 条、真需要专用通道 {needsChannel} 条"
            + $"、挂在别人片段里 {nestedTotal} 条、未分类 {remainder} 条）"
            + $" / 有专用通道 {byChannel.Count} / 推到非默认后出现 {forced.Count} / 仍不出现 {stillAbsent.Count}";
        var text = new System.Text.StringBuilder(summary);
        void Section(string title, List<string> rows)
        {
            if (rows.Count == 0) return;
            text.Append(Environment.NewLine).Append(title).Append('：').Append(string.Join("，", rows));
        }
        Section("推到非默认后出现（字段确实随载体发送）", forced);
        Section("推到非默认后仍不出现", stillAbsent);
        Section("不在载荷里", absent);
        foreach (var group in nested)
            Section("挂在他者片段里 · " + group.Key, group.Value);
        Section("有专用通道（不走 Scribe 片段，不是缺口）", byChannel);
        Section("没有载体", noCarrier);
        Section("在载荷里", inPayload);
        foreach (var pair in byCategory)
            text.Append(Environment.NewLine).Append("按 def 分类 · ").Append(pair.Key).Append('：')
                .Append(string.Join("，", pair.Value));
        return text.ToString();
    }

    /// <summary>The Scribe element name for a gap entry: the part after the last dot. `StoryWatcher_Adaptation.adaptDays`
    /// is written as <c>&lt;adaptDays&gt;</c> - the class name is the entry's own label, never part of the payload.</summary>
    private static string ElementOf(string tag)
    {
        var dot = tag.LastIndexOf('.');
        return dot >= 0 && dot < tag.Length - 1 ? tag[(dot + 1)..] : tag;
    }

    /// <summary>The first spawned thing of one exact type on any map. Written as a generic so the twenty thing checks above
    /// stay one line each - the alternative was twenty copies of the same nested loop, which is where the previous version
    /// went wrong.</summary>
    private static IExposable? FirstThing<T>() where T : Thing
    {
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing is T wanted && !thing.Destroyed && thing.Spawned) return wanted;
        return null;
    }

    /// <summary>The host thing carrying one comp type: a comp's `PostExposeData` writes into its host's element, so the host
    /// is what has to be saved.</summary>
    private static IExposable? FirstHost<T>() where T : ThingComp
    {
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing is ThingWithComps host && !host.Destroyed && host.Spawned && host.TryGetComp<T>() is not null)
                    return host;
        return null;
    }

    /// <summary>A pawn's sub-object, since `Pawn` itself is never sent as a whole (see `AbilitySync`'s note on
    /// `AuthoritativeFrame.PawnState`).</summary>
    private static IExposable? FirstPawnPart<T>(Func<Pawn, T?> pick) where T : class, IExposable
    {
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn is not null && pick(pawn) is { } part) return part;
        return null;
    }

    /// <summary>A bench whose bill stack is non-empty, so the bill counters have somewhere to be scribed.</summary>
    private static IExposable? FirstBillGiver(string _)
    {
        foreach (var map in Find.Maps)
            foreach (var building in map.listerBuildings.allBuildingsColonist)
                if (building is IBillGiver { BillStack.Count: > 0 }) return building;
        return null;
    }

    private static IExposable? FirstLordJob<T>() where T : Verse.AI.Group.LordJob
    {
        var lords = Find.CurrentMap?.lordManager?.lords;
        if (lords is null) return null;
        foreach (var lord in lords)
            if (lord?.LordJob is T wanted) return wanted;
        return null;
    }

    /// <summary>One object's own Scribe fragment, or null when it cannot be produced. Wrapped because a fragment is
    /// produced by re-running the game's writer, which can throw on a half-built object (a minified tree mid-death, a
    /// building whose comp list is being mutated) - and one throw must not end the audit.</summary>
    private static string? Save(IExposable value)
    {
        try { return ScribeBridge.Save(value); }
        catch (Exception) { return null; }
    }

    private static string FactionReport()
    {
        var text = new StringBuilder("阵营诊断：");
        try
        {
            var manager = Find.FactionManager;
            var all = manager?.AllFactionsListForReading;
            if (all is null) return text.Append("没有阵营管理器").ToString();
            text.Append('共').Append(all.Count).Append("个。");
            for (var i = 0; i < all.Count; i++)
            {
                var faction = all[i];
                if (faction is null) { text.Append(Environment.NewLine).Append("  [").Append(i).Append("] null"); continue; }
                var relations = RelationsOf(faction);
                var name = string.IsNullOrEmpty(faction.Name) ? "<空名>" : faction.Name;
                text.Append(Environment.NewLine).Append("  [").Append(i).Append("] ")
                    .Append(faction.def?.defName ?? "<无def>")
                    .Append(" loadID=").Append(faction.loadID)
                    .Append(" 关系数=").Append(relations >= 0 ? relations.ToString() : "读不到")
                    .Append(" 玩家=").Append(faction.IsPlayer ? "是" : "否")
                    .Append(" 隐藏=").Append(faction.Hidden ? "是" : "否")
                    .Append(" 名=").Append(name);
            }
        }
        catch (Exception error) { text.Append(" 读取失败：").Append(error.Message); }
        return text.ToString();
    }

    private static System.Reflection.FieldInfo? relationsField;

    private static int RelationsOf(RimWorld.Faction faction)
    {
        try
        {
            relationsField ??= HarmonyLib.AccessTools.Field(typeof(RimWorld.Faction), "relations");
            return relationsField?.GetValue(faction) is System.Collections.ICollection list ? list.Count : -1;
        }
        catch (Exception) { return -1; }
    }

    private static string ClockReport()    {
        var text = new StringBuilder();
        var tickManager = Find.TickManager;
        text.Append("时钟诊断：ticksGame=").Append(tickManager?.TicksGame ?? -1)
            .Append(" ticksAbs=").Append(tickManager?.TicksAbs ?? -1)
            .Append(" 速度=").Append(tickManager?.CurTimeSpeed.ToString() ?? "无")
            .Append(" 暂停=").Append(tickManager?.Paused == true ? "是" : "否")
            .Append(" 强制暂停=").Append(tickManager?.ForcePaused == true ? "是" : "否")
            .Append(" 程序状态=").Append(Current.ProgramState)
            .Append('。');
        text.Append(" 共享殖民地：闸门=").Append(SharedColony.Gating ? "开" : "关")
            .Append("/正在tick=").Append(SharedColony.TickExecuting ? "是" : "否")
            .Append("/阶段=").Append(SharedColony.Session.Phase)
            .Append("/接输入=").Append(SharedColony.Session.AcceptsInput ? "是" : "否")
            .Append("/可投递=").Append(RoomConnection.CanDeliver ? "是" : "否")
            .Append('/').Append(RoomConnection.DeliveryState())
            .Append('。');
        // The room's own view, so the two clocks can be compared in one line instead of by grepping two logs.
        text.Append(" 房间：副本=").Append(HeadlessRuntime.ReplicaCount)
            .Append(" 未推进原因=").Append(HeadlessRuntime.HeldForAudience
                ? "没有观众(PauseWithoutReplicas)" : "无")
            .Append("/块=").Append(HeadlessRuntime.HoldLocalTicks ? "本地tick被扣住" : "无")
            .Append('。');
        return text.ToString();
    }

    /// <summary>
    /// Answers a dialog that was left open for a human.
    ///
    /// The argument is <c>"&lt;id&gt;:&lt;choice&gt;"</c> - a file carries one string, and inventing a nested object
    /// for this one command would mean teaching both sides a second payload shape. Ids are integers, so the first
    /// colon is an unambiguous separator.
    /// </summary>
    private static (int X, int Z, int Width, int Height)? ParsePlacement(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return null;
        var parts = argument.Split(',');
        if (parts.Length is < 2 or > 4) return null;
        var numbers = new int[4];
        numbers[2] = 5;
        numbers[3] = 1;
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        // A zero-width rectangle designates nothing and would read as "the designator refused everything", which is a
        // different finding. Refused as an argument instead.
        if (numbers[2] <= 0 || numbers[3] <= 0) return null;
        return (numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static void AnswerDialog(string id, string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            Answer(id, false, "answer-required", "参数应为 \"<对话框编号>:<A|B|C|accept|cancel>\"");
            return;
        }
        var split = argument.IndexOf(':');
        if (split <= 0 || split == argument.Length - 1)
        {
            Answer(id, false, "answer-required", "参数应为 \"<对话框编号>:<A|B|C|accept|cancel>\"，收到：" + argument);
            return;
        }
        if (!int.TryParse(argument[..split], NumberStyles.None, CultureInfo.InvariantCulture, out var dialogId))
        {
            Answer(id, false, "answer-required", "对话框编号不是整数：" + argument[..split]);
            return;
        }
        var choice = argument[(split + 1)..];
        if (!HeadlessDialogQueue.Answer(dialogId, choice, out var detail))
        {
            Answer(id, false, "dialog-answer-refused", detail);
            return;
        }
        Answer(id, true, "dialog-answered", detail);
    }

    /// <summary>
    /// Saves the world. Prefers the room's own checkpoint path, because that is what a running authoritative room
    /// restores from: a plain local save would leave the room's durable state behind.
    /// </summary>
    private static void RunSave(Action<CommandOutcome> finish)
    {
        var server = HeadlessRuntime.Server;
        if (server is null) { finish(CommandOutcome.Bad("no-server", "服务端尚未启动")); return; }
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null)
        { finish(CommandOutcome.Bad("no-world", "还没有载入世界，没有可保存的内容")); return; }
        if (Scribe.mode != LoadSaveMode.Inactive || LongEventHandler.AnyEventNowOrWaiting)
        { finish(CommandOutcome.Bad("busy", "正在载入或保存，稍后重试")); return; }
        var tick = SharedColony.Session.Barrier.AppliedTick;
        if (RoomConnection.IsCheckpointOwner && SharedColony.Gating)
        {
            var operation = RoomConnection.PublishCheckpointAsync(tick);
            if (operation.IsCompletedSuccessfully && !operation.Result)
            {
                finish(CommandOutcome.Bad("checkpoint-unavailable", "检查点未能启动（可能已有一个在跑）"));
                return;
            }
            operation.ContinueWith(task =>
            {
                var ok = task.Status == TaskStatus.RanToCompletion && task.Result;
                finish(ok
                    ? CommandOutcome.Good("checkpoint", $"检查点已发布 tick={tick}")
                    : CommandOutcome.Bad("checkpoint-failed", "检查点发布失败，原因见服务端日志"));
            }, TaskScheduler.Default);
            return;
        }
        string path;
        try { path = WorldFiles.SaveCurrentGame(); }
        catch (Exception error)
        {
            finish(CommandOutcome.Bad("save-failed", error.GetType().Name + ": " + error.Message));
            return;
        }
        server.PersistAsync().ContinueWith(task =>
        {
            finish(task.Status == TaskStatus.RanToCompletion
                ? CommandOutcome.Good("saved", $"存档已写入 {Path.GetFileName(path)}，服务端状态已持久化")
                : CommandOutcome.Bad("persist-failed", "存档已写入，但服务端状态持久化失败"));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Stops the <em>world</em> without stopping the process: checkpoint, then freeze the tick.
    ///
    /// The page is in another process now, so this is finally cheap - it used to be the operation that took the
    /// panel away for twenty seconds.
    /// </summary>
    private static CommandOutcome StopWorld()
    {
        var server = HeadlessRuntime.Server;
        if (server is null) return CommandOutcome.Bad("no-server", "服务端尚未启动");
        if (HeadlessRuntime.WorldStoppedForDiagnostics || !server.GameRunning)
            return CommandOutcome.Good("already-lobby", "当前没有在跑的世界，已经在 lobby");
        // Checkpoint synchronously: the caller is a file, and answering "stopped" before the save is durable would
        // let the operator shut down on top of an unsaved colony.
        var check = CheckpointNow();
        if (!check.Ok)
            return CommandOutcome.Bad("checkpoint-failed",
                "停止前" + check.Detail + "，已保留当前世界，未做任何改动");
        HeadlessRuntime.MarkWorldStopped("portal:stop");
        return CommandOutcome.Good("lobby",
            "已保存并停止当前世界（检查点 tick " + check.Tick + "）；服务端回到 lobby，管理页保持在线");
    }

    private static (bool Ok, int Tick, string Detail) CheckpointNow()
    {
        var server = HeadlessRuntime.Server;
        if (server is null) return (false, -1, "服务端尚未启动");
        var tick = SharedColony.Gating ? SharedColony.Session.Barrier.AppliedTick : -1;
        if (RoomConnection.IsCheckpointOwner && SharedColony.Gating)
        {
            try
            {
                if (!RoomConnection.PublishCheckpointAsync(tick).Wait(TimeSpan.FromSeconds(60)))
                    return (false, tick, "检查点未在 60 秒内提交（tick " + tick + "）");
            }
            catch (Exception error)
            {
                return (false, tick, error.GetType().Name + ": " + error.Message);
            }
        }
        else
        {
            try
            {
                if (string.IsNullOrEmpty(WorldFiles.SaveCurrentGame()))
                    return (false, tick, "无法写出检查点");
            }
            catch (Exception error)
            {
                return (false, tick, error.GetType().Name + ": " + error.Message);
            }
        }
        try { server.PersistAsync().Wait(TimeSpan.FromSeconds(30)); }
        catch (Exception error) { Log.Warning("[TurboTick portal] 检查点持久化失败：" + error.Message); }
        return (true, tick, "已提交");
    }

    /// <summary>
    /// Re-reads <c>Mod_*.xml</c> and hot-applies the fields that can be changed without a restart.
    /// </summary>
    private static CommandOutcome ReloadSettings()
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
            return CommandOutcome.Bad("busy", "Scribe 正在使用，稍后重试");
        var mod = LoadedModManager.GetMod<TurboTickMod>();
        var folderName = mod?.Content?.FolderName;
        if (string.IsNullOrEmpty(folderName))
            return CommandOutcome.Bad("settings-mod-missing", "找不到 TurboTickMod 实例");
        var name = nameof(TurboTickMod);
        var path = Path.Combine(GenFilePaths.ConfigFolderPath, "Mod_" + folderName + "_" + name + ".xml");
        if (!File.Exists(path))
            return CommandOutcome.Bad("settings-file-missing",
                "设置文件不存在：" + path + "（游戏只在关闭设置窗口时写它；手工创建同样格式的文件后再试）");
        TurboTickSettings fresh;
        try
        {
            // Cheap sanity check before Scribe: a wrong block would silently produce defaults.
            var text = File.ReadAllText(path);
            if (!text.Contains("TurboTick.TurboTickSettings", StringComparison.Ordinal))
                return CommandOutcome.Bad("settings-file-invalid", "文件里没有 TurboTick.TurboTickSettings 段：" + path);
            fresh = LoadedModManager.ReadModSettings<TurboTickSettings>(folderName, name);
        }
        catch (Exception error)
        {
            return CommandOutcome.Bad("settings-read-failed", error.GetType().Name + ": " + error.Message);
        }
        var live = TurboTickMod.Settings;
        var changed = new List<string>();
        void Copy<T>(string field, Func<TurboTickSettings, T> read, Action<TurboTickSettings, T> write)
        {
            var value = read(fresh);
            if (EqualityComparer<T>.Default.Equals(value, read(live))) return;
            write(live, value);
            changed.Add(field);
        }
        Copy("showHud", settings => settings.showHud, (settings, value) => settings.showHud = value);
        Copy("showPlayerCursors", settings => settings.showPlayerCursors, (settings, value) => settings.showPlayerCursors = value);
        Copy("pauseOnDesync", settings => settings.pauseOnDesync, (settings, value) => settings.pauseOnDesync = value);
        // autoRecover is deliberately not hot: a headless authority pins it at startup (false for an authoritative
        // room, true for lockstep). Copying the file value back would silently un-pin it.
        Copy("experimentalInteractions", settings => settings.experimentalInteractions, (settings, value) => settings.experimentalInteractions = value);
        Copy("everyoneMayTrade", settings => settings.everyoneMayTrade, (settings, value) => settings.everyoneMayTrade = value);
        Copy("enabled", settings => settings.enabled, (settings, value) => settings.enabled = value);
        Copy("multiplier", settings => settings.multiplier, (settings, value) => settings.multiplier = value);
        Copy("maxRateCap", settings => settings.maxRateCap, (settings, value) => settings.maxRateCap = value);
        var detail = changed.Count == 0
            ? "已重读 " + path + "，热更新项没有变化"
            : "已重读 " + path + "，应用：" + string.Join(", ", changed) + "（速度默认值在下次加入房间时生效）";
        return CommandOutcome.Good("reloaded", detail);
    }

    // ---------------------------------------------------------------- the load gate

    /// <summary>
    /// Checks the mod config for a save named on the command line, and records whether a world may be loaded.
    ///
    /// The game reads <c>ModsConfig.xml</c> once, before this code exists, so a config written here cannot change
    /// the <em>current</em> process. What it can do is make the next start correct and say plainly that a restart
    /// is needed. The only other option - loading anyway and hoping - is the silent content loss this exists to
    /// prevent.
    ///
    /// Returns false when the world must not be loaded. The caller gates the load on it.
    /// </summary>
    internal static bool VerifyConfiguredSaveMods(string? saveName)
    {
        if (options is null || string.IsNullOrWhiteSpace(saveName)) return true;
        // No workshop fetch here: this runs inside the game process, and steamcmd must not share a process with a
        // running game (measured: the container entrypoint mistakes it for the game and restart-loops). A
        // downloadable mod is reported as such and the next start picks it up.
        var refusal = SaveModsSync.Verify(saveName, options, HeadlessRuntime.InstalledVersion, download: false,
            fetch: null,
            message => Log.Message("[TurboTick portal] " + message),
            message => Log.Warning("[TurboTick portal] " + message));
        saveModsRefusal = refusal;
        return refusal is null;
    }

    // ---------------------------------------------------------------- plumbing

    private static void Answer(string id, bool ok, string code, string detail)
    {
        if (folder.Length == 0) return;
        var target = Path.Combine(folder, id + ".done.json");
        try
        {
            var temporary = target + ".tmp";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(new { ok, code, detail, at = DateTime.UtcNow.ToString("O") }, Json),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning("[TurboTick portal] 无法写回命令结果 " + id + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Drops requests and answers left behind by a process that is no longer running.</summary>
    private static void Sweep(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be swept is retried next start; it must not stop the inbox from working.
                _ = error;
            }
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>What a command did, in the shape the portal reads back.</summary>
internal sealed record CommandOutcome(bool Ok, string Code, string Detail)
{
    internal static CommandOutcome Good(string code, string detail) => new(true, code, detail);
    internal static CommandOutcome Bad(string code, string detail) => new(false, code, detail);
}
