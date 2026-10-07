# Changelog

All notable user-facing changes are documented in this file.

## [0.4.1] - 2026-10-07

**Everyone must update.** The protocol is still 89, but players with different versions of the mod
cannot connect to each other.

### Changed

- **The Thunderstore edition can play over Steam.** It now has the **LAN / Steam** switch, the
  friends list and **Join Game** in Steam, the same as the full edition, and its archive carries
  `Facepunch.Steamworks.Win64.dll` and `steam_api64.dll`. The only difference left between the
  editions is that the full edition can download missing mods from the host.

## [0.4.0] - 2026-10-07

**Everyone must update.** Protocol 89: this build cannot play with 0.3.1 or earlier.

### Added

- **Hand money to another player.** Look at a crewmate standing next to you and hold `H`: your
  avatar holds out a hand with a coin. The mouse wheel changes the amount, the middle mouse button
  changes the currency. The other player looks at you and presses `H` to take it; release the key
  to cancel. The key is `UI.GiveKey` in the config file.

- **Shared wallet.** The host can switch **Money** to **Shared** in the co-op menu (F8) while
  hosting: the whole crew then spends and earns the host's money and sees one balance. A guest's
  own money is kept and comes back when the guest leaves or the host switches back to
  **Personal**.

### Fixed

- An item bought in a shop stayed on the shelf for the other players, who could buy it again.
- A guest who bought an elixir, snake oil, oakum or a lamp kept it only for themselves: the host
  never saw the item, and for the guest it hung in the air after being put down.

### Changed

- **Mission rewards are divided.** A delivery reward is split equally between the players who are
  in the world; what does not divide stays with the host. Before, every player received the whole
  reward. Reputation is still given to everyone in full.

## [0.3.1] - 2026-10-07

### Added

- **Two editions.** The Thunderstore edition plays over LAN or VPN and compares mods when joining;
  it has no Steam mode and does not download mods. The full edition has both. The editions use the
  same protocol and can play together. The status overlay title shows the edition.
- **Join through Steam itself** (full edition). A friend who hosts over Steam has **Join Game** in
  your Steam friends list, and the host can send **Invite to Game**. Do it from the main menu; if
  the game is not running, Steam starts it and the join begins at the title screen.

### Changed

- With the menu left in Steam mode, the mod now connects to Steam a few seconds after the game
  starts instead of when the menu is first opened. Steam can pass a join request only to a game
  that is connected to it.
- **Friends only** on a Steam host now also closes the LAN port: only Steam friends can join.
  **Anyone** accepts players by Steam ID and LAN players on the UDP port. The button applies to a
  session that is already running; players who are in stay.
- A Steam player removed with **Kick** cannot rejoin until the host starts a new session.
- Sessions over Steam wait 12 seconds without packets before dropping a player, instead of 5.
- A locked or full Steam session is no longer shown to friends as one they can join.

### Fixed

- A Steam host could stop hearing a player after a connection failure until hosting was restarted.
- Joining over Steam now fails at once when Steam reports that the host is not in the game, does
  not own it or is offline, instead of waiting half a minute.
- Quitting the game during a Steam session is seen by the others at once, not after a timeout.
- A guest refused by a **Friends only** host is told that this is a possible reason.

### Network

- **Everyone must update.** The protocol is still `86`, but `0.3.1` and `0.3.0` refuse each other
  at the handshake because the mod version differs.

Not yet verified in game: both editions after the split, and every Steam change above (it needs
two PCs with different Steam accounts).

## [0.3.0] - 2026-10-06

### Added

- **Other mods are compared when joining.** Before the world is sent, the host tells the joining
  player which other BepInEx mods it runs. With the same mods nothing changes. Otherwise the F8 menu
  opens a **Mods** section listing what is missing or different, with **Download**, **Join anyway**
  and **Cancel**.
- **Missing mods can be downloaded from the host.** The files are checked against the host's list
  (size and SHA-256) and placed into `BepInEx/plugins`; the game must then be restarted. Existing
  files and folders are never overwritten, and a mod you already have in another version is only
  reported. Mods run code on your PC: download only from a host you trust.
- Host sharing is **on by default**; the **Sharing** button in the F8 menu (`ShareMods`) turns it
  off. `AllowModDownload` disables downloading on the client, and `ModSyncExclude` lists plugin
  GUIDs that are never compared or shared (the XUnity translator by default).
