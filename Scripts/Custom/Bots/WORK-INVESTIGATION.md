# Why gatherers are not working - investigation memo

> **Status, 22 September 2026, later the same day.** Recommendations 1-3 are done and **4 (coloured
> ore) is deferred to 7f, untouched**. The memo below is kept as written.
>
> - **1, reporting:** `Bots.Work` names every stationless class in the same line as the excluded
>   sites (`BotSystem.WorkVerdict`, `WorkFixtures`), and `BotWorkSites.Validate` runs at
>   `[CallPriority(920)]`, so its boot warnings reach `console.json`.
> - **2, weights:** upstream's gatherer override is restored as data (`destinations.singleMinded`).
>   The Fisherman is held for the dock session, by Sean's decision.
> - **3, sites:** `yew-grove` and `minoc-mine` are on the graph. See the Bots README.
>
> Live, at the shipped population, two ten-minute windows saw **7 and 9 clock-ins**, against 0-2
> here. Both windows had Lumberjacks at Yew Grove and Miners at all three faces.
>
> **Two findings came out of the live runs:**
> - **A fixed census race.** With gatherers really mining, the census double-counted ore that landed
>   between behaviour ticks, which read as F5 losses. Fixed, with `HaulFixtures` rule 6.
> - **An open question.** Only 11-13% of gatherer picks chose their own site, because our crowd
>   floor makes empty far-town banks pull ×4. That is a decision, recorded in the Bots README
>   Deviations row *Gatherers are single-minded again*.
> - **Seen, not fixed: `Nav.Doors` can fail under traffic.** It read Fail after both `[BotSmoke`
>   runs and Ok at every boot. The engine patch (MODIFICATIONS entry 6) is intact:
>   `FastAStarAlgorithm.cs:106`, and both walk audits passed.
>   - **The cause is in the check.** `BotDoorCheck.FindDoor` takes the first closed door within
>     3 tiles of the anchor midpoint. At every boot that is the inn doorway door at 1464,1524.
>   - **When that door stands open** (a bot was seen on its tile), the check takes the *other* inn
>     door at 1461,1524, which the route never passes through. It then reports a reverted patch.
>   - **Why now.** The inn sits on the `brit-mine-north` corridor, and single-minded Miners made it
>     busy.
>   - **For Sean.** What the check should say when its doorway is in use (Warn, retry, or choose
>     the door on the route) is his call. It guards an upstream edit.

Read-only investigation of 22 September 2026, at `982fa8c7`. Nothing in the code, config or data was
changed; this file is the only commit. It **proposes**; the decisions are Sean's.

The question: in both 10-minute live windows of the F5 session at the shipped population (1000), no
Miner or Lumberjack clocked in, and the delivery path was exercised only by the work probe. 7f's
economy depends on gatherers working.

---

## In plain language

**Lumberjacks cannot work at all, and never could.** There is no lumber site anywhere on the
navigation graph. A Lumberjack only goes to work by choosing a `lumber` destination, and there are
none. It was left out on purpose on 6 September, when the only candidate wood near Britain was thin
(commit `769c5156`: *"Lumberjacks have no station until then and Bots.Work says so"*). It was never
added, and **Bots.Work stopped saying so** once the Trammel adopt brought in three forges that fail
validation. The health line reports the excluded forges and returns before it reaches the
no-station line. The boot's own warning never reaches `console.json` either.

**Miners can work, but rarely, and the reason is the dice.** Nothing tells a gatherer to go to work.
Between trips, a Miner makes the same weighted choice of destination as every other Traveler, and
only picking a mine leads to a shift. The two mines weigh 10 each. The other 158 destinations weigh
what they weigh for anybody: shops get double for gatherers (the `craft` tag), gates 1.5, and
everything in the home town 2.5×. So a Britain-born Miner picks a mine about 6% of the time, and a
Trinsic-born one well under 1%. A trip takes around five minutes, because a lot of them go through a
moongate to Jhelom, Moonglow or Vesper and end in a 2-6 minute shop visit. So at 550-1000 bots you
get one or two mine picks in ten minutes. Two windows with none is bad luck, not a broken switch.
**In this session's window, two Miners did clock in and mine**: 33 and 31 ore. Afterwards one of
them delivered 37 to the Britain forge and went straight back for a second shift: a full organic
mine → forge → mine loop, and the first I could find any record of.

