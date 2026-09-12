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

        // Removed: lootPhaseStartTicks. A raid with a loot motivation now starts in its loot toil
        // rather than opening with a timed assault phase - see LordJob_FDMotivatedRaid.CreateGraph.
        // The delay was the reason starving raiders shot colonists on sight for the first fifteen
        // seconds of every raid.

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

        /// <summary>
        /// The envelope severity can fall in. Where inside it a given raid lands is decided by the
        /// origin settlement's hardship - see <see cref="pawnHediffHardshipWeight"/>.
        /// </summary>
        public FloatRange pawnHediffSeverity = new FloatRange(0.2f, 0.45f);

        /// <summary>
        /// How strongly the origin settlement's own hardship steers severity inside
        /// <see cref="pawnHediffSeverity"/>.
        ///
        /// 1 = hardship picks the centre outright: a raid from a town at 90% hardship arrives near
        /// the top of the range, one from a town at 20% near the bottom. 0 = ignore hardship and
        /// centre on the middle of the range, which is the old flat-random behaviour.
        ///
        /// This is what makes the hardship figure mean something you can see. Before it, a raid
        /// from a desperate settlement and a raid from a comfortable one produced identically
        /// starved pawns, and the number on the world map was disconnected from the people it
        /// supposedly described.
        /// </summary>
        public float pawnHediffHardshipWeight = 1f;

        /// <summary>
        /// Per-pawn spread either side of the hardship-derived centre, so raiders in one group are
        /// not all stamped with the same severity.
        /// </summary>
        public float pawnHediffJitter = 0.12f;

        /// <summary>
        /// Chance that ONE raider in the group is someone the faction should not have sent - too
        /// far gone to stand up. At most one per raid, deliberately.
        ///
        /// Severity in the extreme band caps Consciousness at 0.1, and CanBeAwake needs 0.3, so
        /// this pawn collapses on arrival. As a rare beat that reads as desperation; as a common
        /// one it would just look broken, which is why it is capped at a single raider and why the
        /// ordinary severity range stops short of the extreme stage.
        /// </summary>
        public float collapsedRaiderChance;

        /// <summary>Severity used for that one collapsed raider. Kept clear of the 1.0 death line.</summary>
        public FloatRange collapsedRaiderSeverity = new FloatRange(0.82f, 0.95f);

        /// <summary>Food need set on arrival, as a 0-1 fraction. Negative = leave it alone.</summary>
        public FloatRange pawnFoodLevel = new FloatRange(-1f, -1f);

        /// <summary>
        /// Strip every scrap of food out of the raiders' inventories on arrival.
        ///
        /// Vanilla's pawn generator hands raiders travel rations - usually pemmican - which makes
        /// a starvation raid absurd on inspection: an empty-bellied, malnourished raider showing up
        /// with three days of food in his pack. It also quietly broke the loot goal, since the
        /// rations counted toward the nutrition the raid was trying to collect, so they arrived
        /// part of the way to "we have enough" and could turn round early.
        /// </summary>
        public bool arriveWithoutFood;

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
