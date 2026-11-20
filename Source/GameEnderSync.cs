using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>The colony's death countdown, for a replica that never counts it down.
///
/// <para><b>Why a replica needs this.</b> <c>GameEnder.GameEndTick</c> (<c>:361748</c>) is the only writer of
/// <c>ticksToGameOver</c>: it decrements while <c>gameEnding</c> and, exactly at zero, raises the
/// <c>GameOverEveryoneDead</c> letter. It is reached from the game tick, which a display replica does not run
/// (<see cref="AuthoritativeReplica.Frozen"/>). So the last colonist's death arrives as a picture - the map empties, the
/// bars read zero - and nothing ever says the colony ended, which is the one message a player must not miss.</para>
///
/// <para><b>Why both fields.</b> <c>gameEnding</c> is what starts the countdown (`GameEnder.CheckOrUpdateGameOver` sets it
/// when every free colonist is dead or gone) and <c>ticksToGameOver</c> is the remaining time; sending the count alone
/// cannot distinguish "counting down from 400" from "not ending, and this number is stale". The countdown is short - 400
/// ticks, <c>GameEndCountdownDuration</c> - so it is sent unquantised: a ten-tick quantum would be 2.5% of the whole
/// event.</para>
internal static class GameEnderSync
{
    private static string? sent;

    internal static long Sends { get; private set; }
    internal static int ApplyCount { get; private set; }
    internal static bool Ending { get; private set; }
    internal static int TicksLeft { get; private set; } = -1;

    /// <summary>One row when either field changed, otherwise null. Both are read through the private field because
    /// <c>ticksToGameOver</c> has no accessor at all (<c>:361619</c>) and <c>gameEnding</c> is a plain public field that
    /// anything could set.</summary>
    internal static List<ReplicaGameEnder>? Publish()
    {
        try
        {
            var ender = Find.GameEnder;
            if (ender is null) return null;
            var ending = ReadBool(ender, "gameEnding");
            var left = ReadInt(ender, "ticksToGameOver");
            Ending = ending;
            TicksLeft = left;
            var encoded = (ending ? "1" : "0") + "#" + left.ToString(CultureInfo.InvariantCulture);
            if (sent == encoded) return null;
            sent = encoded;
            Sends++;
            return [new ReplicaGameEnder(ending, left)];
        }
        catch (Exception) { return null; }
    }

    internal static void Apply(ReplicaGameEnder state)
    {
        try
        {
            var ender = Find.GameEnder;
            if (ender is null) return;
            WriteField(ender, "gameEnding", state.Ending);
            WriteField(ender, "ticksToGameOver", state.TicksLeft);
            Ending = state.Ending;
            TicksLeft = state.TicksLeft;
            ApplyCount++;
        }
        catch (Exception) { }
    }

    /// <summary>What the census prints. `结束中` and `剩余 -1` are separate columns because -1 is the field's own
    /// "not ending" default (`:361619`), so a single number would read the same whether the countdown was absent or
    /// complete.</summary>
    internal static string Summary() =>
        (Ending ? "结束中" : "未结束") + " 剩余 " + TicksLeft.ToString(CultureInfo.InvariantCulture)
        + " / 发 " + Sends.ToString(CultureInfo.InvariantCulture)
        + " / 应用 " + ApplyCount.ToString(CultureInfo.InvariantCulture);

    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> cache = [];

    private static System.Reflection.FieldInfo? Field(Type type, string name)
    {
        if (cache.TryGetValue((type, name), out var known)) return known;
        var found = HarmonyLib.AccessTools.Field(type, name);
        cache[(type, name)] = found;
        return found;
    }

    private static bool ReadBool(object owner, string name) => Field(owner.GetType(), name)?.GetValue(owner) as bool? ?? false;

    private static int ReadInt(object owner, string name) => Field(owner.GetType(), name)?.GetValue(owner) as int? ?? -1;

    private static void WriteField(object owner, string name, object value) =>
        Field(owner.GetType(), name)?.SetValue(owner, value);

    internal static void Reset() => sent = null;
}
