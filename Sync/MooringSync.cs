using System;
using System.Collections.Generic;
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

        private readonly Transform _boundBoat;
        private readonly ushort _boatId = BoatLocator.NoBoat;
        private uint _layoutHash;
        private readonly BoatContexts<MooringSync> _fleet;

        private readonly CoopNet _net;
        private Transform _cachedBoat;
        private BoatMooringRopes _bm;
        // Stable rope -> index map. Needed because MoorTo() reparents the rope under the DOCK,
        // so walking up the hierarchy no longer reaches BoatMooringRopes; the map doesn't care.
        private readonly System.Collections.Generic.Dictionary<PickupableBoatMooringRope, int> _ropeIndex
            = new System.Collections.Generic.Dictionary<PickupableBoatMooringRope, int>();

        private static bool _applying;   // guard: don't re-forward an action we're applying
        private float _suppressLocalUntil;
        private float _snapshotTimer;
        private static uint _nextRequestId; // Never reuse after a boat context is replaced.
        private readonly Dictionary<ushort, PendingMooringState<MooringStateMsg>> _clientStates
            = new Dictionary<ushort, PendingMooringState<MooringStateMsg>>();
        private readonly HashSet<ushort> _missingApplyDock = new HashSet<ushort>();
        private readonly HashSet<ushort> _missingSendDock = new HashSet<ushort>();

        private string _lastAction = "—";
        private long _lastActionTick;

        public MooringSync(CoopNet net)
        {
            _net = net;
            Instance = this;
            _fleet = new BoatContexts<MooringSync>((boat, id) => new MooringSync(net, boat, id), c => c.Clear(),
                boat => BoatLayout.Stamp(MooringNodes(boat)));
        }

        private MooringSync(CoopNet net, Transform boat, ushort id)
        {
            _net = net; _boundBoat = boat; _boatId = id;
            Tick(0f);
        }

        public string MooringText
        {
            get
            {

                if (_fleet != null) return _fleet.Describe(c => "boat " + c._boatId + ": " + c.MooringText);
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
            if (_fleet != null) { foreach (var c in _fleet.Values) c.Tick(dt); return; }
            if (_net.Role == Role.Client && _net.State == LinkState.Connected)
                foreach (var entry in _clientStates) ApplyClientState(entry.Key, entry.Value);
            if (_net.Role == Role.Host && _net.State == LinkState.Connected && _bm != null)
            {
                _snapshotTimer += dt;
                if (_snapshotTimer >= 1f)
                {
                    _snapshotTimer = 0f;
                    for (int i = 0; _bm.ropes != null && i < _bm.ropes.Length; i++) SendRopeState((ushort)i);
                }
            }
            // Keep the bound hull's stable rope map; moving onto another deck does not rebind it.
            Transform boat = _boundBoat;
            // Boats not known yet (still loading) is not "no boat" — keep the cached one.
            if (boat == null && !BoatLocator.IndicesAuthoritative) return;
            if (boat == _cachedBoat && _bm != null && _ropeIndex.Count > 0) return;
            bool boatChanged = boat != _cachedBoat;
            _cachedBoat = boat;
            if (_net.Role == Role.Client && boatChanged)
                _suppressLocalUntil = Time.time + 3f;
            _bm = boat != null
                ? (boat.GetComponentInChildren<BoatMooringRopes>(true) ?? boat.GetComponentInParent<BoatMooringRopes>())
                : null;
            RebuildIndex();
        }

        private void RebuildIndex()
        {
            _ropeIndex.Clear();
            var ropes = _bm != null ? _bm.ropes : null;
            _layoutHash = BoatLayout.MooringHash(ropes);
            if (ropes == null) return;
            for (int i = 0; i < ropes.Length; i++)
                if (ropes[i] != null) _ropeIndex[ropes[i]] = i;
        }

        private static PickupableBoatMooringRope[] MooringNodes(Transform boat)
        {
            var mooring = boat.GetComponentInChildren<BoatMooringRopes>(true) ?? boat.GetComponentInParent<BoatMooringRopes>();
            return mooring != null && mooring.ropes != null ? mooring.ropes : Array.Empty<PickupableBoatMooringRope>();
        }

        // -----------------------------------------------------------------
        // Called from the Harmony postfixes when a LOCAL mooring action happens
        // -----------------------------------------------------------------

        public void NotifyLocalUnmoor(PickupableBoatMooringRope rope) => NotifyLocal(rope, MooringKind.Unmoor, Vector3.zero, 0f);

        public void NotifyLocalMoor(PickupableBoatMooringRope rope, GPButtonDockMooring dock)
        {
            Vector3 dockReal = dock != null && CoordSpace.Ready
                ? CoordSpace.LocalToReal(dock.transform.position) : Vector3.zero;
            NotifyLocal(rope, MooringKind.Moor, dockReal, rope != null ? rope.currentRopeLengthSquared : 0f);
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
                if (_fleet != null)
                {
                    foreach (var c in _fleet.Values)
                        if (c._ropeIndex.ContainsKey(rope)) { c.NotifyLocal(rope, kind, dockReal, lengthSq); break; }
                    return;
                }
                if (_applying) return;                                   // we triggered this applying a remote action
                if (_net.State != LinkState.Connected) return;
                if (_net.Role == Role.Client &&
                    (GameState.currentlyLoading || GameState.justStarted || Time.time < _suppressLocalUntil))
                    return;
                int idx = IndexOf(rope);
                if (idx < 0)
                {
                    Plugin.Logger.LogWarning("[MooringSync] Local " + kind + ": rope index not found (rope count in map=" + _ropeIndex.Count + ")");
                    return;
                }

                if (_net.Role == Role.Host)
                    _net.Broadcast(new MooringStateMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = (ushort)idx, Kind = kind, DockReal = dockReal, LengthSq = lengthSq },
                                   LiteNetLib.DeliveryMethod.ReliableOrdered);
                else if (_net.Role == Role.Client)
                {
                    uint requestId = unchecked(++_nextRequestId);
                    if (requestId == 0) requestId = unchecked(++_nextRequestId);
                    ClientState((ushort)idx).BeginRequest(requestId);
                    _net.Broadcast(new MooringRequestMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = (ushort)idx, Kind = kind, DockReal = dockReal, LengthSq = lengthSq, RequestId = requestId },
                                   LiteNetLib.DeliveryMethod.ReliableOrdered);
                }

                Remember("out " + kind + " #" + idx);
                Plugin.Logger.LogInfo("[MooringSync] Local " + kind + " boat=" + _boatId + " rope #" + idx + " (role " + _net.Role + ") -> sent");
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
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading || !_net.IsHostPeer(fromPeer)) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "MooringSync")) c.OnMooringState(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Client) return;
            var ropes = _bm != null ? _bm.ropes : null;
            if (ropes == null || msg.Index >= ropes.Length || ropes[msg.Index] == null) return;
            var state = ClientState(msg.Index);
            if (!state.Receive(msg.RequesterNetId, msg.RequestId, _net.MyNetId, msg.StateAvailable ? msg : null)) return;
            ApplyClientState(msg.Index, state);
        }

        /// <summary>Host: a client's mooring request — apply authoritatively, then relay to the others.</summary>
        public void OnMooringRequest(MooringRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading) return;
                if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(fromPeer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "MooringSync")) c.OnMooringRequest(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Host) return;
            Plugin.Logger.LogInfo("[MooringSync] Host received request " + msg.Kind + " boat=" + _boatId +
                " rope #" + msg.Index + " player=" + _net.PlayerNetIdForPeer(fromPeer) + " request=" + msg.RequestId);
            var ropes = _bm != null ? _bm.ropes : null;
            if (ropes == null || msg.Index >= ropes.Length || ropes[msg.Index] == null) return;
            Apply(msg.Index, msg.Kind, msg.DockReal, msg.LengthSq, "in request", applyMoorLength: false);
            SendRopeState(msg.Index, _net.PlayerNetIdForPeer(fromPeer), msg.RequestId);
            // The acknowledgement and snapshots share ReliableOrdered, including to the requester.
        }

        private void SendRopeState(ushort index, uint requester = 0, uint requestId = 0)
        {
            if (_bm == null || _bm.ropes == null || index >= _bm.ropes.Length) return;
            var rope = _bm.ropes[index];
            if (rope == null) return;
            var dock = DockFor(rope);
            bool moored = rope.IsMoored();
            if (moored && dock == null && CoordSpace.Ready) dock = FindDockNear(CoordSpace.LocalToReal(rope.transform.position));
            bool available = !moored || (dock != null && CoordSpace.Ready);
            if (!available)
            {
                if (_missingSendDock.Add(index))
                    Plugin.Logger.LogWarning("[MooringSync] Host state unavailable boat=" + _boatId + " rope #" + index +
                        ": moored dock or coordinate space unavailable; request=" + requestId + ". No Unmoor fabricated.");
            }
            else _missingSendDock.Remove(index);
            var state = new MooringStateMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = index,
                Kind = moored ? MooringKind.Moor : MooringKind.Unmoor,
                DockReal = moored && available ? CoordSpace.LocalToReal(dock.transform.position) : Vector3.zero,
                LengthSq = rope.currentRopeLengthSquared, RequesterNetId = requester, RequestId = requestId,
                StateAvailable = available };
            _net.Broadcast(state, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private PendingMooringState<MooringStateMsg> ClientState(ushort index)
        {
            if (!_clientStates.TryGetValue(index, out var state))
                _clientStates[index] = state = new PendingMooringState<MooringStateMsg>();
            return state;
        }

        private void ApplyClientState(ushort index, PendingMooringState<MooringStateMsg> state)
        {
            var ropes = _bm != null ? _bm.ropes : null;
            if (ropes == null || index >= ropes.Length || ropes[index] == null) return;
            var rope = ropes[index];
            bool held = IsLocallyHeld(rope);
            state.TryApply(held, msg => Apply(msg.Index, msg.Kind, msg.DockReal, msg.LengthSq, "in"));
        }

        private bool Apply(ushort index, MooringKind kind, Vector3 dockReal, float lengthSq, string tag, bool applyMoorLength = true)
        {
            var ropes = _bm != null ? _bm.ropes : null;
            if (ropes == null || index >= ropes.Length) { RefetchBoat(); ropes = _bm != null ? _bm.ropes : null; }
            if (ropes == null || index >= ropes.Length) return true;
            var rope = ropes[index];
            if (rope == null) return true;

            bool changed = false;
            _applying = true;
            try
            {
                if (kind == MooringKind.Unmoor)
                {
                    if (rope.IsMoored())
                    {
                        rope.Unmoor();
                        rope.ResetRopePos();   // Unmoor only reparents; return the rope to the deck so it doesn't float
                        changed = true;
                    }
                }
                else if (kind == MooringKind.Moor)
                {
                    var dock = FindDockNear(dockReal);
                    if (dock == null)
                    {
                        if (_missingApplyDock.Add(index))
                            Plugin.Logger.LogWarning("[MooringSync] " + _net.Role + " cannot apply Moor boat=" + _boatId +
                                " rope #" + index + " dockReal=" + dockReal + ": no loaded dock within 3 m" +
                                (_net.Role == Role.Client ? "; state deferred" : "; broadcasting actual outcome"));
                        return false;
                    }
                    _missingApplyDock.Remove(index);
                    if (rope.IsMoored() && DockFor(rope) != dock) rope.Unmoor();
                    if (!rope.IsMoored())
                    {
                        rope.MoorTo(dock);
                        changed = true;
                    }
                    // Moor requests run vanilla's distance calculation; host states restore its result.
                    if (applyMoorLength)
                    {
                        changed |= !rope.currentRopeLengthSquared.Equals(lengthSq);
                        ApplyLength(rope, lengthSq);
                    }
                }
                else // Length — set the authoritative rope length + spring distance (only while moored)
                {
                    if (rope.IsMoored())
                    {
                        changed = !rope.currentRopeLengthSquared.Equals(lengthSq);
                        ApplyLength(rope, lengthSq);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[MooringSync] " + _net.Role + " Apply " + kind + " boat=" + _boatId + " #" + index + ": " + e.Message);
                return true; // Exceptions are reported, not retried every frame.
            }
            finally { _applying = false; }

            if (changed)
            {
                Remember(tag + " " + kind + " #" + index);
                Plugin.Logger.LogInfo("[MooringSync] " + _net.Role + " Applied " + kind + " boat=" + _boatId + " rope #" + index);
            }
            return true;
        }

        private static void ApplyLength(PickupableBoatMooringRope rope, float lengthSq)
        {
            rope.currentRopeLengthSquared = lengthSq;
            var spring = GetMooredSpring(rope);
            if (spring != null) spring.maxDistance = Mathf.Sqrt(Mathf.Max(0f, lengthSq));
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        // mooredToSpring is private — reach it reflectively to set the spring's max distance.
        private static System.Reflection.FieldInfo _fLengthAdjuster;
        private static bool _warnedLengthAdjuster;
        private static bool IsLocallyHeld(PickupableBoatMooringRope rope)
        {
            if (rope.held != null) return true;
            try
            {
                if (_fLengthAdjuster == null)
                    _fLengthAdjuster = typeof(PickupableBoatMooringRope).GetField("lengthAdjuster",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (_fLengthAdjuster == null) throw new MissingFieldException("PickupableBoatMooringRope.lengthAdjuster");
                var adjuster = _fLengthAdjuster.GetValue(rope) as PickupableItem;
                return adjuster != null && adjuster.held != null;
            }
            catch (Exception e)
            {
                if (!_warnedLengthAdjuster)
                {
                    _warnedLengthAdjuster = true;
                    Plugin.Logger.LogWarning("[MooringSync] Не удалось определить удержание катушки швартова: " + e.Message);
                }
                return false;
            }
        }

        private static GPButtonDockMooring DockFor(PickupableBoatMooringRope rope)
        {
            var dock = rope.GetComponentInParent<GPButtonDockMooring>();
            if (dock != null) return dock;
            var spring = GetMooredSpring(rope);
            // MoorTo parents to the spring, which need not be under the dock button.
            if (spring != null)
                foreach (var candidate in UnityEngine.Object.FindObjectsOfType<GPButtonDockMooring>())
                    if (candidate.spring == spring) return candidate;
            return null;
        }

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
            return -1;
        }

        private void RefetchBoat()
        {
            Transform boat = _boundBoat;
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
            if (_fleet != null) { _fleet.Clear(); return; }
            _cachedBoat = null;
            _bm = null;
            _ropeIndex.Clear();
            _clientStates.Clear();
            _missingApplyDock.Clear();
            _missingSendDock.Clear();
            _snapshotTimer = 0f;
            _applying = false;
            _lastAction = "—";
            _lastActionTick = 0L;
            _suppressLocalUntil = 0f;
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
            SailwindCoop.Runtime.PatchHealth.Report("Mooring", (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0), 3);
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
