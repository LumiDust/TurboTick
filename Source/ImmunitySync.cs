using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimWorld;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>How far a disease has been fought off, for a replica where nobody fights anything off.
///
/// <para><b>Why a replica needs this.</b> <c>ImmunityHandler.ImmunityHandlerTickInterval</c> is the only thing that calls
/// <c>ImmunityRecord.ImmunityTickInterval</c>, and that method is the only writer of <c>ImmunityRecord.immunity</c>
/// (<c>immunity += ImmunityChangePerTick(...) * delta</c>, clamped to 1). A replica ticks no pawns, so every record stays at
/// the value the save held. Three player-visible things read it: <c>CompLabelInBracketsExtra</c> ("已产生免疫力"),
/// <c>CompTipStringExtra</c> ("Immunity: 42.3%") and <c>CompStateIcon</c> (the immune icon).</para>
///
/// <para><b>Why it also changes the disease itself.</b> <c>FullyImmune</c> is <c>Immunity >= 1f</c>, and
/// <c>SeverityPerDay</c> switches between <c>severityPerDayImmune</c> and <c>severityPerDayNotImmune</c> on it. So a disease the
/// host has beaten reads as still progressing here - and one still being fought reads as if the fight had already been won.
/// The severity itself travels on the hediff channel, but only because the host sends it; nothing here would ever compute it.</para>
///
/// <para><b>Why the whole list is sent rather than a delta.</b> A pawn has a handful of records at most and the list is
/// replaced wholesale on the authority when records are dropped (<c>immunity &lt;= 0f</c> at the end of the tick). Sending the
/// list makes a dropped record simply absent, which is the same statement.</para>
/// </summary>
internal static class ImmunitySync
{
    /// <summary>Last encoded record list per pawn, as `defName=immunity` joined by `;`.</summary>
    private static readonly Dictionary<int, string> sent = [];

    /// <summary>Immunity is shown to one decimal of a percent; quantising to 1% keeps a frame from being produced by a value
    /// the UI cannot display.</summary>
    private const float Quantum = 100f;

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

    internal static List<ReplicaImmunity>? Publish()
    {
        List<ReplicaImmunity>? changed = null;
        try
        {
            var live = new HashSet<int>();
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var handler = pawn?.health?.immunity;
                    if (handler is null) continue;
                    var id = pawn.thingIDNumber;
                    var encoded = Encode(handler);
                    if (encoded is null) { sent.Remove(id); continue; }
                    live.Add(id);
                    if (sent.TryGetValue(id, out var last) && last == encoded) continue;
                    sent[id] = encoded;
                    (changed ??= []).Add(new ReplicaImmunity(id, encoded));
                    StatesSent++;
                }
            foreach (var id in new List<int>(sent.Keys))
                if (!live.Contains(id)) sent.Remove(id);
        }
        catch (Exception) { return changed; }
        return changed;
    }

    private static string? Encode(ImmunityHandler handler)
    {
        try
        {
            var records = handler.ImmunityListForReading;
            if (records is null || records.Count == 0) return null;
            var text = new StringBuilder(64);
            foreach (var record in records)
            {
                if (record?.hediffDef?.defName is not { Length: > 0 } name) continue;
                var quantised = MathF.Round(record.immunity * Quantum) / Quantum;
                // A record at zero is dropped by the game on the same tick that reaches it, so carrying one would only ever
                // describe a state the authority is already leaving.
                if (quantised <= 0f) continue;
                if (text.Length > 0) text.Append(';');
                text.Append(name).Append('=')
                    .Append(quantised.ToString("R", CultureInfo.InvariantCulture));
            }
            return text.Length == 0 ? null : text.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Writes the record list onto the replica's handler.
    ///
    /// <para>Written through the game's own `ImmunityRecord` objects rather than by constructing replacements where possible:
    /// a record carries the def it immunises against and, for some, the source hediff that granted it, and a reconstructed
    /// one loses `source` - which `DiseaseContractChanceFactor` reads to decide whether a disease can be caught at all.</para>
    /// </summary>
    /// <summary>What the encoder would produce right now, without recording it as sent.
    ///
    /// <para>A zero record count means "nobody is ill" and, identically, "the handler never had a record to read" - and on a
    /// paused server no frame is published at all, so the sent counter cannot separate them. Running the real encoder is a
    /// claim the counter cannot make.</para></summary>
    internal static string Preview()
    {
        var text = new StringBuilder();
        try
        {
            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var handler = pawn?.health?.immunity;
                    if (handler is null) continue;
                    var encoded = Encode(handler);
                    if (encoded is null) continue;
                    if (text.Length > 0) text.Append(" | ");
                    text.Append(pawn.thingIDNumber).Append(':').Append(encoded);
                }
        }
        catch (Exception error) { return "编码失败：" + error.GetType().Name; }
        return text.Length == 0 ? "（无）" : text.ToString();
    }

    internal static void Apply(ReplicaImmunity state)
    {
        try
        {
            if (!AuthoritativeReplica.FindPawnAny(state.Pawn, out var pawn)) return;
            var handler = pawn.health?.immunity;
            if (handler is null) return;
            var list = handler.ImmunityListForReading;
            if (list is null) return;
            // Existing records are updated in place so `source` survives; anything the authority no longer has is removed,
            // which is how a recovered infection disappears from the panel.
            var wanted = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var entry in state.Records.Split(';'))
            {
                if (entry.Length == 0) continue;
                var split = entry.IndexOf('=');
                if (split <= 0) continue;
                if (float.TryParse(entry.AsSpan(split + 1), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var value)) wanted[entry[..split]] = value;
            }
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var record = list[i];
                if (record?.hediffDef?.defName is not { Length: > 0 } name) continue;
                if (wanted.ContainsKey(name)) continue;
                list.RemoveAt(i);
            }
            foreach (var pair in wanted)
            {
                var def = DefDatabase<HediffDef>.GetNamedSilentFail(pair.Key);
                if (def is null) continue;
                var existing = handler.GetImmunityRecord(def);
                if (existing is not null)
                {
                    // `GetImmunityRecord` may return a synthesised stand-in when a gene or hediff grants full immunity; writing
                    // to that would be writing to a copy. Only records actually in the list are updated.
                    var inList = false;
                    for (var i = 0; i < list.Count; i++)
                        if (list[i] is { } candidate && ReferenceEquals(candidate, existing)) { inList = true; break; }
                    if (inList) { existing.immunity = pair.Value; continue; }
                }
                var record = new ImmunityRecord { hediffDef = def, immunity = pair.Value };
                list.Add(record);
            }
            StatesApplied++;
        }
        catch (Exception)
        {
            ApplyFailures++;
        }
    }
}
