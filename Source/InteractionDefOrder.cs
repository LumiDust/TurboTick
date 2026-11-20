using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>
/// Puts the def lists the simulation iterates into a deterministic order at startup.
///
/// Two independent instances of the same mistake were measured:
///
/// 1. **Interaction defs.** Two peers loaded the same 16 interaction defs with two blocks swapped
///    (<c>BuildRapport…Breakup</c> against <c>Chitchat…KindWords</c>). <c>TryInteractRandomly</c> walks that list with
///    <c>TryRandomElementByWeight</c>, so the same random value selected a different def, the branches drew different
///    numbers of values and the shared stream was offset.
/// 2. **Work givers.** <c>WorkTypeDef.ResolveReferences</c> (<c>Verse/WorkTypeDef.cs:101-110</c>) builds
///    <c>workGiversByPriority</c> with <c>orderby priorityInType descending</c>. <c>OrderBy</c> is a stable sort, so
///    givers with equal priority keep their order from <c>AllDefs</c> - load order, which differs between NTFS and
///    ext4. The first giver that returns a job wins, so the same pawn hauls on one peer and cleans on the other;
///    measured at tick 8076.
///
/// The cause in both cases is that def list order comes from <c>DirectoryInfo.GetFiles</c>, which neither platform
/// orders consistently. Sorting at startup is the fix that does not depend on patches, timing, or load internals:
/// the def XML is already read by the time a mod's constructor runs, so this cannot install too late.
/// </summary>
internal static class DefLoadOrder
{
    internal static bool Applied { get; private set; }

    internal static string Before { get; private set; } = "?";

    internal static int WorkGiversSorted { get; private set; } = -1;

    /// <summary>Sorts the live interaction-def list in place.</summary>
    internal static void Apply()
    {
        try
        {
            var field = AccessTools.Field(typeof(DefDatabase<InteractionDef>), "defsList");
            if (field?.GetValue(null) is not List<InteractionDef> list)
            {
                Log.Warning("[TurboTick] 找不到 DefDatabase<InteractionDef>.defsList，跳过 def 顺序规范化。");
                return;
            }
            // All of them, not the first few: the original order differed from index 5 onwards, so a five-entry
            // sample was identical on both peers and could not show that sorting changed anything.
            Before = string.Join(",", list.Select(d => d.defName));
            // A stable comparison on defName only: two defs may share a name across packages, and OrderBy then
            // falls back to the incoming order, which is as good a tie-break as any and still deterministic
            // for the common case of unique names.
            var sorted = list.OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
            if (sorted.Count != list.Count) return;
            list.Clear();
            list.AddRange(sorted);
            Applied = true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] def 顺序规范化失败：" + error.Message);
        }
    }

    /// <summary>Rebuilds each work type's giver list with a deterministic tie-break. Priorities themselves are
    /// untouched: only the order among givers of equal priority changes, and it changes to a fixed one.</summary>
    internal static void ApplyWorkGiverOrder()
    {
        try
        {
            var changed = 0;
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                var list = workType?.workGiversByPriority;
                if (list is null || list.Count < 2) continue;
                var sorted = list
                    .OrderByDescending(d => d.priorityInType)
                    .ThenBy(d => d.defName, StringComparer.Ordinal)
                    .ToList();
                var differs = false;
                for (var i = 0; i < sorted.Count; i++)
                {
                    if (ReferenceEquals(sorted[i], list[i])) continue;
                    differs = true;
                    break;
                }
                if (!differs) continue;
                list.Clear();
                list.AddRange(sorted);
                changed++;
            }
            WorkGiversSorted = changed;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] 工作优先级排序失败：" + error.Message);
        }
    }

    /// <summary>A digest of the giver order per work type, so two peers can be compared on it directly instead of
    /// inferring order from which job a pawn happened to pick.</summary>
    internal static string DescribeWorkGivers()
    {
        try
        {
            var text = new StringBuilder();
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading
                .OrderBy(d => d.defName, StringComparer.Ordinal))
                text.Append(workType.defName).Append('=')
                    .Append(string.Join(",", workType.workGiversByPriority.Select(g => g.defName)))
                    .Append(';');
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
                [..16].ToLowerInvariant();
            return "givers=" + digest + " reorderedTypes=" + WorkGiversSorted;
        }
        catch (Exception error) { return "givers=error " + error.GetType().Name; }
    }

    /// <summary>Order after the fix, for the startup log. Two peers must print the same string - that is the direct
    /// evidence, and far quicker to read than inferring order from which interaction def got picked.</summary>
    internal static string Describe() => "applied=" + (Applied ? "True" : "False")
        + " count=" + DefDatabase<InteractionDef>.DefCount
        + " head=" + string.Join(",", DefDatabase<InteractionDef>.AllDefsListForReading
            .Select(d => d.defName))
        + " before=" + Before;
}
