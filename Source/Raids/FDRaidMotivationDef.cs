using RimWorld;
using Verse;
using Verse.AI;

namespace FactionDynamics
{
    /// <summary>What a motivated raid is trying to carry off the map, if anything.</summary>
    public enum FDLootCategory : byte
    {
        None = 0,
        Food = 1,
        Valuables = 2
    }

    /// <summary>
    /// A reason raiders came. Data-driven so new motivations are an XML file, not a code change.
    ///
    /// A motivation controls three things: what the raiders target (<see cref="lootCategory"/> +
    /// <see cref="lootDuty"/>), when they decide they are done (<see cref="lootGoalPerRaider"/>),
    /// and when they give up (<see cref="fleeAtLossFraction"/>, <see cref="timeoutTicks"/>).
    /// </summary>
    public class FDRaidMotivationDef : Def
    {
        /// <summary>Relative chance of being picked, before availability checks.</summary>
        public float weight = 1f;

        /// <summary>What the raiders want to steal. None = they are not here for loot at all.</summary>
        public FDLootCategory lootCategory = FDLootCategory.None;

        /// <summary>Duty assigned during the loot phase. Required unless lootCategory is None.</summary>
        public DutyDef lootDuty;

        /// <summary>
        /// How much loot (nutrition for Food, market value for Valuables) each raider needs to
        /// have carried before the group calls it a success and walks off the map.
        /// </summary>
        public float lootGoalPerRaider;

        /// <summary>Fraction of the group that has to be lost before they break off.</summary>
        public float fleeAtLossFraction = 0.4f;

        /// <summary>Hard "we're done here" timer, as vanilla assault raids have.</summary>
        public IntRange timeoutTicks = new IntRange(20000, 30000);

        /// <summary>Ticks of straight fighting before they switch to looting. 0 = loot immediately.</summary>
        public int lootPhaseStartTicks = 1500;

        /// <summary>Raid size multiplier - desperate raids are usually smaller, revenge raids bigger.</summary>
        public float pointsFactor = 1f;

        /// <summary>Whether raiders with this motivation will drag colonists away.</summary>
        public bool canKidnap;

        /// <summary>Whether the vanilla "we've done enough damage, leave" satisfaction check applies.</summary>
        public bool useColonyDamageSatisfaction;

        // ---- availability: a motivation only fires when the faction is actually in that state ----

        /// <summary>Faction hardship (settlement losses, strain) needed for this motivation.</summary>
        public float minHardship;

        /// <summary>Faction grudge against the player needed for this motivation.</summary>
        public float minGrudge;

        // ---- what the raiders themselves look like when they show up ----

        /// <summary>
        /// Hediff applied to every raider on arrival, e.g. Malnutrition for a starvation raid. This
        /// is what makes the motivation visible on the pawns rather than only in the letter: a
        /// starving raider should be visibly starving when you check their health tab.
        /// </summary>
        public HediffDef pawnHediff;

        public FloatRange pawnHediffSeverity = new FloatRange(0.2f, 0.45f);

        /// <summary>Food need set on arrival, as a 0-1 fraction. Negative = leave it alone.</summary>
        public FloatRange pawnFoodLevel = new FloatRange(-1f, -1f);

        /// <summary>Translation key appended to the raid letter, explaining why they came.</summary>
        public string letterSuffixKey;

        /// <summary>Settings toggle this motivation is bound to, resolved in code.</summary>
        public string settingKey;

        public override System.Collections.Generic.IEnumerable<string> ConfigErrors()
        {
            foreach (string err in base.ConfigErrors())
                yield return err;

            if (lootCategory != FDLootCategory.None && lootDuty == null)
                yield return "lootCategory is set but lootDuty is null - raiders would have nothing to do.";

            if (lootCategory != FDLootCategory.None && lootGoalPerRaider <= 0f)
                yield return "lootCategory is set but lootGoalPerRaider is 0 - raiders would leave instantly.";
        }
    }

    [DefOf]
    public static class FDRaidMotivationDefOf
    {
        public static FDRaidMotivationDef FD_Starvation;
        public static FDRaidMotivationDef FD_Plunder;
        public static FDRaidMotivationDef FD_Revenge;

        static FDRaidMotivationDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDRaidMotivationDefOf));
        }
    }

    [DefOf]
    public static class FDDutyDefOf
    {
        public static DutyDef FD_LootFood;
        public static DutyDef FD_LootValuables;

        static FDDutyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDDutyDefOf));
        }
    }

    [DefOf]
    public static class FDRaidStrategyDefOf
    {
        public static RimWorld.RaidStrategyDef FD_MotivatedAssault;

        static FDRaidStrategyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDRaidStrategyDefOf));
        }
    }
}
