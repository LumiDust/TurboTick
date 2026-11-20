using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Auto-slaughter limits, per map and per animal species.
///
/// <para><b>Why a channel and not a patch.</b> `AutoSlaughterManager.configs` (`:64706`) is a scribed list, so it rides
/// the save - but the auto-slaughter dialog edits it during play, and the edit calls
/// <see cref="AutoSlaughterManager.Notify_ConfigChanged"/> (`:64918`) only to dirty a cache. Nothing publishes the
/// numbers. The reader is `AnimalsToSlaughter` (`:64712`), and the authority turns that list into slaughter bills, so a
/// limit typed on a replica is invisible to the side that actually kills animals.</para>
///
/// <para><b>Diffed, not a command.** A command patch on the dialog's cell would have to fire on the number cell's
/// commit, which is a `Widgets.TextFieldNumeric` callback inside a row helper - and the same fields are also written by
/// `ExposeData` and by a gravship landing copying its configs onto a new map (`:706888`). Comparing the whole (tiny)
/// table every frame cannot miss a writer, which is the rule this project settled on for `PlaySettings`,
/// `hostilityResponse` and `medCare`.</para>
///
/// <para><b>Only the limits travel.** The five `uiMax*Buffer` strings are the text being typed on one screen; copying
/// them would move another peer's caret. `animal` travels as a defName because a `ThingDef` has no id.</para></summary>
internal static class AutoSlaughterSync
{
    /// <summary>What was sent last frame, keyed by `mapId|defName`. Only changed rows leave.</summary>
    private static readonly Dictionary<string, string> sent = [];

    internal static int Sent { get; private set; }

    internal static int Applied { get; private set; }

    internal static int ConfigsSeen { get; private set; }

    /// <summary>`animal` defName this peer has no def for - a mod list difference, not a decode failure.</summary>
    internal static int UnknownDef { get; private set; }

    /// <summary>A row for a map this peer does not have. Counted apart from `UnknownDef` for the same reason every other
    /// channel separates "no such map" from "no such def": they are different faults with the same symptom.</summary>
    internal static int MissingMap { get; private set; }

    internal static int Failures { get; private set; }

    internal static List<ReplicaAutoSlaughterConfig>? Publish()
    {
        var rows = new List<ReplicaAutoSlaughterConfig>();
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var map in Find.Maps)
            {
                var manager = map?.autoSlaughterManager;
                if (manager?.configs is null) continue;
                foreach (var config in manager.configs)
                {
                    if (config?.animal is null) continue;
                    ConfigsSeen++;
                    var key = map!.uniqueID.ToString(CultureInfo.InvariantCulture) + "|" + config.animal.defName;
                    seen.Add(key);
                    // The comparison string is built in the same order as the record, so a diff can never agree while the
                    // payload disagrees.
                    var stamp = string.Join(",", new[]
                    {
                        config.maxTotal.ToString(CultureInfo.InvariantCulture),
                        config.maxMales.ToString(CultureInfo.InvariantCulture),
                        config.maxMalesYoung.ToString(CultureInfo.InvariantCulture),
                        config.maxFemales.ToString(CultureInfo.InvariantCulture),
                        config.maxFemalesYoung.ToString(CultureInfo.InvariantCulture),
                        config.allowSlaughterPregnant ? "1" : "0",
                        config.allowSlaughterBonded ? "1" : "0",
                    });
                    if (sent.TryGetValue(key, out var previous) && previous == stamp) continue;
                    sent[key] = stamp;
                    rows.Add(new ReplicaAutoSlaughterConfig(map.uniqueID, config.animal.defName,
                        config.maxTotal, config.maxMales, config.maxMalesYoung,
                        config.maxFemales, config.maxFemalesYoung,
                        config.allowSlaughterPregnant, config.allowSlaughterBonded));
                }
            }
            // A config the authority deleted must not stay "already sent" forever, or re-adding the same species would be
            // diffed against a row nobody has any more.
            if (sent.Count > seen.Count)
                foreach (var key in new List<string>(sent.Keys))
                    if (!seen.Contains(key)) sent.Remove(key);
            Sent += rows.Count;
        }
        catch (Exception error)
        {
            Failures++;
            Log.Warning("[TurboTick] AutoSlaughterSync 打包失败：" + error.GetType().Name + ": " + error.Message);
            return null;
        }
        return rows.Count > 0 ? rows : null;
    }

    internal static void Apply(ReplicaAutoSlaughterConfig state)
    {
        try
        {
            Map? map = null;
            foreach (var candidate in Find.Maps)
                if (candidate.uniqueID == state.Map) { map = candidate; break; }
            if (map?.autoSlaughterManager?.configs is null) { MissingMap++; return; }
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(state.Animal);
            if (def is null) { UnknownDef++; return; }
            var manager = map.autoSlaughterManager;
            AutoSlaughterConfig? config = null;
            foreach (var candidate in manager.configs)
                if (candidate?.animal == def) { config = candidate; break; }
            if (config is null)
            {
                // Created rather than dropped: a species the authority added to the table is a row the player will see in
                // the dialog, and a replica without it would show a shorter list than the colony it mirrors.
                config = new AutoSlaughterConfig { animal = def };
                manager.configs.Add(config);
            }
            config.maxTotal = state.MaxTotal;
            config.maxMales = state.MaxMales;
            config.maxMalesYoung = state.MaxMalesYoung;
            config.maxFemales = state.MaxFemales;
            config.maxFemalesYoung = state.MaxFemalesYoung;
            config.allowSlaughterPregnant = state.AllowPregnant;
            config.allowSlaughterBonded = state.AllowBonded;
            // Vanilla's own side effect after an edit (`:625940-625943`). Without it a replica that is asked for
            // `AnimalsToSlaughter` keeps serving a cache computed from the old limits - and this project has already paid
            // for that mistake once with the forbidden overlay, where the value was right and the derived state was stale.
            manager.Notify_ConfigChanged();
            Applied++;
        }
        catch (Exception error)
        {
            Failures++;
            Log.Warning("[TurboTick] AutoSlaughterSync 应用失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Read by `SyncCensus`, which prints the row label itself - so this must not repeat "自动屠宰".
    /// Measured: the first version did, and the census line read `自动屠宰：自动屠宰：发 111 …`.</summary>
    internal static string Summary() =>
        $"发 {Sent} / 应用 {Applied} / 配置 {ConfigsSeen} 条"
        + $"（缺图 {MissingMap}、缺 def {UnknownDef}、失败 {Failures}）";
}
