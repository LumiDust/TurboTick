using TurboTick.Networking;

/// <summary>The mod list that travels with the handshake, and the verdicts the client renders from it.
///
/// <para>These are the cases that were wrong in the field and are easy to regress: a version difference that no
/// missing/extra/order section can express, an undeclared <c>&lt;modVersion&gt;</c> that must not read as a
/// mismatch, and the older four-field wire form that a peer on the previous build still sends.</para>
/// </summary>
internal static class ModManifestVerification
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("mod manifest round-trips the version field and accepts the older four-field form", async () =>
        {
            var entries = new List<ModEntry>
            {
                new("ludeon.rimworld", "Core", 0, "0", "1.7.5005"),
                new("brrainz.harmony", "Harmony", 1, "2009463077", "3.0.1"),
                new("local.moredoorvariants", "More Door Variants", 2, "0", "")
            };
            var payload = ModManifest.Serialize(entries);
            var parsed = ModManifest.Parse(payload);
            Assert(parsed.Count == 3, "Serialize/Parse lost an entry.");
            Assert(parsed[0].Version == "1.7.5005" && parsed[1].Version == "3.0.1" && parsed[2].Version.Length == 0,
                "Version field did not survive the round trip.");
            Assert(parsed[1].WorkshopUrl is not null && parsed[2].WorkshopUrl is null, "WorkshopUrl is wrong.");

            // A peer on the previous build sends four fields: no version, name last.
            var legacy = ModManifest.Parse("0|ludeon.rimworld|0|Core\n1|brrainz.harmony|2009463077|Harmony\n");
            Assert(legacy.Count == 2 && legacy[0].Name == "Core" && legacy[1].Name == "Harmony",
                "The four-field form was not parsed.");
            Assert(legacy[1].Version.Length == 0, "A four-field entry must report an empty version, not a shifted name.");
        });

        await check("a version difference is its own verdict, not a missing or extra mod", async () =>
        {
            var server = new List<ModEntry>
            {
                new("ludeon.rimworld", "Core", 0, "0", "1.7.5005"),
                new("aoba.framework", "Aoba Framework", 1, "111", "1.4.0")
            };
            var local = new List<ModEntry>
            {
                new("ludeon.rimworld", "Core", 0, "0", "1.7.5005"),
                new("aoba.framework", "Aoba Framework", 1, "111", "1.3.9")
            };
            var diff = ModManifest.Compare(server, local);
            Assert(diff.Missing.Count == 0 && diff.Extra.Count == 0, "A version difference must not be reported as missing/extra.");
            Assert(diff.VersionMismatch is { Count: 1 } && diff.VersionMismatch[0].PackageId == "aoba.framework",
                "The version difference was not isolated.");
            Assert(!diff.Clean, "A version difference must not count as clean.");

            var rows = ModManifest.Check(server, local);
            Assert(rows.Count == 2, "Check must return one row per mod.");
            Assert(rows[0].Verdict == ModVerdict.Ok, "The matching mod should be OK.");
            Assert(rows[1].Verdict == ModVerdict.VersionMismatch, "The differing mod must carry VersionMismatch.");
            Assert(rows[1].ServerVersion == "1.4.0" && rows[1].LocalVersion == "1.3.9",
                "Both versions have to reach the row so the dialog can name them.");
        });

        await check("an undeclared modVersion is not a mismatch, and every mod gets a row", async () =>
        {
            // Most mods never set <modVersion>; two machines that both leave it empty are identical, not different.
            var server = new List<ModEntry>
            {
                new("x.one", "One", 0, "1", ""),
                new("x.two", "Two", 1, "2", ""),
                new("x.three", "Three", 2, "3", "2.0")
            };
            var local = new List<ModEntry>
            {
                new("x.one", "One", 0, "1", ""),
                new("x.two", "Two", 1, "2", ""),
                new("x.three", "Three", 2, "3", "2.0"),
                new("x.ours", "Ours Only", 3, "4", "1.0")
            };
            var diff = ModManifest.Compare(server, local);
            Assert(diff.VersionMismatch is null || diff.VersionMismatch.Count == 0,
                "Two empty modVersions must not be reported as a mismatch.");
            Assert(diff.Extra.Count == 1 && diff.Extra[0].PackageId == "x.ours", "The local-only mod was not reported.");

            var rows = ModManifest.Check(server, local);
            Assert(rows.Count == 4, "Every mod on either side must get a row, including the extra one.");
            Assert(rows.Count(row => row.Verdict == ModVerdict.Ok) == 3, "Three mods should be OK.");
            Assert(rows[^1].Verdict == ModVerdict.Extra && rows[^1].ServerOrder < 0,
                "The extra mod must sort last and carry no room position.");
        });

        await check("order is only reported between mods both sides have", async () =>
        {
            var server = new List<ModEntry>
            {
                new("a", "A", 0, "0", ""),
                new("b", "B", 1, "0", ""),
                new("c", "C", 2, "0", "")
            };
            var local = new List<ModEntry>
            {
                new("b", "B", 0, "0", ""),
                new("a", "A", 1, "0", ""),
                new("c", "C", 2, "0", "")
            };
            var diff = ModManifest.Compare(server, local);
            // One move fixes a swap: pulling `a` back below `b` (or `b` up above `a`) leaves the rest untouched, so the
            // answer is one row, not two. A position-wise scan used to report both sides of it - and on a real 22-mod
            // room one misplaced Harmony came out as **7 rows of 顺序不同**, telling the player to fix seven things
            // when moving one fixes all of them.
            Assert(diff.OutOfOrder.Count == 1, "A swap needs one move, got " + diff.OutOfOrder.Count + ".");
            var rows = ModManifest.Check(server, local);
            Assert(rows.Count(row => row.Verdict == ModVerdict.OutOfOrder) == 1, "Row verdict count is wrong.");
            Assert(rows.Single(row => row.Verdict == ModVerdict.OutOfOrder).PackageId == "a",
                "The mod that has to move was not named.");
            Assert(rows.Single(row => row.PackageId == "c").Verdict == ModVerdict.Ok, "C did not move and should be OK.");
        });

        await check("only the mods that actually have to move are reported", async () =>
        {
            // The real shape from `192.168.0.29`: the room runs Harmony first, the client loaded it last. A
            // position-wise scan called that **7 rows of 顺序不同** for one mod out of place; the answer the player
            // can act on is "move Harmony up".
            var server = new List<ModEntry>
            {
                new("harmony", "Harmony", 0, "0", ""),
                new("core", "Core", 1, "0", ""),
                new("royalty", "Royalty", 2, "0", ""),
                new("ideology", "Ideology", 3, "0", ""),
                new("biotech", "Biotech", 4, "0", ""),
                new("anomaly", "Anomaly", 5, "0", ""),
                new("odyssey", "Odyssey", 6, "0", ""),
                new("turbotick", "TurboTick", 7, "0", "")
            };
            var local = new List<ModEntry>
            {
                new("core", "Core", 0, "0", ""),
                new("royalty", "Royalty", 1, "0", ""),
                new("ideology", "Ideology", 2, "0", ""),
                new("biotech", "Biotech", 3, "0", ""),
                new("anomaly", "Anomaly", 4, "0", ""),
                new("odyssey", "Odyssey", 5, "0", ""),
                new("harmony", "Harmony", 6, "0", ""),
                new("turbotick", "TurboTick", 7, "0", "")
            };
            var diff = ModManifest.Compare(server, local);
            Assert(diff.OutOfOrder.Count == 1, "One dragged mod means one move, got " + diff.OutOfOrder.Count + ".");
            Assert(diff.OutOfOrder[0].PackageId == "harmony", "The named mod should be harmony, got " + diff.OutOfOrder[0].PackageId + ".");
            Assert(ModManifest.Check(server, local).Count(row => row.Verdict == ModVerdict.OutOfOrder) == 1,
                "The dialog should show exactly one row to fix.");
        });

        await check("a content digest difference is named when list and version agree", async () =>
        {
            // The field case: the table said "22 mods, all identical" and the join was still refused, because the
            // mod list and every <modVersion> matched while one mod's dll was a different build.
            var server = new List<ModEntry>
            {
                new("assssssqwww.visualtweaks", "Visual Tweaks", 0, "3835938747", "1.0", "6fb184fd071d066d"),
                new("ludeon.rimworld", "Core", 1, "0", "1.7.5005", "1111")
            };
            var local = new List<ModEntry>
            {
                new("assssssqwww.visualtweaks", "Visual Tweaks", 0, "3835938747", "1.0", "fe47346a752157df"),
                new("ludeon.rimworld", "Core", 1, "0", "1.7.5005", "1111")
            };
            var diff = ModManifest.Compare(server, local);
            Assert(diff.ContentMismatch.Count == 1 && diff.ContentMismatch[0].PackageId == "assssssqwww.visualtweaks",
                "The differing mod was not named.");
            Assert(diff.VersionMismatch.Count == 0, "A content difference must not read as a version difference.");
            Assert(!diff.Clean, "A content mismatch has to clear Clean, or the join-screening logic ignores it.");
            var rows = ModManifest.Check(server, local);
            Assert(rows[0].Verdict == ModVerdict.ContentMismatch && rows[0].Action == "replace", "Row verdict is wrong.");
            Assert(rows[1].Verdict == ModVerdict.Ok, "The matching mod should stay OK.");

            // Both empty means neither peer sent one - every build older than the field itself. Not a difference.
            var old = ModManifest.Parse(ModManifest.Serialize(new List<ModEntry> { new("a", "A", 0, "0", "", "") }));
            Assert(old.Count == 1 && old[0].Digest.Length == 0, "An empty digest must round trip as empty.");
            Assert(ModManifest.Compare(old, old).Clean, "Two peers with no digest must not disagree about content.");
            var half = new List<ModEntry> { new("a", "A", 0, "0", "", "abcdef0123456789") };
            Assert(ModManifest.Compare(old, half).Clean,
                "A one-sided digest cannot be called a difference: an older peer sends none and its files may be perfect.");
        });

        await check("a content mismatch says which side holds the stale copy, and says nothing when it cannot tell", async () =>
        {
            // Measured three times in one day on this install: Steam re-downloaded `assssssqwww.visualtweaks` on the
            // client at 09:54, 12:20 and 13:48 while the server kept the copy it had been given. "files differ" names
            // the mod but not the direction, and "update or roll back" is the only question left after that.
            const long early = 1_763_200_000;   // server copy
            const long late = early + 3600;     // client copy, an hour newer
            var server = new List<ModEntry> { new("m", "M", 0, "1", "1.0", "aaaa", early) };
            var local = new List<ModEntry> { new("m", "M", 0, "1", "1.0", "bbbb", late) };
            var row = ModManifest.Check(server, local)[0];
            Assert(row.Verdict == ModVerdict.ContentMismatch, "The fixtures must differ in content first.");
            Assert(row.OlderSide == "room", "The older side was not identified: " + row.OlderSide);

            // Reversed, the same pair must name the other side. A test that only ever checks one direction cannot tell
            // a working comparison from a constant.
            Assert(ModManifest.Check(local, server)[0].OlderSide == "local", "The comparison is not symmetric.");

            // Within the tolerance the honest answer is "cannot tell" - the two machines copy the same workshop item a
            // few seconds apart, and calling that "this one is newer" would send the player updating for nothing.
            var near = new List<ModEntry> { new("m", "M", 0, "1", "1.0", "bbbb", early + 4) };
            Assert(ModManifest.Check(server, near)[0].OlderSide is null, "A four-second gap must not be called a version gap.");

            // An older peer sends no stamp at all. Zero must read as unknown, never as 1970.
            var unstamped = new List<ModEntry> { new("m", "M", 0, "1", "1.0", "bbbb") };
            Assert(ModManifest.Check(server, unstamped)[0].OlderSide is null, "A missing stamp must stay unknown.");

            // And the field must not leak into the verdict: two peers that extracted the same bytes at different
            // times are compatible, and refusing that join is the defect this tolerance exists to prevent.
            var sameContentOld = new List<ModEntry> { new("m", "M", 0, "1", "1.0", "aaaa", early + 86400) };
            Assert(ModManifest.Check(server, sameContentOld)[0].Verdict == ModVerdict.Ok,
                "An install-time difference must never by itself refuse a join.");
        });

        await check("the summary counts each verdict once and names the total", async () =>
        {
            var rows = new List<ModCheck>
            {
                new(ModVerdict.Ok, "a", "A", null, 0, 0, "", ""),
                new(ModVerdict.Missing, "b", "B", "https://example/1", 1, -1, "", ""),
                new(ModVerdict.Missing, "c", "C", "https://example/2", 2, -1, "", ""),
                new(ModVerdict.VersionMismatch, "d", "D", null, 3, 3, "2.0", "1.0")
            };
            var text = ModManifest.Summary(rows, (key, args) => key + "(" + string.Join(",", args) + ")");
            Assert(text.Contains("4") && text.Contains("TurboTick_ModCheck_CountMissing(2)")
                && text.Contains("TurboTick_ModCheck_CountVersion(1)"), "Summary did not count the verdicts: " + text);
            Assert(ModManifest.Summary([rows[0]], null).Length > 0, "A clean list still needs a summary line.");
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
