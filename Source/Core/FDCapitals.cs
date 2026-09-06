using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Faction capitals.
    ///
    /// Vanilla has no concept of one settlement mattering more than another, so this mod declares
    /// one: a faction's capital is its strongest settlement, and it stays put unless it is destroyed
    /// or overtaken. That gives the world map a landmark per faction - somewhere the player can
    /// point at and say "that's their seat" - and gives the strike quest an obvious prize.
    ///
    /// Selection is deterministic (strength, ties broken by settlement ID) so every Multiplayer
    /// client designates the same capital without any syncing.
    /// </summary>
    public static class FDCapitals
    {
        /// <summary>A capital is always at least this strong - it is the faction's seat, after all.</summary>
        public const float CapitalStrengthFloor = 1.15f;

        /// <summary>
        /// Confirms this faction still has a valid capital, choosing a new one if the old one is
        /// gone. Returns the capital, or null if the faction holds nothing.
        /// </summary>
        public static Settlement EnsureCapital(FactionDynamicsWorldComp comp, Faction faction,
            List<Settlement> settlements, bool announceChange)
        {
            if (comp == null || faction == null || settlements == null || settlements.Count == 0)
                return null;

            FactionRuntimeData fd = comp.GetFactionData(faction);

            Settlement current = null;
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i].ID == fd.capitalSettlementId)
                {
                    current = settlements[i];
                    break;
                }
            }

            if (current != null)
            {
                ApplyCapitalStrength(comp, current);
                return current;
            }

            // No capital, or the old one is off the map: crown the strongest settlement.
            Settlement best = null;
            float bestStrength = float.MinValue;
            for (int i = 0; i < settlements.Count; i++)
            {
                SettlementRuntimeData sd = comp.GetSettlementData(settlements[i], false);
                float strength = sd?.strengthFactor ?? 1f;

                // settlements is already ID-sorted, so ">" keeps the lowest ID on a tie - the
                // oldest settlement, which reads as the historical seat.
                if (strength > bestStrength)
                {
                    bestStrength = strength;
                    best = settlements[i];
                }
            }

            if (best == null) return null;

            bool hadOne = fd.capitalSettlementId >= 0;
            fd.capitalSettlementId = best.ID;
            ApplyCapitalStrength(comp, best);

            FDLog.Debug(faction.Name + (hadOne ? " moved its seat to " : " seat designated: ") + best.Label);

            if (hadOne && announceChange)
            {
                FDWorldUtil.SendNearbyLetter(
                    "FD_LetterCapitalMovedLabel".Translate(faction.Name),
                    "FD_LetterCapitalMovedText".Translate(faction.NameColored, best.Label),
                    LetterDefOf.NeutralEvent, best);
            }

            return best;
        }

        private static void ApplyCapitalStrength(FactionDynamicsWorldComp comp, Settlement capital)
        {
            SettlementRuntimeData sd = comp.GetSettlementData(capital);
            if (sd.strengthFactor < CapitalStrengthFloor)
                sd.strengthFactor = CapitalStrengthFloor;
        }

        /// <summary>
        /// Cheap enough to call from a world-map draw path: two dictionary lookups and an int
        /// compare, no settlement enumeration.
        /// </summary>
        public static bool IsCapital(Settlement settlement)
        {
            if (settlement?.Faction == null || settlement.Faction.IsPlayer) return false;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return false;

            FactionRuntimeData fd = comp.GetFactionData(settlement.Faction, false);
            return fd != null && fd.capitalSettlementId == settlement.ID;
        }
    }

    /// <summary>
    /// Makes capitals stand out on the world map without any new art.
    ///
    /// Three vanilla levers, all of them things the game already does to signal importance:
    ///  - <c>ExpandMore</c>, which draws the expanded icon 1.35x larger once you zoom out far
    ///    enough - vanilla uses it for map parents you are currently standing on;
    ///  - <c>ExpandingIconColor</c>, tinted toward white so the seat reads brighter than the
    ///    faction's ordinary towns while keeping its faction colour;
    ///  - <c>ExpandingIconPriority</c>, so a capital draws on top when neighbouring settlements
    ///    overlap at planet zoom.
    ///
    /// Patch targets verified against the 1.6 assembly: ExpandingIconColor and
    /// ExpandingIconPriority are declared on WorldObject (Settlement does not override them), and
    /// ExpandMore is declared on MapParent - so those are the types patched, not Settlement.
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.ExpandMore), MethodType.Getter)]
    public static class Patch_CapitalExpandMore
    {
        [HarmonyPostfix]
        public static void Postfix(MapParent __instance, ref bool __result)
        {
            if (__result) return;
            if (__instance is Settlement s && FDCapitals.IsCapital(s))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ExpandingIconColor), MethodType.Getter)]
    public static class Patch_CapitalIconColor
    {
        [HarmonyPostfix]
        public static void Postfix(WorldObject __instance, ref Color __result)
        {
            if (!(__instance is Settlement s) || !FDCapitals.IsCapital(s)) return;

            // Keep the faction's colour identity, but lift it well clear of its neighbours.
            // Alpha is set by the caller after this returns, so leave it alone.
            float a = __result.a;
            __result = Color.Lerp(__result, Color.white, 0.45f);
            __result.a = a;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ExpandingIconPriority), MethodType.Getter)]
    public static class Patch_CapitalIconPriority
    {
        [HarmonyPostfix]
        public static void Postfix(WorldObject __instance, ref float __result)
        {
            if (!(__instance is Settlement s) || !FDCapitals.IsCapital(s)) return;

            __result += 1f;
        }
    }
}
