# Goblin Gang — Shard Guide

Custom Ultima Online shard built on **ServUO `pub57`** (assembly 57.4).

Previously built on ModernUO at `E:\dev\UO\ModernUO`; those systems are being ported here.
See `Scripts/Custom/MODIFICATIONS.md` for the upstream-edit log and `CLAUDE.md` for the
engine-level conventions.

## Running it

```
.\tools\dev.ps1
```

Renders the map tiles the first time, starts the shard and the editor bridge in their own windows,
waits for the bridge, and opens the editor.

| | |
| --- | --- |
| **Connect a client to** | `127.0.0.1:2594` |
| **Editor** | http://127.0.0.1:8081/ |
| **Live entities on the map** | the editor's **Live on** button, or `[LiveMap on` in game |

Two windows rather than background jobs, deliberately: the shard's console is where its log goes,
and the whole reason this shard has build scripts at all is that a failed build must be visible
rather than swallowed. Each window stays open on exit, so a crash leaves its own evidence.

Useful switches: `-NoShard` when a shard is already running, `-NoBrowser`, `-Tiles` to re-render,
`-Debug` for the Debug configuration. Nothing about it is required — it starts `build.ps1` and
`node tools\editor\bridge.js` exactly as you would by hand, and both still work on their own.

## Shard facts

| | |
| --- | --- |
| **Port** | `2594` (`Config/Server.cfg`) |
| **Name** | Goblin Gang! |
| **Era / expansion** | **EJ** — Endless Journey (`Config/Expansion.cfg`) |
| **Primary facet** | **Trammel** — all custom content, zones and spawners target Trammel unless stated otherwise |
| **Client data** | `C:\Games\Electronic Arts\Ultima Online Classic` (`Config/DataPath.cfg`) |
| **Target framework** | `net48` (.NET Framework 4.8), x64 |
| **OS** | **Windows now.** Linux later via Mono, or a .NET retarget |

## Conventions

- **All custom content lives in `Scripts/Custom/`**, namespace root `Server.Custom`.
- **Never edit upstream files.** Prefer subclassing, `EventSink`, or a `Configure()` hook.
  If an upstream edit is genuinely unavoidable, it **must** be logged in
  `Scripts/Custom/MODIFICATIONS.md` with the diff and the reason no `Custom/`-side approach worked.
- Custom JSON config lives in `Data/Custom/`; custom spawn XML in `Spawns/Custom/`.
- Custom `.cfg` files go in `Config/` like any other — they are auto-discovered at boot.
- Keep everything **Mono-safe**: no Windows-only APIs (registry, WinForms, Windows-only path
  assumptions), so the Linux path keeps working.

## Build and run

**Use the shard's build scripts.** They run the build, **abort if it fails**, and only then
launch the server.

```
# Windows
.\build.ps1              # build + run
.\build.ps1 -NoRun       # build only
.\build.ps1 -Debug       # Debug config, launches with -debug

# Linux / Mono
./build.sh               # build + run
./build.sh --no-run
./build.sh --debug
```

**The server must be stopped before rebuilding** — `Scripts.dll` is locked while loaded and
there is no hot-reload.

### Why not `_winrelease.bat` / `make`?

Because a failed script build is **silent** on ServUO. `ScriptCompiler.Compile()` never checks
the `dotnet build` exit code: it returns success regardless and loads whatever `Scripts.dll`
already exists, so a compile error boots the **previous, stale DLL** and your change simply
isn't there. The only evidence is MSBuild output scrolling past in the console.

This shard therefore sets `Config/Compiler.cfg` to **`Dynamic=False`** (the server never builds
at boot) and builds only through `build.ps1` / `build.sh`, which check the exit code. The stock
`_winrelease.bat`, `_windebug.bat` and `makefile` are left in place as upstream files but are
not the supported run path.

## Custom systems

Ported from the ModernUO shard, in this order:

