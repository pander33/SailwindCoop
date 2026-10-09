using System;
using System.Collections.Generic;

namespace SailwindCoop.Net
{
    /// <summary>
    /// Compares the mod versions of the session's members, so a player with an older build learns
    /// that a newer one exists. Nothing is asked from the internet: the versions come from the
    /// handshake and travel in the roster.
    /// </summary>
    public static class ModVersions
    {
        /// <summary>Longest version text carried in the roster.</summary>
        public const int MaxLength = 32;

        /// <summary>A version as it goes on the wire: trimmed, and empty when it is too long to be one.</summary>
        public static string Clean(string version)
        {
            version = (version ?? "").Trim();
            return version.Length > MaxLength ? "" : version;
        }

        /// <summary>True when <paramref name="theirs"/> is a later version than <paramref name="mine"/>.
        /// A text that is not a plain dotted number is never newer and never older.</summary>
        public static bool IsNewer(string theirs, string mine)
        {
            return TryParse(theirs, out Version a) && TryParse(mine, out Version b) && a > b;
        }

        /// <summary>System.Version orders a missing part below zero: "0.4" would be older than "0.4.0".</summary>
        private static bool TryParse(string text, out Version version)
        {
            if (!Version.TryParse((text ?? "").Trim(), out version)) return false;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            return true;
        }

        /// <summary>Index of the latest version in <paramref name="others"/> that is later than
        /// <paramref name="mine"/> (the first one among equals), or -1.</summary>
        public static int NewestAbove(string mine, IList<string> others)
        {
            int best = -1;
            if (others == null) return best;
            for (int i = 0; i < others.Count; i++)
                if (IsNewer(others[i], best < 0 ? mine : others[best])) best = i;
            return best;
        }

        /// <summary>Why builds with different protocol numbers cannot play together, addressed to the
        /// joining player: which side has to update. Sent by the host as the reject detail.</summary>
        public static string ProtocolMismatch(int hostProtocol, int clientProtocol, string hostModVersion)
        {
            string host = string.IsNullOrEmpty(hostModVersion) ? "" : " " + hostModVersion;
            string numbers = " (protocol " + clientProtocol + ", host " + hostProtocol + ")";
            return clientProtocol < hostProtocol
                ? "your mod is older than the host's" + host + numbers + ". Update the mod to join"
                : "the host's mod" + host + " is older than yours" + numbers + ". The host has to update the mod";
        }

        public static string Notice(string holder, string theirs, string mine)
        {
            return (string.IsNullOrEmpty(holder) ? "A crewmate" : holder) + " plays with a later mod version, " +
                   theirs + " (you have " + mine + "). You can still play together.";
        }
    }
}