**uo-offline has the same mechanism but different numbers.** Upstream also relies on the
destination choice alone, with no work schedule. But it makes gatherers *single-minded*: its own
site weighs 10, a bank 0.3, a tavern or inn 0.2, and **everything else 0.02**. Its Miner picks a mine
about 70% of the time. Our port kept the mechanism and dropped that override. The per-class table in
`bots.json` treats a Miner like a Swordsman with a hobby, and nothing in the Deviations table
records the change. Upstream also ships a lumber site at Yew and a mine at Minoc; we have neither.

**It is mostly the graph and the weights, not the population.** The weights have not changed since
before the adopt. The adopt took the graph from 70 destinations to 160, and that halved a Miner's
odds per pick: about 10% before, about 6% now, for a Britain-born Miner. The population only sets how
many Miners there are to roll (2% of the lifecycle bots). At the old 60 that was roughly one Miner,
often none, so organic gatherer work was already close to absent at 60. I found no record of an
organic clock-in in the history, and none would have been expected. Lumberjacks were already
impossible then.

**Coloured ore is real, and every count in the bot code misses it.** This is not new with the
`PlayerMobile` swap. `Mining.GetResourceType` returns the vein's own ore type for anyone. The
`PlayerMobile` checks only gate gems, granite and sand. About half the veins are coloured, and a
Miner with enough skill keeps the coloured ore half the time. `GathererBehavior.Carried`, the pack
capacity check, `HaulPending`, `BotHaul` and the `Bots.Conservation` ledger all count exact
`IronOre`/`Log` only. So a coloured stack is invisible:
- it is never mined or delivered in the books;
- it never makes a bot haul;
- it is destroyed without a record when the bot logs out or the boot purge runs;
- the ledger still reads **Balanced**.

The comment in `BotHarvest.cs` saying a bot "only ever gets PLAIN ORE" has been wrong all along.
**Seen live.** The work probe's Grandmaster miner had a stack of **Copper ore** in her pack beside
the iron, while her own status read "carrying 14/120", iron only. The probe then removed her, the
loss record named 14 iron ore and nothing else, and the health line still said Balanced.

**Recommendation, briefly:**
1. Fix the reporting now; it is cheap and independent.
2. Before 7f, port upstream's single-minded gatherer weighting as data in `bots.json`.
3. Author a lumber site and at least one mine outside Britain.
4. Decide coloured ore on purpose before 7f prices anything. My recommendation is to count by
   `BaseOre`/`BaseLog` and let the colour travel.

Details, options and trade-offs follow.

---

## 1. The cause, traced

### What has to be true for a gatherer to start a shift

A gatherer becomes a `GathererBehavior` in exactly one way: an **arrival handoff**. Nothing seeds
one: the population recipe pins only crafter stations, at `BotPopulation.cs:326-361`. Nothing
schedules one either. Every link below must hold, in order:

| # | Condition | Where | Fails in ordinary running? |
| --- | --- | --- | --- |
| 1 | The bot is a **Lifecycle** bot whose brain is `Traveler`. It is not in a Shopper or BankSitter visit, not `Idle`, and not a fixed bank sitter | `BotLifecycle.cs:39` (rolls Traveler or Idle only), phases 30-180 min halved or doubled, `BotPersonality.cs:68`; fixed bots never re-roll, `BotLifecycle.cs:218` | Partly. Miners spent **31%** of the window in Shopper visits. None were fixed this boot |
| 2 | It gets a **route-plan slot** | `TravelerBehavior.cs:321` → `BotTickManager.TryTakePlan` | No. 4-6 plans/s were granted fleet-wide |
| 3 | The **weighted pick lands on its own site type**: `mine` for a Miner, `lumber` for a Lumberjack | `BotDestinations.Pick`, `BotDestinations.cs:31-199` | **Yes. This is the gate.** See below |
| 4 | It **routes and arrives** | `TravelerBehavior.cs:346-464` | No. Both mines route from everywhere; the logged weights carry route tiles |
| 5 | **Handoff:** not hauling, then the station roll `life.station` 0.95, then `BuildVisit` → `IsOwnStation` → `GathererBehavior` | `TravelerBehavior.cs:519`, `:529-547`, `:598-605`; `BotWorkSites.cs:352-377`; `bots.json:140` | No |
| 6 | **Site resolves:** a nav zone tagged `mine` or `lumber` at the bot or its destination | `GathererBehavior.cs:191-208`, `:872-900` | No for Miners (both mine destinations sit in their zones). Lumberjacks never get this far |
| 7 | **Clock-in:** inside the zone **and** at least one harvestable tile in reach, or walk in within 75 s | `GathererBehavior.cs:222-229`, `:373-384`, `:481-517` | No. Both live clock-ins passed first time (reach 11 and 4) |

