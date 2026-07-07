using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;

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

        // Buy: record whether the good was already sold so the postfix can detect a fresh purchase.
        private static void PreBuy(ShipItem item, out bool __state)
        {
            __state = item != null && item.sold;
        }

        private static void PostBuy(ShipItem item, bool __state)
        {
            try
            {
                if (item != null && item.sold && !__state) ShopSync.Instance?.OnBought(item);
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
