using System.Reflection;
using System.Collections;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Moves every "when did this happen" stamp by a delta, so a world whose clock was corrected still behaves.
///
/// <para>Modelled on ColonyTogether's <c>ColonyClockRepair</c>
/// (<c>docs/mp-colonytogether/_decomp/ColonyTogether.Prototype.decompiled.cs:75928</c>), which solves the same problem
/// with a tolerance and a bulk shift instead of refusing the correction:</para>
/// <list type="bullet">
/// <item><c>:75930</c> <c>internal const int Tolerance = 1000;</c> - a drift below a thousand ticks is left alone,
/// because fixing it costs more (every stamp moves, and cooldowns the player is watching jump) than the drift.</item>
/// <item><c>:76028</c> <c>internal static bool PastRecord(FieldInfo f)</c> - the stamps are found by <em>name</em>, not
/// by a hand-written list: a field counts when it is in a Verse/RimWorld type, is not an absolute-tick or length
/// field, and either is named <c>spawnedTick</c>/<c>startTick</c> or starts with <c>last</c>. That is what makes the
/// repair cover fields neither of us has heard of.</item>
/// <item><c>:75990</c> <c>long num3 = Math.Min(num2, Math.Max(1L, num - behind))</c> - the value is moved back by the
/// drift, never to less than 1.</item>
/// <item><c>:76870</c> <c>if (!Checked &amp;&amp; Options.Role != "client")</c> - it runs once per map, latched.</item>
/// </list>
///
/// <para>Why not just refuse the rewind (what this file replaced): a barrier that stays behind the engine clock makes
/// <c>CanAdvance</c> false forever. Measured after the first attempt at this fix:
/// <c>appliedTick=30000, serverTick=0, paused=True</c> - the world never ticked again, which is worse than the drift it
/// was protecting against. The correction itself is legitimate (a checkpoint is an older world); it is the stamps that
/// have to follow it.</para></summary>
internal static class ClockStampShift
{
    internal const int Tolerance = 1000;

    internal static int ShiftedFields { get; private set; }
    internal static int ShiftedObjects { get; private set; }
    internal static int LastDelta { get; private set; }
    internal static int Runs { get; private set; }
    internal static int SkippedSmall { get; private set; }

    /// <summary>Field names whose value grows with time but does not mean "when". Shifting these corrupts the thing
    /// they measure - ColonyTogether keeps the same exclusions at <c>:76413</c>.</summary>
    private static readonly HashSet<string> Lengths = new(StringComparer.Ordinal)
    {
        "tickDelta", "ticksLeftThisToil", "ageTicks", "ageBiologicalTicks", "nextCellCostLeft",
        "nextCellCostTotal", "nextCellCostInitial", "lastTickSpringPos", "growthTicks"
    };

    /// <summary>True when this field is a "when" stamp that should move with the clock.</summary>
    private static bool PastRecord(FieldInfo field)
    {
        var ns = field.DeclaringType?.Namespace;
        if (ns is null) return false;
        if (!ns.StartsWith("Verse", StringComparison.Ordinal) && !ns.StartsWith("RimWorld", StringComparison.Ordinal))
            return false;
        if (field.FieldType != typeof(int)) return false;
        if (field.IsStatic || field.IsLiteral || field.IsInitOnly) return false;
        var name = field.Name;
        if (Lengths.Contains(name)) return false;
        // ColonyTogether's rule: absolute-tick fields are handled by their own owners, everything else that records
        // "the last time X happened" moves with the clock - including fields invented by other mods' comps, which is
        // the whole point of scanning by name instead of listing types.
        return name is "spawnedTick" or "startTick" || name.StartsWith("last", StringComparison.Ordinal);
    }

    private static readonly Dictionary<Type, FieldInfo[]> fieldCache = [];

    private static FieldInfo[] FieldsOf(Type type)
    {
        if (fieldCache.TryGetValue(type, out var cached)) return cached;
        FieldInfo[] fields;
        try { fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
        catch (Exception) { fields = []; }
        fieldCache[type] = fields;
        return fields;
    }

    /// <summary>Shifts every stamp in <paramref name="root"/> by <paramref name="delta"/> ticks.
    ///
    /// <para>Bounded on both axes: depth and visit count. An unbounded walk over a colony reaches hundreds of
    /// thousands of objects through `Map` back-references, and this runs on the game thread during a load.</para></summary>
    internal static void Shift(object? root, int delta, int maxDepth = 4, int maxObjects = 5000)
    {
        if (root is null || delta == 0) return;
        Runs++;
        LastDelta = delta;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, int Depth)>();
        queue.Enqueue((root, 0));
        var seen = 0;
        while (queue.Count > 0 && seen < maxObjects)
        {
            var (node, depth) = queue.Dequeue();
            if (node is null) continue;
            if (!visited.Add(node)) continue;
            seen++;
            var changed = false;
            foreach (var field in FieldsOf(node.GetType()))
            {
                if (PastRecord(field))
                {
                    try
                    {
                        if (field.GetValue(node) is not int value) continue;
                        var moved = (int)Math.Clamp((long)value + delta, 1L, 1073741822L);
                        if (moved == value) continue;
                        field.SetValue(node, moved);
                        ShiftedFields++;
                        changed = true;
                    }
                    catch (Exception) { }
                    continue;
                }
                if (depth >= maxDepth) continue;
                // Only descend into things that can own stamps: exposing containers, defs and plain data. `Map` and
                // `Game` are the expensive hubs and are added explicitly by the caller instead of being crawled.
                var type = field.FieldType;
                if (!type.IsClass || type == typeof(string)) continue;
                object? child;
                try { child = field.GetValue(node); } catch (Exception) { continue; }
                if (child is null) continue;
                if (child is IExposable || child is IEnumerable)
                {
                    if (child is IEnumerable sequence and not string)
                    {
                        var count = 0;
                        try
                        {
                            foreach (var item in sequence)
                            {
                                if (item is null || item.GetType().IsValueType) continue;
                                queue.Enqueue((item, depth + 1));
                                if (++count >= 256) break;
                            }
                        }
                        catch (Exception) { }
                    }
                    else queue.Enqueue((child, depth + 1));
                }
            }
            if (changed) ShiftedObjects++;
        }
    }

    internal static void NoteSmallDrift() => SkippedSmall++;

    internal static string Summary() =>
        $"时钟戳平移 {Runs} 次（累计字段 {ShiftedFields} 个 / 对象 {ShiftedObjects} 个），"
        + $"最近平移 {LastDelta} tick，容差内跳过 {SkippedSmall} 次";
}
