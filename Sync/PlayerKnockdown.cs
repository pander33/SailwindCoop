using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The local player knocked over by a thrown item. The machine of the player who is hit decides
    /// it: it sees the item fly into its own player (the pose of an item another player threw is the
    /// one every machine already gets), so nothing new goes over the wire. The fallen player becomes a
    /// capsule <see cref="Rigidbody"/> beside the character controller, in the controller's own frame
    /// (on a boat that is the static walk copy, where the deck does not move), and the controller is
    /// carried with it; the game's mirror puts the observer there. The camera is tilted with the body
    /// only for the frame being drawn. Space gets the player up. The others see the player
    /// lying through the <c>Lying</c> flag of <see cref="PlayerStateMsg"/>.
    /// </summary>
    public sealed class PlayerKnockdown
    {
        // Relative to the item's own frame (its boat, or the world): a mug that rides the deck of a
        // boat sailing past is not flying. Gravity alone makes no hit: a dropped item falls straight.
        private const float MinSpeed = 4f, MinAcross = 3f, MaxSpeed = 40f, WatchMetres = 5f;
        private const float BodyRadius = .35f, ItemRadius = .2f, FeetBelow = .6f;
        private const float OwnReleaseSeconds = 2f, MinDownSeconds = 1f, ImmuneSeconds = 1.5f;
        private const float PushSpeed = 2.5f, TipSpeed = 3f, BodyMass = 70f, LostMetres = 4f, LieAngle = 45f;

        // Pos is where the item last moved to and Time when. A body the physics moves (every item on
        // the host) moves only on frames with a FixedUpdate, so a step is divided by the time it took,
        // not by the frame it shows up in. Seen is the last frame the item was near.
        private struct Track { public Transform Parent; public Vector3 Pos; public float Time; public int Seen; }

        private static readonly FieldInfo ThrowPower = AccessTools.Field(typeof(GoPointer), "currentThrowPower");

        private readonly CoopNet _net;
        private readonly List<ShipItem> _loose = new List<ShipItem>();
        private readonly Dictionary<ShipItem, Track> _tracks = new Dictionary<ShipItem, Track>();
        private readonly List<ShipItem> _stale = new List<ShipItem>();
        private ShipItem _held, _released;
        private float _releasedUntil, _immuneUntil, _downAt;
        private bool _letGo;

        private GameObject _body;
        private Quaternion _bodyRot0;
        private Vector3 _startLocal, _placedLocal;

        private bool _hooked, _moved;
        private Camera _movedCamera;
        private Vector3 _savedPosition;
        private Quaternion _savedRotation;
        private CoopLog.Repeat _report;

        public bool Enabled = true;
        public bool Down => _body != null;

        public PlayerKnockdown(CoopNet net) { _net = net; }

        public void Tick()
        {
            PutBack(_movedCamera);   // a camera that was culled and never drawn
            if (_body != null) { TickDown(); return; }
            var coop = CoopBehaviour.Instance;
            Transform player = coop?.Players?.LocalPlayer;
            Camera cam = Camera.main;
            if (!Enabled || _net.State != LinkState.Connected || !CoordSpace.Ready || !GameState.playing ||
                GameState.currentlyLoading || player == null || cam == null || coop.Items == null)
            {
                _tracks.Clear(); _held = null;
                return;
            }
            WatchOwnRelease(coop.Items);
            float dt = Time.deltaTime;
            if (dt <= 0f) return;   // a stopped world: nothing flies

            coop.Items.CollectLoose(_loose);
            int frame = Time.frameCount;
            float time = Time.time;
            Vector3 up = player.up;
            Vector3 low = player.position - up * (FeetBelow - .2f), high = cam.transform.position;
            bool canFall = Time.time >= _immuneUntil && CanFall(coop);
            Vector3 hitVel = Vector3.zero, hitAt = Vector3.zero;
            bool hit = false;
            foreach (var item in _loose)
            {
                Transform t = item.transform;
                Vector3 now = t.position;
                if ((now - player.position).sqrMagnitude > WatchMetres * WatchMetres) continue;
                Transform parent = t.parent;
                Vector3 framed = parent != null ? parent.InverseTransformPoint(now) : CoordSpace.LocalToReal(now);
                if (!_tracks.TryGetValue(item, out Track last) || last.Parent != parent || last.Seen != frame - 1)
                {
                    _tracks[item] = new Track { Parent = parent, Pos = framed, Time = time, Seen = frame };
                    continue;
                }
                // Where the item was, as its own frame stands now: the velocity is the item's own.
                Vector3 before = parent != null ? parent.TransformPoint(last.Pos) : CoordSpace.RealToLocal(last.Pos);
                float elapsed = time - last.Time;
                if ((now - before).sqrMagnitude < 1e-8f || elapsed <= 0f)
                {
                    last.Seen = frame;   // no physics step this frame: the step is measured when it comes
                    _tracks[item] = last;
                    continue;
                }
                if (canFall && !hit && !(ReferenceEquals(item, _released) && time < _releasedUntil))
                {
                    Vector3 vel = (now - before) / elapsed;
                    float speed = vel.magnitude;
                    if (speed >= MinSpeed && speed <= MaxSpeed && Vector3.ProjectOnPlane(vel, up).magnitude >= MinAcross &&
                        SegmentDistance(before, now, low, high, out Vector3 onBody) <= BodyRadius + ItemRadius)
                    {
                        hit = true; hitVel = vel; hitAt = onBody;
                        Plugin.Logger.LogInfo("[Knockdown] hit by '" + item.name + "' at " + speed.ToString("F1") + " m/s");
                    }
                }
                _tracks[item] = new Track { Parent = parent, Pos = framed, Time = time, Seen = frame };
            }
            Forget(frame);
            if (hit) Fall(player, up, hitVel, hitAt);
        }

        /// <summary>The session or the world is gone: get up where the body lies, and draw nothing more.</summary>
        public void Clear()
        {
            if (_body != null) GetUp(place: true);
            _tracks.Clear(); _held = null; _released = null;
            PutBack(_movedCamera);
            if (_hooked) { Camera.onPreCull -= Show; Camera.onPostRender -= PutBack; _hooked = false; }
        }

        /// <summary>The fallen player's head and the direction to the feet, in the render frame; false
        /// while the player stands or the body is not yet over.</summary>
        internal bool TryLying(out Vector3 head, out Vector3 feetDir)
        {
            head = feetDir = Vector3.zero;
            Transform player = CoopBehaviour.Instance?.Players?.LocalPlayer;
            Camera cam = Camera.main;
            if (_body == null || player == null || cam == null) return false;
            Quaternion frame = player.parent != null ? player.parent.rotation : Quaternion.identity;
            Vector3 bodyUp = frame * (_body.transform.localRotation * Vector3.up);
            if (Vector3.Angle(bodyUp, frame * Vector3.up) < LieAngle) return false;
            Quaternion tilt = Tilt(player);
            head = player.position + tilt * (cam.transform.position - player.position);
            feetDir = -bodyUp;
            return true;
        }

        private void WatchOwnRelease(ItemSync items)
        {
            ShipItem held = items.LocalHeldItem;
            if (_held != null && !ReferenceEquals(held, _held))
            {
                _released = _held;
                _releasedUntil = Time.time + OwnReleaseSeconds;
            }
            _held = held;
        }

        private void Forget(int frame)
        {
            if (_tracks.Count == 0) return;
            _stale.Clear();
            foreach (var kv in _tracks)
                if (kv.Key == null || kv.Value.Seen != frame) _stale.Add(kv.Key);
            foreach (var item in _stale) _tracks.Remove(item);
        }

        private static bool CanFall(CoopBehaviour coop)
        {
            if (Refs.charController == null) return false;
            // Control off: a shop, the map table, a bed, the host's pause. The game holds the player.
            if (!Refs.charController.enabled) return false;
            if (GameState.inBed != null || GameState.sleeping || GameState.inCursorMenu || BoatCamera.on) return false;
            if (PlayerSwimming.swimming) return false;
            if (SleepSync.Instance != null && SleepSync.Instance.ClientAsleep) return false;
            if (coop.HostPause != null && coop.HostPause.Frozen) return false;
            if (coop.Dice?.Input?.CloseUp != null && coop.Dice.Input.CloseUp.Active) return false;
            return true;
        }

        private void Fall(Transform player, Vector3 up, Vector3 vel, Vector3 hitAt)
        {
            try
            {
                Transform ctrl = Refs.charController.transform;
                CharacterController cc = Refs.charController;
                Transform walk = ctrl.parent;
                Refs.SetPlayerControl(false);

                _body = new GameObject("CoopKnockedBody") { layer = ctrl.gameObject.layer };
                Transform body = _body.transform;
                body.SetParent(walk, false);
                body.localPosition = ctrl.localPosition;
                body.localRotation = ctrl.localRotation;
                body.localScale = ctrl.localScale;
                var capsule = _body.AddComponent<CapsuleCollider>();
                capsule.center = cc.center; capsule.height = cc.height; capsule.radius = cc.radius; capsule.direction = 1;
                capsule.material = new PhysicMaterial("CoopKnockedBody")
                {
                    dynamicFriction = .6f, staticFriction = .7f, bounciness = 0f,
                    frictionCombine = PhysicMaterialCombine.Average, bounceCombine = PhysicMaterialCombine.Minimum,
                };
                // Unity refuses (and logs) an ignore pair with a switched-off collider; the controller itself is off.
                foreach (var own in ctrl.GetComponentsInChildren<Collider>())
                    if (own.enabled) Physics.IgnoreCollision(capsule, own);
                var rb = _body.AddComponent<Rigidbody>();
                rb.mass = BodyMass; rb.drag = .1f; rb.angularDrag = 1f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                // From the render frame (where the item flew) to the controller's frame.
                Quaternion toWalk = Rot(walk) * Quaternion.Inverse(Rot(player.parent));
                Vector3 push = Vector3.ProjectOnPlane(vel, up).normalized;
                Vector3 pushWalk = toWalk * push, upWalk = toWalk * up;
                Vector3 atWalk = body.position + toWalk * (hitAt - player.position);
                rb.AddForceAtPosition(pushWalk * PushSpeed * BodyMass, atWalk, ForceMode.Impulse);
                rb.AddTorque(Vector3.Cross(upWalk, pushWalk) * TipSpeed, ForceMode.VelocityChange);

                _bodyRot0 = body.localRotation;
                _startLocal = _placedLocal = ctrl.localPosition;
                _downAt = Time.time;
                _tracks.Clear();
                _letGo = true;
                if (!_hooked) { Camera.onPreCull += Show; Camera.onPostRender += PutBack; _hooked = true; }
                Plugin.Logger.LogInfo("[Knockdown] down; Space gets up");
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[Knockdown] could not knock the player down", e.Message, ref _report);
                if (_body != null) GetUp(place: false);
            }
        }

        private void TickDown()
        {
            try
            {
                Transform ctrl = Refs.charController != null ? Refs.charController.transform : null;
                Transform body = _body.transform;
                if (ctrl == null || ctrl.parent != body.parent || (ctrl.localPosition - _placedLocal).sqrMagnitude > .01f)
                {
                    // The game moved the player itself (a teleport, a recovery, getting off a boat): it wins.
                    GetUp(place: false);
                    return;
                }
                if (CoopBehaviour.Instance?.HostPause?.Frozen == true) return;
                if (_net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading || Held())
                {
                    GetUp(place: true);
                    return;
                }
                if (Vector3.Dot(_startLocal - body.localPosition, Vector3.up) > LostMetres)
                {
                    // Off the edge of the walk copy: nothing below to land on.
                    body.localPosition = _startLocal;
                    GetUp(place: true);
                    return;
                }
                // Something else gave control back (the host's pause ended): the body still holds it.
                if (Refs.charController.enabled) Refs.SetPlayerControl(false);
                ctrl.localPosition = _placedLocal = body.localPosition;
                if (Time.time - _downAt >= MinDownSeconds && Input.GetKeyDown(KeyCode.Space)) GetUp(place: true);
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[Knockdown] could not drive the fallen body", e.Message, ref _report);
                GetUp(place: false);
            }
        }

        private void GetUp(bool place)
        {
            try
            {
                Transform ctrl = Refs.charController != null ? Refs.charController.transform : null;
                CharacterController cc = Refs.charController;
                if (place && ctrl != null && cc != null && _body != null && ctrl.parent == _body.transform.parent)
                {
                    // The middle of the lying body, raised so the standing capsule clears the deck.
                    Transform body = _body.transform;
                    Vector3 centre = body.localPosition + body.localRotation * Vector3.Scale(body.localScale, cc.center);
                    float lift = Mathf.Max(0f, cc.height * .5f - cc.radius) * body.localScale.y + .05f;
                    ctrl.localPosition = centre - ctrl.localRotation * Vector3.Scale(ctrl.localScale, cc.center) + Vector3.up * lift;
                }
            }
            catch (Exception e) { Plugin.Logger.ReportError("[Knockdown] could not stand the player up", e.Message, ref _report); }
            if (_body != null) UnityEngine.Object.Destroy(_body);
            _body = null;
            _immuneUntil = Time.time + ImmuneSeconds;
            _letGo = false;
            try
            {
                var coop = CoopBehaviour.Instance;
                if (coop?.HostPause is HostPauseSync pause && (pause.Frozen || coop.Pause?.Active == true))
                    pause.RestoreControlWhenReady();
                else if (!Held()) Refs.SetPlayerControl(true);
            }
            catch (Exception e) { Plugin.Logger.ReportError("[Knockdown] could not give control back", e.Message, ref _report); }
            Plugin.Logger.LogInfo("[Knockdown] up");
        }

        /// <summary>Something of the game or the session holds the player's control: a bed, a sleep, a
        /// recovery (passing out), the host's pause. The player then does not lie there, and gets no
        /// control back from here.</summary>
        private static bool Held()
        {
            var coop = CoopBehaviour.Instance;
            return GameState.inBed != null || GameState.sleeping || GameState.recovering ||
                   (SleepSync.Instance != null && SleepSync.Instance.ClientAsleep) ||
                   (coop?.HostPause != null && coop.HostPause.Frozen);
        }

        /// <summary>Called from the prefix of <c>GoPointer.LateUpdate</c>, inside the local input scope
        /// that forwards interactions. A fallen player lets go of the held item and the held control
        /// once, and then points at nothing: true skips the game's pointer.</summary>
        internal bool HandlePointer(GoPointer pointer)
        {
            if (_body == null) return false;
            if (_letGo)
            {
                _letGo = false;
                try
                {
                    PickupableItem held = pointer.GetHeldItem();
                    if (held != null)
                    {
                        _released = held as ShipItem;
                        _releasedUntil = Time.time + OwnReleaseSeconds;
                        ThrowPower?.SetValue(pointer, 0f);   // a throw being wound up is not thrown
                        held.OnDrop();
                        pointer.DropItem();
                    }
                    InputScopePatches.ReleaseClick(pointer);
                }
                catch (Exception e) { Plugin.Logger.ReportError("[Knockdown] could not let go", e.Message, ref _report); }
            }
            return true;
        }

        /// <summary>How far the body has turned from standing, in the axes of the render frame.</summary>
        private Quaternion Tilt(Transform player)
        {
            Quaternion frame = Rot(player.parent);
            Quaternion local = _body.transform.localRotation * Quaternion.Inverse(_bodyRot0);
            return frame * local * Quaternion.Inverse(frame);
        }

        private void Show(Camera camera)
        {
            try
            {
                if (_moved || _body == null || camera != Camera.main) return;
                Transform player = CoopBehaviour.Instance?.Players?.LocalPlayer;
                if (player == null) return;
                var eye = camera.transform;
                _savedPosition = eye.localPosition; _savedRotation = eye.localRotation; _movedCamera = camera; _moved = true;
                Quaternion tilt = Tilt(player);
                eye.SetPositionAndRotation(player.position + tilt * (eye.position - player.position), tilt * eye.rotation);
            }
            catch (Exception e) { Plugin.Logger.ReportError("[Knockdown] camera", e.Message, ref _report); }
        }

        private void PutBack(Camera camera)
        {
            if (!_moved || camera != _movedCamera) return;
            _moved = false;
            if (_movedCamera != null) { _movedCamera.transform.localPosition = _savedPosition; _movedCamera.transform.localRotation = _savedRotation; }
            _movedCamera = null;
        }

        private static Quaternion Rot(Transform t) => t != null ? t.rotation : Quaternion.identity;

        /// <summary>The shortest distance between segments ab and cd; <paramref name="onCd"/> is the
        /// closest point of cd.</summary>
        internal static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 onCd)
        {
            Vector3 u = b - a, v = d - c, w = a - c;
            float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
            float den = uu * vv - uv * uv;
            float s = den > 1e-6f ? Mathf.Clamp01((uv * vw - vv * uw) / den) : 0f;
            float t = vv > 1e-6f ? Mathf.Clamp01((uv * s + vw) / vv) : 0f;
            // t was clamped: s for that t.
            s = uu > 1e-6f ? Mathf.Clamp01((uv * t - uw) / uu) : 0f;
            onCd = c + v * t;
            return Vector3.Distance(a + u * s, onCd);
        }
    }
}
