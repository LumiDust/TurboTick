using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Skills, which a replica freezes at whatever level the save was loaded with.
///
/// <para><b>Why this is visible.</b> <c>SkillRecord.levelInt</c> and <c>xpSinceLastLevel</c> are written by
/// <c>Pawn_SkillTracker.Learn</c>, which is called from work toils - hauling, building, cooking, shooting. A display
/// replica runs none of them, so every colonist's skill page shows the level they had when the session started: a
/// colonist who has been mining for a week is still level 3, the work tab's "average relevant skill" never moves, and the
/// little progress bar under each skill never fills. Skills also gate work: <c>WorkGiver</c>s check
/// <c>TotallyDisabled</c> and the level, so a replica's own copy of a pawn would refuse work the host's pawn has since
/// become able to do.</para>
///
/// <para><b>Why the level and the progress fraction, not the raw xp.</b> The number the player reads is the level, and the
/// bar under it is <c>XpProgressPercent</c> - a fraction of whatever the next level costs, which changes with the level.
/// Sending raw xp would mean the replica has to re-derive the threshold from its own (possibly stale) level and would show
/// a bar that jumps whenever the two disagreed.</para>
///
/// <para><b>Why one string per pawn rather than a list of records.</b> Nineteen skills times a record each is six hundred
/// bytes of JSON framing per pawn per frame on a five-pawn colony, for values that change a few times an hour. Encoded as
/// <c>defName=level:percent:passion</c> joined by <c>;</c> it is about a hundred and eighty bytes, and the replica decodes
/// by split. This is the same shape the thoughts and hediff channels already use.</para>
internal static class SkillSync
{
    /// <summary>Checks that the wire mirror in `Networking` still describes the game's own <c>Passion</c>.
    ///
    /// <para>Same reasoning as the guest status mirror: the frame carries the passion as an integer and the test project can
    /// only see the mirror, so a game update that reordered <c>Passion</c> would keep every test green while every burning
    /// passion became "none" - and a skill with no passion simply gains xp more slowly, so nothing would throw.</para>
    /// </summary>
    private static bool mirrorChecked;

    private static void CheckMirror()
    {
        if (mirrorChecked) return;
        mirrorChecked = true;
        foreach (TurboTick.Networking.PassionWire wire in Enum.GetValues<TurboTick.Networking.PassionWire>())
        {
            var game = (Passion)(byte)wire;
            if (Enum.IsDefined(typeof(Passion), game) && game.ToString() == wire.ToString()) continue;
            Log.Error($"[TurboTick] Passion 与线上镜像不一致：游戏侧 {game}，镜像侧 {wire}。"
                + "技能热情会传到错误的档位，请同步 Networking/SettlementGrants.cs 的 PassionWire。");
            return;
        }
    }

    /// <summary>Passion is part of the encoding because it changes what the player expects: a burning passion on a skill
    /// that never moves is a different complaint from a skill that is simply not being used.</summary>
    internal static string? Encode(Pawn? pawn)
    {
        CheckMirror();
        try
        {
            var tracker = pawn?.skills;
            if (tracker?.skills is null || tracker.skills.Count == 0) return null;
            var builder = new StringBuilder(tracker.skills.Count * 18);
            var first = true;
            foreach (var record in tracker.skills)
            {
                if (record?.def?.defName is not { Length: > 0 } name) continue;
                if (!first) builder.Append(';');
                first = false;
                // Percent is quantised to whole points: the bar is ~100 px wide, and a per-frame float would churn the
                // string for a difference nothing can display.
                builder.Append(name).Append('=')
                    .Append(record.Level).Append(':')
                    .Append((int)Math.Round(record.XpProgressPercent * 100f)).Append(':')
                    .Append((int)record.passion).Append(':')
                    // `xpSinceMidnight`, quantised to hundreds. It is not decoration: `LearningSaturatedToday` is
                    // `xpSinceMidnight > 4000f` (`:478876`), which multiplies the learning rate by 0.2 (`:479128`), and the
                    // skill tooltip prints `LearnedMaxToday` from it (`:680788`). Nothing on a replica advances it, so a
                    // colonist who has stopped learning for the day looks identical to one at full speed - and the line
                    // that says why is missing. Hundreds keeps the step under the 4000 threshold's resolution.
                    .Append((int)Math.Round(record.xpSinceMidnight / 100f) * 100f);
            }
            return first ? null : builder.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Applies an encoded skill string. Writes the public fields rather than calling <c>Learn</c>: that method
    /// checks <c>CanGainXP</c>, applies the learning rate, may level up and fires notifications - all of which assume the
    /// pawn is being simulated. A replica is being told the answer, not asked to earn it.</summary>
    internal static void Apply(Pawn? pawn, string? encoded)
    {
        if (pawn?.skills?.skills is null || encoded is not { Length: > 0 }) return;
        try
        {
            foreach (var entry in encoded.Split(';'))
            {
                if (entry.Length == 0) continue;
                var equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                var name = entry[..equals];
                var body = entry[(equals + 1)..].Split(':');
                if (body.Length < 3) continue;
                var record = pawn.skills.GetSkill(DefDatabase<SkillDef>.GetNamedSilentFail(name));
                if (record is null) continue;
                if (!int.TryParse(body[0], out var level)) continue;
                if (!int.TryParse(body[1], out var percent)) continue;
                if (!int.TryParse(body[2], out var passion)) continue;
                if (record.levelInt != level)
                {
                    record.levelInt = level;
                    // The cached disability flags are derived from the level in `Notify_SkillDisablesChanged`; a stale
                    // cache means the work tab greys out a skill the pawn has since become able to do.
                    pawn.skills.Notify_SkillDisablesChanged();
                }
                // `XpProgressPercent` is a getter over `xpSinceLastLevel`, so the fraction has to be turned back into xp
                // to be written. `XpRequiredForLevelUp` is the denominator the getter uses.
                var target = percent / 100f * record.XpRequiredForLevelUp;
                if (Math.Abs(record.xpSinceLastLevel - target) > record.XpRequiredForLevelUp * 0.02f)
                    record.xpSinceLastLevel = target;
                if ((int)record.passion != passion)
                    record.passion = (Passion)passion;
                // Fourth field is optional: a frame from before it existed still applies the other three instead of being
                // dropped, which is why this is a guarded index rather than a `Length != 4` check.
                if (body.Length >= 4 && float.TryParse(body[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var today)
                    && Math.Abs(record.xpSinceMidnight - today) >= 100f)
                    record.xpSinceMidnight = today;
            }
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] skill apply: " + error.GetType().Name + ": " + error.Message);
        }
    }
}
