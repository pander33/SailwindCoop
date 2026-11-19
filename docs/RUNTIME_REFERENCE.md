# Runtime reference

Current implementation: plugin 0.9.0, Protocol 112 (0.9.0 was released with 111), Sailwind 0.40 / 0.40.1.
Architecture and mandatory development rules: [CLAUDE.md](../CLAUDE.md).
Verification status and the only active task list: [СОСТОЯНИЕ.md](../СОСТОЯНИЕ.md).
Protocol numbers within subsystem descriptions identify when a feature was introduced.
Paths inside code spans are relative to the repository root.

**Client trust.** The host fully trusts connected
clients. Do not add eligibility checks or numeric checks of request values. The only checks are the
sender's handshake, the PlayerState NetId matching its sender, object index bounds/null, and packet
format. `MessageDirection` rejects a packet sent in the wrong direction; that is a format rule, not
a gameplay one.

**Addressing.** Controls, anchor, mooring, damage and interactions have a separate context per
owned boat (`BoatContexts<T>`). Packets carry `BoatIndex` plus an object index; `LayoutHash` is
diagnostic only. Layout-indexed messages carry `Generation`: after a shipyard refit, hull domains
resume behind a generation barrier and a targeted context rebuild. `BoatAuthority` keeps guest
poses only to decide which distant boats stay active.

**State is sent on change.** Controls, anchor, mooring, damage, storms, items, instruments and the
mission journal go through `ChangeStream` or revisions. The one exception is hull damage: the host
also resends it unchanged every 30 s (`BoatDamageSync.RefreshSeconds`). A freshly bound receiver asks once with
`ResyncRequest` (105). Request and revision counters are session-wide; a pending request times out
after 5 s.

**Events come from interactions only.** An interaction event arises from real input, a hold, or a
domain handler. Snapshots, initial sync, pose streams and echoes are not actions and never replace
the last-action diagnostics (`MooringState.IsInteraction`, `SpawnObject.IsSnapshot`,
`InteractionContext`).

**Held things are protected from snapshots.** A rope, winch, anchor, mooring part, folded item or
orb in the local hand is not overwritten by incoming state. Mouse and sticky grab, rope divergence
and quick-release are supported.

Domain notes:

- **Bilge pump:** actual movement and effort travel through the host. Pump visuals use the
  saveable hull index, including unowned hulls; the host applies pumping to water on owned hulls.
- **Hatches:** `HatchState`/`HatchOpen` with deferred application after an interaction; a one-time
  `HatchSnapshot` on join or for a new hull. Hatches outside the fleet toggle locally.