### Condition 3, in numbers

The pick is `weight = byType[type][class] × Π byTag[tag][class] × homeBias(2.5 if home-town tag)
× crowd floor (not hauling) × for work types: VacancyFactor × DistanceFactor`. The pieces:
`BotDestinationConfig.WeightFor` at `:285`, `BotDestinations.cs:63-173`, DistanceFactor at
`:211-243` = `1/(1 + routeTiles/150)` and 0 if unroutable. Excluded sites are zeroed at `:80`.

For a Miner:
- `mine` = 10.0 (`bots.json:57-60`);
- `gate` = 1.5 (`:37-42`);
- `craft` tag = **2.0** (`:67-75`), which applies to every smithy, tailor, carpenter, bowyer and so on;
- everything else is at the class default of about 1.0, times 2.5 in the home town.

On the current graph there are 160 Trammel destinations: 88 shops, 14 forges, 10 banks, 9 docks,
7 gates, 6 shrines, 2 mines and more. An offline replica of the pick (reads `bots.json` and
`navigation.json` bytes; straight-line distance × 1.3 standing in for road tiles, which matches the
live logged weights to within about 10%) gives:

| Miner | Pre-adopt graph `a910d97a^` (70 dests) | Current graph (160 dests) |
| --- | --- | --- |
| Britain-born, standing at `brit-bank` | **10.4%** of picks are a mine | **5.6%** |
| Trinsic-born, standing at `trinsic-bank` | 1.3% | 0.6% |
| Any Lumberjack | 0% (no `lumber` destination) | 0% |

For a Britain-born Miner at the bank the whole field weighs 327, and the two mines 18.2 of it.
Shops weigh 213.5: every Britain shop tagged `craft` is 1.0 × 2.0 × 2.5 = 5.0, half a mine. Then
docks 24, banks 15.6, gates 15, shrines 12. A Trinsic-born Miner gets no home bias at the mines
(both are tagged `britain`), and its distance factor is around 0.1-0.3.

`bots.json` destination weights are **unchanged** since before the adopt. `git diff a910d97a^ HEAD
-- Data/Custom/bots.json` touches only the population block (target 60 → 1000, plan budget).

### Lumberjacks: no site, and the warning is hidden twice

- `navigation.json` has **no `lumber` destination and no zone tagged `lumber`**: 160 destinations,
  8 zones, the only work zones being `brit-mine-north-face` and `brit-mine-west-face`. `Bots.Population`
  says so indirectly: *"byType.lumber matches nothing on the graph"*.
- `BotWorkSites.Validate` does warn: *"Lumberjack has no station … Bots of that class will never
  work"*, `BotWorkSites.cs:532-560`. It runs from `BotSystem.Initialize` (`BotSystem.cs:76`,
  **untagged**, `:93`). Commit `8bb53c6e` records that output printed between `Timestamp.cs`
  replacing `Console.Out` from an untagged Initialize and the tap re-wrapping it at
  `[CallPriority(900)]` goes to the window only. This boot's `console.json` has **one** WARN line in
  1076, and it is not this one. You saw it in the window. The file never has.
- `Bots.Work`'s health builder checks excluded sites first and **returns** (`BotSystem.cs:688-697`)
  before its stationless branch (`:703-712`). Since the adopt, three forges are always excluded:
  `britain-forge-3`, `jhelom-forge-2` and `skara-brae-forge`, none of which has an arrival within
  two tiles of both a forge and an anvil. So the line "Lumberjack works 'lumber' and the graph has
  none" has not appeared in health since 15 September. Neither has the Fisherman's `dock` line,
  which README Seam row 10 describes as a Bots.Work **fail**.

The three excluded forges named in the brief are a symptom here, not a cause. They are Smith
stations, and a Miner's haul simply goes to one of the eleven valid forges. What they break is the
report.

---

## 2. Live evidence

Boot `9f67ace8`, 17:11 UTC (10:11 local), shipped config. `TargetNow` = 1000 × curve at 10:00 =
**550**, and 571 were live (550 lifecycle + 21 fixed). The window ran 17:11:40-17:23:38 (12 minutes,
36 samples of `entities.json` + `botlog.json` via `livemap-on 5 custom`, `health` every 40 s).

