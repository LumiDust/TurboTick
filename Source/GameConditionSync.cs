using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The map's active weather conditions, and the derived values inside them.
///
/// <para><b>Why a replica needs this.</b> <c>GameConditionManagerTick</c> advances every condition
/// (<c>GameConditionTick</c>), ends the expired ones, and <c>MapTemperatureTick</c>'s cache adds
/// <c>GameConditionManager.AggregateTemperatureOffset()</c> to the outdoor reading. A replica runs neither. So a cold snap that
/// starts after the save was loaded does not exist for the replica at all: the weather overlay draws the old sky, the
/// outdoor temperature reads the old number, and - because the outdoor reading is what every indoor room is equalised
/// against - the whole colony's temperature is wrong. The player's report was "服务器上寒潮，我这边还是晴天"</para>
///
/// <para><b>Why conditions are not reconstructed.</b> <c>GameConditionManager.RegisterCondition</c> sets the manager, calls
/// <c>cond.Init()</c> (which resets every derived field - <c>GameCondition_TemperatureOffset.tempOffset</c> comes straight
/// from the def there), fires <c>Notify_GameConditionAdded</c> on every map, and the <c>Expired</c> path calls
/// <c>End()</c>, which sends the def's end message. Rebuilding would send the letter twice and undo the derived values in
/// the same breath. The objects are therefore created once and then only have their values written.</para>
///
/// <para><b>What travels.</b> The def name, <c>startTick</c> (so the replica's own <c>Expired</c> - a pure function of tick -
/// agrees without us sending an expiry), and a per-condition derived-value blob which each concrete class fills in. Nothing
/// else: <c>Duration</c>, <c>Permanent</c> and <c>startTick</c> are all the replica needs to answer "how long is left", and
/// all three are plain data.</para>
/// </summary>
internal static class GameConditionSync
{
    /// <summary>Last encoded condition list sent, so an unchanged map costs nothing.</summary>
    private static readonly Dictionary<int, string> sent = [];

    internal static long ConditionsSent { get; private set; }
    internal static long ConditionsApplied { get; private set; }
    internal static long ConditionsCreated { get; private set; }
    internal static long ConditionsRemoved { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        ConditionsSent = 0;
        ConditionsApplied = 0;
        ConditionsCreated = 0;
        ConditionsRemoved = 0;
        ApplyFailures = 0;
    }

    /// <summary>What the encoder would put on the wire right now, without recording it as sent.
    ///
    /// <para>Exists because "0 conditions sent" is the reading for both an idle channel and a broken one, and on a paused
    /// server with no replica no frame is published at all - so the sent counter can never distinguish them. This runs the
    /// real encoder over the real conditions and returns the exact strings, which is a claim the counter cannot make.</para>
    /// </summary>
    internal static string Preview()
    {
        var text = new StringBuilder();
        try
        {
            foreach (var map in Find.Maps)
            {
                var manager = map.gameConditionManager;
                if (manager is null) continue;
                foreach (var condition in manager.ActiveConditions)
                {
                    if (condition?.def?.defName is not { Length: > 0 } name) continue;
                    if (text.Length > 0) text.Append(" / ");
                    text.Append(name).Append('#').Append(condition.startTick)
                        .Append('#').Append(condition.Permanent ? 1 : 0)
                        .Append('#').Append(condition.Permanent ? -1 : condition.Duration)
                        .Append('#').Append(Derived(condition) ?? "-");
                }
            }
        }
        catch (Exception error) { return "编码失败：" + error.GetType().Name; }
        return text.Length == 0 ? "（无）" : text.ToString();
    }

    /// <summary>Conditions that were sent and are gone now, so the frame can carry the removal.</summary>
    private static readonly List<ReplicaGameConditionRemoval> pendingRemovals = [];
    private static readonly Dictionary<int, int> sentOnMap = [];

    /// <summary>Encodes every active condition on every map.</summary>
    internal static List<ReplicaGameCondition>? Publish() => Publish(out _);

