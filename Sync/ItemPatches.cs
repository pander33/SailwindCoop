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
            bool pickup = TryPatch(harmony, typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) }, postfixName: nameof(PostPickup));
            bool drop = TryPatch(harmony, typeof(GoPointer), "DropItem", Type.EmptyTypes, prefixName: nameof(PreDrop), postfixName: nameof(PostDrop));
            bool bottleClick = TryPatch(harmony, typeof(ShipItemBottle), "OnItemClick", new[] { typeof(PickupableItem) },
                prefixName: nameof(PreBottleItemClick), postfixName: nameof(PostBottleItemClick));
            bool oarHeld = TryPatch(harmony, typeof(ShipItemOar), "OnAltHeld", Type.EmptyTypes, postfixName: nameof(PostOarAltHeld));
            // Eating food is not an OnAltHeld replay (OnAltHeld only sets a flag; EatFood does the consume
            // + DestroyItem and touches the eater's personal PlayerNeeds). Forward the actual consume so
            // the host destroys its copy without running its own PlayerNeeds.
            bool eat = TryPatch(harmony, typeof(ShipItemFood), "EatFood", Type.EmptyTypes, prefixName: nameof(PreEatFood));
            // Hammer nailing targets the item the LOCAL pointer aims at; the held-action replay can't
            // reproduce that aim, so we sync the result (target.nailed) from the two sites that change it:
            // NailItem (nail completes after the 2s hold) and OnAltActivate (instant un-nail).
            bool nail = TryPatch(harmony, typeof(ShipItemHammer), "NailItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostNailItem));
            bool unnail = TryPatch(harmony, typeof(ShipItemHammer), "OnAltActivate", Type.EmptyTypes, postfixName: nameof(PostHammerAltActivate));
            // A caught fish is created on the client by FishingRodFish.CollectFish (returns the new item);
            // forward it so the host authors the authoritative copy (client item replication is host-only).
            bool fish = TryPatch(harmony, typeof(FishingRodFish), "CollectFish", Type.EmptyTypes, postfixName: nameof(PostCollectFish));
            // Крючок удочки: наличие = rod.health; ставится/теряется только на машине держащего
            // (OnItemClick attach / DetachHook при сходе рыбы) — форвардим результат, как nail.
            bool rodDetach = TryPatch(harmony, typeof(ShipItemFishingRod), "DetachHook", Type.EmptyTypes, postfixName: nameof(PostDetachHook));
            bool rodAttach = TryPatch(harmony, typeof(ShipItemFishingRod), "OnItemClick", new[] { typeof(PickupableItem) },
                prefixName: nameof(PreRodItemClick), postfixName: nameof(PostRodItemClick));
            bool lampHook = TryPatch(harmony, typeof(ShipItemLampHook), "OnItemClick", new[] { typeof(PickupableItem) },
                postfixName: nameof(PostLampHookItemClick));
            // Crates: mirror inventory membership (Insert/Withdraw) and relay unseal (item creation) to host.
            bool crateIn = TryPatch(harmony, typeof(CrateInventory), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCrateInsert));
            bool crateOut = TryPatch(harmony, typeof(CrateInventory), "WithdrawItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCrateWithdraw));
            bool unseal = TryPatch(harmony, typeof(ShipItemCrate), "UnsealCrate", Type.EmptyTypes, prefixName: nameof(PreUnseal));
            // Cargo load/unload uses each player's OWN wallet (local money) → vanilla runs locally; we only
            // mirror the resulting membership (like crates). Postfix on insert; withdraw captures the item
            // in a prefix (it isn't an argument) and forwards it in the postfix.
            bool cargoIn = TryPatch(harmony, typeof(CargoCarrier), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostCargoInsert));
            bool cargoOut = TryPatch(harmony, typeof(CargoCarrier), "WithdrawItem", new[] { typeof(GoPointer), typeof(int) }, prefixName: nameof(PreCargoWithdraw), postfixName: nameof(PostCargoWithdraw));
            bool invIn = TryPatch(harmony, typeof(GPButtonInventorySlot), "InsertItem", new[] { typeof(ShipItem) }, postfixName: nameof(PostInventoryInsert));
            bool invOut = TryPatch(harmony, typeof(GPButtonInventorySlot), "WithdrawItem", Type.EmptyTypes, prefixName: nameof(PreInventoryWithdraw), postfixName: nameof(PostInventoryWithdraw));
            bool marketBuy = TryPatch(harmony, typeof(IslandMarket), "SpawnGood", new[] { typeof(GameObject) },
                prefixName: nameof(PreMarketSpawnGood), postfixName: nameof(PostMarketSpawnGood));
            bool marketSell = TryPatch(harmony, typeof(IslandMarketWarehouseArea), "SellGood", new[] { typeof(int) },
                prefixName: nameof(PreWarehouseSellGood));
            Plugin.Logger.LogInfo("[ItemPatches] Item patches: pickup=" + pickup + ", drop=" + drop +
                                  ", bottleClick=" + bottleClick + ", oarHeld=" + oarHeld + ", eat=" + eat +
                                  ", nail=" + nail + ", unnail=" + unnail + ", fish=" + fish +
                                  ", rodDetach=" + rodDetach + ", rodAttach=" + rodAttach + ", lampHook=" + lampHook +
                                  ", crateIn=" + crateIn + ", crateOut=" + crateOut + ", unseal=" + unseal +
                                  ", cargoIn=" + cargoIn + ", cargoOut=" + cargoOut +
                                  ", invIn=" + invIn + ", invOut=" + invOut +
                                  ", marketBuy=" + marketBuy + ", marketSell=" + marketSell);

            // Held alt-actions on ShipItem and its subclasses: a client holding the item triggers an
            // authoritative effect (hammer nail/repair, oar rowing, eat/drink). We forward these so the
            // host replays them on its copy. Patch the (GoPointer) overloads across the hierarchy so
            // subclassed handlers are caught (the no-arg variants the game also calls are left alone to
            // avoid double-forwarding).
            int held = PatchShipItem(harmony, "OnAltHeld", nameof(PostAltHeld));
            int alt = PatchShipItem(harmony, "OnAltActivate", nameof(PostAltActivate));
            bool bottleDrink = TryPatch(harmony, typeof(ShipItemBottle), "Drink", Type.EmptyTypes,
                postfixName: nameof(PostBottleDrink));
            bool foldable = TryPatch(harmony, typeof(ShipItemFoldable), "OnAltActivate", Type.EmptyTypes,
                postfixName: nameof(PostFoldableAltActivate));
            bool broom = TryPatch(harmony, typeof(ShipItemBroom), "OnAltActivate", Type.EmptyTypes,
                postfixName: nameof(PostBroomAltActivate));
            Plugin.Logger.LogInfo("[ItemPatches] Held alt-actions: OnAltHeld=" + held + ", OnAltActivate=" + alt);
            Plugin.Logger.LogInfo("[ItemPatches] Extra item patches: surfacePlace=DropItem, bottleDrink=" + bottleDrink + ", foldable=" + foldable +
                                  ", broom=" + broom);

            int ok = 0;
            foreach (bool patched in new[]
            {
                pickup, drop, bottleClick, oarHeld, eat, nail, unnail, fish, rodDetach, rodAttach,
                lampHook, crateIn, crateOut, unseal, cargoIn, cargoOut, invIn, invOut, marketBuy, marketSell
            })
                if (patched) ok++;
            if (held > 0) ok++;
            if (alt > 0) ok++;
            if (bottleDrink) ok++;
            if (foldable) ok++;
            if (broom) ok++;
            SailwindCoop.Runtime.PatchHealth.Report("Items", ok, 24, ok + "/24, held=" + held + ", alt=" + alt);
        }

        private static int PatchShipItem(Harmony harmony, string gameMethod, string postfixName)
        {
            var postfix = new HarmonyMethod(typeof(ItemPatches).GetMethod(
                postfixName, BindingFlags.Static | BindingFlags.NonPublic));
            var args = new[] { typeof(GoPointer) };
            int patched = 0;
            var baseType = typeof(ShipItem);
            foreach (var t in baseType.Assembly.GetTypes())
            {
                if (!baseType.IsAssignableFrom(t)) continue;
                MethodInfo mi;
                try
                {
                    mi = t.GetMethod(gameMethod,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, args, null);
                }
                catch { continue; }
                if (mi == null || mi.IsAbstract) continue;
                try { harmony.Patch(mi, postfix: postfix); patched++; }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[ItemPatches] Failed to patch " + t.Name + "." + gameMethod + ": " + e.Message);
                }
            }
            return patched;
        }

        private static bool TryPatch(Harmony harmony, Type type, string method, Type[] args, string prefixName = null, string postfixName = null)
        {
            try
            {
                var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null) return false;
                HarmonyMethod prefix = prefixName == null ? null : new HarmonyMethod(typeof(ItemPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod postfix = postfixName == null ? null : new HarmonyMethod(typeof(ItemPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ItemPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static void PostPickup(GoPointer __instance, PickupableItem item)
        {
            try { ItemSync.Instance?.NotifyPickup(__instance, item); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostPickup: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostDrop: " + e.Message); }
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
                Plugin.Logger.LogWarning("[ItemPatches] PreBottleItemClick: " + e.Message);
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
                Plugin.Logger.LogWarning("[ItemPatches] PostBottleItemClick: " + e.Message);
            }
        }

        private static void PostAltHeld(GoPointerButton __instance)
        {
            try
            {
                if (__instance is ShipItemOar) return; // handled by the no-arg oar hook after vanilla sets isRowing
                ItemSync.Instance?.NotifyAltHeld(__instance as ShipItem);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostAltHeld: " + e.Message); }
        }

        private static void PostOarAltHeld(ShipItemOar __instance)
        {
            try
            {
                if (!OarIsRowing(__instance)) return;
                ItemSync.Instance?.NotifyAltHeld(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostOarAltHeld: " + e.Message); }
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

        private static void PostAltActivate(GoPointerButton __instance)
        {
            try { ItemSync.Instance?.NotifyAltActivate(__instance as ShipItem); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostAltActivate: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PreEatFood: " + e.Message); }
        }

        // NailItem(item) is where a nail completes (item.nailed set true unless it bailed). Forward the
        // target's resulting nailed flag.
        private static void PostNailItem(ShipItem __0)
        {
            try { if (__0 != null) ItemSync.Instance?.OnLocalNail(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostNailItem: " + e.Message); }
        }

        // OnAltActivate toggles an already-nailed target off (instant un-nail). Forward the pointed-at
        // item's nailed flag; harmless if nothing changed (idempotent on the host).
        private static void PostHammerAltActivate(ShipItemHammer __instance)
        {
            try
            {
                var target = __instance != null && __instance.held != null ? __instance.held.GetPointedAtItem() : null;
                if (target != null) ItemSync.Instance?.OnLocalNail(target);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostHammerAltActivate: " + e.Message); }
        }

        // Рыба сорвалась (ReleaseFish) или шанс при CollectFish: держащий потерял крючок — форвардим.
        private static void PostDetachHook(ShipItemFishingRod __instance)
        {
            try { if (__instance != null) ItemSync.Instance?.OnLocalRodHook(__instance, attached: false, consumedHook: null); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostDetachHook: " + e.Message); }
        }

        // Attach крючка: ваниль в OnItemClick ставит health=1 и уничтожает крючок-предмет. Ловим
        // переход health 0→>0 (prefix запоминает старое значение) и форвардим + Consume за крючок.
        private static float _rodPreClickHealth;

        private static void PreRodItemClick(ShipItemFishingRod __instance)
        {
            try { _rodPreClickHealth = __instance != null ? __instance.health : 1f; }
            catch { _rodPreClickHealth = 1f; }
        }

        private static void PostRodItemClick(ShipItemFishingRod __instance, PickupableItem __0)
        {
            try
            {
                if (__instance == null || _rodPreClickHealth > 0f || __instance.health <= 0f) return;
                var hook = __0 != null ? __0.GetComponent<ShipItem>() : null;
                ItemSync.Instance?.OnLocalRodHook(__instance, attached: true, consumedHook: hook);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostRodItemClick: " + e.Message); }
        }

        private static void PostLampHookItemClick(ShipItemLampHook __instance, PickupableItem __0, bool __result)
        {
            try
            {
                if (!__result || __0 == null || __0.GetComponent<HangableItem>() == null) return;
                ItemSync.Instance?.NotifyLampHook(__instance, __0);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostLampHookItemClick: " + e.Message); }
        }

        private static void PostBottleDrink(ShipItemBottle __instance)
        {
            try { ItemSync.Instance?.NotifyItemStateChanged(__instance, "bottle-drink"); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostBottleDrink: " + e.Message); }
        }

        private static void PostFoldableAltActivate(ShipItemFoldable __instance)
        {
            try { ItemSync.Instance?.NotifyItemStateChanged(__instance, "foldable-alt"); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostFoldableAltActivate: " + e.Message); }
        }

        private static void PostBroomAltActivate(ShipItemBroom __instance)
        {
            try { ItemSync.Instance?.NotifyBroomActivated(__instance); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostBroomAltActivate: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCollectFish: " + e.Message); }
        }

        private sealed class MarketSpawnState
        {
            public int PrefabIndex;
            public Vector3 Pos;
            public HashSet<int> ExistingIds;
        }

        private static void PreMarketSpawnGood(IslandMarket __instance, GameObject goodPrefab, out MarketSpawnState __state)
        {
            __state = new MarketSpawnState
            {
                PrefabIndex = PatchPrefabIndex(goodPrefab),
                Pos = __instance != null ? __instance.transform.position : Vector3.zero,
                ExistingIds = new HashSet<int>(),
            };
            try
            {
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
                {
                    int id = PatchInstanceId(item);
                    if (id > 0) __state.ExistingIds.Add(id);
                }
            }
            catch { }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostMarketSpawnGood: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PreWarehouseSellGood: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCrateInsert: " + e.Message); }
        }

        private static void PostCrateWithdraw(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCrate) ItemSync.Instance?.OnLocalCrate(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCrateWithdraw: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PreUnseal: " + e.Message); return true; }
        }

        // Cargo load/unload runs locally (own wallet); we only mirror the resulting membership.
        // After InsertItem the item's CargoPort is the carrier's port — forward it.
        private static void PostCargoInsert(ShipItem __0)
        {
            try { if (!ItemSync.ApplyingCargo) ItemSync.Instance?.OnLocalCargo(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCargoInsert: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostCargoWithdraw: " + e.Message); }
        }

        private static void PostInventoryInsert(ShipItem __0)
        {
            try { ItemSync.Instance?.OnLocalInventory(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostInventoryInsert: " + e.Message); }
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
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemPatches] PostInventoryWithdraw: " + e.Message); }
        }
    }
}
