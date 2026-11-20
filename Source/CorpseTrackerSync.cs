using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The unnatural corpse's state machine, carried whole (round 164).
///
/// <para><b>Why.</b> `UnnaturalCorpseTracker` (`:50860-50874`) holds the corpse's awakening timer, its teleport and
/// escape flags, the pawn it haunts and the research that destroys it. `ResidualCounterSync` carried exactly one of those
/// fields (`research`), so on a replica the corpse stayed in whatever state the session started in: never awakening,
/// never teleporting, and `CompDisableUnnaturalCorpse` answering from stale flags.</para>
///
/// <para><b>Why reflection.</b> Every one of these fields is `private` and none has a setter. The round-144 rule applies:
/// the field is the state, so the field is what travels; a property that recomputes it would be a second truth. The names
/// are pinned by the dump, and a rename shows up as `Unresolved` instead of silently doing nothing.</para>
///
/// <para><b>Why the corpse's id.</b> `UnnaturalCorpseTracker.loadID` identifies the tracker, but the replica holds the
/// *thing*: `ReplicaThing` is keyed by `thingIDNumber`, and a tracker rebuilt from a fragment would get a new load id.
/// The corpse's own id is the only identity both ends already agree on.</para></summary>
internal static class CorpseTrackerSync
{
    private static ReplicaCorpseTracker? last;

    internal static long Published { get; private set; }
    internal static long Applied { get; private set; }
    internal static int Unresolved { get; private set; }

    internal static void Reset()
    {
        last = null;
        Published = 0;
        Applied = 0;
        Unresolved = 0;
    }

    /// <summary>The one unnatural corpse the game has, or null. A colony may have none for a whole session.</summary>
    private static UnnaturalCorpse? Corpse()
    {
        // A corpse the probe just spawned is not in `listerThings` yet on the tick it appears, and the probe holds
        // it anyway - so it is a candidate in its own right.
        if (fixture is { Destroyed: false } spawned && spawned.Tracker is not null) return spawned;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
                if (thing is UnnaturalCorpse corpse && corpse.Tracker is not null)
                    return corpse;
        return null;
    }

    internal static ReplicaCorpseTracker? Publish()
    {
        var corpse = Corpse();
        var tracker = corpse?.Tracker;
        if (corpse is null || tracker is null) return null;
        var row = new ReplicaCorpseTracker(corpse.thingIDNumber,
            Int(tracker, "awakenTick"), Int(tracker, "disappearTick"), Int(tracker, "spawnedTick"),
            Int(tracker, "ticksToKill"),
            Ref(Pawn(tracker, "haunted")), Ref(Pawn(tracker, "awokenPawn")),
            (byte)(Flag(tracker, "escape") ? 1 : 0), (byte)(Flag(tracker, "mentalBreak") ? 1 : 0),
            (byte)(Flag(tracker, "teleportIn") ? 1 : 0),
            Float(tracker, "research"), Float(tracker, "researchToDestroy"));
        if (last != row)
        {
            last = row;
            Published++;
        }
        return row;
    }

