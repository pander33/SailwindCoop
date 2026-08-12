using System;
using System.Collections.Generic;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Host-authoritative boat transform sync. P2 extends the original single-current-boat
    /// model to all embarkable boats in the loaded save (main ship + dinghies), addressed by
    /// <see cref="BoatLocator"/> index. This keeps the host boat moving for a guest who has
    /// disembarked and gives remote player poses an unambiguous boat-local frame.
    /// </summary>
    public sealed class BoatSync
    {
        private sealed class HostBoat
        {
            public ushort Index;
            public uint NetId;
            public Transform Boat;
            public Vector3 LastRealPos;
            public long LastRealTick;
            public bool HaveLast;
        }

        private sealed class ClientBoat
        {
            public ushort Index;
            public Transform Boat;
            public readonly NetTransform Net = new NetTransform();
            public Rigidbody Rb;
            public bool PrevKinematic;
            public RigidbodyInterpolation PrevInterp;
            public Component PhysSwitcher;
            public bool PrevPaused;
            public bool PrevSwitcherEnabled;
            /// <summary>False while we are only buffering: physics is still vanilla and we apply nothing.</summary>
            public bool Engaged;
        }

        /// <summary>Snapshots to buffer before engaging slave mode. One sample can be arbitrarily stale
        /// (it is whatever arrived first), and acting on it means teleporting the deck to a pose the host
        /// has already left.</summary>
        private const int SamplesBeforeEngage = 2;


        private readonly CoopNet _net;
        private readonly Dictionary<ushort, HostBoat> _hostBoats = new Dictionary<ushort, HostBoat>();
        private readonly Dictionary<ushort, ClientBoat> _clientBoats = new Dictionary<ushort, ClientBoat>();

        private float _sendTimer;
        private float _refreshTimer;
        private uint _firstBoatNetId;
        private static Type _physSwitcherType;

        public float InterpDelayMs = 100f;
        public int SnapshotHz = 20;

        /// <summary>Supplies the player transform that may be REPOSITIONED so the first (potentially
        /// large) boat correction carries them instead of leaving them where the deck used to be. Must
        /// be the real player body — never the camera fallback. Set by the runtime layer.</summary>
        public Func<Transform> LocalPlayerProvider;

        /// <summary>Supplies the deck the local player is standing on right now (null = ashore/unknown).
        /// A null here means "don't touch the player" — never a licence to guess.</summary>
        public Func<Transform> LocalBoatProvider;

        /// <summary>Like <see cref="LocalBoatProvider"/> but forces a fresh resolve instead of reading a
        /// latch that a later step in the frame pipeline maintains. Preferred when it is set.</summary>
        public Func<Transform> ResolveLocalBoatProvider;

        /// <summary>True once at least one boat is actually driven by us (buffering alone doesn't count).</summary>
        public bool IsSlaving
        {
            get
            {
                foreach (var cb in _clientBoats.Values)
                    if (cb.Engaged) return true;
                return false;
            }
        }
        public uint BoatNetId => _firstBoatNetId;
        public int BoatCount => _net.Role == Role.Host ? _hostBoats.Count : _clientBoats.Count;

        public BoatSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: author all embarkable boats
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            RefreshHostBoats(dt);
            if (_hostBoats.Count == 0) return;

            float interval = 1f / Mathf.Max(1, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            long tick = _net.Clock.ServerTick;
            foreach (var hb in _hostBoats.Values)
            {
                if (hb.Boat == null) continue;

                Vector3 real = CoordSpace.LocalToReal(hb.Boat.position);
                Vector3 vel = Vector3.zero;
                if (hb.HaveLast)
                {
                    float secs = (tick - hb.LastRealTick) / 1000f;
                    if (secs > 0.0001f) vel = (real - hb.LastRealPos) / secs;
                }
                hb.LastRealPos = real;
                hb.LastRealTick = tick;
                hb.HaveLast = true;

                _net.Broadcast(new BoatStateMsg
                {
                    NetId = hb.NetId,
                    BoatIndex = hb.Index,
                    Tick = tick,
                    RealPos = real,
                    Rot = hb.Boat.rotation,
                    RealVel = vel,
                }, LiteNetLib.DeliveryMethod.Unreliable);
            }
        }

        // -----------------------------------------------------------------
        // Client: receive/apply boat poses
        // -----------------------------------------------------------------

        public void OnBoatState(BoatStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;

            Transform boat = BoatLocator.FindByIndex(msg.BoatIndex);
            if (boat == null) return;

            var cb = EnsureClientBoat(msg.BoatIndex, msg.NetId, boat);
            cb.Net.InterpDelayMs = InterpDelayMs;
            cb.Net.Push(msg.Tick, msg.RealPos, msg.Rot, msg.RealVel);
        }

        public void ApplyRemote()
        {
            if (_net.Role != Role.Client) return;
            if (!CoordSpace.Ready) return;
            if (Time.timeScale <= 0.0001f) return;

            foreach (var cb in _clientBoats.Values)
            {
                if (cb.Boat == null || !cb.Net.HasData) continue;

                // Not slaved yet: keep buffering under vanilla physics until the stream is trustworthy,
                // then take over in one controlled step (see EngageSlave).
                if (!cb.Engaged)
                {
                    if (cb.Net.SampleCount < SamplesBeforeEngage) continue;
                    EngageSlave(cb);
                    continue;   // EngageSlave already applied this frame's pose
                }

                // Keep render interpolation OFF while we drive the transform directly: with
                // RigidbodyInterpolation.Interpolate the rendered pose is blended between FIXED-step
                // physics poses, not the one written this Update, so it oscillates against our writes
                // with an error proportional to boat velocity — seen as the player/camera bobbing
                // vertically in time with the swell. BoatPhysicsSwitcher re-enables Interpolate every
                // LateUpdate (we disable that component while slaved), this is the belt-and-braces.
                if (cb.Rb != null && cb.Rb.interpolation != RigidbodyInterpolation.None)
                    cb.Rb.interpolation = RigidbodyInterpolation.None;
                cb.Net.Apply(cb.Boat, _net.Clock.ServerTick);
            }
        }

        public Transform GetBoatByIndex(ushort index)
        {
            if (_net.Role == Role.Client && _clientBoats.TryGetValue(index, out var cb) && cb.Boat != null)
                return cb.Boat;
            if (_net.Role == Role.Host && _hostBoats.TryGetValue(index, out var hb) && hb.Boat != null)
                return hb.Boat;
            return BoatLocator.FindByIndex(index);
        }

        // -----------------------------------------------------------------
        // Host boat discovery
        // -----------------------------------------------------------------

        private void RefreshHostBoats(float dt)
        {
            _refreshTimer += dt;
            if (_refreshTimer < 1f && _hostBoats.Count > 0) return;
            _refreshTimer = 0f;

            var boats = BoatLocator.FindBoats();
            // The host publishes these positions as wire indices and allocates a NetId per position, so
            // registering a partial set is worse here than on the client: every guest would inherit the
            // wrong numbering. Wait for the same stability the lookups wait for. FindBoats() above still
            // runs — it is what advances the stability run.
            if (!BoatLocator.IndicesAuthoritative) return;

            var seen = new HashSet<ushort>();
            for (int i = 0; i < boats.Count && i <= ushort.MaxValue - 1; i++)
            {
                var boat = boats[i];
                if (boat == null) continue;
                ushort idx = (ushort)i;
                seen.Add(idx);

                if (!_hostBoats.TryGetValue(idx, out var hb))
                {
                    hb = new HostBoat
                    {
                        Index = idx,
                        NetId = _net.Registry.AllocateId(),
                        Boat = boat,
                    };
                    _hostBoats[idx] = hb;
                    _net.Registry.Register(hb.NetId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
                    if (_firstBoatNetId == 0) _firstBoatNetId = hb.NetId;
                    Plugin.Logger.LogInfo("[BoatSync] Boat #" + idx + " registered NetId=" + hb.NetId +
                                          " ('" + BoatLocator.PathOf(boat) + "')");
                }
                else if (hb.Boat != boat)
                {
                    hb.Boat = boat;
                    hb.HaveLast = false;
                    _net.Registry.Register(hb.NetId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
                    Plugin.Logger.LogInfo("[BoatSync] Boat #" + idx + " rebound ('" + BoatLocator.PathOf(boat) + "')");
                }
            }

            var remove = new List<ushort>();
            foreach (var kv in _hostBoats)
                if (!seen.Contains(kv.Key)) remove.Add(kv.Key);
            foreach (ushort idx in remove)
            {
                _net.Registry.Remove(_hostBoats[idx].NetId);
                _hostBoats.Remove(idx);
            }
        }

        // -----------------------------------------------------------------
        // Client slave / restore
        // -----------------------------------------------------------------

        private ClientBoat EnsureClientBoat(ushort index, uint netId, Transform boat)
        {
            if (_clientBoats.TryGetValue(index, out var cb))
            {
                if (cb.Boat == boat) return cb;
                RestoreClientBoat(cb);
                _clientBoats.Remove(index);
            }

            // Physics is deliberately left vanilla here — we only start buffering. Taking authority is
            // deferred to EngageSlave once the stream is worth acting on.
            cb = new ClientBoat { Index = index, Boat = boat };
            _clientBoats[index] = cb;
            _net.Registry.Register(netId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
            if (_firstBoatNetId == 0) _firstBoatNetId = netId;

            Plugin.Logger.LogInfo("[BoatSync] Client boat #" + index + " tracked: NetId=" + netId +
                                  " ('" + BoatLocator.PathOf(boat) + "'), buffering before slave mode");
            return cb;
        }

        /// <summary>
        /// Take authority over a client boat and perform the FIRST correction as one controlled step.
        ///
        /// This is the frame that used to drop guests through the deck on join. The client's freshly
        /// loaded boat sits wherever its save put it, the host's is somewhere else entirely, and the
        /// first <see cref="NetTransform.Apply"/> closes that gap in a single frame. Two things go
        /// wrong with a naive teleport: anything standing on the deck but not parented to it is left
        /// behind, and a direct transform write on a kinematic rigidbody does not refresh the collider's
        /// pose in PhysX until the next physics step — so for one frame the deck's collision geometry is
        /// still at the old place and a character controller falls straight through it.
        ///
        /// So: capture the player's deck-local pose, engage, apply, put the player back on the same spot
        /// of the (now moved) deck, and force a physics sync before anything can query colliders.
        /// </summary>
        private void EngageSlave(ClientBoat cb)
        {
            Transform boat = cb.Boat;

            // Only ever move the player when PlayerSync has POSITIVELY confirmed they are standing on
            // this very deck — never by proximity, which dragged guests off piers and out of the water.
            //
            // The confirmation is requested on demand rather than read from the latch, because the latch
            // is refreshed in Players.Tick (step 19 of the frame) while this runs from Boats.ApplyRemote
            // (step 3): on a fresh join the boat engages before Tick has ever latched anything, so
            // reading the latch alone would leave carry permanently false on the one frame it exists for.
            // Resolve the deck FIRST: that call is what forces PlayerSync to find the embarker this
            // frame, and the player transform hangs off the same embarker. Read in the other order and
            // the player comes back null on the first engage frame — precisely the frame this exists
            // for. (The provider is resolving too, so this ordering is belt-and-braces, not the fix.)
            Transform playerBoat = ResolveLocalBoatProvider != null
                ? ResolveLocalBoatProvider()
                : (LocalBoatProvider != null ? LocalBoatProvider() : null);
            Transform player = LocalPlayerProvider != null ? LocalPlayerProvider() : null;
            bool carry = player != null && playerBoat != null && playerBoat == boat;
            Vector3 playerLocalPos = Vector3.zero;
            Quaternion playerLocalRot = Quaternion.identity;
            if (carry)
            {
                playerLocalPos = boat.InverseTransformPoint(player.position);
                playerLocalRot = Quaternion.Inverse(boat.rotation) * player.rotation;
            }

            Vector3 posBefore = boat.position;

            cb.Rb = boat.GetComponent<Rigidbody>();
            if (cb.Rb != null)
            {
                cb.PrevKinematic = cb.Rb.isKinematic;
                cb.PrevInterp = cb.Rb.interpolation;
                cb.Rb.isKinematic = true;
                cb.Rb.interpolation = RigidbodyInterpolation.None;
            }

            TrySetPhysicsPaused(cb, true);
            TrySetSwitcherEnabled(cb, slave: true);
            cb.Engaged = true;

            cb.Net.Apply(boat, _net.Clock.ServerTick);

            if (carry)
            {
                player.position = boat.TransformPoint(playerLocalPos);
                player.rotation = boat.rotation * playerLocalRot;
            }

            // Push the new transforms into PhysX now: without this the deck's colliders stay at the old
            // pose until the next FixedUpdate, which is exactly the window a player falls through.
            Physics.SyncTransforms();

            float correction = Vector3.Distance(posBefore, boat.position);
            Plugin.Logger.LogInfo("[BoatSync] Client boat #" + cb.Index + " in slave mode: correction=" +
                                  correction.ToString("F1") + " m, carriedPlayer=" + carry +
                                  ", rb=" + (cb.Rb != null) + ", physSwitcher=" + (cb.PhysSwitcher != null));
            if (correction > 2000f)
                Plugin.Logger.LogWarning("[BoatSync] Boat #" + cb.Index + " correction is " +
                                         correction.ToString("F0") + " m - the client very likely loaded a " +
                                         "different world than the host's.");
        }

        private void RestoreClientBoat(ClientBoat cb)
        {
            if (cb == null) return;

            // Nothing was taken over if we never engaged — restoring would clobber vanilla state.
            if (cb.Engaged)
            {
                if (cb.Rb != null)
                {
                    cb.Rb.isKinematic = cb.PrevKinematic;
                    cb.Rb.interpolation = cb.PrevInterp;
                }

                TrySetSwitcherEnabled(cb, slave: false);
                TrySetPhysicsPaused(cb, false, restore: true);
                cb.Engaged = false;
            }

            cb.Net.Clear();
            cb.Rb = null;
            cb.PhysSwitcher = null;
            cb.Boat = null;
        }

        /// <summary>
        /// BoatPhysicsSwitcher.LateUpdate unconditionally re-enables Rigidbody interpolation every
        /// frame (our one-time interpolation=None in EnsureClientBoat never sticks). While the boat
        /// is slaved we disable the whole component — with isKinematic=true its pause/restore logic
        /// is a no-op anyway — and restore its enabled state on release.
        /// </summary>
        private void TrySetSwitcherEnabled(ClientBoat cb, bool slave)
        {
            try
            {
                if (cb == null || cb.Boat == null) return;
                if (cb.PhysSwitcher == null)
                {
                    if (_physSwitcherType == null)
                        _physSwitcherType = Type.GetType("BoatPhysicsSwitcher, Assembly-CSharp");
                    if (_physSwitcherType == null) return;
                    cb.PhysSwitcher = cb.Boat.GetComponentInChildren(_physSwitcherType);
                }
                var beh = cb.PhysSwitcher as Behaviour;
                if (beh == null) return;

                if (slave)
                {
                    cb.PrevSwitcherEnabled = beh.enabled;
                    beh.enabled = false;
                }
                else
                {
                    beh.enabled = cb.PrevSwitcherEnabled;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatSync] BoatPhysicsSwitcher enable-toggle failed: " + e.Message);
            }
        }

        private void TrySetPhysicsPaused(ClientBoat cb, bool paused, bool restore = false)
        {
            try
            {
                if (cb == null || cb.Boat == null) return;
                if (_physSwitcherType == null)
                    _physSwitcherType = Type.GetType("BoatPhysicsSwitcher, Assembly-CSharp");
                if (_physSwitcherType == null) return;

                if (cb.PhysSwitcher == null)
                    cb.PhysSwitcher = cb.Boat.GetComponentInChildren(_physSwitcherType);
                if (cb.PhysSwitcher == null) return;

                var field = _physSwitcherType.GetField("paused");
                var prop = field == null ? _physSwitcherType.GetProperty("paused") : null;
                if (field == null && prop == null) return;

                if (!restore)
                    cb.PrevPaused = field != null
                        ? (bool)field.GetValue(cb.PhysSwitcher)
                        : (bool)prop.GetValue(cb.PhysSwitcher, null);

                bool target = restore ? cb.PrevPaused : paused;
                if (field != null) field.SetValue(cb.PhysSwitcher, target);
                else prop.SetValue(cb.PhysSwitcher, target, null);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatSync] BoatPhysicsSwitcher.paused unavailable: " + e.Message);
            }
        }

        public void Clear()
        {
            foreach (var cb in _clientBoats.Values)
                RestoreClientBoat(cb);
            _clientBoats.Clear();
            _hostBoats.Clear();
            _firstBoatNetId = 0;
            _sendTimer = 0f;
            _refreshTimer = 0f;
            // The session (and possibly the loaded world) is going away — never serve a stale enumeration.
            BoatLocator.Invalidate();
        }
    }
}