- The crew list shows `Mods` while a joining player is comparing mods.
- **Play over Steam without an IP address.** The F8 menu has a **LAN / Steam** switch. In Steam
  mode the host is reachable by Steam ID, and a joining player sees Steam friends who are in the
  game, with **Join** next to a friend who is hosting. No port forwarding or VPN is needed. Steam
  must be running, and each player needs their own Steam account that owns Sailwind.
- A Steam host still accepts LAN players on its UDP port. LAN play itself is unchanged and does not
  need Steam.
- By default a Steam host accepts only Steam friends (`Steam/FriendsOnly`, the **Friends only**
  button).
- The mod now ships two more files that must sit next to `SailwindCoop.dll`:
  `Facepunch.Steamworks.Win64.dll` and `steam_api64.dll`.
- **Teleport to boat.** A button in the F8 menu puts you back on the deck: where you last stood,
  next to the crew, or amidships. For a player who fell overboard or was left ashore.

### Changed

- **The F8 menu was redesigned.** It now shows only the session (connection settings, or the
  running session), the crew and **Teleport to boat**. Avatar, mod sharing, the status overlay,
  logging, reports and the debug panel moved under a **Settings** section that is collapsed by
  default. The window is as tall as its content.
- **Joining over a slow connection pauses the host for less time.** The world sent to a joining
  player is now compressed to about a quarter of its size.

### Fixed

- **Goods no longer look like mission cargo on a guest.** A good the host got during the session
  (bought at a market, or cargo of a mission in a slot other than the first) showed the destination
  and due date of the first mission on a guest's screen. The guest now gets the mission slot of
  each good from the host.
- Goods created with the debug item spawn are ordinary goods, not mission cargo.
- **Other players' arms follow what they do.** An avatar now reaches for the item it carries and
  puts its hands on the winch, wheel or rope the player is holding. Not yet verified in game.
- **Gesture wheel.** Hold `G` in a session, pick a gesture with the mouse and release: Wave,
  Land ho!, Point, Come here, Applause, Shrug, Salute, Hooray. "Land ho!" shouts, and other players
  hear it from where you stand. The key is `UI.EmoteKey`; it is shown in the F8 menu and in a
  one-time on-screen hint. Not yet verified in game.
- **Sleep waits for the whole crew.** Time is skipped only when every player is in a bed. A player
  who lies down alone, or collapses from exhaustion, sleeps by himself: his screen goes dark, he
  recovers at the usual sleep speed and does not get hungry or thirsty, while the world keeps
  going for the others. Not yet verified in game.
- Fixed: during a shared sleep guests lost food and water about 16 times slower than the host.
- **Players lie in their beds.** The avatar of a player who is in a bed lies on its back on that
  bed instead of standing in it. Not yet verified in game.

### Network

- **Everyone must update.** The wire protocol moved from `80` to `86`; `0.2.x` and `0.3.0` refuse
  to connect to each other.

Not yet verified in game: the comparison step, the menu section and the download; the Steam
connection (it needs two PCs with different Steam accounts).

## [0.2.1] - 2026-10-05

### Changed

- NPC avatar gait follows movement speed relative to the deck or ground, with smoother
  transitions and a limited running cadence.
- NPC avatars now use procedural crouch, look and turn poses. Crouching bends the legs
  more deeply and smoothly lowers the visual model along Y without moving the network root.

### Fixed

- Leg movement uses model-space axes instead of assuming the NPC bones' local axes.
- NPC skin discovery includes inactive NPCs in loaded scenes and checks for skinned meshes
  with bones instead of rejecting every hierarchy containing a node named `combiner`.

### Network

- Wire protocol remains `80`. These animation changes add no fields or messages to player
  synchronization; bone poses and crouch height are calculated locally.

## [0.2.0] - 2026-10-05

### Changed

- **Everyone must update.** The network protocol moved from `57` to `80`; `0.1.6` and `0.2.0`
  refuse to connect to each other.
- **Sailwind 0.39 is now required.** The mod uses classes unavailable in older game builds.
- Every owned boat has independent controls, anchors, mooring lines, pumps, hatches and hull damage.
  Boats stay active on the host while any crewmate is nearby, even when the host is far away.
- The host's mission journal is mirrored by guests without mixing in missions from their profiles.
- Controls, anchors, mooring lines, hull damage, storms, items, instruments and missions are sent
  only when they change. Joining players request the current values once.