- **Mooring:** waits for the acknowledgement of the last `RequestId` and for the rope or reel to be
  released. An unavailable dock is acknowledged with `StateAvailable=false`; a client without the
  dock defers application. Rope ends and the length reel are carried through `MooringCarry`.
  Game 0.40.1 parents a moored rope to `Refs.shiftingWorld` (0.40: under the dock's spring), so
  `MooringSync.DockFor` takes the dock from the spring's GameObject, never from the rope's parent.
- **Anchor:** bound through `RopeControllerAnchor.joint` (`Anchor.Awake` moves it above the hull).
  `AnchorRequest` (88) carries pickup, held pose and drop: boat-local on the holder's own deck,
  real-space on shore and on drop. `Anchor.ExtraFixedUpdate` is suppressed only on the remote copy;
  free physics stays with the host. That method is the only writer of `RopeControllerAnchor.canPull`,
  which the winch needs to take a pull, so `AnchorState` carries the host's value (Protocol 112)
  and a client writes it into its rope. Pickup and drop, local or remote, empty the anchor's
  `groundedCols`: a carried anchor is a trigger and gets no `OnCollisionExit`, so the list kept
  the seabed or dock and the host dug a guest's dropped anchor in mid-air (`ForgetGround`).
  The high `anchorDrag` is applied on pickup only when the anchor was set. For a local pickup,
  the game's `ExtraFixedUpdate` reduces the drag while the anchor is carried. For a guest's
  pickup, the host suppresses that method and leaves drag alone on subsequent hand poses. On
  drop or guest disconnect, the host subtracts the decay for the time spent carrying the anchor,
  with `anchorDragUp` as the minimum (`MeltCarriedDrag`).
  Every player hull in game 0.39 has exactly one anchor (asset inventory), so binding the first
  one covers them all.
- **Held item wall check (client):** an item in a client's hand is a puppet, but its proxy is a
  kinematic trigger placed into the walk copy each frame (`HeldItemProbe`), as the game's held
  proxy is. The game's `PickupableItemCollisionChecker` then sees walls, `GoPointer` pushes a big
  item out of them and refuses a drop inside one. `SetPuppet` leaves that proxy alone while it is in
  the hand. Without the probe a guest could put a crate inside the hull; the host woke its physics
  there and `BoatMass` threw the boat's centre of mass with it.
  Free cargo (big items, crates, trade goods with `Good`) keeps a solid kinematic proxy that
  follows its item (`_solidCargo`, decided in `ApplyRemote`), so the hand stops at cargo on deck
  too, and the guest's character collides with it as in single player. `SetPuppet` and
  `MoveProxyToItem` read the set, so these colliders are not switched off and on every frame. The
  hand's checker drops colliders that were switched off (`PruneCollisionChecker`): they send no
  `OnTriggerExit`.
- **Pushing with full hands:** nobody pushes a boat (hull, dock or sail push control) with cargo
  (`ItemSync.IsCargo`) in his arms; host and client follow the same rule, and a small item does
  not stop a push. The game itself does not stop it. `InteractionSync.ShouldBlockPushWhileHolding` skips the push control's
  `ExtraFixedUpdate` on the pushing machine; the host applies a client's push from its request,
  not through that method. Wheel, winches and other controls stay usable with an item in hand, as
  in the game.
- **Items:** full ordered item state with revision, acknowledgement and tombstones. Compound
  operations (cutting, elixirs, oakum, oil, candles, tobacco packs) go through
  `ItemOperationRequest`/`Result` and apply once. Typed components cover food, soup, kettle, stove,
  fuel and pipe. `ShipItemFoldable` applies `amount` through absolute Fold/Unfold. Values the
  host's world moves every frame (drying, spoiling, a burning log) get a new revision only after
  a step or at a mark the game reads (`ItemAgeing`). A log in a stove is the stove's child with
  no pose of its own (`ItemComponents.PlaceFuel`). A tilted pot is emptied by one machine only —
  its holder's, or the host's (`ItemSync.OwnsDrain`) — and its level goes as item state.
  Drying, spoiling, the heat left after the stove, a soup's spoiling and the fuel of a burning
  lamp or log belong to the host's world: the host does not take them from a drop or from a
  report of a held item (`ItemSync.KeepWorldOwned`, `ItemDetails.TakeWorldOwned`), and the
  holder takes only those from host states (`ApplyWorldOwned`). An operation's result is still
  applied whole.
- **Client's boat copy:** `BoatDamagePatches` skips `Impact`, `Overflow` and `DailyDamage` on a
  hull `BoatSync` drives and puts `waterLevel`/`waterIntakeChunk` back after `BoatDamage.Update`;
  the pump and the bucket change water outside `Update`. A boat that is not the host's
  `GameState.lastBoat` therefore takes no damage on any machine.
- **Instruments, charts, dirt, orb:** watch/quadrant/scroll targets, chip log and fishing rod child
  poses, chart marks per map instance, hull dirt by UV stroke with a chunked PNG result, wind totem
  orb carry and the actual cast.
- **Shipyard:** `RefitRequest`/`RefitState` (102/103) carry committed sails, parts, cleaning and
  repair. Payment and UI are not replayed; the local preview stays out of the shared world and save.
- **Sleep:** two forms (`Sync/SleepSync.cs`). The game's `FallAsleep` only
  starts a *personal* sleep: dark screen, sleep need recovered at `PersonalWarp` (4), hunger and
  thirst frozen, no time warp, and
  **`GameState.sleeping` stays false** (on the host that flag freezes other players' boats and
  items). It ends when the player is rested or gets up, not by duration; a player lying awake in
  a bed keeps all needs unchanged (`AdjustsNeeds`). Clients report `SleepPresence` (112: in bed /
  wants / rested / tavern / timeskip); the host alone starts the *shared* sleep (blackout for all,
  `timeScale` 4) when every loaded player is in bed or asleep — rested or not, except a fully
  rested crew where time cannot be skipped — and alone ends it; every player then gets the game's
  sleep flags. The game wakes only its own sleeper, so for a personal sleep the host sends
  `GameplayNoticeKind.BoatAlarm` (a damaging `BoatDamage.Impact`, or `Overflow`) with the boat
  index, and a player asleep alone on that boat wakes.
  `Sleep.timeskipSleep` is set on every machine in a shared time skip (the game's `Sun` runs nine
  times faster under it, and needs are counted by the Sun). A `WakeUp` from
  `PlayerNeedsUI.PlayWarning` (own hunger or thirst) wakes that player alone: the host ends the
  shared sleep with `SleepPhase.Cancel`, on which everyone else goes on in a personal sleep
  (`SleepOnAlone`); the host does the same when a loaded player is no longer lying.
  `[SleepSync]` log lines carry the player's needs at every transition. A native `WakeUp` from outside `Sleep.Update` on the host
  (collision, aground, water) ends the shared sleep. Clients send
  no sleep transitions; `SleepRequest` carries only the onsen entrance and the baseline request.
  `PlayerNeeds.LateUpdate` uses the host's simulation rate for passive needs during shared sleep.
  The environment snapshot sends the ratio of simulated to real time measured over its sample
  window, including frames Unity capped. Guests apply it in the native needs calculation, before
  the game's caps and fainting checks. Guests freeze passive needs after five seconds without
  a snapshot and resume them when a new one arrives. `AdjustNeeds` handles personal sleep and lying awake.
  The host keeps the normal 22 ms physics step and caps catch-up at eight steps per frame, or the
  game's existing lower cap. The former 222 ms step could send the hull into a collider under
  buoyancy or mooring forces. `SharedSleepTiming` tracks hours advanced by the host's Sun, including
  midnight. The host wakes the crew after 15 seconds without world-clock progress. A guest can
  leave after 180 seconds without host clock snapshots, including when the last snapshot said
  the host was paused. A pause snapshot stops blocking guest movement when it is 180 seconds old.
  Fresh snapshots sustain an intentional pause. The host's stall timer suspends during an
  intentional pause, including while the F8 window covers the game's settings. Wake and
  disconnect restore the host's clock and frame budget. If the clock
  stopped during the fade-in with no pause owner, wake restores the pre-sleep rate. The sleep
  fade uses unscaled time so it can clear on a frozen world. Escape or the native bed controls
  leave a shared sleep after three real seconds, even if the host is paused. The guest reports
  this immediately with `SleepPresence`. A paused host processes the wake when it resumes,
  cancels the warp and lets the remaining players continue their personal sleep. Bed interaction
  inputs are ignored while a menu owns the cursor.
  `ItemRigidbody.FixedUpdate` puts loose cargo aboard into kinematic mode before physics while the
  local player is in bed or in shared sleep. The item sync applies the same rule when releasing a
  proxy. Cargo keeps its colliders and weight, and the drop pose's overlap correction is capped
  when the cargo becomes dynamic again. The native item step decides when to make the body
  dynamic again. The mod restores the previous continuous collision mode at that point, or
  the normal dynamic mesh mode if the release began with an already kinematic proxy. A deferred
  mesh drop gets its first dynamic step even if the item's vanilla idle timer has expired.
- **Recovery:** the game's own (`Recovery.RecoverPlayer`: passed out, the Recover button of the
  Escape menu, the world border). `Sync/RecoveryPatches.cs`. The host's recovery takes
  the boat to the port with whoever is aboard; when `RecoverBoat` is done (its enumerator is
  wrapped, `Run`) a host with clients brings its origin to the offset a client ends on
  (`OriginCatchUp.AlignToLoadedOffset`), resends every change-only stream
  (`CoopBehaviour.ResendBoatState`) and stops its world for 1.5 s (`OriginCatchUp.SettleHost`,
  the same hold as after a teleport). `SettleHost` also sends the host's origin offset in
  steps (`GameplayNoticeKind.OriginOffset`, a notice kind and not a message: 0.5.0 ignores it);
  a client that stands within one step of zero under that offset — it came along on the boat —
  takes it instead of working one out from its own position, because the host's wave phases
  fit only at the host's offset. Clients elsewhere keep theirs. A client's recovery skips `Recovery.RecoverBoat`
  and sets `boatRecovered` itself; the player is refilled and, off a boat, moved to his port.
  Whoever is recovered pays: with a shared wallet the fee is put back before it reaches the host
  and taken from the client's money set aside (`WalletSync.MoveFeeToOwn`). The dark screen is
  the personal sleep; `SleepSync.TickRecovery` ends a recovery that lasts over 120 s, and
  that cancels the game's coroutines too: in a session `DoRecoverPlayer` and `RecoverBoat` are
  stepped by `RecoveryPatches.Run` under a generation number that `Cancel` and every new
  `RecoverPlayer` move on. A guest recovered aboard stays aboard and pays; a player being
  recovered counts neither as in bed nor as asleep for the shared sleep. While the
  host's world is held (`JoinPause.Settling`) the host ticks of boats, controls, anchor and
  mooring get unscaled time (`CoopBehaviour.SendDt`) — only ticks that use time for nothing but
  their send timers may. `ReloadGameScenes`
  must leave `GameState.loadingScenes` at zero: the recovery waits for it.
