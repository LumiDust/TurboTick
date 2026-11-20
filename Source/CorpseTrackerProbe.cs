using System.Text;
using Verse;

namespace TurboTick;

/// <summary>Probe for the unnatural corpse's state machine (round 164).
///
/// <para><b>What it measures.</b> `UnnaturalCorpseTracker` (`:50860-50874`) is state the game persists and the channel
/// used to carry one field of it (`research`, through `ResidualCounterSync`). The probe reads the corpse's awakening
/// tick, its escape flag and its research, changes them, publishes the row, restores the fields *without* publishing so
/// only the row can put them back, applies the row, and reads them again.</para>
///
/// <para><b>Fixture limit.</b> A colony may have no unnatural corpse at all; the probe says so instead of pretending,
/// because "no corpse" and "no channel" would otherwise read the same.</para></summary>
internal static class CorpseTrackerProbe
{
    internal static string Run(string? argument)
    {
        // Fixture first: this save has no unnatural corpse, and "no corpse" would otherwise be indistinguishable
        // from "no channel". The def is generated per race (`UnnaturalCorpse_<race>`), so it is found by its class.
        var spawned = CorpseTrackerSync.SpawnFixtureCorpse(out var fixtureNote);
        try
        {
            if (!CorpseTrackerSync.ProbeRead(out var before, out var awakenTick, out var escape, out var research))
                return before + "；" + fixtureNote;

            var message = new StringBuilder();
            message.Append($"原 {before}；");
            CorpseTrackerSync.ProbeSet(awakenTick + 111, !escape, 0.42f);
            CorpseTrackerSync.ProbeRead(out var changed, out _, out _, out _);
            message.Append($"设为 觉醒戳+111、逃逸取反、研究 0.42 → {changed}；");

            var row = CorpseTrackerSync.Publish();
            if (row is null)
            {
                message.Append("发布为空（通道看不到这具尸体）；");
            }
            else
            {
                var rowSays = row.AwakenTick == awakenTick + 111 && (row.Escape != 0) == !escape
                              && row.Research.ToString("0.###") == "0.42";
                message.Append($"行 觉醒戳={row.AwakenTick} 逃逸={row.Escape} 研究={row.Research:0.###} "
                               + $"（{(rowSays ? "准" : "不准")}）；");
            }

            // Put the old values back without publishing: if the row then restores the new ones, the apply half wrote them.
            CorpseTrackerSync.ProbeSet(awakenTick, escape, research);
            if (row is not null) CorpseTrackerSync.Apply(row);
            CorpseTrackerSync.ProbeRead(out var readBack, out _, out _, out _);
            var correct = row is not null && readBack.Contains($"觉醒戳 {awakenTick + 111}")
                          && readBack.Contains($"逃逸 {(!escape ? "是" : "否")}");
            message.Append($"写回旧值后应用该行，读回 {readBack} → {(correct ? "准" : "不准")}；");

            // The fixture is the player's corpse: leave it exactly as it was found.
            CorpseTrackerSync.ProbeSet(awakenTick, escape, research);
            CorpseTrackerSync.ProbeRead(out var restored, out _, out _, out _);
            message.Append($"已复原（{restored}）；{CorpseTrackerSync.Summary()}");
            return message.ToString();
        }
        finally
        {
            if (spawned) CorpseTrackerSync.RemoveFixtureCorpse();
        }
    }
}
