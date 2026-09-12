using System;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Deterministic RNG scopes.
    ///
    /// Every stochastic decision this mod makes during simulation (settlement lifecycle rolls,
    /// raid origin choice, motivation choice, ...) must produce the same result on every client
    /// in a RimWorld Multiplayer session. That means: never <see cref="UnityEngine.Random"/>,
    /// never wall-clock time, and never an unseeded <see cref="Rand"/> call from a code path that
    /// isn't already inside the game's own seeded, replayed tick.
    ///
    /// Usage:
    ///     using (FDRand.Push(faction.loadID, dayOfGame, FDRandSalt.SettlementLifecycle))
    ///     {
    ///         if (Rand.Chance(0.1f)) { ... }
    ///     }
    ///
    /// The seed is built only from values that are identical on every client (load IDs, tile IDs,
    /// tick counts) plus a per-system salt so two systems rolling on the same day don't correlate.
    /// </summary>
    public static class FDRand
    {
        public struct Scope : IDisposable
        {
            private bool active;

            internal Scope(bool active)
            {
                this.active = active;
            }

            public void Dispose()
            {
                if (active)
                {
                    active = false;
                    Rand.PopState();
                }
            }
        }

        public static Scope Push(int a, int b)
        {
            Rand.PushState(Gen.HashCombineInt(a, b));
            return new Scope(true);
        }

        public static Scope Push(int a, int b, int c)
        {
            Rand.PushState(Gen.HashCombineInt(Gen.HashCombineInt(a, b), c));
            return new Scope(true);
        }

        public static Scope Push(int a, int b, int c, int d)
        {
            Rand.PushState(Gen.HashCombineInt(a, b, c, d));
            return new Scope(true);
        }

        /// <summary>Deterministic 0..1 value without entering a scope. Useful for weighting.</summary>
        public static float Value(int a, int b, int c)
        {
            return Rand.ValueSeeded(Gen.HashCombineInt(Gen.HashCombineInt(a, b), c));
        }
    }

    /// <summary>
    /// Per-system salts, so two subsystems seeding from the same (faction, day) pair don't roll
    /// identical sequences. Never renumber these - a save made with one numbering would roll
    /// differently after an update.
    /// </summary>
    public static class FDRandSalt
    {
        public const int SettlementLifecycle = 1001;
        public const int SettlementFound = 1002;
        public const int SettlementCollapse = 1003;
        public const int SettlementExpand = 1004;
        public const int RaidOrigin = 2001;
        public const int RaidFaction = 2002;
        public const int RaidArrival = 2003;
        public const int RaidMotivation = 3001;
        public const int RaidRegroup = 3002;
        public const int QuestBounty = 4001;
        public const int StorytellerSpeak = 5001;
        public const int Tribute = 5002;
    }
}
