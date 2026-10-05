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
    }
}
