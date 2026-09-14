# Arrival relocations — Sean's editor list, 14 September 2026

`PATHFINDER-DECISION.md` §1 named four arrival edges with no engine route and recommended spending
a session on the data rather than the search. This is that session's answer: **what is wrong with
each, why, and the exact edit to make** — verified against the running engine before it was written
down.

**Every proposal below was probed on a live shard.** Each goal tile has a quoted `[TileProbe`
reading, and **every affected hop was planned in both directions** with `[NavHopProbe … bot` at the
stock 300-node budget. That second half is the rule the tanner waypoint bought: `7c3db49d` put
`brit-tanner-shopfront` on a closed door because the tiles were probed and the edges were not.

**Seven records change, in three places.** Nothing is added and nothing is deleted:

| place | records |
| --- | --- |
| `trinsic-forge` | arrivals `:2337` (moved + repointed), `:2339`, `:2340` (repointed), destination `:2213` |
| `brit-inn-central` | arrival `:2360` (moved + repointed), destination `:2228` |
| `brit-shop-provisioner-south` | arrival `:2370` (moved) |

---

## Read this before you verify anything yourself

**`nav-hop` cannot answer a question about a bot.** Its probe is
`CorridorProbe : BaseCreature` (`NavCorridor.cs:259`), and on the provisioner edge below that is
the difference between a pass and a fail:

| same tiles, same stock budget of 300 | expansions | result |
| --- | --- | --- |
| `[NavHopProbe 1424,1739,20 1414,1748,10 budget 300 creature` | 297 | **success** |
| `[NavHopProbe 1424,1739,20 1414,1748,10 budget 300 bot` | 301 | **failure** |

Four nodes apart on identical geometry — and the reason is that **the probe is given a permission
the fleet does not have.** `FastAStarAlgorithm`'s two branches are not symmetric:

```csharp
if (bc != null)                                          // FastAStarAlgorithm.cs:97-101
{
    MoveImpl.AlwaysIgnoreDoors       = bc.CanOpenDoors;
    MoveImpl.IgnoreMovableImpassables = bc.CanMoveOverObstacles;
}
else if (botDoors.HasValue)                              // MODIFICATIONS entry 6, :102-107
{
    // IgnoreMovableImpassables is deliberately not granted; see BotPathPolicy.
    MoveImpl.AlwaysIgnoreDoors = botDoors.Value;
}
```

Both ignore doors. Only the creature ignores **movable impassables** — and `CorridorProbe`
overrides `CanOpenDoors => true` (`NavCorridor.cs:281`) while inheriting
`CanMoveOverObstacles => Core.AOS || Body.IsMonster` (`BaseCreature.cs:1926`), which on this shard
is **true**, because `Config/Expansion.cfg` is `EJ`. So `nav-hop`'s probe walks straight through the
crates, barrels and furniture a bot has to go round.

That makes it **strictly more permissive than the fleet**, and systematically so *inside shops* —
which is exactly where the arrivals in this list are. `nav-hop` reports this edge **OK**, and has
been reporting it OK all along.

A full `[WalkAudit` on 14 September shows the same split in its own rows — 5,028 walks, 12 probes,
both classes over the identical graph:

| no-route arrival rows (`kind:"arrival"`, `tiles > 1`, `pathTiles: 0`) | |
| --- | --- |
| `BaseCreature` | **3** |
| `PlayerBot` | **4** |

The extra one is the provisioner. That is `PATHFINDER-DECISION.md` §1's *"arrivals with no route
(real) — 3 / 4"* row, and this is what is behind it.

**Use `[NavHopProbe … bot` to verify an arrival the fleet walks.** `nav-hop` remains right for the
daily-life walkers, which really are `BaseCreature`s.

---

## Why none of this ever showed in the ledger

All four still report `pass: true`. `PathFollower.Follow` falls back to stepping blindly at the
goal when there is no path (`PathFollower.cs:134-145`) and the walker shoves its way round, so the
fleet arrives and nothing is counted. `arrivals`.`failed` reads **0** as well, for the same reason.
The field that tells the truth is **`pathTiles`** on each row of `Data/Live/walk-audit.json` — see
step 6.

