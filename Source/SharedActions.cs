using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Shared-colony action layer. Every player-issued action passes through choke points:
///   DesignationManager.AddDesignation / RemoveDesignation - mine / deconstruct / hunt / harvest / cut / tame / cancels
///   GenConstruct.PlaceBlueprintForBuild                   - building and furniture orders
///   ThingWithComps.Destroy (Blueprint) / Frame.Destroy    - cancel construction
///   Pawn_DraftController.set_Drafted                      - draft toggle
///   Pawn_JobTracker.TryTakeOrderedJob                     - right-click move / haul / attack / interact orders
///   ForbidUtility.SetForbidden                            - item forbid / unforbid
///   Pawn_WorkSettings.SetPriority                         - colonist work priorities
///   Zone.AddCell / RemoveCell / Delete                    - stockpile / growing zone management
///   BillStack.AddBill / Delete                            - workbench crafting bills
///   Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap   - allowed area assignments
/// Captured actions are encoded, ordered by the server, and replayed on every peer at the same tick.
/// </summary>
public static class SharedActions
{
    internal static bool Applying;
    public static void ResetTickWork()
    {
        Applying = false;
    }

    // ---------- Capture ----------

    /// <summary>True when this call is the player acting, rather than the simulation calling the same method.
    ///
    /// That distinction decides whether a capture patch may cancel the call. Inside a tick the AI and vanilla
    /// use these methods constantly (forbidding, drafting, designating); cancelling those would break the
    /// colony on every peer. Outside a tick it is input, and it is safe to defer to the agreed tick so that
    /// every peer applies it at the same moment.</summary>
    internal static bool PlayerActing => !Applying && SharedColony.Session.AcceptsInput && !SharedColony.TickExecuting;

