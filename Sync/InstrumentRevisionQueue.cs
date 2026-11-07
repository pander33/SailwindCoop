using System;
using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    /// <summary>Child snapshots wait here until the corresponding item revision has been applied.</summary>
    internal sealed class InstrumentRevisionQueue<T> where T : class
    {
        private readonly Func<T, uint> _revision;
        private readonly Queue<T> _samples = new Queue<T>();
        private const int Capacity = 32;

        internal InstrumentRevisionQueue(Func<T, uint> revision) => _revision = revision;

        internal void Enqueue(T sample)
        {
            if (_samples.Count == Capacity) _samples.Dequeue();
            _samples.Enqueue(sample);
        }

        internal bool TryTake(uint appliedRevision, out T sample)
        {
            while (_samples.Count > 0)
            {
                int order = unchecked((int)(_revision(_samples.Peek()) - appliedRevision));
                if (order > 0) break;
                var next = _samples.Dequeue();
                if (order != 0) continue;
                sample = next;
                return true;
            }
            sample = null;
            return false;
        }

        internal void Clear() => _samples.Clear();
    }
}
