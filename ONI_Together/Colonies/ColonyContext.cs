using System;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// "Who is acting right now?" — a scoped colony id used to
    ///  (1) tag objects created by a player action (blueprints, dig/mop markers, printed dupes), and
    ///  (2) guard player actions against another colony's objects.
    ///
    /// It is ONLY set around player-initiated code paths:
    ///  - local input: PlayerController.OnKeyDown/OnKeyUp/Update (tools)
    ///  - remote input (host): PacketHandler dispatch of a client's packet (sender's colony)
    ///  - colony-internal events: construction finishing, printing pod delivery, founding a colony
    /// Plain simulation code runs with Current == 0, which means "no guard, no tagging" — this keeps
    /// every machine's simulation identical regardless of who the local player is.
    ///
    /// Main thread only.
    /// </summary>
    public static class ColonyContext
    {
        private const int MaxDepth = 32;
        private static readonly int[] _stack = new int[MaxDepth];
        private static int _depth;

        /// <summary>Acting player has no colony yet (spectator). Every guarded action is refused.</summary>
        public const int Spectator = -1;

        public static int Current => _depth > 0 ? _stack[_depth - 1] : ColonyRegistry.NoColony;

        /// <summary>A real colony is acting (Current &gt; 0).</summary>
        public static bool IsSet => _depth > 0 && _stack[_depth - 1] > 0;

        /// <summary>Some player (colony or spectator) is acting — guards apply.</summary>
        public static bool IsPlayerAction => _depth > 0 && _stack[_depth - 1] != ColonyRegistry.NoColony;

        public static void Push(int colonyId)
        {
            if (_depth >= MaxDepth)
            {
                Debug.LogWarning("[ONIT-Colonies] ColonyContext stack overflow — resetting");
                _depth = 0;
            }
            _stack[_depth++] = colonyId;
        }

        public static void Pop()
        {
            if (_depth > 0) _depth--;
        }

        public static Scope Enter(int colonyId) => new Scope(colonyId);

        /// <summary>Use with `using (ColonyContext.Enter(id)) { ... }`.</summary>
        public struct Scope : IDisposable
        {
            private bool _active;
            public Scope(int colonyId) { _active = true; Push(colonyId); }
            public void Dispose() { if (_active) { _active = false; Pop(); } }
        }
    }

    /// <summary>Permission rules between colonies.</summary>
    public static class ColonyGuard
    {
        /// <summary>
        /// May the acting colony (ColonyContext.Current) issue orders on this object?
        /// Unowned objects (natural tiles, wild plants, POI props, loose debris) are fair game.
        /// </summary>
        public static bool CanActOn(GameObject go)
        {
            if (!ColonyRegistry.Active || !ColonyContext.IsPlayerAction || go == null) return true;
            if (ColonyContext.Current == ColonyContext.Spectator) return false;
            if (!ColonyRules.BuildingProtection) return true;
            int owner = ColonyRegistry.GetOwner(go);
            return owner == ColonyRegistry.NoColony || owner == ColonyContext.Current;
        }

        public static bool CanActOn(Component c) => c == null || CanActOn(c.gameObject);

        /// <summary>Cancel tool: with CancelOnlyOwnOrders only your own colony's (or unowned) orders, otherwise like CanActOn.</summary>
        public static bool CanCancel(GameObject go)
        {
            if (!ColonyRegistry.Active || !ColonyContext.IsPlayerAction || go == null) return true;
            if (ColonyContext.Current == ColonyContext.Spectator) return false;
            if (!ColonyRules.CancelOnlyOwnOrders) return CanActOn(go);
            int owner = ColonyRegistry.GetOwner(go);
            return owner == ColonyRegistry.NoColony || owner == ColonyContext.Current;
        }

        /// <summary>For local UI (details screen, user menu): may the LOCAL player control this object?</summary>
        public static bool LocalCanControl(GameObject go)
        {
            if (!ColonyRegistry.Active || go == null) return true;
            int local = ColonyRegistry.LocalColonyId;
            if (local == ColonyRegistry.NoColony) return false; // spectator until a colony is founded
            if (!ColonyRules.BuildingProtection) return true;
            int owner = ColonyRegistry.GetOwner(go);
            return owner == ColonyRegistry.NoColony || owner == local;
        }

        /// <summary>May a worker of colony `workerColony` use/work on an object owned by `targetColony`?</summary>
        public static bool CanWork(int workerColony, int targetColony)
        {
            if (!ColonyRules.SeparateErrands) return true;
            if (targetColony == ColonyRegistry.NoColony) return true;   // world-owned
            if (workerColony == ColonyRegistry.NoColony) return true;   // unowned workers (e.g. legacy) — don't break them
            return workerColony == targetColony;
        }

        /// <summary>Cell-level check for building/digging inside someone else's territory.</summary>
        public static bool CanModifyCell(int cell)
        {
            if (!ColonyRegistry.Active || !ColonyContext.IsPlayerAction) return true;
            if (ColonyContext.Current == ColonyContext.Spectator) return false;
            return !ColonyRegistry.IsForeignTerritory(cell, ColonyContext.Current);
        }

        /// <summary>
        /// Called when the acting colony issues an errand on an unowned object (sweep, harvest, capture):
        /// the object becomes theirs so other colonies' dupes leave it alone.
        /// </summary>
        public static void ClaimIfUnowned(GameObject go)
        {
            if (!ColonyRegistry.Active || !ColonyContext.IsSet || go == null) return;
            if (ColonyRegistry.GetOwner(go) == ColonyRegistry.NoColony)
                ColonyRegistry.SetOwner(go, ColonyContext.Current);
        }

        /// <summary>Tag a freshly created object with the acting colony (no-op if no context).</summary>
        public static void TagNew(GameObject go)
        {
            if (!ColonyRegistry.Active || !ColonyContext.IsSet || go == null) return;
            if (ColonyRegistry.GetOwner(go) == ColonyRegistry.NoColony)
                ColonyRegistry.SetOwner(go, ColonyContext.Current);
        }
    }
}
