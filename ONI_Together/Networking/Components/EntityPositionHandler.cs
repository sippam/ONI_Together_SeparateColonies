using System;
using System.Collections.Generic;
using ONI_Together.Networking.Packets.Core;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class EntityPositionHandler : KMonoBehaviour
	{
        [MyCmpGet] public KBatchedAnimController kbac;
        [MyCmpGet] public Navigator navigator;

        private Vector3 lastSentPosition;
		private float lastSendTime;

		private const float PositionThreshold = 0.08f;
		// was 0.016 (every frame while moving, one packet per entity): with many critters/dupes that was thousands
		// of packets a second. Now ~10/s for critters, ~15/s for duplicants, batched per player (EntityPositionBatcher).
		private const float MIN_DT_CRITTER = 0.1f;
		private const float MIN_DT_MINION = 0.066f;
		private bool _isMinion;
		private int _prefabHash;
		public int PrefabHash => _prefabHash;

		/// <summary>Every live handler (client: adopting host NetIds for critters that differ per machine).</summary>
		public static readonly HashSet<EntityPositionHandler> All = new HashSet<EntityPositionHandler>();

        public Vector3 serverPosition;
        public long serverTimestamp;
        public bool serverFlipX;
        public bool serverFlipY;
        public NavType serverNavType;
        /// <summary>Client: Time.unscaledTime when the last position from the host arrived.</summary>
        public float lastReceivedAt = -1f;

        private const float SNAP_DISTANCE = 1.5f;
        private const float LERP_SPEED = 14f; // a bit softer: updates now arrive ~10/s instead of every frame

        private float _lastRequestTime;
        private const float REQUEST_COOLDOWN = 0.5f;
        private const float STALE_THRESHOLD = 3.5f;
		private const float HEARTBEAT_INTERVAL = 2f; // stationary entities: the client re-requests if one goes stale

        public override void OnSpawn()
		{
			using var _ = Profiler.Scope();
			base.OnSpawn();

			lastSentPosition = transform.position;
			lastSendTime = Time.unscaledTime;
			_isMinion = GetComponent<MinionIdentity>() != null;
			var kpid = GetComponent<KPrefabID>();
			_prefabHash = kpid != null ? kpid.PrefabTag.GetHash() : 0;
			All.Add(this);
		}

		public override void OnCleanUp()
		{
			All.Remove(this);
			base.OnCleanUp();
		}

		// ---- client: adopt host NetIds ----
		private static readonly Dictionary<int, float> _adoptTried = new Dictionary<int, float>();
		private static readonly Dictionary<int, float> _missingLogged = new Dictionary<int, float>();

		/// <summary>
		/// Client: a host position arrived for a NetId we don't know. Critters born/spawned after the last load get
		/// a NetId from their spawn cell/mass/temperature, which differs per machine, so their positions were dropped
		/// forever ("Could not find entity", ~35k per session) and they froze. Adopt the nearest local entity of the
		/// same prefab that isn't receiving updates of its own.
		/// </summary>
		public static EntityPositionHandler TryAdopt(int netId, int prefabHash, Vector3 pos)
		{
			float now = Time.unscaledTime;
			if (_adoptTried.TryGetValue(netId, out float t) && now - t < 5f) return null;
			_adoptTried[netId] = now;
			if (_adoptTried.Count > 4096) _adoptTried.Clear();

			EntityPositionHandler best = null;
			float bestD = 10f * 10f;
			foreach (var h in All)
			{
				if (h == null || h._isMinion || h._prefabHash != prefabHash) continue; // critters only: duplicants share a prefab
				if (h.lastReceivedAt >= 0f && now - h.lastReceivedAt < STALE_THRESHOLD) continue; // has its own updates
				float d = (h.transform.position - pos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = h; }
			}
			if (best == null) return null;
			var id = best.GetComponent<NetworkIdentity>();
			if (id == null) return null;
			id.OverrideNetId(netId);
			best.serverTimestamp = 0;
			Debug.Log($"[ONIT-Colonies] adopted host NetId {netId} for {best.name}");
			return best;
		}

		public static void NoteMissing(int netId)
		{
			float now = Time.unscaledTime;
			if (_missingLogged.TryGetValue(netId, out float t) && now - t < 60f) return; // once a minute per NetId
			_missingLogged[netId] = now;
			if (_missingLogged.Count > 4096) _missingLogged.Clear();
			ONI_Together.DebugTools.DebugConsole.LogWarning($"[Packets] Could not find entity with NetId {netId}");
		}

		private void Update()
		{
			using var _ = Profiler.Scope();

			if (this.GetNetId() == 0)
				return;

			if (!MultiplayerSession.InSession)
				return;

            if (MultiplayerSession.IsClient)
			{
				UpdatePosition();
                // Only do this if this entity is NOT visible by the host but is visible by the client
                TryRequestEntityPositionIfVisible();
                return;
			}

			// Skip if no clients connected
			if (MultiplayerSession.ConnectedPlayers.Count == 0)
				return;

			SendPositionUpdate();
		}

        private void TryRequestEntityPositionIfVisible()
        {
            if (WorldStateSyncer.TryGetLocalViewport(out var viewport))
            {
                int cell = Grid.PosToCell(transform.position);
                if (WorldStateSyncer.IsCellInRect(cell, viewport) && Time.unscaledTime - _lastRequestTime > REQUEST_COOLDOWN)
                {
                    // serverTimestamp is stale or we've never heard from the host
                    // (this compared Time.unscaledTime with the host's Unix-ms timestamp, so after the first packet it
                    //  was never "stale" again: a critter that moved while out of the client's view froze mid-hop)
                    if (serverTimestamp == 0 || lastReceivedAt < 0f || Time.unscaledTime - lastReceivedAt > STALE_THRESHOLD)
                    {
                        PacketSender.SendToHost(new EntityPositionRequestPacket { NetId = this.GetNetId(), RequesterId = MultiplayerSession.LocalUserID });
                        _lastRequestTime = Time.unscaledTime;
                    }
                }
            }
        }

        private void SendPositionUpdate()
        {
	        using var _ = Profiler.Scope();

	        try
	        {
		        Vector3 currentPosition = transform.position;
		        float currentTime = Time.unscaledTime;

                if (currentTime - lastSendTime < (_isMinion ? MIN_DT_MINION : MIN_DT_CRITTER))
			        return;

                bool moved = Vector3.Distance(currentPosition, lastSentPosition) >= PositionThreshold;
                bool heartbeatDue = currentTime - lastSendTime >= HEARTBEAT_INTERVAL;
                if (!moved && !heartbeatDue)
			        return;

		        NavType navType = NavType.Floor;
		        if (navigator != null && navigator.CurrentNavType != NavType.NumNavTypes)
			        navType = navigator.CurrentNavType;

		        EntityPositionBatcher.Add(new EntityPositionBatchPacket.Entry
		        {
			        NetId = this.GetNetId(),
			        X = currentPosition.x,
			        Y = currentPosition.y,
			        FlipX = kbac != null && kbac.FlipX,
			        FlipY = kbac != null && kbac.FlipY,
			        NavType = navType,
			        Prefab = _prefabHash
		        });

		        lastSentPosition = currentPosition;
		        lastSendTime = currentTime;
	        }
	        catch (Exception)
	        {
	        }
        }

        private void UpdatePosition()
        {
	        using var _ = Profiler.Scope();

            if (serverTimestamp == 0)
                return;

            if (kbac != null)
            {
	            kbac.FlipX = serverFlipX;
	            kbac.FlipY = serverFlipY;
            }

            if (navigator != null && navigator.CurrentNavType != serverNavType)
	            navigator.SetCurrentNavType(serverNavType);

            Vector3 currentPos = transform.position;
            float error = Vector3.Distance(currentPos, serverPosition);

            if (error > SNAP_DISTANCE)
            {
                transform.SetPosition(serverPosition);
                return;
            }

            float t = Mathf.Clamp01(LERP_SPEED * Time.unscaledDeltaTime);
            transform.SetPosition(Vector3.Lerp(currentPos, serverPosition, t));
        }
	}
}
