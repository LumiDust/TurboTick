using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Round-trips the flight channel.
///
/// <para><b>Why an idempotent round trip and not a count.</b> The failure this channel fixes is invisible in any counter:
/// the tracker's five fields are private and the only public accessor, <c>Flying</c> (<c>:729105</c>), collapses four
/// states into one boolean. A probe that printed "N pawns flying" would pass whether or not the channel could put the
/// value back. So this takes each pawn's live encoding, applies it through the real <see cref="FlightSync.Apply"/> path,
/// re-reads the tracker, and compares the two strings - the same shape as `training-probe` and `gene-probe`.</para>
///
/// <para><b>What it still cannot show.</b> A pawn that is genuinely mid-flight is rare in a test colony, and this save is
/// unlikely to have one, so the interesting states may not be exercised. The probe reports the state names it actually saw
/// rather than claiming coverage it does not have; when the only samples are `grounded`, that is what it says.</para></summary>
internal static class FlightProbe
{
    internal static string Probe(string? argument)
    {
        var report = new StringBuilder();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkedPawns = 0;
        var matching = 0;
        var differing = new List<string>();
        var roundTripped = 0;
        try
        {
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var before = FlightSync.Describe(pawn);
                    if (before is null) continue;
                    checkedPawns++;
                    var state = before.Split(':')[0];
                    seen[state] = seen.TryGetValue(state, out var n) ? n + 1 : 1;
                    if (checkedPawns > 40) continue;
                    if (!AuthoritativeReplica.FindPawnAny(pawn!.thingIDNumber, out var live)) continue;
                    // Feed the pawn's own reading back through Apply: this is what a replica would do, and it must be a
                    // no-op on a tracker that already holds that state.
                    FlightSync.Apply(new TurboTick.Networking.ReplicaFlight(pawn.thingIDNumber, before));
                    roundTripped++;
                    var after = FlightSync.Describe(live);
                    if (string.Equals(before, after, StringComparison.Ordinal)) matching++;
                    else if (differing.Count < 4) differing.Add($"#{pawn.thingIDNumber} {before} → {after}");
                }
        }
        catch (Exception error)
        {
            return "飞行往返失败：" + error.GetType().Name + ": " + error.Message;
        }
        if (checkedPawns == 0) return "0 个带飞行追踪器的小人，飞行通道测不到";
        var states = new List<string>();
        foreach (var pair in seen) states.Add(pair.Key + "×" + pair.Value);
        report.Append($"{checkedPawns} 个小人带飞行追踪器（{string.Join("，", states)}）");
        report.Append($";往返 {roundTripped} 个：一致 {matching}"
            + (differing.Count > 0 ? $"，**不一致 {differing.Count}**（{string.Join("；", differing)}）" : "，全部一致"));
        if (!seen.ContainsKey("flying") && !seen.ContainsKey("takingoff") && !seen.ContainsKey("landing"))
            report.Append("；**样本里没有在飞的小人 —— 「飞行中」那三个状态的真实往返测不到**");
        report.Append($";发 {FlightSync.StatesSent} / 应用 {FlightSync.StatesApplied}");
        report.Append(argument is { Length: > 0 } ? $"；参数 {argument} 未使用" : "");
        return report.ToString();
    }
}
