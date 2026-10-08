using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using System.Collections;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class GameServerHardSync
	{
		/// <summary>
		/// Kept for compatibility: true while the host's manual hard sync button is cooling down.
		/// (It used to be once per cycle, which on a new world / long cycles looked stuck forever.)
		/// </summary>
		public static bool hardSyncDoneThisCycle
		{
			get => !CanManualSync;
			set { if (value) _manualCooldownUntil = Time.realtimeSinceStartup + ManualCooldown; else _manualCooldownUntil = 0f; }
		}
		public const float ManualCooldown = 60f;
		private static float _manualCooldownUntil;
		private static float _startedAt;
		private static bool _queued;
		private const float StuckAfter = 600f; // a sync "in progress" longer than this was interrupted

		public static bool CanManualSync => !IsHardSyncInProgress && Time.realtimeSinceStartup >= _manualCooldownUntil;
		public static int ManualCooldownLeft => Mathf.Max(0, Mathf.CeilToInt(_manualCooldownUntil - Time.realtimeSinceStartup));

		private static bool hardSyncInProgress = false;
		private static int numberOfClientsAtTimeOfSync = 0;

		public static bool IsHardSyncInProgress
		{

			get
			{
				if (hardSyncInProgress && Time.realtimeSinceStartup - _startedAt > StuckAfter)
				{
					DebugConsole.LogWarning("[HardSync] previous hard sync never finished — resetting");
					hardSyncInProgress = false;
				}
				return hardSyncInProgress;
			}
			set
			{
				hardSyncInProgress = value;
			}
		}

		public static void PerformHardSync(bool consumeDailyUse = false)
		{
			using var _ = Profiler.Scope();

			if (IsHardSyncInProgress)
			{
				// don't drop it: the running sync may have captured the world before this change (e.g. a new colony)
				_queued = true;
				DebugConsole.Log("[HardSync] A hard sync is already in progress — queued another one after it.");
				return;
			}
			hardSyncInProgress = true;
			_startedAt = Time.realtimeSinceStartup;
			if (consumeDailyUse) _manualCooldownUntil = _startedAt + ManualCooldown;

			SpeedControlScreen.Instance?.Pause(false); // Pause the game
			MultiplayerOverlay.Show(STRINGS.UI.MP_OVERLAY.SYNC.HARDSYNC_INPROGRESS);

            numberOfClientsAtTimeOfSync = MultiplayerSession.ConnectedPlayers.Count;
			var packet = new HardSyncPacket();
			PacketSender.SendToAllClients(packet);

			// Hide other player cursors as they are in hard sync and it'll reappear when they start sending packets again
			foreach (PlayerCursor cursor in MultiplayerSession.PlayerCursors.Values)
			{
				cursor.SetVisibility(false);
			}

			DebugConsole.Log($"[HardSync] Starting hard sync for {numberOfClientsAtTimeOfSync} client(s)...");
			CoroutineRunner.RunOne(HardSyncCoroutine(consumeDailyUse));
		}

		private static IEnumerator HardSyncCoroutine(bool consumeDailyUse = false)
		{
			using var _ = Profiler.Scope();

			hardSyncInProgress = true;

            ReadyManager.MarkAllAsUnready();
            SaveFileRequestPacket.SendSaveFileToAll();
            ReadyManager.RefreshScreen(); // Bring up ready screen for host

            int fileSize = SaveHelper.GetWorldSave().Length;
			int chunkSize = SaveHelper.SAVEFILE_CHUNKSIZE_KB * 1024;
			int chunkCount = Mathf.CeilToInt(fileSize / (float)chunkSize);
			float estimatedTransferDuration = chunkCount * SaveFileRequestPacket.SAVE_DATA_SEND_DELAY;
			yield return new WaitForSecondsRealtime(estimatedTransferDuration * numberOfClientsAtTimeOfSync);

            hardSyncInProgress = false;
			if (_queued && MultiplayerSession.IsHost && MultiplayerSession.InSession)
			{
				_queued = false;
				yield return new WaitForSecondsRealtime(3f);
				PerformHardSync(false);
			}
			_queued = false;
			// With the ready state I do not think this is needed anymore
			//SpeedControlScreen.Instance?.Unpause(false);
			//MultiplayerOverlay.Close();
		}
	}
}
