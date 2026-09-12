using System;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    public class FDSettlementCompProperties : WorldObjectCompProperties
    {
        public FDSettlementCompProperties()
        {
            compClass = typeof(FDSettlementComp);
        }
    }

    /// <summary>
    /// Per-settlement state, living on the settlement itself.
    ///
    /// This replaces a Dictionary&lt;int, SettlementRuntimeData&gt; in the world component, and the
    /// reason is worth writing down because the old comment argued the opposite case.
    ///
    /// The dictionary needed a parallel sorted key list to iterate deterministically, a maintenance
    /// pass to find ids whose settlement no longer existed, and explicit scribing with staging
    /// lists. All three were hand-written, and two of the defects found in the 2026-09-11 review
    /// were failures of exactly that shape - state keyed by id, outliving the thing it described.
    /// A WorldObjectComp is scribed with its parent, destroyed with its parent, and contributes to
    /// its parent's inspect string. None of that code has to exist.
    ///
    /// The one genuine argument for the dictionary was that it works for settlement types added by
    /// other mods without us knowing about them. That still holds against an XML patch on the
    /// vanilla Settlement def - which is why <see cref="FDSettlementCompInjector"/> attaches this
    /// to every WorldObjectDef whose worldObjectClass derives from Settlement instead.
    ///
    /// Load order note: WorldObject.ExposeData calls InitializeComps during LoadingVars, before the
    /// comps' PostExposeData runs, so a settlement saved before this comp existed still gets one on
    /// load - with empty data, which is what the migration in FactionDynamicsWorldComp is for.
    /// </summary>
    public class FDSettlementComp : WorldObjectComp
    {
        private SettlementRuntimeData data = new SettlementRuntimeData();

        /// <summary>The settlement's state. Never null.</summary>
        public SettlementRuntimeData Data => data ??= new SettlementRuntimeData();

        public Settlement ParentSettlement => parent as Settlement;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref data, "fdData");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                data ??= new SettlementRuntimeData();
        }

        /// <summary>
        /// The mod's half of the world-map inspect pane. This used to be a Harmony postfix on
        /// Settlement.GetInspectString; WorldObject.GetInspectString already walks its comps and
        /// appends this, so the patch is gone.
        ///
        /// Vanilla logs an error if this ends in whitespace, so the text is assembled with explicit
        /// separators and trimmed at the end rather than by appending newlines as it goes.
        /// </summary>
        public override string CompInspectStringExtra()
        {
            if (!FactionDynamicsWorldComp.Config.settlements) return null;

            Settlement settlement = ParentSettlement;
            if (settlement == null || settlement.Faction == null) return null;

            var sb = new System.Text.StringBuilder();
            int now = Find.TickManager.TicksGame;

            // The seat of the faction: worth calling out first, since it is the one settlement here
            // that never quietly disappears and the one a strike quest treats as a real prize.
            if (FDCapitals.IsCapital(settlement))
                Line(sb, "FD_InspectCapital".Translate(settlement.Faction.Name));

            // The owning faction's mood, so the world map explains why raids from here look the way
            // they do without the player having to open the Factions tab. Named explicitly, because
            // the settlement's own hardship follows on the next line and two bare "Hardship 100%"
            // lines in a row told the player nothing about which was which.
            if (FDFactionMoodUI.HasAnythingToShow(settlement.Faction, out float hardship, out float grudge))
                Line(sb, FDFactionMoodUI.MoodLineFor(settlement.Faction, hardship, grudge));

            // This settlement's own hardship, which is what decides whether raids *from here* arrive
            // starving - not the faction average. Always printed, including at zero: "this town is
            // fine" is as useful to know as "this town is starving", and a line that only appears
            // above some invisible threshold reads as a bug.
            Line(sb, "FD_InspectSettlementHardship".Translate(
                (data != null && data.hardshipInitialized ? data.hardship : 0f).ToStringPercent()));

            if (data != null)
            {
                if (data.IsRegrouping(now))
                    Line(sb, "FD_InspectRegrouping".Translate(data.RegroupDaysLeft(now).ToString()));
                else if (data.strengthFactor >= 1.2f)
                    Line(sb, "FD_InspectThriving".Translate());
                else if (data.strengthFactor <= 0.8f)
                    Line(sb, "FD_InspectStruggling".Translate());
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }

        private static void Line(System.Text.StringBuilder sb, string text)
        {
            if (text.NullOrEmpty()) return;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(text);
        }
    }

    /// <summary>
    /// Attaches <see cref="FDSettlementComp"/> to every settlement-like WorldObjectDef at startup.
    ///
    /// Deliberately not an XML PatchOperation on the vanilla Settlement def. Other mods ship their
    /// own settlement-like WorldObjectDefs whose worldObjectClass derives from Settlement, and the
    /// dictionary this replaced tracked those without needing to know they existed. Naming defs in
    /// XML would have quietly dropped them - a regression dressed up as a refactor. Asking the type
    /// system which defs are settlements covers mods loaded after this one, too.
    ///
    /// Must run before any world loads, because WorldObject.InitializeComps reads def.comps when the
    /// object is created or loaded. A static constructor is that slot.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class FDSettlementCompInjector
    {
        static FDSettlementCompInjector()
        {
            int injected = 0;

            foreach (WorldObjectDef def in DefDatabase<WorldObjectDef>.AllDefs)
            {
                if (def?.worldObjectClass == null) continue;
                if (!typeof(Settlement).IsAssignableFrom(def.worldObjectClass)) continue;

                def.comps ??= new System.Collections.Generic.List<WorldObjectCompProperties>();

                bool already = false;
                for (int i = 0; i < def.comps.Count; i++)
                {
                    if (def.comps[i] is FDSettlementCompProperties) { already = true; break; }
                }
                if (already) continue;

                def.comps.Add(new FDSettlementCompProperties());
                injected++;
            }

            FDLog.Message("Settlement state attached to " + injected + " world object def(s).");
        }
    }

    public static class FDSettlementCompUtil
    {
        /// <summary>
        /// This settlement's state, or null when it has no comp.
        ///
        /// A settlement can legitimately lack the comp: another mod may have built one from a def
        /// that does not derive from Settlement, or created it before injection ran. Callers that
        /// only want to read should tolerate null rather than assume.
        /// </summary>
        public static SettlementRuntimeData FDData(this Settlement settlement)
        {
            return settlement?.GetComponent<FDSettlementComp>()?.Data;
        }
    }
}
