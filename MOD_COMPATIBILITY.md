# Mod Compatibility — Sailwind LAN Co-op

This document lists Sailwind mods that have dedicated co-op integration, mods verified to work out of the box, known version caveats, and guidelines on what other mods are likely compatible automatically.

Unless noted as **client-side / local only**, every player in a session should install the **same mod versions** (and matching shared-world config settings, which Sailwind Co-op checks automatically at join).

---

## 1. Mods with Dedicated Co-op Integration

### Added in earlier releases (0.7–0.10)

| Mod | What Sailwind Co-op syncs / handles | Notes |
|---|---|---|
| **Economy Overhaul** (1.1.5 / 1.2.0) | Host-owned finances (loans, boat ownership, financed cargo guards), shared markets/currencies/demand events, trade offices (1.2.0), trade book sales & offline receipts, mission crate modifiers, hull plank repair, pole pushing, and attached plank/pole resizing. | Host and guests must run the same Economy Overhaul version (do not mix 1.1.5 and 1.2.0). `Visible traders` setting is checked at join. |
| **Shipyard Expansion** | Custom sail data (flip, texture, scaling) during shipyard refits, saves and late joins. | `Add lug sails` and `Link topmasts` settings are checked at join. |
| **Towable Boats** | Keeps towed boats under host physics authority (including during shared sleep) and treats towing cleats as mooring points rather than generic deck buttons. | `Small boats can tow` setting is checked at join. |
| **Anchor Improvements** (v2.0.0+) | Guest anchor throws are forwarded to the host with matching throw power; `Resize brig anchor` setting is checked at join. | **Requires v2.0.0+ (GitHub).** Legacy Thunderstore v1.1.7 is incompatible with Sailwind 0.40+ co-op anchor sync. |
| **HooksHangMore** | Custom non-`HangableItem` hook attachments (rods, brooms, chip logs, quadrants, knives, hammers, oars, anemometers, anchors) sync live across the crew and on late join. | Standard hangable items (buckets, kettles, pots, mugs, fish) already sync via vanilla `HangableItem`. |
| **Sail Damage** | Host-authoritative sail damage snapshots, guest cloth repairs, yard repairs and late-join resync. | Non-idempotent repair deltas are protected against double-application on timeout. |
| **Postal Expansion** | Shared host mission board, special mail, letter drop-offs, gold payouts and reputation penalties. | Host and guests must use the same Postal Expansion version. |
| **FurstMate** | Host-owned pets (dog, cat, booby), feeding, commands, taming/purchase recovery across reconnects and late-join snapshots. | Separate-wallet purchases keep their recorded charge and recover host results on reconnect. |
| **MusicMod** | Crew instrument notes and sustained sounds follow the player holding the instrument. | Plays across all nearby crewmates. |
| **Viking Horn** | Horn blasts and sustained sound follow the player blowing the horn. | Plays across all nearby crewmates. |

### Added in 0.11.0 (Protocol 116)

