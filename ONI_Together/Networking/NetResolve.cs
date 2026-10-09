using System;
using UnityEngine;

namespace ONI_Together.Networking
{
    /// <summary>
    /// Fallback lookups when a NetId sent by another machine isn't known locally. Building NetIds are derived from
    /// position/element/mass and can differ between host and clients (or after a rebuild), so commands such as
    /// cancel / deconstruct / assign / config were dropped on the host ("NetId ... not found").
    /// </summary>
    public static class NetResolve
    {
        /// <summary>First object at the cell, on any object layer, that matches (prefab hash 0 = any prefab).</summary>
        public static GameObject AtCell(int cell, int prefabHash, Func<GameObject, bool> accept)
        {
            if (!Grid.IsValidCell(cell)) return null;
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[cell, layer];
                if (go == null) continue;
                if (prefabHash != 0)
                {
                    var kpid = go.GetComponent<KPrefabID>();
                    if (kpid == null || kpid.PrefabTag.GetHash() != prefabHash) continue;
                }
                if (accept == null || accept(go)) return go;
            }
            return null;
        }

        /// <summary>
        /// Client only: take over the sender's NetId so later packets resolve directly. The host never renumbers its
        /// own objects (it is the authority — renumbering made other clients' ids stale).
        /// </summary>
        public static void AdoptIfClient(GameObject go, int netId)
        {
            if (go == null || netId == 0 || MultiplayerSession.IsHost) return;
            var id = go.GetComponent<Components.NetworkIdentity>();
            if (id == null) { id = go.AddOrGet<Components.NetworkIdentity>(); id.NetId = netId; id.RegisterIdentity(); }
            else if (id.NetId != netId) id.OverrideNetId(netId);
        }
    }
}
