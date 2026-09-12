using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// A desperate neighbour demands silver.
    ///
    /// This is the first thing the storyteller comp built in Package 2 actually emits. That comp
    /// exists precisely so FD has a tick-driven, Multiplayer-safe place to originate events, instead
    /// of the interception machinery it uses for raids - and a tribute demand is the right first
    /// tenant, because it is something vanilla would never produce on its own and it does not
    /// compete with the storyteller for the threat budget.
    ///
    /// The design argument: FD already models factions that are starving, and already lets them
    /// express that by raiding. Raiding is the only voice they have. Tribute gives a hungry faction
    /// a way to ask before it takes, which makes hardship legible to the player at a point where
    /// they can still do something about it - and makes the raid that follows a refusal feel earned
    /// rather than arbitrary.
    /// </summary>
    public class IncidentWorker_FDTribute : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!base.CanFireNowSub(parms)) return false;
            if (!FactionDynamicsWorldComp.Config.raidMotivations) return false;
            if (!(parms.target is Map map) || !map.IsPlayerHome) return false;
            if (FactionDynamicsWorldComp.Current == null) return false;

            return FindCandidates(map).Count > 0;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            if (!(parms.target is Map map) || !map.IsPlayerHome) return false;

            List<Faction> candidates = FindCandidates(map);
            if (candidates.Count == 0) return false;

            Faction faction;
            int silver;

            // One seeded scope covering both rolls. The seed is built only from values every client
            // agrees on - the map's tile and the current tick - so each client that replays this
            // tick picks the same faction and the same number.
            using (FDRand.Push((int)map.Tile, Find.TickManager.TicksGame, FDRandSalt.Tribute))
            {
                faction = candidates[Rand.Range(0, candidates.Count)];
                silver = SilverFor(faction, map);
            }

            var letter = (ChoiceLetter_FDTribute)LetterMaker.MakeLetter(
                "FD_TributeLetterLabel".Translate(faction.Name),
                "FD_TributeLetterText".Translate(faction.Name, silver),
                FDLetterDefOf.FD_TributeDemand);

            letter.demandingFaction = faction;
            letter.silverDemanded = silver;
            letter.StartTimeout(FDTributeTuning.TimeoutDays * GenDate.TicksPerDay);

            Find.LetterStack.ReceiveLetter(letter);

            // Recorded here rather than when the player answers, because this is what the cooldown
            // below is asking about: "has this faction already come asking recently?" - a question
            // that has the same answer whether or not the player ever opened the letter.
            Find.HistoryEventsManager.RecordEvent(
                new HistoryEvent(FDHistoryEventDefOf.FD_TributeDemanded,
                                 faction.Named(HistoryEventArgsNames.AffectedFaction)));

            FDLog.Message("Tribute demanded by " + faction.Name + ": " + silver + " silver"
                          + " (hardship " + HardshipOf(faction).ToStringPercent() + ")");
            return true;
        }

        /// <summary>
        /// Factions desperate enough to ask, close enough to matter, and not so far gone that they
        /// would rather just come and take it.
        ///
        /// Sorted by loadID before returning. The caller picks from this list with a seeded roll,
        /// and a seeded roll over a differently-ordered list is not deterministic - this is the
        /// same rule the settlement lifecycle follows, and the reason it is written down here is
        /// that the bug it prevents only ever shows up as a desync hours into a session.
        /// </summary>
        private static List<Faction> FindCandidates(Map map)
        {
            var result = new List<Faction>();

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return result;

            int cooldownTicks = FDTributeTuning.CooldownDays * GenDate.TicksPerDay;
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;

            for (int i = 0; i < all.Count; i++)
            {
                Faction f = all[i];
                if (f == null || f.IsPlayer || f.Hidden || f.defeated) continue;
                if (!f.HasGoodwill || f.temporary) continue;

                // Already at war: they are not sending a messenger, they are sending raiders.
                if (f.HostileTo(Faction.OfPlayer)) continue;

                FactionRuntimeData fd = comp.GetFactionData(f, false);
                if (fd == null) continue;
                if (fd.hardship < FDTributeTuning.MinHardship) continue;
                if (fd.grudge > FDTributeTuning.MaxGrudge) continue;

                if (Find.HistoryEventsManager.GetRecentCountWithinTicks(
                        FDHistoryEventDefOf.FD_TributeDemanded, cooldownTicks, f) > 0)
                    continue;

                if (NearestSettlementDistance(f, map) > FDTributeTuning.MaxTiles) continue;

                result.Add(f);
            }

            result.Sort((a, b) => a.loadID.CompareTo(b.loadID));
            return result;
        }

        private static float NearestSettlementDistance(Faction faction, Map map)
        {
            List<Settlement> owned = FDWorldUtil.SettlementsOf(faction);
            float best = float.MaxValue;

            for (int i = 0; i < owned.Count; i++)
            {
                float d = Find.WorldGrid.ApproxDistanceInTiles(map.Tile, owned[i].Tile);
                if (d < best) best = d;
            }

            return best;
        }

        /// <summary>
        /// How much they ask for.
        ///
        /// Two inputs, and the smaller wins. Hardship sets what they need; colony wealth sets what
        /// they could plausibly believe the player has. Without the wealth term an early colony gets
        /// a demand it cannot possibly meet, which is not a decision - it is a letter with one
        /// working button. Rounded to something a person would actually say out loud.
        /// </summary>
        private static int SilverFor(Faction faction, Map map)
        {
            float hardship = HardshipOf(faction);
            float t = Mathf.InverseLerp(FDTributeTuning.MinHardship, 1f, hardship);

            float byNeed = Mathf.Lerp(FDTributeTuning.BaseSilverMin, FDTributeTuning.BaseSilverMax, t);
            float byWealth = map.wealthWatcher.WealthTotal * FDTributeTuning.MaxWealthFraction;

            float raw = Mathf.Min(byNeed, Mathf.Max(byWealth, FDTributeTuning.BaseSilverMin * 0.5f));

            // Round to the nearest 25 so the number reads as a demand rather than a calculation.
            return Mathf.Max(25, Mathf.RoundToInt(raw / 25f) * 25);
        }

        private static float HardshipOf(Faction faction)
        {
            FactionRuntimeData fd = FactionDynamicsWorldComp.Current?.GetFactionData(faction, false);
            return fd?.hardship ?? 0f;
        }
    }

    [DefOf]
    public static class FDLetterDefOf
    {
        public static LetterDef FD_TributeDemand;

        static FDLetterDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDLetterDefOf));
        }
    }

    [DefOf]
    public static class FDIncidentDefOf
    {
        public static IncidentDef FD_TributeDemand;

        static FDIncidentDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDIncidentDefOf));
        }
    }
}
