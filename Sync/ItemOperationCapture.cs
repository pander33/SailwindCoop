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
        private static readonly HashSet<ShipItem> _dropped = new HashSet<ShipItem>();
        internal static bool Absorb() => _active != null;
        internal static void Clear() { _active = null; _dropped.Clear(); }
        internal static void MarkDrop(ShipItem item)
        {
            if (InteractionContext.HasInput && !InteractionContext.Suppressed && item != null &&
                (item.GetComponent<StoveFuel>() != null || item.GetComponent<CookableFood>() != null)) _dropped.Add(item);
        }
        internal static bool TakeDrop(ShipItem item) => item != null && _dropped.Remove(item);
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
        internal static Scope Begin(string label, bool creates, bool completedInput = false)
        {
            if (_active != null || InteractionContext.Suppressed || (!InteractionContext.HasInput && !completedInput) ||
                ItemSync.Instance == null || !ItemSync.Instance.IsSessionActive) return null;
            var scope = new Scope { Label = label, Before = ItemSync.Instance.CaptureOperationItems() };
            if (creates)
            {
                scope.Original = new HashSet<int>();
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>()) if (item != null) scope.Original.Add(item.GetInstanceID());
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
            Install(harmony, hooks, typeof(ShipItemSoup), "DrinkOrSpill", new[] { typeof(bool) });
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
                bool completion = name == "Drink" || name == "EatFood" || name == "DrinkOrSpill" || name == "FinishCast";
                if (name == "InsertFuel" || name == "InsertIntoCookTrigger")
                {
                    var item = __instance is CookableFood cook ? cook.GetComponent<ShipItem>() : __args[0] as ShipItem;
                    completion = ItemOperationCapture.TakeDrop(item);
                    if (ItemSync.Instance != null && ItemSync.Instance.IsClientSession &&
                        !InteractionContext.HasInput && !completion && !InteractionContext.Suppressed) { run = false; return; }
                }
                state = ItemOperationCapture.Begin(__originalMethod.DeclaringType.Name + "." + name,
                    __originalMethod.DeclaringType == typeof(ShipItemKnife), completion);
            }, Report);
            __state = state;
            return run;
        }
        private static void FinishOperation(ItemOperationCapture.Scope __state, MethodBase __originalMethod)
            => PatchGuard.Run(() => { if (__state != null && __originalMethod.Name == "FinishCast") ItemOperationCapture.TotemEffect(); __state?.Dispose(); }, Report);
        private static void PreDestroy(ShipItem __instance) => PatchGuard.Run(() => ItemOperationCapture.Consumed(__instance), Report);
        private static void PostRainbow() => PatchGuard.Run(ItemOperationCapture.RainbowEffect, Report);
    }
}
