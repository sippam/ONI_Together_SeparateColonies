using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// Host: collects the position updates of all entities this frame and sends each player ONE (or a few, MTU-sized)
    /// unreliable packet with only the entities in that player's view — instead of one packet per entity per frame.
    /// Flushed from ColonySession every frame.
    /// </summary>
    public static class EntityPositionBatcher
    {
        private const int MaxEntriesPerPacket = 48; // ~19 bytes each -> stays well under one UDP datagram
        private static readonly List<EntityPositionBatchPacket.Entry> _pending = new List<EntityPositionBatchPacket.Entry>();
        private static readonly List<EntityPositionBatchPacket.Entry> _scratch = new List<EntityPositionBatchPacket.Entry>();

        public static void Add(EntityPositionBatchPacket.Entry e)
        {
            if (_pending.Count < 8192) _pending.Add(e);
        }

        public static void Flush()
        {
            if (_pending.Count == 0) return;
            try
            {
                if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var wss = WorldStateSyncer.Instance;
                foreach (var player in MultiplayerSession.ConnectedPlayers.Values)
                {
                    if (player == null || player.PlayerId == MultiplayerSession.HostUserID || player.Connection == null) continue;
                    _scratch.Clear();
                    foreach (var e in _pending)
                    {
                        int cell = Grid.PosToCell(new Vector3(e.X, e.Y, 0f));
                        if (wss != null && !wss.IsCellInPlayerViewport(player.PlayerId, cell)) continue;
                        _scratch.Add(e);
                    }
                    for (int i = 0; i < _scratch.Count; i += MaxEntriesPerPacket)
                    {
                        var pkt = new EntityPositionBatchPacket { Timestamp = ts };
                        pkt.Entries.AddRange(_scratch.GetRange(i, Math.Min(MaxEntriesPerPacket, _scratch.Count - i)));
                        PacketSender.SendToPlayer(player.PlayerId, pkt, PacketSendMode.Unreliable);
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] position batch: {ex.Message}"); }
            finally { _pending.Clear(); }
        }
    }

    /// <summary>Host -> one client: positions of several entities (x, y, flips, nav type, prefab).</summary>
    public class EntityPositionBatchPacket : IPacket
    {
        public struct Entry
        {
            public int NetId, Prefab;
            public float X, Y;
            public bool FlipX, FlipY;
            public NavType NavType;
        }

        public long Timestamp;
        public List<Entry> Entries = new List<Entry>();

        public void Serialize(BinaryWriter w)
        {
            w.Write(Timestamp);
            w.Write((ushort)Entries.Count);
            foreach (var e in Entries)
            {
                w.Write(e.NetId); w.Write(e.Prefab); w.Write(e.X); w.Write(e.Y);
                byte flags = (byte)((e.FlipX ? 1 : 0) | (e.FlipY ? 2 : 0));
                w.Write(flags); w.Write((byte)e.NavType);
            }
        }

        public void Deserialize(BinaryReader r)
        {
            Timestamp = r.ReadInt64();
            int n = r.ReadUInt16();
            Entries = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { NetId = r.ReadInt32(), Prefab = r.ReadInt32(), X = r.ReadSingle(), Y = r.ReadSingle() };
                byte flags = r.ReadByte();
                e.FlipX = (flags & 1) != 0; e.FlipY = (flags & 2) != 0;
                e.NavType = (NavType)r.ReadByte();
                Entries.Add(e);
            }
        }

        public void OnDispatched()
        {
            if (MultiplayerSession.IsHost) return;
            foreach (var e in Entries)
            {
                try { EntityPositionPacket.Apply(e.NetId, new Vector3(e.X, e.Y, float.NaN), e.FlipX, e.FlipY, e.NavType, Timestamp, e.Prefab); }
                catch (Exception) { }
            }
        }
    }
}
