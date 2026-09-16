# The population scale ramp, 15 September 2026

The measurement `bots.json` has been asking for since the port. `population.target` shipped at 60,
chosen as *"a fraction of upstream's 1600, and measure"*, and the measuring never happened; the
README's `### Later` has carried the procedure since 12 September, deliberately unrun.

**The headline is not a performance number.** Nothing broke. Not one of the six stop rules fired at
any count up to 2000 bots, and at 2000 the shard was still spending 0.9% of its bot-tick budget and
collecting gen2 exactly six times in ten minutes — the same six as at 60. What the ramp found
instead is that **the fleet stops getting livelier at about 400 bots, and past that gets deader**,
because concurrent journeys are capped by an admission rate that has nothing to do with the
population target.

---

## Conditions

Every window below was taken under identical conditions. Where a row is not comparable to an
earlier measurement, it says so rather than being quietly placed beside it.

| | |
| --- | --- |
| Hardware | AMD Ryzen 7 5800X, 8 cores / 16 threads, 64 GB |
| Build | ServUO 57.4.0.0, `net48` x64, Release |
| World | 262,011 items, 47,063 mobiles (at the 2000 window) |
| Graph | **4,155 waypoints**, 4,480 walk + 36 gate edges, 160 destinations, 298 arrivals, eight town tags, nine moongates |
| `Custom.MeasurementProfile` | **False** throughout — it thirds visit windows, and crowding is part of what is measured |
| `population.curve` | **FLAT (24 x 1.0) for this session only**, so every window measures the same fleet regardless of the clock. Reverted at wind-down |
| Client | **None connected** at any point — which is what makes the sector figures attributable to the fleet alone |
| `destinations.towns` | `["britain", "trinsic"]`, untouched — home towns stayed Britain and Trinsic as instructed |
| Procedure per count | stop (save, then shutdown), retarget `bots.json`, boot, `botpop-gen` -> `gg-reimport` -> `botpop-audit`, settle 15 min to a stable live count, then one 10-minute window |
| Instruments | `LoopCost` (whole loop), `NavPathfinder` in `Meter` mode, `NavWalkFailures`, `BotPaceSnapshot`, `ProcessHealth`, and `WorldCensus` — the last built this session, commit `9eade4b4` |

`Bots.Cadence` read **one timer at 0.99x** in all seven windows. That matters: F1 was two tickers
running at double speed, and every per-pass figure here would have been against half the wall clock
it names if it had come back.

### Why the curve had to be flattened

`BotSession.TargetNow` is `Target x CurveAt(DateTime.Now.Hour)` on the real wall clock, and surplus
lifecycle bots are logged out to meet it. At 60 the 21 fixed-role bots absorb the difference; at 400
and above a mid-afternoon window (curve 0.65) would log out one lifecycle bot in three, and every
count from 200 up would have "spawned short of its configured number" — tripping a stop rule on the
clock rather than on capacity. A flat curve changes the clock and nothing else.

---

## The table

`live` is the **sampled** count at window close, never the configured one. Loop figures are
milliseconds of cycle **period**. `tick` is `BotTickManager`'s stopwatch — the brains only, not the
walker. `sect` is active sectors against `nom`, the nominal 25 per Player-flagged bot.

| cfg | live | p50 | p95 | p99 | max | cps | tick~ | tickMax | searches | pf~ | pfMax | gen2 | MB | thr | save | /100 | walks | sect | nom | awake | pace/ |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 60 | 62 | 15.8 | 47.2 | 63.1 | 350 | 63.7 | 1.3 | 49 | 2,489 | 1.87 | 63 | 6 | 405 | 17 | 0.31 | 0.44 | 683 | 767 | 1,550 | 504 | 1.03 |
| 100 | 97 | 15.6 | 18.7 | 22.8 | 177 | 64.3 | 2.3 | 44 | 4,300 | 0.67 | 17 | 6 | 412 | 18 | 0.30 | 0.19 | 1,029 | 1,295 | 2,425 | 749 | 0.99 |
| 200 | 198 | 15.5 | 21.5 | 28.3 | 193 | 63.8 | 3.7 | 45 | 10,119 | 0.78 | 16 | 6 | 429 | 18 | 0.31 | 0.26 | 2,346 | 2,001 | 4,950 | 1,028 | 1.00 |
| **400** | **385** | 15.2 | 26.5 | 33.5 | 174 | 66.7 | 7.2 | 46 | 21,830 | 0.79 | 79 | 6 | 477 | 19 | 0.35 | 0.30 | 4,941 | 2,640 | 9,625 | 1,324 | 1.01 |
| 800 | 784 | 15.1 | 28.6 | 37.7 | 219 | 62.8 | 14.1 | 69 | 26,419 | 0.88 | 77 | 6 | 450 | 20 | 0.37 | 0.35 | 5,762 | 2,737 | 19,600 | 1,466 | 1.00 |
| 1600 | 1,575 | 15.2 | 30.6 | 40.3 | 243 | 64.4 | 18.8 | 78 | 25,103 | 0.99 | 79 | 6 | 500 | 19 | 0.39 | 0.40 | 6,005 | 2,630 | 39,375 | 1,402 | 1.03 |
| 2000 | 1,979 | 15.5 | 30.0 | 39.3 | 239 | 67.8 | 18.2 | 52 | 17,162 | 0.89 | 72 | 6 | 532 | 21 | 0.38 | 0.42 | 3,788 | 2,368 | 49,475 | 1,221 | 1.04 |

