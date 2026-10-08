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
| Steam vents for the host | 1 | Cool Steam Vents stamped from the game's own POI (poi/jungle/geyser_steam, poi/hotmarsh/geyser_steam) in the jungle / marsh biome nearest to the host, like worldgen; added once per save (existing saves too), raising it adds the difference |
| Separate schedules | on | schedule owner per index (`ColonySchedules`, host re-broadcasts owners in the registry); each player sees only their schedules; dupes are kept on their colony's schedules; a new colony gets "&lt;name&gt; schedule" |

The host's rules are used for the session (sent to clients, stored in the save). Switching research back to
shared hard-syncs everyone to the host's tree.

## v0.10.5 notes
- The "Colonies" button can be dragged anywhere (click still opens the panel, right-click puts it back in the default spot). Position is remembered per computer.
- The panel has a minimize button (— / □) next to X: only the header stays on screen. Window position and minimized state are remembered.

## v0.10.4 notes
- Background storage sync: every 4 s the host sends a content hash per player-facing storage (lockers, fridges, ration boxes, feeders... anything with a filter list); a client whose copy still differs after 2 s asks for that storage and gets the host's full content list. Fixes "host sees it full, others see it empty" (ONI Together only pushed storages on change, to viewers, unreliably, and the client re-request never ran; instant auto-store also stores items before clients know them). Uses the "background structure sync" option.
- Storage rebuilds on clients now fire OnStorageChange so meters and the side screen update.

## v0.10.3 notes
- Critters (and other entities with EntityPositionHandler) frozen mid-air on clients: positions are only sent for cells in a client's view, and the client's "no update for 2 s, ask the host" check compared local game time with the host's Unix timestamp, so it never fired after the first packet. A critter that hopped/moved while out of view stayed at its old spot. Now the client tracks when the last update arrived and re-requests stale visible entities.

## v0.10.2 notes
- A joined player's "Sync my world" no longer shows a sync/progress screen on the host: the client's download progress packets (meant for players joining) put the host's overlay up. The client doesn't send them during a personal sync and the host ignores them for players it is serving one.

## v0.10.1 notes
- Host hard sync no longer drops a request while another hard sync is still running (e.g. a colony founded right after an auto/manual sync): it is queued and runs after the current one.
- Host "Perform Hard Sync" button: 60 s cooldown instead of once per cycle (on a fresh world / long cycles it looked stuck on "Already hard synced this cycle"). A hard sync marked in progress for over 10 min is treated as interrupted.
- "Has players" checks on the host counted the host itself: with a single joined player the post-founding hard sync, structure/item/food background sync and some upstream syncers (`SessionHasPlayers`) were skipped. Now they count joined players only.

## v0.10.0 notes
- Built-in logistics (the former separate "Instant Logistics" mod, which did not cooperate with ONI Together): options "Auto-store new items", "Instant delivery from storage", "Logistics max distance" in "0) Separate colonies". Runs only where the world is simulated (single player / host); clients get the result through the normal storage and item sync. With separate colonies, new items go to their colony's storages (territory owner, else nearest base) and deliveries follow the colony fetch rules. Remove the standalone InstantLogistics mod.

## v0.9.9 notes
- Joined players' sync button ("Sync my world (only me)"): it used to run the host's hard sync on the client, which never finished and left the client stuck on "Already hard synced this cycle" forever (only the host resets that flag). Now it asks the host for its world and reloads it for that player only — same full transfer as a hard sync, nobody else is paused or sees the sync screen, the host's once-per-cycle hard sync is untouched. 60 s cooldown, gives up after 5 min.
- New host option "Auto Hard Sync Every N Cycles" (1-100): with "Hard Sync On Cycle Start" on, the automatic hard sync runs every N-th cycle instead of every cycle.

## v0.9.8 notes
- Item sync (background, part of "Background structure sync"): a client's Player.log on a cycle-30 save showed ~500 failed item lookups per minute (pickup / store / eat packets for NetIds the client didn't have), so loose items drifted further apart every cycle. The host now sends a hash per 16x16 block of its loose items (NetId + prefab + cell) every 6 s; blocks that still differ after 3 s are requested and fixed: the client adopts the host NetId for its own copy, moves strays, removes phantoms, spawns what is missing and corrects mass/temperature. Stored/carried items, critters and duplicants are left alone.
- Crash fixes on clients: Operational.IsActive patch threw on destroyed buildings during OnNewDay (aborting the new-day event), FallingObjectPacket during world load (Grid not ready), ToggleEffect / ToggleAnimOverride on missing entities. The pending pickup id sets are now bounded.

## v0.9.7 notes
- Storage / bottle emptier filter checkboxes no longer flip back: the host relayed every building-config change back to the client that made it (the relay overwrote the sender id), and late echoes of earlier clicks undid newer ones. The original sender is now kept, so a client never re-applies its own change. Storage filters are sent as the full accepted set (idempotent) instead of add/remove steps.

