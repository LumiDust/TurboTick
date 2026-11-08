using HarmonyLib;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>RimWorld 1.7 client. The bundled server/protocol are independent from other multiplayer mods.</summary>
public class TurboTickMod : Mod
{
    public static TurboTickSettings Settings = null!;
    public static Harmony Harmony = null!;

    public TurboTickMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<TurboTickSettings>();
        TurboState.InitializeLocal(Settings);
        LongEventHandler.ExecuteWhenFinished(Bootstrap.Init);
    }

    public override string SettingsCategory() => "TurboTick";
    public override void DoSettingsWindowContents(Rect inRect)
    {
        RoomConnection.Pump();
        Settings.Draw(inRect);
    }
}

[StaticConstructorOnStartup]
public static class Bootstrap
{
    private static bool done;
    static Bootstrap() => Init();
    public static void Init()
    {
        if (done) return;
        done = true;
        try
        {
            TurboTickMod.Harmony = new Harmony("lumidust.turbotick");
            TickRatePatch.Install(TurboTickMod.Harmony);
            // Each gameplay patch is installed separately so one moved method cannot disable the rest.
            PatchInstaller.InstallAll(TurboTickMod.Harmony);
            if (HeadlessRuntime.Active)
            {
                TurboTickMod.Harmony.CreateClassProcessor(typeof(HeadlessUpdatePatch)).Patch();
                HeadlessRuntime.Start();
            }
            // Sweep transport debris from previous sessions. A crash or a kill mid-load skips the
            // post-load prune, so without this the hash-named snapshot copies accumulate one per attempt
            // and show up in the load dialog as extra colonies.
            if (!HeadlessRuntime.Active)
            {
                WorldFiles.PruneSaves(keep: 0);
                WorldFiles.PruneCache(keep: 1);
            }
            // GUI/update come from GameComponent; headless also pumps from Root on the title screen.
        }
        catch (Exception error) { Log.Error("[TurboTick] 初始化失败：" + error); }
    }
}
