using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Makes a choice letter answerable on a replica: the click travels to the host instead of running here.
///
/// <para><b>The defect this closes.</b> `LetterSync.Apply` used to rebuild every `ChoiceLetter` as a `StandardLetter`
/// and log 「选项请到主机端操作」. Eleven letter types inherit `ChoiceLetter` and the player is expected to pick from
/// them; `ChoiceLetter_GrowthMoment` alone writes `skill.passion`, a trait, a sexuality trait and two age-tracker
/// fields inside `MakeChoices` (`_lab/asm/Assembly-CSharp.decompiled.cs:662198-662265`). Being shown a read-only copy
/// of a decision you are supposed to make is not a smaller feature — it is a different game.</para>
///
/// <para><b>Why the letter itself now travels whole.</b> `ReplicaLetter.Fragment` carries the host's own
/// `ExposeData` output, and `LetterMaker.MakeLetter(LetterDef)` (`:182441`) instantiates `def.letterClass`, so the
/// replica builds the *right* subclass and loads the host's options into it. The player therefore chooses from
/// exactly the options the host generated — options the host paid `Rand` to produce — instead of a re-run of the same
/// rolls, which would give different options because the host's `Rand` state has moved on.</para>
///
/// <para><b>Why one patch point for eleven letters.</b> Every subclass overrides the same abstract <c>Choices</c>
/// property (`:182745`), every letter opens through `ChoiceLetter.OpenLetter` (`:182872-182878`), and each option's
/// effect is its `DiaOption.action` (`:195541`), invoked from `Activate` (`:195621-195636`). Patching those three
/// covers every option of every letter type, and the index the replica sends is read back off the same enumeration on
/// the host — the shape the designator route already uses, where one patch on the base class covers 54 designators.</para>
///
/// <para><b>Why the click must be suppressed locally.</b> `MakeChoices` writes the pawn's skills, traits and growth
/// points. Letting it run on the replica would make that peer a second writer, and the two would disagree the moment
/// the host's own copy answered the same letter.</para>
///
/// <para><b>Why the capture reads the dialog instead of `Choices`.</b> `Choices` is a lazy iterator: every
/// enumeration constructs new `DiaOption` objects (`_lab/asm/Assembly-CSharp.decompiled.cs:182440-182460` style
/// `yield return` bodies). `OpenLetter` enumerates it once to fill the `DiaNode` the window draws, so a second
/// enumeration from a postfix yields *different* objects and `IndexOf(clicked)` is always -1. The options that are
/// actually on screen are `dialog.curNode.options`, and that list is what this class captures.</para>
///
/// <para><b>Why an unrelated dialog is left alone.</b> A frozen replica still shows dialogs that are not letters
/// (ritual confirmations, mod dialogs, the game's own node trees). A `DiaOption` is matched to a letter only through
/// the `dialog` field the real `Dialog_NodeTree.GotoNode` stamps onto it; an option that belongs to no captured letter
/// dialog falls through to vanilla, which used to be swallowed for every option in the game.</para>
internal static class LetterChoiceState
{
    /// <summary>One letter dialog that is actually on screen: the letter, the window, and the option list the window
    /// drew. The list identity is what makes the reported index the index the host will read.</summary>
    private sealed class OpenLetterDialog
    {
        internal ChoiceLetter Letter = null!;
        internal Dialog_NodeTree Dialog = null!;
        internal List<DiaOption> Options = null!;
    }

    /// <summary>Bounded because a letter dialog is normally opened once and then answered; the oldest entry is
    /// dropped first, and a stale entry can never match because matching is by window instance.</summary>
    private const int MaxOpenDialogs = 8;

    private static readonly List<OpenLetterDialog> openDialogs = [];
    private static readonly FieldInfo? curNodeField = AccessTools.Field(typeof(Dialog_NodeTree), "curNode");

    internal static long ChoicesSent { get; private set; }
    internal static long ChoicesRefused { get; private set; }
    internal static string? LastRefusal { get; private set; }
    internal static int CapturedCount => openDialogs.Count == 0 ? 0 : openDialogs[^1].Options.Count;

