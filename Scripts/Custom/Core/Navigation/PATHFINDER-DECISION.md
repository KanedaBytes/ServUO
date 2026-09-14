# Pathfinder decision memo

**Measured 12 September 2026**, on the graph at `7c3db49d` — 1,003 waypoints, 1,155 walk edges,
0 gate edges, 65 destinations, 136 arrival points, Trammel. Every figure below came off this shard
in this session; nothing is carried over from an older window.

**Nothing changed default.** `Custom.NavPathfinder=Off`, `Custom.NavPathfinderBudget=300`,
`Custom.LoopCostSampler=False`, `MovementPath.OverrideAlgorithm` null, no upstream file edited, the
graph untouched and the hop cap still 12.

**The short version.** The stock pathfinder costs the fleet **3.9% detour on edges and nothing at
all in failures** — 2,514 walks, zero failed, one fragile. The two mechanisms this session set out
to price turned out to be worth **one arrival point between them**. What the measurements actually
found is that the remaining cost is in **authoring**, not in the search: four arrivals have no
engine route, three of them because no route exists rather than because the search gave up, and one
of those is a waypoint standing on a closed door. **Recommendation: keep stock (option a), spend the
next session on the four arrivals and on uo-offline's distance-field final approach (option d).**

---

## 1. What the stock pathfinder costs the fleet

`[WalkAudit` at the shipping default, twice, both probe classes on one boot. The two sweeps are
**row-identical on every deterministic column** (`pass`, `tiles`, `pathTiles`, `ratio`) for both
classes — 0 of 2,514 rows moved — so everything below is a fact about the roads rather than about
the afternoon.

| | `BaseCreature` | **`PlayerBot` — the fleet's own** |
| --- | --- | --- |
| walked / failed / fragile | 2,514 / 0 / 0 | **2,514 / 0 / 1** |
| edge detour, mean | 1.0179 | **1.0388** |
| edge p95 / p99 / max | 1.00 / 1.60 / 3.88 | 1.20 / 1.75 / **4.14** |
| edges ≥ 1.2 | 48 — 2.08% | **104 — 4.50%** |
| edges ≥ 2.0 | 9 — 0.39% | **13 — 0.56%** |
| edges with no engine route | 0 | **0** |
| arrival detour, mean | 1.3740 | 1.5039 |
| arrivals with no route (real) | 3 | **4** |

**The fleet pays 3.9% mean detour on edges and loses no route at all.** That is under the 5% bar the
session was told to check against, and it is the first half of the gate met.

### The 12-tile edges are the cleanest on the graph, and the cap hypothesis is dead

566 authored edges sit at exactly `Custom.NavHopMaxTiles`, 1,132 rows walked both ways. The roadmap
has carried a hypothesis that authoring at the cap is what costs the fleet. **It is false**, on the
fleet's own class:

| authored length | rows | mean ratio | > 1.2 | > 1.5 | no route |
| --- | --- | --- | --- | --- | --- |
| 1 | 120 | 1.000 | 0 | 0 | 0 |
| 5 | 98 | 1.063 | 6 | 4 | 0 |
| 7 | 110 | 1.093 | 6 | 4 | 0 |
| 8 | 114 | **1.114** | 14 | 8 | 0 |
| 10 | 132 | 1.083 | 11 | 8 | 0 |
| 11 | 92 | 1.080 | 9 | 6 | 0 |
| **12** | **1,132** | **1.022** | 24 | 6 | **0** |

Every length from 5 to 11 tiles is **worse** than 12. The detours are short urban edges threaded
between buildings, not long ones; the worst on the graph is `uo-virtue-path-1-s1 -> brit-bake-1`, 7
tiles authored and 29 walked. The README's *evidence for a pathfinder evaluation at a higher cap*
already conceded that "authored edge length correlated with the findings and did not cause them";
this closes it, and that section now points here rather than restating the hypothesis.

Climb is not the discriminator either: |dz| 5-9 runs at mean 1.009 against 1.041 for the flat
bucket, and |dz| 20-24 at 1.009.

### The arrivals, taken apart

Arrival rows are the stochastic ones, so the 1.50 mean is not one number. Decomposed across the two
sweeps:

| | rows |
| --- | --- |
| `tiles: 0` — start and goal within one tile, `MovementPath` returns before searching. **Not failures** | 17 |
| unstable between sweeps (the door-item effect the README records) | **0** |
| no engine route at all | 4 |
| stable, with a real route — **the only arrivals this memo quotes** | 183 |

Of the 183: mean **1.676**, 37 at ratio ≥ 2.0. Splitting those 37 by absolute route length separates
the two things a ratio confuses:

- **6 are a small denominator, not a long road** — `brit-bow-1 -> brit-shop-bowyer` is 2 tiles
  straight and 6 walked. Reported as walked tiles, they are trivial.
- **33 are real: a street waypoint and a shop door on opposite sides of a block.**
  `uo-road-to-britain-forge -> brit-shop-tanner` is 4 tiles as the crow flies and **36 by road**;
  `uo-britain-provisioner-se-alley -> brit-tavern-blue-boar` is 6 and 39.

**And a ratio cannot tell you whether the pathfinder was at fault.** It compares a road to a
straight line, not a road to the best road. The only way to separate "the search is bad" from "the
road really is that long" is to run a better search on the same tiles — which this session did, at
five node budgets (§3), and the answer is that the routes do not get shorter. **These 33 are
geometry and authoring, not the pathfinder.**

Rescue and retarget cannot inflate the `ratio` column at all: it is measured once from
`MovementPath` before the probe moves. What they inflate is `stepRatio`, which is why the two are
reported side by side and the gap is named as contention.

### Where routes actually fail

Four arrivals, on the bot class:

| from → to | tiles | dz | why |
| --- | --- | --- | --- |
| `uo-brit-west-rd-1-s2 -> brit-shop-provisioner-south` | 10 | −10 | **budget exhausted at 301.** A route exists and needs 324 expansions |
| `uo-central-britain-road-2 -> brit-inn-central` | 9 | +3 | no route. Open list drained at 658 expansions with a budget of 8,000 |
| `uo-trinsic-forge-s1 -> trinsic-forge` | **2** | 0 | no route. Drained at 236 |
| `uo-wp-221 -> trinsic-forge` | 22 | 0 | no route. Drained at 859 |

Three of the four are **not the pathfinder giving up — they are places a bot cannot reach.** A
two-tile arrival with no route is a data defect. All four still show `pass: true` in the audit,
because `PathFollower.Follow` falls back to stepping blindly at the goal when there is no path
(`PathFollower.cs:134-145`), and the walker shoves its way round. That fallback is why a fleet can
run for months with unreachable arrival points and nothing in the ledger to show for it.

---

## 2. What a route costs today

Measured with a pass-through meter over `FastAStarAlgorithm.Instance` — behaviour-identical to
stock by construction — and with the whole-loop sampler on `Core.Slice`.

| | live fleet, 60 bots, 667 s | under a 12-probe walk audit, 221 s |
| --- | --- | --- |
| searches | 2,318 — **3.48 per second** | 72,231 — **327 per second** |
| mean / worst | **1.207 ms** / 48.3 ms | 0.161 ms / 14.3 ms |
| no route | 180 — **7.8%** | 114 — 0.16% |
| total pathfinding | 2.80 s — **0.42% of one thread** | 11.6 s — **5.3% of one thread** |
| loop period, p50 / p95 / p99 / max | 15.59 / 17.52 / 21.83 / 185 ms | 15.62 / 23.08 / 47.42 / **8,532 ms** |

Three things worth saying plainly:

- **Pathfinding is not what the game thread spends its time on.** At the shipping population it is
  under half a percent of one core. Even with twelve audit probes hammering it, 5%.
- **Failed searches dominate the live cost.** 7.8% of live searches return nothing, and a failed
  search pays the whole 300-expansion budget — about 5.9 ms against 1.2 ms mean. Roughly a third of
  all pathfinding time on a live shard is spent proving there is no route.
- **The idle loop period is 15.6 ms because that is the Windows timer granularity**, not because a
  cycle costs 15.6 ms of work. The number to read is the tail. `NavPlayerActor.MoveTowards` takes
  the direct step first and only builds a `PathFollower` when it is refused (`NavActor.cs:588-648`),
  which is why 60 bots generate three and a half searches a second rather than sixty.

**The 8.5-second maximum is `[WalkAudit`'s own approach-tile cliff scan** (`cliffSeconds` 8.49,
22,386 tiles), which runs synchronously on the game thread. `LoopCost` found it on its first outing.
It is an admin command, so it is not a live-shard hazard — but it is the longest freeze this shard
has outside a world save, and it is now measured rather than assumed.

---

## 3. The two candidate fixes, priced

Both were measured with a copy of `FastAStarAlgorithm` in `Custom/` — necessary because every
scratch array on the real one is `private static` and `MaxDepth` is a `private const`, so there is
no seam to instrument through. **The copy is proved to be the algorithm**, not assumed: over all
2,310 authored edges walked both ways it returns **byte-identical direction arrays** to stock, and a
full bot walk audit with the copy installed is **row-identical to the baseline on all 2,514 rows**,
with the creature class **also row-identical** because the copy runs for bot subjects only.