    internal static List<ReplicaGameCondition>? Publish(out List<ReplicaGameConditionRemoval>? removals)
    {
        List<ReplicaGameCondition>? changed = null;
        removals = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
            {
                var manager = map.gameConditionManager;
                if (manager is null) continue;
                foreach (var condition in manager.ActiveConditions)
                {
                    if (condition?.def?.defName is not { Length: > 0 } name) continue;
                    var id = condition.uniqueID;
                    if (id < 0) continue;
                    live.Add(id);
                    var encoded = name + "#" + condition.startTick.ToString(CultureInfo.InvariantCulture)
                        + "#" + (condition.Permanent ? "1" : "0")
                        + "#" + condition.Duration.ToString(CultureInfo.InvariantCulture)
                        + "#" + Derived(condition);
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    sentOnMap[id] = map.uniqueID;
                    (changed ??= []).Add(new ReplicaGameCondition(id, map.uniqueID, name, condition.startTick,
                        condition.Permanent, condition.Duration, Derived(condition)));
                    ConditionsSent++;
                }
            }
            foreach (var id in new List<int>(sent.Keys))
            {
                if (live.Contains(id)) continue;
                sent.Remove(id);
                // A condition that ended on the authority has to end here too. The map id is remembered rather than looked
                // up, because by now the object is already out of the list.
                if (sentOnMap.Remove(id, out var mapId))
                {
                    (removals ??= []).Add(new ReplicaGameConditionRemoval(mapId, id));
                    ConditionsRemoved++;
                }
            }
        }
        catch (Exception) { return changed; }
        return changed;
    }

    /// <summary>What this channel would encode for one condition, for the probe. Exposed because the encoding is the only
    /// part that can be wrong without any counter moving: a condition that reads back unchanged looks identical whether the
    /// fields travelled or were re-derived from the def.</summary>
    internal static string? DerivedFor(GameCondition condition) => Derived(condition);

    /// <summary>The derived values each concrete condition keeps. Only the ones that are (a) scribed and (b) changed by a tick
    /// are carried: a value the replica can recompute from the def is not a value at all.</summary>
    private static string? Derived(GameCondition condition)
    {
        // Temperature offsets. Which classes need one is decided by *whether the value can be derived here*, not by which ones
        // happen to keep a field:
        //   `GameCondition_TemperatureOffset.tempOffset` - set from the def in `Init()` and never moved; it is a def value
        //       that survives the rebuild, so it needs nothing.
        //   `GameCondition_ColdSnap` / `HeatWave` - `TemperatureOffset()` is a pure lerp over `TicksPassed`, and the replica's
        //       clock already equals the authority's, so they need nothing either.
        //   `GameCondition_HeatVentHeat` / `GameCondition_UnnaturalHeat` - the offset is accumulated by `GameConditionTick`
        //       (0.001 per tick toward a target), so it cannot be derived and must travel.
        if (condition is GameCondition_HeatVentHeat or GameCondition_UnnaturalHeat)
            return "t" + FloatField(condition, "tempOffset");

        // Aurora family. `CurrentColor => Color.Lerp(Colors[prevColorIndex], Colors[curColorIndex], curColorTransition)`
        // (`:381718`) feeds `SkyTarget` (`:381800`), i.e. the actual sky colour over the map. All three inputs are
        // non-derivable: `Init` picks the indices with `Rand.Range(0, Colors.Length)` (`:381777`) and the transition only
        // moves inside `GameConditionTick` (`:381811`). A replica that merely re-registers the condition therefore rolls a
        // *different* aurora than the host, and the map is tinted the wrong colour for the whole condition.
        // `GameCondition_BioluminescentSpores` shares the same three fields under the same names (`:381852`).
        if (condition is GameCondition_Aurora or GameCondition_BioluminescentSpores)
            return "a" + IntField(condition, "prevColorIndex") + "," + IntField(condition, "curColorIndex")
                + "," + FloatField(condition, "curColorTransition");

        // Volcanic debris. `Expired => remainingLavaRockCount <= 0` (`:383441`), and the scribe default is 0 (`:383475`), so
        // a replica's condition reads as already finished from the moment it is created - the shower ends before it starts.
        if (condition is GameCondition_VolcanicDebrisShower)
            return "v" + IntField(condition, "remainingLavaRockCount");
        return null;
    }

    private static string IntField(object target, string name)
    {
        var info = AccessTools.Field(target.GetType(), name);
        return info?.GetValue(target) is int value
            ? value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string FloatField(object target, string name)
    {
        var info = AccessTools.Field(target.GetType(), name);
        return info?.GetValue(target) is float value
            ? value.ToString("R", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Creates a condition that exists on the authority and not here, or writes the values of one that does.
    ///
    /// <para>Creation goes through <c>GameConditionMaker.MakeCondition</c> (the game's own factory, which allocates the id
    /// and calls <c>PostMake</c>) and then <c>RegisterCondition</c> - unavoidable, because there is no other way to put a
    /// condition into <c>activeConditions</c>. That is why the derived blob is written <em>after</em> registration: the
    /// registration's own <c>Init()</c> is exactly what would overwrite it.</para></summary>
    internal static void Apply(ReplicaGameCondition state)
    {
        try
        {
            var map = Find.Maps?.FirstOrDefault(candidate => candidate.uniqueID == state.Map);
            var manager = map?.gameConditionManager;
            if (manager is null) return;
            GameCondition? condition = null;
            foreach (var existing in manager.ActiveConditions)
                if (existing?.uniqueID == state.Id) { condition = existing; break; }
            if (condition is null)
            {
                var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(state.Def);
                if (def is null) return;
                condition = GameConditionMaker.MakeCondition(def, state.Duration);
                condition.startTick = state.StartTick;
                condition.Permanent = state.Permanent;
                manager.RegisterCondition(condition);
                ConditionsCreated++;
            }
            // Written for both paths: on the creation path the registration already reset them, on the update path the
            // authority has moved them and this peer's tick never will.
            condition.startTick = state.StartTick;
            // Order matters: `Duration`'s setter sets `permanent = false` as a side effect, so a permanent condition must have
            // its `Permanent` flag written *after* the duration. That is also why a permanent one skips the duration
            // entirely - reading `Duration` on a permanent condition logs an error and invents 360000000.
            if (!state.Permanent) condition.Duration = state.Duration;
            condition.Permanent = state.Permanent;
            WriteDerived(condition, state.Derived);
            ConditionsApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    /// <summary>Writes back whatever <see cref="Derived"/> encoded, dispatching on its first character so one string can
    /// carry any concrete condition's fields without a per-type record on the wire.</summary>
    private static void WriteDerived(GameCondition condition, string? derived)
    {
        if (derived is not { Length: > 1 }) return;
        var body = derived.AsSpan(1);
        switch (derived[0])
        {
            case 't':
            {
                if (!float.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
                AccessTools.Field(condition.GetType(), "tempOffset")?.SetValue(condition, value);
                return;
            }
            case 'a':
            {
                // Order is the reader's: `CurrentColor` lerps prev -> cur by the transition, so all three are needed and a
                // partial write would lerp from a colour the host never showed.
                var parts = body.ToString().Split(',');
                if (parts.Length != 3) return;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prev)) return;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cur)) return;
                if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var transition)) return;
                AccessTools.Field(condition.GetType(), "prevColorIndex")?.SetValue(condition, prev);
                AccessTools.Field(condition.GetType(), "curColorIndex")?.SetValue(condition, cur);
                AccessTools.Field(condition.GetType(), "curColorTransition")?.SetValue(condition, transition);
                return;
            }
            case 'v':
            {
                if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) return;
                AccessTools.Field(condition.GetType(), "remainingLavaRockCount")?.SetValue(condition, count);
                return;
            }
        }
    }

    /// <summary>Ends a condition the authority has ended.
    ///
    /// <para>Not <c>End()</c>: that sends the def's end message, and the letter channel already carries the authority's own
    /// copy - the player would read it twice. <c>OnConditionEnd</c> is the registration's counterpart and does the part that
    /// matters here (drop it from the list, notify the map), and the outdoor temperature cache is invalidated separately
    /// because it is only refreshed on a 60-tick boundary the replica never reaches.</para></summary>
    internal static void Remove(int mapId, int id)
    {
        try
        {
            var map = Find.Maps?.FirstOrDefault(candidate => candidate.uniqueID == mapId);
            var manager = map?.gameConditionManager;
            if (manager is null) return;
            for (var i = manager.ActiveConditions.Count - 1; i >= 0; i--)
            {
                var existing = manager.ActiveConditions[i];
                if (existing?.uniqueID != id) continue;
                if (existing.suppressEndMessage) { manager.OnConditionEnd(existing); }
                else
                {
                    // Suppress the end message for this one call rather than skipping `End`: subclasses override `End` to
                    // clean up their own state, and `OnConditionEnd` alone would leak that.
                    existing.suppressEndMessage = true;
                    existing.End();
                }
                ConditionsRemoved++;
                break;
            }
            InvalidateOutdoorCache();
        }
        catch (Exception) { ApplyFailures++; }
    }

    /// <summary>Forces the outdoor temperature cache to recompute now.
    ///
    /// <para>`CachedTileTemperatureData.CheckCache` only runs from `WorldComponentTick`, and it is where
    /// `AggregateTemperatureOffset()` is added to the tile reading. A condition that starts or ends on the authority would
    /// otherwise take up to a full cache interval to show up here, and every indoor room is equalised against that number.
    /// The field is private and there is no public invalidation, so it is cleared by reflection.</para></summary>
    private static void InvalidateOutdoorCache()
    {
        try
        {
            // `TileTemperaturesComp.ClearCaches` is public, so no reflection is needed - only the fact that nothing calls it
            // when a condition changes, which is the whole reason this method exists.
            Find.World?.tileTemperatures?.ClearCaches();
        }
        catch (Exception) { }
    }
}
