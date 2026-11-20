using System.Reflection;

namespace TurboTick.Simulation;

/// <summary>The policy that decides which discovered <c>Designator</c> types are legal Harmony patch targets.
///
/// <para>Lives in the Simulation assembly, next to the rest of the codec, because it holds no Verse types: the rule is
/// "a type Harmony can patch", which is decidable from <see cref="Type"/> alone and therefore testable without the game
/// assembly. The discovery itself (<c>GenTypes.AllSubclasses(typeof(Designator))</c>) stays in the mod, where the game
/// type is available.</para>
///
/// <para><b>Why this is a policy object at all.</b> The designator patches used to be a hand-written array of 19 types
/// against a game that loads several dozen subclasses (<c>Source/DesignatorSync.cs</c>, the old
/// <c>PatchedTypes</c>). Harmony installs whatever it is handed, so the patch audit reported every entry green while the
/// common case - the building designator - was bound correctly and the rest silently were not. Two peers logged zero
/// <c>重放建筑蓝图</c> lines for a whole session, i.e. the capture never fired once, and nothing in the log could have
/// said so: an incomplete list looks exactly like a complete one from the inside.</para>
///
/// <para>Discovery replaces the list; this class keeps the *reasons* a discovered type may be unusable, so each one is
/// tested instead of assumed. Every rule below is a concrete Harmony failure:</para>
/// <list type="bullet">
/// <item>abstract - the method exists but has no body; <c>Patch()</c> throws and the whole class's patches are lost;</item>
/// <item>open generic - the same, plus Harmony cannot resolve a target method from an open definition;</item>
/// <item>constructed generic - <c>ClosedSample&lt;int&gt;</c> is a runtime type the game never instantiates, so patching
/// it binds a body nothing calls.</item>
/// </list>
/// </summary>
public static class DesignatorTargets
{
    /// <summary>True when this type can carry a Harmony patch on one of its declared methods.</summary>
    public static bool Acceptable(Type? type) =>
        type is not null
        && !type.IsAbstract
        && !type.ContainsGenericParameters
        && !type.IsGenericTypeDefinition
        // A constructed generic (`Foo&lt;int&gt;`) passes every check above: it is concrete, closed and not a definition.
        // It is still the wrong target - the game instantiates `Foo&lt;Something&gt;` for whichever argument its own code
        // chose, and patching one construction binds a body that may never run.
        && !type.IsConstructedGenericType;

    /// <summary>The types to patch, in the order they must be patched.
    ///
    /// <para>Ordinal by full name, and stable across runs on purpose: Harmony derives its patch id from the target
    /// method, so an unordered enumeration makes two otherwise identical launches produce different patch listings - and
    /// comparing an audit log between two peers is the main way a designator desync gets diagnosed at all. Ordinal
    /// rather than culture-aware because the two peers may run different locales, and a culture-aware sort would then
    /// order this list differently on each of them.</para></summary>
    public static List<string> Ordered(IEnumerable<string> names)
    {
        var list = new List<string>(names);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>What to log once at startup: how many targets were bound, and whether the one type the feature depends
    /// on is among them. A count alone is not enough - "27 targets" reads fine while omitting the building designator,
    /// which is precisely the state the old fixed list produced.</summary>
    public static string Describe(int types, int singleCell, int multiCell, bool hasBuildType) =>
        "类 " + types + " 个，单格 " + singleCell + " 个目标，拖拽 " + multiCell + " 个目标"
        + (hasBuildType ? "；含 Designator_Build" : "；缺 Designator_Build") + "。";
}
