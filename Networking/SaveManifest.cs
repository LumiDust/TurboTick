using System.Xml;
using System.Xml.Linq;

namespace TurboTick.Networking;

/// <summary>One entry from a save's <c>&lt;meta&gt;</c> block, with the three parallel lists already joined.</summary>
public sealed record SaveMod(string Id, long SteamId, string Name)
{
    /// <summary>A workshop item the server could download. <c>0</c> means "not from the workshop".</summary>
    public bool FromWorkshop => SteamId > 0;
}

/// <summary>
/// What a save file says it was made with.
///
/// RimWorld writes three lists into <c>&lt;meta&gt;</c> and they are **parallel by index**:
/// <code>
/// &lt;modIds&gt;      brrainz.harmony, ludeon.rimworld, lumidust.turbotick
/// &lt;modSteamIds&gt; 0, 0, 0
/// &lt;modNames&gt;    Harmony, Core, TurboTick
/// </code>
/// That correspondence is the whole reason this type exists: <c>modSteamIds</c> is what decides whether a
/// missing mod can be fetched automatically (non-zero = workshop id) or must be put there by hand.
///
/// A short list is not an error. A save written by an older build, or hand-edited, can have fewer steam ids
/// than package ids; those entries are reported with <c>SteamId = 0</c> and therefore land in "manual", which
/// is the safe answer - guessing an id could download the wrong content.
/// </summary>
public sealed record SaveModManifest(string? GameVersion, IReadOnlyList<SaveMod> Mods, IReadOnlyList<string> KnownExpansions, string? Problem)
{
    public static SaveModManifest Empty { get; } = new(null, [], [], "no-meta");
}

public static class SaveManifest
{
    /// <summary>The game's own ids, so the classifier never has to guess from a name.</summary>
    public const string CoreId = "ludeon.rimworld";
    public const string HarmonyId = "brrainz.harmony";

    /// <summary>
    /// The suffix the game appends to a packageId when two copies of the same mod are installed.
    ///
    /// <para>Measured in 1.7 <c>Verse.ModMetaData</c>: <c>PackageId</c> returns <c>packageIdLowerCase</c> plus
    /// <c>SteamModPostfix</c> when <c>appendPackageIdSteamPostfix</c> is set, and <c>PackageIdNonUnique</c> always
    /// returns the bare form. <c>ModLister.TryAddMod</c> is what sets the flag, and only when a local copy and a
    /// workshop copy of the same packageId are both discovered: the workshop one gets the postfix so the two can
    /// coexist.</para>
    ///
    /// <para>That makes the postfix a property of <em>the machine that wrote the save</em>, not of the mod. A save
    /// written where both copies exist names <c>brrainz.harmony_steam</c>; a server holding a single copy registers
    /// it as <c>brrainz.harmony</c>, and without this normalisation the save is reported as needing a mod that
    /// could never be installed. The game's own comparison is <c>ModMetaData.SamePackageId(other, ignorePostfix:
    /// true)</c>, which compares against <c>PackageIdNonUnique</c> - exactly the value this returns.</para>
    /// </summary>
    public const string SteamPostfix = "_steam";

    /// <summary>
    /// This mod's own packageId.
    ///
    /// <para>Every save a player makes on a TurboTick client names it, so the save-driven mod sync sees a
    /// third-party requirement and tries to satisfy it like any other. Measured on the deployment host: that
    /// path finds workshop item <c>3834465013</c> - a 0.6.1 snapshot of this very mod - and copies it next to
    /// the 0.7.1 the server actually runs. The game then discovers two directories declaring the same
    /// packageId, and <c>Verse.ModLister.TryAddMod</c> only resolves that when exactly one of the two is a
    /// workshop item:</para>
    /// <code>
    /// if (mod.OnSteamWorkshop != modWithIdentifier.OnSteamWorkshop)
    ///     { ...append "_steam" to the workshop one and retry... }
    /// Log.Error("Tried loading mod with the same packageId multiple times: " ...);
    /// return false;                                  // whichever was scanned second is dropped
    /// </code>
    /// <para>Inside the container Steam is not running, so <c>WorkshopItems.AllSubscribedItems</c> is empty and
    /// both copies report <c>ContentSource.ModsFolder</c> - <c>OnSteamWorkshop</c> is false for both, the
    /// postfix branch is never taken, and the surviving copy is decided by directory enumeration order. Half
    /// the time the server would boot the old build of itself.</para>
    ///
    /// <para>The server ships this mod; it must never be fetched, linked or substituted.</para>
    /// </summary>
    public const string TurboTickId = "lumidust.turbotick";

    /// <summary>packageIds that belong to the server itself, checked in canonical form.</summary>
    public static IReadOnlyList<string> SelfIds { get; } = [TurboTickId];