- **Missions:** the journal is sent on change and to a client that finished loading; a guest takes
  the journal only from the host.
- **House doors:** `HouseDoor` (106) carries the absolute target of island house doors.
- **World pause:** Escape and F8 offer **Pause world** / **Resume world** in a loaded co-op session.
  Escape retains its camera and cursor behavior. `Runtime/GameMenuPause.cs` hands the clock from
  the stock menu to co-op on session start and back when the session ends. The Escape page follows
  the camera while the world runs. The host applies `PauseRequest` as a desired state and sends
  `PauseState` reliably, including the baseline for joining players. `JoinPause` owns the host
  clock until the manual, loading and teleport reasons have all cleared. The loading timeout clears
  only the load hold. A join can use a fresh save taken under manual pause. If that save stalls,
  it waits for the crew to resume. Unscaled environment snapshots carry the pause revision, so
  delayed UDP updates cannot roll back the clock after a transition. `HostPauseSync` holds local
  movement while the menu pointer stays available. Sleep and recovery can hand back player control
  through `HostPauseSync`, which restores it as soon as no sleep, pause or menu holds it. During a
  world pause, the normal needs update and movement drain wait on both roles. The stock Escape
  menu blocks local locomotion independently of the clock, keeping gravity and collisions active.
- **Saves:** the profile and the co-op slot are written atomically with a backup and `GameVersion`.
  A join from a loaded world first returns to the title screen (`CoopBehaviour.LeaveWorldFirst`):
  the session is torn down, an own world is saved, the game's static state is reset and scene 0 is
  loaded again, then the join is repeated. The game itself never leaves a world, so this path has
  no vanilla counterpart.
