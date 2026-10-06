using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// The game's own "choose your duplicants" cards for a player founding a colony mid-game.
    /// Re-uses the in-game ImmigrantScreen (same CharacterContainer cards as the new-game MinionSelectScreen:
    /// reroll, interest filter, Standard/Bionic filter, name editing) in "starter" mode: 3 dupes, no care packages,
    /// no select buttons, proceed = Embark. When done the Printing Pod screen is restored untouched.
    /// </summary>
    public static class StarterPicker
    {
        public static bool Active { get; private set; }
        private static Action<List<MinionStartingStats>> _onDone;
        private static Telepad _savedTelepad;

        public static bool Available => ImmigrantScreen.instance != null;

        public static bool Open(Action<List<MinionStartingStats>> onDone)
        {
            var scr = ImmigrantScreen.instance;
            if (scr == null || Active) return false;
            try
            {
                Active = true;
                _onDone = onDone;
                _savedTelepad = scr.telepad;
                scr.telepad = null; // ONI Together's printing-pod patches ignore a screen without a pod
                ClearContainers(scr);
                scr.IsStarterMinion = true;
                scr.InitializeContainers(); // 3 dupe cards, reroll enabled, generated as starters
                if (scr.rejectButton != null) scr.rejectButton.gameObject.SetActive(false);
                if (scr.title != null) scr.title.text = "Choose your starting Duplicants";
                scr.Show(true);
                scr.StartCoroutine(AfterSpawn(scr));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] starter picker failed to open: {ex}");
                Restore(scr);
                return false;
            }
        }

        private static IEnumerator AfterSpawn(ImmigrantScreen scr)
        {
            yield return null;
            yield return null;
            if (!Active || scr == null) yield break;
            foreach (var c in scr.containers)
                if (c is CharacterContainer cc && cc != null) cc.DisableSelectButton();
            try
            {
                scr.EnableProceedButton();
                var label = scr.proceedButton != null ? scr.proceedButton.GetComponentInChildren<LocText>() : null;
                if (label != null) label.text = global::STRINGS.UI.IMMIGRANTSCREEN.EMBARK;
            }
            catch { }
        }

        /// <summary>Embark pressed: hand the 3 picks to the panel, then put the Printing Pod screen back.</summary>
        public static void Finish(ImmigrantScreen scr, bool accepted)
        {
            if (!Active) return;
            var picks = new List<MinionStartingStats>();
            if (accepted && scr != null)
                foreach (var c in scr.containers)
                    if (c is CharacterContainer cc && cc != null && cc.Stats != null) picks.Add(cc.Stats);
            var cb = _onDone;
            Restore(scr);
            if (accepted && picks.Count > 0)
            {
                try { cb?.Invoke(picks); }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] starter picker callback failed: {ex.Message}"); }
            }
        }

        private static void Restore(ImmigrantScreen scr)
        {
            Active = false;
            _onDone = null;
            if (scr == null) return;
            try
            {
                ClearContainers(scr);
                scr.IsStarterMinion = false;
                scr.telepad = _savedTelepad;
                _savedTelepad = null;
                if (scr.rejectButton != null) scr.rejectButton.gameObject.SetActive(true);
                if (scr.title != null) scr.title.text = global::STRINGS.UI.IMMIGRANTSCREEN.IMMIGRANTSCREENTITLE;
                var label = scr.proceedButton != null ? scr.proceedButton.GetComponentInChildren<LocText>() : null;
                if (label != null) label.text = global::STRINGS.UI.IMMIGRANTSCREEN.PROCEEDBUTTON;
                if (scr.isActiveAndEnabled) scr.Show(false);
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] starter picker restore: {ex.Message}"); }
        }

        private static void ClearContainers(ImmigrantScreen scr)
        {
            if (scr.containers != null)
            {
                foreach (var c in scr.containers)
                {
                    var go = c?.GetGameObject();
                    if (go != null) UnityEngine.Object.Destroy(go);
                }
                scr.containers.Clear();
            }
            scr.selectedDeliverables?.Clear();
        }

        // ---- patches ----

        [HarmonyPatch(typeof(ImmigrantScreen), "OnProceed")]
        public static class ImmigrantScreen_OnProceed
        {
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(ImmigrantScreen __instance)
            {
                if (!Active) return true;
                Finish(__instance, accepted: true);
                return false;
            }
        }

        /// <summary>Closing the screen (X / Esc) while picking cancels the pick.</summary>
        [HarmonyPatch(typeof(ImmigrantScreen), "OnShow")]
        public static class ImmigrantScreen_OnShow
        {
            public static void Postfix(ImmigrantScreen __instance, bool show)
            {
                if (!show && Active) Finish(__instance, accepted: false);
            }
        }
    }
}
