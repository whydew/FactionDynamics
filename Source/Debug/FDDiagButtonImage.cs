using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FactionDynamics
{
    /// <summary>
    /// TEMPORARY - this does not belong to Faction Dynamics and should be deleted once the real
    /// owner is fixed. It is here only because this is the mod we can rebuild in seconds.
    ///
    /// The problem it addresses: something passes a null (or Unity-destroyed) Texture2D to
    /// Widgets.ButtonImage every frame while a map is open. RimWorld's own draw code hands it
    /// straight to GUI.DrawTexture, which warns once per call - about ten thousand warnings in
    /// eleven seconds, enough to trip "Reached max messages limit" and kill logging for the rest
    /// of the session - and then, intermittently, dies inside UnityPlayer with an access violation.
    /// Two full crashes to desktop have been reproduced from that stack.
    ///
    /// Faction Dynamics is not the source: it loads no textures at all, and it does not appear in a
    /// single frame of any captured stack. But the crash makes the game unusable for testing, so
    /// this does two things:
    ///
    ///   1. Substitutes BaseContent.BadTex for the null, which turns a native crash into a visible
    ///      magenta square. RimWorld can survive drawing a bad texture; it cannot survive drawing a
    ///      null one.
    ///   2. Logs the first offending call stack, once, naming the actual caller - which is the
    ///      thing nobody has been able to see, because the warning Unity emits carries no stack and
    ///      the log is dead by the time anyone looks.
    ///
    /// The null test is deliberately `!tex` and not `tex == null`. Texture2D is a UnityEngine.Object
    /// with an overloaded equality operator: a destroyed texture is a "fake null" that compares
    /// non-null by reference, which is exactly the case a `?? fallback` fails to catch.
    /// </summary>
    [HarmonyPatch(typeof(Widgets), nameof(Widgets.ButtonImage),
        new[] { typeof(Rect), typeof(Texture2D), typeof(Color), typeof(Color), typeof(bool), typeof(string) })]
    public static class FDDiag_ButtonImageGuard
    {
        private static bool reported;
        private static int substitutions;

        [HarmonyPrefix]
        public static void Prefix(ref Texture2D tex)
        {
            if (tex) return;

            substitutions++;

            if (!reported)
            {
                reported = true;
                FDLog.Warning("DIAGNOSTIC (not a Faction Dynamics fault): a null or destroyed "
                              + "Texture2D reached Widgets.ButtonImage. Substituting BadTex so the "
                              + "game cannot crash on it. First offending stack:\n"
                              + new StackTrace(true));
            }

            tex = BaseContent.BadTex;
        }

        /// <summary>How many null textures have been intercepted this session.</summary>
        public static int Substitutions => substitutions;
    }
}
