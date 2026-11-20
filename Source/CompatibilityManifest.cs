using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboTick.Networking;
using TurboTick.Simulation;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Conservative content/order/config fingerprint. Capture Verse objects on the game thread; hash on a worker.</summary>
/// <param name="Folders">The mod's own folder plus every subfolder its <c>LoadFolders.xml</c> activates for this game
/// version, relative to the mod root and using <c>/</c>. Hashing the whole root instead was measured to be wrong twice
/// on the same install: a `local.washingmachine.bak/` backup directory (a player's or an updater's copy) contributed
/// 14 files that no game version ever loads, and two mods then hashed differently while both displaying an identical
/// mod list with identical versions. Only what the loader actually reads may enter the hash.</param>
internal sealed record ModRoot(string Id, string Directory, string[] Folders);
internal sealed record ManifestInputs(string GameVersion, ModRoot[] Mods, string ConfigDirectory);

internal static class CompatibilityManifest
{
    public static ManifestInputs Capture() => new(
        VersionControl.CurrentVersion.ToString(),
        LoadedModManager.RunningModsListForReading.Select(m => new ModRoot(Identity(m), m.RootDir, Folders(m))).ToArray(),
        GenFilePaths.ConfigFolderPath);

    /// <summary>The folders this mod contributes for the running game version, normalised to <c>/</c> and without a
    /// leading <c>./</c>.
    ///
    /// <para>Uses the game's own resolution (<c>ModContentPack.AllLoadFolders</c>) rather than re-reading
    /// <c>LoadFolders.xml</c>, so conditional entries such as
    /// <c>&lt;li IfModActive="Dubwise.DubsBadHygiene"&gt;1.7/Compatibility/DubsBadHygiene&lt;/li&gt;</c> are honoured
    /// on both sides. Falls back to the mod root alone if the game cannot answer, which is the old behaviour and
    /// still correct for a mod with no <c>LoadFolders.xml</c>.</para></summary>
    private static string[] Folders(ModContentPack mod)
    {
        try
        {
            var meta = mod.ModMetaData;
            if (meta is null) return [""];
            var folders = ModContentPack.AllLoadFolders(meta);
            var normalised = folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(folder => folder.Replace('\\', '/').Trim().TrimEnd('/'))
                .Select(folder => folder == "." ? "" : folder)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(folder => folder, StringComparer.Ordinal)
                .ToArray();
            return normalised.Length > 0 ? normalised : [""];
        }
        catch (Exception) { return [""]; }
    }

    /// <summary>The package id to hash, without the <c>_steam</c> suffix the game adds at load time.
    ///
    /// Measured: a player who keeps both a local copy and a subscribed copy of the same mod gets
    /// <c>brrainz.harmony_steam</c> while the server, having one copy, gets <c>brrainz.harmony</c>. The suffix
    /// describes *that machine's folder layout*, not the mod, so hashing it made two identical mod packs
    /// incompatible and the handshake refused the join with a fingerprint mismatch that pointed at nothing.
    /// The game's own <c>ModMetaData.PackageIdNonUnique</c> exists for exactly this comparison.</summary>
    private static string Identity(ModContentPack mod) => SaveManifest.CanonicalId(mod.PackageId ?? "");