- **Mods:** after `HelloAck` the host sends `ModManifest` (107) and starts the world transfer only on
  the client's `ModSyncResult` (108); member state `CheckingMods`. A client with the same mods answers
  by itself; otherwise the F8 **Mods** section offers Download / Join anyway / Cancel. Files are
  requested by manifest index (109–111), staged in `BepInEx/cache/SailwindCoop/modsync`, checked by
  size and SHA-256 and moved into `plugins` without overwriting anything; then the game must be
  restarted. Only missing mods are installed. The host never refuses a client over its mods, and a
  failure on either side falls through to the join. Config section `Mods` (`ShareMods` is on by
  default). The pure parts (`ModPathRules`, `ModCatalog`, `ModTransfer`) are covered by RuntimeSmoke.
  `ModManifest` also carries the host's values of `ModCatalog.SimulationSettings` (config entries of
  other mods that change the shared world, read through each plugin's `Config` by section and key);
  the client lists differing ones in the Mods section and the log and joins anyway. A setting goes on
  that list only when it changes what both machines simulate; reasons per mod in `MOD_COMPAT_PLAN.md`.
- **Other mods:** HarmonyX runs every prefix and every postfix even when one prefix skipped the
  original, so another mod's postfix still runs on a method our prefix skips (and the reverse).
  `InteractionSync` hooks only game-assembly button types: a mod's own `GoPointerButton` override acts
  on the local machine only. Shipyard Expansion's per-sail data (`modData["SEboatSails.{sceneIndex}"]`)
  travels in `RefitBody.ModRig` and is applied through SE's `SailDataManager` by reflection
  (`Sync/ShipyardExpansionRig.cs`). `NpcBoatPatches.PostUpdateKinematic` (last postfix) keeps a
  driven body kinematic against Towable Boats' sleep postfix. `AnchorThrow` adds Anchor
  Improvements' throw to a client's anchor drop.
  Patch sets that name another mod's types are installed from `CoopBehaviour.Start`, not `Awake`:
  the loader may start this plugin before that mod. Economy Overhaul (`Sync/EconomyOverhaulCompat.cs`,
  `ECONOMY_OVERHAUL_PLAN.md`): finance is the host's (on a client its daily loan and rental processing
  is skipped, loan/rental/repay/bankruptcy entry points refuse, `Loans.Debit` does nothing, and
  `Loans.Use` refuses cargo in the host's current financed-id list; unseal and sale guards run before
  their relays/EO trade prefixes); its trade book (`BulkTrade.Execute`) creates and destroys goods past our
  market hooks, so on a client new goods become client-authored (put into the cart through
  `ItemSync.QueueCargoAfterAuthor` once the host id lands) and destroyed ones are consumed on the
  host; plank repairs go as `DamageAction.RepairHull`/`RemoveOakum`; pole forces go through
  `InteractionSync.CaptureBodyPush`; mission modifiers travel in `MissionEntry.ModModifiers`/`ModFlags`.
  The host watches `extraSetting` of every purchasable boat once a second and sends each change as
  `BoatPurchase` with `Owned` (both ways), since mods change ownership without `PurchaseBoat`.
  Markets (`Sync/MarketSync.cs`, `Sync/EconomyOverhaulCompat.Markets.cs`): the host sends each
  ready client what changed in every port's `IslandMarket.currentSupply`, EO's capacity bonus, its
  currency state and demand events (checked once a second, everything every 20 s); a client that
  got one skips EO's `Economy.Cycle`, `Demand.Midnight` and `Fx.AdvanceDay`. A client's trade
  (`BulkTrade.Execute`, `Shopkeeper.TryToSellItem`/`TryToBuyItem` at `Priority.First`,
  `FxBooth.Confirm`) is measured from before to after and sent as a delta with a sequence number; the
  host adds it and acknowledges it in the next state, and the client shows unacknowledged deltas on
  top (the shared wallet's pattern). Price reports of other ports, EO's trader fleet and cargo
  condition stay per machine. Protocol 105 closes review R1–R4: `FinancedCargoSync` publishes new
  host loans before their item stream, market acknowledgements recompute the saved host base plus
  remaining trades, and unowned purchasable hulls reject late poses. The host immediately refreshes
  its streamed fleet on ownership changes, even when the last owned boat is removed.
  The client's Kicia altar trigger also refuses financed cargo before effects and EO's direct
  `Loans.Sacrifice` path; the host's trigger remains authoritative.
  `pwsh Tools/CompatSmoke.ps1` checks production logic with stub game/transport (see current results in СОСТОЯНИЕ.md §3);
  R6/R7 capture the shelf before EO's shop prefix and relay a sale only after the offered item's
  destruction completes. R8 runs EO's `Bottomry.BoughtBack` on the host when it applies a guest
  purchase. R9 records the shop debit before later UI work, and R10 holds a sale against the
  pending authoring request until the host id arrives. Unity/EO acceptance remains open in
  `СОСТОЯНИЕ.md` §4 item 31.
  EO 1.2.0 (protocol 107): trade offices are the host's. A client skips `Office.Midnight` and
  `OfficeFleet.Trade` and refuses converting, upgrades, treasury moves, wages, crew, the fleet and
  moving or restyling a clerk; `OfficeStateSync` sends the host's `OfficeState` (every int/float/
  string array except the per-ship `fleet*` arrays and `playerLook`, by field name) as
  `OfficeState` (130) on change, checked once a second, and after a client's world load; the client
  writes it over its copy and calls `OfficeSites.Refresh`. A unique item a client buys at an office
  is authored by the host (`NotifyClientAuthored`). `WoodPoles.ResizeAttached` sends the item's
  state from either side. `MissionEntry.ModOffered` carries the offered crate count of a mission
  cut with the board's arrows. Per machine: the fleet tab's per-ship figures, the cooperation
  book's daily count until the host's next change.
  Protocol 111 sends an explicit empty office snapshot when the host clears offices. With EO 1.2.0
  on both ends, successful shop and trade-book deletions use a reliable sale request. An early
  sale carries the authoring ID. The guest saves unconfirmed sales with the profile and replays
  them when rejoining the same world. The host stores author identities and sale receipts with
  the world save and applies each sale once.
