using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SailwindCoop.Avatar;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 1 — mutual player presence. Sends this machine's player pose (in real
    /// space) at the snapshot rate, and renders every other player as a smoothed
    /// remote avatar via <see cref="NetTransform"/>. The host relays clients' poses
    /// to other clients so 3+ players already work without a rewrite (F2/F3 spirit).
    /// </summary>
    public sealed class PlayerSync
    {
        private sealed class RemoteAvatar
        {
            public GameObject Go;
            public Transform Body;
            public Transform Head;
            public Animator Animator;
            public AvatarPoseDriver PoseDriver;
            public readonly NetTransform Net = new NetTransform();
            public Quaternion HeadWorldRot = Quaternion.identity;
            public bool HeadDrivenByAnimator;
            public float AnimSpeed;
            public float AnimTurn;
            public float AnimCrouch;
            public float AnimTargetSpeed;
            public float AnimTargetTurn;
            public float AnimTargetCrouch;
            public bool AnimMoving;
            public Quaternion LastAnimRot;
            public long LastAnimTick;
            public CoordFrame LastAnimFrame;
            public bool HasAnimSnapshot;
            public bool HasSpeedParam;
            public bool HasTurnParam;
            public bool HasCrouchFloatParam;
            public bool HasCrouchBoolParam;
            public bool HasIsCrouchingParam;
            public float VisualOffsetY;
            public NpcLocomotionDriver NpcLoco;   // procedural gait for NPC-skin avatars (no Animator)
            public bool NpcFitPending;            // one-time feet-on-deck fit awaiting valid bounds
        }

        private sealed class AvatarPoseDriver : MonoBehaviour
        {
            public Transform Spine;
            public Transform Chest;
            public Transform Neck;
            public float TargetPitch;

            private bool _ready;

            public void CaptureBase()
            {
                _ready = Spine != null || Chest != null || Neck != null;
            }

            private void LateUpdate()
            {
                if (!_ready) return;

                float pitch = Mathf.Clamp(TargetPitch, -35f, 45f);
                if (Spine != null) Spine.localRotation = Spine.localRotation * Quaternion.Euler(-pitch * 0.25f, 0f, 0f);
                if (Chest != null) Chest.localRotation = Chest.localRotation * Quaternion.Euler(-pitch * 0.35f, 0f, 0f);
                if (Neck != null) Neck.localRotation = Neck.localRotation * Quaternion.Euler(-pitch * 0.20f, 0f, 0f);
            }
        }

        /// <summary>
        /// Процедурная походка/дыхание для NPC-скина: у игровых NPC нет Animator-контроллера,
        /// поэтому кости качаются синусоидой вручную (приём из DiamondMiner99/sailwind-coop):
        /// каждый кадр кость сбрасывается в bind-позу и добавляется качание; ноги/руки —
        /// противофазой по фазе шага, позвоночник — медленное «дыхание». Имена костей — Synty-риг.
        /// </summary>
        internal sealed class NpcLocomotionDriver : MonoBehaviour
        {
            /// <summary>Желаемая скорость походки, м/с (0 = стоим). Задаёт PlayerSync каждый кадр.</summary>
            public float TargetSpeedMps;
            public bool Ready { get; private set; }

            private float _speed;
            private float _phase;
            private Transform _spine, _legL, _legR, _kneeL, _kneeR, _armL, _armR, _elbowL, _elbowR;
            private Quaternion _qSpine, _qLegL, _qLegR, _qKneeL, _qKneeR, _qArmL, _qArmR, _qElbowL, _qElbowR;

            public void Setup()
            {
                _spine = Find(transform, "Spine_01");
                _legL = Find(transform, "UpperLeg_L"); _legR = Find(transform, "UpperLeg_R");
                _kneeL = Find(transform, "LowerLeg_L"); _kneeR = Find(transform, "LowerLeg_R");
                _armL = Find(transform, "Shoulder_L"); _armR = Find(transform, "Shoulder_R");
                _elbowL = Find(transform, "Elbow_L"); _elbowR = Find(transform, "Elbow_R");

                if (_spine != null) _qSpine = _spine.localRotation;
                if (_legL != null) _qLegL = _legL.localRotation;
                if (_legR != null) _qLegR = _legR.localRotation;
                if (_kneeL != null) _qKneeL = _kneeL.localRotation;
                if (_kneeR != null) _qKneeR = _kneeR.localRotation;
                if (_armL != null) _qArmL = _armL.localRotation;
                if (_armR != null) _qArmR = _armR.localRotation;
                if (_elbowL != null) _qElbowL = _elbowL.localRotation;
                if (_elbowR != null) _qElbowR = _elbowR.localRotation;

                Ready = _legL != null && _legR != null; // минимум для походки — ноги
            }

            private void LateUpdate()
            {
                if (!Ready) return;

                const float WalkFullSpeed = 1.4f;  // м/с полной амплитуды шага
                const float StrideRadPerM = 4.2f;  // фаза шага на метр пути (каденс)
                const float LegAmp = 28f;          // размах бедра, град
                const float KneeAmp = 40f;         // сгиб колена (однонаправленный), град
                const float KneePhase = 1.1f;      // сдвиг сгиба колена внутри шага, рад
                const float ArmAmp = 22f;          // размах руки, град
                const float ElbowAmp = 16f;        // сгиб локтя, град
                const float BreatheAmp = 2.2f;     // «дыхание» позвоночника, град
                const float BreatheHz = 0.22f;     // вдохов в секунду

                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                _speed = Mathf.Lerp(_speed, TargetSpeedMps, 1f - Mathf.Exp(-8f * dt));
                float blend = Mathf.Clamp01(_speed / WalkFullSpeed);
                _phase = Mathf.Repeat(_phase + _speed * StrideRadPerM * dt, 2f * Mathf.PI);

                float s = Mathf.Sin(_phase);
                float sOpp = Mathf.Sin(_phase + Mathf.PI);

                Swing(_legL, _qLegL, Vector3.up, LegAmp * blend * s);
                Swing(_legR, _qLegR, Vector3.up, LegAmp * blend * sOpp);
                Swing(_kneeL, _qKneeL, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_phase + KneePhase)));
                Swing(_kneeR, _qKneeR, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_phase + Mathf.PI + KneePhase)));
                Swing(_armL, _qArmL, Vector3.down, ArmAmp * blend * sOpp);
                Swing(_armR, _qArmR, Vector3.down, ArmAmp * blend * s);
                Swing(_elbowL, _qElbowL, Vector3.down, ElbowAmp * blend * (0.5f + 0.5f * sOpp));
                Swing(_elbowR, _qElbowR, Vector3.down, ElbowAmp * blend * (0.5f + 0.5f * s));
                Swing(_spine, _qSpine, Vector3.forward, Mathf.Sin(Time.time * BreatheHz * 2f * Mathf.PI) * BreatheAmp);
            }

            private static void Swing(Transform t, Quaternion bind, Vector3 axis, float deg)
            {
                if (t == null) return;
                t.localRotation = bind; // сброс в bind-позу, затем добавка — без накопления
                t.Rotate(axis, deg, Space.Self);
            }

            private static Transform Find(Transform root, string name)
            {
                if (root.name == name) return root;
                for (int i = 0; i < root.childCount; i++)
                {
                    var r = Find(root.GetChild(i), name);
                    if (r != null) return r;
                }
                return null;
            }
        }

        private sealed class AvatarVisualOffsetDriver : MonoBehaviour
        {
            public float OffsetY;

            private void LateUpdate()
            {
                transform.localPosition = new Vector3(0f, OffsetY, 0f);
                transform.localRotation = Quaternion.identity;
            }
        }

        private readonly CoopNet _net;
        private readonly Dictionary<uint, RemoteAvatar> _remotes = new Dictionary<uint, RemoteAvatar>();
        // Per-file bundle cache so two players can use different avatar*.bundle at the same time.
        private readonly Dictionary<string, AssetBundle> _bundleCache = new Dictionary<string, AssetBundle>();
        private readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();
        // netId -> bundle file name the remote player chose (default = avatar.bundle).
        private readonly Dictionary<uint, string> _remoteAvatarFile = new Dictionary<uint, string>();
        // netId -> next retry time (realtime) for players whose NPC skin could not be built yet
        // (no template captured) and who are temporarily shown with the fallback bundle avatar.
        private readonly Dictionary<uint, float> _npcRetryAt = new Dictionary<uint, float>();
        private const float NpcRetrySec = 5f;

        private Transform _localPlayer;
        private PlayerEmbarkerNew _emb;
        private bool _dumped;
        private float _sendTimer;
        private Vector3 _lastRealPos;
        private long _lastRealTick;
        private bool _haveLast;
        private CoordFrame _lastFrame;
        private PlayerCrouching _localCrouching;
        private bool _lastLocalCrouch;
        private float _lastLocalCrouchHeight;
        private static readonly FieldInfo CrouchingField =
            typeof(PlayerCrouching).GetField("crouching", BindingFlags.Instance | BindingFlags.NonPublic);

        public float InterpDelayMs = 100f;
        public int SnapshotHz = 20;

        public int RemoteCount => _remotes.Count;
        public bool LocalPlayerFound => _localPlayer != null;
        public string LocalCrouchText => "local " + (_lastLocalCrouch ? "YES" : "—") +
                                         ", h " + _lastLocalCrouchHeight.ToString("0.00");
        public string NearestRemoteAnim
        {
            get
            {
                RemoteAvatar best = null;
                float bestDist = -1f;
                foreach (var a in _remotes.Values)
                {
                    if (a.Go == null) continue;
                    float d = _localPlayer != null ? Vector3.Distance(_localPlayer.position, a.Go.transform.position) : 0f;
                    if (best == null || d < bestDist)
                    {
                        best = a;
                        bestDist = d;
                    }
                }
                if (best == null || best.Animator == null) return "—";
                return "Speed " + best.AnimSpeed.ToString("0.00") +
                       " -> " + best.AnimTargetSpeed.ToString("0.0") +
                       ", Turn " + best.AnimTurn.ToString("0.00") +
                       ", Crouch " + best.AnimCrouch.ToString("0.00") +
                       " -> " + best.AnimTargetCrouch.ToString("0.0") +
                       ", param " + (best.HasCrouchFloatParam ? "float" : best.HasCrouchBoolParam ? "bool" : best.HasIsCrouchingParam ? "IsCrouching" : "NO") +
                       ", moving " + (best.AnimMoving ? "YES" : "—");
            }
        }

        /// <summary>Distance from the local player to the nearest remote avatar, or -1 if none.</summary>
        public float NearestRemoteDistance
        {
            get
            {
                if (_localPlayer == null) return -1f;
                float best = -1f;
                foreach (var a in _remotes.Values)
                {
                    if (a.Go == null) continue;
                    float d = Vector3.Distance(_localPlayer.position, a.Go.transform.position);
                    if (best < 0f || d < best) best = d;
                }
                return best;
            }
        }

        public PlayerSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Avatar selection: called by CoopBehaviour on handshake / change
        // -----------------------------------------------------------------

        /// <summary>Host-side: remember which avatar bundle a freshly accepted client chose.
        /// Used when their first PlayerState arrives so we instantiate the right model.</summary>
        public void RegisterRemoteAvatarFile(uint netId, string bundleFile)
        {
            string key = ResolveBundleKey(bundleFile);
            _remoteAvatarFile[netId] = key;
        }

        /// <summary>Called when an AvatarChange message arrives (host rebroadcast or our own).
        /// Updates the per-player file map and rebuilds the remote avatar if it already exists.</summary>
        public void ApplyAvatarChange(uint netId, string bundleFile)
        {
            string key = ResolveBundleKey(bundleFile);
            _remoteAvatarFile[netId] = key;
            _npcRetryAt.Remove(netId); // fresh selection restarts the build/fallback cycle

            if (_remotes.TryGetValue(netId, out var existing) && existing.Go != null)
            {
                Plugin.Logger.LogInfo("[PlayerSync] AvatarChange NetId=" + netId + " -> '" + key +
                                      "' (recreating avatar)");
                // Tear down the old avatar; the next PlayerState will rebuild from the new bundle.
                Object.Destroy(existing.Go);
                _remotes.Remove(netId);
            }
            else
            {
                Plugin.Logger.LogInfo("[PlayerSync] AvatarChange NetId=" + netId + " -> '" + key +
                                      "' (avatar not created yet, selection remembered)");
            }
        }

        private string ResolveBundleKey(string bundleFile)
        {
            if (string.IsNullOrWhiteSpace(bundleFile)) return AvatarCatalog.DefaultBundleFile;
            return bundleFile.Trim();
        }

        // -----------------------------------------------------------------
        // Outbound: send my pose
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            EnsureLocalPlayer();
            if (_localPlayer == null) return;

            float interval = 1f / Mathf.Max(1, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            long tick = _net.Clock.ServerTick;
            Transform pl = _localPlayer;
            Transform boat = CurrentBoat;

            // On a boat: send pose in BOAT-LOCAL space — identical on both machines and
            // immune to floating origin. Otherwise fall back to origin-stable real space.
            CoordFrame frame;
            ushort boatIndex = BoatLocator.NoBoat;
            Vector3 pos;
            Quaternion rot;
            Quaternion headRot;
            Camera renderCamera = PickRenderCamera();
            Transform head = renderCamera != null ? renderCamera.transform : pl;
            if (boat != null)
            {
                frame = CoordFrame.Boat;
                boatIndex = BoatLocator.IndexOf(boat);
                pos = boat.InverseTransformPoint(pl.position);
                rot = Quaternion.Inverse(boat.rotation) * pl.rotation;
                headRot = Quaternion.Inverse(boat.rotation) * head.rotation;
            }
            else
            {
                frame = CoordFrame.World;
                pos = CoordSpace.LocalToReal(pl.position);
                rot = pl.rotation;
                headRot = head.rotation;
            }

            // Velocity in the same frame (reset when the frame changes).
            Vector3 vel = Vector3.zero;
            if (_haveLast && _lastFrame == frame)
            {
                float secs = (tick - _lastRealTick) / 1000f;
                if (secs > 0.0001f) vel = (pos - _lastRealPos) / secs;
            }
            _lastRealPos = pos;
            _lastRealTick = tick;
            _lastFrame = frame;
            _haveLast = true;
            _lastLocalCrouch = IsLocalCrouching();

            _net.Broadcast(new PlayerStateMsg
            {
                NetId = _net.MyNetId,
                Tick = tick,
                Frame = frame,
                BoatIndex = boatIndex,
                Pos = pos,
                Rot = rot,
                HeadRot = headRot,
                Vel = vel,
                Crouch = _lastLocalCrouch,
            }, LiteNetLib.DeliveryMethod.Unreliable);
        }

        // -----------------------------------------------------------------
        // Inbound: a player pose arrived
        // -----------------------------------------------------------------

        public void OnPlayerState(PlayerStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            // Host relays a client's pose to the other clients (skip the sender).
            if (_net.Role == Role.Host)
                _net.RelayExcept(msg, fromPeer, LiteNetLib.DeliveryMethod.Unreliable);

            // Never render ourselves.
            if (msg.NetId == _net.MyNetId) return;

            if (!_remotes.TryGetValue(msg.NetId, out var a))
            {
                a = CreateAvatar(msg.NetId);
                _remotes[msg.NetId] = a;
            }
            a.Net.InterpDelayMs = InterpDelayMs;

            // Pick how this remote's buffered pose converts to local world space.
            if (msg.Frame == CoordFrame.Boat)
            {
                Transform boat = BoatLocator.FindByIndex(msg.BoatIndex);
                if (boat != null)
                {
                    a.Net.ToWorldPos = p => boat.TransformPoint(p);
                    a.Net.ToWorldRot = q => boat.rotation * q;
                    a.HeadWorldRot = boat.rotation * msg.HeadRot;
                }
                // If we have no boat yet, leave the previous converter; the avatar will
                // settle once we're aboard.
            }
            else
            {
                a.Net.ToWorldPos = CoordSpace.RealToLocal;
                a.Net.ToWorldRot = q => q;
                a.HeadWorldRot = msg.HeadRot;
            }

            a.Net.Push(msg.Tick, msg.Pos, msg.Rot, msg.Vel);
            UpdateAnimatorTargets(a, msg);
        }

        // -----------------------------------------------------------------
        // Per-frame: drive remote avatars from their buffers
        // -----------------------------------------------------------------

        public void ApplyRemotes()
        {
            if (!CoordSpace.Ready) return;
            long now = _net.Clock.ServerTick;
            foreach (var a in _remotes.Values)
            {
                if (a.Go == null) continue;
                a.Net.Apply(a.Go.transform, now);
                ApplyAvatarPolish(a);
            }
            RetryPendingNpcSkins();
        }

        /// <summary>
        /// Players whose chosen NPC skin could not be built at avatar-creation time (no template)
        /// are shown with the fallback bundle avatar. Periodically re-scan for a template and,
        /// once one is available, tear the fallback down so the next PlayerState rebuilds it
        /// with the proper NPC skin.
        /// </summary>
        private void RetryPendingNpcSkins()
        {
            if (_npcRetryAt.Count == 0) return;
            float now = Time.realtimeSinceStartup;

            List<uint> ready = null;
            foreach (var kv in _npcRetryAt)
            {
                if (now < kv.Value) continue;
                if (ready == null) ready = new List<uint>();
                ready.Add(kv.Key);
            }
            if (ready == null) return;

            NpcSkinLibrary.Scan(force: false); // rate-limited inside
            foreach (uint netId in ready)
            {
                if (!NpcSkinLibrary.CanBuild)
                {
                    _npcRetryAt[netId] = now + NpcRetrySec;
                    continue;
                }
                _npcRetryAt.Remove(netId);
                if (_remotes.TryGetValue(netId, out var a))
                {
                    Plugin.Logger.LogInfo("[PlayerSync] NPC template captured — rebuilding avatar NetId=" + netId);
                    if (a.Go != null) Object.Destroy(a.Go);
                    _remotes.Remove(netId);
                }
            }
        }

        public void RemoveRemote(uint netId)
        {
            if (_remotes.TryGetValue(netId, out var a))
            {
                if (a.Go != null) Object.Destroy(a.Go);
                _remotes.Remove(netId);
            }
            _remoteAvatarFile.Remove(netId);
            _npcRetryAt.Remove(netId);
        }

        public void Clear()
        {
            foreach (var a in _remotes.Values)
                if (a.Go != null) Object.Destroy(a.Go);
            _remotes.Clear();
            _remoteAvatarFile.Clear();
            _npcRetryAt.Clear();
            _localPlayer = null;
            _localCrouching = null;
            _haveLast = false;
            foreach (var b in _bundleCache.Values)
                if (b != null) b.Unload(false);
            _bundleCache.Clear();
            _prefabCache.Clear();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void EnsureLocalPlayer()
        {
            if (_localPlayer != null) return;
            var emb = Object.FindObjectOfType<PlayerEmbarkerNew>();
            if (emb != null && emb.playerObserver != null)
            {
                _emb = emb;
                // playerObserver lives in the render frame (same space as the camera);
                // playerController is in a different frame and must NOT be used here.
                _localPlayer = emb.playerObserver;
                DumpHierarchy();
            }
            else if (Camera.main != null)
                _localPlayer = Camera.main.transform;   // fallback before the player exists
        }

        /// <summary>The boat the local player is currently standing on, or null.</summary>
        private Transform CurrentBoat => _emb != null ? _emb.debugOutCurrentBoat : null;

        private void ApplyAvatarPolish(RemoteAvatar a)
        {
            if (a.Go == null) return;

            ApplyAnimatorParams(a);
            ApplyLookPitch(a);
            ApplyVisualOffset(a);

            if (a.NpcFitPending) FitNpcBody(a);
            if (a.NpcLoco != null)
                a.NpcLoco.TargetSpeedMps = a.AnimMoving ? 1.4f : 0f;

            if (a.Head != null && !a.HeadDrivenByAnimator)
            {
                Quaternion localHead = Quaternion.Inverse(a.Go.transform.rotation) * a.HeadWorldRot;
                a.Head.localRotation = Quaternion.Slerp(a.Head.localRotation, localHead, 0.35f);
            }

        }

        private void ApplyLookPitch(RemoteAvatar a)
        {
            if (a.PoseDriver == null) return;
            Vector3 localLook = Quaternion.Inverse(a.Go.transform.rotation) * (a.HeadWorldRot * Vector3.forward);
            float pitch = Mathf.Asin(Mathf.Clamp(localLook.y, -1f, 1f)) * Mathf.Rad2Deg;
            a.PoseDriver.TargetPitch = Mathf.Clamp(pitch, -35f, 45f);
        }

        private void ApplyVisualOffset(RemoteAvatar a)
        {
            if (a.Body == null || a.Body == a.Go.transform) return;
            a.Body.localPosition = new Vector3(0f, a.VisualOffsetY, 0f);
            a.Body.localRotation = Quaternion.identity;
        }

        private void ApplyAnimatorParams(RemoteAvatar a)
        {
            if (a.Animator == null) return;

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            a.AnimSpeed = Mathf.Lerp(a.AnimSpeed, a.AnimTargetSpeed, 1f - Mathf.Exp(-12f * dt));
            a.AnimTurn = Mathf.Lerp(a.AnimTurn, a.AnimTargetTurn, 1f - Mathf.Exp(-10f * dt));
            a.AnimCrouch = Mathf.Lerp(a.AnimCrouch, a.AnimTargetCrouch, 1f - Mathf.Exp(-12f * dt));

            if (a.HasSpeedParam) a.Animator.SetFloat("Speed", a.AnimSpeed);
            if (a.HasTurnParam) a.Animator.SetFloat("Turn", a.AnimTurn);
            if (a.HasCrouchFloatParam) a.Animator.SetFloat("Crouch", a.AnimCrouch);
            if (a.HasCrouchBoolParam) a.Animator.SetBool("Crouch", a.AnimTargetCrouch > 0.5f);
            if (a.HasIsCrouchingParam) a.Animator.SetBool("IsCrouching", a.AnimTargetCrouch > 0.5f);
        }

        private void UpdateAnimatorTargets(RemoteAvatar a, PlayerStateMsg msg)
        {
            Vector3 planarVel = msg.Vel;
            planarVel.y = 0f; // ignore camera/head bob and small deck height corrections
            float speed = planarVel.magnitude;

            // Feed the animator an intent value, not raw metres/sec. This matches simple
            // controllers where Speed=0 is idle and Speed around 3 is run, and avoids
            // half-blends after the walk clip was removed.
            if (a.AnimMoving)
            {
                if (speed < 0.08f) a.AnimMoving = false;
            }
            else
            {
                if (speed > 0.18f) a.AnimMoving = true;
            }
            a.AnimTargetSpeed = a.AnimMoving ? 3f : 0f;
            a.AnimTargetCrouch = msg.Crouch ? 1f : 0f;

            if (!a.HasAnimSnapshot || a.LastAnimFrame != msg.Frame)
            {
                a.LastAnimRot = msg.Rot;
                a.LastAnimTick = msg.Tick;
                a.LastAnimFrame = msg.Frame;
                a.HasAnimSnapshot = true;
                a.AnimTargetTurn = 0f;
                return;
            }

            float secs = (msg.Tick - a.LastAnimTick) / 1000f;
            if (secs > 0.0001f)
            {
                Vector3 prevForward = a.LastAnimRot * Vector3.forward;
                Vector3 nextForward = msg.Rot * Vector3.forward;
                float yawPerSec = Vector3.SignedAngle(prevForward, nextForward, Vector3.up) / secs;
                a.AnimTargetTurn = Mathf.Abs(yawPerSec) < 15f ? 0f : Mathf.Clamp(yawPerSec / 180f, -1f, 1f);
            }

            a.LastAnimRot = msg.Rot;
            a.LastAnimTick = msg.Tick;
            a.LastAnimFrame = msg.Frame;
        }

        private bool IsLocalCrouching()
        {
            try
            {
                if (_localCrouching == null)
                    _localCrouching = Object.FindObjectOfType<PlayerCrouching>();

                if (_localCrouching == null) return false;

                _lastLocalCrouchHeight = _localCrouching.GetCurrentHeadHeight();
                if (CrouchingField != null)
                {
                    bool crouching = (bool)CrouchingField.GetValue(_localCrouching);
                    return crouching || (_lastLocalCrouchHeight > 0.001f && _lastLocalCrouchHeight < 0.6f);
                }

                return _lastLocalCrouchHeight > 0.001f && _lastLocalCrouchHeight < 0.6f;
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerSync] Failed to read crouch state: " + e.Message);
                return false;
            }
        }

        /// <summary>One-time hierarchy dump to understand which transform = visible player.</summary>
        private void DumpHierarchy()
        {
            if (_dumped) return;
            _dumped = true;
            Vector3 P(Transform t) => t != null ? t.position : Vector3.zero;
            Plugin.Logger.LogInfo("[PlayerSync] HIER observer@" + P(_emb.playerObserver).ToString("F1") +
                " controller@" + P(_emb.playerController).ToString("F1") +
                " shiftingWorld@" + P(_emb.shiftingWorld).ToString("F1") +
                " curBoat=" + (_emb.debugOutCurrentBoat != null ? _emb.debugOutCurrentBoat.name : "null") +
                "@" + P(_emb.debugOutCurrentBoat).ToString("F1"));

            var cam = PickRenderCamera();
            if (cam != null)
            {
                string chain = "";
                var t = cam.transform;
                for (int i = 0; i < 6 && t != null; i++) { chain += t.name + " < "; t = t.parent; }
                Plugin.Logger.LogInfo("[PlayerSync] HIER cam=" + cam.name + "@" + cam.transform.position.ToString("F1") +
                    " parents: " + chain);
            }
        }

        private RemoteAvatar CreateAvatar(uint netId)
        {
            string bundleFile;
            if (!_remoteAvatarFile.TryGetValue(netId, out bundleFile))
                bundleFile = AvatarCatalog.DefaultBundleFile;
            RemoteAvatar bundled = TryCreateBundledAvatar(netId, bundleFile);
            if (bundled != null) return bundled;

            var go = new GameObject("CoopPlayer_" + netId);
            go.name = "CoopPlayer_" + netId;

            // Put the avatar on a layer the render camera actually draws. The local
            // player sits on a "Player" layer the first-person camera culls (so you
            // don't see your own body); we must avoid that layer and pick one in the
            // camera's culling mask.
            go.layer = PickVisibleLayer();

            // CreatePrimitive's Standard material doesn't render reliably in the shipped
            // build. Use unlit, always-visible materials for the simple Stage 3 proxy.
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Standard");

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = new Vector3(0f, -0.45f, 0f);
            body.transform.localScale = new Vector3(0.45f, 0.65f, 0.45f);
            body.layer = go.layer;
            var bodyCol = body.GetComponent<Collider>();
            if (bodyCol != null) Object.Destroy(bodyCol);
            SetUnlit(body.GetComponent<Renderer>(), sh, new Color(0.15f, 0.6f, 1f));

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(go.transform, false);
            head.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            head.transform.localScale = new Vector3(0.32f, 0.32f, 0.32f);
            head.layer = go.layer;
            var headCol = head.GetComponent<Collider>();
            if (headCol != null) Object.Destroy(headCol);
            SetUnlit(head.GetComponent<Renderer>(), sh, new Color(0.95f, 0.85f, 0.65f));

            var look = GameObject.CreatePrimitive(PrimitiveType.Cube);
            look.name = "LookDir";
            look.transform.SetParent(head.transform, false);
            look.transform.localPosition = new Vector3(0f, 0f, 0.28f);
            look.transform.localScale = new Vector3(0.08f, 0.08f, 0.22f);
            look.layer = go.layer;
            var lookCol = look.GetComponent<Collider>();
            if (lookCol != null) Object.Destroy(lookCol);
            SetUnlit(look.GetComponent<Renderer>(), sh, new Color(0.1f, 0.1f, 0.12f));

            Plugin.Logger.LogInfo("[PlayerSync] Avatar NetId=" + netId +
                                  " shader='" + (sh != null ? sh.name : "NO") +
                                  "' layer=" + go.layer);

            Object.DontDestroyOnLoad(go);
            Plugin.Logger.LogInfo("[PlayerSync] Created player avatar NetId=" + netId);
            return new RemoteAvatar { Go = go, Body = body.transform, Head = head.transform };
        }

        private RemoteAvatar TryCreateBundledAvatar(uint netId, string bundleFile)
        {
            // NPC-скин: строится из клона игрового NPC, а не из бандла. При неудаче
            // (шаблон ещё не захвачен — остров с NPC не подгружался) — обычный fallback.
            if (NpcSkinLibrary.IsNpcKey(bundleFile))
            {
                RemoteAvatar npc = TryCreateNpcAvatar(netId, bundleFile);
                if (npc != null)
                {
                    _npcRetryAt.Remove(netId);
                    return npc;
                }
                // Remember the debt: ApplyRemotes retries periodically and rebuilds the
                // avatar with the NPC skin once a template gets captured.
                _npcRetryAt[netId] = Time.realtimeSinceStartup + NpcRetrySec;
                Plugin.Logger.LogWarning("[PlayerSync] NPC skin unavailable (template missing), fallback to " +
                                         AvatarCatalog.DefaultBundleFile + "; retry scheduled");
                bundleFile = AvatarCatalog.DefaultBundleFile;
            }

            GameObject prefab = GetAvatarPrefab(bundleFile);
            if (prefab == null) return null;

            var go = new GameObject("CoopPlayer_" + netId);
            var model = Object.Instantiate(prefab);
            model.name = prefab.name;
            model.transform.SetParent(go.transform, false);
            float verticalOffset = ResolveAvatarVerticalOffset(netId);
            model.transform.localPosition = new Vector3(0f, verticalOffset, 0f);
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            var offsetDriver = model.AddComponent<AvatarVisualOffsetDriver>();
            offsetDriver.OffsetY = verticalOffset;
            int layer = PickVisibleLayer();
            SetLayerRecursive(go.transform, layer);
            StripColliders(go);
            StripRigidbodies(go);

            var animator = model.GetComponentInChildren<Animator>(true);
            bool hasSpeed = false;
            bool hasTurn = false;
            bool hasCrouchFloat = false;
            bool hasCrouchBool = false;
            bool hasIsCrouching = false;
            if (animator != null)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
                animator.applyRootMotion = false;
                animator.enabled = true;
                foreach (var p in animator.parameters)
                {
                    if (p.type == AnimatorControllerParameterType.Float)
                    {
                        if (p.name == "Speed") hasSpeed = true;
                        if (p.name == "Turn") hasTurn = true;
                        if (p.name == "Crouch") hasCrouchFloat = true;
                    }
                    else if (p.type == AnimatorControllerParameterType.Bool)
                    {
                        if (p.name == "Crouch") hasCrouchBool = true;
                        if (p.name == "IsCrouching") hasIsCrouching = true;
                    }
                }
                Plugin.Logger.LogInfo("[PlayerSync] Animator params: Speed=" + hasSpeed +
                                      ", Turn=" + hasTurn +
                                      ", CrouchFloat=" + hasCrouchFloat +
                                      ", CrouchBool=" + hasCrouchBool +
                                      ", IsCrouching=" + hasIsCrouching);
            }

            Transform head = FindChildRecursive(model.transform, "Head");
            if (head == null) head = FindChildRecursive(model.transform, "Neck");
            Transform spine = FindChildRecursive(model.transform, "Spine");
            Transform chest = FindChildRecursive(model.transform, "Spine1");
            if (chest == null) chest = FindChildRecursive(model.transform, "Chest");
            Transform neck = FindChildRecursive(model.transform, "Neck");
            var pose = model.AddComponent<AvatarPoseDriver>();
            pose.Spine = spine;
            pose.Chest = chest;
            pose.Neck = neck;
            pose.CaptureBase();

            Object.DontDestroyOnLoad(go);
            Plugin.Logger.LogInfo("[PlayerSync] Created avatar.bundle avatar NetId=" + netId +
                                  ", head=" + (head != null ? head.name : "none") +
                                  ", offsetY=" + verticalOffset.ToString("F2"));
            Plugin.Logger.LogInfo("[PlayerSync] Pose bones: spine=" + (spine != null ? spine.name : "none") +
                                  ", chest=" + (chest != null ? chest.name : "none") +
                                  ", neck=" + (neck != null ? neck.name : "none"));
            return new RemoteAvatar
            {
                Go = go,
                Body = model.transform,
                Head = head,
                Animator = animator,
                PoseDriver = pose,
                HeadDrivenByAnimator = animator != null,
                HasSpeedParam = hasSpeed,
                HasTurnParam = hasTurn,
                HasCrouchFloatParam = hasCrouchFloat,
                HasCrouchBoolParam = hasCrouchBool,
                HasIsCrouchingParam = hasIsCrouching,
                VisualOffsetY = verticalOffset,
            };
        }

        /// <summary>
        /// Аватар из NPC-скина. Модель — клон игрового NPC (кости в естественной позе),
        /// без Animator: ходьба не анимируется, но голова следит за взглядом через
        /// обычный не-аниматорный путь (HeadDrivenByAnimator=false). AvatarPoseDriver
        /// сюда НЕ ставим: без Animator, переписывающего кости каждый кадр, его
        /// аддитивный поворот в LateUpdate накапливался бы бесконечно.
        /// </summary>
        private RemoteAvatar TryCreateNpcAvatar(uint netId, string key)
        {
            GameObject model = NpcSkinLibrary.BuildModel(key);
            if (model == null) return null;

            var go = new GameObject("CoopPlayer_" + netId);
            // worldPositionStays:false сохраняет локальный TRS модели — масштаб шаблона
            // (lossyScale исходного NPC) НЕ сбрасываем в 1, иначе получится великан.
            model.transform.SetParent(go.transform, false);
            float verticalOffset = ResolveAvatarVerticalOffset(netId);
            model.transform.localPosition = new Vector3(0f, verticalOffset, 0f);
            model.transform.localRotation = Quaternion.identity;
            var offsetDriver = model.AddComponent<AvatarVisualOffsetDriver>();
            offsetDriver.OffsetY = verticalOffset;
            SetLayerRecursive(go.transform, PickVisibleLayer());
            StripColliders(go);
            StripRigidbodies(go);

            Transform head = FindChildRecursive(model.transform, "Head");
            if (head == null) head = FindChildRecursive(model.transform, "Neck");

            var loco = model.AddComponent<NpcLocomotionDriver>();
            loco.Setup();

            Object.DontDestroyOnLoad(go);
            Plugin.Logger.LogInfo("[PlayerSync] Created NPC skin avatar NetId=" + netId +
                                  ", head=" + (head != null ? head.name : "none") +
                                  ", offsetY=" + verticalOffset.ToString("F2") +
                                  ", scale=" + model.transform.localScale.x.ToString("F2") +
                                  ", loco=" + loco.Ready);
            return new RemoteAvatar
            {
                Go = go,
                Body = model.transform,
                Head = head,
                Animator = null,
                PoseDriver = null,
                // Не даём ApplyAvatarPolish крутить кость головы: формула локального
                // поворота рассчитана на голову-примитив (прямой ребёнок корня), а у
                // скелетной кости родитель — шея, и та же формула сворачивает шею.
                HeadDrivenByAnimator = true,
                VisualOffsetY = verticalOffset,
                NpcLoco = loco,
                NpcFitPending = true,
            };
        }

        /// <summary>
        /// Разовая вертикальная подгонка NPC-аватара: ставим ноги (низ skinned-bounds) на ту же
        /// локальную высоту, где у bundle-модели находится пивот (VisualOffsetY). Масштаб клона
        /// зависит от захваченного NPC, поэтому фиксированный конфиг-оффсет сам по себе не
        /// гарантирует ноги на палубе. Отложено до валидных bounds (первые кадры они пустые).
        /// </summary>
        private void FitNpcBody(RemoteAvatar a)
        {
            try
            {
                if (a.Body == null || a.Go == null) { a.NpcFitPending = false; return; }
                var rends = a.Body.GetComponentsInChildren<Renderer>();
                if (rends.Length == 0) { a.NpcFitPending = false; return; }

                Bounds wb = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) wb.Encapsulate(rends[i].bounds);
                if (wb.size.y < 0.5f) return; // bounds ещё не готовы — повторим в следующем кадре

                float feetLocalY = a.Go.transform.InverseTransformPoint(
                    new Vector3(wb.center.x, wb.min.y, wb.center.z)).y;
                float shift = a.VisualOffsetY - feetLocalY;
                a.VisualOffsetY += shift;
                var drv = a.Body.GetComponent<AvatarVisualOffsetDriver>();
                if (drv != null) drv.OffsetY = a.VisualOffsetY;
                a.NpcFitPending = false;
                Plugin.Logger.LogInfo("[PlayerSync] NPC body fit: shift=" + shift.ToString("F2") +
                                      ", offsetY=" + a.VisualOffsetY.ToString("F2"));
            }
            catch (System.Exception e)
            {
                a.NpcFitPending = false;
                Plugin.Logger.LogWarning("[PlayerSync] FitNpcBody: " + e.Message);
            }
        }

        private GameObject GetAvatarPrefab(string bundleFile)
        {
            if (string.IsNullOrWhiteSpace(bundleFile)) bundleFile = AvatarCatalog.DefaultBundleFile;
            string key = bundleFile.Trim();

            // Cache hit — return the same prefab for repeat instantiations.
            if (_prefabCache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            string path = AvatarCatalog.ResolvePath(key);
            if (string.IsNullOrEmpty(path))
            {
                // Fallback chain: requested bundle missing locally -> default avatar.bundle -> primitive.
                if (!string.Equals(key, AvatarCatalog.DefaultBundleFile, System.StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Logger.LogWarning("[PlayerSync] '" + key + "' not found, fallback to " +
                                             AvatarCatalog.DefaultBundleFile);
                    return GetAvatarPrefab(AvatarCatalog.DefaultBundleFile);
                }
                Plugin.Logger.LogInfo("[PlayerSync] " + AvatarCatalog.DefaultBundleFile +
                                      " not found, using primitive avatar");
                return null;
            }

            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.Logger.LogWarning("[PlayerSync] Failed to load " + key + ": " + path +
                                         " (" + ReadBundleHeader(path) + ")");
                if (!string.Equals(key, AvatarCatalog.DefaultBundleFile, System.StringComparison.OrdinalIgnoreCase))
                    return GetAvatarPrefab(AvatarCatalog.DefaultBundleFile);
                return null;
            }

            var prefab = bundle.LoadAsset<GameObject>("Modular Fantasy Character");
            if (prefab == null) prefab = bundle.LoadAsset<GameObject>("Modular Fantasy Character.prefab");
            if (prefab == null) prefab = bundle.LoadAsset<GameObject>("Cowboy");
            var names = bundle.GetAllAssetNames();
            string listed = names != null && names.Length > 0 ? string.Join(", ", names) : "<empty>";
            Plugin.Logger.LogInfo("[PlayerSync] " + key + " assets: " + listed);

            if (prefab == null && names != null)
                prefab = PickAvatarPrefab(bundle, names, "modular fantasy character");
            if (prefab == null && names != null)
                prefab = PickAvatarPrefab(bundle, names, preferCowboy: true);
            if (prefab == null && names != null)
                prefab = PickAvatarPrefab(bundle, names, preferCowboy: false);

            if (prefab == null)
            {
                Plugin.Logger.LogWarning("[PlayerSync] In " + key + " GameObject prefab not found");
                bundle.Unload(false);
                if (!string.Equals(key, AvatarCatalog.DefaultBundleFile, System.StringComparison.OrdinalIgnoreCase))
                    return GetAvatarPrefab(AvatarCatalog.DefaultBundleFile);
                return null;
            }

            _bundleCache[key] = bundle;
            _prefabCache[key] = prefab;
            Plugin.Logger.LogInfo("[PlayerSync] Loaded '" + key + "' prefab '" + prefab.name + "'");
            return prefab;
        }

        private float ResolveAvatarVerticalOffset(uint netId)
        {
            const float defaultOffset = -0.65f;
            if (Plugin.Cfg == null) return defaultOffset;

            if (netId == NetRegistry.HostPlayerNetId)
            {
                float hostOffset = Plugin.Cfg.HostAvatarVerticalOffset.Value;
                if (Mathf.Abs(hostOffset - (-0.25f)) < 0.001f)
                    return -1.15f;
                return hostOffset;
            }

            float offset = Plugin.Cfg.AvatarVerticalOffset.Value;
            if (Mathf.Abs(offset - (-0.25f)) < 0.001f)
            {
                Plugin.Logger.LogInfo("[PlayerSync] Avatar.VerticalOffset=-0.25 is obsolete, applying " + defaultOffset.ToString("F2"));
                return defaultOffset;
            }

            return offset;
        }

        private GameObject PickAvatarPrefab(AssetBundle bundle, string[] names, string contains)
        {
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (name.ToLowerInvariant().IndexOf(contains) < 0) continue;

                var go = bundle.LoadAsset<GameObject>(name);
                if (go != null)
                {
                    Plugin.Logger.LogInfo("[PlayerSync] selected asset '" + name + "' -> '" + go.name + "'");
                    return go;
                }
            }
            return null;
        }

        private GameObject PickAvatarPrefab(AssetBundle bundle, string[] names, bool preferCowboy)
        {
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (preferCowboy && name.ToLowerInvariant().IndexOf("cowboy") < 0) continue;

                var go = bundle.LoadAsset<GameObject>(name);
                if (go != null)
                {
                    Plugin.Logger.LogInfo("[PlayerSync] selected asset '" + name + "' -> '" + go.name + "'");
                    return go;
                }
            }
            return null;
        }

        private string ReadBundleHeader(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int n = Mathf.Min(bytes.Length, 96);
                string header = System.Text.Encoding.ASCII.GetString(bytes, 0, n).Replace('\0', ' ');
                return header.Trim();
            }
            catch (System.Exception e)
            {
                return "header read failed: " + e.Message;
            }
        }

        private void SetUnlit(Renderer rend, Shader sh, Color color)
        {
            if (rend == null) return;
            var mat = new Material(sh) { color = color };
            rend.material = mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
        }

        private void SetLayerRecursive(Transform root, int layer)
        {
            if (root == null) return;
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
                SetLayerRecursive(root.GetChild(i), layer);
        }

        private void StripColliders(GameObject root)
        {
            if (root == null) return;
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                if (col != null) Object.Destroy(col);
        }

        private void StripRigidbodies(GameObject root)
        {
            if (root == null) return;
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                if (rb != null) Object.Destroy(rb);
        }

        private Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The highest-depth enabled camera — usually the one drawing the main view.</summary>
        private Camera PickRenderCamera()
        {
            if (Camera.main != null) return Camera.main;
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if (c.enabled && (best == null || c.depth > best.depth)) best = c;
            return best;
        }

        /// <summary>Pick a layer the active camera renders, avoiding the player's culled layer.</summary>
        private int PickVisibleLayer()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                // Highest-depth enabled camera is usually the main render camera.
                Camera best = null;
                foreach (var c in Camera.allCameras)
                    if (c.enabled && (best == null || c.depth > best.depth)) best = c;
                cam = best;
            }

            int mask = cam != null ? cam.cullingMask : ~0;
            int playerLayer = _localPlayer != null ? _localPlayer.gameObject.layer : -1;

            // Prefer Default(0) if rendered and not the player's layer.
            if ((mask & 1) != 0 && playerLayer != 0) return 0;
            for (int l = 0; l < 32; l++)
                if (l != playerLayer && (mask & (1 << l)) != 0) return l;
            return 0;
        }
    }
}
