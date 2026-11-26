using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Preserve local input/personal consumption; world cooking and fuel remain host simulation.</summary>
    internal static class ItemSimulationPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            Prefix(harmony, hooks, typeof(FoodState), "Update", Type.EmptyTypes);
            Prefix(harmony, hooks, typeof(CookableFood), "Cook", new[] { typeof(float), typeof(bool) });
            Prefix(harmony, hooks, typeof(CookableFoodSoup), "Cook", new[] { typeof(float), typeof(bool) });
            Prefix(harmony, hooks, typeof(CookableFoodKettle), "Cook", new[] { typeof(float), typeof(bool) });
            Prefix(harmony, hooks, typeof(ShipItemSoup), "UpdateSpoiled", Type.EmptyTypes);
            Prefix(harmony, hooks, typeof(ShipItemKettle), "BrewTea", Type.EmptyTypes);
            Prefix(harmony, hooks, typeof(ShipItemStove), "AddHeat", new[] { typeof(float) });
            foreach (var type in new[] { typeof(CookableFood), typeof(CookableFoodSoup), typeof(CookableFoodKettle) })
                Capture(harmony, hooks, type, "Update");
            Capture(harmony, hooks, typeof(ShipItemStove), "ExtraLateUpdate");
            Capture(harmony, hooks, typeof(ShipItemLight), "ExtraLateUpdate");
            Capture(harmony, hooks, typeof(StoveFuel), "Update");
            Capture(harmony, hooks, typeof(ShipItemPipe), "ExtraLateUpdate");
            hooks.Install(typeof(ShipItem), "ExtraFixedUpdate", Type.EmptyTypes,
                m => harmony.Patch(m, prefix: Callback(nameof(PreBoatMembership))));
            hooks.Install(typeof(BoatMass), "UpdateMass", Type.EmptyTypes,
                m => harmony.Patch(m, prefix: Callback(nameof(PreBoatMassUpdateMass)), finalizer: Callback(nameof(FinalBoatMassUpdateMass))));
            PatchHealth.Report("Item simulation", hooks);
            Plugin.Logger.LogInfo("[ItemSimulationPatches] " + hooks.Detail);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(ItemSimulationPatches).GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic));
        private static void Prefix(Harmony harmony, PatchHookCatalog hooks, Type type, string method, Type[] args)
            => hooks.Install(type, method, args, m => harmony.Patch(m, prefix: Callback(nameof(PreWorld))));
        private static void Capture(Harmony harmony, PatchHookCatalog hooks, Type type, string method)
            => hooks.Install(type, method, Type.EmptyTypes, m => harmony.Patch(m, prefix: Callback(nameof(PreSimulation)), postfix: Callback(nameof(PostSimulation))));
        private static void Report(Exception e) { try { Plugin.Logger?.LogWarning("[ItemSimulationPatches] " + e); } catch { } }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UnityEngine.Component, ShipItem> _componentItems =
            new System.Runtime.CompilerServices.ConditionalWeakTable<UnityEngine.Component, ShipItem>();
        private static ShipItem Item(object instance)
        {
            if (instance is ShipItem direct) return direct;
            var comp = instance as UnityEngine.Component;
            if (comp == null) return null;
            if (_componentItems.TryGetValue(comp, out var cached) && cached != null) return cached;
            var found = comp.GetComponent<ShipItem>();
            if (found != null)
            {
                _componentItems.Remove(comp);
                _componentItems.Add(comp, found);
            }
            return found;
        }
        private static bool SharedClient(ShipItem item) => item != null && ItemSync.Instance != null && ItemSync.Instance.IsRemoteSimulationItem(item);
        // These hooks run every frame for every food item, stove, lamp and fuel in the world, including
        // single-player. Only a connected client has anything to do here; everyone else returns before
        // a closure is allocated.
        private static bool ClientSession => ItemSync.Instance != null && ItemSync.Instance.IsClientSession;
        private static bool PreWorld(object __instance)
        {
            if (!ClientSession) return true;
            try { return !SharedClient(Item(__instance)); }
            catch (Exception error) { Report(error); return true; }
        }
        // ShipItem.ExtraFixedUpdate only enters and leaves boats by the embark trigger. On the host an
        // item in a client's hand has its root collider off, so the game sees no trigger and calls
        // ExitBoat two steps after each boat-frame pose had put it back: one "ExitBoat" log line,
        // a BoatMass remove/add and a broken hanging joint per pose. On the client, remote-driven
        // puppets get their boat membership from the host's stream (ApplyRemote), so local trigger
        // enter/exit must not detach settled boat puppets or hijack world puppets onto passing boats.
        // The counter is kept at zero so a drop or local takeover starts with full grace.
        private static AccessTools.FieldRef<ShipItem, int> _frameCounter;
        private static bool _frameCounterMissing;
        private static bool PreBoatMembership(ShipItem __instance)
        {
            var sync = ItemSync.Instance;
            if (sync == null) return true;
            bool isHost = sync.IsHostSession;
            bool isClient = !isHost && sync.IsClientSession;
            if (!isHost && !isClient) return true;
            try
            {
                if (isHost ? !sync.HeldByRemotePlayer(__instance) : !sync.IsRemotePuppetItem(__instance))
                    return true;
                if (_frameCounter == null && !_frameCounterMissing)
                {
                    try { _frameCounter = AccessTools.FieldRefAccess<ShipItem, int>("frameCounter"); }
                    catch (Exception e) { _frameCounterMissing = true; Report(e); }
                }
                if (_frameCounter != null) _frameCounter(__instance) = 0;
                return false;
            }
            catch (Exception error) { Report(error); return true; }
        }
        private static AccessTools.FieldRef<BoatMass, List<ItemRigidbody>> _boatMassItems;
        private static bool _boatMassItemsMissing;
        private static readonly List<ItemRigidbody> _boatMassDeferred = new List<ItemRigidbody>(4);
        private const float MaxBoatLocalMassRadiusSq = 64f * 64f;

        private static bool IsValidBoatLocal(Vector3 p)
            => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z) &&
               !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z) &&
               p.sqrMagnitude <= MaxBoatLocalMassRadiusSq;

        // Guard BoatMass.UpdateMass on the host against newly-spawned proxies whose Start() has not
        // created their Rigidbody yet (would throw NullReferenceException and abort mass calculation)
        // and against proxies whose transform is not in the boat's walkCol frame or has an extreme
        // localPosition (e.g. after ResetPos() or clipping out of the hull), which would otherwise
        // throw centerOfMass far outside the hull and violently jerk or launch the ship.
        private static void PreBoatMassUpdateMass(BoatMass __instance)
        {
            var sync = ItemSync.Instance;
            if (sync == null || !sync.IsHostSession || __instance == null) return;
            try
            {
                if (_boatMassItems == null && !_boatMassItemsMissing)
                {
                    try { _boatMassItems = AccessTools.FieldRefAccess<BoatMass, List<ItemRigidbody>>("itemsOnBoat"); }
                    catch (Exception e) { _boatMassItemsMissing = true; Report(e); }
                }
                if (_boatMassItems == null) return;
                var list = _boatMassItems(__instance);
                if (list == null || list.Count == 0) return;

                _boatMassDeferred.Clear();
                Transform boatRoot = __instance.transform;
                int write = 0;
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    var irb = list[i];
                    if (irb == null) { list[write++] = null; continue; }
                    if (!irb.gameObject.activeInHierarchy || irb.GetCurrentBox() != null)
                    {
                        _boatMassDeferred.Add(irb);
                        continue;
                    }
                    var body = irb.GetBody();
                    if (body == null)
                    {
                        _boatMassDeferred.Add(irb);
                        continue;
                    }
                    float mass = body.mass;
                    if (float.IsNaN(mass) || float.IsInfinity(mass) || mass < 0f)
                    {
                        _boatMassDeferred.Add(irb);
                        continue;
                    }
                    var item = irb.GetShipItem();
                    if (item == null)
                    {
                        continue;
                    }
                    if (item.currentActualBoat == null || item.currentActualBoat.parent != boatRoot)
                    {
                        continue;
                    }
                    // Locally host-held items use Refs.observerMirror.transform.localPosition in vanilla
                    // BoatMass.UpdateMass(), not irb.transform.localPosition.
                    if (item.held != null) { list[write++] = irb; continue; }
                    bool badParent = item.currentWalkCol != null && irb.transform.parent != item.currentWalkCol;
                    bool badPos = !IsValidBoatLocal(irb.transform.localPosition);
                    if (badParent || badPos)
                    {
                        if (item.currentWalkCol != null && sync.HeldByRemotePlayer(item))
                        {
                            if (badParent) irb.transform.parent = item.currentWalkCol;
                            irb.ForceRigidbodyToWalkCol();
                            badParent = false;
                            badPos = !IsValidBoatLocal(irb.transform.localPosition);
                        }
                        if (badParent || badPos)
                        {
                            _boatMassDeferred.Add(irb);
                            continue;
                        }
                    }
                    list[write++] = irb;
                }
                if (write < count) list.RemoveRange(write, count - write);
            }
            catch (Exception error) { Report(error); }
        }

        private static void FinalBoatMassUpdateMass(BoatMass __instance)
        {
            if (_boatMassDeferred.Count == 0) return;
            try
            {
                if (__instance != null && _boatMassItems != null)
                {
                    var list = _boatMassItems(__instance);
                    if (list != null)
                    {
                        for (int i = 0; i < _boatMassDeferred.Count; i++)
                        {
                            var irb = _boatMassDeferred[i];
                            if (irb != null) list.Add(irb);
                        }
                    }
                }
            }
            catch (Exception error) { Report(error); }
            finally { _boatMassDeferred.Clear(); }
        }

        private struct Before
        {
            internal ShipItem Item; internal bool Captured, LightOn; internal float Heat, Health, Amount;
            internal bool PipeLocal;
        }
        private static bool PreSimulation(object __instance, out Before __state)
        {
            if (!ClientSession) { __state = default(Before); return true; }
            return PreSimulationClient(__instance, out __state);
        }
        // Per-frame, per-item hooks: a plain try/catch, not PatchGuard — its closure and delegates were
        // most of a client's garbage (65 food items, every frame).
        private static bool PreSimulationClient(object __instance, out Before __state)
        {
            __state = default(Before);
            try
            {
                var item = Item(__instance);
                if (!SharedClient(item)) return true;
                var state = new Before();
                state.Item = item; state.Captured = true; state.Health = item.health; state.Amount = item.amount;
                state.PipeLocal = item is ShipItemPipe && item.held != null;
                if (__instance is CookableFood cook) state.Heat = cook.GetCurrentHeat();
                else if (__instance is ShipItemStove stove) state.Heat = stove.GetHeat();
                else if (__instance is ShipItemLight light) state.LightOn = LightSync.IsOn(light);
                __state = state;
                // Fuel destruction belongs to the host; don't run the client's terminal branch.
                return !(__instance is StoveFuel && item.health <= 0f);
            }
            catch (Exception error) { Report(error); return true; }
        }
        // currentHeat is put back every frame for every shared cooking item: a field ref, not
        // FieldInfo.SetValue, which boxes the float.
        private static AccessTools.FieldRef<CookableFood, float> _cookHeat;
        private static AccessTools.FieldRef<ShipItemStove, float> _stoveHeat;
        private static bool _heatRefsTried;
        private static void SetHeat(object instance, float heat)
        {
            if (!_heatRefsTried)
            {
                _heatRefsTried = true;
                try { _cookHeat = AccessTools.FieldRefAccess<CookableFood, float>("currentHeat"); } catch (Exception e) { Report(e); }
                try { _stoveHeat = AccessTools.FieldRefAccess<ShipItemStove, float>("currentHeat"); } catch (Exception e) { Report(e); }
            }
            if (instance is CookableFood cook && _cookHeat != null) _cookHeat(cook) = heat;
            else if (instance is ShipItemStove stove && _stoveHeat != null) _stoveHeat(stove) = heat;
            else ItemComponents.Set(instance, "currentHeat", heat);
        }
        private static void PostSimulation(object __instance, Before __state)
        {
            if (__state.Captured) PostSimulationClient(__instance, __state);
        }
        private static void PostSimulationClient(object __instance, Before __state)
        {
            try
            {
                if (!__state.Captured || __state.Item == null) return;
                if (__state.PipeLocal)
                {
                    if (!__state.Health.Equals(__state.Item.health) || !__state.Amount.Equals(__state.Item.amount))
                        ItemSync.Instance?.NotifyPipeUse(__state.Item);
                    return;
                }
                if (__instance is CookableFood)
                {
                    SetHeat(__instance, __state.Heat);
                    if (__instance is CookableFoodKettle kettle) ItemComponents.BoilSound(kettle);
                }
                else if (__instance is ShipItemStove) SetHeat(__instance, __state.Heat);
                else if (__instance is ShipItemLight light) LightSync.ApplyState(light, __state.LightOn, __state.Health);
                else if (__instance is StoveFuel) __state.Item.health = __state.Health;
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }
    }
}
