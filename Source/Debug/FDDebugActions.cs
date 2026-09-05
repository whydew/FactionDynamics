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
    /// behaviour now. In a Multiplayer session they are unsynced local actions like any other dev
    /// tool, and will desync a live session - the log says so when you use one.
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
                    WarnIfMultiplayer();
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
                    WarnIfMultiplayer();
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
            FDLog.Message("Debug raid " + (fired ? "fired" : "FAILED to fire")
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
                    FDLog.Message(f.Name + " hardship set to 100% across " + settlements.Count
                                  + " settlements - starvation raids are now available from them.");
                });
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
                    comp.GetFactionData(f).grudge = 1f;
                    FDLog.Message(f.Name + " grudge set to 100% - revenge raids are now available from them.");
                });
        }

        // ------------------------------------------------------------------ settlements

        [DebugAction(Cat, "Settlements: run lifecycle check now", allowedGameStates = AllowedGameStates.Playing)]
        private static void RunLifecycleNow()
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            WarnIfMultiplayer();
            int evaluated = SettlementLifecycleWorker.DebugEvaluateAll(comp, Find.TickManager.TicksGame);
            FDLog.Message("Ran a lifecycle check for " + evaluated + " factions - see the lines above for what happened.");
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
                    WarnIfMultiplayer();
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
                    WarnIfMultiplayer();
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
                    FDLog.Message(s.Label + " is now regrouping - check its world map inspect pane.");
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
                    WarnIfMultiplayer();

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
                        FDLog.Message("Generated " + defName + " at " + points.ToString("F0")
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
                              + ", capital " + capital);
            }

            sb.AppendLine("-- settlements we track --");
            int now = Find.TickManager.TicksGame;
            List<int> settlementIds = comp.SortedSettlementIds;
            for (int i = 0; i < settlementIds.Count; i++)
            {
                SettlementRuntimeData sd = comp.GetSettlementData(settlementIds[i], false);
                if (sd == null) continue;

                Settlement settlement = null;
                List<Settlement> all = Find.WorldObjects.Settlements;
                for (int j = 0; j < all.Count; j++)
                {
                    if (all[j].ID == sd.settlementId) { settlement = all[j]; break; }
                }

                sb.AppendLine("  " + (settlement != null ? settlement.Label : "(gone) " + sd.settlementId)
                              + (settlement != null && FDCapitals.IsCapital(settlement) ? " [CAPITAL]" : "")
                              + ": hardship " + sd.hardship.ToStringPercent()
                              + ", strength " + sd.strengthFactor.ToString("F2")
                              + ", raids sent " + sd.raidsSent
                              + (sd.IsRegrouping(now) ? ", REGROUPING " + sd.RegroupDaysLeft(now) + "d" : "")
                              + ", origin " + sd.origin);
            }

            FDLog.Message(sb.ToString());
        }

        // ------------------------------------------------------------------ helpers

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
    }
}
