using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>How long each memory has been held, applied on a replica's own <c>Thought_Memory</c> objects.
///
/// <para><b>Why a replica needs this.</b> A memory's age moves in exactly one place - <c>Thought_Memory.ThoughtInterval</c>
/// (<c>age += 150</c>), driven by <c>MemoryThoughtHandler.MemoryThoughtInterval</c> - and its age is what decides whether it
/// is still felt (<c>ShouldDiscard</c> is <c>age &gt; DurationTicks</c>). A replica runs neither, so <b>no memory ever
/// expires</b>: a colonist whose friend died a season ago still carries the full grief, the "had a great meal" bonus from the
/// day the save was taken never wears off, and the needs tab lists every thought ever held with the mood they contributed at
/// the moment of loading.</para>
///
/// <para><b>Why this is worth a channel even though mood is derived.</b> <c>Need_Mood.CurInstantLevel</c> is a getter, and it
/// recomputes from <c>thoughts.TotalMoodOffset()</c> - so the mood card is not frozen, it is simply answering from stale
/// inputs. Fixing the inputs is enough; the mood value itself never needs to travel, which is the same shape as
/// <c>PlantSync</c> sending a stage rather than the derived growth.</para>
///
/// <para><b>Why the memories are matched, not rebuilt.</b> A memory object carries <c>def</c>, <c>otherPawn</c>,
/// <c>sourcePrecept</c> and a duration override; <c>TryGainMemory</c> runs <c>def.Worker</c> callbacks and can merge into an
/// existing memory or spawn a replacement. All of that already happened on the authority. The only thing the passing of time
/// writes is <c>age</c>, so only <c>age</c> is written here.</para>
///
/// <para><b>Why the match key is a triple.</b> A pawn can hold several memories of one def (one per <c>otherPawn</c>), so
/// <c>def</c> alone is ambiguous; and the same social memory can repeat, so the order within one (def, other) group is what
/// tells them apart. The index inside the group is therefore part of the key rather than an assumption of uniqueness.</para></summary>
internal static class MemorySync
{
    /// <summary>Last age sent per (pawn, memory key), so a frame carries only what moved.</summary>
    private static readonly Dictionary<(int Pawn, string Key), int> sent = [];

    internal static long MemoriesSent { get; private set; }
    internal static long MemoriesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static long Expired { get; private set; }

    /// <summary>Builds the stable key for one memory: its def, who it is about, and which of that pair it is.
    ///
    /// <para>The index is not a stable identity across peers in the strict sense - but it is stable for as long as nothing is
    /// added or removed, and a frame where that changed carries every memory of the pawn anyway (a new one is indistinguishable
    /// from a shifted old one, so the whole group is re-sent rather than guessed at).</para></summary>
    private static string KeyOf(Thought_Memory memory, int indexInGroup) =>
        (memory.def?.defName ?? "?") + "|" + (memory.otherPawn?.thingIDNumber ?? -1) + "|" + indexInGroup;

    /// <summary>Every memory whose age differs from the last one sent.</summary>
    internal static List<ReplicaMemory>? Publish()
    {
        try
        {
            List<ReplicaMemory>? result = null;
            var alive = new HashSet<(int, string)>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var handler = pawn?.needs?.mood?.thoughts?.memories;
                    if (handler is null) continue;
                    var id = pawn.thingIDNumber;
                    // Count the occurrences of each (def, other) pair as they come, which is the group index.
                    var counters = new Dictionary<string, int>();
                    foreach (var memory in handler.Memories)
                    {
                        if (memory?.def is null) continue;
                        var group = (memory.def.defName ?? "?") + "|" + (memory.otherPawn?.thingIDNumber ?? -1);
                        var indexInGroup = counters.TryGetValue(group, out var seen) ? seen : 0;
                        counters[group] = indexInGroup + 1;
                        var key = KeyOf(memory, indexInGroup);
                        alive.Add((id, key));
                        var age = memory.age;
                        if (sent.TryGetValue((id, key), out var previous) && previous == age) continue;
                        sent[(id, key)] = age;
                        (result ??= []).Add(new ReplicaMemory(id, key, age));
                        MemoriesSent++;
                    }
                }
            // A memory that expired or was discarded never needs its entry again; the dictionary would otherwise grow with
            // every meal and death for the life of the process.
            if (alive.Count > 0)
                foreach (var stale in sent.Keys.Where(key => !alive.Contains(key)).ToList()) sent.Remove(stale);
            return result;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] memory publish: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Writes one memory's age onto the replica's own copy.</summary>
    internal static void Apply(ReplicaMemory state)
    {
        try
        {
            Pawn? pawn = null;
            foreach (var map in Find.Maps)
            {
                foreach (var each in map.mapPawns.AllPawnsSpawned)
                    if (each.thingIDNumber == state.Pawn) { pawn = each; break; }
                if (pawn is not null) break;
            }
            var handler = pawn?.needs?.mood?.thoughts?.memories;
            if (handler is null) return;
            var counters = new Dictionary<string, int>();
            foreach (var memory in handler.Memories)
            {
                if (memory?.def is null) continue;
                var group = (memory.def.defName ?? "?") + "|" + (memory.otherPawn?.thingIDNumber ?? -1);
                var indexInGroup = counters.TryGetValue(group, out var seen) ? seen : 0;
                counters[group] = indexInGroup + 1;
                if (KeyOf(memory, indexInGroup) != state.Key) continue;
                if (memory.age == state.Age) return;
                if (state.Age < 0) return;
                memory.age = state.Age;
                MemoriesApplied++;
                // The needs tab caches the mood reading per tick; without this it would still show the figure it computed
                // before the age was written.
                pawn?.needs?.mood?.thoughts?.situational?.Notify_SituationalThoughtsDirty();
                return;
            }
            // The memory is gone on the replica but the authority still holds it: that is a difference the passing of time
            // cannot produce (an age is written, never removed), so it is counted and left alone for the next full state.
            Expired++;
            if (Expired <= 6)
                Log.Warning($"[TurboTick] memory apply missed ({state.Pawn}, {state.Key}): replica has no such memory");
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning($"[TurboTick] memory apply failed ({state.Pawn}, {state.Key}): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Reset() => sent.Clear();

    internal static string Summary()
    {
        var memories = 0;
        var pawns = 0;
        try
        {
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var handler = pawn?.needs?.mood?.thoughts?.memories;
                    if (handler?.Memories is not { Count: > 0 } list) continue;
                    memories += list.Count;
                    pawns++;
                }
        }
        catch (Exception) { memories = -1; }
        // The count of pawns holding memories next to the count of memories: a pawn with an empty list is the case where the
        // channel has nothing to carry, and reporting only the total would not say which it was.
        return $"{memories} 条（{pawns} 个小人有）/ 发 {MemoriesSent} / 应用 {MemoriesApplied}"
            + $"（失配 {Expired}，失败 {ApplyFailures}）";
    }
}
