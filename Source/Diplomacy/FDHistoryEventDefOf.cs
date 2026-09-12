using RimWorld;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// The mod's own entries in the game's historical record.
    ///
    /// Why bother, when FD already tracks grudge and hardship itself? Because those are FD's
    /// private numbers and nothing else can read them. A HistoryEventDef is the game's shared
    /// vocabulary for "this happened, to this faction, worth this much goodwill", and three separate
    /// systems consume it without FD having to talk to any of them:
    ///
    ///   - Faction.TryAffectGoodwillWith takes a HistoryEventDef as its `reason` and records the
    ///     event with the goodwill delta attached, so FD's diplomacy shows up in the same ledger as
    ///     vanilla's.
    ///   - HistoryEventsManager.GetRecentCountWithinTicks(def, ticks, faction) answers "how many
    ///     times lately?" - which is how the tribute system rate-limits itself without inventing
    ///     yet another per-faction timestamp field.
    ///   - PreceptComp_GoodwillSituation.Notify_HistoryEvent lets ideoligions react, so a precept
    ///     could in principle care that the colony pays tribute. FD does not ship such a precept;
    ///     it just stops being invisible to one that exists.
    ///
    /// [DefOf] fields are populated by the game after defs load; the static constructor call is the
    /// standard guard against them being read before that happens.
    /// </summary>
    [DefOf]
    public static class FDHistoryEventDefOf
    {
        /// <summary>The colony paid a faction's tribute demand.</summary>
        public static HistoryEventDef FD_TributePaid;

        /// <summary>The colony refused a tribute demand, or let it lapse.</summary>
        public static HistoryEventDef FD_TributeRefused;

        /// <summary>A faction demanded tribute. Recorded whether or not the player answers.</summary>
        public static HistoryEventDef FD_TributeDemanded;

        static FDHistoryEventDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FDHistoryEventDefOf));
        }
    }

    /// <summary>Tuning for tribute demands, kept in one place so the balance is legible.</summary>
    public static class FDTributeTuning
    {
        /// <summary>A faction must be at least this desperate before it will stoop to asking.</summary>
        public const float MinHardship = 0.35f;

        /// <summary>Grudge above this and they do not ask, they raid. Asking implies some restraint.</summary>
        public const float MaxGrudge = 0.65f;

        /// <summary>Only factions within this many tiles bother - a demand has to be enforceable.</summary>
        public const float MaxTiles = 90f;

        /// <summary>Silver demanded at minimum hardship, before wealth scaling.</summary>
        public const int BaseSilverMin = 250;

        /// <summary>Silver demanded at maximum hardship, before wealth scaling.</summary>
        public const int BaseSilverMax = 900;

        /// <summary>Fraction of colony wealth the demand is allowed to reach at most.</summary>
        public const float MaxWealthFraction = 0.04f;

        /// <summary>Days the player has to answer before it lapses.</summary>
        public const int TimeoutDays = 6;

        /// <summary>A faction will not demand again within this many days of its last demand.</summary>
        public const int CooldownDays = 25;

        public const int GoodwillForPaying = 12;
        public const int GoodwillForRefusing = -8;

        /// <summary>Ignoring reads as refusal, but a softer one - they were not told no.</summary>
        public const int GoodwillForIgnoring = -4;

        public const float GrudgeForRefusing = 0.10f;
        public const float GrudgeForIgnoring = 0.05f;
    }
}
