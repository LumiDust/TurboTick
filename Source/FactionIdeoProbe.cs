using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboTick.Networking;
using Verse;
using RimWorld;

namespace TurboTick;

/// <summary>Round-trips each faction's belief set through the wire shape and back.
///
/// <para><b>Why an idempotent round trip and not a count.</b> A count of "sent 23, applied 23" proves the records moved
/// and nothing about whether they say the right thing - the two encoders could agree with each other and be wrong
/// together. Feeding a faction's own belief back through `ApplyIdeo` and re-encoding it compares the encoder against
/// itself only if the apply step is a no-op; if the apply step writes a different value than it read, the re-encode
/// differs and the probe says so.</para>
///
/// <para><b>What it deliberately does not do.</b> It does not run
/// `RecalculateIdeosBasedOnPlayerPawns` to check the answer. That method picks the primary by believer count and fires
/// two letters, so using it as the oracle would make the probe mutate the thing it measures.</para></summary>
internal static class FactionIdeoProbe
{
    /// <summary>Round-trips every faction that has a belief, and reports separately the two facts a single number would
    /// conflate: how many factions claim a primary at all, and how many round-trips came back unchanged.</summary>
    internal static string Run()
    {
        try
        {
            var factions = Find.FactionManager?.AllFactionsListForReading;
            if (factions is null || factions.Count == 0) return "没有派系管理器";
            var withTracker = 0;
            var withPrimary = 0;
            var minorRows = 0;
            var tried = 0;
            var same = 0;
            var differ = 0;
            var failures = 0;
            var samples = new List<string>();
            foreach (var faction in factions)
            {
                if (faction?.ideos is null) continue;
                withTracker++;
                var primary = faction.ideos.PrimaryIdeo;
                if (primary is null) continue;
                withPrimary++;
                var minors = faction.ideos.IdeosMinorListForReading;
                var minorIds = new List<int>();
                if (minors is not null)
                {
                    foreach (var minor in minors)
                    {
                        if (minor is null || minor == primary) continue;
                        minorIds.Add(minor.id);
                    }
                }
                minorRows += minorIds.Count;
                minorIds.Sort();
                var before = Encode(primary.id, minorIds);
                var payload = new ReplicaFactionIdeo(faction.loadID, primary.id,
                    string.Join(",", minorIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
                tried++;
                try
                {
                    FactionSync.ApplyIdeo(payload);
                }
                catch (Exception)
                {
                    failures++;
                    continue;
                }
                var afterPrimary = faction.ideos.PrimaryIdeo?.id ?? 0;
                var afterMinors = new List<int>();
                var afterList = faction.ideos.IdeosMinorListForReading;
                if (afterList is not null)
                {
                    foreach (var minor in afterList)
                    {
                        if (minor is null || minor == faction.ideos.PrimaryIdeo) continue;
                        afterMinors.Add(minor.id);
                    }
                }
                afterMinors.Sort();
                var after = Encode(afterPrimary, afterMinors);
                if (before == after) same++;
                else
                {
                    differ++;
                    if (samples.Count < 4) samples.Add($"{faction.def?.defName}: {before} → {after}");
                }
            }
            var verdict = differ == 0 && failures == 0
                ? (tried == 0 ? "没有可往返的派系" : "全部一致")
                : $"{differ} 个不一致" + (failures > 0 ? $"，{failures} 个抛异常" : string.Empty);
            var detail = samples.Count > 0 ? "；样本 " + string.Join(" | ", samples) : string.Empty;
            return $"信念往返：{verdict}（往返 {tried} 个：一致 {same}）；"
                + $"{withTracker} 个派系有 tracker，{withPrimary} 个有主信念（次信念共 {minorRows} 条）{detail}";
        }
        catch (Exception error)
        {
            return "信念探针失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>`primary;minor,minor` - the same string the channel compares, rebuilt here on purpose. Calling the
    /// channel's own encoder would make the probe assert "the encoder equals itself".</summary>
    private static string Encode(int primary, List<int> minors) =>
        primary.ToString(CultureInfo.InvariantCulture) + ";"
        + string.Join(",", minors.Select(id => id.ToString(CultureInfo.InvariantCulture)));
}
