using System.Xml.Linq;

namespace TurboTick.Networking;

/// <summary>
/// Reads the parts of <c>ModsConfig.xml</c> the administration page must preserve when it rewrites the active
/// list.
///
/// The game treats this file as authoritative at startup: a missing <c>knownExpansions</c> block makes it
/// believe the player owns no DLC, which is how a "just enable one mod" button can silently strip five
/// expansions. Parsing it separately - instead of regenerating the file from scratch - is what keeps the write
/// additive.
/// </summary>
public sealed record ModsConfigSnapshot(string? Version, IReadOnlyList<string> ActiveMods,
    IReadOnlyList<string> KnownExpansions, string? Raw)
{
    public static ModsConfigSnapshot Empty { get; } = new(null, [], [], null);
}

public static class ModsConfigFile
{
    /// <summary>RimWorld's packageId alphabet; also what keeps a hostile id out of the XML and the log.</summary>
    public static bool IsValidPackageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var value = id.Trim();
        if (value.Length is < 3 or > 160) return false;
        if (!value.Contains('.')) return false;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        return value[0] != '.' && value[^1] != '.';
    }

    /// <summary>Parses without throwing: a hand-edited or truncated file yields what could be read.</summary>
    public static ModsConfigSnapshot Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return ModsConfigSnapshot.Empty with { Raw = xml };
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var root = document.Root;
            if (root is null) return ModsConfigSnapshot.Empty with { Raw = xml };
            var version = root.Element("version")?.Value?.Trim();
            var active = ReadList(root.Element("activeMods"));
            var expansions = ReadList(root.Element("knownExpansions"));
            return new ModsConfigSnapshot(string.IsNullOrWhiteSpace(version) ? null : version, active, expansions, xml);
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException or ArgumentException)
        {
            return ModsConfigSnapshot.Empty with { Raw = xml };
        }
    }

    public static ModsConfigSnapshot Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : ModsConfigSnapshot.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ModsConfigSnapshot.Empty;
        }
    }

    private static List<string> ReadList(XElement? parent)
    {
        var items = new List<string>();
        if (parent is null) return items;
        foreach (var element in parent.Elements("li"))
        {
            var value = element.Value?.Trim();
            if (!string.IsNullOrEmpty(value)) items.Add(value);
        }
        return items;
    }

    /// <summary>
    /// Applies a requested active list on top of what is already on disk: order preserved exactly as asked,
    /// duplicates dropped, every other section of the file - notably <c>knownExpansions</c> - kept.
    ///
    /// Core is <em>guaranteed present</em>, not forced to the front. Forcing it first was my own invention and it
    /// contradicted the game: Harmony ships <c>&lt;loadBefore&gt;&lt;li&gt;Ludeon.RimWorld&lt;/li&gt;&lt;/loadBefore&gt;</c>,
    /// so a save written by a working client records <c>brrainz.harmony</c> at index 0 and Core at index 1. Rewriting
    /// that into Core-first put the list out of the order the world was built under, and
    /// <c>ScribeMetaHeaderUtility.ModListsMatch</c> compares the lists element by element - the save then reports a
    /// mod-list change on every load. The game does not need Core at index 0 to start: it needs Core active.
    /// </summary>
    public sealed record ApplyResult(string Xml, IReadOnlyList<string> Active, IReadOnlyList<string> Added, IReadOnlyList<string> Dropped,
        IReadOnlyList<string> PreservedExpansions);

    public static ApplyResult Apply(ModsConfigSnapshot current, IEnumerable<string> requested, string fallbackVersion)
    {
        var active = new List<string>(32);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<string>();
        var added = new List<string>();
        var hadCore = false;
        foreach (var raw in requested)
        {
            var id = raw?.Trim() ?? "";
            if (id.Length == 0) continue;
            if (!IsValidPackageId(id)) { dropped.Add(id); continue; }
            if (!seen.Add(id)) continue;
            if (id.Equals(ModsConfigPlan.Core, StringComparison.OrdinalIgnoreCase)) hadCore = true;
            active.Add(id);
        }
        if (!hadCore)
        {
            // Absent entirely: nothing can start without it, so this is the one case where position is decided here.
            active.Insert(0, ModsConfigPlan.Core);
            added.Add(ModsConfigPlan.Core);
        }
        var version = string.IsNullOrWhiteSpace(current.Version) ? fallbackVersion : current.Version!;
        var xml = ModsConfigPlan.Xml(version, active, current.KnownExpansions);
        return new ApplyResult(xml, active, added, dropped, current.KnownExpansions);
    }
}
