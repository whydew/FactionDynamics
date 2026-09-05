# RimWorld 1.6 Raid Pipeline — API Cheat Sheet

All signatures below were extracted with `monop -r:Assembly-CSharp.dll <Type>` and, where `monop` crashed
(IKVM reflection choking on a missing `Unity.Collections` assembly reference partway through a type),
cross-checked/completed with `monodis --method|--fields|--param|--constant` against the same DLL
(`~/refs/api/Assembly-CSharp.dll`, RimWorld 1.6). XML was read verbatim from the user's live 1.6 install
(`Data/Core/Defs/Storyteller/*.xml`). Anything not directly confirmed this way is explicitly flagged
"UNVERIFIED".

---

## 1. IncidentWorker / IncidentWorker_Raid / IncidentWorker_RaidEnemy

### `RimWorld.IncidentWorker` (base of everything)

```csharp
public class IncidentWorker {
    public IncidentDef def;

    public IncidentWorker();

    public static void SendIncidentLetter(TaggedString baseLetterLabel, TaggedString baseLetterText,
        LetterDef baseLetterDef, IncidentParms parms, LookTargets lookTargets, IncidentDef def,
        params NamedArgument[] textArgs);

    public bool CanFireNow(IncidentParms parms);
    protected virtual bool CanFireNowSub(IncidentParms parms);       // override to gate firing
    public virtual float ChanceFactorNow(IIncidentTarget target);
    public virtual float BaseChanceThisGame { get; }

    public bool TryExecute(IncidentParms parms);
    protected virtual bool TryExecuteWorker(IncidentParms parms);    // override to run the incident

    protected void SendStandardLetter(IncidentParms parms, LookTargets lookTargets, params NamedArgument[] textArgs);
    protected void SendStandardLetter(TaggedString baseLetterLabel, TaggedString baseLetterText,
        LetterDef baseLetterDef, IncidentParms parms, LookTargets lookTargets, params NamedArgument[] textArgs);

    public bool FiredTooRecently(IIncidentTarget target);
}
```

### `RimWorld.IncidentWorker_Raid : IncidentWorker_PawnsArrive` (abstract)

