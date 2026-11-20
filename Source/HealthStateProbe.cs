using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;

namespace TurboTick;

/// <summary>Reports the `healthState` field: what the authority has, and what a replica would end at.
///
/// <para><b>Why this needs a probe at all.</b> The field is invisible where it is wrong. A replica with a stale
/// `Mobile` for a dead pawn looks entirely normal until somebody opens the character card and sees a banish button for
/// a corpse; a replica with a stale `Dead` is worse and rarer. Neither prints anything. What can be measured is the
/// number on the wire and whether writing it back changes it - and whether the two fields that read it agree.</para>
///
/// <para><b>Why it also counts the readers.</b> `Pawn.Dead` and `Pawn.Downed` are the two properties the UI gates on,
/// and they read `health.Dead` / `health.Downed`, not `healthState` directly. Re-reading them after the write is what
/// proves the write reached the readers rather than only the field - the same distinction that made the round-100
/// thoughts cap visible.</para></summary>
internal static class HealthStateProbe
{
    internal static string Summary()
    {
        var total = 0;
        var byState = new Dictionary<int, int>();
        var downedButMobile = 0;
        var deadButNotDead = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.health is null) continue;
                total++;
                var state = (int)pawn.health.State;
                byState[state] = byState.TryGetValue(state, out var count) ? count + 1 : 1;
                // The two readers, checked against the field they are supposed to be derived from. A disagreement here
                // means the field is not the thing the UI reads, which would make the whole channel the wrong one.
                if (pawn.Downed != (pawn.health.State == PawnHealthState.Down)) downedButMobile++;
                if (pawn.Dead != (pawn.health.State == PawnHealthState.Dead)) deadButNotDead++;
            }
        var parts = new List<string>();
        foreach (var pair in byState.OrderBy(p => p.Key))
            parts.Add((PawnHealthState)pair.Key + " " + pair.Value);
        return "本图 " + total + " 个（" + (parts.Count > 0 ? string.Join(" / ", parts) : "无") + "）；"
            + "副本已写回 " + AuthoritativeReplica.healthStatesApplied + " 次，失败 " + AuthoritativeReplica.healthStateFailures + " 次；"
            + "读源一致性：Dead 与 healthState 不符 " + deadButNotDead + " 个、Downed 与 healthState 不符 " + downedButMobile + " 个";
    }

    /// <summary>Round trip on one pawn: read the wire value, force the field to a different value, apply, and check
    /// that both the field and the two readers came back.
    ///
    /// <para>The intermediate write is the part that makes this a test: applying the value that is already there proves
    /// nothing, because the apply path returns early when it matches. `argument` is a pawn id or empty for the first
    /// colonist.</para></summary>
    internal static string Probe(string? argument)
    {
        Pawn? pawn = null;
        if (!string.IsNullOrEmpty(argument)
            && int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == id);
        pawn ??= Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).FirstOrDefault();
        if (pawn?.health is null) return "没有可用的小人";

        var original = (int)pawn.health.State;
        // Pick a *different* value so the apply cannot short-circuit. `Mobile` is the safe side to start from: a pawn
        // forced to `Dead` for one instruction is not ticked in between, and the value is restored before returning.
        var detour = original == (int)PawnHealthState.Mobile ? (int)PawnHealthState.Down : (int)PawnHealthState.Mobile;
        AuthoritativeReplica.ForceHealthState(pawn, detour);
        var readersAfterDetour = $"Dead={pawn.Dead} Downed={pawn.Downed}";
        AuthoritativeReplica.ForceHealthState(pawn, original);
        var readersRestored = $"Dead={pawn.Dead} Downed={pawn.Downed}";

        return $"小人 #{pawn.thingIDNumber}「{pawn.LabelShortCap}」：当前 {pawn.health.State}（{(int)original}）；"
            + $"改为 {(PawnHealthState)detour} 后读者 {readersAfterDetour}；"
            + $"写回 {pawn.health.State}（{(int)pawn.health.State}）后读者 {readersRestored}"
            + (original == (int)pawn.health.State ? "；往返一致" : "；往返不一致");
    }
}
