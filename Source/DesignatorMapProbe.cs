using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Checks that a designator replay can actually be aimed at a map other than the current one.
///
/// <para><b>The defect this measures.</b> `Designator.Map` is a getter over `Find.CurrentMap` (`Designator.cs:162502`), and
/// `DesignateSingleCell` / `DesignateMultiCell` both read it. On a one-map colony that is always correct, so nothing showed
/// it. With two maps - a multi-settlement game - a player editing map 3 while the authority is looking at map 0 had their
/// gesture replayed on map 0: the same coordinates exist there, so there was no error and no wrong-looking log, only a
/// building in the wrong colony. Cancel drags were refused rather than mis-applied; builds and zone drags were not.</para>
///
/// <para><b>What is measured.</b> Four separate facts, because they fail independently:</para>
/// <list type="number">
/// <item>does the build payload carry a map index at the field position the decoder reads (round-tripped through the real
/// encoder and the real decoder, so an offset drift shows up);</item>
/// <item>does the zone payload carry one too;</item>
/// <item>can <see cref="DesignatorSync.CurrentMapScope"/> move `Find.CurrentMap` and put it back;</item>
/// <item>does a designator built while the scope is held report the scoped map from its own <c>Map</c> getter.</item>
/// </list>
///
/// <para><b>What it cannot measure.</b> It cannot create a second map: `MapGenerator` needs a full world and a colony
/// bootstrap, and manufacturing one to test a getter would be a larger experiment than the getter itself. So on a
/// single-map save the map-identity half is reported as 「同一张地图，是空操作，测不到」 rather than as a pass - the
/// same discipline `designator-coverage` needed after reporting a confident zero that only meant "the capture path does
/// not run here".</para></summary>
internal static class DesignatorMapProbe
{
    internal static string Probe(string? argument)
    {
        var report = new StringBuilder();
        // The target has to be a map that is *not* the current one, or the scope is a no-op and the test proves nothing.
        // Rounds 67 and 68 both ended with 「单图：空操作，测不到」 for exactly this reason. Now that `second-map` exists
        // there is something to switch to, so the non-current map is preferred and the current one is only a fallback.
        var current = Find.CurrentMap;
        var other = Find.Maps.FirstOrDefault(candidate => candidate is not null && candidate != current);
        var map = other ?? current;
        if (map is null) return "没有地图，设计器地图通道测不到";
        report.Append(other is null
            ? "目标=当前地图（**没有第二张地图，作用域仍是空操作**）；"
            : $"目标=地图 {map.uniqueID}（不是当前地图 {current?.uniqueID}，作用域会真的移动）；");

        // 1. Build payload shape. Encode a real `Designator_Build` and read the map field back out of the payload the same
        //    way Apply does, rather than trusting a hand-built string.
        var building = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
        Designator? designator = null;
        if (building is not null)
        {
            try
            {
                var build = new Designator_Build(building);
                designator = build;
            }
            catch (Exception error)
            {
                report.Append($"构造 Designator_Build 失败（{error.GetType().Name}）；");
            }
        }
        if (designator is not null)
        {
            // Encoded **inside** the scope. `Designator.Map` is `=> Find.CurrentMap` (`Designator.cs:162502`), so a
            // designator asked for its chunks while a different map is current answers about that other map. In real use
            // the player is on the map they are designating on, so the scope is only needed on the replay side - encoding
            // outside it would test a situation that cannot occur and would report the encoder as broken.
            List<TurboTick.Networking.GameCommand> chunks;
            using (DesignatorSync.CurrentMapScope.Enter(map))
                chunks = DesignatorSync.EncodeChunks(designator, [map.Center]);
            if (chunks.Count == 0)
            {
                report.Append("建筑载荷 **没有产出命令**；");
            }
            else
            {
                var text = Encoding.UTF8.GetString(chunks[0].Payload);
                var parts = text.Split('|');
                var field = parts.Length > 4 ? parts[4] : "<缺>";
                // `uniqueID`, not `Index`: the payload carries the stable id (see `DesignatorSync.FindMapByUniqueId`).
                var ok = int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                         && parsed == map.uniqueID;
                report.Append($"建筑载荷字段4={field}（应={map.uniqueID}，{parts.Length} 段）").Append(ok ? " ✓" : " **与地图不符**").Append("；");
            }
        }

        // 2. Zone payload shape, through the same public encoder entry.
        var zoneDesignator = FindAnyZoneDesignator();
        if (zoneDesignator is null)
            report.Append("找不到区域设计器，zone 载荷测不到；");
        else
        {
            // Inside the scope for the same reason as the build payload: `EncodeZone` reads `designator.Map`, which is
            // `Find.CurrentMap`, so encoding on a different map would write that map's id and the field assertion below
            // would compare the payload against a map the encoder never saw.
            List<TurboTick.Networking.GameCommand> chunks;
            using (DesignatorSync.CurrentMapScope.Enter(map))
                chunks = DesignatorSync.EncodeChunks(zoneDesignator, [map.Center]);
            if (chunks.Count == 0) report.Append("区域载荷 **没有产出命令**；");
            else
            {
                var text = Encoding.UTF8.GetString(chunks[0].Payload);
                var parts = text.Split('|');
                var field = parts.Length > 2 ? parts[2] : "<缺>";
                var ok = int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                         && parsed == map.uniqueID;
                report.Append($"区域载荷字段2={field}（应={map.uniqueID}，{parts.Length} 段）").Append(ok ? " ✓" : " **与地图不符**").Append("；");
            }
        }

        // 3. The scope itself, observed from the outside: before / inside / after.
        var before = Find.CurrentMap?.Index ?? -1;
        int inside;
        using (DesignatorSync.CurrentMapScope.Enter(map))
            inside = Find.CurrentMap?.Index ?? -1;
        var after = Find.CurrentMap?.Index ?? -1;
        report.Append($"地图作用域 进入前{before}/内{inside}/退出后{after}")
            .Append(before == after ? " ✓" : " **没有还原**").Append("；");

        // 4. What the designator's own getter answers while the scope is held.
        if (designator is not null)
        {
            int seen;
            using (DesignatorSync.CurrentMapScope.Enter(map))
                seen = designator.Map?.Index ?? -1;
            report.Append($"设计器自己的 Map={seen}")
                .Append(seen == map.Index ? " ✓" : " **不是作用域指定的地图**").Append("；");
        }

        report.Append($"地图数 {Find.Maps.Count}，目标 uniqueID={map.uniqueID}（列表位置 {map.Index}）");
        if (other is null)
            report.Append("（**只有一张地图：作用域是空操作，跨图重放测不到** —— 这一项不是通过，是测不了）");
        report.Append($";重放累计 {DesignatorSync.Summary()}");
        report.Append(argument is { Length: > 0 } ? $"；参数 {argument} 未使用" : "");
        return report.ToString();
    }

