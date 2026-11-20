using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Networking;

/// <summary>
/// The parts of the save upload that decide "accept or refuse", kept free of Verse so they can be tested without
/// a running game.
///
/// Two of them are security-relevant and were the reason to pull them out of the page:
/// <see cref="LooksLikeSave"/> is the only thing standing between an arbitrary 200 MB body and the save folder,
/// and <see cref="IsDirectChild"/> is what makes "delete this save" impossible to aim at <c>../../etc</c>.
/// </summary>
public static class SaveImport
{
    public const string Extension = ".rws";
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const int PreviewBytes = 64;

    /// <summary>
    /// Whether the first bytes look like a RimWorld save.
    ///
    /// A UTF-8 BOM is skipped and a leading XML declaration or comment is tolerated, because both are things the
    /// game's own writer and third-party re-savers produce. A UTF-16 file is refused instead of guessed at: the
    /// game writes UTF-8 and a mis-decoded save fails much later, inside Scribe, with an unhelpful error.
    /// </summary>
    public static bool LooksLikeSave(ReadOnlySpan<byte> prologue, int length, out string problem)
    {
        problem = "";
        if (length <= 0) { problem = "文件是空的"; return false; }
        var span = prologue[..Math.Min(length, prologue.Length)];
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
        if (span.Length >= 2 && ((span[0] == 0xFF && span[1] == 0xFE) || (span[0] == 0xFE && span[1] == 0xFF)))
        { problem = "文件是 UTF-16 编码；RimWorld 存档是 UTF-8"; return false; }
        var text = Encoding.UTF8.GetString(span).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text.Length == 0) { problem = "文件只有空白"; return false; }
        if (text.StartsWith("<savegame", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            // The declaration alone is not proof - any XML starts that way - but a declaration followed by
            // `savegame` inside the sniffed window is, and that is the shape RimWorld writes.
            return text.Contains("<savegame", StringComparison.OrdinalIgnoreCase);
        }
        if (text.StartsWith("<!--", StringComparison.Ordinal)) return text.Contains("<savegame", StringComparison.OrdinalIgnoreCase);
        problem = text[0] == '<'
            ? "这不是 RimWorld 存档：根元素不是 <savegame>"
            : "这不是 RimWorld 存档：文件不以 XML 开头（前 " + PreviewBytes + " 字节：" + Preview(prologue, length) + "）";
        return false;
    }

    /// <summary>Printable preview of the first bytes, for an error a human has to act on.</summary>
    public static string Preview(ReadOnlySpan<byte> buffer, int length)
    {
        var take = Math.Min(Math.Min(length, buffer.Length), PreviewBytes);
        var builder = new StringBuilder(take * 2);
        for (var index = 0; index < take; index++)
        {
            var b = buffer[index];
            if (b is >= 0x20 and < 0x7F) builder.Append((char)b);
            else builder.Append("\\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a direct child file of <paramref name="directory"/>.
    ///
    /// Lexical, not resolved: in the container deployment the Saves folder is a symlink into the mounted data
    /// volume, so resolving it would compare two different-looking names for the same file and refuse a
    /// legitimate delete. The traversal defence comes from the prefix check plus "no separator in the remainder",
    /// which is what makes a nested path impossible.
    /// </summary>
    public static bool IsDirectChild(string? directory, string path)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(directory);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var remainder = full[root.Length..];
            return remainder.Length > 0 && !remainder.Contains(Path.DirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The name an upload is stored under. Never derived from anything the client sent: a client-chosen file name
    /// is a path, and a path is how an upload overwrites something it should not.
    /// </summary>
    public static string NewName(DateTime utc, string suffix) =>
        "import-" + utc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-" + suffix;

    /// <summary>The one name a downloaded shared snapshot is copied into <c>Saves/</c> under.
    ///
    /// <para>It lives here, next to the other naming rules, because it is a naming rule and nothing more - the game
    /// only needs *a* name it can pass to <c>LoadGame</c>. It used to be the snapshot's content hash, and since every
    /// checkpoint has a new hash that meant one new 20 MB file per reload: measured on the production server as 24
    /// hash-named files, each of which looks exactly like a colony the player saved.</para>
    ///
    /// <para>One constant means one file. A load overwrites the previous copy instead of adding to it, and a crash
    /// mid-load leaves one leftover rather than one per attempt.</para></summary>
    public const string TransportName = "TurboTickShared";

    /// <summary>The save the server loads on its next start: the colony as the operator left it.
    ///
    /// <para>Distinct from <see cref="TransportName"/> on purpose. The transport name is the copy of a *received*
    /// snapshot and is deleted after the load; this one is written after every committed checkpoint and must survive
    /// restarts, because it is what makes a restart resume the colony instead of reloading the original import at
    /// tick 0.</para></summary>
    public const string ResumeName = "TurboTickCurrent";

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
