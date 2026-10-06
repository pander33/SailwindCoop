using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiteNetLib;
using SailwindCoop.Net;

/// <summary>
/// The Steam transport is a datagram tunnel under an unchanged LiteNetLib session. These tests run real
/// LiteNetLib managers through <see cref="DatagramTunnel"/> over an in-memory relay that loses packets,
/// so everything except Steam itself is exercised.
/// </summary>
internal static class TunnelTests
{
    private const string Key = "tunnel-smoke";

    /// <summary>In-memory stand-in for the Steam relay: datagrams addressed by a 64-bit id.</summary>
    private sealed class Hub
    {
        private readonly Dictionary<ulong, Queue<KeyValuePair<ulong, byte[]>>> _queues =
            new Dictionary<ulong, Queue<KeyValuePair<ulong, byte[]>>>();
        private readonly Random _random = new Random(7);
        public double Loss;
        public int Closed;

        public IDatagramRelay Endpoint(ulong id)
        {
            lock (_queues) _queues[id] = new Queue<KeyValuePair<ulong, byte[]>>();
            return new End(this, id);
        }

        private sealed class End : IDatagramRelay
        {
            private readonly Hub _hub;
            private readonly ulong _id;
            public End(Hub hub, ulong id) { _hub = hub; _id = id; }

            public bool Send(ulong peer, byte[] data, int length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                lock (_hub._queues)
                {
                    if (_hub._random.NextDouble() < _hub.Loss) return true;
                    if (_hub._queues.TryGetValue(peer, out var queue))
                        queue.Enqueue(new KeyValuePair<ulong, byte[]>(_id, copy));
                }
                return true;
            }

            public bool TryReceive(byte[] buffer, out ulong peer, out int length)
            {
                peer = 0; length = 0;
                lock (_hub._queues)
                {
                    var queue = _hub._queues[_id];
                    if (queue.Count == 0) return false;
                    var item = queue.Dequeue();
                    peer = item.Key;
                    length = item.Value.Length;
                    Buffer.BlockCopy(item.Value, 0, buffer, 0, length);
                    return true;
                }
            }

            public void Close(ulong peer) { Interlocked.Increment(ref _hub.Closed); }
        }
    }

    private sealed class Node : IDisposable
    {
        public readonly EventBasedNetListener Listener = new EventBasedNetListener();
        public readonly NetManager Net;
        public readonly List<NetPeer> Peers = new List<NetPeer>();
        public readonly List<byte[]> Received = new List<byte[]>();
        public DatagramTunnel Tunnel;

        public Node()
        {
            Net = new NetManager(Listener) { AutoRecycle = true, UpdateTime = 15, IPv6Enabled = false,
                                             UnconnectedMessagesEnabled = false, MaxConnectAttempts = 40, ReconnectDelay = 250 };
            Listener.ConnectionRequestEvent += request => request.AcceptIfKey(Key);
            Listener.PeerConnectedEvent += peer => Peers.Add(peer);
            Listener.PeerDisconnectedEvent += (peer, info) => Peers.Remove(peer);
            Listener.NetworkReceiveEvent += (peer, reader, channel, method) => Received.Add(reader.GetRemainingBytes());
        }

        public void Dispose()
        {
            Net.Stop();
            Tunnel?.Stop();
        }
    }

