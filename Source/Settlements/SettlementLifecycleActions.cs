using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// The actual world mutations behind M1. Every method here assumes it is already inside a
    /// seeded <see cref="FDRand"/> scope opened by <see cref="SettlementLifecycleWorker"/>, so any
    /// Rand call it makes is deterministic and replayable on every Multiplayer client.
    /// </summary>
    public static class SettlementLifecycleActions
    {
        /// <summary>Distance band (tiles) used when a faction expands next to its own territory.</summary>
        private const int ExpansionMinDist = 4;
        private const int ExpansionMaxDist = 22;

        public static void TryFoundSettlement(FactionDynamicsWorldComp comp, Faction faction,
            FactionRuntimeData data, List<Settlement> existing, int now)
        {
            PlanetTile tile = PlanetTile.Invalid;
            FDSettlementOrigin origin = FDSettlementOrigin.Founded;

            // Prefer expanding outward from an existing settlement - factions grow along their own
            // borders rather than teleporting across the planet.
            if (existing.Count > 0)
            {
                Settlement root = existing[Rand.Range(0, existing.Count)];
                if (root != null && root.Tile.Valid &&
                    TileFinder.TryFindPassableTileWithTraversalDistance(root.Tile, ExpansionMinDist,
                        ExpansionMaxDist, out PlanetTile found, IsUsableTile))
                {
                    tile = found;
                    origin = FDSettlementOrigin.Expanded;
                }
            }

            if (!tile.Valid)
            {
                PlanetTile fallback = TileFinder.RandomSettlementTileFor(faction, false, IsUsableTile);
                if (fallback.Valid)
                    tile = fallback;
            }

            if (!tile.Valid)
            {
                FDLog.Debug("No valid tile found for a new " + faction.Name + " settlement this check.");
                return;
            }

            var settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            settlement.SetFaction(faction);
            settlement.Tile = tile;
            settlement.Name = SettlementNameGenerator.GenerateSettlementName(settlement, null);
            Find.WorldObjects.Add(settlement);

            SettlementRuntimeData sd = comp.GetSettlementData(settlement);
            sd.foundedTick = now;
            sd.origin = origin;
            // A brand new settlement is a small one.
            sd.strengthFactor = 0.7f;

            FDLog.Debug(faction.Name + " founded " + settlement.Label + " (" + origin + ").");

            string label = origin == FDSettlementOrigin.Expanded
                ? "FD_LetterSettlementExpandedLabel".Translate(faction.Name)
                : "FD_LetterSettlementFoundedLabel".Translate(faction.Name);
            string text = origin == FDSettlementOrigin.Expanded
                ? "FD_LetterSettlementExpandedText".Translate(faction.NameColored, settlement.Label)
                : "FD_LetterSettlementFoundedText".Translate(faction.NameColored, settlement.Label);

            FDWorldUtil.SendNearbyLetter(label, text, LetterDefOf.NeutralEvent, settlement);
        }

        public static void TryRemoveSettlement(FactionDynamicsWorldComp comp, Faction faction,
            FactionRuntimeData data, List<Settlement> existing, int now)
        {
            // Weight toward the weakest, most recently beaten-up settlements: a settlement that has
            // been throwing raids at the player and losing is the one that falls apart.
            Settlement chosen = null;
            float bestWeight = 0f;

            for (int i = 0; i < existing.Count; i++)
            {
                Settlement s = existing[i];
                if (FDWorldUtil.IsProtected(s)) continue;

                // A faction does not quietly abandon its own seat. The capital can still be taken
                // from them - by the player, or by a strike quest - but attrition never claims it.
                if (s.ID == data.capitalSettlementId) continue;

                SettlementRuntimeData sd = comp.GetSettlementData(s, false);
                float strength = sd?.EffectiveStrength(now, FactionDynamicsWorldComp.Config.regroupStrengthPenalty) ?? 1f;

                // Weight = fragility, plus a deterministic jitter so it isn't always the same one.
                float weight = (1.5f - UnityEngine.Mathf.Clamp(strength, 0.1f, 1.5f))
                               + Rand.Value * 0.35f;

                if (weight > bestWeight)
                {
                    bestWeight = weight;
                    chosen = s;
                }
            }

            if (chosen == null)
            {
                FDLog.Debug("No removable settlement for " + faction.Name
                            + " this check (all protected, or only the capital is left).");
                return;
            }

            SettlementRuntimeData chosenData = comp.GetSettlementData(chosen, false);
            bool wasWeakened = chosenData != null &&
                               (chosenData.IsRegrouping(now) || chosenData.strengthFactor < 0.8f);

            // Reason for the removal, chosen from what actually happened to this settlement.
            string label;
            string text;
            if (wasWeakened)
            {
                label = "FD_LetterSettlementCollapsedLabel".Translate(chosen.Label);
                text = "FD_LetterSettlementCollapsedText".Translate(chosen.Label, faction.NameColored);
            }
            else
            {
                label = "FD_LetterSettlementAbandonedLabel".Translate(chosen.Label);
                text = "FD_LetterSettlementAbandonedText".Translate(chosen.Label, faction.NameColored);
            }

            // Send the letter before removal, while the world object still exists to look at.
            FDWorldUtil.SendNearbyLetter(label, text, LetterDefOf.NeutralEvent, chosen);

            PlanetTile lostTile = chosen.Tile;

            FDLog.Debug(faction.Name + " lost " + chosen.Label + (wasWeakened ? " (collapsed)" : " (abandoned)"));

            Find.WorldObjects.Remove(chosen);

            // Losing ground hurts, and it hurts most nearby: the shock is spread across the
            // faction's remaining settlements, heaviest on the ones closest to what they just lost.
            comp.NotifyFactionSetback(faction, wasWeakened ? 0.35f : 0.2f, lostTile);
        }

        public static void StrengthenSettlement(FactionDynamicsWorldComp comp, Faction faction,
            List<Settlement> existing, int now)
        {
            if (existing.Count == 0) return;

            Settlement s = existing[Rand.Range(0, existing.Count)];
            if (s == null) return;

            SettlementRuntimeData sd = comp.GetSettlementData(s);
            float before = sd.strengthFactor;
            sd.strengthFactor = UnityEngine.Mathf.Min(1.5f, sd.strengthFactor + 0.1f);

            if (sd.strengthFactor > before)
                FDLog.Debug(s.Label + " grew stronger (" + sd.strengthFactor.ToString("F2") + ").");
        }

        /// <summary>Shared tile validator: somewhere a settlement could actually stand.</summary>
        private static bool IsUsableTile(PlanetTile tile)
        {
            if (!tile.Valid) return false;
            if (Find.WorldObjects.AnySettlementBaseAtOrAdjacent(tile)) return false;
            if (Find.WorldObjects.AnyWorldObjectAt(tile)) return false;
            return TileFinder.IsValidTileForNewSettlement(tile, null, false);
        }
    }
}
