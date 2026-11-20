using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Simulation;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Human-readable snapshot of what the mod believes is true. Every line comes from state this mod owns,
/// so a player can report a precise failure instead of "it doesn't work".
/// </summary>
public static class Diagnostics
{
    public static string Build()
    {
        var policy = TurboState.Authority.Current;
        var session = SharedColony.Session;
        var text = new StringBuilder();
        text.AppendLine("版本 " + (typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "?"));
        var names = new Dictionary<string, string>
        {
            ["designation"] = "标记", ["designation-remove"] = "撤销标记",
            ["blueprint"] = "建造", ["blueprint-cancel"] = "取消蓝图", ["frame-cancel"] = "取消施工",
            ["draft"] = "征召", ["order"] = "指令", ["forbid"] = "禁用状态", ["priority"] = "工作优先级",
            ["zone-add"] = "划分区域", ["zone-remove"] = "收缩区域", ["zone-delete"] = "删除区域",
            ["bill-add"] = "添加配方", ["bill-delete"] = "删除配方", ["area-assign"] = "指派活动区",
            ["gate"] = "锁步", ["apply"] = "应用", ["transport"] = "装舱",
            ["tradeguard"] = "交易限制", ["caravanguard"] = "远行队限制",
            ["native-controls"] = "原生控速入口", ["native-speed"] = "原生档位", ["native-pause"] = "原生暂停",
            ["simulation-rng"] = "模拟随机流", ["world-load"] = "读档身份", ["world-load-failure"] = "读档失败回传",
            ["shared-snapshot-ids"] = "快照对象 ID", ["shared-respawn"] = "共享恢复范围",
            ["shared-filth"] = "污渍共存恢复", ["environment-state"] = "环境快照状态",
            ["pawn-path-state"] = "人物路径适配", ["pawn-path-resume"] = "路径无损续接",
            ["world-pawns-sort"] = "世界人物有序", ["wander-herd"] = "兽群漫游确定性",
            ["wander-dest"] = "漫游寻路确定性", ["effect-isolation"] = "视听特效隔离"
        };
        text.AppendLine("补丁 " + string.Join(" ", PatchInstaller.Status
            .Select(pair => (names.TryGetValue(pair.Key, out var label) ? label : pair.Key) + (pair.Value ? "✓" : "✗")))
            + (PatchInstaller.AllCritical ? "" : "（不完整，共享殖民地已禁用）"));
        text.AppendLine("房间 " + RoomConnection.Status);
        text.AppendLine("模式 " + (TurboState.Authority.SessionRequested
            ? (TurboState.Authority.SessionConnected ? "联机" : "联机（等待中）")
            : "单机"));
        text.AppendLine($"原生速度 {NativeSpeedSync.Label}（服务端档位={policy.TimeSpeed} 修订={policy.Revision}）");
        text.AppendLine($"会话 {session.Phase} epoch={session.Epoch} 服务器tick={session.Barrier.ServerTick} 本地tick={session.Barrier.AppliedTick} 待执行={session.Commands.Count}");
        text.AppendLine($"TPS 实测 {TurboState.MeasuredTps:0} / 目标 {TurboState.TargetTps:0}{(TurboState.Behind ? "（偏低）" : "")}");
        if (TurboState.Authority.SessionConnected)
        {
            text.AppendLine("专用服务器 " + RoomConnection.Endpoint + " · 部署权限 " + (RoomConnection.IsHost ? "有" : "无"));
            text.AppendLine("检查点所有者 " + (RoomConnection.IsCheckpointOwner ? "是" : "否"));
        }
        if (session.Phase == SharedPhase.Faulted) text.AppendLine("错误 " + session.LastError);
        return text.ToString().TrimEnd();
    }

    private static string Patched(Type type, string method, string label)
    {
        var target = AccessTools.Method(type, method);
        if (target == null) return label + "缺失";
        var info = Harmony.GetPatchInfo(target);
        return info != null && info.Owners.Contains("lumidust.turbotick") ? label + "✓" : label + "✗";
    }
}
