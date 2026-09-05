using UnityEngine;
using Verse;

namespace FactionDynamics
{
    public class FactionDynamicsMod : Mod
    {
        public static FactionDynamicsSettings Settings;

        private Vector2 scrollPosition = Vector2.zero;
        private float lastContentHeight = 1200f;

        public FactionDynamicsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FactionDynamicsSettings>();
        }

        public override string SettingsCategory() => "Faction Dynamics";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var s = Settings;

            var outRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 40f);
            var viewRect = new Rect(0f, 0f, inRect.width - 20f, lastContentHeight);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(viewRect);

            // ---------------- Modules ----------------
            Header(list, "Modules");
            list.Label("Module toggles take effect after a restart - each module's Harmony patches are applied at startup.");
            list.CheckboxLabeled("Dynamic settlements", ref s.moduleSettlements,
                "Faction settlements are founded, expand and collapse over time, and settlements that raid you are weakened while they regroup.");
            list.CheckboxLabeled("Geography-aware raids", ref s.moduleRaidGeography,
                "Nearby hostile factions raid you far more often, with bigger and faster-arriving groups than distant ones.");
            list.CheckboxLabeled("Raid motivations", ref s.moduleRaidMotivations,
                "Raiders arrive with a goal - stealing food, stealing valuables, or revenge - which changes what they target and when they leave.");
            list.CheckboxLabeled("Settlement raid/defend quests", ref s.moduleQuestsRaiding,
                "Adds quests to attack hostile settlements and defend allied ones.");
            list.CheckboxLabeled("Bounty quests", ref s.moduleQuestsBounties,
                "Powerful factions place bounties on specific named pawns; capture or kill them for a reward.");
            list.CheckboxLabeled("Verbose logging", ref s.verboseLogging,
                "Logs every settlement lifecycle roll and raid decision. Useful for balancing or bug reports, noisy otherwise.");

            // ---------------- M1 ----------------
            if (s.moduleSettlements)
            {
                Header(list, "Dynamic settlements");
                s.settlementCheckIntervalDays = list.SliderLabeled(
                    "Lifecycle check interval: " + s.settlementCheckIntervalDays.ToString("F0") + " days",
                    s.settlementCheckIntervalDays, 1f, 30f);
                s.settlementFoundChance = list.SliderLabeled(
                    "Chance a faction founds a settlement per check: " + s.settlementFoundChance.ToStringPercent(),
                    s.settlementFoundChance, 0f, 0.5f);
                s.settlementCollapseChance = list.SliderLabeled(
                    "Chance a faction loses a settlement per check: " + s.settlementCollapseChance.ToStringPercent(),
                    s.settlementCollapseChance, 0f, 0.5f);
                s.minSettlementsPerFaction = Mathf.RoundToInt(list.SliderLabeled(
                    "Never reduce a faction below: " + s.minSettlementsPerFaction + " settlements",
                    s.minSettlementsPerFaction, 1f, 10f));
                s.maxSettlementsFactor = list.SliderLabeled(
                    "Never grow a faction past its starting count x " + s.maxSettlementsFactor.ToString("F1"),
                    s.maxSettlementsFactor, 1f, 4f);
                s.regroupDaysMin = list.SliderLabeled(
                    "Regroup time after raiding you, minimum: " + s.regroupDaysMin.ToString("F0") + " days",
                    s.regroupDaysMin, 0f, 60f);
                s.regroupDaysMax = list.SliderLabeled(
                    "Regroup time after raiding you, maximum: " + s.regroupDaysMax.ToString("F0") + " days",
                    s.regroupDaysMax, 0f, 90f);
                if (s.regroupDaysMax < s.regroupDaysMin) s.regroupDaysMax = s.regroupDaysMin;
                s.regroupStrengthPenalty = list.SliderLabeled(
                    "Strength lost while regrouping: " + s.regroupStrengthPenalty.ToStringPercent(),
                    s.regroupStrengthPenalty, 0f, 0.9f);
                list.CheckboxLabeled("Letters for nearby settlement events", ref s.settlementLetters);
                if (s.settlementLetters)
                {
                    s.settlementLetterRadius = list.SliderLabeled(
                        "Only for events within: " + s.settlementLetterRadius.ToString("F0") + " tiles",
                        s.settlementLetterRadius, 10f, 300f);
                }
            }

            // ---------------- M2 ----------------
            if (s.moduleRaidGeography)
            {
                Header(list, "Geography-aware raids");
                s.raidNearTiles = list.SliderLabeled(
                    "Close neighbour distance: " + s.raidNearTiles.ToString("F0") + " tiles",
                    s.raidNearTiles, 5f, 100f);
                s.raidFarTiles = list.SliderLabeled(
                    "Distant faction distance: " + s.raidFarTiles.ToString("F0") + " tiles",
                    s.raidFarTiles, 40f, 400f);
                if (s.raidFarTiles < s.raidNearTiles + 5f) s.raidFarTiles = s.raidNearTiles + 5f;
                s.raidProximityBias = list.SliderLabeled(
                    "Proximity bias on who raids you: " + s.raidProximityBias.ToString("F1") + " (0 = vanilla)",
                    s.raidProximityBias, 0f, 3f);
                s.raidPointsMultNear = list.SliderLabeled(
                    "Raid size from close neighbours: x" + s.raidPointsMultNear.ToString("F2"),
                    s.raidPointsMultNear, 0.5f, 2f);
                s.raidPointsMultFar = list.SliderLabeled(
                    "Raid size from distant factions: x" + s.raidPointsMultFar.ToString("F2"),
                    s.raidPointsMultFar, 0.3f, 1.5f);
                list.CheckboxLabeled("Distance affects how raiders arrive", ref s.raidArrivalByDistance,
                    "Close neighbours walk in on foot. Distant factions are more likely to arrive by drop pod, because they had to cross the planet.");
            }

            // ---------------- M3 ----------------
            if (s.moduleRaidMotivations)
            {
                Header(list, "Raid motivations");
                s.motivationChance = list.SliderLabeled(
                    "Chance a raid has a motivation: " + s.motivationChance.ToStringPercent(),
                    s.motivationChance, 0f, 1f);
                s.motivationRetreatMult = list.SliderLabeled(
                    "Retreat threshold multiplier: x" + s.motivationRetreatMult.ToString("F2") + " (higher = raiders stay longer)",
                    s.motivationRetreatMult, 0.25f, 3f);
                list.CheckboxLabeled("Starvation - raiders steal food and leave", ref s.motivationStarvation);
                list.CheckboxLabeled("Plunder - raiders steal valuables and leave", ref s.motivationResourceTheft);
                list.CheckboxLabeled("Revenge - raiders fight harder and retreat later", ref s.motivationRevenge);
            }

            // ---------------- M4/M5 ----------------
            if (s.moduleQuestsRaiding || s.moduleQuestsBounties)
            {
                Header(list, "Quests");
                if (s.moduleQuestsRaiding)
                {
                    s.questWeightMult = list.SliderLabeled(
                        "Settlement raid/defend quest frequency: x" + s.questWeightMult.ToString("F2"),
                        s.questWeightMult, 0f, 3f);
                }
                if (s.moduleQuestsBounties)
                {
                    s.bountyWeightMult = list.SliderLabeled(
                        "Bounty quest frequency: x" + s.bountyWeightMult.ToString("F2"),
                        s.bountyWeightMult, 0f, 3f);
                    s.bountyRewardMult = list.SliderLabeled(
                        "Bounty reward size: x" + s.bountyRewardMult.ToString("F2"),
                        s.bountyRewardMult, 0.25f, 4f);
                    s.bountySightingChance = list.SliderLabeled(
                        "Chance a visitor or raider turns out to be wanted: " + s.bountySightingChance.ToStringPercent(),
                        s.bountySightingChance, 0f, 0.25f);
                    s.bountySightingLeaderChance = list.SliderLabeled(
                        "Same, for a faction leader on your map: " + s.bountySightingLeaderChance.ToStringPercent(),
                        s.bountySightingLeaderChance, 0f, 1f);
                }
            }

            list.Gap();
            if (list.ButtonText("Reset all to defaults"))
                s.ResetToDefaults();

            if (MultiplayerCompat.InMultiplayer)
            {
                list.Gap();
                list.Label("Multiplayer session active: the host's settings are the ones the simulation uses.");
            }

            lastContentHeight = list.CurHeight + 40f;
            list.End();
            Widgets.EndScrollView();
        }

        private static void Header(Listing_Standard list, string label)
        {
            list.Gap();
            list.GapLine();
            Text.Font = GameFont.Medium;
            list.Label(label);
            Text.Font = GameFont.Small;
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            // Tuning values apply live; module flags need a restart, which the settings text says.
        }
    }
}
