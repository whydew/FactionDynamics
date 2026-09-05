using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Every module can be turned off independently. Module flags are read once into
    /// <see cref="FDActive"/> at startup (Harmony patches are applied per module, so module
    /// toggles need a restart); tuning values are read live and can change mid-game.
    ///
    /// In a Multiplayer session these settings are host-authoritative - see MultiplayerCompat.
    /// </summary>
    public class FactionDynamicsSettings : ModSettings
    {
        // ---------------- Modules ----------------
        public bool moduleSettlements = true;
        public bool moduleRaidGeography = true;
        public bool moduleRaidMotivations = true;
        public bool moduleQuestsRaiding = true;
        public bool moduleQuestsBounties = true;

        public bool verboseLogging = false;

        // ---------------- M1: Dynamic settlements ----------------
        /// <summary>How often the world runs a settlement lifecycle check, in days.</summary>
        public float settlementCheckIntervalDays = 4f;
        /// <summary>Chance per faction per check that a faction founds a new settlement.</summary>
        public float settlementFoundChance = 0.10f;
        /// <summary>Chance per faction per check that one of its settlements is lost.</summary>
        public float settlementCollapseChance = 0.08f;
        /// <summary>A faction never drops below this many settlements through our systems.</summary>
        public int minSettlementsPerFaction = 2;
        /// <summary>A faction never grows past (its starting settlement count) x this.</summary>
        public float maxSettlementsFactor = 1.6f;
        /// <summary>Days a settlement spends regrouping after sending a raid at the player.</summary>
        public float regroupDaysMin = 6f;
        public float regroupDaysMax = 15f;
        /// <summary>Strength lost by a settlement while it regroups (0-1).</summary>
        public float regroupStrengthPenalty = 0.45f;
        /// <summary>Send vanilla letters for settlement events within this many tiles of the player.</summary>
        public bool settlementLetters = true;
        public float settlementLetterRadius = 60f;

        // ---------------- M2: Geography-aware raids ----------------
        /// <summary>Distance at or below which a faction counts as a close neighbour (tiles).</summary>
        public float raidNearTiles = 25f;
        /// <summary>Distance at or above which a faction counts as far away (tiles).</summary>
        public float raidFarTiles = 130f;
        /// <summary>How strongly proximity biases which faction raids you. 0 = vanilla, 3 = extreme.</summary>
        public float raidProximityBias = 1.6f;
        /// <summary>Raid size multiplier for a close neighbour.</summary>
        public float raidPointsMultNear = 1.25f;
        /// <summary>Raid size multiplier for a distant faction.</summary>
        public float raidPointsMultFar = 0.75f;
        /// <summary>Let distance influence how raiders arrive (walk in vs. long-range drop).</summary>
        public bool raidArrivalByDistance = true;

        // ---------------- M3: Raid motivations ----------------
        public bool motivationStarvation = true;
        public bool motivationResourceTheft = true;
        public bool motivationRevenge = true;
        /// <summary>Chance that any given raid has a motivation rather than vanilla behaviour.</summary>
        public float motivationChance = 0.55f;
        /// <summary>Scales every motivation's retreat threshold. Higher = raiders stay longer.</summary>
        public float motivationRetreatMult = 1f;

        // ---------------- M4/M5: Quests ----------------
        /// <summary>Selection weight multiplier for this mod's settlement raid/defend quests.</summary>
        public float questWeightMult = 1f;
        /// <summary>Selection weight multiplier for bounty quests.</summary>
        public float bountyWeightMult = 1f;
        /// <summary>Scales bounty payouts.</summary>
        public float bountyRewardMult = 1f;
        /// <summary>Chance an ordinary visiting/raiding pawn turns out to have a bounty on them.</summary>
        public float bountySightingChance = 0.02f;
        /// <summary>Same, for a faction leader who sets foot on your map.</summary>
        public float bountySightingLeaderChance = 0.15f;

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref moduleSettlements, "moduleSettlements", true);
            Scribe_Values.Look(ref moduleRaidGeography, "moduleRaidGeography", true);
            Scribe_Values.Look(ref moduleRaidMotivations, "moduleRaidMotivations", true);
            Scribe_Values.Look(ref moduleQuestsRaiding, "moduleQuestsRaiding", true);
            Scribe_Values.Look(ref moduleQuestsBounties, "moduleQuestsBounties", true);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);

            Scribe_Values.Look(ref settlementCheckIntervalDays, "settlementCheckIntervalDays", 4f);
            Scribe_Values.Look(ref settlementFoundChance, "settlementFoundChance", 0.10f);
            Scribe_Values.Look(ref settlementCollapseChance, "settlementCollapseChance", 0.08f);
            Scribe_Values.Look(ref minSettlementsPerFaction, "minSettlementsPerFaction", 2);
            Scribe_Values.Look(ref maxSettlementsFactor, "maxSettlementsFactor", 1.6f);
            Scribe_Values.Look(ref regroupDaysMin, "regroupDaysMin", 6f);
            Scribe_Values.Look(ref regroupDaysMax, "regroupDaysMax", 15f);
            Scribe_Values.Look(ref regroupStrengthPenalty, "regroupStrengthPenalty", 0.45f);
            Scribe_Values.Look(ref settlementLetters, "settlementLetters", true);
            Scribe_Values.Look(ref settlementLetterRadius, "settlementLetterRadius", 60f);

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

        public void ResetToDefaults()
        {
            var d = new FactionDynamicsSettings();

            moduleSettlements = d.moduleSettlements;
            moduleRaidGeography = d.moduleRaidGeography;
            moduleRaidMotivations = d.moduleRaidMotivations;
            moduleQuestsRaiding = d.moduleQuestsRaiding;
            moduleQuestsBounties = d.moduleQuestsBounties;
            verboseLogging = d.verboseLogging;

            settlementCheckIntervalDays = d.settlementCheckIntervalDays;
            settlementFoundChance = d.settlementFoundChance;
            settlementCollapseChance = d.settlementCollapseChance;
            minSettlementsPerFaction = d.minSettlementsPerFaction;
            maxSettlementsFactor = d.maxSettlementsFactor;
            regroupDaysMin = d.regroupDaysMin;
            regroupDaysMax = d.regroupDaysMax;
            regroupStrengthPenalty = d.regroupStrengthPenalty;
            settlementLetters = d.settlementLetters;
            settlementLetterRadius = d.settlementLetterRadius;

            raidNearTiles = d.raidNearTiles;
            raidFarTiles = d.raidFarTiles;
            raidProximityBias = d.raidProximityBias;
            raidPointsMultNear = d.raidPointsMultNear;
            raidPointsMultFar = d.raidPointsMultFar;
            raidArrivalByDistance = d.raidArrivalByDistance;

            motivationStarvation = d.motivationStarvation;
            motivationResourceTheft = d.motivationResourceTheft;
            motivationRevenge = d.motivationRevenge;
            motivationChance = d.motivationChance;
            motivationRetreatMult = d.motivationRetreatMult;

            questWeightMult = d.questWeightMult;
            bountyWeightMult = d.bountyWeightMult;
            bountyRewardMult = d.bountyRewardMult;
            bountySightingChance = d.bountySightingChance;
            bountySightingLeaderChance = d.bountySightingLeaderChance;
        }
    }

    /// <summary>
    /// Snapshot of which modules were active when Harmony patches were applied. Simulation code
    /// checks these (not the live settings) so a mid-session settings edit can never leave half a
    /// module patched and half of it disabled - which in a Multiplayer session would desync.
    /// </summary>
    public static class FDActive
    {
        public static bool Settlements;
        public static bool RaidGeography;
        public static bool RaidMotivations;
        public static bool QuestsRaiding;
        public static bool QuestsBounties;

        public static void CaptureFrom(FactionDynamicsSettings s)
        {
            Settlements = s.moduleSettlements;
            RaidGeography = s.moduleRaidGeography;
            RaidMotivations = s.moduleRaidMotivations;
            QuestsRaiding = s.moduleQuestsRaiding;
            QuestsBounties = s.moduleQuestsBounties;
        }
    }
}
