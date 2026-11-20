using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboTick.Portal;

/// <summary>
/// One command, written to disk and answered by the game process.
///
/// The HTTP threads used to hand work to the game thread through an in-process queue. That queue cannot cross a
/// process boundary, and the whole point of this executable is that the page survives the game process exiting -
/// so the queue becomes a directory: <c>&lt;data&gt;/portal-commands/&lt;id&gt;.json</c> goes in,
/// <c>&lt;id&gt;.done.json</c> comes back. Polling is cheap because the game already ticks at 60 Hz and the
/// file system here is a tmpfs-backed bind mount.
///
/// Ordering matters for exactly one pair of operations: a checkpoint must be committed <i>before</i> the process
/// is told to exit. That is why "switch world" is a single command the game executes end to end (checkpoint →
/// write the restart marker → exit) rather than two commands the page sequences - a page that crashes between
/// them would otherwise leave a half-switched server.
/// </summary>
internal static class CommandInbox
{
    /// <summary>How long the page waits for the game to pick a command up and finish it.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    private static string directory = "";

    internal static void Configure(string dataDirectory)
    {
        directory = Path.Combine(dataDirectory, "portal-commands");
        try { System.IO.Directory.CreateDirectory(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning("[TurboTick portal] 无法创建命令目录：" + error.Message);
        }
    }

    internal static string Folder => directory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Sends one command and waits for the answer.
    ///
    /// Every failure mode is a sentence rather than an exception: the game may be down (no answer, ever), it may be
    /// stuck in a long event (answer arrives too late), or it may have died between accepting and answering. The
    /// page shows whichever happened; none of them are allowed to look like success.
    /// </summary>
    internal static async Task<CommandOutcome> SendAsync(string name, string? argument, TimeSpan timeout,
        CancellationToken token)
    {
        if (directory.Length == 0) return CommandOutcome.Bad("no-inbox", "命令目录未初始化");
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var request = Path.Combine(directory, id + ".json");
        var answer = Path.Combine(directory, id + ".done.json");
        var payload = JsonSerializer.Serialize(new { id, name, argument, at = DateTime.UtcNow.ToString("O") }, Json);
        try
        {
            // Write-then-rename: the game lists the directory, so a half-written request must never be visible.
            File.WriteAllText(request + ".tmp", payload);
            File.Move(request + ".tmp", request, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return CommandOutcome.Bad("inbox-unwritable", "无法写入命令文件：" + error.Message);
        }

        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(answer))
                {
                    var outcome = ReadAnswer(answer);
                    TryDelete(answer);
                    TryDelete(request);
                    return outcome;
                }
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(request);
            return CommandOutcome.Bad("cancelled", "请求在等待游戏进程时被取消");
        }
        // Dropped rather than left behind: a command file the game reads minutes later would be executed against a
        // world the operator has already moved on from.
        TryDelete(request);
        return CommandOutcome.Bad("command-timeout",
            "命令 " + name + " 没有在 " + (int)timeout.TotalSeconds + " 秒内完成（游戏进程可能已停止，或被长时间事件阻塞）");
    }

    private static CommandOutcome ReadAnswer(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "" : "";
            return new CommandOutcome(ok, code, detail);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return CommandOutcome.Bad("answer-unreadable", "命令回执无法解析：" + error.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes answer files a dead game left behind, so the directory does not grow without bound.</summary>
    internal static void Sweep(TimeSpan olderThan)
    {
        if (directory.Length == 0) return;
        try
        {
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed record CommandOutcome(bool Ok, string Code, string Detail)
{
    internal static CommandOutcome Good(string code, string detail) => new(true, code, detail);
    internal static CommandOutcome Bad(string code, string detail) => new(false, code, detail);
}
