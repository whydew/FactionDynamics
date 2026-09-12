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
        /// <summary>
        /// True if this faction is one the mod simulates and therefore has a mood worth printing.
        /// Zero is a real answer here - "hardship 0%, grudge 0%" tells the player the faction is
        /// comfortable and holds nothing against them, which is information. An earlier version
        /// hid anything under 5% and the numbers simply appeared and vanished with no explanation.
        /// </summary>
        public static bool HasAnythingToShow(Faction faction, out float hardship, out float grudge)
        {
            hardship = 0f;
            grudge = 0f;

            if (faction == null || faction.IsPlayer || faction.Hidden) return false;
            if (faction.def == null || faction.temporary) return false;
            if (!FactionDynamicsWorldComp.Config.raidMotivations
                && !FactionDynamicsWorldComp.Config.settlements) return false;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return false;

            // Read-only: never create runtime data from a draw path. A faction we have not
            // evaluated yet legitimately reads 0/0.
            FactionRuntimeData fd = comp.GetFactionData(faction, false);
            if (fd != null)
            {
                hardship = fd.hardship;
                grudge = fd.grudge;
            }

            return true;
        }

        public static string MoodLine(float hardship, float grudge)
        {
            return "FD_FactionMoodLine".Translate(hardship.ToStringPercent(), grudge.ToStringPercent());
        }

        /// <summary>The mood line prefixed with whose mood it is - for panes that also show a settlement's own.</summary>
        public static string MoodLineFor(Faction faction, float hardship, float grudge)
        {
            return "FD_InspectFactionMood".Translate(faction.Name,
                hardship.ToStringPercent(), grudge.ToStringPercent());
        }

        public static string MoodTooltip(float hardship, float grudge)
        {
            return "FD_FactionMoodTip".Translate(hardship.ToStringPercent(), grudge.ToStringPercent());
        }
    }

    /// <summary>
    /// Adds a hardship/grudge line to each faction's row in the Factions tab.
    ///
    /// Patch target verified against the 1.6 assembly: the tab draws its rows through the private
    /// <c>FactionUIUtility.DrawFactionRow(Faction, float, Rect)</c>. The public
    /// <c>DrawRelatedFactionInfo</c> is only called by dialog boxes, never by the tab - patching
    /// that one (as this originally did) put the line somewhere the player never sees.
    ///
    /// Layout, read off the same method: the row's text block is <c>Rect(90, rowY, 300, 80)</c>
    /// holding name / type / leader, so the mood line goes on a fourth line inside the same 80px
    /// row and the row height the method returns is left alone.
    /// </summary>
    [HarmonyPatch(typeof(FactionUIUtility), "DrawFactionRow")]
    public static class Patch_FactionTabMood
    {
        [HarmonyPostfix]
        public static void Postfix(Faction faction, float rowY)
        {
            if (!FDFactionMoodUI.HasAnythingToShow(faction, out float hardship, out float grudge)) return;

            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.78f, 0.85f);

            // rowY + 62 sits clear of the leader line above and still inside the 80px row the
            // patched method returns, so nothing overlaps and the row height is left alone.
            var row = new Rect(90f, rowY + 62f, 300f, 18f);
            Widgets.Label(row, FDFactionMoodUI.MoodLine(hardship, grudge));

            GUI.color = oldColor;
            Text.Font = oldFont;

            TooltipHandler.TipRegion(row, FDFactionMoodUI.MoodTooltip(hardship, grudge));
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
