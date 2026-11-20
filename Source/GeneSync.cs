using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Every pawn's gene list, for a replica that never re-derives it.
///
/// <para><b>The gap.</b> Genes reach a replica once, through `Pawn.ExposeData`'s
/// `Scribe_Deep.Look(ref genes, "genes", this)` (`:131426`), and never again. Everything that grants or removes a gene
/// afterwards - a xenogerm implantation completing, a `GeneGizmo_RemoveXenogene`, a gene bank transfusion, a dev action -
/// leaves the replica's list at its load-time contents.</para>
///
/// <para><b>What reads the list.</b> `StatWorker.GetValueUnfinalized` (`:715798-715813`) adds
/// `gene.def.statOffsets` for every `Active` gene, so metabolic rate, work speed, immunity gain, pain and about thirty
/// more stats come out wrong. `Gene.Active` (`:111256`) itself is derived from the override pair, so a stale list also
/// reports the wrong active set. `TraitSet.RecalculateSuppression` (`:484101-484134`) matches `gene.def.suppressedTraits`
/// against `traits.allTraits`, which is how the trait channel's suppression flags get their authority-side answer - that
/// link is only correct if the gene list is.</para>
///
/// <para><b>Why def names and a xeno/endo flag, not the genes' own scribe form.</b> `Pawn_GeneTracker` scribes
/// `xenogenes` and `endogenes` as `LookMode.Deep` (`:472004-472005`), which would carry each `Gene`'s own state. But
/// `Gene.ExposeData` writes `overriddenByGene` as a reference and the tracker's PostLoadInit nulls-out bad entries
/// (`:472011-472014`). A def list plus the xeno/endo split is what `GenesListForReading` (`:470828`), the stat offsets and
/// `HasActiveGene` actually consume, and a reference that resolves differently on the two peers cannot corrupt it.</para>
///
/// <para><b>Why reconcile through `AddGene`/`RemoveGene`.</b> `Pawn_GeneTracker.AddGene(GeneDef, bool)` (`:471355-471366`)
/// delegates to a private overload (`:471368+`) that, beyond appending to the list, calls `CheckForOverrides`, grants
/// `gene.def.abilities` through `pawn.abilities.GainAbility`, **and adds the gene's `forcedTraits` through
/// `pawn.story.traits.GainTrait(..., suppressConflicts: true)` with `trait.sourceGene` set**. `RemoveGene`
/// (`:471429-471474`) is the mirror: removes the abilities, calls `traits.Notify_GeneRemoved`, rewinds a
/// `passionMod`, and calls `gene.PostRemove()`. Writing `xenogenes`/`endogenes` directly would skip all of that and leave
/// the trait channel fighting the gene channel over the same traits - a gene-forced trait would appear from neither and
/// both would then delete each other's work every frame.</para>
/// </summary>
internal static class GeneSync
{
    private static readonly Dictionary<int, string> sent = [];

