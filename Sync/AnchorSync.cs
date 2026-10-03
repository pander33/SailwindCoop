using System;
using System.Reflection;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Host physics when free; the actual holder supplies the hand pose.
    /// Resolve through the rope's serialized joint: Anchor.Awake reparents the anchor above the hull.</summary>
    public sealed class AnchorSync
    {
        public static AnchorSync Instance { get; private set; }
        private readonly CoopNet _net;
        private readonly BoatContexts<AnchorSync> _fleet;
        private readonly Transform _boat;
        private readonly ushort _boatId = BoatLocator.NoBoat;
        private readonly uint _layoutHash;
        private readonly RopeControllerAnchor _rope;
        private readonly Anchor _anchor;
        private readonly Rigidbody _body;
        private readonly CapsuleCollider _collider;
        private readonly NetTransform _slave = new NetTransform();
        private readonly AnchorStateGate _gate = new AnchorStateGate();
        private static readonly FieldInfo SetField = typeof(Anchor).GetField("set", BindingFlags.Instance | BindingFlags.NonPublic);
        private uint _holder, _nextRequest, _revision;
        private float _sendTimer;
        private bool _slaved, _previousKinematic, _previousTrigger;
        private RigidbodyInterpolation _previousInterpolation;
        private CoordFrame _frame;
        private bool _haveFrame, _haveLast;
        private Vector3 _lastPos;
        private long _lastTick;
        public float SnapshotHz = 12f;
        private const float DeployedLength = 0.5f;

        public AnchorSync(CoopNet net)
        {
            _net = net;
            Instance = this;
            _fleet = new BoatContexts<AnchorSync>((boat, id) => new AnchorSync(net, boat, id), c => c.Clear(),
                BindingStamp);
        }

        private static long BindingStamp(Transform boat)
        {
            var ropes = boat.GetComponentsInChildren<RopeControllerAnchor>(true);
            var anchors = new Component[ropes.Length];
            for (int i = 0; i < ropes.Length; i++)
                anchors[i] = ropes[i].joint != null ? ropes[i].joint.GetComponent<Anchor>() : null;
            return BoatLayout.Stamp(ropes, anchors);
        }

        private AnchorSync(CoopNet net, Transform boat, ushort id)
        {
            _net = net; _boat = boat; _boatId = id;
            _slave.InterpDelayMs = net.Role == Role.Host ? 0f : 100f;
            _rope = boat.GetComponentInChildren<RopeControllerAnchor>(true);
            _anchor = _rope != null && _rope.joint != null ? _rope.joint.GetComponent<Anchor>() : null;
            _body = _anchor != null ? _anchor.GetComponent<Rigidbody>() : null;
            _collider = _anchor != null ? _anchor.GetComponent<CapsuleCollider>() : null;
            _layoutHash = BoatLayout.Hash(boat, boat.GetComponentsInChildren<RopeControllerAnchor>(true));
            Plugin.Logger.LogInfo("[AnchorSync] boat=" + id + " anchor=" + (_anchor != null) + " (rope joint)");
        }

        private bool LocalHeld => _anchor != null && _anchor.held != null;
        public bool HasAnchor
        {
            get { if (_fleet == null) return _anchor != null; foreach (var c in _fleet.Values) if (c.HasAnchor) return true; return false; }
        }
        public bool ClientSet { get; private set; }
        public bool Slaving
        {
            get { if (_fleet == null) return _slaved; foreach (var c in _fleet.Values) if (c.Slaving) return true; return false; }
        }
        public string AnchorText => _fleet != null ? _fleet.Describe(c => "boat " + c._boatId + ": " + c.AnchorText)
            : _anchor == null ? "no anchor" : (LocalHeld ? "local hand" : _holder != 0 ? "hand " + _holder
            : _anchor.IsSet() ? "set" : "free") + (_slaved ? " slaved" : "") + (_gate.HasPending ? " pending" : "");

        private bool Connected => _net.State == LinkState.Connected && GameState.playing && !GameState.currentlyLoading && CoordSpace.Ready;

        public void Tick(float dt)
        {
            if (_fleet != null) { foreach (var c in _fleet.Values) c.Tick(dt); return; }
            if (!Connected || _anchor == null) return;
            if (LocalHeld) RestoreSlaved();
            _sendTimer += dt;
            if (_sendTimer < 1f / Mathf.Max(1f, SnapshotHz)) return;
            _sendTimer = 0f;
            if (_net.Role == Role.Host) SendState();
            else if (_net.Role == Role.Client && LocalHeld) SendRequest(true, false);
        }

        // Called only from actual GoPointer pickup/drop hooks, never from polling or snapshots.
        public void NotifyPickup(Anchor anchor)
        {
            if (_fleet != null) { ForAnchor(anchor)?.NotifyPickup(anchor); return; }
            if (!Connected || anchor != _anchor || !LocalHeld) return;
            RestoreSlaved();
            _holder = _net.MyNetId;
            _slave.Clear(); _haveFrame = false; _haveLast = false;
            MirrorSet(false);
            if (_body != null) { _body.isKinematic = true; _body.drag = _anchor.anchorDrag; }
            if (_net.Role == Role.Client) SendRequest(true, true);
            else if (_net.Role == Role.Host) SendState(reliable: true);
            Plugin.Logger.LogInfo("[AnchorSync] pickup role=" + _net.Role + " boat=" + _boatId);
        }

        public void NotifyDrop(Anchor anchor)
        {
            if (_fleet != null) { ForAnchor(anchor)?.NotifyDrop(anchor); return; }
            if (!Connected || anchor != _anchor) return;
            _holder = 0;
            _slave.Clear(); _haveFrame = false; _haveLast = false;
            if (_collider != null) _collider.isTrigger = false;
            if (_net.Role == Role.Client) { SendRequest(false, true); EnsureSlaved(false); }
            else if (_net.Role == Role.Host)
            {
                RestoreSlaved();
                if (_body != null) _body.isKinematic = _anchor.IsSet();
                SendState(reliable: true);
            }
            Plugin.Logger.LogInfo("[AnchorSync] drop role=" + _net.Role + " boat=" + _boatId);
        }

        private AnchorSync ForAnchor(Anchor anchor)
        {
            if (anchor == null) return null;
            foreach (var c in _fleet.Values) if (c._anchor == anchor) return c;
            return null;
        }

        public bool PreserveRope(RopeControllerAnchor rope)
        {
            if (_fleet != null) { foreach (var c in _fleet.Values) if (c.PreserveRope(rope)) return true; return false; }
            return rope == _rope && (LocalHeld || _gate.HasPending);
        }

        // Vanilla rewrites kinematic/trigger/interpolation each fixed step and can set the anchor.
        // Let it run for a local hand and for free host physics; remote copies are driven here.
        public bool SuppressPhysics(Anchor anchor)
        {
            if (_fleet != null) return ForAnchor(anchor)?.SuppressPhysics(anchor) ?? false;
            return Connected && anchor == _anchor && !LocalHeld && (_net.Role == Role.Client || _holder != 0);
        }

        private void SendRequest(bool held, bool transition)
        {
            uint request = 0;
            if (transition)
            {
                request = unchecked(++_nextRequest);
                if (request == 0) request = unchecked(++_nextRequest);
                _gate.Begin(request);
            }
            bool onDeck = held && GameState.currentBoat == _boat;
            _net.Broadcast(new AnchorRequestMsg
            {
                BoatIndex = _boatId, LayoutHash = _layoutHash, RequestId = request, Held = held,
                Frame = onDeck ? CoordFrame.Boat : CoordFrame.World,
                Pos = onDeck ? _boat.InverseTransformPoint(_anchor.transform.position) : CoordSpace.LocalToReal(_anchor.transform.position),
                Rot = onDeck ? Quaternion.Inverse(_boat.rotation) * _anchor.transform.rotation : _anchor.transform.rotation,
                Vel = !held && _body != null ? _body.velocity : Vector3.zero,
                RopeLength = _rope.currentLength,
            }, DeliveryMethod.ReliableOrdered);
        }

        public void OnAnchorRequest(AnchorRequestMsg msg, NetPeer peer)
        {
            if (_fleet != null)
            {
                if (!Connected || _net.Role != Role.Host || _net.PlayerNetIdForPeer(peer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "AnchorSync")) c.OnAnchorRequest(msg, peer);
                return;
            }
            if (_anchor == null || _rope == null) return;
            uint actor = _net.PlayerNetIdForPeer(peer);
            bool handChanged = _holder != (msg.Held ? actor : 0);
            _holder = msg.Held ? actor : 0;
            _rope.currentLength = msg.RopeLength;
            MirrorSet(false);
            if (_body != null) _body.drag = _anchor.anchorDrag;
            if (handChanged || !msg.Held) { _slave.Clear(); _haveFrame = false; _haveLast = false; }
            Buffer(_net.Clock.ServerTick, msg.Frame, msg.Pos, msg.Rot, msg.Vel);
            if (!LocalHeld)
            {
                EnsureSlaved(msg.Held);
                if (msg.Held) _slave.Apply(_anchor.transform, _net.Clock.ServerTick);
                if (!msg.Held)
                {
                    _slave.Apply(_anchor.transform, _net.Clock.ServerTick);
                    RestoreSlaved();
                    if (_body != null) { _body.isKinematic = false; _body.velocity = msg.Frame == CoordFrame.Boat ? _boat.TransformDirection(msg.Vel) : msg.Vel; }
                }
            }
            if (msg.RequestId != 0)
            {
                // Echo the final requested pose in the reply, not the previous interpolated host pose.
                var reply = CaptureState();
                reply.Frame = msg.Frame; reply.Pos = msg.Pos; reply.Rot = msg.Rot; reply.Vel = msg.Vel;
                reply.RequesterNetId = actor; reply.RequestId = msg.RequestId;
                _net.Broadcast(reply, DeliveryMethod.ReliableOrdered);
                Plugin.Logger.LogInfo("[AnchorSync] request boat=" + _boatId + " actor=" + actor + " held=" + msg.Held + " request=" + msg.RequestId);
            }
        }

        private AnchorStateMsg CaptureState()
        {
            long tick = _net.Clock.ServerTick;
            uint holder = LocalHeld ? _net.MyNetId : _holder;
            bool world = holder != 0
                ? (LocalHeld ? GameState.currentBoat != _boat : _frame != CoordFrame.Boat)
                : _anchor.IsSet() || _rope.currentLength * _rope.maxLength > DeployedLength;
            Vector3 pos = world ? CoordSpace.LocalToReal(_anchor.transform.position) : _boat.InverseTransformPoint(_anchor.transform.position);
            Vector3 vel = Vector3.zero;
            if (world && _haveLast && tick > _lastTick) vel = (pos - _lastPos) / ((tick - _lastTick) / 1000f);
            _lastPos = pos; _lastTick = tick; _haveLast = world;
            return new AnchorStateMsg
            {
                BoatIndex = _boatId, LayoutHash = _layoutHash, Tick = tick,
                Frame = world ? CoordFrame.World : CoordFrame.Boat, Pos = pos,
                Rot = world ? _anchor.transform.rotation : Quaternion.Inverse(_boat.rotation) * _anchor.transform.rotation,
                Vel = vel, Set = _anchor.IsSet(), HolderNetId = holder, RopeLength = _rope.currentLength, Revision = unchecked(++_revision),
            };
        }

        private void SendState(bool reliable = false)
            => _net.Broadcast(CaptureState(), reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

        public void OnAnchorState(AnchorStateMsg msg, NetPeer peer)
        {
            if (_fleet != null)
            {
                if (!Connected || !_net.IsHostPeer(peer)) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "AnchorSync")) c.OnAnchorState(msg, peer);
                return;
            }
            if (_anchor == null || !_gate.Receive(msg.Revision, msg.RequesterNetId, msg.RequestId, _net.MyNetId)) return;
            if (LocalHeld) return;
            bool changed = _holder != msg.HolderNetId;
            _holder = msg.HolderNetId;
            if (changed) { _slave.Clear(); _haveFrame = false; }
            EnsureSlaved(_holder != 0);
            // Free-anchor payout belongs to ControlsSync (which protects a locally held winch).
            if (_holder != 0 || (msg.RequesterNetId == _net.MyNetId && msg.RequestId != 0)) _rope.currentLength = msg.RopeLength;
            MirrorSet(msg.Set);
            Buffer(msg.Tick, msg.Frame, msg.Pos, msg.Rot, msg.Vel);
        }

        private void MirrorSet(bool value)
        {
            ClientSet = value;
            try { SetField?.SetValue(_anchor, value); }
            catch (Exception e) { Plugin.Logger.LogWarning("[AnchorSync] Не удалось установить set лодки " + _boatId + ": " + e.Message); }
        }

        private void Buffer(long tick, CoordFrame frame, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (!_haveFrame || _frame != frame)
            {
                _slave.Clear(); _frame = frame; _haveFrame = true;
                _slave.ToWorldPos = frame == CoordFrame.Boat ? (Func<Vector3, Vector3>)(p => _boat.TransformPoint(p)) : CoordSpace.RealToLocal;
                _slave.ToWorldRot = frame == CoordFrame.Boat ? (Func<Quaternion, Quaternion>)(r => _boat.rotation * r) : r => r;
            }
            _slave.Push(tick, pos, rot, vel);
        }

        public void ApplyRemote()
        {
            if (_fleet != null) { foreach (var c in _fleet.Values) c.ApplyRemote(); return; }
            if (!Connected || LocalHeld || _gate.HasPending || _anchor == null || !_slave.HasData || Time.timeScale <= 0.0001f) return;
            if (_net.Role == Role.Host && _holder == 0) return;
            EnsureSlaved(_holder != 0);
            _slave.Apply(_anchor.transform, _net.Clock.ServerTick);
        }

        private void EnsureSlaved(bool held)
        {
            if (!_slaved)
            {
                if (_body != null) { _previousKinematic = _body.isKinematic; _previousInterpolation = _body.interpolation; }
                if (_collider != null) _previousTrigger = _collider.isTrigger;
                _slaved = true;
            }
            if (_body != null) { _body.isKinematic = true; _body.interpolation = RigidbodyInterpolation.None; }
            if (_collider != null) _collider.isTrigger = held;
        }

        private void RestoreSlaved()
        {
            if (!_slaved) return;
            if (_body != null) { _body.isKinematic = _previousKinematic; _body.interpolation = _previousInterpolation; }
            if (_collider != null) _collider.isTrigger = _previousTrigger;
            _slaved = false;
            _slave.Clear(); _haveFrame = false;
        }

        public void ClearRemoteActor(uint actor)
        {
            if (_fleet != null) { foreach (var c in _fleet.Existing) c.ClearRemoteActor(actor); return; }
            if (_net.Role != Role.Host || actor == 0 || _holder != actor) return;
            _holder = 0; RestoreSlaved(); _haveLast = false;
            if (_body != null) _body.isKinematic = _anchor.IsSet();
            if (Connected && _anchor != null) SendState(reliable: true);
        }

        public void Clear()
        {
            if (_fleet != null) { _fleet.Clear(); return; }
            RestoreSlaved();
            // A remote pickup may have released an anchor that was originally set.
            // Restore interpolation/collision bookkeeping, but keep the current physical set-state.
            if (_body != null && !LocalHeld) _body.isKinematic = _anchor.IsSet();
        }
    }
}
