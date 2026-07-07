using System;
using System.Collections.Generic;
using System.Text;

namespace SailwindCoop.Runtime
{
    public enum PatchHealthState
    {
        Unknown,
        Ok,
        Partial,
        Failed
    }

    public static class PatchHealth
    {
        private sealed class Entry
        {
            public PatchHealthState State;
            public string Detail;
        }

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        public static void Report(string domain, int ok, int total, string detail = null)
        {
            PatchHealthState state;
            if (total <= 0)
                state = ok > 0 ? PatchHealthState.Ok : PatchHealthState.Unknown;
            else if (ok >= total)
                state = PatchHealthState.Ok;
            else if (ok > 0)
                state = PatchHealthState.Partial;
            else
                state = PatchHealthState.Failed;

            Set(domain, state, string.IsNullOrEmpty(detail) ? ok + "/" + total : detail);
        }

        public static void Set(string domain, PatchHealthState state, string detail = null)
        {
            if (string.IsNullOrEmpty(domain)) return;
            Entries[domain] = new Entry { State = state, Detail = detail ?? "" };
        }

        public static string Summary
        {
            get
            {
                if (Entries.Count == 0) return "not initialized";

                int ok = 0, partial = 0, failed = 0, unknown = 0;
                foreach (var e in Entries.Values)
                {
                    switch (e.State)
                    {
                        case PatchHealthState.Ok: ok++; break;
                        case PatchHealthState.Partial: partial++; break;
                        case PatchHealthState.Failed: failed++; break;
                        default: unknown++; break;
                    }
                }

                if (failed == 0 && partial == 0 && unknown == 0)
                    return "ok (" + ok + ")";
                return "ok " + ok + ", partial " + partial + ", failed " + failed + ", unknown " + unknown;
            }
        }

        public static string Details
        {
            get
            {
                if (Entries.Count == 0) return "not initialized";
                var sb = new StringBuilder();
                foreach (var kv in Entries)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(kv.Key).Append("=").Append(kv.Value.State);
                    if (!string.IsNullOrEmpty(kv.Value.Detail))
                        sb.Append("(").Append(kv.Value.Detail).Append(")");
                }
                return sb.ToString();
            }
        }
    }
}
