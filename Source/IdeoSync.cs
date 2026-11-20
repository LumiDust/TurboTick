using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Which ideoligion each colonist belongs to, how sure they are, and what the ideoligion is called.
///
/// <para><b>Why a channel at all.</b> `Pawn_IdeoTracker.ExposeData` scribes `ideo` (a reference), `previousIdeos`,
/// `certainty`, `joinTick` and `babyIdeoExposure` (`:472487-472493`) - but it is reached through `Pawn.ExposeData`, and
/// <see cref="HeadlessRuntime"/> passes `null` for `AuthoritativeFrame.PawnState`, so <b>no pawn fragment ever arrives</b>.
/// Every other subsystem therefore needs a named channel, and this is the one the CT ledger's whole G-SOCIAL section is
/// about.</para>
///
/// <para><b>What a replica shows without it.</b> Both beliefs load from the save, so a fresh replica is right; the moment
/// anything changes it is wrong forever. `Pawn_IdeoTracker.SetIdeo` (`:472270`) reassigns `ideo` and rolls `Certainty`
/// from `ConversionTuning.InitialCertaintyRange` (`:472295`), `CertaintyChangePerDay` drifts it every day, and
/// `Precept_RoleSingle.Notify_MemberChangedFaction` moves role holders. The visible consequences are the ideoligion icon
/// beside the pawn, the `Certainty` line in the social card, and - the one that actually changes decisions - the
/// precept-driven thoughts and the `IdeoligionForbids` checks that decide whether a bed may be claimed
/// (`:472303`) and whether a bond is broken (`:472308`).</para>
///
/// <para><b>Why the ideoligion's own name and colour travel too.</b> A pawn's entry names its ideoligion by `id`, and the
/// replica has to resolve that id to something it can draw. An ideoligion created or renamed on the authority
/// (`Ideo.name`, `:491646`; `Ideo.colorDef`, `:491654`) exists on the replica only if the save was taken after it was
/// made. Without the name table the pawn payload would resolve to nothing and the icon would vanish silently - the same
/// shape as `Zone_Fishing`, where the zone kind existed in the data and on no replica.</para>
///
/// <para><b>Why a change channel.</b> Certainty moves at most once per day (`CertaintyChangePerDay`) and a conversion is
/// rare; a per-frame field would cost a string per pawn per frame for numbers that almost never move. The per-pawn
/// dictionary records the last string sent, so a quiet colony sends nothing.</para></summary>
internal static class IdeoSync
{
    /// <summary>Quantisation for certainty, in hundredths. The number is printed as a percentage
    /// (`Certainty`, shown as "Certainty: 68%"), so a hundredth is below the last digit the player can read.</summary>
    private const float CertaintyQuantum = 0.01f;

    /// <summary>Last pawn payload sent, by `thingIDNumber`.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>Last ideoligion table sent. One string for the whole world rather than per ideoligion, because the table is
    /// tiny (a colony has one to three) and an entry that disappears has to be noticed as well as one that changes.</summary>
    private static string? sentTable;

