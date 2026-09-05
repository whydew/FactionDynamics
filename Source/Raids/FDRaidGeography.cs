using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// M2 - Geography-aware raids.
    ///
    /// Vanilla picks a raiding faction essentially at random among your enemies, and nothing in the
    /// raid pipeline knows where the raiders came from (verified: IncidentParms has no origin
    /// settlement or world tile - only a map-local spawn cell). This class supplies that missing
    /// geography: it finds which of a faction's settlements would have sent the raid, how far away
    /// that is, and turns the distance into weightings for who raids you, how big the group is, and
    /// how they arrive.
    ///
    /// Everything here is a pure function of world state plus seeded RNG, so it is identical on
    /// every Multiplayer client.
    /// </summary>
    public static class FDRaidGeography
    {
        /// <summary>
        /// The settlement most likely to have sent a raid at <paramref name="targetTile"/>: near
        /// ones are strongly preferred, and settlements currently regrouping are skipped entirely
        /// (that is the whole point of the regroup mechanic - they can't attack again yet).
        /// Returns null if the faction has no usable settlement.
        /// </summary>
        public static Settlement SelectOrigin(Faction faction, PlanetTile targetTile, out float distance)
        {
            distance = FDWorldUtil.UnreachableDistance;
            if (faction == null) return null;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return null;

            int now = Find.TickManager.TicksGame;
            List<Settlement> settlements = FDWorldUtil.SettlementsOf(faction);
            if (settlements.Count == 0) return null;

            Settlement best = null;
            float bestScore = float.MinValue;
            float bestDist = FDWorldUtil.UnreachableDistance;

            // Two passes: first only settlements that are ready to fight, and only if none of those
            // exist, fall back to a regrouping one. The fallback matters - if every base is
            // regrouping and we returned null, the raid would still happen but nothing would be
            // marked as its source, so the regroup cycle would quietly stop working.
            for (int pass = 0; pass < 2 && best == null; pass++)
            {
                bool allowRegrouping = pass == 1;

                for (int i = 0; i < settlements.Count; i++)
                {
                    Settlement s = settlements[i];
                    SettlementRuntimeData sd = comp.GetSettlementData(s, false);
                    bool regrouping = sd != null && sd.IsRegrouping(now);

                    if (regrouping && !allowRegrouping) continue;

                    float dist = FDWorldUtil.TileDistance(s.Tile, targetTile);
                    if (dist >= FDWorldUtil.UnreachableDistance) continue;

                    float strength = sd?.EffectiveStrength(now, FactionDynamicsWorldComp.Config.regroupStrengthPenalty) ?? 1f;

                    // Prefer close and strong. Deterministic jitter keeps it from always being the
                    // same settlement when two are similar.
                    float jitter = FDRand.Value(s.ID, now / GenDate.TicksPerDay, FDRandSalt.RaidOrigin) * 0.2f;
                    float score = ProximityScore(dist) * strength + jitter;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = s;
                        bestDist = dist;
                    }
                }
            }

            distance = bestDist;
            return best;
        }

        /// <summary>
        /// How ready a faction is to mount a raid at all: the share of its settlements that aren't
        /// currently regrouping. A faction whose bases have all just been beaten back should be
        /// sending far fewer raids than one at full strength - without this, regrouping only ever
        /// moved raids to a different settlement of the same faction instead of slowing them down.
        /// </summary>
        public static float FactionReadiness(Faction faction)
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null || faction == null) return 1f;
            if (!FactionDynamicsWorldComp.Config.settlements) return 1f;

            List<Settlement> settlements = FDWorldUtil.SettlementsOf(faction);
            if (settlements.Count == 0) return 1f; // factions without bases (mechanoids) are unaffected

            int now = Find.TickManager.TicksGame;
            int ready = 0;
            for (int i = 0; i < settlements.Count; i++)
            {
                SettlementRuntimeData sd = comp.GetSettlementData(settlements[i], false);
                if (sd == null || !sd.IsRegrouping(now)) ready++;
            }

            // Never zero: a battered faction can still scrape a raid together, just rarely.
            return UnityEngine.Mathf.Max(0.15f, ready / (float)settlements.Count);
        }

        /// <summary>Strength of the settlement a raid is coming from, as a raid-size multiplier.</summary>
        public static float OriginStrength(Settlement origin)
        {
            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null || origin == null) return 1f;

            SettlementRuntimeData sd = comp.GetSettlementData(origin, false);
            if (sd == null) return 1f;

            return sd.EffectiveStrength(Find.TickManager.TicksGame,
                FactionDynamicsWorldComp.Config.regroupStrengthPenalty);
        }

        /// <summary>1 at the colony's doorstep, falling to ~0 for factions on the far side of the world.</summary>
        public static float ProximityScore(float distance)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            float near = cfg.raidNearTiles;
            float far = UnityEngine.Mathf.Max(near + 1f, cfg.raidFarTiles);

            if (distance <= near) return 1f;
            if (distance >= far) return 0f;
            return 1f - (distance - near) / (far - near);
        }

        /// <summary>
        /// Selection weight for a faction as the source of this raid. With bias 0 this returns 1
        /// for everyone (vanilla behaviour); with bias 3 a close neighbour is ~4x as likely as a
        /// faction across the planet.
        /// </summary>
        public static float FactionWeight(float distance)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            return 1f + cfg.raidProximityBias * ProximityScore(distance);
        }

        /// <summary>Faction selection weight including how battered the faction currently is.</summary>
        public static float FactionWeight(Faction faction, float distance)
        {
            return FactionWeight(distance) * FactionReadiness(faction);
        }

        /// <summary>Raid size multiplier: neighbours can commit more people than distant factions.</summary>
        public static float PointsFactor(float distance)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            float t = ProximityScore(distance);
            return UnityEngine.Mathf.Lerp(cfg.raidPointsMultFar, cfg.raidPointsMultNear, t);
        }

        /// <summary>
        /// Distance-appropriate arrival. Close neighbours walk in over the hill; a faction that had
        /// to cross the planet is far more likely to show up by drop pod, because walking would have
        /// taken a season. Returns null to leave vanilla's choice alone.
        /// </summary>
        public static PawnsArrivalModeDef PickArrivalMode(float distance, IncidentParms parms)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            if (!cfg.raidArrivalByDistance) return null;
            if (parms.raidStrategy == null) return null;
            if (distance >= FDWorldUtil.UnreachableDistance) return null;

            float proximity = ProximityScore(distance);

            // Long-distance raids: mostly pods. Close raids: mostly on foot.
            float podChance = UnityEngine.Mathf.Lerp(0.75f, 0.05f, proximity);

            PawnsArrivalModeDef chosen = Rand.Chance(podChance)
                ? PawnsArrivalModeDefOf.EdgeDrop
                : PawnsArrivalModeDefOf.EdgeWalkIn;

            // Respect what the chosen strategy actually allows, otherwise the raid can fail to fire.
            if (parms.raidStrategy.arriveModes == null || !parms.raidStrategy.arriveModes.Contains(chosen))
                return null;

            if (chosen.Worker != null && parms.target is Map map && !chosen.Worker.CanUseWith(parms))
                return null;

            return chosen;
        }

        /// <summary>Distance from an incident target to the closest settlement of a faction.</summary>
        public static float DistanceToFaction(Faction faction, PlanetTile targetTile)
        {
            List<Settlement> settlements = FDWorldUtil.SettlementsOf(faction);
            float best = FDWorldUtil.UnreachableDistance;
            for (int i = 0; i < settlements.Count; i++)
            {
                float d = FDWorldUtil.TileDistance(settlements[i].Tile, targetTile);
                if (d < best) best = d;
            }
            return best;
        }
    }
}
