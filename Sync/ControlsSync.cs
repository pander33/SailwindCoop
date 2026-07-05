using System.Collections.Generic;
using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 1 — replicate the host's boat controls (host -> client).
    ///
    /// Two channels, because Sailwind splits "input" and "result":
    /// <list type="bullet">
    /// <item><b>Rope lengths</b> — every <c>RopeController.currentLength</c>. Drives the
    /// rope-state visuals that follow length directly: sail reef/furl, anchor payout.</item>
    /// <item><b>Node rotations</b> — the local rotation of each moving mechanical part:
    /// sail booms (on a <c>HingeJoint</c>) and the rudder. A boom's angle is physics/wind-driven within limits set
    /// by the rope, so it diverges on the kinematic client; we replicate the real rotation
    /// instead. Boom/rudder rigidbodies are made kinematic on the client so the simulation
    /// stops fighting the value (restored on disconnect). Winch cranks are intentionally
    /// not synced as transforms: rope length is the authoritative state, and host crank
    /// pose is only cosmetic.</item>
    /// </list>
    ///
    /// Both lists are enumerated identically on host and client (same boat → same order);
    /// rotations are slerped each frame for smoothness between the ~12 Hz snapshots.
    /// Stage 2 (shared control) will instead route client adjustments as ownership requests.
    /// </summary>
    public sealed class ControlsSync
    {
        private struct Node
        {
            public Transform T;     // the moving transform whose local rotation we sync
            public Rigidbody Rb;    // its rigidbody (null = visual-only, e.g. a winch crank)
        }

        private readonly CoopNet _net;
        private PlayerEmbarkerNew _emb;

        private Transform _cachedBoat;
        private RopeController[] _ropes = System.Array.Empty<RopeController>();
        private GPButtonRopeWinch[] _winches = System.Array.Empty<GPButtonRopeWinch>();
        private Node[] _nodes = System.Array.Empty<Node>();

        // Steering wheels on the boat. The rudder each one drives is a HingeJoint, so it's in
        // _nodes and follows the host through the normal rotation sync — the client's wheel visual
        // rides that. We don't drive the rudder locally; we only forward the wheel input (below).
        private GPButtonSteeringWheel[] _wheels = System.Array.Empty<GPButtonSteeringWheel>();

        // Steering forward (client -> host): the boat steers off the old Rudder, where the wheel's
        // public currentInput drives the rudder hinge spring. We forward that value when the local
        // player turns a wheel; the host applies it so its boat actually turns (BoatSync echoes back).
        private float[] _steerLastSent = System.Array.Empty<float>();
        private float _steerTimer;
        private MethodInfo _miApplyRudder;
        private const float SteerEps = 0.01f;

        // Diagnostics: steering-rope length + rudder angle, shown in the overlay so we can see
        // the steering channel move on both machines.
        private RopeControllerSteeringWheel _steerRope;
        private RudderNew _rudderNew;
        private Rudder _rudderOld;

        // Client: latest rotation targets + which rigidbodies we forced kinematic.
        private Quaternion[] _targetRots = System.Array.Empty<Quaternion>();
        private bool _haveTargets;
        private readonly List<KeyValuePair<Rigidbody, bool>> _kinematicSaved = new List<KeyValuePair<Rigidbody, bool>>();

        // Stage 2 — shared control. Per rope: last value seen from the host, the time
        // (ServerTick ms) until which the local player "owns" it after touching it, and
        // the last value we forwarded as a request.
        private float[] _hostLen = System.Array.Empty<float>();
        private long[] _localUntil = System.Array.Empty<long>();
        private float[] _lastSentLen = System.Array.Empty<float>();
        private float _reqTimer;
        private const float LocalHoldMs = 600f;   // keep ownership this long after the last local change
        private const float LenEps = 1e-5f;

        private int _lastReqIndex = -1;
        private float _lastReqLength;
        private bool _lastReqHasWinchRotation;
        private bool _lastReqIncoming;
        private long _lastReqTick;

        private float _sendTimer;
        private bool _warnedMismatch;

        // Reflection handle to the local interaction pointer (to detect a held control).
        private GoPointer _gp;
        private FieldInfo _fSticky;

        /// <summary>Control snapshot rate (Hz). The wheel/booms can move quickly, so keep it brisk.</summary>
        public float ControlHz = 12f;
        /// <summary>How fast the client slerps a node toward its latest target rotation.</summary>
        public float RotSmoothing = 14f;

        public int RopeCount => _ropes.Length;
        public int NodeCount => _nodes.Length;
        public int WinchCount => _winches.Length;

        /// <summary>Steering readout: wheels found, steering-rope length, rudder angle. Compare host vs client.</summary>
        public string SteeringText
        {
            get
            {
                if (_wheels.Length == 0) return "нет штурвала";
                string len = _steerRope != null ? _steerRope.currentLength.ToString("0.000") : "—";
                string ang = RudderAngleText();
                return _wheels.Length + "шт len=" + len + " угол=" + ang;
            }
        }

        public string LastControlRequestText
        {
            get
            {
                if (_lastReqIndex < 0) return "—";
                long age = _net.Clock.ServerTick - _lastReqTick;
                if (age < 0) age = 0;
                string dir = _lastReqIncoming ? "вх" : "исх";
                string rot = _lastReqHasWinchRotation ? "+ручка" : "без ручки";
                return dir + " #" + _lastReqIndex + " len=" + _lastReqLength.ToString("0.00") + " " + rot + " " + age + "мс";
            }
        }

        public ControlsSync(CoopNet net) { _net = net; }

        // currentAngle is non-public on both rudder types — read it reflectively for the readout.
        private FieldInfo _fRudderNewAngle, _fRudderOldAngle;
        private string RudderAngleText()
        {
            try
            {
                if (_rudderNew != null)
                {
                    if (_fRudderNewAngle == null)
                        _fRudderNewAngle = typeof(RudderNew).GetField("currentAngle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fRudderNewAngle != null) return ((float)_fRudderNewAngle.GetValue(_rudderNew)).ToString("0.0");
                }
                if (_rudderOld != null)
                {
                    if (_fRudderOldAngle == null)
                        _fRudderOldAngle = typeof(Rudder).GetField("currentAngle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fRudderOldAngle != null) return ((float)_fRudderOldAngle.GetValue(_rudderOld)).ToString("0.0");
                }
            }
            catch { }
            return "—";
        }

        // -----------------------------------------------------------------
        // Host: capture and broadcast lengths + node rotations
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;

            RefreshNodes();
            if (_ropes.Length == 0 && _nodes.Length == 0) return;

            float interval = 1f / Mathf.Max(1f, ControlHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            var lens = new float[_ropes.Length];
            for (int i = 0; i < _ropes.Length; i++)
                lens[i] = _ropes[i] != null ? _ropes[i].currentLength : 0f;

            var rots = new Quaternion[_nodes.Length];
            for (int i = 0; i < _nodes.Length; i++)
                rots[i] = _nodes[i].T != null ? _nodes[i].T.localRotation : Quaternion.identity;

            _net.Broadcast(new ControlStateMsg { Tick = _net.Clock.ServerTick, Lengths = lens, Rotations = rots },
                           LiteNetLib.DeliveryMethod.Unreliable);
        }

        // -----------------------------------------------------------------
        // Client: receive
        // -----------------------------------------------------------------

        public void OnControlState(ControlStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;

            RefreshNodes();

            // Rope lengths apply straight away (reef/furl/anchor track length directly),
            // EXCEPT ropes the local player is currently operating — those we own for a
            // short window so the host's echo doesn't snap our adjustment back (Stage 2).
            if (_ropes.Length > 0)
            {
                if (msg.Lengths.Length != _ropes.Length)
                {
                    WarnMismatch("тросов", msg.Lengths.Length, _ropes.Length);
                }
                else
                {
                    long now = _net.Clock.ServerTick;
                    for (int i = 0; i < _ropes.Length; i++)
                    {
                        var rc = _ropes[i];
                        if (rc == null) continue;
                        _hostLen[i] = msg.Lengths[i];
                        if (now >= _localUntil[i] && rc.currentLength != msg.Lengths[i])
                        {
                            rc.currentLength = msg.Lengths[i];
                            rc.changed = true;   // let the controller's Update re-apply
                        }
                    }
                }
            }

            // Node rotations are buffered and slerped in ApplyClient for smoothness.
            if (_nodes.Length > 0)
            {
                if (msg.Rotations.Length != _nodes.Length)
                {
                    WarnMismatch("узлов", msg.Rotations.Length, _nodes.Length);
                }
                else
                {
                    EnsureKinematic();
                    _targetRots = msg.Rotations;
                    _haveTargets = true;
                }
            }
        }

        /// <summary>
        /// Client per-frame: (1) forward any rope the local player just adjusted to the
        /// host as a request (Stage 2), and (2) smoothly drive each node toward its latest
        /// target rotation.
        /// </summary>
        public void ApplyClient(float dt)
        {
            if (_net.Role != Role.Client) return;

            ForwardLocalRopeChanges(dt);

            // Steering: forward the wheel's input to the host while the local player turns it.
            // We do NOT drive the rudder locally — the host steers authoritatively and its rudder
            // angle comes back through the node-rotation sync below, which turns the client's wheel.
            GoPointerButton heldBtn = HeldButton();
            ForwardSteering(dt, heldBtn);

            if (!_haveTargets || _nodes.Length == 0) return;
            if (_targetRots.Length != _nodes.Length) return;

            // Slerp every moving hinge (booms + rudder) toward the host's value — including the
            // rudder of a wheel the local player is holding, so they see the real steering result.
            float t = 1f - Mathf.Exp(-RotSmoothing * dt);
            for (int i = 0; i < _nodes.Length; i++)
            {
                var tr = _nodes[i].T;
                if (tr == null) continue;
                tr.localRotation = Quaternion.Slerp(tr.localRotation, _targetRots[i], t);
            }
        }

        /// <summary>
        /// Client: while the local player turns a wheel, forward its <c>currentInput</c> to the
        /// host (throttled, on change). The host drives the rudder from this so its boat turns.
        /// </summary>
        private void ForwardSteering(float dt, GoPointerButton heldBtn)
        {
            _steerTimer += dt;
            float interval = 1f / Mathf.Max(1f, ControlHz);
            if (_steerTimer < interval) return;
            _steerTimer = 0f;

            var wheel = heldBtn as GPButtonSteeringWheel;
            if (wheel == null) return;
            int idx = System.Array.IndexOf(_wheels, wheel);
            if (idx < 0) return;

            float input = wheel.currentInput;
            if (idx < _steerLastSent.Length && Mathf.Abs(input - _steerLastSent[idx]) < SteerEps) return;

            _net.Broadcast(new SteerRequestMsg { Index = (ushort)idx, Input = input },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            if (idx < _steerLastSent.Length) _steerLastSent[idx] = input;
        }

        /// <summary>Host: apply a client's wheel input — set it and re-run the rudder rotation.</summary>
        public void OnSteerRequest(SteerRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshNodes();
            int i = msg.Index;
            if (i < 0 || i >= _wheels.Length) return;
            var wheel = _wheels[i];
            if (wheel == null) return;

            wheel.currentInput = msg.Input;
            try
            {
                if (_miApplyRudder == null)
                    _miApplyRudder = typeof(GPButtonSteeringWheel).GetMethod(
                        "ApplyRudderRotation", BindingFlags.NonPublic | BindingFlags.Instance);
                _miApplyRudder?.Invoke(wheel, null);
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[ControlsSync] ApplyRudderRotation не удалось: " + e.Message);
            }
        }

        /// <summary>The control the local player is sticky-holding (winch/wheel), or null.</summary>
        private GoPointerButton HeldButton()
        {
            try
            {
                if (_gp == null) _gp = Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return null;
                if (_fSticky == null)
                    _fSticky = typeof(GoPointer).GetField("stickyClickedButton", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fSticky != null ? _fSticky.GetValue(_gp) as GoPointerButton : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Stage 2 — when the local player operates a winch, its <c>RopeController</c>
        /// changes <c>currentLength</c> locally (the only writer besides the host). We
        /// detect the divergence from the host's last value, take ownership of that rope
        /// for a short window, and forward the new length to the host as a request. The
        /// host applies it authoritatively and the result flows back via ControlState.
        /// </summary>
        private void ForwardLocalRopeChanges(float dt)
        {
            if (_ropes.Length == 0 || _hostLen.Length != _ropes.Length) return;

            long now = _net.Clock.ServerTick;

            // Detect local divergence and (re)arm the ownership window.
            for (int i = 0; i < _ropes.Length; i++)
            {
                var rc = _ropes[i];
                if (rc == null) continue;
                if (Mathf.Abs(rc.currentLength - _hostLen[i]) > LenEps)
                    _localUntil[i] = now + (long)LocalHoldMs;
            }

            // Throttle the request stream; reliable delivery guarantees the final value lands.
            _reqTimer += dt;
            float interval = 1f / Mathf.Max(1f, ControlHz);
            if (_reqTimer < interval) return;
            _reqTimer = 0f;

            for (int i = 0; i < _ropes.Length; i++)
            {
                var rc = _ropes[i];
                if (rc == null) continue;
                if (now < _localUntil[i] && Mathf.Abs(rc.currentLength - _lastSentLen[i]) > LenEps)
                {
                    var req = new ControlRequestMsg { Index = (ushort)i, Length = rc.currentLength };
                    var winch = FindWinchForRope(rc);
                    if (winch != null)
                    {
                        req.HasWinchRotation = true;
                        req.WinchRotation = winch.transform.localRotation;
                    }

                    _net.Broadcast(req, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    RememberControlRequest(i, rc.currentLength, req.HasWinchRotation, incoming: false);
                    _lastSentLen[i] = rc.currentLength;
                }
            }
        }

        // -----------------------------------------------------------------
        // Host: apply a client's control request (Stage 2)
        // -----------------------------------------------------------------

        public void OnControlRequest(ControlRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshNodes();
            int i = msg.Index;
            if (i < 0 || i >= _ropes.Length) return;
            var rc = _ropes[i];
            if (rc == null) return;

            RememberControlRequest(i, msg.Length, msg.HasWinchRotation, incoming: true);

            // F3 authority point — future: reject if another actor owns this node, enforce
            // per-node locks, etc. For now the host trusts and applies; its physics produces
            // the result and the normal ControlState broadcast carries it to everyone.
            if (rc.currentLength != msg.Length)
            {
                rc.currentLength = msg.Length;
                rc.changed = true;
            }

            if (msg.HasWinchRotation)
            {
                var winch = FindWinchForRope(rc);
                if (winch != null)
                    winch.transform.localRotation = msg.WinchRotation;
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void RefreshNodes()
        {
            if (_emb == null) _emb = Object.FindObjectOfType<PlayerEmbarkerNew>();
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat == _cachedBoat) return;

            RestoreKinematic();   // release the previous boat's bodies before rebinding
            _cachedBoat = boat;
            _haveTargets = false;
            _warnedMismatch = false;

            _steerRope = null;
            _rudderNew = null;
            _rudderOld = null;

            if (boat == null)
            {
                _ropes = System.Array.Empty<RopeController>();
                _winches = System.Array.Empty<GPButtonRopeWinch>();
                _wheels = System.Array.Empty<GPButtonSteeringWheel>();
                _nodes = System.Array.Empty<Node>();
                _hostLen = System.Array.Empty<float>();
                _localUntil = System.Array.Empty<long>();
                _lastSentLen = System.Array.Empty<float>();
                return;
            }

            _ropes = boat.GetComponentsInChildren<RopeController>(true);
            _winches = boat.GetComponentsInChildren<GPButtonRopeWinch>(true);
            _wheels = boat.GetComponentsInChildren<GPButtonSteeringWheel>(true);
            _steerLastSent = new float[_wheels.Length];
            for (int i = 0; i < _wheels.Length; i++)
                _steerLastSent[i] = _wheels[i] != null ? _wheels[i].currentInput : 0f;

            // Diagnostics references (first of each kind is enough for the readout).
            _steerRope = boat.GetComponentInChildren<RopeControllerSteeringWheel>(true);
            _rudderNew = boat.GetComponentInChildren<RudderNew>(true);
            _rudderOld = boat.GetComponentInChildren<Rudder>(true);

            // Stage 2 bookkeeping, seeded from current values so we don't false-trigger.
            _hostLen = new float[_ropes.Length];
            _localUntil = new long[_ropes.Length];
            _lastSentLen = new float[_ropes.Length];
            for (int i = 0; i < _ropes.Length; i++)
            {
                float v = _ropes[i] != null ? _ropes[i].currentLength : 0f;
                _hostLen[i] = v;
                _lastSentLen[i] = v;
            }

            // Moving parts whose rotation we replicate, in a stable enumeration order:
            // every hinge (sail booms + rudder). Winch cranks are excluded because a
            // host-side neutral crank pose can snap the client's local handle back after
            // a successful ControlRequest; rope length already carries the real state.
            var nodes = new List<Node>();
            foreach (var h in boat.GetComponentsInChildren<HingeJoint>(true))
                nodes.Add(new Node { T = h.transform, Rb = h.GetComponent<Rigidbody>() });
            _nodes = nodes.ToArray();

            Plugin.Logger.LogInfo("[ControlsSync] Корабль сменился: тросов=" + _ropes.Length +
                                  ", узлов=" + _nodes.Length + ", штурвалов=" + _wheels.Length +
                                  (boat != null ? " ('" + boat.name + "')" : ""));
        }

        /// <summary>Client: make the physics nodes kinematic so they hold the synced rotation.</summary>
        private void EnsureKinematic()
        {
            if (_kinematicSaved.Count > 0) return;   // already done for this boat
            foreach (var n in _nodes)
            {
                if (n.Rb == null) continue;
                _kinematicSaved.Add(new KeyValuePair<Rigidbody, bool>(n.Rb, n.Rb.isKinematic));
                n.Rb.isKinematic = true;
            }
        }

        private void RestoreKinematic()
        {
            foreach (var kv in _kinematicSaved)
                if (kv.Key != null) kv.Key.isKinematic = kv.Value;
            _kinematicSaved.Clear();
        }

        private void WarnMismatch(string what, int host, int client)
        {
            if (_warnedMismatch) return;
            _warnedMismatch = true;
            Plugin.Logger.LogWarning("[ControlsSync] Несовпадение числа " + what + ": хост=" + host +
                                     ", клиент=" + client + " — эта часть не применяется");
        }

        private GPButtonRopeWinch FindWinchForRope(RopeController rope)
        {
            if (rope == null) return null;
            for (int i = 0; i < _winches.Length; i++)
            {
                var w = _winches[i];
                if (w != null && w.rope == rope) return w;
            }
            return null;
        }

        private void RememberControlRequest(int index, float length, bool hasWinchRotation, bool incoming)
        {
            _lastReqIndex = index;
            _lastReqLength = length;
            _lastReqHasWinchRotation = hasWinchRotation;
            _lastReqIncoming = incoming;
            _lastReqTick = _net.Clock.ServerTick;
        }

        public void Clear()
        {
            RestoreKinematic();
            _cachedBoat = null;
            _ropes = System.Array.Empty<RopeController>();
            _winches = System.Array.Empty<GPButtonRopeWinch>();
            _wheels = System.Array.Empty<GPButtonSteeringWheel>();
            _steerLastSent = System.Array.Empty<float>();
            _steerTimer = 0f;
            _steerRope = null;
            _rudderNew = null;
            _rudderOld = null;
            _nodes = System.Array.Empty<Node>();
            _targetRots = System.Array.Empty<Quaternion>();
            _hostLen = System.Array.Empty<float>();
            _localUntil = System.Array.Empty<long>();
            _lastSentLen = System.Array.Empty<float>();
            _haveTargets = false;
            _sendTimer = 0f;
            _reqTimer = 0f;
            _lastReqIndex = -1;
            _lastReqLength = 0f;
            _lastReqHasWinchRotation = false;
            _lastReqIncoming = false;
            _lastReqTick = 0L;
            _warnedMismatch = false;
        }
    }
}