- **Optional crew mods (protocol 108):** MusicMod and Viking Horn use reliable `CrewNote` events
  addressed by player and shared item ID. HooksHangMore sends the hook and the attached item
  or the anchor's boat index. The host commits the attachment once the carry transition has
  settled, and replays attachments after world load. Sail Damage reflects its save keys and
  damage arrays, blocks accrual on a connected guest and mirrors the host's initial state.
  FurstMate runs pet AI on the host, buffers remote poses in the pets' parent coordinates and
  relays guest interactions through the host. Postal Expansion matches the host's mod version,
  shares all three offer tabs and preserves special mail metadata in the journal. Letter drop-offs
  use the game's trigger on the guest and the mod's delivery hours on the host. Their gold and
  signed reputation changes enter the normal co-op rewards path. See `СОСТОЯНИЕ.md` for acceptance status.
- **Co-op mod corrections (protocol 110):** each sail keeps unreported and in-flight repairs
  separately from the clamped preview. The host acknowledges each accepted batch with current
  damage and handles full mends explicitly. Guests pay with their own wallet when balances
  are separate. The pending charge and wallet are saved together in the character profile
  before sending. A guest who rejoins the same world queries the result by its purchase ID.
  The host stores it in the world's mod data with the dog state. Host restarts recover the
  state present in the loaded save. A newer attachment replaces pending work for its item
  or hook. Postal acceptance preserves quantity and price. A successful drop-off reply
  starts the dialogue while the area still exists, even if the letter has left the trigger.
- **Needs FPS:** in a loaded co-op world, normalize the two food-debt refill steps by unscaled
  frame time and the movement drain by the inverse of scaled frame time. All co-op players
  consume 70% of the usual food and water through passive and movement drain, including food
  spent refilling food debt and the guest's shared-sleep catch-up. The reduction also applies
  with the FPS fix disabled.
- **Steam transport:** a second way to carry the same LiteNetLib datagrams.
  `Net/DatagramTunnel` forwards UDP between loopback sockets and an `IDatagramRelay` on its own
  thread (a world load blocks `Update` longer than the disconnect timeout); `SteamRelay` is Steam
  P2P, unreliable, channel 4703. `CoopNet.StartSteamHost` is the normal LAN host plus the tunnel,
  so a Steam host can also accept LAN clients (only with `FriendsOnly` off); `StartSteamClient(steamId)` connects LiteNetLib to the
  loopback tunnel. Nothing in `Sync/`, `Protocol` or the handshake knows which transport is in use.
  **All Steamworks types stay inside `SteamNative`/`SteamRelay` in `Net/SteamLink.cs`**; the
  `SteamLink` facade has none and guards every call, so a missing library or a stopped Steam client
  never affects LAN. Steam is started when the F8 menu is switched to Steam, or 3 s after launch
  when `Steam/UseSteam` is on or the game was started by Steam with a join request. Hosts advertise
  through rich presence (`sailwind_coop` = protocol version, and Steam's `connect` key =
  `SteamJoinLink.Format(hostId)`, which gives "Join Game"/"Invite to Game" in the Steam friends
  list); the menu lists friends in game. `CoopBehaviour.TickSteamJoin` takes a request from the
  callback or the command line and joins once the title screen (`StartMenu`) exists. Config
  section `Steam` (`FriendsOnly` is a transport admission rule, not a gameplay check: Steam friends
  only and the LAN port refuses everyone; off = anyone over Steam and LAN). The host tells a Steam
  peer from a LAN one with `DatagramTunnel.TryGetPeer(endpoint)` (`PeerSession.SteamId`); a kicked
  Steam player is blocked until the next session. `libs/steam_api64.dll` is in git and ships in both
  editions. Two copies
  on one PC share a SteamID, so this transport can only be tested on two machines.
