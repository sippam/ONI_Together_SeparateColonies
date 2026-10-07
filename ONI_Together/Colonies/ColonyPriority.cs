using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Errand priority (1-9 / !!) sync that doesn't depend on NetIds.
    /// ONI Together only syncs SetMasterPriority for objects that carry a NetworkIdentity (finished buildings), so a
    /// client changing the priority of a dig order, blueprint, mop/sweep marker... never reached the host and the next
    /// sync put it back to 5. Here every local change is sent as (cell, prefab, priority); the host applies it under the
    /// sender's colony (guards apply) and relays it to everyone. Batched per frame.
    /// </summary>
    public static class ColonyPriority
    {
        public static bool Applying { get; private set; }

        /// <summary>&gt; 0 while the mod itself changes priorities locally (e.g. relinking plants) — not a player action.</summary>
        public static int Suppress;

        private static readonly Dictionary<long, ColonyPriorityPacket.Entry> _pending = new Dictionary<long, ColonyPriorityPacket.Entry>();

        private static long Key(int cell, int tagHash) => ((long)cell << 32) ^ (uint)tagHash;

        /// <summary>Called from the SetMasterPriority postfix for local player changes.</summary>
        internal static void Queue(Prioritizable p, PrioritySetting setting)
        {
            var kpid = p.GetComponent<KPrefabID>();
            if (kpid == null) return;
            int cell = Grid.PosToCell(p);
            if (!Grid.IsValidCell(cell)) return;
            int hash = kpid.PrefabTag.GetHash();
            _pending[Key(cell, hash)] = new ColonyPriorityPacket.Entry
            {
                Cell = cell, TagHash = hash, Class = (int)setting.priority_class, Value = setting.priority_value
            };
        }

        /// <summary>ColonySession tick: send what changed this frame.</summary>
        public static void Flush()
        {
            if (_pending.Count == 0) return;
            if (!MultiplayerSession.InSession) { _pending.Clear(); return; }
            var pkt = new ColonyPriorityPacket();
            pkt.Entries.AddRange(_pending.Values);
            _pending.Clear();
            if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(pkt);
            else PacketSender.SendToHost(pkt);
        }

        public static Prioritizable Find(int cell, int tagHash)
        {
            if (!Grid.IsValidCell(cell)) return null;
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[cell, layer];
                if (go == null) continue;
                var kpid = go.GetComponent<KPrefabID>();
                if (kpid == null || kpid.PrefabTag.GetHash() != tagHash) continue;
                var p = go.GetComponent<Prioritizable>();
                if (p != null) return p;
            }
            return null;
        }

        internal static void OnPacket(ColonyPriorityPacket pkt)
        {
            var applied = new ColonyPriorityPacket();
            Applying = true;
            try
            {
                foreach (var e in pkt.Entries)
                {
                    var p = Find(e.Cell, e.TagHash);
                    if (p == null) continue;
                    var setting = new PrioritySetting((PriorityScreen.PriorityClass)e.Class, e.Value);
                    if (!p.GetMasterPriority().Equals(setting))
                        p.SetMasterPriority(setting); // host: colony guard runs with the sender's colony
                    if (p.GetMasterPriority().Equals(setting)) applied.Entries.Add(e);
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] priority sync: {ex.Message}"); }
            finally { Applying = false; }

            // host: relay what was accepted to every client (the sender gets it back too, harmless)
            if (MultiplayerSession.IsHost && applied.Entries.Count > 0)
                PacketSender.SendToAllClients(applied);
        }

        [HarmonyPatch(typeof(Prioritizable), nameof(Prioritizable.SetMasterPriority))]
        public static class Prioritizable_SetMasterPriority_Sync
        {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(Prioritizable __instance, PrioritySetting priority, bool __runOriginal)
            {
                if (!__runOriginal || __instance == null) return;          // blocked by a colony guard
                if (!MultiplayerSession.InSession) return;
                if (Applying || Suppress > 0 || PrioritizeStatePacket.IsApplying || StructureSync.Applying) return;
                if (ColonyNet.DispatchDepth > 0) return;                    // applying someone else's packet
                try { Queue(__instance, priority); }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] priority queue: {ex.Message}"); }
            }
        }
    }

    /// <summary>Client -> host (requests) and host -> clients (accepted): errand priorities by cell + prefab.</summary>
    public class ColonyPriorityPacket : IPacket
    {
        public struct Entry
        {
            public int Cell, TagHash, Class, Value;
        }

        public List<Entry> Entries = new List<Entry>();

        public void Serialize(BinaryWriter w)
        {
            w.Write(Entries.Count);
            foreach (var e in Entries) { w.Write(e.Cell); w.Write(e.TagHash); w.Write((byte)(e.Class + 1)); w.Write((byte)e.Value); }
        }

        public void Deserialize(BinaryReader r)
        {
            int n = Math.Min(r.ReadInt32(), 20000);
            Entries = new List<Entry>(Math.Max(0, n));
            for (int i = 0; i < n; i++)
                Entries.Add(new Entry { Cell = r.ReadInt32(), TagHash = r.ReadInt32(), Class = r.ReadByte() - 1, Value = r.ReadByte() });
        }

        public void OnDispatched() => ColonyPriority.OnPacket(this);
    }
}