    internal static long PawnSends { get; private set; }
    internal static long TableSends { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int PawnsTracked { get; private set; }

    /// <summary>Spawned pawns that have a tracker but no ideoligion. Non-zero is normal (animals with a humanlike
    /// tracker, a pawn generated before ideoligions loaded); the number is printed so "4 of 71" is not mistaken for a
    /// channel that lost 67 pawns.</summary>
    internal static int PawnsWithoutIdeo => noIdeo;

    private static int noIdeo;
    internal static string? LastSample { get; private set; }
    internal static string? LastTable { get; private set; }

    internal static List<ReplicaPawnIdeo>? Publish()
    {
        List<ReplicaPawnIdeo>? changed = null;
        try
        {
            var table = EncodeTable();
            if (table is not null && table != sentTable)
            {
                sentTable = table;
                LastTable = table;
                TableSends++;
                (changed ??= []).Add(new ReplicaPawnIdeo(0, table));
            }

            var live = new HashSet<int>();
            var tracked = 0;
            noIdeo = 0;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn?.ideo is null) continue;
                    // Babies have no belief of their own: `Certainty`'s getter forces 0 for them (`:472152-472158`) and
                    // `SetIdeo` returns early (`:472272`), so an entry for one would say "certainty 0, ideo X" forever
                    // and could never change.
                    if (pawn.DevelopmentalStage.Baby()) continue;
                    tracked++;
                    var id = pawn.thingIDNumber;
                    // Why a pawn can be counted and still send nothing: `Encode` returns null when the tracker has no
                    // ideoligion at all, which happens for an animal-like colonist and for a pawn whose `ideo` field was
                    // never initialised. `PawnSends` alone therefore reads "4 pawns, 4 sends" while 71 pawns are spawned -
                    // and that gap is the fact worth printing.
                    if (pawn.ideo.Ideo is null) { noIdeo++; continue; }
                    live.Add(id);
                    var encoded = Encode(pawn);
                    if (encoded is null) continue;
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    LastSample = encoded;
                    PawnSends++;
                    (changed ??= []).Add(new ReplicaPawnIdeo(id, encoded));
                }
            PawnsTracked = tracked;
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    /// <summary>The world's ideoligions as `id#name#colorDefName` joined by `;`, or null when there are none.
    ///
    /// <para>Names are joined with their own separator because an ideoligion name can contain a space, a comma or a
    /// semicolon - a player types it. The field separator is `#` and the entry separator is `;`; a name containing either
    /// is escaped by percent-encoding those two characters, which is the same rule the letter channel uses.</para></summary>
    private static string? EncodeTable()
    {
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null || ideos.Count == 0) return null;
        var parts = new List<string>(ideos.Count);
        var developed = 0;
        var fluid = 0;
        var locked = 0;
        foreach (var ideo in ideos)
        {
            if (ideo is null) continue;
            // A fifth field carries the development tracker when the ideoligion is fluid and has one:
            // `@points,lastTickRaidingApproved`. `IdeoDevelopmentTracker.CanReformNow` (`:493606`) compares `Points`
            // against `NextReformationDevelopmentPoints`, and `Points` only grows inside `TryAddDevelopmentPoints`
            // (`:493684`), which is reached from conversions and rituals the replica never runs - so without this the
            // reform button is grey forever and the "reform your ideoligion" letter (`:493691`) never arrives.
            //
            // `points` is sent raw rather than as a percentage of the threshold because the threshold itself moves:
            // `NextReformationDevelopmentPoints` evaluates `DevelopmentPointsOverReformCountCurve` at `reformCount`
            // (`:493608`), and `reformCount` is one of `ResidualCounterSync`'s nine keys. A percentage would need both
            // halves to stay in step; two integers do not.
            // `Ideo.development` only exists when the ideoligion is fluid: the field is created in the `fluid` setter
            // (`:492030`) and again in `CopyTo` (`:493243`). So "0 developed" has two possible causes — no fluid
            // ideoligion in the world, or the field was never written — and they need separate numbers.
            if (ideo.Fluid) fluid++;
            if (ideo.descriptionLocked) locked++;
            var dev = ideo.development;
            var devField = string.Empty;
            if (dev is not null)
            {
                developed++;
                devField = "#@" + dev.Points.ToString(CultureInfo.InvariantCulture)
                    + "," + dev.lastTickRaidingApproved.ToString(CultureInfo.InvariantCulture);
            }
            // Trailing field: `descriptionLocked` (round 152). `Ideo.RegenerateDescription` (`:493062-493070`) rewrites
            // `description` unless this bool is set, and the toggle lives in `StyleCatOverride.DoDescription`
            // (`:496018-496028`). Nothing carried it, so a replica regenerating the description - which happens after any
            // precept edit, on both peers - overwrote the text the player had locked.
            //
            // Appended rather than woven in: `devField` is itself appended text (`#@points,raided`), so the lock is the
            // LAST field and the apply half can read it positionally from the end without knowing whether a development
            // field is present.
            parts.Add(string.Join("#",
                ideo.id.ToString(CultureInfo.InvariantCulture),
                Escape(ideo.name ?? string.Empty),
                ideo.colorDef?.defName ?? string.Empty) + devField
                + "#" + (ideo.descriptionLocked ? "1" : "0"));
        }
        if (parts.Count == 0) return null;
        parts.Sort(StringComparer.Ordinal);
        DevelopedCount = developed;
        FluidCount = fluid;
        LockedCount = locked;
        return string.Join(";", parts);
    }

    /// <summary>Ideoligions on the authority that carry a development tracker, so the census can say whether the fifth
    /// table field is exercised at all. A count of zero and a broken encoding read the same in a payload sample.</summary>
    internal static int DevelopedCount { get; private set; }

    /// <summary>How many ideoligions are fluid. Printed beside <see cref="DevelopedCount"/> so that "no development
    /// tracker" and "no fluid ideoligion" cannot be read as the same fact.</summary>
    internal static int FluidCount { get; private set; }

    /// <summary>How many development trackers the table actually wrote. A separate number from `DevelopedCount`
    /// because the sender counting its own field and the receiver writing it are two different claims.</summary>
    internal static long DevelopmentApply { get; private set; }