    private static void Pump(Func<bool> done, int timeoutMs, string what, params Node[] nodes)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new Exception("timed out: " + what);
            foreach (Node node in nodes) node.Net.PollEvents();
            Thread.Sleep(5);
        }
    }

    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    internal static void Run(Action<string, Action> test)
    {
        test("tunnel carries a LiteNetLib session over a lossy relay next to a direct LAN client", () => {
            var hub = new Hub { Loss = 0.1 };
            using (var host = new Node())
            using (var steamA = new Node())
            using (var steamB = new Node())
            using (var lan = new Node())
            {
                Assert(host.Net.Start(), "host start");
                int hostPort = host.Net.LocalPort;
                host.Tunnel = new DatagramTunnel(hub.Endpoint(1));
                host.Tunnel.StartHost(new IPEndPoint(IPAddress.Loopback, hostPort));

                foreach (var pair in new[] { new KeyValuePair<Node, ulong>(steamA, 2), new KeyValuePair<Node, ulong>(steamB, 3) })
                {
                    pair.Key.Tunnel = new DatagramTunnel(hub.Endpoint(pair.Value));
                    int port = pair.Key.Tunnel.StartClient(1);
                    Assert(port != hostPort && port > 0, "client tunnel port");
                    Assert(pair.Key.Net.Start(), "client start");
                    pair.Key.Net.Connect("127.0.0.1", port, Key);
                }
                Assert(lan.Net.Start(), "lan start");
                lan.Net.Connect("127.0.0.1", hostPort, Key);

                Pump(() => host.Peers.Count == 3 && steamA.Peers.Count == 1 && steamB.Peers.Count == 1 && lan.Peers.Count == 1,
                     20000, "three clients connect", host, steamA, steamB, lan);
                Assert(host.Tunnel.LinkCount == 2, "host links " + host.Tunnel.LinkCount);
                var endpoints = new HashSet<string>();
                foreach (NetPeer peer in host.Peers) endpoints.Add(peer.Address + ":" + peer.Port);
                Assert(endpoints.Count == 3, "the host must see three distinct endpoints");

                // A world-sized reliable message survives the loss in both directions.
                var big = new byte[300 * 1024];
                new Random(11).NextBytes(big);
                steamA.Peers[0].Send(big, DeliveryMethod.ReliableOrdered);
                Pump(() => host.Received.Count >= 1, 30000, "big message to host", host, steamA, steamB, lan);
                Assert(Same(host.Received[0], big), "big message corrupted on the way to the host");

                host.Received.Clear();
                foreach (NetPeer peer in host.Peers) peer.Send(big, DeliveryMethod.ReliableOrdered);
                Pump(() => steamA.Received.Count >= 1 && steamB.Received.Count >= 1 && lan.Received.Count >= 1,
                     30000, "big message to every client", host, steamA, steamB, lan);
                Assert(Same(steamA.Received[0], big) && Same(steamB.Received[0], big) && Same(lan.Received[0], big),
                       "big message corrupted on the way to a client");

                // Unreliable snapshots get through at roughly the relay's delivery rate.
                steamB.Received.Clear();
                var small = new byte[400];
                NetPeer toB = null;
                foreach (NetPeer peer in host.Peers)
                    if (peer.Port != lan.Net.LocalPort) toB = peer;   // any tunnelled peer
                for (int i = 0; i < 200; i++)
                {
                    toB.Send(small, DeliveryMethod.Unreliable);
                    host.Net.PollEvents(); steamA.Net.PollEvents(); steamB.Net.PollEvents(); lan.Net.PollEvents();
                    Thread.Sleep(2);
                }
                Pump(() => steamA.Received.Count + steamB.Received.Count >= 120, 5000, "unreliable delivery",
                     host, steamA, steamB, lan);

                // A client that leaves is seen leaving, and the others stay.
                steamA.Net.Stop();
                steamA.Tunnel.Stop();
                Pump(() => host.Peers.Count == 2, 15000, "disconnect is noticed", host, steamB, lan);
                Assert(steamB.Peers.Count == 1 && lan.Peers.Count == 1, "remaining clients dropped");
                Assert(host.Tunnel.LastError == null && steamB.Tunnel.LastError == null, "tunnel error: " + host.Tunnel.LastError + steamB.Tunnel.LastError);
            }
        });

        test("link simulation delays and loses datagrams under an intact LiteNetLib session", () => {
            LinkSimulation.DelayMs = 60; LinkSimulation.JitterMs = 40; LinkSimulation.LossPercent = 10;
            using (var host = new Node())
            using (var client = new Node())
            {
                LagRelay lag = null;
                try
                {
                    Assert(host.Net.Start(), "host start");
                    lag = LagRelay.Open("127.0.0.1", host.Net.LocalPort, out string error);
                    Assert(lag != null, "relay: " + error);
                    client.Tunnel = new DatagramTunnel(lag);
                    Assert(client.Net.Start(), "client start");
                    client.Net.Connect("127.0.0.1", client.Tunnel.StartClient(LagRelay.HostPeer), Key);
                    Pump(() => host.Peers.Count == 1 && client.Peers.Count == 1, 20000, "connect", host, client);

                    // Reliable ordered messages arrive complete and in order despite loss and reordering.
                    for (int i = 0; i < 200; i++) client.Peers[0].Send(BitConverter.GetBytes(i), DeliveryMethod.ReliableOrdered);
                    Pump(() => host.Received.Count >= 200, 30000, "ordered messages", host, client);
                    for (int i = 0; i < 200; i++)
                        Assert(BitConverter.ToInt32(host.Received[i], 0) == i, "message " + i + " out of order");

                    // The delay is real: an echo cannot come back sooner than twice the one-way delay.
                    host.Received.Clear(); client.Received.Clear();
                    var watch = Stopwatch.StartNew();
                    client.Peers[0].Send(new byte[] { 1 }, DeliveryMethod.ReliableOrdered);
                    Pump(() => host.Received.Count >= 1, 10000, "echo request", host, client);
                    host.Peers[0].Send(new byte[] { 2 }, DeliveryMethod.ReliableOrdered);
                    Pump(() => client.Received.Count >= 1, 10000, "echo reply", host, client);
                    Assert(watch.ElapsedMilliseconds >= 120, "round trip took " + watch.ElapsedMilliseconds + " ms, expected at least 120");
                    Assert(LinkSimulation.Dropped > 0 && LinkSimulation.Forwarded > LinkSimulation.Dropped,
                           "counters passed=" + LinkSimulation.Forwarded + " lost=" + LinkSimulation.Dropped);

                    // Leaving still reaches the host: the held disconnect packet is sent on Dispose.
                    LinkSimulation.LossPercent = 0;
                    host.Net.DisconnectTimeout = 5000;
                    client.Net.Stop();
                    client.Tunnel.Stop();
                    lag.Dispose(); lag = null;
                    Pump(() => host.Peers.Count == 0, 1500, "the leave is announced through the relay", host);
                }
                finally
                {
                    lag?.Dispose();
                    LinkSimulation.DelayMs = 0; LinkSimulation.JitterMs = 0; LinkSimulation.LossPercent = 0;
                }
            }
        });

        test("tunnel keeps a session alive while the main thread is blocked", () => {
            var hub = new Hub();
            using (var host = new Node())
            using (var client = new Node())
            {
                host.Net.DisconnectTimeout = 2000;
                client.Net.DisconnectTimeout = 2000;
                Assert(host.Net.Start(), "host start");
                host.Tunnel = new DatagramTunnel(hub.Endpoint(1));
                host.Tunnel.StartHost(new IPEndPoint(IPAddress.Loopback, host.Net.LocalPort));
                client.Tunnel = new DatagramTunnel(hub.Endpoint(2));
                Assert(client.Net.Start(), "client start");
                client.Net.Connect("127.0.0.1", client.Tunnel.StartClient(1), Key);
                Pump(() => host.Peers.Count == 1 && client.Peers.Count == 1, 10000, "connect", host, client);

                // A world load blocks Update for far longer than the disconnect timeout.
                Thread.Sleep(5000);
                host.Net.PollEvents(); client.Net.PollEvents();
                Assert(host.Peers.Count == 1 && client.Peers.Count == 1, "the link timed out while nothing polled it");

                // Leaving is announced, not discovered by the 2 s timeout.
                client.Net.Stop();
                client.Tunnel.Stop();
                Pump(() => host.Peers.Count == 0, 800, "the leave is announced through the tunnel", host);
            }
        });

        test("tunnel refuses oversized datagrams and stops cleanly", () => {
            var hub = new Hub();
            IDatagramRelay other = hub.Endpoint(2);
            var tunnel = new DatagramTunnel(hub.Endpoint(1));
            using (var sink = new Node())
            {
                Assert(sink.Net.Start(), "sink start");
                tunnel.StartHost(new IPEndPoint(IPAddress.Loopback, sink.Net.LocalPort));
                other.Send(1, new byte[DatagramTunnel.MaxDatagram + 1], DatagramTunnel.MaxDatagram + 1);
                Thread.Sleep(200);
                Assert(tunnel.LinkCount == 0 && tunnel.ReceivedFromRelay == 0, "an oversized datagram opened a link");
                other.Send(1, new byte[64], 64);
                Thread.Sleep(200);
                Assert(tunnel.LinkCount == 1 && tunnel.ReceivedFromRelay == 1, "a valid datagram did not open a link");
                tunnel.Stop();
                tunnel.Stop();
                Assert(tunnel.LinkCount == 0, "links survive Stop");
            }
        });
    }
}