### (b2) The dead-end `break` at `FastAStarAlgorithm.cs:118` — worth **nothing** here

`if (count == 0) break;` abandons the whole search the first time any expanded cell has no legal
successor, discarding a live open list. ModernUO's otherwise line-for-line descendant of the same
RunUO file `continue`s instead (`BitmapAStarAlgorithm.cs:244-247`). It is a real defect and it was
the session's leading hypothesis.

**It never fires on this shard.** Measured across 2,310 authored edges, 586 synthetic hops at 9-24
tiles, and 36 known-failing hops replayed at five budgets — **zero dead-end outcomes, and zero
routes recovered**. A full bot walk audit with `continue` in place differs from the baseline on
**0 of 2,514 rows** and from the control on 0.

The reason is visible in the outcome split of the 32 synthetic failures: **18 budget-exhausted, 14
open-list-drained, 0 dead-end.** A heuristic this inflated drives the frontier into open ground, and
open ground has successors.

*Not recommended.* One upstream token for a measured zero.

### (b) Raising the node budget — worth **one arrival point**

`MaxDepth = 300`. ModernUO's identical algorithm, with the same heuristic and the same 38-tile box,
runs **1000** and makes it a settable property with a config key (`BitmapAStarAlgorithm.cs:69`,
`:98-106`). Replaying the 36 failing hops:

| budget | rescued | failures still budget-exhausted | mean ms on a failed search |
| --- | --- | --- | --- |
| 300 | — | 21 | 5.89 |
| 600 | **3** | 8 | 7.96 |
| 1,000 | 3 | 0 | 11.38 |
| 3,000 | 3 | 0 | 9.53 |
| 8,000 | 3 | 0 | 9.05 |

**Everything the budget can buy is bought by 600, and it is three hops.** On the whole graph the
effect is exactly one row: a bot walk audit at budget 600 differs from the baseline on **1 of 2,514
rows** — `uo-brit-west-rd-1-s2 -> brit-shop-provisioner-south` gains a 22-tile route where it had
none. Nothing else changes and nothing gets worse.

The cost is paid on failures, which are 7.8% of live searches: a failed search goes from 5.9 ms to
8.0 ms at 600 and 11.4 ms at 1,000. At the live rate that is about +0.05% of one thread at 600.

*Not recommended on its own.* `MaxDepth` is a `private const` on a class whose every scratch array is
`private static`, so shipping this is a **third upstream edit** — and the same arrival can be fixed
by moving a waypoint, for nothing. If it is ever taken, **600 is the number, not 1,000**: above 600
it buys no route and costs 40% more per failure.

### (c) A replacement pathfinder — not built, and the case for it is weaker than expected

The session was authorised to stop before building one, and the measurements say that was right.
What a proper search would fix is detour, and the fleet's detour is 3.9%. What it would not fix is
the four arrivals: three of them have no route at any budget, and an optimal search cannot find a
road that is not there.

Priced anyway, from the expansion data: an admissible Chebyshev heuristic under unit step cost, a
binary-heap open list in place of `FindBest`'s linear scan (`:298-320`), one `byte[]` of node states
in place of two `BitArray`s, and proper relaxation. Roughly 250 lines behind
`MovementPath.OverrideAlgorithm`, one session to build and one to measure. The upside is bounded
above by the numbers in §1: at most a few percent of edge detour and none of the failures.

*Not recommended now.* Revisit if the graph grows into terrain the current search handles worse —
the synthetic set says that begins somewhere past 16 tiles (§5).

### (d) A `DestinationFieldCache`-style final approach — **the one worth doing**

uo-offline's answer to exactly the problem this memo found. Per destination, one bounded Dijkstra
flood to Chebyshev radius 60 (`DestinationFieldCache.cs:31`), held as two sparse
`Dictionary<long,int>` maps of cost and resolved Z (`DistanceField.cs:16-17`), built in
`Initialize()` and cached to disk behind a fingerprint over the destination list. At runtime it is
**not a search at all**: `DistanceField.TryStep` (`:38-55`) is eight dictionary lookups and a
descent to the cheapest neighbour, globally optimal inside coverage, replacing the drift on the
final approach only.

It is **bot-layer code, not engine code** — `Map`, `Point3D`, `Mobile.Move`, `map.CanFit`,
`map.GetAverageZ`, `BinaryWriter` — so it ports to net48 with no ModernUO substrate behind it. That
is the opposite of their `StepCache`, which is ~2,500 lines over `XxHash3`, libdeflate,
`CollectionsMarshal`, `TileMatrix.MapFilesFingerprint` and a field bolted onto `BaseMulti`, none of
which exist here.

