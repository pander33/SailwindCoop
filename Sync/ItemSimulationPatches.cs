using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    /// <summary>Preserve local input/personal consumption; world cooking and fuel remain host simulation.</summary>
    internal static class ItemSimulationPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            Prefix(harmony, hooks, typeof(FoodState), "Update", Type.EmptyTypes);
            Prefix(harmony, hooks, typeof(CookableFood), "Cook", new[] { typeof(float), typeof(bool) });
            Prefix(harmony, hooks, typeof(CookableFoodSoup), "Cook", new[] { typeof(float), typeof(bool) });
            Prefix(harmony, hooks, typeof(CookableFoodKettle), "Cook", new[] { typeof(float), typeof(bool) });
            Prefix(harmony, hooks, typeof(ShipItemSoup), "UpdateSpoiled", Type.EmptyTypes);
            Prefix(harmony, hooks, typeof(ShipItemKettle), "BrewTea", Type.EmptyTypes);
            Prefix(harmony, hooks, typeof(ShipItemStove), "AddHeat", new[] { typeof(float) });
            foreach (var type in new[] { typeof(CookableFood), typeof(CookableFoodSoup), typeof(CookableFoodKettle) })
                Capture(harmony, hooks, type, "Update");
            Capture(harmony, hooks, typeof(ShipItemStove), "ExtraLateUpdate");
            Capture(harmony, hooks, typeof(ShipItemLight), "ExtraLateUpdate");
            Capture(harmony, hooks, typeof(StoveFuel), "Update");
            Capture(harmony, hooks, typeof(ShipItemPipe), "ExtraLateUpdate");
            PatchHealth.Report("Item simulation", hooks);
            Plugin.Logger.LogInfo("[ItemSimulationPatches] " + hooks.Detail);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(ItemSimulationPatches).GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic));
        private static void Prefix(Harmony harmony, PatchHookCatalog hooks, Type type, string method, Type[] args)
            => hooks.Install(type, method, args, m => harmony.Patch(m, prefix: Callback(nameof(PreWorld))));
        private static void Capture(Harmony harmony, PatchHookCatalog hooks, Type type, string method)
            => hooks.Install(type, method, Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreSimulation)), postfix: Callback(nameof(PostSimulation))));
        private static void Report(Exception e) { try { Plugin.Logger?.LogWarning("[ItemSimulationPatches] " + e); } catch { } }
        private static ShipItem Item(object instance) => instance as ShipItem ?? (instance as UnityEngine.Component)?.GetComponent<ShipItem>();
        private static bool SharedClient(ShipItem item) => item != null && ItemSync.Instance != null && ItemSync.Instance.IsRemoteSimulationItem(item);
        // These hooks run every frame for every food item, stove, lamp and fuel in the world, including
        // single-player. Only a connected client has anything to do here; everyone else returns before
        // a closure is allocated.
        private static bool ClientSession => ItemSync.Instance != null && ItemSync.Instance.IsClientSession;
        private static bool PreWorld(object __instance)
        {
            if (!ClientSession) return true;
            try { return !SharedClient(Item(__instance)); }
            catch (Exception error) { Report(error); return true; }
        }
        private struct Before
        {
            internal ShipItem Item; internal bool Captured, LightOn; internal float Heat, Health, Amount;
            internal bool PipeLocal;
        }
        private static bool PreSimulation(object __instance, out Before __state)
        {
            if (!ClientSession) { __state = default(Before); return true; }
            return PreSimulationClient(__instance, out __state);
        }
        private static bool PreSimulationClient(object __instance, out Before __state)
        {
            var state = new Before(); bool run = true;
            PatchGuard.Run(() => {
                var item = Item(__instance);
                if (!SharedClient(item)) return;
                state.Item = item; state.Captured = true; state.Health = item.health; state.Amount = item.amount;
                state.PipeLocal = item is ShipItemPipe && item.held != null;
                if (__instance is CookableFood cook) state.Heat = cook.GetCurrentHeat();
                else if (__instance is ShipItemStove stove) state.Heat = stove.GetHeat();
                else if (__instance is ShipItemLight light) state.LightOn = LightSync.IsOn(light);
                // Fuel destruction belongs to the host; don't run the client's terminal branch.
                if (__instance is StoveFuel && item.health <= 0f) run = false;
            }, Report);
            __state = state; return run;
        }
        private static void PostSimulation(object __instance, Before __state)
        {
            if (__state.Captured) PostSimulationClient(__instance, __state);
        }
        private static void PostSimulationClient(object __instance, Before __state)
            => PatchGuard.Run(() => {
                if (!__state.Captured || __state.Item == null) return;
                if (__state.PipeLocal)
                {
                    if (!__state.Health.Equals(__state.Item.health) || !__state.Amount.Equals(__state.Item.amount))
                        ItemSync.Instance?.NotifyPipeUse(__state.Item);
                    return;
                }
                if (__instance is CookableFood) ItemComponents.Set(__instance, "currentHeat", __state.Heat);
                else if (__instance is ShipItemStove) ItemComponents.Set(__instance, "currentHeat", __state.Heat);
                else if (__instance is ShipItemLight light) LightSync.ApplyState(light, __state.LightOn, __state.Health);
                else if (__instance is StoveFuel) __state.Item.health = __state.Health;
            }, Report);
    }
}
