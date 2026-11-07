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
    /// stops fighting the value (restored on disconnect). Winch cranks are not in this list and
    /// are not sent as transforms either: their angle is the integral of the rope length, so each
    /// machine derives it from the lengths above (`ControlsWinches.cs`).</item>
    /// </list>
    ///
    /// Both lists are enumerated identically on host and client (same boat → same order);
    /// rotations are slerped each frame for smoothness between the ~12 Hz snapshots.
    /// Stage 2 (shared control) will instead route client adjustments as ownership requests.
    /// </summary>
    public sealed partial class ControlsSync
    {
        public static ControlsSync Instance { get; private set; }
        private struct Node
        {
            public Transform T;     // the moving transform whose local rotation we sync
            public Rigidbody Rb;    // its rigidbody (null = visual-only, e.g. a winch crank)
        }
        private struct SavedBody
        {
            public Rigidbody Body;
            public bool Kinematic;
            public RigidbodyInterpolation Interpolation;
        }

        private readonly Transform _boundBoat;
        private readonly ushort _boatId = BoatLocator.NoBoat;
        private uint _layoutHash;
        private readonly BoatContexts<ControlsSync> _fleet;

        private readonly CoopNet _net;

        private Transform _cachedBoat;
        private RopeController[] _ropes = System.Array.Empty<RopeController>();
        private GPButtonRopeWinch[] _winches = System.Array.Empty<GPButtonRopeWinch>();
        private Node[] _nodes = System.Array.Empty<Node>();

        // Steering wheels on the boat. The rudder each one drives is a HingeJoint, so it's in
        // _nodes and follows the host through the normal rotation sync. A guest holding the
        // wheel gets immediate visual feedback from InputScopePatches.
        private GPButtonSteeringWheel[] _wheels = System.Array.Empty<GPButtonSteeringWheel>();

        // Steering forward (client -> host): the boat steers off the old Rudder, where the wheel's
        // public currentInput drives the rudder hinge spring. We forward that value when the local
        // player turns a wheel; the host applies it so its boat actually turns (BoatSync echoes back).
        private float[] _steerLastSent = System.Array.Empty<float>();
        private float _steerTimer;
        private MethodInfo _miApplyRudder;

        // Diagnostics: steering-rope length + rudder angle, shown in the overlay so we can see
        // the steering channel move on both machines.
        private RopeControllerSteeringWheel _steerRope;
        private RudderNew _rudderNew;
        private Rudder _rudderOld;

        // Client: the host's node rotations by host tick + which rigidbodies we forced kinematic.
        // Follows the hull timeline, with bounded catch-up if hinge packets arrive separately.
        private struct RotSnapshot { public long Tick; public Quaternion[] Rots; }
        private readonly List<RotSnapshot> _rotBuf = new List<RotSnapshot>();
        private readonly HingePlayback _hingePlayback = new HingePlayback();
        private const int MaxRotSnapshots = 32;
        /// <summary>How far behind the host clock the client plays node rotations (ms).</summary>
        public float InterpDelayMs = 100f;
        private Quaternion[] _targetRots = System.Array.Empty<Quaternion>();
        private bool _haveTargets;
        private readonly List<SavedBody> _kinematicSaved = new List<SavedBody>();

        // Per-rope capture and acknowledged input; held/pending protection has no timeout.
        private float[] _hostLen = System.Array.Empty<float>();
        private float[] _lastSentLen = System.Array.Empty<float>();
        private float _reqTimer;

        private int _lastReqIndex = -1;
        private float _lastReqLength;
        private bool _lastReqHasWinchRotation;
        private bool _lastReqIncoming;
        private long _lastReqTick;

        private float _sendTimer;
        private readonly HashSet<string> _warnedMismatch = new HashSet<string>();
        private long _lastStateTick;

        // Reflection handle to the local interaction pointer (to detect a held control).
        private GoPointer _gp;
        private FieldInfo _fSticky;
        private FieldInfo _fClicked;

        /// <summary>Client request rate (Hz): rope and steering input forwarded to the host.</summary>
        public float ControlHz = 12f;
        /// <summary>Host state rate (Hz). Sent only while something moves; the client interpolates
        /// between snapshots, so the rate sets how closely it follows a quick boom swing.</summary>
        public float StateHz = 20f;

        public int RopeCount { get { if (_fleet == null) return _ropes.Length; int n = 0; foreach (var c in _fleet.Values) n += c.RopeCount; return n; } }
        public int NodeCount { get { if (_fleet == null) return _nodes.Length; int n = 0; foreach (var c in _fleet.Values) n += c.NodeCount; return n; } }
        public int WinchCount { get { if (_fleet == null) return _winches.Length; int n = 0; foreach (var c in _fleet.Values) n += c.WinchCount; return n; } }

        /// <summary>Steering readout: wheels found, steering-rope length, rudder angle. Compare host vs client.</summary>
        public string SteeringText
        {
            get
            {

                if (_fleet != null) return _fleet.Describe(c => "boat " + c._boatId + ": " + c.SteeringText);
                if (_wheels.Length == 0) return "no wheel";
                string len = _steerRope != null ? _steerRope.currentLength.ToString("0.000") : "—";
                string ang = RudderAngleText();
                return _wheels.Length + " pcs len=" + len + " angle=" + ang;
            }
        }

        public string LastControlRequestText
        {
            get
            {

                if (_fleet != null) return _fleet.Describe(c => "boat " + c._boatId + ": " + c.LastControlRequestText);
                if (_lastReqIndex < 0) return "—";
                long age = _net.Clock.ServerTick - _lastReqTick;
                if (age < 0) age = 0;
                string dir = _lastReqIncoming ? "in" : "out";
                string rot = _lastReqHasWinchRotation ? "+handle" : "no handle";
                return dir + " #" + _lastReqIndex + " len=" + _lastReqLength.ToString("0.00") + " " + rot + " " + age + "ms";
            }
        }

        public ControlsSync(CoopNet net)
        {
            _net = net;
            Instance = this;

            _fleet = new BoatContexts<ControlsSync>((boat, id) => new ControlsSync(net, boat, id) { InterpDelayMs = InterpDelayMs }, c => c.Clear(),
                boat => BoatLayout.Stamp(boat.GetComponentsInChildren<RopeController>(true), boat.GetComponentsInChildren<HingeJoint>(true), boat.GetComponentsInChildren<GPButtonSteeringWheel>(true)));
        }

        private ControlsSync(CoopNet net, Transform boat, ushort id)
        {
            _net = net; _boundBoat = boat; _boatId = id;
            RefreshNodes();
        }

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
            if (_fleet != null) { foreach (var c in _fleet.Values) c.Tick(dt); return; }
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;

            RefreshNodes();
            // Before the send decisions: a crank follows the local length changes of the whole frame.
            DriveCranks(dt);
            if (_ropes.Length == 0 && _nodes.Length == 0) return;

            _replyTimer += Time.unscaledDeltaTime;
            if (_replyOwed && _replyTimer >= ReplyInterval)
            {
                _replyOwed = false; _replyTimer = 0f; _sendTimer = 0f;
                SendState(null, false, true);
                return;
            }

            float interval = 1f / Mathf.Max(1f, StateHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer %= interval;

            SendState(null, false, periodic: true);
        }

        // -----------------------------------------------------------------
        // Client: receive
        // -----------------------------------------------------------------

        private void SendState(LiteNetLib.NetPeer peer, bool reconcile, bool reliable = false, bool periodic = false)
        {
            var inputs = new float[_wheels.Length];
            for (int i = 0; i < inputs.Length; i++) inputs[i] = _wheels[i] != null ? _wheels[i].currentInput : 0f;
            var lens = new float[_ropes.Length];
            for (int i = 0; i < _ropes.Length; i++)
                lens[i] = _ropes[i] != null ? _ropes[i].currentLength : 0f;

            var rots = new Quaternion[_nodes.Length];
            for (int i = 0; i < _nodes.Length; i++)
                rots[i] = _nodes[i].T != null ? _nodes[i].T.localRotation : Quaternion.identity;

            var locks = new bool[_wheels.Length];
            var ropeEpochs = new ControlEpoch[_ropes.Length]; var wheelEpochs = new ControlEpoch[_wheels.Length];
            for (int i = 0; i < ropeEpochs.Length; i++) ropeEpochs[i] = _ropeTracks[i].Capture(lens[i]);
            for (int i = 0; i < wheelEpochs.Length; i++) { locks[i] = _wheels[i] != null && Locked(_wheels[i]); wheelEpochs[i] = _wheelTracks[i].Capture(inputs[i], locks[i]); }
            var state = new ControlStateMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Tick = _net.Clock.ServerTick, Reconcile = reconcile, WheelInputs = inputs, Lengths = lens, Rotations = rots,
                WheelLocks = locks, RopeEpochs = ropeEpochs, WheelEpochs = wheelEpochs };
            if (peer != null) { peer.Send(state, LiteNetLib.DeliveryMethod.ReliableOrdered); return; }
            if (periodic)
            {
                // Nothing moved and nothing was acknowledged since the last packet: stay silent.
                var mode = _stream.Next(Differs(_sentState, state));
                if (mode == StreamSend.None) return;
                reliable = mode == StreamSend.Reliable;
            }
            else _stream.Sent(reliable);
            _sentState = state;
            _net.Broadcast(state, reliable ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
        }

        public void OnControlState(ControlStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading || !_net.IsHostPeer(fromPeer)) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "ControlsSync")) c.OnControlState(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Client) return;

            RefreshNodes();

            bool freshPose = msg.Tick >= _lastStateTick;
            if (freshPose) _lastStateTick = msg.Tick;
            var held = HeldButton();
            // Each part applies on its own: a count mismatch in one must not stop the others.
            bool ropesMatch = msg.Lengths.Length == _ropes.Length && msg.RopeEpochs.Length == _ropes.Length;
            bool wheelsMatch = msg.WheelInputs.Length == _wheels.Length && msg.WheelLocks.Length == _wheels.Length && msg.WheelEpochs.Length == _wheels.Length;
            if (wheelsMatch)
            {
                for (int i = 0; i < _wheels.Length; i++)
                    if (_wheels[i] != null && !_wheelDirty[i] && _wheelTracks[i].Receive(msg.WheelEpochs[i], msg.Tick, _net.MyNetId) && !WheelHeld(_wheels[i]))
                        ApplyWheel(_wheels[i], msg.WheelInputs[i], msg.WheelLocks[i]);
            }
            else WarnMismatch("wheels", msg.WheelInputs.Length, _wheels.Length);

            // Held controls and unsent/pending input retain local state until the latest acknowledgement.
            if (_ropes.Length > 0 || msg.Lengths.Length > 0)
            {
                if (!ropesMatch)
                {
                    WarnMismatch("ropes", msg.Lengths.Length, _ropes.Length);
                }
                else
                {
                    for (int i = 0; i < _ropes.Length; i++)
                    {
                        var rc = _ropes[i];
                        if (rc == null) continue;
                        if (_ropeDirty[i] || !_ropeTracks[i].Receive(msg.RopeEpochs[i], msg.Tick, _net.MyNetId)) continue;
                        _hostLen[i] = msg.Lengths[i];
                        if (!IsLocalRopeHeld(rc, held) && !rc.currentLength.Equals(msg.Lengths[i]))
                        {
                            rc.currentLength = msg.Lengths[i];
                            rc.changed = true;   // let the controller's Update re-apply
                        }
                    }
                }
            }

            // Node rotations are buffered and slerped in ApplyClient for smoothness.
            if (freshPose && (_nodes.Length > 0 || msg.Rotations.Length > 0))
            {
                if (msg.Rotations.Length != _nodes.Length)
                {
                    WarnMismatch("nodes", msg.Rotations.Length, _nodes.Length);
                }
                else
                {
                    EnsureKinematic();
                    PushRotations(msg.Tick, msg.Rotations);
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
            if (_fleet != null) { foreach (var c in _fleet.Values) c.ApplyClient(dt); return; }
            if (_net.Role != Role.Client) return;

            AskResync();
            ForwardLocalRopeChanges(dt);
            // Lengths received this frame are applied; the cranks they imply move before rendering.
            DriveCranks(dt);

            // Steering: forward the wheel's input to the host while the local player turns it.
            // We do NOT drive the rudder locally — the host steers authoritatively and its rudder
            // angle comes back through the node-rotation sync below.
            GoPointerButton heldBtn = HeldButton();
            ForwardSteering(dt, heldBtn);

            if (!_haveTargets || _nodes.Length == 0) return;
            if (_targetRots.Length != _nodes.Length) return;

            // Every moving hinge (booms + rudder) follows the host's value — including the rudder
            // of a wheel the local player is holding, so they see the real steering result.
            long localNow = _net.Clock.LocalTick;
            long renderTick = localNow + (long)_net.Clock.OffsetMs - (long)InterpDelayMs;
            var boats = Runtime.CoopBehaviour.Instance?.Boats;
            if (boats != null && boats.TryGetRenderTick(_boatId, out long hullTick)) renderTick = hullTick;
            renderTick = _hingePlayback.Advance(renderTick, _rotBuf[_rotBuf.Count - 1].Tick, localNow);
            if (!PickRotations(renderTick, out Quaternion[] from, out Quaternion[] to, out float alpha))
                return;
            for (int i = 0; i < _nodes.Length; i++)
            {
                var tr = _nodes[i].T;
                if (tr == null || i >= from.Length || i >= to.Length) continue;
                tr.localRotation = Quaternion.Slerp(from[i], to[i], alpha);
            }
        }

        private void PushRotations(long tick, Quaternion[] rots)
        {
            if (_rotBuf.Count > 0)
            {
                var last = _rotBuf[_rotBuf.Count - 1];
                if (tick < last.Tick) return;
                if (tick == last.Tick) { _rotBuf[_rotBuf.Count - 1] = new RotSnapshot { Tick = tick, Rots = rots }; return; }
                // After an idle interval, start from the resting angle one step back. A gap in
                // an active stream may instead be loss; interpolate across that whole gap.
                long step = (long)(1000f / Mathf.Max(1f, StateHz));
                long gap = tick - last.Tick;
                bool resting = gap > 3 * step && WasResting();
                if (resting || gap > 1000)
                {
                    long restEnd = tick - (resting ? step : System.Math.Max(step, 150));
                    _rotBuf.Add(new RotSnapshot { Tick = restEnd, Rots = last.Rots });
                    _hingePlayback.MarkRest(last.Tick, restEnd);
                }
            }
            _rotBuf.Add(new RotSnapshot { Tick = tick, Rots = rots });
            while (_rotBuf.Count > MaxRotSnapshots) _rotBuf.RemoveAt(0);
        }

        private bool WasResting()
        {
            if (_rotBuf.Count < 2) return false;
            var previous = _rotBuf[_rotBuf.Count - 2].Rots;
            var latest = _rotBuf[_rotBuf.Count - 1].Rots;
            for (int i = 0; i < latest.Length; i++)
                if (Quaternion.Angle(previous[i], latest[i]) > 0.05f) return false;
            return true;
        }

        /// <summary>The two snapshots around <paramref name="renderTick"/>; before the first or after
        /// the last it holds that one (no extrapolation: a hinge does not overshoot the host).</summary>
        private bool PickRotations(long renderTick, out Quaternion[] from, out Quaternion[] to, out float alpha)
        {
            from = to = null; alpha = 0f;
            int n = _rotBuf.Count;
            if (n == 0) return false;
            if (renderTick <= _rotBuf[0].Tick) { from = to = _rotBuf[0].Rots; return true; }
            if (renderTick >= _rotBuf[n - 1].Tick) { from = to = _rotBuf[n - 1].Rots; return true; }
            for (int i = 0; i < n - 1; i++)
            {
                var a = _rotBuf[i]; var b = _rotBuf[i + 1];
                if (renderTick > b.Tick) continue;
                // Older snapshots are no longer needed; keep the pair being played.
                if (i > 0) _rotBuf.RemoveRange(0, i);
                long span = b.Tick - a.Tick;
                alpha = span > 0 ? Mathf.Clamp01((renderTick - a.Tick) / (float)span) : 1f;
                from = a.Rots; to = b.Rots;
                return true;
            }
            from = to = _rotBuf[n - 1].Rots;
            return true;
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

            for (int idx = 0; idx < _wheels.Length; idx++)
            {
                var wheel = _wheels[idx];
                if (wheel == null || !_wheelDirty[idx]) continue;
                float input = _wheelInput[idx];
                _net.Broadcast(new SteerRequestMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = (ushort)idx,
                    Input = input, RequestId = Request(_wheelTracks[idx]) }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                _steerLastSent[idx] = input; _wheelDirty[idx] = false;
            }
        }

        /// <summary>Host: apply a client's wheel input — set it and re-run the rudder rotation.</summary>
        public void OnSteerRequest(SteerRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading) return;
                if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(fromPeer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "ControlsSync")) c.OnSteerRequest(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Host) return;
            RefreshNodes();
            int i = msg.Index;
            if (i < 0 || i >= _wheels.Length) return;
            var wheel = _wheels[i];
            if (wheel == null) return;
            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0 || !_wheelTracks[i].Requests.Accept(actor, msg.RequestId, false)) return;
            try
            {
                ApplyWheel(wheel, msg.Input, Locked(wheel));
                _wheelTracks[i].Acknowledge(actor, msg.RequestId);
                _replyOwed = true;
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[ControlsSync] ApplyRudderRotation failed: " + e.Message);
            }
        }

        /// <summary>The local player's sticky or actively clicked control, or null.</summary>
        private GoPointerButton HeldButton()
        {
            try
            {
                if (_gp == null) _gp = Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return null;
                if (_fSticky == null)
                    _fSticky = typeof(GoPointer).GetField("stickyClickedButton", BindingFlags.NonPublic | BindingFlags.Instance);
                var sticky = _fSticky?.GetValue(_gp) as GoPointerButton;
                if (sticky != null) return sticky;
                if (_fClicked == null)
                    _fClicked = typeof(GoPointer).GetField("clickedButton", BindingFlags.NonPublic | BindingFlags.Instance);
                var clicked = _fClicked?.GetValue(_gp) as GoPointerButton;
                // GoPointer assigns clickedButton before Click; a denied Click is not a grab.
                return clicked != null && clicked.IsCliked() ? clicked : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Actual grabbed input or a DeltaToLength hook marks a rope dirty. A dirty final
        /// value remains queued after quick-release; unrelated world divergence is not input.
        /// </summary>
        private void ForwardLocalRopeChanges(float dt)
        {
            if (_ropes.Length == 0 || _hostLen.Length != _ropes.Length) return;

            var held = HeldButton();
            // A grab protects the local node; an unheld divergence is never evidence of input.
            for (int i = 0; i < _ropes.Length; i++)
            {
                var rc = _ropes[i];
                if (rc == null) continue;
                if (IsLocalRopeHeld(rc, held))
                    if (!rc.currentLength.Equals(_lastSentLen[i])) _ropeDirty[i] = true;
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
                if (_ropeDirty[i])
                {
                    var req = new ControlRequestMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = (ushort)i,
                        Length = rc.currentLength, RequestId = Request(_ropeTracks[i]) };
                    // Still filled for a host that plays the crank pose it receives; this build's host
                    // derives the pose from Length instead (ControlsWinches.cs) and ignores it.
                    var winch = FindWinchForRope(rc);
                    if (winch != null)
                    {
                        req.HasWinchRotation = true;
                        req.WinchRotation = winch.transform.localRotation;
                    }

                    _net.Broadcast(req, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    if (!rc.currentLength.Equals(_lastSentLen[i]))
                        RememberControlRequest(i, rc.currentLength, req.HasWinchRotation, incoming: false);
                    _lastSentLen[i] = rc.currentLength;
                    _ropeDirty[i] = false;
                }
            }
        }

        // -----------------------------------------------------------------
        // Host: apply a client's control request (Stage 2)
        // -----------------------------------------------------------------

        public void OnControlRequest(ControlRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading) return;
                if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(fromPeer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "ControlsSync")) c.OnControlRequest(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Host) return;
            RefreshNodes();
            int i = msg.Index;
            if (i < 0 || i >= _ropes.Length) return;
            var rc = _ropes[i];
            if (rc == null) return;
            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0 || !_ropeTracks[i].Requests.Accept(actor, msg.RequestId, false)) return;

            // F3: the host applies client input and broadcasts the resulting state. The crank pose
            // that comes with the request (msg.WinchRotation) is not applied: the crank is driven from
            // this length change on both machines (ControlsWinches.cs). A raw copy stepped at the
            // request rate and left the game's own delta reference behind, which turned into a rope
            // length jump the next time this machine grabbed the winch.
            if (!rc.currentLength.Equals(msg.Length))
            {
                RememberControlRequest(i, msg.Length, msg.HasWinchRotation, incoming: true);
                rc.currentLength = msg.Length;
                rc.changed = true;
            }

            _ropeTracks[i].Acknowledge(actor, msg.RequestId);
            _replyOwed = true;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        internal void NotifyLocalWinchChanged(GPButtonRopeWinch winch)
        {
            if (InteractionContext.Suppressed || _net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (_fleet != null)
            {
                foreach (var context in _fleet.Values)
                    if (System.Array.IndexOf(context._winches, winch) >= 0) { context.NotifyLocalWinchChanged(winch); return; }
                return;
            }
            RefreshNodes();
            int index = winch == null ? -1 : System.Array.IndexOf(_ropes, winch.rope);
            if (index >= 0) _ropeDirty[index] = true;
        }

        private void RefreshNodes()
        {
            Transform boat = _boundBoat;
            // Same as MooringSync.Tick: null before boats are known is "not yet known", not "no boat".
            if (boat == null && !BoatLocator.IndicesAuthoritative) return;
            if (boat == _cachedBoat) return;

            RestoreKinematic();   // release the previous boat's bodies before rebinding
            _cachedBoat = boat;
            _haveTargets = false;
            _rotBuf.Clear();
            _hingePlayback.Clear();
            _warnedMismatch.Clear();
            _gp = null;
            _fSticky = null;
            _fClicked = null;
            _lastStateTick = 0;

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
                _lastSentLen = System.Array.Empty<float>();
                ResetControlTracks();
                ResetCranks();
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
            _lastSentLen = new float[_ropes.Length];
            for (int i = 0; i < _ropes.Length; i++)
            {
                float v = _ropes[i] != null ? _ropes[i].currentLength : 0f;
                _hostLen[i] = v;
                _lastSentLen[i] = v;
            }

            // Moving parts whose rotation we replicate, in a stable enumeration order:
            // every hinge (sail booms + rudder). Winch cranks are not here and are not sent: a
            // host-side neutral pose would snap the client's local handle back, so their angle is
            // derived on each machine from the rope length (ControlsWinches.cs).
            var nodes = new List<Node>();
            foreach (var h in boat.GetComponentsInChildren<HingeJoint>(true))
                nodes.Add(new Node { T = h.transform, Rb = h.GetComponent<Rigidbody>() });
            _nodes = nodes.ToArray();
            _layoutHash = BoatLayout.Hash(boat, _ropes, boat.GetComponentsInChildren<HingeJoint>(true), _wheels);
            ResetControlTracks();
            ResetCranks();

            Plugin.Logger.LogInfo("[ControlsSync] Boat changed: ropes=" + _ropes.Length +
                                  ", nodes=" + _nodes.Length + ", wheels=" + _wheels.Length +
                                  ", winches=" + _winches.Length +
                                  (boat != null ? " ('" + boat.name + "')" : ""));
        }

        /// <summary>Client: make the physics nodes kinematic so they hold the synced rotation.</summary>
        private void EnsureKinematic()
        {
            if (_kinematicSaved.Count > 0) return;   // already done for this boat
            foreach (var n in _nodes)
            {
                if (n.Rb == null) continue;
                if (!_heldBodies.Add(n.Rb)) continue;
                _kinematicSaved.Add(new SavedBody { Body = n.Rb, Kinematic = n.Rb.isKinematic, Interpolation = n.Rb.interpolation });
                n.Rb.isKinematic = true;
                n.Rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        // Every body some client context holds kinematic. Sail.FixedUpdate clears isKinematic on its
        // own body each physics step and pushes it with the local wind; ShipyardRefitPatches.PostSail
        // asks here to put a held sail back before the step runs.
        private static readonly HashSet<Rigidbody> _heldBodies = new HashSet<Rigidbody>();
        internal static bool HoldsBody(Rigidbody body) => body != null && _heldBodies.Contains(body);

        private void RestoreKinematic()
        {
            foreach (var saved in _kinematicSaved)
            {
                _heldBodies.Remove(saved.Body);
                if (saved.Body == null) continue;
                saved.Body.isKinematic = saved.Kinematic;
                saved.Body.interpolation = saved.Interpolation;
            }
            _kinematicSaved.Clear();
        }

        private void WarnMismatch(string what, int host, int client)
        {
            if (!_warnedMismatch.Add(what)) return;
            Plugin.Logger.LogWarning("[ControlsSync] Count mismatch boat=" + _boatId + " for " + what + ": host=" + host +
                                     ", client=" + client + " - this part is not applied");
        }

        private bool IsLocalRopeHeld(RopeController rope, GoPointerButton held)
        {
            if (rope is RopeControllerAnchor anchorRope && (AnchorSync.Instance?.PreserveRope(anchorRope) ?? false)) return true;
            var winch = FindWinchForRope(rope);
            if (winch != null && Grabbed(winch.rotHandle)) return true;
            if (held == null) return false;
            return FindWinchForRope(rope) == held ||
                (rope is RopeControllerSteeringWheel && held is GPButtonSteeringWheel && held.transform.IsChildOf(_boundBoat));
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

        public void InvalidateHull(ushort id) => _fleet?.Invalidate(id);

        public void Clear()
        {
            if (_fleet != null) { _fleet.Clear(); return; }
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
            _lastSentLen = System.Array.Empty<float>();
            ResetControlTracks();
            _haveTargets = false;
            _rotBuf.Clear();
            _hingePlayback.Clear();
            _sendTimer = 0f;
            _reqTimer = 0f;
            _lastReqIndex = -1;
            _lastReqLength = 0f;
            _lastReqHasWinchRotation = false;
            _lastReqIncoming = false;
            _lastReqTick = 0L;
            _warnedMismatch.Clear();
            _gp = null;
            _fSticky = null;
            _fClicked = null;
            ResetCranks();
        }
    }
}
