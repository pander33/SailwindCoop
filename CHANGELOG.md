# Changelog

All notable user-facing changes are documented in this file.

## [0.2.0] - 2026-07-04

### Added

- Added host save streaming for joining clients: the host sends the current world save in reliable chunks, and the client loads it into a dedicated co-op slot.
- Added a separate client co-op profile for persistent character progress across sessions: currency, reputation, needs, known prices, missions, journal data, and personal belt inventory.
- Added join-pause handling while the host save is being transferred and loaded by the client.
- Added wave state synchronization so client sea surface and host-authoritative boat position stay aligned.
- Added fishing rod cast visual sync, including bobber position, line length, and rod bend.
- Added client support for throwing, placing, hanging, loading/unloading, and inventory movement of synced items.
- Added F7 debug tools for island teleport and expanded coordinate diagnostics in the overlay.

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

## [0.1.0] - 2026-06-29

### Added

- Initial public LAN co-op release for Sailwind.
- Host/join/disconnect hotkeys and diagnostic overlay.
- LAN and VPN/tunneling play support through LiteNetLib UDP.
- Host-authoritative boat, environment, controls, anchor, mooring, interaction, and player state synchronization.
- Default remote player avatar bundle: `avatar.bundle`.
- Basic avatar customization by replacing the included `avatar.bundle` with a compatible Unity 2019 Windows x64 AssetBundle.

### Notes

- This is an early release. Save-game progress is owned by the host and is not synced as separate guest progress.
- All players should use the same mod version and install `SailwindCoop.dll`, `LiteNetLib.dll`, and `avatar.bundle` together.
