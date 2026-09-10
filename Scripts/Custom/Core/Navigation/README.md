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

## The last hop is the one nobody audited

Every waypoint hop was authored against the engine and `[NavAudit` walks it in both directions.
**The arrival is not.** `NavArrivals.TryPick` chooses a point, scatters it by
`Custom.NavArrivalScatter`, and validates the result **for standing and never for reachability** -
so the last hop of every journey aims at the one tile in the route nobody has ever asked the
pathfinder about.

Measured over one 30-minute window: **7 of 17 terminal failures were that**, and each looked
identical - a bot three tiles from a range-2 arrival with a clear road behind it and a wall in
front. Five of the seven had scattered from an arrival the audit already badges **unstandable**,
so the anchor was inside furniture and its neighbourhood was too.

Two changes, and between them the last hop now aims at a tile a mobile can stand on and the engine
will path to:

- **`NavArrivals.Scatter` resolves Z the walker's way.** It used `map.GetAverageZ`, which only sees
  land, so scattering around `brit-shop-tinker`'s arrival on a raised floor at z 11 asked about the
  street underneath it and every candidate came back at z 10. `CanSpawnMobile` passed - a street
  tile IS standable - and the bot was sent through a wall. It now uses `NavWalker.TryResolveZ`,
  whose window is the hint first and the land second.
- **`NavWalker.TryShiftWithinArrival` retargets on three faults, not one.** It fired only when the
  goal tile was occupied; it now also fires when nothing can stand there, or when the engine will
  not path to it from where the bot is, and every candidate it picks has to be pathable as well as
  standable. `Tick` asks it as the bot **halves its distance** to the goal (about 8, 4 and 2 tiles),
  and `HandleStuck` asks it again at the top of the ladder for a tile that became occupied since.

**The check is only asked from inside `MaxApproachDistance`, and that is not a saving.**
`FastAStarAlgorithm` is greedy with a 300-expansion budget, so from twelve tiles out it says "no
path" about goals it reaches comfortably from eight - the finding the approach cap exists for.
Retargeting on that answer would move a perfectly good arrival because the bot had not got there
yet. One check at the moment the bot crossed the cap spent itself on the least trustworthy reading
available, which is why it re-asks as the distance halves rather than once.

