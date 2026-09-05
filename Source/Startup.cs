using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace FactionDynamics
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        public static Harmony HarmonyInstance;

        static Startup()
        {
            HarmonyInstance = new Harmony("matt.factiondynamics");

            MultiplayerCompat.EnsureInitialized();
            FDActive.CaptureFrom(FactionDynamicsMod.Settings);

            // Note on modularity: patches are applied unconditionally and every patch body checks
            // the saved simulation config (FactionDynamicsWorldComp.Config) before doing anything.
            //
            // That is deliberate and differs from "only patch enabled modules": in a Multiplayer
            // session two clients must have identical patched code, and module toggles are local
            // settings. Gating at runtime on the *saved* config keeps every client's code identical
            // and lets the save decide what runs - which is both MP-safe and means module toggles
            // don't need a restart to take effect in singleplayer.
            try
            {
                HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
                FDLog.Message("Patches applied.");
            }
            catch (Exception e)
            {
                FDLog.Error("Failed to apply Harmony patches: " + e);
            }
        }
    }
}
