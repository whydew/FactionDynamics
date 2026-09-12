using HarmonyLib;
using RimWorld;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// The natural sources of faction hardship and grudge.
    ///
    /// Hardship = "this faction is struggling". It rises when they lose settlements and through
    /// winter, and decays year-round. High hardship makes starvation raids available and makes
    /// further settlement losses more likely.
    ///
    /// Grudge = "this faction has a score to settle with YOU". It rises when you kill or capture
    /// their people and when you take their ground, and falls when you make nice with them (gifts,
    /// trade, quest rewards - anything that raises goodwill). High grudge makes revenge raids
    /// available and makes them fight to the end.
    ///
    /// Every amount here is deliberately small: these are meant to accumulate over a campaign, not
    /// swing on a single event. One dead raider is nothing; wiping out three raids is a grudge.
    /// </summary>
    public static class FDMoodTuning
    {
        /// <summary>Grudge per pawn of theirs the player kills.</summary>
        public const float GrudgePerKill = 0.015f;

        /// <summary>Grudge when the player takes one of their people prisoner.</summary>
        public const float GrudgePerCapture = 0.05f;

        /// <summary>Grudge removed per point of goodwill gained (gifts, trade, quest rewards).</summary>
        public const float GrudgeReliefPerGoodwill = 0.004f;

        /// <summary>
        /// Hardship added per DAY a settlement cannot grow food, before decay. Expressed per-day
        /// rather than per-check so it stays consistent when the lifecycle interval is changed in
        /// settings - the decay is per-day too, and the first version of this was a flat per-check
        /// value that the decay simply out-ran, so it did nothing at all.
        ///
        /// At the default 0.02/day decay this nets about +0.025/day at full severity, so a
        /// settlement crosses the 0.25 starvation threshold roughly ten days into a hard freeze.
        /// </summary>
        public const float NoGrowthHardshipPerDay = 0.045f;

        /// <summary>
        /// How far past the growing limit counts as "as bad as it gets" - see
        /// <see cref="NoGrowthSeverity"/>. 20 degrees below freezing is a dead landscape; there is
        /// no point modelling -60 as three times worse than -20.
        /// </summary>
        public const float NoGrowthFullDepthC = 20f;

        /// <summary>
        /// Severity of a settlement's growing failure, 0 (crops are fine) to 1 (nothing will ever
        /// grow here), from the seasonal temperature at its own tile.
        ///
        /// This replaced a plain <c>Season == Winter</c> test, which was the calendar's opinion
        /// rather than the settlement's. Vanilla's Season is derived from latitude and the twelfth
        /// of the year, so an equatorial town could read "winter" at 25 degrees and start starving,
        /// while a highland town sitting at -15 all spring read "summer" and was fine. The
        /// thresholds are vanilla's own plant limits, so this tracks the game's definition of
        /// growable rather than a number invented here.
        ///
        /// Heat is included for the same reason cold is: above 58C nothing grows either, which
        /// matters for extreme desert factions.
        ///
        /// The floor of 0.5 is load-bearing, not cosmetic. Hardship decays at 0.02/day, so any
        /// severity below ~0.44 is out-run by the decay and produces no accumulation at all - a
        /// settlement sitting at -1C would have read as "cannot grow food" while quietly getting
        /// no hungrier forever. At 0.5 the mildest freeze still drifts upward, just slowly (about
        /// 70 days to the starvation threshold, against 10 days at -20C or below).
        /// </summary>
        public static float NoGrowthSeverity(float tempCelsius)
        {
            float belowCold = RimWorld.Plant.DefaultMinGrowthTemperature - tempCelsius;
            float aboveHeat = tempCelsius - RimWorld.Plant.DefaultMaxGrowthTemperature;
            float excess = UnityEngine.Mathf.Max(belowCold, aboveHeat);

            if (excess <= 0f) return 0f;

            return UnityEngine.Mathf.Clamp01(0.5f + 0.5f * (excess / NoGrowthFullDepthC));
        }

        /// <summary>
        /// Rainfall (mm/yr) at or above which a settlement's land counts as ordinary farmland and
        /// carries no standing penalty.
        ///
        /// 600 is not a number invented here - it is vanilla's own dividing line. BiomeWorker_
        /// TemperateForest disqualifies any tile under 600mm, and Arid Shrubland and Desert are
        /// exactly the biomes that live below it. Extreme Desert starts under 340mm.
        /// </summary>
        public const float FertileRainfallMm = 600f;

        /// <summary>
        /// The most standing hardship dry land alone can impose. Deliberately well under the 0.25
        /// starvation threshold: aridity should never start a raid by itself, only mean that a
        /// desert faction is already part-way there when winter or a lost settlement arrives.
        /// </summary>
        public const float MaxAridityHardship = 0.15f;

        /// <summary>
        /// A permanent hardship floor from how dry a settlement's tile is, 0 at temperate-forest
        /// rainfall and <see cref="MaxAridityHardship"/> on bone-dry ground.
        ///
        /// Temperature was the wrong lever for hot regions: RimWorld's hottest tiles top out near
        /// 30C at the equator, nowhere near the 58C at which plants actually die, so the heat side
        /// of the growing check almost never fires on a default world. What actually stops desert
        /// farming in this game is rainfall, and vanilla picks its desert biomes on exactly that.
        ///
        /// Unlike the cold penalty this does not accumulate - it is a floor. A dry settlement can
        /// never decay below it, but it never climbs on its own either.
        /// </summary>
        public static float AridityFloor(float rainfallMm)
        {
            if (rainfallMm >= FertileRainfallMm) return 0f;

            float dryness = (FertileRainfallMm - UnityEngine.Mathf.Max(0f, rainfallMm)) / FertileRainfallMm;
            return UnityEngine.Mathf.Clamp01(dryness) * MaxAridityHardship;
        }

        /// <summary>Hardship a settlement sheds per day when nothing is going wrong.</summary>
        public const float HardshipDecayPerDay = 0.02f;

        /// <summary>Hardship per day for a settlement still regrouping from a costly raid.</summary>
        public const float RegroupHardshipPerDay = 0.03f;

        /// <summary>
        /// A dead growing season alone can't push a settlement past this. Losing ground can take it
        /// higher; otherwise an ice-sheet settlement would sit pinned at 100% hardship forever and
        /// its faction would send nothing but starvation raids.
        /// </summary>
        public const float NoGrowthHardshipCap = 0.6f;

        /// <summary>
        /// The weight a settlement on the far side of the planet carries in its faction's hardship
        /// average. Settlements near the player count for a full 1.0, distant ones for this floor,
        /// with everything between scaled by <see cref="FDRaidGeography.ProximityScore"/>.
        ///
        /// A flat mean made the faction figure useless for judging threat: with fifty settlements
        /// spread across the planet, the three wintering towns actually close enough to raid you
        /// moved the average by a couple of percent. The floor is deliberately non-zero so a faction
        /// collapsing far away still reads as a faction in trouble - just not as loudly as one
        /// starving on your doorstep.
        /// </summary>
        public const float HardshipDistantWeight = 0.15f;
    }

    /// <summary>Player killed one of their pawns - they remember.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_PawnKilled
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, DamageInfo? dinfo)
        {
            if (__instance == null || __instance.Faction == null || __instance.Faction.IsPlayer) return;
            if (__instance.RaceProps == null || !__instance.RaceProps.Humanlike) return;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return;

            // Only killings the player is responsible for.
            Thing instigator = dinfo?.Instigator;
            Faction killerFaction = instigator?.Faction;
            if (killerFaction == null || !killerFaction.IsPlayer) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            comp?.NotifyPlayerAggression(__instance.Faction, FDMoodTuning.GrudgePerKill);
        }
    }

    /// <summary>Player took one of their people prisoner - worse than killing them, to them.</summary>
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.CapturedBy))]
    public static class Patch_PawnCaptured
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_GuestTracker __instance, Faction by)
        {
            if (by == null || !by.IsPlayer) return;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return;

            Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            Faction victimFaction = pawn?.Faction;
            if (victimFaction == null || victimFaction.IsPlayer) return;
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            comp?.NotifyPlayerAggression(victimFaction, FDMoodTuning.GrudgePerCapture);
            FDLog.Debug(victimFaction.Name + " grudge up: you took " + pawn.LabelShort + " prisoner.");
        }
    }

    /// <summary>
    /// Making peace works. Any goodwill the player gains with a faction - gifts, trade, quest
    /// rewards, peace talks - bleeds off part of their grudge, so a grudge is something you can
    /// actually settle rather than a one-way ratchet.
    /// </summary>
    [HarmonyPatch(typeof(Faction), nameof(Faction.TryAffectGoodwillWith))]
    public static class Patch_GoodwillChanged
    {
        [HarmonyPostfix]
        public static void Postfix(Faction __instance, Faction other, int goodwillChange, bool __result)
        {
            if (!__result || goodwillChange <= 0) return;
            if (other == null || !other.IsPlayer) return;
            if (__instance == null || __instance.IsPlayer) return;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            FactionRuntimeData fd = comp?.GetFactionData(__instance, false);
            if (fd == null || fd.grudge <= 0f) return;

            float before = fd.grudge;
            fd.grudge = UnityEngine.Mathf.Max(0f, fd.grudge - goodwillChange * FDMoodTuning.GrudgeReliefPerGoodwill);

            if (before - fd.grudge > 0.01f)
            {
                FDLog.Debug(__instance.Name + " grudge " + before.ToStringPercent()
                            + " -> " + fd.grudge.ToStringPercent() + " (goodwill +" + goodwillChange + ")");
            }
        }
    }
}