### Gatherers present

| | At boot | Seen in window | Fixed-slot | Clocked in |
| --- | --- | --- | --- | --- |
| Miner | 11 | 15 (4 spawned during it) | 0 | **2** |
| Lumberjack | 13 | 14 | 0 | 0 |

### Were they eligible, and what blocked the rest?

**Eligible** here means able to reach a shift by the only path: a Lifecycle Traveler whose class has
a site on the graph, rolling with that site's weight above zero.

- **All 15 Miners were eligible.** Every logged pick carried both mines at non-zero weight with a
  real route: `brit-mine-north w11.87 166t, brit-mine-west w7.46 353t` near Britain, down to `w1.25
  808t` from the far towns. None was fixed or unroutable, and none was Idle (no first phase had
  ended).
  - Their 34 outbound picks went **3** times to a mine (8.8%), with kate Fairweather, Inga and
    Raelfred.
  - The other 31 went to shops and banks in eight towns: 7 Jhelom, 5 Trinsic, 3 Moonglow, 3 Vesper,
    2 Yew, 2 Skara Brae, the rest Britain.
  - Time split: 63% travelling, 31% in Shopper visits, 7% at a face.
  - **What blocked them was the pick itself.** Nothing else was in the way.
- **All 14 Lumberjacks were blocked by the absence of any lumber site.** They made 35 picks, all
  towns, spending 55% of the time travelling and 43% shopping.

### The two shifts

| | kate Fairweather (Expert) | Inga (Apprentice) |
| --- | --- | --- |
| Pick | `*brit-mine-north w7.49 351t` from the east weaponsmith, 17:13:20 | `*brit-mine-west w4.44 695t` from Moonglow, through the gate, 17:15:48 |
| Clock-in | 17:14:31 at 1453,1529, reach 11 | 17:18:07 at 1197,1753, reach 4 |
| Shift | 33 ore mined, then walking to `brit-forge` carrying 37 at 17:23. After the window: delivered 37 (accepted), second shift from 17:28:49 | 31+ ore mined, still working at 17:23:37 |
| Beast | spawned | spawned |

End of window, `Bots.Work`: *"1 gatherer(s) out (1 working, 0 walking in), 1 hauling. 61 unit(s)
mined"*. `Bots.Conservation`: *"mined 61 … unexplained 0 … Balanced"*.

### Reconciling with the F5 windows

About 11-15 Miners × one pick per ~5 minutes × 6-9% gives an expected **1-2 mine picks per 10
minutes** at this population and hour. A window with none is roughly a one-in-four event, and two in
a row roughly one in twenty. Rarer if those windows ran at a lower point of the daily curve, or if
their Miners happened to be Trinsic-born. The F5 observation is consistent with this rate. It does
not need a second fault, and none was found.

---

## 3. Population or graph?

Answered by arithmetic, as agreed; no 60-bot run.

- **The per-Miner chance is set by the graph and the weights.** It halved with the adopt: 10.4% →
  5.6% for a Britain-born Miner, 1.3% → 0.6% for Trinsic. The adopt also made trips longer, since
  gated trips to six new towns now end in shop visits, so there are fewer picks per hour. It also
  made a third of the population's destinations towns that have no mine anywhere near them.
- **The population sets how many Miners roll.** At 2% of lifecycle bots: about 1 at the old 60, 11
  at 550, about 20 at the 1000 peak.
- **So organic gatherer work was already close to absent at 60.** One Miner (often none: the chance
  that 40 lifecycle bots include no Miner is about 45%) at 10% a pick is well under one shift an
  hour. Every gatherer clock-in the commit log describes is the work probe's or a hand-attached
  one (`[BotBehavior`); I found no record of an organic one before today.
- **Lumberjacks: impossible at every population since 6 September.**

The population rise actually made gatherers *more* visible in absolute terms. The graph change made
each one less likely to work.

---

## 4. uo-offline, and whether this is by design upstream

Reference `E:\dev\UO\uo-offline` at `7f38c7c`, paths under `playerbots/source/CustomBots/`.

