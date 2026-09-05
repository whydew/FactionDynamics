# Faction Dynamics — Design Document & Implementation Plan

**Working title:** *Faction Dynamics: Diplomacy & War*
**User's original title:** *Faction & Diplomacy Enhanced* (kept as the in-game display name if preferred — see Naming below)
**Target:** RimWorld 1.6, Harmony (`brrainz.harmony`), full RimWorld Multiplayer (`rwmt.Multiplayer`) compatibility
**Author:** Matt · **Package ID (proposed):** `matt.factiondynamics`
**Status:** Design phase — no code yet, per request

---

## 0. Naming

"Faction & Diplomacy Enhanced" works fine as a Workshop title, but it reads like a generic "Enhanced"-suffix mod and doesn't say what makes this one different. Three alternatives, ranked:

1. **Faction Dynamics: Diplomacy & War** *(recommended)* — clear, searchable, matches the "living world" pitch. Short internal name `FactionDynamics` fits your existing folder-naming pattern (`NewLanding`, `SavageBiomes`, `MoreBiomes`).
2. **Living Factions** — punchier, but collides in spirit with the existing Workshop mod *Dynamic Factions* (see §5) and could cause user confusion even though the feature sets differ substantially.
3. **Rival Factions: Diplomacy & War** — leans into the allied-warfare/treaty angle.

The rest of this document uses **Faction Dynamics** as the working name and `FD` as a code prefix. Swap freely — nothing below depends on the final name except the `packageId` and C# namespace.

---

## 1. High-Level Design Overview

### 1.1 Vision

RimWorld's faction layer is mostly a scoreboard: goodwill numbers move, raids arrive as a wall of "kill the colony," and settlements are static icons that only matter when a quest points at them. Faction Dynamics turns the world map into something the player has to actually manage — settlements that grow, shrink, and vanish for legible reasons; raids that come from *somewhere* and *want something*; and diplomacy that has real, escalating stakes instead of a single goodwill slider.

### 1.2 Design Pillars