---

# The list

## 1. `trinsic-forge` — move the arrival out of the pocket

**One record is the whole of items 3 and 4 in the memo.** Both failing pairs point at the same
arrival.

One detail worth knowing before you judge how urgent this is: on the `uo-trinsic-forge-s1` pair the
walk audit records **`steps: 0`** and stops where it started, at 1884,2646. The walker never tries,
because `range: 2` already covers 1884,2644 from the waypoint tile — so it counts as arrived
without moving. The `uo-wp-221` pair is the one that really walks: 18 steps, ending at 1886,2648,
four tiles off the goal. The reason to fix the record anyway is the **stand-tile** picker, which
ranks tiles inside the arrivals' ranges for a crafting station (`BotWorkSites.cs:1042`) — an
unreachable tile in that set is a candidate a smith can be sent to and cannot occupy.

`Data/Custom/navigation.json:2337`

```json
{"destination":"trinsic-forge","x":1884,"y":2644,"z":0,"exclusive":false,"exact":true,"range":2,"waypoints":"uo-wp-221","source":"uo-offline"}
```

**Why there is no route.** The tile itself is fine — `CanFit True` — but it is walled in on three
sides by the smithy's own fixtures:

| neighbour | what is on it | `CanFit` |
| --- | --- | --- |
| 1884,2645 (N) | `0x0FB1 'forge'`, **Impassable** | **False** |
| 1885,2644 (W) | `0x0FB0 'anvil'`, **Impassable** | **False** |
| 1883,2644 (E) | `0x0166` / `0x015F` sandstone wall | **False** |
| 1883,2645 (NE) | sandstone wall | **False** |

`[TileProbe 1884 2644 0` reads `STAND … CanFit True, CanSpawnMobile False` and refuses **four of
eight** steps — every diagonal. The north-west diagonal from 1885,2645 is refused because both of
its orthogonals are the forge and the anvil. The only approach left is from the south, and
`uo-trinsic-forge-s1` is two tiles **north** of it, on the other side of the forge.

Stock drains the open list rather than giving up: **236 expansions with the budget raised to
8,000** from two tiles away, and **859** from `uo-wp-221`. There is no route at any budget.

**The edit — change `x`,`y` to 1886,2645:**

```json
{"destination":"trinsic-forge","x":1886,"y":2645,"z":0,"exclusive":false,"exact":true,"range":2,"waypoints":"uo-wp-221-s1-s1","source":"uo-offline"}
```

*(the `waypoints` change is item 2 below — make both in one edit)*

**Verified.** `[TileProbe 1886 2645 0`:

```
LAND 0x0445 'stone' z 0 flags [none]; GetAverageZ low 0 avg 0 top 0
STAND hint z 0: TryResolveZ yes (0), ResolveZ 0; CanFit True, CanSpawnMobile True
GOAL 1886,2645,0: CanFit True
```

`[NavHopProbe … bot budget 300`, both directions:

| hop | expansions | result |
| --- | --- | --- |
| `uo-trinsic-forge-s1` 1884,2646 → 1886,2645 | 2 | **ok** |
| 1886,2645 → `uo-trinsic-forge-s1` 1884,2646 | 2 | **ok** |

**It is still a smith's tile.** The forge is 2 away and the anvil 1, both inside
`BotWorkSites.CraftingReach = 2` (`BotWorkSites.cs:1005`), so `InReach(…, AnvilAndForge)`
(`BotWorkSites.cs:1127-1136`) still holds and a Blacksmith can work from it. Keep `"exact": true`:
these four arrivals are the forge apron and scatter must not move them.

---

## 2. `trinsic-forge` — repoint the three arrivals that ride on `uo-wp-221`

`uo-wp-221` (`navigation.json:218`, 1896,2666) is **20–22 tiles** from the four forge arrivals
(22, 20, 21, 20 by Chebyshev), against `Custom.NavHopMaxTiles = 12` (`Config/Custom.cfg:37`). Three
arrivals name it anyway.

