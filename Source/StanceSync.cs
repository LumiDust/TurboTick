using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The concrete busy stance a replica installs.
///
/// <para><b>Why a class of our own rather than a vanilla subclass.</b> `Stance_Busy` is abstract (`:138368`), and every
/// vanilla subclass carries behaviour a display replica must not run: <c>Stance_Warmup</c>'s constructor walks the target's
/// apparel for shields, spawns an aiming sustainer, an effecter and three motes (`:138480` onwards), and
/// <c>Stance_Cooldown</c> draws a cooldown circle. All of that is host-side behaviour whose outcome the replica is being
/// told about, not something it should start again.</para>
///
/// <para>`Stance` itself has no abstract member - `StanceBusy`, `StanceTick`, `StanceDraw` and `ExposeData` are all
/// `virtual` (`:138345` area) - so a subclass that only overrides `StanceBusy` is a complete implementation. Overriding
/// `StanceTick` to do nothing is deliberate: the replica installs the remaining ticks from the authority and must not
/// count them down itself, or the number would drift against the value the next frame carries.</para></summary>
internal sealed class ReplicaBusyStance : Stance_Busy
{
    public ReplicaBusyStance() { }

    public ReplicaBusyStance(int ticks) : base(ticks) { }

    /// <summary>A replica's stance is a display, not a simulation: the authority decides when it ends, and this peer is
    /// told. Counting down here would double-advance the number the host already advanced.</summary>
    public override void StanceTick() { }

    /// <summary>No aim pie and no cooldown ring. Those are drawn from `StanceDraw` on the authority over its own verb and
    /// sustainer objects, which this replica does not have; the weapon-aim and facing logic in
    /// `PawnRenderUtility.DrawEquipmentAndApparelExtras` and `Pawn_RotationTracker` reads `focusTarg` directly and is
    /// unaffected by this override.</summary>
    public override void StanceDraw() { }
}

/// <summary>A pawn's busy stance, stun and stagger, for a replica whose stance tracker never leaves its default.
///
/// <para><b>The defect.</b> `Pawn_StanceTracker.curStance` starts as `new Stance_Mobile()` (`:138231`), and only the
/// tracker's own tick advances or replaces it (`StanceTrackerTick`, which also drives `StunHandler.Tick` and
/// `StaggerHandler.Tick`). A replica never runs that tick, so every pawn here is permanently mobile, never stunned and never
/// staggered - regardless of what the authority shows. Three separate things break, all from that one field:</para>
///
/// <list type="bullet">
/// <item><c>PawnRenderUtility.DrawEquipmentAndApparelExtras</c> (`:107897`) aims the weapon only when
/// <c>curStance is Stance_Busy stance_Busy &amp;&amp; !stance_Busy.neverAimWeapon &amp;&amp; stance_Busy.focusTarg.IsValid</c>.
/// A colonist firing on the authority stands with the weapon at rest here.</item>
/// <item><c>Pawn_RotationTracker.UpdateRotation</c> (`:138050`) faces the target only through the same cast, so a shooter
/// keeps facing wherever its last received `Rot4` put it.</item>
/// <item><c>PawnUtility.IsAttacking</c> (`:452622`) answers from the same cast, and the inspect pane prints
/// <c>StunnedByEMP</c>/<c>StunLower</c>/<c>SlowedByDamage</c> from the other two handlers (`:129948-129965`).</item>
/// </list>
///
/// <para><b>Why the payload is four small numbers and not the stance object.</b> A stance is live behaviour: `Warmup`,
/// `Busy`, `Busy_Defend` and a dozen subclasses each hold verbs, sustainers and target infos, and constructing one on a
/// replica runs whatever its constructor does (`Stance_Busy`'s constructor reads `Find.TickManager.TicksGame`, which is
/// correct here, but subclasses do more). What a player can see is decided by three things only: whether the pawn is busy,
/// which way it faces, and what the inspect pane prints. So the wire carries `ticksLeft`, the focus target and the two
/// counter fields, and the replica installs a plain `Stance_Busy` - the same choice `MentalStateSync` makes for the same
/// reason.</para>
///
/// <para><b>The three are one channel because they are one field to the renderer.</b> `FullBodyBusy` is
/// <c>!Stunned &amp;&amp; curStance.StanceBusy</c> (`:138249`) - the game itself merges them, so sending them separately would
/// let a replica reach a combination the game cannot.</para></summary>
internal static class StanceSync
{
    /// <summary>Last encoded stance per pawn, so an unchanged stance costs nothing.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>Remaining busy ticks are quantised to this. A stance lasts tens to hundreds of ticks and the number is read
    /// by `SetPieSizeFactor` at three thresholds (300 and 450), so a coarse step cannot cross one unnoticed.</summary>
    private const int TickQuantum = 15;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    /// <summary>Pawns currently busy, stunned or staggered. Read by the census so "0" can be told from "the probe never
    /// ran".</summary>
    internal static int BusyCount { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
        BusyCount = 0;
    }

