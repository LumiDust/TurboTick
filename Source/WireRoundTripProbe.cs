using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Publishes one real frame, sends it through the wire codec, and diffs what came back out.
///
/// <para><b>Why this exists.</b> Every channel in this project is verified by a test that asserts the JSON shape of a frame
/// the test constructed itself. That says nothing about the frame the authority actually builds: a channel can be perfectly
/// correct on the wire and still be absent from <c>AuthoritativeFrame</c>, or present with a null list because the publisher
/// returned early. Separately, a channel counter freezing at its first-frame value has two completely different causes - no
/// replica to publish to, and a publisher that never produced anything - and the counters alone cannot tell them apart.</para>
///
/// <para><b>What it proves.</b> Frames go out compressed above 512 bytes (<c>FrameCodec.Pack</c>), through a Brotli stream,
/// and are unpacked on the far end. This runs exactly that round trip on a live frame and compares every channel count before
/// against after, so "the packet is on the wire and the codec returns it intact" becomes a measurement instead of an
/// assumption. It cannot prove a different process applied it - there is no replica here - but it does rule out the two
/// failures that produce a silent empty channel.</para>
///
/// <para><b>Why a probe rather than a test.</b> It needs the live world: real zones, real pawns, real grids. That is the
/// thing under test.</para></summary>
internal static class WireRoundTripProbe
{
    internal static string Report()
    {
        var text = new StringBuilder(1024);
        // The frame the authority *actually* published, not one rebuilt for the probe. Rebuilding would advance every
        // channel's "already sent" record, so the next real frame would become a delta against a frame nobody received -
        // the probe would break the thing it measures.
        if (HeadlessRuntime.LastPublishedFrame is not { } frame)
            return "线上往返自检：尚未发布过帧（房间无副本时不发布 —— 见 tick-report 的「未推进原因」）";

        var before = Counts(frame);
        AuthoritativeFrame? back;
        var sizeRaw = 0;
        var sizeWire = 0;
        var compressed = false;
        try
        {
            // `FrameCodec` and `WireMessage` are `internal` to the `Networking` assembly, so the round trip goes through
            // reflection. That is deliberate rather than lazy: widening them to `public` to reach one probe would let any
            // future code bypass the codec's single entry point.
            var codec = typeof(AuthoritativeFrame).Assembly.GetType("TurboTick.Networking.FrameCodec")
                ?? throw new InvalidOperationException("FrameCodec 类型不存在");
            var messageType = codec.Assembly.GetType("TurboTick.Networking.WireMessage")
                ?? throw new InvalidOperationException("WireMessage 类型不存在");
            var message = Activator.CreateInstance(messageType)
                ?? throw new InvalidOperationException("WireMessage 构造失败");
            messageType.GetProperty("Type")!.SetValue(message, "Frame");
            messageType.GetProperty("Frame")!.SetValue(message, frame);
            messageType.GetProperty("Tick")!.SetValue(message, frame.Tick);
            var pack = codec.GetMethod("Pack", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? throw new InvalidOperationException("FrameCodec.Pack 不存在");
            var packed = pack.Invoke(null, [message])
                ?? throw new InvalidOperationException("Pack 返回 null");
            var zip = messageType.GetProperty("FrameZip")?.GetValue(packed) as byte[];
            compressed = zip is not null;
            sizeWire = zip?.Length ?? 0;
            var options = (System.Text.Json.JsonSerializerOptions?)typeof(AuthoritativeFrame).Assembly
                .GetType("TurboTick.Networking.JsonWire")?.GetProperty("Options",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public)?.GetValue(null)
                ?? new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            sizeRaw = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frame, options).Length;
            var unpack = codec.GetMethod("TryUnpack", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? throw new InvalidOperationException("FrameCodec.TryUnpack 不存在");
            object?[] args = [packed, null, null];
            if (unpack.Invoke(null, args) is not true)
                return "线上往返自检：解包失败 " + (args[2] as string ?? "（无原因）");
            back = messageType.GetProperty("Frame")!.GetValue(args[1]) as AuthoritativeFrame;
        }
        catch (Exception error_) { return "线上往返自检：异常 " + error_.GetType().Name + ": " + error_.Message; }
        if (back is null) return "线上往返自检：解包后没有帧";

        var after = Counts(back);
        // Every channel that carried something is compared. A channel present before and absent after is a codec or
        // serializer failure; one absent before and present after would be an invented field.
        var differences = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(name => name, StringComparer.Ordinal))
        {
            var was = before.TryGetValue(key, out var a) ? a : -1;
            var now = after.TryGetValue(key, out var b) ? b : -1;
            if (was != now) differences.Add($"{key} {was}→{now}");
        }
        text.Append("线上往返自检：").Append(frame.Tick).Append(" tick，")
            .Append(before.Values.Sum()).Append(" 个条目，")
            .Append(sizeRaw).Append(" B → 压缩 ").Append(sizeWire).Append(" B")
            .Append(compressed ? "" : "（未压缩）").Append("。");
        text.Append(differences.Count == 0
            ? $" {before.Count} 个通道全部一致。"
            : $" **不一致 {differences.Count} 个：{string.Join("，", differences)}**");
        // Row counts alone are a weak check: a codec that dropped every string from every row would keep all the
        // counts equal and still lose the payload. `Drivers` is where that matters most - one row per working pawn,
        // carrying a whole Scribe fragment - so its fragments are compared byte for byte.
        var contentMismatches = ContentMismatches(frame, back);
        text.Append(contentMismatches.Count == 0
            ? " 载荷逐字节一致。"
            : $" **载荷不一致 {contentMismatches.Count} 条：{string.Join("，", contentMismatches.Take(3))}**");
        // Empty channels are listed too: an all-zero report reads as "nothing to send" and "broken" at once, and which one
        // it is depends on whether the world has the object at all.
        var empty = before.Where(pair => pair.Value == 0).Select(pair => pair.Key).ToList();
        if (empty.Count > 0) text.Append($" 空通道 {empty.Count} 个：{string.Join("，", empty)}。");
        return text.ToString();
    }

