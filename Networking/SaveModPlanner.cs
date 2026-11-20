namespace TurboTick.Networking;

/// <summary>One requirement from the save, resolved against what the server actually has.</summary>
public sealed record SaveModPlanEntry(string Id, string Name, long WorkshopId, SaveModSource Source, SaveModDisposition Disposition,
    string Detail)
{
    public bool Actionable => Disposition == SaveModDisposition.Downloadable;

    /// <summary>
    /// Manual, but the save recorded a name, so the workshop can be searched for that name and the result verified
    /// against <see cref="Id"/>. Not the same as <see cref="Actionable"/>: a workshop id is a fact, a name is a hint.
    /// </summary>
    public bool CanResolve => Disposition == SaveModDisposition.Manual && HasSearchableName;

    /// <summary>A name worth typing into a search box: present, and not just the id echoed back.</summary>
    public bool HasSearchableName =>
        !string.IsNullOrWhiteSpace(Name) && !Name.Trim().Equals(Id, StringComparison.OrdinalIgnoreCase);
}

public enum SaveModDisposition
{
    /// <summary>Core: always first, always present.</summary>
    Core,
    /// <summary>A DLC whose content is present; it goes into <c>activeMods</c>.</summary>
    Dlc,
    /// <summary>A DLC whose content is missing: the DLC preflight decides, this only reports it.</summary>
    DlcMissing,
    /// <summary>Installed on the server already.</summary>
    Present,
    /// <summary>Not installed, but the save carries a workshop id: steamcmd can fetch it.</summary>
    Downloadable,
    /// <summary>Not installed and not from the workshop: a human has to drop it into the mods directory.</summary>
    Manual,
    /// <summary>The server's own mod, present. Never downloaded, never replaced by a workshop copy.</summary>
    Self,
    /// <summary>The server's own mod is missing - the deployment is broken, and no download can fix it.</summary>
    SelfMissing
}

/// <summary>
/// What applying a save's mod list would do, computed without touching anything.
///
/// Separating the decision from the write is the point: an operator gets to see "these two mods must be put
/// here by hand" *before* a ModsConfig is written and a world is loaded, and the same object is what the
/// preflight prints. Computed from data only - no filesystem, no game - so it is unit-testable and the HTTP
/// thread can build it too.
/// </summary>
public sealed record SaveModPlan(
    IReadOnlyList<SaveModPlanEntry> Required,
    IReadOnlyList<string> ActiveMods,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Missing,
    IReadOnlyList<SaveModPlanEntry> ManualRequired,
    IReadOnlyList<SaveModPlanEntry> Downloadable,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nothing can be downloaded or added: the plan is complete and the save may start.</summary>
    public bool Complete => Missing.Count == 0;

    /// <summary>
    /// Manual entries the workshop search can still chase: a name was recorded, so a search + verify can find them.
    /// Kept separate from <see cref="Downloadable"/> because the outcome is uncertain - "search by name" is a
    /// best-effort step, and folding it into <see cref="Downloadable"/> would make the page promise a download it
    /// cannot guarantee.
    /// </summary>
    public IReadOnlyList<SaveModPlanEntry> Resolvable =>
        Required.Where(entry => entry.CanResolve).ToArray();

    /// <summary>Manual entries with nothing to search for: only a human can supply these.</summary>
    public IReadOnlyList<SaveModPlanEntry> UnresolvableRequired =>
        Required.Where(entry => entry.Disposition == SaveModDisposition.Manual && !entry.CanResolve).ToArray();

    /// <summary>Why the save must not be loaded, or null when it may be.</summary>
    public string? Refusal
    {
        get
        {
            if (Complete) return null;
            var parts = new List<string>();
            if (ManualRequired.Count > 0)
                parts.Add("必须手工放入 mods 目录：" + string.Join(", ",
                    ManualRequired.Select(entry => entry.Id + "（" + entry.Name + "）")));
            if (Downloadable.Count > 0)
                parts.Add("可从工坊下载但尚未下载（匿名下载，无需账号）：" + string.Join(", ",
                    Downloadable.Select(entry => entry.Id + " #" + entry.WorkshopId)));
            var dlc = Required.Where(entry => entry.Disposition == SaveModDisposition.DlcMissing)
                .Select(entry => entry.Id).ToArray();
            if (dlc.Length > 0) parts.Add("缺少 DLC 内容：" + string.Join(", ", dlc));
            // Reported separately from "missing mods": no download and no manual copy can fix it, so the sentence
            // has to point at the deployment instead of sending the operator to the workshop.
            var self = Required.Where(entry => entry.Disposition == SaveModDisposition.SelfMissing)
                .Select(entry => entry.Id).ToArray();
            if (self.Length > 0)
                parts.Add("服务端自身缺失：" + string.Join(", ", self)
                    + "（TurboTick 必须由服务端安装提供，不能在工坊里找一份替代）");
            return "存档需要 " + Missing.Count + " 个服务端没有的 mod，拒绝启动。"
                + string.Join("；", parts);
        }
    }
}

