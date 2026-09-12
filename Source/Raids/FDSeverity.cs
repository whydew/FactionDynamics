using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Turns a settlement's hardship into a hediff severity, in one place.
    ///
    /// Shared deliberately. This decides how starved a raider looks when they arrive at the
    /// colony AND how starved a defender looks when the player attacks their settlement, and those
    /// two must never disagree - a faction that sends visibly malnourished raiders should be
    /// visibly malnourished at home. Keeping one function means changing FD_Starvation's severity
    /// envelope, hardship weight or jitter moves both at once, and neither can quietly rot.
    /// </summary>
    public static class FDSeverity
    {
        /// <summary>
        /// Where in the motivation's severity envelope this hardship lands: the settlement's
        /// hardship picks the centre, and each pawn is jittered around it so a group is not all
        /// stamped identically.
        /// </summary>
        public static float ForHardship(FDRaidMotivationDef motivation, float hardship)
        {
            FloatRange range = motivation.pawnHediffSeverity;

            // weight 1 -> hardship chooses the point in the range; weight 0 -> centre of the range.
            float t = UnityEngine.Mathf.Lerp(0.5f,
                UnityEngine.Mathf.Clamp01(hardship),
                UnityEngine.Mathf.Clamp01(motivation.pawnHediffHardshipWeight));

            float centre = UnityEngine.Mathf.Lerp(range.min, range.max, t);
            float jitter = UnityEngine.Mathf.Max(0f, motivation.pawnHediffJitter);

            return UnityEngine.Mathf.Clamp(centre + Rand.Range(-jitter, jitter), range.min, range.max);
        }
    }
}
