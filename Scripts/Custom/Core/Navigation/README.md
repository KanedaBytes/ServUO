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

**The cap is not the budget an authoring tool gets.** A walker is allowed to stop up to
`NavWalker.ArrivalRangeFor` tiles short of the waypoint it was heading for — 2 by default — and it
then plans the next hop from *there*, so an edge authored at the cap is a `cap + 2` hop for the bot
that actually has to walk it. `NavAdopt.Subdivide` cut at the cap for as long as it existed, which is
why **566 of the graph's 1153 walk edges are authored at exactly 12**, and it is the mechanism behind
`uo-wp-194-s1` (commit `239ff6e2`). It now cuts at `cap - ArrivalRangeFor`, expressed that way rather
than as a literal so that moving the cap moves the budget with it. **Existing edges were not
rewritten**; this changes what a future adopt mints.

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
| `[WalkAudit [probes\|selftest]` | Administrator | **Walk** every edge in both directions and every arrival from each approach, with real probe walkers |

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
their own start waypoint.

**Two of the three were authoring and one was not.** Both arrivals were re-placed in the editor and
are gone from the count. `2026,2832` was never a placement at all - the recovery ladder walks bots
there, nine tiles at a time, and window D below has the trail. It is recorded here as it was written
because being wrong about which of the three was code is the kind of thing worth leaving visible.

### Window D, and the two mechanisms it named

Same shard, same graph, 50-53 bots walking themselves, 30 minutes, nothing steered. The three
records window C was left waiting on had by then been fixed as data - `brit-square`'s stone-wall
arrival and `trinsic-shop-provisioner-2`'s counter were re-placed in the editor, and
`brit-shop-tinker` was moved onto the building it is actually in (its only arrival, 1421,1651, was
inside a plaster wall 20 tall: `CanFit` false and all eight neighbour steps refused).

| | **C** 15 min | **D** 30 min |
| --- | --- | --- |
| terminal failures | 8 | 6 |
| ... per minute | 0.53 | **0.20** |
| on an **arrival** hop | 3 | 4 |
| on a **waypoint** hop | 5 | 2 (**both skip-marched**) |
| rung entries (rungs 1-4) | 68 | 85 |
| ... per minute | 4.53 | **2.83** |
| next-step tile occupied | 5 | 8 |
| ... by something a bot may not push | 0 | 6 |
| arrival retargets | 7 | 6 (taken 1, unstandable 1, unreachable 4) |
| struck edges | 0 | 2 (**neither earned**) |
| unstandable arrivals | 19 | **15** |

The unshovable column is not a regression of the shove edit. Every one of the six is a stock NPC in
a doorway - `Jeanette (InnKeeper) at 1457,1526` accounts for a run of them on its own - which is the
yield-to-stock-NPCs rule working as written. C's zero was fifteen quiet minutes, not a floor.

**The failure per minute fell by 62% and the two waypoint failures that remain are not about roads
at all.** Both carry the new progress field, which is what made them legible:

```
Lirien could not walk 'uo-wp-174' -> 'uo-wp-173-s1' ... It stopped 34 tile(s) SHORT of the goal
  - and it SKIPPED its way there, so the edge is not the suspect.
  [at 2026,2832,20; started 2024,2844,0; step 10/200, 5 skipped]
```

#### The skip and the re-anchor were fighting each other

Lirien's rung log is the whole mechanism, eight minutes of it:

```
17:37:25  stuck at 2035,2832 [Repath]
17:38:26  stuck at 2035,2832 [SkipWaypoint] - skipping to 'uo-wp-193'
17:38:46  stuck at 2030,2832 [None] - 15 tiles from the goal, past the 8-tile approach cap
                                      - walking back to 'uo-wp-194'
17:40:06  ... [SkipWaypoint] - skipping to 'uo-wp-174-s2'
17:40:26  ... [None] - 17 tiles from the goal ...
17:42:06  ... 19 tiles ...      17:43:47  ... 22 tiles ...      17:45:27  ... 34 tiles ...
```

`TryReAnchor` walks the mobile **backwards** toward the waypoint behind it. `TrySkipWaypoint` moves
the goal **forwards**. And a skip calls `ResetHop`, which clears `_reAnchored`, so the pair repeats
for as long as the walker keeps failing - the gap growing every cycle, 15 to 34 tiles. The frozen
watchdog never fires, because the ladder's own sidesteps and re-anchor walks supply the two tiles of
movement it looks for.

Three consequences, all measured rather than reasoned:

- **`2026,2832` z 20 is not a placement.** It looked like something was putting bots on the first
  floor of a Trinsic provisioner 22 tiles from their start waypoint. Nothing was: Lirien *walked*
  there, nine tiles of ladder drift west along the road from 2035,2832, over eight minutes. Every
  walker-initiated `MoveToWorld` now logs where from and where to, so the next one of these is one
  line rather than an inference.
- **A "stopped N tiles SHORT" on a short edge is the index, not the road.** `TrySkipWaypoint` now
  refuses while the goal is already past `MaxApproachDistance`: skipping cannot help a walker that
  could not reach the *nearer* waypoint, so past the cap the ladder tops out and the rescue happens
  at about a hundred seconds and fifteen tiles instead of eight minutes and thirty-four.
- **A strike now requires the walker to have come within the hop cap of the edge.** Both of D's
  strikes were skip-marched onto edges `[NavAudit` walks cleanly in both directions, and a strike is
  a fifteen-minute cost multiplier - so an unearned one deforms routing for a quarter of an hour on
  no evidence. `_bestDistance` is the closest this walker ever got to this step, and it is now the
  gate. This is the same test the section above already asks a person to apply by hand.

#### And an arrival can be scattered onto a doorway

`NavArrivals.Scatter` validates a candidate with `CanSpawnMobile`, which is a true answer with a
lifetime of seconds when the tile is a doorway. Measured: a bot sent to 1845,2710 at the Trinsic
alchemist arrived to find *nothing can stand on it*, and probing that tile six times over a minute
showed it flipping between a wooden door with `CanFit` false and an empty tile with `CanFit` true.
`BaseDoor.Open` **moves** the item by its `Offset` (`BaseDoor.cs:88-94`), so a door occupies one tile
shut and its neighbour open, and the scatter had validated the half-second the doorway was clear.
Scatter now refuses both tiles of every door within the scatter radius. `TryShiftWithinArrival` had
recovered this one, which is why it cost a ladder climb rather than a failure.

