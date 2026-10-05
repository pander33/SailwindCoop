using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    internal static class InputScopePatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            bool scope = hooks.Install(typeof(GoPointer), "LateUpdate", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Callback(nameof(PreInput)), postfix: Callback(nameof(FinishInput)),
                finalizer: Callback(nameof(FinishInput))));
            // Every forwarded interaction requires this scope; without it a session would look alive and sync nothing.
            if (!scope) PatchHealth.Block("Input origin (GoPointer.LateUpdate hook is absent)");
            hooks.Install(typeof(GPButtonRopeWinch), "DeltaToLength", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Callback(nameof(PreLength)), postfix: Callback(nameof(PostLength))));
            foreach (string method in new[] { "Lock", "Unlock" })
                hooks.Install(typeof(GPButtonSteeringWheel), method, Type.EmptyTypes, m => harmony.Patch(m,
                    prefix: Callback(nameof(PreLock)), postfix: Callback(nameof(PostLock))));
            hooks.Install(typeof(GPButtonSteeringWheel), "ExtraLateUpdate", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Callback(nameof(PreWheelInput)), postfix: Callback(nameof(PostWheelInput))));
            hooks.Install(typeof(GPButtonSteeringWheel), "ApplyRudderRotationFromWheel", new[] { typeof(float) }, m => harmony.Patch(m,
                postfix: Callback(nameof(PostTouchWheel))));
            PatchHealth.Report("Input origin", hooks);
            Plugin.Logger.LogInfo("[InputScopePatches] role=initializing " + hooks.Detail);
        }
        private static HarmonyMethod Callback(string name)
            => new HarmonyMethod(typeof(InputScopePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error)
        {
            try { Plugin.Logger?.LogWarning("[InputScopePatches] " + error); } catch { }
        }
        private static void PreInput(out InteractionContext.Scope __state)
        {
            InteractionContext.Scope state = null;
            PatchGuard.Run(() => state = InteractionContext.Begin(InteractionSource.LocalInput), Report);
            __state = state;
        }
        private static void FinishInput(InteractionContext.Scope __state)
            => PatchGuard.Run(() => __state?.Dispose(), Report);

        private struct LengthState { internal bool Captured; internal float Before; }
        private static void PreLength(GPButtonRopeWinch __instance, out LengthState __state)
        {
            var state = new LengthState();
            PatchGuard.Run(() => {
                if (__instance != null && __instance.rope != null)
                { state.Before = __instance.rope.currentLength; state.Captured = true; }
            }, Report);
            __state = state;
        }
        private static void PostLength(GPButtonRopeWinch __instance, LengthState __state)
            => PatchGuard.Run(() => {
                if (!__state.Captured || __instance == null || __instance.rope == null ||
                    __instance.rope.currentLength.Equals(__state.Before) || InteractionContext.Suppressed) return;
                // DeltaToLength is invoked only by the grabbed-winch input branch, after quick-release.
                using (InteractionContext.Begin(InteractionSource.ActiveHold))
                    ControlsSync.Instance?.NotifyLocalWinchChanged(__instance);
            }, Report);
        private struct WheelState { internal bool Captured, Locked, Held; internal float Input; }
        private static void PreLock(GPButtonSteeringWheel __instance, out WheelState __state)
        {
            var state = new WheelState();
            PatchGuard.Run(() => { if (__instance != null && !InteractionContext.Suppressed)
                { state.Locked = ControlsSync.Locked(__instance); state.Captured = true; } }, Report);
            __state = state;
        }
        private static void PostLock(GPButtonSteeringWheel __instance, WheelState __state)
            => PatchGuard.Run(() => { if (__state.Captured && __instance != null && !InteractionContext.Suppressed &&
                __state.Locked != ControlsSync.Locked(__instance)) ControlsSync.Instance?.NotifyWheelLock(__instance); }, Report);
        private static void PreWheelInput(GPButtonSteeringWheel __instance, out WheelState __state)
        {
            var state = new WheelState();
            PatchGuard.Run(() => { if (__instance != null && !InteractionContext.Suppressed)
                { state.Input = __instance.currentInput; state.Held = ControlsSync.WheelHeld(__instance); state.Captured = true; } }, Report);
            __state = state;
        }
        private static void PostWheelInput(GPButtonSteeringWheel __instance, WheelState __state)
            => PatchGuard.Run(() => { if (__state.Captured && __state.Held && __instance != null &&
                !__state.Input.Equals(__instance.currentInput) && !InteractionContext.Suppressed)
                ControlsSync.Instance?.NotifyWheelInput(__instance); }, Report);
        private static void PostTouchWheel(GPButtonSteeringWheel __instance, float rotation)
            => PatchGuard.Run(() => {
                if (__instance == null || InteractionContext.Suppressed || !ControlsSync.WheelHeld(__instance)) return;
                float input = rotation * __instance.gearRatio;
                if (__instance.currentInput.Equals(input)) return;
                // Touch/VR writes the rudder transform directly and otherwise leaves currentInput stale.
                __instance.currentInput = input; ControlsSync.Instance?.NotifyWheelInput(__instance);
            }, Report);
    }
}