    /// <summary>Ideoligions whose description the player locked, and how many lock statements this peer applied
    /// (round 152). "Nobody locked a description" and "the lock never travelled" read the same without both.</summary>
    internal static int LockedCount { get; private set; }
    internal static long LockApply { get; private set; }

    /// <summary>The belief of one pawn: `ideoId#certainty#joinTick#previousIds`.
    ///
    /// <para>`joinTick` is sent because `Pawn_IdeoTracker` scribes it and `Ideo`-side code compares it against the current
    /// tick to decide whether a pawn "just joined"; it is an absolute stamp like
    /// `Pawn_TrainingTracker.countDecayFrom`, so it must <b>not</b> be quantised - quantising a stamp moves the moment it
    /// names, not the precision of a countdown.</para>
    ///
    /// <para>`previousIdeos` travels as a comma list of ids because the social card's "former ideoligion" line reads it,
    /// and because `SetIdeo` moves the old entry into it (`:472284-472287`) - a replica that never sees the move shows the
    /// pawn as having always held the new belief.</para></summary>
    internal static string? EncodeFor(Pawn pawn) => Encode(pawn);

    private static string? Encode(Pawn pawn)
    {
        try
        {
            var tracker = pawn.ideo;
            if (tracker?.Ideo is null) return null;
            var certainty = (float)Math.Round(tracker.Certainty / CertaintyQuantum) * CertaintyQuantum;
            var previous = new List<string>();
            if (tracker.PreviousIdeos is { Count: > 0 } list)
                foreach (var ideo in list)
                    if (ideo is not null) previous.Add(ideo.id.ToString(CultureInfo.InvariantCulture));
            previous.Sort(StringComparer.Ordinal);
            return string.Join("#",
                tracker.Ideo.id.ToString(CultureInfo.InvariantCulture),
                certainty.ToString("F2", CultureInfo.InvariantCulture),
                tracker.joinTick.ToString(CultureInfo.InvariantCulture),
                string.Join(",", previous));
        }
        catch (Exception) { return null; }
    }

    private static string Escape(string text) =>
        text.Replace("%", "%25").Replace("#", "%23").Replace(";", "%3B");

    private static string Unescape(string text) =>
        text.Replace("%3B", ";").Replace("%23", "#").Replace("%25", "%");

