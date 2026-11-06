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
            public readonly NetTransform Net = new NetTransform { SmoothUnderruns = true };
            /// <summary>The boat's body: on the hull itself or, as in game 0.39, on the hull's parent
            /// (the object with <c>BoatProbes</c> and <c>BoatDamage</c>). This is what is placed — the
            /// hull rides on it — so the body, its colliders and the hull never part.</summary>
            public Rigidbody Rb;
            /// <summary>False when another boat of this index set already drives the same body (two
            /// hulls under one root): only one owner places it and restores it.</summary>
            public bool OwnsBody;
            public bool PrevKinematic;
            public RigidbodyInterpolation PrevInterp;
            public Component PhysSwitcher;
            public bool PrevPaused;
            public bool PrevSwitcherEnabled;
            /// <summary>The host's motion, re-set on the kinematic body after every physics step: the
            /// game reads it for the wake, foam, hull sounds and apparent wind.</summary>
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public Quaternion LastBodyRot;
            public bool HaveLastRot;
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

        private readonly HashSet<ushort> _warnedUnknown = new HashSet<ushort>();
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

        /// <summary>No boat moves this fast (m/s); a sample-to-sample speed above it is a teleport.</summary>
        private const float TeleportSpeed = 200f;

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
            _sendTimer %= interval;

            long tick = _net.Clock.ServerTick;
            foreach (var hb in _hostBoats.Values)
            {
                if (hb.Boat == null || ShipyardSync.Suspended(hb.Index)) { hb.HaveLast = false; continue; }

                Vector3 real = CoordSpace.LocalToReal(hb.Boat.position);
                Vector3 vel = Vector3.zero;
                if (hb.HaveLast)
                {
                    float secs = (tick - hb.LastRealTick) / 1000f;
                    if (secs > 0.0001f) vel = (real - hb.LastRealPos) / secs;
                    // A teleport, not motion: a client that runs past this sample would
                    // extrapolate the boat by the whole jump.
                    if (vel.sqrMagnitude > TeleportSpeed * TeleportSpeed) vel = Vector3.zero;
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
            // The main menu shares the world scene; don't slave boats before the host's save is loaded.
            if (!InWorld()) return;

            Transform boat = BoatLocator.FindByIndex(msg.BoatIndex);
            if (boat == null)
            {
                if (!_warnedUnknown.Contains(msg.BoatIndex) && BoatLocator.IndicesAuthoritative)
                {
                    _warnedUnknown.Add(msg.BoatIndex);
                    Plugin.Logger.LogWarning("[BoatSync] Host streams boat #" + msg.BoatIndex +
                                             " (sceneIndex " + BoatLocator.SceneIndexOf(msg.BoatIndex) +
                                             ") but this world has no such boat - worlds differ?");
                }
                return;
            }

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
                if (cb.Boat == null || !cb.Net.HasData || ShipyardSync.Suspended(cb.Index)) continue;

                // Not slaved yet: keep buffering under vanilla physics until the stream is trustworthy,
                // then take over in one controlled step (see EngageSlave).
                if (!cb.Engaged)
                {
                    if (cb.Net.SampleCount < SamplesBeforeEngage) continue;
                    EngageSlave(cb);
                    continue;   // EngageSlave already applied this frame's pose
                }

                Place(cb);
            }
        }

        /// <summary>
        /// Put the boat where the host's stream says its hull is. The stream carries the HULL's pose,
        /// but the body may sit on the hull's parent (game 0.39), so the body is placed such that the
        /// hull lands on that pose, using the hull's current offset inside the body — the hull, its
        /// colliders and the body stay one rigid object, on the host's place.
        /// </summary>
        private void Place(ClientBoat cb)
        {
            if (!cb.Net.Sample(_net.Clock.ServerTick, out Vector3 hullPos, out Quaternion hullRot, out Vector3 vel))
                return;

            if (cb.Rb == null)
            {
                cb.Boat.position = hullPos;
                cb.Boat.rotation = hullRot;
                return;
            }
            if (!TryClaimBody(cb)) return;

            // Kinematic and not interpolated while we drive the transform directly. With
            // RigidbodyInterpolation.Interpolate the rendered pose is blended between FIXED-step
            // physics poses, not the one written this Update, so it oscillates against our writes
            // with an error proportional to boat velocity — seen as the player/camera bobbing
            // vertically in time with the swell. BoatPhysicsSwitcher re-enables Interpolate every
            // LateUpdate (we disable that component while slaved) and the shipyard sets the body's
            // kinematic flag on its own; BoatHorizon.UpdateKinematic is skipped for a driven body
            // (NpcBoatPatches). This is the belt-and-braces for all three.
            if (!cb.Rb.isKinematic) cb.Rb.isKinematic = true;
            if (cb.Rb.interpolation != RigidbodyInterpolation.None)
                cb.Rb.interpolation = RigidbodyInterpolation.None;

            Transform body = cb.Rb.transform;
            Vector3 hullOffset = Quaternion.Inverse(body.rotation) * (cb.Boat.position - body.position);
            Quaternion hullInBody = Quaternion.Inverse(body.rotation) * cb.Boat.rotation;
            Quaternion bodyRot = hullRot * Quaternion.Inverse(hullInBody);
            body.SetPositionAndRotation(hullPos - bodyRot * hullOffset, bodyRot);

            // RealVel is per second of the host's clock (real time); a body's velocity is per second of
            // game time. They differ under a shared sleep's timeScale 16, which both machines run.
            cb.Velocity = vel / Mathf.Max(Time.timeScale, 0.0001f);
            cb.AngularVelocity = AngularVelocity(cb, bodyRot);
            SetMotion(cb);
        }

        /// <summary>A rotation step faster than this (rad/s) is a teleport, not a turn.</summary>
        private const float MaxAngularSpeed = 10f;

        private static Vector3 AngularVelocity(ClientBoat cb, Quaternion bodyRot)
        {
            Vector3 w = Vector3.zero;
            float dt = Time.deltaTime;
            if (cb.HaveLastRot && dt > 0.0001f)
            {
                (bodyRot * Quaternion.Inverse(cb.LastBodyRot)).ToAngleAxis(out float degrees, out Vector3 axis);
                if (degrees > 180f) degrees -= 360f;
                w = axis * (degrees * Mathf.Deg2Rad / dt);
                if (float.IsNaN(w.x) || float.IsInfinity(w.x) || w.sqrMagnitude > MaxAngularSpeed * MaxAngularSpeed)
                    w = Vector3.zero;
            }
            cb.LastBodyRot = bodyRot;
            cb.HaveLastRot = true;
            return w;
        }

        /// <summary>A kinematic body reports zero velocity after every physics step, and a velocity set
        /// on it moves nothing (checked in game 0.39). It is set again after each step, so every
        /// reader in FixedUpdate, Update and LateUpdate sees the host's motion.</summary>
        private static void SetMotion(ClientBoat cb)
        {
            if (cb.Rb == null || !cb.OwnsBody || !cb.Rb.isKinematic) return;
            cb.Rb.velocity = cb.Velocity;
            cb.Rb.angularVelocity = cb.AngularVelocity;
        }

        /// <summary>Client: called after every physics step (<c>WaitForFixedUpdate</c>).</summary>
        public void AfterPhysicsStep()
        {
            if (_net.Role != Role.Client) return;
            foreach (var cb in _clientBoats.Values)
                if (cb.Engaged && cb.Boat != null && !ShipyardSync.Suspended(cb.Index)) SetMotion(cb);
        }

        /// <summary>Hits and water over the side that <see cref="BoatDamagePatches"/> kept from a
        /// client's copy of a boat in this session.</summary>
        public static int IgnoredImpacts, IgnoredOverflows;

        /// <summary>Client: is <paramref name="boatRoot"/> (the object with <c>BoatDamage</c>) the
        /// root of a hull placed from the host's stream.</summary>
        public bool Drives(Transform boatRoot)
        {
            if (boatRoot == null || _net.Role != Role.Client) return false;
            foreach (var cb in _clientBoats.Values)
                if (cb.Engaged && cb.Boat != null && cb.Boat.IsChildOf(boatRoot)) return true;
            return false;
        }

        /// <summary>Client: is <paramref name="body"/> a boat body placed from the host's stream. Its
        /// kinematic flag is ours, not <c>BoatHorizon</c>'s.</summary>
        public bool DrivesBody(Rigidbody body)
        {
            if (body == null || _net.Role != Role.Client) return false;
            foreach (var cb in _clientBoats.Values)
                if (cb.Engaged && cb.Rb == body) return true;
            return false;
        }

        /// <summary>One line per placed boat for the debug panel.</summary>
        public void DescribeClientBoats(List<string> lines)
        {
            foreach (var cb in _clientBoats.Values)
            {
                if (!cb.Engaged || cb.Boat == null) continue;
                string line = "#" + cb.Index + " " + cb.Boat.name + ": ";
                if (cb.Rb == null) line += "no body, hull placed";
                else
                    line += (cb.Rb.transform == cb.Boat ? "hull" : "root '" + cb.Rb.name + "'") +
                            (cb.OwnsBody ? "" : " (driven by another hull)") + ", " +
                            (cb.Rb.isKinematic ? "kinematic" : "dynamic") + ", interp " + cb.Rb.interpolation +
                            ", speed " + cb.Rb.velocity.magnitude.ToString("F1") + " m/s";
                lines.Add(line);
            }
        }

        private static bool InWorld()
        {
            try { return GameState.playing && !GameState.currentlyLoading; }
            catch { return false; }
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

            // Includes boats hidden for distance: their pose is still valid.
            var boats = BoatLocator.FindBoats();
            if (!BoatLocator.IndicesAuthoritative) return;

            var seen = new HashSet<ushort>();
            for (int i = 0; i < boats.Count; i++)
            {
                var boat = boats[i];
                if (boat == null) continue;
                ushort idx = BoatLocator.IndexOf(boat);
                if (idx == BoatLocator.NoBoat) continue;
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

            // The body is on the hull or, in game 0.39, on its parent. It is the body that is placed,
            // so it must also stop answering to this machine's physics.
            cb.Rb = FindBody(boat);
            cb.OwnsBody = false;
            cb.HaveLastRot = false;
            cb.Velocity = cb.AngularVelocity = Vector3.zero;
            TryClaimBody(cb);
            cb.Engaged = true;

            Place(cb);

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
                                  ", body=" + (cb.Rb == null ? "none" : cb.Rb.transform == boat ? "hull" : "'" + cb.Rb.name + "'") +
                                  (cb.Rb != null && !cb.OwnsBody ? " (driven by another hull)" : "") +
                                  ", physSwitcher=" + (cb.PhysSwitcher != null));
            if (correction > 2000f)
                Plugin.Logger.LogWarning("[BoatSync] Boat #" + cb.Index + " correction is " +
                                         correction.ToString("F0") + " m - the client very likely loaded a " +
                                         "different world than the host's.");
        }

        /// <summary>
        /// The nearest <c>Rigidbody</c> on the hull or above it. Walked by hand because
        /// <c>GetComponentInParent</c> skips inactive objects, and <c>BoatHorizonPerformanceSwitcher</c>
        /// deactivates exactly that object — the body — on a distant boat, which is still engaged.
        /// </summary>
        private static Rigidbody FindBody(Transform hull)
        {
            for (Transform t = hull; t != null; t = t.parent)
            {
                var rb = t.GetComponent<Rigidbody>();
                if (rb != null) return rb;
            }
            return null;
        }

        /// <summary>Take the body over unless another engaged hull already drives it. Also run from
        /// <see cref="Place"/>, so a second hull under the same body takes over when the first is released.</summary>
        private bool TryClaimBody(ClientBoat cb)
        {
            if (cb.OwnsBody) return true;
            if (cb.Rb == null || BodyDrivenByOther(cb)) return false;
            cb.OwnsBody = true;
            cb.PrevKinematic = cb.Rb.isKinematic;
            cb.PrevInterp = cb.Rb.interpolation;
            cb.Rb.isKinematic = true;
            cb.Rb.interpolation = RigidbodyInterpolation.None;
            TrySetPhysicsPaused(cb, true);
            TrySetSwitcherEnabled(cb, slave: true);
            return true;
        }

        private bool BodyDrivenByOther(ClientBoat cb)
        {
            foreach (var other in _clientBoats.Values)
                if (other != cb && other.Engaged && other.OwnsBody && other.Rb == cb.Rb) return true;
            return false;
        }

        private void RestoreClientBoat(ClientBoat cb)
        {
            if (cb == null) return;

            // Nothing was taken over if we never engaged — restoring would clobber vanilla state.
            if (cb.Engaged)
            {
                if (cb.Rb != null && cb.OwnsBody)
                {
                    cb.Rb.isKinematic = cb.PrevKinematic;
                    cb.Rb.interpolation = cb.PrevInterp;
                    // The hull was never moved inside the body, so body and hull are still one. A body
                    // that was dynamic when taken sails on from the host's last motion; one that was
                    // kinematic then (BoatHorizon's call at that moment) starts from rest once
                    // BoatHorizon frees it, as in vanilla.
                    if (!cb.Rb.isKinematic)
                    {
                        cb.Rb.velocity = cb.Velocity;
                        cb.Rb.angularVelocity = cb.AngularVelocity;
                    }
                    TrySetSwitcherEnabled(cb, slave: false);
                    TrySetPhysicsPaused(cb, false, restore: true);
                }
                cb.Engaged = false;
            }

            cb.Net.Clear();
            cb.Rb = null;
            cb.OwnsBody = false;
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
                var beh = FindSwitcher(cb) as Behaviour;
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

        /// <summary>
        /// <c>BoatPhysicsSwitcher</c> sits beside the body (game 0.39: on the hull's parent, where
        /// <c>BoatHorizon</c> reads it), so it is looked up on the body first and under the hull only
        /// as a fallback.
        /// </summary>
        private static Component FindSwitcher(ClientBoat cb)
        {
            if (cb.PhysSwitcher != null) return cb.PhysSwitcher;
            if (_physSwitcherType == null)
                _physSwitcherType = Type.GetType("BoatPhysicsSwitcher, Assembly-CSharp");
            if (_physSwitcherType == null) return null;
            Component found = cb.Rb != null ? cb.Rb.GetComponent(_physSwitcherType) : null;
            if (found == null) found = cb.Boat.GetComponentInChildren(_physSwitcherType);
            cb.PhysSwitcher = found;
            return found;
        }

        private void TrySetPhysicsPaused(ClientBoat cb, bool paused, bool restore = false)
        {
            try
            {
                if (cb == null || cb.Boat == null) return;
                if (FindSwitcher(cb) == null) return;

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
            _warnedUnknown.Clear();
            IgnoredImpacts = IgnoredOverflows = 0;
            _firstBoatNetId = 0;
            _sendTimer = 0f;
            _refreshTimer = 0f;
            // The session (and possibly the loaded world) is going away — never serve a stale enumeration.
            BoatLocator.Invalidate();
        }
    }
}