    internal static long GenesSent { get; private set; }
    internal static long GenesApplied { get; private set; }
    internal static long GenesChanged { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static ReplicaPawnGenes? LastSample { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        GenesSent = 0;
        GenesApplied = 0;
        GenesChanged = 0;
        ApplyFailures = 0;
        LastSample = null;
    }

    /// <summary>`x:def,def|e:def,def` - xenogenes first, endogenes second, each ordinal-sorted.
    ///
    /// <para>Sorted because the tracker's list order is insertion order, and two peers that reached the same gene set by
    /// different routes (a xenogerm implant versus a save load) hold the same genes in a different order. Comparing
    /// unsorted would republish every frame. Ordinal, not culture, because the def names are ASCII identifiers.</para>
    ///
    /// <para>`xenotype` is deliberately **not** carried. `SetXenotype` (`:471164-471178`) rebuilds the whole xenogene list
    /// from the def, so sending both would be two writers for one list; the gene list is the state, the xenotype is a
    /// label derived from how it was reached.</para></summary>
    private static string Encode(Pawn_GeneTracker genes)
    {
        var xeno = new List<string>(genes.Xenogenes.Count);
        for (var i = 0; i < genes.Xenogenes.Count; i++)
            if (genes.Xenogenes[i]?.def?.defName is { Length: > 0 } name) xeno.Add(name);
        var endo = new List<string>(genes.Endogenes.Count);
        for (var i = 0; i < genes.Endogenes.Count; i++)
            if (genes.Endogenes[i]?.def?.defName is { Length: > 0 } name) endo.Add(name);
        xeno.Sort(StringComparer.Ordinal);
        endo.Sort(StringComparer.Ordinal);
        return "x:" + string.Join(",", xeno) + "|e:" + string.Join(",", endo);
    }

    internal static List<ReplicaPawnGenes>? Publish()
    {
        List<ReplicaPawnGenes>? changed = null;
        try
        {
            if (!ModsConfig.BiotechActive) return null;
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    // A pawn without the tracker, or with an empty gene list, has nothing to say. Sending an empty
                    // encoding for every animal would put 66 rows in every frame for no reason.
                    if (pawn?.genes is not { } genes) continue;
                    if (genes.Xenogenes.Count == 0 && genes.Endogenes.Count == 0) continue;
                    var id = pawn.thingIDNumber;
                    live.Add(id);
                    var encoded = Encode(genes);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    LastSample = new ReplicaPawnGenes(id, encoded);
                    (changed ??= []).Add(LastSample);
                    GenesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] gene publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaPawnGenes state)
    {
        try
        {
            if (!ModsConfig.BiotechActive) return;
            var pawn = FindPawnAny(state.Pawn);
            if (pawn?.genes is not { } genes) return;
            var bar = state.Genes.IndexOf('|');
            if (bar < 0) return;
            var wantedXeno = Parse(state.Genes[2..bar]);
            var wantedEndo = Parse(state.Genes[(bar + 3)..]);
            var changed = 0;

            // Xenogenes first: `ClearXenogenes`/`AddGene(_, true)` is the pair `SetXenotype` itself uses
            // (`:471164-471178`), so this is the game's own idea of "replace the xenogene set".
            for (var i = genes.Xenogenes.Count - 1; i >= 0; i--)
            {
                var have = genes.Xenogenes[i];
                if (have?.def is null) { genes.Xenogenes.RemoveAt(i); changed++; continue; }
                if (wantedXeno.Contains(have.def)) continue;
                // Through the public `RemoveGene`, which also retracts the abilities, notifies the trait set and runs
                // `PostRemove`. Bypassing it is what would leave a gene's forced trait behind.
                genes.RemoveGene(have);
                changed++;
            }
            for (var i = 0; i < wantedXeno.Count; i++)
            {
                if (HasGene(genes.Xenogenes, wantedXeno[i])) continue;
                genes.AddGene(wantedXeno[i], xenogene: true);
                changed++;
            }

            // Endogenes the same way. `AddGene(_, false)` refuses a duplicate endogene itself (`:471359-471362`), but the
            // check here is what keeps a no-op frame from calling `CheckForOverrides` on every pawn every frame.
            for (var i = genes.Endogenes.Count - 1; i >= 0; i--)
            {
                var have = genes.Endogenes[i];
                if (have?.def is null) { genes.Endogenes.RemoveAt(i); changed++; continue; }
                if (wantedEndo.Contains(have.def)) continue;
                genes.RemoveGene(have);
                changed++;
            }
            for (var i = 0; i < wantedEndo.Count; i++)
            {
                if (HasGene(genes.Endogenes, wantedEndo[i])) continue;
                genes.AddGene(wantedEndo[i], xenogene: false);
                changed++;
            }

            if (changed > 0)
            {
                // `AddGene`/`RemoveGene` already run `CheckForOverrides` and `Notify_DisabledWorkTypesChanged`, but they do
                // not decide the trait-set suppression: `RecalculateSuppression` (`:484101`) is the pass that matches
                // `gene.def.suppressedTraits` against the trait list, and it is only reachable from `GainTrait`.
                pawn.story?.traits?.RecalculateSuppression();
                GenesChanged += changed;
            }
            GenesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] gene apply failed (pawn " + state.Pawn + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    private static bool HasGene(List<Gene> list, GeneDef def)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i]?.def == def) return true;
        return false;
    }

    private static List<GeneDef> Parse(string section)
    {
        var result = new List<GeneDef>();
        if (section.Length == 0) return result;
        foreach (var name in section.Split(','))
        {
            if (name.Length == 0) continue;
            if (DefDatabase<GeneDef>.GetNamedSilentFail(name) is { } def) result.Add(def);
            // A gene def this peer lacks is skipped, not fatal: it would otherwise make every apply fail and freeze the
            // whole list, which is worse than one missing gene.
        }
        return result;
    }

    private static Pawn? FindPawnAny(int id)
    {
        foreach (var map in Find.Maps)
        {
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (var i = 0; i < pawns.Count; i++)
                if (pawns[i] is { } pawn && pawn.thingIDNumber == id) return pawn;
        }
        var world = Find.WorldPawns?.AllPawnsAliveOrDead;
        if (world is not null)
            for (var i = 0; i < world.Count; i++)
                if (world[i] is { } pawn && pawn.thingIDNumber == id) return pawn;
        return null;
    }

    /// <summary>Idempotent round-trip for one pawn's gene list. Same reasoning as `PawnTraitSync.Probe`: a count cannot tell
    /// a correct apply from a wrong one, and this channel's apply is a reconcile that could reorder the list or re-grant
    /// an ability.
    ///
    /// <para>It also reports the ability count before and after, because `AddGene`'s private overload grants
    /// `gene.def.abilities` - if the reconcile ever gated on the wrong comparison it would re-grant duplicates, and that
    /// shows as a rising number rather than a wrong string.</para></summary>
    internal static string Probe(string? argument)
    {
        if (!ModsConfig.BiotechActive) return "Biotech 未启用，基因通道测不到（通道已建）";
        Pawn? subject = null;
        foreach (var map in Find.Maps)
        {
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.genes is not { } g) continue;
                if (g.Xenogenes.Count == 0 && g.Endogenes.Count == 0) continue;
                if (argument is { Length: > 0 }
                    && !pawn.thingIDNumber.ToString(CultureInfo.InvariantCulture).Equals(argument)) continue;
                subject = pawn;
                break;
            }
            if (subject is not null) break;
        }
        if (subject?.genes is not { } genes)
            return "本图没有带基因的小人，基因往返测不到（通道已建，载体待实测）";
        var before = Encode(genes);
        var xenoBefore = genes.Xenogenes.Count;
        var endoBefore = genes.Endogenes.Count;
        var abilitiesBefore = subject.abilities?.AllAbilitiesForReading?.Count ?? -1;
        Apply(new ReplicaPawnGenes(subject.thingIDNumber, before));
        var after = Encode(subject.genes);
        var abilitiesAfter = subject.abilities?.AllAbilitiesForReading?.Count ?? -1;
        return "基因往返：" + (before == after ? "一致" : "不一致")
            + $"（{before} → {after}）；异种基因 {xenoBefore} → {subject.genes.Xenogenes.Count}"
            + $"，内源基因 {endoBefore} → {subject.genes.Endogenes.Count}"
            + $"；能力 {abilitiesBefore} → {abilitiesAfter}；样本 pawn{subject.thingIDNumber}";
    }

    internal static string Summary()
    {
        var withGenes = 0;
        var xeno = 0;
        var endo = 0;
        var overridden = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.genes is not { } genes) continue;
                if (genes.Xenogenes.Count == 0 && genes.Endogenes.Count == 0) continue;
                withGenes++;
                xeno += genes.Xenogenes.Count;
                endo += genes.Endogenes.Count;
                for (var i = 0; i < genes.Xenogenes.Count; i++)
                    if (genes.Xenogenes[i].Overridden) overridden++;
            }
        return $"{withGenes} 个小人有基因（异种 {xeno} 条 / 内源 {endo} 条，其中 {overridden} 条被覆盖）"
            + $" / 发 {GenesSent} / 应用 {GenesApplied}（改动 {GenesChanged} 条，失败 {ApplyFailures}）"
            + $" / 样本 {LastSample?.Genes ?? "（无）"}";
    }
}
