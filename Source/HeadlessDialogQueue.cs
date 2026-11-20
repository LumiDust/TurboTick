using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Turns "a dialog is holding the world" into "a dialog is waiting for an answer".
///
/// <see cref="HeadlessWindowGuard"/> exists because a <c>Dialog_MessageBox</c> pauses the game unconditionally and
/// nothing in a headless process can dismiss it: measured, a real save sat at <c>serverTick=7</c>, <c>tps=0</c> for
/// a whole session while frames kept going out. Clearing <c>forcePause</c> fixed the freeze but created a worse
/// failure: the world runs on, the dialog stays on the stack forever, and whatever the mod was waiting for never
/// happens - a silent stuck workflow instead of a visible one.
///
/// This file keeps the pause on dialogs that actually ask something, and gives the operator a way to answer:
/// <list type="number">
/// <item>every window that wants to pause the game is registered with its title, text and button labels;</item>
/// <item>a dialog with no actions at all ("OK" only) is closed on the next frame and recorded - there was never a
/// decision in it, only an announcement;</item>
/// <item>a dialog with actions stays open and stays paused, because the mod's own state machine is blocked behind
/// that answer - that is what the mod author meant by pausing, and the operator can now see why;</item>
/// <item>an answer arrives through <see cref="PortalCommands"/> (<c>answer-dialog</c>), is applied on the game
/// thread by <see cref="Pump"/>, and executes exactly the callback the vanilla button would have executed.</item>
/// </list>
///
/// The button semantics are copied from <c>Verse.Dialog_MessageBox.DoWindowContents</c> (decompiled from
/// <c>tools/ref17-5005/Assembly-CSharp.dll</c>): A and B always close the window after their action, C closes only
/// when <c>buttonCClose</c> is set. A is also gated by <c>interactionDelay</c>, so an answer that arrives during the
/// delay waits for it rather than being silently dropped - the vanilla button is disabled at that moment and a
/// "decided" that never ran would be a lie.
/// </summary>
internal static class HeadlessDialogQueue
{
    internal sealed record Snapshot(int Id, string Type, string Title, string Text, string? ButtonA, string? ButtonB,
        string? ButtonC, bool Destructive, bool AutoClosed, string? Answer, int Repeats);

    private sealed class Pending
    {
        internal int Id;
        internal string Type = "";
        internal string Title = "";
        internal string Text = "";
        internal string? ButtonA;
        internal string? ButtonB;
        internal string? ButtonC;
        internal bool Destructive;
        internal bool AwaitingDecision;
        internal bool AutoClosed;
        internal string? Answer;
        internal int Repeats;
        internal DateTime OpenedUtc;
        internal Window? Window;
    }

    /// <summary>
    /// How many times the same dialog (same type and same text) may come back before it is answered automatically.
    /// A mod that re-opens its dialog in a tick loop would otherwise hold the colony forever with a fresh id every
    /// time, which is indistinguishable from a legitimate question unless identity is content-based.
    /// </summary>
    private const int MaximumRepeats = 3;

    /// <summary>Status files are polled every 2 s; a dialog body longer than this is noise, not information.</summary>
    private const int MaximumTextLength = 2000;

    private static readonly List<Pending> Items = [];
    private static readonly Dictionary<string, int> Signatures = new(StringComparer.Ordinal);
    private static readonly HashSet<int> Reported = [];
    private static int nextId = 1;

    /// <summary>
    /// True when unanswered dialogs should be answered with the safest button instead of waiting for a person.
    /// Off by default: a mod's question usually changes what the colony becomes, and guessing is worse than asking.
    /// </summary>
    private static bool autoAnswer;

    internal static void Configure(bool answerAutomatically) => autoAnswer = answerAutomatically;

