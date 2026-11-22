using System;

namespace SailwindCoop.Sync
{
    /// <summary>Monotonic playout for a snapshot stream, including streams relayed by the host.</summary>
    internal sealed class PlayerPlaybackClock
    {
        private const double ReserveMs = 25;
        private const double FrameJitterMs = 22;
        private const double FrameJitterGain = 0.25;
        private const double MaxAutomaticDelayMs = 900;
        private const double StreamBreakMs = 1000;
        private const double ReleaseMsPerMs = 0.025;
        private long _latestTick;
        private double _lastArrival, _lastFrame;
        private double _observedDelay, _renderTick;
        private bool _hasSample, _started, _wasIdle;
        private readonly double _initialReserveMs;

        internal PlayerPlaybackClock(double initialReserveMs = 75) => _initialReserveMs = initialReserveMs;

        internal void Observe(long tick, long serverNow, long localNow)
            => Observe(tick, (double)serverNow, (double)localNow);

        internal void Observe(long tick, double serverNow, double localNow)
        {
            if (_hasSample && tick <= _latestTick) return;

            double age = serverNow - tick;
            double silence = Math.Max(0, localNow - _lastArrival);
            if (!_hasSample || silence >= StreamBreakMs || tick - _latestTick >= StreamBreakMs)
            {
                _observedDelay = ClampDelay(age + _initialReserveMs);
            }
            else if (_wasIdle)
            {
                // Resuming from deliberate idle throttling: do not treat the keepalive interval as
                // network loss, but keep any higher jitter buffer already adapted for a rough link.
                double resumeDelay = ClampDelay(age + _initialReserveMs);
                _observedDelay = Math.Max(resumeDelay, _observedDelay - silence * ReleaseMsPerMs);
            }
            else
            {
                // Immediately before this arrival, the previous newest sample was all
                // we could interpolate up to. Its age includes gaps from loss and jitter.
                double needed = ClampDelay(serverNow - _latestTick + ReserveMs);
                if (needed < _observedDelay)
                {
                    _observedDelay = Math.Max(needed, _observedDelay - silence * ReleaseMsPerMs);
                }
                else
                {
                    double rise = needed - _observedDelay;
                    _observedDelay = rise <= FrameJitterMs
                        ? _observedDelay + rise * FrameJitterGain
                        : needed;
                }
            }
            _latestTick = tick;
            _lastArrival = localNow;
            _hasSample = true;
            _wasIdle = false;
        }

        /// <summary>Record a deliberately throttled idle keepalive without treating its interval as network loss.</summary>
        internal void ObserveIdle(long tick, double serverNow, double localNow)
        {
            if (_hasSample && tick <= _latestTick) return;

            double age = serverNow - tick;
            double idleDelay = ClampDelay(age + _initialReserveMs);
            if (!_hasSample)
            {
                _observedDelay = idleDelay;
            }
            else
            {
                double silence = Math.Max(0, localNow - _lastArrival);
                if (idleDelay < _observedDelay)
                    _observedDelay = Math.Max(idleDelay, _observedDelay - silence * ReleaseMsPerMs);
            }
            _latestTick = tick;
            _lastArrival = localNow;
            _hasSample = true;
            _wasIdle = true;
        }

        internal long Advance(long serverNow, long localNow, float minimumDelay)
            => (long)Math.Floor(AdvanceExact(serverNow, localNow, minimumDelay));

        internal double AdvanceExact(double serverNow, double localNow, float minimumDelay)
        {
            double target = serverNow - Math.Max(minimumDelay, _observedDelay);
            return AdvanceTowardsExact(target, localNow);
        }

        internal void Reset(bool preservePosition = false)
        {
            _hasSample = false;
            _wasIdle = false;
            _observedDelay = 0;
            if (!preservePosition) _started = false;
        }

        // Keep one continuous timeline even when the source (or its avatar clock) changes.
        internal long AdvanceTowards(double target, long localNow)
            => (long)Math.Floor(AdvanceTowardsExact(target, localNow));

        internal double AdvanceTowardsExact(double target, double localNow)
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
                double elapsed = Math.Max(0, localNow - _lastFrame);
                double nominal = _renderTick + elapsed;
                double correction = (target - nominal) * Math.Min(1, elapsed / 300.0);
                _renderTick = nominal + Math.Max(-elapsed * 0.25, Math.Min(elapsed * 0.15, correction));
            }
            _lastFrame = localNow;
            return _renderTick;
        }

        private static double ClampDelay(double delay) => Math.Max(0, Math.Min(MaxAutomaticDelayMs, delay));
    }
}
