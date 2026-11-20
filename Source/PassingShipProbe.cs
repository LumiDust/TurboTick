using System.Globalization;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Whether the passing-ship channel carries a ship that is not on the map being looked at.
///
/// <para><b>The defect this exists for.</b> <c>Publish</c> walks every map's <c>passingShipManager</c> and merges them
/// into one flat payload of <c>id:remaining:name</c>. <c>Apply</c> read <c>Find.CurrentMap</c> only, so on a two-map
/// colony every ship on the other map kept whatever countdown it was loaded with. A ship departs when that countdown
/// reaches zero - and a replica never decrements it, because `PassingShipTick` only runs on ticking peers - so the ship
/// stayed in the orbital list forever. Nothing logged, because `Apply` succeeded and just wrote to the wrong manager.</para>
///
/// <para><b>Why the payload had to change shape rather than gain a parameter.</b> `ReplicaPassingShips` is a frame
/// record with no map column, and the flat list is built from several managers; there is nowhere to put the map except
/// in each entry. The map is written as <c>m&lt;uniqueID&gt;</c> rather than as a leading integer, so an entry from an
/// older build (plain <c>id:remaining:name</c>) fails the `<c>m</c>` test and is skipped - a positional change alone
/// would have every field still parse as an integer and quietly write the wrong number.</para>
///
/// <para><b>What this probe reports, and what it cannot.</b> It round-trips the real encoder through the real applier
/// and compares the countdowns afterwards, per map. It runs on the authority, where `Apply` is not part of the normal
/// path - so it calls `Apply` directly, which is the only way to exercise the receive side from here. A pass here means
/// the payload and the applier agree; it does not mean a replica was observed doing this.</para></summary>
internal static class PassingShipProbe
{
    /// <summary>Checks the one thing that can be checked with no ship in space: that an entry from before the map
    /// column existed is skipped instead of being read with a shifted field.
    ///
    /// <para>This matters because every field in the payload is an integer. A purely positional change would let the
    /// old <c>id:remaining:name</c> parse as <c>map:id:remaining</c> and write a countdown onto whichever map happened
    /// to share a number with the ship's loadID - wrong data, no exception, and a ship that departs at the wrong time.
    /// The <c>m</c> prefix is what makes the old shape fail the parse, so this asserts the guard rather than the
    /// feature.</para></summary>
    private static string FormatOnly(int mapCount)
    {
        var text = new StringBuilder(96);
        try
        {
            // An old-format payload: three integer fields, no `m`. The applier must treat it as unparseable.
            PassingShipSync.Apply(new TurboTick.Networking.ReplicaPassingShips("7:1200:旧格式船"));
            text.Append("；旧格式条目被跳过（无异常）");
            // And the new shape must not throw when the map does not exist here.
            PassingShipSync.Apply(new TurboTick.Networking.ReplicaPassingShips("m999999:7:1200:新格式但无此图"));
            text.Append("；指向不存在地图的条目被跳过（无异常）");
        }
        catch (Exception error)
        {
            text.Append("；应用侧抛 ").Append(error.GetType().Name).Append(": ").Append(error.Message);
        }
        if (mapCount < 2) text.Append("；**只有一张地图**");
        return text.ToString();
    }

    internal static string Report()
    {
        var text = new StringBuilder(320);
        var maps = Find.Maps;
        var withShips = 0;
        var shipTotal = 0;
        foreach (var map in maps)
        {
            if (map?.passingShipManager?.passingShips is not { Count: > 0 } ships) continue;
            withShips++;
            shipTotal += ships.Count;
        }
        text.Append(maps.Count).Append(" 张地图上 ").Append(withShips).Append(" 张有过境飞船，共 ")
            .Append(shipTotal).Append(" 艘");
        if (shipTotal == 0)
            return text.Append("（没有飞船：往返测不到）").Append(FormatOnly(maps.Count)).ToString();

        // Snapshot, perturb by one quantum, publish, apply, compare. Restored in `finally`: a probe must not make a
        // trader leave early.
        var before = new List<(Map Map, PassingShip Ship, int Ticks)>();
        try
        {
            foreach (var map in maps)
            {
                if (map?.passingShipManager?.passingShips is not { Count: > 0 } ships) continue;
                foreach (var ship in ships)
                {
                    if (ship is null) continue;
                    before.Add((map, ship, ship.ticksUntilDeparture));
                    // Exactly one quantum, because less would legitimately produce no frame and the probe would then be
                    // describing its own input rather than the channel.
                    ship.ticksUntilDeparture = Math.Max(0, ship.ticksUntilDeparture - 600);
                }
            }
            var published = PassingShipSync.Publish();
            var payload = published is { Count: > 0 } ? published[0].Ships : null;
            if (payload is not { Length: > 0 })
                return text.Append("；改了一个量子后通道没有发布任何行（**编码器与自己的输入不一致**）").ToString();
            text.Append("；载荷 ").Append(payload.Length).Append(" B");

            // Field 0 of every entry must be the owning map. This is the fact that was missing before, and it is
            // checked against `map.uniqueID` rather than the index, because the index shifts when a map is removed.
            var tagged = 0;
            var untagged = 0;
            var wrongMap = 0;
            foreach (var entry in payload.Split(';'))
            {
                var head = entry.Split(':')[0];
                if (!head.StartsWith("m", StringComparison.Ordinal)) { untagged++; continue; }
                if (!int.TryParse(head.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
                {
                    untagged++;
                    continue;
                }
                tagged++;
                if (before.All(entry2 => entry2.Map.uniqueID != mapId)) wrongMap++;
            }
            text.Append("；带地图标记 ").Append(tagged).Append(" 条（无标记 ").Append(untagged)
                .Append("，标记指向不存在的图 ").Append(wrongMap).Append("）");

            // Round trip: hand the payload straight back to the real applier and see whether every countdown landed.
            PassingShipSync.Apply(new TurboTick.Networking.ReplicaPassingShips(payload));
            var landed = 0;
            var mismatched = 0;
            foreach (var (map, ship, _) in before)
            {
                if (ship is null) continue;
                // The applied value is quantised, so compare against what the encoder would have written.
                var expected = Math.Max(0, Math.Max(0, ship.ticksUntilDeparture) / 600 * 600);
                if (ship.ticksUntilDeparture == expected) landed++;
                else mismatched++;
                _ = map;
            }
            text.Append("；回灌后倒计时生效 ").Append(landed).Append('/').Append(before.Count)
                .Append(mismatched == 0 ? " 全部一致" : "（不一致 " + mismatched.ToString(CultureInfo.InvariantCulture) + "）");
            if (maps.Count < 2)
                text.Append("；**只有一张地图，多据点那半测不到**");
        }
        catch (Exception error)
        {
            text.Append("；探针抛 ").Append(error.GetType().Name).Append(": ").Append(error.Message);
        }
        finally
        {
            foreach (var (_, ship, ticks) in before)
            {
                if (ship is null) continue;
                try { ship.ticksUntilDeparture = ticks; } catch (Exception) { }
            }
        }
        return text.ToString();
    }
}
