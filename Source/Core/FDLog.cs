using RimWorld;
using Verse;

namespace FactionDynamics
{
    /// <summary>Prefixed logging so anything this mod says is greppable in the player's log.</summary>
    public static class FDLog
    {
        private const string Prefix = "[Faction Dynamics] ";

        public static void Message(string msg) => Log.Message(Prefix + msg);

        /// <summary>
        /// Confirmation for something the player deliberately triggered from the dev menu. Goes to
        /// the on-screen message area, NOT the log - with "Auto-open is ON" (the dev default) a
        /// Log.Message throws the debug window open over the game every time you use a debug
        /// action, which is what these were doing.
        /// </summary>
        public static void Toast(string msg)
        {
            Messages.Message(Prefix + msg, MessageTypeDefOf.TaskCompletion, false);
        }

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
