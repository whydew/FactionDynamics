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
        /// Hardship added per DAY of winter, before decay. Expressed per-day rather than per-check
        /// so it stays consistent when the lifecycle interval is changed in settings - the decay is
        /// per-day too, and the first version of this was a flat per-check value that the decay
        /// simply out-ran, so winter did nothing at all.
        ///
        /// At the default 0.02/day decay this nets about +0.025/day, so a faction crosses the 0.25
        /// starvation threshold roughly ten days into winter and stays hungry into early spring.
        /// </summary>
        public const float WinterHardshipPerDay = 0.045f;

        /// <summary>Hardship a settlement sheds per day when nothing is going wrong.</summary>
        public const float HardshipDecayPerDay = 0.02f;

        /// <summary>Hardship per day for a settlement still regrouping from a costly raid.</summary>
        public const float RegroupHardshipPerDay = 0.03f;

        /// <summary>
        /// Winter alone can't push a faction past this. Losing settlements can take them higher;
        /// otherwise a permanent-winter faction would sit pinned at 100% hardship forever and send
        /// nothing but starvation raids.
        /// </summary>
        public const float WinterHardshipCap = 0.6f;

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
