# Faction Dynamics — Diplomacy and War

RimWorld **1.6** · Harmony · built for **RimWorld Multiplayer** (`rwmt.Multiplayer`) from the ground up
Package ID `matt.factiondynamics` · no hard dependencies beyond Harmony

This is the **v1.0 core** of the design in `../FactionDynamics/DESIGN.md` (design doc lives one folder up as
`DESIGN.md`): modules M1–M5. Diplomacy summits, alliances, tribute, outposts, logistics and trials of
champions are v1.1+ and are not in this build.

---

## What it does

### M1 — Dynamic settlements
NPC faction settlements are no longer permanent scenery.

- Every faction gets one lifecycle evaluation every N days (default 4). It can **found** a settlement,
  **expand** into one next to its existing territory, **lose** one (collapse or abandonment), or simply
  **consolidate** (an existing settlement gets stronger).
- Losing settlements raises a faction's **hardship**, which makes further losses more likely and unlocks
  starvation raids (M3). Hardship decays over seasons.
- A settlement that **sends a raid at you is weakened and regroups** for 6–15 days: reduced effective
  strength, and it is skipped entirely when picking where the next raid comes from. The world-map inspect
  pane shows "Regrouping after a raid (N days)", plus "Thriving" / "Struggling" for settlements that have
  drifted from baseline strength.
- Guard rails, in priority order: never touch a settlement that has a map, has a player caravan on or
  heading to it, or that an active quest points at; never take a faction below the configured minimum
  settlement count; never grow one past its starting count × the configured factor; never touch player,
  hidden, temporary, defeated or non-settling factions (mechanoids, insects).

### M2 — Geography-aware raids
Vanilla picks a raiding faction essentially at random and has **no concept of where a raid came from**
(verified: `IncidentParms` carries no origin settlement or world tile — `spawnCenter` is a map cell).
This module supplies the missing geography.

- **Who raids you** is re-weighted by proximity: a faction with a settlement 20 tiles away is far more
  likely to be the raider than one on the other side of the planet. Strength of the effect is a slider
  (0 = vanilla behaviour).
- **How big** the raid is scales with distance (default ×1.25 for close neighbours, ×0.75 for distant ones).
- **How they arrive** scales with distance: neighbours walk in over the hill, distant factions are far more
  likely to arrive by drop pod — because walking would have taken a season. The mod only picks an arrival
  mode the chosen raid strategy actually allows, so a raid never fails to fire because of this.
- The raid letter now tells you **which settlement it came from and how far away it is**.

### M3 — Motivated raids
Raiders arrive with a goal instead of "kill everything", and the goal changes their targets and their
retreat conditions. Three motivations ship in v1.0, as data (`Defs/RaidMotivationDefs/`), so more are an
XML file rather than a code change.

| Motivation | Wants | Behaviour |
|---|---|---|
| **Starving** (`FD_Starvation`) | Food (nutrition) | Fights briefly, then the whole group makes for your food stores. Leaves once they carry ~6 nutrition per raider, or after losing 25% of the group. Smaller raids (×0.85). Only offered when the faction has real hardship (lost settlements). |
| **Plundering** (`FD_Plunder`) | Valuables (market value) | Same shape, targeting anything worth ≥8 silver that isn't food. Leaves at ~120 silver of loot per raider or 35% losses. |
| **Revenge** (`FD_Revenge`) | To hurt you | No looting, kidnaps colonists, bigger raids (×1.15), fights until 75% losses, and also accepts "we've done enough damage" as a win. Only offered when the faction holds a real grudge — you destroyed their settlements or cleared their camps. |

Each motivation appends a sentence to the raid letter explaining why they came, and the reason is always
true — a motivation is only offered if the faction is actually in that state.

Under the hood: a custom `RaidStrategyDef` (never randomly selectable) whose worker builds a
`LordJob_FDMotivatedRaid` with a hand-built state graph — assault toil → loot toil → exit toil, with
transitions on goal-met, losses, timeout, and peace. The loot phase uses custom `DutyDef`s driving a
`JobGiver_FDSteal`, which is vanilla's steal search with a category filter (vanilla's
`StealAIUtility.TryFindBestItemToSteal` can only filter on the binary `ThingDef.stealable` flag — there is
no "only food" option anywhere in the vanilla pipeline).

### M4 — Settlement raid / defend quests
Two new quests, both built on the vanilla quest-graph system.

