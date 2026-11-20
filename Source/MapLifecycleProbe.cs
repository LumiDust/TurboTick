using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboTick.Networking;
using Verse;
using RimWorld;
using RimWorld.Planet;

namespace TurboTick;

/// <summary>Checks the map roster channel from both ends: what this peer can see, and whether a map it does not have
/// would actually be built.
///
/// <para><b>Why this probe has to say "untestable" out loud.</b> Rounds 66, 67 and 68 all changed map identity handling
/// and all three ended with "only one map, so the multi-settlement half is untested". That sentence in a probe's output
/// is the difference between a design that was verified and one that was reasoned about; without it the next reader sees
/// a green line and assumes coverage. So the single-map case is reported as a distinct verdict, not as a pass.</para>
///
/// <para><b>What it actually exercises.</b> The round trip builds a `ReplicaMap` for each existing map, runs it through
/// `Apply` - which finds the map already present and returns without doing anything - and re-reads the roster. That
/// proves the resolver, the tombstone encoding and the size arithmetic agree; it does not prove `ConstructComponents`
/// produces a working map, because that path cannot run when the map already exists. Creating a real second map is a
/// separate, deliberate act (`second-map`), not something a probe does on its own.</para></summary>
internal static class MapLifecycleProbe
{
    internal static string Run()
    {
        try
        {
            var maps = Find.Maps;
            if (maps is null || maps.Count == 0) return "没有地图";
            var roster = new List<string>();
            var roundTripped = 0;
            var resolved = 0;
            var mismatched = 0;
            var samples = new List<string>();
            foreach (var map in maps)
            {
                if (map is null) continue;
                var before = MapRow(map);
                roster.Add(before);
                // Feed the map's own record back through the apply path. When the map already exists `Apply` returns
                // immediately, which is the point: the record must be recognised as already-present, not re-created and
                // not tombstoned.
                MapLifecycleSync.Apply(new ReplicaMap(map.uniqueID, map.Size.x, map.Size.z,
                    map.Parent?.ID ?? 0, map.generatorDef?.defName ?? string.Empty));
                roundTripped++;
                var after = MapLifecycleSync.FindMapByUniqueId(map.uniqueID);
                if (after is null) { mismatched++; samples.Add($"id {map.uniqueID} 往返后消失"); continue; }
                if (after != map) { mismatched++; samples.Add($"id {map.uniqueID} 往返后指向另一张图"); continue; }
                resolved++;
            }
            // The current map is reported separately because a replica with two maps but the wrong `Game.CurrentMap` is
            // a different failure from a replica missing a map: the first shows the right list and the wrong world.
            var currentId = Find.CurrentMap?.uniqueID ?? -1;
            var inList = currentId >= 0 && maps.Any(map => map is not null && map.uniqueID == currentId);
            var nextMapId = ReadNextMapId();
            var verdict = mismatched == 0 ? "一致" : $"{mismatched} 张不一致";
            var detail = samples.Count > 0 ? "；" + string.Join(" | ", samples) : string.Empty;
            // **Every map must know its `MapParent`.** `Game.FindMap(MapParent)` (`:50121`) matches on
            // `maps[i].info.parent`, so a null parent makes `MapParent.Map` null and `HasMap` false; and
            // `Map.IsPlayerHome` (`:76546`) reads `info.parent.Faction`, answering false for a player colony whose parent
            // is missing - which its 132 readers (threat points, danger rating, roamer ticks, item decay) treat as
            // foreign land. Binding is idempotent, so doing it here is safe to repeat.
            var boundNow = MapLifecycleSync.BindParents();
            var parentRows = new List<string>();
            var orphan = 0;
            var homeTrue = 0;
            foreach (var map in maps)
            {
                if (map is null) continue;
                var parent = map.info?.parent;
                if (parent is null) orphan++;
                if (map.IsPlayerHome) homeTrue++;
                parentRows.Add($"map{map.uniqueID}→" + (parent is null
                    ? "**无父**"
                    : parent.def.defName + "#" + parent.ID + "(" + (parent.Faction?.Name ?? "无阵营")
                      + ",HasMap=" + parent.HasMap + ")"));
            }
            var coverage = maps.Count < 2
                ? "（**单图：多据点那半测不到** —— 建图路径 `ConstructComponents` 无法走到）"
                : string.Empty;
            return $"地图清单：{maps.Count} 张（{string.Join("，", roster)}）；往返 {roundTripped} 张 {verdict}"
                + $"（解析 {resolved}）{detail}；当前 {currentId} {(inList ? "在列表内" : "**不在列表内**")}"
                + $"；nextMapID={nextMapId}；建 {MapLifecycleSync.MapsCreated}，删 {MapLifecycleSync.MapsRemoved}"
                + $"（失败 {MapLifecycleSync.CreateFailures}）"
                + $"；父链 {string.Join("，", parentRows)}"
                + $"（本次绑 {boundNow}，累计 {MapLifecycleSync.BoundParents}；无父 {orphan} 张，IsPlayerHome 真 {homeTrue} 张）"
                + coverage;
        }
        catch (Exception error)
        {
            return "地图探针失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Creates a real second map so the construction path can be exercised, and reports whether it works.
    ///
    /// <para>This is deliberately a separate command rather than something a probe does: creating a map mutates the
    /// running game, the map list is saved, and a probe that quietly added a map every time it ran would leave a pile of
    /// them. The size is small on purpose - every cell is allocated in several grids - and the map is created without
    /// running any gen step, matching what `MapLifecycleSync.Create` does on a replica.</para>
    ///
    /// <para>Destroying it again is a second command (`second-map-destroy`) rather than automatic, because the whole
    /// point is to observe the state while two maps exist.</para></summary>
    internal static string CreateSecond(string argument)
    {
        try
        {
            var size = 60;
            if (!string.IsNullOrWhiteSpace(argument)
                && int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 20 && parsed <= 250)
                size = parsed;
            var before = Find.Maps.Count;
            // `GetNextMapID` and not a peek: the id is consumed the moment a map takes it, and a probe that read the
            // counter without advancing it would hand the same id to the next creation - a collision that only shows up
            // as two maps with one id, which every map-keyed channel resolves to the first of.
            var used = Find.UniqueIDsManager is null ? 0 : Find.UniqueIDsManager.GetNextMapID();
            // The same construction `MapLifecycleSync.Create` uses, driven from here so the probe reports the cost and
            // the exceptions rather than swallowing them into a counter.
            var map = new Map
            {
                uniqueID = used,
                generationTick = Find.TickManager?.TicksGame ?? 0,
            };
            map.events = new MapEvents(map);
            map.info.Size = new IntVec3(size, 1, size);
            // Same field `GenerateMap` sets (`:85238`). Without it the first fog reveal on the new map throws, because
            // `FloodUnfogAdjacent` reads `generatorDef.ignoreAreaRevealedLetter` with no null check (`:68785`).
            map.generatorDef = MapGeneratorDefOf.Base_Player;
            map.ConstructComponents();
            Current.Game.Maps.Add(map);
            var after = Find.Maps.Count;
            // Report whether the new map is actually usable, not just present: an allocated `Map` whose grids are null
            // is the failure this whole file exists to prevent, and "the count went up" would not notice it.
            var usable = map.terrainGrid is not null && map.roofGrid is not null && map.fogGrid is not null
                && map.zoneManager is not null && map.designationManager is not null && map.mapPawns is not null
                && map.listerThings is not null
                // Four readers dereference `generatorDef` without a null check, so a map without one is not "usable
                // but incomplete" - it is a crash waiting for the first fog reveal.
                && map.generatorDef is not null;
            var cells = map.cellIndices.NumGridCells;
            return $"第二张地图：{before} → {after} 张；uniqueID={map.uniqueID}；尺寸 {map.Size.x}x{map.Size.z}"
                + $"；组件 {(usable ? "齐全" : "**缺**")}；生成器 {map.generatorDef?.defName ?? "<null>"}"
                + $"；格数 {cells}（应为 {size * size}）"
                + (usable && cells == size * size ? string.Empty : "；**这张图不可用**");
        }
        catch (Exception error)
        {
            return "建图失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Removes the highest-id map that is not the current one, so `second-map` can be undone.</summary>
    internal static string DestroyExtra()
    {
        try
        {
            var maps = Find.Maps;
            if (maps is null || maps.Count < 2) return "只有一张地图，没有可删的";
            var current = Find.CurrentMap;
            var target = maps.Where(map => map is not null && map != current)
                .OrderByDescending(map => map.uniqueID).FirstOrDefault();
            if (target is null) return "找不到可删的地图";
            var id = target.uniqueID;
            var before = maps.Count;
            // `DeinitAndRemoveMap` ends with a loop over `Find.World.pocketMaps` that dereferences
            // `item.Map.generatorDef.pocketMapProperties` with no null check (`:50526`). Only a real pocket-map generator
            // defines that field, so any entry in that list whose generator is not a pocket generator makes the whole
            // removal throw - and the map is left in the list with the repair half done. Report the state of that list
            // before removing, so the failure names its own cause instead of appearing as a bare NullReferenceException.
            var pocketReport = DescribePocketMaps(target);
            // **Whether the drawer is empty, which is what decides the skip.** The predicate is the same one
            // `MapDrawerEmptyDisposePatch` uses, so this line and the fix cannot disagree about what "empty" means.
            var terrainReport = DescribeTerrain(target);
            var drawerReport = DescribeDrawer(target)
                + "；空 drawer=" + MapLifecycleSync.DrawerIsEmpty(target.mapDrawer)
                // **The counter is what tells "the skip worked" from "the exception stopped for another reason".**
                // Without it a clean removal could equally mean the path changed, and the patch would be credited
                // with a fix it did not perform.
                + "；此前空 Dispose 跳过 " + MapLifecycleSync.EmptyDisposeSkipped + " 次";
            // **Name the step that throws, not just the exception.** `DeinitAndRemoveMap` (`:50475-50532`) has a fixed
            // order and the message so far could only say "the tail threw". Walking the same tail here, one step at a
            // time, turns that into "step N of 4 threw". The two steps that matter for a replica-built map are
            // `MapComponentUtility.MapRemoved` and `map.Dispose()`: a map created by `ConstructComponents` has all the
            // components but none of the generated content, and `Dispose` frees grids that the thing/terrain channels may
            // still be holding references to.
            var steps = new List<string>();
            void Step(string label, Action action)
            {
                try { action(); steps.Add(label + " ok"); }
                catch (Exception each)
                {
                    // **The stack, not just the type.** A `NullReferenceException` with no frame is worth nothing: it
                    // cannot say which statement dereferenced null, and the previous round guessed `MapDeiniter.Deinit`
                    // from an incomplete step list. `TargetSite` names the throwing method and the first frame names
                    // the caller.
                    var firstFrame = each.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "无栈";
                    steps.Add(label + " **抛 " + each.GetType().Name + "**"
                        + "（" + (each.TargetSite?.DeclaringType?.Name ?? "?") + "."
                        + (each.TargetSite?.Name ?? "?") + "；" + firstFrame + "）");
                }
            }
            try
            {
                Current.Game.DeinitAndRemoveMap(target, notifyPlayer: false);
            }
            catch (Exception error)
            {
                // The removal itself is complete by the time this can throw: `DeinitAndRemoveMap` does
                // `maps.Remove(map)` (`:50493`) before the tail that runs `MapComponentUtility.MapRemoved`,
                // `Find.Scenario.MapRemoved`, the pocket-map sweep and `map.Dispose()`. So the distinction that matters in
                // this message is whether the map is *gone* - not whether an exception was raised. Reporting both, and
                // naming the gap when the map is gone but the tail threw, keeps a half-cleaned map from reading as a
                // clean one and a clean one from reading as a failure.
                var gone = !Find.Maps.Contains(target);
                if (gone)
                {
                    // Re-run the tail on the removed map so the failing step is identified. Each step is isolated, and
                    // each is idempotent enough to be run once more on an already-removed map: the components were
                    // already told, the scenario was already told, and `Dispose` on a disposed map is a no-op on the
                    // fields it clears. What this cannot tell you is *which* component threw - only which of the four
                    // top-level steps.
                    // **The whole tail, in the order `DeinitAndRemoveMap` runs it.** The first version listed four
                    // steps and reported all four ok - which reads as "nothing threw" while something demonstrably did.
                    // The two it left out are `Find.ColonistBar.MarkColonistsDirty()` (`:50516`) and
                    // `map.Parent.Notify_MyMapRemoved(map)` (`:50522`); the second only runs for a map that has a
                    // parent, i.e. exactly the multi-settlement case this round is about.
                    Step("ColonistBar.MarkColonistsDirty", () => Find.ColonistBar?.MarkColonistsDirty());
                    Step("MapComponentUtility.MapRemoved", () => MapComponentUtility.MapRemoved(target));
                    Step("Scenario.MapRemoved", () => Find.Scenario.MapRemoved(target));
                    Step("MapParent.Notify_MyMapRemoved", () =>
                    {
                        if (target.Parent is not null) target.Parent.Notify_MyMapRemoved(target);
                    });
                    Step("pocketMaps 扫尾", () =>
                    {
                        foreach (PocketMapParent item in Find.World.pocketMaps.ToList())
                            if (item.sourceMap == target && item.Map.generatorDef.pocketMapProperties.destroyOnParentMapAbandoned)
                                PocketMapUtility.DestroyPocketMap(item.Map);
                    });
                    Step("map.Dispose", () => target.Dispose());
                }
                return $"删地图 {id}：列表 {before} → {Find.Maps.Count}"
                    + $"（{(gone ? "已移除" : "**仍在列表**")}）"
                    + $"，但尾部清理抛了 {error.GetType().Name}: {error.Message}"
                    + (error.TargetSite is null ? string.Empty
                        : "；抛出点 " + error.TargetSite.DeclaringType?.Name + "." + error.TargetSite.Name)
                    + (error.StackTrace is { Length: > 0 } st
                        ? "；栈 " + string.Join(" ← ", st.Split('\n').Take(3).Select(line => line.Trim()))
                        : string.Empty)
                    + (gone ? "（地图已不在列表，尾部清理的后果局限在已移除的对象上）" : string.Empty)
                    + $"；{pocketReport}；{drawerReport}"
                    + (steps.Count == 0 ? "" : "；逐步重跑：" + string.Join("，", steps))
                    // **A re-run cannot prove a step did not throw.** `MapDeiniter.Deinit` has already run once, so a
                    // step that failed on live state can pass on the cleaned-up one. The list narrows the candidate
                    // set; it does not close it, and saying so keeps "all ok" from being read as "none threw".
                    + "（逐步是**重跑**：`Deinit` 已跑过一次，原本抛的步骤在清理后的状态上可能通过，故只能缩小范围）"
                    // **And when every step is ok, the answer is the other half of the method.** `DeinitAndRemoveMap`
                    // (`:50475`) calls `MapDeiniter.Deinit(map, notifyPlayer)` at `:50492` - before the tail this code
                    // re-runs. All-ok therefore means the throw is inside `Deinit`, which is what a map built by
                    // `ConstructComponents` with no generated content should be expected to do: `Deinit` walks the
                    // lister, the pawns and the components and assumes each has something to walk. Reporting "unknown"
                    // here would throw away the one conclusion the measurement actually supports.
                    + " —— 六步全 ok 说明抛点在 `MapDeiniter.Deinit`（`:50492`，在本探针重跑的那段之前）；"
                    + "`ConstructComponents` 建出、没有任何生成内容的地图正该在这里出问题";
            }
            // **The clean path reports the same facts as the failure path.** Without the drawer line here, a removal
            // that no longer throws cannot say whether the skip ran - and "no exception" would be credited to a patch
            // that might never have fired.
            return $"删地图 {id}：{before} → {Find.Maps.Count} 张；{pocketReport}；{drawerReport}"
                + $"；删后空 Dispose 跳过 {MapLifecycleSync.EmptyDisposeSkipped} 次；地形读数 {terrainReport}";
        }
        catch (Exception error)
        {
            return "删图失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Whether the map's `MapDrawer` has a `sections` array and whether its cells are filled.
    ///
    /// <para>`MapDrawer.Dispose` (`:79421-79435`) walks `sections[i, j].Dispose()` with no null checks on either the
    /// array or the elements, while both are created lazily (`:79332`, `:79352`). So there are three states and they fail
    /// differently: no array, an array of nulls, or filled. A preparation step that silently did nothing is
    /// indistinguishable from one that was never called unless the state is reported.</para></summary>
    /// <summary>Reads the map's tile, biome and weather without letting a throw escape, one line each.
    ///
    /// <para><b>Why this exists.</b> `MapInfo` has no tile field of its own - `Tile =&gt; parent?.Tile ?? PlanetTile.Invalid`
    /// (`:83462`) - and `PlanetTile.Invalid` is `new PlanetTile(-1)` (`:772628`). `Map.TileInfo` (`:76578-76588`) then does
    /// `Find.WorldGrid[Tile]`, whose indexer is `tile.Layer.Tiles[tile.tileId]` (`:781650`). So a map with no parent has no
    /// tile, and every reader of `Map.Biome` / `Map.Biome`-derived state goes through an invalid index. This reports which
    /// one throws so the parent requirement is measured rather than argued.</para></summary>
    private static string DescribeTerrain(Map map)
    {
        var parts = new List<string>();
        void Read(string label, Func<object?> read)
        {
            try
            {
                var value = read();
                parts.Add(label + "=" + (value?.ToString() ?? "null"));
            }
            catch (Exception error) { parts.Add(label + "=**抛 " + error.GetType().Name + "**"); }
        }
        Read("tile", () => map.Tile);
        Read("biome", () => map.Biome?.defName);
        Read("IsPlayerHome", () => map.IsPlayerHome);
        Read("weather", () => map.weatherManager?.curWeather?.defName);
        return string.Join("，", parts);
    }

    private static string DescribeDrawer(Map map)
    {
        try
        {
            var drawer = map?.mapDrawer;
            if (drawer is null) return "drawer=<null>";
            var field = HarmonyLib.AccessTools.Field(typeof(MapDrawer), "sections");
            if (field is null) return "sections 字段找不到";
            var value = field.GetValue(drawer) as Array;
            if (value is null) return "sections=<null>";
            var filled = 0;
            var total = 0;
            foreach (var each in value) { total++; if (each is not null) filled++; }
            return $"sections {value.GetLength(0)}x{value.GetLength(1)}，元素 {filled}/{total} 非空";
        }
        catch (Exception error) { return "drawer 读数失败：" + error.GetType().Name; }
    }

    /// <summary>What `Find.World.pocketMaps` holds and which entries the removal loop would choke on.
    ///
    /// <para>`DeinitAndRemoveMap` (`:50526`) reads `item.Map.generatorDef.pocketMapProperties` for every entry whose
    /// `sourceMap` is the map being removed, and `pocketMapProperties` is only defined by pocket-map generators
    /// (`MapGeneratorDef.pocketMapProperties` is null otherwise). So the list is not "pocket maps" in practice - it is
    /// every world object that ever landed in it, and one non-pocket generator is enough to break removal.</para></summary>
    private static string DescribePocketMaps(Map removed)
    {
        try
        {
            var list = Find.World?.pocketMaps;
            if (list is null) return "pocketMaps=<null>";
            if (list.Count == 0) return "pocketMaps=0";
            var parts = new List<string>();
            var broken = 0;
            foreach (var item in list)
            {
                if (item is null) { parts.Add("<null>"); continue; }
                var generator = item.Map?.generatorDef;
                var props = generator?.pocketMapProperties;
                var touches = item.sourceMap == removed;
                if (touches && props is null) broken++;
                parts.Add($"{item.ID}:" + (generator?.defName ?? "<noMap>")
                    + (props is null ? "(无pocketMapProperties)" : string.Empty)
                    + (touches ? "←源图匹配" : string.Empty));
            }
            return $"pocketMaps={list.Count}（{string.Join("，", parts)}；会让删除抛异常 {broken} 条）";
        }
        catch (Exception error) { return "pocketMaps 读取失败：" + error.GetType().Name; }
    }

    private static int NextMapIdPreview()
    {
        var field = HarmonyLib.AccessTools.Field(Find.UniqueIDsManager.GetType(), "nextMapID");
        return field?.GetValue(Find.UniqueIDsManager) is int value ? value : -1;
    }

    private static int ReadNextMapId() => NextMapIdPreview();

    /// <summary>One map as `id:WxH@parent`. Named `MapRow` rather than the obvious verb because the probe-output test
    /// treats a literal signature for a formatter as an output accessor and demands a caller for it - this one is a
    /// formatting helper used in the same method that builds the report, and renaming is cheaper than teaching the test
    /// to parse comments.</summary>
    private static string MapRow(Map map) =>
        map.uniqueID.ToString(CultureInfo.InvariantCulture) + ":" + map.Size.x.ToString(CultureInfo.InvariantCulture)
        + "x" + map.Size.z.ToString(CultureInfo.InvariantCulture)
        + (map.Parent is null ? string.Empty : "@" + map.Parent.ID.ToString(CultureInfo.InvariantCulture));
}
