using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboTick.Networking;

namespace TurboTick.Portal;

/// <summary>
/// The save library, the mod manager and the DLC report: everything the page needed to stop being read-only
/// about the deployment itself.
///
/// Three rules carry over from the main file and are worth restating because these endpoints are the ones that
/// delete and replace files:
///
/// 1. <b>Nothing the client sends is trusted as a path.</b> An upload is stored under a name this server
///    generates; a delete only accepts a name that passes <see cref="SaveLibrary.IsValidName"/> and resolves to a
///    direct child of the configured directory.
/// 2. <b>No half-written files.</b> Uploads land in <c>.tmp</c> and are renamed only after the hash and the XML
///    sniff both pass, so a dropped connection cannot leave something that looks like a save.
/// 3. <b>Reads never go through Verse.</b> The save directory, the mods config path and the DLC catalog are all
///    handed over by the game thread at startup or published as immutable snapshots.
/// </summary>
internal static partial class AdminPage
{
    internal const string UploadPath = "/api/saves/upload";

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Stamp, string Sha)> HashCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SaveWrite = new();
    private static volatile string? currentSaveName;
    private static volatile string lastUpload = "—";

    // ---------------------------------------------------------------- upload (streamed)

    /// <summary>
    /// Receives a <c>.rws</c> as a raw body and stores it under a name this server chooses.
    ///
    /// Streamed, not buffered: a real colony is 10-25 MB and the declared limit is 256 MB, so the bytes go
    /// straight to a temp file while being hashed. The sniff happens on the first bytes of that temp file, and
    /// the rename is the commit - a rejected upload deletes its temp file and leaves the directory untouched.
    /// </summary>
    private static async Task ReceiveUploadAsync(NetworkStream stream, AdminHttpRequest request, byte[] buffer,
        int headLength, int available, CancellationToken token)
    {
        if (!await AuthorizeAsync(stream, request, write: true, token).ConfigureAwait(false)) return;
        if (string.IsNullOrEmpty(config.SavesDirectory))
        {
            await ReplyAsync(stream, 503, JsonError("saves-directory-missing", "服务端不知道存档目录（未从游戏线程拿到）"), token).ConfigureAwait(false);
            return;
        }
        var declared = request.ContentLength;
        if (declared <= 0)
        {
            await ReplyAsync(stream, 400, JsonError("empty-upload", "请求体为空：请直接把 .rws 的原始字节作为 body 发送"), token).ConfigureAwait(false);
            return;
        }
        if (declared > SaveImport.MaximumBytes)
        {
            await ReplyAsync(stream, 413, JsonError("upload-too-large",
                "请求体 " + declared + " 字节，超过上限 " + SaveImport.MaximumBytes + " 字节"), token).ConfigureAwait(false);
            return;
        }
        var name = SaveImport.NewName(DateTime.UtcNow, Guid.NewGuid().ToString("N")[..6]);
        var directory = config.SavesDirectory;
        var target = SaveLibrary.PathFor(directory, name);
        var temporary = target + ".tmp";
        long received = 0;
        string sha;
        try
        {
            Directory.CreateDirectory(directory);
            await using var sink = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var prologue = new byte[512];
            var prologueLength = 0;
            var chunk = new byte[UploadChunkBytes];
            // Bytes already read while looking for the head belong to the body.
            if (available > 0)
            {
                var first = Math.Min(available, declared);
                buffer.AsSpan(headLength, first).CopyTo(chunk);
                received += first;
                hash.AppendData(chunk, 0, first);
                await sink.WriteAsync(chunk.AsMemory(0, first), token).ConfigureAwait(false);
                prologueLength = CopyPrologue(chunk, first, prologue);
            }
            while (received < declared)
            {
                var want = (int)Math.Min(chunk.Length, declared - received);
                var count = await stream.ReadAsync(chunk.AsMemory(0, want), token).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
                hash.AppendData(chunk, 0, count);
                if (prologueLength < prologue.Length) prologueLength = CopyPrologue(chunk.AsSpan(0, count), 0, prologue, prologueLength);
                await sink.WriteAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
            }
            await sink.FlushAsync(token).ConfigureAwait(false);
            if (received != declared)
            {
                sink.Close();
                TryDeleteFile(temporary);
                await ReplyAsync(stream, 400, JsonError("short-upload",
                    "只收到 " + received + "/" + declared + " 字节，已丢弃"), token).ConfigureAwait(false);
                return;
            }
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            // Read the head back from the temp file rather than trusting the in-memory prologue: measured, a 15 MB
            // upload reported prologueLength=0 while `received` had the full size and the sha256 matched, i.e. the
            // in-memory witness disagreed with what actually reached the disk. The file is the truth.
            var sniff = new byte[512];
            var sniffLength = 0;
            try
            {
                sink.Flush();
                sink.Close();
                using (var probe = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    sniffLength = probe.Read(sniff, 0, sniff.Length);
            }
            catch (Exception error) { Log.Warning("[TurboTick admin] 回读临时文件失败：" + error.Message); }
            if (!SaveImport.LooksLikeSave(sniff, sniffLength, out var problem))
            {
                TryDeleteFile(temporary);
                Log.Warning("[TurboTick admin] 上传被拒（" + problem + "）：" + received + " 字节，sha256=" + sha
                    + "，回读=" + sniffLength + " 字节，头=" + Convert.ToHexStringLower(sniff.AsSpan(0, Math.Min(8, sniffLength)))
                    + "，prologueLength=" + prologueLength + "，available=" + available + "，headLength=" + headLength);
                await ReplyAsync(stream, 400, JsonError("not-a-save", problem), token).ConfigureAwait(false);
                return;
            }
            sink.Close();
            lock (SaveWrite)
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDeleteFile(temporary);
            Log.Warning("[TurboTick admin] 上传失败：" + error.GetType().Name + ": " + error.Message);
            if (error is OperationCanceledException) return;
            await ReplyAsync(stream, 500, JsonError("upload-failed", error.GetType().Name + ": " + error.Message), token).ConfigureAwait(false);
            return;
        }
        var stored = new FileInfo(target);
        var verify = Sha256Of(target);
        lastUpload = name + " · " + stored.Length + " B · sha256 " + sha[..12] + (verify == sha ? " · 落盘校验一致" : " · 落盘校验不一致！");
        if (verify != sha) Log.Warning("[TurboTick admin] 上传落盘后 sha256 不一致：" + name + " 收到 " + sha + " 落盘 " + verify);
        else Log.Message("[TurboTick admin] 已导入存档 " + name + ".rws（" + stored.Length + " 字节，sha256 " + sha + "）");
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            name,
            file = Path.GetFileName(target),
            bytes = stored.Length,
            sha256 = sha,
            sha256OnDisk = verify,
            directory,
            detail = "已导入并落盘；用 /api/server/start 或页面上的「用这个存档启动」载入它"
        }, Json);
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    private static int CopyPrologue(ReadOnlySpan<byte> chunk, int count, byte[] prologue, int already = 0)
    {
        var room = prologue.Length - already;
        if (room <= 0) return already;
        var take = Math.Min(room, count);
        chunk[..take].CopyTo(prologue.AsSpan(already));
        return already + take;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) { Log.Warning("[TurboTick admin] 删除临时文件失败：" + path + " " + error.Message); }
    }

    /// <summary>
    /// sha256 of a save, cached by (length, mtime). Hashing 20 MB per listing would make the 15-second refresh on
    /// the page read the whole library continuously; the cache turns that into one read per changed file.
    /// </summary>
    internal static string? Sha256Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (HashCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Stamp == info.LastWriteTimeUtc)
                return cached.Sha;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            HashCache[path] = (info.Length, info.LastWriteTimeUtc, sha);
            return sha;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- delete

    /// <summary>
    /// Deletes one save, or several when the body carries a list. Refusals are deliberate: a traversal-shaped name is
    /// a 400 rather than a "not found", and a save the authority is running is a 409 because deleting it would remove
    /// the world players are attached to.
    ///
    /// <para>Bulk delete exists because the library grows to a hundred entries and the list view is where an operator
    /// tidies up: one request per row turned "clear out the old imports" into a hundred round trips, each of which
    /// could fail on its own and leave the operator clicking through messages. A list is processed item by item and
    /// reports per-name outcomes, so one refused save (the one in use) does not abort the rest.</para>
    /// </summary>
    private static async Task DeleteSaveAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var names = ReadDeleteNames(request.Body);
        if (names.Count == 0)
        {
            await ReplyAsync(stream, 400, JsonError("name-required",
                "请求体应为 {\"name\":\"<存档名>\"} 或 {\"names\":[\"<存档名>\", …]}"), token).ConfigureAwait(false);
            return;
        }
        var deleted = new List<string>(names.Count);
        var refused = new List<object>();
        var current = currentSaveName;
        foreach (var raw in names)
        {
            // Anything with a separator, a drive letter or a control character is refused as invalid, not looked up.
            if (raw.Contains('/') || raw.Contains('\\') || raw.Contains(':') || raw.Contains("..") || raw.Any(char.IsControl))
            {
                refused.Add(new { name = raw, code = "invalid-name", detail = "存档名不能包含路径分隔符、':' 或 '..'" });
                continue;
            }
            var name = SaveLibrary.Normalize(raw);
            if (name is null)
            {
                refused.Add(new { name = raw, code = "invalid-name",
                    detail = "存档名不合法：" + SaveLibrary.MaximumNameLength + " 字符以内，不能以点开头或结尾" });
                continue;
            }
            var path = SaveLibrary.PathFor(config.SavesDirectory, name);
            if (!SaveImport.IsDirectChild(config.SavesDirectory, path))
            {
                refused.Add(new { name = name, code = "invalid-name", detail = "解析后的路径不在存档目录内，已拒绝" });
                continue;
            }
            if (!string.IsNullOrEmpty(current) && string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
            {
                refused.Add(new { name = name, code = "save-in-use", detail = "正在被权威端使用；先换档或停服再删除" });
                continue;
            }
            lock (SaveWrite)
            {
                if (!File.Exists(path))
                {
                    refused.Add(new { name = name, code = "save-missing", detail = "找不到 " + name + ".rws" });
                    continue;
                }
                try
                {
                    File.Delete(path);
                    HashCache.TryRemove(path, out _);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Log.Warning("[TurboTick admin] 删除存档失败：" + name + " " + error.Message);
                    refused.Add(new { name = name, code = "delete-failed", detail = error.GetType().Name + ": " + error.Message });
                    continue;
                }
            }
            deleted.Add(name);
        }
        // One save keeps the original single-object payload, so a page that only ever deletes one row still reads
        // `data.ok` and `data.name` without knowing this route learned to take a list.
        if (names.Count == 1 && deleted.Count == 1)
        {
            var only = deleted[0];
            Log.Message("[TurboTick admin] 已删除存档 " + only + ".rws");
            await ReplyAsync(stream, 200, JsonSerializer.Serialize(
                new { ok = true, name = only, deleted = true, detail = "已删除 " + only + ".rws" }, Json), token).ConfigureAwait(false);
            return;
        }
        Log.Message("[TurboTick admin] 批量删除存档 " + deleted.Count + " 个（拒绝 " + refused.Count + " 个）"
            + (deleted.Count > 0 ? "：" + string.Join(", ", deleted) : ""));
        var payload = JsonSerializer.Serialize(new
        {
            ok = refused.Count == 0,
            deleted,
            refused,
            detail = "已删除 " + deleted.Count + " 个" + (refused.Count > 0 ? "，拒绝 " + refused.Count + " 个" : "")
        }, Json);
        await ReplyAsync(stream, deleted.Count > 0 ? 200 : 409, payload, token).ConfigureAwait(false);
    }

    /// <summary>The save names in a delete request: one string, or a list of them, JSON or form encoded.</summary>
    private static List<string> ReadDeleteNames(byte[] body)
    {
        var names = new List<string>(4);
        if (body.Length is 0 or > MaximumBodyBytes) return names;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return names;
            if (root.TryGetProperty("names", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(value)) names.Add(value.Trim());
                }
            }
            else if (root.TryGetProperty("name", out var single) && single.ValueKind == JsonValueKind.String)
            {
                var value = single.GetString();
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value.Trim());
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the form encoding the page also uses for single deletes.
            var form = FormValue(body, "name");
            if (!string.IsNullOrWhiteSpace(form)) names.Add(form.Trim());
        }
        return names;
    }

    // ---------------------------------------------------------------- mods

    private static async Task ModsAsync(NetworkStream stream, CancellationToken token)
    {
        var snapshot = mods ?? new AdminMods([], [], [], config.ModsConfigPath, null, [], 0);
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            configPath = snapshot.ConfigPath,
            configVersion = snapshot.ConfigVersion,
            activeCount = snapshot.Active.Count,
            installedCount = snapshot.InstalledCount,
            active = snapshot.Active,
            knownExpansions = snapshot.KnownExpansions,
            duplicates = snapshot.Duplicates,
            mods = snapshot.Mods,
            warning = "改动 activeMods 会改变 modHash：已存在的房间会拒绝新客户端，必须重启服务端（并清 <data>/state）后才一致。"
        }, Json);
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>activeMods</c> in the order the operator gave.
    ///
    /// Deliberately not a sorter: load order is a real decision (a patch must come after what it patches), and the
    /// page shows <c>loadAfter</c>/<c>loadBefore</c> so the operator can make it. What this does enforce is the
    /// two things the game will not start without - Core present and valid package ids only - and it preserves every
    /// other section of the file, <c>knownExpansions</c> above all. Core is not hoisted to index 0: Harmony declares
    /// <c>&lt;loadBefore&gt;Ludeon.RimWorld&lt;/loadBefore&gt;</c>, so the game's own topological sort puts Core at 1,
    /// and <c>ScribeMetaHeaderUtility.ModListsMatch</c> compares element by element.
    /// </summary>
    private static async Task ModsApplyAsync(NetworkStream stream, AdminHttpRequest request, CancellationToken token)
    {
        var active = ReadStringArray(request.Body, "active");
        if (active is null)
        {
            await ReplyAsync(stream, 400, JsonError("active-required",
                "请求体应为 {\"active\":[\"packageId\",…]}；当前列表见 /api/mods"), token).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrEmpty(config.ModsConfigPath))
        {
            await ReplyAsync(stream, 503, JsonError("config-path-missing", "服务端不知道 ModsConfig.xml 的位置"), token).ConfigureAwait(false);
            return;
        }
        var current = ModsConfigFile.Read(config.ModsConfigPath);
        var version = CurrentStatus().InstalledVersion ?? current.Version ?? "1.7";
        var applied = ModsConfigFile.Apply(current, active, version);
        string backup;
        try
        {
            var directory = Path.GetDirectoryName(config.ModsConfigPath)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(config.ModsConfigPath))
            {
                backup = config.ModsConfigPath + ".bak-"
                    + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                File.Copy(config.ModsConfigPath, backup, overwrite: false);
            }
            else backup = "";
            // Written through a temp file so an interrupted write cannot leave the game without a usable config.
            var temporary = config.ModsConfigPath + ".tmp";
            File.WriteAllText(temporary, applied.Xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, config.ModsConfigPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warning("[TurboTick admin] 写 ModsConfig.xml 失败：" + error.GetType().Name + ": " + error.Message);
            await ReplyAsync(stream, 500, JsonError("apply-failed", error.GetType().Name + ": " + error.Message), token).ConfigureAwait(false);
            return;
        }
        Log.Message("[TurboTick admin] 已改写 activeMods（" + applied.Active.Count + " 项）：" + string.Join(", ", applied.Active));
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            active = applied.Active,
            added = applied.Added,
            droppedInvalid = applied.Dropped,
            preservedKnownExpansions = applied.PreservedExpansions,
            backup,
            restartRequired = true,
            detail = "已写入 " + config.ModsConfigPath + "。modHash 会变化，必须重启服务端（并清 <data>/state），否则已有房间会拒绝新客户端。"
        }, Json);
        await ReplyAsync(stream, 200, payload, token).ConfigureAwait(false);
    }

    /// <summary>One array of strings from a JSON body. Null means "the field is missing", empty means "clear it".</summary>
    private static List<string>? ReadStringArray(byte[] body, string property)
    {
        if (body.Length is 0 or > MaximumBodyBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return null;
            var items = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text) items.Add(text);
            return items;
        }
        catch (JsonException) { return null; }
    }
}
