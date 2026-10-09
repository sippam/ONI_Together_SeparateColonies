using HarmonyLib;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Client: selecting something (tile, pipe, wire, blueprint, plant, storage...) asks the host for that area's
    /// buildings and that storage's contents right away, instead of waiting for the next background round.
    /// </summary>
    [HarmonyPatch(typeof(SelectTool), nameof(SelectTool.Select))]
    public static class SelectResync
    {
        public static void Postfix(KSelectable new_selected)
        {
            if (new_selected == null || !MultiplayerSession.InSession || MultiplayerSession.IsHost) return;
            try
            {
                var go = new_selected.gameObject;
                int cell = Grid.PosToCell(go);
                StructureSync.RequestCellNow(cell);
                StorageSync.RequestNow(go);
            }
            catch (System.Exception ex) { Debug.LogWarning($"[ONIT-Colonies] select resync: {ex.Message}"); }
        }
    }
}
