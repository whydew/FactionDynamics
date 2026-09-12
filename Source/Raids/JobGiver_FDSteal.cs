using RimWorld;
using Verse;
using Verse.AI;

namespace FactionDynamics
{
    /// <summary>
    /// Same shape as vanilla's <c>JobGiver_Steal</c>, but it looks for a specific category of loot.
    /// Referenced from our loot DutyDefs in XML, where the category is set per duty.
    /// </summary>
    public class JobGiver_FDSteal : ThinkNode_JobGiver
    {
        public FDLootCategory lootCategory = FDLootCategory.Valuables;

        /// <summary>
        /// How far a raider will go for loot. Effectively "the whole map" by default: a raid that
        /// crossed the planet because its people are starving is not going to give up because your
        /// larder is sixty tiles from where it walked in. The old 40-tile cap meant raiders landing
        /// on the far edge never saw the colony at all.
        /// </summary>
        public float searchRadius = 9999f;

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn == null || pawn.Map == null) return null;

            // Deliberately NO GenAI.InDangerousCombat check here, though vanilla's JobGiver_Steal
            // has one. Vanilla's thief is one pawn opportunistically pocketing something during
            // someone else's fight; ours is the entire raid, and the point of the motivation is
            // that they keep taking food with a colonist standing right there. Bailing out when an
            // enemy came within 12 tiles would drop them through to the exit node and send them
            // home the moment anyone walked over - without a shot being fired.
            //
            // Combat is decided above this node instead: the duty's ThinkNode_HarmedRecently
            // wrapper takes over the moment they are actually hurt, and nothing else in the tree
            // outranks looting.

            if (!RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot, TraverseMode.ByPawn, false))
                return null;

            Thing item = FDStealUtility.FindBestLoot(pawn, lootCategory, searchRadius,
                out bool needsForcedEntry);
            if (item == null)
            {
                // Worth seeing in the log: this is the difference between "the colony has no food"
                // and "the search cannot see the colony's food", and the two look identical from
                // the outside - the raid turns round and walks off the map either way.
                // Throttled so a raid heading for the exit doesn't fill the log on the way.
                if (Find.TickManager.TicksGame % 600 < 60)
                    FDLog.Debug(pawn.LabelShort + " found no " + lootCategory + " worth taking anywhere on the map.");
                return null;
            }

            Job job = JobMaker.MakeJob(JobDefOf.Steal);
            job.targetA = item;
            job.targetB = exitSpot;
            job.count = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.Min(
                item.stackCount,
                (int)(pawn.GetStatValue(StatDefOf.CarryingCapacity)
                      / UnityEngine.Mathf.Max(0.01f, item.def.VolumePerUnit))));

            // Make the steal interruptible.
            //
            // A Steal job runs to completion - walk to the item, pick it up, carry it to the map
            // edge - and RimWorld does not consult the duty's think tree again while a job is
            // running. That is why a raider shot halfway to the stockpile just kept walking: the
            // fight-back nodes in the duty were never reached. An expiry with checkOverrideOnExpire
            // makes the job tracker re-run the tree every two seconds and switch if it comes back
            // with something else - which, under fire, it now does.
            //
            // This does not restart the steal when nothing has changed: Pawn_JobTracker's
            // ShouldStartJobFromThinkTree keeps the current job when the new one has the same
            // JobDef, the driver reports it as a continuation (JobDriver_Steal does not override
            // the base, which returns true) and it came from the same think node. So the pawn
            // carries on hauling and only a genuinely different job - fighting - interrupts it.
            //
            // expireRequiresEnemiesNearby is deliberately left off: a raider being shot at long
            // range by a turret or a sniper has no enemy "nearby" by that check, and they are
            // exactly the case this is meant to fix.
            job.expiryInterval = 120;
            job.checkOverrideOnExpire = true;

            // The pathing has to agree with the search, and only where the search actually needed
            // it. FindBestLoot prefers loot reachable without touching a door and only falls back
            // to a door-locked target when there is nothing else on the map; needsForcedEntry says
            // which of those happened.
            //
            // When it is false these stay off, and that is what stops incidental door-breaking:
            // with canBashDoors false a shut door is impassable to the pathfinder, so the route
            // never runs through one and the raider never walks into a door to bash it. (That
            // matters because Pawn_PathFollower breaks any blocked door a hostile pawn is standing
            // at regardless of this flag - the only way to prevent it is to keep the path away.)
            //
            // When it is true, the door in the way IS the thing between them and the loot.
            job.canBashDoors = needsForcedEntry;
            job.canBashFences = needsForcedEntry;

            return job;
        }

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            var copy = (JobGiver_FDSteal)base.DeepCopy(resolve);
            copy.lootCategory = lootCategory;
            copy.searchRadius = searchRadius;
            return copy;
        }
    }
}
