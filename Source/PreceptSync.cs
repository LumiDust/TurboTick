using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The precept *set* of every ideoligion, and which pawns hold its roles.
///
/// <para><b>What was already carried and what was not.</b> <see cref="IdeoSync"/> sends a table per ideoligion with its
/// `id`, name, colour and development tracker. Precepts were never in it, so a replica's precept list is whatever the save
/// held. That is enough until the authority reforms, removes or adds one - `Ideo.RemovePrecept` (`:492723-492748`) and
/// `Ideo.AddPrecept` (`:492639-492677`) run from reform rituals, dev actions and meme changes.</para>
///
/// <para><b>Why precepts are a simulation input, not decoration.</b> `StatWorker.GetValueUnfinalized` (`:715789-715811`)
/// does two things with the list for every pawn that has an ideoligion:</para>
/// <list type="bullet">
/// <item>adds `precept.def.statOffsets` - shooting accuracy in darkness, melee hit chance, comfort temperature bounds
/// (`:292961-292962`), and the rest of the ideoligion stat table;</item>
/// <item>adds `conditionalStatAffecter.Applies(req)` offsets, and then adds `RoleEffect_PawnStatOffset.modifier` for the
/// role the pawn holds via `Ideo.GetRole(pawn)` (`:715812-715821`), which is `Precept_Role.IsAssigned` over
/// `RolesListForReading` (`:493131-493141`).</item>
/// </list>
/// So a missing precept changes work speed and combat stats, and a missing *role assignment* changes them for one specific
/// pawn - which is why the holder's id travels with the def instead of being inferred from the pawn channel.</para>
///
/// <para><b>What this deliberately does not rebuild.</b> A `Precept_Ritual`'s pattern, a `Precept_Building`'s room
/// requirements and every `IdeoBuildingPresenceDemand` are deep object graphs whose authority-side meaning is "what the
/// player is being asked to build", not a stat input. Creating one from a bare def here would produce a precept that looks
/// present and demands nothing. Ritual and building precepts that are *missing* on the replica are therefore created and
/// counted as unreconstructed, rather than silently pretended to be complete.</para>
///
/// <para><b>Why removal passes `replacing: true`.</b> `RemovePrecept(precept)` with the default `replacing: false`
/// (`:492740-492743`) replaces the removed precept with a **random** one weighted by `defaultSelectionWeight` when the def's
/// own weight is non-positive. On a replica that would inject a precept the authority never chose - a silent divergence
/// that would then be reconciled away next frame, i.e. a flicker with a random walk behind it.</para>
///
/// <para><b>Why `AddPrecept(init: true)`.</b> `Init` is what sets a role precept `active` and fills its abilities
/// (`:514620-514626`, `:514660-514668`). Creating one with `init: false` produces a role nobody can hold and that grants
/// nothing.</para>
/// </summary>
internal static class PreceptSync
{
    private static readonly Dictionary<int, string> sent = [];

    internal static long TablesSent { get; private set; }
    internal static long IdolsApplied { get; private set; }
    internal static long PreceptsAdded { get; private set; }
    internal static long PreceptsRemoved { get; private set; }
    internal static long RolesAssigned { get; private set; }
    internal static int Unreconstructed { get; private set; }
    internal static int ApplyFailures { get; private set; }
    internal static string? LastSample { get; private set; }

    /// <summary>Name rows sent / applied, and rows whose precept this peer cannot find (round 147).</summary>
    internal static long NamesSent { get; private set; }
    internal static long NamesApplied { get; private set; }
    internal static int NameUnresolved { get; private set; }

    /// <summary>Per-precept payload rows sent / applied (round 148), and rows this peer cannot address.</summary>
    internal static long PayloadsSent { get; private set; }
    internal static long PayloadsApplied { get; private set; }
    internal static int PayloadUnresolved { get; private set; }

    /// <summary>Last name/lock statement sent per `(ideo, def, index)`. Separate from <see cref="sent"/>, which keys the
    /// table payload by ideo alone - the two channels move on different edits (a role assignment versus a rename).
    ///
    /// <para>The index is the ordinal among precepts of the same def inside `PreceptsListForReading`. `(ideo, def)` alone
    /// collides whenever a `PreceptDef` has `allowMultiplePrecepts` and the ideoligion carries two of them - the replica
    /// would rename the first and never the second.</para></summary>
    private static readonly Dictionary<(int Ideo, string Def, int Index), string> sentNames = [];

