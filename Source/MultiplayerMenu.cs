using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

// Scope the option insertion to the vanilla main menu, so unrelated option lists stay intact.
[HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
internal static class MultiplayerMenuScope
{
    internal static bool Drawing;
    private static void Prefix(ref Rect __0, out bool __state)
    {
        __state = Drawing;
        Drawing = true;
        __0.height += 45f;
    }
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        Drawing = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
internal static class MultiplayerMenuOption
{
    private static void Prefix(List<ListableOption> __1)
    {
        if (!MultiplayerMenuScope.Drawing) return;
        var label = T("TurboTick_Multiplayer");
        if (__1.Any(option => option.label == label)) return;
        int index;
        if (Current.ProgramState == ProgramState.Entry)
        {
            // The drawer also renders a second option list for web links. Only the primary play list
            // contains NewColony; inserting at zero when it is absent creates a second Multiplayer button.
            index = __1.FindIndex(option => option.label == "NewColony".Translate().ToString());
            if (index < 0) return;
            index++;
        }
        else
        {
            if (!__1.Any(option => option.label == "ReviewScenario".Translate().ToString()
                || option.label == "QuitToMainMenu".Translate().ToString()
                || option.label == "SaveAndQuitToMainMenu".Translate().ToString())) return;
            index = 0;
        }
        __1.Insert(index, new ListableOption(label, MultiplayerWindow.Open));
    }
}

// GameComponents do not exist on the title screen: joining and downloading must still progress there.
[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.UIRootUpdate))]
internal static class MultiplayerEntryUpdate
{
    private static void Postfix()
    {
        if (HeadlessRuntime.Active) return;
        RoomConnection.Pump();
        MultiplayerHosting.Update();
        // Developer aid: a configured server is joined straight from the title screen, so a display
        // replica needs no menu interaction to attach.
        var settings = TurboTickMod.Settings;
        if (settings is { autoConnect: true } && !TurboState.Authority.SessionRequested && !RoomConnection.Busy
            && RoomConnection.Client?.IsConnected != true)
            RoomConnection.Connect(settings);
        // `-turbotick-connect=HOST:PORT` was only honoured from `TurboGameComponent`, i.e. after a colony was already
        // loaded - so the one case the flag exists for, an unattended machine that must join the room without anyone
        // clicking the menu, could not use it. On a remote session the input desktop can be taken over entirely
        // (measured: `input desktop = Screen-saver`, which makes every `SendInput`/`mouse_event`/`PostMessage` a
        // no-op while `PrintWindow` still renders), so "click 多人游戏 → 连接专服并进入" is not available there.
        // `AutoConnectOnce` already parses the flag and is idempotent; it simply had no caller on the entry screen.
        if (ConnectArgPresent) RoomConnection.AutoConnectOnce();        // UI 截图用：`-turbotick-open-multiplayer` 让标题界面直接把窗口挂上去。没有这个开关，要看到
        // 多人游戏窗口就必须点菜单，而点菜单位置随分辨率/语言变化，自动化截图很难稳定命中。
        // 只在 Entry 阶段生效，且只在开关存在时——正式玩法不会走到这里。
        if (OpenOnEntry && Find.WindowStack is { } stack && stack.WindowOfType<MultiplayerWindow>() is null)
            MultiplayerWindow.Open();
        // 同一个理由，给模组校验表用：这个窗口本来只在握手被拒时出现，而制造一次拒绝要改本机 mod 列表。
        // 开关打开时用一份合成的房间清单渲染，截图即可核对列宽、判定的可读性与排序。
        if (ModCheckDemo && Find.WindowStack is { } demoStack && demoStack.WindowOfType<ModSyncDialog>() is null)
            OpenModCheckDemo(demoStack);
    }

    private static readonly bool OpenOnEntry = GenCommandLine.CommandLineArgPassed("turbotick-open-multiplayer");
    private static readonly bool ModCheckDemo = GenCommandLine.CommandLineArgPassed("turbotick-mod-check-demo");
    // NOT `CommandLineArgPassed`: that helper compares the whole argument for equality
    // (`Verse/GenCommandLine.cs:14` - `Compare(args[i], key) == 0`, or against `"-" + key`), so a key that carries a
    // value never matches. Measured: the client launched with `-turbotick-connect=192.168.0.29:30572` logged the
    // argument verbatim under "Command line arguments", and this flag stayed false, so nothing connected. The
    // value-carrying helper is `TryGetCommandLineArg`, which splits on '=' the same way `AutoConnectOnce` does.
    private static readonly bool ConnectArgPresent =
        GenCommandLine.TryGetCommandLineArg("turbotick-connect", out _);
    private static bool demoShown;

    /// <summary>Renders the verification table against a fabricated room list: every verdict class appears once, so a
    /// screenshot proves the layout handles all five without needing a real mismatch.</summary>
    private static void OpenModCheckDemo(WindowStack stack)
    {
        if (demoShown) return;
        demoShown = true;
        try
        {
            var local = ModManifestGame.Local();
            Log.Message("[TurboTick] 模组校验演示：本机 " + local.Count + " 个模组");
            // The room gets every local mod (so nothing is spuriously "extra"), two of them on a different build, and
            // one that exists only on the room side. That yields exactly one row per verdict class.
            var rows = new List<ModEntry>(local.Count + 1);
            for (var i = 0; i < local.Count; i++)
            {
                // One local mod is deliberately left out of the room so the "多余" class appears too.
                if (i == 3) continue;
                var entry = local[i];
                rows.Add(i switch
                {
                    0 => entry with { Version = entry.Version.Length == 0 ? "0.9.0" : entry.Version + "-room" },
                    1 => entry with { Version = entry.Version.Length == 0 ? "0.1.0" : entry.Version + "-old" },
                    _ => entry
                });
            }
            rows.Add(new ModEntry("demo.only.in.room", "Demo · Only In Room", local.Count, "1234567890", "1.0"));
            // The room's build has to be the real one, otherwise the demo reports "版本不一致" against itself and the
            // screenshot reads like a bug in the comparison rather than in the fixture.
            stack.Add(new ModSyncDialog(ModManifest.Serialize(rows), VersionControl.CurrentVersion.ToString()));
            Log.Message("[TurboTick] 模组校验演示窗口已加入窗口栈（" + rows.Count + " 行）");
        }
        catch (Exception error) { Log.Warning("[TurboTick] 模组校验演示窗口打开失败：" + error.Message); }
    }
}

[HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
internal static class MultiplayerReturnToMenu
{
    private static void Prefix()
    {
        if (!HeadlessRuntime.Active) RoomConnection.Leave();
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
internal static class MultiplayerShutdown
{
    private static void Prefix()
    {
        if (HeadlessRuntime.Active) HeadlessRuntime.Shutdown();
        else RoomConnection.Leave();
    }
}
