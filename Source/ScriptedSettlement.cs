using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Scripted settlement creation, so the ownership layer can be exercised without a human clicking anything.
///
/// <para><b>Why this has to exist before the gates mean anything.</b> The layer short-circuits on
/// <c>SettlementData.Active</c>, which is <c>Sites.Count &gt; 0</c>. Until something creates a settlement, every gate
/// returns "allowed" and has never once refused anything - the feature compiles, installs, and does nothing. That is not
/// a flaw in the design (an empty save must behave as it always did), but it does mean the gates cannot be shown to work
/// without a way to declare a settlement.</para>
///
/// <para><b>Why not a proper UI yet.</b> A player-facing editor is stage D of the plan and needs decisions about where
/// it lives (a gizmo? a main tab? the admin page?). This is the same shape <see cref="ScriptedZoneDrag"/> uses, and for
/// the same reason: the transport and the gates can be verified end to end long before the editor exists.</para>
///
/// <para>Two entry points, one implementation: the environment variable makes it fire once during a scripted run, and
/// the portal command makes it fire on demand against a running server.</para>
internal static class ScriptedSettlement
{
    /// <summary>Tick to create the settlement at, or -1. Off unless the variable is set, so nothing happens in a normal
    /// game.</summary>
    private static readonly int AtTick = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_SCRIPT_SETTLEMENT_AT"), out var tick) ? tick : -1;

    private static readonly int Width = ReadInt("TURBOTICK_SCRIPT_SETTLEMENT_W", 24);
    private static readonly int Height = ReadInt("TURBOTICK_SCRIPT_SETTLEMENT_H", 24);
    /// <summary>The largest rectangle a scripted fixture may ask for.</summary>
    /// <remarks>
    /// Four times the default edge: big enough for a real base (a 40x40 site is larger than any measured colony), small
    /// enough that a coordinate pair typed into the size fields is caught instead of producing a map-wide site. Measured
    /// with `110|132`: the site covered (62,73)-(171,204), the work-gate scan hit its object cap and reported "no control
    /// object", and the grant list read as site-wide - a fixture bug that looked like a gate defect.
    /// </remarks>
    private const int MaxWidth = 96;
    private const int MaxHeight = 96;

    /// <summary>The player id the scripted settlement belongs to. 0 leaves it unowned, which is the state that exercises
    /// the visitor path (`GrantsFor` with no match falls back to `Default`).</summary>
    private static readonly int Owner = ReadInt("TURBOTICK_SCRIPT_SETTLEMENT_OWNER", 0);

    private static bool fired;

    internal static bool Pending => AtTick >= 0 && !fired;

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

    /// <summary>Called once per frame from the game component, outside any tick.
    ///
    /// <para>Not inside a tick on purpose: creating a settlement touches no game state that a tick owns - it is a record
    /// of where a player's land is - and doing it from the frame loop means it also works on a replica, which is where
    /// the refusal side needs to be observed.</para></summary>
    internal static void Tick()
    {
        if (!Pending) return;
        if (Find.CurrentMap is null) return;
        if (Find.TickManager is null || Find.TickManager.TicksGame < AtTick) return;
        fired = true;
        var created = Place(null, Owner);
        Log.Message("[TurboTick] 脚本据点：" + (created.Ok ? created.Detail : "失败 " + created.Code + " " + created.Detail));
    }

    /// <summary>Creates a settlement around the map centre.
    ///
    /// <para><paramref name="argument"/> is <c>name|owner|width|height</c>, all optional, as the portal command passes it.
    /// Parsing is forgiving because this is a diagnostic: a malformed field falls back to the default rather than making
    /// the operator retype the whole line.</para></summary>
    internal static (bool Ok, string Code, string Detail) Place(string? argument, int defaultOwner)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return (false, "no-map", "没有当前地图");
            var data = SettlementGates.Data;
            if (data is null) return (false, "no-game", "没有运行中的游戏（据点数据挂在 GameComponent 上）");

