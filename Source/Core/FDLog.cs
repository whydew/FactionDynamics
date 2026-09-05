using Verse;

namespace FactionDynamics
{
    /// <summary>Prefixed logging so anything this mod says is greppable in the player's log.</summary>
    public static class FDLog
    {
        private const string Prefix = "[Faction Dynamics] ";

        public static void Message(string msg) => Log.Message(Prefix + msg);

        public static void Warning(string msg) => Log.Warning(Prefix + msg);

        public static void Error(string msg) => Log.Error(Prefix + msg);

        /// <summary>Logs only when the mod's verbose logging setting is on. Use freely in simulation code.</summary>
        public static void Debug(string msg)
        {
            if (FactionDynamicsMod.Settings != null && FactionDynamicsMod.Settings.verboseLogging)
                Log.Message(Prefix + msg);
        }
    }
}