`uo-wp-221-s1-s1` (`navigation.json:535`, 1890,2653,z10) is **7–9 tiles** from the same tiles and is
already the last hop before the forge in the graph:

```
uo-wp-220-s1 → uo-wp-221 → uo-wp-221-s1 → uo-wp-221-s1-s1 → uo-trinsic-forge-s1
```
*(`navigation.json:1555-1556`)*

**The edit — in all three records, `"waypoints":"uo-wp-221"` becomes
`"waypoints":"uo-wp-221-s1-s1"`:**

| line | arrival |
| --- | --- |
| `:2337` | the one you just moved to 1886,2645 |
| `:2339` | 1885,2645 |
| `:2340` | 1885,2646 |

**And the destination, `navigation.json:2213`** — `"waypoints":"uo-trinsic-forge-s1 uo-wp-221"`
becomes `"waypoints":"uo-trinsic-forge-s1 uo-wp-221-s1-s1"`.

**Verified**, `[NavHopProbe … bot budget 300`, every pair both ways:

| hop | expansions | result |
| --- | --- | --- |
| 1890,2653 ↔ 1884,2646 | 7 | **ok** |
| 1890,2653 ↔ 1885,2645 | 8 | **ok** |
| 1890,2653 ↔ 1885,2646 | 8 | **ok** |
| 1890,2653 ↔ 1886,2645 | 8 | **ok** |

---

## 3. `brit-inn-central` — the arrival is on the wrong side of the building

`Data/Custom/navigation.json:2360` — and it is the destination's **only** arrival, so there is no
second point to fall back on.

```json
{"destination":"brit-inn-central","x":1497,"y":1612,"z":20,"exclusive":false,"range":2,"waypoints":"uo-central-britain-road-2","source":"uo-offline"}
```

**Why there is no route.** The tile is fine — `[TileProbe 1497 1612 20` gives
`CanFit True, CanSpawnMobile True` and allows all eight steps. The building is the problem. Probed
tile by tile over x 1487–1502 × y 1598–1620, the inn's ground floor runs **x 1492–1498 at z20/z21
from y 1605 down to y 1612**, narrowing to the x 1496–1498 column below that. It is sealed by wall
at **x 1491 and x 1499** for its whole height. To the north, the only standable tiles in y 1601–1604
are an interior stair at x 1496–1498 (z27 / z22 / z20) and **y 1601 is solid across its top**, so
that is a dead end rather than a way in. **The only ground-level doors are on the south face** —
`0x06A5` and `0x06A7` at 1495,1620 and 1496,1620, plus `0x06AD` at 1499,1618. The door above
1497,1612 is at **z 40**: that is the first floor, not a way in.

`uo-central-britain-road-2` (`:561`, 1489,1603,z17) is outside to the **north-west**. Stock drains
at **658 expansions with a budget of 8,000** — no route at any budget. The destination's own centre
tile is sealed from there too: 1489,1603 → 1496,1610 is **no route**.

**This is the cluster in your 12 September console.** Seven `shifted its arrival` lines and two
whole-ladder failures inside 1495–1498 / 1610–1614 — see *"What the 1495-1498 / 1610-1614 cluster
was"* at the end of this file. It is this record, and only this record.

**The edit — two fields on `:2360`:**

```json
{"destination":"brit-inn-central","x":1497,"y":1618,"z":20,"exclusive":false,"range":2,"waypoints":"uo-britain-inn-east-road-s1","source":"uo-offline"}
```

**And the destination, `navigation.json:2228`** — `"waypoints":"uo-central-britain-road-2"` becomes
`"waypoints":"uo-britain-inn-east-road-s1"`.

`uo-britain-inn-east-road-s1` is `navigation.json:755`, 1501,1626,z10, and is in the graph at
`:1638-1639` (`uo-britain-inn-east-road → … → uo-britain-inn-south-stairs`). 1497,1618 is **inside
the inn**, by the south entrance, 8 tiles from that waypoint — under the 12-tile cap.

