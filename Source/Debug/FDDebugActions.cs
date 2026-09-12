using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Test-only overrides consumed by the normal code paths. Nothing here is set outside a dev
    /// action, so release play never touches it.
    /// </summary>
    public static class FDDebug
    {
        /// <summary>Forces the next raid's motivation, bypassing the faction-state requirements.</summary>
        public static FDRaidMotivationDef ForcedMotivation;

        /// <summary>Lets a debug-generated quest skip its module/frequency gate.</summary>
        public static bool BypassQuestGate;
    }

    /// <summary>
    /// Dev-mode tools for exercising every part of the mod without waiting for the storyteller or
    /// for faction state to build up naturally. All of these appear under
    /// "Faction Dynamics" in the debug actions menu.
    ///
    /// These deliberately bypass the normal preconditions (a starvation raid needs a faction with
    /// real hardship; a quest needs to win its selection roll), because the point is to see the
    /// behaviour now.
    ///
    /// Anything that changes simulation state refuses to run in a live Multiplayer session. None of
    /// it goes through Multiplayer's sync layer, so it would mutate the world on one client only and
    /// break the session from that tick on. Read-only tools - the state dump, the loot-search
    /// explainer - stay available, because looking at the world cannot diverge it.
    /// </summary>
    public static class FDDebugActions
    {
        private const string Cat = "Faction Dynamics";

        // ------------------------------------------------------------------ raids

        [DebugAction(Cat, "Raid with motivation...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RaidWithMotivation()
        {
            var options = new List<FDRaidMotivationDef>(DefDatabase<FDRaidMotivationDef>.AllDefsListForReading);
            options.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));

            var entries = new List<FDRaidMotivationDef> { null };
            entries.AddRange(options);

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                entries,
                m => m == null ? "(no motivation - vanilla raid)" : m.defName + "  -  " + m.label,
                m =>
                {
                    if (BlockedInMultiplayer()) return;
                    FDDebug.ForcedMotivation = m;
                    try
                    {
                        FireRaid();
                    }
                    finally
                    {
                        FDDebug.ForcedMotivation = null;
                    }
                });
        }

        [DebugAction(Cat, "Raid from faction...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RaidFromFaction()
        {
            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                HostileCandidates(),
                f => f.Name + "  -  nearest settlement "
                     + FDRaidGeography.DistanceToFaction(f, Find.CurrentMap.Tile).ToString("F0") + " tiles",
                f =>
                {
                    if (BlockedInMultiplayer()) return;
                    FireRaid(f);
                });
        }

        private static void FireRaid(Faction faction = null)
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.forced = true;
            if (faction != null) parms.faction = faction;

            bool fired = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
            FDLog.Toast("Debug raid " + (fired ? "fired" : "FAILED to fire")
                          + " (points " + parms.points.ToString("F0") + ").");
        }

        // ------------------------------------------------------------------ faction mood

        [DebugAction(Cat, "Set faction hardship to max...", allowedGameStates = AllowedGameStates.Playing)]
        private static void SetHardship()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                NonPlayerFactions(),
                f => FactionLabelWithMood(comp, f),
                f =>
                {
                    if (BlockedInMultiplayer()) return;

                    // Hardship lives on the settlements now, and the faction figure is their
                    // average - so set the settlements, or the next lifecycle check would recompute
                    // this straight back down.
                    List<Settlement> settlements = FDWorldUtil.SettlementsOf(f);
                    for (int i = 0; i < settlements.Count; i++)
                    {
                        SettlementRuntimeData sd = comp.GetSettlementData(settlements[i]);
                        sd.hardship = 1f;
                        sd.hardshipInitialized = true;
                    }

                    comp.GetFactionData(f).hardship = 1f;
                    FDLog.Toast(f.Name + " hardship set to 100% across " + settlements.Count
                                  + " settlements - starvation raids are now available from them.");
                });
        }

        // ------------------------------------------------------------------ diplomacy

        /// <summary>
        /// Fire a tribute demand now, from a faction of your choosing.
        ///
        /// Without this the only way to see the letter is to wait for the storyteller comp's 15%
        /// roll to land in a 2500-tick window while some faction happens to be desperate, nearby and
        /// off cooldown. That is the right frequency for play and a useless one for testing.
        ///
        /// This deliberately goes through IncidentWorker_FDTribute.TryExecute rather than building
        /// the letter directly, so the thing being tested is the real path - candidate filtering,
        /// seeded rolls, silver scaling and the history record all included.
        /// </summary>
        [DebugAction(Cat, "Diplomacy: demand tribute now...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceTribute()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            IncidentDef def = FDIncidentDefOf.FD_TributeDemand;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);

            if (!def.Worker.CanFireNow(parms))
            {
                // Say why, rather than failing silently - the answer is almost always "no faction
                // is desperate enough yet", which points straight at the hardship debug action.
                FDLog.Toast("No faction can demand tribute right now. They need hardship >= "
                            + FDTributeTuning.MinHardship.ToStringPercent()
                            + ", grudge <= " + FDTributeTuning.MaxGrudge.ToStringPercent()
                            + ", a settlement within " + FDTributeTuning.MaxTiles
                            + " tiles, peace with you, and no demand in the last "
                            + FDTributeTuning.CooldownDays + " days.");
                return;
            }

            if (!def.Worker.TryExecute(parms))
                FDLog.Toast("Tribute incident declined to fire.");
        }

        [DebugAction(Cat, "Set faction grudge to max...", allowedGameStates = AllowedGameStates.Playing)]
        private static void SetGrudge()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                NonPlayerFactions(),
                f => FactionLabelWithMood(comp, f),
                f =>
                {
                    if (BlockedInMultiplayer()) return;

                    comp.GetFactionData(f).grudge = 1f;
                    FDLog.Toast(f.Name + " grudge set to 100% - revenge raids are now available from them.");
                });
        }

        // ------------------------------------------------------------------ settlements

        [DebugAction(Cat, "Settlements: run lifecycle check now", allowedGameStates = AllowedGameStates.Playing)]
        private static void RunLifecycleNow()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            if (BlockedInMultiplayer()) return;
            int evaluated = SettlementLifecycleWorker.DebugEvaluateAll(comp, Find.TickManager.TicksGame);
            FDLog.Toast("Ran a lifecycle check for " + evaluated
                        + " factions. Turn on verbose logging in mod settings for the details.");
        }

        [DebugAction(Cat, "Settlements: found one for faction...", allowedGameStates = AllowedGameStates.Playing)]
        private static void FoundSettlement()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                NonPlayerFactions(),
                f => f.Name + "  -  " + FDWorldUtil.SettlementsOf(f).Count + " settlements",
                f =>
                {
                    if (BlockedInMultiplayer()) return;
                    int now = Find.TickManager.TicksGame;
                    using (FDRand.Push(f.loadID, now, FDRandSalt.SettlementFound))
                    {
                        SettlementLifecycleActions.TryFoundSettlement(
                            comp, f, comp.GetFactionData(f), FDWorldUtil.SettlementsOf(f), now);
                    }
                });
        }

        [DebugAction(Cat, "Settlements: lose one for faction...", allowedGameStates = AllowedGameStates.Playing)]
        private static void LoseSettlement()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                NonPlayerFactions(),
                f => f.Name + "  -  " + FDWorldUtil.SettlementsOf(f).Count + " settlements",
                f =>
                {
                    if (BlockedInMultiplayer()) return;
                    int now = Find.TickManager.TicksGame;
                    using (FDRand.Push(f.loadID, now, FDRandSalt.SettlementCollapse))
                    {
                        SettlementLifecycleActions.TryRemoveSettlement(
                            comp, f, comp.GetFactionData(f), FDWorldUtil.SettlementsOf(f), now);
                    }
                });
        }

        [DebugAction(Cat, "Settlements: make one regroup...", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceRegroup()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            var settlements = new List<Settlement>();
            List<Settlement> all = Find.WorldObjects.Settlements;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i]?.Faction != null && !all[i].Faction.IsPlayer)
                    settlements.Add(all[i]);
            }
            settlements.Sort((a, b) => a.ID.CompareTo(b.ID));

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                settlements,
                s => s.Label + " (" + s.Faction.Name + ")",
                s =>
                {
                    comp.NotifyRaidSent(s, s.Faction, Find.TickManager.TicksGame);
                    FDLog.Toast(s.Label + " is now regrouping - check its world map inspect pane.");
                });
        }

        // ------------------------------------------------------------------ quests

        [DebugAction(Cat, "Generate FD quest...", allowedGameStates = AllowedGameStates.Playing)]
        private static void GenerateQuest()
        {
            var defNames = new List<string>
            {
                "FD_StrikeRivalSettlement",
                "FD_StrikeRivalOutpost",
                "FD_DefendNeighbourSettlement",
                "FD_HighProfileBounty"
            };

            Dialog_DebugOptionListLister.ShowSimpleDebugMenu(
                defNames,
                s => s,
                defName =>
                {
                    if (BlockedInMultiplayer()) return;

                    QuestScriptDef def = DefDatabase<QuestScriptDef>.GetNamedSilentFail(defName);
                    if (def == null)
                    {
                        FDLog.Error("Quest def " + defName + " is missing - check the Defs folder loaded.");
                        return;
                    }

                    float points = StorytellerUtility.DefaultThreatPointsNow(Find.World);
                    FDDebug.BypassQuestGate = true;
                    try
                    {
                        Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(def, points);
                        if (quest == null)
                        {
                            FDLog.Error(defName + " generated nothing - the quest's requirements could not be met "
                                        + "(no valid asker, settlement or site tile). Try again or on another map.");
                            return;
                        }

                        QuestUtility.SendLetterQuestAvailable(quest, "DebugAction");
                        FDLog.Toast("Generated " + defName + " at " + points.ToString("F0")
                                      + " points - check the Quests tab.");
                    }
                    catch (System.Exception e)
                    {
                        FDLog.Error("Generating " + defName + " threw: " + e);
                    }
                    finally
                    {
                        FDDebug.BypassQuestGate = false;
                    }
                });
        }

        // ------------------------------------------------------------------ inspection

        /// <summary>
        /// Raise a bounty on the selected pawn immediately, skipping the sighting roll. The natural
        /// chance is deliberately low (2% base, 3% for hostiles), which is right for play and
        /// useless for testing the quest itself.
        /// </summary>
        [DebugAction(Cat, "Quests: force bounty on selected pawn", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceBounty()
        {
            var pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null)
            {
                FDLog.Toast("Select exactly one pawn first, then run this again.");
                return;
            }

            if (!QuestNode_FDBountyTarget.IsValidTarget(pawn, Faction.OfPlayerSilentFail))
            {
                FDLog.Toast(pawn.LabelShortCap + " can't carry a bounty (needs to be a named, free, "
                            + "humanlike pawn of a visible non-player faction).");
                return;
            }

            if (BlockedInMultiplayer()) return;
            FDLog.Toast(MapComponent_FDBountyWatcher.DebugForceBounty(pawn)
                ? "Bounty raised on " + pawn.LabelShortCap + "."
                : "Could not raise a bounty on " + pawn.LabelShortCap + " - see the log.");
        }

        /// <summary>
        /// Select one raider, run this, read the log. Reports the pawn's duty, its lord toil, and a
        /// per-filter tally of why every haulable thing on the map was or was not accepted as loot,
        /// ending with the top candidates and whether each is reachable.
        ///
        /// Built after several rounds of "the raid arrives and immediately leaves", which looks
        /// identical whether the colony is empty, a filter is too strict, the search is too short
        /// sighted, or the food is simply unreachable.
        /// </summary>
        [DebugAction(Cat, "Raids: explain loot search (select a raider)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ExplainLootSearch()
        {
            var pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null)
            {
                FDLog.Toast("Select exactly one raider first, then run this again.");
                return;
            }

            // Use whatever category the pawn's own duty is running, falling back to Food.
            FDLootCategory category = FDLootCategory.Food;
            Verse.AI.DutyDef duty = pawn.mindState?.duty?.def;
            if (duty != null && duty.defName == "FD_LootValuables")
                category = FDLootCategory.Valuables;

            FDLog.Message(FDStealUtility.Explain(pawn, category, 9999f));
            FDLog.Toast("Loot search explained in the log for " + pawn.LabelShort + ".");
        }

        [DebugAction(Cat, "Dump mod state to log", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpState()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null)
            {
                FDLog.Message("No world component - is a game loaded?");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Faction Dynamics state ===");
            sb.AppendLine("Config hash: " + FactionDynamicsWorldComp.Config.ConfigHash());

            sb.AppendLine("-- factions --");
            List<int> factionIds = comp.SortedFactionIds;
            for (int i = 0; i < factionIds.Count; i++)
            {
                FactionRuntimeData fd = comp.GetFactionData(factionIds[i], false);
                if (fd == null) continue;
                Faction faction = FactionById(fd.factionId);

                string capital = "none";
                if (faction != null && fd.capitalSettlementId >= 0)
                {
                    List<Settlement> owned = FDWorldUtil.SettlementsOf(faction);
                    for (int j = 0; j < owned.Count; j++)
                    {
                        if (owned[j].ID == fd.capitalSettlementId) { capital = owned[j].Label; break; }
                    }
                }

                sb.AppendLine("  " + (faction != null ? faction.Name : "(gone) " + fd.factionId)
                              + ": hardship " + fd.hardship.ToStringPercent()
                              + ", grudge " + fd.grudge.ToStringPercent()
                              + ", settlements " + (faction != null ? FDWorldUtil.SettlementsOf(faction).Count : 0)
                              + " (baseline " + fd.baselineSettlementCount + ")"
                              + ", capital " + capital
                              // Two halves of the same question, side by side on purpose.
                              //
                              // "want" is what FD's workers compute right now. "engine" is what
                              // GoodwillSituationManager actually has cached, which it refreshes on
                              // a 1000-tick timer. If they disagree the recache simply has not
                              // happened yet; if they still disagree after a few thousand ticks,
                              // the situation defs are not reaching the database and that is a very
                              // different bug. Printing only FD's side would have hidden the
                              // difference entirely.
                              + ", goodwill want(cap " + FDGoodwillSituationWorker_Grudge.CapFor(fd.grudge)
                              + "/drift " + FDGoodwillSituationWorker_Hardship.OffsetFor(fd.hardship) + ")"
                              + (faction != null && faction.HasGoodwill
                                  ? " engine(max " + Find.FactionManager.goodwillSituationManager.GetMaxGoodwill(faction)
                                    + "/natural " + faction.NaturalGoodwill
                                    + "/now " + faction.PlayerGoodwill
                                    + (faction.HostileTo(Faction.OfPlayer) ? " HOSTILE" : "") + ")"
                                  : " engine(n/a)"));
            }

            sb.AppendLine("-- settlements we track --");
            int now = Find.TickManager.TicksGame;

            // State lives on the settlements now, so this walks settlements rather than a key list -
            // and the "(gone) <id>" case it used to print is gone with it, because state cannot
            // outlive the settlement it describes any more.
            List<Settlement> settlements = comp.SortedSettlements();
            int untouched = 0;

            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement settlement = settlements[i];
                SettlementRuntimeData sd = comp.GetSettlementData(settlement, false);
                if (sd == null) continue;

                // Every settlement has state now that it lives on the settlement, so "tracked" no
                // longer means "has an entry" - it means "something has actually happened here".
                // Without this the dump printed several hundred identical lines of zeroes and buried
                // the handful that mattered.
                if (!IsWorthReporting(sd, now)) { untouched++; continue; }

                sb.AppendLine("  " + settlement.Label
                              + (FDCapitals.IsCapital(settlement) ? " [CAPITAL]" : "")
                              + ": hardship " + sd.hardship.ToStringPercent()
                              + ", strength " + sd.strengthFactor.ToString("F2")
                              + ", raids sent " + sd.raidsSent
                              + (sd.IsRegrouping(now) ? ", REGROUPING " + sd.RegroupDaysLeft(now) + "d" : "")
                              + ", origin " + sd.origin);
            }

            // Report the remainder as a count rather than dropping it silently: "24 of 490" is the
            // number that tells you the injector attached to everything, and a sudden 0 there would
            // mean the comp stopped being created at all.
            sb.AppendLine("  (" + untouched + " further settlement(s) with nothing to report)");

            // This one belongs in the log - writing it there is the whole point of the action.
            FDLog.Message(sb.ToString());
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Whether a settlement's state is worth a line in the dump.
        ///
        /// Before the comp refactor, having an entry in the dictionary *was* the signal - the mod
        /// only created one when it touched a settlement. Now every settlement carries a comp from
        /// the moment it is created, so presence means nothing and the question has to be asked of
        /// the data: has hardship been seeded, has strength moved off 1, has it sent a raid, did we
        /// create it, is it regrouping right now. Anything else is a default object.
        /// </summary>
        private static bool IsWorthReporting(SettlementRuntimeData sd, int now)
        {
            if (sd == null) return false;

            return sd.hardshipInitialized
                   || sd.raidsSent > 0
                   || sd.origin != FDSettlementOrigin.Preexisting
                   || sd.IsRegrouping(now)
                   // Float compare against the default rather than an epsilon band: strengthFactor is
                   // only ever assigned, never accumulated, so it is exactly 1f until something sets it.
                   || sd.strengthFactor != 1f;
        }

        private static Faction FactionById(int loadId)
        {
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].loadID == loadId)
                    return all[i];
            }
            return null;
        }

        private static List<Faction> NonPlayerFactions()
        {
            var result = new List<Faction>();
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && !all[i].IsPlayer && !all[i].Hidden)
                    result.Add(all[i]);
            }
            result.Sort((a, b) => a.loadID.CompareTo(b.loadID));
            return result;
        }

        private static List<Faction> HostileCandidates()
        {
            var result = new List<Faction>();
            List<Faction> all = NonPlayerFactions();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].HostileTo(Faction.OfPlayer))
                    result.Add(all[i]);
            }
            return result;
        }

        private static string FactionLabelWithMood(FactionDynamicsWorldComp comp, Faction f)
        {
            FactionRuntimeData fd = comp.GetFactionData(f, false);
            float hardship = fd?.hardship ?? 0f;
            float grudge = fd?.grudge ?? 0f;
            return f.Name + "  -  hardship " + hardship.ToStringPercent() + ", grudge " + grudge.ToStringPercent();
        }

        private static void WarnIfMultiplayer()
        {
            if (MultiplayerCompat.InMultiplayer)
                FDLog.Warning("Dev action used in a Multiplayer session - this runs locally only and will desync.");
        }

        /// <summary>
        /// True when a dev action that CHANGES simulation state must not run.
        ///
        /// Warning about the desync after the fact was never much use: none of these actions are
        /// routed through Multiplayer's sync layer, so they mutate the world on one client only and
        /// the session is broken from that tick onward - no amount of log text undoes it. Read-only
        /// actions (state dumps, the loot-search explainer) are unaffected and stay available.
        /// </summary>
        private static bool BlockedInMultiplayer()
        {
            if (!MultiplayerCompat.InMultiplayer) return false;

            FDLog.Toast("That dev action changes world state and is disabled in a Multiplayer "
                        + "session - running it on one client only would desync the game.");
            return true;
        }
    }
}
