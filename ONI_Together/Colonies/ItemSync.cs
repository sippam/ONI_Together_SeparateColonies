using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Background reconciliation of loose items (debris, ore chunks, food, seeds... lying in the world).
    ///
    /// Why: ONI Together addresses items by NetId. Items the host creates through paths that don't announce them
    /// (machine output, drops, splitting/merging stacks...) either never exist on clients or exist there under another
    /// NetId. Every later pickup / store / eat packet then fails ("NetId not yet registered", "Failed to resolve
    /// target"): on a cycle-30 save that was ~500 failures a minute and the client's world kept drifting.
    ///
    /// How (same scheme as StructureSync): every few seconds the host sends one hash per 16x16 block of
    /// (NetId, prefab, cell) of its loose items. A client compares with its own; blocks still different after a grace
    /// period are requested and the host answers with the real list. The client then re-keys its matching item to the
    /// host NetId, moves stray items, removes phantoms and spawns what is missing (with the host NetId), and corrects
    /// mass/temperature drift. Stored, carried, critters and duplicants are not touched.
    /// </summary>
    public static class ItemSync
    {
        public const int BlockSize = 16;
        private const float DigestInterval = 6f;
        private const float ClientGrace = 3f;
        private const int MaxBlocksPerRequest = 6;
        private const int MaxEntriesPerBlock = 400;

        public static bool Enabled => Configuration.Instance?.Colonies?.BackgroundStructureSync ?? true;
        public static bool Applying { get; private set; }
        public static int FixedItems;

        private static int BlocksX => (Grid.WidthInCells + BlockSize - 1) / BlockSize;
        private static int BlocksY => (Grid.HeightInCells + BlockSize - 1) / BlockSize;
        private static int BlockCount => BlocksX * BlocksY;
        private static int BlockOf(int cell) { Grid.CellToXY(cell, out int x, out int y); return (y / BlockSize) * BlocksX + (x / BlockSize); }

        /// <summary>A loose item we reconcile (not stored / carried, not a critter or duplicant).</summary>
        public static bool Tracked(Pickupable p)
        {
            if (p == null || p.gameObject == null || !p.gameObject.activeInHierarchy) return false;
            if (p.storage != null) return false;
            var kpid = p.KPrefabID;
            if (kpid == null) return false;
            if (kpid.HasTag(GameTags.Stored) || kpid.HasTag(GameTags.StoredPrivate) || kpid.HasTag(GameTags.Creature)
                || kpid.HasTag(GameTags.BaseMinion) || kpid.HasTag(GameTags.Robot) || kpid.HasTag(GameTags.Equipped)) return false;
            if (p.GetComponent<MinionIdentity>() != null) return false;
            return true;
        }

        private static int NetIdOf(GameObject go)
        {
            var id = go.GetComponent<NetworkIdentity>();
            if (id == null) { id = go.AddOrGet<NetworkIdentity>(); id.RegisterIdentity(); }
            return id.NetId;
        }

        private static int EntryHash(int netId, int prefab, int cell)
        {
            unchecked
            {
                int h = (int)2166136261;
                h = (h ^ netId) * 16777619;
                h = (h ^ prefab) * 16777619;
                h = (h ^ cell) * 16777619;
                return h;
            }
        }

        public static int[] ComputeAllBlockHashes()
        {
            var hashes = new int[BlockCount];
            foreach (var p in global::Components.Pickupables.Items)
            {
                if (!Tracked(p)) continue;
                int cell = Grid.PosToCell(p);
                if (!Grid.IsValidCell(cell)) continue;
                int b = BlockOf(cell);
                if (b < 0 || b >= hashes.Length) continue;
                unchecked { hashes[b] += EntryHash(NetIdOf(p.gameObject), p.KPrefabID.PrefabTag.GetHash(), cell); }
            }
            return hashes;
        }

        private static List<Pickupable> ItemsInBlock(int block)
        {
            var list = new List<Pickupable>();
            foreach (var p in global::Components.Pickupables.Items)
            {
                if (!Tracked(p)) continue;
                int cell = Grid.PosToCell(p);
                if (Grid.IsValidCell(cell) && BlockOf(cell) == block) list.Add(p);
            }
            return list;
        }

        private static int ComputeBlockHash(int block)
        {
            int h = 0;
            foreach (var p in ItemsInBlock(block))
                unchecked { h += EntryHash(NetIdOf(p.gameObject), p.KPrefabID.PrefabTag.GetHash(), Grid.PosToCell(p)); }
            return h;
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
                var hashes = ComputeAllBlockHashes();
                const int chunk = 1024;
                for (int start = 0; start < hashes.Length; start += chunk)
                {
                    int n = Math.Min(chunk, hashes.Length - start);
                    var p = new ItemDigestPacket { Width = Grid.WidthInCells, Height = Grid.HeightInCells, Start = start, Hashes = new int[n] };
                    Array.Copy(hashes, start, p.Hashes, 0, n);
                    PacketSender.SendToAllClients(p);
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] item digest: {ex.Message}"); }
        }

        public static void HostSendDetails(ulong player, List<int> blocks)
        {
            if (!MultiplayerSession.IsHost || player == 0 || blocks == null) return;
            foreach (int block in blocks)
            {
                if (block < 0 || block >= BlockCount) continue;
                var pkt = new ItemDetailPacket { Block = block };
                foreach (var p in ItemsInBlock(block))
                {
                    if (pkt.Entries.Count >= MaxEntriesPerBlock) break;
                    var pe = p.GetComponent<PrimaryElement>();
                    var pos = p.transform.GetPosition();
                    pkt.Entries.Add(new ItemEntry
                    {
                        NetId = NetIdOf(p.gameObject),
                        Prefab = p.KPrefabID.PrefabTag.Name,
                        Cell = Grid.PosToCell(p),
                        X = pos.x, Y = pos.y,
                        Mass = pe != null ? pe.Mass : 0f,
                        Units = pe != null ? pe.Units : 1f,
                        Temperature = pe != null ? pe.Temperature : 293f,
                        DiseaseIdx = pe != null ? pe.DiseaseIdx : byte.MaxValue,
                        DiseaseCount = pe != null ? pe.DiseaseCount : 0,
                        IsChunk = p.GetComponent<ElementChunk>() != null,
                    });
                }
                PacketSender.SendToPlayer(player, pkt);
            }
        }

        #endregion

        #region Client

        private struct Pending { public int HostHash; public float Since; }
        private static readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private static float _clientTimer;

        public static void Reset() { _pending.Clear(); }

        public static void OnDigest(ItemDigestPacket p)
        {
            if (!Enabled || MultiplayerSession.IsHost || global::Game.Instance == null || GameClient.IsHardSyncInProgress) return;
            if (p.Width != Grid.WidthInCells || p.Height != Grid.HeightInCells) return;
            int[] local = ComputeAllBlockHashes();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < p.Hashes.Length; i++)
            {
                int b = p.Start + i;
                if (b < 0 || b >= local.Length) continue;
                if (local[b] == p.Hashes[i]) { _pending.Remove(b); continue; }
                if (!_pending.TryGetValue(b, out var pend) || pend.HostHash != p.Hashes[i])
                    _pending[b] = new Pending { HostHash = p.Hashes[i], Since = now };
            }
        }

        public static void ClientTick()
        {
            if (!Enabled || MultiplayerSession.IsHost || !MultiplayerSession.InSession || _pending.Count == 0) return;
            _clientTimer += Time.unscaledDeltaTime;
            if (_clientTimer < 1f) return;
            _clientTimer = 0f;
            float now = Time.realtimeSinceStartup;
            var request = new List<int>();
            var drop = new List<int>();
            foreach (var kv in _pending)
            {
                if (now - kv.Value.Since < ClientGrace) continue;
                if (ComputeBlockHash(kv.Key) != kv.Value.HostHash) request.Add(kv.Key);
                drop.Add(kv.Key);
                if (request.Count >= MaxBlocksPerRequest) break;
            }
            foreach (var b in drop) _pending.Remove(b);
            if (request.Count > 0) PacketSender.SendToHost(new ItemDetailRequestPacket { Blocks = request });
        }

        public static void ApplyDetail(ItemDetailPacket p)
        {
            if (MultiplayerSession.IsHost || global::Game.Instance == null || GameClient.IsHardSyncInProgress) return;
            if (ColonySession.GameAge < 15f) return;
            var want = new Dictionary<int, ItemEntry>();
            foreach (var e in p.Entries) want[e.NetId] = e;
            var have = ItemsInBlock(p.Block);
            int changes = 0;
            Applying = true;
            try
            {
                var unmatched = new List<Pickupable>();
                // 1) same NetId: fix drift
                foreach (var it in have)
                {
                    int id = NetIdOf(it.gameObject);
                    if (want.TryGetValue(id, out var e) && e.Prefab == it.KPrefabID.PrefabTag.Name)
                    {
                        if (Fix(it.gameObject, e)) changes++;
                        want.Remove(id);
                    }
                    else unmatched.Add(it);
                }
                // 2) same prefab, other NetId (the client made its own copy): adopt the host NetId
                foreach (var it in unmatched)
                {
                    string prefab = it.KPrefabID.PrefabTag.Name;
                    int cell = Grid.PosToCell(it);
                    ItemEntry best = null;
                    foreach (var e in want.Values)
                        if (e.Prefab == prefab && (best == null || Math.Abs(e.Cell - cell) < Math.Abs(best.Cell - cell))) best = e;
                    if (best != null && !NetworkIdentityRegistry.TryGet(best.NetId, out _))
                    {
                        it.gameObject.AddOrGet<NetworkIdentity>().OverrideNetId(best.NetId);
                        Fix(it.gameObject, best);
                        want.Remove(best.NetId);
                        changes++;
                        continue;
                    }
                    // 3) the host doesn't have it here: phantom
                    if (!StructureSync.IsFullySpawned(it.gameObject)) continue;
                    Util.KDestroyGameObject(it.gameObject);
                    changes++;
                }
                // 4) missing: move it if it exists elsewhere under that NetId, otherwise spawn it
                foreach (var e in want.Values)
                {
                    if (NetworkIdentityRegistry.TryGet(e.NetId, out var existing) && existing != null)
                    {
                        var pk = existing.GetComponent<Pickupable>();
                        if (pk != null && pk.storage == null) { Fix(existing.gameObject, e); changes++; continue; }
                        if (pk != null) continue; // stored/carried on this client: leave it to the storage packets
                    }
                    if (Spawn(e)) changes++;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] item sync apply: {ex.Message}"); }
            finally { Applying = false; }
            if (changes > 0)
            {
                FixedItems += changes;
                Debug.Log($"[ONIT-Colonies] Item sync fixed block {p.Block} ({changes} changes)");
            }
        }

        /// <summary>Position / mass / temperature like the host. True if something changed.</summary>
        private static bool Fix(GameObject go, ItemEntry e)
        {
            bool changed = false;
            var pos = go.transform.GetPosition();
            if (Mathf.Abs(pos.x - e.X) > 0.6f || Mathf.Abs(pos.y - e.Y) > 0.6f)
            {
                go.transform.SetPosition(new Vector3(e.X, e.Y, pos.z));
                changed = true;
            }
            var pe = go.GetComponent<PrimaryElement>();
            if (pe != null)
            {
                if (e.Mass > 0f && Mathf.Abs(pe.Mass - e.Mass) > Mathf.Max(0.01f, e.Mass * 0.02f)) { pe.Mass = e.Mass; changed = true; }
                if (Mathf.Abs(pe.Temperature - e.Temperature) > 2f) { pe.Temperature = e.Temperature; changed = true; }
            }
            return changed;
        }

        private static bool Spawn(ItemEntry e)
        {
            try
            {
                var pos = new Vector3(e.X, e.Y, Grid.GetLayerZ(Grid.SceneLayer.Ore));
                GameObject go = null;
                if (e.IsChunk)
                {
                    var element = ElementLoader.GetElement(new Tag(e.Prefab));
                    if (element?.substance == null) return false;
                    go = element.substance.SpawnResource(pos, Mathf.Max(0.001f, e.Mass), e.Temperature, e.DiseaseIdx, e.DiseaseCount);
                }
                else
                {
                    var prefab = Assets.GetPrefab(new Tag(e.Prefab));
                    if (prefab == null) return false;
                    go = Util.KInstantiate(prefab, pos);
                    if (go == null) return false;
                    go.SetActive(true);
                    var pe = go.GetComponent<PrimaryElement>();
                    if (pe != null)
                    {
                        if (e.Mass > 0f) pe.Mass = e.Mass;
                        pe.Temperature = e.Temperature;
                    }
                }
                if (go == null) return false;
                go.AddOrGet<NetworkIdentity>().OverrideNetId(e.NetId);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] item spawn {e.Prefab} failed: {ex.Message}");
                return false;
            }
        }

        #endregion
    }

    public class ItemEntry
    {
        public int NetId, Cell, DiseaseCount;
        public string Prefab = "";
        public float X, Y, Mass, Units, Temperature;
        public byte DiseaseIdx;
        public bool IsChunk;

        public void Write(BinaryWriter w)
        {
            w.Write(NetId); w.Write(Prefab ?? ""); w.Write(Cell); w.Write(X); w.Write(Y);
            w.Write(Mass); w.Write(Units); w.Write(Temperature); w.Write(DiseaseIdx); w.Write(DiseaseCount); w.Write(IsChunk);
        }

        public static ItemEntry Read(BinaryReader r) => new ItemEntry
        {
            NetId = r.ReadInt32(), Prefab = r.ReadString(), Cell = r.ReadInt32(), X = r.ReadSingle(), Y = r.ReadSingle(),
            Mass = r.ReadSingle(), Units = r.ReadSingle(), Temperature = r.ReadSingle(), DiseaseIdx = r.ReadByte(),
            DiseaseCount = r.ReadInt32(), IsChunk = r.ReadBoolean()
        };
    }

    /// <summary>Host -> clients: per-block hashes of loose items.</summary>
    public class ItemDigestPacket : IPacket
    {
        public int Width, Height, Start;
        public int[] Hashes = new int[0];
        public void Serialize(BinaryWriter w) { w.Write(Width); w.Write(Height); w.Write(Start); w.Write(Hashes.Length); foreach (var h in Hashes) w.Write(h); }
        public void Deserialize(BinaryReader r)
        {
            Width = r.ReadInt32(); Height = r.ReadInt32(); Start = r.ReadInt32();
            int n = r.ReadInt32(); Hashes = new int[Math.Max(0, Math.Min(n, 1 << 20))];
            for (int i = 0; i < Hashes.Length; i++) Hashes[i] = r.ReadInt32();
        }
        public void OnDispatched() => ItemSync.OnDigest(this);
    }

    /// <summary>Client -> host: send me the loose items of these blocks.</summary>
    public class ItemDetailRequestPacket : IPacket
    {
        public List<int> Blocks = new List<int>();
        public void Serialize(BinaryWriter w) { w.Write(Blocks.Count); foreach (var b in Blocks) w.Write(b); }
        public void Deserialize(BinaryReader r) { int n = r.ReadInt32(); Blocks = new List<int>(); for (int i = 0; i < n && i < 64; i++) Blocks.Add(r.ReadInt32()); }
        public void OnDispatched() => ItemSync.HostSendDetails(ColonyNet.CurrentSender, Blocks);
    }

    /// <summary>Host -> one client: the loose items of one block.</summary>
    public class ItemDetailPacket : IPacket
    {
        public int Block;
        public List<ItemEntry> Entries = new List<ItemEntry>();
        public void Serialize(BinaryWriter w) { w.Write(Block); w.Write(Entries.Count); foreach (var e in Entries) e.Write(w); }
        public void Deserialize(BinaryReader r)
        {
            Block = r.ReadInt32();
            int n = r.ReadInt32();
            Entries = new List<ItemEntry>(Math.Max(0, Math.Min(n, 4096)));
            for (int i = 0; i < n && i < 4096; i++) Entries.Add(ItemEntry.Read(r));
        }
        public void OnDispatched() => ItemSync.ApplyDetail(this);
    }
}
