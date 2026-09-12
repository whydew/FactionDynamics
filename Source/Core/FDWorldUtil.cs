using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>World-map helpers shared by the settlement and raid systems.</summary>
    public static class FDWorldUtil
    {
        /// <summary>Treated as "effectively on the other side of the planet".</summary>
        public const float UnreachableDistance = 9999f;

        /// <summary>
        /// Cheap geometric distance in tiles. Returns <see cref="UnreachableDistance"/> when the
        /// tiles are on different planet layers (1.6 / Odyssey: orbit vs surface), because a
        /// straight-line number across layers would be meaningless.
        ///
        /// This deliberately uses ApproxDistanceInTiles rather than TraversalDistanceBetween:
        /// traversal distance runs a graph search, and we call this for every settlement of every
        /// faction on every raid. Approximate distance is the right trade for gameplay weighting.
        /// </summary>
        public static float TileDistance(PlanetTile a, PlanetTile b)
        {
            if (!a.Valid || !b.Valid) return UnreachableDistance;
            if (a.Layer != b.Layer) return UnreachableDistance;
            return Find.WorldGrid.ApproxDistanceInTiles(a, b);
        }

        /// <summary>Player home tiles, in a deterministic order (map creation order).</summary>
        public static List<PlanetTile> PlayerHomeTiles()
        {
            var tiles = new List<PlanetTile>();
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                if (map != null && map.IsPlayerHome && map.Tile.Valid)
                    tiles.Add(map.Tile);
            }
            return tiles;
        }

        /// <summary>Distance from a tile to the closest player home map. UnreachableDistance if none.</summary>
        public static float DistanceToNearestPlayerHome(PlanetTile tile)
        {
            List<PlanetTile> homes = PlayerHomeTiles();
            float best = UnreachableDistance;
            for (int i = 0; i < homes.Count; i++)
            {
                float d = TileDistance(tile, homes[i]);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>
        /// True if it is unsafe for this mod to remove or heavily alter this settlement: the player
        /// is standing on it, is travelling to it, or a quest depends on it.
        /// </summary>
        public static bool IsProtected(Settlement settlement)
        {
            if (settlement == null || settlement.Destroyed) return true;

            // The player is physically there.
            if (settlement.HasMap) return true;

            // A player caravan is on it or heading for it.
            List<Caravan> caravans = Find.WorldObjects.Caravans;
            for (int i = 0; i < caravans.Count; i++)
            {
                Caravan caravan = caravans[i];
                if (caravan == null || !caravan.IsPlayerControlled) continue;
                if (caravan.Tile == settlement.Tile) return true;
                if (caravan.pather != null && caravan.pather.Moving && caravan.pather.Destination == settlement.Tile)
                    return true;
            }

            // A live quest points at it. Removing a quest's target mid-quest breaks the quest,
            // and in a Multiplayer session that is a very visible failure.
            if (QuestDependsOn(settlement)) return true;

            return false;
        }

        /// <summary>
        /// Does a live quest point at this settlement? Public because the garrison thinner needs the
        /// same question answered at map-gen time, where <see cref="IsProtected"/> cannot be used -
        /// that method returns true for anything with a map, which is every settlement we are about
        /// to walk into.
        /// </summary>
        public static bool QuestDependsOn(Settlement settlement)
        {
            List<Quest> quests = Find.QuestManager.QuestsListForReading;
            for (int i = 0; i < quests.Count; i++)
            {
                Quest quest = quests[i];
                if (quest == null) continue;
                if (quest.State != QuestState.Ongoing && quest.State != QuestState.NotYetAccepted) continue;

                foreach (GlobalTargetInfo target in quest.QuestLookTargets)
                {
                    if (target.HasWorldObject && target.WorldObject == settlement)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Settlements belonging to a faction, in a stable (ID) order.</summary>
        public static List<Settlement> SettlementsOf(Faction faction)
        {
            var result = new List<Settlement>();
            if (faction == null) return result;

            List<Settlement> all = Find.WorldObjects.Settlements;
            for (int i = 0; i < all.Count; i++)
            {
                Settlement s = all[i];
                if (s != null && !s.Destroyed && s.Faction == faction)
                    result.Add(s);
            }
            result.Sort((a, b) => a.ID.CompareTo(b.ID));
            return result;
        }

        /// <summary>Sends a vanilla letter, if the event is close enough to the player to care about.</summary>
        public static void SendNearbyLetter(string label, string text, LetterDef def, Settlement about)
        {
            FactionDynamicsSettings settings = FactionDynamicsMod.Settings;
            if (settings == null || !settings.settlementLetters) return;
            if (about == null) return;

            float dist = DistanceToNearestPlayerHome(about.Tile);
            if (dist > settings.settlementLetterRadius) return;

            Find.LetterStack.ReceiveLetter(label, text, def ?? LetterDefOf.NeutralEvent,
                new LookTargets(new GlobalTargetInfo(about)));
        }
    }
}
