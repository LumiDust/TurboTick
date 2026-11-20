using System.Net;
using System.Text;
using TurboTick.Networking;

/// <summary>
/// Verification for the workshop half of "download the mods this save wants".
///
/// Two things are pinned here, both measured rather than imagined:
/// <list type="bullet">
/// <item>the browse page's real markup - the fixtures under <c>Tests/Fixtures</c> are trimmed slices of live
/// steamcommunity responses, so a parser change that only works on hand-written HTML fails;</item>
/// <item>the resolve loop verifies a candidate's own <c>About/About.xml</c> instead of trusting the first hit,
/// because the top results for a mod's title are regularly a translation or a fork.</item>
/// </list>
///
/// No socket is opened: <see cref="WorkshopSearch.LookupAsync"/> takes a send delegate and
/// <see cref="WorkshopSearch.ResolveAsync"/> takes a lookup delegate, so the HTTP path is exercised with canned
/// pages and the download path with a temp directory.
/// </summary>
internal static class WorkshopVerification
{
    internal static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        await check("workshop browse markup is parsed into id/title pairs, skipping images and malformed anchors", () =>
        {
            var html = ReadFixture("workshop-trenches.html");
            var candidates = WorkshopSearch.Parse(html);

            // The fixture holds five cards, each with two anchors to the same item (image + title).
            Assert(candidates.Count == 5, "expected five items, got " + candidates.Count + ": "
                + string.Join(", ", candidates.Select(item => item.Id)));
            Assert(candidates.Select(item => item.Id).SequenceEqual(
                    [3828999937L, 3501291601L, 2471429755L, 3832845748L, 3500414657L]),
                "page order was not preserved: " + string.Join(", ", candidates.Select(item => item.Id)));
            Assert(candidates[0].Title == "Trenches", "the title was not read: " + candidates[0].Title);
            // `&amp;` in the markup must arrive decoded, or a search for the title can never match it.
            Assert(candidates[3].Title == "Earthworks - Trenches & Moats", "HTML entities were not decoded: " + candidates[3].Title);

            // Scores without a query are a page-order decay: strictly decreasing, no invented relevance.
            for (var index = 1; index < candidates.Count; index++)
                Assert(candidates[index].Score < candidates[index - 1].Score,
                    "page-order scores are not strictly decreasing at " + index);

            // Deliberately broken fragments: a link with no text, an anchor that is not a workshop item, a
            // 20-digit "id", an unclosed anchor, and the same item twice. None may throw, none may invent an item.
            var malformed = WorkshopSearch.Parse(
                "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=1\"></a>"
                + "<a href=\"https://steamcommunity.com/profiles/76561198178205031/myworkshopfiles/?appid=294100\">By Oomby</a>"
                + "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=12345678901234567890\">too long</a>"
                + "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=42\">Good</a>"
                + "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=42\">Good</a>"
                + "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=7\">never closed");
            Assert(malformed.Count == 1 && malformed[0].Id == 42 && malformed[0].Title == "Good",
                "the malformed page produced " + malformed.Count + " item(s): "
                + string.Join(", ", malformed.Select(item => item.Id + "=" + item.Title)));
            Assert(WorkshopSearch.Parse(null).Count == 0 && WorkshopSearch.Parse("").Count == 0,
                "an empty page must be empty, not a throw");
            Assert(WorkshopSearch.Parse("<html><body>no anchors</body></html>").Count == 0, "a page without items returned items");
            return Task.CompletedTask;
        });