| | uo-offline | This shard | Match? |
| --- | --- | --- | --- |
| What starts a shift | Traveler destination roll, then arrival handoff at 0.95, 4-8 min window (`Behaviors/TravelerBehavior.cs:2543-2560`) | Same (`TravelerBehavior.cs:519-605`, `bots.json:140`) | **Match** |
| A work schedule or timer | None. Roll only | None | **Match** |
| **Gatherer weights** | **Single-minded**: own site 10.0, other site 0.0, bank 0.3, tavern/inn 0.2, **everything else 0.02** (`Behaviors/DestinationType.cs:337-352`) | Per-type × per-tag class table: site 10.0, but shops ×2.0 `craft`, gates 1.5, home ×2.5, all 158 others at default | **Diverges.** Not recorded in the Deviations table |
| Distance term | None | `1/(1+tiles/150)` on work sites only | Diverges (ours, documented, README *Work sites are weighted by how far they actually are*) |
| Capacity / vacancy | None | VacancyFactor to 0 at arrival count | Diverges (ours, documented) |
| Sites shipped | 3: LumberSpot **Yew Grove** (570,921, 16×16 polygon 562-578 × 913-929), MiningSpot 418 (2569,486, **Minoc**), MiningSpot 448 (1256,1239, Britain) | 2 mines (`brit-mine-north`, `brit-mine-west`), **no lumber** | Diverges |
| No painted zone | Works where it stands, 4-tile leash | Refuses and leaves (`GathererBehavior.cs:194-208`) | Diverges (ours, deliberate: real harvest needs real rock) |
| Lifecycle | BankSitter / Adventurer / Traveler / Idle, 30-180 min phases | Traveler / Idle | Close |
| Yield | Invented `new IronOre(2..6)` every 35-55 s (`Behaviors/GathererBehavior.cs:428-430`) | Real `Mining.System` / `Lumberjacking.System` | Diverges (ours, deliberate) |

Upstream's odds, same arithmetic over its 488 destinations: a Miner picks a mine about **70%** of
the time and a Lumberjack Yew Grove about 54% (73% if Yew-born). **Upstream by design relies on the
roll and makes the roll nearly certain. We kept the roll and lost the near-certainty.**

---

## 5. Options for the gatherer fix

The shape of the fix is two independent halves: make gatherers want to work, and give them
somewhere to work. Either half alone does little.

### A. Port upstream's single-minded gatherer weighting, as data

Give `bots.json` a way to say *"for this class, anything not named weighs X"*. For example, a
per-class `otherwise` beside `byType`, set for Miner and Lumberjack to 0.02, with bank 0.3 and
tavern/inn 0.2 as upstream has them. `WeightFor` reads it where it currently falls back to the type
default.

- **Effect** (replica, current graph, with our home bias and distance term kept): a Britain-born
  Miner picks a mine **64%** of the time, a Trinsic-born one 18%, one in Jhelom 13%. Without the
  distance term those are 83% and 70%.
- **Trade-offs:**
  - Gatherers mostly vanish from town life between shifts. That is upstream's world, and it is what
    a gatherer is.
  - The two mines hold 9 bots; VacancyFactor then zeroes them, and the overflow falls back to the
    0.02 field. That is correct and keeps them milling in town rather than queueing at a face.
  - It does nothing for Lumberjacks without B.
  - Our distance term, which upstream does not have, then dominates for anyone not born in Britain.
    That is an argument for B rather than for dropping the term.
- **Cost:** a small `BotDestinationConfig` change plus a JSON key. The same key could later make
  artisans single-minded if that is ever wanted (upstream does both).

### B. Author the missing sites

- **A lumber site.** Upstream's is **Yew Grove**, and Yew is on our graph: gate, bank, stables,
  shops. It needs a destination, arrivals and a zone through the editor Site tool, then a
  `site-reach` check against `Custom.BotWorkSiteMinReachLumber` (3). `brit-lumber-south`
  (1422,1832, reach 4) is the near-Britain wood `769c5156` chose not to write; it clears the floor
  of 3.
- **A mine outside Britain.** Upstream's second is at **Minoc** (2569,486), and Minoc has three valid
  forges on our graph already, so a Minoc Miner delivers locally.
- **Trade-offs:** authoring time and a live walk/reach audit per site. A Lumberjack's load goes to a
  Carpenter's `woodworking` station, so check the Britain bench is staffed and that one exists near
  Yew, or accept bank deliveries there.

### C. A work bias in the pick instead of weights

Before the weighted roll, a gatherer Traveler goes to its nearest available site with probability
*p* (say 0.7).
- Simple, and independent of how the destination table grows.
- **Trade-off:** a second mechanism beside the weights, which the README has spent a lot of words
  keeping to one, and a divergence from upstream's shape where A is a return to it.

