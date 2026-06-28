using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Phase 1 — host-authoritative shop economy (buy/sell). The wallet
    /// (<c>PlayerGold.currency</c>) lives on the host (host-world model), so the client never
    /// runs the local transaction: <see cref="ShopPatches"/> intercepts the two vanilla choke
    /// points (<c>Shopkeeper.TryToSellItem</c> = buy, <c>TryToBuyItem</c> = sell), blocks the
    /// client's local execution, and forwards a <see cref="ShopRequestMsg"/>. The host replays
    /// the same vanilla method authoritatively under a guard and the result replicates through
    /// the normal item channel: a bought item is handed to the client (ItemSync puppet), a sold
    /// item is destroyed (ItemSync despawn). Mirrors the method-relay pattern (R11–R16).
    /// </summary>
    public sealed class ShopSync
    {
        public static ShopSync Instance { get; private set; }

        /// <summary>True while the host is replaying a client request, so the patch lets vanilla run.</summary>
        internal static bool Replaying;

        private readonly CoopNet _net;

        // Client: remember the last buy so we can adopt the bought item when the host confirms.
        private int _pendingBuyPrefab;
        private Vector3 _pendingBuyRealPos;

        private string _last = "—";

        public const float MatchRadius = 2.5f;

        public string ShopText => _last;

        public ShopSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        // -----------------------------------------------------------------
        // Client: a local buy/sell was intercepted — forward, skip vanilla.
        // Returns true to let vanilla run (host / replaying / offline), false to skip.
        // -----------------------------------------------------------------

        public bool OnLocalBuy(Shopkeeper keeper, ShipItem item)
        {
            if (Replaying || _net.State != LinkState.Connected) return true;
            if (_net.Role == Role.Host) return true;   // host buys authoritatively in-place
            if (keeper == null || item == null) return true;

            ushort ki = ShopLocator.IndexOf(keeper);
            _pendingBuyPrefab = PrefabIndexOf(item);
            _pendingBuyRealPos = CoordSpace.Ready ? CoordSpace.LocalToReal(item.transform.position) : item.transform.position;
            _net.Broadcast(new ShopRequestMsg
            {
                Kind = ShopKind.Buy,
                KeeperIndex = ki,
                PrefabIndex = _pendingBuyPrefab,
                RealPos = _pendingBuyRealPos,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("исх buy keeper=" + ki + " prefab=" + _pendingBuyPrefab);
            return false;
        }

        public bool OnLocalSell(Shopkeeper keeper, ShipItem item)
        {
            if (Replaying || _net.State != LinkState.Connected) return true;
            if (_net.Role == Role.Host) return true;
            if (keeper == null || item == null) return true;

            ushort ki = ShopLocator.IndexOf(keeper);
            int id = InstanceIdOf(item);
            _net.Broadcast(new ShopRequestMsg
            {
                Kind = ShopKind.Sell,
                KeeperIndex = ki,
                InstanceId = id,
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("исх sell keeper=" + ki + " id=" + id);
            return false;
        }

        // -----------------------------------------------------------------
        // Host: apply a client request authoritatively.
        // -----------------------------------------------------------------

        public void OnShopRequest(ShopRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(fromPeer);
            if (actor == 0) return;

            var keeper = ShopLocator.FindByIndex(msg.KeeperIndex);
            if (keeper == null)
            {
                Reply(fromPeer, msg.Kind, false, ShopFailReason.ShopNotFound, 0);
                Remember("отказ keeper=" + msg.KeeperIndex);
                return;
            }

            if (msg.Kind == ShopKind.Buy)
            {
                var item = ItemSync.FindUnsoldNear(msg.PrefabIndex, msg.RealPos, MatchRadius);
                if (item == null)
                {
                    Reply(fromPeer, msg.Kind, false, ShopFailReason.ItemNotFound, 0);
                    Remember("отказ buy: нет товара prefab=" + msg.PrefabIndex);
                    return;
                }

                Replaying = true;
                try { keeper.TryToSellItem(item); }
                catch (Exception e) { Plugin.Logger.LogWarning("[ShopSync] TryToSellItem: " + e.Message); }
                finally { Replaying = false; }

                if (item.sold && ItemSync.Instance != null &&
                    ItemSync.Instance.HostAssignBought(item, actor, out int boughtId))
                {
                    Reply(fromPeer, msg.Kind, true, ShopFailReason.None, boughtId);
                    Remember("buy ok actor=" + actor + " id=" + boughtId);
                }
                else
                {
                    Reply(fromPeer, msg.Kind, false, ShopFailReason.NotEnoughMoney, 0);
                    Remember("buy отказ (деньги?) actor=" + actor);
                }
            }
            else // Sell
            {
                var item = ItemSync.Instance != null ? ItemSync.Instance.HostFindById(msg.InstanceId, 0) : null;
                if (item == null)
                {
                    // prefab unknown here; HostFindById ignores prefab when 0
                    Reply(fromPeer, msg.Kind, false, ShopFailReason.ItemNotFound, 0);
                    Remember("отказ sell: нет item id=" + msg.InstanceId);
                    return;
                }

                Replaying = true;
                try { keeper.TryToBuyItem(item); }
                catch (Exception e) { Plugin.Logger.LogWarning("[ShopSync] TryToBuyItem: " + e.Message); }
                finally { Replaying = false; }

                // Success = the item was destroyed (Unity-null after BuyItem.DestroyItem).
                // ItemSync's despawn diff then removes the item on clients.
                bool sold = item == null;
                Reply(fromPeer, msg.Kind, sold, sold ? ShopFailReason.None : ShopFailReason.ItemNotFound, 0);
                Remember("sell " + (sold ? "ok" : "отказ") + " actor=" + actor + " id=" + msg.InstanceId);
            }
        }

        // -----------------------------------------------------------------
        // Client: react to the host's outcome.
        // -----------------------------------------------------------------

        public void OnShopResult(ShopResultMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;

            if (!msg.Ok)
            {
                ShowNotification(msg.Reason == ShopFailReason.NotEnoughMoney ? "Not enough money" : "Cannot complete trade");
                Remember("вх " + msg.Kind + " отказ " + msg.Reason);
                return;
            }

            if (msg.Kind == ShopKind.Buy)
            {
                ItemSync.Instance?.ClientAdoptBought(msg.InstanceId, _pendingBuyPrefab, _pendingBuyRealPos);
                Remember("вх buy ok id=" + msg.InstanceId);
            }
            else
            {
                // Sell ok: the host destroys the item and ItemSync despawn removes our copy.
                Remember("вх sell ok");
            }
        }

        public void Clear()
        {
            _pendingBuyPrefab = 0;
            _pendingBuyRealPos = Vector3.zero;
            _last = "—";
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void Reply(LiteNetLib.NetPeer peer, ShopKind kind, bool ok, ShopFailReason reason, int instanceId)
        {
            if (peer == null) return;
            peer.Send(new ShopResultMsg { Kind = kind, Ok = ok, Reason = reason, InstanceId = instanceId },
                      LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private static int PrefabIndexOf(ShipItem item)
        {
            var s = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return s != null ? s.prefabIndex : 0;
        }

        private static int InstanceIdOf(ShipItem item)
        {
            var s = item != null ? item.GetComponent<SaveablePrefab>() : null;
            return s != null ? s.instanceId : 0;
        }

        private static void ShowNotification(string text)
        {
            try { NotificationUi.instance?.ShowNotification(text); } catch { }
        }

        private void Remember(string text)
        {
            _last = text;
            Plugin.Logger.LogInfo("[ShopSync] " + text);
        }
    }

    /// <summary>
    /// Harmony bridge: intercept the two vanilla shop choke points so a client's buy/sell is
    /// forwarded to the host instead of running against the client's non-authoritative wallet.
    /// Both prefixes return false on the client (skip vanilla) and true otherwise.
    /// </summary>
    public static class ShopPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool buy = TryPatch(harmony, "TryToSellItem", nameof(PreBuy));
            bool sell = TryPatch(harmony, "TryToBuyItem", nameof(PreSell));
            Plugin.Logger.LogInfo("[ShopPatches] Патчи магазина: buy(TryToSellItem)=" + buy + ", sell(TryToBuyItem)=" + sell);
        }

        private static bool TryPatch(Harmony harmony, string method, string prefixName)
        {
            try
            {
                var mi = typeof(Shopkeeper).GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(ShipItem) }, null);
                if (mi == null) return false;
                var prefix = new HarmonyMethod(typeof(ShopPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[ShopPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static bool PreBuy(Shopkeeper __instance, ShipItem item)
        {
            try { return ShopSync.Instance == null || ShopSync.Instance.OnLocalBuy(__instance, item); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PreBuy: " + e.Message); return true; }
        }

        private static bool PreSell(Shopkeeper __instance, ShipItem item)
        {
            try { return ShopSync.Instance == null || ShopSync.Instance.OnLocalSell(__instance, item); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShopPatches] PreSell: " + e.Message); return true; }
        }
    }
}
