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
| 9 | PlayerBots, session 4: the chat corpus, ambient chatter, and a bot that answers to its name | **done** |
| 10 | PlayerBots, session 5: work — gatherers, artisans, real harvest and real craft | **done** |
| 11 | PlayerBots, session 6: the population — the recipe, `GG_BotPop.xml`, and the session curve | **done** |
| 5e | The editor's left column: collapsible sections, a resizable column, the bot card, and create forms fed from the shard | **done** |
| 5f | The editor's admin panel: save, restart, the long-running probes, broadcast, and the console feed | **done** |

The bot layer is being ported in sessions, from the survey in `docs-src/uo-offline-port-survey.md`.
Session 1 was identity — class, tier, skills, stats, name, speech hue, outfit. Session 2 made them
walk: a behaviour tick over `LiveRegistry`, a `Traveler` on the shard's existing `NavWalker`, and
class-weighted destinations from `bots.json`. Session 3 gave them a life — a personality-weighted
phase roller, a bank with a standing crowd, and shops worth browsing. Session 4 gave them a voice,
session 5 gave them work, and **session 6 gave the world a population**: a recipe read off the nav
graph, written to `Spawns/Custom/trammel/GG_BotPop.xml`, and a daily curve that makes it breathe.
See `Scripts/Custom/Bots/README.md`, in particular its **Deviations from uo-offline**, **Severed
seams** and **Population** sections.

The population is **derived, not authored**: nothing in the code knows the name "Britain". A town is
a tag in `bots.json`, a bank is a `bank` destination carrying it, a station is whatever
`CrafterProfiles` says a class works — so authoring a destination in the editor grows the recipe.
`[BotPopulationAudit` says what it would produce and whether the file still matches; `Bots.Recipe`
says the same thing on every `[CoreSmoke`, and reports the **tick cost** beside the live count so
the target is a number rather than a nerve. Sixty bots cost 0.4 ms of a 2,000 ms budget.

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

1. **`Scripts/Custom/Bots/README.md`** - its **Current contract** section first, then
   **Deviations from uo-offline** and **Severed seams**. The contract is what is true today; every
   place this port does something other than upstream is a row in Deviations, with the reason, and
   every place it stops short is a seam with the session that restores it. If a behaviour looks
   wrong, check the contract and those two tables before reading code - and note that everything
   under **History** is a dated finding rather than a statement about now.
2. **`REVIEW.md`**, the read-only architectural review of 11 September 2026. Required reading
   before changing this layer: it is where the open defects are named and prioritised. F1 fixed,
   **F2 fixed by the PlayerMobile class swap**, F3 interim only, F4 fixed; F5, F6, the save
   acknowledgement and the full F3 request identity are scheduled before 7f. Its section 11 was
   the documentation discrepancy list and is now closed.
3. **The reference is `E:\dev\UO\uo-offline` @ `7f38c7c`**, `playerbots/source/CustomBots/` for
   source and `playerbots/data/` for data - see the table at the top of `CLAUDE.md` for the three
   trees that are *not* it, including the installed snapshot this line used to name. The rule for
   any design question is uo-offline's answer first, deviate only at a named seam.
4. **`docs-src/uo-offline-port-survey.md`** for what has been ported and what the September 2026
   upstream release changed under files not yet reached. Its §1 line counts were taken at the
   older `91848d8` pin and say so; the reference to read is still the one in item 3.
5. **Run `[BotSmoke`** and read `[CoreSmoke`. Two synchronous probes run first - `Bots.Shove`,
   which asserts the collision diagnostic against the engine's own `OnMoveOver` for every ordered
   pair of actors, and `Bots.Death`, which REPRODUCES the murder-report cast failure on purpose and
   reports Ok saying so (REVIEW.md F2; it is expected until the class decision) - and then the chain
   is walk → life → chat → work. Each probe's result line says what it measured and, for the life
   probe, the window it derived and the route that set it. `Bots.Cadence` on `[CoreSmoke` is the
   guard on the behaviour ticker: one timer, at the rate `Custom.BotTickSeconds` names.
   **`Bots.Death` no longer reports an expected failure**: the class swap of 12 September 2026
   closed REVIEW.md F2, and that probe now asserts a clean reportable death and fails if the cast
   comes back.

What a bot is, in one paragraph: an accountless `PlayerMobile` flagged `Player`, with a class, tier, personality
and a **home town** rolled at creation. It picks destinations from `navigation.json` weighted by
`bots.json` - class × kind, times 2.5 for its home town, with a distance term only on work sites -
walks them on `NavWalker`, and hands off to a visit behaviour on arrival. A 60-second roller
transitions it between `Traveler` and `Idle` when its phase expires and it is not mid-walk. **Nothing
about it survives a restart** — `BotStartupPurge` sweeps every bot out of the world save at boot and
says how many, which is upstream's mechanism and replaced a `Timer.DelayCall(Delete)` at the tail of
`Deserialize` — and what does survive is the spawner file it came from, which is rebuilt into
spawners by `[GG_Reimport` and filled at `ServerStarted`. The `GG_` spawners needed no regeneration
for the class swap: XmlSpawner resolves a spawn by **type name** (`XmlSpawner2.cs:9262`), and the
name did not change.

