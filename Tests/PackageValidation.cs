using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static class PackageValidation
{
    /// <summary>The directory that holds About/About.xml, found by walking up from the test binary.</summary>
    public static string PackageRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "About", "About.xml")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new Exception("Repository package root not found.");
    }

    public static void Validate()
    {
        var root = PackageRoot();
        void Require(bool valid, string text) { if (!valid) throw new Exception(text); }
        var about = XDocument.Load(Path.Combine(root, "About", "About.xml")).Root!;
        Require(about.Element("name")?.Value == "TurboTick", "canonical mod title");
        Require(about.Element("packageId")?.Value == "lumidust.turbotick", "stable package identity");
        Require(about.Element("url")?.Value == "https://github.com/LumiDust/TurboTick", "canonical repository URL");
        var buildVersion = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
            .Root!.Element("PropertyGroup")!.Element("Version")!.Value;
        Require(about.Element("modVersion")?.Value == buildVersion, "single release version agrees with mod metadata");
        Require(TurboTick.Networking.NetworkProtocol.ClientVersion == buildVersion,
            "handshake version agrees with the mod and server release");
        var expectedAssemblyVersion = new Version(buildVersion + ".0");
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Require(!readme.Contains("MPTurbo", StringComparison.OrdinalIgnoreCase), "legacy product name remains in README");
        var publicIntro = readme.Split("## 致谢", 2)[0];
        Require(!publicIntro.Contains("Multiplayer", StringComparison.Ordinal)
             && !publicIntro.Contains("RimWorld Together", StringComparison.Ordinal), "reference projects belong in acknowledgments only");
        var preview = Path.Combine(root, "About", "Preview.png");
        var image = File.ReadAllBytes(preview);
        Require(image.Length < 1024 * 1024 && image.Length > 24, "Workshop preview must be below 1 MiB");
        Require(image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "preview must be a PNG");
        Require(BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4)) == 640
             && BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4)) == 360, "project preview convention: 640x360");
        var versions = about.Element("supportedVersions")!.Elements("li").Select(x => x.Value).ToArray();
        Require(versions.SequenceEqual(new[] { "1.7" }), "only RimWorld 1.7 may be declared");
        var dependencies = about.Element("modDependencies")!.Elements("li").Select(x => x.Element("packageId")!.Value).ToArray();
        Require(dependencies.SequenceEqual(new[] { "brrainz.harmony" }), "reference mods must never be dependencies");
        var allowed = new[] { "TurboTick", "TurboTick.Networking", "TurboTick.Simulation" };
        var files = Directory.GetFiles(Path.Combine(root, "1.7", "Assemblies"), "*.dll");
        Require(files.Select(Path.GetFileNameWithoutExtension).Order().SequenceEqual(allowed.Order()),
            "stale/third-party/game DLL accidentally bundled");
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            Require(metadata.GetAssemblyDefinition().Version == expectedAssemblyVersion, "bundled assembly version differs from release metadata");
            var refs = metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle))
                .Select(r => (Name: metadata.GetString(r.Name), r.Version)).ToArray();
            Require(!refs.Any(r => r.Name.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase)
                               || r.Name.Contains("RTClient", StringComparison.OrdinalIgnoreCase)
                               || r.Name.StartsWith("UnityEngine.", StringComparison.Ordinal)), "forbidden assembly reference");
            Require(refs.Any(r => r.Name == "System.Runtime" && r.Version.Major == 10), "assembly must target .NET 10");
            if (Path.GetFileName(file) != "TurboTick.dll")
                Require(!refs.Any(r => r.Name is "Assembly-CSharp" or "RimWorld" or "UnityStubs" or "0Harmony"),
                    "headless networking/simulation must not load game assemblies");
            else
            {
                var component = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
                    .Single(t => metadata.GetString(t.Namespace) == "TurboTick" && metadata.GetString(t.Name) == "TurboGameComponent");
                Require(component.GetMethods().Select(handle => metadata.GetMethodDefinition(handle)).Any(m =>
                    metadata.GetString(m.Name) == ".ctor" && m.GetParameters().Select(p => metadata.GetParameter(p))
                        .Any(p => p.SequenceNumber == 1 && metadata.GetString(p.Name) == "game")),
                    "RimWorld Game.FillComponents expects the public TurboGameComponent(Game game) constructor");
            }
        }
        var chinese = XDocument.Load(Path.Combine(root, "Languages", "ChineseSimplified", "Keyed", "TurboTick.xml"));
        var english = XDocument.Load(Path.Combine(root, "Languages", "English", "Keyed", "TurboTick.xml"));
        Require(chinese.Root!.Elements().GroupBy(x => x.Name.LocalName).All(g => g.Count() == 1)
             && english.Root!.Elements().GroupBy(x => x.Name.LocalName).All(g => g.Count() == 1), "duplicate translation keys");
        var keys = chinese.Root!.Elements().Select(x => x.Name.LocalName).ToHashSet();
        Require(keys.SetEquals(english.Root!.Elements().Select(x => x.Name.LocalName)), "translation key parity");
        var sources = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "Source"), "*.cs").Select(File.ReadAllText));
        foreach (Match match in Regex.Matches(sources, "\"(TurboTick_[A-Za-z0-9_]+)\""))
            Require(keys.Contains(match.Groups[1].Value), "missing translation " + match.Groups[1].Value);
        Require(!sources.Contains("Multiplayer.Client.", StringComparison.Ordinal)
             && !sources.Contains("Multiplayer.API.", StringComparison.Ordinal)
             && !sources.Contains("RTClient.", StringComparison.Ordinal), "runtime integration with reference mods remains");
    }
}

