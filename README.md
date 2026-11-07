# ⚓ Sailwind LAN/Steam Co-op

[![Version](https://img.shields.io/github/v/release/pander33/SailwindCoop?label=version&color=blue)](https://github.com/pander33/SailwindCoop/releases/latest)
[![Game](https://img.shields.io/badge/game-Sailwind%200.40-1b2838?logo=steam&logoColor=white)](https://store.steampowered.com/app/1764530/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.x-orange)](https://github.com/BepInEx/BepInEx/releases)
[![Players](https://img.shields.io/badge/players-2--5-brightgreen)](#features)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

A co-op multiplayer mod for Sailwind: sail one world together with up to four friends, over a
local network, a VPN, or Steam.

**Current Version:** 0.8.0
**Wire Protocol:** 106. Every machine needs a build with protocol 106.
**Requirements:** BepInEx 5.x, Sailwind 0.40 (0.40.1 supported)

The mod is in early development (v0.8.0). Expect bugs, and please report them.

## Features

- **One shared world:** the host's save is sent to every guest; boats, items, weather, waves and
  the time of day run on the host, so everyone sees the same sea under the same hull.
- **Sail together:** sails, winches, rudder, anchor, mooring, hatches, pumps and repairs work
  from any player's hands.
- **Shared items:** cargo, food, cooking, lamps, crates, fishing, instruments and foraged
  mushrooms and coconuts are the same for the whole crew.
- **Missions and money:** delivery rewards are split, or the crew shares one wallet; money can
  be handed from player to player (hold **H**).
- **Sleep:** rest alone in a bed, or skip time together when the whole crew is asleep.
- **Crewmates you can see:** avatars that walk, swim, lie in bed and hold what they carry, with
  footsteps, belt items and burning lanterns; a gesture wheel (hold **G**).
- **Deck dice:** a folding dice table for 2–5 players, with optional coin stakes.
- **Join from the game's own menu:** **Co-op** on the title screen joins, **Co-op** in the
  Escape menu hosts; games on your local network are listed by themselves.

Guest character progress (needs, belt, money) is kept in a local co-op profile on each guest's
machine. Gameplay details are in [MULTIPLAYER_GUIDE.md](MULTIPLAYER_GUIDE.md).

## Installation

<!--full-->
The mod comes in two editions that play together. The **Thunderstore edition** installs through
a mod manager: <https://thunderstore.io/c/sailwind/p/pander33/SailwindCoop/>. The **full
edition** (GitHub) can also download mods the host has and you are missing. The title of the
status overlay (F8 -> Settings -> Show Status) shows the edition, for example
`Sailwind Co-op 0.8.0 (Thunderstore)`.

Manual install of the full edition:

1. Close the game. Download `SailwindCoop-<version>.zip` from
   [GitHub Releases](https://github.com/pander33/SailwindCoop/releases) (the ZIP asset, not
   **Source code**). It includes BepInEx 5.4.23.5 for Windows x64.
2. In Steam, right-click **Sailwind** -> **Manage** -> **Browse local files** to open the folder
   with `Sailwind.exe`.
3. Extract the **contents** of the ZIP into that folder, merging folders and replacing files.
   Not into `BepInEx/plugins/`, and not into a new folder named after the ZIP.
4. Check that `winhttp.dll` is next to `Sailwind.exe` and the mod is at
   `BepInEx/plugins/SailwindCoop/SailwindCoop.dll`.
5. Start the game and press **F8** for the co-op menu.

**Updating:** extract the new archive the same way. Saves and configs are not touched; back up a
custom `avatar.bundle` first. To keep a BepInEx you already have, copy only the
`BepInEx/plugins/SailwindCoop` folder from the archive.

Do not mix a manual install with a mod manager profile.
<!--/full-->
<!--thunderstore:
1. Install **SailwindCoop** and its dependencies through your Thunderstore mod manager.
2. Start Sailwind from the modded profile and press **F8** for the co-op menu.
3. For a manual install, first install BepInEx 5 for Windows x64 into the folder with
   `Sailwind.exe`, then extract this package into `BepInEx/plugins/SailwindCoop/`.
-->

## How to Play

**Host:** load your save, press **Escape** -> **Co-op** -> **Host this world** (or **F8** ->
**Host a game**). Give your friends your IP address (**Copy address**), or switch the menu to
**Steam** first so Steam friends can join without one.

**Join:** on the title screen choose **Co-op**. A game on your local network appears under
**Games on this network**; otherwise type the host's address (`ip` or `ip:port`) and press
**Join**. The host's world is sent to you and loaded into the co-op save slot (slot 5 by
default; **a world of your own in that slot is replaced**). Joining from a loaded world saves it
first and asks before leaving.

**Leave:** **Escape** -> **Co-op** -> **Disconnect**, or **F8** -> **Disconnect**.

The **Crew** section of the menu shows each player's state, ping and boat; the host can lock
the session or remove a guest. **Teleport to boat** puts you back on deck if you fell overboard.

**Finding your IP:** on the same network, run `ipconfig` and take the "IPv4 Address" (usually
`192.168.x.x`). Over a VPN (ZeroTier, Hamachi, Tailscale and the like), use the address the VPN
gives you. Over the internet without a VPN, the host must forward UDP port 7777. Playing over
Steam needs no address: see [Playing Over Steam](MULTIPLAYER_GUIDE.md#playing-over-steam).

## Configuration

`BepInEx/config/com.sailwind.coop.cfg`, created on the first start; every setting is described
in the file. The ones most often changed:

| Setting | Default | What it does |
|---|---|---|
| `Network.PlayerName` | Player | Your name in the crew list |
| `Network.Port` | 7777 | UDP port the host listens on |
| `Server.MaxClients` | 4 | Guests allowed besides the host (1–4) |
| `Save.CoopSaveSlot` | 5 | Slot a guest writes the host's world into. **Its save is overwritten.** |
| `Economy.SharedWallet` | false | Host: the crew shares the host's wallet instead of personal wallets |
| `Gameplay.KnockdownByItems` | true | Items thrown by other players knock you down (Space to get up) |
| `Steam.UseSteam` | false | The co-op menu opens in Steam mode |
| `Steam.FriendsOnly` | false | Steam host: only Steam friends may join, and the LAN port is closed |
| `UI.MenuKey` / `EmoteKey` / `GiveKey` | F8 / G / H | Co-op menu, gesture wheel, handing money |

## Troubleshooting

- **Friends can't connect:** all machines need the same protocol (the version line above). Allow
  the game through the firewall on the host; over the internet without a VPN, forward UDP 7777.
- **The mod does not start:** check that BepInEx is installed (a `BepInEx/LogOutput.log` appears
  after a start) and that `SailwindCoop.dll` is in `BepInEx/plugins/SailwindCoop/`. If a notice
  appears on screen at start, it says what is wrong (most often a game version other than 0.40).
- **Other players have no model:** `avatar.bundle` must be next to `SailwindCoop.dll`.
- **Lag:** prefer a wired connection; the host's machine matters most.
- **Reporting a bug:** press **F8** -> **Settings** -> **Logging** before reproducing the
  problem, then attach `BepInEx/LogOutput.log` (or **Export report**) and say which version each
  machine runs. Issues: <https://github.com/pander33/SailwindCoop/issues>.

## Thanks

- **jankykev** — the fork [jankykev/SailwindCoop](https://github.com/jankykev/SailwindCoop),
  whose reports and fixes led to many of the crate, hook, sail, wave and mission fixes in this mod.

## License

MIT, see [LICENSE](LICENSE).
