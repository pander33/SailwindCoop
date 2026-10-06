using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SailwindCoop.Net
{
    /// <summary>
    /// Settings and counters of the development link simulation (Debug panel). The values are read by
    /// the tunnel thread on every datagram, so they can be changed while a session runs.
    /// </summary>
    public static class LinkSimulation
    {
        /// <summary>The next LAN join goes through <see cref="LagRelay"/>.</summary>
        public static volatile bool Enabled;
        /// <summary>Delay added to every datagram in each direction, ms (round trip grows by twice this).</summary>
        public static volatile int DelayMs;
        /// <summary>Random extra delay 0..JitterMs per datagram; this is what reorders packets.</summary>
        public static volatile int JitterMs;
        /// <summary>Chance to lose a datagram, percent, each direction.</summary>
        public static volatile int LossPercent;

        private static long _forwarded, _dropped;
        public static long Forwarded => Interlocked.Read(ref _forwarded);
        public static long Dropped => Interlocked.Read(ref _dropped);
        internal static void CountForwarded() => Interlocked.Increment(ref _forwarded);
        internal static void CountDropped() => Interlocked.Increment(ref _dropped);
        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _forwarded, 0);
            Interlocked.Exchange(ref _dropped, 0);
        }
    }

    /// <summary>
    /// Development only: a bad link between a LAN client and its host. The local LiteNetLib client
    /// talks to a <see cref="DatagramTunnel"/>, and this relay carries the tunnel's datagrams to the
    /// real host over UDP, holding each one for <see cref="LinkSimulation.DelayMs"/> plus jitter and
    /// losing some. It works on whole datagrams below LiteNetLib, so resends, ordering and
    /// fragmentation react as they would on a real network. One side simulating covers both directions.
    ///
    /// As <see cref="IDatagramRelay"/> requires, every member except <see cref="Dispose"/> runs on the
    /// tunnel thread; Dispose is called after the tunnel has stopped.
    /// </summary>
    public sealed class LagRelay : IDatagramRelay, IDisposable
    {
        /// <summary>The id the tunnel knows the host by; there is only one peer.</summary>
        public const ulong HostPeer = 1UL;
        private const int MaxQueued = 8192;

        private struct Held
        {
            public long Due;
            public byte[] Data;
        }

        private readonly Socket _socket;
        private readonly List<Held> _toHost = new List<Held>();
        private readonly List<Held> _toClient = new List<Held>();
        private readonly Random _random = new Random();
        private readonly byte[] _scratch = new byte[2048];
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private LagRelay(Socket socket) { _socket = socket; }

        /// <summary>Opens the relay towards the host, or returns null with <paramref name="error"/> set.</summary>
        public static LagRelay Open(string host, int port, out string error)
        {
            error = null;
            Socket socket = null;
            try
            {
                if (!IPAddress.TryParse(host, out IPAddress address))
                {
                    address = null;
                    foreach (IPAddress candidate in Dns.GetHostAddresses(host))
                        if (candidate.AddressFamily == AddressFamily.InterNetwork) { address = candidate; break; }
                }
                if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = "no IPv4 address for " + host;
                    return null;
                }
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Blocking = false;
                // See DatagramTunnel.NewSocket: an ICMP "port unreachable" must not look like a reset.
                try { socket.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
                socket.Connect(new IPEndPoint(address, port));
                LinkSimulation.ResetCounters();
                return new LagRelay(socket);
            }
            catch (Exception e)
            {
                try { socket?.Close(); } catch { }
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        public bool Send(ulong peer, byte[] data, int length)
        {
            Hold(_toHost, data, length);
            return true;
        }

        public bool TryReceive(byte[] buffer, out ulong peer, out int length)
        {
            peer = HostPeer;
            length = 0;
            Pump();
            long now = _clock.ElapsedMilliseconds;
            for (int i = 0; i < _toClient.Count; i++)
            {
                if (_toClient[i].Due > now) continue;
                byte[] data = _toClient[i].Data;
                _toClient.RemoveAt(i);
                if (data.Length > buffer.Length) return false;
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                length = data.Length;
                return true;
            }
            return false;
        }

        public void Close(ulong peer) { }

        /// <summary>Sends what is still held for the host (LiteNetLib's disconnect packets) and closes.</summary>
        public void Dispose()
        {
            try
            {
                foreach (Held held in _toHost) _socket.Send(held.Data, 0, held.Data.Length, SocketFlags.None);
            }
            catch { }
            _toHost.Clear();
            _toClient.Clear();
            try { _socket.Close(); } catch { }
        }

        private void Pump()
        {
            long now = _clock.ElapsedMilliseconds;
            for (int i = 0; i < _toHost.Count; i++)
            {
                if (_toHost[i].Due > now) continue;
                byte[] data = _toHost[i].Data;
                _toHost.RemoveAt(i--);
                try { _socket.Send(data, 0, data.Length, SocketFlags.None); }
                catch (SocketException) { }   // a lost datagram; LiteNetLib resends what matters
            }
            try
            {
                while (_socket.Available > 0)
                {
                    int length = _socket.Receive(_scratch, 0, _scratch.Length, SocketFlags.None);
                    Hold(_toClient, _scratch, length);
                }
            }
            catch (SocketException) { }
        }

        private void Hold(List<Held> queue, byte[] data, int length)
        {
            if (length <= 0) return;
            int loss = LinkSimulation.LossPercent;
            if ((loss > 0 && _random.Next(100) < loss) || queue.Count >= MaxQueued)
            {
                LinkSimulation.CountDropped();
                return;
            }
            int jitter = LinkSimulation.JitterMs;
            int delay = Math.Max(0, LinkSimulation.DelayMs) + (jitter > 0 ? _random.Next(jitter + 1) : 0);
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            queue.Add(new Held { Due = _clock.ElapsedMilliseconds + delay, Data = copy });
            LinkSimulation.CountForwarded();
        }
    }
}