- **`FD_StrikeRivalOutpost`** — a faction leader asks you to burn out a rival's forward camp. Modelled on
  Core's `OpportunitySite_BanditCamp` (a generated `Site` you clear), because vanilla has no mechanism for
  assaulting a real persistent `Settlement` through a quest. **What this mod adds is consequence**: clearing
  the camp weakens that faction's nearest real settlement on the world map and feeds their hardship and
  their grudge, through a custom `QuestPart_FDWeakenFaction`.
- **`FD_DefendNeighbourSettlement`** — a neighbour's settlement is under siege from a hostile camp and they
  ask for help. Same machinery, opposite fiction, with diplomatic stakes on both ends: **+20 goodwill** if
  you break the siege, **−18 goodwill** if you let the deadline pass. Failing to help has a real cost.

### M5 — High-profile bounties
**`FD_HighProfileBounty`** — a leader of an established, non-hostile faction puts a price on a *specific
named pawn who already exists in your world* (`canGeneratePawn: false` — not a pawn invented for the quest).

Capture or kill both count, tracked with zero custom quest parts: the target is tagged with
`QuestNode_AddTag` and the engine already fires `bountyTarget.Killed` / `bountyTarget.Arrested` against
quest tags. **Taking them alive pays 35% more** than killing them.

---

## Multiplayer

Everything is deterministic-first rather than patched-for-MP afterwards.

**1. Simulation config lives in the save, not in mod settings.** This is the important one. Mod settings are
per player: two clients with different sliders would roll different outcomes from the same tick and desync
within minutes. So simulation code never reads `FactionDynamicsMod.Settings` — it reads `FDSimConfig`, which
is scribed into the world save and therefore identical for everyone. Singleplayer refreshes it from local
settings on every world load (so slider changes apply to existing saves); in an MP session that refresh is
skipped and the host's values stand. Cosmetic settings (letters, verbose logging) stay local, because they
can differ safely.

**2. Seeded RNG everywhere.** Every stochastic decision outside the game's own already-replayed code paths
goes through `FDRand.Push(...)`, seeded from stable IDs (faction `loadID`, settlement `ID`, day-of-game)
plus a per-system salt. No `UnityEngine.Random`, no wall-clock time.

**3. Deterministic iteration order.** All mod state lives in one world component and is enumerated through
sorted key lists, never raw `Dictionary` order. Faction lists are sorted by `loadID` before any weighted
pick.

**4. Tick-driven, never off-tick.** All simulation runs in `WorldComponentTick` / lord ticks, which
Multiplayer's async time components already replay identically. Nothing runs in `Update`/`FixedUpdate` —
that off-tick pattern is exactly what caused the Allow Tool desync.

**5. Patches are applied unconditionally; behaviour is gated at runtime.** Module toggles do not
conditionally apply Harmony patches, because two clients with different patched code desync. Instead every
patch body checks the saved config. Side benefit: module toggles need no restart in singleplayer.

**6. No hard dependency.** `MultiplayerCompat` detects MP purely from the loaded-mod list and confines all
`Multiplayer.API` IL to one non-inlined method, so the mod loads and runs fine with MP absent.

At world load the mod logs its config hash — if a desync ever does show up, comparing that line between
clients is the first triage step.

---

## Mod settings

Per module, all in one scrollable page. Tuning values apply live; module toggles apply to the current save
on next load (singleplayer) or come from the host (multiplayer).

- **Dynamic settlements**: check interval, found/collapse chance, min settlements per faction, max growth
  factor, regroup duration range, regroup strength penalty, letters on/off and their radius.
- **Geography-aware raids**: near/far distance thresholds, proximity bias (0 = vanilla), raid size
  multipliers for near and far, distance-based arrival on/off.
- **Raid motivations**: chance a raid has one, retreat-threshold multiplier, per-motivation toggles.
- **Quests**: frequency multipliers for the settlement quests and bounties, bounty reward scale.
- **Verbose logging** for balancing and bug reports.

Quest frequency mapping: the quest defs carry double the intended selection weight and `QuestNode_FDGate`
passes with chance (multiplier ÷ 2) — so 1.0 = intended rate, 0 = off, 2.0 = double. That indirection
exists so quest selection reads the saved config instead of a per-client def field.

---

## Project layout

