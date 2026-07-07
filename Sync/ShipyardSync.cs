using System;
using HarmonyLib;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P4.5 — buying a boat at the shipyard. A purchasable boat is already in the scene (pre-placed at the
    /// dock); <c>PurchasableBoat.PurchaseBoat()</c> just flips <c>SaveableObject.extraSetting = true</c> and
    /// deducts the buyer's gold. <see cref="BoatLocator"/> excludes unpurchased boats and includes purchased
    /// ones, so once a boat is marked bought, <see cref="BoatSync"/> picks it up and streams it automatically.
    ///
    /// Co-op: the buyer pays from their OWN wallet (separate-money model); we only replicate the world-state
    /// flip. The buyer broadcasts the boat's stable <c>sceneIndex</c>; the other peer marks the same boat
    /// purchased via <c>LoadAsPurchased()</c> (sets extraSetting + hides the for-sale UI, no charge). Then both
    /// machines enumerate and sync the boat. No runtime spawning is involved.
    /// </summary>
    public sealed class ShipyardSync
    {
        public static ShipyardSync Instance { get; private set; }

        private readonly CoopNet _net;

        public ShipyardSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>Local player just bought a boat (vanilla already paid + flipped extraSetting). Tell the peer.</summary>
        public void NotifyPurchase(int sceneIndex)
        {
            if (_net.State != LinkState.Connected || sceneIndex < 0) return;
            _net.Broadcast(new BoatPurchaseMsg { SceneIndex = sceneIndex }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[ShipyardSync] out boat purchase sceneIndex=" + sceneIndex);
        }

        public void OnBoatPurchase(BoatPurchaseMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            try
            {
                var slm = SaveLoadManager.instance;
                var objects = slm != null ? slm.GetCurrentObjects() : null;
                if (objects == null || msg.SceneIndex < 0 || msg.SceneIndex >= objects.Length)
                {
                    Plugin.Logger.LogWarning("[ShipyardSync] in purchase: missing object sceneIndex=" + msg.SceneIndex);
                    return;
                }
                var so = objects[msg.SceneIndex];
                var boat = so != null ? so.GetComponent<PurchasableBoat>() : null;
                if (boat != null)
                {
                    boat.LoadAsPurchased();   // extraSetting = true + hide for-sale UI, no money charged
                    Plugin.Logger.LogInfo("[ShipyardSync] in boat purchase sceneIndex=" + msg.SceneIndex + " marked");
                }
                else
                {
                    // Fallback: no PurchasableBoat component — flip the flag directly so BoatLocator includes it.
                    if (so != null) so.extraSetting = true;
                    Plugin.Logger.LogInfo("[ShipyardSync] in purchase sceneIndex=" + msg.SceneIndex + " (extraSetting directly)");
                }

                // Host relays a client's purchase to any other clients (2-player needs nothing more).
                if (_net.Role == Role.Host)
                    _net.Broadcast(new BoatPurchaseMsg { SceneIndex = msg.SceneIndex }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipyardSync] OnBoatPurchase: " + e.Message); }
        }
    }

    /// <summary>
    /// Harmony bridge: after a local <c>PurchasableBoat.PurchaseBoat()</c> (buyer paid own wallet, flipped
    /// extraSetting), broadcast the boat's sceneIndex so the peer marks it purchased too. The receiver uses
    /// <c>LoadAsPurchased()</c> (not PurchaseBoat), so no echo loop and no double charge.
    /// </summary>
    public static class ShipyardPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool buy = TryPatch(harmony);
            Plugin.Logger.LogInfo("[ShipyardPatches] Boat purchase patch: PurchaseBoat=" + buy);
            SailwindCoop.Runtime.PatchHealth.Report("Shipyard", buy ? 1 : 0, 1);
        }

        private static bool TryPatch(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("PurchasableBoat");
                if (t == null) return false;
                var mi = AccessTools.Method(t, "PurchaseBoat");
                if (mi == null) return false;
                var postfix = new HarmonyMethod(typeof(ShipyardPatches).GetMethod(nameof(PostPurchase),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
                harmony.Patch(mi, postfix: postfix);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipyardPatches] PurchaseBoat: " + e.Message); return false; }
        }

        private static void PostPurchase(PurchasableBoat __instance)
        {
            try
            {
                if (ShipyardSync.Instance == null || __instance == null) return;
                var so = __instance.GetComponent<SaveableObject>();
                if (so != null) ShipyardSync.Instance.NotifyPurchase(so.sceneIndex);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipyardPatches] PostPurchase: " + e.Message); }
        }
    }
}
