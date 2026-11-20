using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Every need the needs card would show, encoded generically.
///
/// <para><b>Why this replaces the three fixed numbers.</b> <c>NeedsCardUtility</c> does not draw three fixed needs: it
/// enumerates <c>pawn.needs.AllNeeds</c> and keeps whatever reports <c>ShowOnNeedList</c>
/// (<c>RimWorld/NeedsCardUtility.cs:91</c>). On a base-game colonist that is mood, food and rest - which is what the frame
/// used to carry - but joy, beauty, comfort, outdoors, indoors, drug desire and whatever a DLC or mod adds are all in that
/// list too, and all of them are frozen on a replica that runs no tick. A colonist whose host-side joy has been falling for
/// two days shows a full joy bar on the replica; drug desire never rises, so a chemical-interest pawn never becomes visible
/// as one.</para>
///
/// <para><b>Why generic rather than a field per need.</b> The set is open - DLC and mods append to it - so a fixed field
/// list would silently miss every need it did not name, which is exactly the failure this channel exists to fix. Encoding
/// by <c>defName</c> means a mod's need travels without this file knowing it exists.</para>
///
/// <para><b>Why only <c>CurLevel</c> and not the instant level.</b> <c>Need.CurLevel</c> is stored and has a public setter;
/// <c>CurInstantLevel</c> is a <i>virtual getter with no setter</i>, and the needs whose card draws a marker over the bar -
/// comfort and beauty - override it to compute from the pawn's surroundings every read
/// (<c>Need_Comfort</c>, <c>Need_Beauty</c> in <c>acs17_all.txt</c>). So the instant level is not state to send: it is
/// derived, and a replica derives it from its own room data, which the terrain, roof and building channels already keep in
/// step. Sending it would be both impossible (no setter) and wrong (it would freeze a computed value).</para>
internal static class NeedsSync
{
    /// <summary>Percent-encoded `defName=level` joined by `;`. Quantised to whole percent: the bars are a few hundred pixels
    /// wide and the card prints no decimals.</summary>
    internal static string? Encode(Pawn? pawn)
    {
        try
        {
            var needs = pawn?.needs?.AllNeeds;
            if (needs is null || needs.Count == 0) return null;
            var builder = new StringBuilder(needs.Count * 20);
            var first = true;
            for (var i = 0; i < needs.Count; i++)
            {
                var need = needs[i];
                if (need?.def?.defName is not { Length: > 0 } name) continue;
                // Hidden needs still travel: `ShowOnNeedList` can flip - a chemical need appears only once the pawn has
                // taken the drug - and a replica that never received it could not then start showing it.
                if (!first) builder.Append(';');
                first = false;
                builder.Append(name).Append('=').Append((int)Math.Round(need.CurLevel * 100f));
                // Rest's own counter, for the one need that keeps state beyond its level. `Need_Rest.ticksAtZero` is
                // advanced only inside `Need_Rest.NeedInterval` (`:467355`, +150 per interval while the level is at zero) and
                // reset the moment the level rises (`:467359`). `ShouldInvoluntarySleepFromMTB` (`:467406`) reads it as the
                // involuntary-sleep MTB bucket - >1000, >15000, >30000, >45000 ticks. A replica rebuilding the level alone
                // therefore reports a pawn who is exhausted but never collapses from it, and the collapse is exactly the
                // visible outcome.
                if (need is Need_Rest rest) builder.Append(",z").Append(RestQuantise(rest.TicksAtZero));
            }
            return first ? null : builder.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Rest's own counter bucket, for the census. Printed so "0" can be told apart from "the field is not in the
    /// payload at all": the number only appears once a pawn has actually been at zero rest.</summary>
    internal static string RestPreview()
    {
        try
        {
            var zero = 0;
            var buckets = new List<string>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.needs?.rest is not { } rest) continue;
                    var ticks = rest.TicksAtZero;
                    if (ticks <= 0) continue;
                    zero++;
                    if (buckets.Count < 3) buckets.Add(pawn.thingIDNumber + ":" + ticks + "→" + RestQuantise(ticks));
                }
            // The encoded string itself, always. `0 个归零` and `,z 后缀根本没写进载荷` are different defects, and only the
            // payload can tell them apart on a save where nobody is exhausted.
            var sample = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned.Cast<Pawn>())
                .Select(pawn => Encode(pawn)).FirstOrDefault(text => text is { Length: > 0 });
            var suffix = sample is not null && sample.Contains(",z", StringComparison.Ordinal);
            return (zero == 0 ? "0 个小人 Rest 归零（ticksAtZero 全为 0）"
                    : $"{zero} 个 Rest 归零 / " + string.Join("，", buckets))
                + $" / 载荷带 z {suffix} / 样本 {(sample is null ? "(空)" : sample.Length > 120 ? sample[..120] : sample)}";
        }
        catch (Exception error) { return "读取失败：" + error.GetType().Name; }
    }

    /// <summary>Applies an encoded need string.
    ///
    /// <para>Writes <c>CurLevel</c> through its public setter rather than calling <c>NeedTickInterval</c>: the tick is what
    /// moves a need toward its target, and a replica is being told where it already is. A need the replica does not have is
    /// skipped rather than created - the set of needs is decided by the pawn's race, genes and hediffs at spawn time, and
    /// creating one from a frame would give a replica a need the host does not have.</para></summary>
    /// <summary>`ticksAtZero` in 1500-tick buckets. The reader only compares against four thresholds (1000/15000/30000/
    /// 45000), so 1500 ticks - one in-game hour - cannot move any of them across, while sending the raw per-interval
    /// counter would make this channel churn for every exhausted pawn every frame.</summary>
    private static int RestQuantise(int ticksAtZero) =>
        ticksAtZero <= 0 ? 0 : (int)Math.Round(ticksAtZero / 1500.0) * 1500;

    internal static void Apply(Pawn? pawn, string? encoded)
    {
        if (pawn?.needs is null || encoded is not { Length: > 0 }) return;
        try
        {
            foreach (var entry in encoded.Split(';'))
            {
                if (entry.Length == 0) continue;
                var equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                // `name=percent,zticks` - the rest counter is optional so a frame from before it existed still applies.
                var value = entry[(equals + 1)..];
                var comma = value.IndexOf(',');
                var ticksAtZero = 0;
                if (comma >= 0)
                {
                    var tail = value[(comma + 1)..];
                    if (tail.StartsWith('z')) int.TryParse(tail[1..], out ticksAtZero);
                    value = value[..comma];
                }
                if (!int.TryParse(value, out var percent)) continue;
                var need = pawn.needs.TryGetNeed(DefDatabase<NeedDef>.GetNamedSilentFail(entry[..equals]));
                if (need is null) continue;
                var target = percent / 100f;
                // Within one percent is the same pixel; re-setting it every frame would push the bar's own interpolation
                // for nothing.
                if (Math.Abs(need.CurLevel - target) > 0.01f) need.CurLevel = target;
                // Written after the level, because setting `CurLevel` to a nonzero value is what a real tick would use to
                // clear the counter - so the authority's bucket must be the last word.
                if (need is Need_Rest restNeed && ticksAtZero > 0)
                    AccessTools.Field(typeof(Need_Rest), "ticksAtZero")?.SetValue(restNeed, ticksAtZero);
            }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] needs apply: " + error.GetType().Name + ": " + error.Message);
        }
    }
}