### Window E, the first with a denominator

Same shard, same graph, 60 bots, **17m 11s**, nothing steered. The first window measured under
`Custom.MeasurementProfile`, and the first measurement of last session's skip and re-anchor fixes,
which were built *after* window D.

**Read the last column, not the second.** A profile window thirds the visit windows and flattens the
session curve, so it produces walks faster than wall time — which is the point, and which makes its
per-minute figure incomparable with A–D by construction.

> **It said "doubles the target" too, and that dial has since been deleted because it never did
> anything.** The multiplier moved `BotSession.TargetNow`, the session *ceiling*, while the
> population is `GG_BotPop.xml`'s spawner slots — this very window recorded `target 120 now
> (peak 60)` beside `60 bot(s) live` and nobody read the two numbers together. So window E's walks
> were bought by the visit divisor and the flat curve alone, at **60 bots, not 120**. The figures
> below are unaffected; what changes is what they are a measurement *of*.

| | **C** 15 min | **D** 30 min | **E** 17 min, profile |
| --- | --- | --- | --- |
| walks started | – | – | **372** |
| terminal failures | 8 | 6 | **1** |
| ... per minute | 0.53 | 0.20 | 0.06 |
| ... **per 100 walks** | – | – | **0.27** |
| on an **arrival** hop | 3 | 4 | 0 |
| on a **waypoint** hop | 5 | 2 (both skip-marched) | 1 (**0 skip-marched**) |
| rung entries (rungs 1–4) | 68 | 85 | 30 (7.9 per 100 walks) |
| next-step tile occupied | 5 | 8 | 7 |
| ... by something a bot may not push | 0 | 6 | **1** |
| arrival retargets | 7 | 6 (taken 1, unstandable 1, unreachable 4) | **2 (taken 2, unstandable 0, unreachable 0)** |
| struck edges | 0 | 2 (neither earned) | **0** |
| unstandable arrivals | 19 | 15 | **0** |

- **Zero skip-marched failures**, which is what window D's fix was for. D's only two waypoint
  failures were both the ladder marching a rooted walker's index; E's single failure carries
  `skips 0`, `index 3 of 203`, and a start tile 14 tiles from where it gave up — a bot that
  genuinely travelled and genuinely stopped, which is the shape that was being drowned out.
- **Arrival retargets went to zero on both diagnostic columns.** `unstandable 0` and
  `unreachable 0` against D's 1 and 4 is Sean's fifteen relocations, and it agrees with
  `[NavAudit` independently reading 0 unstandable arrivals against D's 15.
- **The unshovable column fell 6 → 1**, which is the shove symmetry: a daily-life actor can now
  step through a bot as a bot steps through it.
- **The tick cost says the population dial has room and is not the binding constraint.**
  `0.5 ms mean / 11 ms max of 2000 ms over 1144 passes at 60 bots` - a pass count taken while the
behaviour ticker ran two timers, so it is twice the passes that interval should have produced (see
the Bots README). The cost per pass is the measurement; the rate is not. The profile asked for 120 and
  got 60 — `target 120 now (peak 60)` — because the population is a *file*: the recipe is derived
  from `population.target` and `[GG_Reimport` turns it into 60 spawner slots, so doubling the
  session ceiling fills the slots that exist and stops. E's walks were bought by the visit divisor
  and the flat curve alone. Raising `population.target` in `bots.json` and re-importing is the
  honest way to raise it, and the tick figures say it is affordable.

## `[WalkAudit` - the second instrument

`[NavAudit` asks the engine whether a step *could* work. This asks real walkers to walk it.

The section above — *What a clean audit does not tell you* — lists why those are different
questions, and four measured windows have now been read against the gap: 655 edges, 0 blocked,
0 over cap, and a failure ledger with entries in it. The audit probes with a `Point3D` from the
authored waypoint; a walker is an uncontrolled `BaseCreature` starting wherever its last hop
stopped, and `FastAStarAlgorithm` is greedy with a 300-node budget and therefore **directional**.

Twelve probe walkers at mounted pace take **every edge in both directions and every arrival from
each of its approach waypoints**, skipping destinations tagged `pending-road`. Per walk it records
pass or fail, the engine's own route length against the authored straight line, the steps actually
taken, the seconds, and on a failure the stop tile, the cause, the blocker and the rungs burned.
Output is `Data/Live/walk-audit.json` plus a console report; the editor has a **Walk audit** button
beside **Audit**, and every failure becomes a Problems row that jumps to the tile the walk died on.

**And it shoves exactly as a bot does, which took a fourth correction to get right.** The three
below are about the probe not *disturbing* what it measures. This one is the opposite and it
falsified in the direction that matters: `BotShove.OnMoveOver` keyed its mover branch on
`PlayerBot`, and a probe is a plain `BaseCreature`, so a probe stepping onto **another bot, a GG
shopkeeper, a daily-life patron or a stock NPC** was refused where a real bot walks straight
through — the last of those through `MODIFICATIONS.md` entry 5's guard, which calls the same
predicate. The instrument was strictly more obstructed than the thing it stood in for, for every
occupant class, and every refusal cost it a rung — which is precisely what the **FRAGILE** list is
made of. The classification lied the other way too: `OccupiedUnshovable` asks
`NavWalkFailures.Shovable(blocker)`, a question about what a *PlayerBot* could push, about a step
the *probe* had taken under a different rule.

`IBotMover` is the fix — an interface in `NavWalkFailures.cs` with exactly two implementers,
`PlayerBot` and `WalkAuditProbe`, tested for in exactly one place. **No engine edit was needed**:
entry 5's guard already routed through `BotShove`, so widening the predicate in `Custom/` widened
the guard for free. A stock vendor in a doorway still jams a bot and a bot still jams a stock
vendor; that named deviation is untouched.

**The self-test caught it, which is what a self-test is for.** `[WalkAudit selftest` read
`SELF-TEST BROKEN: seed failed as required, control FAILED and should not have` on the shipped
build — its must-pass control at `1849,2711` is clean wooden floor with `CanFit` true and all
eight neighbour steps allowed, and a mobile was standing on it. It reads `SELF-TEST OK` after. The
sweep moved from 1 fragile and 1 contested row to **0 and 0**.

**It leaves nothing behind.** Probes are deleted in a `finally`, they return `true` from
`OnMoveOver` so they cannot manufacture each other's obstructions, they override `CheckIdle` so they
never take a step the walker did not ask for, and they carry `NavWalker.Ledger = false` — which
gates the walk counters, the rung totals, the terminal ledger **and `NavEdgeHealth.Strike`**. A
sweep that struck every edge it found hard would put a fifteen-minute cost multiplier on the
fleet's routing, which is the audit rewriting what it was asked to measure. Verified: two 1450-walk
sweeps left `walksStarted` at 252 and `walksCompleted` at 227, untouched.

### The instrument was wrong three times before it was right

Worth keeping, because every one of the three would have produced a confident, wrong report, and
two of them were the *shard* being right and the *test* being wrong.

1. **A seeded hop into a wall passed, correctly.** `brit-shop-tinker`'s old arrival at
   `1421,1651` is a plaster wall twenty tall. As a `Walk` step it names no real waypoint, so
   `ArrivalRangeFor` returns `DefaultArrivalRange` — **2** — and the probe stopping two tiles from
   the wall *is* an arrival. Walking into a wall with two tiles of latitude is a goal you can meet
   from outside the wall.
2. **The same tile as a range-0 arrival passed too.** `TryShiftWithinArrival` retargeted onto
   `1422,1651`, the standable pathable neighbour, exactly as last session built it to. With the
   ladder and the arrival shift both running, very nearly nothing is impossible — so the seed has
   to be a tile the shift has nowhere to shift *to*.
3. **A 45-second deadline could not reach the top rung.** The ladder is five rungs twenty seconds
   apart, so a walk that is going to give up needs a hundred seconds. Every hard failure came back
   as `timeout` and named no cause at all. `cause` and `endedBy` are now two fields — what was
   wrong with the goal, and how the walk ended — and the deadline is 120 s.

`[WalkAudit selftest` is the standing version: one hop it **must** fail (`2072,2865`, the water
under the Trinsic pier — `CanFit` false, `TryResolveZ` no, all eight neighbour steps refused) and
one it **must** pass, and it prints `SELF-TEST OK` or `SELF-TEST BROKEN` rather than leaving a
reader to invert the verdict themselves. It seeds the *work list*, never `navigation.json`, so
there is no seed to remove and no window in which a road through a wall could be committed.

### The detour factor is the engine's route, not the probe's steps

The number that ends up as re-base evidence is **the engine's planned route length over the
authored straight line**, measured once with `MovementPath` before the probe moves.

The obvious number — steps walked over straight line — is the wrong one, and it took three sweeps
to be sure of it:

| what the probe was | worst "ratio" | what it was actually measuring |
| --- | --- | --- |
| `Home` at `Point3D.Zero` | **82:1** on a ten-tile hop, 822 steps | `WalkRandomInHome` falling through to `WalkRandom` — pure random steps |
| `KeepHomeAligned`, `RangeHome = 0` | **21.7** | the wander became `DoMove(toward Home)`, a *straight line* walking into the wall the walker's `PathFollower` was routing round; the two alternated |
| `CheckIdle => true` | **30.0** | the bot population. The top of the list **reshuffled completely** between two sweeps of an unchanged graph |

A list that reorders itself every run is not evidence of anything about the road. The engine's route
is deterministic and contention-free, and it is the exact question the re-base asks: an edge
authored at six tiles whose real road is thirty-nine walks a bot round three sides of a building
*every time*, whoever else is on the map. **Verified: all 1310 edge rows identical across two
consecutive sweeps.** The only 18 rows that moved were arrivals — which is `BaseDoor.Open` moving
the door item, the same effect that made a doorway's `CanSpawnMobile` answer last only seconds.

`stepRatio` keeps the other number beside it, because the gap between the two *is* the contention.
A pass is also marked **contested** when a rung fired or a rung named somebody on the next-step
tile, and contested rows are listed separately from the detour list for the same reason.

### What it costs

**1450 walks — 1310 edges, 140 arrivals — in about 2.5 to 4 minutes over 12 probes**, against a
live population of sixty bots. `Custom.WalkAuditProbes` (12) and `Custom.WalkAuditWalkSeconds`
(120) are the two dials. The work is wall-clock-bound rather than CPU-bound, so probes scale nearly
linearly until they start meeting each other. Measured runs: 137 s, 147 s, 158 s, 159 s and 252 s —
the spread is the failures, which cost the full 100-second ladder each.

### The sweep on the current graph

**1450 walks in 252 s over 12 probes. 3 failures, all from one waypoint; 2 arrivals skipped for
`pending-road`; 8 contested passes; 8 that passed only after a rung.**

**Every failure is `brit-arm-1`, and `[NavAudit` walks all three of its edges cleanly.** That is
this instrument's first finding of the kind it was built for:

```
brit-arm-1 -> brit-shop-armorer (arrival)  short-of-goal  stop 1447,1648  (goal 1442,1650)  852 steps 100s
brit-arm-1 -> brit-arm-2                   short-of-goal  stop 1445,1646  (goal 1441,1650)  855 steps 103s
brit-arm-1 -> brit-shop-armorer (arrival)  short-of-goal  stop 1446,1646  (goal 1444,1651)  854 steps 101s
```

The probe **never leaves the start**: eight hundred and fifty steps of pacing inside two tiles of
`1447,1647`, then the full ladder and a rescue. It is **directional** — `brit-arm-2 -> brit-arm-1`
passes in 7 tiles — which is the greedy-pathfinder shape §*The pathfinder is greedy* describes, and
`MovementPath` does return a 7-step route from the start tile. `[TileProbe 1447,1647` says the tile
is a wooden floor at z 10, `CanFit` true, with **all eight neighbour steps allowed**. So neither the
tile nor the geometry is at fault and nothing cheaper than a real walker could have found this.

`brit-arm-1` is the armoury's *old* interior position. `brit-shop-armorer` and both its arrivals
moved west to `1443,1650` / `1442,1650` / `1444,1651` in the editor; the waypoint stayed where it
was, and now sits on the far side of the shop from everything it points at. **This is authoring, and
it belongs with the Britain re-base** alongside `brit-inn`'s 13-tile arrival and `brit-mine-west`'s
two at 16 and 19.

**The twenty highest detours** — the engine's own route over the authored straight line, clean
passes only, stable across runs:

| edge | engine | authored | detour |
| --- | --- | --- | --- |
| `brit-carp-3 -> brit-tan-1` | 39 | 6 | **6.50** |
| `brit-bank-1 -> brit-bank` (arrival) | 26 | 4 | **6.50** |
| `brit-prov-4 -> brit-tav-1` | 42 | 7 | 6.00 |
| `brit-tink-1 -> brit-shop-tinker` (arrival) | 40 | 7 | 5.71 |
| `brit-carp-3 -> brit-carp-2` | 39 | 7 | 5.57 |
| `brit-tav-1 -> brit-prov-4` | 35 | 7 | 5.00 |
| `brit-cour-3 -> brit-cour-4` | 49 | 10 | 4.90 |
| `brit-cour-4 -> brit-cour-3` | 47 | 10 | 4.70 |
| `brit-tan-1 -> brit-carp-3` | 28 | 6 | 4.67 |
| `brit-inn-1 -> brit-inn` (arrival) | 41 | 9 | 4.56 |
| `brit-desc-3 -> brit-prov-1` | 47 | 11 | 4.27 |
| `brit-cour-5 -> brit-jew-1` | 47 | 11 | 4.27 |
| `brit-jew-1 -> brit-cour-5` | 45 | 11 | 4.09 |
| `brit-mage-app -> brit-north-b` | 31 | 8 | 3.88 |
| `brit-north-b -> brit-mage-app` | 31 | 8 | 3.88 |
| `brit-carp-2 -> brit-carp-3` | 27 | 7 | 3.86 |
| `brit-cour-1 -> brit-prov-1` | 27 | 7 | 3.86 |
| `brit-mkt-1 -> brit-mkt-2` | 45 | 12 | 3.75 |
| `brit-jew-2 -> brit-jew-3` | 41 | 11 | 3.73 |
| `brit-jew-3 -> brit-jew-2` | 41 | 11 | 3.73 |

**This was the evidence for re-basing Britain, and it was a cluster rather than a list.** Every row
above 3.7 is in the upper town — the carpenter/tanner block, the courier run, the jeweller row, the
provisioner-to-tavern leg. A six-tile edge with a thirty-nine-tile road is a bot walking round three
sides of a building on every trip, on a graph whose hop cap exists to keep hops inside the
pathfinder's box. These were not broken and the audit never flagged them; they were simply authored
across walls rather than along streets.

#### What the rebase did to them, and the measure that nearly lied about it

The table above is the **before**. After the rebase (*The whole-facet rule*, below), the same sweep
grown from 1450 walks to 2509 — **0 failures in both**:

| | before | after |
| --- | --- | --- |
| **edges** at detour ≥ 2.0 | 49 of 1208 — **4.1%** | 33 of 2068 — **1.6%** |
| edge mean ratio | 1.11 | **1.05** |
| **arrivals** at detour ≥ 2.0 | 12 of 88 — 13.6% | 30 of 152 — 19.7% |
| arrival mean ratio | 1.45 | 1.55 |

Every named row in the table above is gone from the edge list. And read naively, the arrival numbers
say the rebase made the last hop worse — **which is the wrong conclusion from the wrong instrument,
and worth keeping written down because it took a second measurement to see.**

**A ratio compares a walk to a straight line, and the rebase moved the straight line.** Our door
waypoints stood on the shops' thresholds. Where a merge removed one, the street waypoint that
replaced it is four tiles from the door as the crow flies and thirty-two by road — so the detour
that used to sit on the *edge* reaching the door now sits on the *arrival* leaving the street, and
the denominator shrank with it. Nothing moved; the accounting did.

**The measure that does not lie is walked tiles for the same journey** — the cheapest walked last
hop per destination. Measured straight after the rebase it read *8 shorter, 5 longer, 35 unchanged,
384 tiles against 383*: the roads much better and the last hop a wash. But three had got materially
worse, all the same fault — `brit-shop-tanner` 1 → 20 tiles, `brit-home-carpenter` 2 → 28,
`brit-shop-butcher` 1 → 10 — because their **door** waypoints had been merged into the street.

**Both halves are fixed.** The three doors are restored, each link pathed by `[NavHop` first — and
`PlanMerges` now refuses to merge any waypoint a destination or arrival names, which is the join
rule applied to the other kind of attachment: *the proposal may replace the roads between our
places, never the record standing at one.* With them back:

> **8 destinations shorter, 2 longer, 38 unchanged — 384 walked tiles against 329.**

`brit-shop-tinker` 37 → 10, `brit-inn` 27 → 9, `brit-forge` 11 → 5, `brit-tavern-south` 5 → 1. The
arrival ratios stay higher than before and that is now the honest reading of them: a road waypoint
on the street *is* further from a door than a door waypoint was, and the walk is shorter anyway.

**Passed only after rungs** — the fragile set, invisible in both lists above because a sidestep
costs one step: `brit-prov-1 -> brit-desc-3`, `uo-wp-194-s1-s1 -> uo-wp-194-s1-s2` and
`uo-britain-south-road-b -> britain-forge` (arrival), each one `Repath 1`. A repath that works is
the ladder doing its job; a repath that recurs on the same edge across sweeps is a road that only
works when nobody else is on it.

**And a sweep is not the same evidence as a live fleet.** The rebase's second `[BotSmoke` boot
logged four terminal walk failures in twelve minutes on a graph the sweep had just walked clean:
`uo-wp-194-s1 -> uo-wp-194` (Trinsic, and already in the fragile set above), `brit-jew-1 ->
uo-britain-gem-shop-s4` (a rebase relink, ratio 4.83 in the sweep), and twice `(start) -> uo-wp-5`,
which was a real defect the sweep structurally could not see — a *destination* with no reachable
waypoint inside the hop cap. A sweep walks edges and arrivals that exist; it never asks whether a
bot standing at a place can route **away** from it, because it starts every walk at a waypoint.
`Nav.Data` did not catch it either: its rule is measured from the arrival, which was exactly at the
cap, while the destination centre was fifteen tiles out. **The check that finds it is a flood from
the home waypoint plus a cap test on every destination and arrival**, and it is worth running after
any adoption:

> 1000 of 1000 waypoints reachable; 1 record with no reachable waypoint inside the cap
> (`trinsic-shop-tailor-2`, 14 tiles - pre-dates the rebase).

### The approach-tile scan - the third instrument, and the blind spot both others share

**Every measurement above starts ON an authored waypoint, and a bot does not.** `NavAudit.cs:106`
has said so in its own header for as long as it has existed - *"a walker starts the next hop from
wherever it stopped, which is anywhere within `ArrivalRangeFor` (2 tiles by default) of the
waypoint - a 5x5 box the audit never validated"* - and `[WalkAudit` inherited it, because
`BuildWorkList` seeds each walk at the authored tile. Commit `118be12f` wrote down that a sweep
*"structurally could not see"* a bot's failure to route **away** from where it stood.

`NavNeighbourhood.Scan` is the check for it. A **cliff** is:

> the waypoint can path to a graph neighbour, and a tile inside the waypoint's own arrival
> tolerance **that the waypoint can path to** cannot path to that same neighbour.

All three clauses are load-bearing. Without the first, every `BLOCKED` edge would be re-reported
twenty-four times over; without the last this is just `[NavAudit` again. The middle one —
reachability — was added after the first pass at fixing the findings, and it is the subject of the
next section.

#### A tile a walker cannot get to is not a tile a walker can stop on

**The scan's first clause was `CanFit`, and `CanFit` answers the wrong question.** It says a mobile
*fits* on a tile. It says nothing about whether one can ever *arrive* there — and the check exists
to ask where a walker might legitimately have stopped.

Measured, on the 22 pairs the first run reported. Of their **44 stranded tiles, 34 could not be
pathed to from their own waypoint** — and, tested separately as the falsification, **not from any
graph neighbour of it either: 0 of 34 reachable from anywhere on the graph.** `[TileProbe` says what
they are, and they are all the same thing:

```
== 1469,1548,32 ==                        (2 tiles from brit-north-4, which is on cobblestones)
LAND 0x0408 'wooden floor' z 30 ...
STAND hint z 32: TryResolveZ yes (32); CanFit True, CanSpawnMobile True
```

A **building interior behind a wall**, or a ledge across a fence: standable, sealed, and two tiles
from a waypoint standing in the street outside. A walker planning a hop through that waypoint can no
more stop inside the shop than it can walk through the wall. Ten of the fourteen sample tiles were
`wooden floor` under a `stone roof` or `wooden shingles`; the rest were forest, dirt and grass on the
far side of something.

So the box is filtered by reachability before anything is asked about leaving it. On the current
graph that drops **77 tiles of 22,474 — 0.3%** — and with them **17 of the 22 cliff pairs**. It is a
narrow filter, not a blanket one, and it is the reason the remaining five are worth acting on.

**The count it drops is published, not discarded.** `cliffTilesTested` and `cliffTilesUnreachable`
ride in both snapshots beside `cliffsChecked`, and the summary line reads *"22,349 reachable approach
tile(s) (72 standable but sealed off, not tested)"*. A check that quietly stops looking is the
failure this tree works hardest to avoid; a reader has to be able to see how much of the box was set
aside and why.

It also costs nothing. The reachability path runs **before** the per-neighbour paths, so a sealed
tile costs one `MovementPath` instead of one per neighbour: 54,706 paths in 7.6s became 76,866 in
8.0s.

**The probe that established this over-reported twice, both times the same way.** `nav-hop` answers
with `MovementPath(probe, goal)` and has no special case for an adjacent goal, where `MovementPath`
returns no path at all — so probing *waypoint to a tile one away*, or *a box tile to a neighbour one
away*, reads `ok:false` for a step that is simply a step. `NavNeighbourhood.Paths` has had the
`Chebyshev <= 1` short-circuit since it was written, and `[NavAudit` skips adjacent edges for the
same reason. Filter those out and the two instruments agreed on all 22 pairs exactly, which is what
made the 34 trustworthy.

**Two things it is easy to build smaller, and both would have found nothing.**

- **The box is the arrival tolerance, not the eight neighbours.** Of the 44 stranded tiles across
  the 22 cliff pairs the first run reported, **none is adjacent to its waypoint** - every one is at
  Chebyshev 2, and so is every one of the ten that survived the reachability filter. A radius-1
  check would have reported a clean graph. (That is also *why* `arrivalRange: 1` closes the five
  real ones: the box shrinks past the only ring that had anything in it.)
- **The goal is the next waypoint, not this one.** Measured at the case that named this:
  `2034,2832` and `2035,2832` both path back to `uo-wp-194-s1` *and* to its other two neighbours,
  and fail only toward `uo-wp-194`. "Can a displaced bot get back on its waypoint" would have
  passed them.

The sample tile is the **nearest** stranded one, not the first found. The scan walks its box from
the corner, so a first-found sample reports distance 2 whatever the truth is - a number that looks
like a finding and is an artefact of a loop order. `strandedAdjacent` counts the radius-1 ones
beside it, so the question above stays answerable from the output rather than by argument.

**Cost, and where it runs.** 1001 waypoints, 22,349 reachable approach tiles (72 sealed), **76,866
engine paths in 8.0 s** - engine-only, no probe mobile, no walker, no world mutation. That is sixteen
times the edge sweep, so it is **not** on the default `[NavAudit` path: the editor re-runs that
quietly after every nav save. It runs from **`[NavAudit full`** and from **`[WalkAudit`**, which pays
8 seconds on top of a 195-second sweep. `cliffsChecked`, `cliffTilesTested` and
`cliffTilesUnreachable` are published beside the array in both snapshots, so a reader can tell
"none" from "not looked at", and "clean" from "filtered".

### What the five real cliffs turned out to be, and the fix

After the reachability filter, **five pairs on four waypoints** survived: `uo-rec-1-s2` toward both
`uo-rec-1` and `uo-brit-north-d-s1`, `uo-rec-4-2-s2` toward `uo-rec-4-2`, `uo-wp-194-s3` toward
`uo-wp-204`, and `uo-wp-41-s1` toward `uo-wp-42`. Ten stranded tiles between them, every one
reachable, every one at Chebyshev 2.

**The hop cap does not explain them, and that was the hypothesis going in.** `uo-wp-194-s1` (commit
`239ff6e2`) was a cap case — an edge authored at exactly 12, plus the 2-tile displacement, planning
a 14-tile hop — and the obvious reading was that these were more of the same. Measured, **only 3 of
the 10 tiles put the goal over the cap**; the other 7 were comfortably inside it, and `uo-wp-41-s1`'s
edge is 9 tiles with both its stranded tiles at 8 and 9. A subdivision would have changed nothing.

`[TileProbe` says what they are, and all four are one shape:

| waypoint | stands on | its stranded tiles stand on |
| --- | --- | --- |
| `uo-rec-1-s2` | `cobblestones` z30 | `wooden floor` z30 under `stone roof` |
| `uo-rec-4-2-s2` | `dirt` z20 | `wooden floor` z20 under `wooden shingles` |
| `uo-wp-194-s3` | `grass` z0 | `wooden floor` z10 on `wooden boards`, under `stone roof` |
| `uo-wp-41-s1` | `cobblestones` z0 | `wooden floor` z5 on `wooden boards`, under `slate roof` |

**The waypoint is in the street and its 5x5 box reaches inside the building next to it.** Unlike the
34 sealed tiles above, these interiors have a door, so a walker that legitimately stops two tiles
short can end up on the shop floor or the porch — and from there the pathfinder will not route back
out toward the next road node.

So the fix is `arrivalRange: 1` on those four waypoints, and it is the mechanism used as designed
rather than a check narrowed to pass. `NavWalker.ArrivalRangeFor` carries uo-offline's own reason for
the field in its comment — *"a doorway waypoint at an unusual Z needs the mobile on the exact tile,
so its authored tolerance wins"* — and these are exactly doorway-adjacent nodes. It does not hide the
stranding; it stops the walker considering itself arrived while it is still on the porch. Verified
before authoring: every standable tile of all four 3x3 rings, toward every graph neighbour, 48 probes,
**0 stranded**. `strandedAdjacent` was 0 for all five pairs, which said the same thing from the
other side.

The cost is that a walker takes one more step at four waypoints.

### The evidence for a pathfinder evaluation at a higher cap

Kept deliberately, because the fixes above erase most of the traces and this is the one question the
findings were expected to answer.

**The premise this session started with did not survive measurement, and that is the first thing
worth recording.** The plan read the 22 pairs as "18 of them sit on edges 9-12 tiles long, so
`length + ArrivalRangeFor` crosses the cap, so subdivide them like `uo-wp-194-s1`". Seventeen of the
22 turned out to be sealed tiles no walker can reach, and of the ten stranded tiles that were real,
**seven put their goal comfortably inside the cap**. Authored edge length correlated with the
findings and did not cause them.

What is genuinely evidence, and what a higher-cap evaluation should start from:

- **566 of 1153 walk edges are authored at exactly 12**, the cap itself. That is not a coincidence:
  `NavAdopt.Subdivide` cut at the cap for as long as it existed, so an adopted road is a chain of
  hops each exactly as long as the pathfinder is allowed. Raising the cap without re-subdividing
  leaves those 566 edges where they are; raising it *and* re-adopting lengthens them all.
- **An edge at the cap is a `cap + ArrivalRangeFor` hop for the bot that has to plan it.** That is
  the `uo-wp-194-s1` mechanism (`239ff6e2`) and it is why `Subdivide` now cuts at
  `cap - ArrivalRangeFor` instead. Whatever the cap becomes, that subtraction has to survive.
- **The three stranded tiles that were over the cap**, as the worked cases:

  | waypoint | goal | authored edge | stranded tile | tile-to-goal |
  | --- | --- | --- | --- | --- |
  | `uo-rec-1-s2` | `uo-rec-1` | 12 | `1465,1566,30` | **14** |
  | `uo-rec-1-s2` | `uo-rec-1` | 12 | `1466,1566,30` | **13** |
  | `uo-wp-194-s3` | `uo-wp-204` | 11 | `2038,2808,10` | **13** |

- **`Custom.NavHopMaxTiles` is 12 because `FastAStarAlgorithm`'s search box is 38x38 centred on the
  *midpoint***, so a detour round a building leaves the box. An evaluation at a higher cap is really
  an evaluation of that box, and `MovementPath.OverrideAlgorithm` is the seam for replacing it - see
  the note further down, including the blast radius.

### What uo-offline has, and why this is not a port

They have no whole-graph walk audit. `[auditedges` (`CustomBots/AuditEdgesCommand.cs`) is a
flood-fill geometry check — our `[NavAudit`, arrived at independently and with the same two
verdicts, `BLOCKED` and `FAR`. `[testroute` (`Nav/HpaCommands.cs:58`) plans an abstract route and
walks nothing. The only thing in that tree that moves a real mobile to answer a navigation question
is **`[testapproach`** (`Nav/FieldCommands.cs:88`): one nearby `PlayerBot`, one destination, the
final approach only, and its own header calls it *"Diagnostic only"*.

Their fleet-wide instrument is **passive** — `BotNavWatch` plus `StuckTelemetry`, aggregating what
the live population happened to do into `Data/Live/stuck_report.json` as
`{asOf, bootedAt, windowMinutes, window, total, hotspots, edges}` (`BotStuckTelemetry.cs:200-245`).
`walk-audit.json` mirrors that header and adds the per-walk rows their shape has no place for,
because **theirs reports a window and this reports a sweep**. A window tells you which roads the
fleet happened to try; a sweep tells you about the ones nobody has walked yet.

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

### The whole-facet rule, and the mode that applies it

> **Wherever uo-offline has roads, theirs replace ours. Wherever they have none, ours stay
> authored.**

This is the rule for the facet, not a Britain note, and **rebase** is the mode that applies it:
`[NavAdopt <region> rebase`, or `rebase` in the `nav-adopt` token's body.

The skip above is right while their roads and ours are in *different places* - it stops an adopt
laying a second road network over a town somebody drew by hand. Britain was the case where they are
in the **same** place and ours was the worse of the two: hand-authored, partial, and carrying every
high-detour row the walk audit found. Refusing to propose there meant the town could never be
improved by their data at all.

**So in rebase mode the authored skip stops applying to waypoints and edges - and only to those.**
Destinations, arrivals, sites, zones and routes keep it in full, because those are the records that
carry authored work: names, tags, `exclusive` and `exact` flags, positions somebody placed by eye.
Only the road is rebased. Britain went from 608 waypoints and 655 edges to 995 and 1147 with all 53
destinations, all 115 arrivals (tiles *and* flags), all 8 zones and all 4 routes intact.

**What keeps it one road network is the merge, not the skip.** Each of our waypoints inside the
region is measured to the nearest point on a **proposed edge** - not to the nearest proposed
waypoint - and one inside `Custom.NavAdoptMergeRadius` (6, half the hop cap) is removed and folded
into the proposed record, with every destination, arrival and route that named it re-pointed.

**The segment-versus-node distinction is the whole calibration.** uo-offline authored against a
38-tile leg, so their nodes stand about sixteen tiles apart and ours stand *between* them on the
same street. Britain measured both ways: **44 of our 93 waypoints are within six tiles of one of
their nodes, and 73 are within six tiles of one of their roads.** Merging on node distance would
have left thirty of ours sitting on a road the same proposal was laying - the second road network,
arrived at by the other door.

Four things a rebase has to do that an ordinary adopt never meets, each found by running it:

- **A removal relinks.** Our edges naming a removed waypoint come back from the validator as
  *warnings* - the shard drops them and reloads clean - so a removal on its own silently cuts its
  neighbours loose. `brit-carp-4` was cut loose in the first trial, and it is the only approach
  waypoint `brit-shop-carpenter` has. Every surviving neighbour now gets a walked edge onto the
  waypoint the removal folded into, and a removal whose relink will not walk is **withdrawn**
  rather than allowed to strand anybody.
- **The dangling edge records go too.** Listed as `removedEdges` in their stored order, so Save
  deletes them rather than leaving records that reload clean and get written back by the next
  golden export.
- **A join target is never merged.** A join subdivides the walk onto one of *our* waypoints, so the
  subdivisions on that edge exist because ours does; merging ours into one of them makes a
  self-edge, which is dropped, which strands the subdivision. `town-7` merged into `uo-town-7-s1`,
  one hop along `town-7`'s own join, and `brit-shop-tinker` came back naming a record the save
  never wrote.
- **A route's steps are a path, not a set.** Re-pointing each step alone gave `brit-courier-loop`
  legs of 13, 14, 16 and 14 tiles on a route whose every leg had been under twelve. A stretched leg
  is now re-filled by breadth-first search over the graph the save is about to make.

**A rebase proposal is accepted through the editor's own Save, headlessly or by hand.**
`tools/editor/accept-adopt.js` posts to `/api/save/navigation` against a `baseHash`, so it inherits
the same `survivors()` rule, the same `unproject`, the same replicated validator on a mandatory dry
run, the same `.bak` and atomic rename and reload token. Adopt still cannot write nav data; the
proposal simply gained `removals`, `rewrites`, `relinks`, `removedEdges` and `withdrawn`.

### A join is picked by walked road, not by straight line

**The merge rule learned this and the join rule did not.** `NavAdopt.cs:1038` records it for the
rebase merge in so many words — *"the waypoint that replaces it is four tiles away in a straight
line and thirty-two by road, because the road goes round the building"* — and all four join sites
kept picking their target with `NavGraph.Nearest`, which is pure Chebyshev: Z ignored, walls
ignored. The evidence was sitting in the walk audit's own detour table: **eighteen of the twenty
highest-detour edges on the graph were joins**, worst of them `brit-tan-1 ↔
uo-road-to-britain-forge` at four tiles apart and thirty-three by road — a bot walking round three
sides of a building on every trip.

**Which end moves was settled by measurement, not preference.** A join is a pairing and either end
could be re-picked:

| | alternatives across the 38 joins |
| --- | --- |
| re-pick **our** end (keep their waypoint) | **17 joins have no other waypoint of ours within the hop cap at all**, and the two worst can only swap with each other — `brit-tan-1` and `brit-carp-2` are two doors on the same block, both thirty-odd tiles by road from the same street |
| re-pick **their** end (keep ours) | **160**, and `brit-tan-1` alone has four |

So our end is fixed, which is also the half that must not move: our records carry names, tags and
positions somebody placed by eye.

**`nav-rejoin` applies it to the graph we already have** — `NavRejoin.cs`, a token taking a region.
It ranks every adopted waypoint within the hop cap, takes a **strictly** shorter road only,
engine-verifies the winner both ways with `TryVerifyHopsBothWays`, and writes a proposal to the
**same** `Data/Live/nav-adopt.json` that `tools/editor/accept-adopt.js` already accepts — same
`survivors` rule, same replicated validator on a mandatory dry run, same `baseHash`, same `.bak`.
It proposes **edges only**: no waypoint is created, moved, renamed or deleted, and `removals` and
`rewrites` are structurally empty. `NavRejoin.NearestByRoad` is the rule itself, and `NavAdopt`'s
own join sites now call it, so a future adopt does not repeat the mistake.

#### The ruler was wrong first, and the aggregate hid it

**The first version ranked by `NavCorridor.TryPath`, a breadth-first flood — and was graded on
`MovementPath`.** They are not the same function: the flood is exhaustive and symmetric, the engine
is greedy best-first with a 300-node budget and therefore **directional**. Ranking optimised one
function and the walk audit reported the other, and in aggregate the flood looked like a fair proxy
— 608 flood-tiles became 391 — **so nothing in the summary said anything was wrong.**

Ranking by the engine's own route, both directions summed, is both more honest and about six times
faster (0.4 s against 2.5 s for the same region), because a `MovementPath` costs roughly 0.15 ms
and a flood roughly 22 ms. Both directions because a join is walked both ways and a one-way bargain
is not one.

#### And the ranker still does not predict the sweep, which is worth knowing rather than hiding

`NavRejoin` scores `brit-tan-1 -> uo-rec-2-1` at **46 tiles both ways**. The walk audit then walks
the same pair and reports **24 one way and 46 the other — 70**. Both are `MovementPath`; the
difference is that the ranker probes with a **`Point3D`** and the sweep probes with the **mobile**,
which is the first row of *What a clean audit does not tell you* met from a new direction.
`FastAStarAlgorithm:92` sets `MoveImpl.AlwaysIgnoreDoors` and `IgnoreMovableImpassables` only when
the caller is a `BaseCreature` — but that would make the Point3D answer *longer*, not shorter, so
**it does not explain this and the cause is not yet established.** It is written down rather than
guessed at.

What that costs is honest to state: **`brit-tan-1`'s own join, the edge this whole rule was named
after, is marginally worse by the sweep's measure — 33/33 = 66 before, 24/46 = 70 after** — while
the graph as a whole is much better. The rule change is carried by the aggregate, not by its
poster child, and the next person to improve this should make the ranker probe with a mobile.

#### What it did to the graph

Britain (`1385,1495 360x300`): **30 joins, 135 candidates, 126 pairs routed in 0.4 s — 20 improved,
1286 engine tiles both ways becoming 794, 0 failures.** Waypoints, destinations, arrivals, zones
and routes are byte-identical before and after; 20 edges were replaced by 20.

Measured by a full `[WalkAudit`, 2508 walks before and 2510 after, **0 failures in both**:

| | before | after |
| --- | --- | --- |
| **edges** at detour ≥ 2.0 | 33 of 2304 — **1.43%** | 11 of 2306 — **0.48%** |
| edge mean ratio | 1.045 | **1.021** |
| worst edge detour | 8.25 | **5.75** |
| **arrivals** at detour ≥ 2.0 | 34 of 204 | 34 of 204 — unchanged, as it must be |
| arrival mean ratio | 1.374 | 1.374 |

The arrivals not moving at all is the check that this did what it said: only edges were touched.

**One row reads worse by ratio while its walk gets shorter** — `brit-bake-1` 66 → 47 tiles at a
detour of 5.50 → 6.71, because its straight line shrank from 12 to 7. That is *What the rebase did
to them* met from the other side, and it is why the report prints walked tiles and the ratio side
by side rather than the ratio alone.

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

The whole file, Trammel: **996 waypoints, 1148 walk edges, 65 destinations, 8 zones, 4 routes.**
`[NavAudit` reports **0 blocked, 0 over-cap, 0 unstandable arrivals and 0 stale Z** — every edge has
been pathed in both directions against real map data.

**Britain's roads are uo-offline's; its places are ours.** The town was hand-authored until the
rebase (see *The whole-facet rule* above) replaced 78 of our road waypoints with theirs and left
every destination, arrival, zone and route exactly as authored. **50 waypoints of ours survive in
Britain, and which 50 is the rule stated as data**: the shop doors and approaches their roads do not
front (`brit-smith-1`, `brit-inn-1`, `brit-tav-1`/`-2`, `brit-mage-1`, `brit-bake-1`, `brit-bow-1`,
`brit-jew-1`/`-2`, `brit-prov-1`, `brit-tail-1`, `brit-plaza-4`/`-7`, `brit-north-4`/`-b`/`-d`),
the north-mine corridor, the west-mine road `wp-1`…`wp-23`, and the Trinsic slopes.

Landmarks came from `Spawns/trammel.xml` vendor spawn points, `Data/Regions.xml` town bounds, and
the ModernUO shard's `britain-daily-life.json` (which independently confirms six shop points and
supplies their Z). The roads *between* them started as interpolation and were corrected against the
map — the audit rejected 20 of the first 78 edges. Two things that came out of that are worth
keeping even though the roads themselves have since been replaced, because guessing got both wrong
and the same guesses are available to make again:

- **The descent from the upper town runs east**, not down the middle of the block — the authored
  chain was `brit-plaza-1` → `brit-desc-1` (1472,1639) → `brit-desc-2` (1467,1651), and uo-offline's
  road, arrived at independently, comes down the same side.
- **The northern district is at Z 30, not Z 20**, and `1471,1555` is a dead-end pocket: the road
  to the smithy goes `brit-north-c` → `brit-north-d` (1477,1556) → `brit-north-4`.

Known gaps, in the order worth fixing:

1. **The moongate (1336, 1997, 5) is not in the data.** It is ~90 tiles south of the town and
   every waypoint on the road there would be invented. Walk it with `[NavRecord` and it becomes
   real data in one pass.
2. **Only the west gate is seeded.** The north Chaos guard posts (1521/1525, 1457) and the
   northern approach are outside the seeded box.
3. ~~**`[NavAudit` is a full re-path every time, and gets slower with the graph.**~~ **The "132
   edges take about 25 seconds" in this entry was wrong by more than an order of magnitude, and it
   was wrong because nothing printed the number.** Measured on the current graph: **1153 edges in
   0.52 s warm, 1.7 s cold** — about 3,460 `MovementPath` calls at roughly 0.15 ms each. The
   summary line now carries its own wall time, so the next reader measures rather than guesses.

   **The incremental mode this entry asked for was then declined, on the strength of that
   number.** A per-edge fingerprint saves about half a second and buys the one failure this
   codebase works hardest to avoid: a stale fingerprint silently skips a changed edge, and a check
   that quietly does not run is worse than one that is slow. The `full` flag shipped anyway,
   because the expensive pass is the approach-tile scan (8.6 s) and gating THAT off the editor's
   post-save run is what was actually needed. If an incremental mode is ever wanted, it belongs on
   the cliff scan, where the time is.
4. **Arrival points are audited less strictly than edges** — `[NavAudit` checks edges, and the
   arrival picker validates a scattered tile with `CanSpawnMobile` at pick time, but an arrival
   point sitting in a wall will simply always scatter. `[NavDebug` is how you spot those.

## Reference

`nav-format-comparison.md`, alongside this file, maps the schema against `uo-offline-server`'s, field by field,
with a reason for every divergence — what was adopted from them, what was deliberately not, and
what neither side has.
