using RimWorld;
using TurboTick.Networking;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>Shown when the handshake was refused because this player's mods differ from the room's.
///
/// <para><b>Why a table and not a paragraph.</b> "The hashes differ" is not something anyone can act on, and neither
/// is a sentence listing five names — the player needs to know <i>which</i> mod, <i>in what way</i>, and <i>what to
/// do</i>. So every mod in the room gets a row, with its own verdict, and the rows are ordered by severity: missing
/// first, then wrong build, then wrong order, then the ones that are fine. A mismatch with nothing missing usually
/// turns out to be one mod on a different build, which a missing/extra/order split cannot express.</para>
///
/// <para><b>Why the buttons are per row and also global.</b> Re-ordering forty mods by hand is not something anyone
/// does, so "同步加载顺序" writes the room's order into this player's <c>ModsConfig.xml</c> in one go. Subscribing
/// cannot be done for the player, but each missing row carries its workshop page, so the work is one click each
/// instead of a search per name.</para>
/// </summary>
internal sealed class ModSyncDialog : Window
{
    private readonly List<ModEntry> server;
    private readonly List<ModCheck> rows;
    private readonly string? serverGameVersion;
    private string localGameVersion = "";
    private Vector2 scroll;

    private const float RowHeight = 34f;
    private const float HeaderHeight = 46f;
    private const float FooterHeight = 44f;

    internal ModSyncDialog(string? serverManifest, string? roomVersion = null)
    {
        server = ModManifest.Parse(serverManifest);
        rows = ModManifest.Check(server, ModManifestGame.Local());
        serverGameVersion = roomVersion;
        try { localGameVersion = VersionControl.CurrentVersion.ToString(); }
        catch (Exception) { localGameVersion = ""; }
        doCloseX = true;
        // Not `doCloseButton`. Vanilla draws that one centred on the bottom edge (`Window.CloseButSize`), which is
        // exactly where this dialog's own two actions go - measured: the three overlapped into one unreadable bar.
        // The footer below places all three buttons itself.
        doCloseButton = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
        onlyOneOfTypeAllowed = true;
        resizeable = true;
    }

    internal static bool CanShow => !string.IsNullOrEmpty(RoomConnection.Client?.ServerMods);

    /// <summary>Height of the table body for <paramref name="count"/> rows, never more than the screen allows.
    ///
    /// <para>The dialog used to be a fixed 620 tall with a scroll view inside. On a 22-mod pack that is roughly what
    /// it needed; on a 200-mod pack the player got a 620px window with a scrollbar, i.e. a table that showed nine rows
    /// of a hundred and looked like it had been cut off. Measured on this install: the room page behind it drew its
    /// own content past its bottom edge at the same time, so both windows looked broken for the same reason - a size
    /// that was a constant instead of a measurement.</para>
    ///
    /// <para>The cap is the same `UI.screenHeight - 40` the multiplayer window uses, so the two dialogs cannot
    /// disagree about how tall a window may be.</para></summary>
    private static float TableHeight(int count)
    {
        // The 36 is `Window.Margin` on both sides: `InitialSize` returns the *window* size, while
        // `DoWindowContents` is handed that rect already contracted (`Verse/Window.cs:249`), so a total that omits
        // the margins is 36px short and the last row is clipped.
        var wanted = HeaderHeight + 24f + Math.Max(1, count) * RowHeight + FooterHeight + 14f + 36f;
        var ceiling = Math.Max(360f, UI.screenHeight - 40f);
        return Math.Min(wanted, ceiling);
    }

