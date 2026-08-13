# Sailwind Co-op Multiplayer Guide

This guide explains how co-op gameplay works for actions that behave differently from single-player Sailwind.

The short rule is: the host owns the world, while each player keeps their own character progress where possible.

## Session Model

- The host loads the real world save.
- Joining players should stay in the main menu, open the F8 co-op menu, enter the host IP, and press `Join`.
- The host streams the current world save to the client.
- The client loads that world into a dedicated co-op slot.
- The guest keeps a local co-op profile for personal character progress such as money, reputation, needs, known prices, missions, journal data, and personal belt inventory.

Do not treat the guest's normal single-player save as the active co-op world. In co-op, the host world is authoritative.

The client writes the received world into the co-op save slot (`CoopSaveSlot`, slot 5 by default) and
**overwrites whatever was in it**. Point that setting at a slot you do not use, and back up saves you care
about before a long session.

Joining a host means loading a save file that host sends you, so **only join people you trust** — the same
caution you would apply to any save file someone hands you. Every machine must also run the same mod
version: the network protocol changes between releases and mismatched builds refuse to connect.

## Economy

Economy uses a separate-money model.

- Each player pays with their own wallet.
- Each player receives money into their own wallet.
- Buying an item creates or claims a shared physical item so both players can see and use it.
- Selling an item removes the shared physical item from the world.
- Currency exchange and trade UI browsing are local UI actions.

Example: if the guest buys a good at a market, the guest pays locally. The bought item is then shared through item sync so the host can see it and interact with it.

## Missions

Missions are shared through the host world, with personal reward handling.

- The host's mission journal is the authoritative shared journal.
- Clients can view mission-related UI locally.
- Mission accept/abandon actions are sent to the host.
- Mission rewards are mirrored so the client receives the payout in their own wallet when appropriate.
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
- Host sleep is mirrored to clients with blackout/control lock behavior.
- Client stamina recovery during host sleep is handled separately.

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

If the water ever looks different on one machine, press **Dump water state** in the F8 menu on both machines
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

Best current practice: use one main boat for normal co-op sailing, and test dinghy or multi-boat workflows before relying on them in a long session.

## Avatars And Skins

- Open the F8 co-op menu and press `Avatar`.
- Skin changes are sent during the session.
- The included `avatar.bundle` is the default fallback.
- NPC-style skins can appear if the game has loaded suitable NPC models on that machine.

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

## Reporting A Problem

Logging is off by default, so a normal session writes nothing. Press F8 → **Logging** to switch it on,
reproduce the problem, then attach `BepInEx/LogOutput.log` and say which mod version each machine was
running. Turning logging on afterwards does not help — the log will not contain the moment it broke.

## Known Limitations

- This is an early mod. Some edge cases still require in-game verification.
- The host world is authoritative; independent guest world changes are not merged back.
- Unusual item subclasses may need specific sync support.
- Multi-boat control workflows are less mature than same-boat sailing.
- High latency can cause temporary visual delay even when the authoritative result eventually converges.
