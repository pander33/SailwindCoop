using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SailwindCoop.Sync
{
    internal sealed class DogPurchaseJournal
    {
        private const string Prefix = "SailwindCoop.DogPurchase.";
        internal readonly Dictionary<Guid, Charge> Pending = new Dictionary<Guid, Charge>();

        internal sealed class Charge
        {
            internal Guid Purchase;
            internal int[] Currency;
        }

        internal void LoadProfile(Dictionary<string, string> data)
        {
            Pending.Clear();
            if (data == null) return;
            foreach (var entry in data)
            {
                if (!entry.Key.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(entry.Key.Substring(Prefix.Length), "N", out var world)) continue;
                var fields = entry.Value.Split(',');
                var currency = new int[fields.Length - 1];
                for (int i = 0; i < currency.Length; i++)
                    currency[i] = int.Parse(fields[i + 1], CultureInfo.InvariantCulture);
                Pending.Add(world, new Charge { Purchase = Guid.ParseExact(fields[0], "N"), Currency = currency });
            }
        }

        internal void SaveProfile(Dictionary<string, string> data)
        {
            foreach (var entry in Pending)
                data[Prefix + entry.Key.ToString("N")] = string.Join(",", new[] { entry.Value.Purchase.ToString("N") }
                    .Concat(entry.Value.Currency.Select(value => value.ToString(CultureInfo.InvariantCulture))));
        }

        internal static string GetResult(Dictionary<string, string> worldData, Guid world, Guid purchase)
            => worldData.TryGetValue(ResultKey(world, purchase), out var result) ? result : null;

        internal static string RecordResult(Dictionary<string, string> worldData, Guid world, Guid purchase,
            bool success, string message)
            => worldData[ResultKey(world, purchase)] = (success ? "1" : "0") + message;

        private static string ResultKey(Guid world, Guid purchase)
            => Prefix + world.ToString("N") + "." + purchase.ToString("N");
    }
}
