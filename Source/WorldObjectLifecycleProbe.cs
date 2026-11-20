using System;
using System.Globalization;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace TurboTick;

/// <summary>Whether a world object created or destroyed on the authority actually produces a lifecycle event.
///
/// <para><b>What this can and cannot show.</b> Creation and destruction both happen on the authority, and the receive
/// side is a replica this process is not. So the probe reports the three things that must hold <em>before</em> a
/// replica could ever see anything: the id is not already known (so the row is produced at all), the row carries a
/// fragment that parses, and the removal of that same id comes back out when the object is destroyed. A pass here is
/// not proof that a replica rebuilt it; it is proof that the payload existed, which is the half that a silently empty
/// list cannot tell you.</para>
///
/// <para><b>Why it creates a real object and destroys it rather than synthesising a row.</b> Handing the encoder a
/// fake row would test the encoder against itself. The probe makes a `WorldObject` through the same
/// `WorldObjectMaker.MakeWorldObject` the game uses, adds it the same way, and then destroys it through
/// `WorldObject.Destroy` - so the reading covers the whole path including the `known`/`destroyed` bookkeeping that is
/// easy to get wrong (a first pass that seeded nothing would announce 254 objects; a destroy that did not add to
/// `destroyed` would re-announce the same id next frame).</para></summary>
internal static class WorldObjectLifecycleProbe
{
    internal static string Report()
    {
        var text = new StringBuilder(320);
        var objects = Find.WorldObjects?.AllWorldObjects;
        if (objects is null) return "没有世界物件容器（还没载入存档）";
        text.Append("本端 ").Append(objects.Count).Append(" 个世界物件");
        // A faction-owned site is the cheapest object that is a plain WorldObject with a valid tile: a caravan needs
        // pawns, a gravship needs a launch, a settlement needs inhabitants. `Settlement` is not used because destroying
        // one fires world-level signals that would be a real change to the colony.
        var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("Settlement")
            ?? DefDatabase<WorldObjectDef>.AllDefsListForReading
                .FirstOrDefault(each => each?.worldObjectClass == typeof(WorldObject));
        if (def is null) return text.Append("；没有可用的 WorldObjectDef，测不到").ToString();

        WorldObject? made = null;
        var createdRow = 0;
        var fragmentBytes = 0;
        var removalRows = 0;
        try
        {
            // Before creating anything: one publish pass so `known` is seeded with the loaded world. Without this the
            // first publish after a real creation would also announce all 254 objects and the row count would be
            // ambiguous.
            WorldObjectLifecycleSync.Publish();
            var before = objects.Count;

            made = WorldObjectMaker.MakeWorldObject(def);
            if (made is null) return text.Append("；MakeWorldObject 返回 null，测不到").ToString();
            // A tile this planet actually has: reuse the tile of any existing world object, and fall back to 0 only
            // when the world is empty. `WorldGrid` has no "surface tile" accessor to ask, and inventing a tile id would
            // make `Add` log an error and relocate the object to 0 — which reads as "creation worked" while having
            // tested a different tile than the one reported.
            var seedTile = objects.Count > 0 ? objects[0].Tile : new PlanetTile(0);
            made.Tile = seedTile.Valid ? seedTile : new PlanetTile(0);
            Find.WorldObjects.Add(made);
            text.Append("；建 ").Append(def.defName).Append('#').Append(made.ID).Append(" 后 ")
                .Append(objects.Count).Append(" 个（前 ").Append(before).Append("）");

            var (created, removed) = WorldObjectLifecycleSync.Publish();
            createdRow = created?.Count ?? 0;
            removalRows = removed?.Count ?? 0;
            fragmentBytes = created?.FirstOrDefault(row => row.Id == made.ID)?.Body?.Length ?? 0;
            text.Append("；发布：建行 ").Append(createdRow).Append("（片段 ")
                .Append(fragmentBytes.ToString(CultureInfo.InvariantCulture)).Append(" B），删行 ").Append(removalRows);
            if (createdRow != 1)
                text.Append(" —— **应有 1 行**（多了说明 `known` 每帧都在重新公告，少了说明新对象没被认出来）");
            // The fragment has to be the game's own `<saveable>` document, or `ScribeBridge.Load` on a replica has
            // nothing to read. Checked by shape, not by length: a non-empty string that is not XML would pass a length
            // test and fail at the far end.
            if (created is { Count: > 0 })
            {
                var body = created[0].Body ?? string.Empty;
                var looksLikeXml = body.Contains("<saveable", StringComparison.Ordinal)
                    && body.Contains("</saveable>", StringComparison.Ordinal);
                text.Append("；片段根节点 ").Append(looksLikeXml ? "正确" : "**不是 <saveable>**");
                text.Append("；def ").Append(created[0].Def)
                    .Append(" 地块 ").Append(created[0].Tile).Append('/').Append(created[0].Layer);
            }

            // A second publish with nothing changed must produce nothing: the id is now `known`, and a channel that
            // re-sends would rebuild the object on every replica frame.
            var (againCreated, againRemoved) = WorldObjectLifecycleSync.Publish();
            text.Append("；第二次发布 ").Append(againCreated?.Count ?? 0).Append(" 建 / ")
                .Append(againRemoved?.Count ?? 0).Append(" 删");
            if ((againCreated?.Count ?? 0) != 0)
                text.Append(" —— **应为 0**（同一对象被重复公告，副本会每帧重建它）");
        }
        catch (Exception error)
        {
            text.Append("；探针抛 ").Append(error.GetType().Name).Append(": ").Append(error.Message);
        }
        finally
        {
            // Destroy what the probe added, through the vanilla path so the world's caches agree. This also exercises
            // the removal half: the id is in `known`, the object is gone, so the next publish must produce one row.
            if (made is not null && !made.Destroyed)
            {
                try
                {
                    made.Destroy();
                    var (afterCreated, afterRemoved) = WorldObjectLifecycleSync.Publish();
                    removalRows = afterRemoved?.Count ?? 0;
                    text.Append("；销毁后再发布 ").Append(afterCreated?.Count ?? 0).Append(" 建 / ")
                        .Append(removalRows).Append(" 删");
                    if (removalRows != 1)
                        text.Append(" —— **应有 1 行删除**（少了说明销毁没有变成事件，副本上那个物件会永远留着）");
                }
                catch (Exception error)
                {
                    text.Append("；销毁抛 ").Append(error.GetType().Name);
                }
            }
        }
        return text.ToString();
    }
}
