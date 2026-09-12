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

        internal List<int> consideredIds = new List<int>();
        internal HashSet<int> consideredLookup = new HashSet<int>();

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

            PruneConsidered(spawned);

            int considered = 0;
            int raised = 0;
            int raisedThisPass = 0;
            float lowestChance = float.MaxValue;
            float highestChance = 0f;

            for (int i = 0; i < spawned.Count; i++)
            {
                Pawn pawn = spawned[i];
                if (!QuestNode_FDBountyTarget.IsValidTarget(pawn, player)) continue;
                if (consideredLookup.Contains(pawn.thingIDNumber)) continue;

                consideredLookup.Add(pawn.thingIDNumber);
                consideredIds.Add(pawn.thingIDNumber);
                considered++;

                bool isLeader = pawn.Faction.leader == pawn;
                float chance = isLeader ? cfg.bountySightingLeaderChance : cfg.bountySightingChance;
                if (pawn.Faction.HostileTo(player)) chance *= 1.5f;
                chance = UnityEngine.Mathf.Clamp01(chance);

                if (chance < lowestChance) lowestChance = chance;
                if (chance > highestChance) highestChance = chance;

                bool hit;
                using (FDRand.Push(pawn.thingIDNumber, now, FDRandSalt.QuestBounty))
                {
                    hit = Rand.Chance(chance);
                }

                if (hit)
                {
                    // One sighting per check, however many strangers walked in at once.
                    //
                    // The per-pawn roll is small, but it is rolled per pawn: a 198-raider plunder
                    // raid put 121 new faces on the map in a single check and handed the player four
                    // bounty quests in the same second. That reads as a bug even though every roll
                    // was fair. The rest of the group is simply not considered yet - they are left
                    // unmarked, so they can still be spotted on a later check.
                    TryGenerateBounty(pawn, isLeader);
                    raised++;
                    raisedThisPass++;

                    if (raisedThisPass >= MaxBountiesPerCheck) break;
                }
            }

            // Without this there is no way to tell a run of bad luck from the watcher never having
            // looked at anybody - and those need completely different fixes. The rate is reported as
            // the range actually rolled, because it varies per pawn (leader vs not, hostile vs not);
            // printing one hardcoded figure described a roll that never happened.
            if (considered > 0)
            {
                string rate = UnityEngine.Mathf.Approximately(lowestChance, highestChance)
                    ? lowestChance.ToStringPercent()
                    : lowestChance.ToStringPercent() + "-" + highestChance.ToStringPercent();

                FDLog.Debug("Bounty watch: considered " + considered + " new pawns on "
                            + (map.Parent?.Label ?? "map") + " at " + rate
                            + " each; raised " + raised + ".");
            }
        }

        /// <summary>Most bounty quests one check may hand the player at once.</summary>
        private const int MaxBountiesPerCheck = 1;

        /// <summary>
        /// Forgets people who are no longer anywhere on this map.
        ///
        /// The considered list is scribed, and without this it only ever grows: every stranger who
        /// has ever set foot on a home map stays in it for the life of the save, and the HashSet is
        /// rebuilt from the whole thing on every load. Dropping ids that no longer correspond to a
        /// pawn present keeps it proportional to the map rather than to the playthrough.
        ///
        /// Re-rolling someone who leaves and comes back years later is the intended trade: the point
        /// of the list is stopping a re-roll every 2500 ticks while they stand there, not remembering
        /// a passing trade caravan forever.
        /// </summary>
        private void PruneConsidered(List<Pawn> spawned)
        {
            if (consideredIds.Count < 256) return;

            var present = new HashSet<int>();
            for (int i = 0; i < spawned.Count; i++)
                present.Add(spawned[i].thingIDNumber);

            int removed = consideredIds.RemoveAll(id => !present.Contains(id));
            if (removed <= 0) return;

            consideredLookup.Clear();
            for (int i = 0; i < consideredIds.Count; i++)
                consideredLookup.Add(consideredIds[i]);

            FDLog.Debug("Bounty watch: forgot " + removed + " pawns no longer on "
                        + (map.Parent?.Label ?? "map") + ".");
        }

        /// <summary>Dev entry point: raise a bounty on this pawn now, skipping the sighting roll.</summary>
        public static bool DebugForceBounty(Pawn pawn)
        {
            Map map = pawn?.Map;
            if (map == null) return false;

            var watcher = map.GetComponent<MapComponent_FDBountyWatcher>();
            if (watcher == null) return false;

            // Mark them considered so the natural roll doesn't also fire on the same pawn later.
            if (watcher.consideredLookup.Add(pawn.thingIDNumber))
                watcher.consideredIds.Add(pawn.thingIDNumber);

            watcher.TryGenerateBounty(pawn, pawn.Faction != null && pawn.Faction.leader == pawn);
            return true;
        }

        internal void TryGenerateBounty(Pawn pawn, bool isLeader)
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
