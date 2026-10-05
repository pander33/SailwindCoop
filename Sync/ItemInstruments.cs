using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static partial class ItemComponents
    {
        private static void CaptureInstruments(ShipItem item, ItemDetails state)
        {
            if (item is ShipItemClock clock) { state.HasClock = true; state.ClockOpen = Read<bool>(clock, "lidOpen"); }
            if (item is ShipItemQuadrant quadrant) { state.HasQuadrant = true; state.QuadrantInspect = Read<bool>(quadrant, "inspecting"); }
            if (item is ShipItemScroll scroll)
            { state.HasScroll = true; state.ScrollOpen = Read<Renderer>(scroll, "page")?.enabled ?? false; state.ScrollPage = Read<int>(scroll, "currentPage"); }
            if (item is ShipItemTotem totem) { state.HasTotem = true; state.TotemSpent = totem.health < 0; }
        }
        private static void Animate(ShipItem item, string method, object target)
        {
            var call = item.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            if (call == null) throw new MissingMethodException(item.GetType().Name, method);
            item.StartCoroutine((IEnumerator)call.Invoke(item, new[] { target }));
        }
        internal static void ApplyInstruments(ShipItem item, ItemDetails state)
        {
            if (state.HasClock && item is ShipItemClock clock && Read<bool>(clock, "lidOpen") != state.ClockOpen)
            {
                clock.StopAllCoroutines(); Set(clock, "lidOpen", state.ClockOpen); Set(clock, "lidAnimPlaying", true);
                Animate(clock, "RotateLid", state.ClockOpen ? 105f : 0f);
            }
            if (state.HasQuadrant && item is ShipItemQuadrant quadrant && Read<bool>(quadrant, "inspecting") != state.QuadrantInspect)
            {
                quadrant.StopAllCoroutines(); Set(quadrant, "inspecting", state.QuadrantInspect);
                Animate(quadrant, "SmoothlyRotate", Read<Quaternion>(quadrant, state.QuadrantInspect ? "inspectRot" : "initialRot"));
            }
            if (state.HasScroll && item is ShipItemScroll scroll)
            {
                var pages = Read<Texture[]>(scroll, "pages"); var page = Read<Renderer>(scroll, "page");
                if (pages != null && state.ScrollPage >= 0 && state.ScrollPage < pages.Length)
                { Set(scroll, "currentPage", state.ScrollPage); if (page != null) page.material.mainTexture = pages[state.ScrollPage]; }
                if (page != null) page.enabled = state.ScrollOpen;
                var filter = Read<MeshFilter>(scroll, "filter"); if (filter != null) filter.sharedMesh = Read<Mesh>(scroll, state.ScrollOpen ? "openMesh" : "closedMesh");
                Read<GameObject>(scroll, "arrowUp")?.SetActive(state.ScrollOpen && state.ScrollPage > 0);
                Read<GameObject>(scroll, "arrowDown")?.SetActive(state.ScrollOpen && pages != null && state.ScrollPage < pages.Length - 1);
            }
            if (state.HasTotem && item is ShipItemTotem totem && state.TotemSpent)
            {
                Set(totem, "casting", false); Set(totem, "castingTime", 0f);
                Read<Renderer>(totem, "rune").gameObject.SetActive(true);
                Read<Renderer>(totem, "totem").enabled = false; totem.GetComponent<Renderer>().enabled = false;
            }
        }
    }
    internal static class ItemInstrumentPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            foreach (var type in new[] { typeof(ShipItemClock), typeof(ShipItemQuadrant) })
                hooks.Install(type, "OnAltActivate", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreState)), postfix: Callback(nameof(PostState))));
            hooks.Install(typeof(ShipItemScroll), "OnScroll", new[] { typeof(float) }, m => harmony.Patch(m, prefix: Callback(nameof(PreState)), postfix: Callback(nameof(PostState))));
            // Remote shared resource destruction is delivered by the authoritative lifecycle.
            hooks.Install(typeof(ShipItem), "DestroyItem", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreDestroy))));
            PatchHealth.Report("Special item visuals", hooks);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(ItemInstrumentPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[ItemInstrumentPatches] " + error); } catch { } }
        private static void PreState(ShipItem __instance, out ItemDetails __state)
        {
            ItemDetails state = null;
            PatchGuard.Run(() => { if (__instance != null && __instance.sold && InteractionContext.HasInput && !InteractionContext.Suppressed) state = ItemComponents.Capture(__instance); }, Report);
            __state = state;
        }
        private static void PostState(ShipItem __instance, ItemDetails __state)
            => PatchGuard.Run(() => { if (__state != null && __instance != null && !InteractionContext.Suppressed && !__state.Equals(ItemComponents.Capture(__instance)))
                ItemSync.Instance?.NotifyItemStateChanged(__instance, "instrument target"); }, Report);
        private static bool PreDestroy(ShipItem __instance)
            => PatchGuard.Prefix(() => !(__instance is ShipItemTotem && (ItemSync.Instance?.IsRemoteSimulationItem(__instance) ?? false) &&
                !InteractionContext.HasInput && !InteractionContext.Suppressed), Report);
    }
}
