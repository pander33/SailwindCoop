using System.Collections.Generic;
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
            public readonly NetTransform Net = new NetTransform();
        }

        private readonly CoopNet _net;
        private readonly Dictionary<uint, RemoteAvatar> _remotes = new Dictionary<uint, RemoteAvatar>();

        private Transform _localPlayer;
        private PlayerEmbarkerNew _emb;
        private bool _dumped;
        private float _sendTimer;
        private Vector3 _lastRealPos;
        private long _lastRealTick;
        private bool _haveLast;
        private CoordFrame _lastFrame;

        public float InterpDelayMs = 100f;
        public int SnapshotHz = 20;

        public int RemoteCount => _remotes.Count;
        public bool LocalPlayerFound => _localPlayer != null;

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
            Vector3 pos;
            Quaternion rot;
            if (boat != null)
            {
                frame = CoordFrame.Boat;
                pos = boat.InverseTransformPoint(pl.position);
                rot = Quaternion.Inverse(boat.rotation) * pl.rotation;
            }
            else
            {
                frame = CoordFrame.World;
                pos = CoordSpace.LocalToReal(pl.position);
                rot = pl.rotation;
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

            _net.Broadcast(new PlayerStateMsg
            {
                NetId = _net.MyNetId,
                Tick = tick,
                Frame = frame,
                Pos = pos,
                Rot = rot,
                Vel = vel,
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
                Transform boat = CurrentBoat;   // our own copy of the shared ship
                if (boat != null)
                {
                    a.Net.ToWorldPos = p => boat.TransformPoint(p);
                    a.Net.ToWorldRot = q => boat.rotation * q;
                }
                // If we have no boat yet, leave the previous converter; the avatar will
                // settle once we're aboard.
            }
            else
            {
                a.Net.ToWorldPos = CoordSpace.RealToLocal;
                a.Net.ToWorldRot = q => q;
            }

            a.Net.Push(msg.Tick, msg.Pos, msg.Rot, msg.Vel);
        }

        // -----------------------------------------------------------------
        // Per-frame: drive remote avatars from their buffers
        // -----------------------------------------------------------------

        public void ApplyRemotes()
        {
            if (!CoordSpace.Ready) return;
            long now = _net.Clock.ServerTick;
            foreach (var a in _remotes.Values)
                if (a.Go != null) a.Net.Apply(a.Go.transform, now);
        }

        public void RemoveRemote(uint netId)
        {
            if (_remotes.TryGetValue(netId, out var a))
            {
                if (a.Go != null) Object.Destroy(a.Go);
                _remotes.Remove(netId);
            }
        }

        public void Clear()
        {
            foreach (var a in _remotes.Values)
                if (a.Go != null) Object.Destroy(a.Go);
            _remotes.Clear();
            _localPlayer = null;
            _haveLast = false;
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
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "CoopPlayer_" + netId;
            // No physics: it's a visual proxy driven entirely by the network.
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);

            // Put the avatar on a layer the render camera actually draws. The local
            // player sits on a "Player" layer the first-person camera culls (so you
            // don't see your own body); we must avoid that layer and pick one in the
            // camera's culling mask.
            go.layer = PickVisibleLayer();

            // CreatePrimitive's Standard material doesn't render reliably in the shipped
            // build. Use an unlit, always-visible shader and a bright colour.
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh == null) sh = Shader.Find("Standard");
                var mat = new Material(sh) { color = new Color(0.15f, 0.6f, 1f) };
                rend.material = mat;
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rend.receiveShadows = false;
                Plugin.Logger.LogInfo("[PlayerSync] Аватар NetId=" + netId +
                                      " шейдер='" + (sh != null ? sh.name : "НЕТ") +
                                      "' слой=" + go.layer);
            }

            Object.DontDestroyOnLoad(go);
            Plugin.Logger.LogInfo("[PlayerSync] Создан аватар игрока NetId=" + netId);
            return new RemoteAvatar { Go = go };
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