    public override Vector2 InitialSize
    {
        get
        {
            // Width grows with the widest thing the table must show (a long mod name plus four numeric columns) but
            // never past the monitor; the table re-flows because every column is a fraction of the width.
            var width = Math.Min(1000f, Math.Max(760f, UI.screenWidth - 80f));
            return new Vector2(width, TableHeight(rows.Count));
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        var oldFont = Text.Font;
        var oldAnchor = Text.Anchor;
        var oldWrap = Text.WordWrap;
        var oldColor = GUI.color;
        try
        {
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            // Title states the shape of the problem before any row does: "共 22 个：缺失 2 / 版本不同 1".
            Widgets.Label(new Rect(0f, 0f, inRect.width, 24f), ModManifest.Summary(rows, T));
            var gameLine = GameLine();
            if (gameLine.Length > 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.72f, 0.68f, 0.6f);
                Widgets.Label(new Rect(0f, 24f, inRect.width, 18f), gameLine);
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Small;
            var top = HeaderHeight;

            var viewport = new Rect(0f, top, inRect.width, inRect.height - top - FooterHeight - 10f);
            var content = new Rect(0f, 0f, viewport.width - 18f, Math.Max(viewport.height, rows.Count * RowHeight + RowHeight));
            Widgets.BeginScrollView(viewport, ref scroll, content);
            try
            {
                var y = 0f;
                var header = new Rect(0f, y, content.width, 22f);
                DrawColumns(header, T("TurboTick_ModCheck_ColumnName"), T("TurboTick_ModCheck_ColumnRoom"),
                    T("TurboTick_ModCheck_ColumnLocal"), T("TurboTick_ModCheck_ColumnVerdict"), true);
                y += 24f;
                foreach (var row in rows)
                {
                    var line = new Rect(0f, y, content.width, RowHeight);
                    DrawRow(line, row);
                    y += RowHeight;
                }
            }
            finally { Widgets.EndScrollView(); }

            // Three actions on one row: the two fixes, then close. The dialog's own close button is disabled in the
            // constructor, so nothing overlaps this row.
            var buttonsY = inRect.height - FooterHeight;
            var gap = 8f;
            var third = (inRect.width - gap * 2f) / 3f;
            if (Widgets.ButtonText(new Rect(0f, buttonsY, third, FooterHeight - 6f), T("TurboTick_ModSync_Subscribe")))
                Subscribe();
            if (Widgets.ButtonText(new Rect(third + gap, buttonsY, third, FooterHeight - 6f), T("TurboTick_ModSync_Reorder")))
                Reorder();
            if (Widgets.ButtonText(new Rect((third + gap) * 2f, buttonsY, third, FooterHeight - 6f), T("TurboTick_ModCheck_Close")))
                Close();
        }
        finally
        {
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
            Text.WordWrap = oldWrap;
            GUI.color = oldColor;
        }
    }

    /// <summary>"房间 1.7.5005 / 本机 1.7.5005" - the first thing to check when the whole list is green but the
    /// handshake still refused, because then the difference is the build itself.</summary>
    private string GameLine()
    {
        if (string.IsNullOrEmpty(serverGameVersion) && localGameVersion.Length == 0) return "";
        var mine = localGameVersion.Length == 0 ? T("TurboTick_ModCheck_Unknown") : localGameVersion.Trim();
        var theirs = string.IsNullOrEmpty(serverGameVersion) ? T("TurboTick_ModCheck_Unknown") : serverGameVersion.Trim();
        // Compared on the strings that are actually shown, not on the raw fields: a marker next to two identical
        // versions is unreadable, and the player's question is "do these two lines match".
        var same = string.Equals(theirs, mine, StringComparison.Ordinal);
        return T("TurboTick_ModCheck_GameVersions", theirs, mine)
            + (same ? "" : "   " + T("TurboTick_ModCheck_GameVersionDiffers"));
    }

    private static string T(string key, params object[] args) => TurboTickSettings.T(key, args);

    private static readonly Color Muted = new(0.66f, 0.63f, 0.56f);
    private static readonly Color Good = new(0.53f, 0.78f, 0.45f);
    private static readonly Color Warn = new(1f, 0.73f, 0.36f);
    private static readonly Color Bad = new(0.93f, 0.47f, 0.4f);

    private static Color VerdictColor(ModVerdict verdict) => verdict switch
    {
        ModVerdict.Ok => Good,
        ModVerdict.OutOfOrder => Warn,
        _ => Bad
    };

    /// <summary>One short label per verdict. Short on purpose: it is a column, and the sentence that explains what
    /// to do lives in the tooltip.</summary>
    private static string VerdictLabel(ModVerdict verdict) => T(verdict switch
    {
        ModVerdict.Ok => "TurboTick_ModCheck_Ok",
        ModVerdict.Missing => "TurboTick_ModCheck_Missing",
        ModVerdict.Extra => "TurboTick_ModCheck_Extra",
        ModVerdict.VersionMismatch => "TurboTick_ModCheck_Version",
        ModVerdict.OutOfOrder => "TurboTick_ModCheck_Order",
        ModVerdict.ContentMismatch => "TurboTick_ModCheck_Content",
        _ => "TurboTick_ModCheck_Ok"
    });

    /// <summary>Column x offsets. Fixed rather than proportional so the verdict column never moves: it is the one
    /// the eye scans down.</summary>
    private const float NameWidth = 0.44f;
    private const float ServerX = 0.46f;
    private const float LocalX = 0.56f;
    private const float StateX = 0.68f;

    private void DrawColumns(Rect rect, string name, string room, string mine, string state, bool header)
    {
        var color = header ? Muted : Color.white;
        Text.Font = GameFont.Tiny;
        Label(new Rect(rect.x, rect.y, rect.width * NameWidth, rect.height), name, color);
        Label(new Rect(rect.x + rect.width * ServerX, rect.y, rect.width * 0.1f, rect.height), room, color);
        Label(new Rect(rect.x + rect.width * LocalX, rect.y, rect.width * 0.1f, rect.height), mine, color);
        Label(new Rect(rect.x + rect.width * StateX, rect.y, rect.width * (1f - StateX), rect.height), state, color);
    }

    private void DrawRow(Rect rect, ModCheck row)
    {
        var color = VerdictColor(row.Verdict);
        // A tinted background rather than a coloured text run: with 20+ rows the eye finds the three bad ones by
        // scanning for the stripes, not by reading every word.
        if (row.Verdict != ModVerdict.Ok)
            Widgets.DrawBoxSolid(rect, new Color(color.r, color.g, color.b, 0.10f));
        Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), color);
        if (row.Verdict != ModVerdict.Ok) TooltipHandler.TipRegion(rect, Detail(row));

