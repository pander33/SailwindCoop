using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class MooringCarryPatches
    {
        private static MethodInfo _returnRope;
        private static FieldInfo _justUnmoored;

        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) }, m => harmony.Patch(m, postfix: Callback(nameof(PostPickup))));
            hooks.Install(typeof(GoPointer), "DropItem", Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreDrop)), postfix: Callback(nameof(PostDrop))));
            hooks.Install(typeof(PickupableBoatMooringRope), "ThrowRopeTo", new[] { typeof(GPButtonDockMooring) }, m => harmony.Patch(m, postfix: Callback(nameof(PostThrow))));
            hooks.Install(typeof(PickupableBoatMooringRope), "OnTriggerEnter", new[] { typeof(UnityEngine.Collider) }, m => harmony.Patch(m, prefix: Callback(nameof(PreTrigger))));
            // Auto-moor on drop arrived with the 0.41 hull-color component.
            if (typeof(PickupableBoatMooringRope).Assembly.GetType("BoatCustomColors") != null)
            {
                _returnRope = AccessTools.Method(typeof(PickupableBoatMooringRope), "ThrowRopeSequenceLocal", new[] { typeof(Vector3) });
                _justUnmoored = AccessTools.Field(typeof(PickupableBoatMooringRope), "justUnmoored");
                hooks.Install(typeof(PickupableBoatMooringRope), "OnDrop", Type.EmptyTypes, m =>
                {
                    if (_returnRope == null) throw new MissingMethodException("PickupableBoatMooringRope.ThrowRopeSequenceLocal");
                    harmony.Patch(m, prefix: Callback(nameof(PreRopeDrop)));
                });
            }
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
        private static bool PreRopeDrop(PickupableBoatMooringRope __instance)
        {
            return PatchGuard.Prefix(() =>
            {
                bool remote = InteractionContext.Suppressed;
                bool guest = MooringSync.Instance?.SuppressCarryTrigger(__instance) == true;
                if ((!remote && !guest) || __instance.IsMoored() ||
                    ItemComponents.Read<bool>(__instance, "throwing")) return true;

                // The game also drops a held end from ExtraUpdate when its length limit is exceeded.
                if (!remote && !InteractionContext.HasInput && __instance.held?.GetHeldItem() != __instance) return true;
                var initialPosition = ItemComponents.Read<Vector3>(__instance, "initialPos");
                GPButtonDockMooring dock = null;
                if (!remote) dock = FindDropBollard(__instance, initialPosition);
                __instance.transform.localRotation = ItemComponents.Read<Quaternion>(__instance, "initialRot");
                if (dock == null)
                {
                    // A replicated drop cannot pick a bollard on the receiver. The separate
                    // Moor request carries the guest's target and waits if it has not loaded.
                    __instance.StartCoroutine((IEnumerator)_returnRope.Invoke(__instance, new object[] { initialPosition }));
                }
                else
                {
                    // ThrowRopeTo starts the game's animation and relays its selected target.
                    using (InteractionContext.Begin(InteractionSource.LocalInput))
                        __instance.ThrowRopeTo(dock);
                }
                return false;
            }, Report);
        }
        private static GPButtonDockMooring FindDropBollard(PickupableBoatMooringRope rope, Vector3 initialPosition)
        {
            var parent = ItemComponents.Read<Transform>(rope, "initialParent");
            if (parent == null) return null;
            var position = rope.transform.position;
            float nearest = _justUnmoored?.GetValue(rope) is true
                ? 1f : Vector3.Distance(position, parent.TransformPoint(initialPosition));
            GPButtonDockMooring dock = null;
            // 0.41.1 limits a freshly untied end to posts strictly within 1 m.
            foreach (var collider in Physics.OverlapSphere(position, 10f))
            {
                var candidate = collider.GetComponent<GPButtonDockMooring>();
                if (candidate == null) continue;
                float distance = Vector3.Distance(position, collider.transform.position);
                if (distance >= nearest) continue;
                nearest = distance;
                dock = candidate;
            }
            return dock;
        }
        private static bool PreTrigger(PickupableBoatMooringRope __instance)
            => PatchGuard.Prefix(() => !(MooringSync.Instance?.SuppressDockTrigger(__instance) ?? false), Report);
    }
}
