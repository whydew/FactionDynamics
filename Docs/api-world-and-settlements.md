# RimWorld 1.6 World/Planet API cheat-sheet

Verified against `Assembly-CSharp.dll` in `~/refs/api` via `monop` (public
member signatures) and `ikdasm` full-assembly IL disassembly (to confirm
actual call chains / behavior, quoted below as "**IL-verified**"). Vanilla
XML quoted verbatim from the user's RimWorld 1.6 install
(`Data/Core/Defs/...`). Anything not confirmed one of these ways is flagged
explicitly as **UNVERIFIED**.

Namespace correction up front: the type is **`RimWorld.Planet.PlanetTile`**,
not `Verse.PlanetTile` as guessed in the task — `monop` with no type name
(full listing, grepped) found it under `RimWorld.Planet`. Likewise most
"world" types live in `RimWorld.Planet`, not `Verse`.

---

## 1. `RimWorld.Planet.PlanetTile`

```csharp
public struct PlanetTile : IEquatable<PlanetTile> {
    public PlanetTile(int tileId, PlanetLayer layer);
    public PlanetTile(int tileId);
    public PlanetTile(int tileId, int layerId);

    public static PlanetTile FromString(string str);
    public static bool TryParse(string str, out PlanetTile tile);

    public override bool Equals(object obj);
    public bool Equals(PlanetTile other);
    public override int GetHashCode();
    public override string ToString();

    public static bool operator ==(PlanetTile lhs, PlanetTile rhs);
    public static bool operator !=(PlanetTile lhs, PlanetTile rhs);
    public static implicit operator int(PlanetTile tile);
    public static implicit operator PlanetTile(int tileId);

    public PlanetLayer Layer { get; }
    public RimWorld.PlanetLayerDef LayerDef { get; }
    public Tile Tile { get; }          // the underlying Tile data object
    public bool Valid { get; }

    public readonly int tileId;
    public static readonly PlanetTile Invalid;
}
```

Key facts:

