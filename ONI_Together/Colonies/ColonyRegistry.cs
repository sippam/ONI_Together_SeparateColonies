using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Runtime state for "separate colonies on one asteroid" mode.
    ///
    /// Ownership is keyed by KPrefabID.InstanceID (a [Serialize] field, stable across save/load).
    /// Maps are ConcurrentDictionary because some checks run inside ONI's GlobalJobManager worker
    /// threads (e.g. Pickupable.CouldBePickedUpByMinion during FetchManager.UpdatePickups).
    /// </summary>
    public static class ColonyRegistry
    {
        public const int NoColony = 0;

        /// <summary>Master switch. Saved in the save file. Only meaningful in a multiplayer session.</summary>
        public static bool Enabled { get; private set; }

        public static readonly List<ColonyInfo> Colonies = new List<ColonyInfo>();

        // InstanceID -> colony
        private static readonly ConcurrentDictionary<int, int> _ownerById = new ConcurrentDictionary<int, int>();
        // GameObject.GetInstanceID() -> colony. Session-only cache so hot paths (chore preconditions)
        // skip GetComponent<KPrefabID>(). Kept in sync by SetOwner; filled lazily on miss.
        private static readonly ConcurrentDictionary<int, int> _ownerByGo = new ConcurrentDictionary<int, int>();
        // Storage component -> colony (reference identity, safe to read off the main thread)
        private static readonly ConcurrentDictionary<Storage, int> _ownerByStorage =
            new ConcurrentDictionary<Storage, int>(new RefComparer<Storage>());

        /// <summary>Radius (cells) around a colony's HomeCell where other colonies may not build/dig. 0 = off.</summary>
        public static int TerritoryRadius = 14;

        public static event System.Action OnColoniesChanged;

        #region Session / players

        public static ulong LocalPlayerId
        {
            get
            {
                try { return MultiplayerSession.LocalUserID; }
                catch { return 0; }
            }
        }

        /// <summary>True when the colony system should actively filter/guard things.</summary>
        public static bool Active => Enabled && Colonies.Count > 0;

        public static int LocalColonyId => GetColonyOfPlayer(LocalPlayerId);

        public static ColonyInfo LocalColony => Get(LocalColonyId);

        public static int GetColonyOfPlayer(ulong playerId)
        {
            if (playerId == 0) return NoColony;
            for (int i = 0; i < Colonies.Count; i++)
                if (Colonies[i].OwnerPlayerId == playerId) return Colonies[i].Id;
            return NoColony;
        }

        public static ColonyInfo Get(int colonyId)
        {
            if (colonyId == NoColony) return null;
            for (int i = 0; i < Colonies.Count; i++)
                if (Colonies[i].Id == colonyId) return Colonies[i];
            return null;
        }

        public static int NextColonyId()
        {
            int max = 0;
            foreach (var c in Colonies) max = Math.Max(max, c.Id);
            return max + 1;
        }

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            Debug.Log($"[ONIT-Colonies] Separate colonies mode: {(enabled ? "ON" : "OFF")}");
            RaiseChanged();
        }

        public static void AddColony(ColonyInfo info)
        {
            if (info == null || Get(info.Id) != null) return;
            Colonies.Add(info);
            Colonies.Sort((a, b) => a.Id.CompareTo(b.Id));
            Debug.Log($"[ONIT-Colonies] Colony #{info.Id} '{info.Name}' owner={info.OwnerName}({info.OwnerPlayerId}) home={info.HomeCell}");
            RaiseChanged();
        }

        public static void RaiseChanged()
        {
            try { OnColoniesChanged?.Invoke(); } catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] OnColoniesChanged: {ex.Message}"); }
        }

        #endregion

        #region Ownership

        public static int GetOwner(int instanceId)
        {
            return _ownerById.TryGetValue(instanceId, out int c) ? c : NoColony;
        }

        public static int GetOwner(KPrefabID id) => id == null ? NoColony : GetOwner(id.InstanceID);

        /// <summary>Main thread only (uses GetComponent).</summary>
        public static int GetOwner(GameObject go)
        {
            if (go == null) return NoColony;
            int goId = go.GetInstanceID();
            if (_ownerByGo.TryGetValue(goId, out int cached)) return cached;
            var id = go.GetComponent<KPrefabID>();
            int owner = id == null ? NoColony : GetOwner(id.InstanceID);
            _ownerByGo[goId] = owner;
            return owner;
        }

        /// <summary>Thread-safe.</summary>
        public static int GetOwner(Storage storage)
        {
            if ((object)storage == null) return NoColony;
            return _ownerByStorage.TryGetValue(storage, out int c) ? c : NoColony;
        }

        /// <summary>Assign an object to a colony (main thread). colony 0 removes ownership.</summary>
        public static void SetOwner(GameObject go, int colonyId)
        {
            if (go == null) return;
            var id = go.GetComponent<KPrefabID>();
            if (id == null || id.InstanceID == KPrefabID.InvalidInstanceID) return;

            if (colonyId == NoColony) _ownerById.TryRemove(id.InstanceID, out _);
            else _ownerById[id.InstanceID] = colonyId;
            _ownerByGo[go.GetInstanceID()] = colonyId;

            ColonyNet.NotifyOwnerChanged(go, colonyId);
            if (colonyId != NoColony && go.GetComponent<MinionIdentity>() != null)
                ColonySchedules.EnsureDupeOnOwnSchedule(go, colonyId);
            RegisterStorages(go, colonyId);
        }

        /// <summary>Re-index Storage components of an already owned object (call from OnSpawn hooks).</summary>
        public static void RefreshStorages(GameObject go)
        {
            if (go == null) return;
            int owner = GetOwner(go);
            if (owner != NoColony) RegisterStorages(go, owner);
        }

        private static void RegisterStorages(GameObject go, int colonyId)
        {
            var storages = go.GetComponents<Storage>();
            if (storages == null) return;
            foreach (var s in storages)
            {
                if (s == null) continue;
                if (colonyId == NoColony) _ownerByStorage.TryRemove(s, out _);
                else _ownerByStorage[s] = colonyId;
            }
        }

        public static IEnumerable<Storage> GetStoragesOf(int colonyId)
        {
            var dead = new List<Storage>();
            foreach (var kv in _ownerByStorage)
            {
                if (kv.Key == null) { dead.Add(kv.Key); continue; }
                if (kv.Value == colonyId) yield return kv.Key;
            }
            foreach (var d in dead) _ownerByStorage.TryRemove(d, out _);
        }

        public static Dictionary<int, int> SnapshotOwnership(bool pruneDead)
        {
            var result = new Dictionary<int, int>();
            KPrefabIDTracker tracker = null;
            try { tracker = KPrefabIDTracker.Get(); } catch { }
            foreach (var kv in _ownerById)
            {
                if (pruneDead && tracker != null && tracker.GetInstance(kv.Key) == null)
                {
                    _ownerById.TryRemove(kv.Key, out _);
                    continue;
                }
                result[kv.Key] = kv.Value;
            }
            return result;
        }

        public static int CountOwned(int colonyId)
        {
            int n = 0;
            foreach (var kv in _ownerById) if (kv.Value == colonyId) n++;
            return n;
        }

        #endregion

        #region Territory

        public static int GetTerritoryOwner(int cell)
        {
            if (TerritoryRadius <= 0 || !Grid.IsValidCell(cell)) return NoColony;
            Grid.CellToXY(cell, out int x, out int y);
            int best = NoColony;
            int bestDist = int.MaxValue;
            foreach (var c in Colonies)
            {
                if (!Grid.IsValidCell(c.HomeCell)) continue;
                Grid.CellToXY(c.HomeCell, out int hx, out int hy);
                int dx = x - hx, dy = y - hy;
                int d2 = dx * dx + dy * dy;
                if (d2 <= TerritoryRadius * TerritoryRadius && d2 < bestDist)
                {
                    bestDist = d2;
                    best = c.Id;
                }
            }
            return best;
        }

        /// <summary>True if the cell is inside another colony's protected territory.</summary>
        public static bool IsForeignTerritory(int cell, int colonyId)
        {
            if (!ColonyRules.TerritoryProtection) return false;
            int t = GetTerritoryOwner(cell);
            return t != NoColony && t != colonyId;
        }

        #endregion

        #region Save / reset

        internal static void LoadFromSave(bool enabled, List<ColonyInfo> colonies, Dictionary<int, int> owned, int territoryRadius)
        {
            Reset();
            Enabled = enabled;
            if (colonies != null) Colonies.AddRange(colonies);
            Colonies.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (owned != null)
                foreach (var kv in owned) _ownerById[kv.Key] = kv.Value;
            _ownerByGo.Clear();
            if (territoryRadius > 0) TerritoryRadius = territoryRadius;
            RaiseChanged();
        }

        public static void Reset()
        {
            Enabled = false;
            Colonies.Clear();
            _ownerById.Clear();
            _ownerByGo.Clear();
            _ownerByStorage.Clear();
        }

        /// <summary>Client: replace the colony list from the host (ownership maps are left alone).</summary>
        internal static void ApplyRemote(bool enabled, int territoryRadius, List<ColonyInfo> colonies)
        {
            Enabled = enabled;
            if (territoryRadius > 0) TerritoryRadius = territoryRadius;
            Colonies.Clear();
            if (colonies != null) Colonies.AddRange(colonies);
            Colonies.Sort((a, b) => a.Id.CompareTo(b.Id));
            RaiseChanged();
        }

        #endregion

        private sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