        await check("a search ranks the mod itself above its translations and same-named neighbours", () =>
        {
            // The real ordering from the live page: the mod, a Chinese translation, a Russian one, a re-upload,
            // then two unrelated mods that merely contain the word "Variants".
            var candidates = WorkshopSearch.Parse(ReadFixture("workshop-more-door-variants.html"), "[DG]More Door Variants");
            Assert(candidates.Count == 5, "expected five items, got " + candidates.Count);
            Assert(candidates[0].Id == 3832293718,
                "the mod itself was not first, got " + candidates[0].Id + " (" + candidates[0].Title + ")");
            var order = candidates.Select(item => item.Id).ToArray();
            foreach (var beaten in new[] { 3834040680L, 3834363894L, 3834017386L, 3207125987L })
                Assert(Array.IndexOf(order, beaten) > 0, "a translation ranked above the mod itself: " + beaten);
            for (var index = 1; index < candidates.Count; index++)
                Assert(candidates[index].Score <= candidates[index - 1].Score, "scores are not descending at " + index);
            // A substring hit must not be worth a whole-word hit; that is what keeps "FemaleBodyVariants" down.
            Assert(candidates[^1].Score < 0.5, "a mere substring match scored " + candidates[^1].Score);

            // Trenches: the item is first, and the two continuation forks that only mention it lose on extra words.
            var trenches = WorkshopSearch.Parse(ReadFixture("workshop-trenches.html"), "Trenches");
            Assert(trenches[0].Id == 3828999937, "Trenches was not first: " + trenches[0].Id);
            Assert(trenches[0].Score > trenches[1].Score, "an exact title did not beat a token overlap");
            return Task.CompletedTask;
        });

        await check("titles are normalised for comparison and the query is the name, not the packageId slug", () =>
        {
            // Bracketed author tags are not part of the name: "[DG]More Door Variants" is "More Door Variants".
            Assert(WorkshopSearch.Normalize("[DG]More Door Variants") == "more door variants", "the [DG] prefix survived");
            Assert(WorkshopSearch.Normalize("【DG】【1.7】Trenches") == "trenches", "full-width prefixes survived");
            Assert(WorkshopSearch.Normalize("  Earthworks - Trenches & Moats ") == "earthworks trenches moats",
                "punctuation was not collapsed: " + WorkshopSearch.Normalize("  Earthworks - Trenches & Moats "));
            Assert(WorkshopSearch.Normalize("战壕 Trenches") == "战壕 trenches", "CJK text was dropped");
            Assert(WorkshopSearch.Normalize(null) == "" && WorkshopSearch.Normalize("   ") == "", "an empty name was not empty");

            Assert(WorkshopSearch.QueryFor("local.moredoorvariants", "[DG]More Door Variants") == "[DG]More Door Variants",
                "the recorded name was not preferred over the slug");
            Assert(WorkshopSearch.QueryFor("oomby.trenches", null) == "trenches", "the slug tail was not used");
            Assert(WorkshopSearch.QueryFor("acutus.atmosphere_overhaul", "") == "atmosphere overhaul",
                "underscores were not opened up");
            // A name that is just the id echoed back is not a name; the slug is still the better query.
            Assert(WorkshopSearch.QueryFor("oomby.trenches", "oomby.trenches") == "trenches", "the id was searched as a name");

            var tagged = WorkshopSearch.BrowseUrl("More Door Variants", versionTagged: true);
            Assert(tagged.StartsWith("https://steamcommunity.com/workshop/browse/?appid=294100", StringComparison.Ordinal),
                "the endpoint changed: " + tagged);
            Assert(tagged.Contains("searchtext=More%20Door%20Variants", StringComparison.Ordinal), "the query was not escaped: " + tagged);
            Assert(tagged.Contains("browsesort=textsearch", StringComparison.Ordinal), "textsearch sorting is what matched the live page");
            Assert(tagged.Contains("requiredtags%5B%5D=1.7", StringComparison.Ordinal), "the 1.7 tag pass is missing");
            Assert(!WorkshopSearch.BrowseUrl("Trenches", versionTagged: false).Contains("requiredtags", StringComparison.Ordinal),
                "the fallback pass must be untagged");
            return Task.CompletedTask;
        });

