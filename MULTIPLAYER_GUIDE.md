# Sailwind Co-op Multiplayer Guide

This guide explains how co-op gameplay works for actions that behave differently from single-player Sailwind.

The short rule is: the host owns the world, while each player keeps their own character progress where possible.

Use the same build on every machine. The current build (0.4.1) uses **protocol 89**, requires Sailwind 0.39, and cannot connect to
earlier protocol builds. Most features added since 0.1.6 still await end-to-end in-game verification.

## Editions

The mod comes in two editions that use the same protocol and can play together. The **Thunderstore
edition** (<https://thunderstore.io/c/sailwind/p/pander33/SailwindCoop/>) plays over LAN, VPN or Steam and
compares mods when joining. The **full edition** adds
downloading missing mods from the host. The title of the status overlay shows the edition,
for example `Sailwind Co-op 0.4.1 (Thunderstore)`. Sections below that apply to one edition say so.

## Session Model

- The host loads the real world save.
- Joining players should stay in the main menu, open the F8 co-op menu, enter the host IP, and press `Join`.
- The host streams the current world save to the client.
- The client loads that world into a dedicated co-op slot.
- The guest keeps a local co-op profile for personal character progress such as money, reputation, needs, known prices, missions, journal data, and personal belt inventory.

Profile and received-world writes keep the previous readable file as a `.bak` backup. If the profile
cannot be read, its backup is tried; if both fail, joining stops. Different or unverifiable game save
versions also stop the join before writing/loading the world.

Do not treat the guest's normal single-player save as the active co-op world. In co-op, the host world is authoritative.

The client writes the received world into the co-op save slot (`CoopSaveSlot`, slot 5 by default) and
**overwrites whatever was in it**. Point that setting at a slot you do not use, and back up saves you care
about before a long session.

Joining a host means loading a save file that host sends you, so **only join people you trust** — the same
caution you would apply to any save file someone hands you. Every machine must also run the same mod
version: the network protocol changes between releases and mismatched builds refuse to connect.

## Economy

The host chooses between two money models in the co-op menu (F8, **Money** row of the SESSION
card): **Personal** (the default) or **Shared**. The choice can be changed during a session.

**Shared.** The whole crew uses the host's money. Every purchase, sale, shipyard order and reward
of any player changes the host's wallet, and everyone sees the same balance. A guest's own money
is not touched: it is kept in the guest's profile and comes back when the guest leaves or when the
host switches to Personal. Mission rewards are not divided, and money cannot be handed from player
to player. Two players spending at the same moment can take the balance below zero.

**Personal.** Each player has a wallet of their own:

- Each player pays with their own wallet.
- Each player receives money into their own wallet.
- Buying an item creates or claims a shared physical item so both players can see and use it.
- Selling an item removes the shared physical item from the world.
- Currency exchange and trade UI browsing are local UI actions.
- You can hand money to a player standing next to you. Look at them and hold `H`: your avatar holds
  out a hand with a coin, and a note on screen shows the amount. The mouse wheel changes the amount,
  the middle mouse button changes the currency. The other player looks at you and presses `H` to
  take it. Release `H` to lower your hand without giving anything. The key is `UI.GiveKey` in the
  config file; change it if `H` is bound to something else. Money can only be handed over in
  person; there is no way to send it to a player who is somewhere else.

Example: if the guest buys a good at a market, the guest pays locally. The bought item is then shared through item sync so the host can see it and interact with it.

## Missions

Missions are shared through the host world, with personal reward handling.

- The host's mission journal is the authoritative shared journal.
- Clients can view mission-related UI locally.
- Mission accept/abandon actions are sent to the host.
- A delivery reward is divided equally between the players who are in the world, each part going to that player's own wallet. What does not divide stays with the host. Reputation is given to everyone in full.
- Mission offers can differ between machines, so the mod sends the mission details instead of relying on a local offer index.

If mission UI looks different between host and client, trust the shared journal and the host-side result.

## Cargo And Port Storage

Cargo uses local money, shared physical membership.

- Loading or unloading cargo runs against the acting player's own wallet.
- The result is mirrored as item membership in the cargo carrier.
- A cargo item unloaded by the client should appear in the client's hand and be visible to the host.
- Cargo items are tracked as shared items once they are outside personal inventory.

Known practical advice: after unloading cargo, wait a moment for the item to settle in hand before throwing or placing it.

## Items

Most sold physical `ShipItem` objects are synchronized.

Supported item behavior includes:

- pickup and drop;
- carrying items in hand;
- throwing;
- placing or hanging items;
- moving items while the boat is underway;
- item amount and health for many consumables/tools;
- crates and crate unsealing;
- cargo carrier membership;
- fishing rod cast visuals, rod hook state, and caught fish authoring;
- personal belt transitions.

Personal belt inventory is player-local. When a shared item is put into a belt slot, it becomes part of that player's local co-op profile. When it is taken back out, the mod re-authors it into the shared world.

## Food, Drink, Consumables, And Tools

- Eating or drinking affects the acting player's own needs.
- The consumed shared item is removed or updated for everyone.
- Bottles and similar items sync their amount/health when changed.
- Hammer/nailing actions are forwarded so the target item's nailed state is shared.
- Some held tool behavior is supported, but new unusual item types may still need dedicated sync handling.

## Fishing

Fishing is partly local and partly shared.

- The rod holder's bobber/line visuals are sent to other players.
- The actual catch is authored through the host so the fish becomes a shared item.
- Hook attach/detach state is synchronized.

If a bobber looks slightly off during latency spikes, the catch result should still converge through item sync.

## Boat Controls

The host remains authoritative for boat physics.

Shared controls include:

- sail ropes and winches;
- reefing;
- steering wheel and rudder command;
- anchor rope length;
- anchor pose and dropped/raised state;
- several shared deck toggles;
- pushing interactions that affect boat physics.

Clients can interact with many controls, but the host applies the authoritative result and sends it back. Brief visual delay is normal on slower connections.

## Mooring And Anchor

Mooring is synchronized as a host-authoritative state.

- Unmooring a rope on the client sends a request to the host.
- Mooring to a dock is matched by dock position.
- Rope length changes are mirrored.
- The anchor's position and set state are driven by the host.

Edge cases to watch:

- joining while the host is already moored;
- unmooring immediately after a client finishes loading;
- dropping or raising anchor near docks or shallow water.

If a rope looks wrong after a join, try unmooring and mooring again after both players are fully loaded.

## Damage, Water, And Repairs

Boat damage is host-authoritative.

- Hull damage, water level, oakum, water intake, and sunk state are mirrored from the host.
- Client bilge-pump use is sent as a held action to the host.
- Oakum repair and water bailing are sent to the host as damage requests.
- The host applies the real damage state and broadcasts snapshots back.

For testing, keep the status overlay open and watch the damage line while using the pump or repairing.

## Sleep, Time, And Pausing

Sleep and time advance are shared-world actions.

- The host controls world time.
- Time is skipped only when **every player is in a bed** (a paid tavern night counts) and at least
  one of them is tired enough to fall asleep. Then every screen goes dark and the world runs fast,
  as in the single-player game. Everyone recovers, and everyone gets hungry and thirsty at the
  same rate.
- When the shared sleep ends, everyone is put out of bed.
- A player who lies down alone, or collapses from exhaustion, sleeps by himself. His screen goes
  dark, his sleep need recovers at the usual sleep speed, and hunger and thirst stand still.
  The world keeps its normal pace for the others. He stays in bed, so the rest of the crew can
  still join him and start the shared sleep. Any key gets him up and ends this sleep.
- A tavern night taken alone restores the player at once but does not skip to the morning.
- During a shared sleep nobody can get out of bed; it ends by itself. A collision, running
  aground or water coming into the hull wakes the whole crew, as in the single-player game.

Clients should not expect independent time skipping.

Pausing works the same way. When the host opens the game menu, the host's world stops — and so does every
guest: character control is taken away for the duration and the screen says `Host paused the game`. Guests
can still look around. Control comes back the moment the host resumes, and also if the connection drops, so
a lost host can never leave anyone frozen. The same freeze applies while the host is streaming its world to
someone who is joining.

## Sea, Waves, And Weather

The sea is host-authoritative and identical on every machine.

- Wave shape, size and direction, wind, time of day and the weather cycle all run on the host's clock.
- The water is drawn at the same instant as the boat, so hull and wave stay in step even on a slow link.
- The sea stops while the host is paused, and resumes with it.

If the water ever looks different on one machine, press **Dump water state** (F8 menu, **Settings**) on both machines
at the same moment and compare the two `debug/water-*.txt` files — they are plain `key = value` text meant to
be diffed.

## Shipyard And Boat Purchases

Shipyard browsing is local UI, but world-changing results must converge through the host/save model.

- Boat purchases are mirrored so both peers know the boat was bought.
- More complex shipyard customization should be treated carefully and verified in-game.

Avoid making rapid shipyard changes during unstable connections.

## Embark, Disembark, And Multiple Boats

Player position is synchronized in either boat-local or world-real coordinates.

- Walking on the same boat is the best-supported case.
- Disembarking to land is supported through player pose sync.
- Multiple boats are partially supported by boat indexes.
- Some control systems are still safest when both players are using the same active boat.
- **Teleport to boat** in the F8 menu puts you back on a deck if you fell overboard or the boat left
  without you. It picks the boat you last stood on, otherwise the host's boat, and places you where
  you last stood, next to the crew, or amidships. A rope end, anchor or winch in your hand is
  released first; an ordinary item stays in your hand.

Best current practice: use one main boat for normal co-op sailing, and test dinghy or multi-boat workflows before relying on them in a long session.

## Avatars And Skins

- Open the F8 co-op menu, open **Settings** and press `Avatar`.
- Skin changes are sent during the session.
- The included `avatar.bundle` is the default fallback.
- NPC-style skins can appear if the game has loaded suitable NPC models on that machine.

### Gestures

- Hold `G` during a session to open the gesture wheel, move the mouse to a gesture and release.
  Release in the middle, or press `Esc`, to cancel.
- Gestures: Wave, Land ho!, Point, Come here, Applause, Shrug, Salute, Hooray. "Land ho!" and
  "Point" aim where you are looking; "Land ho!" also shouts, and others hear it from your position.
- Walking ends a gesture. You do not see your own gesture: the game is first person.
- The key is `UI.EmoteKey` in the config file. Change it if `G` is bound to something else.
- A hint with the key appears once, the first time the wheel is available. The F8 menu shows the
  key as well.

If a selected NPC skin is not available on the other machine yet, the remote player may temporarily appear with the default avatar until the skin can be built.

## Saving And Guest Progress

- The host world save is the source of truth for the shared world.
- The client loads a streamed copy into a dedicated co-op slot.
- The guest's personal co-op profile is saved locally on disconnect or shutdown.
- Disconnect from the F8 menu when possible so the guest profile is saved cleanly.

Before a long session, make a normal backup of important Sailwind saves.

## Recommended Play Flow

1. Host loads the save and waits until the world is fully playable.
2. Host opens F8 and presses `Host`.
3. Client stays in the main menu, opens F8, enters the host IP, and presses `Join`.
4. Wait for the client to finish loading the host world.
5. Use the status overlay if something looks wrong.
6. Disconnect through F8 when finished.

## Playing Over Steam

Both editions.

The F8 menu has a **LAN / Steam** switch in the **Connection** section. LAN works as before: the
guest types the host's IP address. Steam mode needs no IP address, port forwarding or VPN.

- Steam must be running on every PC, and each player needs their own Steam account that owns
  Sailwind. Two copies of the game on one PC cannot connect to each other over Steam.
- **Host:** load the world, switch to **Steam**, press **Host**.
- **Guest:** stay in the main menu, switch to **Steam**. Friends who are in Sailwind are listed;
  press **Join** next to the one marked `hosting`. A host who is not listed can be joined by typing
  their Steam ID (17 digits) into **Host ID**; the host copies it with **Copy ID**.
- **Join Game in Steam:** a friend who hosts over Steam also has **Join Game** in your Steam friends
  list, and the host can send you **Invite to Game** from there. Be at the main menu: a request
  that arrives while you are in a world or hosting is refused with a notice. If the game is not
  running, Steam starts it and the join begins when the title screen appears. For a running game
  this works only while the F8 menu is in Steam mode.
- **Friends only / Anyone** (host): by default only your Steam friends can connect, and joining by
  IP is refused. **Anyone** lets in whoever knows your Steam ID, and LAN players can join the same
  session by IP. The **Who can join** row is in the F8 menu on the Steam tab before hosting and in
  the session block while hosting; a change applies at once, and players already in stay.
- A Steam player removed with **Kick** stays out until you host again.
- A guest who is not on a **Friends only** host's friends list gets no answer and sees the
  connection time out.
- `hosting, other version` means that friend runs a different build of the mod; update both sides.
- The first connection can take up to half a minute while Steam finds a route.
- If the menu says Steam is unavailable, start Steam and press **Retry Steam**. LAN play does not
  need Steam at all.

## Other Mods

Right after connecting, and before the world is sent, the host tells the joining player which other
BepInEx mods it runs. Nothing happens when both sides have the same ones.

If the joining player lacks some of the host's mods, or has another version, the F8 menu opens with a
**Mods** section listing the differences and its buttons. The Thunderstore edition has no
**Download** button: install the missing mods yourself, restart the game and join again. A host
running the Thunderstore edition does not offer downloads either.

- **Download** (full edition) — fetch the missing mods from the host. Available while the host has
  **Sharing: ON** in its F8 menu (`ShareMods`, on by default). The files are checked against the host's
  list and placed into `BepInEx/plugins`; the session then ends and **the game must be restarted** before
  joining again. Existing files and folders are never overwritten.
- **Join anyway** — join with your current mods. Things those mods change may not match the host.
- **Cancel** — do not join.

Mods are programs: a downloaded mod runs on your PC with your rights. **Download only from a host you
trust.** The checksum shown in the list guards against a damaged transfer, not against a dishonest host.

Details:

- Only missing mods are downloaded. A mod you already have in another version is reported, but you
  update it yourself.
- A downloaded folder contains `.coop-installed.json`, which lists what was installed and from which host.
  Delete the folder to remove the mod.
- `ModSyncExclude` in the config lists plugin GUIDs that are never compared or shared. By default it
  holds the XUnity translator; add personal or host-only mods there.
- Hosts (full edition): sharing is on by default, so anyone who joins can download your listed mods. Press
  **Sharing** under **Settings** in the F8 menu to turn it off, and share only mods whose authors allow redistribution.

## Crew Status And Session Access

- The F8 **Crew** list shows whether each player is waiting, comparing mods, receiving/loading the world, ready, or failed.
- Ping and the last known boat are shown beside each player.
- The host may close the session to new joins without removing anyone already connected.
- Removing a guest uses a two-step confirmation and sends that guest a readable reason.
- Reconnect is a full fresh join and is only allowed from the main menu.
- **Export report** (under **Settings**) creates a single diagnostic text file that can be attached to a bug report.

## Reporting A Problem

Routine logging is off by default; serious errors still use a limited log budget. Press F8 → **Logging** to switch full diagnostics on,
reproduce the problem, then attach `BepInEx/LogOutput.log` and say which mod version each machine was
running. Turning logging on afterwards does not help — the log will not contain the moment it broke.

## Known Limitations

- This is an early mod. Some edge cases still require in-game verification.
- The host world is authoritative; independent guest world changes are not merged back.
- Unusual item subclasses may need specific sync support.
- Multi-boat control workflows are less mature than same-boat sailing.
- High latency can cause temporary visual delay even when the authoritative result eventually converges.
