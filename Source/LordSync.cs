using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;
using Verse.AI.Group;

namespace TurboTick;

/// <summary>The counters that live on a <see cref="Lord"/>, for a replica that never ticks one.
///
/// <para><b>The defect.</b> <c>Lord.LordTick</c> is reached only through <c>LordManager.LordManagerTick</c> (`:269479`),
/// which is one of the components of <c>Map.MapPostTick</c> (`:77229`) - and a display replica is held at the tick gate
/// before that. So no lord here ever advances, and every number below stays at the value the save was loaded with.</para>
///
/// <para><b>What reads them.</b> Three separate things, which is why they travel together:</para>
/// <list type="bullet">
/// <item><c>ticksInToil</c> - incremented once per <c>LordTick</c> (`:268596`), reset on toil change (`:268545`). Toil
/// transitions test it against fixed thresholds (<c>&gt;= 5000</c> at `:325375`, <c>&gt;= 120000</c> at `:325394`) and staged
/// toils act on multiples of it (<c>% 300 == 0</c> at `:326388`, `:326764`, `:328360`; <c>== 450</c> and <c>&gt; 450</c> at
/// `:328638`, `:328642`). A frozen counter means a ritual that never leaves its first stage.</item>
/// <item><c>numPawnsLostViolently</c> - `:270692` refuses the departing-visitor gift unless it is zero, so on a replica a
/// peaceful visitor who lost nobody would still be treated as having lost someone.</item>
/// <item><c>LordJob_Ritual</c> progress - <c>Progress</c> is <c>ticksPassedWithProgress / durationTicks</c> (`:323753`) and
/// <c>TicksLeft</c> is its complement (`:323751`). Both are what the on-ground progress bar draws
/// (<c>mote.progress = progressBarOverride ?? 1f - TicksLeft / DurationTicks</c>, `:324644`), so a ritual on a replica shows a
/// bar stuck at zero.</item>
/// </list>
///
/// <para><b>Why the lord is addressed by <c>loadID</c> and not by its job.</b> Several lords of the same job can exist at
/// once (two raids, two rituals), and their toils differ. <c>loadID</c> is the identity the save already uses
/// (`:268003`), so a replica can match a row to the lord it rebuilt from the same save.</para>
///
/// <para><b>Why not send the whole lord.</b> <c>Lord.ExposeData</c> already carries the job, the owned pawns and
/// `ticksInToil` (`:268114-268122`) - but a lord is not re-serialised per frame the way a building is, so its own
/// `ExposeData` runs when the world loads and never again. This channel is what makes the frame notice.</para></summary>
internal static class LordSync
{
    /// <summary>Last encoded counters per lord, so an unchanged lord costs nothing.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static long LordsSent { get; private set; }
    internal static long LordsApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Rows whose `loadID` names a lord this peer has never had.
    ///
    /// <para><b>The gap this makes visible.</b> This channel carries <i>counters</i> for lords that already exist on both
    /// sides, which is true for anything that came out of the save. It is not true for a lord the authority created after
    /// loading: a raid arriving (`MakeNewLord` `:269413`), a ritual starting, a visitor landing. `Lord.ExposeData`
    /// (`:268103-268126`) would carry the job and the owned pawns, but it runs once at load and never again, so the replica
    /// has no such lord. The old `if (lord is null) return;` dropped those rows <b>silently</b> - not even counted as a
    /// failure - so a raid whose lord the replica never had looked exactly like a frame with nothing to say.</para>
    ///
    /// <para>Counting it is the honest first step. Rebuilding the lord would mean constructing the job from a def on the
    /// other side, and `LordJob` subclasses carry per-instance state (`LordJob_AssaultColony`, `LordJob_Ritual`'s stage
    /// list) that no payload carries yet - so a rebuilt lord would be a different lord wearing the same id, which is worse
    /// than a missing one because the counters would then apply to the wrong thing.</para></summary>
    internal static int MissingLords { get; private set; }

    /// <summary>The last row published, printed by the census so the encoded shape can be read rather than only counted.</summary>
    internal static ReplicaLord? LastSample { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        LordsSent = 0;
        LordsApplied = 0;
        ApplyFailures = 0;
        LastSample = null;
    }

    /// <summary>`ticksInToil` is quantised to this. It is compared against 450 and against multiples of 300, and a per-tick
    /// value would make this channel - which exists for a counter that moves once per tick - re-send every frame.</summary>
    private const int TickQuantum = 30;