Cost, scaled from their figures: they flood **488** destinations in 43-60 s for a **53 MB** cache;
we have **65**, so roughly **6-10 s at boot and ~7 MB on disk**, behind the same disk cache.

Why it is the right target: **arrivals are where our measured cost is** — mean 1.68 against 1.04 on
edges, 20% of them at ratio ≥ 2.0, and 33 rows where a street waypoint and a shop door are 30 to 49
tiles apart by road. A distance field walks those optimally by construction and does it with no
search on the game thread at all. One session to port, one to measure.

---

## 4. Could the hop cap move to 16 or 24?

The synthetic set: real waypoint pairs at each length, classified flat / climbing (|dz| ≥ 5) /
door-crossing, planned with the bot class. Success rate:

| | 9t | 12t | 16t | 20t | 24t |
| --- | --- | --- | --- | --- | --- |
| flat | 38/40 | 39/40 | **40/40** | 39/40 | 39/40 |
| climbing | 40/40 | 40/40 | 39/40 | **32/40** | 35/40 |
| door | 26/26 | 40/40 | 35/40 | 37/40 | 35/40 |
| **all** | **98.0%** | **99.2%** | **95.0%** | **90.0%** | **90.8%** |

**Flat ground is fine to 24 tiles. Climbing is what breaks**, and it breaks between 16 and 20 —
80% at 20 tiles. Door-crossing degrades from 12 onward.

So the cap could move **for flat road**, and the honest form of that is not a global cap at all: the
constraint is terrain-dependent and a single number cannot express it. What would have to be true
first, if it were attempted anyway:

1. `NavAdopt.Subdivide` keeps cutting at `cap - ArrivalRangeFor`, not at the cap.
2. `NavWalker.MaxApproachDistance` (8) is re-derived — it is sized to the 300-expansion budget, and
   at a longer cap the budget binds before the box does.
3. The 566 edges already at 12 are re-subdivided or left alone deliberately; raising the cap and
   re-adopting lengthens all of them.
4. The climbing figure above is repeated after any change, because it is the one that fails.

**uo-offline's 38 is not evidence for ours.** `WaypointGraph.MaxLegDistance = 38` is defined *as*
`BitmapAStarAlgorithm.AreaSize` (`WaypointGraph.cs:38-41`) and works because they have a 1000-node
budget over a cached terrain query. The box has never been our binding constraint: not one search in
this whole session returned `OutOfBox`.

---

## 5. The route planner, separately — REVIEW.md:91 answered

This is `NavGraph`'s A* over the waypoint graph, not the tile pathfinder. `Nav.Planner` compares it
against a plain Dijkstra on hand-built graphs.

**The mechanism REVIEW.md names is real and reproducible.** With two 0.5 tags stacked on one edge
over 200 tiles, A* returns **100.00** by road where the true optimum is **53.75** through the
discounted leg — 86% worse. The heuristic scales Chebyshev by the cheapest *single* tag while an
edge multiplies *all* of its, and the overshoot grows with the remaining distance: the same shape at
20 tiles agrees, at 200 it does not.

**It cannot fire on the current data.** Five cost tags, of which exactly **one discounts** (`road`,
0.9); exactly one edge carries two tags at all — `trinsic-alchemist-slope-1 ->
trinsic-alchemist-slope-2`, `road stairs`, 0.9 × 1.3 = **1.17**, a net penalty. No edge's product
falls below the cheapest single multiplier, so the heuristic is a valid lower bound.

Both gate cases agree, including one where the gate hangs two hops in rather than off the start
node. That is weaker evidence than the discount result — there are no gate edges to test against and
gate routing is not active — so it is reported as "no disagreement found", not as "gates are safe".

**Nothing is fixed here.** What changed is that the condition is now checked rather than reasoned
about: `Nav.Planner` re-inspects the live graph every sixty seconds and warns the moment a second
discounting tag is authored or an edge's product drops below the cheapest single one. The README's
*Current contract* line — that both cases "must be settled before" gate routing — is settled for
discounts and open for gates.

---

## 6. Recommendation

