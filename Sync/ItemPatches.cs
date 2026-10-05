using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public static class ItemPatches
    {
        private static FieldInfo _fHeldItem;
        private static FieldInfo _fOarIsRowing;
        private static FieldInfo _fPointedAtButton;

        public static void Apply(Harmony harmony)
        {
            var hooks = new SailwindCoop.Runtime.PatchHookCatalog();
            TryPatch(harmony, hooks, typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) }, prefixName: nameof(PrePickup), postfixName: nameof(PostPickup));
            TryPatch(harmony, hooks, typeof(GoPointer), "DropItem", Type.EmptyTypes, prefixName: nameof(PreDrop), postfixName: nameof(PostDrop));
            TryPatch(harmony, hooks, typeof(ShipItemBottle), "OnItemClick", new[] { typeof(PickupableItem) },
                prefixName: nameof(PreBottleItemClick), postfixName: nameof(PostBottleItemClick));
            TryPatch(harmony, hooks, typeof(ShipItemOar), "OnAltHeld", Type.EmptyTypes, postfixName: nameof(PostOarAltHeld));
            // Eating food is not an OnAltHeld replay (OnAltHeld only sets a flag; EatFood does the consume
            // + DestroyItem and touches the eater's personal PlayerNeeds). Forward the actual consume so
            // the host destroys its copy without running its own PlayerNeeds.
            TryPatch(harmony, hooks, typeof(ShipItemFood), "EatFood", Type.EmptyTypes, prefixName: nameof(PreEatFood));
            // Hammer nailing targets the item the LOCAL pointer aims at; the held-action replay can't
            // reproduce that aim, so we sync the result (target.nailed) from the two sites that change it:
            // NailItem (nail completes after the 2s hold) and OnAltActivate (instant un-nail).
            TryPatch(harmony, hooks, typeof(ShipItemHammer), "NailItem", new[] { typeof(ShipItem) }, prefixName: nameof(PreNailItem), postfixName: nameof(PostNailItem));
            TryPatch(harmony, hooks, typeof(ShipItemHammer), "OnAltActivate", Type.EmptyTypes, prefixName: nameof(PreHammerAltActivate), postfixName: nameof(PostHammerAltActivate));
            // A caught fish is created on the client by FishingRodFish.CollectFish (returns the new item);
            // forward it so the host authors the authoritative copy (client item replication is host-only).
            TryPatch(harmony, hooks, typeof(FishingRodFish), "CollectFish", Type.EmptyTypes, postfixName: nameof(PostCollectFish));
            // Крючок удочки: наличие = rod.health; ставится/теряется только на машине держащего
            // (OnItemClick attach / DetachHook при сходе рыбы) — форвардим результат, как nail.
            TryPatch(harmony, hooks, typeof(ShipItemFishingRod), "DetachHook", Type.EmptyTypes, prefixName: nameof(PreRodItemClick), postfixName: nameof(PostDetachHook));
            TryPatch(harmony, hooks, typeof(ShipItemFishingRod), "OnItemClick", new[] { typeof(PickupableItem) },
                prefixName: nameof(PreRodItemClick), postfixName: nameof(PostRodItemClick));
            TryPatch(harmony, hooks, typeof(ShipItemLampHook), "OnItemClick", new[] { typeof(PickupableItem) },
                postfixName: nameof(PostLampHookItemClick));
            // Crates: mirror inventory membership (Insert/Withdraw) and relay unseal (item creation) to host.
            TryPatch(harmony, hooks, typeof(CrateInventory), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCrateInsert));
            TryPatch(harmony, hooks, typeof(CrateInventory), "WithdrawItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCrateWithdraw));
            TryPatch(harmony, hooks, typeof(ShipItemCrate), "UnsealCrate", Type.EmptyTypes, prefixName: nameof(PreUnseal));
            // Cargo load/unload uses each player's OWN wallet (local money) → vanilla runs locally; we only
            // mirror the resulting membership (like crates). Postfix on insert; withdraw captures the item
            // in a prefix (it isn't an argument) and forwards it in the postfix.
            TryPatch(harmony, hooks, typeof(CargoCarrier), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCargoInsert));
            TryPatch(harmony, hooks, typeof(CargoCarrier), "WithdrawItem", new[] { typeof(GoPointer), typeof(int) }, prefixName: nameof(PreCargoWithdraw), postfixName: nameof(PostCargoWithdraw));
            TryPatch(harmony, hooks, typeof(GPButtonInventorySlot), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostInventoryInsert));
            TryPatch(harmony, hooks, typeof(GPButtonInventorySlot), "WithdrawItem", Type.EmptyTypes, prefixName: nameof(PreInventoryWithdraw), postfixName: nameof(PostInventoryWithdraw));
            TryPatch(harmony, hooks, typeof(IslandMarket), "SpawnGood", new[] { typeof(GameObject) },
                prefixName: nameof(PreMarketSpawnGood), postfixName: nameof(PostMarketSpawnGood));
            TryPatch(harmony, hooks, typeof(IslandMarketWarehouseArea), "SellGood", new[] { typeof(int) },
                prefixName: nameof(PreWarehouseSellGood));

            TryPatch(harmony, hooks, typeof(ShipItemBottle), "Drink", Type.EmptyTypes, prefixName: nameof(PreBottleDrink), postfixName: nameof(PostBottleDrink));
            TryPatch(harmony, hooks, typeof(ShipItemFoldable), "OnAltActivate", Type.EmptyTypes,
                prefixName: nameof(PreFoldableAltActivate), postfixName: nameof(PostFoldableAltActivate));
            TryPatch(harmony, hooks, typeof(ShipItemBroom), "OnAltActivate", Type.EmptyTypes,
                postfixName: nameof(PostBroomAltActivate));
            // Every supported action has an explicit result hook. Empty pointer overloads
            // and pending personal/world effects must not become generic host replay.
            SailwindCoop.Runtime.PatchHealth.Report("Items", hooks);
            Plugin.Logger.LogInfo("[ItemPatches] " + hooks.Detail);
        }

        private static bool TryPatch(Harmony harmony, SailwindCoop.Runtime.PatchHookCatalog hooks,
            Type type, string method, Type[] args, string prefixName = null, string postfixName = null)
        {
            bool ok = hooks.Install(type, method, args, target => {
                HarmonyMethod prefix = prefixName == null ? null : Callback(prefixName);
                HarmonyMethod postfix = postfixName == null ? null : Callback(postfixName);
                harmony.Patch(target, prefix: prefix, postfix: postfix);
            });
            if (!ok) WarnPatch("[ItemPatches] role=initializing required hook " +
                SailwindCoop.Runtime.PatchHookCatalog.Signature(type.Name, method, args) +
                " missing/failed; " + hooks.Detail);
            return ok;
        }

        private static HarmonyMethod Callback(string name)
        {
            var method = typeof(ItemPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(typeof(ItemPatches).Name, name);
            return new HarmonyMethod(method);
        }

        private static void WarnPatch(string message)
        {
            try { Plugin.Logger?.LogWarning(message); } catch { }
        }

        private static void PrePickup(PickupableItem item)
        { try { MooringPatches.ForgetPickup(item as PickupableBoatMooringRope); }
          catch (Exception e) { WarnPatch("[ItemPatches] PrePickup: " + e); } }
        private static void PostPickup(GoPointer __instance, PickupableItem item)
        {
            try { ItemSync.Instance?.NotifyPickup(__instance, item); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostPickup: " + e.Message); }
        }

        private sealed class DropState
        {
            public PickupableItem Item;
            public bool SurfacePlaced;
        }

        private static void PreDrop(GoPointer __instance, ref DropState __state)
        {
            try
            {
                if (_fHeldItem == null)
                    _fHeldItem = typeof(GoPointer).GetField("heldItem", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fPointedAtButton == null)
                    _fPointedAtButton = typeof(GoPointer).GetField("pointedAtButton", BindingFlags.NonPublic | BindingFlags.Instance);

                var item = _fHeldItem != null ? _fHeldItem.GetValue(__instance) as PickupableItem : null;
                var pointedAt = _fPointedAtButton != null ? _fPointedAtButton.GetValue(__instance) as GoPointerButton : null;
                MooringPatches.MarkDrop(item as PickupableBoatMooringRope);
                ItemOperationCapture.MarkDrop(item as ShipItem);
                __state = new DropState
                {
                    Item = item,
                    SurfacePlaced = IsSurfacePlacement(pointedAt, item),
                };
            }
            catch { __state = new DropState(); }
        }

        private static FieldInfo _fCurrentThrowPower;

        private static void PostDrop(GoPointer __instance, DropState __state)
        {
            try { ItemSync.Instance?.NotifyDrop(__instance, __state != null ? __state.Item : null, ComputeThrowVelocity(__instance), __state != null && __state.SurfacePlaced); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostDrop: " + e.Message); }
        }

        private static bool IsSurfacePlacement(GoPointerButton target, PickupableItem held)
        {
            try
            {
                if (target == null || held == null || !target.allowPlacingItems) return false;
                if (target is GPButtonInventorySlot) return false;
                if (target is CrateInventoryButton) return false;
                if (target is ShipItemLampHook) return false;
                return held is ShipItem;
            }
            catch { return false; }
        }

        // Vanilla throws via ThrowItemAfterDelay, started right after DropItem(): one WaitForFixedUpdate
        // later it AddForce(forward * throwForce * f * mass) with ForceMode.Force. Δv = force/mass * dt,
        // so mass cancels: Δv = forward * throwForce * f * fixedDeltaTime, where f = min(power - delay, 1)
        // and the coroutine only runs when power > throwDelay. We replicate that velocity at drop time
        // because the deferred impulse never lands on our kinematic puppet. currentThrowPower is still set
        // here (the game zeroes it after the StartCoroutine call). Returns zero for a plain (non-T) drop.
        private static Vector3 ComputeThrowVelocity(GoPointer pointer)
        {
            try
            {
                if (pointer == null) return Vector3.zero;
                if (_fCurrentThrowPower == null)
                    _fCurrentThrowPower = typeof(GoPointer).GetField("currentThrowPower", BindingFlags.NonPublic | BindingFlags.Instance);
                float power = _fCurrentThrowPower != null ? (float)_fCurrentThrowPower.GetValue(pointer) : 0f;
                if (power <= pointer.throwDelay) return Vector3.zero;
                float f = Mathf.Min(power - pointer.throwDelay, 1f);
                return pointer.transform.forward * pointer.throwForce * f * Time.fixedDeltaTime;
            }
            catch { return Vector3.zero; }
        }

        private struct BottleClickState
        {
            public bool HasTarget;
            public float TargetAmount;
            public float TargetHealth;
            public bool HasHeld;
            public float HeldAmount;
            public float HeldHealth;
        }

        private static bool PreBottleItemClick(ShipItemBottle __instance, PickupableItem __0, out BottleClickState __state)
        {
            __state = new BottleClickState();
            try
            {
                if (__instance != null)
                {
                    __state.HasTarget = true;
                    __state.TargetAmount = __instance.amount;
                    __state.TargetHealth = __instance.health;
                }

                var heldBottle = BottleOf(__0);
                if (heldBottle != null)
                {
                    // Vanilla plays the pour sound even when zero liquid can move. On a client this looked
                    // like infinite refills from a barrel with a full mug. Skip the no-op transfer entirely.
                    if (__instance != null && heldBottle.GetRemainingCapacity() <= 0.0001f &&
                        heldBottle.GetCapacity() <= __instance.GetCapacity())
                        return false;
                    __state.HasHeld = true;
                    __state.HeldAmount = heldBottle.amount;
                    __state.HeldHealth = heldBottle.health;
                }
            }
            catch (Exception e)
            {
                WarnPatch("[ItemPatches] PreBottleItemClick: " + e.Message);
            }
            return true;
        }

        private static void PostBottleItemClick(ShipItemBottle __instance, PickupableItem __0, BottleClickState __state)
        {
            try
            {
                var sync = ItemSync.Instance;
                if (sync == null) return;

                const float eps = 0.0001f;
                var heldBottle = BottleOf(__0);
                bool targetChanged = __state.HasTarget && __instance != null &&
                    (Mathf.Abs(__instance.amount - __state.TargetAmount) > eps ||
                     Mathf.Abs(__instance.health - __state.TargetHealth) > eps);
                bool heldChanged = __state.HasHeld && heldBottle != null &&
                    (Mathf.Abs(heldBottle.amount - __state.HeldAmount) > eps ||
                     Mathf.Abs(heldBottle.health - __state.HeldHealth) > eps);

                if (targetChanged) sync.NotifyItemStateChanged(__instance, "bottle-click-target");
                if (heldChanged && !ReferenceEquals(heldBottle, __instance)) sync.NotifyItemStateChanged(heldBottle, "bottle-click-held");

                if (targetChanged || heldChanged)
                {
                    Plugin.Logger.LogInfo("[ItemPatches] BottleClick sync target=" +
                        (targetChanged ? __instance.name : "-") + " held=" +
                        (heldChanged && heldBottle != null ? heldBottle.name : "-"));
                }
            }
            catch (Exception e)
            {
                WarnPatch("[ItemPatches] PostBottleItemClick: " + e.Message);
            }
        }

        private static void PostOarAltHeld(ShipItemOar __instance)
        {
            try
            {
                if (!OarIsRowing(__instance)) return;
                ItemSync.Instance?.NotifyAltHeld(__instance);
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PostOarAltHeld: " + e.Message); }
        }

        private static bool OarIsRowing(ShipItemOar oar)
        {
            try
            {
                if (oar == null) return false;
                if (_fOarIsRowing == null)
                    _fOarIsRowing = typeof(ShipItemOar).GetField("isRowing", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fOarIsRowing != null && (bool)_fOarIsRowing.GetValue(oar);
            }
            catch { return false; }
        }

        // Runs just before vanilla EatFood. EatFood only consumes when the eat cooldown is clear;
        // if it will consume, the item is destroyed this call, so forward the consume first (while the
        // item still resolves). Client-only inside NotifyConsume; the host eats locally via vanilla.
        private static void PreEatFood(ShipItemFood __instance)
        {
            try
            {
                if (__instance == null) return;
                if (PlayerNeeds.instance != null && PlayerNeeds.instance.eatCooldown > 0f) return; // won't eat this call
                ItemSync.Instance?.NotifyConsume(__instance);
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PreEatFood: " + e.Message); }
        }

        // NailItem(item) is where a nail completes (item.nailed set true unless it bailed). Forward the
        // target's resulting nailed flag.
        private sealed class NailBefore { internal ShipItem Target; internal bool Nailed; }
        private static void PreNailItem(ShipItem __0, out NailBefore __state)
        {
            NailBefore state = null;
            SailwindCoop.Runtime.PatchGuard.Run(() => {
                if (__0 != null) state = new NailBefore { Target = __0, Nailed = __0.nailed };
            }, e => WarnPatch("[ItemPatches] PreNailItem: " + e));
            __state = state;
        }
        private static void PreHammerAltActivate(ShipItemHammer __instance, out NailBefore __state)
        {
            NailBefore state = null;
            SailwindCoop.Runtime.PatchGuard.Run(() => {
                var target = __instance != null && __instance.held != null ? __instance.held.GetPointedAtItem() : null;
                if (target != null) state = new NailBefore { Target = target, Nailed = target.nailed };
            }, e => WarnPatch("[ItemPatches] PreHammerAltActivate: " + e));
            __state = state;
        }
        private static void PostNailItem(NailBefore __state)
        {
            try { if (__state?.Target != null && __state.Target.nailed != __state.Nailed) ItemSync.Instance?.OnLocalNail(__state.Target); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostNailItem: " + e.Message); }
        }

        // OnAltActivate toggles an already-nailed target off (instant un-nail). Forward the pointed-at
        // Use the captured target; a no-op or a changed pointer must not invent another action.
        private static void PostHammerAltActivate(NailBefore __state) => PostNailItem(__state);

        // Рыба сорвалась (ReleaseFish) или шанс при CollectFish: держащий потерял крючок — форвардим.
        private static void PostDetachHook(ShipItemFishingRod __instance, HealthBefore __state)
        {
            try { if (__state.Captured && __instance != null && !__state.Value.Equals(__instance.health)) ItemSync.Instance?.OnLocalRodHook(__instance, attached: false, consumedHook: null); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostDetachHook: " + e.Message); }
        }

        // Attach крючка: ваниль в OnItemClick ставит health=1 и уничтожает крючок-предмет. Ловим
        // переход health 0→>0 (prefix запоминает старое значение) и форвардим + Consume за крючок.
        private struct HealthBefore { internal bool Captured; internal float Value; }

        private static void PreRodItemClick(ShipItemFishingRod __instance, out HealthBefore __state)
        {
            __state = default(HealthBefore);
            try { if (__instance != null) __state = new HealthBefore { Captured = true, Value = __instance.health }; }
            catch (Exception e) { WarnPatch("[ItemPatches] PreRodItemClick: " + e); }
        }

        private static void PostRodItemClick(ShipItemFishingRod __instance, PickupableItem __0, HealthBefore __state)
        {
            try
            {
                if (!__state.Captured || __instance == null || __state.Value > 0f || __instance.health <= 0f) return;
                var hook = __0 != null ? __0.GetComponent<ShipItem>() : null;
                ItemSync.Instance?.OnLocalRodHook(__instance, attached: true, consumedHook: hook);
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PostRodItemClick: " + e.Message); }
        }

        private static void PostLampHookItemClick(ShipItemLampHook __instance, PickupableItem __0, bool __result)
        {
            try
            {
                if (!__result || __0 == null || __0.GetComponent<HangableItem>() == null) return;
                ItemSync.Instance?.NotifyLampHook(__instance, __0);
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PostLampHookItemClick: " + e.Message); }
        }

        private static void PreBottleDrink(ShipItemBottle __instance, out BottleClickState __state)
        {
            __state = default(BottleClickState);
            try { if (__instance != null) __state = new BottleClickState {
                HasTarget = true, TargetAmount = __instance.amount, TargetHealth = __instance.health }; }
            catch (Exception e) { WarnPatch("[ItemPatches] PreBottleDrink: " + e); }
        }
        private static void PostBottleDrink(ShipItemBottle __instance, BottleClickState __state)
        {
            try { if (__state.HasTarget && __instance != null &&
                (!__state.TargetAmount.Equals(__instance.amount) || !__state.TargetHealth.Equals(__instance.health)))
                ItemSync.Instance?.NotifyItemStateChanged(__instance, "bottle-drink"); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostBottleDrink: " + e.Message); }
        }

        private static void PreFoldableAltActivate(ShipItemFoldable __instance, ref float __state)
        {
            float captured = 0f;
            SailwindCoop.Runtime.PatchGuard.Run(() => captured = FoldableState.Capture(__instance),
                e => WarnPatch("[ItemPatches] PreFoldableAltActivate: " + e.Message));
            __state = captured;
        }

        private static void PostFoldableAltActivate(ShipItemFoldable __instance, float __state)
        {
            SailwindCoop.Runtime.PatchGuard.Run(() => {
                if (__instance != null && FoldableState.Capture(__instance) != __state)
                    ItemSync.Instance?.NotifyItemStateChanged(__instance, "foldable-alt");
            }, e => WarnPatch("[ItemPatches] PostFoldableAltActivate: " + e.Message));
        }

        private static void PostBroomAltActivate(ShipItemBroom __instance)
        {
            try { ItemSync.Instance?.NotifyBroomActivated(__instance); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostBroomAltActivate: " + e.Message); }
        }

        private static ShipItemBottle BottleOf(PickupableItem item)
        {
            try { return item != null ? item.GetComponent<ShipItemBottle>() : null; }
            catch { return null; }
        }

        // FishingRodFish.CollectFish returns the freshly-instantiated fish ShipItem. Forward it so the
        // host authors the authoritative copy (client-only inside NotifyClientAuthored).
        private static void PostCollectFish(ShipItem __result)
        {
            try { if (__result != null) ItemSync.Instance?.NotifyClientAuthored(__result); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostCollectFish: " + e.Message); }
        }

        private sealed class MarketSpawnState
        {
            public int PrefabIndex;
            public Vector3 Pos;
            public HashSet<int> ExistingIds;
        }

        private static void PreMarketSpawnGood(IslandMarket __instance, GameObject goodPrefab, out MarketSpawnState __state)
        {
            __state = null;
            try
            {
                __state = new MarketSpawnState
                {
                    PrefabIndex = PatchPrefabIndex(goodPrefab),
                    Pos = __instance != null ? __instance.transform.position : Vector3.zero,
                    ExistingIds = new HashSet<int>(),
                };
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                {
                    int id = PatchInstanceId(item);
                    if (id > 0) __state.ExistingIds.Add(id);
                }
            }
            catch (Exception e) { __state = null; WarnPatch("[ItemPatches] PreMarketSpawnGood: " + e.Message); }
        }

        private static void PostMarketSpawnGood(MarketSpawnState __state)
        {
            try
            {
                if (__state == null || __state.PrefabIndex <= 0) return;
                ShipItem best = null;
                float bestSq = 25f;
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                {
                    if (item == null || !item.sold) continue;
                    int id = PatchInstanceId(item);
                    if (id <= 0 || __state.ExistingIds.Contains(id)) continue;
                    if (PatchPrefabIndex(item.gameObject) != __state.PrefabIndex) continue;
                    var good = item.GetComponent<Good>();
                    if (good == null || good.GetMissionIndex() != -1) continue;
                    float sq = (item.transform.position - __state.Pos).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = item;
                    }
                }

                if (best != null)
                {
                    ItemSync.Instance?.NotifyClientAuthored(best);
                    Plugin.Logger.LogInfo("[ItemPatches] Market buy sync prefab=" + __state.PrefabIndex +
                                          " id=" + PatchInstanceId(best) + " '" + best.name + "'");
                }
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PostMarketSpawnGood: " + e.Message); }
        }

        private static FieldInfo _fWarehouseGoodsInArea;
        private static MethodInfo _mWarehouseIsGoodValid;

        private static void PreWarehouseSellGood(IslandMarketWarehouseArea __instance, int goodIndex)
        {
            try
            {
                var good = FindWarehouseGood(__instance, goodIndex);
                if (good == null) return;
                var item = good.GetComponent<ShipItem>();
                var sv = good.GetComponent<SaveablePrefab>();
                if (item != null && sv != null)
                    ItemSync.Instance?.NotifySold(sv.instanceId, sv.prefabIndex);
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PreWarehouseSellGood: " + e.Message); }
        }

        private static Good FindWarehouseGood(IslandMarketWarehouseArea area, int goodIndex)
        {
            if (area == null) return null;
            if (_fWarehouseGoodsInArea == null)
                _fWarehouseGoodsInArea = typeof(IslandMarketWarehouseArea).GetField("goodsInArea", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_mWarehouseIsGoodValid == null)
                _mWarehouseIsGoodValid = typeof(IslandMarketWarehouseArea).GetMethod("IsGoodValid", BindingFlags.NonPublic | BindingFlags.Instance);
            var goods = _fWarehouseGoodsInArea != null ? _fWarehouseGoodsInArea.GetValue(area) as System.Collections.IEnumerable : null;
            if (goods == null) return null;
            foreach (var obj in goods)
            {
                var good = obj as Good;
                if (good == null) continue;
                if (PatchGoodIndex(good) != goodIndex) continue;
                bool valid = true;
                if (_mWarehouseIsGoodValid != null)
                    valid = (bool)_mWarehouseIsGoodValid.Invoke(area, new object[] { good });
                if (valid) return good;
            }
            return null;
        }

        private static int PatchPrefabIndex(GameObject go)
        {
            var sv = go != null ? go.GetComponent<SaveablePrefab>() : null;
            return sv != null ? sv.prefabIndex : 0;
        }

        private static int PatchInstanceId(ShipItem item)
        {
            var sv = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return sv != null ? sv.instanceId : 0;
        }

        private static int PatchGoodIndex(Good good)
        {
            var sv = good != null ? good.GetComponent<SaveablePrefab>() : null;
            return sv != null ? PrefabsDirectory.ItemToGoodIndex(sv.prefabIndex) : -1;
        }

        // Crate Insert/Withdraw set the item's currentCrateId; forward the membership change (skipped while
        // we're applying a remote one — ItemSync.ApplyingCrate guard).
        private static void PostCrateInsert(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCrate) ItemSync.Instance?.OnLocalCrate(__0); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostCrateInsert: " + e.Message); }
        }

        private static void PostCrateWithdraw(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCrate) ItemSync.Instance?.OnLocalCrate(__0); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostCrateWithdraw: " + e.Message); }
        }

        // UnsealCrate authors the contained items. On the client we don't author (phantoms); forward to the
        // host and skip vanilla. Host/offline runs vanilla normally.
        private static bool PreUnseal(ShipItemCrate __instance)
        {
            try
            {
                var sync = ItemSync.Instance;
                if (sync == null) return true;
                return !sync.ForwardUnseal(__instance);   // forwarded → skip vanilla; else run it
            }
            catch (Exception e) { WarnPatch("[ItemPatches] PreUnseal: " + e.Message); return true; }
        }

        // Cargo load/unload runs locally (own wallet); we only mirror the resulting membership.
        // After InsertItem the item's CargoPort is the carrier's port — forward it.
        private static void PostCargoInsert(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCargo) ItemSync.Instance?.OnLocalCargo(__0); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostCargoInsert: " + e.Message); }
        }

        // WithdrawItem(GoPointer, int index) doesn't take the item; capture it from carrier.cargo[index]
        // before vanilla removes it, then forward its new (out-of-carrier) membership afterwards.
        private static void PreCargoWithdraw(CargoCarrier __instance, int __1, out ShipItem __state)
        {
            __state = null;
            try
            {
                if (__instance != null && __instance.cargo != null && __1 >= 0 && __1 < __instance.cargo.Count)
                    __state = __instance.cargo[__1];
            }
            catch { __state = null; }
        }

        private static void PostCargoWithdraw(ShipItem __state)
        {
            try { if (__state != null && !ItemSync.ApplyingCargo) ItemSync.Instance?.OnLocalCargo(__state); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostCargoWithdraw: " + e.Message); }
        }

        private static void PostInventoryInsert(ShipItem __0)
        {
            try { ItemSync.Instance?.OnLocalInventory(__0); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostInventoryInsert: " + e.Message); }
        }

        private static void PreInventoryWithdraw(GPButtonInventorySlot __instance, out ShipItem __state)
        {
            __state = null;
            try { __state = __instance != null ? __instance.currentItem : null; }
            catch { __state = null; }
        }

        private static void PostInventoryWithdraw(ShipItem __state)
        {
            try { if (__state != null) ItemSync.Instance?.OnLocalInventory(__state); }
            catch (Exception e) { WarnPatch("[ItemPatches] PostInventoryWithdraw: " + e.Message); }
        }
    }
}