Note the inheritance chain: `IncidentWorker_Raid` extends `IncidentWorker_PawnsArrive`, not `IncidentWorker`
directly (`IncidentWorker_PawnsArrive` wasn't in the requested list but sits in between).

```csharp
public abstract class IncidentWorker_Raid : IncidentWorker_PawnsArrive {
    public IncidentDef def;

    protected IncidentWorker_Raid();

    public static float AdjustedRaidPoints(float points, PawnsArrivalModeDef raidArrivalMode,
        RaidStrategyDef raidStrategy, Faction faction, PawnGroupKindDef groupKind,
        IIncidentTarget target, RaidAgeRestrictionDef ageRestriction);

    protected IEnumerable<Faction> CandidateFactions(IncidentParms parms, bool desperate);
    protected override bool CanFireNowSub(IncidentParms parms);
    public virtual bool FactionCanBeGroupSource(Faction f, IncidentParms parms, bool desperate);

    protected virtual void GenerateRaidLoot(IncidentParms parms, float raidLootPoints, List<Pawn> pawns);
    protected abstract LetterDef GetLetterDef();
    protected abstract string GetLetterLabel(IncidentParms parms);
    protected abstract string GetLetterText(IncidentParms parms, List<Pawn> pawns);
    protected abstract string GetRelatedPawnsInfoLetterText(IncidentParms parms);

    protected virtual void PostProcessSpawnedPawns(IncidentParms parms, List<Pawn> pawns);

    public virtual void ResolveRaidAgeRestriction(IncidentParms parms);
    public virtual void ResolveRaidArriveMode(IncidentParms parms);
    protected abstract void ResolveRaidPoints(IncidentParms parms);
    public abstract void ResolveRaidStrategy(IncidentParms parms, PawnGroupKindDef groupKind);

    protected override bool TryExecuteWorker(IncidentParms parms);
    public bool TryGenerateRaidInfo(IncidentParms parms, out List<Pawn> pawns, bool debugTest);
    public virtual bool TryResolveRaidArriveMode(IncidentParms parms);
    protected abstract bool TryResolveRaidFaction(IncidentParms parms);

    protected virtual bool MustHaveSettlementOnLayer { get; }   // NOTE: about the *target map's* planet layer, not an origin settlement — see §7
}
```

All the "hook points" you named exist and are exactly the right shape: `TryExecuteWorker`, `CanFireNowSub`,
`ResolveRaidStrategy`, `ResolveRaidArriveMode`, `ResolveRaidPoints`, `TryResolveRaidFaction`, `GetLetterText`
are all present as `protected virtual`/`abstract` in 1.6.

### `RimWorld.IncidentWorker_RaidEnemy : IncidentWorker_Raid` (concrete)

Overrides in 1.6: `FactionCanBeGroupSource`, `GenerateRaidLoot`, `GetLetterDef`, `GetLetterLabel`,
`GetLetterText`, `GetRelatedPawnsInfoLetterText`, `ResolveRaidAgeRestriction`, `ResolveRaidPoints`,
`ResolveRaidStrategy`, `TryExecuteWorker`, `TryResolveRaidFaction`. Everything else (`CanFireNowSub`,
`ResolveRaidArriveMode`, `TryResolveRaidArriveMode`, `PostProcessSpawnedPawns`, ...) is inherited unchanged
from `IncidentWorker_Raid`, still overridable from a subclass of `IncidentWorker_RaidEnemy`.

**This confirms the recommended approach**: subclass `IncidentWorker_RaidEnemy` and override
`ResolveRaidPoints` (for size), `TryResolveRaidFaction` (for faction/proximity picking),
`ResolveRaidStrategy`/`ResolveRaidArriveMode` (for behavior/arrival), and swap `workerClass` in the
`RaidEnemy` `IncidentDef` (or clone the def) to point at it. No Harmony patch is required for any of this.

---

## 2. `RimWorld.IncidentParms` — every public field

```csharp
public class IncidentParms : IExposable {
    public IIncidentTarget target;
    public float points;
    public Faction faction;
    public bool forced;
    public string customLetterLabel;
    public string customLetterText;
    public LetterDef customLetterDef;
    public bool sendLetter;
    public List<ThingDef> letterHyperlinkThingDefs;
    public List<HediffDef> letterHyperlinkHediffDefs;
    public string inSignalEnd;
    public bool silent;
    public IntVec3 spawnCenter;              // map-local cell, NOT a world tile
    public Rot4 spawnRotation;
    public bool generateFightersOnly;
    public bool dontUseSingleUseRocketLaunchers;
    public RaidStrategyDef raidStrategy;
    public PawnsArrivalModeDef raidArrivalMode;
    [LoadAlias("raidForceOneIncap")] public bool raidForceOneDowned;
    public bool raidNeverFleeIndividual;
    public bool raidArrivalModeForQuickMilitaryAid;
    public RaidAgeRestrictionDef raidAgeRestriction;
    public float biocodeWeaponsChance;
    public float biocodeApparelChance;
    public Dictionary<Pawn,int> pawnGroups;
    public int? pawnGroupMakerSeed;
    public Ideo pawnIdeo;
    public Lord lord;
    public PawnKindDef pawnKind;
    public int pawnCount;
    public PawnGroupKindDef pawnGroupKind;
    public TraderKindDef traderKind;
    public int podOpenDelay;
    public Quest quest;
    public QuestScriptDef questScriptDef;
    public string questTag;
    public MechClusterSketch mechClusterSketch;
    public bool canTimeoutOrFlee;
    public bool canSteal;
    public bool canKidnap;
    public Pawn controllerPawn;
    public IntVec3? infestationLocOverride;
    public List<Thing> attackTargets;
    public List<Thing> gifts;
    public float totalBodySize;
    public PsychicRitualDef psychicRitualDef;
    public float pointMultiplier;
    public bool bypassStorytellerSettings;
    public bool? canRoofPunch;
    public int dropInRadius;
    public List<Pawn> storeGeneratedNeutralPawns;

    public int PawnGroupCount { get; }
    public IncidentParms ShallowCopy();
}
```

**Important finding for your mod**: `canTimeoutOrFlee`, `canSteal`, and `canKidnap` already exist as fields
directly on `IncidentParms` (not just as `LordJob_AssaultColony` ctor args) — they get read by
`RaidStrategyWorker` subclasses when building the `LordJob`/`LordJob_AssaultColony`. Setting them in
`ResolveRaidStrategy` or before calling `TryExecute` is the cleanest way to steer "steal and leave" /
"never retreat" behavior without touching `LordJob` construction yourself.

**No settlement/origin-tile field exists on `IncidentParms`.** `spawnCenter` is a map-local `IntVec3`
(where on the target map pawns appear), not a world tile. See §7.

---

## 3. IncidentDef / IncidentCategoryDef / vanilla `RaidEnemy` XML

### `workerClass` declaration

`IncidentDef.workerClass` is a plain `public Type workerClass;` field (System.Type), set from XML as a
class name string that must resolve to an `IncidentWorker` subclass. `IncidentDef.Worker` is a cached
`IncidentWorker` property built from it (`IncidentDef.Worker { get; }`).

### Verbatim vanilla `RaidEnemy` def

Source: `Data/Core/Defs/Storyteller/Incidents_Map_Threats.xml` (read live off the user's 1.6 install):

```xml
<IncidentDef>
  <defName>RaidEnemy</defName>
  <label>enemy raid</label>
  <targetTags>
    <li>Map_PlayerHome</li>
  </targetTags>
  <workerClass>IncidentWorker_RaidEnemy</workerClass>
  <baseChance>7.4</baseChance>
  <category>ThreatBig</category>
  <pointsScaleable>true</pointsScaleable>
  <tale>Raid</tale>
  <ignoreRecentSelectionWeighting>true</ignoreRecentSelectionWeighting>
  <canOccurOnAllPlanetLayers>true</canOccurOnAllPlanetLayers>
</IncidentDef>
```

Note `<ignoreRecentSelectionWeighting>` and `<canOccurOnAllPlanetLayers>` — both new-ish XML tags; the
latter is a 1.6/multi-planet-layer (Odyssey) addition (see §9). `IncidentDef` has no
`ignoreRecentSelectionWeighting` field in the C# dump but the game accepts it — likely a computed/renamed
field (`ShouldIgnoreRecentWeighting` getter exists) — UNVERIFIED which raw field backs the XML tag exactly;
low risk since you'd only ever copy this block, not hand-author it.

### `IncidentDef` — full field list (as dumped)

Key fields beyond `workerClass`/`category`: `targetTags` (`List<IncidentTargetTagDef>`), `baseChance`,
`baseChanceWithRoyalty`, `earliestDay`, `minPopulation`, `requireColonistsPresent`, `minRefireDays`,
`pointsScaleable`, `minThreatPoints`, `maxThreatPoints`, `allowedBiomes`/`disallowedBiomes`, `tags`,
`refireCheckTags`, `chanceFactorByPopulationCurve`, `letterText`/`letterLabel`/`letterDef`,
`layerWhitelist`/`layerBlacklist` (`List<PlanetLayerDef>`), `canOccurOnAllPlanetLayers`. Full dump is in
the monop transcript this doc was built from if you need a field not listed here — ask and I'll re-pull it.

### `IncidentCategoryDef`

```csharp
public class IncidentCategoryDef : Def {
    public bool needsParmsPoints;
    public TaleDef tale;
    public bool canUseAnomalyChance;
}
```
Very thin — just a tag def referenced by `IncidentDef.category` (e.g. `ThreatBig`).

---

## 4. Pawn group / raid-strategy / arrival-mode plumbing

### `RimWorld.PawnGroupMakerParms`

```csharp
public class PawnGroupMakerParms {
    public PawnGroupKindDef groupKind;
    public PlanetTile tile;              // 1.6: PlanetTile struct, not int — see §9
    public bool inhabitants;
    public float points;
    public Faction faction;
    public Ideo ideo;
    public TraderKindDef traderKind;
    public bool generateFightersOnly;
    public bool dontUseSingleUseRocketLaunchers;
    public RaidStrategyDef raidStrategy;
    public bool forceOneDowned;
    public int? seed;
    public RaidAgeRestrictionDef raidAgeRestriction;
    public bool ignoreGroupCommonality;
}
```

### `RimWorld.PawnGroupMakerUtility` (all static)

```csharp
public static class PawnGroupMakerUtility {
    public static bool CanGenerateAnyNormalGroup(Faction faction, float points);
    public static IEnumerable<Pawn> GeneratePawns(PawnGroupMakerParms parms, bool warnOnZeroResults);
    public static float MaxPawnCost(Faction faction, float totalPoints, RaidStrategyDef raidStrategy, PawnGroupKindDef groupKind);
    public static bool TryGetRandomFactionForCombatPawnGroup(float points, out Faction faction,
        Predicate<Faction> validator, bool allowNonHostileToPlayer, bool allowHidden, bool allowDefeated, bool allowNonHumanlike);
    public static bool TryGetRandomFactionForCombatPawnGroupWeighted(IncidentParms parms, out Faction faction,
        Predicate<Faction> validator, bool allowNonHostileToPlayer, bool allowHidden, bool allowDefeated, bool allowNonHumanlike);
    public static bool TryGetRandomPawnGroupMaker(PawnGroupMakerParms parms, out PawnGroupMaker pawnGroupMaker, bool ignoreCommonality);
    // + AnyOptions, ChoosePawnGenOptionsByPoints, GetOptions, PawnGenOptionValid (pawn-gen internals)
}
```

`TryGetRandomFactionForCombatPawnGroupWeighted(IncidentParms parms, ...)` is the method vanilla's
`TryResolveRaidFaction` implementations lean on — it takes the whole `IncidentParms`, so if you want to
bias faction *selection* by geography you'd write your own version of this (or post-filter its `validator`
predicate) inside your `TryResolveRaidFaction` override.

### `RimWorld.RaidStrategyDef`

```csharp
public class RaidStrategyDef : Def {
    public Type workerClass;
    public RaidStrategyWorker Worker { get; }
    public SimpleCurve selectionWeightPerPointsCurve;
    public float minPawns;
    public List<FactionCurve> selectionWeightCurvesPerFaction;
    public List<PlanetLayerDef> layerWhitelist;
    public List<PlanetLayerDef> layerBlacklist;
    public string arrivalTextFriendly;
    public string arrivalTextEnemy;
    public string letterLabelEnemy;
    public string letterLabelFriendly;
    public SimpleCurve pointsFactorCurve;
    public bool pawnsCanBringFood;
    public List<PawnsArrivalModeDef> arriveModes;
    public float raidLootValueFactor;
}
```

### `RimWorld.RaidStrategyWorker` (abstract)

```csharp
public abstract class RaidStrategyWorker {
    public RaidStrategyDef def;
    public virtual bool CanUseWith(IncidentParms parms, PawnGroupKindDef groupKind);
    protected abstract LordJob MakeLordJob(IncidentParms parms, Map map, List<Pawn> pawns, int raidSeed);
    public virtual void MakeLords(IncidentParms parms, List<Pawn> pawns);
    public virtual float MinimumPoints(Faction faction, PawnGroupKindDef groupKind);
    public virtual float SelectionWeight(Map map, float basePoints);
    public virtual List<Pawn> SpawnThreats(IncidentParms parms);
    public virtual void TryGenerateThreats(IncidentParms parms);
}
```

`MakeLordJob` is exactly where `LordJob_AssaultColony`/`LordJob_StageThenAttack`/`LordJob_Siege`/etc. get
constructed from `parms.canSteal`/`canKidnap`/`canTimeoutOrFlee`. Subclassing a `RaidStrategyWorker_*` and
overriding `MakeLordJob` is the other clean hook point (in addition to setting the `IncidentParms` fields)
if you want a genuinely custom `LordJob` for "motivated" raids.

Concrete `RaidStrategyWorker_*` subclasses present in 1.6 (grepped from the type list):
`RaidStrategyWorker_ImmediateAttack`, `_ImmediateAttackBreaching`, `_ImmediateAttackBreachingSmart`,
`_ImmediateAttackFriendly`, `_ImmediateAttackSappers`, `_ImmediateAttackSmart`, `_PsychicRitualSiege`,
`_ShamblerAssault`, `_Siege`, `_SiegeMechanoid`, `_StageThenAttack`, `_WithRequiredPawnKinds`.

### `RimWorld.PawnsArrivalModeDef` / `PawnsArrivalModeWorker`

```csharp
public class PawnsArrivalModeDef : Def {
    public Type workerClass;
    public PawnsArrivalModeWorker Worker { get; }
    public SimpleCurve selectionWeightCurve;
    public SimpleCurve pointsFactorCurve;
    public TechLevel minTechLevel;
    public bool forQuickMilitaryAid;
    public bool walkIn;
    public bool canBeBackup;
    public List<FactionCurve> selectionWeightCurvesPerFaction;
    public string textEnemy, textFriendly, textWillArrive;
}

public abstract class PawnsArrivalModeWorker {
    public PawnsArrivalModeDef def;
    public abstract void Arrive(List<Pawn> pawns, IncidentParms parms);
    public virtual bool CanUseOnTile(PlanetTile tile);   // 1.6: PlanetTile, not int
    public virtual bool CanUseWith(IncidentParms parms);
    public abstract bool TryResolveRaidSpawnCenter(IncidentParms parms);
}
```

Concrete workers: `PawnsArrivalModeWorker_CenterDrop`, `_ClusterDrop`, `_EdgeDrop`, `_EdgeDropGroups`,
`_EdgeWalkIn`, `_EdgeWalkInDarkness`, `_EdgeWalkInDistributed`, `_EdgeWalkInDistributedGroups`,
`_EdgeWalkInGroups`, `_EdgeWalkInHateChanters`, `_EmergeFromWater`, `_RandomDrop`, `_SpecificLocationDrop`.

### `RaidStrategyDefOf` / `PawnsArrivalModeDefOf` (only the statically-cached defs — many more exist as plain defs, e.g. `StageThenAttack`, `Siege`, `EdgeDropGroups`, referenced by string/def-database lookup instead)

```csharp
public static class RaidStrategyDefOf {
    public static RaidStrategyDef ImmediateAttack;
    public static RaidStrategyDef ImmediateAttackFriendly;
    [MayRequireAnomaly] public static RaidStrategyDef PsychicRitualSiege;
    [MayRequireAnomaly] public static RaidStrategyDef ShamblerAssault;
}

public static class PawnsArrivalModeDefOf {
    public static PawnsArrivalModeDef EdgeWalkIn, EdgeWalkInGroups, EdgeWalkInDistributed,
        CenterDrop, EdgeDrop, SpecificDropDebug, EmergeFromWater, RandomDrop;
    [MayRequireAnomaly] public static PawnsArrivalModeDef EdgeWalkInHateChanters, EdgeWalkInDistributedGroups, EdgeWalkInDarkness;
}
```

### Verbatim vanilla `RaidStrategies_Basic.xml` (all 4 basic strategies, unabridged)

Source: `Data/Core/Defs/Storyteller/RaidStrategies_Basic.xml`. Full text — useful as a copy-paste template
for a custom `RaidStrategyDef`:

```xml
<RaidStrategyDef Name="ImmediateAttack">
  <defName>ImmediateAttack</defName>
  <workerClass>RaidStrategyWorker_ImmediateAttack</workerClass>
  <selectionWeightPerPointsCurve><points><li>(0, 1)</li></points></selectionWeightPerPointsCurve>
  <selectionWeightCurvesPerFaction>
    <li>
      <faction>Mechanoid</faction>
      <selectionWeightPerPointsCurve><points><li>(0, 0)</li></points></selectionWeightPerPointsCurve>
    </li>
  </selectionWeightCurvesPerFaction>
  <pointsFactorCurve><points><li>0, 1</li></points></pointsFactorCurve>
  <arriveModes>
    <li>EdgeDrop</li>
    <li>EdgeWalkIn</li>
    <li>CenterDrop</li>
    <li>RandomDrop</li>
    <li>EdgeDropGroups</li>
    <li>EdgeWalkInGroups</li>
    <li MayRequire="Ludeon.RimWorld.Anomaly">EdgeWalkInDarkness</li>
  </arriveModes>
  <letterLabelEnemy>Raid</letterLabelEnemy>
  <arrivalTextEnemy>They are attacking immediately.</arrivalTextEnemy>
  <letterLabelFriendly>Friendlies</letterLabelFriendly>
  <arrivalTextFriendly>They are moving in to help you immediately.</arrivalTextFriendly>
</RaidStrategyDef>

<RaidStrategyDef ParentName="ImmediateAttack">
  <defName>ImmediateAttackFriendly</defName>
  <workerClass>RaidStrategyWorker_ImmediateAttackFriendly</workerClass>
  <pawnsCanBringFood>True</pawnsCanBringFood>
</RaidStrategyDef>

<RaidStrategyDef>
  <defName>ImmediateAttackSmart</defName>
  <workerClass>RaidStrategyWorker_ImmediateAttackSmart</workerClass>
  <selectionWeightPerPointsCurve><points><li>(0,0)</li><li>(1000,0.5)</li></points></selectionWeightPerPointsCurve>
  <pointsFactorCurve><points><li>0, 0.95</li></points></pointsFactorCurve>
  <arriveModes>
    <li>EdgeDrop</li><li>EdgeWalkIn</li><li>CenterDrop</li><li>RandomDrop</li>
    <li>EdgeDropGroups</li><li>EdgeWalkInGroups</li>
  </arriveModes>
  <letterLabelEnemy>Raid</letterLabelEnemy>
  <arrivalTextEnemy>They are attacking immediately.\n\nWatch out - they appear to be unusually clever with their tactics. They'll avoid your turrets' fields of fire and notice some of your traps.</arrivalTextEnemy>
  <letterLabelFriendly>Friendlies</letterLabelFriendly>
  <arrivalTextFriendly>They are moving in to help you immediately.</arrivalTextFriendly>
</RaidStrategyDef>

<RaidStrategyDef>
  <defName>StageThenAttack</defName>
  <workerClass>RaidStrategyWorker_StageThenAttack</workerClass>
  <selectionWeightPerPointsCurve><points><li>(0, 1)</li></points></selectionWeightPerPointsCurve>
  <selectionWeightCurvesPerFaction>
    <li>
      <faction>Mechanoid</faction>
      <selectionWeightPerPointsCurve><points><li>(0, 1)</li></points></selectionWeightPerPointsCurve>
    </li>
  </selectionWeightCurvesPerFaction>
  <pointsFactorCurve><points><li>0, 1</li></points></pointsFactorCurve>
  <arriveModes>
    <li>EdgeDrop</li><li>EdgeWalkIn</li><li>EdgeDropGroups</li><li>EdgeWalkInGroups</li>
  </arriveModes>
  <letterLabelEnemy>Raid</letterLabelEnemy>
  <arrivalTextEnemy>They will prepare for a while, then attack.\n\nPrepare a defense or attack them pre-emptively.</arrivalTextEnemy>
  <letterLabelFriendly>Friendlies</letterLabelFriendly>
  <arrivalTextFriendly>They will prepare for a while before moving in to help you.</arrivalTextFriendly>
</RaidStrategyDef>

<RaidStrategyDef>
  <defName>EmergeFromWater</defName>
  <workerClass>RaidStrategyWorker_ImmediateAttack</workerClass>
  <selectionWeightPerPointsCurve><points><li>(0, 0)</li></points></selectionWeightPerPointsCurve>
  <selectionWeightCurvesPerFaction>
    <li>
      <faction>Mechanoid</faction>
      <selectionWeightPerPointsCurve><points><li>(500, 0)</li><li>(1000, 0.25)</li></points></selectionWeightPerPointsCurve>
    </li>
  </selectionWeightCurvesPerFaction>
  <pointsFactorCurve><points><li>0, 0.8</li></points></pointsFactorCurve>
  <arriveModes><li>EmergeFromWater</li></arriveModes>
  <letterLabelEnemy>Raid</letterLabelEnemy>
  <arrivalTextEnemy>They are attacking immediately.</arrivalTextEnemy>
  <letterLabelFriendly>Friendlies</letterLabelFriendly>
  <arrivalTextFriendly>They are moving in to help you immediately.</arrivalTextFriendly>
</RaidStrategyDef>
```

(`RaidStrategies_Siege.xml`, `RaidStrategies_Breach.xml`, `RaidStrategies_Sapper.xml` exist alongside this
and follow the same shape — not reproduced here, ask if you need them verbatim too.)

---

## 5. LordJobs / LordToils / Lord / Triggers

### `RimWorld.LordJob_AssaultColony` (namespace is `RimWorld`, **not** `Verse.AI.Group` — flag: your original assumption was wrong)

```csharp
public class LordJob_AssaultColony : Verse.AI.Group.LordJob, IDisposable, IExposable, ILordAvoidTraps {
    public LordJob_AssaultColony();
    public LordJob_AssaultColony(SpawnedPawnParams parms);
    public LordJob_AssaultColony(
        Faction assaulterFaction,
        bool canKidnap = true,
        bool canTimeoutOrFlee = true,
        bool sappers = false,
        bool useAvoidGridSmart = false,
        bool canSteal = true,
        bool breachers = false,
        bool canPickUpOpportunisticWeapons = false);

    public override StateGraph CreateGraph();
}
```

**Directly answers your question**: yes — `canSteal`, `canKidnap`, and `canTimeoutOrFlee` are real
constructor parameters (all `[opt]`/optional with the C#-level defaults shown above, confirmed via the
IL `Constant` table, not guessed). They back private fields of the same name (`assaulterFaction`,
`canKidnap`, `canTimeoutOrFlee`, `sappers`, `useAvoidGridSmart`, `canSteal`, `breachers`,
`canPickUpOpportunisticWeapons` — all `private`, no public getters/setters), so you must pass them via the
constructor; there's no way to mutate an existing `LordJob_AssaultColony` after construction. Its only
other declared members are `CreateGraph()` and `ExposeData()` — everything else you see with `monop`
(`AbilityAllowed`, `Notify_*`, etc.) is inherited from base `LordJob` unchanged.

Default note: `useAvoidGridSmart` and `breachers`/`canPickUpOpportunisticWeapons` default to **false**,
not true — worth knowing if you rely on defaults.

### `RimWorld.LordJob_Steal : Verse.AI.Group.LordJob`

```csharp
public class LordJob_Steal : LordJob, IDisposable, IExposable {
    public LordJob_Steal();   // only ctor — no parameters at all
    public override StateGraph CreateGraph();
    public override void ExposeData();
}
```

It has **zero fields and zero configuration parameters** — its entire "steal specific things" logic must
live inside `CreateGraph()`'s state-graph/toil wiring (likely a generic "find and haul highest-value
lootable" behavior), not something you configure from outside. If you want raiders to steal a *specific*
resource type or stop once they've hit a target value, you will need to write your own `LordJob` (or a
`LordToil`/duty) rather than parameterizing this one — it has no hooks for that.