- **Money:** two modes, the host chooses (`Economy/SharedWallet`, the Money row in the F8 SESSION
  card). *Shared*: the host's `PlayerGold.currency` is the crew's wallet. A
  client sets its own money aside (kept in its profile through `WalletSync.WalletForProfile`),
  shows the host's balance in `PlayerGold.currency` and lets the game spend against it;
  `WalletSync.Tick` sends what changed as `WalletDelta` (116) and the host answers with
  `WalletState` (115, balance plus `AckSeq`). Rewards are not divided and hand-to-hand is off.
  *Personal* (default): wallets are personal (`PlayerGold.currency` on each machine; shops, shipyard and
  cargo run against the acting player's own wallet). A delivery reward is paid by the game into
  the host's wallet and `MissionSync.ForwardReward` divides it equally between the host and the
  `Ready` clients (`Sync/WalletShare.cs`; the remainder stays with the host; reputation is not
  divided). `Sync/WalletSync.cs` lets a player give money to another: `MoneyTransfer` (113, both
  directions) is debited by the giver, routed by the host and credited by the receiver; money with
  no receiver comes back with `Returned`. The only way to give is hand to
  hand (`Runtime/MoneyHandUI.cs`): look at a player within
  3 m and hold `UI.GiveKey` (H); the wheel sets the amount, the other player presses the key to
  take. That adds `MoneyOffer` (114: Offer / Withdraw / Take), which moves no money — on `Take`
  the giver calls the same `WalletSync.Give`. The held-out hand is `EmoteId.Offer` (not in the
  wheel, held with `PlayerSync.SustainEmote`) plus a coin primitive at the right hand bone.
- **Shop shelves:** every machine builds a shop's stock for itself. A purchase sends `ShopTaken`
  (117: prefab index and the shelf place in real space, taken from `ShipItem.shopPos`/`shopArea`
  before the purchase); the other machines take the item at that place off sale. A machine that
  does not have the island loaded keeps its copy. Buying is the alternative
  action on an unsold item (`ShipItem.OnAltActivate` -> `Shopkeeper.TryToSellItem`), so
  `ItemOperationPatches` must not open an operation capture for it.
- **Belts of other players:** `Sync/PlayerBelt.cs`. A belt item is player-local (it leaves the
  shared set on `ClaimItemToBelt`) and the game's slots are a panel at the camera, so the others
  have nothing to show. Each machine sends its own player's five slots as `BeltState` (124:
  prefab index per slot, a bit per burning lamp) on change and every 5 s while not empty; the
  host sets `NetId` from the connection and relays. The receiver hangs visual copies (prefab
  meshes and materials only, no game script, collider or body) around the avatar's `Hips` bone,
  with a light for a burning lamp. On the host, an item given a new id after coming back from
  the host's belt loses its author mark (`_authoredIdentity`), and a client waits for its own
  authored item only while it still holds that request (`ItemAuthoring.Awaits`).
- **Forage spots (game 0.40):** `Sync/ForageSync.cs`. `WorldItemSpawner` spawns its item on every
  machine for itself (sold, no instance id, so `ItemSync` does not see it; the place is random
  per machine) and keeps the respawn time in `ItemSpawners.cooldowns`, which is in the save. The
  item gets an id only when taken in hand. A prefix/postfix of `WorldItemSpawner.Update` notices
  that moment; the taker sends `ForageTaken` (123: spot index and its cooldown), the others
  destroy their item of that spot unless they hold it and take the cooldown. A client's picked
  item becomes shared through `NotifyClientAuthored` (as a bought good), the host's through its
  item scan. A client writes the takes it received again after `SaveLoadManager.LoadGame`, so a
  take made while it loads the host's save is not lost.
- **Teleport to boat:** `Sync/BoatTeleport.cs`, the F8 button. Local only, no message: it boards
  through `PlayerEmbarkerNew` (private `currentBoat`, `TeleportToWalkCol`, `PlayerEmbark`) and the
  pose goes out through `PlayerSync`.
- **Avatar motion:** `Runtime/AvatarSnapshotSender.cs` captures the local observer after the
  game's pose update in `LateUpdate` and sends on an unscaled timer. A remote avatar still renders
  on its buffered timeline in `Update` after the boats and before the held objects. When extrapolation
  has run out, the avatar can blend a correction of up to 2.5 m, including after a long render frame.
  Nearby support changes carry the remaining correction into the new frame. Held poses use the
  carrier's correction for the current frame.
- **Avatar arms:** `Sync/PlayerHands.cs`. The sender's `LocalHandProbe` reads
  the held item and the held control from its own `GoPointer` and puts grip points into
  `PlayerState` (`Hands` flags plus `HandR`/`HandL`, in the frame of the transmitted player pose).
  The receiver's `AvatarArmIk` is a two-bone solver. An avatar has one `LateUpdate`
  (`AvatarRigDriver`): model offset, then gait/look bones, then arm IK — keep that order.
- **Crew sounds:** `Sync/StepSignal.cs`, `Sync/PlayerSteps.cs`. The sender packs what it stands on
  (the clip set its own `FootstepsAudio` chose) and "in water" into the free bits of
  `PlayerState.Hands` (bits 3-5 and 6; no new field, the wire format is the same). The receiver
  counts steps itself from `MoveSpeed` (`StepCadence`, the game's rule: one step per
  `stepLength`, one on landing) and plays the game's own clips on a 3D source on the avatar; the
  swim loop follows the speed in water. While the game has character control switched off
  (shop, map table, bed) the sender keeps the last ground, so getting control back is not heard
  as a landing. The volume is taken from the level the player hears its own steps at
  (`StepSounds.OwnStepGain`). No config switch.
- **Lying in bed:** `PlayerState` flag `Lying` (same byte as `Hands`) plus `LieHead`/`LieDir`, the
  head point and the head-to-feet direction in the frame of `Pos` (boat or real space). The sender takes
  them from `GameState.inBed`; the receiver's `AvatarVisualOffsetDriver` lays the model on its back.