        Text.Font = GameFont.Small;
        GUI.color = Color.white;
        var name = row.Name.Length == 0 ? row.PackageId : row.Name;
        Widgets.LabelEllipses(new Rect(rect.x + 10f, rect.y + 8f, rect.width * NameWidth - 12f, 20f), name);

        Text.Font = GameFont.Tiny;
        GUI.color = Muted;
        Label(new Rect(rect.x + 10f, rect.y + 8f + 13f, rect.width * NameWidth - 12f, 14f), row.PackageId);
        Label(new Rect(rect.x + rect.width * ServerX, rect.y + 8f, rect.width * 0.1f, 16f), Order(row.ServerOrder));
        Label(new Rect(rect.x + rect.width * LocalX, rect.y + 8f, rect.width * 0.1f, 16f), Order(row.LocalOrder));
        GUI.color = color;
        Label(new Rect(rect.x + rect.width * StateX, rect.y + 8f, rect.width * (1f - StateX), 16f), VerdictLabel(row.Verdict));
        // A one-character age marker next to the verdict, not instead of it: the tooltip still explains the verdict,
        // and this only says which side is stale. Only drawn when the timestamps actually disagree by more than ten
        // seconds, so a normal reinstall does not put a marker on every row.
        if (row.OlderSide is { } older)
        {
            GUI.color = Muted;
            Label(new Rect(rect.x + rect.width * StateX, rect.y + 8f + 13f, rect.width * (1f - StateX), 14f),
                T(older == "room" ? "TurboTick_ModCheck_RoomOlderMark" : "TurboTick_ModCheck_LocalOlderMark",
                    Stamped(older == "room" ? row.ServerStamped : row.LocalStamped)));
        }

