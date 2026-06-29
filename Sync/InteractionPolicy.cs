using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>How a given interaction should be treated in co-op.</summary>
    public enum InteractPolicy
    {
        /// <summary>Shared ship state — forward to the host, which replays it (default for deck controls).</summary>
        Shared,
        /// <summary>A physical item (pickup/use) — handled by the future item-replication subsystem (P3); not forwarded yet.</summary>
        Item,
        /// <summary>Personal/UI — settings, menus, zoom, personal map. Never synced; works locally only.</summary>
        Local,
        /// <summary>Touches the host's authoritative world (economy, missions, sleep/time, save). Blocked on the client.</summary>
        HostOnly,
    }

    /// <summary>
    /// Classifies a <see cref="GoPointerButton"/> into a co-op policy by its concrete type
    /// (see <c>PLAN_SYNC.md</c>). Without this the interaction layer would blindly forward
    /// any button that happens to be a child of the boat — including personal UI and
    /// host-world actions. Unknown deck controls default to <see cref="InteractPolicy.Shared"/>,
    /// which is correct for the vast majority of ship mechanisms; the personal and host-world
    /// types are enumerated explicitly below.
    /// </summary>
    public static class InteractionPolicy
    {
        // Touches the host's authoritative world — blocked on the client (P4 = host-only for now).
        private static readonly System.Collections.Generic.HashSet<string> HostOnly =
            new System.Collections.Generic.HashSet<string>
            {
                // sleep / time advance (conflicts with the shared world clock) — mediated later (P4.2)
                "GPButtonBed", "GPButtonTavernSleep", "GPButtonOnsenEntrance", "ShipItemBed",
                // save
                "GPButtonAutosaveToggle",
            };

        // Personal / interface — must never be replicated (each player keeps their own).
        private static readonly System.Collections.Generic.HashSet<string> Local =
            new System.Collections.Generic.HashSet<string>
            {
                "GPButtonAntiAliasing", "GPButtonLightQuality", "GPButtonScreenResolution",
                "GPButtonResolutionUI", "GPButtonWindowMode", "GPButtonTargetFramerate",
                "GPButtonKeybinding", "GPButtonResetKeybindings", "GPButtonSliderVolume",
                "GPButtonSettingsCheckbo", "GPButtonInterface", "GPButtonExtraMenus",
                "GPButtonControlToggle", "GPButtonLogMode", "GPButtonDayLogDay",
                "GPButtonMapZoom", "StartMenuButton", "GPButtonInventorySlot",
                "MouseoverTextTrigger",
                // Personal economy (local money model): each player buys/sells/exchanges against their own
                // wallet locally — these UI buttons must work on the client, not be blocked. Only the item
                // consequence of a buy/sell is shared (ShopPatches → ItemSync handoff/despawn).
                "GPButtonBuyItem", "EconomyUIButton", "CurrencyExchangeUIButton",
                "CurrencySwitchButton", "TradeReceiptsUIButton",
                // crate inventory withdraw/insert button: a LOCAL UI button; the resulting
                // CrateInventory.Insert/WithdrawItem is mediated to the host by ItemSync's crate relay.
                "CrateInventoryButton",
                // cargo UI: hire-transport / page-nav / withdraw buttons are LOCAL UI; the wallet ops in
                // CargoCarrier.Insert/WithdrawItem are mediated to the host by ItemSync's cargo relay.
                "CargoCarrierButton", "CargoStorageUIButton",
                // mission scroll: opening/browsing/paging is local UI; accepting/abandoning is mediated to
                // the host at the PlayerMissions.AcceptMission/AbandonMission choke points (MissionPatches).
                "GPButtonPortMissions", "GPButtonListedMission", "GPButtonSetMission",
                "GPButtonMissionListBack", "GPButtonMissionListPage", "GPButtonMissionListWorld",
                // shipyard: browsing/buying is local UI; the boat is bought with the buyer's own wallet and
                // the purchase (extraSetting) is replicated to the peer by ShipyardPatches → ShipyardSync.
                "ShipyardButton", "ShipyardDocuments", "GPButtonPurchaseBoat",
                // player movement only; the resulting player pose is already local/player-sync
                "BoatLadder", "GPButtonRatlines",
            };

        // PickupableItems that are actually shared ship mechanisms handled by a dedicated sync
        // (so they're treated as their own thing, not the generic item/forward path). Mooring
        // ropes are driven by MooringSync; nothing else qualifies yet.
        private static readonly System.Collections.Generic.HashSet<string> SharedPickupable =
            new System.Collections.Generic.HashSet<string>();

        public static InteractPolicy Classify(GoPointerButton btn)
        {
            if (btn == null) return InteractPolicy.Local;
            string n = btn.GetType().Name;

            if (HostOnly.Contains(n)) return InteractPolicy.HostOnly;
            if (Local.Contains(n)) return InteractPolicy.Local;
            if (SharedPickupable.Contains(n)) return InteractPolicy.Shared;
            if (btn is PickupableItem) return InteractPolicy.Item;   // generic item — P3
            return InteractPolicy.Shared;                            // deck control
        }
    }
}