    public static void CaptureDesignation(Designation designation)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return;
        if (!designation.target.IsValid || designation.designationManager?.map is not { } map) return;
        // Thing-keyed designations must carry their target, not the cell it happens to occupy.
        var thingId = designation.target.HasThing ? designation.target.Thing.thingIDNumber : -1;
        Publish(CommandCodec.DesignationCommand(designation.def.defName, map.Index,
            designation.target.Cell.x, designation.target.Cell.z, thingId, SharedColony.NextSequence()));
    }

    public static void CaptureRemoveDesignation(Designation designation)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || designation?.def == null) return;
        var map = designation.designationManager?.map ?? Find.CurrentMap;
        if (map == null) return;
        var cell = designation.target.Cell;
        var thingId = designation.target.HasThing ? designation.target.Thing.thingIDNumber : -1;
        Publish(CommandCodec.RemoveDesignationCommand(designation.def.defName, map.Index,
            cell.x, cell.z, thingId, SharedColony.NextSequence()));
    }

    public static void CaptureBlueprint(BuildableDef buildable, ThingDef stuff, IntVec3 cell, Map map, Rot4 rot)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || buildable == null || map == null) return;
        Publish(CommandCodec.BlueprintCommand(buildable.defName, stuff?.defName ?? "",
            map.Index, cell.x, cell.z, rot.AsInt, SharedColony.NextSequence()));
    }

    public static void CaptureCancelThing(Thing thing)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || thing == null) return;
        Publish(CommandCodec.CancelThingCommand(thing.thingIDNumber, SharedColony.NextSequence()));
    }

    public static void CaptureDraft(Pawn pawn, bool drafted)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null) return;
        Publish(CommandCodec.DraftCommand(pawn.thingIDNumber, drafted, SharedColony.NextSequence()));
    }

    public static void CaptureOrder(Pawn pawn, bool result)
    {
        if (!result || Applying || !SharedColony.Session.AcceptsInput || pawn?.jobs?.curJob is not { } job) return;
        CaptureOrderJob(pawn, job);
    }

    /// <summary>Encodes an order from the job itself, so it can be captured before vanilla runs. A deferred
    /// capture must not read <c>curJob</c>, because the job has not been taken yet.</summary>
    public static void CaptureOrderJob(Pawn pawn, Job job)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn is null || job?.def is null) return;
        var a = job.targetA;
        var b = job.targetB;
        Publish(CommandCodec.OrderCommand(pawn.thingIDNumber, job.def.defName,
            a.IsValid ? a.Cell.x * 1000 + a.Cell.z : -1, a.HasThing ? a.Thing.thingIDNumber : -1,
            b.IsValid ? b.Cell.x * 1000 + b.Cell.z : -1, b.HasThing ? b.Thing.thingIDNumber : -1,
            job.count, SharedColony.NextSequence()));
    }

    public static void CaptureForbid(Thing thing, bool forbidden)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || thing == null) return;
        Publish(CommandCodec.ForbidCommand(thing.thingIDNumber, forbidden, SharedColony.NextSequence()));
    }

    public static void CapturePriority(Pawn pawn, WorkTypeDef w, int priority)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null || w == null) return;
        Publish(CommandCodec.PriorityCommand(pawn.thingIDNumber, w.defName, priority, SharedColony.NextSequence()));
    }

    public static void CaptureZoneCell(Zone zone, IntVec3 cell, bool add)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || zone?.Map == null) return;
        var type = zone is Zone_Growing ? "growing" : "stockpile";
        Publish(CommandCodec.ZoneCellCommand(type, zone.ID, zone.Map.Index, cell.x, cell.z, add, SharedColony.NextSequence()));
    }

    public static void CaptureZoneDelete(Zone zone)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || zone?.Map == null) return;
        Publish(CommandCodec.ZoneDeleteCommand(zone.Map.Index, zone.ID, SharedColony.NextSequence()));
    }

    public static void CaptureBillAdd(BillStack stack, Bill bill)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || stack?.billGiver is not Thing building || bill?.recipe == null) return;
        var repeat = bill is Bill_Production prod ? prod.repeatCount : 1;
        Publish(CommandCodec.BillAddCommand(building.thingIDNumber, bill.recipe.defName, repeat, SharedColony.NextSequence()));
    }

    public static void CaptureBillDelete(BillStack stack, Bill bill)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || stack?.billGiver is not Thing building || bill == null) return;
        var index = stack.IndexOf(bill);
        if (index >= 0)
        {
            Publish(CommandCodec.BillDeleteCommand(building.thingIDNumber, index, SharedColony.NextSequence()));
        }
    }

    public static void CaptureAreaAssign(Pawn pawn, Area area)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null) return;
        Publish(CommandCodec.AreaAssignCommand(pawn.thingIDNumber, area?.ID ?? -1, SharedColony.NextSequence()));
    }

    public static void CaptureTransport(CompTransporter transporter, TransferableOneWay transferable, int count)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return;
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return;
        if (transporter?.parent is not { } pod || transferable?.AnyThing is not { } thing || count <= 0) return;
        Publish(CommandCodec.TransportCommand(pod.thingIDNumber, thing.thingIDNumber, count, SharedColony.NextSequence()));
    }

    private static void Publish(GameCommand command) => SharedColony.Send(command);

    // ---------- Apply ----------

    public static void Apply(GameCommand command)
    {
        Applying = true;
        try
        {
            switch (command.Kind)
            {
                case CommandCodec.Designation:
                    ApplyDesignation(command.Payload);
                    break;
                case CommandCodec.RemoveDesignation:
                    ApplyRemoveDesignation(command.Payload);
                    break;
                case CommandCodec.Blueprint:
                    ApplyBlueprint(command.Payload);
                    break;
                case CommandCodec.CancelThing:
                    ApplyCancelThing(command.Payload);
                    break;
                case CommandCodec.Draft:
                    ApplyDraft(command.Payload);
                    break;
                case CommandCodec.Order:
                    ApplyOrder(command.Payload);
                    break;
                case CommandCodec.Forbid:
                    ApplyForbid(command.Payload);
                    break;
                case CommandCodec.Priority:
                    ApplyPriority(command.Payload);
                    break;
                case CommandCodec.ZoneCell:
                    ApplyZoneCell(command.Payload);
                    break;
                case CommandCodec.ZoneDelete:
                    ApplyZoneDelete(command.Payload);
                    break;
                case CommandCodec.BillAdd:
                    ApplyBillAdd(command.Payload);
                    break;
                case CommandCodec.BillDelete:
                    ApplyBillDelete(command.Payload);
                    break;
                case CommandCodec.AreaAssign:
                    ApplyAreaAssign(command.Payload);
                    break;
                case CommandCodec.Transport:
                    ApplyTransport(command.Payload);
                    break;
                default:
                    Log.Warning("[TurboTick] 未知共享命令：" + command.Kind);
                    break;
            }
        }
        finally { Applying = false; }
    }

    private static void ApplyDesignation(byte[] payload)
    {
        if (!CommandCodec.TryDecodeDesignation(payload, out var defName, out var mapIndex, out var x, out var z, out var thingId)) return;
        var def = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        if (def == null || map == null) return;
        // A Thing-keyed designation cannot be queried by cell; ask the manager through the same index it
        // uses internally, otherwise the lookup throws and the whole shared tick is cancelled.
        if (thingId > 0)
        {
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing is null || map.designationManager.DesignationOn(thing, def) != null) return;
            map.designationManager.AddDesignation(new Designation(thing, def));
            return;
        }
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map) || map.designationManager.DesignationAt(cell, def) != null) return;
        map.designationManager.AddDesignation(new Designation(new LocalTargetInfo(cell), def));
    }

    private static void ApplyRemoveDesignation(byte[] payload)
    {
        if (!CommandCodec.TryDecodeRemoveDesignation(payload, out var defName, out var mapIndex, out var x, out var z, out var thingId)) return;
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        if (map == null) return;
        var def = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
        if (thingId > 0)
        {
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing != null) map.designationManager.RemoveAllDesignationsOn(thing);
        }
        else if (def != null)
        {
            var cell = new IntVec3(x, 0, z);
            if (cell.InBounds(map)) map.designationManager.TryRemoveDesignation(cell, def);
        }
    }

    private static void ApplyBlueprint(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBlueprint(payload, out var buildableName, out var stuffName, out var mapIndex, out var x, out var z, out var rot)) return;
        var buildable = DefDatabase<ThingDef>.GetNamedSilentFail(buildableName);
        var stuff = string.IsNullOrEmpty(stuffName) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        if (buildable == null || map == null) return;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return;
        GenConstruct.PlaceBlueprintForBuild(buildable, cell, map, new Rot4(rot), Faction.OfPlayer, stuff);
    }

    private static void ApplyCancelThing(byte[] payload)
    {
        if (!CommandCodec.TryDecodeCancelThing(payload, out var thingId)) return;
        var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
        if (thing is Blueprint or Frame) thing.Destroy(DestroyMode.Cancel);
    }

    private static void ApplyDraft(byte[] payload)
    {
        if (!CommandCodec.TryDecodeDraft(payload, out var pawnId, out var drafted)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.drafter == null || pawn.drafter.Drafted == drafted) return;
        pawn.drafter.Drafted = drafted;
    }

    private static void ApplyOrder(byte[] payload)
    {
        if (!CommandCodec.TryDecodeOrder(payload, out var pawnId, out var jobName, out var aCell, out var aThing, out var bCell, out var bThing, out var count)) return;
        var def = DefDatabase<JobDef>.GetNamedSilentFail(jobName);
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (def == null || pawn?.jobs == null || pawn.Dead) return;
        var a = ResolveTarget(aCell, aThing);
        var b = ResolveTarget(bCell, bThing);
        if (!a.IsValid) return;
        var job = b.IsValid ? JobMaker.MakeJob(def, a, b) : JobMaker.MakeJob(def, a);
        if (count > 0) job.count = count;
        pawn.jobs.TryTakeOrderedJob(job);
    }

    private static void ApplyForbid(byte[] payload)
    {
        if (!CommandCodec.TryDecodeForbid(payload, out var thingId, out var forbidden)) return;
        var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
        thing?.SetForbidden(forbidden, warnOnFail: false);
    }

    private static void ApplyPriority(byte[] payload)
    {
        if (!CommandCodec.TryDecodePriority(payload, out var pawnId, out var workDefName, out var priority)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workDefName);
        if (pawn?.workSettings != null && def != null)
        {
            pawn.workSettings.SetPriority(def, priority);
        }
    }

    private static void ApplyZoneCell(byte[] payload)
    {
        if (!CommandCodec.TryDecodeZoneCell(payload, out var zoneType, out var zoneId, out var mapIndex, out var x, out var z, out var add)) return;
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        if (map == null) return;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return;
        var zone = map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        if (add)
        {
            if (zone == null)
            {
                if (zoneType == "growing") zone = new Zone_Growing(map.zoneManager);
                else zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                zone.ID = zoneId;
                map.zoneManager.RegisterZone(zone);
            }
            zone.AddCell(cell);
        }
        else
        {
            zone?.RemoveCell(cell);
        }
    }

    private static void ApplyZoneDelete(byte[] payload)
    {
        if (!CommandCodec.TryDecodeZoneDelete(payload, out var mapIndex, out var zoneId)) return;
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        var zone = map?.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        zone?.Delete();
    }

    private static void ApplyBillAdd(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBillAdd(payload, out var buildingId, out var recipeDefName, out var repeatCount)) return;
        var table = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == buildingId) as IBillGiver;
        var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
        if (table?.BillStack != null && recipe != null)
        {
            var bill = recipe.MakeNewBill(null);
            if (bill is Bill_Production prod && repeatCount > 0) prod.repeatCount = repeatCount;
            table.BillStack.AddBill(bill);
        }
    }

    private static void ApplyBillDelete(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBillDelete(payload, out var buildingId, out var billIndex)) return;
        var table = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == buildingId) as IBillGiver;
        if (table?.BillStack != null && billIndex >= 0 && billIndex < table.BillStack.Count)
        {
            table.BillStack.Delete(table.BillStack[billIndex]);
        }
    }

    private static void ApplyAreaAssign(byte[] payload)
    {
        if (!CommandCodec.TryDecodeAreaAssign(payload, out var pawnId, out var areaId)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.playerSettings == null) return;
        var area = areaId < 0 ? null : pawn.Map?.areaManager?.AllAreas?.FirstOrDefault(a => a.ID == areaId);
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
    }

    private static void ApplyTransport(byte[] payload)
    {
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return;
        if (!CommandCodec.TryDecodeTransport(payload, out var transporterId, out var thingId, out var count)) return;
        var pod = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == transporterId);
        var transporter = pod?.TryGetComp<CompTransporter>();
        var cargo = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == thingId);
        if (transporter == null || cargo == null) return;
        var transferable = new TransferableOneWay();
        transferable.things.Add(cargo);
        transporter.AddToTheToLoadList(transferable, count);
    }

    private static LocalTargetInfo ResolveTarget(int packedCell, int thingId)
    {
        if (thingId > 0)
        {
            var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
            return thing != null ? new LocalTargetInfo(thing) : LocalTargetInfo.Invalid;
        }
        if (packedCell < 0) return LocalTargetInfo.Invalid;
        var x = packedCell / 1000;
        var z = packedCell % 1000;
        var map = Find.CurrentMap;
        return map != null && new IntVec3(x, 0, z).InBounds(map) ? new LocalTargetInfo(new IntVec3(x, 0, z)) : LocalTargetInfo.Invalid;
    }
}

