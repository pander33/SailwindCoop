using System;
using System.Collections.Generic;
using System.Globalization;

namespace SailwindCoop.Sync
{
    internal sealed class TradeSaleJournal
    {
        private const string Prefix = "SailwindCoop.TradeSale.";
        internal readonly Dictionary<Guid, Sale> Pending = new Dictionary<Guid, Sale>();

        internal sealed class Sale
        {
            internal Guid World, Author;
            internal int Instance, Prefab;
        }

        internal void LoadProfile(Dictionary<string, string> data)
        {
            Pending.Clear();
            if (data == null) return;
            foreach (var entry in data)
            {
                if (!entry.Key.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(entry.Key.Substring(Prefix.Length), "N", out var id)) continue;
                var fields = entry.Value.Split(',');
                if (fields.Length != 4) throw new FormatException("Invalid pending trade sale");
                Pending.Add(id, new Sale
                {
                    World = Guid.ParseExact(fields[0], "N"), Author = Guid.ParseExact(fields[1], "N"),
                    Instance = int.Parse(fields[2], CultureInfo.InvariantCulture),
                    Prefab = int.Parse(fields[3], CultureInfo.InvariantCulture),
                });
            }
        }

        internal void SaveProfile(Dictionary<string, string> data)
        {
            foreach (var entry in Pending)
            {
                var sale = entry.Value;
                data[Prefix + entry.Key.ToString("N")] = sale.World.ToString("N") + "," + sale.Author.ToString("N") +
                    "," + sale.Instance.ToString(CultureInfo.InvariantCulture) + "," + sale.Prefab.ToString(CultureInfo.InvariantCulture);
            }
        }

        internal const int MaxAuthorsPerWorld = 512;
        internal const int MaxReceiptsPerWorld = 512;

        private static string WorldPrefix(Guid world, string kind)
            => Prefix + world.ToString("N") + "." + kind + ".";

        private static string Key(Guid world, Guid id, string kind)
            => WorldPrefix(world, kind) + id.ToString("N");

        private static string SeqKey(Guid world)
            => Prefix + world.ToString("N") + ".seq";

        private static long NextSeq(Dictionary<string, string> data, Guid world)
        {
            string seqKey = SeqKey(world);
            long seq = data.TryGetValue(seqKey, out var raw) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
                ? parsed + 1 : 1;
            data[seqKey] = seq.ToString(CultureInfo.InvariantCulture);
            return seq;
        }

        private static long ParseSeq(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            int comma = value.IndexOf(',');
            if (comma < 0 || comma + 1 >= value.Length) return 0;
            return long.TryParse(value.Substring(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq) ? seq : 0;
        }

        private static void PruneKind(Dictionary<string, string> data, Guid world, string kind, int limit)
        {
            if (data == null || data.Count <= limit) return;
            string prefix = WorldPrefix(world, kind);
            List<KeyValuePair<string, long>> entries = null;
            foreach (var pair in data)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                (entries ?? (entries = new List<KeyValuePair<string, long>>())).Add(
                    new KeyValuePair<string, long>(pair.Key, ParseSeq(pair.Value)));
            }
            if (entries == null || entries.Count <= limit) return;
            entries.Sort((a, b) => a.Value.CompareTo(b.Value));
            int remove = entries.Count - limit;
            for (int i = 0; i < remove; i++) data.Remove(entries[i].Key);
        }

        internal static int FindAuthor(Dictionary<string, string> data, Guid world, Guid author)
        {
            if (data == null || !data.TryGetValue(Key(world, author, "author"), out var value) || string.IsNullOrEmpty(value))
                return 0;
            int comma = value.IndexOf(',');
            string rawId = comma >= 0 ? value.Substring(0, comma) : value;
            return int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0;
        }

        internal static void RecordAuthor(Dictionary<string, string> data, Guid world, Guid author, int id)
        {
            if (data == null) return;
            long seq = NextSeq(data, world);
            data[Key(world, author, "author")] = id.ToString(CultureInfo.InvariantCulture) + "," + seq.ToString(CultureInfo.InvariantCulture);
            PruneKind(data, world, "author", MaxAuthorsPerWorld);
        }

        internal static bool IsComplete(Dictionary<string, string> data, Guid world, Guid sale)
            => data != null && data.ContainsKey(Key(world, sale, "receipt"));

        internal static void Complete(Dictionary<string, string> data, Guid world, Guid sale)
        {
            if (data == null) return;
            long seq = NextSeq(data, world);
            data[Key(world, sale, "receipt")] = "1," + seq.ToString(CultureInfo.InvariantCulture);
            PruneKind(data, world, "receipt", MaxReceiptsPerWorld);
        }
    }
}
