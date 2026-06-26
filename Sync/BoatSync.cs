using System;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 1 — the host's controlled boat is replicated to clients. The host
    /// authors the boat's pose in REAL (origin-stable) space at the snapshot rate;
    /// each client makes its own copy of the same ship <b>kinematic</b> and drives
    /// its transform from that stream through a <see cref="NetTransform"/> (smooth
    /// interpolation + bounded extrapolation by velocity).
    ///
    /// Why this works without matching object identity yet: in the "Passenger" model
    /// there is exactly one controlled ship — the one the local player stands on
    /// (<see cref="PlayerEmbarkerNew.debugOutCurrentBoat"/>). The client slaves that
    /// boat. Because the player is a child of the boat, riding the deck comes for
    /// free, and the host's avatar (sent boat-local by <see cref="PlayerSync"/>)
    /// lands correctly on the now-synced deck. The boat carries a host-allocated
    /// <c>NetId</c> (registered both sides) so 2+ ships / ownership extend cleanly.
    /// </summary>
    public sealed class BoatSync
    {
        private readonly CoopNet _net;
        private readonly NetTransform _slave = new NetTransform();   // client-side: drives the slaved boat

        private PlayerEmbarkerNew _emb;

        // Host outbound state.
        private uint _boatNetId;
        private float _sendTimer;
        private Vector3 _lastRealPos;
        private long _lastRealTick;
        private bool _haveLast;

        // Client slave bookkeeping (so we can restore single-player physics on leave).
        private Transform _slavedBoat;
        private Rigidbody _slavedRb;
        private bool _prevKinematic;
        private RigidbodyInterpolation _prevInterp;
        private Component _physSwitcher;
        private bool _prevPaused;
        private static Type _physSwitcherType;

        public float InterpDelayMs = 100f;
        public int SnapshotHz = 20;

        public bool IsSlaving => _slavedBoat != null;
        public uint BoatNetId => _boatNetId;

        public BoatSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: author the boat pose
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host) return;            // only the host sails (Stage 1)
            if (_net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            EnsureEmb();
            Transform boat = CurrentBoat;
            if (boat == null) { _haveLast = false; return; }

            // Allocate + register the boat's NetId once it exists.
            if (_boatNetId == 0)
            {
                _boatNetId = _net.Registry.AllocateId();
                _net.Registry.Register(_boatNetId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
                Plugin.Logger.LogInfo("[BoatSync] Корабль хоста зарегистрирован NetId=" + _boatNetId +
                                      " ('" + boat.name + "')");
            }

            float interval = 1f / Mathf.Max(1, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            long tick = _net.Clock.ServerTick;
            Vector3 real = CoordSpace.LocalToReal(boat.position);
            Quaternion rot = boat.rotation;

            Vector3 vel = Vector3.zero;
            if (_haveLast)
            {
                float secs = (tick - _lastRealTick) / 1000f;
                if (secs > 0.0001f) vel = (real - _lastRealPos) / secs;
            }
            _lastRealPos = real;
            _lastRealTick = tick;
            _haveLast = true;

            _net.Broadcast(new BoatStateMsg
            {
                NetId = _boatNetId,
                Tick = tick,
                RealPos = real,
                Rot = rot,
                RealVel = vel,
            }, LiteNetLib.DeliveryMethod.Unreliable);
        }

        // -----------------------------------------------------------------
        // Client: receive the boat pose
        // -----------------------------------------------------------------

        public void OnBoatState(BoatStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;          // clients don't author boat motion
            EnsureEmb();
            Transform boat = CurrentBoat;
            if (boat == null) return;                      // not aboard yet; ignore until we are

            EnsureSlaved(boat, msg.NetId);
            _slave.InterpDelayMs = InterpDelayMs;
            _slave.Push(msg.Tick, msg.RealPos, msg.Rot, msg.RealVel);
        }

        /// <summary>Client per-frame: drive the slaved boat from its interpolation buffer.</summary>
        public void ApplyRemote()
        {
            if (_net.Role != Role.Client) return;
            if (_slavedBoat == null || !_slave.HasData) return;
            if (!CoordSpace.Ready) return;

            // While the local player has the game paused (ESC menu sets Time.timeScale = 0),
            // the world is meant to be frozen, but our interpolation clock is real-time
            // (Stopwatch) and would keep driving the boat — sailing the camera (a child of
            // the boat) out from under the world-anchored pause panel, so the menu appears
            // to "fly away". Freeze the deck while paused; we re-sync to the host on resume
            // (the interpolation buffer absorbs the catch-up).
            if (Time.timeScale <= 0.0001f) return;

            // Default NetTransform converters = real-space (RealToLocal / identity rot),
            // exactly what a world-frame boat needs; no per-frame converter swap required.
            _slave.Apply(_slavedBoat, _net.Clock.ServerTick);
        }

        // -----------------------------------------------------------------
        // Slave / restore the client's boat physics
        // -----------------------------------------------------------------

        private void EnsureSlaved(Transform boat, uint netId)
        {
            if (_slavedBoat == boat) return;
            RestoreSlaved();                               // release a previous boat, if any

            _slavedBoat = boat;
            _slave.Clear();

            _slavedRb = boat.GetComponent<Rigidbody>();
            if (_slavedRb != null)
            {
                _prevKinematic = _slavedRb.isKinematic;
                _prevInterp = _slavedRb.interpolation;
                _slavedRb.isKinematic = true;              // network drives the transform now
                _slavedRb.interpolation = RigidbodyInterpolation.None;
            }

            TrySetPhysicsPaused(boat, true);

            _net.Registry.Register(netId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
            Plugin.Logger.LogInfo("[BoatSync] Корабль клиента в ведомом режиме: NetId=" + netId +
                                  " ('" + boat.name + "'), rb=" + (_slavedRb != null) +
                                  ", physSwitcher=" + (_physSwitcher != null));
        }

        private void RestoreSlaved()
        {
            if (_slavedBoat == null) return;

            if (_slavedRb != null)
            {
                _slavedRb.isKinematic = _prevKinematic;
                _slavedRb.interpolation = _prevInterp;
            }
            TrySetPhysicsPaused(_slavedBoat, false, restore: true);

            _slavedRb = null;
            _physSwitcher = null;
            _slavedBoat = null;
            _slave.Clear();
        }

        public void Clear()
        {
            RestoreSlaved();
            _boatNetId = 0;
            _haveLast = false;
            _sendTimer = 0f;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void EnsureEmb()
        {
            if (_emb != null) return;
            _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
        }

        /// <summary>The boat the local player currently stands on, or null.</summary>
        private Transform CurrentBoat => _emb != null ? _emb.debugOutCurrentBoat : null;

        /// <summary>
        /// Best-effort: pause/unpause the boat's own physics driver so it doesn't fight the
        /// network-driven transform. Uses reflection so a changed/absent signature only logs,
        /// never breaks the build or the run. The kinematic Rigidbody is the real guarantee;
        /// this is belt-and-suspenders for any script that pushes the transform directly.
        /// </summary>
        private void TrySetPhysicsPaused(Transform boat, bool paused, bool restore = false)
        {
            try
            {
                if (_physSwitcherType == null)
                    _physSwitcherType = Type.GetType("BoatPhysicsSwitcher, Assembly-CSharp");
                if (_physSwitcherType == null) return;

                if (_physSwitcher == null)
                    _physSwitcher = boat.GetComponentInChildren(_physSwitcherType);
                if (_physSwitcher == null) return;

                var field = _physSwitcherType.GetField("paused");
                var prop = field == null ? _physSwitcherType.GetProperty("paused") : null;
                if (field == null && prop == null) return;

                if (!restore)   // remember the original value the first time we touch it
                    _prevPaused = field != null
                        ? (bool)field.GetValue(_physSwitcher)
                        : (bool)prop.GetValue(_physSwitcher, null);

                bool target = restore ? _prevPaused : paused;
                if (field != null) field.SetValue(_physSwitcher, target);
                else prop.SetValue(_physSwitcher, target, null);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatSync] BoatPhysicsSwitcher.paused недоступен: " + e.Message);
            }
        }
    }
}
