using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Save-file transport for the shared colony: chunked hashing plus a stable temp location.</summary>
internal static class WorldFiles
{
    public static SnapshotManifest Describe(byte[] data) => SnapshotBuilder.Describe(data);

    public static string Directory => Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTickShared");

    /// <summary>
    /// Saves the running colony and returns the file it actually produced. GameDataSaveLoader.SaveGame
    /// takes a save *name*, not a path, so the path has to be resolved through GenFilePaths afterwards.
    ///
    /// The result is copied out of Saves/ and the original is removed: a checkpoint is transient transport
    /// for the server, not something the player asked to keep. Leaving them there filled Saves/ with
    /// dozens of opaque hash names that are indistinguishable from real colonies - 69 files and 745 MB in
    /// one session.
    /// </summary>
    public static string SaveCurrentGame()
    {
        var name = "TurboTickCheckpoint_" + DateTime.UtcNow.Ticks;
        using (SharedSnapshotStorage.Enter()) GameDataSaveLoader.SaveGame(name);
        var path = GenFilePaths.FilePathForSavedGame(name);
        if (!File.Exists(path)) throw new FileNotFoundException("保存后找不到存档文件：" + path);
        var copy = System.IO.Path.Combine(Directory, "checkpoint.rws");
        System.IO.Directory.CreateDirectory(Directory);
        try { File.Copy(path, copy, overwrite: true); }
        finally { TryDelete(path); }
        return copy;
    }

