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

        private readonly SnapshotBuffer<Snap> _buf = new SnapshotBuffer<Snap>(s => s.Tick, 32);
        private const float MaxExtrapolateMs = 250f;
        private const float MaxAngularExtrapolateMs = 100f;
        private const float RecoveryMs = 80f;
        private long _lastRenderTick, _recoveryTick;
        private bool _wasExtrapolating, _recovering;
        private Snap _lastPredictionStart, _lastPredictionEnd;
        private Vector3 _recoveryPosition;
        private Quaternion _recoveryRotation;
        internal bool SmoothUnderruns;

        /// <summary>Interpolation delay in ms (config). Larger = smoother but more lag.</summary>
        public float InterpDelayMs = 100f;

        public bool HasData => _buf.Count > 0;

        /// <summary>Buffered snapshot count. A consumer that must not act on a single (possibly
        /// stale/first) sample can wait for two before trusting the stream.</summary>
        public int SampleCount => _buf.Count;

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

            _buf.Push(s);
        }

        /// <summary>
        /// Sample the buffer at host time <paramref name="serverTickNow"/> and write the
        /// resulting LOCAL-space pose into <paramref name="t"/>. No-op until data exists.
        /// </summary>
        public void Apply(Transform t, long serverTickNow)
        {
            if (t == null) return;
            if (!Sample(serverTickNow, out Vector3 pos, out Quaternion rot, out _)) return;
            t.position = pos;
            t.rotation = rot;
        }

        /// <summary>
        /// The pose <see cref="Apply"/> would write, for a caller that places something else from it
        /// (a boat's body from its hull's pose). <paramref name="realVel"/> is the sampled velocity;
        /// the origin only translates, so it is the same in local space. False until data exists.
        /// </summary>
        public bool Sample(long serverTickNow, out Vector3 pos, out Quaternion rot, out Vector3 realVel)
        {
            pos = Vector3.zero; rot = Quaternion.identity; realVel = Vector3.zero;
            if (_buf.Count == 0) return false;

            long renderTick = serverTickNow - (long)InterpDelayMs;

            // Before the oldest sample: snap to oldest.
            if (renderTick <= _buf[0].Tick)
            {
                ConvertSample(renderTick, _buf[0].RealPos, _buf[0].Rot, out pos, out rot);
                realVel = _buf[0].RealVel;
                return true;
            }

            // After the newest sample: extrapolate from it (bounded).
            var last = _buf[_buf.Count - 1];
            if (renderTick >= last.Tick)
            {
                var previous = SmoothUnderruns && _buf.Count >= 2 ? _buf[_buf.Count - 2] : last;
                Predict(previous, last, renderTick, out var predictedPosition, out var predictedRotation);
                ConvertSample(renderTick, predictedPosition, predictedRotation, out pos, out rot);
                realVel = last.RealVel;
                return true;
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
                    ConvertSample(renderTick, Vector3.Lerp(a.RealPos, b.RealPos, f), Quaternion.Slerp(a.Rot, b.Rot, f),
                            out pos, out rot);
                    realVel = Vector3.Lerp(a.RealVel, b.RealVel, f);
                    return true;
                }
            }
            return false;
        }

        private static void Predict(Snap previous, Snap last, long tick, out Vector3 position, out Quaternion rotation)
        {
            float gap = tick - last.Tick;
            position = last.RealPos + last.RealVel * (Mathf.Min(gap, MaxExtrapolateMs) / 1000f);
            rotation = last.Rot;
            long span = last.Tick - previous.Tick;
            if (gap <= 0f || gap >= MaxExtrapolateMs || span <= 0 || span > MaxExtrapolateMs) return;

            // A change-only stream may end here. Settle on the last authoritative rotation.
            float angularAhead = Mathf.Min(gap, MaxAngularExtrapolateMs) *
                (1f - Mathf.InverseLerp(MaxAngularExtrapolateMs, MaxExtrapolateMs, gap));
            float step = Quaternion.Angle(previous.Rot, last.Rot);
            float fraction = angularAhead / span;
            if (step > 0f) fraction = Mathf.Min(fraction, 25f / step);
            rotation = Quaternion.SlerpUnclamped(previous.Rot, last.Rot, 1f + fraction);
        }

        private void ApplyRecovery(long tick, ref Vector3 position, ref Quaternion rotation)
        {
            if (!_recovering) return;
            float elapsed = tick - _recoveryTick;
            if (elapsed < 0f || elapsed >= RecoveryMs) return;

            float weight = 1f - elapsed / RecoveryMs;
            position += _recoveryPosition * weight;
            rotation *= Quaternion.Slerp(Quaternion.identity, _recoveryRotation, weight);
        }

        private void ConvertSample(long tick, Vector3 storedPos, Quaternion storedRot, out Vector3 pos, out Quaternion rot)
        {
            if (SmoothUnderruns)
            {
                var newest = _buf[_buf.Count - 1];
                if (_recovering && (tick < _recoveryTick || tick - _recoveryTick >= RecoveryMs))
                    _recovering = false;
                // Compare the two paths at the current render tick, including any previous correction.
                if (_wasExtrapolating && newest.Tick > _lastPredictionEnd.Tick && tick >= _lastRenderTick &&
                    tick - _lastRenderTick <= RecoveryMs)
                {
                    Predict(_lastPredictionStart, _lastPredictionEnd, tick, out var oldPosition, out var oldRotation);
                    ApplyRecovery(tick, ref oldPosition, ref oldRotation);
                    _recoveryPosition = oldPosition - storedPos;
                    _recoveryRotation = Quaternion.Inverse(storedRot) * oldRotation;
                    _recovering = _recoveryPosition.sqrMagnitude <= 1f &&
                        Quaternion.Angle(Quaternion.identity, _recoveryRotation) <= 35f;
                    _recoveryTick = tick;
                }
                ApplyRecovery(tick, ref storedPos, ref storedRot);
                _wasExtrapolating = tick > newest.Tick;
                if (_wasExtrapolating)
                {
                    _lastPredictionEnd = newest;
                    _lastPredictionStart = _buf.Count >= 2 ? _buf[_buf.Count - 2] : newest;
                }
                _lastRenderTick = tick;
            }
            pos = ToWorldPos != null ? ToWorldPos(storedPos) : storedPos;
            rot = ToWorldRot != null ? ToWorldRot(storedRot) : storedRot;
        }

        public void Clear()
        {
            _buf.Clear();
            _wasExtrapolating = _recovering = false;
        }
    }
}
