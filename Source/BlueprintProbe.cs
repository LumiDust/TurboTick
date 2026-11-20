using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Replays a building-blueprint command on the authority every few hundred ticks, off unless
/// <c>TURBOTICK_PROBE_BLUEPRINT</c> names a start tick.
///
/// <para>Why this exists: the client's own click produced <c>commands=designator:2/0</c>, the authority logged taking
/// both commands, <c>重放设计器失败</c> was never printed, and no blueprint existed. "No exception" only proves the
/// method returned - <c>Designator_Build.DesignateSingleCell</c> can decide the strip is unwalkable, the tutor guard
/// can return before touching the map, and <c>Find.CurrentMap</c> can be a different map than the one the cells belong
/// to. All three look identical from outside, so the question has to be asked at a known tick with per-cell answers.</para>
///
/// <para>The command goes through <see cref="SharedActions.Apply"/> rather than calling <see cref="DesignatorSync.Apply"/>
/// directly: the switch arm is part of the path under test, and an unbound <c>case</c> would look exactly like a
/// designator that refuses every cell.</para>
///
/// <para>Only on the authority, because that is where the client's command is delivered and where the blueprint has to
/// exist for the frame to carry it to anyone else.</para>
/// </summary>
internal static class BlueprintProbe
{
    private static readonly int StartTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_BLUEPRINT"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var start) ? start : -1;

    private static readonly int Gap = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_BLUEPRINT_GAP"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var gap) && gap > 0 ? gap : 300;

    private static int nextTick = -1;
    private static int sent;

    internal static void Tick()
    {
        if (StartTick < 0) return;
        if (!HeadlessRuntime.ServerMode) return;
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing) return;
        var manager = Find.TickManager;
        if (manager is null) return;
        var now = manager.TicksGame;
        if (now < StartTick) return;
        if (nextTick < 0) nextTick = StartTick;
        if (now < nextTick) return;
        nextTick = now + Gap;

        var map = Find.CurrentMap;
        if (map is null)
        {
            Log.Warning("[TurboTick probe] 蓝图探针：没有当前地图（Find.CurrentMap 为空，重放会静默不落）");
            return;
        }

        // What became of the blueprints the previous round placed. This is the measurement the whole defect was missing:
        // "有蓝图 117" is measured one line after the placement and "本端蓝图 0" is measured on the frame path, and the
        // only thing between them is time. A reference held across the gap turns "it vanished" into one of three
        // answerable states: destroyed, despawned (removed from the lister but alive), or alive and uncounted.
        if (DesignatorSync.LastPlaced.Count > 0)
        {
            var alive = 0;
            var destroyed = 0;
            var despawned = 0;
            var listed = 0;
            foreach (var blueprint in DesignatorSync.LastPlaced)
            {
                if (blueprint.Destroyed) { destroyed++; continue; }
                alive++;
                if (!blueprint.Spawned) { despawned++; continue; }
                if (blueprint.Map?.listerThings.AllThings.Contains(blueprint) == true) listed++;
            }
            Log.Message($"[TurboTick probe] 上一轮蓝图 {DesignatorSync.LastPlaced.Count} 个："
                + $"存活 {alive}（其中已离图 {despawned}）/ 已销毁 {destroyed}，仍在 lister 里 {listed} 个。"
                + $"地图上现有蓝图 {map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count} 个。");
        }

        var wall = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
        if (wall is null)
        {
            Log.Warning("[TurboTick probe] 蓝图探针：找不到 Wall，无法构造 payload");
            return;
        }

        // A stuffable building with no stuff is refused by our own guard, and the default stuff depends on the map's
        // resource counter - both are exactly what Encode sends, so the probe sends the same thing.
        var stuff = wall.MadeFromStuff ? GenStuff.DefaultStuffFor(wall) : null;
        if (wall.MadeFromStuff && stuff is null)
        {
            Log.Warning("[TurboTick probe] 蓝图探针：Wall 需要材料但没有默认材料");
            return;
        }

        var payload = new StringBuilder();
        // Field 4 is the map index, matching `DesignatorSync.EncodeOne`. The probe builds the payload by hand on purpose -
        // going through the encoder would only prove the encoder agrees with itself - so this has to be kept in step with it
        // by hand too, and a mismatch here reads as "the replay silently did nothing".
        payload.Append("build|").Append(wall.defName).Append('|').Append(stuff?.defName ?? "").Append('|')
            .Append(Rot4.North.AsInt).Append('|').Append(map.uniqueID);
        // Three cells in a row rather than one: a single placement cannot tell "the designator refuses this spot" from
        // "only the later cells of a drag are lost", and the second is the defect that was reported for dragging.
        var cells = 0;
        foreach (var cell in GenRadial.RadialCellsAround(map.Center, 8f, true))
        {
            if (!cell.InBounds(map)) continue;
            if (!GenConstruct.CanPlaceBlueprintAt(wall, cell, Rot4.North, map, false, null, null, stuff).Accepted) continue;
            payload.Append('|').Append(cell.x).Append(',').Append(cell.z);
            if (++cells >= 3) break;
        }
        if (cells == 0)
        {
            Log.Warning("[TurboTick probe] 蓝图探针：地图中心附近找不到能放 Wall 的格子");
            return;
        }

        sent++;
        SharedActions.Apply(new GameCommand(sent, now, CommandCodec.Designator,
            Encoding.UTF8.GetBytes(payload.ToString())));
        Log.Message($"[TurboTick probe] 蓝图探针 tick={now} 第 {sent} 条 designator（{cells} 格，地图 {map.Index}），"
            + DesignatorSync.Summary() + "；" + HeadlessRuntime.BlueprintSummary());
    }
}