    /// <summary>Binds the letter to the window `OpenLetter` has just pushed and captures the options that window
    /// actually draws. Called from the `OpenLetter` postfix, after `Find.WindowStack.Add(window)`.
    ///
    /// <para>`WindowStack.Add` appends, and its `RemoveWindowsOfType(window.GetType())` has already removed a previous
    /// letter window of the same concrete type, so the last window on the stack is the one just built. Nothing is
    /// re-enumerated: `curNode.options` is the list the draw loop iterates.</para></summary>
    internal static void Capture(ChoiceLetter letter, WindowStack? stack)
    {
        openDialogs.RemoveAll(entry => ReferenceEquals(entry.Letter, letter));
        if (stack is null || stack.Count == 0) { Refuse("信件窗口没有进 WindowStack"); return; }
        var window = stack[stack.Count - 1];
        if (window is not Dialog_NodeTree dialog || curNodeField is null)
        {
            Refuse("打开的窗口不是 Dialog_NodeTree");
            return;
        }
        if (curNodeField.GetValue(dialog) is not DiaNode node)
        {
            Refuse("信件对话框没有当前节点");
            return;
        }
        openDialogs.Add(new OpenLetterDialog { Letter = letter, Dialog = dialog, Options = node.options });
        while (openDialogs.Count > MaxOpenDialogs) openDialogs.RemoveAt(0);
    }

    internal static void Capture(ChoiceLetter letter) => Capture(letter, Find.WindowStack);

    /// <summary>Reports the click, or returns false so vanilla runs it here.
    ///
    /// <para>Returns false — the vanilla path, which is correct — in every case that is not a letter decision on this
    /// frozen replica: the host, an unfrozen replica, an option belonging to another dialog, a link option, and an
    /// option that is not in the dialog's top-level list. Returning false is what restores unrelated dialogs.</para>
    ///
    /// <para>On a reported click the action is <b>not</b> run locally, and the letter is <b>not</b> removed locally:
    /// the host runs the option (including its own `RemoveLetter`) and the letter disappears from this stack when the
    /// host's stack update arrives. `resolveTree` still closes the dialog here, because that is pure UI — otherwise
    /// the modal window would keep input captured while the player waits for the host. `Option_Postpone` is exactly
    /// this shape (`resolveTree = true`, no action) and must leave the letter in the list.</para></summary>
    internal static bool TryReport(DiaOption option)
    {
        if (!SharedActions.PlayerActing) return false;
        if (!AuthoritativeReplica.Frozen) return false;
        var entry = DialogFor(option);
        if (entry is null) return false; // not the letter dialog that is on screen: vanilla handles it

        // `link` / `linkLateBind` navigate the local dialog tree (`DiaOption.Activate`, `:195632-195635`). They are
        // not decisions, so they are not reported; the local navigation runs and the residual is recorded.
        if (option.link != null || option.linkLateBind != null)
        {
            Refuse("导航选项留给本地处理");
            return false;
        }

        var index = entry.Options.IndexOf(option);
        if (index < 0)
        {
            // A sub-node option after navigation, or an option drawn from a list that is not this letter's top level.
            // Guessing an index would run a different option on the host, which is worse than local behaviour.
            Refuse("选项不属于该信件打开的顶层选项");
            return false;
        }

        var letter = entry.Letter;
        if (letter.ID == 0)
        {
            Refuse("找不到选项所属的信件");
            return false;
        }

        SharedActions.PublishLetterChoice(letter.ID, index);
        ChoicesSent++;
        if (option.resolveTree && option.dialog != null) option.dialog.Close();
        return true;
    }

    private static OpenLetterDialog? DialogFor(DiaOption option)
    {
        if (option.dialog is not Dialog_NodeTree dialog) return null;
        for (var i = openDialogs.Count - 1; i >= 0; i--)
            if (ReferenceEquals(openDialogs[i].Dialog, dialog)) return openDialogs[i];
        return null;
    }

    internal static void Refuse(string reason)
    {
        ChoicesRefused++;
        LastRefusal = reason;
    }

    /// <summary>Growth-moment letters answer through `MakeChoices`, not through a `DiaOption`; its reporter still
    /// removes the local copy so the button cannot be pressed twice. Kept as an entry point for that patch.</summary>
    internal static void Remove(int letterId) => RemoveLetter(letterId);

    private static void RemoveLetter(int letterId)
    {
        var stack = Find.LetterStack;
        if (stack is null) return;
        var live = stack.LettersListForReading;
        for (var i = 0; i < live.Count; i++)
            if (live[i] is { } each && each.ID == letterId) { stack.RemoveLetter(each); return; }
    }

