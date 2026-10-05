using System;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    internal static class ItemSemanticState
    {
        internal static bool Equal(ItemStateMsg a, ItemStateMsg b)
        {
            if (a == null || b == null) return false;
            if (a.HolderNetId != b.HolderNetId || a.InventorySlot != b.InventorySlot ||
                !a.Amount.Equals(b.Amount) || !a.Health.Equals(b.Health) || a.Sold != b.Sold ||
                a.Nailed != b.Nailed || a.Attached != b.Attached || a.CrateId != b.CrateId ||
                !a.Details.Equals(b.Details) || a.CargoPort != b.CargoPort || a.LightOn != b.LightOn || a.Extras.Length != b.Extras.Length) return false;
            for (int i = 0; i < a.Extras.Length; i++) if (!a.Extras[i].Equals(b.Extras[i])) return false;
            return true;
        }
    }
}