### `RimWorld.LordJob_StageThenAttack : Verse.AI.Group.LordJob`

```csharp
public class LordJob_StageThenAttack : LordJob, IDisposable, IExposable {
    public LordJob_StageThenAttack();
    public LordJob_StageThenAttack(
        Faction faction,
        IntVec3 stageLoc,
        int raidSeed,
        bool canTimeoutFlee = true,
        bool canKidnap = true,
        bool canSteal = true,
        IntRange? delay = null);

    public override StateGraph CreateGraph();
    public override void ExposeData();
}
```

Same `canTimeoutFlee`/`canKidnap`/`canSteal` triple as `LordJob_AssaultColony` (note: parameter is named
`canTimeoutFlee` here, not `canTimeoutOrFlee`), all defaulting to `true`; `delay` (a `Verse.IntRange?`)
controls the random stage-then-attack delay window and defaults to `null`.

### `Verse.AI.Group.LordMaker`

```csharp
public static class LordMaker {
    public static Lord MakeNewLord(Faction faction, LordJob lordJob, Map map, IEnumerable<Pawn> startingPawns);
}
```
This is what `RaidStrategyWorker.MakeLords` ultimately calls — the single entry point for spawning a Lord
with a custom `LordJob` for a set of pawns.

### `Verse.AI.Group.Lord` — key members

