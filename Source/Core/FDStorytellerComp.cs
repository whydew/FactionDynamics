using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FactionDynamics
{
    public class FDStorytellerCompProperties : StorytellerCompProperties
    {
        public FDStorytellerCompProperties()
        {
            compClass = typeof(FDStorytellerComp);
        }
    }

    /// <summary>
    /// The mod's own seat at the storyteller's table.
    ///
    /// Faction Dynamics has never decided when anything happens. It waits for the storyteller to
    /// roll a raid and then reinterprets it - which is why motivationChance exists, why the raid
    /// patches re-pick the faction vanilla already chose, and why a process-global static had to
    /// carry the decision down the call stack. A StorytellerComp is where a mod is supposed to say
    /// "here is an event I would like to happen", and Storyteller calls it from StorytellerTick, on
    /// a tick, which is the property this mod's Multiplayer story rests on.
    ///
    /// It emits only events vanilla would never produce on its own. FD deliberately does NOT author
    /// its own raids here: those would land on top of whatever the storyteller was already going to
    /// send, and the pacing machinery in StorytellerComp (population intent, threat cycles) would
    /// have to be respected by hand or deliberately bypassed. That is a tuning project on its own.
    /// Tribute demands, and the splinter and succession news to come, are none of them raids, and
    /// none of them fight the storyteller for the threat budget.
    ///
    /// DETERMINISM. MakeIntervalIncidents runs on a tick, which is the property the mod's whole
    /// Multiplayer story rests on - but it runs once per incident target, so a bare Rand call would
    /// consume shared RNG state a different number of times on a client with a different number of
    /// maps. Every roll below is inside an FDRand scope seeded from values every client agrees on.
    /// </summary>
    public class FDStorytellerComp : StorytellerComp
    {
        /// <summary>
        /// How often FD even considers speaking, in ticks. Coarse on purpose: this is checked on
        /// every storyteller interval for every target, and a cheap modulo keeps the common case to
        /// an integer compare. It also becomes part of the RNG seed, so the same window always rolls
        /// the same way no matter how many times it is asked.
        /// </summary>
        private const int ConsiderEveryTicks = 2500;

        /// <summary>Chance FD says anything at all in a given window, before per-event gating.</summary>
        private const float SpeakChance = 0.15f;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!FactionDynamicsWorldComp.Config.raidMotivations) yield break;

            // Only the player's own maps. The world is also an incident target, and a tribute
            // messenger has to arrive somewhere.
            if (!(target is Map map) || !map.IsPlayerHome) yield break;

            int now = Find.TickManager.TicksGame;
            if (now % ConsiderEveryTicks != 0) yield break;

            bool speak;
            using (FDRand.Push((int)map.Tile, now / ConsiderEveryTicks, FDRandSalt.StorytellerSpeak))
            {
                speak = Rand.Chance(SpeakChance);
            }
            if (!speak) yield break;

            IncidentDef def = FDIncidentDefOf.FD_TributeDemand;
            var parms = StorytellerUtility.DefaultParmsNow(def.category, target);

            // CanFireNow does the real gating - a desperate faction nearby, off cooldown, not
            // already at war. Asking it here rather than letting the incident fail later keeps the
            // "nothing happened" case free of side effects.
            if (!def.Worker.CanFireNow(parms)) yield break;

            yield return new FiringIncident(def, this, parms);
        }
    }

    /// <summary>
    /// Registers <see cref="FDStorytellerComp"/> with every storyteller at startup.
    ///
    /// Same reasoning as the settlement comp injector: an XML patch would have to name each
    /// StorytellerDef, which silently misses storytellers added by other mods. Walking the database
    /// covers whatever is loaded.
    ///
    /// Storyteller builds its comp list from def.comps in InitializeStorytellerComps, which is
    /// called from the constructor AND from ExposeData during ResolvingCrossRefs - so this works for
    /// existing saves as well as new games, provided the injection has happened before a game loads.
    /// A static constructor is that slot.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class FDStorytellerCompInjector
    {
        static FDStorytellerCompInjector()
        {
            int injected = 0;

            foreach (StorytellerDef def in DefDatabase<StorytellerDef>.AllDefs)
            {
                if (def == null) continue;

                def.comps ??= new List<StorytellerCompProperties>();

                bool already = false;
                for (int i = 0; i < def.comps.Count; i++)
                {
                    if (def.comps[i] is FDStorytellerCompProperties) { already = true; break; }
                }
                if (already) continue;

                def.comps.Add(new FDStorytellerCompProperties());
                injected++;
            }

            FDLog.Message("Storyteller comp registered with " + injected + " storyteller(s).");
        }
    }
}
