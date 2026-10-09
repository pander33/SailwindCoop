using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SailwindCoop.Runtime
{
    internal enum JoinTransport { Lan, Steam }

    internal sealed class JoinTarget
    {
        public readonly JoinTransport Transport;
        /// <summary>LAN: an IP address or a host name. Steam: the host's SteamID64.</summary>
        public readonly string Address;
        public readonly int Port;
        public readonly string PlayerName;
        /// <summary>Steam: the host's display name when it is known; empty otherwise.</summary>
        public readonly string HostName;

        public JoinTarget(JoinTransport transport, string address, int port, string playerName, string hostName = "")
        {
            Transport = transport;
            Address = address ?? "";
            Port = port;
            PlayerName = playerName;
            HostName = hostName ?? "";
        }

        /// <summary>What the player is shown: where this join goes.</summary>
        public string Label => Transport == JoinTransport.Steam
            ? (HostName.Length > 0 ? HostName : "Steam " + Address)
            : JoinPreferences.FormatEndpoint(Address, Port);

        public bool SameHost(JoinTarget other)
        {
            return other != null && Transport == other.Transport && Port == other.Port &&
                   string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class JoinPreferences
    {
        public static bool TryParseLan(string addressText, string portText, string playerName, out JoinTarget target)
        {
            target = null;
            string address = string.IsNullOrWhiteSpace(addressText) ? "127.0.0.1" : addressText.Trim();
            if (address.StartsWith("[", StringComparison.Ordinal))
            {
                int end = address.IndexOf(']');
                if (end < 0) return false;
                if (end + 1 < address.Length)
                {
                    if (address[end + 1] != ':') return false;
                    portText = address.Substring(end + 2);
                }
                address = address.Substring(1, end - 1);
            }
            else if (address.IndexOf(':') >= 0 && address.IndexOf(':') == address.LastIndexOf(':'))
            {
                int separator = address.IndexOf(':');
                portText = address.Substring(separator + 1);
                address = address.Substring(0, separator);
            }
            if (!TryNormalizeHost(address, out address)) return false;
            int port;
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                return false;

            target = new JoinTarget(JoinTransport.Lan, address, port,
                string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim());
            return true;
        }

        // An IP address or a DNS name: the transport resolves names, so "localhost" and a dynamic
        // DNS name are as good as digits. IPAddress.TryParse alone also takes "1" and "192.168.1"
        // (shorthand for 0.0.0.1 and 192.168.0.1), which is a typo, not an address.
        private static bool TryNormalizeHost(string text, out string host)
        {
            host = text;
            if (string.IsNullOrEmpty(text)) return false;
            bool digitsAndDots = true;
            foreach (char c in text)
                if (c != '.' && (c < '0' || c > '9')) { digitsAndDots = false; break; }
            IPAddress parsed;
            if (digitsAndDots)
            {
                if (text.Split('.').Length != 4 || !IPAddress.TryParse(text, out parsed)) return false;
                host = parsed.ToString();
                return true;
            }
            if (text.IndexOf(':') >= 0)
            {
                if (!IPAddress.TryParse(text, out parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6) return false;
                host = parsed.ToString();
                return true;
            }
            return Uri.CheckHostName(text) == UriHostNameType.Dns;
        }

        public static string FormatEndpoint(string address, int port)
        {
            address = address ?? "";
            return (address.IndexOf(':') >= 0 ? "[" + address + "]" : address) + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParseSteam(string idText, string playerName, out JoinTarget target)
        {
            target = null;
            string text = (idText ?? "").Trim();
            ulong id;
            if (text.Length != 17 || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0UL)
                return false;
            target = new JoinTarget(JoinTransport.Steam, id.ToString(CultureInfo.InvariantCulture), 0,
                string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim());
            return true;
        }

        /// <summary>The one address a LAN guest is told to type. A PC with a VPN and virtual switches
        /// has four or five, and a list of them is a riddle, not an address. In order: the interface
        /// the host is bound to (a loopback bind is reachable from this PC alone); a cable or Wi-Fi
        /// adapter that has a gateway (<paramref name="networkAddresses"/>), which is the home or
        /// office network; the address the system sends from (<paramref name="routedAddress"/>);
        /// any other adapter. The routed address is not first on purpose: with a VPN that takes all
        /// traffic it is the tunnel's own end (measured: 172.18.0.1 of a sing-box tunnel beside
        /// 192.168.0.40 on Wi-Fi), where nobody can connect.</summary>
        public static string HostAddress(string listenIp, IEnumerable<string> networkAddresses, string routedAddress,
                                         IEnumerable<string> otherAddresses, int port)
        {
            IPAddress bound;
            if (IPAddress.TryParse((listenIp ?? "").Trim(), out bound) &&
                !bound.Equals(IPAddress.Any) && !bound.Equals(IPAddress.IPv6Any))
            {
                return IPAddress.IsLoopback(bound) ? FormatEndpoint(bound.ToString(), port) + " (this PC only)"
                                                   : FormatEndpoint(bound.ToString(), port);
            }
            string best = Likeliest(networkAddresses);
            if (best == null && !string.IsNullOrWhiteSpace(routedAddress)) best = routedAddress.Trim();
            if (best == null) best = Likeliest(otherAddresses);
            return best != null ? FormatEndpoint(best, port) : FormatEndpoint("127.0.0.1", port) + " (this PC only)";
        }

        private static string Likeliest(IEnumerable<string> addresses)
        {
            string best = null;
            int bestRank = int.MaxValue;
            if (addresses != null)
                foreach (string address in addresses)
                {
                    if (string.IsNullOrEmpty(address)) continue;
                    int rank = AddressRank(address);
                    if (rank < bestRank) { best = address; bestRank = rank; }
                }
            return best;
        }

        // Home routers first, then the other private ranges, then anything else; an address the
        // system gave itself for want of a network (169.254) comes last.
        private static int AddressRank(string address)
        {
            if (address.StartsWith("192.168.", StringComparison.Ordinal)) return 0;
            if (address.StartsWith("10.", StringComparison.Ordinal)) return 1;
            string[] parts = address.Split('.');
            int second;
            if (parts.Length == 4 && parts[0] == "172" && int.TryParse(parts[1], out second) && second >= 16 && second <= 31) return 2;
            return address.StartsWith("169.254.", StringComparison.Ordinal) ? 4 : 3;
        }

        /// <summary>The part of a <see cref="HostAddress"/> row that goes to the clipboard.</summary>
        public static string EndpointOf(string row)
        {
            int space = (row ?? "").IndexOf(' ');
            return space < 0 ? row ?? "" : row.Substring(0, space);
        }
    }
}