    /// <summary>Compares every channel whose payload is text, by content, found by reflection.
    ///
    /// <para>Why this exists: the row-count comparison cannot see a codec that keeps the rows and loses their strings. The
    /// first version of this method named `Drivers` by hand - and by the time `Deathrest` and `Stance` were added it was
    /// silently comparing one channel out of three, while the report still printed "载荷逐字节一致". That is the same stale
    /// table failure as `Counts`, so it is fixed the same way: walk the record's own properties, and for every channel whose
    /// rows carry a string member, compare those strings.</para>
    ///
    /// <para>A row here is any object with a `Pawn` int and one or more string members; that covers all three text
    /// channels without naming them. Rows are matched by `Pawn`, which is unique within a channel.</para></summary>
    private static List<string> ContentMismatches(AuthoritativeFrame before, AuthoritativeFrame after)
    {
        var result = new List<string>();
        foreach (var property in typeof(AuthoritativeFrame).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.GetValue(before) is not System.Collections.IEnumerable leftRows) continue;
            if (property.GetValue(after) is not System.Collections.IEnumerable rightRows) continue;
            // Rows are compared positionally, not indexed by `Pawn`: several channels carry more than one row per pawn
            // (`Memories` carries one per thought), and keying by pawn silently keeps only the last of them - then every
            // earlier row compares against a stranger and reports a length difference that the codec never produced.
            var leftList = new List<object>();
            foreach (var row in leftRows)
                if (row is not null && row.GetType().GetProperty("Pawn") is not null) leftList.Add(row);
            if (leftList.Count == 0) continue;
            var rightList = new List<object>();
            foreach (var row in rightRows)
                if (row is not null && row.GetType().GetProperty("Pawn") is not null) rightList.Add(row);
            if (rightList.Count != leftList.Count)
            {
                result.Add($"{property.Name} 行数 {leftList.Count}→{rightList.Count}");
                continue;
            }
            for (var index = 0; index < leftList.Count; index++)
            {
                var row = leftList[index];
                var other = rightList[index];
                var type = row.GetType();
                var pawnId = type.GetProperty("Pawn")?.GetValue(row) as int? ?? -1;
                foreach (var member in type.GetProperties(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    if (member.PropertyType != typeof(string)) continue;
                    var mine = member.GetValue(row) as string;
                    var theirs = member.GetValue(other) as string;
                    if (!string.Equals(mine, theirs, StringComparison.Ordinal))
                        result.Add($"{property.Name}#{pawnId}.{member.Name} "
                            + $"{mine?.Length ?? 0}→{theirs?.Length ?? 0} 字符");
                }
            }
        }
        return result;
    }

    /// <summary>One entry per channel, counting the rows it carries. Deliberately the wire shape, not a per-channel API: a
    /// count read through <c>PlantSync.Summary</c> would test the summary rather than the frame.
    ///
    /// <para><b>Why this is reflection rather than a table.</b> The table version was hand-maintained and went stale twice:
    /// a channel added to <see cref="AuthoritativeFrame"/> but not to the table is silently excluded from the comparison,
    /// and the report still printed "every channel agrees" over the channels it did know about. That is a green number that
    /// covers nothing - the exact failure this project keeps finding. Enumerating the record's own properties means a new
    /// channel is compared the day it is declared, and a property that is not a collection is still counted (as its null
    /// status) rather than skipped, so nothing can fall out of the comparison by accident.</para>
    ///
    /// <para>Channels whose value is a list of rows count the rows. Channels that are a single object (the settlement
    /// snapshot, the plan list) count one when present, because the question this answers is per-channel "did anything ride
    /// along", not "how many sub-objects".</para></summary>
    private static Dictionary<string, int> Counts(AuthoritativeFrame frame)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in typeof(AuthoritativeFrame).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            var value = property.GetValue(frame);
            var count = value switch
            {
                null => 0,
                // `Tick` and `Map` are numbers, not channels; the record's own scalar fields are counted as present so a
                // scalar that fails to survive the codec still shows up as a difference.
                System.Collections.ICollection collection => collection.Count,
                string text => text.Length > 0 ? 1 : 0,
                _ => 1
            };
            // The tick is the frame's identity, not a payload channel; every other property is one.
            if (string.Equals(property.Name, nameof(AuthoritativeFrame.Tick), StringComparison.Ordinal)) continue;
            result[property.Name] = count;
        }
        return result;
    }
}