- It is a **plain struct**, not `IExposable`. It is **not** a simple `int`
  wrapper conceptually any more (1.6 added multi-layer worlds — orbit,
  underground, etc. — a raw tile index is ambiguous without knowing which
  `PlanetLayer` it's on), but it still carries an implicit conversion
  to/from `int` for back-compat (that conversion only round-trips
  correctly for tiles on the default/root surface layer — for other
  layers use the `(int tileId, PlanetLayer layer)` or `(int, int layerId)`
  constructors and compare via `==`/`.Layer`, not by casting to `int` and
  comparing).
- Comparison: use `==`/`!=` (operator overloads) or `.Equals(...)`. Do not
  compare `.tileId` alone across layers.
- Validity: `PlanetTile.Invalid` is the sentinel (default `tileId` with no
  valid layer); check `tile.Valid`, not `tile != -1`.
- **Serialization — IL-verified**: `Verse.Scribe_Values.Look<T>` is fully
  generic and vanilla scribes `PlanetTile` fields with it directly, no
  special-case wrapper needed:

  ```
  // from RimWorld.Planet.WorldObject.ExposeData(), decompiled call site:
  Scribe_Values.Look(ref this.tile, "tile", default(PlanetTile), false);
  ```
  So: `Scribe_Values.Look(ref myTile, "myTile", PlanetTile.Invalid, false);`
  works exactly like scribing an `int` or any other blittable value struct.
  No `Scribe_Deep`/`Scribe_References` needed, and it is **not**
  `IExposable` so don't try to call `.ExposeData()` on it yourself.

---

## 2. `WorldObject`, `WorldObjectComp`, `WorldObjectCompProperties`

### `RimWorld.Planet.WorldObject`

```csharp
public class WorldObject : Verse.IExposable, Verse.ILoadReferenceable, Verse.ISelectable {
    public WorldObject();

    public T GetComponent<T>() where T : WorldObjectComp;
    public WorldObjectComp GetComponent(Type type);
    public bool TryGetComponent<T>(out T comp) where T : WorldObjectComp;
    public System.Collections.Generic.List<WorldObjectComp> AllComps { get; }

    public virtual void PostAdd();
    public virtual void PostMake();
    public virtual void PostRemove();
    public virtual void Destroy();
    public virtual void SpawnSetup();
    public virtual void SetFaction(RimWorld.Faction newFaction);

    public void DoTick();                       // public, non-virtual — see below
    protected virtual void Tick();               // family (protected), override this
    protected virtual void TickInterval(int delta);

    public virtual void ExposeData();

    public PlanetTile Tile { get; set; }
    public RimWorld.Faction Faction { get; }     // backed by private field `factionInt`
    public bool Spawned { get; }
    public bool Destroyed { get; }

    public RimWorld.WorldObjectDef def;
    public int ID;
    public int creationGameTicks;
    public System.Collections.Generic.List<string> questTags;
    public Verse.ThingOwner<Verse.Thing> rewards;
    public bool isGeneratedLocation;
}
```

Private-but-relevant fields seen in IL: `comps` (`List<WorldObjectComp>`),
`tile`, `destroyed`, `tickDelta`, `factionInt`.

**IL-verified tick behavior**: `WorldObjectsHolder.WorldObjectsHolderTick()`
calls `DoTick()` on **every** world object **every game tick** (it's not
gated by a hash/offset at the holder level). `DoTick()` itself always calls
`Tick()` (which calls `CompTick()` on every comp) every tick, then
separately calls `TickInterval(tickDelta)` (which calls
`CompTickInterval(delta)` on every comp) only when
`Verse.GenTicks.IsTickInterval(UpdateRateTickOffset, UpdateRateTicks)` is
true — i.e. **`Tick()`/`CompTick()` run every single tick for every world
object that exists**, so put expensive per-tick work in
`TickInterval`/`CompTickInterval` (rate-limited by the virtual
`UpdateRateTicks`/`UpdateRateTickOffset` properties), not in `Tick()`.

**IL-verified `ExposeData()` order** (from `WorldObject.ExposeData`):

```csharp
Scribe_Defs.Look(ref def, "def");
// on LoadingVars specifically: InitializeComps() runs here (builds `comps` from def.comps)
Scribe_Values.Look(ref tile, "tile", default(PlanetTile), false);
Scribe_Values.Look(ref ID, "ID", -1, false);
Scribe_Values.Look(ref creationGameTicks, "creationGameTicks", 0, false);
Scribe_Values.Look(ref destroyed, "destroyed", false, false);
Scribe_Values.Look(ref tickDelta, "tickDelta", 0, false);
Scribe_Values.Look(ref isGeneratedLocation, "isGeneratedLocation", false, false);
Scribe_References.Look(ref factionInt, "faction", false);
Scribe_Collections.Look(ref questTags, "questTags", LookMode.Undefined);
if (Scribe.mode != LoadSaveMode.Saving) // actually: skip only when mode == LoadingVars per IL, i.e. always saved/loaded but constructed via a specific path — see note below
    Scribe_Deep.Look(ref rewards, "rewards");
foreach (var comp in comps) comp.PostExposeData();
```
(The `rewards` branch's exact guard in IL compares `Scribe.mode == LoadSaveMode.LoadingVars` and skips the deep-look call on that pass only — treat this as an implementation detail, not something you need to replicate; just call `base.ExposeData()` and add your own fields after it.)

**IL-verified comp instantiation** (`WorldObject.InitializeComps()`,
private, runs during `LoadingVars`): iterates `def.comps`
(`List<WorldObjectCompProperties>`) and constructs one `WorldObjectComp`
per entry (via its `compClass`), matching the familiar `ThingComp`/
`CompProperties` pattern.

### `RimWorld.Planet.WorldObjectComp`

```csharp
public abstract class WorldObjectComp {
    protected WorldObjectComp();

    public virtual void Initialize(RimWorld.WorldObjectCompProperties props);
    public virtual void CompTick();
    public virtual void CompTickInterval(int delta);
    public virtual void PostExposeData();
    public virtual void PostMapGenerate();
    public virtual void PostMyMapRemoved();
    public void PostMyMapSettled();
    public virtual void PostDestroy();
    public virtual void PostPostRemove();       // called by WorldObject.PostRemove — IL-verified
    public virtual void PostCaravanFormed(Caravan caravan);
    public virtual System.Collections.Generic.IEnumerable<Verse.Gizmo> GetGizmos();
    public virtual System.Collections.Generic.IEnumerable<Verse.FloatMenuOption> GetFloatMenuOptions(Caravan caravan);
    public virtual System.Collections.Generic.IEnumerable<Verse.Gizmo> GetCaravanGizmos(Caravan caravan);
    public virtual string CompInspectStringExtra();
    public virtual string GetDescriptionPart();

    public bool ParentHasMap { get; }
    public Verse.IThingHolder ParentHolder { get; }

    public WorldObject parent;
    public RimWorld.WorldObjectCompProperties props;
}
```

Note: there is **no `PostAdd()` hook on `WorldObjectComp`** — only the
parent `WorldObject.PostAdd()` is called on add; comps are notified on
removal (`PostPostRemove`), destroy (`PostDestroy`), map generate/settle/
remove, and caravan-formed, but not on add. If you need "comp attached to
a freshly-created settlement" logic, do it in your comp's `Initialize()` or
override `WorldObjectComp`-owning `WorldObject.PostAdd()`... but since you
can't subclass vanilla `Settlement` easily without replacing
`worldObjectClass`, prefer driving your logic from the comp constructor /
`Initialize()`, or from your own `WorldComponent.WorldComponentTick()`
scanning for newly-added objects.

### `RimWorld.WorldObjectCompProperties`

```csharp
public class WorldObjectCompProperties {
    public WorldObjectCompProperties();
    public virtual void ResolveReferences(WorldObjectDef parentDef);
    public virtual System.Collections.Generic.IEnumerable<string> ConfigErrors(WorldObjectDef parentDef);
    public Type compClass;
}
```
Note namespace: `RimWorld.WorldObjectCompProperties` (NOT
`RimWorld.Planet.WorldObjectCompProperties` — it's outside the `.Planet`
namespace, unlike `WorldObject`/`WorldObjectComp`/`Settlement`).

### Real vanilla XML — comps on `WorldObjectDef` (`Data/Core/Defs/WorldObjectDefs/WorldObjects.xml`, verbatim)

```xml
<WorldObjectDef Name="Settlement" ParentName="StaticWorldObjectBase">
  <defName>Settlement</defName>
  <label>settlement</label>
  <description>A base of one of the factions.</description>
  <worldObjectClass>Settlement</worldObjectClass>
  <fullyExpandedInSpace>true</fullyExpandedInSpace>
  <expandingIcon>true</expandingIcon>
  <expandingIconPriority>10</expandingIconPriority>
  <canBePlayerHome>true</canBePlayerHome>
  <comps>
    <li Class="WorldObjectCompProperties_Abandon" />
    <li Class="WorldObjectCompProperties_TradeRequest" />
    <li Class="WorldObjectCompProperties_FormCaravan" />
    <li Class="WorldObjectCompProperties_TimedDetectionRaids" />
    <li Class="WorldObjectCompProperties_EnterCooldown" />
  </comps>
</WorldObjectDef>
```

`WorldObjectDef.comps` field: `public List<WorldObjectCompProperties> comps;`
— confirmed via `monop`.

### Patching a comp onto the vanilla `Settlement` def — confirmed mechanism

`Verse.PatchOperationAdd` still exists in 1.6 (`monop` confirms it, subclass
of `PatchOperationPathed`, XPath-based, unchanged shape:
`ApplyWorker(XmlDocument)`, `xpath`/`sourceFile` fields). So the standard
XML-patch approach works exactly as in earlier versions:

```xml
<Patch>
  <Operation Class="PatchOperationAdd">
    <xpath>Defs/WorldObjectDef[defName="Settlement"]/comps</xpath>
    <value>
      <li Class="YourMod.YourWorldObjectCompProperties" />
    </value>
  </Operation>
</Patch>
```
This is the right (and only sane) mechanism — there's no C# hook in 1.6 to
add comps to an existing `WorldObjectDef` other than editing its `comps`
list before defs are resolved (e.g. in a `DefsLoaded`-time patch), and XML
patching is simpler/safer than a Harmony postfix on `WorldObjectDef`
loading.

---

## 3. Settlement / MapParent / WorldObjectsHolder / utilities

### `RimWorld.Planet.Settlement : MapParent`

Notable members beyond what `MapParent`/`WorldObject` already give you:

```csharp
public Settlement_TraderTracker trader;
public System.Collections.Generic.List<Verse.Pawn> previouslyGeneratedInhabitants;
public bool namedByPlayer;
public string Name { get; set; }              // via INameableWorldObject
public bool EverVisited { get; }
public bool CanTradeNow { get; }
public RimWorld.TraderKindDef TraderKind { get; }
public override void Abandon(bool wasGravshipLaunch);
public override Verse.AcceptanceReport CanBeSettled { get; }
```
It also carries the full `WorldObject`/`MapParent` surface (`Tile`,
`Faction`, `def`, `AllComps`, `GetComponent<T>`, `DoTick`, `ExposeData`,
`PostAdd`, `PostRemove`, ...).

### `RimWorld.Planet.MapParent : WorldObject`

Adds the "this world object can own a `Verse.Map`" surface:

```csharp
public Verse.Map Map { get; }
public bool HasMap { get; }
public virtual Verse.MapGeneratorDef MapGeneratorDef { get; }
public virtual Verse.AcceptanceReport CanBeSettled { get; }
public virtual void Abandon(bool wasGravshipLaunch);
public virtual void PostMapGenerate();
public virtual bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject);
public void CheckRemoveMapNow();
public virtual void Notify_MyMapRemoved(Verse.Map map);
public virtual void Notify_MyMapSettled(Verse.Map map);
public virtual void Notify_MyMapAboutToBeRemoved();
```

### `RimWorld.Planet.WorldObjectsHolder` (accessed via `Verse.Find.WorldObjects`)

```csharp
public class WorldObjectsHolder : Verse.IExposable {
    public void Add(WorldObject o);
    public void Remove(WorldObject o);
    public bool Contains(WorldObject o);

    public Settlement SettlementAt(PlanetTile tile);
    public Settlement SettlementBaseAt(PlanetTile tile);      // "base" = has-a-map settlements only
    public MapParent MapParentAt(PlanetTile tile);
    public Site SiteAt(PlanetTile tile);
    public DestroyedSettlement DestroyedSettlementAt(PlanetTile tile);
    public bool AnySettlementAt(PlanetTile tile);
    public bool AnySettlementBaseAt(PlanetTile tile);
    public bool AnySettlementBaseAtOrAdjacent(PlanetTile tile);
    public bool AnySettlementBaseAtOrAdjacent(PlanetTile tile, out WorldObject wo);
    public bool AnyMapParentAt(PlanetTile tile);
    public bool AnyWorldObjectAt(PlanetTile tile);
    public bool AnyWorldObjectAt(PlanetTile tile, RimWorld.WorldObjectDef def);
    public bool AnyWorldObjectAt<T>(PlanetTile tile) where T : WorldObject;
    public T WorldObjectAt<T>(PlanetTile tile) where T : WorldObject;
    public bool TryGetWorldObjectAt<T>(PlanetTile tile, out T wo) where T : WorldObject;
    public System.Collections.Generic.IEnumerable<WorldObject> ObjectsAt(PlanetTile tileID);

    public System.Collections.Generic.List<WorldObject> AllWorldObjects { get; }
    public System.Collections.Generic.List<Settlement> Settlements { get; }
    public System.Collections.Generic.List<Settlement> SettlementBases { get; }
    public System.Collections.Generic.List<MapParent> MapParents { get; }
    public System.Collections.Generic.List<Site> Sites { get; }
    public System.Collections.Generic.List<DestroyedSettlement> DestroyedSettlements { get; }
    public System.Collections.Generic.List<Caravan> Caravans { get; }

    // per-layer variants (1.6 multi-layer worlds):
    public System.Collections.Generic.List<WorldObject> AllSettlementsOnLayer(PlanetLayer layer);
    public System.Collections.Generic.List<WorldObject> AllWorldObjectsOnLayer(PlanetLayer layer);
    public bool AnyFactionSettlementOnLayer(RimWorld.Faction faction, PlanetLayer layer);
    public bool AnyFactionSettlementOnRootSurface(RimWorld.Faction faction);
}
```

**IL-verified `Add`/`Remove` behavior** — this is the important part for
"create/destroy settlements deterministically":

```csharp
// WorldObjectsHolder.Add(WorldObject o), decompiled:
if (worldObjects.Contains(o)) { Log.Error(...); return; }
if (!o.Tile.Valid && !(o is PocketMapParent)) {
    Log.Error("... its tile is not set. Setting to 0.");
    o.Tile = new PlanetTile(0);
}
worldObjects.Add(o);
AddToCache(o);
o.SpawnSetup();   // <-- called automatically
o.PostAdd();      // <-- called automatically
```
```csharp
// WorldObjectsHolder.Remove(WorldObject o), decompiled:
if (!worldObjects.Contains(o)) { Log.Error(...); return; }
worldObjects.Remove(o);
RemoveFromCache(o);
o.PostRemove();   // <-- called automatically
```
So: **you must set `.Tile` (to a valid tile) before calling `Add`** — if you
don't, it silently forces the object onto tile 0 and logs an error, which
would break "deterministic placement". Do not call `SpawnSetup()`/`PostAdd()`
yourself — `Add()` does it. Symmetrically, don't call `PostRemove()`
yourself.

### `RimWorld.Planet.SettlementDefeatUtility`
```csharp
public static void CheckDefeated(Settlement factionBase);
public static bool IsDefeated(Verse.Map map, RimWorld.Faction faction);
```

### `RimWorld.Planet.SettlementUtility`
```csharp
public static void AffectRelationsOnAttacked(MapParent mapParent, ref Verse.TaggedString letterText);
public static void Attack(Caravan caravan, Settlement settlement);
public static bool IsPlayerAttackingAnySettlementOf(RimWorld.Faction faction);
```

### `RimWorld.Planet.SettlementProximityGoodwillUtility`
```csharp
public static void AppendProximityGoodwillOffsets(PlanetTile tile, List<Verse.Pair<Settlement,int>> outOffsets, bool ignoreIfAlreadyMinGoodwill, bool ignorePermanentlyHostile);
public static void CheckConfirmSettle(PlanetTile tile, Action settleAction, Action cancelAction, RimWorld.Building_GravEngine gravEngine);
public static void CheckSettlementProximityGoodwillChange();
public static int MaxDist { get; }
```
Relevant to "recently-raided settlement weakened" mechanics only tangentially
(this is about goodwill from settling too close to others, not about raid
history) — for raid cooldowns you'll want your own `WorldObjectComp` +
`HistoryEventsManager`/`HistoryEventDef` (see §9) rather than this utility.

Other settlement-adjacent classes that exist but weren't in the original
ask, noted for completeness: `SettlementAbandonUtility`,
`SettleInEmptyTileUtility`, `SettleInExistingMapUtility`, `SettleUtility`,
`SettlementNameGenerator` (`GenerateSettlementName(WorldObject, RulePackDef)`
— used by vanilla to name a settlement right after creation, see §4),
`AbandonedSettlement`, `DestroyedSettlement` (both `MapParent`/`WorldObject`
subclasses vanilla uses as "husk" markers left behind — could be a model
for your "weakened/regrouping" marker object instead of destroying+
recreating).

---

## 4. Creating a settlement — verified real code path

There is **no `SettlementGenerator` class** in 1.6 (searched the full type
list — only `SiteMaker`/`SiteMakerHelper` exist, for Sites, not
Settlements). The actual world-gen settlement-placement code path,
**IL-verified** end to end:

`RimWorld.Planet.WorldGenStep_Factions.GenerateFresh(string seed, PlanetLayer layer)`
→ calls →
`RimWorld.FactionGenerator.GenerateFactionsIntoWorldLayer(PlanetLayer layer, List<FactionDef> factions)`

Decompiled body of `GenerateFactionsIntoWorldLayer` (the per-settlement
placement loop), reconstructed from IL — **this is the exact vanilla
pattern for creating one settlement at runtime**:

```csharp
Faction faction = GenCollection.RandomElementByWeight(eligibleFactions, f => /* weight */);

WorldObject wo = WorldObjectMaker.MakeWorldObject(
    layer.Def.SettlementWorldObjectDef);   // usually WorldObjectDefOf.Settlement

wo.SetFaction(faction);

wo.Tile = TileFinder.RandomSettlementTileFor(
    layer, faction, mustBeAutoChoosable: false, extraValidator: null);

if (wo is INameableWorldObject nameable)
    nameable.Name = SettlementNameGenerator.GenerateSettlementName(wo, null);

Find.WorldObjects.Add(wo);   // triggers SpawnSetup() + PostAdd() automatically, see §3
```

This is directly usable as your own "spawn a settlement at runtime" recipe
(swap `RandomSettlementTileFor` for a tile you picked deterministically, or
for `TileFinder.TryFindPassableTileWithTraversalDistance` if you want it a
controlled distance from the player).

### `RimWorld.Planet.WorldObjectMaker`
```csharp
public static WorldObject MakeWorldObject(RimWorld.WorldObjectDef def);
public static WorldObject MakeWorldObject(RimWorld.WorldObjectDef def, WorldObject obj);
```

### `RimWorld.Planet.TileFinder`
```csharp
public static bool IsValidTileForNewSettlement(PlanetTile tile, System.Text.StringBuilder reason, bool forGravship);

public static PlanetTile RandomSettlementTileFor(RimWorld.Faction faction, bool mustBeAutoChoosable, Predicate<PlanetTile> extraValidator);
public static PlanetTile RandomSettlementTileFor(PlanetLayer layer, RimWorld.Faction faction, bool mustBeAutoChoosable, Predicate<PlanetTile> extraValidator);
public static PlanetTile RandomStartingTile();

public static bool TryFindNewSiteTile(out PlanetTile tile, int minDist, int maxDist, bool allowCaravans, List<RimWorld.LandmarkDef> allowedLandmarks, float selectLandmarkChance, bool canSelectComboLandmarks, TileFinderMode tileFinderMode, bool exitOnFirstTileFound, bool canBeSpace, PlanetLayer layer, Predicate<PlanetTile> validator);
public static bool TryFindNewSiteTile(out PlanetTile tile, PlanetTile nearTile, int minDist, int maxDist, bool allowCaravans, List<RimWorld.LandmarkDef> allowedLandmarks, float selectLandmarkChance, bool canSelectComboLandmarks, TileFinderMode tileFinderMode, bool exitOnFirstTileFound, bool canBeSpace, PlanetLayer layer, Predicate<PlanetTile> validator);

public static bool TryFindPassableTileWithTraversalDistance(PlanetTile rootTile, int minDist, int maxDist, out PlanetTile result, Predicate<PlanetTile> validator, bool ignoreFirstTilePassability, TileFinderMode tileFinderMode, bool canTraverseImpassable, bool exitOnFirstTileFound);

public static bool TryFindRandomPlayerTile(out PlanetTile tile, bool allowCaravans, Predicate<PlanetTile> validator, bool canBeSpace, PlanetLayer layer);

public static bool TryFindTileWithDistance(PlanetTile rootTile, int minDist, int maxDist, out PlanetTile result, Predicate<PlanetTile> validator, TileFinderMode tileFinderMode, bool exitOnFirstTileFound);
```
`TryFindPassableTileWithTraversalDistance` is exactly what you want for "spawn
a new NPC settlement roughly N tiles of travel from the player colony" — it
takes a `minDist`/`maxDist` in **traversal distance** (see `WorldGrid`
below), not straight-line distance.

### `RimWorld.FactionGenerator` (full surface, for spinning up a brand-new faction to own your dynamic settlements)
```csharp
public static void CreateFactionAndAddToManager(FactionDef facDef);
public static void CreateFactionAndAddToManager(PlanetLayer layer, FactionDef facDef);
public static Faction NewGeneratedFaction(FactionGeneratorParms parms);
public static Faction NewGeneratedFaction(PlanetLayer layer, FactionGeneratorParms parms);
public static Faction NewGeneratedFactionWithRelations(FactionDef facDef, List<FactionRelation> relations, bool hidden);
public static Faction NewGeneratedFactionWithRelations(FactionGeneratorParms parms, List<FactionRelation> relations);
public static Faction NewGeneratedFactionWithRelations(PlanetLayer layer, FactionDef facDef, List<FactionRelation> relations, bool hidden);
public static Faction NewGeneratedFactionWithRelations(PlanetLayer layer, FactionGeneratorParms parms, List<FactionRelation> relations);
public static float NewRandomColorFromSpectrum(Faction faction);
public static System.Collections.Generic.IEnumerable<FactionDef> ConfigurableFactions { get; }
```
(You more likely want existing factions to gain/lose settlements over time,
rather than creating new factions per settlement — but the API is there if
your dynamic-map design calls for it.)

**Destroying** a settlement deterministically: `Find.WorldObjects.Remove(settlement)`
(triggers `PostRemove()` automatically per §3). If it currently owns a
generated `Map`, you generally want to go through
`MapParent.Abandon(bool wasGravshipLaunch)` or vanilla's
`SettlementAbandonUtility`/`Settlement.Destroy()` rather than bare `Remove`,
so the map/pawns are cleaned up — **UNVERIFIED in depth**: I confirmed the
methods exist and their signatures, but did not trace `Destroy()`'s full IL
body; test in-game before relying on it to correctly tear down an
in-progress map.

---

## 5. `RimWorld.Planet.WorldGrid` — distance/traversal API

```csharp
public class WorldGrid : IDisposable, Verse.IExposable {
    // cheap, geometry-only (great-circle-ish approximation), used all over vanilla hot paths:
    public float ApproxDistanceInTiles(PlanetTile tileA, PlanetTile tileB);
    public float ApproxDistanceInTiles(PlanetLayer layer, float sphericalDistance);
    public float ApproxDistanceInTiles(float sphericalDistance);

    // EXPENSIVE — graph search over the tile-neighbor graph (BFS/Dijkstra-like), IL-verified:
    public int TraversalDistanceBetween(PlanetTile start, PlanetTile end,
        bool passImpassable = true, int maxDist = int.MaxValue, bool canTraverseLayers = false);

    public bool InBounds(PlanetTile tile);
    public bool IsNeighbor(PlanetTile tileA, PlanetTile tileB);
    public bool IsNeighborOrSame(PlanetTile tileA, PlanetTile tileB);
    public bool IsOnEdge(PlanetTile tileID);
    public int GetTileNeighborCount(PlanetTile tile);
    public PlanetTile GetTileNeighbor(PlanetTile tile, int adjacentId);
    public void GetTileNeighbors(PlanetTile tile, List<PlanetTile> outNeighbors);
    public int GetNeighborId(PlanetTile tileA, PlanetTile tileB);
    public int GetMaxTileNeighborCountEver(PlanetTile tile);

    public UnityEngine.Vector3 GetTileCenter(PlanetTile tile);
    public UnityEngine.Vector2 LongLatOf(PlanetTile tile);
    public float DistanceFromEquatorNormalized(PlanetTile tile);
    public float GetHeadingFromTo(PlanetTile fromTile, PlanetTile toTile);
    public RimWorld.Direction8Way GetDirection8WayFromTo(PlanetTile fromTile, PlanetTile toTile);
    public Verse.Rot4 GetRotFromTo(PlanetTile fromTile, PlanetTile toTile);

    public RimWorld.RoadDef GetRoadDef(PlanetTile fromTile, PlanetTile toTile, bool visibleOnly);
    public RimWorld.RiverDef GetRiverDef(PlanetTile fromTile, PlanetTile toTile, bool visibleOnly);
    public float GetRoadMovementDifficultyMultiplier(PlanetTile fromTile, PlanetTile toTile, System.Text.StringBuilder explanation);

    public int TilesNumWithinTraversalDistance(int traversalDist);
    public int TilesCount { get; }
    public Tile this[PlanetTile tile] { get; }
}
```

**Cheap vs expensive — IL-verified**:
- `ApproxDistanceInTiles(...)` is a closed-form geometric calculation (no
  loop over the tile graph) — safe to call in tight loops (vanilla does,
  e.g. raid/threat selection scans many settlements against it).
- `TraversalDistanceBetween(...)` walks the tile-neighbor graph starting
  from `start` (a BFS/Dijkstra-style search up to `maxDist`), and the class
  caches the *last* `(start, end)` result pair
  (`cachedTraversalDistanceForStart`/`cachedTraversalDistanceForEnd`/
  `cachedLayer`) to avoid redundant work for repeated queries from the same
  start tile — but a *fresh* start tile is still a real graph search. Don't
  call this every tick for many settlements; cache your own results or call
  it only on the (infrequent) events that need exact travel distance (e.g.
  "is this new settlement's tile close enough to the player to matter").
  Default params: `passImpassable = true`, `maxDist = int.MaxValue`,
  `canTraverseLayers = false`.

Access: `Verse.Find.WorldGrid` (standard).

---

## 6. `WorldComponent` / `GameComponent` / `MapComponent`

```csharp
// RimWorld.Planet.WorldComponent
public abstract class WorldComponent : Verse.IExposable {
    public WorldComponent(World world);          // REQUIRED ctor shape — vanilla instantiates via reflection with the World instance
    public virtual void FinalizeInit(bool fromLoad);
    public virtual void WorldComponentTick();
    public virtual void WorldComponentUpdate();
    public virtual void WorldComponentOnGUI();
    public virtual void ExposeData();
    public World world;
}
```
```csharp
// Verse.GameComponent
public abstract class GameComponent : IExposable {
    protected GameComponent();                    // base ctor takes nothing...
    public virtual void FinalizeInit();
    public virtual void StartedNewGame();
    public virtual void LoadedGame();
    public virtual void GameComponentTick();
    public virtual void GameComponentUpdate();
    public virtual void GameComponentOnGUI();
    public virtual void AppendDebugString(System.Text.StringBuilder sb);
    public virtual void ExposeData();
}
```
**IL-verified**: `Verse.Game.ExposeData()` scribes its component list as
```csharp
Scribe_Collections.Look(ref components, "components", LookMode.Deep, this);
```
i.e. `ctorArgs = new object[] { this /* the Game instance */ }`. That means
even though the base `GameComponent` class itself only declares a no-arg
protected constructor, **your subclass must still declare a public
constructor taking a single `Game` parameter** (the deep-scribe/Activator
path needs a matching constructor to call), by long-standing convention:
```csharp
public class MyGameComponent : GameComponent {
    public MyGameComponent(Game game) { }
    ...
}
```
(This mirrors `WorldComponent`'s explicit `(World world)` ctor and
`MapComponent`'s explicit `(Map map)` ctor below — `GameComponent` is the
odd one out in not declaring the parameterized ctor on the base class
itself, but the save system still requires it on your subclass.)

```csharp
// Verse.MapComponent
public abstract class MapComponent : IExposable {
    public MapComponent(Map map);                 // REQUIRED ctor shape, `map` field is readonly
    public virtual void FinalizeInit();
    public virtual void MapGenerated();
    public virtual void MapRemoved();
    public virtual void MapComponentTick();
    public virtual void MapComponentUpdate();
    public virtual void MapComponentOnGUI();
    public virtual void MapComponentDraw();
    public virtual void ExposeData();
    public readonly Map map;
}
```

For your dynamic-world-map feature, `WorldComponent` is almost certainly
the right home for a "tick over all settlements, expire weakened states,
roll for expansion/collapse" driver — access existing instances via
`Verse.Find.World.GetComponent<T>()` (confirmed present on `World`, not
separately dumped here but same `GetComponent<T>`/`GetComponent(Type)`
pattern as `WorldObject`).

---

## 7. Scribe API for persistence

```csharp
// Verse.Scribe_Values
public static void Look<T>(ref T value, string label, T defaultValue = default, bool forceSave = false);
```
Generic, works for any value type including `PlanetTile`, `int`, `bool`,
enums, etc. — confirmed via IL to be exactly how `PlanetTile` fields are
scribed in vanilla (§1).

```csharp
// Verse.Scribe_References
public static void Look<T>(ref T refee, string label, bool saveDestroyedThings = false);
```
Use for `RimWorld.Faction` fields (vanilla does exactly this for
`WorldObject.factionInt`, label `"faction"`).

```csharp
// Verse.Scribe_Deep
public static void Look<T>(ref T target, string label, params object[] ctorArgs);
public static void Look<T>(ref T target, bool saveDestroyedThings, string label, params object[] ctorArgs);
```
Use for a single owned `CustomClass` instance (not a collection).

```csharp
// Verse.Scribe_Collections
public static void Look<T>(ref List<T> list, string label, LookMode lookMode, params object[] ctorArgs);
public static void Look<T>(ref List<T> list, string label, bool saveDestroyedThings, LookMode lookMode, params object[] ctorArgs);
public static void Look<K,V>(ref Dictionary<K,V> dict, string label, LookMode keyLookMode, LookMode valueLookMode);
public static void Look<K,V>(ref Dictionary<K,V> dict, string label, LookMode keyLookMode, LookMode valueLookMode,
    ref List<K> keysWorkingList, ref List<V> valuesWorkingList, bool logNullErrors, bool saveDestroyedKeys, bool saveDestroyedValues);
public static void Look<T>(ref HashSet<T> valueHashSet, string label, LookMode lookMode);
public static void Look<T>(ref Queue<T> valueQueue, string label, LookMode lookMode);
public static void Look<T>(ref Stack<T> valueStack, string label, LookMode lookMode);
```

### Your specific persistence needs

- **`List<CustomClass>`**: straightforward —
  `Scribe_Collections.Look(ref myList, "myList", LookMode.Deep);` (add
  `ctorArgs` if `CustomClass` needs constructor args on load).
- **Faction references**: `Scribe_References.Look(ref myFaction, "myFaction");`
  (Faction is `ILoadReferenceable`, standard reference-scribe pattern.)
- **`PlanetTile` values**: `Scribe_Values.Look(ref myTile, "myTile", PlanetTile.Invalid);` — see §1.
- **`SortedDictionary<int, CustomClass>` — IMPORTANT GAP**: `Scribe_Collections`
  has **no overload for `SortedDictionary<K,V>`** — only `Dictionary<K,V>`,
  `HashSet<T>`, `List<T>`, `Queue<T>`, `Stack<T>`. You have two practical
  options, neither of which is "call `Scribe_Collections.Look` directly on
  a `SortedDictionary`":
  1. Scribe a regular `Dictionary<int, CustomClass>` field
     (`Scribe_Collections.Look(ref myDict, "myDict", LookMode.Value, LookMode.Deep);`)
     and rebuild a `SortedDictionary<int, CustomClass>` from it in
     `FinalizeInit`/after loading (`new SortedDictionary<int,CustomClass>(myDict)`),
     keeping the `Dictionary` as your on-disk representation and the
     `SortedDictionary` as an in-memory convenience/derived view; or
  2. Scribe two parallel `List<int>` / `List<CustomClass>` (keys/values)
     with `Scribe_Collections.Look` (`LookMode.Value` / `LookMode.Deep`
     respectively) and zip them back into a `SortedDictionary` after
     loading.
  Either way, **don't** pass a `SortedDictionary` directly to
  `Scribe_Collections.Look<K,V>` — it will not compile (no matching generic
  overload) since only `Dictionary<K,V>` is supported by ref.

`LookMode` enum values (for reference, not separately dumped in depth here
but standard/unchanged): `Value`, `Deep`, `Reference`, `Def`, `LocalTargetInfo`,
`GlobalTargetInfo`, `Undefined` — pick `Value` for tiles/ints/bools/enums,
`Deep` for owned custom classes, `Reference` for `Thing`/`Faction`/etc.,
`Def` for def references. **UNVERIFIED**: I did not re-dump `LookMode`
itself in this pass (it's unchanged from prior versions and used correctly
above per the `Scribe_Collections` signatures, but flagging since the task
asked for exactness).

---

## 8. `Verse.Rand` and `Verse.Gen.HashCombineInt`

```csharp
public static class Rand {
    public static void PushState();
    public static void PushState(int replacementSeed);
    public static void PopState();
    public static void EnsureStateStackEmpty();
    public static int Seed { set; }
    public static float Value { get; }
    public static int Int { get; }
    public static bool Bool { get; }
    public static int Range(int minInclusive, int maxExclusive);
    public static float Range(float minInclusive, float maxInclusive);
    public static int RangeInclusive(int minInclusive, int maxInclusive);
    public static int RangeSeeded(int minInclusive, int maxExclusive, int specialSeed);
    public static float ValueSeeded(int specialSeed);
    public static bool ChanceSeeded(float chance, int specialSeed);
    // ...and many Element<T>/ElementByWeight<T> convenience overloads (2-6 args or params array)
}
```
Exactly two `PushState` overloads: parameterless (pushes current state,
Rand continues from where it was) and `PushState(int replacementSeed)`
(pushes current state, then reseeds — useful for deterministic per-object
randomness, e.g. `Rand.PushState(Gen.HashCombineInt(baseSeed, settlementID)); ...; Rand.PopState();`).

```csharp
// Verse.Gen
public static int HashCombineInt(int seed, int value);
public static int HashCombineInt(int v1, int v2, int v3, int v4);
public static int HashCombine<T>(int seed, T obj);
public static int HashCombineStruct<T>(int seed, T obj) where T : struct;
```
Two `HashCombineInt` overloads confirmed: 2-int and 4-int forms.

---

## 9. History events and letters

### `RimWorld.HistoryEventDef : Verse.Def`
Plain `Def` — no special fields beyond the standard `defName`/`label`/
`description`/`modExtensions`/etc. Real vanilla XML
(`Data/Core/Defs/HistoryEventDefs/HistoryEventDefs.xml`, verbatim):
```xml
<HistoryEventDef>
  <defName>CutTree</defName>
  <label>cut tree</label>
</HistoryEventDef>
```
So a custom event def is just:
```xml
<HistoryEventDef>
  <defName>YourMod_SettlementRaidedPlayer</defName>
  <label>settlement raided player</label>
</HistoryEventDef>
```

### `RimWorld.HistoryEvent` (struct)
```csharp
public struct HistoryEvent {
    public HistoryEvent(HistoryEventDef def);
    public HistoryEvent(HistoryEventDef def, SignalArgs args);
    public HistoryEvent(HistoryEventDef def, Verse.NamedArgument arg1);
    public HistoryEvent(HistoryEventDef def, Verse.NamedArgument arg1, Verse.NamedArgument arg2);
    public HistoryEvent(HistoryEventDef def, Verse.NamedArgument arg1, Verse.NamedArgument arg2, Verse.NamedArgument arg3);
    public HistoryEvent(HistoryEventDef def, Verse.NamedArgument arg1, Verse.NamedArgument arg2, Verse.NamedArgument arg3, Verse.NamedArgument arg4);
    public HistoryEvent(HistoryEventDef def, params Verse.NamedArgument[] args);
    public HistoryEventDef def;
    public SignalArgs args;
}
```
`NamedArgument`s are how you pass e.g. the faction ("doer") into the event
for `HistoryEventArgsNames.Doer`-style lookups elsewhere in the game (used
for ideoligion/precept reactions) — for a simple internal record you can
just use `new HistoryEvent(YourDef)` or `new HistoryEvent(YourDef, args)`.

### `RimWorld.HistoryEventsManager` (via `Verse.Find.HistoryEventsManager`)
```csharp
public void RecordEvent(HistoryEvent historyEvent, bool canApplySelfTookThoughts = true);
public bool Any(HistoryEventDef def, Faction forFaction);
public int GetLastTicksGame(HistoryEventDef def, Faction forFaction);
public int GetRecentCountWithinTicks(HistoryEventDef def, int duration, Faction forFaction);
public void GetRecent(HistoryEventDef def, int duration, List<int> outTicks, List<int> outCustomGoodwill, Faction forFaction);
```
Recording a custom event:
```csharp
Find.HistoryEventsManager.RecordEvent(
    new HistoryEvent(YourHistoryEventDefOf.SettlementRaidedPlayer,
        new NamedArgument(theFaction, "FACTION")));
```
Then later check e.g. "did faction X raid recently":
```csharp
bool raidedRecently = Find.HistoryEventsManager.Any(YourDef, theFaction);
int recentCount = Find.HistoryEventsManager.GetRecentCountWithinTicks(YourDef, someTickWindow, theFaction);
```
**UNVERIFIED**: whether `Any`/`GetRecentCountWithinTicks` require the event
to have been recorded *with* that same `Faction` passed as a
`NamedArgument` for the `forFaction` filter to match, vs. matching purely
on `def`. Not traced in IL this pass — test empirically, or fall back to
tracking your own per-settlement "last raided tick" field on your
`WorldObjectComp` (simpler and fully under your control) rather than
depending on the vanilla history-event faction-filtering semantics.

### `RimWorld.LetterDefOf`
```csharp
public static Verse.LetterDef ThreatBig, ThreatSmall, NegativeEvent, NeutralEvent, PositiveEvent,
    Death, AcceptVisitors, AcceptJoiner, GameEnded, ChoosePawn,
    RitualOutcomeNegative, RitualOutcomePositive, BundleLetter;
// [MayRequireIdeology] RelicHuntInstallationFound
// [MayRequireBiotech] BabyBirth, BabyToChild, ChildToAdult, ChildBirthday, Bossgroup
// [MayRequireAnomaly] AcceptCreepJoiner, EntityDiscovered
```
For "settlement regrouping" / "settlement destroyed" notifications with no
new art, `NeutralEvent` or `NegativeEvent` are the right picks (both DLC-free,
core-only).

### `Verse.Find.LetterStack.ReceiveLetter` overloads — IL-verified defaults
```csharp
public void ReceiveLetter(Letter let, string debugInfo = null, int delayTicks = 0, bool playSound = true);

public void ReceiveLetter(TaggedString label, TaggedString text, LetterDef textLetterDef,
    string debugInfo = null, int delayTicks = 0, bool playSound = true);   // no LookTargets

public void ReceiveLetter(TaggedString label, TaggedString text, LetterDef textLetterDef, LookTargets lookTargets,
    RimWorld.Faction relatedFaction = null, RimWorld.Quest quest = null,
    List<ThingDef> hyperlinkThingDefs = null, string debugInfo = null,
    int delayTicks = 0, bool playSound = true);
```
Simplest call for your use case:
```csharp
Find.LetterStack.ReceiveLetter(
    "Settlement regrouping".Translate(),
    "The {0} settlement was routed and is regrouping. It will not raid again for a while.".Translate(factionLabel),
    LetterDefOf.NeutralEvent,
    new LookTargets(settlement));
```
(The `LookTargets`-taking overload internally builds a `ChoiceLetter` via
`Verse.LetterMaker.MakeLetter(...)` then calls the 4-arg
`ReceiveLetter(Letter, string, int, bool)` overload — confirmed via IL.)

---

## 10. Settings UI: `ModSettings`, `Mod`, `Listing_Standard`, `Widgets`

```csharp
// Verse.ModSettings
public abstract class ModSettings : IExposable {
    protected ModSettings();
    public virtual void ExposeData();
    public void Write();
    public Mod Mod { get; }
}

// Verse.Mod
public abstract class Mod {
    public Mod(ModContentPack content);
    public virtual void DoSettingsWindowContents(UnityEngine.Rect inRect);
    public virtual string SettingsCategory();
    public T GetSettings<T>() where T : ModSettings, new();
    public virtual void WriteSettings();
    public ModContentPack Content { get; }
}
```
Standard shape unchanged from prior versions: subclass `Mod`, override
`DoSettingsWindowContents`/`SettingsCategory`, keep your `ModSettings`
subclass alive via `GetSettings<T>()`, call `settings.Write()` (or rely on
`Mod.WriteSettings()`) when the settings window closes.

### `Verse.Listing_Standard` — all the widgets asked about, confirmed present in 1.6:
```csharp
public class Listing_Standard : Listing {
    public Listing_Standard(GameFont font);
    public Listing_Standard();
    public override void Begin(UnityEngine.Rect rect);
    public override void End();

    public void CheckboxLabeled(string label, ref bool checkOn, float tabIn = 0f);
    public void CheckboxLabeled(string label, ref bool checkOn, string tooltip, float height = 0f, float labelPct = 1f);
    public bool CheckboxLabeledSelectable(string label, ref bool selected, ref bool checkOn);

    public float Slider(float val, float min, float max);
    public float SliderLabeled(string label, float val, float min, float max, float labelPct = 1f, string tooltip = null);

    public UnityEngine.Rect Label(string label, float maxHeight = -1f, TipSignal? tipSignal = null);
    public UnityEngine.Rect Label(TaggedString label, float maxHeight = -1f, string tooltip = null);
    public void LabelDouble(string leftLabel, string rightLabel, string tip = null);

    public void Gap(float gapHeight);
    public void GapLine(float gapHeight = 12f);

    public Listing_Standard BeginSection(float height, float sectionBorder = 4f, float bottomBorder = 4f);
    public void EndSection(Listing_Standard listing);

    public string TextEntry(string text, int lineCount = 1);
    public string TextEntryLabeled(string label, string text, int lineCount = 1);
    public void TextFieldNumeric<T>(ref T val, ref string buffer, float min = 0f, float max = float.MaxValue) where T : struct;
    public void TextFieldNumericLabeled<T>(string label, ref T val, ref string buffer, float min = 0f, float max = float.MaxValue) where T : struct;

    public void NewColumn();
    public void Indent(float gapWidth = 6f);
    public void Outdent(float gapWidth = 6f);

    public float ColumnWidth { get; set; }
    public float CurHeight { get; }
}
```
`BeginSection`/`EndSection` is the "collapsible section"-adjacent primitive
— it's actually a bordered sub-panel (draws a background box), not a
collapse/expand toggle; for an actually-collapsible section you compose it
yourself with a `CheckboxLabeled`/button toggling a `bool` that gates
whether you call the inner `Listing_Standard` block that turn — there is
no separate `BeginCollapsibleSection` API in 1.6 (**UNVERIFIED as
"doesn't exist"** in the strict sense — confirmed via `monop` that
`Listing_Standard`'s full public surface has no such member, and nothing
matching that name turned up in the full type grep either).

### `Verse.Widgets` (static-method equivalents, for drawing outside a `Listing_Standard`, e.g. custom rects)
```csharp
public static void CheckboxLabeled(UnityEngine.Rect rect, string label, ref bool checkOn, bool disabled = false, ...);
public static void Checkbox(UnityEngine.Vector2 topLeft, ref bool checkOn, float size = 24f, ...);
public static float HorizontalSlider(UnityEngine.Rect rect, float value, float min, float max, bool middleAlignment = false, string label = null, ...);
public static void HorizontalSlider(UnityEngine.Rect rect, ref float value, FloatRange range, string label = null, float roundTo = -1f);
public static void Label(UnityEngine.Rect rect, string label);
public static void Label(UnityEngine.Rect rect, TaggedString label);
public static void DrawMenuSection(UnityEngine.Rect rect);
public static void ListSeparator(ref float curY, float width, string label);
public const float CheckboxSize = 24f;
```
All confirmed present via `monop` on 1.6's `Verse.Widgets`.

---

## Summary of things flagged UNVERIFIED / needing empirical testing

1. `Settlement.Destroy()` / `MapParent.Abandon()`'s exact teardown sequence
   for an active map with pawns still on it — signatures confirmed, full
   body not traced.
2. Whether `HistoryEventsManager.Any`/`GetRecentCountWithinTicks`'s
   `forFaction` filter matches only when the event was recorded with a
   matching `NamedArgument`, or some other mechanism — recommend rolling
   your own per-settlement "last raided" tick counter on a custom
   `WorldObjectComp` instead of depending on this for correctness.
3. `LookMode` enum's exact member set — used correctly above by inference
   from unchanged prior-version behavior and the `Scribe_Collections`
   signatures, but not independently re-dumped this pass.
4. No `SettlementGenerator` class exists in 1.6 — confirmed by exhaustive
   type-list grep, not just absence-of-evidence; the real path is
   `FactionGenerator.GenerateFactionsIntoWorldLayer` (§4).
5. `Listing_Standard`/`Widgets` have no dedicated "collapsible section"
   widget in 1.6 — confirmed by full public-surface dump, not by memory.
