using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// A demand for tribute from a faction that has fallen on hard times.
    ///
    /// This is the first thing Faction Dynamics has ever asked the player to *decide*. Everything
    /// before it was simulation the player watched happen: raids arriving with a reason, settlements
    /// rising and falling, goodwill drifting. That difference is not cosmetic - it is the reason
    /// this file is careful in ways the rest of the mod does not need to be.
    ///
    /// MULTIPLAYER. Every other FD feature is safe under Multiplayer for free, because it runs
    /// inside the game's own ticks and MP replays those identically on every client. A button in a
    /// letter does not: it is a UI event on one machine. Multiplayer syncs vanilla's choice letters
    /// by explicitly registering each one (SyncDelegates.InitChoiceLetters names ChoiceLetter_
    /// RansomDemand, _AcceptJoiner and six others by type); nothing walks ChoiceLetter subclasses,
    /// so a mod letter gets no handler and its option runs locally. Paying would spend silver on the
    /// clicking client alone and desync the session within a tick.
    ///
    /// So the options below are NAMED PUBLIC METHODS rather than lambdas, and
    /// <see cref="MultiplayerCompat"/> registers them by name. Lambdas would work - MP can register
    /// `LambdaInGetter(type, nameof(Choices), ordinal)` - but the ordinal is an index into Roslyn's
    /// generated names for this property body, so adding or reordering a single option silently
    /// shifts it and syncs the wrong button. A named method has no ordinal to get wrong.
    /// </summary>
    public class ChoiceLetter_FDTribute : ChoiceLetter
    {
        public Faction demandingFaction;
        public int silverDemanded;

        /// <summary>
        /// Set the moment the player answers, so <see cref="Removed"/> can tell "they refused" from
        /// "it expired" from "the letter is being torn down at end of game".
        /// </summary>
        private bool answered;

        public override bool CanShowInLetterStack =>
            base.CanShowInLetterStack && demandingFaction != null && !demandingFaction.defeated;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }

                var accept = new DiaOption("FD_TributeAccept".Translate(silverDemanded))
                {
                    action = Accept,
                    resolveTree = true
                };

                // Vanilla's ransom demand disables rather than hides the option when the colony is
                // short, so the player can see the price they cannot meet. Same here.
                Map payFrom = TradeUtility.PlayerHomeMapWithMostLaunchableSilver();
                if (payFrom == null || !TradeUtility.ColonyHasEnoughSilver(payFrom, silverDemanded))
                    accept.Disable("FD_TributeNotEnoughSilver".Translate(silverDemanded));

                yield return accept;

                // Postpone is vanilla's own - it closes the dialog and leaves the letter in the
                // stack, which is exactly what is wanted and needs no syncing because it changes
                // no game state.
                yield return Option_Postpone;

                // Reject is NOT vanilla's Option_Reject: that one just dismisses the letter, and a
                // refusal here has consequences. Same label key so it reads identically.
                yield return new DiaOption("RejectLetter".Translate())
                {
                    action = Refuse,
                    resolveTree = true
                };
            }
        }

        /// <summary>
        /// Pay. Synced under Multiplayer - see the class comment.
        ///
        /// The silver map is resolved here rather than captured when the letter was built, because
        /// the letter can sit in the stack for days and the map that had the silver may have been
        /// lost in the meantime.
        /// </summary>
        public void Accept()
        {
            answered = true;

            Map payFrom = TradeUtility.PlayerHomeMapWithMostLaunchableSilver();
            if (payFrom == null || !TradeUtility.ColonyHasEnoughSilver(payFrom, silverDemanded))
            {
                // Reachable in multiplayer: another player can spend the silver between this client
                // rendering the option and the synced call landing. Fail quietly rather than
                // launching silver that is not there.
                Messages.Message("FD_TributeNotEnoughSilver".Translate(silverDemanded),
                                 MessageTypeDefOf.RejectInput, false);
                return;
            }

            TradeUtility.LaunchSilver(payFrom, silverDemanded);

            if (demandingFaction != null)
            {
                // Passing the HistoryEventDef as `reason` is not decoration: Faction.
                // TryAffectGoodwillWith records the event itself, with AffectedFaction and
                // CustomGoodwill args attached, exactly the way vanilla records its own goodwill
                // changes. Hand-calling HistoryEventsManager.RecordEvent afterwards would double it.
                demandingFaction.TryAffectGoodwillWith(
                    Faction.OfPlayer,
                    FDTributeTuning.GoodwillForPaying,
                    canSendMessage: false,
                    canSendHostilityLetter: true,
                    reason: FDHistoryEventDefOf.FD_TributePaid,
                    lookTarget: null);

                // Grudge relief is deliberately NOT applied here. Patch_GoodwillChanged already
                // converts any positive goodwill change into grudge relief, and it will see the
                // call above. Doing it again here would pay the player twice for one decision.

                FDLog.Message("Tribute paid: " + silverDemanded + " silver to " + demandingFaction.Name);
            }

            Find.LetterStack.RemoveLetter(this);

            Find.LetterStack.ReceiveLetter(
                "FD_TributePaidLabel".Translate(),
                "FD_TributePaidText".Translate(silverDemanded, demandingFaction?.Name ?? "FD_TheRaiders".Translate()),
                LetterDefOf.NeutralEvent);
        }

        /// <summary>Refuse. Synced under Multiplayer - see the class comment.</summary>
        public void Refuse()
        {
            answered = true;
            ApplyRefusal(explicitRefusal: true);
            Find.LetterStack.RemoveLetter(this);

            Find.LetterStack.ReceiveLetter(
                "FD_TributeRefusedLabel".Translate(),
                "FD_TributeRefusedText".Translate(demandingFaction?.Name ?? "FD_TheRaiders".Translate()),
                LetterDefOf.NegativeEvent);
        }

        /// <summary>
        /// Letting the demand lapse.
        ///
        /// Without this, ignoring the letter would be strictly better than refusing it - same
        /// outcome, no consequence - which makes the Refuse button a trap for players who engage
        /// with the choice honestly. Silence is treated as refusal, at a reduced rate: they were
        /// not told no, they were just not answered.
        /// </summary>
        public override void Removed()
        {
            base.Removed();

            if (answered) return;
            if (!TimeoutPassed) return;   // torn down for some other reason; not the player's doing

            ApplyRefusal(explicitRefusal: false);

            Messages.Message(
                "FD_MessageTributeExpired".Translate(demandingFaction?.Name ?? "FD_TheRaiders".Translate()),
                MessageTypeDefOf.NeutralEvent, false);
        }

        private void ApplyRefusal(bool explicitRefusal)
        {
            if (demandingFaction == null) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            FactionRuntimeData fd = comp?.GetFactionData(demandingFaction, false);
            if (fd != null)
            {
                fd.AddGrudge(explicitRefusal
                    ? FDTributeTuning.GrudgeForRefusing
                    : FDTributeTuning.GrudgeForIgnoring);
            }

            demandingFaction.TryAffectGoodwillWith(
                Faction.OfPlayer,
                explicitRefusal ? FDTributeTuning.GoodwillForRefusing : FDTributeTuning.GoodwillForIgnoring,
                canSendMessage: false,
                canSendHostilityLetter: true,
                reason: FDHistoryEventDefOf.FD_TributeRefused,
                lookTarget: null);

            FDLog.Message("Tribute " + (explicitRefusal ? "refused" : "ignored") + ": "
                          + demandingFaction.Name + ", grudge now "
                          + (fd != null ? fd.grudge.ToStringPercent() : "n/a"));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref demandingFaction, "fdDemandingFaction");
            Scribe_Values.Look(ref silverDemanded, "fdSilverDemanded", 0);
            Scribe_Values.Look(ref answered, "fdAnswered", false);
        }
    }
}
