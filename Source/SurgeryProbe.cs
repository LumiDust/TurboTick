using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Creates a real surgery bill through the game's own factory and reports what the channel did with it.
///
/// <para><b>The defect this measures.</b> `BillAddCapturePatch` is a prefix on `BillStack.AddBill` that cancels the
/// local call, and `HealthCardUtility.CreateSurgeryBill` (`:644616-644623`) assigns the body part *after* that call -
/// with a setter that logs an error and refuses when `billStack` is null (`:442400-442402`). So on the peer that
/// queued the surgery the part was never set, `CompletableEver` (`:442369-442373`) returned false, and every surgery
/// bill wrote `Can only set Bill_Medical.Part after the bill has been added to a pawn's bill stack.` to the log.
/// On the far peers the bill arrived through `bill-add`, which carries only recipe and repeat count and therefore had
/// no part at all.</para>
///
/// <para><b>Why the probe uses `CreateSurgeryBill` rather than constructing a bill.</b> A hand-built `Bill_Medical`
/// would skip the very ordering that caused the bug. Calling the factory runs `AddBill` (patched) and then the part
/// assignment exactly as the UI does, so the reading proves the capture path instead of the codec alone.</para>
///
/// <para><b>What a zero means.</b> This colony has no injured colonists and no medical beds in the sample save, so the
/// probe refuses to invent one; it reports the reason instead of a count that would read as a broken channel.</para>
/// </summary>
internal static class SurgeryProbe
{
    internal static string Probe(string? argument)
    {
        var map = Find.CurrentMap;
        if (map is null) return "没有当前地图";

        // A patient: any spawned pawn that is not dead. `CreateSurgeryBill` needs `mapHeld` for its message checks, so a
        // world pawn would throw before the capture even runs.
        Pawn? patient = null;
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is null || pawn.Dead || pawn.RaceProps?.IsFlesh != true) continue;
            if (pawn.BillStack is null) continue;
            patient = pawn;
            break;
        }
        if (patient is null) return "没有可建账单的活体小人（需要 IsFlesh 且有 BillStack）";

        // A recipe that **targets a body part**, because the defect is specifically that the part is lost. The first
        // version of this probe used `ExtractHemogenPack`, which targets no part (`targetsBodyPart == false`), so
        // "没有部位" was the correct answer and proved nothing - an instrument answering a question it cannot fail.
        // The chosen recipe must also have a part its worker accepts for this pawn, or `CreateSurgeryBill` would build a
        // bill whose `CompletableEver` is false for a reason unrelated to the channel.
        RecipeDef? recipe = null;
        BodyPartRecord? part = null;
        foreach (var candidate in DefDatabase<RecipeDef>.AllDefsListForReading)
        {
            if (!candidate.targetsBodyPart || candidate.Worker is null) continue;
            if (candidate.ingredients is { Count: > 0 }) continue;
            if (candidate.fixedIngredientFilter is { } filter && System.Linq.Enumerable.Any(filter.AllowedThingDefs))
                continue;
            List<BodyPartRecord> parts;
            try { parts = System.Linq.Enumerable.ToList(candidate.Worker.GetPartsToApplyOn(patient, candidate)); }
            catch (Exception) { continue; }
            if (parts.Count == 0) continue;
            recipe = candidate;
            part = parts[0];
            break;
        }
        // A xenogerm implant is the shape this round fixed: `SetTargetPawn` assigns `bill.xenogerm` *after*
        // `CreateSurgeryBill` returns, so the bill's own fragment carried a null gene set. It has no body part, so it
        // cannot exercise the `Part` ordering - both are reported separately rather than blurring into one verdict.
        if (recipe is null)
        {
            var xeno = ProbeXenogerm(patient);
            return "找不到「针对身体部位且无材料需求」的配方，无法在不引入材料的情况下测部位顺序"
                + "（用无部位配方测出的「没有部位」是正确读数，不能证明缺陷）；" + xeno + "；" + ProbeAssembler()
                + "；本进程 " + SharedActions.PlayerActingNote();
        }

        var gated = $"本进程 {SharedActions.PlayerActingNote()}";
        var before = CountMedical(patient, out var beforeWithPart);
        var beforeApplied = SharedActions.SurgeryAppliedForProbe;
        Bill_Medical? created = null;
        string outcome;
        try
        {
            // `CreateSurgeryBill` returns the bill and the probe keeps it, so nothing has to be hunted for afterwards.
            created = HealthCardUtility.CreateSurgeryBill(patient, recipe, part, null, sendMessages: false);
            outcome = "调用成功";
        }
        catch (Exception error)
        {
            outcome = "**抛异常** " + error.GetType().Name + ": " + error.Message;
        }

        var after = CountMedical(patient, out var afterWithPart);
        var text = new StringBuilder();
        text.Append($"患者 {patient.LabelShort}#{patient.thingIDNumber}，配方 {recipe.defName}，目标部位 {part?.Label}；{outcome}；");
        text.Append($"手术账单 {before} → {after}（有部位 {beforeWithPart} → {afterWithPart}）；");
        if (created is not null)
        {
            text.Append(created.Part is not null
                ? $"新建账单的部位 = {created.Part.Label}（期望 {part?.Label}）"
                : $"**新建账单没有部位**——期望 {part?.Label}，部位在被捕获前就丢了");
            text.Append(created.billStack is not null
                ? "；已挂到账单栈"
                : "；**没有挂到账单栈**（部位设置会被拒绝）");
        }
        text.Append($"；通道 应用 {SharedActions.SurgeryAppliedForProbe - beforeApplied} 次；{gated}；");
        text.Append(SharedActions.SurgerySummary());
        return text.ToString();
    }

    /// <summary>Reports what the xenogerm half can prove on this save.
    ///
    /// <para>The fix is that the capture moved from `CreateSurgeryBill`'s postfix - where `bill.xenogerm` is still null
    /// (`:596896` assigns it on the returned value) - to `Xenogerm.SendImplantationLetter`'s postfix, the last statement
    /// of the same callback. A `Bill_Medical` fragment scribes `xenogerm` by reference (`:272315`), so a bill captured
    /// one step early carried null and the receiver's `SurgeryOutcomeComp_XenogermComplexity` (`:722890-722894`) read 0.</para>
    ///
    /// <para>On this save there is no xenogerm item and no Biotech xenogene work, so the honest answer is that the
    /// ordering cannot be exercised here - not that it works.</para></summary>
    private static string ProbeXenogerm(Pawn patient)
    {
        var items = 0;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not Xenogerm) continue;
                items++;
                break;
            }
        if (!ModsConfig.BiotechActive) return $"异种移植：Biotech 未启用，测不到（本图 {items} 个异种胚）";
        if (items == 0) return "异种移植：本图 0 个异种胚，`SetTargetPawn` 的赋值顺序测不到（通道已建，载体待实测）";
        return $"异种移植：本图 {items} 个异种胚，可在 UI 里选「植入到…」后重测";
    }

    /// <summary>Reports whether this map even has the building whose dialog-driven write is now captured.
    ///
    /// <para>`GeneAssemblerStartCapturePatch` covers a dialog button, not a gizmo, so nothing else in the mod sees it -
    /// `GizmoStatePatch` only hooks `Command.ProcessInput` (`:2205-2233`). The write it captures is
    /// `Building_GeneAssembler.Start` (`:581172-581181`), every field of which `ExposeData` (`:581416-581430`) scribes,
    /// so the building's own fragment carries the state and no new channel was needed. On a map with no assembler the
    /// only honest reading is "no carrier".</para></summary>
    private static string ProbeAssembler()
    {
        var assemblers = 0;
        var working = 0;
        foreach (var map in Find.Maps)
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building is not Building_GeneAssembler assembler) continue;
                assemblers++;
                if (assembler.Working) working++;
            }
        if (assemblers == 0) return "基因装配器：本图 0 台，`StartAssembly` 的捕获测不到（补丁已挂，载体待实测）";
        return $"基因装配器：本图 {assemblers} 台（{working} 台在装配中），可在 UI 里点「开始装配」后重测这下一次写入";
    }

    private static int CountMedical(Pawn pawn, out int withPart)
    {
        var bills = 0;
        withPart = 0;
        if (pawn?.BillStack is null) return 0;
        foreach (var bill in pawn.BillStack.Bills)
        {
            if (bill is not Bill_Medical medical) continue;
            bills++;
            if (medical.Part is not null) withPart++;
        }
        return bills;
    }
}
