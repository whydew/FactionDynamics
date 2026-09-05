using System.Text;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Shows a settlement's regrouping state in the world-map inspect pane, so the player can see
    /// that the base which just raided them is weakened - and for how long.
    /// </summary>
    [HarmonyPatch(typeof(Settlement), nameof(Settlement.GetInspectString))]
    public static class Patch_SettlementInspect
    {
        [HarmonyPostfix]
        public static void Postfix(Settlement __instance, ref string __result)
        {
            if (!FactionDynamicsWorldComp.Config.settlements) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;

            int now = Find.TickManager.TicksGame;
            var sb = new StringBuilder(__result ?? "");

            // The seat of the faction: worth calling out first, since it is the one settlement here
            // that never quietly disappears and the one a strike quest treats as a real prize.
            if (FDCapitals.IsCapital(__instance))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("FD_InspectCapital".Translate(__instance.Faction.Name));
            }

            // The owning faction's mood, so the world map explains why raids from here look the way
            // they do without the player having to open the Factions tab. Named explicitly,
            // because the settlement's own hardship follows on the next line and two bare
            // "Hardship 100%" lines in a row told the player nothing about which was which.
            if (FDFactionMoodUI.HasAnythingToShow(__instance.Faction, out float hardship, out float grudge))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(FDFactionMoodUI.MoodLineFor(__instance.Faction, hardship, grudge));
            }

            SettlementRuntimeData data = comp.GetSettlementData(__instance, false);

            // This settlement's own hardship, which is what decides whether raids *from here*
            // arrive starving - not the faction average. Always printed, including at zero: "this
            // town is fine" is as useful to know as "this town is starving", and a line that only
            // appears above some invisible threshold reads as a bug.
            //
            // Read-only lookup, so a settlement we have not evaluated yet reports 0% rather than
            // having runtime data created for it from a draw path.
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("FD_InspectSettlementHardship".Translate(
                (data != null && data.hardshipInitialized ? data.hardship : 0f).ToStringPercent()));

            if (data == null)
            {
                __result = sb.ToString();
                return;
            }

            if (data.IsRegrouping(now))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("FD_InspectRegrouping".Translate(data.RegroupDaysLeft(now).ToString()));
            }
            else if (data.strengthFactor >= 1.2f)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("FD_InspectThriving".Translate());
            }
            else if (data.strengthFactor <= 0.8f)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("FD_InspectStruggling".Translate());
            }

            __result = sb.ToString();
        }
    }
}
