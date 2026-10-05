using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Each owned hull retains its own buffers and original physics, even off deck.</summary>
    internal sealed class BoatContexts<T> where T : class
    {
        private sealed class Entry { public Transform Boat; public long Stamp; public T Context; }
        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();
        private readonly Func<Transform, ushort, T> _create;
        private readonly Action<T> _clear;
        private readonly Func<Transform, long> _stamp;
        private float _nextScan;

        public BoatContexts(Func<Transform, ushort, T> create, Action<T> clear, Func<Transform, long> stamp)
        { _create = create; _clear = clear; _stamp = stamp; }

        public IEnumerable<T> Values
        {
            get
            {
                if (!GameState.playing || GameState.currentlyLoading) yield break;
                Refresh(); foreach (var e in _entries.Values) yield return e.Context;
            }
        }

        public IEnumerable<T> Existing { get { foreach (var e in _entries.Values) yield return e.Context; } }

        public string Describe(Func<T, string> describe)
        {
            var text = new StringBuilder();
            foreach (var context in Values)
            {
                if (text.Length > 0) text.Append(" | ");
                text.Append(describe(context));
            }
            return text.Length > 0 ? text.ToString() : "no boats";
        }
        private int _missingWarnings;
        public T Get(ushort id)
        {
            Refresh();
            if (_entries.TryGetValue(id, out var e)) return e.Context;
            if (Plugin.Logger != null && Plugin.Logger.ShouldReport(ref _missingWarnings))
                Plugin.Logger.LogWarning("[" + typeof(T).Name + "] Rejected unknown owned boat=" + id);
            return null;
        }

        public void Refresh()
        {
            if (!GameState.playing || GameState.currentlyLoading) return;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + BoatLocator.CacheSeconds;
            var alive = new HashSet<ushort>();
            foreach (Transform boat in BoatLocator.FindBoats())
            {
                ushort id = BoatLocator.IndexOf(boat);
                if (boat == null || id == BoatLocator.NoBoat) continue;
                alive.Add(id);
                long stamp = _stamp(boat);
                if (_entries.TryGetValue(id, out var old))
                {
                    if (old.Boat == boat && old.Stamp == stamp) continue;
                    _clear(old.Context);
                    _entries.Remove(id);
                }
                _entries.Add(id, new Entry { Boat = boat, Stamp = stamp, Context = _create(boat, id) });
            }
            var gone = new List<ushort>();
            foreach (var kv in _entries)
                if (!alive.Contains(kv.Key)) { _clear(kv.Value.Context); gone.Add(kv.Key); }
            foreach (ushort id in gone) _entries.Remove(id);
        }

        public void Clear()
        {
            foreach (var e in _entries.Values) _clear(e.Context);
            _entries.Clear();
            _nextScan = 0f;
        }

        public void Invalidate(ushort id)
        {
            if (_entries.TryGetValue(id, out var entry)) { _clear(entry.Context); _entries.Remove(id); }
            _nextScan = 0f;
        }
    }

    internal static class BoatLayout
    {
        private static int _mismatchWarnings;
        public static bool Matches(ushort boat, uint remote, uint local, string domain)
        {
            if (remote == local) return true;
            if (Plugin.Logger != null && Plugin.Logger.ShouldReport(ref _mismatchWarnings))
                Plugin.Logger.LogWarning("[" + domain + "] Layout differs; using existing boat/object indices boat=" + boat +
                    " remote=" + remote + " local=" + local);
            // Runtime hierarchy names differ between independently loaded copies of the same save.
            // This fingerprint is diagnostic; handlers still check boat, object index and payload.
            return true;
        }
        public static uint Hash(Transform boat, params Component[][] groups)
        {
            uint hash = 2166136261;
            foreach (var group in groups)
            {
                Add(ref hash, "|");
                foreach (var node in group)
                {
                    if (node == null) { Add(ref hash, "null;"); continue; }
                    string path = "";
                    for (Transform t = node.transform; t != null && t != boat; t = t.parent)
                    {
                        // Dynamic cargo/player siblings do not change a static control's identity.
                        int ordinal = 0;
                        if (t.parent != null) for (int i = 0; i < t.GetSiblingIndex(); i++)
                            if (t.parent.GetChild(i).name == t.name) ordinal++;
                        path = t.name + ":" + ordinal + "/" + path;
                    }
                    Add(ref hash, node.GetType().FullName + ":" + path + ";");
                }
            }
            return hash;
        }

        // Detect replacement objects as well as topology changes locally (not a wire identity).
        public static long Stamp(params Component[][] groups)
        {
            long result = 17;
            unchecked { foreach (var group in groups) foreach (var node in group)
                result = result * 31 + (node != null ? node.GetInstanceID() : 0); }
            return result;
        }

        // Mooring ropes reparent to docks; their stable identity is their serialized array slot.
        public static uint MooringHash(PickupableBoatMooringRope[] ropes)
        {
            uint hash = 2166136261;
            if (ropes != null) foreach (var rope in ropes)
                Add(ref hash, rope != null ? rope.GetType().FullName + ":" + rope.name + ";" : "null;");
            return hash;
        }

        private static void Add(ref uint hash, string text)
        { unchecked { foreach (char c in text) { hash ^= c; hash *= 16777619; } } }
    }
}
