using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using RimWorld.Planet;
using TurboTick.Networking;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace TurboTick;

/// <summary>The counters that are left once every subsystem with a channel of its own has been accounted for.
///
/// <para><b>Why one channel for nine numbers instead of nine channels.</b> Each of these lives on a different kind of
/// object - a pawn's drug policy, an ideoligion's development tracker, a quest letter, a lord toil, a corpse tracker - and
/// none of them is swept by anything. Nine channels would be nine frames, nine round-trip counts and nine places to
/// forget the wiring; they all share one shape (a key, an owner, an integer), so they share one channel. The key is the
/// <c>Class.field</c> name the gap list uses, which is also what makes the census readable against that list.</para>
///
/// <para><b>What decides membership.</b> The carrier must be an object no sweep visits, the field must have a writer that
/// only runs inside a tick a replica does not run, and something in the game must read it. Every entry below was checked
/// against those three separately; the line references are in the table.</para>
///
/// <para><b>Why a change channel.</b> Several of these advance every tick (<c>PsychicRitualToil_Wait.durationTicks</c>
/// counts down 60 times and then the toil is done). Sending only on change keeps a quiet colony at zero bytes, which is the
/// same reason <see cref="HediffTimerSync"/> is a change channel.</para></summary>
internal static class ResidualCounterSync
{
    /// <summary>One counter: where to find every live carrier, how to read it, and how to write it back.
    ///
    /// <para><c>Locate</c> returns `(ownerId, carrier)` pairs. The owner id has to be stable across peers and is what the
    /// wire uses to address the write-back - a pawn's <c>thingIDNumber</c>, a lord's <c>loadID</c>, a letter's <c>ID</c>.</para></summary>
    private sealed record Counter(
        string Key,
        Func<List<(int Owner, object Carrier)>> Locate,
        Func<object, int> Read,
        Action<object, int> Write);

    private static readonly Counter[] Counters =
    [
        // A pawn's drug record: `lastTakenTicks` is compared against the current tick to decide whether the policy allows
        // another dose (`:478137`), and only `TakeDrug` writes it (`:478160`). A replica therefore keeps every pawn in
        // "just took it" and the policy silently refuses medication forever.
        new("DrugTakeRecord.lastTakenTicks",
            () => PawnParts(pawn => DrugRecords(pawn)),
            carrier => Field(carrier.GetType(), "lastTakenTicks")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "lastTakenTicks")?.SetValue(carrier, value)),

