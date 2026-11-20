using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>Letters and messages the host raised, rebuilt on a replica that will never raise them itself.
///
/// <para><b>Why this channel has to exist.</b> Everything a raid, a disease, a wanderer or a quest does is decided
/// inside a tick. A display replica runs no tick (see <see cref="AuthoritativeReplica.Frozen"/>), so
/// <c>LetterStack.ReceiveLetter</c> is never called on it and the player watching the colony sees a colony under
/// attack with no letter, no message and no red text - the event happened, the picture moved, and nothing said so.
/// The channel is not "nice to have": it is the only way a player learns about anything the host decided.</para>
///
/// <para><b>Why the letters are not carried as Scribe fragments.</b> <c>Letter.ExposeData</c> writes
/// <c>lookTargets</c> as <c>LookMode.GlobalTargetInfo</c>, which names things by their load id and resolves them
/// against the peer's own object table. A letter arrives on a replica that has <i>just</i> received the thing it
/// points at, and the frame that carries the letter and the frame that creates the thing are not the same frame.
/// Decoding would therefore half-succeed - a letter with no jump target, or none at all - and a letter nobody can
/// click is worse than the structured path below. Instead the fields that decide what the player sees are carried
/// as plain values and the object is rebuilt through <c>LetterMaker</c>, exactly as vanilla builds it.</para>
///
/// <para><b>What is deliberately not carried.</b> <c>ChoiceLetter</c> subclasses hold behaviour: a
/// <c>ChoiceLetter_AcceptJoiner</c> knows which pawn it is about and what accepting does. Rebuilding one requires
/// its own state, and the host-side accept would have to be routed back as a command. Those letters are delivered
/// as a <see cref="StandardLetter"/> with the same label, text and definition - the player reads what happened and
/// dismisses it, and the decision stays on the host where the simulation is. Reported once per letter class so the
/// gap is visible rather than silent.</para>
internal static class LetterSync
{
    /// <summary>Letters already sent, by the host's letter id. Bounded: a colony archives tens of letters, not
    /// thousands, and an id that leaves this set only costs one duplicate letter if it ever returned - it cannot,
    /// because letter ids are never reused (<c>UniqueIDsManager.GetNextLetterID</c>).</summary>
    private static readonly HashSet<int> sentLetters = [];

    /// <summary>One entry per message the host showed, keyed by its tick and text so the same message shown on two
    /// frames is not shown twice.</summary>
    private static readonly HashSet<string> sentMessages = [];

    internal static int LettersSent { get; private set; }
    internal static int LettersApplied { get; private set; }
    internal static int LettersDowngraded { get; private set; }

    /// <summary>Choice letters rebuilt from the host's own fragment, so the player could answer them here.</summary>
    internal static int ChoiceLettersRebuilt { get; private set; }
    internal static int MessagesSent { get; private set; }
    internal static int MessagesApplied { get; private set; }
    /// <summary>Messages that also reached `Find.Archive` on this end. Counted apart from `MessagesApplied` because
    /// "arrived on screen" and "entered the history tab" are different claims, and the history tab was the half that
    /// never happened (round 163).</summary>
    internal static int MessagesArchived { get; private set; }

    /// <summary>Letter classes already reported as delivered in a reduced form. One line each, not one per letter:
    /// a raid that spawns thirty joiners would otherwise print thirty warnings about the same class.</summary>
    private static readonly HashSet<string> downgradesReported = [];

