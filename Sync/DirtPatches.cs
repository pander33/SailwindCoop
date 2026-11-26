using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class DirtPatches
    {
        private struct Stroke { internal bool Active; internal bool Color; }
        private static readonly AccessTools.FieldRef<Cleaner, ShipItem> CleanerItemRef = InitCleanerItemRef();
        private static AccessTools.FieldRef<Cleaner, ShipItem> InitCleanerItemRef()
        {
            try { return AccessTools.FieldRefAccess<Cleaner, ShipItem>("item"); }
            catch { return null; }
        }
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
        // Cleaner.LateUpdate runs every frame for every broom: plain try/catch, not PatchGuard (no
        // closure per call). Report never throws.
        private static void PreHold(Cleaner __instance, out InteractionContext.Scope __state)
        {
            __state = default(InteractionContext.Scope);
            try
            {
                var sync = DirtSync.Instance;
                if (sync == null || !sync.Connected || InteractionContext.Suppressed) return;
                var item = CleanerItemRef != null ? CleanerItemRef(__instance) : ItemComponents.Read<ShipItem>(__instance, "item");
                if (item?.held != null)
                    __state = InteractionContext.Begin(item.held.GetHeldItem() == item ? InteractionSource.ActiveHold : InteractionSource.RemoteApply);
            }
            catch (Exception e) { Report(e); }
        }
        private static void PreOrder(out InteractionContext.Scope __state)
        {
            __state = default(InteractionContext.Scope);
            // Concrete UI command. Failed affordability checks never reach CleanFully.
            try { if (!InteractionContext.Suppressed) __state = InteractionContext.Begin(InteractionSource.LocalInput); }
            catch (Exception e) { Report(e); }
        }
        private static void EndScope(InteractionContext.Scope __state)
        {
            try { __state.Dispose(); }
            catch (Exception e) { Report(e); }
        }
        private static bool PreStroke(CleanableObject paintedObject, out Stroke __state)
        {
            __state = default(Stroke);
            try
            {
                var sync = DirtSync.Instance;
                // ItemSync uses a pointer marker for broom animation. It is not a local hand.
                if (InteractionContext.Suppressed || (sync != null && sync.IsClient &&
                    !InteractionContext.HasInput && DirtSync.Registered(paintedObject))) return false;
                if (sync != null && sync.BeforeStroke(paintedObject))
                    __state = new Stroke { Active = true, Color = Input.GetKey("v") };
            }
            catch (Exception e) { Report(e); }
            return true;
        }
        private static void PostStroke(CleanableObject paintedObject, Vector2 offset, Stroke __state)
        {
            if (!__state.Active) return;
            try { DirtSync.Instance?.AfterStroke(paintedObject, offset, __state.Color); }
            catch (Exception e) { Report(e); }
        }
        private static bool PreCoat(CleanableObject paintedObject)
            => PatchGuard.Prefix(() => !(DirtSync.Instance?.IsClient == true && GameState.playing && !GameState.currentlyLoading &&
                !InteractionContext.Suppressed && DirtSync.Registered(paintedObject)), Report);
        private static void PostTexture(CleanableObject __instance) => PatchGuard.Run(() => DirtSync.Instance?.TextureChanged(__instance), Report);
        private static bool PreClean(CleanableObject __instance) => PatchGuard.Prefix(() => !(DirtSync.Instance?.CleanFully(__instance) ?? false), Report);
    }
}