Windows ran 16:55 to 20:26 local on 15 September 2026, in ramp order, each ~604 s of samples. The
local hour is recorded for completeness only; the flat curve makes it inert.

### Beside the old baselines, which are not comparable

The README's figures were taken on a **1,031-waypoint Britain-and-Trinsic graph**. The graph is now
four times larger and roughly a third of every destination roll crosses a moongate, so bots travel
much further for the same behaviour. Quoted for contrast, not comparison:

| | 12 Sep, 1,031-wp graph, 60 bots | this session, 4,155-wp graph, 60 bots |
| --- | --- | --- |
| loop p50 / p95 / p99 / max | 15.59 / 17.52 / 21.83 / 185 ms | 15.8 / 47.2 / 63.1 / 350 ms |
| behaviour tick | 0.7 ms mean / 15 ms max | 1.3 ms mean / 49 ms max |
| world save | 0.26 s | 0.31 s |
| managed / working set | 385 / 529 MB | 405 / 551 MB |

---

## What actually stops scaling

The resource columns say the shard is idle at every count. The fleet's own liveliness says
something else entirely:

| cfg | live | **travelling** | lingering | idle | share moving | walks per bot |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 60 | 62 | 17 | 2 | 43 | 27% | 11.0 |
| 100 | 97 | 34 | 5 | 58 | 35% | 10.6 |
| 200 | 198 | 67 | 12 | 119 | 34% | 11.8 |
| **400** | **385** | **141** | 23 | 221 | **37%** | **12.8** |
| 800 | 784 | 146 | 24 | 614 | 19% | 7.4 |
| 1600 | 1,575 | 129 | 34 | 1,412 | 8% | 3.8 |
| 2000 | 1,979 | **83** | 9 | **1,887** | **4%** | 1.9 |

Concurrent travellers double cleanly — 17, 34, 67, 141 — and then stop. Doubling 400 to 800 bought
**five** more travelling bots and 393 more idle ones. At 2000, 95% of the fleet is standing still
and there are **half as many bots moving as at 400**.

### The mechanism, named from the code

`BotTickManager.TryTakePlan()` is called from exactly two places — `TravelerBehavior.cs:321` and
`GathererBehavior.cs:542`. `Custom.BotPlansPerTick` is **8**, reset once per pass, and
`Custom.BotTickSeconds` is **2**, so the shard admits **four Traveler route plans per second**,
whatever the population. `NavWalker` imposes no concurrency cap of its own.

By Little's law, four admissions a second against a steady state of ~140 concurrent travellers
implies a mean journey of about 35 seconds, which is the right order for a continent crossing
through a moongate. The cap therefore binds somewhere between 200 bots (67 travelling, under it) and
400 (141, at it) — exactly where the table turns over.

Total `walks` keeps climbing past that point while `travelling` does not, because **Shopper, Crafter
and BankSitter never take the gate** — their short intra-town hops are admitted freely. So the
number that saturates is long-distance travel specifically, which is the behaviour the eight-town
graph and the moongates were built for.

This is an arithmetic fit and a call-site count, not a proof. Confirming it means instrumenting the
refusals, which is deliberately not done here.

### The sector question, answered

The instrument built for this session (`WorldCensus`, commit `9eade4b4`) settles it: **sector
activation is not the scaling term.** Each Player-flagged bot nominally wakes a 5x5 block of sectors
(`SectorActiveRange = 2`, `SectorSize = 16` — an 80x80 tile footprint), and `BaseAI.cs:87` gates
every creature's AI on its sector being active, so the fleet holds ordinary monsters awake.