- **Modular by construction.** Every feature ships as an independently toggleable module. A player who only wants geography-aware raids should be able to disable dynamic settlements, quests, and outposts entirely, and vice versa. This isn't just a settings checkbox — it's a code-organization principle (see §2.1).
- **Multiplayer-safe by construction, not by patch.** Every new piece of mutable shared state is designed deterministic-first: `WorldComponent`/`MapComponent` state exposed via `ExposeData`, all player-triggered state changes routed through `[SyncMethod]`-registered entry points, `SortedDictionary`/`SortedSet` (never plain `Dictionary`/`HashSet` iteration order) for anything enumerated during simulation, and `Rand.PushState()`-scoped RNG seeded from deterministic inputs (tile ID, faction loadID, tick) rather than `Environment.TickCount`/`DateTime` or unseeded `UnityEngine.Random`.
- **Reuse vanilla assets aggressively.** No new art or audio in v1.0. Every new gizmo, alert, and letter reuses existing `TexButton`/`TexCommand` icons, vanilla letter defs (`LetterDefOf`), and vanilla sound defs (`SoundDefOf`). New Textures/ only appear if a genuinely new UI concept (e.g., a "treaty" icon) has no reasonable vanilla stand-in — and even then, start by recoloring/compositing existing sprites before commissioning anything.
- **Reactive, not busywork.** Every new system is built to *reduce* required attention in the common case (settlements regroup and recover on their own, caravans run themselves once configured, allies request help through the existing letter/quest UI) and only demand player decisions at meaningful branch points (accept/reject a summit term, answer an ally's call for help, choose a champion).

### 1.3 Feature-to-Module Map & Prioritization

| # | Module (code name) | Feature | v1.0 | v1.x | v2.0+ |
|---|---|---|---|---|---|
| M1 | `Settlements` | Dynamic settlement spawn/collapse/expansion, raid-weakened "regrouping" state | ✅ Core | | |
| M2 | `RaidGeography` | Distance-weighted raid frequency/size/arrival time | ✅ Core | | |
| M3 | `RaidMotivations` | Goal-oriented raid AI (food, anti-pollution, revenge, resource theft) | ✅ Core (food + resource theft + revenge) | Anti-pollution, ideological punishment | Custom motivation API for other mods |
| M4 | `Quests.Raiding` | Raid/defend-a-settlement quests | ✅ Core | | |
| M5 | `Quests.Bounties` | Empire/faction bounties on named pawns | ✅ Core | | |
| M6 | `Diplomacy.Summits` | Multi-faction summits, conditional non-aggression pacts, treaty breach consequences | | ✅ v1.1 | |
| M7 | `Warfare.Alliances` | Coordinated allied raids, double-crosses, "help your ally or else" | | ✅ v1.1 | Full faction-vs-faction war campaigns |
| M8 | `Warfare.Tribute` | Mafia-style protection rackets | | ✅ v1.2 | |
| M9 | `Outposts` | Sustainable far-away outposts | | ✅ v1.2 | |
| M10 | `Logistics.Caravans` | Semi-automated trade caravans/trade lines | | ✅ v1.3 | |
| M11 | `Conflict.TrialOfChampions` | Duel-based dispute resolution | | ✅ v1.3 | |

**Why this order:** M1–M3 are the foundation everything else reads from (settlement strength/state, distance, and per-faction "mood") and are the highest-value, lowest-risk slice — they're additive to existing systems (`Settlement`, `IncidentWorker_RaidEnemy`) rather than replacing them wholesale, so they're the safest to prove out for multiplayer determinism first. M4–M5 build entirely on the vanilla `QuestScriptDef`/`QuestNode` system, which is already MP-safe by design (quests are server-authoritative data), making them a natural second slice. M6–M8 (diplomacy, alliances, tribute) are the most mechanically novel and highest-desync-risk (multi-party negotiated state, retaliation scheduling) and benefit from M1–M3 already being battle-tested. M9–M11 are valuable but self-contained "quality of life / late game" features that don't block anything else, so they're pushed later deliberately. Each milestone is playable and shippable on its own — nothing in v1.0 requires v1.1+ code to exist.

### 1.4 Non-Goals (v1.0)

- No full faction-vs-faction territorial conquest simulation (that's a v2+ stretch goal, and it's the single riskiest thing on this list for both balance and desyncs — see Dynamic Factions' own admitted balance problems, §5).
- No new pawnkinds, weapons, or apparel. Motivated raids reuse existing raid strategies and pawnkinds; they change *behavior*, not *rosters*.
- No romance/relationship-with-NPC-leaders systems.

---

## 2. Technical Architecture

### 2.1 The Module System

Every module (M1–M11) is gated at three layers simultaneously, so disabling a module is airtight rather than "mostly off":

1. **Settings layer** — `FactionDynamicsSettings` (a `ModSettings` subclass) exposes one `bool` per module plus module-specific tuning. Read once into a static `FDSettings.Enabled` snapshot on settings-close and game-load (never read `LoadedModManager.GetMod<T>().GetSettings<T>()` from hot paths).
2. **Component layer** — Each module's `WorldComponent`/`GameComponent` no-ops its `WorldComponentTick`/`FinalizeInit` when its module flag is off, and — critically — **never removes or corrupts save data** when toggled off mid-save; it just stops advancing its own state. This matches how vanilla handles disabled DLC content and avoids the "can't be removed mid-save" failure mode that Dynamic Factions is criticized for (§5).
3. **Harmony layer** — Patches are grouped into per-module `PatchCategory`-tagged classes and conditionally applied via `Harmony.CreateClassProcessor(type).Patch()` gated on the module flag, evaluated once at `StaticConstructorOnStartup`, mirroring the category-based patch application already used in RimWorld Multiplayer's own `MultiplayerStatic.DoPatches` (`SetCategory`/`TryPatch` pattern — see `Source/Client/MultiplayerStatic.cs` in `rwmt.Multiplayer`). Toggling a setting takes effect after restart, which is the same UX RimWorld players already expect from other mod option toggles that add/remove Harmony patches.

Multiplayer wrinkle: settings must be **host-authoritative**. `FactionDynamicsSettings` participates in Multiplayer's `SyncConfigs` (the same mechanism `EarlyInit.ProcessEnvironment → SyncConfigs.Init()` sets up for mod settings sync in `rwmt.Multiplayer`), so joining clients can't silently run with different module sets than the host — that alone would desync any module whose Harmony patches are conditionally applied.

### 2.2 Multiplayer-Safety Strategy

This is the load-bearing section. Three patterns, applied uniformly:

**(a) Detect-then-wire, never hard-reference.** Exactly like your existing `NewLanding/Source/Compat/MultiplayerCompat.cs`: detection via `LoadedModManager.RunningModsListForReading` package-ID scan touches zero `Multiplayer.API` types, so the mod loads and runs standalone with Multiplayer absent. Only `WireUp()` (marked `[MethodImpl(MethodImplOptions.NoInlining)]`, exactly as in your existing code) references `Multiplayer.API`, and it only runs after detection succeeds. `Multiplayer.API.dll` is a `Private=false` compile-time-only reference in the `.csproj`, same as `NewLanding.csproj`.

**(b) Every player-triggered mutation is a `[SyncMethod]`.** Anything the player can click that changes shared world/map state — accepting a summit term, calling for allied help, paying tribute, choosing a champion, dismissing a bounty — is a static method registered via `MP.RegisterSyncMethod` (or annotated `[SyncMethod]` + `Sync.RegisterAllAttributes`, matching the pattern in `Multiplayer`'s own `Source/Client/Factions/MultifactionPatches.cs`, e.g. `PawnChangeRelationGizmo.SetRelation`). The Gizmo/dialog `action` callback does *only* input capture (which pawn, which map, which faction) and calls the synced method; it never mutates state directly on the UI thread. In singleplayer the same method just runs inline — no branching needed in the method body itself, only in whether the call is wrapped through `MP.IsInMultiplayer`-gated UI (disable a button instead of letting it silently do nothing, same as `AllowTool`'s `Alert_NoUrgentStorage` pattern from your compat fix notes).

**(c) Determinism discipline for autonomous (non-player-triggered) simulation.** Settlement lifecycle, raid-motivation AI, and caravan automation run on world/map ticks for *every* client identically, so they must never read non-deterministic sources:
   - **RNG:** every stochastic decision (does this settlement collapse this check, which motivation does this raid pick, does the caravan skip a stop) uses `Rand.PushState()` scoped around a seed built from stable IDs (`Gen.HashCombineInt(tile, faction.loadID, GenTicks.TicksGame)` style), never `UnityEngine.Random` and never wall-clock time. This is exactly why `rwmt.Multiplayer`'s `EarlyPatches` install a prefix/finalizer around `Def`'s constructor via `RandPatches` — the whole mod is built around "every RNG call must be replayable identically." We follow the same discipline in our own code rather than relying on their patch to save us (it can't reach RNG calls inside *our* methods).
   - **Iteration order:** any collection enumerated while mutating simulation state (all settlements checked for collapse this tick, all raid parties evaluated for retreat) is a `SortedDictionary`/`List` sorted by a stable key (`loadID`), mirroring `MultiplayerWorldComp.factionData` (`SortedDictionary<int, FactionWorldData>` — the comment right there says *"to ensure determinism"*).
   - **Timing:** state advances on `WorldComponentTick`/`MapComponentTick` (which Multiplayer's `AsyncWorldTimeComp`/`AsyncTimeComp` already schedule identically for every client), never `Update()`/`FixedUpdate()`/coroutines, which run off the synced tick and are exactly the bug class documented in your own `AllowTool` compat fix (`FixedUpdate()` doing live simulation queries off-tick).
   - **Persistence:** every new component implements `ExposeData()` properly, including session-scoped negotiation state (an in-progress summit is itself an `MpComp`-style *session* object — see §2.3 — persisted the same way Multiplayer persists its own `SessionManager`/`IHasSessionData` sessions, so a save/reload or a client reconnecting mid-negotiation doesn't desync or drop state).
   - **Faction context:** any code that reads/writes `Faction.OfPlayer` implicitly (a lot of vanilla helper methods do) must be reviewed against Multiplayer's multi-faction model — `Map.PushFaction`/`PopFaction` (`Source/Client/Factions/FactionExtensions.cs`) exists precisely because MP sessions can have more than one player faction sharing a map. Our per-faction bookkeeping (tribute owed, treaty state, alliance standing) is keyed by `Faction.loadID`, never assumed-singular "the player."

**(d) Fail loud, fail safe.** Every Harmony patch is applied via a `TryPatch`-style wrapper that logs and continues rather than throwing (matching `MultiplayerStatic.DoPatches`'s own `TryPatch`), and every module independently no-ops if its own `StaticConstructorOnStartup` setup throws, rather than taking the whole mod down.

### 2.3 New Components, Comps, and Defs

**World-level (persist once per save, keyed by faction/tile):**

| Type | Purpose |
|---|---|
| `FactionDynamicsWorldComp : WorldComponent, IExposable` | Owns `SortedDictionary<int, FactionRuntimeData>` (one per faction, keyed by `loadID`) — power/wealth/stability, regroup-cooldown timers, active treaties, tribute schedule, alliance standing. Ticks each module's per-faction logic once/day (`GenDate.TicksPerDay`), not per-tick — no need for finer granularity and it keeps the hot path tiny. |
| `SettlementRuntimeComp` (attached via a `WorldObjectComp` on `Settlement`, `M1`) | Per-settlement population/wealth modifier, "regrouping" flag + expiry tick, origin tag (expansion/conquest/founded), parent-faction link for expansions. |
| `NegotiationSession : IHasSessionData` (`M6`) | Mirrors Multiplayer's own session pattern (`SessionManager`/`IHasSessionData`, e.g. `MpTradeSession`, `PauseLockSession`): an in-progress summit is a session object registered with our own lightweight `SessionManager` (or literally reusing Multiplayer's `Multiplayer.WorldComp.sessionManager` when MP is active, since it already exists and already solves "one player has a modal negotiation dialog open, don't let two clients act on it simultaneously"). |
| `TreatyRecord : IExposable` (`M6`) | Faction pair, terms (no-pollution / no-drug-trade / etc. as a `List<TreatyTermDef>`), start tick, breach conditions, penalty tier already applied. |
| `AllianceRecord : IExposable` (`M7`) | Faction, standing score, pending "help me" requests with expiry, double-cross risk accumulator. |

**Map-level:**

| Type | Purpose |
|---|---|
| `FactionDynamicsMapComp : MapComponent` | Tracks active raid-motivation state for lords currently raiding this map (target priority list, retreat threshold, "goal met" flag) and active Trial-of-Champions arenas. |
| `CompOutpostLogistics : ThingComp` (`M9`/`M10`) | Attached to a designated outpost's HQ building; holds the caravan schedule, last-run tick, and resource thresholds. Deterministic tick via `CompTick` → `GenTicks.TicksGame % interval == 0`, not `Rand.Chance` per tick. |

**Key Defs (new):**

- `RaidMotivationDef` (M3) — abstract-ish `Def` with a `Type raidAIWorkerClass` and tunables (retreat threshold %, priority `ThingRequestGroup`, incompatible-with tags). Ships with `FD_Starvation`, `FD_ResourceTheft`, `FD_Revenge`, `FD_AntiPollution` (v1.x), `FD_IdeologicalPunishment` (v1.x).
- `TreatyTermDef` (M6) — a negotiable condition (`FD_StopPollution`, `FD_StopDrugTrade`, `FD_TributeAmount`) with a `Type evaluatorClass` that checks breach conditions against real game state (e.g., `FD_StopPollution`'s evaluator checks for `ThingDefOf.WastePack` stockpiles/dumping within the settlement's claimed radius using the same "polluted terrain" checks Biotech already exposes).
- `QuestScriptDef` additions (M4/M5) — `FD_RaidAlliedSettlement`, `FD_DefendAlliedSettlement`, `FD_EmpireBounty`, `FD_DiplomaticSummit`, built as data using the vanilla `QuestNode` graph system (`QuestNode_Root`, `QuestPart` subclasses), *not* hand-rolled C# state machines — this is the same approach Royalty/Ideology/Biotech use for their own quests, it's server/host-authoritative by construction (the host resolves the quest graph, results replicate as data), and it's the correct MP-safety boundary: don't reinvent quest sync, ride the one vanilla already guarantees.
- `IncidentDef` additions (M1/M2) — `FD_SettlementFounded`, `FD_SettlementCollapsed`, `FD_SettlementExpanded`, layered on the existing world `IncidentWorker` pipeline so they integrate with the storyteller's existing pacing/threat budget instead of running on an out-of-band timer that could fight the storyteller.
- `HistoryEventDef` additions — for treaty signed/broken, tribute paid/refused, ally betrayed — feeding vanilla's existing memory/opinion systems (`Thought_MemorySocial`, faction goodwill history) rather than a bespoke reputation number, so other mods reading faction opinion via vanilla APIs keep working.

### 2.4 Harmony Patch Inventory

| Target | Patch type | Module | Why |
|---|---|---|---|
| `IncidentWorker_RaidEnemy.TryExecuteWorker` (or a `IncidentWorker_RaidEnemy` subclass registered instead of patched, preferred) | Prefer **subclass + `IncidentDef.workerClass` swap** over patching | M2/M3 | Raid arrival/size/motivation selection is intercepted at the point where vanilla already builds a `PawnGroupMakerParams`. Subclassing avoids fighting other mods that patch the same method (lower conflict risk than a transpiler here — see §5). |
| `Faction.TryAffectGoodwillWith` | Postfix | M6/M7 | Feed goodwill swings into `HistoryEventDef` records for treaty/alliance tracking without altering vanilla's own math. |
| `SettlementDefeatUtility.CheckDefeated` | Postfix (read-only) | M1 | Detect settlement collapse triggers already computed by vanilla to log our `FD_SettlementCollapsed` incident — mirrors the read-only, non-mutating style of `rwmt.Multiplayer`'s own `CheckDefeatedPatch`. |
| `WorldObjectsHolder.Add`/`Remove` | Postfix (read-only) | M1 | Keep `SettlementRuntimeComp` bookkeeping in sync with settlement creation/removal regardless of *which* system created it (ours, vanilla, or another mod's), so we don't miss settlements spawned by other content. |
| `Lord`/`LordJob_AssaultColony` toil selection | New `LordJob` subclass (`LordJob_AssaultColony_Motivated`) assigned via our raid worker, not a patch | M3 | Motivated behavior (retreat-with-loot, target waste packs) is cleanest as a proper `LordJob`/`LordToil` implementation, same architecture vanilla uses for its own raid variety — patching `LordJob_AssaultColony` internals is high-conflict-risk with Combat Extended and other combat-AI mods (§5), so we add a sibling class instead of altering the vanilla one. |
| `Settlement.GetGizmos` | Postfix | M6/M7/M11 | Add "Propose Summit," "Request Aid," "Challenge to Trial of Champions" gizmos — same technique your own `NewLanding/Source/Patches/Patch_Settlement_GetGizmos.cs` already uses. |
| `MainTabWindow_Quests`/world alert pipeline | Postfix, minor | M4/M5/M8 | Reuse `Alert`-derived classes for "tribute due," "ally under attack" instead of new UI chrome — this is the "prefer existing UI elements" requirement made concrete. |
| `CaravanArrivalAction`/`Caravan` tick | Postfix + new `WorldObjectComp` | M10 | Automated caravan movement rides the existing caravan pathing/arrival-action system rather than teleporting goods, so it's visible, interceptable (raids can still hit it), and inherently the same code path Multiplayer already keeps deterministic for player-driven caravans. |

Everything in this table is additive (postfix / new sibling classes / `workerClass` swaps via XML) rather than replacing vanilla logic wholesale. Prefixes that return `false` (full override) are avoided except where unavoidable, because those are exactly the patch shape that caused the Smart Speed × Multiplayer collision documented in your own `smart-speed-multiplayer-compat-fix.md` — two mods both fully overriding the same method is the highest-risk conflict pattern that exists, and it's avoidable here by design.

---

## 3. Suggested Project Structure

Matches your existing `NewLanding` layout, which already has the right shape for a modular, MP-aware mod:

```
FactionDynamics/
├── About/
│   └── About.xml                  # packageId matt.factiondynamics, loadAfter brrainz.harmony + rwmt.Multiplayer
├── Assemblies/
│   └── FactionDynamics.dll
├── Defs/
│   ├── RaidMotivationDefs/
│   ├── TreatyTermDefs/
│   ├── IncidentDefs/
│   ├── HistoryEventDefs/
│   └── QuestScriptDefs/
├── Languages/
│   └── English/Keyed/FactionDynamics.xml
├── Source/
│   ├── FactionDynamics.csproj      # same net472 / RimWorldManaged HintPath pattern as NewLanding.csproj
│   ├── FactionDynamicsMod.cs        # Mod + Settings window entry point
│   ├── FactionDynamicsSettings.cs   # ModSettings, per-module toggles + tuning
│   ├── Startup.cs                   # StaticConstructorOnStartup: module gating, Harmony patch application
│   ├── Compat/
│   │   └── MultiplayerCompat.cs     # same detect-then-wire pattern as NewLanding's
│   ├── Core/
│   │   ├── FactionDynamicsWorldComp.cs
│   │   ├── FactionRuntimeData.cs
│   │   └── DeterministicRand.cs     # small helper: seeded Rand.PushState scopes, shared across modules
│   ├── Settlements/                 # M1
│   │   ├── SettlementRuntimeComp.cs
│   │   ├── SettlementLifecycleWorker.cs
│   │   └── IncidentWorker_SettlementFounded.cs (+ Collapsed, Expanded)
│   ├── Raids/                       # M2 + M3
│   │   ├── IncidentWorker_RaidEnemy_Geographic.cs
│   │   ├── RaidMotivationDef.cs
│   │   ├── RaidMotivationWorker.cs (+ per-motivation subclasses)
│   │   └── LordJob_AssaultColony_Motivated.cs
│   ├── Quests/                      # M4 + M5
│   │   └── (QuestNode helper classes only — content lives in Defs/QuestScriptDefs XML)
│   ├── Diplomacy/                   # M6
│   │   ├── NegotiationSession.cs
│   │   ├── TreatyRecord.cs
│   │   ├── TreatyTermDef.cs
│   │   └── Dialog_DiplomaticSummit.cs
│   ├── Warfare/                     # M7 + M8
│   │   ├── AllianceRecord.cs
│   │   ├── CoordinatedRaidWorker.cs
│   │   └── TributeSchedule.cs
│   ├── Outposts/                    # M9 + M10
│   │   ├── CompOutpostLogistics.cs
│   │   └── CaravanAutomationWorker.cs
│   ├── Conflict/                    # M11
│   │   └── TrialOfChampionsWorker.cs
│   ├── Patches/                     # thin Harmony patch classes, one file per target per §2.4
│   └── UI/
│       └── (gizmos, alerts, dialogs — reusing vanilla Widgets/Texs throughout)
└── Textures/
    └── UI/                          # only if a term/treaty icon genuinely has no vanilla equivalent
```

Each module folder (`Settlements/`, `Raids/`, `Diplomacy/`, `Warfare/`, `Outposts/`, `Conflict/`) is a physical boundary that matches the settings-flag boundary from §2.1 — makes it trivial to verify "does disabling M6 actually stop all Diplomacy/ code from running" during testing.

**Key class responsibilities (summary):**

- `FactionDynamicsSettings` — one bool + tuning block per module; synced via Multiplayer's `SyncConfigs` when present.
- `FactionDynamicsWorldComp` — the single source of truth for per-faction runtime state; every module reads/writes through it rather than keeping parallel state.
- `SettlementLifecycleWorker` — daily-tick evaluation of spawn/collapse/expand chances per faction, seeded deterministically per faction+tile+date.
- `RaidMotivationWorker` (+ subclasses) — chosen at raid-generation time by `IncidentWorker_RaidEnemy_Geographic`, drives both `PawnGroupMakerParams` (composition) and the assigned `LordJob`.
- `NegotiationSession` — modal summit UI state, session-synced.
- `CoordinatedRaidWorker` — schedules allied-raid participation and evaluates double-cross rolls (deterministically seeded) at raid-launch time only, never mid-raid on a UI thread.

---

## 4. Balancing & Configuration

Mod settings window, organized as one collapsible section per module (`Widgets.CheckboxLabeled` per module toggle, revealing that module's tuning below it — same UX pattern as most Vanilla Expanded mods' settings screens):

**Global**
- Master enable per module (11 checkboxes, §1.3).
- Difficulty presets: *Vanilla-Friendly*, *Balanced (default)*, *Chaotic World* — each just sets sane defaults for every slider below; players can still hand-tune after picking one.

**M1 Settlements**
- Settlement spawn/collapse check frequency (days).
- Collapse/expansion weight sliders per cause (economic collapse, conquest, abandonment, player action).
- Regroup duration range (days) and regroup strength penalty (% population/wealth) after a settlement raids the player.
- Min/max settlement count per faction (floor so a faction can't be wiped to zero and softlock its own quests).

**M2 Raid Geography**
- Distance-frequency curve (a `SimpleCurve` editable via the existing vanilla curve-editor widget style) — distance in tiles → frequency multiplier.
- Distance → size multiplier and arrival-time multiplier curves.
- Cap: max frequency multiplier, so a faction 2 tiles away doesn't become absurd.

**M3 Raid Motivations**
- Per-motivation enable + weight.
- Retreat threshold (% losses or % goal achieved) per motivation, with sane defaults.

**M4/M5 Quests**
- Frequency multiplier layered on top of vanilla's own quest pacing (never a hard schedule that could stack with vanilla's).
- Bounty reward scale.

**M6 Diplomacy**
- Which `TreatyTermDef`s are enabled.
- Breach penalty severity (goodwill hit, embargo duration, retaliation raid weight) — three tiers, tunable.

**M7/M8 Warfare & Tribute**
- Double-cross base chance + modifiers (goodwill, treaty status).
- "Failed to help ally" penalty severity.
- Tribute demand frequency/amount scaling with colony wealth (reusing vanilla's existing wealth-for-raid-scaling math, `StorytellerUtility.DefaultThreatPointsNow`-adjacent, so tribute demands feel proportionate the same way raids already do).

**M9/M10 Outposts & Logistics**
- Max outpost distance from home colony before sustainability penalties kick in.
- Caravan automation interval, risk-of-interception toggle (should automated caravans be raidable off-screen, abstracted, like vanilla trade caravans already are?).

**M11 Trial of Champions**
- Enable/disable per faction relation state (only hostile, or also to preempt a treaty breach).
- Stakes options (loot only / territory / prisoner release).

All settings are read into a cached snapshot once (not per-frame/per-tick), and — per §2.1 — synced host→client when Multiplayer is active.

---

## 5. Potential Conflicts With Other Mods & Mitigation

| Mod (reference) | Overlap | Risk | Mitigation |
|---|---|---|---|
| **Dynamic Factions** ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3656715444)) | Direct: faction lifecycle, settlements spawning/collapsing, faction-vs-faction conflict | High if both installed — duplicate settlement lifecycle logic could double-collapse settlements, and its own listing admits no multiplayer support and "settlements die out excessively rapidly" balance issues, which is exactly the failure mode we're designing M1 to avoid | Detect via package ID at startup; if present, log a clear incompatibility warning and offer to auto-disable M1 (settlement lifecycle) while leaving M2–M11 active, since those don't touch settlement spawn/despawn directly. Document the overlap in the mod description up front. |
| **More Faction Interaction (Continued)** ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=2379076640)) | Partial: faction-to-faction interactions/events | Medium — likely both hook faction relation changes | Route our goodwill changes through vanilla's own `Faction.TryAffectGoodwillWith` (postfix-only, §2.4) rather than a custom relation field, so both mods' effects stack additively instead of one clobbering the other. |
| **RimWar** ([mod page](https://top-mods.com/mods/rimworld/gameplay/5288-rim-war.html)) | Direct: allied warfare, faction-vs-faction battles resolved off-map | High for M7 specifically | RimWar resolves entire wars abstractly off-map; our M7 is narrower (player-adjacent coordinated raids and double-crosses, not full off-map war simulation). Detect RimWar and, if present, disable only the parts of M7 that would double-trigger faction-vs-faction combat resolution, while keeping player-facing "call for allied help" intact. |
| **Vanilla Factions Expanded series** (Settlers, Empire, Pirates, Deserters, etc.) | Partial: new factions, settlement-adjacent content, Empire honor/bounty-adjacent systems | Low-Medium — mostly additive content, but M5 (Empire bounties) should not fight the base Empire/Royalty bounty-quest content | Build `FD_EmpireBounty` as an *additional* `QuestScriptDef`, not a patch to vanilla's existing Empire bounty quests — same "ride the vanilla quest graph" approach from §2.3 means it composes rather than conflicts. |
| **Hospitality** | Thematic: NPC diplomacy/visitors | Low — different mechanic (guest management vs. our summit dialogs), but both add world-map "visit" style content | No code overlap expected; call out in description that Hospitality guests and Diplomacy Summits are complementary, not competing. |
| **Combat Extended** | Structural: heavily patches raid AI, pawn combat behavior, `LordJob`/verb systems | High if M3's `LordJob_AssaultColony_Motivated` and CE's combat patches both touch raid pathing/targeting decisions | This is exactly why §2.4 specifies a *new sibling `LordJob`* rather than patching `LordJob_AssaultColony` — CE's own patches target vanilla lord jobs; a new class is invisible to CE's patch targets unless CE explicitly walks all `LordJob` subclasses (rare). Flag as "not explicitly tested with Combat Extended" in v1.0 and treat as a known follow-up compatibility pass. |
| **Vanilla Outposts Expanded / outpost-style mods** | Direct: M9 outposts | Medium | If detected, default M9 off and point players to use the other mod's outposts instead, rather than shipping two competing outpost systems — outposts are infrastructure-heavy (new `WorldObject` types) and running two in parallel is a recipe for save-breaking conflicts. |
| **Any mod patching `IncidentWorker_RaidEnemy` with a full override prefix** | Structural | High, generic | This is the single biggest generic risk in the whole design. Mitigated by preferring `workerClass` subclassing over patching wherever the vanilla incident system supports it (§2.4) — a `workerClass` swap in XML only conflicts with another mod that *also* swaps the same `IncidentDef`'s `workerClass`, which is easy to detect at load time and warn about, versus a silent Harmony patch-order race. |
| **RimWorld Multiplayer itself** | N/A — dependency, not conflict | — | Entire §2.2 is written against the actual `rwmt.Multiplayer` source checked into this project (`Source/Client/...`), not assumptions. Re-verify against whatever MP version is current at implementation time, since MP's internals do shift between releases — the sync patterns (`SyncMethod`, `SessionManager`, `FactionExtensions.PushFaction`) are stable across recent versions per the [MultiplayerAPI wiki](https://github.com/rwmt/MultiplayerAPI/wiki/), but always confirm against the copy in `RimworldMP/` before release. |

General mitigation strategy used throughout: **detect, don't assume.** Every module that has a plausible conflict does a one-time `LoadedModManager` scan at startup and either (a) auto-disables the overlapping sub-feature with a clear log message and in-game mod-settings notice, or (b) routes through vanilla-shared systems (goodwill, quest graph, incident worker) so multiple mods' effects compose instead of colliding — the same philosophy already proven in your `AllowTool`, `SmartSpeed`, and `NewLanding` compat work.

---

## 6. Implementation Roadmap

Each phase ends with a working, MP-tested build. "MP test" means: host + 1 client, verify no desync over a 1-in-game-day session with the feature actively triggering, using the existing `MP Desync Logs` workflow you already have set up.

### Phase 0 — Scaffolding (pre-M1)
1. Create project structure per §3; `About.xml`, `.csproj` matching `NewLanding`'s offline-build pattern (RimWorldManaged HintPath, Harmony + MultiplayerAPI compile-time refs).
2. `FactionDynamicsMod`, `FactionDynamicsSettings` with the 11 module toggles wired up but every module body empty (no-op). Confirm settings persist and (with MP installed) sync host→client.
3. `Compat/MultiplayerCompat.cs` ported from `NewLanding`'s pattern. Confirm mod loads clean with MP absent and present.
4. `FactionDynamicsWorldComp` skeleton with `ExposeData`; confirm save/load round-trips an empty `SortedDictionary` correctly, in both SP and MP.

**Exit criteria:** mod loads in SP and MP with all modules toggled off, zero errors, zero desyncs, settings persist.

### Phase 1 — M1 Dynamic Settlements
1. `SettlementRuntimeComp`, `SettlementLifecycleWorker` (deterministic daily-tick evaluation).
2. `IncidentWorker_SettlementFounded/Collapsed/Expanded`, wired into the storyteller's incident pipeline.
3. Raid-weakens-origin-settlement hook: postfix on wherever the raid's origin settlement is resolved (likely `IncidentWorker_RaidEnemy`'s parms setup) to mark that `Settlement`'s `SettlementRuntimeComp` as regrouping.
4. Settings UI section for M1.

**Exit criteria:** settlements visibly spawn/collapse/expand over a multi-season test game; a settlement that raids the player measurably weakens and recovers; MP test passes (both clients see identical settlement state after a shared session, verified via `ExposeData` dump comparison, not just "no error message").

### Phase 2 — M2 + M3 Geography & Motivated Raids
1. `IncidentWorker_RaidEnemy_Geographic` (or `workerClass` swap) computing distance from nearest hostile settlement to colony tile, feeding frequency/size/arrival curves from §4.
2. `RaidMotivationDef` + starter motivations (Starvation, ResourceTheft, Revenge).
3. `LordJob_AssaultColony_Motivated` with retreat conditions per motivation.
4. Settings UI for M2/M3.

**Exit criteria:** raids from a near settlement are visibly more frequent than a far one over a controlled test; a Starvation raid demonstrably prioritizes food stockpiles and retreats once "fed"; MP test passes with both clients observing identical raid composition/targets (this is the highest desync-risk phase so far — budget extra MP testing time here).

### Phase 3 — M4 + M5 Quests
1. `QuestScriptDef`s for raid/defend-allied-settlement and Empire bounty, built as `QuestNode` graphs.
2. Bounty target selection (named pawn generation reusing vanilla's own named-pawn quest content patterns).
3. Settings UI.

**Exit criteria:** both quest types generate, complete, and reward correctly in SP and MP (quest system is inherently host-authoritative, so this phase should be lower-risk — confirm that assumption holds).

### Phase 4 — M6 Diplomacy & Summits
1. `TreatyTermDef` + starter terms (no-pollution, no-drug-trade, tribute).
2. `NegotiationSession` (session-synced modal dialog), `Dialog_DiplomaticSummit`.
3. `TreatyRecord`, breach detection per term's `evaluatorClass`, breach consequence application (goodwill, embargo, retaliation raid scheduling via M2's raid pipeline).
4. Settings UI.

**Exit criteria:** a full summit → treaty → later breach → consequence cycle completes correctly in a shared MP session with both clients agreeing on treaty state throughout (this is the second highest-risk phase — multi-step negotiated state over time is exactly the shape of bug that produces subtle, hard-to-repro desyncs).

### Phase 5 — M7 + M8 Alliances & Tribute
1. `AllianceRecord`, `CoordinatedRaidWorker` (allied-raid scheduling, deterministic double-cross rolls).
2. "Help your ally" request/consequence flow.
3. `TributeSchedule`, demand/payment/refusal flow.
4. Settings UI.

**Exit criteria:** coordinated raid triggers correctly, double-cross rate matches configured odds over repeated deterministic-seed tests, tribute cycle runs correctly in MP.

### Phase 6 — M9 + M10 Outposts & Logistics
1. `CompOutpostLogistics`, sustainability tuning for distant outposts.
2. `CaravanAutomationWorker` riding vanilla caravan pathing/arrival actions.
3. Settings UI.

**Exit criteria:** an automated trade caravan completes a full round trip unattended, interceptable by a raid like any other caravan, correct in MP.

### Phase 7 — M11 Trial of Champions
1. `TrialOfChampionsWorker`, arena setup (reusing existing map-generation/quest-site patterns rather than a bespoke arena system), stakes resolution.
2. Settings UI.

**Exit criteria:** a full Trial of Champions offer → accept → resolve → stakes-applied cycle works correctly in MP.

### Phase 8 — Polish & Compatibility Pass
1. Full mod-conflict detection pass per §5's table (implement the actual detect-and-warn/auto-disable logic for each listed mod, not just design it).
2. Balance pass using the difficulty-preset framework from §4.
3. Full README (matching the depth of your existing `NewLanding/README.md`) documenting every module, setting, and known compatibility caveat.
4. Extended MP soak test: multi-hour session with all modules enabled simultaneously, watching for desyncs that only emerge from module *interactions* (e.g., a settlement collapsing mid-negotiation, a tribute demand arriving during a Trial of Champions).

---

## Appendix: Reference Material Consulted

- Your existing `NewLanding`, `AllowTool`, and `SmartSpeed` compatibility work (this project's `claude/*-multiplayer-compat-fix.md` docs) — source of the detect-then-wire pattern, the `[SyncMethod]`-everything-mutating rule, and the "off-tick simulation query = desync" lesson.
- `rwmt.Multiplayer` source, synced into this project (`Source/Client/...`) — `EarlyInit.cs`, `MultiplayerStatic.cs`, `MultiplayerWorldComp.cs`, `FactionExtensions.cs`, `MultifactionPatches.cs` — source of the `SortedDictionary`-for-determinism pattern, `PushFaction`/`PopFaction`, `[SyncMethod]` usage on faction-relation changes, and the category-based patch-application style.
- [RimWorld MultiplayerAPI wiki](https://github.com/rwmt/MultiplayerAPI/wiki/) — `SyncMethod`/`SyncField`/`SyncWorker`/`ISynchronizable` reference.
- [Dynamic Factions](https://steamcommunity.com/sharedfiles/filedetails/?id=3656715444) — closest existing prior art for dynamic settlement lifecycle; its documented balance issues ("settlements die out excessively rapidly") and lack of multiplayer support directly informed M1's floor/ceiling settlement-count safeguard and the MP-safety-first design principle.
- [More Faction Interaction (Continued)](https://steamcommunity.com/sharedfiles/filedetails/?id=2379076640) and [RimWar](https://top-mods.com/mods/rimworld/gameplay/5288-rim-war.html) — prior art for faction interaction and allied warfare, informing the conflict-mitigation table in §5.

Note: mod names, authors, and exact current feature sets on the Workshop can drift over time — reconfirm the §5 conflict table against the live Workshop listings at implementation time rather than treating this document as a permanent snapshot.