`Bots.Population`'s recovery line carries the split: `arrival retargets: taken 4, unstandable 1,
unreachable 7`. **A crowded tile is the world being busy; the other two columns are records
somebody should move**, so they log at `Info` with the destination and the tile while the crowded
case stays at `Debug`.

### uo-offline solves the same problem by never aiming at the arrival

Worth knowing before this is "simplified". Their final leg targets the approach **waypoint** plus a
random +/-5 jitter and completes at `FinalLegArrivalRange` **8** (`TravelerBehavior.cs:70-77`,
`:1834-1916`, `:1942-1963`); the arrival coordinate is reached, if at all, by a bounded cosmetic
drift afterwards that is **allowed to fail** (`DriftArriveRange` 2, 6 s total, 3 s without
progress, `:214-218`, `:2042-2094`). Their comment names our exact bug: *"The destination tile may
sit inside a building ... a bot routing from the street can't reach the inner tile and would grind
the outer wall."* There is no standability sweep anywhere in their arrival path.

**We cannot take their 8.** Our arrival ranges are load-bearing - a forge stand tile has to be
within 2 of both anvil and forge or `DefBlacksmithy.CanCraft` refuses - so we keep the authored
range and buy reachability with the sweep instead. That is the deviation, and this is its reason.

## The rescue lands somewhere a mobile can stand

`NavWalker.Teleport` used to move the mobile to `step.Point` at `ResolveZ(step.Point)` - and
`ResolveZ` answers with the **land Z when nothing is standable**, so a rescue aimed at a tile
nothing fits on put the mobile exactly there.

Measured in one window: three walks failed at `trinsic-dock-2`'s arrival (`2072,2865` z-15, which
`[NavAudit` now reports as *blocked by 0x1797 'water'*), each was teleported onto it, and each of
the three **next** failures in the ledger began at `2072,2865`, eleven tiles from a goal it could no
longer reach. The same shape again at `trinsic-shop-smith-2`. **Eight of seventeen failures, and all
three strikes on `uo-wp-990 <-> uo-wp-197-s1`** - a three-tile hop between two audited waypoints
with nothing wrong with it.

The rule is uo-offline's, from `MagicTravel.PickLanding` (`MagicTravel.cs:266-336`): validate every
candidate, try the **authored Z first and the ground Z second** (*"docks and shop floors sit ABOVE
what GetAverageZ reports - averaging under a pier returns the water level"*), escalate outward
because popular arrival points are permanently crowded, and fall back to the approach waypoint when
nothing near the anchor will take a landing. `TryResolveZ` already *is* their two-height rule.

Two deliberate differences:

- **Ours prefers a tile inside the arrival's own range**, which theirs has no notion of - no
  teleport site in their tree reads `ArrivalRange`, `DriftArriveRange` or `FinalLegArrivalRange`.
  It needs no special case: the scan goes outward from the arrival tile, so while the ring is
  inside the range those are the tiles tried first. It matters because our ranges are load-bearing.
- **If nothing is found, the mobile is not moved.** Theirs falls through to the raw authored
  coordinate as a last resort, which is precisely the behaviour this change removes. Standing in
  the road is recoverable; standing under a pier is not.

`OnTransition` gets the same validated landing, because a gate's far side is authored data with the
same exposure - it keeps the raw fallback, since a transition that does not happen leaves the
mobile on the wrong facet with a route it cannot walk.

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

### The corridor search prefers roads

`NavCorridor` weights each tile by what kind of ground it is - road 1, grass and sand 3, forest 4,
anything else 3, all in `Config/Custom.cfg`. A road is no shorter than the grass beside it, but it
is where a road GOES, and a corridor laid across open country is one nobody would ever have walked.

Weighting rather than restricting keeps it a preference: the search still crosses a field when a
field is the only way through, it just will not do so to save two tiles. Measured on the three
authored roads, with the weights flattened to 1 for the comparison:

| road | flat | weighted |
| --- | --- | --- |
| west gate to the west cliff | 315 tiles, **28%** on roads | 319 tiles, **78%** |
| south bridge to the wood | 154 tiles, **45%** on roads | 184 tiles, **83%** |

**The first attempt to measure that reported 100% for both**, because `IsRoad` was written as
"costs no more than a road" - true of every tile the moment the weights are equal. The
classification is a fact about the tile and the cost is a policy about it; only the second belongs
in config, and they are separate functions now.

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
| Mobiles in the way | invisible | invisible **to the pathfinder**, then refused **at the step** by whoever it is — see below |
| Closed doors | solid | walked through, if `CanOpenDoors` (`FastAStarAlgorithm.cs:92`) |
| Start tile | the authored waypoint, exactly | wherever the last hop stopped — anywhere in the `ArrivalRangeFor` box, 2 tiles by default |

The door row is the known false positive above. **The other two rows are false negatives, and
those are the dangerous ones, because a clean report is silent about them.**

#### Which movement implementation is installed, and why it decides the first row

**`FastMovementImpl` is what is live, not `MovementImpl`,** and the difference is the whole first
row. Both install themselves and the last one wins: `MovementImpl.Configure()`
(`Scripts/Services/Pathing/Movement.cs:65`) runs in the Configure pass, then
`FastMovementImpl.Initialize()` (`FastMovement.cs:23`) runs in the Initialize pass and replaces it,
keeping the first as a `_Successor` it delegates to only while `Enabled` is false. Nothing in this
tree sets `Enabled` to false.

`MovementImpl` refuses an uncontrolled `BaseCreature` any tile holding a live mobile
(`Movement.cs:345-356`, exemption at `:411`). **`FastMovementImpl` never builds a mobile list at
all** (`FastMovement.cs:361-484`). So on this shard:

- `MovementPath` plans straight through a standing vendor — which is why the audit's occupancy pass
  finds so many, 31 of 655 edges on one boot;
- the step is then refused by **the occupant's own `OnMoveOver`** (`Server/Mobile.cs:3216`), where
  a fellow bot or a daily-life actor consents through `BotShove` and a stock NPC or a real player
  does not.

This was written down the other way round for a while, and `NavWalker.TryShiftWithinArrival` and
`NavArrivals.Choose` both argued from it. `NavMovement.cs` now reports the live answer as the
`Nav.Movement` health check on every `[CoreSmoke`, so the next reader does not have to derive it:

```
[OK] Nav.Movement - Movement.Impl is 'FastMovementImpl': a standing mobile does not block a step
onto its tile, so an occupied tile is refused only by the occupant's own OnMoveOver, which is
where BotShove sits
```

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

#### And whether the engine is telling the truth at all

Every number on this page is worth what the engine's honesty is worth, so it was checked rather
than assumed. The occasion was a report that a player and bots could walk onto lamp posts and
braziers on this shard, where stock ServUO and the uo-offline ModernUO build refuse. **The
regression does not exist, and here is what was measured rather than argued:**

- **The movement path is byte-identical to upstream ServUO `pub57`.** Six tracked files in the
  whole tree differ from the fork point `d76bf444`; `Server/` is untouched entirely, and so are all
  eight files under `Scripts/Services/Pathing/`. The one `.cs` edit is `BotShove` in
  `BaseCreature.OnMoveOver`, which runs *after* `CheckMovement` has approved the tile
  (`Server/Mobile.cs:3138`) and can only loosen mobile-vs-mobile collision. Nothing under
  `Scripts/Custom/` assigns `Movement.Impl`, either global switch, or mutates `TileData`.
- **All 519 lamp posts on Trammel were swept, and 0 are passable** — `tile-probe sweep 0x0B20
  0x0B25`, asking `CheckMovement` from each of the eight neighbours with a real uncontrolled
  `BaseCreature`. Their positions, ItemIDs and Z values also match `Data/Decoration/**/*.cfg`
  exactly, 519 for 519, and every one of them is in all three item lists (`World.Items`, the
  sector's, and `GetItemsInRange`).
- **The brazier is a tiledata fact, not a bug.** `0x0E31`, `0x0E32` and `0x0E33` — the ones
  `[Decorate` places in towns — are **not** `Impassable` and have height 0. They are passable on
  stock ServUO and on ModernUO too. Only `0x19AA`, `0x19BB` and `0x1F2B` block.

So the audit's `0 blocked` over 655 edges was **not** measured against a permissive engine. Two
things can still put a mobile somewhere an impassable tile says it cannot be, and neither is
`CheckMovement` failing:

1. **Staff cut diagonals.** `FastMovementImpl.CheckMovement:441` requires *both* diagonal side
   tiles to pass only for `m.Player && m.AccessLevel < GameMaster`; everything else needs one. A
   GM squeezes past a lamp post on a diagonal where a player cannot. That is upstream's rule.
2. **`MoveToWorld`, which asks nothing.** Every teleport in this tree is a placement, not a step:
   `TryPickLanding` validates with `CanFit`, but `NavWalker.OnTransition`'s fallback does not, and
   neither do the fixed-coordinate placements in the probes and daily-life systems.

`RefusesSolidTile` in `NavMovement.cs` now asks the engine to refuse a step from 1842,2711 onto the
sandstone wall static at 1843,2711 on every `[CoreSmoke`, and reports both global switches beside
it. A map static rather than a decoration, so a re-decorate cannot turn the check into noise.

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

## The pathfinder is greedy, and it has a budget

Measured this session, and it explains more walk failures than the graph does.

`MovementPath` (`Scripts/Services/Pathing/MovementPath.cs:39`) runs `FastAStarAlgorithm`, whose
`Heuristic` (`FastAStarAlgorithm.cs`) returns **squared** distance — `(x*x) + (y*y) + (z*z)`, with
x and y scaled by 11 — while `cost` accumulates linearly at roughly one per step. A twelve-tile gap
scores about 34,800 against a cost of about 12, so the cost term is noise: **this is greedy
best-first search, not A\***. It then stops after `MaxDepth = 300` node expansions
(`FastAStarAlgorithm.cs:26`).

The consequence is that reachability is **directional**, which geometry never is. Walking to
`1840,2710` (the Trinsic alchemist arrival, ten Z above the road below it):

| from | distance | result |
| --- | --- | --- |
| `1840,2719` | 9 | path found |
| `1840,2720` | 10 | **no path** |
| `1837,2722` | 12 | **no path** |
| `1840,2710` → `1841,2723` | 13, reversed | path found |

Same tiles, same box, same algorithm. The road out of there is 30 tiles long and detours ten tiles
**east** before it can climb, so a greedy frontier aimed straight at the goal meets the wall and
unwinds tile by tile until the budget runs out. From inside the constriction it spills into open
ground immediately and finishes.

Two things follow, and both are already acted on:

- **Author uphill hops short.** An edge whose straight-line length is 12 can have a 30-tile walk.
  `trinsic-alchemist-slope-1..4` exist for exactly this: they split `uo-wp-188-s3 -> uo-wp-188-s4`
  into hops of at most six tiles, each verified with the engine. The shop end was then relocated in
  the editor - the arrival, `uo-trinsic-shop-alchemist-s1` and the spawner all moved to `1847,2711`
  - and `uo-wp-188-s4` moved onto the road at `1849,2711`, so the chain now runs
  `slope-3 -> uo-wp-188-s4 -> uo-trinsic-shop-alchemist-s1 -> slope-4` at 3, 2 and 1 tiles.
  **The order is the ground's, not the ids'**: `nav-route 1852,2713 1847,2711` walks
  `1852,2713 -> 1851,2712 -> 1850,2711 -> 1849,2711 -> 1848,2711 -> 1847,2711`, straight west along
  `y=2711`, and `slope-4` (1846,2711) is one tile *past* the arrival rather than before it - so the
  monotone chain ends there and `slope-4` is a leaf pointing at the shop centre. `uo-wp-188-s4`'s
  previous placement doubled back six tiles north and six south to a tile one east of `slope-4`.
- **`NavWalker.MaxApproachDistance = 8`** is the named port of uo-offline's approach cap (theirs is
  36, sized to the 38-tile box; ours is sized to the 300-expansion budget, which binds first). Past
  it the walker gives up aiming at the goal and walks back to the waypoint behind it, once per hop.
  The ladder's own nudges are what push a bot out of range, so without this the rescue is the cause.
- **`NavAudit` cannot see this**, because `CanWalk` asks the same pathfinder. An edge it calls
  BLOCKED may be walkable in the other direction, and one it passes may be unreachable in practice
  from where a bot actually stands. `NavWalkFailures`' `cause` field is the instrument for that,
  not the audit.

### A strike is not always a verdict on the edge

`uo-wp-990 <-> uo-wp-197-s1` carried three strikes and there is nothing wrong with it. Measured
with the engine, both directions:

| probe | answer |
| --- | --- |
| `nav-route 2069,2856 2066,2854` | **4 tiles**, every hop verified |
| `nav-hop verify` 2069,2856,-2 -> 2066,2854,-2 and the reverse | walkable both ways |
| the leg that actually failed, 2072,2865,-15 -> 2066,2854,-2 | **no path** |
| from where the validated rescue now lands, 2070,2863,-2 -> 2066,2854,-2 | walkable both ways |

A three-tile edge with a four-tile road. Every one of its failures started eleven tiles away at
`2072,2865` - open water under the pier, where the old rescue had put the bot after the PREVIOUS
hop failed at `trinsic-dock-2`'s arrival. **The strike was earned by the tile the walker
manufactured, not by the edge**, so no waypoints were added and none were needed.

That is the general shape and it is worth naming: `NavEdgeHealth` strikes the edge a hop lay
between, and the top rung is reached from wherever the ladder has drifted to. A struck edge should
be walked **from the failing bot's own start tile** - which is why `NavWalkFailure` records
`fromX/fromY/fromZ` at all - before anything is concluded about the road.

The arrival itself was the real fault and is fixed as data: `trinsic-dock-2`'s second arrival moves
from `2072,2865` z-15 to `2070,2863` z-2, the tile `[NavAudit`'s own nearest-standable field named,
seven tiles from `uo-wp-990` and inside the hop cap. Unstandable arrivals: 20 -> 19.

### What three measured windows say

Same shard, same graph, sixty bots walking themselves. `[BotSendTo` needs a connected client, so
nothing was steered in any of them.

| | **A** 30 min, before | **B** 30 min, arrival + rescue | **C** 15 min, + the shove edit |
| --- | --- | --- | --- |
| terminal failures | 17 | 18 | 8 |
| ... per minute | 0.57 | 0.60 | **0.53** |
| on an **arrival** hop | 11 | 8 | 3 |
| on a **waypoint** hop | 6 | 10 | 5 |
| the `trinsic-dock-2` cascade | 8 | **0** | **0** |
| struck edges | 3 | **0** | **0** |
| unstandable arrivals | 20 | 19 | 19 |
| rung entries (rungs 1-4) | - | 185 | 68 |
| ... per minute | - | 6.17 | **4.53** |
| next-step tile occupied | - | 40 | 5 |
| ... **by something a bot may not push** | - | **36** | **0** |
| arrival retargets | - | 26 | 7 |

**The totals barely move and that is not the finding.** What moved is which failures they are.

- **A -> B closed the two causes the work named.** The cascade is gone: no walk in B or C began on
  a tile the walker had manufactured, and the three strikes on `uo-wp-990 <-> uo-wp-197-s1` went
  with it. The arrival class fell from 11 to 8 while 26 retargets fired - 18 of them on a goal the
  engine would not path to, each one an eighty-second ladder climb that no longer happens.
- **The waypoint class rose from 6 to 10 in B**, which is the honest cost of the count staying
  flat. Every one of those is the shape §3 describes: a bot far off its own hop. Measured,
  `uo-wp-164-s1 -> uo-wp-164` and `uo-wp-138-s1 -> uo-wp-138` are both **13-tile roads for 12-tile
  edges and both verify walkable**; the bots failing them were standing at `1882,2805` (a counter)
  and `1850,2820` z10.
- **B -> C is the shove edit, and `unshovable` going to exactly zero is the whole of it.** 36 rung
  entries in thirty minutes were a bot refused by something it may not push; none in fifteen. Rung
  entries fell 27% per minute with it. The 5 remaining occupied next-step tiles all held mobiles a
  bot *can* push, which refuse nothing - that column is occupancy noise, not obstruction.

**What is left is three records, and the audit now names all three.** `brit-square`'s arrival at
`1475,1641` is a **stone wall** with no neighbour the engine will path to from the plaza side, and
it accounts for 5 of C's 8 failures on its own; `trinsic-shop-provisioner-2`'s at `1882,2805` is a
**counter** whose only standable neighbour is the shop floor a storey up, which strands a bot
against a 12-tile hop at z 0; and something puts bots on `2026,2832` z 20 in Trinsic, 22 tiles from
their own start waypoint. All three are authoring decisions rather than code, which is why they are
written down here rather than guessed at.

### The seam for a custom pathfinder

`MovementPath.OverrideAlgorithm` (`MovementPath.cs:58`) is a public static `PathAlgorithm` setter,
consulted at `MovementPath.cs:39` before `FastAStarAlgorithm.Instance` is chosen. Assigning it
swaps the algorithm for **every** caller in the server, bots and stock NPCs alike — so it is a real
seam and a loaded gun in the same object.

**This is a note, not a plan. Nothing here assigns it**, and a session that wants to should price
the blast radius first: every creature in the world paths through it.

## Adopting uo-offline's data

`Data/Custom/reference/uo-offline-nav.trammel.json` holds 3952 waypoints and 4291 edges converted
from uo-offline (see that directory's README). `NavAdopt` proposes a region of it.

**It cannot write navigation data.** It reads the reference, walks what it selected, and writes
`Data/Live/nav-adopt.json`. The editor draws that as unsaved records and the author accepts through
the ordinary save path. That is the safety property, and it is structural rather than a rule: there
is no code path from this file to `navigation.json`.

**Every edge is re-walked, not copied.** Theirs were authored against a 38-tile leg cap and 56% of
them are longer than `Custom.NavHopMaxTiles`, so each is flood-filled with `NavCorridor.TryPath` and
subdivided under the cap. That is a real pathfind per edge, which is why the work runs in
`LoopQueue` passes and writes progress rather than finishing inside one tick.

**And every hop is then pathed by the engine, both ways** - `NavCorridor.TryVerifyHopsBothWays`, the
test `[NavAudit` applies after a save, run before the proposal is written. The flood and
`MovementPath` answer different questions and can disagree about a step; a hop the flood accepted
and the engine refused fails its whole edge with the reason `flood ok, engine refused: a -> b`, so
the disagreement shows in the proposal rather than at the next audit. Hops of one tile are skipped,
as the audit skips them, because `MovementPath` returns no path for an adjacent goal.

**Ground we have already authored is skipped** - the union of our nav-zone rects and a hop cap's
radius around every existing waypoint, derived rather than a Britain rectangle, so it keeps holding
for the next town authored by hand. 158 of their waypoints sit inside Britain where we have 121.

**But an edge reaching into that ground becomes a JOIN.** Skipping authored ground on its own
guarantees the adopted region is an island - the road to Trinsic is exactly the edge whose Britain
end was refused. So the endpoint inside the authored region is mapped to our nearest waypoint
within the hop cap, the edge is walked like any other, and the hop that touches our waypoint is
marked `join`. **Adding an edge onto an existing waypoint is additive**: the waypoint's own record
is not touched, it gains a neighbour.

**A failed edge is reported, never written.** Its waypoints still land; the edge goes to `failures`
with the walker's reason. A waypoint left with no surviving edge at all is listed in `stranded`, and
Accept drops it - writing a point with no road is the shape of the fault the west road shipped with.

**A destination none of whose arrivals is inside the hop cap gets a corridor.** Nav.Data's rule -
an arrival further than the cap from every waypoint is one a bot can be sent to and cannot leave -
applied before the write. uo-offline authored against a 38-tile leg, so a forge 20 tiles from its
road is fine in their data and was skipped in ours: eight destinations in the Britain-Trinsic box,
all for our cap and none for theirs. After the component joins have walked, `PlanArrivalCorridors`
takes, for each such destination, the arrival nearest any reachable waypoint (the proposal's or
ours), mints a waypoint on that arrival's tile, and queues a corridor from the one to the other -
walked, engine-pathed and subdivided like any edge. Once it walks, the minted id goes **first** in
the destination's `waypoints` (a route ends at the first declared waypoint that exists, then appends
the arrival) and on the arrival itself. Every corridor is listed in `corridors` with its length and
the waypoint it starts from, flagged `review` past two hops so a far one is looked at before Save
rather than refused; one that fails to walk is in `failures` with the walker's reason and its
waypoint is withdrawn rather than reported as stranded.

**Save writes the component that reaches the graph and drops the rest.** After the walk, every
proposed waypoint is sorted into reached (a flood from our own waypoints along the proposed edges
gets there), stranded (no surviving edge at all) or unreachable (connected to each other, not to
us). The proposal carries `reachable`, `unreachable` and `blocked`; the editor writes the reached
set, drops the other two with both counts in its headline, and prunes arrivals against what will
actually be written. Dropped records stay in the reference layer, dashed, and the next box that
overlaps the saved ground joins onto them. The one refusal left is `blocked`: nothing proposed
reaches us, decided from that flood rather than from joins queued (a join whose walk failed used to
count). `status` says `done` only once joins, islands and pruning have all run; it used to say so
as soon as the last edge was walked, and the editor latched that snapshot.

**Two reference records on one tile fold into the first.** uo-offline's WP 140 and Honor Trail 1
both sit at 1824,2843 with an edge between them, a road of no length that `MovementPath` cannot
path and the flood reported as "no walkable road" from a tile to itself - which severed the 48
Honor Trail records from Trinsic. The first in reference order is kept; every edge, destination and
arrival naming the other is re-pointed to it, and the pair is listed in `folded` as `folded>kept`.
Their own dungeon cleanup merged 519 co-located nodes the same way.

**A record is written at the Z the walker stood on, with the reference's kept as `refZ`.** uo-offline
stores the water's Z for every generated dock: `uo-wp-990` at -15 under a deck at -2, and the
Trinsic dock arrivals the same. Where the walk's snap kept the record's tile and changed only its Z,
or an arrival resolves onto a surface at another Z, Adopt writes the standable Z and keeps theirs as
`refZ`, so a diff against the reference file explains the change; every correction is listed in
`corrected`. Destinations are left as authored - an anvil tile is never standable, and "the land
under it" would be a second wrong answer. Their `[fixdest` repairs their data the same way.

Every adopted record carries `source: "uo-offline"`, and every id is namespaced `uo-` including the
waypoints minted to subdivide a hop. Both exist so an adopted record stays tellable from one
measured against this shard's map: ours were flood-filled and audited here, an adopted one had its
edges walked at adopt time and nothing else.

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
3. **`[NavAudit` is a full re-path every time, and gets slower with the graph.** It pathfinds
   every walk edge with the real engine, which is the whole point of it - 132 edges take about 25
   seconds today. Adopting uo-offline's overworld would take the graph into the thousands, and the
   audit into minutes, at which point it reads as a hang rather than a check. **Later: an
   incremental mode that re-checks only edges changed since the last full run** - the snapshot at
   `Data/Live/nav-audit.json` already carries a timestamp, so the missing half is a per-edge
   fingerprint and a `[NavAudit full` to force the whole sweep.
4. **Arrival points are audited less strictly than edges** — `[NavAudit` checks edges, and the
   arrival picker validates a scattered tile with `CanSpawnMobile` at pick time, but an arrival
   point sitting in a wall will simply always scatter. `[NavDebug` is how you spot those.

## Reference

`nav-format-comparison.md`, alongside this file, maps the schema against `uo-offline-server`'s, field by field,
with a reason for every divergence — what was adopted from them, what was deliberately not, and
what neither side has.
