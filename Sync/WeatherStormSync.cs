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
        private readonly CoopNet _net;
        private float _sendTimer;
        private static FieldInfo _fStorms;

        /// <summary>Re-anchor rate (Hz). Storms move slowly (~12 m/s by wind), so this is plenty.</summary>
        public float StormHz = 2f;

        public WeatherStormSync(CoopNet net) { _net = net; }

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            float interval = 1f / Mathf.Max(0.5f, StormHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            var storms = GetStorms();
            if (storms == null || storms.Length == 0 || storms.Length > 255) return;

            var msg = new StormStateMsg
            {
                Pos = new Vector3[storms.Length],
                Active = new bool[storms.Length],
                Distance = WeatherStorms.currentStormDistance,
            };
            for (int i = 0; i < storms.Length; i++)
            {
                var s = storms[i];
                msg.Pos[i] = s != null ? CoordSpace.LocalToReal(s.transform.position) : Vector3.zero;
                msg.Active[i] = s != null && s.active;
            }
            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.Unreliable);
        }

        public void OnStormState(StormStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            if (!CoordSpace.Ready) return;

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