    /// <summary>Encodes <c>busyTicks|focusKind:focusValue|stunTicks|staggerTicks|neverAim</c> for the pawns that have
    /// something to say, or null for the ones that do not.
    ///
    /// <para>A pawn that is mobile, unstunned and unstaggered produces nothing at all - not a row of zeros - because the
    /// replica's default already is exactly that. That is what keeps this channel at a handful of rows per colony instead of
    /// one per pawn per frame.</para>
    /// </summary>
    internal static List<ReplicaStance>? Publish()
    {
        List<ReplicaStance>? changed = null;
        try
        {
            var live = new HashSet<int>();
            var busy = 0;
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var stances = pawn?.stances;
                    if (stances is null) continue;
                    var id = pawn!.thingIDNumber;
                    var encoded = Encode(stances);
                    if (encoded is not null) busy++;
                    if (encoded is null)
                    {
                        // A pawn that just stopped being busy must be told once, then forgotten: leaving a stale entry in
                        // `sent` would make the next real stance look unchanged and never be sent.
                        if (sent.Remove(id)) (changed ??= []).Add(new ReplicaStance(id, string.Empty));
                        continue;
                    }
                    live.Add(id);
                    if (sent.TryGetValue(id, out var previous) && previous == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaStance(id, encoded));
                    StatesSent++;
                }
            // Pawns that left the map keep no entry. The body above already removes the ones that stopped being busy;
            // this removes the ones that died or despawned while still busy, which nothing else would ever revisit - the
            // dictionary would otherwise grow by one entry per death for the life of the process.
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
            BusyCount = busy;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 姿态发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    private static string? Encode(Pawn_StanceTracker stances)
    {
        var stance = stances.curStance;
        var isBusy = stance is Stance_Busy;
        var stun = stances.stunner?.StunTicksLeft ?? 0;
        var stagger = stances.stagger?.StaggerTicksLeft ?? 0;
        if (!isBusy && stun <= 0 && stagger <= 0) return null;
        var busy = stance as Stance_Busy;
        var ticks = 0;
        if (busy is not null)
        {
            ticks = busy.ticksLeft;
            // Quantised, but never to zero while the stance is still running: a busy stance whose remaining ticks rounded
            // away would be indistinguishable from a stance that just ended.
            ticks = ticks / TickQuantum * TickQuantum;
            if (ticks <= 0) ticks = TickQuantum;
        }
        // The focus target decides which way the pawn faces and whether the weapon is aimed, so it travels as a
        // cell or as a thing id - the same two shapes `Job.targetA` uses.
        var focus = "-";
        if (busy?.focusTarg is { IsValid: true } target)
            focus = target.HasThing
                ? "t" + (target.Thing?.thingIDNumber ?? -1).ToString(CultureInfo.InvariantCulture)
                : "c" + target.Cell.x.ToString(CultureInfo.InvariantCulture) + ","
                    + target.Cell.z.ToString(CultureInfo.InvariantCulture);
        return (isBusy ? "B" : "-")
            + (busy?.neverAimWeapon == true ? "n" : "-")
            + ":" + ticks.ToString(CultureInfo.InvariantCulture)
            + ":" + focus
            + ":" + stun.ToString(CultureInfo.InvariantCulture)
            + ":" + stagger.ToString(CultureInfo.InvariantCulture);
    }

    internal static void Apply(ReplicaStance state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var stances = pawn?.stances;
            if (stances is null) return;
            var fields = state.Values.Split(':');
            if (fields.Length != 5) { ApplyFailures++; return; }
            var flags = fields[0];
            var busy = flags.Length > 0 && flags[0] == 'B';
            var neverAim = flags.Length > 1 && flags[1] == 'n';
            _ = int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
            _ = int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stun);
            _ = int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stagger);
            // Written by reflection rather than through `StunFor`/`StaggerFor`: those take a fresh duration and would
            // restart the countdown, and `StunFor` also records a battle log entry the authority already recorded.
            stunField ??= HarmonyLib.AccessTools.Field(typeof(StunHandler), "stunTicksLeft");
            staggerField ??= HarmonyLib.AccessTools.Field(typeof(StaggerHandler), "staggerTicksLeft");
            stunField?.SetValue(stances.stunner, stun);
            staggerField?.SetValue(stances.stagger, stagger);
            var current = stances.curStance;
            if (!busy)
            {
                // Back to mobile. Not `SetStance(new Stance_Mobile())` unconditionally: that allocates every frame for the
                // common case of a pawn that was never busy.
                if (current is Stance_Busy) stances.SetStance(new Stance_Mobile());
                StatesApplied++;
                return;
            }
            // Rebuilt only when the busy values differ. `Stance_Busy` holds a verb and a target info; rebuilding it every
            // frame would discard the pie size factor the constructor computes and re-run it for nothing.
            if (current is not Stance_Busy busyStance)
            {
                busyStance = new ReplicaBusyStance(ticks) { neverAimWeapon = neverAim };
                // `SetStance` rather than assigning the field: it is what wires `newStance.stanceTracker`, and
                // `Stance_Busy.StanceTick` dereferences that on its first line. Assigning the field directly leaves a
                // stance whose tracker is null - measured on the probe, where the census read a live stance but nothing
                // that walked it could proceed.
                stances.SetStance(busyStance);
            }
            else
            {
                busyStance.ticksLeft = ticks;
                busyStance.neverAimWeapon = neverAim;
            }
            busyStance.focusTarg = ParseFocus(fields[2]);
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    private static LocalTargetInfo ParseFocus(string encoded)
    {
        if (encoded.Length < 2 || encoded == "-") return LocalTargetInfo.Invalid;
        if (encoded[0] == 't')
        {
            if (!int.TryParse(encoded.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || id < 0) return LocalTargetInfo.Invalid;
            var thing = AuthoritativeReplica.FindThing(id);
            return thing is null ? LocalTargetInfo.Invalid : new LocalTargetInfo(thing);
        }
        if (encoded[0] != 'c') return LocalTargetInfo.Invalid;
        var comma = encoded.IndexOf(',');
        if (comma < 0) return LocalTargetInfo.Invalid;
        if (!int.TryParse(encoded.AsSpan(1, comma - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(encoded.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
            return LocalTargetInfo.Invalid;
        return new LocalTargetInfo(new IntVec3(x, 0, z));
    }

    private static System.Reflection.FieldInfo? stunField;
    private static System.Reflection.FieldInfo? staggerField;

    /// <summary>The live reading plus the encoded payload of every busy pawn, so the census can show that the numbers are
    /// real rather than only that the channel fired.</summary>
    internal static string Preview()
    {
        try
        {
            var busy = 0;
            var stunned = 0;
            var staggered = 0;
            var samples = new List<string>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var stances = pawn?.stances;
                    if (stances is null) continue;
                    if (stances.curStance is Stance_Busy stance)
                    {
                        busy++;
                        if (samples.Count < 3)
                            samples.Add(pawn!.thingIDNumber + ":" + stance.ticksLeft.ToString(CultureInfo.InvariantCulture)
                                + (stance.focusTarg.IsValid ? "→" + stance.focusTarg : "/无目标"));
                    }
                    if ((stances.stunner?.StunTicksLeft ?? 0) > 0) stunned++;
                    if ((stances.stagger?.StaggerTicksLeft ?? 0) > 0) staggered++;
                }
            if (busy == 0 && stunned == 0 && staggered == 0)
                return "0 个忙姿 / 0 个眩晕 / 0 个失衡（全图没有攻击或受伤中的小人） / 发 " + StatesSent;
            return $"{busy} 个忙姿 / {stunned} 个眩晕 / {staggered} 个失衡"
                + (samples.Count == 0 ? "" : " / " + string.Join("，", samples))
                + $" / 发 {StatesSent}";
        }
        catch (Exception error) { return "读取失败：" + error.GetType().Name; }
    }
}
