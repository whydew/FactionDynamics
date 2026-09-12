using System;
using System.Runtime.CompilerServices;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// Optional integration with the RimWorld Multiplayer mod (rwmt.Multiplayer).
    ///
    /// Detection uses only the loaded-mod list - it touches NO Multiplayer.API type. Only after
    /// confirming MP is present do we call <see cref="WireUp"/>, which is the sole method that
    /// references Multiplayer.API. Because the API type is never referenced from any always-JIT'd
    /// path, the mod loads perfectly fine when Multiplayer is not installed.
    ///
    /// Faction Dynamics needs very little from the Multiplayer API in v1.0, by design: all of its
    /// simulation runs inside the game's own world/map ticks, which Multiplayer already replays
    /// identically on every client, and all of its tuning values live in the save
    /// (<see cref="FDSimConfig"/>) rather than in per-player mod settings. The only thing we need
    /// the API for is knowing whether a session is live, so we can skip refreshing the saved config
    /// from local settings and so UI can tell the player what is authoritative.
    /// </summary>
    public static class MultiplayerCompat
    {
        private static bool initialized;
        private static bool active;

        /// <summary>True when the Multiplayer mod is installed (session or not).</summary>
        public static bool Active => active;

        /// <summary>True only while an actual MP session is live. Never touches the API unless MP is loaded.</summary>
        public static bool InMultiplayer
        {
            get
            {
                if (!active) return false;
                try
                {
                    return InMultiplayerInternal();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Called from Startup. Idempotent.</summary>
        public static void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;

            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                string id = mod.PackageId?.ToLowerInvariant() ?? string.Empty;
                if (id.StartsWith("rwmt.multiplayer"))
                {
                    active = true;
                    break;
                }
            }

            if (!active) return;

            try
            {
                WireUp();
                FDLog.Message("Multiplayer detected - simulation config will be treated as host-authoritative.");
            }
            catch (Exception e)
            {
                active = false;
                FDLog.Warning("Failed to wire up Multiplayer compatibility, disabling it: " + e);
            }
        }

        // All Multiplayer.API IL is confined to this method and InMultiplayerInternal below.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WireUp()
        {
            // Touch the API once so a broken/renamed API surfaces here, inside the try/catch,
            // rather than at some later random call site.
            bool _ = Multiplayer.API.MP.enabled;

            // The tribute letter is the one place FD asks the player to press a button, and button
            // presses are the one thing Multiplayer does not replay for us.
            //
            // MP syncs vanilla's choice letters by naming each one explicitly (SyncDelegates.
            // InitChoiceLetters registers ChoiceLetter_RansomDemand and seven siblings); nothing
            // enumerates ChoiceLetter subclasses, and the "close expired dialogs" machinery looks
            // its default choice up by exact GetType(). So a mod letter is invisible to all of it:
            // unregistered, the option body runs only on the client that clicked, silver leaves one
            // colony's stockpile and not the others', and the session desyncs on the next tick.
            //
            // Registering by method NAME rather than by lambda ordinal is deliberate. MP supports
            // RegisterSyncMethodLambdaInGetter(type, nameof(Choices), ordinal), but that ordinal is
            // an index into the compiler's generated names for the getter body - insert an option,
            // reorder two, and it silently points at a different button. Named methods have no
            // ordinal to rot.
            Multiplayer.API.MP.RegisterSyncMethod(
                typeof(ChoiceLetter_FDTribute), nameof(ChoiceLetter_FDTribute.Accept));
            Multiplayer.API.MP.RegisterSyncMethod(
                typeof(ChoiceLetter_FDTribute), nameof(ChoiceLetter_FDTribute.Refuse));

            // And when the letter times out, every client must resolve it the same way rather than
            // each popping its own dialog. MP keys this on the exact letter type.
            Multiplayer.API.MP.RegisterDefaultLetterChoice(
                HarmonyLib.AccessTools.Method(
                    typeof(ChoiceLetter_FDTribute), nameof(ChoiceLetter_FDTribute.Refuse)),
                typeof(ChoiceLetter_FDTribute));

            FDLog.Message("Multiplayer: tribute letter choices registered for syncing.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool InMultiplayerInternal()
        {
            return Multiplayer.API.MP.enabled && Multiplayer.API.MP.IsInMultiplayer;
        }
    }
}
