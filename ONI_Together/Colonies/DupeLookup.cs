using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Duplicant lookup for packets. ONI Together addresses dupes by NetworkIdentity.NetId, but those can differ
    /// between host and client (dupes printed/spawned between hard syncs) — then priority/consumable changes were
    /// silently dropped ("NetId not found") and the host's value came back. Fall back to the dupe's name.
    /// </summary>
    public static class DupeLookup
    {
        public static GameObject Find(int netId, string name)
        {
            if (netId != 0 && NetworkIdentityRegistry.TryGet(netId, out var ni) && ni != null) return ni.gameObject;
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                var id = mi.GetComponent<NetworkIdentity>();
                if (netId != 0 && id != null && id.NetId == netId) return mi.gameObject;
            }
            if (string.IsNullOrEmpty(name)) return null;
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
                if (mi != null && mi.GetProperName() == name) return mi.gameObject;
            return null;
        }
    }
}
