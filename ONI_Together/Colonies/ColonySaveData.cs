using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using KSerialization;
using Newtonsoft.Json;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Persists the separate-colonies state inside the save file (attached to the SaveGame object).
    /// Stored as ONE JSON string. Clients receive it as part of the host's save (join / hard sync), so the
    /// KPrefabID.InstanceID based ownership map is identical on every machine right after a load.
    /// If the mod is removed, SaveLoadRoot skips the unknown component — the save still loads.
    /// </summary>
    [SerializationConfig(KSerialization.MemberSerialization.OptIn)]
    public class ColonySaveData : KMonoBehaviour
    {
        public static ColonySaveData Instance { get; private set; }

        /// <summary>Extra water geysers were already handled for this world.</summary>
        public static bool ExtraWaterDone;

        /// <summary>Water geysers already placed near the host's base in this save.</summary>
        public static int HostWaterPlaced;

        [Serialize] private string json = "";

        [Serializable]
        private class Payload
        {
            public int Version = 1;
            public bool Enabled;
            public int TerritoryRadius;
            public List<ColonyInfo> Colonies = new List<ColonyInfo>();
            public List<int> OwnedIds = new List<int>();
            public List<int> OwnedColonies = new List<int>();
            public ColonyRulesData Rules;
            public List<int> ScheduleOwners;
            public bool ExtraWaterDone;
            public int HostWaterPlaced;
            /// <summary>2 = steam vent POIs (v0.9.4+). Older saves counted plain water geysers: they get a steam vent too.</summary>
            public int HostWaterStyle;
        }

        public override void OnPrefabInit()
        {
            base.OnPrefabInit();
            Instance = this;
            // A new SaveGame object = a new game/load. Forget the previous game's colonies;
            // OnDeserialized (if this is a loaded save) fills them back in.
            ColonyRegistry.Reset();
            ColonySchedules.Reset();
            ExtraWaterDone = false;
            HostWaterPlaced = 0;
        }

        public override void OnCleanUp()
        {
            if (Instance == this) Instance = null;
            base.OnCleanUp();
        }

        [OnSerializing]
        private void OnSerializing()
        {
            try
            {
                var p = new Payload
                {
                    Enabled = ColonyRegistry.Enabled,
                    TerritoryRadius = ColonyRegistry.TerritoryRadius,
                    Colonies = new List<ColonyInfo>(ColonyRegistry.Colonies),
                    Rules = ColonyRules.Data,
                    ScheduleOwners = ColonySchedules.Snapshot(),
                    ExtraWaterDone = ExtraWaterDone,
                    HostWaterPlaced = HostWaterPlaced,
                    HostWaterStyle = 2,
                };
                foreach (var kv in ColonyRegistry.SnapshotOwnership(pruneDead: true))
                {
                    p.OwnedIds.Add(kv.Key);
                    p.OwnedColonies.Add(kv.Value);
                }
                json = JsonConvert.SerializeObject(p);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] Save failed: {ex}");
            }
        }

        [OnDeserialized]
        private void OnDeserialized()
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var p = JsonConvert.DeserializeObject<Payload>(json);
                if (p == null) return;
                var owned = new Dictionary<int, int>();
                for (int i = 0; i < p.OwnedIds.Count && i < p.OwnedColonies.Count; i++)
                    owned[p.OwnedIds[i]] = p.OwnedColonies[i];
                ColonyRegistry.LoadFromSave(p.Enabled, p.Colonies, owned, p.TerritoryRadius);
                if (p.Rules != null) ColonyRules.Data = p.Rules;
                ColonySchedules.LoadFromSave(p.ScheduleOwners);
                ExtraWaterDone = p.ExtraWaterDone;
                HostWaterPlaced = p.HostWaterStyle >= 2 ? p.HostWaterPlaced : 0;
                Debug.Log($"[ONIT-Colonies] Loaded {p.Colonies.Count} colonies, {owned.Count} owned objects (enabled={p.Enabled})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] Load failed: {ex}");
            }
        }
    }
}
