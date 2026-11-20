using System;
using System.Collections.Generic;
using TurboTick.Networking;
using Verse;
using Var = Verse;

namespace TurboTick;

/// <summary>Carries the bossgroup summon cooldown, which a replica currently draws as "ready".
///
/// <para><b>The defect (round 140).</b> `Command_CallBossgroup.IsDisabled` reads
/// <c>Find.BossgroupManager.lastBossgroupCalled</c> and computes <c>TicksGame - lastBossgroupCalled</c>; under 120000
/// ticks the gizmo is disabled with "BossgroupAvailableIn" (`_lab/asm/Assembly-CSharp.decompiled.cs:3184-3199`). That
/// method runs from <c>GizmoOnGUI</c>, so <b>every peer draws its own answer</b> - and the only writer,
/// <c>GameComponent_Bossgroup.Notify_BossgroupCalled</c> (`:723953`, called from the summon job's use effect at
/// `:348967`), runs on whichever side executes the action. On a replica the field therefore stays at whatever the save
/// loaded, the arithmetic is enormous, and the gizmo is enabled while the host's is on cooldown.
///
/// <para><b>The starting value is a sentinel, not zero.</b> Measured on the container: the stamp reads `-9999999`
/// before anything is summoned, so `TicksGame - stamp` is enormous and every peer begins with the gizmo enabled. A
/// payload carrying "0" would therefore mean "called at tick zero" and would re-open the cooldown for two days.</para>
///
/// <para><b>Why that is worse than a wrong tooltip.</b> The click is captured and re-run on the authority
/// (`GizmoStatePatch` covers `RimWorld.Command_CallBossgroup`), and the action it runs - `TryStartSummonBossgroupJob`
/// - has no cooldown check of its own: the cooldown exists <i>only</i> in that gizmo. So a non-host player can summon a
/// second bossgroup during the host's two-day cooldown, and the summon really happens.</para>
///
/// <para><b>Why one int and not four.</b> `GameComponent_Bossgroup` scribes four fields (`:723953` region); the audit in
/// `_lab/r140-scribed.txt` checked each for readers outside its own type: `lastBossgroupCalled` has two
/// (`Command_CallBossgroup`, `DebugToolsMisc`), while `timesCalledBossgroups`, `killedBosses` and
/// `killedBossgroupMechs` have none. Only the field something reads travels; carrying counters nobody reads would grow
/// the frame and imply coverage that does not exist.</para></summary>
internal static class BossgroupSync
{
    private static int lastCalled;
    /// <summary>Whether <see cref="lastCalled"/> describes anything. A fresh save holds 0 legitimately, so "0" cannot
    /// mean "never sent"; without this a peer joining an existing colony whose cooldown already ran out would never
    /// receive the value and would compute a cooldown from zero.</summary>
    private static bool sent;

