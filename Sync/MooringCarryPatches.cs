using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    internal static class MooringCarryPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) }, m => harmony.Patch(m, postfix: Callback(nameof(PostPickup))));
            hooks.Install(typeof(GoPointer), "DropItem", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreDrop)), postfix: Callback(nameof(PostDrop))));
            hooks.Install(typeof(PickupableBoatMooringRope), "ThrowRopeTo", new[] { typeof(GPButtonDockMooring) }, m => harmony.Patch(m, postfix: Callback(nameof(PostThrow))));
            hooks.Install(typeof(PickupableBoatMooringRope), "OnTriggerEnter", new[] { typeof(UnityEngine.Collider) }, m => harmony.Patch(m, prefix: Callback(nameof(PreTrigger))));
            PatchHealth.Report("Mooring carry", hooks);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(MooringCarryPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[MooringCarryPatches] " + error); } catch { } }
        private static bool IsCarry(PickupableItem item) => item is PickupableBoatMooringRope || item is MooringRopeLengthAdjuster;
        private static void PostPickup(PickupableItem item)
            => PatchGuard.Run(() => { if (IsCarry(item) && item.held != null) MooringSync.Instance?.NotifyCarry(item, true); }, Report);
        private static void PreDrop(GoPointer __instance, out PickupableItem __state)
        {
            PickupableItem captured = null;
            PatchGuard.Run(() => { var item = __instance.GetHeldItem(); if (IsCarry(item)) captured = item; }, Report);
            __state = captured;
        }
        private static void PostDrop(PickupableItem __state)
            => PatchGuard.Run(() => { if (__state != null && __state.held == null) MooringSync.Instance?.NotifyCarry(__state, false); }, Report);
        private static void PostThrow(PickupableBoatMooringRope __instance, GPButtonDockMooring mooring)
            => PatchGuard.Run(() => MooringSync.Instance?.NotifyThrow(__instance, mooring), Report);
        private static bool PreTrigger(PickupableBoatMooringRope __instance)
            => PatchGuard.Prefix(() => !(MooringSync.Instance?.SuppressCarryTrigger(__instance) ?? false), Report);
    }
}
