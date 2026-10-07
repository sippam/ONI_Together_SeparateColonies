using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Per-colony "whose work first" order (e.g. Host -> P2 -> P3). Each owner arranges their own list;
    /// their duplicants take errands colony by colony in that order (own colony's list slot also covers
    /// unowned / shared work). Default: own colony first, then the others in founding order.
    /// Stored in ColonyInfo.WorkOrder (saved + replicated with the registry); applied on the host in ChooseChore.
    /// </summary>
    public static class ColonyWorkOrder
    {
        /// <summary>Effective order for a colony: saved order filtered to existing colonies, missing ones appended.</summary>
        public static List<int> Get(int colonyId)
        {
            var result = new List<int>();
            var info = ColonyRegistry.Get(colonyId);
            if (info == null) return result;
            var existing = new HashSet<int>();
            foreach (var c in ColonyRegistry.Colonies) existing.Add(c.Id);
            if (info.WorkOrder != null)
                foreach (int id in info.WorkOrder)
                    if (existing.Contains(id) && !result.Contains(id)) result.Add(id);
            if (!result.Contains(colonyId)) result.Insert(0, colonyId);
            var rest = new List<int>();
            foreach (var c in ColonyRegistry.Colonies) if (!result.Contains(c.Id)) rest.Add(c.Id);
            rest.Sort();
            result.AddRange(rest);
            return result;
        }

        // ---- rank cache for ChooseChore (hot path, may run off the main thread) ----

        private static Dictionary<int, Dictionary<int, int>> _ranks = new Dictionary<int, Dictionary<int, int>>();
        private static int _builtAt = int.MinValue;

        public static void Invalidate() => _builtAt = int.MinValue;

        /// <summary>colony -> slot (0 = first) for the worker's colony, or null. Rebuilt at most once a second.</summary>
        internal static Dictionary<int, int> Ranks(int workerColony)
        {
            int now = Environment.TickCount;
            var map = _ranks;
            if (_builtAt == int.MinValue || unchecked(now - _builtAt) > 1000)
            {
                var fresh = new Dictionary<int, Dictionary<int, int>>();
                try
                {
                    foreach (var c in ColonyRegistry.Colonies)
                    {
                        var order = Get(c.Id);
                        var r = new Dictionary<int, int>();
                        for (int i = 0; i < order.Count; i++) r[order[i]] = i;
                        fresh[c.Id] = r;
                    }
                }
                catch { return null; } // list changed under us: try again next call
                _ranks = map = fresh;
                _builtAt = now;
            }
            return map.TryGetValue(workerColony, out var ranks) ? ranks : null;
        }

        // ---- editing ----

        public static void RequestSet(List<int> order)
        {
            if (order == null) return;
            if (ColonyNet.IsHost || !ColonyNet.InSession) HostSet(ColonyRegistry.LocalPlayerId, order);
            else PacketSender.SendToHost(new ColonyWorkOrderPacket { Order = new List<int>(order) });
        }

        public static void HostSet(ulong player, List<int> order)
        {
            var c = ColonyRegistry.Get(ColonyRegistry.GetColonyOfPlayer(player));
            if (c == null || order == null) return;
            var clean = new List<int>();
            foreach (int id in order)
                if (ColonyRegistry.Get(id) != null && !clean.Contains(id)) clean.Add(id);
            c.WorkOrder = clean;
            Invalidate();
            ColonyRegistry.RaiseChanged();
            ColonyNet.BroadcastRegistry();
            Debug.Log($"[ONIT-Colonies] Work order for '{c.Name}': {string.Join(" > ", clean)}");
        }

        /// <summary>Move an entry of the local colony's order one slot up (-1) or down (+1).</summary>
        public static void LocalMove(int index, int dir)
        {
            int mine = ColonyRegistry.LocalColonyId;
            if (mine == ColonyRegistry.NoColony) return;
            var order = Get(mine);
            int j = index + dir;
            if (index < 0 || index >= order.Count || j < 0 || j >= order.Count) return;
            (order[index], order[j]) = (order[j], order[index]);
            var info = ColonyRegistry.Get(mine);
            if (info != null) info.WorkOrder = new List<int>(order); // instant UI feedback; host echoes the registry
            RequestSet(order);
        }

        public static void LocalReset()
        {
            int mine = ColonyRegistry.LocalColonyId;
            var info = ColonyRegistry.Get(mine);
            if (info == null) return;
            info.WorkOrder = new List<int>();
            RequestSet(Get(mine));
        }
    }

    /// <summary>Client -> host: my colony's work order.</summary>
    public class ColonyWorkOrderPacket : IPacket
    {
        public List<int> Order = new List<int>();
        public void Serialize(BinaryWriter w) { w.Write(Order.Count); foreach (int id in Order) w.Write(id); }
        public void Deserialize(BinaryReader r)
        {
            int n = Math.Min(r.ReadInt32(), 64);
            Order = new List<int>(Math.Max(0, n));
            for (int i = 0; i < n; i++) Order.Add(r.ReadInt32());
        }
        public void OnDispatched()
        {
            if (!MultiplayerSession.IsHost) return;
            ColonyWorkOrder.HostSet(ColonyNet.CurrentSender, Order);
        }
    }
}