// ---------- Harmony Capture Patches ----------
//
// Every patch here follows the model Multiplayer uses: a player's action is forwarded to the server and
// the local vanilla call is cancelled, so the command is applied when its tick arrives - on every peer,
// the one that clicked included. Vanilla's own calls during a tick are left alone, because those run
// identically on every peer anyway and cancelling them would drop them on the origin only.
//
// The earlier shape (let vanilla run, then skip the echo) applied the action twice when the echo was not
// skipped and gave the origin a two-tick head start when it was. Both showed up as divergence.

/// <summary>Shape shared by the simple patches: capture the arguments, cancel the local call.</summary>
internal static class CapturePrefix
{
    internal static bool Defer(bool captured)
    {
        // false means "the player acted": vanilla must not also perform it.
        return !captured;
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
internal static class DesignationCapturePatch
{
    private static bool Prefix(Designation newDes)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureDesignation(newDes);
        return false;
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation))]
internal static class DesignationRemoveCapturePatch
{
    private static bool Prefix(Designation des)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureRemoveDesignation(des);
        return false;
    }
}

[HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild))]
internal static class BlueprintCapturePatch
{
    private static bool Prefix(BuildableDef sourceDef, IntVec3 center, Map map, Rot4 rotation, ThingDef stuff,
        ref Blueprint __result)
    {
        if (!SharedActions.PlayerActing || sourceDef is null || map is null) return true;
        SharedActions.CaptureBlueprint(sourceDef, stuff, center, map, rotation);
        __result = null!;
        return false;
    }
}

