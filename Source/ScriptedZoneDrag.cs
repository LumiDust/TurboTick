using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Performs a stockpile drag at a scripted tick, so the zone-command path can be exercised on demand instead of
/// waiting for someone to click.
///
/// Why this exists: every measured divergence for several rounds turned out to be **triggered** by one action rather
/// than driven by time - a player dragging a stockpile. Runs with no drag were clean (111 samples of `zones=0`), runs
/// with a drag diverged within minutes. Using "minutes until divergence" as the measure therefore measured how often
/// someone happened to drag, which is not a property of the code.
///
/// With a scripted trigger the case becomes reproducible: same tick, same rectangle, every run. That turns an
/// intermittent observation into a regression case - the single most useful thing this rig can now produce.
///
/// Runs outside the tick on purpose: <c>SharedActions.PlayerActing</c> is
/// <c>!Applying &amp;&amp; AcceptsInput &amp;&amp; !TickExecuting</c>, so a trigger fired from inside the tick would be
/// classified as simulation rather than input and would not exercise the capture path at all.
///
/// Off unless <c>TURBOTICK_SCRIPT_ZONE_AT</c> is set to a tick number. Set it on exactly one peer.
/// </summary>
internal static class ScriptedZoneDrag
{
    private static readonly int AtTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_ZONE_AT"), out var tick) ? tick : -1;

    // Defaults chosen to fit inside any colony map; a smaller map clamps rather than failing.
    private static readonly int Width = ReadInt("TURBOTICK_SCRIPT_ZONE_W", 5);
    private static readonly int Height = ReadInt("TURBOTICK_SCRIPT_ZONE_H", 5);

    private static bool fired;

    /// <summary>Scripted designation trigger, off unless <c>TURBOTICK_SCRIPT_DESIGNATE_AT</c> is set. Set it on exactly
    /// one peer, like the zone drag.</summary>
    private static readonly int AtDesignateTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_DESIGNATE_AT"), out var designateTick) ? designateTick : -1;

    private static readonly int DesignateWidth = ReadInt("TURBOTICK_SCRIPT_DESIGNATE_W", 4);

    private static bool designationsPending = true;

