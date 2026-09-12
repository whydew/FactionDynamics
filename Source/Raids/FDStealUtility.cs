using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

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

        /// <summary>
        /// How a raider paths when it is going after loot: doors are passable, and bashable if they
        /// will not open.
        ///
        /// This is the fix for the raids that arrived and turned straight back around. The
        /// diagnostic was unambiguous - 21 valid targets on the map (simple meals, rice, fox and
        /// ibex meat), all about 206 tiles away, and every single one reporting reachable=false.
        /// Nothing was wrong with the filters or the search radius; the food was inside the
        /// colony, and the default TraverseMode.ByPawn will not path a hostile pawn through the
        /// doors to get at it. Every candidate failed the reachability test, FindBestLoot returned
        /// null, the duty fell through to JobGiver_ExitMapBest, and the raid walked home.
        ///
        /// PassDoors rather than PassAllDestroyableThings on purpose: a raider who can path through
        /// walls would happily pick a target on the far side of one, and JobDriver_TakeAndExitMap
        /// has no mining or breaching step to get there - it would just stall against the wall. A
        /// sealed colony with no door is genuinely un-lootable, and the raid leaving is the correct
        /// outcome for that.
        /// </summary>
        public static TraverseParms LootTraverse(Pawn pawn)
        {
            return TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors,
                canBashDoors: true, alwaysUseAvoidGrid: false, canBashFences: true,
                avoidPersistentDanger: false);
        }

        /// <summary>
        /// The thing this raider should go and take, or null if there is genuinely nothing.
        ///
        /// Two stages, because the regional search alone was quietly failing. It used to run with
        /// minRegions/maxRegions of 15/15 and lookInHaulSources false, and it has NO global
        /// fallback of its own - so it gave up after about fifteen regions around the raider. That
        /// is a small patch of ground next to wherever they walked onto the map, and a colony's
        /// food is usually much further in. While trees and corpses still passed the food filter
        /// there was always something in that little bubble to grab, which hid the problem
        /// completely; once those were excluded the search started coming back empty and the raid
        /// walked straight back off the map.
        ///
        /// Now: a wide regional pass first (it ranks by how much the raid actually wants the
        /// thing, and looks inside shelves and other haul sources), and if that still finds
        /// nothing, a whole-map sweep. The sweep only runs when the cheap pass failed, and it
        /// checks reachability lazily, best candidate first, so it costs a path query or two rather
        /// than one per item on the map.
        /// </summary>
        /// <summary>
        /// How a raider paths when it will not break anything: doors it cannot open are simply
        /// impassable, so the route only ever runs through ground that is genuinely open to them.
        /// </summary>
        public static TraverseParms OpenTraverse(Pawn pawn)
        {
            return TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn,
                canBashDoors: false, alwaysUseAvoidGrid: false, canBashFences: false,
                avoidPersistentDanger: false);
        }

        public static Thing FindBestLoot(Pawn pawn, FDLootCategory category, float maxDist)
        {
            return FindBestLoot(pawn, category, maxDist, out _);
        }

        /// <summary>
        /// The thing this raider should go and take, or null if there is genuinely nothing.
        /// <paramref name="needsForcedEntry"/> reports whether reaching it means breaking a door.
        ///
        /// TWO PASSES, and the order is the whole point: first look for loot they can walk to
        /// without breaking anything, and only if there is none does door-breaking come into play.
        /// That is what makes "they only break doors that are directly blocking their target" true
        /// by construction rather than by tuning - a raider will cross the map for an outdoor
        /// stockpile before touching your freezer door, and the only time a door comes down is when
        /// what is behind it is the sole loot on the map.
        ///
        /// Cost tuning alone could not express this. Patch_LootDoorCost makes a door expensive, so
        /// among door-breaking routes they take the one with the fewest doors - but "expensive" is
        /// still comparable to a long walk, so open loot and shut-away loot stayed on one scale.
        /// Splitting the passes puts them on two.
        ///
        /// Each pass is itself two stages, because the regional search alone was quietly failing.
        /// It used to run with minRegions/maxRegions of 15/15 and lookInHaulSources false, and it
        /// has NO global fallback of its own - so it gave up after about fifteen regions around the
        /// raider, a small patch of ground next to wherever they walked onto the map. While trees
        /// and corpses still passed the food filter there was always something in that bubble to
        /// grab, which hid the problem; once those were excluded the search came back empty and the
        /// raid walked straight back off the map.
        /// </summary>
        public static Thing FindBestLoot(Pawn pawn, FDLootCategory category, float maxDist,
            out bool needsForcedEntry)
        {
            needsForcedEntry = false;

            if (pawn == null || pawn.Map == null) return null;
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return null;

            int now = Find.TickManager.TicksGame;

            // A raid re-runs this every 120 ticks per raider for as long as it is on the map (see
            // JobGiver_FDSteal's expiryInterval), and the answer almost never changes between one
            // run and the next - the pawn is still walking to the same stockpile. Measured on a
            // 43-raider plunder raid, doing the full search every time cost 5.2x the tick rate:
            // 355 TPS with no raid against 68 TPS with the raid, at a speed setting asking for 180.
            //
            // So the answer is remembered for a few seconds. This does NOT slow their reaction to
            // being shot: the duty's ThinkNode_HarmedRecently sits ABOVE this node in the tree and
            // is evaluated before we are ever called, so a raider under fire still switches to
            // fighting on the next job-override check.
            if (TryGetCachedTarget(pawn, category, now, out Thing cached, out bool cachedForced))
            {
                needsForcedEntry = cachedForced;
                return cached;
            }

            // Pass 1: anything they can reach without laying a hand on a door.
            // Pass 2 only runs if pass 1 found nothing anywhere, which is what makes "they only
            // break the door that is actually in the way" true by construction rather than tuning.
            List<Thing> sweep = null;

            Thing open = FindBestLootWith(pawn, category, maxDist, OpenTraverse(pawn), ref sweep);
            if (open != null)
            {
                Remember(pawn, category, now, open, false);
                return open;
            }

            // Pass 2: nothing is open, so whatever door stands in the way now IS the thing between
            // them and the only loot there is.
            Thing forced = FindBestLootWith(pawn, category, maxDist, LootTraverse(pawn), ref sweep);
            needsForcedEntry = forced != null;
            Remember(pawn, category, now, forced, needsForcedEntry);
            return forced;
        }

        private static Thing FindBestLootWith(Pawn pawn, FDLootCategory category, float maxDist,
            TraverseParms parms, ref List<Thing> sweep)
        {
            Thing best = GenClosest.ClosestThing_Regionwise_ReachablePrioritized(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForGroup(ThingRequestGroup.HaulableEverOrMinifiable),
                PathEndMode.ClosestTouch,
                parms,
                maxDist,
                t => IsValidLoot(pawn, t, category),
                t => LootValue(t, category),
                30,
                300,
                true);

            if (best != null) return best;

            // The whole-map candidate list is identical for both passes - only the reachability
            // test differs - so it is built at most once per search instead of twice.
            sweep ??= BuildSweepCandidates(pawn, category, maxDist);
            return FirstReachable(pawn, sweep, category, parms);
        }

        /// <summary>
        /// Every thing on the map worth taking, best first. Used only when the regional search comes
        /// back empty.
        ///
        /// Reservation is deliberately NOT checked here. CanReserve is the expensive part of the
        /// filter and this loop runs over every haulable on the map; checking it lazily in
        /// <see cref="FirstReachable"/> means it is evaluated for the handful of candidates actually
        /// considered rather than for all of them.
        ///
        /// Deterministic for Multiplayer: the scan order is the map's thing lister and the sort
        /// breaks score ties on thingIDNumber, so every client builds the same list in the same
        /// order and picks the same target.
        /// </summary>
        private static List<Thing> BuildSweepCandidates(Pawn pawn, FDLootCategory category, float maxDist)
        {
            var candidates = new List<Thing>();

            List<Thing> all = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEverOrMinifiable);
            if (all == null || all.Count == 0) return candidates;

            float maxSq = maxDist * maxDist;

            // Values are computed once here and reused by the sort. Recomputing LootValue inside the
            // comparator meant O(n log n) stat lookups per sweep, per raider, every two seconds.
            var scored = new List<KeyValuePair<float, Thing>>();

            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || !t.Spawned) continue;
                if (maxSq < float.MaxValue && (t.Position - pawn.Position).LengthHorizontalSquared > maxSq) continue;
                if (!IsCarryableLoot(t)) continue;
                if (!t.def.stealable) continue;
                if (t.IsBurning()) continue;
                if (IsSpoiledOrCorpse(t)) continue;
                if (!MatchesCategory(t, category)) continue;

                float value = LootValue(t, category);
                if (value <= 0f) continue;

                scored.Add(new KeyValuePair<float, Thing>(value, t));
            }

            scored.Sort((a, b) =>
            {
                int cmp = b.Key.CompareTo(a.Key);
                return cmp != 0 ? cmp : a.Value.thingIDNumber.CompareTo(b.Value.thingIDNumber);
            });

            for (int i = 0; i < scored.Count; i++)
                candidates.Add(scored[i].Value);

            return candidates;
        }

        /// <summary>
        /// The best candidate this raider can actually get to under the given traverse rules, and
        /// actually claim. Reachability must use the SAME traverse the caller's pass is testing, or
        /// the sweep would hand back door-locked loot during the no-breaking pass.
        /// </summary>
        private static Thing FirstReachable(Pawn pawn, List<Thing> candidates, FDLootCategory category,
            TraverseParms parms)
        {
            if (candidates == null) return null;

            Reachability reach = pawn.Map.reachability;

            for (int i = 0; i < candidates.Count; i++)
            {
                Thing t = candidates[i];

                // Things can be destroyed or picked up between the list being built and this pass.
                if (t == null || t.Destroyed || !t.Spawned) continue;
                if (!reach.CanReach(pawn.Position, t, PathEndMode.ClosestTouch, parms)) continue;
                if (!pawn.CanReserve(t)) continue;

                return t;
            }

            return null;
        }

        // ---------------------------------------------------------------- per-raider target cache

        private struct CachedLoot
        {
            public Thing thing;
            public FDLootCategory category;
            public int takenTick;
            public int expiryTick;
            public bool forced;
        }

        /// <summary>How long a chosen target stays chosen before the search runs again.</summary>
        private const int CacheTicks = 600;

        /// <summary>
        /// Keyed by pawn id. Static, and safe in Multiplayer: it is written only from this search,
        /// which every client runs on the same ticks with the same inputs, and it is only ever read
        /// back by key - never enumerated - so Dictionary ordering cannot influence any outcome.
        /// </summary>
        private static readonly Dictionary<int, CachedLoot> targetCache = new Dictionary<int, CachedLoot>();

        private static bool TryGetCachedTarget(Pawn pawn, FDLootCategory category, int now,
            out Thing thing, out bool forced)
        {
            thing = null;
            forced = false;

            if (!targetCache.TryGetValue(pawn.thingIDNumber, out CachedLoot entry)) return false;

            // Loading an earlier save winds TicksGame backwards, which would otherwise leave entries
            // sitting on an expiry far in the future holding Things from a map that no longer exists.
            if (now < entry.takenTick || now >= entry.expiryTick)
            {
                targetCache.Remove(pawn.thingIDNumber);
                return false;
            }

            if (entry.category != category) return false;

            // A remembered target is still only good if it is still there and still takeable.
            if (entry.thing == null || entry.thing.Destroyed || !entry.thing.Spawned
                || entry.thing.Map != pawn.Map
                || !IsValidLoot(pawn, entry.thing, category))
            {
                targetCache.Remove(pawn.thingIDNumber);
                return false;
            }

            thing = entry.thing;
            forced = entry.forced;
            return true;
        }

        private static void Remember(Pawn pawn, FDLootCategory category, int now, Thing thing, bool forced)
        {
            if (thing == null)
            {
                // Nothing to take. Don't remember a negative - the colony's stores change, and a
                // raider that gave up once should look again on its next check.
                targetCache.Remove(pawn.thingIDNumber);
                return;
            }

            targetCache[pawn.thingIDNumber] = new CachedLoot
            {
                thing = thing,
                category = category,
                takenTick = now,
                expiryTick = now + CacheTicks,
                forced = forced
            };

            PruneCache(now);
        }

        /// <summary>
        /// Drops expired entries so the cache cannot grow across a long game and cannot keep Things
        /// from an unloaded map alive. Which entries are expired is a pure function of the tick, so
        /// the surviving set is identical on every client regardless of removal order.
        /// </summary>
        private static void PruneCache(int now)
        {
            if (targetCache.Count < 128) return;

            List<int> stale = null;
            foreach (KeyValuePair<int, CachedLoot> kv in targetCache)
            {
                if (now < kv.Value.takenTick || now >= kv.Value.expiryTick)
                    (stale ??= new List<int>()).Add(kv.Key);
            }

            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
                targetCache.Remove(stale[i]);
        }

        /// <summary>
        /// Diagnostic: walks every haulable thing on the map and reports which filter rejected it,
        /// then whether the survivors are actually reachable.
        ///
        /// This exists because "the raid arrived and immediately left" is indistinguishable from
        /// the outside whether the cause is an empty colony, a filter that is too strict, a search
        /// that cannot see far enough, or loot that is real and visible but unreachable. Guessing
        /// between those cost several round trips; this answers it in one run.
        /// </summary>
        public static string Explain(Pawn pawn, FDLootCategory category, float maxDist)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Loot search for " + pawn.LabelShort + " (" + category + ", radius "
                          + maxDist.ToString("F0") + ")");
            sb.AppendLine("  duty: " + (pawn.mindState?.duty?.def?.defName ?? "(none)")
                          + "   lord toil: " + (pawn.GetLord()?.CurLordToil?.GetType().Name ?? "(no lord)"));
            sb.AppendLine("  manipulation: " + pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
            sb.AppendLine("  exit spot found: " +
                RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 _, TraverseMode.ByPawn, false));

            List<Thing> all = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEverOrMinifiable);
            sb.AppendLine("  haulable things on map: " + (all?.Count ?? 0));

            int notSpawned = 0, tooFar = 0, notCarryable = 0, notStealable = 0, burning = 0,
                spoiled = 0, wrongCategory = 0, cantReserve = 0, zeroValue = 0;
            var passed = new List<Thing>();

            float maxSq = maxDist * maxDist;
            for (int i = 0; i < (all?.Count ?? 0); i++)
            {
                Thing t = all[i];
                if (t == null || !t.Spawned) { notSpawned++; continue; }
                if ((t.Position - pawn.Position).LengthHorizontalSquared > maxSq) { tooFar++; continue; }
                if (!IsCarryableLoot(t)) { notCarryable++; continue; }
                if (!t.def.stealable) { notStealable++; continue; }
                if (t.IsBurning()) { burning++; continue; }
                if (IsSpoiledOrCorpse(t)) { spoiled++; continue; }
                if (!MatchesCategory(t, category)) { wrongCategory++; continue; }
                if (LootValue(t, category) <= 0f) { zeroValue++; continue; }
                if (!pawn.CanReserve(t)) { cantReserve++; continue; }
                passed.Add(t);
            }

            sb.AppendLine("  rejected: notSpawned=" + notSpawned + " tooFar=" + tooFar
                          + " notCarryable=" + notCarryable + " notStealable=" + notStealable
                          + " burning=" + burning + " spoiledOrCorpse=" + spoiled
                          + " wrongCategory=" + wrongCategory + " zeroValue=" + zeroValue
                          + " cantReserve=" + cantReserve);
            sb.AppendLine("  passed all filters: " + passed.Count);

            passed.Sort((a, b) =>
            {
                int cmp = LootValue(b, category).CompareTo(LootValue(a, category));
                return cmp != 0 ? cmp : a.thingIDNumber.CompareTo(b.thingIDNumber);
            });

            // Reachability per traverse mode, because "unreachable" has very different causes:
            // ByPawn failing but PassDoors succeeding means doors; all three failing means solid
            // walls (or a different landmass) and no amount of pathing config will help.
            TraverseParms loot = LootTraverse(pawn);
            Reachability reach = pawn.Map.reachability;

            int reachable = 0;
            int shown = 0;
            for (int i = 0; i < passed.Count; i++)
            {
                bool ok = reach.CanReach(pawn.Position, passed[i], PathEndMode.ClosestTouch, loot);
                if (ok) reachable++;
                if (shown < 10)
                {
                    sb.AppendLine("    " + passed[i].LabelShort
                                  + "  value " + LootValue(passed[i], category).ToString("F1")
                                  + "  dist " + passed[i].Position.DistanceTo(pawn.Position).ToString("F0")
                                  + "  byPawn " + reach.CanReach(pawn.Position, passed[i],
                                        PathEndMode.ClosestTouch, TraverseParms.For(pawn))
                                  + "  passDoors " + ok
                                  + "  passWalls " + reach.CanReach(pawn.Position, passed[i],
                                        PathEndMode.ClosestTouch,
                                        TraverseParms.For(pawn, Danger.Deadly,
                                            TraverseMode.PassAllDestroyableThings, true, false, true, false)));
                    shown++;
                }
            }

            sb.AppendLine("  of those, reachable with loot traverse (PassDoors + bash): " + reachable);

            // Force a real search: a diagnostic that reported a remembered answer would hide the
            // very thing it exists to show.
            targetCache.Remove(pawn.thingIDNumber);
            Thing chosen = FindBestLoot(pawn, category, maxDist, out bool forced);
            sb.AppendLine("  FindBestLoot returns: " + (chosen?.LabelShort ?? "NULL")
                          + (chosen == null ? "" : forced
                              ? "  (pass 2 - needs a door broken)"
                              : "  (pass 1 - reachable without breaking anything)"));
            return sb.ToString();
        }

        public static bool IsValidLoot(Pawn pawn, Thing t, FDLootCategory category)
        {
            if (t == null || t.def == null) return false;
            if (!IsCarryableLoot(t)) return false;
            if (!t.def.stealable) return false;
            if (t.IsBurning()) return false;
            if (IsSpoiledOrCorpse(t)) return false;
            if (!MatchesCategory(t, category)) return false;
            if (!pawn.CanReserve(t)) return false;
            return true;
        }

        /// <summary>
        /// Things that count as nutrition to the engine but that nobody would carry home.
        ///
        /// Corpses are the sharp edge here. A corpse is a haulable Item with a real Nutrition stat,
        /// so it sailed through the food filter - and a single one outweighs everything else on the
        /// map. A human corpse is around 4.5 nutrition and a big animal far more, against a
        /// three-raider goal of 18, so the raid's best move was to grab one body and walk off. That
        /// is exactly what it did: pick up a carcass, meet the goal, leave.
        ///
        /// Rot is the other half. Rotten meals and rotten meat keep their nutrition value as far as
        /// this search is concerned, and a raid hauling away your spoiled stockpile looks like a
        /// bug even when it is arithmetically sensible.
        ///
        /// Both are excluded outright rather than tuned. A raid that came for the food stores means
        /// the meals and the raw ingredients - not the graveyard, and not the bin.
        /// </summary>
        private static bool IsSpoiledOrCorpse(Thing t)
        {
            if (t is Corpse) return true;

            CompRottable rot = t.TryGetComp<CompRottable>();
            return rot != null && rot.Stage != RotStage.Fresh;
        }

        /// <summary>
        /// Something a raider can physically pick up and carry off the map.
        ///
        /// This check exists because of a real crash: raiders on a starvation raid were targeting
        /// PINE TREES. Two vanilla facts combine badly here -
        ///
        ///   * every tree inherits an &lt;ingestible&gt; block (so grazing animals can eat them) and
        ///     TreeBase carries &lt;Nutrition&gt;2.0&lt;/Nutrition&gt;, so ThingDef.IsNutritionGivingIngestible
        ///     is TRUE for a pine tree - it passed the food filter honestly;
        ///   * TreeBase sets &lt;minifiedDef&gt;MinifiedTree&lt;/minifiedDef&gt;, so ThingDef.Minifiable is
        ///     true and trees are inside ThingRequestGroup.HaulableEverOrMinifiable, the same group
        ///     vanilla's own steal search uses.
        ///
        /// A Steal job on a tree then reaches JobDriver_TakeAndExitMap's UninstallIfMinifiable toil,
        /// which - seeing Minifiable - reads def.building.uninstallWork. Trees have no building
        /// properties, so that NullRefs, once per raider per retry, forever.
        ///
        /// Vanilla never hits this because it ranks steal targets purely by market value and a tree
        /// is worth nothing. A food raid ranks by nutrition, where a tree scores 2.0 and there are
        /// several hundred of them, so it went straight to the top.
        ///
        /// Filtering by category is the durable fix - loot is items, not scenery - and it keeps
        /// the vanilla behaviour of prising an installed sculpture off the floor, which is what
        /// that toil is actually for.
        /// </summary>
        private static bool IsCarryableLoot(Thing t)
        {
            switch (t.def.category)
            {
                case ThingCategory.Item:
                    return t.def.EverHaulable;

                case ThingCategory.Building:
                    // Uninstall-and-carry. building != null is the exact condition the vanilla
                    // toil assumes and trees violate.
                    //
                    // t.Spawned && ParentHolder is Map is the second half, and it is not optional:
                    // the regional search runs with lookInHaulSources true, so it sees INSIDE
                    // containers. An electric stove sitting in a MinifiedThing (uninstalled, in a
                    // crate, mid-haul) passes the def-level test perfectly well - and then
                    // JobDriver_TakeAndExitMap's UninstallIfMinifiable toil tries to minify a thing
                    // that is already minified, which warns "Can't minify thing which is in a
                    // ThingOwner" and then NullRefs inside GenSpawn.Spawn, once per tick, forever.
                    //
                    // Items inside containers are fine to steal - that is the whole point of
                    // looking in haul sources. Buildings inside containers are not: only a building
                    // actually installed on the map can be uninstalled off it.
                    return t.def.Minifiable
                           && t.def.building != null
                           && t.Spawned
                           && t.ParentHolder is Map;

                default:
                    // Plants, filth, pawns, projectiles - not loot.
                    return false;
            }
        }

        /// <summary>
        /// Does this thing count toward the raid's goal? Same test as the search uses, minus the
        /// reachability and reservation parts, so the raid can never be satisfied by something it
        /// would never have been allowed to pick up in the first place.
        /// </summary>
        public static bool CountsTowardGoal(Thing t, FDLootCategory category)
        {
            if (t == null || t.def == null) return false;
            if (IsSpoiledOrCorpse(t)) return false;
            return MatchesCategory(t, category);
        }

        public static bool MatchesCategory(Thing t, FDLootCategory category)
        {
            switch (category)
            {
                case FDLootCategory.Food:
                    // Nutrition the raiders can actually carry home. IsCarryableLoot has already
                    // ruled out plants, which is what keeps this from meaning "that tree".
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
