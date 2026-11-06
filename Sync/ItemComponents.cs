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
        private static readonly List<ShipItem> StoveLookupItems = new List<ShipItem>();
        private static readonly Dictionary<int, ShipItemStove> SceneStoves = new Dictionary<int, ShipItemStove>();
        private static readonly List<KeyValuePair<ShipItem, ItemDetails>> WaitingBindings = new List<KeyValuePair<ShipItem, ItemDetails>>();
        private static int _stoveLookupFrame = -1;
        private static float _nextBindingRetry;

        internal static void Clear()
        {
            Pending.Clear();
            SceneStoves.Clear();
            _stoveLookupFrame = -1;
            _nextBindingRetry = 0f;
        }
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
            if (Pending.Count == 0 || Time.unscaledTime < _nextBindingRetry) return;
            _nextBindingRetry = Time.unscaledTime + 0.1f;
            WaitingBindings.AddRange(Pending);
            try
            {
                foreach (var pair in WaitingBindings)
                {
                    if (pair.Key == null) { Pending.Remove(pair.Key); continue; }
                    var cook = pair.Key.GetComponent<CookableFood>();
                    bool ready = !pair.Value.HasCookable || cook == null || ApplyCookBinding(pair.Key, cook, pair.Value.CookStoveId, pair.Value.CookSlot);
                    ready &= ApplyFuelBinding(pair.Key, pair.Value);
                    if (ready) Pending.Remove(pair.Key);
                }
            }
            finally { WaitingBindings.Clear(); }
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
        internal struct StoveBindings
        {
            internal StoveCookTrigger Cook;
            internal StoveFuelTrigger Fuel;
        }

        internal static bool BindingsReady(ShipItem item, ItemDetails state) => BindingsReady(item, state, out _);

        internal static bool BindingsReady(ShipItem item, ItemDetails state, out StoveBindings targets)
        {
            targets = default;
            if (state == null) return true;
            if (item == null) return false;
            if (state.HasCookable)
            {
                targets.Cook = FindCookSlot(state.CookStoveId, state.CookSlot);
                if (item.GetComponent<CookableFood>() == null || (state.CookStoveId != 0 && targets.Cook == null))
                    return false;
            }
            if (state.HasFuel)
            {
                targets.Fuel = FindFuelTrigger(state.FuelStoveId);
                if (item.GetComponent<StoveFuel>() == null || (state.FuelStoveId != 0 && targets.Fuel == null))
                    return false;
            }
            return true;
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
        internal static void Apply(ShipItem item, ItemDetails state, StoveBindings? targets = null)
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
                if (!ApplyCookBinding(item, cook, state.CookStoveId, state.CookSlot, targets)) Pending[item] = state;
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
            if (!ApplyFuelBinding(item, state, targets)) Pending[item] = state;
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
        private static StoveCookTrigger FindCookSlot(int id, int slot)
        {
            var stove = FindStove(id);
            return stove != null && stove.slots != null && slot >= 0 && slot < stove.slots.Length
                ? stove.slots[slot] : null;
        }

        private static StoveFuelTrigger FindFuelTrigger(int id)
        {
            var stove = FindStove(id);
            return stove != null ? stove.GetComponentInChildren<StoveFuelTrigger>(true) : null;
        }

        private static bool ApplyCookBinding(ShipItem item, CookableFood cook, int stoveId, int slotIndex, StoveBindings? targets = null)
        {
            var previous = cook.GetCurrentCookTrigger();
            var target = targets.HasValue ? targets.Value.Cook : FindCookSlot(stoveId, slotIndex);
            // Missing target is loading, not evidence that the cookable was removed.
            if (stoveId != 0 && target == null) return false;
            if (previous != null && previous.currentFood == cook) previous.currentFood = null;
            Set(cook, "currentTrigger", target);
            if (target != null) target.currentFood = cook;
            var body = item.GetItemRigidbody();
            if (body != null) { body.inStove = target != null; body.disableCol = target != null; if (target != null) body.attached = true; }
            return true;
        }
        private static bool ApplyFuelBinding(ShipItem item, ItemDetails state, StoveBindings? targets = null)
        {
            var fuel = item.GetComponent<StoveFuel>();
            if (!state.HasFuel || fuel == null) return true;
            var old = Read<StoveFuelTrigger>(fuel, "fuelTrigger");
            var target = targets.HasValue ? targets.Value.Fuel : FindFuelTrigger(state.FuelStoveId);
            if (state.FuelStoveId != 0 && target == null) return false;
            bool membershipChanged = fuel.inserted != state.FuelInserted || old != target;
            fuel.inserted = state.FuelInserted; Set(fuel, "lit", state.FuelLit);
            Set(fuel, "fuelTrigger", target);
            Set(fuel, "cookTrigger", target != null ? Read<StoveCookTrigger>(target, "cookTrigger") : null);
            PlaceFuel(item, state.FuelInserted ? target : null);
            if (membershipChanged)
            {
                RebuildFuelCount(old);
                if (target != old) RebuildFuelCount(target);
            }
            return true;
        }
        internal static bool InStove(ShipItem item)
        {
            var parent = item != null ? item.transform.parent : null;
            return parent != null && parent.GetComponent<ShipItemStove>() != null;
        }
        // The rest of vanilla StoveFuelTrigger.InsertFuel: the log leaves physics and sits inside the
        // stove as its child. Without it the burning log stays a loose item that can be picked up, on
        // every machine except the one where it was inserted.
        private static void PlaceFuel(ShipItem item, StoveFuelTrigger target)
        {
            var proxy = item.GetItemRigidbody();
            var collider = item.GetComponent<Collider>();
            if (target != null)
            {
                Transform stove = target.transform.parent;
                if (proxy != null && proxy.gameObject.activeSelf) proxy.gameObject.SetActive(false);
                if (collider != null && collider.enabled) collider.enabled = false;
                item.forceDisableRedOutline = true;
                if (item.transform.parent == stove) return;
                item.transform.parent = stove;
                item.transform.localPosition = target.transform.localPosition;
                return;
            }
            if (!InStove(item)) return;
            if (proxy != null) proxy.gameObject.SetActive(true);
            if (collider != null) collider.enabled = true;
            item.forceDisableRedOutline = false;
            Transform home = item.currentActualBoat != null ? item.currentActualBoat
                : FloatingOriginManager.instance != null ? FloatingOriginManager.instance.transform : null;
            item.transform.parent = home;
        }
        // The game raises a kettle's boil sound inside Cook, which a client does not run; the
        // kettle's own Update only fades it. Same condition and rate as CookableFoodKettle.Cook
        // for one burning log.
        internal static void BoilSound(CookableFoodKettle cook)
        {
            var slot = cook.GetCurrentCookTrigger();
            if (slot == null || slot.stove == null || cook.GetCurrentHeat() < 1f) return;
            var kettle = Read<ShipItemKettle>(cook, "kettle");
            if (kettle == null || kettle.currentWater <= 0f) return;
            var fire = Read<StoveFuelTrigger>(slot.stove, "fuelTrigger");
            if (fire == null || Read<int>(fire, "currentFuel") <= 0) return;
            var audio = Read<AudioSource>(cook, "boilAudio");
            if (audio == null) return;
            if (!audio.enabled) { audio.enabled = true; audio.Play(); }
            audio.volume += Time.deltaTime * 0.22f;
        }
        private static void RebuildFuelCount(StoveFuelTrigger trigger)
        {
            if (trigger == null) return;
            int count = 0;
            foreach (var fuel in UnityEngine.Object.FindObjectsOfType<StoveFuel>())
                if (fuel != null && fuel.inserted && Read<StoveFuelTrigger>(fuel, "fuelTrigger") == trigger) count++;
            if (Read<int>(trigger, "currentFuel") == count) return;
            Set(trigger, "currentFuel", count);
            // The game writes the stove's look text only from its own insertion and burn-out.
            var stove = trigger.transform.parent != null ? trigger.transform.parent.GetComponent<ShipItemStove>() : null;
            if (stove != null) stove.description = count == 0 ? "" : count + " / " + Read<int>(trigger, "maxFuel");
        }
        private static ShipItemStove FindStove(int id)
        {
            if (id == 0) return null;
            var tracked = ItemSync.Instance?.FindTrackedStove(id);
            if (tracked != null) return tracked;

            // A stove can appear before ItemSync rebuilds its index, or be absent from the shared set.
            if (_stoveLookupFrame != Time.frameCount)
            {
                _stoveLookupFrame = Time.frameCount;
                SceneStoves.Clear();
                ShipItemRegistry.Snapshot(StoveLookupItems);
                foreach (var item in StoveLookupItems)
                    if (item is ShipItemStove stove && stove != null) SceneStoves[Id(stove)] = stove;
                StoveLookupItems.Clear();
            }
            return SceneStoves.TryGetValue(id, out var found) && found != null && found.gameObject.activeInHierarchy &&
                Id(found) == id ? found : null;
        }
    }
}
