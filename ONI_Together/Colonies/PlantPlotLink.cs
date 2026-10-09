using System;
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Trackers;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Client: plants that arrive by sync (PlantLifecyclePacket / structure sync / hard sync timing) are sometimes
    /// spawned next to their farm tile / planter box instead of inside it. The plot then believes it is empty:
    /// "No seed selected", a red seed preview and a fetch errand for a seed, although the plant is right there.
    /// Every 2 s this links such plants back into their plot (the same ReplacePlant call ONI Together uses).
    /// </summary>
    public static class PlantPlotLink
    {
        private static float _timer;
        public static int Linked;
        // plants whose relink threw: retrying every 2 s only spammed the log (~1000 NREs per session)
        private static readonly Dictionary<int, int> _failures = new Dictionary<int, int>();

        public static void ClientTick()
        {
            if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < 2f) return;
            _timer = 0f;
            try { Relink(); }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] plant relink: {ex.Message}"); }
        }

        private static void Relink()
        {
            // plants by cell, only ones not already sitting in a plot
            Dictionary<int, Growing> loose = null;
            lock (PlantTracker.AllPlants)
            {
                foreach (var g in PlantTracker.AllPlants)
                {
                    if (g == null || g.gameObject == null) continue;
                    var rm = g.GetComponent<ReceptacleMonitor>();
                    if (rm == null) continue; // wild-only plants can't sit in a plot
                    var rec = rm.GetReceptacle();
                    if (rec != null && rec.Occupant == g.gameObject) continue;
                    if (loose == null) loose = new Dictionary<int, Growing>();
                    loose[Grid.PosToCell(g)] = g;
                }
            }
            if (loose == null) return;

            foreach (int worldId in global::Components.PlantablePlots.GetWorldsIds())
            {
                foreach (var plot in global::Components.PlantablePlots.GetItems(worldId))
                {
                    if (plot == null || plot.Occupant != null) continue;
                    // exactly where the plot puts its occupant (same math as PositionOccupyingObject), so a
                    // plant is never stolen by the plot of the next farm row
                    Vector3 rel = plot.occupyingObjectRelativePosition;
                    var rot = plot.GetComponent<Rotatable>();
                    if (rot != null) rel = rot.GetRotatedOffset(rel);
                    int expected = Grid.PosToCell(plot.transform.GetPosition() + rel);
                    if (!Grid.IsValidCell(expected) || !loose.TryGetValue(expected, out var g) || g == null) continue;
                    var go = g.gameObject;
                    loose.Remove(Grid.PosToCell(g));
                    int key = go.GetInstanceID();
                    if (_failures.TryGetValue(key, out int n) && n >= 2) continue;
                    if (!Link(plot, go))
                    {
                        _failures[key] = n + 1;
                        if (_failures.Count > 2048) _failures.Clear();
                    }
                }
            }
        }

        private static bool Link(PlantablePlot plot, GameObject plant)
        {
            ColonyPriority.Suppress++;
            try
            {
                if (plant.GetComponent<StateMachineController>() == null || !StructureSync.IsFullySpawned(plant)) return false;
                plot.ReplacePlant(plant, true);
                var rm = plant.GetComponent<ReceptacleMonitor>();
                if (rm != null) rm.SetReceptacle(plot);
                Linked++;
                Debug.Log($"[ONIT-Colonies] Linked {plant.PrefabID()} into plot at {Grid.PosToCell(plot)}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] link plant {plant.PrefabID()} at {Grid.PosToCell(plot)} failed: {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally { ColonyPriority.Suppress--; }
        }
    }
}
