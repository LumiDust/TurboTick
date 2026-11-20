using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using UnityEngine;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Authoritative mode: the headless host is the only simulator. A joining client still loads the same
/// save so it has every object to draw, but it never advances its own tick list. Pawn positions arrive
/// in frames and are written straight onto the local objects. Because no local simulation runs, there is
/// no state stream to diverge, and the per-tick hash comparison stops applying to replicas.
/// </summary>
/// <summary>Merges sparse precept-name rows on the exact `(ideo, def)` pair, newer winning.
///
/// <para>Not `EventUnion.ById`: that helper keys on one int, and these rows have no id - the pair is the identity.
/// Folding them onto the ideo id alone would make two precepts of one ideoligion overwrite each other.</para></summary>
internal static class PreceptNameMerge
{
    internal static IReadOnlyList<ReplicaPreceptName>? Merge(IReadOnlyList<ReplicaPreceptName>? newer,
        IReadOnlyList<ReplicaPreceptName>? older)
    {
        if (newer is not { Count: > 0 }) return older;
        if (older is not { Count: > 0 }) return newer;
        var byKey = new Dictionary<(int Ideo, string Def, int Index), ReplicaPreceptName>(older.Count + newer.Count);
        foreach (var state in older)
            if (state is not null && state.Def is { Length: > 0 }) byKey[(state.Ideo, state.Def, state.Index)] = state;
        foreach (var state in newer)
            if (state is not null && state.Def is { Length: > 0 }) byKey[(state.Ideo, state.Def, state.Index)] = state;
        return byKey.Values.OrderBy(state => state.Ideo).ThenBy(state => state.Def, StringComparer.Ordinal)
            .ThenBy(state => state.Index).ToList();
    }
}

/// <summary>Merges sparse precept-payload rows on `(ideo, def, index)`, newer winning.
///
/// <para>Same reason as the names: the payload belongs to one precept instance, not to the ideo and not to the def. A
/// `PreceptDef` with `allowMultiplePrecepts` gives several instances whose payloads differ, so folding on the def alone
/// would make two apparel precepts wear each other's def.</para></summary>
internal static class PreceptPayloadMerge
{
    internal static IReadOnlyList<ReplicaPreceptPayload>? Merge(IReadOnlyList<ReplicaPreceptPayload>? newer,
        IReadOnlyList<ReplicaPreceptPayload>? older)
    {
        if (newer is not { Count: > 0 }) return older;
        if (older is not { Count: > 0 }) return newer;
        var byKey = new Dictionary<(int Ideo, string Def, int Index), ReplicaPreceptPayload>(older.Count + newer.Count);
        foreach (var state in older)
            if (state is not null && state.Def is { Length: > 0 }) byKey[(state.Ideo, state.Def, state.Index)] = state;
        foreach (var state in newer)
            if (state is not null && state.Def is { Length: > 0 }) byKey[(state.Ideo, state.Def, state.Index)] = state;
        return byKey.Values.OrderBy(state => state.Ideo).ThenBy(state => state.Def, StringComparer.Ordinal)
            .ThenBy(state => state.Index).ToList();
    }
}

internal static class AuthoritativeReplica
{
    private static volatile bool enabled;
    private static AuthoritativeFrame? pending;

    /// <summary>True once the server told this peer to follow rather than simulate.</summary>
    internal static bool Enabled => enabled;

    /// <summary>Opt-in local prediction. Measured on a live room it does not hold up: the two simulations
    /// run independent AI, so roughly a quarter of the pawns exceed the correction radius every frame and
    /// are snapped back, which reads as teleporting. The display path is the shipping default.</summary>
    internal static readonly bool Predictive =
        string.Equals(Environment.GetEnvironmentVariable("TURBOTICK_PREDICTIVE"), "1", StringComparison.Ordinal);

    /// <summary>Local simulation must not advance. Derived from the live phase so no tick can slip in
    /// between the load finishing and the first frame.</summary>
    internal static bool Frozen => enabled && !Predictive && SharedColony.Session.Phase == Simulation.SharedPhase.Playing;

    internal static int LastFrameTick { get; private set; } = -1;
    internal static int AppliedFrames { get; private set; }
    internal static int StatesApplied { get; private set; }
    internal static int StatesFailed { get; private set; }
    internal static int Created { get; private set; }
    internal static int CreateFailures { get; private set; }
    internal static int MissingDefs { get; private set; }

