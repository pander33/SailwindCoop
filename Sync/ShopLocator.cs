using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stable cross-machine index of <c>Shopkeeper</c> instances for save-identical peers.
    /// Shopkeepers are static scene objects; sorting by transform hierarchy path gives both
    /// machines the same index, so a client's buy/sell request can name the keeper the host
    /// must act on (see <c>ShopSync</c>). Mirrors <see cref="BoatLocator"/>.
    /// </summary>
    public static class ShopLocator
    {
        public const ushort NoShop = ushort.MaxValue;

        public static List<Shopkeeper> FindKeepers()
        {
            var list = new List<Shopkeeper>();
            foreach (var k in Object.FindObjectsOfType<Shopkeeper>())
                if (k != null) list.Add(k);
            list.Sort((a, b) => string.CompareOrdinal(BoatLocator.PathOf(a.transform), BoatLocator.PathOf(b.transform)));
            return list;
        }

        public static Shopkeeper FindByIndex(ushort index)
        {
            if (index == NoShop) return null;
            var list = FindKeepers();
            return index < list.Count ? list[index] : null;
        }

        public static ushort IndexOf(Shopkeeper keeper)
        {
            if (keeper == null) return NoShop;
            var list = FindKeepers();
            for (int i = 0; i < list.Count; i++)
                if (list[i] == keeper) return (ushort)i;
            return NoShop;
        }
    }
}
