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

    public static string DownloadPath(string id)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, id + ".rws");
    }

    /// <summary>
    /// Moves a downloaded save into the game's Saves folder and returns its save name.
    /// LoadGame resolves names under Saves/, so a file elsewhere is not loadable even with a full path.
    /// The copy in Saves/ is removed again after loading, so a peer does not accumulate one opaque save per
    /// checkpoint it has ever loaded.
    /// </summary>
    public static string PrepareLoad(string downloadedPath)
    {
        var name = Path.GetFileNameWithoutExtension(downloadedPath);
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
    /// Deleting at LoadGame time instead does nothing: the load is a long event, the file is still open, and
    /// the delete fails silently.</summary>
    public static void PruneSaves(int keep = 0)
    {
        try
        {
            var ours = new DirectoryInfo(GenFilePaths.SaveDataFolderPath).GetFiles("*.rws")
                .Where(file =>
                {
                    // Only our own artefacts: the server names snapshots by content hash, and a checkpoint
                    // save carries the prefix. Anything the player named is left alone.
                    var stem = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    return stem.StartsWith("TurboTickCheckpoint_", StringComparison.Ordinal)
                        || (stem.Length == 64 && stem.All(Uri.IsHexDigit));
                })
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            for (var i = keep; i < ours.Length; i++) TryDelete(ours[i].FullName);
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
