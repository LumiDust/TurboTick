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
}
