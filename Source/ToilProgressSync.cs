using System;
using System.Collections.Generic;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>How far through its current toil a pawn is, for a replica that never advances one.
///
/// <para><b>Why a replica needs this.</b> The progress bar over a working pawn's head is
/// <c>JobDriver.curToil.WithProgressBar</c> (<c>Verse.AI/Toil.cs</c>), whose value is
/// <c>1f - (float)toil.actor.jobs.curDriver.ticksLeftThisToil / (float)toil.defaultDuration</c>. Only
/// <c>JobDriver.DriverTick</c> decrements <c>ticksLeftThisToil</c>, and a replica never ticks a driver, so that ratio stays
/// at whatever the save was loaded with. The player sees every colonist's bar frozen at the same place while their job
/// names keep changing - the job channel sends <c>Toil</c>, so the bar jumps to a new toil's start value and then stops
/// again.</para>
///
/// <para><b>Why only the counter is sent.</b> <c>defaultDuration</c> comes from the toil, which comes from the job's driver,
/// which the replica builds from the job def it was already given - so the replica has the denominator and only needs the
/// numerator. Sending both would let the two disagree.</para>
///
/// <para><b>Why a tenth of a second.</b> The bar is a few hundred pixels wide next to a pawn's head; it cannot show
/// 1/60 of a toil. Quantising to 6 ticks cuts the frame traffic of a colony at work by an order of magnitude and is still
/// one pixel of motion per step.</para>
internal static class ToilProgressSync
{
    private const int Quantum = 6;

    /// <summary>Last quantised counter sent per pawn, or -1 for "no job".</summary>
    private static readonly Dictionary<int, int> sent = [];

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
    }

    /// <summary>Reads each working pawn's remaining toil ticks, quantised, for the frame.</summary>
    internal static List<ReplicaToilProgress>? Publish()
    {
        List<ReplicaToilProgress>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.jobs?.curDriver is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    // -1 is the "no toil left" reading and is a legitimate value: a driver between toils reports it. It is
                    // sent as -1 rather than dropped, because a dropped entry would leave the last bar standing.
                    var left = pawn.jobs.curDriver.ticksLeftThisToil;
                    if (left >= 0) left -= left % Quantum;
                    if (sent.TryGetValue(id, out var last) && last == left) continue;
                    sent[id] = left;
                    (changed ??= []).Add(new ReplicaToilProgress(id, left));
                    StatesSent++;
                }
            // A pawn that stopped working must have its entry dropped, otherwise it keeps a stale counter forever.
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    /// <summary>Writes the counter onto the replica's driver so the bar matches the host's.</summary>
    internal static void Apply(ReplicaToilProgress state)
    {
        // -1 means "this pawn is no longer working". It must not be written to a driver - a negative counter would make the
        // bar read past its end - and the entry exists only so a merged frame can clear one that is now stale.
        if (state.TicksLeft < 0) return;
        var ticksLeft = state.TicksLeft;
        try
        {
            var pawn = FindPawn(state.Pawn);
            var driver = pawn?.jobs?.curDriver;
            if (driver is null) return;
            // Written directly rather than through any driver method: every one of those also acts on the toil (job
            // expiry at <= 0, `ReadyForNextToil`, side effects), and a replica's job must not progress - only its bar
            // should read correctly.
            driver.ticksLeftThisToil = ticksLeft;
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    private static Pawn? FindPawn(int id)
    {
        foreach (var map in Find.Maps)
        {
            var pawn = map.mapPawns?.AllPawnsSpawned;
            if (pawn is null) continue;
            for (var i = 0; i < pawn.Count; i++)
                if (pawn[i]?.thingIDNumber == id) return pawn[i];
        }
        return null;
    }
}