        // A ritual obligation's start tick: `ActiveForTicks` and `TicksUntilExpiration` (`:504864`, `:504866`) are both
        // this number subtracted from the clock, and the obligation is only created inside `Precept_Ritual`'s tick.
        new("RitualObligation.triggeredTick",
            () => RitualObligations(),
            carrier => Field(carrier.GetType(), "triggeredTick")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "triggeredTick")?.SetValue(carrier, value)),

        // The corpse tracker's research progress. Every reader is inside `UnnaturalCorpseTracker`'s own tick
        // (`:50882` onwards); the tracker holds the pawn it haunts, so it is addressed by that pawn.
        new("UnnaturalCorpseTracker.research",
            () => CorpseTrackers(),
            carrier => Field(carrier.GetType(), "research")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "research")?.SetValue(carrier, value)),

        // How many trait options the growth-moment letter offers. `TrySetChoices` decrements it once with a 35% roll
        // (`:662280`) and then generates that many traits (`:662283`); a replica that receives the letter without it shows
        // a growth popup with no choices at all.
        new("ChoiceLetter_GrowthMoment.traitChoiceCount",
            () => GrowthLetters(),
            carrier => Field(carrier.GetType(), "traitChoiceCount")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "traitChoiceCount")?.SetValue(carrier, value)),

        // A healing gene's countdown. `Gene_Healing.Tick` decrements it and heals at zero (`:111421`); the gene rides on a
        // pawn and a replica has no gene tick.
        new("Gene_Healing.ticksToHeal",
            () => GeneCarriers(),
            carrier => Field(carrier.GetType(), "ticksToHeal")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "ticksToHeal")?.SetValue(carrier, value)),

        // How many times an ideoligion has been reformed. It selects the development-points curve
        // (`NextReformationDevelopmentPoints`, `:493608`) and is incremented only by the reform ritual.
        new("IdeoDevelopmentTracker.reformCount",
            () => DevelopmentTrackers(),
            carrier => Field(carrier.GetType(), "reformCount")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "reformCount")?.SetValue(carrier, value)),

        // A ritual's participant roster size, captured when the toil starts (`initialCount = pawns.Count`, `:263853`).
        // `AnyPawnsDone` and `PawnDoneCount` (`:263833`, `:263835`) are both this number minus the live count, so without it
        // a replica's ritual progress bar is a constant zero.
        new("JobSyncTracker.initialCount",
            () => RitualTrackers(),
            carrier => Field(carrier.GetType(), "initialCount")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "initialCount")?.SetValue(carrier, value)),

        // A wait step's remaining time. `Tick` decrements it and returns "done" at zero (`:266705`), so a replica holds
        // the ritual in this step forever.
        new("PsychicRitualToil_Wait.durationTicks",
            () => PsychicToils("PsychicRitualToil_Wait"),
            carrier => Field(carrier.GetType(), "durationTicks")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "durationTicks")?.SetValue(carrier, value)),

        // A trial's idle timer: it increments per tick and is reset by an interaction (`:504381`, `:504365`), and it decides
        // when the trial gives up waiting.
        // Two world-level history stamps with **one writer each**, and both are absolute tick stamps that a replica never
        // advances. Owner 0 because they are singletons on `Find.History` / `Find.FactionManager` rather than per carrier.
        //
        // `History.lastTickPlayerRaidedSomeone` (`:362270`, scribed `:362328`) is written only by
        // `History.Notify_PlayerRaidedSomeone` (`:362353-362356`). Four precept ThoughtWorkers read it through
        // `DaysSinceLastRaid` (`:334473`, `:334481`): `ThoughtWorker_Precept_RecentConquest` (`:334466-334495`) turns it
        // into a four-stage thought whose stage is a function of days since the last raid, and the raid itself is a
        // world-map action the replica never performs - so on a replica the "we have not raided anyone in 25 days" mood
        // penalty never starts and never ends.
        //
        // `Faction.lastExecutionTick` (`:485266`, scribed `:485511`) is written only at `:719495`, inside the execution
        // path in `InteractionWorker_Execution`. `ThoughtWorker_Precept_NoRecentExecution` (`:335217-335226`) compares
        // `TicksGame - lastExecutionTick` against 1800000, so a replica that never saw the execution keeps the
        // load-time stamp forever and the precept's "30 days without an execution" thought is decided from it.
        //
        // Absolute stamps, not durations: `ClockStampShift` already moves "when did this happen" fields by the load
        // delta, and both of these are on its list - the replica's `TicksGame` is the authority's, so the raw value is
        // the correct thing to send. Quantised by the same `Quantum` as everything else in this file, which is well under
        // the 25-day and 30-day thresholds these feed.
        new("History.lastTickPlayerRaidedSomeone",
            () => Find.History is null ? [] : [(0, Find.History)],
            carrier => Field(carrier.GetType(), "lastTickPlayerRaidedSomeone")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "lastTickPlayerRaidedSomeone")?.SetValue(carrier, value)),

        new("Faction.lastExecutionTick",
            () => PlayerFactions(),
            carrier => Field(carrier.GetType(), "lastExecutionTick")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "lastExecutionTick")?.SetValue(carrier, value)),

        new("RitualBehaviorWorker_Trial.ticksSinceLastInteraction",
            () => TrialBehaviors(),
            carrier => Field(carrier.GetType(), "ticksSinceLastInteraction")?.GetValue(carrier) as int? ?? 0,
            (carrier, value) => Field(carrier.GetType(), "ticksSinceLastInteraction")?.SetValue(carrier, value)),
    ];

    /// <summary>Quantised to 150 ticks per entry, and deliberately not per counter: every one of these is compared against a
    /// span of at least a few hundred ticks (the trial's 700, the ritual toil's 60-second steps, a drug policy's sixth of a
    /// day), so three in-game minutes of error cannot flip any of those comparisons - while an unquantised per-tick counter
    /// would make this channel churn every frame, which is the defect the plant sweep and `countDecayFrom` both hit.</summary>
    private const int Quantum = 150;

    private static string? sent;
    private static string? lastSample;

    internal static long Sends { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int Carriers { get; private set; }
    internal static string? LastSample => lastSample;

    /// <summary>How many carriers each key found on the last publish. Printed by the census because `119 个载体` with
    /// eight keys listed cannot tell "nine counters are working" from "one counter has 119 carriers and the other eight have
    /// none" - and only the second is the truth on this save.</summary>
    internal static IReadOnlyDictionary<string, int> CarrierCounts => carrierCounts;

    private static readonly Dictionary<string, int> carrierCounts = new(StringComparer.Ordinal);

    internal static List<ReplicaResidualCounters>? Publish()
    {
        try
        {
            var rows = new List<string>();
            var carriers = 0;
            carrierCounts.Clear();
            foreach (var counter in Counters)
            {
                List<(int Owner, object Carrier)> found;
                try { found = counter.Locate(); }
                catch (Exception) { continue; }
                if (found.Count > 0) carrierCounts[counter.Key] = found.Count;
                foreach (var (owner, carrier) in found)
                {
                    int raw;
                    try { raw = counter.Read(carrier); }
                    catch (Exception) { continue; }
                    carriers++;
                    rows.Add(counter.Key + "#" + owner.ToString(CultureInfo.InvariantCulture) + "#"
                        + (raw / Quantum * Quantum).ToString(CultureInfo.InvariantCulture));
                }
            }
            Carriers = carriers;
            rows.Sort(StringComparer.Ordinal);
            var encoded = string.Join(";", rows);
            if (sent == encoded) return null;
            sent = encoded;
            // Four rows, not all of them: one save has 119 drug records, and a sample that prints every row buries the
            // eight keys that have none - which is the fact the census exists to show.
            if (encoded.Length == 0) lastSample = "(无)";
            else
            {
                var sampleRows = encoded.Split(';');
                lastSample = sampleRows.Length <= 4
                    ? encoded
                    : string.Join(";", sampleRows[..4]) + "…（共 " + sampleRows.Length + " 条）";
            }
            Sends++;
            return [new ReplicaResidualCounters(encoded)];
        }
        catch (Exception) { return null; }
    }

    internal static void Apply(ReplicaResidualCounters state)
    {
        try
        {
            if (state.Rows is not { Length: > 0 }) return;
            var wanted = new Dictionary<string, List<(int Owner, int Value)>>(StringComparer.Ordinal);
            foreach (var row in state.Rows.Split(';'))
            {
                var fields = row.Split('#');
                if (fields.Length < 3) continue;
                if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner)) continue;
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
                if (!wanted.TryGetValue(fields[0], out var list)) wanted[fields[0]] = list = [];
                list.Add((owner, value));
            }
            foreach (var counter in Counters)
            {
                if (!wanted.TryGetValue(counter.Key, out var list)) continue;
                List<(int Owner, object Carrier)> found;
                try { found = counter.Locate(); }
                catch (Exception) { continue; }
                foreach (var (owner, value) in list)
                {
                    foreach (var (carrierOwner, carrier) in found)
                    {
                        if (carrierOwner != owner) continue;
                        try { counter.Write(carrier, value); }
                        catch (Exception) { ApplyFailures++; }
                        break;
                    }
                }
            }
            ApplyCount++;
        }
        catch (Exception) { ApplyFailures++; }
    }

    /// <summary>The tag names this channel carries, in table order.
    ///
    /// <para>Exists so `gap-audit` can ask instead of keeping its own copy. That probe's first version had a hand-written
    /// `ChannelFor` switch naming six fields, and it was already stale when written: `ResidualCounterSync` carries eight
    /// of the entries it was reporting as 「没有载体」. A duplicated list of what travels is exactly the kind of list that
    /// silently stops matching the code it describes, so the authoritative version lives here and is read from there.</para>
    /// </summary>
    internal static IReadOnlyList<string> Keys()
    {
        var keys = new List<string>(Counters.Length);
        foreach (var counter in Counters) keys.Add(counter.Key);
        return keys;
    }

    internal static string Summary()
    {
        var perKey = carrierCounts.Count == 0
            ? "无载体"
            : string.Join(",", carrierCounts.Select(pair => pair.Key.Split('.')[^1] + "×"
                + pair.Value.ToString(CultureInfo.InvariantCulture)));
        return Carriers.ToString(CultureInfo.InvariantCulture) + " 个载体（" + perKey + "）"
            + " / 发 " + Sends.ToString(CultureInfo.InvariantCulture)
        + " / 应用 " + ApplyCount.ToString(CultureInfo.InvariantCulture)
        + "（失败 " + ApplyFailures.ToString(CultureInfo.InvariantCulture) + "）"
        + " / 样本 " + (lastSample ?? "(无)");
    }

    // ---- carriers -------------------------------------------------------------------------------------------------

    /// <summary>A pawn sub-object, addressed by the pawn's id. `Pawn` itself is never sent as a whole (see
    /// `ReplicaPawnState`), so every counter that lives on one has to address it this way.</summary>
    private static List<(int Owner, object Carrier)> PawnParts(Func<Pawn, object?> pick)
    {
        var found = new List<(int, object)>();
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn is null) continue;
                if (pick(pawn) is not { } part) continue;
                found.Add((pawn.thingIDNumber, part));
            }
        return found;
    }

    /// <summary>The drug records on a pawn. The list is private (`Pawn_DrugPolicyTracker.drugTakeRecords`, `:477880`) and
    /// each record is its own exposable object, so the counter is per drug rather than per pawn - which is why the owner
    /// is the pawn and the field is addressed on the record.</summary>
    private static IEnumerable<object> DrugRecords(Pawn pawn)
    {
        var tracker = pawn.drugs;
        if (tracker is null) return [];
        if (HarmonyLib.AccessTools.Field(typeof(Pawn_DrugPolicyTracker), "drugTakeRecords")?.GetValue(tracker)
            is not System.Collections.IEnumerable records) return [];
        var list = new List<object>();
        foreach (var record in records)
            if (record is not null) list.Add(record);
        return list;
    }

    /// <summary>The player's own factions, keyed by `loadID` so both peers agree. Only factions that are not the
    /// player's are excluded: `ThoughtWorker_Precept_NoRecentExecution` reads `p.Faction.lastExecutionTick`
    /// (`:335223`) and `p` is a colonist, so the only faction whose stamp matters is the one colonists belong to.
    /// Sending every faction would be a row per pirate band for a value no precept reads.</summary>
    private static List<(int Owner, object Carrier)> PlayerFactions()
    {
        var result = new List<(int, object)>(1);
        var factions = Find.FactionManager?.AllFactionsListForReading;
        if (factions is null) return result;
        foreach (var faction in factions)
            if (faction is not null && faction.IsPlayer) result.Add((faction.loadID, faction));
        return result;
    }

    private static List<(int Owner, object Carrier)> RitualObligations()
    {
        var found = new List<(int, object)>();
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return found;
        foreach (var ideo in ideos)
            foreach (var precept in ideo?.PreceptsListForReading ?? [])
            {
                if (precept is not Precept_Ritual ritual) continue;
                if (HarmonyLib.AccessTools.Field(typeof(Precept_Ritual), "activeObligations")?.GetValue(ritual)
                    is not System.Collections.IEnumerable obligations) continue;
                foreach (var obligation in obligations)
                    if (obligation is RitualObligation live) found.Add((live.GetHashCode() & 0x7FFFFFF, live));
            }
        return found;
    }

    private static List<(int Owner, object Carrier)> CorpseTrackers()
    {
        var found = new List<(int, object)>();
        if (Find.Anomaly is not { } anomaly) return found;
        foreach (var fieldName in new[] { "corpseTrackers", "looseCorpseTrackers" })
        {
            if (HarmonyLib.AccessTools.Field(typeof(GameComponent_Anomaly), fieldName)?.GetValue(anomaly)
                is not System.Collections.IEnumerable trackers) continue;
            foreach (var tracker in trackers)
            {
                if (tracker is null) continue;
                var haunted = HarmonyLib.AccessTools.Field(tracker.GetType(), "haunted")?.GetValue(tracker) as Pawn;
                found.Add((haunted?.thingIDNumber ?? tracker.GetHashCode() & 0x7FFFFFF, tracker));
            }
        }
        return found;
    }

    private static List<(int Owner, object Carrier)> GrowthLetters()
    {
        var found = new List<(int, object)>();
        var letters = Find.LetterStack?.LettersListForReading;
        if (letters is null) return found;
        foreach (var letter in letters)
            if (letter is ChoiceLetter_GrowthMoment growth) found.Add((growth.ID, growth));
        return found;
    }

    private static List<(int Owner, object Carrier)> GeneCarriers()
    {
        var found = new List<(int, object)>();
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                var genes = pawn?.genes?.GenesListForReading;
                if (genes is null) continue;
                foreach (var gene in genes)
                {
                    if (gene is null) continue;
                    if (gene.GetType().Name != "Gene_Healing") continue;
                    found.Add((pawn!.thingIDNumber, gene));
                }
            }
        return found;
    }

    private static List<(int Owner, object Carrier)> DevelopmentTrackers()
    {
        var found = new List<(int, object)>();
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return found;
        var index = 0;
        foreach (var ideo in ideos)
        {
            index++;
            if (ideo?.development is { } development) found.Add((index, development));
        }
        return found;
    }

    /// <summary>The `JobSyncTracker`s on ritual toils. They are `protected` fields of `LordToil_PsychicRitual`
    /// (`initialGoto`/`finalGoto`, `:265884`), addressed by the owning lord's `loadID`.</summary>
    private static List<(int Owner, object Carrier)> RitualTrackers()
    {
        var found = new List<(int, object)>();
        foreach (var map in Find.Maps)
        {
            var lords = map.lordManager?.lords;
            if (lords is null) continue;
            foreach (var lord in lords)
            {
                // The current toil is `curLordToil` (`Lord :267999`, private) with no accessor - `CurToil` does not exist.
                if (HarmonyLib.AccessTools.Field(typeof(Lord), "curLordToil")?.GetValue(lord) is not LordToil_PsychicRitual toil) continue;
                foreach (var name in new[] { "initialGoto", "finalGoto" })
                    if (HarmonyLib.AccessTools.Field(typeof(LordToil_PsychicRitual), name)?.GetValue(toil) is { } tracker)
                        found.Add((lord.loadID, tracker));
            }
        }
        return found;
    }

    /// <summary>The active psychic-ritual toils of one class name. They live in the `PsychicRitual` a
    /// `LordJob_Ritual` holds (`:264089`), not in any map thing list.</summary>
    private static List<(int Owner, object Carrier)> PsychicToils(string typeName)
    {
        var found = new List<(int, object)>();
        foreach (var map in Find.Maps)
        {
            var lords = map.lordManager?.lords;
            if (lords is null) continue;
            foreach (var lord in lords)
            {
                if (lord?.LordJob is not LordJob_Ritual ritualJob) continue;
                if (HarmonyLib.AccessTools.Field(typeof(LordJob_Ritual), "psychicRitual")?.GetValue(ritualJob)
                    is not PsychicRitual psychic) continue;
                foreach (var fieldName in new[] { "psychicRitualToil", "currentToil" })
                {
                    if (HarmonyLib.AccessTools.Field(typeof(PsychicRitual), fieldName)?.GetValue(psychic) is not { } toil)
                        continue;
                    if (toil.GetType().Name != typeName) continue;
                    found.Add((lord.loadID, toil));
                }
            }
        }
        return found;
    }

    /// <summary>The trial behaviour workers, which hang off the ideoligion's precepts rather than off a map.</summary>
    private static List<(int Owner, object Carrier)> TrialBehaviors()
    {
        var found = new List<(int, object)>();
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return found;
        var index = 0;
        foreach (var ideo in ideos)
            foreach (var precept in ideo?.PreceptsListForReading ?? [])
            {
                if (precept is not Precept_Ritual ritual) continue;
                if (ritual.behavior is not { } behavior) continue;
                if (behavior.GetType().Name != "RitualBehaviorWorker_Trial") continue;
                found.Add((++index, behavior));
            }
        return found;
    }

    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> cache = [];

    private static System.Reflection.FieldInfo? Field(Type type, string name)
    {
        if (cache.TryGetValue((type, name), out var known)) return known;
        var found = HarmonyLib.AccessTools.Field(type, name);
        cache[(type, name)] = found;
        return found;
    }

    internal static void Reset()
    {
        sent = null;
        lastSample = null;
    }
}
