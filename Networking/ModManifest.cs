using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TurboTick.Networking;

/// <summary>One active mod, in load order.</summary>
/// <param name="Digest">Short digest of everything this mod contributes to the simulation (its hashed files, in path
/// order). Empty when the peer did not send one, which is the case for every build before this field existed - and
/// an absent digest must not read as a difference, or every older peer would light up the whole table.</param>
public sealed record ModEntry(string PackageId, string Name, int Order, string WorkshopId, string Version = "",
    string Digest = "",
    // Unix seconds of the newest hashed file, 0 = unknown. Only used to say *which side is older* — it never
    // enters the compatibility decision, because two machines legitimately extract the same mod at different
    // times and a fingerprint must describe content, not install history.
    long Stamped = 0)
{
    /// <summary>Steam workshop page for this mod, or null when it did not come from the workshop.</summary>
    public string? WorkshopUrl =>
        string.IsNullOrEmpty(WorkshopId) || WorkshopId == "0"
            ? null
            : "https://steamcommunity.com/sharedfiles/filedetails/?id=" + WorkshopId;
}

/// <summary>How one mod compares between the room and this machine. The point of the list is that every mod gets a
/// verdict, not just the ones that differ: "23 mods checked, this one is wrong" is what an operator can act on.
/// </summary>
public enum ModVerdict
{
    /// <summary>Present on both sides, same version, same position.</summary>
    Ok,
    /// <summary>The room runs it, this machine does not have it at all.</summary>
    Missing,
    /// <summary>This machine has it, the room does not.</summary>
    Extra,
    /// <summary>Both have it, but <c>About.xml</c>'s <c>&lt;modVersion&gt;</c> differs - the usual cause of a hash
    /// mismatch that names no missing mod at all.</summary>
    VersionMismatch,
    /// <summary>Both have it, same version, but it loads at a different position.</summary>
    OutOfOrder,
    /// <summary>Both have it, same version, same position, but the files themselves differ - a hand-patched dll, a
    /// partially updated workshop item, a beta build. This is the verdict that explains "the list says all 22
    /// identical, and the join is still refused", which used to have no row at all.</summary>
    ContentMismatch
}

/// <summary>One row of the verification list: the verdict plus both sides' values, so the dialog needs no lookups.</summary>
public sealed record ModCheck(ModVerdict Verdict, string PackageId, string Name, string? WorkshopUrl,
    int ServerOrder, int LocalOrder, string ServerVersion, string LocalVersion,
    // Unix seconds of the newest hashed file on each side, 0 = unknown. Diagnostic only: it answers "which copy is
    // the old one" for a `ContentMismatch`, and is deliberately absent from the verdict decision.
    long ServerStamped = 0, long LocalStamped = 0)
{
    /// <summary>Which side is older, or <c>null</c> when that cannot be said.
    ///
    /// <para>This is the whole point of carrying the timestamps: a content mismatch names a mod but not a direction,
    /// and the player's next question is always "update or roll back". A ten-second tolerance because the two
    /// machines copy the same workshop item at slightly different moments - inside that window the honest answer is
    /// "same age", not "this one is newer".</para></summary>
    public string? OlderSide
    {
        get
        {
            if (Verdict != ModVerdict.ContentMismatch) return null;
            if (ServerStamped <= 0 || LocalStamped <= 0) return null;
            if (Math.Abs(ServerStamped - LocalStamped) <= 10) return null;
            return ServerStamped < LocalStamped ? "room" : "local";
        }
    }
    /// <summary>Which side carries the older <c>&lt;modVersion&gt;</c>, or <c>null</c> when the two strings cannot be
    /// ordered.
    ///
    /// <para>The verdict alone said "these differ", and every operator then had to open both <c>About.xml</c> files to
    /// learn which one to update. Naming the side is the difference between a report and an instruction: the player
    /// updates a local mod, the operator updates the server's copy, and for a mod that ships on the workshop both are
    /// the same download.</para></summary>
    public string? VersionOlderSide
    {
        get
        {
            if (Verdict != ModVerdict.VersionMismatch) return null;
            return ModManifest.CompareVersionStrings(ServerVersion, LocalVersion) switch
            {
                < 0 => "room",     // the server's copy is the older one
                > 0 => "local",
                _ => null,
            };
        }
    }

    /// <summary>What the player has to do, or an empty string when nothing is wrong.</summary>
    public string Action => Verdict switch
    {
        ModVerdict.Missing => "install",
        ModVerdict.Extra => "disable",
        ModVerdict.VersionMismatch => "update",
        ModVerdict.OutOfOrder => "reorder",
        ModVerdict.ContentMismatch => "replace",
        _ => ""
    };
}

