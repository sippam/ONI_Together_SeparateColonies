using System;
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Founding colonies (Printing Pod + starting dupes). Host-authoritative: the host spawns the base and
    /// then hard-syncs the world, so every client gets the exact same objects (ONI Together's clients don't
    /// simulate dupes themselves).
    /// </summary>
    public static class ColonyFounder
    {
        public const int StartingDupes = 3;
        public const int StarterRationsPerDupe = 2;
        public const string HeadquartersId = "Headquarters";

        private static readonly Color[] Palette =
        {
            new Color(0.30f, 0.69f, 0.31f), // green (host)
            new Color(0.13f, 0.59f, 0.95f), // blue
            new Color(1.00f, 0.60f, 0.00f), // orange
            new Color(0.91f, 0.12f, 0.39f), // pink
            new Color(0.61f, 0.15f, 0.69f), // purple
            new Color(0.00f, 0.74f, 0.83f), // cyan
        };

        #region Host: first colony / mode

        /// <summary>Host: switch the mode for this save (and create the host's colony from the existing base).</summary>
        public static void HostSetMode(bool enabled)
        {
            if (!ColonyNet.IsHost && ColonyNet.InSession) return;
            ColonyRegistry.SetEnabled(enabled);
            if (enabled) HostCreateFirstColonyIfNeeded();
            ColonyNet.BroadcastRegistry();
            if (ColonyNet.InSession) PacketSender.SendToAllClients(ColonyOwnersPacket.FullSnapshot());
        }

        /// <summary>Host, mode on, host has no colony yet: the existing starting base becomes colony #1.</summary>
        public static void HostCreateFirstColonyIfNeeded()
        {
            if (!ColonyRegistry.Enabled) return;
            ulong me = ColonyRegistry.LocalPlayerId;
            if (me == 0 || ColonyRegistry.GetColonyOfPlayer(me) != ColonyRegistry.NoColony) return;

            int home = -1;
            foreach (Telepad pad in global::Components.Telepads.Items)
            {
                if (pad != null && ColonyRegistry.GetOwner(pad.gameObject) == ColonyRegistry.NoColony)
                {
                    home = Grid.PosToCell(pad);
                    break;
                }
            }
            var info = NewInfo(ColonyRegistry.NextColonyId(), "Host colony", me, LocalPlayerName(), home);
            ColonyRegistry.AddColony(info);
            ColonySchedules.ClaimAllUnowned(info.Id);
            using (ColonyContext.Enter(info.Id))
                ClaimExistingBase(info);
        }

        #endregion

        #region Client: request

        /// <summary>Local player asks to found a colony at `cell` (UI entry point).</summary>
        public static bool RequestFound(int cell, string colonyName, out string reason)
        {
            reason = null;
            if (!ColonyRegistry.Enabled) { reason = "Separate colonies mode is off"; return false; }
            if (ColonyRegistry.LocalColonyId != ColonyRegistry.NoColony) { reason = "You already have a colony"; return false; }
            if (!ValidateHomeCell(cell, out reason)) return false;

            string name = string.IsNullOrEmpty(colonyName) ? $"{LocalPlayerName()}'s colony" : colonyName;
            if (ColonyNet.IsHost || !ColonyNet.InSession)
                HostHandleRequest(ColonyRegistry.LocalPlayerId, cell, name, LocalPlayerName());
            else
                PacketSender.SendToHost(new ColonyFoundRequestPacket { HomeCell = cell, ColonyName = name, OwnerName = LocalPlayerName() });
            return true;
        }

        public static bool ValidateHomeCell(int cell, out string reason)
        {
            reason = null;
            if (!Grid.IsValidCell(cell)) { reason = "Invalid cell"; return false; }
            var def = Assets.GetBuildingDef(HeadquartersId);
            if (def == null) { reason = "Headquarters def missing"; return false; }
            // check placement without our own territory guard (the requester has no colony yet)
            using (ColonyContext.Enter(ColonyRegistry.NoColony))
            {
                if (!def.IsValidPlaceLocation(null, cell, Orientation.Neutral, out string fail))
                {
                    reason = $"Can't place a Printing Pod here: {fail}";
                    return false;
                }
            }
            int minDist = ColonyRules.TerritoryProtection ? Math.Max(10, ColonyRegistry.TerritoryRadius * 2 + 4) : 10;
            Grid.CellToXY(cell, out int x, out int y);
            foreach (var c in ColonyRegistry.Colonies)
            {
                if (!Grid.IsValidCell(c.HomeCell)) continue;
                Grid.CellToXY(c.HomeCell, out int hx, out int hy);
                int dx = x - hx, dy = y - hy;
                if (dx * dx + dy * dy < minDist * minDist)
                {
                    reason = $"Too close to {c.Name} (need {minDist} cells)";
                    return false;
                }
            }
            return true;
        }

        #endregion

        #region Host: authoritative handling

        public static void HostHandleRequest(ulong requester, int homeCell, string colonyName, string ownerName)
        {
            if (ColonyNet.InSession && !ColonyNet.IsHost) return;
            if (requester == 0) { Debug.LogWarning("[ONIT-Colonies] Found request without sender ignored"); return; }
            if (!ColonyRegistry.Enabled) { ColonyNet.SendMessageTo(requester, "Host hasn't enabled separate colonies"); return; }
            if (ColonyRegistry.GetColonyOfPlayer(requester) != ColonyRegistry.NoColony)
            {
                ColonyNet.SendMessageTo(requester, "You already have a colony");
                return;
            }
            if (!ValidateHomeCell(homeCell, out string reason))
            {
                ColonyNet.SendMessageTo(requester, $"Can't found colony: {reason}");
                return;
            }

            var info = NewInfo(ColonyRegistry.NextColonyId(), colonyName, requester, ownerName, homeCell);
            ColonyResearch.InitNewColony(info);
            ColonyImmigration.InitFresh(info.Immigration);
            ColonyRegistry.AddColony(info);
            using (ColonyContext.Enter(info.Id))
                SpawnBase(info);
            ColonyNet.BroadcastRegistry();
            ColonyNet.SendMessageTo(requester, $"Colony '{info.Name}' founded — syncing the world...");
            // Clients don't simulate the new pod/dupes on their own; give everyone the host's exact world.
            ColonySession.ScheduleHardSync(2f, $"colony #{info.Id} founded");
        }

        #endregion

        private static void ClaimExistingBase(ColonyInfo info)
        {
            int claimed = 0;
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
            {
                if (mi == null || ColonyRegistry.GetOwner(mi.gameObject) != ColonyRegistry.NoColony) continue;
                ColonyRegistry.SetOwner(mi.gameObject, info.Id);
                claimed++;
            }
            foreach (BuildingComplete b in global::Components.BuildingCompletes.Items)
            {
                if (b == null || ColonyRegistry.GetOwner(b.gameObject) != ColonyRegistry.NoColony) continue;
                // Everything players built belongs to the host colony (also when the mode is switched on mid-game).
                // World-gen/POI buildings (TemplateBuilding) stay world-owned unless they are part of the start base.
                bool template = b.HasTag(GameTags.TemplateBuilding);
                if (!template || b.GetComponent<Telepad>() != null || WithinRadius(Grid.PosToCell(b), info.HomeCell, Math.Max(ColonyRegistry.TerritoryRadius, 20)))
                {
                    ColonyRegistry.SetOwner(b.gameObject, info.Id);
                    claimed++;
                }
            }
            Debug.Log($"[ONIT-Colonies] Colony #{info.Id} claimed {claimed} existing objects");
        }

        private static bool WithinRadius(int cell, int center, int radius)
        {
            if (!Grid.IsValidCell(cell) || !Grid.IsValidCell(center)) return false;
            Grid.CellToXY(cell, out int x, out int y);
            Grid.CellToXY(center, out int cx, out int cy);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
        }

        private static void SpawnBase(ColonyInfo info)
        {
            var def = Assets.GetBuildingDef(HeadquartersId);
            if (def == null)
            {
                Debug.LogWarning("[ONIT-Colonies] Headquarters def not found — cannot spawn base");
                return;
            }

            // Build() bypasses placement rules; ValidateHomeCell already checked them.
            GameObject hq = def.Build(info.HomeCell, Orientation.Neutral, null, def.DefaultElements(), 293.15f, playsound: false, GameClock.Instance.GetTime());
            if (hq != null) ColonyRegistry.SetOwner(hq, info.Id);
            var telepad = hq != null ? hq.GetComponent<Telepad>() : null;
            if (ColonySchedules.SeparateActive) ColonySchedules.EnsureColonySchedule(info.Id);

            Grid.CellToXY(info.HomeCell, out int x, out int y);
            int spawned = 0, bionics = 0;
            for (int i = 0; i < StartingDupes; i++)
            {
                try
                {
                    var stats = new MinionStartingStats(is_starter_minion: true);
                    int cell = Grid.XYToCell(x + (i % 4) - 1, y);
                    if (!Grid.IsValidCell(cell)) cell = info.HomeCell;
                    var go = SpawnMinion(stats, cell);
                    if (go == null) continue;
                    ColonyRegistry.SetOwner(go, info.Id);
                    // extra_power_banks=false: vanilla would deliver bonus batteries to the START world's pod (host's base)
                    if (telepad != null) telepad.AddNewBaseMinion(go, false);
                    if (stats.personality.model == GameTags.Minions.Models.Bionic) bionics++;
                    spawned++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ONIT-Colonies] Failed to spawn starting dupe: {ex.Message}");
                }
            }

            if (telepad != null)
            {
                telepad.ScheduleNewBaseEvents(); // "A New Hope" morale buff
                for (int b = 0; b < bionics; b++)
                {
                    var pad = telepad;
                    GameScheduler.Instance.Schedule("ONIT_Colonies_ExtraPowerBanks", 3f + 4.5f * b, _ =>
                    {
                        if (pad != null) pad.Trigger(1982288670);
                    });
                }
            }

            var ration = Assets.GetPrefab(new Tag(FieldRationConfig.ID));
            if (ration != null)
            {
                for (int r = 0; r < StarterRationsPerDupe * Math.Max(1, spawned); r++)
                {
                    var food = GameUtil.KInstantiate(ration, Grid.CellToPosCCC(info.HomeCell, Grid.SceneLayer.Ore), Grid.SceneLayer.Ore);
                    food.SetActive(true);
                    ColonyRegistry.SetOwner(food, info.Id);
                }
            }
            Debug.Log($"[ONIT-Colonies] Spawned base for colony #{info.Id} at cell {info.HomeCell} with {spawned} dupes");
        }

        /// <summary>Same as Telepad/NewBaseScreen: instantiate the right minion model and apply the stats.</summary>
        private static GameObject SpawnMinion(MinionStartingStats stats, int cell)
        {
            var prefab = Assets.GetPrefab(BaseMinionConfig.GetMinionIDForModel(stats.personality.model));
            if (prefab == null) return null;
            var go = Util.KInstantiate(prefab);
            go.name = prefab.name;
            Immigration.Instance.ApplyDefaultPersonalPriorities(go);
            Vector3 pos = Grid.CellToPosCBC(cell, Grid.SceneLayer.Move);
            go.transform.SetLocalPosition(pos);
            go.SetActive(true);
            stats.Apply(go);
            return go;
        }

        private static ColonyInfo NewInfo(int id, string name, ulong owner, string ownerName, int home)
        {
            var col = Palette[(id - 1) % Palette.Length];
            return new ColonyInfo
            {
                Id = id,
                Name = string.IsNullOrEmpty(name) ? $"Colony {id}" : name,
                OwnerPlayerId = owner,
                OwnerName = string.IsNullOrEmpty(ownerName) ? owner.ToString() : ownerName,
                HomeCell = home,
                ColorR = col.r, ColorG = col.g, ColorB = col.b
            };
        }

        public static string LocalPlayerName()
        {
            try
            {
                var p = MultiplayerSession.LocalPlayer;
                if (p != null && !string.IsNullOrEmpty(p.PlayerName)) return p.PlayerName;
                if (MultiplayerSession.KnownPlayerNames.TryGetValue(MultiplayerSession.LocalUserID, out var n) && !string.IsNullOrEmpty(n)) return n;
                return Steamworks.SteamFriends.GetPersonaName();
            }
            catch { }
            return $"Player {ColonyRegistry.LocalPlayerId}";
        }
    }
}
