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

        // Sliding window of recent RTT samples to reject asymmetric latency spikes.
        private const int WindowSize = 8;
        private const double MaxSlewPerReplyMs = 3.0;
        private const double SnapThresholdMs = 500.0;
        private readonly double[] _rttWindow = new double[WindowSize];
        private readonly double[] _scratch = new double[WindowSize];
        private int _windowCount;
        private int _windowIndex;

        /// <summary>Monotonic local clock in milliseconds since plugin start.</summary>
        public long LocalTick => _sw.ElapsedMilliseconds;

        /// <summary>Sub-millisecond monotonic local clock in milliseconds since plugin start.</summary>
        public double LocalTimeMs => _sw.Elapsed.TotalMilliseconds;

        /// <summary>Best estimate of the host's clock right now (ms).</summary>
        public long ServerTick => LocalTick + (long)System.Math.Round(_offsetMs);

        /// <summary>Sub-millisecond estimate of the host's clock right now (ms).</summary>
        public double ServerTimeMs => LocalTimeMs + _offsetMs;

        public double RttMs => _rttMs;
        public double OffsetMs => _offsetMs;
        public bool HasSample => _hasSample;

        /// <summary>
        /// Feed a completed round trip. <paramref name="clientSendTick"/> is our
        /// local tick when we sent; <paramref name="serverTick"/> is the host clock
        /// it reported; this is called the moment the reply arrives.
        /// </summary>
        public void OnReply(long clientSendTick, long serverTick)
            => OnReply(clientSendTick, serverTick, LocalTick);

        public void OnReply(long clientSendTick, long serverTick, long now)
        {
            double rtt = now - clientSendTick;
            if (rtt < 0) rtt = 0;

            // Assume symmetric latency: host's clock at our 'now' ≈ serverTick + rtt/2.
            double offset = (serverTick + rtt * 0.5) - now;

            if (!_hasSample)
            {
                _rttMs = rtt;
                _offsetMs = offset;
                _hasSample = true;
                RecordRtt(rtt);
                return;
            }

            // Evaluate spike against the existing window before recording this sample so a large
            // stall cannot distort its own median or bypass the filter via SnapThresholdMs.
            GetWindowStats(out double minRtt, out double medianRtt);
            bool spike = _windowCount >= 3 && rtt > medianRtt + 20.0 && rtt > minRtt * 1.5 + 10.0;
            RecordRtt(rtt);

            const double a = 0.1;
            _rttMs = _rttMs * (1 - a) + rtt * a;

            double err = offset - _offsetMs;
            if (!spike && System.Math.Abs(err) > SnapThresholdMs)
            {
                _offsetMs = offset;
                return;
            }

            double weight = spike ? 0.02 : a;
            double delta = err * weight;
            if (_windowCount >= 4)
            {
                if (delta > MaxSlewPerReplyMs) delta = MaxSlewPerReplyMs;
                else if (delta < -MaxSlewPerReplyMs) delta = -MaxSlewPerReplyMs;
            }
            _offsetMs += delta;
        }

        private void RecordRtt(double rtt)
        {
            _rttWindow[_windowIndex] = rtt;
            _windowIndex = (_windowIndex + 1) % WindowSize;
            if (_windowCount < WindowSize) _windowCount++;
        }

        private void GetWindowStats(out double min, out double median)
        {
            if (_windowCount == 0) { min = median = _rttMs; return; }
            min = double.MaxValue;
            for (int i = 0; i < _windowCount; i++)
            {
                double v = _rttWindow[i];
                _scratch[i] = v;
                if (v < min) min = v;
            }
            System.Array.Sort(_scratch, 0, _windowCount);
            median = _scratch[_windowCount / 2];
        }

        public void Reset()
        {
            _offsetMs = 0;
            _rttMs = 0;
            _hasSample = false;
            _windowCount = 0;
            _windowIndex = 0;
        }
    }
}
