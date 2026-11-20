using Verse;

namespace TurboTick;

/// <summary>Per-game settings only; sockets and server policy are transient, never serialized into a save.</summary>
public class TurboGameComponent : GameComponent
{
    private readonly Game? ownerGame;
    public TurboGameComponent() { }

    // 1.7 creates several temporary Game objects before reading/finalizing a save. Construction proves
    // nothing about load success. Only the final instance receives LoadedGame below.
    public TurboGameComponent(Game game) => ownerGame = game;

    private bool savedEnabled = true;
    private float savedMultiplier = 2f;
    private float savedCap = 60f;
    private ulong savedRandomState;
    private bool hasRandomState;
    private bool stableSnapshotIds;

    /// <summary>Ownership, if the save has any settlements.
    ///
    /// <para>Lives on the game component rather than beside it because <c>ExposeData</c> is the only place a save
    /// passes through, and a feature that can change who may act in the world has to travel with the world. An old
    /// save has no node for it, loads as empty, and <see cref="SettlementData.Active"/> stays false - which every gate
    /// treats as "no ownership layer, allow everything".</para></summary>
    public SettlementData Settlements = new();

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            var policy = TurboState.Authority.Current;
            savedEnabled = policy.Enabled;
            savedMultiplier = policy.Multiplier;
            savedCap = policy.MaxRate;
            savedRandomState = SimulationRandom.Capture();
            hasRandomState = true;
            stableSnapshotIds = SharedSnapshotStorage.Writing;
        }
        Scribe_Values.Look(ref savedEnabled, "turboEnabled", true);
        Scribe_Values.Look(ref savedMultiplier, "turboMultiplier", 2f);
        Scribe_Values.Look(ref savedCap, "turboMaxRate", 60f);
        Scribe_Values.Look(ref savedRandomState, "turboRandomState", 0UL);
        Scribe_Values.Look(ref hasRandomState, "turboHasRandomState", false);
        Scribe_Values.Look(ref stableSnapshotIds, "turboStableSnapshotIds", false);
        // Deep, not a reference: the settlement list is owned by this save and by nothing else, so it must be written
        // inline. A save without the node assigns null, which the class itself turns back into an empty list.
        Scribe_Deep.Look(ref Settlements, "settlements");
        Settlements ??= new SettlementData();
    }

    public override void LoadedGame()
    {
        if (!TurboState.Authority.SessionRequested)
            TurboState.Authority.SetLocal(savedEnabled, Math.Clamp(savedMultiplier, 1f, 20f), Math.Clamp(savedCap, 1f, 240f));
        if (ownerGame is not null)
        {
            SharedColony.ScheduleWorldLoaded(ownerGame, WorldLoadScope.CurrentSave,
                hasRandomState ? savedRandomState : null, stableSnapshotIds);
            if (HeadlessRuntime.Active) HeadlessRuntime.GameReady(ownerGame, WorldLoadScope.CurrentSave, newGame: false);
            else MultiplayerHosting.GameReady(ownerGame, WorldLoadScope.CurrentSave, newGame: false);
        }
    }

    public override void StartedNewGame()
    {
        if (!TurboState.Authority.SessionRequested) TurboState.InitializeLocal(TurboTickMod.Settings);
        if (ownerGame is not null)
        {
            if (HeadlessRuntime.Active) HeadlessRuntime.GameReady(ownerGame, null, newGame: true);
            else MultiplayerHosting.GameReady(ownerGame, null, newGame: true);
        }
    }

    public override void GameComponentUpdate()
    {
        if (HeadlessRuntime.Active) return; // Root.Update pumps exactly once, including the lobby.
        TurboState.Update();
        MultiplayerHosting.Update();
        RoomConnection.AutoConnectOnce();
        SharedColony.Update();
        // The acceptance script used to run only from the headless pump, so passing -turbotick-acceptance to
        // a GUI client silently did nothing. Same pump, same flag, both entry points.
        AcceptanceRun.Update();
        // Outside any tick on purpose: the scripted zone drag needs `PlayerActing`, which is false during a tick.
        ScriptedZoneDrag.Tick();
        // The same pump for the ownership layer's scripted entry point. Both are off unless their environment
        // variable is set, so a normal game pays one boolean test per frame each.
        ScriptedSettlement.Tick();
        // The designation path had its own reported failure ("no marks appear at all"), so it gets its own trigger.
        if (Find.CurrentMap is { } scriptMap)
        {
            ScriptedZoneDrag.TickDesignations(scriptMap);
            // Same reasoning for construction: the wall ring never got built, and the capture path could not be asked
            // whether it fired. This runs the player's own build designator at a fixed tick.
            ScriptedZoneDrag.TickBuild(scriptMap);
        }
        // Full operation coverage: one of every command kind, so "various operations do not diverge" can be measured.
        if (Find.CurrentMap is { } opsMap) ScriptedOperations.Tick(opsMap);
        // The replica-side forbidding probe. It lives here rather than in the headless pump because a display replica
        // runs this component and not `HeadlessRuntime.Update`; the authority probe stays on the other side of that
        // split so neither process measures itself.
        ForbidProbe.TickReplica();
    }
    public override void GameComponentOnGUI()
    {
        if (!HeadlessRuntime.Active) TurboHud.Draw();
    }
}




