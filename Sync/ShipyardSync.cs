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
    public sealed partial class ShipyardSync
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
            // extraSetting just flipped, so this boat now belongs to the network set — drop the
            // cached enumeration before anything addresses a boat by index.
            BoatLocator.Invalidate();
            if (sceneIndex >= 0) _ownership[sceneIndex] = true;
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
                if (!msg.Owned)
                {
                    if (_net.Role == Role.Host) return; // only the host's world gives a boat back
                    // Only a real change: the join baseline lists every boat the host does not own.
                    if (so == null || !so.extraSetting) return;
                    Unown(so, boat);
                    _ownership[msg.SceneIndex] = false;
                    BoatLocator.Invalidate();
                    Runtime.CoopBehaviour.Instance?.Boats?.ReleaseBoat(so.transform);
                    Plugin.Logger.LogInfo("[ShipyardSync] in boat no longer owned sceneIndex=" + msg.SceneIndex);
                    return;
                }
                if (boat != null)
                {
                    boat.LoadAsPurchased();   // extraSetting = true + hide for-sale UI, no money charged
                    // The native helper only sets the flag when the for-sale UI has been registered.
                    so.extraSetting = true;
                    if (_net.Role == Role.Host)
                        EconomyOverhaulCompat.CompleteRemoteBuyback(boat);
                    Plugin.Logger.LogInfo("[ShipyardSync] in boat purchase sceneIndex=" + msg.SceneIndex + " marked");
                }
                else
                {
                    // Fallback: no PurchasableBoat component — flip the flag directly so BoatLocator includes it.
                    if (so != null) so.extraSetting = true;
                    Plugin.Logger.LogInfo("[ShipyardSync] in purchase sceneIndex=" + msg.SceneIndex + " (extraSetting directly)");
                }

                // The boat set just grew — force a fresh enumeration before it is addressed by index.
                BoatLocator.Invalidate();
                _ownership[msg.SceneIndex] = true;

                // Host relays a client's purchase to any other clients (2-player needs nothing more).
                if (_net.Role == Role.Host)
                    _net.Broadcast(new BoatPurchaseMsg { SceneIndex = msg.SceneIndex }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipyardSync] OnBoatPurchase: " + e.Message); }
        }

        // -----------------------------------------------------------------
        // Ownership changed without PurchaseBoat. Other mods set SaveableObject.extraSetting
        // themselves (Economy Overhaul's rentals, returns, seizures and buy-backs), so the host
        // watches the flag of every purchasable boat and sends each change, both ways. Once a second:
        // a mod that flips the flag only for the length of one call is never seen.
        // -----------------------------------------------------------------

        private readonly System.Collections.Generic.Dictionary<int, bool> _ownership = new System.Collections.Generic.Dictionary<int, bool>();
        private float _ownershipTimer;
        private bool _ownershipPrimed;

        private void TickOwnership(float dt)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected || !EconomyOverhaulCompat.Present ||
                !GameState.playing || GameState.currentlyLoading) return;
            _ownershipTimer += dt;
            if (_ownershipTimer < 1f) return;
            _ownershipTimer = 0f;
            var objects = SaveLoadManager.instance != null ? SaveLoadManager.instance.GetCurrentObjects() : null;
            if (objects == null) return;
            bool changed = false;
            for (int i = 0; i < objects.Length; i++)
            {
                var so = objects[i];
                if (so == null || so.GetComponent<PurchasableBoat>() == null) continue;
                bool owned = so.extraSetting;
                if (_ownership.TryGetValue(i, out bool known) && known == owned) continue;
                _ownership[i] = owned;
                if (!_ownershipPrimed) continue; // the first pass only learns: clients loaded the same save
                changed = true;
                BoatLocator.Invalidate();
                _net.Broadcast(new BoatPurchaseMsg { SceneIndex = i, Owned = owned }, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Plugin.Logger.LogInfo("[ShipyardSync] out ownership sceneIndex=" + i + " owned=" + owned);
            }
            _ownershipPrimed = true;
            if (changed) Runtime.CoopBehaviour.Instance?.Boats?.RefreshOwnedBoats();
        }

        private void ClearOwnership() { _ownership.Clear(); _ownershipPrimed = false; _ownershipTimer = 0f; }

        /// <summary>The boat back to the shipyard: not owned, its for-sale sign out again.</summary>
        private static void Unown(SaveableObject so, PurchasableBoat boat)
        {
            if (so != null) so.extraSetting = false;
            if (boat == null) return;
            try
            {
                var ui = AccessTools.Field(typeof(PurchasableBoat), "purchaseUI")?.GetValue(boat) as UnityEngine.GameObject;
                if (ui != null) ui.SetActive(true);
                if (GameState.lastOwnedBoat == boat.transform) GameState.lastOwnedBoat = null;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipyardSync] for-sale sign: " + e.Message); }
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
                // A finalizer, not a postfix: since game 0.39 PurchaseBoat ends with a loop over
                // BoatRefs.masts, which has empty slots, and throws on the first one after the boat is
                // already paid and marked bought. A throw skips postfixes, so the purchase never went out.
                var finalizer = new HarmonyMethod(typeof(ShipyardPatches).GetMethod(nameof(FinishPurchase),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
                harmony.Patch(mi, finalizer: finalizer);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipyardPatches] PurchaseBoat: " + e.Message); return false; }
        }

        /// <summary>Runs whether or not the game's method threw; the game's own exception is left as
        /// it is. The boat counts as bought only if the game got as far as marking it.</summary>
        private static void FinishPurchase(PurchasableBoat __instance)
        {
            try
            {
                if (ShipyardSync.Instance == null || __instance == null || !__instance.isPurchased()) return;
                var so = __instance.GetComponent<SaveableObject>();
                if (so != null) ShipyardSync.Instance.NotifyPurchase(so.sceneIndex);
            }
            catch (Exception e) { ShipyardSync.Report("FinishPurchase", e); }
        }
    }
}
