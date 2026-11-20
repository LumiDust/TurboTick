using System.Net;
using TurboTick.Networking;

/// <summary>
/// Verification for the parts of the server portal that carry policy rather than plumbing: who may bind where,
/// what a session is worth, what steamcmd is allowed to be told, and which save names may reach the filesystem.
///
/// Everything here is deliberately free of Verse and of sockets except loopback-free file IO, so a regression in
/// "an unauthenticated panel is reachable from the network" fails a test instead of waiting for a stranger to find
/// it on port 30573.
/// </summary>
internal static class AdminPortalVerification
{
    internal static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        await check("admin bind refuses a network bind without a password and keeps loopback free", () =>
        {
            var loopback = AdminBinding.Resolve(null, 30573, null);
            Assert(loopback.Kind == AdminBindKind.Loopback && loopback.Listening && !loopback.RequiresLogin,
                "Default bind is not loopback.");
            Assert(loopback.Refusal is null, "Loopback bind was refused without a password.");

            var externalWithoutPassword = AdminBinding.Resolve("0.0.0.0", 30573, null);
            Assert(externalWithoutPassword.Kind == AdminBindKind.Disabled && !externalWithoutPassword.Listening,
                "A network bind without a password still produced a listener.");
            Assert(externalWithoutPassword.Refusal is not null
                && externalWithoutPassword.Refusal.Contains("TURBOTICK_ADMIN_PASSWORD", StringComparison.Ordinal),
                "The refusal does not tell the operator which variable to set.");

            var external = AdminBinding.Resolve("0.0.0.0", 30573, "secret");
            Assert(external.Kind == AdminBindKind.External && external.RequiresLogin, "Network bind with password was not external/login.");

            // Empty password is the same as no password: an env var set to "" must not widen the panel.
            Assert(AdminBinding.Resolve("0.0.0.0", 30573, "").Kind == AdminBindKind.Disabled, "Empty password widened the panel.");

            Assert(AdminBinding.Resolve("*", 30573, "s").Kind == AdminBindKind.External, "* was not treated as any address.");
            Assert(AdminBinding.Resolve("::", 30573, "s").Kind == AdminBindKind.External, "IPv6 any was not external.");
            Assert(AdminBinding.Resolve("[::1]", 30573, null).Kind == AdminBindKind.Loopback, "IPv6 loopback was not loopback.");
            Assert(AdminBinding.Resolve("localhost", 30573, null).Kind == AdminBindKind.Loopback, "localhost was not loopback.");
            Assert(AdminBinding.Resolve("0.0.0.0", 0, "s").Kind == AdminBindKind.Disabled, "Port 0 still produced a plan.");
            Assert(AdminBinding.Resolve("example.com", 30573, "s").Kind == AdminBindKind.Disabled, "A host name was accepted as a bind address.");
            Assert(AdminBinding.Resolve("0.0.0.0", 70000, "s").Kind == AdminBindKind.Disabled, "An out-of-range port was accepted.");
            Assert(AdminBinding.Url(AdminBinding.Resolve("0.0.0.0", 30573, "s"), 30573).Contains("30573", StringComparison.Ordinal),
                "The external URL lost its port.");
            return Task.CompletedTask;
        });

        await check("admin sessions expire, revoke and stay bounded", () =>
        {
            var clock = 1_000_000L;
            var sessions = new AdminSessions(lifetimeMinutes: 10, maximum: 3, clock: () => clock);
            var first = sessions.Issue(out var expires);
            Assert(first.Length == AdminSessions.TokenBytes * 2, "Session token is not 32 random bytes in hex.");
            Assert(expires == clock + 600_000, "Session expiry is not the configured lifetime.");
            Assert(sessions.Validate(first), "A fresh session was rejected.");
            Assert(sessions.Count == 1, "Session table count is wrong.");

            clock += 600_001;
            Assert(!sessions.Validate(first), "An expired session was accepted.");
            Assert(sessions.Count == 0, "An expired session was not pruned.");

            var second = sessions.Issue(out _);
            Assert(sessions.Revoke(second) && !sessions.Validate(second), "Revoked session still validated.");
            Assert(sessions.Revoke(null) == false, "Revoking null reported success.");

            // Bounded: a browser left open in many tabs must not grow the table without limit.
            var issued = new List<string>();
            for (var index = 0; index < 5; index++) issued.Add(sessions.Issue(out _));
            Assert(sessions.Count == 3, "the session table grew past its maximum: " + sessions.Count);
            Assert(!sessions.Validate(issued[0]) && sessions.Validate(issued[^1]), "Eviction did not drop the oldest session first.");

            Assert(!sessions.Validate("not-a-token"), "A garbage token validated.");
            Assert(!sessions.Validate(new string('a', 4096)), "An oversized token validated.");
            return Task.CompletedTask;
        });

