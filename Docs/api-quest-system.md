# RimWorld 1.6 Quest System API Cheat-Sheet

Scope: everything needed to build (A) a "raid or defend another faction's settlement" quest and
(B) a "bounty on a specific named world pawn" quest, on top of the vanilla `QuestScriptDef` /
`QuestNode` graph system. Every signature below is copy-pasted from `monop` output against
`~/refs/api/Assembly-CSharp.dll` (game version `1.6.9676.17735`), and every XML block is quoted
verbatim from the user's installed `RimWorld/Data/<DLC>/Defs/...` files. Nothing here is from memory.

---

## 0. TL;DR / most important findings

- **1.6 tile type**: world tiles are `RimWorld.Planet.PlanetTile` (a `struct`), not `int`. It has an
  implicit conversion to/from `int` for back-compat, plus a `.Layer` / `.LayerDef` (surface vs. orbit
  vs. other planet layers — new in 1.6/Odyssey). Any code taking a "tile" argument in 1.6 quest APIs
  uses `PlanetTile`, e.g. `QuestNode_Root_Mission.GenerateSite(Pawn asker, float threatPoints, int
  pawnCount, int population, PlanetTile tile)`.
- **How a quest gets offered**: there is **no** `IncidentDef` per quest in 1.6. `Storyteller/
  Incidents_World_Quests.xml` defines exactly one generic incident, `GiveQuest_Random`
  (`workerClass=IncidentWorker_GiveQuest`, **no** `questScriptDef` set), which the storyteller fires
  periodically on the World target. `IncidentWorker_GiveQuest.TryExecuteWorker` then calls into
  `RimWorld.NaturalRandomQuestChooser.ChooseNaturalRandomQuest(points, target)`, which picks a random
  `QuestScriptDef` weighted by `rootSelectionWeight` / gated by `rootMinPoints`, `rootMinProgressScore`,
  `rootEarliestDay`, `CanRun(...)`, etc. **=> To make a new quest occur naturally, you generally just
  add a `QuestScriptDef` with `randomlySelectable`/`rootSelectionWeight` set — you do not need a new
  `IncidentDef`.** A dedicated `IncidentDef` (like `GiveQuest_EndGame_ShipEscape`, which pins
  `questScriptDef` and sets `baseChance=0`, "given by a special storyteller comp") is only needed if
  you want to force/trigger the quest from your own C# code instead of natural random selection.
- **QuestNode_\* vs QuestPart_\***: `QuestNode_*` (301 types) are what you can write in XML under
  `<root>` — pure logic/graph-building blocks, run once at quest-generation time. `QuestPart_*` (200
  types) are the **runtime, saved** objects that live on the `Quest` after generation and react to
  signals every tick (`Quest.AddPart`/`Quest.PartsListForReading`). Most `QuestPart_*` classes have a
  corresponding `QuestNode_*` wrapper that XML can instantiate directly (e.g. `QuestNode_PawnsKilled` →
  `QuestPart_PawnsKilled`, `QuestNode_RandomRaid` → `QuestPart_RandomRaid`, `QuestNode_AssaultColony` →
  `QuestPart_AssaultColony`). **But a meaningful subset of `QuestPart_*` types have NO XML-facing
  `QuestNode_*` wrapper at all** — most importantly `QuestPart_IsDead`, `QuestPart_IsPrisoner`,
  `QuestPart_DefendPoint`, and most of the `QuestPart_Filter_*` conditionals (only 3 of ~30
  `QuestPart_Filter_*` subclasses have a `QuestNode_Filter_*` wrapper). Those can only be attached to a
  quest by writing a small custom `QuestNode` subclass whose `RunInt()` does
  `QuestGen.quest.AddPart(new QuestPart_IsDead { pawn = x, ... })`.
- **Tracking a specific existing pawn's death/capture is mostly free, in pure XML**, via a different,
  more general mechanism: `Verse.Thing.questTags` (`List<string>`). `QuestNode_AddTag` tags a
  thing/pawn with a quest-scoped tag; game code elsewhere (kill code, arrest code, faction-change code,
  etc.) already calls `RimWorld.QuestUtility.SendQuestTargetSignals(thing.questTags, "<SignalPart>",
  ...)` automatically whenever that thing is killed/arrested/destroyed/etc. You just listen for
  `"<tag>.Killed"` / `"<tag>.Arrested"` with `QuestNode_Signal` / `QuestNode_AnySignal`. This is exactly
  how `Script_OrbitalFugitive.xml` listens for `fugitive.Destroyed` without any custom C#. The full set
  of signal-part constants is in `QuestUtility` (§2) — `Killed`, `Arrested`, `Kidnapped`,
  `ChangedFactionToPlayer`, `Rescued`, `Destroyed`, `Despawned`, etc.