    internal static List<ReplicaLord>? Publish()
    {
        List<ReplicaLord>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
            {
                var lords = map.lordManager?.lords;
                if (lords is null) continue;
                foreach (var lord in lords)
                {
                    if (lord is null || lord.loadID < 0) continue;
                    var id = lord.loadID;
                    live.Add(id);
                    var encoded = Encode(lord);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaLord(id, encoded));
                    LordsSent++;
                    // Always the latest, not the first: `??=` would leave the census printing the value from before the
                    // first change, which reads as a payload that never moves while the live reading does.
                    LastSample = new ReplicaLord(id, encoded);
                }
            }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 领主发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>`job|ticksInToil|gained|lost|stageTicks|stageIndex|progress`.
    ///
    /// <para>The job name is part of the payload because the counters mean different things per job: a ritual's
    /// `stageIndex` is meaningless for a siege, and a replica has to know which decoder to use. It is also the change key's
    /// first component, so a lord that is replaced by a different job is always re-sent.</para></summary>
    private static string Encode(Lord lord)
    {
        var builder = new StringBuilder(64);
        builder.Append(lord.LordJob?.GetType().Name ?? "-").Append('|')
            .Append((lord.ticksInToil / TickQuantum * TickQuantum).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(lord.numPawnsEverGained.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(lord.numPawnsLostViolently.ToString(CultureInfo.InvariantCulture));
        // The ritual fields only exist on `LordJob_Ritual`. Written as zeroes for every other job so the column count is
        // constant - a decoder that has to branch on the length of the string is a decoder waiting to silently stop.
        // `LordJob_HateChant.lostPawns` decides whether the chant becomes an assault: the transition's trigger is
        // `lostPawns >= assaultLossThreshold` (`:319355`), and it is only incremented from `Notify_PawnLost`
        // (`:319377`, `:319381`), which a replica never runs. A replica therefore keeps the chanters chanting forever and
        // never shows the `HateChantersAttacking` letter. Sent for every non-ritual job as well - one more constant column
        // is cheaper than a decoder that has to branch on length.
        var hateChant = lord.LordJob as LordJob_HateChant;
        var lostPawns = hateChant is null ? -1 : ReadInt(hateChant, "lostPawns");
        var ritual = lord.LordJob as LordJob_Ritual;
        if (ritual is null) return builder.Append("|0|0|0|").Append(lostPawns.ToString(CultureInfo.InvariantCulture)).ToString();
        builder.Append('|')
            .Append(ReadInt(ritual, "stageTicks").ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(ReadInt(ritual, "stageIndex").ToString(CultureInfo.InvariantCulture)).Append('|')
            // Ten-thousandths: the bar is a few hundred pixels wide, and the sum is a float advanced by a fraction per tick.
            .Append(((int)Math.Round(ritual.Progress * 10000f)).ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(lostPawns.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static int ReadInt(object owner, string name) =>
        fieldCache.TryGetValue((owner.GetType(), name), out var cached)
            ? cached?.GetValue(owner) as int? ?? 0
            : 0;

    /// <summary>One cache for every reflected counter, keyed by declaring type and name. Built once per type instead of once
    /// per lord per frame, and a renamed field fails here once rather than silently reading zero forever.</summary>
    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> fieldCache = [];

    private static System.Reflection.FieldInfo? Field(Type type, string name)
    {
        if (fieldCache.TryGetValue((type, name), out var known)) return known;
        var found = HarmonyLib.AccessTools.Field(type, name);
        fieldCache[(type, name)] = found;
        if (found is null) Log.Warning($"[TurboTick] LordSync 找不到字段 {type.Name}.{name}");
        return found;
    }

    internal static void Apply(ReplicaLord state)
    {
        try
        {
            Lord? lord = null;
            foreach (var map in Find.Maps)
            {
                var lords = map.lordManager?.lords;
                if (lords is null) continue;
                foreach (var candidate in lords)
                    if (candidate?.loadID == state.Lord) { lord = candidate; break; }
                if (lord is not null) break;
            }
            if (lord is null)
            {
                // Not an apply failure: nothing was malformed, the lord simply is not here. Counted apart so a raid whose
                // lord the replica never received is visible instead of looking like an idle channel.
                MissingLords++;
                return;
            }
            var fields = state.Values.Split('|');
            if (fields.Length < 4) { ApplyFailures++; return; }
            if (int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inToil))
                lord.ticksInToil = inToil;
            if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gained))
                lord.numPawnsEverGained = gained;
            if (int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lost))
                lord.numPawnsLostViolently = lost;
            // The ritual columns are optional so a frame produced by an older build still applies the counters above.
            if (fields.Length >= 7 && lord.LordJob is LordJob_Ritual ritual)
            {
                if (int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stageTicks))
                    Field(ritual.GetType(), "stageTicks")?.SetValue(ritual, stageTicks);
                if (int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stageIndex))
                    Field(ritual.GetType(), "stageIndex")?.SetValue(ritual, stageIndex);
                // `Progress` is a getter, so the sum behind it is written directly. `durationTicks` is not touched: it comes
                // from the ritual's own def on the replica and the authority both.
                if (int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                {
                    var progress = percent / 10000f;
                    var duration = Field(ritual.GetType(), "durationTicks")?.GetValue(ritual) as int? ?? 0;
                    Field(ritual.GetType(), "ticksPassedWithProgress")
                        ?.SetValue(ritual, progress * duration);
                }
            }
            // The hate-chant counter is the last column and may be absent in a frame from an older build, so it is read by
            // position only when present. -1 means "this lord is not a hate chant", which must not be written as a count.
            if (fields.Length >= 8 && lord.LordJob is LordJob_HateChant hateChant
                && int.TryParse(fields[fields.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hateLost)
                && hateLost >= 0)
                Field(typeof(LordJob_HateChant), "lostPawns")?.SetValue(hateChant, hateLost);
            LordsApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }
}
