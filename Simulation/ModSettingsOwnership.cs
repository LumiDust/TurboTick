namespace TurboTick.Simulation;

/// <summary>
/// Which files in RimWorld's Config folder belong to a mod that is actually loaded.
///
/// RimWorld writes mod settings as <c>Mod_&lt;packageId&gt;_&lt;settingsClass&gt;.xml</c> and never removes them, so a
/// player who has tried a lot of mod packs accumulates hundreds of files for mods they no longer run. Hashing
/// all of them made the compatibility fingerprint depend on the player's whole mod history - a tester with 207
/// leftovers could not join a host who had none, although both ran the same three mods.
///
/// The rule lives here rather than in the mod assembly so it can be tested without the game.
/// </summary>
public static class ModSettingsOwnership
{
    /// <summary>
    /// True when <paramref name="fileName"/> is a settings file of one of <paramref name="prefixes"/>, where a
    /// prefix is <c>Mod_&lt;sanitised packageId&gt;_</c> produced by <see cref="Prefix"/>.
    /// </summary>
    public static bool IsOwnedBy(string fileName, IReadOnlyCollection<string> prefixes)
    {
        foreach (var prefix in prefixes)
            if (fileName.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The file-name prefix a mod's settings files share. <paramref name="sanitize"/> must be the game's own
    /// sanitiser; passing it in keeps this file free of game references while still matching exactly what
    /// RimWorld writes.
    /// </summary>
    public static string Prefix(string packageId, Func<string, string> sanitize)
        => sanitize("Mod_" + packageId + "_");

    /// <summary>Settings the deployment decides, not the player, and which the fingerprint must ignore on *both*
    /// sides or no two machines can ever agree.
    ///
    /// Measured: `Mod_acutus.atmosphereoverhaul.release_AtmosphereOverhaulMod.xml` carrying
    /// <c>weatherEngineV1=1</c> exists only on the server, because the container has to write it before launch -
    /// AtmosphereOverhaul's default value selects its own WebGPU climate engine, which has no adapter in a
    /// container and stops the world permanently. The client has the same mod with no such file, so hashing
    /// config *files* made every join fail with a fingerprint mismatch that named nothing.
    ///
    /// The exclusion is by file name and is unconditional: a player who happens to have the file left over is
    /// skipped as well, so the rule cannot depend on who wrote it last. The boundary is deliberate - this setting
    /// chooses a weather *implementation*, and display replicas never run the simulation, so it cannot change
    /// what the world does. A mod setting that genuinely feeds simulation must not be listed here.</summary>
    public static bool IsDeploymentLocal(string fileName)
    {
        foreach (var suffix in DeploymentLocalSuffixes)
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Suffixes of <see cref="IsDeploymentLocal"/>. The folder name before the suffix varies between
    /// installs (`atmosphereoverhaul.release` vs `AtmosphereOverhaul`), the settings class at the end does not.</summary>
    private static readonly string[] DeploymentLocalSuffixes = ["_AtmosphereOverhaulMod.xml"];
}
