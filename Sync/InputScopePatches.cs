using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class InputScopePatches
    {
        private static readonly FieldInfo ClickedButton = AccessTools.Field(typeof(GoPointer), "clickedButton");
        private static readonly FieldInfo StickyClickedButton = AccessTools.Field(typeof(GoPointer), "stickyClickedButton");

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
        // GoPointer.LateUpdate and the wheel's ExtraLateUpdate run every frame: plain try/catch here,
        // not PatchGuard, whose closure and delegates were garbage on every call. Report never throws.
        private static bool PreInput(GoPointer __instance, out InteractionContext.Scope __state)
        {
            __state = null;
            try { __state = InteractionContext.Begin(InteractionSource.LocalInput); }
            catch (Exception e) { Report(e); }
            // A release edge could otherwise be lost during the hold. StartMenu uses its own
            // MouseButtonPointer and remains clickable.
            if (CoopBehaviour.Instance?.HostPause?.Frozen == true)
            {
                try { ReleaseClick(__instance); }
                catch (Exception e) { Report(e); }
                return false;
            }
            bool consumed = false;
            try { consumed = CoopBehaviour.Instance?.Dice?.Input.Handle(__instance) == true; }
            catch (Exception e) { Report(e); }
            // A knocked-over player lets go of what it holds here, in the input scope, and points at nothing.
            if (!consumed)
                try { consumed = CoopBehaviour.Instance?.Knockdown?.HandlePointer(__instance) == true; }
                catch (Exception e) { Report(e); }
            return !consumed;
        }
        private static void FinishInput(InteractionContext.Scope __state)
        {
            try { __state?.Dispose(); }
            catch (Exception e) { Report(e); }
        }

        internal static void ReleaseClick(GoPointer pointer)
        {
            if (pointer == null) return;
            var clicked = ClickedButton?.GetValue(pointer) as GoPointerButton;
            if (clicked != null)
            {
                try
                {
                    clicked.OnUnactivate();
                    clicked.OnUnactivate(pointer);
                    clicked.Unclick();
                }
                catch (Exception e) { Report(e); }
                finally { ClickedButton.SetValue(pointer, null); }
            }
            var sticky = StickyClickedButton?.GetValue(pointer) as GoPointerButton;
            if (sticky != null)
            {
                try
                {
                    sticky.UnStickyClick();
                    var coop = CoopBehaviour.Instance;
                    bool frozen = coop?.HostPause?.Frozen == true;
                    if (frozen || coop?.Knockdown?.Down == true)
                    {
                        if (frozen) coop.HostPause.RestoreControlWhenReady();
                        Refs.SetPlayerControl(false);
                    }
                }
                catch (Exception e) { Report(e); }
                finally { StickyClickedButton.SetValue(pointer, null); }
            }
        }

        private struct LengthState { internal bool Captured; internal float Before; }
        private static void PreLength(GPButtonRopeWinch __instance, out LengthState __state)
        {
            __state = default(LengthState);
            try
            {
                if (__instance != null && __instance.rope != null)
                {
                    __state.Before = __instance.rope.currentLength;
                    __state.Captured = true;
                }
            }
            catch (Exception e) { Report(e); }
        }
        private static void PostLength(GPButtonRopeWinch __instance, LengthState __state)
        {
            try
            {
                if (!__state.Captured || __instance == null || __instance.rope == null ||
                    __instance.rope.currentLength.Equals(__state.Before) || InteractionContext.Suppressed) return;
                // DeltaToLength is invoked only by the grabbed-winch input branch, after quick-release.
                using (InteractionContext.Begin(InteractionSource.ActiveHold))
                    ControlsSync.Instance?.NotifyLocalWinchChanged(__instance);
            }
            catch (Exception e) { Report(e); }
        }
        private struct WheelState { internal bool Captured, Locked, Held; internal float Input; }
        private sealed class WheelPresentation
        {
            internal Quaternion LocalRotation;
            internal float ReturnStarted = -1f;
        }
        private const float WheelReturnSeconds = 0.18f;
        private static readonly ConditionalWeakTable<GPButtonSteeringWheel, WheelPresentation> WheelViews =
            new ConditionalWeakTable<GPButtonSteeringWheel, WheelPresentation>();
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
        private static bool PreWheelInput(GPButtonSteeringWheel __instance, out WheelState __state)
        {
            __state = default(WheelState);
            try
            {
                var coop = CoopBehaviour.Instance;
                if (coop?.HostPause?.Frozen == true ||
                    (coop?.Knockdown?.Down == true && __instance != null && ControlsSync.WheelHeld(__instance)))
                    return false;
                if (__instance != null && !InteractionContext.Suppressed)
                {
                    __state.Input = __instance.currentInput;
                    __state.Held = ControlsSync.WheelHeld(__instance);
                    __state.Captured = true;
                }
            }
            catch (Exception e) { Report(e); }
            return true;
        }
        private static void PostWheelInput(GPButtonSteeringWheel __instance, WheelState __state)
        {
            try
            {
                if (__state.Captured && __state.Held && __instance != null &&
                    !__state.Input.Equals(__instance.currentInput) && !InteractionContext.Suppressed)
                    ControlsSync.Instance?.NotifyWheelInput(__instance);
                if (__instance != null && !InteractionContext.Suppressed)
                    PresentWheel(__instance);
            }
            catch (Exception e) { Report(e); }
        }
        private static void PresentWheel(GPButtonSteeringWheel wheel)
        {
            var net = CoopBehaviour.Instance?.Net;
            if (net?.Role != Role.Client || net.State != LinkState.Connected)
            {
                WheelViews.Remove(wheel);
                return;
            }

            // Desktop input already updates the commanded angle this frame. Only the
            // visible wheel follows it locally. The host still simulates the rudder.
            bool steering = ItemComponents.Read<GoPointer>(wheel, "stickyClickedBy") != null ||
                (Settings.steeringWithMouse && wheel.IsCliked());
            bool returning = WheelViews.TryGetValue(wheel, out var view);
            if (!steering && !returning) return;
            if (ControlsSync.Grabbed(ItemComponents.Read<TouchRotateHandle>(wheel, "rotHandle")))
            {
                WheelViews.Remove(wheel);
                return;
            }
            if (steering)
            {
                view = WheelViews.GetValue(wheel, _ => new WheelPresentation());
                view.LocalRotation = Quaternion.Euler(wheel.currentInput, 0f, 0f);
                view.ReturnStarted = -1f;
                wheel.transform.localRotation = view.LocalRotation;
            }
            else
            {
                float now = Time.unscaledTime;
                if (view.ReturnStarted < 0f) view.ReturnStarted = now;
                float progress = (now - view.ReturnStarted) / WheelReturnSeconds;
                if (progress >= 1f) WheelViews.Remove(wheel);
                else wheel.transform.localRotation = Quaternion.Slerp(view.LocalRotation, wheel.transform.localRotation,
                    Mathf.SmoothStep(0f, 1f, progress));
            }
        }
        private static void PostTouchWheel(GPButtonSteeringWheel __instance, float rotation)
        {
            try
            {
                if (__instance == null || InteractionContext.Suppressed || !ControlsSync.WheelHeld(__instance)) return;
                float input = rotation * __instance.gearRatio;
                if (__instance.currentInput.Equals(input)) return;
                // Touch/VR writes the rudder transform directly and otherwise leaves currentInput stale.
                __instance.currentInput = input;
                ControlsSync.Instance?.NotifyWheelInput(__instance);
            }
            catch (Exception e) { Report(e); }
        }
    }
}
