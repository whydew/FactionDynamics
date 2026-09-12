using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// M1 - Dynamic settlements.
    ///
    /// Once every <see cref="FDSimConfig.settlementCheckIntervalDays"/> days, each eligible NPC
    /// faction gets one deterministic evaluation: it may found a settlement, lose one, or have one
    /// grow stronger. Rolls are seeded from (faction id, day, salt) so every Multiplayer client
    /// reaches the same answer from the same tick without any network traffic.
    ///
    /// Safety rails, in order of importance:
    ///  - never touch a settlement that has a map, a caravan heading to it, or an active quest;
    ///  - never take a faction below the configured minimum settlement count;
    ///  - never grow a faction past its starting count x the configured factor;
    ///  - never touch player factions or hidden/permanent-enemy special factions.
    /// </summary>
    public static class SettlementLifecycleWorker
    {
        /// <summary>Only evaluate on this tick boundary; the per-faction interval is checked inside.</summary>
        private const int EvaluationCadenceTicks = 2500;

        public static void Tick(FactionDynamicsWorldComp comp, int now)
        {
            if (now % EvaluationCadenceTicks != 0) return;

            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            int intervalTicks = UnityEngine.Mathf.Max(
                EvaluationCadenceTicks,
                UnityEngine.Mathf.RoundToInt(cfg.settlementCheckIntervalDays * GenDate.TicksPerDay));

            List<Faction> factions = Find.FactionManager.AllFactionsListForReading;

            // Deterministic order: FactionManager's list order is the load order, identical on
            // every client, but sort by loadID anyway so nothing here depends on that assumption.
            var eligible = new List<Faction>();
            for (int i = 0; i < factions.Count; i++)
            {
                if (IsEligible(factions[i]))
                    eligible.Add(factions[i]);
            }
            eligible.Sort((a, b) => a.loadID.CompareTo(b.loadID));

            for (int i = 0; i < eligible.Count; i++)
            {
                Faction faction = eligible[i];
                FactionRuntimeData data = comp.GetFactionData(faction);

                if (data.lastLifecycleTick > 0 && now - data.lastLifecycleTick < intervalTicks)
                    continue;

                // Stagger factions so they don't all evaluate on the same tick after a load.
                if (data.lastLifecycleTick < 0)
                {
                    data.lastLifecycleTick = now - (faction.loadID % intervalTicks);

                    // Designate the seat straight away rather than making the player wait out a
                    // whole lifecycle interval before the world map shows them anything.
                    FDCapitals.EnsureCapital(comp, faction, SettlementsOf(faction), false);
                    continue;
                }

                data.lastLifecycleTick = now;
                EvaluateFaction(comp, faction, data, cfg, now);
            }
        }

        /// <summary>
        /// Dev action: runs one lifecycle evaluation for every eligible faction right now, ignoring
        /// the normal interval. Returns how many factions were evaluated.
        /// </summary>
        public static int DebugEvaluateAll(FactionDynamicsWorldComp comp, int now)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            List<Faction> factions = Find.FactionManager.AllFactionsListForReading;

            var eligible = new List<Faction>();
            for (int i = 0; i < factions.Count; i++)
            {
                if (IsEligible(factions[i]))
                    eligible.Add(factions[i]);
            }
            eligible.Sort((a, b) => a.loadID.CompareTo(b.loadID));

            for (int i = 0; i < eligible.Count; i++)
            {
                FactionRuntimeData data = comp.GetFactionData(eligible[i]);
                data.lastLifecycleTick = now;
                EvaluateFaction(comp, eligible[i], data, cfg, now);
            }

            return eligible.Count;
        }

        private static bool IsEligible(Faction faction)
        {
            if (faction == null) return false;
            if (faction.IsPlayer) return false;
            if (faction.temporary) return false;
            if (faction.defeated) return false;
            if (faction.Hidden) return false;
            if (faction.def == null) return false;
            // Factions the world generator never gives settlements to (mechanoids, insects,
            // the empire's special cases in some mods) stay out of the lifecycle entirely.
            if (faction.def.settlementGenerationWeight <= 0f) return false;
            return true;
        }

        private static void EvaluateFaction(FactionDynamicsWorldComp comp, Faction faction,
            FactionRuntimeData data, FDSimConfig cfg, int now)
        {
            List<Settlement> settlements = SettlementsOf(faction);

            if (data.baselineSettlementCount < 0)
                data.baselineSettlementCount = UnityEngine.Mathf.Max(1, settlements.Count);

            data.DecayMoods(cfg.settlementCheckIntervalDays);

            // Winter is hard on everyone. A faction whose heartland is in winter accumulates
            // hardship instead of shedding it, which is what eventually makes them hungry enough to
            // come and take your food - the seasonal pressure the whole starvation motivation
            // is supposed to come from.
            if (settlements.Count > 0)
                UpdateSettlementHardship(comp, faction, data, settlements, cfg, now);
            else
                data.DecayHardshipDirectly(cfg.settlementCheckIntervalDays);

            // Make sure this faction still has a seat. Done before the found/collapse rolls below so
            // the capital is already designated when TryRemoveSettlement checks what it may take.
            FDCapitals.EnsureCapital(comp, faction, settlements, true);

            int dayOfGame = GenDate.DaysPassed;
            int maxSettlements = UnityEngine.Mathf.Max(
                cfg.minSettlementsPerFaction,
                UnityEngine.Mathf.RoundToInt(data.baselineSettlementCount * cfg.maxSettlementsFactor));

            using (FDRand.Push(faction.loadID, dayOfGame, FDRandSalt.SettlementLifecycle))
            {
                // A faction under hardship is less likely to expand and more likely to lose ground.
                float foundChance = cfg.settlementFoundChance * (1f - 0.7f * data.hardship);
                float collapseChance = cfg.settlementCollapseChance * (1f + 1.5f * data.hardship);

                if (settlements.Count < maxSettlements && Rand.Chance(foundChance))
                {
                    SettlementLifecycleActions.TryFoundSettlement(comp, faction, data, settlements, now);
                    return;
                }

                if (settlements.Count > cfg.minSettlementsPerFaction && Rand.Chance(collapseChance))
                {
                    SettlementLifecycleActions.TryRemoveSettlement(comp, faction, data, settlements, now);
                    return;
                }

                // Nothing dramatic: a settlement may simply consolidate and get a little stronger.
                if (settlements.Count > 0 && Rand.Chance(0.25f))
                    SettlementLifecycleActions.StrengthenSettlement(comp, faction, settlements, now);
            }
        }

        /// <summary>
        /// Advances every settlement's own hardship, then rolls the faction's figure up from them.
        ///
        /// Each settlement answers for its own circumstances: its own tile's season, whether it is
        /// still regrouping from a raid that went badly, and how weak it has become. That is what
        /// makes a starvation raid legible - the town that sent it is the town that is hungry, and
        /// a faction spanning the equator and the ice sheet is not uniformly desperate.
        /// </summary>
        private static void UpdateSettlementHardship(FactionDynamicsWorldComp comp, Faction faction,
            FactionRuntimeData data, List<Settlement> settlements, FDSimConfig cfg, int now)
        {
            float days = cfg.settlementCheckIntervalDays;
            float decay = FDMoodTuning.HardshipDecayPerDay * days;
            float noGrowthGain = FDMoodTuning.NoGrowthHardshipPerDay * days;
            float regroupGain = FDMoodTuning.RegroupHardshipPerDay * days;

            int absTicks = GenTicks.TicksAbs;
            int starving = 0;
            int arid = 0;
            float total = 0f;

            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement s = settlements[i];
                SettlementRuntimeData sd = comp.GetSettlementData(s);

                // Saves made before per-settlement hardship existed: inherit the faction's figure
                // so a faction that was already struggling doesn't reset to comfortable.
                if (!sd.hardshipInitialized)
                {
                    sd.hardship = data.hardship;
                    sd.hardshipInitialized = true;
                }

                sd.hardship = UnityEngine.Mathf.Max(0f, sd.hardship - decay);

                // Can this settlement grow food right now? Asked of the actual seasonal temperature
                // at its own tile, not of the calendar. GetTemperatureFromSeasonAtTile is a pure
                // function - tile base temperature plus the season offset, no per-tile caching - so
                // it is safe to call for every settlement of every faction on this cadence.
                float tempC = GenTemperature.GetTemperatureFromSeasonAtTile(absTicks, s.Tile);
                float severity = FDMoodTuning.NoGrowthSeverity(tempC);
                if (severity > 0f)
                {
                    starving++;
                    if (sd.hardship < FDMoodTuning.NoGrowthHardshipCap)
                    {
                        sd.hardship = UnityEngine.Mathf.Min(FDMoodTuning.NoGrowthHardshipCap,
                            sd.hardship + noGrowthGain * severity);
                    }
                }

                // Still licking its wounds from a raid that cost it people.
                if (sd.IsRegrouping(now))
                    sd.hardship = UnityEngine.Mathf.Clamp01(sd.hardship + regroupGain);

                // A settlement worn down below its normal strength struggles to feed itself.
                if (sd.strengthFactor < 0.8f)
                    sd.hardship = UnityEngine.Mathf.Clamp01(sd.hardship + regroupGain * 0.5f);

                // Dry ground. A floor rather than a gain: a desert settlement never recovers all
                // the way to comfortable, but aridity alone never climbs toward a raid either.
                // Applied last so it also catches a settlement the decay just pulled under it.
                RimWorld.Planet.Tile tile = s.Tile.Valid ? Find.WorldGrid[s.Tile] : null;
                if (tile != null)
                {
                    float aridity = FDMoodTuning.AridityFloor(tile.rainfall);
                    if (aridity > 0f)
                    {
                        arid++;
                        if (sd.hardship < aridity) sd.hardship = aridity;
                    }
                }

                total += sd.hardship;
            }

            float before = data.hardship;

            // The faction figure is a proximity-weighted average, not a flat mean - see
            // RecomputeFactionHardship. The raw total above is only kept for the log line.
            comp.RecomputeFactionHardship(faction, settlements);

            if (UnityEngine.Mathf.Abs(data.hardship - before) > 0.01f)
            {
                FDLog.Debug(faction.Name + " hardship " + before.ToStringPercent()
                            + " -> " + data.hardship.ToStringPercent()
                            + " (weighted toward your colony; flat mean would be "
                            + (total / settlements.Count).ToStringPercent() + ", "
                            + starving + "/" + settlements.Count + " settlements too cold or hot to grow food, "
                            + arid + " on dry ground)");
            }
        }

        /// <summary>All of a faction's settlements, in a deterministic order.</summary>
        public static List<Settlement> SettlementsOf(Faction faction)
        {
            var result = new List<Settlement>();
            List<Settlement> all = Find.WorldObjects.Settlements;
            for (int i = 0; i < all.Count; i++)
            {
                Settlement s = all[i];
                if (s != null && s.Faction == faction)
                    result.Add(s);
            }
            result.Sort((a, b) => a.ID.CompareTo(b.ID));
            return result;
        }
    }
}
