# "Motivated Raids" — Lord/LordJob/LordToil/Trigger API Spec (RimWorld 1.6)

Everything below was extracted with `monop -r:Assembly-CSharp.dll <Type>` and, for method bodies,
`ikdasm $HOME/refs/api/Assembly-CSharp.dll` (dumped to a scratch `.il` file, grepped by method, then
deleted). XML is verbatim from the user's live 1.6 install (`Data/Core/Defs/DutyDefs/*.xml`,
`Data/Core/Defs/FactionDefs/*.xml`). Anything not directly confirmed this way is flagged **UNVERIFIED**.

This doc assumes you've read `raids.md` first (incident/strategy/arrival-mode pipeline, `IncidentParms`,
`RaidStrategyWorker`). It supersedes and corrects two specific claims in `raids.md` §5 — see §0.

---

## 0. Corrections to `raids.md`

1. **`LordJob_Steal` is *not* a "go steal specific loot" job.** Its `CreateGraph()` is a two-toil
   `LordToil_StealCover` cover-swap graph (see §2) — it's a **subgraph vanilla `LordJob_AssaultColony`
   attaches to itself** when `canSteal` is true, not a standalone "steal raid" LordJob. `raids.md`'s
   description ("its entire 'steal specific things' logic must live inside `CreateGraph()`") is wrong —
   the real item-selection logic lives in `RimWorld.StealAIUtility` and `JobGiver_Steal`, reached via a
   `DutyDef` (`DutyDefOf.Steal`), not via anything on `LordJob_Steal` itself. See §4/§6.
2. **`LordJob_Steal` has no `ExposeData()` override.** `raids.md` listed one (`public override void
   ExposeData();`); the actual IL member list for `RimWorld.LordJob_Steal` is only `get_GuiltyOnDowned()`,
   `CreateGraph()`, and the parameterless `.ctor()` — no `ExposeData` override (harmless since it has zero
   fields, but the claim itself was wrong).