```csharp
public class Lord : IDisposable, IExposable, ILoadReferenceable, ISignalReceiver {
    public Faction faction;
    public List<Pawn> ownedPawns;
    public LordJob LordJob { get; }
    public LordToil CurLordToil { get; }
    public StateGraph Graph { get; }
    public Map Map { get; }

    public void GotoToil(LordToil newLordToil);
    public void SetJob(LordJob lordJob, bool loading);
    public void AddPawn(Pawn p);
    public void RemovePawn(Pawn p);
    public void ReceiveMemo(string memo);   // how Trigger_Memo / signals reach a LordJob's state graph
}
```

### `Verse.AI.Group.LordToil` (base) / `LordToil_ExitMap`

`LordToil_ExitMap(LocomotionUrgency locomotion, bool canDig, bool interruptCurrentJob)` — the one
constructor. Fields: `Lord lord`, `LordToilData data`, `bool useAvoidGrid`. This is the toil raid strategies
switch to once a raid decides to leave (e.g. after a steal-and-flee threshold is hit) — transitioning a
Lord to `LordToil_ExitMap` (or a `Trigger_*`-driven transition to it in your `CreateGraph()`) is the
standard way to make a raid retreat programmatically.

### Full `LordJob_*` type list (1.6, grepped from the assembly's type table)

