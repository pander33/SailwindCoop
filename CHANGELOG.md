# Changelog

All notable user-facing changes are documented in this file.

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
