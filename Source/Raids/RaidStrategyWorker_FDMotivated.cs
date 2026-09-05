using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace FactionDynamics
{
    /// <summary>
    /// The strategy our motivated raids run on. It exists only so we get to build the raiders'
    /// LordJob ourselves - everything else (pawn generation, arrival, grouping) stays vanilla.
    ///
    /// Its def has a zero selection weight, so the game will never pick it on its own; it is only
    /// ever used when our incident patch deliberately assigns it, with a motivation waiting in
    /// <see cref="FDRaidContext"/>. If it somehow runs without one, it falls back to the vanilla
    /// assault job rather than doing something surprising.
    /// </summary>
    public class RaidStrategyWorker_FDMotivated : RaidStrategyWorker
    {
        protected override LordJob MakeLordJob(IncidentParms parms, Map map, List<Pawn> pawns, int raidSeed)
        {
            FDRaidMotivationDef motivation = FDRaidContext.Motivation;

            if (motivation == null)
            {
                return new LordJob_AssaultColony(parms.faction, parms.canKidnap, parms.canTimeoutOrFlee,
                    false, false, parms.canSteal, false, false);
            }

            FDSimConfig cfg = FactionDynamicsWorldComp.Config;

            int raiderCount = UnityEngine.Mathf.Max(1, pawns?.Count ?? 1);
            float lootGoal = motivation.lootGoalPerRaider * raiderCount;
            float fleeFraction = UnityEngine.Mathf.Clamp(
                motivation.fleeAtLossFraction * cfg.motivationRetreatMult, 0.05f, 0.95f);

            // Rolled here, once, and scribed by the LordJob: CreateGraph() must stay a pure
            // function of scribed state so a mid-raid save/load rebuilds the same graph.
            int timeout = motivation.timeoutTicks.RandomInRange;

            FDLog.Debug("Motivated raid: " + motivation.defName + " with " + raiderCount
                        + " raiders, loot goal " + lootGoal.ToString("F0")
                        + ", flee at " + fleeFraction.ToStringPercent());

            return new LordJob_FDMotivatedRaid(parms.faction, motivation, lootGoal, fleeFraction, timeout);
        }

        public override bool CanUseWith(IncidentParms parms, PawnGroupKindDef groupKind)
        {
            // Only usable as part of a raid we are steering, with a motivation actually chosen.
            // This is what keeps vanilla's own strategy roll off it, since the def has to carry a
            // non-zero selection weight for base.CanUseWith to accept it at all.
            if (!FDRaidContext.Active || FDRaidContext.Motivation == null) return false;
            return base.CanUseWith(parms, groupKind);
        }
    }
}
