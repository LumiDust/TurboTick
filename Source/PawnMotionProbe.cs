using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Answers "is the pawn standing still, or is the picture of it standing still?" from the authority's own
/// state, on demand.
///
/// <para>Why: the player reported that colonists and animals do not move at all, while their needs, moods and jobs do
/// change. Those two facts together rule out "the world is paused" and rule out "the replica is not receiving frames"
/// (it logs `toil 一致 72/落后 0` every few seconds), and leave exactly one question: does the authority's own
/// <c>Pawn_PathFollower</c> advance? Position alone cannot answer it - a pawn in a 60-tick <c>Wait_Wander</c> is
/// legitimately still - so this prints the path follower's own bookkeeping next to the position, twice, a few seconds
/// apart (<c>pawn-report</c> then <c>pawn-report-again</c>).</para></summary>
internal static class PawnMotionProbe
{
    private static readonly Dictionary<int, (int X, int Z, int JobStartTick, int PathNode)> previous = [];

    /// <summary>Prints the roster and remembers it. The second call says what changed, so a single answer cannot be
    /// mistaken for a measurement.</summary>
    internal static (bool Ok, string Code, string Detail) Report(bool compare)
    {
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing)
            return (false, "no-game", "还没有载入存档");
        if (Find.CurrentMap is not { } map) return (false, "no-map", "没有当前地图");
        try
        {
            var text = new StringBuilder();
            var now = Find.TickManager?.TicksGame ?? -1;
            text.Append(compare ? "对照" : "首测").Append(" tick=").Append(now);
            // The tick counter moving is not the same as the tick *reaching pawns*: a world can advance while a patch
            // skips the pawn list. Both numbers are printed so the two can never be confused again.
            text.Append(" 引擎tick=").Append(Find.TickManager?.TicksGame ?? -1);
            text.Append(" 物体=").Append(map.listerThings.AllThings.Count);
            var all = map.mapPawns.AllPawnsSpawned;
            text.Append(" 角色=").Append(all.Count);
            text.Append(" 殖民者=").Append(map.mapPawns.FreeColonistsSpawnedCount);
            text.Append(" 动物=").Append(all.Count(p => p.RaceProps?.Animal == true));
            var moved = 0;
            var pathing = 0;
            foreach (var pawn in all)
            {
                if (pawn.RaceProps?.Animal != true && !pawn.IsColonist) continue;
                var pather = pawn.pather;
                var moving = pather?.MovingNow == true;
                var nodes = moving ? PathNodes(pawn) : 0;
                if (moving) pathing++;
                var changed = "";
                var key = pawn.thingIDNumber;
                if (compare && previous.TryGetValue(key, out var before))
                {
                    var dx = pawn.Position.x - before.X;
                    var dz = pawn.Position.z - before.Z;
                    if (dx != 0 || dz != 0) moved++;
                    // A job that advances while the cell does not is the exact signature of "the tick reaches the
                    // brain and not the legs" - the branch `PatherTick` is what moves a pawn, and it is called from
                    // `Pawn.Tick` right after the job tracker.
                    changed = $" Δ=({dx},{dz})";
                }
                previous[key] = (pawn.Position.x, pawn.Position.z, 0, nodes);
                text.Append(Environment.NewLine).Append("  ").Append(pawn.LabelShort)
                    .Append(' ').Append(pawn.def.defName)
                    .Append(" @").Append(pawn.Position.x).Append(',').Append(pawn.Position.z)
                    .Append(" 移动=").Append(moving ? "是" : "否")
                    .Append(" 路径节点=").Append(nodes)
                    .Append(" 进度=").Append((pather?.MovePercentage ?? -1f).ToString("0.00"))
                    // The four numbers that separate "the pather is not called" from "the pather is called and pays
                    // nothing". `LastMovedTick` ancient while `tick` advances means `PatherTick` never completes a
                    // cell; a small `nextCellCostLeft` that stays constant means the cost is never decremented,
                    // which is what a patched clock or a skipped tick would look like. Measured need: 1500 ticks
                    // elapsed and every pawn moved at most one cell, which neither a stopped pather nor a working
                    // one explains on its own.
                    .Append(" 上次移动tick=").Append(pather?.LastMovedTick ?? -999)
                    .Append(" 本格剩余=").Append((pather?.nextCellCostLeft ?? -1f).ToString("0.0"))
                    .Append('/').Append((pather?.nextCellCostTotal ?? -1f).ToString("0.0"))
                    .Append(" 下次格=").Append(pather is null ? "?" : pather.nextCell.x + "," + pather.nextCell.z)
                    .Append(" 到期job=").Append(pawn.CurJob?.expiryInterval ?? -1)
                    .Append(" job已跑=").Append(pawn.jobs?.curDriver?.ticksLeftThisToil ?? -1)
                    .Append(" job=").Append(pawn.CurJobDef?.defName ?? "无")
                    .Append(" toil=").Append(pawn.jobs?.curDriver?.CurToilIndex ?? -1)
                    // Whether the pawn can act at all. Measured need: every colonist was running `Wait_Downed` and the
                    // construction report read "nothing is being built" - which is the correct report for a colony
                    // whose only three workers are unconscious, and a completely different finding from a job-giver
                    // defect. `移动=否` alone cannot tell those apart.
                    .Append(" 倒地=").Append(pawn.Downed ? "是" : "否")
                    // `GetPosture()` is the value the renderer reads (`RimWorld/PawnUtility.cs:265`), and a body lying
                    // on the ground is drawn rotated - which is exactly the "characters are upside down" report. It is
                    // `jobs.posture` whenever the pawn is not downed, so printing it names the field that went wrong
                    // instead of leaving it to be guessed from the picture.
                    .Append(" 姿势=").Append(pawn.GetPosture())
                    .Append(" 能醒=").Append(pawn.health?.capacities?.CanBeAwake == true ? "是" : "否")
                    .Append(" 食物=").Append((pawn.needs?.food?.CurLevel ?? -1f).ToString("0.00"))
                    .Append(" 休息=").Append((pawn.needs?.rest?.CurLevel ?? -1f).ToString("0.00"))
                    .Append(" 心情=").Append((pawn.needs?.mood?.CurLevel ?? -1f).ToString("0.00"))
                    .Append(" 饥饿=").Append(pawn.health?.hediffSet?.HasHediff(HediffDefOf.Malnutrition) == true ? "是" : "否")
                    .Append(" 目标=").Append(DescribeTarget(pawn.CurJob?.targetA ?? LocalTargetInfo.Invalid))
                    .Append(changed);
            }
            if (compare)
                text.Append(Environment.NewLine).Append("对照结果：位移角色 ").Append(moved)
                    .Append(" 个，正在走 ").Append(pathing).Append(" 个（共 ")
                    .Append(all.Count(p => p.RaceProps?.Animal == true || p.IsColonist)).Append(" 个参评）");
            return (true, "ok", text.ToString());
        }
        catch (Exception error)
        {
            return (false, "exception", error.GetType().Name + ": " + error.Message);
        }
    }

    private static int PathNodes(Pawn pawn)
    {
        // `curPath` is a public field on `Pawn_PathFollower`; `NodesLeftCount` is what makes "walking" countable.
        // A follower can report `MovingNow` true while the path is exhausted, so the count is the honest number.
        try { return pawn.pather?.curPath?.NodesLeftCount ?? 0; }
        catch (Exception) { return -1; }
    }

    private static string DescribeTarget(LocalTargetInfo target) =>
        target.HasThing ? (target.Thing.def?.defName ?? "?") + "#" + target.Thing.thingIDNumber
            : target.IsValid ? "格" + target.Cell.x + "," + target.Cell.z
            : "无";
}
