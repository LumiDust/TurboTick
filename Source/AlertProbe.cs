using System;
using System.Collections.Generic;
using Verse;

namespace TurboTick;

/// <summary>Which alerts cannot answer yet because the world clock has not reached their threshold.
///
/// <para><b>Why a table and not a reflection pass (round 96).</b> Four shipped alerts open <c>GetReport()</c> with a
/// world-clock floor: <c>Alert_LowFood</c> (<c>Assembly-CSharp.decompiled.cs:611350</c>), <c>Alert_LowBabyFood</c>
/// (<c>:611408</c>) and <c>Alert_LowMedicine</c> (<c>:611683</c>) all use <c>150000</c>, and
/// <c>Alert_NeedHoldingPlatform</c> uses <c>240000</c> (<c>:615911</c>). None of them exposes the constant - the method
/// returns a bool and the reason lives inside the body - so the only honest sources are the decompiled text and a table
/// taken from it.</para>
///
/// <para>A table is right rather than brittle here because <see cref="Tests"/> cross-checks it against the dump: the test
/// re-parses every <c>Alert_*</c> class with a <c>TicksGame</c> comparison and fails if the table and the dump disagree
/// in either direction. So a stale entry is a build failure, not a silently wrong reading. The mod also cannot read the
/// dump at runtime - it runs on a Linux server that has none.</para>
///
/// <para><b>What the defect was.</b> An observed world was at tick 358. All four alerts printed 「假」, and a colony with
/// an empty larder prints 「假」 for the same reason. The reading said "no problem" and could not have distinguished that
/// from "not yet measurable", which is the same shape as the round-80 war path: present, wired, structurally unobservable.
/// </para></summary>
internal static class AlertProbe
{
    /// <summary>Alert type name to the world tick its <c>GetReport()</c> requires before it can answer at all.</summary>
    private static readonly Dictionary<string, int> Floors = new(StringComparer.Ordinal)
    {
        ["Alert_LowFood"] = 150000,
        ["Alert_LowBabyFood"] = 150000,
        ["Alert_LowMedicine"] = 150000,
        ["Alert_NeedHoldingPlatform"] = 240000,
    };

    /// <summary>The floor for an alert type, or 0 when it can answer immediately. 0 means "no floor", not "unknown" -
    /// an alert that is not in the table is one the dump has no <c>TicksGame</c> comparison for, and the test keeps that
    /// claim true.</summary>
    internal static int FloorOf(string typeName) => Floors.TryGetValue(typeName, out var floor) ? floor : 0;

    /// <summary>One line naming every floor and whether the clock has reached it, for the probe's header. Printed even
    /// when nothing is below its floor, because "all measurable" and "the table is empty" are different states and only
    /// one of them is good news.</summary>
    internal static string Describe(int nowTick)
    {
        if (Floors.Count == 0) return "时钟判据：表为空（没有告警受世界时钟限制）";
        var parts = new List<string>(Floors.Count);
        var below = 0;
        foreach (var pair in Floors)
        {
            var reached = nowTick >= pair.Value;
            if (!reached) below++;
            parts.Add(pair.Key + "=" + pair.Value + (reached ? "✓" : "×"));
        }
        return $"时钟判据（现在 {nowTick}）：{string.Join(" ", parts)}"
            + (below > 0 ? $"；{below} 个未到判据，它们的「假」不构成证据" : "；全部可判");
    }
}
