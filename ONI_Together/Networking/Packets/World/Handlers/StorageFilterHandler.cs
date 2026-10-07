using UnityEngine;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World.Handlers
{
	/// <summary>
	/// Handles TreeFilterable buildings (storage bins, refrigerators, critter buildings).
	/// </summary>
	public class StorageFilterHandler : IBuildingConfigHandler
	{
		private static readonly int[] _hashes = new int[]
		{
			"StorageFilterAdd".GetHashCode(),
			"StorageFilterRemove".GetHashCode(),
			"StorageFilterSet".GetHashCode(),
			"StorageSweepOnly".GetHashCode(),
		};

		public int[] SupportedConfigHashes => _hashes;

		public static string Encode(IEnumerable<Tag> tags)
		{
			var names = new List<string>();
			if (tags != null) foreach (var t in tags) names.Add(t.Name);
			return string.Join("|", names);
		}

		public static HashSet<Tag> Decode(string s)
		{
			var set = new HashSet<Tag>();
			if (string.IsNullOrEmpty(s)) return set;
			foreach (var n in s.Split('|')) if (n.Length > 0) set.Add(new Tag(n));
			return set;
		}

		public bool TryApplyConfig(GameObject go, BuildingConfigPacket packet)
		{
			using var _ = Profiler.Scope();

			int hash = packet.ConfigHash;

			// Handle TreeFilterable
			var treeFilterable = go.GetComponent<TreeFilterable>();
			if (treeFilterable != null)
			{
				if (hash == "StorageFilterAdd".GetHashCode())
				{
					if (packet.ConfigType == BuildingConfigType.String && !string.IsNullOrEmpty(packet.StringValue))
					{
						Tag tag = new Tag(packet.StringValue);
						treeFilterable.AddTagToFilter(tag);
						//DebugConsole.Log($"[StorageFilterHandler] Added filter tag {tag} on {go.name}");
						return true;
					}
				}

				if (hash == "StorageFilterSet".GetHashCode() && packet.ConfigType == BuildingConfigType.String)
				{
					var set = Decode(packet.StringValue);
					if (!set.SetEquals(treeFilterable.AcceptedTags))
						treeFilterable.UpdateFilters(set);
					return true;
				}

				if (hash == "StorageFilterRemove".GetHashCode())
				{
					if (packet.ConfigType == BuildingConfigType.String && !string.IsNullOrEmpty(packet.StringValue))
					{
						Tag tag = new Tag(packet.StringValue);
						treeFilterable.RemoveTagFromFilter(tag);
						//DebugConsole.Log($"[StorageFilterHandler] Removed filter tag {tag} on {go.name}");
						return true;
					}
				}
			}

			// Handle Storage sweep-only
			var storage = go.GetComponent<Storage>();
			if (storage != null && hash == "StorageSweepOnly".GetHashCode())
			{
				storage.SetOnlyFetchMarkedItems(packet.Value > 0.5f);
				//DebugConsole.Log($"[StorageFilterHandler] Set SweepOnly={packet.Value > 0.5f} on {go.name}");
				return true;
			}

			return false;
		}
	}
}
