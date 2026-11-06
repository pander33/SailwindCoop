using System;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    internal static class ItemSemanticState
    {
        internal static bool Equal(ItemStateMsg a, ItemStateMsg b) => Equal(a, b, true);

        /// <summary>Host stream: is <paramref name="now"/> still the state last given a revision?
        /// Everything a player or an operation changes is compared exactly; what the world moves by
        /// itself every frame (<see cref="ItemAgeing"/>) only after a visible step.
        /// <paramref name="lampFull"/> is the full fuel of a lamp, 0 for any other item.</summary>
        internal static bool Settled(ItemStateMsg sent, ItemStateMsg now, float lampFull = 0f)
        {
            if (!Equal(sent, now, false)) return false;
            var a = sent.Details; var b = now.Details;
            if (a == null || b == null) return sent.Health.Equals(now.Health);
            bool lamp = sent.LightOn && now.LightOn;
            if (ItemAgeing.Health(sent.Health, now.Health, BurningFuel(sent, now), lamp ? lampFull : 0f)) return false;
            if (a.HasCookable && ItemAgeing.Heat(a.CookHeat, b.CookHeat)) return false;
            if (!a.HasFood && a.RecipeKind != 1) return true;
            return !ItemAgeing.Dried(a.Dried, b.Dried) && !ItemAgeing.Spoiled(a.Spoiled, b.Spoiled) &&
                !ItemAgeing.SoupSpoiled(a.SoupSpoiled, b.SoupSpoiled, b.Energy + b.UncookedEnergy);
        }

        private static bool BurningFuel(ItemStateMsg a, ItemStateMsg b)
            => a.Details.HasFuel && a.Details.FuelLit && b.Details.HasFuel && b.Details.FuelLit;

        private static bool Equal(ItemStateMsg a, ItemStateMsg b, bool ageing)
        {
            if (a == null || b == null) return false;
            if (a.HolderNetId != b.HolderNetId || a.InventorySlot != b.InventorySlot ||
                !a.Amount.Equals(b.Amount) || a.Sold != b.Sold ||
                a.Nailed != b.Nailed || a.Attached != b.Attached || a.CrateId != b.CrateId ||
                !(a.Details == null ? b.Details == null : a.Details.Equals(b.Details, ageing)) || a.CargoPort != b.CargoPort || a.LightOn != b.LightOn || a.Extras.Length != b.Extras.Length) return false;
            if (ageing && !a.Health.Equals(b.Health)) return false;
            for (int i = 0; i < a.Extras.Length; i++) if (!a.Extras[i].Equals(b.Extras[i])) return false;
            return true;
        }
    }
}
