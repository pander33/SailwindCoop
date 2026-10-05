using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class ShipyardRefitPatches
    {
        private sealed class Order : IDisposable
        {
            internal Shipyard Editor;
            internal bool ReachedCommit, Repair, Clean, Finished, PreviousConfirming;
            internal InteractionContext.Scope Scope;
            internal Order Previous;
            internal bool Fault;
            public void Dispose()
            {
                if (Finished) return; Finished = true;
                try { if (ReachedCommit) ShipyardSync.Instance?.CommitLocal(Editor, Repair, Clean, Fault); }
                finally { if (ShipyardSync.Instance != null) ShipyardSync.Instance.Confirming = PreviousConfirming; Scope?.Dispose(); _order = Previous; }
            }
        }
        private static Order _order;
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(Shipyard), "AdmitShip", new[] { typeof(GameObject) }, m => harmony.Patch(m, prefix: Hook(nameof(PreAdmit))));
            hooks.Install(typeof(Shipyard), "CancelOrder", Type.EmptyTypes, m => harmony.Patch(m, prefix: Hook(nameof(PreCancel)), postfix: Hook(nameof(PostCancel))));
            hooks.Install(typeof(Shipyard), "DischargeShip", Type.EmptyTypes, m => harmony.Patch(m, postfix: Hook(nameof(PostExit))));
            hooks.Install(typeof(Shipyard), "ConfirmOrder", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Hook(nameof(PreOrder), Priority.First), postfix: Hook(nameof(EndOrder), Priority.Last), finalizer: Hook(nameof(EndFault))));
            hooks.Install(typeof(ShipyardSailInstaller), "InstallSails", Type.EmptyTypes, m => harmony.Patch(m, prefix: Hook(nameof(PreInstall))));
            hooks.Install(typeof(SaveableBoatCustomization), "GetData", Type.EmptyTypes, m => harmony.Patch(m, prefix: Hook(nameof(PreData))));
            hooks.Install(typeof(Sail), "FixedUpdate", Type.EmptyTypes, m => harmony.Patch(m, prefix: Hook(nameof(PreSail))));
            PatchHealth.Report("Shipyard refit", hooks);
        }
        private static HarmonyMethod Hook(string name, int priority = Priority.Normal)
            => new HarmonyMethod(typeof(ShipyardRefitPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
        private static void Report(Exception error) => ShipyardSync.Report("patch", error);
        private static void PreAdmit(Shipyard __instance, GameObject ship)
            => PatchGuard.Run(() => ShipyardSync.Instance?.BeginPreview(__instance, ship), Report);
        private static void PreCancel(Shipyard __instance) => PatchGuard.Run(() => ShipyardSync.Instance?.BeforeCancel(__instance), Report);
        private static void PostCancel(Shipyard __instance) => PatchGuard.Run(() => ShipyardSync.Instance?.AfterCancel(__instance), Report);
        private static void PostExit(Shipyard __instance) => PatchGuard.Run(() => ShipyardSync.Instance?.EndPreview(__instance), Report);
        private static void PreOrder(Shipyard __instance, out Order __state)
        {
            Order state = null;
            PatchGuard.Run(() => {
                if (ShipyardSync.Instance == null || InteractionContext.Suppressed) return;
                state = new Order { Editor = __instance, Repair = __instance.IncludeRepair(), Clean = __instance.IncludeHullCleaning(),
                    Previous = _order, PreviousConfirming = ShipyardSync.Instance.Confirming };
                state.Scope = InteractionContext.Begin(InteractionSource.LocalInput); _order = state; ShipyardSync.Instance.Confirming = true;
            }, Report); __state = state;
        }
        // Vanilla reaches this call only after its local payment; no affordability checks are repeated by the mod.
        private static void PreInstall() => PatchGuard.Run(() => { if (_order != null) _order.ReachedCommit = true; }, Report);
        private static void EndOrder(Order __state) => PatchGuard.Run(() => __state?.Dispose(), Report);
        private static void EndFault(Order __state, Exception __exception) => PatchGuard.Run(() => {
            if (__state != null) { __state.Fault = __exception != null; __state.Dispose(); }
        }, Report);
        private static bool PreData(SaveableBoatCustomization __instance, ref SaveBoatCustomizationData __result)
        {
            SaveBoatCustomizationData data = null;
            bool vanilla = PatchGuard.Prefix(() => {
                if (ShipyardSync.Instance == null || !ShipyardSync.Instance.SavedPreview(__instance, out data)) return true;
                return false;
            }, Report);
            if (!vanilla) __result = data; return vanilla;
        }
        // Physics-rate hook for every sail: no closure per call.
        private static bool PreSail(Sail __instance)
        {
            try { return !ShipyardSync.PauseSail(__instance); }
            catch (Exception error) { try { Report(error); } catch { } return true; }
        }
    }
}
