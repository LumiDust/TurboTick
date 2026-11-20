using System;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Probe for the message half of the letter/message channel (round 163).
///
/// <para><b>What it measures.</b> `Messages.Message(Message, bool historical)` (`:185729`) does two things: it puts the
/// message on the live list and, when `historical` is true, into `Find.Archive`. The channel used to send every message
/// as `historical: false` and to carry only "has a target" instead of the target, so the two ends agreed on screen and
/// disagreed in the history tab, and a click on a replica message did nothing. The probe posts one message of each kind
/// through the game's own door, publishes, applies the captured rows, and reads all three answers: archive growth, the
/// history flag on the row, and whether the applied message carries the same cell.</para>
///
/// <para><b>Cleanup.</b> `Archive.Remove` (`:361945`) is public, so the entries the probe creates are removed again -
/// the history tab is the player's, not a fixture's.</para></summary>
internal static class MessageArchiveProbe
{
    internal static string Run(string? argument)
    {
        var parts = (argument ?? string.Empty).Split('|');
        var label = parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : "probe-message";
        var map = Find.CurrentMap;
        if (map is null) return "没有当前地图";
        var archive = Find.Archive;
        if (archive is null) return "本端没有 Archive";
        var cell = map.Center;
        var before = archive.ArchivablesListForReading.Count;
        // Two messages through the game's own door: one archived, one live-only. Both carry the same cell, so the target
        // half is read on the archived one and the flag half on the pair.
        Messages.Message(new Message(label + "-archived", MessageTypeDefOf.NeutralEvent, new LookTargets(cell, map)),
            historical: true);
        Messages.Message(new Message(label + "-transient", MessageTypeDefOf.NeutralEvent, new LookTargets(cell, map)),
            historical: false);
        var afterPost = archive.ArchivablesListForReading.Count;
        var rows = 0;
        var archivedFlag = false;
        var transientFlag = true;
        var targetCell = false;
        var rowDetail = new StringBuilder();
        var applied = 0;
        var archivedByApply = 0;
        var liveBefore = LiveCount();
        try
        {
            var published = LetterSync.Publish();
            if (published.Messages is { Count: > 0 } messages)
                foreach (var row in messages)
                {
                    if (row is null || !row.Text.StartsWith(label, StringComparison.Ordinal)) continue;
                    rows++;
                    if (row.Text.EndsWith("-archived", StringComparison.Ordinal))
                    {
                        archivedFlag = row.Historical;
                        targetCell = row.Map == map.uniqueID && row.X == cell.x && row.Z == cell.z;
                        // The numbers the verdict is made from, so a failing reading names itself.
                        rowDetail.Append($"行 HasTarget={row.HasTarget} Map={row.Map} X={row.X} Z={row.Z}（期望格 {cell.x},{cell.z}）");
                    }
                    if (row.Text.EndsWith("-transient", StringComparison.Ordinal)) transientFlag = row.Historical;
                    // `Messages.AcceptsMessage` (`:185800`) drops a message whose text and look targets already match a
                    // live one - and it drops it *before* the archive branch, so applying the very text the probe just
                    // posted would measure the dedupe instead of the channel. The apply half is what is under test, and
                    // the text is not part of it, so the applied row carries a suffix.
                    LetterSync.Apply(row with { Text = row.Text + "-apply" });
                    applied++;
                    if (row.Historical) archivedByApply++;
                }
        }
        finally
        {
            // The fixture must not survive: the archive is the player's history.
            foreach (var entry in archive.ArchivablesListForReading
                         .Where(entry => entry is Message msg && msg.text is not null
                             && msg.text.StartsWith(label, StringComparison.Ordinal)).ToList())
                archive.Remove(entry);
        }
        var after = archive.ArchivablesListForReading.Count;
        var message = new StringBuilder();
        message.Append($"发布前档 {before} 条 → 两条消息后 {afterPost} 条（应 +1：一条入档、一条只上屏）；");
        message.Append($"通道行 {rows}（应 2，入档条标记 {(archivedFlag ? "true" : "false")}、只上屏条标记 {(transientFlag ? "true" : "false")}）；");
        message.Append($"目标格 {(targetCell ? "随行带来" : "没带来")}（{rowDetail}）；");
        message.Append($"应用 {applied} 行、其中入档 {archivedByApply}（应 1）；活列表 {liveBefore} → {LiveCount()}；");
        message.Append($"清理后档 {after} 条（应回到 {before}）；");
        message.Append(LetterSync.Summary());
        return message.ToString();
    }

    private static int LiveCount()
    {
        var live = LetterSync.LiveMessagesForTrim;
        return live?.Count ?? 0;
    }
}