    internal static void Apply(ReplicaCorpseTracker row)
    {
        try
        {
            var corpse = Corpse();
            if (corpse is null || corpse.thingIDNumber != row.Corpse || corpse.Tracker is null)
            {
                Unresolved++;
                return;
            }
            var tracker = corpse.Tracker;
            Set(tracker, "awakenTick", row.AwakenTick);
            Set(tracker, "disappearTick", row.DisappearTick);
            Set(tracker, "spawnedTick", row.SpawnedTick);
            Set(tracker, "ticksToKill", row.TicksToKill);
            Set(tracker, "haunted", PawnById(row.Haunted));
            Set(tracker, "awokenPawn", PawnById(row.AwokenPawn));
            Set(tracker, "escape", row.Escape != 0);
            Set(tracker, "mentalBreak", row.MentalBreak != 0);
            Set(tracker, "teleportIn", row.TeleportIn != 0);
            Set(tracker, "research", row.Research);
            Set(tracker, "researchToDestroy", row.ResearchToDestroy);
            Applied++;
        }
        catch (Exception error)
        {
            Unresolved++;
            Log.Warning("[TurboTick] corpse tracker apply failed: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Reads the three fields the probe compares, and says whether a corpse exists at all.</summary>
    internal static bool ProbeRead(out string line, out int awakenTick, out bool escape, out float research)
    {
        line = "没有尸体（这是当前存档的夹具限制，不是通道限制）";
        awakenTick = 0;
        escape = false;
        research = 0f;
        var corpse = Corpse();
        if (corpse?.Tracker is not { } tracker) return false;
        awakenTick = Int(tracker, "awakenTick");
        escape = Flag(tracker, "escape");
        research = Float(tracker, "research");
        line = $"尸体 #{corpse.thingIDNumber}：觉醒戳 {awakenTick}、逃逸 {(escape ? "是" : "否")}、研究 {research:0.###}";
        return true;
    }

    /// <summary>Writes the three fields the probe compares. Only the probe uses this: the channel never writes locally.</summary>
    private static UnnaturalCorpse? fixture;

    /// <summary>Spawns a throwaway unnatural corpse and gives it a tracker, so the channel can be measured in a save
    /// that has none. The tracker's constructor is public (`:7630` metadata: the `.ctor(Pawn, UnnaturalCorpse)` row is
    /// `Public`), and the corpse's own `tracker` field is written directly - the registry
    /// (`RegisterUnnaturalCorpse`, `:723738`) would also mark the codex discovered, which a probe must not do.</summary>
    internal static bool SpawnFixtureCorpse(out string note)
    {
        note = "";
        if (Corpse() is not null) return false;
        var map = Find.CurrentMap;
        var pawn = map?.mapPawns.FreeColonists.FirstOrDefault();
        if (map is null || pawn is null) { note = "没有地图或殖民者，无法造夹具尸体"; return false; }
        var def = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(candidate =>
            typeof(UnnaturalCorpse).IsAssignableFrom(candidate.thingClass));
        if (def is null) { note = "没有 `UnnaturalCorpse` 的 def（异常 DLC 未启用？）"; return false; }
        var corpse = (UnnaturalCorpse)ThingMaker.MakeThing(def);
        GenSpawn.Spawn(corpse, CellFinder.RandomClosewalkCellNear(pawn.Position, map, 4), map);
        Set(corpse, "tracker", new UnnaturalCorpseTracker(pawn, corpse));
        fixture = corpse;
        note = $"夹具尸体 #{corpse.thingIDNumber}（{def.defName}）已造";
        return true;
    }

    internal static void RemoveFixtureCorpse()
    {
        if (fixture is null) return;
        if (!fixture.Destroyed) fixture.Destroy();
        fixture = null;
    }

    internal static void ProbeSet(int awakenTick, bool escape, float research)
    {
        if (Corpse()?.Tracker is not { } tracker) return;
        Set(tracker, "awakenTick", awakenTick);
        Set(tracker, "escape", escape);
        Set(tracker, "research", research);
    }

    // No label of its own: the census row already carries one, and a summary that repeats it reads
    // "尸体追踪：尸体追踪 发 …".
    internal static string Summary() => $"发 {Published} / 应用 {Applied}（未解析 {Unresolved}）";

    private static Pawn? PawnById(int id)
    {
        if (id == 0) return null;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn.thingIDNumber == id)
                    return pawn;
        return null;
    }

    private static readonly Dictionary<(Type, string), FieldInfo?> cache = [];

    private static FieldInfo? Field(object target, string name)
    {
        var key = (target.GetType(), name);
        if (cache.TryGetValue(key, out var known)) return known;
        var field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        cache[key] = field;
        return field;
    }

    private static void Set(object target, string name, object? value)
    {
        var field = Field(target, name);
        if (field is null) return;
        field.SetValue(target, value);
    }

    private static int Int(object target, string name) => Field(target, name)?.GetValue(target) is int value ? value : 0;
    private static float Float(object target, string name) => Field(target, name)?.GetValue(target) is float value ? value : 0f;
    private static bool Flag(object target, string name) => Field(target, name)?.GetValue(target) is true;
    private static Pawn? Pawn(object target, string name) => Field(target, name)?.GetValue(target) as Pawn;
    private static int Ref(Pawn? pawn) => pawn?.thingIDNumber ?? 0;
}