3. **`LordJob_AssaultColony.CreateGraph()` does *not* use `Trigger_FractionPawnsLost`.** `raids.md` listed
   it among the "off-the-shelf building blocks" relevant to a losses-based retreat, which is true in
   general (it's used by `LordJob_Siege`, `LordJob_StageThenAttack`, `LordJob_DefendBase`,
   `LordJob_SitePawns` at fractions 0.2–0.3, and by base `Lord.SetJob` itself — see §7.4), but it is
   **absent from `LordJob_AssaultColony`**, the exact class you're templating from. Standard assault raids
   have no "retreat once we've lost X% of our force" logic at all by default; they use
   `Trigger_TicksPassed` (random give-up timer) and `Trigger_FractionColonyDamageTaken` (retreat once
   they've done enough damage) instead. Full reconstruction in §1.

---

## 1. `RimWorld.LordJob_AssaultColony.CreateGraph()` — full reconstruction

Ctor (confirmed, matches `raids.md`):

```csharp
public LordJob_AssaultColony(
    Faction assaulterFaction,
    bool canKidnap = true,
    bool canTimeoutOrFlee = true,
    bool sappers = false,
    bool useAvoidGridSmart = false,
    bool canSteal = true,
    bool breachers = false,
    bool canPickUpOpportunisticWeapons = false);
```

Private fields backing those params, plus three `static readonly IntRange` thresholds (from
`monodis --fields`):

```csharp
private Faction assaulterFaction;
private bool canKidnap, canTimeoutOrFlee, sappers, useAvoidGridSmart, canSteal, breachers,
             canPickUpOpportunisticWeapons;
private static readonly IntRange AssaultTimeBeforeGiveUp;
private static readonly IntRange SapTimeBeforeGiveUp;
private static readonly IntRange BreachTimeBeforeGiveUp;   // exact IntRange values not dumped — [opt] not literal-constant, UNVERIFIED
```

Reconstructed pseudo-C# of `CreateGraph()` (every API call below is a literal read of the IL, in order):

```csharp
public override StateGraph CreateGraph()
{
    var graph = new StateGraph();
    var mainToils = new List<LordToil>();       // toils that should flee/kidnap/steal-trigger from
    LordToil sapperToil = null;

    if (sappers)
    {
        sapperToil = new LordToil_AssaultColonySappers();
        if (useAvoidGridSmart) sapperToil.useAvoidGrid = true;
        graph.AddToil(sapperToil);
        mainToils.Add(sapperToil);

        // sapper toil -> itself, triggered by losing a pawn (re-picks a dig target basically)
        var t = new Transition(sapperToil, sapperToil, canMoveToSameState: true, updateDutiesIfMovedToSameState: true);
        t.AddTrigger(new Trigger_PawnLost(default, null));   // PawnLostCondition.Undefined, pawn=null -> any loss
        graph.AddTransition(t, false);
    }

    LordToil breachToil = null;
    if (breachers)
    {
        breachToil = new LordToil_AssaultColonyBreaching();
        if (useAvoidGridSmart) breachToil.useAvoidGrid = useAvoidGridSmart;
        graph.AddToil(breachToil);
        mainToils.Add(breachToil);
    }

    var assaultToil = new LordToil_AssaultColony(attackDownedIfStarving: false, canPickUpOpportunisticWeapons);
    if (useAvoidGridSmart) assaultToil.useAvoidGrid = true;
    graph.AddToil(assaultToil);

    var exitToil = new LordToil_ExitMap(LocomotionUrgency.Jog, canDig: false, interruptCurrentJob: true);
    exitToil.useAvoidGrid = true;
    graph.AddToil(exitToil);

    if (sappers)
    {
        var t = new Transition(sapperToil, assaultToil, false, true);
        t.AddTrigger(new Trigger_NoFightingSappers());
        graph.AddTransition(t, false);
    }

    if (assaulterFaction != null && assaulterFaction.def.humanlikeFaction)
    {
        if (canTimeoutOrFlee)
        {
            // 1) give-up-after-a-while timeout (from mainToils -> exit)
            var giveUp = new Transition(assaultToil, exitToil, false, true);
            giveUp.AddSources(mainToils);   // sapper/breach toils too, if present
            IntRange giveUpRange = sappers ? SapTimeBeforeGiveUp : breachers ? BreachTimeBeforeGiveUp : AssaultTimeBeforeGiveUp;
            var timeoutTrigger = new Trigger_TicksPassed(giveUpRange.RandomInRange);
            timeoutTrigger.WithFilter(new TriggerFilter_MapExitable());   // only fires if map is exitable
            giveUp.AddTrigger(timeoutTrigger);
            giveUp.AddPreAction(new TransitionAction_Message("MessageRaidersGivenUpLeaving".Translate(
                assaulterFaction.def.pawnsPlural.CapitalizeFirst(), assaulterFaction.Name), null, 1f));
            graph.AddTransition(giveUp, false);

            // 2) "satisfied enough damage dealt" retreat (assaultToil -> exit only, NOT from sappers/breach)
            var satisfied = new Transition(assaultToil, exitToil, false, true);
            satisfied.AddSources(mainToils);
            float desiredFraction = new FloatRange(0.25f, 0.35f).RandomInRange;
            var damageTrigger = new Trigger_FractionColonyDamageTaken(desiredFraction, minDamage: 900f);
            damageTrigger.WithFilter(new TriggerFilter_MapExitable());
            satisfied.AddTrigger(damageTrigger);
            satisfied.AddPreAction(new TransitionAction_Message("MessageRaidersSatisfiedLeaving".Translate(...), null, 1f));
            graph.AddTransition(satisfied, false);
        }

        if (canKidnap)
        {
            var kidnapGraph = new LordJob_Kidnap().CreateGraph();
            graph.AttachSubgraph(kidnapGraph);
            LordToil kidnapStart = kidnapGraph.StartingToil;

            var t = new Transition(assaultToil, kidnapStart, false, true);
            t.AddSources(mainToils);
            t.AddPreAction(new TransitionAction_Message("MessageRaidersKidnapping".Translate(...), null, 1f));
            t.AddTrigger(new Trigger_KidnapVictimPresent());
            graph.AddTransition(t, false);
        }

        if (canSteal)
        {
            var stealGraph = new LordJob_Steal().CreateGraph();     // see §2 — this is the "cover" subgraph
            graph.AttachSubgraph(stealGraph);
            LordToil stealStart = stealGraph.StartingToil;

            var t = new Transition(assaultToil, stealStart, false, true);
            t.AddSources(mainToils);
            t.AddPreAction(new TransitionAction_Message("MessageRaidersStealing".Translate(...), null, 1f));
            t.AddTrigger(new Trigger_HighValueThingsAround());
            graph.AddTransition(t, false);
        }
    }

    // Mechanoids specifically: leave when the game/game-ending sequence starts (2500 ticks)
    if (assaulterFaction == Faction.OfMechanoids)
    {
        var t = new Transition(assaultToil, exitToil, false, true);
        t.AddSources(mainToils);
        t.AddTrigger(new Trigger_GameEnding(2500));
        t.AddPreAction(new TransitionAction_Message("MechanoidsMovingOn".Translate(), null, 1f));
        graph.AddTransition(t, false);
    }

    // Any raid: if the faction becomes non-hostile mid-raid, leave
    if (assaulterFaction != null)
    {
        var t = new Transition(assaultToil, exitToil, false, true);
        t.AddSources(mainToils);
        t.AddTrigger(new Trigger_BecameNonHostileToPlayer());
        t.AddPreAction(new TransitionAction_Message("MessageRaidersLeaving".Translate(...), null, 1f));
        graph.AddTransition(t, false);
    }

    return graph;
}
```

Key takeaways for our design:

- **No losses-based retreat at all** in vanilla `LordJob_AssaultColony` — only a randomized give-up timer
  (`AssaultTimeBeforeGiveUp`/`SapTimeBeforeGiveUp`/`BreachTimeBeforeGiveUp`, exact ranges UNVERIFIED — not
  literal IL constants) and a "we've done enough colony damage" satisfaction check
  (`Trigger_FractionColonyDamageTaken(RandomInRange(0.25f,0.35f), minDamage:900f)`).
- `AttachSubgraph(StateGraph)` is the standard way to compose a sub-job's toils/transitions into a bigger
  graph and jump into it via a normal `Transition` targeting `subgraph.StartingToil` — this is exactly the
  mechanism to use for a "loot phase" sub-behavior in our motivated raid.
- Every `Trigger_TicksPassed`/`Trigger_FractionColonyDamageTaken` transition here is wrapped with
  `.WithFilter(new TriggerFilter_MapExitable())` — i.e. it only actually fires if the map currently allows
  exiting. Worth copying for any of our own "go home" transitions.
- `mainToils` (sapper/breach/base-assault toils, NOT the steal/kidnap subgraphs) are the transition
  *sources* for every one of these — meaning a raider actively in the "steal" or "kidnap" sub-state does
  **not** independently re-evaluate these give-up/satisfied/game-ending triggers; only pawns still on the
  main assault toil do. Something to be deliberate about in our own graph shape.

---

## 2. `RimWorld.LordJob_Steal.CreateGraph()` — full reconstruction (it's a cover-swap subgraph)

```csharp
public LordJob_Steal();   // zero fields, zero params

public override StateGraph CreateGraph()
{
    var graph = new StateGraph();

    var a = new LordToil_StealCover { useAvoidGrid = true };
    graph.AddToil(a);

    var b = new LordToil_StealCover { cover = false, useAvoidGrid = true };
    // (LordToil_DoOpportunisticTaskOrCover.cover defaults false already; `a` never sets `cover=true`
    //  either — see below, both toils behave identically at construction time)
    graph.AddToil(b);

    var t = new Transition(a, b, canMoveToSameState: false, updateDutiesIfMovedToSameState: true);
    t.AddTrigger(new Trigger_TicksPassedAndNoRecentHarm(1200));   // 0x4b0 = 1200 ticks = 20s
    graph.AddTransition(t, false);

    return graph;
}
```

`LordToil_StealCover : LordToil_DoOpportunisticTaskOrCover` overrides only:

```csharp
protected override DutyDef DutyDef => DutyDefOf.Steal;

protected override bool TryFindGoodOpportunisticTaskTarget(Pawn pawn, out Thing target, List<Thing> alreadyTakenTargets)
{
    // if pawn is ALREADY on the Steal duty and is carrying something, that carried thing IS the target
    // (keeps them from re-rolling); otherwise delegates to:
    return StealAIUtility.TryFindBestItemToSteal(pawn.Position, pawn.Map, maxDist: 7f, out target, pawn, alreadyTakenTargets);
}
```

`LordToil_DoOpportunisticTaskOrCover` (abstract base, `cover` public bool field) does the actual per-pawn
work in **`UpdateAllDuties()`** and **`LordToilTick()`**, both reconstructed from IL:

- `UpdateAllDuties()`: for every owned pawn — if `cover == true`, try `TryFindGoodOpportunisticTaskTarget`;
  if it finds something AND the pawn isn't `GenAI.InDangerousCombat`, switch the pawn's duty to
  `DutyDef` (`DutyDefOf.Steal`) and end its current job. Otherwise (no target, or `cover == false`), put
  the pawn back on `DutyDefOf.AssaultColony`.
- `LordToilTick()`: **only runs every 181 ticks** (`TicksGame % 0xB5 == 0`) and **only if `cover ==
  true`**. For each owned pawn not downed and still on `DutyDefOf.AssaultColony`: try to find a steal
  target; if found, reserve it (`Map.reservationManager.IsReservedByAnyoneOf`) and not already in danger,
  switch that individual pawn to the `Steal` duty (ending their current job) — a rolling, throttled
  opportunistic re-tasking rather than an all-at-once `UpdateAllDuties()` pass.

**So "the steal subgraph" is really just two identically-configured `LordToil_StealCover` states that cycle
every 1200 ticks (this appears to be a keep-alive / re-evaluate mechanism, not a meaningful state change —
`canMoveToSameState:false` with `updateDutiesIfMovedToSameState:true` means transitioning `a → b` forces a
fresh `UpdateAllDuties()` pass even though the toil classes are behaviorally identical).** The real
per-pawn "go grab an item and carry it off" logic lives in the `DutyDef` (`DutyDefOf.Steal`) and its
`JobGiver_Steal` `ThinkNode`, reached via `PawnDuty.def` — see §4/§6.

---

## 3. Base classes — full overridable-member surface

### `Verse.AI.Group.LordJob` (abstract)

```csharp
public abstract class LordJob : IDisposable, IExposable
{
    public Lord lord;                                    // the only public field

    public abstract StateGraph CreateGraph();             // MUST implement
    public virtual void ExposeData();                     // no-op in base — MUST override to save custom fields (see §7.3)
    public virtual void LordJobTick();                    // no-op in base — called every tick from Lord.LordTick()
    public virtual void Cleanup();
    public virtual void PostCleanup();
    public virtual void Notify_AddedToLord();
    public virtual void Notify_PawnAdded(Pawn p);
    public virtual void Notify_PawnLost(Pawn p, PawnLostCondition condition);
    public virtual void Notify_PawnJobDone(Pawn p, JobCondition condition);
    public virtual void Notify_InMentalState(Pawn pawn, MentalStateDef stateDef);
    public virtual void Notify_BuildingAdded(Building b);
    public virtual void Notify_CorpseAdded(Corpse c);
    public virtual void Notify_BuildingLost(Building b);
    public virtual void Notify_CorpseLost(Corpse c);
    public virtual void Notify_LordDestroyed();
    public virtual void Notify_MapRemoved();
    public virtual void Notify_PawnUndowned(Pawn p);
    public virtual void Notify_PawnDowned(Pawn p);
    public virtual ThinkResult Notify_DutyConstantResult(ThinkResult result, Pawn pawn, JobIssueParams issueParams);
    public virtual ThinkResult Notify_DutyResult(ThinkResult result, Pawn pawn, JobIssueParams issueParams);
    public virtual string GetJobReport(Pawn pawn);
    public virtual string GetReport(Pawn pawn);
    public virtual bool CanOpenAnyDoor(Pawn p);
    public virtual bool ShouldRemovePawn(Pawn p, PawnLostCondition reason);
    public virtual IEnumerable<Gizmo> GetPawnGizmos(Pawn p);
    public virtual bool EndPawnJobOnCleanup(Pawn p);
    public virtual bool BlocksSocialInteraction(Pawn pawn);
    public virtual bool DutyActiveWhenDown(Pawn pawn);
    public virtual AcceptanceReport AllowsFloatMenu(Pawn pawn);
    public virtual bool PrisonerSecure(Pawn pawn);
    public virtual AcceptanceReport AbilityAllowed(Ability ability);
    public virtual AcceptanceReport AllowsDrafting(Pawn pawn);
    public virtual bool ValidateAttackTarget(Pawn searcher, Thing target);
    public virtual void Dispose();

    // properties (all virtual, getter-only unless noted)
    public virtual bool LostImportantReferenceDuringLoading { get; }
    public virtual bool AllowStartNewGatherings { get; }
    public virtual bool AllowStartNewRituals { get; }
    public virtual bool NeverInRestraints { get; }
    public virtual bool GuiltyOnDowned { get; }
    public virtual bool CanBlockHostileVisitors { get; }
    public virtual bool AddFleeToil { get; }               // ***default true*** — see §7.4, base Lord.SetJob auto-injects LordToil_PanicFlee when this is true
    public virtual bool OrganizerIsStartingPawn { get; }
    public virtual bool KeepExistingWhileHasAnyBuilding { get; }
    public virtual bool AlwaysShowWeapon { get; }
    public virtual bool IsCaravanSendable { get; }
    public virtual bool ManagesRopableAnimals { get; }
    public virtual bool DontInterruptLayingPawnsOnCleanup { get; }
    public virtual bool CanAutoAddPawns { get; }
    public virtual bool ShouldExistWithoutPawns { get; }
    public Map Map { get; }                                // = lord.lordManager.map, non-virtual
}
```

### `Verse.AI.Group.LordToil` (abstract)

```csharp
public abstract class LordToil : IDisposable
{
    public Lord lord;
    public LordToilData data;          // null unless you assign one — see §7.3
    public bool useAvoidGrid;

    public abstract void UpdateAllDuties();      // MUST implement — this is where you assign PawnDuty to lord.ownedPawns
    public virtual void Init();
    public virtual void LordToilTick();          // called every tick from Lord.LordTick() while this is CurLordToil
    public virtual void Cleanup();
    public virtual bool CanAddPawn(Pawn p);
    public virtual bool ShouldFail { get; }
    public virtual bool AssignsDuties { get; }
    public virtual bool AllowAggressiveTargetingOfRoamers { get; }
    public virtual bool AllowRestingInBed { get; }
    public virtual bool AllowSatisfyLongNeeds { get; }
    public virtual bool AllowSelfTend { get; }
    public virtual bool ForceHighStoryDanger { get; }
    public virtual float? CustomWakeThreshold { get; }
    public virtual IntVec3 FlagLoc { get; }
    public Map Map { get; }                       // non-virtual
    public void AddFailCondition(Func<bool> failCondition);
    public virtual IEnumerable<FloatMenuOption> ExtraFloatMenuOptions(Pawn target, Pawn forPawn);
    public virtual IEnumerable<Gizmo> GetPawnGizmos(Pawn p);
    public virtual IEnumerable<Gizmo> GetBuildingGizmos(Building building);
    public virtual ThinkTreeDutyHook VoluntaryJoinDutyHookFor(Pawn p);
    public virtual void DrawPawnGUIOverlay(Pawn pawn);
    public virtual void Notify_PawnAcquiredTarget(Pawn detector, Thing newTarg);
    public virtual void Notify_PawnDamaged(Pawn victim, DamageInfo dinfo);
    public virtual void Notify_PawnJobDone(Pawn p, JobCondition condition);
    public virtual void Notify_PawnLost(Pawn victim, PawnLostCondition cond);
    public virtual void Notify_ReachedDutyLocation(Pawn pawn);
    public virtual void Notify_BuildingSpawnedOnMap(Building b);
    public virtual void Notify_BuildingDespawnedOnMap(Building b);
    public virtual void Notify_BuildingLost(Building b);
    public virtual void Notify_CorpseLost(Corpse c);
    public virtual void Notify_ConstructionCompleted(Pawn pawn, Building building);
    public virtual void Notify_ConstructionFailed(Pawn pawn, Frame frame, Blueprint_Build newBlueprint);
    public void Dispose();
}
```

### `Verse.AI.Group.Trigger` (abstract) / `TriggerFilter` (abstract) / `TriggerSignal` (struct)

```csharp
public abstract class Trigger
{
    public TriggerData data;                     // null unless the trigger needs persistent progress state
    public List<TriggerFilter> filters;

    public abstract bool ActivateOn(Lord lord, TriggerSignal signal);   // MUST implement — called EVERY TICK, see §7.2
    public virtual void SourceToilBecameActive(Transition transition, LordToil previousToil);
}

public abstract class TriggerFilter
{
    public abstract bool AllowActivation(Lord lord, TriggerSignal signal);
}
// concrete: TriggerFilter_MapExitable (only allows activation if Map.CanEverExit / can currently exit)

public struct TriggerSignal
{
    public TriggerSignalType type;
    public string memo;                 // set when type==Memo
    public Signal signal;                // set when type==Signal
    public Pawn Pawn { get; }
    public Thing thing, otherThing;
    public DamageInfo dinfo;
    public PawnLostCondition condition;
    public Faction faction;
    public FactionRelationKind? previousRelationKind;
    public ClamorDef clamorType;

    public static TriggerSignal ForTick { get; }             // type=Tick, sent every Lord.LordTick()
    public static TriggerSignal ForMemo(string memo);
    public static TriggerSignal ForSignal(Signal signal);
}

public enum TriggerSignalType : byte {
    Undefined, Tick, Memo, PawnDamaged, PawnArrestAttempted, PawnLost, BuildingDamaged, BuildingLost,
    FactionRelationsChanged, DormancyWakeup, Clamor, MechClusterDefeated, Signal, CorpseLost, AcquiredTarget
}

public abstract class TriggerData : IExposable { public abstract void ExposeData(); }
public class TriggerData_TicksPassed : TriggerData { public int ticksPassed; public override void ExposeData(); }
```

### `Verse.AI.Group.StateGraph` / `Transition`

```csharp
public class StateGraph {
    public List<LordToil> lordToils;
    public List<Transition> transitions;
    public LordToil StartingToil { get; set; }     // first AddToil()'d toil unless you set this explicitly — UNVERIFIED default-assignment mechanics; set it explicitly to be safe
    public void AddToil(LordToil toil);
    public void AddTransition(Transition transition, bool highPriority);
    public StateGraph AttachSubgraph(StateGraph subGraph);   // merges subGraph's toils/transitions into this one, returns subGraph (so you chain .StartingToil off the RETURN value, not off subGraph itself, per vanilla usage)
    public void ErrorCheck();
}

public class Transition {
    public List<LordToil> sources;
    public LordToil target;
    public List<Trigger> triggers;
    public List<TransitionAction> preActions, postActions;
    public bool canMoveToSameState, updateDutiesIfMovedToSameState;

    public Transition(LordToil firstSource, LordToil target, bool canMoveToSameState, bool updateDutiesIfMovedToSameState);
    public void AddSource(LordToil source);
    public void AddSources(IEnumerable<LordToil> sources);
    public void AddSources(params LordToil[] sources);
    public void AddTrigger(Trigger trigger);
    public void AddPreAction(TransitionAction action);
    public void AddPostAction(TransitionAction action);
    public bool CheckSignal(Lord lord, TriggerSignal signal);   // ORs together trigger.ActivateOn(...) results, each gated by that trigger's own filters
}
```

### `Verse.AI.Group.LordManager` / `LordMaker` / `Lord` — the spawn/tick/save pipeline

```csharp
public static class LordMaker {           // sealed static, exactly ONE method
    public static Lord MakeNewLord(Faction faction, LordJob lordJob, Map map, [opt] IEnumerable<Pawn> startingPawns = null);
}
```
`MakeNewLord` IL, reconstructed: `new Lord(); lord.loadID = UniqueIDsManager.GetNextLordID(); lord.faction
= faction; map.lordManager.AddLord(lord); if (startingPawns != null) lord.AddPawns(startingPawns, false);
lord.SetJob(lordJob, false); lord.GotoToil(lord.Graph.StartingToil); if (startingPawns != null) { ... }` —
i.e. `SetJob` builds the graph (via `lordJob.CreateGraph()`) and only then do pawns get moved onto its
starting toil.

`Lord.LordTick()` (called once per game tick per Lord, from `Map`'s tick loop — reconstructed):

```csharp
void LordTick() {
    // every 60 ticks: sanity-check no owned pawn is a "free world pawn" (log-only)
    if (!initialized) Init();
    curJob.LordJobTick();
    curLordToil.LordToilTick();
    CheckTransitionOnSignal(TriggerSignal.ForTick);   // <-- every trigger on every transition sourced from curLordToil gets ActivateOn() called, EVERY TICK
    ticksInToil++;
}
```

`Lord.SetJob(LordJob lordJob, bool loading)`: **automatically appends a `LordToil_PanicFlee` toil and a
high-priority transition from every existing toil to it**, triggered by
`Trigger_FractionPawnsLost(faction.def.attackersDownPercentageRangeForAutoFlee.RandomInRangeSeeded(lord.loadID))`
— **but only if all of**: `faction != null`, `!faction.IsPlayer`, `faction.def.autoFlee == true`,
`!faction.neverFlee`, `lordJob.AddFleeToil == true` (default true, per §3), and `Map.CanEverExit`. See §7.4
— **every vanilla player-hostile `FactionDef` checked has `autoFlee` unset (defaults `false`)**, so this
mechanism is currently dormant for normal raids; don't assume it's doing anything for you.

`Lord.ExposeData()` (confirmed via IL, `[newslot virtual final]` — **cannot be overridden further**, but
your `LordJob.ExposeData()` IS called from inside it):

```csharp
void ExposeData() {
    // (Saving-mode only) prune destroyed things from extraForbiddenThings/ownedPawns/ownedBuildings/ownedCorpses
    Scribe_Values.Look(ref loadID, "loadID");
    Scribe_References.Look(ref faction, "faction");
    Scribe_Collections.Look(ref extraForbiddenThings, "extraForbiddenThings", LookMode.Reference);
    Scribe_Collections.Look(ref ownedPawns, "ownedPawns", LookMode.Reference);
    Scribe_Collections.Look(ref ownedBuildings, "ownedBuildings", LookMode.Reference);
    Scribe_Collections.Look(ref ownedCorpses, "ownedCorpses", LookMode.Reference);
    Scribe_Deep.Look(ref curJob, "lordJob");            // <-- THIS calls your subclass's ExposeData() polymorphically
    Scribe_Values.Look(ref initialized, "initialized", true);
    Scribe_Values.Look(ref ticksInToil, "ticksInToil");
    Scribe_Values.Look(ref numPawnsEverGained, "numPawnsEverGained");
    Scribe_Values.Look(ref numPawnsLostViolently, "numPawnsLostViolently");
    Scribe_Values.Look(ref initialColonyHealthTotal, "initialColonyHealthTotal");
    Scribe_Values.Look(ref lastPawnHarmTick, "lastPawnHarmTick", int.MinValue + something);
    Scribe_Values.Look(ref inSignalLeave, "inSignalLeave");
    Scribe_Collections.Look(ref questTags, "questTags");
    // (PostLoadInit only) re-key ownedPawns/etc against loaded refs...
    ExposeData_StateGraph();
}

void ExposeData_StateGraph() {
    // builds tmpLordToilData: Dictionary<int toilIndex, LordToilData> from graph.lordToils[i].data (skips null .data)
    // builds tmpTriggerData: Dictionary<int flatTriggerIndex, TriggerData> flattened across ALL transitions'
    //   triggers in graph.transitions order, then each transition's triggers order (skips null .data)
    // tmpCurLordToilIdx = graph.lordToils.IndexOf(curLordToil)
    Scribe_Collections.Look(ref tmpLordToilData, "lordToilData", LookMode.Value, LookMode.Deep);
    Scribe_Collections.Look(ref tmpTriggerData, "triggerData", LookMode.Value, LookMode.Deep);
    Scribe_Values.Look(ref tmpCurLordToilIdx, "curLordToilIdx", -1);

    // (PostLoadInit only):
    if (curJob.LostImportantReferenceDuringLoading) { lordManager.RemoveLord(this); return; }
    var loadedJob = curJob; curJob = null;
    SetJob(loadedJob, loading: true);          // <-- REBUILDS THE GRAPH by calling loadedJob.CreateGraph() AGAIN, using the just-restored field values
    // for each (idx, data) in tmpLordToilData: graph.lordToils[idx].data = data   (errors logged if idx out of range)
    // for each (idx, data) in tmpTriggerData: GetTriggerByIndex(idx).data = data  (same flattening as above; errors logged if idx out of range)
    if (tmpCurLordToilIdx in range) curLordToil = graph.lordToils[tmpCurLordToilIdx];
}
```

**This is the single most important mechanic for §7.3/§7.5**: on load, `curJob.ExposeData()` (your
subclass) runs FIRST (restoring your custom fields), and **only then** does `SetJob(loadedJob, true)` call
`CreateGraph()` again to rebuild the toil/transition graph from scratch, after which saved
`LordToilData`/`TriggerData`/`curLordToilIdx` are matched back onto it **purely by list index** — there is
no per-toil/per-trigger identity preserved across save/load other than position. See §7.5 for the
determinism consequences.

---

## 4. Constructor signatures — every `LordToil_*` / `Trigger_*` you'd plausibly use

All confirmed via `monop`. Note namespaces carefully — some are `RimWorld`, some `Verse.AI.Group`.

```csharp
// ---- LordToil_* ----
RimWorld.LordToil_AssaultColony(bool attackDownedIfStarving, bool canPickUpOpportunisticWeapons);
RimWorld.LordToil_StealCover();                          // subclass of LordToil_DoOpportunisticTaskOrCover, see §2
RimWorld.LordToil_ExitMapAndEscortCarriers();             // no-arg; escorts pawns whose IsAnyDefendingPosition/IsDefendingPosition indicates they're guarding carriers — full behavior not traced, UNVERIFIED beyond ctor/statics
Verse.AI.Group.LordToil_ExitMap(LocomotionUrgency locomotion, bool canDig, bool interruptCurrentJob);
Verse.AI.Group.LordToil_ExitMapFighting();                // confirmed to exist; ctor/behavior not traced — UNVERIFIED beyond existence
Verse.AI.Group.LordToil_ExitMapRandom();                  // confirmed to exist; not traced — UNVERIFIED beyond existence
Verse.AI.Group.LordToil_ExitMapNear(IntVec3 nearPoint, ...);  // exists; exact ctor NOT verified this session

// no LordToil_ExitMapTrapsOrSpots exists in 1.6 — that name in your prompt doesn't match any real type (checked the full ~55-entry RimWorld.LordToil_* list and the Verse.AI.Group set; nearest relatives are LordToil_ExitMapRandom/_ExitMapNear/_ExitMapFighting and RimWorld.LordToil_ExitMapAndDefendSelf/_ExitMapAndEscortCarriers)

// ---- Trigger_* ----
Verse.AI.Group.Trigger_FractionPawnsLost(float fraction);                         // e.g. 0.2–0.3 in vanilla LordJob_DefendBase/_Siege/_SitePawns/_StageThenAttack
Verse.AI.Group.Trigger_TicksPassed(int tickLimit);
Verse.AI.Group.Trigger_TicksPassedAndNoRecentHarm(int tickLimit);                 // subclass of the above; used by LordJob_Steal's cover-swap (1200 ticks)
Verse.AI.Group.Trigger_PawnHarmed(float chance, bool requireInstigatorWithFaction,
    Faction requireInstigatorWithSpecificFaction, DutyDef skipDuty, int? minTicks);
Verse.AI.Group.Trigger_Memo(string memo);                                          // paired with Lord.ReceiveMemo(string) / TriggerSignal.ForMemo
Verse.AI.Group.Trigger_PawnLost(PawnLostCondition condition, Pawn pawn);           // condition=Undefined,pawn=null used in vanilla to mean "any loss"
Verse.AI.Group.Trigger_BecameNonHostileToPlayer();
Verse.AI.Group.Trigger_NoFightingSappers();
Verse.AI.Group.TriggerFilter_MapExitable();                                        // used via the WithFilter() extension, not a Trigger itself
RimWorld.Trigger_KidnapVictimPresent();
RimWorld.Trigger_FractionColonyDamageTaken(float desiredColonyDamageFraction, float minDamage);
RimWorld.Trigger_HighValueThingsAround();                                          // see §7.2 for its exact cheap/throttled ActivateOn — great template
RimWorld.Trigger_GameEnding(int triggerAfterTicks);
RimWorld.Trigger_WoundedGuestPresent();                                            // exists (raids.md listed it); not traced this session

// extension method used throughout vanilla to gate a trigger on map-exitability:
static Trigger Verse.AI.Group.TriggerFilterExtension.WithFilter(this Trigger t, TriggerFilter f);
```

`Trigger_FractionColonyDamageTaken` and `Trigger_HighValueThingsAround` weren't in your requested list but
are directly relevant (satisfaction-based retreat, loot-triggered state change) and are exactly what
`LordJob_AssaultColony` itself uses — worth reusing rather than reinventing.

---

## 5. Duties

### `RimWorld.DutyDefOf` — full field list (confirmed via `monop`)

```
TravelOrLeave, TravelOrWait, Kidnap, Steal, TakeWoundedGuest, Follow, PrisonerEscape, PrisonerEscapeSapper,
DefendAndExpandHive, DefendHiveAggressively, LoadAndEnterTransporters, EnterTransporterAndDefendSelf,
LoadAndEnterPortal, ManClosestTurret, SleepForever, Idle, IdleNoInteraction, WanderClose, WanderClose_NoNeeds,
[Anomaly] ShamblerSwarm, SightstealerSwarm, SightstealerAssault, GorehulkAssault, DevourerAssault,
  FleshbeastAssault, PerformHateChant, ChimeraStalkFlee, ChimeraStalkWander, ChimeraAttack,
  DefendFleshmassHeart, VoidAwakeningWander,
[Odyssey] WanderNest, NestAssault,
AssaultColony, Breaching, Sapper, Escort, Defend, [Anomaly] DefendInvoker, Build, HuntEnemiesIndividual,
DefendBase, [Royalty] AssaultThing, PrisonerAssaultColony, HuntDownColonists, ExitMapRandom, ExitMapBest,
ExitMapBestAndDefendSelf, ExitMapNearDutyTarget, MarryPawn, GiveSpeech, Spectate, BestowingCeremony_MoveInPlace,
[Royalty] Bestow, [Ideology] Pilgrims_Spectate, PlayTargetInstrument, [Biotech] SocialMeeting,
PrepareCaravan_GatherItems, PrepareCaravan_Wait, PrepareCaravan_GatherAnimals, PrepareCaravan_CollectAnimals,
PrepareCaravan_GatherDownedPawns, PrepareCaravan_Pause, ReturnedCaravan_PenAnimals,
[Anomaly] Goto, Goto_NoZeroLengthPaths, Invoke, PsychicRitualDance, WaitForRitualParticipants,
  DeliverPawnToPsychicRitualCell, GatherOfferingsForPsychicRitual
```

### `Verse.AI.DutyDef` — full field list (confirmed via `monop`)

```csharp
public class DutyDef : Def {
    public ThinkNode thinkNode;              // the per-pawn priority-list of JobGivers evaluated while this duty is active
    public ThinkNode constantThinkNode;       // evaluated on EVERY think cycle regardless of priority (e.g. "pick up opportunistic weapon")
    public bool alwaysShowWeapon;
    public ThinkTreeDutyHook hook;
    public RandomSocialMode socialModeMax;
    public bool threatDisabled;
    public bool ritualSpectateTarget;
    public bool forceFaceUpPosture;
    public bool? drawBodyOverride;
    // ... standard Def fields (defName, label, description, modExtensions, etc.)
}
```

**Important correction to how you framed Q3**: `DutyDef` has **no** `focus`/`focusSecond`/`radius` fields —
those live on the *runtime* `Verse.AI.PawnDuty` object, not the XML-authored `DutyDef`:

```csharp
public class PawnDuty : IExposable {
    public DutyDef def;
    public LocalTargetInfo focus, focusSecond, focusThird;
    public float radius;
    public LocomotionUrgency locomotion;
    public Danger maxDanger;
    public bool canDig;
    public bool attackDownedIfStarving;
    public float? wanderRadius;
    public bool pickupOpportunisticWeapon;
    public Rot4 overrideFacing;
    public ILoadReferenceable source;
    public string tag;
    // + spectate-related fields (spectateRect, spectateDistance, etc.), transportersGroup, ropeeLimit
    public PawnDuty(DutyDef def);
    public PawnDuty(DutyDef def, LocalTargetInfo focus, float radius);
    public PawnDuty(DutyDef def, LocalTargetInfo focus, LocalTargetInfo focusSecond, float radius);
    public PawnDuty(DutyDef def, LocalTargetInfo focus, LocalTargetInfo focusSecond, LocalTargetInfo focusThird, float radius);
}
```

So: a `DutyDef`'s XML only wires up **which `ThinkNode`/`JobGiver`s run** (i.e., *what kind of behavior*),
while `focus`/`radius`/etc are **set in code by whatever `LordToil.UpdateAllDuties()` constructs the
`PawnDuty`** (e.g. `new PawnDuty(DutyDefOf.Steal)` — no focus at all in vanilla's own steal-cover toil; the
`JobGiver_Steal` ThinkNode figures out its own target independently via `StealAIUtility`, see §6). If you
want a per-pawn focus point, you set it yourself when constructing the `PawnDuty` in your own
`LordToil.UpdateAllDuties()` override.

### Verbatim vanilla `DutyDef` XML (from `Data/Core/Defs/DutyDefs/Duties_Misc.xml`)

```xml
<DutyDef>
  <defName>AssaultColony</defName>
  <alwaysShowWeapon>true</alwaysShowWeapon>
  <thinkNode Class="ThinkNode_Priority">
    <subNodes>
      <li Class="JobGiver_TakeCombatEnhancingDrug" />
      <li Class="ThinkNode_Subtree"><treeDef>Abilities_Aggressive</treeDef></li>
      <li Class="JobGiver_AIFightEnemies">
        <targetAcquireRadius>65</targetAcquireRadius>
        <targetKeepRadius>72</targetKeepRadius>
      </li>
      <li Class="JobGiver_AITrashColonyClose" />
      <li Class="JobGiver_AITrashBuildingsDistant" />
      <li Class="JobGiver_AIGotoNearestHostile" />
      <li Class="JobGiver_AITrashBuildingsDistant"><attackAllInert>true</attackAllInert></li>
      <li Class="JobGiver_AISapper"><canMineNonMineables>false</canMineNonMineables></li>
    </subNodes>
  </thinkNode>
  <constantThinkNode Class="ThinkNode_ConditionalCanPickupOpportunisticWeapon">
    <subNodes>
      <li Class="JobGiver_PickUpOpportunisticWeapon" />
    </subNodes>
  </constantThinkNode>
</DutyDef>

<DutyDef>
  <defName>Escort</defName>
  <alwaysShowWeapon>true</alwaysShowWeapon>
  <thinkNode Class="ThinkNode_Priority">
    <subNodes>
      <li Class="JobGiver_TakeCombatEnhancingDrug"><onlyIfInDanger>true</onlyIfInDanger></li>
      <li Class="JobGiver_AIDefendEscortee">
        <targetAcquireRadius>65</targetAcquireRadius>
        <targetKeepRadius>72</targetKeepRadius>
      </li>
      <li Class="JobGiver_AIFollowEscortee"/>
      <li Class="ThinkNode_Subtree"><treeDef>SatisfyVeryUrgentNeeds</treeDef></li>
      <li Class="JobGiver_WanderNearDutyLocation"><wanderRadius>8</wanderRadius></li>
    </subNodes>
  </thinkNode>
</DutyDef>

<DutyDef>
  <defName>Kidnap</defName>
  <thinkNode Class="ThinkNode_Priority">
    <subNodes>
      <li Class="JobGiver_Kidnap" />
      <li Class="JobGiver_ExitMapBest">
        <defaultLocomotion>Jog</defaultLocomotion>
        <jobMaxDuration>200</jobMaxDuration>
        <forceCanDigIfCantReachMapEdge>true</forceCanDigIfCantReachMapEdge>
      </li>
    </subNodes>
  </thinkNode>
</DutyDef>

<DutyDef>
  <defName>Steal</defName>
  <thinkNode Class="ThinkNode_Priority">
    <subNodes>
      <li Class="JobGiver_Steal" />
      <li Class="JobGiver_ExitMapBest">
        <defaultLocomotion>Jog</defaultLocomotion>
        <jobMaxDuration>200</jobMaxDuration>
        <forceCanDig>true</forceCanDig>
      </li>
    </subNodes>
  </thinkNode>
</DutyDef>
```

(`ExitMapRandom`/`ExitMapBest`/`ExitMapBestAndDefendSelf` DutyDefs also live in this file at lines
509–525 — thin wrappers, mostly just `JobGiver_ExitMapRandom`/`JobGiver_ExitMapBest` with tuning params;
ask if you need them verbatim too.)

**Design implication**: the `Steal` `DutyDef`'s `ThinkNode_Priority` is exactly the shape you'd copy for a
custom `FDLootFood`/`FDLootValuables` DutyDef — swap `JobGiver_Steal` for your own `JobGiver_FDLoot*`
subclass (see §6) that restricts the target search by category, keep the same `JobGiver_ExitMapBest`
fallback so pawns carry their loot to the map edge once nothing more is worth grabbing.

---

## 6. The steal pipeline, traced end-to-end

Confirmed via IL, mechanism is **duty-driven** (via `ThinkNode`s reached from `PawnDuty.def`), with the
`LordToil` layer only responsible for **which pawns get switched onto the `Steal` duty and when** — not for
picking targets itself:

1. **`LordToil_DoOpportunisticTaskOrCover.LordToilTick()`** (throttled to every 181 ticks, only while
   `cover == true`): for each owned pawn still on `DutyDefOf.AssaultColony`, calls
   `TryFindGoodOpportunisticTaskTarget` → `StealAIUtility.TryFindBestItemToSteal(pawn.Position, pawn.Map,
   maxDist:7f, out item, pawn, alreadyTakenTargets)`. If found and not reserved by another
   faction-member and pawn isn't `GenAI.InDangerousCombat`, the pawn's `PawnDuty` is swapped to
   `new PawnDuty(DutyDefOf.Steal)` and its current job ended (so the duty-driven `ThinkNode` re-evaluates
   next tick).
2. **`RimWorld.StealAIUtility.TryFindBestItemToSteal(IntVec3 root, Map map, float maxDist, out Thing item,
   Pawn thief, List<Thing> disallowed)`** — confirmed signature and reconstructed body:
   - Bails if `thief` can't manipulate (`PawnCapacityDefOf.Manipulation`) or can't reach the map edge.
   - Searches `ThingRequest.ForGroup(ThingRequestGroup.HaulableEverOrMinifiable)` via
     `GenClosest.ClosestThing_Regionwise_ReachablePrioritized(root, map, request, PathEndMode, TraverseParms,
     maxDist, predicate, priorityGetter, searchRegionsMin:15, searchRegionsMax:15, ...)` — i.e. it's a
     **region-wise search prioritized by a `Func<Thing,float>` getter** (almost certainly
     `StealAIUtility.GetValue`, based on call ordering — not independently re-verified byte-for-byte), not
     a pure nearest-match. So it already balances "close" against "valuable" within `maxDist`.
   - The predicate: `pawn.CanReserve(thing) && !disallowed.Contains(thing) && thing.def.stealable &&
     !thing.IsBurning()`. **`ThingDef.stealable` is the only per-item eligibility flag that exists** — it's
     a binary "can this ever be stolen" switch (most ThingDefs default true), **not a category filter**.
     There is no "only food" / "only above value X" parameter anywhere in this call.
3. **The `Steal` `DutyDef`'s `ThinkNode_Priority`**: `JobGiver_Steal` runs first. Its `TryGiveJob`
   (reconstructed): finds an exit spot via `RCellFinder.TryFindBestExitSpot`, then independently calls
   `StealAIUtility.TryFindBestItemToSteal(pawn.Position, pawn.Map, maxDist:12f, out item, pawn, null)` (a
   *second*, wider-radius, independent lookup — not reusing whatever `LordToil` found), and if a target
   exists and the pawn isn't in dangerous combat, builds:
   ```csharp
   Job job = JobMaker.MakeJob(JobDefOf.Steal);
   job.targetA = item;
   job.targetB = exitSpot;
   job.count = Mathf.Min(item.stackCount,
       (int)(pawn.GetStatValue(StatDefOf.CarryingCapacity) / item.def.VolumePerUnit));
   ```
   i.e. **the amount actually stolen is capped by the pawn's `CarryingCapacity` stat divided by the
   item's per-unit volume** — this is your natural "how much can one raider carry" knob, already exposed
   as a normal stat you can read/modify.
4. If no steal target remains, `JobGiver_ExitMapBest` (the duty's second priority node) takes over and
   walks the pawn off the map.

**Direct answer to your Q4**: the pickup-and-carry-off behavior is **duty-driven** (a `ThinkNode_JobGiver`
subclass reached via `PawnDuty.def.thinkNode`), with the `LordToil` only deciding *which pawns* get
switched onto that duty and *when* (throttled polling, not per-tick). **No existing vanilla field lets you
restrict a duty (or `StealAIUtility.TryFindBestItemToSteal`) to a category of item** — food vs. valuables
vs. anything else. To get "raiders only grab food" or "raiders only grab silver/valuables," you need:

- A **custom `JobGiver_FDSteal : ThinkNode_JobGiver`** (copy `JobGiver_Steal`'s shape) that calls your own
  target-finder instead of `StealAIUtility.TryFindBestItemToSteal` — e.g. a copy of that method's
  `GenClosest.ClosestThing_Regionwise_ReachablePrioritized` call with your own predicate
  (`thing.def.IsNutritionGivingIngestible` / `thing.def.category == ThingCategory.Item &&
  thing.MarketValue >= threshold`, etc., still AND'd with `pawn.CanReserve` / `!IsBurning` /
  `thing.def.stealable`).
- A **custom `DutyDef`** (`FDLootFood`, `FDLootValuables`) whose `thinkNode` uses your `JobGiver_FDSteal`
  in place of `JobGiver_Steal`, keeping `JobGiver_ExitMapBest` as the fallback (copy the verbatim XML
  shape from §5).
- Everything else (the `LordToil_StealCover`-style duty-switching, the `JobDefOf.Steal`/carry-off
  `JobDriver`, the carrying-capacity cap) can be reused as-is — you don't need a custom `JobDriver`.

---

## 7. Recommended design — `LordJob_FDMotivatedRaid`

### 7.1 Class shape

**Don't** subclass `LordJob_AssaultColony` — its fields (`assaulterFaction`, `canSteal`, etc.) are
`private` with no protected accessors or virtual hooks into `CreateGraph()` (it's not built from smaller
overridable pieces — the whole method is one monolithic block, per §1), so you can't cleanly graft custom
loot-category/threshold behavior onto it. Instead: write a standalone

```csharp
public class LordJob_FDMotivatedRaid : LordJob
{
    // --- scribed fields (see 7.3) ---
    private Faction assaulterFaction;
    private FDRaidMotivationDef motivation;      // your own Def: Starvation / ResourceTheft / Revenge
    private float lootValueGoal;                 // e.g. total market value of food/valuables to carry off
    private float lootValueCarried;               // running total, updated by our custom trigger's cheap poll
    private float fleeAtLossFraction;              // per-motivation: low for Starvation/Theft, high for Revenge
    private bool goalMet;                          // latched once true, avoids re-summing every check

    public LordJob_FDMotivatedRaid() { }           // required parameterless ctor for Scribe_Deep
    public LordJob_FDMotivatedRaid(Faction faction, FDRaidMotivationDef motivation, float lootValueGoal, float fleeAtLossFraction)
    { assaulterFaction = faction; this.motivation = motivation; this.lootValueGoal = lootValueGoal; this.fleeAtLossFraction = fleeAtLossFraction; }

    public override bool AddFleeToil => false;      // see 7.4 — we hand-roll our own losses trigger; don't double up with the (currently-dormant) vanilla auto-flee

    public override StateGraph CreateGraph() { ... }   // 7.2
    public override void ExposeData() { ... }          // 7.3
}
```

### 7.2 `CreateGraph()` sketch (real API calls)

```csharp
public override StateGraph CreateGraph()
{
    var graph = new StateGraph();

    var assault = new LordToil_AssaultColony(attackDownedIfStarving: false, canPickUpOpportunisticWeapons: false);
    graph.AddToil(assault);
    graph.StartingToil = assault;

    // custom loot toil: reuses the vanilla cover-swap SHAPE from LordToil_StealCover, but with our
    // own DutyDef (FDLootFood / FDLootValuables per motivation) and our own JobGiver behind it (see §6)
    var loot = new LordToil_FDLoot(motivation);       // your own LordToil_DoOpportunisticTaskOrCover subclass
    graph.AddToil(loot);

    var exit = new LordToil_ExitMap(LocomotionUrgency.Jog, canDig: false, interruptCurrentJob: true);
    exit.useAvoidGrid = true;
    graph.AddToil(exit);

    // assault -> loot, once there's enough loot around to bother (mirror Trigger_HighValueThingsAround's shape)
    var toLoot = new Transition(assault, loot, false, true);
    toLoot.AddTrigger(new Trigger_FDLootAvailable(motivation));      // your own cheap Trigger, §7.2b
    graph.AddTransition(toLoot, false);

    // EITHER state -> exit, once our custom goal-met check fires
    var toExitGoal = new Transition(assault, exit, false, true);
    toExitGoal.AddSource(loot);
    toExitGoal.AddTrigger(new Trigger_FDGoalMet());                   // your own cheap Trigger, reads THIS LordJob's fields — §7.2b
    toExitGoal.WithFilter(new TriggerFilter_MapExitable());          // note: WithFilter is on Trigger not Transition — apply it to the trigger, then AddTrigger the result
    graph.AddTransition(toExitGoal, false);

    // EITHER state -> exit, on losses (per-motivation threshold — Starvation/Theft flee early, Revenge doesn't)
    var toExitLosses = new Transition(assault, exit, false, true);
    toExitLosses.AddSource(loot);
    toExitLosses.AddTrigger(new Trigger_FractionPawnsLost(fleeAtLossFraction));   // reuse the vanilla trigger verbatim — no need for a custom one
    graph.AddTransition(toExitLosses, false);

    // safety net: give-up timeout, same shape as vanilla AssaultColony
    var toExitTimeout = new Transition(assault, exit, false, true);
    toExitTimeout.AddSource(loot);
    var timeoutTrig = new Trigger_TicksPassed(new IntRange(2500, 4000).RandomInRange);
    toExitTimeout.AddTrigger(timeoutTrig.WithFilter(new TriggerFilter_MapExitable()));
    graph.AddTransition(toExitTimeout, false);

    return graph;
}
```

### 7.2b Making the goal check cheap

Confirmed from IL that **`Lord.LordTick()` runs every single game tick for every Lord**, and
`CheckTransitionOnSignal(TriggerSignal.ForTick)` calls **every `Trigger.ActivateOn()` on every transition
sourced from the pawn group's *current* toil, unconditionally, every tick** (no built-in throttling —
`Transition.CheckSignal` just loops `triggers` and calls `ActivateOn` on each, short-circuiting only on the
first `true`). At normal speed that's 60 calls/sec per active raid Lord, more at higher game speeds — so a
naive "iterate all raiders' inventories and sum market value" implementation in `ActivateOn` would run far
too often.

Vanilla's own `RimWorld.Trigger_HighValueThingsAround.ActivateOn` is the exact template to copy (confirmed
via IL, reconstructed verbatim):

```csharp
public override bool ActivateOn(Lord lord, TriggerSignal signal)
{
    if (signal.type != TriggerSignalType.Tick) return false;                 // cheapest possible gate first
    if (Find.TickManager.TicksGame % 120 != 0) return false;                 // throttle: only 1 in 120 ticks (2s)
    if (TutorSystem.TutorialMode) return false;
    if (Find.TickManager.TicksGame - lord.lastPawnHarmTick <= 300) return false;  // cheap int compare before the expensive part
    return StealAIUtility.TotalMarketValueAround(lord.ownedPawns) > StealAIUtility.StartStealingMarketValueThreshold(lord);
}
```

Apply the same pattern to `Trigger_FDGoalMet`:

```csharp
public class Trigger_FDGoalMet : Trigger
{
    public override bool ActivateOn(Lord lord, TriggerSignal signal)
    {
        if (signal.type != TriggerSignalType.Tick) return false;
        if (Find.TickManager.TicksGame % 60 != 0) return false;       // pick an interval; 60 ticks (1s) is plenty responsive
        if (!(lord.LordJob is LordJob_FDMotivatedRaid job)) return false;
        if (job.goalMet) return true;                                  // already latched, skip recompute
        // cheap running-total field maintained incrementally elsewhere (e.g. Notify_PawnJobDone hook,
        // or your custom LordToil polling only the pawns who just finished a Steal job) is far cheaper
        // than re-summing every raider's CarryTracker every 60 ticks — prefer that if you can.
        return job.lootValueCarried >= job.lootValueGoal;
    }
}
```

Prefer maintaining `lootValueCarried` **incrementally** (e.g. from a `LordJob.Notify_PawnJobDone` override,
which base `LordJob` already exposes as a virtual hook called by `Lord.Notify_PawnJobDone` — no Harmony
needed — check `pawn.carryTracker.CarriedThing` when a `JobDefOf.Steal` job completes) rather than
recomputing a full sum every poll; that turns the trigger's steady-state cost into a single field read.

### 7.3 `ExposeData()` — exactly what a custom `LordJob` must scribe

Base `LordJob.ExposeData()` is a **complete no-op** (confirmed via IL — empty method body, just `ret`).
Everything your subclass needs persisted, it must scribe itself:

```csharp
public override void ExposeData()
{
    base.ExposeData();   // no-op today, but harmless/future-proof to call
    Scribe_References.Look(ref assaulterFaction, "assaulterFaction");
    Scribe_Defs.Look(ref motivation, "motivation");
    Scribe_Values.Look(ref lootValueGoal, "lootValueGoal");
    Scribe_Values.Look(ref lootValueCarried, "lootValueCarried");
    Scribe_Values.Look(ref fleeAtLossFraction, "fleeAtLossFraction");
    Scribe_Values.Look(ref goalMet, "goalMet");
    // if you track claimed-target Things/Pawns in a List<T>, use Scribe_Collections with LookMode.Reference
}
```

What you get **for free**, and must NOT re-scribe yourself (handled by `Lord.ExposeData`/
`ExposeData_StateGraph`, per §3): `lord` back-reference, `faction` (on the `Lord`, separate from your own
`assaulterFaction` field if you keep one), `ownedPawns`/`ownedBuildings`/`ownedCorpses`, `curLordToilIdx`
(by index), any `LordToilData`/`TriggerData` your toils/triggers attach (also by index — you only need a
custom `LordToilData`/`TriggerData` subclass if a toil/trigger itself holds mutable per-instance state that
must outlive a save; **our design above keeps all persistent motivation state directly on the `LordJob`**,
so no custom `LordToilData`/`TriggerData` classes are needed at all — simpler and fewer failure modes).

**Critical consequence of `ExposeData_StateGraph`'s reload sequence** (§3): `CreateGraph()` is called
**again**, from scratch, during `PostLoadInit`, using only the field values your `ExposeData()` just
restored — and the resulting `lordToils`/`transitions` lists are matched back to saved
`LordToilData`/`TriggerData` **purely by list position**. That means:

- `CreateGraph()` **must produce toils and transitions in the exact same order every time**, as a pure
  function of your scribed fields (`motivation`, `assaulterFaction`, etc.) and static defs — no
  conditionals on transient state (current map contents, current tick, `Rand`, pawn list order beyond
  what's already implied by scribed fields).
- If `CreateGraph()`'s shape ever depends on something not scribed (e.g. "does this map currently have
  food stockpiles" to decide whether to add the loot toil at all), a reload could rebuild a
  **structurally different graph**, silently invalidating the saved `curLordToilIdx`/toil-data indices
  (logged as `Verse.Log.Error` — "Could not find lord toil..."/"...trigger index is out of bounds"). Always
  add every toil unconditionally and gate *behavior*, not *graph shape*, on runtime state.

### 7.4 The (dormant) auto-flee mechanism — don't let it surprise you

`Lord.SetJob` unconditionally tries to inject a `LordToil_PanicFlee` toil + high-priority
`Trigger_FractionPawnsLost(faction.def.attackersDownPercentageRangeForAutoFlee.RandomInRangeSeeded(lord.loadID))`
transition from *every* toil in the graph, gated on `faction.def.autoFlee == true` (among other conditions,
§3). **Checked every vanilla player-hostile `FactionDef` (`Factions_Misc.xml`) and every hidden/special one
(`Factions_Hidden.xml`) — none set `<autoFlee>` to `true`; several explicitly set it `false`; it defaults
`false` in C#.** So in vanilla, this mechanism never actually fires for normal raids today. Implications:

- If your motivated-raid factions are ordinary vanilla `FactionDef`s (Pirate, tribal, etc.), you don't need
  to worry about this mechanism interfering — it's inert.
- If a user's other mod (or a future vanilla patch) sets `autoFlee=true` on some faction, that faction's
  raiders would get a **second, uncontrolled** flee-on-losses transition layered on top of your own
  `Trigger_FractionPawnsLost(fleeAtLossFraction)` — using a *different*, faction-defined threshold you don't
  control, which could undercut a "Revenge" motivation's "fight to the near-last-pawn" intent. That's why
  §7.1 overrides `AddFleeToil => false` on our `LordJob` — it's a one-line, IL-confirmed way (§3: `Lord
  .SetJob` checks `lordJob.AddFleeToil` before injecting anything) to fully own loss-based retreat behavior
  regardless of what any faction's `autoFlee` says.

### 7.5 Determinism hazards (for RimWorld Multiplayer)

1. **`CreateGraph()` runs twice per raid lifetime in a load scenario**: once at spawn (inside a synced
   command — safe, matches every client), and once more during `PostLoadInit` when a save is loaded
   (§3/§7.3). Loading itself happens identically on every client from the same synced save file, so this
   is *probably* fine — but it means **any `Rand`/`Verse.Rand` call inside `CreateGraph()` executes an
   extra, otherwise-unnecessary time on every load**, and if that call's result affects only a `Trigger`'s
   *behavior* parameter (not the graph *shape*), it's silently overwritten by the reloaded `TriggerData`
   for that trigger's progress — but if the call feeds a threshold that ISN'T part of any `TriggerData`
   (e.g. `Trigger_TicksPassed`'s `tickLimit` itself, which is a private field, not part of
   `TriggerData_TicksPassed` — only `ticksPassed`, the *progress*, is scribed), **the threshold silently
   re-randomizes on every reload**. Vanilla `LordJob_AssaultColony` already does exactly this (`AssaultTimeBeforeGiveUp.RandomInRange`
   inside `CreateGraph()`) and evidently ships fine, so it's not fatal — but under MP, verify with an actual
   save/load-mid-raid test rather than assuming it's safe; `CreateGraph()` on load runs during
   `PostLoadInit`, and whether that executes inside MP's synced-command context (so all clients compute the
   identical `Rand` draw) or as unsynced per-client code (each client could draw a different, harmless
   value since it doesn't affect deserialized state) needs confirming against the actual `Multiplayer` mod
   behavior around save-load — **flagging as UNVERIFIED, needs an in-game MP test**.
   - **Mitigation**: prefer `FloatRange.RandomInRangeSeeded(lord.loadID)` (the exact pattern base `Lord
     .SetJob` itself uses for its `Trigger_FractionPawnsLost` threshold, §3/§7.4) over plain
     `Rand.Range`/`IntRange.RandomInRange` for anything computed inside `CreateGraph()` — it's
     reproducible from a stable, already-scribed integer (`lord.loadID`, itself saved via
     `Scribe_Values.Look(ref loadID, "loadID")`) rather than depending on the ambient `Rand` seed/call-count
     state at the moment `CreateGraph()` happens to run, which is exactly the kind of thing that diverges
     between "freshly spawned" and "just reloaded" call sites.
2. **No `HashSet`/`Dictionary` iteration observed** in any of the vanilla code paths reconstructed here
   (`Lord.ownedPawns` is a `List<Pawn>`, iterated by index throughout; `ExposeData_StateGraph`'s
   `Dictionary<int,...>` usage is only for save-file *storage*, never iterated in an order that affects
   simulation — it's looked up by known integer key). Follow the same discipline in your own
   `LordToil.UpdateAllDuties()`/`Trigger.ActivateOn()`/custom `JobGiver`: iterate `lord.ownedPawns` (a
   `List<Pawn>`) directly, never build a `HashSet<Pawn>`/`Dictionary<Pawn,...>` and iterate it if that
   iteration order affects which pawn gets a duty/job first.
3. **`GenClosest.ClosestThing_Regionwise_ReachablePrioritized`** (used by `StealAIUtility
   .TryFindBestItemToSteal`, §6) is RimWorld's standard region-based spatial search — deterministic given
   identical map/region state on every client (same class vanilla raids already rely on for stealing), so
   reusing it (or copying its call shape for a custom category-filtered version, §6) carries no additional
   determinism risk beyond what vanilla already accepts.
4. **Your `Trigger_FDGoalMet`/incremental-loot-tracking hook** (§7.2b) should update `lootValueCarried` from
   a **synced simulation path only** — e.g. `LordJob.Notify_PawnJobDone` (called from `Lord
   .Notify_PawnJobDone`, itself called from the pawn's `JobDriver` cleanup during normal, synced tick
   simulation) is safe; do **not** compute or mutate it from a UI/draw-tick context or any Harmony patch on
   a non-synced method, or clients could disagree on when the goal is met.

---

## 8. Substituting our `LordJob` into a vanilla-generated raid

### Confirmed call path (via IL, `IncidentWorker_RaidEnemy`/`RaidStrategyWorker` class bodies)

```
IncidentWorker_RaidEnemy.TryExecuteWorker(IncidentParms parms)          // override, confirmed per raids.md
  -> parms.raidStrategy.Worker.MakeLords(parms, pawns)                   // RaidStrategyDef.Worker property
       RaidStrategyWorker.MakeLords(IncidentParms parms, List<Pawn> pawns)   // virtual, base impl reconstructed:
         var groups = IncidentParmsUtility.SplitIntoGroups(pawns, parms.pawnGroups);
         int raidSeed = Rand.Int;
         foreach (var group in groups) {
             LordJob job = this.MakeLordJob(parms, map, group, raidSeed);   // *** abstract — every concrete RaidStrategyWorker_* implements this ***
             Lord lord = LordMaker.MakeNewLord(parms.faction, job, map, group);
             lord.inSignalLeave = parms.inSignalEnd;
             QuestUtility.AddQuestTag(lord, parms.questTag);
         }
```

`LordMaker` (confirmed, `sealed static`, **exactly one method**):

```csharp
public static Lord MakeNewLord(Faction faction, LordJob lordJob, Map map, [opt] IEnumerable<Pawn> startingPawns = null);
```

Vanilla's own `RaidStrategyWorker_ImmediateAttack.MakeLordJob` (confirmed via IL — the exact copy-paste
template for how `canSteal`/`canKidnap`/`canTimeoutOrFlee` flow from `IncidentParms` into the `LordJob`
constructor):

```csharp
protected override LordJob MakeLordJob(IncidentParms parms, Map map, List<Pawn> pawns, int raidSeed)
{
    if (parms.attackTargets is { Count: > 0 })
        return new LordJob_AssaultThings(parms.faction, parms.attackTargets, pointMultiplier: 1f, useAvoidGridSmart: false);

    if (parms.faction.HostileTo(Faction.OfPlayer))
        return new LordJob_AssaultColony(parms.faction, parms.canKidnap, parms.canTimeoutOrFlee,
            sappers: false, useAvoidGridSmart: false, parms.canSteal, breachers: false, canPickUpOpportunisticWeapons: false);

    // friendly-arrival fallback
    RCellFinder.TryFindRandomSpotJustOutsideColony(pawns[0].PositionHeld, map, out IntVec3 spot);
    return new LordJob_AssistColony(parms.faction, spot);
}
```

### Option (a): Harmony prefix on `LordMaker.MakeNewLord`

**Not recommended.** `LordMaker.MakeNewLord` is the single spawn point for **every** `Lord` in the game —
not just raids: caravan-formation jobs, ritual jobs, prepare-caravan jobs, quest-spawned lords, sieges,
trade caravans, all funnel through this one method (confirmed — it's the only method in the `LordMaker`
class, and grepping the assembly shows dozens of distinct `LordJob_*` types constructed right before calls
to it, from many unrelated systems). A prefix would need:

- A call-stack-scoped flag (e.g. `ThreadStatic bool` set by your `IncidentWorker_RaidEnemy` subclass around
  its call to `base.TryExecuteWorker`/`MakeLords`, cleared in a `finally`) to avoid hijacking unrelated
  `Lord` creation.
- Careful ordering against any other mod's patches on the same method (Harmony patch-order is a real
  cross-mod risk on a method this central).
- Extra scrutiny under RimWorld Multiplayer specifically: MP's own compatibility layer patches
  pawn/Lord-spawning machinery for sync/determinism purposes (ID allocation, `Rand` interception, etc.);
  adding your own prefix to one of the most heavily-used low-level spawn methods in the game increases the
  chance of an ordering conflict with those patches, for no benefit specific to raids.

### Option (b): custom `RaidStrategyDef` + `RaidStrategyWorker` subclass — recommended

No Harmony needed anywhere in this path. This mirrors the hook points `raids.md` already established for
the incident layer:

```csharp
public class RaidStrategyWorker_FDMotivated : RaidStrategyWorker
{
    protected override LordJob MakeLordJob(IncidentParms parms, Map map, List<Pawn> pawns, int raidSeed)
    {
        var motivation = FDMotivationUtility.PickMotivation(parms);          // your own logic
        float lootGoal = FDMotivationUtility.LootGoalFor(motivation, parms.points);
        float fleeFrac = motivation == FDRaidMotivationDefOf.Revenge ? 0.85f : 0.25f;
        return new LordJob_FDMotivatedRaid(parms.faction, motivation, lootGoal, fleeFrac);
    }
}
```

```xml
<RaidStrategyDef>
  <defName>FDMotivatedRaid</defName>
  <workerClass>RaidStrategyWorker_FDMotivated</workerClass>
  <selectionWeightPerPointsCurve><points><li>(0, 0)</li></points></selectionWeightPerPointsCurve>  <!-- 0 = never picked by the storyteller directly; you assign it explicitly, see below -->
  <arriveModes><li>EdgeWalkIn</li><li>EdgeDrop</li></arriveModes>
  <letterLabelEnemy>Raid</letterLabelEnemy>
  <arrivalTextEnemy>They are attacking immediately.</arrivalTextEnemy>
</RaidStrategyDef>
```

Then, from your `IncidentWorker_RaidEnemy` subclass (already the recommended base per `raids.md` §1),
override `ResolveRaidStrategy` to assign `parms.raidStrategy = RaidStrategyDefOf_FD.FDMotivatedRaid` (and
set `parms.canSteal`/`canKidnap`/`canTimeoutOrFlee` as you like — your own `MakeLordJob` can also just
ignore those and use your own motivation-driven fields instead, since you own the whole `LordJob`
construction).

**Why (b) is more robust**: it only touches code paths that already exist specifically to be
subclassed/replaced per-strategy (`RaidStrategyWorker.MakeLordJob` is literally `abstract` — every raid
strategy in the game already implements a version of exactly this method), doesn't add a Harmony patch to
a hot shared method used by non-raid systems, and can't interfere with anything else in the game or any
other mod's `LordMaker` usage. It's also the only option that keeps `CreateGraph()`'s determinism
requirements (§7.5) easy to reason about — your `LordJob` is constructed once, explicitly, with known
arguments, rather than being swapped in via a prefix whose exact timing relative to argument construction
you'd have to re-verify.

**MP-specific note**: since option (b) never touches `LordMaker`/`Lord` spawning machinery itself — only
supplies a different `LordJob` instance through the exact same vanilla call path RimWorld Multiplayer
already knows how to sync (it already syncs every vanilla raid strategy's `MakeLordJob` call) — there's no
new MP compatibility surface here beyond what §7.5 already covers for the `LordJob` itself.

---

## Tooling notes / caveats

- IL was dumped once via `ikdasm $HOME/refs/api/Assembly-CSharp.dll > /tmp/rw.il` (≈198MB, ~3.9M lines),
  greped/sliced by method/class name, and deleted after use — not left on disk.
- `monop` again hit the `Unity.Collections` `FileNotFoundException` mid-listing for several types
  (`LordJob`, `LordJob_AssaultColony`, `LordJob_Steal`, `RaidStrategyWorker`, etc.) — where that happened,
  the missing tail of the member list was recovered from the `ikdasm` dump directly (which doesn't hit this
  issue), cross-checked against `monodis --fields`.
- `RimWorld.Trigger_WoundedGuestPresent` and `LordToil_ExitMapAndEscortCarriers`'s/`LordToil_ExitMapNear`'s
  full behavior were confirmed to exist (ctors/statics for the latter) but **not traced through IL** this
  session — flagged inline as UNVERIFIED beyond their public signatures.
- `AssaultTimeBeforeGiveUp`/`SapTimeBeforeGiveUp`/`BreachTimeBeforeGiveUp`'s actual `IntRange` values were
  not literal IL constants (they're `static readonly`, built in a static constructor, not the constant
  table `monodis --constant` reads) — not pulled this session; only their existence, type, and usage site
  are confirmed.
- The identity of `StealAIUtility.TryFindBestItemToSteal`'s `Func<Thing,float> priorityGetter` lambda
  (almost certainly `StealAIUtility.GetValue`, per §6 point 2) was inferred from call-site ordering, not
  independently re-verified by reading the lambda body's own IL.
- Everything else in this document is a direct read of `~/refs/api/Assembly-CSharp.dll` (RimWorld 1.6) IL,
  `monop` member listings, or the user's live `RimWorld/Data/Core/Defs/{DutyDefs,FactionDefs}/*.xml` — no
  memory/guessing was used for any signature. Anything I could not directly verify is called out inline as
  "UNVERIFIED".