            var name = "Scripted";
            var owner = defaultOwner;
            var width = Width;
            var height = Height;
            var anchor = IntVec3.Invalid;
            // Which map the rectangle lands on. Before this, `Place` always used `Find.CurrentMap`, so the only way to
            // put a second settlement on a second map was to switch the current map between two calls - and the whole
            // point of the multi-settlement mode is two territories that exist at the same time on different maps. The
            // site record already carries `MapId` and the overlay/gates already resolve it; only the scripted entry
            // point was pinned to one map.
            var targetMap = Find.CurrentMap;
            // `name|owner|width|height` and, optionally, `|x,z`.
            //
            // <para><b>Why the centre is now an argument.</b> Without it every settlement lands on the centroid of the
            // colonists, so two of them are two rectangles around the same point - and the whole feature under test is two
            // *separate* territories with a gate that has to tell them apart. Measured before this argument existed: two
            // `script-settlement` calls produced byte-identical rects `(105,127)–(128,150)`, so every object on the map was
            // either inside both or inside neither, and the only possible reading was "no settlement at all".</para>
            if (!string.IsNullOrWhiteSpace(argument))
            {
                var parts = argument.Split('|');
                if (parts.Length > 0 && parts[0].Length > 0) name = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[1], out var parsedOwner)) owner = parsedOwner;
                if (parts.Length > 2 && int.TryParse(parts[2], out var parsedWidth) && parsedWidth > 0) width = parsedWidth;
                if (parts.Length > 3 && int.TryParse(parts[3], out var parsedHeight) && parsedHeight > 0) height = parsedHeight;
                // **Fields 3 and 4 are a size, not a position.** Every fixture in this project writes `14|14`, and the one
                // time a caller wrote `110|132` meaning a cell the parse accepted it as a 110x132 rectangle: the site
                // covered most of the map, `settlement-workgate scan` hit its 900-object cap and reported "no control
                // object", every grant read as site-wide, and the reading looked like a gate defect instead of a typo.
                // A rectangle that wide cannot be a settlement, so refusing it names the mistake at the call site.
                if (width > MaxWidth || height > MaxHeight)
                    return (false, "size",
                        $"据点尺寸 {width}x{height} 太大 —— 第三、四个字段是**尺寸**（宽|高），坐标要写在第五个字段 x,z；"
                        + $"例如 `名字|主人|{Width}|{Height}|117,139`");

                // Field 4 is the centre **only when it looks like one**. It used to be read unconditionally, which
                // made `name|owner|w|h|map=1` put the map field at index 4 and then silently ignore it - measured: two
                // `script-settlement` calls with `map=1` on a second map both reported 地图 0.
                if (parts.Length > 4)
                {
                    var pair = parts[4].Split(',');
                    if (pair.Length >= 2 && int.TryParse(pair[0].Trim(), out var parsedX)
                        && int.TryParse(pair[1].Trim(), out var parsedZ))
                        anchor = new IntVec3(parsedX, 0, parsedZ);
                }
                // Optional `|map=<uniqueID>` in any trailing field. A named field rather than a sixth positional one
                // because the two existing shapes (`name|owner|w|h` and `name|owner|w|h|x,z`) both have to keep working:
                // a positional map id would have to skip over the centre field.
                // Scanned from 4, not 5: with no centre field the map is the fifth field, and with a centre it is the
                // sixth. Scanning from 4 covers both because a centre never starts with `map=`.
                for (var i = 4; i < parts.Length; i++)
                {
                    var field = parts[i].Trim();
                    if (!field.StartsWith("map=", StringComparison.Ordinal)) continue;
                    if (!int.TryParse(field[4..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var wanted))
                        return (false, "bad-map", "map= 后面不是数字：" + field);
                    var resolved = MapLifecycleSync.FindMapByUniqueId(wanted);
                    if (resolved is null)
                        return (false, "no-map", $"地图 uniqueID={wanted} 不在本端列表"
                            + $"（本端 {Find.Maps.Count} 张：{string.Join("/", Find.Maps.Select(m => m.uniqueID))}）");
                    targetMap = resolved;
                }
            }

            if (targetMap is null) return (false, "no-map", "目标地图为空");
            var centre = anchor.IsValid ? anchor : Anchor(targetMap, width, height);
            var rect = CellRect.CenteredOn(centre, width, height).ClipInsideMap(targetMap);
            // Ids are allocated from the existing list rather than from a counter: a settlement that was retired keeps
            // its id forever (so an old grant record still means the same place), and reusing one would silently
            // re-point those records at the new settlement.
            var next = 1;
            foreach (var existing in data.Sites)
                if (existing is not null && existing.Id >= next) next = existing.Id + 1;

            var site = new SettlementSite
            {
                Id = next,
                MapId = targetMap.uniqueID,
                OwnerPlayer = owner,
                Name = name,
                MinX = rect.minX, MinZ = rect.minZ, MaxX = rect.maxX, MaxZ = rect.maxZ,
                FoundedTick = Find.TickManager?.TicksGame ?? 0,
            };
            data.Sites.Add(site);
            // `Members` is one entry per player, so this overwrite is intentional: a new settlement becomes the one the
            // player "is in". It is *not* how ownership is decided - `SettlementSite.OwnerPlayer` records every site a
            // player owns, and `SettlementData.Owns` reads both. Keeping the write means a single-settlement UI row and an
            // older save behave exactly as before.
            if (owner != 0) data.Members[owner] = site.Id;
            // Founding a settlement assigns the pawns standing inside it to it. Without this every colonist would be a
            // visitor in its own base and be refused permission to build: the gates ask "does this pawn belong to the
            // land it is on", and a new settlement has no members until somebody says which pawns are its.
            // `targetMap`, not `map`: `rect` is on the target map, so iterating the *current* map's pawns and testing
            // them against that rectangle claims pawns that are nowhere near the settlement. Measured in round 73:
            // `乙|2|10|10|map=1` reported 「认领 3 人」 while the 3 colonists were all on map 0 - their positions fell
            // inside the map-1 rectangle's coordinates, so they were recorded as belonging to a settlement on a map they
            // have never been on. Those three pawns then had a home in a settlement whose gates could never apply to
            // them, which is worse than claiming nobody: the report says the settlement has members.
            var claimed = 0;
            foreach (var pawn in targetMap.mapPawns.AllPawnsSpawned)
            {
                if (pawn is null || pawn.RaceProps?.Humanlike != true) continue;
                if (!rect.Contains(pawn.PositionHeld)) continue;
                if (!pawn.IsColonist && !pawn.IsSlave && !pawn.IsPrisoner) continue;
                data.PawnHome[pawn.thingIDNumber] = site.Id;
                claimed++;
            }
            // The table changed, so replicas have to be told. Without this bump a settlement declared after the snapshot
            // exists on the authority alone: the replica's `SettlementGates.Data` stays empty, every gate there
            // short-circuits to "allowed" while the authority refuses, and the overlay has nothing to draw.
            SettlementSync.Touch();
            Log.Message($"[TurboTick] 据点 #{site.Id}「{name}」建立：地图 {site.MapId} "
                + $"({site.MinX},{site.MinZ})–({site.MaxX},{site.MaxZ})，主人 {owner}，认领 {claimed} 人，"
                + SettlementRule.Describe(data));
            return (true, "ok", $"据点 #{site.Id}「{name}」({site.MinX},{site.MinZ})–({site.MaxX},{site.MaxZ})"
                + $" 中心 {centre.x},{centre.z} 主人 {owner} 认领 {claimed} 人");
        }
        catch (Exception error)
        {
            return (false, "failed", error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Encodes the kept-cell table onto the settlement wire and decodes it back, row by row.
    ///
    /// <para>Compared on all four fields per row rather than on the count: a wire that dropped `owner` would still carry
    /// the right number of ints, and `OwnerAt` would then answer 0 for a cell somebody kept - the silent direction.</para>
    /// </summary>
    private static string RoundTripKeptCells(SettlementData data)
    {
        var wire = SettlementSync.Publish();
        if (wire is null) return "没有据点表（所有权层关闭）";
        var expected = data.Overrides.Count;
        var quads = wire.KeptCells.Count / 4;
        if (quads != expected) return $"**行数不符**：表 {expected} 行，线上 {quads} 行";
        var index = 0;
        foreach (var entry in data.Overrides.ForScribing())
        {
            if (wire.KeptCells[index] != entry.MapId || wire.KeptCells[index + 1] != entry.X
                || wire.KeptCells[index + 2] != entry.Z || wire.KeptCells[index + 3] != entry.Owner)
                return $"**第 {index / 4 + 1} 行不一致**：表 ({entry.MapId},{entry.X},{entry.Z},{entry.Owner})"
                    + $" 线上 ({wire.KeptCells[index]},{wire.KeptCells[index + 1]},{wire.KeptCells[index + 2]},{wire.KeptCells[index + 3]})";
            index += 4;
        }
        return $"{quads} 行一致";
    }

    /// <summary>Encodes the pact table onto the settlement wire and decodes it back, reporting whether every row
    /// survived.
    ///
    /// <para>Returns a sentence rather than a bool because the useful failure is "3 rows in, 2 out" - a wire that
    /// dropped `Allied` would still have the right number of quints, so the comparison is on all five fields per row,
    /// not on the count. Round 94 widened the row from four to five: `NoticeUntil` decides whether a declaration is
    /// refused, so a wire that dropped it would let a replica declare a war the authority rejects.</para></summary>
    private static string RoundTripPacts(SettlementData data)
    {
        var wire = SettlementSync.Publish();
        if (wire is null) return "没有据点表（所有权层关闭）";
        var expected = data.Pacts.Count(each => each is not null);
        var rows = wire.Pacts.Count / 5;
        if (rows != expected) return $"**行数不符**：表 {expected} 行，线上 {rows} 行";
        // Field-by-field: `a`, `b`, `warSince`, `allied`, `noticeUntil`. A pair comparison would pass on a table where two
        // settlements happened to have the same `WarSince`, which on a same-tick declaration they do.
        var index = 0;
        foreach (var pact in data.Pacts)
        {
            if (pact is null) continue;
            if (wire.Pacts[index] != pact.A || wire.Pacts[index + 1] != pact.B
                || wire.Pacts[index + 2] != pact.WarSince || (wire.Pacts[index + 3] != 0) != pact.Allied
                || wire.Pacts[index + 4] != pact.NoticeUntil)
                return $"**第 {index / 5 + 1} 行不一致**：表 (#{pact.A},#{pact.B},{pact.WarSince},{(pact.Allied ? 1 : 0)},{pact.NoticeUntil})"
                    + $" 线上 (#{wire.Pacts[index]},#{wire.Pacts[index + 1]},{wire.Pacts[index + 2]},"
                    + $"{wire.Pacts[index + 3]},{wire.Pacts[index + 4]})";
            index += 5;
        }
        return $"{rows} 行一致";
    }

    /// <summary>Clears a settlement's membership records and re-adds nothing - the state the report's
    /// `未登记小人` column counts.
    ///
    /// <para><b>Why a command is needed to test a counter.</b> `PawnHome` is written at founding and by
    /// `settlement-claim`, and nothing else removes an entry except the departed-pawn prune. So the "a colonist joined
    /// later" state cannot be reached from the console at all - every colonist standing inside the rectangle at founding
    /// time is recorded, and one that arrives afterwards cannot be spawned by any existing command. Without a way to
    /// create the state, the counter would ship unverified: a number that is always 0 looks exactly like a working check.
    /// </para>
    ///
    /// <para>Argument: <c>&lt;settlement&gt;</c>. Drops every record pointing at that settlement, then reports the
    /// counter. `settlement-claim` puts them back.</para></summary>
    internal static string Forget(string? argument)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null) return "没有运行中的游戏";
            if (!data.Active) return "据点 0 个（所有权层关闭）";
            if (!int.TryParse((argument ?? "").Trim().Split(' ')[0], out var siteId))
                return "用法：settlement-forget <据点>";
            var site = data.Site(siteId);
            if (site is null || site.Retired) return $"没有这个据点：{siteId}";
            var dropped = 0;
            foreach (var pawn in data.PawnHome.Where(pair => pair.Value == siteId).Select(pair => pair.Key).ToList())
                if (data.PawnHome.Remove(pawn)) dropped++;
            if (dropped > 0) SettlementSync.Touch();
            var unrecorded = SettlementRule.UnrecordedColonists(data);
            return $"据点 #{siteId}「{site.Name}」清掉 {dropped} 条归属记录；"
                + $"未登记小人现在 {unrecorded} 个（据点-claim {siteId} 可恢复）";
        }
        catch (Exception error)
        {
            return "清除失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Gives a colonist a royal title, so the royalty-derived UI has a carrier to be measured on.
    ///
    /// <para><b>Why this exists.</b> `royalty-count` reports 「有 royalty tracker 的 4 个；有头衔 0 个」 on this save, and
    /// `PermitsCardUtility` reads <c>pawn.royalty.GetCurrentTitle(selectedFaction)</c> - so the card, the permit list and
    /// the royal ability list are all unmeasurable for the same reason: nothing in the world has a title. A channel with
    /// zero carriers cannot be told from a broken one, which is the trap `ResidualCounterSync` already fell into with
    /// seven of its nine keys, and no other command can create a titled pawn.
    /// </para>
    ///
    /// <para>Argument: <c>[pawnId] [titleDefName]</c>. With no pawn id it takes the first colonist; with no title it uses
    /// <c>RoyalTitleDefOf.Knight</c>, which the game's own debug action also grants
    /// (<c>Assembly-CSharp.decompiled.cs:166739</c>). <c>grantRewards: false</c> matches that call - the rewards path
    /// spawns items and can open dialogs, and this command is for measuring readers, not for playing the game.
    /// </para></summary>
    internal static string GrantTitle(string? argument)
    {
        try
        {
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            var parts = (argument ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Pawn? pawn = null;
            if (parts.Length > 0 && int.TryParse(parts[0], out var only))
                pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p is not null && p.thingIDNumber == only);
            pawn ??= map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (pawn is null) return "地图上没有可授予头衔的小人";
            if (pawn.royalty is null) return $"小人 #{pawn.thingIDNumber} 没有 royalty tracker（Humanlike 才有）";
            // Default to the highest title that actually grants a permit, not to a fixed name. `Knight` was the first
            // default and it grants **no** permits and **no** abilities in 1.7 (measured: `许可 0 个 / 能力 0 个` after
            // granting it), so the one reader this command exists for - `PermitsCardUtility`, which reads
            // `pawn.royalty.GetCurrentTitle(faction)` and then lists permits - still had nothing to read.
            //
            // `RoyalTitleDef.permits` and `.grantedAbilities` are the two lists (`Assembly-CSharp.decompiled.cs`), so the
            // default is chosen by looking at them rather than by trusting a name. A caller that names a title still gets
            // exactly that title.
            RoyalTitleDef? title;
            if (parts.Length > 1)
            {
                title = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(parts[1]);
                if (title is null) return $"没有这个头衔定义：{parts[1]}";
            }
            else
            {
                title = DefDatabase<RoyalTitleDef>.AllDefsListForReading
                    .Where(def => def is not null && def.permits is { Count: > 0 })
                    .OrderBy(def => def.seniority)
                    .FirstOrDefault();
                if (title is null)
                    title = DefDatabase<RoyalTitleDef>.AllDefsListForReading
                        .Where(def => def is not null)
                        .OrderBy(def => def.seniority).FirstOrDefault();
                if (title is null) return "没有任何头衔定义（Royalty 未加载）";
            }
            var faction = Faction.OfEmpire ?? Find.FactionManager?.AllFactions?.FirstOrDefault(f => f?.def?.isPlayer == false);
            if (faction is null) return "世界里没有可用的授予方派系（帝国未生成）";
            var before = pawn.royalty.GetCurrentTitle(faction);
            pawn.royalty.SetTitle(faction, title, grantRewards: false);
            var after = pawn.royalty.GetCurrentTitle(faction);
            // **`AddPermit` is a dead end here, and the two readings disagree by design (round 98).**
            //
            // `Pawn_RoyaltyTracker.SetTitle` writes `titles[index].def` and stops; permits live in a separate list,
            // `factionPermits`, exposed as `AllFactionPermits`. So the obvious next step is `AddPermit` for each permit in
            // the title def - and it does nothing, because its first line is
            // `if (!ModLister.CheckRoyalty("Permit") || HasPermit(permit, faction)) return;` and `HasPermit` falls back
            // to `currentTitle.permits.Contains(permit)` **after** searching `factionPermits`. A permit the title already
            // carries is therefore "held" by that method and never reaches the list.
            //
            // Measured: seniority-100 title offering 2 permits, `AddPermit` called twice, `AllFactionPermits` still 0.
            //
            // This is shipped behaviour, not a defect, and the consequence is the reading that matters:
            // `PermitsCardUtility` lists `AllFactionPermits` (bought permits only) while `HasPermit` answers from the
            // title as well. The card being empty and the permit working are both correct at once. So the reply reports
            // the two numbers separately rather than merging them - a single `许可 N 个` conflated "the pawn bought N" with
            // "the title grants N", and this command's whole purpose is to make a zero impossible to misread.
            var bought = pawn.royalty.AllFactionPermits?.Count() ?? 0;
            var offered = title.permits?.Count ?? 0;
            return $"小人 #{pawn.thingIDNumber}「{pawn.LabelShortCap}」的头衔 {before?.defName ?? "无"} → {after?.defName ?? "无"}"
                + $"（授予方 {faction.Name}，grantRewards=false，seniority {title.seniority}）"
                + $"；头衔自带 {offered} 个 / 已购买 {bought} 个 / 能力 {title.grantedAbilities?.Count ?? 0} 个"
                + "（卡片只列已购买，判定两者都算 —— 原版即为如此）";
        }
        catch (Exception error)
        {
            return "授予头衔失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Records colonists as belonging to a settlement - the explicit half of `UnrecordedColonists`.
    ///
    /// <para><b>Why this is a command and not a fallback.</b> See <see cref="SettlementRule.UnrecordedColonists"/>: both
    /// automatic answers hand `All` to a stranger, because on a shared colony every player's pawns are one faction and
    /// the ground says nothing about whose they are. Claiming is a decision, so it is an operation somebody invokes and
    /// the result is a row in the table that can be inspected.</para>
    ///
    /// <para>Argument: <c>&lt;settlement&gt;</c> or <c>&lt;settlement&gt; &lt;pawnId&gt;</c>. Without a pawn id it claims
    /// every unrecorded colonist standing on that settlement's land, which is what a founder means by "these are mine".
    /// </para></summary>
    internal static string Claim(string? argument)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null) return "没有运行中的游戏";
            if (!data.Active) return "据点 0 个（所有权层关闭）";
            // **No `Find.CurrentMap` here.** Claiming is about a settlement, and a settlement knows its own map
            // (`SettlementSite.MapId`). Reading the current map made claiming impossible from anywhere else: measured with
            // a site on map 1 and the player looking at map 0, `settlement-claim 1 873` answered 「地图上没有小人 #873」
            // about a pawn that was standing exactly on that site's land. The map is resolved per site below, and the
            // pawn lookup searches every map.
            if (string.IsNullOrWhiteSpace(argument)) return "用法：settlement-claim <据点> [小人id]";
            var parts = argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!int.TryParse(parts[0], out var siteId)) return "据点必须是整数：" + parts[0];
            var site = data.Site(siteId);
            if (site is null || site.Retired) return $"没有这个据点：{siteId}";
            var claimed = 0;
            var skipped = 0;
            if (parts.Length >= 2 && int.TryParse(parts[1], out var only))
            {
                Pawn? pawn = null;
                foreach (var each in Find.Maps)
                {
                    if (each?.mapPawns is null) continue;
                    pawn = each.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p is not null && p.thingIDNumber == only);
                    if (pawn is not null) break;
                }
                if (pawn is null) return $"已生成列表里没有小人 #{only}（找遍 {Find.Maps.Count} 张地图）";
                // The pawn's own map, not the settlement's: the refusal below names both, and the containment test needs
                // the map the pawn is actually on to say which is wrong.
                var pawnMap = pawn.MapHeld;
                if (pawnMap is null) return $"小人 #{only} 不在任何地图上（在商队或运输舱里）";
                // Refused rather than claimed when it is not on this settlement's land: recording a pawn that is nowhere
                // near it produces exactly the state round 78 had to prune - a membership row pointing at a pawn whose
                // position says the record is meaningless.
                // Map-aware: two settlements on different maps share a coordinate space, so a cell-only test accepted a
                // pawn standing nowhere near this settlement. See `SettlementSite.Contains(int, IntVec3)`.
                if (!site.Contains(pawnMap.uniqueID, pawn.PositionHeld))
                    return $"小人 #{only} 不在据点 #{siteId}（地图 {site.MapId}）的矩形内，小人在地图 {pawnMap.uniqueID}";
                data.PawnHome[pawn.thingIDNumber] = siteId;
                claimed = 1;
            }
            else
            {
                // Bulk claim scans the settlement's own map. Any other map cannot contain this settlement's land, and
                // `Contains` would answer false for every pawn on it anyway - so reading `Find.CurrentMap` changed the
                // result depending on where the player was standing.
                var siteMap = MapLifecycleSync.FindMapByUniqueId(site.MapId);
                if (siteMap?.mapPawns is null) return $"据点 #{siteId} 的地图 {site.MapId} 不在本端";
                foreach (var pawn in siteMap.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is null || pawn.RaceProps?.Humanlike != true) continue;
                    if (!pawn.IsColonist && !pawn.IsSlave && !pawn.IsPrisoner) continue;
                    if (!site.Contains(siteMap.uniqueID, pawn.PositionHeld)) { skipped++; continue; }
                    if (data.HomeOfPawn(pawn.thingIDNumber) == siteId) continue;
                    data.PawnHome[pawn.thingIDNumber] = siteId;
                    claimed++;
                }
            }
            if (claimed > 0) SettlementSync.Touch();
            return $"据点 #{siteId}「{site.Name}」认领 {claimed} 个小人"
                + (skipped > 0 ? $"（{skipped} 个不在此据点矩形内，未动）" : "")
                + $"；该据点现有归属 {data.PawnHome.Count(pair => pair.Value == siteId)} 人";
        }
        catch (Exception error)
        {
            return "认领失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Builds a real door on the current map so the `Pass`/`Door` gate can be exercised.
    ///
    /// <para><b>Why a command is needed at all.</b> The first reading of the door gate was
    /// 「（据点里没有门；闸门无从测起）」 - the base has no door of its own, so there was no object to ask about and the
    /// answer said so rather than pretending to have tested something. But that also means the gate could ship broken
    /// and never be caught, because a colony with no doors produces no refusals and no evidence. Placing one puts the
    /// gate in the path of a real object.</para>
    ///
    /// <para>Argument form: <c>door [x,z]</c>. With no argument the door goes at the centre of the first live
    /// settlement, which is the cell the gates are already being asked about.</para></summary>
    internal static string PlaceDoor(string? argument)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null) return "没有运行中的游戏";
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            var cell = map.Center;
            if (!string.IsNullOrWhiteSpace(argument))
            {
                var pair = argument.Trim().Split(',');
                if (pair.Length >= 2 && int.TryParse(pair[0].Trim(), out var x) && int.TryParse(pair[1].Trim(), out var z))
                    cell = new IntVec3(x, 0, z);
                else if (data.Sites.Count > 0 && data.Sites[0] is { } first)
                    cell = new IntVec3((first.MinX + first.MaxX) / 2, 0, (first.MinZ + first.MaxZ) / 2);
            }
            else if (data.Sites.Count > 0 && data.Sites[0] is { } firstSite)
                cell = new IntVec3((firstSite.MinX + firstSite.MaxX) / 2, 0, (firstSite.MinZ + firstSite.MaxZ) / 2);

            if (!cell.InBounds(map)) return $"({cell.x},{cell.z}) 不在图内";
            // A door needs a stuff, and stone blocks are what a colony uses. `GenStuff.DefaultStuffFor` is the engine's
            // own answer to "what is this normally made of", so the door is an ordinary one rather than a special case.
            var stuff = GenStuff.DefaultStuffFor(ThingDefOf.Door);
            var made = ThingMaker.MakeThing(ThingDefOf.Door, stuff);
            GenSpawn.Spawn(made, cell, map);
            var door = made as Building_Door;
            var home = SettlementRule.HomeOf(data, made);
            return $"已建门 #{made.thingIDNumber}（{made.def.defName}，料 {stuff?.defName ?? "无"}）"
                + $" 于 ({cell.x},{cell.z})，所在据点 {home}"
                + (door is null ? "，**但生成的不是 Building_Door**" : "");
        }
        catch (Exception error)
        {
            return "建门失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Builds a care structure (a cryptosleep casket) so `SettlementKind.Pod` has an object to be classified.
    ///
    /// <para><b>Why this is needed.</b> `Pod` had four table rows and no classifier arm, so the rows were unreachable -
    /// but that is a fact about the code, not a reading. Placing a casket turns it into one: the probe prints the kind
    /// `KindOf` gives it and whether `Care`/`Use` is what decides. Before the fix the same object classifies as
    /// `Building`, and `Use`/`Building` is unlisted, so `Care` could not admit a visitor.</para></summary>
    internal static string PlacePod(string? argument)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null) return "没有运行中的游戏";
            var map = Find.CurrentMap;
            if (map is null) return "没有当前地图";
            var cell = map.Center;
            if (data.Sites.Count > 0 && data.Sites[0] is { } first)
                cell = new IntVec3((first.MinX + first.MaxX) / 2 + 2, 0, (first.MinZ + first.MaxZ) / 2);
            var wantedDef = "CryptosleepCasket";
            var wantedCount = 0;
            if (!string.IsNullOrWhiteSpace(argument))
            {
                var parts = argument.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var z))
                    cell = new IntVec3(x, 0, z);
                // `x z [defName] [count]`: a fourth shape, added in round 135 because the work gate could not be read
                // with the one thing this probe could build. A casket attracts no work giver at all (measured: owner and
                // visitor both 0), so "the gate permitted the visitor" and "vanilla had nothing to do" were the same
                // number. Filth and haulable stacks do attract work, which is what makes the reading interpretable.
                if (parts.Length >= 3) wantedDef = parts[2];
                if (parts.Length >= 4 && int.TryParse(parts[3], out var count)) wantedCount = Math.Max(1, count);
            }
            if (!cell.InBounds(map)) return $"({cell.x},{cell.z}) 不在图内";
            // Looked up by defName rather than through `ThingDefOf`: this game version has no
            // `ThingDefOf.CryptosleepCasket` (the field list carries the pods but not the casket), and the def exists
            // regardless - `RimWorld.Building_CryptosleepCasket` is a shipped type, so its def is in the database.
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(wantedDef);
            if (def is null) return $"ThingDef {wantedDef} 未定义（defName 拼错或这个版本没有它）";
            var stuff = GenStuff.DefaultStuffFor(def);
            var made = ThingMaker.MakeThing(def, stuff);
            if (wantedCount > 0 && made.def.stackLimit > 1) made.stackCount = Math.Min(wantedCount, made.def.stackLimit);
            GenSpawn.Spawn(made, cell, map);
            var kind = SettlementRule.KindOf(made, SettlementAct.Use);
            return $"已在 ({cell.x},{cell.z}) 建 {made.def.defName}#{made.thingIDNumber}（{made.GetType().Name}"
                + $"{(made.def.stackLimit > 1 ? " ×" + made.stackCount.ToString() : string.Empty)}），所在据点 "
                + $"{SettlementRule.HomeOf(data, made)}，分类 {kind}";
        }
        catch (Exception error)
        {
            return "建休眠舱失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Whether the rule table has a `Use` row for whatever this work giver works on.
    ///
    /// <para>This is the exact question that was wrong before round 85: the gate asked `Use` for every work giver, and on
    /// a kind with no `Use` row that is a refusal no grant can overcome. `否` beside a work giver is therefore the mark of
    /// a gate that would have been permanently closed for it.</para></summary>
    private static bool TableHasUseRowFor(WorkGiver_Scanner giver) => giver switch
    {
        WorkGiver_CleanFilth => SettlementRuleTable.Permits(SettlementGrant.All, SettlementAct.Use, SettlementKind.Filth),
        WorkGiver_HunterHunt => SettlementRuleTable.Permits(SettlementGrant.All, SettlementAct.Use, SettlementKind.WildAnimal),
        WorkGiver_GrowerHarvest or WorkGiver_PlantsCut
            => SettlementRuleTable.Permits(SettlementGrant.All, SettlementAct.Use, SettlementKind.Plant),
        WorkGiver_Miner => SettlementRuleTable.Permits(SettlementGrant.All, SettlementAct.Use, SettlementKind.Rock),
        WorkGiver_Tend => SettlementRuleTable.Permits(SettlementGrant.All, SettlementAct.Use, SettlementKind.Pawn),
        // Surgery: the row that answers it is `Operate`/`Pawn`, and `Use`/`Pawn` deliberately does not exist - so
        // "Use 可判=否" here is the correct reading, not a hole. It is printed because that is exactly the question round
        // 89 fixed, and a probe that hid it would make the fix unobservable.
        WorkGiver_DoBill => SettlementRuleTable.Permits(SettlementGrant.All, SettlementAct.Use, SettlementKind.Pawn),
        _ => true,
    };

    /// <summary>Membership entries whose pawn exists nowhere: not spawned, not in `WorldPawns`, or dead.
    ///
    /// <para>Counts rather than deletes, because the report should show what the table looks like - the deletion
    /// belongs to `SettlementSync.Publish`, which is the one place that runs before every frame and therefore the one
    /// place where a repair reaches both peers.</para></summary>
    private static int CountGhostMembers(SettlementData data)
    {
        if (data.PawnHome.Count == 0) return 0;
        var alive = new HashSet<int>();
        try
        {
            foreach (var map in Find.Maps)
            {
                if (map?.mapPawns is null) continue;
                foreach (var pawn in map.mapPawns.AllPawns)
                    if (pawn is not null && !pawn.Destroyed && !pawn.Dead) alive.Add(pawn.thingIDNumber);
            }
            if (Find.WorldPawns is null) return 0;
            foreach (var pawn in Find.WorldPawns.AllPawnsAliveOrDead)
                if (pawn is not null && !pawn.Destroyed && !pawn.Dead) alive.Add(pawn.thingIDNumber);
        }
        catch (Exception) { return 0; }
        if (alive.Count == 0) return 0;
        var ghost = 0;
        foreach (var id in data.PawnHome.Keys) if (!alive.Contains(id)) ghost++;
        return ghost;
    }

    /// <summary>Where to put the settlement: the colonists' own centre of mass, not the map centre.
    ///
    /// <para>Measured on the server: a settlement at <c>map.Center</c> claimed <b>0</b> colonists, because a colony sits
    /// wherever it landed and the map is drawn around the whole biome. A settlement that contains nobody is not a
    /// settlement - the gates ask "does this pawn belong to the land it is on", and the answer was "there is no such
    /// pawn anywhere". Anchoring on the colonists makes the scripted settlement cover the base, which is what the
    /// operator meant.</para></summary>
    private static IntVec3 Anchor(Map map, int width, int height)
    {
        long x = 0, z = 0;
        var count = 0;
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is null || pawn.RaceProps?.Humanlike != true) continue;
            if (!pawn.IsColonist && !pawn.IsSlave && !pawn.IsPrisoner) continue;
            var position = pawn.PositionHeld;
            if (!position.InBounds(map)) continue;
            x += position.x;
            z += position.z;
            count++;
        }
        if (count == 0) return map.Center;
        return new IntVec3((int)(x / count), 0, (int)(z / count));
    }

    /// <summary>Runs the gate against real objects on the map and reports what it answered.
    ///
    /// <para><b>Why this exists.</b> A gate that is installed and never refuses is indistinguishable from a gate that
    /// does not work. Waiting for the AI to wander into a forbidden action is not a test: on a colony of a few pawns
    /// doing their routines inside one settlement, nothing is ever refused, and the refusal path stays unexercised for
    /// the life of the build. This probe asks the gate directly and prints the answer next to the grants that produced
    /// it.</para>
    ///
    /// <para><b>Why it probes a real pawn and not null.</b> <c>Can(Pawn? pawn, …)</c> returns "allowed" immediately for
    /// a null pawn - the caller is saying "no pawn is acting" - so probing with null measures nothing and reports every
    /// action as permitted. The first version of this method did exactly that and produced a clean, meaningless result.
    /// The pawn path is therefore probed with a colonist that belongs to <i>another</i> settlement, which is the
    /// visitor case the layer exists for; the player path is probed separately with a player id that is a member of
    /// nothing.</para></summary>
    /// <summary>Asks the land gate one question for one named player, on one named map.
    ///
    /// <para><b>Why a separate probe (round 132).</b> Twenty rounds measured the ownership layer's <i>allow</i> side -
    /// an owner's pawn is permitted, a visitor is refused - and never "player 2 on player 1's land", because the gate
    /// that takes a player id also reads <c>Find.CurrentMap</c>, so there was no way to say which map the question was
    /// about. The deny half therefore had no reading at all, and a refuse path with no reading is indistinguishable
    /// from a permit path: both end with a pawn doing something.
    ///
    /// <para>Argument form: <c>#settlement x z [act] [player]</c>. <c>#settlement</c> is the map's <c>uniqueID</c> and
    /// may be any map, not only the current one. Defaults: act <c>Build</c>, player = the local player + 1, i.e. the
    /// cheapest way to spell "somebody who does not own this". Both sides are printed in one line each, because a
    /// reading that only shows the refusal cannot distinguish "the gate refuses everyone" from "the gate refuses this
    /// one" - the same reason the visitor sample prints the owner too.</para></summary>
    /// <summary>The grant flags a player holds for one settlement, spelled out for a reading.
    ///
    /// <para>Named flags rather than a count because the interesting question is *which* grant is missing: "player 2 may
    /// not build here" and "player 2 may build but not take" look identical in a boolean answer, and the second one is
    /// a configuration a player would actually set.</para></summary>
    private static string DescribeGrants(SettlementGrant grants)
    {
        if (grants == SettlementGrant.None) return "无";
        var names = new List<string>();
        foreach (SettlementGrant flag in Enum.GetValues(typeof(SettlementGrant)))
            if (flag != SettlementGrant.None && (grants & flag) == flag) names.Add(flag.ToString());
        return names.Count == 0 ? "无" : string.Join("+", names);
    }

    /// <summary>Asks the **installed patch**, not the decision function, and compares the two.
    ///
    /// <para><b>Why this is a different question from `GateProbe` (round 134).</b> `GateProbe` calls
    /// <c>SettlementGates.Can</c> directly: it answers "is the decision right". The thing the player experiences is
    /// "is the decision <i>reached</i>" - a `<c>HarmonyPatch</c>` that never matched a signature, or matched a different
    /// overload, leaves the decision function perfectly correct and completely unused, and both read the same from
    /// outside. So this calls <c>ReservationManager.CanReserve</c> - the six-parameter method the gate patches
    /// (`SettlementReservationGate`) - and prints it beside the raw answer. Agreement is evidence the patch is live;
    /// disagreement means the patched path does not ask what the decision function answers.</para>
    ///
    /// <para>Argument form: <c>&lt;pawnId&gt; &lt;#map&gt; x z</c>. The pawn is looked up on every map because a
    /// colonist claimed into a settlement may be standing on the other one, and `Find.CurrentMap` is only the map the
    /// player is looking at.</para>
    ///
    /// <para>Counted as nothing: the whole body runs under <see cref="SettlementGates.Uncounted"/>, because a probe that
    /// increments the counters it is meant to validate was round 95's defect and made `settlement-counters` report the
    /// probe's own questions as pawn traffic.</para></summary>
    /// <summary>Which work givers would take a job on this object, for this pawn.
    ///
    /// <para><b>The third chokepoint, and the one with the most ways to be wrong (round 135).</b>
    /// `SettlementWorkGate` asks `SettlementGates.WorkActOf(workGiver, thing)` - the act is derived from the *work
    /// giver instance*, because a work giver's verb is not knowable at a generic chokepoint. The doc on that class
    /// records the defect this replaced: asking `Use` unconditionally made `SettlementRuleTable.Permits` refuse every
    /// unlisted pair, so for Filth, Fire, Plant, WildAnimal and Animal **every** work giver was refused no matter what
    /// grants the pawn held - a visitor with `All` could not clean, firefight, harvest, hunt or rope an animal, and the
    /// refusal was indistinguishable from the owner having denied it.</para>
    ///
    /// <para>So the reading worth taking is not "does the gate answer", but "**which** work givers still see work".
    /// A gate that refuses everything and a gate that refuses exactly the unpermitted thing look the same in a boolean.
    /// This enumerates every `WorkGiver_Scanner` in the database, calls the patched
    /// <c>HasJobOnThing(pawn, thing, forced: false)</c>, and prints the ones that said yes - once for the visitor and
    /// once for a colonist whose home *is* that settlement, so the two lists can be diffed.</para>
    ///
    /// <para>Uncounted, for the round-95 reason: this asks every work giver about one object, which would otherwise
    /// appear in `settlement-counters` as a burst of pawn refusals.</para></summary>
    internal static string WorkGateProbe(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        // One split for every mode: the modes are checked before the ownership guard (a fixture has to be buildable from
        // zero settlements), so each branch reads this array rather than re-splitting and drifting out of order.
        var whoParts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // **Where the Home area is.** `WorkGiver_CleanFilth.HasJobOnThing` (`:311022-311041`) refuses any filth whose cell
        // is not in `areaManager.Home`, and refuses filth younger than `MinTicksSinceThickened = 600` (`:311004`). Both are
        // vanilla preconditions, not the gate - measured, spawned filth answered `owner 0` for exactly these two reasons
        // and the zero was indistinguishable from a refusal. Naming the area and the thickening age is what makes the
        // fixture buildable and the zero attributable.
        if (whoParts.Length >= 2 && whoParts[0].Equals("home", StringComparison.OrdinalIgnoreCase))
        {
            var homeMap = int.TryParse(whoParts[1], out var hid) ? MapLifecycleSync.FindMapByUniqueId(hid) : null;
            if (homeMap?.areaManager?.Home is not { } homeOfMap) return "本端没有该地图或该地图没有 Home 区";
            var cells = homeOfMap.ActiveCells.ToList();
            if (cells.Count == 0) return $"图 {homeMap.uniqueID} 的 Home 区 0 格 —— 清洁/搬运类工作在这里没有落点";
            var minX = cells.Min(c => c.x); var maxX = cells.Max(c => c.x);
            var minZ = cells.Min(c => c.z); var maxZ = cells.Max(c => c.z);
            return $"图 {homeMap.uniqueID} 的 Home 区 {cells.Count} 格，范围 ({minX},{minZ})–({maxX},{maxZ})；"
                   + $"样本格 ({cells[0].x},{cells[0].z})";
        }
        // **Which pawns can work at all, and where do they live.** Without this the probe is unusable on a save whose
        // colonist ids are unknown: measured twice (`IsColonist` filter, then `AllPawns`) the control came back as an
        // animal (`Lincoln#3849`, the only pawn with a training tracker) and every work giver said no for a reason that
        // has nothing to do with the gate. Colonist ids are scattered across the global `thingIDNumber` space, so
        // brute-forcing them over HTTP is not an option; asking once is.
        // **Fixture setter for the Home area.** Measured round 136: this container save's Home area is **0 cells**, so
        // `WorkGiver_CleanFilth.HasJobOnThing` (`:311028`) can never return true on this map - the work gate's positive
        // half was unmeasurable not because of the gate but because there is nowhere cleaning applies. Painting Home
        // cells is a player-draggable world state, so a probe may set it for a reading as long as it says so and can put
        // it back; `unpaint` is the pair half of that promise.
        if (whoParts.Length >= 6 && (whoParts[0].Equals("paint", StringComparison.OrdinalIgnoreCase)
                                     || whoParts[0].Equals("unpaint", StringComparison.OrdinalIgnoreCase)))
        {
            var add = whoParts[0].Equals("paint", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(whoParts[1], out var pid) || !int.TryParse(whoParts[2], out var px)
                || !int.TryParse(whoParts[3], out var pz) || !int.TryParse(whoParts[4], out var pw)
                || !int.TryParse(whoParts[5], out var ph))
                return "用法：paint|unpaint <#地图> x z 宽 高";
            var paintMap = MapLifecycleSync.FindMapByUniqueId(pid);
            if (paintMap?.areaManager?.Home is not { } painted) return "本端没有该地图或该地图没有 Home 区";
            // `Area_Home.Set(IntVec3, bool)` is `protected` (`CS0122`), and the indexer setter routes through it, so the
            // only way to set a Home cell from outside is the same method by reflection. Reported, not swallowed: a
            // missing method name would otherwise look like "painted 0 cells" and read as "the area refused".
            var setter = HarmonyLib.AccessTools.Method(typeof(Area), "Set", [typeof(IntVec3), typeof(bool)]);
            if (setter is null) return "找不到 Area.Set(IntVec3,bool)（签名变更？），无法涂 Home 区";
            var changed = 0;
            for (var dx = 0; dx < Math.Max(1, pw); dx++)
                for (var dz = 0; dz < Math.Max(1, ph); dz++)
                {
                    var cell2 = new IntVec3(px + dx, 0, pz + dz);
                    if (!cell2.InBounds(paintMap)) continue;
                    if (painted[cell2] == add) continue;
                    setter.Invoke(painted, [cell2, add]);
                    changed++;
                }
            var live = painted.ActiveCells.Count();
            return $"{(add ? "涂上" : "抹掉")} Home {changed} 格（({px},{pz}) 起 {Math.Max(1, pw)}×{Math.Max(1, ph)}），"
                   + $"现在 Home 区 {live} 格";
        }
        if (whoParts.Length >= 1 && whoParts[0].Equals("who", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<string>();
            foreach (var eachMap in Find.Maps)
                foreach (var each in eachMap.mapPawns.AllPawns)
                {
                    if (each is null) continue;
                    var work = each.workSettings?.EverWork == true;
                    var home2 = data.HomeOfPawn(each.thingIDNumber);
                    if (!work && home2 == 0) continue;
                    rows.Add($"#{each.thingIDNumber} {each.LabelShortCap}/{(each.def?.defName ?? "?")}"
                             + $" 图{eachMap.uniqueID} {(work ? "可工作" : "无工作设置")} 归属 {(home2 == 0 ? "无" : "#" + home2.ToString())}"
                             + $"{(each.IsColonist ? " 殖民者" : string.Empty)}");
                }
            return rows.Count == 0 ? "本端没有可工作的小人，也没有归属记录"
                : $"可工作或已归属的小人 {rows.Count} 个：\n  " + string.Join("\n  ", rows.Take(24))
                  + (rows.Count > 24 ? $"\n  …（共 {rows.Count} 个，只列前 24 个）" : string.Empty);
        }

        if (!data.Active && !(argument ?? string.Empty).TrimStart().StartsWith("who", StringComparison.OrdinalIgnoreCase)) return "据点 0 个（所有权层关闭），工作闸门一律放行，问不出差别";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // **Scan mode: choose the object by whether the owner sees work on it.** A hand-picked target cannot answer the
        // question this probe exists for - measured: a casket and a tuft of tall grass both returned "owner sees 0,
        // visitor sees 0", which is the gate permitting *and* agreeing with vanilla, two readings that look identical.
        // Only an object where a work giver genuinely fires for the owner can show whether the same work is refused
        // for a permitted visitor.
        if (parts.Length >= 3 && parts[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            var scanId = int.TryParse(parts[1], out var sid) ? sid : -1;
            var scanMap = int.TryParse(parts[2], out var smid) ? MapLifecycleSync.FindMapByUniqueId(smid) : null;
            if (scanId < 0 || scanMap is null) return "用法：scan <小人 id> <#地图>";
            Pawn? who = null;
            foreach (var each in scanMap.mapPawns.AllPawns)
                if (each is not null && each.thingIDNumber == scanId) { who = each; break; }
            if (who is null) return $"本端没有小人 #{scanId}";
            var scanHome = data.HomeOfPawn(who.thingIDNumber);
            var lines = new List<string>();
            var examined = 0;
            var examinedInSites = 0;
            foreach (var candidate in scanMap.listerThings.AllThings)
            {
                if (candidate is null || candidate.def is null) continue;
                if (++examined > 4000) break;
                if (SettlementRule.HomeOf(data, candidate) == 0) continue;
                if (++examinedInSites > 900) break;
                Pawn? siteOwner = null;
                foreach (var each in scanMap.mapPawns.AllPawns)
                    if (each is not null && each != who && each.workSettings?.EverWork == true
                        && data.HomeOfPawn(each.thingIDNumber) == SettlementRule.HomeOf(data, candidate))
                    { siteOwner = each; break; }
                if (siteOwner is null) continue;
                var (ownerYes, _) = Taking(siteOwner, candidate);
                if (ownerYes.Count == 0) continue;
                var (visitorYes, _) = Taking(who, candidate);
                var scanLost = ownerYes.Except(visitorYes).ToList();
                lines.Add($"{candidate.def.defName}@({candidate.Position.x},{candidate.Position.z}) 业主 #{siteOwner.thingIDNumber} "
                          + $"{ownerYes.Count} 个 → 访客 #{scanId}（归属 {(scanHome == 0 ? "无" : "#" + scanHome.ToString())}）"
                          + $"{visitorYes.Count} 个"
                          + $"{(scanLost.Count == 0 ? "，无差" : "，访客独缺：" + string.Join(",", scanLost))}");
                if (lines.Count >= 4) break;
            }
            if (lines.Count == 0)
                return $"扫了 {examined} 个物件（据点内 {examinedInSites} 个），没有一个让同据点业主看到工作"
                       + "—— 这个存档里没有可用的对照物件，读数不能当结论"
                       + "（对照按 `EverWork` 选人：没有工作的业主等于没有对照）";
            return $"扫了 {examined} 个物件（据点内 {examinedInSites} 个），找到 {lines.Count} 个业主有工作的物件：\n  "
                   + string.Join("\n  ", lines);
        }
        if (parts.Length < 4)
            return "用法：<小人 id> <#地图> x z [defName]（物件要落在一个有业主的据点里），"
                   + "或 scan <小人 id> <#地图>，或 who";
        if (!int.TryParse(parts[0], out var pawnId) || !int.TryParse(parts[1], out var mapId)
            || !int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var z))
            return "参数要四个整数：小人 id、#地图、x、z";
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        Pawn? pawn = null;
        foreach (var candidate in Find.Maps)
            foreach (var each in candidate.mapPawns.AllPawnsSpawned)
                if (each is not null && each.thingIDNumber == pawnId) { pawn = each; break; }
        if (pawn is null) return $"本端没有小人 #{pawnId}";
        var cell = new IntVec3(x, 0, z);
        // **A cell holds several things and plants are usually first.** Measured: `(112,118)` had been given filth and
        // was reported as `Plant_TallGrass`, so the probe answered about a plant while the fixture was about filth - and
        // a plant is exactly the kind of object no work giver takes with `forced: false`, so every reading came back 0.
        // The cell's contents are listed in the answer, and an optional fifth argument picks one by defName.
        var atCell = map.thingGrid.ThingsAt(cell).Where(t => t?.def is not null).ToList();
        var cellNames = string.Join(",", atCell.Select(t => t.def.defName).Distinct());
        var wantDef = parts.Length >= 5 ? parts[4] : null;
        var thing = wantDef is null
            ? atCell.FirstOrDefault(t => t.def.category != ThingCategory.Plant)
            : atCell.FirstOrDefault(t => t.def.defName == wantDef);
        if (thing is null)
            return wantDef is null
                ? $"图 {mapId} ({x},{z}) 没有物件（本格：{(cellNames.Length == 0 ? "空" : cellNames)}）"
                : $"图 {mapId} ({x},{z}) 没有 {wantDef}（本格：{(cellNames.Length == 0 ? "空" : cellNames)}）";
        var site = SettlementRule.HomeOf(data, thing);
        if (site == 0) return $"图 {mapId} ({x},{z}) 的 {thing.def?.defName} 不在任何据点里，工作闸门不会拒绝";
        var home = data.HomeOfPawn(pawn.thingIDNumber);

        static (List<string> Yes, List<string> Threw) Taking(Pawn who, Thing target)
        {
            var yes = new List<string>();
            var threw = new List<string>();
            foreach (var def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                var worker = def?.Worker as WorkGiver_Scanner;
                if (worker?.def is null) continue;
                try
                {
                    if (worker.HasJobOnThing(who, target, forced: false)) yes.Add(worker.def.defName);
                }
                // **Exceptions are counted apart from permits.** Measured first version: eight work givers "said yes" and
                // every one of them was this catch block - `ExecuteGuiltyColonist`, `HaulToCarrier`, `LoadTransporters`
                // and friends throw when asked about an arbitrary object (they need a prisoner, a carrier, a loaded
                // transporter). Printing those as "allowed" would have read as a gate that permits eight things; the
                // honest reading is "none permitted, eight not applicable".
                catch (Exception) { threw.Add(worker.def.defName); }
            }
            yes.Sort(StringComparer.Ordinal);
            threw.Sort(StringComparer.Ordinal);
            return (yes, threw);
        }

        // **Why a "0 work givers" answer is not a gate reading.** `HasJobOnThing(pawn, t, forced: false)` also returns
        // false when the pawn has no work priority for that type, cannot reach the thing, or when the giver needs a
        // prisoner/carrier - none of which is the gate. So the gate's own decision is counted separately, per work giver,
        // by asking `SettlementGates.Can(pawn, WorkActOf(worker, thing), thing)` - the same predicate the prefix uses.
        // Measured before this existed: filth and a steel stack both answered `owner 0, visitor 0`, which reads as "the
        // gate permits everyone" and as "the gate refuses everyone" at the same time.
        static (int Denied, List<string> Acts) GateVerdict(Pawn? who, Thing target)
        {
            var denied = 0;
            var acts = new List<string>();
            if (who is null) return (0, acts);
            foreach (var def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                if (def?.Worker is not WorkGiver_Scanner worker) continue;
                var act = SettlementGates.WorkActOf(worker, target);
                if (SettlementGates.Can(who, act, target)) continue;
                denied++;
                acts.Add(worker.def.defName + "→" + act);
            }
            acts.Sort(StringComparer.Ordinal);
            return (denied, acts);
        }

        var (visitor, visitorThrew) = Taking(pawn, thing);
        // **Why vanilla said no, per pawn.** The work gate is only one of the reasons `HasJobOnThing` returns false, and
        // measured three times in a row the zero could not be attributed: owner 1 / visitor 0 on the very same filth, with
        // the gate permitting both. The four checks here are exactly the ones `WorkGiver_CleanFilth.HasJobOnThing`
        // (`:311022-311041`) and `ReservationManager.CanReserve` (`:258589-258596`) perform, so the answer names which of
        // them failed instead of being a number to argue about.
        static string WhyRefused(Pawn who, Thing target)
        {
            var spawnOk = who.Spawned && who.Map == target.MapHeld;
            var canReserve = who.MapHeld?.reservationManager is { } rm && rm.CanReserve(who, target, 1, -1, null, false);
            var fogged = target.Fogged();
            return $"#{who.thingIDNumber} spawned={who.Spawned} mapOK={who.Map == target.MapHeld} "
                   + $"canReserve={canReserve} fogged={fogged} drafted={who.Drafted} "
                   + $"homeArea={(target.MapHeld?.areaManager?.Home is { } ha && ha[target.Position] ? "是" : "否")}"
                   + $"{(spawnOk ? "" : "（未生成或在别的图）")}";
        }

        var (visitorDenied, visitorActs) = GateVerdict(pawn, thing);
        // The owner control: a colonist claimed into the same settlement the object is in. Without it, "the visitor gets
        // nothing" cannot be told apart from "nothing ever takes a job on this kind of object".
        Pawn? owner = null;
        // `AllPawns`, not `AllPawnsSpawned`: a claimed pawn moved by `pawn-to-map` can be held rather than spawned, and
        // the first version's owner control silently came back empty for exactly that reason (measured: `没有可比的业主小人`
        // while `settlement-report` said `小人归属 #1=1人`). The control's only requirement is `HomeOfPawn == site` -
        // which is the same field the permit branch of the gate keys on, so it is the right pairing, and `IsColonist`
        // was an unnecessary narrowing that also excluded the very pawns a settlement can claim.
        // **The control must be able to work at all.** Measured: the widest lookup still returned an animal (`#3849`,
        // the one pawn with a training tracker) and every work giver said no to it for reasons that have nothing to do
        // with the gate - work types are zero for a non-worker. `EverWork` is the vanilla predicate for "this pawn has
        // work givers in the first place", so pairing against it is what makes "the owner sees work, the visitor does
        // not" a statement about the gate instead of a statement about the pawn.
        foreach (var candidate in map.mapPawns.AllPawns)
            if (candidate is not null && candidate != pawn && candidate.workSettings?.EverWork == true
                && data.HomeOfPawn(candidate.thingIDNumber) == site) { owner = candidate; break; }
        var (ownerList, ownerThrew) = owner is null ? (null, null) : Taking(owner, thing);
        var (ownerDenied, _) = GateVerdict(owner, thing);
        var lost = ownerList is null
            ? "没有可比的业主小人"
            : $"业主 #{owner!.thingIDNumber} 有 {ownerList.Count} 个，访客独缺："
              + (ownerList.Except(visitor).Any() ? string.Join(",", ownerList.Except(visitor)) : "无");
        var inHome = map.areaManager?.Home is { } homeArea && homeArea[cell];
        var filthAge = thing is Filth filthNow ? filthNow.TicksSinceThickened : -1;
        return $"物件 {thing.def?.defName}（据点 #{site}；本格：{cellNames}；该格在 Home 区：{(inHome ? "是" : "否")}"
               + $"{(filthAge < 0 ? "" : $"；厚度 {filthAge} ticks（清洁要 ≥600）")}）；小人 #{pawnId} 归属 "
               + $"{(home == 0 ? "无" : "#" + home.ToString())}、该据点授权 {DescribeGrants(data.GrantsFor(playerIdOf(pawn, data), site))}；"
               + $"工作闸门放行 {visitor.Count} 个{(visitor.Count == 0 ? "（无）" : "：" + string.Join(",", visitor))}"
               + $"；不适用（调用抛异常）{visitorThrew.Count} 个"
               + $"{(visitorThrew.Count == 0 ? "" : "：" + string.Join(",", visitorThrew))}"
               + $"；{lost}"
               + $"{(ownerThrew is null || ownerThrew.Count == 0 ? "" : "；业主侧不适用 " + ownerThrew.Count + " 个")}"
               + $"；闸门判定 访客拒绝 {visitorDenied} 对 / 业主拒绝 {ownerDenied} 对"
               + $"{(visitorDenied == 0 ? "" : "（访客被拒：" + string.Join(",", visitorActs.Take(6)) + "）")}"
               + $"；vanilla 检查 {WhyRefused(pawn, thing)}"
               + $"{(owner is null ? "" : " / " + WhyRefused(owner, thing))}";
    }

    /// <summary>The player id a pawn answers to, for printing a grant set. `SettlementOf` reads the membership table,
    /// which is keyed by player; a pawn's own site is what matters here, so the two-step is deliberate.</summary>
    private static int playerIdOf(Pawn pawn, SettlementData data)
    {
        var home = data.HomeOfPawn(pawn.thingIDNumber);
        if (home == 0) return 0;
        foreach (var member in data.Members)
            if (member.Value == home) return member.Key;
        return 0;
    }

    internal static string ChokepointProbe(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        // One split for every mode: the modes are checked before the ownership guard (a fixture has to be buildable from
        // zero settlements), so each branch reads this array rather than re-splitting and drifting out of order.
        
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        if (!data.Active) return "据点 0 个（所有权层关闭），闸门一律放行，问不出拒绝";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return "用法：<小人 id> <#地图> x z [动作]（先 script-settlement 声明据点、settlement-claim 认领小人）";
        if (!int.TryParse(parts[0], out var pawnId)) return "小人 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1], out var mapId)) return "#地图 不是整数：" + parts[1];
        if (!int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var z))
            return "坐标不是整数：" + parts[2] + " " + parts[3];
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        Pawn? pawn = null;
        foreach (var candidate in Find.Maps)
            foreach (var each in candidate.mapPawns.AllPawnsSpawned)
                if (each is not null && each.thingIDNumber == pawnId) { pawn = each; break; }
        if (pawn is null) return $"本端没有小人 #{pawnId}";
        var cell = new IntVec3(x, 0, z);
        var thing = map.thingGrid.ThingsAt(cell).FirstOrDefault();
        if (thing is null)
        {
            // **An empty cell is the reservation gate's other half.** Its thing branch asks `Use`/`Store` about the
            // object; its cell branch is what hauling into a stockpile goes through, and nothing could read it because
            // this probe stopped at "no thing here". Measured round 139: a stockpile cell answered about the cell.
            var zone = map.zoneManager?.ZoneAt(cell);
            // **Ask the act the gate will ask.** The gate asks `Store` for a stockpile cell and `Place` otherwise, so a
            // probe that hardcoded `Store` compared "haul into a stockpile" against "paint a zone" and reported a
            // disagreement that was the probe's own - the round-134 mistake in a new place (measured: a plain empty cell
            // read 放行/拒绝 with `BuildArea`). The control reading only exists once both sides ask the same question.
            var onStorage = SettlementRule.KindOfCell(map, cell) == SettlementKind.Storage;
            var cellAct = onStorage ? SettlementAct.Store : SettlementAct.Place;
            var cellPatched = map.reservationManager.CanReserve(pawn, new LocalTargetInfo(cell));
            var cellBare = SettlementGates.CanCell(pawn, cellAct, cell);
            return $"小人 #{pawnId}（归属 {(data.HomeOfPawn(pawn.thingIDNumber) is var h0 && h0 == 0 ? "无" : "#" + h0)}）"
                   + $"问图 {mapId} ({x},{z}) 的格子（区域 {(zone is null ? "无" : zone.GetType().Name + "「" + zone.label + "」")}，"
                   + $"在据点 #{data.SettlementAt(map.uniqueID, cell)}）：保留闸门 CanReserve=⌈{(cellPatched ? "放行" : "拒绝")}⌉，"
                   + $"裸判据 CanCell({cellAct})=⌈{(cellBare ? "放行" : "拒绝")}⌉ → "
                   + $"{(cellPatched == cellBare ? "一致（补丁是活的）" : "不一致（补丁与判据走岔）")}";
        }
        var homeOfPawn = data.HomeOfPawn(pawn.thingIDNumber);
        var homeOfThing = SettlementRule.HomeOf(data, thing);
        // **The reservation gate asks `Use`, always.** `SettlementReservationGate` calls
        // `SettlementGates.Can(claimant, SettlementAct.Use, target.Thing)`; it is the seam where "may I take this"
        // arrives as "may I reserve it", and `SettlementGates.Permitted` carries the explicit carve-out that lets a
        // `Take`-granted player reserve food, supply and equipment. So comparing `CanReserve` against a raw call with an
        // arbitrary act compares two different questions: measured in round 134 as a *disagreement* in both directions -
        // `TakeApart` with no grant read 放行/拒绝, and after granting only `Construction` it read 拒绝/放行. Both were
        // the probe's own defect: the patch was asking `Use` while the probe asked `TakeApart`.
        //
        // What is comparable is `Use` against `Use`. A second act is still worth printing, but labelled as what it is -
        // the answer of a *different* gate (work giver / designator), not of this one.
        var act = parts.Length > 4 && Enum.TryParse<SettlementAct>(parts[4], true, out var parsedAct)
            ? parsedAct
            : SettlementAct.Use;
        var patched = map.reservationManager.CanReserve(pawn, new LocalTargetInfo(thing));
        var raw = SettlementGates.Can(pawn, SettlementAct.Use, thing);
        var agrees = patched == raw;
        var other = "";
        if (act != SettlementAct.Use)
        {
            var rawOther = SettlementGates.Can(pawn, act, thing);
            other = $"；另：动作={act} 的判据答案={(rawOther ? "放行" : "拒绝")}"
                    + "（保留闸门不按这个动作判，它由工作闸门/设计器闸门决定 —— 这一行不是本闸门的读数）";
        }
        return $"小人 #{pawnId}（归属据点 {(homeOfPawn == 0 ? "无" : "#" + homeOfPawn.ToString())}）问图 {mapId} ({x},{z}) 的"
               + $" {thing.def?.defName}（据点 {(homeOfThing == 0 ? "无" : "#" + homeOfThing.ToString())}）："
               + $"保留闸门 CanReserve={(patched ? "放行" : "拒绝")}，裸判据 Can(Use)={(raw ? "放行" : "拒绝")}"
               + $" → {(agrees ? "一致（补丁是活的）" : "**不一致：补丁路径没有问到判据的答案**")}"
               + other;
    }

    internal static string GateProbe(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        if (!data.Active) return $"据点 0 个（所有权层关闭），闸门一律放行；参数应为 #地图 x z [动作] [玩家]，收到 {argument}";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return "用法：#地图 x z [动作] [玩家]（例：0 125 125 Build 2）";
        if (!int.TryParse(parts[0], out var mapId)) return "地图 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var z))
            return "坐标不是整数：" + parts[1] + " " + parts[2];
        var act = parts.Length > 3 && Enum.TryParse<SettlementAct>(parts[3], true, out var parsed) ? parsed : SettlementAct.Build;
        var player = parts.Length > 4 && int.TryParse(parts[4], out var explicitPlayer)
            ? explicitPlayer
            : SettlementGates.LocalPlayer + 1;
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        var cell = new IntVec3(x, 0, z);
        var site = data.SettlementAt(mapId, cell);
        if (site == 0)
            return $"图 {mapId} ({x},{z})：不属于任何据点（home=0，闸门直接放行，这是空地的正常答案）"
                   + $"；本图据点 {string.Join("/", data.Sites.Where(s => s.MapId == mapId).Select(s => "#" + s.Id))}";
        var owner = data.Sites.FirstOrDefault(s => s.Id == site)?.OwnerPlayer ?? -1;
        var grants = data.GrantsFor(player, site);
        var owned = data.Owns(player, site);
        var answer = SettlementGates.CanCell(player, act, map, cell);
        var self = SettlementGates.LocalPlayer;
        var selfAnswer = SettlementGates.CanCell(self, act, map, cell);
        // **The thing gate as well as the cell gate.** They are separate decisions with separate inputs - the cell one
        // asks the rectangle, the thing one asks `SettlementRule.HomeOf`, which uses `thing.MapHeld` - and round 132
        // found a reader bug in the grant path that only the cell side happened to expose. A probe that answers one
        // gate and is called "the gate probe" invites the reading "both sides are fine".
        var thing = map.thingGrid.ThingsAt(cell).FirstOrDefault();
        var thingRows = new StringBuilder();
        if (thing is not null)
        {
            var kind = SettlementRule.KindOf(thing, act);
            var selfThing = SettlementGates.Can(self, act, thing);
            var otherThing = SettlementGates.Can(player, act, thing);
            thingRows.Append($"；物件 {thing.def?.defName}（kind={kind}）：本端玩家 {self}={(selfThing ? "放行" : "拒绝")}"
                            + $"，玩家 {player}={(otherThing ? "放行" : "拒绝")}");
        }
        else
        {
            thingRows.Append("；该格没有物件（东西闸门这一半未测）");
        }
        return $"图 {mapId} ({x},{z})：据点 #{site}「{data.Sites.FirstOrDefault(s => s.Id == site)?.Name}」，业主=玩家 {owner}"
               + $"；本端玩家 {self}：{(selfAnswer ? "放行" : "拒绝")}（归属={data.Owns(self, site)}）"
               + $"；玩家 {player}：{(answer ? "放行" : "拒绝")}（归属={owned}，授权={DescribeGrants(grants)}）"
               + $"；动作={act}" + thingRows;
    }

    /// <summary>Round 153: the automation half of the ownership layer, which no pawn gate covers.
    ///
    /// <para>`ThingUtility.CheckAutoRebuildOnDestroyed` (`:160060`) and `CheckAutoRebuildTerrainOnDestroyed` (`:160069`)
    /// run inside `Destroy` with no actor, and `AutoBuildRoofAreaSetter.TryGenerateAreaFor` (`:91537`) works on a room.
    /// The probe answers the gate for one cell, reports whether the three Harmony targets really carry a patch (round 140's
    /// lesson: a `[HarmonyPatch]` class nobody installs is dead code), and - since the cell is usually empty - makes a
    /// fixture settlement owned by another player and asks the gate at the fixture's own centre, so the refusal branch is
    /// exercised rather than assumed. `ScriptedSettlement.Place` always builds around the map centre, which is why the
    /// fixture reading uses the centre rather than the cell the caller passed.</para></summary>
    internal static string AutomationProbe(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return "用法：#地图 x z [业主玩家]（例：0 120 120 2）";
        if (!int.TryParse(parts[0], out var mapId)) return "地图 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var z))
            return "坐标不是整数：" + parts[1] + " " + parts[2];
        var owner = parts.Length > 3 && int.TryParse(parts[3], out var parsedOwner) ? parsedOwner : 2;
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        var cell = new IntVec3(x, 0, z);
        var report = new StringBuilder();
        report.Append($"图 {mapId} ({x},{z})：据点层 {(data.Active ? "开" : "关")}，本端玩家 {SettlementGates.LocalPlayer}");
        // Which of the three targets really carry a patch: `Harmony.GetPatchInfo` answers for the installed session,
        // which is the whole question - the registration list alone proves nothing.
        foreach (var (label, method) in new (string, System.Reflection.MethodInfo?)[]
                 {
                     ("建筑重建", typeof(ThingUtility).GetMethod("CheckAutoRebuildOnDestroyed", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)),
                     ("地形重建", typeof(ThingUtility).GetMethod("CheckAutoRebuildTerrainOnDestroyed", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)),
                     ("自动铺顶", typeof(AutoBuildRoofAreaSetter).GetMethod("TryGenerateAreaFor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)),
                 })
        {
            var info = method is null ? null : HarmonyLib.Harmony.GetPatchInfo(method);
            report.Append($"；{label} {(info is null || info.Prefixes.Count == 0 ? "**未挂上**" : "已挂上 " + info.Prefixes.Count)}");
        }
        var home = data.SettlementAt(mapId, cell);
        report.Append(home == 0
            ? $"；该格不属于任何据点 → 闸门 {(SettlementAutomationGate.Allowed(map, cell) ? "放行" : "拒绝")}（空地的正常答案）"
            : $"；该格属于据点 #{home}（业主 {data.Sites.FirstOrDefault(s => s.Id == home)?.OwnerPlayer}）→ 闸门 "
              + $"{(SettlementAutomationGate.Allowed(map, cell) ? "放行" : "拒绝")}");
        // Fixture: a settlement owned by somebody else, so the refusal branch gets a reading.
        //
        // Two things the first version got wrong, both caught by the reading itself: `Place` puts the rectangle around the
        // colonists' centroid rather than the map centre (`Anchor`), and `Retire` takes an **id**, not a name (it answered
        // `BadArgument`). So the fixture is located from the site record it just created, and retired by its id.
        var made = Place($"AutoProbe|{owner}|3|3", owner);
        var fixture = made.Ok
            ? data.Sites.LastOrDefault(site => site is not null && site.Name == "AutoProbe" && !site.Retired)
            : null;
        if (fixture is null)
        {
            report.Append($"；造夹具失败 {made.Code}：{made.Detail}");
            report.Append($"；{SettlementAutomationGate.Summary()}");
            return report.ToString();
        }
        try
        {
            var centre = new IntVec3((fixture.MinX + fixture.MaxX) / 2, 0, (fixture.MinZ + fixture.MaxZ) / 2);
            var allowed = SettlementAutomationGate.Allowed(map, centre);
            var mine = SettlementGates.LocalPlayer == fixture.OwnerPlayer;
            report.Append($"；夹具据点 #{fixture.Id}（业主 {fixture.OwnerPlayer}，本端{(mine ? "就是业主" : "**不是**业主")}）"
                          + $"在 ({centre.x},{centre.z}) → 闸门 {(allowed ? "放行" : "拒绝")}");
        }
        finally
        {
            var retired = Retire(fixture.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            report.Append($"；夹具已移除（{(retired.Ok ? "成功" : retired.Code)}）");
        }
        report.Append($"；{SettlementAutomationGate.Summary()}");
        return report.ToString();
    }

    /// <summary>Round 154: whether another settlement's goods are <i>presented</i> as forbidden to a guest.
    ///
    /// <para>The reading has to be two-sided: `IsForbidden(thing, pawn)` for the owner (false: it is their stuff) and for
    /// the guest (true: it is not), plus the three grants that would un-hide it. A one-sided reading of "true" would also
    /// be produced by an item somebody simply forbade by hand.</para></summary>
    internal static string ForbidVisibilityProbe(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return "用法：<小人 id> #地图 x z（例：873 0 118 139）";
        if (!int.TryParse(parts[0], out var pawnId)) return "小人 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1].TrimStart('#'), out var mapId)) return "地图 id 不是整数：" + parts[1];
        if (!int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var z))
            return "坐标不是整数：" + parts[2] + " " + parts[3];
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        var guest = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
            .FirstOrDefault(p => p?.thingIDNumber == pawnId);
        if (guest is null) return $"本端没有小人 #{pawnId}";
        var cell = new IntVec3(x, 0, z);
        var report = new StringBuilder();
        report.Append($"据点层 {(data.Active ? "开" : "关")}；小人 #{pawnId} 属据点 {data.HomeOfPawn(pawnId)}");
        // Optional fifth argument: which thing at the cell to ask about. Grass and filth share cells with everything, and
        // a probe that always takes the first one reports the grass rather than the item the question was about.
        var wantedDef = parts.Length > 4 ? parts[4] : null;
        var thing = map.thingGrid.ThingsAt(cell)
            .FirstOrDefault(t => t.Spawned && !(t is Mote)
                                 && (wantedDef is null || string.Equals(t.def?.defName, wantedDef, StringComparison.OrdinalIgnoreCase)));
        if (thing is null)
            return report.Append($"；({x},{z}) 没有物件（这一半未测）").ToString();
        var home = SettlementRule.HomeOf(data, thing);
        report.Append($"；物件 {thing.def?.defName}#{thing.thingIDNumber} 属据点 {home}"
                      + (wantedDef is null ? string.Empty : $"（按 defName 选）")
                      + $" 分类 {SettlementRule.KindOf(thing)}");
        if (home == 0)
        {
            report.Append("；该物件不属于任何据点 ⇒ 闸门不应藏它"
                          + $"（IsForbidden={ForbidUtility.IsForbidden(thing, guest)}）");
            report.Append($"；{SettlementForbidVisibility.Summary()}");
            return report.ToString();
        }
        var site = data.Sites.FirstOrDefault(s => s.Id == home);
        var ownerPawn = map.mapPawns.AllPawnsSpawned
            .FirstOrDefault(p => p?.thingIDNumber is { } id2 && data.HomeOfPawn(id2) == home);
        var guestHidden = ForbidUtility.IsForbidden(thing, guest);
        var ownerHidden = ownerPawn is null ? (bool?)null : ForbidUtility.IsForbidden(thing, ownerPawn);
        report.Append($"（业主 {site?.OwnerPlayer}）→ 访客 IsForbidden={guestHidden}"
                      + (ownerHidden is null ? "；本据点没有已认领的小人（业主一侧未测）" : $"；业主 IsForbidden={ownerHidden}"));
        report.Append($"，访客授权 Take={SettlementGates.Can(guest, SettlementAct.Take, thing)}"
                      + $"/Use={SettlementGates.Can(guest, SettlementAct.Use, thing)}"
                      + $"/Store={SettlementGates.Can(guest, SettlementAct.Store, thing)}");
        report.Append($"；{SettlementForbidVisibility.Summary()}");
        return report.ToString();
    }

    /// <summary>Round 155: the cell half of the work gate, read per work giver.
    ///
    /// <para>Twelve work givers act on cells. Each row prints three things side by side: the act this gate derives, the
    /// bare ownership answer, and what the <b>installed</b> `HasJobOnCell` actually returned. The bare answer alone cannot
    /// show that the patch is wired, and `HasJobOnCell` alone cannot show that ownership is the reason it said no - vanilla
    /// refuses most cells for its own reasons (no roof area, no snow, nothing harvestable).</para>
    ///
    /// <para>For `GrowerHarvest` the probe forces the plant in the cell to full growth and restores it afterwards: a
    /// freshly spawned plant is at 5% and vanilla refuses it before ownership is ever consulted, which reads as "no gap"
    /// while measuring nothing.</para></summary>
    internal static string CellGateProbe(string? argument)
    {
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return "用法：<小人 id> #地图 x z（例：873 0 116 139）";
        if (!int.TryParse(parts[0], out var pawnId)) return "小人 id 不是整数：" + parts[0];
        if (!int.TryParse(parts[1].TrimStart('#'), out var mapId)) return "地图 id 不是整数：" + parts[1];
        if (!int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var z))
            return "坐标不是整数：" + parts[2] + " " + parts[3];
        var map = MapLifecycleSync.FindMapByUniqueId(mapId);
        if (map is null) return $"本端没有地图 {mapId}";
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned)
            .FirstOrDefault(p => p?.thingIDNumber == pawnId);
        if (pawn is null) return $"本端没有小人 #{pawnId}";
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return $"({x},{z}) 不在地图内";

        // Every cell-work giver: an override of `HasJobOnCell` that is not the base declaration.
        var signature = new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) };
        var givers = DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Select(def => def?.Worker).OfType<WorkGiver_Scanner>()
            .Where(giver => giver.GetType().GetMethod("HasJobOnCell", signature)?.DeclaringType != typeof(WorkGiver_Scanner))
            .GroupBy(giver => giver.GetType().Name).Select(group => group.First())
            .OrderBy(giver => giver.GetType().Name, StringComparer.Ordinal).ToList();

        var report = new StringBuilder();
        var home = data.SettlementAt(map.uniqueID, cell);
        report.Append($"据点层 {(data.Active ? "开" : "关")}；小人 #{pawnId} 属据点 {data.HomeOfPawn(pawnId)}；"
                      + $"({x},{z}) 属据点 {home}，分类 {SettlementRule.KindOfCell(map, cell)}；"
                      + $"格子工作 giver {givers.Count} 个");
        var plant = cell.GetPlant(map);
        var oldGrowth = plant?.Growth ?? 0f;
        try
        {
            if (plant is not null) plant.Growth = 1f;
            foreach (var giver in givers)
            {
                var act = SettlementGates.CellWorkAct(giver);
                var thing = SettlementGates.CellWorkTarget(map, cell, act);
                var kind = SettlementRule.KindOfCell(map, cell);
                var hasRow = thing is null && SettlementRuleTable.HasRow(act, kind);
                // `bare` must be the gate's answer, not "false because there is no row": the gate *permits* a pair the
                // table does not describe, and the first version printed 拒绝 for exactly those three givers, which made the
                // row contradict its own `why` ("表里没有这一对（不拒绝） 裸判据=拒绝").
                var bare = thing is not null
                    ? SettlementGates.Can(pawn, act, thing)
                    : !hasRow || SettlementGates.CanCell(pawn, act, cell);
                var why = thing is not null ? $"看物件 {thing.def?.defName}"
                    : hasRow ? "看格子" : "表里没有这一对（放行，不拒绝）";
                bool actual;
                try
                {
                    actual = giver.HasJobOnCell(pawn, cell);
                }
                catch (Exception error)
                {
                    report.Append($"；{giver.GetType().Name} 动作 {act} {why} 裸判据={(bare ? "放行" : "拒绝")}"
                                  + $" HasJobOnCell 抛 {error.GetType().Name}");
                    continue;
                }
                if (act == SettlementAct.Gather && plant is null)
                {
                    report.Append($"；{giver.GetType().Name} 动作 {act} 该格没有植物（这一半未测）");
                    continue;
                }
                report.Append($"；{giver.GetType().Name} 动作 {act} {why} 裸判据={(bare ? "放行" : "拒绝")}"
                              + $" HasJobOnCell={(actual ? "true" : "false")}");
            }
        }
        finally
        {
            if (plant is not null) plant.Growth = oldGrowth;
        }
        report.Append(plant is null ? "；该格没有植物" : $"；植物 {plant.def?.defName} 生长已复原 {oldGrowth:0.##}");
        report.Append($"；{SettlementGates.Summary()}");
        return report.ToString();
    }

    internal static string Probe(string? argument)
    {
        // **A probe must not be measured by the counters it validates (round 95).** This method asks `Can` twelve times
        // per sampled object to print twelve act rows, and `settlement-counters` reported that as real traffic:
        // measured 「放行 370 … 战争 62」 with 「按动作：Use/Plant=4 Take/Plant=4 …」 on a map where no colonist had asked
        // for anything - the whole table was this probe's own questions about one plant. Scoped with `using` so an
        // exception cannot leave counting switched off.
        using var uncounted = SettlementGates.Uncounted();
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        if (!data.Active) return "据点 0 个（所有权层关闭），闸门一律放行，没有可测的东西";
        // **Every settlement carries its own map, so the probe must not assume they share one.** `SettlementSite.MapId`
        // exists because a multi-settlement colony spans maps, and `SettlementAt(mapId, cell)` respects it. Reading only
        // `Find.CurrentMap` here meant a report taken while looking at map 0 described every settlement's pawns as if
        // they stood on map 0 - the same class of defect the claim path had. The visitor sample stays on the current map
        // (that is where a player is looking), but each settlement's own map is named so a mismatch is visible.
        var map = Find.CurrentMap;
        if (map is null) return "没有当前地图";

        // A visitor: a colonist whose home is a different settlement from the one it is standing in, or - if the base
        // is not split yet - the colonist standing furthest from its own settlement's centre.
        Pawn? visitor = null;
        var ownerSite = data.Sites.Count > 0 ? data.Sites[0] : null;
        // One owning pawn **per settlement**, not one overall. The earlier version kept a single `owner` — the first
        // pawn whose `HomeOfPawn` was non-zero — and printed it beside every sample. On a two-settlement map that meant
        // the sample taken in settlement #1 was judged against a pawn belonging to #2, so every row read
        // `业主=拒×7` and the gate looked broken. Measured: `Plant #4060 在据点 1 … 业主=拒×7` while #1 was owned by
        // player 1 and the pawn belonged to #2. That is the correct answer to the wrong question.
        var owners = new Dictionary<int, Pawn>();
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is null || pawn.RaceProps?.Humanlike != true) continue;
            if (!pawn.IsColonist) continue;
            visitor ??= pawn;
            // Both sides in one call. An unaffiliated visitor answers with `Unaffiliated` grants, and a report that only
            // ever shows that side reads as "the layer refuses everything" - the owner's own pawn is the control, and it
            // has to be printed even when no pawn is currently owned.
            var home = data.HomeOfPawn(pawn.thingIDNumber);
            if (home != 0) owners.TryAdd(home, pawn);
        }

        var text = new System.Text.StringBuilder();
        text.Append("探针：地图 ").Append(map.uniqueID).Append("，小人 ").Append(visitor?.thingIDNumber ?? -1)
            .Append(" 归属 ").Append(visitor is null ? -1 : data.HomeOfPawn(visitor.thingIDNumber))
            .Append("；据点 ");
        foreach (var site in data.Sites)
            text.Append('#').Append(site.Id).Append('「').Append(site.Name).Append("」主人").Append(site.OwnerPlayer)
                // The map each settlement lives on, and whether it is the one being looked at. Without it a two-map
                // colony's report cannot be checked against anything: `map=1` on a settlement is invisible while the
                // probe only ever prints the current map's id once.
                .Append(" 地图").Append(site.MapId).Append(site.MapId == map.uniqueID ? "" : "(非当前)")
                // **Ownership by both records, per site.** A player owning two settlements keeps only the last one in
                // `Members`, so `属于 #2` beside `#1 主人1` read as a contradiction - and it was: the gate compared
                // against the scalar and refused the player their own #1. `Owns` reads `OwnerPlayer` too, and printing
                // both here is what makes that visible instead of inferred.
                .Append(data.Owns(SettlementGates.LocalPlayer, site.Id) ? " 本端拥有" : " 非本端")
                .Append(' ');
        text.Append("；无归属玩家 99 的授权 = ").Append(data.GrantsFor(99, ownerSite?.Id ?? 0));
        // War and alliance, in the wire as well as in the table.
        //
        // **Why the round trip and not just the table.** `Pacts` was the last part of this table with no channel: it is
        // scribed (`SettlementData.cs:239`) so it survived a save, and `SettlementWar.AtWar` is consulted before every
        // grant, but `SettlementWire` did not carry it - so a replica kept the pact list from the *save* and an enemy's
        // pawns got the visitor four on every peer but the authority. A reading that only printed the local list would
        // have looked correct in exactly that broken state, which is why the second half encodes the table and decodes
        // it back.
        text.Append("；战争/同盟 ");
        if (data.Pacts.Count == 0) text.Append("无");
        else
        {
            var first = true;
            foreach (var pact in data.Pacts)
            {
                if (pact is null) continue;
                if (!first) text.Append(", ");
                first = false;
                var nowTick = Find.TickManager?.TicksGame ?? 0;
                var ban = pact.NoticeLeft(nowTick);
                text.Append('#').Append(pact.A).Append('-').Append('#').Append(pact.B)
                    .Append(pact.Allied ? " 同盟" : pact.WarSince < 0 ? " 和平" : " 交战中");
                // The declaration ban is a property of a pact that is neither at war nor allied, so it needs its own
                // suffix: a row printed as 「#1-#2 和平」 was, before round 94, indistinguishable from one that would
                // refuse a declaration for another 50 000 ticks.
                if (ban > 0) text.Append("（宣战禁令还剩 ").Append(ban).Append(" tick）");
            }
        }
        // **Does the war actually refuse anything (round 94).** Every earlier reading of this table printed the state -
        // 「通知中」 / 「交战中」 / 「同盟」 - and `AtWar` was never once observed returning true from a gate, because round
        // 80 had put a 60 000-tick countdown on the declaration edge and no probe advanced the clock that far. A
        // `WarActive` that returned false forever, or a gate that forgot to call it, would have looked identical to a
        // working war for 14 rounds.
        //
        // The reading asks the same question the gate asks, through the same function, and prints both sides so a
        // disagreement is visible: the state, and the answer `AtWar` gives right now.
        if (data.Pacts.Count > 0)
        {
            text.Append("；开战判定 ");
            var any = false;
            foreach (var pact in data.Pacts)
            {
                if (pact is null) continue;
                any = true;
                var answer = SettlementWar.AtWar(data, pact.A, pact.B);
                text.Append('#').Append(pact.A).Append('-').Append('#').Append(pact.B).Append('=')
                    .Append(answer ? "拒" : "不拒");
                if (!answer && pact.WarSince >= 0) text.Append("（战争记录未置起）");
            }
            if (!any) text.Append("无");
        }
        var roundTrip = RoundTripPacts(data);
        text.Append("；过信道 ").Append(roundTrip);
        // Kept cells, in the same two halves. Zero on almost every save - which is exactly why it needs its own line:
        // 「过信道 1 行一致」 for the pacts says nothing about whether the *other* new field on the wire works, and an
        // empty list round-trips trivially. The reading is meaningful only when a row exists, so the count is printed
        // beside it and the probe below creates one.
        // Empty list: say so, rather than "0 行一致". An empty list round-trips trivially, and printing that as
        // 「过信道 0 行一致」 reads like a passing check of the field when it is a check of nothing - the same shape as
        // the 「应用 0」 counters this project has already mistaken for a broken channel twice (rounds 74, 75).
        var keptCount = data.Overrides.Count;
        text.Append("；例外格 ").Append(keptCount).Append(" 条，");
        text.Append(keptCount == 0 ? "空表（往返无意义，先 kept-cell 写一条）" : "过信道 " + RoundTripKeptCells(data));
        // Per-map ownership answer. The sample above only covers the current map, so on a two-settlement colony the
        // second territory was never asked about - and `SettlementAt` takes a map id precisely so that it can be.
        // Asking it from the *outside* (a cell at each site's centre) is the check: a site whose map id does not
        // resolve, or whose rectangle is on the wrong map, answers 0 here and would read as "no settlement" rather
        // than as a bug.
        text.Append("\n  逐地图：");
        foreach (var site in data.Sites)
        {
            if (site is null || site.Retired) continue;
            var siteMap = MapLifecycleSync.FindMapByUniqueId(site.MapId);
            var centre = new IntVec3((site.MinX + site.MaxX) / 2, 0, (site.MinZ + site.MaxZ) / 2);
            var ownerHere = data.SettlementAt(site.MapId, centre);
            text.Append(" #").Append(site.Id).Append(" 图").Append(site.MapId)
                .Append(siteMap is null ? "（**本端无此图**）" : $"（{siteMap.Size.x}x{siteMap.Size.z}）")
                .Append(" 中心归属=").Append(ownerHere)
                .Append(ownerHere == site.Id ? " ✓" : " **与据点不符**").Append("；");
            // And the same cell asked about a map it is not on: the answer must be 0, or the map id is being ignored
            // and every settlement would claim every map.
            var otherMap = Find.Maps.FirstOrDefault(m => m is not null && m.uniqueID != site.MapId);
            if (otherMap is not null)
            {
                var cross = data.SettlementAt(otherMap.uniqueID, centre);
                text.Append("图").Append(otherMap.uniqueID).Append("同格=").Append(cross)
                    .Append(cross == 0 ? " ✓" : " **跨图也算**").Append("；");
            }
        }
        text.Append("；业主小人 ");
        if (owners.Count == 0) text.Append("无（没有任何小人归属于据点，业主路径无法演示）");
        else foreach (var pair in owners) text.Append("#").Append(pair.Value.thingIDNumber).Append("→据点").Append(pair.Key).Append(' ');

        // **The cell gate's map, asked from a pawn on each map (round 82).**
        //
        // The pawn overload used to read `Find.CurrentMap`, so a colonist standing in a second settlement had its cell
        // checked against the map being drawn. The reading is the pair: for a pawn on each map, ask the gate about a
        // cell inside *that* settlement and about the same three numbers on the *other* map. The first must be the
        // settlement's answer and the second must be 0 when the other settlement does not cover them.
        foreach (var site in data.Sites)
        {
            if (site is null || site.Retired) continue;
            var siteMap = MapLifecycleSync.FindMapByUniqueId(site.MapId);
            if (siteMap is null) continue;
            var centre = new IntVec3((site.MinX + site.MaxX) / 2, 0, (site.MinZ + site.MaxZ) / 2);
            Pawn? local = null;
            foreach (var candidate in siteMap.mapPawns.AllPawnsSpawned)
                if (candidate is { } && candidate.Faction == RimWorld.Faction.OfPlayer) { local = candidate; break; }
            if (local is null) continue;
            var other = Find.Maps.FirstOrDefault(each => each is not null && each.uniqueID != site.MapId);
            var ownAnswer = SettlementGates.CanCell(local, SettlementAct.Build, centre);
            text.Append("\n  格子门的图：#").Append(site.Id).Append(" 图").Append(site.MapId)
                .Append(" 小人 #").Append(local.thingIDNumber).Append(" 问自家中心格 = ")
                .Append(ownAnswer ? "准" : "拒");
            if (other is not null)
            {
                // The same numbers, asked about a map this pawn is not on. `SettlementAt` is map-keyed, so the answer
                // has to come from the *other* settlement's rectangle - and 0 when nothing covers it there.
                var crossOwner = data.SettlementAt(other.uniqueID, centre);
                text.Append("；同一组坐标问图").Append(other.uniqueID).Append(" = 归属 ").Append(crossOwner)
                    .Append(crossOwner == 0 ? "（无人认领，闸门放行）" : "（**被那张图的据点认领**，说明坐标跨图串了）");
            }
        }
        // **Which act the work gate asks for, per work giver, and whether the table can permit it at all (round 85).**
        //
        // The row count is the point, not the answers: `Permits(All, act, kind)` is true exactly when the table has a row
        // for that pair, so this prints "the table has a row" beside "the work gate asks for it". They disagreed for
        // Filth/Fire/Plant/WildAnimal/Animal until round 85 - the gate asked `Use`, `Use` has no row on those kinds, and an
        // unlisted pair is a refusal, so no grant could ever allow cleaning or hunting.
        text.Append("\n  工作表：");
        // Each work giver is paired with the target its act depends on, because one of them depends on it:
        // `WorkGiver_DoBill` answers `Use` for a workbench and `Operate` for a wounded pawn. Printing only the giver would
        // show the bench answer twice and hide the surgery side - the side that had no table row at all until round 89.
        foreach (var (giver, sample) in new (WorkGiver_Scanner Giver, Thing? Sample)[]
                 {
                     (new WorkGiver_CleanFilth(), null), (new WorkGiver_HunterHunt(), null),
                     (new WorkGiver_GrowerHarvest(), null), (new WorkGiver_PlantsCut(), null),
                     (new WorkGiver_Miner(), null), (new WorkGiver_Tend(), null),
                     // The patient is the visitor itself: what matters is the answer for a pawn target, not which pawn.
                     (new WorkGiver_DoBill(), visitor),
                 })
        {
            var act = SettlementGates.WorkActOf(giver, sample);
            var name = giver.GetType().Name;
            // What the gate used to ask, and whether that was even answerable. Printed so the difference is visible on a
            // system where the new mapping regresses.
            text.Append(' ').Append(name).Append("→").Append(act)
                .Append("(Use 可判=").Append(TableHasUseRowFor(giver) ? "是" : "否").Append(")；");
        }
        // **The door gate, asked through the engine's own method rather than through the table (round 86).**
        //
        // `Doors`/`Pass`/`Door` was a table row with no reader: doors are not reserved, worked or designated, so none of
        // the three existing gates is ever called for a doorway and the grant did nothing. The engine asks
        // `Building_Door.PawnCanOpen` from the pathfinder, so the reading has to be that method - printing
        // `SettlementGates.Can(pawn, Pass, door)` would only re-test the table and would pass with the patch missing
        // entirely, which is exactly the shape of mistake this project has made three times.
        text.Append("\n  门：");
        var doorSamples = 0;
        foreach (var thing in map.listerThings.AllThings)
        {
            if (thing is not Building_Door door) continue;
            var doorHome = SettlementRule.HomeOf(data, door);
            if (doorHome == 0) continue;
            doorSamples++;
            var owner = owners.TryGetValue(doorHome, out var found) ? found : null;
            text.Append(" #").Append(door.thingIDNumber).Append(" 在据点 ").Append(doorHome)
                .Append(" 访客开门=").Append(door.PawnCanOpen(visitor) ? "可" : "不可")
                .Append("(表=").Append(SettlementGates.Can(visitor, SettlementAct.Pass, door) ? "准" : "拒").Append(')');
            if (owner is not null)
                text.Append(" 业主开门=").Append(door.PawnCanOpen(owner) ? "可" : "不可")
                    .Append("(表=").Append(SettlementGates.Can(owner, SettlementAct.Pass, door) ? "准" : "拒").Append(')');
            else text.Append(" 业主=无");
            // **Which path refused (round 94).** `访客开门=不可(表=…)` printed the table's answer, and the table's answer
            // is not the gate's answer when the two settlements are at war: `Can` returns false before it reads the grants
            // at all. Measured with #1 at war with #3 and a permissive grant table, the line read 「不可(表=拒)」 - which
            // claims the table refused, and the table had not. The two refusals need different fixes (grant a flag vs make
            // peace), so a reading that conflates them sends the operator to the wrong screen.
            var guestHome = data.HomeOfPawn(visitor?.thingIDNumber ?? -1);
            if (SettlementWar.AtWar(data, guestHome, doorHome))
            {
                text.Append(" 拒因=交战（表=")
                    .Append(SettlementRuleTable.Permits(data.GrantsForPawn(guestHome, doorHome),
                        SettlementAct.Pass, SettlementKind.Door) ? "准" : "拒")
                    .Append("，战争盖过它）");
            }
            if (doorSamples >= 3) break;
        }
        if (doorSamples == 0) text.Append("（据点里没有门；闸门无从测起）");
        var samples = new List<(SettlementKind Kind, Thing Thing)>();
        // One sample per kind, but **per settlement**: the earlier version took the first thing of each kind and stopped,
        // so on a map whose settlements were both placed away from the buildings every sample read 「在据点 0」 and the
        // report was identical with one settlement, two, or none. Measured: `#1 Alpha (90,90)–(109,109)` and
        // `#2 Beta (140,140)–(159,159)` both reported twelve rows of 「无人领地，一律放行」.
        var perSite = new Dictionary<(int Site, SettlementKind Kind), int>();
        foreach (var thing in map.listerThings.AllThings)
        {
            if (thing is null) continue;
            var kind = SettlementRule.KindOf(thing, SettlementAct.Use);
            if (kind == SettlementKind.None) continue;
            var at = SettlementRule.HomeOf(data, thing);
            // Two per (settlement, kind): one to test the owner's pawn and one for the outsider is the same object, so a
            // second is only useful when the first is outside every settlement.
            var key = (at, kind);
            var seen = perSite.TryGetValue(key, out var count) ? count : 0;
            if (seen >= (at == 0 ? 1 : 2)) continue;
            perSite[key] = seen + 1;
            samples.Add((kind, thing));
            if (samples.Count >= 24) break;
        }
        if (samples.Count == 0)
            text.Append("\n  （地图上没有可测的物体）");

        // The probe's own act list, and it has to contain the acts the work gate now asks for - otherwise a reading of
        // "小人=Use=拒" on a filthy floor says nothing about whether cleaning is permitted, which is the question round 85
        // fixed. `Clean`, `PutOut`, `Hunt`, `Handle` and `Tend` were missing from this list, so the fix had **no
        // observable reading at all** until they were added: every filth, fire, plant and animal row above was showing a
        // refusal for `Use` - an act the table deliberately has no row for on those kinds - and calling it a finding.
        var acts = new[]
        {
            SettlementAct.Use, SettlementAct.Take, SettlementAct.Store, SettlementAct.Build,
            SettlementAct.TakeApart, SettlementAct.Repair, SettlementAct.Gather,
            SettlementAct.Clean, SettlementAct.PutOut, SettlementAct.Hunt, SettlementAct.Handle,
            SettlementAct.Tend, SettlementAct.Operate, SettlementAct.Pass, SettlementAct.Relax,
        };
        foreach (var (kind, thing) in samples)
        {
            var home = SettlementRule.HomeOf(data, thing);
            text.Append("\n  ").Append(kind).Append(" #").Append(thing.thingIDNumber)
                .Append(" 在据点 ").Append(home);
            if (home == 0) { text.Append("（无人领地，一律放行）"); continue; }
            text.Append(" 小人=");
            foreach (var act in acts)
                text.Append(act).Append('=')
                    .Append(SettlementGates.Can(visitor, act, thing) ? "准" : "拒").Append(' ');
            // A player that belongs to nothing is the designator path: the human dragging a stockpile into somebody
            // else's base. Probed with id 99 so it cannot accidentally be a member of the settlement in question.
            text.Append(" 玩家99=");
            foreach (var act in acts)
                text.Append(act).Append('=')
                    .Append(SettlementGates.Can(99, act, thing) ? "准" : "拒").Append(' ');
            // The control: a pawn that owns this land must be allowed everything in it. Without this line the report can
            // only ever say "refused", and a gate hard-wired to false would look identical to a working one.
            // The control: a pawn that owns **this** land must be allowed everything in it. Taken per settlement, and
            // the absent case is printed as "no owner pawn here" rather than as a column of refusals - a refusal is an
            // answer about a gate, and there is no gate being asked when nobody owns the land.
            if (owners.TryGetValue(home, out var owner))
            {
                text.Append(" 业主=");
                foreach (var act in acts)
                    text.Append(act).Append('=')
                        .Append(SettlementGates.Can(owner, act, thing) ? "准" : "拒").Append(' ');
            }
            else text.Append(" 业主=无（据点 ").Append(home).Append(" 没有归属小人）");
        }
        return text.ToString();
    }

    /// <summary>Dumps every settlement, who belongs to which, and what a given player may do in each. The last part is
    /// the one that answers "why was that refused" without reading code.
    /// </summary>
    /// <summary>Writes a grant mask into the table, which is the one edit the game only exposes through
    /// <see cref="SettlementGrantWindow"/>.
    ///
    /// <para><b>Why a command and not just the window.</b> The authority is headless: it has no local player, so
    /// <c>LocalPlayer</c> is 0 and no one can open that window there. Measured consequence: `settlement-report` printed
    /// 「授权 0」 no matter how many settlements existed, and every gate answered from
    /// <c>SettlementData.Unaffiliated</c> - the visitor mask - for every player. Two settlements with distinct owners were
    /// therefore indistinguishable from two settlements with no owners at all, which is precisely the state the whole layer
    /// is supposed to move past.</para>
    ///
    /// <para>Argument form: <c>grants &lt;settlement&gt; &lt;player&gt; &lt;mask&gt;</c>, where mask is either a number or a
    /// comma list of flag names (case-insensitive). A mask of <c>-1</c> removes the record, so the settlement falls back to
    /// its default. The write goes through <see cref="SettlementSync.Touch"/> like the window's own edit does, so replicas
    /// hear about it in the same way.</para></summary>
    internal static string Grant(string? argument)
    {
        try
        {
            var data = SettlementGates.Data;
            if (data is null) return "没有运行中的游戏（据点数据挂在 GameComponent 上）";
            if (!data.Active) return "据点 0 个（所有权层关闭），没有可授权的对象";
            if (string.IsNullOrWhiteSpace(argument)) return "用法：grants <据点> <玩家> <掩码|旗标列表|-1 删除>";
            var parts = argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return "用法：grants <据点> <玩家> <掩码|旗标列表|-1 删除>";
            if (!int.TryParse(parts[0], out var siteId)) return "据点必须是整数：" + parts[0];
            if (!int.TryParse(parts[1], out var player)) return "玩家必须是整数：" + parts[1];
            if (data.Sites.All(site => site is null || site.Id != siteId)) return "没有这个据点：" + siteId;

            int mask;
            if (!int.TryParse(parts[2], out mask))
            {
                mask = 0;
                foreach (var name in parts[2].Split(','))
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length == 0) continue;
                    if (!Enum.TryParse<SettlementGrant>(trimmed, ignoreCase: true, out var flag))
                        return "不认识的旗标：" + trimmed + "（可用："
                            + string.Join(",", Enum.GetNames<SettlementGrant>()) + "）";
                    mask |= (int)flag;
                }
            }

            // **`To` is a settlement id, not a player id (round 88).** The command wrote the raw player id while
            // `GrantsForPawn` read the same field as a settlement id, so "player 2" became "settlement 2" whenever the
            // numbers coincided - and the gates, which ask `GrantsForPawn`, acted on the collision. Player 0 keeps its
            // old spelling but now means what it always acted like: the settlement-level record that speaks for every
            // visitor.
            var target = 0;
            if (player != 0)
            {
                target = data.SettlementOf(player);
                if (target == 0)
                    return $"玩家 {player} 不属于任何据点，写入没有接收者"
                        + "（闸门按据点查授权；用 player 0 写「对所有访客生效」的据点级记录）";
            }
            var existing = data.Grants.FirstOrDefault(row => row is not null && row.From == siteId && row.To == target);
            if (mask < 0)
            {
                if (existing is null) return $"据点 #{siteId} → {(target == 0 ? "所有访客" : "据点 " + target)} 本来就没有点授权，无需删除";
                data.Grants.Remove(existing);
            }
            else if (existing is null)
            {
                data.Grants.Add(new SettlementGrantRecord { From = siteId, To = target, Flags = mask });
            }
            else
            {
                existing.Flags = mask;
            }
            SettlementSync.Touch();
            var after = data.GrantsFor(player, siteId);
            return $"据点 #{siteId} → {(target == 0 ? "所有访客（据点级记录）" : "据点 " + target + "（玩家 " + player + "）")} 授权 {mask}"
                + (mask < 0 ? "（已删除，回落到默认）" : "（" + Describe(mask) + "）")
                + $"；该玩家此后的实际授权 = {after}（{(int)after}）";
        }
        catch (Exception error)
        {
            return "授权写入失败：" + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>The readable name of a mask, so a report says what the number means.
    ///
    /// <para><b>Single-bit names only.</b> `SettlementGrant` also declares composites - <c>Default</c> is
    /// <c>Doors|Care|Benches|Food</c> and <c>All</c> is everything - so listing every name whose bits are all present
    /// prints <c>Default</c> beside the four flags it is made of. Measured: a mask of 255 reported
    /// 「Doors,Care,Benches,Items,Default,Equipment,Food,Storage,Alerts」, which reads as nine permissions when it is eight
    /// and hides which four came from the composite.</para></summary>
    private static string Describe(int mask)
    {
        var names = new List<string>();
        foreach (var name in Enum.GetNames<SettlementGrant>())
        {
            var flag = (int)Enum.Parse<SettlementGrant>(name);
            // A single bit is `x != 0 && (x & (x - 1)) == 0`; anything else is a composite this table's rows never use.
            if (flag == 0 || (flag & (flag - 1)) != 0) continue;
            if ((mask & flag) != flag) continue;
            names.Add(name);
        }
        return names.Count == 0 ? "无" : string.Join(",", names);
    }

    /// <summary>War, peace and alliances, driven from the portal    /// <summary>War, peace and alliances, driven from the portal so the gate can be exercised without a UI.
    ///
    /// <para>Argument form: <c>war A B</c> / <c>peace A B</c> / <c>ally A B</c> / <c>unaligned A B</c>. The three share the
    /// same two-settlement parse and the same validation, and the wire format is the same table the editor writes.</para></summary>
    /// <summary>An acceptance scenario for the ownership layer: one sequence, every step with its expected answer.
    ///
    /// <para><b>Why a scenario and not another probe (round 141).</b> Every gate in this layer has been measured
    /// separately - land by `settlement-gate`, goods by `settlement-chokepoint`, work by `settlement-workgate`, the
    /// reservation patch by both - and each reading was taken with the fixture arranged by hand. What has never been
    /// measured is the *sequence a player produces*: claim, be refused, be granted, be allowed, go to war, be refused
    /// again, make peace, be allowed again. Multi-channel defects live exactly in that ordering (the war check must come
    /// before the grants; the grant reader must address the visitor's own settlement; a retired site must stop governing
    /// its cells), and none of them shows up in a single question asked twice.
    ///
    /// <para>Runs inside <see cref="SettlementGates.Uncounted"/>: the scenario asks the gates about twenty times and none
    /// of those questions are gameplay. It declares its own two sites around the two pawns it is given and retires them
    /// afterwards, so it leaves the save as it found it.</para></summary>
    internal static string Scenario(string? argument)
    {
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏（据点数据挂在 GameComponent 上）";
        var parts = (argument ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "用法：settlement-scenario <小人A id> <小人B id>（A 属据点 1，B 属据点 2）";
        if (!int.TryParse(parts[0], out var idA) || !int.TryParse(parts[1], out var idB))
            return "小人 id 必须是整数：" + string.Join(" ", parts);

        var report = new System.Text.StringBuilder();
        var passed = 0;
        var total = 0;
        void Step(string what, bool got, bool expected)
        {
            total++;
            if (got == expected) passed++;
            report.Append($"  {total}. {what} → {(got ? "放行" : "拒绝")}（期望 {(expected ? "放行" : "拒绝")}）")
                  .Append(got == expected ? " 符合" : " **不符**")
                  .Append('\n');
        }

        Pawn? Find(int id)
        {
            foreach (var map in Verse.Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    if (pawn?.thingIDNumber == id && !pawn.Dead) return pawn;
            return null;
        }

        var a = Find(idA);
        var b = Find(idB);
        if (a is null || b is null) return $"没有找到小人：{(a is null ? "#" + idA + " " : "")}{(b is null ? "#" + idB : "")}";

        var siteA = 0;
        var siteB = 0;
        var createdA = false;
        var createdB = false;
        var warDeclared = false;
        try
        {
            using (SettlementGates.Uncounted())
            {
                // Two sites, one per pawn, small enough to stay inside a loaded map.
                // **Identify the two sites by the name just written, not by "last" or "min".** The first version took the
                // last non-retired row as Alpha and the smallest id as Beta, which is the same thing only when the table is
                // empty and ids ascend from 1 - on a save that had ever retired a site it silently swapped the two, and
                // every step would then be measuring the wrong settlement.
                int IdOf(string name)
                {
                    var match = data.Sites.FirstOrDefault(site => site is { Retired: false } && site.Name == name);
                    return match?.Id ?? 0;
                }

                // **Owners are players, not factions.** The first version wrote `a.Faction?.loadID` as the owner, which is
                // the faction's id - measured 22 on the container - so both sites belonged to the same "player" and the
                // scenario could not show two owners disagreeing. Defaults are players 1 and 2, overridable.
                var ownerA = parts.Length > 2 && int.TryParse(parts[2], out var parsedOwnerA) ? parsedOwnerA : 1;
                var ownerB = parts.Length > 3 && int.TryParse(parts[3], out var parsedOwnerB) ? parsedOwnerB : 2;
                var placedA = Place($"Alpha|{ownerA}|9|9|{a.Position.x},{a.Position.z}", 0);
                createdA = placedA.Ok;
                siteA = IdOf("Alpha");
                var placedB = Place($"Beta|{ownerB}|9|9|{b.Position.x},{b.Position.z}", 0);
                createdB = placedB.Ok;
                siteB = IdOf("Beta");
                if (!createdA || !createdB || siteA == 0 || siteB == 0 || siteA == siteB)
                    return "无法建立两个据点（" + placedA.Detail + " / " + placedB.Detail + "）";
                SettlementAreas.Rebuild(data);

                Claim($"{siteA} {idA}");
                Claim($"{siteB} {idB}");
                var cellA = a.Position;
                var cellB = b.Position;

                // 1-2. No grants at all: land is refused, and a pawn is not - `Default` carries `Care`, and a kind with no
                // `Use` row widens to whatever grant names it (rounds 136/138). This pair is the policy in one line.
                Step($"无授权 · 土地 Place @({cellA.x},{cellA.z})", SettlementGates.CanCell(b, SettlementAct.Place, cellA), false);
                Step("无授权 · 商品 Use(小人 A)", SettlementGates.Can(b, SettlementAct.Use, a), true);

                // 3-4. The land grant: both land rows move, because a player who may lay a blueprint must also be able to
                // paint the zone under it.
                Grant($"{siteA} {ownerB} BuildArea");
                Step("授权 BuildArea · 土地 Place", SettlementGates.CanCell(b, SettlementAct.Place, cellA), true);
                Step("授权 BuildArea · 土地 Build", SettlementGates.CanCell(b, SettlementAct.Build, cellA), true);

                // 5. War outranks every grant. If this step ever passes as "allowed", the war check has been placed after
                // the grant table and a declared enemy can act inside the settlement it is at war with.
                var war = SettlementWar.Declare(data, siteA, siteB);
                warDeclared = SettlementWar.AtWar(data, siteA, siteB);
                if (!warDeclared) report.Append($"  （宣战未生效：{war}）\n");
                Step("宣战 · 土地 Place（即使持 BuildArea）", SettlementGates.CanCell(b, SettlementAct.Place, cellA), false);

                // 6. Peace returns the grant, not a blank slate: `BuildArea` was never revoked.
                SettlementWar.Peace(data, siteA, siteB);
                Step("求和 · 土地 Place", SettlementGates.CanCell(b, SettlementAct.Place, cellA), true);

                // 7. A retired site governs nothing. Otherwise the overlay says "not claimed" while the gate still refuses.
                Retire(siteA.ToString());
                Step($"撤销据点 #{siteA} · 土地 Place", SettlementGates.CanCell(b, SettlementAct.Place, cellA), true);
                createdA = false;
            }
        }
        finally
        {
            using (SettlementGates.Uncounted())
            {
                if (warDeclared) SettlementWar.Peace(data, siteA, siteB);
                if (createdA) Retire(siteA.ToString());
                if (createdB) Retire(siteB.ToString());
            }
        }

        return $"据点场景（A=#{idA} 属 #{siteA}，B=#{idB} 属 #{siteB}）：{passed}/{total} 步符合\n" + report;
    }

    /// <summary>Disbands one settlement: the flag, the membership rows, the grants, and the overlay.
    ///
    /// <para><b>Extracted from the command in round 141.</b> The block lived inline in `settlement-retire`, and the new
    /// acceptance scenario needs the same four-part sweep. Copying it would have produced a second implementation of
    /// "retire" - and the four parts are exactly the kind of set that drifts: the flag is read in seven places, the
    /// `PawnHome` entries decide `HomeOfPawn`, the `Grants` rows decide `GrantsFor`, and the overlay decides what the
    /// player sees. Missing one leaves a settlement that is invisible or a permission for a place nobody can see.</para>
    ///
    /// <para>Ids are never reused (`Place` allocates `highest + 1`), so a retired row keeps meaning that place for
    /// grants and kept-cell overrides that name it.</para></summary>
    internal static (bool Ok, string Code, string Detail) Retire(string? argument)
    {
        if (!int.TryParse((argument ?? string.Empty).Trim(), out var retireId))
            return (false, "BadArgument", "用法：settlement-retire <据点编号>（用 settlement-report 查编号）");
        var data = SettlementGates.Data;
        if (data is null) return (false, "NoData", "据点表还没载入");
        SettlementSite? target = null;
        foreach (var candidate in data.Sites)
            if (candidate is not null && candidate.Id == retireId) { target = candidate; break; }
        if (target is null)
            return (false, "NotFound", $"没有据点 #{retireId}；现有 " + string.Join("/", data.Sites.Select(each => each?.Id)));
        if (target.Retired) return (false, "AlreadyRetired", $"据点 #{retireId}「{target.Name}」已经是废弃状态");
        // Counted before the sweep so the answer says what actually moved: a settlement with no members and no grants is
        // a legitimate state, and printing 0/0 there is the truth rather than a failure.
        var pawnsDropped = 0;
        foreach (var pawnId in data.PawnHome.Where(pair => pair.Value == retireId).Select(pair => pair.Key).ToList())
            if (data.PawnHome.Remove(pawnId)) pawnsDropped++;
        var grantsDropped = data.Grants.RemoveAll(record => record is not null && (record.From == retireId || record.To == retireId));
        var membersDropped = 0;
        foreach (var player in data.Members.Where(pair => pair.Value == retireId).Select(pair => pair.Key).ToList())
            if (data.Members.Remove(player)) membersDropped++;
        var defaultsDropped = data.Defaults.Remove(retireId) ? 1 : 0;
        target.Retired = true;
        // The table changed, so the replica has to be told - without this bump the settlement stays live on every other
        // peer while it is gone here.
        SettlementSync.Touch();
        SettlementAreas.Rebuild(data);
        var centre = new IntVec3((target.MinX + target.MaxX) / 2, 0, (target.MinZ + target.MaxZ) / 2);
        var ownerNow = data.SettlementAt(target.MapId, centre);
        return (true, "ok",
            $"据点 #{retireId}「{target.Name}」已废弃；清理 小人归属 {pawnsDropped} 条 / 授权 {grantsDropped} 条"
            + $" / 成员 {membersDropped} 条 / 默认授权 {defaultsDropped} 条"
            + $"；中心格归属现在 = {ownerNow}"
            + (ownerNow == retireId ? " **仍归它，废弃没生效**" : "（不再是它）")
            + $"；剩余据点 {data.Sites.Count(each => each is { Retired: false })}"
            + (SettlementAreas.LastFailure is { Length: > 0 } failure ? $"；**覆盖层重建失败 {failure}**" : "；覆盖层已重建"));
    }

    internal static string War(string? argument)
    {
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var parts = (argument ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return "用法：war|peace|ally|unaligned A B";
        if (!int.TryParse(parts[1], out var a) || !int.TryParse(parts[2], out var b))
            return "据点 id 必须是数字";
        var result = parts[0].ToLowerInvariant() switch
        {
            "war" => SettlementWar.Declare(data, a, b),
            "peace" => SettlementWar.Peace(data, a, b),
            "ally" => SettlementWar.SetAllied(data, a, b, true),
            "unaligned" => SettlementWar.SetAllied(data, a, b, false),
            // **Backdate the alliance break (round 94).** The declaration ban runs 60 000 ticks - one in-game day - so
            // the only way to observe "a war that is live actually refuses" on a world whose clock is at tick 358 is to
            // write the value the clock would have reached. This moves `NoticeUntil` into the past, which is exactly the
            // state the clock would produce; nothing else about the record changes.
            //
            // Round 80 had this operator backdating `WarSince` instead, because it had put the countdown on the wrong
            // edge. `AtWar` returning true had **never been observed**: every reading printed 「通知中」 and a `WarActive`
            // stuck false, or a gate that forgot to call it, would have looked the same.
            "expire" => SettlementWar.Expire(data, a, b),
            _ => "未知操作：" + parts[0],
        };
        Log.Message("[TurboTick] 据点战争：" + result);
        return result + "\n  " + SettlementWar.Summary(data);
    }

    /// <summary>Retired with round 94: the declaration ban is now on <see cref="SettlementPact.NoticeUntil"/> and backdated
    /// by <see cref="SettlementWar.Expire"/>. Kept as a comment rather than deleted code because the mistake it recorded -
    /// countdown on the wrong edge, so a war could never be observed - is the reason the war path was untested for 14
    /// rounds.</summary>
    private static string Report_BackdateWarRemoved() => "";

    internal static string Report(string? argument)
    {
        var data = SettlementGates.Data;
        if (data is null) return "没有运行中的游戏";
        var player = int.TryParse(argument, out var parsed) ? parsed : SettlementGates.LocalPlayer;
        var text = new System.Text.StringBuilder();
        // **Both records, because they disagree in exactly the case this mode adds.** `Members` holds one settlement per
        // player and keeps only the last written; `OwnerPlayer` is per site and keeps them all. A reading that printed
        // only the scalar said `本端玩家 1（属于 2）` for a player who owned #1 and #2 - and the gates' answer in #1 then
        // looked unexplained. Measured before `Owns` existed: two sites owned by player 1, `据点 2 个（成员 1，授权 0）`.
        var owned = data.SitesOwnedBy(player);
        text.Append(SettlementRule.Describe(data)).Append("；本端玩家 ").Append(player)
            .Append("（属于 ").Append(data.SettlementOf(player)).Append('）')
            .Append("；拥有 ").Append(owned.Count == 0 ? "无" : "#" + string.Join("/#", owned));
        if (owned.Count > 1)
            text.Append("（多据点：`Members` 只记最后一个，判定走 OwnerPlayer）");
        // How many pawns each settlement actually owns. Without this the report shows land and grants but not who is
        // governed by them, and "why did that colonist get refused" stays unanswerable.
        var perHome = new Dictionary<int, int>();
        foreach (var pair in data.PawnHome)
            perHome[pair.Value] = perHome.GetValueOrDefault(pair.Value) + 1;
        // How many entries in the membership table point at a pawn that is nowhere. `PawnHome` has no removal path -
        // it is written when a settlement is founded and never cleaned - so a dead or departed colonist keeps its
        // membership forever and the count above only grows. `SettlementSync.Publish` reconciles it; this reports the
        // state before that runs, which is what makes a non-zero number here a finding rather than a transient.
        var ghosts = CountGhostMembers(data);
        if (ghosts > 0) text.Append("（其中 ").Append(ghosts).Append(" 条指向已不在场的小人，发布时会清掉）");
        text.Append("；小人归属 ");
        if (perHome.Count == 0) text.Append("无");
        else
        {
            var first = true;
            foreach (var pair in perHome)
            {
                if (!first) text.Append(", ");
                first = false;
                text.Append('#').Append(pair.Key).Append('=').Append(pair.Value).Append("人");
            }
        }
        // **Colonists the table does not know about (round 93).** Membership is written when a settlement is founded, so
        // a colonist born, recruited or arrived afterwards is treated as a visitor on its own land: refused building,
        // food and storage by the gates that exist to keep strangers out. Printed unconditionally, because "无" was what
        // this line said for a map with four colonists - a number that is always shown is the only way the gap stops
        // looking like an empty table.
        var unrecorded = SettlementRule.UnrecordedColonists(data);
        text.Append("；未登记小人 ").Append(unrecorded).Append(" 个");
        if (unrecorded > 0) text.Append("（在此据点地上但无归属记录：会被当访客，用 settlement-claim 认领）");
        foreach (var site in data.Sites)
        {
            if (site is null) continue;
            var grants = data.GrantsFor(player, site.Id);
            text.Append("\n  #").Append(site.Id).Append(" 「").Append(site.Name).Append("」地图 ").Append(site.MapId)
                .Append(" (").Append(site.MinX).Append(',').Append(site.MinZ).Append(")–(")
                .Append(site.MaxX).Append(',').Append(site.MaxZ).Append(") 主人 ").Append(site.OwnerPlayer);
            // The retired marker is printed once, below, together with what it means. Printing it here as well produced
            // 「… 主人 1 已废弃 **已废弃**（授权已清空…）」 - the same fact twice in one line, which reads as two
            // different findings.
            // Whether the map this settlement names actually exists here. A site whose `MapId` resolves to nothing is
            // land on a map this peer does not have - which is exactly the state a replica is in before the map channel
            // catches up, and it would otherwise read as a normal settlement whose gates simply never fire.
            var siteMap = MapLifecycleSync.FindMapByUniqueId(site.MapId);
            if (siteMap is null)
                text.Append(" **本端没有这张地图**（闸门不会生效）");
            else if (site.Retired) { }
            else
            {
                // The rectangle has to lie inside the map, or the claim rows index cells that do not exist. Vanilla
                // clips at creation, so a mismatch means the site was made on a bigger map than this one.
                var clipped = CellRect.CenteredOn(
                    new IntVec3((site.MinX + site.MaxX) / 2, 0, (site.MinZ + site.MaxZ) / 2),
                    site.MaxX - site.MinX + 1, site.MaxZ - site.MinZ + 1);
                var inside = clipped.minX >= 0 && clipped.minZ >= 0
                    && clipped.maxX < siteMap.Size.x && clipped.maxZ < siteMap.Size.z;
                text.Append(inside ? " 矩形在图内" : $" **矩形越界**（图 {siteMap.Size.x}x{siteMap.Size.z}）");
            }
            // A retired settlement still lists its id and name - so an old grant record or kept-cell override that
            // names it can be traced - but not a grant table. Retiring removes every `Grants` row for it
            // (`settlement-retire`), so what `GrantsFor` answers now is the **default** for a settlement id that
            // nothing claims, and printing that as "该玩家授权 Default 可做：…" reads as "this player may do these things
            // there" for a place that no longer exists. Measured before this branch: a retired #1 printed a full
            // nine-action table identical to a live settlement's.
            if (site.Retired)
            {
                text.Append(" **已废弃**（授权已清空，闸门不再认这块地）");
                continue;
            }
            text.Append(" 该玩家授权 ").Append(grants);
            // A compact truth table beats a flags dump: "may build, may not take food" is what a player wants to know.
            text.Append(" 可做：");
            var acts = new List<string>();
            foreach (var act in new[]
                     {
                         SettlementAct.Pass, SettlementAct.Use, SettlementAct.Take, SettlementAct.Store,
                         SettlementAct.Build, SettlementAct.TakeApart, SettlementAct.Repair, SettlementAct.Hunt,
                         SettlementAct.Gather, SettlementAct.Place,
                     })
                foreach (SettlementKind kind in new[]
                         {
                             SettlementKind.Door, SettlementKind.Bed, SettlementKind.Bench, SettlementKind.Food,
                             SettlementKind.Supply, SettlementKind.Building, SettlementKind.Land,
                         })
                    if (SettlementRuleTable.Permits(grants, act, kind))
                        acts.Add(act + "/" + kind);
            text.Append(acts.Count == 0 ? "无" : string.Join(",", acts));
        }
        text.Append("\n  ").Append(SettlementAreas.Summary());
        if (SettlementAreas.LastFailure is { } failure) text.Append("（失败：").Append(failure).Append('）');
        return text.ToString();
    }
}
