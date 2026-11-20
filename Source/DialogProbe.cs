using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Opens a real <see cref="Dialog_MessageBox"/> with two buttons so the dialog queue can be measured end to end.
///
/// Off unless <c>TURBOTICK_PROBE_DIALOG</c> is set to the tick to fire on. It has to be a real dialog from the real
/// class, not a stand-in: the whole point of <see cref="HeadlessDialogQueue"/> is that it distinguishes "a question
/// with a choice" from "an announcement with only OK", and that distinction is read off the fields
/// <c>Dialog_MessageBox</c>'s own constructor sets. A fake window would test the queue against my assumptions
/// instead of against the game.
///
/// The buttons do something observable rather than something plausible: A and B leave a line in the log, and A is
/// marked destructive so the page's styling for "this is the irreversible one" is exercised too. B is the answer the
/// probe expects to be chosen (it is the safe one), so the log order proves which callback ran.
/// </summary>
internal static class DialogProbe
{
    private static readonly int FireAt = int.TryParse(
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_DIALOG"), NumberStyles.None, CultureInfo.InvariantCulture,
        out var tick) ? tick : -1;

    /// <summary>Set to 1 to have the probe open a dialog with no callbacks at all, i.e. an announcement.</summary>
    private static readonly bool AnnouncementOnly =
        Environment.GetEnvironmentVariable("TURBOTICK_PROBE_DIALOG_ANNOUNCE") == "1";

    private static bool fired;

    internal static void Tick()
    {
        if (FireAt < 0 || fired) return;
        // `Find.TickManager` is `Current.Game.tickManager` and throws while the title screen is up - the same trap
        // that made WriteStatus log a NullReferenceException every frame. Checked before every read here.
        if (Current.Game is null || Current.ProgramState != ProgramState.Playing) return;
        var manager = Find.TickManager;
        if (manager is null || manager.TicksGame < FireAt) return;
        fired = true;
        try { Open(manager.TicksGame); }
        catch (Exception error) { Log.Error("[TurboTick] 对话框探针失败：" + error); }
    }

    private static void Open(int tick)
    {
        if (AnnouncementOnly)
        {
            // No callbacks at all: Dialog_MessageBox turns that into a single "OK" and the queue is supposed to
            // close it on its own. If it instead waits for a human, the operator is being asked nothing.
            Find.WindowStack.Add(new Dialog_MessageBox("探针公告（没有回调，应当被自动关掉）：tick " + tick));
            Log.Message("[TurboTick] 对话框探针：已弹出公告型对话框（tick " + tick + "）");
            return;
        }

        var box = new Dialog_MessageBox(
            "探针对话框：这个窗口带着真正的按钮回调，无头进程里没有人能点它，世界应当停在这里等选择。"
            + "\n选择「保留」会写一行 PROBE-B，选择「销毁」会写一行 PROBE-A。",
            "销毁（A）",
            () => Log.Warning("[TurboTick] 对话框探针：收到了 PROBE-A（破坏性按钮）"),
            "保留（B）",
            () => Log.Warning("[TurboTick] 对话框探针：收到了 PROBE-B（安全按钮）"),
            "对话框探针 tick " + tick,
            buttonADestructive: true);
        Find.WindowStack.Add(box);
        Log.Warning("[TurboTick] 对话框探针：已弹出待决对话框（tick " + tick + "），在管理页回答后世界才会继续");
    }
}
