using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Visual-only <c>Ethereal</c> carriers: the orbital strikes, debris and tornadoes the map diff deliberately skips.
///
/// <para><b>Why they are skipped there</b> (<c>HeadlessRuntime</c>'s thing sweep): the category also holds
/// <c>SignalAction_*</c> and <c>RectTrigger</c>, which are <b>logic</b> carriers - spawning one on a replica makes that
/// replica run the action a second time - and <c>OrbitalStrike.Tick</c> calls its own explosion, so a replica that ticked
/// one would apply the damage twice. The filter is right; what was missing is that the purely visual members never reach
/// a replica either, so a player watching the colony sees no bombardment at all.</para>
///
/// <para><b>What the payload actually is.</b> Measured, not assumed: `Bombardment`'s own fields are the strike's settings
/// (<c>bombIntervalTicks</c>/<c>explosionCount</c>...) plus <c>ticksToNextEffect</c>; the falling shells are a nested
/// <c>Bombardment.BombardmentProjectile</c> list, and <b>that</b> is what has <c>lifeTime</c>/<c>maxLifeTime</c>, plus the
/// <c>targetCell</c> each one falls on. A row that carried only the outer object would draw an empty sky, which is exactly
/// what the first round here measured (`生 0/0`, "定时字段不可写"). So the shells travel as text and are rebuilt.</para>
///
/// <para><b>Why the replica must never detonate one.</b> <c>ticksToNextEffect</c> is pinned after the write: the damage is
/// the authority's and it already reaches the replica as thing rows, so a replica that started ticking would double it.</para></summary>
internal static class EtherealVisualSync
{
    /// <summary>The visual members of the category. Matched by class name so the list is greppable and a test can pin
    /// that the logic carriers are absent from it.</summary>
    internal static readonly string[] VisualClasses = ["Bombardment", "OrbitalDebrisSpawner", "Tornado"];

    /// <summary>Classes that look like effects but carry logic and must never be listed above.</summary>
    internal static readonly string[] LogicClasses = ["SignalAction_Letter", "SignalAction_Ambush", "RectTrigger"];

    internal static long Sent { get; private set; }
    internal static long Applied { get; private set; }
    internal static long Unresolved { get; private set; }
    internal static string? LastSample { get; private set; }

    /// <summary>Row id -> the local thing that represents it, so a re-sent row updates in place instead of doubling.</summary>
    private static readonly Dictionary<int, int> localOf = [];

    private static string? signature;

    private static readonly Dictionary<string, FieldInfo?> fields = [];

    internal static string? VisualClass(Thing? thing)
    {
        if (thing?.def is null) return null;
        var name = thing.GetType().Name;
        return Array.IndexOf(VisualClasses, name) >= 0 ? name : null;
    }

    internal static (List<ReplicaLivingEffect>? Rows, List<int>? Removed) Publish()
    {
        List<ReplicaLivingEffect>? rows = null;
        var seen = new List<int>();
        try
        {
            foreach (var map in Find.Maps)
            {
                foreach (var thing in map.listerThings.AllThings)
                {
                    if (thing.def?.category != ThingCategory.Ethereal) continue;
                    if (VisualClass(thing) is null) continue;
                    var shells = ShellsOf(thing);
                    (rows ??= []).Add(new ReplicaLivingEffect(thing.thingIDNumber, thing.def.defName, map.uniqueID,
                        thing.Position.x, thing.Position.z, thing.Rotation.AsInt, shells));
                    seen.Add(thing.thingIDNumber);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning("[TurboTick] 视觉载体发布失败：" + e.Message);
            return (null, null);
        }
        List<int>? removed = null;
        foreach (var id in localOf.Keys) if (!seen.Contains(id)) (removed ??= []).Add(id);
        var full = (rows is null ? "" : string.Join(",", rows.ConvertAll(r => r.Id + "@" + r.X + "," + r.Z + "=" + r.Projectiles)))
                   + "|" + (removed is null ? "" : string.Join(",", removed));
        if (full == signature) return (null, null);
        signature = full;
        if (rows is not null)
        {
            Sent += rows.Count;
            LastSample = rows[0].Def + "#" + rows[0].Id + " 弹丸 " + rows[0].Projectiles.Split(';').Length;
        }
        if (removed is not null) foreach (var id in removed) localOf.Remove(id);
        return (rows, removed);
    }

    /// <summary>How many shells the local copy of a row is drawing - the probe's ruler, and the proof the payload landed.</summary>
    internal static int ShellCountOfRow(int rowId) => Local(rowId) is { } thing ? ShellsOf(thing).Split(';').Length : 0;

    internal static void Apply(ReplicaLivingEffect row)
    {
        try
        {
            var thing = Local(row.Id);
            if (thing is null)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(row.Def);
                var map = Find.Maps.Find(m => m.uniqueID == row.Map);
                if (def is null || map is null) { Unresolved++; return; }
                thing = ThingMaker.MakeThing(def);
                GenSpawn.Spawn(thing, new IntVec3(row.X, 0, row.Z).ClampInsideMap(map), map, WipeMode.Vanish);
                localOf[row.Id] = thing.thingIDNumber;
            }
            WriteShells(thing, row.Projectiles);
            if (thing is OrbitalStrike) Set(thing, "ticksToNextEffect", int.MaxValue);
            Applied++;
        }
        catch (Exception e)
        {
            Unresolved++;
            Log.Warning("[TurboTick] 视觉载体应用失败：" + e.Message);
        }
    }

    internal static void ApplyRemoval(int id)
    {
        if (Local(id) is { Destroyed: false } thing) thing.Destroy();
        localOf.Remove(id);
    }

    private static Thing? Local(int id)
    {
        if (!localOf.TryGetValue(id, out var local)) return null;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing.thingIDNumber == local && !thing.Destroyed) return thing;
        return null;
    }