```
FactionDynamics/
├── About/About.xml
├── Assemblies/FactionDynamics.dll        (built output)
├── Defs/
│   ├── DutyDefs/Duties_FDLoot.xml        FD_LootFood / FD_LootValuables
│   ├── RaidMotivationDefs/               FD_Starvation / FD_Plunder / FD_Revenge
│   ├── RaidStrategyDefs/                 FD_MotivatedAssault (weight 0, never auto-picked)
│   └── QuestScriptDefs/                  strike / defend / bounty
├── Languages/English/Keyed/
└── Source/
    ├── FactionDynamics.csproj            offline build against the game's own DLLs
    ├── FactionDynamicsMod.cs             settings window
    ├── FactionDynamicsSettings.cs        per-player settings
    ├── Startup.cs                        Harmony bootstrap
    ├── Compat/MultiplayerCompat.cs       detect-then-wire, no hard dependency
    ├── Core/                             world comp, saved sim config, per-faction and per-settlement
    │                                     state, seeded RNG, world-map helpers
    ├── Settlements/                      M1 lifecycle worker + actions
    ├── Raids/                            M2 geography, M3 motivations, lord job, toil, triggers, jobgiver
    ├── Quests/                           M4/M5 custom quest nodes and the weaken-faction quest part
    └── Patches/                          Harmony patches (raid pipeline, raid letter, settlement inspect)
```

### Two deliberate deviations from the design doc

1. **Per-settlement data lives in the world component, keyed by settlement ID — not in a `WorldObjectComp`.**
   One ordered structure we control is easier to keep deterministic, needs no XML patch on the `Settlement`
   def, and works for settlement types added by other mods without knowing about them.
2. **The raid pipeline is hooked with Harmony postfixes on `IncidentWorker_RaidEnemy`, not by swapping the
   `IncidentDef`'s `workerClass`.** Other raid mods swap that same `workerClass`, and whoever loads last
   would silently win, discarding the other mod's behaviour. Postfixes still run for any subclass that calls
   base, so this composes with those mods instead of fighting them.

---

## Building

```
cd Source
dotnet build -c Release
```

Paths at the top of `FactionDynamics.csproj` point at a standard Steam install and the Harmony workshop mod;
override on the command line if yours differ:

```
dotnet build -c Release -p:RimWorldManaged="...\RimWorldWin64_Data\Managed" -p:HarmonyDll="...\0Harmony.dll"
```

`0MultiplayerAPI.dll` is referenced compile-time-only (`Private=false`) from `..\..\RimworldMP\Assemblies\`,
matching the layout of your mods folder. Output goes straight to `Assemblies/FactionDynamics.dll`.

---

## Testing checklist

Nothing here has been run in-game yet — the code compiles clean and every def reference, XML field name and
API signature was verified against your actual 1.6 assembly and def files, but that is not the same as
playing it. Suggested order:

**Singleplayer smoke test**
1. Load with the mod enabled, check the log for `[Faction Dynamics] Patches applied.` and a config-hash line,
   and for any red def-load errors.
2. Turn on verbose logging, dev-mode fast-forward a season, and watch for settlement lifecycle lines. Confirm
   settlements appear/disappear on the world map and that letters arrive for nearby ones.
3. Dev-mode trigger `RaidEnemy` several times: check the letter names an origin settlement, that the origin
   shows "Regrouping" afterwards, and that the next raids prefer a *different* settlement.
4. Trigger raids until a motivated one fires (turn motivation chance to 1.0 to force it). Watch that
   starving raiders actually path to food, pick it up, and leave; that the "got what they came for" message
   fires; and that revenge raids don't loot.
5. Dev-mode force each of the three quests and play one through, including a deliberate failure of the
   defend quest to confirm the goodwill penalty lands.

**Multiplayer test** (host + one client, the workflow you already use for MP Desync Logs)
6. Both clients load the same save; confirm the config-hash line matches on both.
7. Run a shared in-game day with settlement lifecycle active and compare world-map settlement lists.
8. Trigger a motivated raid and let it play out to its retreat, then save/reload mid-raid on both ends —
   `LordJob` graphs are rebuilt from scribed fields on load, and that is the single most likely place for a
   desync to hide.

**Known risks worth watching**
- Quest XML is the least verifiable part without running the game; a mistyped node field shows up as a red
  error at def load, not as silent misbehaviour, so the log will say so immediately.
- `QuestNode_GetNearbySettlement` has no hostility filter, so the "defend a neighbour" quest can occasionally
  be offered on behalf of a faction you are not friendly with. Harmless, slightly odd flavour text.
- Motivated raiders on the loot duty fight back only within 20 tiles; if that reads as too passive in play,
  raise `targetAcquireRadius` in `Defs/DutyDefs/Duties_FDLoot.xml`.