    /// <summary>True when the save is naming the mod that is running this server.</summary>
    public static bool IsSelf(string id)
    {
        var canonical = CanonicalId(id);
        foreach (var self in SelfIds)
            if (string.Equals(canonical, self, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The packageId a mod is known by regardless of which copy registered it: the save's id with any
    /// <see cref="SteamPostfix"/> removed and lower-cased.
    /// </summary>
    public static string CanonicalId(string id)
    {
        var lowered = id.Trim().ToLowerInvariant();
        return lowered.EndsWith(SteamPostfix, StringComparison.Ordinal) && lowered.Length > SteamPostfix.Length
            ? lowered[..^SteamPostfix.Length]
            : lowered;
    }

    public static IReadOnlyList<string> Dlcs { get; } =
    [
        "ludeon.rimworld.royalty",
        "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech",
        "ludeon.rimworld.anomaly",
        "ludeon.rimworld.odyssey"
    ];

    /// <summary>
    /// Reads <c>&lt;meta&gt;</c> from a save without loading it into the game.
    ///
    /// Streaming on purpose: a 20 MB colony holds a huge game graph and the only thing needed here is a few
    /// hundred bytes at the top. <see cref="XmlReader"/> stops as soon as the meta element closes.
    /// </summary>
    public static SaveModManifest Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            return Read(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SaveModManifest.Empty with { Problem = error.GetType().Name + ": " + error.Message };
        }
    }

    public static SaveModManifest Read(Stream stream)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false,
                CloseInput = false
            };
            using var reader = XmlReader.Create(stream, settings);
            string? gameVersion = null;
            var ids = new List<string>();
            var steam = new List<string>();
            var names = new List<string>();
            var expansions = new List<string>();
            var inMeta = false;
            var field = "";
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "meta") { inMeta = true; continue; }
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "meta") break;
                if (!inMeta) continue;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    field = reader.Name;
                    // `<gameVersion>1.7.5000 rev1275</gameVersion>` is a leaf; the three mod lists are not.
                    if (field is "gameVersion" or "modIds" or "modSteamIds" or "modNames" or "knownExpansions")
                    {
                        if (field == "gameVersion")
                        {
                            if (reader.Read() && reader.NodeType == XmlNodeType.Text) gameVersion = reader.Value.Trim();
                            continue;
                        }
                        using var subtree = reader.ReadSubtree();
                        var node = XElement.Load(subtree);
                        var target = field switch
                        {
                            "modIds" => ids,
                            "modSteamIds" => steam,
                            "modNames" => names,
                            _ => expansions
                        };
                        foreach (var item in node.Elements("li"))
                            target.Add((item.Value ?? "").Trim());
                    }
                }
            }
            if (ids.Count == 0 && names.Count == 0)
                return new SaveModManifest(gameVersion, [], expansions, "no-mod-list");
            var mods = new List<SaveMod>(ids.Count);
            for (var index = 0; index < ids.Count; index++)
            {
                var id = ids[index];
                if (id.Length == 0) continue;
                // A missing or unparsable steam id is 0 = "not from the workshop" = manual. Never guess.
                long.TryParse(index < steam.Count ? steam[index] : "0", out var workshop);
                var name = index < names.Count ? names[index] : "";
                mods.Add(new SaveMod(id, workshop < 0 ? 0 : workshop, name.Length > 0 ? name : id));
            }
            return new SaveModManifest(gameVersion, mods, expansions, null);
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException or ArgumentException)
        {
            return SaveModManifest.Empty with { Problem = "不是可解析的存档：" + error.Message };
        }
    }

    /// <summary>
    /// Core / DLC / Harmony / third-party, decided from the packageId, never from a display name.
    ///
    /// Classified on the canonical id: a save written where a mod existed in both local and workshop form names it
    /// <c>brrainz.harmony_steam</c>, and a literal comparison would file Harmony as just another third-party mod -
    /// which then means "missing, put it there by hand" instead of "this is the loader, it must be here".
    /// </summary>
    public static SaveModSource Classify(string id)
    {
        var lowered = CanonicalId(id);
        if (lowered == CoreId) return SaveModSource.Core;
        if (lowered == HarmonyId) return SaveModSource.Harmony;
        if (Dlcs.Contains(lowered)) return SaveModSource.Dlc;
        // The save names this mod because the client had it loaded; the server *is* it. Classifying it as
        // third-party would send it through the download path, which is how a 0.6.1 copy ended up beside the
        // 0.7.1 the server runs. See TurboTickId for the collision this prevents.
        if (IsSelf(lowered)) return SaveModSource.Self;
        return SaveModSource.ThirdParty;
    }
}

public enum SaveModSource
{
    Core,
    Dlc,
    Harmony,
    ThirdParty,
    /// <summary>The server's own mod, named by every save a client made. Never downloaded, never replaced.</summary>
    Self
}