    internal static void Apply(ReplicaPawnIdeo state)
    {
        try
        {
            // Owner 0 is the ideoligion table, addressed to the world rather than to a pawn. Encoding both in one record
            // type keeps the frame's shape unchanged; the discriminator is the owner id, which is never 0 for a pawn
            // (`thingIDNumber` starts at 1).
            if (state.Pawn == 0)
            {
                ApplyTable(state.Payload);
                return;
            }
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn) || pawn?.ideo is null) return;
            var fields = state.Payload.Split('#');
            if (fields.Length < 4) return;
            if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ideoId)) return;
            var ideo = IdeoById(ideoId);
            if (ideo is null) return;
            // `SetIdeo` runs `Find.HistoryEventsManager.RecordEvent` and reassigns `Certainty` from a random range
            // (`:472278`, `:472295`) - on a replica that would record a second history event for a change that already
            // happened on the authority, and roll a different certainty. The field is written directly instead, which is
            // the same choice `MentalStateSync` makes for a mental state's def.
            var realityField = HarmonyLib.AccessTools.Field(typeof(Pawn_IdeoTracker), "ideo");
            realityField?.SetValue(pawn.ideo, ideo);
            if (float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var certainty))
                HarmonyLib.AccessTools.Field(typeof(Pawn_IdeoTracker), "certaintyInt")?.SetValue(pawn.ideo, certainty);
            if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var joinTick))
                pawn.ideo.joinTick = joinTick;
            ApplyPrevious(pawn, fields[3]);
            ApplyCount++;
        }
        catch (Exception) { ApplyFailures++; }
    }

    private static void ApplyPrevious(Pawn pawn, string encoded)
    {
        var field = HarmonyLib.AccessTools.Field(typeof(Pawn_IdeoTracker), "previousIdeos");
        if (field?.GetValue(pawn.ideo) is not List<Ideo> list) return;
        list.Clear();
        if (encoded.Length == 0) return;
        foreach (var text in encoded.Split(','))
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            if (IdeoById(id) is { } ideo) list.Add(ideo);
        }
    }

    /// <summary>The name table. Renames an existing ideoligion in place rather than replacing the object, because every
    /// pawn's tracker holds a reference to it and a replacement would leave those references pointing at the old one.
    /// </summary>
    private static void ApplyTable(string payload)
    {
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return;
        foreach (var entry in payload.Split(';'))
        {
            var fields = entry.Split('#');
            if (fields.Length < 2) continue;
            if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var target = IdeoById(id);
            if (target is null) continue;
            target.name = Unescape(fields[1]);
            if (fields.Length > 2 && fields[2].Length > 0)
            {
                var color = DefDatabase<ColorDef>.GetNamedSilentFail(fields[2]);
                if (color is not null) target.colorDef = color;
            }
            // Fifth field: `@points,lastTickRaidingApproved`. Written field by field rather than through any of the
            // tracker's methods, because `TryAddDevelopmentPoints` caps at the threshold and fires the reform letter
            // (`:493691`) - on a replica that letter would arrive a second time for a milestone the authority already
            // announced. `lastTickRaidingApproved` is an absolute tick stamp (`:493647`, `:334489`), so it is not
            // quantised, the same rule as `joinTick` above.
            // The lock is the last field: with a development field the row is `id#name#color#@p,r#L`, without one it is
            // `id#name#color#L`. Reading from the end keeps one branch instead of two positional cases.
            if (fields.Length > 3 && fields[^1] is "0" or "1")
            {
                target.descriptionLocked = fields[^1] == "1";
                LockApply++;
            }
            if (fields.Length > 3 && fields[3].StartsWith("@", StringComparison.Ordinal))
            {
                var dev = target.development;
                if (dev is not null)
                {
                    var halves = fields[3].Substring(1).Split(',');
                    if (halves.Length == 2)
                    {
                        if (int.TryParse(halves[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var points))
                            dev.points = points;
                        if (int.TryParse(halves[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var raided))
                            dev.lastTickRaidingApproved = raided;
                        DevelopmentApply++;
                    }
                }
            }
        }
    }

    /// <summary>An ideoligion by its own `id` field (`:491608`). `IdeoManager` has no lookup by id - `IdeosListForReading`
    /// is the only list - so this scans it. The list holds one to three entries in practice, which is why a linear scan is
    /// the right shape here and a dictionary would be more code for less.</summary>
    internal static Ideo? IdeoById(int id)
    {
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return null;
        foreach (var ideo in ideos)
            if (ideo is not null && ideo.id == id) return ideo;
        return null;
    }

    internal static void Reset()
    {
        sent.Clear();
        sentTable = null;
        LastSample = null;
        LastTable = null;
    }

    /// <summary>`人形` is in the wording on purpose. Measured: the map has 71 spawned pawns of which 67 are animals, so a
    /// row that said "4 个小人" next to another row's "71 个小人" read as a channel that lost 67 pawns. Only a humanlike
    /// has an `ideo` tracker at all - `PawnComponentsUtility.AddAndRemoveDynamicComponents` adds it per race
    /// (`:200322`, `:445211`) - so the denominator is the humanlike count and the number in front of it is not a loss.</summary>
    internal static string Summary() =>
        PawnsTracked.ToString(CultureInfo.InvariantCulture) + " 个人形带 tracker（其中无信念 "
        + noIdeo.ToString(CultureInfo.InvariantCulture) + " 个） / 表 "
        + (sentTable is null ? "无" : sentTable.Split(';').Length + " 个") + " / 发 "
        + PawnSends.ToString(CultureInfo.InvariantCulture) + "+" + TableSends.ToString(CultureInfo.InvariantCulture)
        + " / 应用 " + ApplyCount.ToString(CultureInfo.InvariantCulture)
        + "（失败 " + ApplyFailures.ToString(CultureInfo.InvariantCulture) + "）"
        // Development trackers printed beside the payload because the fifth table field is only produced when an
        // ideoligion is fluid. "0 developed" and "the field is never written" read the same in a payload sample.
        + " / 发展 " + DevelopedCount.ToString(CultureInfo.InvariantCulture) + " 个意识形态有 tracker（流动 "
        + FluidCount.ToString(CultureInfo.InvariantCulture) + " 个）"
        + "，写入 " + DevelopmentApply.ToString(CultureInfo.InvariantCulture) + " 次"
        // The description lock: "nobody locked a description" and "the lock never travelled" are the same reading
        // unless both numbers are printed.
        + " / 描述锁 " + LockedCount.ToString(CultureInfo.InvariantCulture) + " 个锁住"
        + "（应用 " + LockApply.ToString(CultureInfo.InvariantCulture) + " 段）"
        + " / 样本 " + (LastSample ?? "(无)");
}
