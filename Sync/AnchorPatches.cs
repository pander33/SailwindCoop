using System;
using HarmonyLib;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The game throws only held <c>ShipItem</c>s. Anchor Improvements (2.0.0,
    /// <c>com.nandbrew.anchorimprovements</c>) throws anchors too: from its <c>GoPointer.LateUpdate</c>
    /// postfix it calls <c>DropItem()</c> and adds the force one physics step later, like the game's
    /// <c>ThrowItemAfterDelay</c>. A client sends its drop at <c>DropItem</c> and hands the anchor to
    /// the host's physics at once, so that force never reached the host. Our <c>DropItem</c> postfix
    /// runs inside that mod's call, before it resets its throw power, so the velocity the force will
    /// give is worked out here, with the game's formula (as <c>ItemPatches.ComputeThrowVelocity</c>).
    /// Without that mod, or another version of it, the field is not found and nothing is added.
    /// </summary>
    internal static class AnchorThrow
    {
        private static bool _resolved;
        private static System.Reflection.FieldInfo _power;

        internal static UnityEngine.Vector3 Velocity(GoPointer pointer)
        {
            try
            {
                if (pointer == null) return UnityEngine.Vector3.zero;
                if (!_resolved)
                {
                    _resolved = true;
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.nandbrew.anchorimprovements", out BepInEx.PluginInfo info) && info?.Instance != null)
                        _power = info.Instance.GetType().Assembly.GetType("AnchorRework.Patches+Thrower")?
                            .GetField("currentThrowPower", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (_power != null) Plugin.Logger.LogInfo("[AnchorSync] Anchor Improvements found: thrown anchors carry their throw to the host");
                }
                if (_power == null) return UnityEngine.Vector3.zero;
                float power = (float)_power.GetValue(null);
                if (power <= pointer.throwDelay) return UnityEngine.Vector3.zero;
                float f = UnityEngine.Mathf.Min(power - pointer.throwDelay, 1f);
                // AddForce(forward * throwForce * f * mass), ForceMode.Force: the mass cancels.
                return pointer.transform.forward * pointer.throwForce * f * UnityEngine.Time.fixedDeltaTime;
            }
            catch { return UnityEngine.Vector3.zero; }
        }
    }

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
        private static void PostDrop(GoPointer __instance, Anchor __state)
            => PatchGuard.Run(() => { if (__state != null) AnchorSync.Instance?.NotifyDrop(__state, AnchorThrow.Velocity(__instance)); }, Report);
        // Every physics step: a plain try/catch, not PatchGuard (no closure).
        private static bool PrePhysics(Anchor __instance)
        {
            try { return !(AnchorSync.Instance?.SuppressPhysics(__instance) ?? false); }
            catch (Exception e) { PatchGuard.Fail(Report, e); return true; }
        }
    }
}
