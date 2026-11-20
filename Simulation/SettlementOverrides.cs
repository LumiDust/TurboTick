using System;
using System.Collections.Generic;

namespace TurboTick.Simulation;

/// <summary>Cells whose owner is not what the rectangles say.
///
/// <para><b>Why the rectangles are not enough once a colony has more than one settlement.</b> `SettlementOwnership`
/// answers "which claim contains this cell", and with several claims it answers with the smallest containing one. That
/// is a geometric answer, and geometry has no way to express "this corner inside Alpha's rectangle belongs to Beta" -
/// which is exactly what a player does when they keep a patch of ground for the second settlement, or when a boundary
/// is drawn once and the neighbour later expands over it. CT carries a `Dictionary&lt;long,int&gt; KeptCells` for this
/// (`_lab/ct045/ct045.decompiled.cs:369264`, key `CellKey(map.uniqueID, cellIndex)`), consulted before its own distance
/// rule at `:369713`.</para>
///
/// <para><b>Why overrides are a separate table rather than a fifth field on the claim.</b> A claim is a rectangle: four
/// numbers that every peer can rebuild from the settlement's own record. An override is a per-cell exception, sparse and
/// usually absent - measured on this save, zero. Keeping it apart means the common path (no overrides) costs one
/// `Count == 0` test, and the persistent form stays a list of triples rather than a map-sized array.</para>
///
/// <para><b>Why the key is a pair of ints and not a packed long.</b> `mapId` and a cell index both fit in 32 bits, but
/// packing them means every caller has to agree on the shift, and a wrong shift silently lands on a cell in another map
/// rather than failing. A struct key cannot be built wrongly.</para></summary>
public sealed class SettlementOverrides
{
    /// <summary>One exception: this cell belongs to this claim, whatever the rectangles say.</summary>
    public readonly record struct Entry(int MapId, int X, int Z, int Owner);

    private readonly List<Entry> entries = [];
    private readonly Dictionary<(int MapId, int X, int Z), int> index = [];

    /// <summary>How many exceptions exist. Zero is the normal case and the caller tests it before doing anything
    /// else, so the per-call cost when there are none is a single comparison.</summary>
    public int Count => entries.Count;

    /// <summary>The owner the rectangles would not produce for this cell, or 0 when there is no exception.
    ///
    /// <para>Returns 0 - not "no answer" - because 0 is the id the caller already uses for "no claim owns this". An
    /// exception that names 0 is therefore indistinguishable from no exception, and setting one is refused: releasing a
    /// cell back to the geometric rule is done by removing the entry, not by writing 0 over it.</para></summary>
    public int OwnerAt(int mapId, int x, int z) =>
        entries.Count == 0 ? 0 : index.TryGetValue((mapId, x, z), out var owner) ? owner : 0;

    /// <summary>Records that a cell belongs to a claim regardless of the rectangles. Returns false when nothing changed.
    ///
    /// <para>Setting the same owner twice is a no-op, which matters because the caller may run this from a UI action
    /// that fires every frame while a mouse button is held. Setting a <paramref name="owner"/> of 0 removes the entry
    /// instead of storing it - see <see cref="OwnerAt"/> for why a stored 0 would be meaningless.</para></summary>
    public bool Set(int mapId, int x, int z, int owner)
    {
        var key = (mapId, x, z);
        if (owner == 0) return Remove(mapId, x, z);
        if (index.TryGetValue(key, out var existing))
        {
            if (existing == owner) return false;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].MapId != mapId || entries[i].X != x || entries[i].Z != z) continue;
                entries[i] = new Entry(mapId, x, z, owner);
                index[key] = owner;
                return true;
            }
            // The index and the list disagree: rebuild from the list rather than trusting either. This is reachable
            // only if something mutated `entries` without going through this method (a load, today), and rebuilding is
            // cheaper than a wrong answer about who may build.
            RebuildIndex();
            return Set(mapId, x, z, owner);
        }
        entries.Add(new Entry(mapId, x, z, owner));
        index[key] = owner;
        return true;
    }

    /// <summary>Drops an exception, handing the cell back to the geometric rule.</summary>
    public bool Remove(int mapId, int x, int z)
    {
        if (!index.TryGetValue((mapId, x, z), out _)) return false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].MapId != mapId || entries[i].X != x || entries[i].Z != z) continue;
            entries.RemoveAt(i);
        }
        index.Remove((mapId, x, z));
        return true;
    }

    /// <summary>Every exception, for Scribe. A copy, so the caller cannot mutate the list behind the index.</summary>
    public IReadOnlyList<Entry> ForScribing() => entries;

    /// <summary>Replaces the whole table, e.g. after a load. Entries naming a claim of 0 are dropped, because they
    /// would answer the same as no entry and only cost a lookup.</summary>
    public void Load(IEnumerable<Entry> loaded)
    {
        entries.Clear();
        index.Clear();
        foreach (var entry in loaded)
        {
            if (entry.Owner == 0) continue;
            if (index.ContainsKey((entry.MapId, entry.X, entry.Z))) continue;
            entries.Add(entry);
            index[(entry.MapId, entry.X, entry.Z)] = entry.Owner;
        }
    }

    private void RebuildIndex()
    {
        index.Clear();
        foreach (var entry in entries) index[(entry.MapId, entry.X, entry.Z)] = entry.Owner;
    }

    /// <summary>Total cells covered, for the census line. Separate from <see cref="Count"/> so a report cannot confuse
    /// "one settlement kept twelve cells" with "twelve settlements kept one cell each".</summary>
    public string Describe() => entries.Count == 0 ? "无格子例外" : $"{entries.Count} 个格子例外";
}