**Keep stock, and spend the next session on the data rather than the search.** The fleet pays 3.9%
detour on edges and fails nothing; the two engine-level fixes this session priced are worth one
arrival point between them, and both cost an upstream edit in a tree with two. What the numbers
actually point at is four arrival points and thirty-three shop doors: three of the four arrivals
have no route at any budget — one of them a two-tile hop, and `brit-tanner-shopfront` is a waypoint
standing on a closed door — and the 33 worst arrivals are a street waypoint and a shop entrance on
opposite sides of a block, which no search can shorten. Fix those in the editor, where they cost
nothing, and then port uo-offline's distance-field final approach (option d), which is bot-layer
code that ports cleanly to net48, costs perhaps 6-10 s at boot and eight dictionary lookups per bot
per tick, and aims precisely at the column where our cost actually is. If a budget change is ever
wanted alongside, it is **600 and not 1,000** — and it should be taken with the arrival it rescues
already fixed in the data, so that it buys something new rather than the same thing twice.

---

## 7. Findings this session turned up that are not about the pathfinder

Three, all pre-existing, none introduced here.

> **The first two were fixed the next day, 14 September 2026. Both entries below are now false and
> are kept for the record.** `94c60973` moved `brit-tanner-shopfront` from the door tile at
> 1439,1612 to **1438,1611** and re-exported the goldens in the same commit; both files under
> `Data/Custom/golden/` are byte-identical to their shipped counterparts today (same git blob), and
> `node --test tools/editor/*.test.js` runs **380 tests, 380 pass, 0 fail**.
>
> The third still stands. And the four arrivals §1 named have since been taken apart against the
> running engine — see **[`ARRIVAL-RELOCATIONS.md`](ARRIVAL-RELOCATIONS.md)**, which also corrects
> §1's account of them in one respect: `uo-trinsic-forge-s1 -> trinsic-forge` and
> `uo-wp-221 -> trinsic-forge` are **one record between them**, the arrival at 1884,2644 walled in
> by the forge, the anvil and a sandstone wall. A fourth finding belongs beside these three:
> `nav-hop`'s probe is a `BaseCreature` (`NavCorridor.cs:259`), so it reports the provisioner edge
> **OK** while the same hop fails for a bot — 297 expansions against 301, on identical tiles at the
> same budget.

- **`brit-tanner-shopfront` (1439,1612,20) is a closed door tile.** `[TileProbe` reads
  `CanFit False`, `CanSpawnMobile False`, and all eight steps onto it refused for a creature. So
  `[NavAudit full` reports `brit-tanner-street -> brit-tanner-shopfront` **blocked** — one blocked
  edge where the gate is zero. The bot fleet does not notice, because MODIFICATIONS entry 6 lets a
  bot's route plan through a closed door and `PlayerBot.Move` opens it; the daily-life walkers and
  the audit's own point probe do notice. Introduced by `7c3db49d` (the editor relocations). **Sean's
  to move**, one tile off the threshold.
- **`Data/Custom/golden/` is stale since `7c3db49d`**, which changed `navigation.json` without
  re-exporting. `node --test tools/editor/*.test.js` runs **380 tests, 379 pass, 1 fails** —
  *"navigation: the shipped file is canonical"*. Deliberately **not** regenerated here: the golden
  should be re-exported *after* the door waypoint moves, in one editor pass, not before.
- **`[WalkAudit`'s approach-tile cliff scan freezes the game thread for 8.5 seconds** (22,386 tiles,
  76,984 engine paths). Admin-only, so not a live hazard, but it is the shard's longest non-save
  stall and `LoopCost` now reports it.

---

## 8. What was built, and what to delete when this is decided

Everything is off by default and no upstream file was edited.

| | |
| --- | --- |
| `NavPathfinder.cs` | the copy of `FastAStarAlgorithm` with a dead-end switch and a budget knob, four modes behind `Custom.NavPathfinder`, and `Nav.Pathfinder` — which asserts the override is null when the flag is off, reinstalls and counts if `[Path` clears it, and never reports Ok while a runtime override is in force |
| `NavHopProbe.cs` | `[NavHopProbe` / `nav-hop-probe` — the synthetic set, the three-way plan, and the copy-validation that makes every expansion figure here quotable |
| `NavPlannerCheck.cs` | `[NavPlannerCheck` / `nav-planner` and `Nav.Planner` — §5 |
| `LoopCost.cs` | the whole-loop sampler on `Core.Slice`, reported through `Core.Process`. A standing instrument: REVIEW.md:114 asks for whole-loop p95/p99/max and this is the only thing in the tree that has it |

**If the decision is "keep stock", `NavPathfinder.cs` and `NavHopProbe.cs` should go**, along with
`NavActorCheck`'s ledger entry for the copied `BaseCreature` test — that entry says so at the site.
`LoopCost.cs` and `NavPlannerCheck.cs` stay either way; they are not about this decision.
