using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;
using Shared.Interfaces.Networking;

public class EntityPositionPacket : IPacket, IViewportCullable
{
	public int NetId;
	public Vector3 Position;
	public bool FlipX;
	public bool FlipY;
	public NavType NavType;
	public long Timestamp;

    public int GetViewportCell()
    {
		var cell = Grid.PosToCell(Position);
		return cell;
    }
	
    public void Serialize(BinaryWriter writer)
	{
		using var _ = Profiler.Scope();

		writer.Write(NetId);
		writer.Write(Position);
		writer.Write(FlipX);
		writer.Write(FlipY);
		writer.Write((byte)NavType);
		writer.Write(Timestamp);
	}

	public void Deserialize(BinaryReader reader)
	{
		using var _ = Profiler.Scope();

		NetId = reader.ReadInt32();
		Position = reader.ReadVector3();
		FlipX = reader.ReadBoolean();
		FlipY = reader.ReadBoolean();
		NavType = (NavType)reader.ReadByte();
		Timestamp = reader.ReadInt64();
	}

	public void OnDispatched()
	{
		using var _ = Profiler.Scope();
		if (MultiplayerSession.IsHost) return;
		Apply(NetId, Position, FlipX, FlipY, NavType, Timestamp, 0);
	}

	/// <summary>Client: apply one host position (single packet or batch entry).</summary>
	public static void Apply(int netId, Vector3 position, bool flipX, bool flipY, NavType navType, long timestamp, int prefabHash)
	{
		EntityPositionHandler handler = null;
		if (NetworkIdentityRegistry.TryGet(netId, out var entity) && entity != null)
			handler = entity.GetComponent<EntityPositionHandler>();
		else if (prefabHash != 0)
			handler = EntityPositionHandler.TryAdopt(netId, prefabHash, position); // NetIds of critters differ per machine
		if (!handler)
		{
			EntityPositionHandler.NoteMissing(netId);
			return;
		}

		if (handler.serverTimestamp > timestamp)
			return;

		if (float.IsNaN(position.z)) position.z = handler.transform.position.z; // batch entries carry x/y only
		handler.lastReceivedAt = Time.unscaledTime;
		handler.serverPosition = position;
		handler.serverTimestamp = timestamp;
		handler.serverFlipX = flipX;
		handler.serverFlipY = flipY;
		handler.serverNavType = navType;
	}
}
