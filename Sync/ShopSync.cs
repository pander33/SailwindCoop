using System;
using System.Reflection;
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

        /// <summary>A good is about to be sold locally (own wallet); have the host despawn its copy.</summary>
        public void OnSold(int instanceId, int prefabIndex)
        {
            ItemSync.Instance?.NotifySold(instanceId, prefabIndex);
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
    /// forward only the item consequence to the host. Buy = <c>Shopkeeper.TryToSellItem</c>; the postfix
    /// fires when the good actually became <c>sold</c>. Sell = <c>Shopkeeper.TryToBuyItem</c>; the prefix
    /// forwards the despawn before vanilla destroys the item (Destroy is deferred to end-of-frame, so the
    /// reference isn't reliably Unity-null in a postfix).
    /// </summary>
    public static class ShopPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool buy = TryPatch(harmony, "TryToSellItem", nameof(PreBuy), nameof(PostBuy));
            bool sell = TryPatch(harmony, "TryToBuyItem", nameof(PreSell), null);
            Plugin.Logger.LogInfo("[ShopPatches] Shop patches (local money): buy(TryToSellItem)=" + buy + ", sell(TryToBuyItem)=" + sell);
            SailwindCoop.Runtime.PatchHealth.Report("Shop", (buy ? 1 : 0) + (sell ? 1 : 0), 2);
        }

        private static bool TryPatch(Harmony harmony, string method, string prefixName, string postfixName)
        {
            try
            {
                var mi = typeof(Shopkeeper).GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(ShipItem) }, null);
                if (mi == null) return false;
                var prefix = prefixName == null ? null : new HarmonyMethod(typeof(ShopPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = postfixName == null ? null : new HarmonyMethod(typeof(ShopPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ShopPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private sealed class BuyState
        {
            public bool WasSold;
            public bool HasPlace;
            public Vector3 Place;
            public int PrefabIndex;
        }

        // Buy: record whether the good was already sold so the postfix can detect a fresh purchase,
        // and its shelf place, which the purchase forgets.
        private static void PreBuy(ShipItem item, out BuyState __state)
        {
            __state = new BuyState { WasSold = true };
            try
            {
                if (item == null) return;
                __state.WasSold = item.sold;
                var prefab = item.GetComponent<SaveablePrefab>();
                if (prefab == null) return;
                __state.PrefabIndex = prefab.prefabIndex;
                __state.HasPlace = ShopSync.ShelfPlace(item, out __state.Place);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PreBuy: " + e.Message); }
        }

        private static void PostBuy(ShipItem item, BuyState __state)
        {
            try
            {
                if (item == null || !item.sold || __state == null || __state.WasSold) return;
                ShopSync.Instance?.OnBought(item);
                if (__state.HasPlace) ShopSync.Instance?.OnShelfBought(__state.PrefabIndex, __state.Place);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PostBuy: " + e.Message); }
        }

        // Sell: forward the despawn before vanilla destroys the item. TryToBuyItem only bails when the
        // item is a crate that still has contents — replicate that check so we don't despawn a no-op.
        private static void PreSell(ShipItem item)
        {
            try
            {
                if (item == null) return;
                var inv = item.GetComponent<CrateInventory>();
                if (inv != null && inv.containedItems != null && inv.containedItems.Count > 0) return;
                var sv = item.GetComponent<SaveablePrefab>();
                if (sv != null) ShopSync.Instance?.OnSold(sv.instanceId, sv.prefabIndex);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PreSell: " + e.Message); }
        }
    }
}
