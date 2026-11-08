using UnityEngine;
using Verse;

namespace TurboTick;

public class TurboTickSettings : ModSettings
{
    // Connection/presentation preferences are local. Enabled/multiplier/cap are local defaults only;
    // once a room is requested every simulation setting must be confirmed by its server.
    public bool enabled = true;
    public float multiplier = 2f;
    public float maxRateCap = 60f;
    public bool showHud = true;
    public bool showPlayerCursors = true;
    /// <summary>Detected divergence: reconnect and catch up from the server's checkpoint automatically.</summary>
    public bool autoRecover = true;
    /// <summary>Detected divergence: pause and ask instead of silently rewinding the colony.
    ///
    /// Rewinding loses everything the players did since the checkpoint, and it also hides which field
    /// actually diverged. Pausing first means the world is still there to look at, and the recovery is a
    /// deliberate choice. MU pauses on desync by default for the same two reasons.</summary>
    public bool pauseOnDesync = true;
    /// <summary>Transport loading sync - unverified in game, therefore off by default.</summary>
    public bool experimentalInteractions = false;
    /// <summary>Trading is normally limited to the host; enabling this leaves it ungated and unsynced.</summary>
    public bool everyoneMayTrade = false;
    public string ServerHost = "127.0.0.1";
    public string ServerPort = "30572";
    public string PlayerName = "Player";
    /// <summary>Developer aid: join the configured server as soon as a colony is loaded.</summary>
    public bool autoConnect = false;
    /// <summary>Remembered HUD corner. Negative means "use the default anchor".</summary>
    public float hudX = -1f;
    public float hudY = -1f;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref enabled, "enabled", true);
        Scribe_Values.Look(ref multiplier, "multiplier", 2f);
        Scribe_Values.Look(ref maxRateCap, "maxRateCap", 60f);
        Scribe_Values.Look(ref showHud, "showHud", true);
        Scribe_Values.Look(ref showPlayerCursors, "showPlayerCursors", true);
        Scribe_Values.Look(ref autoRecover, "autoRecover", true);
        Scribe_Values.Look(ref pauseOnDesync, "pauseOnDesync", true);
        Scribe_Values.Look(ref experimentalInteractions, "experimentalInteractions", false);
        Scribe_Values.Look(ref everyoneMayTrade, "everyoneMayTrade", false);
        Scribe_Values.Look(ref ServerHost, "serverHost", "127.0.0.1");
        Scribe_Values.Look(ref ServerPort, "serverPort", "30572");
        Scribe_Values.Look(ref PlayerName, "playerName", "Player");
        Scribe_Values.Look(ref autoConnect, "autoConnect", false);
        Scribe_Values.Look(ref hudX, "hudX", -1f);
        Scribe_Values.Look(ref hudY, "hudY", -1f);
    }

    public void Draw(Rect area)
    {
        var listing = new Listing_Standard();
        listing.Begin(area);
        var entries = listing.GetRect(34f);
        var entryWidth = (entries.width - 12f) / 2f;
        if (Widgets.ButtonText(new Rect(entries.x, entries.y, entryWidth, 32f), T("TurboTick_OpenLobby"))) MultiplayerWindow.Open();
        if (Widgets.ButtonText(new Rect(entries.x + entryWidth + 12f, entries.y, entryWidth, 32f), T("TurboTick_ManageServer"))) ServerAdminWindow.Open();
        listing.Label(T("TurboTick_Prototype"), 48f);
        ServerHost = Field(listing, T("TurboTick_Host"), ServerHost);
        ServerPort = Field(listing, T("TurboTick_Port"), ServerPort);
        PlayerName = Field(listing, T("TurboTick_Player"), PlayerName);
        var buttons = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, 150f, 28f), T("TurboTick_Connect"), true, true, !RoomConnection.Busy))
            RoomConnection.Connect(this);
        if (Widgets.ButtonText(new Rect(buttons.x + 160f, buttons.y, 150f, 28f), T("TurboTick_Disconnect")))
            RoomConnection.Leave();
        listing.Label(RoomConnection.Status, 44f);
        listing.Label(T("TurboTick_Fingerprint", RoomConnection.Fingerprint), 38f);
        listing.Gap(6f);

        listing.Label(T("TurboTick_NativeControls"), 54f);
        listing.CheckboxLabeled(T("TurboTick_ShowHud"), ref showHud, T("TurboTick_ShowHudDesc"));
        listing.CheckboxLabeled(T("TurboTick_ShowPlayerCursors"), ref showPlayerCursors, T("TurboTick_ShowPlayerCursorsDesc"));
        listing.CheckboxLabeled(T("TurboTick_AutoRecover"), ref autoRecover, T("TurboTick_AutoRecoverDesc"));
        listing.CheckboxLabeled(T("TurboTick_PauseOnDesync"), ref pauseOnDesync, T("TurboTick_PauseOnDesyncDesc"));
        listing.CheckboxLabeled(T("TurboTick_Experimental"), ref experimentalInteractions, T("TurboTick_ExperimentalDesc"));
        listing.CheckboxLabeled(T("TurboTick_EveryoneTrades"), ref everyoneMayTrade, T("TurboTick_EveryoneTradesDesc"));
        listing.Gap(6f);
        var shared = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(shared.x, shared.y, 320f, 28f), T("TurboTick_StartShared"), true, true, RoomConnection.IsHost && RoomConnection.Client?.PresenceEpoch == -1 && Current.ProgramState == ProgramState.Playing))
            RoomConnection.StartSharedColony();
        listing.Gap(6f);
        var cache = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(cache.x, cache.y, 320f, 28f), T("TurboTick_CacheSnapshot"), true, true, TurboState.Authority.SessionConnected))
            RoomConnection.CacheSnapshot();
        listing.Label(RoomConnection.SnapshotStatus, 42f);
        listing.Gap(6f);
        listing.Label(T("TurboTick_Diagnostics"), 24f);
        listing.Label(Diagnostics.Build(), 150f);
        listing.End();
    }

    private static string Field(Listing_Standard listing, string label, string current)
    {
        var rect = listing.GetRect(28f);
        Widgets.Label(new Rect(rect.x, rect.y, 110f, 26f), label);
        return Widgets.TextField(new Rect(rect.x + 116f, rect.y, Math.Max(150f, rect.width - 116f), 26f), current);
    }

    internal static string T(string key, params object[] args)
    {
        string text = key.Translate().ToString();
        return args.Length == 0 ? text : string.Format(text, args);
    }
}