    /// <summary>A checkpoint save is transport; failing to delete it must not fail the checkpoint.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) { Log.Warning("[TurboTick] 删除临时检查点存档失败：" + error.Message); }
    }

    /// <summary>The save the server should load on its *next* start: the colony as the operator left it.
    ///
    /// <para>Why this exists: the configured save (<c>TURBOTICK_SAVE</c>) is the world the room was created from, not
    /// the world it is in now. The shutdown path committed a checkpoint at tick 28316 and then the next container start
    /// loaded the import again at tick 0 - the player's wall, the research, the whole colony gone, with every log line
    /// saying the checkpoint had been accepted. The checkpoint file was complete on disk; nothing pointed the next
    /// start at it.</para>
    ///
    /// <para>The reason a plain copy into <c>Saves/</c> is used rather than pointing at
    /// <c>TurboTickShared/checkpoint.rws</c>: <c>LoadGame</c> resolves names against <c>Saves/</c> only, and that
    /// directory is the one the operator can see and delete if a run ever needs to start over.</para></summary>
    public const string ResumeName = TurboTick.Networking.SaveImport.ResumeName;

    /// <summary>The resume save's name when one has been captured, otherwise null - in which case the caller keeps
    /// whatever the operator configured. Checked on every start, so deleting the file in the admin page's save list is
    /// enough to make the next start go from the configured save again.
    ///
    /// <para>Also adopts a checkpoint already on disk when no resume save exists yet. That is not a convenience: the
    /// process that wrote <c>checkpoint.rws</c> may have been SIGKILLed (Docker's default stop, and what a deploy does
    /// to the old container), and the checkpoints published during that run are exactly as durable as one written on
    /// the way out - they are committed to the room's snapshot store before the file is copied here. Without the
    /// adoption, the first restart after a long run still loads the import at tick 0, because the resume file was
    /// never written by the process that died.</para></summary>
    public static string? ResumeSave
    {
        get
        {
            try
            {
                var path = GenFilePaths.FilePathForSavedGame(ResumeName);
                if (File.Exists(path) && new FileInfo(path).Length > 0) return ResumeName;
                // Nothing captured yet. If a checkpoint from an earlier run is on disk, promote it now - the next
                // process is this one's successor, and that checkpoint is the newest world anyone has.
                return CaptureResume() ? ResumeName : null;
            }
            catch (Exception error)
            {
                Log.Warning("[TurboTick] 检查续跑存档失败：" + error.Message);
                return null;
            }
        }
    }

    /// <summary>Copies the just-committed checkpoint into <c>Saves/</c> as <see cref="ResumeName"/>, so the next
    /// process starts from where this one stopped. Called after every committed checkpoint and once at startup.
    ///
    /// <para>A failure here is reported and swallowed: the checkpoint itself is already committed for the room, and a
    /// shutdown must not be turned into a crash by a copy.</para></summary>
    /// <returns>True when a resume save exists afterwards.</returns>
    public static bool CaptureResume()
    {
        try
        {
            var source = Path.Combine(Directory, "checkpoint.rws");
            if (!File.Exists(source))
            {
                Log.Warning("[TurboTick] 续跑存档未写入：没有 checkpoint.rws");
                return false;
            }
            System.IO.Directory.CreateDirectory(GenFilePaths.SavedGamesFolderPath);
            var target = GenFilePaths.FilePathForSavedGame(ResumeName);
            // Written beside the target and renamed, never copied over it. The process this protects against is
            // SIGKILL during a 20 MB copy - with a direct overwrite that leaves a truncated save in the one place the
            // next start reads first, which turns "the container was killed" into "the colony is unreadable". A rename
            // on one filesystem either happens or does not.
            var staging = target + ".part";
            File.Copy(source, staging, overwrite: true);
            File.Move(staging, target, overwrite: true);
            Log.Message($"[TurboTick] 续跑存档已写入：{ResumeName}.rws（{new FileInfo(target).Length / 1024} KiB），"
                + "下次启动从这里接着跑");
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 写续跑存档失败：" + error.Message);
            try
            {
                var leftover = GenFilePaths.FilePathForSavedGame(ResumeName) + ".part";
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception) { }
            return false;
        }
    }

    public static string DownloadPath(string id)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, id + ".rws");
    }

    /// <summary>中的固定名字：快照是传输介质，不是殖民地。
    ///
    /// <para>Why a fixed name and not the content id: every checkpoint produces a new content hash, so naming the copy
    /// in <c>Saves/</c> after it made **one new 20 MB save per reconnect** - measured on the production server as 24
    /// hash-named files, each indistinguishable from a colony the player saved. The copy only exists so
    /// <c>GameDataSaveLoader.LoadGame</c> can find it by name; which name it is has no meaning, so it is one constant
    /// and each load overwrites the previous one instead of accumulating.</para>
    ///
    /// <para>The delete still happens after the load (the game holds the file open during it), and
    /// <see cref="PruneSaves"/> treats this name as ours - so a crash mid-load costs one file, not one per attempt.
    /// </para></summary>
    /// <summary>中的固定名字：快照是传输介质，不是殖民地。取值与 <see cref="SnapshotManifest"/> 无关，由
    /// <c>SaveImport.TransportName</c> 定义（那一份是纯命名规则，测试不需要游戏程序集就能断言）。</summary>
    public static string TransportName => TurboTick.Networking.SaveImport.TransportName;

    /// <summary>基线下发落盘后，复制进 Saves/ 时用的名字。
    ///
    /// 必须避开玩家自己的存档：用内容 id 的前 16 位 + 固定后缀，既不与任何手写名字冲突，
    /// 也能在载入对话框里一眼看出「这是联机副本的临时存档」。载入完成后
    /// <see cref="PruneSaves"/> 会把它删掉——它是传输介质，不是玩家要留的殖民地。</summary>
    public static string BaselineSaveName(string id) =>
        (id.Length <= 16 ? id : id[..16]) + "-baseline";

    /// <summary>把下载/推送来的存档复制进 Saves/ 并返回可载入的名字。
    ///
    /// <c>LoadGame</c> 只认 Saves/ 下的名字，不认任意路径；副本因此必须落一份到 Saves/。
    /// 传了 <paramref name="saveName"/> 就用它（基线要避开玩家存档名），否则用 <see cref="TransportName"/> -
    /// 不是文件名的词干：词干是内容哈希，一次检查点一个，Saves/ 会因此每次重连多一个 20 MB 的「殖民地」。</summary>
    public static string PrepareLoad(string downloadedPath, string? saveName = null)
    {
        var name = saveName ?? TransportName;
        // 一个「本地完全没有存档」的客户端，Saves 目录可能压根不存在：先建再复制，
        // 否则 File.Copy 会抛 DirectoryNotFoundException，而那条路只会在没有存档的机器上出现。
        System.IO.Directory.CreateDirectory(GenFilePaths.SavedGamesFolderPath);
        var target = GenFilePaths.FilePathForSavedGame(name);
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(downloadedPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(downloadedPath, target, overwrite: true);
        return name;
    }

    /// <summary>Removes the transient copies this mod placed in Saves/. Called after a load finishes and at
    /// startup, when the game has released the file.
    ///
    /// Default is to remove all of them: these are pure transport, the world is already in memory, and a
    /// leftover is indistinguishable from a real colony in the load dialog. <c>keep</c> exists only for
    /// callers that want to retain the most recent one.
    ///
    /// <para>Scans <c>SavedGamesFolderPath</c>, not <c>SaveDataFolderPath</c>. The latter is the profile root; the
    /// saves live in its <c>Saves/</c> child, and <c>GetFiles</c> is not recursive - so this method used to enumerate
    /// the profile directory, find nothing, and report success. Nothing was ever pruned: measured as 24 leftover
    /// copies of 20 MiB each on the production server, one per reload, in a directory this code had been "cleaning"
    /// for weeks.</para>
    ///
    /// Deleting at LoadGame time instead does nothing: the load is a long event, the file is still open, and
    /// the delete fails silently.</summary>
    public static void PruneSaves(int keep = 0)
    {
        try
        {
            var ours = new DirectoryInfo(GenFilePaths.SavedGamesFolderPath).GetFiles("*.rws")
                .Where(file =>
                {
                    // Only our own artefacts: the transport copy has a fixed name, a baseline carries the prefix, and
                    // a checkpoint carries its own. Anything the player named is left alone.
                    var stem = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    return stem == TransportName
                        || stem.StartsWith("TurboTickCheckpoint_", StringComparison.Ordinal)
                        || stem.EndsWith("-baseline", StringComparison.Ordinal)
                        // Older builds named the transport copy after the snapshot's content hash. Those files are
                        // this mod's debris, not colonies, so the sweep still recognises and removes them - 24 of
                        // them at 20 MB each had accumulated on the production server before this line existed.
                        || (stem.Length == 64 && stem.All(Uri.IsHexDigit));
                })
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            for (var i = keep; i < ours.Length; i++) TryDelete(ours[i].FullName);
            // Said out loud because this sweep was silently finding nothing for weeks (wrong directory) and every
            // reading of "the cleanup runs at startup" looked healthy. A count is the only thing that distinguishes
            // "nothing to remove" from "looked in the wrong place".
            if (ours.Length > keep) Log.Message($"[TurboTick] 清理临时存档：删除 {ours.Length - keep} 个（保留 {keep}）");
        }
        catch (Exception error) { Log.Warning("[TurboTick] 清理临时存档失败：" + error.Message); }
    }

    /// <summary>Bounds the shared snapshot cache. Only the newest few can be relevant: the room keeps one
    /// current checkpoint, and an older one is only useful while a join is in flight.</summary>
    public static void PruneCache(int keep = 3)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var files = new DirectoryInfo(Directory).GetFiles("*.rws")
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            for (var i = keep; i < files.Length; i++) TryDelete(files[i].FullName);
        }
        catch (Exception error) { Log.Warning("[TurboTick] 清理快照缓存失败：" + error.Message); }
    }
}