        await check("the steam proxy is resolved from the environment without inventing one", () =>
        {
            static Func<string, string?> Env(params (string Name, string Value)[] pairs) => name =>
                pairs.FirstOrDefault(pair => pair.Name == name).Value;

            var dedicated = WorkshopSearch.ResolveProxy(Env((WorkshopSearch.ProxyVariable, "http://192.168.0.30:7897")));
            Assert(dedicated is { Host: "192.168.0.30", Port: 7897 }, "the dedicated variable was ignored");

            // The dedicated one wins over the generic ones, so a tunnel for this feature alone is expressible.
            var precedence = WorkshopSearch.ResolveProxy(Env(
                ("HTTPS_PROXY", "http://wrong.example:1"),
                (WorkshopSearch.ProxyVariable, "127.0.0.1:7890")));
            Assert(precedence is { Host: "127.0.0.1", Port: 7890 }, "the dedicated variable did not win");

            Assert(WorkshopSearch.ResolveProxy(Env(("HTTPS_PROXY", "http://proxy.example:8080"))) is { Port: 8080 },
                "the generic HTTPS proxy was ignored");
            // "none" has to mean direct, otherwise a container that inherits a proxy cannot opt out.
            Assert(WorkshopSearch.ResolveProxy(Env((WorkshopSearch.ProxyVariable, "none"))) is null, "none did not disable the proxy");
            Assert(WorkshopSearch.ResolveProxy(Env(("HTTPS_PROXY", "direct"))) is null, "direct did not disable the proxy");
            Assert(WorkshopSearch.ResolveProxy(Env()) is null, "an empty environment invented a proxy");
            return Task.CompletedTask;
        });

        await check("the page-set proxy overrides the environment for our own HTTP client", () =>
        {
            static Func<string, string?> Env(params (string Name, string Value)[] pairs) => name =>
                pairs.FirstOrDefault(pair => pair.Name == name).Value;
            try
            {
                // What the admin page writes: one value that both steamcmd and the workshop search must follow.
                WorkshopSearch.SetProxy("http://192.168.0.30:7897");
                var overridden = WorkshopSearch.ResolveProxy(Env(("HTTPS_PROXY", "http://wrong.example:1")));
                Assert(overridden is { Host: "192.168.0.30", Port: 7897 },
                    "the page-set proxy did not override the environment");

                // Empty means "connect directly", which has to beat an inherited proxy rather than defer to it.
                WorkshopSearch.SetProxy("");
                Assert(WorkshopSearch.ResolveProxy(Env(("HTTPS_PROXY", "http://proxy.example:8080"))) is null,
                    "an empty setting did not clear an inherited proxy");

                // "none"/"off"/"direct" are the same answer spelled the way an operator would type it.
                WorkshopSearch.SetProxy("none");
                Assert(WorkshopSearch.ResolveProxy(Env(("HTTPS_PROXY", "http://proxy.example:8080"))) is null,
                    "none from the page did not disable the proxy");
            }
            finally
            {
                // Cleared so every later test goes back to reading the environment, which is the deployment default.
                WorkshopSearch.SetProxy(null);
            }
            Assert(WorkshopSearch.ResolveProxy(Env(("HTTPS_PROXY", "http://proxy.example:8080"))) is { Port: 8080 },
                "clearing the override did not restore environment resolution");
            return Task.CompletedTask;
        });

