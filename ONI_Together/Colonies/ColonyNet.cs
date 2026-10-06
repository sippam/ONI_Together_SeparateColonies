using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Network glue for separate colonies.
    ///  - CurrentSender: which player sent the packet being dispatched right now (set by the transports
    ///    and HostBroadcastPacket). On the host this decides the acting colony of a client's tool packet.
    ///  - Registry/ownership replication host -> clients.
    /// </summary>
    public static class ColonyNet
    {
        /// <summary>Player id of the sender of the packet currently being dispatched (0 = unknown / local).</summary>
        [ThreadStatic] public static ulong CurrentSender;

        public static bool IsHost => MultiplayerSession.IsHost;
        public static bool InSession => MultiplayerSession.InSession;

        /// <summary>Host: colony context for a packet sent by `sender`. 0 = not a player action / mode off.</summary>
        public static int ContextForSender(ulong sender)
        {
            if (!IsHost || !ColonyRegistry.Active || sender == 0) return ColonyRegistry.NoColony;
            if (sender == MultiplayerSession.HostUserID && IsHost) return ColonyRegistry.NoColony; // host's own echo
            int colony = ColonyRegistry.GetColonyOfPlayer(sender);
            return colony == ColonyRegistry.NoColony ? ColonyContext.Spectator : colony;
        }

        #region Registry

        private static bool _registryDirty;
        private static float _registryDirtyTimer;

        /// <summary>Host: re-broadcast the registry soon (coalesced) — e.g. schedule owners changed.</summary>
        public static void MarkRegistryDirty()
        {
            if (!IsHost || !InSession) return;
            _registryDirty = true;
        }

        public static void BroadcastRegistry()
        {
            if (!IsHost || !InSession) return;
            try { PacketSender.SendToAllClients(ColonyRegistryPacket.FromRegistry()); }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] BroadcastRegistry failed: {ex.Message}"); }
        }

        public static void SendRegistryTo(ulong player)
        {
            if (!IsHost || !InSession || player == 0) return;
            try
            {
                PacketSender.SendToPlayer(player, ColonyRegistryPacket.FromRegistry());
                PacketSender.SendToPlayer(player, ColonyOwnersPacket.FullSnapshot());
                if (ColonyResearch.SeparateActive)
                    ColonyResearch.SendProfileNow(ColonyRegistry.GetColonyOfPlayer(player));
                if (ColonyImmigration.SeparateActive)
                    ColonyImmigration.SendStateNow(ColonyRegistry.GetColonyOfPlayer(player));
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] SendRegistryTo failed: {ex.Message}"); }
        }

        public static void SendMessageTo(ulong player, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (player == 0 || player == MultiplayerSession.LocalUserID) { ColonyMessages.Push(text); return; }
            try { PacketSender.SendToPlayer(player, new ColonyMessagePacket { Text = text }); }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] SendMessageTo failed: {ex.Message}"); }
        }

        #endregion

        #region Ownership deltas (host -> clients, keyed by NetworkIdentity.NetId)

        private static readonly Dictionary<int, int> _pendingOwners = new Dictionary<int, int>();
        private static float _flushTimer;
        private static float _snapshotTimer;

        /// <summary>Called by ColonyRegistry.SetOwner on every machine; only the host replicates.</summary>
        public static void NotifyOwnerChanged(GameObject go, int colonyId)
        {
            if (!IsHost || !InSession || go == null || ColonyOwnersPacket.Applying) return;
            var ni = go.GetComponent<NetworkIdentity>();
            if (ni == null || ni.NetId == 0) return;
            _pendingOwners[ni.NetId] = colonyId;
        }

        /// <summary>Host, main thread, every frame.</summary>
        public static void Tick()
        {
            if (!IsHost || !InSession) { _pendingOwners.Clear(); _registryDirty = false; return; }
            if (_registryDirty)
            {
                _registryDirtyTimer += Time.unscaledDeltaTime;
                if (_registryDirtyTimer > 1f) { _registryDirty = false; _registryDirtyTimer = 0f; BroadcastRegistry(); }
            }
            // safety net: objects whose NetId didn't exist yet when they were tagged (spawn order) get fixed here
            _snapshotTimer += Time.unscaledDeltaTime;
            if (_snapshotTimer > 30f && ColonyRegistry.Active)
            {
                _snapshotTimer = 0f;
                try { PacketSender.SendToAllClients(ColonyOwnersPacket.FullSnapshot()); }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] owner snapshot failed: {ex.Message}"); }
            }
            _flushTimer += Time.unscaledDeltaTime;
            if (_flushTimer < 0.5f || _pendingOwners.Count == 0) return;
            _flushTimer = 0f;
            var p = new ColonyOwnersPacket();
            foreach (var kv in _pendingOwners) { p.NetIds.Add(kv.Key); p.Colonies.Add(kv.Value); }
            _pendingOwners.Clear();
            try { PacketSender.SendToAllClients(p); }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] owner flush failed: {ex.Message}"); }
        }

        #endregion
    }

    /// <summary>Small feedback queue for the colony panel (refusals from the host etc.).</summary>
    public static class ColonyMessages
    {
        public static readonly List<string> Recent = new List<string>();
        public static event Action<string> OnMessage;

        public static void Push(string text)
        {
            Recent.Add(text);
            if (Recent.Count > 6) Recent.RemoveAt(0);
            Debug.Log($"[ONIT-Colonies] {text}");
            try { OnMessage?.Invoke(text); } catch { }
        }
    }

    // =====================================================================
    // Packets (auto-registered by PacketRegistry: every IPacket in this assembly)
    // =====================================================================

    /// <summary>Host -> clients: mode flag, territory radius and the colony list.</summary>
    public class ColonyRegistryPacket : IPacket
    {
        public bool Enabled;
        public int TerritoryRadius;
        public string ColoniesJson = "[]";
        public string RulesJson = "";
        public string ScheduleOwnersJson = "";

        public static ColonyRegistryPacket FromRegistry() => new ColonyRegistryPacket
        {
            Enabled = ColonyRegistry.Enabled,
            TerritoryRadius = ColonyRegistry.TerritoryRadius,
            ColoniesJson = JsonConvert.SerializeObject(ColonyRegistry.Colonies),
            RulesJson = JsonConvert.SerializeObject(ColonyRules.Data),
            ScheduleOwnersJson = JsonConvert.SerializeObject(ColonySchedules.Snapshot())
        };

        public void Serialize(BinaryWriter w) { w.Write(Enabled); w.Write(TerritoryRadius); w.Write(ColoniesJson ?? "[]"); w.Write(RulesJson ?? ""); w.Write(ScheduleOwnersJson ?? ""); }
        public void Deserialize(BinaryReader r) { Enabled = r.ReadBoolean(); TerritoryRadius = r.ReadInt32(); ColoniesJson = r.ReadString(); RulesJson = r.ReadString(); ScheduleOwnersJson = r.ReadString(); }

        public void OnDispatched()
        {
            if (MultiplayerSession.IsHost) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<ColonyInfo>>(ColoniesJson) ?? new List<ColonyInfo>();
                if (!string.IsNullOrEmpty(RulesJson))
                {
                    var rules = JsonConvert.DeserializeObject<ColonyRulesData>(RulesJson);
                    if (rules != null) ColonyRules.Data = rules; // client: research is driven by ColonyResearchPacket
                }
                ColonyRegistry.ApplyRemote(Enabled, TerritoryRadius, list);
                if (!string.IsNullOrEmpty(ScheduleOwnersJson))
                    ColonySchedules.ApplyRemote(JsonConvert.DeserializeObject<List<int>>(ScheduleOwnersJson));
                Debug.Log($"[ONIT-Colonies] Registry from host: enabled={Enabled} colonies={list.Count} local=#{ColonyRegistry.LocalColonyId} rules: {ColonyRules.Describe()}");
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] Bad registry packet: {ex.Message}"); }
        }
    }

    /// <summary>Client -> host: please send me the registry + ownership snapshot.</summary>
    public class ColonyRegistryRequestPacket : IPacket
    {
        public void Serialize(BinaryWriter w) { }
        public void Deserialize(BinaryReader r) { }
        public void OnDispatched()
        {
            if (!MultiplayerSession.IsHost) return;
            ColonyNet.SendRegistryTo(ColonyNet.CurrentSender);
        }
    }

    /// <summary>Client -> host: found my colony with the Printing Pod at HomeCell.</summary>
    public class ColonyFoundRequestPacket : IPacket
    {
        public int HomeCell;
        public string ColonyName = "";
        public string OwnerName = "";
        /// <summary>Starting dupes the player picked (same encoding as ONI Together's printing pod options).</summary>
        public List<ONI_Together.Networking.Packets.Social.ImmigrantOptionEntry> Starters = new List<ONI_Together.Networking.Packets.Social.ImmigrantOptionEntry>();

        public void Serialize(BinaryWriter w)
        {
            w.Write(HomeCell); w.Write(ColonyName ?? ""); w.Write(OwnerName ?? "");
            w.Write(Starters.Count);
            foreach (var s in Starters) s.Serialize(w);
        }

        public void Deserialize(BinaryReader r)
        {
            HomeCell = r.ReadInt32(); ColonyName = r.ReadString(); OwnerName = r.ReadString();
            int n = r.ReadInt32();
            Starters = new List<ONI_Together.Networking.Packets.Social.ImmigrantOptionEntry>(n);
            for (int i = 0; i < n; i++) Starters.Add(ONI_Together.Networking.Packets.Social.ImmigrantOptionEntry.Deserialize(r));
        }

        public void OnDispatched()
        {
            if (!MultiplayerSession.IsHost) return;
            var stats = new List<MinionStartingStats>();
            foreach (var e in Starters)
            {
                try { if (e.IsDuplicant && e.ToGameDeliverable() is MinionStartingStats s) stats.Add(s); }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] bad starter dupe: {ex.Message}"); }
            }
            ColonyFounder.HostHandleRequest(ColonyNet.CurrentSender, HomeCell, ColonyName, OwnerName, stats);
        }
    }

    /// <summary>Host -> clients: object ownership changes, keyed by NetworkIdentity.NetId.</summary>
    public class ColonyOwnersPacket : IPacket
    {
        public static bool Applying;
        public List<int> NetIds = new List<int>();
        public List<int> Colonies = new List<int>();

        public static ColonyOwnersPacket FullSnapshot()
        {
            var p = new ColonyOwnersPacket();
            foreach (var go in OwnedNetworkObjects())
            {
                var ni = go.GetComponent<NetworkIdentity>();
                if (ni == null || ni.NetId == 0) continue;
                p.NetIds.Add(ni.NetId);
                p.Colonies.Add(ColonyRegistry.GetOwner(go));
            }
            return p;
        }

        private static IEnumerable<GameObject> OwnedNetworkObjects()
        {
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
                if (mi != null && ColonyRegistry.GetOwner(mi.gameObject) != ColonyRegistry.NoColony) yield return mi.gameObject;
            foreach (BuildingComplete b in global::Components.BuildingCompletes.Items)
                if (b != null && ColonyRegistry.GetOwner(b.gameObject) != ColonyRegistry.NoColony) yield return b.gameObject;
        }

        public void Serialize(BinaryWriter w)
        {
            int n = Math.Min(NetIds.Count, Colonies.Count);
            w.Write(n);
            for (int i = 0; i < n; i++) { w.Write(NetIds[i]); w.Write(Colonies[i]); }
        }

        public void Deserialize(BinaryReader r)
        {
            int n = r.ReadInt32();
            NetIds = new List<int>(n); Colonies = new List<int>(n);
            for (int i = 0; i < n; i++) { NetIds.Add(r.ReadInt32()); Colonies.Add(r.ReadInt32()); }
        }

        public void OnDispatched()
        {
            if (MultiplayerSession.IsHost) return;
            Applying = true;
            try
            {
                for (int i = 0; i < NetIds.Count; i++)
                    if (NetworkIdentityRegistry.TryGet(NetIds[i], out var ni) && ni != null)
                        ColonyRegistry.SetOwner(ni.gameObject, Colonies[i]);
            }
            finally { Applying = false; }
        }
    }

    /// <summary>Host -> one client: short feedback text (e.g. why founding was refused).</summary>
    public class ColonyMessagePacket : IPacket
    {
        public string Text = "";
        public void Serialize(BinaryWriter w) { w.Write(Text ?? ""); }
        public void Deserialize(BinaryReader r) { Text = r.ReadString(); }
        public void OnDispatched() => ColonyMessages.Push(Text);
    }
}
