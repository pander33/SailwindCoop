using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stable-enough boat enumeration for save-identical peers. A Sailwind embark collider sits
    /// under the visible "world boat" transform used by <c>PlayerEmbarkerNew</c>; sorting by the
    /// transform hierarchy path gives both machines the same index for main boat / dinghies.
    /// </summary>
    public static class BoatLocator
    {
        public const ushort NoBoat = ushort.MaxValue;

        public static List<Transform> FindBoats()
        {
            var set = new HashSet<Transform>();
            foreach (var col in Object.FindObjectsOfType<BoatEmbarkCollider>())
            {
                if (col == null || col.transform == null || col.transform.parent == null) continue;
                Transform boat = col.transform.parent;
                if (!IsNetworkBoat(boat)) continue;
                set.Add(boat);
            }

            var boats = new List<Transform>(set);
            // Original boats first (stable order), purchasable boats last. A purchasable boat only joins
            // the network set once bought (extraSetting); sorting it after the originals means buying one
            // APPENDS its index instead of shifting the main ship's index (which items/players address).
            boats.Sort((a, b) =>
            {
                int pa = IsPurchasable(a) ? 1 : 0;
                int pb = IsPurchasable(b) ? 1 : 0;
                if (pa != pb) return pa - pb;
                return string.CompareOrdinal(PathOf(a), PathOf(b));
            });
            return boats;
        }

        private static bool IsPurchasable(Transform worldBoat)
        {
            if (worldBoat == null) return false;
            Transform root = worldBoat.parent != null ? worldBoat.parent : worldBoat;
            return root.GetComponent("PurchasableBoat") != null;
        }

        private static bool IsNetworkBoat(Transform worldBoat)
        {
            if (worldBoat == null) return false;
            Transform root = worldBoat.parent != null ? worldBoat.parent : worldBoat;
            var saveable = root.GetComponent<SaveableObject>();
            var probes = root.GetComponent("BoatProbes");
            if (saveable != null && probes != null && !saveable.extraSetting)
                return false;
            return true;
        }

        public static Transform FindByIndex(ushort index)
        {
            if (index == NoBoat) return null;
            var boats = FindBoats();
            return index < boats.Count ? boats[index] : null;
        }

        public static ushort IndexOf(Transform boat)
        {
            if (boat == null) return NoBoat;
            var boats = FindBoats();
            for (int i = 0; i < boats.Count; i++)
                if (boats[i] == boat) return (ushort)i;
            return NoBoat;
        }

        public static string PathOf(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            Transform p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}