/// <summary>
/// Builds a save's <c>activeMods</c> from its own <c>&lt;meta&gt;</c> list.
///
/// Three rules, all of them "the save wins":
/// <list type="bullet">
/// <item>the order is the save's order - load order changes behaviour, and a re-sort silently applies patches
/// before what they patch;</item>
/// <item>Core is guaranteed present, at the position the save recorded. It is not hoisted to index 0: Harmony
/// ships <c>&lt;loadBefore&gt;&lt;li&gt;Ludeon.RimWorld&lt;/li&gt;&lt;/loadBefore&gt;</c>, so a save written by a
/// working client has Harmony at index 0 and Core at index 1, and rewriting that into Core-first makes
/// <c>ScribeMetaHeaderUtility.ModListsMatch</c> report a mod-list change on every load. Core only moves to the
/// front in the one case where the save never named it, because then there is no recorded position;</item>
/// <item><c>knownExpansions</c> is never derived from here - it belongs to the config being edited, because a
/// save written before a DLC was installed still loads fine on a server that has it.</item>
/// </list>
/// </summary>
public static class SaveModPlanner
{
    /// <summary>
    /// <paramref name="installed"/> are the lower-cased packageIds the server can actually load (its mods
    /// directory plus the DLC/content directories the game already exposes).
    /// <paramref name="dlcPresent"/> are the lower-cased DLC packageIds whose *content* is on disk.
    /// </summary>
    public static SaveModPlan Plan(SaveModManifest manifest, ISet<string> installed, ISet<string> dlcPresent,
        ISet<string> currentActive)
    {
        // A save names mods the way the machine that wrote it knew them: if that machine had both a local and a
        // workshop copy of one mod, the workshop copy was registered as "<id>_steam" (Verse.ModLister.TryAddMod).
        // A server holding a single copy registers it without the postfix, so a literal comparison says "missing"
        // for a mod that is sitting right there. Resolve against the canonical id, and then name the *installed*
        // form in activeMods - the config must contain a packageId the game will actually produce.
        string? Resolve(string id)
        {
            if (installed.Contains(id)) return id;
            var canonical = SaveManifest.CanonicalId(id);
            if (installed.Contains(canonical)) return canonical;
            // The set is usually OrdinalIgnoreCase, but a caller can hand us a case-sensitive one.
            foreach (var candidate in installed)
                if (SaveManifest.CanonicalId(candidate) == canonical) return candidate;
            return null;
        }

        var required = new List<SaveModPlanEntry>(manifest.Mods.Count);
        var active = new List<string>(manifest.Mods.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var notes = new List<string>();

        // The save's own list, in its own order. Core is placed by its own entry below, so a save that recorded
        // Harmony first keeps Harmony first.
        foreach (var mod in manifest.Mods)
        {
            var id = mod.Id.Trim();
            if (id.Length == 0 || !id.Equals(id.ToLowerInvariant(), StringComparison.Ordinal)) id = id.ToLowerInvariant();
            // Dedupe on the canonical id: a save can name both `foo` and `foo_steam` for the same mod, and adding
            // both to activeMods makes the game log "same packageId loaded twice" and drop one at random.
            if (!seen.Add(SaveManifest.CanonicalId(id))) continue;
            var source = SaveManifest.Classify(id);
            switch (source)
            {
                case SaveModSource.Core:
                    active.Add(SaveManifest.CoreId);
                    required.Add(new SaveModPlanEntry(id, mod.Name, 0, source, SaveModDisposition.Core, "核心"));
                    break;
                case SaveModSource.Harmony:
                    if (Resolve(id) is { } harmony)
                    {
                        active.Add(harmony);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Present,
                            harmony == id ? "已安装" : "已安装为 " + harmony));
                    }
                    else
                    {
                        // Harmony is not optional and not a third-party mod: a server without it cannot run
                        // this mod at all, so it is reported as missing rather than as "download something".
                        missing.Add(id);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Manual,
                            "Harmony 缺失（服务端必须自带）"));
                    }
                    break;
                case SaveModSource.Self:
                    // Every save a client wrote names this mod; the server is running it. Treating it as
                    // third-party is what fetched a 0.6.1 workshop copy beside the 0.7.1 binary and left the
                    // game to drop one of two directories declaring the same packageId - see
                    // SaveManifest.TurboTickId. Present means "the thing that is serving you", so it is never
                    // downloaded and never substituted.
                    if (Resolve(id) is { } self)
                    {
                        active.Add(self);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Self,
                            self == id ? "服务端自身" : "服务端自身（注册为 " + self + "）"));
                    }
                    else
                    {
                        // Not a download problem: the server cannot run without itself. Reported as missing so the
                        // start is refused with a sentence that points at the deployment, not at the workshop.
                        missing.Add(id);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source,
                            SaveModDisposition.SelfMissing,
                            "服务端自身缺失（TurboTick 未放进 mods 目录）：这一条工坊下载解决不了，重装服务端 mod"));
                    }
                    break;
                case SaveModSource.Dlc:
                    if (dlcPresent.Contains(id) || Resolve(id) is not null)
                    {
                        active.Add(id);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Dlc, "DLC 内容已就位"));
                    }
                    else
                    {
                        missing.Add(id);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.DlcMissing,
                            "DLC 内容缺失：由 DLC 预检处理"));
                    }
                    break;
                default:
                    if (Resolve(id) is { } present)
                    {
                        active.Add(present);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Present,
                            present == id ? "已安装" : "已安装为 " + present));
                    }
                    else if (mod.FromWorkshop)
                    {
                        // Reported as required-but-not-obtained: this is what the downloader acts on.
                        missing.Add(id);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Downloadable,
                            "可从工坊下载：workshop " + mod.SteamId));
                    }
                    else
                    {
                        missing.Add(id);
                        required.Add(new SaveModPlanEntry(id, mod.Name, mod.SteamId, source, SaveModDisposition.Manual,
                            "非工坊来源（modSteamId=0）：必须手工放入 mods 目录"));
                    }
                    break;
            }
        }

        // A save always names Core somewhere, but a hand-edited or truncated meta may not. Only then is the
        // position decided here - front, because there is no recorded position to respect.
        if (!seen.Contains(SaveManifest.CoreId) && !active.Contains(SaveManifest.CoreId))
            active.Insert(0, SaveManifest.CoreId);

        var manual = required.Where(entry => entry.Disposition == SaveModDisposition.Manual).ToArray();
        var downloadable = required.Where(entry => entry.Disposition == SaveModDisposition.Downloadable).ToArray();
        var changed = active.Where(id => !currentActive.Contains(id)).ToArray();
        if (manifest.Problem is { } problem) notes.Add("存档 meta：" + problem);
        if (manifest.GameVersion is { } version) notes.Add("存档游戏版本：" + version);
        AppendInventoryNotes(notes, manual, downloadable);
        return new SaveModPlan(required, active, changed, missing, manual, downloadable, notes);
    }

    /// <summary>
    /// The two notes that describe what is still not installed. Written by both the first plan and the re-plan so
    /// they cannot disagree: measured, the re-plan updated the entries but not the notes, and the page went on
    /// saying "must be copied in by hand" for a mod that had just been downloaded.
    /// </summary>
    private static void AppendInventoryNotes(List<string> notes, IReadOnlyList<SaveModPlanEntry> manual,
        IReadOnlyList<SaveModPlanEntry> downloadable)
    {
        if (manual.Count > 0)
            notes.Add(ManualNotePrefix + string.Join(", ",
                manual.Select(entry => entry.Id + "（" + entry.Name + "）")));
        if (downloadable.Count > 0)
            notes.Add("可匿名从工坊下载（无需 Steam 账号）：" + string.Join(", ",
                downloadable.Select(entry => entry.Id + " #" + entry.WorkshopId)));
    }

    private const string ManualNotePrefix = "必须人工放入 mods 目录：";

    /// <summary>
    /// The plan once the named entries have been fetched: they become <see cref="SaveModDisposition.Present"/>.
    /// Re-planned rather than mutated so that "what is installed" stays a single input.
    ///
    /// <para>Both <see cref="SaveModDisposition.Downloadable"/> and <see cref="SaveModDisposition.Manual"/> can be
    /// fetched: the second only when a name search resolved it to a workshop item that declared the same
    /// packageId (<see cref="WorkshopSearch.ResolveAsync"/>). Matching is on the canonical id, because the
    /// downloaded copy is registered without the <c>_steam</c> postfix the saving machine may have used.</para>
    /// </summary>
    public static SaveModPlan WithDownloaded(SaveModPlan plan, IEnumerable<string> downloaded)
    {
        var fetched = new HashSet<string>(downloaded.Select(SaveManifest.CanonicalId), StringComparer.OrdinalIgnoreCase);
        if (fetched.Count == 0) return plan;
        var required = plan.Required
            .Select(entry =>
            {
                if (!IsFetchable(entry.Disposition)) return entry;
                var canonical = SaveManifest.CanonicalId(entry.Id);
                if (!fetched.Contains(canonical)) return entry;
                return entry with
                {
                    Disposition = SaveModDisposition.Present,
                    // A downloaded copy is a single copy, so the game registers it without the `_steam` postfix the
                    // saving machine may have used. Say what was actually installed, not what the save called it.
                    Detail = canonical == entry.Id ? "已从工坊下载" : "已从工坊下载，注册为 " + canonical
                };
            })
            .ToArray();
        var active = new List<string>(plan.ActiveMods);
        foreach (var entry in required)
        {
            if (entry.Disposition != SaveModDisposition.Present) continue;
            if (!fetched.Contains(SaveManifest.CanonicalId(entry.Id))) continue;
            var installed = SaveManifest.CanonicalId(entry.Id);
            if (!active.Contains(installed, StringComparer.OrdinalIgnoreCase)) active.Add(installed);
        }
        var missing = required.Where(entry => entry.Disposition is SaveModDisposition.Manual or SaveModDisposition.DlcMissing
            or SaveModDisposition.Downloadable).Select(entry => entry.Id).ToArray();
        var manual = required.Where(entry => entry.Disposition == SaveModDisposition.Manual).ToArray();
        var stillDownloadable = required.Where(entry => entry.Disposition == SaveModDisposition.Downloadable).ToArray();
        // Rebuilt, never carried over: the first plan's notes described what was missing *before* the fetch, and a
        // report that keeps them reads as "it failed" right after it worked.
        var notes = new List<string>();
        if (plan.Notes.Count > 0) notes.AddRange(plan.Notes.Where(note => !note.StartsWith(ManualNotePrefix, StringComparison.Ordinal)
            && !note.StartsWith("可匿名从工坊下载", StringComparison.Ordinal)));
        AppendInventoryNotes(notes, manual, stillDownloadable);
        return plan with
        {
            Required = required,
            ActiveMods = active,
            Missing = missing,
            ManualRequired = manual,
            Downloadable = stillDownloadable,
            Notes = notes
        };
    }

    /// <summary>Entries a download can satisfy: a known workshop id, or a name that resolved to one.</summary>
    private static bool IsFetchable(SaveModDisposition disposition) =>
        disposition is SaveModDisposition.Downloadable or SaveModDisposition.Manual;
}