    internal static string Summary() =>
        $"抉择上送 {ChoicesSent} / 拒绝 {ChoicesRefused}"
        + (LastRefusal is null ? "" : $"（最近：{LastRefusal}）");
}

/// <summary>Captures the option list at the moment the letter is opened.
///
/// <para>`ChoiceLetter.OpenLetter` (`:182872-182878`) is the one code path every `ChoiceLetter` goes through: it builds
/// a `DiaNode` from `Choices`, wraps it in `Dialog_NodeTreeWithFactionInfo`, and pushes that window. Capturing in the
/// postfix reads the options the window really holds, for all eleven subclasses, without patching any of them and
/// without enumerating the lazy `Choices` a second time.</para></summary>
[HarmonyPatch(typeof(ChoiceLetter), nameof(ChoiceLetter.OpenLetter))]
internal static class LetterOpenPatch
{
    private static void Postfix(ChoiceLetter __instance) => LetterChoiceState.Capture(__instance, Find.WindowStack);
}

/// <summary>Intercepts the click. `Activate` is where every option of every letter ends up (`:195621-195636`), so a
/// prefix here sees the same delegate the host would run.
///
/// <para>Returning false skips the whole method for a replica: the click sound, the `OwningDialog.Close()` and the
/// option's own `action`. The sound is skipped on purpose — the host plays its own when it executes, and a replica
/// playing both would double it. The dialog close is re-issued by `LetterChoiceState.TryReport` for a reported click;
/// every other option returns false and keeps vanilla's close.</para></summary>
[HarmonyPatch(typeof(DiaOption), "Activate")]
internal static class LetterOptionActivatePatch
{
    private static bool Prefix(DiaOption __instance) => !LetterChoiceState.TryReport(__instance);
}

/// <summary>The growth-moment letter, which is <b>not</b> a `ChoiceLetter`.
///
/// <para>`ChoiceLetter_GrowthMoment` derives from `LetterWithTimeout` (`_lab/asm/Assembly-CSharp.decompiled.cs:662067`)
/// and answers through `MakeChoices(List&lt;SkillDef&gt;, Trait)` (`:662198`) called from the OK button of its own dialog
/// (`:662457`). None of the three patches above reaches it: it has no `Choices` property, so `OpenLetter` yields
/// nothing to capture, and its dialog button is a `Widgets.ButtonText`, not a `DiaOption`. The first design put every
/// patch point on `ChoiceLetter` and therefore missed exactly the letter a player is most likely to face.</para>
///
/// <para>What the effect is: `MakeChoices` increments each chosen `SkillRecord.passion`, keeps `Gene.passionPreAdd` in
/// step, applies the trait's own skill gains, rolls a sexuality trait at 13, and clears `growthPoints` /
/// `canGainGrowthPoints`. All of that happens on the authority, so the replica only has to report the choice.</para>
/// </summary>
[HarmonyPatch(typeof(ChoiceLetter_GrowthMoment), nameof(ChoiceLetter_GrowthMoment.MakeChoices))]
internal static class GrowthMomentChoicePatch
{
    private static bool Prefix(ChoiceLetter_GrowthMoment __instance, List<SkillDef> skills, Trait trait)
    {
        if (!SharedActions.PlayerActing) return true;
        if (!AuthoritativeReplica.Frozen) return true;
        if (__instance.ID == 0) { LetterChoiceState.Refuse("成长时刻信件没有 id"); return true; }
        var names = new List<string>();
        foreach (var skill in skills)
            if (skill?.defName is { Length: > 0 } name) names.Add(name);
        // `NoTrait` is a `Trait` with no def — the letter's own sentinel for "the player took no trait"
        // (`:662071`, offered 35% of the time at `:662277-662281`). It travels as an empty trait section rather than as
        // a def name, because there is no def to name.
        var traitDef = trait?.def?.defName;
        var degree = trait?.Degree ?? 0;
        SharedActions.PublishGrowthChoice(__instance.ID, names, traitDef, degree);
        // The letter is removed here as well: the host removes its own copy after running `MakeChoices`, and this peer
        // would otherwise keep a button that does nothing the second time.
        LetterChoiceState.Remove(__instance.ID);
        return false;
    }
}
