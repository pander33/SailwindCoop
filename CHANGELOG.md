# Changelog

All notable user-facing changes are documented in this file.

## [Unreleased]

### Changed

- Boat controls, anchors, mooring, pumps, damage actions and deck interactions now carry a stable boat
  address and a layout signature. Each owned boat keeps independent state and restores its original
  client physics on disconnect. Protocol is now **63**; all players need this build.
- Host fully trusts connected client actions and broadcasts the result. Only the sender handshake,
  matching PlayerState NetId, object/index bounds/null checks and packet format checks remain.
  Do not add gameplay eligibility or numeric request checks. Remote pump holds expire without renewal.
- Owned boats near a guest remain active on the host even when its own player is beyond the horizon
  activation range, so their authoritative controls and physics can continue running.
- Character profiles and received world saves use atomic replacement with backups. A readable profile
  backup is recovered automatically; unreadable profiles and mismatched save versions abort the join.
- Interaction Harmony callbacks contain exceptions, including failures inside diagnostic reporting.
- Truncated packets are rejected even when LiteNetLib's backing buffer contains additional bytes.

- Mouse and sticky winch/wheel grabs and quick-release controls are supported. Host snapshots
  do not overwrite the rope being operated locally.
- Anchors bind through their rope joint after the game reparents them. Guest pickup, held pose,
  payout and drop now travel through the host; local holds and pending drops resist stale snapshots.
  Remote anchor physics stays slaved, while free host anchors retain vanilla physics. In-game
  validation of this carrying fix is pending.
- Fold/unfold of maps and other foldable items now updates remote meshes, details and collider
  sizes through absolute setters. Loaded folded meshes remain folded in initial snapshots; local
  holds resist old echoes. Fold state is sent only when the shape changes. In-game validation pending.
- Hatches send event targets only after interaction and retry them after an active animation.
  A separate one-time snapshot provides the initial state for a joining client or new hull.
- Initial/periodic states and item poses/echoes leave last-action diagnostics untouched. Mooring
  distinguishes interaction replies from snapshots; initial item manifests go only to the joining
  peer. Anchor physics polling no longer announces an invented player action.

- Mooring state acknowledges the latest local request and waits until the guest releases its rope
  or length adjuster. Unchanged snapshots leave logs and last-action diagnostics untouched.
  Missing docks are reported; client targets wait for loading, and unavailable host states acknowledge
  requests without fabricating an unmoor. This dock failure path still needs in-game reproduction.
- Fleet diagnostics display the actual state of each boat; control count mismatches report each part.
- Joining from an already loaded world reports the existing error before overwriting the co-op slot.

Validation: Release build, protocol smoke and 23 filesystem/patch/hatch/mooring scenarios pass on Windows
.NET Framework. Multi-boat gameplay and `File.Replace` under Unity Mono still need in-game verification.
An intermediate runtime run failed the existing File.Replace recovery scenario; a subsequent run
passed all 23 cases. The intermittent failure remains unexplained.

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