    internal static int Count()
    {
        var n = 0;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (!thing.Destroyed && thing.def?.category == ThingCategory.Ethereal && VisualClass(thing) is not null) n++;
        return n;
    }

    internal static string Summary() => $"发 {Sent:N0} / 应用 {Applied:N0}（未解析 {Unresolved:N0}）";

    /// <summary><c>life:max:x:z</c> per shell, joined by <c>;</c>; empty when the carrier has no shell list.</summary>
    private static string ShellsOf(Thing thing)
    {
        if (Field(thing, "projectiles")?.GetValue(thing) is not IEnumerable list) return string.Empty;
        var parts = new List<string>();
        foreach (var shell in list)
        {
            if (shell is null) continue;
            parts.Add(Int(shell, "lifeTime") + ":" + Int(shell, "maxLifeTime") + ":" +
                      CellX(shell, "targetCell") + ":" + CellZ(shell, "targetCell"));
        }
        return string.Join(";", parts);
    }

    /// <summary>Puts a shell list on a hand-spawned fixture, so the probe has a payload worth measuring (a real
    /// `Bombardment` gets its shells from the comp that drives it).</summary>
    internal static void SeedShells(Thing thing, string encoded) => WriteShells(thing, encoded);

    private static void WriteShells(Thing thing, string encoded)
    {
        var field = Field(thing, "projectiles");
        if (field is null || field.GetValue(thing) is not IList list) return;
        list.Clear();
        foreach (var part in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 4) continue;
            if (Activator.CreateInstance(field.FieldType.GetGenericArguments()[0]) is not { } shell) continue;
            Set(shell, "lifeTime", Parse(bits[0]));
            Set(shell, "maxLifeTime", Parse(bits[1]));
            SetCell(shell, "targetCell", Parse(bits[2]), Parse(bits[3]));
            list.Add(shell);
        }
    }

    private static int Parse(string text) => int.TryParse(text, out var v) ? v : 0;

    private static int Int(object target, string name) => Field(target, name)?.GetValue(target) is int v ? v : 0;

    private static int CellX(object target, string name) => Field(target, name)?.GetValue(target) is IntVec3 c ? c.x : 0;

    private static int CellZ(object target, string name) => Field(target, name)?.GetValue(target) is IntVec3 c ? c.z : 0;

    private static void SetCell(object target, string name, int x, int z)
    {
        if (Field(target, name) is { } field && field.FieldType == typeof(IntVec3)) field.SetValue(target, new IntVec3(x, 0, z));
    }

    private static void Set(object target, string name, int value)
    {
        if (Field(target, name) is { } field && field.FieldType == typeof(int)) field.SetValue(target, value);
    }

    private static FieldInfo? Field(object target, string name)
    {
        var key = target.GetType().FullName + "#" + name;
        if (fields.TryGetValue(key, out var cached)) return cached;
        FieldInfo? found = null;
        for (var t = target.GetType(); t is not null && found is null; t = t.BaseType)
            found = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        fields[key] = found;
        return found;
    }
}