### D. Fix the reporting (independent of the rest)

- `Bots.Work` reports excluded sites **and** stationless classes together rather than returning at
  the first. The stationless list is a Warn today and should stay one line.
- Either `BotSystem.Initialize` gains a priority above 900, or the load warnings are repeated into
  health, so "Lumberjack has no station" reaches `console.json` and the editor.
- Add "N gatherer(s) could not reach any site" to `Bots.Population`'s *no station at home* line. It
  already names the Lumberjacks by town.

### Recommendation

**D now, then A + B before 7f.**
- D is small and makes the next regression visible.
- A is a return to upstream's intent, as data, and gets Britain Miners to about two shifts in three
  picks.
- B gives Lumberjacks a reason to exist and non-Britain Miners a mine within reach. Yew Grove and
  Minoc are the two sites upstream chose.

I would not do C. And I would not remove the distance term: with B it becomes the thing that sends a
Minoc Miner to the Minoc face rather than to Britain.

Not recommended as a lever: the population. Doubling it doubles the Miners and does nothing for the
odds.

---

## 6. Coloured ore

### Source

- **Veins** (`Scripts/Services/Harvest/Mining.cs:90-99`): Iron 49.6%, then Dull Copper 11.2, Shadow
  Iron 9.8, Copper 8.4, Bronze 7.0, Gold 5.6, Agapite 4.2, Verite 2.8, Valorite 1.4. Each coloured
  vein falls back to iron at 0.5. `RandomizeVeins = Core.ML` (`:121`) re-rolls a bank's vein when
  it respawns.
- **Resource choice** (`HarvestSystem.cs:359-372`, `MutateResource`): past the 0.5 fallback, the
  coloured resource is kept unless the harvester's skill is below its `ReqSkill`. Those are 65 / 70
  / 75 / 80 / 85 / 90 / 95 / 99 (`Mining.cs:78-86`). Then `CheckHarvestSkill` (`HarvestSystem.cs:265-268`).
- **Type** (`Mining.cs:187-233`, `GetResourceType`): the `PlayerMobile` branches are gems (`:216`),
  granite (`:221`) and stone-only (`:224`). All three need flags nothing in `Scripts/Custom` sets.
  Everyone else, bot or player, gets `resource.Types[0]` (`:229`), which **is the coloured ore on a
  coloured resource**.
