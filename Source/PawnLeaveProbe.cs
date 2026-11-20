using System;
using System.Globalization;
using System.Linq;
using Verse;

namespace TurboTick;

/// <summary>Reports whether the three "this pawn leaves the colony" decisions actually publish a command.
///
/// <para><b>Why a counter and not a codec round trip.</b> The way these three were missed is specific: the gizmo that
/// <em>opens</em> the confirmation dialog is a <c>Command</c> and is covered, so the ledger read the entry as covered
/// while the dialog's own button - a <c>Dialog_MessageBox</c> callback - never passed through
/// <c>Command.ProcessInput</c>. A round trip through the codec would exercise the branch that was never broken.
/// What has to be observed is "a click produced a command", and that is <see cref="SharedActions.PawnLeaveSent"/>.</para>
///
/// <para><b>Why it can also run the capture.</b> The authority has no player, so <c>SharedActions.PlayerActing</c> is
/// false and a real click cannot be reproduced there. <see cref="TryRun"/> therefore calls
/// <c>CapturePawnLeave</c> directly, which is one of the two halves that can be wrong: the publish throws, or the
/// gate refuses. It cannot check "Harmony attached the prefix to the real method"; that is the install line in
/// <c>game-stdout.log</c>.</para>
///
/// <para><b>What it cannot prove, measured.</b> Running it on the authority publishes a real command, and the server
/// answers <c>SimulationReadOnly</c> - the authority joins the room as the <c>simulation</c> peer and is refused every
/// command it sends by design (<c>DedicatedServer.RelayCommandAsync</c>: "The process simulation does not issue player
/// commands"). So <c>发出</c> moving is the whole claim here; <c>应用</c> stays 0 on this machine and only moves on a
/// peer that actually clicked. That is not a defect in the probe - it is the same reason `PlayerActing` excludes
/// <c>SimulationPeerProcess</c>, and a probe that claimed to prove the apply side from here would be lying.</para></summary>
internal static class PawnLeaveProbe
{
    internal static string Summary() =>
        "小人离队命令：发出 " + SharedActions.PawnLeaveSent
        + " / 应用 " + SharedActions.PawnLeaveApplied
        + " / 失败 " + SharedActions.PawnLeaveMissed
        + (SharedActions.LastPawnLeave is { Length: > 0 } last ? "；最近 " + last : "；最近 无")
        // Named because the counters alone cannot tell "no player ever pressed it" from "the patch is not installed",
        // and those need opposite fixes.
        // The apply side is named as unreachable here rather than left at 0: a reader who sees 应用 0 has no way to
        // tell "the authority never applied it" from "the authority is not allowed to apply it", and only one of those
        // is a bug.
        + "（三个入口：驱逐 Banish / 招募 Recruit / 放归 DoReleaseAnimal；"
        + "权威端是 simulation peer，服务端拒绝它发出的命令，所以「应用」只会在真正点击过的副本上增长）";

    internal static bool TryRun(string op, string pawnIdText, out string outcome)
    {
        outcome = "";
        // `-1` (or any non-number) means "pick a colonist": requiring a real id made the probe unusable on the
        // authority, where the operator has no way to learn one before asking.
        if (!int.TryParse(pawnIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pawnId)) pawnId = -1;
        Pawn? pawn = null;
        if (pawnId < 0)
        {
            // `-1` means "any colonist". The authority has pawns in the save but no player, and the operator reading
            // this has no way to learn an id before running the probe - so requiring one made the probe unusable in
            // exactly the situation it exists for.
            pawn = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).FirstOrDefault();
        }
        else pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn is null)
        {
            outcome = "找不到 pawn=" + pawnId;
            return true;
        }
        var before = SharedActions.PawnLeaveSent;
        var published = SharedActions.CapturePawnLeave(op, pawn);
        outcome = (published ? "已发出 " : "未发出 ") + op + " pawn=" + pawn.thingIDNumber
            + "（发出计数 " + before + " → " + SharedActions.PawnLeaveSent + "）";
        return true;
    }
}