Namespace `RimWorld` (raid/quest/ritual-flavored jobs — most of the interesting ones):
`LordJob_AssaultColony`, `_AssaultThings`, `_AssistColony`, `_BegForItems`, `_BestowingCeremony`,
`_BossgroupAssaultColony`, `_ChimeraAssault`, `_CreepJoiner`, `_DefendAndExpandHive`,
`_DefendAttackedTraderCaravan`, `_DefendBase`, `_DefendCerebrexCore`, `_DefendFleshmassHeart`,
`_DevourerAssault`, `_EntitySwarm`, `_EscortPawn`, `_ExitOnShuttle`, `_FleshbeastAssault`,
`_FormAndSendCaravan`, `_GorehulkAssault`, `_HateChant`, `_HiveQueen`, `_Joinable_Concert`,
`_Joinable_Gathering`, `_Joinable_MarriageCeremony`, `_Joinable_Party`, `_Joinable_Speech`, `_Kidnap`,
`_LoadAndEnterPortal`, `_LoadAndEnterTransporters`, `_ManTurrets`, `_MechanoidDefendBase`,
`_MechanoidsDefend`, `_Metalhorror`, `_PartyDanceDrums`, `_PrisonBreak`, `_PsychicRitual`,
`_PsychicRitualRepeating`, `_ReimplantXenogerm`, `_ReturnedCaravan`, `_Ritual`, `_Ritual_ChildBirth`,
`_Ritual_Duel`, `_Ritual_Mutilation`, `_SanguophageMeeting`, `_ShamblerAssault`, `_ShamblerSwarm`,
`_Siege`, `_SightstealerAssault`, `_SightstealerSwarm`, `_SitePawns`, `_SlaveRebellion`,
`_SleepThenAssaultColony`, `_SleepThenMechanoidsDefend`, `_StageThenAttack`, `_Steal`,
`_StructureThreatCluster`, `_TradeWithColony`, `_Venerate`, `_VisitColony`,
`_VoidAwakeningDefendStructure`, `_VoidAwakeningWander`, `_VoluntarilyJoinable`,
`_WaitForDurationThenExit`, `_WaitForEscort`, `_WanderNest`.