/// <summary>Cancelling a blueprint or frame is a removal the player asked for; the simulation also destroys
/// things during a tick, and those must run locally or the colony stalls.</summary>
[HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.Destroy), new[] { typeof(DestroyMode) })]
internal static class BlueprintCancelCapturePatch
{
    private static bool Prefix(ThingWithComps __instance, DestroyMode mode)
    {
        if (mode != DestroyMode.Cancel || __instance is not Blueprint) return true;
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureCancelThing(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(Frame), nameof(Frame.Destroy))]
internal static class FrameCancelCapturePatch
{
    private static bool Prefix(Frame __instance, DestroyMode mode)
    {
        if (mode != DestroyMode.Cancel || !SharedActions.PlayerActing) return true;
        SharedActions.CaptureCancelThing(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
internal static class DraftCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_DraftController, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_DraftController, Pawn>("pawn");

    private static bool Prefix(Pawn_DraftController __instance, bool value)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureDraft(PawnRef(__instance), value);
        return false;
    }
}

/// <summary>An order is captured from its argument, because the job has not been taken yet.</summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
internal static class OrderCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

    private static bool Prefix(Pawn_JobTracker __instance, Job job, ref bool __result)
    {
        if (!SharedActions.PlayerActing) return true;
        var pawn = PawnRef(__instance);
        if (pawn is null || job?.def is null) return true;
        SharedActions.CaptureOrderJob(pawn, job);
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.SetForbidden))]
internal static class ForbidCapturePatch
{
    private static bool Prefix(Thing t, bool value)
    {
        if (!SharedActions.PlayerActing || t is null) return true;
        SharedActions.CaptureForbid(t, value);
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
internal static class PriorityCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");

    private static bool Prefix(Pawn_WorkSettings __instance, WorkTypeDef w, int priority)
    {
        if (!SharedActions.PlayerActing || w is null) return true;
        SharedActions.CapturePriority(PawnRef(__instance), w, priority);
        return false;
    }
}

[HarmonyPatch(typeof(Zone), nameof(Zone.AddCell))]
internal static class ZoneAddCellCapturePatch
{
    private static bool Prefix(Zone __instance, IntVec3 c)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureZoneCell(__instance, c, add: true);
        return false;
    }
}

