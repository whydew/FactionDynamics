using RimWorld;
using RimWorld.Planet;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Carries the decisions our raid patches made (which settlement sent this raid, why) down into
    /// the raid strategy worker that builds the raiders' LordJob, later in the same synchronous call.
    ///
    /// Ordering matters and cost us a bug once: vanilla resolves the raiding faction *inside*
    /// IncidentWorker_RaidEnemy.TryExecuteWorker, so anything computed in a prefix on that method
    /// sees a stale (or null) faction. Everything here is therefore computed lazily through
    /// <see cref="EnsureFor"/>, which does its work the first time it is called with a faction
    /// actually set, and is called from every hook that could plausibly run first. That makes the
    /// context independent of the order vanilla happens to resolve things in.
    ///
    /// Multiplayer note: this is set and cleared inside one incident execution on a single call
    /// stack, and every client executes that same incident on the same tick with the same inputs,
    /// so the context is identical everywhere. Anything that finds it inactive falls back to
    /// vanilla behaviour.
    /// </summary>
    public static class FDRaidContext
    {
        public static FDRaidMotivationDef Motivation;
        public static Settlement Origin;
        public static float OriginDistance = FDWorldUtil.UnreachableDistance;
        public static bool Active;

        private static IncidentParms forParms;
        private static Faction forFaction;
        private static bool pointsScaled;

        /// <summary>The faction this context was computed for, or null when inactive.</summary>
        public static Faction ForFaction => forFaction;

        /// <summary>
        /// Is this context actually about the raid described by these parms?
        ///
        /// Anything reading <see cref="Motivation"/> from a hook that is not exclusively an enemy
        /// raid MUST ask this first. PostProcessSpawnedPawns is declared on IncidentWorker_Raid,
        /// not on IncidentWorker_RaidEnemy, and IncidentWorker_RaidFriendly declares neither it nor
        /// TryExecuteWorker - it runs the identical base method body. A postfix there therefore
        /// fires for ALLY arrivals too, and without this check an allied relief force walked in
        /// carrying the malnutrition of the previous enemy raid, at a severity scaled by the
        /// hardship of an enemy settlement on the other side of the planet.
        /// </summary>
        /// <summary>
        /// Does the live context belong to this exact parms object? Ownership only - unlike
        /// <see cref="IsFor"/> it does not care which faction ended up on it, so it stays true
        /// across the window where vanilla has not resolved the faction yet.
        /// </summary>
        public static bool IsForParms(IncidentParms parms)
        {
            return Active && parms != null && ReferenceEquals(forParms, parms);
        }

        public static bool IsFor(IncidentParms parms)
        {
            return Active
                   && parms != null
                   && ReferenceEquals(forParms, parms)
                   && parms.faction != null
                   && forFaction == parms.faction;
        }

        public static void Clear()
        {
            Motivation = null;
            Origin = null;
            OriginDistance = FDWorldUtil.UnreachableDistance;
            Active = false;
            forParms = null;
            forFaction = null;
            pointsScaled = false;
        }

        /// <summary>
        /// Works out where this raid came from and why, once the raiding faction is final.
        /// No-ops until a faction is set, and no-ops again once it has run for this raid.
        /// </summary>
        public static void EnsureFor(IncidentParms parms)
        {
            if (parms?.faction == null) return;

            // Already computed for this exact raid and faction.
            if (Active && ReferenceEquals(forParms, parms) && forFaction == parms.faction) return;

            // A context is already live for a DIFFERENT raid. That means one raid is executing
            // inside another - a mod firing an incident from a raid hook, a quest spawning a raid
            // mid-resolve. Taking over here would overwrite the outer raid's motivation and origin
            // while its own LordJob is still being built from them, and the outer raid is the one
            // the player is about to meet. The nested raid simply runs as vanilla instead.
            if (Active && forParms != null && !ReferenceEquals(forParms, parms))
            {
                FDLog.Debug("Nested raid detected for " + parms.faction.Name
                            + "; leaving the in-flight context for " + (forFaction?.Name ?? "?")
                            + " alone and letting this one run as vanilla.");
                return;
            }

            Clear();
            forParms = parms;
            forFaction = parms.faction;
            Active = true;

            FDSimConfig cfg = FactionDynamicsWorldComp.Config;

            if (FactionDynamicsWorldComp.Current != null && cfg.raidGeography
                && parms.target != null && parms.target.Tile.Valid)
            {
                Origin = FDRaidGeography.SelectOrigin(parms.faction, parms.target.Tile, out float dist);
                OriginDistance = dist;
            }

            Motivation = FDMotivationChooser.Choose(parms.faction, Origin);

            if (Motivation != null)
            {
                parms.canKidnap = Motivation.canKidnap;
                parms.canSteal = Motivation.lootCategory != FDLootCategory.None;
                parms.canTimeoutOrFlee = true;
            }

            FDLog.Debug("Raid resolved: faction " + parms.faction.Name
                        + ", origin " + (Origin != null ? Origin.Label : "none")
                        + (OriginDistance < FDWorldUtil.UnreachableDistance
                            ? " at " + OriginDistance.ToString("F0") + " tiles"
                            : " (distance unknown)")
                        + ", motivation " + (Motivation != null ? Motivation.defName : "none"));
        }

        /// <summary>
        /// Applies the distance and motivation multipliers to the raid's points. Idempotent - only
        /// the first call for a given raid does anything, however many hooks call it.
        /// </summary>
        public static void ApplyPointsScaling(IncidentParms parms)
        {
            if (!Active || parms == null || !ReferenceEquals(forParms, parms)) return;
            if (pointsScaled) return;
            pointsScaled = true;

            float before = parms.points;

            if (FactionDynamicsWorldComp.Config.raidGeography
                && OriginDistance < FDWorldUtil.UnreachableDistance)
            {
                parms.points *= FDRaidGeography.PointsFactor(OriginDistance);
            }

            // A settlement that is still regrouping sends a smaller party - this is the other half
            // of the regroup mechanic, and the half you actually feel in a fight.
            if (Origin != null && FactionDynamicsWorldComp.Config.settlements)
                parms.points *= FDRaidGeography.OriginStrength(Origin);

            if (Motivation != null)
                parms.points *= Motivation.pointsFactor;

            parms.points = UnityEngine.Mathf.Max(1f, parms.points);

            if (!UnityEngine.Mathf.Approximately(before, parms.points))
            {
                FDLog.Debug("Raid points " + before.ToString("F0") + " -> " + parms.points.ToString("F0")
                            + " (distance " + OriginDistance.ToString("F0")
                            + ", motivation " + (Motivation != null ? Motivation.defName : "none") + ")");
            }
        }

        /// <summary>Drops the motivation but keeps the origin - used when our strategy can't run this raid.</summary>
        public static void DropMotivation()
        {
            Motivation = null;
        }
    }
}
