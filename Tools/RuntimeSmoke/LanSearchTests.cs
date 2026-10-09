using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using SailwindCoop.Net;

/// <summary>
/// The search of the local network: the two datagrams, the list of games heard, and one real exchange
/// between a LiteNetLib host socket and <see cref="LanSearch"/> on this machine.
/// </summary>
internal static class LanSearchTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static LanHost Sample(ulong id = 77, string name = "Smoke host") => new LanHost
    { SessionId = id, ProtocolVersion = 92, ModVersion = "0.4.3", HostName = name, Players = 2, MaxPlayers = 5, Accepting = true };

    public static void Run(Action<string, Action> test)
    {
        test("LAN search datagrams round-trip and reject anything else", () =>
        {
            var query = new NetDataWriter(); LanBeacon.WriteQuery(query);
            Check(LanBeacon.IsQuery(new NetDataReader(query.Data, 0, query.Length)), "query not recognised");
            var reply = new NetDataWriter(); LanBeacon.WriteReply(reply, Sample());
            Check(!LanBeacon.IsQuery(new NetDataReader(reply.Data, 0, reply.Length)), "reply taken for a query");
            Check(LanBeacon.TryReadReply(new NetDataReader(reply.Data, 0, reply.Length), out LanHost host), "reply not read");
            Check(host.SessionId == 77 && host.ProtocolVersion == 92 && host.ModVersion == "0.4.3" && host.HostName == "Smoke host" &&
                  host.Players == 2 && host.MaxPlayers == 5 && host.Accepting, "reply fields");
            Check(!LanBeacon.TryReadReply(new NetDataReader(query.Data, 0, query.Length), out host), "query taken for a reply");
            for (int length = 0; length < reply.Length; length++)
            {
                var cut = new byte[length]; Buffer.BlockCopy(reply.Data, 0, cut, 0, length);
                Check(!LanBeacon.TryReadReply(new NetDataReader(cut), out host), "cut reply accepted at " + length);
                Check(!LanBeacon.TryReadReply(new NetDataReader(reply.Data, 0, length), out host), "cut pooled reply accepted at " + length);
            }
            Check(!LanBeacon.IsQuery(new NetDataReader(new byte[] { 1, 2, 3 })) && !LanBeacon.IsQuery(new NetDataReader(new byte[64])), "noise taken for a query");
            var longName = new NetDataWriter(); LanBeacon.WriteReply(longName, Sample(name: new string('x', 100)));
            Check(LanBeacon.TryReadReply(new NetDataReader(longName.Data, 0, longName.Length), out host) && host.HostName.Length == LanBeacon.MaxName, "long name not cut");
        });
        test("LAN host list: one row per host, steady address, expiry", () =>
        {
            var list = new LanHostList();
            list.Seen(Sample(), "192.168.1.4", 7777, 0f);
            list.Seen(Sample(), "10.8.0.2", 7777, 0.1f);
            Check(list.Count == 1 && list.Snapshot()[0].Address == "192.168.1.4", "second adapter replaced a live address");
            list.Seen(Sample(), "10.8.0.2", 7777, 4f);
            Check(list.Snapshot()[0].Address == "10.8.0.2", "silent address kept");
            list.Seen(Sample(), "127.0.0.1", 7777, 4.1f);
            list.Seen(Sample(), "10.8.0.2", 7777, 9f);
            Check(list.Snapshot()[0].Address == "127.0.0.1", "loopback not preferred for a host on this PC");
            var changed = Sample(); changed.Players = 3;
            list.Seen(changed, "127.0.0.1", 7777, 9.5f);
            Check(list.Snapshot()[0].Players == 3, "crew size not refreshed");
            list.Seen(Sample(78, "Another"), "192.168.1.9", 7778, 9.5f);
            LanHost[] rows = list.Snapshot();
            Check(rows.Length == 2 && rows[0].HostName == "Another" && rows[0].Port == 7778, "order or second host");
            list.Expire(9.5f + LanHostList.Lifetime - 0.1f); Check(list.Count == 2, "expired early");
            list.Expire(9.5f + LanHostList.Lifetime + 0.1f); Check(list.Count == 0, "silent host kept");
        });
        test("directed broadcast address of an adapter", () =>
        {
            Check(LanSearch.Directed(IPAddress.Parse("192.168.1.4"), IPAddress.Parse("255.255.255.0")).ToString() == "192.168.1.255", "/24");
            Check(LanSearch.Directed(IPAddress.Parse("10.8.3.2"), IPAddress.Parse("255.255.252.0")).ToString() == "10.8.3.255", "/22");
            Check(LanSearch.Directed(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.255")) == null, "/32");
            Check(LanSearch.Directed(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("0.0.0.0")) == null, "no mask");
        });
        test("a host socket answers the search and is listed once", () =>
        {
            var listener = new EventBasedNetListener();
            NetManager host = null;
            int queries = 0;
            listener.NetworkReceiveUnconnectedEvent += (remote, reader, type) =>
            {
                if (!LanBeacon.IsQuery(reader)) return;
                queries++;
                var w = new NetDataWriter(); LanBeacon.WriteReply(w, Sample());
                host.SendUnconnectedMessage(w, remote);
            };
            host = new NetManager(listener) { AutoRecycle = true, UnconnectedMessagesEnabled = true, BroadcastReceiveEnabled = true, IPv6Enabled = false };
            Check(host.Start(), "host socket");
            var search = new LanSearch(null);
            try
            {
                var clock = Stopwatch.StartNew();
                LanHost[] found = new LanHost[0];
                // Past the first answer: the same host also answers the broadcast from its LAN address.
                while (clock.ElapsedMilliseconds < 4000 && (found.Length == 0 || clock.ElapsedMilliseconds < 600))
                {
                    host.PollEvents();
                    search.Tick(true, clock.ElapsedMilliseconds / 1000f, host.LocalPort, host.LocalPort, 0);
                    found = search.Hosts;
                    Thread.Sleep(10);
                }
                Check(queries > 0, "the host heard no query");
                Check(found.Length == 1, "listed " + found.Length + " hosts");
                Check(found[0].HostName == "Smoke host" && found[0].Port == host.LocalPort && found[0].Players == 2 &&
                      found[0].ProtocolVersion == 92 && found[0].Address.Length > 0, "listed host fields");
                search.Tick(false, clock.ElapsedMilliseconds / 1000f);
                Check(!search.Running && search.Hosts.Length == 0, "search kept running or kept its list");
            }
            finally { search.Stop(); host.Stop(); }
        });
    }
}
