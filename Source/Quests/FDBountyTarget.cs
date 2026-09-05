using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace FactionDynamics
{
    [DefOf]
    public static class FDQuestScriptDefOf
    {
        public static QuestScriptDef FD_HighProfileBounty;

        static FDQuestScriptDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDQuestScriptDefOf));
        }
    }

    /// <summary>
    /// Chooses who the bounty is on.
    ///
    /// Two ways in: the quest may be generated with a target already in the slate - that is what the
    /// "somebody with a price on their head just walked onto your map" path does - or, with no
    /// target supplied, it picks a notable pawn out in the world. Doing both in one node keeps the
    /// XML identical for either route.
    /// </summary>
    public class QuestNode_FDBountyTarget : QuestNode
    {
        public SlateRef<string> storeAs = "target";

        protected override bool TestRunInt(Slate slate)
        {
            string key = storeAs.GetValue(slate) ?? "target";
            if (slate.Exists(key)) return true;
            return FindWorldTarget(out _);
        }

        protected override void RunInt()
        {
            Slate slate = QuestGen.slate;
            string key = storeAs.GetValue(slate) ?? "target";

            Pawn target = slate.Get<Pawn>(key, null);

            if (target == null)
            {
                if (!FindWorldTarget(out target)) return;
                slate.Set(key, target);
            }

            // Plain strings for the quest text - safer than assuming a text rule exists for
            // every property we want to mention.
            slate.Set("targetName", target.LabelShortCap);
            slate.Set("targetFactionName", target.Faction != null ? target.Faction.Name : "no faction");

            FDLog.Debug("Bounty target: " + target.LabelShortCap
                        + " of " + (target.Faction?.Name ?? "no faction")
                        + (target.Spawned ? " (currently on a player map)" : " (out in the world)"));
        }

        /// <summary>
        /// A notable pawn somewhere in the world: named, alive, free, and belonging to a faction.
        /// Hostile factions and leaders are weighted up, because a bounty on a nobody is a
        /// non-story.
        /// </summary>
        public static bool FindWorldTarget(out Pawn chosen)
        {
            chosen = null;

            Faction player = Faction.OfPlayerSilentFail;
            if (player == null || Find.WorldPawns == null) return false;

            List<Pawn> candidates = new List<Pawn>();
            List<float> weights = new List<float>();
            float total = 0f;

            List<Pawn> alive = Find.WorldPawns.AllPawnsAlive;
            for (int i = 0; i < alive.Count; i++)
            {
                Pawn p = alive[i];
                if (!IsValidTarget(p, player)) continue;

                float w = 1f;
                if (p.Faction.HostileTo(player)) w *= 3f;
                if (p.Faction.leader == p) w *= 2.5f;

                candidates.Add(p);
                weights.Add(w);
                total += w;
            }

            if (candidates.Count == 0 || total <= 0f) return false;

            // Deterministic order before the weighted roll.
            for (int i = 1; i < candidates.Count; i++)
            {
                Pawn p = candidates[i];
                float w = weights[i];
                int j = i - 1;
                while (j >= 0 && candidates[j].thingIDNumber > p.thingIDNumber)
                {
                    candidates[j + 1] = candidates[j];
                    weights[j + 1] = weights[j];
                    j--;
                }
                candidates[j + 1] = p;
                weights[j + 1] = w;
            }

            float roll = Rand.Value * total;
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0f)
                {
                    chosen = candidates[i];
                    return true;
                }
            }

            chosen = candidates[candidates.Count - 1];
            return true;
        }

        public static bool IsValidTarget(Pawn p, Faction player)
        {
            if (p == null || p.Dead || p.Destroyed) return false;
            if (p.RaceProps == null || !p.RaceProps.Humanlike) return false;
            if (p.Faction == null || p.Faction.IsPlayer) return false;
            if (p.Faction.Hidden) return false;
            if (p.IsPrisoner || p.IsSlave) return false;
            if (p.Name == null) return false;
            return true;
        }
    }

    /// <summary>
    /// Watches for people walking onto the player's maps with a price on their head.
    ///
    /// Any humanlike from another faction who shows up - a raider, a visitor, a trader's guard -
    /// has a small chance of turning out to be wanted somewhere, and a much larger one if they lead
    /// their faction. Each pawn is only ever considered once per map, so nobody gets re-rolled every
    /// time they wander back through.
    /// </summary>
    public class MapComponent_FDBountyWatcher : MapComponent
    {
        private const int CheckIntervalTicks = 2500;

        private List<int> consideredIds = new List<int>();
        private HashSet<int> consideredLookup = new HashSet<int>();

        public MapComponent_FDBountyWatcher(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            int now = Find.TickManager.TicksGame;
            if (now % CheckIntervalTicks != 0) return;

            FDSimConfig cfg = FactionDynamicsWorldComp.Config;
            if (!cfg.questsBounties) return;
            if (cfg.bountySightingChance <= 0f && cfg.bountySightingLeaderChance <= 0f) return;
            if (!map.IsPlayerHome) return;

            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) return;

            // Deterministic order - mapPawns order is not guaranteed to match between clients.
            List<Pawn> spawned = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            spawned.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));

            for (int i = 0; i < spawned.Count; i++)
            {
                Pawn pawn = spawned[i];
                if (!QuestNode_FDBountyTarget.IsValidTarget(pawn, player)) continue;
                if (consideredLookup.Contains(pawn.thingIDNumber)) continue;

                consideredLookup.Add(pawn.thingIDNumber);
                consideredIds.Add(pawn.thingIDNumber);

                bool isLeader = pawn.Faction.leader == pawn;
                float chance = isLeader ? cfg.bountySightingLeaderChance : cfg.bountySightingChance;
                if (pawn.Faction.HostileTo(player)) chance *= 1.5f;

                bool hit;
                using (FDRand.Push(pawn.thingIDNumber, now, FDRandSalt.QuestBounty))
                {
                    hit = Rand.Chance(UnityEngine.Mathf.Clamp01(chance));
                }

                if (hit)
                    TryGenerateBounty(pawn, isLeader);
            }
        }

        private void TryGenerateBounty(Pawn pawn, bool isLeader)
        {
            QuestScriptDef def = FDQuestScriptDefOf.FD_HighProfileBounty;
            if (def == null) return;

            try
            {
                var slate = new Slate();
                slate.Set("target", pawn);
                slate.Set("points", StorytellerUtility.DefaultThreatPointsNow(Find.World));

                Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(def, slate);
                if (quest == null) return;

                QuestUtility.SendLetterQuestAvailable(quest, "FD_BountySighting");

                FDLog.Debug("Bounty raised on " + pawn.LabelShortCap
                            + (isLeader ? " (faction leader)" : "") + " after being spotted on "
                            + map.Parent?.Label + ".");
            }
            catch (System.Exception e)
            {
                FDLog.Warning("Could not raise a bounty on " + pawn.LabelShortCap + ": " + e.Message);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref consideredIds, "consideredIds", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                consideredIds ??= new List<int>();
                consideredLookup = new HashSet<int>(consideredIds);
            }
        }
    }
}
