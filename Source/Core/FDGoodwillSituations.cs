using RimWorld;
using UnityEngine;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Shared tuning and plumbing for the mod's goodwill situations.
    ///
    /// Until now grudge and hardship were private numbers. They changed how raids behaved and they
    /// showed up in FD's own UI, but the rest of the game could not see them: the Factions tab, the
    /// natural-goodwill drift, the hostility check - none of it knew a faction was furious. This is
    /// the seam that fixes that, and vanilla hands it over without a single Harmony patch.
    ///
    /// How the engine uses these (verified against Assembly-CSharp, 1.6):
    ///
    ///   GoodwillSituationManager.Recalculate walks DefDatabase&lt;GoodwillSituationDef&gt; and asks
    ///   every def's worker for a max and an offset. Being in the database IS the registration -
    ///   there is no list to patch into.
    ///
    ///   A situation is only kept if maxGoodwill &lt; 100 || naturalGoodwillOffset != 0. So a worker
    ///   that returns (100, 0) costs one virtual call and then vanishes - it does not appear in the
    ///   Factions tab explanation and contributes nothing. That is why every worker here returns
    ///   neutral rather than "zero effect but present" when the mod has nothing to say.
    ///
    ///   GetMaxGoodwill is a MIN across situations, starting at 100 - workers cap.
    ///   GetNaturalGoodwill is a SUM across situations, starting at 0 - workers offset.
    ///
    ///   Recalculation happens in GoodwillManagerTick every 1000 ticks, on a tick, which is what
    ///   makes this safe under Multiplayer: the inputs are world-save state, the schedule is the
    ///   synced tick counter, and nothing here rolls dice. There is deliberately no FDRand scope in
    ///   this file - a seeded roll would be *less* correct here, not more, because the same faction
    ///   must produce the same cap every time it is asked, not once per recache.
    /// </summary>
    public static class FDGoodwillTuning
    {
        /// <summary>
        /// The goodwill value at or below which vanilla turns a faction hostile.
        ///
        /// From Faction.GoodwillToMakeHostile, which returns (-75 - GoodwillWith(other)) - i.e. the
        /// delta needed to reach -75. The threshold itself is not exposed as a constant, so it is
        /// named here rather than spelled -75 at each use site.
        /// </summary>
        public const int HostileAtOrBelow = -75;

        /// <summary>
        /// How far above the hostility line FD's cap is allowed to squeeze, and no further.
        ///
        /// This is the whole "cap with a floor" decision in one number. FD can make a faction's
        /// relations cold enough that they will not recover on their own, but it will never be the
        /// thing that declares war. Crossing the line stays something the player did, or something
        /// vanilla did - a raid they wiped out, a settlement they burned - which keeps the causality
        /// legible. A mod that quietly flips neighbours hostile from an invisible counter is a mod
        /// the player experiences as broken.
        /// </summary>
        public const int HostilityFloorMargin = 10;

        /// <summary>Lowest max-goodwill FD will ever return. Stays clear of the hostility line.</summary>
        public const int GrudgeCapFloor = HostileAtOrBelow + HostilityFloorMargin;   // -65

        /// <summary>Below this grudge FD says nothing at all, so light friction stays invisible.</summary>
        public const float GrudgeCapThreshold = 0.20f;

        /// <summary>Cap applied the moment grudge crosses the threshold. Deliberately slack.</summary>
        public const int GrudgeCapAtThreshold = 75;

        /// <summary>Below this hardship FD says nothing.</summary>
        public const float HardshipOffsetThreshold = 0.25f;

        /// <summary>Natural-goodwill offset at maximum hardship.</summary>
        public const int HardshipOffsetAtMax = -30;

        /// <summary>
        /// Whether this worker should say anything about <paramref name="other"/> at all.
        ///
        /// The player check is not paranoia. GoodwillSituationManager.RecalculateAll skips only
        /// Faction.OfPlayer - the *local* player faction. Under RimWorld Multiplayer's multifaction
        /// support there are several player factions, so another player's faction reaches this code
        /// as `other`. Multiplayer itself prefixes GetNaturalGoodwill/GetMaxGoodwill on the manager
        /// to bail on player factions, but that is the manager's entry point, not the worker's, and
        /// relying on another mod's patch to keep our own code correct is how load-order bugs are
        /// made. FD has nothing to say about relations between two players regardless.
        /// </summary>
        public static bool Applies(Faction other, out FactionRuntimeData data)
        {
            data = null;

            if (other == null || other.IsPlayer || other.Hidden) return false;
            if (!other.HasGoodwill) return false;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return false;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return false;

            // create: false - being asked about a faction is not a reason to start tracking it.
            // This runs every 1000 ticks for every faction in the game, including ones FD has never
            // touched, and creating on read would fill the table with empty rows the dump then has
            // to filter out. That exact mistake was already made once with settlements.
            data = comp.GetFactionData(other, false);
            return data != null;
        }
    }

    /// <summary>
    /// "They have not forgotten." Grudge caps how good relations can get.
    ///
    /// A faction whose settlements the player has burned should not be able to be bought back to
    /// warm relations with a few gifts while the grudge is still fresh. Capping rather than
    /// offsetting is the honest shape for that: the player can still raise goodwill by every normal
    /// means, they simply cannot get past the ceiling until the grudge decays (which it does, over
    /// seasons, in FactionRuntimeData.DecayMoods).
    /// </summary>
    public class FDGoodwillSituationWorker_Grudge : GoodwillSituationWorker
    {
        public override int GetMaxGoodwill(Faction other)
        {
            if (!FDGoodwillTuning.Applies(other, out FactionRuntimeData data)) return 100;

            return CapFor(data.grudge);
        }

        /// <summary>
        /// Grudge → goodwill ceiling. Public and static so the debug dump and the tests can ask the
        /// same question the engine asks without going through a recache.
        /// </summary>
        public static int CapFor(float grudge)
        {
            if (grudge < FDGoodwillTuning.GrudgeCapThreshold) return 100;

            float t = Mathf.InverseLerp(FDGoodwillTuning.GrudgeCapThreshold, 1f, grudge);
            int cap = Mathf.RoundToInt(Mathf.Lerp(
                FDGoodwillTuning.GrudgeCapAtThreshold,
                FDGoodwillTuning.GrudgeCapFloor,
                t));

            // The floor is the point of the whole design, so it is enforced here rather than left
            // to the arithmetic above happening to stay in range.
            return Mathf.Max(cap, FDGoodwillTuning.GrudgeCapFloor);
        }

        /// <summary>
        /// Vanilla prints this line in the Factions tab's goodwill explanation. The base returns the
        /// bare def label; showing the number it is actually enforcing turns that pane into the
        /// mod's own diagnostic readout, which is worth more than a tidy label.
        /// </summary>
        public override string GetPostProcessedLabel(Faction other)
        {
            if (!FDGoodwillTuning.Applies(other, out FactionRuntimeData data)) return def.label;

            return "FD_GoodwillGrudge".Translate(
                data.grudge.ToStringPercent(),
                CapFor(data.grudge).ToString());
        }
    }

    /// <summary>
    /// "Desperate." Hardship drags natural goodwill down.
    ///
    /// This one offsets rather than caps, and the difference is the argument for it. A starving
    /// neighbour has not done anything to the player and the player has not done anything to them;
    /// what has changed is that the faction is in no state to be generous, and FD already treats
    /// that as the reason they come raiding. So relations *drift* colder - the player can still pull
    /// them all the way back with gifts and trade, they just have to keep doing it while the
    /// famine lasts. A cap here would punish the player for someone else's bad winter.
    /// </summary>
    public class FDGoodwillSituationWorker_Hardship : GoodwillSituationWorker
    {
        public override int GetNaturalGoodwillOffset(Faction other)
        {
            if (!FDGoodwillTuning.Applies(other, out FactionRuntimeData data)) return 0;

            return OffsetFor(data.hardship);
        }

        public static int OffsetFor(float hardship)
        {
            if (hardship < FDGoodwillTuning.HardshipOffsetThreshold) return 0;

            float t = Mathf.InverseLerp(FDGoodwillTuning.HardshipOffsetThreshold, 1f, hardship);
            return Mathf.RoundToInt(Mathf.Lerp(0, FDGoodwillTuning.HardshipOffsetAtMax, t));
        }

        public override string GetPostProcessedLabel(Faction other)
        {
            if (!FDGoodwillTuning.Applies(other, out FactionRuntimeData data)) return def.label;

            return "FD_GoodwillHardship".Translate(
                data.hardship.ToStringPercent(),
                OffsetFor(data.hardship).ToString());
        }
    }
}
