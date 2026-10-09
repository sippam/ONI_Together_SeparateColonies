using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Wrangle / attack orders on critters from joined players.
    ///
    /// ONI Together only forwarded the drag tools as a rectangle, which the host re-evaluates against ITS critter
    /// positions (often different from the client's), and the per-critter buttons (select a critter -> Wrangle /
    /// Attack) were not sent at all. Here every local change of Capturable.markedForCapture / FactionAlignment.targeted
    /// on a client is sent as (NetId, cell, prefab, state, priority); the host applies it under the sender's colony
    /// and relays the result so every player sees the order.
    /// </summary>
    public static class CritterOrders
    {
        public const byte KindCapture = 0, KindAttack = 1;

        [ThreadStatic] private static int _applying;
        private static readonly List<CritterOrderPacket.Entry> _pending = new List<CritterOrderPacket.Entry>();

        private static bool ShouldSend()
        {
            if (!MultiplayerSession.InSession || _applying > 0) return false;
            if (ColonyNet.DispatchDepth > 0) return false;           // applying someone else's packet
            if (ColonySession.GameAge < 5f) return false;           // load / OnSpawn restoring saved state
            return true;
        }

        private static void Queue(GameObject go, byte kind, bool state)
        {
            if (go == null) return;
            var kpid = go.GetComponent<KPrefabID>();
            if (kpid == null) return;
            int netId = 0;
            var id = go.GetComponent<NetworkIdentity>();
            if (id != null) netId = id.NetId;
            var p = go.GetComponent<Prioritizable>();
            var prio = p != null ? p.GetMasterPriority() : new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);
            _pending.Add(new CritterOrderPacket.Entry
            {
                NetId = netId, Cell = Grid.PosToCell(go), Prefab = kpid.PrefabTag.GetHash(), Kind = kind, State = state,
                PrioClass = (int)prio.priority_class, PrioValue = prio.priority_value
            });
        }

        /// <summary>ColonySession tick.</summary>
        public static void Flush()
        {
            if (_pending.Count == 0) return;
            if (!MultiplayerSession.InSession) { _pending.Clear(); return; }
            var pkt = new CritterOrderPacket();
            pkt.Entries.AddRange(_pending);
            _pending.Clear();
            if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(pkt);
            else PacketSender.SendToHost(pkt);
        }

        private static GameObject Find(CritterOrderPacket.Entry e)
        {
            if (e.NetId != 0 && NetworkIdentityRegistry.TryGet(e.NetId, out var identity) && identity != null)
            {
                var kp = identity.GetComponent<KPrefabID>();
                if (kp != null && kp.PrefabTag.GetHash() == e.Prefab) return identity.gameObject;
            }
            // fallback: nearest critter of that kind within a few cells (NetIds of critters can differ per machine)
            if (!Grid.IsValidCell(e.Cell)) return null;
            Grid.CellToXY(e.Cell, out int x0, out int y0);
            GameObject best = null; int bestD = 5 * 5 + 1;
            void Consider(KMonoBehaviour c)
            {
                if (c == null) return;
                var kp = c.GetComponent<KPrefabID>();
                if (kp == null || kp.PrefabTag.GetHash() != e.Prefab) return;
                int cell = Grid.PosToCell(c);
                if (!Grid.IsValidCell(cell)) return;
                Grid.CellToXY(cell, out int x, out int y);
                int d = (x - x0) * (x - x0) + (y - y0) * (y - y0);
                if (d < bestD) { bestD = d; best = c.gameObject; }
            }
            if (e.Kind == KindCapture) foreach (var c in global::Components.Capturables.Items) Consider(c);
            else foreach (var f in global::Components.FactionAlignments.Items) Consider(f);
            return best;
        }

        internal static void OnPacket(CritterOrderPacket pkt)
        {
            var applied = new CritterOrderPacket();
            _applying++;
            try
            {
                foreach (var e in pkt.Entries)
                {
                    try
                    {
                        var go = Find(e);
                        if (go == null) continue;
                        var prio = new PrioritySetting((PriorityScreen.PriorityClass)e.PrioClass, e.PrioValue);
                        bool now;
                        if (e.Kind == KindCapture)
                        {
                            var cap = go.GetComponent<Capturable>();
                            if (cap == null) continue;
                            cap.MarkForCapture(e.State, prio, true); // host: colony guard runs with the sender's colony
                            now = cap.IsMarkedForCapture;
                        }
                        else
                        {
                            var fa = go.GetComponent<FactionAlignment>();
                            if (fa == null) continue;
                            fa.SetPlayerTargeted(e.State);
                            if (e.State) go.GetComponent<Prioritizable>()?.SetMasterPriority(prio);
                            now = fa.IsPlayerTargeted();
                        }
                        if (MultiplayerSession.IsHost)
                        {
                            var id = go.GetComponent<NetworkIdentity>();
                            var r = e;
                            r.NetId = id != null ? id.NetId : 0;
                            r.Cell = Grid.PosToCell(go);
                            r.State = now;
                            applied.Entries.Add(r);
                            Debug.Log($"[ONIT-Colonies] critter order from {ColonyNet.CurrentSender}: {(e.Kind == KindCapture ? "wrangle" : "attack")} {go.PrefabID()} -> {now}");
                        }
                    }
                    catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] critter order: {ex.Message}"); }
                }
            }
            finally { _applying--; }

            if (MultiplayerSession.IsHost && applied.Entries.Count > 0)
                PacketSender.SendToAllClients(applied);
        }

        [HarmonyPatch(typeof(Capturable), nameof(Capturable.MarkForCapture), new Type[] { typeof(bool), typeof(PrioritySetting), typeof(bool) })]
        public static class Capturable_MarkForCapture_Sync
        {
            public static void Prefix(Capturable __instance, out bool __state) => __state = __instance != null && __instance.IsMarkedForCapture;

            [HarmonyPriority(Priority.Low)]
            public static void Postfix(Capturable __instance, bool __state, bool mark, bool updateMarkedPriority)
            {
                if (__instance == null || !ShouldSend()) return;
                bool now = __instance.IsMarkedForCapture;
                // only player-visible changes (a priority update on an already marked critter counts too)
                if (now == __state && !(now && updateMarkedPriority)) return;
                // client: send its intent (what was asked), host: send the result
                Queue(__instance.gameObject, KindCapture, MultiplayerSession.IsHost ? now : mark);
            }
        }

        [HarmonyPatch(typeof(FactionAlignment), nameof(FactionAlignment.SetPlayerTargeted))]
        public static class FactionAlignment_SetPlayerTargeted_Sync
        {
            public static void Prefix(FactionAlignment __instance, out bool __state) => __state = __instance != null && __instance.IsPlayerTargeted();

            [HarmonyPriority(Priority.Low)]
            public static void Postfix(FactionAlignment __instance, bool __state, bool state)
            {
                if (__instance == null || !ShouldSend()) return;
                bool now = __instance.IsPlayerTargeted();
                if (now == __state) return;
                Queue(__instance.gameObject, KindAttack, MultiplayerSession.IsHost ? now : state);
            }
        }
    }

    /// <summary>Client -> host (requests) and host -> clients (results): wrangle / attack orders on critters.</summary>
    public class CritterOrderPacket : IPacket
    {
        public struct Entry
        {
            public int NetId, Cell, Prefab, PrioClass, PrioValue;
            public byte Kind;
            public bool State;
        }

        public List<Entry> Entries = new List<Entry>();

        public void Serialize(BinaryWriter w)
        {
            w.Write(Entries.Count);
            foreach (var e in Entries)
            {
                w.Write(e.NetId); w.Write(e.Cell); w.Write(e.Prefab); w.Write(e.Kind); w.Write(e.State);
                w.Write((byte)(e.PrioClass + 2)); w.Write((byte)e.PrioValue);
            }
        }

        public void Deserialize(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 4096) n = 0;
            Entries = new List<Entry>(n);
            for (int i = 0; i < n; i++)
                Entries.Add(new Entry
                {
                    NetId = r.ReadInt32(), Cell = r.ReadInt32(), Prefab = r.ReadInt32(), Kind = r.ReadByte(), State = r.ReadBoolean(),
                    PrioClass = r.ReadByte() - 2, PrioValue = r.ReadByte()
                });
        }

        public void OnDispatched() => CritterOrders.OnPacket(this);
    }
}