        await check("steamcmd argv never carries a password and follows the version table", () =>
        {
            Assert(SteamCmd.IsValidAccount("dedicated_server.01@example.com", out _), "A normal account name was rejected.");
            Assert(!SteamCmd.IsValidAccount("bad name", out var reason) && reason.Length > 0, "An account name with a space was accepted.");
            Assert(!SteamCmd.IsValidAccount("", out _), "An empty account was accepted.");
            Assert(!SteamCmd.IsValidAccount("a;rm -rf /", out _), "An account name with shell punctuation was accepted.");

            var version = GameVersions.Find(null);
            Assert(version is not null && version.Key == GameVersions.DefaultKey, "The default version is missing.");
            Assert(GameVersions.Find("1.7")?.BetaBranch == "unstable", "The 1.7 branch is not the measured one.");
            Assert(GameVersions.Find("1.8") is null, "An unknown version resolved.");
            Assert(GameVersions.All.Count == 1, "The table grew more versions than the deployment knows.");

            var line = SteamCmd.GameUpdate(SteamCmd.DefaultExecutable, "/data/game", "someone", version!.SteamAppId, version.BetaBranch, validate: true);
            Assert(line.Arguments.Contains("-beta") && line.Arguments.Contains("unstable"), "The beta branch is not on the command line.");
            Assert(line.Arguments.Contains("294100") && line.Arguments.Contains("validate"), "The app id or validate flag is missing.");
            Assert(line.Arguments.Contains("+@sSteamCmdForcePlatformType") && line.Arguments.Contains("linux"),
                "The forced platform type is missing: a Linux host could fetch Windows depots.");
            Assert(line.Arguments[0] == "+force_install_dir" && line.Arguments[1] == "/data/game", "force_install_dir is not first.");
            Assert(line.Arguments[^1] == "+quit", "The command does not end with +quit.");
            Assert(!line.Display.Contains("password", StringComparison.OrdinalIgnoreCase), "The command line mentions a password.");
            Assert(line.Arguments.All(argument => !argument.Contains("hunter2", StringComparison.Ordinal)), "A password leaked into argv.");

            var anonymous = SteamCmd.GameUpdate(SteamCmd.DefaultExecutable, "/data/game", null, "294100", null, validate: false);
            Assert(anonymous.Arguments.Contains("anonymous"), "A missing account did not fall back to anonymous.");
            Assert(!anonymous.Arguments.Contains("-beta"), "A default-branch install still passed -beta.");
            Assert(!anonymous.Arguments.Contains("validate"), "validate was passed when it was disabled.");
            return Task.CompletedTask;
        });

        await check("steamcmd progress and failure lines are classified", () =>
        {
            Assert(SteamCmd.TryReadProgress(
                    " Update state (0x61) downloading, progress: 42.31 (3145728 / 7340032)", out var progress)
                && progress is not null, "A real steamcmd progress line was not recognised.");
            Assert(Math.Abs(progress!.Percent - 42.31) < 0.001, "The percentage was misread: " + progress.Percent);
            Assert(progress.BytesDone == 3145728 && progress.BytesTotal == 7340032, "The byte counters were misread.");
            Assert(progress.Phase == "downloading" && progress.Running, "The phase or running state was misread.");

            Assert(SteamCmd.TryReadProgress(" Update state (0x5) verifying install, progress: 99.99 (1 / 1)", out var verifying)
                && verifying!.Phase.Contains("verifying", StringComparison.Ordinal), "The verifying phase was not captured.");
            Assert(!SteamCmd.TryReadProgress("Success! App '294100' fully installed.", out _), "A success line was read as progress.");
            Assert(!SteamCmd.TryReadProgress("", out _), "An empty line was read as progress.");

            Assert(SteamCmd.TryReadFailure("ERROR! Failed to install app '294100' (No subscription)", out var noSubscription)
                && noSubscription.Length > 0, "An ERROR! line was not a failure.");
            Assert(SteamCmd.TryReadFailure("Login Failure: Invalid Password", out _), "A login failure was not a failure.");
            Assert(SteamCmd.TryReadFailure("Two-factor code required", out var twoFactor)
                && twoFactor.Contains("Steam Guard", StringComparison.Ordinal), "A two-factor prompt was not explained.");
            Assert(!SteamCmd.TryReadFailure("Update state (0x61) downloading, progress: 1.00 (1 / 100)", out _),
                "A progress line was read as a failure.");
            Assert(SteamCmd.ReportsSuccess("Success! App '294100' fully installed."), "The success line was not recognised.");
            return Task.CompletedTask;
        });

