using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// F4 — buffered interpolation/extrapolation for one remote transform.
    ///
    /// Snapshots arrive over Unreliable with a host tick; we don't apply them raw
    /// (that would jitter with every late/lost/jittered packet). Instead we buffer
    /// them and render the remote object at <c>(now - interpDelay)</c>, interpolating
    /// between the two snapshots that bracket that time. If we've run past the newest
    /// snapshot (packet loss), we extrapolate briefly from the last known velocity.
    ///
    /// Snapshots are stored in REAL (origin-stable) space; conversion to local space
    /// happens at apply time every frame, because the floating origin keeps drifting.
    /// </summary>
    public sealed class NetTransform
    {
        private struct Snap
        {
            public long Tick;        // host tick (ms)
            public Vector3 RealPos;  // origin-stable position
            public Quaternion Rot;
            public Vector3 RealVel;  // real-space velocity for extrapolation (may be zero)
        }

        private readonly List<Snap> _buf = new List<Snap>(32);
        private const int MaxBuffer = 32;
        private const float MaxExtrapolateMs = 250f;

        /// <summary>Interpolation delay in ms (config). Larger = smoother but more lag.</summary>
        public float InterpDelayMs = 100f;

        public bool HasData => _buf.Count > 0;

        /// <summary>
        /// Converts a buffered position/rotation into world space at apply time. Swappable
        /// so a remote can be expressed in real (origin-stable) space OR boat-local space —
        /// the latter places same-ship players correctly regardless of floating origin.
        /// Defaults to real-space (FloatingOriginManager) conversion.
        /// </summary>
        public System.Func<Vector3, Vector3> ToWorldPos = CoordSpace.RealToLocal;
        public System.Func<Quaternion, Quaternion> ToWorldRot = q => q;

        /// <summary>Feed a snapshot (real space). Out-of-order packets are inserted in order.</summary>
        public void Push(long tick, Vector3 realPos, Quaternion rot, Vector3 realVel = default)
        {
            var s = new Snap { Tick = tick, RealPos = realPos, Rot = rot, RealVel = realVel };

            // Drop duplicates / stale.
            if (_buf.Count > 0 && tick <= _buf[_buf.Count - 1].Tick)
            {
                // Late but newer than some — insert in sorted position; ignore if older than buffer.
                if (tick <= _buf[0].Tick) return;
                int i = _buf.Count - 1;
                while (i >= 0 && _buf[i].Tick > tick) i--;
                if (i >= 0 && _buf[i].Tick == tick) { _buf[i] = s; return; }
                _buf.Insert(i + 1, s);
            }
            else
            {
                _buf.Add(s);
            }

            while (_buf.Count > MaxBuffer) _buf.RemoveAt(0);
        }

        /// <summary>
        /// Sample the buffer at host time <paramref name="serverTickNow"/> and write the
        /// resulting LOCAL-space pose into <paramref name="t"/>. No-op until data exists.
        /// </summary>
        public void Apply(Transform t, long serverTickNow)
        {
            if (t == null || _buf.Count == 0) return;

            long renderTick = serverTickNow - (long)InterpDelayMs;

            // Before the oldest sample: snap to oldest.
            if (renderTick <= _buf[0].Tick)
            {
                Set(t, _buf[0].RealPos, _buf[0].Rot);
                return;
            }

            // After the newest sample: extrapolate from it (bounded).
            var last = _buf[_buf.Count - 1];
            if (renderTick >= last.Tick)
            {
                float ahead = Mathf.Min(renderTick - last.Tick, MaxExtrapolateMs) / 1000f;
                Set(t, last.RealPos + last.RealVel * ahead, last.Rot);
                return;
            }

            // Find bracketing pair and lerp.
            for (int i = 0; i < _buf.Count - 1; i++)
            {
                var a = _buf[i];
                var b = _buf[i + 1];
                if (renderTick >= a.Tick && renderTick <= b.Tick)
                {
                    float span = b.Tick - a.Tick;
                    float f = span > 0 ? (renderTick - a.Tick) / span : 0f;
                    Vector3 real = Vector3.Lerp(a.RealPos, b.RealPos, f);
                    Quaternion rot = Quaternion.Slerp(a.Rot, b.Rot, f);
                    Set(t, real, rot);
                    return;
                }
            }
        }

        private void Set(Transform t, Vector3 storedPos, Quaternion storedRot)
        {
            t.position = ToWorldPos != null ? ToWorldPos(storedPos) : storedPos;
            t.rotation = ToWorldRot != null ? ToWorldRot(storedRot) : storedRot;
        }

        public void Clear() => _buf.Clear();
    }
}
