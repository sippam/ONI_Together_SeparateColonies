using System;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Lifecycle glue (MonoBehaviour on the persistent Multiplayer_Modules object):
    ///  - after a world load / when a session starts: host enables the mode + colony #1, client asks for the registry
    ///  - replicates ownership deltas (ColonyNet.Tick)
    ///  - runs scheduled hard syncs (after founding a colony)
    /// </summary>
    public class ColonySession : MonoBehaviour
    {
        private static float _hardSyncAt = -1f;
        private static string _hardSyncWhy;

        private global::Game _lastGame;
        private float _gameSeenAt = -1f;
        private bool _gameInitDone;
        private bool _lastInSession;
        private bool _sessionInitDone;
        private float _registryRetry;

        public static int TerritoryRadiusSetting => Mathf.Clamp(Configuration.Instance?.Colonies?.TerritoryRadius ?? 14, 0, 64);

        private void Update()
        {
            try { Tick(); }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] ColonySession: {ex}"); }
        }

        private void Tick()
        {
            var game = global::Game.Instance;
            if (game != _lastGame)
            {
                _lastGame = game;
                _gameInitDone = false;
                _sessionInitDone = false;
                _gameSeenAt = game != null ? Time.realtimeSinceStartup : -1f;
                if (game != null) { Logistics.AutoStore.OnGameStart(); Logistics.StorageIndex.Invalidate(); }
                StructureSync.Reset();
                ItemSync.Reset();
                FoodSync.Reset();
            }
            try { ClientResync.Tick(); } catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] resync tick: {ex.Message}"); }
            bool inSession = MultiplayerSession.InSession;
            if (inSession != _lastInSession)
            {
                _lastInSession = inSession;
                _sessionInitDone = false;
            }

            if (game == null) return;
            // wait a moment so the save (and ColonySaveData) is fully loaded and the world is spawned
            if (!_gameInitDone && Time.realtimeSinceStartup - _gameSeenAt > 2f)
            {
                _gameInitDone = true;
                if (!MultiplayerSession.IsClient)
                {
                    try { ColonyWorldTools.AddExtraWaterIfNew(); }
                    catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] extra water failed: {ex.Message}"); }
                    try { ColonyWorldTools.AddHostWaterIfNeeded(); }
                    catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] host water failed: {ex.Message}"); }
                }
            }
            if (!_gameInitDone) return;

            // built-in logistics (auto-store / instant delivery): single player or host only
            if (Logistics.LogisticsCfg.Simulates)
            {
                try { Logistics.AutoStore.Tick(); Logistics.InstantDelivery.Tick(); }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Logistics] tick: {ex.Message}"); }
            }

            if (inSession && !_sessionInitDone)
            {
                _sessionInitDone = true;
                OnSessionReady();
            }

            if (inSession)
            {
                ColonyNet.Tick();
                ColonyResearch.Tick();
                ColonyImmigration.Tick();
                StructureSync.HostTick();
                StructureSync.ClientTick();
                ItemSync.HostTick();
                ItemSync.ClientTick();
                FoodSync.HostTick();
                ColonyPriority.Flush();
                PlantPlotLink.ClientTick();

                // client: keep asking until the host answered (registry may arrive before/after the world)
                if (MultiplayerSession.IsClient && ColonyRegistry.Enabled == false)
                {
                    _registryRetry += Time.unscaledDeltaTime;
                    if (_registryRetry > 10f) { _registryRetry = 0f; RequestRegistry(); }
                }

                if (_hardSyncAt > 0f && Time.realtimeSinceStartup >= _hardSyncAt && MultiplayerSession.IsHost)
                {
                    _hardSyncAt = -1f;
                    if (ColonyNet.HostHasClients)
                    {
                        Debug.Log($"[ONIT-Colonies] Hard sync ({_hardSyncWhy})");
                        GameServerHardSync.PerformHardSync(false);
                    }
                }
            }
        }

        private void OnSessionReady()
        {
            if (MultiplayerSession.IsHost)
            {
                ColonyRegistry.TerritoryRadius = TerritoryRadiusSetting;
                ColonyRules.Apply(ColonyRules.FromConfig());
                bool want = Configuration.Instance?.Colonies?.SeparateColonies ?? true;
                if (want && !ColonyRegistry.Enabled) ColonyFounder.HostSetMode(true);
                else if (ColonyRegistry.Enabled)
                {
                    ColonyFounder.HostCreateFirstColonyIfNeeded();
                    ColonyNet.BroadcastRegistry();
                }
            }
            else
            {
                RequestRegistry();
            }
            Debug.Log($"[ONIT-Colonies] Session ready. host={MultiplayerSession.IsHost} enabled={ColonyRegistry.Enabled} colonies={ColonyRegistry.Colonies.Count} local=#{ColonyRegistry.LocalColonyId}");
        }

        private static void RequestRegistry()
        {
            try { PacketSender.SendToHost(new ColonyRegistryRequestPacket()); }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] registry request failed: {ex.Message}"); }
        }

        /// <summary>Host: hard sync all clients after a delay (multiple calls coalesce).</summary>
        public static void ScheduleHardSync(float delaySeconds, string why)
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
            float at = Time.realtimeSinceStartup + delaySeconds;
            if (_hardSyncAt < 0f || at > _hardSyncAt) _hardSyncAt = at;
            _hardSyncWhy = why;
            Debug.Log($"[ONIT-Colonies] Hard sync scheduled in {delaySeconds:0}s ({why})");
        }
    }
}
