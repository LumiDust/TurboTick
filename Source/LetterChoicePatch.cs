using System;
using System.Collections.Generic;
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
/// the host's own copy answered the same letter. Suppressing it is not a UI gesture: the option's own
/// `RemoveLetter(this)` (e.g. `ChoiceLetter_AcceptJoiner.Choices`, `:661356`) is part of the action, so the letter has
/// to be removed here explicitly once the click has been reported.</para>
internal static class LetterChoiceState
{
    /// <summary>The options of the letter that was opened last, in the order the dialog shows them.</summary>
    private static readonly List<DiaOption> options = [];

    /// <summary>The letter those options belong to. A `DiaOption` carries no back-reference to its letter, so the
    /// letter is remembered at open time instead of searched for at click time.</summary>
    private static ChoiceLetter? activeLetter;

    internal static long ChoicesSent { get; private set; }
    internal static long ChoicesRefused { get; private set; }
    internal static string? LastRefusal { get; private set; }
    internal static int CapturedCount => options.Count;

    /// <summary>Rebuilt every time a letter is opened, so the index the player clicks is the index the host will read.
    /// The list is captured rather than recounted at click time because `Choices` is a lazy `IEnumerable`: enumerating
    /// it twice may yield different objects, and the dialog the player is clicking is the one built from this pass.</summary>
    internal static void Capture(ChoiceLetter letter)
    {
        activeLetter = letter;
        options.Clear();
        try
        {
            var count = 0;
            foreach (var option in letter.Choices)
            {
                options.Add(option);
                // A letter presents a handful; the bound stops a modded letter that yields forever from hanging the
                // draw. It matches `SharedActions.MaxLetterOptions`.
                if (++count >= 32) break;
            }
        }
        catch (Exception error)
        {
            options.Clear();
            Log.Warning("[TurboTick] 抉择信件选项枚举失败：" + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Reports the click, or returns false so vanilla runs it here when this peer is the authority.</summary>
    internal static bool TryReport(DiaOption option)
    {
        // On the host, and on a replica that is not frozen, the vanilla path is the correct one: the authority's own
        // click runs `action` locally and every replica learns the result through the state channels that follow.
        if (!SharedActions.PlayerActing) return false;
        if (!AuthoritativeReplica.Frozen) return false;
        var index = options.IndexOf(option);
        if (index < 0)
        {
            // A click on an option this peer did not capture - a link inside a hyperlink, or a list rebuilt between
            // draw and click. Reporting a guessed index would run a different option on the host, which is worse than
            // doing nothing visible.
            ChoicesRefused++;
            LastRefusal = "选项不在本帧捕获的列表里";
            return true;
        }
        var letter = activeLetter;
        if (letter is null || letter.ID == 0)
        {
            ChoicesRefused++;
            LastRefusal = "找不到选项所属的信件";
            return true;
        }
        SharedActions.PublishLetterChoice(letter.ID, index);
        ChoicesSent++;
        // The letter is answered as far as this peer is concerned: the host runs the option, and its own copy removes
        // the letter, whose removal reaches us with the next stack update. Removing it here too keeps the two stacks
        // in step without waiting for that frame and stops the player clicking twice.
        if (option.resolveTree) RemoveLetter(letter.ID);
        return true;
    }

    internal static void Refuse(string reason)
    {
        ChoicesRefused++;
        LastRefusal = reason;
    }

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
/// a `DiaNode` from `Choices` and pushes it onto the window stack. Capturing in the postfix means the captured order is
/// the order the dialog shows, for all eleven subclasses, without patching any of them.</para></summary>
[HarmonyPatch(typeof(ChoiceLetter), nameof(ChoiceLetter.OpenLetter))]
internal static class LetterOpenPatch
{
    private static void Postfix(ChoiceLetter __instance) => LetterChoiceState.Capture(__instance);
}

/// <summary>Intercepts the click. `Activate` is where every option of every letter ends up (`:195621-195636`), so a
/// prefix here sees the same delegate the host would run.
///
/// <para>Returning false skips the whole method for a replica: the click sound, the `OwningDialog.Close()` and the
/// option's own `action`. The sound is skipped on purpose — the host plays its own when it executes, and a replica
/// playing both would double it.</para></summary>
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