Namespace `Verse.AI.Group` (generic base jobs): `LordJob_DefendPoint`, `LordJob_ExitMapBest`,
`LordJob_ExitMapNear`, `LordJob_Travel`, `LordJob_TravelAndExit`.

### Full `LordToil_*` type list

`RimWorld` namespace has ~55 concrete toils (assault/siege/breach/sapper variants, ceremony/ritual toils,
prepare-caravan toils, etc.) — notably for raids: `LordToil_AssaultColony`,
`LordToil_AssaultColonyBossgroup`, `_AssaultColonyBreaching`, `_AssaultColonyPrisoners`,
`_AssaultColonySappers`, `_HuntDownColonists`, `_HuntEnemies`, `_PanicFlee`, `_Siege`, `_StealCover`,
`_Stage`. `Verse.AI.Group` has the generic base set: `LordToil` (abstract base), `_DefendPoint`,
`_DefendSelf`, `_End`, `_ExitMap`, `_ExitMapFighting`, `_ExitMapNear`, `_ExitMapRandom`,
`_ExitMapTraderFighting`, `_PsychicRitual`, `_Travel`.

### Full `Trigger_*` type list

`RimWorld` namespace (raid-specific): `Trigger_FractionColonyDamageTaken`, `Trigger_GameEnding`,
`Trigger_HighValueThingsAround`, `Trigger_KidnapVictimPresent`, `Trigger_WoundedGuestPresent`.

`Verse.AI.Group` namespace (generic, state-graph building blocks — this is the set you'll actually compose
a custom `CreateGraph()` with): `Trigger_AcquiredTarget`, `_AnyThingDamageTaken`,
`_BecameNonHostileToPlayer`, `_BecamePlayerEnemy`, `_ChanceOnPlayerHarmNPCBuilding`, `_ChanceOnSignal`,
`_ChanceOnTickInterval`, `_Custom`, `_DormancyWakeup`, `_DormancyWakeupOrClamor`, `_FractionPawnsLost`,
`_GameCondition`, `_ImportantTraderCaravanPeopleLost`, `_Memo`, `_MentalState`, `_NoFightingSappers`,
`_NoMentalState`, `_NoPawnsVeryTiredAndSleeping`, `_OnClamor`, `_OnHumanlikeHarmAnyThing`,
`_OnPlayerMechHarmAnything`, `_PawnCannotReachMapEdge`, `_PawnCanReachMapEdge`,
`_PawnExperiencingAnomalousWeather`, `_PawnExperiencingDangerousTemperatures`, `_PawnHarmed`,
`_PawnKilled`, `_PawnLost`, `_PawnLostViolently`, `_PawnsLost`, `_Signal`, `_ThingDamageTaken`,
`_ThingsDamageTaken`, `_TickCondition`, `_TicksPassed`, `_TicksPassedAfterConditionMet`,
`_TicksPassedAndNoRecentHarm`, `_TicksPassedRitual`, `_TicksPassedWithoutHarm`,
`_TicksPassedWithoutHarmOrMemos`, `_TraderAndAllTraderCaravanGuardsLost`, `_UrgentlyHungry`.

For a "steal food and leave once you have enough" motivation, the most relevant off-the-shelf building
blocks are `Trigger_TicksPassed`/`Trigger_TicksPassedWithoutHarm` (timeouts), `Trigger_FractionPawnsLost`
(retreat once losses are too high — used by vanilla `LordJob_AssaultColony`), and `Trigger_Memo`/
`Trigger_Custom`/`Trigger_ChanceOnSignal` for a custom "carried enough loot" signal you fire yourself
(e.g. from a Harmony postfix on the hauling job, or a custom `LordToil` that polls held item value each
tick and calls `lord.ReceiveMemo("EnoughLoot")`).

---

## 6. Storyteller / incident frequency

### `RimWorld.StorytellerUtility` (all static)

```csharp
public static class StorytellerUtility {
    public static float DefaultThreatPointsNow(IIncidentTarget target);
    public static float DefaultSiteThreatPointsNow();
    public static IncidentParms DefaultParmsNow(IncidentCategoryDef incCat, IIncidentTarget target);
    public static float GlobalPointsMin();
    public static float GetProgressScore(IIncidentTarget target);
    public static float AllyIncidentFraction(bool fullAlliesOnly);

    public const float GlobalPointsMinRangeFloor = 35f;
    public const float GlobalPointsMax = 10000f;
}
```

`DefaultThreatPointsNow(IIncidentTarget target)` is exactly the signature you'd call to get "how many
raid points would the storyteller normally use against this colony right now" — useful as a baseline you
then multiply by your own proximity factor.

### `StorytellerComp` subclasses relevant to raid frequency

Raids fire via `RimWorld.StorytellerComp_ThreatsGenerator` (paired with
`StorytellerCompProperties_ThreatsGenerator`), which is what most storyteller defs use for "ThreatBig"/
"ThreatSmall" category incidents including `RaidEnemy`:

