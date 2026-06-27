using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 2 — replicate the boat anchor. Paying the anchor in/out is already synced as the
    /// anchor rope's <c>currentLength</c> (ControlState), and the boat-hold effect comes for free
    /// because the client's boat follows the host (BoatSync). What's left is the anchor's own
    /// <b>pose</b> and its discrete <b>set</b> state: on the client the anchor is a free rigidbody
    /// jointed to a kinematic boat, so its physics drift from the host's.
    ///
    /// <para>We slave it exactly like the boat: make the anchor rigidbody kinematic and drive its
    /// transform from the host through a <see cref="NetTransform"/>. The host sends boat-local pose
    /// while stowed (so it rides the deck without interp lag) and real-space pose once deployed/set
    /// (so it stays put on the seabed while the boat swings). <c>Anchor.set</c> is mirrored for any
    /// UI/indicator that reads <c>IsSet()</c>.</para>
    /// </summary>
    public sealed class AnchorSync
    {
        private readonly CoopNet _net;
        private readonly NetTransform _slave = new NetTransform();

        private PlayerEmbarkerNew _emb;
        private Transform _cachedBoat;
        private Anchor _anchor;
        private Transform _anchorTr;

        // Reflection into Anchor internals (rigidbody + set flag are not all public).
        private static FieldInfo _fBody, _fSet;

        // Host outbound velocity estimation (real space).
        private float _sendTimer;
        private Vector3 _lastRealPos;
        private long _lastRealTick;
        private bool _haveLast;
        private CoordFrame _lastFrame = CoordFrame.World;

        // Client slave bookkeeping.
        private Rigidbody _anchorRb;
        private bool _prevKinematic;
        private RigidbodyInterpolation _prevInterp;
        private bool _slaved;
        private CoordFrame _curFrame = CoordFrame.World;
        private bool _haveFrame;

        public float SnapshotHz = 12f;
        /// <summary>Rope length (m) past which the anchor counts as deployed (world frame), not stowed.</summary>
        private const float DeployedLen = 0.5f;

        public bool HasAnchor => _anchor != null;
        public bool ClientSet { get; private set; }
        public bool Slaving => _slaved;

        public string AnchorText
        {
            get
            {
                if (_anchor == null) return "нет якоря";
                if (_net.Role == Role.Host) return _anchor.IsSet() ? "хост: уложен" : "хост: поднят";
                return (_slaved ? "ведомый" : "—") + (ClientSet ? " уложен" : " поднят") +
                       " [" + _curFrame + "]";
            }
        }

        public AnchorSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: author the anchor pose + set-state
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            RefreshAnchor();
            if (_anchor == null || _anchorTr == null) return;
            Transform boat = _cachedBoat;
            if (boat == null) return;

            float interval = 1f / Mathf.Max(1f, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            long tick = _net.Clock.ServerTick;
            bool deployed = _anchor.IsSet() || GetRopeLen() > DeployedLen;
            CoordFrame frame = deployed ? CoordFrame.World : CoordFrame.Boat;

            Vector3 pos; Quaternion rot; Vector3 vel = Vector3.zero;
            if (frame == CoordFrame.World)
            {
                pos = CoordSpace.LocalToReal(_anchorTr.position);
                rot = _anchorTr.rotation;
                if (_haveLast && _lastFrame == CoordFrame.World)
                {
                    float secs = (tick - _lastRealTick) / 1000f;
                    if (secs > 0.0001f) vel = (pos - _lastRealPos) / secs;
                }
                _lastRealPos = pos;
                _lastRealTick = tick;
                _haveLast = true;
            }
            else // boat-local (stowed)
            {
                pos = boat.InverseTransformPoint(_anchorTr.position);
                rot = Quaternion.Inverse(boat.rotation) * _anchorTr.rotation;
                _haveLast = false;
            }
            _lastFrame = frame;

            _net.Broadcast(new AnchorStateMsg
            {
                Tick = tick,
                Frame = frame,
                Pos = pos,
                Rot = rot,
                Vel = vel,
                Set = _anchor.IsSet(),
            }, LiteNetLib.DeliveryMethod.Unreliable);
        }

        // -----------------------------------------------------------------
        // Client: receive
        // -----------------------------------------------------------------

        public void OnAnchorState(AnchorStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            RefreshAnchor();
            if (_anchor == null || _anchorTr == null) return;

            EnsureSlaved();

            // Switch coordinate frame if it changed (stow <-> deploy); a clean break avoids
            // interpolating a boat-local sample against a real-space one.
            if (!_haveFrame || msg.Frame != _curFrame)
            {
                _curFrame = msg.Frame;
                _haveFrame = true;
                _slave.Clear();
                ApplyFrameConverters();
            }

            _slave.Push(msg.Tick, msg.Pos, msg.Rot, msg.Vel);

            // Mirror the discrete set-state for any UI/indicator reading IsSet().
            ClientSet = msg.Set;
            if (_fSet != null) { try { _fSet.SetValue(_anchor, msg.Set); } catch { } }
        }

        /// <summary>Client per-frame: drive the slaved anchor from its interpolation buffer.</summary>
        public void ApplyRemote()
        {
            if (_net.Role != Role.Client) return;
            if (!_slaved || _anchorTr == null || !_slave.HasData) return;
            if (!CoordSpace.Ready) return;
            if (Time.timeScale <= 0.0001f) return;   // freeze while paused (same reason as BoatSync)

            _slave.Apply(_anchorTr, _net.Clock.ServerTick);
        }

        // -----------------------------------------------------------------
        // Slave / restore the client's anchor physics
        // -----------------------------------------------------------------

        private void EnsureSlaved()
        {
            if (_slaved || _anchor == null) return;

            _anchorRb = GetAnchorBody();
            if (_anchorRb != null)
            {
                _prevKinematic = _anchorRb.isKinematic;
                _prevInterp = _anchorRb.interpolation;
                _anchorRb.isKinematic = true;
                _anchorRb.interpolation = RigidbodyInterpolation.None;
            }
            _slaved = true;
            Plugin.Logger.LogInfo("[AnchorSync] Якорь клиента в ведомом режиме (rb=" + (_anchorRb != null) + ")");
        }

        private void RestoreSlaved()
        {
            if (_anchorRb != null)
            {
                _anchorRb.isKinematic = _prevKinematic;
                _anchorRb.interpolation = _prevInterp;
            }
            _anchorRb = null;
            _slaved = false;
            _haveFrame = false;
            _slave.Clear();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void RefreshAnchor()
        {
            if (_emb == null) _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat == _cachedBoat) return;

            RestoreSlaved();
            _cachedBoat = boat;
            _anchor = boat != null ? boat.GetComponentInChildren<Anchor>(true) : null;
            _anchorTr = _anchor != null ? _anchor.transform : null;
            _haveLast = false;

            if (_anchor != null)
            {
                if (_fBody == null) _fBody = typeof(Anchor).GetField("body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fSet == null) _fSet = typeof(Anchor).GetField("set", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Plugin.Logger.LogInfo("[AnchorSync] Якорь найден на '" + (boat != null ? boat.name : "?") + "'");
            }
        }

        private Rigidbody GetAnchorBody()
        {
            try
            {
                if (_fBody != null && _fBody.GetValue(_anchor) is Rigidbody rb && rb != null) return rb;
            }
            catch { }
            return _anchorTr != null ? _anchorTr.GetComponent<Rigidbody>() : null;
        }

        private float GetRopeLen()
        {
            try { return _anchor.GetRopeLength(); } catch { return 0f; }
        }

        private void ApplyFrameConverters()
        {
            if (_curFrame == CoordFrame.Boat && _cachedBoat != null)
            {
                Transform boat = _cachedBoat;
                _slave.ToWorldPos = p => boat.TransformPoint(p);
                _slave.ToWorldRot = r => boat.rotation * r;
            }
            else
            {
                _slave.ToWorldPos = CoordSpace.RealToLocal;
                _slave.ToWorldRot = r => r;
            }
        }

        public void Clear()
        {
            RestoreSlaved();
            _cachedBoat = null;
            _anchor = null;
            _anchorTr = null;
            _haveLast = false;
            _sendTimer = 0f;
            ClientSet = false;
        }
    }
}