        await check("save names cannot leave the Saves directory and the listing is newest first", () =>
        {
            foreach (var bad in new[] { "", " ", "../escape", "a/b", @"a\b", "..", ".", ".hidden", "name.", "c:evil" })
                Assert(SaveLibrary.Normalize(bad) is null, "Unsafe save name accepted: " + bad);
            Assert(SaveLibrary.Normalize("My Colony 2") == "My Colony 2", "A normal save name was rejected.");
            Assert(SaveLibrary.Normalize("My Colony.rws") == "My Colony", "The .rws suffix was not stripped.");
            Assert(SaveLibrary.Normalize(new string('a', SaveLibrary.MaximumNameLength + 1)) is null, "An over-long name was accepted.");

            var folder = Path.Combine(root, "save-library");
            Directory.CreateDirectory(folder);
            var older = Path.Combine(folder, "Older.rws");
            var newer = Path.Combine(folder, "Newer.rws");
            File.WriteAllText(older, "x");
            File.WriteAllText(newer, "xx");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "ignored");
            var listed = SaveLibrary.List(folder);
            Assert(listed.Count == 2, "The listing did not contain exactly the two .rws files: " + listed.Count);
            Assert(listed[0].Name == "Newer" && listed[1].Name == "Older", "The listing is not newest first.");
            Assert(listed[0].Bytes == 2, "The size was not reported.");
            Assert(SaveLibrary.List(Path.Combine(root, "missing-saves")).Count == 0, "A missing directory was not an empty list.");
            Assert(SaveLibrary.List(null).Count == 0, "A null directory was not an empty list.");
            return Task.CompletedTask;
        });

        await check("mods config merge keeps the client's order, adds only installed mods and reports the rest", () =>
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.biotech", "brrainz.harmony", "lumidust.turbotick"
            };
            // A real client order: Harmony first (it declares <loadBefore>Core), then Core, then its own mods.
            // Royalty was not requested and is inserted right after Core; Biotech was requested, so it keeps the
            // position the client gave it - the client's order is what both sides must agree on.
            var merged = ModsConfigPlan.Merge(["brrainz.harmony", "ludeon.rimworld", "lumidust.turbotick", "someone.missing", "Ludeon.RimWorld.Biotech"], installed, includeDlc: true);
            Assert(merged.ActiveMods is ["brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty", "lumidust.turbotick", "ludeon.rimworld.biotech"],
                "the merge did not keep the client's order: " + string.Join(",", merged.ActiveMods));
            Assert(merged.ActiveMods.Contains("ludeon.rimworld.royalty") && merged.ActiveMods.Contains("ludeon.rimworld.biotech"),
                "Installed DLC was not added.");
            Assert(!merged.ActiveMods.Contains("ludeon.rimworld.ideology"), "DLC that is not installed was added.");
            Assert(merged.ActiveMods.Contains("lumidust.turbotick") && merged.ActiveMods.Contains("brrainz.harmony"), "Requested mods were dropped.");
            Assert(merged.Missing.SequenceEqual(["someone.missing"]), "The missing mod was not reported: " + string.Join(",", merged.Missing));
            Assert(merged.ActiveMods.Count(id => id == "lumidust.turbotick") == 1, "A duplicate was kept.");

            var without = ModsConfigPlan.Merge(["lumidust.turbotick"], installed, includeDlc: false);
            Assert(!without.ActiveMods.Any(id => id.StartsWith("ludeon.rimworld.", StringComparison.Ordinal)),
                "The DLC was still added with the switch off.");

            var xml = ModsConfigPlan.Xml("1.7.5005 rev1082", ["ludeon.rimworld", "a<b"], ["ludeon.rimworld.royalty"]);
            Assert(xml.Contains("<version>1.7.5005 rev1082</version>", StringComparison.Ordinal), "The version was not written.");
            Assert(xml.Contains("<li>a&lt;b</li>", StringComparison.Ordinal), "The XML was not escaped.");
            Assert(xml.Contains("<knownExpansions>", StringComparison.Ordinal), "knownExpansions was not written.");
            Assert(ModsConfigPlan.Xml("1.7", ["ludeon.rimworld"], []).Contains("<knownExpansions />", StringComparison.Ordinal),
                "An empty expansion list is not written as a self-closing element.");
            return Task.CompletedTask;
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
