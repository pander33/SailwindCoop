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
                // sleep / time advance (conflicts with the shared world clock)
                "GPButtonBed", "GPButtonTavernSleep", "GPButtonOnsenEntrance", "ShipItemBed",
                // cargo / storage operations
                "CargoCarrierButton", "CargoStorageUIButton", "CrateInventoryButton",
                // economy / trade / shipyard
                "EconomyUIButton", "CurrencyExchangeUIButton", "CurrencySwitchButton",
                "GPButtonBuyItem", "GPButtonPurchaseBoat", "ShipyardButton", "ShipyardDocuments",
                "TradeReceiptsUIButton",
                // missions
                "GPButtonListedMission", "GPButtonSetMission", "GPButtonPortMissions",
                "GPButtonMissionListBack", "GPButtonMissionListPage", "GPButtonMissionListWorld",
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
