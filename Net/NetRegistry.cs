using System.Collections.Generic;

namespace SailwindCoop.Net
{
    /// <summary>Kind of networked object a NetId refers to.</summary>
    public enum NetObjKind : byte
    {
        Player = 1,
        Boat = 2,
        Sail = 3,
        SteeringWheel = 4,
        Rudder = 5,
        Anchor = 6,
        Rope = 7,
    }

    /// <summary>
    /// One networked entry: stable id, kind, owner, and (locally) the bound game object.
    /// <see cref="Target"/> is an <c>object</c> on purpose — the registry stays
    /// independent of game types; Sync layers cast as needed.
    /// </summary>
    public sealed class NetEntry
    {
        public uint NetId;
        public NetObjKind Kind;
        public uint OwnerNetId;     // who has authority; 0 = host/server authority
        public object Target;       // bound UnityEngine.Object on this machine (may be null on host before bind)
    }

    /// <summary>
    /// F2 + F3: maps stable <c>uint NetId</c> &lt;-&gt; game objects, tracks players as a
    /// dictionary (not a "second player" singleton), and records ownership/authority.
    ///
    /// The host is the sole id allocator. Clients only ever learn ids from the host
    /// (via SpawnObject / HelloAck), so ids never collide.
    /// </summary>
    public sealed class NetRegistry
    {
        public const uint HostAuthority = 0;
        public const uint HostPlayerNetId = 1;   // host's own player avatar is always NetId 1

        private readonly Dictionary<uint, NetEntry> _byId = new Dictionary<uint, NetEntry>();
        private readonly Dictionary<uint, NetEntry> _players = new Dictionary<uint, NetEntry>();
        private uint _nextId = 2;                 // ids 0 reserved (host authority), 1 = host player

        /// <summary>Host-only: allocate a fresh, never-reused NetId.</summary>
        public uint AllocateId() => _nextId++;

        /// <summary>Register (or overwrite) an entry. Used on both host and client.</summary>
        public NetEntry Register(uint netId, NetObjKind kind, uint ownerNetId = HostAuthority, object target = null)
        {
            var e = new NetEntry { NetId = netId, Kind = kind, OwnerNetId = ownerNetId, Target = target };
            _byId[netId] = e;
            if (kind == NetObjKind.Player) _players[netId] = e;
            if (netId >= _nextId) _nextId = netId + 1;
            return e;
        }

        public bool TryGet(uint netId, out NetEntry entry) => _byId.TryGetValue(netId, out entry);

        public void Remove(uint netId)
        {
            _byId.Remove(netId);
            _players.Remove(netId);
        }

        /// <summary>True if <paramref name="actorNetId"/> may act on the object (its owner, or host authority).</summary>
        public bool HasAuthority(uint objectNetId, uint actorNetId, bool actorIsHost)
        {
            if (actorIsHost) return true;                 // host can always act
            if (!_byId.TryGetValue(objectNetId, out var e)) return false;
            return e.OwnerNetId == actorNetId;
        }

        public IEnumerable<NetEntry> Players => _players.Values;
        public int PlayerCount => _players.Count;
        public IEnumerable<NetEntry> All => _byId.Values;

        public void Clear()
        {
            _byId.Clear();
            _players.Clear();
            _nextId = 2;
        }
    }
}
