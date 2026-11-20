using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>The title-screen and in-game entry point share the same room UI and connection state.</summary>
public sealed class MultiplayerWindow : Window
{
    private enum WorldSource { Save, New, Current }
    private WorldSource source;
    private readonly List<FileInfo> saves = [];
    private FileInfo? selectedSave;
    private string search = "";
    private Vector2 scroll;
    private Vector2 bodyScroll;
    private Vector2 rosterScroll;
    private string error = "";
    private bool enteredRoom;
    private bool chooseFreshWorld;
    private static readonly Color Background = new(0.055f, 0.075f, 0.095f, 1f);
    private static readonly Color Card = new(0.09f, 0.115f, 0.14f, 1f);
    private static readonly Color Accent = new(0.32f, 0.83f, 0.73f, 1f);
    private static readonly Color Muted = new(0.62f, 0.69f, 0.75f, 1f);
    /// <summary>Amber, used for "not connected" - the state that needs the player to do something. Matches the
    /// verification table's `Warn` so one colour means one thing across both windows.</summary>
    private static readonly Color Warn = new(1f, 0.73f, 0.36f);

    /// <summary>The window is sized to fit the page it opens on, clamped to the screen.
    ///
    /// <para>Every page is measured at the width it will be drawn at and the window takes that height. Nothing
    /// scrolls: a scrollbar next to five lines of text reads as "this window is broken", and the fix is to measure
    /// rather than to make the page scroll. The only case that can overflow is a page taller than the screen, and
    /// there the window is clamped and the body scrolls - a 4K window cannot grow past the monitor.</para></summary>
    public override Vector2 InitialSize
    {
        get
        {
            var width = Math.Min(1000f, UI.screenWidth - 40f);
            var body = BodyWidth(width);
            var content = ShowsRoomPage ? RoomPageHeight(body)
                : MultiplayerHosting.ReadyToChoose ? HostPageHeight(body)
                : ConnectionCardHeight(body);
            return new Vector2(width,
                Math.Min(Math.Max(ChromeHeight() + content, 460f), UI.screenHeight - 40f));
        }
    }

    /// <summary>Everything around the body, in the coordinate space <see cref="InitialSize"/> works in.
    ///
    /// <para>Which is the *window* rect, while <see cref="DoWindowContents"/> is handed the same rect already
    /// contracted by <c>Window.Margin</c> on all four sides (<c>Verse/Window.cs:249</c>,
    /// <c>Rect rect3 = rect.ContractedBy(Margin)</c>, and <c>Margin</c> is <c>18f</c>; this method's caller passes
    /// <c>rect3.AtZero()</c>). The old value summed only the in-rect offsets - 98 for the header block and 87 for
    /// the status strip and footer - and therefore under-reported the window's own height by exactly the two
    /// margins, 36px.</para>
    ///
    /// <para>Measured: the room page drew its guide text under the two action buttons, and the bottom of the card
    /// was clipped mid-sentence, because the body was 38px shorter than the page it was measured for. Nothing
    /// scrolled, because the body rect was also pinned to the viewport height - so the overflow was simply lost.
    /// Both halves of that are fixed here and in <see cref="DoWindowContents"/>.</para></summary>
    private static float ChromeHeight() => 98f + 87f + 2f * 18f;

    /// <summary>Width the body is drawn at. Matches the viewport arithmetic in <see cref="DoWindowContents"/>, so the
    /// measurement in <see cref="InitialSize"/> is taken at the width the page will really have.</summary>
    private static float BodyWidth(float windowWidth) => Math.Max(560f, windowWidth - 36f);

    public MultiplayerWindow()
    {
        doCloseX = true;
        absorbInputAroundWindow = true;
        preventCameraMotion = true;
        forcePause = false; // A local window must never pause a shared simulation.
        closeOnAccept = false;
        doWindowBackground = true;
        onlyOneOfTypeAllowed = true;
        source = Current.ProgramState == ProgramState.Playing ? WorldSource.Current : WorldSource.New;
        RefreshSaves();
    }

    public static void Open()
    {
        if (Find.WindowStack == null || Find.WindowStack.WindowOfType<MultiplayerWindow>() != null) return;
        Find.WindowStack.Add(new MultiplayerWindow());
    }

