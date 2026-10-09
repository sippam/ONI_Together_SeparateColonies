using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Packets.Core
{
    public class EntityPositionRequestPacket : IPacket
    {
        public ulong RequesterId;
        public int NetId;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(RequesterId);
            writer.Write(NetId);
        }

        public void Deserialize(BinaryReader reader)
        {
            RequesterId = reader.ReadUInt64();
            NetId = reader.ReadInt32();
        }

        public void OnDispatched()
        {
            if (!MultiplayerSession.IsHost) return;

            if(!NetworkIdentityRegistry.TryGet(NetId, out var identity))
                return;

            if (!identity.TryGetComponent<EntityPositionHandler>(out var handler))
                return;

            var packet = new EntityPositionPacket
            {
                NetId = NetId,
                Position = handler.transform.position,
                FlipX = handler.kbac?.FlipX ?? false,
                FlipY = handler.kbac?.FlipY ?? false,
                NavType = handler.navigator?.CurrentNavType ?? NavType.Floor,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            // the client never filled RequesterId, so every answer went to "SteamID 0" (dropped, ~150k warnings per
            // session in host logs) and frozen critters/dupes were never corrected: use the transport's sender
            ulong to = RequesterId != 0 ? RequesterId : ONI_Together.Colonies.ColonyNet.CurrentSender;
            if (to == 0) return;
            PacketSender.SendToPlayer(to, packet, PacketSendMode.ReliableImmediate);
        }
    }
}