    /// <summary>Dialogs still waiting for an answer; what the page must show before anything else.</summary>
    internal static bool AnyPending
    {
        get
        {
            foreach (var item in Items)
                if (item.AwaitingDecision && ReferenceEquals(item.Window, Find.WindowStack?.Windows.FirstOrDefault(w => ReferenceEquals(w, item.Window))))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// True for a window that must keep its <c>forcePause</c>: an unanswered question is a reason to hold the
    /// timeline, and hiding that reason was the bug this whole area already had once.
    /// </summary>
    internal static bool HoldsPause(Window? window)
    {
        if (window is null) return false;
        foreach (var item in Items)
            if (item.AwaitingDecision && ReferenceEquals(item.Window, window)) return true;
        return false;
    }

    /// <summary>Called from a Harmony postfix on <c>WindowStack.Add</c>, i.e. for every window any mod opens.</summary>
    internal static void Register(Window? window, string when)
    {
        if (window is null || !HeadlessRuntime.Active) return;
        var messageBox = window as Dialog_MessageBox;
        // A window that does not pause the game cannot be holding the timeline; it is also not something an
        // operator needs to answer, so it is not this file's business.
        if (!window.forcePause && messageBox is null) return;
        if (HoldsPause(window)) return;

        var text = messageBox?.text.RawText ?? "";
        var title = messageBox?.title ?? "";
        var type = window.GetType().FullName ?? window.GetType().Name;
        var signature = type + "\u0000" + title + "\u0000" + text;
        var repeats = Signatures.TryGetValue(signature, out var seen) ? ++seen : 1;
        Signatures[signature] = repeats;

        var pending = new Pending
        {
            Id = nextId++,
            Type = type,
            Title = title,
            Text = text.Length > MaximumTextLength ? text[..MaximumTextLength] + "…" : text,
            ButtonA = messageBox?.buttonAText,
            ButtonB = messageBox?.buttonBText,
            ButtonC = messageBox?.buttonCText,
            Destructive = messageBox?.buttonADestructive == true,
            Window = window,
            OpenedUtc = DateTime.UtcNow,
            Repeats = repeats
        };

        // No callback on any button and no accept/cancel handler: nothing to decide. This is the "your mod list
        // changed" class of announcement, and leaving it on the stack is how the colony got stuck before.
        pending.AutoClosed = messageBox is not null
            && messageBox.buttonAAction is null && messageBox.buttonBAction is null && messageBox.buttonCAction is null
            && messageBox.acceptAction is null && messageBox.cancelAction is null;
        pending.AwaitingDecision = !pending.AutoClosed;

        // A question that keeps coming back is a loop, not a question. Answering it is the only way out that does
        // not end with an operator staring at a frozen server and a dialog nobody can read.
        if (pending.AwaitingDecision && repeats > MaximumRepeats)
        {
            pending.Answer = SafeChoice(pending);
            Log.Warning("[TurboTick headless] 同一个对话框已第 " + repeats + " 次出现（" + type + "）："
                + "按「" + pending.Answer + "」自动回答，以免它无限次重新弹出。标题：" + OneLine(title));
        }

        Items.Add(pending);
        Log.Warning("[TurboTick headless] 对话框 " + type + "（" + when + "）标题：" + OneLine(title)
            + "；按钮 A=" + (pending.ButtonA ?? "-") + " B=" + (pending.ButtonB ?? "-") + " C=" + (pending.ButtonC ?? "-")
            + (pending.AutoClosed ? "；没有回调，仅通知，将自动关闭" : "；等待管理页选择"));
        // Trim history so a long session cannot grow this list forever; only live windows matter.
        if (Items.Count > 64) Items.RemoveAll(item => !IsLive(item));
    }

    private static string SafeChoice(Pending pending)
    {
        // B is the cancel/go-back slot in every vanilla CreateConfirmation, so it is the choice that changes the
        // least. Only when a dialog has no B at all does A become the least surprising answer.
        if (!string.IsNullOrEmpty(pending.ButtonB)) return "B";
        if (pending.Window is Dialog_MessageBox { cancelAction: not null }) return "cancel";
        return "A";
    }

    private static bool IsLive(Pending item) =>
        item.Window is { } window && Find.WindowStack is { } stack && stack.IsOpen(window);

    /// <summary>
    /// Applies queued answers and closes announcements. Runs once per frame from <c>HeadlessRuntime.Update</c>, on
    /// the game thread, because every callback below belongs to the mod that created the dialog.
    /// </summary>
    internal static void Pump()
    {
        if (!HeadlessRuntime.Active || Items.Count == 0) return;
        var stack = Find.WindowStack;
        if (stack is null) return;

        for (var index = Items.Count - 1; index >= 0; index--)
        {
            var item = Items[index];
            if (item.Window is not { } window)
            {
                Items.RemoveAt(index);
                continue;
            }
            if (!stack.IsOpen(window))
            {
                // The mod closed it itself (or its owner went away). Keep the record, drop the reference so the
                // page stops asking about a window that no longer exists.
                item.Window = null;
                if (item.Answer is null && item.AwaitingDecision && item.AutoClosed == false)
                    Log.Message("[TurboTick headless] 对话框 " + item.Type + " 已被游戏自己关闭（不是管理页回答的）");
                continue;
            }

            var answer = item.Answer ?? (item.AutoClosed || autoAnswer && item.AwaitingDecision ? SafeChoice(item) : null);
            if (answer is null) continue;

            if (item.Answer is null && item.AutoClosed) item.Answer = "closed";
            if (!Apply(item, window, answer)) continue;
            item.Answer = answer;
            item.AwaitingDecision = false;
            item.Window = null;
            Log.Warning("[TurboTick headless] 对话框 " + item.Type + " 已按「" + answer + "」处理（标题："
                + OneLine(item.Title) + "）");
        }
    }

    /// <summary>
    /// Executes one answer. Returns false when the window is not ready for it yet (vanilla disables A for
    /// <c>interactionDelay</c> seconds), so the answer stays queued instead of being counted as delivered.
    /// </summary>
    private static bool Apply(Pending item, Window window, string answer)
    {
        if (window is not Dialog_MessageBox box)
        {
            // Not a message box: there is no labelled button to press, only a window to dismiss. Answering it is
            // still the operator's decision, which is why it does not happen automatically.
            try { window.Close(); }
            catch (Exception error)
            {
                Log.Warning("[TurboTick headless] 关闭对话框 " + item.Type + " 失败：" + error.GetType().Name + ": " + error.Message);
            }
            return true;
        }

        // `RealTime.LastRealTime`, not `UnityEngine.Time.realtimeSinceStartup`: it is the same quantity (that is
        // literally what `RealTime.Update` assigns), `Dialog_MessageBox` stamps `creationRealTime` from it, and
        // `UnityEngine.Time` is not part of this assembly's reference set in 1.7.
        var opened = AccessTools.FieldRefAccess<Dialog_MessageBox, float>("creationRealTime")(box);
        if (answer == "A" && RealTime.LastRealTime - opened < box.interactionDelay) return false;

        void Run(Action? action, bool close)
        {
            try { action?.Invoke(); }
            catch (Exception error)
            {
                Log.Error("[TurboTick headless] 对话框回调（" + item.Type + "）抛出 " + error.GetType().Name + ": " + error.Message);
            }
            if (close) box.Close();
        }

        switch (answer)
        {
            case "A":
                Run(box.buttonAAction, true);
                return true;
            case "B":
                if (string.IsNullOrEmpty(box.buttonBText)) return false;
                Run(box.buttonBAction, true);
                return true;
            case "C":
                if (string.IsNullOrEmpty(box.buttonCText)) return false;
                Run(box.buttonCAction, box.buttonCClose);
                return true;
            case "accept":
                try { box.OnAcceptKeyPressed(); }
                catch (Exception error) { Log.Warning("[TurboTick headless] accept 失败：" + error.Message); }
                return true;
            case "cancel":
                try { box.OnCancelKeyPressed(); }
                catch (Exception error) { Log.Warning("[TurboTick headless] cancel 失败：" + error.Message); }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Queues an operator answer. Returns a sentence for the command reply either way.</summary>
    internal static bool Answer(int id, string choice, out string detail)
    {
        choice = choice.Trim();
        if (choice.ToLowerInvariant() is "a" or "b" or "c") choice = choice.ToUpperInvariant();
        else if (choice.ToLowerInvariant() is not ("accept" or "cancel"))
        {
            detail = "可选项是 A / B / C / accept / cancel，收到：" + choice;
            return false;
        }
        foreach (var item in Items)
        {
            if (item.Id != id) continue;
            if (!IsLive(item))
            {
                detail = "对话框 " + id + " 已经不在了（游戏自己关掉或世界已换）";
                return false;
            }
            if (item.Answer is not null)
            {
                detail = "对话框 " + id + " 已经按「" + item.Answer + "」处理过";
                return false;
            }
            item.Answer = choice;
            item.AwaitingDecision = false;
            detail = "已记录选择「" + choice + "」，将在下一帧执行（标题：" + OneLine(item.Title) + "）";
            return true;
        }
        detail = "没有编号为 " + id + " 的对话框；当前待决：" + (Items.Count == 0 ? "无" : string.Join("，", Items.Select(item => item.Id)));
        return false;
    }

    internal static Snapshot[] Snapshots()
    {
        if (!HeadlessRuntime.Active || Items.Count == 0) return [];
        var live = new List<Snapshot>();
        foreach (var item in Items)
        {
            if (item.Window is null && item.Answer is null) continue;
            live.Add(new Snapshot(item.Id, item.Type, item.Title, item.Text, item.ButtonA, item.ButtonB, item.ButtonC,
                item.Destructive, item.AutoClosed, item.Answer, item.Repeats));
        }
        return [.. live];
    }

    /// <summary>Cleared on world change: a dialog belongs to the world that opened it.</summary>
    internal static void Reset()
    {
        Items.Clear();
        Signatures.Clear();
        Reported.Clear();
        nextId = 1;
    }

    private static string OneLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(无)";
        var flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    internal static void NoteReported(int id) => Reported.Add(id);
}
