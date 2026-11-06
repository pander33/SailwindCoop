using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class ItemOperationCapture
    {
        private static Scope _active;
        // A drop lets the game put the item into a stove by itself, as the player's own action. The
        // mark lasts as long as a thrown item can fly: an item that lies on the deck and is pushed
        // into a stove later is the world's doing, not the player's.
        private const float DropWindow = 5f;
        private static readonly Dictionary<ShipItem, float> _dropped = new Dictionary<ShipItem, float>();
        private static readonly List<ShipItem> _expired = new List<ShipItem>();
        internal static bool Absorb() => _active != null;
        internal static void Clear() { _active = null; _dropped.Clear(); }
        internal static void MarkDrop(ShipItem item)
        {
            if (!InteractionContext.HasInput || InteractionContext.Suppressed || item == null ||
                (item.GetComponent<StoveFuel>() == null && item.GetComponent<CookableFood>() == null)) return;
            float now = Time.unscaledTime;
            foreach (var pair in _dropped) if (pair.Key == null || now - pair.Value > DropWindow) _expired.Add(pair.Key);
            foreach (var old in _expired) _dropped.Remove(old);
            _expired.Clear();
            _dropped[item] = now;
        }
        internal static bool TakeDrop(ShipItem item)
        {
            if (item == null || !_dropped.TryGetValue(item, out float at)) return false;
            _dropped.Remove(item);
            return Time.unscaledTime - at <= DropWindow;
        }
        internal static void Consumed(ShipItem item)
        {
            if (_active == null || item == null) return;
            ItemStateMsg before;
            if (_active.Before.TryGetValue(item, out before))
            { _active.Dead.Add(before.InstanceId); ItemSync.Instance?.MarkOperationDestroy(item); }
        }
        internal static bool Damage(DamageRequestMsg damage)
        { if (_active == null) return false; _active.Hull.Add(damage); return true; }
        internal static void RainbowEffect() { if (_active != null && !InteractionContext.Suppressed) _active.WorldEffects |= 1; }
        internal static void TotemEffect()
        { if (_active != null && !InteractionContext.Suppressed) { _active.WorldEffects |= 2; _active.TotemAttraction = WeatherStorms.totemAttraction; } }
        internal static Scope Begin(string label, bool creates, bool completedInput, ShipItem first, ShipItem second)
        {
            if (_active != null || InteractionContext.Suppressed || (!InteractionContext.HasInput && !completedInput) ||
                ItemSync.Instance == null || !ItemSync.Instance.IsSessionActive) return null;
            var scope = new Scope
            {
                Label = label,
                Before = first != null ? ItemSync.Instance.CaptureOperationParticipants(first, second) :
                    ItemSync.Instance.CaptureOperationItems()
            };
            if (creates)
            {
                scope.Original = new HashSet<int>();
                foreach (var item in ShipItemRegistry.Active()) if (item != null) scope.Original.Add(item.GetInstanceID());
            }
            _active = scope; return scope;
        }
        internal sealed class Scope : IDisposable
        {
            internal string Label;
            internal Dictionary<ShipItem, ItemStateMsg> Before;
            internal HashSet<int> Original;
            internal readonly HashSet<int> Dead = new HashSet<int>();
            internal readonly List<DamageRequestMsg> Hull = new List<DamageRequestMsg>();
            internal byte WorldEffects;
            internal float TotemAttraction;
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_active == this) _active = null;
                ItemSync.Instance?.SendOperation(Before, Dead, Original, Hull, Label, WorldEffects, TotemAttraction);
            }
        }
    }

    internal static class ItemOperationPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            Install(harmony, hooks, typeof(ShipItemKnife), "CutFood", new[] { typeof(FoodState) });
            Install(harmony, hooks, typeof(ShipItemTobaccoPack), "CutPack", Type.EmptyTypes);
            Install(harmony, hooks, typeof(ShipItemElixir), "OnAltActivate", Type.EmptyTypes);
            Install(harmony, hooks, typeof(ShipItemRandomElixir), "OnAltActivate", Type.EmptyTypes);
            Install(harmony, hooks, typeof(ShipItemOakum), "OnAltActivate", Type.EmptyTypes);
            Install(harmony, hooks, typeof(ShipItemTotem), "FinishCast", Type.EmptyTypes);
            Install(harmony, hooks, typeof(HullDamageButton), "OnItemClick", new[] { typeof(PickupableItem) });
            Install(harmony, hooks, typeof(BoatDamageWaterButton), "OnItemClick", new[] { typeof(PickupableItem) });
            Install(harmony, hooks, typeof(ShipItemLight), "LoadFuel", new[] { typeof(ShipItemLanternFuel) });
            Install(harmony, hooks, typeof(ShipItemLight), "OnAltActivate", Type.EmptyTypes);
            foreach (var type in new[] { typeof(ShipItemBottle), typeof(ShipItemSoup), typeof(ShipItemKettle),
                typeof(ShipItemPipe), typeof(ShipItemSalt), typeof(ShipItemStove) })
                Install(harmony, hooks, type, "OnItemClick", new[] { typeof(PickupableItem) });
            Install(harmony, hooks, typeof(ShipItemBottle), "Drink", Type.EmptyTypes);
            Install(harmony, hooks, typeof(ShipItemFood), "EatFood", Type.EmptyTypes);
            hooks.Install(typeof(ShipItemSoup), "DrinkOrSpill", new[] { typeof(bool) }, m => harmony.Patch(m,
                // A finalizer, not a postfix: the game's call ends in ItemRigidbody.UpdateMass,
                // and a throw there must not leave the level that already changed unreported.
                prefix: Callback(nameof(PreDrain), Priority.First), finalizer: Callback(nameof(PostDrain), Priority.Last)));
            Install(harmony, hooks, typeof(CookableFood), "InsertIntoCookTrigger", new[] { typeof(StoveCookTrigger) });
            Install(harmony, hooks, typeof(StoveFuelTrigger), "InsertFuel", new[] { typeof(ShipItem) });
            hooks.Install(typeof(ShipItem), "DestroyItem", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreDestroy), Priority.First)));
            hooks.Install(typeof(Rainbow), "ForceShowRainbow", Type.EmptyTypes, m => harmony.Patch(m, postfix: Callback(nameof(PostRainbow), Priority.Last)));
            PatchHealth.Report("Item results", hooks);
            Plugin.Logger.LogInfo("[ItemOperationPatches] " + hooks.Detail);
        }
        private static void Install(Harmony harmony, PatchHookCatalog hooks, Type type, string name, Type[] args)
            => hooks.Install(type, name, args, m => harmony.Patch(m,
                prefix: Callback(nameof(PreOperation), Priority.First),
                postfix: Callback(nameof(FinishOperation), Priority.Last), finalizer: Callback(nameof(FinishOperation), Priority.Last)));
        private static HarmonyMethod Callback(string name, int priority)
            => new HarmonyMethod(typeof(ItemOperationPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)) { priority = priority };
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[ItemOperationPatches] " + error); } catch { } }
        private static bool PreOperation(object __instance, object[] __args, MethodBase __originalMethod, out ItemOperationCapture.Scope __state)
        {
            ItemOperationCapture.Scope state = null;
            bool run = true;
            PatchGuard.Run(() => {
                string name = __originalMethod.Name;
                // The alternative action on an item that is still for sale is its purchase
                // (ShipItem.OnAltActivate -> Shopkeeper.TryToSellItem), not the item's own operation.
                // A capture opened here would absorb the report of the bought item, and the host
                // would never learn about it.
                if (name == "OnAltActivate" && __instance is ShipItem bought && !bought.sold) return;
                bool completion = name == "Drink" || name == "EatFood" || name == "FinishCast";
                if (name == "InsertFuel" || name == "InsertIntoCookTrigger")
                {
                    var item = __instance is CookableFood cook ? cook.GetComponent<ShipItem>() : __args[0] as ShipItem;
                    completion = ItemOperationCapture.TakeDrop(item);
                    if (ItemSync.Instance != null && ItemSync.Instance.IsClientSession &&
                        !InteractionContext.HasInput && !completion && !InteractionContext.Suppressed) { run = false; return; }
                }
                var type = __originalMethod.DeclaringType;
                ShipItem first = null, second = null;
                // These vanilla methods mutate only the receiver, their explicit item argument,
                // and (for cutting) the newly created items tracked by the scope.
                if (name == "OnItemClick" && (type == typeof(ShipItemBottle) || type == typeof(ShipItemSoup) ||
                    type == typeof(ShipItemKettle) || type == typeof(ShipItemPipe) ||
                    type == typeof(ShipItemSalt) || type == typeof(ShipItemStove)) ||
                    name == "LoadFuel")
                {
                    first = __instance as ShipItem;
                    second = __args[0] as ShipItem;
                }
                else if (name == "OnItemClick" && (type == typeof(HullDamageButton) || type == typeof(BoatDamageWaterButton)) ||
                    name == "InsertFuel")
                    first = __args[0] as ShipItem;
                else if (name == "InsertIntoCookTrigger")
                    first = (__instance as CookableFood)?.GetComponent<ShipItem>();
                else if (name == "CutFood")
                {
                    first = __instance as ShipItem;
                    second = (__args[0] as FoodState)?.GetComponent<ShipItem>();
                }
                else if (name == "CutPack" || name == "Drink" || name == "EatFood" ||
                    ((type == typeof(ShipItemLight) || type == typeof(ShipItemOakum)) && name == "OnAltActivate"))
                    first = __instance as ShipItem;

                state = ItemOperationCapture.Begin(type.Name + "." + name,
                    type == typeof(ShipItemKnife) || type == typeof(ShipItemTobaccoPack), completion, first, second);
            }, Report);
            __state = state;
            return run;
        }
        private static void FinishOperation(ItemOperationCapture.Scope __state, MethodBase __originalMethod)
            => PatchGuard.Run(() => {
                if (__state != null && __originalMethod.Name == "FinishCast") ItemOperationCapture.TotemEffect();
                if (__state != null && __originalMethod.Name == "CutPack") RegisterCreated(__state);
                __state?.Dispose();
            }, Report);
        // CutPack, unlike CutFood, leaves its tobacco without an instance id; the item layer
        // addresses items by that id, so assign it before the operation snapshot is taken.
        private static void RegisterCreated(ItemOperationCapture.Scope scope)
        {
            if (scope.Original == null) return;
            foreach (var item in ShipItemRegistry.Active())
            {
                if (item == null || scope.Original.Contains(item.GetInstanceID())) continue;
                var saveable = item.GetComponent<SaveablePrefab>();
                if (saveable != null && saveable.instanceId == 0 && item.sold) saveable.RegisterToSave();
            }
        }
        // The game calls DrinkOrSpill every frame while a pot is tilted, on every machine that sees
        // it tilted. One machine empties the pot (ItemSync.OwnsDrain) and the level travels as item
        // state: an operation per frame took a snapshot of every item each time, and a watcher's
        // copy spilled along with the holder's.
        private static bool PreDrain(ShipItemSoup __instance, bool __0, out float __state)
        {
            __state = float.NaN;
            // Called every frame for a tilted pot, also outside a session: no closure.
            try
            {
                var sync = ItemSync.Instance;
                if (sync == null || !sync.IsSessionActive) return true;
                __state = __instance.currentWater;
                // Not spilled but poured into this machine's own player: nobody else can feed him.
                if (__0 && !InteractionContext.Suppressed) return sync.OwnsDrain(__instance);
            }
            catch (Exception error) { Report(error); }
            return true;
        }
        private static void PostDrain(ShipItemSoup __instance, float __state)
        {
            if (float.IsNaN(__state) || __instance == null || __state.Equals(__instance.currentWater)) return;
            PatchGuard.Run(() => ItemSync.Instance?.NotifyDrain(__instance), Report);
        }
        private static void PreDestroy(ShipItem __instance) => PatchGuard.Run(() => ItemOperationCapture.Consumed(__instance), Report);
        private static void PostRainbow() => PatchGuard.Run(ItemOperationCapture.RainbowEffect, Report);
    }
}
