using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Surfaces hardship and grudge where the player already looks for faction information: the
    /// Factions tab and the faction report text. Without this the two numbers that drive raid
    /// motivations are invisible, and a starvation or revenge raid looks arbitrary rather than
    /// earned.
    /// </summary>
    public static class FDFactionMoodUI
    {
        /// <summary>Below this, the faction is unremarkable and we say nothing rather than add noise.</summary>
        private const float ShowThreshold = 0.05f;

        public static bool HasAnythingToShow(Faction faction, out float hardship, out float grudge)
        {
            hardship = 0f;
            grudge = 0f;

            if (faction == null || faction.IsPlayer || faction.Hidden) return false;
            if (!FactionDynamicsWorldComp.Config.raidMotivations
                && !FactionDynamicsWorldComp.Config.settlements) return false;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            FactionRuntimeData fd = comp?.GetFactionData(faction, false);
            if (fd == null) return false;

            hardship = fd.hardship;
            grudge = fd.grudge;
            return hardship >= ShowThreshold || grudge >= ShowThreshold;
        }

        public static string MoodLine(float hardship, float grudge)
        {
            return "FD_FactionMoodLine".Translate(hardship.ToStringPercent(), grudge.ToStringPercent());
        }

        public static string MoodTooltip(float hardship, float grudge)
        {
            return "FD_FactionMoodTip".Translate(hardship.ToStringPercent(), grudge.ToStringPercent());
        }
    }

    /// <summary>Adds a hardship/grudge line to each faction's block in the Factions tab.</summary>
    [HarmonyPatch(typeof(FactionUIUtility), nameof(FactionUIUtility.DrawRelatedFactionInfo))]
    public static class Patch_FactionTabMood
    {
        [HarmonyPostfix]
        public static void Postfix(Rect rect, Faction faction, ref float curY)
        {
            if (!FDFactionMoodUI.HasAnythingToShow(faction, out float hardship, out float grudge)) return;

            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.85f, 0.8f, 0.7f);

            var row = new Rect(rect.x, curY, rect.width, 20f);
            Widgets.Label(row, FDFactionMoodUI.MoodLine(hardship, grudge));
            TooltipHandler.TipRegion(row, FDFactionMoodUI.MoodTooltip(hardship, grudge));
            curY += 20f;

            GUI.color = oldColor;
            Text.Font = oldFont;
        }
    }

    /// <summary>
    /// Adds the same information to the faction report text, which is what the faction info card
    /// shows - so it is available from anywhere the player can open a faction, not just the tab.
    /// </summary>
    [HarmonyPatch(typeof(Faction), nameof(Faction.GetReportText), MethodType.Getter)]
    public static class Patch_FactionReportText
    {
        [HarmonyPostfix]
        public static void Postfix(Faction __instance, ref string __result)
        {
            if (!FDFactionMoodUI.HasAnythingToShow(__instance, out float hardship, out float grudge)) return;

            __result = (__result ?? "") + "\n\n" + FDFactionMoodUI.MoodTooltip(hardship, grudge);
        }
    }
}
