using System.Collections.Generic;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal struct DeckForceBatch
    {
        internal Vector3 Linear, Angular;
        internal float Seconds;

        internal void Add(Vector3 force, Vector3 lever, float dt)
        {
            var impulse = force * dt;
            Linear += impulse;
            Angular += Vector3.Cross(lever, impulse);
            Seconds += dt;
        }

        internal void Write(DeckForceRequest msg)
        { msg.LinearImpulse = Linear; msg.AngularImpulse = Angular; msg.Seconds = Seconds; }

        internal float Take(float budget, ref Vector3 linear, ref Vector3 angular)
        {
            if (Seconds <= budget)
            {
                float used = Seconds;
                linear += Linear;
                angular += Angular;
                this = default;
                return used;
            }
            float fraction = budget / Seconds;
            var linearPart = Linear * fraction;
            var angularPart = Angular * fraction;
            linear += linearPart;
            angular += angularPart;
            Linear -= linearPart;
            Angular -= angularPart;
            Seconds -= budget;
            return budget;
        }
    }

    /// <summary>Play ordered, finite input batches without applying an entire network burst at once.</summary>
    internal sealed class DeckForceTimeline
    {
        private readonly Queue<DeckForceBatch> _pending = new Queue<DeckForceBatch>();
        private DeckForceBatch _current;
        private float _seconds;

        internal bool Empty => _current.Seconds == 0f && _pending.Count == 0;

        internal void Enqueue(DeckForceBatch batch)
        {
            _pending.Enqueue(batch);
            _seconds += batch.Seconds;
        }

        internal void Advance(float dt, out Vector3 linear, out Vector3 angular)
        {
            // Catch up gently when more than two batches accumulate.
            float budget = dt * Mathf.Lerp(1f, 2f, (_seconds - 0.12f) / 0.3f);
            linear = Vector3.zero;
            angular = Vector3.zero;
            while (budget > 0f)
            {
                if (_current.Seconds == 0f)
                {
                    if (_pending.Count == 0) break;
                    _current = _pending.Dequeue();
                }
                float used = _current.Take(budget, ref linear, ref angular);
                budget -= used;
                _seconds -= used;
            }
            if (Empty) _seconds = 0f;
        }
    }
}