## v0.9.6 notes
- Farm tiles / planters on clients no longer say "No seed selected" (with a red seed preview and a seed delivery errand) while a plant grows on them: synced plants were sometimes spawned beside the plot instead of inside it. Clients now link such plants into their plot every 2 s (exact occupant cell, so stacked farm rows don't swap plants), and the plant sync also finds the plot one cell below/above the plant.

## v0.9.5 notes
- Toilets on clients no longer blink between "needs emptying / unclogging" and ready: every state packet rebuilt the storage, which fired the toilet state machine's storage transitions. Outhouse and lavatory now take their predicates (needs dirt, waste removed, water full, contaminated) and their top-level state from the host.
- Errand priority (1-9 / !!) set by a client now sticks: ONI Together only synced priorities of objects with a NetId (finished buildings), so dig orders, blueprints, mop/sweep markers etc. went back to 5. New ColonyPriorityPacket syncs by cell + prefab (client -> host under the client's colony -> all clients, batched per frame). Background structure sync now also compares and fixes priorities.

## v0.9.4 notes
- Host water is now a Cool Steam Vent built from the game's own worldgen structures: the POI templates poi/jungle/geyser_steam (ToxicJungle) and poi/hotmarsh/geyser_steam (BoggyMarsh) are stamped as-is (rock shell, cave, plants, vent), only in the biome worldgen allows for that POI (worldTemplateRules zoneTypes) with at least 70% of the template inside it. Of the valid spots the one nearest to the host is used, at least 30 cells (or territory + 12) from every colony. No distance option any more: the biome decides, like worldgen.
- "Extra steam vents (new games)" uses the same POIs at random valid spots.
- Saves from v0.9.3 and older (plain water geyser) get one steam vent too; the old geyser stays.

## v0.9.3 notes
- Host water: only Water Geysers (clean water vents), placed 45-70 cells from the host by default (new option "Water vent distance from the host"), only in biomes where water vents form (biomes this world already has water vents in + temperate/wet biomes: Sandstone, Marsh, Ocean, Forest, Swamp...).
- Natural caves instead of a rectangular pocket: wobbly ellipse with a rough edge, carved only inside rock (not into open areas or buildings), filled with the surrounding gas at the local temperature, floor made of the surrounding rock. The same applies to "Extra water vents (new games)".

## v0.9.2 notes
- Thai support (works with any Thai translation mod): the Colonies window, banners, placement messages and host messages are shown in Thai when the game runs in Thai (detected from the language code, or from Thai text in the game's own strings). IMGUI windows use an OS font with Thai glyphs (Leelawadee UI / Tahoma).
- translations/th.po: Thai for all ONI Together strings (multiplayer menus, lobby, overlays, chat) and the "0) Separate colonies" mod options, which now use STRINGS keys so they can be translated like the rest.

## v0.9.1 notes
- New look for the Colonies window (ColonyTheme: ONI-style dark navy panels, plum/slate buttons, custom checkboxes): header with your colony, tabs "My colony / Start", "Colonies", "Rules", "View", cards instead of plain text, step-by-step "Start your own colony", work priority rows with rank badges and colour bars, grouped rules with explanations, status bar at the bottom.
- Banners (spectating, placement with colour legend) and the owner-under-cursor box use the same style. The window hides while you place a colony and while the game's duplicant cards are open.

## v0.9.0 notes
- Work priority per colony: each player arranges "whose errands first" in the Colonies panel (▲/▼). Defaults: own colony first, then the others in founding order (Host: Host > P2 > P3, P2: P2 > Host > P3, P3: P3 > Host > P2). Your dupes do the first colony's errands, then the next, and so on; your slot also covers unowned/shared work. One entry per colony on the asteroid.
- Personal needs (eat, sleep, toilet...) and compulsory chores always stay above other colonies' errands; emergency (!!) errands are ordered the same way within their own tier. Dupes finish their current errand before switching (vanilla interrupt rules).
- Needs the host rules "Separate errands" and "Help other colonies". Saved with the colony and replicated with the registry.

## v0.8.3 notes
- Colony food calories are host-authoritative: every 3 s the host sends the per-world kcal total (and per-food units) that RationTracker counts; clients use it for the top-left kcal meter, its tooltip, the Food diagnostic and the kcal graph. Falls back to the local count if no update arrived for 20 s. Dupe stomach calories were already synced by VitalStatsPacket (1/s).

## v0.8.2 notes
- The water geyser placed next to every newly founded colony is gone. Instead the option "Water geysers near the host's base" (0-4, default 1) places geysers 12-30 cells from the host's Printing Pod, once per save (existing saves too).

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