/// <summary>What differs between two mod lists, in the terms the player can act on.</summary>
public sealed record ModDiff(
    IReadOnlyList<ModEntry> Missing,
    IReadOnlyList<ModEntry> Extra,
    IReadOnlyList<ModEntry> OutOfOrder,
    IReadOnlyList<ModEntry> VersionMismatch = null!,
    IReadOnlyList<ModEntry> ContentMismatch = null!)
{
    public bool Clean => Missing.Count == 0 && Extra.Count == 0 && OutOfOrder.Count == 0
        && (VersionMismatch?.Count ?? 0) == 0 && (ContentMismatch?.Count ?? 0) == 0;
}

/// <summary>The active mod list, carried in the handshake so a mismatch can be explained and fixed.
///
/// The simulation hash alone says "we are different" and nothing else. A player who has just been told that has no
/// way to act: they cannot see which mod is missing, and re-ordering forty mods by hand to match someone else's list
/// is not something anyone does. ColonyTogether solves this with a synchronisation dialog that shows the difference
/// and offers to fix it; this is the same idea over our existing one-envelope protocol - one additive string field,
/// no new message types.
///
/// Format, current (`TTM3` header line first): `order|packageId|workshopId|version|digest|name` per mod, joined by
/// '\n'. Package ids are lower case and never contain '|' or '\n', so the parse is unambiguous without escaping. The
/// name is last so a stray '|' in a name only mangles the name. The header exists because a digest per mod is a
/// different field count than the previous form, and guessing from the field count would misfire on an empty digest.
///
/// Older peers send no header and either four fields (no version) or five (`order|packageId|workshopId|version|name`).
/// Both still parse: the refused handshake is exactly when the list matters most, and an absent digest has to read as
/// "not declared", never as "different".</summary>
public static class ModManifest
{
    /// <summary>Payload ceiling, matching the handshake's tolerance for the hash itself.</summary>
    public const int MaxLength = 24 * 1024;

    /// <summary>Marks the field layout that carries a per-mod digest and a per-mod timestamp. A header line rather than
    /// a field count: the legacy five-field form and a new six-field form with an empty name are otherwise the same
    /// shape, and adding the timestamp in the middle makes a silent misparse the alternative.</summary>
    private const string Header = "TTM4";

