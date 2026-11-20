using System;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Measures the visual-carrier channel without a bombardment having to happen: spawn a whitelisted carrier, give
/// it a shell list, read its row, destroy the local copy, apply the row back, and clean up. Nothing here simulates the
/// strike - the point is that a replica can draw it.</summary>
internal static class EtherealVisualProbe
{
    internal static string Run(string argument)
    {
        var cls = (argument ?? string.Empty).Trim();
        if (cls.Length == 0) cls = "Bombardment";
        var sb = new StringBuilder("类 " + cls + "：");
        if (Array.IndexOf(EtherealVisualSync.LogicClasses, cls) >= 0)
        {
            sb.Append("这是逻辑载体，按设计不进白名单（副本生成会二次触发事件）");
            return sb.ToString();
        }
        var map = Find.CurrentMap;
        if (map is null) { sb.Append("没有地图"); return sb.ToString(); }
        var def = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d =>
            d.category == ThingCategory.Ethereal && d.thingClass is not null && d.thingClass.Name == cls);
        if (def is null) { sb.Append("没有这个类的 Ethereal def"); return sb.ToString(); }
        var before = EtherealVisualSync.Count();
        var thing = ThingMaker.MakeThing(def);
        var cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 6);
        GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
        // The payload is the shell list, not a field on the carrier: `Bombardment`'s own fields are settings, and the
        // nested `BombardmentProjectile` is what draws. A carrier with no shells draws an empty sky.
        EtherealVisualSync.SeedShells(thing, "37:60:125:125;12:40:120:130");
        var id = thing.thingIDNumber;
        var (rows, _) = EtherealVisualSync.Publish();
        var row = rows?.Find(r => r.Id == id);
        sb.Append($"夹具 #{id} 在 {cell.x},{cell.z}；行 " +
                  (row is null ? "无（白名单没认）" : row.Def + " 弹丸 " + row.Projectiles) +
                  $"；本图视觉载体 {before}→{EtherealVisualSync.Count()}");
        if (row is not null)
        {
            thing.Destroy();
            var mid = EtherealVisualSync.Count();
            EtherealVisualSync.Apply(row);
            var shells = EtherealVisualSync.ShellCountOfRow(row.Id);
            sb.Append($"；销毁后 {mid}、应用该行后 {EtherealVisualSync.Count()}（应比销毁后多 1）、重建的弹丸 {shells} 颗（应 2）；");
            EtherealVisualSync.ApplyRemoval(row.Id);
            sb.Append($"移除后 {EtherealVisualSync.Count()}；" + EtherealVisualSync.Summary());
        }
        if (!thing.Destroyed) thing.Destroy();
        return sb.ToString();
    }
}
