using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Per-faction state this mod tracks across the whole save. Keyed by <see cref="Faction.loadID"/>
    /// in <see cref="FactionDynamicsWorldComp"/>. Everything here is deterministic simulation state
    /// and must round-trip through <see cref="ExposeData"/> exactly.
    /// </summary>
    public class FactionRuntimeData : IExposable
    {
        /// <summary>Faction.loadID this data belongs to.</summary>
        public int factionId = -1;

        /// <summary>Settlement count the first time we saw this faction; the growth cap is relative to it.</summary>
        public int baselineSettlementCount = -1;

        /// <summary>Game tick of the last lifecycle evaluation for this faction.</summary>
        public int lastLifecycleTick = -1;

        /// <summary>
        /// 0..1. Rises when the faction loses settlements or has recently been beaten back, decays
        /// slowly. High hardship makes "we are starving" raid motivations available and makes the
        /// faction less likely to found new settlements.
        /// </summary>
        public float hardship;

        /// <summary>
        /// 0..1. Rises when the player destroys their settlements or kills their people, decays
        /// slowly. High grudge makes revenge raids more likely and more committed.
        /// </summary>
        public float grudge;

        /// <summary>Tick this faction last sent a raid at the player (-1 = never).</summary>
        public int lastRaidTick = -1;

        /// <summary>
        /// Settlement.ID of this faction's capital (-1 = none designated yet). See
        /// <see cref="FDCapitals"/> - vanilla has no capital concept, so this mod declares one.
        /// </summary>
        public int capitalSettlementId = -1;

        public FactionRuntimeData()
        {
        }

        public FactionRuntimeData(int factionId)
        {
            this.factionId = factionId;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref factionId, "factionId", -1);
            Scribe_Values.Look(ref baselineSettlementCount, "baselineSettlementCount", -1);
            Scribe_Values.Look(ref lastLifecycleTick, "lastLifecycleTick", -1);
            Scribe_Values.Look(ref hardship, "hardship", 0f);
            Scribe_Values.Look(ref grudge, "grudge", 0f);
            Scribe_Values.Look(ref lastRaidTick, "lastRaidTick", -1);
            Scribe_Values.Look(ref capitalSettlementId, "capitalSettlementId", -1);
        }

        /// <summary>
        /// Grudge decay. Called once per lifecycle check - slow, so a grudge fades over seasons.
        ///
        /// Hardship is NOT decayed here: it is recomputed from this faction's settlements, each of
        /// which carries and decays its own. Only settlement-less factions (mechanoids and the like)
        /// fall back to decaying the faction value directly, via <see cref="DecayHardshipDirectly"/>.
        /// </summary>
        public void DecayMoods(float checkIntervalDays)
        {
            grudge = UnityEngine.Mathf.Max(0f, grudge - 0.012f * checkIntervalDays);
        }

        /// <summary>Fallback decay for factions that hold no settlements to aggregate from.</summary>
        public void DecayHardshipDirectly(float checkIntervalDays)
        {
            hardship = UnityEngine.Mathf.Max(0f, hardship - 0.02f * checkIntervalDays);
        }

        public void AddHardship(float amount)
        {
            hardship = UnityEngine.Mathf.Clamp01(hardship + amount);
        }

        /// <summary>
        /// Adds hardship but never pushes past <paramref name="cap"/>, and never reduces it if the
        /// faction is already above the cap from some other cause (lost settlements, say).
        /// </summary>
        public void AddHardshipUpTo(float amount, float cap)
        {
            if (hardship >= cap) return;
            hardship = UnityEngine.Mathf.Min(cap, hardship + amount);
        }

        public void AddGrudge(float amount)
        {
            grudge = UnityEngine.Mathf.Clamp01(grudge + amount);
        }
    }
}
