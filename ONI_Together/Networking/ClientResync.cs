using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Networking
{
    /// <summary>
    /// "Sync my world" for a joined player: the same full world transfer as the host's hard sync, but only for the
    /// player who asked. Nobody else is paused or sees a sync screen, and the host's once-per-cycle hard sync is not used.
    ///
    /// Before, the sync button in the lobby window ran the HOST hard sync on the client: it paused the client, showed the
    /// overlay, tried to broadcast to "all clients" (nothing happens on a client), so it never finished, and the client's
    /// "already synced this cycle" flag was never reset because only the host resets it on a new cycle.
    /// </summary>
    public static class ClientResync
    {
        /// <summary>Client: a self-requested sync is running (reload of the host's world).</summary>
        public static bool Active { get; private set; }
        private static float _startedAt;
        private static float _cooldownUntil;

        private const float Timeout = 300f;   // seconds without finishing -> give up
        private const float Cooldown = 60f;   // between two requests of the same player
        private static readonly System.Collections.Generic.Dictionary<ulong, float> _hostLastServed = new System.Collections.Generic.Dictionary<ulong, float>();

        public static bool CanRequest => MultiplayerSession.IsClient && !Active && !GameClient.IsHardSyncInProgress && Time.realtimeSinceStartup >= _cooldownUntil;
        public static int CooldownLeft => Mathf.Max(0, Mathf.CeilToInt(_cooldownUntil - Time.realtimeSinceStartup));

        /// <summary>Client: ask the host for its current world.</summary>
        public static void Request()
        {
            if (!CanRequest) return;
            Active = true;
            _startedAt = Time.realtimeSinceStartup;
            _cooldownUntil = _startedAt + Cooldown;
            // lets SaveFileChunkPacket accept the transfer while in game and load it in place (same path as hard sync)
            GameClient.IsHardSyncInProgress = true;
            MultiplayerOverlay.Show(STRINGS.UI.MP_OVERLAY.SYNC.HARDSYNC_INPROGRESS);
            PacketSender.SendToHost(new ClientResyncRequestPacket { Requester = MultiplayerSession.LocalUserID });
            DebugConsole.Log("[ClientResync] requested the host's world");
        }

        /// <summary>Client: the reloaded world is running again (GameClient.ContinueConnectionFlow, in-game branch).</summary>
        public static void Finished()
        {
            if (!Active) return;
            Active = false;
            DebugConsole.Log($"[ClientResync] done in {Time.realtimeSinceStartup - _startedAt:0.0}s");
        }

        /// <summary>Called every frame (ColonySession): give up if the host never answered.</summary>
        public static void Tick()
        {
            if (!Active) return;
            if (!MultiplayerSession.InSession) { Active = false; return; }
            if (Time.realtimeSinceStartup - _startedAt < Timeout) return;
            Active = false;
            GameClient.IsHardSyncInProgress = false;
            MultiplayerOverlay.Close();
            DebugConsole.LogWarning("[ClientResync] timed out — the host did not send its world");
        }

        /// <summary>Host: send the world to that one player, without pausing or overlays for anyone.</summary>
        internal static void HostServe(ulong requester)
        {
            if (!MultiplayerSession.IsHost || requester == 0) return;
            if (GameServerHardSync.IsHardSyncInProgress) return; // a full hard sync is already sending it
            float now = Time.realtimeSinceStartup;
            if (_hostLastServed.TryGetValue(requester, out float last) && now - last < Cooldown * 0.5f)
            {
                DebugConsole.LogWarning($"[ClientResync] ignoring repeated request from {requester}");
                return;
            }
            _hostLastServed[requester] = now;
            DebugConsole.Log($"[ClientResync] sending the world to {requester}");
            SaveFileRequestPacket.SendSaveFile(requester);
        }
    }

    /// <summary>Client -> host: "send me your world" (personal sync).</summary>
    public class ClientResyncRequestPacket : IPacket
    {
        public ulong Requester;
        public void Serialize(BinaryWriter w) { w.Write(Requester); }
        public void Deserialize(BinaryReader r) { Requester = r.ReadUInt64(); }
        public void OnDispatched()
        {
            if (!MultiplayerSession.IsHost) return;
            ulong who = Requester != 0 ? Requester : ONI_Together.Colonies.ColonyNet.CurrentSender;
            ClientResync.HostServe(who);
        }
    }
}