**Verified.** `[TileProbe 1497 1618 20`:

```
STAND hint z 20: TryResolveZ yes (20), ResolveZ 20; CanFit True, CanSpawnMobile True
GOAL 1497,1618,20: CanFit True
```

`[NavHopProbe … bot budget 300`:

| hop | expansions | result |
| --- | --- | --- |
| 1501,1626 → 1497,1618 | 40 | **ok** |
| 1497,1618 → 1501,1626 | 40 | **ok** |

**Why not keep the arrival at 1497,1612 and just repoint it?** Because both southern approaches are
over the cap: `uo-britain-inn-south-stairs` (1496,1631) is 19 tiles from it and
`uo-britain-inn-east-road-s1` is 14 — even though both genuinely route to it (checked, ok both
ways). Moving the arrival to 1497,1618 is what brings it under 12.

---

## 4. `brit-shop-provisioner-south` — the arrival is at the far end of the shop

`Data/Custom/navigation.json:2370`

```json
{"destination":"brit-shop-provisioner-south","x":1414,"y":1748,"z":10,"exclusive":false,"range":2,"waypoints":"uo-brit-west-rd-1-s2","source":"uo-offline"}
```

**This is the one the memo called "budget exhausted, a route exists".** It does exist — and it is
also the one that fails for a bot and passes for a creature (see the box at the top).

**Why it is 301 expansions.** The shop's interior runs x 1413–1422 at z10 and its **only door is on
the east wall at 1423,1748** (`0x06AD 'wooden door'`, z10). The approach waypoint
`uo-brit-west-rd-1-s2` (`:807`, 1424,1739,z20) is outside and **north of a wall at 1424,1742**, so
the walk is a detour east, south, and back in through the door — while the arrival sits at the far
**west** end of the shop, nine tiles past the door. Greedy best-first spends its budget pressing
west against the shop's south wall.

`[TileProbe 1414 1748 10` on the current tile: `CanFit True, CanSpawnMobile False`, all eight steps
allowed — a fine tile in a bad place.

**The edit — change `x`,`y` to 1422,1748.** Nothing else; the waypoint stays as it is.

```json
{"destination":"brit-shop-provisioner-south","x":1422,"y":1748,"z":10,"exclusive":false,"range":2,"waypoints":"uo-brit-west-rd-1-s2","source":"uo-offline"}
```

**Verified.** `[TileProbe 1422 1748 10`:

```
STAND hint z 10: TryResolveZ yes (10), ResolveZ 10; CanFit True, CanSpawnMobile True
GOAL 1422,1748,10: CanFit True
```

`[NavHopProbe … bot budget 300`:

| hop | expansions | result |
| --- | --- | --- |
| `uo-brit-west-rd-1-s2` 1424,1739 → 1422,1748 | **27** | **ok** |
| 1422,1748 → `uo-brit-west-rd-1-s2` 1424,1739 | **27** | **ok** |

against 301-and-fail today. 1422,1748 is inside the shop, one tile west of the door — which is
where a shopper who has just walked in is standing anyway.

**If you would rather the bot went deep into the shop**, the alternative is a new waypoint on the
road outside the door at **1424,1748** (probed: `CanFit True, CanSpawnMobile True`), with the
arrival left at 1414,1748 — that hop costs **10** expansions both ways. It is the better route but
it is more work: a waypoint to place and two edges to author and verify. The single-field change
above is the recommendation.

---

## 5. Re-export the goldens

`[NavExportGolden`, then commit `Data/Custom/navigation.json` **and** both files under
`Data/Custom/golden/`. `tools/editor/project.test.js`'s *"the shipped file is canonical"* compares
them byte for byte, so a nav edit without a re-export turns the suite red — which is exactly what
`7c3db49d` did and `94c60973` repaired.

Check the line endings survived: `head -c 4 Data/Custom/navigation.json | xxd -p` must read
**`7b0a`**, not `7b0d0a`.

## 6. Confirm it took

In this order:

1. `[NavReload`
2. `[NavAudit full` — expect `0 blocked, 0 over cap`, `0 unstandable arrival(s), 0 stale Z,
   0 struck edge(s)`, `0 cliff pair(s)`. Occupied edges are warning-only; a bot standing on a
   waypoint is not a finding.
3. `[WalkAudit` — then count the no-route rows in `Data/Live/walk-audit.json`:

   ```
   rows where kind == "arrival" and probeClass == "bot" and tiles > 1 and pathTiles == 0
   ```

   **Not `arrivals`.`failed`, and not `pass`** — both read 0 today with all four broken.
   `pathTiles` is the one that tells the truth: `NavWalkAudit.cs:1315-1330` sets it to 0 when
   `MovementPath` finds nothing, and it is measured *before* the probe moves, so the blind-step
   rescue cannot launder it. It should fall from **4** to **0**.
4. `node --test tools/editor/*.test.js` — 380
5. `[CoreSmoke` — `Nav.Data` should not have gained a warning

---

## What the 1495-1498 / 1610-1614 cluster was

Your 12 September console showed **seven `shifted its arrival` lines and two whole-ladder walk
failures**, all inside 1495–1498 / 1610–1614, two minutes after *"Shops closing"*.

**All of it is `brit-inn-central`, and nothing in that box is unexplained.**

- **It is the only nav record in the box.** A sweep of `navigation.json` over 1493–1500 × 1608–1616
  returns the destination `brit-inn-central` (1496,1610) and its single arrival (1497,1612), and
  nothing else — no waypoint, no other destination, no other arrival.
- **Daily life is not a candidate.** `britain-daily-life.json` holds no coordinate in the box, and
  its tavern is `brit-tavern-blue-boar` at 1498,**1691**, with arrivals at y 1680–1694 — eighty
  tiles south. *"Shops closing"* is the day phase that sends bots to a social destination, which is
  why the inn lit up when it did; the actors themselves were never there.
- **The coordinate spread is the scatter, not several records.** The arrival is
  `"exclusive": false`, so `Custom.NavArrivalScatter = 2` (`Config/Custom.cfg:71`) offsets each
  bot's target by up to two tiles. One record at 1497,1612 produces targets across
  1495–1499 × 1610–1614 — the box you saw. Seven lines are seven bots, not seven records.
- **Reproduced live on 14 September**, in a fifteen-minute fleet run on the unchanged data:

  ```
  18:04:13 [INFO ] [Nav] Kayleigh shifted its arrival from 1495,1610 to 1495,1612
                         - the engine will not path to it from here.
  ```

  `from 1495,1610` is a scattered target, and the reason is the third fault in
  `NavWalker.ArrivalTileFault` (`NavWalker.cs:1270-1273`) — the one raised at `Log.Info`, which is
  why these are the lines that reached your console rather than staying at Debug. It is asked only
  from inside `MaxApproachDistance`, so it fires on the approach rather than at plan time.

The two whole-ladder failures are the other end of the same thing: when
`TryPickArrivalTile` finds nothing it can both stand on and path to, there is no shift to make and
the walker climbs the ladder to a teleport instead. Item 3 removes the cause of both.

---

## What was checked and left alone

- **`brit-tanner-shopfront` is already fixed.** `94c60973` moved it from the door tile at
  1439,1612 to 1438,1611 and re-exported the goldens; both golden files are byte-identical to their
  shipped counterparts today (same git blob). `PATHFINDER-DECISION.md` §7 still says otherwise and
  has been corrected.
- **The pathfinder budget stays at 300.** `PATHFINDER-DECISION.md` §6 asks that any budget change
  be taken *after* the data is fixed so that it buys something new; with item 4 applied, the one
  arrival a raise to 600 would have rescued is rescued already. `FastAStarAlgorithm.cs:26` is a
  `const` in an upstream file, so raising it for real would cost a third `.cs` edit in a tree with
  two — and `Custom.NavPathfinder` (`Config/Custom.cfg:145`, default `Off`) is an instrument, not a
  production path.
