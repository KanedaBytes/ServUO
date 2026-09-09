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
- **Claude commits its own work as it goes; `git push` is Sean's.** A session runs long and nobody
  is watching it, so printed `git commit` lines used to pile up unrun - and `git add -A` then swept
  several checkpoints into one commit under the earliest message. Committing at each checkpoint
  keeps the tree clean, so `git add -A` can only stage what the message describes. See `CLAUDE.md`
  §16, including what to do when a commit is refused.

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
| 5d | The editor's isometric art view — the renderer, then editing on the art | **done** |
| 6 | `Scripts/Custom/Bots/` — PlayerBots, session 1: the bot mobile you can spawn and inspect | **done** |
| 7 | PlayerBots, session 2: the behaviour tick, `Traveler` on `NavWalker`, class-weighted destinations | **done** |
| 8 | PlayerBots, session 3: the lifecycle roller, bank crowds and shoppers | **done** |
| 5e | The editor's left column: collapsible sections, a resizable column, the bot card, and create forms fed from the shard | **done** (the admin panel is 5f) |

The bot layer is being ported in sessions, from the survey in `docs-src/uo-offline-port-survey.md`.
Session 1 was identity — class, tier, skills, stats, name, speech hue, outfit. Session 2 made them
walk: a behaviour tick over `LiveRegistry`, a `Traveler` on the shard's existing `NavWalker`, and
class-weighted destinations from `bots.json`. Session 3 gave them a life — a personality-weighted
phase roller, a bank with a standing crowd, and shops worth browsing. Speech and population follow.
See `Scripts/Custom/Bots/README.md`, in particular its **Deviations from uo-offline** and **Severed
seams** sections.

The art view (5d) took three sessions: the isometric renderer and its on-demand tile cache; then
stretched terrain, the shard's own furniture and the floor slider; then **the pick map**, which is
what made it an editor rather than a picture. The isometric projection has no inverse — a screen
pixel names a world tile only once a Z is assumed — so the renderer writes down which tile and which
standing Z each pixel belongs to while it is drawing, and everything the radar view can do the art
view now does on the art. See `tools/editor/README.md`, **The pick map**.

Roadmap beyond the port: a test project, a possible .NET retarget, a `TimedSpawner`, and the art
view's cave section mode — a cut through the world rather than a storey of a building, which is the
one thing the floor slider cannot express.

### Picking the bot layer up cold

Where a fresh session should start, in order:

1. **`Scripts/Custom/Bots/README.md`**, its **Deviations from uo-offline** and **Severed seams**
   sections first. Every place this port does something other than upstream is a row there, with
   the reason; every place it stops short is a seam with the session that restores it. If a
   behaviour looks wrong, check those two tables before reading code.
2. **The reference is `C:\Users\sean.GEKKOSTATE\uo-modernuo\ModernUO`**, `Projects/UOContent/CustomBots/`
   for source and `Distribution/Data/` for data - see the table at the top of `CLAUDE.md` for the
   three trees that are *not* it. The rule for any design question is uo-offline's answer first,
   deviate only at a named seam.
3. **`docs-src/uo-offline-port-survey.md`** for what has been ported and what the September 2026
   upstream release changed under files not yet reached.
4. **Run `[BotSmoke`** and read `[CoreSmoke`. The chain is walk → life → chat → work; each probe's
   result line says what it measured and, for the life probe, the window it derived and the route
   that set it.

What a bot is, in one paragraph: a `BaseCreature` flagged `Player`, with a class, tier, personality
and a **home town** rolled at creation. It picks destinations from `navigation.json` weighted by
`bots.json` - class × kind, times 2.5 for its home town, with a distance term only on work sites -
walks them on `NavWalker`, and hands off to a visit behaviour on arrival. A 60-second roller
transitions it between `Traveler` and `Idle` when its phase expires and it is not mid-walk. Nothing
about it survives a restart. There is no spawner, no session curve and no population yet; that is
the population session.

Rules in force for bots and occupied tiles, each with its row in the README's Deviations table:

