using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Tells the player where the raid came from and why, in the raid letter they already read.
    /// No new UI - the information rides along in vanilla's own letter text.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterText")]
    public static class Patch_RaidLetter
    {
        [HarmonyPostfix]
        public static void Postfix(IncidentParms parms, List<Pawn> pawns, ref string __result)
        {
            if (!FDRaidContext.Active) return;
            if (string.IsNullOrEmpty(__result)) return;

            string extra = "";

            if (FDRaidContext.Origin != null && FactionDynamicsWorldComp.Config.raidGeography)
            {
                extra += "\n\n" + "FD_RaidOriginText".Translate(
                    FDRaidContext.Origin.Label,
                    UnityEngine.Mathf.RoundToInt(FDRaidContext.OriginDistance).ToString());
            }

            FDRaidMotivationDef motivation = FDRaidContext.Motivation;
            if (motivation != null && !motivation.letterSuffixKey.NullOrEmpty())
            {
                extra += "\n\n" + motivation.letterSuffixKey.Translate(
                    parms?.faction != null ? parms.faction.Name : "");
            }

            if (extra.Length > 0)
                __result += extra;
        }
    }
}
