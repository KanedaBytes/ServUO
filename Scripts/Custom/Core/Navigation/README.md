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
{"id":"brit-bank-2","name":"Bank plaza","map":"Trammel","x":1434,"y":1697,"z":0,"arrivalRange":6,"tags":"town road bank"}
{"from":"brit-bank-0","to":"brit-bank-2","kind":"walk","tags":"road"}
{"id":"brit-bank","name":"First Bank Of Britain","type":"bank","map":"Trammel","x":1433,"y":1690,"z":0,"tags":"service guarded","waypoints":"brit-bank-2 brit-bank-0"}
{"destination":"brit-bank","x":1437,"y":1694,"z":0,"exclusive":false,"waypoints":"brit-bank-2"}
```

| Section | What it is |
| --- | --- |
| `waypoints` | Graph nodes. `arrivalRange` overrides the walker's arrival tolerance — set it to 1 for a doorway at an unusual Z where the mobile must step on the exact tile. `name` is **optional** and is only a label for the editor: absent means show the id, which for a road node like `brit-plaza-4` is the more useful thing, so most waypoints have none |
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

**Polygons, and the promised migration was free.** A zone may carry `"shape":"poly"` plus a
`points` token list (`"1443,1508 1456,1512 …"`) alongside `x`/`y`/`width`/`height`. Absent `shape`
means `rect`, so every record written before this stays valid and untouched.

Three details that are not decoration:

- **`x`/`y`/`width`/`height` remain, as the polygon's BOUNDING BOX.** `Contains` checks it first and
  rejects almost every point for four comparisons rather than a ray cast, and validation refuses a
  vertex outside it — one the box excludes could never be reached.
- **`Area` is the shoelace formula, not `width × height`.** `Nav.SmallestZoneAt` picks the smallest
  zone at a point, and a polygon's box is larger than the polygon; using the box would let a big
  diagonal zone beat a small rectangle inside it.
- **An unknown `shape` is FATAL, not a warning.** Treated as a rectangle it would silently make the
  zone contain its whole bounding box — wrong over a far larger area than intended, which for a
  restricted zone or a guarded region is the worst way to be wrong.

The editor mirrors the containment test tile-centre for tile-centre (`insidePolygon` in
`shapes.js`), because a vertex landing exactly on a tile boundary must not make containment depend
on which way a floating-point comparison happens to fall in one language and not the other.

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
  with no arrival point within the hop cap of a waypoint, **or with any individual arrival beyond
  it** (see below); a waypoint with no edges; a failing self-test.

## Stuck recovery

A hop that makes no progress for `HopTimeout` climbs a ladder, one rung per timeout. Translated
from uo-offline-server's leg recovery, which escalates repath → nudge-and-repath → extract.

| Rung | What it does | Why it is where it is |
| --- | --- | --- |
| 1 `Repath` | Drop the cached goal so A\* runs again from here | Most obstructions are a mobile that has since moved. The cheapest thing that works, and in practice it is the one that fires |
| 2 `Sidestep` | Step off the tile in a random direction, up to `SidestepTiles`, then repath | Breaks a wedge against scenery or a crowd, and gives the repath a different starting tile — which matters, because the audit only ever validated the canonical one |
| 3 `Door` | Open the closed door in the way, through `BaseDoor.Use` | Their bots get this free inside `Move` because a `PlayerBot` overrides it; a `BaseCreature` does not, so it has to be a deliberate rung |
| 4 `SkipWaypoint` | Give up on this waypoint and aim at the next one in the route | The cheap "route via a different waypoint". Two hops is at most twice the cap, still inside the engine's 38-tile box. Refused on the last step, where skipping would mean arriving somewhere the caller did not ask for |
| 5 `Teleport` | Move the mobile onto the waypoint | Visibly wrong, so it goes last and prefers nobody watching |

**Progress resets the ladder**, and the test is *closer than ever on this hop*, not *moved* — a
mobile pinned against a lightpost does the second all day. Adopted from their code, for their
reason.

**Every rung logs once at Debug, when it is entered** — never per tick. The top rung stays a
`Warn` naming the edge, because that line is the bug report.

### Every arrival must be routable, not just one of them

`Nav.TryRouteFrom` refuses to route from anywhere with no waypoint inside `Custom.NavHopMaxTiles`,
so **an arrival point beyond the cap is a one-way trip**: the picker sends a mobile there, it does
what it came for, then asks for a way home every tick and is told there is none, for ever. Silent,
permanent, and from outside indistinguishable from a broken walker.

`CheckQuality` used to ask whether **any** arrival was reachable, which a destination with one good
point and four stranding ones passes quietly — and did. `brit-mine-north` shipped with an arrival
**16 tiles** from the nearest waypoint against a cap of **12**, so roughly one miner in five was
stranded the moment it arrived, and the only symptom was an intermittent work-probe failure a long
way downstream. The check now names each offender and its real distance:

```
destination 'brit-mine-north' has 1 arrival point(s) further than the 12-tile hop cap from any
waypoint, so a mobile sent to one cannot route away again: (1450,1512) is 16 tiles from 'brit-inn-1'
```

The site itself was fixed by adding `brit-minenorth-2` and `-3` **on existing arrival tiles** —
coordinates `BotWorkScout` had already verified standable with `map.CanFit` at `NavWalker.ResolveZ`,
which is why two new edges across a mountain face passed `[NavAudit` first time. One approach
waypoint cannot cover a zone 26 tiles deep inside a 12-tile cap; the worst tile is now 9 tiles from
a waypoint rather than 21.

**`RungFired` is an optional callback beside `Arrived`**, raised at the same moment with the rung
and the reason. It exists so a consumer can record recovery in its own diagnostics — the bots'
event log subscribes to it — without this layer learning what a consumer is. That is the same
inversion `NavigationSystem.RegisterAuditor` uses for the data checks, and for the same reason:
navigation is Core and must not reach upward. A subscriber that throws is caught and logged rather
than being allowed to break the recovery ladder it is watching.

**A watched walker escalates; it does not freeze.** Previously, a walker with its retries spent
held position for as long as a player stood within `PlayerNearTiles`, resetting its deadline
without repathing — unbounded, and it never tried anything again. Now the recoverable rungs are
cycled up to `WatchedCycles` times (about two minutes of genuine attempts), and only then does it
teleport in view. An NPC frozen against a wall until the player wanders off is a worse thing to
watch than one that steps around a corner.

Measured on a live boot: four rung entries across two walkers, all of them rung 1, three recoveries
and no `Warn` at all — on exactly the edges the audit had flagged as occupied.

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
walkers plus a bank crowd meet one constantly, and they also block each other. **On a measured
boot, 14 of 86 edges were occupied at a single instant** — one in six — and the walkers that got
stuck got stuck on exactly those edges.

So the second pass below now reports occupancy directly, and `NavWalker` climbs a recovery ladder
rather than giving up after two repaths. But **a `could not walk` line is still the symptom, not
the bug**: check occupancy and the approach tiles before believing the data is wrong, because the
geometry pass has already told you the road exists.

### The second pass: `OCCUPIED`

After an edge passes the geometry check, the audit re-walks the path the engine just returned and
reports the first thing standing on it:

```
OCCUPIED 'brit-cour-2' -> 'brit-prov-1': geometry is fine, Stanton (Fisherman) is standing at (1471, 1668)
```

**A warning, never a block.** Occupancy is a fact about right now, not about the data — a vendor
wanders off, a crowd disperses — so failing the audit on it would make a clean run depend on the
weather. `blocked` stays false, the editor does not colour the edge red, and `[NavAudit` still
passes. What it buys is that a permanently-parked shopkeeper stops being invisible.

It does not spawn a probe creature to find this out; that would put a mobile in the world for the
sake of a report. It re-walks `MovementPath.Directions` and matches the engine's own rules exactly
— `CanMoveOver` lets a walker pass the dead, dead bonded pets and hidden staff, and the goal tile
is exempt — so it does not invent obstructions the walker would not meet.

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