A bot is one of two things, and it is the difference the whole population layer turns on. A
**lifecycle** bot has a session: it rolls behaviours, plays for one to four hours, says goodbye and
vanishes, and the daily curve decides how many of them are on. A **fixed-role** bot is furniture: it
never rolls, never logs out and never counts toward the target, so the bank crowds and the staffed
benches are there at 05:00 exactly as at 19:00. Which one it is comes from its spawner, as a
property on the bot rather than as the spawner's type — because XmlSpawner calls `OnAfterSpawn`
*before* it applies the spawn string, so upstream's `Spawner is FixedRoleBotSpawner` could not be
carried across.

Rules in force for bots and occupied tiles, each with its row in the README's Deviations table:

- **A bot walks through every occupant — including, since 12 September 2026, a real player.**
  uo-offline's `CheckShove => true`, with the reason recorded (the engine's full-stamina rule jammed
  their plazas). The *shoved* side consents through `BotShove`, and `BaseCreature.OnMoveOver` routes
  every stock creature - a vendor, a guard, an animal - through it too (MODIFICATIONS entry 5), which
  is still what carries the walk-audit probe. The "a bot still yields to a real player" half is gone:
  `PlayerMobile.OnMoveOver` refuses a mover only when it is an uncontrolled `BaseCreature`, and a bot
  is no longer one, so it falls through to the mover's own `CheckShove`. **The class swap spent that
  deviation without anybody choosing to, and `Bots.Shove` is what caught it** - one failing pair out
  of twenty. Sean's decision of 12 September 2026 was to keep upstream's rule, so `NavWalkFailures.MayBotPass`
  now says so too and takes the **mover** to say it: a bot passes a live player, and the walk audit's
  `BaseCreature` probe, being an uncontrolled creature, still does not. **The whole rule is one
  table** - `Scripts/Custom/Bots/README.md`, *The collision table*, which is the current contract
  and supersedes every prose description of shove behaviour anywhere else.
- **A bot's route plans through a closed door, and its step opens one.** MODIFICATIONS entry 6 - the
  second `.cs` edit in the tree - puts an `IBotActor` branch beside `FastAStarAlgorithm`'s
  `BaseCreature` one, because `MoveImpl.AlwaysIgnoreDoors` is reset inside the search loop and no
  `Custom/`-side assignment survives a single iteration. The step-time half, `PlayerBot.Move` through
  `Core/DoorHelper.cs`, was already there and was firing on routes that never aimed at a door.
  `Nav.Doors` on `[CoreSmoke` asserts both, plus the half that matters for a merge: a plain
  `PlayerMobile` must **not** get the same route. The known limit is locked doors, counted as the
  ledger's `locked-door` cause rather than guessed at.
- **The reverse pass is narrower, on purpose**: a bot lets through another bot, either walk-audit
  probe and a daily-life actor, and a stock vendor still jams against it. `NavWalkFailures.MayBotPass`
  answers the forward question and `ConsentsToBotPass` the reverse one; they were one predicate
  until 7e, which is what made the rung log call a stock NPC unpushable about a step the engine
  allows. `[BotSmoke`'s `Bots.Shove` asserts them against the real `OnMoveOver` for every ordered
  pair **on both facets** - because `Mobile.CheckShove` is a no-op on Trammel, where `FreeMovement`
  skips its whole body, so a Trammel-only grid cannot tell our rules from stock. When a walker is
  wedged, its rung log names who is standing on the tile the next step wants.
