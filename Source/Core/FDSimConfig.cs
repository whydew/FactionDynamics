using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// The tuning values the *simulation* actually runs on, stored in the save.
    ///
    /// Why this exists: mod settings live on each player's machine. In a RimWorld Multiplayer
    /// session, two clients with different slider values would roll different outcomes from the
    /// same tick and desync within minutes. So simulation code never reads
    /// <see cref="FactionDynamicsMod.Settings"/> directly - it reads this object, which is part
    /// of the world save and therefore identical for everyone in the session.
    ///
    /// In singleplayer the config is refreshed from the local settings every time the world loads,
    /// so slider changes apply to existing saves. In a multiplayer session that refresh is skipped:
    /// the values that came with the host's world stand for the whole session.
    ///
    /// Cosmetic-only settings (letters, logging) are NOT here - they can differ per player safely.
    /// </summary>
    public class FDSimConfig : IExposable
    {
        // Modules (simulation-affecting half of the module toggles)
        public bool settlements = true;
        public bool raidGeography = true;
        public bool raidMotivations = true;
        public bool questsRaiding = true;
        public bool questsBounties = true;

        // M1
        public float settlementCheckIntervalDays = 4f;
        public float settlementFoundChance = 0.10f;
        public float settlementCollapseChance = 0.08f;
        public int minSettlementsPerFaction = 2;
        public float maxSettlementsFactor = 1.6f;
        public float regroupDaysMin = 6f;
        public float regroupDaysMax = 15f;
        public float regroupStrengthPenalty = 0.45f;

        // M2
        public float raidNearTiles = 25f;
        public float raidFarTiles = 130f;
        public float raidProximityBias = 1.6f;
        public float raidPointsMultNear = 1.25f;
        public float raidPointsMultFar = 0.75f;
        public bool raidArrivalByDistance = true;

        // M3
        public bool motivationStarvation = true;
        public bool motivationResourceTheft = true;
        public bool motivationRevenge = true;
        public float motivationChance = 0.55f;
        public float motivationRetreatMult = 1f;

        // M4 / M5
        public float questWeightMult = 1f;
        public float bountyWeightMult = 1f;
        public float bountyRewardMult = 1f;
        public float bountySightingChance = 0.02f;
        public float bountySightingLeaderChance = 0.15f;

        public void CopyFrom(FactionDynamicsSettings s)
        {
            if (s == null) return;

            settlements = s.moduleSettlements;
            raidGeography = s.moduleRaidGeography;
            raidMotivations = s.moduleRaidMotivations;
            questsRaiding = s.moduleQuestsRaiding;
            questsBounties = s.moduleQuestsBounties;

            settlementCheckIntervalDays = s.settlementCheckIntervalDays;
            settlementFoundChance = s.settlementFoundChance;
            settlementCollapseChance = s.settlementCollapseChance;
            minSettlementsPerFaction = s.minSettlementsPerFaction;
            maxSettlementsFactor = s.maxSettlementsFactor;
            regroupDaysMin = s.regroupDaysMin;
            regroupDaysMax = s.regroupDaysMax;
            regroupStrengthPenalty = s.regroupStrengthPenalty;

            raidNearTiles = s.raidNearTiles;
            raidFarTiles = s.raidFarTiles;
            raidProximityBias = s.raidProximityBias;
            raidPointsMultNear = s.raidPointsMultNear;
            raidPointsMultFar = s.raidPointsMultFar;
            raidArrivalByDistance = s.raidArrivalByDistance;

            motivationStarvation = s.motivationStarvation;
            motivationResourceTheft = s.motivationResourceTheft;
            motivationRevenge = s.motivationRevenge;
            motivationChance = s.motivationChance;
            motivationRetreatMult = s.motivationRetreatMult;

            questWeightMult = s.questWeightMult;
            bountyWeightMult = s.bountyWeightMult;
            bountyRewardMult = s.bountyRewardMult;
            bountySightingChance = s.bountySightingChance;
            bountySightingLeaderChance = s.bountySightingLeaderChance;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref settlements, "settlements", true);
            Scribe_Values.Look(ref raidGeography, "raidGeography", true);
            Scribe_Values.Look(ref raidMotivations, "raidMotivations", true);
            Scribe_Values.Look(ref questsRaiding, "questsRaiding", true);
            Scribe_Values.Look(ref questsBounties, "questsBounties", true);

            Scribe_Values.Look(ref settlementCheckIntervalDays, "settlementCheckIntervalDays", 4f);
            Scribe_Values.Look(ref settlementFoundChance, "settlementFoundChance", 0.10f);
            Scribe_Values.Look(ref settlementCollapseChance, "settlementCollapseChance", 0.08f);
            Scribe_Values.Look(ref minSettlementsPerFaction, "minSettlementsPerFaction", 2);
            Scribe_Values.Look(ref maxSettlementsFactor, "maxSettlementsFactor", 1.6f);
            Scribe_Values.Look(ref regroupDaysMin, "regroupDaysMin", 6f);
            Scribe_Values.Look(ref regroupDaysMax, "regroupDaysMax", 15f);
            Scribe_Values.Look(ref regroupStrengthPenalty, "regroupStrengthPenalty", 0.45f);

            Scribe_Values.Look(ref raidNearTiles, "raidNearTiles", 25f);
            Scribe_Values.Look(ref raidFarTiles, "raidFarTiles", 130f);
            Scribe_Values.Look(ref raidProximityBias, "raidProximityBias", 1.6f);
            Scribe_Values.Look(ref raidPointsMultNear, "raidPointsMultNear", 1.25f);
            Scribe_Values.Look(ref raidPointsMultFar, "raidPointsMultFar", 0.75f);
            Scribe_Values.Look(ref raidArrivalByDistance, "raidArrivalByDistance", true);

            Scribe_Values.Look(ref motivationStarvation, "motivationStarvation", true);
            Scribe_Values.Look(ref motivationResourceTheft, "motivationResourceTheft", true);
            Scribe_Values.Look(ref motivationRevenge, "motivationRevenge", true);
            Scribe_Values.Look(ref motivationChance, "motivationChance", 0.55f);
            Scribe_Values.Look(ref motivationRetreatMult, "motivationRetreatMult", 1f);

            Scribe_Values.Look(ref questWeightMult, "questWeightMult", 1f);
            Scribe_Values.Look(ref bountyWeightMult, "bountyWeightMult", 1f);
            Scribe_Values.Look(ref bountyRewardMult, "bountyRewardMult", 1f);
            Scribe_Values.Look(ref bountySightingChance, "bountySightingChance", 0.02f);
            Scribe_Values.Look(ref bountySightingLeaderChance, "bountySightingLeaderChance", 0.15f);
        }

        /// <summary>Cheap fingerprint of the values the simulation depends on, for desync triage.</summary>
        public int ConfigHash()
        {
            int h = 17;
            h = Gen.HashCombineInt(h, settlements ? 1 : 0);
            h = Gen.HashCombineInt(h, raidGeography ? 1 : 0);
            h = Gen.HashCombineInt(h, raidMotivations ? 1 : 0);
            h = Gen.HashCombineInt(h, Q(settlementCheckIntervalDays));
            h = Gen.HashCombineInt(h, Q(settlementFoundChance));
            h = Gen.HashCombineInt(h, Q(settlementCollapseChance));
            h = Gen.HashCombineInt(h, minSettlementsPerFaction);
            h = Gen.HashCombineInt(h, Q(maxSettlementsFactor));
            h = Gen.HashCombineInt(h, Q(regroupDaysMin));
            h = Gen.HashCombineInt(h, Q(regroupDaysMax));
            h = Gen.HashCombineInt(h, Q(regroupStrengthPenalty));
            h = Gen.HashCombineInt(h, Q(raidNearTiles));
            h = Gen.HashCombineInt(h, Q(raidFarTiles));
            h = Gen.HashCombineInt(h, Q(raidProximityBias));
            h = Gen.HashCombineInt(h, Q(raidPointsMultNear));
            h = Gen.HashCombineInt(h, Q(raidPointsMultFar));
            h = Gen.HashCombineInt(h, Q(motivationChance));
            h = Gen.HashCombineInt(h, Q(motivationRetreatMult));
            return h;
        }

        private static int Q(float f) => UnityEngine.Mathf.RoundToInt(f * 1000f);
    }
}
