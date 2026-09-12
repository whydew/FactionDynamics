using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FactionDynamics
{
    /// <summary>
    /// Makes a looting raider treat a shut door as a last resort rather than a shortcut.
    ///
    /// The problem, in vanilla's own numbers. PathUtility.GetDoorCost prices a door the pawn cannot
    /// open at PathFinderCostTuning.Cost_BlockedDoor (50) plus Cost_BlockedDoorPerHitPoint (0.2)
    /// per hit point. A wooden door is about 150HP, so smashing it costs 80; a steel one about
    /// 280HP, so 106. A plain walking tile costs 13. That means breaking a door is priced at
    /// roughly SIX TILES of walking - so the moment going around costs more than a seven-tile
    /// detour, the pathfinder happily puts a boot through the door instead.
    ///
    /// For an assault raid that is correct and intended. For a theft it is not: these raiders are
    /// supposed to slip in for the food and slip out, and wrecking every door between the map edge
    /// and the freezer reads as an assault with extra steps.
    ///
    /// Note this is the ONLY lever that works. Pawn_PathFollower bashes any blocked door it walks
    /// into when pawn.HostileTo(building) - it does not consult Job.canBashDoors for hostiles at
    /// all - so bashing cannot be switched off once a raider is standing at a closed door. The only
    /// way to stop it is to stop the path from going there, which means making the door expensive.
    ///
    /// The inflated cost stays clear of 0xffff, which the pathfinder reads as impassable, so a door
    /// that is genuinely the only way in is still broken down. "Absolutely necessary" here means
    /// "there is no other route within 2000 tiles of walking" - which on any real map means no
    /// other route at all.
    /// </summary>
    [HarmonyPatch(typeof(PathUtility), nameof(PathUtility.GetDoorCost))]
    public static class Patch_LootDoorCost
    {
        /// <summary>
        /// How far out of their way a thief will walk to avoid breaking a door.
        ///
        /// The pathfinder costs each cardinal step at PathFinderJob.moveTicksCardinal, which is
        /// Pawn.TicksPerMoveCardinal - 13 for a baseline human. So the door cost below is simply
        /// this many tiles of walking, expressed in the pathfinder's own units.
        ///
        /// This was briefly set to 2000 and that broke the raids outright. Raiders cannot open
        /// player doors at all - Building_Door.PawnCanOpen ends at GenAI.MachinesLike(door.Faction,
        /// pawn), which is false for a hostile - so EVERY door in a colony is a "blocked door" to
        /// them and gets this cost. At 26,000 per door the A* heuristic (straight-line distance
        /// times 13) underestimates the true cost so badly that the search expands a colossal
        /// frontier hunting for a cheaper way in, up against PathFinder's 500,000 node SearchLimit.
        /// Paths failed, the job ended, the think tree reissued it, and the raid stuttered on the
        /// spot until the give-up timer sent it home.
        ///
        /// 30 tiles is about five times vanilla's ~6, which is enough to make an open corridor
        /// clearly preferable to smashing a side door, without wrecking the pathfinder when doors
        /// are genuinely unavoidable.
        /// </summary>
        private const int DetourTilesTolerated = 30;

        /// <summary>Ticks per cardinal step for a baseline human, the pathfinder's cost unit.</summary>
        private const int TicksPerTile = 13;

        /// <summary>
        /// 390 - thirty tiles of walking at thirteen ticks each. Comfortably under the 65535 ushort
        /// ceiling and nowhere near the 0xffff sentinel, so the door stays passable as a last resort
        /// rather than becoming a wall, and small enough that A*'s straight-line heuristic is not
        /// wildly optimistic about it. (This comment used to say 26000, describing the 2000-tile
        /// version that made the pathfinder explode against its 500,000-node search limit.)
        /// </summary>
        private const int DetourCost = DetourTilesTolerated * TicksPerTile;

        /// <summary>Vanilla's "this door is impassable" sentinel - never touch a door already at it.</summary>
        private const ushort Impassable = 65535;

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ushort __result)
        {
            // 0 means the pawn can just open it - nothing to discourage.
            if (__result == 0 || __result >= Impassable) return;
            if (pawn?.mindState?.duty?.def == null) return;
            if (!FDLootDuties.IsLootDuty(pawn.mindState.duty.def)) return;

            // Flat detour cost plus the original, so a plasteel door still reads as worse than a
            // wooden one when two of them sit on the only way in. Both are last resorts either way.
            int inflated = DetourCost + __result;
            __result = inflated >= Impassable ? (ushort)(Impassable - 1) : (ushort)inflated;
        }
    }

    /// <summary>
    /// Identifies the mod's loot duties without hard-coding string comparisons at every call site.
    /// Resolved lazily rather than through DefOf, because these are the mod's own defs and a
    /// DefOf failure at startup would take the whole mod down over a cosmetic pathing tweak.
    /// </summary>
    public static class FDLootDuties
    {
        private static DutyDef lootFood;
        private static DutyDef lootValuables;
        private static bool resolved;

        public static bool IsLootDuty(DutyDef def)
        {
            if (def == null) return false;

            if (!resolved)
            {
                lootFood = DefDatabase<DutyDef>.GetNamedSilentFail("FD_LootFood");
                lootValuables = DefDatabase<DutyDef>.GetNamedSilentFail("FD_LootValuables");
                resolved = true;
            }

            return def == lootFood || def == lootValuables;
        }
    }
}
