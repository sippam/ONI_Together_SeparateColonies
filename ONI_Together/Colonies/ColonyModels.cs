using System;
using System.Collections.Generic;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// One player-owned colony on the shared asteroid.
    /// Plain data — stored as JSON in ColonySaveData and sent in ColonyRegistryPacket.
    /// </summary>
    [Serializable]
    public class ColonyInfo
    {
        /// <summary>1-based. 0 = "no colony / world-owned".</summary>
        public int Id;
        public string Name = "";
        /// <summary>MultiplayerSession player id (SteamID on Steam, connection id on LAN) of the owner.</summary>
        public ulong OwnerPlayerId;
        public string OwnerName = "";
        /// <summary>Cell of the colony's Printing Pod when it was founded (territory center).</summary>
        public int HomeCell = -1;
        public float ColorR = 1f, ColorG = 1f, ColorB = 1f;

        /// <summary>Per-colony research tree (only used when ColonyRules.SeparateResearch). Host-authoritative.</summary>
        public ResearchProfile Research = new ResearchProfile();

        /// <summary>Per-colony Printing Pod timer (only used when ColonyRules.SeparatePrintingPod). Host-authoritative.</summary>
        public ImmigrationProfile Immigration = new ImmigrationProfile();

        /// <summary>Whose errands this colony's dupes do first (colony ids, best first). Empty = own first, then founding order.</summary>
        public List<int> WorkOrder = new List<int>();
    }

    /// <summary>Copy of Immigration's timer fields.</summary>
    [Serializable]
    public class ImmigrationProfile
    {
        public float TimeBeforeSpawn = float.PositiveInfinity;
        public bool ImmigrantAvailable;
        public int SpawnIdx;
        public bool Initialized;
    }

    /// <summary>Copy of a research tree state (mirrors Research / TechInstance save data).</summary>
    [Serializable]
    public class ResearchProfile
    {
        /// <summary>Last tech in the queue (what the player clicked). "" = none.</summary>
        public string TargetTechId = "";
        public List<string> CompletedTechIds = new List<string>();
        /// <summary>techId -> (researchTypeId -> points)</summary>
        public Dictionary<string, Dictionary<string, float>> Progress = new Dictionary<string, Dictionary<string, float>>();
        public bool Initialized;
    }

    /// <summary>
    /// Host-decided rules for separate colonies (from the host's mod options; sent to clients in the registry).
    /// </summary>
    [Serializable]
    public class ColonyRulesData
    {
        /// <summary>Other colonies can't build/dig inside a colony's territory circle.</summary>
        public bool TerritoryProtection = true;
        /// <summary>Other colonies can't give orders to (deconstruct, prioritize, configure...) your buildings/dupes.</summary>
        public bool BuildingProtection = true;
        /// <summary>Dupes only take errands of their own colony and don't fetch from other colonies' storage.</summary>
        public bool SeparateErrands = true;
        /// <summary>Each colony has its own research tree.</summary>
        public bool SeparateResearch = true;
        /// <summary>Jobs/priorities, consumables, vitals and skills screens show and edit every colony's dupes.</summary>
        public bool SharedDupeManagement = false;
        /// <summary>Each colony has its own Printing Pod timer and its own printable choices.</summary>
        public bool SeparatePrintingPod = true;
        /// <summary>Each colony has its own schedules; dupes can only be put on their colony's schedules.</summary>
        public bool SeparateSchedules = true;
        /// <summary>With separate errands: dupes do their own colony's work first and help other colonies only when they have nothing of their own to do.</summary>
        public bool HelpOtherColonies = true;
        /// <summary>The cancel tool only removes your own colony's orders (dig/mop/build/deconstruct markers), even when building protection is off.</summary>
        public bool CancelOnlyOwnOrders = true;
        /// <summary>Buildings, machines, storage and food are shared between colonies; only commanding dupes stays separate.</summary>
        public bool SharedResources = false;
        /// <summary>New starting / printed dupes get the colony name as a prefix.</summary>
        public bool PrefixDupeNames = true;
    }
}
