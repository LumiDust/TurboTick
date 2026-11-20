using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>How far a sanguophage has got through a deathrest, and how many buildings it may bind.
///
/// <para><b>The defect.</b> <c>Gene_Deathrest.TickDeathresting</c> is the only writer of <c>deathrestTicks</c>
/// (<c>deathrestTicks += delta</c>, <c>Assembly-CSharp.decompiled.cs:727252</c>) and it runs from
/// <c>Hediff_Deathrest</c>'s own tick, which a replica never runs. So the progress a player watches never moves, and the
/// number is read in two places that both matter:</para>
///
/// <list type="bullet">
/// <item><c>Hediff_Deathrest.LabelInBrackets</c> returns <c>DeathrestGene.DeathrestPercent.ToStringPercent("F0")</c>
/// (<c>:122496</c>) - that IS the health tab's deathrest readout, nothing else feeds it.</item>
/// <item><c>Gene_Deathrest.ShowWakeAlert</c> and <c>TickDeathresting</c> both compare <c>DeathrestPercent &gt;= 1f</c>
/// (<c>:727189</c>, <c>:727258</c>) - the "can wake safely" alert and the auto-wake are decided from it, so a frozen
/// counter means a replica never shows the alert.</item>
/// </list>
///
/// <para><b>Why the numerator only.</b> <c>DeathrestPercent</c> is <c>deathrestTicks / MinDeathrestTicks</c>, and
/// <c>MinDeathrestTicks</c> is <c>round(240000 / DeathrestEfficiency)</c> - derived from the gene's own def. Sending a
/// fraction would let a replica whose efficiency disagreed show a bar that explains nothing; sending the numerator leaves
/// the denominator to be computed on both sides from the same def, which is the same shape as
/// <c>ToilProgressSync</c> sending <c>ticksLeftThisToil</c> and not the duration.</para>
///
/// <para><b>What is deliberately not sent.</b> <c>adjustedDeathrestTicks</c> is written in exactly three places
/// (<c>+= DeathrestEfficiency * delta</c>, a reset to 0, and a 240001 sentinel) and read in none - grep finds the field,
/// its two writes and its <c>Scribe</c> line, and no reader at all. A value nothing reads cannot change what a player
/// sees, so it stays out. <c>boundBuildings</c> is a list of things the building channel already carries, and
/// <c>CurrentCapacity</c> is computed by counting their comps, so both are derived here.</para>
///
/// <para><b>Why the capacity travels even though it rarely changes.</b> <c>deathrestCapacity</c> is written by
/// <c>OffsetCapacity</c>, which is the player spending a deathrest accelerator - a permanent upgrade that cannot be derived
/// from anything the replica has. Without it the gizmo shows the wrong limit and <c>AtBuildingCapacityLimit</c> refuses a
/// fourth building the host would accept.</para></summary>
internal static class DeathrestSync
{
    /// <summary>Last `ticks:capacity` sent per pawn.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>A tenth of the shortest requirement.
    ///
    /// <para><c>MinDeathrestTicks</c> is 240000 ticks for efficiency 1 and less for higher efficiency. Quantising to 24000
    /// gives ten steps across the bar and still collapses the per-tick write into a frame entry every few hours of game
    /// time - which matters because the encoding doubles as the change key.</para></summary>
    private const int TickQuantum = 24000;

    internal static long StatesSent { get; private set; }
    internal static long StatesApplied { get; private set; }
    internal static int ApplyFailures { get; private set; }

    internal static void Reset()
    {
        sent.Clear();
        StatesSent = 0;
        StatesApplied = 0;
        ApplyFailures = 0;
    }

    internal static List<ReplicaDeathrest>? Publish()
    {
        List<ReplicaDeathrest>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var gene = pawn?.genes?.GetFirstGeneOfType<Gene_Deathrest>();
                    if (gene is null) continue;
                    var id = pawn!.thingIDNumber;
                    live.Add(id);
                    // A pawn with the gene but no deathrest progress is a legitimate reading and is still sent: the
                    // health tab prints 0% either way, and the zero has to be able to replace a stale 80%.
                    var ticks = gene.deathrestTicks;
                    if (ticks > 0) ticks = ticks / TickQuantum * TickQuantum;
                    var encoded = ticks.ToString(CultureInfo.InvariantCulture)
                        + ":" + gene.DeathrestCapacity.ToString(CultureInfo.InvariantCulture);
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaDeathrest(id, encoded));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 死眠发布失败：" + error.GetType().Name + ": " + error.Message);
        }
        return changed;
    }

    internal static void Apply(ReplicaDeathrest state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var gene = pawn?.genes?.GetFirstGeneOfType<Gene_Deathrest>();
            if (gene is null) { ApplyFailures++; return; }
            var fields = state.Values.Split(':');
            if (fields.Length != 2) { ApplyFailures++; return; }
            if (int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                gene.deathrestTicks = ticks;
            // `deathrestCapacity` is private with only a getter, so it is written by reflection. `OffsetCapacity` would
            // also send a "capacity changed" message to the player on every frame the value differed - the replica would
            // be told about an upgrade it is being shown, not one that just happened.
            if (int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
                capacityField ??= HarmonyLib.AccessTools.Field(typeof(Gene_Deathrest), "deathrestCapacity");
            if (capacityField?.GetValue(gene) is int current && current != capacity)
                capacityField.SetValue(gene, capacity);
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }

    private static System.Reflection.FieldInfo? capacityField;

    /// <summary>What the census shows: every pawn carrying the gene, its percentage and its capacity limit.
    ///
    /// <para>The percentage is printed rather than a count because that is the number the health tab prints, and "3 pawns
    /// have the gene" cannot tell a working channel from one that sends nothing.</para></summary>
    internal static string Preview()
    {
        try
        {
            var carriers = 0;
            var restored = 0;
            var encoded = new List<string>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var gene = pawn?.genes?.GetFirstGeneOfType<Gene_Deathrest>();
                    if (gene is null) continue;
                    carriers++;
                    if (gene.deathrestTicks > 0) restored++;
                    if (encoded.Count < 4)
                        encoded.Add(pawn!.thingIDNumber + ":"
                            + gene.DeathrestPercent.ToStringPercent("F0")
                            + "/上限" + gene.DeathrestCapacity);
                }
            if (carriers == 0) return "0 个带死眠基因的小人 / 发 " + StatesSent;
            return $"{carriers} 个带死眠基因 / {restored} 个有进度"
                + " / " + string.Join("，", encoded) + $" / 发 {StatesSent}";
        }
        catch (Exception error) { return "读取失败：" + error.GetType().Name; }
    }

    /// <summary>The exact string this channel last decided to send, per pawn.
    ///
    /// <para>Printed beside the live reading because the two answer different questions: the census shows what the game's own
    /// properties report, this shows what actually goes on the wire. A value that is right in the first and absent in the
    /// second is a channel that never fires; the reverse would be a payload built from something else.</para></summary>
    internal static string LastSent()
    {
        if (sent.Count == 0) return "（无）";
        var parts = new List<string>();
        foreach (var pair in sent)
        {
            parts.Add(pair.Key + "=" + pair.Value);
            if (parts.Count >= 4) break;
        }
        return string.Join("，", parts);
    }
}
