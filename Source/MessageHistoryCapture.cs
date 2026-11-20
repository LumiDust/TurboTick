using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Remembers the `historical` flag of every live message (round 163).
///
/// <para><b>Why a capture and not a field read.</b> `Messages.Message(Message, bool historical = true)` (`:185729`)
/// archives the message when the flag is true (`Find.Archive.Add(msg)`) and skips the archive when it is false - that
/// is the whole difference between "a colonist wandered in" and "not enough material for that bill". The flag is a
/// <b>parameter</b>: nothing on `Message` stores it, so the channel cannot read it back off the object. Without this
/// capture the replica has to guess, and guessing `true` would fill its history tab with the transient messages the
/// authority deliberately kept out of its own.</para>
///
/// <para><b>Why the object is the key.</b> `liveMessages` holds the very instance that was passed here, and the publish
/// half walks that same list, so a `ConditionalWeakTable`-style keyed lookup answers without touching the message. The
/// table is trimmed when it grows past twice the live-list bound, which keeps a long session from accumulating dead
/// keys.</para></summary>
[HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
internal static class MessageHistoryCapturePatch
{
    private static readonly Dictionary<Message, bool> historical = [];

    private static void Prefix(Message __0, bool __1)
    {
        try
        {
            if (__0 is null) return;
            historical[__0] = __1;
            if (historical.Count <= 256) return;
            // Only the live list can still be published, so keys beyond that bound are already dead.
            var live = new HashSet<Message>();
            if (LetterSync.LiveMessagesForTrim is { } list)
                foreach (var message in list) if (message is not null) live.Add(message);
            foreach (var key in new List<Message>(historical.Keys))
                if (!live.Contains(key)) historical.Remove(key);
        }
        catch (System.Exception error)
        {
            Log.Warning("[TurboTick] 消息历史标记失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>The flag this message was posted with. Absent means "posted before the patch existed" - answered with
    /// the vanilla default rather than with false, so an old message still archives.</summary>
    internal static bool Historical(Message message) =>
        message is not null && historical.TryGetValue(message, out var flag) ? flag : true;

    internal static int Tracked => historical.Count;

    internal static void Reset() => historical.Clear();
}