    public static string Serialize(IReadOnlyList<ModEntry> entries)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        foreach (var entry in entries)
        {
            // Names can contain the separators a player typed; they are last so a stray '|' only mangles the name.
            var name = (entry.Name ?? "").Replace('\n', ' ').Replace('\r', ' ');
            var version = (entry.Version ?? "").Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ');
            text.Append(entry.Order).Append('|')
                .Append(entry.PackageId).Append('|')
                .Append(entry.WorkshopId).Append('|')
                .Append(version).Append('|')
                .Append(entry.Digest ?? "").Append('|')
                .Append(entry.Stamped).Append('|')
                .Append(name).Append('\n');
            if (text.Length > MaxLength) break;
        }
        return text.ToString();
    }

    /// <summary>Parses the wire form. Also accepts every older shape (four fields without a version, five with it,
    /// six with a digest) so a peer on the previous build is listed rather than dropped - a refused handshake is
    /// exactly when a player most needs the list.</summary>
    public static List<ModEntry> Parse(string? payload)
    {
        var entries = new List<ModEntry>();
        if (string.IsNullOrWhiteSpace(payload)) return entries;
        foreach (var line in payload.Split('\n'))
        {
            if (line.Length == 0) continue;
            // Every generation of the header, not just this one: the old peers send `TTM3`, and a `TTM3` line parsed as
            // a mod row would show up as a bogus entry with id "ttm3".
            if (line.StartsWith("TTM", StringComparison.Ordinal)) continue;
            var parts = line.Split('|', 7);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], out var order)) continue;
            var id = parts[1].Trim().ToLowerInvariant();
            if (id.Length == 0) continue;
            entries.Add(parts.Length switch
            {
                4 => new ModEntry(id, parts[3], order, parts[2]),
                5 => new ModEntry(id, parts[4], order, parts[2], parts[3]),
                6 => new ModEntry(id, parts[5], order, parts[2], parts[3], parts[4]),
                // A malformed stamp degrades to "unknown" rather than dropping the row: losing the mod from the list is
                // worse than losing the direction hint, because the list is what tells the player what to install.
                _ => new ModEntry(id, parts[6], order, parts[2], parts[3], parts[4],
                    long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamped) ? stamped : 0)
            });
        }
        return entries;
    }

    /// <summary>Compares the server's list (what the room runs) against ours.</summary>
    public static ModDiff Compare(IReadOnlyList<ModEntry> server, IReadOnlyList<ModEntry> local)
    {
        var mine = new Dictionary<string, ModEntry>(local.Count, StringComparer.Ordinal);
        foreach (var entry in local) mine[entry.PackageId] = entry;
        var theirs = new HashSet<string>(server.Select(entry => entry.PackageId), StringComparer.Ordinal);
        var missing = server.Where(entry => !mine.ContainsKey(entry.PackageId)).ToList();
        var extra = local.Where(entry => !theirs.Contains(entry.PackageId)).ToList();
        // A version difference is the one mismatch that produces a different hash while both sides have the same
        // mod list, so it has to be named separately: "you have all 23, but one of them is the wrong build" is not
        // something the missing/extra/order sections can say.
        var versionMismatch = new List<ModEntry>();
        var contentMismatch = new List<ModEntry>();
        foreach (var entry in server)
        {
            if (!mine.TryGetValue(entry.PackageId, out var found)) continue;
            if (VersionDiffers(entry.Version, found.Version)) { versionMismatch.Add(entry); continue; }
            // Only when both sides bothered to send a digest. An older peer sends none, and "did not say" is not a
            // difference - reporting it would light up every row against every pre-update client.
            if (DigestDiffers(entry.Digest, found.Digest)) contentMismatch.Add(entry);
        }
        // Order only matters between mods we both have; a mod one side lacks is already reported above.
        //
        // Report the *smallest* set that has to move rather than every position that disagrees. A position-wise scan
        // cascades: one mod dragged to the front shifts the whole tail, so a single misplaced mod flags everything
        // after it. Measured on a real 22-mod room (`192.168.0.29`, 2026-11-14) one misplaced Harmony produced
        // **7 rows of 顺序不同** - the player is told to fix seven things when moving one fixes all of them.
        // The mods that can stay put are the longest run that is already in local order; everything outside it moves.
        var shared = server.Where(entry => mine.ContainsKey(entry.PackageId)).ToList();
        var stays = LongestIncreasingRun(shared.Select(entry => mine[entry.PackageId].Order).ToList());
        var outOfOrder = new List<ModEntry>();
        for (var i = 0; i < shared.Count; i++)
            if (!stays[i]) outOfOrder.Add(shared[i]);
        return new ModDiff(missing, extra, outOfOrder, versionMismatch, contentMismatch);
    }

    /// <summary>True when two per-mod digests really disagree. Anything short of both sides declaring one is not a
    /// difference: a peer on a build older than the field sends none, and its global hash already decides compatibility.
    ///
    /// <para>Treating one-sided as a difference was measured to be the same false positive class this verdict exists
    /// to remove: an up-to-date server and an older client with byte-identical files would light up every row, and
    /// the player would be told to reinstall 22 mods that are already correct. A digest can only ever refine an
    /// already-mismatching picture, never contradict an agreeing one.</para></summary>
    private static bool DigestDiffers(string? server, string? local)
    {
        var a = (server ?? "").Trim();
        var b = (local ?? "").Trim();
        return a.Length != 0 && b.Length != 0 && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Marks one longest strictly-increasing subsequence of <paramref name="values"/>: those entries are
    /// already in the right relative order and do not have to move.
    ///
    /// <para>O(n log n) patience sorting with predecessor links, so the marked set is a real optimal answer rather
    /// than a heuristic. Equal values cannot occur (each mod's order is unique), and ties between two equally long
    /// runs resolve the same way every call, so the dialog does not reshuffle itself between redraws.</para></summary>
    private static bool[] LongestIncreasingRun(IReadOnlyList<int> values)
    {
        var stays = new bool[values.Count];
        if (values.Count == 0) return stays;
        var tail = new int[values.Count];      // tail[len] = index of the smallest tail among runs of length len+1
        var previous = new int[values.Count];
        var length = 0;
        for (var i = 0; i < values.Count; i++)
        {
            previous[i] = -1;
            var lo = 0;
            var hi = length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (values[tail[mid]] < values[i]) lo = mid + 1;
                else hi = mid;
            }
            if (lo > 0) previous[i] = tail[lo - 1];
            tail[lo] = i;
            if (lo == length) length++;
        }
        for (var i = tail[length - 1]; i >= 0; i = previous[i]) stays[i] = true;
        return stays;
    }

    /// <summary>True when two <c>&lt;modVersion&gt;</c> strings really disagree.
    ///
    /// Empty means "did not declare one", which is extremely common (most mods never set the field) and must not be
    /// reported as a mismatch against a room that also left it empty. When one side declared a version and the other
    /// did not, that <i>is</i> worth showing, because it is a real difference between the two installs.</summary>
    private static bool VersionDiffers(string? server, string? local)
    {
        var a = (server ?? "").Trim();
        var b = (local ?? "").Trim();
        if (a.Length == 0 && b.Length == 0) return false;
        if (a.Length == 0 || b.Length == 0) return true;
        // Numeric order, not string order: "3.0.10" and "3.0.9" are different builds and "1.0" and "1.0.0" are not.
        return CompareVersionStrings(a, b) != 0;
    }

    /// <summary>Orders two <c>&lt;modVersion&gt;</c> strings: negative when <paramref name="left"/> is older.
    ///
    /// <para>Dotted numbers are compared as numbers, not as text: <c>"3.0.10"</c> is newer than <c>"3.0.9"</c> and a
    /// string comparison gets that backwards. Anything that does not parse (a date, a git hash, <c>"v1-beta"</c>)
    /// falls back to an ordinal comparison, which is arbitrary but stable - and the caller only uses the sign to say
    /// which side to update, never to decide whether the two builds are compatible.</para>
    ///
    /// <para>Missing trailing segments count as zero, so <c>"1.0"</c> and <c>"1.0.0"</c> compare equal: two mods that
    /// wrote the same version two ways are not a version mismatch worth a row in the table.</para></summary>
    public static int CompareVersionStrings(string? left, string? right)
    {
        var a = (left ?? "").Trim();
        var b = (right ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return string.CompareOrdinal(a, b);
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            if (int.TryParse(sa, out var na) && int.TryParse(sb, out var nb))
            {
                if (na != nb) return na < nb ? -1 : 1;
                continue;
            }
            var text = string.CompareOrdinal(sa, sb);
            if (text != 0) return text < 0 ? -1 : 1;
        }
        return 0;
    }

    /// <summary>Every mod with a verdict, room list first then anything the room does not have. This is what the
    /// verification dialog renders - one row per mod, so "everything is fine except this one" is visible at a glance.
    /// </summary>
    public static List<ModCheck> Check(IReadOnlyList<ModEntry> server, IReadOnlyList<ModEntry> local)
    {
        var diff = Compare(server, local);
        var mine = new Dictionary<string, ModEntry>(local.Count, StringComparer.Ordinal);
        foreach (var entry in local) mine[entry.PackageId] = entry;
        var rows = new List<ModCheck>(server.Count + diff.Extra.Count);
        foreach (var entry in server)
        {
            var found = mine.GetValueOrDefault(entry.PackageId);
            var verdict = found is null ? ModVerdict.Missing
                : VersionDiffers(entry.Version, found.Version) ? ModVerdict.VersionMismatch
                : DigestDiffers(entry.Digest, found.Digest) ? ModVerdict.ContentMismatch
                : diff.OutOfOrder.Any(other => other.PackageId == entry.PackageId) ? ModVerdict.OutOfOrder
                : ModVerdict.Ok;
            rows.Add(new ModCheck(verdict, entry.PackageId, found?.Name ?? entry.Name, entry.WorkshopUrl,
                entry.Order, found?.Order ?? -1, entry.Version, found?.Version ?? "",
                entry.Stamped, found?.Stamped ?? 0));
        }
        foreach (var entry in diff.Extra)
            rows.Add(new ModCheck(ModVerdict.Extra, entry.PackageId, entry.Name, entry.WorkshopUrl,
                -1, entry.Order, "", entry.Version));
        return rows;
    }

    /// <summary>Counts per verdict, for the window title ("共 22 个：缺失 2 / 版本不同 1；其余 20 个一致").
    ///
    /// <para>The text goes through the caller's translator (<paramref name="t"/>), because this project must not
    /// reference Verse: the networking assembly is shared with the portal, which has no language database. Passing
    /// no translator yields the English words, which is what a test and a headless log want anyway.</para>
    /// </summary>
    public static string Summary(IReadOnlyList<ModCheck> rows, Func<string, object[], string>? t = null)
    {
        string Tr(string key, params object[] args) => t is null ? key : t(key, args);
        var bad = rows.Where(row => row.Verdict != ModVerdict.Ok).ToList();
        if (bad.Count == 0) return Tr("TurboTick_ModCheck_AllOk", rows.Count);
        var parts = new List<string>(4);
        void Add(ModVerdict verdict, string key)
        {
            var count = bad.Count(row => row.Verdict == verdict);
            if (count > 0) parts.Add(Tr(key, count));
        }
        Add(ModVerdict.Missing, "TurboTick_ModCheck_CountMissing");
        Add(ModVerdict.VersionMismatch, "TurboTick_ModCheck_CountVersion");
        Add(ModVerdict.ContentMismatch, "TurboTick_ModCheck_CountContent");
        Add(ModVerdict.OutOfOrder, "TurboTick_ModCheck_CountOrder");
        Add(ModVerdict.Extra, "TurboTick_ModCheck_CountExtra");
        return Tr("TurboTick_ModCheck_Summary", rows.Count, string.Join(" / ", parts), rows.Count - bad.Count);
    }

    /// <summary>Round-trips through the wire representation. Kept here so the format has one owner.</summary>
    public static string Describe(ModDiff diff)
    {
        var text = new StringBuilder();
        if (diff.Missing.Count > 0) text.Append("缺少 ").Append(diff.Missing.Count).Append(" 个：")
            .Append(string.Join("、", diff.Missing.Take(5).Select(entry => entry.Name)))
            .Append(diff.Missing.Count > 5 ? "…" : "");
        if (diff.Extra.Count > 0)
        {
            if (text.Length > 0) text.Append("；");
            text.Append("多出 ").Append(diff.Extra.Count).Append(" 个：")
                .Append(string.Join("、", diff.Extra.Take(5).Select(entry => entry.Name)))
                .Append(diff.Extra.Count > 5 ? "…" : "");
        }
        if (diff.OutOfOrder.Count > 0)
        {
            if (text.Length > 0) text.Append("；");
            text.Append("顺序不同 ").Append(diff.OutOfOrder.Count).Append(" 处");
        }
        if (diff.VersionMismatch is { Count: > 0 } versions)
        {
            if (text.Length > 0) text.Append("；");
            text.Append("版本不同 ").Append(versions.Count).Append(" 个：")
                .Append(string.Join("、", versions.Take(5).Select(entry => entry.Name)))
                .Append(versions.Count > 5 ? "…" : "");
        }
        if (diff.ContentMismatch is { Count: > 0 } contents)
        {
            if (text.Length > 0) text.Append("；");
            text.Append("文件不同 ").Append(contents.Count).Append(" 个：")
                .Append(string.Join("、", contents.Take(5).Select(entry => entry.Name)))
                .Append(contents.Count > 5 ? "…" : "");
        }
        return text.Length == 0 ? "模组列表一致" : text.ToString();
    }

    /// <summary>One line per version-mismatched mod, with both versions and the side that has to move.
    ///
    /// <para>This is the sentence a server operator needs from their own console: the room's copy of <c>Harmony</c>
    /// being older than the player's is the server's problem, and the same words tell the player the opposite case.
    /// Empty when no mod differs by version.</para></summary>
    public static string ExplainVersions(IReadOnlyList<ModCheck> rows, Func<string, object[], string>? t = null,
        string serverLabel = "房间", string localLabel = "本地")
    {
        var mismatched = rows.Where(row => row.Verdict == ModVerdict.VersionMismatch).ToList();
        if (mismatched.Count == 0) return "";
        var parts = new List<string>(mismatched.Count);
        foreach (var row in mismatched)
        {
            var side = row.VersionOlderSide;
            var note = side switch
            {
                "room" => t is null ? "room copy is older - update the server" : t("TurboTick_ModCheck_RoomOlder", []),
                "local" => t is null ? "local copy is older - update this client" : t("TurboTick_ModCheck_LocalOlder", []),
                _ => t is null ? "cannot order these version strings" : t("TurboTick_ModCheck_VersionUnordered", []),
            };
            parts.Add($"{row.Name} {serverLabel} {Show(row.ServerVersion)} / {localLabel} {Show(row.LocalVersion)}：{note}");
        }
        return string.Join("；", parts);
        static string Show(string version) => string.IsNullOrWhiteSpace(version) ? "未声明" : version.Trim();
    }

    /// <summary>JSON of the server's list, for the error that reports the mismatch.</summary>
    public static string ToJson(IReadOnlyList<ModEntry> entries) =>
        JsonSerializer.Serialize(entries.Select(entry => new[]
            { entry.Order.ToString(), entry.PackageId, entry.WorkshopId, entry.Version, entry.Name, entry.Stamped.ToString() }));
}


