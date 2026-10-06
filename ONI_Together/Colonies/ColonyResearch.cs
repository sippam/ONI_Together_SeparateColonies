using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Per-colony research (ColonyRules.SeparateResearch).
    ///
    /// Host-authoritative, matching ONI Together's model (only the host simulates dupes):
    ///  - On the host, Research.Instance is the HOST's colony tree. Every other colony's tree lives in
    ///    ColonyInfo.Research and is advanced by research centers that colony owns (redirect patches).
    ///  - On a client, Research.Instance shows the CLIENT's own colony: the host sends that colony's profile
    ///    (ColonyResearchPacket) and the client writes it into Research.Instance (research screen, build menu).
    ///  - A client clicking a tech sends ONI Together's ResearchRequestPacket; the host applies it to the
    ///    sender's profile instead of its own tree.
    /// </summary>
    public static class ColonyResearch
    {
        private static readonly FieldInfo F_techs = AccessTools.Field(typeof(Research), "techs");
        private static readonly FieldInfo F_queued = AccessTools.Field(typeof(Research), "queuedTech");
        private static readonly FieldInfo F_complete = AccessTools.Field(typeof(TechInstance), "complete");

        /// <summary>True while a profile is written into Research.Instance (suppresses ONI Together's research packets).</summary>
        public static bool IsApplyingProfile { get; private set; }

        public static bool SeparateActive => ColonyRegistry.Active && ColonyRules.SeparateResearch;

        /// <summary>Colony whose tree is Research.Instance on this machine.</summary>
        public static int HolderColony => ColonyRegistry.LocalColonyId;

        /// <summary>Owner of a research building if its points must go to a colony profile instead of Research.Instance.</summary>
        public static int ForeignOwner(Component c)
        {
            if (!SeparateActive || c == null) return ColonyRegistry.NoColony;
            int owner = ColonyRegistry.GetOwner(c.gameObject);
            if (owner == ColonyRegistry.NoColony || owner == HolderColony) return ColonyRegistry.NoColony;
            return owner;
        }

        #region Research.Instance <-> profile

        public static void Capture(Research research, ResearchProfile profile)
        {
            if (research == null || profile == null) return;
            var techs = F_techs?.GetValue(research) as List<TechInstance>;
            var queued = F_queued?.GetValue(research) as List<TechInstance>;
            if (techs == null) return;
            profile.CompletedTechIds.Clear();
            profile.Progress.Clear();
            foreach (var ti in techs)
            {
                if (ti?.tech == null) continue;
                if (ti.IsComplete()) profile.CompletedTechIds.Add(ti.tech.Id);
                var pts = new Dictionary<string, float>();
                bool any = false;
                foreach (var kv in ti.progressInventory.PointsByTypeID)
                {
                    pts[kv.Key] = kv.Value;
                    if (kv.Value != 0f) any = true;
                }
                if (any) profile.Progress[ti.tech.Id] = pts;
            }
            profile.TargetTechId = (queued != null && queued.Count > 0) ? queued[queued.Count - 1].tech.Id : "";
            profile.Initialized = true;
        }

        /// <summary>Overwrite Research.Instance with a profile (client side). Newly completed techs fire ResearchComplete.</summary>
        public static void Apply(Research research, ResearchProfile profile)
        {
            if (research == null || profile == null) return;
            var techs = F_techs?.GetValue(research) as List<TechInstance>;
            if (techs == null || F_complete == null) return;

            IsApplyingProfile = true;
            try
            {
                foreach (var id in profile.CompletedTechIds) { var t = Db.Get().Techs.TryGet(id); if (t != null) research.GetOrAdd(t); }
                foreach (var id in profile.Progress.Keys) { var t = Db.Get().Techs.TryGet(id); if (t != null) research.GetOrAdd(t); }

                var completed = new HashSet<string>(profile.CompletedTechIds);
                var newlyCompleted = new List<Tech>();
                foreach (var ti in techs)
                {
                    if (ti?.tech == null) continue;
                    bool done = completed.Contains(ti.tech.Id);
                    if (done && !ti.IsComplete()) newlyCompleted.Add(ti.tech);
                    F_complete.SetValue(ti, done);
                    var keys = new List<string>(ti.progressInventory.PointsByTypeID.Keys);
                    profile.Progress.TryGetValue(ti.tech.Id, out var pts);
                    foreach (var k in keys)
                    {
                        float v = 0f;
                        if (pts != null) pts.TryGetValue(k, out v);
                        ti.progressInventory.PointsByTypeID[k] = v;
                    }
                }

                Tech target = string.IsNullOrEmpty(profile.TargetTechId) ? null : Db.Get().Techs.TryGet(profile.TargetTechId);
                var active = research.GetActiveResearch();
                string currentTarget = "";
                var queued = F_queued?.GetValue(research) as List<TechInstance>;
                if (queued != null && queued.Count > 0) currentTarget = queued[queued.Count - 1].tech.Id;
                if (currentTarget != (profile.TargetTechId ?? "") || (target == null && active != null))
                    research.SetActiveResearch(target, clearQueue: true);

                foreach (var t in newlyCompleted)
                {
                    try { research.Trigger((int)GameHashes.ResearchComplete, t); } catch { }
                }
                RefreshScreen();
            }
            finally
            {
                IsApplyingProfile = false;
            }
        }

        private static void RefreshScreen()
        {
            try
            {
                var screen = ManagementMenu.Instance != null ? ManagementMenu.Instance.researchScreen : null;
                if (screen != null) Traverse.Create(screen).Method("UpdateProgressBars").GetValue();
            }
            catch { }
        }

        #endregion

        #region Foreign colony research (host, profile-only)

        public static bool IsComplete(ResearchProfile p, Tech tech) => tech != null && p.CompletedTechIds.Contains(tech.Id);

        /// <summary>Same order as Research.SetActiveResearch: target + all incomplete prerequisites, sorted by tier.</summary>
        public static List<Tech> BuildQueue(ResearchProfile p)
        {
            var result = new List<Tech>();
            if (p == null || string.IsNullOrEmpty(p.TargetTechId)) return result;
            var target = Db.Get().Techs.TryGet(p.TargetTechId);
            if (target == null) return result;
            AddToQueue(p, target, result);
            result.Sort((a, b) => a.tier.CompareTo(b.tier));
            return result;
        }

        private static void AddToQueue(ResearchProfile p, Tech tech, List<Tech> queue)
        {
            if (!IsComplete(p, tech) && !queue.Contains(tech)) queue.Add(tech);
            foreach (var req in tech.requiredTech) AddToQueue(p, req, queue);
        }

        public static Tech GetActiveTech(ResearchProfile p)
        {
            var q = BuildQueue(p);
            return q.Count > 0 ? q[0] : null;
        }

        public static float GetPoints(ResearchProfile p, string techId, string typeId)
        {
            if (p != null && p.Progress.TryGetValue(techId, out var pts) && pts.TryGetValue(typeId, out var v)) return v;
            return 0f;
        }

        public static void AddPoints(int colonyId, string typeId, float points)
        {
            var colony = ColonyRegistry.Get(colonyId);
            if (colony == null) return;
            if (colonyId == HolderColony)
            {
                Research.Instance?.AddResearchPoints(typeId, points);
                return;
            }
            var p = colony.Research;
            var active = GetActiveTech(p);
            if (active == null) return;

            if (!p.Progress.TryGetValue(active.Id, out var pts))
            {
                pts = new Dictionary<string, float>();
                p.Progress[active.Id] = pts;
            }
            pts.TryGetValue(typeId, out float cur);
            pts[typeId] = cur + points;
            MarkDirty(colonyId);

            bool canAfford = true;
            foreach (var cost in active.costsByResearchTypeID)
            {
                pts.TryGetValue(cost.Key, out float have);
                if (have < cost.Value) { canAfford = false; break; }
            }
            if (!canAfford)
            {
                NotifyCenters(colonyId);
                return;
            }

            foreach (var cost in active.costsByResearchTypeID)
            {
                pts.TryGetValue(cost.Key, out float have);
                pts[cost.Key] = have - cost.Value;
            }
            if (!p.CompletedTechIds.Contains(active.Id)) p.CompletedTechIds.Add(active.Id);
            if (p.TargetTechId == active.Id) p.TargetTechId = "";
            Debug.Log($"[ONIT-Colonies] Colony #{colonyId} completed research {active.Id}");
            NotifyCenters(colonyId);
            SendProfileNow(colonyId);
        }

        /// <summary>Host: a player chose research. Returns false if the vanilla/host tree should handle it.</summary>
        public static bool HostHandleRequest(int colonyId, string techId)
        {
            if (!SeparateActive) return false;
            if (colonyId == ColonyContext.Spectator) return true; // swallow
            if (colonyId <= 0 || colonyId == HolderColony) return false;
            var colony = ColonyRegistry.Get(colonyId);
            if (colony == null) return true;
            colony.Research.TargetTechId = techId ?? "";
            colony.Research.Initialized = true;
            NotifyCenters(colonyId);
            SendProfileNow(colonyId);
            return true;
        }

        /// <summary>Foreign research centers don't hear Research.Instance events — poke them (ActiveResearchChanged).</summary>
        public static void NotifyCenters(int colonyId)
        {
            try
            {
                foreach (KMonoBehaviour rc in global::Components.ResearchCenters)
                    if (rc != null && ColonyRegistry.GetOwner(rc.gameObject) == colonyId)
                        rc.Trigger(-1914338957, null);
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] NotifyCenters: {ex.Message}"); }
        }

        public static float GetPercentComplete(int colonyId, string typeId)
        {
            var colony = ColonyRegistry.Get(colonyId);
            if (colony == null) return 0f;
            var active = GetActiveTech(colony.Research);
            if (active == null) return 0f;
            if (!active.costsByResearchTypeID.TryGetValue(typeId, out float cost) || cost <= 0f) return 1f;
            return GetPoints(colony.Research, active.Id, typeId) / cost;
        }

        public static bool ComponentCompleted(int colonyId, string typeId)
        {
            var colony = ColonyRegistry.Get(colonyId);
            if (colony == null) return false;
            var active = GetActiveTech(colony.Research);
            if (active == null) return false;
            active.costsByResearchTypeID.TryGetValue(typeId, out float cost);
            return GetPoints(colony.Research, active.Id, typeId) >= cost;
        }

        /// <summary>Name of what a colony is researching (panel).</summary>
        public static string ActiveName(int colonyId)
        {
            if (!SeparateActive || colonyId == HolderColony)
                return Research.Instance?.GetActiveResearch()?.tech?.Name ?? "-";
            var c = ColonyRegistry.Get(colonyId);
            if (c == null) return "-";
            // clients only know their own tree in detail
            return GetActiveTech(c.Research)?.Name ?? "-";
        }

        #endregion

        #region Replication (host -> owning client)

        private static readonly HashSet<int> _dirty = new HashSet<int>();
        private static float _sendTimer, _fullTimer;

        public static void MarkDirty(int colonyId) => _dirty.Add(colonyId);

        /// <summary>Host, every frame.</summary>
        public static void Tick()
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession || !SeparateActive) { _dirty.Clear(); return; }
            _sendTimer += Time.unscaledDeltaTime;
            _fullTimer += Time.unscaledDeltaTime;
            if (_fullTimer > 10f)
            {
                _fullTimer = 0f;
                foreach (var c in ColonyRegistry.Colonies) if (c.Id != HolderColony) _dirty.Add(c.Id);
            }
            if (_sendTimer < 2f || _dirty.Count == 0) return;
            _sendTimer = 0f;
            foreach (var id in new List<int>(_dirty)) SendProfileNow(id);
            _dirty.Clear();
        }

        public static void SendProfileNow(int colonyId)
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
            var c = ColonyRegistry.Get(colonyId);
            if (c == null || c.Id == HolderColony || c.OwnerPlayerId == 0) return;
            if (!MultiplayerSession.ConnectedPlayers.ContainsKey(c.OwnerPlayerId)) return;
            try
            {
                PacketSender.SendToPlayer(c.OwnerPlayerId, new ColonyResearchPacket
                {
                    ColonyId = c.Id,
                    Separate = true,
                    ProfileJson = JsonConvert.SerializeObject(c.Research)
                });
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] research send failed: {ex.Message}"); }
        }

        #endregion

        /// <summary>Host side: separate research switched on/off.</summary>
        public static void OnModeChanged(bool separate)
        {
            if (!MultiplayerSession.IsHost && MultiplayerSession.InSession) return;
            if (!ColonyRegistry.Enabled) return;
            if (separate)
            {
                // nobody loses progress: every colony starts from the current shared tree
                foreach (var c in ColonyRegistry.Colonies)
                    if (c.Id != HolderColony && !c.Research.Initialized) Capture(Research.Instance, c.Research);
                foreach (var c in ColonyRegistry.Colonies) MarkDirty(c.Id);
            }
            else
            {
                // back to one tree: clients reload the host's world (its tree is the shared one)
                ColonySession.ScheduleHardSync(1f, "research mode -> shared");
            }
        }

        /// <summary>Host: a colony was founded — fresh tree (or a copy of the shared tree if research is shared).</summary>
        public static void InitNewColony(ColonyInfo info)
        {
            info.Research = new ResearchProfile { Initialized = true };
        }
    }

    /// <summary>Host -> owning client: that colony's research tree.</summary>
    public class ColonyResearchPacket : IPacket
    {
        public int ColonyId;
        public bool Separate;
        public string ProfileJson = "";

        public void Serialize(BinaryWriter w) { w.Write(ColonyId); w.Write(Separate); w.Write(ProfileJson ?? ""); }
        public void Deserialize(BinaryReader r) { ColonyId = r.ReadInt32(); Separate = r.ReadBoolean(); ProfileJson = r.ReadString(); }

        public void OnDispatched()
        {
            if (MultiplayerSession.IsHost || !Separate) return;
            if (ColonyId != ColonyRegistry.LocalColonyId || Research.Instance == null) return;
            try
            {
                var p = JsonConvert.DeserializeObject<ResearchProfile>(ProfileJson);
                if (p == null) return;
                var mine = ColonyRegistry.Get(ColonyId);
                if (mine != null) mine.Research = p;
                ColonyResearch.Apply(Research.Instance, p);
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] Bad research packet: {ex.Message}"); }
        }
    }
}
