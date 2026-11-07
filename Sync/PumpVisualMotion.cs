using System;

namespace SailwindCoop.Sync
{
    /// <summary>Plays finite batches of remote rotation with continuous angular speed.</summary>
    internal struct PumpVisualMotion
    {
        private const double Response = 1.0 / 0.045;
        private double _remaining, _speed;

        internal void Add(float angle) => _remaining += angle;

        internal float Advance(float dt)
        {
            if (_remaining == 0 && _speed == 0) return 0f;

            // Exact update of a critically damped rotation. A new batch changes
            // acceleration, while speed carries through the packet boundary.
            double previous = _remaining;
            double slope = Response * previous - _speed;
            double decay = Math.Exp(-Response * dt);
            _remaining = (previous + slope * dt) * decay;
            _speed = (_speed + Response * slope * dt) * decay;
            if (Math.Abs(_remaining) < 0.01 && Math.Abs(_speed) < 0.2)
                _remaining = _speed = 0;
            return (float)(previous - _remaining);
        }
    }
}
