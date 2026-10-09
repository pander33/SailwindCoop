using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    /// <summary>A hosted game as another machine of the local network sees it.</summary>
    public sealed class LanHost
    {
        public ulong SessionId;
        public int ProtocolVersion;
        public string ModVersion = "";
        public string HostName = "";
        public int Players;
        public int MaxPlayers;
        public bool Accepting;
        // Filled by the searching side from the datagram's source.
        public string Address = "";
        public int Port;
        public float SeenAt;
        internal float AddressSeenAt;
    }

    /// <summary>
    /// The two datagrams of the search. They travel outside a connection (LiteNetLib "unconnected"
    /// packets), so they are not <see cref="MsgType"/> messages and builds with different protocol
    /// numbers still see each other: the reply says which protocol the host speaks.
    /// </summary>
    public static class LanBeacon
    {
        private const uint Magic = 0x50435753;   // "SWCP"
        private const byte Query = 1, Reply = 2;
        public const int MaxName = 32;

        public static void WriteQuery(NetDataWriter w) { w.Put(Magic); w.Put(Query); }

        public static bool IsQuery(NetDataReader r)
        {
            try { return r.AvailableBytes == 5 && r.GetUInt() == Magic && r.GetByte() == Query; }
            catch { return false; }
        }

        public static void WriteReply(NetDataWriter w, LanHost host)
        {
            w.Put(Magic); w.Put(Reply);
            w.Put(host.SessionId);
            w.Put(host.ProtocolVersion);
            w.Put(ModVersions.Clean(host.ModVersion));
            string name = (host.HostName ?? "").Trim();
            w.Put(name.Length > MaxName ? name.Substring(0, MaxName) : name);
            w.Put((byte)Math.Max(0, Math.Min(255, host.Players)));
            w.Put((byte)Math.Max(0, Math.Min(255, host.MaxPlayers)));
            w.Put(host.Accepting);
        }

        public static bool TryReadReply(NetDataReader r, out LanHost host)
        {
            host = null;
            try
            {
                if (r.AvailableBytes < 5 || r.GetUInt() != Magic || r.GetByte() != Reply) return false;
                var read = new LanHost
                {
                    SessionId = r.GetULong(),
                    ProtocolVersion = r.GetInt(),
                    ModVersion = r.GetString(ModVersions.MaxLength),
                    HostName = r.GetString(MaxName),
                    Players = r.GetByte(),
                    MaxPlayers = r.GetByte(),
                    Accepting = r.GetBool(),
                };
                // A getter can run past the datagram into the pooled buffer; such a reply is not one.
                if (r.AvailableBytes < 0 || read.SessionId == 0) return false;
                host = read;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>The games heard recently. A host answers from every address it has, and is one row.</summary>
    public sealed class LanHostList
    {
        /// <summary>A host that stopped answering leaves the list after this many seconds.</summary>
        public const float Lifetime = 5f;
        // One address is kept while it keeps answering, so a host with two adapters does not flicker.
        private const float AddressHold = 3f;
        private readonly Dictionary<ulong, LanHost> _hosts = new Dictionary<ulong, LanHost>();

        public int Count => _hosts.Count;

        public void Seen(LanHost host, string address, int port, float now)
        {
            if (host == null || string.IsNullOrEmpty(address)) return;
            host.SeenAt = now;
            bool loopback = IsLoopback(address);
            if (_hosts.TryGetValue(host.SessionId, out LanHost known))
            {
                bool same = known.Address == address && known.Port == port;
                // The same PC answers on loopback too, and that address needs no firewall rule.
                bool take = same || (loopback && !IsLoopback(known.Address)) ||
                            (!IsLoopback(known.Address) && now - known.AddressSeenAt > AddressHold);
                host.Address = take ? address : known.Address;
                host.Port = take ? port : known.Port;
                host.AddressSeenAt = take ? now : known.AddressSeenAt;
            }
            else { host.Address = address; host.Port = port; host.AddressSeenAt = now; }
            _hosts[host.SessionId] = host;
        }

        public void Expire(float now)
        {
            List<ulong> gone = null;
            foreach (var pair in _hosts)
                if (now - pair.Value.SeenAt > Lifetime) (gone ?? (gone = new List<ulong>())).Add(pair.Key);
            if (gone != null) foreach (ulong id in gone) _hosts.Remove(id);
        }

        /// <summary>In a fixed order, so rows do not jump while the list is on screen.</summary>
        public LanHost[] Snapshot()
        {
            var list = new List<LanHost>(_hosts.Values);
            list.Sort((a, b) =>
            {
                int byName = string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : a.SessionId.CompareTo(b.SessionId);
            });
            return list.ToArray();
        }

        public void Clear() { _hosts.Clear(); }

        private static bool IsLoopback(string address) => address != null && address.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>
    /// The searching side: while it is wanted it asks the local network who hosts a game and keeps
    /// the answers. It has a socket of its own, so it runs before any session exists. Called from the
    /// Unity main thread only (F5).
    /// </summary>
    public sealed class LanSearch
    {
        public const float QueryInterval = 1.5f;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly LanHostList _list = new LanHostList();
        private readonly Action<string> _log;
        private NetManager _net;
        private float _now, _nextQuery, _retryAt, _addressesAt = float.NegativeInfinity;
        private List<IPAddress> _broadcast = new List<IPAddress>();

        public LanSearch(Action<string> log)
        {
            _log = log ?? (_ => { });
            _listener.NetworkReceiveUnconnectedEvent += OnDatagram;
        }

        public bool Running => _net != null;
        public LanHost[] Hosts => _list.Snapshot();

        /// <summary>Every frame. <paramref name="ports"/> are the ports a host may listen on.</summary>
        public void Tick(bool wanted, float now, params int[] ports)
        {
            if (!wanted) { Stop(); return; }
            _now = now;
            if (_net == null && !Start(now)) return;
            _net.PollEvents();
            _list.Expire(now);
            if (now < _nextQuery) return;
            _nextQuery = now + QueryInterval;
            for (int i = 0; i < ports.Length; i++)
                if (ports[i] > 0 && ports[i] <= 65535 && Array.IndexOf(ports, ports[i]) == i) Query(ports[i]);
        }

        public void Stop()
        {
            if (_net != null) { try { _net.Stop(); } catch { } _net = null; }
            _list.Clear();
            _nextQuery = 0f;
        }

        private bool Start(float now)
        {
            if (now < _retryAt) return false;
            var net = new NetManager(_listener) { AutoRecycle = true, UnconnectedMessagesEnabled = true, IPv6Enabled = false };
            if (!net.Start())
            {
                _retryAt = now + 5f;
                _log("[LanSearch] Could not open a socket to look for games on the local network");
                return false;
            }
            _net = net;
            _nextQuery = 0f;
            return true;
        }

        private void Query(int port)
        {
            try
            {
                var w = new NetDataWriter();
                LanBeacon.WriteQuery(w);
                // The limited broadcast leaves through one adapter only; each adapter's own broadcast
                // address covers the others (a second network, a VPN adapter), and loopback covers a
                // host on this PC whatever the firewall thinks of broadcasts.
                _net.SendBroadcast(w, port);
                foreach (IPAddress address in BroadcastAddresses()) _net.SendUnconnectedMessage(w, new IPEndPoint(address, port));
                _net.SendUnconnectedMessage(w, new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (Exception e) { _log("[LanSearch] Query failed: " + e.Message); }
        }

        private void OnDatagram(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
        {
            if (remote == null || !LanBeacon.TryReadReply(reader, out LanHost host)) return;
            _list.Seen(host, remote.Address.ToString(), remote.Port, _now);
        }

        /// <summary>The directed broadcast address of every working IPv4 adapter; re-read now and then.</summary>
        private List<IPAddress> BroadcastAddresses()
        {
            if (_now - _addressesAt < 10f) return _broadcast;
            _addressesAt = _now;
            var found = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                        IPAddress directed = Directed(unicast.Address, unicast.IPv4Mask);
                        if (directed != null && !found.Contains(directed)) found.Add(directed);
                    }
                }
            }
            catch (Exception e) { _log("[LanSearch] Could not list network adapters: " + e.Message); }
            _broadcast = found;
            return found;
        }

        /// <summary>Address with every host bit set; null for a mask that leaves no network (0.0.0.0, /32).</summary>
        public static IPAddress Directed(IPAddress address, IPAddress mask)
        {
            byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
            if (a.Length != 4 || m.Length != 4) return null;
            bool any = false, all = true;
            var result = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                result[i] = (byte)(a[i] | ~m[i]);
                if (m[i] != 0) any = true;
                if (m[i] != 255) all = false;
            }
            return any && !all ? new IPAddress(result) : null;
        }
    }
}
