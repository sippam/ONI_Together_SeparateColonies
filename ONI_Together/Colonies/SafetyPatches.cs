using System;
using HarmonyLib;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Safety nets for game cleanup code that assumes OnSpawn ran. In multiplayer, sync code can destroy an object
    /// that never finished spawning; the vanilla OnCleanUp then throws and ONI shows its crash screen / quits.
    /// Finalizers only swallow the exception while in a multiplayer session (single player behaves as vanilla).
    /// </summary>
    public static class SafetyPatches
    {
        [HarmonyPatch(typeof(ManualGenerator), "OnCleanUp")]
        public static class ManualGenerator_OnCleanUp_Safe
        {
            public static Exception Finalizer(Exception __exception, ManualGenerator __instance)
            {
                if (__exception == null) return null;
                if (!MultiplayerSession.InSession) return __exception;
                Debug.LogWarning($"[ONIT-Colonies] ignored ManualGenerator cleanup error ({__exception.GetType().Name}) on {(__instance != null ? __instance.name : "?")}");
                return null;
            }
        }
    }
}
