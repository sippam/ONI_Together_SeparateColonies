using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Per-colony Printing Pod timers (ColonyRules.SeparatePrintingPod). Host-authoritative:
    ///  - host: Immigration.Instance = host colony; other colonies tick in ColonyInfo.Immigration (Sim200ms postfix)
    ///  - client: Immigration.Instance shows the client's own colony; the host sends its profile
    ///    (ColonyImmigrationPacket) whenever it changes and every few seconds
    ///  - printables are generated locally by each player (no shared "first opener wins" options)
    ///  - printing / rejecting ends the timer of the colony whose pod was used (EndImmigration redirect)
    /// </summary>
    public static class ColonyImmigration
    {
        private static readonly FieldInfo F_available = AccessTools.Field(typeof(Immigration), "bImmigrantAvailable");
        private static readonly FieldInfo F_spawnIdx = AccessTools.Field(typeof(Immigration), "spawnIdx");

        public static bool SeparateActive => ColonyRegistry.Active && ColonyRules.SeparatePrintingPod && MultiplayerSession.InSession && HolderColony > 0;

        public static int HolderColony => ColonyRegistry.LocalColonyId;

        public static void Capture(Immigration imm, ImmigrationProfile p)
        {
            if (imm == null || p == null) return;
            p.TimeBeforeSpawn = imm.timeBeforeSpawn;
            p.ImmigrantAvailable = F_available != null && (bool)F_available.GetValue(imm);
            p.SpawnIdx = F_spawnIdx != null ? (int)F_spawnIdx.GetValue(imm) : 0;
            p.Initialized = true;
        }

        public static void Apply(Immigration imm, ImmigrationProfile p)
        {
            if (imm == null || p == null) return;
            imm.timeBeforeSpawn = p.TimeBeforeSpawn;
            F_available?.SetValue(imm, p.ImmigrantAvailable);
            F_spawnIdx?.SetValue(imm, p.SpawnIdx);
        }

        /// <summary>A newly founded colony starts the first printing countdown from scratch.</summary>
        public static void InitFresh(ImmigrationProfile p)
        {
            var imm = Immigration.Instance;
            p.SpawnIdx = 0;
            p.ImmigrantAvailable = false;
            p.TimeBeforeSpawn = (imm != null && imm.spawnInterval != null && imm.spawnInterval.Length > 0) ? imm.spawnInterval[0] : 600f;
            p.Initialized = true;
        }

        /// <summary>Host: tick every foreign colony (mirrors Immigration.Sim200ms).</summary>
        public static void TickForeign(float dt)
        {
            if (!SeparateActive) return;
            if (MultiplayerSession.InSession && !MultiplayerSession.IsHost) return;
            foreach (var c in ColonyRegistry.Colonies)
            {
                if (c.Id == HolderColony) continue;
                var p = c.Immigration;
                if (!p.Initialized) InitFresh(p);
                if (p.ImmigrantAvailable || IsHalted(c.Id)) continue;
                p.TimeBeforeSpawn = Math.Max(0f, p.TimeBeforeSpawn - dt);
                if (p.TimeBeforeSpawn <= 0f)
                {
                    p.ImmigrantAvailable = true;
                    SendStateNow(c.Id);
                }
            }
        }

        /// <summary>Is the colony's printing pod missing / not operational? (mirrors Immigration.IsHalted)</summary>
        public static bool IsHalted(int colonyId)
        {
            foreach (Telepad pad in global::Components.Telepads.Items)
            {
                if (pad == null || ColonyRegistry.GetOwner(pad.gameObject) != colonyId) continue;
                var op = pad.GetComponent<Operational>();
                if (op != null && op.IsOperational) return false;
            }
            return true;
        }

        /// <summary>Mirrors Immigration.EndImmigration for a foreign colony. Returns the spawnTable entry.</summary>
        public static int EndForeign(int colonyId)
        {
            var c = ColonyRegistry.Get(colonyId);
            var imm = Immigration.Instance;
            if (c == null || imm == null) return 1;
            var p = c.Immigration;
            p.ImmigrantAvailable = false;
            p.SpawnIdx++;
            int idx = Math.Min(p.SpawnIdx, imm.spawnInterval.Length - 1);
            p.TimeBeforeSpawn = imm.spawnInterval[idx];
            p.Initialized = true;
            SendStateNow(colonyId);
            return imm.spawnTable[idx];
        }

        #region Replication

        private static float _syncTimer;

        /// <summary>Host, every frame: periodic timer sync so clients' countdowns don't drift.</summary>
        public static void Tick()
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession || !SeparateActive) return;
            _syncTimer += Time.unscaledDeltaTime;
            if (_syncTimer < 5f) return;
            _syncTimer = 0f;
            foreach (var c in ColonyRegistry.Colonies) if (c.Id != HolderColony) SendStateNow(c.Id);
        }

        public static void SendStateNow(int colonyId)
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
            var c = ColonyRegistry.Get(colonyId);
            if (c == null || c.Id == HolderColony || c.OwnerPlayerId == 0) return;
            if (!MultiplayerSession.ConnectedPlayers.ContainsKey(c.OwnerPlayerId)) return;
            try
            {
                PacketSender.SendToPlayer(c.OwnerPlayerId, new ColonyImmigrationPacket
                {
                    ColonyId = c.Id,
                    TimeBeforeSpawn = c.Immigration.TimeBeforeSpawn,
                    ImmigrantAvailable = c.Immigration.ImmigrantAvailable,
                    SpawnIdx = c.Immigration.SpawnIdx
                });
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] immigration send failed: {ex.Message}"); }
        }

        #endregion

        /// <summary>Host: separate pods switched on/off.</summary>
        public static void OnModeChanged(bool separate)
        {
            if (MultiplayerSession.InSession && !MultiplayerSession.IsHost) return;
            if (!ColonyRegistry.Enabled || !separate) return;
            // every other colony starts from the current shared timer
            foreach (var c in ColonyRegistry.Colonies)
                if (c.Id != HolderColony && !c.Immigration.Initialized) Capture(Immigration.Instance, c.Immigration);
            foreach (var c in ColonyRegistry.Colonies) SendStateNow(c.Id);
        }

        public static string Describe(int colonyId)
        {
            if (!SeparateActive) return "";
            if (colonyId == HolderColony && Immigration.Instance != null)
                return Immigration.Instance.ImmigrantsAvailable ? "printables ready" : $"next print in {Mathf.CeilToInt(Immigration.Instance.timeBeforeSpawn)}s";
            var c = ColonyRegistry.Get(colonyId);
            if (c == null || !c.Immigration.Initialized) return "";
            return c.Immigration.ImmigrantAvailable ? "printables ready" : $"next print in {Mathf.CeilToInt(c.Immigration.TimeBeforeSpawn)}s";
        }
    }

    /// <summary>Host -> owning client: that colony's Printing Pod timer.</summary>
    public class ColonyImmigrationPacket : IPacket
    {
        public int ColonyId;
        public float TimeBeforeSpawn;
        public bool ImmigrantAvailable;
        public int SpawnIdx;

        public void Serialize(BinaryWriter w) { w.Write(ColonyId); w.Write(TimeBeforeSpawn); w.Write(ImmigrantAvailable); w.Write(SpawnIdx); }
        public void Deserialize(BinaryReader r) { ColonyId = r.ReadInt32(); TimeBeforeSpawn = r.ReadSingle(); ImmigrantAvailable = r.ReadBoolean(); SpawnIdx = r.ReadInt32(); }

        public void OnDispatched()
        {
            if (MultiplayerSession.IsHost || ColonyId != ColonyRegistry.LocalColonyId) return;
            var p = new ImmigrationProfile { TimeBeforeSpawn = TimeBeforeSpawn, ImmigrantAvailable = ImmigrantAvailable, SpawnIdx = SpawnIdx, Initialized = true };
            var mine = ColonyRegistry.Get(ColonyId);
            if (mine != null) mine.Immigration = p;
            ColonyImmigration.Apply(Immigration.Instance, p);
        }
    }
}