Measured, that term saturates almost immediately:

| cfg | nominal sectors | active | overlap | creatures awake | of 17,600 sensitive |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 60 | 1,550 | 767 | 51% | 504 | 2.9% |
| 400 | 9,625 | 2,640 | 73% | 1,324 | 7.5% |
| 800 | 19,600 | 2,737 | 86% | 1,466 | 8.3% |
| 2000 | 49,475 | 2,368 | **95%** | 1,221 | 6.9% |

Active sectors **peak at 800 bots and then fall**. The fleet packs into Britain and Trinsic, so each
additional bot increasingly wakes ground another bot is already standing on. At 2000 bots, 95% of
the nominal footprint is overlap and fewer than 7% of the facet's range-sensitive creatures are
awake. Sectors are a fixed cost of having a fleet at all, not a cost of having a big one.

---

## The stop rules

**None fired.** Recorded as run:

| rule | threshold | worst observed | where |
| --- | --- | --- | --- |
| whole-loop p99 | > 189.3 ms (3x the 60-bot baseline) | 63.1 ms | at 60 |
| whole-loop max | > 1000 ms | 350 ms | at 60 |
| pace | slower than the 60-bot pace | ratio 0.99-1.04 throughout | — |
| world save | > 5 s | 0.39 s | at 1600 |
| ledger | > 1 per 100 walks | 0.44 | at 60 |
| liveness / short spawn | unresponsive, or short of configured | 98-99% of configured every time | — |

Two of these need honest qualification, because read naively they would each have produced a wrong
answer.

**The p99 gate was anchored on the wrong end of the ramp.** `LoopCost` measures the cycle *period*,
and its own header says a large p99 on an idle shard means nothing. The 60-bot windows are the
idlest in the ramp, so their tail is the longest and noisiest: two independent 60-bot windows gave
p95 of **23.9** and **47.2** ms, and p99 of 61.5 and 63.1 — while 100 bots gave a tighter 18.7 and
22.8. p50 is 15.6 ms at every count, pinned to the Windows timer granularity. **The metric falls as
the shard gets busier before it ever rises**, so 3x the 60-bot figure is a threshold pointing away
from the direction of travel. The `max` limb is sound in both regimes and is the one to keep. If
this gate is wanted again, anchor it on the busiest clean window (100 bots, p99 22.8) rather than
the idlest.

**Pace cannot be read as tiles per second.** The sampler reports whichever bot it catches, and a bot
given `CurrentSpeed 100ms/step` walks 10 tiles/s where one given `200ms/step` walks 5 — on an
identical shard. Between the 60 and 100 windows that looked exactly like a 50% collapse and would
have stopped the ramp at 100. The quantity that survives comparison is **measured step interval
divided by the pace the bot was given**, which was 0.99-1.04 at every count: the engine delivered
the pace it promised, everywhere, up to 2000 bots.

---

## Findings

**First failing count: none, on the stop rules as written. The first count that stops *paying* is
800.** Every resource stayed flat or grew gently to 2000 bots, while useful fleet activity peaked at
400 and declined thereafter. The bottleneck is **contention for a fixed admission rate**, not
pathfinding cost, save duration, GC, sector activation, or the loop.

**Last passing count: 2000**, on the rules. **Last useful count: 400.**

The cost terms, for the record:

- **Bot tick is linear and trivial** — 0.019-0.024 ms per bot, flat per capita across a 32x
  population change. At 2000 bots it is 18.2 ms of a 2000 ms budget: **0.9%**.
- **Tile pathfinding is the only superlinear term**, and only while the fleet is still moving:
  2,489 -> 4,300 -> 10,119 -> 21,830 searches for 60 -> 400 bots, then it *falls* to 17,162 at 2000
  because there is less walking to do. Mean cost per search stayed 0.67-1.87 ms throughout.
- **GC never moved.** Six gen2 collections per ten-minute window at every single count, 60 through
  2000. Managed heap grew 405 -> 532 MB, working set 551 -> ~700 MB.
- **Saves never moved.** 0.30 s to 0.39 s across the whole ramp, against a 5 s budget.
- **The roads held.** 0.19-0.44 terminal failures per 100 walks, all seven windows, every failure
  `short-of-goal`, no hop failing more than once in any window.

