using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Components.StructureStateSyncers;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Background reconciliation of storage contents (lockers, fridges, ration boxes, feeders... anything with a
    /// filter list).
    ///
    /// Why: ONI Together's StorageStateSyncer only pushes a storage when its mass changes, only to players whose view
    /// covers it, over an unreliable channel, and the client-side "ask again when stale" never runs (SessionHasPlayers
    /// is false on clients). Items stored by packets also fail when the item's NetId isn't known on the client (very
    /// common with instant auto-store: the item is stored the same frame it spawned). Result: the host sees a full
    /// locker / ration box, a client sees it empty.
    ///
    /// How: every few seconds the host sends (storage NetId, content hash). A client compares with its own copy;
    /// storages still different after a short grace period are requested, and the host answers with the normal
    /// StructureStatePacket (full content list), which rebuilds the storage on that client.
    /// </summary>
    public static class StorageSync
    {
        private const float DigestInterval = 4f;
        private const float ClientGrace = 2f;
        private const int MaxPerRequest = 48;

        public static bool Enabled => Configuration.Instance?.Colonies?.BackgroundStructureSync ?? true;
        public static int Fixed;

        /// <summary>Player-facing storages only (filter list) — machine buffers change every tick and are left alone.</summary>
        public static bool Tracked(StorageStateSyncer s, out Storage storage, out int netId)
        {
            storage = null; netId = 0;
            if (s == null || s.gameObject == null) return false;
            storage = s.SyncedStorage;
            if (storage == null) return false;
            if (s.GetComponent<TreeFilterable>() == null || s.GetComponent<BuildingComplete>() == null) return false;
            if (s.GetComponent<MinionBrain>() != null) return false;
            var id = s.GetComponent<NetworkIdentity>();
            if (id == null || id.NetId == 0) return false;
            netId = id.NetId;
            return true;
        }

        private static readonly Dictionary<int, float> _massByPrefab = new Dictionary<int, float>();

        /// <summary>
        /// Total mass per prefab (0.1 kg). Per prefab rather than per item: the client rebuild merges element chunks
        /// into one stack while the host may keep several, and that must not count as a difference.
        /// </summary>
        public static int ContentHash(Storage storage)
        {
            _massByPrefab.Clear();
            var items = storage.items;
            for (int i = 0; i < items.Count; i++)
            {
                var go = items[i];
                if (go == null) continue;
                var kpid = go.GetComponent<KPrefabID>();
                var pe = go.GetComponent<PrimaryElement>();
                if (kpid == null || pe == null || pe.Mass <= 0f) continue;
                int tag = kpid.PrefabTag.GetHash();
                _massByPrefab.TryGetValue(tag, out float m);
                _massByPrefab[tag] = m + pe.Mass;
            }
            unchecked
            {
                int sum = 0;
                foreach (var kv in _massByPrefab)
                {
                    int h = (int)2166136261;
                    h = (h ^ kv.Key) * 16777619;
                    h = (h ^ Mathf.RoundToInt(kv.Value * 10f)) * 16777619;
                    sum += h;
                }
                return sum ^ (_massByPrefab.Count * 397);
            }
        }

        #region Host

        private static float _hostTimer;

        public static void HostTick()
        {
            if (!Enabled || !MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
            if (!ColonyNet.HostHasClients || GameServerHardSync.IsHardSyncInProgress) return;
            _hostTimer += Time.unscaledDeltaTime;
            if (_hostTimer < DigestInterval) return;
            _hostTimer = 0f;
            try
            {
                var pkt = new StorageDigestPacket();
                foreach (var s in StorageStateSyncer.All)
                {
                    if (!Tracked(s, out var storage, out int netId)) continue;
                    pkt.NetIds.Add(netId);
                    pkt.Hashes.Add(ContentHash(storage));
                    if (pkt.NetIds.Count >= 2000)
                    {
                        PacketSender.SendToAllClients(pkt);
                        pkt = new StorageDigestPacket();
                    }
                }
                if (pkt.NetIds.Count > 0) PacketSender.SendToAllClients(pkt);
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] storage digest: {ex.Message}"); }
        }

        public static void HostSend(ulong player, List<int> netIds)
        {
            if (!MultiplayerSession.IsHost || player == 0 || netIds == null) return;
            foreach (int id in netIds)
            {
                try
                {
                    if (!NetworkIdentityRegistry.TryGet(id, out var identity) || identity == null) continue;
                    var syncer = identity.GetComponent<StorageStateSyncer>();
                    if (syncer != null) syncer.SendStateToClient(player);
                }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] storage send {id}: {ex.Message}"); }
            }
        }

        #endregion

        #region Client

        private struct Pending { public int HostHash; public float Since; }
        private static readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private static float _clientTimer;

        public static void Reset() { _pending.Clear(); }

        private static Storage LocalStorage(int netId)
        {
            if (!NetworkIdentityRegistry.TryGet(netId, out var identity) || identity == null) return null;
            var s = identity.GetComponent<StorageStateSyncer>();
            return s != null ? s.SyncedStorage : null;
        }

        public static void OnDigest(StorageDigestPacket p)
        {
            if (!Enabled || MultiplayerSession.IsHost || global::Game.Instance == null || GameClient.IsHardSyncInProgress) return;
            float now = Time.realtimeSinceStartup;
            int n = Math.Min(p.NetIds.Count, p.Hashes.Count);
            for (int i = 0; i < n; i++)
            {
                int id = p.NetIds[i];
                var storage = LocalStorage(id);
                if (storage == null) continue; // building not on this client yet: structure sync handles that
                if (ContentHash(storage) == p.Hashes[i]) { _pending.Remove(id); continue; }
                if (!_pending.TryGetValue(id, out var pend) || pend.HostHash != p.Hashes[i])
                    _pending[id] = new Pending { HostHash = p.Hashes[i], Since = now };
            }
        }

        public static void ClientTick()
        {
            if (!Enabled || MultiplayerSession.IsHost || !MultiplayerSession.InSession || _pending.Count == 0) return;
            if (ColonySession.GameAge < 15f) return;
            _clientTimer += Time.unscaledDeltaTime;
            if (_clientTimer < 1f) return;
            _clientTimer = 0f;
            float now = Time.realtimeSinceStartup;
            var request = new List<int>();
            var drop = new List<int>();
            foreach (var kv in _pending)
            {
                if (now - kv.Value.Since < ClientGrace) continue;
                drop.Add(kv.Key);
                var storage = LocalStorage(kv.Key);
                if (storage != null && ContentHash(storage) != kv.Value.HostHash) request.Add(kv.Key);
                if (request.Count >= MaxPerRequest) break;
            }
            foreach (var id in drop) _pending.Remove(id);
            if (request.Count > 0)
            {
                Fixed += request.Count;
                PacketSender.SendToHost(new StorageContentRequestPacket { NetIds = request });
            }
        }

        #endregion
    }

    /// <summary>Host -> clients: (storage NetId, content hash) for every player-facing storage.</summary>
    public class StorageDigestPacket : IPacket
    {
        public List<int> NetIds = new List<int>();
        public List<int> Hashes = new List<int>();

        public void Serialize(BinaryWriter w)
        {
            w.Write(NetIds.Count);
            for (int i = 0; i < NetIds.Count; i++) { w.Write(NetIds[i]); w.Write(Hashes[i]); }
        }

        public void Deserialize(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000) n = 0;
            NetIds = new List<int>(n); Hashes = new List<int>(n);
            for (int i = 0; i < n; i++) { NetIds.Add(r.ReadInt32()); Hashes.Add(r.ReadInt32()); }
        }

        public void OnDispatched() => StorageSync.OnDigest(this);
    }

    /// <summary>Client -> host: send me the contents of these storages.</summary>
    public class StorageContentRequestPacket : IPacket
    {
        public List<int> NetIds = new List<int>();

        public void Serialize(BinaryWriter w) { w.Write(NetIds.Count); foreach (var id in NetIds) w.Write(id); }

        public void Deserialize(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 4096) n = 0;
            NetIds = new List<int>(n);
            for (int i = 0; i < n; i++) NetIds.Add(r.ReadInt32());
        }

        public void OnDispatched()
        {
            if (!MultiplayerSession.IsHost) return;
            var ids = NetIds.Count > 64 ? NetIds.GetRange(0, 64) : NetIds;
            StorageSync.HostSend(ColonyNet.CurrentSender, ids);
        }
    }
}
