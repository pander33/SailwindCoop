using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SailwindCoop.Net
{
    /// <summary>
    /// A way to carry datagrams to a peer known by a 64-bit id instead of an IP address.
    /// Every member is called from the tunnel thread only.
    /// </summary>
    public interface IDatagramRelay
    {
        bool Send(ulong peer, byte[] data, int length);
        /// <summary>Reads one datagram into <paramref name="buffer"/>; false when nothing is waiting.</summary>
        bool TryReceive(byte[] buffer, out ulong peer, out int length);
        void Close(ulong peer);
    }

    /// <summary>
    /// Carries the UDP datagrams of an unchanged LiteNetLib session over an <see cref="IDatagramRelay"/>.
    /// LiteNetLib keeps talking plain UDP to a loopback socket owned by this class, so the handshake,
    /// reliability, fragmentation and every <c>NetPeer</c> in the Sync layer work exactly as on a LAN.
    ///
    /// Host: one loopback socket per remote peer, each "connected" to the host's own UDP port; the host
    /// sees every relayed player as a separate 127.0.0.1 endpoint. Client: one loopback socket the local
    /// LiteNetLib client connects to instead of the host's address.
    ///
    /// The forwarding runs on its own thread. LiteNetLib keeps its connection alive from background
    /// threads, and a main thread blocked by a world load must not starve the link into a timeout.
    /// The thread touches sockets and the relay only (F5: no Unity API off the main thread).
    /// </summary>
    public sealed class DatagramTunnel
    {
        /// <summary>Largest datagram forwarded; LiteNetLib's default MTU (508) is well below it.</summary>
        public const int MaxDatagram = 1200;
        private const int MaxLinks = 64;
        private const int IdleMs = 30000;
        private const int BatchLimit = 512;

        private sealed class Link
        {
            public ulong Peer;
            public Socket Socket;
            public long LastSeen;
        }

        private readonly IDatagramRelay _relay;
        private readonly List<Link> _links = new List<Link>();   // tunnel thread only
        private Thread _thread;
        private volatile bool _running;
        private bool _hostMode;
        private IPEndPoint _server;      // host mode: the local LiteNetLib host
        private ulong _hostPeer;         // client mode: the remote host
        private Socket _client;          // client mode: the socket the local LiteNetLib client talks to
        private bool _clientBound;       // client mode: the local client's endpoint is known
        private long _lastExpire;
        private long _toRelay, _fromRelay;
        private int _linkCount;
        private volatile string _lastError;

        public DatagramTunnel(IDatagramRelay relay)
        {
            _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        }

        public string LastError => _lastError;
        public long SentToRelay => Interlocked.Read(ref _toRelay);
        public long ReceivedFromRelay => Interlocked.Read(ref _fromRelay);
        public int LinkCount => _linkCount;

        /// <summary>Host: forward every relayed peer to the LiteNetLib host listening on <paramref name="server"/>.</summary>
        public void StartHost(IPEndPoint server)
        {
            Stop();
            _hostMode = true;
            _server = server;
            StartThread();
        }

        /// <summary>Client: returns the loopback port the local LiteNetLib client must connect to.</summary>
        public int StartClient(ulong hostPeer)
        {
            Stop();
            _hostMode = false;
            _hostPeer = hostPeer;
            _clientBound = false;
            _client = NewSocket(IPAddress.Loopback);
            int port = ((IPEndPoint)_client.LocalEndPoint).Port;
            StartThread();
            return port;
        }

        public void Stop()
        {
            _running = false;
            Thread thread = _thread;
            _thread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                thread.Join(1000);
                // LiteNetLib writes its disconnect packets while it stops, just before this call. Forward
                // what is already in the sockets, or the other side learns of the leave by timeout only.
                try
                {
                    var buffer = new byte[2048];
                    if (_hostMode) DrainLinks(buffer);
                    else if (_client != null) DrainClient(buffer);
                }
                catch { }
            }
            foreach (Link link in _links) CloseQuietly(link.Socket);
            _links.Clear();
            _linkCount = 0;
            CloseQuietly(_client);
            _client = null;
        }

        private void StartThread()
        {
            _lastError = null;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "SailwindCoop tunnel" };
            _thread.Start();
        }

        private void Run()
        {
            var buffer = new byte[2048];
            while (_running)
            {
                bool worked = false;
                try
                {
                    worked |= DrainRelay(buffer);
                    worked |= _hostMode ? DrainLinks(buffer) : DrainClient(buffer);
                    if (_hostMode) Expire();
                }
                catch (ObjectDisposedException) { }   // Stop closed a socket under us
                catch (Exception e)
                {
                    _lastError = e.GetType().Name + ": " + e.Message;
                    Thread.Sleep(50);
                }
                if (!worked) Thread.Sleep(1);
            }
        }

        private bool DrainRelay(byte[] buffer)
        {
            int count = 0;
            while (count < BatchLimit && _relay.TryReceive(buffer, out ulong peer, out int length))
            {
                count++;
                if (length <= 0 || length > MaxDatagram) continue;
                Interlocked.Increment(ref _fromRelay);
                try
                {
                    if (_hostMode)
                    {
                        Link link = LinkFor(peer);
                        if (link == null) continue;
                        link.LastSeen = Now;
                        link.Socket.Send(buffer, 0, length, SocketFlags.None);
                    }
                    else if (peer == _hostPeer && _clientBound)
                    {
                        _client.Send(buffer, 0, length, SocketFlags.None);
                    }
                }
                catch (SocketException) { }   // a lost datagram; LiteNetLib resends what matters
            }
            return count > 0;
        }

        private bool DrainLinks(byte[] buffer)
        {
            int count = 0;
            for (int i = 0; i < _links.Count; i++)
            {
                Link link = _links[i];
                try
                {
                    while (count < BatchLimit && link.Socket.Available > 0)
                    {
                        int length = link.Socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        count++;
                        Forward(link.Peer, buffer, length);
                    }
                }
                catch (SocketException) { }
            }
            return count > 0;
        }

        private bool DrainClient(byte[] buffer)
        {
            int count = 0;
            try
            {
                while (count < BatchLimit && _client.Available > 0)
                {
                    int length;
                    if (_clientBound)
                    {
                        length = _client.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                    }
                    else
                    {
                        // The first datagram reveals which port the local LiteNetLib client uses; from
                        // then on the socket talks to that endpoint only.
                        EndPoint from = new IPEndPoint(IPAddress.Loopback, 0);
                        length = _client.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                        var ep = (IPEndPoint)from;
                        if (!IPAddress.IsLoopback(ep.Address)) continue;
                        _client.Connect(ep);
                        _clientBound = true;
                    }
                    count++;
                    Forward(_hostPeer, buffer, length);
                }
            }
            catch (SocketException) { }
            return count > 0;
        }

        private void Forward(ulong peer, byte[] buffer, int length)
        {
            if (length <= 0 || length > MaxDatagram) return;
            if (_relay.Send(peer, buffer, length)) Interlocked.Increment(ref _toRelay);
        }

        private Link LinkFor(ulong peer)
        {
            for (int i = 0; i < _links.Count; i++)
                if (_links[i].Peer == peer) return _links[i];
            if (_links.Count >= MaxLinks) return null;
            // A host bound to one interface (Network/ListenIp) is reached from that same address.
            Socket socket = NewSocket(IPAddress.IsLoopback(_server.Address) ? IPAddress.Loopback : _server.Address);
            socket.Connect(_server);
            var link = new Link { Peer = peer, Socket = socket, LastSeen = Now };
            _links.Add(link);
            _linkCount = _links.Count;
            return link;
        }

        /// <summary>Drops peers that went silent: a connected LiteNetLib peer pings every second.</summary>
        private void Expire()
        {
            long now = Now;
            if (now - _lastExpire < 1000) return;
            _lastExpire = now;
            for (int i = _links.Count - 1; i >= 0; i--)
            {
                Link link = _links[i];
                if (now - link.LastSeen < IdleMs) continue;
                CloseQuietly(link.Socket);
                _links.RemoveAt(i);
                _relay.Close(link.Peer);
            }
            _linkCount = _links.Count;
        }

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static long Now => Clock.ElapsedMilliseconds;

        private static Socket NewSocket(IPAddress local)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Blocking = false;
            // Windows reports an ICMP "port unreachable" as a reset on the next receive (SIO_UDP_CONNRESET);
            // for a datagram socket that is noise, not a closed connection.
            try { socket.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
            socket.Bind(new IPEndPoint(local, 0));
            return socket;
        }

        private static void CloseQuietly(Socket socket)
        {
            try { socket?.Close(); } catch { }
        }
    }
}