    public static ClientHello Build(ManifestInputs input, string playerName, string exportFile, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rows = new List<object>();
        void Append(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            hash.AppendData(bytes);
        }
        Append("TurboTick-manifest-v2");
        Append(input.GameVersion);
        // Mods are hashed in package-id order, not load order.
        //
        // Measured: `ModsConfig.TrySortMods()` runs a topological sort over the `loadBefore`/`loadAfter` edges, and
        // mods that declare neither keep their *relative input order*. So the resulting order depends on the order
        // of `activeMods` in each machine's own `ModsConfig.xml` - a client that happens to list Harmony first and
        // Core second sorts differently from a server that forces Core first, and two byte-identical mod packs then
        // hash differently. Hashing load order made the fingerprint a statement about the machine's config history
        // rather than about the mods, and the handshake refused joins that had nothing wrong with them.
        //
        // Order is not dropped: `ModManifest.Compare` reports `OutOfOrder` from the list carried in the same
        // handshake, so a real ordering difference is still named, and named in the terms the player can act on.
        foreach (var mod in input.Mods.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            Append("mod:" + mod.Id.ToLowerInvariant()); // Sorted above; sort only files inside each mod.
            foreach (var (relative, path) in ContentFiles(mod))
            {
                ct.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(path);
                string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
                Append(relative + ":" + digest);
                rows.Add(new { mod = mod.Id, file = relative, sha256 = digest });
            }
        }
        if (Directory.Exists(input.ConfigDirectory))
        {
            // Only settings belonging to a mod that is actually loaded can change the simulation.
            var ownedPrefixes = input.Mods
                .Select(mod => ModSettingsOwnership.Prefix(mod.Id, GenText.SanitizeFilename))
                .ToArray();
            foreach (var file in Directory.EnumerateFiles(input.ConfigDirectory, "Mod_*.xml")
                .Where(p => !Path.GetFileName(p).Contains("turbotick", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(p).Contains("mpturbo", StringComparison.OrdinalIgnoreCase))
                .Where(p => !ModSettingsOwnership.IsDeploymentLocal(Path.GetFileName(p)))
                .Where(p => ModSettingsOwnership.IsOwnedBy(Path.GetFileName(p), ownedPrefixes))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(file);
                var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
                var name = Path.GetFileName(file);
                Append("config:" + name + ":" + digest);
                rows.Add(new { mod = "config", file = name, sha256 = digest });
            }
        }
        string fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        Directory.CreateDirectory(Path.GetDirectoryName(exportFile)!);
        File.WriteAllText(exportFile, JsonSerializer.Serialize(new
        {
            schema = 2, gameVersion = input.GameVersion, modHash = fingerprint, files = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        // The list travels with the hash: without it a mismatch can only be announced, not explained. Each entry
        // carries a short digest of that mod's own files so the verification table can say *which* mod differs when
        // the list and the versions agree but the join is still refused - that case had no row at all before.
        return new ClientHello(playerName, input.GameVersion, fingerprint, NetworkProtocol.ClientVersion,
            ModManifest.Serialize(ModManifestGame.Local()));
    }

    /// <summary>Short per-mod digest of exactly the files that entered the global hash, keyed by canonical package id.
    ///
    /// <para>Short (16 hex chars) because it only has to disagree when the bytes disagree; computing and sending 64
    /// chars per mod for a 200-mod pack would spend a kilobyte of the handshake on a hint. Folding the same path and
    /// digest pairs as the global hash means "this mod differs" always lines up with "the fingerprints differ" -
    /// two independent notions of content would be worse than none.</para>
    ///
    /// <para>Cached for the session, and <b>that is a correctness requirement, not an optimisation</b>: the handshake
    /// and the verification dialog each read this, and if they hashed at different moments they could disagree about
    /// what this machine has. A mod set cannot change without a restart, so one answer per process is the only
    /// consistent one.</para></summary>
    internal static Dictionary<string, string> Digests()
    {
        if (cachedDigests is not null) return cachedDigests;
        try { cachedDigests = ModDigests(Capture()); }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 计算模组摘要失败：" + error.Message);
            cachedDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return cachedDigests;
    }

    private static Dictionary<string, string>? cachedDigests;

    /// <summary>When each mod's hashed content was last written, as Unix seconds, keyed by canonical package id.
    ///
    /// <para>Sending this next to the digest turns "文件不同 1" into "this one is older here than there", which is the
    /// difference between a puzzle and an instruction. Measured twice on the same mod: Steam re-downloaded
    /// <c>assssssqwww.visualtweaks</c> on the client at 13:48 while the server still held the 12:20 copy, and both
    /// sides reported a content mismatch with identical size and name - the only outward difference was the mtime.</para>
    ///
    /// <para>Taken as the newest mtime among the hashed files, not of the mod directory: a directory's own timestamp
    /// changes when an unrelated file is added, and the copied-in dll is exactly the file whose age matters. Zero
    /// when nothing could be read, which reads as "unknown" and must never be shown as a date.</para></summary>
    internal static Dictionary<string, long> Stamps()
    {
        if (cachedStamps is not null) return cachedStamps;
        var stamps = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var mod in Capture().Mods)
            {
                long newest = 0;
                foreach (var (_, path) in ContentFiles(mod))
                {
                    try
                    {
                        var written = new FileInfo(path).LastWriteTimeUtc;
                        var seconds = new DateTimeOffset(written).ToUnixTimeSeconds();
                        if (seconds > newest) newest = seconds;
                    }
                    catch (Exception) { }
                }
                stamps[mod.Id.ToLowerInvariant()] = newest;
            }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 读取模组时间戳失败：" + error.Message);
        }
        cachedStamps = stamps;
        return stamps;
    }

    private static Dictionary<string, long>? cachedStamps;

    /// <summary>Mods whose hashed content was rewritten since the previous launch, newest mtime per mod.
    ///
    /// <para>Steam updates a workshop item silently between sessions, and the only symptom on this setup is a refused
    /// handshake that names no cause - measured three times in one day on <c>assssssqwww.visualtweaks</c>: the client
    /// was re-downloaded at 09:54, 12:20 and 13:48 while the server kept the copy it had been given. Nobody can
    /// notice a silent file replacement, so the process records what it saw last time and says what changed.</para>
    ///
    /// <para>The record lives beside the settings rather than inside the compatibility hash: this is install history,
    /// and putting it in the fingerprint would make two machines with identical content refuse each other over the
    /// order in which they happened to extract it.</para></summary>
    internal static (string Id, string Name, long Stamped)[] UpdatedSinceLastLaunch { get; private set; } = [];

    /// <summary>Compares this launch's stamps with the recorded ones, then records today's set. Safe to call once
    /// per process; the result is what the startup notice and the HUD read.</summary>
    internal static void DetectUpdates()
    {
        try
        {
            var stamps = Stamps();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var mod in Capture().Mods) names[mod.Id.ToLowerInvariant()] = mod.Id;
            }
            catch (Exception) { }
            var path = Path.Combine(GenFilePaths.ConfigFolderPath, "TurboTick", "mod-stamps.json");
            Dictionary<string, long>? previous = null;
            try
            {
                if (File.Exists(path))
                    previous = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            }
            catch (Exception error) { Log.Warning("[TurboTick] 读取上次模组时间戳失败：" + error.Message); }
            if (previous is { Count: > 0 })
            {
                // Only an increase counts. A *decrease* is a rollback or a fresh extract from an older archive, and
                // calling that an update would send the operator looking for a version that was never published.
                var changed = stamps
                    .Where(pair => previous.TryGetValue(pair.Key, out var was) && was > 0 && pair.Value > was + 10)
                    .Select(pair => (pair.Key, names.GetValueOrDefault(pair.Key, pair.Key), pair.Value))
                    .ToArray();
                UpdatedSinceLastLaunch = changed;
                if (changed.Length > 0)
                    Log.Message("[TurboTick] 检测到 " + changed.Length + " 个模组自上次启动后被更新："
                        + string.Join("，", changed.Select(entry => entry.Item2)));
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(stamps));
            }
            catch (Exception error) { Log.Warning("[TurboTick] 记录模组时间戳失败：" + error.Message); }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 模组更新检测失败：" + error.Message);
        }
    }

    private static Dictionary<string, string> ModDigests(ManifestInputs input)
    {
        var digests = new Dictionary<string, string>(input.Mods.Length, StringComparer.Ordinal);
        foreach (var mod in input.Mods)
        {
            try
            {
                using var local = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var (relative, path) in ContentFiles(mod))
                {
                    using var stream = File.OpenRead(path);
                    var line = relative + ":" + Convert.ToHexStringLower(SHA256.HashData(stream)) + "\n";
                    local.AppendData(Encoding.UTF8.GetBytes(line));
                }
                digests[mod.Id.ToLowerInvariant()] = Convert.ToHexStringLower(local.GetHashAndReset())[..16];
            }
            catch (Exception) { }
        }
        return digests;
    }

    /// <summary>Every file of one mod that the loader will actually read, ordered by its path inside the mod.
    ///
    /// <para>Only the folders named by <c>LoadFolders.xml</c> are walked, and inside each of them only the directories
    /// RimWorld names. Walking the whole root recursively is what made two identical installs incomparable on
    /// `192.168.0.29`: the washing machine mod's root contained a `local.washingmachine.bak/` copy of itself, 14 of
    /// its files entered the server's hash, the client had none of them, and the client was refused while the table
    /// said "22 mods, all identical". Note that `<c>&lt;li&gt;/&lt;/li&gt;</c>` means the mod root, so "only walk the
    /// listed folders" is not enough by itself - what is omitted is recursion into arbitrary subdirectories.</para>
    ///
    /// <para>`LoadFolders.xml` lives in the mod root and decides which folders exist, so it is always included even
    /// though it is not inside a named content directory.</para></summary>
    private static IEnumerable<(string Relative, string Path)> ContentFiles(ModRoot mod)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<(string Relative, string Path)>();
        foreach (var folder in mod.Folders)
        {
            var baseDir = folder.Length == 0 ? mod.Directory : Path.Combine(mod.Directory, folder);
            foreach (var name in ContentDirectories)
            {
                foreach (var contentDir in SubdirectoriesNamed(baseDir, name))
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories); }
                    catch (Exception) { continue; }
                    foreach (var path in files) Add(path);
                }
            }
            // `About/About.xml` rides along with its directory; the other two files in there are icons.
            foreach (var aboutDir in SubdirectoriesNamed(baseDir, "About"))
            {
                foreach (var about in FilesNamed(aboutDir, "About.xml")) Add(about);
            }

            void Add(string path)
            {
                var relative = Path.GetRelativePath(mod.Directory, path).Replace('\\', '/');
                if (!seen.Add(relative)) return;
                if (!IsSimulationFile(relative)) return;
                found.Add((relative, path));
            }
        }
        // `LoadFolders.xml` decides which folders exist, so it is always hashed even though it is not inside a
        // named content directory.
        foreach (var loadFolders in FilesNamed(mod.Directory, "LoadFolders.xml"))
        {
            if (seen.Add("LoadFolders.xml")) found.Add(("LoadFolders.xml", loadFolders));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return found;
    }

    /// <summary>Immediate subdirectories of <paramref name="parent"/> whose name matches, case-insensitively.
    ///
    /// <para><b>Case matters here because the two machines disagree about it.</b> The server runs Linux inside a
    /// container and the client runs Windows: `Path.Combine(dir, "Defs")` finds `Defs`, `defs` or `DEFS` on the
    /// client and only the exact spelling on the server. Measured on `192.168.0.29`: two mods ship
    /// `loadFolders.xml` while two others ship `LoadFolders.xml`, so a literal-name lookup found the file on one
    /// side only and the verification table reported two phantom `文件不同` rows for byte-identical mods.</para></summary>
    private static IEnumerable<string> SubdirectoriesNamed(string parent, string name)
    {
        if (!Directory.Exists(parent)) yield break;
        string[] found;
        try { found = Directory.GetDirectories(parent); }
        catch (Exception) { yield break; }
        foreach (var path in found)
            if (Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) yield return path;
    }

    /// <summary>Files directly inside <paramref name="parent"/> whose name matches, case-insensitively. Same reason as
    /// <see cref="SubdirectoriesNamed"/>.</summary>
    private static IEnumerable<string> FilesNamed(string parent, string name)
    {
        if (!Directory.Exists(parent)) yield break;
        string[] found;
        try { found = Directory.GetFiles(parent); }
        catch (Exception) { yield break; }
        foreach (var path in found)
            if (Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) yield return path;
    }

    /// <summary>The directories RimWorld reads content from, relative to a load folder.
    ///
    /// <para>`Defs` and `Patches` are XML; `Assemblies` are the dlls; there is nothing else this fingerprint hashes.
    /// Languages, Textures and Sounds are excluded by <see cref="IsSimulationFile"/> anyway, so they are not walked -
    /// a texture pack is hundreds of files and cannot change the simulation.</para></summary>
    private static readonly string[] ContentDirectories = ["Defs", "Patches", "Assemblies"];

    private static bool IsSimulationFile(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        // Development/build trees never count as loaded mod content.
        if (parts.Any(p => p.Equals("Source", StringComparison.OrdinalIgnoreCase)
                        || p.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || p.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith('.'))) return false;
        var extension = Path.GetExtension(relative);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return parts.Any(p => p.Equals("Assemblies", StringComparison.OrdinalIgnoreCase));
        if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return false;
        return parts.Any(p => p.Equals("Defs", StringComparison.OrdinalIgnoreCase)
                           || p.Equals("Patches", StringComparison.OrdinalIgnoreCase))
            || parts.Last().Equals("LoadFolders.xml", StringComparison.OrdinalIgnoreCase)
            || (parts.Length == 2 && parts[0].Equals("About", StringComparison.OrdinalIgnoreCase)
                                  && parts[1].Equals("About.xml", StringComparison.OrdinalIgnoreCase));
    }
}

