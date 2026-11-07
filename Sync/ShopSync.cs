using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Local-money shop economy. Each player buys/sells against their OWN <c>PlayerGold</c> wallet
    /// (the "separate money" co-op model), so the transaction is NOT mediated over the network — the
    /// client runs the vanilla buy/sell locally. Only the physical item is shared:
    /// <list type="bullet">
    ///   <item>A bought good (vanilla <c>Sell()</c> → sold + picked up) is a client-authored runtime item;
    ///   <see cref="ItemSync.NotifyClientAuthored"/> asks the host to author the shared twin.</item>
    ///   <item>A sold good is destroyed locally; <see cref="ItemSync.NotifySold"/> tells the host to
    ///   despawn its authoritative copy so it disappears for everyone.</item>
    /// </list>
    /// This class is just the thin bridge that <see cref="ShopPatches"/> calls; it holds the overlay text.
    /// </summary>
    public sealed class ShopSync
    {
        public static ShopSync Instance { get; private set; }

        private readonly CoopNet _net;
        private string _last = "—";

        public string ShopText => _last;

        public ShopSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>A good was just bought locally (own wallet); make it a shared host-authored item.</summary>
        public void OnBought(ShipItem item)
        {
            if (item == null) return;
            ItemSync.Instance?.NotifyClientAuthored(item);
            Remember("bought '" + item.name + "'");
        }

        /// <summary>A sale destroyed a local good after paying the seller. Despawn the host's copy.</summary>
        public void OnSold(int instanceId, int prefabIndex) => OnSold(null, instanceId, prefabIndex);

        internal void OnSold(ShipItem item, int instanceId, int prefabIndex)
        {
            ItemSync.Instance?.NotifySold(item, instanceId, prefabIndex);
            Remember("sold id=" + instanceId);
        }

        // -----------------------------------------------------------------
        // Shelf stock. Each machine builds a shop's stock for itself, so an item bought on one machine
        // would stay for sale on the others, next to the bought one.
        // -----------------------------------------------------------------

        /// <summary>
        /// How far apart the same shelf place may be measured on two machines. Kept below the gap
        /// between neighbouring items of one kind (about 0.2 m on a crowded shelf): when the right
        /// item is already gone here, its neighbour must not be taken instead.
        /// </summary>
        private const float ShelfTolerance = 0.15f;

        private static readonly FieldInfo _fShopPos = typeof(ShipItem).GetField("shopPos", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fShopArea = typeof(ShipItem).GetField("shopArea", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// The shelf place of an item that is for sale, in real space. The place, not where the item is
        /// now: a player may be carrying an unsold item around the shop.
        /// </summary>
        public static bool ShelfPlace(ShipItem item, out Vector3 real)
        {
            real = Vector3.zero;
            try
            {
                if (item == null || _fShopPos == null || _fShopArea == null) return false;
                var area = _fShopArea.GetValue(item) as ShopArea;
                if (area == null) return false;
                real = CoordSpace.LocalToReal(area.transform.TransformPoint((Vector3)_fShopPos.GetValue(item)));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ShopSync] shelf place unavailable: " + e.Message);
                return false;
            }
        }

        /// <summary>The local player bought the item of this shelf place: the others take theirs off sale.</summary>
        public void OnShelfBought(int prefabIndex, Vector3 realPlace)
        {
            if (_net.Role == Role.None || _net.State != LinkState.Connected) return;
            _net.Broadcast(new ShopTakenMsg { PrefabIndex = prefabIndex, Pos = realPlace }, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void OnShopTaken(ShopTakenMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            try
            {
                if (_net.Role == Role.Host) _net.RelayExcept(msg, fromPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
                ShipItem match = null;
                ShopArea matchArea = null;
                float best = ShelfTolerance;
                foreach (ShopArea area in UnityEngine.Object.FindObjectsOfType<ShopArea>())
                {
                    if (area.itemsForSale == null) continue;
                    foreach (ShipItem item in area.itemsForSale)
                    {
                        if (item == null || item.sold) continue;
                        var prefab = item.GetComponent<SaveablePrefab>();
                        if (prefab == null || prefab.prefabIndex != msg.PrefabIndex) continue;
                        Vector3 place;
                        if (!ShelfPlace(item, out place)) continue;
                        float d = Vector3.Distance(place, msg.Pos);
                        if (d >= best) continue;
                        best = d;
                        match = item;
                        matchArea = area;
                    }
                }
                if (match == null)
                {
                    // The island is not loaded here, or the place is already empty: nothing to take off.
                    Plugin.Logger.LogInfo("[ShopSync] role=" + _net.Role + " shelf place not found prefab=" + msg.PrefabIndex);
                    return;
                }
                matchArea.itemsForSale.Remove(match);
                // This player may be looking the item over in hand: let go of it before it disappears,
                // or the hand keeps a destroyed object.
                if (match.held != null)
                {
                    try { match.held.DropItem(); }
                    catch (Exception e) { Plugin.Logger.LogWarning("[ShopSync] drop before removal: " + e.Message); }
                }
                Remember("shelf item taken off sale '" + match.name + "' prefab=" + msg.PrefabIndex);
                UnityEngine.Object.Destroy(match.gameObject);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopSync] OnShopTaken: " + e.Message); }
        }

        public void Clear() { _last = "—"; }

        private void Remember(string text)
        {
            _last = text;
            Plugin.Logger.LogInfo("[ShopSync] " + text);
        }
    }

    /// <summary>
    /// Harmony bridge: let the vanilla shop run locally (so each player pays from their own wallet) and
    /// forward only the item consequence to the host. Buy = <c>Shopkeeper.TryToSellItem</c>; the
    /// finalizer also reports a paid purchase if later UI work throws. Sell = <c>Shopkeeper.TryToBuyItem</c>; only a
    /// completed <c>DestroyItem</c> for the offered item inside that call sends a despawn.
    /// </summary>
    public static class ShopPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool buy = TryPatch(harmony, "TryToSellItem", nameof(PreBuy), nameof(FinishBuy));
            bool payment = buy && PatchPurchaseDebit(harmony, typeof(Shopkeeper), "SellItem",
                new[] { typeof(ShipItem), typeof(int), typeof(int) });
            bool sell = PatchDestroy(harmony) &&
                        TryPatch(harmony, "TryToBuyItem", nameof(PreSell), nameof(FinishSell));
            Plugin.Logger.LogInfo("[ShopPatches] Shop patches: buy=" + buy + ", payment=" + payment + ", sell=" + sell);
            SailwindCoop.Runtime.PatchHealth.Report("Shop", (buy ? 1 : 0) + (payment ? 1 : 0) + (sell ? 1 : 0), 3);
        }

        private static HarmonyMethod Hook(string name, int priority = Priority.Normal) => name == null ? null :
            new HarmonyMethod(typeof(ShopPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        private static bool TryPatch(Harmony harmony, string method, string prefixName, string finalizerName)
        {
            try
            {
                var mi = typeof(Shopkeeper).GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(ShipItem) }, null);
                if (mi == null) return false;
                // Economy Overhaul performs the transaction in its normal-priority prefix.
                harmony.Patch(mi, prefix: Hook(prefixName, Priority.First), finalizer: Hook(finalizerName));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ShopPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static bool PatchDestroy(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(ShipItem), nameof(ShipItem.DestroyItem), Type.EmptyTypes);
                if (target == null) return false;
                harmony.Patch(target, postfix: Hook(nameof(PostDestroy), Priority.First));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ShopPatches] DestroyItem: " + e.Message);
                return false;
            }
        }

        // Harmony runs the transpiler synchronously during Patch. Track this installation
        // separately from any later rebuild of the method by Harmony.
        [ThreadStatic] private static MethodBase _pendingDebitMarker;

        internal static bool PatchPurchaseDebit(Harmony harmony, Type type, string name, Type[] args)
        {
            var previousProbe = _pendingDebitMarker;
            try
            {
                var target = type != null ? AccessTools.Method(type, name, args) : null;
                if (target == null) throw new MissingMethodException(type?.FullName ?? "FxShops", name);
                _pendingDebitMarker = target;
                harmony.Patch(target, transpiler: Hook(nameof(AfterPurchaseDebit)));
                return _pendingDebitMarker == null;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ShopPatches] " + name + " payment hook: " + e.Message);
                return false;
            }
            finally { _pendingDebitMarker = previousProbe; }
        }

        private static IEnumerable<CodeInstruction> AfterPurchaseDebit(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            var sell = AccessTools.Method(typeof(ShipItem), nameof(ShipItem.Sell), Type.EmptyTypes);
            var wallet = AccessTools.Field(typeof(PlayerGold), nameof(PlayerGold.currency));
            int debit = -1;
            // Both shops call item.Sell(), then do PlayerGold.currency[currency] -= price.
            // Mark the write itself so a zero-price purchase counts too.
            // On a mismatch keep the input IL: Harmony may run this transpiler again when another mod patches the method.
            for (int i = 0; i + 8 < code.Count; i++)
            {
                if (!code[i].Calls(sell) || code[i + 1].opcode != OpCodes.Ldsfld || !Equals(code[i + 1].operand, wallet) ||
                    code[i + 3].opcode != OpCodes.Ldelema || !Equals(code[i + 3].operand, typeof(int)) ||
                    code[i + 4].opcode != OpCodes.Dup || code[i + 5].opcode != OpCodes.Ldind_I4 ||
                    code[i + 7].opcode != OpCodes.Sub || code[i + 8].opcode != OpCodes.Stind_I4) continue;
                if (debit >= 0)
                {
                    Plugin.Logger.LogWarning("[ShopPatches] " + original + ": more than one shop payment site");
                    return code;
                }
                debit = i + 8;
            }
            if (debit < 0)
            {
                Plugin.Logger.LogWarning("[ShopPatches] " + original + ": shop payment after ShipItem.Sell was not found");
                return code;
            }
            // item is IL argument 1 in both the instance method and EO's static (shop, item, ...) method.
            code.Insert(debit + 1, new CodeInstruction(OpCodes.Ldarg_1));
            code.Insert(debit + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShopPatches), nameof(MarkPurchasePaid))));
            if (Equals(_pendingDebitMarker, original)) _pendingDebitMarker = null;
            return code;
        }

        private sealed class BuyState
        {
            internal BuyState Previous;
            internal ShipItem Item;
            internal bool Paid, HasPlace;
            internal Vector3 Place;
            internal int PrefabIndex;
        }

        private static BuyState _buy;

        private static void PreBuy(ShipItem item, out BuyState __state)
        {
            __state = null;
            try
            {
                if (item == null || item.sold) return;
                var state = new BuyState { Previous = _buy, Item = item };
                var prefab = item.GetComponent<SaveablePrefab>();
                if (prefab != null)
                {
                    state.PrefabIndex = prefab.prefabIndex;
                    state.HasPlace = ShopSync.ShelfPlace(item, out state.Place);
                }
                __state = _buy = state;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PreBuy: " + e.Message); }
        }

        private static void MarkPurchasePaid(ShipItem item)
        {
            if (_buy != null && ReferenceEquals(_buy.Item, item)) _buy.Paid = true;
        }

        private static void FinishBuy(BuyState __state, Exception __exception)
        {
            if (__state == null || _buy != __state) return;
            _buy = __state.Previous;
            try
            {
                var item = __state.Item;
                if (item == null || !item.sold || (__exception != null && !__state.Paid)) return;
                ShopSync.Instance?.OnBought(item);
                if (__state.HasPlace) ShopSync.Instance?.OnShelfBought(__state.PrefabIndex, __state.Place);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] FinishBuy: " + e.Message); }
        }

        private sealed class Sale
        {
            internal Sale Previous;
            internal ShipItem Item;
            internal int InstanceId, PrefabIndex;
            internal bool Destroyed;
        }

        private static Sale _sale;

        private static void PreSell(ShipItem item, out Sale __state)
        {
            __state = null;
            try
            {
                if (item == null || ItemSync.Instance?.IsClientSession != true) return;
                // DestroyItem unregisters the item, so retain its identity before the transaction.
                var save = item.GetComponent<SaveablePrefab>();
                if (save == null) return;
                __state = _sale = new Sale
                {
                    Previous = _sale, Item = item, InstanceId = save.instanceId, PrefabIndex = save.prefabIndex
                };
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PreSell: " + e.Message); }
        }

        private static void PostDestroy(ShipItem __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            try { ItemSync.Instance?.MarkRemoteConsume(__instance); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] remote consume: " + e.Message); }
            for (var sale = _sale; sale != null; sale = sale.Previous)
            {
                if (!ReferenceEquals(sale.Item, __instance)) continue;
                sale.Destroyed = true;
                return;
            }
        }

        private static void FinishSell(Sale __state)
        {
            if (__state == null || _sale != __state) return;
            _sale = __state.Previous;
            try
            {
                if (__state.Destroyed) ShopSync.Instance?.OnSold(__state.Item, __state.InstanceId, __state.PrefabIndex);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] FinishSell: " + e.Message); }
        }
    }
}
