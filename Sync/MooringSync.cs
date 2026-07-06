using System;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 2 — replicate the boat's mooring lines (leaving / arriving at a dock).
    ///
    /// <para>Decompile facts: a rope <c>IsMoored()</c> iff <c>mooredToSpring != null</c>;
    /// the state-changing methods are <c>Unmoor()</c> (triggered by <c>OnPickup</c> — grabbing the
    /// moored rope) and <c>MoorTo(GPButtonDockMooring)</c> (triggered by <c>OnTriggerEnter</c> when
    /// the rope touches a dock). Polling those was unreliable, so we <b>Harmony-patch the two
    /// methods directly</b> and relay the action through the host (F3 authority).</para>
    ///
    /// <para>The rope is addressed by its index in its own <c>BoatMooringRopes.ropes</c> (identical
    /// on both machines — same ship). A mooring target dock is addressed by its real-space position;
    /// the receiver snaps to the nearest <c>GPButtonDockMooring</c> (docks are static). On the client
    /// mooring is cosmetic (its boat is a kinematic puppet) — only the host's spring holds the
    /// authoritative boat — but unmooring on the client must reach the host so the boat can sail.</para>
    /// </summary>
    public sealed class MooringSync
    {
        public static MooringSync Instance { get; private set; }

        private readonly CoopNet _net;
        private PlayerEmbarkerNew _emb;
        private Transform _cachedBoat;
        private BoatMooringRopes _bm;
        // Stable rope -> index map. Needed because MoorTo() reparents the rope under the DOCK,
        // so walking up the hierarchy no longer reaches BoatMooringRopes; the map doesn't care.
        private readonly System.Collections.Generic.Dictionary<PickupableBoatMooringRope, int> _ropeIndex
            = new System.Collections.Generic.Dictionary<PickupableBoatMooringRope, int>();

        private static bool _applying;   // guard: don't re-forward an action we're applying

        private string _lastAction = "—";
        private long _lastActionTick;

        public MooringSync(CoopNet net) { _net = net; Instance = this; }

        public string MooringText
        {
            get
            {
                var ropes = _bm != null ? _bm.ropes : null;
                if (ropes == null || ropes.Length == 0) return "no moorings";
                int moored = 0;
                for (int i = 0; i < ropes.Length; i++)
                    if (ropes[i] != null && ropes[i].IsMoored()) moored++;
                string act = "—";
                if (_lastActionTick != 0)
                {
                    long age = _net.Clock.ServerTick - _lastActionTick;
                    if (age < 0) age = 0;
                    act = _lastAction + " " + age + "ms";
                }
                return moored + "/" + ropes.Length + " moored · " + act;
            }
        }

        public void Tick(float dt)
        {
            // Only keeps the local boat's mooring component cached for applying remote actions
            // and for the overlay; the actual sync is event-driven via the Harmony hooks.
            if (_emb == null) _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat == _cachedBoat && _bm != null && _ropeIndex.Count > 0) return;
            _cachedBoat = boat;
            _bm = boat != null
                ? (boat.GetComponentInChildren<BoatMooringRopes>(true) ?? boat.GetComponentInParent<BoatMooringRopes>())
                : null;
            RebuildIndex();
        }

        private void RebuildIndex()
        {
            _ropeIndex.Clear();
            var ropes = _bm != null ? _bm.ropes : null;
            if (ropes == null) return;
            for (int i = 0; i < ropes.Length; i++)
                if (ropes[i] != null) _ropeIndex[ropes[i]] = i;
        }

        // -----------------------------------------------------------------
        // Called from the Harmony postfixes when a LOCAL mooring action happens
        // -----------------------------------------------------------------

        public void NotifyLocalUnmoor(PickupableBoatMooringRope rope) => NotifyLocal(rope, MooringKind.Unmoor, Vector3.zero, 0f);

        public void NotifyLocalMoor(PickupableBoatMooringRope rope, GPButtonDockMooring dock)
        {
            Vector3 dockReal = dock != null && CoordSpace.Ready
                ? CoordSpace.LocalToReal(dock.transform.position) : Vector3.zero;
            NotifyLocal(rope, MooringKind.Moor, dockReal, 0f);
        }

        public void NotifyLocalLength(PickupableBoatMooringRope rope)
        {
            if (rope == null) return;
            NotifyLocal(rope, MooringKind.Length, Vector3.zero, rope.currentRopeLengthSquared);
        }

        private void NotifyLocal(PickupableBoatMooringRope rope, MooringKind kind, Vector3 dockReal, float lengthSq)
        {
            // A Harmony postfix MUST NOT throw into the game's interaction flow (that would leave
            // the rope half-handled — "stuck in the air"). Swallow everything.
            try
            {
                if (_applying) return;                                   // we triggered this applying a remote action
                if (_net.State != LinkState.Connected) return;
                int idx = IndexOf(rope);
                if (idx < 0)
                {
                    Plugin.Logger.LogWarning("[MooringSync] Local " + kind + ": rope index not found (rope count in map=" + _ropeIndex.Count + ")");
                    return;
                }

                if (_net.Role == Role.Host)
                    _net.Broadcast(new MooringStateMsg { Index = (ushort)idx, Kind = kind, DockReal = dockReal, LengthSq = lengthSq },
                                   LiteNetLib.DeliveryMethod.ReliableOrdered);
                else if (_net.Role == Role.Client)
                    _net.Broadcast(new MooringRequestMsg { Index = (ushort)idx, Kind = kind, DockReal = dockReal, LengthSq = lengthSq },
                                   LiteNetLib.DeliveryMethod.ReliableOrdered);

                Remember("out " + kind + " #" + idx);
                Plugin.Logger.LogInfo("[MooringSync] Local " + kind + " rope #" + idx + " (role " + _net.Role + ") -> sent");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[MooringSync] NotifyLocal " + kind + ": " + e);
            }
        }

        // -----------------------------------------------------------------
        // Receivers
        // -----------------------------------------------------------------

        /// <summary>Client: apply the host's mooring action.</summary>
        public void OnMooringState(MooringStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            Plugin.Logger.LogInfo("[MooringSync] Client received from host " + msg.Kind + " rope #" + msg.Index);
            Apply(msg.Index, msg.Kind, msg.DockReal, msg.LengthSq, "in");
        }

        /// <summary>Host: a client's mooring request — apply authoritatively, then relay to the others.</summary>
        public void OnMooringRequest(MooringRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            Plugin.Logger.LogInfo("[MooringSync] Host received request " + msg.Kind + " rope #" + msg.Index);
            Apply(msg.Index, msg.Kind, msg.DockReal, msg.LengthSq, "in request");

            // Relay to the other clients so everyone converges (not back to the sender).
            _net.RelayExcept(new MooringStateMsg { Index = msg.Index, Kind = msg.Kind, DockReal = msg.DockReal, LengthSq = msg.LengthSq },
                             fromPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private void Apply(ushort index, MooringKind kind, Vector3 dockReal, float lengthSq, string tag)
        {
            var ropes = _bm != null ? _bm.ropes : null;
            if (ropes == null || index >= ropes.Length) { RefetchBoat(); ropes = _bm != null ? _bm.ropes : null; }
            if (ropes == null || index >= ropes.Length) return;
            var rope = ropes[index];
            if (rope == null) return;

            _applying = true;
            try
            {
                if (kind == MooringKind.Unmoor)
                {
                    if (rope.IsMoored())
                    {
                        rope.Unmoor();
                        rope.ResetRopePos();   // Unmoor only reparents; return the rope to the deck so it doesn't float
                    }
                }
                else if (kind == MooringKind.Moor)
                {
                    if (!rope.IsMoored())
                    {
                        var dock = FindDockNear(dockReal);
                        if (dock != null) rope.MoorTo(dock);
                        else Plugin.Logger.LogWarning("[MooringSync] Nearby dock not found for rope #" + index);
                    }
                }
                else // Length — set the authoritative rope length + spring distance (only while moored)
                {
                    if (rope.IsMoored())
                    {
                        rope.currentRopeLengthSquared = lengthSq;
                        var spring = GetMooredSpring(rope);
                        if (spring != null) spring.maxDistance = Mathf.Sqrt(Mathf.Max(0f, lengthSq));
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MooringSync] Apply " + kind + " #" + index + ": " + e.Message); }
            finally { _applying = false; }

            Remember(tag + " " + kind + " #" + index);
            Plugin.Logger.LogInfo("[MooringSync] Applied " + kind + " rope #" + index);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        // mooredToSpring is private — reach it reflectively to set the spring's max distance.
        private static System.Reflection.FieldInfo _fSpring;
        private static SpringJoint GetMooredSpring(PickupableBoatMooringRope rope)
        {
            try
            {
                if (_fSpring == null)
                    _fSpring = typeof(PickupableBoatMooringRope).GetField("mooredToSpring",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return _fSpring != null ? _fSpring.GetValue(rope) as SpringJoint : null;
            }
            catch { return null; }
        }

        private int IndexOf(PickupableBoatMooringRope rope)
        {
            if (rope == null) return -1;
            if (_ropeIndex.Count == 0) RebuildIndex();
            if (_ropeIndex.TryGetValue(rope, out int i)) return i;
            // Fallback (rope still under the boat, e.g. when unmooring): walk up.
            var bm = rope.GetComponentInParent<BoatMooringRopes>();
            if (bm != null && bm.ropes != null) return Array.IndexOf(bm.ropes, rope);
            return -1;
        }

        private void RefetchBoat()
        {
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat != null)
            {
                _bm = boat.GetComponentInChildren<BoatMooringRopes>(true) ?? boat.GetComponentInParent<BoatMooringRopes>();
                RebuildIndex();
            }
        }

        private static GPButtonDockMooring FindDockNear(Vector3 real)
        {
            if (!CoordSpace.Ready) return null;
            Vector3 local = CoordSpace.RealToLocal(real);
            GPButtonDockMooring best = null;
            float bestSqr = 9f;   // within 3 m
            foreach (var d in UnityEngine.Object.FindObjectsOfType<GPButtonDockMooring>())
            {
                float sqr = (d.transform.position - local).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = d; }
            }
            return best;
        }

        private void Remember(string text)
        {
            _lastAction = text;
            _lastActionTick = _net.Clock.ServerTick;
        }

        public void Clear()
        {
            _cachedBoat = null;
            _bm = null;
            _ropeIndex.Clear();
            _applying = false;
            _lastAction = "—";
            _lastActionTick = 0L;
        }
    }

    /// <summary>
    /// Harmony postfixes on the mooring rope's state-changing methods, so every unmoor/moor —
    /// however it was triggered (pickup, dock trigger, UnmoorAllRopes) — is forwarded.
    /// </summary>
    public static class MooringPatches
    {
        public static void Apply(Harmony harmony)
        {
            var t = typeof(PickupableBoatMooringRope);
            bool a = TryPatch(harmony, t, "Unmoor", Type.EmptyTypes, nameof(PostUnmoor));
            bool b = TryPatch(harmony, t, "MoorTo", new[] { typeof(GPButtonDockMooring) }, nameof(PostMoorTo));
            bool c = TryPatch(harmony, t, "ChangeRopeLength", new[] { typeof(float) }, nameof(PostChangeLength));
            Plugin.Logger.LogInfo("[MooringPatches] Mooring patches: Unmoor=" + a + ", MoorTo=" + b + ", ChangeRopeLength=" + c);
        }

        private static bool TryPatch(Harmony harmony, Type t, string method, Type[] args, string postfixName)
        {
            try
            {
                var mi = t.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, args, null);
                if (mi == null) { Plugin.Logger.LogWarning("[MooringPatches] Not found " + method); return false; }
                var postfix = new HarmonyMethod(typeof(MooringPatches).GetMethod(postfixName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
                harmony.Patch(mi, postfix: postfix);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MooringPatches] " + method + ": " + e.Message); return false; }
        }

        // Postfixes never throw into the game flow.
        private static void PostUnmoor(PickupableBoatMooringRope __instance)
        {
            try { MooringSync.Instance?.NotifyLocalUnmoor(__instance); } catch (Exception e) { Plugin.Logger.LogWarning("[MooringPatches] PostUnmoor: " + e.Message); }
        }

        private static void PostMoorTo(PickupableBoatMooringRope __instance, GPButtonDockMooring mooring)
        {
            try { MooringSync.Instance?.NotifyLocalMoor(__instance, mooring); } catch (Exception e) { Plugin.Logger.LogWarning("[MooringPatches] PostMoorTo: " + e.Message); }
        }

        // Only forward when the length actually changed (ChangeRopeLength returns false at limits).
        private static void PostChangeLength(PickupableBoatMooringRope __instance, bool __result)
        {
            if (!__result) return;
            try { MooringSync.Instance?.NotifyLocalLength(__instance); } catch (Exception e) { Plugin.Logger.LogWarning("[MooringPatches] PostChangeLength: " + e.Message); }
        }
    }
}
