using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace FactionDynamics
{
    /// <summary>
    /// A raid that came for something specific and leaves when it has it (or when it has bled
    /// enough). Standalone rather than a subclass of LordJob_AssaultColony, because that class
    /// builds its whole graph in one private block with no hooks.
    ///
    /// Save/load contract (this is the part that bites): the base LordJob.ExposeData is a no-op, so
    /// every field here is scribed explicitly, and CreateGraph() is called again from scratch after
    /// loading. Toils and triggers are matched back to their saved state purely by list index, so
    /// CreateGraph must be a pure function of the scribed fields - which is why the give-up timeout
    /// is rolled once in the constructor and stored, rather than re-rolled inside CreateGraph.
    /// </summary>
    public class LordJob_FDMotivatedRaid : LordJob
    {
        private Faction faction;
        private FDRaidMotivationDef motivation;
        private float lootGoal;
        private float fleeAtLossFraction = 0.4f;
        private int timeoutTicks = 25000;
        private bool goalMet;

        /// <summary>
        /// The graph's SHAPE, frozen at construction and scribed.
        ///
        /// CreateGraph has to be a pure function of scribed state, because toils and triggers are
        /// matched back to their saved state by list index alone. Deciding the shape from
        /// <see cref="motivation"/> broke that quietly: the def is data, and data changes. Rename
        /// FD_Plunder, remove a lootDuty, open a save against a slightly edited Defs folder, and the
        /// graph comes back a different shape than the one the indices were recorded against - the
        /// Lord restores into the wrong toil with the wrong triggers.
        ///
        /// Reading these from scribed bools instead means the shape is whatever it was when the raid
        /// started, for the life of that raid, no matter what happens to the defs underneath it.
        /// </summary>
        private bool graphShapeSaved;
        private bool graphHasLoot;
        private bool graphDamageSatisfaction;

        /// <summary>
        /// What each raider was already carrying the first time we looked, keyed by pawn ID. The
        /// goal counts the difference from this, never the raw total.
        ///
        /// Belt and braces against the raid satisfying itself with its own supplies: vanilla hands
        /// raiders rations and then distributes raid loot into their packs, and any mod can add
        /// more. arriveWithoutFood empties them, but relying on that alone made the goal one bad
        /// interaction away from a raid that walked in, checked its own pockets and walked out.
        /// Measuring the delta means only what they take off the player can ever count.
        ///
        /// The baseline is captured lazily on the first check, 120 ticks in - long before anyone
        /// has walked to a stockpile and picked something up.
        /// </summary>
        private Dictionary<int, float> baselineLoot = new Dictionary<int, float>();
        private List<int> tmpBaselineKeys;
        private List<float> tmpBaselineValues;

        public FDRaidMotivationDef Motivation => motivation;

        public LordJob_FDMotivatedRaid()
        {
        }

        public LordJob_FDMotivatedRaid(Faction faction, FDRaidMotivationDef motivation,
            float lootGoal, float fleeAtLossFraction, int timeoutTicks)
        {
            this.faction = faction;
            this.motivation = motivation;
            this.lootGoal = lootGoal;
            this.fleeAtLossFraction = fleeAtLossFraction;
            this.timeoutTicks = timeoutTicks;

            graphHasLoot = ShapeHasLoot(motivation);
            graphDamageSatisfaction = motivation != null && motivation.useColonyDamageSatisfaction;
            graphShapeSaved = true;
        }

        /// <summary>The shape test, in one place, so the constructor and the old-save fallback agree.</summary>
        private static bool ShapeHasLoot(FDRaidMotivationDef m)
        {
            return m != null && m.lootCategory != FDLootCategory.None && m.lootDuty != null;
        }

        /// <summary>We own retreat behaviour explicitly; don't let the base class add its own flee toil.</summary>
        public override bool AddFleeToil => false;

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();

            bool hasLoot = graphHasLoot;

            // The toil the raid lives in.
            //
            // A raid that came to TAKE something starts taking it, immediately. This used to open
            // with a LordToil_AssaultColony phase and only switch to looting after
            // lootPhaseStartTicks (15-25 seconds), which meant every starvation raid began as an
            // ordinary assault: they walked in and shot colonists on sight, unprovoked, and only
            // went for the food afterwards. That erased the whole point of the motivation - a
            // starving raid is supposed to read differently from a war party.
            //
            // Fighting is not lost, it is just no longer the opening move: the loot duty's
            // ThinkNode_HarmedRecently node turns a raider into a combatant the moment they are
            // actually hurt. Shoot them and they fight; leave them alone and they rob you and go.
            //
            // A revenge raid has no loot category, so it still starts - and stays - in assault.
            // hasLoot comes from a scribed bool, but the duty it needs still lives in a def that may
            // since have been renamed or removed. Falling back to assault keeps the toil COUNT the
            // same - which is what the index-matched restore actually depends on - instead of
            // null-reffing on a raid the player is already fighting.
            DutyDef lootDuty = hasLoot ? motivation?.lootDuty : null;

            LordToil main = lootDuty != null
                ? (LordToil)new LordToil_FDLoot(lootDuty)
                : new LordToil_AssaultColony(false, false);
            graph.AddToil(main);
            graph.StartingToil = main;

            var exit = new LordToil_ExitMap(LocomotionUrgency.Jog, false, true);
            exit.useAvoidGrid = true;
            graph.AddToil(exit);

            // Got what they came for - leave.
            //
            // Added unconditionally, even on a raid with no loot goal. Toils and triggers are
            // restored after loading purely by LIST INDEX, so the graph must have the same shape on
            // load as it had on save. Gating this transition on the motivation def meant that a
            // renamed or removed def - or a save opened against a slightly different Defs folder -
            // shifted every index after it, and the Lord came back in the wrong toil with the wrong
            // triggers attached, which RimWorld reports as "trigger index out of bounds" if you are
            // lucky and as inexplicable raider behaviour if you are not.
            //
            // Trigger_FDGoalMet already returns false when there is no loot goal, so an
            // always-present transition costs one bool check and cannot fire on a raid that has
            // nothing to collect.
            var toExitGoal = new Transition(main, exit, false, true);
            toExitGoal.AddTrigger(new Trigger_FDGoalMet());
            toExitGoal.AddPreAction(new TransitionAction_Message(
                "FD_MessageRaidersGotWhatTheyCameFor".Translate(FactionLabel())));
            graph.AddTransition(toExitGoal, false);

            // Bled too much - break off. This is the escalation valve: a player who fights back
            // hard enough drives them off, rather than the raid deciding to become a battle.
            var toExitLosses = new Transition(main, exit, false, true);
            toExitLosses.AddTrigger(new Trigger_FractionPawnsLost(
                UnityEngine.Mathf.Clamp(fleeAtLossFraction, 0.05f, 0.95f)));
            toExitLosses.AddPreAction(new TransitionAction_Message(
                "FD_MessageRaidersBrokeOff".Translate(FactionLabel())));
            graph.AddTransition(toExitLosses, false);

            // Hard timeout, same idea as vanilla's give-up timer.
            var toExitTimeout = new Transition(main, exit, false, true);
            toExitTimeout.AddTrigger(new Trigger_TicksPassed(timeoutTicks).WithFilter(new TriggerFilter_MapExitable()));
            toExitTimeout.AddPreAction(new TransitionAction_Message(
                "FD_MessageRaidersGivingUp".Translate(FactionLabel())));
            graph.AddTransition(toExitTimeout, false);

            // Revenge raids also accept "we've hurt them enough" as a win condition.
            //
            // Keyed off the scribed bool, not off the def, for the reason given on graphShapeSaved:
            // this one genuinely has to stay conditional (the trigger would otherwise let ANY raid
            // leave once the colony was hurt enough), so it is the transition most able to shift
            // every index after it if its condition ever changed under a save.
            if (graphDamageSatisfaction)
            {
                var toExitSatisfied = new Transition(main, exit, false, true);
                toExitSatisfied.AddTrigger(new Trigger_FractionColonyDamageTaken(0.4f, 900f)
                    .WithFilter(new TriggerFilter_MapExitable()));
                toExitSatisfied.AddPreAction(new TransitionAction_Message(
                    "FD_MessageRaidersSatisfied".Translate(FactionLabel())));
                graph.AddTransition(toExitSatisfied, false);
            }

            // Peace broke out mid-raid.
            //
            // Unconditional. This used to be gated on faction != null, which is a scribed REFERENCE
            // - fine in the normal case, since cross-refs resolve before the graph is rebuilt, but
            // it would silently drop a transition (and shift nothing after it only because nothing
            // follows) if the faction ever failed to resolve. The trigger handles a null faction on
            // its own; the shape no longer depends on the answer.
            var toExitPeace = new Transition(main, exit, false, true);
            toExitPeace.AddTrigger(new Trigger_BecameNonHostileToPlayer());
            graph.AddTransition(toExitPeace, false);

            return graph;
        }

        private string FactionLabel()
        {
            return faction != null ? faction.Name : "FD_TheRaiders".Translate().ToString();
        }

        /// <summary>
        /// Have the raiders collected what they came for? Called from a throttled trigger (once
        /// every 120 ticks), and latched once true so it is a single field read afterwards.
        /// </summary>
        public bool CheckGoalMet(Lord lord)
        {
            if (goalMet) return true;
            if (motivation == null || motivation.lootCategory == FDLootCategory.None) return false;
            if (lootGoal <= 0f) return false;
            if (lord?.ownedPawns == null) return false;

            float total = 0f;
            List<Pawn> pawns = lord.ownedPawns;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.Dead) continue;

                float carrying = LootCarriedBy(pawn, motivation.lootCategory);

                // Only the difference from what they walked in with counts as stolen.
                if (!baselineLoot.TryGetValue(pawn.thingIDNumber, out float baseline))
                {
                    baseline = carrying;
                    baselineLoot[pawn.thingIDNumber] = baseline;
                }

                total += UnityEngine.Mathf.Max(0f, carrying - baseline);

                if (total >= lootGoal) break;
            }

            if (total >= lootGoal)
            {
                goalMet = true;
                FDLog.Debug("Motivated raid goal met (" + total.ToString("F0") + "/" + lootGoal.ToString("F0") + ").");
            }

            return goalMet;
        }

        /// <summary>
        /// Everything of the raid's chosen category this pawn is holding, in hands and in pack.
        ///
        /// CountsTowardGoal rather than MatchesCategory: it also rules out corpses and spoiled
        /// food, so nothing the steal search would have refused to target can satisfy the raid.
        /// </summary>
        private static float LootCarriedBy(Pawn pawn, FDLootCategory category)
        {
            float total = 0f;

            Thing carried = pawn.carryTracker?.CarriedThing;
            if (FDStealUtility.CountsTowardGoal(carried, category))
                total += FDStealUtility.LootValue(carried, category);

            // Raiders stash what they've grabbed in their inventory when they pick up more.
            ThingOwner<Thing> inv = pawn.inventory?.innerContainer;
            if (inv != null)
            {
                for (int i = 0; i < inv.Count; i++)
                {
                    Thing t = inv[i];
                    if (FDStealUtility.CountsTowardGoal(t, category))
                        total += FDStealUtility.LootValue(t, category);
                }
            }

            return total;
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_References.Look(ref faction, "faction");
            Scribe_Defs.Look(ref motivation, "motivation");
            Scribe_Values.Look(ref lootGoal, "lootGoal", 0f);
            Scribe_Values.Look(ref fleeAtLossFraction, "fleeAtLossFraction", 0.4f);
            Scribe_Values.Look(ref timeoutTicks, "timeoutTicks", 25000);
            Scribe_Values.Look(ref goalMet, "goalMet", false);
            Scribe_Collections.Look(ref baselineLoot, "baselineLoot", LookMode.Value, LookMode.Value,
                ref tmpBaselineKeys, ref tmpBaselineValues);

            Scribe_Values.Look(ref graphShapeSaved, "graphShapeSaved", false);
            Scribe_Values.Look(ref graphHasLoot, "graphHasLoot", false);
            Scribe_Values.Look(ref graphDamageSatisfaction, "graphDamageSatisfaction", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                baselineLoot ??= new Dictionary<int, float>();

                // Saves written before the shape was scribed. Recreate exactly what the old code
                // would have built from the def, so a raid already in flight when the player updates
                // the mod restores into the same graph it was saved against. Without this the new
                // fields default to false, the loot transition vanishes, and every index moves.
                if (!graphShapeSaved)
                {
                    graphHasLoot = ShapeHasLoot(motivation);
                    graphDamageSatisfaction = motivation != null && motivation.useColonyDamageSatisfaction;
                    graphShapeSaved = true;
                }
            }
        }
    }

    /// <summary>
    /// Fires once the raid has what it came for. Throttled hard: Lord.LordTick calls every trigger
    /// on every transition out of the current toil every single tick, so anything expensive here
    /// would be running 60+ times a second per raid.
    /// </summary>
    public class Trigger_FDGoalMet : Trigger
    {
        public override bool ActivateOn(Lord lord, TriggerSignal signal)
        {
            if (signal.type != TriggerSignalType.Tick) return false;
            if (Find.TickManager.TicksGame % 120 != 0) return false;

            return lord.LordJob is LordJob_FDMotivatedRaid job && job.CheckGoalMet(lord);
        }
    }
}