    /// <summary>Collects letters and messages the host raised since the last frame.
    ///
    /// <para>Called once per frame on the authority. <c>Find.LetterStack.LettersListForReading</c> is the live stack
    /// and holds a handful of entries - a raid, a death, a quest - so the per-frame cost is one int hash lookup per
    /// letter, and nothing at all when the stack is unchanged.</para></summary>
    internal static (List<ReplicaLetter>? Letters, List<ReplicaMessage>? Messages) Publish()
    {
        List<ReplicaLetter>? letters = null;
        List<ReplicaMessage>? messages = null;
        try
        {
            var stack = Find.LetterStack;
            var live = stack?.LettersListForReading;
            if (live is not null)
                for (var i = 0; i < live.Count; i++)
                {
                    var letter = live[i];
                    if (letter is null) continue;
                    // `ID` is assigned by `LetterMaker` from the host's own counter, so it is unique across the
                    // colony's whole life and a stable key. A letter whose id is still 0 was built by hand rather
                    // than through the maker; it cannot be deduplicated, so it is sent and the replica's own id is
                    // whatever the maker gives it there.
                    if (letter.ID != 0 && !sentLetters.Add(letter.ID)) continue;
                    var projection = Describe(letter);
                    if (projection is null) continue;
                    (letters ??= []).Add(projection);
                    LettersSent++;
                }
            messages = PublishMessages();
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] letter publish: " + error.GetType().Name + ": " + error.Message);
        }
        return (letters, messages);
    }

    /// <summary>The messages currently on screen, as the host shows them.
    ///
    /// <para>Unlike letters, <c>Messages</c> keeps only the live list plus a per-tick history, and its entries have no
    /// id. The key is therefore the tick and the text together: the same text at a different tick is a different
    /// event ("Raiders are attacking" twice in one raid is two messages), and the same tick with the same text is
    /// the same message seen on two frames.</para></summary>
    private static List<ReplicaMessage>? PublishMessages()
    {
        // `Messages` exposes no read accessor for the live list - only `IsLive(msg)` for an instance the caller
        // already holds - so the field is read directly. It is a `static List<Message>` initialised inline and
        // never reassigned, and `Message` exposes `text`, `def` and `lookTargets` publicly.
        var live = LiveMessages;
        if (live is null || live.Count == 0) return null;
        var tick = Find.TickManager?.TicksGame ?? 0;
        List<ReplicaMessage>? messages = null;
        foreach (var message in live)
        {
            if (message is null || string.IsNullOrEmpty(message.text)) continue;
            var key = tick.ToString(CultureInfo.InvariantCulture) + "\u001f" + message.text;
            if (!sentMessages.Add(key)) continue;
            // The target cell, not only the fact that one existed: a message with a dead click target is a message the
            // replica cannot use, and `IsValid` says nothing about where it pointed. `Historical` comes from the capture
            // patch, because that flag is a parameter of `Messages.Message` and is not stored on the message.
            var hasCell = false;
            var cellMap = 0;
            var cellX = 0;
            var cellZ = 0;
            if (message.lookTargets.IsValid)
            {
                var primary = message.lookTargets.PrimaryTarget;
                // A target that is a thing is not carried: the replica may not have that object, and a dead target is
                // worse than none. A cell target always resolves by map id and position.
                if (primary.IsValid && !primary.HasThing && primary.Map is not null)
                {
                    hasCell = true;
                    cellMap = primary.Map.uniqueID;
                    cellX = primary.Cell.x;
                    cellZ = primary.Cell.z;
                }
            }
            (messages ??= []).Add(new ReplicaMessage(message.text,
                message.def?.defName ?? "NeutralEvent", message.lookTargets.IsValid,
                // -1 and not 0: `Map.uniqueID` is legitimately 0 for a game's first map, so 0 cannot mean "no map".
                hasCell ? cellMap : -1,
                hasCell ? cellX : 0,
                hasCell ? cellZ : 0,
                MessageHistoryCapturePatch.Historical(message)));
            MessagesSent++;
        }
        // The host keeps a bounded history, so keys for messages that scrolled away can never match again. Trimmed
        // to twice the live list so a quiet colony's set does not grow for the whole session.
        if (sentMessages.Count > 128)
        {
            sentMessages.Clear();
            foreach (var message in live)
                if (message?.text is { Length: > 0 } text)
                    sentMessages.Add(tick.ToString(CultureInfo.InvariantCulture) + "\u001f" + text);
        }
        return messages;
    }

    /// <summary>One letter as plain values.
    ///
    /// <para><paramref name="Choices"/> marks a letter whose class the replica cannot rebuild honestly; it arrives as
    /// <c>StandardLetter</c> and this flag is only used to decide whether to report the downgrade.</para></summary>
    private static ReplicaLetter? Describe(Letter letter)
    {
        var defName = letter.def?.defName;
        if (defName is not { Length: > 0 }) return null;
        var label = letter.Label.RawText;
        var text = letter is ChoiceLetter choice ? choice.Text.RawText : string.Empty;
        var choices = letter is ChoiceLetter and not StandardLetter;
        // Look targets are reduced to a cell and a map. A `GlobalTargetInfo` that names a Thing cannot travel: the
        // replica resolves references against its own object table and the thing may not be created yet. The cell is
        // what the "jump to" button needs, and it is a value the replica cannot invent.
        var hasTarget = false;
        int x = 0, z = 0, map = 0;
        try
        {
            var targets = letter.lookTargets;
            if (targets is not null && targets.IsValid)
            {
                var primary = targets.PrimaryTarget;
                if (primary.IsValid)
                {
                    hasTarget = true;
                    x = primary.Cell.x;
                    z = primary.Cell.z;
                    // -1, not 0: map 0 is a real map (see `ReplicaMessage`).
                    map = primary.Map?.uniqueID ?? -1;
                }
            }
        }
        catch (Exception) { hasTarget = false; }
        // A choice letter carries its whole Scribe fragment so the replica can rebuild the real subclass and let the
        // player answer it there. `null` for the rest: a readable letter has nothing to answer, and a fragment per
        // letter would be the larger half of the channel for no gain.
        string? fragment = null;
        if (choices)
        {
            fragment = ScribeBridge.SaveOrNull(letter);
            if (fragment is { Length: > MaxFragment })
            {
                Log.Warning($"[TurboTick] 抉择信件 {defName} 片段 {fragment.Length} 字符，超过上限，退回只读");
                fragment = null;
            }
        }
        return new ReplicaLetter(letter.ID, defName, label, text, letter.arrivalTick, choices,
            hasTarget, x, z, map, fragment);
    }

    /// <summary>Rebuilds one letter on the replica. Called from the frame loop, on the main thread, inside the same
    /// pass that applies pawns and things - so a letter that points at a colonist does so after that colonist exists
    /// whenever both arrived in the same frame.</summary>
    internal static void Apply(ReplicaLetter state)
    {
        try
        {
            if (state.Id != 0 && !appliedLetters.Add(state.Id)) return;
            var def = DefDatabase<LetterDef>.GetNamedSilentFail(state.Def);
            if (def is null)
            {
                Log.Warning("[TurboTick] 收到未知信件类型 " + state.Def + "，跳过（标题：" + state.Label + "）");
                return;
            }
            // A letter whose class cannot be rebuilt (a `ChoiceLetter_*` holding the pawn it is about) arrives as a
            // plain read-only letter. Built through the maker so it gets an id from this peer's own counter and
            // behaves exactly like a vanilla letter from then on.
            Letter letter;
            // A fragment means the host sent the real letter's saved state, so the real subclass is rebuilt and the
            // player can answer it here. `LetterMaker.MakeLetter(LetterDef)` instantiates `def.letterClass` — the same
            // factory the host used — and `ScribeBridge.Load` fills it from the host's own `ExposeData`. A choice letter
            // whose fragment is missing (too large, or Scribe was busy when it was sampled) still falls through to the
            // read-only path below rather than being dropped, so nothing disappears from the stack.
            if (state.Choices && state.Fragment is { Length: > 0 }
                && typeof(ChoiceLetter).IsAssignableFrom(def.letterClass))
            {
                var rebuilt = LetterMaker.MakeLetter(def);
                if (rebuilt is ChoiceLetter && ScribeBridge.Load(rebuilt, state.Fragment, LetterReferences(rebuilt)))
                {
                    letter = rebuilt;
                    ChoiceLettersRebuilt++;
                    // The host's id is kept: `appliedLetters` is keyed by it, and `RemoveLetter` on the host side later
                    // refers to the same one. Two peers in one process during a local test would otherwise collide.
                    letter.ID = state.Id != 0 ? state.Id : letter.ID;
                    FinishLetter(letter, state, def);
                    return;
                }
                // A failed load leaves a half-filled object; it is dropped, not shown.
                Log.Warning("[TurboTick] 抉择信件 " + state.Def + " 片段无法应用，退回只读");
            }
            if (state.Choices && !typeof(StandardLetter).IsAssignableFrom(def.letterClass))
            {
                var plain = DefDatabase<LetterDef>.GetNamedSilentFail("ThreatBig") ?? def;
                if (!downgradesReported.Add(state.Def))
                    Log.Message("[TurboTick] 信件类型 " + state.Def + "（" + def.letterClass?.Name
                        + "）需要本地抉择状态，副本以只读信件呈现；选项请到主机端操作。");
                letter = LetterMaker.MakeLetter(state.Label, state.Text, plain);
                LettersDowngraded++;
            }
            else
            {
                letter = LetterMaker.MakeLetter(state.Label, state.Text, def);
            }
            if (letter is null) { appliedLetters.Remove(state.Id); return; }
            FinishLetter(letter, state, def);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] letter apply failed (" + state.Def + "): " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Positions the rebuilt letter and puts it on the stack.
    ///
    /// <para>Straight onto the stack rather than through `ReceiveLetter`: the other overloads pause the game for
    /// `LetterDef.pauseMode` and signal a speed change, and a replica must not pause on a frame it received. The stack
    /// itself is what draws the button - and for a rebuilt choice letter, the button opens the real dialog.</para></summary>
    private static void FinishLetter(Letter letter, ReplicaLetter state, LetterDef def)
    {
            letter.arrivalTick = state.ArrivalTick;
            if (state.HasTarget && state.Map >= 0)
            {
                var targetMap = Find.Maps?.FirstOrDefault(m => m.uniqueID == state.Map);
                var cell = new IntVec3(state.X, 0, state.Z);
                if (targetMap is not null && cell.InBounds(targetMap)) letter.lookTargets = new LookTargets(cell, targetMap);
            }
            // Straight onto the stack rather than through `ReceiveLetter`: the other overloads pause the game for
            // `LetterDef.pauseMode` and signal a speed change, and a replica must not pause on a frame it received.
            // The stack itself is what draws the button.
            var stack = Find.LetterStack;
            if (stack is null) { appliedLetters.Remove(state.Id); return; }
            // The sound is played here rather than left to vanilla: `ReceiveLetter` only plays it when its own
            // `playSound` argument is true, and the guard below suppresses that argument for anything arriving from a
            // replica. Whether a letter is worth a sound is the definition's own decision - `arriveSound` is null for
            // the quiet ones - so the definition is asked instead of inventing a rule.
            var audible = def.arriveSound is not null;
            ReplicaLetterGuard.Delivering = true;
            try { stack.ReceiveLetter(letter, null, 0, audible); }
            finally { ReplicaLetterGuard.Delivering = false; }
            LettersApplied++;
    }

    /// <summary>Letter ids this peer has shown. Separate from <see cref="sentLetters"/> because the two sides run the
    /// same static in one process during a local test, and the authority's "already sent" set must not suppress the
    /// replica's "already shown" check.</summary>
    private static readonly HashSet<int> appliedLetters = [];

    /// <summary>Upper bound on a letter fragment. `traitChoices` is `LookMode.Deep` and a growth moment carries three
    /// of them plus two label lists, so this is well above anything measured; it exists so one pathological letter
    /// cannot put a megabyte on the command channel.</summary>
    private const int MaxFragment = 32768;

    /// <summary>Everything outside the fragment that its <c>Scribe_References.Look</c> calls may name.
    ///
    /// <para>`ChoiceLetter_GrowthMoment.pawn` (`:662310`), `ChoiceLetter_ChoosePawn.pawns` and the `pawns`/`overrideMap`
    /// of the joiner letters are references to objects that existed before the fragment was taken, so they are in no
    /// fragment and in no directory. Without this list the load reports success and leaves those fields null.</para>
    ///
    /// <para>The list is collected from the live game objects rather than from a type switch: reading every spawned and
    /// world pawn covers whatever a mod's letter points at, and a pawn that is not on any map (a world pawn, a
    /// prisoner in a far colony) is the case a map-only walk would miss.</para></summary>
    private static IEnumerable<ILoadReferenceable> LetterReferences(Letter letter)
    {
        foreach (var map in Find.Maps)
        {
            var pawns = map.mapPawns?.AllPawns;
            if (pawns is null) continue;
            for (var i = 0; i < pawns.Count; i++)
                if (pawns[i] is { } pawn) yield return pawn;
        }
        var world = Find.WorldPawns?.AllPawnsAliveOrDead;
        if (world is not null)
            for (var i = 0; i < world.Count; i++)
                if (world[i] is { } pawn) yield return pawn;
    }

    private static System.Reflection.FieldInfo? liveField;

    /// <summary>The live message list, read through the field because <c>Messages</c> publishes no accessor for it.
    /// A failure here costs the message channel, not the frame: the caller treats null as "nothing to say".</summary>
    /// <summary>The live list, for the history-flag capture's trim pass. Same accessor, so the two halves cannot
    /// disagree about what "live" means.</summary>
    internal static List<Message>? LiveMessagesForTrim => LiveMessages;

    private static List<Message>? LiveMessages
    {
        get
        {
            try
            {
                liveField ??= AccessTools.Field(typeof(Messages), "liveMessages");
                return liveField?.GetValue(null) as List<Message>;
            }
            catch (Exception) { return null; }
        }
    }

    internal static void Apply(ReplicaMessage state)
    {
        try
        {
            var def = DefDatabase<MessageTypeDef>.GetNamedSilentFail(state.Def) ?? MessageTypeDefOf.NeutralEvent;
            // `historical` is not decoration: `Messages.Message(Message, bool)` (`:185729`) adds the message to
            // `Find.Archive` when it is true and keeps it on the live list only when it is false. Sending everything as
            // `false` (as this did) left the replica's history tab missing every message the authority had archived,
            // while the two live lists looked identical - the reading "messages arrive" was true and the divergence was
            // invisible.
            var historical = state.Historical;
            // The look target, so clicking the message moves the camera to the same place it does on the host. A target
            // that is a thing (not a cell) is not carried: the replica may not have that object, and a dead target is
            // worse than none.
            LookTargets? look = null;
            if (state.HasTarget && state.Map >= 0)
            {
                var map = Find.Maps?.FirstOrDefault(candidate => candidate.uniqueID == state.Map);
                var cell = new IntVec3(state.X, 0, state.Z);
                if (map is not null && cell.InBounds(map)) look = new LookTargets(cell, map);
            }
            var message = look is null
                ? new Message(state.Text, def)
                : new Message(state.Text, def, look);
            Messages.Message(message, historical);
            MessagesApplied++;
            if (historical && Find.Archive is not null && Find.Archive.Contains(message)) MessagesArchived++;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] message apply failed: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Forgets everything the host has told a peer. Called when a replica starts over from a new baseline,
    /// because the new stack is loaded from the save and the old ids mean nothing to it.</summary>
    internal static void Reset()
    {
        sentLetters.Clear();
        sentMessages.Clear();
        appliedLetters.Clear();
    }

    internal static string Summary() =>
        $"信件 发出 {LettersSent} / 应用 {LettersApplied}（重建抉择 {ChoiceLettersRebuilt}，降级 {LettersDowngraded}），消息 发出 {MessagesSent} / 应用 {MessagesApplied}（入档 {MessagesArchived}）";
}

/// <summary>Replica-side backstop for <c>LetterStack.ReceiveLetter</c>.
///
/// <para>A replica runs no tick, so vanilla never raises a letter there - the channel above is the only source. This
/// patch exists for the opposite case: a letter raised by a third-party mod's code that runs on the replica anyway
/// (a comp ticked during the checkpoint load, a GUI action, a Harmony patch of someone else's). Such a letter is not
/// the authority's, and the sound it plays is a false signal - measured: a replica raised "Raiders are attacking"
/// during load for a raid that was already over, and the player heard the alert.</para>
///
/// <para>Only the sound is suppressed, not the letter: suppressing the object would mean a mod's own feature breaks
/// silently, while a suppressed sound is exactly the part that misinforms.</para>
[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), [typeof(Letter), typeof(string), typeof(int), typeof(bool)])]
internal static class ReplicaLetterGuard
{
    /// <summary>Set while <see cref="LetterSync.Apply(ReplicaLetter)"/> is on the stack. A prefix cannot tell the
    /// channel's own delivery from a local one by inspecting arguments, so the flag is the discriminator. The channel
    /// passes <c>playSound = true</c> only when the host's letter actually arrived with a sound.</summary>
    internal static bool Delivering;

    private static void Prefix(ref bool playSound)
    {
        if (Delivering) return;
        if (!AuthoritativeReplica.Frozen) return;
        playSound = false;
    }
}