- **So this does not depend on the `PlayerMobile` swap.** `BotHarvest.cs:30-34` (*"A bot only ever
  gets PLAIN ORE … falls through to resource.Types[0] for anything that is not a PlayerMobile"*)
  reads `Types[0]` as "iron". It never was.
- **Lumberjacking** has no `PlayerMobile` gate at all: Oak 65, Ash 80, Yew 95, Heartwood / Bloodwood
  / Frostwood 100.
- **Who gets it:** bot skill is a fraction of cap by tier (`BotSkillTemplate.cs:275-288`), ±3-10.
  Journeyman and up can reach 65: about **45%** of Miners and Lumberjacks, and every probe miner
  (Grandmaster).

### Does the bot code see it? No, anywhere

`DullCopperOre` and friends are **siblings** of `IronOre` under `BaseOre` (`Scripts/Items/Resource/Ore.cs:8`,
`:443`, `:489`), and `OakLog` of `Log` under `BaseLog` (`Log.cs:6`, `:120`, `:290`). Every count is
by the exact yield type (`BotHarvest.YieldFor`, `BotHarvest.cs:91-103`):

| Reader | How it counts | Coloured ore |
| --- | --- | --- |
| `GathererBehavior.Carried` (`:908-926`) | `GetAmount(typeof(IronOre))`, which matches by `IsAssignableFrom` (`Container.cs:1260`) | invisible, since the classes are siblings |
| Pack-full check (`:346-351`, `Capacity` `:929-932`) | on `Carried` | not counted. The 60/120 cap is iron only, so a bot on a coloured vein keeps swinging past it |
| `NoticeYield` / `BotGoodsLedger.NoteMined` (`:393-416`) | on `Carried` | never counted as mined |
| `EndShift` → `HaulPending` (`:633-651`) | `Carried > 0` | a coloured-only load reads as **nothing**: no haul, pack beast released |
| `BotHaul.InContainer` / consume / bank (`BotHaul.cs:88`, `:201`, `:273`) | `item.GetType() == raw` | never offered, delivered or banked |
| `BotGoodsLedger` census (`:261-275`, `:376`) | `BotHaul.Custody`, tracked types only | never held, lost or unexplained. **Conservation reads Balanced** |
| `BotSession.CanLogoutNow` (`:325`, `:379`) | `HaulPending` | a bot holding only coloured ore logs out, and the ore is deleted with it, unrecorded |

The one real harm a player could see today: coloured ore **accumulates in a gatherer's pack until
the session ends and then disappears**. Nothing hauls it and nothing books it. Past the real pack
limit, `HarvestSystem.cs:207-209` would delete further harvests, including iron, with no record,
because `Carried` says there is room.

Seen, not investigated: `WeightOverloading.cs:66-83` applies stamina drain to any `Player`-flagged
mobile. Kate walked to the forge carrying 37 ore, so it did not stop her.

### Live

There is no live surface that lists a bot's pack by item type, so this was read from the save.
`Data/Live/bot-items.json` (every item serial a live bot owns, written at each save by
`BotOrphans`) is joined with `Saves/Items/Items.idx` (type index + serial per item) and `Items.tdb`
(type names). Read-only; the script is 60 lines of Python.

| Save | Bot | Tier (Mining) | Resource stacks held | What the bot code said |
| --- | --- | --- | --- | --- |
| gen 12, 17:24:32 | kate Fairweather, hauling from `brit-mine-north` | Expert (73.2) | IronOre ×3 stacks, Diamond ×1 (spawn kit; 53 bots hold one) | carrying 37 |
| gen 12 | Inga, working `brit-mine-west` | Apprentice (about 45, cannot reach 65) | IronOre ×4 | carrying 41 |
| gen 14, 17:31:49 | **Eira, work-probe miner at `brit-mine-north`** | Grandmaster | IronOre ×4, **CopperOre ×1**, Citrine (kit) | **"carrying 14/120"** |
| gen 14 | Lael, work-probe miner, hauling | Grandmaster | IronOre ×4 | carrying her load |
| gen 15, 17:32:43 | kate Fairweather, second shift | Expert (73.2) | IronOre ×3, Diamond | carrying 25 |

Three seconds after the gen-14 save the probe removed Eira. `goods-lost.jsonl` has exactly one row
for her: `"why":"probe" … "item":"IronOre","amount":14`. The copper went with her and nothing
recorded it. `Bots.Shift` reported *"Ok … a full cycle completed"* and `Bots.Conservation`
*"… unexplained 0 … Balanced"*.

Both organic miners' faces were iron veins this time. kate's 33 + 25 ore arrived as an unbroken run
of `+1`s, which is what iron looks like. Kate's skill is enough for Dull Copper and Shadow Iron on a
coloured bank, so she would have picked it up with the next vein roll. Across the whole fleet at
gen 14, one bot held a coloured-ore stack: Eira.

**Kate's organic haul was delivered.** `Bots.Conservation` recorded 2 settled hand-overs, 57 units
accepted: the probe's 20, plus Kate's 37 at `brit-forge` after two stuck-recovery moves on the
approach (17:25:54, 17:27:34). She then rolled the mine again and was on a second shift at 17:28:49.
So one full organic mine → forge → mine loop happened in this boot.

### Options for coloured ore

1. **Count the family, keep the colour.** `YieldFor` becomes the base type (`BaseOre` / `BaseLog`)
   for counting, capacity, `HaulPending`, `BotHaul` and the ledger; delivery moves the real stacks.
   - A smith then receives coloured ore. Today `CrafterStock` mints `IronIngot` by count
     (`CrafterStock.cs:118`), so the colour would be flattened at the bench unless 7f gives it a
     price and a product.
   - **Trade-off:** the most honest option, and it touches every reader in the table.
2. **Keep bots on iron.** Clamp the bot's effective harvest to iron, for example a `BotHarvest` swing
   that retries or rejects a coloured type.
   - Smallest change.
   - **Trade-off:** it overrides the real harvest system this layer was built on, and a coloured vein
     then yields nothing to a bot.
3. **Count it and bank it.** Count the family (as in 1) but hand only iron to the smith and bank the
   rest.
   - No 7f pricing question now.
   - **Trade-off:** a second delivery rule.

**Recommendation: 1, decided together with 7f's prices.** Whatever 7f pays for ore should know what
colour it is. Independently, `BotHarvest.cs:30-34` should be corrected so nobody else relies on it.
