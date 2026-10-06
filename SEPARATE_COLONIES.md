# ONI Together — Separate Colonies (fork)

Fork of [ONI Together](https://github.com/Lyraedan/Oxygen_Not_Included_Together) by Lyraedan & contributors (MIT).
Adds **separate colonies on the same asteroid**: every player founds their own base (Printing Pod + 3 dupes),
dupes only work for their own colony, other colonies can't build/dig inside your territory or give orders to
your buildings/dupes.

> Disable the original "Oxygen Not Included Together" mod while using this fork (same assembly name).
> Host and all clients must run the exact same build (protocol fingerprint check).

## How to play
1. Mod options → "0) Separate colonies": on by default. Optional: *Asteroid size (%)* for new games, *Territory radius*.
2. Host starts/loads a game and hosts as usual. The host's existing base becomes colony #1.
3. A joining player starts as **spectator** (yellow banner). Open the panel (**Ctrl+Shift+C** or the *Colonies*
   button) → *Found my colony — pick a spot* → click a floor spot ≥ 2×radius+4 cells from other colonies.
4. The host spawns the new base and hard-syncs the world (everyone reloads once).

## What is separated (v0.1.0 MVP)
- Ownership of dupes, buildings, blueprints, dig/mop markers, printed dupes, fabricator products, robots
- Chores: dupes only take errands of their own colony (or world-owned objects); no fetching from other colonies' storage
- Territory around each Printing Pod: no building/digging by other colonies
- Guards on deconstruct / priority / sweep / harvest / capture / disinfect / cancel / empty pipe / disconnect / copy settings
- UI: other colonies' side screens & action buttons hidden; Jobs/Consumables/Vitals/Skills list only your dupes
- Printing Pod: a client's print is delivered at the client's own pod

## Shared for now (planned)
- Research tree and the Printing Pod timer (one timer, whoever accepts first)
- Schedules, trading between colonies

## Implementation notes
- `ONI_Together/Colonies/*` — registry, context/guards, save data, founding, patches, panel, packets
- Host-authoritative: client packets are dispatched on the host inside the sender's colony context
  (`ColonyNet.CurrentSender`, set by the transports and `HostBroadcastPacket`)
- Ownership is saved by `KPrefabID.InstanceID` (clients get it through the save) and replicated live by
  `NetworkIdentity.NetId` (`ColonyOwnersPacket`)

## Offline build (no NuGet)
`Directory.Build.props.user` with `OfflineBuild=true` compiles net471 against the game's own mscorlib/System dlls,
using `lib/PLib.dll` (built from peterhaneve/ONIMods) and `lib/RiptideNetworking.dll` (built from source), and a
pre-publicized `PublicisedAssembly/`. Normal Windows builds (NuGet, ILRepack) are unchanged.
