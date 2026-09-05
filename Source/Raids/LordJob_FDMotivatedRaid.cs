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
        }

        /// <summary>We own retreat behaviour explicitly; don't let the base class add its own flee toil.</summary>
        public override bool AddFleeToil => false;

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();

            var assault = new LordToil_AssaultColony(false, false);
            graph.AddToil(assault);
            graph.StartingToil = assault;

            var exit = new LordToil_ExitMap(LocomotionUrgency.Jog, false, true);
            exit.useAvoidGrid = true;
            graph.AddToil(exit);

            LordToil loot = null;
            if (motivation != null && motivation.lootCategory != FDLootCategory.None && motivation.lootDuty != null)
            {
                loot = new LordToil_FDLoot(motivation.lootDuty);
                graph.AddToil(loot);

                // Fight first, then go for what they came for.
                var toLoot = new Transition(assault, loot, false, true);
                toLoot.AddTrigger(new Trigger_TicksPassed(UnityEngine.Mathf.Max(0, motivation.lootPhaseStartTicks)));
                toLoot.AddPreAction(new TransitionAction_Message(
                    "FD_MessageRaidersLooting".Translate(FactionLabel())));
                graph.AddTransition(toLoot, false);

                // Got what they came for - leave.
                var toExitGoal = new Transition(loot, exit, false, true);
                toExitGoal.AddSource(assault);
                toExitGoal.AddTrigger(new Trigger_FDGoalMet());
                toExitGoal.AddPreAction(new TransitionAction_Message(
                    "FD_MessageRaidersGotWhatTheyCameFor".Translate(FactionLabel())));
                graph.AddTransition(toExitGoal, false);
            }

            // Bled too much - break off.
            var toExitLosses = new Transition(assault, exit, false, true);
            if (loot != null) toExitLosses.AddSource(loot);
            toExitLosses.AddTrigger(new Trigger_FractionPawnsLost(
                UnityEngine.Mathf.Clamp(fleeAtLossFraction, 0.05f, 0.95f)));
            toExitLosses.AddPreAction(new TransitionAction_Message(
                "FD_MessageRaidersBrokeOff".Translate(FactionLabel())));
            graph.AddTransition(toExitLosses, false);

            // Hard timeout, same idea as vanilla's give-up timer.
            var toExitTimeout = new Transition(assault, exit, false, true);
            if (loot != null) toExitTimeout.AddSource(loot);
            toExitTimeout.AddTrigger(new Trigger_TicksPassed(timeoutTicks).WithFilter(new TriggerFilter_MapExitable()));
            toExitTimeout.AddPreAction(new TransitionAction_Message(
                "FD_MessageRaidersGivingUp".Translate(FactionLabel())));
            graph.AddTransition(toExitTimeout, false);

            // Revenge raids also accept "we've hurt them enough" as a win condition.
            if (motivation != null && motivation.useColonyDamageSatisfaction)
            {
                var toExitSatisfied = new Transition(assault, exit, false, true);
                if (loot != null) toExitSatisfied.AddSource(loot);
                toExitSatisfied.AddTrigger(new Trigger_FractionColonyDamageTaken(0.4f, 900f)
                    .WithFilter(new TriggerFilter_MapExitable()));
                toExitSatisfied.AddPreAction(new TransitionAction_Message(
                    "FD_MessageRaidersSatisfied".Translate(FactionLabel())));
                graph.AddTransition(toExitSatisfied, false);
            }

            // Peace broke out mid-raid.
            if (faction != null)
            {
                var toExitPeace = new Transition(assault, exit, false, true);
                if (loot != null) toExitPeace.AddSource(loot);
                toExitPeace.AddTrigger(new Trigger_BecameNonHostileToPlayer());
                graph.AddTransition(toExitPeace, false);
            }

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

                Thing carried = pawn.carryTracker?.CarriedThing;
                if (carried != null)
                    total += FDStealUtility.LootValue(carried, motivation.lootCategory);

                // Raiders stash what they've grabbed in their inventory when they pick up more.
                if (pawn.inventory?.innerContainer != null)
                {
                    ThingOwner<Thing> inv = pawn.inventory.innerContainer;
                    for (int j = 0; j < inv.Count; j++)
                    {
                        Thing t = inv[j];
                        if (FDStealUtility.MatchesCategory(t, motivation.lootCategory))
                            total += FDStealUtility.LootValue(t, motivation.lootCategory);
                    }
                }

                if (total >= lootGoal) break;
            }

            if (total >= lootGoal)
            {
                goalMet = true;
                FDLog.Debug("Motivated raid goal met (" + total.ToString("F0") + "/" + lootGoal.ToString("F0") + ").");
            }

            return goalMet;
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