| # | System | Status |
| --- | --- | --- |
| 0 | `Scripts/Custom/Core/` — loop queue, JSON config, logger, persistence base, health checks | **done** |
| 1 | Restricted zones + countdown gump → auto-jail | **done** |
| 2 | Jail administration (on ServUO's existing jail region) | **done** |
| 3 | Old Marta + auto-collect + `[ResetQuest` | **done** |
| 4a | `Scripts/Custom/Core/Navigation/` — waypoint graph, destinations, arrivals, zones, routes | **done** |
| 4b | Britain daily life (day cycle, tavern, watch, townsfolk, shops) | **done** |
| 5a | Map export + editor bridge, read-only layers | **done** |
| 5b | Editing through the bridge | **done** |
| 5c | Spawners in the editor | **done** |
| 6 | `Scripts/Custom/Bots/` — PlayerBots, session 1: the bot mobile you can spawn and inspect | **done** |
| 7 | PlayerBots, session 2: the behaviour tick, `Traveler` on `NavWalker`, class-weighted destinations | **done** |
| 8 | PlayerBots, session 3: the lifecycle roller, bank crowds and shoppers | **done** |

The bot layer is being ported in sessions, from the survey in `docs-src/uo-offline-port-survey.md`.
Session 1 was identity — class, tier, skills, stats, name, speech hue, outfit. Session 2 made them
walk: a behaviour tick over `LiveRegistry`, a `Traveler` on the shard's existing `NavWalker`, and
class-weighted destinations from `bots.json`. Session 3 gave them a life — a personality-weighted
phase roller, a bank with a standing crowd, and shops worth browsing. Speech and population follow.
See `Scripts/Custom/Bots/README.md`, in particular its **Deviations from uo-offline** and **Severed
seams** sections.

Roadmap beyond the port: a test project, a possible .NET retarget, and a `TimedSpawner`.

## Staff commands

| Command | Access | Effect |
| --- | --- | --- |
| `[CoreSmoke` | Administrator | Exercises the `Custom/Core` foundations and reports every health check |
| `[RestrictZone <name>` | GameMaster | Target two corners to create a restricted zone |
| `[UnrestrictZone <name>` | GameMaster | Remove a restricted zone |
| `[ListRestrictedZones` | GameMaster | List every restricted zone |
| `[RestrictedZonesReload` | GameMaster | Re-read `restricted-zones.json` and rebuild the regions |
| `[Jail <player> [reason]` | GameMaster | Jail an online player; sentence escalates per offence |
| `[Unjail <player>` | GameMaster | Release immediately |
| `[JailInfo <player>` | Counselor | Show a player's jail record |
| `[JailRecord` | Player | Show your own jail record |
| `[ResetQuest <QuestTypeName>` | GameMaster | Target a player; cancel that quest and clear its completion record |
| `[ResetAllQuests` | GameMaster | Target a player; erase every quest and record, after confirmation |
| `[GG_Reimport` | Administrator | Delete every `GG_` spawner and re-import `Spawns/Custom` |
| `[NavReload` | GameMaster | Re-read `navigation.json` and rebuild the travel graph (alias `[ReloadNav`) |
| `[NavDebug [radius]` | GameMaster | Toggle markers on nearby waypoints, arrivals, destinations and zone corners |
| `[NavMark [id] [nolink]` | GameMaster | Add a waypoint where you stand, chained to your previous mark |
| `[NavRecord on\|off [tiles]` | GameMaster | Drop a chained waypoint every N tiles as you walk |
| `[NavLink <a> <b> [gate]` | GameMaster | Add one explicit edge |
| `[NavUnlink <a> <b>` | GameMaster | Remove every edge between two waypoints |
| `[NavDelete <id>` | GameMaster | Remove a waypoint and every edge touching it |
| `[NavArrival <destId> [exclusive]` | GameMaster | Add an arrival point where you stand |
| `[NavRoute <from> <to>` | GameMaster | Print the computed route between two waypoints or destinations |
| `[NavAudit` | Administrator | Pathfind every walk edge against real map data |
| `[DayPhase` | GameMaster | Report the day phase, anchor time and whether an override is active |
| `[DayPhase <dawn\|day\|dusk\|night\|clear>` | GameMaster | Force a phase for testing, or release it |
| `[DailyLifeReload` | GameMaster | Re-read `britain-daily-life.json` and rebuild the town |
| `[DailyLifeSmoke` | Administrator | Force a full day cycle and check the town reacts |
| `[GG_MigrateVendors` | Administrator | Hand Britain's six shopkeeper spawn points to daily life (one time, reversible) |
| `[GG_RestoreVendors` | Administrator | Undo the migration |
| `[SpawnBot [class] [tier]` | GameMaster | Spawn a bot at your feet; class and tier roll when omitted (alias `[SpawnTestBot`) |
| `[BotInfo` | GameMaster | Target a bot; dump its class, tier, stats and skills against the caps in force |
| `[BotsReload` | GameMaster | Re-read `bots.json` and the player caps it defaults from (alias `[ReloadBots`) |
| `[BotBehavior [name]` | GameMaster | Target a bot; report its brain, or switch it (`Idle`, `Traveler`) |
| `[BotLifecycle [on\|off]` | GameMaster | Report the phase roller, or pause it for testing |
| `[BotSmoke` | Administrator | Spawn one bot per class, check them against the caps, then run the party, five-traveller and twelve-bot lifecycle probes |
| `[BotPace [seconds]` | GameMaster | Target a walking bot; measure its step cadence for N seconds and report the pace the engine actually used against the pace it was given |

## Custom spawns

Custom XmlSpawner definitions live in `Spawns/Custom/<facet>/GG_<Thing>.xml`, kept out of the
stock `Spawns/` files so world regeneration never mixes the two.

**Every custom spawner's `<Name>` starts with `GG_`.** That prefix is the handle: both `[XmlLoad`
and `[XmlUnLoad` take an optional `SpawnerPrefixFilter` second argument matched with an ordinal
`StartsWith`, so the prefix makes this shard's spawners addressable as a set.

```
[GG_Reimport                    # preferred: sweeps GG_ spawners, then re-imports
[XmlLoad Spawns/Custom GG_      # raw import; replaces by <UniqueId>, leaves orphans behind
```

`[GG_Reimport` is preferred because `[XmlLoad` alone is an upsert — a spawn point deleted from the
XML would keep its spawner in the world forever. See `Spawns/Custom/README.md`.

## Navigation

`Data/Custom/navigation.json` is the shard's single source of truth for where things are and how
to get there — waypoints joined by explicit edges, destinations with standable arrival points,
tagged zones, and authored patrol routes. Britain daily life uses it; the PlayerBots system will
use the same data.

Hops are capped at 12 tiles (`Custom.NavHopMaxTiles`) because ServUO's `FastAStarAlgorithm`
searches a 38x38 box centred on the midpoint of start and goal, and a longer hop whose detour
leaves that box fails **silently** — the NPC walks into scenery. `[NavAudit` pathfinds every edge
against real map data and is the check that keeps the file honest; set
`Custom.NavAuditOnStart=True` to run it headlessly.

See `Scripts/Custom/Core/Navigation/README.md`, and `Scripts/Custom/Core/Navigation/nav-format-comparison.md`
for how the schema maps onto the `uo-offline-server` bot navigation format.

## 5d-1b — uo-offline's navigation as a base

**All four items are done and committed.** What remains is the acceptance test, which is Sean's to
run; see the bottom of this section.

`Data/Custom/reference/uo-offline-nav.trammel.json` holds 3952 waypoints, 4291 edges and 485
destinations converted from uo-offline (GPL-3, pinned commit in that directory's README). The shard
never loads it. The editor draws it as a read-only layer, and **Adopt** copies a region into
`navigation.json` after re-walking every edge — 56% of theirs are longer than our hop cap, so
nothing in that file is usable until something walks it.

1. **Converter** — `tools/nav-import/uo-offline.js`, run by hand. Waypoints keep `uo-wp-N`;
   destinations are named `<town>-<kind>` from their `City` (`trinsic-bank`, `trinsic-shop-smith`).
   Every adopted record carries `source: "uo-offline"`.
2. **Reference layer** — bbox-loaded like the stock spawners, dashed and half-opacity so it can
   never be mistaken for authored data. Dungeon (1988) and Lost Lands (43) are separate toggles,
   off by default, and Adopt refuses both.
3. **Adopt** — `NavAdopt.cs`, driven by the `nav-adopt` token and the editor's *Adopt region* tool.
   It cannot write nav data: it writes a proposal to `Data/Live/nav-adopt.json` and the editor
   accepts it through the ordinary save path.
4. **Island check** — `Nav.Data` warns when a component holding a destination or arrival cannot
   reach the main graph, and `[NavAudit` repeats it.

### The ResolveZ window fix, and why it mattered

`NavWalker.ResolveZ` probed its hint Z once and fell back to `map.GetAverageZ`. A bridge deck is a
chain of statics whose Z changes tile by tile, so the probe missed and the answer came back as the
river bed underneath. **The Britain-Trinsic bridge was unwalkable to every flood, audit and scout
we own** — and an adopt of the road south left 372 waypoints unreachable because of it.

It now searches a window around the hint, nearest first, climb 4 / drop 20 — translated from
uo-offline's `CustomBots/Nav/Walkable.cs:27-28,118-140`; see `Scripts/Custom/Bots/README.md`. This
was **not** a pathfinder difference: ServUO's `MovementPath` crosses that bridge when handed Z 6 to
Z 3, and we had simply never handed it the deck.

Two things it bought beyond the bridge: the adopt's unreachable count fell from **372 to 134**, and
the **walk probe passed for the first time** — sidestep recoveries went from 1-5 per run to zero,
because bots had been resolving onto the wrong surface and shouldering around it.

`CoreSmoke` walks four bridges as a regression test (`RunCrossingCheck`). Nothing cheaper catches
this: `[NavAudit` only paths edges already authored, and there was no authored edge over a bridge
nobody could cross.

**The window was still one riser short, and the pier needed a different fix.** Trinsic's canal
bridges stand on wooden ramps that rise 5 Z per tile (a `Bridge` tile is stood on at half its
height but stepped from at its full height, so the engine allows it); the window climbed 4 and
stopped at the foot of all four. It now climbs 5 - a named deviation from uo-offline's 4, derived in
`Scripts/Custom/Bots/README.md`. The south pier's waypoint is stored at the **water's** Z, -15,
thirteen below the planks, so no window found it; `ResolveZ` now scans outward from the land as far
as 60 for a seed, as uo-offline's `TryFindSeedZ` does, while the floods keep to the window
(`ResolveStepZ`) so they cannot step from the ground onto the middle of a ramp. `CoreSmoke` walks
the four canal bridges and the pier as well.

### Config keys

| key | default | what it does |
| --- | --- | --- |
| `Custom.NavAdoptJoinReach` | 100 | How far a join may reach for one of our waypoints. Not the hop cap: a join is walked and subdivided like any edge, so it never has to fit in one hop. At the cap, one adopt made a single join and left 372 waypoints floating. |
| `Custom.NavHomeWaypoint` | `brit-bank-2` | Which component is "the main graph". Was the *largest* component, which held only while we were the biggest thing in the file — one adopt of 481 waypoints against Britain's 231 inverted it and reported Britain as the island. |

### Sending a bot somewhere

```
[BotSendTo <destination or words> [bot name]
```

Target the bot, or name it when it is not within 12 tiles. A partial argument lists the matches, so
`[BotSendTo trinsic bank` resolves without knowing the exact id. It routes through
`TravelerBehavior.SendTo`, the same path the lifecycle uses. `[BotInfo` shows behaviour,
destination and leg progress; the editor's Bots panel shows the same live.

### The Britain-Trinsic adopt, as it stands

Adopting `1291,1729 929x1274` gave, before the canal-bridge fix: 472 waypoints, 501 edges, 1 join,
5 failures, 1 stranded, 8 destinations skipped for having no arrival inside the hop cap, and **122
waypoints that cannot reach the existing graph**. All five failures were Trinsic's water: three
canal bridges (a ramp riser one Z taller than the window climbed), the south pier (a waypoint stored
at the water's Z), and two records on one tile. After the fix the same box walks with **0 failures,
0 stranded and 0 unreachable**, one pair folded, and every hop pathed by the engine both ways.

**Save writes the part of a proposal that reaches the graph and drops the rest**, with both counts
in the headline; dropped records stay in the reference layer for a later box. The one proposal
still refused outright is a box that reaches nothing at all.

**A destination whose arrivals are all beyond the hop cap gets a walked corridor** from the nearest
reachable waypoint to a waypoint minted on the arrival tile, listed with its length and flagged for
review past two hops. The eight skipped destinations were their data measured against our cap.

### What the acceptance test still needs

1. Adopt outward from Britain **one box at a time**, each overlapping ground already saved, so each
   box's edges have something to join onto. One large box cannot join and will be refused.
2. Save each box, then run `[NavAudit`.
3. `[BotSendTo trinsic-bank` and watch the bot arrive.

## Britain daily life

`Data/Custom/britain-daily-life.json` drives the town's day: tavern patrons after dark, the night
watch on its posts, a courier and a farmer walking their rounds, and six shopkeepers who go home
at dusk. **Every location in it is a nav id**, including the clock anchor — there are no
coordinates in the file. All movement goes through `NavWalker`.

The six shopkeepers are `GG*` subclasses of the stock vendor types, because a stock `BaseVendor`
moves one step per 30-120 seconds and could never finish a walk home inside the night. Handing the
stock spawn points over is an explicit, reversible, one-time migration:

```
[GG_MigrateVendors     # switch the stock six off (asks for confirmation)
[GG_Reimport           # import Spawns/Custom, which brings ours in
```

`[DailyLifeSmoke` forces a whole dusk-to-day cycle in milliseconds and checks the town reacts; its
last result shows up in `[CoreSmoke` as `DailyLife.Smoke`. Set `Custom.DailyLifeSmokeOnStart=True`
to run it headlessly.

See `Scripts/Custom/DailyLife/README.md`.

## Health checks

`[CoreSmoke` (Administrator) exercises the `Custom/Core` foundations and reports every
registered health check — including any persistence store that has gone **degraded** and is
refusing to save. Run it after every upstream merge.

Systems register their own checks with `HealthCheck.Register(...)` rather than growing the
command, so `[CoreSmoke` stays a one-command health check for the whole shard.

ServUO's console cannot invoke staff commands, so to run it headlessly set
`CoreSmokeOnStart=True` in `Config/Custom.cfg` and read the console. Leave it `False` on a
live shard.

## Shard editor

A browser map editor for the shard's own data - the nav graph, destinations, arrival points,
zones, routes, restricted zones, daily-life actors - with live entities drawn on top.

**Editing works as of step 5b**; spawners are 5c. Add, move and delete waypoints, destinations,
arrivals and zones; link and unlink edges; author routes by clicking waypoints in order; edit the
daily-life config as a form. A save writes the file, asks the shard to reload it, and says what the
shard said - including when the write succeeded and the reload did not, which is a state the editor
has to show rather than hide.

`.\tools\dev.ps1` starts it along with the shard (see **Running it** above). By hand it is two
commands, and the script does nothing more than run them:

```
.\tools\editor\export-tiles.ps1     # render the map tiles, once
node tools\editor\bridge.js         # then browse http://127.0.0.1:8081/
```

**It is a file bridge, not an API inside the shard.** The shard writes `Data/Live/entities.json`
and `Data/Live/health.json`, and watches `Data/Live/requests/` for token files; a small Node
process reads those files, projects them into the editor's shape vocabulary, and drops tokens.
Neither side holds a socket to the other, so either can restart without the other noticing, the
shard has no HTTP surface to secure, and the whole channel can be inspected with `type` and `del`.

The bridge binds `127.0.0.1` only and refuses cross-site writes, so there is no token to paste.
The three data files it may write are named by a key in a fixed table, never by a path from the
caller, and a save carries a hash of the bytes it started from - so a stale editor cannot flatten a
walk `[NavRecord` just wrote.

| Command | Access | Effect |
| --- | --- | --- |
| `[LiveMap on\|off [seconds] [custom\|all] [zoneId]` | Administrator | Write the live entity snapshot. Defaults to custom actors and players every 2s; `all` needs a nav zone to bound it |
| `[NavExportGolden` | Administrator | Write the golden JSON fixtures the bridge's writer is tested against |
| `[RestrictedZonesReload` | GameMaster | Now also reachable from the bridge as the `zones-reload` token |
| `[GG_Reimport` | Administrator | Now refuses with the world intact when a spawn file cannot be read, instead of emptying it and reporting success |

Map tiles are derived data, gitignored, and safe to render while the shard is up - MapExport
builds its `Server.csproj` reference into its own folder rather than the repo root. A second facet
is another run: `export-tiles.ps1 -Facet Felucca`.

A reload ack now carries the shard's validator strings themselves (`errors` and `warnings`), not
just a count of them, so the editor can say which problem to fix rather than that there are three.

**Spawners (5c).** `Spawns/Custom/<facet>/GG_*.xml` is editable through the same save path; the
~2,500 stock spawners are read-only context, loaded by viewport rather than by facet. A save
reloads **one file** - `spawn-reload`, which unloads from the `.bak` and loads the new file - rather
than `[GG_Reimport`, which deletes every `GG_` spawner in the world along with its spawned mobiles.
`Data/Live/spawners.json` carries what each spawner is actually doing: running, current count, next
spawn, its source file, and whether the vendor migration switched it off. Written on a slow timer
and immediately after any spawner reload. It also counts every spawner in the world by name and
tile, which is how a spawn point that exists twice becomes visible - see the note in
`Scripts/Custom/MODIFICATIONS.md` about the 47 Khaldun rows.

The sidebar's **Audit** button runs `[NavAudit` and draws what it could not path; **Resync all
spawns** runs `[GG_Reimport` behind a confirmation, because that one deletes every `GG_` spawner in
the world along with its spawned mobiles.

See `tools/editor/README.md` for the save contract, the two tiers of validation failure - a dangling
edge id is a warning here, not a rejection - and what is still to bring back from the ModernUO
original when the editing UI lands.

## Tech debt

- **`NotifyStaff` is copied privately in three systems** (`RestrictedZoneSystem`,
  `NavigationSystem`, `DailyLifeSystem`). Three is tolerable; the fourth should become a
  `Custom/Core` helper rather than a fourth copy.