- **Emotes:** hold `UI.EmoteKey` (G) for the wheel (`Runtime/EmoteWheelUI.cs`); `PlayerState`
  carries `Emote` plus `EmoteTick`, the host-clock start, and receivers compute the phase from it.
  Gestures are procedural hand targets relative to the shoulder (`Sync/PlayerEmotes.cs`) played by
  the same arm IK — there are no clips, so there are no dances. `Avatar/EmoteAudio.cs` plays
  `sounds/landho.wav` for "Land ho!". `EmoteId` values are wire values: append only.
- **Deck dice:** `Sync/DiceSync.cs`. The
  host owns every table: `DiceGame` is the pure rule model (RuntimeSmoke covers it), the host rolls
  with its own `System.Random`, and `DiceState` (119) is the full state of one table, sent on
  change to every `Ready` client, players or not. `DiceRequest` (118) carries the table's
  `Epoch`/`MatchId`/`TurnId`/`Revision`; a request that does not match them is dropped and the
  current state is sent back. The host does not check whose turn it is or how far the player
  stands; the local input does. A table is boat-local, or real-space on shore
  (`BoatIndex = NoBoat`); one per boat, one per player (`DiceTable.Owner`, by identity) and five in
  a world. Tables are not saved: a table lives in the session and is removed when its owner leaves
  (`DiceSync.PlayerLeft`). A `Place` from a player who already owns a table moves it (the old one
  is removed at once) unless dice are being rolled at it; only the owner folds a table away, also
  a paused one. `Runtime/DiceTableView.cs` builds the table from
  primitives and plays the roll from the host clock (`DiceClock` stops with `timeScale` 0);
  `Runtime/DiceTableInput.cs` is called from the `GoPointer.LateUpdate` prefix and returns
  "consumed" to skip the game's handler. There is no F8 panel: `DiceTableInput.Choose` maps the cup,
  the dice and the score board to actions for the pick-up and use buttons, and the gesture wheel
  sector `EmoteId.DiceTable` (local, never sent) starts placement. `Runtime/DiceCloseUp.cs` is the
  close view (local, nothing sent): it moves the main camera above the table only between
  `Camera.onPreCull` and `onPostRender`, frees the cursor like the gesture wheel does, and
  `DiceTableInput.HandleCloseUp` maps the left and right mouse buttons to the same choices. It
  opens on `Join` (`Dice/CloseUp`) or with `UI/DiceViewKey` (T) and closes in `Update`, before the
  game's Escape menu reads the mouse look in `LateUpdate`. Stakes (`Dice/AllowStakes`, on
  by default) are paid by each loser from its own machine with `WalletSync.Give` when it sees the
  party end. The table materials are copies of an item material of the game (`Dice/LitMaterials`).
  Completed matches go to `DiceJournal`: its id is kept in
  the save itself (`GameState.modData["SailwindCoop.DiceJournal"]`), the file is
  `coop_dice_<id>.dat` next to the saves, and `DiceSavePatches` drops the id on a load and on a
  new game because the game does not clear `modData`. `Hello` carries `PlayerGuid`
  (`BepInEx/config/SailwindCoop/player-identity.txt`); a second copy from the same install folder
  gets an identity of its connection (`CoopNet.SharesPlayerGuid`), decided once for a connection
  (`DiceSync.IdentityOf` keeps it until the player leaves).
  Protocol 97 adds a lobby-selectable Liar's Dice mode (`LiarDiceGame`): five dice per seat,
  no wild ones, ascending quantity/face bids, challenge loses one die, last survivor wins.
  Full hands exist only in the host's game model. Every outbound dice state uses
  `DiceSync.SendState`, including baseline and request replies: only the recipient's hand is
  sent before a challenge, all hands afterwards. `DiceTableInput.Liar.cs` owns bid/peek controls;
  `DiceTableView.Liar.cs` owns the cup row and hand/reveal meshes. The journal writes schema 4
  (game mode) and reads schemas 3 and 4. This new mode has not been verified in game.
  Visual maintenance: Runtime/DiceVisuals.cs owns the 8 × 128 px face atlas and procedural
  meshes, DiceBoard.cs the score board, and DiceGameMaterials.cs the game-material source.
  Keep atlas indexing consistent with its 1024 × 128 layout. Roll/cup animation, writing/coin
  sounds, placement feedback and the close-up HUD are local presentation; changing them alone
  does not require wire changes. Close-up input has separate normal/Liar paths; verify both.
  The completed visual plan is archived under
  docs/archive/2026-11-08-cleanup/DICE_IMPROVEMENT_PLAN.md; its Protocol 96 claims describe
  that historical change, not the current protocol.
- **Knocked over by an item:** `Sync/PlayerKnockdown.cs`. The hit player's own machine decides,
  from the poses of free items it already has (`ItemSync.CollectLoose`), measuring each item's
  velocity in its parent's frame; no message. The fallen player is a capsule `Rigidbody` next to
  the `CharacterController`, in its parent (the walk copy on a boat); control is off, the
  controller follows the capsule, the camera is tilted only between `onPreCull` and
  `onPostRender`. On the fall the GoPointer prefix (`InputScopePatches`) drops the held item and
  releases the held button once, then skips the pointer while the player lies. Space gets up. Others see the `Lying` flag (`PlayerSync.LocalLying`).
  Config `Gameplay/KnockdownByItems`.
