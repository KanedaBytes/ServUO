# Custom/Core/Navigation

The single source of truth for **where things are and how to get there**. Britain daily life
(step 4b) uses it now; the PlayerBots system will use the same data later, which is why the
schema is designed for bots rather than for daily life.

Config lives in `Data/Custom/navigation.json`. Namespace is `Server.Custom` — flat, like
everything else under `Custom/Core/` (a namespace segment called `Core` would shadow
`Server.Core`; see `../README.md`).

## The constraint everything else follows from

**ServUO's `FastAStarAlgorithm` searches a 38×38 box centred on the midpoint of start and goal**
(`Scripts/Services/Pathing/FastAStarAlgorithm.cs:26-32`, `MaxDepth = 300`). A hop longer than
that cannot be pathed *at all*, and — worse — `MovementPath` returns no path silently, so the
mobile walks into scenery rather than failing loudly. Even inside 38 tiles, a detour around a
building can leave the box.

So travel is **two-layer**: a coarse graph of short, explicitly authored hops, each one walked by
the engine's own pathfinder. `Custom.NavHopMaxTiles` (default **12**) is the authored cap. The
ModernUO shard independently arrived at 15 for the same reason.

Two more ServUO facts that shape the code:

- **`BaseAI.MoveTo` compares its cached goal by reference** (`BaseAI.cs:2644`). `Point3D` is a
  struct, so passing one boxes a fresh object every tick and rebuilds the `PathFollower`,
  destroying path persistence. `NavWalker` holds a `NavGoal` — a class — for the length of a hop.
- **`PlayerRangeSensitive` stops a creature's AI timer** when no player is in its sector, so a
  walker driven from its own `OnThink` freezes the moment nobody is watching. One shared timer
  drives every `NavWalker` instead.

## Schema

Four record types plus edges, cost tags and self-tests, all in one file. **Every record is flat
scalars on one line**, which is why list-valued fields are space-separated strings rather than
JSON arrays: `JsonConfig.SerializeCompact` only collapses a container whose children are all
scalars, so a nested `"tags": [...]` would expand its record over eight lines.

```json
{"id":"brit-bank-2","map":"Trammel","x":1434,"y":1697,"z":0,"arrivalRange":6,"tags":"town road bank"}
{"from":"brit-bank-0","to":"brit-bank-2","kind":"walk","tags":"road"}
{"id":"brit-bank","name":"Britain Bank","type":"bank","map":"Trammel","x":1433,"y":1690,"z":0,"tags":"service guarded","waypoints":"brit-bank-2 brit-bank-0"}
{"destination":"brit-bank","x":1437,"y":1694,"z":0,"exclusive":false,"waypoints":"brit-bank-2"}
```

| Section | What it is |
| --- | --- |
| `waypoints` | Graph nodes. `arrivalRange` overrides the walker's arrival tolerance — set it to 1 for a doorway at an unusual Z where the mobile must step on the exact tile |
| `edges` | **Explicit links only, never derived from proximity.** `kind` is `walk` or `gate` |
| `destinations` | Places worth going to. `type` is a free token (`bank`, `tavern`, `shop`, `home`, `gate`, `guard`, `work`, `wander`) |
| `arrivals` | Standable tiles at a destination — a separate concept from the destination's own centre |
| `zones` | Tagged rectangles |
| `routes` | **Authored** waypoint sequences (`cycle` / `oneway` / `pingpong`) — patrol loops, walks home. The thing a search cannot invent |
| `costTags` | Edge cost multipliers, applied as `distance × Π(multipliers)` |
| `selfTests` | Canary route pairs, checked by `Nav.Data` |

**`z` is mostly advisory.** A hop's goal Z is the authored Z when a mobile can actually stand
there, and `map.GetAverageZ(x, y)` when it cannot — `NavWalker.ResolveZ`, which `[NavAudit` uses
too so the two always agree. So a slightly wrong Z self-corrects, but an authored Z is what makes
an upper floor or a raised entrance reachable at all, since `GetAverageZ` only sees land and
would aim at the ground beneath it.

**Polygons later, without a migration:** add `"shape":"poly"` plus a `points` token list
alongside `x`/`y`/`width`/`height`, default `shape` to `"rect"`, and make `NavZone.Contains` a
switch. Every record written today stays valid.

