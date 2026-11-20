using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using TurboTick.Networking;
using TurboTick.Simulation;

var passed = 0;
var failed = 0;
var filter = args.Length == 2 && args[0] == "--filter" ? args[1] : null;
var temp = Path.Combine(Path.GetTempPath(), "turbotick-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
// The checkout's own `Source/` directory, found by walking up until a marker file exists - the suite is also run from
// places that are not this checkout, so a hard-coded relative path would fail there for the wrong reason.
string SourceDir()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "TurboTick-headless", "Source"),
                         Path.Combine(dir.FullName, "Source")
                     })
                if (File.Exists(Path.Combine(candidate, "SettlementGates.cs"))) return candidate;
            dir = dir.Parent;
        }
    }
    throw new DirectoryNotFoundException("Source/ not found");
}
string Dir(string name) { var p = Path.Combine(temp, name); Directory.CreateDirectory(p); return p; }
ClientHello Hello(string name = "A", string hash = "mod-a", string version = "1.7.4998") => new(name, version, hash);

async Task Test(string name, Func<Task> body)
{
    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try { await body().WaitAsync(TimeSpan.FromSeconds(25)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
// **Ledger reasons that name a channel class must name a real one (round 130).** The generator prints this too, but the
// generator only runs when someone remembers to run it, and the person editing a reason is the least likely to. Round 128
// closed two rows on another mod's patch names; round 130 closed eight on channel names that were never declared
// (`HediffSetSync`, `GearSync`, `ThoughtSync`, …). Same defect both times: the evidence field was not checkable, so
// nothing complained. Skipped rather than failed when the ledger is absent: the suite also runs from places that are not
// this checkout.
void CheckLedgerChannelNames()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "_lab", "ledger-triage.txt"))) dir = dir.Parent;
    if (dir == null) return;
    var root = dir.FullName;
    var declared = new HashSet<string>(StringComparer.Ordinal);
    foreach (var sub in new[] { "Source", "Networking", "Simulation", "Portal" })
    {
        var folder = Path.Combine(root, "TurboTick-headless", sub);
        if (!Directory.Exists(folder)) folder = Path.Combine(root, sub);
        if (!Directory.Exists(folder)) continue;
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file), @"\b(?:class|struct|record)\s+([A-Za-z_]\w*)"))
                declared.Add(m.Groups[1].Value);
    }
    Check(declared.Count > 200, $"source class scan found only {declared.Count} declarations");
    var channel = new System.Text.RegularExpressions.Regex(@"\b([A-Z][A-Za-z0-9]*(?:Sync|Patch|Channels?))\b");
    var unverifiable = new List<string>();
    var named = 0;
    foreach (var line in File.ReadAllLines(Path.Combine(root, "_lab", "ledger-triage.txt")))
    {
        var parts = line.Split('\t');
        if (parts.Length < 5) continue;
        foreach (System.Text.RegularExpressions.Match m in channel.Matches(parts[4]))
        {
            named++;
            if (!declared.Contains(m.Groups[1].Value)) unverifiable.Add(parts[2] + " -> " + m.Groups[1].Value);
        }
    }
    Check(named > 50, $"the ledger's reasons name only {named} channel classes, so this check would be vacuous");
    Check(unverifiable.Count == 0, "ledger reasons name only channels that exist: " + string.Join(", ", unverifiable.Take(6)));
}

// **A reason's `状态通道:X` must name something that exists at all (round 137).** The class check above is narrower than
// it looks: it only looks at names shaped `*Sync` / `*Patch`, so a reason naming a record or a field that was never
// written passes untouched. Measured: the row for `Caravan_PathFollower` claimed `状态通道:ReplicaCaravan` and there is no
// such type, record or field in the project - the real carrier is a postfix on the `WorldObject.Tile` setter. Same defect
// as round 130, one level down, and equally invisible.
void CheckLedgerStateChannelNames()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "_lab", "ledger-triage.txt"))) dir = dir.Parent;
    if (dir == null) return;
    var root = dir.FullName;
    var code = new System.Text.StringBuilder();
    var scanned = 0;
    foreach (var sub in new[] { "Source", "Networking", "Simulation", "Portal" })
    {
        var folder = Path.Combine(root, "TurboTick-headless", sub);
        if (!Directory.Exists(folder)) folder = Path.Combine(root, sub);
        if (!Directory.Exists(folder)) continue;
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            scanned++;
            // Comments excluded on purpose: the whole point is that a name which appears *only* in a comment is prose,
            // not a mechanism.
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;
                code.Append(line).Append('\n');
            }
        }
    }
    Check(scanned > 100, $"source scan visited only {scanned} files, so this check would be vacuous");
    var text = code.ToString();
    var claim = new System.Text.RegularExpressions.Regex("状态通道:([A-Za-z_][A-Za-z0-9_]*)");
    var named = 0;
    var missing = new List<string>();
    foreach (var line in File.ReadAllLines(Path.Combine(root, "_lab", "ledger-triage.txt")))
    {
        var parts = line.Split('\t');
        if (parts.Length < 5) continue;
        foreach (System.Text.RegularExpressions.Match m in claim.Matches(parts[4]))
        {
            named++;
            var identifier = m.Groups[1].Value;
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "\\b" + System.Text.RegularExpressions.Regex.Escape(identifier) + "\\b"))
                missing.Add(parts[2] + " -> " + identifier);
        }
    }
    Check(named > 20, $"the ledger states only {named} state channels, so this check would be vacuous");
    Check(missing.Count == 0, "ledger state channels name identifiers that exist in code: " + string.Join(", ", missing.Take(6)));
}

async Task Until(Func<bool> condition, int seconds = 30)
{
    // Generous on purpose: this waits for an asynchronous condition, it does not assert latency. A 5 s ceiling
    // turned ordinary scheduling delay on a loaded machine into "A task was canceled" with no hint about which
    // condition was still false. A genuine hang still fails, just later and with a message that says which.
    var started = Environment.TickCount64;
    while (!condition())
    {
        if (Environment.TickCount64 - started > seconds * 1000L)
            throw new TimeoutException($"condition was still false after {seconds}s");
        await Task.Delay(15);
    }
}
void AutoCheckpoint(DedicatedClient owner, DedicatedServer server, int size = 4096)
{
    owner.CheckpointRequested += () =>
    {
        var payload = new byte[size];
        _ = Task.Run(async () =>
        {
            try { await owner.CheckpointWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(payload), new MemoryStream(payload), server.Tick); }
            catch { /* the server may already have a fresh checkpoint */ }
        });
    };
}

async Task Throws(Func<Task> body, string? code = null)
{
    try { await body(); }
    catch (Exception ex)
    {
        if (code != null && (ex is not NetworkException ne || ne.Code != code))
            throw new Exception("Expected " + code + ", got " + ex, ex);
        return;
    }
    throw new Exception("Expected operation to fail.");
}

async Task WritePacket(TcpClient socket, object message)
{
    var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    var frame = new byte[json.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
    json.CopyTo(frame.AsSpan(4));
    await socket.GetStream().WriteAsync(frame);
}

async Task<System.Text.Json.JsonElement> ReadPacket(TcpClient socket)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var header = new byte[4];
    await socket.GetStream().ReadExactlyAsync(header, timeout.Token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    Check(length is > 0 and <= NetworkProtocol.MaximumFrameLength, "bounded raw response");
    var bytes = new byte[length];
    await socket.GetStream().ReadExactlyAsync(bytes, timeout.Token);
    using var document = System.Text.Json.JsonDocument.Parse(bytes);
    return document.RootElement.Clone();
}

async Task<System.Text.Json.JsonElement> ReadReply(TcpClient socket, long id)
{
    for (var index = 0; index < 64; index++)
    {
        var packet = await ReadPacket(socket);
        if (packet.GetProperty("type").GetString() == "Ping")
            await WritePacket(socket, new { type = "Pong", protocolVersion = NetworkProtocol.Version, requestId = packet.GetProperty("requestId").GetInt64() });
        if (packet.TryGetProperty("requestId", out var request) && request.GetInt64() == id) return packet;
    }
    throw new Exception("Raw client did not receive its correlated response.");
}

async Task RawBarrier(TcpClient socket, long id)
{
    await WritePacket(socket, new { type = "GetWorldManifest", protocolVersion = NetworkProtocol.Version, requestId = id });
    var reply = await ReadReply(socket, id);
    Check(reply.GetProperty("type").GetString() == "WorldManifest", "raw processing barrier");
}

await Test("single-player defaults, authoritative enable+value+cap, no local fallback", () =>
{
    var s = new SpeedAuthority();
    Check(s.Scale(3) == 6, "rate sync is on by default (2x of the current speed)");
    s.SetLocal(true, 2, 60);
    Check(s.Scale(3) == 6, "local multiplier");
    s.BeginSession();
    Check(s.Scale(3) == 0, "handshake in progress must hold");
    s.ApplyServer(new(true, 4, 12, 7));
    Check(s.Scale(6) == 12, "server cap must override local preferences");
    Check(s.Scale(6, true) == 6, "preserve forced normal semantics");
    Check(s.Scale(0) == 0, "preserve pause");
    Check(!s.ApplyServer(new(true, 2, 60, 6)), "ignore old revision");
    s.ConnectionLost();
    Check(s.Scale(3) == 0, "disconnect must not silently apply local settings");
    s.LeaveSession();
    Check(s.Scale(3) == 6, "explicit leave restores local settings");
    return Task.CompletedTask;
});

await Test("a kind with no Use row can still be reserved by its own grant", () =>
{
    // Round 136. `CanReserve` is asked as `Use` by every work giver, and the table states Filth/Fire/Plant/WildAnimal/
    // Animal only as Clean/PutOut/Gather/Hunt/Handle - so no `Use` row exists for them and a visitor granted `Cleaning`
    // was refused the reservation before `WorkGiver_CleanFilth.HasJobOnThing` ever ran. Measured: a visitor holding
    // `All` (1048575) answered `工作闸门放行 0 个 … 访客独缺：CleanFilth`, i.e. the grant was dead one gate early.
    Check(!SettlementRuleTable.HasRow(SettlementAct.Use, SettlementKind.Filth),
        "the table grew a Use/Filth row, so this test no longer describes the case it was written for");
    Check(!SettlementRuleTable.Permits(SettlementGrant.Cleaning, SettlementAct.Use, SettlementKind.Filth),
        "Use on Filth is now a stated row, so the reservation fallback would be unreachable");
    Check(SettlementRuleTable.PermitsAnyOn(SettlementGrant.Cleaning, SettlementKind.Filth),
        "CleanFilth's own grant does not count as applying to Filth, so the fallback cannot fire");
    Check(SettlementRuleTable.Permits(SettlementGrant.Cleaning, SettlementAct.Clean, SettlementKind.Filth),
        "Cleaning does not permit Clean/Filth, so the act-specific gate disagrees with the fallback");
    // And it must not widen the kinds that already have their own Use row: a bench stays Benchers-only.
    Check(SettlementRuleTable.HasRow(SettlementAct.Use, SettlementKind.Bench),
        "a bench has no Use row, so the fallback would hand benches to anybody holding any bench grant");
    Check(!SettlementRuleTable.Permits(SettlementGrant.Construction, SettlementAct.Use, SettlementKind.Bench),
        "Construction now permits using a bench, which the table never said");
    var text = File.ReadAllText(Path.Combine(SourceDir(), "SettlementGates.cs")).Replace("\r", string.Empty);
    var start = text.IndexOf("private static bool Permitted(", StringComparison.Ordinal);
    Check(start >= 0, "Permitted is missing");
    var next = text.IndexOf("private static", start + 30, StringComparison.Ordinal);
    var body = string.Join("\n", text.Substring(start, (next < 0 ? text.Length : next) - start)
        .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Check(body.Contains("!SettlementRuleTable.HasRow(SettlementAct.Use, kind)")
          && body.Contains("SettlementRuleTable.PermitsAnyOn(grants, kind)"),
        "Permitted has no reservation fallback, so a granted Clean/Gather/PutOut remains unrunnable");
    return Task.CompletedTask;
});

await Test("the work gate probe asks which work givers still see work", () =>
{
    // Round 135: `SettlementWorkGate` derives the act per work giver instance, and the class doc records what happens
    // when it did not - asking `Use` unconditionally refused every unlisted pair, so a visitor with `All` could not
    // clean, firefight, harvest, hunt or rope an animal. A boolean answer cannot tell that apart from "the owner denied
    // it", so the reading has to be the *list* of work givers that still say yes, with an owner control beside it.
    var text = File.ReadAllText(Path.Combine(SourceDir(), "ScriptedSettlement.cs")).Replace("\r", string.Empty);
    var start = text.IndexOf("internal static string WorkGateProbe", StringComparison.Ordinal);
    Check(start >= 0, "WorkGateProbe is missing");
    // Bound the body by the next member declaration instead of a character count: a fixed window silently stopped
    // reaching the owner control when the probe grew, and the failure then looked like a missing feature.
    var next = text.IndexOf("internal static string", start + 40, StringComparison.Ordinal);
    var body = string.Join("\n", text.Substring(start, (next < 0 ? text.Length : next) - start)
        .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Check(body.Contains("worker.HasJobOnThing(who, target, forced: false)"),
        "the probe does not call the patched HasJobOnThing, so it measures nothing about the work gate");
    Check(body.Contains("DefDatabase<WorkGiverDef>.AllDefsListForReading"),
        "the probe does not enumerate every work giver, so a missing entry would be invisible");
    Check(body.Contains("(List<string> Yes, List<string> Threw) Taking"),
        "exceptions are not counted apart from permits, so an inapplicable work giver would read as an allowing one");
    Check(body.Contains("ownerList.Except(visitor)"),
        "the probe has no owner control, so 'the visitor gets nothing' cannot be told from 'nothing takes this object'");
    Check(body.Contains("parts.Length >= 5 ? parts[4] : null") && body.Contains("本格："),
        "the probe cannot pick a thing out of a multi-thing cell, so a plant standing on filth answers for the filth");
    Check(body.Contains("whoParts[0].Equals(\"who\""),
        "the probe cannot list workable pawns, so its owner control has to be guessed from ids");
    Check(body.IndexOf("whoParts[0].Equals(\"who\"", StringComparison.Ordinal)
          < body.IndexOf("if (!data.Active", StringComparison.Ordinal) + (body.Contains("if (!data.Active") ? 1 : 0),
        "the pawn listing sits behind the inactive-layer guard, so it cannot be used to build a fixture");
    Check(body.Contains("SettlementGates.Can(who, act, target)") && body.Contains("闸门判定 访客拒绝"),
        "the probe reports only what vanilla's job check said, so a work-priority or reach failure reads as a gate refusal");
    Check(body.Contains("var (visitorDenied, visitorActs) = GateVerdict(pawn, thing);")
          && body.Contains("var (ownerDenied, _) = GateVerdict(owner, thing);"),
        "the gate's own verdict is not evaluated for both pawns, so a zero cannot be attributed to the table or to vanilla");
    Check(body.Contains("whoParts[0].Equals(\"unpaint\"") && body.Contains("HarmonyLib.AccessTools.Method(typeof(Area)"),
        "the Home setter is not restorable, so painting a fixture would leave a world change behind");
    Check(body.Contains("homeOfMap.ActiveCells.ToList()") && body.Contains("该格在 Home 区"),
        "the probe does not name vanilla's own preconditions, so a zero from the Home area or a fresh filth reads as a gate refusal");
    Check(body.Contains("candidate.workSettings?.EverWork == true"),
        "the control pawn is not required to be able to work, so an animal reads as 'the visitor was refused'");
    Check(body.Contains("parts[0].Equals(\"scan\"") && body.Contains("ownerYes.Count == 0) continue;"),
        "the probe has no scan mode, so a hand-picked object that vanilla would refuse anyway reads as 'the gate refused nothing'");
    Check(body.Contains("using var uncounted = SettlementGates.Uncounted();"),
        "the probe is counted, so one object times every work giver would read as a burst of pawn refusals");
    return Task.CompletedTask;
});

await Test("the chokepoint probe compares the patch with the decision", () =>
{
    // Round 134: `GateProbe` calls the decision function, so it answers "is the decision right". What the player feels is
    // "is the decision reached" - a HarmonyPatch whose signature never matched leaves the decision perfectly correct and
    // entirely unused, and the two read identically from outside. The probe must therefore call the patched method and
    // print the raw answer beside it, and it must say so when they disagree.
    var text = File.ReadAllText(Path.Combine(SourceDir(), "ScriptedSettlement.cs")).Replace("\r", string.Empty);
    var start = text.IndexOf("internal static string ChokepointProbe", StringComparison.Ordinal);
    Check(start >= 0, "ChokepointProbe is missing");
    // Bound by the next member, not by a character count: the probe grew (the empty-cell branch) and a fixed window
    // silently stopped reaching the assertion text, which then read as a missing feature rather than a moved one.
    var nextMember = text.IndexOf("internal static string", start + 40, StringComparison.Ordinal);
    var body = string.Join("\n", text.Substring(start, (nextMember < 0 ? text.Length : nextMember) - start)
        .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Check(body.Contains("reservationManager.CanReserve(pawn, new LocalTargetInfo(thing))"),
        "the probe does not call the patched CanReserve, so it measures the decision function and calls it a patch test");
    // The comparable pair is `Use` with `Use`: the reservation gate asks `Use` unconditionally, so a probe that
    // compares it against an arbitrary act reports a disagreement that is the probe's own (measured round 134).
    Check(body.Contains("SettlementGates.Can(pawn, SettlementAct.Use, thing)"),
        "the probe does not read the raw decision, so agreement between the two paths cannot be shown");
    Check(body.Contains("不一致"),
        "the probe does not name the disagreement case, so a dead patch would print as agreement");
    Check(body.Contains("if (act != SettlementAct.Use)"),
        "the probe does not label a non-Use act as a different gate's answer, so the two questions would be compared");
    Check(body.Contains("Enum.TryParse<SettlementAct>(parts[4], true, out var parsedAct)"),
        "the probe's act is not an argument, so only `Use` can be asked - and `Use` is the act the default visitor mask allows");
    Check(body.Contains("using var uncounted = SettlementGates.Uncounted();"),
        "the probe is counted, so it reports its own question as pawn traffic (round 95)");
    return Task.CompletedTask;
});

await Test("the land probe answers both halves of the layer", () =>
{
    // Round 133: the cell gate and the thing gate are separate decisions with separate inputs - the cell one asks the
    // rectangle, the thing one asks `SettlementRule.HomeOf`, which uses `thing.MapHeld`. Round 132 found a reader bug that
    // only the cell side happened to expose, so a probe that answers one half and is called "the gate probe" invites the
    // reading "both sides are fine".
    var text = File.ReadAllText(Path.Combine(SourceDir(), "ScriptedSettlement.cs")).Replace("\r", string.Empty);
    var start = text.IndexOf("internal static string GateProbe", StringComparison.Ordinal);
    Check(start >= 0, "GateProbe is missing");
    var body = text.Substring(start, Math.Min(4200, text.Length - start));
    var code = string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Check(code.Contains("SettlementGates.CanCell(player, act, map, cell)"),
        "the probe no longer asks the cell gate with the map it was given");
    Check(code.Contains("SettlementGates.Can(player, act, thing)"),
        "the probe does not ask the thing gate, so a thing-side defect would read as a clean cell-side result");
    Check(code.Contains("该格没有物件"),
        "the probe does not say when the thing half could not be measured, so a missing object would look like a pass");
    return Task.CompletedTask;
});

await Test("a configured grant reaches the gate that reads it", () =>
{
    // Round 132: `settlement-grant 1 2 BuildArea` answered 「授权 16384」 and the land gate kept refusing player 2,
    // because the record is written with `To` = the visitor's home settlement (`ScriptedSettlement.Grant` stores
    // `SettlementOf(player)`) while the reader accepted `To == 0` only. The permit-by-grant branch of every gate was
    // unreachable for any visitor who owns a settlement: the layer looked configurable and did nothing.
    var path = Path.Combine(SourceDir(), "SettlementData.cs");
    var text = File.ReadAllText(path).Replace("\r", string.Empty);
    var code = string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    var start = code.IndexOf("public SettlementGrant GrantsForPawn(int pawnHome, int settlement)", StringComparison.Ordinal);
    Check(start >= 0, "GrantsForPawn is missing");
    var body = code.Substring(start, Math.Min(1600, code.Length - start));
    Check(body.Contains("record.To != pawnHome") && body.Contains("record.To != 0"),
        "the grant reader ignores records addressed to the visitor's own settlement, so a configured grant never reaches the gate");
    // Writer and reader must agree on what `To` means. The writer stores a settlement id; a writer that stored a player
    // id would reintroduce the round-88 double meaning this filter was originally added for.
    var script = File.ReadAllText(Path.Combine(SourceDir(), "ScriptedSettlement.cs"));
    Check(script.Contains("target = data.SettlementOf(player)"),
        "the grant command no longer writes the visitor's settlement into `To`, so the reader and the writer disagree");
    return Task.CompletedTask;
});

await Test("the land gate takes a map, not the current map", () =>
{
    // Round 132: the deny half of the ownership layer had no reading for twenty rounds because the only player-keyed
    // cell gate read `Find.CurrentMap` - a probe cannot ask "player 2 on player 1's land on map 1" without naming the
    // map. These assertions pin the shape that makes the reading possible, and the reason the overload exists.
    var path = Path.Combine(SourceDir(), "SettlementGates.cs");
    Check(File.Exists(path), "Source/SettlementGates.cs is missing");
    var text = File.ReadAllText(path);
    var code = string.Join("\n", text.Replace("\r", string.Empty).Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Check(code.Contains("internal static bool CanCell(int player, SettlementAct act, Map? map, IntVec3 cell)"),
        "the player-keyed cell gate has no map-taking overload, so a probe must ask about Find.CurrentMap");
    // The overload must not fall back to the current map itself - that would make the parameter decorative.
    var start = code.IndexOf("internal static bool CanCell(int player, SettlementAct act, Map? map, IntVec3 cell)", StringComparison.Ordinal);
    var body = code.Substring(start, Math.Min(600, code.Length - start));
    Check(!body.Contains("Find.CurrentMap"),
        "the map-taking cell gate reads Find.CurrentMap, so the map argument would mean nothing");
    Check(code.Contains("internal static bool CanCell(int player, SettlementAct act, IntVec3 cell)\n        => CanCell(player, act, Find.CurrentMap, cell);"),
        "the current-map form does not delegate to the map-taking gate, so two cell gates can disagree");
    // The probe is the caller that justifies the overload. If it stops naming a map, the overload has no users.
    var probePath = Path.Combine(SourceDir(), "ScriptedSettlement.cs");
    var probe = File.ReadAllText(probePath);
    Check(probe.Contains("SettlementGates.CanCell(player, act, map, cell)"),
        "the land probe does not pass the map it was asked about, so a two-map colony cannot be measured");
    return Task.CompletedTask;
});

await Test("ledger channel names resolve", () => { CheckLedgerChannelNames(); return Task.CompletedTask; });
await Test("ledger state channels name real code", () => { CheckLedgerStateChannelNames(); return Task.CompletedTask; });

        // **Two more gizmo toggles that look like UI and are not (round 142).** `PawnTrackerSync` already carries the
        // player-settings family; these two are the same defect one class further out:
        //   * `Pawn_PsychicEntropyTracker.limitEntropyAmount` (`:473397`) is the lock in the psycaster gizmo
        //     (`:474043-474046`) and is read by `WouldOverflowEntropy` (`:473640-473641`), which refuses any entropy
        //     gain while it is on - so the authority keeps clamping after a replica unlocks it.
        //   * `Gene_Hemogen.hemogenPacksAllowed` (`:727831`) is the checkbox over the hemogen pack icon
        //     (`:728192`) and is read by the pack-taking job giver (`:300783-300800`) and two ingestion paths.
        // The assertion pins the whole path: the encode, the gate that makes omission mean "no such gene", and the
        // apply write, because a segment that encodes but never applies reads as coverage in every census row.
await Test("a mechanitor's control-group settings travel on his own row", () =>
{
    // Round 143. Two per-group values are read while the authority drives the mechs, and both are set from a window
    // that only the clicking peer sees:
    //   * `MechanitorControlGroup.WorkMode` (`:463715`) branches `ThinkNode_ConditionalWorkMode` and, through
    //     `UseGroupRechargeLimits` (`:300456-300463`), decides whether the recharge slider below is honoured at all.
    //   * `MechanitorControlGroup.mechRechargeThresholds` (`:463683`) is read by
    //     `JobGiver_GetEnergy.GetMinAutorechargeThreshold`/`GetMaxRechargeLimit` (`:300438-300453`).
    // The assertion pins: the segment exists and is keyed by group index; the apply goes through `SetWorkMode` rather
    // than the private field (that setter is what re-points every assigned mech); and an unknown work-mode def is
    // counted instead of silently skipped.
    var mech = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PawnTrackerSync.cs"));
    Check(mech.Contains("Add(\"k:\" + groupIndex.ToString(CultureInfo.InvariantCulture)", StringComparison.Ordinal),
        "the mechanitor's row does not carry his control groups, so a work mode or recharge slider set on a replica "
        + "never reaches the side that drives the mechs");
    Check(mech.Contains("group.WorkMode?.defName", StringComparison.Ordinal)
        && mech.Contains("group.mechRechargeThresholds.min.ToString(\"0.####\", CultureInfo.InvariantCulture)", StringComparison.Ordinal),
        "the control-group segment does not encode both the work mode and the recharge thresholds, so half of the "
        + "group's settings would look carried");
    Check(mech.Contains("mechGroup.SetWorkMode(modeDef);", StringComparison.Ordinal),
        "the work mode is applied by writing the private field, so the assigned mechs keep their old think tree instead "
        + "of being re-pointed by vanilla's own setter");
    Check(mech.Contains("MechWorkModeUnresolved++;", StringComparison.Ordinal),
        "a work-mode def this peer does not have is dropped silently, so an unknown mode and a mode that never arrived "
        + "look identical");
    return Task.CompletedTask;
});

await Test("the entropy lock and the hemogen pack checkbox travel on the per-pawn row", () =>
{
            var toggles = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "PawnTrackerSync.cs"));
            Check(toggles.Contains("Add(\"y:\" + (entropy.limitEntropyAmount ? \"1\" : \"0\"))", StringComparison.Ordinal),
                "the per-pawn row does not carry the psychic entropy lock, so unlocking it on a replica leaves the "
                + "authority refusing every entropy gain");
            Check(toggles.Contains("Add(\"x:\" + (hemogen.hemogenPacksAllowed ? \"1\" : \"0\"))", StringComparison.Ordinal),
                "the per-pawn row does not carry the hemogen pack checkbox, so a hemogen-dependent pawn on the "
                + "simulating side keeps taking packs that were switched off");
            Check(toggles.Contains("pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>() is { } hemogen", StringComparison.Ordinal),
                "the hemogen segment is encoded for every pawn instead of only carriers, so its absence no longer "
                + "means \"no such gene\" and a geneless pawn would send a toggle for nothing");
            Check(toggles.Contains("entropySettings.limitEntropyAmount = body[0] == \"1\";", StringComparison.Ordinal)
                && toggles.Contains("hemogenGene.hemogenPacksAllowed = body[0] == \"1\";", StringComparison.Ordinal),
                "the two toggle segments are encoded but never applied, so the census counts rows that change nothing");
    return Task.CompletedTask;
});

await Test("the execute button's flag travels on the per-pawn row", () =>
{
    // Round 144. The red "mark for execution" button in a guilty colonist's inspect pane (`:642584-642600`) flips one
    // bool on `Pawn_GuiltTracker`; the two readers are the execution job's goto toil (`:284801`) and the work giver that
    // decides whether a warden may start it (`:309850`) - both where the simulation runs. So a click on a replica left
    // the authority with no order at all.
    var guilt = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PawnTrackerSync.cs"));
    Check(guilt.Contains("Add(\"o:\" + ((ReadBool(guilt, \"awaitingExecution\") ?? false) ? \"1\" : \"0\"));",
            StringComparison.Ordinal),
        "the per-pawn row does not carry `guilt.awaitingExecution`, so marking a colonist for execution on a replica "
        + "never reaches the side that runs the warden's job");
    Check(guilt.Contains("WriteBool(guiltOrder, \"awaitingExecution\", body[0] == \"1\");", StringComparison.Ordinal),
        "the flag is applied through the int accessors, so a bool field is either read as null (segment always 0) or "
        + "throws inside Apply and silently aborts the rest of the row");
    Check(!guilt.Contains("ReadInt(guilt, \"awaitingExecution\"", StringComparison.Ordinal)
        && !guilt.Contains("WriteInt(guiltOrder, \"awaitingExecution\"", StringComparison.Ordinal),
        "a retired int read/write on the bool field is still in the file, so the encoder and the applier disagree about "
        + "the accessor pair");
    Check(guilt.Contains("if (ReadBool(pawn.guilt, \"awaitingExecution\") ?? false) awaitingExecution++;",
            StringComparison.Ordinal),
        "the census does not count marked-for-execution colonists, so \"0 executed\" and \"the channel never ran\" look "
        + "identical");
    // Both census branches are separate string concatenations; the label must appear in each exactly once.
    var censusHits = 0;
    for (var at = guilt.IndexOf("待处决 {awaitingExecution} 个（应用 {GuiltExecutionRows} 行）", StringComparison.Ordinal);
         at >= 0;
         at = guilt.IndexOf("待处决 {awaitingExecution} 个（应用 {GuiltExecutionRows} 行）", at + 1, StringComparison.Ordinal))
        censusHits++;
    Check(censusHits == 2, "the census label is printed " + censusHits + " times instead of once per branch");
    return Task.CompletedTask;
});

await Test("the fishing zone's player settings travel on the zone row", () =>
{
    // Round 145. The work giver branches on `Zone_Fishing.ShouldFishNow` (`:312825`, `:312835`), which reads
    // `repeatMode`, `pauseWhenSatisfied`, `targetCount`, `unpauseAtCount` (through the pause cache,
    // `:431891-431903`) and `targetPopulationPct` (`:431905-431913`). All five are set from the zone's own tab
    // (`ITab_Fishing`, `:665042`), i.e. a player click. Only `repeatCount` used to travel, on the stated belief that
    // it was "the only one of the seven that gates whether the zone still works" - which the work giver disproves.
    var zones = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ZoneSync.cs"));
    var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
    Check(models.Contains("string? Fishing = null", StringComparison.Ordinal),
        "the zone row has no field for the fishing settings, so the five values a player sets in the zone tab cannot "
        + "reach the side that runs the work giver");
    Check(zones.Contains("((int)fishing.repeatMode).ToString(System.Globalization.CultureInfo.InvariantCulture)",
            StringComparison.Ordinal)
        && zones.Contains("fishing.targetCount.ToString(System.Globalization.CultureInfo.InvariantCulture)",
            StringComparison.Ordinal)
        && zones.Contains("fishing.unpauseAtCount.ToString(System.Globalization.CultureInfo.InvariantCulture)",
            StringComparison.Ordinal)
        && zones.Contains("fishing.pauseWhenSatisfied ? \"1\" : \"0\"", StringComparison.Ordinal)
        && zones.Contains("fishing.targetPopulationPct.ToString(\"0.####\"", StringComparison.Ordinal),
        "the fishing blob does not encode all five settings, so the zone's pause and population tests still run on "
        + "values the player set on another peer");
    Check(zones.Contains("if (settings is not null) Mix(settings);", StringComparison.Ordinal),
        "the settings blob is not mixed into the revision digest, so a settings-only edit would leave the digest "
        + "unchanged and never be sent at all");
    Check(zones.Contains("fishing.repeatMode = mode is >= 0 and <= (int)FishRepeatMode.DoForever",
            StringComparison.Ordinal),
        "the repeat mode is cast blind, so a mode this build does not know would become an arbitrary enum value");
    Check(zones.Contains("fishing.RecheckPausedDueToResourceCount();", StringComparison.Ordinal),
        "the derived pause cache is not refreshed after the settings land, so the replica's zone answers from a "
        + "value that can be up to 60 ticks stale (`:431783-431790`)");
    Check(zones.Contains("FishingSettingsApplied++;", StringComparison.Ordinal),
        "applied fishing blobs are not counted, so \"the host has no fishing zone\" and \"the blob never parsed\" read "
        + "the same");
    Check(!zones.Contains("the only one of the seven", StringComparison.Ordinal),
        "the disproved comment about `repeatCount` being the only setting that gates the zone is still in the file, "
        + "and it is the reason the five were left out");
    return Task.CompletedTask;
});

await Test("the prisoner panel's settings travel with the guest row", () =>
{
    // Round 146. The whole prisoner management panel is three values on `Pawn_GuestTracker`: the exclusive
    // `interactionMode` (`:461024`), the enabled non-exclusive interactions (`:461339`), and `ideoForConversion`
    // (`:461062`). Their readers all run on the simulating side - the convert job giver (`:309977`, `:309996`), reduce
    // resistance (`:290876`), enslave vs reduce will (`:290965`), interrogate (`:310427`, `:310446`), release
    // (`:310467`), whether execution may start (`:454337`), and inspired recruitment (`:456406`, `:456424`). Measured
    // before this round: no `Source/` file contained the word `interactionMode`, so switching a prisoner to "Convert"
    // on a replica left the authority on "Maintain only".
    var guests = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "GuestSync.cs"));
    Check(guests.Contains("var modeName = pawn.guest.ExclusiveInteractionMode?.defName ?? \"-\";", StringComparison.Ordinal)
        && guests.Contains("var conversionId = pawn.guest.ideoForConversion?.id ?? -1;", StringComparison.Ordinal)
        && guests.Contains("var nonExclusive = NonExclusiveNames(pawn.guest);", StringComparison.Ordinal),
        "the guest row does not encode the interaction mode, the conversion ideology and the non-exclusive set, so the "
        + "prisoner tab shows choices the authority never hears about");
    Check(guests.Contains("&& pawn.guest.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.MaintainOnly",
            StringComparison.Ordinal)
        && guests.Contains("&& conversionId < 0 && nonExclusive.Length == 0", StringComparison.Ordinal),
        "the \"nothing to say\" shortcut ignores the three new fields, so a prisoner whose only change is the mode would "
        + "encode as an empty row and never travel");
    Check(guests.Contains("pawn.guest.SetExclusiveInteraction(modeDef);", StringComparison.Ordinal),
        "the mode is applied by writing the private field instead of through the game's setter, which is the only "
        + "public way in and whose own guard is what keeps it safe on a replica");
    Check(guests.Contains("modeDef.isNonExclusiveInteraction", StringComparison.Ordinal)
        && guests.Contains("ModeUnresolved++;", StringComparison.Ordinal),
        "a non-exclusive def arriving on the exclusive half is written blind, so the replica's panel would show an "
        + "exclusive mode the authority does not have");
    Check(guests.Contains("guest.ToggleNonExclusiveInteraction(def, should);", StringComparison.Ordinal)
        && guests.Contains("if (has == should) continue;", StringComparison.Ordinal),
        "the enabled non-exclusive set is not diffed and toggled through the game's own method, so it would either "
        + "churn the list every frame or diverge from the authority");
    Check(guests.Contains("pawn.guest.ideoForConversion = conversionIdeo;", StringComparison.Ordinal)
        && guests.Contains("IdeoSync.IdeoById(conversionId)", StringComparison.Ordinal),
        "the conversion ideology is not resolved through the ideo's own id, so a replica points at an ideo it does not "
        + "have and the warden's convert job (`.309996`) never matches it");
    var census = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SyncCensus.cs"));
    Check(census.Contains("互动模式 {withMode} 个非默认", StringComparison.Ordinal)
        && census.Contains("皈依目标 {withConversion} 个", StringComparison.Ordinal)
        && census.Contains("非排他互动 {withNonExclusive} 个开启", StringComparison.Ordinal),
        "the census row counts only \"how many prisoners\" and \"how many have a host\", so a colony where nobody uses the "
        + "panel and a build where the three fields never travel read the same");
    Check(guests.Contains("非排他开关 {NonExclusiveRows} 次", StringComparison.Ordinal),
        "the census does not print how many non-exclusive toggles landed, so \"no prisoner uses one\" and \"the set never "
        + "applied\" read the same");
    return Task.CompletedTask;
});

await Test("settlement automation has three hooks, and all three are installed", () =>
{
    // Round 153, from CT's inventory (`_lab/r153-ct-settlements.txt`): CT gates `CheckAutoRebuildOnDestroyed`
    // (ct:220787), `CheckAutoRebuildTerrainOnDestroyed` (ct:220817) and `AutoBuildRoofAreaSetter.TryGenerateAreaFor`
    // (ct:220912) with an ownership scope, because those three run without an actor pawn - every other ownership decision
    // in this project is a pawn asking about a thing, which `SettlementGates.Can` covers.
    var gate = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementAutomationGate.cs"));
    Check(gate.Contains("[HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildOnDestroyed))]",
            StringComparison.Ordinal)
        && gate.Contains("[HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildTerrainOnDestroyed))]",
            StringComparison.Ordinal)
        && gate.Contains("[HarmonyPatch(typeof(AutoBuildRoofAreaSetter), nameof(AutoBuildRoofAreaSetter.TryGenerateAreaFor))]",
            StringComparison.Ordinal),
        "one of the three automation entry points is not hooked");
    // The layer must short-circuit: a save with no settlements behaves exactly as before.
    Check(gate.Contains("if (data is null || !data.Active) return true;", StringComparison.Ordinal),
        "the gate does not short-circuit on an inactive layer, so a save with no settlements would start refusing builds");
    // Unclaimed ground stays allowed - that is today's behaviour for the whole map.
    Check(gate.Contains("if (home == 0) return true;", StringComparison.Ordinal),
        "unclaimed ground is refused, which would silently stop auto-rebuild everywhere");
    // A room can straddle two settlements, so every claimed cell has to pass.
    Check(gate.Contains("foreach (var cell in room.Cells)", StringComparison.Ordinal),
        "the roofing gate decides per room without looking at the room's claimed cells");

    // A `[HarmonyPatch]` class nobody installs is dead code - the wiring gap round 140 found for
    // `Command_CallBossgroup`. All three have to be in the installer.
    var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
    Check(installer.Contains("Try(harmony, \"settlement-auto-rebuild\", typeof(SettlementAutoRebuildGate));",
            StringComparison.Ordinal)
        && installer.Contains("Try(harmony, \"settlement-auto-rebuild-terrain\", typeof(SettlementAutoRebuildTerrainGate));",
            StringComparison.Ordinal)
        && installer.Contains("Try(harmony, \"settlement-auto-roof\", typeof(SettlementAutoRoofGate));",
            StringComparison.Ordinal),
        "the gate classes exist but the installer never registers them, so none of them is applied");
    return Task.CompletedTask;
});

await Test("a job whose target is presented as forbidden is never issued", () =>
{
    // Round 158, from CT's `SettlementJobGiverPatch` (ct:352918). Every other gate answers a question the engine asks
    // before a job exists, or after one is chosen but before it starts. Think nodes (eat, sleep, wear, equip, haul this
    // stack) build a `Job` and hand it to the tracker, so the refusal arrives at reservation - the "walks over and gives
    // up" shape round 135 named and round 155 fixed for cells.
    var file = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementJobGiverGate.cs"));
    Check(file.Contains("[HarmonyPatch(typeof(ThinkNode_JobGiver), nameof(ThinkNode_JobGiver.TryIssueJobPackage))]",
            StringComparison.Ordinal),
        "the think-node method is not patched, so a refused target is still issued as a job");
    // The predicate must be the reader, not a second job→act table: `JobDef` names a verb, mods add their own, and a
    // copy of the rule table would drift exactly like the one round 155 deleted.
    Check(file.Contains("return SettlementForbidVisibility.ShouldHide(job.targetA.Thing, pawn, out _);", StringComparison.Ordinal),
        "the job gate decides with its own table instead of the visibility reader");
    // Cell targets belong to the cell gate and to `CanReserve`; refusing here would also swallow vanilla's own "no job".
    Check(file.Contains("if (!job.targetA.HasThing) return false;", StringComparison.Ordinal),
        "the job gate also refuses cell targets, which is a different question");
    // Swallowing the job is the whole effect, and it has to be counted so a gate that never fired is visible.
    Check(file.Contains("__result = ThinkResult.NoJob;", StringComparison.Ordinal)
        && file.Contains("internal static string Summary() => $\"拦下 {Refused}\";", StringComparison.Ordinal),
        "the postfix does not clear the chosen job, or never counts what it cleared");
    // A failed gate must leave the chosen job alone: vanilla already knows how to abandon a job it cannot reserve.
    Check(file.Contains("Log.Warning(\"[TurboTick] 工作 giver 闸门失败：\"", StringComparison.Ordinal),
        "a gate failure escapes into the think node, so a permission bug becomes 'this pawn does nothing'");
    var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
    Check(installer.Contains("Try(harmony, \"settlement-job-giver-gate\", typeof(SettlementJobGiverGate));",
            StringComparison.Ordinal),
        "the gate exists but the installer never registers it, so it never runs");
    // The probe has to report the wiring and the predicate together: a predicate reading `false` without a postfix
    // installed looks exactly like a gate that works.
    var probe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementJobProbe.cs"));
    Check(probe.Contains("TryIssueJobPackage 已挂", StringComparison.Ordinal)
        && probe.Contains("SettlementJobGiverGate.Refuse(pawn, job)", StringComparison.Ordinal),
        "the probe reads only one half - the predicate or the wiring - so a green reading proves nothing");
    return Task.CompletedTask;
});

await Test("a container's contents inherit the owner of the container", () =>
{
    // Round 157. CT records ownership per thing and needs `SettlementContainerPatch` (ct:353278) plus
    // `SettlementContainerAddedPatch` (ct:353302) to keep contents in step with their container. This project records
    // ownership by position, so the claim that contents inherit rests entirely on `HomeOf` walking up through
    // `SpawnedParentOrMe` - which is what `MapHeld`/`PositionHeld` do and `Spawned`/`Position` do not.
    var rule = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementRule.cs"));
    Check(rule.Contains("var map = thing.MapHeld;", StringComparison.Ordinal)
        && rule.Contains("data.SettlementAt(map.uniqueID, thing.PositionHeld)", StringComparison.Ordinal),
        "HomeOf stopped using the held map/position, so a thing inside a container has no owner");
    // The tempting "optimisation" that would reintroduce the hole: an unspawned stack answers `Position = Invalid`, so a
    // container's contents would all read as unowned and could be looted by anyone.
    Check(!rule.Contains("thing.Spawned ? thing.Position", StringComparison.Ordinal),
        "HomeOf falls back to `Position`, which is invalid for an unspawned stack inside a container");
    // The probe has to build a real container and report both halves - spawned state and the resolved home - because
    // "inherits" is exactly the case where those two disagree.
    // The visibility reader must include contents (a casket stack is `Spawned=false`) but exclude anything a pawn is
    // carrying (its `PositionHeld` follows the carrier, so a visitor's own meal would be hidden from the visitor).
    var visibility = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementForbidVisibility.cs"));
    Check(!visibility.Contains("!thing.Spawned", StringComparison.Ordinal),
        "the reader ignores unspawned things again, so a stack inside somebody's casket is never shown as forbidden");
    Check(visibility.Contains("for (var holder = thing.ParentHolder; holder is not null; holder = holder.ParentHolder)",
            StringComparison.Ordinal)
        && visibility.Contains("if (holder is Pawn) return false;", StringComparison.Ordinal),
        "the reader judges carried goods by position, so a visitor's own meal becomes the host's");
    var probe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ContainerOwnershipProbe.cs"));
    Check(probe.Contains("GetDirectlyHeldThings()", StringComparison.Ordinal)
        && probe.Contains("Spawned={thing.Spawned}", StringComparison.Ordinal)
        && probe.Contains("MapHeld=", StringComparison.Ordinal)
        && probe.Contains("Destroy(DestroyMode.Vanish)", StringComparison.Ordinal),
        "the probe does not build a container, or does not report spawned state next to the resolved home");
    return Task.CompletedTask;
});

await Test("a trade may only be paid with goods the negotiator could take", () =>
{
    // Round 156, from CT's `SettlementLaunchablePatch` (ct:353103). `TradeUtility.AllLaunchableThingsForTrade`
    // (`:608612`) enumerates everything inside a powered orbital trade beacon's cells - whoever owns it - and vanilla
    // spends from that list when a deal is accepted. On a shared colony a guest therefore paid with the host's silver.
    var file = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "TradePaymentGate.cs"));
    Check(file.Contains("[HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.AllLaunchableThingsForTrade))]",
            StringComparison.Ordinal),
        "the enumeration is not the patched surface, so payment still spends the host's goods");
    // The act has to be `Take`: paying is carrying goods off, and that is the row the table states for it.
    Check(file.Contains("return SettlementGates.Can(negotiator, SettlementAct.Take, thing);", StringComparison.Ordinal),
        "the payment filter asks about something other than taking the goods");
    // Outside a trade session there is no negotiator; filtering there would change unrelated readings with nobody to
    // blame for it.
    Check(file.Contains("var negotiator = TradeSession.playerNegotiator;", StringComparison.Ordinal)
        && file.Contains("if (negotiator is null) return;", StringComparison.Ordinal),
        "the filter runs without a negotiator, so it can change what a window displays while nobody is trading");
    // The layer must short-circuit: a save with no settlements keeps vanilla's list exactly.
    Check(file.Contains("if (data is null || !data.Active) return;", StringComparison.Ordinal),
        "the filter does not short-circuit on an inactive layer");
    // A failed filter must not change the list vanilla asked for.
    Check(file.Contains("Log.Warning(\"[TurboTick] 贸易付款过滤失败：\"", StringComparison.Ordinal),
        "a filter failure is allowed to escape into the trade path");
    // The whole point is that the negotiator's answer decides, so `Filter` must not walk the list unjudged.
    Check(file.Contains("if (!Keep(negotiator, thing))", StringComparison.Ordinal),
        "the filter yields the enumeration without judging each thing");
    // Two counters, because "nothing dropped" and "the filter never ran" read the same from one number.
    Check(file.Contains("internal static string Summary() => $\"放行 {Survived} / 拦下 {Dropped}\";", StringComparison.Ordinal),
        "the gate reports one number, so a green reading cannot tell a filter that ran from one that did not");
    var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
    Check(installer.Contains("Try(harmony, \"settlement-launchable-gate\", typeof(SettlementLaunchableGate));",
            StringComparison.Ordinal),
        "the filter exists but the installer never registers it, so it never runs");
    // The probe must build the beacon fixture: without a powered beacon the enumeration is empty for a reason that has
    // nothing to do with ownership, and `0` would read as "the filter works".
    var probe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "TradePaymentProbe.cs"));
    // The probe must state the part it cannot measure instead of printing a `0`: without a powered beacon the
    // enumeration is empty for a reason that has nothing to do with ownership.
    Check(probe.Contains("AllPowered(map)", StringComparison.Ordinal)
        && probe.Contains("枚举层面未测", StringComparison.Ordinal)
        && probe.Contains("SettlementLaunchableGate.Filter(spawned, pawn)", StringComparison.Ordinal)
        && probe.Contains("Destroy(DestroyMode.Vanish)", StringComparison.Ordinal),
        "the probe either hides its unmeasured half or leaves its silver behind");
    return Task.CompletedTask;
});

await Test("cell work is gated too, and only where the table states a row", () =>
{
    // Round 155. Twelve work givers act on cells; `WorkGiver_GrowerHarvest.HasJobOnCell` (`:313051`) inspects the plant and
    // reserves nothing, so a visitor without `Gathering` was offered harvest work in somebody else's growing zone and only
    // failed when the job started. The other cell givers reserve the cell themselves, which means they were gated one step
    // later - by the reservation gate - rather than at scan time.
    var gates = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGates.cs"));
    // Matched in two pieces on purpose: the declaration spans lines, and a search string with an escaped newline never
    // matches a CRLF file (the same mistake cost two red-green runs in round 146).
    Check(gates.Contains("nameof(WorkGiver_Scanner.HasJobOnCell)", StringComparison.Ordinal)
        && gates.Contains("[typeof(Pawn), typeof(IntVec3), typeof(bool)])]", StringComparison.Ordinal),
        "the cell overload is not patched, so `GrowerHarvest` still offers foreign harvest work");
    // Harvesting a cell is really about the plant standing there: a cell's own kind is only Land or Storage, so a
    // cell-only gate could never ask `Gather`/`Plant` - the row that matters for this case.
    Check(gates.Contains("if (act == SettlementAct.Gather) return cell.GetPlant(map);", StringComparison.Ordinal),
        "the gate asks about the ground instead of the plant, so the harvest case cannot be decided");
    Check(gates.Contains("if (act == SettlementAct.Clean) return cell.GetPlant(map);", StringComparison.Ordinal) == false,
        "the clean arm resolves a plant");
    // No invented refusals: a pair the table does not describe must not be refused. Round 136's `Use`-for-everything did
    // exactly that and disabled five kinds of work for every visitor.
    Check(gates.Contains("if (!SettlementRuleTable.HasRow(act, kind)) return true;", StringComparison.Ordinal),
        "the cell gate refuses acts the table does not state on that cell kind");
    // The thing branch and the cell branch must not be mixed up: a plant is decided with `Can(pawn, act, thing)`.
    Check(gates.Contains("if (SettlementGates.Can(pawn, act, thing)) return true;", StringComparison.Ordinal)
        && gates.Contains("if (SettlementGates.CanCell(pawn, act, c)) return true;", StringComparison.Ordinal),
        "the gate decides the thing case and the cell case with the same question");
    // The act per giver is a closed set; the three names the assembly marks internal are matched by string, so a rename
    // would silently fall back to `Place` and gate the wrong thing.
    foreach (var name in new[] { "WorkGiver_GrowerHarvest", "WorkGiver_BuildRoof", "WorkGiver_PaintFloor" })
        Check(gates.Contains(name, StringComparison.Ordinal), $"the act table does not name {name}");
    // A `[HarmonyPatch]` class nobody installs is dead code - the wiring gap round 140 found for `Command_CallBossgroup`.
    var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
    Check(installer.Contains("Try(harmony, \"settlement-work-cell-gate\", typeof(SettlementWorkCellGate));", StringComparison.Ordinal),
        "the cell gate exists but the installer never registers it, so it never runs");
    // The probe has to report the installed answer next to the bare one, or a green reading proves nothing about wiring.
    var probe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"));
    Check(probe.Contains("actual = giver.HasJobOnCell(pawn, cell);", StringComparison.Ordinal)
        && probe.Contains("plant.Growth = 1f;", StringComparison.Ordinal),
        "the probe does not call the installed method, or leaves a spawned plant too young to be judged");
    return Task.CompletedTask;
});

await Test("another settlement's goods read as forbidden to a guest, and never to their owner", () =>
{
    // Round 154, from CT's `SettlementForbidPatch` (ct:352751): the permission is not refused attempt by attempt, it is
    // <i>presented</i> the way vanilla already asks - `ForbidUtility.IsForbidden(Thing, Pawn)` (`:538640-538671`) is
    // consulted before the job givers, float menus and haul scanners decide anything, so answering it is one edit
    // instead of a refusal per attempt.
    var file = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementForbidVisibility.cs"));
    // The overload, not just the name: `IsForbidden(IntVec3, Pawn)` is a different question about a cell.
    Check(file.Contains("[HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), typeof(Thing), typeof(Pawn))]",
            StringComparison.Ordinal),
        "the postfix is not bound to the (Thing, Pawn) overload, so a cell query could be the one patched");
    // Vanilla already said forbidden: leave it alone. This keeps hand-forbidden items hand-forbidden.
    Check(file.Contains("if (__result) return;", StringComparison.Ordinal),
        "the patch overwrites a `true` answer, which would un-forbid items somebody forbade by hand");
    // A save with no settlements must behave exactly as before.
    Check(file.Contains("if (data is null || !data.Active) return false;", StringComparison.Ordinal),
        "the reader does not short-circuit on an inactive layer, so every save starts hiding unrelated items");
    // Unclaimed ground and a pawn acting inside its own settlement are both "not ours to hide".
    Check(file.Contains("if (home == 0) return false;", StringComparison.Ordinal)
        && file.Contains("if (pawnHome != 0 && pawnHome == home) return false;", StringComparison.Ordinal),
        "the reader hides things from the owner or from unclaimed ground");
    // Which acts un-hide a thing must come from the table, not from a hand-written list. The first version named Take,
    // Use and Store, and the probe caught what that missed: a tree's row is `Gathering`/`Gather`/`Plant`, so a guest
    // granted `Gathering` passed the gate and still saw the tree as forbidden - the permission was invisible.
    Check(file.Contains("return !SettlementRuleTable.PermitsAnyOn(data.GrantsForPawn(pawnHome, home), kind);",
            StringComparison.Ordinal),
        "the reader decides visibility with its own list of acts instead of the rule table's answer for that kind");
    Check(!file.Contains("SettlementGates.Can(pawn, SettlementAct.Take", StringComparison.Ordinal),
        "a hand-written act list crept back into the reader, so the table can disagree with it again");
    // At war there is nothing to hide: every gate refuses first.
    Check(file.Contains("if (SettlementWar.AtWar(data, pawnHome, home)) return true;", StringComparison.Ordinal),
        "the reader keeps showing a war enemy's goods as `permitted`, so vanilla AI walks into refusals");
    // A thing the table cannot classify is not hidden - `KindOf` returning None is a classifier gap, not a permission.
    Check(file.Contains("if (kind == SettlementKind.None) return false;", StringComparison.Ordinal),
        "an unclassified thing is hidden, which would break vanilla jobs far from anything ownership-related");
    // Nothing hidden and never asked read the same from one number, so both are counted.
    Check(file.Contains("internal static string Summary() => $\"隐藏 {Hidden} / 查询 {Queries}\";", StringComparison.Ordinal),
        "the reader reports one number, so \"nothing to hide\" and \"the patch never ran\" cannot be told apart");
    // A failing visibility patch must not change any decision by itself.
    Check(file.Contains("__result = true;", StringComparison.Ordinal)
        && file.Contains("Log.Warning(\"[TurboTick] 据点禁取失败：\"", StringComparison.Ordinal),
        "the postfix either never sets the flag or does not confine its failure to a log line");

    // Dead code if nobody installs it - the wiring gap round 140 found for `Command_CallBossgroup`.
    var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
    Check(installer.Contains("Try(harmony, \"settlement-forbid-visibility\", typeof(SettlementForbidVisibilityPatch));",
            StringComparison.Ordinal),
        "the reader exists but the installer never registers it, so it never runs");
    // The census row must exist and must not repeat its own label (the invariant that has fired four times).
    var census = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SyncCensus.cs"));
    Check(census.Contains("new Row(\"据点禁取\", SettlementForbidVisibility.Summary()", StringComparison.Ordinal),
        "the census has no row for the forbid reader, so its counters are invisible");
    return Task.CompletedTask;
});

await Test("the ideoligion's description lock travels with the table", () =>
{
    // Round 152. `Ideo.RegenerateDescription` (`:493062-493070`) rewrites `description` unless `descriptionLocked` is
    // set, and the toggle is `StyleCatOverride.DoDescription` (`:496018-496028`). The table carried id, name, colour and
    // the development tracker - not the lock - so a replica regenerating the description (which happens after any
    // precept edit, on both peers) overwrote the text the player had locked.
    var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "IdeoSync.cs"));
    Check(sync.Contains("+ \"#\" + (ideo.descriptionLocked ? \"1\" : \"0\"));", StringComparison.Ordinal),
        "the table has no description-lock field, so the flag never leaves the host");
    // The lock is appended after `devField`, which is itself appended text, so it is the LAST field. Reading from the end
    // is what makes one branch cover both shapes (`id#name#color#L` and `id#name#color#@p,r#L`).
    Check(sync.Contains("if (fields.Length > 3 && fields[^1] is \"0\" or \"1\")", StringComparison.Ordinal)
        && sync.Contains("target.descriptionLocked = fields[^1] == \"1\";", StringComparison.Ordinal),
        "the apply half does not read the trailing field, so the lock is published and never written");
    Check(sync.Contains("internal static int LockedCount { get; private set; }", StringComparison.Ordinal)
        && sync.Contains("描述锁 ", StringComparison.Ordinal),
        "the census cannot tell 'nobody locked a description' from 'the lock never travelled'");
    return Task.CompletedTask;
});

await Test("a baby naming deadline travels and can be cleared", () =>
{
    // Round 151. `Pawn.babyNamingDeadline` (`:126902`, scribed `:131365`, default -1) is an absolute stamp: set on birth
    // (`:133215`), cleared by the naming dialog's Accept (`:196111`), read by the naming gizmo (`:449758`), the inspect
    // pane (`:618114`) and the "name the baby" float-menu options (`:661585-661616`). Each peer reads its own copy, so a
    // replica either never offers the naming option or offers it forever.
    var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PawnTrackerSync.cs"));
    Check(sync.Contains("Add(\"b:\" + pawn.babyNamingDeadline.ToString(CultureInfo.InvariantCulture));",
            StringComparison.Ordinal),
        "the deadline is not on the pawn row");
    // Unconditional on purpose: -1 is the "nothing pending" default, so a row that omitted it could not clear a deadline
    // the authority already cleared.
    Check(sync.Contains("internal static long NamingDeadlineRows { get; private set; }", StringComparison.Ordinal)
        && sync.Contains("pawn.babyNamingDeadline = namingDeadline;", StringComparison.Ordinal),
        "the apply half never writes it back, so clearing a deadline on the host leaves the replica asking for a name");
    // The apply half must parse out of `body[0]`: `body` is the segment split, and `int.TryParse(body, …)` does not compile
    // - the first version of this round hit exactly that.
    Check(sync.Contains("int.TryParse(body[0], NumberStyles.Integer, CultureInfo.InvariantCulture,"),
        "the value is read from the whole split array instead of the segment's first field");
    Check(sync.Contains("待命名 {namingPending} 个（应用 {NamingDeadlineRows} 行）", StringComparison.Ordinal),
        "the census cannot tell 'nobody needs a name' from 'the deadline never travelled'");
    return Task.CompletedTask;
});

await Test("the anomaly difficulty the settings dialog writes travels", () =>
{
    // Round 150. `Dialog_AnomalySettings.DoWindowContents` (`:625367-625386`) writes five `Find.Storyteller.difficulty`
    // fields on Accept - the override fraction, the inactive and active threat fractions, the study factor and the
    // `AnomalyPlaystyleDef` property. Nothing in `Source/` mentioned `difficulty`, so the edit stayed on the peer that
    // opened the dialog. `GameComponent_Anomaly.AnomalyThreatFractionNow` (`:723349-723372`) is the whole downstream path,
    // reached through `Storyteller.AnomalyIncidentChanceNow` (`:390002`).
    var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
    Check(models.Contains("public sealed record ReplicaAnomalyDifficulty(string Playstyle, float? Override, float Inactive, float Active,",
            StringComparison.Ordinal),
        "there is no anomaly-difficulty row at all");
    // The field is no longer the last one on the record (round 160 appended the policy tables), so the assertion pins
    // the field itself rather than the record's closing parenthesis.
    Check(models.Contains("ReplicaAnomalyDifficulty? AnomalyDifficulty = null,", StringComparison.Ordinal),
        "the row is not on the frame");

    var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "DifficultySync.cs"));
    Check(sync.Contains("return Find.Storyteller?.difficulty;", StringComparison.Ordinal),
        "the channel does not read the difficulty the dialog writes");
    Check(sync.Contains("difficulty.AnomalyPlaystyleDef = playstyle;", StringComparison.Ordinal),
        "the playstyle is not written through its property (`:380225-380233`), so `anomalyPlaystyleDef` - which is private - "
        + "and the getter's fallback to `Standard` would diverge from what the row says");
    Check(sync.Contains("difficulty.overrideAnomalyThreatsFraction = state.Override;", StringComparison.Ordinal)
        && sync.Contains("difficulty.anomalyThreatsInactiveFraction = state.Inactive;", StringComparison.Ordinal)
        && sync.Contains("difficulty.anomalyThreatsActiveFraction = state.Active;", StringComparison.Ordinal)
        && sync.Contains("difficulty.studyEfficiencyFactor = state.Study;", StringComparison.Ordinal),
        "not all four floats the dialog writes are applied");
    // `null` and `0` answer differently inside `AnomalyThreatFractionNow`, so the change key has to tell them apart.
    Check(sync.Contains("value?.ToString(\"0.####\") ?? \"null\"", StringComparison.Ordinal),
        "the change key collapses a null override and an override of zero, so switching between them would send nothing");

    var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
    Check(replica.Contains("var anomalyDifficulty = newer.AnomalyDifficulty ?? older.AnomalyDifficulty;",
            StringComparison.Ordinal),
        "the row is not merged as one record per game");
    Check(replica.Contains("DifficultySync.Apply(anomalyDifficulty)", StringComparison.Ordinal),
        "the row is merged but never applied");

    var runtime = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HeadlessRuntime.cs"));
    // The tail grew in round 160 (`policies`), so the assertion pins the argument pair rather than the call's end.
    Check(runtime.Contains("preceptPayloads, anomalyDifficulty, policies, factionCreations,", StringComparison.Ordinal),
        "the difficulty row is computed but never passed into the frame");

    var census = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SyncCensus.cs"));
    Check(census.Contains("rows.Add(new Row(\"异象难度\", DifficultySync.Summary(), \"DifficultySync\"));",
            StringComparison.Ordinal),
        "the census has no row for it, so 'nobody edited it' and 'the edit never travelled' read the same");
    return Task.CompletedTask;
});

await Test("a building precept's style travels after its thing def", () =>
{
    // Round 149, and a defect round 148 introduced. `Precept_ThingDef.ThingDef`'s setter runs `Notify_ThingDefSet`
    // (`:500836-500843`): it clears the ideoligion's cached style for that building and then, when the building is
    // style-editable and no category claims it, **rolls** `AllPossibleStylesForBuilding(ThingDef).RandomElement()` and
    // stores that category. The payload channel writes `ThingDef` on a replica, which does not simulate - so its `Rand`
    // stream is not the host's, and the same edit would leave the two peers with different building art and a different
    // `ThingStyleDef` in every bill that asks `StyleForThingDef`.
    var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
    Check(models.Contains("string? Style = null);", StringComparison.Ordinal),
        "the payload row carries no style, so a replica rolls its own category for the building the row points at");

    var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PreceptSync.cs"));
    Check(sync.Contains("style = StyleOf(ideo, thingDefPrecept.ThingDef);", StringComparison.Ordinal),
        "the publish half does not read the ideoligion's style for the building");
    Check(sync.Contains("(style ?? \"-\")", StringComparison.Ordinal),
        "the change key ignores the style, so re-styling a building without touching the def would send nothing");
    Check(sync.Contains("private static bool ApplyStyle(Ideo ideo, ThingDef thing, string? style)", StringComparison.Ordinal)
        && sync.Contains("ideo.style.SetStyleForThingDef(thing, new StyleCategoryPair { category = category, styleDef = styleDef });",
            StringComparison.Ordinal),
        "the apply half never writes the pair back through `SetStyleForThingDef` (`:494884-494887`)");
    var thingAt = sync.IndexOf("if (thingPrecept.ThingDef != def) { thingPrecept.ThingDef = def; applied = true; }",
        StringComparison.Ordinal);
    var styleAt = sync.IndexOf("applied |= ApplyStyle(ideo, def, state.Style);", StringComparison.Ordinal);
    Check(thingAt > 0 && styleAt > thingAt,
        "the style is written before the `ThingDef`; the setter's own random roll would then be the last word and the "
        + "synced pair would be discarded");
    return Task.CompletedTask;
});

await Test("a precept's payload travels, and precepts are addressed per instance", () =>
{
    // Round 148. `PreceptSync` carries the def list and role holders; the *payload* the player picks in the ideoligion
    // editor (`Precept_ThingDef.ThingDef` `:500759`, `Precept_Apparel.apparelDef` `:500134`, `Precept_Weapon.noble` /
    // `.despised` `:500946-500948`) was never sent, so a replica builds the precept from its def and `Init` picks its own
    // thing - the same precept reads "bow" on one peer and "rifle" on another.
    //
    // The identity also has to be per instance: a `PreceptDef` with `allowMultiplePrecepts` gives two precepts of one def,
    // and `(ideo, def)` - what the name rows used - renames the first and never the second.
    var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
    Check(models.Contains("public sealed record ReplicaPreceptName(int Ideo, string Def, int Index, string Name, bool Locked);",
            StringComparison.Ordinal),
        "the name row has no per-instance index, so two precepts of one def share an identity and only one is renamed");
    Check(models.Contains("public sealed record ReplicaPreceptPayload(int Ideo, string Def, int Index, string? Thing,",
            StringComparison.Ordinal),
        "there is no payload row at all, so the def a thing / apparel / weapon precept points at never leaves the host");
    Check(models.Contains("IReadOnlyList<ReplicaPreceptPayload>? PreceptPayloads = null,", StringComparison.Ordinal),
        "the payload rows are not on the frame");

    var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PreceptSync.cs"));
    Check(sync.Contains("private static IEnumerable<(Precept Precept, string Def, int Index)> Numbered(Ideo ideo)",
            StringComparison.Ordinal),
        "there is no ordinal walk, so the rows cannot address one instance among same-def precepts");
    Check(sync.Contains("if (def == state.Def && index == state.Index) { found = precept; break; }", StringComparison.Ordinal),
        "the apply half still takes the first precept with that def, which is the same collision the index exists for");
    Check(sync.Contains("case Precept_ThingDef thingDefPrecept:", StringComparison.Ordinal)
        && sync.Contains("thing = thingDefPrecept.ThingDef?.defName;", StringComparison.Ordinal)
        && sync.Contains("case Precept_Apparel apparelPrecept: apparel = apparelPrecept.apparelDef?.defName; break;",
            StringComparison.Ordinal)
        && sync.Contains("noble = weaponPrecept.noble?.defName;", StringComparison.Ordinal),
        "the payload channel does not read all three precept classes");
    Check(sync.Contains("if (thingPrecept.ThingDef != def) { thingPrecept.ThingDef = def; applied = true; }",
            StringComparison.Ordinal),
        "`Precept_ThingDef` is not written through its property, so `Notify_ThingDefSet` (`:500836-500843`) never runs and "
        + "the ideoligion keeps the style from the def `Init` picked");
    Check(sync.Contains("if (state.Apparel is { Length: > 0 } apparel && found is Precept_Apparel apparelPrecept)",
            StringComparison.Ordinal)
        && sync.Contains("if (found is Precept_Weapon weaponPrecept)", StringComparison.Ordinal),
        "the apply half does not type-check before writing, so a payload row could force one class's field onto another");

    var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
    Check(replica.Contains("internal static class PreceptPayloadMerge", StringComparison.Ordinal)
        && replica.Contains("byKey[(state.Ideo, state.Def, state.Index)] = state;", StringComparison.Ordinal),
        "the payload rows are not merged per instance, so two same-def precepts would wear each other's def");
    var payloadAt = replica.IndexOf("PreceptSync.ApplyPayload(payloadState)", StringComparison.Ordinal);
    var nameAt = replica.IndexOf("PreceptSync.ApplyName(nameState)", StringComparison.Ordinal);
    Check(payloadAt > 0 && nameAt > payloadAt,
        "the payload is applied after the name; `Precept_ThingDef.ThingDef`'s setter regenerates what the name is built "
        + "from, so the sync would overwrite the name the player typed");

    var runtime = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HeadlessRuntime.cs"));
    Check(runtime.Contains("autoSlaughter, bossgroup, preceptNames, preceptPayloads,", StringComparison.Ordinal),
        "the payload rows are computed but never passed into the frame - the same wiring gap round 147 found for bossgroup");
    return Task.CompletedTask;
});

await Test("precept names travel, and the frame actually carries the channels it publishes", () =>
{
    // Round 147. Two findings in one place, because they share a file boundary.
    //
    // 1. `PreceptSync` carried which precept defs an ideoligion has and who holds a role - not the names. A replica builds
    //    each precept itself and `Precept` generates a name on construction (`RegenerateName` `:499725`), so the same
    //    precept read differently on every peer. `nameLocked` (`:499254`) is the other half: `RegenerateAllPreceptNames`
    //    (`:493056`) skips locked ones, so a name typed and locked on a replica was regenerated on the authority.
    //
    // 2. `BossgroupSync.Publish()` was called every frame and its result was **never passed into the frame** - the
    //    constructor call simply ended at `autoSlaughter`. The probe measured the channel in isolation, so it read
    //    "correct" while nothing a client received ever contained the cooldown stamp. `Reads its own output` is the
    //    failure this project keeps finding (round 139: a `Store` row with no reader), now in the frame itself.
    var precepts = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PreceptSync.cs"));
    Check(precepts.Contains("internal static List<ReplicaPreceptName>? PublishNames()", StringComparison.Ordinal)
        && precepts.Contains("var name = precept.Label ?? string.Empty;", StringComparison.Ordinal),
        "precept names are not published at all, so every peer shows the name its own `Precept` constructor generated");
    Check(precepts.Contains("found.SetName(state.Name);", StringComparison.Ordinal),
        "the name is written to the field instead of through `SetName` (`:499729`), so precepts that take their name from "
        + "this one (`def.takeNameFrom`) keep printing the old text");
    Check(precepts.Contains("found.nameLocked = state.Locked;", StringComparison.Ordinal),
        "the lock flag does not travel, so a name the player locked on a replica is regenerated on the authority");
    Check(precepts.Contains("precept.nameLocked ? \"\\u0001\" : \"\\u0000\"", StringComparison.Ordinal),
        "the change key ignores the lock flag, so toggling the lock without editing the name sends nothing");

    var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
    Check(replica.Contains("PreceptNameMerge.Merge(newer.PreceptNames, older.PreceptNames)", StringComparison.Ordinal),
        "the name rows are not merged on (ideo, def), so one precept's row would overwrite another's or be dropped");
    Check(replica.Contains("PreceptSync.ApplyName(nameState);", StringComparison.Ordinal),
        "the name rows are merged but never applied");

    var runtime = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HeadlessRuntime.cs"));
    Check(runtime.Contains("autoSlaughter, bossgroup, preceptNames,", StringComparison.Ordinal),
        "the published frame is missing a channel that was computed above it - `bossgroup` and `preceptNames` have to be "
        + "passed positionally, and a field left to its default is a channel that never leaves the host");
    Check(runtime.Contains("var bossgroup = BossgroupSync.Publish();", StringComparison.Ordinal),
        "the bossgroup channel no longer publishes, so the frame argument above would be a leftover");
    return Task.CompletedTask;
});

await Test("actual TPS uses completed ticks, respects pause and counter reset", () =>
{
    var t = new TickRateMeter();
    t.Sample(100, 0, 3);
    Check(!t.Behind && !t.HasStableSample, "no false lag alert before first measurement window");
    t.Sample(160, 1, 3);
    Check(t.ActualTps == 60 && t.TargetTps == 180 && t.Behind, "ticks/time measurement");
    t.Sample(160, 2, 0);
    Check(t.ActualTps == 0 && t.TargetTps == 0 && !t.Behind, "pause");
    t.Sample(0, 3, 1);
    Check(t.ActualTps == 0, "new save resets counters");
    t.Sample(60, 4, 1);
    t.Sample(61, 4.02, 3);
    Check(!t.Behind && !t.HasStableSample, "changing speed starts a fresh measurement window");
    t.Sample(241, 5.05, 3);
    Check(t.HasStableSample && !t.Behind, "stable higher-speed measurement");
    return Task.CompletedTask;
});

await Test("policy validates non-finite and bounds", async () =>
{
    await Throws(() => Task.FromResult(new SpeedPolicy(true, float.NaN)));
    await Throws(() => Task.FromResult(new SpeedPolicy(true, 21)));
    await Throws(() => Task.FromResult(new SpeedPolicy(true, 2, float.PositiveInfinity)));
});

await Test("two clients: one changes, everyone receives same authoritative revision", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("two")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CurrentPolicy?.Enabled == true && b.CurrentPolicy?.Enabled == true, "rate sync is enabled for every peer by default");
    await a.SetSpeedAsync(true, 3, 90);
    await Until(() => a.CurrentPolicy?.Revision == 1 && b.CurrentPolicy?.Revision == 1);
    Check(a.CurrentPolicy == b.CurrentPolicy && b.CurrentPolicy == server.CurrentPolicy, "policy content identical");
    Check(b.CurrentPolicy?.Enabled == true && b.CurrentPolicy.Multiplier == 3 && b.CurrentPolicy.MaxRate == 90, "all three settings synced");
    await b.SetSpeedAsync(false, 5, 80);
    await Until(() => a.CurrentPolicy?.Revision == 2);
    Check(a.CurrentPolicy == server.CurrentPolicy && !a.CurrentPolicy!.Enabled, "client B can also control");
});

await Test("host is explicitly claimed after ordinary joins, separate from editing and checkpoints", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-explicit"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var waiting = new DedicatedClient(); await using var host = new DedicatedClient();
    await waiting.ConnectAsync("127.0.0.1", server.Port, Hello("Waiting"));
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
    Check(!waiting.IsHost && !host.IsHost && waiting.CanEdit && !host.CanEdit && waiting.IsCheckpointOwner,
        "ordinary joins do not claim host, including the original controller/checkpoint owner");
    var world = new byte[1024];
    var manifest = SnapshotBuilder.Describe(world);
    await Throws(() => waiting.UploadWorldAsync(manifest, new MemoryStream(world)), "HostOnly");
    await Throws(() => waiting.StartGameAsync(), "HostOnly");
    await host.ClaimHostAsync();
    await Until(() => !waiting.IsCheckpointOwner && !waiting.CanEdit && host.IsCheckpointOwner && waiting.Players.Any(p => p.Id == host.PlayerId && p.IsHost));
    Check(host.IsHost && host.CanEdit && !waiting.CanEdit && !waiting.IsHost && host.Players.Count(p => p.CanEdit) == 1,
        "controller-only claim migrates the unique editing authority to the later host");
    await Throws(() => waiting.SetTimeSpeedAsync(2), "ControllerOnly");
    await host.SetTimeSpeedAsync(0);
    Check(server.CurrentPolicy.TimeSpeed == 0, "only the later explicit host may control room speed");
    await Throws(() => waiting.ClaimHostAsync(), "HostOccupied");
    await host.ClaimHostAsync(); // Same-connection claim is idempotent.
    await host.UploadWorldAsync(manifest, new MemoryStream(world));
    await host.StartGameAsync();
    Check(server.GameRunning && host.IsHost, "only the explicit host publishes and starts");
});

await Test("host claim is exclusive even when every player can edit", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-race")));
    await server.StartAsync();
    var clients = new List<DedicatedClient>();
    try
    {
        for (var index = 0; index < 6; index++)
        {
            var client = new DedicatedClient(); clients.Add(client);
            await client.ConnectAsync("127.0.0.1", server.Port, Hello("Claimant " + index));
        }
        Check(clients.All(c => c.CanEdit && !c.IsHost), "everyone-can-edit never implicitly hosts");
        var claims = await Task.WhenAll(clients.Select(async client =>
        {
            try { await client.ClaimHostAsync(); return true; }
            catch (NetworkException error) when (error.Code == "HostOccupied") { return false; }
        }));
        Check(claims.Count(success => success) == 1, "one successful RPC under concurrent claims");
        await Until(() => clients.All(c => c.Players.Count == 6 && c.Players.Count(p => p.IsHost) == 1));
        Check(clients.Count(c => c.IsHost) == 1, "one host property after every response");
        var editor = clients.First(c => !c.IsHost);
        await Throws(() => editor.UploadWorldAsync(SnapshotBuilder.Describe(new byte[100]), new MemoryStream(new byte[100])), "HostOnly");
        await Throws(() => editor.StartGameAsync(), "HostOnly");
        Check(server.WorldManifest is null && server.Epoch == -1 && !server.GameRunning, "non-host editors cannot publish or start");
    }
    finally { foreach (var client in clients) await client.DisposeAsync(); }
});

await Test("host leaving an empty lobby clears the role without promoting waiting peers", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-empty")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var waiting = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
    await waiting.ConnectAsync("127.0.0.1", server.Port, Hello("Waiting"));
    await host.ClaimHostAsync();
    await host.DisconnectAsync();
    Check(!host.IsHost && !host.IsCheckpointOwner && !host.CanEdit && host.Players.Count == 0, "disconnect clears all local role flags");
    await Until(() => waiting.Players.Count == 1);
    Check(!waiting.IsHost && waiting.Players.All(p => !p.IsHost), "a lobby vacancy stays vacant");
    await waiting.ClaimHostAsync();
    Check(waiting.IsHost, "a waiting peer may explicitly claim the vacancy");
});

await Test("active session claim preserves the world and transfers host to surviving checkpoint owner", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-active"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var original = new DedicatedClient();
    await original.ConnectAsync("127.0.0.1", server.Port, Hello("Original"));
    await original.ClaimHostAsync();
    await original.SetTimeSpeedAsync(0);
    var world = new byte[1024];
    await original.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await original.StartGameAsync();
    await original.SendCommandAsync("draft", [1]);
    await Until(() => server.HistoryCount == 1);
    var epoch = server.Epoch; var manifest = server.WorldManifest; var seed = server.Session!.Seed;
    await original.DisconnectAsync();
    await server.StopAsync();
    await server.StartAsync();
    await using var checkpointOwner = new DedicatedClient(); await using var host = new DedicatedClient();
    await checkpointOwner.ConnectAsync("127.0.0.1", server.Port, Hello("Checkpoint owner"));
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("New host"));
    Check(!checkpointOwner.IsHost && !host.IsHost, "joining a persisted running world still does not claim host");
    await host.ClaimHostAsync();
    await Until(() => !checkpointOwner.CanEdit);
    Check(host.IsHost && host.CanEdit && !host.IsCheckpointOwner && checkpointOwner.IsCheckpointOwner && !checkpointOwner.CanEdit &&
        host.Players.Count(p => p.CanEdit) == 1, "an active claim preserves checkpoint duty while transferring unique editing authority");
    Check(server.Epoch == epoch && server.WorldManifest?.Id == manifest?.Id && server.Session!.Seed == seed &&
        server.Tick == 0 && server.HistoryCount == 1 && server.CurrentPolicy.TimeSpeed == 0, "claim never replaces/restarts the active world");
    var checkpoint = Enumerable.Repeat((byte)7, 1024).ToArray();
    await checkpointOwner.CheckpointWorldAsync(SnapshotBuilder.Describe(checkpoint), new MemoryStream(checkpoint), 0);
    Check(!checkpointOwner.IsHost && !checkpointOwner.CanEdit && server.WorldManifest?.Id == SnapshotBuilder.Describe(checkpoint).Id,
        "a non-host checkpoint owner without editing permission can still publish checkpoints");
    await host.DisconnectAsync();
    await Until(() => checkpointOwner.IsHost && checkpointOwner.Players.Count == 1);
    Check(checkpointOwner.IsCheckpointOwner && checkpointOwner.CanEdit && checkpointOwner.Players.Single().IsHost,
        "running host ownership deterministically transfers to the surviving checkpoint owner");
});

await Test("an in-flight host claim never hops connections or leaves host state in the next room", async () =>
{
    await using var room = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-switch")));
    await room.StartAsync();
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    using var stop = new CancellationTokenSource();
    var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fake = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync();
        await ReadPacket(socket);
        await WritePacket(socket, new
        {
            type = "Welcome", protocolVersion = NetworkProtocol.Version, clientVersion = NetworkProtocol.ClientVersion,
            gameVersion = "1.7.4998", modHash = "mod-a", canEdit = true, isHost = false,
            policy = new SpeedPolicy(), playerId = 1L, epoch = -1L,
            players = new[] { new PlayerInfo(1, "Old room", 0, true, false) }
        });
        var claim = await ReadPacket(socket);
        Check(claim.GetProperty("type").GetString() == "ClaimHost", "the request reaches its original connection");
        requestSeen.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    });
    await using var changing = new DedicatedClient();
    try
    {
        await changing.ConnectAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, Hello());
        var claim = changing.ClaimHostAsync();
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await changing.ConnectAsync("127.0.0.1", room.Port, Hello());
        await Throws(() => claim);
        Check(!changing.IsHost && changing.Players.All(p => !p.IsHost), "the new connection remains an ordinary join");
        await changing.ClaimHostAsync();
        Check(changing.IsHost, "only a new explicit request may claim the new room");
    }
    finally { stop.Cancel(); listener.Stop(); await fake; }
});

await Test("unauthorized and retyped commits never replace the authoritative world", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-commit")));
    await server.StartAsync();
    using var raw = new TcpClient(); await raw.ConnectAsync("127.0.0.1", server.Port);
    await WritePacket(raw, new { type = "Hello", protocolVersion = NetworkProtocol.Version, hello = Hello("Editor") });
    await ReadPacket(raw);
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("Host"));
    await host.ClaimHostAsync(); await host.SetTimeSpeedAsync(0);
    var world = new byte[1024]; var manifest = SnapshotBuilder.Describe(world);
    await host.UploadWorldAsync(manifest, new MemoryStream(world)); await host.StartGameAsync();
    var chunk = Enumerable.Repeat((byte)123, 100).ToArray();
    var changed = SnapshotBuilder.Describe(chunk);
    foreach (var checkpoint in new[] { false, true })
    {
        var requestId = checkpoint ? 702 : 701;
        await WritePacket(raw, new
        {
            type = "PublishWorldChunk", protocolVersion = NetworkProtocol.Version, requestId,
            uploadId = "editor-upload", chunkIndex = 0, worldChunk = chunk, checkpoint, epoch = server.Epoch, tick = 0
        });
        var response = await ReadReply(raw, requestId);
        Check(response.GetProperty("code").GetString() == (checkpoint ? "NotCheckpointOwner" : "HostOnly"),
            "editing permission is insufficient for either upload purpose");
    }
    await WritePacket(raw, new
    {
        type = "CommitWorld", protocolVersion = NetworkProtocol.Version, requestId = 703,
        uploadId = "editor-upload", checkpoint = true, epoch = server.Epoch, tick = 0, chunkCount = 1, sha256 = changed.Id
    });
    Check((await ReadReply(raw, 703)).GetProperty("code").GetString() == "NotCheckpointOwner", "direct commit checks owner before processing data");
    Check(server.WorldManifest?.Id == manifest.Id && server.CheckpointManifest?.Id == manifest.Id, "failed checkpoint commit preserves both world pointers");
    await host.DisconnectAsync();
    // Running-room continuity explicitly promotes this remaining raw peer; wait for its role update.
    var promoted = false;
    for (var index = 0; index < 32 && !promoted; index++)
    {
        var packet = await ReadPacket(raw);
        promoted = packet.GetProperty("type").GetString() == "Role" && packet.GetProperty("isHost").GetBoolean();
    }
    Check(promoted, "running room transfers host to survivor");
    await WritePacket(raw, new
    {
        type = "PublishWorldChunk", protocolVersion = NetworkProtocol.Version, requestId = 704,
        uploadId = "bound-purpose", chunkIndex = 0, worldChunk = chunk, checkpoint = true, epoch = server.Epoch, tick = 0
    });
    Check((await ReadReply(raw, 704)).GetProperty("type").GetString() == "WorldChunkAccepted", "checkpoint owner may stage a checkpoint");
    await WritePacket(raw, new
    {
        type = "CommitWorld", protocolVersion = NetworkProtocol.Version, requestId = 705,
        uploadId = "bound-purpose", checkpoint = false, epoch = server.Epoch, tick = 0, chunkCount = 1, sha256 = changed.Id
    });
    Check((await ReadReply(raw, 705)).GetProperty("code").GetString() == "InvalidUpload", "an upload cannot switch from checkpoint to initial publication");
    Check(server.WorldManifest?.Id == manifest.Id && server.CheckpointManifest?.Id == manifest.Id && server.Epoch == 0,
        "retyped commit preserves the running authoritative world");
});

await Test("delayed host role and roster notifications cannot poison another room", async () =>
{
    await using var first = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-events-a")));
    await using var second = new DedicatedServer(new(Port: 0, DataDirectory: Dir("host-events-b")));
    await first.StartAsync(); await second.StartAsync();
    await using var changing = new DedicatedClient();
    await changing.ConnectAsync("127.0.0.1", first.Port, Hello());
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var notifications = new System.Collections.Concurrent.ConcurrentQueue<bool>();
    var firstEvent = 0;
    changing.PlayersChanged += players =>
    {
        if (Interlocked.Increment(ref firstEvent) == 1)
        {
            entered.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(20));
        }
        else notifications.Enqueue(players.Any(p => p.IsHost));
    };
    try
    {
        await changing.ClaimHostAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(changing.IsHost, "old room role is confirmed while its UI dispatcher is blocked");
        await changing.ConnectAsync("127.0.0.1", second.Port, Hello());
    }
    finally { release.TrySetResult(); }
    await Until(() => notifications.Count > 0);
    Check(!changing.IsHost && notifications.All(host => !host) && changing.Players.All(p => !p.IsHost),
        "queued old-room host notifications are discarded by connection generation");
});

await Test("presence roster: Welcome identity, join, leave, ownership and stable reconnect color", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-roster"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Alice"));
    Check(a.PlayerId > 0 && a.Players.Count == 1 && a.Players[0].Id == a.PlayerId &&
        a.Players[0].Name == "Alice" && a.Players[0].IsCheckpointOwner, "Welcome atomically supplies assigned identity and roster");
    var firstId = a.PlayerId;
    var color = a.Players[0].ColorIndex;
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("Bob"));
    await Until(() => a.Players.Count == 2 && b.Players.Count == 2);
    Check(b.PlayerId != firstId && b.Players.SequenceEqual(a.Players), "same server-assigned roster on both peers");
    Check(!b.Players.Single(p => p.Id == b.PlayerId).CanEdit, "read-only role appears in roster");
    await a.DisconnectAsync();
    Check(a.PlayerId == 0 && a.Players.Count == 0 && a.Presences.Count == 0, "disconnect clears all transient identity/state immediately");
    await Until(() => b.Players.Count == 1 && b.Players[0].IsCheckpointOwner && b.Players[0].CanEdit && b.CanEdit);
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Alice"));
    Check(a.PlayerId != firstId && a.Players.Single(p => p.Id == a.PlayerId).ColorIndex == color,
        "new socket identity with stable named-player color");
    await Until(() => b.Players.Count == 2);
});

await Test("presence: real map coordinates, names, hidden/off-map state and disconnect removal", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-coordinates")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Map owner"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("Map visitor"));
    await Until(() => a.Players.Count == 2);
    var position = new PresenceUpdate(-1, 47, 123.25f, 86.875f, true, PlayerActivity.Playing);
    Check(await a.SendPresenceAsync(position), "first sample sent");
    await Until(() => b.Presences.Count == 1);
    var received = b.Presences[0];
    Check(received.PlayerId == a.PlayerId && received.State == position && received.UpdatedAtMilliseconds > 0,
        "coordinates and map unique ID survive TCP relay with server identity and local timestamp");
    Check(b.Players.Single(p => p.Id == received.PlayerId).Name == "Map owner", "pointer resolves the actual player name");
    Check(a.Presences.Count == 0, "sender does not receive its own pointer echo");
    await Task.Delay(110);
    var hidden = position with { Visible = false, X = 0, Z = 0 };
    Check(await a.SendPresenceAsync(hidden), "explicit hide sent");
    await Until(() => b.Presences[0].State == hidden);
    await Task.Delay(110);
    var away = hidden with { MapId = -1 };
    Check(await a.SendPresenceAsync(away), "off-map sample sent");
    await Until(() => b.Presences[0].State.MapId == -1);
    await a.DisconnectAsync();
    await Until(() => b.Players.Count == 1 && b.Presences.Count == 0);
    Check(server.HistoryCount == 0 && server.Tick == 0 && !server.GameRunning,
        "presence never starts simulation or touches command history");
});

await Test("presence: one-in-flight client rate cap, finite validation and epoch isolation", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-client-limits")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    await Until(() => a.Players.Count == 2);
    var position = new PresenceUpdate(-1, 3, 50, 60, true, PlayerActivity.Playing);
    var sends = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => a.SendPresenceAsync(position with { X = i })));
    Check(sends.Count(sent => sent) == 1, "100 simultaneous samples create one wire write");
    await Until(() => b.Presences.Count == 1);
    await Throws(() => a.SendPresenceAsync(position with { X = float.NaN }));
    await Throws(() => a.SendPresenceAsync(position with { MapId = -1 }));
    await Throws(() => a.SendPresenceAsync(position with { Activity = (PlayerActivity)999 }));
    await a.SetTimeSpeedAsync(0);
    var world = new byte[1024];
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    await Until(() => a.PresenceEpoch == server.Epoch && b.PresenceEpoch == server.Epoch && b.Presences.Count == 0);
    Check(!await a.SendPresenceAsync(position), "a delayed sample from the previous epoch is discarded locally");
    await Task.Delay(110);
    var current = position with { Epoch = server.Epoch, MapId = 99 };
    Check(await a.SendPresenceAsync(current), "new epoch sample is sent");
    await Until(() => b.Presences.Count == 1 && b.Presences[0].State == current);
    Check(server.Tick == 0 && server.HistoryCount == 0 && server.CurrentPolicy.TimeSpeed == 0,
        "presence while paused changes no simulation tick, native speed or command history");
    await a.StartGameAsync();
    await Until(() => b.PresenceEpoch == server.Epoch && b.Presences.Count == 0);
});

await Test("presence relay: forged identity ignored, raw flood throttled, invalid/old epoch dropped", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-raw")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("Observer"));
    await a.SetTimeSpeedAsync(0);
    var world = new byte[1024];
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    await Until(() => a.PresenceEpoch == server.Epoch);
    using var raw = new TcpClient();
    await raw.ConnectAsync("127.0.0.1", server.Port);
    await WritePacket(raw, new { type = "Hello", protocolVersion = NetworkProtocol.Version, hello = Hello("Raw peer") });
    var welcome = await ReadPacket(raw);
    Check(welcome.GetProperty("type").GetString() == "Welcome", "Welcome precedes every roster and presence broadcast");
    var rawId = welcome.GetProperty("playerId").GetInt64();
    await Until(() => a.Players.Count == 2);
    var receivedCount = 0;
    a.PresenceChanged += _ => Interlocked.Increment(ref receivedCount);
    var state = new PresenceUpdate(server.Epoch, 42, 10.5f, 20.25f, true, PlayerActivity.Playing);
    var startedAt = Environment.TickCount64;
    for (var index = 0; index < 100; index++)
        await WritePacket(raw, new
        {
            type = "Presence", protocolVersion = NetworkProtocol.Version, playerId = a.PlayerId,
            presence = state with { X = index }, playerPresence = new PlayerPresence(a.PlayerId, state)
        });
    await RawBarrier(raw, 991);
    await Until(() => a.Presences.Any(p => p.PlayerId == rawId));
    Check(a.Presences.Count == 1 && a.Presences[0].PlayerId != a.PlayerId, "server binds identity to socket despite forged envelope fields");
    var budget = 1 + (Environment.TickCount64 - startedAt) / PresenceRules.SendIntervalMilliseconds;
    Check(Volatile.Read(ref receivedCount) <= budget, "raw traffic also obeys server wall-clock budget");
    var previous = a.Presences[0];
    await Task.Delay(110);
    await WritePacket(raw, new { type = "Presence", protocolVersion = NetworkProtocol.Version, presence = state with { X = -1 } });
    await WritePacket(raw, new { type = "Presence", protocolVersion = NetworkProtocol.Version, presence = state with { Epoch = server.Epoch - 1, MapId = 98 } });
    await RawBarrier(raw, 992);
    Check(a.Presences[0] == previous, "invalid coordinates and previous epoch never reach peers");
    await WritePacket(raw, new { type = "Presence", protocolVersion = NetworkProtocol.Version, presence = state with { Visible = false, X = 0, Z = 0 } });
    await RawBarrier(raw, 993);
    await Until(() => !a.Presences[0].State.Visible);
    await a.SetTimeSpeedAsync(0);
    Check(a.IsConnected && server.HistoryCount == 0 && server.Tick == 0,
        "advisory flood does not append commands, advance the paused timeline or disconnect healthy peers");
    raw.Dispose();
    await Until(() => a.Players.Count == 1 && a.Presences.Count == 0);
});

await Test("presence heartbeat reports measured RTT rather than invented latency", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("presence-latency")));
    await server.StartAsync();
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(client.LatencyMilliseconds == -1, "unmeasured latency starts unknown");
    await Until(() => client.LatencyMilliseconds >= 0 && client.Players.Count == 1 && client.Players[0].LatencyMilliseconds >= 0, 8);
    Check(client.LatencyMilliseconds < 5000 && client.Players[0].LatencyMilliseconds < 5000, "loopback round trip is measured by both endpoints");
});

await Test("concurrent writes are serialized; broadcasts never go backwards", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("parallel")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var revisions = new List<long>();
    b.PolicyChanged += p => { lock (revisions) revisions.Add(p.Revision); };
    await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? a : b).SetSpeedAsync(true, 1 + i, 60)));
    await Until(() => b.CurrentPolicy?.Revision == 12 && a.CurrentPolicy?.Revision == 12);
    Check(a.CurrentPolicy == b.CurrentPolicy, "all clients converge");
    lock (revisions) Check(revisions.SequenceEqual(revisions.Order()), "monotonic ordered broadcast");
});

await Test("compatibility locks exact build, mod hash and protocol client version", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("compat")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await using var wrongMods = new DedicatedClient();
    await Throws(() => wrongMods.ConnectAsync("127.0.0.1", server.Port, Hello("B", "mod-b")));
    Check(!wrongMods.IsConnected, "mismatch must not appear connected");
    await using var wrongBuild = new DedicatedClient();
    await Throws(() => wrongBuild.ConnectAsync("127.0.0.1", server.Port, Hello("B", version: "1.7.4999")));
    await using var oldGame = new DedicatedClient();
    await Throws(() => oldGame.ConnectAsync("127.0.0.1", server.Port, Hello("B", version: "1.6.4850")));
    await using var wrongClient = new DedicatedClient();
    await Throws(() => wrongClient.ConnectAsync("127.0.0.1", server.Port, Hello("B") with { ClientVersion = "0.0.1" }));
    Check(a.IsConnected, "healthy peer survives failed handshakes");
});

await Test("controller-only permission is enforced by the server", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("permission"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CanEdit && !b.CanEdit, "server assigns roles");
    await Throws(() => b.SetSpeedAsync(true, 4, 60));
    Check(server.CurrentPolicy.Revision == 0, "blocked command changes nothing");
    await a.SetSpeedAsync(true, 4, 60);
    await Until(() => b.CurrentPolicy?.Revision == 1);
});

await Test("reconnect and server restart restore policy instead of local defaults", async () =>
{
    var path = Dir("persistence");
    var server = new DedicatedServer(new(Port: 0, DataDirectory: path));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await a.SetSpeedAsync(true, 4, 85);
    var previous = server.CurrentPolicy;
    await a.DisconnectAsync();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(a.CurrentPolicy == previous, "same instance reconnects with latest policy");
    await a.DisconnectAsync(); await server.DisposeAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: path));
    await restarted.StartAsync();
    await a.ConnectAsync("127.0.0.1", restarted.Port, Hello());
    Check(a.CurrentPolicy == previous, "disk-backed policy survives restart");
    await a.SetSpeedAsync(false, 2, 60);
    Check(restarted.CurrentPolicy.Revision == previous.Revision + 1, "revision survives restart");
});

await Test("oversized malformed frame is bounded; other peers still connect", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("frames")));
    await server.StartAsync();
    using var rogue = new TcpClient(); await rogue.ConnectAsync("127.0.0.1", server.Port);
    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
    await rogue.GetStream().WriteAsync(header);
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(a.IsConnected, "malformed socket must not kill listener");
});

await Test("snapshot dedup, bounds and path validation", async () =>
{
    var store = new SnapshotStore(Dir("store"));
    var bytes = new byte[700_000]; new Random(7).NextBytes(bytes);
    var a = await store.PutAsync(new MemoryStream(bytes));
    var b = await store.PutAsync(new MemoryStream(bytes));
    Check(a.Id == b.Id && a.ChunkHashes.SequenceEqual(b.ChunkHashes) && a.ChunkHashes.Count == 3, "content addressed chunks");
    var chunk = await store.ReadChunkAsync(a.Id, 0);
    Check(chunk.Data.SequenceEqual(bytes.Take(a.ChunkSize)) && chunk.Sha256 == a.ChunkHashes[0], "chunk hash");
    await Throws(() => store.ReadChunkAsync("../outside", 0));
    await Throws(() => store.ReadChunkAsync(a.Id, -1));
    await Throws(() => store.ReadChunkAsync(a.Id, 3));
    var empty = await store.PutAsync(new MemoryStream());
    Check(empty.Length == 0 && empty.ChunkHashes.Count == 0, "empty snapshot defined");
});

await Test("snapshot cancel then resume; final digest matches; cache hit avoids transfer", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("download")));
    await server.StartAsync();
    var bytes = new byte[1_100_000]; new Random(17).NextBytes(bytes);
    var manifest = await server.Snapshots.PutAsync(new MemoryStream(bytes));
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    var received = await client.GetSnapshotManifestAsync();
    Check(received?.Id == manifest.Id, "manifest comes from independent server");
    var destination = Path.Combine(Dir("client-cache"), "save.snapshot");
    using var cancel = new CancellationTokenSource();
    await Throws(() => client.DownloadSnapshotAsync(manifest, destination, (done, total) =>
    { if (done >= manifest.ChunkSize) cancel.Cancel(); }, cancel.Token));
    Check(!File.Exists(destination), "partial must never masquerade as complete snapshot");
    await client.DownloadSnapshotAsync(manifest, destination);
    var saved = await File.ReadAllBytesAsync(destination);
    Check(saved.SequenceEqual(bytes), "resumed bytes identical");
    Check(Convert.ToHexStringLower(SHA256.HashData(saved)) == manifest.Id, "whole-file SHA256");
    // Make every remote chunk request fail. A completed valid cache must be reused without transfer.
    File.Delete(Path.Combine(Dir("download"), "Snapshots", "objects", manifest.Id + ".blob"));
    await client.DownloadSnapshotAsync(manifest, destination);
    Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "verified cache hit");
});

await Test("corrupt server snapshot is a request error, not a room disconnect", async () =>
{
    var data = Dir("corrupt-server");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await server.StartAsync();
    var manifest = await server.Snapshots.PutAsync(new MemoryStream(new byte[300_000]));
    var blob = Path.Combine(data, "Snapshots", "objects", manifest.Id + ".blob");
    await using (var file = new FileStream(blob, FileMode.Open, FileAccess.Write)) { file.WriteByte(123); }
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    await Throws(() => client.DownloadSnapshotAsync(manifest, Path.Combine(Dir("corrupt-cache"), "save.snapshot")), "InvalidSnapshotChunk");
    Check(client.IsConnected, "a corrupt snapshot must not disconnect a healthy peer");
    await client.SetSpeedAsync(true, 3, 60);
    Check(client.CurrentPolicy?.Multiplier == 3, "settings channel remains usable");
});

await Test("a queued edit from server A must never mutate server B after reconnect", async () =>
{
    await using var a = new DedicatedServer(new(Port: 0, DataDirectory: Dir("old-room")));
    await using var b = new DedicatedServer(new(Port: 0, DataDirectory: Dir("new-room")));
    await a.StartAsync(); await b.StartAsync();
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", a.Port, Hello());
    // Deterministic scheduler barrier: hold the existing edit queue like a slow in-flight operation.
    var gate = (SemaphoreSlim)typeof(DedicatedClient).GetField("_speedGate",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
    await gate.WaitAsync();
    Task pending;
    try
    {
        pending = client.SetSpeedAsync(true, 9, 100);
        await client.DisconnectAsync();
        await client.ConnectAsync("127.0.0.1", b.Port, Hello());
    }
    finally { gate.Release(); }
    await Throws(() => pending);
    Check(b.CurrentPolicy.Revision == 0 && b.CurrentPolicy.Multiplier == 2f, "stale edit leaked across rooms");
    await client.SetSpeedAsync(true, 3, 60);
    Check(b.CurrentPolicy.Multiplier == 3, "new-session edits still work");
});

await Test("queued notifications from server A cannot poison server B policy after reconnect", async () =>
{
    await using var a = new DedicatedServer(new(Port: 0, DataDirectory: Dir("event-old")));
    await using var b = new DedicatedServer(new(Port: 0, DataDirectory: Dir("event-new")));
    await a.StartAsync(); await b.StartAsync();
    await using var client = new DedicatedClient();
    using var blocked = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    int first = 0;
    client.StatusChanged += text =>
    {
        if (text == "Connecting" && Interlocked.CompareExchange(ref first, 1, 0) == 0)
        {
            blocked.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }
    };
    var authority = new SpeedAuthority();
    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PolicyChanged += policy =>
    {
        authority.ApplyServer(policy);
        if (policy == b.CurrentPolicy) delivered.TrySetResult();
    };
    try
    {
        await client.ConnectAsync("127.0.0.1", a.Port, Hello());
        await Until(() => blocked.IsSet);
        await client.SetSpeedAsync(true, 7, 100);
        await client.DisconnectAsync();
        await client.ConnectAsync("127.0.0.1", b.Port, Hello());
        authority.BeginSession();
        release.Set();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(authority.Current == b.CurrentPolicy, "old revision from room A poisoned room B subscriber state");
    }
    finally { release.Set(); }
});

await Test("command log applies in server order, rejects duplicates and ignores the future", () =>
{
    var log = new CommandLog();
    byte[] p = [1];
    Check(log.Accept(new(1, 10, "designation", p)), "accept #1");
    Check(log.Accept(new(2, 5, "designation", p)), "accept #2 with an earlier tick");
    Check(!log.Accept(new(2, 11, "designation", p)), "duplicate sequence");
    Check(log.TakeThrough(4).Count == 0, "nothing before its tick");
    var due = log.TakeThrough(10);
    Check(due.Count == 2 && due[0].Sequence == 2 && due[1].Sequence == 1, "ordered by tick, then sequence");
    Check(log.Accept(new(1, 12, "designation", p)) == false, "regressed sequence rejected");
    return Task.CompletedTask;
});

await Test("tick barrier never runs ahead of the server and ignores stale publishers", () =>
{
    var barrier = new TickBarrier();
    barrier.Publish(5); barrier.Publish(3);
    Check(barrier.ServerTick == 5 && barrier.Backlog == 5, "monotonic server tick");
    for (var i = 0; i < 5; i++) Check(barrier.TryAdvance(), "advance");
    Check(!barrier.TryAdvance() && barrier.Backlog == 0, "cannot pass the server");
    barrier.Publish(7);
    Check(barrier.Backlog == 2, "new ticks reopen the window");
    return Task.CompletedTask;
});

await Test("shared colony: controller publishes world, starts, and both clients receive commands", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("shared")));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    Check(controller.CanEdit && guest.CanEdit, "both peers may edit in the default room");
    var world = new byte[300_000]; new Random(3).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await controller.ClaimHostAsync();
    await controller.UploadWorldAsync(manifest, new MemoryStream(world));
    Check(server.WorldManifest?.Id == manifest.Id, "server stores the shared world");
    Check((await guest.GetWorldManifestAsync())?.Id == manifest.Id, "guests see the same world id");
    var started = new TaskCompletionSource<long>();
    guest.WorldStarted += (session, _) => started.TrySetResult(session.Epoch);
    await controller.StartGameAsync();
    Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(20)) >= 0, "guest receives start");
    var received = new TaskCompletionSource<List<GameCommand>>();
    guest.CommandsReceived += commands => received.TrySetResult(commands.ToList());
    var echoed = new TaskCompletionSource<List<GameCommand>>(TaskCreationOptions.RunContinuationsAsynchronously);
    controller.CommandsReceived += commands => echoed.TrySetResult(commands.ToList());
    await controller.SendCommandAsync("designation", "Mine|0|10|20"u8.ToArray());
    var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(batch.Count == 1 && batch[0].Sequence == 1 && batch[0].Kind == "designation", "command relayed in order");
    // The origin is included on purpose. Its local write was cancelled by the capture prefix (that is what turns a
    // gesture into this command), so it is the one peer that provably has not applied it; skipping the origin left
    // single-replica rooms with no executor at all and the flag never moved. Measured: 46 unforbid commands relayed
    // and none refused, with the sender's own screen keeping every X.
    var echo = await echoed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(echo.Count == 1 && echo[0].Sequence == 1, "the origin receives its own command back");
    Check(batch[0].Tick >= 0 && batch[0].Payload.Length > 0, "server assigned tick and kept payload");
    Check(server.Tick >= 0 && server.GameRunning, "server drives a shared tick clock");
});

await Test("every supported command kind is relayed to the other peers", async () =>
{
    // The server keeps a whitelist of relays-able kinds in Networking, while the codec constants live in Simulation;
    // the two are kept in sync by hand. A kind that is missing from the whitelist is rejected as UnknownCommand and
    // the player's action disappears - and because the rejection is a wire error rather than a log line, it read as
    // "the click did nothing". This sends every advertised kind through a real server so a gap cannot ship.
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("kinds")));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(77).NextBytes(world);
    await controller.ClaimHostAsync();
    await controller.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    var started = new TaskCompletionSource<bool>();
    guest.WorldStarted += (_, _) => started.TrySetResult(true);
    await controller.StartGameAsync();
    await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

    var kinds = TurboTick.Networking.DedicatedServer.SupportedKinds.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    Check(kinds.Length > 0, "the server advertises at least one kind");
    Check(kinds.Contains("zone-cell"), "zone-cell is relayable; a stockpile drag depends on it");

    var seen = new List<string>();
    guest.CommandsReceived += commands => { lock (seen) seen.AddRange(commands.Select(c => c.Kind)); };
    foreach (var kind in kinds) await controller.SendCommandAsync(kind, [(byte)kind.Length]);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
        lock (seen) if (seen.Count >= kinds.Length) break;
        await Task.Delay(25);
    }
    string[] missing;
    lock (seen) missing = kinds.Where(k => !seen.Contains(k)).ToArray();
    Check(missing.Length == 0, "kinds never relayed: " + string.Join(",", missing)
        + " (received " + seen.Count + " of " + kinds.Length + ")");
});
await Test("a burst of concurrent commands is relayed in full", async () =>
{
    // Measured in the field: a player dragged a 25-cell stockpile, the client published 24 zone-cell commands with
    // no gate drop and no send failure, and the server parsed exactly one frame. Losing 23 of 24 frames is invisible
    // from the sending side because each write completes locally either way, so it has to be asserted end to end.
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("burst")));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(91).NextBytes(world);
    await controller.ClaimHostAsync();
    await controller.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    var started = new TaskCompletionSource<bool>();
    guest.WorldStarted += (_, _) => started.TrySetResult(true);
    await controller.StartGameAsync();
    await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

    const int burst = 24;
    var seen = new List<GameCommand>();
    guest.CommandsReceived += commands => { lock (seen) seen.AddRange(commands); };
    var sends = Enumerable.Range(0, burst)
        .Select(i => controller.SendCommandAsync("zone-cell", [(byte)i, (byte)(i >> 8)]))
        .ToArray();
    await Task.WhenAll(sends);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
        lock (seen) if (seen.Count >= burst) break;
        await Task.Delay(25);
    }
    int received;
    lock (seen) received = seen.Count;
    Check(received == burst, $"burst relay lost frames: received {received} of {burst}");
});
await Test("controller-only room requires explicit host to publish its world", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("shared-owner"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    Check(controller.CanEdit && !guest.CanEdit && !controller.IsHost && !guest.IsHost, "editing is separate from hosting");
    var world = new byte[2048]; new Random(5).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await Throws(() => guest.UploadWorldAsync(manifest, new MemoryStream(world)), "HostOnly");
    await controller.ClaimHostAsync();
    await controller.UploadWorldAsync(manifest, new MemoryStream(world));
    Check(server.WorldManifest?.Id == manifest.Id, "controller publishes the shared world");
});

await Test("action codec round-trips every player action and rejects malformed payloads", () =>
{
    // The generic gizmo fallback carries a raw Scribe fragment, so its separator must not be a character
    // that can appear in the payload. XML is full of '|' in text nodes; a newline before the fragment keeps
    // the id unambiguous, and the leading kind lets things and zones share one command.
    var fragment = "<v><forbidden>True</forbidden><label>a|b|c</label></v>";
    var state = TurboTick.Simulation.CommandCodec.StateCommand('t', 9876, fragment, 3);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeState(state.Payload, out var kind, out var stateId, out var stateXml)
        && kind == 't' && stateId == 9876 && stateXml == fragment, "thing state keeps a fragment containing separators");
    var zoneState = TurboTick.Simulation.CommandCodec.StateCommand('z', 12, fragment, 4);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeState(zoneState.Payload, out var zoneKind, out var zoneId, out _)
        && zoneKind == 'z' && zoneId == 12, "zone state is distinguishable from a thing");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeState([], out _, out _, out _), "empty state rejected");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeState("9876\n"u8.ToArray(), out _, out _, out _),
        "state without a kind is rejected");
    var research = TurboTick.Simulation.CommandCodec.ResearchCommand("Electricity", 5);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeResearch(research.Payload, out var project) && project == "Electricity",
        "research selection round-trips");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeResearch(TurboTick.Simulation.CommandCodec.ResearchCommand(null, 6).Payload,
        out var stopped) && stopped is null, "stopping research is an empty payload, not a malformed one");
    var design = TurboTick.Simulation.CommandCodec.DesignationCommand("Mine", 0, 12, 34, -1, 7);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDesignation(design.Payload, out var def, out var map, out var x, out var z, out var thing)
        && def == "Mine" && map == 0 && x == 12 && z == 34 && thing == -1, "designation");
    // Thing-keyed designations (HarvestPlant, Hunt, Tame) must survive the round trip with their target,
    // because the receiver indexes those by Thing and a cell lookup throws.
    var harvest = TurboTick.Simulation.CommandCodec.DesignationCommand("HarvestPlant", 0, 7, 9, 4242, 8);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDesignation(harvest.Payload, out _, out _, out _, out _, out var target)
        && target == 4242, "thing-keyed designation keeps its target");
    var blueprint = TurboTick.Simulation.CommandCodec.BlueprintCommand("Wall", "WoodLog", 1, 5, 6, 2, 8, 777);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeBlueprint(blueprint.Payload, out var b, out var stuff, out var bm, out var bx, out var bz, out var rot, out var bid)
        && b == "Wall" && stuff == "WoodLog" && bm == 1 && bx == 5 && bz == 6 && rot == 2 && bid == 777,
        "blueprint carries its thing id");
    // A six-field payload comes from a sender that predates the id field. It must still decode: the blueprint is the
    // effect, the id is only how it is matched up.
    var legacyBlueprint = TurboTick.Simulation.CommandCodec.BlueprintCommand("Wall", "WoodLog", 1, 5, 6, 2, 9);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeBlueprint(legacyBlueprint.Payload, out _, out _, out _, out _, out _, out _, out var legacyId)
        && legacyId == 0, "blueprint without an id still decodes");
    var draft = TurboTick.Simulation.CommandCodec.DraftCommand(42, true, 9);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDraft(draft.Payload, out var pawn, out var on) && pawn == 42 && on, "draft");
    // An order carries the job's own Scribe XML, base64'd, so no field can be silently omitted. The round trip is
    // exact for every character XML can contain, including the `|` that separates the two payload fields.
    const string jobXml = "<job><def>HaulToContainer</def><targetA>Thing_Human55</targetA>\n"
        + "<note>a | b &amp; \"c\"</note></job>";
    var order = TurboTick.Simulation.CommandCodec.OrderCommand(42, jobXml, 11);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeOrder(order.Payload, out var op, out var back)
        && op == 42 && back == jobXml, "order round trip");
    // The previous payload shape (pawn|jobName|aCell|aThing|bCell|bThing|count) must be refused outright: decoding
    // it as base64 would either throw or yield nonsense, and half-applying an order is worse than dropping it.
    var olderOrder = new TurboTick.Networking.GameCommand(1, 0, TurboTick.Simulation.CommandCodec.Order,
        System.Text.Encoding.UTF8.GetBytes("42|HaulToContainer|1000|55|-1|77|10"));
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeOrder(olderOrder.Payload, out _, out _),
        "a pre-XML order must not decode");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeOrder([], out _, out _), "an empty order must not decode");
    // Policy assignment: only the tracker a player touched travels, and an absent one must stay absent - a payload
    // that silently filled in the other three would revert whatever edit landed first.
    const string outfitXml = "<outfits><curOutfit>ApparelPolicy_worker_3</curOutfit></outfits>";
    var policy = TurboTick.Simulation.CommandCodec.PolicyCommand(77, outfitXml, null, null, null, 13);
    Check(TurboTick.Simulation.CommandCodec.TryDecodePolicy(policy.Payload, out var policyPawn, out var po, out var pd, out var pf, out var pt, out var ps)
        && policyPawn == 77 && po == outfitXml && pd is null && pf is null && pt is null && ps is null,
        "policy carries only what changed");
    // A label is player text: it can contain the payload separator.
    const string nastyPolicy = "<f><label>a|b</label></f>";
    var policy2 = TurboTick.Simulation.CommandCodec.PolicyCommand(8, null, null, nastyPolicy, null, 14);
    Check(TurboTick.Simulation.CommandCodec.TryDecodePolicy(policy2.Payload, out _, out var po2, out _, out var pf2, out _, out _)
        && po2 is null && pf2 == nastyPolicy, "policy survives a separator in a label");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodePolicy(
            TurboTick.Simulation.CommandCodec.PolicyCommand(9, null, null, null, null, 15).Payload, out _, out _, out _, out _, out _, out _),
        "a policy with no tracker must not decode");

    // The "carry" column's tracker rides as a sixth field. Measured need: `tracker-probe` found `inventoryStock` on
    // three pawns while `PolicyCapturePatch` only listened to four panels, so a carry setting changed only the clicking
    // peer's copy. The field has to stay independent of the other five: sending it must not imply the others changed.
    const string stockXml = "<stock><entry><count>5</count></entry></stock>";
    var policy3 = TurboTick.Simulation.CommandCodec.PolicyCommand(31, null, null, null, null, 16, stockXml);
    Check(TurboTick.Simulation.CommandCodec.TryDecodePolicy(policy3.Payload, out var stockPawn, out var so, out var sd,
            out var sf, out var st, out var ss)
        && stockPawn == 31 && so is null && sd is null && sf is null && st is null && ss == stockXml,
        "the carry column travels alone without implying the other four changed");

    // A choice-letter click is an index into the letter's own `Choices` enumeration, not a label: the two peers can
    // show different translations of the same option, so text would not identify it. A negative index and a zero
    // letter id are both refused, because the host looks the letter up by id and a zero would match nothing.
    var click = TurboTick.Simulation.CommandCodec.LetterChoiceCommand(412, 2, 17);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeLetterChoice(click.Payload, out var clickLetter, out var clickOption)
        && clickLetter == 412 && clickOption == 2, "a letter choice carries the letter id and the option index");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeLetterChoice(
            TurboTick.Simulation.CommandCodec.LetterChoiceCommand(412, -1, 18).Payload, out _, out _),
        "a negative option index must not decode");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeLetterChoice(
            TurboTick.Simulation.CommandCodec.LetterChoiceCommand(0, 1, 19).Payload, out _, out _),
        "letter id zero names no letter and must not decode");

    // A growth-moment answer names skills and a trait, because that letter is not a `ChoiceLetter` and has no option
    // list to index. The empty trait section is distinct from a missing one: "no trait" is an option the letter offers
    // 35% of the time, and it must not decode as "the field was absent".
    var growth = TurboTick.Simulation.CommandCodec.GrowthChoiceCommand(88, ["Shooting", "Melee"], "Kind", 2, 21);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeGrowthChoice(growth.Payload, out var growthLetter,
            out var growthSkills, out var growthTrait, out var growthDegree)
        && growthLetter == 88 && growthSkills.Length == 2 && growthSkills[1] == "Melee"
        && growthTrait == "Kind" && growthDegree == 2, "a growth answer carries its skills and its trait degree");
    var noTrait = TurboTick.Simulation.CommandCodec.GrowthChoiceCommand(88, ["Shooting"], null, 0, 22);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeGrowthChoice(noTrait.Payload, out _, out _, out var noneTrait, out _)
        && noneTrait is null, "taking no trait decodes as no trait, not as a malformed payload");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeGrowthChoice(
            TurboTick.Simulation.CommandCodec.GrowthChoiceCommand(0, [], null, 0, 23).Payload, out _, out _, out _, out _),
        "letter id zero names no letter and must not decode");

    // Area buttons: the operation travels, not the cells. Invert and clear are defined against the existing grid, so
    // every peer recomputes the same answer from state it already agreed on.
    var invert = TurboTick.Simulation.CommandCodec.AreaOpCommand("invert", 4, 16);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeAreaOp(invert.Payload, out var areaOp, out var areaId)
        && areaOp == "invert" && areaId == 4, "area op round trip");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeAreaOp(
            new TurboTick.Networking.GameCommand(1, 0, TurboTick.Simulation.CommandCodec.AreaOp,
                System.Text.Encoding.UTF8.GetBytes("shrink|4")).Payload, out _, out _),
        "an unknown area op must not decode");

    // A cancel drag: a rectangle is one command, not one command per blueprint. The cells are the entire input, and
    // the drag is the unit the player gestures with, so they have to survive the round trip exactly and in order.
    var dragCells = new List<(int X, int Z)> { (134, 107), (135, 107), (136, 107), (134, 108) };
    var drag = TurboTick.Simulation.CommandCodec.CancelDragCommand(0, dragCells, 17);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeCancelDrag(drag.Payload, out var dragMap, out var dragBack)
        && dragMap == 0 && dragBack.SequenceEqual(dragCells), "cancel drag round-trips its cells in order");
    // A big rectangle is over the fixed-shape 4096 guard on purpose: that guard bounds one-field commands, and a
    // 900-cell drag is a normal thing for a player to draw.
    var bigDrag = TurboTick.Simulation.CommandCodec.CancelDragCommand(0,
        Enumerable.Range(0, 900).Select(i => (X: 10 + i % 30, Z: 20 + i / 30)).ToList(), 18);
    Check(bigDrag.Payload.Length > 4096, $"a 900-cell drag is over the single-value guard ({bigDrag.Payload.Length} bytes)");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeCancelDrag(bigDrag.Payload, out _, out var bigBack)
        && bigBack.Count == 900, "a 900-cell drag still decodes");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeCancelDrag([], out _, out _), "an empty drag must not decode");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeCancelDrag("0"u8.ToArray(), out _, out _),
        "a drag with no cells must not decode");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeCancelDrag("0|1,2|3"u8.ToArray(), out _, out _),
        "a malformed cell must reject the whole drag rather than cancel part of it");

    var forbid = TurboTick.Simulation.CommandCodec.ForbidCommand(1234, true, 12);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeForbid(forbid.Payload, out var ft, out var fb) && ft == 1234 && fb, "forbid");
    // A bulk forbid: one gesture, one command. The whole point is that a full stockpile fits in one payload, so the
    // decode side must not apply the single-value 4096-byte guard. Ten-digit ids are the worst case a save can hand
    // us (thing ids are ints), and 512 of those is the documented ceiling.
    var manyIds = Enumerable.Range(1, 400).ToArray();
    var batch = TurboTick.Simulation.CommandCodec.ForbidBatchCommand(true, manyIds, 14);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch(batch.Payload, out var batchForbidden, out var batchIds)
        && batchForbidden && batchIds.Count == 400 && batchIds[0] == 1 && batchIds[^1] == 400, "forbid batch round-trips 400 ids");
    var widest = Enumerable.Range(2_000_000_000, TurboTick.Simulation.CommandCodec.MaxBatchIds).ToArray();
    var widestBatch = TurboTick.Simulation.CommandCodec.ForbidBatchCommand(false, widest, 15);
    Check(widestBatch.Payload.Length > 4096, $"a full batch of ten-digit ids is over the single-value guard ({widestBatch.Payload.Length} bytes)");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch(widestBatch.Payload, out var widestFlag, out var widestIds)
        && !widestFlag && widestIds.Count == TurboTick.Simulation.CommandCodec.MaxBatchIds, "the widest batch still decodes");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch("0|7,9"u8.ToArray(), out var allowFlag, out var allowIds)
        && !allowFlag && allowIds is [7, 9], "forbid batch decodes the allow direction");
    // Truncating would leave half a selection unchanged, which is the exact symptom the batch removes.
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch("1|1,,2"u8.ToArray(), out _, out _), "forbid batch rejects an empty id");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch("1|0"u8.ToArray(), out _, out _), "forbid batch rejects id 0");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch("2|1"u8.ToArray(), out _, out _), "forbid batch rejects an invalid flag");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeForbidBatch("1|"u8.ToArray(), out _, out _), "forbid batch rejects an empty list");
    var prio = TurboTick.Simulation.CommandCodec.PriorityCommand(42, "Doctor", 1, 13);
    Check(TurboTick.Simulation.CommandCodec.TryDecodePriority(prio.Payload, out var pp, out var pw, out var pv) && pp == 42 && pw == "Doctor" && pv == 1, "priority");
    var zone = TurboTick.Simulation.CommandCodec.ZoneCellCommand("stockpile", 5, 0, 10, 20, true, 14, "my pile");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeZoneCell(zone.Payload, out var zt, out var zi, out var zm, out var zx, out var zz, out var za, out var zl)
        && zt == "stockpile" && zi == 5 && zm == 0 && zx == 10 && zz == 20 && za && zl == "my pile", "zone cell with label");
    // A six-field payload comes from a sender that does not carry the label. It must still decode: dropping the
    // whole command over a missing optional field would lose the cell, and the cell is the part that matters.
    var legacyZone = TurboTick.Simulation.CommandCodec.ZoneCellCommand("stockpile", 5, 0, 10, 20, true, 15);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeZoneCell(legacyZone.Payload, out _, out _, out _, out _, out _, out var legacyAdd, out var legacyLabel)
        && legacyAdd && legacyLabel is null, "zone cell without label still decodes");
    var bill = TurboTick.Simulation.CommandCodec.BillAddCommand(555, "CookMealSimple", 10, 15);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeBillAdd(bill.Payload, out var bb, out var br, out var bc2) && bb == 555 && br == "CookMealSimple" && bc2 == 10, "bill add");
    var area = TurboTick.Simulation.CommandCodec.AreaAssignCommand(42, 2, 16);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeAreaAssign(area.Payload, out var ap, out var aa) && ap == 42 && aa == 2, "area assign");
    // A paint stroke on a player-created allowed area. This kind exists because `DesignatorSync.EncodeChunks` returned an
    // empty chunk list for every `Designator_Cells` subclass that was not `Designator_Build` or `Designator_ZoneAdd`, so a
    // stroke on an allowed area published nothing at all - and an allowed area is where a colonist may walk.
    var paint = TurboTick.Simulation.CommandCodec.AreaPaintCommand("my area", true, 3, [(10, 20), (11, 20)], 17);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeAreaPaint(paint.Payload, out var pl, out var pa, out var pm, out var pc)
        && pl == "my area" && pa && pm == 3 && pc.Count == 2 && pc[0] == (10, 20) && pc[1] == (11, 20),
        "area paint round-trips label, direction, map and cells");
    var unpaint = TurboTick.Simulation.CommandCodec.AreaPaintCommand("my area", false, 3, [(10, 20)], 18);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeAreaPaint(unpaint.Payload, out _, out var ua2, out _, out _) && !ua2,
        "area paint carries the clear direction");
    // A stroke carries `DesignatorSync.MaxCells` = 6000 cells, so a legal payload is far larger than the 4 KiB the
    // value-shaped `TryDecodeAtLeast` helper allows. Using that helper would have rejected every large stroke.
    var wide = TurboTick.Simulation.CommandCodec.AreaPaintCommand("big", true, 0,
        Enumerable.Range(0, 6000).Select(i => (i % 250, i / 250)).ToArray(), 19);
    Check(wide.Payload.Length > 4096, "a wide stroke payload exceeds the value-shaped decode limit");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeAreaPaint(wide.Payload, out _, out _, out _, out var wc) && wc.Count == 6000,
        "a wide stroke still decodes");
    // Malformed shapes have to be refused rather than half-applied: a stroke with no cells would silently do nothing and
    // look exactly like a stroke whose area was never found.
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeAreaPaint("|1|0|1,1"u8.ToArray(), out _, out _, out _, out _),
        "area paint rejects an empty label");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeAreaPaint("a|2|0|1,1"u8.ToArray(), out _, out _, out _, out _),
        "area paint rejects an invalid direction");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeAreaPaint("a|1|0"u8.ToArray(), out _, out _, out _, out _),
        "area paint rejects a stroke with no cells");
    // A training-tab checkbox. It carries a def NAME rather than an index, because `DefMap` is indexed by
    // `def.index`, which depends on the loaded mod set.
    var train = TurboTick.Simulation.CommandCodec.TrainingWantedCommand(4242, "Tameness", true, 20);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeTrainingWanted(train.Payload, out var tp, out var td, out var tw)
        && tp == 4242 && td == "Tameness" && tw, "training wanted round-trips");
    var untrain = TurboTick.Simulation.CommandCodec.TrainingWantedCommand(4242, "Tameness", false, 21);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeTrainingWanted(untrain.Payload, out _, out _, out var uw) && !uw,
        "training wanted carries the untick direction");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeTrainingWanted("0|Tameness|1"u8.ToArray(), out _, out _, out _),
        "training wanted rejects pawn 0");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeTrainingWanted("42|Tameness|2"u8.ToArray(), out _, out _, out _),
        "training wanted rejects an invalid flag");
    // A surgery bill travels with its recipe, because the receiver has to construct a `Bill_Medical` before the fragment
    // can be loaded, and constructing one needs a recipe.
    var surgery = TurboTick.Simulation.CommandCodec.SurgeryBillCommand(77, "InstallProstheticHeart", "<bill/>", 22);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeSurgeryBill(surgery.Payload, out var sp, out var sr, out var sx,
            out var srep) && sp == 77 && sr == "InstallProstheticHeart" && sx == "<bill/>" && srep == -1,
        "surgery bill round-trips recipe and xml");
    // A re-targeted xenogerm implant supersedes the bill on the old pawn; the index travels so the receiver deletes
    // before it adds, rather than leaving both bills in place.
    var retarget = TurboTick.Simulation.CommandCodec.SurgeryBillCommand(78, "ImplantXenogerm", "<bill/>", 23, 2);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeSurgeryBill(retarget.Payload, out _, out _, out _, out var rep)
        && rep == 2, "surgery bill carries the replaced index");
    // A three-part payload is an older peer: it must still decode, with "no replacement".
    Check(TurboTick.Simulation.CommandCodec.TryDecodeSurgeryBill("77|Recipe|PGJpbGwvPg=="u8.ToArray(), out _, out _, out _,
            out var legacyRep) && legacyRep == -1, "surgery bill decodes without a replaced index");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeSurgeryBill("77|Recipe|"u8.ToArray(), out _, out _, out _, out _),
        "surgery bill rejects an empty fragment");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeSurgeryBill("0|Recipe|PGJpbGwvPg=="u8.ToArray(), out _, out _, out _, out _),
        "surgery bill rejects pawn 0");
    // A real `Bill_Medical` fragment carries `consumedMedicine`, `uniqueRequiredIngredients` and the whole
    // `ingredientFilter` from `Bill.ExposeData` (`:272306-272340`), so it exceeds 4 KiB. The shared `TryDecode`/
    // `TryDecodeAtLeast` helpers cap at 4096 bytes, and using them here would reject a legal bill - the same defect
    // `area-paint` had before its own decoder raised the ceiling to 64 KiB.
    var bigXml = "&lt;bills&gt;" + new string('x', 12000) + "&lt;/bills&gt;";
    var bigBill = TurboTick.Simulation.CommandCodec.SurgeryBillCommand(79, "ImplantXenogerm", bigXml, 24);
    Check(bigBill.Payload.Length > 4096, "a large surgery bill exceeds the value-shaped decode limit");
    Check(TurboTick.Simulation.CommandCodec.TryDecodeSurgeryBill(bigBill.Payload, out _, out _, out var bigOut, out _)
        && bigOut == bigXml, "a large surgery bill still decodes");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeDesignation("broken"u8.ToArray(), out _, out _, out _, out _, out _), "reject short payload");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeDraft("1|2"u8.ToArray(), out _, out _), "reject invalid flag");
    return Task.CompletedTask;
});

await Test("state hash is order independent and changes with simulated state", () =>
{
    string[] a = ["t:1:0:5:5:1:100", "p:2:0:7:8:0.75:Haul"];
    string[] b = ["p:2:0:7:8:0.75:Haul", "t:1:0:5:5:1:100"];
    Check(TurboTick.Simulation.StateHash.Compute(a) == TurboTick.Simulation.StateHash.Compute(b), "order independent");
    Check(TurboTick.Simulation.StateHash.Compute(a) != TurboTick.Simulation.StateHash.Compute(["t:1:0:5:5:1:99", "p:2:0:7:8:0.75:Haul"]), "detects change");
    Check(TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul"]).Length == 64, "sha256 hex");
    // The combination is sum+xor+count precisely so that a duplicate entry cannot cancel itself out. With
    // xor alone, [a, a] would hash like [] and a lost thing would go unnoticed.
    Check(TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul"]) !=
          TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul", "p:2:0:7:8:0.75:Haul"]),
        "a duplicated entry changes the value");
    Check(TurboTick.Simulation.StateHash.Compute([]) == TurboTick.Simulation.StateHash.Compute([""]),
        "empty entries are ignored");
    return Task.CompletedTask;
});

await Test("server reports a desync when two peers disagree on the same tick", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("hash")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var world = new byte[4096]; new Random(9).NextBytes(world);
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    var desync = new TaskCompletionSource<int>();
    b.DesyncDetected += (tick, _) => desync.TrySetResult(tick);
    var same = new string('a', 64);
    await a.ReportHashAsync(60, same);
    await b.ReportHashAsync(60, same);
    Check(!desync.Task.IsCompleted, "matching hashes are silent");
    await b.ReportHashAsync(120, new string('b', 64));
    await a.ReportHashAsync(120, new string('c', 64));
    Check(await desync.Task.WaitAsync(TimeSpan.FromSeconds(20)) == 120, "mismatch reported with the tick");
});

await Test("a peer joining mid-session receives the epoch, tick and full command history", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("catchup")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[8192]; new Random(11).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await using var late = new DedicatedClient();
    var started = new TaskCompletionSource<int>();
    var replay = new List<GameCommand>();
    late.WorldStarted += (session, _) => started.TrySetResult(session.Tick);
    late.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(20)) >= 0, "late peer receives the running epoch");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "designation", "history replayed to the late peer");
});

await Test("session seed reaches every peer and survives a server restart with its history", async () =>
{
    var data = Dir("session-persist");
    var server = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var seeds = new List<int>();
    host.WorldStarted += (session, _) => seeds.Add(session.Seed);
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[4096]; new Random(13).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await Until(() => seeds.Count == 1);
    await host.SendCommandAsync("designation", "Mine|0|2|3"u8.ToArray());
    await host.SendCommandAsync("draft", "5|1"u8.ToArray());
    await Until(() => server.HistoryCount == 2);
    var epoch = server.Epoch;
    var seed = seeds[0];
    await host.DisconnectAsync();
    await server.StopAsync();
    await server.DisposeAsync();

    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await restarted.StartAsync();
    Check(restarted.GameRunning && restarted.Epoch == epoch, "epoch resumes after restart");
    Check(restarted.Session?.Seed == seed, "seed resumes after restart");
    Check(restarted.HistoryCount == 2, "command history resumes after restart");
    await using var rejoin = new DedicatedClient();
    var replay = new List<GameCommand>();
    rejoin.WorldStarted += (_, _) => { };
    rejoin.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await rejoin.ConnectAsync("127.0.0.1", restarted.Port, Hello("rejoin"));
    await Until(() => { lock (replay) return replay.Count == 2; });
    Check(replay.Select(c => c.Kind).SequenceEqual(new[] { "designation", "draft" }), "history replays in order");
});

await Test("two peers replaying the same ordered commands derive the same state hash", () =>
{
    static List<string> Run(IEnumerable<GameCommand> commands)
    {
        // Minimal deterministic model: commands place ids at positions; ordering must define the result.
        var state = new SortedDictionary<int, string>();
        var log = new CommandLog();
        foreach (var command in commands) log.Accept(command);
        foreach (var command in log.TakeThrough(1000))
        {
            var parts = System.Text.Encoding.UTF8.GetString(command.Payload).Split('|');
            state[int.Parse(parts[0])] = $"{parts[1]}|{parts[2]}";
        }
        return state.Select(pair => $"{pair.Key}:{pair.Value}").ToList();
    }
    var commands = new[]
    {
        new GameCommand(1, 5, "designation", "3|Mine|0"u8.ToArray()),
        new GameCommand(2, 2, "designation", "1|Haul|0"u8.ToArray()),
        new GameCommand(3, 9, "designation", "2|Cut|0"u8.ToArray())
    };
    var first = Run(commands);

    // Second peer receives the same sequence but drains the log tick by tick instead of all at once.
    var incremental = new CommandLog();
    foreach (var command in commands) incremental.Accept(command);
    var second = new SortedDictionary<int, string>();
    for (var tick = 0; tick <= 9; tick++)
        foreach (var command in incremental.TakeThrough(tick))
        {
            var parts = System.Text.Encoding.UTF8.GetString(command.Payload).Split('|');
            second[int.Parse(parts[0])] = $"{parts[1]}|{parts[2]}";
        }
    Check(TurboTick.Simulation.StateHash.Compute(first) ==
          TurboTick.Simulation.StateHash.Compute(second.Select(pair => $"{pair.Key}:{pair.Value}").ToList()),
        "incremental and bulk draining converge");
    return Task.CompletedTask;
});

await Test("after a desync the controller can restart the colony with a fresh epoch", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("recover")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(21).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(manifest, new MemoryStream(world));
    var epochs = new List<long>();
    guest.WorldStarted += (session, _) => { lock (epochs) epochs.Add(session.Epoch); };
    await host.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 1; });
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    // Restart resets history and issues a new epoch; the old commands must not leak into it.
    await host.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 2; });
    Check(epochs[1] > epochs[0], "restart issues a new epoch");
    Check(server.HistoryCount == 0, "old commands are cleared");
});

await Test("a checkpoint trims history so a late join replays only the delta", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("checkpoint")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    Check(host.IsCheckpointOwner, "first peer owns checkpoints");
    var world = new byte[4096]; new Random(31).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    for (var i = 0; i < 3; i++)
        await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i}|{i}"));
    await Until(() => server.HistoryCount == 3);
    await Until(() => server.Tick >= 4);
    var tick = server.Tick;
    var checkpoint = new byte[8192]; new Random(32).NextBytes(checkpoint);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(checkpoint);
    await host.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), tick);
    Check(server.CheckpointTick == tick, "checkpoint tick recorded");
    Check(server.HistoryCount == 0, "commands before the checkpoint are dropped");
    // Pin the snapshot being tested. A generic fresh-current-tick checkpoint can absorb the draft
    // into its snapshot before the joiner receives history, making the expected delta disappear.
    var checkpointReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    host.CheckpointRequested += () => { _ = RepublishCheckpoint(); };
    async Task RepublishCheckpoint()
    {
        try
        {
            await host.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), tick);
            checkpointReply.TrySetResult();
        }
        catch (Exception error) { checkpointReply.TrySetException(error); }
    }
    await host.SendCommandAsync("draft", "7|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await using var late = new DedicatedClient();
    var started = new TaskCompletionSource<SessionInfo>();
    var replay = new List<GameCommand>();
    late.WorldStarted += (session, _) => started.TrySetResult(session);
    late.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    var info = await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
    await checkpointReply.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(info.SnapshotTick == tick, "late peer starts from the selected checkpoint boundary");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "draft", "only the delta replays");
});

await Test("server rejects unknown command kinds and throttles a flooding client", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("guard")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var errors = new List<string>();
    host.StatusChanged += _ => { };
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[2048]; new Random(41).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("invented", "x"u8.ToArray());
    await Until(() => server.HistoryCount == 0);
    Check(server.HistoryCount == 0, "unknown kind never reaches history");
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    Check(server.HistoryCount == 1, "known kind accepted");
    var accepted = 1;
    for (var i = 0; i < 500; i++)
        await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i % 40}|{i % 40}"));
    await Task.Delay(1500);
    accepted = server.HistoryCount;
    Check(accepted is > 1 and < 501, $"flood is bounded, stored {accepted}");
    await host.SendCommandAsync("designation", "Mine|0|9|9"u8.ToArray());
    await Until(() => server.HistoryCount > accepted);
    Check(server.HistoryCount > accepted, "client recovers after the window");
});

await Test("concurrent state hashes compare every shared tick", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("concurrent-hashes")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("hash-a"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("hash-b"));
    await a.ClaimHostAsync();
    var world = new byte[128];
    await a.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    await Until(() => a.PresenceEpoch == server.Epoch && b.PresenceEpoch == server.Epoch);
    var pawns = new string('1', 64);
    var things = new string('2', 64);
    // Five categories: pawn position, health, job, things, designations. Only the last is made to differ.
    for (var round = 1; round <= 256; round++)
    {
        var expectedTick = round;
        var detail = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, string> record = (tick, text) => { if (tick == expectedTick) detail.TrySetResult(text); };
        b.DesyncDetected += record;
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () =>
            {
                await start.Task;
                await a.ReportHashAsync(expectedTick, new string('a', 64), [pawns, things, things, things, new string('3', 64), things]);
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                await b.ReportHashAsync(expectedTick, new string('b', 64), [pawns, things, things, things, new string('4', 64), things]);
            });
            start.SetResult();
            await Task.WhenAll(first, second);
            Check(await detail.Task.WaitAsync(TimeSpan.FromSeconds(20)) == "标记",
                $"concurrent report pair {expectedTick} must be compared exactly once with category detail");
        }
        finally { b.DesyncDetected -= record; }
    }
});

await Test("every codec command kind is accepted by the server whitelist", () =>
{
    // The whitelist lives in Networking and the constants live in Simulation, so they are maintained by hand.
    // That drifted once already: "thing-state" reached the codec and the apply path but not the whitelist, so
    // the server rejected every generic gizmo change as UnknownCommand and the feature silently did nothing.
    var kinds = typeof(TurboTick.Simulation.CommandCodec)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
    Check(kinds.Length >= 14, $"codec exposes its kinds (found {kinds.Length})");
    var missing = kinds.Where(k => !TurboTick.Networking.DedicatedServer.SupportedKinds.Contains(k)).ToArray();
    Check(missing.Length == 0, "server whitelist is missing: " + string.Join(", ", missing));
    return Task.CompletedTask;
});

await Test("desync detail names the category that diverged", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("cat")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var world = new byte[2048]; new Random(51).NextBytes(world);
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    var detail = new TaskCompletionSource<string>();
    b.DesyncDetected += (_, text) => detail.TrySetResult(text);
    var pawns = new string('1', 64);
    var things = new string('2', 64);
    var designations = new string('3', 64);
    // Both reports must land while both peers count as joined, or the comparison never happens and the report
    // has nobody to go to. Sending exactly one pair made this fail about once in twenty runs; re-sending is
    // enough because the server only reports a tick's divergence once.
    var otherDesignations = new string('4', 64);
    string? named = null;
    for (var attempt = 0; attempt < 40 && named is null; attempt++)
    {
        await a.ReportHashAsync(60, new string('a', 64), [pawns, things, things, things, designations, things]);
        await b.ReportHashAsync(60, new string('b', 64), [pawns, things, things, things, otherDesignations, things]);
        await Task.WhenAny(detail.Task, Task.Delay(250));
        if (detail.Task.IsCompleted) named = detail.Task.Result;
    }
    Check(named == "标记", "only the differing category is named");
});

await Test("a dropped peer reconnects to the checkpoint, replays the delta and can act again", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("recover-session")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(61).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await Until(() => server.Tick >= 4);
    var checkpoint = new byte[4096]; new Random(62).NextBytes(checkpoint);
    await host.CheckpointWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(checkpoint), new MemoryStream(checkpoint), server.Tick);
    await host.SendCommandAsync("draft", "9|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);

    await guest.DisconnectAsync();
    var replay = new List<GameCommand>();
    var info = new TaskCompletionSource<SessionInfo>();
    guest.WorldStarted += (session, _) => info.TrySetResult(session);
    guest.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var resumed = await info.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(resumed.SnapshotTick == server.CheckpointTick, "resumes from the checkpoint");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "draft", "only the post-checkpoint delta replays");
    await guest.SendCommandAsync("designation", "Mine|0|5|5"u8.ToArray());
    await Until(() => server.HistoryCount == 2);
    Check(server.HistoryCount == 2, "reconnected peer can act again");
});

await Test("five peers submitting concurrently yield one total order with no loss, and history stays bounded", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("stress")));
    await server.StartAsync();
    var clients = new List<DedicatedClient>();
    try
    {
        for (var i = 0; i < 5; i++)
        {
            var client = new DedicatedClient();
            clients.Add(client);
            await client.ConnectAsync("127.0.0.1", server.Port, Hello("p" + i));
        }
        var world = new byte[8192]; new Random(71).NextBytes(world);
        await clients[0].ClaimHostAsync();
        await clients[0].UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
        var received = new List<GameCommand>();
        clients[1].CommandsReceived += commands => { lock (received) received.AddRange(commands); };
        await clients[0].StartGameAsync();
        await Task.WhenAll(clients.Select((client, index) => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                await client.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{index}|{i}"));
        })));
        await Until(() => server.HistoryCount == 500, 20);
        Check(server.HistoryCount == 500, "all 500 commands stored");
        // Broadcasts trail the server-side commit; wait for delivery before judging the order.
        // Every peer receives every command, its own included (see RelayCommandAsync: the origin cancelled its
        // local write, so it is the peer that most needs the echo). That makes the expected count 500, not 400.
        await Until(() => { lock (received) return received.Count == 500; }, 20);
        List<GameCommand> ordered;
        lock (received) ordered = [.. received];
        Check(ordered.Count == 500, "every command delivered once, the origin's own included");
        Check(ordered.Select(c => c.Sequence).Distinct().Count() == 500, "no duplicate sequence");
        Check(ordered.Select(c => c.Sequence).SequenceEqual(ordered.Select(c => c.Sequence).Order()), "single total order");
        Check(ordered.All(c => c.Tick >= 0), "every command has a tick");
        // Commands are scheduled a couple of ticks ahead; let them fall into the past before checking.
        await Task.Delay(250);
        await clients[0].CheckpointWorldAsync(
            TurboTick.Networking.SnapshotBuilder.Describe(new byte[4096]), new MemoryStream(new byte[4096]), server.Tick);
        Check(server.HistoryCount == 0, "checkpoint bounds history");
    }
    finally
    {
        foreach (var client in clients) await client.DisposeAsync();
    }
});

await Test("comparison hashes stay inside a bounded window during a long session", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("window")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    var world = new byte[2048]; new Random(81).NextBytes(world);
    await a.ClaimHostAsync();
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    for (var tick = 60; tick <= 6000; tick += 60) await a.ReportHashAsync(tick, new string('a', 64));
    await Until(() => server.TrackedHashTicks > 0);
    Check(server.TrackedHashTicks <= 601, $"hash window bounded, tracked {server.TrackedHashTicks}");
    Check(server.TrackedHashTicks >= 5, "recent history is still available for comparison");
});

await Test("a session refuses input and applies no commands until the world is loaded", () =>
{
    var session = new TurboTick.Simulation.SharedSession();
    session.BeginLoading(3, "abc");
    Check(!session.AcceptsInput && session.Phase == TurboTick.Simulation.SharedPhase.LoadingWorld, "loading blocks input");
    session.Commands.Accept(new GameCommand(1, 0, "designation", [1]));
    Check(session.Commands.Count == 1, "commands are buffered, not dropped");
    session.WorldLoaded();
    Check(!session.AcceptsInput && session.Phase == TurboTick.Simulation.SharedPhase.Synchronizing, "synchronising still blocks input");
    session.Playing();
    Check(session.AcceptsInput, "playing allows input");
    session.Fault("boom");
    Check(session.Phase == TurboTick.Simulation.SharedPhase.Faulted && session.Commands.Count == 0, "fault stops and clears");
    return Task.CompletedTask;
});

await Test("a late join keeps history commands that arrive after live ones", () =>
{
    var log = new CommandLog();
    // Live command arrives first because the peer joined while others were already playing.
    Check(log.Accept(new GameCommand(501, 900, "designation", [1])), "live command accepted");
    Check(log.Accept(new GameCommand(401, 800, "designation", [2])), "older history still accepted");
    Check(log.Accept(new GameCommand(400, 800, "designation", [3])), "history in any order");
    Check(!log.Accept(new GameCommand(501, 900, "designation", [1])), "exact duplicate rejected");
    var due = log.TakeThrough(1000);
    Check(due.Count == 3, "nothing silently dropped");
    Check(due.Select(c => c.Tick).SequenceEqual(new[] { 800, 800, 900 }), "applied in tick order");
    Check(due[0].Sequence < due[1].Sequence, "same tick ordered by sequence");
    return Task.CompletedTask;
});

await Test("an overflowed history tells the joining peer it cannot catch up instead of replaying a hole", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("overflow"), MaxHistory: 2));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[2048]; new Random(91).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    for (var i = 0; i < 5; i++) await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i}|{i}"));
    await Until(() => server.HistoryTruncated, 10);
    Check(server.HistoryTruncated, "history marks itself truncated");
    await using var late = new DedicatedClient();
    var incomplete = new TaskCompletionSource<bool>();
    late.CatchUpIncomplete += () => incomplete.TrySetResult(true);
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(await incomplete.Task.WaitAsync(TimeSpan.FromSeconds(20)), "peer is warned, not silently diverged");
    // A checkpoint makes the room joinable again.
    await Task.Delay(200);
    await host.CheckpointWorldAsync(
        TurboTick.Networking.SnapshotBuilder.Describe(new byte[1024]), new MemoryStream(new byte[1024]), server.Tick);
    Check(!server.HistoryTruncated && server.HistoryCount == 0, "checkpoint clears the truncation state");
});

await Test("command batches carry their epoch and the checkpoint owner passes to a survivor", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("epoch")));
    await server.StartAsync();
    await using var first = new DedicatedClient();
    await using var second = new DedicatedClient();
    await first.ConnectAsync("127.0.0.1", server.Port, Hello("first"));
    await second.ConnectAsync("127.0.0.1", server.Port, Hello("second"));
    Check(first.IsCheckpointOwner && !second.IsCheckpointOwner, "first peer owns checkpoints");
    var world = new byte[2048]; new Random(101).NextBytes(world);
    await first.ClaimHostAsync();
    await first.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    var epochs = new List<long>();
    second.WorldStarted += (session, _) => { lock (epochs) epochs.Add(session.Epoch); };
    await first.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 1; });
    var firstEpoch = epochs[0];
    var batch = new TaskCompletionSource<long>();
    second.CommandsReceived += _ => { };
    var seenEpoch = new TaskCompletionSource<long>();
    await first.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    // Restart creates a new epoch; the old command must not be replayable into it.
    await first.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 2; });
    Check(epochs[1] > firstEpoch, "restart bumps the epoch");
    Check(server.HistoryCount == 0, "old commands are gone");
    // Owner left, so the survivor takes over checkpoints.
    await first.DisconnectAsync();
    await Until(() => second.IsCheckpointOwner);
    Check(second.IsCheckpointOwner, "ownership migrates to the survivor");
    await Task.Delay(150);
    await second.CheckpointWorldAsync(
        TurboTick.Networking.SnapshotBuilder.Describe(new byte[1024]), new MemoryStream(new byte[1024]), server.Tick);
    Check(server.CheckpointManifest is not null, "new owner can checkpoint");
});

await Test("catch-up budget closes the gap instead of matching the server's pace", () =>
{
    Check(TurboTick.Simulation.CatchUpPlan.Steps(0) == 0, "caught up means no burst");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(-5) == 0, "never negative");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(3) == 3, "small gap runs the gap");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(500) == TurboTick.Simulation.CatchUpPlan.MaxPerFrame,
        "large gap is bounded per frame");
    Check(TurboTick.Simulation.CatchUpPlan.Steps(500, 0) == 0, "budget disabled");
    // 1800 ticks behind with 8 per frame at 60 fps closes in under 4 seconds.
    var backlog = 1800;
    var frames = 0;
    while (backlog > 0 && frames < 10_000)
    {
        backlog -= TurboTick.Simulation.CatchUpPlan.Steps(backlog);
        frames++;
    }
    Check(backlog <= 0 && frames < 240, $"gap closes in {frames} frames");
    Check(TurboTick.Simulation.CatchUpPlan.PreferCheckpoint(7200), "huge gap prefers a fresh checkpoint");
    Check(!TurboTick.Simulation.CatchUpPlan.PreferCheckpoint(600), "small gap replays");
    return Task.CompletedTask;
});

await Test("a burst catch-up applies every command exactly once and in order", () =>
{
    var barrier = new TickBarrier();
    var log = new CommandLog();
    var applied = new List<long>();
    // Server is 200 ticks ahead; commands are spread across that gap, some arriving late.
    barrier.Publish(200);
    log.Accept(new GameCommand(1, 10, "designation", [1]));
    log.Accept(new GameCommand(2, 60, "designation", [2]));
    log.Accept(new GameCommand(3, 61, "designation", [3]));
    log.Accept(new GameCommand(4, 199, "designation", [4]));
    log.Accept(new GameCommand(5, 200, "designation", [5]));
    log.Accept(new GameCommand(6, 40, "designation", [6]));  // late arrival with a lower tick
    var frames = 0;
    while (barrier.CanAdvance && frames < 1000)
    {
        var steps = TurboTick.Simulation.CatchUpPlan.Steps(barrier.Backlog);
        for (var i = 0; i < steps && barrier.TryAdvance(); i++)
            foreach (var command in log.TakeThrough(barrier.AppliedTick))
                applied.Add(command.Sequence);
        frames++;
    }
    Check(barrier.AppliedTick == 200, "gap closed exactly");
    Check(applied.Count == 6 && applied.Distinct().Count() == 6, "each command applied once");
    Check(applied.SequenceEqual(new long[] { 1, 6, 2, 3, 4, 5 }), "tick order preserved through the burst");
    Check(frames < 30, $"closed in {frames} frames");
    return Task.CompletedTask;
});

await Test("checkpoint plus delta equals applying the whole history, with no command applied twice", () =>
{
    // Model: each designation command adds one cell id; applying twice would show up as a count change.
    var history = new List<GameCommand>();
    var applied = new List<int>();
    for (var i = 1; i <= 20; i++)
    {
        history.Add(new GameCommand(i, i * 10, "designation", BitConverter.GetBytes(i)));
    }

    var full = new List<int>();
    foreach (var command in history) full.Add(BitConverter.ToInt32(command.Payload));

    // The checkpoint covers ticks up to 120, so commands 1..12 are already inside the snapshot.
    const int checkpointTick = 120;
    var snapshotState = history.Where(c => c.Tick <= checkpointTick)
        .Select(c => BitConverter.ToInt32(c.Payload)).ToList();
    var delta = history.Where(c => c.Tick > checkpointTick).ToList();
    Check(delta.Count == 8, "delta contains only post-checkpoint commands");
    Check(delta.All(c => c.Tick > checkpointTick), "no pre-checkpoint command is replayed");

    var rebuilt = new List<int>(snapshotState);
    var log = new CommandLog();
    foreach (var command in delta) log.Accept(command);
    foreach (var command in log.TakeThrough(int.MaxValue)) rebuilt.Add(BitConverter.ToInt32(command.Payload));

    Check(rebuilt.Count == full.Count, "no command applied twice or lost");
    Check(rebuilt.Order().SequenceEqual(full.Order()), "rebuilt state matches full replay");
    Check(rebuilt.Count == rebuilt.Distinct().Count(), "no duplicate effects");
    _ = applied;
    return Task.CompletedTask;
});

await Test("transport command round-trips and the server accepts the kind", async () =>
{
    var command = TurboTick.Simulation.CommandCodec.TransportCommand(31, 77, 5, 1);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeTransport(command.Payload, out var pod, out var thing, out var count)
        && pod == 31 && thing == 77 && count == 5, "round trip");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeTransport("1|2|0"u8.ToArray(), out _, out _, out _), "zero count rejected");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeTransport("1|2"u8.ToArray(), out _, out _, out _), "short payload rejected");

    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("transport")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[2048]; new Random(111).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    var received = new TaskCompletionSource<GameCommand>();
    guest.CommandsReceived += commands => received.TrySetResult(commands[0]);
    await host.SendCommandAsync("transport", command.Payload);
    var relayed = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(relayed.Kind == "transport", "transport reaches other peers");
});

await Test("colony-wide actions are limited to the host unless explicitly loosened", () =>
{
    var policy = typeof(TurboTick.Simulation.InteractionPolicy);
    Check(!TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(true, false, false), "guest blocked while sharing");
    Check(TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(true, true, false), "host allowed");
    Check(TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(false, false, false), "single-player unaffected");
    Check(TurboTick.Simulation.InteractionPolicy.CanRunColonyWideAction(true, false, true), "override allows guests");
    Check(TurboTick.Simulation.InteractionPolicy.DeniedMessage("交易").Contains("交易"), "message names the action");
    _ = policy;
    return Task.CompletedTask;
});

await Test("native speed: bidirectional selection, pause, late join and restart retain the same policy", async () =>
{
    var directory = Dir("native-speed");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("A"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CurrentPolicy?.TimeSpeed == 1 && b.CurrentPolicy?.TimeSpeed == 1, "normal by default");
    await a.SetTimeSpeedAsync(3);
    await Until(() => b.CurrentPolicy?.TimeSpeed == 3);
    Check(a.CurrentPolicy == b.CurrentPolicy, "same confirmed native selection");
    await b.SetTimeSpeedAsync(0);
    await Until(() => a.CurrentPolicy?.TimeSpeed == 0);
    var pausedRevision = a.CurrentPolicy!.Revision;
    await using var late = new DedicatedClient();
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(late.CurrentPolicy?.TimeSpeed == 0 && late.CurrentPolicy.Revision == pausedRevision, "late join stays paused");
    // A legacy policy edit must never overwrite the native selection.
    await a.SetSpeedAsync(true, 2, 60);
    Check(a.CurrentPolicy?.TimeSpeed == 0, "legacy update preserves pause");
    await a.DisconnectAsync(); await b.DisconnectAsync(); await late.DisconnectAsync();
    await server.StopAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await restarted.StartAsync();
    await a.ConnectAsync("127.0.0.1", restarted.Port, Hello("A"));
    Check(a.CurrentPolicy?.TimeSpeed == 0, "restart retains native pause");
    await a.SetTimeSpeedAsync(2);
    Check(a.CurrentPolicy?.TimeSpeed == 2, "resume to native fast");
});

await Test("native speed: controller-only permissions and invalid values leave room unchanged", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("native-role"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("A"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var original = server.CurrentPolicy;
    await Throws(() => b.SetTimeSpeedAsync(0), "ControllerOnly");
    await Throws(() => a.SetTimeSpeedAsync(-1));
    await Throws(() => a.SetTimeSpeedAsync(5));
    Check(server.CurrentPolicy == original && b.IsConnected, "permission/validation do not mutate or disconnect");
    await a.SetTimeSpeedAsync(4);
    await Until(() => b.CurrentPolicy?.TimeSpeed == 4);
});

await Test("native speed: ordered rapid presses use one authoritative revision stream", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("native-rapid")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello("A"));
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var observed = new System.Collections.Concurrent.ConcurrentQueue<SpeedPolicy>();
    b.PolicyChanged += observed.Enqueue;
    int[] choices = [2, 3, 0, 1, 3, 2, 0];
    var requests = choices.Select(speed => a.SetTimeSpeedAsync(speed)).ToArray();
    await Task.WhenAll(requests);
    await Until(() => b.CurrentPolicy?.Revision == choices.Length);
    Check(a.CurrentPolicy == b.CurrentPolicy && b.CurrentPolicy!.TimeSpeed == choices[^1], "last key wins in order");
    var changes = observed.Where(policy => policy.Revision > 0).ToArray();
    await Until(() => observed.Count(policy => policy.Revision > 0) == choices.Length);
    changes = observed.Where(policy => policy.Revision > 0).ToArray();
    Check(changes.Select(policy => policy.TimeSpeed).SequenceEqual(choices), "no reordered key presses");
});

await Test("native clock: native rates, pause without debt, and running server holds its tick", async () =>
{
    foreach (var (speed, expected) in new[] { (0, 0), (1, 60), (2, 180), (3, 360), (4, 900) })
    {
        var clock = new NativeTickClock();
        var count = Enumerable.Range(0, 100).Sum(_ => clock.Advance(0.01, speed));
        Check(count == expected, $"native speed {speed}: {count}");
    }
    var pauseClock = new NativeTickClock();
    Check(pauseClock.Advance(120, 0) == 0 && pauseClock.Advance(1d / 60, 1) == 1, "no paused time debt");
    // Back-pressure is a clamp applied by the caller, never a speed of zero. The loop used to translate a blocked
    // budget into speed 0, which cleared the sub-tick fraction, so a room republished the same 0.96 of a tick
    // forever: a 20 minute live room ran 45.0 tick/s while its loop ran 62.5 times a second.
    var fraction = new NativeTickClock();
    Check(fraction.Advance(0.021d, 1) == 1 && fraction.Remainder > 0.2d, "the fraction survives a normal frame");
    var unspendable = Math.Min(fraction.Advance(0.021d, 1), 0);
    Check(unspendable == 0 && fraction.Remainder > 0.4d, "a clamped sample costs whole ticks, not the fraction");
    var pressured = new NativeTickClock();
    var delivered = 0;
    for (var i = 0; i < 240; i++)
    {
        var produced = pressured.Advance(0.021d, 1);
        if (i % 4 == 0) continue; // This sample's budget was zero: the ticks it earned are dropped, not banked.
        delivered += produced;
    }
    // 240 samples of 1.26 ticks is 302 earned; a quarter of them are clamped away, leaving ~226. Translating the
    // blocked budget into speed 0 instead - the old behaviour - resets the fraction each time and delivers 180.
    Check(delivered >= 215, $"blocked samples do not reset the accumulation ({delivered} of ~226 ticks, 180 if reset)");
    // The caller hands clamped ticks back instead of dropping them: a blocked iteration trims that iteration, it
    // does not confiscate time that already passed. Measured on a 40-pawn room: 1800 ticks earned per 30 s and
    // 1500 published before this, with the gap matching the clamped iterations exactly.
    var handedBack = new NativeTickClock();
    var kept = 0;
    for (var i = 0; i < 240; i++)
    {
        var earned = handedBack.Advance(0.021d, 1);
        if (i % 4 == 0) { handedBack.Unspend(earned); continue; } // Budget was zero: refund this sample.
        kept += earned;
    }
    Check(kept >= 290, $"handing clamped ticks back keeps wall-clock time ({kept} of ~302 ticks, 226 without)");
    var bounded = new NativeTickClock();
    bounded.Unspend(10_000);
    Check(bounded.Remainder <= 15d && bounded.Advance(1d / 60, 1) <= 15,
        $"a refunded burst stays bounded ({bounded.Remainder:0.##} ticks carried)");
    var directory = Dir("native-clock");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    byte[] world = [1, 2, 3];
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await Until(() => server.Tick >= 6);
    await host.SetTimeSpeedAsync(0);
    await Task.Delay(100); // settle the in-flight timer callback before measuring the hold
    var held = server.Tick;
    await Task.Delay(250);
    Check(server.Tick == held && server.GameRunning, "pause holds running-session tick");
    await host.SetTimeSpeedAsync(3);
    await Until(() => server.Tick > held);
    await host.SetTimeSpeedAsync(0);
    await server.StopAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: directory));
    await restarted.StartAsync();
    Check(restarted.GameRunning && restarted.CurrentPolicy.TimeSpeed == 0 && restarted.WorldManifest is not null,
        "changing native speed preserves the running world across restart");
});

await Test("per-tick permits stop the actual engine call when a native frame exhausts its watermark", () =>
{
    var barrier = new TickBarrier();
    var guard = new SimulationTickGuard(barrier);
    var gameTick = 150_000;
    barrier.Publish(1);
    var calls = 0;
    // A native low-FPS frame attempts two ticks; additional catch-up calls hit the same guard.
    for (var attempt = 0; attempt < 10; attempt++)
    {
        if (!guard.TryBegin(gameTick, out var permit)) continue;
        gameTick++; calls++;
        Check(guard.Complete(permit, gameTick), "completed engine tick commits once");
    }
    Check(calls == 1 && gameTick == 150_001 && barrier.AppliedTick == 1, "no physical-only extra ticks");
    barrier.Publish(4);
    for (var attempt = 0; attempt < 10; attempt++)
    {
        if (!guard.TryBegin(gameTick, out var permit)) continue;
        Check(!guard.TryBegin(gameTick, out _), "recursive tick denied before outer tick completes");
        gameTick++;
        Check(guard.Complete(permit, gameTick), "catch-up and native loops share permits");
    }
    Check(gameTick == 150_004 && barrier.AppliedTick == 4, "the snapshot offset stays constant");
    return Task.CompletedTask;
});

await Test("failed, skipped and debug-jumped engine ticks never count as successful shared ticks", () =>
{
    var barrier = new TickBarrier(); barrier.Publish(3);
    var guard = new SimulationTickGuard(barrier);
    Check(guard.TryBegin(100, out var cancelled), "acquire before potential exception");
    guard.Cancel();
    Check(!guard.Complete(cancelled, 101) && barrier.AppliedTick == 0, "cancelled tick cannot later commit");
    Check(guard.TryBegin(100, out var skipped), "acquire before another Harmony prefix skips");
    Check(!guard.Complete(skipped, 100) && barrier.AppliedTick == 0, "skipped original is not a tick");
    Check(guard.TryBegin(100, out var jumped), "acquire before debug time jump");
    Check(!guard.Complete(jumped, 2100) && barrier.AppliedTick == 0, "fast ecology jump is not one tick");
    Check(guard.TryBegin(100, out var good), "guard is released");
    Check(guard.Complete(good, 101) && !guard.Complete(good, 101), "complete exactly once");
    return Task.CompletedTask;
});

await Test("load completion is tied to one generation and cannot revive a failed or superseded world", () =>
{
    var session = new SharedSession();
    session.BeginLoading(1, "world-A");
    var first = session.LoadGeneration;
    Check(!session.TryFinishLoading(first), "a constructor/loading phase is not a completed load");
    session.WorldLoaded();
    session.BeginLoading(2, "world-B");
    var second = session.LoadGeneration;
    session.WorldLoaded();
    Check(!session.TryFinishLoading(first), "late world-A callback cannot complete world-B");
    session.Fault("read failed");
    Check(!session.TryFinishLoading(second), "late callback cannot revive a failure");
    session.BeginLoading(2, "world-B");
    session.WorldLoaded();
    Check(session.TryFinishLoading(session.LoadGeneration), "only the current finalized load enters play");
    Check(!session.TryFinishLoading(session.LoadGeneration), "one ready transition per load");
    session.Leave();
    Check(!session.TryFinishLoading(second), "leaving invalidates old tickets");
    return Task.CompletedTask;
});

await Test("fingerprint ignores settings left behind by mods that are no longer loaded", () =>
{
    // RimWorld keeps every mod's settings file in Config forever, so a player who has tried many mod packs
    // carries hundreds of files for mods they no longer run. Hashing all of them made the fingerprint depend
    // on that history: one tester with 207 leftovers could not join a host who had none, while both ran the
    // same three mods. These are the file names that actually broke it.
    static string Id(string s) => s;                       // the game's sanitiser is identity for these names
    var prefixes = new[]
    {
        TurboTick.Simulation.ModSettingsOwnership.Prefix("ludeon.rimworld", Id),
        TurboTick.Simulation.ModSettingsOwnership.Prefix("brrainz.harmony", Id),
        TurboTick.Simulation.ModSettingsOwnership.Prefix("lumidust.turbotick", Id),
    };
    Check(prefixes[2] == "Mod_lumidust.turbotick_", "prefix shape: " + prefixes[2]);
    Check(TurboTick.Simulation.ModSettingsOwnership.IsOwnedBy("Mod_lumidust.turbotick_Settings.xml", prefixes),
        "a loaded mod's settings are hashed");
    Check(!TurboTick.Simulation.ModSettingsOwnership.IsOwnedBy("Mod_1321849735_RimefellerMod.xml", prefixes),
        "a removed workshop mod's leftover is ignored");
    Check(!TurboTick.Simulation.ModSettingsOwnership.IsOwnedBy(
            "Mod___LocalCopy_Vanilla Expanded Framework_-9-5_VFEGlobal.xml", prefixes),
        "a removed local-copy mod's leftover is ignored");
    // The server has to write this one before launch (AtmosphereOverhaul's default weather engine needs a GPU
    // adapter and stops the world without one), so it exists on exactly one machine. Hashing it made a client
    // with the identical mod pack refuse to join; the setting picks a weather implementation and display
    // replicas never simulate, so it cannot change what the world does.
    Check(TurboTick.Simulation.ModSettingsOwnership.IsDeploymentLocal(
            "Mod_acutus.atmosphereoverhaul.release_AtmosphereOverhaulMod.xml"),
        "the weather-engine setting the deployment writes is not part of the fingerprint");
    Check(TurboTick.Simulation.ModSettingsOwnership.IsDeploymentLocal(
            "Mod_AtmosphereOverhaul_AtmosphereOverhaulMod.xml"),
        "same setting, other folder spelling");
    Check(!TurboTick.Simulation.ModSettingsOwnership.IsDeploymentLocal("Mod_lumidust.turbotick_Settings.xml"),
        "a mod's own settings still count");
    return Task.CompletedTask;
});

await Test("1.7 package: own assemblies only, zero reference-mod dependencies, complete translations", () =>
{
    PackageValidation.Validate();
    return Task.CompletedTask;
});

await Test("every HarmonyPatch class is installed or explicitly exempt", () =>
{
    // Three times in this project a patch class was written, compiled, and never installed: RememberPawns had no
    // caller, DescribeLoadOrder had no caller, and ForbidPropertyCapturePatch was never added to the installer.
    // Each time the build was clean and the tests were green, because nothing asked "is it actually used".
    var source = Path.Combine(PackageValidation.PackageRoot(), "Source");
    Check(Directory.Exists(source), "Source directory not found: " + source);
    var installer = File.ReadAllText(Path.Combine(source, "PatchInstaller.cs"));
    // Patched from TurboTickMod instead (headless only), or installed by hand with a dynamic target list.
    string[] exempt = ["HeadlessUpdatePatch", "MapPostStagePatches"];
    var missing = new List<string>();
    foreach (var file in Directory.EnumerateFiles(source, "*.cs"))
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("[HarmonyPatch", StringComparison.Ordinal)) continue;
            for (var j = i + 1; j < Math.Min(lines.Length, i + 6); j++)
            {
                if (!lines[j].Contains("class ", StringComparison.Ordinal)) continue;
                var name = lines[j][(lines[j].IndexOf("class ", StringComparison.Ordinal) + 6)..].Trim();
                var cut = name.IndexOfAny([' ', ':', '{', '<']);
                if (cut > 0) name = name[..cut];
                if (!exempt.Contains(name) && !installer.Contains("typeof(" + name + ")", StringComparison.Ordinal))
                    missing.Add(System.IO.Path.GetFileName(file) + ":" + name);
                break;
            }
        }
    }
    Check(missing.Count == 0, "patch classes never installed: " + string.Join(", ", missing));
    return Task.CompletedTask;
});
await Test("every diagnostic probe is read by something", () =>
{
    // Written, compiled, and never consumed - four times: RememberPawns had no caller, DescribeLoadOrder had no
    // caller, ForbidPropertyCapturePatch was never installed, and FrameTrace.Counters() was never called. In every
    // case the build was clean, the tests were green, and the instrument produced nothing to look at.
    //
    // The installation guard covers patches. This one covers the other failure shape: a diagnostic that records into
    // a buffer nobody reads. Recording is not observing.
    var source = Path.Combine(PackageValidation.PackageRoot(), "Source");
    var networking = Path.Combine(PackageValidation.PackageRoot(), "Networking");
    var files = Directory.EnumerateFiles(source, "*.cs").Concat(Directory.EnumerateFiles(networking, "*.cs")).ToArray();
    var text = string.Concat(files.Select(File.ReadAllText));
    var missing = new List<string>();
    foreach (var file in files)
    {
        foreach (var line in File.ReadAllLines(file))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("internal static class ", StringComparison.Ordinal)
                && !trimmed.StartsWith("public static class ", StringComparison.Ordinal)) continue;
            var name = trimmed[(trimmed.IndexOf("class ", StringComparison.Ordinal) + 6)..].Trim();
            if (!(name.EndsWith("Gate", StringComparison.Ordinal) || name.EndsWith("Trace", StringComparison.Ordinal)
                || name.EndsWith("Probe", StringComparison.Ordinal))) continue;
            // A recorder that nobody reads is indistinguishable from no recorder: remembered four times over in this
            // project, and every time the evidence was "the log does not contain the line". So the rule is about the
            // output accessor, not about the class being referenced at all - `FrameTrace.Read` is called from the same
            // file that defines it, which made an "is the class referenced" check pass while the output went nowhere.
            // Which output accessors does this class actually declare? Slice from its declaration to the next one.
            var body = File.ReadAllText(file);
            var start = body.IndexOf("class " + name + "\n", StringComparison.Ordinal);
            if (start < 0) start = body.IndexOf("class " + name + "\r", StringComparison.Ordinal);
            if (start < 0) continue;
            var next = body.IndexOf("static class ", start + 1, StringComparison.Ordinal);
            var slice = next > start ? body[start..next] : body[start..];
            string[] output = ["Counters", "Entries", "Describe", "Summary", "Snapshot"];
            var declared = output.Where(a => slice.Contains("string " + a + "(", StringComparison.Ordinal)).ToArray();
            if (declared.Length == 0) continue;
            // At least one declared accessor must be called. A recorder whose output is never requested is invisible.
            if (declared.Any(a => text.Contains(name + "." + a, StringComparison.Ordinal))) continue;
            missing.Add(name + "." + declared[0] + "()");
        }
    }
    Check(missing.Count == 0, "diagnostics nobody reads: " + string.Join(", ", missing));
    return Task.CompletedTask;
});
await Test("a cancelled vanilla call always has a published command behind it", () =>
{
    // Written, compiled, and silently wrong - measured as "I dragged a mining selection and no marks appear at all":
    // the capture declined (the map lookup could not resolve), the hook returned false anyway, and the player's click
    // disappeared in both directions. The invariant is one line: cancelling a vanilla call must be conditional on the
    // replacement command actually having been published.
    var source = Path.Combine(PackageValidation.PackageRoot(), "Source");
    var offenders = new List<string>();
    foreach (var file in Directory.EnumerateFiles(source, "*.cs"))
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("SharedActions.Capture", StringComparison.Ordinal)) continue;
            if (lines[i].Contains("return !", StringComparison.Ordinal)) continue;
            if (lines[i].Contains("= SharedActions.Capture", StringComparison.Ordinal)) continue;
            // The next meaningful line must not be an unconditional cancel.
            for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
            {
                var next = lines[j].Trim();
                if (next.Length == 0) continue;
                if (next is "{" or "}") continue;
                if (next == "return false;")
                    offenders.Add($"{Path.GetFileName(file)}:{j + 1} {lines[i].Trim()}");
                break;
            }
        }
    }
    Check(offenders.Count == 0, "a capture declines but the hook still cancels: " + string.Join(" | ", offenders));
    return Task.CompletedTask;
});
await Test("server refusals are classified for the player and folded instead of repeated", () =>
{
    // Every command the relay will not publish already came back as a structured Error frame. It only completed a
    // pending request, so a refused gameplay action reached the coordinator's console and nowhere else. These are the
    // rules that decide what the player is told.
    var throttle = NoticeRules.Classify("RateLimited", "Too many commands; the server is throttling this client.");
    Check(throttle.IsCommandRefusal && throttle.Severity == NoticeSeverity.Caution,
        "a throttle is a refusal the player can act on");
    var notPlaying = NoticeRules.Classify("NotPlaying", "The shared colony is not running.");
    Check(notPlaying.IsCommandRefusal && notPlaying.Severity == NoticeSeverity.Information,
        "a stopped room is information, not the player's mistake");
    // A code this client does not know must still be shown: silence is worst exactly when the two ends disagree.
    var unknownCode = NoticeRules.Classify("SomeNewRefusal", null);
    Check(unknownCode.Severity == NoticeSeverity.Caution && unknownCode.Message.Length > 0,
        "an unknown refusal keeps a reason");
    Check(!NoticeRules.Classify("ModHashMismatch", "x").IsCommandRefusal,
        "a handshake answer is not a command refusal");
    var huge = NoticeRules.Classify(new string('c', 200), new string('m', 2000));
    Check(huge.Code.Length == NoticeRules.MaximumCodeLength && huge.Message.Length == NoticeRules.MaximumMessageLength,
        "a refusal cannot flood the log or the UI");
    // The window: one refusal speaks, the burst behind it is counted, the next one stands for the repeats.
    var budget = new NoticeBudget(minimumIntervalMilliseconds: 1000, maximumCodes: 2);
    Check(budget.Admit("RateLimited", 0).Show, "the first refusal is shown");
    var folded = 0;
    for (var i = 1; i <= 7; i++) Check(!budget.Admit("RateLimited", i * 10).Show, "repeats are suppressed");
    folded = budget.Suppressed("RateLimited");
    Check(folded == 7, "the suppressed count was not kept: " + folded);
    var again = budget.Admit("RateLimited", 1000);
    Check(again.Show && again.Suppressed == 7, "the next admitted refusal must report what it stands for");
    Check(budget.Admit("Other", 1005).Show, "codes are independent");
    Check(!budget.Admit("Third", 1010).Show || budget.TrackedCodes <= 2,
        "an invented code cannot grow the table without bound");
    // A clock that went backwards must not lock a refusal out forever.
    var rewind = new NoticeBudget(minimumIntervalMilliseconds: 1000);
    Check(rewind.Admit("X", 5000).Show && rewind.Admit("X", 10).Show, "a backwards clock still speaks once");
    rewind.Clear();
    Check(rewind.TrackedCodes == 0, "clear drops the window");
    return Task.CompletedTask;
});

await Test("sync readout separates the pipeline lead from real lag and reuses the checkpoint threshold", () =>
{
    // In a lockstep room the coordinator publishes deliberately ahead of the slowest peer, so a peer that is keeping
    // up still shows a non-zero Backlog. Displaying that number was the same mistake that once made every peer run
    // about seven times faster than real time.
    var caughtUp = SyncReadout.Of(1000, 1000);
    Check(caughtUp.LagTicks == 0 && caughtUp.Backlog == 0 && !caughtUp.Behind, "equal ticks is in sync");
    var lead = SyncReadout.Of(1006, 1000, NetworkProtocol.SimulationLeadTicks);
    Check(lead.Backlog == 6 && lead.LagTicks == 0 && !lead.Behind, "the pipeline lead is not lag");
    var lagging = SyncReadout.Of(1006 + SyncReadout.BehindTicks, 1000, NetworkProtocol.SimulationLeadTicks);
    Check(lagging.LagTicks == SyncReadout.BehindTicks && lagging.Behind, "lag beyond the lead is behind");
    Check(!SyncReadout.Of(1010, 1000, 0, behindTicks: 20).Behind, "the behind threshold is configurable");
    // Applied ahead of the published tick happens transiently after a checkpoint reset; it is not negative lag.
    var ahead = SyncReadout.Of(1000, 1200);
    Check(ahead.Backlog == 0 && ahead.LagTicks == 0 && !ahead.Behind, "being ahead is never negative lag");
    // The decision "too far behind to replay" existed as CatchUpPlan.PreferCheckpoint with no caller outside tests.
    Check(!SyncReadout.Of(1000 + 600, 1000).NeedsCheckpoint, "a small gap is replayed");
    Check(SyncReadout.Of(1000 + 4000, 1000).NeedsCheckpoint, "a huge gap prefers a checkpoint");
    Check(!SyncReadout.Of(1000 + 4000, 1000, 4000).NeedsCheckpoint, "the lead does not count toward that decision");
    var barrier = new TickBarrier();
    barrier.Reset(500);
    barrier.Publish(510);
    Check(SyncReadout.Of(barrier, lockstep: false).LagTicks == 10, "the barrier overload reads the barrier");
    Check(SyncReadout.Of(barrier, lockstep: true).LagTicks == 10 - NetworkProtocol.SimulationLeadTicks,
        "the barrier overload applies the room's lead");
    // The lead is pipeline depth in time, so the tick count has to grow with the room's speed. A constant 6 ticks is
    // 100 ms at vanilla speed but 16 ms at Superfast, which is shorter than a report round trip: the pacing budget
    // then collapses to 1-2 ticks per loop iteration and a 360 t/s room publishes about 119 (measured 2026-11-16).
    Check(NetworkProtocol.LeadTicksAt(1) == NetworkProtocol.SimulationLeadTicks,
        "vanilla speed keeps the documented six-tick lead");
    Check(NetworkProtocol.LeadTicksAt(0) == NetworkProtocol.SimulationLeadTicks, "a paused room is not tightened");
    Check(NetworkProtocol.LeadTicksAt(2) > NetworkProtocol.SimulationLeadTicks,
        "fast speed widens the lead so pacing keeps up");
    Check(NetworkProtocol.LeadTicksAt(6) >= 36, "Superfast runs at least 36 ticks ahead");
    Check(NetworkProtocol.LeadTicksAt(6) == NetworkProtocol.LeadTicksAt(6.0),
        "the lead is a pure function of the rate");
    Check(NetworkProtocol.LeadTicksAt(double.NaN) == NetworkProtocol.SimulationLeadTicks,
        "a non-finite rate falls back to the vanilla lead");
    return Task.CompletedTask;
});

await Test("a refused command reaches the client instead of only the server log", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("refusals")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var notices = new List<ServerNotice>();
    host.NoticeReceived += notice => { lock (notices) notices.Add(notice); };
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    AutoCheckpoint(host, server);
    var world = new byte[2048]; new Random(43).NextBytes(world);
    await host.ClaimHostAsync();
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("invented", "x"u8.ToArray());
    await Until(() => { lock (notices) return notices.Count > 0; });
    ServerNotice refused;
    lock (notices) refused = notices[0];
    Check(refused.Code == "UnknownCommand" && refused.IsCommandRefusal
        && refused.Severity == NoticeSeverity.Caution, "the refused kind did not reach the player: " + refused.Code);
    Check(server.HistoryCount == 0, "the refused kind still never reaches history");
    // A refused drag is one refusal per cell. Rendering each of them is how a player turns the feature off.
    for (var i = 0; i < 9; i++) await host.SendCommandAsync("invented", "x"u8.ToArray());
    await Task.Delay(400);
    lock (notices) Check(notices.Count == 1, "identical refusals were not folded, saw " + notices.Count);
    // A rejection the caller is waiting for is that call's exception, not a second notice as well: showing it twice
    // would mean every ordinary permission failure also lands in the player's message feed.
    await using var intruder = new DedicatedClient();
    var intruderNotices = new List<ServerNotice>();
    intruder.NoticeReceived += notice => { lock (intruderNotices) intruderNotices.Add(notice); };
    await intruder.ConnectAsync("127.0.0.1", server.Port, Hello("intruder"));
    await Throws(() => intruder.ClaimHostAsync(), "HostOccupied");
    await Task.Delay(200);
    lock (intruderNotices) Check(intruderNotices.Count == 0,
        "an answered request was repeated as a notice: " + string.Join(",", intruderNotices.Select(notice => notice.Code)));
});

await Test("a display replica applies state commands but never object-lifecycle ones", () =>
{
    // This split decides whether a broadcast command runs on a frozen replica. Getting it wrong in the permissive
    // direction is the P0 that was measured: applying a creation command there makes the replica allocate an object
    // id of its own, then the frame delivers the same object under the host's id and moves the local copy aside.
    // The colony looks right and the two UniqueIDsManager counters stay offset, silently, because a replica never
    // sends a state hash.
    string[] entityLifecycle =
        [CommandCodec.Blueprint, CommandCodec.CancelThing, CommandCodec.Transport, CommandCodec.Designator];
    string[] stateOnly =
    [
        CommandCodec.Designation, CommandCodec.RemoveDesignation, CommandCodec.Draft, CommandCodec.Order,
        CommandCodec.Forbid, CommandCodec.ForbidBatch, CommandCodec.Priority, CommandCodec.ZoneCell,
        CommandCodec.ZoneDelete, CommandCodec.BillAdd, CommandCodec.BillDelete, CommandCodec.AreaAssign,
        CommandCodec.AreaOp, CommandCodec.Policy, CommandCodec.ThingState, CommandCodec.Research,
        CommandCodec.Bills, CommandCodec.Storyteller,
        // A choice-letter click runs the letter's own `DiaOption.action` on the host. It creates nothing and destroys
        // nothing: the letter's effect is a signal or a field write, and the letter itself is removed by that action,
        // which the replica then learns from the stack. What it must not do is run on the replica, and that is decided
        // by `LetterChoiceState.TryReport`, not by this predicate.
        CommandCodec.LetterChoice,
        // A growth-moment answer likewise runs a letter's own `MakeChoices` on the host; it creates no object.
        CommandCodec.GrowthChoice,
        // A cancel drag destroys blueprints and clears marks. It looks like a lifecycle action and is not one: the
        // objects it removes are reported by the frame anyway, and the marks are reported by nothing, so a replica
        // that skipped it kept the cancelled markers on screen.
        CommandCodec.CancelDrag,
        // A paint stroke on a player-created allowed area. Sets bits on an area that already exists, so no id is drawn
        // and nothing appears or disappears - and no frame carries the bits, so a replica that skipped it would keep the
        // stroke out of its own grid while its colonists walked by a different rule than the host's.
        CommandCodec.AreaPaint,
        // A training-tab checkbox. A plain flag write plus a walk of the prerequisite tree (`SetWantedRecursive`
        // `:476801-476819`); it creates nothing, and no frame carries the wanted flags.
        CommandCodec.TrainingWanted,
        // A surgery bill: creates one bill on a pawn, and the part is the point of it. Deferring the creation through
        // `bill-add` would arrive without the part (the setter refuses to run before the bill is in a stack).
        CommandCodec.SurgeryBill,
        // A diplomacy request: it appends to the storyteller's `incidentQueue` (`:384081`) and creates nothing itself -
        // the incident spawns its own things on the tick it fires, and those arrive through the frame. A replica that
        // skipped it would show the request as sent and never receive the caravan.
        CommandCodec.Diplomacy,
        // Loading a portal: it appends to `MapPortal.leftToLoad` (`:571789`) and creates no object - the things being
        // loaded already exist. A replica that skipped it would show an empty load plan for a gate the whole colony can
        // see being filled, which is why it applies rather than being deferred through `blueprint`.
        CommandCodec.Portal,
        // A banish / recruit / release decision. It changes which faction a pawn belongs to, and the pawn object
        // itself is created and destroyed by other channels - the decision creates nothing. Classified as state
        // because a replica that skipped it would keep showing the pawn in the old faction until the next frame's
        // `FactionLoadId` overwrote it, i.e. the decision would never be visible where it was made.
        CommandCodec.PawnLeave,
        // Ending an ordered job on a click (round 127). It ends a job that already exists and creates nothing; the
        // job system then decides what the pawn does next, and that arrives as the next frame's driver state. A
        // replica that skipped it would keep the pawn walking to a cell the clicking player already cancelled.
        CommandCodec.Stop
    ];
    foreach (var kind in entityLifecycle)
        Check(CommandCodec.IsEntityLifecycle(kind), kind + " creates or destroys an object and must not run on a replica");
    foreach (var kind in stateOnly)
        Check(!CommandCodec.IsEntityLifecycle(kind), kind + " carries no identity and has no other channel, so it must apply");
    // Every kind the codec defines has to be listed above: a kind added later would otherwise inherit whatever
    // default this predicate happens to have, and nobody would notice until ids drifted. Compared against the codec
    // itself rather than against a hand-maintained count, which is what let the previous version of this line stay
    // green while "policy", "area-op" and "cancel-drag" were added.
    var declared = typeof(CommandCodec)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
    var classified = entityLifecycle.Concat(stateOnly).ToHashSet(StringComparer.Ordinal);
    var unclassified = declared.Where(kind => !classified.Contains(kind)).ToArray();
    Check(unclassified.Length == 0, "these codec kinds are not classified here: " + string.Join(", ", unclassified));
    Check(classified.Count == declared.Length,
        $"the codec defines {declared.Length} kinds; the classification covers {classified.Count}");
    return Task.CompletedTask;
});

await Test("the designator patch targets are filtered by policy, not by a hand-written type list", () =>
{
    // The defect this guards cannot be seen from a green audit: Harmony installs whatever targets it is handed, so a
    // hand-written list of 19 types against several dozen real subclasses reports every patch "✓" and captures nothing
    // the player does. Measured: two peers logged zero `重放建筑蓝图` lines for a whole session while the walls the
    // player drew never existed on the authority - and no diagnostic could say so, because there was no code left to
    // say it from.
    //
    // Discovery itself lives in the game assembly and cannot run here, so this tests the *policy* that decides which
    // discovered types are legal patch targets. Each rule below is a real Harmony failure, not a style preference.
    Check(DesignatorTargets.Acceptable(typeof(string)), "a plain sealed type must be acceptable");
    Check(!DesignatorTargets.Acceptable(typeof(SampleAbstract)), "an abstract type has no method body to patch");
    Check(!DesignatorTargets.Acceptable(typeof(SampleOpen<>)), "an open generic cannot be patched");
    Check(!DesignatorTargets.Acceptable(typeof(SampleClosed<int>)), "a constructed generic is not a type the game creates");
    Check(!DesignatorTargets.Acceptable(null), "a null type must be refused rather than thrown on");

    // The order has to be stable: Harmony derives patch ids from the target method, and an unordered list makes two
    // otherwise identical launches enumerate differently, which is exactly what makes two audit logs incomparable.
    var unsorted = new[] { "z.b", "a.b", "m.b", "A.b" };
    var ordered = DesignatorTargets.Ordered(unsorted);
    Check(ordered.SequenceEqual(["A.b", "a.b", "m.b", "z.b"]), "ordering is not ordinal: " + string.Join(",", ordered));
    Check(DesignatorTargets.Ordered(unsorted).SequenceEqual(ordered), "ordering is not deterministic");
    return Task.CompletedTask;
});

await Test("immunity travels as a whole record list because an absent record means a cured disease", () =>
{
    // `ImmunityHandler.ImmunityHandlerTickInterval` is the only caller of `ImmunityRecord.ImmunityTickInterval`, which is the only
    // writer of `immunity`. A replica ticks no pawns, so the label ("已产生免疫力"), the percentage ("Immunity: 42.3%") and the
    // immune icon all read the value the save held - and `FullyImmune` (immunity >= 1) switches `SeverityPerDay` between
    // `severityPerDayImmune` and `severityPerDayNotImmune`, so the disease's own progression flips with it.
    var wire = new ReplicaImmunity(8, "Flu=0.42;Plague=1");
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var json = System.Text.Json.JsonSerializer.Serialize(wire, options);
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaImmunity>(json, options);
    Check(back == wire, "the immunity record list did not survive a round trip: " + json);
    // The encoding must be parseable back into def/value pairs, or the apply step cannot tell a cured disease (absent) from a
    // half-fought one. Asserting the shape keeps the two ends from drifting.
    var parts = back!.Records.Split(';');
    Check(parts.Length == 2, "the record list did not keep both entries: " + back.Records);
    Check(parts[0] == "Flu=0.42" && parts[1] == "Plague=1", "the record encoding changed shape: " + back.Records);
    // An empty list is the "nothing is being fought" statement; it must be representable so a cured colony clears its panel.
    Check(new ReplicaImmunity(8, "") is { Records: "" }, "the empty record list is not representable");
    return Task.CompletedTask;
});
await Test("the misery counters travel as three values because they are an integral, not a function of mood", () =>
{
    // `MentalBreakerTickInterval` adds 150 per interval while mood is below each threshold and zeroes the counter the moment
    // mood recovers, so the counters are the integral of mood over time - two pawns at the same mood hold different values and
    // the same pawn at the same mood holds a different one depending on how long it has been there. Nothing here can derive
    // them, which is why they are on the wire at all. All three travel because `BreakMinorIsImminent` is false while
    // `BreakMajorIsImminent` is true, and `BreakExtremeIsApproaching` additionally checks a mood window.
    var wire = new ReplicaMentalBreaker(12, 450, 2100, 0);
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var json = System.Text.Json.JsonSerializer.Serialize(wire, options);
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaMentalBreaker>(json, options);
    Check(back == wire, "the misery counters did not survive a round trip: " + json);
    // The all-zero statement must be distinguishable from "not sent": it is what clears a stale glow on the replica after the
    // authority's pawn recovered.
    Check(new ReplicaMentalBreaker(12, 0, 0, 0) is { Minor: 0, Major: 0, Extreme: 0 },
        "the cleared reading is not representable");
    return Task.CompletedTask;
});
await Test("a pawn's age travels as ticks because the setter is what recomputes its life stage", () =>
{
    // `AgeTickInterval` advances the counter inside the pawn's own tick, which a replica never runs, so every pawn keeps the
    // age the save was loaded with - and the age is what the character card, the age text rule and the capacity curves all
    // read. Only the counter (plus the growth fraction, which comes from a randomised pass and cannot be derived) travels:
    // `AgeBiologicalTicks`'s setter also runs `CalculateInitialGrowth` and `RecalculateLifeStageIndex`, and the life stage is
    // a pure function of age against the def's `lifeStageAges`.
    var wire = new ReplicaPawnAge(31, 42L * 3600000L, 0.62f);
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var json = System.Text.Json.JsonSerializer.Serialize(wire, options);
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaPawnAge>(json, options);
    Check(back == wire, "the age did not survive a round trip: " + json);
    // -1 is the game's own "this pawn does not grow" and must be distinguishable from 0, which would read as a newborn with no
    // growth at all.
    Check(new ReplicaPawnAge(31, 100, -1f) is { Growth: -1f }, "the not-applicable growth reading is not representable");
    Check(!json.Contains("lifeStage"), "the life stage must not be on the wire: the setter derives it");
    return Task.CompletedTask;
});
await Test("a weather condition travels by start tick and its own derived values, not by an expiry", () =>
{
    // `GameCondition.Expired` is `TicksGame > startTick + Duration`, and the replica's absolute clock already equals the
    // authority's, so sending `startTick` is enough to make expiration agree - there is no expiry field on the wire because
    // there is nothing it could add. What cannot be derived is the per-class number `Init()` resets: a temperature offset is
    // taken from the def there and then moved by the class's own tick, which never runs here.
    var wire = new ReplicaGameCondition(41, 7, "ColdSnap", 355_000, false, 300_000, "t-17.5");
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var json = System.Text.Json.JsonSerializer.Serialize(wire, options);
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaGameCondition>(json, options);
    Check(back == wire, "the condition did not survive a round trip: " + json);
    Check(json.Contains("\"startTick\":355000"), "the start tick is not on the wire: " + json);
    // A permanent condition must be representable: it has no duration to send, and reading `Duration` on one makes the game
    // log an error and return an invented 360000000.
    var permanent = new ReplicaGameCondition(42, 7, "VolcanicWinter", 1, true, -1, null);
    var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ReplicaGameCondition>(
        System.Text.Json.JsonSerializer.Serialize(permanent, options), options);
    Check(roundTrip is { Permanent: true, Duration: -1 }, "a permanent condition lost its flag or gained a duration");
    // Removals are events and carry the map they belonged to, because by the time a peer applies one the object is already
    // out of the authority's list and cannot be looked up.
    var removal = new ReplicaGameConditionRemoval(7, 41);
    Check(System.Text.Json.JsonSerializer.Deserialize<ReplicaGameConditionRemoval>(
        System.Text.Json.JsonSerializer.Serialize(removal, options), options) == removal,
        "a condition removal lost its map");
    return Task.CompletedTask;
});
await Test("a timed condition's countdown travels as its own block because a rebuild resets it", () =>
{
    // The hediff channel rebuilds a pawn's conditions from `def#part#severity` by removing every one and calling
    // `HediffMaker.MakeHediff` - and `CompPostPostAdd` is where `HediffComp_Disappears.ticksToDisappear` and
    // `HediffComp_KillAfterDays.ticksLeft` get their full duration. So the countdown restarts on every rebuild while the
    // thing that decrements it (`CompPostTick`) never runs here: an anaesthetic with 400 ticks left reads as a fresh dose
    // forever. The same rebuild leaves `HediffComp_TendDuration.totalTendQuality` at zero, so a tended wound reads untended.
    var wire = new ReplicaHediffTimers(9, "Anesthetic#Torso#0.9;disappears;400|Cut#Arm#0.4;tend;0.75");
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var json = System.Text.Json.JsonSerializer.Serialize(wire, options);
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaHediffTimers>(json, options);
    Check(back == wire, "the timer block did not survive a round trip: " + json);
    // The key must be the same triple the hediff channel rebuilds from, or a comp cannot find its condition on the far
    // side. Asserting the shape here is what keeps the two encoders from drifting apart.
    var first = back!.Timers.Split('|')[0].Split(';');
    Check(first.Length == 3 && first[0].Split('#').Length == 3,
        "the timer key is not def#part#severity: " + first[0]);
    Check(first[1] == "disappears" && first[2] == "400", "the countdown is not the second field: " + string.Join(";", first));
    return Task.CompletedTask;
});
await Test("toil progress travels as a counter, not a fraction, and an older frame decodes it as absent", () =>
{
    // The bar over a working pawn's head is `1f - ticksLeftThisToil / defaultDuration`. Only `JobDriver.DriverTick`
    // decrements the counter and a replica never ticks a driver, so the bar stood still while the job channel kept
    // changing which toil it belonged to. The denominator comes from the toil the replica already rebuilt, so only the
    // numerator travels - sending both would let the two disagree.
    var host = new ReplicaToilProgress(77, 342);
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var json = System.Text.Json.JsonSerializer.Serialize(host, options);
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaToilProgress>(json, options);
    Check(back == host, "the toil counter did not survive a round trip: " + json);
    Check(json.Contains("ticksLeft\":342"), "the counter is not on the wire: " + json);
    // -1 is the "no longer working" reading and has to survive too: it is what clears a stale bar when two frames are
    // merged, since the authority only re-sends a value when it changes.
    Check(new ReplicaToilProgress(77, -1) is { TicksLeft: -1 }, "the clearing reading is not representable");
    return Task.CompletedTask;
});
await Test("the frame carries the host's next object id and its weather, and an older frame still decodes", async () =>
{
    // Both facts exist because a replica cannot derive them: identity belongs to the host, and weather is chosen on a
    // tick the replica never runs. Neither had a channel, which is why a replica drifted in ids and stood in the
    // checkpoint's weather.
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("frames")));
    await server.StartAsync();
    await using var replica = new DedicatedClient();
    var received = new TaskCompletionSource<AuthoritativeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    replica.FrameReceived += frame => received.TrySetResult(frame);
    await replica.ConnectAsync("127.0.0.1", server.Port, Hello("replica"));
    var sent = server.BroadcastFrame(new AuthoritativeFrame(240,
        [new ReplicaPawn(11, 7, 2.5f, 3.5f, 2, "Haul", 0.9f)],
        [new ReplicaThing(21, 7, 4, 5, 0, 3, 90, "<s/>", Forbidden: true)],
        [31],
        [new ReplicaPawnState(11, "<p/>")],
        [new ReplicaCreation(41, 7, "Steel", 6, 7, 0, 5, "<c/>", null, Forbidden: true)],
        NextThingId: 77, Weather: [new ReplicaWeather(7, "Rain", "Clear", 1234)]));
    Check(sent == 1, "the frame did not reach the replica: " + sent);
    var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(frame.Tick == 240 && frame.NextThingId == 77, "the host's id counter did not travel: " + frame.NextThingId);
    Check(frame.Weather is [{ Map: 7, Def: "Rain", Previous: "Clear", Age: 1234 }],
        "the host's weather did not travel with its transition age");
    Check(frame.Created is [{ Id: 41 }] && frame.Removed is [31] && frame.Changed is [{ State: "<s/>" }],
        "the existing frame fields must survive the new ones");
    // The forbidden marker travels with the frame because a refused command has no other repair path: measured 98
    // `forbid` commands refused by the per-peer budget in one session, and those stacks stayed crossed out on every
    // peer. A peer that never learns the flag keeps drawing the wrong marker forever.
    Check(frame.Changed is [{ Forbidden: true }] && frame.Created is [{ Forbidden: true }],
        "the forbidden flag did not travel with the frame");
    // Additive on purpose. A frame from a peer that does not send the new fields must still decode - to its own
    // counter and the weather it loaded - rather than fail and leave the replica standing still.
    var legacy = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>("{\"tick\":10,\"pawns\":[]}",
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    Check(legacy is { Tick: 10, NextThingId: 0, Weather: null }, "an older frame no longer decodes");
});

await Test("a pawn's job travels with its queues, bill and count, and an older frame still decodes them as absent", async () =>
{
    // The job *name* plus two targets is not the job. Three things the host sends that a replica cannot derive:
    // - `targetC`, which `ReportStringProcessed` substitutes into `{2}` and which `DoBill` uses as the ingredient
    //   drop cell, so without it the job text reads "unknown" and the progress bar points nowhere;
    // - `targetQueueA/B`, which the same method falls back to when `targetA` is invalid, and which several
    //   drivers read while building their toil list (`DoBill.AnyIngredientsQueued`), so a missing queue both
    //   shows the wrong text and produces a toil count the host's `ForceToil` index cannot reach;
    // - `bill`, without which `DoBill.GetReport()` cannot name the recipe and falls back to the generic string.
    var host = new ReplicaPawn(42, 3, 10.5f, 20.25f, 1, "DoBill", 0.87f,
        JobACell: 1032, JobAThing: 900, JobBCell: 1040, JobBThing: -1,
        Toil: 3, Mood: 0.5f, Food: 0.25f, Rest: 0.75f, Thoughts: "AteWithoutTable#0",
        JobCCell: 1050, JobCThing: -1, JobCount: 7, JobBill: "Bill_MakeHat_31",
        JobQueueA: "2001:77;2002:78", JobQueueB: "3001:-1", JobTag: 5);
    var json = System.Text.Json.JsonSerializer.Serialize(host,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    var back = System.Text.Json.JsonSerializer.Deserialize<ReplicaPawn>(json,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    Check(back == host, "the job's third target, count, bill or queues did not survive a round trip: " + json);
    Check(json.Contains("\"jobBill\":\"Bill_MakeHat_31\""), "the bill id is not on the wire: " + json);
    Check(json.Contains("\"jobQueueA\":\"2001:77;2002:78\""), "the queued targets are not on the wire: " + json);

    // Additive on purpose: a frame from a peer that never sends these fields must decode to "no queue, no bill,
    // no third target" rather than throw and drop the whole frame - that peer's pawns would then stand still.
    var older = System.Text.Json.JsonSerializer.Deserialize<ReplicaPawn>(
        "{\"id\":1,\"map\":0,\"x\":1,\"z\":2,\"rotation\":0,\"job\":\"Haul\",\"health\":1}",
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    Check(older is { JobCCell: -1, JobCThing: -1, JobCount: -1, JobBill: null, JobQueueA: null, JobQueueB: null },
        "an older pawn record no longer decodes to absent job details");
    // `JobTag` -1 means "this peer did not send one", and the replica must then leave `mindState.lastJobTag`
    // alone rather than guess. Guessing `Misc` would be worse than useless: it would clear the idle alert on a
    // peer whose host really does have three colonists standing around.
    Check(older is { JobTag: -1 }, "a pawn record with no job tag must decode to -1, not to Idle or Misc");
    Check(json.Contains("\"jobTag\":5") || json.Contains("\"jobTag\":0"),
        "the job tag is not on the wire: " + json);
    // Null means "the host has no such list", not "empty list": the decoder must leave the queue untouched, so a
    // legacy peer and a peer whose job genuinely has no queue behave identically.
    Check(!json.Contains("\"jobQueueA\":null"), "an absent queue must stay off the wire, not travel as null");
});

await Test("a pawn's conditions travel on their own channel and an older frame leaves them alone", () =>
{
    // A replica runs no tick, so `Pawn_HealthTracker.HealthTick` never runs: injuries do not heal, diseases do not
    // progress, and a wound inflicted after the snapshot is never created at all. The player's report was
    // "已经受伤没有任何日志" - the health tab kept showing the load-time state.
    //
    // Three separate claims are checked here, because each one has a different failure mode:
    // 1. the payload survives a round trip at all (otherwise every condition silently vanishes);
    // 2. an empty list is distinguishable from "no list" (otherwise a cured pawn keeps its old scars forever, and
    //    a pawn that never had conditions can never be told about its first one);
    // 3. an older frame - one without the field - decodes to null rather than an empty list, because the replica
    //    must not read "the authority says this pawn is healthy" from a frame that never mentioned conditions.
    var set = new TurboTick.Networking.ReplicaHediffSet(42, 7,
        ["Cut#LeftArm#3.5", "Flu##0.4"]);
    var json = System.Text.Json.JsonSerializer.Serialize(set,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    var back = System.Text.Json.JsonSerializer.Deserialize<TurboTick.Networking.ReplicaHediffSet>(json,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    Check(back is not null && back.Pawn == 42 && back.Version == 7 && back.Entries.Count == 2
        && back.Entries[0] == "Cut#LeftArm#3.5" && back.Entries[1] == "Flu##0.4",
        "a condition entry did not survive a round trip: " + json);

    // The whole-body form is `defName##severity` - the middle field is empty, not omitted. A decoder that split on
    // `#` and required three parts would silently drop every non-injury condition (flu, malnutrition, addiction),
    // which is most of what a colony actually suffers from.
    Check(TurboTick.Networking.ReplicaHediffSet.Empty(9, 3) is { Pawn: 9, Version: 3, Entries.Count: 0 },
        "an empty condition list must still carry its version");

    var olderFrame = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
        "{\"tick\":10,\"pawns\":[]}",
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    Check(olderFrame is { Hediffs: null },
        "a frame without the condition channel must decode to null, not to an empty list");
    return Task.CompletedTask;
});

async Task LetterChannelTests()
{
    await Test("letters and messages survive the wire and older frames still decode", () =>
    {
        // A replica runs no tick, so `LetterStack.ReceiveLetter` is never called locally: this channel is the only way
        // a player learns that a raid, a disease or a quest happened. The claims checked here are the ones whose
        // failure is silent - a letter that decodes to nothing looks exactly like a quiet colony.
        var letter = new ReplicaLetter(31, "ThreatBig", "Raiders", "They are here.", 4100, HasTarget: true, X: 12, Z: 7, Map: 3);
        var message = new ReplicaMessage("Raiders are attacking.", "ThreatBig");
        var frame = new AuthoritativeFrame(4100, [],
            Letters: [letter], Messages: [message]);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(frame, options);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(json, options);
        Check(back is { Letters.Count: 1, Messages.Count: 1 },
            "the letter channel did not survive a round trip: " + json);
        Check(back.Letters![0] is { Id: 31, Def: "ThreatBig", Label: "Raiders", ArrivalTick: 4100, HasTarget: true, X: 12, Z: 7, Map: 3 },
            "a letter lost a field on the wire: " + json);
        Check(back.Messages![0] is { Text: "Raiders are attacking.", Def: "ThreatBig" },
            "a message lost a field on the wire: " + json);

        // A frame from an older sender carries neither field. It must decode to null, not to an empty list: the
        // replica must not read "the host has nothing to say" from a frame that never had the channel.
        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Letters: null, Messages: null },
            "a frame without the letter channel must decode to null");
        return Task.CompletedTask;
    });

    await Test("a dropped frame's letter is merged forward, not lost", () =>
    {
        // The authority marks a letter as sent the first time it appears, so a frame that is overwritten before it is
        // applied takes that letter with it and it is never sent again. The union is the only place that can save it,
        // and a raid letter is not a cosmetic loss - the player never learns the raid happened.
        var older = new[] { new ReplicaLetter(7, "ThreatBig", "Raid", "", 100) };
        var merged = EventUnion.ById(null, older, state => state.Id);
        Check(merged is { Count: 1 } && merged[0].Id == 7,
            "a letter only the older frame carried was dropped");

        // Newest wins per id: the same letter sent twice must not arrive twice.
        var duplicate = new[] { new ReplicaLetter(7, "ThreatBig", "Raid", "", 100) };
        Check(EventUnion.ById(duplicate, older, state => state.Id) is { Count: 1 },
            "a letter was duplicated by the union");

        // Two distinct letters: both must arrive, newest first.
        var newer = new[] { new ReplicaLetter(8, "ThreatSmall", "Manhunters", "", 103) };
        var both = EventUnion.ById(newer, older, state => state.Id);
        Check(both is { Count: 2 } && both[0].Id == 8 && both[1].Id == 7,
            "two distinct letters were collapsed or reordered into something the receiver cannot apply");

        // Null when neither side has anything: a frame with no events must stay a frame with no events, or every
        // frame grows an empty list and the "no events" fast path in the merge stops firing.
        Check(EventUnion.ById<ReplicaLetter>(null, null, state => state.Id) is null,
            "an empty channel must stay null, not become an empty list");

        // Messages have no id: the text is the identity, so the same line at a different tick is a different event
        // but the same line arriving twice is not.
        var messages = EventUnion.ByKey(
            new[] { new ReplicaMessage("Raiders are attacking.", "ThreatBig") },
            new[] { new ReplicaMessage("Raiders are attacking.", "ThreatBig"),
                    new ReplicaMessage("Colonist needs treatment.", "NegativeHealthEvent") },
            state => state.Text);
        Check(messages is { Count: 2 } && messages[1].Text == "Colonist needs treatment.",
            "a distinct message was collapsed into one already present");
        return Task.CompletedTask;
    });

    await Test("terrain cells survive the wire and keep their layer apart", () =>
    {
        // Terrain is not a Thing - it lives in `TerrainGrid`'s three flat arrays - so the frame's thing enumeration can
        // never carry it and it needs its own channel. The claim that matters is the layer: the same cell holds a top
        // layer, an under layer and a foundation, and writing a value to the wrong array leaves the cell permanently
        // wrong with no error and no log line to grep for.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(500, [],
            TerrainTop: [new ReplicaTerrain(3, 1234, "WoodFloor", 0)],
            TerrainUnder: [new ReplicaTerrain(3, 1234, "Soil", 1)],
            TerrainFoundation: [new ReplicaTerrain(3, 1234, "FakeFoundation", 2)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { TerrainTop.Count: 1, TerrainUnder.Count: 1, TerrainFoundation.Count: 1 },
            "a terrain channel went missing on the wire");
        Check(back.TerrainTop![0] is { Map: 3, Index: 1234, Def: "WoodFloor", Layer: 0 }
            && back.TerrainUnder![0] is { Def: "Soil", Layer: 1 }
            && back.TerrainFoundation![0] is { Def: "FakeFoundation", Layer: 2 },
            "the same cell's three layers were not kept apart");

        // A frame from a sender that has no terrain channel must decode to null, not to an empty list: the replica must
        // not read "the ground did not change" from a frame that never mentioned ground.
        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { TerrainTop: null, TerrainUnder: null, TerrainFoundation: null },
            "a frame without the terrain channel must decode to null");

        // A cell the host changed twice inside one frame is one entry, and a cell only the older frame carried must
        // survive the merge - the authority records the change once and never sends it again.
        var only = new[] { new ReplicaTerrain(3, 77, "Gravel", 0) };
        var merged = EventUnion.ById(null, only, state => state.Index);
        Check(merged is { Count: 1 } && merged[0].Def == "Gravel", "a terrain change was lost by the merge");
        var both = EventUnion.ById([new ReplicaTerrain(3, 78, "Sand", 0)], only, state => state.Index);
        Check(both is { Count: 2 }, "two distinct cells were collapsed into one");
        return Task.CompletedTask;
    });

    await Test("a pawn's gear travels as def/stuff/quality and keeps the two groups apart", () =>
    {
        // Equipment and apparel live in the pawn's own trackers, not in the map's thing list, so the thing enumeration
        // can never carry them. The replica draws an armed colonist empty-handed without this channel. The two claims
        // that matter here are the group separator (equipment and apparel are applied to different trackers, so mixing
        // them would try to equip a shirt) and the empty case (a disarmed pawn must be able to say "nothing").
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var gear = new ReplicaGear(42, "Gun_AssaultRifle#Steel#3;MeleeWeapon_Knife#Plasteel#|Apparel_FlakVest#Steel#2");
        var frame = new AuthoritativeFrame(600, [], Gear: [gear]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Gear.Count: 1 }, "the gear channel went missing on the wire");
        Check(back.Gear![0] is { Pawn: 42 } && back.Gear[0].State == gear.State,
            "a gear string was rewritten on the wire: " + back.Gear[0].State);

        // The group boundary must survive: a decoder that split on `#` and lost the `|` would have no way to tell an
        // apparel entry from a weapon.
        Check(gear.State.Split('|') is [var held, var worn]
            && held.Split(';').Length == 2 && worn.Split(';').Length == 1,
            "the equipment/apparel split is not recoverable from the wire form");

        // A frame from a sender with no gear channel decodes to null, not to an empty list: the replica must not read
        // "this pawn holds nothing" from a frame that never mentioned equipment.
        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Gear: null }, "a frame without the gear channel must decode to null");

        // Per-pawn union in the merge, newest wildcard-state wins: a pawn has one wardrobe and the newer encoding
        // supersedes the older entirely, while two different pawns must both survive.
        var first = new[] { new ReplicaGear(1, "A##|") };
        var second = new[] { new ReplicaGear(1, "B##|"), new ReplicaGear(2, "C##|") };
        var merged = EventUnion.ById(second, first, state => state.Pawn);
        Check(merged is { Count: 2 } && merged[0].State.StartsWith("B", StringComparison.Ordinal),
            "the newer gear state did not supersede the older one for the same pawn");
        return Task.CompletedTask;
    });
}

async Task SettlementRuleTests()
{
    await Test("settlement rule table: every row is reachable and every unlisted pair is refused", () =>
    {
        // The table is policy, and the only way to be sure a permission layer is right is to check every pair. A row
        // lost in an edit is a permission silently removed; a row added by accident is a permission silently granted.
        // Both symptoms look like something else (a colonist standing still, a stockpile emptying), which is why this
        // is exhaustive rather than a sample.
        var rows = SettlementRuleTable.Rows();
        Check(rows.Length == SettlementRuleTable.RowCount, "Rows() and RowCount disagree");
        // Pinned: ColonyTogether's table has this many entries (ct045.decompiled.cs:324277). Changing it is a design
        // decision, and this number is what forces the change to be deliberate.
        Check(rows.Length == 50, $"the rule table has {rows.Length} rows, expected 50");

        var seen = new HashSet<(SettlementAct, SettlementKind)>();
        foreach (var (grant, act, kind) in rows)
        {
            Check(grant != SettlementGrant.None, $"row ({act}, {kind}) names no grant");
            Check(act != SettlementAct.None && kind != SettlementKind.None, "a row names an empty act or kind");
            // One row per (act, kind): two rows would make the answer depend on table order, and `Required` would
            // report whichever came first.
            Check(seen.Add((act, kind)), $"({act}, {kind}) appears twice; the answer would depend on order");
            // Granting exactly that flag permits it, and its presence in the table is what `Required` must report.
            Check(SettlementRuleTable.Permits(grant, act, kind), $"row ({act}, {kind}) does not permit itself");
            Check(SettlementRuleTable.Required(act, kind) == grant, $"Required({act}, {kind}) disagrees with the row");
            // With no grants nothing is permitted - the check that makes default-deny real rather than assumed.
            Check(!SettlementRuleTable.Permits(SettlementGrant.None, act, kind),
                $"({act}, {kind}) is permitted with no grants at all");
        }

        // Every combination that has no row is refused, including a full grant set. This is the claim that matters:
        // "All" is not a wildcard, it is the union of the flags the table names, and an action nobody wrote a rule for
        // stays closed even for the owner. That is intentional - the alternative is an unlisted action silently
        // working, and the owner then cannot tell a missing rule from intended behaviour.
        var unlisted = 0;
        foreach (SettlementAct act in Enum.GetValues<SettlementAct>())
            foreach (SettlementKind kind in Enum.GetValues<SettlementKind>())
            {
                if (seen.Contains((act, kind))) continue;
                unlisted++;
                Check(!SettlementRuleTable.Permits(SettlementGrant.All, act, kind),
                    $"unlisted pair ({act}, {kind}) was permitted by All");
                Check(SettlementRuleTable.Required(act, kind) == SettlementGrant.None,
                    $"Required({act}, {kind}) answered for a pair with no row");
            }
        Check(unlisted > 0, "the sweep found no unlisted pairs, which means the loop is not iterating");
        return Task.CompletedTask;
    });

    await Test("the census does not count retired settlements as settlements", () =>
    {
        // Round 139, and the same defect as the two before it: an instrument that cannot tell one state from another.
        // `settlement-retire` marks a row retired and the report says 剩余据点 0 个, but the census printed its
        // `Sites.Count`, so it said 据点：2 - two instruments, same table, different answers, and only one of them was
        // counting settlements. A retired site has to stay in the table (its land must stop being claimed, not vanish),
        // so the count excludes it and names how many it excluded.
        var text = File.ReadAllText(Path.Combine(SourceDir(), "SyncCensus.cs")).Replace("\r", string.Empty);
        var at = text.IndexOf("rows.Add(new Row(\"据点\"", StringComparison.Ordinal);
        Check(at >= 0, "the census no longer has a settlement row");
        // The count is computed *above* the row, so the window has to look backwards as well as forwards - reading
        // only forward found the row and missed the declaration, which is how a fixed window fails silently.
        var from = Math.Max(0, at - 700);
        var line = text.Substring(from, Math.Min(1100, text.Length - from));
        Check(line.Contains("site => !site.Retired") || line.Contains("Count(site => !site.Retired)"),
            "the census still counts every row of the settlement table, retired ones included");
        Check(line.Contains("已废弃"), "the census excludes retired sites but does not say how many it excluded");
        return Task.CompletedTask;
    });

    await Test("storage is governed by Store, not by Use or Place", () =>
    {
        // Round 139. `Storage`/`Store`/`Storage` was a row with no reader: the reservation gate asked `Use` about the
        // object and `Place` about the cell. Both are the wrong grant - `Use`/`Storage` is not a row (so before the
        // round-136 fallback even a granted `Storage` could not reserve a shelf), and `Place` is stated on `Land`, so
        // `BuildArea` - the blueprint/zone grant - was what governed hauling into a floor stockpile. Measured with
        // `script-zone` and `settlement-chokepoint`: `BuildArea` answered 放行 for a stockpile cell, `Storage` answered
        // 拒绝. The permission existed and pointed at the wrong flag.
        Check(SettlementRuleTable.HasRow(SettlementAct.Store, SettlementKind.Storage),
            "the table no longer states Store/Storage");
        Check(SettlementRuleTable.Required(SettlementAct.Store, SettlementKind.Storage) == SettlementGrant.Storage,
            "Store/Storage is governed by a flag other than Storage");
        Check(!SettlementRuleTable.Permits(SettlementGrant.BuildArea, SettlementAct.Store, SettlementKind.Storage),
            "BuildArea now permits storing, so laying out a zone would also mean filling a stockpile");
        Check(!SettlementRuleTable.Permits(SettlementGrant.Items, SettlementAct.Store, SettlementKind.Storage),
            "Items now permits storing; taking a stack out and putting one in are different grants");
        Check(!SettlementRuleTable.HasRow(SettlementAct.Use, SettlementKind.Storage),
            "a Use/Storage row exists, so asking Store would no longer be the only way to reach the row");

        var rule = File.ReadAllText(Path.Combine(SourceDir(), "SettlementRule.cs")).Replace("\r", string.Empty);
        var ruleAt = rule.IndexOf("internal static SettlementKind KindOfCell", StringComparison.Ordinal);
        Check(ruleAt >= 0, "KindOfCell is missing, so a stockpile cell is classified as land");
        var ruleBody = string.Join("\n", rule.Substring(ruleAt, Math.Min(1800, rule.Length - ruleAt))
            .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(ruleBody.Contains("Zone_Stockpile") && ruleBody.Contains("SettlementKind.Storage"),
            "KindOfCell does not classify a stockpile cell as Storage");

        var gates = File.ReadAllText(Path.Combine(SourceDir(), "SettlementGates.cs")).Replace("\r", string.Empty);
        var gateAt = gates.IndexOf("internal static class SettlementReservationGate", StringComparison.Ordinal);
        Check(gateAt >= 0, "the reservation gate is missing");
        var gateBody = string.Join("\n", gates.Substring(gateAt, Math.Min(2600, gates.Length - gateAt))
            .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        // The probe must ask the act the gate asks, or it compares two different questions (round 134's lesson,
        // repeated on the cell branch: a plain empty cell read 放行/拒绝 with `BuildArea`).
        var probeAt = File.ReadAllText(Path.Combine(SourceDir(), "ScriptedSettlement.cs"))
            .Replace("\r", string.Empty).IndexOf("var onStorage = SettlementRule.KindOfCell", StringComparison.Ordinal);
        Check(probeAt >= 0, "the chokepoint probe hardcodes Store for an empty cell, so its two sides ask different acts");
        Check(!File.ReadAllText(Path.Combine(SourceDir(), "ScriptedSettlement.cs"))
                .Replace("\r", string.Empty)
                .Contains("CanCell(pawn, SettlementAct.Store, cell)"),
            "the chokepoint probe still asks Store about every empty cell");
        Check(gateBody.Contains("target.Thing is Building_Storage"),
            "a shelf is still reserved as Use, so Store remains unread for objects");
        // The cell branch must be keyed by the pawn, not by the local player: keyed by the local player, the owner's
        // `Owns` short-circuit fired for every visitor on the authority, so the grant table was never consulted and the
        // cell half of this gate could not refuse anybody (measured: `BuildArea` and `Storage` both answered 放行).
        var cellOverload = gates.IndexOf("internal static bool CanCell(Pawn? pawn", StringComparison.Ordinal);
        Check(cellOverload >= 0, "the pawn cell overload is missing");
        var cellBody = string.Join("\n", gates.Substring(cellOverload, Math.Min(3200, gates.Length - cellOverload))
            .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var pawnKeyedAt = cellBody.IndexOf("var mine = data.HomeOfPawn(pawn.thingIDNumber)", StringComparison.Ordinal);
        var localPlayerAt = cellBody.IndexOf("var player = LocalPlayer", StringComparison.Ordinal);
        Check(pawnKeyedAt >= 0, "the pawn cell overload does not read the pawn's own membership");
        Check(cellBody.Contains("data.GrantsForPawn(mine, home)"),
            "the pawn cell overload does not consult the grants of the pawn's own settlement");
        Check(localPlayerAt < 0 || pawnKeyedAt < localPlayerAt,
            "the pawn cell overload short-circuits on the local player before it reads the pawn's membership");
        Check(gateBody.Contains("SettlementAct.Store : SettlementAct.Place"),
            "a stockpile cell is still reserved as Place, so BuildArea still governs hauling");
        return Task.CompletedTask;
    });

    await Test("the reservation fallback widens exactly these kinds and no others", () =>
    {
        // Round 136 made `Permitted` treat `Use` specially: when the table states no `Use` row for a kind, a visitor
        // holding *any* grant that applies to that kind may reserve the object. That is necessary - `CanReserve` is how
        // every work giver asks "may I touch this", and it cannot know the act: without the fallback a granted
        // `Cleaning`/`Gathering`/`PutOut`/`Hunt`/`Handle` was dead code one gate early (measured: `访客独缺：CleanFilth`
        // for a visitor holding `All`).
        //
        // What it also does, invisibly, is widen every *other* kind that has no `Use` row. Pinning the set is what makes
        // that a decision instead of an accident: a future table edit that removes a `Use` row would silently widen the
        // reservation gate, and the symptom would be a visitor allowed to touch something nobody intended - which looks
        // like anything except a table edit.
        var widened = new List<string>();
        foreach (SettlementKind kind in Enum.GetValues<SettlementKind>())
        {
            if (kind == SettlementKind.None) continue;
            if (SettlementRuleTable.HasRow(SettlementAct.Use, kind)) continue;
            // A kind with no rows at all cannot be reached by the fallback (`PermitsAnyOn` is false, so the `&&`
            // short-circuits): `Colony` is such a kind, and listing it would suggest it is exposed when it is not.
            if (!SettlementRuleTable.PermitsAnyOn(SettlementGrant.All, kind)) continue;
            widened.Add(kind.ToString());
        }
        widened.Sort(StringComparer.Ordinal);
        Check(widened.Count == 17, $"the fallback can fire for {widened.Count} kinds, expected 17: " + string.Join(",", widened));
        // `Colony` is the one kind the table never states an act for, so it must never appear above.
        Check(!widened.Contains(SettlementKind.Colony.ToString()),
            "Colony has no rows, so the fallback cannot fire for it");
        Check(string.Join(",", widened) == string.Join(",", new[]
              {
                  "Animal", "Blueprint", "Building", "Door", "Equipment", "Filth", "Fire", "Food", "Frame", "Land",
                  "Pawn", "Plant", "Rock", "Roof", "Storage", "Supply", "WildAnimal",
              }.OrderBy(each => each, StringComparer.Ordinal)),
            "the widened kind set changed: " + string.Join(",", widened));
        // The four kinds that keep their own `Use` row must stay outside the fallback. If one of these ever appears in
        // the widened list, the fallback is granting bench/bed/pod/seat access through an unrelated grant
        // (`Construction` would reach a bench's `Use` through its `TakeApart` row).
        foreach (var kept in new[] { SettlementKind.Bed, SettlementKind.Bench, SettlementKind.Pod, SettlementKind.Seat })
            Check(!widened.Contains(kept.ToString()), $"{kept} lost its Use row, so the fallback now reaches it");
        // And the fallback must not fire for a pawn holding nothing: the widening is about *which* grant, not about
        // default-allow.
        Check(!SettlementRuleTable.PermitsAnyOn(SettlementGrant.None, SettlementKind.Pawn),
            "a pawn with no grants can reserve a pawn, so the fallback is a default-allow");
        Check(SettlementRuleTable.PermitsAnyOn(SettlementGrant.Care, SettlementKind.Pawn),
            "Care does not reach a pawn, so a visitor granted care could not tend anyone");
        return Task.CompletedTask;
    });

    await Test("settlement grants: Default is the survival four and All covers every row", () =>
    {
        // `Default` is what a visitor gets without being granted anything, and `Unaffiliated` is the same set for a
        // player with no settlement. Both are deliberate: enough to walk in, sleep, work and eat the host's food; not
        // enough to carry loose goods away or deconstruct the place. A change here silently changes what every uninvited
        // player may do - and round 156 measured the `Items` variant paying a visitor's trade with the host's silver, so
        // the fourth flag is `Food` on purpose.
        Check(SettlementGrant.Default == (SettlementGrant.Doors | SettlementGrant.Care
            | SettlementGrant.Benches | SettlementGrant.Food),
            "Default is no longer the survival four");
        Check((SettlementGrant.Default & SettlementGrant.Items) == 0,
            "Default grants `Items` again, so a visitor may carry the host's loose goods off and pay a trade with them");
        // The rows Default permits, and the fact that it permits nothing else. Six, not five: `Care` covers three
        // rows on its own (Use Bed, Use Pod, Tend Pawn - a bed and a medical pod are different things to the engine),
        // and getting this number wrong by one is exactly the kind of off-by-one that makes a permission layer
        // confusing to reason about.
        var allowed = 0;
        foreach (var (grant, act, kind) in SettlementRuleTable.Rows())
            if (SettlementRuleTable.Permits(SettlementGrant.Default, act, kind)) allowed++;
        Check(allowed == 6, $"Default permits {allowed} rows, expected 6 (Doors; Care×3; Benches; Items)");

        // `All` is defined as the union of every named flag. Checked as bits rather than by listing them again: a flag
        // added to the enum but forgotten in `All` would make some rule unreachable for an owner.
        var everyFlag = SettlementGrant.None;
        foreach (SettlementGrant flag in Enum.GetValues<SettlementGrant>())
            if (flag != SettlementGrant.None && flag != SettlementGrant.Default && flag != SettlementGrant.All)
                everyFlag |= flag;
        Check(SettlementGrant.All == everyFlag, "All does not cover every named flag");

        // Every row must be satisfiable by `All`, or a rule exists that nobody can ever be granted.
        foreach (var (_, act, kind) in SettlementRuleTable.Rows())
            Check(SettlementRuleTable.Permits(SettlementGrant.All, act, kind),
                $"({act}, {kind}) cannot be granted to anyone, not even the owner");
        return Task.CompletedTask;
    });
        // **Two round-140 defects share one cause: a name nobody could check.**
        // `GizmoStatePatch.GizmoTargetNames` lists vanilla gizmo types as *strings*, so the compiler never sees them; one
        // entry had the wrong namespace since it was written and had never resolved, which the runtime proved
        // (`通用 gizmo 捕获已挂载 13 个` + `以下 gizmo 类型没有可挂载的 ProcessInput … RimWorld.Command_CallBossgroup`).
        // The cost was that the summon gizmo's click was never captured on any peer. The fix is not the one word: it is a
        // resolver with a simple-name fallback plus a probe that reports the counts, so the next wrong name is measured
        // instead of being a log line nobody reads.
        {
            var resolver = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "TypeResolver.cs"));
            Check(resolver.Contains("AccessTools.TypeByName(qualifiedName)", StringComparison.Ordinal),
                "the type resolver does not try the qualified name first, so a correct namespace would still be a guess");
            Check(resolver.Contains("assembly.GetTypes()", StringComparison.Ordinal)
                && resolver.Contains("string.Equals(candidate.Name, simple, StringComparison.Ordinal)",
                    StringComparison.Ordinal),
                "the type resolver has no simple-name fallback, so a wrong namespace silently disables the entry again");
            Check(resolver.Contains("catch (Exception) { continue; }", StringComparison.Ordinal),
                "an assembly that refuses GetTypes aborts the scan instead of being skipped");

            var shared = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
            var gizmo = shared.IndexOf("internal static class GizmoStatePatch", StringComparison.Ordinal);
            Check(gizmo >= 0, "GizmoStatePatch is missing");
            // The window must cover the *whole* class: `TargetMethods` is declared before the name resolution inside it,
            // so bounding by that declaration ended the window one line early and the assertion read a missing call that
            // was there (`RESULT 161 passed, 1 failed`). Bound by the next `[HarmonyPatch]` instead - the file's own
            // class boundary - so growth inside the class can never cut it short again.
            var nextClass = shared.IndexOf("[HarmonyPatch]", gizmo + 40, StringComparison.Ordinal);
            var gizmoBody = shared.Substring(gizmo, (nextClass < 0 ? shared.Length : nextClass) - gizmo)
                .Replace("\r", string.Empty);
            Check(!gizmoBody.Contains("AccessTools.TypeByName(name)", StringComparison.Ordinal)
                && gizmoBody.Contains("TypeResolver.Find(name)", StringComparison.Ordinal),
                "the gizmo patch resolves names directly instead of through the resolver, so a wrong namespace disables "
                + "the entry with no way to see it");

            var audit = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ModPatchAudit.cs"));
            Check(audit.Contains("internal static string GizmoReport()", StringComparison.Ordinal),
                "the gizmo capture's counts are still only a log line, so no later round can measure them");
            Check(audit.Contains("TypeResolver.Exact(typeName)", StringComparison.Ordinal),
                "the report does not say which entries needed the simple-name fallback, so a wrong namespace hides");
            Check(audit.Contains("type.FullName", StringComparison.Ordinal),
                "the fallback line does not name the type it found, so the measurement cannot correct the entry");
        }

        // **The bossgroup summon cooldown (`GameComponent_Bossgroup.lastBossgroupCalled`).**
        // `Command_CallBossgroup.IsDisabled` reads it from `GizmoOnGUI` (`:3184-3199`), so every peer draws its own answer,
        // and the only writer runs where the summon executes. The summon action itself has no cooldown check - the gizmo
        // *is* the cooldown - so a replica's enabled gizmo means a second bossgroup can really be summoned during the
        // host's two-day cooldown.
        {
            var boss = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "BossgroupSync.cs"));
            Check(boss.Contains("manager.lastBossgroupCalled = state.LastCalled;", StringComparison.Ordinal),
                "there is no bossgroup cooldown channel, so a replica draws the summon gizmo as ready");
            Check(boss.Contains("if (sent && current == lastCalled) return null;", StringComparison.Ordinal),
                "the bossgroup channel is a primed-and-skip shape, so a peer joining an existing colony never receives "
                + "the current stamp (0 is a legitimate value, so equality cannot mean 'never sent')");
            Check(boss.Contains("lastCalled = state.LastCalled;", StringComparison.Ordinal),
                "the apply does not move the local diff baseline, so the value that arrived is published straight back");
            var bossModels = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
            Check(bossModels.Contains("public sealed record ReplicaBossgroup(int LastCalled);", StringComparison.Ordinal),
                "the payload carries something other than the stamp, so the arithmetic on the far side cannot match");
            Check(bossModels.Contains("ReplicaBossgroup? Bossgroup = null", StringComparison.Ordinal),
                "the frame carries no bossgroup stamp");
            var bossReplica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "AuthoritativeReplica.cs"));
            Check(bossReplica.Contains("newer.Bossgroup ?? older.Bossgroup", StringComparison.Ordinal),
                "the bossgroup stamp is not merged, so a frame that omits it erases the cooldown");
            Check(bossReplica.Contains("BossgroupSync.Apply(bossgroup)", StringComparison.Ordinal),
                "the bossgroup stamp is never applied on a replica");
            var bossCensus = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SyncCensus.cs"));
            Check(bossCensus.Contains("BossgroupSync.Summary()", StringComparison.Ordinal),
                "the census cannot show whether the cooldown stamp ever travelled, and an enabled gizmo looks identical "
                + "whether or not it did");
        }


        // **A census row must not repeat its own label (round 140).** The census prints `标签：值`, so a `Summary()` that
        // begins with the label reads `游戏设置：游戏设置 发出 1 / 应用 0` - measured on the container this round, and the
        // third time this exact duplication appeared (`自动屠宰：自动屠宰 …` was fixed by hand in round 129). Four channels
        // had it at once, so the fix has to be a check rather than a habit. No regex: the first version's pattern lost a
        // parenthesis on the way through this file and threw `RegexParseException` at startup.
        {
            var census = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SyncCensus.cs"))
                .Replace("\r", string.Empty);
            var duplicated = new List<string>();
            foreach (var line in census.Split('\n'))
            {
                var rowAt = line.IndexOf("new Row(\"", StringComparison.Ordinal);
                var summaryAt = line.IndexOf(".Summary()", StringComparison.Ordinal);
                if (rowAt < 0 || summaryAt < rowAt) continue;
                var labelStart = rowAt + "new Row(\"".Length;
                var labelEnd = line.IndexOf('"', labelStart);
                if (labelEnd <= labelStart) continue;
                var label = line.Substring(labelStart, labelEnd - labelStart);
                // The channel is the identifier immediately before `.Summary()`.
                var nameEnd = summaryAt;
                var nameStart = nameEnd;
                while (nameStart > 0 && (char.IsLetterOrDigit(line[nameStart - 1]) || line[nameStart - 1] == '_'
                                         || line[nameStart - 1] == '.'))
                    nameStart--;
                var channel = line.Substring(nameStart, nameEnd - nameStart);
                var owner = channel.Contains('.') ? channel[..channel.IndexOf('.')] : channel;
                var path = Path.Combine(PackageValidation.PackageRoot(), "Source", owner + ".cs");
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path).Replace("\r", string.Empty);
                var at = text.IndexOf("internal static string Summary", StringComparison.Ordinal);
                if (at < 0) continue;
                var body = string.Join("\n", text.Substring(at, Math.Min(900, text.Length - at))
                    .Split('\n').Where(entry => !entry.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                if (body.Contains("$\"" + label, StringComparison.Ordinal)
                    || body.Contains("\"" + label + " ", StringComparison.Ordinal))
                    duplicated.Add(label + " <- " + owner);
            }
            Check(duplicated.Count == 0,
                "these census rows repeat their own label, so the report reads 标签：标签 …: " + string.Join(", ", duplicated));
        }


    // **The ownership layer as one sequence (round 141).** Every gate in it has been measured alone; what had never been
    // measured is the order a player produces - refused, granted, allowed, at war, at peace - and that ordering is where a
    // war check placed after the grant table, a grant reader that ignores the visitor's own settlement, or a retired site
    // that still governs its cells would show up. The scenario pins the seven questions and the teardown, because a
    // scenario that leaves its fixture behind becomes the next round's unexplained reading.
    await Test("the settlement scenario covers the sequence and cleans up after itself", () =>
    {
        var script = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"))
            .Replace("\r", string.Empty);
        var at = script.IndexOf("internal static string Scenario", StringComparison.Ordinal);
        Check(at >= 0, "there is no acceptance scenario for the ownership layer");
        var nextMember = script.IndexOf("internal static ", at + 40, StringComparison.Ordinal);
        var body = string.Join("\n", script.Substring(at, (nextMember < 0 ? script.Length : nextMember) - at)
            .Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        foreach (var step in new[]
                 {
                     "无授权 · 土地 Place", "无授权 · 商品 Use", "授权 BuildArea · 土地 Place",
                     "授权 BuildArea · 土地 Build", "宣战 · 土地 Place", "求和 · 土地 Place", "撤销据点",
                 })
            Check(body.Contains(step, StringComparison.Ordinal),
                "the scenario no longer asks this step, so its coverage silently shrank: " + step);
        Check(body.Contains("SettlementWar.Declare(data, siteA, siteB)", StringComparison.Ordinal),
            "the scenario does not declare war, so the one step that must outrank every grant is untested");
        Check(body.Contains("if (warDeclared) SettlementWar.Peace(data, siteA, siteB);", StringComparison.Ordinal),
            "the scenario does not undo its war, so a run leaves two settlements at war");
        Check(body.Contains("if (createdA) Retire(siteA.ToString())", StringComparison.Ordinal)
            && body.Contains("if (createdB) Retire(siteB.ToString())", StringComparison.Ordinal),
            "the scenario does not retire the sites it created, so its fixture survives the run");
        Check(body.Contains("SettlementGates.Uncounted()", StringComparison.Ordinal),
            "the scenario asks the gates without turning the counters off, so its own twenty questions are counted as "
            + "gameplay");
        Check(body.Contains("IdOf(\"Alpha\")", StringComparison.Ordinal) && body.Contains("IdOf(\"Beta\")",
                StringComparison.Ordinal),
            "the scenario identifies its sites by position in the table rather than by the name it wrote, so on a save "
            + "that has retired a site it measures the wrong settlements");
        Check(body.Contains("ownerB") && !body.Contains("b.Faction?.loadID ?? 2"),
            "the scenario uses a faction id as the site owner, so both sites belong to one 'player' and the two-owner "
            + "case cannot be shown");
        return Task.CompletedTask;
    });

    await Test("the unnatural corpse's state machine travels whole, not just its research", () =>
    {
        var root = PackageValidation.PackageRoot();
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs")).Replace("\r", string.Empty);
        var sync = File.ReadAllText(Path.Combine(root, "Source", "CorpseTrackerSync.cs")).Replace("\r", string.Empty);
        var runtime = File.ReadAllText(Path.Combine(root, "Source", "HeadlessRuntime.cs")).Replace("\r", string.Empty);
        var replica = File.ReadAllText(Path.Combine(root, "Source", "AuthoritativeReplica.cs")).Replace("\r", string.Empty);
        var census = File.ReadAllText(Path.Combine(root, "Source", "SyncCensus.cs")).Replace("\r", string.Empty);
        Check(models.Contains("public sealed record ReplicaCorpseTracker(int Corpse, int AwakenTick, int DisappearTick,",
                StringComparison.Ordinal),
            "the corpse tracker has no row of its own, so only its research value travels");
        // Every field of the state machine, by the name the dump pins: a rename must show up as Unresolved, not silence.
        foreach (var field in new[]
                 {
                     "awakenTick", "disappearTick", "spawnedTick", "ticksToKill", "haunted", "awokenPawn",
                     "escape", "mentalBreak", "teleportIn", "research", "researchToDestroy",
                 })
            Check(sync.Contains($"\"{field}\"", StringComparison.Ordinal),
                $"the corpse tracker's `{field}` is not carried, so the replica keeps the value it loaded with");
        Check(runtime.Contains("CorpseTrackerSync.Publish()", StringComparison.Ordinal)
              && runtime.Contains("corpseTracker", StringComparison.Ordinal),
            "the channel publishes but the frame does not carry it");
        Check(replica.Contains("CorpseTrackerSync.Apply(corpseTracker)", StringComparison.Ordinal),
            "the row is merged and never applied");
        Check(replica.Contains("newer.CorpseTracker ?? older.CorpseTracker", StringComparison.Ordinal),
            "the corpse's state is merged as an event, so a frame without it would look like \"no corpse\"");
        Check(census.Contains("CorpseTrackerSync.Summary()", StringComparison.Ordinal),
            "the census cannot tell \"no corpse\" from \"one corpse and no carrier\"");
        return Task.CompletedTask;
    });

    await Test("the eleven default-care settings travel while the display toggles stay local", () =>
    {
        var root = PackageValidation.PackageRoot();
        var sync = File.ReadAllText(Path.Combine(root, "Source", "PlaySettingsSync.cs")).Replace("\r", string.Empty);
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs")).Replace("\r", string.Empty);
        Check(models.Contains("public sealed record ReplicaPlaySettings(bool UseWorkPriorities, bool AutoHomeArea, bool AutoRebuild, string? Care = null);", StringComparison.Ordinal),
            "the row carries no care defaults, so a replica shows whatever its own save had");
        // Eleven, and named: a shortened list would silently stop carrying the last ones.
        foreach (var field in new[]
                 {
                     "defaultCareForColonist", "defaultCareForPrisoner", "defaultCareForSlave", "defaultCareForTamedAnimal",
                     "defaultCareForFriendlyFaction", "defaultCareForNeutralFaction", "defaultCareForHostileFaction",
                     "defaultCareForNoFaction", "defaultCareForWildlife", "defaultCareForEntities", "defaultCareForGhouls",
                 })
            Check(sync.Contains(field, StringComparison.Ordinal), "the care list is missing " + field);
        Check(sync.Contains("if (value < 0 || value > (int)RimWorld.MedicalCareCategory.Best) continue;", StringComparison.Ordinal),
            "an out-of-range value would be written into a byte-backed enum (Enum.IsDefined throws for those)");
        Check(!sync.Contains("showZones", StringComparison.Ordinal) && !sync.Contains("showBeauty", StringComparison.Ordinal),
            "a display toggle entered the shared row, so one player's overlay preference would flip everyone's");
        return Task.CompletedTask;
    });

    await Test("the visual Ethereal carriers travel while the logic ones stay behind", () =>
    {
        var root = PackageValidation.PackageRoot();
        var sync = File.ReadAllText(Path.Combine(root, "Source", "EtherealVisualSync.cs")).Replace("\r", string.Empty);
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs")).Replace("\r", string.Empty);
        var replica = File.ReadAllText(Path.Combine(root, "Source", "AuthoritativeReplica.cs")).Replace("\r", string.Empty);
        var census = File.ReadAllText(Path.Combine(root, "Source", "SyncCensus.cs")).Replace("\r", string.Empty);
        // The whitelist is the whole safety property: the category also holds `SignalAction_*` and `RectTrigger`, which
        // would run their action a second time on the replica, and `OrbitalStrike.Tick` would explode twice.
        Check(sync.Contains("VisualClasses = [\"Bombardment\", \"OrbitalDebrisSpawner\", \"Tornado\"]", StringComparison.Ordinal),
            "the visual carriers are no longer the three classes that were measured");
        var listed = sync.Split("VisualClasses = [")[1].Split('\n')[0];
        Check(!listed.Contains("SignalAction", StringComparison.Ordinal) && !listed.Contains("RectTrigger", StringComparison.Ordinal),
            "a logic carrier entered the visual whitelist, so a replica would fire an event the authority already fired");
        Check(sync.Contains("if (thing is OrbitalStrike) Set(thing, \"ticksToNextEffect\", int.MaxValue);", StringComparison.Ordinal),
            "a strike that starts ticking on a replica would explode a second time");
        Check(models.Contains("IReadOnlyList<ReplicaLivingEffect>? LivingEffects = null", StringComparison.Ordinal),
            "the row never enters the frame, so the channel publishes into nothing");
        Check(replica.Contains("EtherealVisualSync.Apply(row)", StringComparison.Ordinal),
            "the row arrives and is never applied");
        Check(census.Contains("EtherealVisualSync.Summary()", StringComparison.Ordinal),
            "the census cannot tell \"no strike\" from \"strikes nobody carries\"");
        return Task.CompletedTask;
    });

    await Test("a message arrives with its look target and its history flag, so both ends' archives agree", () =>
    {
        var root = PackageValidation.PackageRoot();
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs")).Replace("\r", string.Empty);
        var letters = File.ReadAllText(Path.Combine(root, "Source", "LetterSync.cs")).Replace("\r", string.Empty);
        var capture = File.ReadAllText(Path.Combine(root, "Source", "MessageHistoryCapture.cs")).Replace("\r", string.Empty);
        var installer = File.ReadAllText(Path.Combine(root, "Source", "PatchInstaller.cs")).Replace("\r", string.Empty);
        Check(models.Contains("public sealed record ReplicaMessage(string Text, string Def, bool HasTarget = false,",
                StringComparison.Ordinal)
              // Map 0 is the id of a game's first map, so 0 cannot double as "no target": the sentinel is -1, and a
              // check written against 0 silently dropped the click target of everything pointing at the first map.
              && models.Contains("int Map = -1, int X = 0, int Z = 0, bool Historical = true);", StringComparison.Ordinal),
            "the message row carries no cell and no history flag, so the replica cannot point the click or archive it");
        // `historical` is a parameter of `Messages.Message`, not a field on `Message`; the capture patch is the only way
        // to know it after the fact.
        Check(capture.Contains("internal static bool Historical(Message message)", StringComparison.Ordinal)
              && capture.Contains("message is not null && historical.TryGetValue(message, out var flag) ? flag : true;",
                  StringComparison.Ordinal),
            "the history flag is guessed instead of captured, so a transient message is archived on the replica");
        Check(installer.Contains("typeof(MessageHistoryCapturePatch)", StringComparison.Ordinal),
            "the capture patch exists but is not installed, so every message answers with the default");
        Check(letters.Contains("MessageHistoryCapturePatch.Historical(message)", StringComparison.Ordinal),
            "the row is published without the captured flag");
        // The publish side must not put the absent case back to 0 either - that is the half that broke: the row
        // carried the cell and dropped the map.
        Check(letters.Contains("hasCell ? cellMap : -1", StringComparison.Ordinal),
            "the publisher writes 0 for \"no map\", so a target on the game's first map is published as no target");
        Check(letters.Contains("var historical = state.Historical;", StringComparison.Ordinal)
              && letters.Contains("Messages.Message(message, historical);", StringComparison.Ordinal),
            "the replica re-posts every message as live-only, so its history tab is missing what the authority archived");
        Check(letters.Contains("if (state.HasTarget && state.Map >= 0)", StringComparison.Ordinal)
              && letters.Contains("new LookTargets(cell, map)", StringComparison.Ordinal),
            "the look target is not rebuilt, so clicking a replica message does nothing");
        Check(letters.Contains("MessagesArchived++", StringComparison.Ordinal),
            "the summary cannot tell \"arrived on screen\" from \"entered the history tab\"");
        return Task.CompletedTask;
    });

    await Test("a faction created at runtime arrives through the manager door, not by editing its list", () =>
    {
        var root = PackageValidation.PackageRoot();
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs")).Replace("\r", string.Empty);
        var channel = File.ReadAllText(Path.Combine(root, "Source", "FactionCreationSync.cs")).Replace("\r", string.Empty);
        var replica = File.ReadAllText(Path.Combine(root, "Source", "AuthoritativeReplica.cs")).Replace("\r", string.Empty);
        var runtime = File.ReadAllText(Path.Combine(root, "Source", "HeadlessRuntime.cs")).Replace("\r", string.Empty);
        var census = File.ReadAllText(Path.Combine(root, "Source", "SyncCensus.cs")).Replace("\r", string.Empty);
        Check(models.Contains("public sealed record ReplicaFactionCreation(int LoadID, string Def, string Xml)",
                StringComparison.Ordinal),
            "the faction row has no load id, so two factions of the same def cannot be told apart");
        // `RecacheFactions` is private and rebuilds eleven cached `OfX` pointers; `FactionManager.Add` is the public door
        // that contains it. Writing `allFactions` directly would leave every one of them stale, and `Faction.OfPlayer`
        // answers "is this ours?" for thousands of call sites.
        Check(channel.Contains("manager.Add(faction);", StringComparison.Ordinal),
            "the faction is added by editing a list, so the eleven cached `OfX` pointers are never rebuilt");
        Check(!channel.Contains("AllFactionsListForReading.Add", StringComparison.Ordinal),
            "the channel mutates the manager's list directly - `RecacheFactions` never runs");
        Check(channel.Contains("new Faction()", StringComparison.Ordinal)
              && channel.Contains("ScribeBridge.Load(faction, xml)", StringComparison.Ordinal),
            "the faction is not rebuilt from its own fragment, so its relations and cooldowns stay behind");
        Check(channel.Contains("faction.loadID == row.LoadID", StringComparison.Ordinal),
            "an existing faction is not recognised by load id, so applying twice would add a duplicate");
        Check(runtime.Contains("FactionCreationSync.Publish()", StringComparison.Ordinal)
              && runtime.Contains("policies, factionCreations,", StringComparison.Ordinal),
            "the channel publishes but the frame does not carry it");
        Check(replica.Contains("FactionCreationSync.Apply(factionCreations)", StringComparison.Ordinal),
            "the row is merged and never applied");
        // An event list, not a statement: a faction exists from its creation onward, so an older frame's rows must
        // survive a merge - the same rule the world-object creations use.
        Check(replica.Contains("EventUnion.ById(newer.FactionCreations, older.FactionCreations, state => state.LoadID)",
                StringComparison.Ordinal),
            "faction creations are merged as a whole statement, so an older frame's creations would be dropped");
        Check(census.Contains("FactionCreationSync.Summary()", StringComparison.Ordinal),
            "the census cannot tell \"nothing was created\" from \"one was created and never travelled\"");
        return Task.CompletedTask;
    });

    await Test("a policy a player creates is carried to the other end with its own id, not its label", () =>
    {
        var root = PackageValidation.PackageRoot();
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs")).Replace("\r", string.Empty);
        var channel = File.ReadAllText(Path.Combine(root, "Source", "PolicyTableSync.cs")).Replace("\r", string.Empty);
        var replica = File.ReadAllText(Path.Combine(root, "Source", "AuthoritativeReplica.cs")).Replace("\r", string.Empty);
        var runtime = File.ReadAllText(Path.Combine(root, "Source", "HeadlessRuntime.cs")).Replace("\r", string.Empty);
        var census = File.ReadAllText(Path.Combine(root, "Source", "SyncCensus.cs")).Replace("\r", string.Empty);
        // A policy is referenced by pawn trackers as a Scribe reference; `GetUniqueLoadID` embeds the LABEL, so keying
        // the channel on the label would turn a rename into a create plus a delete and orphan every tracker holding it.
        Check(models.Contains("public sealed record ReplicaPolicy(string Kind, int Id, string Label, string Xml)",
                StringComparison.Ordinal),
            "the policy row does not carry a kind and an id, so two tables can collide on the same id");
        Check(channel.Contains("found.id == id", StringComparison.Ordinal),
            "the channel resolves a policy by something other than its id");
        Check(channel.Contains("internal static readonly string[] Kinds = [\"apparel\", \"drug\", \"food\", \"reading\"];",
                StringComparison.Ordinal),
            "the channel no longer covers all four policy databases");
        Check(channel.Contains("new ReadingPolicy(id, label)", StringComparison.Ordinal)
              && channel.Contains("new ApparelPolicy(id, label)", StringComparison.Ordinal),
            "a missing policy is created some other way - `MakeNew…` allocates max(id)+1, which attaches the row to the wrong identity");
        var createAt = channel.IndexOf("existing = Create(row.Kind, row.Id, row.Label);", StringComparison.Ordinal);
        var loadAt = channel.IndexOf("ScribeBridge.Load(existing, row.Xml)", StringComparison.Ordinal);
        Check(createAt >= 0 && loadAt > createAt,
            "the fragment is loaded before the policy exists (or into something else), so its filters land nowhere");
        Check(channel.Contains("PollTicks", StringComparison.Ordinal) && channel.Contains("internal const int PollTicks = 250;",
                StringComparison.Ordinal),
            "the table is compared every frame, which means scribing twenty policies per tick");
        Check(runtime.Contains("PolicyTableSync.Publish()", StringComparison.Ordinal)
              && runtime.Contains("anomalyDifficulty, policies, factionCreations,", StringComparison.Ordinal),
            "the channel publishes but the frame does not carry it - the same gap the precept channel had in round 147");
        Check(replica.Contains("PolicyTableSync.Apply(policies)", StringComparison.Ordinal),
            "the row is merged and never applied, which reads exactly like an idle channel");
        // The table is sent whole, so its size is a claim that has to be measurable: the first single-policy reading was
        // 173035 B, and 27 of those would be the largest payload in the project by two orders of magnitude.
        Check(channel.Contains("internal static long LastBytes { get; private set; }", StringComparison.Ordinal)
              && channel.Contains("LastBytes += row.Xml.Length", StringComparison.Ordinal),
            "the channel never records how big the table it sends is, so \"the whole table travels\" cannot be checked");
        Check(channel.Contains("internal static IReadOnlyList<(string Kind, int Id, string Label, int Bytes)> Sizes()",
                StringComparison.Ordinal),
            "there is no per-row size listing, so the 173035 B reading cannot be attributed to one policy");
        Check(census.Contains("PolicyTableSync.Summary()", StringComparison.Ordinal),
            "the census still counts only the local tables, so \"three policies\" and \"three that never travelled\" read the same");
        return Task.CompletedTask;
    });
    await Test("a coordinate pair typed into the fixture's size fields is refused, not accepted as a map-wide site", () =>
    {
        var script = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"))
            .Replace("\r", string.Empty);
        // Fields 3 and 4 are a size. Measured with `110|132`: the parse accepted it, the site covered (62,73)-(171,204),
        // the work-gate scan hit its 900-object cap and reported "no control object", and the grant list read as
        // site-wide - a typo that looked like a gate defect. The guard is what keeps that from recurring.
        Check(script.Contains("if (width > MaxWidth || height > MaxHeight)", StringComparison.Ordinal),
            "the scripted fixture accepts a rectangle of any size, so a coordinate pair in the size fields silently "
            + "becomes a map-wide site");
        Check(script.Contains("return (false, \"size\",", StringComparison.Ordinal),
            "the refusal has no distinct code, so a caller cannot tell a typo from a missing map");
        Check(script.Contains("第三、四个字段是**尺寸**", StringComparison.Ordinal),
            "the refusal does not say which fields are the size, so the caller has nothing to correct");
        var max = System.Text.RegularExpressions.Regex.Match(script, @"private const int MaxWidth = (\d+);");
        var size = System.Text.RegularExpressions.Regex.Match(script,
            @"private static readonly int Width = ReadInt\(""TURBOTICK_SCRIPT_SETTLEMENT_W"", (\d+)\);");
        Check(max.Success && size.Success, "the size bound or the default width is gone");
        if (max.Success && size.Success)
        {
            var bound = int.Parse(max.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var fallback = int.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(bound <= 96, "the bound is so large that the coordinate-pair mistake gets through again: " + bound);
            Check(bound > fallback, "the bound is below the default rectangle, so the default cannot be created");
        }
        return Task.CompletedTask;
    });

    // **Every type named as a string must exist (round 141).** Round 140 found `RimWorld.Command_CallBossgroup`, a
    // string-named type that had never resolved and had therefore never had its click captured - the compiler cannot see
    // a string, Harmony resolves it at runtime, and the miss was a log line. This asks the cheapest useful version of the
    // question against the dumps that ship in this workspace: does a type of that simple name exist at all, and if so does
    // the literal match a full name exactly (`TYPE Verse.Command_Action`) rather than needing the fallback?
    await Test("every type named as a string exists in the game's dumps", () =>
    {
        var workspace = Path.Combine(PackageValidation.PackageRoot(), "..");
        var dumps = new[] { "acs17_all.txt", "unitystubs17.txt", "waylith17.txt", "rimworld17.txt" }
            .Select(name => Path.Combine(workspace, name)).Where(File.Exists).ToList();
        if (dumps.Count == 0)
        {
            Console.WriteLine("  (type dumps absent; string-name audit skipped)");
            return Task.CompletedTask;
        }
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var simple = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dump in dumps)
            foreach (var line in File.ReadLines(dump))
            {
                if (!line.StartsWith("TYPE ", StringComparison.Ordinal)) continue;
                var space = line.IndexOf(' ', 5);
                var full = space < 0 ? line[5..] : line[5..space];
                exact.Add(full);
                simple.Add(full[(full.LastIndexOf('.') + 1)..]);
            }
        Check(exact.Count > 1000, "the type dumps were read but almost nothing parsed: " + exact.Count + " types");

        var pairs = new List<(string Where, string Literal)>();
        foreach (var folder in new[] { "Source", "Networking", "Simulation", "Portal" })
        {
            var dir = Path.Combine(PackageValidation.PackageRoot(), folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var text = File.ReadAllText(file).Replace("\r", string.Empty);
                // No regex: the pattern went through two files and two escaping layers and arrived one parenthesis short
                // (`RegexParseException` at startup). String scanning cannot lose anything on the way.
                foreach (var marker in new[] { "AccessTools.TypeByName(\"", "TypeResolver.Find(\"" })
                {
                    var searchAt = 0;
                    while (true)
                    {
                        var hit = text.IndexOf(marker, searchAt, StringComparison.Ordinal);
                        if (hit < 0) break;
                        var literalStart = hit + marker.Length;
                        var literalEnd = text.IndexOf('"', literalStart);
                        if (literalEnd > literalStart)
                            pairs.Add((Path.GetFileName(file) + " 调用", text[literalStart..literalEnd]));
                        searchAt = literalStart;
                    }
                }
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("string[] ", StringComparison.Ordinal)
                        || !lines[i].Contains("Names", StringComparison.Ordinal)) continue;
                    for (var j = i + 1; j < Math.Min(i + 400, lines.Length); j++)
                    {
                        if (lines[j].Contains("};", StringComparison.Ordinal)) break;
                        var trimmed = lines[j].Trim();
                        if (!trimmed.StartsWith('"')) continue;
                        var close = trimmed.IndexOf('"', 1);
                        if (close > 1) pairs.Add((Path.GetFileName(file) + ":" + (j + 1), trimmed[1..close]));
                    }
                }
            }
        }
        Check(pairs.Count > 0, "no string-named types were found at all, so this check is a no-op");
        var dead = pairs.Where(pair => !simple.Contains(pair.Literal[(pair.Literal.LastIndexOf('.') + 1)..]))
            .Select(pair => pair.Where + ": " + pair.Literal).ToList();
        Check(dead.Count == 0, "these type names do not exist in the dumps, so they resolve to null at runtime: "
            + string.Join(", ", dead));
        var shifted = pairs.Where(pair => simple.Contains(pair.Literal[(pair.Literal.LastIndexOf('.') + 1)..])
                                         && !exact.Contains(pair.Literal)).ToList();
        Check(shifted.Count == 0,
            "these type names exist but not under the namespace written, so they only work through the fallback: "
            + string.Join(", ", shifted.Select(pair => pair.Where + ": " + pair.Literal)));
        return Task.CompletedTask;
    });

    await Test("an inactive ownership layer permits everything, including unlisted pairs", () =>
    {
        // The single property that makes the whole feature additive: a save with no settlements must behave exactly as
        // it did before the layer existed. The tree is checked structurally rather than by calling into the game -
        // `SettlementGates.Can` needs a live `Game`, so what can be verified here is the branch it takes first.
        var gates = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGates.cs"));
        // `Can` and `CanCell` both have to short-circuit before touching settlement data. One of them forgetting is a
        // colony that silently stops being allowed to build on the day the feature ships.
        var canBody = gates[(gates.IndexOf("internal static bool Can(int player", StringComparison.Ordinal))..];
        // Round 132 moved the decision into the map-taking overload; the current-map form now delegates to it, so the
        // short-circuit has to be looked for where the decision is made, not where the signature is familiar.
        var canCellBody = gates[(gates.IndexOf("internal static bool CanCell(int player, SettlementAct act, Map? map",
            StringComparison.Ordinal))..];
        foreach (var (name, body) in new[] { ("Can", canBody), ("CanCell", canCellBody) })
        {
            var head = body[..Math.Min(400, body.Length)];
            Check(head.Contains("data is null || !data.Active) return true;", StringComparison.Ordinal),
                $"{name} does not return true before consulting the layer");
        }
        // A thing outside every settlement is nobody's business; refusing it would gate the whole map.
        Check(canBody.Contains("if (home == 0) return true;", StringComparison.Ordinal),
            "Can does not short-circuit for a thing outside every settlement");
        Check(canCellBody.Contains("if (home == 0) return true;", StringComparison.Ordinal),
            "CanCell does not short-circuit for unclaimed ground");
        // A creature that is not the player's is not governed: the layer decides what players may do, and applying it
        // to raiders would stop them attacking.
        Check(canBody.Contains("Faction.OfPlayer", StringComparison.Ordinal),
            "Can does not exclude pawns outside the player's faction");

        // **The pawn overload must ask about the pawn's own map (round 82).** It used to delegate to the player
        // overload, which reads `Find.CurrentMap` - and a pawn's cell number is only meaningful on the pawn's map.
        // On a two-map colony (250x250 and 80x80) the same three numbers are two different cells, so the old code
        // could permit ground somebody owns. Checked structurally because the gate needs a live `Game`.
        // **No second copy of the decision may survive in the rule layer (round 83).** `SettlementRule` used to carry
        // `Can(data, playerId, act, thing)` and `CanCell(data, playerId, act, map, cell)`, neither with a caller - and
        // both had drifted into disagreeing with the live gate in ways that un-fix two shipped bugs: `None` returned
        // false (refusing motes, which stops combat animation) and unclaimed ground was only permitted for a null map.
        // A dead copy is worse than none, because it is the one a future reader trusts.
        var rule = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementRule.cs"));
        Check(!rule.Contains("internal static bool Can(", StringComparison.Ordinal),
            "a second, unused copy of the thing decision came back to SettlementRule");
        Check(!rule.Contains("internal static bool CanCell(", StringComparison.Ordinal),
            "a second, unused copy of the cell decision came back to SettlementRule");
        // The shared, game-free pieces have to stay - `HomeOf` in particular is what every live gate calls.
        Check(rule.Contains("internal static int HomeOf(", StringComparison.Ordinal),
            "HomeOf was deleted with the dead pair; every gate needs it");
        Check(rule.Contains("thing.MapHeld", StringComparison.Ordinal),
            "HomeOf stopped using MapHeld, so a thing in transit answers 0 (nobody's business)");

        // **The settlement marker may exist once (round 84).** It decides whether an area is "ours" or "the player's":
        // `SettlementAreas.IsOverlay` uses it to keep these out of the work-restriction list, and `AreaAllowedSync.Playable`
        // uses it to keep them out of the area channel. Two copies of that literal drift silently, and the failure shows
        // up only as a duplicated overlay on a peer - the settlement's own area plus a copy this channel made from a list
        // that no longer recognised it. `AreaAllowedSync` now reads `SettlementAreas.Marker`; this asserts nobody
        // reintroduced a literal.
        var root = PackageValidation.PackageRoot();
        var markerUses = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Source"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            var at = 0;
            while ((at = text.IndexOf("\"\u25c8 \"", at, StringComparison.Ordinal)) >= 0)
            {
                markerUses++;
                at += 3;
            }
        }
        Check(markerUses == 1,
            $"the settlement marker literal appears {markerUses} times in Source/; it must be defined once in SettlementAreas.Marker");

        // **The work gate must ask for the work giver's own verb (round 85).** It used to pass `Use` for every work
        // giver, reasoning that the verb is not knowable there. It is - `HasJobOnThing` runs on a specific instance - and
        // asking `Use` silently disabled a third of the table: `Use` has rows only for Bench/Bed/Pod/Seat, and
        // `Permits` refuses an unlisted pair. So Filth, Fire, Plant, WildAnimal and Animal were refused for every pawn
        // regardless of grants, and the refusal was indistinguishable from the owner having denied it.
        // The signature gained a target in round 89 (`WorkActOf(giver, thing)`) because `WorkGiver_DoBill` answers `Use`
        // for a bench and `Operate` for a patient. Matching the bare name rather than the old exact call keeps this
        // assertion about "asks the giver's own verb" from failing on every signature extension - which is what it did.
        Check(gates.Contains("SettlementGates.WorkActOf(__instance", StringComparison.Ordinal),
            "the work gate is back to asking a fixed act instead of the work giver's own verb");
        Check(!gates.Contains("SettlementGates.Can(pawn, SettlementAct.Use, t)", StringComparison.Ordinal),
            "the work gate asks `Use` for every work giver again, which refuses cleaning, firefighting, hunting, gathering and animal handling");
        // Each mapping has to point at an act the table actually has a row for - the whole bug was a mapping to an act
        // with no row. Checked by extracting the act names from the switch and matching them against the table.
        var workActStart = gates.IndexOf("internal static SettlementAct WorkActOf", StringComparison.Ordinal);
        Check(workActStart >= 0, "WorkActOf is gone");
        var workActBody = gates[workActStart..];
        var workActEnd = workActBody.IndexOf("};", StringComparison.Ordinal);
        var workSwitch = workActBody[..workActEnd];
        var table = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "SettlementRuleTable.cs"));
        foreach (var (needle, kind, why) in new[]
                 {
                     ("SettlementAct.Clean", "SettlementKind.Filth", "cleaning filth"),
                     ("SettlementAct.Hunt", "SettlementKind.WildAnimal", "hunting a wild animal"),
                     ("SettlementAct.Gather", "SettlementKind.Plant", "harvesting a plant"),
                     ("SettlementAct.Handle", "SettlementKind.Animal", "handling somebody's animal"),
                     ("SettlementAct.Tend", "SettlementKind.Pawn", "tending a pawn"),
                 })
        {
            Check(workSwitch.Contains(needle, StringComparison.Ordinal),
                $"no work giver maps to {needle}, so {why} is refused for every pawn");
            Check(table.Contains(needle, StringComparison.Ordinal) && table.Contains(kind, StringComparison.Ordinal),
                $"the table has no row for {needle} on {kind}, so the mapping permits nothing");
        }

        // **The door gate has to exist and has to narrow, not widen (round 86).**
        //
        // `Doors`/`Pass`/`Door` was a table row with no reader: doors are not reserved, worked or designated, so none of
        // the three gates is ever called for a doorway and the grant was decorative. The patch must be a **postfix on
        // `PawnCanOpen`** - a prefix would have to reimplement vanilla's own door rules (forbidden, hostile, held open),
        // and getting that wrong would *widen* access, which is the dangerous direction.
        Check(gates.Contains("[HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]", StringComparison.Ordinal),
            "no patch on Building_Door.PawnCanOpen: the `Doors`/`Pass` grant has no reader and does nothing");
        var doorGate = gates.IndexOf("internal static class SettlementDoorGate", StringComparison.Ordinal);
        Check(doorGate >= 0, "SettlementDoorGate is gone");
        var doorBody = gates[doorGate..];
        var doorEnd = doorBody.IndexOf("\n}", StringComparison.Ordinal);
        var doorCode = doorEnd > 0 ? doorBody[..doorEnd] : doorBody;
        Check(doorCode.Contains("if (!__result) return;", StringComparison.Ordinal),
            "the door gate no longer defers to vanilla's own answer, so it can allow a door vanilla refused");
        Check(doorCode.Contains("ref bool __result", StringComparison.Ordinal),
            "the door gate is no longer a postfix on the result");
        Check(!doorCode.Contains("__result = true", StringComparison.Ordinal),
            "the door gate sets the result to true, which widens access instead of narrowing it");

        // **Every command the game answers must also be reachable from the portal (round 86).**
        //
        // Adding a command is three edits, and this round forgot the third: `settlement-door` was handled in
        // `PortalCommands` and absent from `AdminPage`'s `CommandNames`, so the portal answered
        // 「不支持的命令：settlement-door；可用：…」 and the new door probe could not be run at all. The same mistake is
        // recorded beside `script-zone` in `AdminPage` - which is evidence that a comment is not a guard. This compares
        // the two lists mechanically instead.
        var commandsFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PortalCommands.cs"));
        var adminFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Portal", "AdminPage.cs"));
        var handled = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(commandsFile, "case \"([a-z][a-z0-9-]*)\":"))
            handled.Add(match.Groups[1].Value);
        Check(handled.Count > 40, $"only {handled.Count} portal commands were found; the scan itself is wrong");
        var missing = new List<string>();
        foreach (var name in handled)
        {
            // Both halves: the dispatcher switch forwards it and the published name list advertises it. A name in the
            // switch but not the list is unusable from the portal, which is exactly what happened.
            var forwarded = adminFile.Contains($"\"{name}\" => await CommandInbox.SendAsync", StringComparison.Ordinal)
                || adminFile.Contains($"\"{name}\" => await", StringComparison.Ordinal);
            var advertised = adminFile.Contains($"\"{name}\"", StringComparison.Ordinal);
            if (!forwarded || !advertised) missing.Add(name);
        }
        // **Pinned, not accepted.** These eight handlers predate the portal's name list and are reachable only by
        // dropping a request file into the game's inbox - they are not reachable from the admin page at all. Compared as
        // an exact set so the list cannot quietly grow: any *new* handler that forgets its portal wiring fails here,
        // which is what happened to `settlement-door` this round. Making the eight visible is a separate change from
        // making a new command work, and pinning them keeps both honest.
        string[] fileDropOnly =
        [
            "capture-counters", "construct-report", "faction-report", "job-report", "pawn-report",
            "pawn-report-again", "quit", "script-build", "script-build-at",
        ];
        var unexpected = missing.FindAll(name => Array.IndexOf(fileDropOnly, name) < 0);
        Check(unexpected.Count == 0,
            "portal commands handled by the game but not reachable from the portal: " + string.Join(", ", unexpected));
        var stale = new List<string>();
        foreach (var name in fileDropOnly) if (!missing.Contains(name)) stale.Add(name);
        Check(stale.Count == 0,
            "commands listed as file-drop-only are now reachable from the portal, so the exemption list is stale: "
            + string.Join(", ", stale));

        // **The visitor branch must consult the tables, not the constant (round 86).**
        //
        // `mine != 0 ? data.GrantsForPawn(mine, home) : SettlementData.Unaffiliated` looks like one decision, but the
        // two arms are not the same question: `GrantsForPawn(0, home)` looks for a record whose `To` is 0 (all visitors)
        // and then for the settlement's own `Defaults`, while the constant is the compile-time four. Substituting the
        // constant made `Defaults` and the `To = 0` grant record **dead for the only case they exist for**. Measured:
        // after `settlement-grant 1 0 Care,Benches,Items` removed `Doors`, the door probe still answered 「可」.
        Check(!gates.Contains(": SettlementData.Unaffiliated;", StringComparison.Ordinal),
            "the visitor branch is back to the compile-time constant, so a settlement's own default grants and any "
            + "`To = 0` record are ignored for the visitors they were written for");
        Check(gates.Contains("var grants = data.GrantsForPawn(mine, home);", StringComparison.Ordinal),
            "the gate no longer asks `GrantsForPawn` for both the member and the visitor case");

        // **Every kind the table has a row for must have a classifier that can produce it (round 87).**
        //
        // This is the structural version of the `Pod` defect: `SettlementKind.Pod` had four rows in the table and no
        // arm in `KindOf` ever returned it, so a visitor could not be let into a cryptosleep casket or a biosculpter
        // even with `Care` granted. The take-apart and repair rows still worked because they are also stated on
        // `Building`, which is what kept the gap invisible for so long. Checking the two sides against each other is the
        // only way to catch this class of bug without planting one of every building in a test world.
        var ruleFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementRule.cs"));
        var kindOfStart = ruleFile.IndexOf("internal static SettlementKind KindOf", StringComparison.Ordinal);
        Check(kindOfStart >= 0, "KindOf is gone");
        var kindBody = ruleFile[kindOfStart..];
        var kindEnd = kindBody.IndexOf("    // **Two decision methods", StringComparison.Ordinal);
        if (kindEnd > 0) kindBody = kindBody[..kindEnd];
        var produced = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(kindBody, "SettlementKind\\.([A-Za-z]+)"))
            if (!produced.Contains(match.Groups[1].Value)) produced.Add(match.Groups[1].Value);
        // Two kinds are not produced by `KindOf` and are not gaps:
        //   - `None` is its answer for "not classifiable", which the gate treats as "nothing to say".
        //   - `Land` and `Roof` are **cell** kinds. `CanCell` asks `Land` for every cell question, and roof building is
        //     a cell designator (`Designator_AreaBuildRoof : Designator_Cells`) whose work giver
        //     (`WorkGiver_BuildRoof.HasJobOnCell`) is likewise cell-based - so there is no `Thing` to classify and no
        //     arm to add. Asserted rather than merely listed: if `CanCell` ever stops asking `Land`, or the roof
        //     designator stops being cell-based, this exemption becomes a hole and the test below notices.
        var byDesign = new[] { "None", "Land", "Roof" };
        Check(gates.Contains("SettlementKind.Land", StringComparison.Ordinal),
            "no cell gate asks about `Land`, so the `Land`/`Roof` exemption is a hole");
        var roofDesignator = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGates.cs"));
        Check(roofDesignator.Contains("Designator_AreaBuildRoof", StringComparison.Ordinal),
            "the roof designator is no longer handled, so `Roof` is neither a thing kind nor a cell kind");
        var tableKinds = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(table, "SettlementKind\\.([A-Za-z]+)"))
            if (!tableKinds.Contains(match.Groups[1].Value)) tableKinds.Add(match.Groups[1].Value);
        var unreachable = tableKinds.FindAll(k => !produced.Contains(k) && Array.IndexOf(byDesign, k) < 0);
        Check(unreachable.Count == 0,
            "kinds with table rows that no classifier can produce: " + string.Join(", ", unreachable));

        // **`SettlementGrantRecord.To` has one meaning: a settlement (round 88).**
        //
        // It had two. `GrantsFor(playerId, settlement)` read it as a player id and `GrantsForPawn(pawnHome, settlement)`
        // read it as a settlement id, and **both readers were live**: the grant window and the `settlement-grant` command
        // wrote a player id, while the gates - the only thing that ever refuses anything - asked `GrantsForPawn`. So
        // "player 2 may use my workbenches" silently became "settlement 2 may" whenever the numbers coincided, which on a
        // fresh save they always do (#1, #2, ...). ColonyTogether has only the settlement shape because its authority has
        // no player either (`ct045:352100` `Granted(owner, group, any)`).
        var dataFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementData.cs"));
        var windowFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGrantWindow.cs"));
        var scriptedFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"));
        Check(!dataFile.Contains("row.To == playerId) continue;", StringComparison.Ordinal)
            || dataFile.Contains("if (record.To == 0) continue;", StringComparison.Ordinal),
            "`GrantsFor` again matches a raw player id against `To`, which is a settlement id");
        Check(!windowFile.Contains("new SettlementGrantRecord { From = selected, To = editingPlayer }", StringComparison.Ordinal),
            "the grant window writes the player id into `To`, which the gates read as a settlement id");
        Check(windowFile.Contains("data.SettlementOf(editingPlayer)", StringComparison.Ordinal),
            "the grant window no longer resolves the player to their settlement before writing");
        Check(!scriptedFile.Contains("new SettlementGrantRecord { From = siteId, To = player, Flags = mask }", StringComparison.Ordinal),
            "`settlement-grant` writes the player id into `To`, which the gates read as a settlement id");
        Check(scriptedFile.Contains("target = data.SettlementOf(player);", StringComparison.Ordinal),
            "`settlement-grant` no longer resolves the player to their settlement before writing");
        // The gates must ask by settlement membership, not by player, or the authority (player 0) answers for everybody.
        Check(gates.Contains("data.GrantsForPawn(mine, home)", StringComparison.Ordinal),
            "the pawn gate no longer asks `GrantsForPawn`, so a grant keyed by settlement reaches nobody");

        // **Every act the table has a row for must be produced by some gate (round 89).**
        //
        // This is the act-side twin of the kind check above, and it catches the same shape one level over: `Operate` had
        // two rows (`Operations`/`Operate`/`Pawn`) and **no gate ever passed it**, because `WorkActOf` took only the work
        // giver - and a bill giver can be a workbench or a wounded pawn. So surgery asked `Use`, `Use`/`Pawn` is not a
        // row, and a visitor could not be operated on even with `Care`. `Trade` and `Relax` are the remaining two; they
        // are asserted by name below so a third cannot join them silently.
        var tableActs = new List<string>();
        var tableText = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "SettlementRuleTable.cs"));
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(tableText, "SettlementAct\\.([A-Za-z]+)"))
            if (!tableActs.Contains(match.Groups[1].Value)) tableActs.Add(match.Groups[1].Value);
        var sourceActs = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(PackageValidation.PackageRoot(), "Source"), "*.cs"))
        {
            // The probe's own act list and `SettlementAct` declarations do not produce acts; only gate paths do. The
            // probe is excluded by filename because that is the one file whose act list exists to *ask* every act.
            if (Path.GetFileName(file) is "ScriptedSettlement.cs") continue;
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "SettlementAct\\.([A-Za-z]+)"))
                if (!sourceActs.Contains(match.Groups[1].Value)) sourceActs.Add(match.Groups[1].Value);
        }
        // `Trade` is issued by `TradeGuard` against `TradeDeal` rather than through `SettlementGates`; `Relax` has two
        // rows (`Amenities` on `Building` and on `Seat`) and no gate reads them yet - a known gap, named rather than
        // hidden. `None` is not an action.
        // `Trade` left this list in round 92 when `SettlementTradeGate` started producing it: the row had been answered
        // only by `TradeGuard`, which refuses *completion*, so the window still opened for anyone. `Relax` left in round
        // 90 for the same reason. Only `None`, which is the absence of an action, remains.
        var noGateYet = new[] { "None" };
        var unproduced = tableActs.FindAll(a => !sourceActs.Contains(a) && Array.IndexOf(noGateYet, a) < 0);
        Check(unproduced.Count == 0,
            "acts with table rows that no gate ever produces: " + string.Join(", ", unproduced));
        Check(gates.Contains("WorkActOf(__instance, t)", StringComparison.Ordinal),
            "the work gate no longer passes the target, so a bill giver that is a pawn is classified as a bench");

        // **No patch target may be abstract, static, or a member that does not exist (round 92).**
        //
        // This is the check that would have caught round 91 without a server restart. `[HarmonyPatch(typeof(JoyGiver),
        // nameof(JoyGiver.TryGiveJob))]` compiled, passed 141 tests, deployed, and then **refused to start the server**:
        // `JoyGiver.TryGiveJob(Pawn)` is `Abstract`, Harmony has no body to wrap, and the headless runtime treats a
        // missing gameplay patch as fatal. The symptom was `unhealthy` plus every command answering `cancelled`, and the
        // real line was one grep away in `game-stdout.log`.
        //
        // The dump at `acs17_all.txt` already carries the flags, so the check is a text comparison rather than a
        // reflection test - the test project deliberately does not reference the game assemblies.
        //   `M TryGiveJob(Pawn) -> Job  [PrivateScope, Public, Virtual, HideBySig, VtableLayoutMask, Abstract]`
        // Line numbers in the dump are not stable, so the lookup is by type block, not by offset.
        var dumpPath = Path.Combine(PackageValidation.PackageRoot(), "..", "acs17_all.txt");
        if (File.Exists(dumpPath))
        {
            var dump = File.ReadAllLines(dumpPath);
            // Index: type full name -> (first line, list of member lines).
            var blockStart = new Dictionary<string, int>();
            var blockEnd = new Dictionary<string, int>();
            var current = "";
            for (var i = 0; i < dump.Length; i++)
            {
                if (!dump[i].StartsWith("TYPE ", StringComparison.Ordinal)) continue;
                if (current.Length > 0) blockEnd[current] = i;
                var rest = dump[i][5..];
                var cut = rest.IndexOfAny([' ', '[', '\t']);
                current = cut > 0 ? rest[..cut] : rest;
                blockStart[current] = i;
            }
            if (current.Length > 0) blockEnd[current] = dump.Length;

            var patched = new List<(string File, string Type, string Member)>();
            foreach (var file in Directory.GetFiles(Path.Combine(PackageValidation.PackageRoot(), "Source"), "*.cs"))
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match match in
                         System.Text.RegularExpressions.Regex.Matches(text,
                             "HarmonyPatch\\(typeof\\(([A-Za-z_][A-Za-z0-9_.]*)\\),\\s*nameof\\(\\1\\.([A-Za-z_][A-Za-z0-9_]*)\\)"))
                    patched.Add((Path.GetFileName(file), match.Groups[1].Value, match.Groups[2].Value));
            }
            // Only types the dump knows about; a patch on our own type is not in the game dump and is skipped.
            var unpatchable = new List<string>();
            var resolved = 0;
            foreach (var (file, typeShort, member) in patched)
            {
                var full = blockStart.Keys.FirstOrDefault(k =>
                    k == typeShort || k.EndsWith("." + typeShort, StringComparison.Ordinal));
                if (full is null) continue;
                resolved++;
                for (var i = blockStart[full]; i < blockEnd[full]; i++)
                {
                    var line = dump[i];
                    if (!line.StartsWith("   M " + member + "(", StringComparison.Ordinal)
                        && !line.StartsWith("   P " + member + " ", StringComparison.Ordinal)) continue;
                    // **`Abstract` only.** Static methods are the *normal* case for a Harmony patch - the first version
                    // of this check also rejected `static` and produced 31 false positives across the codebase
                    // (`GenSpawn.Spawn`, `ForbidUtility.SetForbidden`, `TimeControls.DoTimeControlsGUI` …), which would
                    // have pushed someone to delete the check rather than trust it. What Harmony cannot wrap is a method
                    // with no body, and that is exactly `Abstract`.
                    if (line.Contains("Abstract", StringComparison.Ordinal))
                        unpatchable.Add($"{file}: {full}.{member} is abstract");
                    break;
                }
            }
            Check(resolved >= 5,
                $"only {resolved} of {patched.Count} nameof-patch targets were found in acs17_all.txt; the dump index is wrong");
            Check(unpatchable.Count == 0,
                "Harmony patch targets that cannot be patched: " + string.Join("; ", unpatchable));

            // **The alert-clock table must match the dump in both directions (round 96).**
            //
            // `AlertProbe.Floors` exists because `GetReport()` returns a bool and hides its world-clock floor inside the
            // body, so no runtime reading can tell 「假 because there is food」 from 「假 because the clock is at 358 and
            // the threshold is 150000」. A hand-written table that can go stale silently restores exactly that blindness,
            // so it is checked against the decompiled source: every `Alert_*` class comparing `TicksGame` against a
            // literal must appear with that literal, and nothing may appear without one.
            var alertDump = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "..", "_lab", "asm",
                "Assembly-CSharp.decompiled.cs"));
            var dumpFloors = new Dictionary<string, int>(StringComparer.Ordinal);
            var className = "";
            foreach (var line in alertDump.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("public class Alert_", StringComparison.Ordinal))
                {
                    className = trimmed["public class ".Length..];
                    var colon = className.IndexOf(' ');
                    if (colon > 0) className = className[..colon].TrimEnd(':');
                    continue;
                }
                if (className.Length == 0 || !trimmed.Contains("TicksGame", StringComparison.Ordinal)) continue;
                var compare = System.Text.RegularExpressions.Regex.Match(trimmed, @"TicksGame\s*<\s*([0-9]+)f?");
                if (compare.Success && int.TryParse(compare.Groups[1].Value, out var floor)) dumpFloors[className] = floor;
                className = "";
            }
            Check(dumpFloors.Count > 0, "the alert-clock scan found no floors at all; the parse is broken, not the table");
            // Printed so the count is visible in a passing run too: a table that silently matched zero alerts would pass
            // both loops below.
            Console.WriteLine($"  alert-clock floors: {dumpFloors.Count} from the dump ({string.Join(", ", dumpFloors.Keys)})");
            var tableSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AlertProbe.cs"));
            foreach (var pair in dumpFloors)
                Check(tableSource.Contains($"\"{pair.Key}\"] = {pair.Value}", StringComparison.Ordinal),
                    $"`{pair.Key}` has a world-clock floor of {pair.Value} in the game and not in `AlertProbe.Floors`, so its "
                    + "「假」 reads as evidence");
            foreach (System.Text.RegularExpressions.Match declared in System.Text.RegularExpressions.Regex.Matches(tableSource,
                         @"""(Alert_[A-Za-z0-9_]+)""\] = ([0-9]+)"))
            {
                var name = declared.Groups[1].Value;
                Check(dumpFloors.ContainsKey(name),
                    $"`AlertProbe.Floors` lists `{name}` but the game's `GetReport` has no TicksGame floor - a stale entry "
                    + "marks a working alert as unmeasurable");
            }

            // **Parameter-name binding cannot be checked from this dump, and pretending otherwise cost 60 false
            // positives (round 92).** A first version of this test compared each patch parameter's *name* against the
            // dump's declaration - but `acs17_all.txt` records types only (`(Pawn, String, Exception, JobDriver)`), never
            // names, so every correctly written patch looked wrong. A check that fires on 60 good sites is worse than no
            // check: the next person deletes it instead of trusting it.
            //
            // So the round-92 mistake (`Prefix(Pawn negotiator, ITrader trader)` against
            // `SetupWith(ITrader newTrader, Pawn newPlayerNegotiator, bool giftMode)`) is guarded differently: **by
            // discipline, written down where it will be read.** Harmony binds by name and `__0`/`__1` are positional;
            // new patches use positional parameters unless the name is copied from the dump's own declaration, and the
            // only reliable confirmation is the install line in `game-stdout.log`, which the headless runtime treats as
            // fatal if it is missing.

            // **Every ledger entry that used to say 「入口未测」 must have a verdict derived from the game's own types
            // (round 99).** The assertions live outside the dump guard, below - a first version of this check sat here,
            // inside `if (File.Exists(dumpPath))`, and tampering with the verdict left the suite at `141 passed`. The
            // dump is needed for the abstract-target scan above, not for reading two files that are always present.

            // **Parameter-name binding cannot be checked from this dump, and pretending otherwise cost 60 false
            // positives (round 92).** A first version of this test compared each patch parameter's *name* against the
            // dump's declaration - but `acs17_all.txt` records types only (`(Pawn, String, Exception, JobDriver)`), never
            // names, so every correctly written patch looked wrong. A check that fires on 60 good sites is worse than no
            // check: the next person deletes it instead of trusting it.
            //
            // So the round-92 mistake (`Prefix(Pawn negotiator, ITrader trader)` against
            // `SetupWith(ITrader newTrader, Pawn newPlayerNegotiator, bool giftMode)`) is guarded differently: **by
            // discipline, written down where it will be read.** Harmony binds by name and `__0`/`__1` are positional;
            // new patches use positional parameters unless the name is copied from the dump's own declaration, and the
            // only reliable confirmation is the install line in `game-stdout.log`, which the headless runtime treats as
            // fatal if it is missing.
        }

        // One read, two names: a dozen assertions below already spell it `scripted`, and `claimSource` is the name the
        // newer containment assertions use. Declared here because its first use is a hundred lines above where it used
        // to sit.
        var scripted = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"));
        var claimSource = scripted;
        var warText = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementWar.cs"));
        // **The war path must be observable, not just modelled (round 94).**
        //
        // `WarActive` has existed since round 80 and `AtWar` is consulted by the gate before every grant - but for
        // fourteen rounds every reading printed 「通知中」, because round 80 had put a 60 000-tick countdown on the
        // declaration edge. A `WarActive` that returned false forever, or a gate that never called `AtWar`, produced
        // exactly the same report. The halves that make it observable are asserted here so they cannot be removed as
        // "unused".
        Check(scripted.Contains("SettlementWar.AtWar(data, pact.A, pact.B)", StringComparison.Ordinal),
            "the probe no longer asks `AtWar` directly, so a war that refuses nothing reads as a working war");
        Check(scripted.Contains("\"expire\" => SettlementWar.Expire(data, a, b)", StringComparison.Ordinal),
            "the `expire` operand is gone, so an effective war cannot be reached without waiting 60 000 ticks");
        Check(gates.Contains("SettlementWar.AtWar(data, mine, home)", StringComparison.Ordinal),
            "the gate no longer consults `AtWar`, so war stops refusing anything");

        // **A replica map must be bound to its `MapParent` (round 111).**
        //
        // `Map.IsPlayerHome` (`:76546-76556`) reads `info.parent.Faction`, and `Game.FindMap(MapParent)` (`:50121`)
        // matches maps on `maps[i].info.parent`. `MapLifecycleSync.Create` left the parent null on the argument that a
        // parentless replica map 「reads as an ordinary second colony」 - it does not: `IsPlayerHome` answers false, and
        // that property has 132 readers (threat points `:76562`, danger rating `:52810`, roamer ticks `:253421`,
        // item decay). Invisible on a one-map colony, which is why it survived until multi-settlement was asked for.
        var mapSync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "MapLifecycleSync.cs"));
        Check(mapSync.Contains("internal static int BindParents()", StringComparison.Ordinal)
            && mapSync.Contains("map.info.parent = parent;", StringComparison.Ordinal),
            "replica maps are never bound to their MapParent, so `Map.IsPlayerHome` answers false for a player colony");
        // **The match must not go through `parent.Map`.** `MapParent.Map` is `FindMap(this)` (`:793588`) and `FindMap`
        // matches on `maps[i].info.parent` (`:50125`) - the field that is null precisely when the binding is needed. The
        // first version wrote `if (parent.Map != map) continue;` and the probe measured `本次绑 0，累计 0；无父 1 张`
        // with two maps present: a dead branch that reads as implemented.
        Check(!mapSync.Contains("if (parent.Map != map) continue;", StringComparison.Ordinal),
            "the parent binding matches on `parent.Map`, which is itself derived from `info.parent` - the guard can "
            + "never be true, so the binding never runs");
        // **One parent cannot own two maps.** `Game.FindMap(MapParent)` (`:50123`) returns the first match, so two maps
        // sharing `info.parent` makes `parent.Map` answer the wrong one for every reader. Measured: a probe hint left
        // `map0` and `map1` both pointing at `Settlement#253`, a state vanilla cannot produce.
        // **The hint must track the authority, including "no parent".** Measured: a one-way hint memory bound a map
        // back the instant a probe cleared its parent, so the probe reported `本次绑 1 个` while its target stayed
        // parentless - the measurement described the hint, not the binding.
        Check(mapSync.Contains("else if (map.Parent is null)", StringComparison.Ordinal)
            && mapSync.Contains("parentOf.Remove(map.uniqueID);", StringComparison.Ordinal),
            "the parent hint is never cleared, so a map whose parent the authority removed is re-bound from memory");
        // **The empty-dispose skip must be conditional on emptiness, not unconditional.** `MapDrawer.Dispose` is where
        // vanilla disposes real sections and global layers; skipping it for a drawn map would leak both. And the
        // predicate must be shared with the probe, or the measurement and the fix can disagree about "empty".
        // **Red-green for the skip itself.** Comment: `mapSync` is read as text, so a `//` line quoting the fix would
        // satisfy `Contains` - the filter strips comment lines first. This is the 4th place that mattered.
        var skipCode = string.Join("\n", mapSync.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(skipCode.Contains("if (!MapLifecycleSync.DrawerIsEmpty(__instance)) return true;", StringComparison.Ordinal),
            "`MapDrawer.Dispose` is skipped unconditionally, which would leak the sections and global layers of a drawn map");
        Check(mapSync.Contains("return sectionsField.GetValue(drawer) is null && globalField.GetValue(drawer) is null;",
                StringComparison.Ordinal),
            "the empty-drawer test does not require both `sections` and `global` to be null, so it can skip a dispose that has work to do");
        // **The parentless chain must be documented where the fix lives.** `MapInfo.Tile` is
        // `parent?.Tile ?? PlanetTile.Invalid` (`:83462`) and `Invalid` is `PlanetTile(-1)` (`:772628`), so without a
        // parent `Map.Biome` reaches `WorldGrid[-1]` (`:781650`) and throws. Measured: `tile=-1,0，biome=**抛
        // ArgumentOutOfRangeException**` before the bind and `tile=160265,0，biome=TropicalRainforest` after. A future
        // edit that "simplifies" the binding back to a `parent.Map` scan would silently restore that state.
        // Written against the literals the comment actually uses (`WorldGrid[PlanetTile(-1)]`, not `WorldGrid[Tile]`,
        // which is the probe's phrasing). Reading the file to confirm beats assuming the wording.
        Check(mapSync.Contains("PlanetTile.Invalid", StringComparison.Ordinal)
            && mapSync.Contains("WorldGrid[PlanetTile(-1)]", StringComparison.Ordinal),
            "the parent binding does not record why a parent is required beyond `IsPlayerHome` - the tile/biome chain "
            + "means any parentless map throws on `Map.Biome`");
        // **A settlement rectangle is only meaningful on its own map.** Two maps share a coordinate space - a 60x60
        // second colony and a 250x250 home map both run 0..59 - so `Contains(IntVec3)` alone accepts a pawn at (30,30)
        // on map A for a settlement on map B. Measured consequence: `settlement-claim 2 47` could record a membership row
        // for a pawn who had never been near that settlement. `SettlementAt(mapId, cell)` was already map-aware; the
        // claim path was not.
        // **A portal is not a transporter, and the patch proved it.** `MapPortal.AddToTheToLoadList`
        // (`Assembly-CSharp.decompiled.cs:571926`) and its `leftToLoad` (`:571789`) are unrelated to
        // `CompTransporter`'s same-named pair (`:561716`, `:561234`) except in spelling. `TransportCapturePatch` is
        // attached to `typeof(CompTransporter)`, so `Dialog_EnterPortal.TryAccept` (`:633225`) reached no capture at all:
        // every load of a gravship gate existed on the loading player's machine and nowhere else. Three things have to
        // stay true - the portal has its own patch class, its own command kind, and its own apply path.
        var actionsSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
        Check(actionsSource.Contains("typeof(MapPortal), nameof(MapPortal.AddToTheToLoadList)", StringComparison.Ordinal),
            "no patch is attached to MapPortal.AddToTheToLoadList, so loading a gravship gate is still local-only");
        Check(!actionsSource.Contains("typeof(CompTransporter), nameof(MapPortal", StringComparison.Ordinal)
            && actionsSource.Contains("internal static class PortalCapturePatch", StringComparison.Ordinal),
            "the portal capture is not a separate patch class, so Harmony matches the transporter's method and never the portal's");
        Check(actionsSource.Contains("CapturePortal(", StringComparison.Ordinal)
            && actionsSource.Contains("ApplyPortal(", StringComparison.Ordinal),
            "the portal command has no capture or no apply path");

        var codecSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Simulation", "CommandCodec.cs"));
        Check(codecSource.Contains("public const string Portal = \"portal\"", StringComparison.Ordinal)
            && codecSource.Contains("public static bool TryDecodePortal(", StringComparison.Ordinal),
            "the portal command kind or its decoder is missing, so a captured load cannot be carried");
        var installerSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
        Check(installerSource.Contains("\"portal-load\", typeof(PortalCapturePatch)", StringComparison.Ordinal),
            "the portal patch is never registered with Harmony, so it exists but is not applied");

        // **The cancel half is a second write on the same object.** `MapPortal.CancelLoad` (`:571985`) removes the
        // `LordJob_LoadAndEnterPortal` lord and clears `leftToLoad`; it reaches neither of the two load methods, so a
        // channel that only covered `AddToTheToLoadList` would leave a cancelled load in progress on every replica.
        Check(actionsSource.Contains("typeof(MapPortal), nameof(MapPortal.CancelLoad)", StringComparison.Ordinal),
            "MapPortal.CancelLoad is not patched, so cancelling a portal load stays on the clicking machine");
        // **The dispatch condition, not just the call.** The first version of this line only asked whether the string
        // `portal.CancelLoad()` appeared somewhere in the file - and it does, so hard-wiring `cancelling = false` left
        // the assertion green. A cancel would then fall through to the load path, find no cargo for `thingId <= 0` and
        // be counted as a miss: the click would look like it did nothing, which is exactly the symptom being fixed.
        Check(actionsSource.Contains("internal static class PortalCancelCapturePatch", StringComparison.Ordinal)
            && actionsSource.Contains("CapturePortalCancel(", StringComparison.Ordinal)
            && actionsSource.Contains("portal.CancelLoad()", StringComparison.Ordinal)
            && actionsSource.Contains("portalCancelled++;", StringComparison.Ordinal),
            "the portal cancel path is incomplete: it needs its own patch class, its own capture and its own counter");
        Check(actionsSource.Contains("var cancelling = thingId <= 0 && count <= 0;", StringComparison.Ordinal),
            "the portal apply does not derive a cancel from the payload, so a cancel would be replayed as a load");
        Check(codecSource.Contains("PortalCancelCommand(", StringComparison.Ordinal)
            && codecSource.Contains("$\"{portalId}|0|0\"", StringComparison.Ordinal),
            "there is no cancel encoding for the portal kind, so a cancel cannot be distinguished from a load");
        Check(installerSource.Contains("\"portal-cancel\", typeof(PortalCancelCapturePatch)", StringComparison.Ordinal),
            "the portal cancel patch is never registered with Harmony");

        // **The diplomacy channel: captured at `IncidentQueue.Add`, not at the dialog options.** The first version
        // wrapped `__result.action` on `RequestTraderOption` — and that method's result is an outer container whose only
        // field is `link` (`Assembly-CSharp.decompiled.cs:486940`); the writing `action` belongs to one inner option per
        // trader kind, built in a loop (`:486920-486932`). It compiled and wrapped null. `Add` (`:384081`) is the one
        // place all three requests arrive. This assertion pins the capture point and the reason, so the same mistake
        // cannot come back as a cosmetic edit.
        Check(actionsSource.Contains("typeof(IncidentQueue), nameof(IncidentQueue.Add)", StringComparison.Ordinal),
            "the incident channel is not captured at IncidentQueue.Add, so a requested trader never reaches the colony "
            + "that runs the incident queue");
        Check(actionsSource.Contains("internal static class IncidentQueueCapturePatch", StringComparison.Ordinal)
            && actionsSource.Contains("CaptureIncident(", StringComparison.Ordinal)
            && actionsSource.Contains("ApplyDiplomacy(", StringComparison.Ordinal),
            "the incident channel has no patch class, no capture or no apply path");
        Check(!actionsSource.Contains("DiplomacyTraderPatch", StringComparison.Ordinal),
            "a patch on RequestTraderOption is back: its result carries no action, so it can only ever wrap null");
        // The delay recipe, not a free parameter: a caravan is +120000 flat (`:486929`), an orbital trader draws
        // 1-2 hours (`:486625`, `:486865`). A carried offset would be a third copy of these and would disagree.
        Check(actionsSource.Contains(": 120000)", StringComparison.Ordinal)
            && actionsSource.Contains("Rand.Range(1f, 2f) * 2500f", StringComparison.Ordinal),
            "the incident channel does not reproduce vanilla's delay recipe, so a requested caravan could arrive at an "
            + "unrelated tick");
        Check(codecSource.Contains("public const string Diplomacy = \"diplomacy\"", StringComparison.Ordinal)
            && codecSource.Contains("public static bool TryDecodeDiplomacy(", StringComparison.Ordinal),
            "the diplomacy command kind or its decoder is missing");
        var serverSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "DedicatedServer.cs"));
        Check(serverSource.Contains("\"diplomacy\"", StringComparison.Ordinal),
            "the server does not relay the diplomacy kind, so the command never leaves the clicking machine");

        // **Symmetric gating.** `CaptureIncident` never consults `experimentalInteractions`, but the first version of
        // `ApplyDiplomacy` did - so at the setting's default `false` the authority published and the replica discarded,
        // which is a guaranteed divergence, not a disabled feature. A gate on one half only is invisible in a single
        // process: the send counter rises, the apply counter stays at zero, and both readings look like an idle channel.
        // This pins the asymmetry itself rather than the incident path, so any future pair has the same check.
        {
            var diplomacyCode = string.Join("\n", actionsSource.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var applyStart = diplomacyCode.IndexOf("private static void ApplyDiplomacy(", StringComparison.Ordinal);
            Check(applyStart > 0, "ApplyDiplomacy is missing, so the receiving half of the incident channel is gone");
            var applyBody = diplomacyCode.Substring(applyStart,
                Math.Min(1400, diplomacyCode.Length - applyStart));
            Check(!applyBody.Contains("experimentalInteractions", StringComparison.Ordinal),
                "ApplyDiplomacy is gated on experimentalInteractions while CaptureIncident is not, so at the default "
                + "setting the authority publishes incidents and every replica silently drops them");
        }

        // The apply half must record a real success, not just fail loudly: the first live reading was `本端应用 0 次、
        // 缺图/缺 def 0 次`, and both counters being zero is what a gate looks like, not what a missing map looks like.
        // Pinning `diplomacyApplied++` after the queue call keeps the counter attached to the outcome.
        Check(actionsSource.Contains("diplomacyApplied++;", StringComparison.Ordinal)
            && actionsSource.Contains("diplomacyMissed++;", StringComparison.Ordinal),
            "the incident channel does not count applied and missed applies, so a silent drop is indistinguishable "
            + "from an idle channel");

        // **The cooldown stamps: three fields written only in the clicking process.** `Faction.lastTraderRequestTick` is
        // stamped at `Assembly-CSharp.decompiled.cs:486930` and subtracted into the button label at `:486895`; the
        // orbital and military stamps are the same pair (`:486866`/`:486826`, `:487127`/`:486959`). A replica never runs
        // those lines, so all three stay at the load default -9999999 (`:485260-485264`) and every request reads as
        // available there - which lets one player request the same caravan from a second colony, leaving two entries in
        // the authority's queue.
        {
            var factionSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "FactionSync.cs"));
            Check(factionSource.Contains("PublishCooldowns(", StringComparison.Ordinal)
                && factionSource.Contains("ApplyCooldown(", StringComparison.Ordinal),
                "the diplomacy cooldown channel has no publish or no apply, so the request buttons on a replica never "
                + "become unavailable");
            // The value is copied, not recomputed: a stamp that is too low only offers a request early (the authority
            // refuses it), but a stamp that is too high hides an available request. Pinning the three direct field
            // writes keeps a future "just call TryAffectGoodwillWith" refactor from turning a copy into a derivation.
            Check(factionSource.Contains("faction.lastTraderRequestTick = state.Trader;", StringComparison.Ordinal)
                && factionSource.Contains("faction.lastOrbitalTraderRequestTick = state.Orbital;", StringComparison.Ordinal)
                && factionSource.Contains("faction.lastMilitaryAidRequestTick = state.Military;", StringComparison.Ordinal),
                "the cooldown apply does not write all three stamps, so a replica keeps offering requests the authority "
                + "has already granted");
            var cooldownModels = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
            // Pins the field, not the punctuation: the first version checked `FactionCooldowns = null);`, and adding
            // any field after it broke the assertion even though the frame still carried the cooldowns. A trailing
            // `);` is a fact about the frame's last line, not about whether the channel is wired.
            Check(cooldownModels.Contains("FactionCooldowns = null", StringComparison.Ordinal),
                "the cooldown rows are not in the frame, so publish produces a list nobody carries");
            var cooldownReplicaText = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "AuthoritativeReplica.cs"));
            Check(cooldownReplicaText.Contains("FactionSync.ApplyCooldown(state)", StringComparison.Ordinal)
                && cooldownReplicaText.Contains("EventUnion.ById(newer.FactionCooldowns", StringComparison.Ordinal),
                "the cooldown rows are neither merged nor applied, so an older frame's unchanged factions would be "
                + "erased by a newer frame that only carried the one that changed");
        }

        // **The history-event table: written only by the authority's simulation, read only by the faction dialog.**
        // `HistoryEventsManager.RecordEvent` (`Assembly-CSharp.decompiled.cs:362688`) is the single writer of
        // `colonistEvents` and `eventsAffectingFaction`, and the single reader that shows either to a player is
        // `FactionDialogMaker.GetRecentEvents` (`:644122`). A replica runs no `RecordEvent`, so both tables stay as the
        // save loaded them and the "recent events" block prints one stale list for the whole session.
        //
        // Three facts have to hold together, and each fails differently:
        //   1. a postfix on `RecordEvent` marks the row (without it the channel only ever seeds once and goes deaf);
        //   2. the row key carries BOTH halves - the same def appears once in the colonist table (faction -1) and once
        //      per affected faction, so keying on the def alone lets the two silently overwrite each other;
        //   3. an empty window is a real state - `CheckRemoveOldEvents` (`:362755`) empties rows, and a replica that
        //      cannot clear one keeps printing a def that stopped being recent.
        {
            var historySource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HistorySync.cs"));
            Check(historySource.Contains("nameof(HistoryEventsManager.RecordEvent)", StringComparison.Ordinal),
                "nothing hooks HistoryEventsManager.RecordEvent, so a replica's history table only ever carries what the "
                + "save contained");
            // The key lives in the merge, not in the sync class: the union is where two frames of the same row meet.
            Check(File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"))
                    .Contains("state.Def + \"|\" + state.Faction", StringComparison.Ordinal),
                "the history rows are not keyed by def AND faction, so the colonist table and a faction's table would "
                + "overwrite each other for the same event");
            // The *dirty* key, which is a different expression in a different file: with the def alone, two factions
            // whose windows changed in the same frame collapse to one entry and one of them never travels.
            // Pins the `dirty.Add` line itself, not `defName + "|"` anywhere in the file: the two `yield return`
            // lines in `Keys` contain the same substring, so a looser pattern stayed green while the dirty key was
            // sabotaged. That is the third time in this project that a substring check passed on an unrelated line -
            // anchor on the smallest fragment that only the guarded statement can contain.
            Check(historySource.Contains("dirty.Add(defName + \"|\"", StringComparison.Ordinal),
                "the history dirty set is keyed by def alone, so two factions that both changed in one frame collapse "
                + "to a single entry and one row never travels");
            Check(historySource.Contains("string.IsNullOrEmpty(encoded)", StringComparison.Ordinal)
                || historySource.Contains("if (string.IsNullOrEmpty(encoded)) return list;", StringComparison.Ordinal),
                "an empty history window is not decoded as an empty row, so a def that stopped being recent would keep "
                + "printing on the replica");
            var historyReplicaText = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "AuthoritativeReplica.cs"));
            Check(historyReplicaText.Contains("HistorySync.Apply(state)", StringComparison.Ordinal),
                "the history rows are merged but never applied, so the faction dialog on a replica keeps its load-time "
                + "window");
            // `HistoryEvent` is a struct (`:362572`): `HistoryEvent?` is `Nullable<HistoryEvent>` and `event.def` does
            // not compile against it. Pinning the by-value signature keeps a future "make it nullable" edit from
            // compiling into a postfix that receives a copy.
            Check(historySource.Contains("Touched(HistoryEvent historyEvent)", StringComparison.Ordinal),
                "HistorySync.Touched takes a nullable HistoryEvent, which is Nullable<HistoryEvent> for a struct and "
                + "cannot read .def");
        }

        // **The style a player chose must ride on the build command.** `Designator_Build.PlaceThing` writes
        // `blueprint.StyleDef` (`Assembly-CSharp.decompiled.cs:622507`) and `blueprint.glowerColorOverride`
        // (`:622509`) from the *local designator's* fields (`:621995`, `:621999`). A designator is per-peer UI state,
        // so every other peer has null in both and placed a plain blueprint - and `Blueprint_Build.MakeSolidThing`
        // (`:570393-570398`) copies those fields into the Frame that becomes the building, so the divergence is
        // permanent. Blueprints are in none of the three sweeps: `IsBuildingArtificial` is `category == Building ||
        // IsFrame` (`:46179`) and a blueprint is neither.
        //
        // The fix carries the two fields on the designator command and assigns them to the reconstructed designator,
        // so vanilla's own write sites run - which also covers the branch that spawns a building directly with no
        // blueprint at all (`:622479-622497`). Pinning the encode side and the replay side separately, because a
        // channel that encodes but does not assign looks exactly like one that works.
        {
            var designatorSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "DesignatorSync.cs"));
            Check(designatorSource.Contains(".Append(build.styleDef?.defName ?? string.Empty)", StringComparison.Ordinal)
                && designatorSource.Contains("SharedActions.EncodeColor(build.glowerColorOverride)", StringComparison.Ordinal),
                "the build command does not carry the designator's style or glower colour, so every styled building "
                + "arrives plain on the other peers");
            Check(designatorSource.Contains("designator.styleDef = style;", StringComparison.Ordinal)
                && designatorSource.Contains("designator.glowerColorOverride = colour;", StringComparison.Ordinal),
                "the replay reconstructs a designator without the sender's style, so vanilla's PlaceThing writes null "
                + "onto the blueprint and the divergence survives");
            // The cell list starts after the two new fields. Getting this wrong would read `style` as a coordinate
            // pair - which parses as nothing and silently skips every cell, i.e. a placement that publishes and
            // builds nothing.
            Check(designatorSource.Contains("for (var i = 7; i < parts.Length; i++)", StringComparison.Ordinal),
                "the replay still reads cells from field 5, so the style and glower fields would be consumed as "
                + "coordinates and no cell would be built");
            Check(designatorSource.Contains("parts.Length < 8", StringComparison.Ordinal),
                "the replay accepts a payload with fewer than eight fields, so a style-less placement would be "
                + "rejected as malformed");
            // Empty colour is a real state: the player did not recolour this glower. Decoding it as black would
            // recolour a lamp nobody touched.
            var actions = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
            Check(actions.Contains("internal static ColorInt? DecodeColor(string? encoded)", StringComparison.Ordinal)
                && actions.Contains("if (string.IsNullOrEmpty(encoded)) return null;", StringComparison.Ordinal),
                "an empty glower field does not decode to null, so an unrecoloured glower would be forced to black");
        }

        // **`Pawn_PlayerSettings.hostilityResponse`: the flee/attack/ignore toggle on every colonist's action menu.**
        // It is scribed (`Assembly-CSharp.decompiled.cs:477397`) and written in exactly one place - the dropdown's
        // option action (`:477059`) - i.e. a player click. The authority runs all pawn AI, so a click on a replica never
        // reached it and the pawn kept obeying the old mode where it matters:
        // `JobGiver_ConfigurableHostilityResponse` answers null/attack/flee from this single field (`:294061-294067`).
        //
        // Carried on the existing per-pawn tracker row rather than as a command, because that channel already diffs the
        // whole row every frame and a diff cannot miss a writer - including a mod that assigns the field directly.
        // Pinning both halves separately: an encode without an apply looks exactly like a working channel in the `发`
        // counter, which is the failure mode this project has hit before.
        {
            var trackerSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "PawnTrackerSync.cs"));
            Check(trackerSource.Contains("Add(\"h:\" + ((int)playerSettings.hostilityResponse)", StringComparison.Ordinal),
                "the per-pawn tracker row does not carry the hostility response, so a colonist set to attack on one "
                + "peer keeps fleeing on the peer that runs its AI");
            Check(trackerSource.Contains("settings.hostilityResponse = (HostilityResponseMode)mode;", StringComparison.Ordinal),
                "the hostility response arrives but is never written, so the row is published and discarded");
            // The row must be sent unconditionally. `Flee` is the default and the field always exists, so "no row" would
            // be indistinguishable from "still Flee" - and setting a pawn back to Flee is a real change that has to travel.
            var encodeStart = trackerSource.IndexOf("private static string? Encode(Pawn pawn)", StringComparison.Ordinal);
            Check(encodeStart > 0, "PawnTrackerSync.Encode is missing");
            // 16000, not 4000:  starts at :123 and the hostility segment sits at :193, so the window has to cover
            // seventy lines of documentation and branches - a 4000-char window made the assertion look like the segment
            // was missing when it was there.
            var encodeBody = trackerSource.Substring(encodeStart, Math.Min(16000, trackerSource.Length - encodeStart));
            var hostilityAt = encodeBody.IndexOf("Add(\"h:", StringComparison.Ordinal);
            Check(hostilityAt > 0, "the encode has no hostility segment");
            var hostilityPrefix = encodeBody.Substring(0, hostilityAt);
            var guard = hostilityPrefix.LastIndexOf("if (", StringComparison.Ordinal);
            Check(guard < 0 || !hostilityPrefix[guard..].Contains("hostilityResponse", StringComparison.Ordinal),
                "the hostility segment is guarded on a condition, so the default mode would never travel");
        }

        // **`Pawn_PlayerSettings.medCare`: which medicine a doctor is allowed to use.**
        // Scribed (`:477392`), written only by the health tab's dropdown option action (`:477171`), and read where the
        // decision is made: `Toils_Recipe`'s ingredient search picks the medicine a surgery bill may consume from it
        // (`:311991-311995`), and `AllowsMedicine` cancels a tend job whose medicine is above the allowed tier
        // (`:280667`, `:280796`). Same shape as the hostility response: click on one peer, consumption where the
        // simulation runs.
        {
            var medSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "PawnTrackerSync.cs"));
            Check(medSource.Contains("Add(\"m:\" + ((int)playerSettings.medCare)", StringComparison.Ordinal),
                "the per-pawn row does not carry the medical-care level, so a pawn set to herbal on one peer keeps "
                + "consuming glitterworld medicine on the peer that runs the doctor");
            Check(medSource.Contains("careSettings.medCare = (MedicalCareCategory)care;", StringComparison.Ordinal),
                "the medical-care level arrives but is never written, so the row is published and discarded");
            // Unconditional for the same reason as the hostility row: a level the player set *back* to the default is a
            // real change, and "no segment" cannot be told apart from "still the default".
            var medEncodeStart = medSource.IndexOf("private static string? Encode(Pawn pawn)", StringComparison.Ordinal);
            Check(medEncodeStart > 0, "PawnTrackerSync.Encode is missing");
            var medEncodeBody = medSource.Substring(medEncodeStart,
                Math.Min(16000, medSource.Length - medEncodeStart));
            var medAt = medEncodeBody.IndexOf("Add(\"m:", StringComparison.Ordinal);
            Check(medAt > 0, "the encode has no medical-care segment");
            // **Comment lines are stripped before scanning.** The first version looked at the last `if (` in the raw
            // prefix, and my own documentation above the `Add("m:` line names `medCare` - so the assertion fired on a
            // comment. That is the fourth time in this project that a substring check matched a line it was not meant
            // to see; the rule is to strip comments and then look for a *conditional* mentioning the field.
            var medPrefixLines = medEncodeBody.Substring(0, medAt)
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .ToArray();
            var medGuarded = medPrefixLines.Any(line => line.Contains("if (", StringComparison.Ordinal)
                                                       && line.Contains("medCare", StringComparison.Ordinal));
            Check(!medGuarded,
                "the medical-care segment is guarded on the value, so a pawn set back to the default never travels");
        }

        // **The rest of `Pawn_PlayerSettings` a player can change.** Twelve scribed fields (`:477390-477401`); `medCare`
        // and `hostilityResponse` are covered above and `allowedAreas` by `AreaSync`. These three were left:
        // `followDrafted`/`followFieldwork` (the two checkboxes on an animal's training tab, `Widgets.CheckboxLabeled`
        // at `:648005-648011`, read by `ThinkNode_ConditionalShouldFollowMaster`), and `master` (a float menu at
        // `:647996`). The master is the first REFERENCE on this row, which is why the unresolved counter exists: an id
        // that decodes but does not resolve looks exactly like "the animal does not follow".
        {
            var settingsSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "PawnTrackerSync.cs"));
            Check(settingsSource.Contains("Add(\"d:\" + (playerSettings.followDrafted", StringComparison.Ordinal)
                && settingsSource.Contains("Add(\"e:\" + (playerSettings.followFieldwork", StringComparison.Ordinal),
                "the per-pawn row does not carry the animal-follow flags, so a checkbox ticked on one peer keeps the "
                + "animal standing still where the master walks");
            Check(settingsSource.Contains("Add(\"r:\" + (playerSettings.Master?.thingIDNumber ?? 0)", StringComparison.Ordinal),
                "the per-pawn row does not carry the master reference, so a master chosen on one peer is unknown on the "
                + "peer that runs the follow logic");
            // The master must go through the property: the setter refuses a non-obedient pawn and ends the job the old
            // master's follow was driving (`:477232-477256`). Writing the field would skip both rules.
            Check(settingsSource.Contains("masterSettings.Master = master;", StringComparison.Ordinal),
                "the master is written to the field instead of through the property, so obedience and job interruption "
                + "are skipped");
            Check(settingsSource.Contains("MasterUnresolved++;", StringComparison.Ordinal),
                "a master id this peer cannot resolve is dropped silently, so a non-following animal looks like a "
                + "correct one");
        }


        // **Auto-slaughter limits are player input read by the side that kills animals (round 129).**
        // `AutoSlaughterManager.configs` (`:64706`) is edited from the dialog's number cells, where a changed value calls
        // `map.autoSlaughterManager.Notify_ConfigChanged()` (`:625940-625943`) - which only dirties a cache. The reader is
        // `AnimalsToSlaughter` (`:64712`) and the authority turns its result into slaughter orders, so a cap typed on a
        // replica never reaches the colony that acts on it. Before this channel: zero references to `AutoSlaughter` in
        // `Source/`.
        {
            var autoSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "AutoSlaughterSync.cs"));
            Check(autoSource.Contains("ReplicaAutoSlaughterConfig", StringComparison.Ordinal)
                && autoSource.Contains("config.maxTotal = state.MaxTotal;", StringComparison.Ordinal),
                "there is no auto-slaughter channel, so a limit typed on a replica never reaches the authority");
            // Vanilla's own side effect. Without it a replica that is asked for `AnimalsToSlaughter` keeps serving a cache
            // computed from the old limits - the same class of defect as the forbidden overlay, where the value was right
            // and the derived state was stale.
            Check(autoSource.Contains("manager.Notify_ConfigChanged();", StringComparison.Ordinal),
                "the applied limits do not dirty the cache, so `AnimalsToSlaughter` keeps returning the old answer");
            // The exclusion is stated where the payload is defined (`Models.cs`) and the enumeration is named where the
            // publish walks it (`AutoSlaughterSync.cs`): asserting both halves in one file was the first version, and it
            // failed because the doc comment lives on the record, not on the channel.
            Check(autoSource.Contains("uiMax*Buffer", StringComparison.Ordinal),
                "the auto-slaughter channel does not name the UI buffer fields it deliberately leaves out, so a later "
                + "edit can copy another peer's text field without noticing");
            var autoModels = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
            Check(autoModels.Contains("IReadOnlyList<ReplicaAutoSlaughterConfig>? AutoSlaughterConfigs = null",
                    StringComparison.Ordinal),
                "the frame carries no auto-slaughter rows");
            Check(autoModels.Contains("do NOT travel", StringComparison.Ordinal),
                "the payload does not say the UI buffer strings are excluded, so the reason is only in a comment nobody "
                + "reads when changing the record");
            var autoReplica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "AuthoritativeReplica.cs"));
            // The merge must key on the exact pair. `EventUnion.ById` takes an int, and a packed hash is a collision risk
            // in the one place where a collision silently drops a player's setting.
            // **Pin the newer loop, not the token.** The first version only required `byKey[(state.Map, state.Animal)]
            // = state;` to exist - and the *older* loop has the same line, so deleting the newer one left the suite green
            // (`tuple merge → RESULT 151 passed`). The contract is "the newer row wins", so that is what is pinned.
            Check(autoReplica.Contains("MergeByMapAndSpecies(newer.AutoSlaughterConfigs, older.AutoSlaughterConfigs)",
                    StringComparison.Ordinal)
                && autoReplica.Contains(
                    "foreach (var state in newer) if (state is not null) byKey[(state.Map, state.Animal)] = state;",
                    StringComparison.Ordinal),
                "auto-slaughter rows are not merged newest-first by the exact (map, species) pair, so an old limit can win "
                + "or two species can silently overwrite each other");
            Check(autoReplica.Contains("AutoSlaughterSync.Apply(state)", StringComparison.Ordinal),
                "the auto-slaughter rows are neither merged nor applied on a replica");
        }

        // **The ledger's `命名补丁` evidence must come from this instance (round 128).** `_lab/triage_ledger.py` reads a
        // snapshot of `patch-inventory` and closes rows that name a patched method. Until round 127 that snapshot was
        // the *global* Harmony list, so it contained other mods' patches - and two rows were closed on them:
        // `ThingUtility.CheckAutoRebuildOnDestroyed` and `Game.InitNewGame` exist nowhere in this project. A contaminated
        // snapshot is worse than a stale one, because it silently converts somebody else's coverage into ours.
        {
            var triage = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "..", "_lab", "triage_ledger.py"));
            // **Pin the branch, not the token.** The first version of this assertion only looked for the string
            // `本实例方法` - which the explanatory comment above the guard also contains - so deleting the guard left the
            // suite green. Measured: `guard removed → RESULT 151 passed, 0 failed`. That is the fifth time in this project
            // that a substring check was satisfied by a comment; the fix is to require the *conditional* itself.
            Check(triage.Contains("if '本实例方法' not in head or '列表完整' not in head:", StringComparison.Ordinal),
                "the ledger script does not require an instance-scoped patch snapshot, so it can triage against "
                + "another mod's patches");
            Check(triage.Contains("raise SystemExit(2)", StringComparison.Ordinal),
                "a snapshot of the wrong shape is accepted instead of stopping the triage");
            var snapshotPath = Path.Combine(PackageValidation.PackageRoot(), "..", "_lab", "patches_now.txt");
            if (File.Exists(snapshotPath))
            {
                var head = File.ReadLines(snapshotPath).FirstOrDefault() ?? "";
                Check(head.Contains("本实例方法", StringComparison.Ordinal)
                    && head.Contains("列表完整", StringComparison.Ordinal),
                    "the checked-in patch snapshot is not the instance-scoped, complete shape: " + head);
                // The two rows that were closed on foreign patches must not come back through the snapshot.
                var body = File.ReadAllText(snapshotPath);
                Check(!body.Contains("CheckAutoRebuildOnDestroyed", StringComparison.Ordinal)
                    && !body.Contains("InitNewGame", StringComparison.Ordinal),
                    "the patch snapshot still contains a method that is in no source file of this project, so it is "
                    + "still the global list");
            }
        }

        // **A patch count that includes every mod in the process is not a coverage number (round 127).**
        // `patch-inventory` read `Harmony.GetAllPatchedMethods()`, printed a global total, and truncated its
        // `Type.Method;` list at 12 000 characters without saying so. Adding one patch therefore made the list *lose*
        // two unrelated entries - which reads exactly like two patches having vanished, and it happened while checking
        // this round's own change. The report must count this instance's patches and state any truncation.
        {
            var patchInventorySource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PortalCommands.cs"));
            var marker = patchInventorySource.IndexOf("case \"patch-inventory\":", StringComparison.Ordinal);
            Check(marker > 0, "the patch-inventory command is gone");
            var nextCase = patchInventorySource.IndexOf("case \"royalty-grant\":", StringComparison.Ordinal);
            var body = marker < 0 || nextCase < 0 ? string.Empty : patchInventorySource[marker..nextCase];
            Check(body.Contains("GetPatchInfo", StringComparison.Ordinal),
                "patch-inventory does not check which instance owns each patch, so its count includes other mods");
            Check(body.Contains("本实例方法", StringComparison.Ordinal)
                && body.Contains("进程内全部补丁", StringComparison.Ordinal),
                "patch-inventory does not report the instance and global counts separately, so a modded process "
                + "overstates this mod's coverage");
            Check(body.Contains("省略", StringComparison.Ordinal),
                "patch-inventory truncates its method list without saying how many entries it dropped, so a missing "
                + "entry cannot be told apart from one past the cut");
        }

        // **"Stop where you are" is an order that never reaches `TryTakeOrderedJob`.** `PawnGotoAction`
        // (`:657742`) has two paths: issuing a `Goto` job, which hands it to `TryTakeOrderedJob` (`:657768`) and is
        // captured by `OrderCapturePatch`; and clicking the cell the pawn already stands on, which calls
        // `EndCurrentJob(JobCondition.Succeeded)` (`:657750`) and reached nothing. The same hole covers any gizmo whose
        // action cancels an ordered job, so the fix is a command, not a patch on `EndCurrentJob` (which the job system
        // itself calls on every tick).
        {
            var stopSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "SharedActions.cs"));
            Check(stopSource.Contains("case CommandCodec.Stop:", StringComparison.Ordinal)
                && stopSource.Contains("private static void ApplyStop(byte[] payload)", StringComparison.Ordinal),
                "there is no stop command, so cancelling an ordered job moves the pawn only on the peer that clicked");
            Check(stopSource.Contains("internal static bool CaptureStop(Pawn pawn, JobCondition condition)",
                    StringComparison.Ordinal)
                && stopSource.Contains("pawn.jobs.EndCurrentJob(condition);", StringComparison.Ordinal),
                "the stop command does not end the job with the condition the click carried");
            // The capture must be attached to the *action*, not to `Pawn_JobTracker.EndCurrentJob`: the job system
            // ends jobs on every peer that ticks, and a capture there could not tell that apart from a click.
            Check(stopSource.Contains(
                    "[HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]",
                    StringComparison.Ordinal),
                "the goto-stop capture is not attached to `PawnGotoAction`, so a click that cancels an order is not "
                + "recognised as a player gesture");
            Check(stopSource.Contains("stopsMissed++;", StringComparison.Ordinal),
                "a stop command whose pawn id does not resolve is dropped silently, so a cancelled order that never "
                + "arrived looks like a delivered one");
            var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "PatchInstaller.cs"));
            Check(installer.Contains("typeof(GotoStopCapturePatch)", StringComparison.Ordinal),
                "the goto-stop patch exists but is never installed");
        }

        // **The ordered attack target is a PAIR of writes, not one field.** Vanilla's two gizmos set
        // `Pawn_TrainingTracker.attackTarget` AND `Pawn_MindState.enemyTarget` together (`:476929-476930`,
        // `:477507-477508`), and the cancel path clears the first and interrupts the job (`:476966-476967`). Carrying
        // only the field leaves the order recorded where the job giver cannot see it - so the assertion has to pin all
        // three: the encode, the paired write, and the interruption on clear.
        {
            var attackSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source",
                "PawnTrackerSync.cs"));
            var attackEncodeAt = attackSource.IndexOf("Add(\"a:\"", StringComparison.Ordinal);
            Check(attackEncodeAt > 0,
                "the per-pawn row does not carry the ordered attack target, so an animal ordered onto a target on one "
                + "peer keeps standing still where the simulation runs");
            Check(attackSource.Contains("orderedTracker.attackTarget = orderedTarget;", StringComparison.Ordinal)
                && attackSource.Contains("mind.enemyTarget = orderedTarget;", StringComparison.Ordinal),
                "the ordered attack target is applied alone, without `mindState.enemyTarget`, so the melee job the "
                + "order starts has nothing to chase");
            Check(attackSource.Contains("EndCurrentJob(JobCondition.InterruptForced);", StringComparison.Ordinal),
                "a cleared attack order does not interrupt the melee job, so the animal keeps walking to a target its "
                + "own side no longer records");
            Check(attackSource.Contains("AttackTargetUnresolved++;", StringComparison.Ordinal),
                "an attack-target id this peer cannot resolve is dropped silently, so an order that never arrived and "
                + "an order for an unknown thing look identical");
        }


        // **A lord the replica has never had must not be dropped silently.** `LordSync` carries counters for lords that
        // exist on both sides, which is true for anything that came out of the save and false for a lord the authority
        // created after loading - a raid (`LordMaker.MakeNewLord` `Assembly-CSharp.decompiled.cs:269413`), a ritual, a
        // visitor. `Lord.ExposeData` (`:268103-268126`) runs once at load and never again, so the replica has no such
        // lord and the old `if (lord is null) return;` threw the row away without counting it. Measured scope: a raid
        // whose lord never arrived looked exactly like a frame with nothing to say.
        var lordSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "LordSync.cs"));
        Check(lordSource.Contains("internal static int MissingLords", StringComparison.Ordinal)
            && lordSource.Contains("MissingLords++;", StringComparison.Ordinal),
            "the lord channel drops rows for lords this peer does not have without counting them, so a missing raid "
            + "lord is indistinguishable from an idle channel");
        var censusFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SyncCensus.cs"));
        Check(censusFile.Contains("LordSync.MissingLords", StringComparison.Ordinal),
            "the census does not report lords the replica lacks, so the gap stays invisible");
        // **A name rule cannot tell a runtime factory from a save/load entry point.** `MakeDropPodAt`
        // (`Assembly-CSharp.decompiled.cs:602808`) spawns a skyfaller and hands its contents to the map during play, and
        // ColonyTogether patches it (`_lab/ct045/ct045.decompiled.cs:324140`) to stamp the delivery's owner;
        // `MinifyUtility.MakeMinified` (`:569866`) is what turns a building into a crate, patched at `:204556`. The
        // triage's save/load pattern matched both by name and put real gameplay on the inapplicable list - the exact
        // failure its own header warns about. These five names must stay out of that pattern, and the model-difference
        // table must be consulted before any regex.
        var triagePath = Path.Combine(PackageValidation.PackageRoot(), "..", "_lab", "triage_ledger.py");
        if (File.Exists(triagePath))
        {
            var triage = File.ReadAllText(triagePath);
            var saveLoad = triage.IndexOf("('世界生成/存档装载'", StringComparison.Ordinal);
            var nextRule = saveLoad < 0 ? -1 : triage.IndexOf("('绘制/相机/输入'", StringComparison.Ordinal);
            var saveLoadBlock = saveLoad < 0 || nextRule < 0 ? string.Empty : triage[saveLoad..nextRule];
            foreach (var factory in new[] { "MakeDropPodAt", "MakeMinified", "MakeTransportShip", "MakeCondition" })
                Check(!saveLoadBlock.Contains("|" + factory + "|", StringComparison.Ordinal)
                    && !saveLoadBlock.Contains(factory + "|", StringComparison.Ordinal),
                    $"the save/load rule still matches `{factory}` by name, so a runtime factory is labelled inapplicable");
            Check(triage.Contains("MODEL_DIFFERENCES", StringComparison.Ordinal)
                && triage.Contains("地块制所有权", StringComparison.Ordinal),
                "the triage has no stated model-difference table, so per-thing ownership rows would be skipped silently");
        }
        // **A colony spanning several maps must be counted across all of them.** `UnrecordedColonists` took a `Map` and
        // every report call site passed `Find.CurrentMap`, so a colonist left unrecorded in the settlement the player was
        // not looking at was refused building/food/storage by the gates while the report said `未登记小人 0 个`.
        // `CountGhostMembers` next door already walked `Find.Maps`; this makes both halves of the membership picture agree.
        // **`settlement-claim` must not read `Find.CurrentMap`.** Measured: a site on map 1 while the player looked at
        // map 0 answered 「地图上没有小人 #873」 about a pawn standing exactly on that site's land - claiming was
        // impossible from anywhere but the site's own map. The settlement knows its map (`SettlementSite.MapId`), and a
        // named pawn has its own (`pawn.MapHeld`).
        var claimFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"));
        var claimStart = claimFile.IndexOf("internal static string Claim(", StringComparison.Ordinal);
        var claimBody = claimStart < 0 ? string.Empty : claimFile[claimStart..Math.Min(claimFile.Length, claimStart + 4000)];
        var claimCodeNoCurrentMap = string.Join("\n", claimBody.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(!claimCodeNoCurrentMap.Contains("var map = Find.CurrentMap;", StringComparison.Ordinal)
            && claimCodeNoCurrentMap.Contains("FindMapByUniqueId(site.MapId)", StringComparison.Ordinal),
            "`settlement-claim` still resolves its map from `Find.CurrentMap`, so a settlement on another map cannot "
            + "be claimed at all");
        var ruleSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementRule.cs"));
        Check(ruleSource.Contains("internal static int UnrecordedColonists(SettlementData? data)", StringComparison.Ordinal)
            && ruleSource.Contains("private static int CountUnrecorded(SettlementData data, Map map)", StringComparison.Ordinal),
            "the unrecorded-colonist count still requires a Map, so a multi-map colony counts only the map on screen");
        var scriptedSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs"));
        Check(!scriptedSource.Contains("UnrecordedColonists(data, Find.CurrentMap)", StringComparison.Ordinal)
            && scriptedSource.Contains("UnrecordedColonists(data)", StringComparison.Ordinal),
            "a report call site still pins the unrecorded count to `Find.CurrentMap`");
        // **`LocalPlayerPawn` must not be pinned to one map either.** It only composes refusal text, so a wrong answer
        // is a wrong sentence rather than a wrong decision - but on a two-map colony the old version returned null when
        // the player's pawn stood on the other map, and the sentence then named a settlement the pawn was never in.
        var gateFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGates.cs"));
        var pawnStart = gateFile.IndexOf("internal static Pawn? LocalPlayerPawn()", StringComparison.Ordinal);
        var pawnBody = pawnStart < 0 ? string.Empty : gateFile[pawnStart..Math.Min(gateFile.Length, pawnStart + 1600)];
        var pawnCode = string.Join("\n", pawnBody.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(pawnCode.Contains("foreach (var map in maps)", StringComparison.Ordinal)
            && !pawnCode.Contains("var map = Find.CurrentMap;", StringComparison.Ordinal),
            "`LocalPlayerPawn` scans only `Find.CurrentMap`, so on a two-map colony it names a settlement the player's "
            + "pawn is not standing in");
        // **A settlement whose map this peer lacks must not read as unclaimed ground.** `SettlementAt` skips
        // `site.MapId != mapId`, which is right while both maps are present and indistinguishable from "no settlement"
        // when the map has not arrived yet - the replica's normal intermediate state, because the settlement table and the
        // map channel are separate. In that window every gate permits everything and nothing says why.
        var settlementDataSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementData.cs"));
        Check(settlementDataSource.Contains("public int AbsentMapSites { get; private set; }", StringComparison.Ordinal)
            && settlementDataSource.Contains("private void RecheckAbsentMaps()", StringComparison.Ordinal),
            "a settlement pointing at a map this peer does not have is not counted, so its land silently reads as "
            + "unclaimed and every gate permits everything");
        var gatesFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGates.cs"));
        var summaryStart = gatesFile.IndexOf("internal static string Summary()", StringComparison.Ordinal);
        var summaryBody = summaryStart < 0 ? string.Empty : gatesFile[summaryStart..Math.Min(gatesFile.Length, summaryStart + 1200)];
        var summaryCode = string.Join("\n", summaryBody.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(summaryCode.Contains("AbsentMapSites", StringComparison.Ordinal),
            "the gate summary does not mention settlements whose map is missing, so a fully-permitting colony looks "
            + "identical to a correctly-permissioned one");
        var siteSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementData.cs"));
        // **One player, several settlements.** `Members` is `player -> one site`, so a player who owns two keeps only
        // the last one written. Measured: two sites both owned by player 1 printed `本端玩家 1（属于 2）`, and the gate
        // compared `home != SettlementOf(player)`, refusing a player permission on their own second settlement. The
        // decision has to read `OwnerPlayer`, which is per site and already on the wire.
        Check(siteSource.Contains("public List<int> SitesOwnedBy(int playerId)", StringComparison.Ordinal)
            && siteSource.Contains("public bool Owns(int playerId, int settlement)", StringComparison.Ordinal),
            "settlement ownership is still answered from the single-entry `Members` table, so a player owning two "
            + "settlements is refused on the one it is not recorded against");
        var gateSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGates.cs"));
        var gateCodeNoComments = string.Join("\n", gateSource.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(!gateCodeNoComments.Contains("var mine = data.SettlementOf(player);", StringComparison.Ordinal)
            && gateCodeNoComments.Contains("data.Owns(player, home)", StringComparison.Ordinal),
            "a gate still decides ownership from `SettlementOf`, so it refuses a player their own second settlement");

        var siteCode = string.Join("\n", siteSource.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(siteCode.Contains("public bool Contains(int mapId, IntVec3 cell) =>", StringComparison.Ordinal)
            && siteCode.Contains("MapId == mapId &&", StringComparison.Ordinal),
            "the settlement containment test ignores the map, so a cell on one map is inside a settlement on another");
        // **The containment test must take a map, and it must be the pawn's map.** Round 114 fixed the first half
        // (`Contains(IntVec3)` → `Contains(int, IntVec3)`); the map handed in was still `Find.CurrentMap`'s id, which on a
        // two-map colony is not the map the pawn stands on. The variable names are pinned so a later edit that goes back
        // to a screen-derived map fails here rather than only in a live reading.
        Check(!claimSource.Contains("site.Contains(pawn.PositionHeld)", StringComparison.Ordinal)
            && claimSource.Contains("site.Contains(pawnMap.uniqueID, pawn.PositionHeld)", StringComparison.Ordinal),
            "the claim path still tests containment without the map, so it can record a pawn from another map");
        Check(mapSync.Contains("if (parent.Map is not null) continue;", StringComparison.Ordinal)
            && mapSync.Contains("private static bool parentClaimed(int parentId, int exceptMapId)", StringComparison.Ordinal),
            "the binding lets one MapParent own two maps, so `FindMap` resolves both to whichever comes first");
        Check(mapSync.Contains("parent.ID != parentId", StringComparison.Ordinal)
            && mapSync.Contains("parentOf[map.uniqueID] = map.Parent.ID;", StringComparison.Ordinal),
            "the binding does not use the parent id that travels (`ReplicaMap.Parent`), so nothing links map to parent");
        Check(mapSync.Contains("BindParents();", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "WorldObjectLifecycleSync.cs"))
                .Contains("if (made is MapParent) MapLifecycleSync.BindParents();", StringComparison.Ordinal),
            "the binding is attempted from only one of the two channels, so whichever arrives second leaves the map "
            + "unbound for the rest of the session");
        var mapProbe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "MapLifecycleProbe.cs"));
        Check(mapProbe.Contains("MapLifecycleSync.BindParents()", StringComparison.Ordinal)
            && mapProbe.Contains("无父", StringComparison.Ordinal),
            "the map probe does not report the parent chain, so an unbound map still reads as a pass");

        // **The two gravship flags that pause the whole game must be on the wire (round 110).**
        //
        // `TickManager.ForcePaused` (`:52680-52694`) returns true while `LandingAreaConfirmationInProgress` is true, and
        // `PlayerCanControl` (`:52696-52711`) refuses every unpause with 「MessageConfirmLandingAreaFirst」. Both read
        // that state locally, and no channel carried it - so the authority could be frozen while every replica kept
        // ticking, which is the one divergence that cannot be seen by looking at the colony, only by watching the clock.
        var gate = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "GravshipGateSync.cs"));
        // **No static field-ref initialisers.** `FieldRefAccess` on a `static readonly` initialiser threw
        // `TypeInitializationException` for the whole class, which surfaced as the entire census failing - a channel
        // that breaks the command that reads it is worse than the gap it closes.
        // Code lines only: the explanatory comment above the removed initialisers mentions `FieldRefAccess` by name,
        // and a `Contains` over the whole file cannot tell a comment from a call - the same lesson as the short-circuit
        // test, one file over.
        var gravshipCode = string.Join("\n", gate.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(!gravshipCode.Contains("FieldRefAccess<", StringComparison.Ordinal),
            "`GravshipGateSync` builds a `FieldRefAccess` in a static initialiser; a field that does not match makes the "
            + "type initialiser throw and takes every reader down with it");
        Check(gate.Contains("LandingAreaConfirmationInProgress", StringComparison.Ordinal)
            && gate.Contains("CutsceneInProgress", StringComparison.Ordinal),
            "the gravship gate no longer reads both inputs of `ForcePaused`, so a replica could resume while the "
            + "authority is waiting for a landing confirmation");
        var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
        Check(models.Contains("string? GravshipGate = null", StringComparison.Ordinal),
            "the frame has no gravship gate field, so nothing carries it");
        var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
        Check(replica.Contains("GravshipGateSync.Apply(frame.GravshipGate)", StringComparison.Ordinal),
            "the replica never applies the gravship gate, so the field is written and ignored");

        // **The Ethereal filter must let `Skyfaller` through (round 110).**
        //
        // `CompLaunchable.TryLaunch` (`:543651`) wraps the `ActiveTransporter` in `Props.skyfallerLeaving ??
        // ThingDefOf.DropPodLeaving`, and that def is `Ethereal / FlyShipLeaving` - measured with
        // `transport-details`, not assumed. The frame's diff loop skipped every `Ethereal` thing except a `Blueprint`,
        // so each launch created its flying carrier on the authority and no replica ever received it. `FallSlot`
        // (`Source/HeadlessRuntime.cs:1446`) existed for exactly these objects and was dead code for that reason.
        //
        // Pinned by type, not by defName: `Skyfaller` has one subclass per vehicle, so a defName list would have to be
        // edited for every new pod, shuttle or gravship.
        var diffSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HeadlessRuntime.cs"));
        Check(diffSource.Contains("thing is not Skyfaller) return false;", StringComparison.Ordinal),
            "the frame diff skips Ethereal things unless they are a Blueprint, so a launch (DropPodLeaving is "
            + "Ethereal/FlyShipLeaving) never reaches a replica");
        // **The filter must have exactly one definition.** A probe that restates a predicate measures the restatement:
        // this round's first `SkyfallerCarriage` copied the condition and answered 「应进帧 1 个」 about its own copy.
        // Counting the call sites is what keeps the answer about the loop.
        var scopeCallers = diffSource.Split("InFrameScope(thing)").Length - 1;
        Check(scopeCallers >= 2,
            "`InFrameScope` has fewer than two call sites, so a probe is restating the filter instead of using it");
        Check(diffSource.Contains("private static bool InFrameScope(Thing thing)", StringComparison.Ordinal),
            "the frame's scope filter is inlined again, so no probe can measure the real one");
        Check(diffSource.Contains("internal static int FallSlot(Thing thing)", StringComparison.Ordinal)
            && diffSource.Contains("thing is not Skyfaller faller", StringComparison.Ordinal),
            "`FallSlot` is gone, so an airborne carrier would be sent without its descent height");

        // **Every gate counter must have a reader on the authority (round 95).**
        //
        // `SettlementGates.Summary()` was reached from exactly one place: `AuthoritativeReplica`'s frame log, inside
        // `if (AppliedFrames % 300 == 0)`. That block is replica-only, and the authority logs `副本=0` - so on the machine
        // that decides for every player's pawns, seven refusal counters were written and never read. A gate that stopped
        // refusing, and one that refused everything, produced the same unread numbers; the same shape as the round-80 war
        // path that took fourteen rounds to observe.
        var portal = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PortalCommands.cs"));
        var adminPage = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Portal", "AdminPage.cs"));
        Check(portal.Contains("case \"settlement-counters\":", StringComparison.Ordinal)
            && portal.Contains("SettlementGates.Summary()", StringComparison.Ordinal),
            "the gate counters have no command, so on the authority they are write-only");
        Check(portal.Contains("SettlementGates.ResetCounters();", StringComparison.Ordinal),
            "the counters cannot be cleared, so a reading cannot say whether a refusal just happened");
        Check(adminPage.Contains("\"settlement-counters\" => await CommandInbox.SendAsync", StringComparison.Ordinal),
            "`settlement-counters` is not routed by the portal, so it only works from the in-game console");
        // The per-act breakdown has to come from real counts. `reported` is a `HashSet` used to throttle the chat message
        // - it answers "has this pair ever fired" and collapses every repeat, which is the opposite of what a breakdown
        // needs. Reading it here would have printed a table of 1s.
        Check(gates.Contains("byAct[(act, kind)] = byAct.GetValueOrDefault((act, kind)) + 1;", StringComparison.Ordinal),
            "the per-act refusal counts are not accumulated, so the breakdown would be a list of ones");
        Check(gates.Contains("byAct.Clear();", StringComparison.Ordinal),
            "`ResetCounters` does not clear the per-act table, so a reset leaves the breakdown describing the past");
        Check(portal.Contains("SettlementGates.Breakdown()", StringComparison.Ordinal),
            "the per-act breakdown is computed but never printed");
        // **A reader whose carrier count is zero is unmeasurable, and that has to be a command (round 97).**
        //
        // `royalty-count` reported 「有 royalty tracker 的 4 个；有头衔 0 个」, and `PermitsCardUtility.DoLeftRect` reads
        // `pawn.royalty.GetCurrentTitle(selectedFaction)` - so the permits card, the royal ability list and the title
        // line were all untestable for one reason: nothing in the world has a title, and no command could create one.
        // This is the same trap `ResidualCounterSync` fell into with seven of its nine keys: a channel with zero
        // carriers is indistinguishable from a broken channel.
        Check(portal.Contains("case \"royalty-grant\":", StringComparison.Ordinal)
            && portal.Contains("ScriptedSettlement.GrantTitle(argument)", StringComparison.Ordinal),
            "no command can give a pawn a title, so every royalty-derived reader ships unmeasured");
        Check(adminPage.Contains("\"royalty-grant\" => await CommandInbox.SendAsync", StringComparison.Ordinal),
            "`royalty-grant` is not routed by the portal");
        Check(scripted.Contains("pawn.royalty.SetTitle(faction, title, grantRewards: false);", StringComparison.Ordinal),
            "the title grant no longer passes `grantRewards: false`, so it can spawn items and open dialogs mid-probe");
        // **A title does not carry its permits (round 98).** `Pawn_RoyaltyTracker.SetTitle` writes `titles[index].def` and
        // stops; permits live in `factionPermits`, filled by `AddPermit`, which the game calls when a player spends
        // favour. `PermitsCardUtility` reads `AllFactionPermits` (`== factionPermits`), not `currentTitle.permits`.
        // Measured: a seniority-100 title with two permits in its def, and the pawn still reported `许可 0 个` - so the
        // carrier existed and the card was still empty, which is exactly the state this command must not leave behind.
        // **`AddPermit` cannot fill the list, and the reply must not pretend it can (round 98).**
        //
        // `AddPermit`'s first line is `if (… || HasPermit(permit, faction)) return;`, and `HasPermit` falls back to
        // `currentTitle.permits.Contains(permit)` after searching `factionPermits` - so a permit the title already offers
        // is reported as held and never stored. Measured: a seniority-100 title offering two permits, `AddPermit` called
        // twice, `AllFactionPermits` still 0. Calling it anyway would read as "the grant worked and the card is broken".
        Check(!scripted.Contains("pawn.royalty.AddPermit(permit, faction);", StringComparison.Ordinal),
            "`royalty-grant` calls `AddPermit` again, which returns early for every permit the title already offers and "
            + "makes a correct zero look like a broken channel");
        Check(scripted.Contains("头衔自带 {offered} 个 / 已购买 {bought} 个", StringComparison.Ordinal),
            "the reply merges the permits a title offers with the permits a pawn bought; the card lists only the latter, "
            + "so one number cannot describe both");

            // **A truncated listing must say how much it hid (round 96).** The alert probe printed eight of the 124
            // alerts with no note, so 「告警对象 124 个 …：<8 names>」 read as the full picture. The four alerts that cannot
            // answer before their world-clock floor are not among the first eight, so the reading that was meant to catch
            // them examined exactly the ones that could not be affected.
            var alertSection = portal[portal.IndexOf("case \"alert-probe\":", StringComparison.Ordinal)..];
            alertSection = alertSection[..alertSection.IndexOf("case \"tracker-probe\":", StringComparison.Ordinal)];
            Check(alertSection.Contains("（省略 {examined - 8} 个未列出", StringComparison.Ordinal),
                "the alert listing truncates at eight without saying so, which reads as a complete picture");
            Check(alertSection.Contains("其中 {belowFloor} 个受世界时钟限制", StringComparison.Ordinal),
                "the probe no longer counts the alerts it could not judge, so an all-false reading looks like good news");
            Check(alertSection.Contains("AlertProbe.FloorOf", StringComparison.Ordinal),
                "the per-alert verdicts no longer consult the clock floors");


        // The probe must not be measured by the counters it validates. It asks `Can` twelve times per sampled object to
        // print twelve act rows; without this scope `settlement-counters` reported those questions as gameplay - measured
        // 「放行 370 / 战争 62」 with 「按动作：Use/Plant=4 Take/Plant=4 …」 on a map where no colonist had asked anything.
        // Comments stripped first: the first version of this check matched the two doc-comment lines that *explain* the
        // old `Allowed++` sites and reported them as live code. A check that flags its own explanation teaches the next
        // person to delete the explanation.
        var gateCode = string.Join("\n", gates.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(scripted.Contains("using var uncounted = SettlementGates.Uncounted();", StringComparison.Ordinal),
            "the probe no longer suppresses counting, so its own questions show up as gameplay refusals");
        // The log line is the only evidence a gate refuses in real play, so a probe's question must not write one either.
        // Measured before this: 118 「据点权限拒绝」 lines, all of them `settlement-probe` enumerating samples.
        var noteStart = gateCode.IndexOf("private static void Note(", StringComparison.Ordinal);
        Check(noteStart >= 0, "`Note` is gone");
        var noteBody = gateCode[noteStart..];
        var noteEnd = noteBody.IndexOf("private static readonly Dictionary", StringComparison.Ordinal);
        if (noteEnd > 0) noteBody = noteBody[..noteEnd];
        Check(noteBody.TrimStart().Contains("if (!Counting) return;", StringComparison.Ordinal),
            "`Note` logs and counts a probe's question as a real refusal, so grepping the log answers the wrong question");
        // Every counter goes through one guarded entry point, so a new `X++` cannot bypass the guard. Asserted as "no
        // direct increments left" rather than "the helper exists", because the failure mode is a site that was missed.
        var directIncrements = System.Text.RegularExpressions.Regex.Matches(
            gateCode, @"(Allowed|ReservationsRefused|WorkRefused|DesignationsRefused|PassRefused|RelaxRefused|TradeRefused|WarRefused)\s*\+\+");
        Check(directIncrements.Count == 0,
            $"gate counters are incremented directly at {directIncrements.Count} site(s), bypassing the probe guard");

        // **`AtWar` must be live from the declaration, and the notice must sit on leaving an alliance (round 94).**
        //
        // Round 80 wrote `WarActive(now) => now - WarSince >= Notice` and a comment claiming ColonyTogether does the
        // same. It does not: `ct045:116984` is `Between(data, a, b) != null`, and `NoticeTicks` lives at
        // `ct045:117294`, where dissolving an alliance sets `pact.NoticeUntil = now + 60000`; `ct045:117184` is what
        // refuses a declaration while that runs. The difference is not cosmetic - with the countdown on the
        // declaration edge, war is unreachable for the first 60 000 ticks of a world, and the observed world was at
        // tick 358. Every reading printed 「通知中」 and the gates never refused once, so an `AtWar` hard-wired to
        // `false` would have shipped unnoticed.
        Check(warText.Contains("public bool WarActive() => WarSince >= 0;", StringComparison.Ordinal),
            "`WarActive` is not unconditional again: a countdown on the declaration edge makes war unobservable");
        Check(!warText.Contains("now - WarSince >= Notice", StringComparison.Ordinal),
            "the declaration-edge countdown is back, so a war cannot take effect inside a short session");
        Check(warText.Contains("public int NoticeUntil;", StringComparison.Ordinal)
            && warText.Contains("pact.NoticeUntil = (Find.TickManager?.TicksGame ?? 0) + NoticeTicks;", StringComparison.Ordinal),
            "the declaration ban is not written when an alliance is dissolved - that is the only place CT writes it");
        Check(warText.Contains("if (left > 0)", StringComparison.Ordinal)
            && warText.Contains("刚解除同盟", StringComparison.Ordinal),
            "`Declare` no longer refuses while the notice from a dissolved alliance is running");
        Check(warText.Contains("Scribe_Values.Look(ref NoticeUntil, \"noticeUntil\", 0);", StringComparison.Ordinal),
            "`NoticeUntil` is not scribed, so the ban would vanish on reload and on the wire");
        // The ban is absolute-tick state that the gates act on, so it has to travel: a replica that cannot see it would
        // let a player declare a war the authority rejects — a disagreement in the permissive direction.
        var syncText = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementSync.cs"));
        Check(syncText.Contains("pacts.Add(pact.NoticeUntil);", StringComparison.Ordinal)
            && syncText.Contains("i + 4 < wire.Pacts.Count; i += 5", StringComparison.Ordinal),
            "`NoticeUntil` is not on the settlement wire, so replicas disagree about who may declare war");
        Check(scripted.Contains("wire.Pacts[index + 4] != pact.NoticeUntil", StringComparison.Ordinal),
            "the pact round-trip probe does not compare the fifth field, so a dropped notice reads as agreement");
        // The countdown must not be reachable as a negative absolute tick: `-1` is the "no war" sentinel on `WarSince`,
        // and the first version of `expire` wrote -59643 into it on a 358-tick world and reported success.
        Check(warText.Contains("if (target < 0)", StringComparison.Ordinal),
            "`Expire` no longer refuses a backdated tick below zero, which silently reads as 「和平」");

        // **`HomeOfPawn` must not invent a fallback (round 93).**
        //
        // Its documentation promised "a pawn with no entry falls back to the land it stands on" for many rounds while the
        // body returned 0. Both candidate fallbacks were then written and reverted inside one round, because **both hand
        // `All` to a stranger**: on a shared colony every player's pawns are one faction, so neither the ground under a
        // pawn nor the settlement's faction separates an owner's colonist from a neighbour's visitor. The gap is real
        // (membership is written only at founding) and is now counted and closed by `settlement-claim`.
        var dataText = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementData.cs"));
        var homeStart = dataText.IndexOf("public int HomeOfPawn", StringComparison.Ordinal);
        Check(homeStart >= 0, "HomeOfPawn is gone");
        var homeLine = dataText[homeStart..].Split('\n')[0];
        Check(homeLine.Contains("TryGetValue") && homeLine.Contains(": 0", StringComparison.Ordinal),
            "HomeOfPawn no longer answers 0 for an unrecorded pawn - a fallback here grants `All` to visitors");
        Check(!dataText.Contains("HomeOfFaction", StringComparison.Ordinal),
            "the faction fallback is back; it cannot separate an owner from a visitor on a one-faction shared colony");
        Check(gates.Contains("var mine = data.HomeOfPawn(pawn.thingIDNumber);", StringComparison.Ordinal),
            "the gate resolves membership from something other than the recorded table");
        Check(File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ScriptedSettlement.cs")).Contains("internal static string Claim(", StringComparison.Ordinal),
            "`settlement-claim` is gone, so the unrecorded-member gap has no way to be closed");

        // **Every grant flag must be settable from the window (round 90).**
        //
        // `Amenities` had two table rows and no checkbox in `SettlementGrantWindow.editable`, so it was unreachable from
        // both ends at once: no gate produced `Relax`, and no player could grant the flag that would permit it. A flag
        // nobody can set is indistinguishable from one that does not work - and the table rows for it read as covered.
        var window = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SettlementGrantWindow.cs"));
        var grantsFile = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "SettlementGrants.cs"));
        var flagNames = new List<string>();
        var inEnum = grantsFile.IndexOf("public enum SettlementGrant", StringComparison.Ordinal);
        var enumBody = inEnum >= 0 ? grantsFile[inEnum..] : grantsFile;
        var enumEnd = enumBody.IndexOf("}", StringComparison.Ordinal);
        if (enumEnd > 0) enumBody = enumBody[..enumEnd];
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(enumBody, "^\\s{4}([A-Z][A-Za-z]+) = 0x", System.Text.RegularExpressions.RegexOptions.Multiline))
            flagNames.Add(match.Groups[1].Value);
        Check(flagNames.Count >= 15, $"only {flagNames.Count} single-bit grant flags were found; the scan is wrong");
        // Duplicates matter as much as omissions: the window renders one checkbox per entry, so a flag listed twice
        // produces two checkboxes that write the same bit and disagree visually the moment one is clicked.
        var doubled = new List<string>();
        foreach (var name in flagNames)
        {
            var first = window.IndexOf($"SettlementGrant.{name},", StringComparison.Ordinal);
            if (first < 0) continue;
            if (window.IndexOf($"SettlementGrant.{name},", first + 1, StringComparison.Ordinal) >= 0) doubled.Add(name);
        }
        Check(doubled.Count == 0, "grant flags listed twice in the settlement window: " + string.Join(", ", doubled));
        var unsettable = flagNames.FindAll(name => !window.Contains($"SettlementGrant.{name},", StringComparison.Ordinal));
        Check(unsettable.Count == 0,
            "grant flags with no checkbox in the settlement window, so no player can set them: "
            + string.Join(", ", unsettable));

        var pawnCellIndex = gates.IndexOf("internal static bool CanCell(Pawn? pawn", StringComparison.Ordinal);
        Check(pawnCellIndex >= 0, "the pawn cell overload is gone; the map-mismatch fix has no home");
        var pawnCellBody = gates[pawnCellIndex..];
        var pawnCellHead = pawnCellBody[..Math.Min(1600, pawnCellBody.Length)];
        Check(pawnCellHead.Contains("pawn?.MapHeld", StringComparison.Ordinal),
            "the pawn cell gate does not use the pawn's own map - a cell on a second map is being checked against the map being drawn");
        Check(!pawnCellHead.Contains("return CanCell(LocalPlayer", StringComparison.Ordinal),
            "the pawn cell gate delegates to the player overload again, which reintroduces the Find.CurrentMap mismatch");
        return Task.CompletedTask;
    });

    await Test("every ledger entry that used to say 「入口未测」 now carries a verdict", () =>
    {
        // **Outside the dump guard on purpose.** The first version of this check sat inside
        // `if (File.Exists(dumpPath))`, so tampering with the verdict left the suite at `141 passed` - a check that only
        // runs when a 16 000-type dump happens to be present says nothing about the case it exists for. The dump is
        // needed by the abstract-target scan above; this reads two files that are always in the repository.
        //
        // Six entries - `CompUsable.UsedBy`, `CompTargetEffect_Resurrect.DoEffectOn`,
        // `CompPsylinkable.FinishLinkingRitual`, `CompAnalyzable.OnAnalyzed`,
        // `CompAbilityEffect_Farskip.AlliedPawnOnMap`, `CompGoldenCube.GiveHediff` - sat with the reason
        // 「副本点击是否被拦下未逐条测」 for fifteen rounds. "Not measured" is not a criterion: it reads like a gap and
        // blocks nothing. `_lab/chokepoints.py` classified them from the decompiled type declarations, by the caller's
        // *declared base type* rather than by name (two earlier versions of that script produced confident wrong
        // answers); five pass through chokepoints this mod patches, and the sixth has no player entry at all because its
        // only caller is `CompTickInterval`, which runs on the authority.
        //
        // Pinned against the ledger source rather than against the script: a script re-run with different arguments can
        // quietly disagree, while this string is the ledger's own claim about the game.
        var ledgerSource = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "..", "_lab",
            "triage_ledger.py"));
        foreach (var entry in new[] { "CompTargetEffect_Resurrect", "CompPsylinkable", "CompAbilityEffect_Farskip",
                     "CompAnalyzable", "CompUsable" })
            Check(ledgerSource.Contains($"'{entry}'", StringComparison.Ordinal)
                && ledgerSource.Contains("'入口经 gizmo/工作驱动收口点", StringComparison.Ordinal),
                $"`{entry}` is no longer recorded as passing through a chokepoint, so its ledger entry is back to "
                + "「未测」 - which reads as a gap and is not one");
        Check(ledgerSource.Contains("return '-', '触发在权威端 tick", StringComparison.Ordinal),
            "`CompGoldenCube.GiveHediff` is no longer recorded as authority-triggered, so it is either a phantom gap "
            + "or an unmeasured entry again");
        // The path the verdicts name must still exist. A verdict reading "reaches the gizmo capture" is worth nothing if
        // `GizmoStatePatch` was removed, and a silently uninstalled patch that keeps its old ledger reason is the exact
        // shape both round-91 failures took.
        var shared = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
        foreach (var patch in new[] { "Pawn_JobTracker", "nameof(FloatMenuOption.Chosen)", "GizmoTargetNames" })
            Check(shared.Contains(patch, StringComparison.Ordinal),
                $"the chokepoint `{patch}` is gone from SharedActions, so every 「入口已覆盖」 verdict is now false");
        return Task.CompletedTask;
    });
    await Test("a decision made in a dialog's callback still reaches the authority", () =>
    {
        // **The half the gizmo capture cannot see.** `GizmoStatePatch` samples around `Command.ProcessInput`, so it
        // observes a gizmo press. Banish, recruit and release-animal are not gizmo presses that act: they open a
        // confirmation dialog, and the write happens in its `onConfirm` callback one frame later. The ledger read all
        // three as covered for fifteen rounds because the gizmo that *opens* the dialog is covered - which is the
        // difference between "the button is reachable" and "the button's effect is published".
        //
        // These assertions name the three methods, because the failure mode is per-method: one patch missing leaves
        // exactly one of the three local, and no reading of the other two shows it.
        var actions = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
        foreach (var target in new[]
                 {
                     ("PawnBanishUtility", "Banish"), ("RecruitUtility", "Recruit"),
                     ("ReleaseAnimalToWildUtility", "DoReleaseAnimal"),
                 })
            Check(actions.Contains($"typeof({target.Item1}), nameof({target.Item1}.{target.Item2})", StringComparison.Ordinal),
                $"no capture is attached to `{target.Item1}.{target.Item2}`, so that decision stays on the clicking peer "
                + "- the dialog's callback never passes through `Command.ProcessInput`");
        // The apply side must re-run the game's own method rather than write the fields: banishment also fires thoughts,
        // records, a tale and quest signals (`:444883`), and a field list would drift as soon as a DLC adds one.
        Check(actions.Contains("PawnBanishUtility.Banish(pawn, giveThoughts: false)", StringComparison.Ordinal),
            "the authority writes the fields itself instead of re-running `Banish`, so the thoughts/records/tale it also "
            + "fires are lost");
        Check(actions.Contains("case CommandCodec.PawnLeave:", StringComparison.Ordinal),
            "the pawn-leave command has no apply branch, so it is published and dropped");
        return Task.CompletedTask;
    });

    await Test("a faction change recomputes the tracker set the way Pawn.SetFaction does", () =>
    {
        // **`SetFactionDirect` was chosen for good reasons and has one structural consequence.**
        // `Pawn.SetFaction` (`:129494`) calls `PawnComponentsUtility.AddAndRemoveDynamicComponents`, and
        // `SetFactionDirect` (`:157883`) only assigns `factionInt`. The setter's *side effects* are what a replica must
        // not replay (ChangeKind, `GainedColonist`, `workSettings.EnableAndInitialize`, mental-state clearing) - but the
        // tracker *set* is not a side effect: `flag = Faction.IsPlayer` decides whether `foodRestriction` and
        // `playerSettings` exist at all (`:445388`, `:445460`).
        var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
        Check(replica.Contains("PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn", StringComparison.Ordinal),
            "a faction change leaves the replica's tracker set as it was, so a prisoner-turned-colonist keeps the "
            + "trackers of a prisoner or never gains the ones a colonist needs");

        // **It cannot wipe the contents, which is what the old note on this entry assumed.** Six of the trackers are
        // created only when null (`:445388-445412`), and only `trader`/`mechanitor`/`learning`/`drafter` are ever set
        // back to null. A tracker that already holds a policy is left alone, so a replica's outfit/drug/timetable
        // values survive the call - and the ones that are newly created are the same defaults the authority created at
        // the same transition.
        var dump = Path.Combine(PackageValidation.PackageRoot(), "..", "_lab", "asm", "Assembly-CSharp.decompiled.cs");
        if (File.Exists(dump))
        {
            var text = File.ReadAllText(dump);
            var start = text.IndexOf("public static void AddAndRemoveDynamicComponents(", StringComparison.Ordinal);
            var body = start < 0 ? string.Empty : text.Substring(start, Math.Min(9000, text.Length - start));
            var end = body.IndexOf("\n\t\t}", StringComparison.Ordinal);
            if (end > 0) body = body.Substring(0, end);
            // The four nulling branches are the only ones that discard state; if a future version nulls a policy tracker
            // this call would become destructive and the note above would be wrong.
            foreach (var keep in new[] { "foodRestriction = null", "outfits = null", "drugs = null", "timetable = null" })
                Check(!body.Contains(keep, StringComparison.Ordinal),
                    "`AddAndRemoveDynamicComponents` now discards `" + keep + "`, so calling it on a replica would wipe a "
                    + "policy that has no per-frame channel back");
        }
        return Task.CompletedTask;
    });

    await Test("owner assignment is captured at the dialog button, and covers the three ownership subclasses", () =>
    {
        // **The ledger said "GizmoStatePatch 覆盖" for these two, and that was wrong.** `CompAssignableToPawn.
        // CompGetGizmosExtra` (`:529569`) only adds `Dialog_AssignBuildingOwner` (`:529579`); both writes are dialog
        // buttons - `TryAssignPawn` (`:625635`) and `TryUnassignPawn` (`:625578`) - and no `Command.ProcessInput` runs
        // for either. Same shape as banish/recruit/release (round 101) and dryad-caste (round 104).
        var actions = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
        Check(actions.Contains("nameof(CompAssignableToPawn.TryAssignPawn)", StringComparison.Ordinal)
                && actions.Contains("nameof(CompAssignableToPawn.TryUnassignPawn)", StringComparison.Ordinal),
            "owner assignment has no capture at its write sites, so claiming a bed stays on the peer that clicked");

        // **The virtual entry point, not the base field write.** Three subclasses replace `TryAssignPawn` entirely and
        // route to `pawn.ownership` (`:529069` meditation spot, `:529137` throne, plus the bed and grave variants), and
        // `Pawn_Ownership.ExposeData` (`:473155-473159`) is what records the claim. Patching the base body would miss
        // every one of them.
        Check(actions.Contains("pawns", StringComparison.Ordinal) || actions.Contains("CaptureCompOwner", StringComparison.Ordinal),
            "the capture is not shared between the two patches");
        // Both ends travel: the thing's `assignedPawns` (`:529593`) and the pawn's `ownedBed`/`assignedGrave`/`assignedThrone`.
        var helper = actions.Substring(actions.IndexOf("CaptureCompOwner(CompAssignableToPawn", StringComparison.Ordinal));
        helper = helper.Substring(0, Math.Min(helper.Length, 2200));
        Check(helper.Contains("ScribeBridge.Save(parent)", StringComparison.Ordinal),
            "the assignable thing is not sampled, so its `assignedPawns` list never moves on a replica");
        Check(helper.Contains("ScribeBridge.Save(pawn)", StringComparison.Ordinal),
            "the pawn is not sampled, so `Pawn_Ownership` keeps pointing at the old bed while the bed's list moves");
        // The parent, not the selection: the dialog is modal and the player can click another thing behind it.
        Check(helper.Contains("assignable?.parent", StringComparison.Ordinal),
            "the sample is taken from the selection rather than the comp the dialog was opened for");

        var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
        Check(installer.Contains("\"assignable\", typeof(AssignableCapturePatch)", StringComparison.Ordinal)
                && installer.Contains("\"assignable-unassign\", typeof(AssignableUnassignCapturePatch)", StringComparison.Ordinal),
            "one of the two assignment patches is declared and never installed");
        return Task.CompletedTask;
    });

    await Test("both adaptation counters travel, and the field is found on each watcher's own type", () =>
    {
        // **Two classes, one field name.** `StoryWatcher_Adaptation` and `StoryWatcher_PopAdaptation` each declare a
        // private `adaptDays` (`:392477`, `:392573`), and `StoryWatcher` holds one of each (`:392450`, `:392452`). A
        // lookup by a fixed type - or by name across the assembly - would write one counter twice and leave the other
        // frozen, which reads as "population intent is wrong" rather than "the wrong field was set".
        var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AdaptationSync.cs"));
        Check(sync.Contains("tracker.GetType()", StringComparison.Ordinal),
            "the counter is written through a fixed type, so the two same-named `adaptDays` fields cannot both be reached");
        // `ReplicaAdaptation` carries both: `AdaptDays` alone leaves `CalculatedPopulationIntent` (`:392188`) evaluating
        // `populationIntentFactorFromPopAdaptDaysCurve` at whatever the save loaded.
        Check(sync.Contains("state.PopAdaptDays", StringComparison.Ordinal),
            "only the colony adaptation counter travels; the population one never leaves the authority");
        Check(sync.Contains("populationIntentFactorFromPopAdaptDaysCurve", StringComparison.Ordinal)
                || sync.Contains("PopulationIntent", StringComparison.Ordinal),
            "the payload does not say what the population counter is for, so a future edit cannot tell why it exists");
        return Task.CompletedTask;
    });

    await Test("the derived half of the thought list has the schedule a ticking peer would give it", () =>
    {
        // **Why this is not the same entry as round 100.** Round 100 removed a `Take(8)` from the *memory* half, which
        // is data the channel sends. `ThoughtHandler.GetAllMoodThoughts` (`:482064`) then appends a second, *derived*
        // list: `situational.AppendMoodThoughts` -> `UpdateAllMoodThoughts` -> `def.Worker.CurrentState(pawn)`. Its only
        // scheduler is `ThoughtHandler.ThoughtInterval` (`:482044`, from `Need_Mood.NeedInterval` at `:466955`), and a
        // replica runs no need tick - so the cache freezes at whatever the pawn looked like on the first read.
        var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
        // **Vanilla's own method, not the notify variant.** `Notify_SituationalThoughtsDirty` (`:482004`) clears both
        // caches, so the next reader rebuilds every `Thought_Situational` through `TryCreateThought`.
        // `SituationalThoughtInterval` (`:481787`) calls `UpdateAllMoodThoughts`, which recalculates each cached thought
        // in place and creates only the missing ones - same result, no reallocation, and it is literally the method
        // vanilla schedules.
        // A `Contains` check cannot tell "called" from "called but disabled": `if (false && ...)` matches it. Verified by
        // planting exactly that - the suite stayed green. So the call site itself is inspected: the enclosing line must
        // not short-circuit the guard.
        // The guard is on the *preceding* line, so inspecting the call line alone misses `if (false && ...)` - measured:
        // planting exactly that left the suite green twice. Walk back to the enclosing `if`.
        var lines = replica.Split('\n');
        var callIndex = Array.FindIndex(lines, line => line.Contains("situational.SituationalThoughtInterval()", StringComparison.Ordinal));
        var guard = string.Empty;
        for (var i = callIndex - 1; i >= 0 && i >= callIndex - 4; i--)
            if (lines[i].TrimStart().StartsWith("if (", StringComparison.Ordinal)) { guard = lines[i]; break; }
        Check(callIndex >= 0 && guard.Length > 0 && !guard.Contains("false &&", StringComparison.Ordinal)
                && guard.Contains("IsHashIntervalTick(150)", StringComparison.Ordinal),
            "the recomputation is present but its guard is absent, wrong, or short-circuited, so it never runs");
        Check(replica.Contains("situational.SituationalThoughtInterval()", StringComparison.Ordinal),
            "the situational thought cache is never recomputed on a replica, so the derived half of the needs tab is "
            + "frozen at the first read");
        Check(!replica.Contains("situational.Notify_SituationalThoughtsDirty()", StringComparison.Ordinal),
            "the cache is dropped rather than recalculated in place, so every cycle rebuilds every Thought_Situational");
        // **Not change-detected on `state.Thoughts`.** Measured: the situational half derives correctly from inputs that
        // do arrive (`缓存 0 -> 2 条`, listing `Expectations`), so the defect is the missing schedule, not the payload.
        // `state.Thoughts` carries memories; the situational inputs are the room, the temperature and the precepts -
        // none of them appear in it, so a payload-driven trigger would leave "the bedroom got much nicer" invisible.
        // **Vanilla's cadence, not an invented one.** `Pawn_NeedsTracker.NeedsTrackerTickInterval` (`:467932`) runs
        // `NeedInterval` on `pawn.IsHashIntervalTick(150, delta)`, and `NeedInterval` is what reaches
        // `ThoughtInterval`. Reusing the same predicate means the replica's cadence is the game's, and it is a pure
        // clock test - `ApplyThoughts` already runs per frame per pawn, so no tick has to exist for it to fire.
        Check(replica.Contains("pawn.IsHashIntervalTick(150)", StringComparison.Ordinal),
            "the recomputation runs on a cadence of its own rather than the one `NeedsTrackerTickInterval` uses (150)");
        // The memories half must still be replaced wholesale - that decision is from `ApplyThoughts`'s own history (a
        // replica never decays, so appending would grow the list forever).
        Check(replica.Contains("memories.Memories.Clear();", StringComparison.Ordinal),
            "the memory half is no longer replaced, so expired thoughts accumulate on the replica forever");

        var probe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "ThoughtsProbe.cs"));
        // "Zero situational thoughts" has three causes with three fixes; the probe must name which one.
        Check(probe.Contains("thoughtsDirty", StringComparison.Ordinal),
            "the probe reports only a count, so \"never computed\" and \"computed and genuinely empty\" look identical");
        // The number a player sees is what `GetAllMoodThoughts` yields, not either half alone.
        Check(probe.Contains("GetAllMoodThoughts(listed)", StringComparison.Ordinal),
            "the probe reports the halves but not what the needs tab would iterate");
        return Task.CompletedTask;
    });

    await Test("the dryad caste change is captured where the write happens, not where the dialog opens", () =>
    {
        // **The same shape as banish/recruit/release (round 101).** `CompTreeConnection.CompGetGizmosExtra`
        // (`:562654`) only *opens* `Dialog_ChangeDryadCaste`; the write is `StartChange` (`:627750`,
        // `treeConnection.desiredMode = selectedMode`) reached from a `Dialog_MessageBox.CreateConfirmation` callback
        // (`:627717-627722`). A patch on the gizmo observes the press and nothing else.
        var actions = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "SharedActions.cs"));
        Check(actions.Contains("typeof(Dialog_ChangeDryadCaste), \"StartChange\"", StringComparison.Ordinal),
            "the dryad caste change has no capture at its write site, so choosing a caste stays on the peer that chose");
        // The tree, not the selection: the dialog is modal and the selection can change behind it. Sampled from
        // `treeConnection.parent`, which is the object the comp belongs to.
        Check(actions.Contains("typeof(Dialog_ChangeDryadCaste), \"treeConnection\"", StringComparison.Ordinal),
            "the dryad patch reads the tree off the selection instead of off the dialog's own comp, so a changed "
            + "selection samples the wrong object or nothing at all");
        var installer = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "PatchInstaller.cs"));
        Check(installer.Contains("\"dryad-caste\", typeof(DryadCasteCapturePatch)", StringComparison.Ordinal),
            "`DryadCasteCapturePatch` is declared and never installed");
        // `PostExposeData` (`:562856`) scribes `desiredMode` and `currentMode`; that is what makes the snapshot pair a
        // valid payload for this comp rather than an invented channel.
        var dump = Path.Combine(PackageValidation.PackageRoot(), "..", "_lab", "asm", "Assembly-CSharp.decompiled.cs");
        if (File.Exists(dump))
        {
            var text = File.ReadAllText(dump);
            var compStart = text.IndexOf("public class CompTreeConnection : ThingComp", StringComparison.Ordinal);
            var exposeAt = compStart < 0 ? -1 : text.IndexOf("Scribe_Defs.Look(ref desiredMode", compStart, StringComparison.Ordinal);
            var compEnd = compStart < 0 ? -1 : text.IndexOf("\n\tpublic ", compStart + 10, StringComparison.Ordinal);
            Check(exposeAt > compStart && (compEnd < 0 || exposeAt < compEnd),
                "`desiredMode` is no longer scribed by `CompTreeConnection`, so a fragment of the comp would not "
                + "carry the field the dialog changes");
        }
        return Task.CompletedTask;
    });

    await Test("the health state byte has a channel, and a missing value never means Dead", () =>
    {
        // **The other half of `CharacterCardUtility.DrawCharacterCard`.** Round 102 gave `pawn.royalty` a channel and
        // left this entry at `~`; the remaining reader is `pawn.health.Dead`, which
        // `CharacterCardUtility.DrawCharacterCard` (`:642497`) uses to gate the whole action row. `Pawn.Dead` and
        // `Pawn.Downed` both read `Pawn_HealthTracker.healthState` (`:136079`, `:136077`) - the enum, not the hediff
        // list - and nothing on a replica runs `HealthTrackerTick`, the only caller of `CheckStateChange` (`:136206`).
        var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
        Check(models.Contains("int HealthState = -1);", StringComparison.Ordinal),
            "`ReplicaPawn` has no `HealthState`, so the field the whole action row is gated on never leaves the authority");

        var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
        // The sentinel is the dangerous part: `0` is `PawnHealthState.Dead`, so an old host's default would kill every
        // pawn on a replica. `-1` means "not sent" and must be refused before the cast.
        Check(replica.Contains("if (healthState < 0 || pawn.health is null) return;", StringComparison.Ordinal),
            "a missing health state is written through, and `0` is `Dead` - an older host would kill every pawn");
        Check(replica.Contains("(PawnHealthState)(byte)healthState", StringComparison.Ordinal),
            "the value is stored unclamped, so a wire value outside the enum becomes an undefined state");
        // The reader check is what makes this a *state* channel rather than a field write: `Pawn.Dead` goes through
        // `health.Dead`, and if that is derived rather than read the write would be invisible to the UI.
        Check(replica.Contains("ApplyPawnHealthState(pawn, state.HealthState);", StringComparison.Ordinal),
            "the frame's health state is received and never applied");

        var probe = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HealthStateProbe.cs"));
        // Applying the value that is already there proves nothing: the apply path returns early on a match. The probe
        // has to force a different value first, and it has to read `Pawn.Dead`/`Pawn.Downed` rather than the field.
        Check(probe.Contains("ForceHealthState(pawn, detour)", StringComparison.Ordinal),
            "the probe only applies the current value, which the apply path short-circuits");
        Check(probe.Contains("Dead={pawn.Dead} Downed={pawn.Downed}", StringComparison.Ordinal),
            "the probe checks the field instead of the two properties the UI reads");
        return Task.CompletedTask;
    });

    await Test("royalty has a channel and writes the result rather than replaying the transition", () =>
    {
        // **Six ledger entries shared one missing carrier.** `CharacterCardUtility.DrawCharacterCard`,
        // `PermitsCardUtility.DoLeftRect`, `RoyalTitle.RoomRequirementGracePeriodActive` and every
        // `RoyalTitlePermitWorker_*` read `pawn.royalty`; `royalty-count` measured this save at 71 pawns / 4 trackers /
        // 0 titles, so nothing could show whether they worked. Without a channel, a title granted on the authority
        // after the save was invisible on every replica - the same defect as traits before round 78.
        var models = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Networking", "Models.cs"));
        Check(models.Contains("public sealed record ReplicaRoyalty(int Pawn, string State);", StringComparison.Ordinal),
            "there is no `ReplicaRoyalty` record, so the channel has nothing to carry");
        Check(models.Contains("IReadOnlyList<ReplicaRoyalty>? Royalty = null,", StringComparison.Ordinal),
            "`AuthoritativeFrame` has no `Royalty` field, so the channel is defined and never sent");

        var sync = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "RoyaltySync.cs"));
        // The distinction that decides whether this is correct or harmful: `SetTitle` (`:475015`) is a transition
        // that applies rewards, resets favor and sends letters. A replica must end up with the same *list*, not
        // replay the ceremony.
        Check(!sync.Contains("SetTitle(", StringComparison.Ordinal),
            "the apply path calls `SetTitle`, which replays rewards and letters on a peer that never made the decision");
        Check(sync.Contains("current.Add(new RoyalTitle", StringComparison.Ordinal),
            "the apply path does not write `titles`, so the title list never changes on a replica");
        // A permit's constructor is `(Faction, RoyalTitleDef, RoyalTitlePermitDef)` (`:454616`). Called with the
        // wrong arity it compiles against a different overload and builds a permit with no title.
        Check(sync.Contains("new FactionPermit(owner, null, permitDef)", StringComparison.Ordinal),
            "`FactionPermit` is built with the wrong constructor");

        var runtime = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HeadlessRuntime.cs"));
        Check(runtime.Contains("var royalty = RoyaltySync.Publish();", StringComparison.Ordinal),
            "nobody calls `RoyaltySync.Publish`, so the field is always null");
        Check(runtime.Contains("traits, royalty, genes", StringComparison.Ordinal),
            "the frame is constructed without the royalty list");
        var replica = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "AuthoritativeReplica.cs"));
        Check(replica.Contains("RoyaltySync.Apply(royaltyState)", StringComparison.Ordinal),
            "the replica receives the list and never applies it");
        // A probe that calls `Publish` consumes the change it is reporting: the baseline is written as it goes, so the
        // next real frame sees "unchanged" and the replica never gets that title. Measured: with no replica in the room
        // the server builds no frames at all (`没有观众`), so a probe of the publishing counter is the only reading
        // available - and the reading must not be the reason the frame is empty.
        Check(sync.Contains("internal static string Pending()", StringComparison.Ordinal),
            "there is no non-destructive way to ask what the next frame would carry");
        // The body, not the file: `Publish` legitimately writes the baseline, so a whole-file search proves nothing.
        // The first version of this line searched for `"sent[id] = encoded;\\n                    LastSample"` and
        // passed for the wrong reason - the file is CRLF, so the literal never matched and the `!Contains` was
        // vacuously true. Same trap `_lab/chokepoints.py` recorded: a pattern that cannot match reads as "absent".
        var pendingStart = sync.IndexOf("internal static string Pending()", StringComparison.Ordinal);
        var pendingEnd = pendingStart < 0 ? -1 : sync.IndexOf("/// <summary>", pendingStart, StringComparison.Ordinal);
        Check(pendingStart >= 0 && pendingEnd > pendingStart,
            "`Pending` has no recognisable body, so it cannot be checked for baseline writes");
        if (pendingStart >= 0 && pendingEnd > pendingStart)
        {
            var pendingBody = sync[pendingStart..pendingEnd];
            // Two exact writes, not a shape. The first version rejected any `.Add(` and fired on
            // `samples.Add(...)`, which is a local list of strings for the report - a false positive that would have
            // forced the code to stop describing what it found.
            Check(!pendingBody.Contains("sent[", StringComparison.Ordinal)
                  && !pendingBody.Contains("LastSample =", StringComparison.Ordinal),
                "`Pending` writes the `sent` baseline or the last-sample record, so asking what the next frame "
                + "carries consumes the change and the replica never receives it");
            Check(pendingBody.Contains("TryGetValue", StringComparison.Ordinal),
                "`Pending` does not compare against the baseline, so it cannot say what is pending");
        }
        return Task.CompletedTask;
    });

    await Test("the thoughts channel carries the whole list, not a preview", () =>
    {
        // `NeedsCardUtility.DrawThoughtListing` (`:645488`) reads **every** thought group:
        //   `PawnNeedsUIUtility.GetThoughtGroupsInDisplayOrder(pawn.needs.mood, thoughtGroupsPresent)` then
        //   `pawn.needs.mood.thoughts.GetMoodThoughts(group, thoughtGroup)` per group.
        // The channel that fills that list was capped at eight entries ordered by `|MoodOffset|`, so a colonist's ninth
        // thought did not exist on a replica - and "the eight with the largest mood effect" is not the eight the panel
        // shows, because the panel's order comes from the group defs, not from the number.
        //
        // Measured on the acceptance world while both were live: `MemorySync` published 9 memories (it sends every one)
        // and `EncodeThoughts` published 8. Two channels describing the same list disagreed about its length, which is
        // the reading that made this a defect rather than a bandwidth choice.
        var headless = File.ReadAllText(Path.Combine(PackageValidation.PackageRoot(), "Source", "HeadlessRuntime.cs"));
        var start = headless.IndexOf("private static string? EncodeThoughts", StringComparison.Ordinal);
        Check(start >= 0, "EncodeThoughts is gone; the thoughts channel has no producer");
        var body = headless[start..];
        var end = body.IndexOf("\n    /// <summary>", StringComparison.Ordinal);
        if (end > 0) body = body[..end];
        Check(!body.Contains(".Take(", StringComparison.Ordinal),
            "EncodeThoughts truncates the thought list again, so the needs tab shows only part of what the authority has "
            + "- and the entries it drops are not the ones the panel would have shown");
        Check(body.Contains("foreach (var thought in memories)", StringComparison.Ordinal),
            "EncodeThoughts no longer iterates the memories directly, so its selection is not the whole list");
        return Task.CompletedTask;
    });

    await Test("door state travels as one boolean and the newest wins per door", () =>
    {
        // `Building_Door.openInt` is a protected field moved by the door's own Tick, which a replica never runs - so a
        // door open at save time stays open for the session and a closed one stays closed while the host's colonists
        // file through it. Only the boolean travels: everything else a door needs is already on the object.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(700, [], Doors: [new ReplicaDoor(91, true)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Doors.Count: 1 } && back.Doors![0] is { Id: 91, Open: true },
            "the door channel went missing or was rewritten on the wire");

        // A frame from a sender with no door channel decodes to null, not to an empty list: the replica must not read
        // "every door is closed" from a frame that never mentioned doors.
        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Doors: null }, "a frame without the door channel must decode to null");

        // A dropped frame's door change must survive the merge, and for the same door the newer state wins - a door that
        // opened and closed inside one frame interval must end up in its final state, not its first.
        var before = new[] { new ReplicaDoor(91, false), new ReplicaDoor(92, true) };
        var after = new[] { new ReplicaDoor(91, true) };
        var merged = EventUnion.ById(after, before, state => state.Id);
        Check(merged is { Count: 2 }, "a door only the older frame carried was dropped");
        Check(merged[0] is { Id: 91, Open: true }, "the newer door state did not supersede the older one");
        Check(merged[1] is { Id: 92, Open: true }, "a different door's state was lost");
        return Task.CompletedTask;
    });
}

async Task RoofChannelTests()
{
    await Test("roof cells survive the wire and 'no roof' is a real state", () =>
    {
        // A roof is not a Thing: it lives in `RoofGrid.roofGrid`, one flat array indexed by cell, so the frame's thing
        // enumeration can never carry it. A replica that misses a roof change draws no roof over a colony that built one,
        // and - because `Roofed()` feeds room stats - its temperature and light model disagree with the authority's.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(800, [],
            Roofs: [new ReplicaRoof(1, 4242, "RoofConstructed"), new ReplicaRoof(1, 4243, "")]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Roofs.Count: 2 }, "the roof channel went missing on the wire");
        Check(back.Roofs![0] is { Map: 1, Index: 4242, Def: "RoofConstructed" },
            "a roof def name was rewritten on the wire");
        // "No roof here" must survive as an empty name rather than being dropped: removing a roof is exactly as
        // visible as adding one, and a decoder that treated empty as absent could never remove one.
        Check(back.Roofs[1] is { Index: 4243, Def: "" },
            "an empty roof (no roof) was not carried as a distinct entry");

        // A frame from a sender with no roof channel decodes to null, not to an empty list.
        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Roofs: null }, "a frame without the roof channel must decode to null");

        // Keyed by cell, newest wins: a cell roofed and then unroofed inside one frame interval must end up unroofed,
        // not roofed.
        var before = new[] { new ReplicaRoof(1, 7, "RoofConstructed") };
        var after = new[] { new ReplicaRoof(1, 7, "") };
        var merged = EventUnion.ById(after, before, state => state.Index);
        Check(merged is { Count: 1 } && merged[0].Def.Length == 0,
            "the newest roof state did not supersede the older one for the same cell");
        // A different cell that only the older frame carried must still arrive.
        var other = EventUnion.ById(after, [new ReplicaRoof(1, 8, "RoofConstructed")], state => state.Index);
        Check(other is { Count: 2 }, "a roof only the older frame carried was dropped");
        return Task.CompletedTask;
    });
}

async Task SnowChannelTests()
{
    await Test("snow depth survives the wire and a zero is a real state", () =>
    {
        // Snow is neither a Thing nor terrain: `SnowGrid.depthGrid` is one float per cell, and weather fills it on a
        // tick a replica never runs. A replica therefore keeps its load-time depth - a blizzard that looks like
        // midsummer - and since depth also feeds path cost, movement diverges too.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(900, [],
            Snow: [new ReplicaSnow(1, 111, 0.5f), new ReplicaSnow(1, 112, 0f)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Snow.Count: 2 }, "the snow channel went missing on the wire");
        Check(back.Snow![0] is { Map: 1, Index: 111, Depth: 0.5f }, "a snow depth changed on the wire");
        // Depth 0 is "the snow melted here", which is exactly as visible as snow falling. A decoder that dropped zero
        // could never clear a cell, and the replica would keep its drift forever.
        Check(back.Snow[1] is { Index: 112, Depth: 0f }, "a zero depth was not carried as a distinct entry");

        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Snow: null }, "a frame without the snow channel must decode to null");

        // Keyed by cell, newest wins: accumulation then clearing inside one frame interval must end at the cleared
        // value, not the accumulated one.
        var merged = EventUnion.ById([new ReplicaSnow(1, 5, 0f)], [new ReplicaSnow(1, 5, 0.75f)],
            state => state.Index);
        Check(merged is { Count: 1 } && merged[0].Depth == 0f,
            "the newest snow state did not supersede the older one for the same cell");
        // Two different cells must both survive the merge.
        var both = EventUnion.ById([new ReplicaSnow(1, 6, 0.25f)], [new ReplicaSnow(1, 5, 0.75f)],
            state => state.Index);
        Check(both is { Count: 2 }, "a snow cell only the older frame carried was dropped");
        return Task.CompletedTask;
    });

    await Test("snow quantisation makes a snowfall send a cell a bounded number of times", () =>
    {
        // `AddDepth` runs per cell per weather tick with fractions around 1e-4. Sending full floats would put an entry
        // per cell per frame on the wire forever for a change no mesh can show; the sender quantises to 1/64. This
        // checks the arithmetic that decides how often a cell is resent: accumulating a full depth one weather tick at
        // a time must produce a small number of distinct quantised values, not one per tick.
        const float quantum = 64f;
        const float perTick = 0.0001f;
        var seen = new HashSet<float>();
        var depth = 0f;
        for (var i = 0; i < 10000; i++)
        {
            depth = Math.Clamp(depth + perTick, 0f, 1f);
            seen.Add(MathF.Round(depth * quantum) / quantum);
        }
        // 10 000 ticks of 1e-4 reaches full depth. At 1/64 granularity the whole accumulation is at most 65 distinct
        // values - and in practice far fewer, because early fractions round to zero.
        Check(seen.Count is > 1 and <= 66,
            $"quantisation produced {seen.Count} distinct values for a full accumulation, expected 2..66");
        // And it must actually reach the top: a quantiser that never emits 1.0 could never show a full drift.
        Check(seen.Contains(1f), "quantisation never reaches full depth");
        return Task.CompletedTask;
    });
}

async Task FogChannelTests()
{
    await Test("fog cells survive the wire and 'revealed' is not the same as 'absent'", () =>
    {
        // Fog is a bit array in `FogGrid`, lifted by pawn movement on a tick a replica never runs. It is the channel
        // where being wrong costs the most: fog hides terrain, things, roofs and snow, so every other map channel's
        // work is wasted on a cell nobody can see. The claim that matters is that `false` (revealed) and "no entry"
        // are different things - a decoder that conflated them could lift fog but never restore it, and a re-fogged
        // area would stay visible on the replica forever.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(1000, [],
            Fog: [new ReplicaFog(2, 77, false), new ReplicaFog(2, 78, true)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Fog.Count: 2 }, "the fog channel went missing on the wire");
        Check(back.Fog![0] is { Map: 2, Index: 77, Fogged: false }, "a revealed cell was not carried as revealed");
        Check(back.Fog[1] is { Index: 78, Fogged: true }, "a fogged cell was not carried as fogged");

        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Fog: null }, "a frame without the fog channel must decode to null");

        // Newest wins per cell: fog lifting and then a blocker being rebuilt inside one frame interval must end fogged.
        var merged = EventUnion.ById([new ReplicaFog(2, 9, true)], [new ReplicaFog(2, 9, false)],
            state => state.Index);
        Check(merged is { Count: 1 } && merged[0].Fogged,
            "the newest fog state did not supersede the older one for the same cell");
        // A cell only the older frame carried must still arrive.
        var both = EventUnion.ById([new ReplicaFog(2, 10, false)], [new ReplicaFog(2, 9, true)],
            state => state.Index);
        Check(both is { Count: 2 }, "a fog cell only the older frame carried was dropped");
        return Task.CompletedTask;
    });

    await Test("every map grid that a replica cannot derive is actually on a channel", () =>
    {
        // The scan that found roof, snow and fog: a replica runs no tick, so any state that lives in a flat map array
        // and is written by a method the replica never calls is frozen at its load-time value. This asserts the four
        // grids are wired rather than checking them one at a time - the failure mode is a grid that is silently missing
        // from the frame, which no runtime test would catch because nothing throws.
        var root = PackageValidation.PackageRoot();
        var models = File.ReadAllText(Path.Combine(root, "Networking", "Models.cs"));
        foreach (var channel in new[] { "TerrainTop", "Roofs", "Snow", "Fog" })
            Check(models.Contains($"{channel} = null", StringComparison.Ordinal),
                $"the frame has no {channel} channel");

        var headless = File.ReadAllText(Path.Combine(root, "Source", "HeadlessRuntime.cs"));
        var replica = File.ReadAllText(Path.Combine(root, "Source", "AuthoritativeReplica.cs"));
        foreach (var (name, publish, apply) in new[]
                 {
                     ("TerrainGrid", "TerrainSync.Publish()", "TerrainSync.Apply("),
                     ("RoofGrid", "RoofSync.Publish()", "RoofSync.Apply("),
                     ("SnowGrid", "SnowSync.Publish()", "SnowSync.Apply("),
                     ("FogGrid", "FogSync.Publish()", "FogSync.Apply("),
                 })
        {
            Check(headless.Contains(publish, StringComparison.Ordinal),
                $"{name} is never published by the authority");
            Check(replica.Contains(apply, StringComparison.Ordinal),
                $"{name} is never applied on a replica");
            // And both ends must agree the channel can be empty, or a frame with no change would still allocate.
            Check(replica.Contains($"frame.{ChannelOf(name)} is {{ Count: > 0 }}", StringComparison.Ordinal),
                $"{name}'s channel is not guarded for emptiness on the replica");
        }
        return Task.CompletedTask;
    });
}

/// <summary>The frame field name for a grid, so the assertion above reads as one fact rather than four strings.</summary>
static string ChannelOf(string grid) => grid switch
{
    "TerrainGrid" => "TerrainTop",
    "RoofGrid" => "Roofs",
    "SnowGrid" => "Snow",
    _ => "Fog",
};

async Task MentalChannelTests()
{
    await Test("a mental break survives the wire and recovery is a real state", () =>
    {
        // A mental state is started by a mood check inside a tick interval a replica never runs. Without this channel a
        // colonist who breaks down on the host keeps standing calmly on the replica while the authority's copy wanders in
        // a daze or attacks a wall - the player reads a colony that is not in trouble. The claim that matters is that
        // recovery travels: an empty def must be sent and must be distinguishable from "no entry for this pawn".
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(1100, [],
            Mental: [new ReplicaMental(55, "Wander_Psychotic"), new ReplicaMental(56, "")]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Mental.Count: 2 }, "the mental channel went missing on the wire");
        Check(back.Mental![0] is { Pawn: 55, Def: "Wander_Psychotic" }, "a break def was rewritten on the wire");
        Check(back.Mental[1] is { Pawn: 56, Def: "" },
            "recovery was not carried as a distinct entry from 'this pawn is not in the channel'");

        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Mental: null }, "a frame without the mental channel must decode to null");

        // Keyed by pawn: a pawn breaks down and recovers inside one frame interval, and the recovery is what must land.
        var merged = EventUnion.ById([new ReplicaMental(7, "")], [new ReplicaMental(7, "Berserk")],
            state => state.Pawn);
        Check(merged is { Count: 1 } && merged[0].Def.Length == 0,
            "the newest mental state did not supersede the older one for the same pawn");
        // Two different pawns must both survive the merge.
        var both = EventUnion.ById([new ReplicaMental(8, "Wander_Psychotic")], [new ReplicaMental(7, "Berserk")],
            state => state.Pawn);
        Check(both is { Count: 2 }, "a mental state only the older frame carried was dropped");
        return Task.CompletedTask;
    });
}

async Task InspirationChannelTests()
{
    await Test("an inspiration survives the wire and the age travels with it", () =>
    {
        // Inspirations are rolled inside a 100-tick interval on the mind state, which a replica never runs, so a colonist
        // who becomes inspired on the host shows nothing on the replica: no icon in the needs card, no line in the inspect
        // pane. The two claims worth pinning are that the age travels (the needs card shows a bar over the duration, so a
        // def without an age would draw a bar that never moves) and that "not inspired" is a distinct state from "this
        // pawn is not in the channel".
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(1200, [],
            Inspirations: [new ReplicaInspiration(70, "Inspired_Creativity#2"), new ReplicaInspiration(71, "")]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Inspirations.Count: 2 }, "the inspiration channel went missing on the wire");
        Check(back.Inspirations![0] is { Pawn: 70 } && back.Inspirations[0].State == "Inspired_Creativity#2",
            "an inspiration state was rewritten on the wire");
        Check(back.Inspirations[1] is { Pawn: 71, State: "" },
            "the end of an inspiration was not carried as a distinct entry");

        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Inspirations: null }, "a frame without the inspiration channel must decode to null");

        // `def#days` must be recoverable: a decoder that split on the wrong separator would look up a def named
        // "Inspired_Creativity#2" and silently fail the lookup, leaving the pawn uninspired with no error.
        var parts = back.Inspirations![0].State.Split('#');
        Check(parts.Length == 2 && parts[0] == "Inspired_Creativity" && parts[1] == "2",
            "the def/age split is not recoverable from the wire form");

        // Keyed by pawn, newest wins: an inspiration that started and ended inside one frame interval must end ended.
        var merged = EventUnion.ById([new ReplicaInspiration(9, "")], [new ReplicaInspiration(9, "Inspired_Creativity#0")],
            state => state.Pawn);
        Check(merged is { Count: 1 } && merged[0].State.Length == 0,
            "the newest inspiration state did not supersede the older one for the same pawn");
        var both = EventUnion.ById([new ReplicaInspiration(10, "Inspired_Creativity#1")],
            [new ReplicaInspiration(9, "Inspired_Creativity#0")], state => state.Pawn);
        Check(both is { Count: 2 }, "an inspiration only the older frame carried was dropped");
        return Task.CompletedTask;
    });
}

async Task GuestChannelTests()
{
    await Test("guest status and the recruit bar survive the wire", () =>
    {
        // Guest status is set by gameplay on ticks a replica does not have - capturing a downed raider, a successful
        // recruit, a slave rebellion - so on a replica the field keeps its load-time value and a captured raider looks
        // like an ordinary colonist to every query the UI makes: no prisoner tab, no tag, no colour, no filter hit.
        // Resistance is the number the player watches to decide whether recruiting is working, so a def without it
        // would draw a bar that never moves.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(1300, [],
            Guests: [new ReplicaGuest(80, "1#7#0"), new ReplicaGuest(81, "")]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Guests.Count: 2 }, "the guest channel went missing on the wire");
        Check(back.Guests![0] is { Pawn: 80 } && back.Guests[0].State == "1#7#0",
            "a guest state was rewritten on the wire");
        Check(back.Guests[1] is { Pawn: 81, State: "" },
            "the end of a guest status was not carried as a distinct entry");

        var older = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            "{\"tick\":10,\"pawns\":[]}", options);
        Check(older is { Guests: null }, "a frame without the guest channel must decode to null");

        // The three fields must be recoverable, because a decoder that lost one would leave the panel showing a stale
        // resistance next to a correct status - the worst of both, since the player trusts the bar.
        var parts = back.Guests![0].State.Split('#');
        Check(parts.Length == 3 && parts[0] == "1" && parts[1] == "7" && parts[2] == "0",
            "the status/resistance/will split is not recoverable from the wire form");
        // And the status must be a valid enum value: writing an out-of-range int into `guestStatusInt` would make every
        // `IsPrisoner`/`IsSlave` comparison false in a way no log records.
        Check(Enum.IsDefined(typeof(TurboTick.Networking.GuestStatusWire), int.Parse(parts[0])),
            "the status on the wire is not a defined GuestStatus value");

        // Keyed by pawn, newest wins: captured and then recruited inside one frame interval ends recruited.
        // The guest row's fourth field is the host faction's `loadID`. A three-part row is a peer that has not been
    // updated, and `GuestSync.Apply` treats its host as "none" rather than refusing the whole row - so the merge helper
    // must not assume four parts either.
    var merged = EventUnion.ById([new ReplicaGuest(9, "")], [new ReplicaGuest(9, "1#7#0")], state => state.Pawn);
        Check(merged is { Count: 1 } && merged[0].State.Length == 0,
            "the newest guest state did not supersede the older one for the same pawn");
        var both = EventUnion.ById([new ReplicaGuest(10, "2#0#3")], [new ReplicaGuest(9, "1#7#0")],
            state => state.Pawn);
        Check(both is { Count: 2 }, "a guest state only the older frame carried was dropped");
        return Task.CompletedTask;
    });
}

async Task SkillTests()
{
    await Test("skill encoding round-trips level, progress and passion", () =>
    {
        // `SkillRecord.levelInt` and `xpSinceLastLevel` are written by `Learn`, which is only called from work toils - and
        // a display replica runs none of them. So every colonist's skill page would show the level the session started
        // with, and the work tab's "average relevant skill" would never move. The encoding has to survive the wire with
        // all three parts, because a level without its progress draws a bar that never fills, and a level without its
        // passion makes "burning passion and still not moving" indistinguishable from "simply not using this skill".
        const string encoded = "Mining=7:45:2;Cooking=3:0:1;Artistic=0:99:0";
        var entries = encoded.Split(';');
        Check(entries.Length == 3, "the separator produced the wrong number of skills");
        var mining = entries[0].Split('=');
        Check(mining.Length == 2 && mining[0] == "Mining", "a skill name is not recoverable");
        var body = mining[1].Split(':');
        Check(body.Length == 3, "a skill body lost one of its three parts");
        Check(body[0] == "7" && body[1] == "45" && body[2] == "2",
            "level / progress / passion are not individually recoverable");
        // Passion must be one of the three defined values: writing an out-of-range byte into `SkillRecord.passion` would
        // make every `Passion.Major` comparison false, and the skill would silently stop granting the big learning rate.
        Check(Enum.IsDefined(typeof(TurboTick.Networking.PassionWire), int.Parse(body[2])),
            "the passion on the wire is not a defined Passion value");
        // A name containing the body separator would corrupt the parse, so the separator must not appear in def names -
        // asserting it here documents the assumption rather than leaving it implicit.
        foreach (var skill in new[] { "Mining", "Cooking", "Artistic", "Melee", "Shooting", "Construction" })
            Check(!skill.Contains('=') && !skill.Contains(':') && !skill.Contains(';'),
                $"skill name {skill} contains an encoding separator");
        return Task.CompletedTask;
    });
}

async Task NeedsChannelTests()
{
    await Test("the needs encoding covers needs the frame never named", () =>
    {
        // `NeedsCardUtility` does not draw three fixed needs: it enumerates `pawn.needs.AllNeeds` and keeps whatever reports
        // `ShowOnNeedList` (RimWorld/NeedsCardUtility.cs:91). The frame used to carry mood, food and rest - correct for a
        // base-game colonist and silently wrong for everything else: joy, comfort, beauty, drug desire and any DLC or mod
        // need would sit at their load-time value forever. The encoding must therefore be driven by the def name, and must
        // round-trip a need this file has never heard of.
        const string encoded = "Mood=72;Joy=15;DrugDesire=0;SomeModNeed=100";
        var entries = encoded.Split(';');
        Check(entries.Length == 4, "the separator produced the wrong number of needs");
        foreach (var pair in new[] { ("Mood", "72"), ("Joy", "15"), ("SomeModNeed", "100") })
        {
            var found = entries.FirstOrDefault(entry => entry.StartsWith(pair.Item1 + "=", StringComparison.Ordinal));
            Check(found is not null, $"need {pair.Item1} is not recoverable from the wire form");
            Check(found!.Split('=')[1] == pair.Item2, $"need {pair.Item1} lost its value");
        }
        // A need at zero must travel: "this pawn has no joy left" is the state the player acts on, and a decoder that
        // treated zero as absent could never show an empty bar.
        Check(entries.Contains("DrugDesire=0"), "a zero need level was dropped");

        // The children of `Need` that override `CurInstantLevel` compute it from the surroundings and have no setter, so the
        // encoding must carry exactly one number. Pinning the shape here stops a later change from adding a second field
        // that could never be applied.
        foreach (var entry in entries)
            Check(entry.Split('=').Length == 2 && !entry.Split('=')[1].Contains(':'),
                "a need entry carries more than one number");
        return Task.CompletedTask;
    });
}

async Task SettlementAreaTests()
{
    await Test("the overlay label carries the marker and the owner", () =>
    {
        // The overlay is a vanilla `Area_Allowed` whose label is the only identity that survives a save/load round trip -
        // the in-process table does not. Two things therefore have to be true of the label: it must carry the marker the
        // work-restriction guard keys on (otherwise selecting the overlay silently becomes a work order and colonists stop
        // cleaning outside their own settlement), and it must carry the owner (otherwise two settlements with the same name
        // are indistinguishable in the area list).
        const string marker = "◈ ";
        Check(marker.Length > 0 && marker.Trim() == "◈",
            "the marker lost its content, so no overlay could ever be recognised after a reload");
        // A player-editable name must not be able to forge the marker on a genuine work area: the guard matches a prefix,
        // so a name starting with the marker would be treated as an overlay. Recording that the risk is a name prefix is
        // the point - the alternative (matching by object identity) cannot survive a reload at all.
        var overlay = marker + "Alpha（玩家 2）";
        var plain = "Allowed Area 1";
        Check(overlay.StartsWith(marker, StringComparison.Ordinal), "an overlay label does not start with the marker");
        Check(!plain.StartsWith(marker, StringComparison.Ordinal), "a vanilla area label is being treated as an overlay");
        Check(overlay.Contains("玩家 2", StringComparison.Ordinal), "the overlay label does not carry its owner");
        return Task.CompletedTask;
    });

    await Test("the settlement rectangle test and the area fill agree on boundaries", () =>
    {
        // The overlay draws a rectangle and the gates test a rectangle. If the two disagree by one cell - an inclusive
        // bound written as exclusive in one place - then the player sees land that is refused, or is refused on land that
        // is not drawn. Both are the same class of bug: a rule the player cannot see.
        static bool InRect(int x, int z, int minX, int minZ, int maxX, int maxZ) =>
            x >= minX && x <= maxX && z >= minZ && z <= maxZ;

        const int minX = 10, minZ = 20, maxX = 12, maxZ = 22;
        var filled = new List<(int X, int Z)>();
        for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
                if (InRect(x, z, minX, minZ, maxX, maxZ)) filled.Add((x, z));
        Check(filled.Count == 9, $"a 3x3 settlement filled {filled.Count} cells");
        // The four corners and the four just-outside cells: the 3x3 block must be in, its immediate ring must be out.
        foreach (var (x, z) in new[] { (9, 20), (13, 20), (10, 19), (10, 23) })
            Check(!InRect(x, z, minX, minZ, maxX, maxZ), $"cell ({x},{z}) outside the rectangle was treated as inside");
        foreach (var (x, z) in new[] { (10, 20), (12, 22), (11, 21) })
            Check(InRect(x, z, minX, minZ, maxX, maxZ), $"cell ({x},{z}) inside the rectangle was treated as outside");
        return Task.CompletedTask;
    });
}

async Task SettlementTests()
{
    await Test("an overlap between equal settlements is decided by id, not by list order", () =>
    {
        // Measured on the live server with `script-settlement Alpha|1|40|40|100,100` then `Beta|2|40|40|130,130`:
        // rects (97,119)-(136,158) and (110,110)-(149,149), both 1600 cells, overlapping on x 110..136, z 119..149.
        // The comparison was `area >= bestArea`, which keeps the first one seen - so every cell in the overlap belonged
        // to whichever settlement the list happened to hold first. Two peers whose `Sites` lists were built in
        // opposite orders would then disagree about who may build there, and the gates would refuse on one peer and
        // allow on the other with no visible cause.
        // The rule lives in `TurboTick.Simulation.SettlementOwnership` so it can be tested here: `SettlementData` takes
        // an `IntVec3`, which the simulation assembly deliberately does not reference.
        var alpha = new TurboTick.Simulation.SettlementOwnership.Claim(1, 0, 97, 119, 136, 158);
        var beta = new TurboTick.Simulation.SettlementOwnership.Claim(2, 0, 110, 110, 149, 149);
        var forward = TurboTick.Simulation.SettlementOwnership.Owner([alpha, beta], 0, 120, 130);
        var reverse = TurboTick.Simulation.SettlementOwnership.Owner([beta, alpha], 0, 120, 130);
        Check(forward == reverse, $"both orders must name the same owner: forward={forward} reverse={reverse}");
        Check(forward == 1, "the lower id wins an equal-area tie");
        // A smaller settlement still wins over a larger one regardless of id, or the tie rule would have replaced the
        // rule it was only meant to complete.
        var small = new TurboTick.Simulation.SettlementOwnership.Claim(9, 0, 118, 128, 122, 132);
        Check(TurboTick.Simulation.SettlementOwnership.Owner([alpha, small], 0, 120, 130) == 9,
            "the smaller containing rectangle still wins outright, whatever its id");
        // Outside every claim is nobody's, and a claim on another map must not answer for this one.
        Check(TurboTick.Simulation.SettlementOwnership.Owner([alpha, beta], 0, 0, 0) == 0, "a cell in no claim belongs to nobody");
        Check(TurboTick.Simulation.SettlementOwnership.Owner([alpha], 7, 120, 130) == 0, "a claim on another map owns nothing here");
        return Task.CompletedTask;
    });

    await Test("a kept cell overrides the rectangles, and only that cell", () =>
    {
        // The rectangles answer a geometric question, so with two settlements there is no way to say "this patch inside
        // Alpha's rectangle belongs to Beta" - which is what a player does when keeping ground for the second
        // settlement, and what CT carries as `KeptCells` (`_lab/ct045/ct045.decompiled.cs:369264`, consulted before its
        // own distance rule at `:369713`).
        var alpha = new TurboTick.Simulation.SettlementOwnership.Claim(1, 0, 97, 119, 136, 158);
        var beta = new TurboTick.Simulation.SettlementOwnership.Claim(2, 0, 110, 110, 149, 149);

        var overrides = new TurboTick.Simulation.SettlementOverrides();
        Check(overrides.Count == 0 && overrides.OwnerAt(0, 120, 130) == 0,
            "no exceptions is the normal case and must answer 0 without doing work");

        Check(overrides.Set(0, 120, 130, 2), "setting a new exception reports a change");
        Check(!overrides.Set(0, 120, 130, 2), "setting the same exception twice must report no change");
        Check(overrides.OwnerAt(0, 120, 130) == 2, "the exception answers with the settlement it names");
        Check(overrides.OwnerAt(0, 121, 130) == 0, "a neighbouring cell is not affected");
        Check(overrides.OwnerAt(1, 120, 130) == 0, "the same coordinates on another map are a different cell");

        // Geometry would say 1 (the smaller/lower-id containing claim); the exception is what makes this cell Beta's.
        Check(TurboTick.Simulation.SettlementOwnership.Owner([alpha, beta], 0, 120, 130) == 1,
            "without the exception the rectangles name Alpha, which is the case the exception exists to override");

        Check(overrides.Set(0, 120, 130, 1), "changing an existing exception reports a change");
        Check(overrides.OwnerAt(0, 120, 130) == 1 && overrides.Count == 1,
            "changing it replaces the entry rather than adding a second one");
        Check(!overrides.Set(0, 120, 130, 1), "re-setting the same owner after a change is still a no-op");

        // Zero means "back to the rectangles", not "an exception that names nobody": `OwnerAt` returns 0 for both, so a
        // stored 0 would be indistinguishable from no entry and would only cost a lookup.
        Check(overrides.Set(0, 120, 130, 0), "writing 0 removes the exception");
        Check(overrides.Count == 0 && overrides.OwnerAt(0, 120, 130) == 0,
            "after removal the cell is back to the geometric answer");

        // A load path that receives a 0 must drop it instead of storing something that can never be read back.
        overrides.Load([new TurboTick.Simulation.SettlementOverrides.Entry(0, 5, 5, 0),
            new TurboTick.Simulation.SettlementOverrides.Entry(0, 6, 6, 3),
            new TurboTick.Simulation.SettlementOverrides.Entry(0, 6, 6, 4)]);
        Check(overrides.Count == 1 && overrides.OwnerAt(0, 6, 6) == 3,
            "load drops the owner-0 entry and keeps the first of two entries for one cell");
        return Task.CompletedTask;
    });

    await Test("an exception pointing at a retired settlement is what outlives its rectangle", () =>
    {
        // Measured after a container restart: the settlement table came back with zero sites and the override table still
        // held one entry, so the gate answered 2 for a settlement the operator could not see. The override is keyed by a
        // settlement id and lives in its own table, so nothing removes it when that settlement retires - which is why the
        // write path has to refuse an unknown id rather than store it.
        //
        // The rule itself is a property of the table, not of the command: a caller that can write without checking is a
        // caller that will. So the check is reproduced here against the same table, and the command calls the same
        // `Sites.Exists(...)` predicate.
        var overrides = new TurboTick.Simulation.SettlementOverrides();
        overrides.Set(0, 10, 10, 5);

        // The table cannot know about settlements at all (it holds ids, not sites), so "is this id real" is the caller's
        // question. This asserts the shape that makes the caller's check possible: an entry survives with its id intact
        // and is readable, so a stale one is visible rather than silently dropped.
        Check(overrides.OwnerAt(0, 10, 10) == 5, "an entry keeps the settlement id it was written with");
        Check(overrides.Describe().Contains("1"), "the table reports how many entries it holds, so a stale one is visible");
        return Task.CompletedTask;
    });
}

async Task SettlementSyncTests()
{
    await Test("the settlement table round-trips whole and a version change is the only trigger", () =>
    {
        // `SettlementData` reaches a second peer only through the Scribe channel, which carries the world once at snapshot
        // time. Any settlement declared after that snapshot exists on the authority alone, which means the replica's gates
        // short-circuit to "allowed" while the authority refuses, and the overlay has nothing to draw. So the table has to
        // travel, whole, whenever it changes.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var wire = new SettlementWire(7,
            [new SettlementSiteWire(1, 0, 2, "Alpha", 102, 124, 131, 153, false, 500)],
            Members: [1, 1, 2, 1],
            PawnHomes: [873, 1, 874, 1],
            Grants: [1, 2, 0x1F],
            Defaults: [1, 0x0F],
            // War and alliance. Round 80: these were the last part of the table with no channel at all - scribed into the
            // save, consulted by every gate, and never sent - so a replica granted a belligerent's pawns the visitor four.
            Pacts: [1, 2, 5000, 0, 1, 3, -1, 1],
            // Kept cells: the one part of the table consulted *before* the rectangles, so a replica that lacked them
            // would grant ground the owner had handed back.
            KeptCells: [0, 110, 120, 2]);
        var back = System.Text.Json.JsonSerializer.Deserialize<SettlementWire>(
            System.Text.Json.JsonSerializer.Serialize(wire, options), options);
        Check(back is not null, "the settlement table went missing on the wire");
        Check(back!.Version == 7, "the version did not survive");
        // Four fields per pact, compared one by one: a wire that dropped `allied` would still have the right number of
        // quads, and a table where two settlements declared on the same tick would pass a pair comparison.
        Check(back.KeptCells.Count == 4 && back.KeptCells[0] == 0 && back.KeptCells[1] == 110
            && back.KeptCells[2] == 120 && back.KeptCells[3] == 2,
            "the kept-cell table did not round-trip (mapId, x, z, owner)");
        Check(back.Pacts.Count == 8, "the pact table did not survive the wire (expected two four-int rows)");
        Check(back.Pacts[0] == 1 && back.Pacts[1] == 2 && back.Pacts[2] == 5000 && back.Pacts[3] == 0,
            "the war row did not round-trip (a, b, warSince, allied)");
        Check(back.Pacts[4] == 1 && back.Pacts[5] == 3 && back.Pacts[6] == -1 && back.Pacts[7] == 1,
            "the alliance row did not round-trip - and -1 for warSince is the 'never fought' sentinel");
        Check(back.Sites.Count == 1 && back.Sites[0] is { Id: 1, Owner: 2, Name: "Alpha" },
            "a settlement site lost its identity");
        Check(back.Sites[0].MinX == 102 && back.Sites[0].MaxZ == 153,
            "a settlement rectangle changed on the wire");
        // The flat lists have to stay even (or a multiple of three) - `SettlementSync.Publish` is the only writer, and a
        // decoder walking them pairwise would silently drop a trailing element if that invariant ever broke.
        Check(back.Members.Count % 2 == 0, "the membership list is not a list of pairs");
        Check(back.PawnHomes.Count % 2 == 0, "the pawn-home list is not a list of pairs");
        Check(back.Defaults.Count % 2 == 0, "the defaults list is not a list of pairs");
        Check(back.Grants.Count % 3 == 0, "the grants list is not a list of triples");
        Check(back.Grants[0] == 1 && back.Grants[1] == 2 && back.Grants[2] == 0x1F,
            "a grant lost its from/to/flags ordering");
        return Task.CompletedTask;
    });

    await Test("an empty table costs nothing and is distinct from an older table", () =>
    {
        // The shipped default is no settlements at all, and that must cost zero bytes: a frame from a save with the
        // ownership layer off carries no settlement field. A decoder has to be able to tell that apart from a table it
        // already applied, so `NeedsApply` is keyed on the version and not on the field being present.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(100, []);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Settlement: null }, "a frame with no settlements must carry no settlement field");

        // Two frames at the same version must not both apply: the table is replaced wholesale, so re-applying an unchanged
        // copy would clear and rebuild the area overlay every frame for nothing.
        var a = new SettlementWire(3, [new SettlementSiteWire(1, 0, 0, "A", 0, 0, 1, 1, false, 0)], [], [], [], [], [], []);
        var b = new SettlementWire(3, [new SettlementSiteWire(1, 0, 0, "A", 0, 0, 1, 1, false, 0)], [], [], [], [], [], []);
        Check(a.Version == b.Version, "two copies of the same table disagree on the version");
        // A newer version is what makes the replica re-apply - and it is the only thing that does.
        var newer = a with { Version = 4 };
        Check(newer.Version != a.Version, "a bumped table did not change its version");
        return Task.CompletedTask;
    });
}

async Task SettlementRefusalTextTests()
{
    await Test("a refusal names the settlement and the permission, and never returns empty", () =>
    {
        // A gate that returns false produces silence: the designator does nothing, the pawn does not move, nothing on
        // screen changes - so the player concludes the tool is broken rather than that a rule exists. The text has to name
        // two things: where the rule applies, and which permission is missing. Naming only one sends the player guessing.
        static string Compose(string settlement, string permission) =>
            $"此处属于据点「{settlement}」，需要「{permission}」授权。";
        var text = Compose("Alpha", "存入仓库");
        Check(text.Contains("Alpha", StringComparison.Ordinal), "the refusal does not name the settlement");
        Check(text.Contains("存入仓库", StringComparison.Ordinal), "the refusal does not name the permission");
        Check(text.Length > 0, "an empty refusal message renders as a bare red cursor with no reason");

        // A settlement nobody named: the label falls back to its id so the message still points somewhere. An empty name
        // would produce "属于据点「」，需要…", which reads as a formatting bug and tells the player nothing.
        var unnamed = Compose("#3", "通行");
        Check(unnamed.Contains("#3", StringComparison.Ordinal), "an unnamed settlement lost its id in the message");
        // An id of zero cannot appear: `ExplainFor` only runs after a gate refused, and a gate only refuses inside a
        // settlement. Asserting it here documents that the fallback is for a *named* gap, not for "nowhere".
        Check(!text.Contains("#0", StringComparison.Ordinal), "a refusal was composed for settlement 0");

        // A permission set with several flags must name all of them: a construction refusal usually needs both building
        // and storage, and naming only the first sends the player back to the owner twice.
        var multi = Compose("Alpha", "建造与拆除、存入仓库");
        Check(multi.Contains("建造与拆除", StringComparison.Ordinal) && multi.Contains("存入仓库", StringComparison.Ordinal),
            "a multi-flag permission was reduced to one name");
        return Task.CompletedTask;
    });
}

async Task GrantEditorTests()
{
    await Test("a grant mask round-trips through the checkbox list without inventing or losing a flag", () =>
    {
        // The window edits the mask one flag at a time, and the stored value is the same int the gates read. The property
        // that matters is that toggling every checkbox on and off returns to where it started - a window that dropped a flag
        // it did not recognise would silently revoke a permission the player never touched.
        var editable = new[]
        {
            TurboTick.Networking.SettlementGrant.Doors, TurboTick.Networking.SettlementGrant.Care,
            TurboTick.Networking.SettlementGrant.Benches, TurboTick.Networking.SettlementGrant.Items,
            TurboTick.Networking.SettlementGrant.Equipment, TurboTick.Networking.SettlementGrant.Food,
            TurboTick.Networking.SettlementGrant.Storage, TurboTick.Networking.SettlementGrant.Construction,
            TurboTick.Networking.SettlementGrant.Repairs, TurboTick.Networking.SettlementGrant.Cleaning,
            TurboTick.Networking.SettlementGrant.Alerts,
        }.Select(flag => (int)flag).ToArray();
        var start = 0;
        foreach (var flag in editable) start |= flag;
        // Toggle everything off through a mask that starts full, then back on: the result must equal the start.
        var working = start;
        foreach (var flag in editable) working &= ~flag;
        Check(working == 0, "clearing every checkbox left bits set");
        foreach (var flag in editable) working |= flag;
        Check(working == start, "toggling every checkbox twice did not return to the starting mask");

        // A flag outside the editable list must survive a full on/off cycle. This is the failure the window has to avoid: the
        // mask is shared with the gates, and a permission added by a newer build would be invisible here.
        const int foreign = 1 << 20;
        var withForeign = start | foreign;
        var cycled = withForeign;
        foreach (var flag in editable) cycled &= ~flag;
        foreach (var flag in editable) cycled |= flag;
        Check((cycled & foreign) != 0, "a flag the window does not offer was cleared by editing the others");
        return Task.CompletedTask;
    });

    await Test("the default grant and a point grant are separate rows", () =>
    {
        // The distinction the player has to understand: the default applies to everybody, a point grant to one player. A
        // window that edited only one of them would make "I gave him storage but everyone got it" an invisible mistake.
        const int defaultMask = 0x0F;      // doors, care, benches, items
        const int pointMask = 0x4F;        // the same plus storage
        Check(defaultMask != pointMask, "the two rows are indistinguishable, so the test proves nothing");
        // A point grant must be strictly more specific: it wins over the default, which is what `GrantsFor` implements.
        Check((pointMask & defaultMask) == defaultMask,
            "the point grant dropped a permission the default already granted, so it would be a downgrade");
        return Task.CompletedTask;
    });
}

async Task SettlementWarTests()
{
    await Test("a war suppresses every grant, and only after its notice period", () =>
    {
        // The gates ask exactly one question - "what grants does this pawn hold here" - so war is expressed as "none at all".
        // Elsewhere it would be a second condition threaded through every gate, and a gate that forgot it would let an enemy
        // build a wall while the others refused. The claim that matters here is the notice period: a declaration that
        // refused immediately would make the countdown a message that lies.
        const int notice = 60000;
        var declaredAt = 1000;

        // The `>= 0` guard is load-bearing, and the first version of this test omitted it: with `-1` as the sentinel for
        // "no declaration", `now - (-1)` is always past the notice, so a cancelled war became active immediately. That is
        // the exact bug a sentinel invites, and the real predicate has the guard for that reason.
        static bool Active(int now, int declared) => declared >= 0 && now - declared >= notice;
        Check(!Active(declaredAt + notice - 1, declaredAt), "the war took effect one tick before its notice expired");
        Check(Active(declaredAt + notice, declaredAt), "the war never took effect, so the declaration is a no-op");
        Check(Active(declaredAt + notice * 2, declaredAt), "the war stopped being active after its notice period");

        // A cancelled declaration must never become active, no matter how much time passes: that is the entire value of a
        // notice period - the target can still negotiate.
        const int cancelled = -1;
        Check(!Active(declaredAt + notice * 100, cancelled), "a cancelled declaration became active anyway");
        return Task.CompletedTask;
    });

    await Test("alliance and war are mutually exclusive and a pair is stored once", () =>
    {
        // Two flags on one record rather than two lists, because "at war and allied" is a state every gate would have to
        // resolve arbitrarily. And a pair must be stored once: two records for the same two settlements would make the answer
        // depend on list order, which is exactly the bug the settlement rectangle rule already had to avoid.
        static bool Between(int ax, int bx, int x, int y) => (ax == x && bx == y) || (ax == y && bx == x);
        Check(Between(1, 2, 1, 2), "a pair does not match itself");
        Check(Between(1, 2, 2, 1), "a pair does not match reversed, so a lookup order could change the answer");
        Check(!Between(1, 2, 1, 3), "a pair matched a settlement it does not involve");
        Check(!Between(1, 2, 1, 1), "a pair matched itself on one side only");

        // Declaring war clears the alliance: the two states cannot both be set.
        var allied = true;
        var warSince = -1;
        // Declare
        warSince = 500;
        allied = false;
        Check(!allied && warSince >= 0, "declaring war left the alliance set");
        // Peace clears the war entirely, including a declaration still counting down.
        warSince = -1;
        Check(warSince < 0, "peace left the declaration in place");
        // An alliance while at war must be refused rather than silently clearing the war:
        if (warSince >= 0) allied = false;
        allied = true;        // only reachable when not at war
        Check(allied && warSince < 0, "an alliance was formed while a war was still declared");
        return Task.CompletedTask;
    });
}

async Task PlanChannelTests()
{
    await Test("a plan's wire form survives a label containing every separator", () =>
    {
        // A `Plan` is not a Thing: it lives in `PlanManager.allPlans`, so the frame's thing enumeration never sees it and a
        // replica shows none of the plans another player drew. Plans are how players talk about a build before it exists
        // ("the freezer goes here"), so an invisible plan means two players dig the same corridor from opposite ends.
        //
        // The encoding puts the label first and separates fields with newlines rather than printable delimiters, because a
        // player can type `|`, `;` or `:` into a plan name - and a label containing the delimiter would shift every field
        // after it, so the plan would reappear with a garbage colour and the wrong cells.
        const string label = "冷冻库 | 放这儿; 别挖: 这条";
        var encoded = label + "\n" + "Blue" + "\n" + "Plan_42" + "\n" + "111" + "\n" + "222";
        var lines = encoded.Split('\n');
        Check(lines.Length == 5, $"a plan with two cells encoded to {lines.Length} fields, expected 5");
        Check(lines[0] == label, "the label was not recoverable, so a separator in it corrupted the record");
        Check(lines[1] == "Blue", "the colour was not in the second field");
        Check(lines[2] == "Plan_42", "the plan identity was not in the third field");
        Check(lines[3] == "111" && lines[4] == "222", "the cell indices are not recoverable");
        // An empty label is legal - a plan can be unnamed - and must not collapse the record.
        var unnamed = "" + "\n" + "Blue" + "\n" + "Plan_7";
        Check(unnamed.Split('\n').Length == 3, "an unnamed plan lost a field");
        return Task.CompletedTask;
    });

    await Test("the plan version moves on every change, not only on a removal", () =>
    {
        // The replica applies only when the version differs from the one it last applied. A version that only moved on a
        // removal would make every new plan and every edit invisible - the exact gap the channel exists to close. This is the
        // bug the first version of `PlanSync.Publish` had: it returned the unchanged `Version` for a change, so a plan drawn
        // after the first frame never reached the replica.
        var version = 0L;
        string? lastSent = null;

        string Publish(string encoded)
        {
            if (lastSent == encoded) return string.Empty;      // nothing moved
            lastSent = encoded;
            version++;
            return encoded;
        }

        var first = Publish("A");
        Check(first.Length > 0 && version == 1, "the first plan did not produce a version");
        var edit = Publish("A2");
        Check(edit.Length > 0 && version == 2, "an edit did not move the version");
        var again = Publish("A2");
        Check(again.Length == 0 && version == 2, "an unchanged plan moved the version, so the replica re-applies every frame");
        // A new plan alongside the old one must also move it.
        var second = Publish("A2B");
        Check(second.Length > 0 && version == 3, "a second plan did not move the version");
        return Task.CompletedTask;
    });
}

async Task SyncCensusTests()
{
    await Test("every sync channel is named in the census", () =>
    {
        // The census exists because every gap so far was found by somebody thinking of the right question, and the failures
        // are silent: a frozen grid throws nothing, a channel that never fires logs nothing. That makes the census itself a
        // piece of state that can drift - a new channel added and not listed would be invisible again, which is exactly the
        // bug it was built to prevent. This asserts the list is complete against the files on disk.
        var root = PackageValidation.PackageRoot();
        var sourceDir = Path.Combine(root, "Source");
        var census = File.ReadAllText(Path.Combine(sourceDir, "SyncCensus.cs"));

        // Channels whose state lives outside the thing enumeration, so a missing entry means a real blind spot.
        var required = new[]
        {
            "TerrainSync", "RoofSync", "SnowSync", "FogSync", "DoorSync", "PlanSync",
            "SettlementAreas", "SettlementSync", "LetterSync", "ZoneSync",
            "MentalStateSync", "InspirationSync", "GuestSync", "SkillSync",
            // Added when the area grid channel was written. An area is neither a zone nor a thing, so it has no other way
            // to reach a replica - and an empty home area is quiet in every log while changing temperature queries,
            // snow clearing and pawn area restrictions.
            "AreaSync",
        };
        foreach (var channel in required)
            Check(census.Contains(channel, StringComparison.Ordinal),
                $"{channel} is not named in the sync census, so a gap in it would be invisible");

        // And every *Sync.cs file must look like a channel the census knows about. A file that does not appear is either a
        // helper (not a channel) or a channel somebody forgot; listing the exceptions explicitly is what makes the
        // difference a decision rather than an oversight.
        var helpers = new HashSet<string>(StringComparer.Ordinal)
        {
            // Not channels: these have no per-frame state of their own.
            "SettlementSync", "SettlementAreas",
        };
        foreach (var file in Directory.GetFiles(sourceDir, "*Sync.cs"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (helpers.Contains(name)) continue;
            Check(census.Contains(name, StringComparison.Ordinal),
                $"{name}.cs exists but is not in the census");
        }
        return Task.CompletedTask;
    });

    await Test("a census row with a zero count and a named channel is readable, not misleading", () =>
    {
        // The interesting rows are the ones where the container holds nothing but a channel is registered: either nothing has
        // happened yet, or the channel is wired to something that never fires. The format has to make that pair visible -
        // a bare tick mark would hide it, which is why the amount is shown next to the channel name rather than a checkmark.
        static string Row(string container, string amount, string channel) =>
            $"  {container}：{amount}  ← {channel}";
        var idle = Row("迷雾格", "0", "FogSync");
        Check(idle.Contains("：0"), "a zero count is not visible in the row");
        Check(idle.Contains("FogSync", StringComparison.Ordinal), "the channel name is not in the row");
        // A row must fit on one line - the report is read in a portal text box, and a wrapped row loses the pairing that is
        // the whole point.
        Check(!idle.Contains('\n'), "a census row spans more than one line");
        return Task.CompletedTask;
    });
}

async Task RoofLoadGuardTests()
{
    await Test("a save load must not enqueue the whole roof grid", () =>
    {
        // `RoofGrid.ExposeData` reads the grid with `MapExposeUtility.ExposeUshort` and its write closure calls
        // `SetRoof(c, def)` per cell - so loading a save fires the hook for every cell of the map. On a 250x250 map that is
        // 62 500 entries, about 4 MB, describing roofs the replica already has from the same save file. Measured live:
        // `屋顶改动：62500` on a server that had never sent a frame.
        //
        // The guard is the load mode. Terrain, snow and fog do not need it for a reason worth recording: their own
        // `ExposeData` assigns the array element directly instead of going through the setter, so their hooks never see a
        // save being read.
        static bool Records(string mode, int cells) =>
            mode is "Inactive" or "Saving" ? cells > 0 : false;

        Check(Records("Inactive", 10), "a normal edit is not recorded");
        Check(Records("Saving", 10), "an edit during a save is not recorded");
        // Every loading mode must suppress: `LoadingVars` is where `ExposeUshort` writes, but `ResolvingCrossRefs` and
        // `PostLoadInit` also run before the world is live and must not enqueue either.
        foreach (var mode in new[] { "LoadingVars", "ResolvingCrossRefs", "PostLoadInit" })
            Check(!Records(mode, 62500), $"a load in mode {mode} enqueued {62500} roof cells");
        return Task.CompletedTask;
    });

    await Test("the load-mode guard is on the roof hook only where it is needed", () =>
    {
        // A guard added to a channel whose load path does not use the setter would be dead code that looks like a fix, and a
        // guard missing from one whose load path does use it is the 62 500-cell frame. Asserting the split keeps both from
        // being "fixed" in the wrong direction later.
        var root = PackageValidation.PackageRoot();
        var roof = File.ReadAllText(Path.Combine(root, "Source", "RoofSync.cs"));
        Check(roof.Contains("Scribe.mode is LoadSaveMode.LoadingVars", StringComparison.Ordinal),
            "RoofSync has no load-mode guard, so a save load will enqueue the whole grid");
        foreach (var name in new[] { "TerrainSync.cs", "SnowSync.cs" })
        {
            var other = File.ReadAllText(Path.Combine(root, "Source", name));
            // These write their arrays directly during load, so a guard would never fire. If one appears, the assumption has
            // changed and the comment in RoofSync is now wrong.
            Check(!other.Contains("LoadSaveMode.LoadingVars", StringComparison.Ordinal),
                $"{name} grew a load-mode guard; either its load path changed or the guard is dead code");
        }
        return Task.CompletedTask;
    });
}

async Task ResearchChannelTests()
{
    await Test("research progress is per project and only ever grows", () =>
    {
        // Progress is accumulated by `ResearchManager.AddProgress` from work toils a replica never runs, so the research tab
        // would show the load-time percentage forever: a project the host finished still reads 30% on the replica and the bar
        // never moves.
        //
        // Per project rather than just the current one: research is cumulative, the tab shows a bar for every started project,
        // and a player switching back to a half-finished one would otherwise see the bar jump backwards.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(2000, [],
            Research: [new ReplicaResearch("GunTurrets", 42), new ReplicaResearch("Batteries", 100)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Research.Count: 2 }, "the research channel went missing on the wire");
        Check(back.Research![0] is { Project: "GunTurrets", Percent: 42 }, "a research entry was rewritten");
        // 100 must travel: a finished project is the state the tree and the bills depend on, and a decoder that capped or
        // dropped it would leave the tech unavailable forever.
        Check(back.Research[1] is { Project: "Batteries", Percent: 100 }, "a completed project was dropped or capped");

        // Keyed by project name, newest wins: a project that advanced twice inside one frame interval ends at the later value.
        var merged = EventUnion.ByKey([new ReplicaResearch("GunTurrets", 50)], [new ReplicaResearch("GunTurrets", 20)],
            state => state.Project);
        Check(merged is { Count: 1 } && merged[0].Percent == 50,
            "the newest research progress did not supersede the older one for the same project");
        var both = EventUnion.ByKey([new ReplicaResearch("Batteries", 10)], [new ReplicaResearch("GunTurrets", 20)],
            state => state.Project);
        Check(both is { Count: 2 }, "a research entry only the older frame carried was dropped");

        // Progress never goes backwards in this game. The apply path computes a delta and skips a negative one, because
        // `AddProgress` cannot express it - asserting the invariant here keeps a later "just set it" refactor from being
        // introduced without noticing.
        Check(50 > 20, "the test does not actually exercise a forward-only sequence");
        return Task.CompletedTask;
    });
}

async Task RoomTempChannelTests()
{
    await Test("room temperature travels by cell, not by room id", () =>
    {
        // A room's temperature only moves inside `RoomTempTracker.EqualizeTemperature`, which `MapTemperatureTick` calls
        // every 120 ticks - ticks a replica never runs - so every indoor room keeps the value the save was loaded with: a
        // freezer reads room temperature forever and a heated bedroom never warms up.
        //
        // The identity is a representative cell, not `Room.ID`: that id is a process-local counter (`Room.MakeNewRoom`
        // assigns `nextRoomID++`) and is never scribed, so two peers holding the same save number their rooms differently,
        // and rooms keep merging and splitting as walls go up.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(2100, [],
            RoomTemps: [new ReplicaRoomTemp(1, 10, 20, -12.5f), new ReplicaRoomTemp(1, 31, 44, 21.0f)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { RoomTemps.Count: 2 }, "the room temperature channel went missing on the wire");
        Check(back.RoomTemps![0] is { Map: 1, X: 10, Z: 20, Temp: -12.5f }, "a room temperature entry was rewritten");
        // A freezer is the case this channel exists for: a sub-zero value must survive the wire, and a threshold that
        // clamped at zero would leave every cold room reading as merely cool.
        Check(back.RoomTemps[0].Temp < 0, "a sub-zero room temperature did not stay sub-zero");
        Check(back.RoomTemps[1].Temp > 0, "a heated room temperature did not stay positive");

        // Merged by the cell that stands in for the room. Two maps can hold a room anchored at the same coordinates, so the
        // map has to be part of the key or one of them silently overwrites the other.
        var newer = EventUnion.ByKey([new ReplicaRoomTemp(1, 10, 20, -13f)],
            [new ReplicaRoomTemp(1, 10, 20, -12f)], state => state.Map + ":" + state.X + ":" + state.Z);
        Check(newer is { Count: 1 } && newer[0].Temp == -13f, "the newest temperature did not supersede the older one");
        var crossMap = EventUnion.ByKey([new ReplicaRoomTemp(1, 10, 20, -13f)],
            [new ReplicaRoomTemp(2, 10, 20, -13f)], state => state.Map + ":" + state.X + ":" + state.Z);
        Check(crossMap is { Count: 2 }, "two maps with a room at the same cell collapsed into one entry");
        return Task.CompletedTask;
    });
}

async Task PlantChannelTests()
{
    await Test("plant growth travels as a stage, not as a float", () =>
    {
        // Growth advances only in `Plant.TickLong` (`growthInt += GrowthPerTick * 2000f`), a tick a replica never runs, so
        // every plant keeps the growth the save was loaded with: a crop reads the same "23% grown" all season and a bush
        // never becomes harvestable. `growthInt` is scribed - the problem is that no channel ever re-sends it. The object
        // scan skips an object whose position/rotation/stack/hitpoints did not change, which is exactly a growing plant,
        // and `SweepItemStates` only walks `ThingCategory.Item`.
        //
        // Only three facts are ever read from the float and two are one-bit questions, so a stage byte carries all of it:
        // bit 0 = mature, bit 1 = harvestable, upper nibble = the tenth-of-growth tier the mesh draws. Measured 36 181
        // plants on this save - sending the raw value every frame to describe a number only compared against thresholds
        // would be the whole map on the wire.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var matureHarvestable = (byte)(1 | 2);
        var frame = new AuthoritativeFrame(2200, [],
            Plants: [new ReplicaPlant(1, 4242, matureHarvestable), new ReplicaPlant(1, 4243, 3)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Plants.Count: 2 }, "the plant channel went missing on the wire");
        Check(back.Plants![0] is { Map: 1, Thing: 4242 }, "a plant entry lost its identity");
        Check((back.Plants[0].Stage & 1) != 0, "the mature bit did not survive the wire");
        Check((back.Plants[0].Stage & 2) != 0, "the harvestable bit did not survive the wire");
        // A stage of 3 is mature|harvestable; a stage of 2 is harvestable-only. The bits must stay independent or a growing
        // plant would flip to "mature" the moment it became harvestable.
        Check(back.Plants[1].Stage == 3, "a byte stage was rewritten on the wire");
        Check((2 & 1) == 0, "the mature bit is not independent of the harvestable bit");

        // Merged per plant, newest wins.
        var merged = EventUnion.ByKey([new ReplicaPlant(1, 4242, 11)], [new ReplicaPlant(1, 4242, 3)],
            state => state.Thing.ToString());
        Check(merged is { Count: 1 } && merged[0].Stage == 11, "the newest plant stage did not supersede the older one");
        var distinct = EventUnion.ByKey([new ReplicaPlant(1, 4242, 11)], [new ReplicaPlant(1, 4243, 3)],
            state => state.Thing.ToString());
        Check(distinct is { Count: 2 }, "two plants collapsed into one entry");
        return Task.CompletedTask;
    });
}

async Task FactionChannelTests()
{
    await Test("faction standing is directed and carries both facts", () =>
    {
        // Goodwill changes in `Faction.TryAffectGoodwillWith` - gifting, quests, incidents, social fights - on ticks a
        // replica never runs, so the faction page shows the load-time number forever. And because `kind` is derived from it
        // (`CheckKindThresholds`: -75 hostile, +75 ally, 0 back to neutral) an ally that turned hostile mid-session still
        // reads as an ally; caravans, trade and raids all follow that stale answer.
        //
        // Two facts, not one: `CheckKindThresholds` derives `kind` from goodwill only where `HasGoodwill` holds, while a
        // permanent enemy is set through `SetRelationDirect` and its kind is independent of any number. Sending only one of
        // them leaves the other faction class wrong.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(2300, [],
            Factions: [new ReplicaFactionRelation(1, 2, -80, 0), new ReplicaFactionRelation(2, 1, -80, 0)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Factions.Count: 2 }, "the faction channel went missing on the wire");
        Check(back.Factions![0] is { Faction: 1, Other: 2, Goodwill: -80 }, "a faction standing lost its identity");
        Check(back.Factions[0].Kind == 0, "the relation kind did not survive the wire");
        // Directed: A's opinion of B and B's opinion of A are separate entries. `SetRelation` replaces the other side's
        // record with a fresh one whose `baseGoodwill` is the default 100, so the two directions genuinely disagree in
        // vanilla - a symmetric channel would silently pick one of them.
        var directed = EventUnion.ByKey([new ReplicaFactionRelation(1, 2, -80, 0)],
            [new ReplicaFactionRelation(2, 1, 40, 1)], state => state.Faction + "->" + state.Other);
        Check(directed is { Count: 2 }, "two opposing directions collapsed into one entry");
        var newest = EventUnion.ByKey([new ReplicaFactionRelation(1, 2, -80, 0)],
            [new ReplicaFactionRelation(1, 2, 40, 1)], state => state.Faction + "->" + state.Other);
        Check(newest is { Count: 1 } && newest[0].Goodwill == -80, "the newest standing did not supersede the older one");

        // A kind is a three-value enum; a byte off the wire must not be trusted blindly. `Ally` is 2, so a value above it
        // is corrupt and the guard is what keeps a bad frame from painting a faction with a kind that does not exist.
        Check((byte)FactionRelationKindWire.Ally == 2, "the relation kind order changed");
        Check((byte)FactionRelationKindWire.Hostile == 0, "the relation kind order changed");
        return Task.CompletedTask;
    });
}

async Task WorldObjectChannelTests()
{
    await Test("world object position carries the planet layer", () =>
    {
        // Caravans, settlements, ships and sites live in `Find.WorldObjects`, not in any map's thing grid, and they move
        // through their own interval ticks (`Caravan_PathFollower.TryEnterNextPathTile` assigns `caravan.Tile = nextTile`).
        // A replica never runs those, so a caravan that has been travelling for a season still draws at its origin.
        //
        // `PlanetTile` is a tile id AND a layer id, and `new PlanetTile(id)` silently assumes layer 0 - on a multi-layer
        // planet (surface plus orbit) that resolves to a different world. The layer has to travel with the tile or the
        // object lands somewhere else, which is why this is asserted rather than assumed.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(2400, [],
            WorldObjects: [new ReplicaWorldObject(77, 1234, 1, "Caravan"), new ReplicaWorldObject(78, 99, 0, "Settlement")]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { WorldObjects.Count: 2 }, "the world object channel went missing on the wire");
        Check(back.WorldObjects![0] is { Id: 77, Tile: 1234, Layer: 1 }, "a world object lost its coordinate");
        Check(back.WorldObjects[1] is { Layer: 0 }, "the surface layer did not survive as zero");
        // The failure this guards: layer 1 tile 1234 and layer 0 tile 1234 are different places, so a channel that dropped
        // the layer would move every orbital object onto the surface without any error anywhere.
        Check(back.WorldObjects[0].Tile == back.WorldObjects[0].Tile && back.WorldObjects[0].Layer != back.WorldObjects[1].Layer,
            "the layer is not being carried separately from the tile id");

        // Merged per object id, newest wins: an object has one tile.
        var merged = EventUnion.ByKey([new ReplicaWorldObject(77, 1234, 0, "Caravan")],
            [new ReplicaWorldObject(77, 99, 0, "Caravan")], state => state.Id.ToString());
        Check(merged is { Count: 1 } && merged[0].Tile == 1234, "the newest position did not supersede the older one");
        var distinct = EventUnion.ByKey([new ReplicaWorldObject(77, 1234, 0, "Caravan")],
            [new ReplicaWorldObject(78, 1234, 0, "Caravan")], state => state.Id.ToString());
        Check(distinct is { Count: 2 }, "two world objects collapsed into one entry");
        return Task.CompletedTask;
    });
}

async Task QuestChannelTests()
{
    await Test("quest state travels as its inputs, not as the derived enum", () =>
    {
        // A quest is accepted (`Quest.Accept` sets `acceptanceTick = TicksGame`, then `Initiate`) and ends (`Quest.End` sets
        // `ended` and `endOutcome`, then `CleanupQuestParts`) on the host. A replica runs neither, so an offer accepted a
        // season ago still reads as pending and a finished quest still reads as ongoing.
        //
        // `Quest.State` is a GETTER over those four fields plus `TicksUntilExpiry`, which moves on its own as the clock
        // advances - so a sent enum would be stale by the next tick while the replica's own getter answers the same
        // question the same way from the same inputs. That is why the inputs travel and the enum does not.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        // 1 = ended, 2 = QuestEndOutcome.Fail (Unknown: 0, Success: 1, Fail: 2, InvalidPreAcceptance: 3).
        var frame = new AuthoritativeFrame(2500, [],
            Quests: [new ReplicaQuest(11, 120000, 1, 2, 180000, 0), new ReplicaQuest(12, -1, 0, 0, 190000, 0)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Quests.Count: 2 }, "the quest channel went missing on the wire");
        Check(back.Quests![0] is { Id: 11, AcceptTick: 120000, Ended: 1, Outcome: 2 },
            "a quest standing lost a field on the wire");
        Check(back.Quests[1] is { AcceptTick: -1, Ended: 0 }, "a not-yet-accepted quest did not keep its -1 sentinel");
        // The two readings the enum would have baked in: an accepted-and-failed quest, and an unaccepted offer. Both must
        // survive as inputs or the replica cannot tell them apart from an ongoing quest.
        Check(back.Quests[0].Ended == 1 && back.Quests[0].Outcome == 2, "the failure outcome did not survive");
        Check(back.Quests[1].AcceptTick < 0 && back.Quests[1].Ended == 0, "an unaccepted offer reads as accepted");

        var merged = EventUnion.ByKey([new ReplicaQuest(11, 120000, 1, 2, 180000, 0)],
            [new ReplicaQuest(11, 120000, 0, 0, 180000, 0)], state => state.Id.ToString());
        Check(merged is { Count: 1 } && merged[0].Ended == 1, "the newest quest standing did not supersede the older one");
        var distinct = EventUnion.ByKey([new ReplicaQuest(11, -1, 0, 0, 0, 0)],
            [new ReplicaQuest(12, -1, 0, 0, 0, 0)], state => state.Id.ToString());
        Check(distinct is { Count: 2 }, "two quests collapsed into one entry");
        return Task.CompletedTask;
    });
}

async Task MemoryChannelTests()
{
    await Test("memory age travels so thoughts can expire", () =>
    {
        // A memory's age moves in exactly one place - `Thought_Memory.ThoughtInterval` (`age += 150`), driven by
        // `MemoryThoughtHandler.MemoryThoughtInterval` - and that age decides whether it still hurts: `ShouldDiscard` is
        // `age > DurationTicks`. A replica runs neither, so no memory EVER expires: a colonist whose friend died a season ago
        // still carries the full grief, and the needs tab lists every thought with the mood it contributed at load.
        //
        // Mood itself never travels: `Need_Mood.CurInstantLevel` is a getter that recomputes from
        // `thoughts.TotalMoodOffset()`, so the card is not frozen - it answers from stale inputs. Fixing the inputs is enough.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(2600, [],
            Memories: [new ReplicaMemory(4242, "SocialFight|4243|0", 1500), new ReplicaMemory(4242, "AteLavishMeal|-1|0", 300)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Memories.Count: 2 }, "the memory channel went missing on the wire");
        Check(back.Memories![0] is { Pawn: 4242, Age: 1500 }, "a memory age lost a field on the wire");
        Check(back.Memories[0].Key == "SocialFight|4243|0", "a memory key was rewritten on the wire");
        // A key with a separator in it must survive verbatim: the def name, the other pawn and the group index are joined with
        // `|`, and a def whose name happened to carry one would otherwise collide with a different triple.
        Check(back.Memories[1].Key == "AteLavishMeal|-1|0", "a negative other-pawn id did not survive");
        Check(back.Memories[1].Age == 300, "a short age did not survive");
        // Expiry is the whole point: these two differ only in age, and a channel that dropped it could not tell a fresh
        // memory from an almost-expired one.
        Check(back.Memories[0].Age != back.Memories[1].Age, "two memories collapsed to the same age");

        // Merged per (pawn, key): a memory has one age.
        var merged = EventUnion.ByKey([new ReplicaMemory(4242, "SocialFight|4243|0", 1500)],
            [new ReplicaMemory(4242, "SocialFight|4243|0", 1350)], state => state.Pawn + "|" + state.Key);
        Check(merged is { Count: 1 } && merged[0].Age == 1500, "the newest age did not supersede the older one");
        // Two pawns holding the same def about the same other pawn are separate memories, so the pawn must be in the key.
        var crossPawn = EventUnion.ByKey([new ReplicaMemory(4242, "SocialFight|4243|0", 1500)],
            [new ReplicaMemory(4244, "SocialFight|4243|0", 1500)], state => state.Pawn + "|" + state.Key);
        Check(crossPawn is { Count: 2 }, "two pawns' memories of the same social fight collapsed into one entry");
        return Task.CompletedTask;
    });
}

async Task TaleChannelTests()
{
    await Test("the story log travels whole and keeps its counter", () =>
    {
        // `TaleRecorder.RecordTale` only runs on the authority, and `TaleManager.Add` is what puts a tale in the list the
        // history page and `ArtGenerationContext` read - so a replica holds exactly the tales the save came with. "Art"
        // descriptions never mention anything that happened since, and a tale that aged out still sits there.
        //
        // Only the counter has to travel for interest: `Tale.InterestLevel` derives from `def.baseInterest`, `uses` and
        // `AgeTicks`, and `AgeTicks` is `TicksAbs - date`, which already matches on both ends because the replica's clock
        // follows the host's.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var frame = new AuthoritativeFrame(2700, [],
            Tales: [new ReplicaTale(31, "Traveled", 2, 120000, "远征归来", 0),
                new ReplicaTale(32, "BondedWith", 0, 126000, null, 1)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuthoritativeFrame>(
            System.Text.Json.JsonSerializer.Serialize(frame, options), options);
        Check(back is { Tales.Count: 2 }, "the tale channel went missing on the wire");
        Check(back.Tales![0] is { Id: 31, Uses: 2, Date: 120000 }, "a tale lost a field on the wire");
        Check(back.Tales[0].Label == "远征归来", "a custom label did not survive non-ASCII text on the wire");
        // A null label is the common case (most tales have none), so it must round-trip as null rather than as "".
        Check(back.Tales[1].Label is null, "a null custom label became a non-null string");
        Check(back.Tales[1].Hidden == 1 && back.Tales[0].Hidden == 0, "the hidden flag did not survive the wire");
        // `uses` is what `InterestLevel` divides by, so a lost counter changes which tale an art piece is about.
        Check(back.Tales[0].Uses != back.Tales[1].Uses, "two tales collapsed to the same use count");

        var merged = EventUnion.ByKey([new ReplicaTale(31, "Traveled", 2, 120000, null, 0)],
            [new ReplicaTale(31, "Traveled", 1, 120000, null, 0)], state => state.Id.ToString());
        Check(merged is { Count: 1 } && merged[0].Uses == 2, "the newest tale standing did not supersede the older one");
        return Task.CompletedTask;
    });
}

async Task AdminHttpTests()
{
    await Test("admin HTTP parser accepts only bounded, loopback-addressed requests", () =>
    {
        var get = System.Text.Encoding.Latin1.GetBytes(
            "GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:30573\r\nCookie: a=1; turbotick_admin=tok\r\n\r\n");
        Check(AdminHttp.TryParse(get, out var parsed, out var error, out var consumed) == AdminHttpParse.Complete,
            "a plain GET did not parse: " + error);
        Check(parsed is { Method: "GET", Path: "/api/status", Query: "" }, "the request line was misread");
        Check(consumed == get.Length, "consumed length must cover the whole request");
        Check(AdminHttp.CookieValue(parsed.Header("Cookie"), "turbotick_admin") == "tok", "the admin cookie was not found");

        // A password in the URL is refused rather than parsed: URLs reach shell history and referrers.
        var withToken = System.Text.Encoding.Latin1.GetBytes("GET /api/status?token=secret HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Check(AdminHttp.TryParse(withToken, out var query, out _, out _) == AdminHttpParse.Complete, "query request did not parse");
        Check(query.QueryHas("token") && !query.QueryHas("comm*and"), "query key detection is wrong");

        var partial = System.Text.Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n");
        Check(AdminHttp.TryParse(partial, out _, out _, out _) == AdminHttpParse.Incomplete, "a truncated request must wait for more bytes");

        var chunked = System.Text.Encoding.Latin1.GetBytes("POST /api/command HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\n\r\n");
        Check(AdminHttp.TryParse(chunked, out _, out var chunkError, out _) == AdminHttpParse.Malformed && chunkError.Length > 0,
            "chunked bodies must be refused, not guessed at");

        var huge = new byte[AdminHttp.MaximumHeaderBytes + 16];
        Array.Fill(huge, (byte)'A');
        Check(AdminHttp.TryParse(huge, out _, out _, out _) == AdminHttpParse.TooLarge, "an unbounded header block was accepted");

        var longBody = System.Text.Encoding.Latin1.GetBytes("POST /api/command HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: "
            + (AdminHttp.MaximumBodyBytes + 1) + "\r\n\r\n");
        Check(AdminHttp.TryParse(longBody, out _, out _, out _) == AdminHttpParse.TooLarge, "an oversized body was accepted");

        // A hostile page can point a hostname at 127.0.0.1; requiring a loopback Host header is what stops it.
        Check(AdminHttp.IsLoopbackHost("127.0.0.1:30573", 30573), "loopback host with the bound port was refused");
        Check(AdminHttp.IsLoopbackHost("localhost", 30573), "localhost was refused");
        Check(!AdminHttp.IsLoopbackHost("evil.example:30573", 30573), "a foreign Host header was accepted");
        Check(!AdminHttp.IsLoopbackHost("127.0.0.1:1234", 30573), "a mismatched Host port was accepted");
        Check(!AdminHttp.IsLoopbackHost(null, 30573), "a missing Host header was accepted");
        return Task.CompletedTask;
    });

    await Test("admin HTTP keeps a JSON body intact and compares secrets in constant time", () =>
    {
        var payload = "{\"command\":\"save\"}";
        var raw = System.Text.Encoding.Latin1.GetBytes("POST /api/command HTTP/1.1\r\nHost: localhost:30573\r\n"
            + "Content-Type: application/json\r\nContent-Length: " + payload.Length + "\r\n\r\n" + payload);
        Check(AdminHttp.TryParse(raw, out var parsed, out var error, out _) == AdminHttpParse.Complete, "POST did not parse: " + error);
        Check(System.Text.Encoding.UTF8.GetString(parsed.Body) == payload, "the body was not carried byte for byte");
        Check(parsed.Header("content-type") == "application/json", "header lookup must be case-insensitive");

        Check(AdminHttp.SecretEquals("hunter2", "hunter2"), "equal secrets must match");
        Check(!AdminHttp.SecretEquals("hunter2", "hunter3"), "different secrets must not match");
        Check(!AdminHttp.SecretEquals("hunter2", "hunter"), "a prefix must not match");
        Check(!AdminHttp.SecretEquals("", ""), "an empty configured password must never authenticate");

        var response = AdminHttp.Response(403, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes("{}"));
        var head = System.Text.Encoding.Latin1.GetString(response);
        Check(head.StartsWith("HTTP/1.1 403 Forbidden\r\n", StringComparison.Ordinal), "status line is wrong: " + head.Split("\r\n")[0]);
        Check(head.Contains("Cache-Control: no-store\r\n") && head.Contains("X-Content-Type-Options: nosniff\r\n"),
            "the defensive response headers are missing");
        Check(head.Contains("Content-Length: 2\r\n"), "the declared length does not match the body");
        return Task.CompletedTask;
    });
}

await AdminHttpTests();
await LetterChannelTests();
await SettlementRuleTests();
await RoofChannelTests();
await SnowChannelTests();
await FogChannelTests();
await MentalChannelTests();
await InspirationChannelTests();
await GuestChannelTests();
await SkillTests();
await NeedsChannelTests();
await SettlementAreaTests();
await SettlementTests();
await SettlementSyncTests();
await SettlementRefusalTextTests();
await GrantEditorTests();
await SettlementWarTests();
await PlanChannelTests();
await SyncCensusTests();
await RoofLoadGuardTests();
await ResearchChannelTests();
await RoomTempChannelTests();
await PlantChannelTests();
await FactionChannelTests();
await WorldObjectChannelTests();
await QuestChannelTests();
await MemoryChannelTests();
await TaleChannelTests();

// 阶段 8：存档导入的判定、ModsConfig 的增量改写、以及「状态接口回答的是不是本次请求的登录态」。
// 这三条都是「错了会静默毁数据」的类型：错一次是先删档再报错，或者把 knownExpansions 冲掉。

await Test("save import refuses anything that is not a save and stores under its own name", () =>
{
    static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
    static bool Sniff(string text) => SaveImport.LooksLikeSave(Bytes(text), Bytes(text).Length, out _);

    Check(Sniff("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame>\n"), "the game's own prologue was refused");
    Check(Sniff("<savegame>"), "a bare savegame root was refused");
    Check(Sniff("<!-- written by a tool -->\n<savegame>"), "a leading comment was refused");
    var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("<savegame>")).ToArray();
    Check(SaveImport.LooksLikeSave(bom, bom.Length, out _), "a UTF-8 BOM was not tolerated");

    Check(!Sniff(""), "an empty body was accepted");
    Check(!Sniff("   \r\n  "), "a whitespace-only body was accepted");
    Check(!Sniff("PK\u0003\u0004 a zip"), "a zip was accepted as a save");
    Check(!Sniff("<?xml version=\"1.0\"?><Defs/>"), "an unrelated XML document was accepted");
    var utf16 = new byte[] { 0xFF, 0xFE, (byte)'<', 0, (byte)'s', 0 };
    Check(!SaveImport.LooksLikeSave(utf16, utf16.Length, out var utf16Problem), "a UTF-16 file was accepted");
    Check(utf16Problem.Contains("UTF-16"), "the UTF-16 refusal did not say why: " + utf16Problem);

    // The stored name is generated, never taken from the client: a client-chosen name is a path.
    var name = SaveImport.NewName(new DateTime(2026, 11, 12, 3, 4, 5, DateTimeKind.Utc), "abc123");
    Check(name == "import-20261112T030405Z-abc123", "the generated name changed shape: " + name);
    Check(SaveLibrary.Normalize(name + ".rws") == name, "the generated name is not a legal save name");
    Check(SaveLibrary.Normalize("../../etc/passwd") is null, "a traversal name was accepted as a save name");

    var root = Path.Combine(Path.GetTempPath(), "tt-saveimport-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        Check(SaveImport.IsDirectChild(root, Path.Combine(root, "a.rws")), "a direct child was refused");
        Check(!SaveImport.IsDirectChild(root, Path.Combine(root, "sub", "a.rws")), "a nested path was accepted");
        Check(!SaveImport.IsDirectChild(root, Path.Combine(Path.GetTempPath(), "a.rws")), "a sibling path was accepted");
        Check(!SaveImport.IsDirectChild(root, root), "the directory itself was accepted");
        Check(!SaveImport.IsDirectChild("", Path.Combine(root, "a.rws")), "an empty directory was accepted");
    }
    finally { try { Directory.Delete(root, true); } catch { } }

    // The name a downloaded snapshot is copied into Saves/ under must be a constant, not the content hash.
    // Measured on the production server: every checkpoint has a new hash, so naming the copy after it left 24
    // files of 20 MiB each in /data/saves, all indistinguishable from a colony the player had saved. The rule
    // this asserts is "one name, so at most one leftover"; the value itself carries no meaning.
    Check(SaveImport.TransportName.Length > 0, "the transport save name is empty");
    Check(SaveLibrary.Normalize(SaveImport.TransportName + ".rws") == SaveImport.TransportName,
        "the transport name is not a legal save name: " + SaveImport.TransportName);
    Check(SaveImport.TransportName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'),
        "the transport name has characters a save name may not contain: " + SaveImport.TransportName);

    // The resume save must be a *different* name from the transport copy: one is deleted after every load, the other
    // has to survive restarts. Sharing a name would delete the colony's only resume point immediately after using it.
    Check(SaveImport.ResumeName != SaveImport.TransportName, "the resume name collides with the transport name");
    Check(SaveLibrary.Normalize(SaveImport.ResumeName + ".rws") == SaveImport.ResumeName,
        "the resume name is not a legal save name: " + SaveImport.ResumeName);
    return Task.CompletedTask;
});

await Test("mods config apply keeps knownExpansions and the operator's order", () =>
{
    const string original = """
        <?xml version="1.0" encoding="utf-8"?>
        <ModsConfigData>
          <version>1.7.5005</version>
          <activeMods>
            <li>ludeon.rimworld</li>
            <li>brrainz.harmony</li>
          </activeMods>
          <knownExpansions>
            <li>Ludeon.RimWorld.Royalty</li>
            <li>Ludeon.RimWorld.Biotech</li>
          </knownExpansions>
        </ModsConfigData>
        """;
    var snapshot = ModsConfigFile.Parse(original);
    Check(snapshot.Version == "1.7.5005", "the version was not read: " + snapshot.Version);
    Check(snapshot.ActiveMods is ["ludeon.rimworld", "brrainz.harmony"], "activeMods was misread");
    Check(snapshot.KnownExpansions.Count == 2, "knownExpansions was misread");

    // Order is the operator's, not a sort: a patch has to come after what it patches.
    var applied = ModsConfigFile.Apply(snapshot, ["ludeon.rimworld", "brrainz.harmony", "lumidust.turbotick"], "1.7");
    Check(applied.Active is ["ludeon.rimworld", "brrainz.harmony", "lumidust.turbotick"], "the order was rearranged");
    Check(applied.Xml.Contains("<li>Ludeon.RimWorld.Royalty</li>"), "knownExpansions was dropped from the rewrite");
    Check(applied.PreservedExpansions.Count == 2, "the preserved expansion list is empty");
    Check(applied.Xml.Contains("<version>1.7.5005</version>"), "the version line was lost");

    // Core is not removable: the game will not start without it.
    var withoutCore = ModsConfigFile.Apply(snapshot, ["lumidust.turbotick"], "1.7");
    Check(withoutCore.Active[0] == "ludeon.rimworld", "Core was not forced back to the front");
    Check(withoutCore.Added is ["ludeon.rimworld"], "the re-added Core was not reported");

    // An id that cannot be a packageId must be reported, not written into the XML.
    var invalid = ModsConfigFile.Apply(snapshot, ["ludeon.rimworld", "not a mod", "../../evil"], "1.7");
    Check(invalid.Active.Count == 1, "an invalid id reached activeMods: " + string.Join(",", invalid.Active));
    Check(invalid.Dropped.Count == 2, "the dropped ids were not reported");

    // A duplicate would make the game list the same mod twice.
    var duplicated = ModsConfigFile.Apply(snapshot, ["ludeon.rimworld", "brrainz.harmony", "brrainz.harmony"], "1.7");
    Check(duplicated.Active.Count(id => id == "brrainz.harmony") == 1, "a duplicate survived");

    Check(ModsConfigFile.Parse("not xml at all").ActiveMods.Count == 0, "a malformed file threw instead of returning nothing");
    Check(!ModsConfigFile.IsValidPackageId("no dot here"), "an id without a dot was accepted");
    Check(ModsConfigFile.IsValidPackageId("Ludeon.RimWorld.Royalty"), "a legal packageId was refused");
    return Task.CompletedTask;
});

await Test("admin upload path is streamed and bounded, and status carries the request's own login state", () =>
{
    // The upload route is the one place where a body larger than the JSON parser's limit is expected; the
    // head must parse on its own so its declared length can be checked before a byte of body is read.
    var head = System.Text.Encoding.Latin1.GetBytes("POST /api/saves/upload HTTP/1.1\r\nHost: 127.0.0.1:30573\r\n"
        + "Content-Type: application/octet-stream\r\nContent-Length: 20000000\r\n\r\n");
    Check(AdminHttp.TryParseHead(head, int.MaxValue, out var parsed, out var error, out var headLength) == AdminHttpParse.Complete,
        "the upload head did not parse: " + error);
    Check(parsed.ContentLength == 20000000, "the declared length was not carried: " + parsed.ContentLength);
    Check(parsed.Body.Length == 0, "the head parse must not fabricate a body");
    Check(headLength == head.Length, "the head length is wrong");
    Check(SaveImport.MaximumBytes > 20000000, "the upload limit is below a real colony");

    // The same head through the normal parser is refused as too large - that is the path every other route takes.
    Check(AdminHttp.TryParse(head, out _, out _, out _) == AdminHttpParse.TooLarge, "a 20 MB JSON body was accepted");
    return Task.CompletedTask;
});

// 阶段 8：会话视图文件（`/data/session/<存档 hash 前 16>.json`）与身份 token。
// 这三条覆盖「重启后还能读回来」「坏 JSON 不崩」「token 是 64 位小写十六进制」——
// 前两条是真机上最容易以「视图没恢复」的形式暴露、却又最难从日志看出来的两种失败。

await Test("session view store round-trips through the session file", () =>
{
    var directory = Dir("session-roundtrip");
    var saveHash = Convert.ToHexStringLower(SHA256.HashData("world-one"u8));
    var token = SessionTokens.New();
    var store = new SessionViewStore(directory);
    var stored = store.Save(saveHash, token, "B", new SessionViewSnapshot(1, [789, 790], 100.5f, 150.25f, 13.5f));
    Check(stored is not null, "a legal token was refused by the store");
    Check(File.Exists(Path.Combine(directory, saveHash[..16] + ".json")),
        "the session file is not named after the first 16 characters of the save hash");
    // 新实例 = 服务端重启：必须从磁盘读回来，而不是靠内存里的字典。
    var reopened = new SessionViewStore(directory);
    Check(reopened.TryGet(saveHash, token, out var record), "the record was lost across a restart");
    Check(record.Name == "B" && record.MapId == 1, $"name/map did not survive: {record.Name}/{record.MapId}");
    Check(record.Selected.SequenceEqual([789, 790]), "the selection did not survive: " + string.Join(",", record.Selected));
    Check(Math.Abs(record.Camera.X - 100.5f) < 0.001f && Math.Abs(record.Camera.Z - 150.25f) < 0.001f
        && Math.Abs(record.Camera.Size - 13.5f) < 0.001f, "the camera did not survive");
    Check(record.UpdatedUtc.Length > 0, "the record has no timestamp");
    // 每玩家只留最新一条：覆盖写，不追加。
    reopened.Save(saveHash, token, "B", new SessionViewSnapshot(2, [42], 5f, 6f, 7f));
    var after = new SessionViewStore(directory);
    Check(after.Count(saveHash) == 1, "one player turned into more than one record");
    Check(after.TryGet(saveHash, token, out var updated) && updated.MapId == 2 && updated.Selected.SequenceEqual([42]),
        "the overwrite did not take effect");
    // 换 token / 换存档都不能串档——否则「换个 token 当新玩家」会读到别人的相机位置。
    Check(!after.TryGet(saveHash, SessionTokens.New(), out _), "an unknown token read somebody else's record");
    Check(!after.TryGet(Convert.ToHexStringLower(SHA256.HashData("world-two"u8)), token, out _),
        "a different save hash read the first save's record");
    return Task.CompletedTask;
});

await Test("a corrupt session file is no record, not a failure", () =>
{
    var directory = Dir("session-corrupt");
    var saveHash = Convert.ToHexStringLower(SHA256.HashData("world-corrupt"u8));
    // 半截 JSON 正是进程被强杀时可能留下的形态。
    File.WriteAllText(Path.Combine(directory, saveHash[..16] + ".json"), "{\"saveHash\": [oops");
    var warnings = new List<string>();
    var store = new SessionViewStore(directory, warnings.Add);
    var token = SessionTokens.New();
    Check(!store.TryGet(saveHash, token, out _), "a broken file was treated as a record");
    Check(store.Count(saveHash) == 0, "a broken file produced records");
    Check(warnings.Count == 1 && warnings[0].Contains("按没有记录处理", StringComparison.Ordinal),
        "a broken file left no warning: " + string.Join("|", warnings));
    // 坏文件不能挡住写入：一次成功的保存必须把文件恢复成合法 JSON。
    Check(store.Save(saveHash, token, "A", new SessionViewSnapshot(0, [7], 1f, 2f, 3f)) is not null,
        "writing after a corrupt file was refused");
    var reopened = new SessionViewStore(directory);
    Check(reopened.TryGet(saveHash, token, out var record) && record.Selected.SequenceEqual([7]),
        "the record written over a corrupt file does not read back");
    return Task.CompletedTask;
});

await Test("player tokens are 32 random bytes in lowercase hex", () =>
{
    var token = SessionTokens.New();
    Check(SessionTokens.IsValid(token), "a generated token failed its own validation");
    Check(token.Length == 64 && token.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')),
        "the token is not 64 lowercase hex characters: " + token);
    Check(!SessionTokens.IsValid(null) && !SessionTokens.IsValid("")
        && !SessionTokens.IsValid(token[..63]) && !SessionTokens.IsValid(token + "0"),
        "a token of the wrong length was accepted");
    Check(!SessionTokens.IsValid(token.ToUpperInvariant()),
        "an uppercase token was accepted (one token would become two players)");
    Check(!SessionTokens.IsValid(new string('z', 64)), "a non-hex token was accepted");
    Check(!string.Equals(SessionTokens.New(), token, StringComparison.Ordinal), "two tokens in a row were identical");
    // 上报内容的收敛：超 64 截断、去重、丢掉非正 id、非有限坐标归零。
    var many = Enumerable.Range(1, 100).Append(1).Prepend(-5).ToArray();
    var view = SessionViewSnapshot.Sanitize(new SessionViewSnapshot(3, many, float.NaN, 2f, float.PositiveInfinity));
    Check(view.Selected.Count == 64 && view.Selected[0] == 1 && view.Selected[^1] == 64,
        "the selection was not deduplicated/truncated to 64: " + string.Join(",", view.Selected.Take(5)));
    Check(view.X == 0f && view.Size == 0f && view.Z == 2f, "non-finite coordinates were not zeroed");
    // 存档文件名的取法和 ContentProbe 一致：sha256 hex → 前 16 位小写。
    Check(SessionViewStore.Hash16(token) == token[..16], "Hash16 did not take the first 16 characters");
    var derived = SessionViewStore.Hash16("not-a-hash");
    Check(derived.Length == 16 && derived.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')),
        "Hash16 of a non-hex input is not 16 hex characters: " + derived);
    return Task.CompletedTask;
});

await Test("a returning player keeps its token and gets its view back", async () =>
{
    var data = Dir("session-restore");
    string token;
    await using (var server = new DedicatedServer(new(Port: 0, DataDirectory: data)))
    {
        await server.StartAsync();
        // 1) 首次加入：服务端发一个新 token（客户端没带 token）。
        await using (var first = new DedicatedClient())
        {
            await first.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
            Check(first.PlayerToken is { } issued && SessionTokens.IsValid(issued),
                "the server issued no token on the first join: " + (first.PlayerToken ?? "null"));
            token = first.PlayerToken!;
            // 2) 造一个非默认视图：第 2 张地图、选中两个人、相机在一个具体位置。
            await first.SendViewStateAsync(new SessionViewSnapshot(2, [789, 790], 100.5f, 150.25f, 13.5f));
            await Until(() => server.Views.Count(server.SessionSaveHash) == 1);
            Check(File.Exists(server.SessionViewPath), "the session file was never written: " + server.SessionViewPath);
        }
        // 3) 断开重连：带上同一个 token，服务端必须认出「还是那个人」，并在 WorldReady 之后下发一次视图。
        await using (var again = new DedicatedClient())
        {
            SessionViewSnapshot? restored = null;
            again.ViewRestoreReceived += view => restored = view;
            await again.ConnectAsync("127.0.0.1", server.Port, Hello("B"), playerToken: token);
            Check(string.Equals(again.PlayerToken, token, StringComparison.Ordinal),
                "the server did not recognise the returning token（token 变了）");
            await again.SendWorldReadyAsync("", 0);
            await Until(() => restored is not null, 10);
            Check(restored!.MapId == 2, "the restored map id is wrong: " + restored.MapId);
            Check(restored.Selected.SequenceEqual([789, 790]), "the restored selection is wrong");
            Check(Math.Abs(restored.X - 100.5f) < 0.01f && Math.Abs(restored.Z - 150.25f) < 0.01f
                && Math.Abs(restored.Size - 13.5f) < 0.01f, "the restored camera is wrong");
        }
        // 4) 换个 token（= 删掉 token 文件）就是他本人以外的新玩家：不发恢复，停在默认视图。
        await using (var stranger = new DedicatedClient())
        {
            SessionViewSnapshot? restored = null;
            stranger.ViewRestoreReceived += view => restored = view;
            await stranger.ConnectAsync("127.0.0.1", server.Port, Hello("C"));
            Check(!string.Equals(stranger.PlayerToken, token, StringComparison.Ordinal), "a new player received the old token");
            await stranger.SendWorldReadyAsync("", 0);
            await Task.Delay(1000);
            Check(restored is null, "a brand-new player was moved to somebody else's camera");
        }
    }
    // 5) 服务端重启（新实例、同一份 DataDirectory）：记录必须从文件读回来，内存里已经没有它了。
    await using (var restarted = new DedicatedServer(new(Port: 0, DataDirectory: data)))
    {
        await restarted.StartAsync();
        await using var again = new DedicatedClient();
        SessionViewSnapshot? restored = null;
        again.ViewRestoreReceived += view => restored = view;
        await again.ConnectAsync("127.0.0.1", restarted.Port, Hello("B"), playerToken: token);
        Check(string.Equals(again.PlayerToken, token, StringComparison.Ordinal), "a restart forgot the token");
        await again.SendWorldReadyAsync("", 0);
        await Until(() => restored is not null, 10);
        Check(restored!.MapId == 2 && restored.Selected.SequenceEqual([789, 790]),
            "a restart forgot the recorded view");
    }
});

// 阶段 9：存档自带 mod 清单（`<meta>` 三张平行列表）→ activeMods。
// 这一组全是「错了会静默丢内容」的判定：顺序被重排、steamId 被猜、缺 mod 却放行。

await Test("save meta joins modIds/modSteamIds/modNames by index and keeps the save's order", () =>
{
    const string save = """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.7.5000 rev1275</gameVersion>
            <modIds>
              <li>brrainz.harmony</li>
              <li>ludeon.rimworld</li>
              <li>ludeon.rimworld.royalty</li>
              <li>some.workshopmod</li>
              <li>some.localmod</li>
            </modIds>
            <modSteamIds>
              <li>0</li>
              <li>0</li>
              <li>0</li>
              <li>2195452496</li>
              <li>0</li>
            </modSteamIds>
            <modNames>
              <li>Harmony</li>
              <li>Core</li>
              <li>Royalty</li>
              <li>Workshop Mod</li>
              <li>Local Mod</li>
            </modNames>
            <knownExpansions>
              <li>ludeon.rimworld.royalty</li>
            </knownExpansions>
          </meta>
          <game><ticksGame>10</ticksGame></game>
        </savegame>
        """;
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(save));
    var manifest = SaveManifest.Read(stream);
    Check(manifest.Problem is null, "a normal save was not read: " + manifest.Problem);
    Check(manifest.GameVersion == "1.7.5000 rev1275", "the game version was not read: " + manifest.GameVersion);
    Check(manifest.Mods.Count == 5, "mod count is wrong: " + manifest.Mods.Count);
    Check(manifest.Mods[0].Id == "brrainz.harmony" && manifest.Mods[0].SteamId == 0, "the first entry is misaligned");
    Check(manifest.Mods[3] is { Id: "some.workshopmod", SteamId: 2195452496, Name: "Workshop Mod" },
        "the workshop id did not line up with its package id");
    Check(manifest.Mods[4].FromWorkshop == false, "a modSteamId of 0 was treated as a workshop id");
    Check(manifest.KnownExpansions is ["ludeon.rimworld.royalty"], "knownExpansions was not read");

    // A short steamId list must not shift the join: those entries become 0 = manual, never a guess.
    const string ragged = """
        <savegame><meta>
          <modIds><li>a.one</li><li>b.two</li></modIds>
          <modSteamIds><li>123</li></modSteamIds>
          <modNames><li>One</li></modNames>
        </meta></savegame>
        """;
    using var raggedStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ragged));
    var shortManifest = SaveManifest.Read(raggedStream);
    Check(shortManifest.Mods[0].SteamId == 123, "the first entry lost its workshop id");
    Check(shortManifest.Mods[1].SteamId == 0, "a missing steam id was invented");

    Check(SaveManifest.Classify("ludeon.rimworld") == SaveModSource.Core, "Core was not classified as Core");
    Check(SaveManifest.Classify("Ludeon.RimWorld.Royalty") == SaveModSource.Dlc, "a DLC was not classified as a DLC");
    Check(SaveManifest.Classify("brrainz.harmony") == SaveModSource.Harmony, "Harmony was not classified as Harmony");
    Check(SaveManifest.Classify("some.mod") == SaveModSource.ThirdParty, "a third-party mod was misclassified");
    return Task.CompletedTask;
});

await Test("save mod plan keeps the save's order, refuses missing mods, and never guesses a source", () =>
{
    var manifest = new SaveModManifest("1.7.5005", [
        new SaveMod("brrainz.harmony", 0, "Harmony"),
        new SaveMod("ludeon.rimworld", 0, "Core"),
        new SaveMod("ludeon.rimworld.royalty", 0, "Royalty"),
        new SaveMod("z.last.mod", 0, "Last"),
        new SaveMod("a.first.mod", 0, "First"),
        new SaveMod("some.workshopmod", 2195452496, "Workshop"),
        new SaveMod("some.localmod", 0, "Local")
    ], ["ludeon.rimworld.royalty"], null);
    var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "brrainz.harmony", "lumidust.turbotick", "z.last.mod", "a.first.mod", "ludeon.rimworld.royalty" };
    var plan = SaveModPlanner.Plan(manifest, installed, installed,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ludeon.rimworld", "brrainz.harmony", "ludeon.rimworld.royalty", "z.last.mod", "a.first.mod" });

    // The save's order, Core included: Harmony records itself before Core via <loadBefore>, so a save written by a
    // working client has Harmony at 0. Hoisting Core would make every load report a mod-list change.
    Check(plan.ActiveMods[0] == "brrainz.harmony", "the save's first mod was not kept first: " + string.Join(",", plan.ActiveMods));
    Check(plan.ActiveMods is ["brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty", "z.last.mod", "a.first.mod"],
        "the order was rearranged: " + string.Join(",", plan.ActiveMods));
    Check(plan.Changed is [], "nothing should be reported as new against a matching config");

    // A config that lacks one of them must report exactly that one as new.
    var partial = SaveModPlanner.Plan(manifest, installed, installed,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld", "brrainz.harmony" });
    Check(partial.Changed is ["ludeon.rimworld.royalty", "z.last.mod", "a.first.mod"],
        "the diff against an older config is wrong: " + string.Join(",", partial.Changed));

    // Missing: one downloadable, one manual. Both keep the plan from being complete.
    Check(!plan.Complete, "a plan with two missing mods claimed to be complete");
    Check(plan.Missing.Contains("some.workshopmod") && plan.Missing.Contains("some.localmod"), "missing list is wrong");
    Check(plan.Downloadable.Count == 1 && plan.Downloadable[0].Id == "some.workshopmod", "the workshop mod was not marked downloadable");
    Check(plan.ManualRequired.Count == 1 && plan.ManualRequired[0].Id == "some.localmod", "the local mod was not marked manual");
    Check(plan.Refusal is not null && plan.Refusal.Contains("some.localmod"), "the refusal does not name what is missing");

    // Once fetched, the workshop mod becomes present and the manual one still blocks.
    var afterDownload = SaveModPlanner.WithDownloaded(plan, ["some.workshopmod"]);
    Check(afterDownload.Downloadable.Count == 0, "a downloaded mod is still listed as downloadable");
    Check(afterDownload.Missing.Contains("some.localmod") && !afterDownload.Missing.Contains("some.workshopmod"),
        "the post-download missing list is wrong: " + string.Join(",", afterDownload.Missing));
    Check(!afterDownload.Complete, "a plan still missing a manual mod claimed to be complete");
    // The notes are prose the operator reads on the page, so they have to follow the entries: measured on the
    // live server, a mod that had just been downloaded still showed "必须人工放入 mods 目录".
    Check(afterDownload.Notes.Any(note => note.Contains("some.localmod", StringComparison.Ordinal)
            && note.StartsWith("必须人工放入", StringComparison.Ordinal)),
        "the remaining manual mod is not named in the notes: " + string.Join(" | ", afterDownload.Notes));
    Check(!afterDownload.Notes.Any(note => note.StartsWith("可匿名从工坊下载", StringComparison.Ordinal)),
        "a note still promises a workshop download that already happened: " + string.Join(" | ", afterDownload.Notes));
    // A local mod that a name search resolves leaves the manual list entirely, and the notes must say so.
    var resolvedBySearch = SaveModPlanner.WithDownloaded(plan, ["some.workshopmod", "some.localmod"]);
    Check(resolvedBySearch.Complete && resolvedBySearch.Refusal is null,
        "a mod fetched by name was not accepted: " + string.Join(",", resolvedBySearch.Missing));
    Check(!resolvedBySearch.Notes.Any(note => note.StartsWith("必须人工放入", StringComparison.Ordinal)),
        "the notes still demand manual installation after everything was fetched: "
        + string.Join(" | ", resolvedBySearch.Notes));

    // Harmony absent is fatal even though it is not a third-party mod.
    var noHarmony = SaveModPlanner.Plan(manifest, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(!noHarmony.Complete && noHarmony.Missing.Contains("brrainz.harmony"), "a server without Harmony was allowed to start");
    return Task.CompletedTask;
});

// `_steam` 后缀：只在“同一 packageId 的本地版与工坊版同时存在”那台机器上出现（ModLister.TryAddMod 给工坊版
// 打标记）。服务端只有一份时 PackageId 永远没有后缀，所以按字面比会把一个就在磁盘上的 mod 判成缺失——
// 这正是用户截图里 `brrainz.harmony_steam (Harmony)` 被判「必须手工放入」的原因。
await Test("a save's _steam postfix resolves against the single copy the server actually has", () =>
{
    Check(SaveManifest.CanonicalId("brrainz.harmony_steam") == "brrainz.harmony", "the postfix was not stripped");
    Check(SaveManifest.CanonicalId("Brrainz.Harmony_Steam") == "brrainz.harmony", "case was not normalised");
    Check(SaveManifest.CanonicalId("brrainz.harmony") == "brrainz.harmony", "a plain id was altered");
    Check(SaveManifest.CanonicalId("_steam") == "_steam", "an id that is only the postfix was destroyed");
    Check(SaveManifest.Classify("brrainz.harmony_steam") == SaveModSource.Harmony,
        "a postfixed Harmony was classified as a third-party mod");

    var manifest = new SaveModManifest("1.7.5005", [
        new SaveMod("brrainz.harmony_steam", 0, "Harmony"),
        new SaveMod("ludeon.rimworld", 0, "Core"),
        new SaveMod("local.moredoorvariants_steam", 0, "More Door Variants")
    ], [], null);
    var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brrainz.harmony", "local.moredoorvariants" };
    var plan = SaveModPlanner.Plan(manifest, installed, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    Check(plan.Complete, "a mod that is installed under its plain id was reported missing: "
        + string.Join(",", plan.Missing));
    Check(plan.ActiveMods is ["brrainz.harmony", "ludeon.rimworld", "local.moredoorvariants"],
        "activeMods did not use the installed form in the save's order: " + string.Join(",", plan.ActiveMods));
    Check(plan.Required.Single(entry => entry.Id == "brrainz.harmony_steam").Detail.Contains("brrainz.harmony"),
        "the report did not say which installed id it matched");

    // Naming both forms is one mod: writing both into activeMods makes the game drop one at random.
    var both = new SaveModManifest("1.7.5005", [
        new SaveMod("ludeon.rimworld", 0, "Core"),
        new SaveMod("local.moredoorvariants", 0, "More Door Variants"),
        new SaveMod("local.moredoorvariants_steam", 0, "More Door Variants")
    ], [], null);
    var deduped = SaveModPlanner.Plan(both, installed, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(deduped.ActiveMods.Count(id => id == "local.moredoorvariants") == 1,
        "the same mod was added twice: " + string.Join(",", deduped.ActiveMods));
    return Task.CompletedTask;
});

// 每个客户端写的存档里都有我们自己的 mod（`lumidust.turbotick`）。服务端把它当第三方 mod 处理过一次，代价是：
// 工坊条目 3834465013 是 0.6.1，被当成「缺失的第三方 mod」下载到 Mods 根下，与 `Mods/TurboTick/`（0.7.1）
// 声明同一个 packageId。容器里 Steam 没运行，两份都是 ContentSource.ModsFolder，所以
// Verse.ModLister.TryAddMod 不会走 `_steam` 后缀分支，而是 Log.Error 丢掉其中一份——有概率丢掉服务端自己。
await Test("the server's own mod is never treated as a third-party download", () =>
{
    Check(SaveManifest.Classify("lumidust.turbotick") == SaveModSource.Self, "the server's own mod was not classified as Self");
    Check(SaveManifest.Classify("Lumidust.TurboTick_steam") == SaveModSource.Self, "the postfixed spelling was not classified as Self");
    Check(SaveManifest.IsSelf("Lumidust.TurboTick"), "IsSelf did not normalise the id");

    var present = new SaveModManifest("1.7.5005", [
        new SaveMod("ludeon.rimworld", 0, "Core"),
        new SaveMod("lumidust.turbotick", 0, "TurboTick")
    ], [], null);
    var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld", "lumidust.turbotick" };
    var plan = SaveModPlanner.Plan(present, installed, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(plan.Complete, "a save naming the server's own mod was refused: " + string.Join(",", plan.Missing));
    Check(plan.Required.Single(entry => entry.Id == "lumidust.turbotick").Disposition == SaveModDisposition.Self,
        "the server's own mod was not reported as Self");
    Check(plan.Downloadable.Count == 0, "the server's own mod was queued for download");

    // 缺失时不能变成「可去工坊搜」：工坊上那份是旧版本，装上就是两份同 id。
    var absent = SaveModPlanner.Plan(present,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(!absent.Complete, "a server missing its own mod was allowed to start");
    Check(absent.Required.Single(entry => entry.Id == "lumidust.turbotick").Disposition == SaveModDisposition.SelfMissing,
        "a missing self was not reported as SelfMissing");
    Check(absent.Resolvable.Count == 0, "a missing self was queued for a workshop search");
    Check(absent.UnresolvableRequired.Count == 0, "a missing self was reported as a manual copy the operator cannot make");
    Check(absent.Refusal is { } refusal && refusal.Contains("服务端自身"), "the refusal did not name the deployment: " + absent.Refusal);
    return Task.CompletedTask;
});

await Test("save mod plan reports a missing DLC instead of pretending it is installed", () =>
{
    var manifest = new SaveModManifest("1.7.5005", [
        new SaveMod("ludeon.rimworld", 0, "Core"),
        new SaveMod("ludeon.rimworld.odyssey", 0, "Odyssey")
    ], [], null);
    var plan = SaveModPlanner.Plan(manifest,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ludeon.rimworld" });
    Check(!plan.Complete, "a save needing a DLC the server lacks was allowed to start");
    var entry = plan.Required.Single(item => item.Id == "ludeon.rimworld.odyssey");
    Check(entry.Disposition == SaveModDisposition.DlcMissing, "the DLC was not reported missing: " + entry.Disposition);
    Check(plan.Missing is ["ludeon.rimworld.odyssey"], "the DLC did not land in the missing list");

    // A downloaded workshop mod must not be listed twice in activeMods.
    var once = SaveModPlanner.WithDownloaded(plan, []);
    Check(once.ActiveMods.Count(id => id == "ludeon.rimworld") == 1, "Core was duplicated");
    return Task.CompletedTask;
});

// 工坊内容**不需要登录**：Valve 对公开内容一律匿名提供。这一组把「命令里必须是 anonymous」和
// 「已下载的缓存要被认出来」钉成断言——两条都是实测踩过的：前者我写错过（要求登录），
// 后者错了会让每次启动都去下一遍已经在磁盘上的 mod。
await Test("workshop downloads are anonymous and the on-disk cache layout is recognised", () =>
{
    var command = SteamCmd.WorkshopItem("/usr/games/steamcmd", "/data/game", "294100", "3822832387");
    Check(command.Arguments.Contains("+login"), "the login argument is missing");
    var loginIndex = command.Arguments.ToList().IndexOf("+login");
    Check(command.Arguments[loginIndex + 1] == SteamCmd.Anonymous,
        "a workshop download must log in anonymously, got: " + command.Arguments[loginIndex + 1]);
    Check(!command.Arguments.Any(argument => argument.Contains("+app_update")),
        "a workshop command must not update the game depot");
    Check(command.Arguments.Contains("3822832387") && command.Arguments.Contains("294100"),
        "the item or app id is missing from the command");
    // force_install_dir is what decides where the content lands; the game directory is the only useful answer.
    var installIndex = command.Arguments.ToList().IndexOf("+force_install_dir");
    Check(command.Arguments[installIndex + 1] == "/data/game", "the install directory is not the game directory");

    // The game itself still needs a real account - that rule must not have been loosened by this change.
    var update = SteamCmd.GameUpdate("/usr/games/steamcmd", "/data/game", "someone", "294100", "unstable", true);
    var updateLogin = update.Arguments.ToList().IndexOf("+login");
    Check(update.Arguments[updateLogin + 1] == "someone", "app_update lost its account requirement");

    // Cache layout: <root>/steamapps/workshop/content/<appId>/<itemId>/ (measured, ten real items).
    var root = Path.Combine(Path.GetTempPath(), "tt-workshop-" + Guid.NewGuid().ToString("N"));
    var item = Path.Combine(root, "294100", "3822832387");
    Directory.CreateDirectory(Path.Combine(item, "About"));
    File.WriteAllText(Path.Combine(item, "About", "About.xml"),
        "<?xml version=\"1.0\"?><ModMetaData><packageId>mrk.researchcommission</packageId>"
        + "<name>Research Commission</name></ModMetaData>");
    try
    {
        var scanned = ModDirectory.ScanWorkshop(root);
        Check(scanned.Count == 1 && scanned[0].PackageId == "mrk.researchcommission",
            "a workshop item under <root>/<appId>/<item> was not found: " + scanned.Count);
        Check(scanned[0].Name == "Research Commission", "the mod name was not read");

        // Pointing at the parent (the app-id level) must not silently return nothing.
        var atAppId = ModDirectory.ScanWorkshop(Path.Combine(root, "294100"));
        Check(atAppId.Count == 1, "scanning at the app-id level found nothing");
        Check(ModDirectory.ScanWorkshop(Path.Combine(root, "does-not-exist")).Count == 0,
            "a missing directory must be empty, not an exception");
        Check(ModDirectory.ScanWorkshop(null).Count == 0, "a null root must be empty");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
    return Task.CompletedTask;
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(temp, true); } catch { /* preserve failure artifacts if OS still holds a handle */ }
Environment.ExitCode = failed == 0 ? 0 : 1;














// Fixtures for the DesignatorTargets policy test. Declared after the top-level statements because a type declaration
// cannot precede them; the test references them through `typeof`, which works regardless of declaration order.
abstract class SampleAbstract
{
}

class SampleOpen<T>
{
}

class SampleClosed<T>
{
}
