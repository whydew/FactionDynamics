using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Picks why a raid is happening. A motivation is only offered if the faction is actually in
    /// that state - a well-fed faction never sends a starvation raid, and a faction with no grudge
    /// never sends a revenge raid - so the reason the letter gives the player is always true.
    /// </summary>
    public static class FDMotivationChooser
    {
        public static FDRaidMotivationDef Choose(Faction faction, Settlement origin)
        {
            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            if (!cfg.raidMotivations) return null;
            if (faction == null) return null;

            // Dev action override: skips the chance roll and the faction-state requirements.
            if (FDDebug.ForcedMotivation != null)
            {
                FDLog.Message("Motivation forced by dev action: " + FDDebug.ForcedMotivation.defName);
                return FDDebug.ForcedMotivation;
            }
            if (faction.def != null && !faction.def.humanlikeFaction) return null; // mechs don't get hungry

            if (!Rand.Chance(cfg.motivationChance)) return null;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            FactionRuntimeData fd = comp?.GetFactionData(faction, false);

            // Hardship is judged on the settlement that actually sent this raid - a town in deep
            // winter can send starving raiders even if the faction as a whole is comfortable, and
            // a town in high summer can't, however badly the rest of the faction is doing.
            // Grudge stays faction-wide: a score to settle with you is held by the whole faction.
            float hardship = comp?.HardshipFor(faction, origin) ?? 0f;
            float grudge = fd?.grudge ?? 0f;

            List<FDRaidMotivationDef> all = DefDatabase<FDRaidMotivationDef>.AllDefsListForReading;

            // Deterministic order regardless of def load order.
            var candidates = new List<FDRaidMotivationDef>();
            for (int i = 0; i < all.Count; i++)
            {
                FDRaidMotivationDef def = all[i];
                if (!IsEnabled(def, cfg)) continue;
                if (hardship < def.minHardship) continue;
                if (grudge < def.minGrudge) continue;
                candidates.Add(def);
            }
            candidates.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));

            if (candidates.Count == 0) return null;

            float totalWeight = 0f;
            for (int i = 0; i < candidates.Count; i++)
                totalWeight += Weight(candidates[i], hardship, grudge);

            if (totalWeight <= 0f) return null;

            float roll = Rand.Value * totalWeight;
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= Weight(candidates[i], hardship, grudge);
                if (roll <= 0f)
                    return candidates[i];
            }

            return candidates[candidates.Count - 1];
        }

        /// <summary>A motivation gets more likely the deeper the faction is in that state.</summary>
        private static float Weight(FDRaidMotivationDef def, float hardship, float grudge)
        {
            float w = def.weight;
            if (def.minHardship > 0f) w *= 0.5f + hardship;
            if (def.minGrudge > 0f) w *= 0.5f + grudge;
            return UnityEngine.Mathf.Max(0f, w);
        }

        private static bool IsEnabled(FDRaidMotivationDef def, FDSimConfig cfg)
        {
            switch (def.settingKey)
            {
                case "starvation": return cfg.motivationStarvation;
                case "plunder": return cfg.motivationResourceTheft;
                case "revenge": return cfg.motivationRevenge;
                default: return true; // motivations added by other mods are on unless they opt in
            }
        }
    }
}
