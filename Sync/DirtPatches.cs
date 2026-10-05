using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class DirtPatches
    {
        private sealed class Stroke { internal byte[] Before; internal bool Color; }
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(Cleaner), "LateUpdate", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Callback(nameof(PreHold)), postfix: Callback(nameof(EndScope)), finalizer: Callback(nameof(EndScope))));
            hooks.Install(typeof(Shipyard), "ConfirmOrder", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Callback(nameof(PreOrder)), postfix: Callback(nameof(EndScope)), finalizer: Callback(nameof(EndScope))));
            hooks.Install(typeof(MasterPainter), "PaintObject", new[] { typeof(CleanableObject), typeof(Vector2), typeof(Texture) }, m => harmony.Patch(m,
                prefix: Callback(nameof(PreStroke)), postfix: Callback(nameof(PostStroke))));
            hooks.Install(typeof(MasterPainter), "ApplyCoat", new[] { typeof(CleanableObject), typeof(Texture), typeof(float) }, m => harmony.Patch(m, prefix: Callback(nameof(PreCoat))));
            hooks.Install(typeof(CleanableObject), "ApplyNewDirtTexture", new[] { typeof(Texture) }, m => harmony.Patch(m, postfix: Callback(nameof(PostTexture))));
            hooks.Install(typeof(CleanableObject), "CleanFully", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreClean))));
            PatchHealth.Report("Dirt textures", hooks);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(DirtPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[DirtPatches] " + error); } catch { } }
        private static void PreHold(Cleaner __instance, out InteractionContext.Scope __state)
        {
            InteractionContext.Scope state = null;
            PatchGuard.Run(() => {
                var item = ItemComponents.Read<ShipItem>(__instance, "item");
                if (item?.held != null && !InteractionContext.Suppressed)
                    state = InteractionContext.Begin(item.held.GetHeldItem() == item ? InteractionSource.ActiveHold : InteractionSource.RemoteApply);
            }, Report); __state = state;
        }
        private static void PreOrder(out InteractionContext.Scope __state)
        {
            InteractionContext.Scope state = null;
            // Concrete UI command. Failed affordability checks never reach CleanFully.
            PatchGuard.Run(() => { if (!InteractionContext.Suppressed) state = InteractionContext.Begin(InteractionSource.LocalInput); }, Report); __state = state;
        }
        private static void EndScope(InteractionContext.Scope __state) => PatchGuard.Run(() => __state?.Dispose(), Report);
        private static bool PreStroke(CleanableObject paintedObject, out Stroke __state)
        {
            Stroke state = null; bool run = true;
            PatchGuard.Run(() => {
                // ItemSync uses a pointer marker for broom animation. It is not a local hand.
                if (InteractionContext.Suppressed || (DirtSync.Instance?.IsClient == true &&
                    !InteractionContext.HasInput && DirtSync.Registered(paintedObject))) { run = false; return; }
                byte[] before = DirtSync.Instance?.BeforeStroke(paintedObject);
                if (before != null) state = new Stroke { Before = before, Color = Input.GetKey("v") };
            }, Report); __state = state; return run;
        }
        private static void PostStroke(CleanableObject paintedObject, Vector2 offset, Stroke __state)
            => PatchGuard.Run(() => { if (__state != null) DirtSync.Instance?.AfterStroke(paintedObject, __state.Before, offset, __state.Color); }, Report);
        private static bool PreCoat(CleanableObject paintedObject)
            => PatchGuard.Prefix(() => !(DirtSync.Instance?.IsClient == true && GameState.playing && !GameState.currentlyLoading &&
                !InteractionContext.Suppressed && DirtSync.Registered(paintedObject)), Report);
        private static void PostTexture(CleanableObject __instance) => PatchGuard.Run(() => DirtSync.Instance?.TextureChanged(__instance), Report);
        private static bool PreClean(CleanableObject __instance) => PatchGuard.Prefix(() => !(DirtSync.Instance?.CleanFully(__instance) ?? false), Report);
    }
}
