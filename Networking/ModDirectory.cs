using System.Xml;
using System.Xml.Linq;

namespace TurboTick.Networking;

/// <summary>One mod directory found on disk, identified by the packageId in its <c>About/About.xml</c>.</summary>
public sealed record InstalledMod(string PackageId, string Name, string Directory, long WorkshopId, string? Problem);

/// <summary>
/// Enumerates a mods directory without Verse.
///
/// This is deliberately file IO only: the same enumeration has to be available to the HTTP thread (which must
/// not touch <see cref="Verse.ModLister"/>) and to the game thread, and the answer has to be "what would the
/// game find on disk", not "what does the current session happen to have loaded". The two can differ - a mod
/// dropped in while the process runs is on disk but not active - and the difference matters exactly when a save
/// is about to be started against it.
/// </summary>
public static class ModDirectory
{
    /// <summary>Directories are scanned one level deep: RimWorld's mod root is a flat list of folders.</summary>
    public static IReadOnlyList<InstalledMod> Scan(string? root)
    {
        var found = new List<InstalledMod>();
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root)) return found;
        IEnumerable<string> directories;
        try { directories = System.IO.Directory.EnumerateDirectories(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return found; }
        foreach (var directory in directories)
        {
            var about = Path.Combine(directory, "About", "About.xml");
            if (File.Exists(about))
            {
                if (TryRead(directory, out var mod) && mod is not null) found.Add(mod);
                continue;
            }
            // Steam's workshop layout nests content under <root>/294100/<itemid>/; accept that one extra level
            // so a workshop cache pointed straight at the mods directory still resolves.
            var inner = Path.Combine(directory, "294100");
            foreach (var child in SafeDirectories(inner) ?? [])
                if (TryRead(child, out var nested) && nested is not null) found.Add(nested);
        }
        return found;
    }

    /// <summary>
    /// Enumerates workshop item directories, one level below the app id.
    ///
    /// Measured layout on the deployment host: <c>&lt;game&gt;/steamapps/workshop/content/294100/&lt;itemid&gt;/</c>
    /// with <c>About/About.xml</c> directly inside the item id. Item folders are numeric, which is also what makes
    /// "is this a workshop item" answerable without reading the files.
    /// </summary>
    public static IReadOnlyList<InstalledMod> ScanWorkshop(string? contentRoot)
    {
        var found = new List<InstalledMod>();
        if (string.IsNullOrWhiteSpace(contentRoot)) return found;
        foreach (var item in SafeDirectories(contentRoot) ?? [])
        {
            if (TryRead(item, out var mod) && mod is not null) found.Add(mod);
            else foreach (var nested in SafeDirectories(item) ?? [])
                if (TryRead(nested, out var deeper) && deeper is not null) found.Add(deeper);
        }
        return found;
    }

    /// <summary>
    /// Reads one mod directory, whatever it is: an unpacked mod root, or a workshop item that nests its real
    /// content one level down.
    ///
    /// <para>This is what closes the download loop - steamcmd puts content at
    /// <c>.../workshop/content/294100/&lt;id&gt;/</c> and the only trustworthy statement about what was just
    /// fetched is the <c>packageId</c> that directory declares. Returns null when there is no readable
    /// <c>About/About.xml</c> anywhere under it.</para>
    /// </summary>
    public static InstalledMod? Describe(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (File.Exists(Path.Combine(directory, "About", "About.xml")))
        {
            // An About.xml that exists but has no packageId is a real answer ("this is not a usable mod"), so it is
            // returned rather than papered over by looking deeper for a different one.
            TryRead(directory, out var direct);
            return direct;
        }
        foreach (var nested in SafeDirectories(directory) ?? [])
            if (TryRead(nested, out var deeper) && deeper is not null) return deeper;
        return null;
    }

    private static string[]? SafeDirectories(string path)
    {
        try { return System.IO.Directory.Exists(path) ? System.IO.Directory.GetDirectories(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TryRead(string directory, out InstalledMod? mod)
    {
        mod = null;
        var about = Path.Combine(directory, "About", "About.xml");
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(about, settings);
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root is null) return false;
            var packageId = (root.Element("packageId")?.Value ?? "").Trim().ToLowerInvariant();
            if (packageId.Length == 0)
            {
                mod = new InstalledMod("", Path.GetFileName(directory), directory, 0, "About.xml 没有 packageId");
                return false;
            }
            var name = (root.Element("name")?.Value ?? Path.GetFileName(directory)).Trim();
            long.TryParse((root.Element("steamAppId")?.Value ?? "0").Trim(), out var workshop);
            var published = Path.Combine(directory, "About", "PublishedFileId.txt");
            if (workshop == 0 && File.Exists(published))
            {
                // A workshop mod usually records its own item id here rather than as a steamAppId.
                var text = File.ReadAllText(published).Trim();
                if (long.TryParse(text, out var id)) workshop = id;
            }
            mod = new InstalledMod(packageId, name, directory, workshop < 0 ? 0 : workshop, null);
            return true;
        }
        catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            mod = new InstalledMod("", Path.GetFileName(directory), directory, 0, error.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Where the DLC content lives. Core ships inside <c>Data/Core</c>; the five expansions are siblings, and a
    /// server that staged them from a mounted folder has the same layout.
    /// </summary>
    public static IReadOnlyList<string> DataDirectories(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return [];
        var data = Path.Combine(gameDirectory, "Data");
        try { return System.IO.Directory.Exists(data) ? System.IO.Directory.GetDirectories(data) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The lower-cased packageIds whose content is present under <c>Data/</c>.</summary>
    public static ISet<string> DataPackageIds(string? gameDirectory)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in DataDirectories(gameDirectory))
        {
            if (TryRead(directory, out var mod) && mod is not null && mod.PackageId.Length > 0) ids.Add(mod.PackageId);
        }
        return ids;
    }
}
