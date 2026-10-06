using System;

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
    }
}
