using System;
using System.Collections.Generic;
using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static partial class ItemComponents
    {
        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> Fields = new Dictionary<Type, Dictionary<string, FieldInfo>>();
        private static readonly Dictionary<ShipItem, ItemDetails> Pending = new Dictionary<ShipItem, ItemDetails>();
        private static readonly MethodInfo FoodLookText = typeof(FoodState).GetMethod("UpdateLookText", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static void Clear() => Pending.Clear();
        internal static void RemoveBindings(ShipItem item)
        {
            if (item == null) return;
            Pending.Remove(item);
            ClearCookBinding(item);
            var fuel = item.GetComponent<StoveFuel>();
            if (fuel == null) return;
            var trigger = Read<StoveFuelTrigger>(fuel, "fuelTrigger");
            fuel.inserted = false;
            Set(fuel, "fuelTrigger", null);
            Set(fuel, "cookTrigger", null);
            RebuildFuelCount(trigger);
        }
        internal static void RetryBindings()
        {
            if (Pending.Count == 0) return;
            var waiting = new List<KeyValuePair<ShipItem, ItemDetails>>(Pending);
            foreach (var pair in waiting)
            {
                if (pair.Key == null) { Pending.Remove(pair.Key); continue; }
                var cook = pair.Key.GetComponent<CookableFood>();
                bool ready = !pair.Value.HasCookable || cook == null || ApplyCookBinding(pair.Key, cook, pair.Value.CookStoveId, pair.Value.CookSlot);
                ready &= ApplyFuelBinding(pair.Key, pair.Value);
                if (ready) Pending.Remove(pair.Key);
            }
        }
        internal static FieldInfo Field(Type type, string name)
        {
            // Read/Set are used on per-frame paths; the lookup must not build a key string each time.
            if (!Fields.TryGetValue(type, out var byName)) Fields[type] = byName = new Dictionary<string, FieldInfo>();
            if (byName.TryGetValue(name, out var cached)) return cached;
            FieldInfo field = null;
            for (var t = type; t != null && field == null; t = t.BaseType)
                field = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field == null) throw new MissingFieldException(type.Name, name);
            return byName[name] = field;
        }
        internal static T Read<T>(object value, string name) => (T)Field(value.GetType(), name).GetValue(value);
        internal static void Set(object value, string name, object state) => Field(value.GetType(), name).SetValue(value, state);
        internal static bool BindingsReady(ShipItem item, ItemDetails state)
        {
            if (state == null) return true;
            if (item == null) return false;
            var stove = state.CookStoveId != 0 ? FindStove(state.CookStoveId) : null;
            bool cookReady = ItemTargetReadiness.Binding(state.HasCookable, item.GetComponent<CookableFood>() != null,
                state.CookStoveId != 0, stove != null, stove != null && stove.slots != null && state.CookSlot >= 0 &&
                state.CookSlot < stove.slots.Length && stove.slots[state.CookSlot] != null);
            var fuelStove = state.FuelStoveId != 0 ? FindStove(state.FuelStoveId) : null;
            return cookReady && ItemTargetReadiness.Binding(state.HasFuel, item.GetComponent<StoveFuel>() != null,
                state.FuelStoveId != 0, fuelStove != null,
                fuelStove != null && fuelStove.GetComponentInChildren<StoveFuelTrigger>(true) != null);
        }
        internal static bool HasPendingBinding(ShipItem item) => Pending.ContainsKey(item);
        internal static bool SaveLoaded(ShipItem item)
        {
            try { return Read<bool>(item.GetComponent<SaveablePrefab>(), "loaded"); }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning("[ItemSync] Не удалось проверить загрузку save identity id=" + Id(item) + ": " + error);
                return false;
            }
        }
        private static int Id(ShipItem item) => item != null ? item.GetComponent<SaveablePrefab>()?.instanceId ?? 0 : 0;
        internal static ItemDetails Capture(ShipItem item)
        {
            var state = new ItemDetails();
            var food = item.GetComponent<FoodState>();
            if (food != null)
            {
                state.HasFood = true; state.Dried = food.dried; state.Smoked = food.smoked;
                state.Salted = food.salted; state.Spoiled = food.spoiled; state.InWater = food.inWater;
            }
            var cook = item.GetComponent<CookableFood>();
            if (cook != null)
            {
                state.HasCookable = true; state.CookHeat = cook.GetCurrentHeat();
                var slot = cook.GetCurrentCookTrigger();
                if (slot != null && slot.stove != null)
                { state.CookStoveId = Id(slot.stove); state.CookSlot = Array.IndexOf(slot.stove.slots, slot); }
            }
            if (item is ShipItemSoup soup)
            {
                state.RecipeKind = 1; state.Water = soup.currentWater; state.Energy = soup.currentEnergy;
                state.UncookedEnergy = soup.currentUncookedEnergy; state.Vitamins = soup.currentVitamins;
                state.Protein = soup.currentProtein; state.SoupSpoiled = soup.currentSpoiled; state.SoupSalted = soup.currentSalted;
            }
            if (item is ShipItemKettle kettle)
            {
                state.RecipeKind = 2; state.Water = kettle.currentWater; state.TeaAmount = kettle.currentTeaAmount;
                state.CookedTeaAmount = kettle.currentCookedTeaAmount; state.TeaType = (int)kettle.currentTeaType;
            }
            if (item is ShipItemStove stove) { state.HasStove = true; state.StoveHeat = stove.GetHeat(); }
            var fuel = item.GetComponent<StoveFuel>();
            if (fuel != null)
            {
                state.HasFuel = true; state.FuelLit = Read<bool>(fuel, "lit"); state.FuelInserted = fuel.inserted;
                var trigger = Read<StoveFuelTrigger>(fuel, "fuelTrigger");
                if (trigger != null) state.FuelStoveId = Id(trigger.GetComponentInParent<ShipItemStove>());
            }
            if (item is ShipItemPipe pipe) { state.HasPipe = true; state.PipeHeat = Read<float>(pipe, "currentHeat"); }
            CaptureInstruments(item, state);
            return state;
        }
        internal static void Apply(ShipItem item, ItemDetails state)
        {
            if (state == null || item == null) return;
            Pending.Remove(item);
            var food = item.GetComponent<FoodState>();
            if (state.HasFood && food != null)
            {
                food.dried = state.Dried; food.smoked = state.Smoked; food.salted = state.Salted;
                food.spoiled = state.Spoiled; food.inWater = state.InWater;
            }
            var cook = item.GetComponent<CookableFood>();
            if (state.HasCookable && cook != null)
            {
                Set(cook, "currentHeat", state.CookHeat);
                if (!ApplyCookBinding(item, cook, state.CookStoveId, state.CookSlot)) Pending[item] = state;
                // The base material updater assumes food-specific renderer fields exist.
                if (cook.GetType() == typeof(CookableFood)) cook.UpdateMaterial();
            }
            if (item is ShipItemSoup soup && state.RecipeKind == 1)
            {
                soup.currentWater = state.Water; soup.currentEnergy = state.Energy; soup.currentUncookedEnergy = state.UncookedEnergy;
                soup.currentVitamins = state.Vitamins; soup.currentProtein = state.Protein;
                soup.currentSpoiled = state.SoupSpoiled; soup.currentSalted = state.SoupSalted;
            }
            if (item is ShipItemKettle kettle && state.RecipeKind == 2)
            {
                kettle.currentWater = state.Water; kettle.currentTeaAmount = state.TeaAmount;
                kettle.currentCookedTeaAmount = state.CookedTeaAmount; kettle.currentTeaType = (LiquidType)state.TeaType;
            }
            if (state.HasStove && item is ShipItemStove stove) Set(stove, "currentHeat", state.StoveHeat);
            if (!ApplyFuelBinding(item, state)) Pending[item] = state;
            if (state.HasPipe && item is ShipItemPipe pipe)
            {
                Set(pipe, "currentHeat", state.PipeHeat);
                var renderer = Read<Renderer>(pipe, "tobaccoGraphics");
                if (renderer != null) renderer.gameObject.SetActive(pipe.health > 0f);
            }
            ApplyInstruments(item, state);
            item.UpdateLookText();
            if (food != null) FoodLookText?.Invoke(food, null);
            // An item created in this frame (SpawnObject) has an ItemRigidbody whose Start has not run:
            // UpdateMass throws there, which used to abort the item's first state — it stayed at its
            // spawn pose under local physics. Its own Start sets the mass a frame later.
            if (item.itemRigidbodyC != null)
            {
                try { item.itemRigidbodyC.UpdateMass(); }
                catch (NullReferenceException) { }
            }
        }
        internal static void ClearCookBinding(ShipItem item)
        {
            var cook = item.GetComponent<CookableFood>();
            if (cook != null) ApplyCookBinding(item, cook, 0, -1);
        }
        private static bool ApplyCookBinding(ShipItem item, CookableFood cook, int stoveId, int slotIndex)
        {
            var previous = cook.GetCurrentCookTrigger();
            StoveCookTrigger target = null;
            if (stoveId != 0)
            {
                var stove = FindStove(stoveId);
                // Missing target is loading, not evidence that the cookable was removed.
                if (stove == null || stove.slots == null || slotIndex < 0 || slotIndex >= stove.slots.Length || stove.slots[slotIndex] == null) return false;
                target = stove.slots[slotIndex];
            }
            if (previous != null && previous.currentFood == cook) previous.currentFood = null;
            Set(cook, "currentTrigger", target);
            if (target != null) target.currentFood = cook;
            var body = item.GetItemRigidbody();
            if (body != null) { body.inStove = target != null; body.disableCol = target != null; if (target != null) body.attached = true; }
            return true;
        }
        private static bool ApplyFuelBinding(ShipItem item, ItemDetails state)
        {
            var fuel = item.GetComponent<StoveFuel>();
            if (!state.HasFuel || fuel == null) return true;
            var old = Read<StoveFuelTrigger>(fuel, "fuelTrigger");
            StoveFuelTrigger target = null;
            if (state.FuelStoveId != 0)
            {
                var stove = FindStove(state.FuelStoveId);
                if (stove == null || (target = stove.GetComponentInChildren<StoveFuelTrigger>(true)) == null) return false;
            }
            fuel.inserted = state.FuelInserted; Set(fuel, "lit", state.FuelLit);
            Set(fuel, "fuelTrigger", target);
            Set(fuel, "cookTrigger", target != null ? Read<StoveCookTrigger>(target, "cookTrigger") : null);
            RebuildFuelCount(old); if (target != old) RebuildFuelCount(target);
            return true;
        }
        private static void RebuildFuelCount(StoveFuelTrigger trigger)
        {
            if (trigger == null) return;
            int count = 0;
            foreach (var fuel in UnityEngine.Object.FindObjectsOfType<StoveFuel>())
                if (fuel != null && fuel.inserted && Read<StoveFuelTrigger>(fuel, "fuelTrigger") == trigger) count++;
            Set(trigger, "currentFuel", count);
        }
        private static ShipItemStove FindStove(int id)
        {
            if (id == 0) return null;
            foreach (var stove in UnityEngine.Object.FindObjectsOfType<ShipItemStove>()) if (Id(stove) == id) return stove;
            return null;
        }
    }
}
