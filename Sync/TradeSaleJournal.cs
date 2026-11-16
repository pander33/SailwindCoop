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

        private static string Key(Guid world, Guid id, string kind)
            => Prefix + world.ToString("N") + "." + kind + "." + id.ToString("N");

        internal static int FindAuthor(Dictionary<string, string> data, Guid world, Guid author)
            => data.TryGetValue(Key(world, author, "author"), out var value) &&
               int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0;

        internal static void RecordAuthor(Dictionary<string, string> data, Guid world, Guid author, int id)
            => data[Key(world, author, "author")] = id.ToString(CultureInfo.InvariantCulture);

        internal static bool IsComplete(Dictionary<string, string> data, Guid world, Guid sale)
            => data.ContainsKey(Key(world, sale, "receipt"));

        internal static void Complete(Dictionary<string, string> data, Guid world, Guid sale)
            => data[Key(world, sale, "receipt")] = "1";
    }
}