- **A bot walks through other bots and daily-life actors, free** - uo-offline's `CheckShove => true`,
  with the reason recorded (the engine's full-stamina rule jammed their plazas). A `BaseCreature` is
  refused before its `CheckShove` is asked, so the *shoved* side consents through `BotShove`. A real
  player shoving a bot pays the player rule. **A bot yields to real players and stock NPCs** - a
  vendor, a guard, an animal - because those two `OnMoveOver` overrides are upstream files; when a
  walker is wedged, its rung log names what stands on the goal tile or within two tiles of it.
- **An arrival can be a place**: `NavArrival.range` rides on the route's last step and the walker
  accepts arrival from inside it. The forge arrivals author 2 (uo-offline's `DriftArriveRange`);
  banks, shops and guard posts keep the tile. A shop spot is scattered by `Custom.NavArrivalScatter`
  and validated for standing, never for reachability, and the last-hop wedges the probes still show
  are that: a scattered spot behind a counter with nobody to name.
- **A crafting station's stand tile is chosen on arrival**, for what the bot came to do: every
  standable tile inside the arrivals' ranges with the trade's fixtures in crafting reach, free
  first, nearest second; an occupied tile when the apron is full; another station of its kind in
  the same town only when reach holds no standable tile at all. No waiting state.
- **A laden gatherer delivers from where its walk ends** when a buyer is within twelve tiles of
  the destination, arrived or not - upstream's `DeliverMaterials` after a stalled drift.
- **The walker has uo-offline's frozen watchdog**: two tiles in six hop timeouts (120 s, not their
  60 s, because our rungs are 20 s apart and a shorter window pre-empts a Skip) sends a rooted
  mobile straight to the top rung.
- **The probes** derive the life window from the graph and pin every probe bot's home to the spawn
  town; the life probe counts a teleport only on a bot still wearing the Traveler brain; the work
  probe expects the packed forge - first smith on the station tile, the ore reaching whichever
  smith is at the bench, a second smith from town settling on a free tile and working.

Running a chain headlessly: set `BotSmokeOnStart=True` in `Config/Custom.cfg`, start `ServUO.exe`
with its console redirected to a file, drop `Data/Live/requests/livemap-on.token` (body `2 custom`)
so `Data/Live/botlog.json` records each bot's route, rung and arrival events, and read the console
for the five result lines. Put the flag back to `False` before committing - a `git add -A` at a
checkpoint has shipped it as `True` once already.

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
| `[SpawnBot [class] [tier] [home:<town>]` | GameMaster | Spawn a bot at your feet; class, tier and home town roll when omitted (alias `[SpawnTestBot`) |
| `[BotInfo` | GameMaster | Target a bot; dump its class, tier, stats and skills against the caps in force |
| `[BotsReload` | GameMaster | Re-read `bots.json` and the player caps it defaults from (alias `[ReloadBots`) |
| `[BotBehavior [name]` | GameMaster | Target a bot; report its brain, or switch it (`Idle`, `Traveler`) |
| `[BotLifecycle [on\|off]` | GameMaster | Report the phase roller, or pause it for testing |
| `[BotSmoke` | Administrator | Spawn one bot per class, check them against the caps, then run the party, five-traveller and twelve-bot lifecycle probes |
| `[BotPace [seconds]` | GameMaster | Target a walking bot; measure its step cadence for N seconds and report the pace the engine actually used against the pace it was given |
| `[WorldItems [facet]` | Administrator | Write the art view's world-item snapshot - every loose, immovable, visible item on the facet |
| `[Vocabulary` | Administrator | Write what the shard actually loaded - every spawnable creature type, which are vendors, and the C# enums - for the editor's dropdowns |

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

**The art view (5d-2).** The base map has two settings. **Radar** is the facet-wide pyramid above.
**Art** is the real client art drawn isometrically, with a floor slider - Ground, First floor, All -
that peels the roof off a building so its interior is visible.

There is no batch export of the art and there cannot be: Trammel's isometric canvas is
`(7168 + 4096) x 22 = 247,808` pixels on each side, 61 gigapixels per floor. So a tile is rendered
the first time somebody looks at it and cached forever - the bridge holds the request while
`MapExport.exe --serve` draws it, and radar shows through underneath until it arrives. Britain
becomes art because you look at Britain; Trinsic becomes art by panning there. Roughly 13 ms a tile
at 1:1 and 260 ms at 1:8, so a screenful is well under a second.
`export-tiles.ps1 -Prerender 1380,1495,365,345` warms the whole of Britain - 16,608 tiles, 3m26s,
873 MB - and is never required. That number is the argument FOR on demand: it is one town out of a
facet, and nobody pays it for the map they never open.

Art is read-only this session: selecting works, because the editor knows where it drew every shape,
but placing and dragging need the inverse projection and the inverse is not a function - a screen
pixel names a world tile only once you assume a Z, which is 2.7 tiles of error where Britain stands.
A per-pixel pick map from the renderer is the fix.

**Terrain is stretched (v2).** Each land tile is drawn across the heights of its four corners with
a `texmaps.mul` texture, where the client would use one, and as the flat 44x44 art where it would
not - 75% of land ids have no texmap, so both paths are needed. The corners come from the shard's
own rule, `Server.Map.GetAverageZ`, so a slope is drawn from the four numbers the walker walks. At
the graveyard, the castle, the north outcrop and the cave entrance, 99.4-100% of sloped tiles
stretch; the handful that do not are animated water. `MapExport --terrain-report x,y,w,h` reports
that for any box.

**A storey is measured from the land AROUND a column, not under it.** The smithy's back wall stands
on the cliff edge above the river, so the land in its own column is 45 below the building's floor -
and the first rule read a ground-floor wall as a first-floor one and deleted it at the Ground stop.
The ground is now the highest land in the 3x3 neighbourhood that is not above the static itself.
Land only, never a surface static: every item stands on some floor, so measuring from the nearest
floor below would make every item storey zero and the slider would stop doing anything.

**A cave passage still does not read as a trench.** The floor slider filters statics, and a mountain
is land, so `1263,1251` is byte-identical at every stop. Seeing into a cave needs the cutoff to
apply to land too - a section through the world rather than a storey of a building - which is its
own feature.

**The shard's own furniture is drawn too, from a snapshot it writes on request.** `[WorldItems`
(or the `world-items` token) walks the world for loose, immovable, visible items - decoration,
doors, signs, every `[Decorate` addon component - and writes `Data/Live/world-items.json`: 29,778
items on Trammel, 1.37 MB. The renderer draws them exactly as it draws statics, in a second
transparent tile layer keyed by the snapshot's id, so a re-decorate throws away seconds of item
tiles rather than the minutes the map layer costs. Occlusion is exact: an item tile paints the whole
column and emits only the pixels the items ended up owning, so a bench indoors is behind the wall
in front of it. The floor stops apply to items exactly as they do to statics.

**The art view works with the shard down** - the map layer is the client's own files. Without a
snapshot there is simply no furniture, and the editor's art line says so.

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

**The left column, and the forms (5e).** Every section collapses and remembers it; the column's
width is dragged and remembered. Clicking a bot - a row, or its dot on the map in either view -
opens a card over the map with the same detail and event log the Bots panel shows, closable and
draggable.

**And no create form carries a list of shard values any more.** A field whose valid values are a
finite set is populated from real data: `[Vocabulary` (or the `vocabulary` token) writes what
`Scripts.dll` actually loaded - every concrete `BaseCreature` with a constructor a spawner can
call, which of them are vendors, and the C# enums - and the bridge merges that with what is written
down in `navigation.json`, `bots.json` and `Spawns/Custom`. The Monster/NPC split is the bridge's,
because ServUO exposes no source folder at runtime and `Scripts/Mobiles/Normal` holds `Alligator`
next to `Balron`: the shard says what exists, the repo says where it was written, and neither is a
list anybody maintains. A `<select>` only where the shard has a closed enum; a typeable combo
wherever it takes a free token, because `NavRecords` makes a destination type free *deliberately*
and a stricter control would refuse what the shard would accept.

The three forms that needed rebuilding are rebuilt. **Corridor** takes a name and mints
`<name>-WP-0001` upward in walk order, with its tags on the waypoints and the edges between them.
**Work site** explains each field in a line. **Spawner** is uo-offline's form - kind first, type
filtered by kind, count, home range, respawn window - with `GG_` applied rather than typed.

> **Saving a spawner from the browser had never written anything.** `filesWithEdits` returned a
> fixed three-name list and a spawn key is `spawn:<facet>/GG_Thing.xml`, so `save()` looped over
> nothing and reported *"Saved and reloaded."* The bridge's half was right and tested the whole
> time; nobody called it. `save()` now refuses out loud when it has edits and no file to put them
> in. The Bots panel's trailing slot had the same shape of fault - `updateCounts` swept `.count`
> document-wide, so every row read `0` where its stuck rung should be.

**Still to come: the admin panel** - restart the shard, save the world, a live console feed and the
named actions. It is the only part of the editor that touches the shard *process*; see the end of
`tools/editor/README.md` for what else it could hold.

See `tools/editor/README.md` for the save contract, the two tiers of validation failure - a dangling
edge id is a warning here, not a rejection - and what is still to bring back from the ModernUO
original when the editing UI lands.

## Tech debt

- **`NotifyStaff` is copied privately in three systems** (`RestrictedZoneSystem`,
  `NavigationSystem`, `DailyLifeSystem`). Three is tolerable; the fourth should become a
  `Custom/Core` helper rather than a fourth copy.
