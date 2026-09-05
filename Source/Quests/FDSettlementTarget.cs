using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Finds a real, existing hostile settlement the player could realistically go and destroy.
    ///
    /// This is the difference between "go clear a bandit camp we invented for you" and "go take
    /// that base off the map" - hitting a real settlement removes it from the world permanently and
    /// costs its faction their actual territory, which is the whole point of the mod.
    /// </summary>
    public static class FDSettlementTargeting
    {
        /// <summary>
        /// Nearest hostile settlement within range of any player colony, or null. Deterministic and
        /// free of RNG so it is safe to call from a quest's TestRun (which the game calls
        /// speculatively, and repeatedly).
        /// </summary>
        public static Settlement FindTarget(float maxTiles, out float distance)
        {
            distance = FDWorldUtil.UnreachableDistance;

            List<PlanetTile> homes = FDWorldUtil.PlayerHomeTiles();
            if (homes.Count == 0) return null;

            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) return null;

            Settlement best = null;
            float bestDist = float.MaxValue;

            List<Settlement> all = Find.WorldObjects.Settlements;
            for (int i = 0; i < all.Count; i++)
            {
                Settlement s = all[i];
                if (s == null || s.Destroyed) continue;
                if (s.Faction == null || s.Faction.IsPlayer) continue;
                if (!s.Faction.HostileTo(player)) continue;

                // Don't offer a target the player is already standing in, travelling to, or that
                // another quest depends on.
                if (FDWorldUtil.IsProtected(s)) continue;

                for (int h = 0; h < homes.Count; h++)
                {
                    float d = FDWorldUtil.TileDistance(s.Tile, homes[h]);
                    if (d > maxTiles) continue;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = s;
                    }
                }
            }

            if (best != null) distance = bestDist;
            return best;
        }
    }

    /// <summary>
    /// Picks a real hostile settlement as the quest's objective and wires up the part that watches
    /// for it being destroyed. If there is no valid target the quest cannot run at all - the
    /// generated-outpost version of the quest covers that case instead.
    /// </summary>
    public class QuestNode_FDPickSettlementTarget : QuestNode
    {
        public SlateRef<float> maxTiles = 90f;
        public SlateRef<string> storeSettlementAs = "targetSettlement";
        public SlateRef<string> storeFactionAs = "targetFaction";
        public SlateRef<string> storeTileAs = "targetTile";

        /// <summary>
        /// Plain (unscoped) signal name. QuestNode_Signal in XML applies the same quest-id scoping
        /// we apply here, so both sides end up with the identical string.
        /// </summary>
        public const string DefeatedSignal = "fdSettlementDefeated";

        protected override bool TestRunInt(Slate slate)
        {
            return FDSettlementTargeting.FindTarget(maxTiles.GetValue(slate), out _) != null;
        }

        protected override void RunInt()
        {
            Slate slate = QuestGen.slate;
            Quest quest = QuestGen.quest;

            Settlement target = FDSettlementTargeting.FindTarget(maxTiles.GetValue(slate), out float distance);
            if (target == null) return;

            slate.Set(storeSettlementAs.GetValue(slate), target);
            slate.Set(storeFactionAs.GetValue(slate), target.Faction);
            slate.Set(storeTileAs.GetValue(slate), target.Tile);
            slate.Set("targetSettlementDistance", Mathf(distance));
            // Plain string for the quest text: a WorldObject in the slate has no guaranteed
            // "_label" text rule the way a Faction has "_name", so don't rely on one.
            slate.Set("targetSettlementName", target.Label);

            quest.AddPart(new QuestPart_FDSettlementDefeated
            {
                settlement = target,
                outSignal = QuestGenUtility.HardcodedSignalWithQuestID(DefeatedSignal)
            });

            FDLog.Debug("Quest target settlement: " + target.Label + " (" + target.Faction?.Name
                        + ") at " + distance.ToString("F0") + " tiles.");
        }

        private static int Mathf(float f) => UnityEngine.Mathf.RoundToInt(f);
    }

    /// <summary>
    /// Fires its signal when the target settlement leaves the world - which is what happens when
    /// the player defeats it. Vanilla has no quest node for "this existing settlement was
    /// destroyed", so the watcher below tells this part when it happens.
    /// </summary>
    public class QuestPart_FDSettlementDefeated : QuestPart
    {
        public Settlement settlement;
        public string outSignal;

        public override IEnumerable<GlobalTargetInfo> QuestLookTargets
        {
            get
            {
                foreach (GlobalTargetInfo t in base.QuestLookTargets)
                    yield return t;

                if (settlement != null && !settlement.Destroyed)
                    yield return settlement;
            }
        }

        public void Notify_SettlementGone(Settlement gone)
        {
            if (gone == null || gone != settlement) return;
            if (outSignal.NullOrEmpty()) return;
            if (quest == null || quest.State != QuestState.Ongoing) return;

            FDLog.Debug("Quest target " + gone.Label + " destroyed - firing " + outSignal);
            Find.SignalManager.SendSignal(new Signal(outSignal));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref settlement, "settlement");
            Scribe_Values.Look(ref outSignal, "outSignal");
        }
    }

    /// <summary>
    /// Watches world-object removal so settlement-target quests can complete. Every route to a
    /// settlement leaving the world - player assault, our own lifecycle collapse, another mod -
    /// goes through WorldObjectsHolder.Remove, which makes this the one reliable place to hook.
    /// </summary>
    [HarmonyPatch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove))]
    public static class Patch_WorldObjectRemoved
    {
        [HarmonyPostfix]
        public static void Postfix(WorldObject o)
        {
            if (!(o is Settlement settlement)) return;
            if (Find.QuestManager == null) return;

            List<Quest> quests = Find.QuestManager.QuestsListForReading;
            for (int i = 0; i < quests.Count; i++)
            {
                Quest quest = quests[i];
                if (quest == null || quest.State != QuestState.Ongoing) continue;

                List<QuestPart> parts = quest.PartsListForReading;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (parts[j] is QuestPart_FDSettlementDefeated part)
                        part.Notify_SettlementGone(settlement);
                }
            }
        }
    }
}
