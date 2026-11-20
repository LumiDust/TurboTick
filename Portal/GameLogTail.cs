namespace TurboTick.Portal;

/// <summary>One line of the game's own log, as the page shows it.</summary>
internal sealed record GameLogEntry(string Type, string Text, int Repeats);

/// <summary>
/// Reads the tail of the game's stdout without owning the process that writes it.
///
/// The game's log used to be reachable in memory (<c>Verse.Log.Messages</c>), which is impossible once the game
/// runs in a separate process. Two sources remain: the supervisor's captured output, and the file the entrypoint
/// tees stdout to. Both are read here, tolerantly: a log being appended to while it is read is normal, and a page
/// that throws because of it would be worse than one that shows slightly fewer lines.
/// </summary>
internal static class GameLogTail
{
    /// <summary>How much of the file is inspected. Colonist logs are noisy; the last page of them is enough.</summary>
    private const int MaximumBytes = 96 * 1024;

    /// <summary>
    /// Returns up to <paramref name="count"/> lines, newest last.
    ///
    /// Populated lines are typed by their own text, because the file has no severity column: the game writes plain
    /// stdout, and the only level information available is the wording it chose. Guessing is documented rather than
    /// hidden - a line saying "Exception" is an error, a line saying "Warning" is a warning, everything else is
    /// ordinary output.
    /// </summary>
    internal static IReadOnlyList<GameLogEntry> Tail(string dataDirectory, int count)
    {
        var lines = new List<GameLogEntry>(count);
        foreach (var name in new[] { "game-stdout.log", "rimworld-stdout.log" })
        {
            var path = Path.Combine(dataDirectory, name);
            var read = ReadTail(path, count);
            if (read.Count == 0) continue;
            lines = read;
            break;
        }
        return lines;
    }

    private static List<GameLogEntry> ReadTail(string path, int count)
    {
        var result = new List<GameLogEntry>(count);
        try
        {
            if (!File.Exists(path)) return result;
            // Shared read/write/delete: the writer keeps the handle open for the lifetime of the game process, so
            // any stricter share mode here would fail every single time rather than occasionally.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - MaximumBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var split = text.Replace("\r\n", "\n").Split('\n');
            // A partial first line is an artefact of seeking into the middle of the file, not content.
            var first = start > 0 ? 1 : 0;
            var last = split.Length - 1;
            var begin = Math.Max(first, last - count);
            for (var index = begin; index < last; index++)
            {
                var line = split[index];
                if (line.Length == 0) continue;
                result.Add(new GameLogEntry(Classify(line), line, 0));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    /// <summary>
    /// The severity a plain-stdout line implies. Deliberately conservative: <c>Log.Error</c> in Verse writes
    /// "Exception" or "Error" into the text, warnings write "Warning", and everything else is not worth colouring.
    /// </summary>
    private static string Classify(string line)
    {
        if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Fatal", StringComparison.OrdinalIgnoreCase)) return "Error";
        if (line.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Warn", StringComparison.OrdinalIgnoreCase)) return "Warning";
        return "Message";
    }
}
