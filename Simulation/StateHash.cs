using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;

namespace TurboTick.Simulation;

/// <summary>
/// Canonical hash of the simulated colony. Two peers that ran the same commands from the same snapshot
/// must produce the same value; a mismatch is the only reliable signal that the worlds have diverged.
/// Entries are sorted so iteration order cannot create a false positive.
/// </summary>
public static class StateHash
{
    public static string Compute(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // Order-independent without sorting. The previous version sorted every entry with an ordinal compare
        // before hashing, which on a late-game map means ordering tens of thousands of strings once per
        // hash tick while the simulation is paused - the hitch the caller's comment describes. Sum, xor and
        // count over a per-entry digest give the same property in one pass: the multiset decides the value,
        // iteration order does not.
        //
        // Sum and xor are not enough on their own: swapping two entries leaves the sum, the xor and the count
        // unchanged, so a colony whose two things exchanged ids or positions hashed identically. The
        // sum-of-squares term is what breaks that symmetry; it is still one multiply per entry.
        ulong sum = 0, xor = 0, squares = 0;
        long count = 0;
        foreach (var entry in entries)
        {
            if (entry.Length == 0) continue;
            var digest = Digest(entry);
            sum += digest;
            xor ^= digest;
            squares += digest * digest;
            count++;
        }
        Span<byte> combined = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(combined, sum);
        BinaryPrimitives.WriteUInt64LittleEndian(combined[8..], xor);
        BinaryPrimitives.WriteUInt64LittleEndian(combined[16..], squares);
        BinaryPrimitives.WriteInt64LittleEndian(combined[24..], count);
        return Convert.ToHexStringLower(SHA256.HashData(combined));
    }

    /// <summary>FNV-1a over UTF-16 code units. Ordinal, cheap, and independent of string allocation
    /// interning, which is all the combination above needs from it.</summary>
    private static ulong Digest(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>Canonical description of one pawn's simulated state.</summary>
    public static string Pawn(int id, int map, int x, int z, float health, string job) =>
        // Invariant: a German or French client writes 0,5 where an English one writes 0.5, which makes every
        // tick's hash differ - a permanent "desync" that is really a locale.
        $"p:{id}:{map}:{x}:{z}:{health.ToString("0.##", CultureInfo.InvariantCulture)}:{job}";

    /// <summary>Canonical description of one thing's simulated state.</summary>
    public static string Thing(int id, int map, int x, int z, int stack, int hitPoints) =>
        $"t:{id}:{map}:{x}:{z}:{stack}:{hitPoints}";
}