        // The workshop page is a button, not a note: "install this" is the fix for the commonest verdict.
        if (row.Verdict == ModVerdict.Missing && row.WorkshopUrl is not null)
        {
            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(rect.xMax - 84f, rect.y + 6f, 78f, 22f), T("TurboTick_ModSync_Open")))
                Open(row.WorkshopUrl);
        }
        GUI.color = Color.white;
    }

    private static string Order(int order) => order < 0 ? "—" : (order + 1).ToString();

    /// <summary>The tooltip: everything the row had to leave out, plus both versions when they are what differs.</summary>
    private static string Detail(ModCheck row)
    {
        var text = row.PackageId + "\n" + VerdictLabel(row.Verdict);
        if (row.Verdict is ModVerdict.VersionMismatch or ModVerdict.Ok)
            text += "\n" + T("TurboTick_ModCheck_Versions", Show(row.ServerVersion), Show(row.LocalVersion));
        // Which side is the stale copy. A content mismatch names the mod but not the direction, and the player's next
        // question is always "update, or roll back" - answered from the file timestamps the handshake carries.
        if (row.OlderSide is { } older)
            text += "\n" + T(older == "room" ? "TurboTick_ModCheck_RoomIsOlder" : "TurboTick_ModCheck_LocalIsOlder",
                Stamped(row.ServerStamped), Stamped(row.LocalStamped));
        var hint = row.Verdict switch
        {
            ModVerdict.Missing => "TurboTick_ModCheck_MissingHint",
            ModVerdict.Extra => "TurboTick_ModCheck_ExtraHint",
            ModVerdict.VersionMismatch => "TurboTick_ModCheck_VersionHint",
            ModVerdict.OutOfOrder => "TurboTick_ModCheck_OrderHint",
            ModVerdict.ContentMismatch => "TurboTick_ModCheck_ContentHint",
            _ => null
        };
        return hint is null ? text : text + "\n" + T(hint);
    }

    /// <summary>A timestamp as a local date, or an em dash when the peer did not send one. Never guesses "now":
    /// an older peer reports 0 and showing that as 1970 would read as a corrupt file.</summary>
    private static string Stamped(long unix)
    {
        if (unix <= 0) return "—";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException) { return "—"; }
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value)
        ? T("TurboTick_ModCheck_Undeclared") : value;

    private void Subscribe()
    {
        var opened = 0;
        foreach (var row in rows)
        {
            if (row.Verdict != ModVerdict.Missing || row.WorkshopUrl is null) continue;
            Open(row.WorkshopUrl);
            opened++;
        }
        Messages.Message(opened == 0
                ? T("TurboTick_ModSync_NoWorkshopPages")
                : T("TurboTick_ModSync_OpenedPages", opened),
            MessageTypeDefOf.NeutralEvent, historical: false);
    }

    private void Reorder()
    {
        if (!ModManifestGame.ApplyOrder(server, out var reason))
        {
            Messages.Message(reason, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Messages.Message(T("TurboTick_ModSync_ReorderDone"), MessageTypeDefOf.PositiveEvent, historical: false);
        Log.Message("[TurboTick] " + reason);
    }

    /// <summary>Draws text, restoring <c>GUI.color</c> afterwards. <paramref name="color"/> is optional so a caller
    /// that already set <c>GUI.color</c> (the verdict tint) does not have to repeat it.</summary>
    private static void Label(Rect rect, string text, Color? color = null)
    {
        var previous = GUI.color;
        if (color is { } tint) GUI.color = tint;
        Widgets.Label(rect, text);
        GUI.color = previous;
    }

    private static void Open(string url)
    {
        try { Application.OpenURL(url); }
        catch (Exception error) { Log.Warning("[TurboTick] 打开链接失败：" + error.Message + " " + url); }
    }

    /// <summary>Shows the dialog for the last refused handshake, if there is one to show.</summary>
    internal static void ShowForCurrentMismatch()
    {
        var client = RoomConnection.Client;
        var manifest = client?.ServerMods;
        if (string.IsNullOrEmpty(manifest)) return;
        try { Find.WindowStack?.Add(new ModSyncDialog(manifest, client?.ServerGameVersion)); }
        catch (Exception error) { Log.Warning("[TurboTick] 无法打开模组同步窗口：" + error.Message); }
    }

    /// <summary>One-line summary for the HUD when the window is not open.</summary>
    internal static string Describe()
    {
        var manifest = RoomConnection.Client?.ServerMods;
        if (string.IsNullOrEmpty(manifest)) return "";
        try { return ModManifest.Summary(ModManifest.Check(ModManifest.Parse(manifest), ModManifestGame.Local()),
            (key, args) => TurboTickSettings.T(key, args)); }
        catch (Exception error) { return "模组列表解析失败：" + error.Message; }
    }
}
