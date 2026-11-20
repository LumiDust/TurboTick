using System;
using System.Collections.Generic;

namespace TurboTick.Simulation;

/// <summary>Which of several claimed land rectangles owns a cell, as plain integers.
///
/// <para><b>Why this lives in Simulation and not with the settlement data.</b> The answer decides whether a build order
/// is refused, so it has to be testable without a running game - and the settlement data type takes an <c>IntVec3</c>,
/// which this assembly deliberately does not reference. Splitting the rule out leaves exactly one place where the
/// tie-breaking can be wrong, and that place is covered by a unit test.</para>
///
/// <para><b>The rule.</b> Only rectangles that contain the cell are candidates. The <b>smallest</b> one wins, so a
/// settlement founded inside another keeps its own ground. Two of the same size is not a corner case - a player who
/// founds twice with the same dialog gets two identical squares - so the tie is decided by the lower id. Ids are
/// assigned once and never reused (a retired settlement keeps its id), which makes the answer independent of the order
/// the list happens to hold. That matters because two peers build their lists at different times: an order-dependent
/// rule would let one peer refuse a build the other allowed, with nothing on screen to explain it.</para>
/// </summary>
public static class SettlementOwnership
{
    /// <summary>One claimed rectangle. Deliberately a flat record so the caller does not have to project its own type
    /// into this assembly's vocabulary more than once.</summary>
    public readonly record struct Claim(int Id, int MapId, int MinX, int MinZ, int MaxX, int MaxZ);

    /// <summary>The id of the claim that owns the cell, or 0 when no claim contains it.
    ///
    /// <para>Iterating a list is right here: a colony has a handful of settlements, and the same linear scan answers
    /// both the containment test and the tie-break in one pass.</para></summary>
    public static int Owner(IReadOnlyList<Claim> claims, int mapId, int x, int z) =>
        claims is null ? 0 : Owner(claims, mapId, x, z, claims.Count);

    /// <summary>The same rule over the first <paramref name="count"/> entries of a reusable buffer.</summary>
    public static int Owner(IReadOnlyList<Claim> claims, int mapId, int x, int z, int count)
    {
        if (claims is null) return 0;
        if (count > claims.Count) count = claims.Count;
        var best = 0;
        var bestArea = long.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var claim = claims[i];
            if (claim.MapId != mapId) continue;
            if (x < claim.MinX || x > claim.MaxX || z < claim.MinZ || z > claim.MaxZ) continue;
            var area = (long)(claim.MaxX - claim.MinX + 1) * (claim.MaxZ - claim.MinZ + 1);
            // Smaller wins; equal area is decided by the lower id. The second half is what makes the result independent
            // of iteration order - without it two equal settlements would be resolved by whichever came first.
            if (area > bestArea) continue;
            if (area == bestArea && claim.Id >= best) continue;
            best = claim.Id;
            bestArea = area;
        }
        return best;
    }
}