- **`Mobile.CheckShove` does nothing on Trammel**, and a good deal of older prose in this repository
  assumes otherwise. `MapRules.TrammelRules` includes `FreeMovement` (`Map.cs:128`) and
  `Mobile.cs:3518` - the only reader of that flag in the whole `Server/` tree - skips the entire
  full-stamina body when it is set. So *"a real player shoving a bot still pays full stamina, minus
  ten"* was true of Felucca and false of the facet the bots live on, and `PlayerBot.CheckShove =>
  true` buys nothing here.
- **One daily-life actor cannot walk through another**, which nobody decided: `IsSymmetricMover`
  keys on `shoved is PlayerBot`, so the town jams between its own eight actors. Reported rather
  than fixed; the count is `BotShove.ActorJams`, reported on `DailyLife.Smoke`, and the one-line
  widening waits on it.
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
| `nav-rejoin` (token only, like `nav-adopt`) | Administrator | Re-pick every join in a region by **walked road length** rather than straight line. Body is `x,y,width,height`. Proposes edges only - no waypoint is created, moved or deleted - and writes to `Data/Live/nav-adopt.json` for `accept-adopt.js` |
| `[NavAudit [full]` | Administrator | Pathfind every walk edge against real map data (1153 edges, ~0.5 s). `full` adds the approach-tile cliff scan — 54,683 engine paths, ~9 s — which the editor's quiet post-save run deliberately skips |
| `[WalkAudit [probes] [selftest] [class]` | Administrator | **Walk** every edge and arrival with real probe walkers, **once per probe class and reported per class**; `selftest` proves the instrument, per class. The class key is `creature` (the `BaseCreature` probe, which is the daily-life walkers' instrument) or `bot` (a real `PlayerBot`, which is the fleet's); omit it for both. Arguments are order-free, and the `walk-audit` token takes the same three |
| `[TileProbe [<x> <y> [z]]` | Administrator | What the engine sees at a tile - land, statics, items in all three lists, the movement switches, and whether a step onto it is refused from each of the eight neighbours (token: `tile-probe`, which also takes `sweep <lo> <hi>` over an ItemID range) |
| `[DayPhase` | GameMaster | Report the day phase, anchor time and whether an override is active |
| `[DayPhase <dawn\|day\|dusk\|night\|clear>` | GameMaster | Force a phase for testing, or release it |
| `[DailyLifeReload` | GameMaster | Re-read `britain-daily-life.json` and rebuild the town |
| `[DailyLifeSmoke` | Administrator | Force a full day cycle and check the town reacts |
| `[GG_MigrateVendors` | Administrator | Hand Britain's six shopkeeper spawn points to daily life (one time, reversible) |
| `[GG_RestoreVendors` | Administrator | Undo the migration |
| `[SpawnBot [class] [tier] [home:<town>]` | GameMaster | Spawn a bot at your feet; class, tier and home town roll when omitted (alias `[SpawnTestBot`) |
| `[BotInfo` | GameMaster | Target a bot; dump its class, tier, stats and skills against the caps in force |
| `[BotsReload` | GameMaster | Re-read `bots.json` and the player caps it defaults from (alias `[ReloadBots`). Also re-reads `life.idle` and `mounts`, which is the point of those two sections being data |
| `[BotBehavior [name]` | GameMaster | Target a bot; report its brain, or switch it (`Idle`, `Traveler`) |
| `[BotLifecycle [on\|off]` | GameMaster | Report the phase roller, or pause it for testing |
| `[BotSmoke` | Administrator | Spawn one bot per class, check them against the caps, then run the party, five-traveller and twelve-bot lifecycle probes |
| `[BotPace [auto] [seconds]` | GameMaster | Target a walking bot; measure its step cadence for N seconds and report the pace the engine actually used against the pace it was given. `auto` picks a walking bot itself rather than asking for a target, which is how it runs without a client |
| `[BotSteps [clear]` | GameMaster | Steps per bot-minute in each phase and what moved them - wander, drift-back, walk, teleport. `clear` opens a fresh window (token: `bot-steps`) |
| `[BotPopulationAudit` | Administrator | What the population recipe would produce per town, and whether `GG_BotPop.xml` still matches it. Spawns nothing |
| `[BotPopulationGen` | Administrator | Write the recipe to `Spawns/Custom/<facet>/GG_BotPop.xml`; then `[GG_Reimport` |
| `[BotPopulation [n]` | Administrator | Live count against the curve target and the tick cost, or set the target for this session |
| `[BotSessions [on\|off]` | GameMaster | Report the logon/logoff curve, or pin the population where it is |
| `[BotSendTo <destination or words> [bot name]` | GameMaster | Send a bot to a destination by id or by words, for testing a route by hand |
| `[BotTrace on \| off \| off all \| list` | GameMaster | Per-bot verbose tracing; `list` names who is traced |
| `[BotWorkScout` | Administrator | Propose work sites from what the engine says is harvestable and standable, to `Data/Live/work-scout.json`. **Never touches `navigation.json`** - a human merges it |
| `[BotSiteAudit` | Administrator | Every authored work site against the engine: what can be dug, what can be stood on, and whether the hops path |
| `[BotSitePick <x> <y> [mine\|chop]` | Administrator | Measure one tile as a work station - standable, and how many harvestable tiles it reaches |
| `[BotOreSweep [radius]` | Administrator | Sweep for harvestable ore around you, the same question the site tools ask |
| `[BotForgeAudit` | Administrator | Every forge and anvil the crafters depend on, against `Data/Decoration` |
| `[NavResampleZ [apply]` | Administrator | Report every record whose stored Z is not where a mobile would stand, and with `apply` correct them. Records it cannot place are reported and never moved (token: `nav-resample-z`) |
| `[WorldItems [facet]` | Administrator | Write the art view's world-item snapshot - every loose, immovable, visible item on the facet |
| `[BotOrphans` | Administrator | Names the owner of every item ServUO's boot cleanup is about to reap. `Custom.BotOrphanScanOnStart=True` runs it headlessly 0.5 s before `Cleanup.Run` |
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

**`trammel/GG_BotPop.xml` is generated, not authored.** `[BotPopulationGen` writes it whole from the
recipe and owns every `GG_BotPop_` name in it; hand-placed bot spawners belong in `GG_Bots.xml`,
which the generator never touches. Both are LF, pinned in `.gitattributes` for the reason the JSON
files are.

**A spawner will not construct a type whose constructor lacks `[Constructable]`** — and it says so
to nobody: `XmlSpawner2.cs:11263` defaults `requireconstructable` to true, and a refused spawn sets
`status_str` and *returns true*, so the spawner reports success and sits at a count of zero for
ever. This cost eight minutes of staring at thirty-seven working spawners that made nothing.
`[Vocabulary` now filters on the same attribute, so the editor's Type dropdown offers only what a
spawner can actually call.

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

### A record's Z, and the two different right answers

Half the Z values in the file were stale — 388 of 772 records sat at `z: 0`, because the editor's
create path wrote a flat zero for its whole life (`js/build.js:22-28`) and a drag only sometimes
wrote a new one. Britain's upper town stands at Z 20–30, so those records drew about 2.7 tiles off
along the isometric diagonal, which is how a marker comes to look wrong when the record is right.

**`[NavResampleZ`** corrects them, and writes `NavWalker.TryResolveZ`'s answer — where a mobile
carrying the stored Z as a hint would actually stand — **not** the pick map's `StandingZ`, which is
one line of `map.GetAverageZ` and would have dragged every upper-floor record, bridge deck and
second storey to the ground. `TryResolveZ`'s **return value** draws the line the repair needs: false
means nothing standable was found near the stored Z, and those records are **reported and left
alone**, because they are misplaced rather than stale and moving them would bury the fault.

**The editor asks the same question, because it used to ask the other one and that was the bug.**
Its `z?` badge compared the stored Z against the pick map's `StandingZ` and flagged anything more
than a storey out — which is every record standing on something other than bare ground, so it read
**13** (`town-2` on the bridge deck over a riverbed at -15, `uo-wp-79` on the Trinsic bridge, stock
`trammel.xml` spawners on upper floors) while `[NavAudit` read **0**. Two rules, two answers, and
the editor's named nothing anybody could fix. `[NavAudit` now exports the records themselves as
`staleZRecords` beside the count, the editor badges membership in that list and nothing else, a
nav save re-runs the audit quietly so the badge is at most one save behind, and read-only layers
— stock spawners, entities, the uo-offline reference — are never badged at all. `landz` still
answers the properties row's `z? 28 (ground -15)` and the cursor readout; it just does not decide.

**Neither finds a record authored on the wrong storey.** `trinsic-shop-tailor-2`'s tile has two
standable levels and the resample *confirmed* the upper one, because a hint wrong by more than the
window is ratified rather than corrected. Only a person spots that, and `[TileProbe <x> <y>` is
what shows them the two levels.

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
| `Custom.NavHomeWaypoint` | `uo-britain-bank` | Which component is "the main graph". Was the *largest* component, which held only while we were the biggest thing in the file — one adopt of 481 waypoints against Britain's 231 inverted it and reported Britain as the island. It is Britain's bank plaza, not a particular record: the default was `brit-bank-2` until the Britain rebase found uo-offline's waypoint on the same tile and merged ours into it. |
| `Custom.NavJoinRoadCandidates` | 6 | How many of the nearest candidates a **join** ranks by the road a bot actually walks — the engine's own route, both directions summed — before settling. The target used to be the nearest by straight line — Z ignored, walls ignored — which is what put a 33-tile road on `brit-tan-1`'s 4-tile join, and made 18 of the graph's 20 highest-detour edges joins. The pick now shortlists this many by line and floods each, taking the shortest road; it falls back to the nearest by line when none floods, so it can only improve on the old answer. Two `MovementPath` calls per candidate, one per direction. |
| `Custom.NavAdoptMergeRadius` | 6 | In a **rebase** adopt, how close one of our waypoints has to be to the road being proposed before it counts as the same road and is removed in favour of it. Half the hop cap, so a merged route end never moves by more than half a leg — and measured to the nearest point on a proposed **edge**, not the nearest proposed waypoint. That is the calibration: uo-offline authored on a 38-tile leg so their nodes stand ~16 apart with ours between them, and Britain has 44 of its 93 within six tiles of one of their *nodes* against 73 within six tiles of one of their *roads*. |

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

### The Britain rebase

**Britain's roads are uo-offline's now; its places are still ours.** The town was the one place
their graph had never touched, because Adopt refuses to propose over ground we have authored — and
the walk audit's evidence was a cluster rather than a list: every high-detour row in the file was
Britain's upper town, `brit-carp-3 -> brit-tan-1` being a six-tile edge with a thirty-nine-tile road.

`[NavAdopt <region> rebase` turns that skip off **for waypoints and edges only**. Destinations,
arrivals, sites, zones and routes keep it, so the records carrying authored work — names, tags,
`exclusive`/`exact` flags, positions placed by eye — cannot be proposed over. What keeps the result
one road network rather than two is a merge measured to the nearest point on a proposed **edge**;
`Custom.NavAdoptMergeRadius` is that distance. The full rule, the calibration behind it and the four
things a rebase has to do that an ordinary adopt never meets are in
`Scripts/Custom/Core/Navigation/README.md` under *The whole-facet rule*.

The rule itself is for the facet, not for Britain:

> **Wherever uo-offline has roads, theirs replace ours. Wherever they have none, ours stay
> authored.**

Adopting `1385,1495 360x300 rebase` gave 465 waypoints and 598 edges walked with **0 failures,
0 stranded, 0 unreachable and 0 islands**, removed 78 of our road waypoints, re-pointed 52 records
and walked 27 relinks. The file went from 608 waypoints and 655 edges to 996 and 1148, with all 53
destinations, all 115 arrivals and all 8 zones and 4 routes intact — then gained 12 more
destinations adopted from theirs.

**Where they have none, ours stayed.** Their graph reaches neither mine: the north-mine corridor and
the west-mine road `wp-1`…`wp-23` are still hand-authored. The west mine also needed a new stub, and
it does not run where anyone expected — the two southern arrivals sit on flat forest on the **far
side** of the cliff, with impassable rock and forest from y 1758 to y 1770, so the road comes round
from the east through `brit-minewest-1` and `-2` rather than down the face. `[BotWorkScout` had been
saying so on its own: *"no walkable hop from the face chain"*.

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

## Measuring the walker

Three instruments and a switch, because a clean `[NavAudit` and bots that fail walks are both true
at once and the audit cannot see why - and a bot standing still can be failing at that too.

- **`[WalkAudit`** walks the whole graph with real probe walkers - every edge both ways, every
  arrival from each approach - and reports the engine's route length against the authored straight
  line. **Two probe classes since 12 September 2026, reported per class**: a `BaseCreature`, which
  is the daily-life walkers' instrument and the only thing measuring `NavCreatureActor`, and a real
  `PlayerBot`, which is the fleet's. 5,020 walks in about eight minutes; `[WalkAudit bot` or
  `[WalkAudit creature` narrows it to one. `[WalkAudit selftest` proves the instrument - **per
  class**, because the aggregate version of that verdict was last-row-wins and could have read OK
  while one class passed the hop it is required to fail.

  **It has caught a real fault twice.** The first: the probe was refused by every occupant class a
  real bot walks through, because `BotShove` keyed on `PlayerBot` and a probe is a plain
  `BaseCreature`. The second is the reason there are now two of them - **the `BaseCreature` probe
  plans routes through movable impassables and a bot does not**, because
  `FastAStarAlgorithm.cs:100` sets `IgnoreMovableImpassables` from `bc.CanMoveOverObstacles` inside
  the `BaseCreature` branch only, and that property is `Core.AOS || Body.IsMonster` on an EJ shard.
  394 of 2,510 routes are longer for a bot and two do not exist for it, so every detour figure this
  instrument recorded before the rebaseline was measured on a more permissive pathfinder than the
  fleet's. See `Scripts/Custom/Core/Navigation/README.md`, *Two probes, and the rebaseline*.
- **The approach-tile scan**, in `[NavAudit full` and in every `[WalkAudit`. Both of the other two
  instruments start every measurement *on* an authored waypoint; a bot starts wherever its last hop
  stopped, anywhere inside `ArrivalRangeFor`'s 5x5 box. A **cliff** is a tile in that box the
  waypoint can reach that cannot path to a neighbour the waypoint itself reaches. **The graph reads
  0 cliffs today**, and getting there took two corrections rather than one:
  - **`CanFit` was the wrong first question.** Of the 44 stranded tiles the first run reported, 34
    could be pathed to from nowhere on the graph - building interiors behind a wall, two tiles from
    a waypoint standing in the street. The box is filtered by reachability now, which drops 0.3% of
    it and 17 of the 22 findings; `cliffTilesUnreachable` publishes how much was set aside.
  - **The five real ones were not the hop cap**, which was the standing hypothesis. Seven of their
    ten tiles put the goal comfortably inside it. All four waypoints stand in the street with their
    box reaching inside the shop next door, so they carry `arrivalRange: 1` - measured clean over
    all four 3x3 rings before it was authored.

  **Not one of the 44 stranded tiles was adjacent to its waypoint**, so an eight-neighbour version of
  this check would have reported nothing at all.
- **Failures per 100 walks.** `NavWalkFailures` counts walks started and completed, so a window's
  rate no longer depends on how long the window was. Both numbers are on `Bots.Population` and in
  `Data/Live/walk-failures.json`; the `walk-failures` token with body `clear` opens a fresh window
  without a restart.
- **Steps per bot-minute, by cause.** `[BotSteps` counts every tile a bot moves at
  `PlayerBot.OnLocationChange` - which is downstream of the walker, the recovery ladder, a
  gatherer's own `Move` and `BaseAI`'s wander alike - and attributes each to one of four causes
  from the state at the moment of the step. The denominator is **bot-seconds in that phase**, not
  wall time, so two windows with different populations are comparable; `bot-steps clear` opens a
  fresh one. It exists because neither obvious source can answer the question: `botlog.json` has no
  step event at all, and `entities.json` is a 2-second snapshot against a 400ms step.

  **A short window over-reports the BankSitter.** Its only residual is the once-per-visit walk to the
  scattered spot `PickScatteredHome` chose, so a two-minute window - in which every bot has just
  arrived - read 2.97 where the full fifteen read 0.41. Give it the whole window.
- **`Custom.MeasurementProfile=True`** buys walks instead of wall time: visit windows divided by 3
  and the session curve flattened. A loud yellow banner prints once a minute and `[CoreSmoke`
  reports it as a **WARN**. **A profile window is not comparable to an earlier one except per walk**
  - it changes crowding, and a per-minute figure measures crowding. Put it back to `False` before
  committing, like the `*OnStart` flags.

  **It used to claim a third dial, and the third dial never worked.** It doubled
  `BotSession.TargetNow`, which is the session *ceiling* - how many lifecycle bots may be live, not
  how many exist. The population is `GG_BotPop.xml`'s spawner slots, so a ceiling of 120 against 60
  authored slots fills all 60 and stops: window E measured `target 120 now (peak 60)` beside
  `60 bot(s) live`. It is gone rather than fixed, because making it real means a reversible switch
  rewriting a tracked file. **Raising the population is `population.target` in `bots.json`, then
  `[BotPopulationGen` → `[GG_Reimport` → `[BotPopulationAudit`.** The tick-budget clamp went with
  it; it bounded a multiplier that no longer exists.
- **`[BotPace auto`** samples any bot already walking, with no client to target one with -
  `[BotPace` itself `BeginTarget`s a mobile, so the one instrument that reads both the pace written
  to `CurrentSpeed` and the delay `DoMoveImpl` derives from it was unreachable from a headless run.
  The `bot-pace` token is the same thing from the bridge; both write `Data/Live/bot-pace.json`.
  Baseline at 60 bots: *"engine delay 100ms matches the pace: the bot steps at the pace it was
  given"*, 10.0 tiles per second, 203 of 203 steps at run pace.

## Health checks

### The orphans are still not the bots, and now the test can tell

The earlier answer was right and the objection to it was fair. `[BotOrphans` runs half a second
before `Cleanup.Run`, which is **after** `World.Load` - by which time a bot has already deleted
itself. So "zero belong to a `PlayerBot`" is exactly what you would see whether or not they were the
bots', because the parent is gone before anything can look. The owner check could not answer its own
question.

**The join key has to be the item's own serial**, recorded while the parent is still alive.
`BotOrphans.WriteCensus` runs at every `WorldSave` and writes `Data/Live/bot-items.json`: every live
bot, its mount, and every item on it or inside its pack and bank box, by serial. The orphan snapshot
now carries the serials it found. Match the two across a restart and the parent's absence stops
mattering.

Measured, over a save taken after twelve minutes of ordinary running:

| | |
| --- | --- |
| in the save | **57 bots owning 2,871 items** |
| orphans at the next boot | **26** - 13 `Backpack`, 13 `Gold` |
| orphan serials found in the bot census | **0 of 26** |
| bot-owned items that were orphaned | **0 of 2,871** |

**It is the second row that settles it.** A null owner check on 26 items is weak; *not one of 2,871
bot-owned items being orphaned* is not. If the ephemeral delete ran before its items were attached,
those 2,871 would be the orphan list.

**And the ordering says why it cannot.** `Timer.DelayCall(Delete)` queued inside `Deserialize` goes
onto the timer queue, and the **Timer thread does not start until after `World.Load` and after
`Initialize`** (`Server/Main.cs`, and the boot sequence in `CLAUDE.md` section 3). By the time the
callback can run, every item in the save is loaded and attached to its parent, so `Mobile.Delete`
cascades exactly as designed - `OnDelete`, then `OnParentDeleted` on every item, which is `Delete()`,
which recurses into its own contents. The 2,871-to-0 figure is that cascade working.

The composition still points where it pointed: **26 serials in 14 consecutive runs**, almost all
adjacent `Backpack`+`Gold` pairs with every gold's parent one of those packs, which is
`BaseCreature.AddLoot`/`PackGold` allocating exactly those two back to back and nothing in between.
A bot's pack is never gold-only - `EquipmentTable.RollOutfit` puts six to twenty items in before the
gold - so an orphaned bot pack would have dragged its robe and its shoes into the same list.

Two things worth keeping for whoever picks this up:

- **"Swept N stray bot mounts" did not appear at this boot at all**, so it is not the reliable
  companion the hypothesis assumed. When it does fire it is `SweepStrayMounts` clearing *parked*
  mounts, which reach the save precisely because the dismount stopped deleting them - a different
  mechanism from an orphaned pack, and one that already has an owner.
- **The count tracking the bot population is a coincidence of scale, not a link.** Both track how
  busy the shard has been; 26 here against 57 bots, and 132 on a longer-running shard, are the same
  loot packs accumulating at their own rate.

**No code changed.** Where those loot packs come from is ServUO's own creature lifecycle rather than
this shard's, `Scripts/Misc/Cleanup.cs` is upstream and untouched, and the ephemeral delete is
correct where it is. What changed is that the question is now answerable: run `[BotOrphans` after any
restart and the serial match either names a bot or rules one out.

**`Cleanup: Detected N inaccessible items` is not the bots.** That line appears on every boot —
30, 288, 50, 240, 240, 136 and 132 across this session's boots — and the standing suspicion was
that it was the packs and purses of PlayerBots, which delete themselves on load because nothing
about a bot survives a restart. Measured with `[BotOrphans`, which names each item's owner half a
second before `Cleanup.Run` takes the evidence away:

> **132 items with no facet. 0 belong to a PlayerBot.** 66 `Backpack` and 66 `Gold`, and every
> `Gold`'s parent is one of those backpacks — so each pack holds exactly one gold pile and nothing
> else.

Two independent reasons that rules bots out. `Mobile.Delete` **already** cascades — `Mobile.cs:3747`
runs `OnDelete`, then `OnParentDeleted` on every item, which is `Delete()`, which recurses into its
own contents — so a bot takes its backpack, its gear and its bank box with it. And a bot's pack is
never gold-only: `EquipmentTable.RollOutfit` allocates six to twenty items before the gold, and a
contained item inherits its owner's facet, so an orphaned bot pack would bring its robe, shoes and
tools into the same count. One backpack holding one gold pile is `BaseCreature.AddLoot`/`PackGold`.

**No fix was written, because nothing was ours to fix.** Where those loot packs come from is a
separate question about ServUO's own creature lifecycle.


### The console is readable from outside its window

**The shard's log IS its console, and nothing outside that window could read a line of it.**
`tools/dev.ps1` deliberately gives the shard its own window so a crash leaves its evidence on
screen; ServUO writes `Logs/Console.log` only under `-service`; `CustomLogger` goes straight to
`Utility.WriteConsoleColor`. So there was no file to follow.

`ConsoleTap` (`Scripts/Custom/Core/Bridge/ConsoleTap.cs`) tees `Console.Out` into a ring buffer and
publishes the last `Custom.ConsoleTapLines` (2000) to **`Data/Live/console.json`**, plus sign-ins
and sign-outs to **`Data/Live/logins.json`**. `Custom.ConsoleTap=False` turns the whole thing off.

**The seam that looks right is not the one that works.** `Core.MultiConsoleOut` is a
`MultiTextWriter` and `RemoteAdmin/Network.cs:23` adds a listener to it — but in a **Release** build,
which is what `build.ps1` produces, `ConsoleHook.Initialize` (`Scripts/Misc/Timestamp.cs:31-38`)
calls `Console.SetOut` with a writer over the raw stdout stream, and from that moment nothing
reaches `MultiConsoleOut` at all. (RemoteAdmin's console relay has therefore been dead in Release
for as long as both have existed. Noted; nothing here uses it.) So the tap wraps whatever
`Console.Out` *is*, from `[CallPriority(900)]` — after `ConsoleHook`, which is untagged and so
priority 0.

It installs **twice**: once at `Configure`, which is before `World.Load` and so covers the part of a
boot worth reading when a boot goes wrong, and again at `Initialize` over the hook. The first is
discarded rather than chained, because `ConsoleHook` does not chain; the gap between them is one
`Invoke` pass.

### What the process itself costs

`Core.Process` (`Scripts/Custom/Core/ProcessHealth.cs`) reports `Core.CyclesPerSecond` and
`AverageCPS`, managed heap, working set, and **how long each world save took** — which matters more
than it looks, because `World.Save` runs synchronously on the core thread and the Timer thread idles
through it, so the save duration is the length of the longest freeze the shard has. Nothing in
`Scripts/Custom/` reported any of these before, and `AdminGump` — which needs a client — was the
only place they appeared.

Baseline, 61 bots and 219,308 items: **0.26 s** to save, 385 MB managed / 529 MB working set.

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

**Editing works on the art too, and the pick map is why.** This passage read *"art is read-only this
session"* until the documentation pass of 11 September 2026, and the reason it gave was sound: the
isometric projection has no inverse, because a screen pixel names a world tile only once you assume
a Z - 2.7 tiles of error where Britain stands. The fix it predicted is the one that shipped. The
renderer emits a per-pixel pick map alongside each tile, so the question is answered by lookup
rather than by inversion, and selecting, placing and dragging all work. See *The pick map, and why
the inverse stopped mattering* in `tools/editor/README.md`.

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

**The two bot kinds are there now** - *Bot - fixed role* and *Bot - lifecycle seed*, upstream's
`PlayerBotFixed` and `PlayerBotLifecycle`. For those two, `kind` stops being a filter and becomes
the field that decides what is written: there is no `PlayerBotFixed` class to spawn, so the form
writes `PlayerBot/Role/Fixed/Seed/BankSitter` and the bot is told what to be by property setters
after it lands. Their Type list is the **behaviour** registry, exported by `[Vocabulary` from
`BotBehaviors`' own keys because there is no enum to reflect over. Most bot spawners are written by
`[BotPopulationGen`; this form is for the fixture the recipe cannot know about.

> **Saving a spawner from the browser had never written anything.** `filesWithEdits` returned a
> fixed three-name list and a spawn key is `spawn:<facet>/GG_Thing.xml`, so `save()` looped over
> nothing and reported *"Saved and reloaded."* The bridge's half was right and tested the whole
> time; nobody called it. `save()` now refuses out loud when it has edits and no file to put them
> in. The Bots panel's trailing slot had the same shape of fault - `updateCounts` swept `.count`
> document-wide, so every row read `0` where its stuck rung should be.

**The admin panel (5f).** Everything that acts on the shard rather than on its files, and the only
part of the editor that touches the shard *process*. Eleven buttons - save world, core smoke, bot
smoke, bots reload, nav audit and `full`, walk audit, world items, resync spawns behind a typed
`RESYNC`, regen bot spawns behind `REGEN`, broadcast, and restart behind `RESTART` - plus a live
console feed reading `Data/Live/console.json`, which had to exist before the buttons could.
**Every button runs without a client**: each case calls the command's underlying static method
rather than synthesising a `CommandEventArgs`. The three that take minutes - `[CoreSmoke`,
`[BotSmoke`, `[WalkAudit` - acknowledge *started* and report into Health and the console feed,
because `Poll` is a `Timer` callback on the game thread and dispatching one inline would freeze the
world for its whole length. See *The Admin section* in `tools/editor/README.md`.

See `tools/editor/README.md` for the save contract, the two tiers of validation failure - a dangling
edge id is a warning here, not a rejection - and what is still to bring back from the ModernUO
original when the editing UI lands.

## Tech debt

- **`NotifyStaff` is copied privately in three systems** (`RestrictedZoneSystem`,
  `NavigationSystem`, `DailyLifeSystem`). Three is tolerable; the fourth should become a
  `Custom/Core` helper rather than a fourth copy.
- **21 authored arrival points sit on tiles nothing can stand on** — `brit-bank`, `brit-square`,
  both taverns, `brit-inn`, five shops, a guard post. **A `[NavAudit` reading of 9 September 2026,
  not re-measured since**; treat the number as dated rather than as current telemetry.
  `[NavAudit` counts them, `nav-audit.json` carries them, and the editor badges each one with `!`
  so they can be placed by eye. The stand-tile sweep in the arrivals-as-places work will stop them
  mattering; the badge is what will get them actually fixed.

  **The exposure is narrower than this entry used to claim**, and the difference decides how urgent
  it is. It read *"every bot sent to it is a walk that cannot finish"*, which described
  `NavArrivals.TryPick` before it learned to scatter. It now takes the tile literally only for an
  `exclusive` arrival (reserved - one guard, precisely there) or an `exact` one (a forge bench, where
  two tiles off is out of reach of the anvil); **everything else goes through `Scatter`**, and the
  walker may additionally stop `NavWalker.ArrivalRangeFor` tiles short. So an ordinary unstandable
  arrival now costs a scatter rather than a failed journey, and **the points still worth fixing by
  hand are the exact and exclusive ones** - which the badge does not yet distinguish.
- **`Data/Live/vocabulary.json` shrank when `[Constructable]` became the filter**, which is
  correct — it now lists what a spawner can actually construct — but nothing has audited which
  types left. If a type that used to be offered turns out to be spawnable some other way, the
  filter is the thing to revisit, not the attribute.
