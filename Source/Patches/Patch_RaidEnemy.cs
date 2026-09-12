using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// M2 + M3 hook into the vanilla raid pipeline.
    ///
    /// Why Harmony postfixes on IncidentWorker_RaidEnemy rather than swapping the IncidentDef's
    /// workerClass to a subclass of ours: other popular raid mods swap that same workerClass, and
    /// whoever loads last would silently win, throwing away the other mod's behaviour entirely.
    /// Postfixes on the vanilla methods still run for any subclass that calls base, so this
    /// composes with those mods instead of fighting them.
    ///
    /// Three rules learned from the first round of live logs:
    ///  1. The raiding faction is resolved *inside* TryExecuteWorker, so nothing that depends on
    ///     the faction may be computed in a prefix on it. All of that goes through
    ///     FDRaidContext.EnsureFor, called from every hook that might run first.
    ///  2. Never override a faction the caller deliberately set - dev tools, quests and other mods
    ///     pin parms.faction on purpose, and re-picking it breaks them. We only re-weight when
    ///     vanilla was choosing freely.
    ///  3. Only ever pick a faction that can actually field a raid. Vanilla's own candidate list
    ///     plus a CanGenerateAnyNormalGroup check, or you end up sending insects to do a pirate's
    ///     job and the raid fails to generate at all.
    ///
    /// Everything here is additive: we never skip the original, only adjust the parms vanilla
    /// produced. With no world component (world generation, main menu) every patch no-ops.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy))]
    public static class Patch_RaidEnemy
    {
        /// <summary>Vanilla's own "which factions could send this raid" list. Protected, so reflected once.</summary>
        private static readonly MethodInfo CandidateFactionsMethod =
            AccessTools.Method(typeof(IncidentWorker_Raid), "CandidateFactions",
                new[] { typeof(IncidentParms), typeof(bool) });

        // ---------------------------------------------------------------- faction selection

        /// <summary>Remembers whether the caller had already chosen a faction before vanilla ran.</summary>
        [HarmonyPrefix]
        [HarmonyPatch("TryResolveRaidFaction")]
        public static void TryResolveRaidFaction_Prefix(IncidentParms parms, out bool __state)
        {
            __state = parms?.faction != null;
        }

        /// <summary>
        /// Re-picks the raiding faction weighted by how close its settlements are to the target,
        /// but only when vanilla was free to choose. Then locks in the raid's origin and motivation,
        /// now that the faction is final.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("TryResolveRaidFaction")]
        public static void TryResolveRaidFaction_Postfix(IncidentWorker_RaidEnemy __instance,
            IncidentParms parms, bool __result, bool __state)
        {
            if (!__result || parms == null) return;

            if (!__state)
                TryRepickByProximity(__instance, parms);

            FDRaidContext.EnsureFor(parms);
        }

        private static void TryRepickByProximity(IncidentWorker_RaidEnemy worker, IncidentParms parms)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            if (!cfg.raidGeography) return;
            if (cfg.raidProximityBias <= 0f) return;
            if (FactionDynamicsWorldComp.Current == null) return;
            if (parms.target == null) return;

            PlanetTile targetTile = parms.target.Tile;
            if (!targetTile.Valid) return;

            List<Faction> candidates = GetCandidates(worker, parms);
            if (candidates.Count <= 1) return;

            var weights = new List<float>(candidates.Count);
            float totalWeight = 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                float dist = FDRaidGeography.DistanceToFaction(candidates[i], targetTile);
                float weight = FDRaidGeography.FactionWeight(candidates[i], dist);
                weights.Add(weight);
                totalWeight += weight;
            }

            if (totalWeight <= 0f) return;

            float roll = Rand.Value * totalWeight;
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= weights[i];
                if (roll > 0f) continue;

                if (parms.faction != candidates[i])
                {
                    FDLog.Debug("Raid faction re-picked by proximity: " + candidates[i].Name
                                + " (vanilla chose " + (parms.faction?.Name ?? "none") + ")");
                    parms.faction = candidates[i];
                }
                return;
            }
        }

        /// <summary>
        /// Vanilla's candidate factions for this raid, filtered to those that can actually field a
        /// group at these points, in a deterministic order. Falls back to a hand-rolled filter if
        /// the protected method ever moves.
        /// </summary>
        private static List<Faction> GetCandidates(IncidentWorker_RaidEnemy worker, IncidentParms parms)
        {
            var result = new List<Faction>();

            IEnumerable<Faction> source = null;
            if (CandidateFactionsMethod != null)
            {
                try
                {
                    source = CandidateFactionsMethod.Invoke(worker, new object[] { parms, false })
                        as IEnumerable<Faction>;
                }
                catch (System.Exception e)
                {
                    FDLog.Warning("Could not read vanilla's raid candidate factions, falling back: " + e.Message);
                    source = null;
                }
            }

            if (source == null)
            {
                var fallback = new List<Faction>();
                List<Faction> all = Find.FactionManager.AllFactionsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    Faction f = all[i];
                    if (f == null || f.IsPlayer) continue;
                    if (worker.FactionCanBeGroupSource(f, parms, false))
                        fallback.Add(f);
                }
                source = fallback;
            }

            foreach (Faction f in source)
            {
                if (f == null || f.IsPlayer) continue;

                // The bug this guards against: a faction can pass the "could raid" checks and still
                // have no pawn group makers for a combat group, which makes the raid fail outright.
                if (!PawnGroupMakerUtility.CanGenerateAnyNormalGroup(f, parms.points)) continue;

                // Only factions that actually live somewhere take part in a *proximity* re-pick.
                // Settlement-less factions (mechanoids, Ancients) have no geography to weigh, and
                // pulling them into this pool let them win rolls they should never have been in -
                // an Ancients "raid" of one pawn from nowhere. Vanilla can still choose them.
                if (FDWorldUtil.SettlementsOf(f).Count == 0) continue;

                result.Add(f);
            }

            // Deterministic order regardless of how the candidate list was built.
            result.Sort((a, b) => a.loadID.CompareTo(b.loadID));
            return result;
        }

        // ---------------------------------------------------------------- points, strategy, arrival

        /// <summary>Scales the raid by distance and motivation, after vanilla has set its points.</summary>
        [HarmonyPostfix]
        [HarmonyPatch("ResolveRaidPoints")]
        public static void ResolveRaidPoints_Postfix(IncidentParms parms)
        {
            FDRaidContext.EnsureFor(parms);
            FDRaidContext.ApplyPointsScaling(parms);
        }

        /// <summary>Assigns our motivated strategy when this raid has a motivation.</summary>
        [HarmonyPostfix]
        [HarmonyPatch("ResolveRaidStrategy")]
        public static void ResolveRaidStrategy_Postfix(IncidentParms parms, PawnGroupKindDef groupKind)
        {
            if (parms == null) return;

            // In case points were resolved before the faction was known.
            FDRaidContext.EnsureFor(parms);
            FDRaidContext.ApplyPointsScaling(parms);

            if (FDRaidContext.Motivation == null) return;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return;

            RaidStrategyDef ours = FDRaidStrategyDefOf.FD_MotivatedAssault;
            if (ours?.Worker == null) return;

            if (ours.Worker.CanUseWith(parms, groupKind))
            {
                parms.raidStrategy = ours;
            }
            else
            {
                FDLog.Debug("Motivated strategy unusable for " + parms.faction?.Name
                            + " at " + parms.points.ToString("F0") + " points (groupKind "
                            + (groupKind?.defName ?? "null") + "); using vanilla behaviour.");
                FDRaidContext.DropMotivation();
            }
        }

        // ---------------------------------------------------------------- aftermath

        /// <summary>
        /// After the raid actually fired: weaken the settlement that sent it, and remember that this
        /// faction just attacked. Always clears the context, success or not.
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch("TryExecuteWorker")]
        public static void TryExecuteWorker_Finalizer(IncidentParms parms, bool __result)
        {
            try
            {
                if (!__result) return;

                FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
                if (comp == null) return;
                if (!FactionDynamicsWorldComp.Config.settlements) return;

                comp.NotifyRaidSent(FDRaidContext.Origin, parms?.faction, Find.TickManager.TicksGame);
            }
            finally
            {
                // Only the raid that owns the context may tear it down. A nested raid finishing
                // first must not clear the outer raid's state out from under it.
                if (FDRaidContext.IsForParms(parms))
                    FDRaidContext.Clear();
            }
        }

        /// <summary>
        /// Starts every raid from a clean slate - unless another raid is already mid-execution.
        ///
        /// Clearing unconditionally was the other half of the nested-raid problem: an incident fired
        /// from inside a raid would wipe the outer raid's context on its way in, and the outer raid
        /// would finish building its LordJob from nothing.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("TryExecuteWorker")]
        public static void TryExecuteWorker_Prefix(IncidentParms parms)
        {
            if (FDRaidContext.Active && !FDRaidContext.IsForParms(parms)) return;
            FDRaidContext.Clear();
        }
    }

    /// <summary>
    /// Makes the motivation visible on the raiders themselves. A starvation raid should arrive with
    /// empty bellies and real malnutrition, not just a line of letter text - which also drags their
    /// mood down and makes them break sooner, exactly as a desperate raid should.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_Raid), "PostProcessSpawnedPawns")]
    public static class Patch_RaidPawnCondition
    {
        [HarmonyPostfix]
        public static void Postfix(IncidentParms parms, List<Pawn> pawns)
        {
            if (pawns == null) return;

            // This postfix is on IncidentWorker_Raid, which is where PostProcessSpawnedPawns is
            // declared - IncidentWorker_RaidEnemy does not override it, and IncidentWorker_RaidFriendly
            // overrides neither it nor TryExecuteWorker. So this runs for ALLY arrivals, and for any
            // raid-like incident another mod builds on IncidentWorker_Raid, not just for our raids.
            //
            // The prefix/finalizer that bracket the context are on IncidentWorker_RaidEnemy's OWN
            // TryExecuteWorker override, so neither of them runs for those other callers. Asking the
            // context whether it actually belongs to this incident is what keeps a relief force from
            // arriving with the malnutrition of whatever raid last set it.
            if (!FDRaidContext.IsFor(parms)) return;

            // Belt and braces: a motivation only ever describes people attacking us.
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null || !parms.faction.HostileTo(player)) return;

            FDRaidMotivationDef motivation = FDRaidContext.Motivation;
            if (motivation == null) return;
            if (motivation.pawnHediff == null && motivation.pawnFoodLevel.min < 0f) return;

            // How badly off the settlement that sent them actually is. This is the origin's own
            // figure, not the faction average - the town that emptied its stores is the town whose
            // people show up starving.
            float hardship = 0f;
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp != null)
                hardship = comp.HardshipFor(parms.faction, FDRaidContext.Origin);

            int affected = 0;
            bool collapsedUsed = false;
            float worst = 0f;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.Dead || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) continue;

                if (motivation.pawnFoodLevel.min >= 0f && pawn.needs?.food != null)
                    pawn.needs.food.CurLevelPercentage = motivation.pawnFoodLevel.RandomInRange;

                if (motivation.pawnHediff != null)
                {
                    float severity;

                    // At most one per raid: the person they should not have brought.
                    if (!collapsedUsed && motivation.collapsedRaiderChance > 0f
                        && Rand.Chance(motivation.collapsedRaiderChance))
                    {
                        severity = motivation.collapsedRaiderSeverity.RandomInRange;
                        collapsedUsed = true;
                    }
                    else
                    {
                        severity = SeverityFor(motivation, hardship);
                    }

                    HealthUtility.AdjustSeverity(pawn, motivation.pawnHediff, severity);
                    if (severity > worst) worst = severity;
                }

                affected++;
            }

            if (affected > 0)
            {
                FDLog.Debug("Applied " + motivation.defName + " condition to " + affected + " raiders"
                            + (motivation.pawnHediff != null
                                ? " (" + motivation.pawnHediff.defName
                                  + ", origin hardship " + hardship.ToStringPercent()
                                  + ", worst severity " + worst.ToString("F2")
                                  + (collapsedUsed ? ", one collapsed" : "") + ")"
                                : "") + ".");
            }
        }

        private static float SeverityFor(FDRaidMotivationDef motivation, float hardship)
        {
            return FDSeverity.ForHardship(motivation, hardship);
        }
    }

    /// <summary>
    /// Empties the raiders' packs for motivations that say they arrive with nothing.
    ///
    /// This hangs off GenerateRaidLoot rather than PostProcessSpawnedPawns, and the ordering is the
    /// entire point. In IncidentWorker_Raid.TryGenerateRaidInfo the calls run:
    ///
    ///     PostProcessSpawnedPawns(parms, pawns);      // where the malnutrition is applied
    ///     ...
    ///     GenerateRaidLoot(parms, points, pawns);     // ThingSetMaker + RaidLootDistributor
    ///
    /// so vanilla stuffs the raiders' inventories AFTER the post-process hook. Stripping there
    /// emptied packs that were then immediately refilled, which is why starving raiders kept
    /// turning up with packaged survival meals even with arriveWithoutFood set - the strip was
    /// running and reporting nothing, because at that moment there genuinely was nothing.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GenerateRaidLoot")]
    public static class Patch_RaidStripFood
    {
        [HarmonyPostfix]
        public static void Postfix(List<Pawn> pawns)
        {
            FDRaidMotivationDef motivation = FDRaidContext.Motivation;
            if (motivation == null || !motivation.arriveWithoutFood || pawns == null) return;

            int stripped = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.Dead) continue;
                stripped += StripFood(pawn);
            }

            if (stripped > 0)
                FDLog.Debug("Removed " + stripped + " food stacks from " + motivation.defName + " raiders.");
        }

        /// <summary>
        /// Empties a raider's pack of anything edible. Returns how many stacks went.
        ///
        /// Deterministic for Multiplayer: fixed reverse-index iteration, no RNG. Runs inside the
        /// incident, which every client executes identically.
        /// </summary>
        private static int StripFood(Pawn pawn)
        {
            ThingOwner<Thing> inv = pawn.inventory?.innerContainer;
            if (inv == null) return 0;

            int removed = 0;
            for (int i = inv.Count - 1; i >= 0; i--)
            {
                Thing t = inv[i];
                if (t?.def == null || !t.def.IsNutritionGivingIngestible) continue;

                // Drugs are not rations - a starving band can still be carrying go-juice, and
                // JobGiver_TakeCombatEnhancingDrug in the loot duty expects to find it.
                if (t.def.IsDrug) continue;

                // Destroy-then-Remove, matching ThingOwner.ClearAndDestroyContents. Doing it the
                // other way round drops the thing out of the container before the owner is
                // notified of the destruction.
                t.Destroy(DestroyMode.Vanish);
                inv.Remove(t);
                removed++;
            }

            return removed;
        }
    }

    /// <summary>
    /// Arrival mode by distance. Separate patch class because this method lives on
    /// IncidentWorker_Raid, not on IncidentWorker_RaidEnemy.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.ResolveRaidArriveMode))]
    public static class Patch_RaidArriveMode
    {
        [HarmonyPostfix]
        public static void Postfix(IncidentParms parms)
        {
            if (parms == null) return;
            if (!FactionDynamicsWorldComp.Config.raidArrivalByDistance) return;

            FDRaidContext.EnsureFor(parms);
            if (!FDRaidContext.Active) return;
            if (FDRaidContext.OriginDistance >= FDWorldUtil.UnreachableDistance) return;

            PawnsArrivalModeDef mode = FDRaidGeography.PickArrivalMode(FDRaidContext.OriginDistance, parms);
            if (mode != null && mode != parms.raidArrivalMode)
            {
                FDLog.Debug("Arrival mode by distance: " + (parms.raidArrivalMode?.defName ?? "none")
                            + " -> " + mode.defName);
                parms.raidArrivalMode = mode;
            }
        }
    }
}