    internal static void OpenAfterServerReset()
    {
        if (Find.WindowStack == null) return;
        Find.WindowStack.WindowOfType<MultiplayerWindow>()?.Close();
        Find.WindowStack.Add(new MultiplayerWindow { chooseFreshWorld = true, source = WorldSource.New, enteredRoom = true });
    }

    public override void PostClose()
    {
        base.PostClose();
        TurboTickMod.Settings?.Write();
    }

    public override void WindowUpdate()
    {
        base.WindowUpdate();
        if (enteredRoom && SharedColony.Session.Phase == Simulation.SharedPhase.Playing) Close();
    }

    public override void DoWindowContents(Rect inRect)
    {
        var font = Text.Font;
        var anchor = Text.Anchor;
        var wrap = Text.WordWrap;
        var color = GUI.color;
        try
        {
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Widgets.DrawBoxSolid(inRect, Background);
            Widgets.DrawBoxSolid(new Rect(inRect.x, inRect.y, inRect.width, 3f), Accent);
            Label(new Rect(22, 18, inRect.width - 240, 32), T("TurboTick_Multiplayer"), GameFont.Medium, Color.white);
            if (Button(new Rect(inRect.width - 204, 17, 160, 34), T("TurboTick_ManageServer"))) ServerAdminWindow.Open();
            Label(new Rect(22, 55, inRect.width - 44, 30), T(chooseFreshWorld ? "TurboTick_AdminResetComplete" : "TurboTick_LobbySubtitle"), GameFont.Small, Muted);
            var viewport = new Rect(18, 98, inRect.width - 36, inRect.height - 185);
            var bodyWidth = Math.Max(560f, viewport.width);
            // The body is drawn at exactly the size the page measured for, so nothing scrolls in the normal case.
            //
            // The scroll view stays, and is the reason this works at every screen size: `InitialSize` clamps the
            // window to `UI.screenHeight - 40`, so on a short screen the measured page no longer fits and *some*
            // overflow has to be reachable. Keeping the rect equal to `viewport` height means the common case has a
            // body that is neither taller nor shorter than its contents - a shorter body is what put a scrollbar next
            // to five lines of text, and a taller one is what left the empty half on this page.
            // The body must be at least as tall as the page it holds, or the overflow is unreachable: a scroll view
            // whose content rect equals its viewport has nothing to scroll, so anything past the bottom edge is
            // silently clipped. Measured on the disconnected room page - the guide line was drawn underneath the two
            // action buttons and the card ended mid-sentence.
            //
            // `InitialSize` normally makes the two numbers equal, so this only matters when the window is clamped to
            // `UI.screenHeight - 40`: there the measured page genuinely does not fit and scrolling is the answer.
            var page = ShowsRoomPage ? RoomPageHeight(bodyWidth) : 0f;
            var body = new Rect(0, 0, Math.Max(560f, viewport.width), Math.Max(Math.Max(1f, viewport.height), page));
            Widgets.BeginScrollView(viewport, ref bodyScroll, body);
            try
            {
                // No host/join tabs: the server always owns the simulation, so there is exactly one
                // page. Publishing a colony is a second button on it, not a different mode.
                if (MultiplayerHosting.ReadyToChoose) DrawHost(body);
                else if (ShowsRoomPage) DrawRoom(body);
                else DrawConnection(body);
            }
            finally { Widgets.EndScrollView(); }
            // Status and footer share one strip: a two-line status used to overlap the footer line because
            // both were positioned from `inRect.height` independently.
            //
            // The room page already prints `RoomConnection.Status` large in its own column, so the strip falls back
            // to nothing there instead of repeating the same sentence twice on one page - visible in the screenshot
            // that reported the overlap: "模组不一致：…" appeared both at the top of the card and again at the bottom.
            var status = error.Length > 0 ? error : MultiplayerHosting.Status;
            if (status.Length == 0 && !ShowsRoomPage) status = RoomConnection.Status;
            var statusHeight = Math.Min(46f, Math.Max(22f, Text.CalcHeight(status, inRect.width - 44)));
            Label(new Rect(22, inRect.height - 40 - statusHeight, inRect.width - 44, statusHeight), status, GameFont.Tiny,
                error.Length > 0 ? new Color(1f, 0.65f, 0.5f) : Muted);
            Widgets.DrawBoxSolid(new Rect(22, inRect.height - 32, inRect.width - 44, 1), new Color(1f, 1f, 1f, 0.06f));
            Label(new Rect(22, inRect.height - 26, inRect.width - 44, 22), T("TurboTick_LobbyFooter"), GameFont.Tiny, new Color(Muted.r, Muted.g, Muted.b, 0.7f));
        }
        finally { Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; GUI.color = color; }
    }