    /// <summary>Last payload statement sent per `(ideo, def, index)`; a precept's payload changes when the player edits
    /// the ideoligion, not every frame.</summary>
    private static readonly Dictionary<(int Ideo, string Def, int Index), string> sentPayloads = [];

    /// <summary>The ordinal of every precept inside its ideoligion, keyed by `(ideo, def)` and counting same-def
    /// occurrences. Both peers walk `PreceptsListForReading` in the order the table channel created them.</summary>
    private static IEnumerable<(Precept Precept, string Def, int Index)> Numbered(Ideo ideo)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var precept in ideo.PreceptsListForReading)
        {
            if (precept?.def?.defName is not { Length: > 0 } def) continue;
            var index = seen.TryGetValue(def, out var count) ? count : 0;
            seen[def] = index + 1;
            yield return (precept, def, index);
        }
    }

    internal static void Reset()
    {
        sent.Clear();
        sentNames.Clear();
        sentPayloads.Clear();
        NamesSent = 0;
        NamesApplied = 0;
        NameUnresolved = 0;
        PayloadsSent = 0;
        PayloadsApplied = 0;
        PayloadUnresolved = 0;
        TablesSent = 0;
        IdolsApplied = 0;
        PreceptsAdded = 0;
        PreceptsRemoved = 0;
        RolesAssigned = 0;
        Unreconstructed = 0;
        ApplyFailures = 0;
        LastSample = null;
    }

    /// <summary>`ideoId:def|def@holder;def|def@holder` - the def name, and for a role precept the `thingIDNumber` of its
    /// holder (0 for an unfilled role).
    ///
    /// <para>Sorted ordinally within each ideoligion so a set the player did not touch does not look touched. A
    /// `Precept_RoleMulti` can hold several pawns; only the single-holder classes send an id, because multi-role holders are
    /// already the whole point of `Precept_RoleMulti.Assign` (`:514631-514652`) and re-deriving that list from one id per
    /// def would lose everyone after the first. Multi roles send `@m` and are counted separately.</para></summary>
    private static string Encode(Ideo ideo)
    {
        var entries = new List<string>(ideo.PreceptsListForReading.Count);
        foreach (var precept in ideo.PreceptsListForReading)
        {
            if (precept?.def?.defName is not { Length: > 0 } def) continue;
            var suffix = string.Empty;
            if (precept is Precept_Role role)
            {
                // `ChosenPawnSingle()` returns null for the multi variant by design (`:514622-514625`), so `@m` is not an
                // error marker - it says "this def's holders are not expressible in this field".
                var single = role.ChosenPawnSingle();
                suffix = precept is Precept_RoleMulti ? "@m"
                    : single is null ? "@0"
                    : "@" + single.thingIDNumber.ToString(CultureInfo.InvariantCulture);
            }
            entries.Add(def + suffix);
        }
        entries.Sort(StringComparer.Ordinal);
        return ideo.id.ToString(CultureInfo.InvariantCulture) + ":" + string.Join("|", entries);
    }

    internal static List<ReplicaPreceptTable>? Publish()
    {
        List<ReplicaPreceptTable>? changed = null;
        try
        {
            var ideos = Find.IdeoManager?.IdeosListForReading;
            if (ideos is null || ideos.Count == 0) return null;
            var live = new HashSet<int>();
            foreach (var ideo in ideos)
            {
                if (ideo is null) continue;
                var id = ideo.id;
                live.Add(id);
                var encoded = Encode(ideo);
                if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                sent[id] = encoded;
                LastSample = encoded;
                (changed ??= []).Add(new ReplicaPreceptTable(id, encoded));
                TablesSent++;
            }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] precept publish: " + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaPreceptTable state)
    {
        try
        {
            var ideo = IdeoById(state.Id);
            if (ideo is null) return;
            var colon = state.Payload.IndexOf(':');
            if (colon < 0) return;
            var wanted = Parse(state.Payload[(colon + 1)..]);

            // Remove first, so a def that moved from one holder to another is not briefly doubled - `AddPrecept`
            // refuses a duplicate and logs (`:492646-492650`), and the log line would be the only trace.
            for (var i = ideo.PreceptsListForReading.Count - 1; i >= 0; i--)
            {
                var have = ideo.PreceptsListForReading[i];
                if (have?.def is null) continue;
                if (wanted.ContainsKey(have.def.defName)) continue;
                ideo.RemovePrecept(have, replacing: true);
                PreceptsRemoved++;
            }

            foreach (var pair in wanted)
            {
                var def = DefDatabase<PreceptDef>.GetNamedSilentFail(pair.Key);
                if (def is null) continue;
                Precept? precept = FindByDef(ideo, def);
                if (precept is null)
                {
                    precept = PreceptMaker.MakePrecept(def);
                    ideo.AddPrecept(precept, init: true);
                    PreceptsAdded++;
                    // A ritual or building demand created from a bare def carries no pattern and no room requirements;
                    // counting it keeps "created" from reading as "correct".
                    if (precept is Precept_Ritual || precept is Precept_Building) Unreconstructed++;
                }
                if (precept is Precept_Role role && pair.Value.Length > 0 && pair.Value != "m")
                {
                    var holderId = int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var parsed) ? parsed : 0;
                    var holder = holderId > 0 ? FindPawnAny(holderId) : null;
                    if (holder is not null && !role.IsAssigned(holder))
                    {
                        // `addThoughts: false`: the thought is the authority's to grant, and a replica granting it would
                        // double-count a mood effect that is already carried by the memory channel.
                        role.Assign(holder, addThoughts: false);
                        RolesAssigned++;
                    }
                }
            }
            IdolsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] precept apply failed (ideo " + state.Id + "): "
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    private static Precept? FindByDef(Ideo ideo, PreceptDef def)
    {
        foreach (var precept in ideo.PreceptsListForReading)
            if (precept?.def == def) return precept;
        return null;
    }

    private static Dictionary<string, string> Parse(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (section.Length == 0) return result;
        foreach (var entry in section.Split('|'))
        {
            if (entry.Length == 0) continue;
            var at = entry.IndexOf('@');
            if (at < 0) result[entry] = string.Empty;
            else result[entry[..at]] = entry[(at + 1)..];
        }
        return result;
    }

    private static Ideo? IdeoById(int id)
    {
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null) return null;
        foreach (var ideo in ideos)
            if (ideo is not null && ideo.id == id) return ideo;
        return null;
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

    /// <summary>Idempotent round-trip per ideoligion. Same reasoning as the trait and gene probes: the apply is a reconcile
    /// that can reorder or re-create, and a count cannot tell that from a correct no-op.</summary>
    internal static string Probe(string? argument)
    {
        var ideos = Find.IdeoManager?.IdeosListForReading;
        if (ideos is null || ideos.Count == 0) return "本图没有 ideoligion，precept 往返测不到（通道已建，载体待实测）";
        var parts = new List<string>(ideos.Count);
        foreach (var ideo in ideos)
        {
            if (ideo is null) continue;
            var before = Encode(ideo);
            var countBefore = ideo.PreceptsListForReading.Count;
            Apply(new ReplicaPreceptTable(ideo.id, before));
            var after = Encode(ideo);
            parts.Add("ideo" + ideo.id + "：" + (before == after ? "一致" : "不一致")
                + $"（{countBefore} → {ideo.PreceptsListForReading.Count} 条）");
        }
        return "precept 往返：" + string.Join("；", parts) + $"；样本 {LastSample ?? "（无）"}";
    }

    /// <summary>One row per precept whose name or lock flag changed since the last publish.
    ///
    /// <para>Reads `Precept.Label` (`public virtual string Label => name`) rather than the protected field, so the value
    /// that travels is exactly the one the ideo screen prints. The change key includes the lock flag because a lock with
    /// an unchanged name is still a real edit: it decides whether the authority regenerates the name later
    /// (`:493056`).</para></summary>
    internal static List<ReplicaPreceptName>? PublishNames()
    {
        List<ReplicaPreceptName>? changed = null;
        try
        {
            var ideos = Find.IdeoManager?.IdeosListForReading;
            if (ideos is null || ideos.Count == 0) return null;
            var live = new HashSet<(int, string, int)>();
            foreach (var ideo in ideos)
            {
                if (ideo?.PreceptsListForReading is not { } precepts) continue;
                foreach (var (precept, def, index) in Numbered(ideo))
                {
                    var key = (ideo.id, def, index);
                    live.Add(key);
                    // `Label` is the raw name; `LabelCap` would capitalise, and the authority stores the raw one.
                    var name = precept.Label ?? string.Empty;
                    var state = name + (precept.nameLocked ? "\u0001" : "\u0000");
                    if (sentNames.TryGetValue(key, out var previous) && previous == state) continue;
                    sentNames[key] = state;
                    (changed ??= []).Add(new ReplicaPreceptName(ideo.id, def, index, name, precept.nameLocked));
                    NamesSent++;
                }
            }
            foreach (var key in new List<(int, string, int)>(sentNames.Keys))
                if (!live.Contains(key)) sentNames.Remove(key);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 戒律名字发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>Writes one precept's name and lock flag.
    ///
    /// <para>Through `SetName` (`:499729`), not the field: it clears the cached label and re-runs `RegenerateName` for
    /// every precept whose def names this one as `takeNameFrom`, which is what vanilla does when the player renames.
    /// Writing the field would leave a dependent precept printing the old name until something else rebuilt it.
    /// `nameLocked` itself is a plain public field (`:499254`).</para></summary>
    internal static void ApplyName(ReplicaPreceptName state)
    {
        try
        {
            var ideo = IdeoById(state.Ideo);
            if (ideo?.PreceptsListForReading is null || state.Def is not { Length: > 0 })
            {
                NameUnresolved++;
                return;
            }
            Precept? found = null;
            foreach (var (precept, def, index) in Numbered(ideo))
                if (def == state.Def && index == state.Index) { found = precept; break; }
            if (found is null)
            {
                NameUnresolved++;
                return;
            }
            if (found.nameLocked != state.Locked) found.nameLocked = state.Locked;
            if (!string.Equals(found.Label, state.Name, StringComparison.Ordinal) && state.Name.Length > 0)
                found.SetName(state.Name);
            NamesApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] 戒律名字应用失败 " + state.Def + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Writes one `<category>/<styleDef>` style for a building, when the row carries one.
    ///
    /// <para>`SetStyleForThingDef(ThingDef, StyleCategoryPair)` (`:494884-494887`) is the only public write into
    /// `IdeoStyleTracker.styleForThingDef`, so the pair is built here rather than reached for by reflection.</para>
    ///
    /// <para>`styleDef` may be `-`: a category can own the thing without pinning one style def, and
    /// `StyleCategoryPair.styleDef` is legitimately null then.</para></summary>
    private static bool ApplyStyle(Ideo ideo, ThingDef thing, string? style)
    {
        if (style is not { Length: > 0 } || ideo?.style is null) return false;
        var slash = style.IndexOf('/');
        if (slash <= 0) { PayloadUnresolved++; return false; }
        var categoryName = style.Substring(0, slash);
        var styleName = style.Substring(slash + 1);
        var category = DefDatabase<StyleCategoryDef>.GetNamedSilentFail(categoryName);
        if (category is null) { PayloadUnresolved++; return false; }
        ThingStyleDef? styleDef = null;
        if (styleName is { Length: > 0 } && styleName != "-")
        {
            styleDef = DefDatabase<ThingStyleDef>.GetNamedSilentFail(styleName);
            if (styleDef is null) PayloadUnresolved++;
        }
        var current = ideo.style.StyleForThingDef(thing);
        if (current?.category == category && current.styleDef == styleDef) return false;
        ideo.style.SetStyleForThingDef(thing, new StyleCategoryPair { category = category, styleDef = styleDef });
        return true;
    }

    /// <summary>Publishes the chosen def of every thing / apparel / weapon precept, sparsely.
    ///
    /// <para>The three payload fields are the ones the ideoligion editor writes: `StyleCatOverride.AddWeaponPreceptOption`
    /// (`:496677-496688`) sets `Precept_Weapon.noble` / `.despised` from a `WeaponClassPairDef`, and the apparel/building
    /// menu at `:496618-496630` sets `Precept_Apparel.apparelDef` / `Precept_ThingDef.ThingDef`. A replica builds precepts
    /// from their defs and `Init` picks its own, so without these rows the two peers hold different requirements under the
    /// same label - and the label itself is generated from the payload.</para></summary>
    internal static List<ReplicaPreceptPayload>? PublishPayloads()
    {
        List<ReplicaPreceptPayload>? changed = null;
        try
        {
            var ideos = Find.IdeoManager?.IdeosListForReading;
            if (ideos is null || ideos.Count == 0) return null;
            var live = new HashSet<(int, string, int)>();
            foreach (var ideo in ideos)
            {
                if (ideo?.PreceptsListForReading is null) continue;
                foreach (var (precept, def, index) in Numbered(ideo))
                {
                    string? thing = null, apparel = null, noble = null, despised = null, style = null;
                    switch (precept)
                    {
                        case Precept_ThingDef thingDefPrecept:
                            thing = thingDefPrecept.ThingDef?.defName;
                            style = StyleOf(ideo, thingDefPrecept.ThingDef);
                            break;
                        case Precept_Apparel apparelPrecept: apparel = apparelPrecept.apparelDef?.defName; break;
                        case Precept_Weapon weaponPrecept:
                            noble = weaponPrecept.noble?.defName;
                            despised = weaponPrecept.despised?.defName;
                            break;
                    }
                    if (thing is null && apparel is null && noble is null && despised is null) continue;
                    var key = (ideo.id, def, index);
                    live.Add(key);
                    var state = (thing ?? "-") + "|" + (apparel ?? "-") + "|" + (noble ?? "-") + "|" + (despised ?? "-")
                        + "|" + (style ?? "-");
                    if (sentPayloads.TryGetValue(key, out var previous) && previous == state) continue;
                    sentPayloads[key] = state;
                    (changed ??= []).Add(new ReplicaPreceptPayload(ideo.id, def, index, thing, apparel, noble, despised,
                        style));
                    PayloadsSent++;
                }
            }
            foreach (var key in new List<(int, string, int)>(sentPayloads.Keys))
                if (!live.Contains(key)) sentPayloads.Remove(key);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 戒律载荷发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    /// <summary>The ideoligion's style for a building as `<category>/<styleDef>`, or null when there is none.
    ///
    /// <para>`IdeoStyleTracker.StyleForThingDef` (`:494840-494886`) returns the cached pair, and on a miss walks
    /// `ideo.thingStyleCategories` and **caches** the pair it lands on. Reading it here therefore warms the authority's
    /// cache with the value it would have computed anyway - the same deterministic walk the replica would take if its
    /// category list matched. It is only the miss branch of `Notify_ThingDefSet` that rolls, which is exactly why this
    /// column exists.</para></summary>
    private static string? StyleOf(Ideo ideo, ThingDef? thing)
    {
        if (ideo is null || thing is null) return null;
        try
        {
            var pair = ideo.style?.StyleForThingDef(thing);
            if (pair?.category is null) return null;
            return pair.category.defName + "/" + (pair.styleDef?.defName ?? "-");
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 戒律风格读取失败 " + (thing.defName ?? "?") + "：" + error.GetType().Name + ": "
                + error.Message);
            return null;
        }
    }

    /// <summary>Writes one payload row onto the precept at that ordinal, after checking the precept class.
    ///
    /// <para>`Precept_ThingDef.ThingDef` goes through the property, not the field: its setter runs `Notify_ThingDefSet`
    /// (`:500836-500843`), which resets the ideoligion's style for that building. Writing the field would leave the
    /// styles from the def `Init` had picked.</para>
    ///
    /// <para>`Precept_Weapon.noble` / `.despised` are plain fields (`:500946-500948`) whose caches are `[Unsaved]`, so a
    /// field write is the whole update.</para></summary>
    internal static void ApplyPayload(ReplicaPreceptPayload state)
    {
        try
        {
            var ideo = IdeoById(state.Ideo);
            if (ideo?.PreceptsListForReading is null || state.Def is not { Length: > 0 })
            {
                PayloadUnresolved++;
                return;
            }
            Precept? found = null;
            foreach (var (precept, def, index) in Numbered(ideo))
                if (def == state.Def && index == state.Index) { found = precept; break; }
            if (found is null)
            {
                PayloadUnresolved++;
                return;
            }
            var applied = false;
            if (state.Thing is { Length: > 0 } thing && found is Precept_ThingDef thingPrecept)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(thing);
                if (def is null) PayloadUnresolved++;
                else
                {
                    if (thingPrecept.ThingDef != def) { thingPrecept.ThingDef = def; applied = true; }
                    // After the setter: it clears the cached style and may have rolled a random category
                    // (`Notify_ThingDefSet` `:500836-500843`). The synced pair is the last word.
                    applied |= ApplyStyle(ideo, def, state.Style);
                }
            }
            if (state.Apparel is { Length: > 0 } apparel && found is Precept_Apparel apparelPrecept)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(apparel);
                if (def is null) PayloadUnresolved++;
                else if (apparelPrecept.apparelDef != def) { apparelPrecept.apparelDef = def; applied = true; }
            }
            if (found is Precept_Weapon weaponPrecept)
            {
                if (state.Noble is { Length: > 0 } noble)
                {
                    var def = DefDatabase<WeaponClassDef>.GetNamedSilentFail(noble);
                    if (def is null) PayloadUnresolved++;
                    else if (weaponPrecept.noble != def) { weaponPrecept.noble = def; applied = true; }
                }
                if (state.Despised is { Length: > 0 } despised)
                {
                    var def = DefDatabase<WeaponClassDef>.GetNamedSilentFail(despised);
                    if (def is null) PayloadUnresolved++;
                    else if (weaponPrecept.despised != def) { weaponPrecept.despised = def; applied = true; }
                }
            }
            // `applied` is false when the row said exactly what this peer already had: a no-op row is not a failure, but
            // counting it would inflate the census away from "how many precepts this peer actually re-pointed".
            if (applied) PayloadsApplied++;
        }
        catch (Exception error)
        {
            ApplyFailures++;
            if (ApplyFailures <= 6)
                Log.Warning("[TurboTick] 戒律载荷应用失败 " + state.Def + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static string Summary()
    {
        var ideos = 0;
        var precepts = 0;
        var roles = 0;
        var assigned = 0;
        var multi = 0;
        var rituals = 0;
        var locked = 0;
        var manager = Find.IdeoManager?.IdeosListForReading;
        if (manager is not null)
            foreach (var ideo in manager)
            {
                if (ideo is null) continue;
                ideos++;
                precepts += ideo.PreceptsListForReading.Count;
                foreach (var precept in ideo.PreceptsListForReading)
                {
                    if (precept is Precept_Ritual) rituals++;
                    if (precept.nameLocked) locked++;
                    if (precept is not Precept_Role role) continue;
                    roles++;
                    if (precept is Precept_RoleMulti) { multi++; continue; }
                    if (role.ChosenPawnSingle() is not null) assigned++;
                }
            }
        var payloadRows = 0;
        if (manager is not null)
            foreach (var ideo in manager)
            {
                if (ideo?.PreceptsListForReading is null) continue;
                foreach (var precept in ideo.PreceptsListForReading)
                    if (precept is Precept_ThingDef { ThingDef: not null } or Precept_Apparel { apparelDef: not null }
                        or Precept_Weapon { noble: not null }) payloadRows++;
            }
        return $"{ideos} 个 ideoligion 共 {precepts} 条 precept（{roles} 个角色，其中 {assigned} 个有人、{multi} 个多任，"
            + $"{rituals} 条仪式）/ 发 {TablesSent} / 应用 {IdolsApplied}"
            + $"（加 {PreceptsAdded}，删 {PreceptsRemoved}，指派 {RolesAssigned}，未重建 {Unreconstructed}，失败 {ApplyFailures}）"
            + $"；名字 发 {NamesSent} / 应用 {NamesApplied}（未解析 {NameUnresolved}），本端锁住 {locked} 条"
            + $"；载荷 发 {PayloadsSent} / 应用 {PayloadsApplied}（未解析 {PayloadUnresolved}），本端 "
            + $"{payloadRows} 条有载荷";
    }
}
