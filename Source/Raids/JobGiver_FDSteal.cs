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
        public float searchRadius = 30f;

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn == null || pawn.Map == null) return null;
            if (GenAI.InDangerousCombat(pawn)) return null;

            if (!RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot, TraverseMode.ByPawn, false))
                return null;

            Thing item = FDStealUtility.FindBestLoot(pawn, lootCategory, searchRadius);
            if (item == null) return null;

            Job job = JobMaker.MakeJob(JobDefOf.Steal);
            job.targetA = item;
            job.targetB = exitSpot;
            job.count = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.Min(
                item.stackCount,
                (int)(pawn.GetStatValue(StatDefOf.CarryingCapacity)
                      / UnityEngine.Mathf.Max(0.01f, item.def.VolumePerUnit))));
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
