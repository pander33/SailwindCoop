using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 1.4 (deferred) — wandering storms. Weather visuals (rain/cloud/fog, ocean colour) are
    /// derived from synced inputs: storm proximity, wind, time and the boat's region, so they track
    /// closely on their own for the shared-boat model. The one drift source is that each machine
    /// integrates the per-frame wind drift of <c>WanderingStorm</c> over its own frame times, so storm
    /// positions slowly diverge. The host therefore re-anchors every storm's real-space position and
    /// active flag a couple of times a second; the local storm sim keeps running (cheap, keeps emission
    /// and the short between-snapshot drift smooth) and is corrected each snapshot. No <c>WeatherSet</c>
    /// scene objects are serialized — only storm transforms.
    /// </summary>
    public sealed class WeatherStormSync
    {
        public static WeatherStormSync Instance { get; private set; }
        private uint _revision;
        private float _attraction;
        private bool _haveAttraction;
        private readonly ItemStateGate _gate = new ItemStateGate();
        private readonly CoopNet _net;
        private float _sendTimer;
        private static FieldInfo _fStorms;

        /// <summary>Re-anchor rate (Hz). Storms move slowly (~12 m/s by wind), so this is plenty.</summary>
        public float StormHz = 2f;

        public WeatherStormSync(CoopNet net) { _net = net; Instance = this; }
        internal uint CaptureAttraction()
        {
            float value = WeatherStorms.totemAttraction;
            if (!_haveAttraction || !_attraction.Equals(value)) { _revision++; _attraction = value; _haveAttraction = true; }
            return _revision;
        }
        internal void ApplyAttraction(float value, uint revision, long tick)
        { if (_gate.Receive(revision, tick, 0, 0, 0, out var changed, out var pose)) WeatherStorms.totemAttraction = value; }
        public void Clear() { _revision = 0; _haveAttraction = false; _sendTimer = 0; _gate.Clear(); _stream.Reset(); _sentState = null; _resyncAsked = false; }
        private readonly ChangeStream _stream = new ChangeStream();
        private StormStateMsg _sentState;
        private bool _resyncAsked;
        /// <summary>Host: a client finished loading and has no world-stream state yet.</summary>
        public void Resync() => _stream.Reset();
        private static bool Changed(StormStateMsg a, StormStateMsg b)
        {
            if (a == null || a.Pos.Length != b.Pos.Length || !a.Distance.Equals(b.Distance) || !a.TotemAttraction.Equals(b.TotemAttraction)) return true;
            for (int i = 0; i < a.Pos.Length; i++)
                if (a.Active[i] != b.Active[i] || (a.Pos[i] - b.Pos[i]).sqrMagnitude > 0.01f) return true;
            return false;
        }

        public void Tick(float dt)
        {
            if (_net.Role == Role.Client && _net.State == LinkState.Connected && !_resyncAsked &&
                CoordSpace.Ready && GameState.playing && !GameState.currentlyLoading)
            {
                _resyncAsked = true;
                _net.Broadcast(new ResyncRequestMsg { Domain = ResyncDomain.World }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            float interval = 1f / Mathf.Max(0.5f, StormHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            var storms = GetStorms();
            if (storms == null) storms = System.Array.Empty<WanderingStorm>();
            if (storms.Length > 255) return;

            var msg = new StormStateMsg
            {
                Pos = new Vector3[storms.Length],
                Active = new bool[storms.Length],
                Distance = WeatherStorms.currentStormDistance,
                TotemAttraction = WeatherStorms.totemAttraction, Revision = CaptureAttraction(), Tick = _net.Clock.ServerTick,
            };
            for (int i = 0; i < storms.Length; i++)
            {
                var s = storms[i];
                msg.Pos[i] = s != null ? CoordSpace.LocalToReal(s.transform.position) : Vector3.zero;
                msg.Active[i] = s != null && s.active;
            }
            // No storms, or storms that have not moved: nothing to send.
            var mode = _stream.Next(Changed(_sentState, msg));
            if (mode == StreamSend.None) return;
            _sentState = msg;
            _net.Broadcast(msg, mode == StreamSend.Reliable ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
        }

        public void OnStormState(StormStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (!CoordSpace.Ready) return;
            ApplyAttraction(msg.TotemAttraction, msg.Revision, msg.Tick);

            var storms = GetStorms();
            if (storms == null) return;
            int n = Mathf.Min(storms.Length, msg.Pos.Length);
            for (int i = 0; i < n; i++)
            {
                var s = storms[i];
                if (s == null) continue;
                s.transform.position = CoordSpace.RealToLocal(msg.Pos[i]);
                s.active = msg.Active[i];
            }
            WeatherStorms.currentStormDistance = msg.Distance;
        }

        private static WanderingStorm[] GetStorms()
        {
            try
            {
                var ws = WeatherStorms.instance;
                if (ws == null) return null;
                if (_fStorms == null)
                    _fStorms = typeof(WeatherStorms).GetField("storms", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fStorms != null ? _fStorms.GetValue(ws) as WanderingStorm[] : null;
            }
            catch { return null; }
        }
    }
}
