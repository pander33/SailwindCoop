using System;
using System.Collections.Generic;

namespace SailwindCoop.Net
{
    /// <summary>Keep arrival order across configuration barriers; obsolete indices can never escape.</summary>
    public sealed class BoatGenerationQueue<T>
    {
        private sealed class Entry { internal ushort Boat; internal uint Generation; internal uint Coalesce; internal T Value; }
        private readonly List<Entry> _entries = new List<Entry>();
        public int Count => _entries.Count;

        /// <param name="coalesce">Nonzero for loss-tolerant (unreliable) snapshots: a newer one of the
        /// same kind replaces the waiting one, exactly as if the older datagram had been lost. Without
        /// it a hull held in the shipyard queues every periodic snapshot for as long as it is held.
        /// Zero keeps every entry — reliable requests and events are never dropped.</param>
        public void Add(ushort boat, uint generation, T value, uint coalesce = 0)
        {
            if (coalesce != 0)
                for (int i = 0; i < _entries.Count; i++)
                {
                    var old = _entries[i];
                    if (old.Coalesce != coalesce || old.Boat != boat || old.Generation != generation) continue;
                    // Re-queued at the tail: the newer snapshot was captured after every event already waiting.
                    _entries.RemoveAt(i);
                    break;
                }
            _entries.Add(new Entry { Boat = boat, Generation = generation, Coalesce = coalesce, Value = value });
        }
        public List<T> TakeReady(BoatGenerationBook book, Func<ushort, bool> suspended)
        {
            var ready = new List<T>();
            for (int i = 0; i < _entries.Count;)
            {
                var entry = _entries[i]; var order = book.Compare(entry.Boat, entry.Generation);
                if (order == GenerationOrder.Past) _entries.RemoveAt(i);
                else if (order == GenerationOrder.Current && !suspended(entry.Boat)) { ready.Add(entry.Value); _entries.RemoveAt(i); }
                else i++;
            }
            return ready;
        }
        public void Clear() => _entries.Clear();
    }
}
