using RimWorld;
using Verse;
using Verse.AI;

namespace FactionDynamics
{
    /// <summary>
    /// Category-filtered version of vanilla's steal search.
    ///
    /// Vanilla's <see cref="StealAIUtility.TryFindBestItemToSteal"/> can only filter on the binary
    /// <c>ThingDef.stealable</c> flag - there is no way to say "only food" or "only valuables". This
    /// reimplements the same region-wise prioritised search with a category predicate, so a
    /// starving raider walks past the gold and goes for the meals.
    /// </summary>
    public static class FDStealUtility
    {
        /// <summary>Anything below this market value isn't worth a raider's time on a plunder run.</summary>
        private const float MinValuableMarketValue = 8f;

        public static Thing FindBestLoot(Pawn pawn, FDLootCategory category, float maxDist)
        {
            if (pawn == null || pawn.Map == null) return null;
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return null;

            return GenClosest.ClosestThing_Regionwise_ReachablePrioritized(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForGroup(ThingRequestGroup.HaulableEverOrMinifiable),
                PathEndMode.ClosestTouch,
                TraverseParms.For(pawn),
                maxDist,
                t => IsValidLoot(pawn, t, category),
                t => LootValue(t, category),
                15,
                15,
                false);
        }

        public static bool IsValidLoot(Pawn pawn, Thing t, FDLootCategory category)
        {
            if (t == null || t.def == null) return false;
            if (!t.def.stealable) return false;
            if (t.IsBurning()) return false;
            if (!MatchesCategory(t, category)) return false;
            if (!pawn.CanReserve(t)) return false;
            return true;
        }

        public static bool MatchesCategory(Thing t, FDLootCategory category)
        {
            switch (category)
            {
                case FDLootCategory.Food:
                    // Nutrition the raiders can actually carry home. Corpses and live animals are
                    // excluded by the haulable request group already.
                    return t.def.IsNutritionGivingIngestible;

                case FDLootCategory.Valuables:
                    if (t.def.IsNutritionGivingIngestible) return false; // that's the other motivation's job
                    return t.MarketValue >= MinValuableMarketValue;

                default:
                    return true;
            }
        }

        /// <summary>
        /// How much this thing counts toward the raid's goal: nutrition for a food raid, silver
        /// value for a plunder raid. Always the whole stack, since that's what gets carried.
        /// </summary>
        public static float LootValue(Thing t, FDLootCategory category)
        {
            if (t == null) return 0f;

            int count = UnityEngine.Mathf.Max(1, t.stackCount);

            if (category == FDLootCategory.Food)
            {
                if (!t.def.IsNutritionGivingIngestible) return 0f;
                return t.GetStatValue(StatDefOf.Nutrition) * count;
            }

            return t.MarketValue * count;
        }
    }
}
