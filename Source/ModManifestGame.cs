using System.Xml.Linq;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The game-side half of <see cref="ModManifest"/>: reading the active list and rewriting it.
///
/// Kept apart from the networking half because the coordinator and the standalone networking tests have no reference
/// to RimWorld - `LoadedModManager` and `GenFilePaths` only exist inside the game process.
/// </summary>
internal static class ModManifestGame
{
    /// <summary>The local active mod list, in load order, exactly as the running game loaded it, including each mod's
    /// content digest.
    ///
    /// <para>The digest matters: without it a row can only compare list membership, version and position, so a room
    /// whose 22 mods are all present with matching versions but whose files differ shows **"全部一致"** and then
    /// refuses the join with nothing to blame. Measured on the player's own client (2026-11-15): every row said 一致
    /// while `assssssqwww.visualtweaks` was a different build on the two machines.</para>
    ///
    /// <para>Reading it here rather than at the handshake keeps the two views identical: the handshake and the
    /// verification dialog both call this method, so they cannot disagree about what this machine has.</para>
    /// </summary>
    internal static List<ModEntry> Local()
    {
        var digests = CompatibilityManifest.Digests();
        var stamps = CompatibilityManifest.Stamps();
        var entries = new List<ModEntry>();
        try
        {
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                // Canonical, for the same reason the content hash is: `_steam` marks that *this* machine keeps
                // both a local and a subscribed copy, so a client with two Harmonies would otherwise be told it
                // has a mod the server lacks and lacks one the server has - two fake entries for one mod.
                var id = SaveManifest.CanonicalId(mod.PackageId ?? "");
                entries.Add(new ModEntry(
                    id,
                    mod.Name ?? mod.PackageId ?? "",
                    i,
                    "0",
                    // `About.xml`'s <modVersion>. Read through ModMetaData because ModContentPack has no version of
                    // its own; a mod that never declared one yields "" and is not treated as a mismatch against a
                    // room that also left it empty.
                    SafeVersion(mod),
                    digests.GetValueOrDefault(id.ToLowerInvariant()) ?? "",
                    stamps.GetValueOrDefault(id.ToLowerInvariant())));
            }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 读取本地模组列表失败：" + error.Message);
        }
        return entries;
    }

    /// <summary>Rewrites the active list in <c>ModsConfig.xml</c> so the next launch loads exactly these mods in this
    /// order. Only that list is touched: other elements are preserved, because the game rejects a config it cannot
    /// parse and the player would lose their setup with no way back.
    ///
    /// Takes effect on the next start - RimWorld reads this file once at launch.</summary>
    internal static bool ApplyOrder(IReadOnlyList<ModEntry> wanted, out string reason)
    {
        reason = "";
        var path = GenFilePaths.ModsConfigFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            reason = "找不到 ModsConfig.xml";
            return false;
        }
        if (wanted.Count == 0)
        {
            reason = "服务器没有给出模组列表";
            return false;
        }
        try
        {
            var document = XDocument.Load(path);
            var root = document.Root;
            if (root is null)
            {
                reason = "ModsConfig.xml 结构异常";
                return false;
            }
            var active = root.Element("activeMods");
            if (active is null)
            {
                active = new XElement("activeMods");
                root.Add(active);
            }
            active.RemoveNodes();
            foreach (var entry in wanted.OrderBy(entry => entry.Order))
                active.Add(new XElement("li", entry.PackageId));
            // One copy of the old file: the player may be mid-session with a different set on purpose.
            try { File.Copy(path, path + ".turbotick.bak", overwrite: true); } catch (Exception) { }
            document.Save(path);
            reason = "已写入 ModsConfig.xml（下次启动生效，旧文件备份为 .turbotick.bak）";
            return true;
        }
        catch (Exception error)
        {
            reason = "写入失败：" + error.Message;
            return false;
        }
    }

    /// <summary>A mod's own <c>&lt;modVersion&gt;</c>, or "" when it never declared one.
    ///
    /// <para>Most mods leave the field empty, so "" is a normal answer, not an error - and it must not read as a
    /// mismatch against a room that also left it empty. Wrapped because a mod with a hand-edited
    /// <c>About.xml</c> can throw from <c>ModMetaData</c>, and a diagnostic window must not be the thing that
    /// crashes.</para>
    /// </summary>
    private static string SafeVersion(ModContentPack mod)
    {
        try { return (mod.ModMetaData?.ModVersion ?? "").Trim(); }
        catch (Exception) { return ""; }
    }
}