### Added

- Guests can pick up, carry, pay out and drop anchors; everyone sees the anchor in its carrier's hands.
- Guests can start shared sleep for the whole crew.
- Guest shipyard orders for sails, parts, cleaning and repair reach the host and every crewmate.
  The ordering player pays once; local shipyard previews stay out of the shared world and save.
- Food preservation, spoilage, cooking progress, soup and kettle contents, fuel and stove slots
  travel with items. Consumption and use of food, drinks, knives, elixirs, oakum, oil and candles
  take effect for everyone at the same moment.
- Pocket watch lids, quadrants, scroll pages, chip log lines and fishing rod state are visible
  to other players, including the line, bobber, rod bend and hooked fish.
- Chart drawings are shared, survive late joins and persist in the host's world save.
  Folding and unfolding maps and furniture is shared.
- Hull scrubbing removes the same dirt for everyone, stroke by stroke.
- Wind totem orbs are visible in another player's hands; guest casts change weather for the crew.
- Sailwind 0.39 tobacco packs can be cut by guests, house doors are shared with late arrivals,
  and sleeping in house beds skips time as in single player.

### Stability

- Failed game hook groups disable only the affected feature and name it in an on-screen notice.
- Character profiles and received world saves use atomic writes with backups. Readable backups
  are recovered; unreadable profiles and incompatible save versions stop the join with a message.
- Packets are accepted only from handshaken peers and in the expected direction. Snapshots too
  large for one datagram are delivered reliably.
- Unanswered requests are abandoned after five seconds instead of blocking the object indefinitely.
- Lamps retain their identity when item lists are reordered; late packets cannot resurrect destroyed items.
- Fixed guest winches failing on every control update due to an inactive steering wheel check.
- Fixed guests being unable to release mooring ropes or length coils and pick them up after dropping.

### Reporting problems

Enable **F8 → Logging** before reproducing a problem and attach `BepInEx/LogOutput.log`
plus an **Export report** file.

## [0.1.6] - 2026-09-22

### Added

- **AI ships are now the same ships for everyone.** Until now every machine sailed its own private
  fleet: a trader the host was passing simply was not there for the guest, and a wreck the host had
  rammed was still afloat on the other screen. AI ships now run on the host and are mirrored to
  everyone, sails and all.
- AI ships stay alive around *any* crewmate, not just the host. The game normally freezes a ship once
  it is far from the player, which meant a guest who sailed ahead met motionless hulls; the range is
  now measured to whichever player is nearest.
- The F8 menu now shows the whole crew with loading/ready state, individual ping and current boat.
- Hosts can close the session to new players and remove a connected guest. Rejected or removed players
  receive a readable reason instead of a generic timeout.
- Short co-op notifications report joins, readiness, departures, session access, anchor and sleep events.
- A previously connected guest can reconnect from the main menu with one button; reconnect still performs
  a full safe world join rather than trying to merge into an already loaded client world.
- **Export report** writes one bounded diagnostic text file with protocol/session/crew/join state and recent
  warnings, even when normal logging was off.

### Fixed

- A mission cargo delivered by a guest now counts for the whole crew: the host's mission journal advances,
  and the payout reaches the host and every guest. Before, only the delivering guest's own screen counted it.
- A guest standing near the host while the host delivered cargo is no longer paid twice for it.
- A guest joining a host who owns several boats no longer sees two boats stacked in one place. When the
  host had sailed far away from one of their boats, the guest's copy of that boat was dragged onto the
  host's current ship (and players or items could end up on the wrong hull). Boats are now matched by
  their save identity instead of their order in a scan, and a guest's boats stay untouched until the
  host's world has finished loading.
- A guest's money and reputation are now kept when the connection drops (host quits, network timeout).
  Before, they were saved only on an F8 disconnect or when quitting the game while still connected.
- Hosting or joining again without disconnecting first now closes the previous session cleanly, saving
  the guest's progress and freeing any join that was still queued.
- Cargo carried by a guest should no longer stay behind on the host's screen at the spot where it was
  picked up. If it still happens, the log (with logging on) now records why.

### Notes

- The wire protocol moved from `53` to `57`, so every machine must be updated together again.
- Trading vessels that carry goods between ports are not covered yet — they are part of the economy
  rather than the sea traffic, and their prices are already settled by the host.

