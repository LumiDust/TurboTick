using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Mod compatibility core. Third-party mods never cooperate with a custom delta protocol, but every
/// mod that survives a save/reload has already described its state through <see cref="IExposable.ExposeData"/>.
/// Scribe is file oriented, yet both halves read and write plain XML: the saver writes to a StringWriter and
/// the loader reads from <c>curXmlParent</c>, a public field. So a field can be round-tripped entirely in
/// memory, which is what this class does.
///
/// This is why compatibility is "the same as save compatibility": if a mod saves and loads its state
/// correctly, its state can also be transported here, with no changes by the mod author.
/// </summary>
internal static class ScribeBridge
{
    /// <summary>How many reference registrations a fragment declared and never read back, summed over every
    /// load. Reported as one line instead of one warning per load; see <see cref="ResolveReferences"/>.</summary>
    private static long unread;
    private static long loads;

    internal static long UnreadReferences => Interlocked.Read(ref unread);

    /// <summary>The object directory is private inside <see cref="CrossRefHandler"/>, and the resolve pass only
    /// registers the objects that took part in the load itself. A reference target that lives outside the
    /// fragment therefore never resolves: the dead pawn inside a corpse is stored by the save under
    /// worldPawns/pawnsDead, not under any map's thing list, so `TakeResolvedRefList` yields null and the
    /// corpse comes back empty (and `Corpse.Bugged` then refuses to spawn it).</summary>
    private static FieldInfo? directoryField;
    private static FieldInfo? objectsField;