```csharp
public class StorytellerComp_ThreatsGenerator : StorytellerComp {
    public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target);
    public virtual IncidentParms GenerateParms(IncidentCategoryDef incCat, IIncidentTarget target);
    protected float IncidentChanceFactor_CurrentPopulation(IncidentDef def);
    protected float IncidentChanceFactor_PopulationIntent(IncidentDef def);
    protected float IncidentChanceFinal(IncidentDef def, IIncidentTarget target);
    protected StorytellerCompProperties_ThreatsGenerator Props { get; }
}

public class StorytellerCompProperties_ThreatsGenerator : StorytellerCompProperties {
    public ThreatsGeneratorParams parms;
    public float minDaysPassed;
    public List<IncidentTargetTagDef> allowedTargetTags;
    public List<IncidentTargetTagDef> disallowedTargetTags;
    public float minIncChancePopulationIntentFactor;
}
```

Full `StorytellerComp_*` list in 1.6: `_CategoryIndividualMTBByBiome`, `_CategoryMTB`, `_ClassicIntro`,
`_DeepDrillInfestation`, `_Disease`, `_DissolutionTriggered`, `_FactionInteraction`,
`_GauranlenPodSpawn`, `_ImportantQuest`, `_MechanitorComplexQuest`, `_MonolithMigration`, `_NoxiousHaze`,
`_OnOffCycle`, `_RandomEpicQuest`, `_RandomMain`, `_RandomQuest`, `_RefiringUniqueQuest`,
`_ShipChunkDrop`, `_SingleMTB`, `_SingleOnceFixed`, `_ThreatsGenerator`, `_Triggered`, `_WorkSite`.
For raid-frequency tuning specifically, `_ThreatsGenerator` (raids/threats) and `_OnOffCycle` (wraps
another comp to enable/disable it, e.g. "peaceful intro period") are the ones worth subclassing or
patching; the rest are quest/disease/cosmetic generators.

`IncidentChanceFinal(IncidentDef, IIncidentTarget)` (protected) is the natural Harmony-postfix or
subclass-override point if you want to multiply `RaidEnemy`'s fire chance by a proximity factor without
touching the raid-generation side at all.

### `RimWorld.IncidentQueue`

```csharp
public class IncidentQueue : IExposable {
    public bool Add(IncidentDef def, int fireTick, IncidentParms parms, int retryDurationTicks);
    public bool Add(QueuedIncident qi);
    public int Count { get; }
}
```
This is how you'd schedule a raid to fire sooner (smaller `fireTick` offset) for "faster-arriving when
close" — build an `IncidentParms` yourself (e.g. via `StorytellerUtility.DefaultParmsNow` then override
`faction`/`points`) and `Add` it to `Find.Storyteller.incidentQueue` (field name UNVERIFIED — I did not
dump `Storyteller` itself; can pull it if you need the exact field name on the `Storyteller` class).

---

## 7. Raid origin — does anything link a raid to a specific settlement?

**No.** Searched every signature above (`IncidentParms`, `PawnGroupMakerParms`, `IncidentWorker_Raid`,
`RaidStrategyWorker`, `PawnsArrivalModeWorker`, `Faction`) for any `Settlement`-typed parameter, field, or
return value. There is none. The only "settlement + raid-adjacent" surface in the whole assembly (grepped
the full 9391-type list for `Settlement`) is:

- `RimWorld.Planet.SettlementProximityGoodwillUtility` — an unrelated vanilla mechanic: it looks at
  distance between the *player's own* settlements and other factions' settlements to decay goodwill when
  you found a colony too close to someone else (`AppendProximityGoodwillOffsets(PlanetTile tile, ...)`,
  `MaxDist` property, `CheckSettlementProximityGoodwillChange()`). It doesn't feed into raid generation at
  all — it only ever calls `TryAffectGoodwillWith`.
- `GoodwillSituationWorker_AttackingSettlement`, `CaravanArrivalAction_AttackSettlement`,
  `TransportersArrivalAction_AttackSettlement` — these are for the *player* attacking an NPC settlement
  (caravan assault flow), not for incoming raids against the player.

`IncidentWorker_Raid.TryResolveRaidFaction` picks a **faction** (via
`CandidateFactions`/`FactionCanBeGroupSource` and ultimately
`PawnGroupMakerUtility.TryGetRandomFactionForCombatPawnGroupWeighted`), never a specific `Settlement`
belonging to that faction. Vanilla raids have no concept of "which settlement did this raid come from" —
pawns just spawn at `spawnCenter` on the target map.

**Conclusion for your mod**: if you want "raids from geographically close settlements are more likely",
you must implement that yourself — e.g. in your `TryResolveRaidFaction` override (or a wrapper around
`FactionManager.AllFactions`/`RandomRaidableEnemyFaction`), enumerate `Find.WorldObjects.Settlements`
(UNVERIFIED exact accessor name — `WorldObjectsHolder` wasn't in your requested list; likely
`Find.WorldObjects.SettlementsInRandomOrder`/`.Settlements` — confirm before coding), filter to
`s.Faction == candidateFaction`, and weight/pick a faction (and, separately, remember which settlement you
"attribute" the raid to, entirely in your own mod state — nothing built-in tracks or needs it) using
`WorldGrid.TraversalDistanceBetween(PlanetTile, PlanetTile, ...)` or
`WorldGrid.ApproxDistanceInTiles(PlanetTile, PlanetTile)` (both confirmed on `RimWorld.Planet.WorldGrid`,
§9) between the player colony's tile and each settlement's `Tile` (a `PlanetTile`, inherited from
`WorldObject.Tile { get; set; }`).

---

## 8. Faction / FactionManager

### `RimWorld.Faction` — relevant members

