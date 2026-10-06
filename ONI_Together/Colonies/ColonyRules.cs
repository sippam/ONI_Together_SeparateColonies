using System;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// The rules in effect for this session. The host takes them from its mod options (and the in-game panel),
    /// clients get them from the host in ColonyRegistryPacket, single-player loads read them from the save.
    /// </summary>
    public static class ColonyRules
    {
        public static ColonyRulesData Data = new ColonyRulesData();

        public static bool TerritoryProtection => Data.TerritoryProtection;
        public static bool BuildingProtection => Data.BuildingProtection;
        public static bool SeparateErrands => Data.SeparateErrands;
        public static bool SeparateResearch => Data.SeparateResearch;
        public static bool SharedDupeManagement => Data.SharedDupeManagement;
        public static bool SeparatePrintingPod => Data.SeparatePrintingPod;
        public static bool SeparateSchedules => Data.SeparateSchedules;
        public static bool HelpOtherColonies => Data.HelpOtherColonies;
        public static bool CancelOnlyOwnOrders => Data.CancelOnlyOwnOrders;

        /// <summary>Host: read the rules from the mod options.</summary>
        public static ColonyRulesData FromConfig()
        {
            var c = Configuration.Instance?.Colonies;
            if (c == null) return new ColonyRulesData();
            return new ColonyRulesData
            {
                TerritoryProtection = c.TerritoryProtection,
                BuildingProtection = c.BuildingProtection,
                SeparateErrands = c.SeparateErrands,
                SeparateResearch = c.SeparateResearch,
                SharedDupeManagement = c.SharedDupeManagement,
                SeparatePrintingPod = c.SeparatePrintingPod,
                SeparateSchedules = c.SeparateSchedules,
                HelpOtherColonies = c.HelpOtherColonies,
                CancelOnlyOwnOrders = c.CancelOnlyOwnOrders,
            };
        }

        /// <summary>Host: write the current rules back to the mod options file.</summary>
        public static void SaveToConfig()
        {
            try
            {
                var c = Configuration.Instance?.Colonies;
                if (c == null) return;
                c.TerritoryProtection = Data.TerritoryProtection;
                c.BuildingProtection = Data.BuildingProtection;
                c.SeparateErrands = Data.SeparateErrands;
                c.SeparateResearch = Data.SeparateResearch;
                c.SharedDupeManagement = Data.SharedDupeManagement;
                c.SeparatePrintingPod = Data.SeparatePrintingPod;
                c.SeparateSchedules = Data.SeparateSchedules;
                c.HelpOtherColonies = Data.HelpOtherColonies;
                c.CancelOnlyOwnOrders = Data.CancelOnlyOwnOrders;
                Configuration.Instance.Save();
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] Saving rules to config failed: {ex.Message}"); }
        }

        /// <summary>Replace the rules (host: from config / panel, client: from the host). Handles research mode switches.</summary>
        public static void Apply(ColonyRulesData data)
        {
            if (data == null) return;
            bool researchWas = Data.SeparateResearch;
            bool podWas = Data.SeparatePrintingPod;
            bool schedWas = Data.SeparateSchedules;
            Data = data;
            if (researchWas != data.SeparateResearch)
                ColonyResearch.OnModeChanged(data.SeparateResearch);
            if (podWas != data.SeparatePrintingPod)
                ColonyImmigration.OnModeChanged(data.SeparatePrintingPod);
            if (schedWas != data.SeparateSchedules)
                ColonySchedules.OnModeChanged(data.SeparateSchedules);
            ColonyRegistry.RaiseChanged();
        }

        public static string Describe() =>
            $"territory={(TerritoryProtection ? "on" : "off")} buildings={(BuildingProtection ? "protected" : "open")} " +
            $"errands={(SeparateErrands ? "separate" : "shared")} research={(SeparateResearch ? "separate" : "shared")} " +
            $"dupeManagement={(SharedDupeManagement ? "shared" : "separate")} pod={(SeparatePrintingPod ? "separate" : "shared")} " +
            $"schedules={(SeparateSchedules ? "separate" : "shared")} help={(HelpOtherColonies ? "when idle" : "never")} cancel={(CancelOnlyOwnOrders ? "own" : "any")}";
    }
}