## API

Game-thread only — `FastAStarAlgorithm` keeps its whole working set in shared static fields on a
singleton. Background callers go through `LoopQueue.Post` / `TryPostAndWait`.

```csharp
NavDestination bank = Nav.Destination("brit-bank");
NavWaypoint    near = Nav.NearestWaypoint(m.Location, m.Map, 12);
List<NavDestination> taverns = Nav.Destinations(Map.Trammel, "tavern", null);

NavRoute route;
string error;
if (Nav.TryRouteFrom(m.Location, m.Map, "brit-tavern-blue-boar", m, out route, out error))
{
    new NavWalker(creature).Follow(route);
}

Point3D spot;
Nav.TryPickArrival("brit-guard-west", creature, out spot);
```

Ids passed to `TryRoute` may name a waypoint **or** a destination; a destination resolves to its
declared approach waypoint, falling back to the nearest. Declared first, never the other way
round, so an author can override a bad automatic choice.

## Arrival points

Stochastic, not allocated: **random pick plus a scatter offset**, no claim/release registry. It
is stateless, survives a restart, cannot leak, and `CanSpawnMobile` already refuses an occupied
tile. Both reference shards converged on this after trying otherwise.

The exception is `"exclusive": true` — a guard post, where the point is that the guard stands
*precisely* there. Exclusive spots are taken exactly, with no scatter, and are skipped while
anyone is within a tile. That is a live world query, so nothing has to be released.

`Nav.Data` warns when a destination has exclusive spots but fewer than two shared ones: a guard
post with nowhere for a second guard to stand is a data bug, not a runtime one.

## Failure contract

**A failed load keeps the live data.** Every error path returns before the store is replaced, so
a bad edit can never leave the shard with no navigation. Severity is split so one typo cannot
take it offline:

- **Load errors** (fail, keep live data): blank or duplicate id, unknown facet, non-positive zone
  size, unknown `kind`/`mode`, self-edge, a `schemaVersion` from the future.
- **Dropped with a warning** (load succeeds): edge naming an unknown waypoint, cross-facet `walk`
  edge, arrival for an unknown destination. A route with a dangling id is withheld from the index
  rather than handed to a consumer that would walk into the gap.
- **`Nav.Data` warnings** (quality): destination with no arrival points; over-cap edge or route
  leg; more than one walk-connected component on a facet with destinations in both; a destination
  with no arrival point within the hop cap of a waypoint; a waypoint with no edges; a failing
  self-test.

## Commands

| Command | Access | Effect |
| --- | --- | --- |
| `[NavReload` | GameMaster | Re-read the JSON (alias `[ReloadNav`). On failure the live data is kept and it says so |
| `[NavDebug [radius]` | GameMaster | Toggle markers on nearby waypoints, arrival points, destinations and zone corners |
| `[NavMark [id] [nolink]` | GameMaster | Waypoint at your feet, linked to your **previous mark this session** |
| `[NavRecord on\|off [tiles]` | GameMaster | Auto-drop a chained waypoint every N tiles walked |
| `[NavLink <a> <b> [gate]` / `[NavUnlink <a> <b>` | GameMaster | Add or remove one edge |
| `[NavDelete <id>` | GameMaster | Remove a waypoint and every edge touching it |
| `[NavArrival <destId> [exclusive]` | GameMaster | Arrival point where you stand |
| `[NavRoute <from> <to>` | GameMaster | Print the computed hops and cost |
| `[NavAudit` | Administrator | Pathfind every walk edge against real map data |

**`[NavMark` links only to your previous mark, never by proximity.** Linking by nearness would
silently connect two waypoints through a building — which is exactly the class of bug the
explicit-edges rule exists to prevent. `[NavMark nolink` starts a new chain.

Auto-generated ids are `<zoneTag>-<n>`, taken from the smallest nav zone containing the point.

**Writes apply to live data first, then save.** Each mutator keeps a `.bak`, writes, and **rolls
the change back if the write fails**, so memory and disk can never disagree and `[NavDebug`
reflects a change immediately. `[NavReload` is for edits made outside the game.

