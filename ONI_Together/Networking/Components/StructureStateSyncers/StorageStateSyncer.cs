using System;
using System.Collections.Generic;
using System.Text;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
    public class StorageStateSyncer : StructureSyncerBase
    {
        private Storage storage;
        public Storage SyncedStorage => storage;

        /// <summary>Every live storage syncer (used by the colonies background storage sync).</summary>
        public static readonly HashSet<StorageStateSyncer> All = new HashSet<StorageStateSyncer>();
        private float temperatureThreshold = 0.3f;
        private float lastStorageTemperature;

        public struct StorageData
        {
            public int PrefabTagHash;
            public float Mass;
            public float Units;
            public float Temperature;
            public byte DiseaseIdx;
            public int DiseaseCount;
        }

        protected override void Initialize()
        {
            storage = GetComponent<Storage>();
            All.Add(this);
            checkOptionalsValuesForChanges = false; // Skip checking optionals for changes since we check temperature in ShouldForceSync and mass is checked via value
        }


        protected override void SampleState(out Variant value, out bool active, out Dictionary<string, Variant> optionalValues)
        {
            value = storage?.MassStored() ?? 0f;
            active = false;
            optionalValues = new Dictionary<string, Variant>();
            BuildingUtils.EncodeStorageContents(storage, optionalValues);
        }

        protected override void ApplyState(StructureStatePacket packet)
        {
            if (storage == null || packet.OptionalValues.Count < 1) return;
            BuildingUtils.RebuildStorageFromData(storage, packet.OptionalValues);
            // the rebuild stores with block_events, so meters / side screens never heard about it
            try { storage.Trigger((int)GameHashes.OnStorageChange, null); } catch (Exception) { }
        }

        public override void OnCleanUp()
        {
            All.Remove(this);
            base.OnCleanUp();
        }

        protected override bool ShouldForceSync()
        {
            if (storage == null) return false;

            float currentTemp = GetMaxStorageTemperature(storage);
            if (Mathf.Abs(currentTemp - lastStorageTemperature) > temperatureThreshold)
            {
                lastStorageTemperature = currentTemp;
                return true;
            }
            return false;
        }

        // TODO: Does not scale well in the late game
        private float GetMaxStorageTemperature(Storage storage)
        {
            float max = 0f;
            for (int i = 0; i < storage.items.Count; i++)
            {
                var pe = storage.items[i]?.GetComponent<PrimaryElement>();
                if (pe != null && pe.Temperature > max)
                    max = pe.Temperature;
            }

            return max;
        }
    }
}