    private void DrawHost(Rect area)
    {
        var gap = 14f;
        var left = new Rect(area.x, area.y, Math.Max(300f, area.width * 0.55f - gap / 2f), area.height);
        var right = new Rect(left.xMax + gap, area.y, area.width - left.width - gap, area.height);
        Panel(left);
        Panel(right);
        DrawHostConfig(right);
        SectionTitle(new Rect(left.x + 20, left.y + 16, left.width - 40, 24), T("TurboTick_WorldSource"));
        var y = left.y + 56;
        // Both branches draw the same 66px card; they differ only in which source it selects, so the layout
        // after it is identical and must not be duplicated per branch.
        if (Current.ProgramState == ProgramState.Playing && !chooseFreshWorld)
        {
            if (Choice(new Rect(left.x + 20, y, left.width - 40, 66), T("TurboTick_CurrentColony"),
                T("TurboTick_CurrentColonyDesc"), source == WorldSource.Current)) source = WorldSource.Current;
        }
        else if (Choice(new Rect(left.x + 20, y, left.width - 40, 66), T("TurboTick_NewColony"),
            T("TurboTick_NewColonyDesc"), source == WorldSource.New)) source = WorldSource.New;
        y += 78;
        Label(new Rect(left.x + 20, y, left.width - 140, 18), T("TurboTick_ExistingSaves"), GameFont.Tiny, Muted);
        if (Button(new Rect(left.xMax - 106, y - 4, 86, 26), T("TurboTick_Refresh"))) RefreshSaves();
        y += 28;
        Text.Font = GameFont.Small;
        search = Widgets.TextField(new Rect(left.x + 20, y, left.width - 40, 28), search);
        TooltipHandler.TipRegion(new Rect(left.x + 20, y, left.width - 40, 28), T("TurboTick_SearchSaves"));
        y += 38;
        var view = new Rect(left.x + 20, y, left.width - 40, Math.Max(20, left.yMax - y - 20));
        var filtered = saves.Where(file => file.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0)
        {
            Label(view.ContractedBy(8), T("TurboTick_NoSaves"), GameFont.Small, Muted);
            return;
        }
        var rowHeight = 56f;
        var content = new Rect(0, 0, view.width - 18, filtered.Length * rowHeight);
        Widgets.BeginScrollView(view, ref scroll, content);
        try
        {
            for (var i = 0; i < filtered.Length; i++)
            {
                var file = filtered[i];
                var row = new Rect(0, i * rowHeight, content.width, 50);
                var chosen = source == WorldSource.Save && selectedSave?.FullName == file.FullName;
                if (Choice(row, Path.GetFileNameWithoutExtension(file.Name),
                    file.LastWriteTime.ToString("g") + "  ·  " + (file.Length / 1024) + " KiB", chosen))
                { selectedSave = file; source = WorldSource.Save; }
            }
        }
        finally { Widgets.EndScrollView(); }
    }

    private void DrawHostConfig(Rect right)
    {
        var x = right.x + 20;
        var width = right.width - 40;
        const float Pad = 20f;
        const float ButtonHeight = 42f;
        SectionTitle(new Rect(x, right.y + 16, width, 24), T("TurboTick_DedicatedConnected"));
        Label(new Rect(x, right.y + 56, width, 54), RoomConnection.Endpoint, GameFont.Small, Color.white);
        if (Button(new Rect(x, right.y + 114, width, 30), T("TurboTick_CopyPort")))
            GUIUtility.systemCopyBuffer = RoomConnection.Endpoint;
        Label(new Rect(x, right.y + 158, width, Math.Max(48, right.height - 158 - Pad - ButtonHeight * 2 - 12)),
            T("TurboTick_HostSaveGuide"), GameFont.Small, Muted);
        var title = source == WorldSource.New ? T("TurboTick_CreateAndHost") : T("TurboTick_StartHosting");
        var enabled = MultiplayerHosting.ReadyToChoose && (source != WorldSource.Save || selectedSave != null);
        if (Button(new Rect(x, right.yMax - Pad - ButtonHeight * 2 - 8, width, ButtonHeight), title, true, enabled))
        {
            // Close first: the native wizard/load dialog must own the window stack.
            Close();
            if (!MultiplayerHosting.Begin(selectedSave, source == WorldSource.New, source == WorldSource.Current)) Open();
        }
        if (Button(new Rect(x, right.yMax - Pad - 30, width, 30), T("TurboTick_Disconnect"))) RoomConnection.Leave();
    }