    private static int skippedCommands;
    private static int idAlignments;
    /// <summary>多分配时抓调用栈最上面几帧，用来指认「哪条路径在副本上创建物体」。
    /// 只有计数无法定位来源，而没有来源就只能靠猜。</summary>
    private static string IdAheadTrace()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(true).GetFrames();
            var parts = new List<string>();
            for (var i = 1; i < trace.Length && parts.Count < 4; i++)
            {
                var method = trace[i].GetMethod();
                if (method is null) continue;
                var type = method.DeclaringType?.Name ?? "?";
                if (type is "AuthoritativeReplica") continue;
                parts.Add(type + "." + method.Name);
            }
            return string.Join(" <- ", parts);
        }
        catch (Exception) { return "(栈不可用)"; }
    }
    private static int idAheadEvents;
    private static int expectedAllocations;
    private static int worstIdAhead;
    private static int idCheckFailures;
    private static int weatherChanges;

    /// <summary>Whether a broadcast command may run on this peer.
    ///
    /// A display replica applies broadcast commands, because the frame carries no designations, zones, areas,
    /// research or gizmo state and somebody has to keep those in step. What it must never do is **create or
    /// destroy an object**: object identity belongs to the host, and the frame delivers the very same object
    /// afterwards.
    ///
    /// Measured before this rule existed: dragging one blueprint made the replica place its own copy, which drew
    /// an id from the replica's own counter; the next frame's creation then arrived under the host's id and moved
    /// the local copy aside (`WipeMode.VanishOrMoveAside`). The object looked right, but the two
    /// `UniqueIDsManager` counters were offset from then on, and nothing could notice: a replica never sends a
    /// state hash (`ReportHash` returns early while frozen), so the drift was silent by construction. Off by one
    /// id is enough to make a later `cancel-thing`, `forbid` or job target resolve to a *different* object here
    /// than on the host.</summary>
    internal static bool AcceptsCommand(string kind)
    {
        if (!Frozen || !Simulation.CommandCodec.IsEntityLifecycle(kind)) return true;
        if (skippedCommands++ < 8)
            Log.Message($"[TurboTick] 副本跳过命令 kind={kind}：它会在本端生成/销毁物体，"
                + "而物体只能由权威帧创建。");
        return false;
    }

    internal static void OnWorldStarted(SessionInfo session)
    {
        enabled = session.Authoritative;
        pending = null;
        LastFrameTick = -1;
        AppliedFrames = 0;
        ResetCounters();
        // Zone ids and digests belong to the world being replaced. A stale digest would suppress the first send for a
        // zone that happens to have the same id in the new colony, and the replica would then draw nothing for it.
        ZoneSync.ResetForNewWorld();
        if (enabled) Log.Message("[TurboTick] 权威模式：本端为显示副本，不参与本地模拟。");
    }

    internal static void OnLeft() => enabled = false;

    /// <summary>Called from the network thread; keeps only the newest frame.</summary>
    internal static void Receive(AuthoritativeFrame frame)
    {
        if (!enabled) return;
        if (Interlocked.Increment(ref receivedFrames) == 1)
            Log.Message($"[TurboTick] 收到首个权威帧：tick={frame.Tick}，{frame.Pawns.Count} 个角色。");
        // 只有一个槽位：两帧之间本端循环可能一次都没跑到，后到的那帧会把前一帧整个顶掉。
        // 顶掉 Pawns/Changed 没关系（下一帧就是更新的状态），但 Created/Removed 是**事件**不是状态：
        // 那个 id 只有那一帧说过它存在。实测长跑 tick 84346 只应用了 29700 帧（约三分之二被顶掉），
        // 被顶掉的 Created 之后再也不会重发，于是它名下的每一条变化记录都永远找不到目标 ——
        // 64522 条变化里 59599 条找不到，就是这么来的。
        while (true)
        {
            var previous = Volatile.Read(ref pending);
            var merged = previous is null ? frame : MergeEvents(previous, frame);
            if (ReferenceEquals(Interlocked.CompareExchange(ref pending, merged, previous), previous)) return;
        }
    }

    /// <summary>把被顶掉那帧的创建/删除并进新帧。位置与状态取新的，事件取并集（按 id 去重）。</summary>
    /// <remarks>
    /// <b>Zones belong in this list for exactly the same reason `Created`/`Removed` do.</b> The authority records a
    /// zone's revision when it sends it and skips it forever after, so a zone whose frame is dropped is never sent
    /// again - it simply does not exist on that peer, with no error and no colour. Measured end to end: `script-zone`
    /// put a 24-cell stockpile on the authority (`区域同步 发送=1 本端区域=1`) and the replica's next applied frame read
    /// `应用=0 本端区域=0`, because the frame carrying it had been overwritten by a newer pose-only frame.
    /// </remarks>
    /// <summary>Removals from both frames, deduplicated. Losing one resurrects a world object the authority destroyed,
    /// and the two frames are merged precisely so a frame that was overwritten cannot take an event with it.
    /// </summary>
    private static IReadOnlyList<int>? WorldObjectsRemovedUnion(
        IReadOnlyList<int>? newer, IReadOnlyList<int>? older)
    {
        if (newer is not { Count: > 0 }) return older;
        if (older is not { Count: > 0 }) return newer;
        var merged = new List<int>(newer);
        var seen = new HashSet<int>(newer);
        foreach (var id in older) if (seen.Add(id)) merged.Add(id);
        return merged;
    }

    private static AuthoritativeFrame MergeEvents(AuthoritativeFrame older, AuthoritativeFrame newer)
    {
        var hadEvents = older.Created is { Count: > 0 } || older.Removed is { Count: > 0 }
            || older.Zones is { Count: > 0 } || older.ZonesRemoved is { Count: > 0 }
            || older.Letters is { Count: > 0 } || older.Messages is { Count: > 0 }
            || older.TerrainTop is { Count: > 0 } || older.TerrainUnder is { Count: > 0 }
            || older.TerrainFoundation is { Count: > 0 } || older.Gear is { Count: > 0 }
            || older.Doors is { Count: > 0 } || older.Roofs is { Count: > 0 } || older.Snow is { Count: > 0 } || older.Fog is { Count: > 0 } || older.Mental is { Count: > 0 } || older.Inspirations is { Count: > 0 } || older.Guests is { Count: > 0 } || older.Settlement is not null || older.PlanList is not null || older.Research is { Count: > 0 } || older.RoomTemps is { Count: > 0 } || older.Plants is { Count: > 0 } || older.Factions is { Count: > 0 } || older.WorldObjects is { Count: > 0 } || older.WorldObjectStates is { Count: > 0 } || older.Quests is { Count: > 0 } || older.Memories is { Count: > 0 } || older.Tales is { Count: > 0 }
            || older.ToilProgress is { Count: > 0 } || older.AbilityCharges is { Count: > 0 } || older.PlaySettings is { Count: > 0 } || older.Adaptation is { Count: > 0 } || older.GameEnder is { Count: > 0 } || older.PassingShips is { Count: > 0 } || older.ResidualCounters is { Count: > 0 } || older.Ideos is { Count: > 0 } || older.Discoveries is { Count: > 0 } || older.Areas is { Count: > 0 }
            || older.HediffTimers is { Count: > 0 }
            || older.Conditions is { Count: > 0 } || older.ConditionsRemoved is { Count: > 0 }
            || older.Ages is { Count: > 0 }
            || older.Breaking is { Count: > 0 }
            || older.Immunity is { Count: > 0 }
            || older.Drivers is { Count: > 0 }
            || older.Deathrest is { Count: > 0 }
            || older.Stance is { Count: > 0 }
            || older.Lords is { Count: > 0 }
            || older.QuestParts is { Count: > 0 }
            || older.PawnTrackers is { Count: > 0 };
        if (!hadEvents) return newer;
        List<ReplicaCreation>? created = newer.Created is { Count: > 0 } fresh ? [.. fresh] : null;
        if (older.Created is { Count: > 0 } stale)
        {
            created ??= [];
            var have = new HashSet<int>(created.ConvertAll(state => state.Id));
            foreach (var state in stale)
                if (have.Add(state.Id))
                    created.Add(state);
        }
        List<int>? removed = newer.Removed is { Count: > 0 } freshRemoved ? [.. freshRemoved] : null;
        if (older.Removed is { Count: > 0 } staleRemoved)
        {
            removed ??= [];
            var have = new HashSet<int>(removed);
            foreach (var id in staleRemoved)
                if (have.Add(id))
                    removed.Add(id);
        }
        // 同一批里既创建又删除的以删除为准：那是「建了又立刻销毁」，两件事都发生过，后者是终态。
        if (created is not null && removed is not null)
            created.RemoveAll(state => removed.Contains(state.Id));
        List<ReplicaZone>? zones = newer.Zones is { Count: > 0 } freshZones ? [.. freshZones] : null;
        if (older.Zones is { Count: > 0 } staleZones)
        {
            zones ??= [];
            // Newest wins per id: a zone edited between two frames must not be rolled back to the older cell set.
            var have = new HashSet<int>(zones.ConvertAll(state => state.Id));
            foreach (var state in staleZones)
                if (have.Add(state.Id))
                    zones.Add(state);
        }
        List<ReplicaZoneRemoval>? zonesRemoved = newer.ZonesRemoved is { Count: > 0 } freshZoneRemovals
            ? [.. freshZoneRemovals] : null;
        if (older.ZonesRemoved is { Count: > 0 } staleZoneRemovals)
        {
            zonesRemoved ??= [];
            var have = new HashSet<int>(zonesRemoved.ConvertAll(state => state.Id));
            foreach (var state in staleZoneRemovals)
                if (have.Add(state.Id))
                    zonesRemoved.Add(state);
        }
        if (zones is not null && zonesRemoved is not null)
        {
            // Deleting is the final state, same rule as objects: a zone that was created and deleted inside the merge
            // window must end up gone, not resurrected.
            var gone = new HashSet<int>(zonesRemoved.ConvertAll(state => state.Id));
            zones.RemoveAll(state => gone.Contains(state.Id));
        }
        mergedEventFrames++;
        // Letters and messages ride the same rule as `Created`/`Zones`: the authority marks a letter as sent the first
        // time it appears, so a frame that is overwritten before it is applied takes that letter with it and it is
        // never sent again. A dropped raid letter is not a cosmetic loss - the player never learns the raid happened.
        var letters = EventUnion.ById(newer.Letters, older.Letters, state => state.Id);
        var messages = EventUnion.ByKey(newer.Messages, older.Messages, state => state.Text);
        // Terrain is keyed by cell *and layer*: the same cell appears once per layer and the three are applied to
        // different arrays, so keying by cell alone would let an under-layer value overwrite a top-layer one.
        var terrainTop = EventUnion.ById(newer.TerrainTop, older.TerrainTop, state => state.Index);
        var terrainUnder = EventUnion.ById(newer.TerrainUnder, older.TerrainUnder, state => state.Index);
        var terrainFoundation = EventUnion.ById(newer.TerrainFoundation, older.TerrainFoundation, state => state.Index);
        // Gear is keyed by pawn: a pawn has one wardrobe, and the newer encoding supersedes the older one entirely.
        var gear = EventUnion.ById(newer.Gear, older.Gear, state => state.Pawn);
        // Doors are keyed by door id; the newer state supersedes the older one for the same door.
        var doors = EventUnion.ById(newer.Doors, older.Doors, state => state.Id);
        // Roofs are keyed by cell. No layer dimension: `RoofGrid` holds one def per cell.
        var roofs = EventUnion.ById(newer.Roofs, older.Roofs, state => state.Index);
        // Snow is keyed by cell, newest wins: depth is a value that only ever moves toward its latest reading.
        var snow = EventUnion.ById(newer.Snow, older.Snow, state => state.Index);
        // Fog is keyed by cell: a cell is fogged or it is not, and the newer statement wins.
        var fog = EventUnion.ById(newer.Fog, older.Fog, state => state.Index);
        // Research is keyed by project def name.
        var research = EventUnion.ByKey(newer.Research, older.Research, state => state.Project);
        // Room temperature is keyed by the cell that stands in for the room.
        var roomTemps = EventUnion.ByKey(newer.RoomTemps, older.RoomTemps,
            state => state.Map + ":" + state.X + ":" + state.Z);
        var plants = EventUnion.ByKey(newer.Plants, older.Plants, state => state.Thing.ToString());
        var factions = EventUnion.ByKey(newer.Factions, older.Factions,
            state => state.Faction + "->" + state.Other);
        var worldObjects = EventUnion.ByKey(newer.WorldObjects, older.WorldObjects, state => state.Id.ToString());
        // A world object's state supersedes per object for the same reason its tile does: there is one of it, and the newer
        // statement is the truth. Kept separate from the tile union because the two are independent - a caravan can move
        // without any of its scribed fields changing, and a settlement's raid countdown ticks while it sits still.
        var worldObjectStates = EventUnion.ByKey(newer.WorldObjectStates, older.WorldObjectStates,
            state => state.Id.ToString());
        var abilityCharges = EventUnion.ById(newer.AbilityCharges, older.AbilityCharges, state => state.Pawn);
        // Adaptation is one row for the whole save, so the newer statement simply supersedes - there is nothing to key on
        // and nothing to merge.
        var adaptation = newer.Adaptation is { Count: > 0 } ? newer.Adaptation : older.Adaptation;
        // The game-end countdown is one row for the save; the newer statement supersedes.
        var gameEnder = newer.GameEnder is { Count: > 0 } ? newer.GameEnder : older.GameEnder;
        var passingShips = newer.PassingShips is { Count: > 0 } ? newer.PassingShips : older.PassingShips;
        var residualCounters = newer.ResidualCounters is { Count: > 0 } ? newer.ResidualCounters : older.ResidualCounters;
        var ideos = newer.Ideos is { Count: > 0 } ? newer.Ideos : older.Ideos;
        // Whole-state tables, newest wins; there is nothing to union.
        var discoveries = newer.Discoveries is { Count: > 0 } ? newer.Discoveries : older.Discoveries;
    var playSettings = newer.PlaySettings is { Count: > 0 } ? newer.PlaySettings : older.PlaySettings;
    // A single value with no id: newer wins, and a null on the newer side keeps the older one (a channel that stops
    // publishing must not erase the stamp and re-open the cooldown).
    var bossgroup = newer.Bossgroup ?? older.Bossgroup;
    var areas = newer.Areas is { Count: > 0 } ? newer.Areas : older.Areas;
    // The allowed-area list is whole-state: a removal is only expressible by the newer list being shorter, so a frame
    // that carries it replaces the older one outright rather than unioning.
    var allowedAreas = newer.AllowedAreas is { Count: > 0 } ? newer.AllowedAreas : older.AllowedAreas;
    var traits = newer.Traits is { Count: > 0 } ? newer.Traits : older.Traits;
    // A pawn has one title list and one permit list, and the channel sends the whole thing per pawn, so the newer
    // statement supersedes. Same shape as traits.
    var royalty = newer.Royalty is { Count: > 0 } ? newer.Royalty : older.Royalty;
    var genes = newer.Genes is { Count: > 0 } ? newer.Genes : older.Genes;
    var precepts = newer.Precepts is { Count: > 0 } ? newer.Precepts : older.Precepts;
    // Precept names are keyed by (ideo, def) and sparse: only rows whose name or lock moved are on the wire, so the
    // older frame's rows for other precepts must survive. Merged on the exact pair, newer wins - the same rule the
    // auto-slaughter rows use, and for the same reason: there is no single int to key on.
    var preceptNames = PreceptNameMerge.Merge(newer.PreceptNames, older.PreceptNames);
    // Payload rows are sparse too, and keyed on the precept instance: see `PreceptPayloadMerge`.
    var preceptPayloads = PreceptPayloadMerge.Merge(newer.PreceptPayloads, older.PreceptPayloads);
    // One record per game: the newer statement supersedes the older, which is why this is not a list union.
    var anomalyDifficulty = newer.AnomalyDifficulty ?? older.AnomalyDifficulty;
    // A statement, not an event: there is one corpse per game and its state is "what it is now".
    var corpseTracker = newer.CorpseTracker ?? older.CorpseTracker;
    // A living effect is an event list, not state: a row that is present in either frame is present now.
    var livingEffects = EventUnion.ById(newer.LivingEffects, older.LivingEffects, row => row.Id);
    var livingEffectsRemoved = EventUnion.ById(newer.LivingEffectsRemoved, older.LivingEffectsRemoved, id => id);
    // The policy tables are sent whole and rarely: a frame either carries the entire table or nothing. `newer` wins
    // because a table is a statement about the present, not a sparse edit - taking the older one when the newer is
    // empty would resurrect a policy the authority just deleted.
    var policies = newer.Policies is { Count: > 0 } ? newer.Policies : older.Policies;
    // Faction creations are an event list, not a statement: a faction exists from its creation onward, so the older
    // frame's rows for other factions must survive the merge (`EventUnion.ById`, the same rule the world-object
    // creations use).
    var factionCreations = EventUnion.ById(newer.FactionCreations, older.FactionCreations, state => state.LoadID);
    // Flight is keyed by pawn like the other per-pawn one-value channels: a pawn is in one flight state, and the newer
    // statement supersedes the older. Whole-state would be wrong here - the list only carries pawns that differ from
    // "grounded with no cooldown", so the older frame's entries for other pawns must survive.
    var flights = EventUnion.ById(newer.Flights, older.Flights, state => state.Pawn);
    // Faction belief is one value per faction, so the newer statement supersedes the older - same shape as flight.
    // Whole-list would be wrong: the channel only carries factions whose belief changed, so the older frame's rows for
    // every other faction have to survive the merge.
    var factionIdeos = EventUnion.ById(newer.FactionIdeos, older.FactionIdeos, state => state.Faction);
    // A map is one value per id: its size never changes, and only the tombstone supersedes a creation.
    var maps = EventUnion.ById(newer.Maps, older.Maps, state => state.Id);
    // A faction has one set of three stamps, so the newer statement supersedes the older - the rows only carry factions
    // whose stamps changed, so the older rows for every other faction must survive the merge.
    var factionCooldowns = EventUnion.ById(newer.FactionCooldowns, older.FactionCooldowns, state => state.Faction);
    // Keyed by (map, species) as an exact tuple, not by a hash: the same species has an independent limit on every map,
    // so a map-only or def-only key would let one map's row overwrite another's - and `EventUnion.ById` takes an int, so
    // a packed hash would be a collision risk in the one place where a collision silently drops a player's setting.
    var autoSlaughter = MergeByMapAndSpecies(newer.AutoSlaughterConfigs, older.AutoSlaughterConfigs);
    // A history row is one def's window. Keyed by `Def|Faction` because the same def appears once in the colonist table
    // (Faction -1) and once per affected faction - keying on either half alone would let the two overwrite each other.
    var historyEvents = EventUnion.ByKey(newer.HistoryEvents, older.HistoryEvents,
        state => state.Def + "|" + state.Faction.ToString(CultureInfo.InvariantCulture));
        var quests = EventUnion.ByKey(newer.Quests, older.Quests, state => state.Id.ToString());
        var memories = EventUnion.ByKey(newer.Memories, older.Memories, state => state.Pawn + "|" + state.Key);
        var tales = EventUnion.ByKey(newer.Tales, older.Tales, state => state.Id.ToString());
        // Mental state is keyed by pawn: a pawn has one mental state, and the newer statement supersedes the older.
        var mental = EventUnion.ById(newer.Mental, older.Mental, state => state.Pawn);
        // Inspiration is keyed by pawn: a pawn has one inspiration and the newer statement supersedes the older.
        var inspirations = EventUnion.ById(newer.Inspirations, older.Inspirations, state => state.Pawn);
        // Guest status is keyed by pawn: a pawn has one status and the newer statement supersedes the older.
        var guests = EventUnion.ById(newer.Guests, older.Guests, state => state.Pawn);
        // Per-pawn, newer wins, but still carried across an overwritten frame: a pawn whose last entry said "stopped
        // working" would otherwise keep a frozen bar forever, because the authority only re-sends a value when it changes.
        var toilProgress = EventUnion.ById(newer.ToilProgress, older.ToilProgress, state => state.Pawn);
        var drivers = EventUnion.ById(newer.Drivers, older.Drivers, state => state.Pawn);
        var deathrest = EventUnion.ById(newer.Deathrest, older.Deathrest, state => state.Pawn);
        var stance = EventUnion.ById(newer.Stance, older.Stance, state => state.Pawn);
        var lords = EventUnion.ById(newer.Lords, older.Lords, state => state.Lord);
        var questParts = EventUnion.ById(newer.QuestParts, older.QuestParts, state => state.Quest);
        var pawnTrackers = EventUnion.ById(newer.PawnTrackers, older.PawnTrackers, state => state.Pawn);
        // Per-pawn, newest wins. It has to be merged rather than dropped for the same reason `Hediffs` is: the hediff
        // entry and its timers are only meaningful together, and a frame that carried one without the other would rebuild a
        // condition with a reset countdown.
        var hediffTimers = EventUnion.ById(newer.HediffTimers, older.HediffTimers, state => state.Pawn);
        // Weather conditions are identified by the game\'s own condition id, which is scribed and allocated by
        // `UniqueIDsManager`, so both peers agree on it. Newest statement per id wins.
        var conditions = EventUnion.ById(newer.Conditions, older.Conditions, state => state.Id);
        // A removal is an event, not state: it has to survive being overwritten exactly as `Created` does, or a
        // condition the authority ended would keep standing on this peer with no error anywhere.
        var conditionsRemoved = EventUnion.ById(newer.ConditionsRemoved, older.ConditionsRemoved, state => state.Id);
        var ages = EventUnion.ById(newer.Ages, older.Ages, state => state.Pawn);
        var breaking = EventUnion.ById(newer.Breaking, older.Breaking, state => state.Pawn);
        // Per-pawn and whole-state: the string lists every record the authority has, so the newer one supersedes.
        var immunity = EventUnion.ById(newer.Immunity, older.Immunity, state => state.Pawn);
        return newer with { Created = created, Removed = removed, Zones = zones, ZonesRemoved = zonesRemoved,
            Letters = letters, Messages = messages,
            TerrainTop = terrainTop, TerrainUnder = terrainUnder, TerrainFoundation = terrainFoundation,
            Gear = gear, Doors = doors, Roofs = roofs, Snow = snow, Fog = fog, Mental = mental, Inspirations = inspirations, Guests = guests,
            // The table is whole-state, so a newer frame's copy simply supersedes the older one - there is nothing to
            // union. A dropped frame that carried a newer table loses to nothing: the authority's counter keeps moving, so
            // the next frame re-sends it.
            Settlement = newer.Settlement ?? older.Settlement,
            // Plans are whole-state keyed by version, so the newer copy simply supersedes the older one.
            PlanList = newer.PlanList ?? older.PlanList,
            // Research progress is keyed by project and only ever grows, so the newer statement supersedes the older.
            Research = research,
            // Room temperature is per room and only ever moves toward the outdoors, so the newer reading supersedes.
            // Merged by cell, not by room: the cell is the only identity that survives a frame.
            RoomTemps = roomTemps,
            // Plant stage is one byte per plant and monotone within a round, so the newer statement supersedes per plant.
            Plants = plants,
            // Directed per faction pair, newest wins.
            Factions = factions,
            // A world object has one tile, so the newer statement supersedes per object.
            WorldObjects = worldObjects,
            WorldObjectStates = worldObjectStates,
            // Creations are taken whole from the newer frame and never merged by id: a creation is sent once, and
            // merging would drop the one the older frame carried - which is exactly the object that exists on the
            // authority and nowhere else. Removals are unioned, because a removal is an event and losing one
            // resurrects an object the authority destroyed.
            WorldObjectCreations = newer.WorldObjectCreations ?? older.WorldObjectCreations,
            WorldObjectsRemoved = WorldObjectsRemovedUnion(newer.WorldObjectsRemoved, older.WorldObjectsRemoved),
            // A quest has one standing, newest wins.
            Quests = quests,
            // A memory has one age, newest wins.
            Memories = memories,
            // One standing per tale, newest wins.
            Tales = tales , ToilProgress = toilProgress, Drivers = drivers, Deathrest = deathrest, Stance = stance, Lords = lords, QuestParts = questParts, PawnTrackers = pawnTrackers, HediffTimers = hediffTimers, Conditions = conditions, ConditionsRemoved = conditionsRemoved, Ages = ages, Breaking = breaking, Immunity = immunity, AbilityCharges = abilityCharges, Adaptation = adaptation, GameEnder = gameEnder, PassingShips = passingShips,
            ResidualCounters = residualCounters,
            Ideos = ideos, Discoveries = discoveries, PlaySettings = playSettings, Areas = areas, AllowedAreas = allowedAreas, Traits = traits, Royalty = royalty, Genes = genes, Precepts = precepts, Flights = flights, FactionIdeos = factionIdeos, Maps = maps, FactionCooldowns = factionCooldowns, AutoSlaughterConfigs = autoSlaughter, Bossgroup = bossgroup, PreceptNames = preceptNames, PreceptPayloads = preceptPayloads,
            AnomalyDifficulty = anomalyDifficulty, Policies = policies, FactionCreations = factionCreations,
            CorpseTracker = corpseTracker, LivingEffects = livingEffects, LivingEffectsRemoved = livingEffectsRemoved,
        HistoryEvents = historyEvents};
    }

    private static int mergedEventFrames;

    private static int receivedFrames;

    /// <summary>Game thread: apply the newest frame and follow the host clock.</summary>
    internal static void Update()
    {
        // Counted before every early return: the number that matters is how often this peer's own loop ran,
        // whether or not a frame was waiting for it.
        pumpCount++;
        // A predictive peer still consumes frames, but as a correction source rather than a state feed.
        if (!Frozen && !(Predictive && enabled)) return;
        var frame = Interlocked.Exchange(ref pending, null);
        if (frame is null) return;
        if (frame.Tick <= LastFrameTick) return;
        // 帧间隔只说明「两帧之间隔了多久」，不说明这段间隔里本端有没有在跑。这两种情况修法相反：
        // 本端被 CPU 饿住（三个副本加一个权威挤在同一台机器上就会这样）是压测环境的问题，
        // 帧真的晚了才是网络/发送节奏的问题。上一次 96 角色 + 3 副本读到 `帧间隔 最差 0.75s`，
        // 而同一场景 1 副本是 0.00s —— 那个对比指向本端，但一个数字分不清「没跑」和「跑了没帧」。
        // 这里数一下间隔内本端循环被调用了多少次：60 fps 下 0.43 秒应该有 26 次，
        // 只有个位数就是被饿住了。
        var arrival = Clock.Now;
        if (lastFrameArrival > 0f)
        {
            var measured = arrival - lastFrameArrival;
            if (measured > 0.001f && measured < 0.5f) frameInterval = frameInterval * 0.8f + measured * 0.2f;
            // A gap far longer than the smoothing window is a visible hitch: the timeline stopped.
            if (measured > 0.25f)
            {
                hitchCount++;
                worstGap = Math.Max(worstGap, measured);
                var pumps = pumpCount - pumpsAtLastFrame;
                if (pumps < worstHitchPumps || worstHitchPumps < 0) worstHitchPumps = pumps;
                if (pumps > mostHitchPumps) mostHitchPumps = pumps;
            }
        }
        pumpsAtLastFrame = pumpCount;
        lastFrameArrival = arrival;
        LastFrameTick = frame.Tick;
        AppliedFrames++;
        // Everything below reproduces state the host already agreed on. Without this scope the writes go through
        // vanilla setters, which our own capture patches treat as player input: the assignment is cancelled and
        // turned into a command, so the frame never lands and the peer mails the host back the value it was just
        // told. Measured before this line existed: 1459 commands in one run (791 of them forbid-batch, almost all
        // value=True), 翻转 0 次 on the replica, and its forbidden count climbing to 1296 while the host's items
        // were being re-forbidden by its own replica.
        using var stateWrite = SharedActions.StateWrite();
        // Fresh cross-reference index for this frame. `ScribeBridge.Load` resolves a fragment's references against an
        // index of the live world, and the live world moves inside this very frame: `Created` below builds objects and
        // a pawn can pick something up. Measured before the fix: only the fragment's own exposables were registered, so
        // `Faction_0` was absent, `Scribe_References.Look` wrote null, and vanilla's `Settlement.ExposeData` destroyed
        // the settlement with "had null faction on load". Rebuilding is lazy - a frame that loads no fragment pays
        // nothing beyond clearing three fields.
        ScribeBridge.InvalidateReferenceIndex();
        // A predictive peer must publish its OWN clock, not the host's. Observing the host tick told the
        // barrier the peer was caught up while the local simulation sat 2400 ticks behind, so no catch-up
        // ever ran and every position comparison was between two different moments in time.
        SharedColony.Session.Barrier.Observe(Predictive
            ? Find.TickManager?.TicksGame ?? frame.Tick
            : frame.Tick);
        var pawnStart = Clock.Now;
        foreach (var state in frame.Pawns) ApplyPawn(state);
        // Conditions after the pawns exist: `ApplyHediffs` needs `pawn.health`, and a pawn created by a snapshot
        // arriving in the same frame has it only once `ApplyPawn` has run.
        if (frame.Hediffs is { Count: > 0 } hediffSets)
            foreach (var set in hediffSets) ApplyHediffs(set);
        // Timers after the rebuild, never before: `ApplyHediffs` removes every condition and makes new ones, and
        // `CompPostPostAdd` is where a timed comp sets its counter to the full duration. Writing the host's value first
        // would be overwritten by that same rebuild - the counters have to be the last word on the condition.
        // `ageTicks` is one of them: `MakeHediff` starts it at zero, and `Hediff_Injury.BleedRate` reads it to
        // decide whether the wound still bleeds.
        if (frame.HediffTimers is { Count: > 0 } hediffTimers)
            foreach (var timers in hediffTimers) HediffTimerSync.Apply(timers);
        // Ability charges, after the hediff rebuild for the same reason: a hediff can grant an ability, and
        // `Hediff_Ability` runs `GrantAbility` inside the rebuild - so the ability list this writes into does not exist
        // until after `ApplyHediffs` has made it.
        if (frame.AbilityCharges is { Count: > 0 } abilityCharges)
            foreach (var charges in abilityCharges) AbilitySync.Apply(charges);
        // Storyteller adaptation. Nothing to order it against - the counters are read by the raid calculation, which is a
        // question asked later, not a value other channels overwrite.
        if (frame.Adaptation is { Count: > 0 } adaptationRows)
            foreach (var adaptationRow in adaptationRows) AdaptationSync.Apply(adaptationRow);
        // The colony's end state. Applied after everything else: it is a statement about the whole colony, and a frame
        // that also emptied the map should land the countdown on top of an empty colony rather than before it.
        if (frame.GameEnder is { Count: > 0 } gameEnderRows)
            foreach (var gameEnderRow in gameEnderRows) GameEnderSync.Apply(gameEnderRow);
        // Ships last: a frame that also removed pawns may be the frame in which the ship's countdown ended.
        if (frame.PassingShips is { Count: > 0 } passingShipRows)
            foreach (var passingShipRow in passingShipRows) PassingShipSync.Apply(passingShipRow);
        // Leftover counters last: several of them write a field on an object another channel may have just rebuilt, so
        // applying them after everything else means the write lands on the object that exists.
        if (frame.ResidualCounters is { Count: > 0 } residualRows)
            foreach (var residualRow in residualRows) ResidualCounterSync.Apply(residualRow);
        // Beliefs after everything else: the ideoligion table has to exist before a pawn entry can resolve its id, and
        // the table is sent first inside the same list, but a frame that carries only a pawn entry still needs whatever
        // table a previous frame established.
        if (frame.Ideos is { Count: > 0 } ideoRows)
            foreach (var ideoRow in ideoRows) IdeoSync.Apply(ideoRow);
        // Discovery last for the same reason: these two databases decide what the player is allowed to see, so they
        // must be in place before the next frame draws anything. They replace whole tables rather than merging, and
        // nothing else in a frame writes them.
        if (frame.Discoveries is { Count: > 0 } discoveryRows)
            foreach (var discoveryRow in discoveryRows) DiscoverySync.Apply(discoveryRow);
        // Weather conditions. Created first, then removals - a condition that ended and another that started in the same
        // gap must both land, and the removal ids are the ended one's, so order between them is irrelevant; doing creates
        // first simply means the ids exist before anything refers to them.
        if (frame.Conditions is { Count: > 0 } conditions)
            foreach (var condition in conditions) GameConditionSync.Apply(condition);
        if (frame.ConditionsRemoved is { Count: > 0 } conditionsGone)
            foreach (var gone in conditionsGone) GameConditionSync.Remove(gone.Map, gone.Id);
        // Ages. The counter is advanced only inside the pawn's own tick, so every pawn here keeps the age the save was
        // loaded with. Applied through the property, which also recomputes the life stage and the capacity curves.
        if (frame.Ages is { Count: > 0 } ages)
            foreach (var age in ages) PawnAgeSync.Apply(age);
        // Misery counters. The integral of mood over time, which nothing here advances - and what the "about to break"
        // flags and the flashing head glow are computed from.
        if (frame.Breaking is { Count: > 0 } breakingStates)
            foreach (var breakingState in breakingStates) MentalBreakerSync.Apply(breakingState);
        // Immunity. Applied after the hediffs above, because a disease that arrived in this same frame has to exist before its
        // immunity record can be attached to it.
        if (frame.Immunity is { Count: > 0 } immunityStates)
            foreach (var immunityState in immunityStates) ImmunitySync.Apply(immunityState);
        ReconcilePawns(frame);
        var pawnSeconds = Clock.Now - pawnStart;
        pawnApplyTotal += pawnSeconds;
        if (pawnSeconds > worstPawnApply)
        {
            worstPawnApply = pawnSeconds;
            worstPawnApplyCount = frame.Pawns.Count;
        }
        // One clock read for the whole frame: `Clock.Now` is a Stopwatch call, and it was being taken once per
        // object inside the budget checks below.
        var budgetEnd = Clock.Now + ObjectBudgetSeconds;
        var budgetSpent = false;
        // The id index is per frame; anything the world changed since the last one is re-read on demand.
        frameIndex = null;
        // 预算只用来丢「可以重来」的工作。创建是唯一不可重来的通道：一个 id 在权威端存在之后，
        // 增量帧只在它「变了」的时候再发变化记录，而一个从没建出来的物体，每一条变化记录都找不到目标 ——
        // 实测长跑 64522 条变化里 59599 条找不到、同时 `超预算丢弃 552`，两者是同一件事：
        // 帧里的 Created 被预算 break 掉，之后 552 个 id 的每一条变化都成了空转。
        // 所以 Created 与 Removed 先无条件应用（都是 O(名单长度) 的一次调用），预算留给 Changed ——
        // Changed 丢得掉，因为轮转补扫会再发一遍。
        if (frame.Created is { Count: > 0 } created)
        {
            foreach (var state in created) CreateThing(state);
            frameCreatedOrRemoved = true;
        }
        if (frame.Removed is { Count: > 0 } removed)
        {
            foreach (var id in removed) RemoveThing(id);
            frameCreatedOrRemoved = true;
        }
        if (frame.Changed is { Count: > 0 } changed)
            // 累计而不是单帧：轮转补扫一次只发 24 个，单帧看到 0 是正常的，只有累计持续增长才说明它在工作。
            changedThingsTotal += changed.Count;
        if (frame.Changed is { Count: > 0 } changed2)
            foreach (var state in changed2)
            {
                if (budgetSpent) break;
                if (Clock.Now > budgetEnd) { DroppedWork++; budgetSpent = true; break; }
                ApplyThing(state);
            }
        // Weather selection happens on the host's tick, which a replica never runs, and no other field carries it.
        if (frame.Weather is { Count: > 0 } weather)
            foreach (var state in weather) ApplyWeather(state);
        // Zones are their own channel: a zone has no `thingIDNumber`, is not in any map's thing list, and the frame's
        // object scan therefore cannot carry one. Skipping this is why a stockpile the player drew after the snapshot
        // had a correct info card and no colour on the ground - `SectionLayer_Zones` draws only cells where
        // `zoneManager.ZoneAt` returns a zone, so "no colour" means "no object here".
        if (frame.Zones is { Count: > 0 } zones)
            foreach (var state in zones) ZoneSync.Apply(state);
        if (frame.ZonesRemoved is { Count: > 0 } zonesRemoved)
            foreach (var state in zonesRemoved) ZoneSync.Remove(state);
        // Letters and messages: the only path by which a replica learns that anything happened at all. A replica runs
        // no tick, so `LetterStack.ReceiveLetter` is never called locally for a raid, a disease or a quest.
        if (frame.Letters is { Count: > 0 } letters)
            foreach (var state in letters) LetterSync.Apply(state);
        if (frame.Messages is { Count: > 0 } messages)
            foreach (var state in messages) LetterSync.Apply(state);
        // Terrain. A cell whose ground the host changed - a mined wall, a built floor, a collapse. Not a thing, so the
        // thing diff can never see it, and a replica that misses it draws the wrong ground for the rest of the session
        // while everything else about the cell looks correct.
        if (frame.TerrainTop is { Count: > 0 } terrainTop)
            foreach (var state in terrainTop) TerrainSync.Apply(state);
        if (frame.TerrainUnder is { Count: > 0 } terrainUnder)
            foreach (var state in terrainUnder) TerrainSync.Apply(state);
        if (frame.TerrainFoundation is { Count: > 0 } terrainFoundation)
            foreach (var state in terrainFoundation) TerrainSync.Apply(state);
        // Equipment and apparel. Separate from `Pawns` because they live in the pawn's own trackers rather than in the
        // map's thing list, and change-only because a wardrobe changes a few times an hour while `Pawns` is every frame.
        if (frame.Gear is { Count: > 0 } gear)
            foreach (var state in gear) ApparelSync.Apply(state);
        // Doors. `openInt` is driven by the door's own Tick, which a replica never runs, so without this every door stays
        // at whatever state the save was loaded with.
        // Whether the whole game is paused for a gravship landing confirmation. `TickManager.ForcePaused` (`:52680`)
        // and `PlayerCanControl` (`:52696`) read it locally on every peer, so a replica that does not receive it keeps
        // ticking while the authority has stopped, and one that loaded mid-confirmation stays frozen forever.
        if (!string.IsNullOrEmpty(frame.GravshipGate)) GravshipGateSync.Apply(frame.GravshipGate);
        if (frame.Doors is { Count: > 0 } doors)
            foreach (var state in doors) DoorSync.Apply(state);
        // Roofs. A roof is not a Thing either - `RoofGrid` is one flat array - so a player-built roof never reaches a
        // replica without this, and `Roofed()` feeding room stats means the effect is not merely visual.
        if (frame.Roofs is { Count: > 0 } roofs)
            foreach (var state in roofs) RoofSync.Apply(state);
        // Snow. Neither a Thing nor terrain: `SnowGrid.depthGrid` is one float per cell, and weather fills it on a tick a
        // replica never runs, so the ground keeps its load-time depth forever.
        if (frame.Snow is { Count: > 0 } snow)
            foreach (var state in snow) SnowSync.Apply(state);
        // Fog of war. Last of the map grids because it hides the others: a cell still fogged shows none of the terrain,
        // roof or snow work, so lifting fog is what makes the rest of the frame visible.
        if (frame.Fog is { Count: > 0 } fog)
            foreach (var state in fog) FogSync.Apply(state);
        // Mental breaks. Started by a mood check inside a tick interval a replica never runs, so a colonist who breaks
        // down on the host keeps standing calmly on the replica while the authority's copy wanders in a daze.
        if (frame.Mental is { Count: > 0 } mental)
            foreach (var state in mental) MentalStateSync.Apply(state);
        // Inspirations. Rolled inside a 100-tick interval a replica never runs, so an inspired colonist shows nothing on
        // the replica's needs card while the host shows the icon.
        if (frame.Inspirations is { Count: > 0 } inspirations)
            foreach (var state in inspirations) InspirationSync.Apply(state);
        // Guest / prisoner / slave status. Set by gameplay on ticks a replica does not have, so a captured raider would
        // look like an ordinary colonist to every query the UI makes.
        if (frame.Guests is { Count: > 0 } guests)
            foreach (var state in guests) GuestSync.Apply(state);
        // The settlement table. Replaced wholesale when the authority's version counter moves: a replica whose grants
        // differ from the authority's would disagree about whether an action is legal, which is the one outcome that must
        // not happen.
        if (SettlementSync.NeedsApply(frame.Settlement)) SettlementSync.Apply(frame.Settlement!);
        // Planning marks. Not Things - they live in `PlanManager.allPlans`, so a replica would show none of the plans the
        // other players drew, and two players would dig the same corridor from opposite ends.
        if (frame.PlanList is { Plans: { Count: > 0 } plans } list && list.Version != lastPlanVersion)
        {
            lastPlanVersion = list.Version;
            PlanSync.Apply(Find.CurrentMap, plans);
        }
        // Research progress. Per project because the tab shows a bar for every started project; sending only the current one
        // would make a player switching back see the bar jump backwards.
        if (frame.Research is { Count: > 0 } research)
            foreach (var state in research) ResearchSync.Apply(state);
        // The three simulation-input `PlaySettings` fields. This apply had no call site until now: the channel published
        // and the frame carried the row, but nothing on the receiving side read it, so `游戏设置 无变更` was the only
        // possible reading - not "nothing changed" but "nothing was ever applied". A channel whose payload is merged
        // and never consumed looks identical to an idle one from every log line it produces.
        if (frame.PlaySettings is { Count: > 0 } playSettings)
            foreach (var state in playSettings) PlaySettingsSync.Apply(state);

        // The bossgroup cooldown. Applied before the gizmo is drawn, which is the only thing that reads it.
        if (frame.Bossgroup is { } bossgroup) BossgroupSync.Apply(bossgroup);
        if (frame.AnomalyDifficulty is { } anomalyDifficulty) DifficultySync.Apply(anomalyDifficulty);
        if (frame.CorpseTracker is { } corpseTracker) CorpseTrackerSync.Apply(corpseTracker);
        // Visual-only Ethereal carriers. Applied after the thing rows so a strike spawned in the same frame cannot be
        // overwritten by a stale row, and removals after creations so an id in both lists ends up existing.
        if (frame.LivingEffects is { Count: > 0 } livingEffects)
            foreach (var row in livingEffects) EtherealVisualSync.Apply(row);
        if (frame.LivingEffectsRemoved is { Count: > 0 } livingEffectsRemoved)
            foreach (var id in livingEffectsRemoved) EtherealVisualSync.ApplyRemoval(id);
        // The four policy tables. Applied before any pawn's `PolicySnapshot` fragment is loaded, because that fragment
        // names a policy by reference - a policy that does not exist yet resolves to nothing and the tracker falls
        // back to its default, which is exactly the silent divergence this channel exists to remove.
        if (frame.Policies is { Count: > 0 } policies) PolicyTableSync.Apply(policies);
        // Faction creations. Applied before pawns, because a pawn's `FactionLoadId` resolves against the factions that
        // exist - a pawn whose faction is missing is counted as unfiled and drawn as unaffiliated for the rest of the
        // session. `Apply` goes through `FactionManager.Add`, which rebuilds the eleven cached `OfX` pointers.
        if (frame.FactionCreations is { Count: > 0 } factionCreations) FactionCreationSync.Apply(factionCreations);
        // Area grids. After the zone pass (nothing in common) and after settlement areas are rebuilt, because those
        // add and remove `Area_Allowed` entries on the same manager and this one only writes cells inside the built-in
        // areas it resolves by kind.
        if (frame.Areas is { Count: > 0 } areas)
            foreach (var state in areas) AreaSync.Apply(state);
        // Player-created allowed areas: the list is authoritative and arrives whole, so this reconciles by label -
        // create, delete, diff. Nothing else reaches these objects: `Designator_AreaAllowedExpand`/`Clear` are
        // `Designator_Cells` subclasses and `DesignatorSync.EncodeChunks` only encodes `Designator_Build` and
        // `Designator_ZoneAdd`, so a paint stroke publishes no command at all.
        if (frame.AllowedAreas is not null) AreaAllowedSync.Apply(frame.AllowedAreas);
        if (frame.Traits is not null) foreach (var traitState in frame.Traits) PawnTraitSync.Apply(traitState);
        // Royalty after traits: a title change and a trait change never depend on each other, but both write into the
        // same pawn and this keeps the reading order the same as the publishing order.
        if (frame.Royalty is not null) foreach (var royaltyState in frame.Royalty) RoyaltySync.Apply(royaltyState);
        // Genes after traits: `AddGene` adds the gene's forced traits through `GainTrait`, so a gene applied first would
        // have its forced trait overwritten by the trait pass that follows.
        if (frame.Genes is not null) foreach (var geneState in frame.Genes) GeneSync.Apply(geneState);
        // Precepts after the ideo table (which `IdeoSync` applied above): a role precept's holder is looked up by
        // `thingIDNumber`, and `AddPrecept` re-sorts the ideoligion's own list.
        if (frame.Precepts is not null) foreach (var preceptState in frame.Precepts) PreceptSync.Apply(preceptState);
        // Payload before names, and both after the table: a row addresses a precept by (ideo, def, ordinal), and the
        // precept has to exist here first. Payload first because `Precept_ThingDef.ThingDef`'s setter runs
        // `Notify_ThingDefSet`, which regenerates what the name is built from - applying the name first would let the
        // payload overwrite the name the player typed.
        if (frame.PreceptPayloads is not null)
            foreach (var payloadState in frame.PreceptPayloads) PreceptSync.ApplyPayload(payloadState);
        if (frame.PreceptNames is not null) foreach (var nameState in frame.PreceptNames) PreceptSync.ApplyName(nameState);
        // Flight state, after the pawn channels: a pawn has to exist here before its tracker can be written, and the
        // tracker itself comes with the pawn.
        if (frame.Flights is not null) foreach (var flight in frame.Flights) FlightSync.Apply(flight);
        // Indoor temperature, keyed by a representative cell because a room has no cross-peer id. Applied after the grids so
        // the rooms exist, and it marks the temperature overlay dirty - that drawer's own rebuild is a tick a replica never
        // reaches, so without it the numbers would update and the map would keep its load-time colours.
        if (frame.RoomTemps is { Count: > 0 } roomTemps)
            foreach (var state in roomTemps) RoomTempSync.Apply(state);
        // Plant growth stages. A crop's "23% grown" and a bush's "harvestable" are both read straight off `growthInt`, which
        // is frozen here - and the object scan skips a plant that has not moved, so nothing else carries it.
        if (frame.Plants is { Count: > 0 } plants)
            foreach (var state in plants) PlantSync.Apply(state);
        // Faction standing. Goodwill drives the faction page and, through `CheckKindThresholds`, the relation kind that
        // caravans, trade and raids all read.
        if (frame.Factions is { Count: > 0 } factions)
            foreach (var state in factions) FactionSync.Apply(state);
        // Which ideoligion each faction believes. Applied before the pawn/ideo channels below, because
        // `RecalculateIdeosBasedOnPlayerPawns` - which those channels trigger through `SetIdeo` - reads the minor list
        // this writes, and would otherwise re-derive a different primary from a stale set.
        if (frame.FactionIdeos is { Count: > 0 } factionIdeos)
            foreach (var state in factionIdeos) FactionSync.ApplyIdeo(state);
        // The three diplomacy cooldown stamps, right after the faction rows they belong to: the dialog reads them to
        // decide whether a request button is available, and a replica that never receives them offers every request
        // forever (`Faction.lastTraderRequestTick` stays at its load value -9999999, `:485260-485264`).
        if (frame.FactionCooldowns is { Count: > 0 } factionCooldowns)
            foreach (var state in factionCooldowns) FactionSync.ApplyCooldown(state);
        if (frame.AutoSlaughterConfigs is { Count: > 0 } autoSlaughter)
            foreach (var state in autoSlaughter) AutoSlaughterSync.Apply(state);
        // The history-event windows. Applied here, next to the faction rows, because the only reader builds the faction
        // dialog's "recent events" block from them (`FactionDialogMaker.GetRecentEvents`, `:644122`) and would
        // otherwise print the list the save was loaded with for the whole session.
        if (frame.HistoryEvents is { Count: > 0 } historyEvents)
            foreach (var state in historyEvents) HistorySync.Apply(state);
        // Maps before everything else that names one. A frame can carry terrain, roofs, zones and objects for a map this
        // peer has never heard of; every one of those paths resolves the id and drops the record when it fails, so the
        // map has to exist before they run or the whole frame for that map is silently discarded.
        if (frame.Maps is { Count: > 0 } maps)
            foreach (var state in maps) MapLifecycleSync.Apply(state);
        // Where world objects sit. Written through the `Tile` property so the setter dirties both tiles' fast finders and
        // notifies the static drawer - a field write would leave the object in the right place with the wrong picture.
        if (frame.WorldObjects is { Count: > 0 } worldObjects)
            foreach (var state in worldObjects) WorldObjectSync.Apply(state);
            // Creations before the position rows above would be wasted work; after them is right, because a creation's
            // own fragment already carries the tile. Removals last, so an id that appears in both lists in one frame
            // (destroyed and re-created with the same number, which the authority can do) ends up existing.
            if (frame.WorldObjectCreations is { Count: > 0 } worldObjectCreations)
                foreach (var state in worldObjectCreations) WorldObjectLifecycleSync.Apply(state);
            if (frame.WorldObjectsRemoved is { Count: > 0 } worldObjectsRemoved)
                foreach (var id in worldObjectsRemoved) WorldObjectLifecycleSync.ApplyRemoval(id);
        // World object state. Applied after the tile, because moving an object can rebuild parts of it and the state
        // that arrived in the same frame is the newer statement of the two.
        if (frame.WorldObjectStates is { Count: > 0 } worldObjectStates)
            foreach (var state in worldObjectStates) WorldObjectStateSync.Apply(state);
        // Quest standing. Only the fields the passing of time would have written - rebuilding a quest here would replay
        // every QuestPart, and the object already came in with the save.
        if (frame.Quests is { Count: > 0 } quests)
            foreach (var state in quests) QuestSync.Apply(state);
        // Memory ages. Only `age` travels: the mood card recomputes from the memories, so fixing the inputs is enough.
        if (frame.Memories is { Count: > 0 } memories)
            foreach (var state in memories) MemorySync.Apply(state);
        // The story log. Only the fields the passing of time writes - `TaleManager.Add` runs `CheckCullTales`, which can evict
        // other tales to make room.
        if (frame.Tales is { Count: > 0 } tales)
            foreach (var state in tales) TaleSync.Apply(state);
        // Toil progress. The bar over a working pawn's head is `1f - ticksLeftThisToil / defaultDuration`; only
        // `JobDriver.DriverTick` decrements that counter and a replica never ticks a driver, so every colonist's bar
        // stands at the value the save was loaded with while the job channel keeps changing the toil it belongs to.
        // Only the numerator travels: the denominator comes from the toil the replica already rebuilt.
        if (frame.ToilProgress is { Count: > 0 } toilProgressStates)
            foreach (var state in toilProgressStates) ToilProgressSync.Apply(state);
        // After the toil counter, and after any driver the job channel rebuilt this frame: the counters live on the
        // driver instance, so writing them before `TickPawn` had a chance to replace it would write the old one.
        if (frame.Drivers is { Count: > 0 } driverStates)
            foreach (var state in driverStates) DriverStateSync.Apply(state);
        if (frame.Deathrest is { Count: > 0 } deathrestStates)
            foreach (var state in deathrestStates) DeathrestSync.Apply(state);
        // Stance after the job: `SyncJob` installs the driver, and the stance is what the renderer and the rotation
        // tracker read on top of it.
        if (frame.Stance is { Count: > 0 } stanceStates)
            foreach (var state in stanceStates) StanceSync.Apply(state);
        // Lord counters last: the ritual progress bar is a mote that any of the above may have spawned.
        if (frame.Lords is { Count: > 0 } lordStates)
            foreach (var state in lordStates) LordSync.Apply(state);
        // Quest parts after everything that could have changed a part's state: the fragment is the whole list.
        if (frame.QuestParts is { Count: > 0 } questPartStates)
            foreach (var state in questPartStates) QuestPartSync.Apply(state);
        // Pawn sub-trackers last: guilt gates the arrest and execution menus, and the flight lerp lifts the graphic.
        if (frame.PawnTrackers is { Count: > 0 } trackerStates)
            foreach (var state in trackerStates) PawnTrackerSync.Apply(state);
        // Identity is the one thing a replica cannot infer: objects are delivered under the host's ids, so this
        // peer's counter is a copy of the host's, never a source. Compared every frame, because a single local
        // allocation is enough to make a later id-based command address a different object here.
        AlignIdentityCounter(frame.NextThingId);
        RefreshResourceCounts(frame.Tick);
        // 300 rather than 600: a 150-second acceptance run only applies a few hundred frames, so the old interval
        // never printed and the counters below were unreadable exactly when they were needed.
        if (AppliedFrames % 300 == 0)
        {
            PruneMotions();
            // Verification probe: TicksGame is patched to report the host clock, so it cannot answer "did this
            // peer actually simulate". The engine's own counter can. It only moves inside DoSingleTick, so a
            // replica that never ticked keeps reporting the tick its checkpoint was loaded at.
            Log.Message($"[TurboTick] 引擎tick={EngineTicks()} 帧tick={frame.Tick} "
                + $"权威帧 {frame.Tick}：{frame.Pawns.Count} 个角色（滑动中 {SmoothCount()}），{frame.Changed?.Count ?? 0} 个物体变化（累计 {changedThingsTotal}），" +
                $"新建 {Created} 失败 {CreateFailures} 缺Def {MissingDefs}，" +
                $"禁止修复 {forbidRepairs} 次（修复后仍未对上 {forbidMisses} 次），翻转 {forbidFlips} 次"
                + $"（到禁止 {forbidTrueArrivals} / 到允许 {forbidFalseArrivals}），到达的禁止位 真 {forbidArrivedTrue} / 假 {forbidArrivedFalse}，样本 {forbidFlipSamples}，" +
                $"变化记录 {applyThingCalls} 条/找不到 {applyThingMisses} 条（其中曾建过又被销毁 {applyThingMissCreated} / 从没收到创建帧 {applyThingMissNever}），"
                + $"被顶掉的事件帧 合并 {mergedEventFrames} 次，" +
                ActivitySummary(frame) +
                $"Scribe 状态 成功 {StatesApplied} / 失败 {StatesFailed}，" +
                $"失去地图 {DetachCount()}，帧间隔 最差 {worstGap:0.00}s / 停顿 {hitchCount} 次，"
                // 停顿期间本端循环跑了多少次。60 fps 下 0.43 秒应该有约 26 次；只有个位数说明这个副本
                // 根本没被调度到（同机压测的 CPU 抢占），而不是帧来晚了 —— 两种情况一个改环境一个改协议。
                + $"停顿期本端循环 {worstHitchPumps}–{mostHitchPumps} 次，"
                + $"角色应用 最差 {worstPawnApply * 1000f:0.0}ms（{worstPawnApplyCount} 人）/ 累计 {pawnApplyTotal:0.00}s，"
                + $"超预算丢弃 {DroppedWork}，" +
                $"校正 {corrections}，对齐工作 {jobAlignments}/失败 {jobAlignmentFailures}，时钟偏 {worstTickOffset} tick，平均漂移 {(driftSamples == 0 ? 0f : driftSum / driftSamples):0.00} 格 / 最差 {worstDrift:0.00} 格，" +
                $"跳过生成类命令 {skippedCommands}，id 对齐 {idAlignments} 次（多分配 {idAheadEvents} 次/最差 {worstIdAhead}，自检失败 {idCheckFailures}），天气同步 {weatherChanges} 次，" +
                $"呈现 {PresentationProbe()}，" +
                $"{LetterSync.Summary()}，{PlaySettingsSync.Summary()}，{AutoSlaughterSync.Summary()}，{AreaSync.Summary()}，{DoorSync.Summary()}，{RoofSync.Summary()}，{SnowSync.Summary()}，{FogSync.Summary()}，{MentalStateSync.Summary()}，{InspirationSync.Summary()}，{GuestSync.Summary()}，{PawnTraitSync.Summary()}，{RoyaltySync.Summary()}，{SettlementSync.Summary()}，{PlanSync.Summary()}，{ResearchSync.Summary()}，{RoomTempSync.Summary()}，{PlantSync.Summary()}，{FactionSync.Summary()}，{WorldObjectSync.Summary()}，{QuestSync.Summary()}，{MemorySync.Summary()}，{TaleSync.Summary()}，{SettlementRule.Describe(SettlementGates.Data)}，{SettlementGates.Summary()}，" +
                $"累计 {AppliedFrames} 帧。");
            hitchCount = 0;
            worstGap = 0f;
            pawnApplyTotal = 0f;
            worstPawnApply = 0f;
            worstPawnApplyCount = 0;
            worstHitchPumps = -1;
            mostHitchPumps = 0;
            mergedEventFrames = 0;
            jobSyncs = 0;
            jobSyncFailures = 0;
            jobsCleared = 0;
            billLinks = 0;
            queueTargetsPushed = 0;
            toilForced = 0;
            toilForceFailures = 0;
            toilClamped = 0;
            startFailures = 0;
            pawnsReconciled = 0;
            pawnsRestored = 0;
            reconcileSkips = 0;
            worstTickOffset = 0;
            worstDrift = 0f;
            driftSum = 0f;
            driftSamples = 0;
        }
    }

    /// <summary>Objects left in the draw list with a null map: the exact condition behind the
    /// GetSnowDepth NullReferenceException. Recovering any means a location field leaked through.</summary>
    /// <summary>Live objects that lost their map. Destroyed objects linger in the section layer until the
    /// next rebuild and are expected; anything still alive here means a location field leaked through.</summary>
    private static int DetachCount()
    {
        // Called from the once-every-600-frames log line. The scan walks every thing on every map, so it is
        // cached for a minute of frames: a broken plant does not become more broken, and the number is only
        // meant to answer "did anything get orphaned since the last look".
        if (Clock.Now - detachedSampledAt < 60f) return detached;
        detachedSampledAt = Clock.Now;
        detached = ScanDetached();
        return detached;
    }

    private static float detachedSampledAt;
    private static int detached;

    private static int ScanDetached()
    {
        var count = 0;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not RimWorld.Plant plant || plant.Destroyed) continue;
                if (plant.Map is not null && plant.Spawned && plant.def?.plant is not null) continue;
                count++;
                ReportBrokenPlant(plant);
            }
        return count;
    }

    private static readonly HashSet<int> BrokenLogged = [];

    internal static void ReportBrokenPlant(RimWorld.Plant plant)
    {
        if (plant.Destroyed) return;
        if (!BrokenLogged.Add(plant.thingIDNumber) || BrokenLogged.Count > 12) return;
        Log.Warning($"[TurboTick] 跳过绘制 id={plant.thingIDNumber} def={plant.def?.defName} " +
            $"graphicData={(plant.def?.graphicData is null ? "null" : "ok")} " +
            $"defPlant={(plant.def?.plant is null ? "null" : "ok")} map={(plant.Map is null ? "null" : "ok")} " +
            $"spawned={plant.Spawned} pos={plant.Position}");
    }
    private static int RespawnFailures;

    /// <summary>Drops interpolation entries for pawns that are gone, so a long session cannot leak them.</summary>
    private static void PruneMotions()
    {
        if (motions.Count == 0) return;
        var alive = new HashSet<int>();
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned) alive.Add(pawn.thingIDNumber);
        foreach (var id in motions.Keys.Where(id => !alive.Contains(id)).ToList()) motions.Remove(id);
    }

    /// <summary>Despawns pawns the host stopped publishing. A frame's <see cref="AuthoritativeFrame.Pawns"/>
    /// is a **full roster**, not a delta (unlike things, which travel as changes plus explicit removals), so a
    /// pawn missing from it is gone on the host - died, walked off the map, left in a caravan.
    ///
    /// Nothing did this before, and the omission is invisible in a short run: the roster only shrinks when
    /// somebody dies. Measured over a 4-hour 5-replica run the host went 31 → 26 colonists
    /// (`权威帧 … 26 个角色` in 63 consecutive frames) while every replica still reported `有job的副本 30/31`
    /// and `toil … /未知 6` - six colonists standing in the colony that no longer existed anywhere else.
    /// They were also why `toil 未知` was non-zero: their ids are absent from `hostToil`, so every comparison
    /// skipped them.
    ///
    /// Two frames of agreement are required before despawning (`PawnMissFrames`), because a single frame whose
    /// pawn list was cut short must not delete a visible colonist. `DeSpawn(Vanish)` rather than `Destroy`: the
    /// replica runs no death processing, and `Destroy` on a live pawn runs `Pawn.Kill` paths that write thoughts,
    /// relations and corpses - all host-side effects this peer is not allowed to have.
    ///
    /// Not run for predictive peers: they simulate, so a pawn missing from one frame may legitimately exist
    /// locally (mid-path, mid-spawn).</summary>
    private static void ReconcilePawns(AuthoritativeFrame frame)
    {
        if (Predictive) return;
        var roster = frame.Pawns;
        if (roster is null) return;
        var present = rosterIds;
        present.Clear();
        foreach (var state in roster) present.Add(state.Id);
        // A parked pawn is back in the host's roster: it left and returned (caravan, drop pod, quest). Handing
        // it to `ApplyPawn` is what respawns it, so it only has to be dropped from the "missing" tally here.
        foreach (var id in parkedPawns.Keys.Where(present.Contains).ToList())
            pawnMissFrames.Remove(id);
        // Collected first, despawned after: `mapPawns.AllPawnsSpawned` is the live list that `DeSpawn` mutates,
        // and enumerating a collection while removing from it throws.
        stalePawns.Clear();
        var localPawns = 0;
        var matched = 0;
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                localPawns++;
                var id = pawn.thingIDNumber;
                if (present.Contains(id)) { matched++; pawnMissFrames.Remove(id); continue; }
                pawnMissFrames.TryGetValue(id, out var misses);
                misses++;
                pawnMissFrames[id] = misses;
                // Two frames of agreement, not one: a single frame that decoded short must not delete a
                // visible colonist, and the cost of waiting is one frame (~17 ms).
                if (misses >= 2) stalePawns.Add(pawn);
            }
        // Safety valve: if the roster shares no id with this peer at all, the two sides are not describing the
        // same pawns - an id-counter divergence or a frame decoded against the wrong map - and acting on it would
        // despawn the entire colony in one pass. That failure is unrecoverable (every later frame finds nothing to
        // apply to), while skipping costs one frame and shows up as `对账跳过`.
        if (localPawns > 1 && matched == 0)
        {
            pawnMissFrames.Clear();
            reconcileSkips++;
            if (reconcileSkips <= 8)
                Log.Warning($"[TurboTick] 名单对账跳过：本端 {localPawns} 个角色与权威 {present.Count} 个 id 无一重合"
                    + "（疑似 id 计数分叉，先不动它们）");
            return;
        }
        foreach (var pawn in stalePawns)
        {
            var id = pawn.thingIDNumber;
            pawnMissFrames.Remove(id);
            jobShapes.Remove(id);
            billRetries.Remove(id);
            motions.Remove(id);
            try
            {
                pawn.jobs?.ClearDriver();
                if (pawn.jobs is not null) pawn.jobs.curJob = null;
                // Kept, not destroyed: a colonist that left in a caravan or a drop pod can come back, and the
                // host would publish it again. `ApplyPawn` cannot find a despawned pawn, so the reference is
                // parked here and `RespawnParked` puts it back the moment its id shows up in a roster.
                parkedPawns[id] = pawn;
                pawn.DeSpawn(DestroyMode.Vanish);
                pawnsReconciled++;
            }
            catch (Exception error)
            {
                if (pawnReconcileFailures++ < 8)
                    Log.Warning("[TurboTick] 清理掉队角色失败 id=" + id + "：" + error.GetType().Name + " " + error.Message);
            }
        }
    }

    private static readonly List<Pawn> stalePawns = [];

    /// <summary>Puts back a pawn this peer despawned from <see cref="ReconcilePawns"/>. A colonist who left on a
    /// caravan and returned, or who was off-map when the roster was compared, is published again by the host -
    /// and `ApplyPawn` looks pawns up with `mapPawns.AllPawnsSpawned`, which a despawned pawn is not part of.
    /// Without this the pawn would stay invisible forever even though the host lists it every frame.
    ///
    /// Returns true when a parked pawn was respawned, so the caller knows the map list changed and must look the
    /// pawn up again.</summary>
    private static bool RespawnParked(int id, int mapId, Vector3 where)
    {
        if (parkedPawns.Count == 0) return false;
        if (!parkedPawns.Remove(id, out var pawn)) return false;
        var map = Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == mapId);
        if (map is null || pawn.Destroyed) return false;
        try
        {
            // Spawn at the host's position, not at a corner: the pawn is being restored and `ApplyPawn` is about
            // to run on the same frame, so the cell here only has to be legal.
            var cell = new IntVec3((int)where.x, 0, (int)where.z);
            if (!cell.InBounds(map)) cell = new IntVec3(map.Size.x / 2, 0, map.Size.z / 2);
            var landed = GenSpawn.Spawn(pawn, cell, map) is not null;
            if (landed) pawnsRestored++;
            return landed;
        }
        catch (Exception error)
        {
            if (pawnReconcileFailures++ < 8)
                Log.Warning("[TurboTick] 恢复角色失败 id=" + id + "：" + error.GetType().Name + " " + error.Message);
        }
        return false;
    }

    private static readonly Dictionary<int, Pawn> parkedPawns = new();

    private static readonly HashSet<int> rosterIds = [];
    /// <summary>How many consecutive frames each pawn has been missing from the host's roster.
    /// See <see cref="ReconcilePawns"/> for why one frame is not enough.</summary>
    private static readonly Dictionary<int, int> pawnMissFrames = new();
    private static int pawnsReconciled;
    private static int pawnReconcileFailures;
    private static int reconcileSkips;
    /// <summary>Pawns put back into the world after having been reconciled away. Counted separately from
    /// `pawnsReconciled` because "we cleaned up" and "we cleaned up something that then came back" are two
    /// different outcomes, and only the pair proves the parking round-trip works.</summary>
    private static int pawnsRestored;

    /// <summary>Last plan-list version applied. Kept apart from `PlanSync.Version` so the authority's counter and the
    /// replica's bookkeeping cannot interfere.</summary>
    private static long lastPlanVersion = -1;

    /// <summary>Moves a pawn that the host has on another map onto the map the frame names.
    ///
    /// <para><b>Why `DeSpawn` + `GenSpawn.Spawn` and not a `Position` write.</b> `Thing.Position` only means something
    /// within one map's grid: assigning a cell from a 250x250 map to a pawn standing on an 80x80 one lands out of bounds
    /// and every later `Print`/`GetThingList` reads garbage. Changing `Thing.Map` has no public setter - the only
    /// supported route is to take the pawn off one map and put it on the other, which is what
    /// `GenSpawn.Spawn(pawn, cell, map)` does (it sets `mapIndexOrState`, registers with `mapPawns` and the lister).</para>
    ///
    /// <para><b>Why the destination cell is clamped.</b> A pawn crossing to a smaller map keeps its old coordinates in
    /// `Thing.Position` until it is spawned; if those are out of bounds the spawn is refused and the pawn is left
    /// despawned - invisible rather than merely misplaced, which is harder to notice. Clamping to the destination's
    /// centre makes the first frame show it somewhere legal; the next frame carries its real position.</para>
    ///
    /// <para>`mapPawns.DeRegisterPawn` is called by `DeSpawn` through the thing's own remove path, and the
    /// faction/attack-target caches are refreshed in `ApplyPawnFaction` on the same frame - the pawn is on the new map
    /// byte-by-byte before anything reads it.</para></summary>
    private static Pawn? RelocateAcrossMaps(ReplicaPawn state)
    {
        if (!FindPawnAny(state.Id, out var pawn) || pawn is null || pawn.Destroyed) return null;
        var destination = FindMapsByUniqueId(state.Map);
        if (destination is null) return null;
        var origin = pawn.Map;
        if (origin is null) return null;
        if (origin == destination) return pawn;
        try
        {
            var cell = new IntVec3((int)state.X, 0, (int)state.Z);
            if (!cell.InBounds(destination)) cell = destination.Center;
            pawn.DeSpawn(DestroyMode.Vanish);
            if (GenSpawn.Spawn(pawn, cell, destination) is null)
            {
                // Put it back rather than leave it nowhere: a pawn that failed to arrive should still be visible on
                // the map it came from, with the failure counted.
                if (origin is not null && !pawn.Spawned) GenSpawn.Spawn(pawn, origin.Center, origin);
                mapRelocateFailures++;
                return null;
            }
            mapRelocations++;
            return pawn;
        }
        catch (Exception error)
        {
            mapRelocateFailures++;
            if (mapRelocateFailures <= 6)
                Log.Warning($"[TurboTick] 跨图转移失败 id={state.Id} 图{(origin is null ? "?" : origin.uniqueID.ToString(CultureInfo.InvariantCulture))}"
                    + $"→{state.Map}：{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>How many pawns this peer moved between maps because the host had them elsewhere. Zero on a
    /// single-map colony, so a non-zero number is the evidence that map transitions travel at all.</summary>
    internal static int MapRelocations => mapRelocations;
    internal static int MapRelocateFailures => mapRelocateFailures;

    private static int mapRelocations;
    private static int mapRelocateFailures;

    private static Map? FindMapsByUniqueId(int mapId)
    {
        foreach (var map in Find.Maps)
            if (map is not null && map.uniqueID == mapId) return map;
        return null;
    }

    private static Pawn? FindPawn(int id, int mapId)
    {
        foreach (var map in Find.Maps)
        {
            if (map.uniqueID != mapId) continue;
            return map.mapPawns.AllPawnsSpawned.FirstOrDefault(candidate => candidate.thingIDNumber == id);
        }
        return null;
    }

    /// <summary>Finds a pawn by id without knowing its map.
    ///
    /// <para>`FindPawn` needs the map id because a frame entry carries one, but the hediff channel does not: the
    /// condition list is keyed by pawn alone. Scanning spawned pawns on every map is a handful of comparisons and
    /// avoids carrying a map id that would be redundant with the pawn's own position.</para></summary>
    internal static bool FindPawnAny(int id, out Pawn pawn)
    {
        foreach (var map in Find.Maps)
        {
            var found = map.mapPawns.AllPawnsSpawned.FirstOrDefault(candidate => candidate.thingIDNumber == id);
            if (found is not null) { pawn = found; return true; }
        }
        pawn = null!;
        return false;
    }

    /// <summary>Per-frame id index. `FindThing` used to scan `Find.Maps × listerThings.AllThings` on every
    /// lookup, so one frame carrying N changed objects cost O(N × everything) - the measured hitch source
    /// (`帧间隔 最差 0.41s / 停顿 5 次`, 46 tps against a 60 target). Built on first use in a frame and dropped
    /// at the start of the next, so a change made by anything else is picked up exactly as before.</summary>
    private static Dictionary<int, Thing>? frameIndex;

    /// <summary>One id lookup table per frame, covering the three places a receiver can find an object by id:
    /// spawned things (`listerThings`), map pawns (a pawn that died on the map stays here and is *not* a world
    /// pawn), and world pawns (anything off the map). Every caller used to scan a subset linearly, so a frame
    /// with N changes cost O(N x everything) and a corpse's contents were looked up in the one place they
    /// provably were not (`Find.WorldPawns`), which is what left 22 corpses unbuildable in the client log.</summary>
    private static Dictionary<int, Thing> BuildFrameIndex()
    {
        var index = new Dictionary<int, Thing>();
        foreach (var map in Find.Maps)
        {
            // Every list below can contain a null element. `WorldPawns.ExposeData` proves it for the world lists
            // (`pawnsAlive.RemoveWhere(x => x == null)` on load, with an error message for each one removed), and the
            // map lists are maintained by spawn/despawn notifications rather than rebuilt, so an object destroyed
            // without one lingers as a hole. Reading `thing.thingIDNumber` on that hole is the NullReferenceException
            // this method used to throw once per frame - measured 5048 lines in three minutes on the client, every one
            // of them aborting `ApplyPawn` -> `SyncJob` -> `FindThing`, which is the whole frame application.
            foreach (var thing in map.listerThings.AllThings)
                if (thing is not null) index[thing.thingIDNumber] = thing;
            foreach (var pawn in map.mapPawns.AllPawns)
                if (pawn is not null) index[pawn.thingIDNumber] = pawn;
            // A thing a pawn is holding is *not* in `listerThings`: `Pawn_CarryTracker.innerContainer` takes the
            // stack off the map's thing grid. That is exactly the state a hauling job is in, and its
            // `targetA` is the carried stack - so the index has to reach into the holders or the target reads
            // as "missing" for every job in progress. Measured as `Casted LocalTargetInfo to Thing but it had
            // cell (121, 0, 122)` once per haul, from `JobDriver_HaulToContainer.ThingToCarry`.
            foreach (var pawn in map.mapPawns.AllPawns)
                if (pawn is not null) IndexHeld(index, pawn);
        }
        var worldPawns = Find.WorldPawns?.AllPawnsAliveOrDead;
        if (worldPawns is not null)
            for (var i = 0; i < worldPawns.Count; i++)
            {
                var pawn = worldPawns[i];
                if (pawn is null) continue;
                if (!index.ContainsKey(pawn.thingIDNumber)) index[pawn.thingIDNumber] = pawn;
                IndexHeld(index, pawn);
            }
        return index;
    }

    /// <summary>Indexes everything a pawn carries, wears or holds, without recursing into the map.
    ///
    /// <para>Deliberately shallow: <c>ThingOwnerUtility.GetAllThingsRecursively(map, …)</c> would also walk every
    /// building's storage, and the index is rebuilt once per frame. The containers below are the ones a job
    /// can name as a target while the thing is off the grid.</para></summary>
    private static void IndexHeld(Dictionary<int, Thing> index, Pawn pawn)
    {
        try
        {
            var carried = pawn.carryTracker?.innerContainer;
            if (carried is not null)
                for (var i = 0; i < carried.Count; i++) index[carried[i].thingIDNumber] = carried[i];
            var inventory = pawn.inventory?.innerContainer;
            if (inventory is not null)
                for (var i = 0; i < inventory.Count; i++) index[inventory[i].thingIDNumber] = inventory[i];
            var equipment = pawn.equipment?.AllEquipmentListForReading;
            if (equipment is not null)
                for (var i = 0; i < equipment.Count; i++) index[equipment[i].thingIDNumber] = equipment[i];
        }
        catch (Exception) { }
    }

    internal static Thing? FindThing(int id)
    {
        frameIndex ??= BuildFrameIndex();
        return frameIndex.TryGetValue(id, out var found) ? found : null;
    }

    private static void ApplyThing(ReplicaThing state)
    {
        applyThingCalls++;
        var thing = FindThing(state.Id);
        if (thing is null)
        {
            // 93% 的 miss（21221 条变化里 19660 条找不到）不可能是「少量瞬时物体」，但
            // 「曾经建过又被销毁」与「从来没收到创建帧」是两种完全不同的事故：
            // 前者是权威端每帧都在为一个本端已销毁的 id 发变化，后者是创建帧真的丢了。
            // 只报一个总和时这两种情况读起来一模一样。
            if (createdIds.Contains(state.Id)) applyThingMissCreated++;
            else applyThingMissNever++;
            applyThingMisses++;
            return; // A creation frame that has not arrived yet; the next one carries it.
        }
        var cell = new IntVec3(state.X, 0, state.Z);
        var map = thing.Map;
        if (map is null) return;
        if (thing.Position != cell && cell.InBounds(map))
        {
            frameMovedThing = true;
            // DeSpawn clears Thing.Map, so the map and rotation must be captured first; otherwise the
            // object is respawned onto a null map and every later Print throws in GetSnowDepth.
            var rotationBefore = thing.Rotation;
            thing.DeSpawn(DestroyMode.Vanish);
            var respawned = GenSpawn.Spawn(thing, cell, map, rotationBefore, WipeMode.VanishOrMoveAside);
            if (respawned is null || !thing.Spawned)
            {
                if (RespawnFailures++ < 8)
                    Log.Warning($"[TurboTick] 重定位失败 {thing.def.defName} id={thing.thingIDNumber} -> {cell}");
            }
        }
        var rotation = new Rot4(state.Rotation);
        if (thing.Rotation != rotation) thing.Rotation = rotation;
        if (thing.def.stackLimit > 1 && thing.stackCount != state.Stack) thing.stackCount = state.Stack;
        if (thing.def.useHitPoints && thing.HitPoints != state.HitPoints) thing.HitPoints = state.HitPoints;
        // Mod state rides along as the object's own Scribe XML; no per-mod handling is required.
        if (state.State is { Length: > 0 } xml && thing is IExposable exposable)
            ApplyPreservingIdentity(thing, exposable, xml);
        // A plant that has grown needs its mesh rebuilt, and vanilla does that from `Plant.TickLong` - which a replica
        // never runs. `MapMeshDirty` is therefore not a cache hint here, it is the only thing that turns a received
        // `growthInt` into a different picture. Vanilla's own call is gated on `CurrentlyCultivated()` and on a
        // tenth-of-growth change; a replica cannot reproduce that test (it holds no `growingZone`), and the sender
        // already quantised the change to 1/256, so every arrival is a visible step. Measured defect this fixes: a
        // growing zone stayed a field of `GraphicSowing` because nothing ever asked the section to repaint.
        if (thing is RimWorld.Plant growing && growing.Spawned && growing.Map is { } plantMap)
            plantMap.mapDrawer.MapMeshDirty(growing.Position, MapMeshFlagDefOf.Things);
        ApplyForbidden(thing, state.Forbidden, state.State is { Length: > 0 });
        // A container's contents are reassembled from the fragment above, but only if the inner things exist here.
        // The authority sends them only on creation (they are separate objects with their own ids), and a
        // construction site gains its materials long after it was created - so the arrival of a new material is a
        // `ReplicaThing`, not a `ReplicaCreation`, and without this the received wood is a reference id pointing at
        // nothing. Measured symptom: the authority's frame held `WoodLog x2`, the replica's stayed at 0.
        if (state.State is { Length: > 0 } && thing is IConstructible && thing is IThingHolder holder)
            ReconcileContents(thing, holder);
    }

    /// <summary>Makes the local container match what the fragment just loaded.
    ///
    /// <para>The fragment's <c>ThingOwner.ExposeData</c> resolved whatever reference ids it could; the rest were
    /// pruned, silently and by design (that is how a load drops references to things that no longer exist). For a
    /// construction site the pruned entries are its building materials - objects created in a *different* frame, so
    /// this peer has never seen their ids.</para>
    ///
    /// <para>So the total is read back from the fragment and compared with the container. A mismatch is reported
    /// rather than patched by inventing a stack: inventing one would put material on this peer that the authority
    /// might not have, and the two colonies would disagree about how much is left to build. What this does instead is
    /// make the discrepancy visible - the alternative is a silent zero that reads like "nobody deposited anything".
    /// </summary>
    private static void ReconcileContents(Thing thing, IThingHolder holder)
    {
        try
        {
            if (thing is not IConstructible constructible) return;
            var container = holder.GetDirectlyHeldThings();
            if (container is null) return;
            // What the fragment says it should hold, read from the same interface the givers use.
            foreach (var cost in constructible.TotalMaterialCost())
            {
                var need = Math.Max(0, cost.count - constructible.ThingCountNeeded(cost.thingDef));
                var have = 0;
                for (var i = 0; i < container.Count; i++)
                    if (container[i]?.def == cost.thingDef) have += container[i].stackCount;
                if (have >= need) continue;
                if (contentsShort++ < 8)
                    Log.Warning($"[TurboTick] 建造材料缺失 {thing.def.defName}#{thing.thingIDNumber}"
                        + $"（{thing.Position.x},{thing.Position.z}）：应为 {need} 个 {cost.thingDef.defName}，"
                        + $"本端只有 {have} 个（片段里的引用目标本端从未见过）");
            }
        }
        catch (Exception error)
        {
            if (contentsErrors++ < 4)
                Log.Warning("[TurboTick] 建造材料对账失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static int contentsShort;
    private static int contentsErrors;

    /// <summary>The red X is an overlay handle, and nothing on the loading path rebuilds it: <c>PostExposeData</c>
    /// writes the backing field, so a state reload can leave the flag correct and the marker wrong. The vanilla
    /// setter also returns before the refresh whenever the value already matches, so the overlay is rebuilt here
    /// explicitly - whenever the flag changes, and after a state reload, which is the only other way it can drift.</summary>
    private static void ApplyForbidden(Thing thing, bool forbidden, bool reloaded)
    {
        if (thing is not ThingWithComps withComps) return;
        if (withComps.GetComp<CompForbiddable>() is not { } comp) return;
        var wasForbidden = comp.Forbidden;
        var changed = wasForbidden != forbidden;
        // Counted on arrival, not on change: if every record already agrees with the local value then either the
        // authority never applied anything, or the two worlds disagree about which objects these ids are. Counting
        // only the changes cannot tell those apart - both read zero.
        if (forbidden) forbidArrivedTrue++; else forbidArrivedFalse++;
        if (changed)
        {
            forbidFlips++;
            if (forbidden) forbidTrueArrivals++; else forbidFalseArrivals++;
            if (forbidFlipSamples.Length < 220)
                forbidFlipSamples.Append(thing.thingIDNumber).Append(':').Append(thing.def?.defName)
                    .Append(wasForbidden ? "->T" : "->F").Append(' ');
        }
        // Counted BEFORE the write, and counted on the value rather than on the marker: the vanilla setter calls
        // UpdateOverlayHandle itself, so any marker test placed after it is guaranteed to agree.
        var markerWrong = !OverlayMatches(comp, forbidden);
        // Exactly one write path, and it is the one ColonyTogether's GenericRefresh.After uses: nudge the backing
        // field off the target value, then assign the property, so vanilla's own guard passes and it runs every side
        // effect. Assigning the property directly here is what left the flag right and the X on screen for forty
        // batches in a row: when the value already matched, the setter returned before touching the marker.
        if (changed || reloaded || markerWrong) SharedActions.SetForbiddenThroughSetter(thing, forbidden);
        if (markerWrong) forbidRepairs++;
        if (!OverlayMatches(comp, forbidden)) forbidMisses++;
    }

    private static int forbidRepairs;
    private static int forbidMisses;

    // The number that actually answers "did the frame change the flag": how many times the arrived value differed
    // from the local one. Split by direction, because "the authority keeps sending true" and "it keeps sending
    // false and the peer keeps coming back forbidden" are different bugs with the same end state on screen.
    private static int forbidFlips;
    private static int forbidTrueArrivals;
    private static int forbidFalseArrivals;
    private static int forbidArrivedTrue;
    private static int forbidArrivedFalse;
    private static readonly System.Text.StringBuilder forbidFlipSamples = new();

    // 补扫已经把物品状态发出去了，但标志位一个都没变，所以现在要区分两种可能：
    // 副本根本找不到这个 id（帧里的变化记录被首行 return 掉），还是找到了却没生效。
    // 这两个数一起读才有意义，单独一个都会和「补扫没发」混在一起。
    private static int applyThingCalls;
    private static int applyThingMisses;
    /// <summary>拆开的两个 miss 成因：本端曾经建过这个 id 又被销毁（权威端还在为它发变化），
    /// 与本端从没收到过创建帧。见 `ApplyThing` 里的注释——一个总和读不出是哪一种。</summary>
    private static int applyThingMissCreated;
    private static int applyThingMissNever;
    private static readonly HashSet<int> createdIds = [];

    /// <summary>What this replica's map actually contains, by category, read fresh instead of counted.</summary>
    /// <remarks>
    /// The counters above describe the traffic; these describe the world. The difference matters for the reported
    /// "the wall finished on the host but a blueprint is still drawn here": frame counters can be perfect while one
    /// object is left behind, and only the map itself says whether the replica holds a blueprint the host does not.
    /// Reading counts instead of accumulating them also makes double-counted applications visible - a counter cannot
    /// detect itself, a recount can.
    /// </remarks>
    internal static string WorldTally()
    {
        try
        {
            var blueprints = 0;
            var frames = 0;
            var walls = 0;
            var things = 0;
            foreach (var map in Find.Maps)
            {
                if (map is null) continue;
                foreach (var thing in map.listerThings.AllThings)
                {
                    if (thing.Destroyed || !thing.Spawned) continue;
                    things++;
                    if (thing is Blueprint) blueprints++;
                    else if (thing is Frame) frames++;
                    if (thing.def?.defName == "Wall") walls++;
                }
            }
            // The blueprint ids are printed, not just their count: an id the host no longer has is a leftover, and an
            // id the host does have is a timing question. Those two need opposite fixes and read the same as a number.
            var ids = new List<int>();
            foreach (var map in Find.Maps)
            {
                if (map is null) continue;
                foreach (var thing in map.listerThings.AllThings)
                    if (thing is Blueprint blueprint && !blueprint.Destroyed && blueprint.Spawned)
                        ids.Add(blueprint.thingIDNumber);
            }
            ids.Sort();
            return $"副本世界 物体={things} 蓝图={blueprints} 建造架={frames} 墙={walls}"
                + (ids.Count > 0 ? " 蓝图id=" + string.Join(",", ids.Take(12)) : "");
        }
        catch (Exception error) { return "副本世界统计失败：" + error.GetType().Name; }
    }

    /// <summary>True when the drawn marker agrees with the flag. Read rather than inferred: "the refresh definitely
    /// fixed it" is the kind of claim that stays plausible for months while the screen shows otherwise.</summary>
    private static bool OverlayMatches(CompForbiddable comp, bool forbidden)
    {
        try
        {
            overlayHandleField ??= AccessTools.Field(typeof(CompForbiddable), "overlayHandle");
            if (overlayHandleField is null) return true;
            return (overlayHandleField.GetValue(comp) is not null) == forbidden;
        }
        catch (Exception) { return true; }
    }

    private static System.Reflection.FieldInfo? overlayHandleField;

    /// <summary>Rebuilds the objects a container holds that this peer has never seen. They stay unspawned:
    /// the container owns them, so spawning them onto the map would put two copies of one pawn in the world.</summary>
    private static void CreateContents(IReadOnlyList<ReplicaCreation>? contents)
    {
        if (contents is null) return;
        // Registered only after every fragment is loaded: each `ScribeBridge.Load` clears the cross-reference
        // directory when it finishes, so a target registered before the next load would be forgotten again.
        List<ILoadReferenceable>? created = null;
        for (var i = 0; i < contents.Count; i++)
        {
            var state = contents[i];
            if (state is null) continue;
            if (FindThing(state.Id) is { } present)
            {
                if (present is ILoadReferenceable already) (created ??= []).Add(already);
                continue;
            }
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(state.Def);
            if (def is null)
            {
                if (MissingDefs++ < 8) Log.Warning("[TurboTick] 副本缺少 Def：" + state.Def);
                continue;
            }
            try
            {
                var idBeforeMake = SharedActions.NextThingId();
                var thing = MakeFromFrame(def);
                var madeIds = SharedActions.NextThingId() - idBeforeMake;
                if (madeIds > 0) expectedAllocations += madeIds;
                if (thing.thingIDNumber != state.Id) thing.thingIDNumber = state.Id;
                if (def.stackLimit > 1 && state.Stack > 1) thing.stackCount = state.Stack;
                if (state.State is { Length: > 0 } xml && thing is IExposable exposable)
                    ApplyPreservingIdentity(thing, exposable, xml);
                if (thing is ILoadReferenceable reffable) (created ??= []).Add(reffable);
                frameIndex?[thing.thingIDNumber] = thing;
            }
            catch (Exception error)
            {
                Log.Warning("[TurboTick] 容器内容创建失败 " + state.Def + " id=" + state.Id + "：" + error.GetType().Name + ": " + error.Message);
            }
        }
        if (created is null) return;
        for (var i = 0; i < created.Count; i++) ScribeBridge.RegisterReference(created[i]);
    }

    /// <summary>Cross-reference targets that live outside the fragment cannot be resolved by the load alone.
    /// A corpse's contents are stored as reference ids (`Thing_Human37571`), and that pawn sits under
    /// `world/worldPawns/pawnsDead` in the save - not under any map's thing list - so the replica's own map
    /// scan never finds it, `TakeResolvedRefList` hands back null and `ThingOwner`'s PostLoadInit prunes the
    /// entry, leaving the corpse empty and unspawnable. Registering the live object here puts it in the
    /// directory the resolve pass reads, which is exactly what a real save load has already done.</summary>
    private static void RegisterReferencedWorldPawns(string xml)
    {
        try
        {
            var document = new System.Xml.XmlDocument();
            document.LoadXml("<r>" + xml + "</r>");
            // Both shapes, because vanilla writes two.
            //
            // `Scribe_References.Look` writes a *leaf*: `Scribe.saver.WriteElement(label, uniqueLoadID)`
            // (`Verse/Scribe_References.cs:18`), producing `<targetA>Thing_X</targetA>`. `Scribe_Collections.Look`
            // writes `<li>Thing_X</li>` instead. Scanning only `//li` therefore missed every single-reference field -
            // a job's `targetA/B/C`, a door's `user`, a pawn's `carryTracker` - and the load failed on exactly those.
            // Measured on the player's colony: 1122 `Could not resolve reference` warnings in one session, e.g.
            // `curParent=Equip (Job_300) curPathRelToParent=/targetA` for `Thing_Gun_Revolver3844`, and
            // `curParent=Door49438` for `Thing_Human876`. Both names are single-reference fields.
            var nodes = document.SelectNodes("//li|//*[not(*)]");
            if (nodes is null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < nodes.Count; i++)
            {
                var loadId = nodes[i]?.InnerText;
                if (loadId is null || !loadId.StartsWith("Thing_", StringComparison.Ordinal)) continue;
                // A nested node repeats its own children, so the same id arrives several times per fragment; the
                // directory rejects duplicates with an error rather than ignoring them.
                if (!seen.Add(loadId)) continue;
                var id = Thing.IDNumberFromThingID(loadId);
                if (id < 0) continue;
                if (FindReferencedThing(id) is { } target) { ScribeBridge.RegisterReference(target); continue; }
                if (MissingReferences++ < 16)
                    Log.Warning($"[TurboTick] 引用目标缺失 {loadId}：地图上有 {CountOnMaps(id)} 个，" +
                        $"世界 pawn 里 {(WorldPawnCount(id))} 个，帧索引来源 {(frameIndex is null ? "空" : frameIndex.Count.ToString())} 条。");
            }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 预注册引用目标失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Reference targets come from three places and the corpse case needs all of them. A pawn that
    /// died on the map is still a map pawn (`mapPawns.AllPawns`), a pawn that left the map is a world pawn,
    /// and an ordinary thing is in `listerThings`. Looking only at world pawns is what made a measured 22
    /// corpses stay unbuildable in the client log: "Could not resolve reference Thing_Tortoise37501".</summary>
    private static Thing? FindReferencedThing(int id) => FindThing(id);

    private static int MissingReferences;

    private static int CountOnMaps(int id)
    {
        var found = 0;
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings) if (thing.thingIDNumber == id) found++;
            foreach (var pawn in map.mapPawns.AllPawns) if (pawn.thingIDNumber == id) found++;
        }
        return found;
    }

    private static int WorldPawnCount(int id)
    {
        var pawns = Find.WorldPawns?.AllPawnsAliveOrDead;
        if (pawns is null) return -1;
        var found = 0;
        for (var i = 0; i < pawns.Count; i++) if (pawns[i].thingIDNumber == id) found++;
        return found;
    }

    /// <summary>A Scribe fragment rewrites the def reference too. If that lookup cannot resolve, the def
    /// would become null and every later draw would throw, so the identity is restored on failure.
    ///
    /// <para>`Scribe_Defs.Look` writes the field on <i>every</i> path: a name that no longer resolves to a def leaves
    /// null behind and only logs, so "the load returned true" does not mean "the def survived". <c>Pawn.RaceProps</c>
    /// is <c>def.race</c> with no null check, and <c>PawnRenderer</c> reads it through
    /// <c>RaceProps.renderTree?.root</c> - so one missing def is a NullReferenceException in
    /// <c>PawnRenderTree.TrySetupGraphIfNeeded</c> every single frame the pawn is on screen. Measured 2026-11-16:
    /// 4263 `Exception drawing Jackalope` lines in three minutes, at a steady 24 per second.</para>
    ///
    /// <para>The restore is therefore unconditional rather than limited to the failure branch: the object's identity is
    /// not part of what the fragment owns, and letting a hostile/older fragment clear it would take the pawn off the
    /// render path permanently with no way back.</para></summary>
    private static void ApplyPreservingIdentity(Thing thing, IExposable exposable, string xml)
    {
        var def = thing.def;
        var id = thing.thingIDNumber;
        // The resolve pass only knows what took part in this one load, so any reference whose target is not in
        // the fragment - the dead pawn a corpse holds, a job's target thing - is unresolvable unless it is put
        // into the directory first. The corpse case was measured: `ReplicaThing` frames (the object already
        // existed, only its state changed) reached `ThingOwner.ExposeData` with an empty directory and logged
        // "Could not resolve reference ... Thing_Human37571", after which the container was pruned.
        RegisterReferencedWorldPawns(xml);
        var loaded = ScribeBridge.Load(exposable, xml);
        // Both branches, and before anything can read the object: see the summary above.
        if (thing.def is null && def is not null) thing.def = def;
        if (thing.thingIDNumber != id) thing.thingIDNumber = id;
        if (loaded) { StatesApplied++; return; }
        StatesFailed++;
    }

    /// <summary>Creates a thing from a definition without letting vanilla log an error about its material.
    ///
    /// <para>`ThingMaker.MakeThing(def)` is called with no stuff here because the frame does not carry one: the
    /// material arrives inside the object's own <c>ExposeData</c> fragment (<c>&lt;stuff&gt;Plasteel&lt;/stuff&gt;</c>),
    /// which is applied after the object exists. That ordering is fine - <c>Scribe_Defs.Look(ref stuffInt, "stuff")</c>
    /// restores it - but vanilla logs
    /// <c>MakeThing error: Apparel_AdvancedHelmet is madeFromStuff but stuff=null. Assigning default.</c>
    /// on the way there, and every such line lands in the player's debug log as a red ERROR for something that is
    /// about to be corrected. Measured on `192.168.0.29`: three such lines at 01:50:45 / 01:51:28 / 01:51:40.
    ///
    /// <para>Passing <c>GenStuff.DefaultStuffFor</c> is exactly what vanilla would have substituted anyway, so the
    /// object is identical and the log stays clean. When the def is not made from stuff the argument must be null,
    /// or vanilla logs the opposite error.</para></summary>
    private static Thing MakeFromFrame(ThingDef def)
    {
        ThingDef? stuff = null;
        if (def.MadeFromStuff)
        {
            try { stuff = GenStuff.DefaultStuffFor(def); }
            catch (Exception) { stuff = null; }
        }
        // Still null for a def whose stuffCategories match nothing installed; MakeThing has to log that one itself.
        return ThingMaker.MakeThing(def, stuff);
    }

    /// <summary>Re-counts stored resources on this peer, because nothing else ever does.
    ///
    /// <para><c>ResourceCounter.UpdateResourceCounts</c> is driven by <c>ResourceCounterTick</c>, which is called
    /// from <c>Map.MapPostTick</c> (<c>Verse/Map.cs:1085</c>), which is called from
    /// <c>TickManager.DoSingleTick</c> (<c>acs5005/Verse/TickManager.cs:454</c>) - and a replica never runs a tick:
    /// <see cref="SharedColony.TryBeginTick"/> returns false. So on a client the counter was only ever filled once,
    /// by whatever <c>Map.FinalizeInit</c> did at load, and it never moved again. The visible symptom is the one the
    /// player reported: haul a stack into a stockpile and the resource readout at the top of the screen keeps saying
    /// the colony has none of it.</para>
    ///
    /// <para>Same 204-tick cadence vanilla uses, counted against the host's tick so both sides refresh on the same
    /// frames. Forced on the frames that created or removed objects, because those are the ones a player is watching
    /// for (hauling something in should update the number immediately, not up to 204 ticks later), and
    /// <c>UpdateResourceCounts</c> is a walk over the map's slot groups - cheap at this scale.</para>
    ///
    /// <para>A third trigger is the one the report was actually about: <b>hauling does not create or remove
    /// anything</b>. A stack dragged into a stockpile keeps its id and only changes position, so the
    /// created/removed flag stays false and this would fall back to the 204-tick cadence - up to 3.4 seconds of the
    /// readout saying "0" after the player watched the item land. <see cref="ApplyThing"/> raises
    /// <see cref="frameMovedThing"/> on a reposition, which is the same event the counter is counting.</para>
    /// </summary>
    private static void RefreshResourceCounts(int hostTick)
    {
        var due = lastResourceCountTick == long.MinValue
            || hostTick - lastResourceCountTick >= 204
            || ((frameCreatedOrRemoved || frameMovedThing) && hostTick != lastResourceCountTick);
        if (!due) return;
        lastResourceCountTick = hostTick;
        frameCreatedOrRemoved = false;
        frameMovedThing = false;
        foreach (var map in Find.Maps)
        {
            try { map.resourceCounter?.UpdateResourceCounts(); }
            catch (Exception error)
            {
                // This is a guard, not the fix. `UpdateResourceCounts` walks every slot group and calls
                // `GetInnerIfMinified`, i.e. `MinifiedThing.InnerThing`, which threw while a minified object existed
                // with an empty container. The cause is fixed above (such objects are now loaded before they spawn);
                // what is left here is that one bad object must not take the frame down with it.
                if (resourceCountFailures++ < 4)
                    Log.Warning("[TurboTick] 资源计数刷新失败：" + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    /// <summary>The host tick the counts were last refreshed at. `long`, not `int`: the initial value is the
    /// "never refreshed" sentinel, and `hostTick - int.MinValue` overflows to a negative number, which would read as
    /// "not due yet" and skip the first refresh exactly when the counter is still empty.</summary>
    private static long lastResourceCountTick = long.MinValue;
    private static bool frameCreatedOrRemoved;
    /// <summary>Set when a frame repositioned an object. Hauling is exactly this: the stack keeps its id, its
    /// definition and its count, and only its cell changes - so it is invisible to the created/removed triggers.</summary>
    private static bool frameMovedThing;
    private static int resourceCountFailures;

    private static void RemoveThing(int id)
    {
        // Look the thing up **before** dropping the index entry, because the lookup is served from that index.
        //
        // Measured, and it cost a whole stage: the order used to be `frameIndex?.Remove(id); ... FindThing(id)?.Destroy()`
        // and `FindThing` is `frameIndex.TryGetValue`. Removing first guaranteed the lookup returned null, so `Destroy`
        // was never called and every removal the host sent was received, counted, and then silently discarded. The
        // player saw it as "the wall was built on the host but a blueprint is still drawn here": the replica kept the
        // blueprint forever, and the info panel showed `剩余工作量 2 / 原木 0/5` for an object the host had already
        // turned into a finished wall.
        var thing = FindThing(id);
        frameIndex?.Remove(id);
        Forget(id);
        // The `Destroyed` guard is not politeness. Two paths legitimately reach the same object: the host sends a
        // removal for the frame, and this peer already destroyed it inside `GenSpawn.Spawn` -> `WipeExistingThings`
        // when the finished wall was created on the same cell. Vanilla's `Thing.Destroy` answers a second call with
        // `Log.Error("Tried to destroy already-destroyed thing ...")`, so without this guard every completed wall filled
        // the player's debug log with red lines for a sequence that is exactly what is supposed to happen.
        if (thing is null || thing.Destroyed) return;
        try { thing.Destroy(DestroyMode.Vanish); }
        catch (Exception error) { if (destroyFailures++ < 4) Log.Warning("[TurboTick] 副本销毁失败：" + error.Message); }
    }

    private static int destroyFailures;

    /// <summary>Recreates an object the host created after the snapshot. The definition is looked up by
    /// defName, so a modded building or item arrives as long as both sides run the same mod set.</summary>
    private static void CreateThing(ReplicaCreation state)
    {
        if (FindThing(state.Id) is not null) return; // Already present; a repeated creation is harmless.
        Map? map = null;
        foreach (var candidate in Find.Maps)
            if (candidate.uniqueID == state.Map) { map = candidate; break; }
        if (map is null) return;
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(state.Def);
        if (def is null)
        {
            if (MissingDefs++ < 8) Log.Warning("[TurboTick] 副本缺少 Def：" + state.Def);
            return;
        }
        try
        {
            // MakeThing 会消耗一个本地 id；这是「从帧里实例化」的正常代价，不是野路子分配。
            // 记账下来，自检比较时扣掉，否则日志里会一直显示「多分配」而其实没有异常。
            var idBeforeMake = SharedActions.NextThingId();
            var thing = MakeFromFrame(def);
            var madeIds = SharedActions.NextThingId() - idBeforeMake;
            if (madeIds > 0) expectedAllocations += madeIds;
            // `MakeThing` drew an id from this peer's own counter. Identity here comes from the host and from
            // nowhere else: keeping the local id would make the object invisible to every later frame, which
            // addresses it as `state.Id`, and would leave this counter offset. `ApplyPreservingIdentity` below
            // restores whatever id the object carries, so it must already be the host's.
            if (thing.thingIDNumber != state.Id) thing.thingIDNumber = state.Id;
            ReplicaBlueprintDefGuard.Note(thing);
            if (def.stackLimit > 1 && state.Stack > 1) thing.stackCount = state.Stack;
            var cell = new IntVec3(state.X, 0, state.Z);
            var rotation = new Rot4(state.Rotation);
            if (!cell.InBounds(map)) return;
            // A corpse cannot be spawned empty. `Corpse.Bugged` is true while `innerContainer` is empty, and
            // `Corpse.SpawnSetup` logs "spawned in bugged state." and returns *before* `base.SpawnSetup`, so the
            // object never reaches the grid - measured as 36 such errors plus 8 "新建后未落地" and a destroy
            // retry every frame in one Player.log. The contents ride in the fragment as a reference list, so the
            // fragment must be applied before the spawn, not after. An unspawned thing makes that cheap: the
            // load has nothing to despawn and nothing to put back (see ScribeBridge.Load's wasSpawned branch).
            // Anything whose rendering or ticking dereferences state carried by its own fragment has to be loaded
            // *before* it is spawned. A corpse is the case that was already handled (`Corpse.Bugged` refuses to
            // spawn at all), and a `MinifiedThing` is the second, measured on the player's own colony:
            //
            //   ERROR Exception printing MinifiedThing6912 at (126, 0, 122): System.NullReferenceException
            //     at RimWorld.MinifiedThing.LoadCrateFrontGraphic()
            //     at RimWorld.MinifiedThing.Print(SectionLayer layer)
            //
            // `MinifiedThing.Graphic` dereferences `InnerThing`, which is `innerContainer[0]` - a field filled only by
            // the fragment (`ThingOwner.ExposeData` → `Scribe_Collections.Look(ref innerList, …)`). Spawning first
            // therefore handed `Map.MapUpdate` → `SectionLayer_ThingsGeneral.TakePrintFrom` an object with an empty
            // container, and the same object then failed our own `RefreshResourceCounts` through
            // `MinifyUtility.GetInnerIfMinified`. Both warnings were one cause: the fragment arrived one step late.
            //
            // Spawn status is what decides this, not the type: an unspawned thing has nothing to despawn and nothing
            // to put back, so loading it first costs nothing (see ScribeBridge.Load's wasSpawned branch). The
            // forbidden flag is still applied after the spawn, because that is what builds the overlay handle.
            var loadBeforeSpawn = thing is Corpse || thing is MinifiedThing;
            if (loadBeforeSpawn && state.State is { Length: > 0 } earlyXml && thing is IExposable earlyExposable)
            {
                // The carried things only exist as reference ids, and these pawns/buildings are created after the
                // snapshot: the receiver has never seen them, so its directory cannot resolve them and ThingOwner
                // prunes the entry. They arrive with the container for exactly that reason - rebuild them unspawned,
                // register each one so the reference resolves, then let the load put it inside.
                CreateContents(state.Contents);
                ApplyPreservingIdentity(thing, earlyExposable, earlyXml);
            }
            // Never WipeMode.Vanish here: the host may legitimately have two things in one cell, and
            // destroying the receiver's copy to make room would silently desync the colony.
            GenSpawn.Spawn(thing, cell, map, rotation, WipeMode.VanishOrMoveAside);
            if (!thing.Spawned)
            {
                if (RespawnFailures++ < 8)
                    Log.Warning($"[TurboTick] 新建后未落地 {state.Def} id={state.Id} -> {cell}");
                thing.Destroy(DestroyMode.Vanish);
                return;
            }
            if (!loadBeforeSpawn && state.State is { Length: > 0 } xml && thing is IExposable exposable)
                ApplyPreservingIdentity(thing, exposable, xml);
            // A fresh object has no overlay yet (`PostSpawnSetup` built one from the empty default), so the value
            // must be forced through even when it agrees with `state.Forbidden`.
            ApplyForbidden(thing, state.Forbidden, reloaded: true);
            // The frame index was built before this object existed; without this the same id would not be
            // found later in the frame and a duplicate would be created from the next referencing record.
            frameIndex?[thing.thingIDNumber] = thing;
            createdIds.Add(thing.thingIDNumber);
            Created++;
        }
        catch (Exception error)
        {
            if (CreateFailures++ < 8)
                Log.Warning("[TurboTick] 副本创建失败 " + state.Def + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void ResetCounters()
    {
        StatesApplied = 0;
        StatesFailed = 0;
        Created = 0;
        CreateFailures = 0;
        MissingDefs = 0;
        skippedCommands = 0;
        idAlignments = 0;
        idAheadEvents = 0;
        worstIdAhead = 0;
        idCheckFailures = 0;
        weatherChanges = 0;
        applyThingMissCreated = 0;
        applyThingMissNever = 0;
        createdIds.Clear();
    }

    /// <summary>Weather is decided in <c>WeatherDecider.WeatherDeciderTick</c>, which runs inside a tick and is
    /// therefore frozen on a replica; nothing in the frame carried it, so a replica kept whatever weather the
    /// checkpoint was saved under.
    ///
    /// The definition alone would not be enough. <c>TransitionLerpFactor</c> is <c>curWeatherAge / 4000</c>, and that
    /// counter is advanced in <c>WeatherManagerTick</c> - also a tick. A replica that only learned the new definition
    /// would blend from the old weather forever and show the old rain rate, so the host's age and its previous
    /// weather travel with it and are adopted verbatim.</summary>
    private static void ApplyWeather(ReplicaWeather state)
    {
        var def = DefDatabase<WeatherDef>.GetNamedSilentFail(state.Def);
        var previous = state.Previous is { Length: > 0 } name ? DefDatabase<WeatherDef>.GetNamedSilentFail(name) : null;
        Map? map = null;
        foreach (var candidate in Find.Maps)
            if (candidate.uniqueID == state.Map) { map = candidate; break; }
        var manager = map?.weatherManager;
        if (def is null || manager is null) return;
        if (manager.curWeather != def)
        {
            // Vanilla's own entry point: it keeps the old definition, ends its overlays and tells the handler. It also
            // zeroes `curWeatherAge`, which is overwritten below with the host's value.
            manager.TransitionTo(def);
            weatherChanges++;
            if (weatherChanges <= 8)
                Log.Message($"[TurboTick] 副本跟随天气：{manager.lastWeather?.defName ?? "-"} -> {def.defName}");
        }
        if (previous is not null && manager.lastWeather != previous) manager.lastWeather = previous;
        if (manager.curWeatherAge != state.Age) manager.curWeatherAge = state.Age;
    }

    /// <summary>Presentation probe, for the same reason <see cref="EngineTicks"/> exists: the two things this fix
    /// promises - a calendar that advances and a sky that recomputes - are otherwise invisible in a headless run.
    /// A frozen `日历tick` or a constant `天光` across samples is the failure signature.</summary>
    private static string PresentationProbe()
    {
        var clock = Find.TickManager;
        // TicksAbs is the getter this peer patches to the host clock; reading it here is what the renderer reads.
        var abs = clock is null ? -1 : clock.TicksAbs;
        // Every map, not just the current one. The blueprint count is here because "the frame was sent" and "this peer is
        // holding the object" are two different claims, and the defect that made blueprints invisible was fixed in the
        // sending filter - this number is the only one on this side that can contradict a sender-side count of zero. A
        // per-map breakdown is necessary rather than tidy: with two colonies, one map's zero and another's 117 average out
        // to a number that describes neither, and the current map is whichever one the observer happens to be looking at.
        var maps = Find.Maps;
        if (maps is null || maps.Count == 0) return $"日历tick={abs} 无地图";
        var blueprints = 0;
        var perMap = new System.Text.StringBuilder();
        foreach (var map in maps)
        {
            if (map?.listerThings is null) continue;
            var here = 0;
            foreach (var thing in map.listerThings.AllThings)
                if (thing is Blueprint) here++;
            blueprints += here;
            perMap.Append(perMap.Length > 0 ? "，" : "").Append('图').Append(map.Index).Append('=').Append(here);
        }
        var current = Find.CurrentMap;
        var glow = current?.skyManager is null ? -1f : current.skyManager.CurSkyGlow;
        return $"日历tick={abs} 天光={glow:0.000}"
            + $" 天气={current?.weatherManager?.curWeather?.defName ?? "-"}/age={current?.weatherManager?.curWeatherAge ?? -1}"
            + $" 本端蓝图 {blueprints} 个（{perMap}）"
            + (maps.Count > 1 ? $"；共 {maps.Count} 张地图，天光/天气只报当前图 {current?.Index.ToString(CultureInfo.InvariantCulture) ?? "无"}" : "");
    }

    /// <summary>Self-check for the one divergence a replica cannot see any other way.
    ///
    /// A replica never sends a state hash (`ReportHash` returns early while frozen), so an id counter that drifts
    /// from the host is silent by construction: the colony looks identical, the objects look identical, and a
    /// later id-based command (`cancel-thing`, `forbid`, a job target) quietly addresses a different object here
    /// than on the host. The host therefore reports its own `nextThingID` every frame, and this compares - then
    /// repairs, because on a replica the counter is a copy: the host is the only allocator.
    ///
    /// Repairing rather than only logging is deliberate. Logging alone leaves the replica holding a counter that
    /// hands out ids the host already used, and the damage appears much later in an unrelated command.</summary>
    private static void AlignIdentityCounter(int authoritativeNext)
    {
        if (authoritativeNext <= 0) return;
        var local = SharedActions.NextThingId();
        if (local <= 0 || local == authoritativeNext) return;
        idAlignments++;
        // Only one direction is a defect. Ahead means this peer allocated an identity the host has not issued, so a
        // later id-based command can hit a different object here; behind is ordinary bookkeeping - the host creates
        // pawns, motes and projectiles that the frame does not enumerate, so its counter legitimately runs ahead.
        // 扣掉「从帧里实例化物体」造成的分配：那是设计内的，不是异常。
        var ahead = local - authoritativeNext - expectedAllocations;
        if (ahead <= 0) expectedAllocations = 0;
        else expectedAllocations -= Math.Max(0, local - authoritativeNext);
        if (ahead > 0)
        {
            if (ahead > worstIdAhead) worstIdAhead = ahead;
            if (idAheadEvents++ < 8)
                Log.Warning($"[TurboTick] 副本多分配了 {ahead} 个物体 id：本地 nextThingID={local}，权威={authoritativeNext}"
                    + "（仍有路径在副本上创建物体，id 已对齐回权威值） 调用栈：" + IdAheadTrace() + "");
        }
        else if (idAlignments <= 8)
            Log.Message($"[TurboTick] 副本 id 计数落后 {-ahead}（权威端创建了本端不跟踪的对象），已对齐到 {authoritativeNext}");
        if (!SharedActions.SetNextThingId(authoritativeNext) && idCheckFailures++ < 8)
            Log.Warning("[TurboTick] id 计数自检失败：无法写回 UniqueIDsManager.nextThingID");
    }

    /// <summary>How much of the frame's job information actually landed on the replica.
    ///
    /// A replica that shows the right position but no activity is the difference between "it works" and "it looks like
    /// the game", and that difference is invisible unless it is counted. `toil` is the host's current-step index, so a
    /// count well below the job count means the pawn is displaying the wrong half of an action.</summary>
    private static System.Reflection.FieldInfo? factionRelationsField;

    /// <summary>How many factions this peer holds, and how many of them cannot answer <c>RelationWith</c>.
    ///
    /// <para>A faction with an empty <c>relations</c> list is not harmless: `Faction.RelationWith` logs an ERROR every
    /// time it is asked (<c>RimWorld/Faction.cs:503</c>) and returns a dummy, and `AttackTargetsCache.RegisterTarget`
    /// asks it once per faction per spawned thing (<c>Verse.AI/AttackTargetsCache.cs:181</c>). One such faction
    /// therefore turns every spawn into 22 ERROR lines - measured as 7256 lines in one client log, all reading
    /// `Faction  has null relation with …`, with the faction's own name empty because it has no def.</para>
    ///
    /// <para>Reporting the count rather than "healthy" is deliberate: a replica that is one faction short still looks
    /// fine on screen, and the only way to notice is the number disagreeing with the authority's.</para></summary>
    private static string FactionHealth()
    {
        try
        {
            var all = Find.FactionManager?.AllFactionsListForReading;
            if (all is null) return "阵营=无管理器";
            factionRelationsField ??= AccessTools.Field(typeof(RimWorld.Faction), "relations");
            var empty = 0;
            var nameless = 0;
            for (var i = 0; i < all.Count; i++)
            {
                var faction = all[i];
                if (faction is null) { empty++; continue; }
                var relations = factionRelationsField?.GetValue(faction) as System.Collections.ICollection;
                if (relations is { Count: 0 }) empty++;
                if (faction.def is null) nameless++;
            }
            return $"阵营={all.Count}(无关系{empty}/无def{nameless})";
        }
        catch (Exception error) { return "阵营=读取失败:" + error.GetType().Name; }
    }

    private static string ActivitySummary(AuthoritativeFrame frame)    {
        try
        {
            // 权威端发来的 toil 序号，按 thingIDNumber 索引。
            var hostToil = new Dictionary<int, int>();
            var hostThoughts = new Dictionary<int, string>();
            var thoughtsMatch = 0;
            var thoughtsDiffer = 0;
            var hostHealth = new Dictionary<int, float>();
            var lowestHostHealth = 1f;
            foreach (var state in frame.Pawns)
            {
                if (state.Thoughts is { Length: > 0 })
                    hostThoughts[state.Id] = state.Thoughts;
                if (state.Health < 0f) continue;
                hostHealth[state.Id] = state.Health;
                replicaHealth[state.Id] = state.Health;
                if (state.Health < lowestHostHealth) lowestHostHealth = state.Health;
            }
            var healthMatch = 0;
            var healthDiffer = 0;
            var hostNeeds = new Dictionary<int, float>();
            // 上一次观察到的权威值：用来断言「这个数确实在变」。不变的话，一致与否毫无意义。
            var moved = 0;
            var movedAndMatched = 0;
            foreach (var state in frame.Pawns)
            {
                if (state.Mood < 0f) continue;
                hostNeeds[state.Id] = state.Mood;
                if (observedMood.TryGetValue(state.Id, out var before) && Math.Abs(before - state.Mood) > 0.0005f) moved++;
                observedMood[state.Id] = state.Mood;
            }
            foreach (var state in frame.Pawns)
                if (state.Toil >= 0) hostToil[state.Id] = state.Toil;

            var total = 0;
            var withJob = 0;
            var same = 0;
            var behind = 0;
            var ahead = 0;
            var unknown = 0;
            // 本端连 driver 都没有，是「没同步」而不是「落后」。混在一起时 `落后 15` 既可以表示
            // 同步做错了，也可以表示同步压根没跑，两种情况的修法完全不同。
            var noDriver = 0;
            var busy = 0;      // toil > 0，说明真的在做事，这一轮对比才有意义
            var needsMatch = 0;
            var needsDiffer = 0;
            var maxToil = 0;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    total++;
                    if (pawn.CurJobDef is not null) withJob++;
                    var local = pawn.jobs?.curDriver?.CurToilIndex ?? -1;
                    if (hostHealth.TryGetValue(pawn.thingIDNumber, out var wantHealth))
                    {
                        var gotHealth = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
                        if (Math.Abs(gotHealth - wantHealth) <= 0.01f) healthMatch++;
                        else healthDiffer++;
                    }
                    if (hostThoughts.TryGetValue(pawn.thingIDNumber, out var wantThoughts))
                    {
                        // 比内容不比条数：条数对上但内容不同，面板依然是错的。
                        var memoriesNow = pawn.needs?.mood?.thoughts?.memories?.Memories;
                        var gotText = memoriesNow is null || memoriesNow.Count == 0
                            ? ""
                            : string.Join(Convert.ToChar(59).ToString(),
                                memoriesNow.Select(m => (m?.def?.defName ?? "?") + "#" + (m?.CurStageIndex ?? -1)));
                        if (gotText == wantThoughts) thoughtsMatch++;
                        else thoughtsDiffer++;
                    }
                    if (hostNeeds.TryGetValue(pawn.thingIDNumber, out var want))
                    {
                        var got = pawn.needs?.mood?.CurLevel ?? -1f;
                        if (want < 0f) { }
                        else if (Math.Abs(got - want) <= 0.01f) { needsMatch++; if (moved > 0) movedAndMatched++; }
                        else needsDiffer++;
                    }
                    if (!hostToil.TryGetValue(pawn.thingIDNumber, out var host)) { unknown++; continue; }
                    if (host > 0) busy++;
                    if (host > maxToil) maxToil = host;
                    if (local < 0)
                    {
                        noDriver++;
                        behind++;
                    }
                    else if (local == host) same++;
                    else if (local < host) behind++;
                    else ahead++;
                }
            // 按 job 类型分类，而不是看 toil：Goto/Wait/Wander 全程 toil 都是 0，用「toil > 0」当
            // 「在干活」的代理会把「正在走」全算成「没干活」，而正在走占了绝大部分时间。
            var idle = 0;
            var work = 0;
            var top = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    // 只看殖民者：mapPawns 里还有动物，动物永远在 Wander，会把比例冲淡到看不出人。
                    if (!pawn.IsColonist) continue;
                    var name = pawn.CurJobDef?.defName;
                    if (name is null) { idle++; continue; }
                    if (name.StartsWith("Wait", StringComparison.Ordinal)
                        || name is "Goto" or "GotoWander" or "Wander" or "GotoWanderFlee")
                    { idle++; continue; }
                    work++;
                    top[name] = top.TryGetValue(name, out var n) ? n + 1 : 1;
                }
            var summary = string.Join(",", top.OrderByDescending(pair => pair.Value).Take(3)
                .Select(pair => pair.Key + "=" + pair.Value));
            return $"有job的副本 {withJob}/{total}，工作类 {work}/空闲类 {idle}（{summary}），"
                 + $"toil 一致 {same}/落后 {behind}（其中本端没有driver {noDriver}）/超前 {ahead}/未知 {unknown}，"
                 + $"工作同步 {jobSyncs} 次/失败 {jobSyncFailures} 次/清空 {jobsCleared} 次，"
                 + $"配方接线 {billLinks} 次/找不到 {billMisses} 次，队列目标 {queueTargetsPushed} 个，"
                 + $"清掉掉队角色 {pawnsReconciled} 个/放回 {pawnsRestored} 个/失败 {pawnReconcileFailures} 次/跳过 {reconcileSkips} 次，"
                 + $"toil 推送 {toilForced} 次/失败 {toilForceFailures} 次/截断 {toilClamped} 次/起步失败 {startFailures} 次，累计保留权威工作 {jobsKept} 次，"
                 + $"想法 一致 {thoughtsMatch}/不一致 {thoughtsDiffer}，健康 一致 {healthMatch}/不一致 {healthDiffer}（权威最低 {lowestHostHealth:0.000}），需求 一致 {needsMatch}/不一致 {needsDiffer}，权威值变动 {moved}（其中一致 {movedAndMatched}），最高 toil {maxToil}，"
                 // The field that decides the drawn rotation. A non-zero count proves the replica's postures were
                 // actually stale - without it, "everyone is drawn head-down" and "the posture field was wrong" are
                 // two unrelated claims and only the first one is visible.
                 + $"姿势纠正 {posturesApplied} 次，"
                 // Zone count is the only readable proof that the zone channel landed: a missing zone draws nothing
                 // and logs nothing, so without this number "the overlay is off" and "the zone never arrived" look
                 // identical - which is exactly how a stockpile with a correct info card and no colour went unnoticed.
                 + ZoneSync.Summary() + "，"
                 // The capture counters belong on the client's own diagnostic line, not only on the authority's.
                 // `请求0/重放0` on the authority means "nothing arrived", which is indistinguishable from "the patch
                 // never bound" - and those two have opposite fixes. `gestures=0` here means the patch did not run
                 // (wrong target, or the player's click never reached `DesignateSingleCell`); `gestures>0 commands=0`
                 // means it ran and produced nothing; `commands>0` with `请求0` on the authority means the wire lost it.
                 + DesignatorSync.CaptureCounters() + "，"
                 // The faction scan is here for one measured symptom: 7256 lines of
                 // `Faction  has null relation with X. Returning dummy relation.` in a client log while the same log on
                 // the authority had zero. `AttackTargetsCache.RegisterTarget` asks `thing.HostileTo(faction)` for every
                 // faction on every spawn (`Verse.AI/AttackTargetsCache.cs:181`), so one faction with an empty
                 // `relations` list turns every spawn into 22 ERROR lines. The name in the message is empty because
                 // `Faction.Name` falls back to `def.LabelCap` and this faction has no def - so the message cannot
                 // identify it and this count is the only place it shows up.
                 + FactionHealth() + "，";
        }
        catch (Exception error) { return "活动统计失败：" + error.Message + "，"; }
    }

    /// <summary>Keeps a replica's pawn in the faction the authority says, without running `Pawn.SetFaction`.
    ///
    /// <para><b>Why a separate path.</b> `Pawn.SetFaction` (`:129460-129520`) is a transition, not an assignment: it
    /// calls `ChangeKind`, `PawnComponentsUtility.AddAndRemoveDynamicComponents`, `workSettings.EnableAndInitialize()`
    /// (which RESETS every priority to the default six), `ReachabilityUtility.ClearCacheFor`, `health.surgeryBills.Clear()`,
    /// `Map.mapPawns.RegisterPawn`, `playerSettings.ResetMedicalCare`, `ClearMind_NewTemp` and a mood-thought invalidation.
    /// On a display replica every one of those would fire again on a value the authority already computed - a recruit
    /// would lose the work priorities the player set, and a prisoner whose faction flipped would have its medical care
    /// reset. The only thing that actually has to change here is the field and the per-faction index.</para>
    ///
    /// <para><b>What the index costs.</b> <c>MapPawns</c> caches `<c>pawnsInFactionSpawned</c>`, a
    /// `FactionPawnList` per faction, filled in `RegisterPawn` (`:76011-76040`) and cleared in `DeRegisterPawn`
    /// (`:76065-76079`). `UpdateRegistryForPawn` (`:76083-76089`) is exactly "move this pawn to the right list": it
    /// deregisters, re-registers and fires the list-changed notifications. Without it, "how many colonists does this
    /// faction have", the colonist bar and every `PawnsOfFaction` query answer from a stale index - which is what the
    /// replica does today for every pawn whose faction ever changed.</para>
    ///
    /// <para><b>Why the write goes through `SetFactionDirect`.</b> <c>Thing.factionInt</c> is `protected` (`:156168`),
    /// and `SetFactionDirect` (`:157883-157893`) exists as the plain assignment beside the transition (`:157895`). It is
    /// what the game itself uses when it wants the field without the transition.</para></summary>
    /// <summary>Mirrors `Pawn_HealthTracker.healthState`.
    ///
    /// <para><b>What a stale value costs.</b> `CharacterCardUtility.DrawCharacterCard` (`:642497`) wraps the entire
    /// action row - banish, the romance/recruit buttons, every colonist interaction - in `else if (!pawn.health.Dead)`.
    /// A pawn who died on the authority after the save keeps that row here, offering actions the authority would
    /// refuse; `ShowBanishPawnConfirmationDialog`'s own precondition reads `pawn.Downed` (`:642510`), so the downed
    /// case is stale in the same way.</para>
    ///
    /// <para><b>Why writing this field is safe and writing a hediff would not.</b> The class note on
    /// <see cref="ReplicaHealthPatch"/> already decided that: injecting injuries would change work speed, moving and
    /// medical judgement - a display problem turned into a simulation problem. `healthState` is only a cached summary
    /// of a judgement the authority already made, so copying it costs nothing and invents nothing.</para>
    ///
    /// <para><b><c>-1</c> means "this peer did not send it".</b> An older host's row decodes with the default, and
    /// writing 0 (Dead) would kill every pawn on a replica talking to an older authority. Same reason
    /// `ApplyPawnFaction` refuses to write `null` for an unresolved faction id.</para></summary>
    private static void ApplyPawnHealthState(Pawn pawn, int healthState)
    {
        if (healthState < 0 || pawn.health is null) return;
        try
        {
            // `State` has no setter (`:136075` is a getter over the private `healthState`), so the field is written.
            var field = AccessTools.Field(typeof(Pawn_HealthTracker), "healthState");
            if (field is null) { healthStateFailures++; return; }
            if (field.GetValue(pawn.health) is PawnHealthState current && (byte)current == (byte)healthState) return;
            field.SetValue(pawn.health, (PawnHealthState)(byte)healthState);
            healthStatesApplied++;
            // `Pawn_HealthTracker.CanCrawl` (`:126995`) reads `Downed`, and `Pawn.Crawling` combines it with the current
            // job - so a posture the renderer picked from a stale `Downed` would otherwise stay until the next frame
            // that happens to resend posture, which is not every frame.
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
        }
        catch (Exception error)
        {
            if (healthStateFailures++ < 6)
                Log.Warning("[TurboTick] 健康状态应用失败 " + pawn.LabelShortCap + " → " + healthState + "："
                    + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Writes one pawn's health state without any frame, for `HealthStateProbe`. Same body as the frame path,
    /// so a probe that passes proves the code the frame uses rather than a second copy of it.</summary>
    internal static void ForceHealthState(Pawn pawn, int healthState) => ApplyPawnHealthState(pawn, healthState);

    internal static long healthStatesApplied;
    internal static int healthStateFailures;

    private static void ApplyPawnFaction(Pawn pawn, int factionLoadId, ref long applied, ref long failures)
    {
        try
        {
            var wanted = factionLoadId < 0 ? null : FactionOf(factionLoadId);
            // A faction id we cannot resolve is not "no faction": writing null would strip a colonist out of the colony
            // because a faction was not in this peer's world list yet. Counted and skipped.
            if (factionLoadId >= 0 && wanted is null) { failures++; return; }
            if (pawn.Faction == wanted) return;
            pawn.SetFactionDirect(wanted);
            // **The tracker set is a function of the faction, and nothing else here recomputes it.**
            // `Pawn.SetFaction` (`:129494`) calls `PawnComponentsUtility.AddAndRemoveDynamicComponents`, and
            // `SetFactionDirect` (`:157883`) does not - which is exactly why it was chosen (the full setter also changes
            // the kind, fires `GainedColonist`, enables work settings and clears mental state, none of which a display
            // replica simulated). But the *structure* is not a side effect: `flag = Faction.IsPlayer` decides whether
            // `foodRestriction`/`playerSettings` exist (`:445388`, `:445460`), `HostFaction.IsPlayer` decides the same
            // for a prisoner, and `drafter` needs `Spawned`.
            //
            // **It does not wipe the contents.** Six of the trackers it can create -
            // `foodRestriction`, `outfits`, `drugs`, `timetable`, `reading`, `inventoryStock` (`:445388-445412`) - are
            // created only when null, so a tracker that already holds a policy is left alone. `training` (`:445464`) is
            // the one that is reassigned unconditionally, but only when it is null too (`pawn.trainin` guard), and its
            // contents are in `PawnTrackerSync`'s `w:` rows. The earlier note on this entry assumed every branch
            // replaced the tracker; reading the bodies shows only `trader`/`mechanitor`/`learning`/`drafter` are ever
            // set back to null, and those are exactly the ones the probe reports as absent on this save.
            try { PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, actAsIfSpawned: pawn.Spawned); }
            catch (Exception) { }
            if (pawn.Spawned)
            {
                // Two caches index a pawn by faction, and both answer questions the UI asks every frame.
                // `MapPawns.UpdateRegistryForPawn` (`:76083`) moves it between the per-faction lists; the attack-target
                // cache (`:263576-263586`) keeps its own registration because `GetPotentialTargetsFor` reads
                // `thing.Faction` to decide who is hostile to whom. Updating only one leaves the other stale, and both
                // are cheap (they no-op unless the pawn is already registered).
                pawn.Map?.mapPawns?.UpdateRegistryForPawn(pawn);
                pawn.Map?.attackTargetsCache?.UpdateTarget(pawn);
            }
            applied++;
        }
        catch (Exception error)
        {
            failures++;
            if (failures <= 6)
                Log.Warning("[TurboTick] 派系应用失败 " + pawn.LabelShortCap + " → " + factionLoadId
                    + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>How many pawns have had their faction written, and how many times a write failed. Counted rather than
    /// logged per pawn: a colony of seventy would produce seventy lines for one systemic failure.</summary>
    internal static long factionsApplied;
    internal static long factionApplyFailures;

    private static Faction? FactionOf(int loadId)
    {
        var all = Find.FactionManager?.AllFactionsListForReading;
        if (all is null) return null;
        for (var i = 0; i < all.Count; i++)
            if (all[i] is { } faction && faction.loadID == loadId) return faction;
        return null;
    }

    private static void ApplyPawn(ReplicaPawn state)
    {
        // A pawn this peer parked (see `RespawnParked`) is not in `AllPawnsSpawned`, so it must be put back
        // before the lookup or it would stay invisible while the host lists it every frame.
        RespawnParked(state.Id, state.Map, new Vector3(state.X, 0f, state.Z));
        var pawn = FindPawn(state.Id, state.Map);
        // Not on the map the host named. Two different situations produce this and they need opposite answers:
        //   - the pawn is not spawned anywhere here (still loading, or parked): nothing to move yet, wait for a frame;
        //   - the pawn IS spawned, on a different map: it changed maps on the host (caravan, gravship, teleport,
        //     transporter) and a replica has no code that would ever move it. `FindPawn` returns null for it and the
        //     old version returned right there, so the pawn was drawn on the old map forever while the host had it
        //     somewhere else - and in a multi-settlement colony that is exactly "I cannot see my colonists in the other
        //     base".
        pawn ??= RelocateAcrossMaps(state);
        if (pawn is null || !pawn.Spawned) return;
        // Faction first: everything below reads `pawn.Faction` indirectly (the colonist bar, `IsColonistPlayerControlled`,
        // the per-faction index `MapPawns` keeps), and a pawn counted under the wrong faction while its position moves
        // is a worse reading than the same pawn five centimetres off.
        ApplyPawnFaction(pawn, state.FactionLoadId, ref factionsApplied, ref factionApplyFailures);
        // Health state next to the faction, and for the same reason: it is a property of who this pawn *is*, and two
        // readers gate the whole UI on it. `Pawn.Dead` is `health.Dead` (`:127019`) and `Pawn.Downed` is
        // `health.Downed` (`:126989`); both read `healthState` (`:136077`, `:136079`), which nothing on this peer ever
        // writes because `HealthTrackerTick` - the only caller of `CheckStateChange` (`:136206`) - never runs here.
        ApplyPawnHealthState(pawn, state.HealthState);
        var target = new Vector3(state.X, 0f, state.Z);
        if (Predictive)
        {
            // Two simulations can only be compared at the same tick. If the local clock has drifted away
            // from the host's, every position looks wrong even when both sides agree, so the offset is
            // measured separately from the actual distance.
            var localTick = Find.TickManager?.TicksGame ?? 0;
            var tickOffset = localTick - LastFrameTick;
            if (Math.Abs(tickOffset) > Math.Abs(worstTickOffset)) worstTickOffset = tickOffset;
            var drift = (pawn.DrawPos - target).MagnitudeHorizontal();
            driftSum += drift;
            driftSamples++;
            if (drift <= CorrectionRadius) return;
            corrections++;
            if (drift > worstDrift) worstDrift = drift;
        }
        var now = Clock.Now;
        if (motions.TryGetValue(state.Id, out var motion))
        {
            // Continue from wherever the portrait currently is, so a new frame never snaps backwards.
            motion.From = Sample(motion, now);
            motion.To = target;
            motion.Start = now;
        }
        else
        {
            motions[state.Id] = new Motion { From = target, To = target, Start = now };
        }
        // Still keep the integer cell in step for selection, targeting and culling.
        var cell = target.ToIntVec3();
        if (cell.InBounds(pawn.Map) && pawn.Position != cell) pawn.Position = cell;
        var rotation = new Rot4(state.Rotation);
        if (pawn.Rotation != rotation) pawn.Rotation = rotation;
        if (Predictive) AlignJob(pawn, state);
        // 显示副本没有本地 AI，也就永远不会自己开 job：不在这里把它补上，`curDriver` 一直是空的，
        // 下面的 ForceToil 每次都第一行返回，画面上的角色永远停在「没有工作」的姿态。
        // 实测四个副本 15 个角色：toil 一致 0 / 落后 15，而有 job 的只有 1 个。
        if (!Predictive) SyncJob(pawn, state);
        // 无条件对齐 toil：yaw 与动作姿态不该受 Predictive 开关影响，而副本本就不做预测。
        ForceToil(pawn, state.Toil);
        // 需求是玩家在选择面板上直接看到的数字。副本不 tick，它们不会自己变——不写回去就永远停在载入值。
        ApplyNeeds(pawn, state);
        ApplyThoughts(pawn, state);
        ApplyCarried(pawn, state);
        ApplyJobTag(pawn, state);
        ApplyPosture(pawn, state);
        // Skills. `Learn` is only called from work toils, which a replica never runs, so every colonist's skill page shows
        // the level the session started with and the work tab's "relevant skill" average never moves.
        SkillSync.Apply(pawn, state.Skills);
        // Every need the card would show. The three fixed numbers in `ApplyNeeds` cover a base-game colonist; joy, comfort,
        // beauty, drug desire and any DLC or mod need would otherwise sit at their load-time value forever.
        NeedsSync.Apply(pawn, state.Needs);
    }

    /// <summary>Mirrors the host's posture, which is what decides the drawn rotation of every pawn and animal.
    ///
    /// <para>`PawnRenderer.GetDrawParms` picks the body facing with
    /// <c>(posture == Standing) ? pawn.Rotation : LayingFacing()</c> (`Verse/PawnRenderer.cs:342`), and
    /// <c>BodyAngle()</c> returns <c>LayingFacing().AsAngle</c> for a non-standing humanlike - 0/90/270 degrees chosen
    /// by <c>thingIDNumber % 4</c>. So the wrong posture does not merely look like "lying down": it rotates the whole
    /// sprite, which is exactly the reported "characters and animals are all drawn head-down".</para>
    ///
    /// <para>The field never moves on a replica by itself. `JobDriver.SetInitialPosture()` is deliberately skipped by
    /// `SyncJob` (it forces `Standing`, and on the host the `LayDown` toil's initAction immediately overrides it - a
    /// replica runs no initActions, so calling it would make every sleeping colonist stand up), and the only other
    /// writer is <c>Pawn_PathFollower.StartPath</c> (`Verse.AI/Pawn_PathFollower.cs:196`), which a replica never runs
    /// because it never paths. The value therefore stays at whatever the save was loaded with: whoever was asleep at
    /// that moment is drawn lying down for the rest of the session, even while the frame shows them walking.</para>
    ///
    /// <para>Guarded on <c>-1</c> so an older host that does not send the field leaves the replica's own value alone
    /// instead of forcing every pawn flat.</para></summary>
    private static void ApplyPosture(Pawn pawn, ReplicaPawn state)
    {
        if (state.Posture < 0 || pawn.jobs is null) return;
        var posture = (PawnPosture)state.Posture;
        if (pawn.jobs.posture == posture) return;
        pawn.jobs.posture = posture;
        posturesApplied++;
    }

    private static int posturesApplied;

    /// <summary>Copies the host's `JobTag` so the idle alert stops lying.
    ///
    /// <para>`mindState.lastJobTag` is written in exactly one place - `Pawn_JobTracker.StartJob`
    /// (`Verse.AI/Pawn_JobTracker.cs:357`) - and a display replica never calls it: `SyncJob` assembles the `Job` by
    /// hand and assigns `curJob` directly, precisely to avoid the reservations `StartJob` would take. So the field
    /// keeps whatever value the save was loaded with.</para>
    ///
    /// <para>Measured: the save on `192.168.0.29` holds `lastJobTag = Idle` for all 78 pawns, and the player's own
    /// screenshot shows `3位殖民者无事可做` over three colonists visibly carrying wood. The alert reads
    /// `pawn.mindState.IsIdle` (`Verse.AI/Pawn_MindState.cs:232`, literally `lastJobTag == JobTag.Idle`), so the
    /// warning is not about what the pawn does, it is about a stale byte. `ThinkNode_ConditionalShouldFollowMaster`
    /// reads the same field.</para>
    ///
    /// <para>A pawn with no job is set to `Misc`, never `Idle`: the alert is *supposed* to fire when a colonist
    /// genuinely has nothing to do, and a replica that always says "busy" would hide that. The host decides what
    /// idle means; this only mirrors it.</para></summary>
    private static void ApplyJobTag(Pawn pawn, ReplicaPawn state)
    {
        if (state.JobTag < 0 || pawn.mindState is null) return;
        var tag = (JobTag)state.JobTag;
        if (pawn.mindState.lastJobTag == tag) return;
        pawn.mindState.lastJobTag = tag;
    }

    /// <summary>Shows what the host pawn is carrying.
    ///
    /// <para>`Pawn_CarryTracker.innerContainer` takes the stack off the map grid, so the replica never receives it as a
    /// thing - and a pawn carrying materials to a blueprint looked empty-handed from pickup to delivery. Measured as
    /// the player's own description: "小人走到料堆旁、蹲下、然后空手走到蓝图边，蓝图还是 0/5".</para>
    ///
    /// <para>The display object gets <c>thingIDNumber = -1</c> on purpose. `ThingMaker.MakeThing` draws a real id from
    /// `UniqueIDsManager`, and that id would then exist twice - once as this phantom, once when the host allocates the
    /// same number to something real - and <see cref="BuildFrameIndex"/> now scans carry trackers, so `FindThing`
    /// could hand back the phantom in place of the real object. A negative id cannot collide with anything the host
    /// allocates, and it is recreated whenever the definition or count changes, so nothing accumulates.</para>
    ///
    /// <para><b>`overrideGraphicIndex` is not decoration.</b> A negative id reaches vanilla's
    /// <c>Graphic_Random.SubGraphicFor</c>, which picks the variant with <c>thingIDNumber % subGraphics.Length</c> -
    /// and C#'s remainder keeps the sign, so <c>-1 % n == -1</c> and the lookup throws
    /// <c>IndexOutOfRangeException</c> every frame the item is carried. Measured 2026-11-16: 66
    /// `Exception drawing Jan-Hendrik` lines, all of them from `PawnRenderUtility.DrawCarriedThing` ->
    /// <c>Graphic_Random.DrawWorker</c> -> <c>SubGraphicFor</c>. Setting the index explicitly is the documented way
    /// to override that choice, so the phantom keeps its collision-free id and still draws.</para>
    ///
    /// <para>Never spawned: it lives only in the carry tracker, exactly like a carried stack on the authority.</para>
    /// </summary>
    private static void ApplyCarried(Pawn pawn, ReplicaPawn state)
    {
        var tracker = pawn.carryTracker;
        if (tracker?.innerContainer is null) return;
        var current = tracker.CarriedThing;
        var wantedDef = state.CarriedDef is { Length: > 0 } name ? DefDatabase<ThingDef>.GetNamedSilentFail(name) : null;
        var wantedCount = wantedDef is null ? 0 : Math.Max(1, state.CarriedCount);
        // Same definition and count is already on display: rebuilding it every frame would allocate a Thing per frame.
        if (wantedDef is not null && current?.def == wantedDef && current.stackCount == wantedCount) return;
        try
        {
            tracker.innerContainer.ClearAndDestroyContents(DestroyMode.Vanish);
            if (wantedDef is null) return;
            var shown = MakeFromFrame(wantedDef);
            shown.thingIDNumber = -1;
            // Keeps the phantom's variant selection off the negative id: Graphic_Random.SubGraphicFor indexes
            // `thingIDNumber % subGraphics.Length`, and -1 % n == -1 in C#. See the remarks above.
            shown.overrideGraphicIndex = 0;
            shown.stackCount = wantedCount;
            // `TryAdd(Thing, int)` returns the number actually accepted, and the count is already on the object.
            if (tracker.innerContainer.TryAdd(shown, wantedCount) <= 0) shown.Destroy(DestroyMode.Vanish);
        }
        catch (Exception error)
        {
            if (carryDisplayFailures++ < 4)
                Log.Warning("[TurboTick] 携带物显示失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static int carryDisplayFailures;

    /// <summary>A predictive peer that only matches positions still walks somewhere else, because the AI
    /// picked a different job. When the host's current job differs, the local pawn is told to do the same
    /// thing with the same targets, so the two simulations make the same decision instead of being fought
    /// apart by position correction.</summary>
    private static void AlignJob(Pawn pawn, ReplicaPawn state)
    {
        if (state.Job is not { Length: > 0 } jobName || pawn.jobs is null || pawn.Dead) return;
        if (pawn.CurJobDef?.defName == jobName) return;
        var def = DefDatabase<JobDef>.GetNamedSilentFail(jobName);
        if (def is null) return;
        var a = DecodeTarget(state.JobACell, state.JobAThing);
        if (!a.IsValid) return;
        var b = DecodeTarget(state.JobBCell, state.JobBThing);
        try
        {
            var job = b.IsValid ? JobMaker.MakeJob(def, a, b) : JobMaker.MakeJob(def, a);
            pawn.jobs.TryTakeOrderedJob(job);
            jobAlignments++;
        }
        catch (Exception error)
        {
            if (jobAlignmentFailures++ < 8)
                Log.Warning("[TurboTick] 工作对齐失败 " + jobName + "：" + error.GetType().Name);
        }
    }

    /// <summary>Gives the replica the host's current job, without letting it run any of the job's own logic.
    ///
    /// A display peer runs no AI at all, so it never starts a job by itself; the pawn slides along the projected
    /// path with `curDriver == null`, and every toil comparison reads as "behind". Measured on a four-replica run:
    /// `toil 一致 0/落后 15`, `有job的副本 1/16`.
    ///
    /// `StartJob` and `TryTakeOrderedJob` both call `TryMakePreToilReservations`, which reserves real map
    /// resources — on a replica that means fighting the host for the same stack and desyncing reservation state.
    /// The job is therefore assembled and installed by hand: `new Job{...}` (the parameterless constructor is the
    /// only one that does not draw a `loadID` from `UniqueIDsManager`, and job ids must never be allocated here),
    /// `Job.MakeDriver`, then the internal `SetupToils` by reflection. `curToilIndex` is left to `ForceToil`, and
    /// `ReadyForNextToil` is deliberately not called because it runs the toil's init action, which mutates the map.</summary>
    private static void SyncJob(Pawn pawn, ReplicaPawn state)
    {
        if (pawn.jobs is null || pawn.Dead) return;
        if (state.Job is not { Length: > 0 } jobName)
        {
            // The host finished its job. A replica runs no AI, so nothing would ever clear this one and the pawn
            // would keep playing the last animation forever - visibly wrong the moment a colonist stops working.
            // Dropped the same way the reference replica does it: fields only, never `EndCurrentJob`, because that
            // releases reservations and starts a Wait job, both of which are host-side effects.
            if (pawn.jobs.curJob is null && pawn.jobs.curDriver is null) return;
            pawn.jobs.ClearDriver();
            pawn.jobs.curJob = null;
            // 形状缓存跟着清：不清的话同一个 pawn 下一次接到的 job 名称与目标恰好与旧形状相同，
            // 会被误判成「已经是这个 job」而跳过重建，而本地 curJob 已经是 null。
            jobShapes.Remove(state.Id);
            billRetries.Remove(state.Id);
            jobsCleared++;
            return;
        }
        var a = DecodeTarget(state.JobACell, state.JobAThing);
        var b = DecodeTarget(state.JobBCell, state.JobBThing);
        // Same definition is not the same job: hauling a different stack is the same `HaulToCell` with a new
        // target, and testing only the definition name left the replica walking to the previous stack.
        // `count`, `bill` and the two target queues belong in the comparison for the same reason - a bill job
        // that moves on to its next queued ingredient is the same `DoBill` with a different head of queue, and
        // rebuilding it every frame would clear the driver for nothing.
        var shape = new JobShape(jobName, state.JobACell, state.JobAThing, state.JobBCell, state.JobBThing,
            state.JobCCell, state.JobCThing, state.JobCount, state.JobBill, state.JobQueueA, state.JobQueueB);
        if (pawn.CurJobDef?.defName == jobName && pawn.jobs.curDriver is not null
            && pawn.jobs.curJob is { } existing
            && existing.targetA == a && existing.targetB == b
            && jobShapes.TryGetValue(state.Id, out var previous) && previous == shape)
        {
            // 形状没变就早退，只有一个例外：配方还没接上。
            // 建筑是后生成的，它的「创建帧」可能比带着 `bill` 的工作帧晚到一帧；这一帧 `ResolveBill`
            // 找不到那栋楼，于是 `job.bill` 是 null，而形状相同又让下一帧直接早退——`配方接线 0 次 /
            // 找不到 N 次` 就永远不再重试，症状是「配方看板一直是泛化文字」，看不出原因。
            // 这里补一次重试；上限 8 次是为了让「权威端真的没有那条 bill」不至于每帧重建 driver。
            if (existing.bill is null && state.JobBill is not null && BillRetry(state.Id))
            {
                var retried = ResolveBill(existing.targetA, state.JobBill);
                if (retried is not null) { existing.bill = retried; billLinks++; }
            }
            return;
        }
        var def = DefDatabase<JobDef>.GetNamedSilentFail(jobName);
        if (def is null || def.driverClass is null) return;
        if (!a.IsValid) return;
        try
        {
            var job = new Job { def = def, targetA = a, targetB = b, targetC = DecodeTarget(state.JobCCell, state.JobCThing) };
            // `count` is -1 when the host did not send one, and -1 is also vanilla's "unset" value, so it is
            // assigned verbatim rather than skipped: a job that carries "carry three of these" must not read as
            // "carry whatever".
            job.count = state.JobCount;
            job.bill = ResolveBill(a, state.JobBill);
            if (job.bill is not null) billLinks++; else if (state.JobBill is not null) billMisses++;
            // Queues before the driver exists: `MakeNewToils` reads them (`DoBill.AnyIngredientsQueued`,
            // `JumpIfAlsoCollectingNextTargetInQueue`), so a queue filled afterwards would produce a toil list
            // that disagrees with the host's and every `ForceToil` on it would clamp.
            queueTargetsPushed += FillQueue(job, TargetIndex.A, state.JobQueueA);
            queueTargetsPushed += FillQueue(job, TargetIndex.B, state.JobQueueB);
            pawn.jobs.ClearDriver();
            pawn.jobs.curJob = job;
            var driver = job.MakeDriver(pawn);
            driver.pawn = pawn;
            driver.job = job;
            pawn.jobs.curDriver = driver;
            // `Notify_Starting()` before `SetupToils()`, which is the order vanilla uses
            // (`Pawn_JobTracker.StartJob`: SetInitialPosture -> Notify_Starting -> SetupToils -> ReadyForNextToil).
            // Skipping it left every driver field that `MakeNewToils` depends on at its default, and the failure
            // is silent and permanent: `JobDriver_LayDown` computes `canMoveOrCrawl` there (default false), so the
            // sleeping job produced ONE toil on the replica and TWO on the host - the host sits on toil 1, which
            // clamps to 0 forever. Measured on 5 replicas: `toil 一致 0/落后 43`, `toil 推送 0 次/失败 0 次/
            // 截断 12900 次`, with all 43 colonists asleep. The counters looked like "the host is ahead", but
            // nothing was ever pushed because the local list was shorter than the host's index.
            //
            // `SetInitialPosture()` is deliberately NOT mirrored: it forces `PawnPosture.Standing`, and on the
            // host the LayDown toil's initAction immediately overrides it. A replica runs no initActions, so
            // calling it here would make every sleeping colonist stand up.
            //
            // Wrapped on its own: a driver whose `Notify_Starting` throws must not cost us `SetupToils` too -
            // an unbuildable toil list is a visible failure (the pawn freezes on toil 0), while a missing
            // `canMoveOrCrawl`-style field only costs the toil *count*.
            try { driver.Notify_Starting(); }
            catch (Exception error)
            {
                if (startFailures++ < 8)
                    Log.Warning("[TurboTick] Notify_Starting 失败 " + jobName + "：" + error.GetType().Name + " " + error.Message);
            }
            setupToilsMethod ??= AccessTools.Method(typeof(JobDriver), "SetupToils");
            setupToilsMethod?.Invoke(driver, null);
            jobShapes[state.Id] = shape;
            billRetries.Remove(state.Id);
            jobSyncs++;
        }
        catch (Exception error)
        {
            if (jobSyncFailures++ < 8)
                Log.Warning("[TurboTick] 工作同步失败 " + jobName + "：" + error.GetType().Name + " " + error.Message);
        }
    }

    /// <summary>Everything about the host's job that a replica reproduces, in a shape that compares by value.
    ///
    /// Without the queues and the bill in here, `SyncJob` had to either rebuild on every frame (which clears the
    /// driver and the toil list for no reason) or never notice that the host moved on to the next ingredient -
    /// and "同一个 JobDef 换了队列首元素" is exactly what a multi-stack haul looks like from the outside.</summary>
    private readonly record struct JobShape(string Def, int ACell, int AThing, int BCell, int BThing,
        int CCell, int CThing, int Count, string? Bill, string? QueueA, string? QueueB);

    private static readonly Dictionary<int, JobShape> jobShapes = new();

    /// <summary>`bill` 的补接次数上限。见 `SyncJob` 里那段注释：建筑晚到一帧会让第一次接线失败，
    /// 事后形状相同又会早退，所以必须给「已同步但 bill 还是空」留一条重试路径。
    /// 8 次足够覆盖「物体创建帧晚到 / 同一帧内先应用 pawn 后应用 thing」这几种次序，
    /// 又不至于在权威端真的没有那条 bill 时每帧重建 driver。</summary>
    private static readonly Dictionary<int, int> billRetries = new();

    private static bool BillRetry(int pawnId)
    {
        billRetries.TryGetValue(pawnId, out var tries);
        // 240 帧（60 fps 下约 4 秒），不是 8：实测副本拿到 `job.bill` 的引用后报 `找不到 5–7 次` 就再也不试了，
        // 而那栋建筑的「创建帧」+ Scribe XML（billStack 就在里面）要等下一帧物体变化才到。8 帧的预算在
        // 帧率掉下来或者物体那帧被预算截断时必然耗尽，症状是「配方永远接不上，但计数看起来只试过几次」。
        if (tries >= 240) return false;
        billRetries[pawnId] = tries + 1;
        return true;
    }

    /// <summary>Finds the host's `Bill` on the target building. The bill list itself already arrived with the
    /// building's Scribe XML (`Building_WorkTable.ExposeData` scribes `billStack`), so this only has to point
    /// `Job.bill` at the right one; nothing is created here. `Bill.GetUniqueLoadID()` is `Bill_<recipe>_<loadID>`
    /// and is stable across peers because `loadID` is scribed.</summary>
    private static Bill? ResolveBill(LocalTargetInfo giver, string? billId)
    {
        if (billId is null) return null;
        if (giver.Thing is not IBillGiver billGiver)
        {
            // 「找不到」有两种完全不同的原因：目标物体还没到（`Thing` 是 null，只剩格子），或者物体到了但
            // `BillStack` 里没有这条 bill。两者的改法完全不同，而计数器只报一个数字——实测 `找不到 5–7 次`
            // 就是靠这行区分开的。
            if (billMissNotes++ < 3)
                Log.Warning($"[TurboTick] 配方找不到：目标不是 IBillGiver（thing={(giver.Thing?.def?.defName ?? "null")}，"
                    + $"cell={giver.Cell}，bill={billId}）");
            return null;
        }
        var stack = billGiver.BillStack;
        if (stack?.Bills is null)
        {
            if (billMissNotes++ < 3)
                Log.Warning($"[TurboTick] 配方找不到：{giver.Thing.def.defName} 没有 BillStack（bill={billId}）");
            return null;
        }
        foreach (var bill in stack.Bills)
            if (bill is not null && string.Equals(bill.GetUniqueLoadID(), billId, StringComparison.Ordinal))
                return bill;
        if (billMissNotes++ < 3)
            Log.Warning($"[TurboTick] 配方找不到：{giver.Thing.def.defName} 的 BillStack 有 {stack.Count} 条"
                + $"（{string.Join(",", stack.Bills.Select(b => b?.GetUniqueLoadID() ?? "null"))}），要找 {billId}");
        return null;
    }

    /// <summary>只打前几条诊断：这条路径每帧都会走，全量记录会把日志冲掉。</summary>
    private static int billMissNotes;

    /// <summary>Rebuilds one `Job.targetQueueA/B` from the host's `cell:thingId;...` encoding.
    ///
    /// A queued target whose thing has not arrived yet still resolves as a cell, which is what the host's own
    /// `LocalTargetInfo` would report before the object was spawned - and dropping it instead would shift every
    /// later entry, making the head of the queue (the one the job text shows) wrong rather than merely stale.</summary>
    private static int FillQueue(Job job, TargetIndex index, string? encoded)
    {
        if (encoded is not { Length: > 0 }) return 0;
        var queue = job.GetTargetQueue(index);
        var added = 0;
        foreach (var entry in encoded.Split(';'))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0) continue;
            if (!int.TryParse(entry.AsSpan(0, separator), out var cell)) continue;
            if (!int.TryParse(entry.AsSpan(separator + 1), out var thingId)) continue;
            var target = DecodeTarget(cell, thingId);
            if (!target.IsValid) continue;
            queue.Add(target);
            added++;
        }
        return added;
    }

    private static int jobSyncs;
    private static int jobSyncFailures;
    private static int jobsCleared;
    /// <summary>`Job.bill` 接上对象计数 / 未接上计数。分开记的原因是：`JobBill` 字段到达但本端建筑里找不到
    /// 那一条（loadID 对不上），与权威端根本没发，是两种不同的故障，而「配方看板还是空的」这个症状一样。</summary>
    private static int billLinks;
    private static int billMisses;
    /// <summary>本端实际写进 `targetQueueA/B` 的条目数。它是「队列真的到了」的唯一正面证据：
    /// 只统计同步次数的话，队列全空与队列全到会读出同一个数。</summary>
    private static int queueTargetsPushed;
    private static System.Reflection.MethodInfo? setupToilsMethod;

    /// <summary>True on a peer that only displays the host's state. Used to keep a host job alive when its toils
    /// cannot be rebuilt here (see <see cref="ReplicaJobGuardPatch"/>).</summary>
    internal static bool AcceptsRebuiltJob => enabled && !Predictive;

    /// <summary>Driver types whose rebuild already failed once, so a per-frame failure cannot flood the log.
    /// Reported per driver type: the message is about a job class the replica cannot rebuild, not about a pawn.</summary>
    private static readonly HashSet<string> reportedJobKeeps = [];

    internal static void NoteJobKept(Pawn pawn, JobDriver driver)
    {
        jobsKept++;
        var key = driver.GetType().FullName ?? driver.GetType().Name;
        if (!reportedJobKeeps.Add(key)) return;
        Log.Message("[TurboTick] 副本保留权威工作：" + (pawn?.LabelShort ?? "?") + "（" + driver.GetType().Name
            + "）的步骤在本端无法重建，按权威端的进度显示，不退回等待。");
    }

    private static int jobsKept;

    internal static int JobsKept => jobsKept;

    /// <summary>Pushes the local driver to the host's toil index.
    ///
    /// A replica never ticks, so `JobDriver` never advances its own `curToilIndex` - the pawn slides along the
    /// interpolated path while displaying the first step of the action forever. Measured: `最高 toil 8` on the host
    /// against two pawns permanently behind on the replica.
    ///
    /// `curToilIndex` is a private field with only a getter, so it is written by reflection. Nothing else needs
    /// writing: `JobDriver` has no `curToil` field at all (`CurToil` is a protected property that indexes `toils`
    /// by `curToilIndex`), so an earlier version's attempt to copy the toil object silently did nothing.
    ///
    /// The index is clamped to the local `toils` count, because `CurToil` logs an error and returns null when the
    /// index is past the end - and it is read on every draw. A replica whose `SetupToils` failed (see
    /// <see cref="ReplicaJobGuardPatch"/>) has fewer toils than the host, so writing the raw index would turn one
    /// unbuildable job into one error per frame.</summary>
    private static void ForceToil(Pawn pawn, int hostToil)
    {
        if (hostToil < 0) return;
        var driver = pawn.jobs?.curDriver;
        if (driver is null) return;
        try
        {
            toilsField ??= AccessTools.Field(typeof(JobDriver), "toils");
            if (toilsField?.GetValue(driver) is not System.Collections.IList toils || toils.Count == 0) return;
            var target = hostToil < toils.Count ? hostToil : toils.Count - 1;
            if (target != hostToil) toilClamped++;
            if (driver.CurToilIndex == target) return;
            toilIndexField ??= AccessTools.Field(typeof(JobDriver), "curToilIndex");
            toilIndexField?.SetValue(driver, target);
            toilForced++;
        }
        catch (Exception error)
        {
            if (toilForceFailures++ < 8)
                Log.Warning("[TurboTick] toil 对齐失败：" + error.GetType().Name + " " + error.Message);
        }
    }

    /// <summary>累计收到的「物体状态变化」条数。单帧偶发的 1 次不能说明轮转补扫在工作，
    /// 要看它在一段时间里是否持续增长——否则和 `忙碌 0` 一样是空读数。</summary>
    private static int changedThingsTotal;
    /// <summary>每个 pawn 上一次看到的权威心情值，用来证明这个数确实在变化。</summary>
    private static readonly Dictionary<int, float> observedMood = new();

    /// <summary>权威端的健康百分比，按 thingIDNumber 索引。副本不 tick、也不会自己生病受伤，
    /// 所以面板上的健康必须直接来自权威——这里用只读投影（patch getter），不去伪造 hediff：
    /// 伪造的伤势会污染 hediffSet，进而影响别处读健康的地方（工作能力、移动速度）。</summary>
    private static readonly Dictionary<int, float> replicaHealth = new();

    internal static bool TryHealth(int id, out float percent) => replicaHealth.TryGetValue(id, out percent);

    private static int toilForced;
    private static int toilForceFailures;
    private static int toilClamped;
    /// <summary>`Notify_Starting()` 抛异常的次数。单列一档是因为它和 `toil 截断` 的后果不同：
    /// 前者只让某些 driver 的 toil 条数少一格，后者让画面停在错误的那一步。</summary>
    private static int startFailures;
    private static System.Reflection.FieldInfo? toilsField;
    private static System.Reflection.FieldInfo? toilIndexField;
    /// <summary>把权威的 心情/食物/休息 写回本地 pawn。
    ///
    /// 这三个数在副本上不会自己变（不 tick），而它们正是选择面板与心情条显示的东西。
    /// 用 `CurLevel` 写而不是加偏移，是为了让面板读出来就是权威端的值，而不是"看起来差不多"。</summary>
    private static void ApplyNeeds(Pawn pawn, ReplicaPawn state)
    {
        try
        {
            if (state.Mood >= 0f && pawn.needs?.mood is { } mood) mood.CurLevel = state.Mood;
            if (state.Food >= 0f && pawn.needs?.food is { } food) food.CurLevel = state.Food;
            if (state.Rest >= 0f && pawn.needs?.rest is { } rest) rest.CurLevel = state.Rest;
        }
        catch (Exception) { }
    }
    /// <summary>在副本上重建想法列表，让「Thoughts」面板显示权威端的内容。
    ///
    /// 不清空只追加会让列表越来越长（副本不 tick，旧想法不会自然衰减消失），所以每次整体替换。
    /// 这是显示副本，替换它的想法不会影响任何模拟——`needs.mood.CurLevel` 已经由 `ApplyNeeds` 直接投影。
    ///
    /// <para>`pawn` 必须自己填，这是 `ThoughtMaker.MakeThought` 与 `MemoryThoughtHandler.AddThought` 的分工：
    /// 前者只 `def`/`SetForcedStage`/`Init()`，后者才在 `memories.Add` 之前写 `newThought.pawn = pawn`
    /// （<c>RimWorld/MemoryThoughtHandler.cs:160</c>）。我们是从序列化数据直接重建列表，跳过了后者，于是
    /// `Thought_Memory.MoodOffset()` 第一行的 `ThoughtUtility.ThoughtNullified(pawn, def)` 拿到 null 的 pawn，
    /// 走到 `NullifyingHediff` 的 `pawn.health.hediffSet` 就是 NullReferenceException。实测每开一次小人面板
    /// 就刷一条：`Exception filling tab RimWorld.ITab_Pawn_Needs` → `ThoughtUtility.NullifyingHediff` →
    /// `Thought_Memory.MoodOffset_Patch1`。想法列表本身没错，错的是它是「没有归属人的想法」。</para></summary>
    /// <summary>How many situational recomputations have run, and how many were skipped because the pawn ticked in the
    /// same frame. Both are counted because a silent zero is exactly how the missing schedule went unnoticed for a
    /// hundred rounds.</summary>
    internal static long situationalInvalidations;

    private static void ApplyThoughts(Pawn pawn, ReplicaPawn state)
    {
        if (state.Thoughts is null) return;
        try
        {
            var thoughts = pawn.needs?.mood?.thoughts;
            var memories = thoughts?.memories;
            if (memories is null) return;
            // **The derived half.** `ThoughtHandler.GetAllMoodThoughts` (`:482064`) returns the memories below PLUS
            // `situational.AppendMoodThoughts`, and that second list is not data: `UpdateAllMoodThoughts` builds it
            // through `def.Worker.CurrentState(pawn)` - room, temperature, precepts, colonist count. Its only scheduler
            // is `ThoughtHandler.ThoughtInterval` (`:482044`), reached from `Need_Mood.NeedInterval` (`:466955`), which
            // `Pawn_NeedsTracker.NeedsTrackerTickInterval` (`:467932`) calls on `pawn.IsHashIntervalTick(150, delta)`.
            // No needs tick runs here, so after the first read `thoughtsDirty` stays false for the life of the session
            // and the panel keeps showing the mood explanations of the moment the tab was first opened.
            //
            // **Measured, and why the trigger is not `state.Thoughts`:** `thoughts probe` on #873 read
            // `缓存 0 条（读取前 thoughtsDirty=True）`, then `读取后 缓存 2 条`, listing `NewColonyOptimism` (memory
            // half) and `Expectations` (situational half). The derivation itself is correct and complete - it reads
            // only inputs that do arrive. What was missing is the schedule, and the payload is the wrong clock for it:
            // `state.Thoughts` carries memories, while the situational inputs are the room, the temperature and the
            // precepts, none of which appear in it. A memory-driven invalidation would leave "the bedroom got much
            // nicer" invisible forever.
            //
            // **`SituationalThoughtInterval`, not `Notify_SituationalThoughtsDirty`.** The notify version clears both
            // caches (`:482006`), so the next reader rebuilds every `Thought_Situational` through `TryCreateThought`;
            // the interval version (`:481787`) calls `UpdateAllMoodThoughts`, which recalculates each cached thought in
            // place and only creates the missing ones. Same result, no reallocation, and it is literally the method
            // vanilla schedules - so the replica now follows vanilla's cadence instead of a constant of my own.
            if (thoughts?.situational is { } situational && pawn.IsHashIntervalTick(150))
            {
                try { situational.SituationalThoughtInterval(); situationalInvalidations++; }
                catch (Exception) { }
            }
            memories.Memories.Clear();
            foreach (var entry in state.Thoughts.Split(';'))
            {
                var parts = entry.Split('#');
                if (parts.Length != 2) continue;
                var def = DefDatabase<RimWorld.ThoughtDef>.GetNamedSilentFail(parts[0]);
                if (def is null) continue;
                if (!int.TryParse(parts[1], out var stage)) stage = 0;
                var thought = RimWorld.ThoughtMaker.MakeThought(def, stage);
                // 见方法注释：MakeThought 不填归属人，AddThought 才填。
                thought.pawn = pawn;
                memories.Memories.Add(thought);
            }
        }
        catch (Exception) { }
    }

    /// <summary>Rebuilds the authority's conditions on a replica that never runs the tick which would create them.
    ///
    /// <para>Two separate facts make this necessary. A replica does not tick, so <c>Pawn_HealthTracker.HealthTick</c>
    /// never runs: injuries do not heal, diseases do not progress, severity never moves. And a wound inflicted after
    /// the snapshot is never created locally at all, because damage is applied by the simulation. The player's report
    /// was "已经受伤没有任何日志" - the health tab kept showing the state the save was loaded with, and the damage
    /// left no trace anywhere on the replica.</para>
    ///
    /// <para><b>Why not just Scribe the HediffSet:</b> <c>HediffSet.ExposeData</c> uses <c>LookMode.Deep</c> over the
    /// whole list, serialising every hediff's comps. For a raid's worth of injuries that is a large fragment for data
    /// the receiving side can rebuild from <c>HediffMaker</c> plus the def. The three fields carried here are what the
    /// factory actually needs: defName, body part, and the severity that becomes <c>Hediff_Injury.Severity</c> or a
    /// disease's stage.</para>
    ///
    /// <para><b>Why the list is replaced rather than merged:</b> a hediff the authority no longer has is gone (healed,
    /// amputated, cured), and leaving it would keep a scar on screen forever. The version guard makes a late older
    /// frame safe: it cannot overwrite a newer list.</para>
    ///
    /// <para><b>Why a part that does not resolve is treated as whole-body:</b> <c>AddHediff</c> with a Part that does
    /// not belong to this pawn's body is the one way this throws, and a raid full of modded races makes that real. A
    /// whole-body hediff is what vanilla uses for every non-injury condition anyway.</para></summary>
    private static void ApplyHediffs(ReplicaHediffSet state)
    {
        if (!FindPawnAny(state.Pawn, out var pawn)) return;
        var health = pawn.health;
        var hediffSet = health?.hediffSet;
        if (health is null || hediffSet is null) return;
        // Later frames win. `hediffVersions` is per pawn and only moves forward, so an out-of-order frame is dropped
        // instead of resurrecting conditions the player already healed.
        if (hediffVersions.TryGetValue(state.Pawn, out var seen) && state.Version <= seen) return;
        hediffVersions[state.Pawn] = state.Version;
        try
        {
            // `RemoveHediff` rather than editing `hediffs` directly: it keeps `cachedParts` and the capacity caches
            // coherent, and those are what the health tab and every capacity reader consult.
            for (var i = hediffSet.hediffs.Count - 1; i >= 0; i--)
            {
                var existing = hediffSet.hediffs[i];
                if (existing is not null) health.RemoveHediff(existing);
            }
            foreach (var entry in state.Entries)
            {
                var parts = entry.Split('#');
                if (parts.Length < 3) continue;
                var def = DefDatabase<HediffDef>.GetNamedSilentFail(parts[0]);
                if (def is null) { MissingHediffDefs++; continue; }
                var part = parts[1].Length == 0 ? null : FindBodyPart(pawn, parts[1]);
                if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var severity))
                    severity = 0f;
                var hediff = HediffMaker.MakeHediff(def, pawn, part);
                // Set before adding: some defs copy severity into the injury during AddHediff, and a zero there reads
                // as an intact limb carrying a scar.
                hediff.Severity = severity;
                health.AddHediff(hediff, part);
                // After `AddHediff`, not before: `PostAdd` is what rolls the value this overwrites, and it runs
                // inside `AddHediff`. Writing first would be discarded by that roll.
                WriteHediffDerived(hediff, parts.Length > 3 ? parts[3] : string.Empty);
            }
            hediffSet.DirtyCache();
            HediffSetsApplied++;
        }
        catch (Exception error)
        {
            if (hediffFailures++ < 8)
                Log.Warning("[TurboTick] 重建伤势失败 id=" + state.Pawn + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Finds a body part by def across the pawn's whole body.
    ///
    /// <para>`BodyDef.AllParts` is the flat list of every part the race can have, which is what a hediff's Part points
    /// into. Searching that rather than the not-missing parts matters: a part that is already destroyed still has to
    /// resolve, or a scar on a missing limb would silently move to the torso.</para></summary>
    /// <summary>Writes the fourth field back onto a rebuilt hediff. Mirrors `HeadlessRuntime.HediffDerived`.
    ///
    /// <para>Has to run after <c>AddHediff</c>: the field it writes is assigned inside that call's `PostAdd`, so a value
    /// written before would be replaced by the replica's own roll. That ordering is the whole reason this is a separate
    /// statement rather than part of the construction.</para></summary>
    private static void WriteHediffDerived(Hediff hediff, string derived)
    {
        if (derived is not { Length: > 1 }) return;
        var body = derived.AsSpan(1);
        switch (derived[0])
        {
            case 'b' when hediff is Hediff_BloodRage:
            {
                if (!float.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var raise)) return;
                HarmonyLib.AccessTools.Field(typeof(Hediff_BloodRage), "adjustedSeverityRaisePerTick")
                    ?.SetValue(hediff, raise);
                return;
            }
            case 'm' when hediff is Hediff_MetalhorrorImplant:
            {
                if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timer)) return;
                HarmonyLib.AccessTools.Field(typeof(Hediff_MetalhorrorImplant), "filthTimer")
                    ?.SetValue(hediff, timer);
                return;
            }
        }
    }

    private static BodyPartRecord? FindBodyPart(Pawn pawn, string partDefName)
    {
        var parts = pawn.RaceProps?.body?.AllParts;
        if (parts is null) return null;
        for (var i = 0; i < parts.Count; i++)
            if (parts[i]?.def?.defName == partDefName) return parts[i];
        return null;
    }

    private static readonly Dictionary<int, int> hediffVersions = [];

    private static int hediffFailures;

    internal static int HediffSetsApplied { get; private set; }

    internal static int MissingHediffDefs { get; private set; }

    /// <summary>Resolves one target carried by the frame. A missing thing is reported, not silently degraded.
    ///
    /// <para>Falling back to the cell when the id did not resolve is what turned "carry this stack" into
    /// "carry whatever is at this cell": `JobDriver_HaulToContainer.ThingToCarry` is
    /// <c>(Thing)job.GetTarget(TargetIndex.A)</c>, and the explicit cast logs
    /// <c>Casted LocalTargetInfo to Thing but it had cell (121, 0, 122)</c> and then returns null - the driver
    /// was built over a position. Measured once per hauling job, with the stack trace reaching back into
    /// <c>AuthoritativeReplica.SyncJob</c>.</para>
    ///
    /// <para>The ids that legitimately cannot be found in <c>listerThings</c> are the ones a holder owns:
    /// a stack inside a <c>Pawn_CarryTracker</c> or a <c>ThingOwner</c> is not registered on the map, so the
    /// frame index has to look there too (see <see cref="BuildFrameIndex"/>). A job whose target really is gone
    /// keeps the cell and stays valid - that is still the correct reading for "go here" jobs.</para></summary>
    private static LocalTargetInfo DecodeTarget(int cell, int thingId)
    {
        if (thingId > 0)
        {
            var thing = FindThing(thingId);
            if (thing is not null) return new LocalTargetInfo(thing);
            // 目标物体不在本端：绝不退化成格子。一个指向格子的 targetA 会被 driver 当成「物体」解引用，
            // 那正是 `Casted LocalTargetInfo to Thing` 的来源。
            if (TargetMisses++ < 8)
                Log.Warning($"[TurboTick] 目标物体缺失 id={thingId}（cell={cell}），该 job 本帧跳过。");
            return LocalTargetInfo.Invalid;
        }
        // x * 1000 + z, so the divisor recovers the column.
        return cell < 0 ? LocalTargetInfo.Invalid : new LocalTargetInfo(new IntVec3(cell / 1000, 0, cell % 1000));
    }

    private static int TargetMisses;

    private static int jobAlignments;
    private static int jobAlignmentFailures;

    /// <summary>How far a predictive peer may drift before the host's position wins. Roughly one cell:
    /// smaller than that is ordinary pathing noise, larger means the two simulations disagree.</summary>
    private static readonly float CorrectionRadius = 1.5f;
    private static int corrections;
    private static int worstTickOffset;
    private static float worstDrift;
    private static float driftSum;
    private static int driftSamples;

    private sealed class Motion
    {
        public Vector3 From;
        public Vector3 To;
        public float Start;
    }

    private static readonly Dictionary<int, Motion> motions = [];
    private static float lastFrameArrival;
    private static int hitchCount;
    private static float worstGap;
    /// <summary>Wall-clock budget for object updates in one rendered frame. Each update is a Scribe round
    /// trip, and enough of them in one frame blocks rendering until Windows calls the window hung.</summary>
    private static readonly float ObjectBudgetSeconds =
        float.TryParse(Environment.GetEnvironmentVariable("TURBOTICK_OBJECT_BUDGET_MS"), out var ms) && ms > 0
            ? ms / 1000f : 0.006f;
    private static int DroppedWork;
    /// <summary>How long the per-frame pawn pass takes. The `帧间隔` reading cannot separate "the host sent late"
    /// from "this peer's game thread was busy", and the two need opposite fixes — one is a network/pacing
    /// question, the other a per-frame cost question. Measured: 96 pawns × 3 replicas reported
    /// `帧间隔 最差 0.75s / 停顿 8 次` while a 1-replica run of the same map stayed at 0.00s, which points at this
    /// loop but does not prove it without this timer.</summary>
    private static float pawnApplyTotal;
    private static float worstPawnApply;
    private static int worstPawnApplyCount;

    /// <summary>How many times this peer's own update loop ran. Paired with the frame counter it separates the two
    /// causes of a long `帧间隔` that need opposite fixes: a starved local loop (this number stays tiny across the
    /// gap) versus a frame that genuinely arrived late while the loop kept running.</summary>
    private static int pumpCount;
    private static int pumpsAtLastFrame;
    private static int worstHitchPumps = -1;
    private static int mostHitchPumps;

    /// <summary>Smoothed time between frames; the interpolation window matches what actually arrives.</summary>
    private static float frameInterval = 1f / 30f;

    /// <summary>
    /// The engine's own tick counter, read behind the patched <see cref="TickManager.TicksGame"/> getter.
    ///
    /// This exists only so a run can prove the replica is not simulating: TicksGame is overridden to report
    /// the host clock, which advances whether or not this peer ticks, while this counter only moves inside
    /// <c>DoSingleTick</c>. Equal-to-the-load-tick after minutes of frames is the proof; two equal numbers
    /// from a frozen and a healthy replica would prove nothing.
    /// </summary>
    private static int EngineTicks()
    {
        try
        {
            engineTicks ??= AccessTools.FieldRefAccess<TickManager, int>("ticksGameInt");
            var manager = Find.TickManager;
            return manager is null ? -1 : engineTicks(manager);
        }
        catch (Exception) { return -1; }
    }

    private static AccessTools.FieldRef<TickManager, int>? engineTicks;

    /// <summary>The engine's own counter, for probes that have to tell "the clock reports the host tick" from "this peer
    /// is ticking" - the two read the same through `TicksGame` because that getter is patched. `Frozen` says which mode
    /// the peer is in; this says what the underlying counter did.</summary>
    internal static int EngineTickForProbe() => EngineTicks();

    private static Vector3 Sample(Motion motion, float now)
    {
        var span = Mathf.Clamp(frameInterval, 0.016f, 0.25f);
        var t = Mathf.Clamp01((now - motion.Start) / span);
        if (t >= 1f) return motion.To;
        // Ease-out so a stop decelerates instead of halting abruptly.
        var eased = 1f - (1f - t) * (1f - t);
        return Vector3.Lerp(motion.From, motion.To, eased);
    }

    /// <summary>Interpolated world position for a replica pawn, or false when this pawn has no motion yet.</summary>
    internal static bool TryDrawPos(int id, out Vector3 position)
    {
        if (motions.TryGetValue(id, out var motion))
        {
            position = Sample(motion, Clock.Now);
            return true;
        }
        position = default;
        return false;
    }

    internal static void Forget(int id) => motions.Remove(id);

    /// <summary>Pawns currently between two frames. A non-zero count proves rendering is interpolating
    /// instead of sitting on cell centres.</summary>
    private static int SmoothCount()
    {
        var now = Clock.Now;
        var count = 0;
        foreach (var motion in motions.Values)
            if (now - motion.Start < frameInterval) count++;
        return count;
    }

    /// <summary>Merges auto-slaughter rows by exact (map, species).
    ///
    /// <para>`EventUnion.ById` is the right tool for every other list here because their keys are the ids the game
    /// already uses. These rows have no id: a limit is addressed by the map it applies to and the species it caps, and
    /// both are needed - the same species has one independent limit per map. A packed `int` key would therefore be a
    /// collision risk, and a collision drops a player's limit with no symptom anywhere.</para></summary>
    private static List<ReplicaAutoSlaughterConfig>? MergeByMapAndSpecies(
        IReadOnlyList<ReplicaAutoSlaughterConfig>? newer, IReadOnlyList<ReplicaAutoSlaughterConfig>? older)
    {
        if (newer is null or { Count: 0 }) return older is { Count: > 0 } ? [.. older] : null;
        if (older is null or { Count: 0 }) return [.. newer];
        var byKey = new Dictionary<(int Map, string Animal), ReplicaAutoSlaughterConfig>();
        foreach (var state in older) if (state is not null) byKey[(state.Map, state.Animal)] = state;
        foreach (var state in newer) if (state is not null) byKey[(state.Map, state.Animal)] = state;
        var merged = new List<ReplicaAutoSlaughterConfig>(byKey.Count);
        foreach (var state in byKey.Values) merged.Add(state);
        return merged.Count > 0 ? merged : null;
    }
}

/// <summary>Plant.Print reads Map, def and graphic through several paths and throws if any is missing.
/// A replica can transiently hold such an object between a creation and its state, so printing is
/// skipped and the real condition is reported instead of spamming the log once per section rebuild.</summary>
[HarmonyPatch(typeof(RimWorld.Plant), nameof(RimWorld.Plant.Print))]
internal static class ReplicaPlantPrintGuard
{
    private static bool Prefix(RimWorld.Plant __instance)
    {
        // Not gated on Frozen: a predictive peer simulates locally and can also hold an object that lost
        // its map between a creation and its state, which is exactly what Print throws on.
        if (!AuthoritativeReplica.Enabled) return true;
        if (__instance.Map is not null && __instance.Spawned && __instance.def?.graphicData is not null
            && __instance.def.plant is not null) return true;
        AuthoritativeReplica.ReportBrokenPlant(__instance);
        return false;
    }
}

/// <summary>Backstop for the one render path a replica cannot keep valid.
///
/// <para><c>PawnRenderer.EnsureGraphicsInitialized</c> dereferences <c>pawn.RaceProps.renderTree</c>, and
/// <c>Pawn.RaceProps</c> is <c>def.race</c> with no null check. A pawn whose def went missing therefore throws from
/// inside the renderer every frame it is on screen - measured 2026-11-16: <c>Exception drawing Jackalope</c> 4263
/// times in three minutes, a steady 24 lines per second, all of them re-entering the same dead object.</para>
///
/// <para>The cause is fixed in <see cref="AuthoritativeReplica"/> (`ApplyPreservingIdentity` restores the def on both
/// branches, and <see cref="ScribeBridge"/> restores it before its own respawn). This guard exists because the failure
/// mode is a per-frame exception storm on a player's screen: if any other path ever clears a def again, one skipped
/// draw plus one named warning is the right cost, not a log full of stack traces.</para></summary>
[HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
internal static class ReplicaPawnRenderGuard
{
    /// <summary>`PawnRenderer.pawn` is private, so it is read once through AccessTools and cached. The field is
    /// assigned in the constructor and never reassigned, so the cached handle stays valid for the process.</summary>
    private static AccessTools.FieldRef<PawnRenderer, Pawn>? pawnField;
    private static readonly HashSet<int> reported = [];

    private static bool Prefix(PawnRenderer __instance)
    {
        if (!AuthoritativeReplica.Enabled) return true;
        Pawn? pawn;
        try { pawn = (pawnField ??= AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn"))(__instance); }
        catch (Exception) { return true; }
        if (pawn is null) return true;
        // `def` first: `RaceProps` is `def.race` and `DefaultRenderFlagsNow` then reads `pawn.health.hediffSet`.
        if (pawn.def?.race is not null && pawn.health?.hediffSet is not null) return true;
        if (reported.Add(pawn.thingIDNumber) && reported.Count <= 12)
            Log.Warning($"[TurboTick] 跳过绘制角色 id={pawn.thingIDNumber} "
                + $"def={(pawn.def is null ? "null" : "ok")} race={(pawn.def?.race is null ? "null" : "ok")} "
                + $"health={(pawn.health is null ? "null" : "ok")} hediffSet={(pawn.health?.hediffSet is null ? "null" : "ok")} "
                + $"spawned={pawn.Spawned} dead={pawn.Dead}");
        return false;
    }
}

/// <summary>`FindAllowedDesignator` treats a null buildable as a dictionary key, which throws instead of returning.
///
/// <para>Measured 2026-11-16 (`[Ref 3CAD3752]`): selecting a blueprint on a replica produced
/// <c>ArgumentNullException: Value cannot be null. (Parameter 'key')</c> every frame the inspect pane was open, from
/// <c>Blueprint_Build.GetGizmos</c> → <c>BuildCopyCommand(def.entityDefToBuild, …)</c> → this method. The stack ends
/// at <c>GizmoGridDrawer.DrawGizmoGridFor</c>, i.e. the whole gizmo grid is abandoned, not just the one button.</para>
///
/// <para>Vanilla's own answer is "no designator is allowed for this", which is exactly what the method returns for a
/// buildable it cannot find. The only reason it throws is that <c>Dictionary.ContainsKey</c> rejects a null key before
/// the lookup can say "not found". So the null case is answered here with the same value the method would have
/// produced, and nothing else changes.</para>
///
/// <para>This is deliberately narrow: it does not invent a designator, does not swallow the condition, and stays out of
/// the way whenever <c>buildable</c> is non-null. <see cref="ReplicaBlueprintDefGuard"/> reports which def is broken
/// so the cause is named rather than merely hidden.</para></summary>
[HarmonyPatch(typeof(RimWorld.BuildCopyCommandUtility), nameof(RimWorld.BuildCopyCommandUtility.FindAllowedDesignator))]
internal static class ReplicaFindDesignatorNullGuard
{
    private static bool Prefix(Verse.BuildableDef? buildable, ref RimWorld.Designator_Build? __result)
    {
        if (buildable is not null) return true;
        __result = null;
        return false;
    }
}

/// <summary>Names the blueprint whose def lost its build target, instead of leaving a silent null to crash later.
///
/// <para>`Blueprint_Build.def.entityDefToBuild` is written once, by `ThingDefGenerator_Buildings` when the blueprint
/// def is generated (`thingDef.entityDefToBuild = def;`, `RimWorld/ThingDefGenerator_Buildings.cs:183`). It is not part
/// of `Blueprint.ExposeData`, so it cannot be lost by a fragment - which means a null there says the object's `def` is
/// not the generated blueprint def at all. That is a real defect worth naming: every later reader assumes it
/// (`Blueprint.Label`, `BlockingHaulableOnTop`, `GenConstruct.CanPlaceBlueprintAt`), and they fail in different
/// places with different exceptions.</para>
///
/// <para>Checked where objects enter the replica rather than in the gizmo path, because the gizmo path only runs when
/// the player selects the object - a blueprint nobody clicks would stay broken and silent forever.</para></summary>
internal static class ReplicaBlueprintDefGuard
{
    private static readonly HashSet<string> reported = [];

    internal static void Note(Thing thing)
    {
        if (thing is not RimWorld.Blueprint blueprint) return;
        if (blueprint.def?.entityDefToBuild is not null) return;
        var name = blueprint.def?.defName ?? "<null def>";
        if (!reported.Add(name) || reported.Count > 8) return;
        Log.Warning($"[TurboTick] 蓝图的 Def 缺少 entityDefToBuild：{name} id={blueprint.thingIDNumber} "
            + $"类型={blueprint.GetType().Name} spawned={blueprint.Spawned} "
            + "（该 Def 不是 ThingDefGenerator_Buildings 生成的蓝图 Def）");
    }
}

/// <summary>Reports why a replica's right-click menu came up empty, without changing what it contains.
///
/// <para>Vanilla builds that menu from <c>FloatMenuOptionProvider</c>s, and most of them ask the simulation whether the
/// action is possible: <c>CanReach</c>, <c>CanReserve</c>, <c>HasJobOnThing</c>, work-type checks. A replica runs no
/// tick and no AI, so those questions get answers computed from a world that never advances - and a provider that
/// answers "no" produces no option at all, which the player sees as "无法下达强制任务": the menu opens, or does not
/// open, and nothing happens.</para>
///
/// <para>This deliberately does not fabricate options. Inventing one would let the player click an order the authority
/// then refuses, which is a worse experience than an honest empty menu. It records the count and the pawn so the
/// failure is named in the log instead of being invisible.</para></summary>
[HarmonyPatch(typeof(RimWorld.FloatMenuMakerMap), nameof(RimWorld.FloatMenuMakerMap.GetOptions))]
internal static class ReplicaFloatMenuProbe
{
    /// <summary>Counts how many times each (pawn, cell) came up empty, because "empty" alone says nothing.
    ///
    /// <para><b>What `floatmenu-probe` changed here.</b> Measured on the authority, the same colonist standing at
    /// (117,142) produced ONE option for his own cell and ZERO for the empty cell to his east, with 53 providers
    /// registered. So an empty menu is frequently the correct answer: the cell holds nothing to act on and every
    /// provider says no. The old version of this postfix logged "右键菜单为空" with a guess at the cause
    /// （「副本不跑模拟，多数 provider 会回答否」）, and that guess cannot be checked - it reads the same whether the
    /// menu is correctly empty or broken.</para>
    ///
    /// <para>The log now records the cell and the repetition count, so a report of "the menu is empty where it should
    /// not be" can be compared against the authority's answer for that exact cell using `floatmenu-probe`. It still does
    /// not fabricate options: inventing one would let the player click an order the authority then refuses.</para></summary>
    private static void Postfix(RimWorld.FloatMenuContext? context, List<Verse.FloatMenuOption> __result)
    {
        if (!AuthoritativeReplica.Frozen) return;
        if (__result is { Count: > 0 }) return;
        var pawns = context?.ValidSelectedPawns?.ToList();
        var who = pawns is { Count: > 0 } ? pawns[0]?.LabelShortCap ?? "?" : "无";
        var cell = context?.ClickedCell ?? IntVec3.Invalid;
        // Keyed by cell as well as pawn: one pawn right-clicking three different cells is three different questions,
        // and collapsing them to one line hid that the first cell was the only one that mattered.
        var key = who + "@" + (cell.IsValid ? cell.x.ToString(CultureInfo.InvariantCulture) + ","
            + cell.z.ToString(CultureInfo.InvariantCulture) : "?");
        if (!reportedMenus.Add(key)) return;
        if (reportedMenus.Count > 8) return;
        Log.Warning("[TurboTick] 右键菜单为空：选中=" + who
            + $" 格=({cell.x.ToString(CultureInfo.InvariantCulture)},{cell.z.ToString(CultureInfo.InvariantCulture)})"
            + $" 数量={pawns?.Count ?? 0}（该格在权威端也可能本来就 0 个选项：空格上没有可执行动作。"
            + "用 floatmenu-probe 查同一格的基线，两者不同才是缺陷）");
    }

    private static readonly HashSet<string> reportedMenus = [];
}

/// <summary>Keeps the colonist bar's cached screen positions in step with a world that never fires its own events.
///
/// <para>`Selector.SelectInsideDragBox` (`RimWorld/Selector.cs:417`) selects colonists by testing
/// <c>rect.Overlaps(drawLocs[i])</c> against `ColonistBar.DrawLocs` (`RimWorld/ColonistBar.cs:443`
/// → `ColonistsOrCorpsesInScreenRect`). Those positions come from `cachedDrawLocs`, which is rebuilt only inside
/// `CheckRecacheEntries` and only when `entriesDirty` is true - and `entriesDirty` is set by `MarkColonistsDirty()`,
/// which vanilla calls from pawn spawn/death, renames, display-order changes and caravan events (twenty-odd call sites
/// across the assembly).</para>
///
/// <para>A replica runs none of those events, so after the initial load the cached positions never move again: the
/// box selects whoever stood where the bar last remembered, regardless of where the pawns are drawn now. That is the
/// reported "框选判断也有问题" - not a hit-test bug, a stale cache.</para>
///
/// <para>Marking dirty every frame costs one rebuild of a list that holds one entry per colonist. Rebuilding is
/// already what vanilla does on any busy tick, and the alternative is a drag box that selects the wrong pawns.</para>
///
/// <para>Gated on `Frozen` so the predictive path keeps vanilla's own invalidation: there the local simulation does
/// fire those events, and forcing a rebuild would be work with no effect.</para></summary>
[HarmonyPatch(typeof(RimWorld.ColonistBar), nameof(RimWorld.ColonistBar.ColonistBarOnGUI))]
internal static class ReplicaColonistBarRefresh
{
    private static void Prefix()
    {
        if (!AuthoritativeReplica.Frozen) return;
        Find.ColonistBar?.MarkColonistsDirty();
    }
}

/// <summary>A display replica never runs the pather, so the vanilla draw tracker has no tween to follow
/// and pawns jump one cell per frame. World positions arrive in frames and are interpolated here instead,
/// which is what makes walking look continuous.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawPos), MethodType.Getter)]
internal static class ReplicaDrawPosPatch
{
    private static bool Prefix(Pawn __instance, ref Vector3 __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        if (!AuthoritativeReplica.TryDrawPos(__instance.thingIDNumber, out var position)) return true;
        __result = position;
        return false;
    }
}

/// <summary>The property the renderer actually reads.
///
/// <see cref="Pawn.DrawPos"/> is not it: drawing goes through <c>Pawn_DrawTracker.DrawPos</c>, which returns
/// <c>PawnTweener.TweenedPos</c> - a value the tweener recomputes inside a tick. Patching <c>Pawn.DrawPos</c> alone
/// therefore changed a property nobody asks for, and a replica drew pawns at their load-time position while the
/// interpolation was working perfectly in our own bookkeeping. Measured: replicas reported `滑动中 33/33` and
/// `平均漂移 0.00 格` while the picture never moved.
///
/// <c>PawnTweener</c> holds the pawn in a private field, so the id is reached through <see cref="Pawn_DrawTracker"/>'s
/// own back-reference instead of a lookup by hand.</summary>
[HarmonyPatch(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos), MethodType.Getter)]
internal static class ReplicaDrawTrackerPosPatch
{
    private static bool Prefix(Pawn_DrawTracker __instance, ref Vector3 __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        var pawn = TrackerPawn(__instance);
        if (pawn is null) return true;
        if (!AuthoritativeReplica.TryDrawPos(pawn.thingIDNumber, out var position)) return true;
        __result = position;
        return false;
    }

    private static Pawn? TrackerPawn(Pawn_DrawTracker tracker)
    {
        try
        {
            pawnField ??= AccessTools.Field(typeof(Pawn_DrawTracker), "pawn");
            return pawnField?.GetValue(tracker) as Pawn;
        }
        catch (Exception) { return null; }
    }

    private static System.Reflection.FieldInfo? pawnField;
}

/// <summary>健康百分比：副本上直接返回权威的值。
///
/// 不伪造 hediff 是有意的——往 `hediffSet` 里塞伤势会连带影响工作能力、移动速度、医疗判断，
/// 那是把显示问题变成模拟问题。而显示副本本来就只需要「面板显示多少」这一个数。</summary>
[HarmonyPatch(typeof(SummaryHealthHandler), nameof(SummaryHealthHandler.SummaryHealthPercent), MethodType.Getter)]
internal static class ReplicaHealthPatch
{
    private static bool Prefix(SummaryHealthHandler __instance, ref float __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        var pawn = HealthOwner(__instance);
        if (pawn is null) return true;
        if (!AuthoritativeReplica.TryHealth(pawn.thingIDNumber, out var percent)) return true;
        __result = percent;
        return false;
    }

    private static Pawn? HealthOwner(SummaryHealthHandler handler)
    {
        try
        {
            // 1.7 里 SummaryHealthHandler 用私有字段回指 pawn；名字没有公开契约，逐个试。
            foreach (var name in new[] { "pawn", "parent", "owner" })
            {
                var field = AccessTools.Field(typeof(SummaryHealthHandler), name);
                if (field?.GetValue(handler) is Pawn found) return found;
            }
            return null;
        }
        catch (Exception) { return null; }
    }


}
/// <summary>A display replica must report the host's clock. Without this every TicksGame reader in the UI
/// and in rendering would freeze at the tick the save was loaded, because the replica never ticks.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TicksGame), MethodType.Getter)]
internal static class ReplicaClockPatch
{
    private static bool Prefix(ref int __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        __result = SharedColony.Session.Barrier.AppliedTick;
        return false;
    }
}

/// <summary>Following the host's tick counter is not enough: the calendar is a second clock.
///
/// Vanilla keeps <c>TicksAbs => ticksGameInt + gameStartAbsTick</c>, and everything that answers "what time is
/// it" - the date, the hour, <c>GenCelestial</c>'s sun angle, day/night, the light level derived from it - reads
/// <c>TicksAbs</c>, not <c>TicksGame</c>. Patching only <c>TicksGame</c> left a replica reporting the host's tick
/// while its sky was frozen at the checkpoint's date: the replica stood in permanent noon, and unfreezing the
/// light grid (see <see cref="SharedLightGrid"/>) would have recomputed the same noon over and over.
/// Measured 2026-11-11: with only <c>TicksGame</c> patched, a 3000-frame run never changed the hour.
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TicksAbs), MethodType.Getter)]
internal static class ReplicaAbsClockPatch
{
    private static bool Prefix(TickManager __instance, ref int __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        // Same formula as vanilla, with the host's tick standing in for the local one. The zero guard is vanilla's
        // too: `gameStartAbsTick` is only set once a world starts, and adding it then would invent a date.
        var tick = SharedColony.Session.Barrier.AppliedTick;
        __result = __instance.gameStartAbsTick == 0 ? tick : tick + __instance.gameStartAbsTick;
        return false;
    }
}

/// <summary>A replica rebuilds the host's job with `SetupToils`, and a toil whose setup needs the local AI
/// (a reservation, a reachability path, a pawn not yet spawned here) throws. Vanilla answers a throwing
/// setup by ending the job and starting a wait, which on a replica means the pawn abandons the host's job
/// and stands still - the exact "no work" pose this whole path exists to remove.
///
/// The host's job is therefore kept and its step index set to -1: the driver stays attached, `ForceToil`
/// writes the true index on the next frame, and nothing here runs any job logic. Same as the reference
/// headless-replica mod, whose rebuild keeps the driver and only resets the step.
///
/// Only a replica is affected: an authoritative or predictive peer is running its own simulation, where a
/// failed setup really is an error worth recovering from.</summary>
[HarmonyPatch(typeof(Verse.AI.JobUtility), nameof(Verse.AI.JobUtility.TryStartErrorRecoverJob))]
internal static class ReplicaJobGuardPatch
{
    private static readonly AccessTools.FieldRef<JobDriver, int> Step =
        AccessTools.FieldRefAccess<JobDriver, int>("curToilIndex");

    private static bool Prefix(Pawn pawn, JobDriver concreteDriver)
    {
        if (!AuthoritativeReplica.AcceptsRebuiltJob) return true;
        if (concreteDriver is null) return true;
        try { Step(concreteDriver) = -1; } catch (Exception) { return true; }
        AuthoritativeReplica.NoteJobKept(pawn, concreteDriver);
        return false;
    }


}



























