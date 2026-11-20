using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Reports what content actually loaded, so two peers can be compared on the thing that matters rather than on
/// what happens to be on disk.
///
/// The verification rig caught a real trap: the two machines' <c>Data/</c> folders are not identical - one has
/// Core plus the four expansions, the other only Core - while <c>ModsConfig.xml</c> lists the same three active
/// mods and <c>knownExpansions</c> is empty on both. Directory listings and XML file counts therefore say nothing
/// about which defs are loaded: only <c>DefDatabase</c> does. <c>ModsConfig.cs:99-161</c> decides expansion
/// membership from what was <em>known</em>, not from what is on disk, so guessing from folders can produce a
/// confident and wrong diagnosis in either direction.
/// </summary>
internal static class ContentProbe
{
    /// <summary>A short digest of the loaded def counts plus the active mod list, printed at startup and carried
    /// in the divergence dump. Two peers that print the same line loaded the same content.</summary>
    /// <summary>Content only: everything in here must match on both peers. Load order is deliberately excluded
    /// and reported separately - it legitimately differed between operating systems before the fix, so folding it
    /// into this digest would make "the fingerprints match" impossible and destroy the one assertion that says a
    /// run is comparable at all.</summary>
    internal static string Describe()
    {
        var builder = new StringBuilder();
        try
        {
            builder.Append("mods=").Append(string.Join(",",
                LoadedModManager.RunningModsListForReading.Select(m => m.PackageId.ToLowerInvariant())));
            // From the loaded expansion defs, not from ModsConfig internals: what matters is which expansions the
            // running game believes it has, and that is exactly what DefDatabase reflects.
            builder.Append(" expansion=").Append(string.Join(",",
                DefDatabase<ExpansionDef>.AllDefsListForReading
                    .Where(d => !d.isCore)
                    .Select(d => d.defName)
                    .OrderBy(name => name, StringComparer.Ordinal)));
            builder.Append(" defs=");
            // Counts for the def types that decide simulation behaviour. A missing expansion moves several of
            // these at once, so one line is enough to spot it.
            builder.Append("Interaction=").Append(Count<InteractionDef>());
            builder.Append(",Incident=").Append(Count<RimWorld.IncidentDef>());
            builder.Append(",Thing=").Append(Count<ThingDef>());
            builder.Append(",Hediff=").Append(Count<HediffDef>());
            builder.Append(",MentalState=").Append(Count<MentalStateDef>());
            builder.Append(",Recipe=").Append(Count<RecipeDef>());
            // Work giver order is derived from def load order and decides which work a pawn picks, so it belongs
            // in the same line as the content it comes from.
            builder.Append(" ").Append(DefLoadOrder.DescribeWorkGivers());

        }
        catch (Exception error)
        {
            builder.Append("error=").Append(error.GetType().Name);
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return digest[..16].ToLowerInvariant() + " " + builder;
    }

    private static string Count<T>() where T : Def
    {
        try { return DefDatabase<T>.DefCount.ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { return "?"; }
    }
}