    /// <summary>A concrete zone designator, so the zone payload's map field can be exercised. `Designator_ZoneAdd` is
    /// abstract; `Designator_ZoneAddStockpile_Resources` (`:624356`) is concrete and has a parameterless constructor, which
    /// is why `DesignatorSync.FindZoneDesignator` looks designators up rather than constructing them - this probe needs the
    /// other direction, one it can build without the architect menu.</summary>
    /// <summary>One census line. Deliberately short: the map count leads, because on a one-map save every other number here
    /// is a tautology and a reader has to see that first.</summary>
    internal static string Summary()
    {
        var maps = Find.Maps?.Count ?? 0;
        var current = Find.CurrentMap?.uniqueID ?? -1;
        var designators = 0;
        var ok = 0;
        try
        {
            var wall = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
            if (wall is not null)
            {
                designators++;
                var chunks = DesignatorSync.EncodeChunks(new Designator_Build(wall), [Find.CurrentMap?.Center ?? default]);
                if (chunks.Count > 0)
                {
                    var parts = Encoding.UTF8.GetString(chunks[0].Payload).Split('|');
                    if (parts.Length > 4 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var parsed) && parsed == current) ok++;
                }
            }
            designators++;
            var zoneChunks = DesignatorSync.EncodeChunks(new Designator_ZoneAddStockpile_Resources(),
                [Find.CurrentMap?.Center ?? default]);
            if (zoneChunks.Count > 0)
            {
                var parts = Encoding.UTF8.GetString(zoneChunks[0].Payload).Split('|');
                if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var parsed) && parsed == current) ok++;
            }
        }
        catch (Exception) { }
        return $"{maps} 张地图（当前 uniqueID {current}）"
            + $"/载荷带地图 {ok}/{designators}"
            + (maps < 2 ? "/**单地图：空操作，测不到**" : "");
    }

    private static Designator_ZoneAdd? FindAnyZoneDesignator()
    {
        try { return new Designator_ZoneAddStockpile_Resources(); }
        catch (Exception) { return null; }
    }
}