    internal static long ChangesSent { get; private set; }
    internal static long ChangesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>One row when the cooldown stamp moved, otherwise null.</summary>
    internal static ReplicaBossgroup? Publish()
    {
        try
        {
            // `Find.BossgroupManager` is vanilla's own accessor (`:207682`), which keeps the component type out of this
            // file entirely - naming it would need its namespace and would break the moment it moved.
            var manager = Verse.Find.BossgroupManager;
            if (manager is null) return null;
            var current = manager.lastBossgroupCalled;
            if (sent && current == lastCalled) return null;
            sent = true;
            lastCalled = current;
            ChangesSent++;
            // One component per game (`Find.BossgroupManager`), so the frame carries one record rather than a list.
            return new ReplicaBossgroup(current);
        }
        catch (Exception error)
        {
            Var.Log.Warning("[TurboTick] bossgroup publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes the stamp the host's summon set, so this peer's gizmo draws the same cooldown.</summary>
    internal static void Apply(ReplicaBossgroup state)
    {
        try
        {
            // `Find.BossgroupManager` is vanilla's own accessor (`:207682`), which keeps the component type out of this
            // file entirely - naming it would need its namespace and would break the moment it moved.
            var manager = Verse.Find.BossgroupManager;
            if (manager is null) { ApplyFailures++; return; }
            manager.lastBossgroupCalled = state.LastCalled;
            // Keep the local diff baseline in step: otherwise the next publish sees a change that arrived from the
            // host and sends it straight back.
            lastCalled = state.LastCalled;
            sent = true;
            ChangesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            Var.Log.Warning("[TurboTick] bossgroup apply: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Round-trips the cooldown stamp and, when a mechanitor exists, asks vanilla itself.</summary>
    internal static string Probe()
    {
        var manager = Verse.Find.BossgroupManager;
        if (manager is null) return "没有运行中的游戏：首领召唤通道无读数";
        var original = manager.lastBossgroupCalled;
        var now = Verse.Find.TickManager?.TicksGame ?? 0;
        var report = new System.Text.StringBuilder();

        // 1. The publish half. `sent` starts false on a fresh process, so the first Publish must carry the value even
        //    when it equals the previous one - the property this channel's `sent` flag exists for.
        manager.lastBossgroupCalled = now - 100000;
        var row = Publish();
        var encodedOk = row is not null && row.LastCalled == now - 100000;
        report.Append($"原冷却戳 {original} → 设为 {now - 100000}，发出 {(row is null ? 0 : 1)} 行")
              .Append(row is not null ? $"（这一行编码 {row.LastCalled}）" : "")
              .Append(encodedOk ? " → 编码准；" : " → 编码不准；");

        // 2. The apply half, against a different local value so the read-back cannot be coincidence.
        var marker = 1234567;
        manager.lastBossgroupCalled = marker;
        var beforeCount = ChangesApplied;
        if (row is not null) Apply(row);
        var readBack = manager.lastBossgroupCalled;
        report.Append($"先把本端改成 {marker}（不发），应用该行后读回 {readBack} → ")
              .Append(readBack == now - 100000 ? "准" : "不准")
              .Append($"（应用计数 +{ChangesApplied - beforeCount}）；");

        // 3. Vanilla's own gate, when this save has the pawn it needs. `IsDisabled` is private, but it is the whole
        //    question: constructing it here and reading the out-parameter is the same code the gizmo runs.
        var mechanists = new System.Collections.Generic.List<Verse.Pawn>();
        foreach (var map in Verse.Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn?.mechanitor is not null) mechanists.Add(pawn);
        report.Append($"本存档有机械师 {mechanists.Count} 个；");
        if (mechanists.Count == 0)
        {
            report.Append("命令可用性这一半未测（没有机械师：`Command_CallBossgroup.IsDisabled` 需要 mechanitor.Pawn）");
        }
        else
        {
            // Resolved by name rather than `typeof`: the namespace is the very thing that was wrong (round 140), and a
            // compiled reference cannot be checked against the game assembly from this project.
            var commandType = TypeResolver.Find("Command_CallBossgroup");
            var method = commandType is null ? null : HarmonyLib.AccessTools.Method(commandType, "IsDisabled");
            string ReasonFor(int stamp)
            {
                manager.lastBossgroupCalled = stamp;
                var args = new object?[] { null, null };
                if (commandType is not null && method is not null)
                {
                    var command = System.Activator.CreateInstance(commandType, mechanists[0].mechanitor);
                    method.Invoke(command, args);
                }
                return args[1] as string ?? "(无原因)";
            }
            var cooling = ReasonFor(now - 1000);
            var ready = ReasonFor(now - 200000);
            report.Append($"冷却中（{now - 1000}）禁用原因「{cooling}」，过期（{now - 200000}）「{ready}」 → ")
                  .Append(cooling != ready ? "两态可分（命令端读到的是这个字段）" : "两态不可分（读数无效）");
        }

        // Leave the field as it was found: a probe must not hand the game a two-day cooldown.
        manager.lastBossgroupCalled = original;
        var resetRow = Publish();
        if (resetRow is not null) Apply(resetRow);
        report.Append($"；已复原 {original}");
        return report.ToString();
    }

    internal static string Summary() =>
        $"发 {ChangesSent} / 应用 {ChangesApplied}"
        + $"（失败 {ApplyFailures}、当前冷却戳 {(sent ? lastCalled.ToString() : "未发")}）";
}
