using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    internal sealed class ItemAuthoring<T> where T : class
    {
        private sealed class Request
        {
            internal T Item;
            internal int Prefab;
            internal int HostId;
        }
        private readonly Dictionary<uint, Request> requests = new Dictionary<uint, Request>();
        internal bool Contains(T item)
        {
            foreach (var request in requests.Values)
                if (request.HostId == 0 && ReferenceEquals(request.Item, item)) return true;
            return false;
        }
        internal void Add(uint request, T item, int prefab)
            => requests.Add(request, new Request { Item = item, Prefab = prefab });
        internal T Resolve(uint requester, uint localPlayer, uint request, int prefab, int hostId)
        {
            if (requester != localPlayer || request == 0 || !requests.TryGetValue(request, out var pending) ||
                pending.Prefab != prefab || (pending.HostId != 0 && pending.HostId != hostId)) return null;
            return pending.Item;
        }
        internal void Bind(uint request, int hostId) => requests[request].HostId = hostId;
        internal void Clear() => requests.Clear();
        internal int Count => requests.Count;
        /// <summary>Drops the requests that already have a host id and whose item no longer exists:
        /// nothing can resolve to them again. A request still waiting for its id is kept.</summary>
        internal void Prune(System.Func<T, bool> exists)
        {
            List<uint> gone = null;
            foreach (var pair in requests)
                if (pair.Value.HostId != 0 && !exists(pair.Value.Item)) (gone ?? (gone = new List<uint>())).Add(pair.Key);
            if (gone != null) foreach (uint request in gone) requests.Remove(request);
        }
    }

    /// <summary>
    /// How long each host item has waited for a local counterpart. A wait that goes on is said once
    /// in the log, and from then on the item is retried once a second instead of every frame.
    /// </summary>
    internal sealed class SpawnWait
    {
        internal const float ReportAfter = 10f, SlowRetry = 1f;
        private sealed class Entry { internal float Since, Tried; internal bool Reported; }
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        /// <summary>True when the item is to be tried now. <paramref name="report"/> is true once per wait.</summary>
        internal bool Due(int id, float now, out bool report)
        {
            report = false;
            if (!entries.TryGetValue(id, out var entry)) { entries[id] = new Entry { Since = now, Tried = now }; return true; }
            if (now - entry.Since < ReportAfter) { entry.Tried = now; return true; }
            if (!entry.Reported) { entry.Reported = report = true; }
            if (now - entry.Tried < SlowRetry) return false;
            entry.Tried = now; return true;
        }
        internal void Remove(int id) => entries.Remove(id);
        internal void Clear() => entries.Clear();
        internal int Count => entries.Count;
    }

    internal static class ItemBaseline
    {
        internal static bool CanMatch(bool baselinePacket, bool baselineObject, bool shared, bool awaitingAuthor)
            => baselinePacket && baselineObject && !shared && !awaitingAuthor;
        internal static bool Ready(bool playing, bool loading, int count, float elapsed, float settle)
            => playing && !loading && count >= 0 && elapsed >= settle;
    }

    internal sealed class ItemSaveIdentity
    {
        private readonly Dictionary<int, int> prefabs = new Dictionary<int, int>();
        internal void Add(int id, int prefab) => prefabs[id] = prefab;
        internal bool Contains(int id, int prefab) => prefabs.TryGetValue(id, out var saved) && saved == prefab;
        internal void Clear() => prefabs.Clear();
        internal int Count => prefabs.Count;
    }
}