> **There is no GC pause time in this table and there cannot be.**
> `GC.GetTotalPauseDuration()` is .NET 7 and later; this tree is `net48`. The `.NET CLR Memory`
> performance counter is Windows-only and barred by CLAUDE.md section 15. So gen2 **count** is
> reported and `LoopCost`'s **max** is the observable stall — a blocking collection lands in the
> loop period as one large sample. Given gen2 sat at six per window at every population, there is
> nothing here for a pause figure to have revealed.

---

## Proposed default

**Raise `population.target` from 60 to 400.**

The argument is the liveliness table, not the resource table. 400 is the largest population at which
every bot is still doing something: it is the peak of concurrent travellers (141), of the share of
the fleet moving (37%), and of walks per bot (12.8), and it is precisely where the four-plans-a-second
admission rate saturates. Below it the shard is under-used; above it, bots are scenery.

What it costs against the shipped 60: bot tick 1.3 -> 7.2 ms of 2000 (0.36% of budget), managed heap
405 -> 477 MB, save 0.31 -> 0.35 s, whole-loop p99 *better* (63.1 -> 33.5 ms, because the loop is
busier). What it buys: **eight times the bots visibly travelling** — 17 to 141.

Two caveats on the number:

1. **It was measured with the curve flat.** In production the curve returns, so a target of 400
   means about 400 live at the 19:00 peak and about 81 at the 05:00 trough (0.15 x 379 lifecycle,
   plus the 21 fixed). That breathing is the intended behaviour and is arguably the better argument
   for 400 than for a flat 400: the shard would be busy in the evening and quiet at dawn, with the
   evening figure now a measured one.
2. **Nothing here was measured with a real client connected.** Every figure is a headless shard. A
   connected player adds its own sector footprint and its own network path, and the loop figures in
   particular deserve one confirming window with a client attached before 400 ships.

A more conservative 200 is defensible if the 477 MB or the eight-fold jump in visible population is
unwelcome: it holds 67 travellers for 3.7 ms of tick and 429 MB. It is under the cap, so it wastes
nothing — it simply asks for less.

---

## The ceiling, and what would move it

**The ceiling is about 140 concurrent long-distance journeys, and it is an admission rate, not a
resource.** `Custom.BotPlansPerTick` (8) divided by `Custom.BotTickSeconds` (2) is four Traveler
plans a second, and no population raises it.

Moving it is arithmetic on those two numbers: doubling `BotPlansPerTick` to 16, or halving
`BotTickSeconds` to 1, should each roughly double concurrent travellers. The headroom to pay for it
is enormous — at 1600 bots the whole bot layer cost 18.8 ms of its 2000 ms budget, and the planning
the budget exists to ration is the expensive half of that.

**This was not built and should not be, on this evidence alone.** Three things need settling first,
and each is a measurement rather than a change:

1. **Confirm the cap is the plan budget**, by counting `TryTakePlan` refusals per pass. The
   arithmetic fits and the call sites are right, but a refusal counter would make it a fact. The
   competing explanation — destination capacity, since the graph has 298 arrival points against a
   fleet of hundreds and a site's capacity *is* its arrival count — has not been excluded.
2. **Find what the budget is protecting.** It was set at 8 against a 1,031-waypoint graph. The graph
   is now four times larger and a third of routes cross a moongate, so the cost of one admitted plan
   has gone up, and the right new value is not simply "double it".
3. **Re-measure with a client connected**, because the whole-loop numbers this rests on were taken
   with nobody watching.

---

## Method notes worth keeping

- **The baseline was taken twice.** The first 60-bot window regenerated on a running shard rather
  than rebooting first, so it was re-taken through the identical path as every other count. It
  reproduced (p99 61.5 then 63.1), which is how the idle-shard artefact above was identified rather
  than mistaken for contamination.
- **`Bots.Recipe` read `REGEN NEEDED` when the session started** — `GG_BotPop.xml` was missing
  `GG_BotPop_britain_shop_britain-shop-smith`. Cleared before the baseline; `botpop-audit` reported
  "the file matches" before every window, and the driver aborted the step otherwise.
- **The top-10-hops-by-failure ranking asked for here does not exist in the tree.**
  `walk-failures.json` carries a `hop` per failure and nothing aggregates them. It was done as
  read-only post-processing; with 2-20 failures a window and no hop repeating, there was no ranking
  to find. If it is wanted standing, it belongs in `NavWalkFailures` beside `PerHundredWalks`.
- **Three instruments were built for this and are now standing**: GC collection counts and a thread
  count on `ProcessHealth`, and `WorldCensus` (active sectors, awakened creatures, and the fleet's
  location by region rather than by home town). Commit `9eade4b4`, before the first window.
