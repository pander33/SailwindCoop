using System;

namespace SailwindCoop.Sync
{
    /// <summary>Tracks the hull timeline and plays a gap in the hinge stream back at a bounded rate.</summary>
    internal sealed class HingePlayback
    {
        private const double CatchUpRate = 1.35;
        private double _tick, _lastLocal;
        private long _restStart, _restEnd;
        private bool _started;

        internal void MarkRest(long from, long to)
        {
            _restStart = from;
            _restEnd = to;
        }

        internal long Advance(long hullTick, long newestTick, long localNow)
            => (long)Math.Floor(AdvanceExact(hullTick, newestTick, localNow));

        internal double AdvanceExact(double hullTick, long newestTick, double localNow)
        {
            double target = Math.Min(hullTick, (double)newestTick);
            if (!_started)
            {
                _started = true;
                _tick = target;
            }
            else if (target > _tick)
            {
                // Silence while resting contains no motion to replay. Never skip a moving interval.
                if (_tick >= _restStart && _tick < _restEnd) _tick = Math.Min(target, _restEnd);
                _tick = Math.Min(target, _tick + Math.Max(0, localNow - _lastLocal) * CatchUpRate);
            }
            _lastLocal = localNow;
            return _tick;
        }

        internal void Clear()
        {
            _started = false;
            _restStart = _restEnd = 0;
        }
    }
}