```csharp
public class Faction : ICommunicable, IExposable, ILoadReferenceable {
    public FactionDef def;
    public Pawn leader;
    public bool defeated;
    public bool factionHostileOnHarmByPlayer;
    public bool neverFlee;

    public static Faction OfPlayer { get; }
    public bool IsPlayer { get; }
    public FactionRelationKind PlayerRelationKind { get; }   // Hostile / Neutral / Ally, relative to player
    public int PlayerGoodwill { get; }
    public bool HasGoodwill { get; }

    public FactionRelationKind RelationKindWith(Faction other);
    public FactionRelation RelationWith(Faction other, bool allowNull);
    public int GoodwillWith(Faction other);
    public int BaseGoodwillWith(Faction other);
    public int GoodwillToMakeHostile(Faction other);
    public bool CanChangeGoodwillFor(Faction other, int goodwillChange);

    public bool TryAffectGoodwillWith(
        Faction other,
        int goodwillChange,
        bool canSendMessage,
        bool canSendHostilityLetter,
        HistoryEventDef reason,
        RimWorld.Planet.GlobalTargetInfo? lookTarget);

    public void SetRelationDirect(Faction other, FactionRelationKind kind, bool canSendHostilityLetter,
        string reason, RimWorld.Planet.GlobalTargetInfo? lookTarget);
}
```

`HostileTo` is **not** a `Faction` instance method in 1.6 — it's a static extension-style method on a
separate utility class:

```csharp
public static class RimWorld.FactionUtility {
    public static bool HostileTo(Faction fac, Faction other);
    public static bool AllyOrNeutralTo(Faction fac, Faction other);
}
```
(There's also `RimWorld.GenHostility.HostileTo(Thing, Thing)` / `.HostileTo(Thing, Faction)` for
thing-level hostility checks — different overload set, different class.)

### `RimWorld.FactionManager` — relevant members

```csharp
public class FactionManager : IExposable {
    public IEnumerable<Faction> AllFactions { get; }
    public List<Faction> AllFactionsListForReading { get; }
    public IEnumerable<Faction> AllFactionsVisible { get; }

    public IEnumerable<Faction> GetFactions(bool allowHidden, bool allowDefeated, bool allowNonHumanlike,
        TechLevel minTechLevel, bool allowTemporary);

    public Faction RandomEnemyFaction(bool allowHidden, bool allowDefeated, bool allowNonHumanlike, TechLevel minTechLevel);
    public Faction RandomRaidableEnemyFaction(bool allowHidden, bool allowDefeated, bool allowNonHumanlike, TechLevel minTechLevel);
    public Faction RandomAlliedFaction(bool allowHidden, bool allowDefeated, bool allowNonHumanlike, TechLevel minTechLevel);
    public Faction FirstFactionOfDef(FactionDef facDef);
}
```

`RandomRaidableEnemyFaction` is the vanilla-style call for "give me a faction that can currently raid the
player" — worth wrapping/filtering by settlement proximity rather than reimplementing its internal
raidability checks from scratch.

---

## 9. What's 1.6/Odyssey-specific vs. older versions

- **`PlanetTile` replaces raw `int` tile IDs almost everywhere.** Confirmed as a `struct PlanetTile :
  IEquatable<PlanetTile>` with `implicit operator int` / `implicit operator PlanetTile(int)` (so old code
  using `int tile` mostly still compiles against it) plus a `Layer`/`LayerDef` (`PlanetLayer`/
  `PlanetLayerDef`) — this is new: RimWorld 1.6 (Odyssey) supports multiple "planet layers" (e.g. orbit),
  and a tile ID alone is no longer globally unique — it's only unique per layer. Every method that used to
  take `int tile` now takes `PlanetTile` (`PawnGroupMakerParms.tile`, `PawnsArrivalModeWorker.CanUseOnTile`,
  `WorldGrid.TraversalDistanceBetween`, `Settlement`/`WorldObject.Tile`, etc.).
- **`IncidentDef` gained `layerWhitelist`/`layerBlacklist`/`canOccurOnAllPlanetLayers`**, and
  `RaidStrategyDef`/`PawnsArrivalModeDef` likewise gained `layerWhitelist`/`layerBlacklist` — all new for
  multi-layer planets. The vanilla `RaidEnemy` def explicitly sets `<canOccurOnAllPlanetLayers>true</>`.
  `IncidentWorker_Raid.MustHaveSettlementOnLayer` is also new-ish framing tied to this system — despite
  the name, it's about the *target's* planet layer having a valid settlement to raid, not about tracking
  a raid's origin settlement (see §7).
- **`Verse.AI.Group.Lord` gained `Notify_MechClusterDefeated`, `Notify_Clamor`,
  `Notify_FactionRelationsChanged`, `AbilityAllowed`** and similar newer notify hooks — can't fully
  confirm which are 1.6-new vs. earlier-version additions without a 1.5 dll to diff against
  (UNVERIFIED — no 1.5 reference assembly available in this environment).
- Many Anomaly/Odyssey-flavored `LordJob_*`/`LordToil_*`/`Trigger_*` types exist alongside the classic
  ones (`_Metalhorror`, `_VoidAwakeningWander`, `_FleshbeastAssault`, `_PawnExperiencingAnomalousWeather`,
  etc.) — these are DLC content, not needed for a base raids mod but listed in §5 for completeness since
  you asked for the full type lists.
- I could **not** verify whether `LordJob_AssaultColony`'s constructor signature or its `canKidnap`/
  `canSteal`/`canTimeoutOrFlee` parameters changed from 1.5 → 1.6 (no 1.5 `Assembly-CSharp.dll` available
  to diff) — flagging this since it's central to your plan; the *current* 1.6 signature above is
  independently solid (verified twice: `monop` constructor listing + `monodis --method`/`--fields`/
  `--constant` for parameter names, order, and default values).

---

## Tooling notes / caveats

- `monop` crashed with a `FileNotFoundException` on `Unity.Collections` for several types
  (`LordJob_AssaultColony`, `LordJob_Steal`, `LordJob_StageThenAttack`) partway through their member list —
  looks like an IKVM reflection issue resolving *some* method signature elsewhere in the type, not
  specific to any one member. Where this happened, the missing tail of the member list was recovered via
  `monodis --method`/`--fields` grepped by class name against the same DLL, which does not hit this issue.
  Constructor default parameter values (`[opt]` params) were pulled from `monodis --constant`'s `Param:`
  entries, matched to parameter tokens from `monodis --param`.
- Everything in this document is from `~/refs/api/Assembly-CSharp.dll` (RimWorld 1.6) and the user's live
  `RimWorld/Data/Core/Defs/Storyteller/*.xml` — no memory/guessing was used for any signature. Anything I
  could not directly verify is called out inline as "UNVERIFIED".
