using System;
using HarmonyLib;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    internal static class AnchorPatches
    {
        public static void Apply(Harmony harmony)
        {
            int patched = 0;
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(GoPointer), "PickUpItem"),
                postfix: new HarmonyMethod(typeof(AnchorPatches), nameof(PostPickup))); patched++; }, Report);
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(GoPointer), "DropItem"),
                prefix: new HarmonyMethod(typeof(AnchorPatches), nameof(PreDrop)),
                postfix: new HarmonyMethod(typeof(AnchorPatches), nameof(PostDrop))); patched++; }, Report);
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(Anchor), "ExtraFixedUpdate"),
                prefix: new HarmonyMethod(typeof(AnchorPatches), nameof(PrePhysics))); patched++; }, Report);
            PatchHealth.Report("Anchor", patched, 3, patched + "/3");
        }
        private static void Report(Exception error) => Plugin.Logger.LogWarning("[AnchorPatches] " + error.Message);
        private static void PostPickup(PickupableItem item)
            => PatchGuard.Run(() => { if (item is Anchor anchor) AnchorSync.Instance?.NotifyPickup(anchor); }, Report);
        private static void PreDrop(GoPointer __instance, ref Anchor __state)
        {
            Anchor captured = null;
            PatchGuard.Run(() => captured = __instance.GetHeldItem() as Anchor, Report);
            __state = captured;
        }
        private static void PostDrop(Anchor __state)
            => PatchGuard.Run(() => { if (__state != null) AnchorSync.Instance?.NotifyDrop(__state); }, Report);
        private static bool PrePhysics(Anchor __instance)
            => PatchGuard.Prefix(() => !(AnchorSync.Instance?.SuppressPhysics(__instance) ?? false), Report);
    }
}
