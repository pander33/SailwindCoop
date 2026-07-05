using System.Diagnostics;

namespace SailwindCoop.Net
{
    /// <summary>
    /// F4 — shared network time. Both sides keep a monotonic millisecond clock
    /// (<see cref="LocalTick"/>). The client estimates the host clock offset and
    /// round-trip time from TimeSync ping-pongs so that snapshot ticks from the
    /// host can be expressed on a common timeline for interpolation.
    ///
    /// On the host, offset is 0 (it IS the authority). On the client, offset is a
    /// smoothed estimate of (hostClock - localClock).
    /// </summary>
    public sealed class NetClock
    {
        private static readonly Stopwatch _sw = Stopwatch.StartNew();

        // Smoothed estimates.
        private double _offsetMs;     // hostClock - localClock
        private double _rttMs;
        private bool _hasSample;

        /// <summary>Monotonic local clock in milliseconds since plugin start.</summary>
        public long LocalTick => _sw.ElapsedMilliseconds;

        /// <summary>Best estimate of the host's clock right now (ms).</summary>
        public long ServerTick => LocalTick + (long)_offsetMs;

        public double RttMs => _rttMs;
        public double OffsetMs => _offsetMs;
        public bool HasSample => _hasSample;

        /// <summary>
        /// Feed a completed round trip. <paramref name="clientSendTick"/> is our
        /// local tick when we sent; <paramref name="serverTick"/> is the host clock
        /// it reported; this is called the moment the reply arrives.
        /// </summary>
        public void OnReply(long clientSendTick, long serverTick)
        {
            long now = LocalTick;
            double rtt = now - clientSendTick;
            if (rtt < 0) rtt = 0;

            // Assume symmetric latency: host's clock at our 'now' ≈ serverTick + rtt/2.
            double offset = (serverTick + rtt * 0.5) - now;

            if (!_hasSample)
            {
                _rttMs = rtt;
                _offsetMs = offset;
                _hasSample = true;
            }
            else
            {
                // Exponential smoothing — robust to jitter without lagging badly.
                const double a = 0.1;
                _rttMs = _rttMs * (1 - a) + rtt * a;
                _offsetMs = _offsetMs * (1 - a) + offset * a;
            }
        }

        public void Reset()
        {
            _offsetMs = 0;
            _rttMs = 0;
            _hasSample = false;
        }
    }
}