## [0.1.5] - 2026-08-13

A stability release: joins that finish cleanly, boats and items that stay where they belong, a sea that
is the same sea on every machine, and a mod that is quiet unless you ask it not to be.

### Added

- The co-op menu now reports problems that previously only reached the log file: a joining player who
  never finished loading, an unreadable or missing host save, a client rejected because the host has no
  world loaded, and a world that was un-paused automatically after a join. If something goes wrong
  during a join, the reason is on screen.
- Logging can be switched on and off from the F8 menu at any time, without restarting the game.

### Changed

- **Logging is now off by default.** A normal session writes nothing to `BepInEx/LogOutput.log`. Turn it
  on from the co-op menu (F8 → Logging) when you need to diagnose something or report a bug; the choice
  is remembered in the config file. Serious errors are still written even with logging off — a few lines,
  never a flood — so a broken installation can never look identical to a healthy one.
- Debug tools are hidden unless explicitly enabled in the config, so a public build no longer exposes
  them by accident. The menu says which mode it is in.
- Joins are now handled one at a time. If several players connect at once, each waits its turn for the
  world transfer instead of the transfers overlapping; the queue releases itself if a transfer stalls, so
  one bad join no longer blocks everyone behind it.
- Fewer scene-wide searches per frame while sailing, which removes a source of stutter — and with it the
  frame hitches that could drop a player through the deck.

### Fixed

- **Fixed boats sinking into the waves on the client.** The ocean ran on each machine's own clock, and the
  shape of a wave is a function of that clock — so the host placed the boat on a crest at a spot where the
  client was drawing a trough, and a passing wave swallowed the hull. The client's sea now runs on the
  host's clock, and so do the size and direction of the waves, which each machine used to decide for
  itself on its own free-running weather cycle. The water is also drawn at the same instant as the boat
  itself (the hull is rendered slightly in the past to smooth out network jitter; the water used to be
  drawn at the present moment) and no longer lags behind by the connection's latency.
- **Fixed the client's sea running on while the host had the game paused**, and fixed the water tearing
  across the screen when the host un-paused with the boat below the surface.
- **Fixed other players walking on the spot while the boat sails.** A crewmate standing still on deck
  was shown marching (and turning, when the ship came about), because the mod worked out whether someone
  was walking from how fast their position changed — and on a moving ship, the ship's own motion counts.
  Walking and turning are now measured at the source, against the deck the player is actually standing on.
- **The client is now held still while the host has the game paused.** Opening the host's menu stops the
  host's world, but a guest could still walk around it, step off the ship or fall in the water. Guests are
  now frozen for the duration and told why on screen; they are released the moment the host resumes, or if
  the connection drops.
- **Fixed a joining player being attached to the wrong boat.** While the world was still spawning boats,
  a boat could briefly answer to another boat's number, and a guest could be teleported onto — or have
  their view locked to — a hull that was not theirs. Boat numbering is now withheld until it is proven
  stable on both machines.
- **Fixed items jumping to the player.** Items carried on a deck could snap to whoever was looking at
  them for a fraction of a second after the boat set changed (most visibly right after buying a boat).
- **Fixed the world staying frozen after a player joined.** If the host opened the game menu during the
  join freeze and closed it afterwards, the world could end up paused with nothing left to un-pause it,
  which needed killing the game. This is now detected and undone automatically.
- **Fixed a join un-pausing the game behind an open settings menu.** Joining while the host already had
  the game paused no longer resumes the world, and no longer changes physics settings owned by that menu.
- **Fixed a guest being left behind when the client's boat snaps into place on join.** The player is now
  carried with the deck in the same step, instead of the boat moving out from under them.
- Fixed mooring ropes and deck controls going unresponsive for about three seconds after a boat was
  bought or the boat set otherwise changed.
- Fixed remote players sometimes taking up to half a second to appear after a world finished loading,
  including after the host reloads a save or leaves a shipyard.
- Fixed a stalled world transfer holding the host frozen until the two-minute safety timeout, and fixed a
  transfer cancelled by a disconnect interfering with the next player's join.
- Fixed failures inside the mod's own error handling going unreported, which could leave a subsystem
  silently doing nothing for a whole session.

### Notes

- The wire protocol moved from `47` to `53`. **Every machine must be updated together** — `0.1.3` and
  `0.1.5` cannot connect to each other.
