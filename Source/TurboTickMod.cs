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
            // Def lists are already loaded here, so this is the earliest point a mod can still change their
            // order - and it needs no patch, so nothing can install too late for it.
            DefLoadOrder.Apply();
            Log.Message("[TurboTick] Def 加载顺序：" + DefLoadOrder.Describe());
            // Work givers of equal priority keep their load order, and the first one that answers wins.
            DefLoadOrder.ApplyWorkGiverOrder();
            Log.Message("[TurboTick] 工作优先级顺序：" + DefLoadOrder.DescribeWorkGivers());
            TickRatePatch.Install(TurboTickMod.Harmony);
            // Each gameplay patch is installed separately so one moved method cannot disable the rest.
            PatchInstaller.InstallAll(TurboTickMod.Harmony);
            // Installed for every `-headless` process, not only for the server and the simulation participant.
            // A display replica sets neither of those two mode flags, but it is still a headless process: the
            // engine runs its bare `while` loop for it too, so without this patch nothing caps its frame rate
            // and it spins at ~1900 fps on a pinned core. Measured: three replicas starved the authority of
            // CPU (`帧间隔 最差 0.75s / 停顿 8 次`, `停顿期本端循环 1-1190 次`).
            if (HeadlessRuntime.HeadlessProcess)
            {
                TurboTickMod.Harmony.CreateClassProcessor(typeof(HeadlessUpdatePatch)).Patch();
            }
            if (HeadlessRuntime.Active)
            {
                HeadlessRuntime.Start();
            }
            // Sweep transport debris from previous sessions. A crash or a kill mid-load skips the post-load prune,
            // so without this the transport copies accumulate one per attempt and show up in the load dialog as extra
            // colonies - measured on the production server as 24 hash-named files.
            //
            // This runs for the headless process too. It used to be guarded on `!HeadlessRuntime.Active`, which is
            // exactly backwards: the headless server is what loads a shared snapshot on every start, so it is the
            // process that leaves one behind each time, and skipping the sweep there is why nobody ever cleaned up.
            WorldFiles.PruneSaves(keep: 0);
            WorldFiles.PruneCache(keep: 1);
            // Steam replaces workshop files between sessions with no notice, and on this setup the only symptom is a
            // refused handshake that names a fingerprint instead of a mod. Record what was loaded and report what
            // changed, so "did a mod update?" is answered by the log rather than by comparing timestamps by hand.
            CompatibilityManifest.DetectUpdates();
            // GUI/update come from GameComponent; headless also pumps from Root on the title screen.
        }
        catch (Exception error) { Log.Error("[TurboTick] 初始化失败：" + error); }
    }
}


