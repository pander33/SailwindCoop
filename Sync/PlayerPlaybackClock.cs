using System;

namespace SailwindCoop.Sync
{
    /// <summary>Monotonic playout for a snapshot stream, including streams relayed by the host.</summary>
    internal sealed class PlayerPlaybackClock
    {
        private const double ReserveMs = 25;
        private const double MaxAutomaticDelayMs = 900;
        private const long StreamBreakMs = 1000;
        private const double ReleaseMsPerMs = 0.025;
        private long _latestTick, _lastArrival, _lastFrame;
        private double _observedDelay, _renderTick;
        private bool _hasSample, _started;
        private readonly double _initialReserveMs;

        internal PlayerPlaybackClock(double initialReserveMs = 75) => _initialReserveMs = initialReserveMs;

        internal void Observe(long tick, long serverNow, long localNow)
        {
            if (_hasSample && tick <= _latestTick) return;

            double age = serverNow - tick;
            long silence = localNow - _lastArrival;
            if (!_hasSample || silence >= StreamBreakMs || tick - _latestTick >= StreamBreakMs)
            {
                _observedDelay = ClampDelay(age + _initialReserveMs);
            }
            else
            {
                // Immediately before this arrival, the previous newest sample was all
                // we could interpolate up to. Its age includes gaps from loss and jitter.
                double needed = ClampDelay(serverNow - _latestTick + ReserveMs);
                _observedDelay = Math.Max(needed, _observedDelay - silence * ReleaseMsPerMs);
            }
            _latestTick = tick;
            _lastArrival = localNow;
            _hasSample = true;
        }

        internal long Advance(long serverNow, long localNow, float minimumDelay)
        {
            double target = serverNow - Math.Max(minimumDelay, _observedDelay);
            return AdvanceTowards(target, localNow);
        }

        internal void Reset(bool preservePosition = false)
        {
            _hasSample = false;
            _observedDelay = 0;
            if (!preservePosition) _started = false;
        }

        // Keep one continuous timeline even when the source (or its avatar clock) changes.
        internal long AdvanceTowards(double target, long localNow)
        {
            if (!_started)
            {
                _renderTick = target;
                _started = true;
            }
            else
            {
                // Advance on the local monotonic clock. Delay and clock-offset changes
                // adjust playback speed without making the avatar jump back or forward.
                long elapsed = localNow - _lastFrame;
                double nominal = _renderTick + elapsed;
                double correction = (target - nominal) * Math.Min(1, elapsed / 300.0);
                _renderTick = nominal + Math.Max(-elapsed * 0.25, Math.Min(elapsed * 0.15, correction));
            }
            _lastFrame = localNow;
            return (long)Math.Floor(_renderTick);
        }

        private static double ClampDelay(double delay) => Math.Max(0, Math.Min(MaxAutomaticDelayMs, delay));
    }
}