    /// <summary>Height of the connection page's two cards, measured at a given body width. Shared by
    /// <see cref="InitialSize"/> and <see cref="DrawConnection"/> so the window is never shorter than the
    /// content and the cards never stretch to fill a taller window than they need.</summary>
    private static float ConnectionCardHeight(float bodyWidth)
    {
        const float Pad = 22f;
        const float ButtonHeight = 42f;
        const float FieldHeight = 50f;   // 18 (label) + 4 (gap) + 28 (input)
        var gap = 14f;
        var leftWidth = Math.Max(280f, bodyWidth * 0.475f - gap / 2f);
        var rightWidth = bodyWidth - leftWidth - gap;
        var leftContent = 24 + 14 + FieldHeight + 16 + FieldHeight + 22 + ButtonHeight;
        var rw = rightWidth - Pad * 2;
        var rightContent = 24 + 14
            + StepHeight(rw, T("TurboTick_ConnectGuideStep1"))
            + StepHeight(rw, T("TurboTick_ConnectGuideStep2"))
            + StepHeight(rw, T("TurboTick_ConnectGuideStep3"))
            + 22 + ButtonHeight;
        return Math.Max(leftContent, rightContent) + Pad * 2;
    }

    /// <summary>The one page this client has. Left: what the player must fill in. Right: what the server is and
    /// the single secondary action (handing a colony over). Both cards are sized to their content and share the
    /// same height, so the two bottom actions line up - a form whose button floats mid-card reads as broken
    /// whenever the field list grows or shrinks, and a card stretched to the window height with two fields in it
    /// reads as a mostly-empty box.</summary>
    private void DrawConnection(Rect area)
    {
        var gap = 14f;
        var leftWidth = Math.Max(280f, area.width * 0.475f - gap / 2f);
        var rightWidth = area.width - leftWidth - gap;
        var settings = TurboTickMod.Settings;
        const float Pad = 22f;
        const float ButtonHeight = 42f;
        const float FieldHeight = 50f;   // 18 (label) + 4 (gap) + 28 (input)
        var cardHeight = Math.Min(ConnectionCardHeight(area.width), area.height);
        var left = new Rect(area.x, area.y, leftWidth, cardHeight);
        var right = new Rect(left.xMax + gap, area.y, rightWidth, cardHeight);

        // ---- left: connection form
        Panel(left);
        var x = left.x + Pad;
        var width = left.width - Pad * 2;
        SectionTitle(new Rect(x, left.y + Pad - 6, width, 24), T("TurboTick_ConnectStep"));
        var fieldTop = left.y + Pad + 28;
        settings.PlayerName = Field(new Rect(x, fieldTop, width, FieldHeight), T("TurboTick_Player"), settings.PlayerName);
        fieldTop += FieldHeight + 16;
        // Address and port share a row: a 5-digit port does not deserve a full line, and stacking it was what
        // made the form look like it had run out of content halfway down.
        var portWidth = Math.Min(96f, width * 0.3f);
        settings.ServerHost = Field(new Rect(x, fieldTop, width - portWidth - 10, FieldHeight), T("TurboTick_Host"), settings.ServerHost);
        settings.ServerPort = Field(new Rect(x + width - portWidth, fieldTop, portWidth, FieldHeight), T("TurboTick_Port"), settings.ServerPort);
        if (Button(new Rect(x, left.yMax - Pad - ButtonHeight, width, ButtonHeight), T("TurboTick_ConnectToServer"), true))
        {
            if (!MultiplayerHosting.Validate(settings, out error)) return;
            settings.Write();
            enteredRoom = true;
            RoomConnection.Connect(settings);
        }

        // ---- right: what the server is, then the secondary action
        Panel(right);
        var rx = right.x + Pad;
        var rw = right.width - Pad * 2;
        SectionTitle(new Rect(rx, right.y + Pad - 6, rw, 24), T("TurboTick_ConnectGuideTitle"));
        var stepTop = right.y + Pad + 28;
        stepTop = Step(rx, stepTop, rw, 1, T("TurboTick_ConnectGuideStep1"));
        stepTop = Step(rx, stepTop, rw, 2, T("TurboTick_ConnectGuideStep2"));
        Step(rx, stepTop, rw, 3, T("TurboTick_ConnectGuideStep3"));
        // Deploying a colony is an administrative action on the same connection, not a second way to
        // play: this client never simulates, it only uploads a save for the server to own.
        if (Button(new Rect(rx, right.yMax - Pad - ButtonHeight, rw, ButtonHeight), T("TurboTick_DeployColony"), false,
            RoomConnection.Busy || RoomConnection.IsHost))
        {
            if (!MultiplayerHosting.Validate(settings, out error)) return;
            settings.Write();
            enteredRoom = true;
            RoomConnection.Connect(settings, asHost: true);
        }
    }

