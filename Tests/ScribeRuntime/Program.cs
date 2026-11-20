using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using TurboTick;

internal static class Program
{
    static readonly string GameDir = "C:/SteamLibrary/steamapps/common/RimWorld";
    static readonly List<string> Logs = new();
    public static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            foreach (var dir in new[] { GameDir, GameDir + "/Mods/Harmony/1.7/Assemblies" }) {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            return null;
        };
        try { return Run(args); } catch (Exception e) { Console.WriteLine(e); return 2; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        var clock = Stopwatch.StartNew();
        var harmony = new Harmony("turbotick.scribe.runtime.tests");
        foreach (var name in new[] { "Message", "Warning", "Error" })
            foreach (var m in typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)))
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureLog)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), "DevMode"), prefix: new HarmonyMethod(typeof(Program), nameof(FalseValue)));
        harmony.Patch(AccessTools.Method(typeof(ContentFinder<UnityEngine.Texture2D>), "Get"), prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        Set(game, "worldInt", world); Set(game, "maps", new List<Map>()); Set(game, "components", new List<GameComponent>());
        Current.Game = game;
        var faction = new Faction { loadID = 0, def = new FactionDef { defName = "RuntimeFaction" } };
        var manager = new FactionManager(); Set(manager, "allFactions", new List<Faction> { faction }); Set(world, "factionManager", manager);
        var holder = new WorldObjectsHolder(); Set(world, "worldObjects", holder);
        var def = new WorldObjectDef { defName = "RuntimeSettlement", worldObjectClass = typeof(Settlement), canHaveFaction = true };
        DefDatabase<WorldObjectDef>.Add(def);
        var settlement = new Settlement { ID = 42, def = def, Name = "runtime settlement" };
        Set(settlement, "factionInt", faction);
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        Set(map, "uniqueID", 3); Set(map, "info", new MapInfo { parent = settlement }); game.Maps.Add(map);
        // Keep a real world-holder membership but intercept broad rendering/map teardown dependencies only.
        var objects = (List<WorldObject>)AccessTools.Field(typeof(WorldObjectsHolder), "worldObjects").GetValue(holder)!;
        objects.Add(settlement);
        harmony.Patch(AccessTools.Method(typeof(MapParent), "Destroy"), prefix: new HarmonyMethod(typeof(Program), nameof(ObserveDestroy)));
        var xml = "<saveable><def>RuntimeSettlement</def><ID>42</ID><faction>Faction_0</faction><nameInt>runtime settlement</nameInt><previouslyGeneratedInhabitants /></saveable>";
        var loaded = ScribeBridge.Load(settlement, xml);
        var sameFaction = ReferenceEquals(settlement.Faction, faction);
        var membership = objects.Contains(settlement);
        var mapKept = game.Maps.Contains(map);
        Console.WriteLine($"RUNTIME {typeof(Scribe).Assembly.Location} version={typeof(Scribe).Assembly.GetName().Version}");
        Console.WriteLine($"SETTLEMENT loaded={loaded} factionSame={sameFaction} destroyed={settlement.Destroyed} holderMember={membership} mapKept={mapKept} destroyCalls={DestroyCalls} elapsedMs={clock.ElapsedMilliseconds}");
        foreach (var log in Logs) Console.WriteLine("GAME " + log);
        var pass = loaded && sameFaction && !settlement.Destroyed && membership && mapKept && DestroyCalls == 0;
        Console.WriteLine(pass ? "PASS settlement live-fragment" : "FAIL settlement live-fragment");
        return pass ? 0 : 1;
    }
    static void Set(object obj, string name, object? value) => AccessTools.Field(obj.GetType(), name).SetValue(obj, value);
    static bool CaptureLog(string text) { Logs.Add(text); return false; }
    static bool FalseValue(ref bool __result) { __result = false; return false; }
    static bool Skip() => false;
    static int DestroyCalls;
    static bool ObserveDestroy(WorldObject __instance)
    {
        DestroyCalls++;
        Set(__instance, "destroyed", true);
        var objects = (List<WorldObject>)AccessTools.Field(typeof(WorldObjectsHolder), "worldObjects").GetValue(Find.WorldObjects)!;
        objects.Remove(__instance);
        Current.Game.Maps.RemoveAll(m => m.Parent == __instance);
        return false;
    }
}
