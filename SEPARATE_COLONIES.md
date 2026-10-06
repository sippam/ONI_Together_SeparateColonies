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
   Before placing, pick your 3 starting duplicants in the panel (interests, traits, attributes; *Reroll* per slot,
   Standard/Bionic filter when Bionic Booster Pack is active). They are sent to the host with the request.
4. The host spawns the new base with exactly those dupes and hard-syncs the world (everyone reloads once).

## Rules (v0.2.0) — Mod options "0) Separate colonies" or the in-game panel (host)
| Rule | Default | Effect |
|---|---|---|
| Territory protection | on | no building/digging by other colonies inside the circle (off: min. distance between pods drops to 10) |
| Building protection | on | no deconstruct/priority/settings/cancel on other colonies' buildings & dupes, their side screens hidden |
| Separate errands | on | dupes only take their own colony's errands, no fetching from other colonies' storage |
| Separate research | on | one research tree per colony (host-authoritative, client trees sent by `ColonyResearchPacket`) |
| Shared dupe priorities | off | Priorities/Consumables/Vitals/Skills screens show & edit every colony's dupes |
| Separate Printing Pod | on | own printing timer per colony (host ticks foreign timers, `ColonyImmigrationPacket` to the owner), own printable choices, printing/rejecting only affects that colony |
| Help other colonies when idle | on | with separate errands: own colony's errands first (`ChoreConsumer.ChooseChore` reorder: idle < other colonies < own), help others only when nothing of their own is left |
| Cancel only your own orders | on | cancel tool skips other colonies' markers/blueprints even when building protection is off |
| Extra water geysers (new games) | 2 | water geysers / cool steam vents added once to a freshly generated world |
| Water geyser near each new colony | on | one water geyser 20–40 cells from a newly founded colony |
| Separate schedules | on | schedule owner per index (`ColonySchedules`, host re-broadcasts owners in the registry); each player sees only their schedules; dupes are kept on their colony's schedules; a new colony gets "&lt;name&gt; schedule" |

The host's rules are used for the session (sent to clients, stored in the save). Switching research back to
shared hard-syncs everyone to the host's tree.

## v0.8.1 notes
- Placement overlay while founding a colony: every cell of the start base is previewed under the cursor — green free, yellow dug out / plants removed, magenta existing (unowned) building that stays, red blocked — plus outlines of the base's buildings (Printing Pod in cyan), the new colony's territory circle and a red keep-out ring around every existing colony. The tooltip at the top of the screen shows the reason and a cell count summary.

## v0.8.0 notes
- Joining players now pick their 3 starting Duplicants with the game's own new-game cards (reroll, interest filter, Standard/Bionic filter, name edit). Press "Found my colony — choose duplicants" in the colony panel, press Embark, then click the map to place the base.
- Implemented by reusing the Printing Pod screen (ImmigrantScreen) in starter mode; it is restored afterwards. If that screen is unavailable the old IMGUI picker is used.

## v0.7.0 notes
- **Background structure sync** (`StructureSync`): host sends a hash per 16x16 block of all buildings, blueprints and plants every 5 s; a client block that still differs after 2.5 s is requested and only that block is fixed (missing/extra/stale buildings and blueprints rebuilt or removed, plants re-sent with ONI Together's PlantLifecyclePacket). Replaces most hard syncs.
- **Share buildings, machines & food** rule: dupes of any colony may operate/use finished buildings and fetch/eat from any storage; commanding dupes stays separate.
- **Colony name as dupe prefix** for new starting and printed dupes; owners can rename their colony in the panel.

## v0.6.0 notes
- New colonies get the host asteroid's start-base template stamped at the chosen spot (area cleared, Printing Pod + starting room), then the chosen dupes + rations. Needs the game unpaused for a moment (the stamp runs over sim ticks).
- Relayed tool packets are wrapped in `ColonyActingPacket` (acting colony), so clients tag new markers with the same owner as the host and apply the same guards.
- Background structural sweep (100k cells per sync tick): dug/built/collapsed cells and liquid changes outside every viewport reach clients within ~15 s even on a 300% asteroid (the old 32x32 rotating scan needed ~20 min).
- Priority / consumable packets fall back to the dupe's name when the NetId differs between machines (fixes priorities snapping back to 5).

## Performance (v0.3.0)
- ConduitFlowSyncer walks only cells with a pipe (was: every grid cell every 1.5 s — ~800k dictionary lookups on a 300% asteroid)
- WorldStateSyncer scans at most 40k cells per viewport per tick (rotating bands) — zoomed-out cameras on big maps no longer scan the whole world each tick
- ONI Together info logs no longer go to Player.log unless "Verbose multiplayer log" is on; warnings capped at 10/s
- Ownership safety snapshot every 30 s (was 15 s)
The game's own simulation still scales with the number of cells (300% = 9x cells); 150–200% is the sweet spot.

## What is separated (v0.1.0 MVP)
- Ownership of dupes, buildings, blueprints, dig/mop markers, printed dupes, fabricator products, robots
- Chores: dupes only take errands of their own colony (or world-owned objects); no fetching from other colonies' storage
- Territory around each Printing Pod: no building/digging by other colonies
- Guards on deconstruct / priority / sweep / harvest / capture / disinfect / cancel / empty pipe / disconnect / copy settings
- UI: other colonies' side screens & action buttons hidden; Jobs/Consumables/Vitals/Skills list only your dupes
- Printing Pod: a client's print is delivered at the client's own pod

## Not yet
- Trading between colonies, colony takeover requests
- Research / Printing Pod separation only applies while hosting (offline play of the save uses the host's tree/timer)

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
