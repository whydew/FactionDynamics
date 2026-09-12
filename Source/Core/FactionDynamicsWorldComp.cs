using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// The single source of truth for everything this mod tracks in a save: per-faction mood
    /// (hardship, grudge), per-settlement state (regrouping, strength), and the simulation config.
    ///
    /// Determinism notes for Multiplayer:
    ///  - all state lives here, in one place, and is scribed in full;
    ///  - every enumeration used by simulation goes through <see cref="SortedFactionIds"/> /
    ///    <see cref="SortedSettlementIds"/>, never raw Dictionary order (which is not guaranteed
    ///    to match between clients);
    ///  - work is driven from <see cref="WorldComponentTick"/> on tick boundaries the game (and
    ///    Multiplayer's world time comp) already replays identically for everyone.
    /// </summary>
    public class FactionDynamicsWorldComp : WorldComponent
    {
        /// <summary>How often housekeeping (pruning dead references, expiring regroups) runs.</summary>
        private const int MaintenanceIntervalTicks = 2000;

        private static readonly FDSimConfig FallbackConfig = new FDSimConfig();

        private Dictionary<int, FactionRuntimeData> factionData = new Dictionary<int, FactionRuntimeData>();
        private Dictionary<int, SettlementRuntimeData> settlementData = new Dictionary<int, SettlementRuntimeData>();
        private FDSimConfig config = new FDSimConfig();

        // Scratch lists for Scribe_Collections (it needs somewhere to stage keys/values).
        private List<int> tmpFactionKeys;
        private List<FactionRuntimeData> tmpFactionValues;
        private List<int> tmpSettlementKeys;
        private List<SettlementRuntimeData> tmpSettlementValues;

        // Reusable sorted key buffers so per-tick iteration doesn't allocate.
        private readonly List<int> sortedFactionIds = new List<int>();
        private readonly List<int> sortedSettlementIds = new List<int>();

        public FactionDynamicsWorldComp(World world) : base(world)
        {
        }

        public static FactionDynamicsWorldComp Current
        {
            get
            {
                World w = Find.World;
                return w?.GetComponent<FactionDynamicsWorldComp>();
            }
        }

        /// <summary>
        /// The config the simulation runs on. Falls back to defaults when there is no world
        /// (main menu, def loading) so nothing ever null-refs on a settings read.
        /// </summary>
        public static FDSimConfig Config
        {
            get
            {
                FactionDynamicsWorldComp c = Current;
                return c != null ? c.config : FallbackConfig;
            }
        }

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);

            if (config == null)
                config = new FDSimConfig();

            // Singleplayer: local settings win, so slider changes apply to existing saves.
            // Multiplayer: whatever came with the host's world stands for the session, because
            // every client must run the same numbers or the simulation diverges.
            if (!MultiplayerCompat.InMultiplayer)
            {
                config.CopyFrom(FactionDynamicsMod.Settings);
            }
            else
            {
                FDLog.Message("Multiplayer session: using the saved (host) simulation config.");
            }

            FDLog.Message("World ready. Simulation config hash " + config.ConfigHash()
                          + " (settlements=" + config.settlements
                          + ", raidGeography=" + config.raidGeography
                          + ", raidMotivations=" + config.raidMotivations
                          + ", questsRaiding=" + config.questsRaiding
                          + ", questsBounties=" + config.questsBounties
                          + " [sighting " + config.bountySightingChance.ToStringPercent()
                          + ", leader " + config.bountySightingLeaderChance.ToStringPercent() + "])");

            RebuildSortedKeys();

            // Saves written before settlement state moved onto the settlements still carry it in the
            // world component. Move it across before anything reads it. No-ops on a new game and on
            // every load after the first.
            MigrateLegacySettlementData();
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();

            int now = Find.TickManager.TicksGame;

            if (now % MaintenanceIntervalTicks == 0)
                Maintenance(now);

            if (config.settlements)
                SettlementLifecycleWorker.Tick(this, now);
        }

        // ---------------------------------------------------------------- data access

        public FactionRuntimeData GetFactionData(Faction faction, bool create = true)
        {
            if (faction == null) return null;
            return GetFactionData(faction.loadID, create);
        }

        public FactionRuntimeData GetFactionData(int factionId, bool create = true)
        {
            if (factionData.TryGetValue(factionId, out FactionRuntimeData data))
                return data;

            if (!create) return null;

            data = new FactionRuntimeData(factionId);
            factionData[factionId] = data;
            sortedFactionIds.Add(factionId);
            sortedFactionIds.Sort();
            return data;
        }

        /// <summary>
        /// This settlement's state, held on the settlement itself by <see cref="FDSettlementComp"/>.
        ///
        /// The <paramref name="create"/> parameter is kept for call-site compatibility but no longer
        /// means much: the comp exists from the moment the settlement does, so there is nothing to
        /// lazily create. It still governs whether a settlement WITHOUT the comp is worth warning
        /// about - a read-only caller (a draw path, an inspect string) gets a quiet null, while a
        /// caller that meant to write gets a log line, because that means some settlement type is
        /// escaping the comp injector and its state is being silently discarded.
        /// </summary>
        public SettlementRuntimeData GetSettlementData(Settlement settlement, bool create = true)
        {
            if (settlement == null) return null;

            FDSettlementComp comp = settlement.GetComponent<FDSettlementComp>();
            if (comp != null) return comp.Data;

            if (create)
            {
                FDLog.Warning("No FDSettlementComp on " + settlement.Label + " (def "
                              + (settlement.def?.defName ?? "null") + "); its state cannot be stored. "
                              + "This usually means a settlement type the comp injector did not recognise.");
            }

            return null;
        }

        /// <summary>Faction ids in a stable order. Never iterate the dictionary directly.</summary>
        public List<int> SortedFactionIds => sortedFactionIds;

        /// <summary>
        /// Every tracked settlement, sorted by ID.
        ///
        /// Settlement state now lives on the settlements, so "what do we track" is simply "what
        /// settlements exist". The sort is not decoration: Find.WorldObjects is insertion-ordered,
        /// which is equal across Multiplayer clients in practice but is not a guarantee the engine
        /// makes, so any loop that mutates state or consumes RNG iterates this instead.
        /// </summary>
        public List<Settlement> SortedSettlements()
        {
            var result = new List<Settlement>();
            List<Settlement> all = Find.WorldObjects.Settlements;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null) result.Add(all[i]);
            }
            result.Sort((a, b) => a.ID.CompareTo(b.ID));
            return result;
        }

        // ---------------------------------------------------------------- events

        /// <summary>
        /// Records that <paramref name="origin"/> sent a raid at the player: it is now weakened and
        /// regrouping, and won't be picked as a raid origin again until it recovers.
        /// </summary>
        public void NotifyRaidSent(Settlement origin, Faction faction, int nowTick)
        {
            if (faction != null)
            {
                FactionRuntimeData fd = GetFactionData(faction);
                fd.lastRaidTick = nowTick;
            }

            if (origin == null) return;

            SettlementRuntimeData sd = GetSettlementData(origin);
            sd.raidsSent++;

            float min = config.regroupDaysMin;
            float max = UnityEngine.Mathf.Max(config.regroupDaysMax, min);

            float days;
            using (FDRand.Push(origin.ID, nowTick, FDRandSalt.RaidRegroup))
            {
                days = Rand.Range(min, max);
            }

            sd.regroupUntilTick = nowTick + UnityEngine.Mathf.RoundToInt(days * GenDate.TicksPerDay);

            FDLog.Debug("Settlement " + origin.Label + " sent a raid; regrouping for "
                        + days.ToString("F1") + " days.");
        }

        /// <summary>The player hurt this faction - they remember it.</summary>
        public void NotifyPlayerAggression(Faction faction, float grudgeAmount)
        {
            if (faction == null || faction.IsPlayer) return;
            GetFactionData(faction).AddGrudge(grudgeAmount);
        }

        /// <summary>
        /// A blow to this faction - a settlement lost, a camp burned out. Hardship lives on the
        /// settlements, so the shock is distributed to them: the three nearest the event feel it in
        /// full, everyone else a quarter. The faction figure is then recomputed as the average, so
        /// it stays a real summary of its towns rather than a separate number drifting on its own.
        /// </summary>
        public void NotifyFactionSetback(Faction faction, float amount, PlanetTile at)
        {
            if (faction == null || faction.IsPlayer || amount <= 0f) return;

            List<Settlement> settlements = FDWorldUtil.SettlementsOf(faction);
            if (settlements.Count == 0)
            {
                // Nothing to spread it across (mechanoids, or a faction already wiped off the map).
                GetFactionData(faction).AddHardship(amount);
                return;
            }

            // Nearest first, ties broken by ID so the order is identical on every client.
            settlements.Sort((a, b) =>
            {
                float da = FDWorldUtil.TileDistance(a.Tile, at);
                float db = FDWorldUtil.TileDistance(b.Tile, at);
                int cmp = da.CompareTo(db);
                return cmp != 0 ? cmp : a.ID.CompareTo(b.ID);
            });

            for (int i = 0; i < settlements.Count; i++)
            {
                SettlementRuntimeData sd = GetSettlementData(settlements[i]);
                sd.hardshipInitialized = true;
                float share = i < 3 ? amount : amount * 0.25f;
                sd.hardship = UnityEngine.Mathf.Clamp01(sd.hardship + share);
            }

            RecomputeFactionHardship(faction, settlements);
        }

        /// <summary>
        /// Faction hardship is the average of its settlements' hardship, weighted toward the ones
        /// near the player's colonies.
        ///
        /// The player only ever meets the near half of a faction, so a flat mean answered the wrong
        /// question: it told you how the faction was doing planet-wide when what you want to know is
        /// how the part of it that can reach you is doing. Settlements within raid range count in
        /// full, distant ones taper to <see cref="FDMoodTuning.HardshipDistantWeight"/>.
        ///
        /// Deterministic for Multiplayer: player home tiles and world distances are identical on
        /// every client, and the sum runs in ID order.
        /// </summary>
        public void RecomputeFactionHardship(Faction faction, List<Settlement> settlements = null)
        {
            if (faction == null) return;

            settlements ??= FDWorldUtil.SettlementsOf(faction);
            if (settlements.Count == 0) return;

            List<PlanetTile> homes = FDWorldUtil.PlayerHomeTiles();

            float total = 0f;
            float weightTotal = 0f;

            for (int i = 0; i < settlements.Count; i++)
            {
                SettlementRuntimeData sd = GetSettlementData(settlements[i], false);
                float hardship = sd?.hardship ?? 0f;
                float weight = PlayerProximityWeight(settlements[i], homes);

                total += hardship * weight;
                weightTotal += weight;
            }

            // weightTotal can never be zero - the floor guarantees every settlement contributes -
            // but guard anyway rather than risk a NaN reaching the UI.
            if (weightTotal <= 0f) return;

            GetFactionData(faction).hardship = UnityEngine.Mathf.Clamp01(total / weightTotal);
        }

        /// <summary>
        /// How much this settlement counts toward its faction's headline hardship: 1 for one within
        /// raid range of a colony, tapering to <see cref="FDMoodTuning.HardshipDistantWeight"/> for
        /// one on the far side of the planet. With no colony on the map yet (or none reachable)
        /// every settlement lands on the floor, which is a flat mean by another name.
        /// </summary>
        private static float PlayerProximityWeight(Settlement settlement, List<PlanetTile> homes)
        {
            float floor = FDMoodTuning.HardshipDistantWeight;

            float best = FDWorldUtil.UnreachableDistance;
            for (int i = 0; i < homes.Count; i++)
            {
                float d = FDWorldUtil.TileDistance(settlement.Tile, homes[i]);
                if (d < best) best = d;
            }

            if (best >= FDWorldUtil.UnreachableDistance) return floor;

            return floor + (1f - floor) * FDRaidGeography.ProximityScore(best);
        }

        /// <summary>
        /// Hardship as it applies to a particular raid: the origin settlement's own figure when we
        /// know where the raid came from, otherwise the faction average. This is what decides
        /// whether a starvation raid is available, so the settlement that sent it has to be the one
        /// that is actually hungry.
        /// </summary>
        public float HardshipFor(Faction faction, Settlement origin)
        {
            if (origin != null)
            {
                SettlementRuntimeData sd = GetSettlementData(origin, false);
                if (sd != null && sd.hardshipInitialized) return sd.hardship;
            }

            FactionRuntimeData fd = GetFactionData(faction, false);
            return fd?.hardship ?? 0f;
        }

        // ---------------------------------------------------------------- housekeeping

        private void Maintenance(int now)
        {
            // Settlement pruning used to live here: build the set of live settlement ids, diff it
            // against our keys, remove the orphans. All of that is gone. State lives on the
            // settlement now, so a destroyed settlement takes its state with it and there is no
            // orphan to find.
            //
            // Expiring finished regroups is gone too, for a different reason: IsRegrouping(now)
            // already answers false once the tick has passed, so resetting the field was only ever
            // tidying. The one thing that read regroupUntilTick directly was this loop.
            //
            // Faction data is still a dictionary - factions are not WorldObjects - but nothing has
            // ever pruned it, and a faction's data staying behind after the faction is gone is
            // harmless: it is a handful of floats, looked up by id and never iterated for anything
            // that reaches the world.
        }

        /// <summary>
        /// Moves settlement state out of the pre-comp save format and onto the settlements.
        ///
        /// Runs once, on the first load of a save written before <see cref="FDSettlementComp"/>
        /// existed. Pure data movement: no RNG, no ordering sensitivity, so every Multiplayer client
        /// performs it identically from the same save.
        ///
        /// Deliberately tolerant. A legacy entry whose settlement no longer exists is dropped (the
        /// old pruning pass may simply not have reached it yet), and one whose settlement has no
        /// comp is reported rather than silently lost.
        /// </summary>
        private void MigrateLegacySettlementData()
        {
            if (settlementData == null || settlementData.Count == 0) return;

            int moved = 0, orphaned = 0, uncomped = 0;

            List<Settlement> all = Find.WorldObjects.Settlements;
            var byId = new Dictionary<int, Settlement>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null) byId[all[i].ID] = all[i];
            }

            for (int i = 0; i < sortedSettlementIds.Count; i++)
            {
                int id = sortedSettlementIds[i];
                if (!settlementData.TryGetValue(id, out SettlementRuntimeData legacy) || legacy == null)
                    continue;

                if (!byId.TryGetValue(id, out Settlement settlement)) { orphaned++; continue; }

                FDSettlementComp comp = settlement.GetComponent<FDSettlementComp>();
                if (comp == null) { uncomped++; continue; }

                SettlementRuntimeData target = comp.Data;
                target.regroupUntilTick = legacy.regroupUntilTick;
                target.strengthFactor = legacy.strengthFactor;
                target.raidsSent = legacy.raidsSent;
                target.foundedTick = legacy.foundedTick;
                target.hardship = legacy.hardship;
                target.hardshipInitialized = legacy.hardshipInitialized;
                target.origin = legacy.origin;
                moved++;
            }

            settlementData.Clear();
            sortedSettlementIds.Clear();

            FDLog.Message("Migrated settlement state onto the settlements: " + moved + " moved"
                          + (orphaned > 0 ? ", " + orphaned + " for settlements that no longer exist" : "")
                          + (uncomped > 0 ? ", " + uncomped + " LOST (no comp on that settlement type)" : "")
                          + ".");
        }

        private void RebuildSortedKeys()
        {
            sortedFactionIds.Clear();
            foreach (int key in factionData.Keys)
                sortedFactionIds.Add(key);
            sortedFactionIds.Sort();

            // Legacy only - populated from an old save so the migration has a deterministic order to
            // walk, and emptied by it. Nothing writes to settlementData any more.
            sortedSettlementIds.Clear();
            if (settlementData != null)
            {
                foreach (int key in settlementData.Keys)
                    sortedSettlementIds.Add(key);
                sortedSettlementIds.Sort();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Deep.Look(ref config, "config");
            Scribe_Collections.Look(ref factionData, "factionData", LookMode.Value, LookMode.Deep,
                ref tmpFactionKeys, ref tmpFactionValues);
            Scribe_Collections.Look(ref settlementData, "settlementData", LookMode.Value, LookMode.Deep,
                ref tmpSettlementKeys, ref tmpSettlementValues);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                config ??= new FDSimConfig();
                factionData ??= new Dictionary<int, FactionRuntimeData>();
                settlementData ??= new Dictionary<int, SettlementRuntimeData>();
                RebuildSortedKeys();
            }
        }
    }
}