    internal static bool Pending => AtTick >= 0 && !fired;

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    /// <summary>Called once per frame from the game component, outside any tick.</summary>
    internal static void Tick()
    {
        if (!Pending) return;
        if (!SharedColony.HasSharedWorld) return;
        var map = Find.CurrentMap;
        if (map is null) return;
        if (Find.TickManager is null || Find.TickManager.TicksGame < AtTick) return;
        fired = true;
        try
        {
            var anchor = map.Center;
            var rect = CellRect.CenteredOn(anchor, Width, Height).ClipInsideMap(map);
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            zone.label = "scripted";
            var added = 0;
            foreach (var cell in rect)
            {
                if (zone.ContainsCell(cell)) continue;
                // The same call the designator makes, so the capture path under test is the real one.
                zone.AddCell(cell);
                added++;
            }
            // Report the resulting cell count, not the number of calls: a zone that exists with zero cells is the
            // exact symptom the player described ("creating a work area does nothing"), and counting attempts hides
            // it. Measured: this line said cells=25 while the map contained a `Zone_Stockpile:0`.
            Log.Message($"[TurboTick script] 存储区已创建 tick={Find.TickManager.TicksGame} "
                + $"attempted={added} actual={zone.CellCount} rect={rect.minX}|{rect.minZ}-{rect.maxX}|{rect.maxZ} "
                + $"zone={zone.ID}");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick script] 存储区创建失败：" + error);
        }
    }

    /// <summary>Designates a few cells at a scripted tick, so the <c>DesignationManager.AddDesignation</c> capture path
    /// is exercised on demand.
    ///
    /// This exists because the reported symptom was "I dragged a mining selection and no marks appear at all", and the
    /// cause was the capture declining while the hook cancelled the vanilla call anyway. A scripted trigger turns that
    /// into a regression case instead of a report from a player: same tick, same cells, every run.</summary>
    internal static void TickDesignations(Map map)
    {
        if (!designationsPending) return;
        if (Find.TickManager is null || Find.TickManager.TicksGame < AtDesignateTick) return;
        designationsPending = false;
        try
        {
            var def = DefDatabase<DesignationDef>.GetNamedSilentFail("Mine");
            if (def is null) { Log.Warning("[TurboTick script] 找不到 Mine 标记定义"); return; }
            var rect = CellRect.CenteredOn(map.Center, DesignateWidth, 1).ClipInsideMap(map);
            var attempted = 0;
            foreach (var cell in rect)
            {
                if (map.designationManager.DesignationAt(cell, def) != null) continue;
                // The same call the designator makes, so the capture path under test is the real one.
                map.designationManager.AddDesignation(new Designation(cell, def));
                attempted++;
            }
            // Report what the manager actually holds, not how many calls were made: the failure being guarded against
            // is exactly "the call was made and the mark is not there".
            Log.Message($"[TurboTick script] 标记已下达 tick={Find.TickManager.TicksGame} "
                + $"attempted={attempted} present={CountDesignations(map, def)} def={def.defName}");
        }
        catch (Exception error) { Log.Error("[TurboTick script] 标记触发失败：" + error); }
    }

    private static int CountDesignations(Map map, DesignationDef def)
    {
        var n = 0;
        foreach (var designation in map.designationManager.AllDesignations) if (designation.def == def) n++;
        return n;
    }

    /// <summary>Places a short row of wall blueprints at a scripted tick, through the player's own designator.
    ///
    /// <para>Exists because the reported defect - "the wall ring never gets built" - could not be reproduced without a
    /// human dragging a selection, and the capture path was silently dead for a whole session while every audit line
    /// was green. A scripted trigger turns "does the build designator capture at all?" into a fixed,
    /// repeatable question that a headless run answers by itself.</para>
    ///
    /// <para>Deliberately goes through <c>Designator_Build.DesignateMultiCell</c>, not through
    /// <c>GenConstruct.PlaceBlueprintForBuild</c>: the first is the gesture the player makes and the one the patch
    /// binds to; the second is the mutation underneath it, which several other callers also reach and which therefore
    /// cannot tell "the player's drag was captured" from "some mod placed a blueprint".</para>
    ///
    /// <para>Gated on <c>TURBOTICK_SCRIPT_BUILD_AT</c> for the automatic form; <see cref="PlaceBuildNow"/> is the same
    /// gesture reachable from the portal, because a container restart reloads the checkpoint and destroys the very
    /// colony state the question is about.</para></summary>
    internal static void TickBuild(Map map)
    {
        if (!BuildPending) return;
        if (Find.TickManager is null || Find.TickManager.TicksGame < AtBuildTick) return;
        buildPending = false;
        var result = PlaceBuild(map, BuildDefName, BuildStuffName, BuildWidth, BuildHeight, "脚本");
        if (!result.Ok) Log.Warning("[TurboTick script] 脚本建造跳过：" + result.Detail);
    }

    /// <summary>The scripted build gesture, callable at any time. Returns the same line it logs, so a portal answer can
    /// carry it instead of asking the operator to read a container's stdout.
    ///
    /// <para>Writes through the player's designator and reports three independent facts: how many cells the designator
    /// accepted (<c>送格</c>), how many blueprints the map gained (<c>前/后</c>), and what the capture counters did.
    /// Those three separate the failures that look identical from a screenshot: vanilla refusing every cell, the
    /// capture prefix declining because the session is not delivering, and the encode producing nothing.</para></summary>
    /// <summary>Creates a zone on the authority, so the zone channel can be measured end to end without a human
    /// dragging one.
    ///
    /// <para>What this answers: a player drew a stockpile and a growing zone on their own client, the overlay drew no
    /// colour, and no reading anywhere said whether the zone existed on the side that simulates. `<c>job-report</c>`'s
    /// `区域同步 … 本端区域=0` was the answer, and this command is how to produce a non-zero case on demand and watch
    /// it arrive on a replica.</para>
    ///
    /// <para>Argument: <c>"stockpile|x,z[,w[,h]]"</c> or <c>"growing|…"</c>. Defaults to a 5×5 stockpile at the map
    /// centre.</para></summary>
    internal static (bool Ok, string Code, string Detail) PlaceZoneAt(string? argument)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return (false, "no-map", "没有当前地图");
            var kind = "stockpile";
            var placement = argument;
            var bar = argument?.IndexOf('|') ?? -1;
            if (bar > 0) { kind = argument![..bar]; placement = argument[(bar + 1)..]; }
            var cell = map.Center;
            var width = 5;
            var height = 5;
            if (placement is { Length: > 0 })
            {
                var pieces = placement.Split(',');
                if (pieces.Length >= 2)
                {
                    if (!int.TryParse(pieces[0], out var x) || !int.TryParse(pieces[1], out var z))
                        return (false, "bad-argument", "坐标必须是整数：" + placement);
                    cell = new IntVec3(x, 0, z);
                }
                if (pieces.Length >= 3 && (!int.TryParse(pieces[2], out width) || width <= 0))
                    return (false, "bad-argument", "宽必须是正整数：" + placement);
                if (pieces.Length >= 4 && (!int.TryParse(pieces[3], out height) || height <= 0))
                    return (false, "bad-argument", "高必须是正整数：" + placement);
            }
            Zone zone = kind switch
            {
                "growing" => new Zone_Growing(map.zoneManager),
                // Fishing was not reachable from this probe either, so the one zone kind whose publisher entry was
                // missing had no way to be observed at all.
                "fishing" => new Zone_Fishing(map.zoneManager),
                _ => new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager)
            };
            map.zoneManager.RegisterZone(zone);
            zone.label = "scripted-" + kind;
            // A non-default repeat count, so the census row can tell "the payload carried it" from "it happens to equal
            // the def default" - 1 would read the same whether or not the field travels.
            if (zone is Zone_Fishing fishingZone) fishingZone.repeatCount = 7;
            var rect = CellRect.CenteredOn(cell, width, height).ClipInsideMap(map);
            var added = 0;
            foreach (var c in rect)
            {
                // `AddCell` is what registers the zone grid cell and dirties the section mesh (`Verse/Zone.cs:231`),
                // which is what ultimately makes `SectionLayer_Zones` draw the colour.
                if (zone.ContainsCell(c)) continue;
                zone.AddCell(c);
                added++;
            }
            var tick = Find.TickManager?.TicksGame ?? -1;
            return (true, "placed", $"指定{cell.x},{cell.z}建区域 tick={tick} 种类={kind} id={zone.ID} "
                + $"尝试{added}格 实际={zone.CellCount}格 范围={rect.minX}|{rect.minZ}-{rect.maxX}|{rect.maxZ}。"
                + "下一帧起应出现在所有副本上（看 job-report 的区域同步）。");
        }
        catch (Exception error)
        {
            return (false, "error", "建区域失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static (bool Ok, string Code, string Detail) PlaceBuildNow() =>
        Current.Game is null || Current.ProgramState != ProgramState.Playing
            ? (false, "no-game", "还没有载入存档")
            : Find.CurrentMap is not { } map
                ? (false, "no-map", "没有当前地图")
                : PlaceBuild(map, BuildDefName, BuildStuffName, BuildWidth, BuildHeight, "手动");

    /// <summary>The same gesture at an explicit centre cell.
    ///
    /// <para>Exists because the fixed centre is not always usable: the map centre was already built over, so the second
    /// scripted run answered `设计器拒绝了全部 5 格`, which is correct and useless. A probe that can only build in one
    /// place stops being a probe as soon as that place is occupied.</para></summary>
    internal static (bool Ok, string Code, string Detail) PlaceBuildAt(int x, int z, int width, int height)
    {
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing)
            return (false, "no-game", "还没有载入存档");
        if (Find.CurrentMap is not { } map) return (false, "no-map", "没有当前地图");
        var rect = CellRect.CenteredOn(new IntVec3(x, 0, z), width, height).ClipInsideMap(map);
        return PlaceBuild(map, BuildDefName, BuildStuffName, width, height, $"指定{x},{z}", rect);
    }

    private static (bool Ok, string Code, string Detail) PlaceBuild(Map map, string defName, string stuffName,
        int width, int height, string source) =>
        PlaceBuild(map, defName, stuffName, width, height, source,
            CellRect.CenteredOn(map.Center, width, height).ClipInsideMap(map));

    private static (bool Ok, string Code, string Detail) PlaceBuild(Map map, string defName, string stuffName,
        int width, int height, string source, CellRect rect)
    {
        try
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def is null) return (false, "no-def", "找不到建造定义 " + defName);
            // A stuffable building with no stuff is not "a wall of unknown material": it is a blueprint that every
            // construction work giver throws on. Refused here for the same reason `DesignatorSync.Apply` refuses it.
            var stuff = def.MadeFromStuff ? DefDatabase<ThingDef>.GetNamedSilentFail(stuffName) : null;
            if (def.MadeFromStuff && stuff is null) return (false, "no-stuff", "找不到材料 " + stuffName);
            // The replay's own designator builder, not a second copy: if the script built it differently the test would
            // be measuring the script instead of the path that runs when a player's gesture is replayed.
            var designator = DesignatorSync.BuildDesignator(def, stuff, 0);
            // Only cells the designator itself would accept, so the script measures the capture path rather than the
            // map's terrain. A refusal here is a legitimate answer and is counted separately from a capture failure.
            var cells = rect.Cells.Where(cell => designator.CanDesignateCell(cell).Accepted).ToList();
            if (cells.Count == 0)
                return (false, "no-cells",
                    $"设计器拒绝了全部 {rect.Area} 格（中心可能已被占）def={def.defName} stuff={stuff?.defName ?? "无"}");
            var before = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count;
            var gesturesBefore = DesignatorSync.CaptureCounters();
            designator.DesignateMultiCell(cells);
            var after = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count;
            // Both numbers matter: `after == before` means vanilla refused every cell, `after > before` with no command
            // on the wire means the capture is dead - and only the pair distinguishes them. `acting` is the third
            // state: the capture prefix declines before it ever encodes when the session is not delivering, which
            // leaves the blueprints in place locally and sends nothing.
            var detail = $"{source}建造 tick={Find.TickManager?.TicksGame ?? -1} def={def.defName} "
                + $"stuff={stuff?.defName ?? "无"} 送格={cells.Count} 蓝图 前{before}/后{after} "
                + $"acting={SharedActions.PlayerActing} 捕获 前[{gesturesBefore}] 后[{DesignatorSync.CaptureCounters()}]";
            Log.Message("[TurboTick script] " + detail);
            // `after == before` is a real finding, not a failure of this helper: it says the designator ran and the map
            // did not change, which is the vanilla half of the defect.
            return after > before ? (true, "placed", detail) : (false, "no-blueprint", detail);
        }
        catch (Exception error)
        {
            return (false, "exception", error.GetType().Name + ": " + error.Message);
        }
    }

    private static readonly int AtBuildTick = Env("TURBOTICK_SCRIPT_BUILD_AT", -1);
    private static readonly bool BuildPendingHolder = AtBuildTick >= 0;
    private static bool buildPending = BuildPendingHolder;
    private static bool BuildPending => buildPending;
    private static readonly int BuildWidth = Env("TURBOTICK_SCRIPT_BUILD_WIDTH", 5);
    private static readonly int BuildHeight = Env("TURBOTICK_SCRIPT_BUILD_HEIGHT", 1);
    private static readonly string BuildDefName =
        Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_BUILD_DEF") is { Length: > 0 } d ? d : "Wall";
    private static readonly string BuildStuffName =
        Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_BUILD_STUFF") is { Length: > 0 } s ? s : "WoodLog";

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}


/// <summary>Counts cells that a zone actually accepted versus the calls that were made.
///
/// A zone that exists with zero cells is what the player experiences as "creating a work area does nothing": the
/// zone object is there, its slot group is registered, and nothing can be stored in it. Counting how many times
/// `AddCell` was called cannot show that, because the call succeeds as a method invocation either way.
/// </summary>
internal static class ZoneCellProbe
{
    private const int Capacity = 32;

    private static readonly List<string> Recent = new(Capacity);

    internal static int Calls;
    internal static int Refused;

    internal static void Record(Zone zone, IntVec3 cell, int before, int after)
    {
        Calls++;
        if (after <= before) Refused++;
        lock (Recent)
        {
            Recent.Add($"zonecell zone={zone?.ID ?? -1} cell={cell.x}|{cell.z} before={before} after={after}");
            while (Recent.Count > Capacity) Recent.RemoveAt(0);
        }
    }

    internal static List<string> Entries()
    {
        lock (Recent) return new List<string>(Recent);
    }

    internal static string Counters() => $"zonecells calls={Calls} refused={Refused}";
}