- **Co-op in the game's menu:** `Runtime/GameMenuUI.cs`. The game's menus are 3D
  `StartMenuButton`s (a `GoPointerButton`) with `TextMesh` labels, clicked through
  `MouseButtonPointer` (`OnActivate()`). Copies of them, with the script swapped for
  `CoopMenuButton`, make a **Co-op** button under Settings on the title screen, one in the right
  column of the Escape menu (shown only in a world) and the page `coop UI`, a copy of the recovery
  page: text, five rows, a primary button, back. The page is refilled from `CoopMenuUI`'s snapshot
  (`RefreshView`) and acts through its methods, so hosting and joining stay in one place; it
  answers the join's questions itself, and an automatic `CoopMenuUI.Visible = true` does not open
  the F8 window over it (`PageOpen`). A prefix of `StartMenu.LateUpdate` turns Escape on the world
  page into closing the page and the game's menu (`SettingsToGame`). In a world without a
  session the page lists games to join as on the title screen, with Host this world as its
  primary button. While an address is typed, `GameInput.GetKeyDown/GetKey/GetKeyUp` (in
  Oculus.VR, hooked by name) read nothing, so F and C do not act in the game. Places on the page were
  measured in game 0.39; a menu built differently leaves only the F8 window.
- **Version mismatch:** builds with different protocol numbers cannot play together, and the join
  failure says which side must update. `CoopNet.OnConnectionRequest` reads the connection key
  itself; for another protocol it refuses with `RejectMsg` `ProtocolMismatch`
  (`ModVersions.ProtocolMismatch`), which clients since 0.4.3 read from the refusal. A host up to
  0.4.3 refuses in silence, and the client then says that the versions most likely differ. Same
  protocol, different mod version: no message, only a muted line in the F8 crew card
  (`SessionRosterMsg` carries the versions after the members, read only when bytes remain). No
  internet request and no install, the same in both editions.
- **LAN search:** `Net/LanDiscovery.cs`. Two datagrams
  outside a connection (LiteNetLib unconnected packets, `LanBeacon`), not `MsgType` messages:
  builds with different protocols see each other, and the reply carries the host's protocol and
  mod version. The host answers from its game socket (`CoopNet.OnLanQuery`; only a host enables
  unconnected packets; `Server/AnnounceOnLan`; never while `FriendsOnly`; at most 20 replies a
  second). `LanSearch` has its own socket and runs only while the LAN join screen of F8 is open
  (`CoopMenuUI.WantsLanSearch`, ticked from `CoopBehaviour.Update`): it asks the limited
  broadcast, each adapter's directed broadcast and loopback every 1.5 s; `LanHostList` keeps one
  row per host session for 5 s. Join goes through the address field.
- **Player needs in Debug:** F8 → Settings → Debug opens `Runtime/DebugPanel.cs` when
  `[Debug] EnableDebugPanel` is enabled. In a loaded world, the Food, Sleep and Water sliders
  show the current bars and set the selected bar to 0–100 for the local player.
- **Wrong game version:** `CoopBehaviour` names game classes in its fields, so on a game that
  lacks one Unity does not create it and reports that only in `Player.log`. `Plugin.Awake` adds
  it through `Runtime/StartupNotice.cs`, which then shows an on-screen notice and logs the reason
  past the logging switch. `StartupNotice` must not name a game class;
  `StartupText.SupportedGame` is the game version to change on a game update.
- **Patches:** every patch set is installed through `PatchHealth.Install`; a failed set disables
  only its feature. Only `Input origin` is mandatory. Patch bodies are wrapped by `PatchGuard`, except
  patches that run every frame or per item (`Update`, `LateUpdate`, `ExtraFixedUpdate`…): its
  lambda and delegates are garbage on every call (they were most of the mod's garbage), so those
  use a plain `try/catch` that reports through a never-throwing `Report` or `PatchGuard.Fail`.
- **Scene searches:** avoid repeated full-scene searches in hot paths. Historical tests measured
  roughly 1.2 ms per search in their scene; that is not a universal cost. Ship items (lights
  included) come from `Sync/ShipItemRegistry.cs` (a `ShipItem.Awake`
  postfix; `Snapshot`/`Active` return exactly the items on active objects, and fall back to the
  search when the hook is missing; its check against that search, `Verify`, is compiled only in
  Debug). Temporary checks and measurements go under `#if DEBUG`, never into Release. `BoatLocator` caches the hulls of each registry boat and
  rereads only ownership once a second; call `BoatLocator.Invalidate()` when a boat's hulls
  change. Wind orbs bind from the `WindTotemOrb.Update` prefix; the full orb search runs every
  5 s. Historical measurements and completed optimization stages: `ОПТИМИЗАЦИЯ.md` (not in git).

**Verification.** Smoke checks of 2026-11-07 (0.8.0, Protocol 106): ProtocolSmoke 92/138/33226,
RuntimeSmoke 155/155, CompatSmoke 49/49; both editions 0 errors / 0 warnings. Every game acceptance is recorded in
`СОСТОЯНИЕ.md` §3, with the list of what has not been verified in game — check it before claiming
a subsystem works.
