using UnityEngine;
using ONI_Together.DebugTools;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World.Handlers
{
	/// <summary>
	/// Handles SingleEntityReceptacle buildings (planters, incubators, etc.).
	/// </summary>
	public class ReceptacleHandler : IBuildingConfigHandler
	{
		private static readonly int[] _hashes = new int[]
		{
			"ReceptacleEntityTag".GetHashCode(),
			"ReceptacleFilterTag".GetHashCode(),
			"ReceptacleCancelRequest".GetHashCode(),
			"IncubatorAutoReplace".GetHashCode(),
		};

		public int[] SupportedConfigHashes => _hashes;

		public bool TryApplyConfig(GameObject go, BuildingConfigPacket packet)
		{
			using var _ = Profiler.Scope();

			int hash = packet.ConfigHash;

			// Handle SingleEntityReceptacle
			var receptacle = go.GetComponent<SingleEntityReceptacle>();
			if (receptacle != null)
			{
				if (hash == "ReceptacleEntityTag".GetHashCode())
				{
					if (packet.ConfigType == BuildingConfigType.String)
					{
						Tag entityTag = string.IsNullOrEmpty(packet.StringValue) ? Tag.Invalid : new Tag(packet.StringValue);
						if (!MultiplayerSession.IsHost)
						{
							// client: CreateOrder is blocked here (no local fetch / preview), so just mirror the host's
							// request — otherwise the side screen keeps asking to choose an egg / seed
							ApplyClientRequest(receptacle, entityTag);
							return true;
						}
						if (entityTag.IsValid)
						{
							receptacle.CreateOrder(entityTag, Tag.Invalid);
							UnityEngine.Debug.Log($"[ONIT-Colonies] receptacle order {entityTag} on {go.name} at {packet.Cell} -> requested={receptacle.requestedEntityTag}");
							RelayToClients(go, packet.Cell, entityTag.Name);
							//DebugConsole.Log($"[ReceptacleHandler] Created order for {entityTag} on {go.name}");
						}
						else
						{
							receptacle.CancelActiveRequest();
							RelayToClients(go, packet.Cell, "");
							//DebugConsole.Log($"[ReceptacleHandler] Cancelled order on {go.name}");
						}
						return true;
					}
				}

				if (hash == "ReceptacleFilterTag".GetHashCode())
				{
					// Additional filter tag (for mutations, etc.) is handled together with entity tag
					return true;
				}

				if (hash == "ReceptacleCancelRequest".GetHashCode())
				{
					if (!MultiplayerSession.IsHost) { ApplyClientRequest(receptacle, Tag.Invalid); return true; }
					receptacle.CancelActiveRequest();
					RelayToClients(go, packet.Cell, "");
					//DebugConsole.Log($"[ReceptacleHandler] Cancelled request on {go.name}");
					return true;
				}
			}

			// Handle EggIncubator auto-replace
			var incubator = go.GetComponent<EggIncubator>();
			if (incubator != null && hash == "IncubatorAutoReplace".GetHashCode())
			{
				incubator.autoReplaceEntity = packet.Value > 0.5f;
				//DebugConsole.Log($"[ReceptacleHandler] Set autoReplaceEntity={incubator.autoReplaceEntity} on {go.name}");
				return true;
			}

			return false;
		}

		/// <summary>Host: tell every client (the requester too) what the receptacle now asks for.</summary>
		private static void RelayToClients(GameObject go, int cell, string tagName)
		{
			if (!MultiplayerSession.IsHost) return;
			var identity = go.GetComponent<Components.NetworkIdentity>();
			if (identity == null) return;
			var relay = new BuildingConfigPacket
			{
				NetId = identity.NetId,
				Cell = cell,
				ConfigHash = "ReceptacleEntityTag".GetHashCode(),
				Value = 0,
				ConfigType = BuildingConfigType.String,
				StringValue = tagName ?? ""
			};
			PacketSender.SendToAllClients(relay); // new packet: Sender = host, so the requester applies it too
		}

		private static void ApplyClientRequest(SingleEntityReceptacle receptacle, Tag tag)
		{
			try
			{
				receptacle.requestedEntityTag = tag;
				receptacle.requestedEntityAdditionalFilterTag = Tag.Invalid;
				receptacle.UpdateStatusItem();
			}
			catch (System.Exception ex) { UnityEngine.Debug.LogWarning($"[ONIT-Colonies] receptacle mirror: {ex.Message}"); }
		}
	}
}
