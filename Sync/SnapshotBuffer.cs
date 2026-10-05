using System;
using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    /// <summary>Bounded, sorted timeline. A later result at the same tick replaces even the first sample.</summary>
    internal sealed class SnapshotBuffer<T>
    {
        private readonly List<T> _samples = new List<T>();
        private readonly Func<T, long> _tick;
        private readonly int _capacity;
        internal SnapshotBuffer(Func<T, long> tick, int capacity) { _tick = tick; _capacity = capacity; }
        internal int Count => _samples.Count;
        internal T this[int index] => _samples[index];
        internal void Push(T sample)
        {
            long tick = _tick(sample);
            if (_samples.Count > 0 && tick < _tick(_samples[0])) return;
            int i = _samples.Count - 1;
            while (i >= 0 && _tick(_samples[i]) > tick) i--;
            if (i >= 0 && _tick(_samples[i]) == tick) _samples[i] = sample;
            else _samples.Insert(i + 1, sample);
            while (_samples.Count > _capacity) _samples.RemoveAt(0);
        }
        internal void Clear() => _samples.Clear();
    }
}
