using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Makes the world map's simulation mean something when the PLAYER is the attacker.
    ///
    /// Until now hardship and regrouping were entirely outbound: they shaped raids coming at the
    /// colony and nothing else. Walk a caravan up to a settlement the mod had spent a season
    /// describing as half-starved and beaten down, and you fought a garrison generated from
    /// vanilla's numbers alone - identical to a thriving one. The mod told the player a settlement
    /// was on its knees and then quietly contradicted itself.
    ///
    /// Two effects, deliberately mirroring what the same state does to their raids:
    ///
    ///   REGROUPING - fewer defenders, by the same regroupStrengthPenalty (45% by default) that
    ///   shrinks a raid launched from a regrouping settlement. Their people are away or hurt; the
    ///   ones left holding the place are thinner on the ground.
    ///
    ///   HARDSHIP - the defenders are hungry, with exactly the malnutrition and food level a raider
    ///   from this settlement would have arrived with. It runs through the same
    ///   FDRaidMotivationDef the starvation raid uses, so the two can never drift apart: change the
    ///   def and both the raid and the garrison change together.
    ///
    /// Hooked on MapGenerator.GenerateMap rather than the settlement gen steps or symbol resolvers
    /// on purpose. By the time this runs every gen step has finished and the defenders exist, so it
    /// does not matter which map generator, symbol resolver or mod produced them - anything hostile
    /// standing on a tracked settlement's map is a defender. Reaching into
    /// SymbolResolver_Settlement's ResolveParams instead would have bound this to internals that
    /// differ between vanilla settlements, quest outposts and modded bases.
    /// </summary>
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    public static class Patch_SettlementDefenders
    {
        /// <summary>Never strip a settlement below this many defenders - an empty base is not a raid.</summary>
        private const int MinDefenders = 2;

        [HarmonyPostfix]
        public static void Postfix(MapParent parent, Map __result)
        {
            if (__result == null) return;
            if (!(parent is Settlement settlement)) return;
            if (settlement.Faction == null || settlement.Faction.IsPlayer) return;
            if (!FactionDynamicsWorldComp.Config.settlements) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            SettlementRuntimeData sd = comp.GetSettlementData(settlement, false);
            if (sd == null) return;

            // Never touch a settlement a quest is built around. Thinning the garrison here means
            // Destroy(Vanish), which takes the pawn's inventory with them - so a quest that asked
            // the player to rescue someone, or that put an item on a defender, can be quietly made
            // unwinnable at map-gen with nothing in the log to explain it.
            if (FDWorldUtil.QuestDependsOn(settlement))
            {
                FDLog.Debug("Leaving " + settlement.Label + "'s garrison alone - a live quest points at it.");
                return;
            }

            int now = Find.TickManager.TicksGame;

            List<Pawn> defenders = DefendersOn(__result, settlement.Faction);
            if (defenders.Count == 0) return;

            int removed = sd.IsRegrouping(now) ? ThinGarrison(defenders, settlement, now) : 0;
            float hardship = comp.HardshipFor(settlement.Faction, settlement);
            int starved = ApplyHardship(defenders, hardship);

            FDLog.Debug("Defending " + settlement.Label + ": " + defenders.Count + " left"
                        + (removed > 0 ? " (" + removed + " away regrouping)" : "")
                        + ", hardship " + hardship.ToStringPercent()
                        + (starved > 0 ? " applied to " + starved + " defenders" : ""));
        }

        /// <summary>
        /// Everyone on the map who belongs to the settlement's faction and is up and about.
        /// Sorted by ID so every Multiplayer client sees the same list in the same order.
        /// </summary>
        private static List<Pawn> DefendersOn(Map map, Faction faction)
        {
            var result = new List<Pawn>();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;

            for (int i = 0; i < all.Count; i++)
            {
                Pawn pawn = all[i];
                if (pawn == null || pawn.Dead || pawn.Faction != faction) continue;
                if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike) continue;

                // A pawn a quest is tracking is not a nameless garrison body, whatever faction tag
                // it is wearing. Vanishing one breaks the quest silently, so these are never even
                // candidates for removal.
                if (pawn.questTags != null && pawn.questTags.Count > 0) continue;
                if (pawn.IsQuestLodger()) continue;

                result.Add(pawn);
            }

            result.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            return result;
        }

        /// <summary>
        /// Removes the share of the garrison that is away or too hurt to stand, using the same
        /// penalty that shrinks a raid this settlement launches. Removes from the END of the
        /// ID-sorted list, which is deterministic and needs no RNG at all.
        /// </summary>
        private static int ThinGarrison(List<Pawn> defenders, Settlement settlement, int now)
        {
            float penalty = UnityEngine.Mathf.Clamp01(FactionDynamicsWorldComp.Config.regroupStrengthPenalty);
            if (penalty <= 0f) return 0;

            int target = UnityEngine.Mathf.RoundToInt(defenders.Count * (1f - penalty));
            if (target < MinDefenders) target = MinDefenders;
            if (target >= defenders.Count) return 0;

            int removed = 0;
            for (int i = defenders.Count - 1; i >= target; i--)
            {
                Pawn pawn = defenders[i];
                defenders.RemoveAt(i);

                // Vanish rather than kill: these people were never here, so no corpse, no blood,
                // and nothing for the player to loot off a body that should not exist.
                if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
                removed++;
            }

            return removed;
        }

        /// <summary>
        /// Gives the defenders the same hunger a raider from this settlement would arrive with.
        ///
        /// Reads the starvation motivation's own def so the numbers cannot drift from the raid
        /// version - same hediff, same severity envelope, same hardship weighting, same food level.
        /// If the motivation is missing or its module is off, nothing happens.
        /// </summary>
        private static int ApplyHardship(List<Pawn> defenders, float hardship)
        {
            if (hardship <= 0f) return 0;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return 0;

            FDRaidMotivationDef starvation =
                DefDatabase<FDRaidMotivationDef>.GetNamedSilentFail("FD_Starvation");
            if (starvation == null || starvation.pawnHediff == null) return 0;

            // Below the threshold at which the faction would send a starvation raid, they are
            // hungry but not visibly starving - leave the garrison alone.
            if (hardship < starvation.minHardship) return 0;

            int affected = 0;
            for (int i = 0; i < defenders.Count; i++)
            {
                Pawn pawn = defenders[i];
                if (pawn == null || pawn.Dead) continue;

                if (starvation.pawnFoodLevel.min >= 0f && pawn.needs?.food != null)
                    pawn.needs.food.CurLevelPercentage = starvation.pawnFoodLevel.RandomInRange;

                HealthUtility.AdjustSeverity(pawn, starvation.pawnHediff,
                    FDSeverity.ForHardship(starvation, hardship));
                affected++;
            }

            return affected;
        }
    }
}