- The co-op menu gained a **Dump water state** button. Press it on both machines at the same moment and
  compare the two `debug/water-*.txt` files if the sea ever looks different on one of them.
- The water fixes and the two player fixes are verified on two machines: the client's ocean clock now
  matches the host's to a few milliseconds, the wave field matches exactly, crewmates stand still on a
  sailing deck, and a guest is held in place while the host has the game paused. The rest of this release
  is verified by code review only — please report anything that behaves differently from `0.1.3` with
  logging switched on.

## [0.1.3] - 2026-07-07

### feat(sync): Enhance player synchronization and item handling

- Improved player synchronization on boats by stabilizing local positions and adding a fallback mechanism for boat detection.
- Introduced a new PatchHealth reporting system to monitor the success of various synchronization patches across different game systems (Save, Shop, Shipyard, Sleep).
- Added comprehensive item synchronization patches to ensure consistent item interactions (pickup, drop, eat, nail, etc.) across clients and hosts.
- Implemented a protocol smoke test to validate message types and ensure proper serialization/deserialization of network messages.

## [0.1.2] - 2026-07-06

### Added

- Added the F8 Sailwind Co-op menu for hosting, joining, disconnecting, avatar selection, diagnostics, and debug tools.
- Added a high-contrast dark menu backdrop/window background for readability in bright scenes and the main menu.

### Changed

- Replaced separate F6/F7/F8/F9/F10/F11 co-op hotkeys with menu-driven controls.
- Moved the debug panel to the left side of the screen so it does not overlap the co-op menu.
- The co-op menu captures cursor input while open and closes companion Avatar/Debug panels when closed.

## [0.1.1] - 2026-07-06

### Added

- Added character skin switching through the avatar selection menu.

## [0.1.0] - 2026-07-04

### Added

- Added host save streaming for joining clients: the host sends the current world save in reliable chunks, and the client loads it into a dedicated co-op slot.
- Added a separate client co-op profile for persistent character progress across sessions: currency, reputation, needs, known prices, missions, journal data, and personal belt inventory.
- Added join-pause handling while the host save is being transferred and loaded by the client.
- Added wave state synchronization so client sea surface and host-authoritative boat position stay aligned.
- Added fishing rod cast visual sync, including bobber position, line length, and rod bend.
- Added client support for throwing, placing, hanging, loading/unloading, and inventory movement of synced items.
- Added debug tools for island teleport and expanded coordinate diagnostics in the overlay.

### Changed

- Expanded item synchronization to cover more runtime state: attached/placed items, crates, cargo, inventory slots, sold goods, market-spawned goods, rod hooks, and lamp hooks.
- Improved economy, mission, and journal synchronization, including richer mission reward data and client-side trade flow handling.
- Updated the wire protocol from `39` to `47` and added message types `SaveSnapshotBegin`, `SaveSnapshotChunk`, `SaveSnapshotEnd`, `ClientWorldLoaded`, and `RodState`.
- Updated documentation and project status notes for the new save/profile, item, economy, and debug workflows.

### Fixed

- Fixed client purchase of trade goods in port.
- Fixed host interaction lockouts after a client unloaded items from cargo.
- Fixed client-side unloading behavior.
- Fixed moving items while the boat is underway.
- Fixed client cross-session inventory handling.
- Fixed client fastener/attachment handling.
- Fixed grill/brazier visuals turning into a white cube after pickup/drop replication.
- Fixed several fishing-related synchronization issues.

### Notes

- Client world loading now depends on the host's streamed save and overwrites the configured local co-op slot on the client.
- All peers must use the same mod build because the protocol changed after `0.1.0`.

## [0.0.1] - 2026-06-29

### Added

- Initial public LAN co-op release for Sailwind.
- Host/join/disconnect controls and diagnostic overlay.
- LAN and VPN/tunneling play support through LiteNetLib UDP.
- Host-authoritative boat, environment, controls, anchor, mooring, interaction, and player state synchronization.
- Default remote player avatar bundle: `avatar.bundle`.
- Basic avatar customization by replacing the included `avatar.bundle` with a compatible Unity 2019 Windows x64 AssetBundle.

### Notes

- This is an early release. Save-game progress is owned by the host and is not synced as separate guest progress.
- All players should use the same mod version and install `SailwindCoop.dll`, `LiteNetLib.dll`, and `avatar.bundle` together.
