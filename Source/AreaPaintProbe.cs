using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Exercises the allowed-area paint path on demand, because a path that is installed and never fired looks
/// exactly like one that works.
///
/// <para><b>The question this answers.</b> `DesignatorSync.EncodeChunks` used to return an empty chunk list for every
/// <c>Designator_Cells</c> subclass that was not <c>Designator_Build</c> or <c>Designator_ZoneAdd</c> - and
/// <c>Designator_AreaAllowedExpand</c>/<c>Clear</c> are exactly that shape. Nothing in the log could have said so: a
/// gesture with no capture and a gesture the player never made produce the same zero. So this probe commits a real stroke
/// through the real encoder and reports how many commands came out, then replays them through the real decoder so the
/// round trip is proven rather than assumed.</para>
///
/// <para><b>Why it has to run the designator, not a hand-built payload.</b> The encoder reads
/// <c>Designator_AreaAllowed.SelectedArea</c>, a static set by the area-list float menu. A probe that constructed its own
/// payload would prove the codec and nothing about the capture - which is the half that was broken.</para></summary>
internal static class AreaPaintProbe
{
    internal static string Probe(string? argument)
    {
        var map = Find.CurrentMap;
        if (map is null) return "没有当前地图";
        var manager = map.areaManager;
        if (manager?.AllAreas is null) return "地图没有区域管理器";

        var targets = new System.Collections.Generic.List<Area_Allowed>();
        foreach (var candidate in manager.AllAreas)
            if (candidate is Area_Allowed allowed) targets.Add(allowed);
        if (targets.Count == 0) return "本图没有 Area_Allowed（自建区域 0 个），没有可涂的目标";

        var text = new StringBuilder();
        text.Append($"自建区域 {targets.Count} 个；");
        var area = targets[0];
        // The static the encoder reads. Set here and restored afterwards so the probe leaves the UI state as it found it.
        var previous = Designator_AreaAllowed.SelectedArea;
        Designator_AreaAllowed.selectedArea = area;
        try
        {
            if (Designator_AreaAllowed.SelectedArea != area)
                return "无法设置 Designator_AreaAllowed.SelectedArea（字段名变更？），编码器读不到目标";
            // A cell the area does not already hold, so the stroke is a real change rather than a no-op.
            var wanted = default(IntVec3);
            var found = false;
            for (var x = 0; x < map.Size.x && !found; x++)
                for (var z = 0; z < map.Size.z && !found; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!area[cell]) { wanted = cell; found = true; }
                }
            if (!found) return $"「{area.Label}」已覆盖整张地图，没有可加的格子";

            var expand = new Designator_AreaAllowedExpand();
            var chunks = DesignatorSync.EncodeChunks(expand, [wanted]);
            text.Append($"涂加 1 格 → 命令 {chunks.Count} 条");
            if (chunks.Count == 1)
            {
                var payload = Encoding.UTF8.GetString(chunks[0].Payload);
                var decoded = Simulation.CommandCodec.TryDecodeAreaPaint(chunks[0].Payload,
                    out var label, out var add, out var mapIndex, out var cells);
                text.Append(decoded
                    ? $"；解码 标签「{label}」方向 {(add ? "加" : "减")} 地图 {mapIndex} 格子 {cells.Count} 个"
                    : "；解码 **失败**");
                if (decoded && cells.Count == 1)
                {
                    // Apply on this peer too, so the probe proves the replay half as well. The origin applies its own
                    // stroke in vanilla; here it is done by hand because there is no room to round-trip through a server.
                    DesignatorSync.ApplyPaint(payload);
                    text.Append(area[wanted] ? "；重放后该格已置位" : "；重放后该格 **仍未置位**");
                }
                if (chunks[0].Kind != Simulation.CommandCodec.AreaPaint)
                    text.Append($"；**kind 是 {chunks[0].Kind}，会被副本当作生命周期命令丢弃**");
            }
            else
            {
                // Zero is the defect this whole file exists for; more than one would mean the split is mis-sized.
                text.Append(chunks.Count == 0
                    ? $"（**缺陷**：EncodeChunks 没有为 Designator_AreaAllowedExpand 产出任何命令）"
                    : "（格子数超过单条上限，被拆成多条）");
            }
        }
        finally { Designator_AreaAllowed.selectedArea = previous; }

        var clear = new Designator_AreaAllowedClear();
        Designator_AreaAllowed.selectedArea = area;
        try
        {
            var chunks = DesignatorSync.EncodeChunks(clear, [new IntVec3(0, 0, 0)]);
            if (chunks.Count == 1 && Simulation.CommandCodec.TryDecodeAreaPaint(chunks[0].Payload,
                    out _, out var add, out _, out _))
                text.Append(add
                    ? "；**减格设计器编码成了加格**"
                    : "；减格设计器编码方向正确");
            else text.Append("；减格设计器未产出命令");
        }
        finally { Designator_AreaAllowed.selectedArea = previous; }
        return text.ToString();
    }
}
