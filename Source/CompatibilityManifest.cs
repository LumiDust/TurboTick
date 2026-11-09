using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboTick.Networking;
using TurboTick.Simulation;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Conservative content/order/config fingerprint. Capture Verse objects on the game thread; hash on a worker.</summary>
internal sealed record ModRoot(string Id, string Directory);
internal sealed record ManifestInputs(string GameVersion, ModRoot[] Mods, string ConfigDirectory);

internal static class CompatibilityManifest
{
    public static ManifestInputs Capture() => new(
        VersionControl.CurrentVersion.ToString(),
        LoadedModManager.RunningModsListForReading.Select(m => new ModRoot(m.PackageId, m.RootDir)).ToArray(),
        GenFilePaths.ConfigFolderPath);

    public static ClientHello Build(ManifestInputs input, string playerName, string exportFile, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rows = new List<object>();
        void Append(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            hash.AppendData(bytes);
        }
        Append("TurboTick-manifest-v1");
        Append(input.GameVersion);
        foreach (var mod in input.Mods)
        {
            ct.ThrowIfCancellationRequested();
            Append("mod:" + mod.Id.ToLowerInvariant()); // Keep active load order; sort only files inside each mod.
            foreach (var path in Directory.EnumerateFiles(mod.Directory, "*", SearchOption.AllDirectories)
                .Where(p => IsSimulationFile(Path.GetRelativePath(mod.Directory, p)))
                .OrderBy(p => Path.GetRelativePath(mod.Directory, p).Replace('\\', '/'), StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(mod.Directory, path).Replace('\\', '/');
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
            schema = 1, gameVersion = input.GameVersion, modHash = fingerprint, files = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new ClientHello(playerName, input.GameVersion, fingerprint, NetworkProtocol.ClientVersion);
    }

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