    /// <summary>Height of one numbered step at the given width. Used by both the measuring pass and
    /// <see cref="Step"/>, so a two-line step cannot make the drawn text overlap the next numeral or push the
    /// card's button out of its rect.</summary>
    private static float StepHeight(float width, string text)
    {
        Text.Font = GameFont.Small;
        Text.WordWrap = true;
        return Math.Max(18f, Text.CalcHeight(text, width - 28)) + 12;
    }

    /// <summary>A card heading: small text, accent rule underneath. The rule is what separates a heading from
    /// body text at Small size - the old page relied on Medium for this, which made a whole sentence shout.</summary>
    private static void SectionTitle(Rect rect, string text)
    {
        Label(rect, text, GameFont.Small, Color.white);
        Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax + 4, 28, 2), Accent);
    }

    /// <summary>One numbered step in the right-hand card. The numeral gets its own square so the three lines
    /// read as a sequence instead of a paragraph - the old version was one Label holding `\n\n`-separated prose,
    /// which at this width wrapped into an unreadable block and pushed the second half out of its rect.</summary>
    private static float Step(float x, float y, float width, int number, string text)
    {
        var badge = new Rect(x, y + 1, 18, 18);
        Widgets.DrawBoxSolid(badge, new Color(Accent.r, Accent.g, Accent.b, 0.18f));
        var oldAnchor = Text.Anchor;
        Text.Anchor = TextAnchor.MiddleCenter;
        Label(badge, number.ToString(), GameFont.Tiny, Accent);
        Text.Anchor = oldAnchor;
        // Height comes from the same helper the measuring pass uses, so the two can never disagree.
        var body = new Rect(x + 28, y, width - 28, StepHeight(width, text) - 12);
        Label(body, text, GameFont.Small, Muted);
        return y + StepHeight(width, text);
    }

    private void DrawRoom(Rect area)
    {
        Panel(area);
        const float Pad = 22f;
        const float ButtonHeight = 38f;
        var x = area.x + Pad;
        var width = area.width - Pad * 2;
        var disconnected = !RoomConnection.Busy && RoomConnection.Client?.IsConnected != true;
        var buttonsY = area.yMax - Pad - ButtonHeight;
        Label(new Rect(x, area.y + Pad, width, 34), T(disconnected ? "TurboTick_RoomDisconnected"
            : MultiplayerHosting.Pending ? "TurboTick_PreparingRoom" : "TurboTick_RoomTitle"), GameFont.Medium, Accent);
        var top = area.y + Pad + 42;

        // Two layouts, not one layout with a hole in it.
        //
        // While disconnected there is no roster to show, and the old shape still reserved 44% of the width for it -
        // the result was a page whose only content lived in the left half of a wide, short box, which read as broken
        // (the player said so about this exact screen). Disconnected is also the state a player sees right after a
        // refusal, i.e. the state that carries the message they have to act on, so it gets the full width.
        var split = !disconnected;
        var detailWidth = split ? width * 0.56f : width;
        if (split)
            DrawRoster(new Rect(x + detailWidth + 22, area.y + Pad, width - detailWidth - 22, buttonsY - area.y - Pad - 14));
        DrawRoomDetail(new Rect(x, top, detailWidth, Math.Max(1f, buttonsY - top - 10f)), disconnected);

        if (Button(new Rect(x, buttonsY, width * 0.48f, ButtonHeight), T(disconnected ? "TurboTick_Connect" : "TurboTick_BackToGame"), true,
            disconnected || Current.ProgramState == ProgramState.Playing && !MultiplayerHosting.Pending))
        {
            if (disconnected) RoomConnection.ReconnectFromFault();
            else Close();
        }
        if (Button(new Rect(x + width * 0.52f, buttonsY, width * 0.48f, ButtonHeight), T("TurboTick_Disconnect")))
        {
            RoomConnection.Leave();
            enteredRoom = false;
            error = "";
        }
    }

    /// <summary>The room's own detail card: current status, where the room is, and what to do next.
    ///
    /// <para>No scroll view of its own. The window is measured to fit this card (<see cref="RoomPageHeight"/>), so a
    /// scrollbar here only ever appeared because the measurement was wrong - and it was: measured in game, the
    /// disconnected guide is two lines and it landed **on top of** the connect button because the stack had no lower
    /// bound. The fix is the measurement, not a scrollbar; the card is laid out to fill the rect it is given.</para>
    /// </summary>
    private void DrawRoomDetail(Rect area, bool disconnected)
    {
        var cardPadding = 16f;
        var statusText = RoomConnection.Status;
        var statusHeight = Math.Max(20f, Text.CalcHeight(statusText, area.width - cardPadding * 2 - 14f));
        var address = T("TurboTick_DedicatedRoomAddress", RoomConnection.Endpoint);
        var addressHeight = Math.Max(20f, Text.CalcHeight(address, area.width - cardPadding * 2));
        var guide = Guide(disconnected);
        var guideHeight = Math.Max(20f, Text.CalcHeight(guide, area.width - cardPadding * 2));
        var faulted = SharedColony.Session.Phase == Simulation.SharedPhase.Faulted;
        var fault = faulted ? T("TurboTick_SharedFault", SharedColony.Session.LastError) : "";
        var faultHeight = faulted ? Math.Max(20f, Text.CalcHeight(fault, area.width - cardPadding * 2 - 14f)) : 0f;

        // The card's height comes from the same measurements the drawing below uses. A hand-picked constant is what
        // makes a card end mid-sentence the first time a status wraps to three lines.
        var cardHeight = cardPadding + statusHeight + 14f + addressHeight + 12f + 32f + 14f + guideHeight + cardPadding
            + (faulted ? faultHeight + 12f : 0f);
        var card = new Rect(area.x, area.y, area.width, cardHeight);
        Widgets.DrawBoxSolid(card, new Color(1f, 1f, 1f, 0.028f));
        // A 3px bar in the status colour: this is the one line the player has to read, and colour is how the rest of
        // this mod marks severity (the roster rows, the verification table).
        var barColor = faulted ? new Color(1f, 0.55f, 0.45f) : disconnected ? Warn : Accent;
        Widgets.DrawBoxSolid(new Rect(card.x, card.y, 3f, card.height), barColor);

        var cx = card.x + cardPadding + 11f;
        var cwidth = card.width - cardPadding * 2 - 11f;
        var cy = card.y + cardPadding;
        Label(new Rect(cx, cy, cwidth, statusHeight), statusText, GameFont.Small, Color.white);
        cy += statusHeight + 14f;
        Label(new Rect(cx, cy, cwidth, addressHeight), address, GameFont.Small, Muted);
        cy += addressHeight + 12f;
        if (Button(new Rect(cx, cy, Math.Min(210f, cwidth), 32f), T("TurboTick_CopyPort"), false,
            RoomConnection.Endpoint.Length > 0))
            GUIUtility.systemCopyBuffer = RoomConnection.Endpoint;
        cy += 32f + 14f;
        Label(new Rect(cx, cy, cwidth, guideHeight), guide, GameFont.Small, Muted);
        cy += guideHeight;
        if (faulted)
        {
            cy += 12f;
            Label(new Rect(cx, cy, cwidth, faultHeight), fault, GameFont.Small, new Color(1f, 0.65f, 0.5f));
        }
    }

    /// <summary>The one-line advice under the room details, chosen from the same state the page is drawn in.</summary>
    private static string Guide(bool disconnected) => T(disconnected ? "TurboTick_RoomDisconnectedGuide"
        : RoomConnection.Busy ? "TurboTick_RoomConnecting"
        : MultiplayerHosting.Pending ? "TurboTick_HostPublishing"
        : RoomConnection.IsHost && RoomConnection.Client?.PresenceEpoch >= 0 ? "TurboTick_ServerWorldContinues"
        : "TurboTick_WaitForHost");

    /// <summary>Height of the room page's detail card at a given width. Same stack as
    /// <see cref="DrawRoomDetail"/> measures into, so the window can be tall enough that the common case never
    /// scrolls - a scrollbar on a five-line card is what made this page look unfinished.</summary>
    private static float DetailHeight(float width)
    {
        var saved = Text.Font;
        var wrap = Text.WordWrap;
        Text.Font = GameFont.Small;
        Text.WordWrap = true;
        const float CardPadding = 16f;
        var textWidth = Math.Max(1f, width - CardPadding * 2 - 11f);
        var status = Math.Max(20f, Text.CalcHeight(RoomConnection.Status, textWidth));
        var address = Math.Max(20f, Text.CalcHeight(T("TurboTick_DedicatedRoomAddress", RoomConnection.Endpoint), textWidth));
        var guide = Math.Max(20f, Text.CalcHeight(Guide(GuideDisconnected), textWidth));
        var faulted = SharedColony.Session.Phase == Simulation.SharedPhase.Faulted;
        var fault = faulted
            ? Math.Max(20f, Text.CalcHeight(T("TurboTick_SharedFault", SharedColony.Session.LastError), textWidth)) + 12f
            : 0f;
        Text.Font = saved;
        Text.WordWrap = wrap;
        return CardPadding + status + 14f + address + 12f + 32f + 14f + guide + CardPadding + fault;
    }

    private static bool GuideDisconnected => !RoomConnection.Busy && RoomConnection.Client?.IsConnected != true;

    /// <summary>Total height of the room page at the given body width: card + heading + button strip.
    ///
    /// <para>Decided by the same predicates the drawing uses, not by looking the window up in the stack:
    /// <see cref="InitialSize"/> runs while the window is being constructed, so a stack lookup would find nothing and
    /// the measurement would silently always be zero.</para></summary>
    private static float RoomPageHeight(float bodyWidth)
    {
        if (!ShowsRoomPage) return 0f;
        // Disconnected draws the card at full body width; connected splits it with the roster.
        var width = GuideDisconnected ? Math.Max(1f, bodyWidth) : Math.Max(1f, bodyWidth * 0.56f);
        const float Pad = 22f;
        const float Heading = 42f;
        const float ButtonHeight = 38f;
        // Pad + heading + card + gap above buttons + buttons + pad. The body rect is the page, so no outer inset
        // belongs here - `ChromeHeight` already accounts for the viewport's own margins.
        return Pad + Heading + DetailHeight(width) + 10f + ButtonHeight + Pad;
    }

    /// <summary>Height of the world-source page: the taller of its two columns plus padding.</summary>
    private static float HostPageHeight(float bodyWidth)
    {
        var gap = 14f;
        var leftWidth = Math.Max(300f, bodyWidth * 0.55f - gap / 2f);
        var rightWidth = bodyWidth - leftWidth - gap;
        // Left column: title rule + two 66px choice cards + a save list that takes whatever is left. Rather than
        // re-measure the list (it is a scroll view of its own and can paginate), give it a fixed comfortable share -
        // 5 rows - so the page is the same height whether the player has three saves or thirty.
        var left = 56f + 78f + 28f + 5f * 34f + 18f + 40f;
        var right = 56f + 3f * 78f + 40f;
        return Math.Max(left, right) + 40f;
    }

    /// <summary>Which page opens, in one place: <see cref="DoWindowContents"/>, <see cref="InitialSize"/> and the
    /// footer all have to agree or the window is sized for a page it is not showing.</summary>
    private static bool ShowsRoomPage =>
        !MultiplayerHosting.ReadyToChoose && (MultiplayerHosting.Pending || TurboState.Authority.SessionRequested);

    private void DrawRoster(Rect area)
    {
        var client = RoomConnection.Client;
        var players = client?.Players;
        SectionTitle(new Rect(area.x, area.y, area.width, 22), T("TurboTick_HudTitle", players?.Count ?? 0));
        if (players == null || players.Count == 0) return;
        var viewport = new Rect(area.x, area.y + 36, area.width, area.height - 36);
        const float RowHeight = 52f;
        var content = new Rect(0, 0, viewport.width - 18, players.Count * RowHeight);
        Widgets.BeginScrollView(viewport, ref rosterScroll, content);
        try
        {
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var row = new Rect(0, i * RowHeight, content.width, RowHeight - 6);
                var color = PlayerPresence.ColorFor(player.ColorIndex);
                Widgets.DrawBoxSolid(row, new Color(1, 1, 1, 0.035f));
                // A colour bar rather than a dot: at 3px it reads as the player's identity at a glance, and it is
                // the same 3px accent used by the status card and the section rules.
                Widgets.DrawBoxSolid(new Rect(row.x, row.y, 3, row.height), color);
                var name = PlayerPresence.DisplayName(player.Name) + (player.Id == client!.PlayerId ? " · " + T("TurboTick_HudYou") : "");
                Text.Font = GameFont.Small;
                GUI.color = color;
                Widgets.LabelEllipses(new Rect(row.x + 12, row.y + 3, row.width - 24, 24), name);
                // Everyone is a display replica here; the only difference worth showing is whether
                // the server accepts commands from this player.
                var role = T(player.CanEdit ? "TurboTick_HudCanCommand" : "TurboTick_HudViewOnly");
                var latency = player.LatencyMilliseconds < 0 ? "…" : T("TurboTick_HudLatency", player.LatencyMilliseconds);
                Label(new Rect(row.x + 12, row.y + 27, row.width - 24, 20), role + " · " + latency, GameFont.Tiny, Muted);
            }
        }
        finally { Widgets.EndScrollView(); GUI.color = Color.white; }
    }

    private void RefreshSaves()
    {
        try
        {
            saves.Clear();
            saves.AddRange(GenFilePaths.AllSavedGameFiles.Where(file =>
                !file.Name.StartsWith("TurboTickCheckpoint_", StringComparison.OrdinalIgnoreCase)
                && !IsSharedCache(file)).OrderByDescending(file => file.LastWriteTime));
        }
        catch (Exception failure) { error = failure.Message; }
    }

    private static bool IsSharedCache(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        return name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string Field(Rect rect, string label, string value)
    {
        // Label above, input under. Height is fixed at 56 so callers can advance by a constant; the label box is
        // 18 instead of 24 because `Tiny` line height is 14 - a 24px box left a 10px gap that read as a mistake.
        Label(new Rect(rect.x, rect.y, rect.width, 18), label, GameFont.Tiny, Muted);
        Text.Font = GameFont.Small;
        return Widgets.TextField(new Rect(rect.x, rect.y + 22, rect.width, 28), value);
    }

    private static void Panel(Rect rect) => Widgets.DrawBoxSolid(rect, Card);

    private static void Label(Rect rect, string text, GameFont font, Color color)
    {
        Text.Font = font;
        GUI.color = color;
        Widgets.Label(rect, text);
        GUI.color = Color.white;
    }

    private static bool Choice(Rect rect, string title, string description, bool active)
    {
        Widgets.DrawBoxSolid(rect, active ? new Color(0.12f, 0.23f, 0.24f) : new Color(0.115f, 0.14f, 0.165f));
        if (active) Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3, rect.height), Accent);
        if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
        Text.WordWrap = false;
        Text.Font = GameFont.Small;
        GUI.color = active ? Accent : Color.white;
        Widgets.LabelEllipses(new Rect(rect.x + 13, rect.y + 5, rect.width - 26, 24), title);
        Text.Font = GameFont.Tiny;
        GUI.color = Muted;
        Widgets.LabelEllipses(new Rect(rect.x + 13, rect.y + 30, rect.width - 26, rect.height - 30), description);
        GUI.color = Color.white;
        Text.WordWrap = true;
        return Widgets.ButtonInvisible(rect);
    }

    private static bool Button(Rect rect, string text, bool active = false, bool enabled = true)
    {
        Widgets.DrawBoxSolid(rect, !enabled ? new Color(0.12f, 0.14f, 0.16f) : active
            ? new Color(0.14f, 0.34f, 0.33f) : new Color(0.16f, 0.19f, 0.23f));
        if (enabled && Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
        Text.Anchor = TextAnchor.MiddleCenter;
        Label(rect, text, GameFont.Small, !enabled ? Muted : active ? Accent : Color.white);
        Text.Anchor = TextAnchor.UpperLeft;
        return enabled && Widgets.ButtonInvisible(rect);
    }
}
