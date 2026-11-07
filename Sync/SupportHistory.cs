using System;
using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    // Source samples, rather than arrival-created segments, define the support topology.
    internal sealed class SupportHistory<T>
    {
        private readonly List<T> _samples = new List<T>();
        private readonly Func<T, long> _tick;
        private readonly Func<T, T, bool> _sameSupport;
        private const int Capacity = 128;
        private long _restartTick = long.MinValue;
        internal bool Restarted { get; private set; }
        internal long PlayedTick { get; private set; } = long.MinValue;
        internal int Count => _samples.Count;
        internal T this[int i] => _samples[i];
        internal SupportHistory(Func<T, long> tick, Func<T, T, bool> sameSupport)
        { _tick = tick; _sameSupport = sameSupport; }
        internal bool Push(T sample)
        {
            Restarted = false;
            long tick = _tick(sample);
            if (tick < _restartTick) return false;
            if (Count > 0 && tick < _tick(_samples[Count - 1]) && tick <= PlayedTick) return false;
            int i = Count - 1;
            while (i >= 0 && _tick(_samples[i]) > tick) i--;
            if (i >= 0 && _tick(_samples[i]) == tick)
            {
                if (!_sameSupport(_samples[i], sample)) return false;
                _samples[i] = sample;
                return true;
            }
            if (Count == Capacity)
            {
                // A stalled/unresolvable support must never block current movement.
                // Abandon its backlog and resume from the newest source pose.
                if (tick < _tick(_samples[Count - 1])) return false;
                _samples.Clear(); i = -1;
                _restartTick = tick;
                Restarted = true;
            }
            _samples.Insert(i + 1, sample);
            return true;
        }
        internal int EndOfRun(int start)
        {
            int end = start;
            while (end + 1 < Count && _sameSupport(_samples[start], _samples[end + 1])) end++;
            return end;
        }
        internal void Prune(long tick)
        {
            PlayedTick = Math.Max(PlayedTick, tick);
            int remove = 0;
            while (remove + 1 < Count && _tick(_samples[remove + 1]) <= PlayedTick) remove++;
            if (remove > 0) _samples.RemoveRange(0, remove);
        }
        internal void Clear()
        { _samples.Clear(); PlayedTick = _restartTick = long.MinValue; Restarted = false; }
    }
}