- **"Defend a settlement / site from waves of raiders" has no generic XML node**, but Core ships a
  reusable XML *subscript* (`Util_RaidDelayRepeatable` in `Scripts_Utility_ThreatsCore.xml`) that does
  exactly "N raids, fixed/variable delay between them, done via `QuestNode_LoopCount` +
  `QuestNode_Delay` + `QuestNode_Raid`" — fully composable from XML, no custom C# required. Vanilla's
  own "go defend a location quest" (`Script_Site.xml` → `SurveySite`, described in-game as "Defending
  the Scanner") instead uses one purpose-built custom node, `QuestNode_Root_SurveyScanner`
  (`raidChance`, `raidAttackRemainingHoursRange`), because it also needs bespoke win/lose site-part
  logic — that's the model to copy for a from-scratch "defend an allied settlement" root node.
- There is **no vanilla "bounty on a named pawn" quest**, and no `QuestPart_Bounty*`/`IncidentWorker`
  named "Bounty" anywhere in 1.6. The closest analog is Odyssey's `OrbitalFugitive`
  (`QuestScriptDef` `OrbitalFugitive`, custom node `QuestNode_Root_OrbitalFugitive`), but that node
  *generates* the fugitive pawn and their site itself — it does not pick an existing named world pawn.
  For "capture or kill an existing named pawn," compose from `QuestNode_GetPawn` (with
  `mustBeWorldPawn`/`mustBeFactionLeader`/`mustBeOfKind` filters) + `QuestNode_AddTag` +
  `QuestNode_AnySignal` as described above — no vanilla precedent to copy verbatim, but no custom
  QuestPart needed either.
- There is also **no vanilla "attack or defend an existing `Settlement` (a real faction base)"
  quest** — vanilla "raid a settlement" quests (`OpportunitySite_BanditCamp`, `Mission_BanditCamp`)
  all generate a **temporary `Site`** (via `QuestNode_GenerateSite`/`QuestNode_Root_Mission`) dressed up
  as a "bandit camp," not an actual persistent `RimWorld.Planet.Settlement`. Attacking a real
  `Settlement` in vanilla happens only through normal caravan-vs-settlement combat, outside the quest
  system. `QuestNode_GetNearbySettlement` *does* let you fetch a real, existing `Settlement` (with its
  faction/leader) as the quest's *subject/asker*, which is the right building block if you want the
  quest to be *about* a real faction's base (e.g., for flavor/reward text, or as the defend target),
  but the actual fight will still play out on a `Site`/generated map, or — for "defend it in place" —
  you'd generate a map on the settlement itself and run raids into it, which is uncharted-by-vanilla
  territory (custom node required).

---

## 1. Vanilla QuestScriptDefs closest to our two quest types

### Full inventory of `QuestScriptDefs/` folders (by DLC)

```
Core/Defs/QuestScriptDefs/
  Script_BanditCamp.xml, Script_DelayedRewardDropPods.xml, Script_DownedRefugee.xml,
  Script_EndGame_ShipEscape.xml, Script_ItemStash.xml, Script_LongRangeMineralScannerLump.xml,
  Script_PeaceTalks.xml, Script_PrisonerWillingToJoin.xml, Script_TradeRequest.xml,
  Script_TransportPodCrash.xml, Script_WandererJoins.xml, Scripts_JoinerThreatCore.xml,
  Scripts_Utility_RewardsCore.xml, Scripts_Utility_ThreatsCore.xml

Royalty/Defs/QuestScriptDefs/
  Script_Bestower.xml, Script_ChangeRoyalHeir.xml, Script_Hospitality_Refugee.xml,
  Script_PawnLend.xml, Script_ShuttleCrash_Rescue.xml, Script_WandererJoins.xml,
  Scripts_Missions.xml, Scripts_Permits.xml, Scripts_ProblemCausers.xml,
  Scripts_Utility_TransportShip.xml,
  + subfolders: BuildMonument/, Decree/, Hospitality/, Intro/, RewardThreat/ (incl.
    Scripts_RewardRaid.xml), Utility/

Ideology/Defs/QuestScriptDefs/
  Script_AncientSignalActivation.xml, Script_Beggars.xml, Script_EndGame_ArchonexusVictory.xml,
  Script_Hack_AncientComplex.xml, Script_Hack_Spacedrone.xml, Script_Loot_AncientComplex.xml,
  Script_Missions.xml, Script_RelicHunt.xml, Script_ReliquaryPilgrims.xml, Script_WorkSite.xml,
  Script_WorshipppedTerminal.xml

Biotech/Defs/QuestScriptDefs/
  Script_Bossgroup.xml, Script_Loot_AncientComplex.xml, Script_MechanitorShip.xml,
  Script_PollutionDump.xml, Script_PollutionRaid.xml, Script_PollutionRetaliation.xml,
  Script_SanguophageMeetingHost.xml, Script_SanguophageShip.xml, Script_StartingMech.xml,
  Script_TransportPodCrash_Baby.xml

Anomaly/Defs/QuestScriptDefs/
  Script_CreepjoinerJoins.xml, Script_DistressCall.xml, Script_EndGame_VoidAwakening.xml,
  Script_MonolithMigration.xml, Script_MysteriousCargo.xml, Script_SightstealerArrival.xml,
  Script_TransportPodCrash_Ghoul.xml, Script_UnnaturalDarkness.xml

Odyssey/Defs/QuestScriptDefs/
  Script_AlphaThrumboSighting.xml, Script_AncientComplex.xml, Script_AncientMercenaries.xml,
  Script_AncientStructures.xml, Script_BanditCamp.xml, Script_GravShip.xml, Script_ItemStash.xml,
  Script_OrbitalFugitive.xml, Script_Site.xml, Script_SpaceSites.xml
```

### For quest type 1 ("raid or defend another faction's settlement")

| defName | File | Mechanism | Why it matters |
|---|---|---|---|
| `OpportunitySite_BanditCamp` | `Core/.../Script_BanditCamp.xml` | Pure XML graph (`QuestNode_Sequence` of ~15 generic nodes) that generates a temporary `Site` guarded by a hostile faction and ends on `site.AllEnemiesDefeated` / fails on `site.Destroyed` or timeout. | **Best fully-XML template for "raid a hostile site."** No custom C# at all. |
| `Mission_BanditCamp` | `Royalty/.../Scripts_Missions.xml` | `<root Class="QuestNode_Root_Mission_BanditCamp">`, a **custom C# node** (subclass of abstract `QuestNode_Root_Mission`) — shuttles your colonists to/from the site, has a strict time limit, faction of the *asker* is drawn from a list, faction of the *site* from another list. | Shows the "asker draws leader from a faction list, custom node builds the whole quest" pattern, and the exact abstract-base contract (`GenerateSite`, `GetAsker`, `GetRequiredPawnCount`, `QuestTag`) you'd override for a bespoke raid/defend root node. |
| `SurveySite` | `Odyssey/.../Script_Site.xml` | Mix of generic nodes (`QuestNode_Root_Site`, `QuestNode_WorldObjectTimeout`) **plus one custom node**, `QuestNode_Root_SurveyScanner`, which owns "player must protect the site for a duration; `raidChance` chance of an attack arriving in `raidAttackRemainingHoursRange`." Its questNameRules literally include "Defending the Scanner" / "Remote Protection." | **Best template for the "defend" half** — travel to a site, generate/keep a map there, survive incoming raid(s) for a duration, win when `site.SurveyCompleted` fires. |
| `ThreatReward_Raid_MiscReward` | `Royalty/.../RewardThreat/Scripts_RewardRaid.xml` | Pure XML: generates 1–3 raids against the player's own home map with `Util_RaidDelayRepeatable`, ends on `AllRaidsSent` AND `raid<N-1>/lord.AllEnemiesDefeated`. | Best template for **"waves of raiders arrive over time and you must survive them," entirely in XML** (no custom node) — directly reusable for a "defend your own colony as a reward-quest" variant, and the delay/looping pattern to copy for "defend an allied settlement." |
| `ProblemCauser` | `Royalty/.../Scripts_ProblemCausers.xml` | Pure XML: spawns a pirate outpost or mech cluster near the player's map that "will remain until you send a team there to attack and destroy it," ends on `conditionCauser.Destroyed`. | Second fully-XML "go destroy this hostile outpost" template, faction chosen at random between Pirate/Mechanoid. |

### For quest type 2 ("bounty on a specific named pawn")

| defName | File | Mechanism | Why it matters |
|---|---|---|---|
| `OrbitalFugitive` | `Odyssey/.../Script_OrbitalFugitive.xml` | Custom node `QuestNode_Root_OrbitalFugitive` generates the fugitive pawn + a `Site` (`OrbitalFugitivePlatform`), player must travel there and kill them; quest ends on `fugitive.Destroyed`. Description text literally uses "A Bounty for [fugitive_nameDef]". | **Closest vanilla analog and best template for the "hunt/kill a named pawn at a location" shape** — but note it *generates* the pawn rather than picking an existing world pawn, and it's "kill only," no capture-alternative. |
| — (no def) | — | No vanilla `QuestScriptDef` targets a **pre-existing** named world pawn (faction leader, notable, etc.) for a bounty. | Confirms this quest type needs original design; see §5. |

### Full XML — best templates, verbatim

#### `Core/Defs/QuestScriptDefs/Script_BanditCamp.xml` (`OpportunitySite_BanditCamp`)

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Defs>

  <QuestScriptDef>
    <defName>OpportunitySite_BanditCamp</defName>
    <rootSelectionWeight>1.0</rootSelectionWeight>
    <rootMinPoints>350</rootMinPoints>
    <canGiveRoyalFavor>true</canGiveRoyalFavor>
    <expireDaysRange>4~8</expireDaysRange>
    <successHistoryEvent MayRequire="Ludeon.RimWorld.Ideology">Raided_BanditCamp</successHistoryEvent>
    <everAcceptableInSpace>true</everAcceptableInSpace>
    <questNameRules>
      <rulesStrings>
        <li>questName->The [bandit] [camp]</li>
        <li>questName->[bandit] [camp]</li>
        <li>questName->[asker_nameDef] and the [camp]</li>
        <li>camp->Camp</li>
        <li>camp->Outpost</li>
        <li>camp->Lair</li>
        <li>camp->Encampment</li>
        <li>bandit->Bandit</li>
        <li>bandit->Raider</li>
        <li>bandit->Outlaw</li>
        <li>bandit->Desperado</li>
        <li>bandit->Fugitive</li>
        <li>bandit->Marauder</li>
        <li>bandit->Robber</li>
        <li>bandit->Brigand</li>
      </rulesStrings>
    </questNameRules>
    <questDescriptionRules>
      <rulesStrings>
        <li>questDescription->[asker_nameFull], [asker_faction_leaderTitle] of [asker_faction_name], has sent us a message. Apparently, [siteFaction_pawnsPlural] based in a nearby camp have been raiding their caravans. The camp is controlled by [siteFaction_name].
\n[asker_nameDef] is asking us to destroy the camp, which means eliminating all enemies and turrets. [asker_label] says that [sitePart0_description].</li>
      </rulesStrings>
    </questDescriptionRules>
    <root Class="QuestNode_Sequence">
      <nodes>
        <li Class="QuestNode_SubScript">
          <def>Util_RandomizePointsChallengeRating</def>
          <parms>
            <pointsFactorTwoStar>1.5</pointsFactorTwoStar>
            <pointsFactorThreeStar>2</pointsFactorThreeStar>
          </parms>
        </li>

        <li Class="QuestNode_SubScript">
          <def>Util_AdjustPointsForDistantFight</def>
        </li>

        <li Class="QuestNode_GetMap">
          <canBeSpace>true</canBeSpace>
        </li>

        <li Class="QuestNode_GetPawn">
          <storeAs>asker</storeAs>
          <mustBeFactionLeader>true</mustBeFactionLeader>
          <allowPermanentEnemyFaction>false</allowPermanentEnemyFaction>
          <hostileWeight>0.15</hostileWeight>
        </li>

        <li Class="QuestNode_GetSiteTile">
          <storeAs>siteTile</storeAs>
          <preferCloserTiles>true</preferCloserTiles>
          <selectLandmarkChance>0.5</selectLandmarkChance>
          <allowedLandmarks>
            <!-- ~20 Odyssey landmark <li> entries, MayRequire="Ludeon.RimWorld.Odyssey" -->
          </allowedLandmarks>
        </li>

        <li Class="QuestNode_GetSitePartDefsByTagsAndFaction">
          <storeAs>sitePartDefs</storeAs>
          <storeFactionAs>siteFaction</storeFactionAs>
          <sitePartsTags>
            <li><tag>BanditCamp</tag></li>
          </sitePartsTags>
          <mustBeHostileToFactionOf>$asker</mustBeHostileToFactionOf>
        </li>

        <li Class="QuestNode_GetDefaultSitePartsParams">
          <tile>$siteTile</tile>
          <faction>$siteFaction</faction>
          <sitePartDefs>$sitePartDefs</sitePartDefs>
          <storeSitePartsParamsAs>sitePartsParams</storeSitePartsParamsAs>
        </li>

        <li Class="QuestNode_GetSiteThreatPoints">
          <storeAs>sitePoints</storeAs>
          <sitePartsParams>$sitePartsParams</sitePartsParams>
        </li>
        <li Class="QuestNode_SubScript">
          <def>Util_GetDefaultRewardValueFromPoints</def>
          <parms>
            <!-- Use the actual threat points generated (some site parts define a minimum threshold) -->
            <points>$sitePoints</points>
          </parms>
        </li>

        <!-- Inflate reward value. Since we're basing the reward value on the threat points generated, we need to do this
             even though the threat points was deflated from the input points already. -->
        <li Class="QuestNode_Multiply">
            <value1>$rewardValue</value1>
            <value2>1.75</value2>
            <storeAs>rewardValue</storeAs>
        </li>

        <li Class="QuestNode_SubScript">
          <def>Util_GenerateSite</def>
        </li>

        <li Class="QuestNode_SpawnWorldObjects">
          <worldObjects>$site</worldObjects>
        </li>

        <li Class="QuestNode_WorldObjectTimeout">
          <worldObject>$site</worldObject>
          <isQuestTimeout>true</isQuestTimeout>
          <delayTicks>$(randInt(12,28)*60000)</delayTicks>
          <inSignalDisable>site.MapGenerated</inSignalDisable>
          <destroyOnCleanup>true</destroyOnCleanup>
          <node Class="QuestNode_Sequence">
            <nodes>
              <li Class="QuestNode_Letter">
                <label TKey="LetterLabelQuestExpired">Quest expired: [resolvedQuestName]</label>
                <text TKey="LetterTextQuestExpired">The bandit camp has packed up and moved on. The quest [resolvedQuestName] has expired.</text>
              </li>
              <li Class="QuestNode_End">
                <outcome>Fail</outcome>
              </li>
            </nodes>
          </node>
        </li>

        <!-- If we enter and leave, the map is destroyed. Fail the quest. -->
        <li Class="QuestNode_Signal">
          <inSignal>site.Destroyed</inSignal>
          <node Class="QuestNode_Sequence">
            <nodes>
              <li Class="QuestNode_Letter">
                <label TKey="LetterLabelQuestFailed">Quest failed: [resolvedQuestName]</label>
                <text TKey="LetterTextQuestFailed">After being discovered, the bandit camp has dispersed. The quest [resolvedQuestName] has ended.</text>
              </li>
              <li Class="QuestNode_End">
                <outcome>Fail</outcome>
              </li>
            </nodes>
          </node>
        </li>

        <li Class="QuestNode_Signal">
          <inSignal>site.AllEnemiesDefeated</inSignal>
          <node Class="QuestNode_Sequence">
            <nodes>
              <li Class="QuestNode_Notify_PlayerRaidedSomeone">
                <getRaidersFromMapParent>$site</getRaidersFromMapParent>
              </li>
              <li Class="QuestNode_GiveRewards">
                <parms>
                  <allowGoodwill>true</allowGoodwill>
                  <allowRoyalFavor>true</allowRoyalFavor>
                  <chosenPawnSignal>ChosenPawnForReward</chosenPawnSignal>
                </parms>
                <addCampLootReward>true</addCampLootReward>
                <customLetterLabel TKey="LetterLabelPaymentArrived">Payment arrived</customLetterLabel>
                <customLetterText TKey="LetterTextPaymentArrived">You have defeated the bandit camp!\n\nThe payment from [asker_faction_name] has arrived.</customLetterText>
                <nodeIfChosenPawnSignalUsed Class="QuestNode_Letter">
                  <letterDef>ChoosePawn</letterDef>
                  <label TKey="LetterLabelFavorReceiver">[asker_faction_royalFavorLabel]</label>
                  <text TKey="LetterTextFavorReceiver">These colonists participated in the victory for the quest [resolvedQuestName]. [asker_definite] wants to know who should receive the [royalFavorReward_amount] [asker_faction_royalFavorLabel] for this service.</text>
                  <useColonistsOnMap>$site</useColonistsOnMap>
                  <chosenPawnSignal>ChosenPawnForReward</chosenPawnSignal>
                </nodeIfChosenPawnSignalUsed>
              </li>
            </nodes>
          </node>
        </li>
        <li Class="QuestNode_End">
          <inSignal>site.AllEnemiesDefeated</inSignal>
          <outcome>Success</outcome>
        </li>
      </nodes>
    </root>
  </QuestScriptDef>
</Defs>
```
*(Landmark `<allowedLandmarks>` list elided — ~20 Odyssey `TileMutatorDef` names, all `MayRequire="Ludeon.RimWorld.Odyssey"`.)*

#### `Odyssey/Defs/QuestScriptDefs/Script_OrbitalFugitive.xml` (`OrbitalFugitive`) — full, unelided

```xml
<?xml version="1.0" encoding="utf-8"?>
<Defs>

  <QuestScriptDef>
    <defName>OrbitalFugitive</defName>
    <rootSelectionWeight>1</rootSelectionWeight>
    <expireDaysRange>4~8</expireDaysRange>
    <minRefireDays>30</minRefireDays>
    <canOccurOnAllPlanetLayers>true</canOccurOnAllPlanetLayers>
    <everAcceptableInSpace>true</everAcceptableInSpace>
    <questNameRules>
      <rulesStrings>
        <li>questName->The Hunt for [fugitive_nameDef]</li>
        <li>questName->A Bounty for [fugitive_nameDef]</li>
        <li>questName->[fugitive_nameDef] the [criminal]</li>
        <li>questName->The Orbital [criminal]</li>

        <li>criminal->Criminal</li>
        <li>criminal->Fugitive</li>
        <li>criminal->Outlaw</li>
        <li>criminal->Felon</li>
        <li>criminal->Renegade</li>
      </rulesStrings>
    </questNameRules>
    <questDescriptionRules>
      <rulesStrings>
        <!-- Asker is null -->
        <li>questDescription(askerIsNull==true)->An anonymous AI has requested your help dealing with an outlaw named [fugitive_nameDef]. The AI claims [fugitive_nameDef] is wanted for [reason_AI].\n\n[fugitive_nameDef] has stolen a transport pod and has fled to an abandoned orbital platform.\n\nHaving heard rumors of your gravship, the AI requests that you travel to the abandoned platform and kill [fugitive_nameDef]. In return, it will reward you with a gravtech device.\n\nThe AI warns you that [fugitive_nameDef] may be in the company of hostile orbital pirates.</li>

        <!-- Leader asker -->
        <li>questDescription(asker_factionLeader==true)->[asker_faction_leaderTitle] [asker_nameFull] of [asker_faction_name] has sent us a message. A fugitive named [fugitive_nameDef] escaped from [asker_possessive] custody and used a transport pod to flee to an abandoned orbital platform. [fugitive_nameDef] was in custody for [reason].\n\nHaving heard rumors of your gravship, the [asker_faction_leaderTitle] asks that you travel to the abandoned platform and kill [fugitive_nameDef]. If you do so, [asker_nameDef] will reward you with a gravtech device.\n\n[asker_nameDef] warns you that [fugitive_nameDef] may be in the company of hostile orbital pirates.</li>

        <!-- Royal asker -->
        <li>questDescription(asker_royalInCurrentFaction==true)->[asker_nameFull], a [asker_royalTitleInCurrentFaction] of [asker_faction_name], requests that you track down and kill a dangerous fugitive known as [fugitive_nameDef]. The fugitive is wanted for [reason_royal].\n\n[fugitive_nameDef] is hiding out on an abandoned orbital platform and may be guarded by orbital pirates. If you kill the fugitive, [asker_nameDef] will reward you with a gravtech device.</li>

        <li>reason_AI->the destruction of multiple high-level subpersona cores</li>
        <li>reason_AI->deorbiting an AI satellite</li>
        <li>reason_AI->crimes on numerous distant planets</li>
        <li>reason_AI->unlicensed orbital trafficking of psychic artifacts</li>
        <li>reason_AI->[reason]</li>

        <li>reason_royal->drunkenly insulting the [asker_royalTitleInCurrentFaction] in a wedding speech</li>
        <li>reason_royal->attempting to assassinate the [asker_royalTitleInCurrentFaction]</li>
        <li>reason_royal->accidentally detonating a firefoam pack in an imperial shuttle</li>
        <li>reason_royal->the improper use of a tornado generator near an imperial settlement</li>
        <li>reason_royal->trying to orchestrate a rebellion</li>
        <li>reason_royal->treason</li>
        <li>reason_royal->[reason]</li>

        <li>reason->collaborating with a sanguophage cabal</li>
        <li>reason->trafficking dangerous psychic artifacts</li>
        <li>reason->grand larceny and multiple counts of corporate sabotage</li>
        <li>reason->unauthorized genetic experimentation</li>
        <li>reason->inciting inter-factional conflict</li>
        <li>reason->drunkenly profaning a peace ritual</li>
        <li>reason->the destruction of an orbital trade vessel</li>
      </rulesStrings>
    </questDescriptionRules>
    <root Class="QuestNode_Sequence">
      <nodes>
        <li Class="QuestNode_RequirementsToAcceptResearch">
          <reserach>BasicGravtech</reserach>  <!-- sic: typo'd XML tag name in vanilla -->
        </li>

        <li Class="QuestNode_SubScript">
          <def>Util_GetDefaultRewardValueFromPoints</def>
        </li>

        <li Class="QuestNode_SubScript">
          <def>Util_AdjustPointsForDistantFight</def>
        </li>

        <li Class="QuestNode_GetMap">
          <canBeSpace>true</canBeSpace>
        </li>

        <li Class="QuestNode_IsSet">
          <name>asker</name>
          <elseNode Class="QuestNode_GetPawn">
            <storeAs>asker</storeAs>
            <mustBeFactionLeader>true</mustBeFactionLeader>
            <mustBeNonHostileToPlayer>true</mustBeNonHostileToPlayer>
            <hostileWeight>0</hostileWeight>
            <selectionWeight>1.0</selectionWeight>
            <minTechLevel>Industrial</minTechLevel>
          </elseNode>
        </li>

        <li Class="QuestNode_Root_Site">
          <layerWhitelist>
            <li>Orbit</li>
          </layerWhitelist>
          <canBeSpace>true</canBeSpace>
          <sitePartDef>OrbitalFugitivePlatform</sitePartDef>
          <worldObjectDef>ClaimableSpaceSite</worldObjectDef>
          <distanceFromColonyRange>2~8</distanceFromColonyRange> <!-- distance is in on the orbital layer tiles -->
        </li>

        <li Class="QuestNode_WorldObjectTimeout">
          <worldObject>$site</worldObject>
          <isQuestTimeout>true</isQuestTimeout>
          <delayTicks>$(randInt(45,60)*60000)</delayTicks>
          <inSignalDisable>site.MapGenerated</inSignalDisable>
          <node Class="QuestNode_End">
            <outcome>Fail</outcome>
            <sendStandardLetter>true</sendStandardLetter>
          </node>
        </li>

        <li Class="QuestNode_Root_OrbitalFugitive" />

        <li Class="QuestNode_GenerateThingSet">
          <thingSetMaker>Reward_GravshipUpgrade</thingSetMaker>
          <storeAs>upgradeItem</storeAs>
        </li>

        <!-- Send rewards and end after survey has been completed -->
        <li Class="QuestNode_AllSignals">
          <inSignals>
            <li>fugitive.Destroyed</li>
          </inSignals>
          <node Class="QuestNode_Sequence">
            <nodes>
              <li Class="QuestNode_AddItemsReward">
                <items>$upgradeItem</items>
              </li>
              <li Class="QuestNode_End">
                <outcome>Success</outcome>
                <sendStandardLetter>true</sendStandardLetter>
              </li>
            </nodes>
          </node>
        </li>
      </nodes>
    </root>
  </QuestScriptDef>

  <SitePartDef ParentName="SpaceSiteBase">
    <defName>OrbitalFugitivePlatform</defName>
    <expandingIconTexture>World/WorldObjects/Expanding/AbandonedPlatform</expandingIconTexture>
    <tags>
      <li>OrbitalFugitivePlatform</li>
    </tags>
    <minMapSize>(200, 0, 200)</minMapSize>
    <copyQuestName>true</copyQuestName>
  </SitePartDef>

  <GenStepDef>
    <defName>OrbitalFugitivePlatform</defName>
    <linkWithSite>OrbitalFugitivePlatform</linkWithSite>
    <order>200</order>
    <genStep Class="GenStep_OrbitalPlatform">
      <factionDef>Salvagers</factionDef>
      <platformTerrain>OrbitalPlatform</platformTerrain>
      <layoutDef>Opportunity_AbandonedPlatform_Enterable</layoutDef>
      <orbitalDebrisDef>Manmade</orbitalDebrisDef>
      <temperature>20</temperature>
      <spawnSentryDrones>false</spawnSentryDrones>
    </genStep>
  </GenStepDef>

</Defs>
```
Note: `fugitive.Destroyed` here is exactly the "tag a generated pawn, listen for `<tag>.Destroyed`"
pattern described in §0 — `QuestNode_Root_OrbitalFugitive` internally must be doing the equivalent of
`QuestNode_AddTag` with `tag=fugitive` on the pawn it generates (its C# body is opaque to `monop`, but
the signal name proves the tagging).

#### `Odyssey/Defs/QuestScriptDefs/Script_Site.xml` (`SurveySite`) — the "defend" template, full

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Defs>

  <QuestScriptDef>
    <defName>SurveySite</defName>
    <rootSelectionWeight>1</rootSelectionWeight>
    <expireDaysRange>20~30</expireDaysRange>
    <minRefireDays>30</minRefireDays>
    <everAcceptableInSpace>true</everAcceptableInSpace>
    <questNameRules>
      <rulesStrings>
        <li>questName->Surveying [site]</li>
        <li>questName->[remote] Survey</li>
        <li>questName(p=0.5)->Defending the Scanner</li>
        <li>questName(p=0.5)->Remote Protection</li>

        <li>site->Site</li>
        <li>site->Station</li>
        <li>site->Defense</li>
        <li>site->Operation</li>
        <li>remote->Remote</li>
        <li>remote->Regional</li>
        <li>remote->Distant</li>
      </rulesStrings>
    </questNameRules>
    <questDescriptionRules>
      <rulesStrings>
        <!-- Asker is null -->
        <li>questDescription(askerIsNull==true)->An anonymous AI has requested your help defending surveying equipment at a remote site.\n\nThe surveying operation will take ten to fifteen days and will require traveling to a remote destination.\n\nIn exchange for your services, the AI is offering a valuable gravtech device.</li>

        <!-- Leader asker -->
        <li>questDescription(asker_factionLeader==true)->[asker_faction_leaderTitle] [asker_nameFull] of [asker_faction_name] has heard rumors of your gravship. [asker_pronoun] wants to survey a distant region and asks that you protect [asker_possessive] surveying equipment.\n\nThe surveying operation will take ten to fifteen days and will require traveling to a remote destination.\n\nIn exchange for your services, [asker_nameDef] is offering a valuable gravtech device.</li>

        <!-- Royal asker -->
        <li>questDescription(asker_royalInCurrentFaction==true)->[asker_nameFull], a [asker_royalTitleInCurrentFaction] of [asker_faction_name], requests that you protect [asker_possessive] surveying operation. If you do so, [asker_pronoun] will reward you handsomely.\n\nThe surveying operation will take ten to fifteen days and will require traveling to a remote destination.\n\nIn exchange for your services, [asker_nameDef] is offering a valuable gravtech device.</li>
      </rulesStrings>
    </questDescriptionRules>
    <questContentRules>
      <rulesStrings>
        <li>attackReason->claim the surveying scanner was sent to spy on their secret rituals</li>
        <li>attackReason->say the surveying site infringes on their territory</li>
        <li>attackReason->say they were sent to destroy the scanner as payback</li>
        <li>attackReason->claim the surveying scanner is too close to one of their hidden stashes</li>
        <li>attackReason->want to scrap the surveying equipment for parts</li>
        <li>attackReason->claim that the surveying scanner is secretly a weapon and its activation has caused psychic disturbances</li>
      </rulesStrings>
    </questContentRules>
    <canOccurOnAllPlanetLayers>true</canOccurOnAllPlanetLayers>
    <root Class="QuestNode_Sequence">
      <nodes>

        <li Class="QuestNode_RequirementsToAcceptResearch">
          <reserach>BasicGravtech</reserach>
        </li>

        <li Class="QuestNode_SubScript">
          <def>Util_GetDefaultRewardValueFromPoints</def>
        </li>

        <li Class="QuestNode_SubScript">
          <def>Util_AdjustPointsForDistantFight</def>
        </li>

        <li Class="QuestNode_GetMap">
          <canBeSpace>true</canBeSpace>
        </li>

        <li Class="QuestNode_IsSet">
          <name>asker</name>
          <elseNode Class="QuestNode_RandomNode">
            <nodes>
              <li Class="QuestNode_Set">
                <name>askerIsNull</name>
                <value>true</value>
                <selectionWeight>0.4</selectionWeight>
              </li>
              <li Class="QuestNode_GetPawn">
                <storeAs>asker</storeAs>
                <mustBeFactionLeader>true</mustBeFactionLeader>
                <mustBeNonHostileToPlayer>true</mustBeNonHostileToPlayer>
                <hostileWeight>0</hostileWeight>
                <selectionWeight>0.6</selectionWeight>
                <minTechLevel>Industrial</minTechLevel>
              </li>
            </nodes>
          </elseNode>
        </li>

        <li Class="QuestNode_Root_Site">
          <layerWhitelist>
            <li>Surface</li>
          </layerWhitelist>
          <sitePartDef>Opportunity_SurveySite</sitePartDef>
          <worldObjectDef>ClaimableSite</worldObjectDef>
          <distanceFromColonyRange>20~80</distanceFromColonyRange>
          <selectLandmarkChance>1</selectLandmarkChance>
          <desperateIgnoreDistance>true</desperateIgnoreDistance>
          <allowedLandmarks>
            <!-- ~40 landmark entries, no MayRequire (works without Odyssey too) -->
          </allowedLandmarks>
        </li>

        <li Class="QuestNode_WorldObjectTimeout">
          <worldObject>$site</worldObject>
          <isQuestTimeout>true</isQuestTimeout>
          <delayTicks>$(randInt(15,20)*60000)</delayTicks>
          <inSignalDisable>site.MapGenerated</inSignalDisable>
          <node Class="QuestNode_Sequence">
            <nodes>
              <li Class="QuestNode_Letter">
                <label TKey="LetterLabelQuestExpired">Quest expired: [resolvedQuestName]</label>
                <text TKey="LetterTextQuestExpired">The scanner has gone offline. The quest [resolvedQuestName] has expired.</text>
              </li>
              <li Class="QuestNode_End">
                <outcome>Fail</outcome>
              </li>
            </nodes>
          </node>
        </li>

        <li Class="QuestNode_Set">
          <name>duration</name>
          <value>$(randInt(10,15)*60000)</value>
        </li>

        <li Class="QuestNode_Root_SurveyScanner">
          <site>$site</site>
          <duration>$duration</duration>
          <raidChance>1</raidChance>
          <raidAttackRemainingHoursRange>6~48</raidAttackRemainingHoursRange>
          <raidLetterText TKey="LetterTextSurveySiteRaid">{BASETEXT}\n\nThe [enemyFaction_pawnsPlural] [attackReason].</raidLetterText>
        </li>

        <li Class="QuestNode_GenerateThingSet">
          <thingSetMaker>Reward_GravshipUpgrade</thingSetMaker>
          <storeAs>upgradeItem</storeAs>
        </li>

        <!-- Send rewards and end after survey has been completed -->
        <li Class="QuestNode_AllSignals">
          <inSignals>
            <li>site.SurveyCompleted</li>
          </inSignals>
          <node Class="QuestNode_Delay">
            <delayTicks>300</delayTicks>
            <node Class="QuestNode_Sequence">
              <nodes>
                <li Class="QuestNode_Letter">
                  <label TKey="LetterLabelSurveySiteQuestCompleted">Quest completed</label>
                  <letterDef>PositiveEvent</letterDef>
                  <text TKey="LetterTextSurveySiteQuestCompleted">You have successfully completed the quest '[resolvedQuestName]'!</text>
                </li>
                <li Class="QuestNode_AddItemsReward">
                  <items>$upgradeItem</items>
                </li>
                <li Class="QuestNode_End">
                  <outcome>Success</outcome>
                </li>
              </nodes>
            </node>
          </node>
        </li>

        <!-- Ending -->
        <li Class="QuestNode_NoWorldObject">
          <worldObject>$site</worldObject>
          <node Class="QuestNode_End">
            <outcome>Fail</outcome>
          </node>
        </li>

      </nodes>
    </root>
  </QuestScriptDef>

</Defs>
```

#### Core utility subscripts referenced above, full (`Scripts_Utility_ThreatsCore.xml`)

The **repeatable-wave raid pattern**, fully XML (used to compose "defend" logic without a custom node):

```xml
  <!-- Send a single raid to attack the player.
  Params:
    map           : Map where the raid arrives
    enemyFaction  : Raid faction
    customLetterX : Custom letter texts
  -->
  <QuestScriptDef>
    <defName>Util_Raid</defName>
    <questDescriptionRules>
      <rulesStrings>
        <li>threatDescription->[enemyFaction_pawnsPlural] from [enemyFaction_name] will attack you. Their group is composed of:\n\n[raidPawnKinds]\n\n[raidArrivalModeInfo]</li>
      </rulesStrings>
    </questDescriptionRules>
    <root Class="QuestNode_Sequence">
      <nodes>
        <li Class="QuestNode_Raid">
          <tag>$tag</tag>
          <customLetterLabel>$customLetterLabel</customLetterLabel>
          <customLetterText>$customLetterText</customLetterText>
          <customLetterLabelRules>$customLetterLabelRules</customLetterLabelRules>
          <customLetterTextRules>$customLetterTextRules</customLetterTextRules>
          <arrivalMode>$arrivalMode</arrivalMode>
          <canTimeoutOrFlee>$canTimeoutOrFlee</canTimeoutOrFlee>
          <raidPawnKind>$raidPawnKind</raidPawnKind>
          <inSignalLeave>EndRaid</inSignalLeave>
        </li>
      </nodes>
    </root>
  </QuestScriptDef>

  <!-- Send a series of raids to attack the player, with a fixed time interval between each raid.
  Params:
    map                   : Map where the raids arrive.
    firstRaidDelayTicks   : delay before first raid
    raidCount             : number of raids
    raidX/raidDelayTicks  : optional specific delay for raid X
  Constants:
    time between raids    : 9000 ticks
     -->
  <QuestScriptDef>
    <defName>Util_RaidDelayRepeatable</defName>
    <questDescriptionRules>
      <rulesStrings>
        <li>pawnKindsParagraph(raidCount==1)->The group of [enemyFaction_pawnsPlural] is composed of: \n\n[raid0/raidPawnKinds]\n\n[raid0/raidArrivalModeInfo]</li>
        <li>pawnKindsParagraph(raidCount==2)->The first group of [enemyFaction_pawnsPlural] is composed of: \n\n[raid0/raidPawnKinds]\n\nThe second similar-sized group will follow soon after.</li>
        <li>pawnKindsParagraph(raidCount==3)->The first group of [enemyFaction_pawnsPlural] is composed of: \n\n[raid0/raidPawnKinds]\n\nTwo similar-sized groups will follow soon after.</li>
        <li>pawnKindsParagraph(raidCount>=4)->The first group of [enemyFaction_pawnsPlural] is composed of: \n\n[raid0/raidPawnKinds]\n\n[raidCountMinusOne] similar-sized groups will follow soon after.</li>
        <li>numGroupsOf(raidCount==1)-></li>
        <li>numGroupsOf(raidCount==2)->two groups of</li>
        <li>numGroupsOf(raidCount==3)->three groups of</li>
        <li>numGroupsOf(raidCount>=4)->[raidCount] groups of</li>
      </rulesStrings>
    </questDescriptionRules>
    <root Class="QuestNode_Sequence">
      <nodes>
        <li Class="QuestNode_GetFaction"> <!-- does nothing if acceptable faction is already stored -->
          <allowEnemy>true</allowEnemy>
          <mustBePermanentEnemy>true</mustBePermanentEnemy>
          <storeAs>enemyFaction</storeAs>
        </li>

        <li Class="QuestNode_Set">
          <name>nextRaidDelayTicks</name>
          <value>$firstRaidDelayTicks</value>
        </li>

        <li Class="QuestNode_Set">
          <name>raidCountMinusOne</name>
          <value>$raidCount</value>
        </li>
        <li Class="QuestNode_Subtract">
          <value1>$raidCountMinusOne</value1>
          <value2>1</value2>
          <storeAs>raidCountMinusOne</storeAs>
        </li>

        <li Class="QuestNode_LoopCount">
          <loopCount>$raidCount</loopCount>
          <storeLoopCounterAs>raidLoopCounter</storeLoopCounterAs>
          <node Class="QuestNode_Sequence">
            <nodes>
              <!-- Try using specific delay -->
              <li Class="QuestNode_IsNull">
                <value>$raid(($raidLoopCounter))/raidDelayTicks</value>
                <elseNode Class="QuestNode_Set">
                  <name>nextRaidDelayTicks</name>
                  <value>$raid(($raidLoopCounter))/raidDelayTicks</value>
                </elseNode>
              </li>

              <li Class="QuestNode_Delay">
                <delayTicks>$nextRaidDelayTicks</delayTicks>
                <waitUntilPlayerHasHomeMap>true</waitUntilPlayerHasHomeMap>
                <node Class="QuestNode_Sequence">
                  <nodes>
                    <li Class="QuestNode_SubScript">
                      <prefix>raid$raidLoopCounter</prefix>
                      <def>Util_Raid</def>
                      <parms>
                        <tag>lord</tag>
                        <inSignal>$inSignal</inSignal>
                        <map>$map</map>
                        <enemyFaction>$enemyFaction</enemyFaction>
                        <points>$points</points>
                        <walkInSpot>$walkInSpot</walkInSpot>
                        <customLetterLabel>$customLetterLabel</customLetterLabel>
                        <customLetterText>$customLetterText</customLetterText>
                        <customLetterLabelRules>$customLetterLabelRules</customLetterLabelRules>
                        <customLetterTextRules>$customLetterTextRules</customLetterTextRules>
                      </parms>
                    </li>
                    <li Class="QuestNode_Equal">
                      <value1>$raidLoopCounter</value1>
                      <value2>$($raidCount - 1)</value2>
                      <compareAs>int</compareAs>
                      <node Class="QuestNode_SendSignals">
                        <outSignals>AllRaidsSent</outSignals>
                      </node>
                    </li>
                  </nodes>
                </node>
              </li>
              <li Class="QuestNode_Add"> <!-- next raid comes 9000 ticks after the previous -->
                <value1>$nextRaidDelayTicks</value1>
                <value2>9000</value2>
                <storeAs>nextRaidDelayTicks</storeAs>
              </li>
            </nodes>
          </node>
        </li>
      </nodes>
    </root>
  </QuestScriptDef>
```

The consumer of `Util_RaidDelayRepeatable`, `ThreatReward_Raid_MiscReward` (`Royalty/.../Scripts_RewardRaid.xml`), ends like this — copy this ending pattern for "defend and survive N waves":

```xml
        <!-- End -->
        <li Class="QuestNode_AllSignals">
          <inSignals>
            <li>AllRaidsSent</li>
            <li>raid$($raidCountMinusOne)/lord.AllEnemiesDefeated</li>
          </inSignals>
          <node Class="QuestNode_Sequence">
            <nodes>
              <li Class="QuestNode_Delay">
                <delayTicks>300</delayTicks>
                <waitUntilPlayerHasHomeMap>true</waitUntilPlayerHasHomeMap>
                <node Class="QuestNode_Sequence">
                  <nodes>
                    <li Class="QuestNode_HasRoyalTitleInCurrentFaction">
                      <pawn>$asker</pawn>
                      <node Class="QuestNode_GiveRewards">
                        <parms>
                          <allowGoodwill>true</allowGoodwill>
                          <allowRoyalFavor>true</allowRoyalFavor>
                        </parms>
                      </node>
                      <elseNode Class="QuestNode_GiveRewards">
                        <parms>
                          <thingRewardItemsOnly>true</thingRewardItemsOnly>
                        </parms>
                      </elseNode>
                    </li>
                    <li Class="QuestNode_End" />
                  </nodes>
                </node>
              </li>
            </nodes>
          </node>
        </li>

        <!-- Map removed -->
        <li Class="QuestNode_Signal">
          <inSignal>map.MapRemoved</inSignal>
          <node Class="QuestNode_End">
            <outcome>Fail</outcome>
            <sendStandardLetter>true</sendStandardLetter>
          </node>
        </li>
```

### How quests are actually offered (`Storyteller/Incidents_World_Quests.xml`, Core, full)

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Defs>

  <IncidentDef Abstract="True" Name="GiveQuestBase">
    <category>GiveQuest</category>
    <targetTags>
      <li>World</li>
    </targetTags>
    <workerClass>IncidentWorker_GiveQuest</workerClass>
    <letterDef>NewQuest</letterDef>
    <baseChance>1</baseChance>
    <canOccurOnAllPlanetLayers>true</canOccurOnAllPlanetLayers> <!-- Quests check if they can run on a layer. -->
  </IncidentDef>

  <IncidentDef ParentName="GiveQuestBase">
    <defName>GiveQuest_Random</defName>
    <label>quest</label>
    <earliestDay>2</earliestDay>
    <letterLabel>Quest available</letterLabel>
  </IncidentDef>

  <IncidentDef ParentName="GiveQuestBase">
    <defName>GiveQuest_EndGame_ShipEscape</defName>
    <label>journey offer</label>
    <letterLabel>Journey offer</letterLabel>
    <questScriptDef>EndGame_ShipEscape</questScriptDef>
    <baseChance>0</baseChance> <!-- given by a special storyteller comp -->
  </IncidentDef>

</Defs>
```

---

## 2. Exact C# signatures (from `monop`)

### `RimWorld.QuestScriptDef`

```csharp
public class QuestScriptDef : Verse.Def, System.IEquatable<Verse.Def> {

    public QuestScriptDef ();

    public bool CanRun (float points, IIncidentTarget target);
    public bool CanRun (RimWorld.QuestGen.Slate slate, IIncidentTarget target);
    public virtual void ClearCachedData ();
    public override System.Collections.Generic.IEnumerable<string> ConfigErrors ();
    public bool Equals (Verse.Def other);
    public T GetModExtension<T> () where T : Verse.DefModExtension;
    public bool HasModExtension<T> () where T : Verse.DefModExtension;
    public void InitializeRules ();
    public bool IsParentSuitableForQuest (RimWorld.Planet.MapParent mapParent);
    public virtual void PostLoad ();
    public virtual void PostSetIndices ();
    public void ResolveDefNameHash ();
    public override void ResolveReferences ();
    public void Run ();
    public RimWorld.Planet.MapParent TryFindNewSuitableMapParentForRetarget ();

    public bool IsEpic { get; }
    public bool IsRootAny { get; }
    public bool IsRootDecree { get; }
    public bool IsRootRandomSelected { get; }
    public virtual Verse.TaggedString LabelCap { get; }

    // Fields (the ones you set in XML):
    public RimWorld.QuestGen.QuestNode root;
    public float rootSelectionWeight;
    public Verse.SimpleCurve rootSelectionWeightFactorFromPointsCurve;
    public bool randomlySelectable;
    public float rootMinPoints;
    public float rootMinProgressScore;
    public int rootEarliestDay;
    public bool rootIncreasesPopulation;
    public float minRefireDays;
    public float decreeSelectionWeight;
    public System.Collections.Generic.List<string> decreeTags;
    public Verse.Grammar.RulePack questDescriptionRules;
    public Verse.Grammar.RulePack questNameRules;
    public Verse.Grammar.RulePack questDescriptionAndNameRules;
    public Verse.Grammar.RulePack questContentRules;
    public Verse.Grammar.RulePack questSubjectRules;
    public bool autoAccept;
    public bool hideOnCleanup;
    public Verse.FloatRange expireDaysRange;
    public bool nameMustBeUnique;
    public int defaultChallengeRating;
    public bool defaultHidden;
    public bool isRootSpecial;
    public bool canGiveRoyalFavor;
    public string questAvailableLetterLabel;
    public Verse.LetterDef questAvailableLetterDef;
    public bool questAvailableLetterTextIsDescription;
    public bool hideInvolvedFactionsInfo;
    public bool affectedByPopulation;
    public bool affectedByPoints;
    public bool defaultCharity;
    public HistoryEventDef successHistoryEvent;
    public HistoryEventDef failedOrExpiredHistoryEvent;
    public bool sendAvailableLetter;
    public bool epic;
    public QuestScriptDef epicParent;
    public bool endOnColonyMove;
    public bool everAcceptableInSpace;
    public bool neverPossibleInSpace;
    public System.Collections.Generic.List<QuestGiverTag> givenBy;
    public System.Collections.Generic.List<PlanetLayerDef> layerWhitelist;
    public System.Collections.Generic.List<PlanetLayerDef> layerBlacklist;
    public bool canOccurOnAllPlanetLayers;
}
```

### `RimWorld.QuestGen.QuestNode` (abstract base — everything under `<root>` derives from this)

```csharp
public abstract class QuestNode {
    public QuestNode ();

    public void Run ();
    protected abstract void RunInt ();
    public float SelectionWeight (Slate slate);
    public bool TestRun (Slate slate);
    protected abstract bool TestRunInt (Slate slate);

    public string myTypeShort;                 // [Unsaved][TranslationHandle]
    public SlateRef<T> selectionWeight;
}
```
**A custom `QuestNode` subclass only needs to override `RunInt()` and `TestRunInt(Slate)`.**
`RunInt` does the real work against `QuestGen.slate` / `QuestGen.quest`; `TestRunInt` is a
dry-run/validity check used by `CanRun`/quest eligibility.

### `RimWorld.QuestGen.QuestNode_Root_Mission` (abstract — the base class to copy for a custom "attack a site" root)

```csharp
public abstract class QuestNode_Root_Mission : QuestNode {
    protected QuestNode_Root_Mission ();

    public static bool PawnCanFight (Verse.Pawn p);
    protected virtual bool CanGetAsker ();
    protected virtual bool DoesPawnCountAsAvailableForFight (Verse.Pawn p);
    protected abstract RimWorld.Planet.Site GenerateSite (Verse.Pawn asker, float threatPoints, int pawnCount, int population, RimWorld.Planet.PlanetTile tile);
    protected abstract Verse.Pawn GetAsker (RimWorld.Quest quest);
    protected abstract int GetRequiredPawnCount (int population, float threatPoints);
    protected override void RunInt ();
    protected override bool TestRunInt (Slate slate);
    protected virtual bool TryFindSiteTile (out RimWorld.Planet.PlanetTile tile, bool exitOnFirstTileFound);

    protected virtual bool AddCampLootReward { get; }
    protected virtual bool IsViolent { get; }
    protected abstract string QuestTag { get; }

    public const int MinTilesAwayFromColony = 80;
    public const int MaxTilesAwayFromColony = 85;
    public Verse.FloatRange timeLimitDays;
    public bool canBeSpace;
}
```
Concrete example, `QuestNode_Root_Mission_BanditCamp`, overrides exactly those 4 abstract members plus
2 fields (`factionsToDrawLeaderFrom`, `siteFactions`) — the tightest possible template for a custom
"attack a generated site guarded by a hostile faction" root node.

### `RimWorld.QuestGen.QuestNode_Root_OrbitalFugitive` (concrete — shows the shape of a from-scratch root node)

```csharp
public class QuestNode_Root_OrbitalFugitive : QuestNode {
    public QuestNode_Root_OrbitalFugitive ();
    protected override void RunInt ();
    protected override bool TestRunInt (Slate slate);
    // no extra public fields — all its parameters are read from slate vars set by
    // earlier sibling nodes (site, asker, etc.) rather than its own XML fields.
}
```

### `RimWorld.QuestGen.QuestGen` (static — the generation-time context)

```csharp
public static class QuestGen {
    public static void AddQuestContentRules (System.Collections.Generic.List<Verse.Grammar.Rule> rules);
    public static void AddQuestContentRules (Verse.Grammar.RulePack rulePack);
    public static void AddQuestDescriptionConstants (System.Collections.Generic.Dictionary<string,string> constants);
    public static void AddQuestDescriptionRules (System.Collections.Generic.List<Verse.Grammar.Rule> rules);
    public static void AddQuestDescriptionRules (Verse.Grammar.RulePack rulePack);
    public static void AddQuestNameConstants (System.Collections.Generic.Dictionary<string,string> constants);
    public static void AddQuestNameRules (System.Collections.Generic.List<Verse.Grammar.Rule> rules);
    public static void AddQuestNameRules (Verse.Grammar.RulePack rulePack);
    public static void AddSlateQuestTagToAddWhenFinished (string slateVarNameWithPrefix);
    public static void AddTextRequest (string localKeyword, Action<string> setter, System.Collections.Generic.List<Verse.Grammar.Rule> extraLocalRules);
    public static void AddTextRequest (string localKeyword, Action<string> setter, Verse.Grammar.RulePack extraLocalRules);
    public static void AddToGeneratedPawns (Verse.Pawn pawn);
    public static RimWorld.Quest Generate (RimWorld.QuestScriptDef root, Slate initialVars);
    public static string GenerateNewSignal (string signalString, bool ensureUnique);
    public static string GenerateNewTargetQuestTag (string targetString, bool ensureUnique);
    public static string GenerateResolvedQuestName (RimWorld.QuestScriptDef root, Slate initialVars);
    public static bool WasGeneratedForQuestBeingGenerated (Verse.Pawn pawn);

    public static RimWorld.QuestScriptDef Root { get; }
    public static bool Working { get; }

    // The two statics every custom QuestNode.RunInt() touches:
    public static RimWorld.Quest quest;
    public static Slate slate;
}
```
**Pattern for a custom node**: inside `RunInt()`, read inputs off `QuestGen.slate.Get<T>(name, ...)`,
do work, then either `QuestGen.slate.Set(name, value)` (to hand a value to later XML nodes) or
`QuestGen.quest.AddPart(new QuestPart_Whatever { ... })` (to attach live runtime behavior).

### `RimWorld.QuestGen.QuestGenUtility` (static helpers)

```csharp
public static class QuestGenUtility {
    public static void AddRangeToOrMakeList (Slate slate, string name, System.Collections.Generic.List<object> objs);
    public static void AddToOrMakeList (Slate slate, string name, object obj);
    public static string HardcodedSignalWithQuestID (string signal);
    public static string HardcodedTargetQuestTagWithQuestID (string questTag);
    public static bool IsInList (Slate slate, string name, object obj);
    public static Verse.ChoiceLetter MakeLetter (string labelKeyword, string textKeyword, Verse.LetterDef def, RimWorld.Faction relatedFaction, RimWorld.Quest quest);
    public static Verse.ChoiceLetter MakeLetter (string labelKeyword, string textKeyword, Verse.LetterDef def, Verse.LookTargets lookTargets, RimWorld.Faction relatedFaction, RimWorld.Quest quest);
    public static string NormalizeVarPath (string path);
    public static string QuestTagSignal (string questTag, string signal);
    public static void RunAdjustPointsForDistantFight ();
    public static void RunInner (Action inner, RimWorld.QuestPartActivable outerQuestPart);
    public static void RunInner (Action inner, string innerNodeInSignal);
    public static void RunInnerNode (QuestNode node, RimWorld.QuestPartActivable outerQuestPart);
    public static void RunInnerNode (QuestNode node, string innerNodeInSignal);
    public static Verse.LookTargets ToLookTargets (System.Collections.Generic.IEnumerable<object> objects);
    public static Verse.LookTargets ToLookTargets (SlateRef<IEnumerable<object>> objects, Slate slate);

    public const string OuterNodeCompletedSignal = "OuterNodeCompleted";
}
```

### `RimWorld.QuestGen.QuestGen_Sites` / `RimWorld.QuestGen.QuestGen_Pawns` (static extension-method helpers used by custom nodes)

```csharp
public static class QuestGen_Sites {
    public static RimWorld.Planet.Site GenerateSite (
        System.Collections.Generic.IEnumerable<RimWorld.Planet.SitePartDefWithParams> sitePartsParams,
        RimWorld.Planet.PlanetTile tile, RimWorld.Faction faction, bool hiddenSitePartsPossible,
        Verse.Grammar.RulePack singleSitePartRules, RimWorld.WorldObjectDef worldObjectDef);

    // extension methods on Quest:
    public static QuestPart_SetSitePartThreatPointsToCurrent SetSitePartThreatPointsToCurrent (this Quest quest, RimWorld.Planet.Site site, SitePartDef sitePartDef, RimWorld.Planet.MapParent useMapParentThreatPoints, string inSignal, float threatPointsFactor);
    public static QuestPart_SpawnWorldObject SpawnWorldObject (this Quest quest, RimWorld.Planet.WorldObject worldObject, System.Collections.Generic.List<Verse.ThingDef> defsToExcludeFromHyperlinks, string inSignal);
    public static QuestPart_StartDetectionRaids StartRecurringRaids (this Quest quest, RimWorld.Planet.WorldObject worldObject, System.Nullable<Verse.FloatRange> delayRangeHours, Nullable<int> firstRaidDelayTicks, string inSignal);
}

public static class QuestGen_Pawns {
    // extension methods on Quest — this is how custom nodes attach pawn-tracking QuestParts in C#:
    public static QuestPart_EnsureNotDowned EnsureNotDowned (this Quest quest, IEnumerable<Verse.Pawn> pawns, string inSignal);
    public static Verse.Pawn GeneratePawn (this Quest quest, Verse.PawnGenerationRequest request, bool ensureNonNumericName);
    public static Verse.Pawn GeneratePawn (this Quest quest, Verse.PawnKindDef kindDef, RimWorld.Faction faction, bool allowAddictions, IEnumerable<RimWorld.TraitDef> forcedTraits, float biocodeWeaponChance, bool mustBeCapableOfViolence, Verse.Pawn extraPawnForExtraRelationChance, float relationWithExtraPawnChanceFactor, float biocodeApparelChance, bool ensureNonNumericName, bool forceGenerateNewPawn, Verse.DevelopmentalStage developmentalStages, bool allowPregnant);
    public static Verse.Pawn GetPawn (this Quest quest, GetPawnParms parms);
    public static QuestPart_ReservePawns ReservePawns (this Quest quest, IEnumerable<Verse.Pawn> pawns);

    public struct GetPawnParms {
        public System.Nullable<RimWorld.Planet.PlanetTile> tile;
        public bool mustBeFactionLeader;
        public bool mustBeWorldPawn;
        public bool ifWorldPawnThenMustBeFree;
        public bool mustHaveNoFaction;
        public bool mustBeFreeColonist;
        public bool mustBePlayerPrisoner;
        public bool mustHaveRoyalTitleInCurrentFaction;
        public bool mustBeNonHostileToPlayer;
        public Nullable<bool> allowPermanentEnemyFaction;
        public bool canGeneratePawn;
        public Verse.PawnKindDef mustBeOfKind;
        public RimWorld.Faction mustBeOfFaction;
        public Verse.FloatRange seniorityRange;
        public RimWorld.TechLevel minTechLevel;
        public System.Collections.Generic.List<RimWorld.FactionDef> excludeFactionDefs;
        public bool mustBeCapableOfViolence;
        // ...
    }
    public const int MaxUsablePawnsToGenerate = 10;
}
```
No `EnsureIsDead`/`EnsureIsCaptured`-style helper exists on `QuestGen_Pawns` — confirms §0's point that
`QuestPart_IsDead`/`QuestPart_IsPrisoner` are wired up ad hoc inside specific `Root_*` nodes, not via a
shared helper.

### `RimWorld.QuestPart` (abstract — base of ALL runtime quest behavior)

```csharp
public abstract class QuestPart : Verse.IExposable, Verse.ILoadReferenceable {
    protected QuestPart ();

    public virtual void Cleanup ();
    public virtual void ExposeData ();
    public virtual void Notify_FactionRemoved (Faction faction);
    public virtual void Notify_PawnBorn (Verse.Thing baby, Verse.Thing birther, Verse.Pawn mother, Verse.Pawn father);
    public virtual void Notify_PawnDiscarded (Verse.Pawn pawn);
    public virtual void Notify_PawnKilled (Verse.Pawn pawn, System.Nullable<Verse.DamageInfo> dinfo);
    public virtual void Notify_PlantHarvested (Verse.Pawn worker, Verse.Thing harvested);
    public virtual void Notify_PreCleanup ();
    public virtual void Notify_QuestSignalReceived (Signal signal);   // <-- the main override point
    public virtual void Notify_ThingsProduced (Verse.Pawn worker, System.Collections.Generic.List<Verse.Thing> things);
    public virtual void PostQuestAdded ();
    public virtual void PreQuestAccept ();
    public virtual bool QuestPartReserves (Faction f);
    public virtual bool QuestPartReserves (Verse.Pawn p);
    public virtual bool QuestPartReserves (TransportShip ship);
    public virtual void ReplacePawnReferences (Verse.Pawn replace, Verse.Pawn with);

    public virtual string DescriptionPart { get; }
    public virtual bool IncreasesPopulation { get; }
    public virtual System.Collections.Generic.IEnumerable<Faction> InvolvedFactions { get; }
    public virtual bool PreventsAutoAccept { get; }
    public virtual System.Collections.Generic.IEnumerable<RimWorld.Planet.GlobalTargetInfo> QuestLookTargets { get; }
    public virtual System.Collections.Generic.IEnumerable<RimWorld.Planet.GlobalTargetInfo> QuestSelectTargets { get; }
    public virtual bool RequiresAccepter { get; }

    public Quest quest;
    public SignalListenMode signalListenMode;
    public string debugLabel;

    public enum SignalListenMode { OngoingOnly, NotYetAcceptedOnly, OngoingOrNotYetAccepted, HistoricalOnly, Always }
}
```

### `RimWorld.QuestPartActivable` (abstract — base for "enable → wait for signal → complete" parts; `QuestPart_IsDead`/`QuestPart_IsPrisoner` are this)

```csharp
public abstract class QuestPartActivable : QuestPart, Verse.IExposable, Verse.ILoadReferenceable {
    protected QuestPartActivable ();

    protected void Complete ();                                   // + overloads taking NamedArgument signal args
    protected virtual void Complete (SignalArgs signalArgs);
    public void DebugForceComplete ();
    protected virtual void Disable ();
    protected virtual void Enable (SignalArgs receivedArgs);
    public override void Notify_QuestSignalReceived (Signal signal);
    protected virtual void ProcessQuestSignal (Signal signal);
    public virtual void QuestPartTick ();

    public virtual bool AlertCritical { get; }
    public virtual string AlertLabel { get; }
    public int EnableTick { get; }
    public string OutSignalCompleted { get; }
    public string OutSignalEnabled { get; }
    public QuestPartState State { get; }

    public string inSignalEnable;
    public string inSignalDisable;
    public bool reactivatable;
    public System.Collections.Generic.List<string> outSignalsCompleted;
    public QuestEndOutcome outcomeCompletedSignalArg;
}
```

### `RimWorld.QuestPart_IsDead` / `QuestPart_IsPrisoner` (the direct "track this exact pawn" parts — full)

```csharp
public class QuestPart_IsDead : QuestPartActivable, Verse.IExposable, Verse.ILoadReferenceable {
    public QuestPart_IsDead ();
    public override void ExposeData ();
    public override void Notify_QuestSignalReceived (Signal signal);
    public override void QuestPartTick ();

    public Verse.Pawn pawn;                 // <-- the single tracked pawn
    public string inSignalEnable;
    public string inSignalDisable;
    public bool reactivatable;
    public System.Collections.Generic.List<string> outSignalsCompleted;   // fired when pawn.Dead becomes true
    public QuestEndOutcome outcomeCompletedSignalArg;
    // + all QuestPartActivable members above
}

public class QuestPart_IsPrisoner : QuestPartActivable, Verse.IExposable, Verse.ILoadReferenceable {
    public QuestPart_IsPrisoner ();
    public override void ExposeData ();
    public override void Notify_QuestSignalReceived (Signal signal);
    public override void QuestPartTick ();

    public Verse.Pawn pawn;                 // <-- the single tracked pawn
    // fields identical in shape to QuestPart_IsDead (inSignalEnable/Disable, outSignalsCompleted, ...)
}
```
Both classes have **no XML-facing `QuestNode_*` wrapper** — instantiate and `quest.AddPart(...)` them
from a custom `QuestNode.RunInt()` if you go this route instead of the `questTags`/signal route.

### `RimWorld.QuestPart_PawnKilled` / `QuestPart_PawnsKilled` (NOT single-pawn trackers — faction/race-scoped, for completeness)

```csharp
public class QuestPart_PawnKilled : QuestPart, ... {
    public override void Notify_PawnKilled (Verse.Pawn pawn, System.Nullable<Verse.DamageInfo> dinfo);
    public Faction faction;
    public RimWorld.Planet.MapParent mapParent;
    public string outSignal;                // fires whenever ANY pawn of `faction` dies on `mapParent`
}

public class QuestPart_PawnsKilled : QuestPartActivable, ... {   // wrapped by QuestNode_PawnsKilled
    public Verse.ThingDef race;
    public Faction requiredInstigatorFaction;
    public int count;                       // completes after `count` pawns of `race` die
    public RimWorld.Planet.MapParent mapParent;
    public string outSignalPawnKilled;
}
```

### `RimWorld.QuestPart_AssaultColony` / `QuestPart_DefendPoint` / `QuestPart_RandomRaid` (map-threat parts relevant to raid/defend)

```csharp
public class QuestPart_AssaultColony : QuestPart_MakeLord, ... {   // wrapped by QuestNode_AssaultColony
    public bool canTimeoutOrFlee;
    public bool canKidnap;
    public bool canSteal;
    public string questTag;
    public System.Collections.Generic.List<Verse.Pawn> pawns;
    public string inSignal;
    public Faction faction;
    public RimWorld.Planet.MapParent mapParent;
    public string inSignalRemovePawn;
}

public class QuestPart_DefendPoint : QuestPart_MakeLord, ... {     // NO QuestNode_* wrapper — custom-node-only
    public Verse.IntVec3 point;
    public Nullable<float> wanderRadius;
    public Nullable<float> defendRadius;
    public bool isCaravanSendable;
    public bool addFleeToil;
    public System.Collections.Generic.List<Verse.Pawn> pawns;
    public string inSignal;
    public Faction faction;
    public RimWorld.Planet.MapParent mapParent;
}

public class QuestPart_RandomRaid : QuestPart, ... {                // wrapped by QuestNode_RandomRaid
    public string inSignal;
    public RimWorld.Planet.MapParent mapParent;
    public Verse.FloatRange pointsRange;
    public Faction faction;
    public bool useCurrentThreatPoints;
    public float currentThreatPointsFactor;
    public PawnsArrivalModeDef arrivalMode;
    public RaidStrategyDef raidStrategy;
    public string customLetterLabel;
    public string customLetterText;
    public System.Collections.Generic.List<Verse.Thing> attackTargets;
    public bool generateFightersOnly;
    public bool fallbackToPlayerHomeMap;
    public bool sendLetter;
}
```

### `RimWorld.Quest`

```csharp
public class Quest : Verse.IExposable, Verse.ILoadReferenceable, ISignalReceiver {
    public Quest ();

    public static Quest MakeRaw ();
    public void Accept (Verse.Pawn by);
    public T AddPart<T> () where T : QuestPart;
    public void AddPart (QuestPart part);
    public void CleanupQuestParts ();
    public void End (QuestEndOutcome outcome, bool sendLetter, bool playSound);
    public void ExposeData ();
    public T GetFirstOrAddPart<T> () where T : QuestPart;
    public T GetFirstPartOfType<T> () where T : QuestPart;
    public void Initiate ();
    public bool IsParentSuitableForQuest (RimWorld.Planet.MapParent mapParent);
    public void Notify_FactionRemoved (Faction faction);
    public void Notify_PawnKilled (Verse.Pawn pawn, System.Nullable<Verse.DamageInfo> dinfo);
    public void Notify_SignalReceived (Signal signal);
    public void PostAdded ();
    public bool QuestReserves (Faction f);
    public bool QuestReserves (Verse.Pawn p);
    public void QuestTick ();
    public void RemovePart (QuestPart part);
    public void SetInitiallyAccepted ();
    public void SetNotYetAccepted ();
    public RimWorld.Planet.MapParent TryFindNewSuitableMapParentForRetarget ();
    public bool TryGetFirstPartOfType<T> (out T part) where T : QuestPart;

    public Verse.Pawn AccepterPawn { get; }
    public string AddedSignal { get; }
    public bool EverAccepted { get; }
    public bool Historical { get; }
    public System.Collections.Generic.List<QuestPart> PartsListForReading { get; }
    public QuestState State { get; }
    public int TicksSinceAccepted { get; }
    public int TicksUntilExpiry { get; }

    public int id;
    public string name;
    public Verse.TaggedString description;
    public float points;
    public int challengeRating;
    public System.Collections.Generic.List<string> tags;
    public QuestScriptDef root;
    public bool hidden;
    public Quest parent;
    public bool initiallyAccepted;
    public bool dismissed;
    public bool charity;
    public bool canGenerateInSpace;
}
```

### `RimWorld.QuestUtility` (static — includes the full signal-name-part constant catalog)

```csharp
public static class QuestUtility {
    public static void AddQuestTag (ref System.Collections.Generic.List<string> questTags, string questTagToAdd);
    public static void AddQuestTag (object obj, string questTagToAdd);
    public static Verse.AcceptanceReport CanAcceptQuest (Quest quest);
    public static bool CanPawnAcceptQuest (Verse.Pawn p, Quest quest);
    public static Quest GenerateQuestAndMakeAvailable (QuestScriptDef root, float points);
    public static Quest GenerateQuestAndMakeAvailable (QuestScriptDef root, RimWorld.QuestGen.Slate vars);
    public static Faction GetExtraFaction (this Verse.Pawn p, ExtraFactionType extraFactionType, Quest forQuest);
    public static void GetExtraFactionsFromQuestParts (Verse.Pawn pawn, System.Collections.Generic.List<ExtraFaction> outExtraFactions, Quest forQuest);
    public static int GetQuestTicksRemaining (Quest quest);
    public static bool IsQuestReward (this Verse.Pawn pawn, Quest quest);
    public static bool IsReservedByQuestOrQuestBeingGenerated (Verse.Pawn pawn);
    public static void SendLetterQuestAvailable (Quest quest, string discoveryMethod);
    public static void SendQuestTargetSignals (System.Collections.Generic.List<string> questTags, string signalPart);
    public static void SendQuestTargetSignals (System.Collections.Generic.List<string> questTags, string signalPart, Verse.NamedArgument arg1 /* ...up to 4 overloads, or SignalArgs */);

    // The signal-name-part vocabulary. Real signal string sent is "<questTag>.<Part>".
    // (all are `public const string`)
    QuestTargetSignalPart_MapGenerated = "MapGenerated"
    QuestTargetSignalPart_MapRemoved = "MapRemoved"
    QuestTargetSignalPart_MapSettled = "MapSettled"
    QuestTargetSignalPart_Spawned = "Spawned"
    QuestTargetSignalPart_Despawned = "Despawned"
    QuestTargetSignalPart_Destroyed = "Destroyed"
    QuestTargetSignalPart_Killed = "Killed"
    QuestTargetSignalPart_TookDamage = "TookDamage"
    QuestTargetSignalPart_TookDamageFromPlayer = "TookDamageFromPlayer"
    QuestTargetSignalPart_ChangedFaction = "ChangedFaction"
    QuestTargetSignalPart_ChangedFactionToPlayer = "ChangedFactionToPlayer"
    QuestTargetSignalPart_ChangedFactionToNonPlayer = "ChangedFactionToNonPlayer"
    QuestTargetSignalPart_Hacked = "Hacked"
    QuestTargetSignalPart_Unfogged = "Unfogged"
    QuestTargetSignalPart_Inspected = "Inspected"
    QuestTargetSignalPart_SwappedMap = "SwappedMap"
    QuestTargetSignalPart_LeftBehind = "LeftBehind"
    QuestTargetSignalPart_LeftMap = "LeftMap"
    QuestTargetSignalPart_Arrested = "Arrested"          // <-- "captured" for a hostile/world pawn
    QuestTargetSignalPart_Released = "Released"
    QuestTargetSignalPart_Recruited = "Recruited"
    QuestTargetSignalPart_Kidnapped = "Kidnapped"
    QuestTargetSignalPart_ChangedHostFaction = "ChangedHostFaction"
    QuestTargetSignalPart_NoLongerFactionLeader = "NoLongerFactionLeader"
    QuestTargetSignalPart_Banished = "Banished"
    QuestTargetSignalPart_Rescued = "Rescued"
    QuestTargetSignalPart_RanWild = "RanWild"
    QuestTargetSignalPart_Enslaved = "Enslaved"
    QuestTargetSignalPart_AllEnemiesDefeated = "AllEnemiesDefeated"
    QuestTargetSignalPart_ExitMentalState = "ExitMentalState"
    QuestTargetSignalPart_BeingAttacked = "BeingAttacked"
    QuestTargetSignalPart_Fleeing = "Fleeing"
    QuestTargetSignalPart_QuestEnded = "QuestEnded"
    // (full list is ~65 constants total; see raw dump for the rest — item/ship/monument/hack-specific ones)
}
```

### `RimWorld.Signal` / `RimWorld.SignalArgs`

```csharp
public struct Signal {
    public Signal (string tag, bool global);
    public Signal (string tag, SignalArgs args, bool global);
    public Signal (string tag, Verse.NamedArgument arg1 /* ...up to 4, or params array */);

    public string tag;
    public SignalArgs args;
    public bool global;
}
public struct SignalArgs { /* not separately dumped in detail — carries NamedArgument payload */ }
```

### `RimWorld.QuestGen.Slate` (the per-generation variable bag)

```csharp
public class Slate {
    public Slate ();
    public Slate DeepCopy ();
    public bool Exists (string name, bool isAbsoluteName);
    public T Get<T> (string name, T defaultValue, bool isAbsoluteName);
    public void PopPrefix ();
    public void PushPrefix (string newPrefix, bool allowNonPrefixedLookup);
    public bool Remove (string name, bool isAbsoluteName);
    public void Reset ();
    public void Set<T> (string name, T var, bool isAbsoluteName);
    public void SetAll (Slate otherSlate);
    public void SetIfNone<T> (string name, T var, bool isAbsoluteName);
    public bool TryGet<T> (string name, out T var, bool isAbsoluteName);

    public string CurrentPrefix { get; }
    public const char Separator = '/';
}
```

### `RimWorld.SitePartDef` (fields relevant to generating a site the player attacks)

```csharp
public class SitePartDef : Verse.Def, System.IEquatable<Verse.Def> {
    public bool CompatibleWith (SitePartDef part);
    public bool FactionCanOwn (Faction faction);
    public SitePartWorker Worker { get; }

    public Verse.ThingDef conditionCauserDef;
    public float activeThreatDisturbanceFactor;
    public Type workerClass;
    public string siteTexture;
    public string expandingIconTexture;
    public bool applyFactionColorToSiteTexture;
    public bool requiresFaction;
    public TechLevel minFactionTechLevel;
    public System.Collections.Generic.List<string> tags;            // <-- matched by QuestNode_GetSitePartDefsByTagsAndFaction's sitePartsTags
    public System.Collections.Generic.List<string> excludesTags;
    public string arrivedLetter;
    public Verse.LetterDef arrivedLetterDef;
    public bool wantsThreatPoints;
    public float minThreatPoints;
    public System.Nullable<Verse.IntVec3> minMapSize;
    public float selectionWeight;
    public bool considerEnteringAsAttack;
    public bool copyQuestName;
    public bool leaveAbandonedSettlement;
    public System.Collections.Generic.List<WorkSiteLootThing> lootTable;
}
```

### `RimWorld.Planet.Site` (partial — the world object generated for opportunity-site quests)

```csharp
public class Site : MapParent, Verse.IExposable, Verse.ILoadReferenceable, Verse.ISelectable, Verse.IThingHolder {
    public Site ();
    public void AddPart (SitePart part);
    public override void Destroy ();
    public override void PostMapGenerate ();
    public virtual void SetFaction (RimWorld.Faction newFaction);

    public float ActualThreatPoints { get; }
    public bool Destroyed { get; }
    public virtual RimWorld.BiomeDef Biome { get; }
    public override Verse.AcceptanceReport CanBeSettled { get; }
    // (MapParent base contributes HasMap, Map, etc.)
}
```

### `RimWorld.Planet.PlanetTile` (the 1.6 tile type — replaces bare `int` tile IDs)

```csharp
public struct PlanetTile : IEquatable<PlanetTile> {
    public PlanetTile (int tileId, PlanetLayer layer);
    public PlanetTile (int tileId);
    public PlanetTile (int tileId, int layerId);

    public static PlanetTile FromString (string str);
    public static bool TryParse (string str, out PlanetTile tile);

    public static bool operator == (PlanetTile lhs, PlanetTile rhs);
    public static implicit operator int (PlanetTile tile);
    public static implicit operator PlanetTile (int tileId);

    public PlanetLayer Layer { get; }
    public RimWorld.PlanetLayerDef LayerDef { get; }
    public Tile Tile { get; }
    public bool Valid { get; }

    public readonly int tileId;
    public static readonly PlanetTile Invalid;
}
```
**Any new C# code that takes/returns a "tile" in 1.6 should use `PlanetTile`, not `int`** — the
implicit conversions mean old-style `int` call sites still mostly compile, but APIs like
`QuestNode_Root_Mission.GenerateSite(...)`, `QuestGen_Sites.GenerateSite(...)`,
`QuestNode_GetSiteTile`, and `QuestNode_Root_Site.TryFindSiteTile` all declare `PlanetTile` in their
signatures, and `PlanetTile.Layer`/`LayerDef` is how 1.6 disambiguates Surface vs. Orbit vs. other
planet layers (new with Odyssey).

### How a quest is offered (`RimWorld.IncidentWorker_GiveQuest`, `RimWorld.NaturalRandomQuestChooser`, `RimWorld.IncidentDef`)

```csharp
public class IncidentWorker_GiveQuest : IncidentWorker {
    public IncidentWorker_GiveQuest ();
    public bool CanFireNow (IncidentParms parms);
    protected override bool CanFireNowSub (IncidentParms parms);
    protected virtual void GiveQuest (IncidentParms parms, QuestScriptDef questDef);
    protected override bool TryExecuteWorker (IncidentParms parms);
    public IncidentDef def;
}
public class IncidentWorker_GiveQuest_Map : IncidentWorker_GiveQuest { /* map-target variant */ }

public static class NaturalRandomQuestChooser {
    public static QuestScriptDef ChooseNaturalRandomQuest (float points, IIncidentTarget target);
    public static float GetNaturalRandomSelectionWeight (QuestScriptDef quest, float points, StoryState storyState);
    public static float PopulationIncreasingQuestChance ();
}

// RimWorld.IncidentDef relevant field:
public QuestScriptDef questScriptDef;   // null on GiveQuest_Random => picked at random by NaturalRandomQuestChooser
```

### `RimWorld.RewardsGeneratorParams` (the struct `QuestNode_GiveRewards.parms` takes)

```csharp
public struct RewardsGeneratorParams {
    public string ConfigError ();

    public float rewardValue;
    public Faction giverFaction;
    public string chosenPawnSignal;
    public bool giveToCaravan;
    public float minGeneratedRewardValue;
    public bool thingRewardDisallowed;
    public bool thingRewardRequired;
    public bool thingRewardItemsOnly;
    public System.Collections.Generic.List<Verse.ThingDef> disallowedThingDefs;
    public bool allowRoyalFavor;
    public bool allowGoodwill;
    public bool allowDevelopmentPoints;
    public bool allowXenogermReimplantation;
    public float populationIntent;
}
```

### `RimWorld.QuestGen.QuestNode_GiveRewards` (the XML node wrapping the above)

```csharp
public class QuestNode_GiveRewards : QuestNode {
    public SlateRef<string> inSignal;
    public SlateRef<RewardsGeneratorParams> parms;
    public SlateRef<string> customLetterLabel;
    public SlateRef<string> customLetterText;
    public SlateRef<bool> useDifficultyFactor;
    public QuestNode nodeIfChosenPawnSignalUsed;
    public SlateRef<...> variants;
    public SlateRef<bool> addCampLootReward;
}
```

---

## 3. Full grep-derived list of `QuestNode_*` and `QuestPart_*` types in 1.6

301 `QuestNode_*` types (namespace `RimWorld.QuestGen`) and 200 `QuestPart_*` types (namespace
`RimWorld`), grouped by rough function (grouping is mine, from the names/signatures — not an in-game
category):

### `QuestNode_*` (301 total, XML `Class="..."` names — drop the `QuestNode_` prefix shown, full names below)

**Root** (70 — quest entry points, one per `QuestScriptDef`'s `<root>`, mostly custom-C#-only):
`QuestNode_Root_AncientComplex, QuestNode_Root_AncientMercenaries, QuestNode_Root_AncientSignalActivation, QuestNode_Root_AncientStructure, QuestNode_Root_ArchonexusVictory(_Cycle/_FirstCycle/_SecondCycle/_ThirdCycle), QuestNode_Root_Asteroid, QuestNode_Root_Beggars, QuestNode_Root_BestowingCeremony, QuestNode_Root_Bossgroup, QuestNode_Root_Creepjoiner_Arrival, QuestNode_Root_DelayedRewardDropPods, QuestNode_Root_DistressCall, QuestNode_Root_GravShip, QuestNode_Root_Gravcore(+7 subtype variants), QuestNode_Root_Gravship_Wreckage, QuestNode_Root_Hack_AncientComplex, QuestNode_Root_Hack_Spacedrone, QuestNode_Root_Hack_WorshippedTerminal, QuestNode_Root_Hospitality_Refugee, QuestNode_Root_Loot_AncientComplex(_Mechanitor), QuestNode_Root_MechanitorShip, QuestNode_Root_MechanitorStartingMech, QuestNode_Root_MechanoidSignal, QuestNode_Root_Mission, QuestNode_Root_Mission_AncientComplex, QuestNode_Root_Mission_BanditCamp, QuestNode_Root_MonolithMigration, QuestNode_Root_MysteriousCargo(+RevenantSpine/+UnnaturalCorpse/+UnnaturalCube), QuestNode_Root_OrbitalFugitive, QuestNode_Root_PollutionDump, QuestNode_Root_PollutionRaid, QuestNode_Root_PollutionRetaliation, QuestNode_Root_RefugeeBetrayal, QuestNode_Root_RefugeeDelayedReward, QuestNode_Root_RefugeePodCrash(_Baby/_Ghoul), QuestNode_Root_RelicHunt, QuestNode_Root_ReliquaryPilgrims, QuestNode_Root_SanguophageMeetingHost, QuestNode_Root_SanguophageShip, QuestNode_Root_ShuttleCrash_Rescue, QuestNode_Root_SightstealerArrival, QuestNode_Root_Site, QuestNode_Root_SurveyScanner, QuestNode_Root_UnnaturalDarkness, QuestNode_Root_VoidAwakening, QuestNode_Root_VoidMonolith, QuestNode_Root_WandererJoin(Abasia/_WalkIn), QuestNode_Root_WorkSite`

**Get*** (49 — read/derive a value or object into the slate): `QuestNode_GetAnimalKindByPoints, GetAnimalToHunt, GetBodySize, GetColonistCountFromColonyPercentage, GetDefaultSitePartsParams, GetDropSpot, GetEventDelays, GetExampleRaid, GetFaction, GetFactionOf, GetFieldValue, GetFreeColonistsCount, GetHediff, GetHivesCountFromPoints, GetLargestClearArea, GetMap, GetMapOf, GetMapWealth, GetMarketValue, GetMonumentRequiredResourcesString, GetMonumentSize, GetMonumentSketch, GetNearbySettlement, GetNearestHomeMapOf, GetPawn, GetPawnCountByPointsWeighted, GetPawnKind, GetPawnKindCombatPower, GetPawnsWithRoyalTitle, GetPlantPlayerCanHarvest, GetPlayerFaction, GetPopIntentForQuest, GetRandomByCurve, GetRandomElement, GetRandomElementByWeight, GetRandomFactionForSite, GetRandomInRangeFloat, GetRandomInRangeForChallengeRating, GetRandomInRangeInt, GetRandomNegativeGameCondition, GetRandomPawnKindForFaction, GetRelationsInfo, GetSameQuestsCount, GetSiteDisturbanceFactor, GetSitePartDefsByTagsAndFaction, GetSiteThreatPoints, GetSiteTile, GetThingPlayerCanProduce, GetWalkInSpot`

**Control flow** (18): `QuestNode_CannotRun, Chance, ChildrenAllowed, Delay, IsNull, IsSet, IsTrue, IsTrueOrUnset, IsZero, LoopCount, Prefix, QuestUnique, RandomNode, Sequence, Set, SetAndRestore, SubScript, Unset`

**Math / logic / lists** (19): `QuestNode_Add, AddRangeToList, AddToList, Clamp, Divide, Equal, EqualOrFail, EvaluateSimpleCurve, Greater, GreaterOrEqual, GreaterOrFail, IsInList, Less, LessOrEqual, LessOrFail, Multiply, MultiplyRange, SplitRandomly, Subtract`

**Signals** (7): `QuestNode_AllSignals, AllSignalsActivable, AnySignal, AnySignalActivable, SendSignals, Signal, SignalActivable`

**Ships/shuttles** (14): `QuestNode_AddContentsToShuttle, AddShipJob(_Arrive/_FlyAway/_Unload/_Wait), ChangeGoodwillForAlivePawnsMissingFromShuttle, GenerateShuttle, GenerateTransportShip, SendShuttleAway(OnCleanup), SendTransportShipAwayOnCleanup, ShuttleDelay, ShuttleLeaveDelay`

**Rewards** (8): `QuestNode_AddItemsReward, AddPassageOffworldReward, AddPawnReward, CampLootReward, GiveRewards, GiveRoyalFavor, GiveRoyalFavorAndDevelopmentPoints, GiveTechprints`

**Pawns** (14): `QuestNode_AnyPawnAlive, ExtraFaction, GeneratePawn, GeneratePawnRandDevelopmentStage, GiveNearPawn, IsFreeWorldPawn, JoinPlayer, Leave, LeaveOnCleanup, LendColonistsToFaction, PawnsArrive, PawnsKilled, RemoveEquipmentFromPawns, SetupCreepjoiner`

**Factions** (11): `QuestNode_ChangeFactionGoodwill, FactionExists, FactionGoodwillForMoodChange, HasRoyalTitleInCurrentFaction, IsFactionHostileToPlayer, IsFactionLeader, IsOfFaction, IsOfRoyalFaction, IsPermanentEnemy, RequireRoyalFavorFromFaction, SetFaction`

**Sites / world objects** (7): `QuestNode_AnyHiddenSitePart, DestroyWorldObject, GenerateSite, GenerateWorldObject, NoWorldObject, SpawnWorldObjects, WorldObjectTimeout`

**Threats / raids** (8): `QuestNode_AssaultColony, GameCondition, GenerateThreats, Infestation, ManhunterPack, MoodBelow, Raid, RandomRaid`

**Text / letters / debug** (11): `QuestNode_InspectString, Letter, Log, Message, ResolveQuestDescription, ResolveQuestName, ResolveTextNow, ResolveTextRequests, RuntimeLog, SlateDump, TextRules`

**Requirements / gating** (9): `QuestNode_ExpansionActive, ModIsActive, RequirementsToAcceptBedroom, RequirementsToAcceptColonistWithTitle, RequirementsToAcceptPlanetLayer, RequirementsToAcceptResearch, SetChallengeRating, SetRoyalTitle, ViolentQuestsAllowed`

**Filter** (3 — only these have a `QuestNode_Filter_*` wrapper; most `QuestPart_Filter_*` subclasses do not): `QuestNode_Filter_AnyColonistAlive, Filter_DecreeNotPossible, Filter_FactionNonPlayer`

**Misc** (53): `QuestNode_AddHediff, AddMemoryThought, AddTag, AllowDecreesForLodger, BetrayMTB, BiocodeWeapons, ChangeHeir, ChangeNeed, CreateIncidents, DamageUntilDowned, DestroyOrPassToWorld(OnCleanup), DisableRandomMoodCausedMentalBreaks, DropMonumentMarkerCopy, DropPods, End, EndGame, EndGame_ShipEscape_FindShipTile, Filter, GenerateMonumentMarker, GenerateNonBuildableMonumentRequiredResources, GenerateThing, GenerateThingSet, GuardianShipDelay, HasGravEngine, Incident, IsAnimal, IsFlesh, IsHumanlike, IsMechanoid, MakeMinified, Notify_PlayerRaidedSomeone, PlantsHarvested, RaceProperty, RecordHistoryEvent, ReleaseParalyzedAnimals, RemoveMemoryThought, ReplaceLostLeaderReferences, RoyalTitleHyperlink, SetAllApparelLocked, SetChildCount, SetItemStashContents, SetTicksUntilAcceptanceExpiry, SituationalThought, SpawnMechCluster, SpawnSkyfaller, ThingsProduced, TrackWhenExitMentalState, TradeRequest_GetRequestedThing, TradeRequest_Initiate, TradeRequest_RandomOfferDuration, VisitColony, WorkDisabled`

### `QuestPart_*` (200 total, namespace `RimWorld` — the runtime objects; only some are directly XML-instantiable)

**Filter** (33 — conditions, abstract `QuestPart_Filter` base; only 3 have `QuestNode_Filter_*` wrappers): `QuestPart_Filter, Filter_AcceptedAfterTicks, Filter_AllPawnsDespawned, Filter_AllPawnsDestroyed, Filter_AllPawnsDowned, Filter_AllRequiredThingsLoaded, Filter_AllThingsHacked, Filter_AllThingsHackedOrDestroyed, Filter_AnyColonistAlive, Filter_AnyColonistCapableOfHacking, Filter_AnyColonistWithCharityPrecept, Filter_AnyHostileThreatToPlayer, Filter_AnyOnTransporter, Filter_AnyOnTransporterCapableOfHacking, Filter_AnyPawn, Filter_AnyPawnAlive, Filter_AnyPawnHasHediff, Filter_AnyPawnInCombatShape, Filter_AnyPawnPlayerControlled, Filter_AnyPawnUnhealthy, Filter_ArgEqual, Filter_ArgNotEqual, Filter_BuiltNearSettlement, Filter_CanAcceptQuest, Filter_FactionHostileToOtherFaction, Filter_FactionNonPlayer, Filter_Fail, Filter_Hacked, Filter_PawnDestroyed, Filter_PlayerWealth, Filter_Success, Filter_ThingAnalyzed, Filter_UnknownOutcome`

**RequirementsToAccept** (13): `QuestPart_RequirementsToAccept, +Bedroom, +ColonistWithTitle, +FactionRelation, +NoDanger, +NoOngoingBestowingCeremony, +PawnOnColonyMap, +PlanetLayer, +PlayerWealth, +Research, +ThingStudied(_ArchotechStructures), +ThroneRoom`

**Pass** (14 — graph-branching/signal fan-out helpers): `QuestPart_Pass, PassActivable, PassAll, PassAllActivable, PassAllOutMany, PassAllSequence, PassAny, PassAnyActivable, PassAnyOutMany, PassOutInterval, PassOutMany, PassOutRandom, PassWhileActive, PassWithFactionArg`

**Ships/shuttles** (13): `QuestPart_AddContentsToShuttle, ExitOnShuttle, FactionGoodwillChange_ShuttleSentThings, RequirePawnsCurrentlyOnShuttle, RequiredShuttleThings, SendShuttleAway(OnCleanup), ShuttleDelay, ShuttleLeaveDelay, TransporterPawns(_Feed/_Tend/_TendWithMedicine)`

**Subquest generators** (4): `QuestPart_SubquestGenerator, _ArchonexusVictory, _Gravcores, _RelicHunt`

**Pawn tracking / state** (31 — the pawn-death/capture-relevant group): `QuestPart_AddHediff, AddMemoryThought, BiocodeWeapons, ChangeNeed, DamageUntilDowned, EnsureNotDowned, EscortPawn, ExtraFaction, GiveDiedOrDownedThoughts, GiveHediff, GiveNearPawn, **IsDead**, **IsPrisoner**, JoinPlayer, Leave, LeavePlayer, LendColonistsToFaction, PawnJoinOffer, PawnKilled, PawnsKilled, RefugeeInteractions, RemoveEquipmentFromPawns, RemoveMemoryThought, ReplaceLostLeaderReferences, ReserveFaction, ReservePawns, SetAllApparelLocked, SituationalThought, TrackWhenExitMentalState, WaitForEscort, WorkDisabled`

**Threats / raids / sites** (14): `QuestPart_AssaultColony, Bossgroup, BossgroupArrives, **DefendPoint**, GameCondition, Infestation, InnerFactionFight, MechCluster, **RandomRaid**, Skyfaller, StartDetectionRaids, StartWick, SurpriseReinforcement, ThreatsGenerator`

**Factions** (8): `QuestPart_FactionGoodwillChange, FactionGoodwillForMoodChange, FactionGoodwillLocked, FactionRelationChange, FactionRelationKind, InvolvedFactions, SetFaction, SetFactionHidden`

**Rewards** (6): `QuestPart_AddQuestRefugeeDelayedReward, DelayedRewardDropPods, GiveRoyalFavor, GiveTechprints, GuardianShipDelay, ReimplantXenogerm`

**World objects / things** (10): `QuestPart_DestroyThingsOrPassToWorld(OnCleanup), DestroyWorldObject, DropPods, MakeLord, NewColony, NoWorldObject, SpawnThing, SpawnWorldObject, WorldObjectTimeout`

**UI/letters/debug** (9): `QuestPart_Alert, CameraJump, DescriptionPart, Dialog, InspectString, Letter, Log, Message, PlayOneShotOnCamera`

**Subquests/end** (6): `QuestPart_AddGiverQuest, AddQuest, AddQuest_RefugeeBetrayal, QuestEnd, QuestEndParent, SetQuestNotYetAccepted`

**Misc** (39): `QuestPart_AllowDecreesForLodger, AssaultThings, AssignMechToMechanitor, BegForItems, BestowingCeremony, Bestowing_TargetChangedTitle, BetrayMTB, BetrayalOffer, ChangeHeir, Choice, Delay, DelayRandom, DisableRandomMoodCausedMentalBreaks, DisableTradeRequest, DropMonumentMarkerCopy, EndGame, GiveToCaravan, Incident, InitiateTradeRequest, LinkUnnaturalCorpse, LookTargets, MTB, MergeOutcomes, MoodBelow, Notify_PlayerRaidedSomeone, PawnsArrive, PawnsAvailable, PlantsHarvested, PlayerWealth, RecordHistoryEvent, ReleaseParalyzedAnimals, SagnuophageMeeting, SetSitePartThreatPointsToCurrent, SpawnSpaceDrone, ThingsProduced, TradeRequestInactive, Venerate, VisitColony, WaitForDurationThenExit`

---

## 4. Concrete mechanics notes

### 4.1 How a quest gets offered
- There is **one** generic `IncidentDef` (`GiveQuest_Random`, `workerClass=IncidentWorker_GiveQuest`,
  no `questScriptDef` pinned) that the storyteller fires on the World target periodically (`baseChance:
  1`, `earliestDay: 2`). Its worker calls `NaturalRandomQuestChooser.ChooseNaturalRandomQuest(points,
  target)`, which weighs every eligible `QuestScriptDef` (must pass `CanRun`, `rootMinPoints`,
  `rootMinProgressScore`, `rootEarliestDay`, layer whitelist/blacklist, etc.) by
  `rootSelectionWeight` (optionally scaled by `rootSelectionWeightFactorFromPointsCurve`) and picks
  one, then calls `QuestScriptDef.Run()` → `QuestGen.Generate(root, slate)`.
- **To add a new naturally-occurring quest, you normally just add a `QuestScriptDef`** with
  `randomlySelectable`/`rootSelectionWeight`/`rootMinPoints` etc. set — no new `IncidentDef` needed.
- A pinned `IncidentDef` (`questScriptDef` set, `baseChance: 0`) is the pattern for a quest that's
  triggered by special code/other systems rather than natural random rolling (e.g.
  `GiveQuest_EndGame_ShipEscape`, "given by a special storyteller comp"). If you need your own trigger
  condition (e.g. "only after the player has X tech"), either gate it purely through
  `QuestScriptDef.rootMinPoints`/`CanRun`/`QuestNode_RequirementsToAccept*` nodes (keeps it inside
  `GiveQuest_Random`'s natural pool), or write your own `IncidentDef` + custom
  `IncidentWorker`/`IncidentWorker_GiveQuest` subclass and call it from your own storyteller/trigger
  code (`QuestUtility.GenerateQuestAndMakeAvailable(root, points)` is the direct call for "just make
  this quest available right now").

### 4.2 How rewards are attached
- `QuestNode_GiveRewards` (XML node) wraps a `RimWorld.RewardsGeneratorParams` struct (`rewardValue`,
  `giverFaction`, `chosenPawnSignal`, `allowGoodwill`, `allowRoyalFavor`, `allowDevelopmentPoints`,
  `thingRewardItemsOnly`, `giveToCaravan`, etc.) passed via its `<parms>` XML block — see
  `OpportunitySite_BanditCamp`'s usage above (`allowGoodwill`, `allowRoyalFavor`, `chosenPawnSignal`,
  `addCampLootReward`, plus a `nodeIfChosenPawnSignalUsed` sub-node to let the player pick which
  colonist personally receives royal favor).
- `Util_GetDefaultRewardValueFromPoints` (Core subscript, `Scripts_Utility_RewardsCore.xml`) is the
  standard way to turn quest `points` (or `sitePoints`/threat points) into a `rewardValue` via a
  `QuestNode_EvaluateSimpleCurve` (curve `200→550 ... 20000→20000`).
- For "give a specific item reward" instead of the generic rewards roll: `QuestNode_GenerateThingSet`
  (pulls from a `ThingSetMakerDef`, e.g. `Reward_GravshipUpgrade`, `Reward_ItemsStandard`) →
  `QuestNode_AddItemsReward` (as in `OrbitalFugitive`/`SurveySite`) or `QuestNode_DropPods` (as in
  `Util_SendItemPods`).
- For "reward is a joining pawn": `Util_JoinerWalkIn` / `Util_JoinerDropIn` subscripts —
  `QuestNode_GeneratePawn`/`GeneratePawnRandDevelopmentStage` → `QuestNode_PawnsArrive` with
  `joinPlayer=true`.

### 4.3 How a quest tracks a specific pawn (death/capture)
Two independent, both-vanilla mechanisms — pick one:

1. **Generic quest-tag + engine signal (pure XML, recommended default)**. Any `Verse.Thing` (pawns
   included) carries `List<string> questTags`. `QuestNode_AddTag` (`targets`, `tag`) appends a
   quest-scoped tag to a thing's `questTags`. Game code elsewhere already calls
   `QuestUtility.SendQuestTargetSignals(thing.questTags, "<Part>", ...)` whenever that thing is
   killed/arrested/etc. — you just listen for `"<tag>.<Part>"` with `QuestNode_Signal` /
   `QuestNode_AnySignal` / `QuestNode_AllSignals`. Relevant `<Part>` constants from `QuestUtility`:
   `Killed`, `Destroyed`, `Despawned`, `Arrested` (captured/imprisoned), `Kidnapped`,
   `ChangedFactionToPlayer`, `Recruited`, `Rescued`, `Banished`, `RanWild`, `Enslaved`. This is exactly
   how `Script_OrbitalFugitive.xml` listens for `fugitive.Destroyed` with zero custom `QuestPart` code.
2. **`QuestPart_IsDead` / `QuestPart_IsPrisoner` (custom-C#-only)**. Both are `QuestPartActivable`
   subclasses with a single `Verse.Pawn pawn` field; once enabled they watch that exact pawn and fire
   `outSignalsCompleted` when `pawn.Dead` (or, for `IsPrisoner`, when the pawn becomes a prisoner)
   becomes true. **Neither has an XML-facing `QuestNode_*` wrapper** — you attach them via
   `QuestGen.quest.AddPart(new QuestPart_IsDead { pawn = targetPawn, inSignalDisable = ...,
   outSignalsCompleted = new List<string> { "TargetDied" } })` inside a custom `QuestNode.RunInt()`.
   Use this only if you need behavior the tag+signal route can't express (e.g. reactivating/disabling
   the watch dynamically via `inSignalEnable`/`inSignalDisable`, which the generic tag mechanism
   doesn't offer).

For "many pawns must die" (not our case, but adjacent): `QuestNode_PawnsKilled` →
`QuestPart_PawnsKilled` (counts kills of a given `race`/`ThingDef` up to `count`, scoped to a
`mapParent`) — this is *not* single-pawn-specific, it's a race/faction/map counter.

### 4.4 How quest success/failure signals work
- Every generated quest gets a fresh, quest-scoped signal namespace; `QuestGen.GenerateNewSignal` and
  `GenerateNewTargetQuestTag` mint unique names so multiple simultaneous quests of the same
  `QuestScriptDef` don't collide (`QuestGenUtility.HardcodedSignalWithQuestID` /
  `HardcodedTargetQuestTagWithQuestID` show the id-suffixing).
- Convention observed in every template read: a slate var that stores an object (e.g. `$site`,
  `$asker`, `$fugitive`) also becomes an implicit **signal-tag prefix** — `site.Destroyed`,
  `site.AllEnemiesDefeated`, `site.MapGenerated`, `fugitive.Destroyed`, `map.MapRemoved`. That is the
  `QuestUtility.SendQuestTargetSignals` mechanism from §4.3 applied to world objects/maps, not just
  pawns (any quest-taggable target: pawn, thing, map, site, ship...).
- `QuestNode_Signal` (single in-signal → run a sub-`node`), `QuestNode_AnySignal`/`QuestNode_AllSignals`
  (wait for any/all of a list of in-signals, `inSignals`), and `QuestNode_SendSignals` (`outSignals` —
  fire your own custom signal names, e.g. `Util_RaidDelayRepeatable`'s `AllRaidsSent`) are the
  XML-composable graph primitives for wiring this up.
- `QuestNode_End` (`outcome`: `Success`/`Fail`/`Unknown`\*, optional `inSignal`,
  `sendStandardLetter`) ends the quest. It can appear multiple times in the same `<root>` graph, gated
  by different `inSignal`s (see `OpportunitySite_BanditCamp`'s two terminal `QuestNode_End`s: one
  wrapped in a `QuestNode_Signal` for the win letter+rewards, one bare with `inSignal` directly for the
  bare `Success` outcome once `site.AllEnemiesDefeated` fires; a third at top level handles
  `site.Destroyed`/timeout → `Fail`). (\* `QuestEndOutcome` enum values weren't separately dumped, but
  `Success`/`Fail` are used verbatim in every XML example above.)
- `QuestPart` runtime objects independently react every tick via `Notify_QuestSignalReceived(Signal
  signal)` (see `QuestPart` base, §2) — this is the override point for any custom `QuestPart` you write
  that needs to react to signals after quest generation is complete (as opposed to `QuestNode`, which
  only runs once at generation time).

---

## 5. Recommended skeletons

### 5.1 "Raid or defend another faction's settlement"

**Raid branch — buildable almost entirely from existing XML nodes**, modeled directly on
`OpportunitySite_BanditCamp` (§1) with the faction/tag swapped to whatever you want raidable (real
`Settlement` faction via `QuestNode_GetNearbySettlement` for flavor text/asker, `Site` for the actual
map):
1. `QuestNode_SubScript(Util_RandomizePointsChallengeRating)` + `Util_AdjustPointsForDistantFight`
2. `QuestNode_GetMap`
3. `QuestNode_GetPawn` (`mustBeFactionLeader`) or `QuestNode_GetNearbySettlement`
   (`storeFactionAs`/`storeFactionLeaderAs`) for the asker/target-faction flavor
4. `QuestNode_GetSiteTile`, `QuestNode_GetSitePartDefsByTagsAndFaction` (your own custom
   `SitePartDef` with a distinguishing `tags` entry, `mustBeHostileToFactionOf`), `QuestNode_
   GetDefaultSitePartsParams`, `QuestNode_GetSiteThreatPoints`
5. `QuestNode_SubScript(Util_GetDefaultRewardValueFromPoints)`
6. `QuestNode_SubScript(Util_GenerateSite)` → `QuestNode_SpawnWorldObjects`
7. `QuestNode_WorldObjectTimeout` (fail on expiry) + `QuestNode_Signal(site.Destroyed → Fail)` +
   `QuestNode_Signal(site.AllEnemiesDefeated → QuestNode_GiveRewards)` + `QuestNode_End`
   (`inSignal=site.AllEnemiesDefeated, outcome=Success`)

**No custom C# required** for the raid branch — everything above is a generic `QuestNode_*`.

**Defend branch — needs one small custom `QuestNode`**, because `QuestPart_DefendPoint` has no XML
wrapper and vanilla's own defend-quest (`SurveySite`) also uses a bespoke node
(`QuestNode_Root_SurveyScanner`). Two viable shapes:

- **(a) Reuse the "waves of raids into a map" pattern with zero custom code**, copying
  `Util_RaidDelayRepeatable`/`Util_Raid` + the `ThreatReward_Raid_MiscReward` ending (§1) verbatim,
  retargeted at the allied settlement's map instead of the player's home map (get the map via
  `QuestNode_GetNearbySettlement` → generate/enter its map, or `QuestNode_Root_Site` +
  `QuestNode_GenerateSite` dressed as "an allied outpost under attack"). This gets you "go to an
  allied base, N waves of enemies arrive over time, win when all raids sent + last raid's lord defeated"
  with **no custom `QuestNode`/`QuestPart` subclass** — only a copy-and-retarget of the existing
  subscripts.
- **(b) Full custom node**, modeled on `QuestNode_Root_SurveyScanner`'s contract (raidChance +
  raidAttackRemainingHoursRange + a duration to survive, win signal `site.SurveyCompleted`-equivalent),
  if you want it to also gate "must be alive/undestroyed the whole time" or other bespoke win/lose
  conditions. Base it on plain `QuestNode` (override `RunInt()`/`TestRunInt(Slate)` only — it has no
  suitable abstract base among the ones inspected; `QuestNode_Root_Mission` is raid-shaped, not
  defend-shaped). Inside `RunInt()`:
  - Read `site`/`map` and `points` off `QuestGen.slate`.
  - `QuestGen.quest.AddPart(new QuestPart_RandomRaid { mapParent = ..., faction = ...,
    pointsRange = ..., inSignal = "<yourStartSignal>" })` (or several, chained via delay signals) to
    fire the wave(s) — `QuestPart_RandomRaid` **does** have field-level parity with the XML
    `QuestNode_RandomRaid` node, so either the generic node or manual `AddPart` works.
  - Optionally `QuestGen.quest.AddPart(new QuestPart_DefendPoint { point = ..., pawns = ...,
    faction = ..., mapParent = ... })` if you want the allied faction's own pawns (not just the
    player) to actively defend a specific point using `LordJob_DefendPoint` — this is the one piece
    with **no** existing XML equivalent, hence must be added by hand here.
  - Fire your own `outSignalsCompleted`/success signal (e.g. via `QuestGen.GenerateNewSignal` +
    `QuestUtility.SendQuestTargetSignals` on cleanup, or a `QuestPart_Filter_Success`-style completion
    check) once the defense duration elapses or all raids are repelled.

### 5.2 "Bounty on a specific named pawn"

Fully composable from existing generic `QuestNode_*` — no custom `QuestNode`/`QuestPart` subclass is
required for the core mechanic, unlike the defend quest:

1. **Pick the asker** (the powerful faction/leader offering the bounty):
   `QuestNode_GetPawn { storeAs: asker, mustBeFactionLeader: true, minTechLevel: Industrial }` (as in
   `OpportunitySite_BanditCamp`/`OrbitalFugitive`), or specifically an Empire-flavored ask by
   restricting `mustBeOfFaction`/`excludeFactionDefs`, or reuse the "royal asker" branching pattern from
   `OrbitalFugitive`'s `questDescriptionRules` (`asker_royalInCurrentFaction`).
2. **Pick the bounty target** — an *existing* named world pawn, not a generated one. There is no
   single vanilla node that means "any notable/named world pawn" directly, so compose:
   `QuestNode_GetPawn { storeAs: target, mustBeWorldPawn: true, mustHaveNoFaction: false,
   allowPermanentEnemyFaction: true/false, excludeFactionDefs: [...], hostileWeight: <>, ... }` —
   tune `GetPawnParms` fields (§2) to bias toward pawns who already have a name/backstory (world pawns
   generated for prior quests/relations qualify automatically; a plain random pawnkind-generated pawn
   generally won't have a unique name, so lean on `mustBeWorldPawn: true` plus `mustBeFactionLeader`
   or `mustHaveRoyalTitleInCurrentFaction` variants to land on someone narratively "somebody," matching
   the spirit of "specific named pawn that exists in the world").
3. **Tag the target for tracking** (pure XML): `QuestNode_AddTag { targets: $target, tag: target }`.
4. **Describe/require accept conditions** as needed: `QuestNode_RequirementsToAcceptResearch`,
   `QuestNode_RequireRoyalFavorFromFaction`, etc.
5. **Reward setup**: `QuestNode_SubScript(Util_GetDefaultRewardValueFromPoints)` →
   `QuestNode_GiveRewards` (or `QuestNode_GenerateThingSet` + `QuestNode_AddItemsReward`/`DropPods` for
   a fixed bounty payout instead of the generic rolled reward).
6. **Win/lose graph** — listen for either outcome with `QuestNode_AnySignal`:
   ```xml
   <li Class="QuestNode_AnySignal">
     <inSignals>
       <li>target.Killed</li>
       <li>target.Arrested</li>
     </inSignals>
     <node Class="QuestNode_Sequence">
       <nodes>
         <li Class="QuestNode_GiveRewards"> ... </li>
         <li Class="QuestNode_End"><outcome>Success</outcome></li>
       </nodes>
     </node>
   </li>
   ```
   ("Arrested" fires when the pawn becomes a prisoner of the player, i.e. "captured"; "Killed" covers
   the kill branch. If you also want to react differently to whether they died vs. were captured — e.g.
   a bonus for delivering them alive — branch on which signal fired inside the sequence via two
   separate `QuestNode_Signal` listeners instead of one `AnySignal`, each ending with its own
   `QuestNode_GiveRewards`/`QuestNode_End`.)
7. **Timeout/expiry**: `QuestNode_WorldObjectTimeout` (if the target is tied to a spawned world
   object/site) or a plain `QuestNode_Delay` + `QuestNode_End(Fail)` keyed off quest acceptance if the
   target simply roams the world map indefinitely and you want a deadline.

**If** you need the bounty to also work while the target is a *hostile combatant on a generated map*
(e.g. spawn them into a defended camp a la `OrbitalFugitive`, rather than "somewhere out in the
world"), reuse `OrbitalFugitive`'s shape instead: `QuestNode_Root_Site` (generate/claim a site) +
a small custom node analogous to `QuestNode_Root_OrbitalFugitive` that spawns the *existing* pawn
(via `Verse.PawnGenerationRequest`/direct placement rather than `QuestGen_Pawns.GeneratePawn`, since
the pawn already exists) into that site's map and tags them — this is the one case where a thin
custom `QuestNode` (just to relocate/spawn an existing world `Pawn` onto a freshly generated map) is
the cleanest option, since no generic `QuestNode_*` "spawn this specific existing pawn onto a new
site's map" node exists (`QuestNode_GeneratePawn`/`GeneratePawnRandDevelopmentStage` only create *new*
pawns).

No custom `QuestPart` subclass is needed in either shape — `QuestPart_IsDead`/`QuestPart_IsPrisoner`
(§2, §4.3) are an alternative to the tag+signal approach but add nothing the generic mechanism doesn't
already give you here, since our target pawn is a `Verse.Thing` like any other and the engine already
fires `Killed`/`Arrested` signals against `questTags` for it automatically.
