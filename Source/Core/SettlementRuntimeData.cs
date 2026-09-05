using RimWorld;
using Verse;

namespace FactionDynamics
{
    /// <summary>How a settlement came to exist, so letters and inspect text can say something true.</summary>
    public enum FDSettlementOrigin : byte
    {
        Preexisting = 0,
        Founded = 1,
        Expanded = 2
    }

    /// <summary>
    /// Per-settlement state, keyed by <see cref="RimWorld.Planet.WorldObject.ID"/> in
    /// <see cref="FactionDynamicsWorldComp"/>.
    ///
    /// This lives in the world component rather than in a <c>WorldObjectComp</c> on purpose: one
    /// ordered structure that we control is easier to keep deterministic under Multiplayer than
    /// per-object comps, it needs no XML patch on the Settlement def, and it works for settlement
    /// types added by other mods without us having to know about them.
    /// </summary>
    public class SettlementRuntimeData : IExposable
    {
        public int settlementId = -1;

        /// <summary>Tick this settlement finishes regrouping after sending a raid (-1 = not regrouping).</summary>
        public int regroupUntilTick = -1;

        /// <summary>Multiplier on this settlement's effective strength. 1 = normal.</summary>
        public float strengthFactor = 1f;

        /// <summary>How many raids this settlement has sent at the player.</summary>
        public int raidsSent;

        /// <summary>Tick it was created by our lifecycle system (-1 for settlements we didn't create).</summary>
        public int foundedTick = -1;

        /// <summary>
        /// 0..1. How badly THIS settlement is struggling: its own winter, its own losses, its own
        /// weakness. The faction's hardship is the average of its settlements', so a faction is only
        /// "starving" to the degree its actual towns are - and a raid's motivation is judged by the
        /// settlement that sent it, not by a planet-wide average.
        /// </summary>
        public float hardship;

        /// <summary>
        /// False until this settlement's hardship has been seeded. On saves made before per-settlement
        /// hardship existed, the first evaluation seeds it from the faction value so nothing is lost.
        /// </summary>
        public bool hardshipInitialized;

        public FDSettlementOrigin origin = FDSettlementOrigin.Preexisting;

        public SettlementRuntimeData()
        {
        }

        public SettlementRuntimeData(int settlementId)
        {
            this.settlementId = settlementId;
        }

        public bool IsRegrouping(int nowTick) => regroupUntilTick > 0 && nowTick < regroupUntilTick;

        public int RegroupDaysLeft(int nowTick)
        {
            if (!IsRegrouping(nowTick)) return 0;
            return UnityEngine.Mathf.CeilToInt((regroupUntilTick - nowTick) / (float)GenDate.TicksPerDay);
        }

        /// <summary>Effective strength, accounting for an active regroup penalty.</summary>
        public float EffectiveStrength(int nowTick, float regroupPenalty)
        {
            float s = strengthFactor;
            if (IsRegrouping(nowTick))
                s *= UnityEngine.Mathf.Clamp01(1f - regroupPenalty);
            return UnityEngine.Mathf.Clamp(s, 0.1f, 3f);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref settlementId, "settlementId", -1);
            Scribe_Values.Look(ref regroupUntilTick, "regroupUntilTick", -1);
            Scribe_Values.Look(ref strengthFactor, "strengthFactor", 1f);
            Scribe_Values.Look(ref raidsSent, "raidsSent", 0);
            Scribe_Values.Look(ref foundedTick, "foundedTick", -1);
            Scribe_Values.Look(ref hardship, "hardship", 0f);
            Scribe_Values.Look(ref hardshipInitialized, "hardshipInitialized", false);
            Scribe_Values.Look(ref origin, "origin", FDSettlementOrigin.Preexisting);
        }
    }
}