    /// <summary>Makes an already existing object findable as a cross-reference target for the next load.
    /// Registering the same object twice is not harmless: <c>LoadedObjectDirectory.RegisterLoaded</c> logs an
    /// error for a duplicate key in both build modes, and a corpse's own fragment and its carried contents
    /// name the same pawn. So the directory's own table is consulted first.</summary>
    internal static void RegisterReference(ILoadReferenceable? reffable)
    {
        if (reffable is null) return;
        try
        {
            directoryField ??= AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory");
            if (directoryField?.GetValue(Scribe.loader.crossRefs) is not LoadedObjectDirectory directory) return;
            objectsField ??= AccessTools.Field(typeof(LoadedObjectDirectory), "allObjectsByLoadID");
            if (objectsField?.GetValue(directory) is System.Collections.IDictionary known
                && known.Contains(reffable.GetUniqueLoadID())) return;
            directory.RegisterLoaded(reffable);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 注册引用目标失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Serialises one exposable object to an XML fragment. Returns null when Scribe is busy, because
    /// ExposeData must never be re-entered from inside another save or load.</summary>
    internal static string? Save(IExposable value, string element = "v")
    {
        if (value is null || Scribe.mode != LoadSaveMode.Inactive) return null;
        try
        {
            // `element` exists for readability at call sites only: DebugOutputFor writes its own node name,
            // so a caller cannot influence it.
            return Scribe.saver.DebugOutputFor(value);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] Scribe save failed: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Same as <see cref="Save"/>, but tolerates a null tracker.
    ///
    /// <para>A pawn has no <c>outfits</c> when it is not a tool user, and no <c>timetable</c> when it is not
    /// humanlike - a hauling animal, a mech and a corpse all reach the policy sampler. Passing those through
    /// <see cref="Save"/> would return null anyway; the difference is that this overload makes "absent tracker" and
    /// "Scribe busy" both read as null at the call site, which is what the sampler wants: it sends only what exists.</para></summary>
    internal static string? SaveOrNull(IExposable? value) => value is null ? null : Save(value);

    /// <summary>Applies an XML fragment previously produced by <see cref="Save"/>. References to other
    /// objects (a job target, a carried item) cannot resolve until every cross reference is resolved, so
    /// that pass runs before the real loader state is restored.</summary>
    internal static bool Load(IExposable value, string xml) => Load(value, xml, null);

    /// <summary>Same as <see cref="Load(IExposable, string)"/>, plus objects from outside the fragment that its
    /// <c>Scribe_References.Look</c> calls name.
    ///
    /// <para><b>Why this overload has to exist.</b> The resolve pass looks a reference up in
    /// <c>LoadedObjectDirectory</c>, and that directory is filled by whoever is doing the loading. A save file names
    /// every object it refers to before any <c>ExposeData</c> runs, so everything is there by the time a reference is
    /// resolved. A fragment does not: it holds only the object's own state, and a field like
    /// <c>ChoiceLetter_GrowthMoment.pawn</c> (<c>Scribe_References.Look(ref pawn, "pawn")</c>, `:662310`) points at
    /// something that was already alive before the fragment existed.</para>
    ///
    /// <para>Measured: `letter-probe` rebuilt a growth-moment letter from its own 1578 B fragment, the load returned
    /// true, and `pawn` came back null — a letter whose options are about a pawn that is not there. Reporting success
    /// was correct in the narrow sense (the XML parsed) and wrong in the only sense that matters.</para></summary>
    internal static bool Load(IExposable value, string xml, IEnumerable<ILoadReferenceable>? referenced)
    {
        if (value is null || string.IsNullOrEmpty(xml) || Scribe.mode != LoadSaveMode.Inactive) return false;
        var document = new XmlDocument();
        try { document.LoadXml("<r>" + xml + "</r>"); }
        catch (XmlException error)
        {
            Log.Warning("[TurboTick] Scribe payload is not valid XML: " + error.Message);
            return false;
        }
        var root = document.DocumentElement?.FirstChild;
        if (root is null) return false;
        // The fragment carries the sender's def, id, map and position. Applying those to a live object
        // assigns Map and Position directly, bypassing the thing grid, so the object would keep drawing
        // with a null map and Plant.Print would throw in GridsUtility.GetSnowDepth. Identity is restored
        // after the load instead of rewritten beforehand: serialising the receiver on every load doubled
        // the Scribe work per object per frame and showed up as multi-second frame hitches.
        var thing = value as Thing;
        // Cheap field reads only: no serialisation on this path.
        var mapBefore = thing?.Map;
        var positionBefore = thing?.Position ?? IntVec3.Invalid;
        var rotationBefore = thing?.Rotation ?? Rot4.North;
        var defBefore = thing?.def;
        var idBefore = thing?.thingIDNumber ?? -1;
        // Was this object on the grid before the load? Thing.ExposeData clears the map index while loading,
        // and GenSpawn.Spawn registers the thing again in listerThings - which is a List<Thing> in 1.7, so a
        // second registration is not rejected. Doing that on a live object left two, then three, then N
        // copies of the same thing in Map.listerThings: every save then logged "Saving Thing with
        // already-used ID <def><id>" once per copy (25 at tick 6000, 50 at tick 12000 in
        // 192.168.0.29:/data/rimworld/det/peerA/console.log). Taking it off the grid first makes the
        // respawn an ordinary re-registration, and only objects that were spawned come back.
        var wasSpawned = thing?.Spawned == true;
        var loader = Scribe.loader;
        var previousParent = loader.curXmlParent;
        var previousPath = loader.curPathRelToParent;
        var previousCurParent = loader.curParent;
        try
        {
            if (wasSpawned && thing is not null) thing.DeSpawn(DestroyMode.Vanish);
            Scribe.mode = LoadSaveMode.LoadingVars;
            loader.curXmlParent = root;
            loader.curPathRelToParent = null;
            // `curParent` is not decoration: `RegisterLoadIDListReadFromXml(list, label)` keys its record by
            // `(Scribe.loader.curParent, path)`, and the resolve pass looks it up under the object that
            // re-runs ExposeData. Leaving it null made every reference list register under `null` and then
            // be looked up under the thing, so nothing was ever consumed and every load ended with
            // "Not all loadIDs which were read were consumed." - 144 of those per Player.log, and the
            // unconsumed list was `CompMetalhorrorInfectible.infectedPawns` on every thing that has comps.
            loader.curParent = value;
            // Vanilla registers the deep-saved object itself before loading it, which is what gets its
            // ExposeData re-run in both later passes. Calling ExposeData directly skipped that, so reference
            // lists and nested PostLoadInit were only ever half applied.
            loader.crossRefs.RegisterForCrossRefResolve(value);
            loader.initer.RegisterForPostLoadInit(value);
            // The object being loaded, plus anything the caller pre-registered, must be in the load-id directory or the
            // resolve pass silently drops their references. A real load registers everything the save names before any
            // ExposeData runs; a fragment names only what it holds, so the object itself is the one guaranteed entry.
            // Measured without this: `letter-probe` rebuilt a `ChoiceLetter_GrowthMoment` from its own 1578 B fragment,
            // the load reported success, and `pawn` came back null - a letter whose options are about a pawn that is
            // not there.
            RegisterReference(value as ILoadReferenceable);
            if (referenced is not null)
                foreach (var each in referenced) RegisterReference(each);
            value.ExposeData();
            // Two-pass load, in vanilla's order: the objects registered their wanted references above, so
            // resolve them now. `curXmlParent` must be null here - the resolve pass works from paths alone,
            // and a non-null node makes `EnterNode` refuse a label the fragment does not carry, which would
            // register the same list a second time.
            Scribe.mode = LoadSaveMode.ResolvingCrossRefs;
            loader.curXmlParent = null;
            loader.curPathRelToParent = null;
            ResolveReferences(loader);
            // RimWorld calls ExposeData again in PostLoadInit during a real load. Mods rely on it:
            // their `if (Scribe.mode == LoadSaveMode.PostLoadInit) { ...rebuild caches... }` branch never ran
            // here, so a component's derived state stayed half applied. Nested objects (the pawn inside a
            // corpse) were skipped entirely by the hand-rolled call, so the whole set runs through the
            // queue vanilla uses.
            loader.initer.DoAllPostLoadInits();
            PostLoad(value);
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] Scribe load failed: " + error.GetType().Name + ": " + error.Message);
            return false;
        }
        finally
        {
            try { loader.crossRefs.Clear(errorIfNotEmpty: false); } catch (Exception) { }
            loader.curXmlParent = previousParent;
            loader.curPathRelToParent = previousPath;
            // Restore the parent too, or the next load in the same frame keys its reference lists under this
            // object instead of its own and they never resolve. A real load always unwinds curParent.
            loader.curParent = previousCurParent;
            Scribe.mode = LoadSaveMode.Inactive;
            if (thing is not null)
            {
                if (defBefore is not null && thing.def is null) thing.def = defBefore;
                if (idBefore >= 0 && thing.thingIDNumber != idBefore) thing.thingIDNumber = idBefore;
                if (wasSpawned) RestoreSpawn(thing, mapBefore, positionBefore, rotationBefore);
                CheckSingleRegistration(thing);
            }
        }
    }

    /// <summary>Vanilla's resolve pass with one difference: leftovers are counted, not warned about.
    /// `CrossRefHandler.ResolveAllCrossReferences()` ends with `Clear(errorIfNotEmpty: true)`, so every
    /// reference a fragment declares and never reads back logs "Not all loadIDs which were read were
    /// consumed." That is structural, not a defect: `Scribe_References.Look` registers a wanted load id even
    /// when the element is absent (`/sourcePrecept` on every thing-style precept, `/lastUser` on every
    /// downed pawn), and `Scribe_Collections.Look` does the same for reference lists (`infectedPawns` on
    /// every thing with comps). Measured on a live room: 1246 warnings in one Player.log, dominated by
    /// 1126 `/sourcePrecept` nulls, while "Could not get load ID" - the actual key-mismatch symptom - was
    /// zero. So the registration work is correct and only its reporting is wrong. Counted here and logged
    /// once per 600 loads instead.</summary>
    private static void ResolveReferences(ScribeLoader loader)
    {
        Scribe.mode = LoadSaveMode.ResolvingCrossRefs;
        var exposables = loader.crossRefs.crossReferencingExposables;
        for (var i = 0; i < exposables.Count; i++)
            if (exposables[i] is ILoadReferenceable reffable) RegisterReference(reffable);
        for (var i = 0; i < exposables.Count; i++)
        {
            try
            {
                loader.curParent = exposables[i];
                loader.curPathRelToParent = null;
                exposables[i].ExposeData();
            }
            catch (Exception error)
            {
                Log.Error("[TurboTick] 解析引用失败：" + exposables[i].GetType().Name + ": " + error);
            }
        }
        loader.curParent = null;
        loader.curPathRelToParent = null;
        Scribe.mode = LoadSaveMode.Inactive;
        var leftover = PendingReferenceCount(loader);
        Interlocked.Add(ref unread, leftover);
        var total = Interlocked.Increment(ref loads);
        try { loader.crossRefs.Clear(errorIfNotEmpty: false); } catch (Exception) { }
        if (total % 600 == 0 && UnreadReferences > 0)
            Log.Message($"[TurboTick] 引用残留 {UnreadReferences} 条（{total} 次回灌，最近一次 {leftover} 条）。"
                + "存档里本来为空的引用会被注册成 null 且无人读取，属预期。");
    }

    private static FieldInfo? idsReadField;
    private static FieldInfo? idListsReadField;

    /// <summary>How many reference registrations are still waiting to be read back.</summary>
    private static int PendingReferenceCount(ScribeLoader loader)
    {
        try
        {
            var bank = loader.crossRefs.loadIDs;
            idsReadField ??= AccessTools.Field(typeof(LoadIDsWantedBank), "idsRead");
            idListsReadField ??= AccessTools.Field(typeof(LoadIDsWantedBank), "idListsRead");
            var singles = idsReadField?.GetValue(bank) as System.Collections.ICollection;
            var lists = idListsReadField?.GetValue(bank) as System.Collections.ICollection;
            return (singles?.Count ?? 0) + (lists?.Count ?? 0);
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Refuses to let a duplicate registration go unnoticed: the symptom (a thing ticking and saving
    /// several times) only shows up hours later as save corruption. Scoped to the thing's own def list so the
    /// cost is proportional to how many of that def exist, not to the size of the map.</summary>
    private static void CheckSingleRegistration(Thing thing)
    {
        var map = thing.Map;
        if (map is null || thing.def is null || !thing.Spawned) return;
        try
        {
            var copies = 0;
            // `ThingsOfDef` short-circuits to `Log.ErrorOnce("Tried to get ThingsOfDef of MinifiedThing…")`
            // for that one def, so the self-check would report a defect it is not looking for. `ThingsMatching`
            // is the call vanilla itself points at, and works for every def.
            var list = map.listerThings.ThingsMatching(ThingRequest.ForDef(thing.def));
            for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], thing)) copies++;
            if (copies > 1)
                Log.Error($"[TurboTick] 回灌后同一物体在 listerThings 里有 {copies} 份：{thing.def.defName}#{thing.thingIDNumber}");
        }
        catch (Exception error) { Log.Warning("[TurboTick] 重复注册自检失败：" + error.Message); }
    }

    /// <summary>Thing.ExposeData deliberately clears the map index on load, because a real save re-registers
    /// every thing afterwards. A live object must be put back on the grid here, or it keeps drawing while
    /// its Map is null and Plant.Print throws in GridsUtility.GetSnowDepth.</summary>
    private static void RestoreSpawn(Thing thing, Map? map, IntVec3 position, Rot4 rotation)
    {
        if (map is null || thing.Destroyed) return;
        if (thing.Spawned && thing.Map == map) return;
        if (thing.Spawned) thing.DeSpawn(DestroyMode.Vanish);
        if (!position.IsValid || !position.InBounds(map)) return;
        // Vanish, not VanishOrMoveAside: the caller already took this thing off the grid, so its own cell is
        // the one it had. If something else occupies it now, refusing is correct - moving that other thing
        // aside would be a silent change nobody asked for.
        GenSpawn.Spawn(thing, position, map, rotation, WipeMode.Vanish);
    }

    /// <summary>Runs an optional post-load hook by reflection: 1.7 exposes several unrelated PostLoad
    /// members without a shared interface, and a mod's Comp may have one.</summary>
    private static void PostLoad(IExposable value)
    {
        for (var type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            var method = type.GetMethod("PostLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                Type.EmptyTypes);
            if (method is null) continue;
            try { method.Invoke(value, null); }
            catch (Exception error) { Log.Warning("[TurboTick] PostLoad: " + error.GetType().Name); }
            return;
        }
    }
}