[HarmonyPatch(typeof(Zone), nameof(Zone.RemoveCell))]
internal static class ZoneRemoveCellCapturePatch
{
    private static bool Prefix(Zone __instance, IntVec3 c)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureZoneCell(__instance, c, add: false);
        return false;
    }
}

// Delete() is the user-facing wrapper; Delete(bool) is a separate virtual overload.
[HarmonyPatch(typeof(Zone), nameof(Zone.Delete), new Type[0])]
internal static class ZoneDeleteCapturePatch
{
    private static bool Prefix(Zone __instance)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureZoneDelete(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(BillStack), nameof(BillStack.AddBill))]
internal static class BillAddCapturePatch
{
    private static bool Prefix(BillStack __instance, Bill bill)
    {
        if (!SharedActions.PlayerActing || bill?.recipe is null) return true;
        SharedActions.CaptureBillAdd(__instance, bill);
        return false;
    }
}

[HarmonyPatch(typeof(BillStack), nameof(BillStack.Delete))]
internal static class BillDeleteCapturePatch
{
    private static bool Prefix(BillStack __instance, Bill bill)
    {
        if (!SharedActions.PlayerActing || bill is null) return true;
        SharedActions.CaptureBillDelete(__instance, bill);
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
internal static class AreaAssignCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_PlayerSettings, Pawn>("pawn");

    private static bool Prefix(Pawn_PlayerSettings __instance, Area value)
    {
        if (!SharedActions.PlayerActing) return true;
        SharedActions.CaptureAreaAssign(PawnRef(__instance), value);
        return false;
    }
}

[HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.AddToTheToLoadList))]
internal static class TransportCapturePatch
{
    private static bool Prefix(CompTransporter __instance, TransferableOneWay t, int count)
    {
        if (!SharedActions.PlayerActing || t is null) return true;
        SharedActions.CaptureTransport(__instance, t, count);
        return false;
    }
}