| Mod | What Sailwind Co-op syncs / handles | Notes |
|---|---|---|
| **BetterStoves** | Syncs custom `SoupMug` data (nutrition, vitamins, protein, heat, steam, spoilage) when ladling soup into a mug, plus late-join resync. Vanilla-based custom stoves, fuel, cooking and boiler ladling work natively. | Cabin Stove & Galley Range capacity/burn settings are checked at join. |
| **Boat Colors** | Live hull, cabin/roof and trim repaints and color resets sync across the crew and to late joiners. | Sail color settings (`Use Custom Sail Colors`, `Custom Sail Color 1/2`) are checked at join. *Upstream note:* Boat Colors 0.2.2 throws an NRE on startup if `Use Custom Sail Colors` is left `false`. |
| **Tent and Plank** | Guest wood-plank hull repairs, bundle unbundling, pallet cutting, canvas pack consumption/spawning and canvas tarps (tie, repair, untie) sync across the crew. Tarp updates carry only tarp records, so they never reset other players' cargo condition or half-placed tie spots; if two players edit tarps at once the host's set wins. `TarpKnot` buttons stay local so deck button hashes remain stable. | Plank repair, tarp wear and cargo water/fragile damage settings are checked at join. Picking up an item with tarps tied to it unties them for everyone. |
| **Sailor's Keepsakes** (`AdornosMod`) | Sword, helmet and lamp display racks (`EspaderoItem`, `SoporteItem`, `SoporteLamparaItem`) attach/detach across the crew; incense smoke toggles relay through the host; Mjolnir summons the storm around the player who raised it, with lightning and thunder for crewmates nearby (the cooldown starts only when the host confirms). | Hidden treasures appear for everyone at their configured spot; the first player to pick one up takes it for the whole crew (the others' copies vanish and are marked found), and it becomes a normal shared item. Late joiners receive current rack attachments and lit incense state. |
| **RadRefinements** | Continual rowing strokes on clients, knife container-to-firewood cutting, and belt slot swaps (`SwapItems`). | HUD readouts, pipe/tobacco and personal needs multipliers remain local. |
| **Kemy's Furniture** | `AutoLeveler` pose adjustments broadcast to the crew via `ItemSync`; custom furniture prefabs, shops and `CrateInventory` storage sync natively. | All players must have the mod installed. |
| **BetterTables** | Unlocks `AutoLevel` tabletops while carried by a remote player so tabletops re-level cleanly when placed on a differently sloped deck. | All players must have the mod installed. |
| **MoreSailwindSails** | Refreshes `ControlsSync` automatically when dynamic `OwnedWinch` mounts are created or swapped; custom sails and shipyard refits sync natively. | `ForceSheetFallbacks` setting is checked at join. |
| **SimpleTides** | Deterministic tide height stays aligned automatically because `EnvironmentSync` already syncs sun time and moon phase. | All 12 solar, antipodal and regional tide/offset settings are checked at join. |
| **FreeCameraAndWaterCollision** | Keeps Photo Mode time-of-day previews local (host Photo Mode no longer shifts the sky for guests, whose clock keeps running at the normal rate; client snapshots no longer fight the slider) and defers `HostPauseSync` control restore until free camera exits. | Local-only mod; safe to install on only one machine. |
| **Player Home** | Host owns the sidecar save (`ModSave`) and warehouse item capture/restore (restored `ShipItem`s replicate via `ItemSync`). Home/tavern ownership syncs on join and on live `HomeBuyButton` claims; guests suppress local sidecar I/O and duplicate item restores. | 8 claiming/radii/persistence settings are checked at join. |
| **BetterNPCBoats** | Host simulates the 30 pooled regional fishing boats (`BetterNPCFishingBoat`) and streams poses, velocities, lanterns, pilot/fisherman visibility and sail animation at 5 Hz; clients slave fishing boat bodies kinematically (cooperating with `BoatHorizon`). | All players must have the mod installed. |
| **RandomEncounters** | Host drives encounter generation and save restore; guests suppress local RNG timers and `LoadEncounter`. Standard flotsam syncs via `ItemSync`; asset-bundle wreckage, Dense Fog, Fishing Bonanza (seagulls + guest catch boost), Intense Storm Crest wave/lightning/rain overrides, and SeaLifeMod whales sync from host (including late join). | `Control SeaLifeMod spawns` setting is checked at join. |
| **HMS Leopard** | Base hull, sails, winches, anchor, mooring and damage sync natively; dedicated adapter syncs the ship's bell, lower/upper/quarter gunport groups (with overflows/masks/triggers), cannons and carriages (single & broadside push/pull/fire), cutter launch/hoist, cutter rowing (`OarController` forces on host + remote oar visuals), and late-join snapshots. | All players must have the mod installed. |

---

## 2. Compatible Out of the Box

These mods were audited against Sailwind Co-op's sources and need no adapter:

- **SailwindModdingHelper** (`2.1.1`) — helper library and event hooks; compatible as-is (note that mods storing separate sidecar files via `ModSave` outside `SaveContainer` still need their own sync if those files represent shared world state).
- **CookedInfo** (`1.2.3`) — local food/soup tooltip text; reads fields already synced by `ItemSync`.
- **ProfitPercent** (`1.2.1`) — local `EconomyUI` profit columns and standard receipt printing.
- **SailInfo** (`1.2.1`) — local winch/rudder/boat HUD; works accurately on clients because `BoatSync` restores kinematic boat velocity after each physics step.
- **ModVersionChecker** (`1.3.3`) — local title-screen version notice; no menu hierarchy collision.
- **Furniture Fix** (`1.1.3`) — local pickup/insert crouch gating; does not interfere with remote `ItemSync` cargo loading.
- **Kemy's Navigation Tools** (`1.2.2`) — standard `ShipItem`/`ShipItemLight` prefabs and visual needle scripts; syncs natively when installed on all machines.
- **Better Lanterns and Lights** (`1.1.3`) — local flicker visuals and belt lantern toggling via standard `ShipItemLight.OnAltActivate()` / `SetLight()`.
- **SailCollisionFix** (`1.1.3`) — shipyard placement collision relaxation; final `SaveSailData` applies cleanly via `ShipyardSync`.
- **Performance Tweaks** (`1.1.0`) — local CPU/rendering optimizations; compatible with `DirtSync`, `ItemSync` and `ShopSync`.
- **RadFixes** (`1.3.4`) — bug fixes and scene shopkeeper fixes; compatible when installed on both sides.
- **NANDFixes** (`1.4.4`) — bug fixes; compatible with Sailwind Co-op's kinematic boats and anchors (*upstream note:* its `Ambient Occlusion fog fix` option can throw an NRE in `Plugin.Update` during loading screens — disable that option if you see log spam).
- **Save Cleaner** (`0.4.1`) — cleans invalid entries from `SaveContainer` on load; safe in co-op without an adapter.
- **Deep Ports** (`0.3`) — deterministic startup harbor terrain/mesh swap; compatible when installed in `BepInEx/plugins/Deep Ports/` on every player (hardcoded asset bundle path).
- **ModSaveBackups** (`1.2.0`) — sidecar backup library for `SailwindModdingHelper.ModSave`; safe by itself, though sidecar files are not streamed with the world save (each consumer mod needs its own adapter, such as `Player Home`).
- **Shattered Seas Expansion** (`0.1.3`) — custom Clipper boat built on vanilla Sailwind boat/shipyard components; compatible when installed in `BepInEx/plugins/ShatteredSeasExpansion/` on every player without `ClipperSaveCleaner.dll` (*upstream note:* rejoining after a scene reload in the same game launch can fail to reload its AssetBundle — restart the game before rejoining).

---

## 3. Which Other Mods Are Likely Automatically Compatible

Even if a mod is not listed above, it will **usually work out of the box** with Sailwind Co-op if it falls into one of these categories:

- **Purely local / client-side mods** (safe even when only one player has them installed): HUD overlays, extra tooltips and instrument readouts, compass/chart/clock annotations that only change display text, camera tweaks, audio/music replacements, weather visuals, lighting/shader/color tweaks, hotkeys, UI layout fixes, and local CPU/rendering performance mods.
- **Standard-API content mods** (must be installed with the same version on **all** players):
  - New items built as standard `ShipItem` / `SaveablePrefab` prefabs sold through vanilla `ShopItemSpawner`s (including standard `ShipItemFood`, `ShipItemBottle`, `ShipItemLight`, `ShipItemCrate` with `CrateInventory`, `HangableItem`, and `ShipItemStove`).
  - New sails or shipyard parts registered through vanilla `PrefabsDirectory` / `SaveableBoatCustomization` / `Mast.LoadSail` without custom per-frame physics scripts.
  - Static island/port geometry and visual/collider tweaks (such as port or harbor layout mods) when every player loads the exact same scene assets.
  - Deterministic world-state mods whose output depends only on synced vanilla inputs (wind, sun time, moon phase, waves, or host-saved `GameState.modData` that does not change mid-session).

By contrast, a mod **usually needs a dedicated adapter** (and may desync without one) if it:
- Mutates `GameState.modData` or custom world objects live during a session (e.g., building structures, painting hulls, spawning world encounters or custom NPC ships);
- Stores shared world progress in external sidecar save files outside Sailwind's `SaveContainer`;
- Implements custom item attachment/docking systems that bypass vanilla `HangableItem`, `CrateInventory` or `GPButtonInventorySlot`;
- Lets clients directly spawn (`Instantiate` + `RegisterToSave`) or destroy (`DestroyItem`) items, apply forces directly to kinematic client boat `Rigidbody`s, or modify host-authoritative hull damage/economy/storms outside vanilla methods.