        await check("two search passes are merged by id, and a failed search reads as a message instead of a throw", async () =>
        {
            var requests = new List<string>();
            var taggedPage = ReadFixture("workshop-trenches.html");
            Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken _)
            {
                requests.Add(request.RequestUri!.Query);
                // The tagged pass finds the item; the untagged pass finds it plus one more, which must not duplicate it.
                var body = request.RequestUri.Query.Contains("requiredtags", StringComparison.Ordinal)
                    ? taggedPage
                    : taggedPage + "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=999\">Extra</a>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(body, Encoding.UTF8, "text/html") });
            }

            var found = await WorkshopSearch.LookupAsync("oomby.trenches", "Trenches", Send, CancellationToken.None);
            Assert(requests.Count == 2, "expected two passes, got " + requests.Count);
            Assert(requests[0].Contains("requiredtags", StringComparison.Ordinal), "the tagged pass did not run first");
            Assert(!requests[1].Contains("requiredtags", StringComparison.Ordinal), "the fallback pass was tagged");
            Assert(found.Found && found.Candidates.Count == 6, "the merge wrong: " + found.Candidates.Count);
            Assert(found.Candidates.Select(item => item.Id).Distinct().Count() == found.Candidates.Count, "a duplicate id survived");
            Assert(found.Problem is null, "a successful lookup reported a problem: " + found.Problem);
            Assert(found.Notes.Count == 2 && found.Notes.All(note => note.Contains("项", StringComparison.Ordinal)),
                "the notes do not say how many items each pass returned: " + string.Join(" | ", found.Notes));

            // A dead proxy must read as a sentence on the page, never as an unhandled exception on an HTTP worker.
            var offline = await WorkshopSearch.LookupAsync("oomby.trenches", "Trenches",
                (_, _) => throw new HttpRequestException("proxy unreachable"), CancellationToken.None);
            Assert(!offline.Found && offline.Problem is not null, "a failed lookup reported success");
            Assert(offline.Problem!.Contains("工坊", StringComparison.Ordinal), "the failure is not explainable: " + offline.Problem);

            var timeout = await WorkshopSearch.LookupAsync("oomby.trenches", "Trenches",
                (_, _) => throw new TaskCanceledException("timed out"), CancellationToken.None);
            Assert(timeout.Problem is not null && timeout.Problem.Contains("超时", StringComparison.Ordinal),
                "a timeout was not named as one: " + timeout.Problem);

            var nameless = await WorkshopSearch.LookupAsync("", null, (_, _) => throw new Exception("must not be called"), CancellationToken.None);
            Assert(!nameless.Found && nameless.Problem is not null, "a lookup with no name still opened a request");
            return;
        });

        await check("resolving downloads candidates until one's own About.xml declares the packageId", async () =>
        {
            var downloads = Path.Combine(root, "workshop-resolve");
            Directory.CreateDirectory(downloads);
            // First candidate is a translation: it declares a different packageId and must be rejected.
            WriteAbout(Path.Combine(downloads, "3834040680"), "dg.moredoorvariants.zh");
            WriteAbout(Path.Combine(downloads, "3832293718"), "local.moredoorvariants");

            var looked = new List<string>();
            Task<WorkshopLookup> Lookup(string? packageId, string? displayName, CancellationToken _)
            {
                looked.Add(packageId + "/" + displayName);
                return Task.FromResult(new WorkshopLookup("More Door Variants", [
                    new WorkshopCandidate(3834040680, "[DG] More Door Variants - 简体/繁体中文汉化", 0.9),
                    new WorkshopCandidate(3832293718, "[DG]More Door Variants", 0.85)
                ], [], null));
            }

            var downloaded = new List<long>();
            Task<WorkshopDownload> Download(long id, CancellationToken _)
            {
                downloaded.Add(id);
                return Task.FromResult(new WorkshopDownload(true, id, Path.Combine(downloads, id.ToString()), "installed"));
            }

            var resolved = await WorkshopSearch.ResolveAsync("local.moredoorvariants_steam", "[DG]More Door Variants",
                Download, CancellationToken.None, Lookup);
            Assert(looked is ["local.moredoorvariants_steam/[DG]More Door Variants"], "the lookup was not given the save's own entry");
            Assert(resolved.Ok, "the mod was not resolved: " + resolved.Problem);
            Assert(resolved.WorkshopId == 3832293718, "the wrong item was accepted: " + resolved.WorkshopId);
            Assert(resolved.PackageId == "local.moredoorvariants", "the declared packageId was not reported: " + resolved.PackageId);
            // The `_steam` postfix a save carries must not stop the match; this is the exact bug the user hit.
            Assert(downloaded.Count == 2, "the translation was not tried first: " + string.Join(",", downloaded));
            Assert(resolved.Attempts.Count == 2 && resolved.Attempts[0].PackageId == "dg.moredoorvariants.zh",
                "the rejected candidate was not reported with the id it actually declares");
            Assert(resolved.Attempts[0].Detail.Contains("不匹配", StringComparison.Ordinal), "the rejection reason is missing");

            // Nothing matches: the answer has to name every item tried and what each one declared, or the operator
            // cannot tell "wrong mod" from "wrong search".
            var missed = await WorkshopSearch.ResolveAsync("oomby.trenches", "Trenches", Download, CancellationToken.None,
                (_, _, _) => Task.FromResult(new WorkshopLookup("Trenches", [
                    new WorkshopCandidate(1, "Trenches", 1.0)
                ], [], null)));
            Assert(!missed.Ok && missed.Problem is not null, "a failed resolve claimed success");
            Assert(missed.Problem!.Contains("1（无 packageId）", StringComparison.Ordinal),
                "the failure does not list what was tried: " + missed.Problem);

            // A download that fails must be recorded, not thrown, and the loop must try the next candidate.
            var failedDownload = await WorkshopSearch.ResolveAsync("oomby.trenches", "Trenches",
                (id, _) => Task.FromResult(new WorkshopDownload(false, id, null, "Login Failure")),
                CancellationToken.None,
                (_, _, _) => Task.FromResult(new WorkshopLookup("Trenches", [
                    new WorkshopCandidate(1, "Trenches", 1.0),
                    new WorkshopCandidate(2, "Trenches", 0.9)
                ], [], null)));
            Assert(!failedDownload.Ok && failedDownload.Attempts.Count == 2, "a failed download aborted the loop");
            Assert(failedDownload.Attempts[0].Detail == "Login Failure", "the steamcmd failure text was lost");
            Assert(failedDownload.Attempts[0].PackageId is null, "a failed download reported a packageId");

            // No candidates at all is the lookup's problem, and its wording must survive.
            var none = await WorkshopSearch.ResolveAsync("oomby.trenches", "Trenches", Download, CancellationToken.None,
                (_, _, _) => Task.FromResult(WorkshopLookup.Empty("Trenches", "工坊请求超时（20 秒）")));
            Assert(!none.Ok && none.Problem == "工坊请求超时（20 秒）", "the lookup's own explanation was replaced: " + none.Problem);
            Assert(none.Attempts.Count == 0, "nothing was searched for, yet attempts were reported");

            try { Directory.Delete(downloads, true); } catch { /* best effort */ }
        });

        await check("a mod buried under same-named siblings is still found, because the budget is six not three", async () =>
        {
            // Measured on the deployment host: aoba.deadmanswitch.core is published as "The Dead Man's Switch II",
            // so it scores the same as the four sibling add-ons and one re-upload that sit above it. A budget of
            // four (let alone three) rejects a save that a budget of six loads. This group pins the measured row.
            var downloads = Path.Combine(root, "workshop-deep");
            Directory.CreateDirectory(downloads);
            WriteAbout(Path.Combine(downloads, "3819799478"), "aoba.deadmanswitch.core");

            var page = new WorkshopLookup("The Dead Man's Switch", [
                new WorkshopCandidate(3121742525, "The Dead Man's Switch", 1.0),
                new WorkshopCandidate(3469398006, "The Dead Man's Switch - AncientCorps", 0.75),
                new WorkshopCandidate(3377130226, "The Dead Man's Switch - MobileDragoon", 0.75),
                new WorkshopCandidate(3374615241, "The Dead Man's Switch - Synthetic", 0.75),
                new WorkshopCandidate(3819799478, "The Dead Man's Switch II", 0.75),
                new WorkshopCandidate(3689879335, "The Dead Man's Switch - Expedition", 0.75),
                new WorkshopCandidate(3666117000, "The Dead Man's Switch - Legion", 0.75)
            ], [], null);

            Assert(WorkshopSearch.MaximumAttempts >= 6, "the default budget went back below the measured worst case");

            var downloaded = new List<long>();
            Task<WorkshopDownload> Download(long id, CancellationToken _)
            {
                downloaded.Add(id);
                var directory = Path.Combine(downloads, id.ToString());
                var staged = Directory.Exists(directory) ? directory : Path.Combine(downloads, "unrelated-" + id);
                if (!Directory.Exists(staged)) WriteAbout(staged, "unknown." + id);
                return Task.FromResult(new WorkshopDownload(true, id, staged, "installed"));
            }

            var resolved = await WorkshopSearch.ResolveAsync("aoba.deadmanswitch.core", "The Dead Man's Switch",
                Download, CancellationToken.None, (_, _, _) => Task.FromResult(page));
            Assert(resolved.Ok, "the buried mod was not reached: " + resolved.Problem);
            Assert(resolved.WorkshopId == 3819799478, "the wrong item was accepted: " + resolved.WorkshopId);
            Assert(resolved.PackageId == "aoba.deadmanswitch.core", "the declared packageId was lost: " + resolved.PackageId);
            Assert(downloaded.Count == 5, "the loop did not walk far enough or walked too far: " + string.Join(",", downloaded));
            Assert(resolved.Attempts.Take(4).All(attempt => attempt.Detail.Contains("不匹配", StringComparison.Ordinal)),
                "a rejection was not explained");

            // And the same page with a budget of four must still refuse, so the pin above is about the budget and
            // not about a lucky order.
            var starved = await WorkshopSearch.ResolveAsync("aoba.deadmanswitch.core", "The Dead Man's Switch",
                Download, CancellationToken.None, (_, _, _) => Task.FromResult(page), maximumAttempts: 4);
            Assert(!starved.Ok && starved.Attempts.Count == 4, "a short budget unexpectedly found the mod");

            try { Directory.Delete(downloads, true); } catch { /* best effort */ }
        });

        await check("a save entry with no workshop id is marked resolvable by name, and only becomes present once fetched", () =>
        {
            // The exact shape the user's save produces: every third-party mod has modSteamId = 0, so the planner can
            // only fall back on the recorded display name.
            var manifest = new SaveModManifest("1.7.5005", [
                new SaveMod("brrainz.harmony_steam", 0, "Harmony"),
                new SaveMod("ludeon.rimworld", 0, "Core"),
                new SaveMod("local.moredoorvariants", 0, "[DG]More Door Variants"),
                new SaveMod("local.washingmachine", 0, "[DG]Simple Washing Machine"),
                new SaveMod("mystery.noname", 0, ""),
                new SaveMod("some.workshopmod", 2195452496, "Workshop")
            ], [], null);
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brrainz.harmony" };
            var plan = SaveModPlanner.Plan(manifest, installed, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var doors = plan.Required.Single(entry => entry.Id == "local.moredoorvariants");
            Assert(doors.Disposition == SaveModDisposition.Manual, "a steamid-less mod was not manual: " + doors.Disposition);
            Assert(doors.CanResolve && doors.HasSearchableName, "a named manual mod was not marked resolvable");
            Assert(plan.Resolvable.Select(entry => entry.Id).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "local.moredoorvariants", "local.washingmachine" }),
                "the resolvable set is wrong: " + string.Join(",", plan.Resolvable.Select(entry => entry.Id)));
            // No name to search for: nothing can be done automatically, so it must stay in the manual list.
            var nameless = plan.Required.Single(entry => entry.Id == "mystery.noname");
            Assert(!nameless.CanResolve, "an entry with no name claimed to be resolvable");
            Assert(plan.UnresolvableRequired.Any(entry => entry.Id == "mystery.noname"), "the nameless mod left the manual list");
            // A known workshop id is still the stronger signal and keeps its own disposition.
            Assert(plan.Downloadable.Count == 1 && plan.Downloadable[0].Id == "some.workshopmod",
                "the workshop id stopped being download-ready");
            Assert(!plan.Complete, "an incomplete plan claimed to be complete");

            // Fetching by name is the same operation as fetching by id, once ResolveAsync has confirmed the packageId.
            var fetched = SaveModPlanner.WithDownloaded(plan, ["local.moredoorvariants", "some.workshopmod"]);
            Assert(fetched.Required.Single(entry => entry.Id == "local.moredoorvariants").Disposition == SaveModDisposition.Present,
                "a fetched manual mod did not become present");
            Assert(!fetched.Missing.Contains("local.moredoorvariants") && !fetched.Missing.Contains("some.workshopmod"),
                "the fetched mods are still missing: " + string.Join(",", fetched.Missing));
            Assert(fetched.ActiveMods.Contains("local.moredoorvariants") && fetched.ActiveMods.Contains("some.workshopmod"),
                "the fetched mods were not added to activeMods: " + string.Join(",", fetched.ActiveMods));
            Assert(fetched.Missing is ["local.washingmachine", "mystery.noname"],
                "only the unfetched mods should remain: " + string.Join(",", fetched.Missing));
            Assert(fetched.ManualRequired.Count == 2 && fetched.Refusal is not null
                && fetched.Refusal.Contains("local.washingmachine", StringComparison.Ordinal),
                "the refusal no longer names what is still missing");
            // Core survives a re-plan exactly once, at the position the save recorded it - here index 1, because the
            // save's own first entry is `brrainz.harmony_steam` and Harmony loads before Core.
            Assert(fetched.ActiveMods is ["brrainz.harmony", "ludeon.rimworld", ..],
                "the save's order was not kept: " + string.Join(",", fetched.ActiveMods));
            Assert(fetched.ActiveMods.Count(id => id == "ludeon.rimworld") == 1, "Core was duplicated");

            // A save that names the `_steam` form resolves it to the single copy and records the installed form.
            var postfixed = SaveModPlanner.Plan(new SaveModManifest("1.7.5005", [
                new SaveMod("ludeon.rimworld", 0, "Core"),
                new SaveMod("local.moredoorvariants_steam", 0, "[DG]More Door Variants")
            ], [], null), installed, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Assert(postfixed.Resolvable.Count == 1, "the postfixed entry was not offered for resolution");
            var resolvedForm = SaveModPlanner.WithDownloaded(postfixed, ["local.moredoorvariants"]);
            Assert(resolvedForm.Complete, "the postfixed entry was not satisfied by the plain id: "
                + string.Join(",", resolvedForm.Missing));
            Assert(resolvedForm.ActiveMods is ["ludeon.rimworld", "local.moredoorvariants"],
                "activeMods did not switch to the installed form: " + string.Join(",", resolvedForm.ActiveMods));
            return Task.CompletedTask;
        });

        await check("a downloaded workshop directory is described by the packageId it declares, not by its folder name", () =>
        {
            // steamcmd writes <workshop>/content/294100/<id>/ and some items nest their real content one level down.
            var nested = Path.Combine(root, "workshop-describe", "294100", "3822832387");
            WriteAbout(Path.Combine(nested, "actual"), "mrk.researchcommission");
            var described = ModDirectory.Describe(nested);
            Assert(described is not null && described.PackageId == "mrk.researchcommission",
                "a nested workshop item was not described: " + (described?.PackageId ?? "null"));
            Assert(ModDirectory.Describe(Path.Combine(root, "workshop-describe", "missing")) is null,
                "a missing directory produced a mod");
            Assert(ModDirectory.Describe(null) is null, "a null directory produced a mod");
            return Task.CompletedTask;
        });

        await check("a save whose mods carry no workshop id is satisfied by searching for their names", async () =>
        {
            // End to end through SaveModsSync, which is where the search path lived before it was silently dropped
            // during a file move: the loop had been reduced to "download entries that already have a workshop id",
            // and since every third-party entry in a real 1.7 save has modSteamId = 0, it never ran. Nothing in the
            // unit tests noticed, because they exercised the planner and the resolver, not the wiring between them.
            var root2 = Path.Combine(root, "save-mods-resolve");
            if (Directory.Exists(root2)) Directory.Delete(root2, true);
            var saves = Path.Combine(root2, "Saves");
            var mods = Path.Combine(root2, "Mods");
            var game = Path.Combine(root2, "Game");
            var data = Path.Combine(root2, "State");
            Directory.CreateDirectory(saves);
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(data);
            // The one mod that is genuinely present, so the plan is not trivially about a single missing entry.
            WriteAbout(Path.Combine(mods, "harmony"), "brrainz.harmony");
            Directory.CreateDirectory(Path.Combine(saves));
            File.WriteAllText(Path.Combine(saves, "colony.rws"), """
                <?xml version="1.0" encoding="utf-8"?>
                <savegame><meta>
                  <gameVersion>1.7.5005 rev1082</gameVersion>
                  <modIds><li>ludeon.rimworld</li><li>brrainz.harmony_steam</li><li>oomby.trenches</li></modIds>
                  <modSteamIds><li>0</li><li>0</li><li>0</li></modSteamIds>
                  <modNames><li>Core</li><li>Harmony</li><li>Trenches</li></modNames>
                </meta><game><ticksGame>1</ticksGame></game></savegame>
                """);
            var options = new SaveModsOptions(saves, data, game, mods, Path.Combine(data, "ModsConfig.xml"), true);

            // The search answers with a decoy first - which is what the real page does for this mod's title - and the
            // download delegate writes each candidate's own About.xml, so only the second one can match.
            var searched = new List<string?>();
            var fetched = new List<long>();
            Task<WorkshopLookup> Search(string? packageId, string? displayName, CancellationToken token)
            {
                searched.Add(displayName);
                return Task.FromResult(new WorkshopLookup(displayName ?? "", [
                    new WorkshopCandidate(111111111, "Trenches (translation)", 1.0),
                    new WorkshopCandidate(3828999937, "Trenches", 0.9)
                ], [], null));
            }
            Task<WorkshopFetch> Fetch(long workshopId, CancellationToken token)
            {
                fetched.Add(workshopId);
                var directory = Path.Combine(root2, "downloads", workshopId.ToString());
                WriteAbout(directory, workshopId == 3828999937 ? "oomby.trenches" : "someone.else.translation");
                return Task.FromResult(new WorkshopFetch(true, directory, "ok"));
            }

            // First the dry run: nothing is downloaded, so the entry must be advertised as "a search can still get
            // this" rather than "copy it in by hand". This is the state the page shows before the operator presses
            // apply, and the state that was wrong when the search path had been dropped.
            var dry = await SaveModsSync.RunAsync("colony", options, "1.7.5005 rev1082", download: false, write: false,
                Fetch, null, null, CancellationToken.None, Search);
            Assert(searched.Count == 0, "a dry run still opened the workshop");
            Assert(!dry.Written, "a dry run wrote the config");
            Assert(dry.Resolvable.Count == 1 && dry.Resolvable[0].Id == "oomby.trenches",
                "the named manual entry was not reported as resolvable: " + string.Join(",", dry.Resolvable));
            Assert(dry.UnresolvableRequired.Count == 0, "a resolvable entry was reported as hand-only");
            Assert(dry.Refusal is not null && dry.Refusal.Contains("oomby.trenches", StringComparison.Ordinal),
                "the refusal stopped naming the unresolved mod: " + dry.Refusal);

            var report = await SaveModsSync.RunAsync("colony", options, "1.7.5005 rev1082", download: true, write: true,
                Fetch, null, null, CancellationToken.None, Search);

            Assert(searched is ["Trenches"], "the search did not run with the save's recorded name: "
                + string.Join(",", searched));
            Assert(fetched is [111111111, 3828999937],
                "the decoy was not tried and rejected before the real item: " + string.Join(",", fetched));
            Assert(report.AutoDownloaded is ["oomby.trenches"], "the resolved mod was not recorded as downloaded: "
                + string.Join(",", report.AutoDownloaded));
            Assert(report.Missing.Count == 0 && report.Refusal is null, "the plan still refuses: " + report.Refusal);
            Assert(report.ActiveMods.Contains("oomby.trenches"), "the resolved mod is not in activeMods: "
                + string.Join(",", report.ActiveMods));
            Assert(report.ActiveMods[0] == "ludeon.rimworld", "Core is not first: " + string.Join(",", report.ActiveMods));
            // Resolved entries are Present now, so neither bucket of the manual list may still claim them: a page
            // that kept saying "search for it" after it had been installed would invite a second download.
            Assert(report.Resolvable.Count == 0 && report.UnresolvableRequired.Count == 0,
                "a satisfied entry was still advertised as needing attention");
            // The copy, not a link: a link anywhere under the room's state directory makes the archive refuse it.
            var installedPath = Path.Combine(mods, "oomby.trenches");
            Assert(Directory.Exists(installedPath), "the resolved mod was not installed into the mods root");
            Assert((File.GetAttributes(installedPath) & FileAttributes.ReparsePoint) == 0,
                "the installed mod is a link, which the room archive will refuse");
            Assert(File.Exists(Path.Combine(installedPath, "About", "About.xml")), "the copy is incomplete");

            try { Directory.Delete(root2, true); } catch { /* best effort */ }
        });
    }

    private static void WriteAbout(string directory, string packageId)
    {
        Directory.CreateDirectory(Path.Combine(directory, "About"));
        File.WriteAllText(Path.Combine(directory, "About", "About.xml"),
            "<?xml version=\"1.0\"?><ModMetaData><packageId>" + packageId + "</packageId><name>" + packageId + "</name></ModMetaData>");
    }

    /// <summary>
    /// Reads a trimmed real response. Copied next to the test binary by the project file, with a walk-up fallback so
    /// the same file is found when the test runs straight out of a source-tree build directory.
    /// </summary>
    private static string ReadFixture(string name)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (File.Exists(beside)) return File.ReadAllText(beside);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Fixtures", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            var inTests = Path.Combine(directory.FullName, "Tests", "Fixtures", name);
            if (File.Exists(inTests)) return File.ReadAllText(inTests);
        }
        throw new FileNotFoundException("fixture not found: " + name + " (looked beside " + AppContext.BaseDirectory + ")");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
