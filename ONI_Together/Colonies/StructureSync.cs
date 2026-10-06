using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Background structure reconciliation — the incremental alternative to a full hard sync.
    ///
    /// ONI Together replicates buildings/plants with one-shot event packets (build, build complete, deconstruct,
    /// plant spawn...). When one gets lost or arrives before its target exists, the client keeps a stale
    /// blueprint / missing tile / "not planted" plot forever, and only a hard sync (full world reload) fixes it.
    ///
    /// Here the host periodically sends a digest: one hash per 16x16 block over every building, blueprint and
    /// plant (prefab + state + cell + layer). Clients hash their own blocks the same way; a block that still
    /// differs after a short grace period is requested, the host answers with the authoritative contents of
    /// just that block, and the client fixes only the differences. Cost: one pass over the object layer maps
    /// (~tens of thousands of entries) every few seconds and a few KB of traffic.
    /// </summary>
    public static class StructureSync
    {
        public const int BlockSize = 16;
        private const float DigestInterval = 5f;
        private const float ClientGrace = 2.5f;
        private const int MaxBlocksPerRequest = 12;

        public static readonly ObjectLayer[] Layers =
        {
            ObjectLayer.Building, ObjectLayer.Backwall, ObjectLayer.FoundationTile,
            ObjectLayer.GasConduit, ObjectLayer.GasConduitConnection,
            ObjectLayer.LiquidConduit, ObjectLayer.LiquidConduitConnection,
            ObjectLayer.SolidConduit, ObjectLayer.SolidConduitConnection,
            ObjectLayer.Wire, ObjectLayer.WireConnectors, ObjectLayer.LogicGate, ObjectLayer.LogicWire,
            ObjectLayer.TravelTube, ObjectLayer.TravelTubeConnection, ObjectLayer.LadderTile,
        };

        public const byte StateComplete = 1, StateConstruction = 2, StatePlant = 3;

        public static bool Enabled => Configuration.Instance?.Colonies?.BackgroundStructureSync ?? true;

        public static int BlocksX => (Grid.WidthInCells + BlockSize - 1) / BlockSize;
        public static int BlocksY => (Grid.HeightInCells + BlockSize - 1) / BlockSize;
        public static int BlockCount => BlocksX * BlocksY;

        public static int BlockOf(int cell)
        {
            Grid.CellToXY(cell, out int x, out int y);
            return (y / BlockSize) * BlocksX + (x / BlockSize);
        }

        /// <summary>Kind + id of a tracked object, or false for anything we don't reconcile.</summary>
        public static bool Classify(GameObject go, out byte state, out string id)
        {
            state = 0; id = null;
            if (go == null) return false;
            var building = go.GetComponent<Building>();
            if (building != null && building.Def != null)
            {
                if (go.GetComponent<BuildingComplete>() != null) state = StateComplete;
                else if (go.GetComponent<Constructable>() != null) state = StateConstruction;
                else return false;
                id = building.Def.PrefabID;
                return true;
            }
            var kpid = go.GetComponent<KPrefabID>();
            if (kpid != null && kpid.HasTag(GameTags.Plant) && go.GetComponent<Growing>() != null)
            {
                state = StatePlant;
                id = kpid.PrefabTag.Name;
                return true;
            }
            return false;
        }

        private static int EntryHash(int cell, int layer, string id, byte state)
        {
            unchecked
            {
                int h = (int)2166136261;
                h = (h ^ cell) * 16777619;
                h = (h ^ layer) * 16777619;
                h = (h ^ Hash.SDBMLower(id)) * 16777619;
                h = (h ^ state) * 16777619;
                return h;
            }
        }

        /// <summary>One pass over the tracked object layers: order-independent hash per block.</summary>
        public static int[] ComputeAllBlockHashes()
        {
            var hashes = new int[BlockCount];
            for (int li = 0; li < Layers.Length; li++)
            {
                int layer = (int)Layers[li];
                var map = Grid.ObjectLayers[layer];
                if (map == null) continue;
                foreach (var kv in map)
                {
                    var go = kv.Value;
                    if (go == null) continue;
                    int cell = kv.Key;
                    if (Grid.PosToCell(go) != cell) continue; // multi-cell objects count once, at their origin
                    if (!Classify(go, out byte state, out string id)) continue;
                    int b = BlockOf(cell);
                    if (b < 0 || b >= hashes.Length) continue;
                    unchecked { hashes[b] += EntryHash(cell, layer, id, state); }
                }
            }
            return hashes;
        }

        public static int ComputeBlockHash(int block)
        {
            int h = 0;
            ForEachInBlock(block, (cell, layer, go, state, id) => { unchecked { h += EntryHash(cell, layer, id, state); } });
            return h;
        }

        public delegate void EntryVisitor(int cell, int layer, GameObject go, byte state, string id);

        public static void ForEachInBlock(int block, EntryVisitor visit)
        {
            int bx = block % BlocksX, by = block / BlocksX;
            int x0 = bx * BlockSize, y0 = by * BlockSize;
            int x1 = Math.Min(x0 + BlockSize, Grid.WidthInCells), y1 = Math.Min(y0 + BlockSize, Grid.HeightInCells);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int cell = Grid.XYToCell(x, y);
                for (int li = 0; li < Layers.Length; li++)
                {
                    int layer = (int)Layers[li];
                    var map = Grid.ObjectLayers[layer];
                    if (map == null || !map.TryGetValue(cell, out var go) || go == null) continue;
                    if (Grid.PosToCell(go) != cell) continue;
                    if (!Classify(go, out byte state, out string id)) continue;
                    visit(cell, layer, go, state, id);
                }
            }
        }

        #region Host

        private static float _hostTimer;

        public static void HostTick()
        {
            if (!Enabled || !MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
            if (MultiplayerSession.ConnectedPlayers.Count <= 1 || GameServerHardSync.IsHardSyncInProgress) return;
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
                    var p = new StructureDigestPacket { Width = Grid.WidthInCells, Height = Grid.HeightInCells, Start = start, Hashes = new int[n] };
                    Array.Copy(hashes, start, p.Hashes, 0, n);
                    PacketSender.SendToAllClients(p);
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] structure digest failed: {ex.Message}"); }
        }

        public static void HostSendDetails(ulong player, List<int> blocks)
        {
            if (!MultiplayerSession.IsHost || player == 0 || blocks == null) return;
            foreach (int block in blocks)
            {
                if (block < 0 || block >= BlockCount) continue;
                var p = new StructureDetailPacket { Block = block };
                var plants = new List<Growing>();
                ForEachInBlock(block, (cell, layer, go, state, id) =>
                {
                    var e = new StructureEntry { Cell = cell, Layer = layer, Id = id, State = state };
                    var rot = go.GetComponent<Rotatable>();
                    e.Orientation = rot != null ? (int)rot.GetOrientation() : 0;
                    var pe = go.GetComponent<PrimaryElement>();
                    if (pe != null && pe.Element != null) { e.Element = pe.Element.tag.Name; e.Temperature = pe.Temperature; }
                    var facade = go.GetComponent<BuildingFacade>();
                    e.Facade = facade != null && !string.IsNullOrEmpty(facade.CurrentFacade) ? facade.CurrentFacade : "DEFAULT_FACADE";
                    var vis = go.GetComponent<KAnimGraphTileVisualizer>();
                    e.Connections = vis != null ? (int)vis.Connections : 0;
                    var con = go.GetComponent<Constructable>();
                    if (con != null && con.SelectedElementsTags != null)
                        foreach (var t in con.SelectedElementsTags) e.Materials.Add(t.Name);
                    p.Entries.Add(e);
                    if (state == StatePlant) { var g = go.GetComponent<Growing>(); if (g != null) plants.Add(g); }
                });
                PacketSender.SendToPlayer(player, p);
                // plants are re-created by ONI Together's own plant packet (keeps growth/receptacle data right)
                foreach (var g in plants)
                {
                    try
                    {
                        if (PlantGrowthSyncer.TryBuildPlantData(g, out var data))
                            PacketSender.SendToPlayer(player, new PlantLifecyclePacket { Operation = PlantLifecycleOperation.Spawn, Plant = data });
                    }
                    catch { }
                }
            }
        }

        #endregion

        #region Client

        private struct Pending { public int HostHash; public float Since; }
        private static readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private static float _clientTimer;
        public static int FixedBlocks;

        public static void Reset() { _pending.Clear(); }

        public static void OnDigest(StructureDigestPacket p)
        {
            if (MultiplayerSession.IsHost || global::Game.Instance == null || GameClient.IsHardSyncInProgress) return;
            if (p.Width != Grid.WidthInCells || p.Height != Grid.HeightInCells) return; // different world (mid-load)
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
            if (MultiplayerSession.IsHost || !MultiplayerSession.InSession || _pending.Count == 0) return;
            _clientTimer += Time.unscaledDeltaTime;
            if (_clientTimer < 1f) return;
            _clientTimer = 0f;
            float now = Time.realtimeSinceStartup;
            var request = new List<int>();
            var drop = new List<int>();
            foreach (var kv in _pending)
            {
                if (now - kv.Value.Since < ClientGrace) continue;
                // still different after the grace period (in-flight packets had their chance)?
                if (ComputeBlockHash(kv.Key) != kv.Value.HostHash) request.Add(kv.Key);
                drop.Add(kv.Key);
                if (request.Count >= MaxBlocksPerRequest) break;
            }
            foreach (var b in drop) _pending.Remove(b);
            if (request.Count > 0)
                PacketSender.SendToHost(new StructureDetailRequestPacket { Blocks = request });
        }

        public static bool Applying { get; private set; }

        public static void ApplyDetail(StructureDetailPacket p)
        {
            if (MultiplayerSession.IsHost || global::Game.Instance == null) return;
            var want = new Dictionary<long, StructureEntry>();
            foreach (var e in p.Entries) want[Key(e.Cell, e.Layer)] = e;

            var have = new Dictionary<long, (GameObject go, byte state, string id)>();
            ForEachInBlock(p.Block, (cell, layer, go, state, id) => have[Key(cell, layer)] = (go, state, id));

            int changes = 0;
            Applying = true;
            try
            {
                // 1) remove what the host doesn't have (or has differently)
                foreach (var kv in have)
                {
                    if (want.TryGetValue(kv.Key, out var e) && e.Id == kv.Value.id && e.State == kv.Value.state) continue;
                    if (kv.Value.state == StatePlant && want.TryGetValue(kv.Key, out var pe) && pe.State == StatePlant) continue; // plant packet handles
                    try { kv.Value.go.DeleteObject(); changes++; }
                    catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] structure remove failed: {ex.Message}"); }
                }
                // 2) add what is missing
                foreach (var kv in want)
                {
                    var e = kv.Value;
                    if (have.TryGetValue(kv.Key, out var h) && h.id == e.Id && h.state == e.State) continue;
                    if (e.State == StatePlant) continue; // arrives as PlantLifecyclePacket
                    if (Create(e)) changes++;
                }
            }
            finally { Applying = false; }
            if (changes > 0)
            {
                FixedBlocks++;
                Debug.Log($"[ONIT-Colonies] Structure sync fixed block {p.Block} ({changes} changes)");
            }
        }

        private static long Key(int cell, int layer) => ((long)cell << 8) | (uint)layer;

        private static bool Create(StructureEntry e)
        {
            try
            {
                var def = Assets.GetBuildingDef(e.Id);
                if (def == null) return false;
                var orientation = (Orientation)e.Orientation;
                var tags = new List<Tag>();
                if (e.State == StateConstruction && e.Materials.Count > 0) foreach (var m in e.Materials) tags.Add(new Tag(m));
                else if (!string.IsNullOrEmpty(e.Element)) tags.Add(new Tag(e.Element));
                if (tags.Count == 0) tags.Add(SimHashes.SandStone.CreateTag());

                // clear whatever stale object sits on that layer/cell first
                var stale = Grid.Objects[e.Cell, e.Layer];
                if (stale != null) { stale.DeleteObject(); Grid.Objects[e.Cell, e.Layer] = null; }

                if (e.State == StateComplete)
                {
                    float temp = e.Temperature > 0f ? e.Temperature : def.Temperature;
                    var go = def.Build(e.Cell, orientation, null, tags, temp, e.Facade, playsound: false, GameClock.Instance.GetTime());
                    if (go != null && e.Connections != 0 && go.TryGetComponent<KAnimGraphTileVisualizer>(out var vis))
                    {
                        vis.UpdateConnections((UtilityConnections)e.Connections);
                        vis.Refresh();
                    }
                    return go != null;
                }
                if (e.State == StateConstruction)
                {
                    Vector3 pos = Grid.CellToPosCBC(e.Cell, Grid.SceneLayer.Building);
                    var preview = Util.KInstantiate(def.BuildingPreview, pos);
                    GameObject go = null;
                    try { go = def.TryPlace(preview, pos, orientation, tags, e.Facade); }
                    finally { if (preview != null) Util.KDestroyGameObject(preview); }
                    return go != null;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] structure create {e.Id} failed: {ex.Message}"); }
            return false;
        }

        #endregion
    }

    public class StructureEntry
    {
        public int Cell, Layer, Orientation, Connections;
        public byte State;
        public string Id = "", Element = "", Facade = "DEFAULT_FACADE";
        public float Temperature;
        public List<string> Materials = new List<string>();

        public void Write(BinaryWriter w)
        {
            w.Write(Cell); w.Write(Layer); w.Write(State); w.Write(Id ?? ""); w.Write(Orientation);
            w.Write(Element ?? ""); w.Write(Temperature); w.Write(Facade ?? "DEFAULT_FACADE"); w.Write(Connections);
            w.Write(Materials.Count); foreach (var m in Materials) w.Write(m ?? "");
        }

        public static StructureEntry Read(BinaryReader r)
        {
            var e = new StructureEntry
            {
                Cell = r.ReadInt32(), Layer = r.ReadInt32(), State = r.ReadByte(), Id = r.ReadString(), Orientation = r.ReadInt32(),
                Element = r.ReadString(), Temperature = r.ReadSingle(), Facade = r.ReadString(), Connections = r.ReadInt32()
            };
            int n = r.ReadInt32();
            for (int i = 0; i < n && i < 16; i++) e.Materials.Add(r.ReadString());
            return e;
        }
    }

    /// <summary>Host -> clients: per-block hashes of buildings / blueprints / plants.</summary>
    public class StructureDigestPacket : IPacket
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
        public void OnDispatched() => StructureSync.OnDigest(this);
    }

    /// <summary>Client -> host: send me the real contents of these blocks.</summary>
    public class StructureDetailRequestPacket : IPacket
    {
        public List<int> Blocks = new List<int>();
        public void Serialize(BinaryWriter w) { w.Write(Blocks.Count); foreach (var b in Blocks) w.Write(b); }
        public void Deserialize(BinaryReader r) { int n = r.ReadInt32(); Blocks = new List<int>(); for (int i = 0; i < n && i < 64; i++) Blocks.Add(r.ReadInt32()); }
        public void OnDispatched() => StructureSync.HostSendDetails(ColonyNet.CurrentSender, Blocks);
    }

    /// <summary>Host -> one client: authoritative contents of one block.</summary>
    public class StructureDetailPacket : IPacket
    {
        public int Block;
        public List<StructureEntry> Entries = new List<StructureEntry>();
        public void Serialize(BinaryWriter w) { w.Write(Block); w.Write(Entries.Count); foreach (var e in Entries) e.Write(w); }
        public void Deserialize(BinaryReader r)
        {
            Block = r.ReadInt32();
            int n = r.ReadInt32();
            Entries = new List<StructureEntry>(Math.Max(0, Math.Min(n, 4096)));
            for (int i = 0; i < n && i < 4096; i++) Entries.Add(StructureEntry.Read(r));
        }
        public void OnDispatched() => StructureSync.ApplyDetail(this);
    }
}