### `[NavAudit`

Pathfinds every walk edge with the real engine against real map data — the only way to check seed
coordinates without a client. `BLOCKED` means a wall or river between two waypoints; `FAR` means
over the cap. Both directions are tested, because a one-way ledge strands a walker halfway
through a patrol. Too costly for a health check, so it is a command plus
`Custom.NavAuditOnStart` for a headless run.

**A waypoint at a closed door is a false positive.** Read the report before editing.

### What a clean audit does not tell you

**A pass means the road exists, not that everyone will get through.** The audit probes with a
`Point3D`; `NavWalker` probes with the mobile. The engine treats those differently, and not only
in the forgiving direction:

| | Audit probe (`Point3D`) | Walker probe (uncontrolled `BaseCreature`) |
| --- | --- | --- |
| Mobiles in the way | invisible — `checkMobs` is false | **every tile but the goal is blocked by whoever stands on it** (`Movement.cs:411`) |
| Closed doors | solid | walked through, if `CanOpenDoors` (`FastAStarAlgorithm.cs:92`) |
| Start tile | the authored waypoint, exactly | wherever the last hop stopped — anywhere in the `ArrivalRangeFor` box, 2 tiles by default |

The door row is the known false positive above. **The other two rows are false negatives, and
those are the dangerous ones, because a clean report is silent about them.**

Seen in practice: `Perrin could not walk brit-prov-1 -> brit-cour-2` while the audit reported all
86 edges clean. `brit-prov-1` (1469,1668) has a stock vendor standing on it permanently — the
audit cannot see her, and every walker can.

This matters more as the graph gets busier. One courier meets an obstruction rarely; a dozen
walkers plus a bank crowd meet one constantly, and they also block each other. `NavWalker` already
degrades sensibly — two repaths, then hold if a player is watching, else teleport and log the edge
— so nothing breaks. But **the log line is the symptom, not the bug**: check occupancy and the
approach tiles before believing the data is wrong, because the audit has already told you the
geometry is fine.

One consequence worth knowing at scale: while a player is within `PlayerNearTiles` the walker
holds instead of teleporting, and it resets the deadline each time without repathing — so a
walker obstructed in view of a player stays put until the player leaves.

## Seed data, and what is still missing

Britain, Trammel: 75 waypoints, 86 walk edges, 27 destinations, 65 arrival points, 6 zones,
4 routes. **`[NavAudit` reports 0 blocked and 0 over-cap edges** — every edge has been pathed in
both directions against real map data.

Landmarks come from `Spawns/trammel.xml` vendor spawn points, `Data/Regions.xml` town bounds, and
the ModernUO shard's `britain-daily-life.json` (which independently confirms six shop points and
supplies their Z). The roads *between* landmarks started as interpolation and were then corrected
against the map — the audit rejected 20 of the first 78 edges, and the replacements were
flood-filled from the map itself. Two things that came out of that are worth knowing, because
guessing got both wrong:

- **The descent from the upper town runs east**, `brit-plaza-1` → `brit-desc-1` (1472,1639) →
  `brit-desc-2` (1467,1651), not down the middle of the block.
- **The northern district is at Z 30, not Z 20**, and `1471,1555` is a dead-end pocket: the road
  to the smithy goes `brit-north-c` → `brit-north-d` (1477,1556) → `brit-north-4`.

Known gaps, in the order worth fixing:

1. **The moongate (1336, 1997, 5) is not in the data.** It is ~90 tiles south of the town and
   every waypoint on the road there would be invented. Walk it with `[NavRecord` and it becomes
   real data in one pass.
2. **Only the west gate is seeded.** The north Chaos guard posts (1521/1525, 1457) and the
   northern approach are outside the seeded box.
3. **Arrival points are audited less strictly than edges** — `[NavAudit` checks edges, and the
   arrival picker validates a scattered tile with `CanSpawnMobile` at pick time, but an arrival
   point sitting in a wall will simply always scatter. `[NavDebug` is how you spot those.

## Reference

`nav-format-comparison.md`, alongside this file, maps the schema against `uo-offline-server`'s, field by field,
with a reason for every divergence — what was adopted from them, what was deliberately not, and
what neither side has.
