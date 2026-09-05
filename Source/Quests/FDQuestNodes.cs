using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Gates one of this mod's quests on its module being enabled in the *saved* simulation config,
    /// and applies the configured frequency multiplier.
    ///
    /// Why not just set rootSelectionWeight from mod settings: defs are shared, but mod settings are
    /// per player. In a Multiplayer session two clients with different sliders would generate
    /// different quests from the same storyteller tick. Reading the saved config here keeps quest
    /// selection identical for everyone, and the Rand call sits inside the storyteller's own
    /// deterministic execution.
    ///
    /// Frequency mapping: the quest defs carry double the intended selection weight, and this node
    /// passes with chance (multiplier / 2). So multiplier 1 = intended rate, 0 = off, 2 = double.
    /// </summary>
    public class QuestNode_FDGate : QuestNode
    {
        /// <summary>"raiding" or "bounty".</summary>
        public string module = "raiding";

        /// <summary>
        /// When true this quest is only offered if there is NO real hostile settlement within reach.
        /// Used by the generated-outpost quest so it acts as the fallback for the settlement strike,
        /// rather than the two competing.
        /// </summary>
        public bool requireNoSettlementTarget;

        /// <summary>Search range used by the check above; keep it equal to the strike quest's range.</summary>
        public float settlementTargetRange = 90f;

        protected override bool TestRunInt(Slate slate)
        {
            if (requireNoSettlementTarget
                && FDSettlementTargeting.FindTarget(settlementTargetRange, out _) != null)
            {
                return false;
            }

            return Passes();
        }

        protected override void RunInt()
        {
            // Nothing to do at run time - this node exists only to make the quest un-selectable.
        }

        private bool Passes()
        {
            // Dev action generating this quest directly - skip the module/frequency gate.
            if (FDDebug.BypassQuestGate) return true;

            FDSimConfig cfg = FactionDynamicsWorldComp.Config;

            switch (module)
            {
                case "bounty":
                    if (!cfg.questsBounties) return false;
                    return Rand.Chance(UnityEngine.Mathf.Clamp01(cfg.bountyWeightMult * 0.5f));

                default:
                    if (!cfg.questsRaiding) return false;
                    return Rand.Chance(UnityEngine.Mathf.Clamp01(cfg.questWeightMult * 0.5f));
            }
        }
    }

    /// <summary>
    /// Scales a reward value already on the slate by the configured bounty reward multiplier.
    /// Reads the saved config, not local settings, for the same reason as the gate above.
    /// </summary>
    public class QuestNode_FDScaleBountyReward : QuestNode
    {
        public string slateVar = "rewardValue";

        protected override bool TestRunInt(Slate slate) => true;

        protected override void RunInt()
        {
            Slate slate = QuestGen.slate;
            float value = slate.Get(slateVar, 0f);
            if (value <= 0f) return;

            float mult = UnityEngine.Mathf.Clamp(FactionDynamicsWorldComp.Config.bountyRewardMult, 0.1f, 10f);
            slate.Set(slateVar, value * mult);
        }
    }

    /// <summary>
    /// Adds the quest part that weakens a faction's local settlement when the player clears one of
    /// their camps. This is the tie-in that makes M4's quests matter to M1's world: hitting a
    /// faction's forward camp actually costs them strength on the world map.
    /// </summary>
    public class QuestNode_FDWeakenFaction : QuestNode
    {
        public SlateRef<string> inSignal;
        public SlateRef<Faction> faction;
        public SlateRef<PlanetTile> tile;
        public SlateRef<float> hardship = 0.25f;

        protected override bool TestRunInt(Slate slate) => true;

        protected override void RunInt()
        {
            Quest quest = QuestGen.quest;
            Slate slate = QuestGen.slate;

            var part = new QuestPart_FDWeakenFaction
            {
                inSignal = QuestGenUtility.HardcodedSignalWithQuestID(inSignal.GetValue(slate))
                           ?? QuestGen.slate.Get<string>("inSignal"),
                faction = faction.GetValue(slate),
                tile = tile.GetValue(slate),
                hardshipAmount = hardship.GetValue(slate)
            };

            quest.AddPart(part);
        }
    }

    /// <summary>
    /// When its signal fires, weakens the nearest settlement of a faction to a tile: it goes into
    /// the same "regrouping" state a settlement enters after sending a raid, and the faction takes
    /// on hardship, which makes it more likely to lose ground later and unlocks starvation raids.
    /// </summary>
    public class QuestPart_FDWeakenFaction : QuestPart
    {
        public string inSignal;
        public Faction faction;
        public PlanetTile tile;
        public float hardshipAmount = 0.25f;

        public override void Notify_QuestSignalReceived(Signal signal)
        {
            base.Notify_QuestSignalReceived(signal);

            if (signal.tag != inSignal) return;
            if (faction == null) return;

            FactionDynamicsWorldComp comp = FactionDynamicsWorldComp.Current;
            if (comp == null) return;
            if (!FactionDynamicsWorldComp.Config.settlements) return;

            Settlement nearest = null;
            float bestDist = float.MaxValue;

            System.Collections.Generic.List<Settlement> settlements = FDWorldUtil.SettlementsOf(faction);
            for (int i = 0; i < settlements.Count; i++)
            {
                float d = FDWorldUtil.TileDistance(settlements[i].Tile, tile);
                if (d < bestDist)
                {
                    bestDist = d;
                    nearest = settlements[i];
                }
            }

            FactionRuntimeData fd = comp.GetFactionData(faction);
            // Spread the blow across their settlements, worst near where it happened.
            comp.NotifyFactionSetback(faction, hardshipAmount, tile);
            // They will remember who did this - grudge is faction-wide.
            fd.AddGrudge(hardshipAmount * 0.8f);

            if (nearest != null)
            {
                comp.NotifyRaidSent(nearest, null, Find.TickManager.TicksGame);
                SettlementRuntimeData sd = comp.GetSettlementData(nearest);
                sd.strengthFactor = UnityEngine.Mathf.Max(0.4f, sd.strengthFactor - 0.2f);
                FDLog.Debug("Quest victory weakened " + nearest.Label + " (" + faction.Name + ").");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref inSignal, "inSignal");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref tile, "tile");
            Scribe_Values.Look(ref hardshipAmount, "hardshipAmount", 0.25f);
        }
    }
}
