using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace FactionDynamics
{
    /// <summary>
    /// The loot phase of a motivated raid: every raider switches to the motivation's loot duty,
    /// which sends them after the category of goods they came for and then off the map.
    ///
    /// Deliberately simpler than vanilla's opportunistic steal-cover toil, which only peels off
    /// individual pawns: when a starving faction decides it has the colony's food store in reach,
    /// the whole group commits, which is what makes the behaviour readable to the player.
    /// </summary>
    public class LordToil_FDLoot : LordToil
    {
        private readonly DutyDef lootDuty;

        public LordToil_FDLoot(DutyDef lootDuty)
        {
            this.lootDuty = lootDuty;
        }

        public override void UpdateAllDuties()
        {
            if (lootDuty == null) return;

            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                Pawn pawn = lord.ownedPawns[i];
                if (pawn?.mindState == null) continue;
                pawn.mindState.duty = new PawnDuty(lootDuty);
            }
        }
    }
}
