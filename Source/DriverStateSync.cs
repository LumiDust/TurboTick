using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using TurboTick.Networking;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Each working pawn's <see cref="Verse.AI.JobDriver"/>, carried as the driver's own Scribe fragment.
///
/// <para><b>The defect.</b> About twenty of the game's progress bars are not the generic toil-delay bar. They read a
/// counter the driver owns: <c>workDone / Plant.def.plant.harvestWork</c> (harvesting), <c>sowWorkDone /
/// def.plant.sowWork</c> (sowing), <c>paintingTime / 1f</c> (painting a building or a floor), <c>totalCleaningWorkDone /
/// totalCleaningWorkRequired</c> (cleaning), <c>gatherProgress / WorkTotal</c> (gathering body resources),
/// <c>bottleNutrition / initialNutritionNeeded</c> (bottle-feeding a baby), <c>workLeft / BaseWorkAmount</c> (the
/// "affect floor/roof" drivers). Each getter is evaluated inside <c>Toil.AddPreTickIntervalAction</c>, so on a replica -
/// which runs no toil at all - the getter is never called and the bar never appears.
/// <c>ToilProgressSync</c> covers only the generic bar, because that one divides <c>ticksLeftThisToil</c>, which is a
/// base field.</para>
///
/// <para><b>Why the whole fragment, and what the earlier reading got wrong.</b> My first pass concluded that
/// <c>JobDriver.ExposeData</c> writes ten base fields and no subclass counter, so a Scribe payload would be "green and
/// empty". That was measured on the base class. Measured on the subclasses: twelve of the fourteen progress-bar drivers
/// <em>do</em> override <c>ExposeData</c> and scribe their counter - <c>JobDriver_PlantWork</c> writes <c>workDone</c>,
/// <c>JobDriver_CleanFilth</c> all three of its floats, <c>JobDriver_BottleFeedBaby</c> three, and so on. The real defect
/// is that nothing ever calls that <c>ExposeData</c> on the receiving side: <c>AuthoritativeReplica</c> builds the driver
/// with <c>job.MakeDriver(pawn)</c>, and a constructor is not a load. So the game has already declared exactly which
/// fields matter, in the one place a mod author is obliged to maintain.</para>
///
/// <para><b>Why this is better than a field-name list.</b> A list of twelve names is a list I have to keep in step with
/// every DLC, and it silently stops covering a renamed counter. <c>ExposeData</c> is the game's own answer to "what is
/// this object's saved state", so a driver that ships with a new explosion counter is carried without a code change -
/// the same reasoning that makes the building sweep use <c>Thing.ExposeData</c> instead of a field list. The cost is
/// payload size: roughly fifteen short nodes per working pawn instead of two or three, which is bounded by the change
/// detection below.</para>
///
/// <para><b>Why the change key is not the fragment itself.</b> The fragment carries <c>ticksLeftThisToil</c>, which
/// changes every single tick, and float counters that move by fractions of a unit. Comparing raw fragments would put
/// every working pawn in every frame - exactly the traffic <c>ToilProgressSync</c>'s 6-tick quantum exists to avoid. So
/// the comparison key is the fragment with that one field blanked (its channel already carries it) and every numeric
/// node rounded to two decimals. What travels is the unmodified fragment, so the replica gets the host's exact value;
/// only the decision to send is coarse.</para>
///
/// <para><b>Why <c>curToilIndex</c> is re-clamped after a load.</b> The fragment also carries the base bookkeeping, and
/// the job channel deliberately applies <c>Toil</c> through a clamp because a replica can build fewer toils than the
/// host (<c>JobDriver_LayDown</c> computes <c>canMoveOrCrawl</c> in <c>Notify_Starting</c>; skipping that once produced
/// one toil here and two on the host). A load would overwrite the clamped index with the host's raw one, so the clamp is
/// re-applied afterwards. That keeps "the counters are the host's" and "the toil index is in range here" both true, which
/// are different invariants.</para></summary>
internal static class DriverStateSync
{
    /// <summary>Last comparison key per pawn, so a frame carries only drivers whose counters moved.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>The one node dropped from the comparison key: it moves every tick and has its own channel.</summary>
    private static readonly Regex VolatileNode = new(
        @"<ticksLeftThisToil>[^<]*</ticksLeftThisToil>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every numeric leaf, so the key can be compared on the grid the bar can actually draw.</summary>
    private static readonly Regex NumericNode = new(
        @"<([A-Za-z_][A-Za-z0-9_]*)>(-?[0-9]+(?:\.[0-9]+)?)</\1>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every leaf node whose value is scalar, whatever its type.
    ///
    /// <para>The census needs this rather than <see cref="NumericNode"/>: measured on <c>JobDriver_Ingest</c>, whose only
    /// subclass fields are two bools (<c>usingNutrientPasteDispenser</c>, <c>eatingFromInventory</c>). A numeric-only scan
    /// reports "0 drivers carry a subclass counter" for a fragment that plainly carries two, which is the same
    /// unreadable-zero failure this project keeps producing.</para></summary>
    private static readonly Regex LeafNode = new(
        @"<([A-Za-z_][A-Za-z0-9_]*)>([^<]*)</\1>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The rounding applied to the comparison key only. Two decimals is finer than a bar a few dozen pixels
    /// wide, and it collapses a counter that moves by 0.01 per tick into one entry per unit of visible motion.</summary>
    private const int KeyDecimals = 2;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static int LoadReferenced { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
        LoadReferenced = 0;
    }

    /// <summary>Every working pawn whose driver fragment differs from the last one sent.</summary>
    internal static List<ReplicaDriverState>? Publish()
    {
        List<ReplicaDriverState>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var driver = pawn?.jobs?.curDriver;
                    if (driver is null) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    // The game's own saved state for this driver. Null means Scribe is busy - a save is in progress and
                    // `ExposeData` must not be re-entered - which resolves itself on the next frame.
                    var fragment = ScribeBridge.Save(driver);
                    if (fragment is not { Length: > 0 })
                    {
                        sent.Remove(id);
                        continue;
                    }
                    // One named driver does not scribe its counter at all (see `UnscribedCounters`); its value is
                    // appended so the receiving side needs no special case beyond writing it back.
                    fragment = AppendUnscribed(driver, fragment);
                    var key = KeyOf(fragment);
                    if (sent.TryGetValue(id, out var last) && last == key) continue;
                    sent[id] = key;
                    (changed ??= []).Add(new ReplicaDriverState(id, fragment));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 驱动片段发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>Strips the every-tick field and rounds the numerics, so a fragment that differs only below the bar's
    /// resolution does not become a frame entry.</summary>
    private static string KeyOf(string fragment)
    {
        var withoutVolatile = VolatileNode.Replace(fragment, "");
        return NumericNode.Replace(withoutVolatile, match =>
        {
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return match.Value;
            var rounded = Math.Round(number, KeyDecimals, MidpointRounding.AwayFromZero);
            return "<" + match.Groups[1].Value + ">" + rounded.ToString("0.##", CultureInfo.InvariantCulture)
                + "</" + match.Groups[1].Value + ">";
        });
    }

    /// <summary>Loads the fragment into the replica's own driver, then restores the invariants the load can break.
    ///
    /// <para>The load runs the game's <c>ExposeData</c> through <see cref="ScribeBridge.Load"/>, which also unwinds the
    /// Scribe loader state and runs the post-load pass - the same path every other object in this project uses. Doing it
    /// by hand (assigning the fields directly) is what the earlier name-list version did, and it would have to be
    /// extended for every driver that ships later.</para></summary>
    internal static void Apply(ReplicaDriverState state)
    {
        if (state.Fragment is not { Length: > 0 } fragment) return;
        var driver = DriverOf(state.Pawn);
        if (driver is null) { ApplyFailures++; return; }
        try
        {
            if (!ScribeBridge.Load(driver, fragment)) { ApplyFailures++; return; }
        }
        catch (Exception) { ApplyFailures++; return; }
        LoadReferenced++;
        ApplyUnscribed(driver, fragment);
        ClampToil(driver);
        StatesApplied++;
    }

    /// <summary>Puts the toil index back inside the list this replica actually built.
    ///
    /// <para><c>JobDriver.ExposeData</c> carries <c>curToilIndex</c> as a plain integer, and loading overwrites whatever
    /// the job channel had already clamped. A replica's toil list can be shorter than the host's - measured once at two
    /// toils against one, because <c>JobDriver_LayDown</c> decides <c>canMoveOrCrawl</c> in <c>Notify_Starting</c> and a
    /// skipped call changes how many toils <c>MakeNewToils</c> yields. <c>CurToil</c> then logs an error and returns null
    /// on every draw. Both the field and the list are private with getters only, so the same reflection
    /// <c>AuthoritativeReplica.ForceToil</c> already uses is reused here rather than duplicated - that method is the one
    /// place in this project that knows how to write this index.</para>
    ///
    /// <para>Re-clamping does not undo the load: the counters that the progress bar divides are unaffected by the index,
    /// and the index itself is still re-applied from the job channel every frame. It only guarantees the value the
    /// replica holds is addressable.</para></summary>
    private static void ClampToil(JobDriver driver)
    {
        try
        {
            toilsField ??= AccessTools.Field(typeof(JobDriver), "toils");
            if (toilsField?.GetValue(driver) is not System.Collections.IList toils || toils.Count == 0) return;
            var index = driver.CurToilIndex;
            if (index >= 0 && index < toils.Count) return;
            toilIndexField ??= AccessTools.Field(typeof(JobDriver), "curToilIndex");
            toilIndexField?.SetValue(driver, index < 0 ? 0 : toils.Count - 1);
        }
        catch (Exception) { /* a driver whose toil list is mid-rebuild keeps the value it has */ }
    }

    private static System.Reflection.FieldInfo? toilsField;
    private static System.Reflection.FieldInfo? toilIndexField;

    private static JobDriver? DriverOf(int pawId)
    {
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn?.thingIDNumber == pawId) return pawn.jobs?.curDriver;
        return null;
    }

    /// <summary>The one driver the Scribe fragment cannot carry, handled by name.
    ///
    /// <para><b>Why this exception exists at all.</b> The whole point of carrying the fragment is that the game decides what
    /// is saved, so there is no list to maintain. Enumerating the fourteen drivers that show a counter-driven progress bar
    /// and checking each one: thirteen override <c>ExposeData</c> and scribe their counter (<c>JobDriver_BuildCubeSculpture</c>
    /// writes <c>workDone</c>, <c>JobDriver_PlantWork</c> writes <c>workDone</c>, <c>JobDriver_CleanFilth</c> writes three,
    /// ...). Exactly one does not: <c>JobDriver_RemovePaintFloor</c> declares <c>private float removalTime</c>, drives it
    /// from <c>toil.tickIntervalAction</c>, shows <c>removalTime / 1f</c> in its bar, and has no <c>ExposeData</c> at all.
    /// Vanilla does not save that counter either, so the omission is the game's own - which is why this is one named
    /// exception rather than a general mechanism.</para>
    ///
    /// <para>If a future version adds the override, this block becomes a harmless duplicate write of the same value; if it
    /// renames the field, the block silently stops matching and the census keeps reporting the fragment, so the exception
    /// cannot quietly grow into a wrong value.</para></summary>
    private static readonly (string Type, string Field)[] UnscribedCounters =
    [
        ("JobDriver_RemovePaintFloor", "removalTime")
    ];

    /// <summary>Reads the named counters onto the fragment for the one driver that has no Scribe of its own, and reports
    /// how many such values were appended. Called from <see cref="Publish"/> so the receiving side needs no special case.</summary>
    private static string AppendUnscribed(JobDriver driver, string fragment)
    {
        foreach (var (type, field) in UnscribedCounters)
        {
            if (!string.Equals(driver.GetType().Name, type, StringComparison.Ordinal)) continue;
            var info = driver.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.Public);
            if (info is null || info.FieldType != typeof(float)) continue;
            if (info.GetValue(driver) is not float value) continue;
            // Quantised to the same two decimals the change key uses, so a value that differs only below the bar's
            // resolution does not create a frame entry of its own.
            var text = ((int)Math.Round(value * 100f, MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture);
            var close = fragment.LastIndexOf("</saveable>", StringComparison.Ordinal);
            if (close < 0) continue;
            return fragment[..close] + "<" + field + ">" + text + "</" + field + ">" + fragment[close..];
        }
        return fragment;
    }

    /// <summary>Writes the appended counter back onto the driver, because <c>ExposeData</c> never knew about it.
    ///
    /// <para>The load ignores an unknown node, so this is a separate pass rather than something the Scribe machinery could
    /// do. It runs before <see cref="ClampToil"/> only so that both writes land before the driver is drawn; the two touch
    /// different fields and the order between them carries no meaning.</para></summary>
    private static void ApplyUnscribed(JobDriver driver, string fragment)
    {
        foreach (var (type, field) in UnscribedCounters)
        {
            if (!string.Equals(driver.GetType().Name, type, StringComparison.Ordinal)) continue;
            var open = fragment.LastIndexOf("<" + field + ">", StringComparison.Ordinal);
            if (open < 0) continue;
            var start = open + field.Length + 2;
            var close = fragment.IndexOf("</" + field + ">", start, StringComparison.Ordinal);
            if (close <= start) continue;
            if (!int.TryParse(fragment.AsSpan(start, close - start), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var scaled)) continue;
            var info = driver.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.Public);
            if (info is null || info.FieldType != typeof(float)) continue;
            try { info.SetValue(driver, scaled / 100f); }
            catch (Exception) { ApplyFailures++; }
        }
    }

    /// <summary>What the census shows. The fragment length is included because "0 sent" alone cannot distinguish "no
    /// pawn is doing a counter-driven job" from "every fragment was identical", and the driver type names tell those
    /// apart from "the driver being sampled has no saved state at all".</summary>
    internal static string Preview()
    {
        try
        {
            var working = 0;
            var withState = 0;
            var withOwnField = 0;
            var biggest = 0;
            var extras = new SortedSet<string>(StringComparer.Ordinal);
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            // Idle drivers are counted apart from working ones. A colony of seventy pawns is almost entirely `Wait` and
            // `Goto`, so a flat "top six types" list truncates the one driver this channel exists for - measured: the
            // cleaning job was invisible in that list while it was running.
            var busy = 0;
            var busyKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            string? sample = null;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var driver = pawn?.jobs?.curDriver;
                    if (driver is null) continue;
                    working++;
                    var kind = driver.GetType().Name;
                    kinds[kind] = kinds.TryGetValue(kind, out var seen) ? seen + 1 : 1;
                    if (!IdleDriver(kind))
                    {
                        busy++;
                        busyKinds[kind] = busyKinds.TryGetValue(kind, out var busySeen) ? busySeen + 1 : 1;
                    }
                    var fragment = ScribeBridge.Save(driver);
                    if (fragment is not { Length: > 0 }) continue;
                    withState++;
                    if (sample is null && !IdleDriver(kind) && fragment.Length < 400) sample = fragment;
                    if (fragment.Length > biggest) biggest = fragment.Length;
                    // The decisive reading: a fragment that contains nothing beyond the base fields proves only that
                    // `ScribeBridge.Save` works, not that a subclass counter rides along - and the whole channel exists
                    // for the subclass counters. Listing the extras by name is what makes "0" interpretable.
                    var own = false;
                    foreach (Match match in LeafNode.Matches(fragment))
                    {
                        var name = match.Groups[1].Value;
                        if (IsBaseField(name)) continue;
                        own = true;
                        if (extras.Count < 12) extras.Add(name);
                    }
                    if (own) withOwnField++;
                }
            if (working == 0) return "0 个在跑驱动的（没有正在工作的驱动）";
            var top = new List<string>();
            foreach (var pair in busyKinds)
                if (top.Count < 12) top.Add(pair.Key + "×" + pair.Value);
            return $"{working} 个在跑驱动 / {withState} 个有存档片段 / {withOwnField} 个含子类计数器"
                + $" / 最大 {biggest} B"
                + (extras.Count == 0 ? "" : " / 子类字段 " + string.Join(",", extras))
                + $" / 非闲置 {busy} 个" + (top.Count == 0 ? "" : "：" + string.Join(",", top))
                + $" / 闲置 {working - busy} 个"
                // The raw fragment of one non-idle driver. Without it "0 with subclass counters" cannot be told apart
                // from "the scan is broken", which is exactly what it looked like when `JobDriver_Ingest` - whose
                // `ExposeData` plainly writes two bools - was reported as carrying nothing.
                + (sample is null ? "" : " / 样本 " + sample);
        }
        catch (Exception error) { return "读取失败：" + error.GetType().Name; }
    }

    /// <summary>The driver types that mean "not doing a job": standing, walking somewhere, lying down hurt. They are still
    /// carried - a `Wait` driver's fragment is what keeps `ticksLeftThisToil` and the posture in step - but they carry no
    /// subclass counter, so counting them as evidence would be counting the wrong thing.</summary>
    private static bool IdleDriver(string kind) =>
        kind is "JobDriver_Wait" or "JobDriver_WaitMaintainPosture" or "JobDriver_WaitDowned" or "JobDriver_Goto";

    /// <summary>Whether one node name belongs to the base class rather than the subclass.
    ///
    /// <para><b>Why this is a field lookup and not a list.</b> A hand-written list of "the base fields" was wrong on the
    /// first try: <c>locomotionUrgencySameAs</c> is a base field written with <c>Scribe_References</c>, so a numeric-only
    /// scan missed it and the census then reported all seventy-one idle drivers as "carrying a subclass counter". Asking
    /// the type which class declares the field cannot go stale: the node name is the field name, and the declaring class
    /// is exactly the distinction being made.</para></summary>
    private static bool IsBaseField(string name)
    {
        for (var type = typeof(JobDriver); type is not null && type != typeof(object); type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly) is not null) return true;
        return false;
    }
}
