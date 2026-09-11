# Custom/Bots

> **Licence.** The code in this folder is a derived work of the PlayerBots system in
> [`Klein187/uo-offline`](https://github.com/Klein187/uo-offline), **Copyright (C) Klein187**,
> licensed under the **GNU General Public License, version 3**. It is therefore distributed under
> GPL-3.0, and every file here carries that notice. See [`LICENSE-BOTS`](../../../LICENSE-BOTS) at
> the repository root. The rest of this repository is GPL-2.0-or-later (RunUO/ServUO lineage),
> which combines lawfully with GPL-3.0; the combined work is GPL-3.0. Provenance, not legal advice.

Fake players: a `PlayerBot` with a class, a skill tier, a personality, a name, a speech colour and
an outfit. Config is `Data/Custom/bots.json`; namespace `Server.Custom`.

**Sessions 1 to 4 of the bot layer.** Session 1 was a bot you could spawn and inspect: class,
tier, skills, stats, name, speech hue, outfit. Session 2 made them live in the world — a behaviour
tick, a `Traveler` that walks the shard's `NavWalker` between destinations, and class-weighted
destination choice from `bots.json`.

Session 3 gave them a life: a phase roller that picks a new behaviour from the bot's own
personality, a bank with a standing crowd, and shops worth browsing.

Session 4 gave them a voice — the chat corpus, ambient chatter per behaviour, and a bot that turns
and answers when you say its name. See [Speech](#speech).

Session 5 gave them work. A Miner or Lumberjack walks out of the west gate to a real rock face or a
real wood, digs or chops with ServUO's own harvest system, hauls the load back and hands it to the
Smith or Carpenter it belongs to; the artisan turns it into real goods through ServUO's own craft
system. See [Working](#working).

Session 6 gave the world a population. A recipe read off the nav graph decides how many bots each
town holds and what they are doing — a crowd at every bank, a smith at every forge, a shopper in
every shop — writes it to `Spawns/Custom/trammel/GG_BotPop.xml`, and a daily curve makes the roaming
half of it rise and fall with the hour. See [Population](#population).

The full port survey is `docs-src/uo-offline-port-survey.md`.

---

## The constraint everything else follows from

**A bot is a `BaseCreature`, not a `PlayerMobile`.** Upstream it is a `PlayerMobile`, and that one
change drives most of the differences in this folder.

Four reasons, in the order of what they cost to work around otherwise:

1. **`NavWalker` takes a `BaseCreature`** (`Core/Navigation/NavWalker.cs:63`). It drives
   `BaseAI.DoMove` and works around `ForceStayHome` and `Home`. This shard has exactly one walker
   on purpose, and a `PlayerMobile` cannot use it.
2. **Doors.** `FastAStarAlgorithm.cs:93` sets `MoveImpl.AlwaysIgnoreDoors` from `bc.CanOpenDoors`,
   and only for a `BaseCreature` — a `PlayerMobile` bot treats every closed door as a wall.
   `CanOpenDoors` defaults true for a humanoid body (`BaseCreature.cs:1924`). Upstream had to patch
   its pathfinder to get this; here it is free.
3. **The `PlayerMobile` dependency was shallow** — 15 overrides upstream, and `OpenTrade`,
   `ApplyNameSuffix`, `CheckShove` and `ShouldCheckStatTimers` are all virtual on `Mobile` anyway.
4. **It matches the actor pattern already running here** — `DailyLifeTownsfolk` is a `BaseCreature`
   with a stand-aside AI.

## Where a `BaseCreature` reads as an NPC, and what was done about it

| Surface | As a plain `BaseCreature` | Here |
| --- | --- | --- |
| **Notoriety** | `Notoriety.cs:441-443` falls through to `CanBeAttacked` for anything not `InitialInnocent`, so the bot reads grey | `InitialInnocent` overridden to `true`. `Murderer`, `Criminal` and guild notoriety all still work — they live on `Mobile` |
| **Paperdoll** | Works. `Mobile.CanPaperdollBeOpenedBy:6654` gates on `Body.IsHuman` | nothing needed |
| **Context menu** | `BaseCreature.GetContextMenuEntries:4559` adds Rename, AI commands, Tame and Teach | all four are gated on flags a bot leaves false, so the menu is a player's: just the paperdoll |
| **Party** | **Refused,** then **ignored.** `AddPartyTarget.cs:30-32` answers a human-bodied non-player with *"Nay, I would rather stay here and watch a nail rust."* Passing that gate is not enough: the invitation is a packet, and a bot has no `NetState` to receive it | `Player = true`, plus `BotParty` — see below |
| **Name and guild** | Works. `Guild`, `GuildTitle` and `DisplayGuildTitle` are all on `Mobile` and serialized there | nothing needed |

### What `Player = true` costs

- **Combat timer priority.** The combat timer is created only when `Combatant` is set and is
  destroyed when it clears (`Mobile.cs:2215-2245`), so an **idle bot has no combat timer at all**.
  While actually fighting, the flag moves it from the 50 ms polling bucket to every tick
  (`Timer.cs:136`). Note the stock condition is `!m_Player && m_Dex <= 100`, so any bot with Dex
  above 100 lands in the every-tick bucket regardless of this flag.
- **Death.** `Mobile.OnDeath:4229` deletes a dying mobile only when the Player flag is false, so a
  bot ghosts instead of vanishing. `PlayerBot.OnDeath` deletes it on a one-second delay — delayed
  rather than inline so the death packets and the corpse finish being built first, and the **corpse
  is deliberately kept**, because it is the thing a player would loot and the death behaviour will
  want it. Replaced by the death behaviour session.
- **The live map.** `LiveMapSnapshot.KindOf` tests `Mobile.Player` early, so without care every bot
  would draw as a real logged-in account. A `"bot"` kind is tested **before** that branch.

## Caps are configuration, never literals

Upstream was a T2A shard and said so in its numbers: a 225 stat total with each stat at 100, a 700
skill cap, and a Grandmaster primary hardcoded at 99.0. This shard is EJ. **No cap literal appears
anywhere in `BotSkillTemplate.cs`.**

What survives from upstream is the *shape* — which skills a class has, which leads, and the relative
standing of primary, secondary and utility. Stat profiles became proportions; tier targets became
fractions of the per-skill cap.

Resolution order, in `BotCaps.Resolve`:

```
skillCap    <- PlayerCaps.SkillCap      / 10   (1000 -> 100.0)
skillTotal  <- PlayerCaps.TotalSkillCap / 10   (7000 -> 700.0)
statCap     <- PlayerCaps.StrCap               (125)
statTotal   <- PlayerCaps.TotalStatCap         (225)
```

`Data/Custom/bots.json` carries **no cap values** unless someone deliberately sets one.
`Config/PlayerCaps.cfg` is this shard's authoritative source and already holds the EJ values;
duplicating them would create a second source of truth that drifts the first time either is edited.

Setting one is allowed — a shard of deliberately weaker bots is a legitimate thing to want — but it
is never silent. `Bots.Population` always ends with a caps clause, one of:

```
caps: from PlayerCaps.cfg (skill 100/700, stat 125/225)
caps: overridden (skillTotal 600 vs players 700)
```

## Party invitations

`Player = true` only gets a bot past the *gate*. `Party.Invite` (`Party.cs:167`) then adds the bot
to the party's `Candidates`, sets `bot.Party` to the **leader Mobile** — that is what "pending"
looks like — sends an invitation packet, and starts a 30-second `DeclineTimer`. A player answers by
typing `/accept`.

A bot has no `NetState`, so the packet goes nowhere and nobody types anything. The invite is not
refused; it *expires*, and the leader is told the bot "does not wish to join the party."

`BotParty.CheckInvite` notices the pending invite and walks the same path `/accept` walks —
`PartyCommands.Handler.OnAccept(bot, leader)`, **not** `Party.OnAccept` directly, because the
handler is where the candidate and capacity checks live and going around them would let a bot join
a full party. It answers on a **1.5–4 second delay**, so it reads as a person clicking rather than
a reflex, and comfortably inside the 30-second decline.

It is noticed in **`PlayerBot.OnThink`**, which the AI timer calls only while a player is in the
sector (`BaseAI.cs:3072-3082`). That is exactly when an invitation can arrive — somebody has to be
standing there to send one — so this costs nothing for a bot alone in the woods and needs no sweep
of its own.

**Acceptance is unconditional for now.** Whether a bot *should* join — mid-errand, an outlaw,
dislikes the asker — is the social layer's decision and belongs with the personality it would read.

**Nothing is cached.** `Party.Remove` and `Party.Disband` both write `m.Party = null` straight onto
the `Mobile` (`Party.cs:297`, `:343`) with no packet involved, so the engine already clears the
reference when a leader kicks the bot or the party breaks up. The only way to hold a stale one is to
keep your own, so `BotParty` keeps none: every decision re-reads `bot.Party`, and the accept
re-validates that the leader has not changed underneath it. A bot deleted while still a candidate
declines on its way out, so the leader is not left waiting thirty seconds on somebody who no longer
exists.

## Ephemerality

> **Sessions 1 to 6.** This folder now covers identity, movement, life, speech, work and
> **population**. What follows is the ephemeral rule, which the population session leans on rather
> than changes: bots are still transient, and now so is nothing else — the spawners that make them
> are rebuilt from a file on every import. See **Population**.

**Bots never survive a restart**, and unlike upstream this shard has to do something about it.

ModernUO writes player characters through their **account**, so an accountless bot was never in the
world save at all — upstream got the guarantee for free. ServUO's
`StandardSaveStrategy.SaveMobiles` (`Server/Persistence/StandardSaveStrategy.cs:74-76`) writes
**every** mobile in `World.Mobiles`, with no account filter. Verified rather than assumed: a save
taken with three bots standing grew `Saves/Mobiles/Mobiles.bin` by ~144 KB.

The answer is the same ephemeral idiom the daily-life actors use — `Timer.DelayCall(Delete)` at the
tail of `Deserialize` (ServUO has no `[AfterDeserialization]`). Also verified: that save reloaded
24,928 mobiles and stood at 24,925 moments later. Exactly three.

`LiveRegistry` registration follows the ephemeral rule too — **from the constructor, never from
`Deserialize`**, or a bot that is about to delete itself would sit on the editor's live map for one
tick. `OnAfterSpawn` also registers, harmlessly (`Register` no-ops on a duplicate), so the spawner
path the population session adds is already covered.

## Deviations from uo-offline

Places where this port deliberately does something else. **Each is a decision, not a gap** - if one
looks like a mistake later, read the reason before "fixing" it back.

> **Which upstream a row is talking about.** Every row below was written against the `fe18a469`
> snapshot at `C:\Users\sean.GEKKOSTATE\uo-modernuo\ModernUO`, and its file:line citations are
> paths in *that* tree - bot source under `Projects/UOContent/CustomBots/`, data under
> `Distribution/Data/`. **The reference has moved.** New work reads the git clone at
> `E:\dev\UO\uo-offline`, pinned at `7f38c7c`, where the same files live under
> `playerbots/source/CustomBots/` and `playerbots/data/` - and which, unlike the snapshot, carries
> real history for them. A row is only re-pinned when it is next revisited: rewriting them all at
> once would restate seventy citations nobody had re-checked. The navigation data itself is
> identical at both pins (`Data/Custom/reference/README.md` has the record-level comparison), so no
> row that turns on waypoints or destinations is affected by the move.

### A haul goes to the nearest STAFFED bench, not to any bench of the trade

Upstream's haul weighting is a pure type switch (`DestinationCatalog.cs:191-206`): `Bank => 2.0`,
`Forge`/`VendorSmith` `=> 9.0` for a Miner, everything else `0.02`, then a `HomeCity` x2.5 bias and
`BotDangerMap`. It asks neither **who is there** nor **how far it is**. Both questions are ours.

**Staffing.** A load left at an empty forge is a load in a bank box with extra steps, so a station
with a crafter of the trade clocked in at it weighs 20.0, and an empty one of the same trade drops
to the bank's level once anybody is staffed anywhere. Upstream never met this: their spawner pins a
smith at every forge, so every forge is staffed by construction. Ours has four forges and one
probe-staffed smith.

**Distance.** Staffing alone still left every staffed forge on the facet weighing the same, so the
choice between them was dice. Measured: a miner that filled its pack at `brit-mine-north`
(`1449,1521`) walked **255 tiles** to `britain-forge`, past `brit-forge` **36 tiles** away, and
`Bots.Shift` read that as a hand-over that never happened - because the smith it was watching was
the one that got walked past. The nearest staffed bench now takes 20.0 and a farther one 0.5.

Distance is deliberately **not** a general term in the weight table, and the `HomeBias` comment in
`BotDestinations` says so: a bot choosing where to spend its evening should not be dragged towards
whatever is closest, or every town empties into its own bank. A haul is the exception, and the load
is the reason - the ore is already in the pack, every tile is another chance for the recovery
ladder to fire, and one bench of the trade is as good as another. A far bench falls to 0.5 rather
than being excluded, so a lone distant forge is still reachable.

### The phase clock does not reset on every behaviour swap

Upstream resets `PhaseStartedAt` inside the `Behavior` setter, so *any* swap anywhere restarts it.
Combined with visits of 1-15 minutes and phases of 15-360, the effect is that a bot which visits
anything never accrues enough phase time to roll at all - their lifecycle is close to dead for any
bot that travels.

Here, only a **lifecycle transition** resets it. An arrival handoff and a visit expiry preserve it,
so the phase clock measures what it claims to and an Idle-inclined bot eventually gets its Idle
phase. `PlayerBot.BehaviorChanges` counts brain changes separately, because the clock deliberately
cannot answer "has this bot done anything?".

### A busy behaviour defers its transition rather than losing it

A `Traveler` declines to be interrupted mid-walk, and on a real graph a Traveler is mid-walk much of
the time. A roller that simply skipped a busy bot would pass over the same bot for ever. When a
phase expires against a refusal, `PlayerBot.TransitionPending` remembers, and the roll happens the
moment the bot is free.

### There is no emote path

Upstream routes any line starting with `*` to `Mobile.Emote`. Here every line goes through `Say`
in the bot's own hue, and a `*` line is **rejected at load** and reported by `Bots.Chat` — spoken,
it would show its asterisks.

No shipped line starts with one, so this changes nothing today; the check is a guard for future
corpus edits. `emotes.txt` keeps its 10% ambient draw either way, because what is actually in that
file is the deliberate typo-noise (`asdf`, `oops wrong window`) its own header defends as
*"imperfection reads human"* — which is the good part, and is plain speech.

### The responder hooks OnSpeech, not OnThink

The party check lives in `OnThink` and belongs there: an invitation is *state*, sitting on the bot
waiting to be noticed. Speech is an *event*. ServUO delivers it through
`Mobile.DoSpeech` → `HandlesOnSpeech` → `OnSpeech`, which fires only when somebody actually speaks
near the bot — a **tighter** gate than `OnThink`'s sector test, not a looser one, and one that
cannot miss an utterance by falling between ticks.

**`PlayerBot.HandlesOnSpeech` is required, not defensive.** `BaseCreature.HandlesOnSpeech`
(`:4605-4615`) ANDs the AI's answer with `from.InRange(this, RangePerception)`, and `PlayerBot`'s
constructor passes a perception of **2** — so without the override a bot cannot hear you from three
tiles away, let alone the ten the name branch needs. `OldMarta` hit the identical trap; it is
recorded at `Scripts/Custom/Mobiles/README.md:76-85`.

Both overrides go on the **mobile**, not on `BotAI`. Seam 5 keeps `BotAI` thin so it can become a
`MeleeAI` or `MageAI` when combat lands, and hearing is not a property of how a bot fights.

### The bank crowd is a garrison AND a pull, and `life.crowds.bank` is both numbers

**This section used to say "a pull, not a garrison", and that was wrong about our own code.**
Upstream keeps bank crowds with `BankFixtures`: a spawner at every bank holding five permanent,
**lifecycle-exempt**, curve-exempt sitters, and its lifecycle only ever adds extras by teleporting a
rolled BankSitter to a uniformly random bank with **no occupancy check at all**. We have that too.
`BotPopulation` reads `bankCrowd = store.Life.FloorFor("bank")` and pins exactly that many
`BotRole.Fixed` sitters at every bank in the recipe - three each, four banks, twelve bots - so the
garrison is there at 05:00 as at 19:00, by construction, and measured at **exactly 3.00 standing at
all four banks in 449 of 449 samples**.

What is ours rather than theirs is the *second* use of the same number. The floor is also a weight
multiplier on an under-floor destination - bots *want* to go where the crowd is thin - capped at four
times, with **bots already routing there counting toward it** so one empty slot does not pull every
traveller in town. Nobody is placed, teleported or commandeered to satisfy it.

**One number doing two jobs is why the defect was invisible for so long.** `life.crowds.bank = 3`
sizes the garrison *and* sets the threshold the garrison is supposed to satisfy, so the two can only
agree - and they did not, because `BotCrowds.CountFor` matches `BankSitterBehavior.DestinationId` and
a seeded fixture had none: `PlayerBot.ApplySeed` forwarded `_seedStation` to a `CrafterBehavior` and a
`GathererBehavior` and to nothing else, and `BotPopulation` passed `null` where the bank slot's
station argument goes, so there was no token to forward. Twelve permanent sitters counted toward **no
destination's floor at all**, every bank read under 3 for ever, and **the 4x pull ran permanently on
top of a garrison that already met the floor** - a standing distortion of the whole destination roll,
against shops, taverns and inns that had no such multiplier. Both halves are fixed; see the
before-and-after table under *Later*.

Consequence still worth knowing: the *pull* is a tendency, not a guarantee - `Bots.Population`
reports `banks below floor` rather than asserting it. The *garrison* is a guarantee, and is the half
that actually keeps a bank looking busy.

**`BotCrowds.CountFor` now has a `Shopper` branch too**, which changes nothing measurable today
because `bots.json`'s `life.crowds` holds only `bank: 3`, so `FloorFor("shop")` is 0 and no shop
has a floor to be below. It is there because without it the count goes wrong *at the moment a bot
arrives*: the `Traveler` branch counts a bot routing to a shop and stops counting it the instant
the visit behaviour takes over, so a shop with a floor would have read "nobody there" about a bot
standing in its doorway. That is the same shape as the bank defect, and finding it the same way
would have cost another window.

### The Shopper walks

Upstream's is rotation-only, by explicit design - *"No movement = no wall-grinding"* - because it
leaned on a zone check to guarantee it was already in the right place. The tell is that their file
still carries `Home`, `HomeMap` and `VendorSpeakRange` fields that are written and never read:
vestiges of a walking version that was removed.

Ours walks between the shop's arrival points, because the hops are two or three tiles, they go
through `NavWalker` like every other walk here, and a shopper wedged behind a counter gets the
recovery ladder for free. What made movement dangerous for them is what steps 4a and 7b already
solved here.

**Except that for a long time it did not, for all 27 seeded ones — the other half of the
`SeedStation` defect.** `PlayerBot.ApplySeed` forwarded `_seedStation` to a `Crafter`, a `Gatherer`
and (since `701e7332`) a `BankSitter` and to nothing else, and `BotPopulation.cs:389` passed `null`
where the shop slot's station goes, so the spawn string carried no `SeedStation` token to forward.
`MoveToAnotherSpot` tests `DestinationId` first, so a seeded shopper paused and re-paused for its
whole visit on the arrival tile `SpawnPoint` chose — arrival #0 exactly, the shop slot's `Spread`
being 0. It is the same shape as the bank defect and the same two halves: a branch in `ApplySeed`,
and a token on the spawner.

**And wiring the seed alone would have fixed eleven shops of twenty-seven.** *Sixteen of the
thirty-two shop destinations have exactly one arrival*, and the browse gate was
`arrivals.Count < 2` — so those bots would have gone straight back to standing still, the same
symptom arrived at one step later. The gate is now `< 1`, because `Nav.TryPickArrival` **scatters**:
`NavArrivals.Scatter` offsets by `Custom.NavArrivalScatter` (2) around the chosen arrival and
validates the candidate for standing, so a one-arrival shop browses a 5×5 box. None of the sixteen
is `exact` or `exclusive` — the two flags that suppress the scatter — and `Eligible` drops an
arrival only when it is *both* exclusive and occupied, so a pick can never fall through to the
destination **centre**, which for a shop is as likely to be a counter as a floor.

Measured over two fifteen-minute windows: **Shopper walk steps per bot-minute 1.597 → 2.437**, idle
steps `0.00` in both — which is the right pair of answers, since a browse hop goes through
`NavWalker` and is booked as `walk`, so the fidget figure must *not* move. All 34 live shoppers
report `shopping at <id>` with zero null destinations, where every seeded one used to read a bare
`shopping`.

**One honest limit the measurement found.** At a *cramped* single-arrival shop the scatter can
still pin a bot: it validates with `CanSpawnMobile`, deliberately refuses a doorway, and falls back
to the exact tile after six attempts. Two bots at `trinsic-shop-alchemist` sat on `1847,2711` for a
whole window. The fix for those is a second authored arrival, not a wider scatter.

### The stock wander is off, and it WAS the fidget

Sean watched the bank and said they move around far too much. They did, and none of it was written
here: every idle tick in this layer is speech, an animation and a facing change, and not one of them
calls a movement primitive. The stepping was the engine's.

`BotAI.DoActionWander` stands aside only while `Commuting` (`BotAI.cs:48-58`), which is exactly the
case an *arrived* bot is not. So it fell through to `BaseAI.DoActionWander` ->
`WalkRandomInHome(2,2,1)` (`BaseAI.cs:1061-1067`, `:2509-2582`), on the AI timer, whose interval is
`CurrentSpeed` - and `BotMovement.Settle` drops that to `Mobile.WalkFoot`, 400ms. `WalkRandom`'s own
gate is `Utility.Random(16) <= 8`, about 56% (`:2241`), and `BaseCreature.CheckIdle` damps only 5% of
calls into a 15-25 second pause (`BaseCreature.cs:4754-4759`).

**Upstream has none of this layer at all**, because their bot is a `PlayerMobile` and there is no
`BaseAI` under it. Their idle is only what their behaviour code does: a BankSitter steps when it is
further than `HomeRadius = 1` from its spot and otherwise not at all
(`uo-offline BankSitterBehavior.cs:58, :586-617`), and a Shopper does not move, by explicit design -
*"does NOT path anywhere... No movement = no wall-grinding"* (`ShopperBehavior.cs:1-14`). We had
ported their texture faithfully and left the engine running underneath it.

Measured with `[BotSteps`, idle steps per bot-minute, two full 15-minute windows of ~61 bots, same
settings, `MeasurementProfile` off - and the "before" column is also the argument for the fix,
because one phase already had it:

| phase | A: before | B: idle fix | C: and mounts | |
| --- | --- | --- | --- | --- |
| Crafter | **0.00** | **0.00** | **0.00** | already pinned with `RangeHome 0` - the shape the others now copy, and unchanged by any of this |
| Shopper | 20.35 | **0.00** | **0.00** | no `Home` at all, so the untethered `WalkRandom` branch (`BaseAI.cs:2516, :2542`) |
| BankSitter | 25.97 | **0.41** | **0.16** | `RangeHome 2`, which is a licence to wander inside 2, not a tolerance |
| Traveler (lingering) | 24.16 | **0.00** | **0.00** | also untethered, and the worst of the three |

**11,925 idle steps become 108, and then 37** - and window C is the one to quote, because it is the
shipped configuration. Mounts did not cost the idle fix anything, which was not a given: a mounted
bot steps at 200ms rather than 400, so its AI thinks twice as often and every rate above would have
doubled had the wander still been running. Before: 101,546 steps total, of which 89,096 were a
Traveler walking a route and the rest were 3,167 BankSitter wander, 2,711 BankSitter drift-back,
4,838 Shopper and 1,209 lingering Traveler. After: 62 and 46.

**The BankSitter does not go to zero, and should not.** Its residual is the settle itself:
`PickScatteredHome` puts `Home` up to four tiles off the arrival, the engine walks the bot there once
per visit, and `DoMoveImpl`'s `SuccessAutoTurn` (`BaseAI.cs:2483-2496`) steps round an obstacle in a
direction that is not toward `Home` - which the census correctly calls `wander` because it cannot know
better. That is traffic on the way to a chosen spot, not fidgeting at one. (A two-minute reading of
the same window said 2.97, because every bot in it had just arrived and the settle walks had nothing
to average against. Short windows over-report this number.)

**Two side effects, both measured, neither expected.** The wander was not only ugly, it was in the
way:

- **Walk failures fell and walks rose.** 1.11 per 100 walks over 452 walks became **0.18 over 562** -
  more walking done, less of it failing, with no change to the graph or the walker. A bot shuffling
  off its arrival tile is a bot standing where the next arrival is aiming.

**And the cost of the same thing, which `[WalkAudit` found and the live numbers did not.** A bot that
holds its ground also holds a tile a probe wants. The full sweep went from 2509 walks with zero
failures to **2508 with two**, both `short-of-goal`, both Trinsic shop arrivals, and both because a
**fixture was standing on the arrival tile**: `trinsic-shop-tailor-2` had its staffed Tailor within
three tiles in **446 of 446 samples**, and `trinsic-shop-provisioner-6` a seeded Shopper. `[TileProbe`
clears the tiles themselves - `CanFit True`, all eight neighbour steps ALLOWED, nothing on them - and
the paths are `ratio 1.0`, so this is occupancy and nothing else. Before the fix those bots shuffled
continuously and a probe eventually slipped in; the wander was accidentally clearing doorways.

**This is the AUDIT failing, not the bots.** A live walker has two things the audit probe deliberately
does not: `NavArrivals.TryPick` prefers a free arrival, and `NavWalker.TryShiftWithinArrival` shifts
once to a free tile inside the arrival's own range. The live population in the same window took **one**
terminal failure in 474 walks. Worth knowing before reading a sweep as a regression - and worth
fixing at the arrival rather than by putting the fidget back, because two probe failures is a cheaper
price than 11,925 idle steps.
- **The bank crowds filled up.** `brit-bank` went from 0.91 bots standing to **1.94**, `brit-bank-east`
  from 0.45 to **1.63**, and the share of samples under the floor of 3 from 98% to 39%. Which means
  the standing-crowd shortfall was partly a SYMPTOM of the fidget rather than a weighting problem -
  worth knowing before anybody raises `life.crowds.bank`.

The lever is **`PlayerBot.CheckIdle`**, not `DoActionWander`: `CheckIdle` is the one thing
`DoActionWander` asks before it wanders and nothing else consults it, so overriding it leaves
herding, navpoints, waypoints and the combatant-facing tail alone. It is also the lever this tree
already proved - `NavWalkAudit`'s probe overrides exactly this, and `Navigation/README.md:760-762`
tabulates three measured variants.

Three consequences worth knowing:

- **`RangeHome` is not a tolerance and never was.** `SitterRange = 2` is gone; the slack is now
  `PlayerBot.IdleTolerance`, consulted by `CheckIdle`, and `bots.json` `life.idle.sitterTolerance`
  holds the number. A crafter sets it to **0** explicitly, because the tolerance a sitter wants is
  one tile and one tile is what a Smith cannot afford.
- **Off the spot, `CheckIdle` returns `false` rather than `base`**, so the walk-back is immediate and
  certain; `base` would pause one call in twenty and leave a shoved sitter standing where it was
  shoved to, which upstream's unconditional walk-back does not. The walk itself is still the
  engine's pin idiom (`BaseAI.cs:2573-2578`) - nothing here re-implements movement.
- **A sitter now gives up on a spot it cannot reach.** The walk-back is a straight-line `DoMove`, not
  a pathfind, and `PickScatteredHome` validates its tile for standing and occupancy but never for
  reachability - so a spot across a counter is one the bot walks into a wall to reach. That was
  survivable while the five-percent pause damped it. After 20 seconds off the spot it re-pins to
  where it actually stands. **The grind would have been invisible to `[BotSteps`** - a blocked
  `DoMove` takes no step - which is why it is guarded rather than waited for.

And the idle texture gains the half of upstream's we had not ported: the Shopper's 15%-per-tick
facing shift between hops (`ShopperBehavior.cs:113-122`) and the lingering Traveler's turn every 4-9
seconds (`TravelerBehavior.cs:2727-2732`). Both are turns, not steps - `Direction` is not `Location`,
and `[BotSteps` does not count them.

### `PickScatteredHome` is kept, and matters more here than there

A BankSitter settles a few tiles *off* the arrival point rather than on it. Upstream's reason was
that "every bot homes on the exact tile it arrived at and the crowd stacks" - cosmetic. Here it is
load-bearing: an uncontrolled `BaseCreature` cannot walk through another mobile
(`Movement.cs:411`), so sitters parked on the arrival points make those tiles unreachable for the
next arrival. Dropping it turned the bank into a traffic jam, visible as a flood of Sidestep and
Door recoveries in the walk probe.

Ours additionally passes `checkMobiles: true` when picking the spot, which upstream does not.

### Bots walk through crowds, and yield to players and stock NPCs

Upstream's rule is `PlayerBot.CheckShove => true` (`PlayerBot.cs:504-512`), with its reason in the
same comment: the engine's full-stamina shove rule "bounced every road-weary bot off the permanent
bank-plaza crowds forever - 500+ pacing events per soak". Ours is the same line. A real player
shoving a bot still pays the player rule - full stamina, minus ten (`Server/Mobile.cs:3516-3550`).

The seam is that a `BaseCreature` is never asked. `BaseCreature.OnMoveOver`
(`BaseCreature.cs:4546-4554`) and `PlayerMobile.OnMoveOver` (`PlayerMobile.cs:3487-3494`) both refuse
an uncontrolled creature before the mover's `CheckShove` is consulted, which is why Adelyn the
miner stood one tile from the forge for the whole work probe with Elara on the station tile. So
the **shoved** side consents: every bot and every daily-life actor routes a bot mover through
`BotShove.OnMoveOver`, and there it is the free pass.

**This deviation is now half retired, with the evidence it asked for.** It said a bot cannot push a
real player or a stock NPC, and that a wedged walker would name its blocker "so the case can be
revisited with evidence". The evidence arrived and it was decisive, so the stock-NPC half is gone
and the player half stays.

Getting it needed a better instrument first. `DescribeBlocker` named a mobile on the **goal** tile
and otherwise listed whatever was near the walker, and a wedge is almost never caused by somebody
standing on a goal twelve tiles away - it is caused by somebody on the tile the walker is trying to
step onto right now. Over one run of 133 rung entries, exactly **one** said "blocked by" (a dog),
while 75 of the 76 that carried a mobile list at all had a stock NPC or an animal within two tiles:
"a vendor was near the wedge" was well supported and "a vendor caused it" was not.

`NavWalker` now asks the engine for the first direction of the path from here to the goal and names
who is standing on **that** tile - the one `Mobile.Move` hands to an occupant's `OnMoveOver`, and
therefore the only one whose occupant can refuse the step. Over a 30-minute window of 185 rung
entries: **40 had a mobile on the next-step tile, and 36 of the 40 were mobiles a bot may not
push.** `Bots.Population` carries the running count.

So `Scripts/Mobiles/Normal/BaseCreature.cs` gains a three-line guard routing a `PlayerBot` mover
through `BotShove` before its own branch - the first `.cs` edit in this tree, logged as entry 5 in
`MODIFICATIONS.md` with why no `Custom/`-side approach works and what it costs. Upstream needs no
engine edit for this at all, because their `PlayerBot` is a `PlayerMobile` and falls through to
`CheckShove => true` on its own (`CustomBots/PlayerBot.cs:587-595`); ours is a `BaseCreature`, and
the mobile being shoved is a stock creature this shard does not construct, so there is nothing of
ours to override.

**What a bot still cannot push**: a real player. `PlayerMobile.OnMoveOver` is untouched, so a bot
yields to a player and a player shoving a bot still pays the engine's full-stamina rule. That half
of the deviation was always the right half.

**And the rule is now symmetric, which it was not.** Everything above is about a bot as the
*mover*. A bot as the *shoved* side refused every uncontrolled creature, including the daily-life
actors it may itself walk through - not by anybody's decision, but because `BaseCreature.OnMoveOver`
turns them all away and `PlayerBot` inherits that. So the town jammed in one direction only: a GG
shopkeeper walking home at dusk stopped dead at a bot standing in its doorway, while the same bot
would have walked straight through the shopkeeper.

`BotShove.OnMoveOver` now answers the reverse case too, and **the pass is exactly as wide as the one
going the other way**: `NavWalkFailures.Shovable` - a `PlayerBot` or an `IDailyLifeActor` - decides
both directions, so the rule and the measurement cannot disagree about which bucket a mobile is in.
No new upstream edit was needed, because `BaseCreature.OnMoveOver` passes the *stock* creature as
the shoved side and the new branch cannot fire from there.

**It stops at `IDailyLifeActor` on purpose.** A stock vendor in a doorway still jams a bot, and a
bot still jams a stock vendor - which is the row above, held open deliberately, with window D's six
unshovable rung entries (`Jeanette (InnKeeper) at 1457,1526` and its run) as the evidence still
being gathered. Widening the symmetric pass to every uncontrolled creature would settle that
question sideways, and would also go further than uo-offline, whose `PlayerBot` is a `PlayerMobile`
and therefore blocks uncontrolled creatures outright.

### A station is a place, and the stand tile is chosen on arrival

Upstream's arrival is `DriftArriveRange` 2 (`TravelerBehavior.cs:218`): the Traveler drifts toward
a random arrival spot and counts itself arrived within two tiles, or when the drift stalls, and
the smith becomes a Crafter wherever it stands (`:2561-2577`, `CrafterBehavior.cs:124-128`). That
works for them because their production is an illusion (`CrafterProduction.cs:2-4`); ours runs
the real craft system, and `DefBlacksmithy.CanCraft` refuses anything not within two tiles of both
an anvil and a forge with line of sight.

So the two halves are split the way the data now says. An arrival carries a **range**
(`NavArrival.Range`; the forge arrivals author 2, everything else the tile), the walker accepts
arrival from inside it, and `BotWorkSites.TryPickStandTile` then chooses the tile for what the
bot came to do: every standable tile inside the arrivals' ranges with the trade's fixtures in
crafting reach - a smith needs the anvil and the forge, a miner only the forge - ranked **free
first, nearest second**. Nothing free, and the bot takes an occupied tile: the shove makes the
step legal, and two smiths on one tile is what a packed forge looked like. Only a reach with **no
standable tile at all** sends the bot to another station of its kind in the same town. There is no
waiting state. A `CanCraft` refusal (1044267) refuses that tile for the visit and chooses again,
because the sweep has no line of sight and the engine does.

Delivery follows the same logic. Upstream's `DeliverMaterials` runs from the arrival handoff even
after a drift that stalled, and finds its buyer within twelve tiles of wherever the gatherer stands
(`BotEconomy.cs:298`). Ours fires on arrival, and now also when a laden Traveler's walk **ends
short** within the buyer's reach of the destination - a miner whose last step was refused is
standing in the smithy with the ore on its back, and rolling another destination with it was the
one thing no player ever did. The work probe's expectation is the packed forge: the first smith on
the station tile, the miner delivering to whichever smith is at the bench, and a second smith
arriving from town to settle on a free tile of its own and make something from it.

### The last leg aims at a tile it can reach, where theirs aims at the waypoint

Upstream's final leg never targets the arrival coordinate at all. It targets the approach
**waypoint** plus a random +/-5 jitter and calls itself arrived at `FinalLegArrivalRange` **8**
(`TravelerBehavior.cs:70-77`, `:1942-1963`), and its own comment says why in our words: *"The
destination tile may sit inside a building ... a bot routing from the street can't reach the inner
tile and would grind the outer wall."* The last few tiles are a bounded cosmetic drift that is
allowed to fail (`DriftArriveRange` 2, six seconds, `:2042-2094`). They have no standability sweep
in the arrival path anywhere.

**We cannot take their 8, and the reason is the row above.** A station is a place *and* the stand
tile has to be within 2 of both anvil and forge or `DefBlacksmithy.CanCraft` refuses. An arrival
range is load-bearing here and decorative there, so eight tiles of latitude would put a smith
outside the shop it came to work in.

So the walker keeps the authored range and buys reachability instead: `NavWalker` retargets the
last hop onto a tile that is standable **and** pathable inside that range, re-asking as the bot
halves its distance. See the Navigation README, *The last hop is the one nobody audited* - the
measurement is 7 of 17 terminal failures in one window, every one a bot three tiles from a range-2
arrival.

### The walker has uo-offline's frozen watchdog

`NavWalker`'s ladder resets on progress, and a `SkipWaypoint` that succeeds is progress: it aims at
the next waypoint and the "closer than ever" test starts over. A bot that cannot take a single step
therefore walks its whole route with its index - Repath, Sidestep, Door, Skip, Repath, Sidestep,
Door, Skip - and never reaches Teleport. A miner rooted at the mine face after its shift did exactly
that for the length of a work probe, at a tile the engine could path out of when asked directly.

uo-offline's answer is `CheckFrozenWatchdog` (`TravelerBehavior.cs:2963-3033`): a bot that has not
moved `FrozenMoveTiles` (2) in `FrozenLimit` (60 s) is rooted whatever the plan says, and the spot
itself is the problem. Ours is the same clock on the walker, with one number re-derived: their 60 s
outlasts their whole ladder, whose rungs are seconds apart, but ours are 20 s apart, so one pass
through Repath, Sidestep, Door and Skip is 80 s and a 60 s window pre-empted a Skip that would have
worked - three teleports in one life probe. The window is **six hop timeouts**, 120 s: the ladder's
whole turn, the Skip's own hop, and one more. Two tiles within that, and the bot goes straight to
the top rung, which keeps its own rule about players watching. Their two stages (repick, then
rescue) collapse to one here because the ladder's first rung is already a re-plan.

### A home is a town tag, not a `City` column

Upstream gives every destination a `City` field and every bot a `HomeCity`, rolled at creation and
weighted by how many destinations each city has (`BotEconomy.cs:34-83`); the roll then weighs home
destinations 2.5× (`DestinationCatalog.cs:216-223`), and that is the whole of what a home does -
no distance term, no "go home" rule, no fallback for a resident whose town has no station of its
kind. All of that is mirrored exactly: `PlayerBot.HomeTown`, `BotHomeTowns.Roll`, and the
`homeBias` multiplier in `BotDestinations.Pick`, applied to the haul roll as theirs is.

The one seam is where the town lives. Ours has no `City` column, because the geography of this
graph is **tags** - the adopt step writes `britain` and `trinsic`, the editor writes whatever an
author types - and a second column saying the same thing would drift from the first. So a town is
a tag named in `bots.json` (`destinations.towns`), and a destination belongs to it by carrying the
tag. Our own Britain records gained a `britain` tag for this; the mines are tagged too, as
upstream's Britain `MiningSpot` carries `City: Britain`.

What a resident does when its home has no station of its kind is upstream's answer, unchanged: it
rolls like anybody else. A Trinsic-home Miner sees two mines, both Britain's, and works them; a
Trinsic-home Smith sees `trinsic-forge` at 8 × 2.5 and mostly works at home. `Bots.Population`
counts those residents (`no station at home:`) as a report, not a rule.

### Trinsic's bank weighed what Britain's did, and the hand-over failed three runs in five

**No commit broke the hand-over.** The work probe was reported failing — *"the miner is still
carrying its load - it never reached a delivery point... miner ended as Traveler, 37 units mined"* —
and the first job was to find the regression. There is not one:

- All three suspects (the gatherer dismount/re-mount, the `CheckIdle` pin, the Shopper seed) are
  **ancestors of the commit whose own message records the chain passing**, hand-over included.
- Of the six commits since, exactly one touches `Scripts/Custom/Bots/` at all, and it touches the
  measurement profile, `[BotPace` and `BotSession` — nothing on the delivery path.
- It **passes on that build**, twice: standalone, and the full chain reading *"Bot smoke chain
  complete"* with 43 mined and 25 delivered.

It is a standing intermittent defect, and running the probe five times says how intermittent:
**three failures in five**, every one of them Sean's message verbatim.

| work probe, one boot per run | PASS | FAIL |
| --- | --- | --- |
| before | 2 | **3** |
| after, non-nearest at 0.1 | 4 | 1 |
| after, non-nearest at 0.01 | **6** | **0** |

#### What was actually wrong

`HaulWeightFor` has four branches and the nearest-bench rule was written into one of them. That rule
exists because of a measured failure — *"a miner that filled its pack at `brit-mine-north` walked 255
tiles to `britain-forge` past `brit-forge` 36 tiles away, both staffed, both weighing the same"* — and
the fix, nearest staffed bench 20.0 against any other staffed bench 0.5, went into the **staffed**
branch only. The unstaffed-station branch (9.0), the bank branch (2.0) and the everything-else branch
(0.02) kept no distance term.

**A forge survived that, and a bank did not.** `BotWorkSites.IsWorkType` returns true for `forge`, so
a forge candidate already went through `DistanceFactor` *and* the route check. **A bank is not a work
type**, so neither ever touched one. With nobody at a bench — which is where the unstaffed branch
lives — every bank on the facet weighed a flat 2.0:

| from `brit-mine-north` (1449,1521) | tiles | walking at 0.4 s/tile | weight, before |
| --- | --- | --- | --- |
| `brit-bank` | 169 | ~67 s | 2.00 |
| `brit-bank-east` | 202 | ~80 s | 2.00 |
| `trinsic-bank-2` | 1167 | ~466 s | **2.00** |
| `trinsic-bank` | 1307 | **~522 s** | **2.00** |

The probe leaves about 300 seconds after the shift ends. A haul that rolled a Trinsic bank was
**structurally unable to arrive**, and it is a quarter of the bank weight. Home bias makes it worse
rather than better: a Trinsic-resident miner multiplies its home town's bank by 2.5 and sets off
across the map with the ore.

**And 0.02 is not the same as never.** Fifty-seven of this graph's sixty-five destinations are
neither a forge nor a bank. At 0.02 apiece they aggregate to **1.14 against the nearest staffed
bench's 20.0 — 5.4% per roll**, in the *normal* staffed case, that a miner with a pack full of ore
sets off for a tavern. Thirty-two of the fifty-seven are shops, where the 0.8 handoff then commits it
to a two-to-six minute shopping visit with the ore still on its back. The intent was always
"effectively never" — the method's own header says hauling is a different errand from living in the
town — and a small weight times a large number of candidates is not "effectively never", it is a slow
leak.

#### The fix is one sentence in all four branches: while hauling, the errand is the delivery

- A non-delivery destination weighs **0**, not 0.02. It cannot strand a bot — four forges and four
  banks are always positive — and the caller reads any value `>= 0` as a haul weight, so zero stays
  distinct from the `-1.0` that means "not hauling, use the ordinary table".
- An unstaffed station and a bank both get the nearest rule the staffed branch already had.
- Non-nearest gets **0.01** where the staffed branch uses 0.5, and the asymmetry is the point:
  another *staffed* bench is a real second choice because somebody is there working, while another
  *empty* forge is just a further-away version of the empty one nearby. It also keeps the 2.5 home
  bias from lifting a far one back over a near one. **0.01 was measured, not picked** - see below.
  It is not zero because the nearest bench could be excluded or unroutable, and a laden bot with no
  positive candidate anywhere would have nothing to walk to at all: an escape hatch, not an option.

**And a handoff no longer commits a bot that is still carrying.** `TryDeliver` runs before the
handoff roll and clears `HaulPending` whenever it reaches a delivery point, so a flag still set there
means the hand-over did **not** happen — most often because the bot settled a tile outside the
arrival's apron, which the stand-tile sweep and the walker's free-tile shift both do. The roll would
then commit it to the place anyway: a forge is 0.95, so a laden miner that stopped one tile short of
the anvil had a nineteen-in-twenty chance of becoming a Crafter with the ore still in its pack — and
`BotSession` refuses to log out a bot that is hauling, so it would carry it until something else
moved it.

#### The roll is in the log now, and was not

`LogChoice` filtered to **work sites** — a mine or a lumber camp. A laden gatherer rolls forges and
banks, so the one decision that ends an errand logged nothing at all, and "where did the ore go" could
only be answered by guessing. It now reports a haul roll, on the console as well as in the event log,
because a haul that goes to the wrong town is diagnosed from a probe run's console days later and
`botlog.json` only exists while the live map is on. One line per completed shift:

```
Ivor is hauling to 'brit-forge', 34 tile(s) away: brit-bank w0.05, *brit-forge w8.15 80t,
brit-forge-south w0.29 337t, brit-forge-south-2 w0.30 324t, trinsic-bank w0.02, trinsic-forge w0.03
```

**What the arithmetic does not do is predict three-in-five.** The terms above explain the failure
*mode* and each is worth removing on its own, but they do not add up to the observed rate, so the
fix is carried by the measurement rather than by the model. The likeliest remaining ingredient is a
race the probe has with itself — whether its smith has clocked in by the time the 120-second shift
ends, which decides whether the roll takes the staffed branch or the unstaffed one — and the haul log
is now what would settle that on the next failure rather than another afternoon.

### An empty bench weighs what the bank weighs

Upstream's haul roll gives *every* station of the right trade 9.0 against the bank's 2.0 and never
asks whether anybody is there (`DestinationCatalog.cs:191-206`); it does not have to, because its
`[GenerateBots` pins one `Crafter:Smith` at every forge arrival point, so every forge is staffed by
construction. Ours are not, which is why the 20/9 split already existed here (see *The hand-over*
below). The Trinsic adopt found the half of that rule still missing: three unstaffed forges at 9
each outbid the one staffed forge at 20 about one haul in three, and the work probe became a
lottery about which forge the ore went to.

Now, once **any** bench of the trade is staffed, an unstaffed one weighs **0.02** - the bank's
weight, because that is what it is: a load in a bank box with extra steps. With nobody at any
bench the 9/2 upstream split stands. "Staffed" is read live on every roll from
`CrafterBehavior.IsAtStation` - a crafter settled at its bench and not `Blocked`, so a smith still
walking to the forge does not count - and never from data. A Trinsic forge is a delivery point the
moment somebody works it and stops being one when they leave.

### The probes pin home and derive their window

Upstream has no probes. Ours make two choices a shard would not: the life probe's bots are all
residents of the town they spawn in, because the roller is under test and a Trinsic resident
walking home from Britain says nothing about phases; and its window is derived from the graph at
every run rather than written down - the longest route a probe bot could roll, at the pace the
engine assigns it on foot, plus the longest linger, the longest phase clamp and two roller passes.
The result line names the window and the route that set it, so an adopt that lengthens the road
shows up in `[CoreSmoke` rather than as a slower chain nobody can explain. The work probe puts its
smith at the forge nearest the mine by road and asserts that the ore reached **that** smith.

### A fixture's home town is its station's town

Upstream rolls `HomeCity` in the constructor and never touches it again — its `ReinitializeAsClass`
re-derives a pinned crafter's class, gear and skills and deliberately leaves home alone
(`PlayerBot.cs:447-457`). So a `Crafter:Smith` pinned at a Vesper forge may be a Yew resident who
will never once go home, and whose 2.5× home bias pulls at a town it is not standing in.

Here the generator writes the station's town onto every **fixed-role** record and `SeedHome` applies
it, so the smith at `trinsic-forge` is a Trinsic resident. Only fixed roles: a lifecycle seed keeps
its birth roll, because a traveller genuinely should be from somewhere else and that is what makes
the roads busy. The visible effect is in `Bots.Population` — fixtures leave the `no station at home:`
list, and the `byHome` census stops reading as though the staffed benches were staffed by tourists.

### The population is a file, not a pile of world items

The largest deviation of the population session, and it has its own section — see **Population**
above. In one line: upstream mints `PlayerBotSpawner` Items and the world save becomes the record;
here the recipe writes `GG_BotPop.xml` and `[GG_Reimport` turns it into spawners, so the population
is in git, is drawn in the editor, and cannot stack.

### A seed is properties on the bot, not a spawner subclass

Also the population session's, also with its own section. In one line: XmlSpawner calls
`OnAfterSpawn` **before** it applies the spawn string (`XmlSpawner2.cs:9337` then `:9344`), so
upstream's `Spawner is FixedRoleBotSpawner` test cannot be transplanted and the role travels as a
`[CommandProperty]` instead.

### The Z window climbs 5, not 4

`NavWalker.MaxClimb` is 5; uo-offline's `Walkable.MaxClimb` (`Nav/Walkable.cs:27`) is 4. The
difference is a wooden ramp, and it is derived from the engine rather than tuned:

- A bridge foot is a `Bridge`-flagged ramp of height 5. A mobile stands on it at **half** its height
  (`ItemData.CalcHeight`, `Server/TileData.cs`): ramp at Z 10, stand at 12.
- The engine's step budget is measured from its **full** height. `MovementImpl.GetStartZ` takes
  `tile.Z + Height` as the top of what you stand on (15), and `Check` allows a step onto anything
  whose bottom is at most `startTop + StepHeight`, 15 + 2 (`Scripts/Services/Pathing/Movement.cs`).
  The next ramp at Z 15 (stand 17) qualifies.
- Stand-to-stand, that is a climb of **5**. Every one of Trinsic's canal bridges rises 2 → 7 → 10 or
  12 → 17 → 22 that way, and at 4 the flood stopped at the foot of all four; the adopt of the road
  south reported them as "no walkable road" with both ends standable.

uo-offline lives with the 4. Their `Nav/DistanceField.cs:100-106` says the approximation "rejects
legs the game allows (Vesper canal bridges, dock ramps, low arches), which made the audit cry
BLOCKED on edges bots walk every day", and their `[auditedges` floods with the real movement engine
instead. We take the other exit: the window is a candidate generator, `MovementPath` is the verdict,
and NavAdopt paths every hop it proposes (see *Z resolution* below). One Z looser than theirs is the
safe direction for a generator, because a hop the engine refuses is caught and named rather than
saved.

## Severed seams

Places where this layer deliberately stops short. Each is marked with a one-line comment at the
site naming the session that restores it, so none of them has to be rediscovered.

**Seams 1 and 3 were paid off in session 5** and are kept in the table, struck through, because a
reader of an older diff will find their markers and should not go looking for what replaced them.

| # | Site | Severed as | Restored by |
| --- | --- | --- | --- |
| ~~1~~ | `BotClassHelper.StationFor` | ~~Not ported. It returned upstream's `DestinationType` enum~~ | **Done (7e)** — returns a `BotStation`: our nav `type` token plus a tag, because 13 of 27 destinations are `shop` and type alone cannot tell a loom from a bakery. `CrafterTypeHelper.StationFor` did **not** come back: the subtypes are real classes now, so the station belongs to the class |
| 2 | `EquipmentTable.AddMarkedRune` | A blank `RecallRune`. Upstream marked it from its `DestinationCatalog`; nothing routes yet, and a rune marked to somewhere no bot can walk is a lie in the pack | Travel session — wires to `Nav.Destinations(...)` |
| ~~3~~ | `EquipmentTable.SeedCrafterStarterProps`, `EquipCrafterTool` | ~~Empty. Both read `CrafterProfiles`~~ | **Done (7e)**, minus the gold in upstream's `StarterProps` — seeding a purse for a purse system that does not exist would be inventing an economy one item at a time. That half is 7f |
| 4 | `EquipmentTable` → `BotItemFactory` | **Not severed** — pulled in as a leaf, since it is self-contained and the outfit roller cannot work without it | n/a |
| 5 | **`BotAI`'s base class** | `: VendorAI` — the right stand-aside for a bot that only stands still | Combat session — becomes `MeleeAI`, `MageAI` or a purpose-built `BaseAI` |
| 6 | `BankSitterBehavior` Hawker's **WTS** half | Only `wtb`. Upstream's hawker shouts a line `BotShop` builds from a real item in its pack, so "WTS GM halberd 5k" means there *is* one and 5k buys it. A WTS from a bot holding nothing is the lie this system exists not to tell | Economy session, with `BotShop` |
| 7 | `BankSitterBehavior`'s **three macro roles** | `ResistMacro`, `HidingMacro` and `StealthMacro` roll as `Regular`. Their **weights are kept** in `RollRole` behind markers, so restoring them is deleting three redirects rather than re-deriving upstream's distribution | Combat session, which brings spellcasting and `Hidden` toggling |
| 8 | Gossip, and `PlayerBotBehavior`'s `GossipShare` branch | A marker comment. `Gossip/` is copied but never scanned, and its `{actor}`/`{other}`/`{when}` tokens are registered unwired — so the lines are doubly unreachable | Event-journal session, with `BotEventJournal` |
| 9 | `friend_greet` and `BotSocialGraph` | Not wired. `{name}` **resolves**, but nothing decides that two bots are friends, so no path picks the category | Social session |
| 10 | **Fisherman** — `CrafterProfiles` has no entry, `StationFor` answers `dock`, and no `dock` destination exists | Reported, not hidden: `Bots.Work` **fails** with "Fisherman works 'dock' and the graph has none". Its production is not a craft at all — upstream drove `Fishing.System`, walking to the water's edge and casting only when open water was directly adjacent, which needs their `IsWet` / `HasStandableStatic` scan to tell a pier from the sea | Dock session. Britain's waterfront is **The Oaken Oar** (1424,1747, the dockside tavern) and **Customs** (1480,1746, on the docks) |
| 11 | The gatherer's **stable round trip** | The beast is deleted on delivery — upstream's own no-stables-in-range path. `AnimalTrainer.EndStable` hard-codes a 30gp fee from pack or bank, `DoClaim` is private, and there is no `stables` destination to walk to | Economy session (7f): add their Britain Stables (1393,1645, just outside our west edge), the fee, and the detour |
| 12 | The **gold half of the hand-over** | The load moves, the coin does not. Upstream paid 2–4gp a unit from the crafter's purse and refused the sale when it was broke; the crafter's three-minute counter restock went with it | Economy session (7f) |
| 13 | A gatherer **under attack** | Downs tools and travels. Upstream swapped to a defender `AdventurerBehavior` — "the tool is a real axe" — and there is no Adventurer here yet | Combat session |

**Seam 5 is a discipline, not a stub.** `BotAI.cs` is the only file in the tree permitted to name
`VendorAI` — no cast, no type test, no call to a `VendorAI` member anywhere else. `PlayerBot`
reaches its AI only through `ForcedAI` and the `Commuting` flag. Keep `BotAI` thin: anything that
is really bot logic belongs on `PlayerBot` or in a behaviour, where it survives the swap.

## The engine writes skills behind you

`ApplySkills` zeroes every skill before laying down the template, and that is not defensive tidying.

`BaseCreature.ChangeAIType` stamps `Focus` (2–20) and `DetectHidden` (10 or 60) onto every
non-vendor creature when the pet-training system is on (`BaseCreature.cs:625-632`), and
`PetTrainingHelper.Enabled` is `Core.TOL` — so on this EJ shard it is always on. That is the pet
system deciding a bot is a trainable animal. It is wrong in flavour, and it silently spent up to 80
points of the skill budget: the boot audit caught it as four classes over cap on the first run.

Clearing beats subtracting the two known skills, because any future engine change or upstream merge
lands in the same trap.

## The engine does not refuse a double-equip, it just tells you about it

`Mobile.AddItem` (`Server/Mobile.cs:6779`) notices that a layer is taken, writes `LayerConflict.log`
and a red console line — **and then equips the item anyway**, falling through to `m_Items.Add(item)`
at `:6807`. Nothing is dropped and nothing is refused. Both items sit on the layer,
`FindItemOnLayer` returns whichever is first in list order, and the other is an invisible ghost that
still carries weight and resistances and still drops on the corpse. (`DailyLifeWatchman.cs:26` says
the engine "drops one". It does not.)

The log had **107 records — 92 of them bots**, in three families:

| n | offending | equipped | layer | why |
| --- | --- | --- | --- | --- |
| 55 | `FullApron` | `Doublet` | MiddleTorso | the apron roll did not know what `CommonerUp` had just put on |
| 32 | a shield | `Halberd` / `BattleAxe` / `ExecutionersAxe` | TwoHanded | weapon and shield rolled independently |
| 5 | `HalfApron` | `HalfApron` | Waist | a guard that tested the wrong layer, on classes it excluded |

**Everything now goes through `BaseCreature.SetWearable`** (`:5706`), which `PlayerBot` already
inherited: it runs `CheckEquip` and `PackItem`s the loser instead of ghosting it. `EquipmentTable`'s
`Add` and `AddArmor` carried about thirty call sites straight to `AddItem`; they carry them here
instead. Stock `BaseVendor.InitOutfit` dresses every vendor the same way.

That is also the only thing that knows a shield and a halberd cannot share a bot, because
**`Layer` is not knowable from a `Type`**: clothing takes it as a constructor argument
(`BaseClothing.cs:846`), and armour and weapons read it from tiledata at `(Layer)ItemData.Quality`
(`BaseArmor.cs:2373`, `BaseWeapon.cs:5167`) from an ItemID that is itself a constructor literal.
There is no table to pre-check against, and `BotItemFactory` builds by reflection where the ItemID
is invisible. **Construct, then ask.**

**The three rolls are fixed at source as well**, and that is not redundancy. `SetWearable` stops a
conflict becoming a ghost; the rolls stop the outfit quietly losing the piece it rolled. A bot that
rolled an apron and a doublet should end up in one of them *on purpose*, not in whichever the engine
happened to accept first. So `AddApron` picks the apron that fits — `HalfApron` is `Layer.Waist`, so
the aproned look survives a taken torso — the accessory apron asks about `Layer.Waist` instead of
`Layer.OuterTorso` and stops excluding the gatherers who already have one, and a shield is rolled
only when `HasFreeHand()`, which the comment beside it always claimed and nothing enforced.

`[BotSmoke` asserts the file does not grow across its run. That is the only assertion available: a
conflict leaves **no trace on the bot** to inspect afterwards, because both items are equipped and
one of them is simply invisible. Seventeen classes at Grandmaster, a sixty-bot population fill and
the full probe chain now leave `LayerConflict.log` byte-identical.

## Equipment is still T2A-flavoured

`EquipmentTable.cs` is ported as-is, and its tables are the ones upstream curated for 1998-99 — no
wakizashi, pre-AOS magic tiers, era-sized reagent stashes. **Every item type in it exists in this
tree**, so nothing is broken by that; it is a flavour mismatch, not a compatibility one.

Rebalancing it for EJ is a deliberate later data pass, and the right shape for that pass is to lift
these tables out into `Data/Custom/` rather than to edit 2,000 lines of C# in place.

## The behaviour tick

One `Timer` at `Custom.BotTickSeconds` (2s), started from `BotSystem.Initialize`. It is the first
caller `PlayerBotBehavior.Tick` has ever had.

**It does not move anything.** `NavWalker` has driven movement on its own shared timer since step
4a and still does. The tick makes decisions and watches for stranded walkers.

**`LiveRegistry` is not a bot registry.** It also holds `DailyLifeTownsfolk`, `DailyLifePatron`,
`DailyLifeWatchman`, the six `GG*` vendors and `OldMarta` — today those *outnumber* the bots. The
tick filters with `as PlayerBot` and skips a null; a hard cast would throw on Perrin and take every
bot's brain down with it.

**Not `PlayerBot.OnThink`.** That is where the party check lives, and correctly — an invitation only
arrives when somebody is standing there. But `OnThink` runs off the AI timer, and
`PlayerRangeSensitive` stops that entirely when no player is in the sector
(`BaseAI.cs:3072-3082`), so a bot would stop deciding the moment nobody was watching. A shared
timer is immune, which is why `NavWalker` has one too.

`Custom.BotPlansPerTick` budgets **planning**, not ticking: ticking a bot is a switch on an enum,
while picking a destination and building a route walks the graph.

## Travelling

`TravelerBehavior` is a three-state machine — choose, walk, linger — and the feet are not in it.
Upstream's is 3,216 lines because it also owns leg walking, stuck recovery, magic travel and the
handoff to eight other behaviours; almost none of that is this class's job here.

It follows `DailyLifeTownsfolk`, **not** `ShopScheduleSystem`: `PlayerBot` already clears `Home` and
`RangeHome` in its constructor, so `KeepHomeAligned` stays false and nothing restores `Home`
afterwards.

Two details that are easy to get wrong:

- **`forMobile` matters.** `Nav.TryRouteFrom(..., bot, ...)` ends the route on a *picked arrival
  point* rather than the destination's centre tile. With five bots converging that is the
  difference between arriving beside each other and all aiming at one tile.
- **The walker instance is reused, not discarded.** `Follow` calls `Stop` internally so re-following
  is safe, and a walker that survives the journey carries its recovery history with it. Discarding
  it on every destination change threw the rung counts away — which is exactly how the first walk
  probe reported zero rungs for bots that had visibly climbed the ladder.

**Linger is always bounded.** Upstream had a `Wait` arrival style meaning "indefinitely, until the
lifecycle moves it"; it parked 40% of arrivals for entire sessions and made their status page read
as a stuck-bot epidemic. There is no lifecycle here yet to move anyone on, which would make an
unbounded linger permanent.

### The watchdog, and why the tick exists at all

**`NavWalker` does not always call `Arrived`.** Its `Tick` calls `Stop()` — not `Finish()` — when the
mobile is deleted or lands off-facet, and `DriveAll` stops a faulting walker the same way. A
Traveler that waited only on the callback would sit in `Walking` for ever with `Commuting` still
true and the stock wander suppressed: precisely the frozen-NPC failure the recovery ladder removed
last session, reintroduced one layer up.

So the tick watches for `Walking && !walker.Active` and treats it as a failed journey.
`Bots.Population` counts them.

## The lifecycle

When a bot's phase expires it rolls a new behaviour weighted by its own personality. The roller
rides `BotTickManager`'s existing pass over `LiveRegistry` on a slower accumulator
(`Custom.BotLifecycleSeconds`, 60s) - one scan, two cadences.

**Two mechanisms, not one**, and this is upstream's design rather than an invention:

| | chooses | when |
| --- | --- | --- |
| **The lifecycle roll** | behaviours a bot can be *anywhere*: `Traveler`, `Idle` | its phase expires |
| **The arrival handoff** | behaviours that only make sense *somewhere*: `BankSitter`, `Shopper` | it arrives there |

So `BankSitter` and `Shopper` are **not** roll targets. Rolling one out of nowhere would teleport the
concept - a bank sitter sitting in a field. `AdventurerTendency` has no target yet and simply does
not participate; the roll renormalises over what is registered, so that weight starts meaning
something the session an Adventurer lands.

The roll floors each weight at a small epsilon **first**, then halves the current behaviour's, so it
leans toward change. That order is upstream's and reversing it silently changes the distribution.
(Their doc comment claims it "boosts non-current weights"; the code halves the current one. The code
is right.)

**Not every arrival commits.** `handoff` in `bots.json` is the chance that arriving somewhere turns
into staying: 40% at a bank, 80% at a shop. A place where every arrival stops is a queue; the
passers-through are what make a bank look busy. A declined *shop* arrival leaves at once - a bot
standing in a smithy doing nothing makes no sense - while a declined *bank* arrival may linger.

**Phase length is the bot's own.** `BotPersonality.AveragePhaseDuration` is rolled once per bot
(15-360 minutes, halved by Restless, doubled by Homebody) and used as the exact threshold every
phase - it is not an average, despite the name. `bots.json` `phases` entries are **optional clamps
only**, and production ships none.

## The standing crowd

`crowds` in `bots.json` says how many bots each destination of a type wants:

```json
"crowds": { "bank": 3 }
```

An under-floor destination has its weight multiplied by `1 + min(shortfall, 3)` - capped, so a large
floor cannot make one bank the only place anybody goes. **Bots already routing there count toward
the floor.** Validated at load: a floor larger than the destination's authored arrival points warns
(`brit-bank` has four).

### Z resolution: a translated piece

`NavWalker.ResolveZ` answers "what Z would a mobile stand at on this tile", and every flood, audit
and scout we own goes through it. It used to probe the hint Z once and fall back to
`map.GetAverageZ`:

```
if (map.CanFit(x, y, hintZ, ...)) return hintZ;
return map.GetAverageZ(x, y);
```

Correct on open ground, wrong on anything built. **A bridge deck is a chain of statics whose Z
changes tile by tile**, so the tile after a Z 6 deck tile is at 7, the single probe at 6 fails, and
the answer comes back as the river bed underneath. The Britain-Trinsic crossing was unwalkable to
every tool we have for that reason alone.

**Translated from uo-offline's `CustomBots/Nav/Walkable.cs`**, numbers included:

- `MaxClimb = 4`, `MaxDrop = 20` (`Walkable.cs:27-28`) - asymmetric, because UO lets a mobile climb
  a little and drop a lot.
- `TryFindStandZ` (`Walkable.cs:118-140`) searches that window around the reference Z, nearest
  first, instead of probing one value.
- `CanStep` (`Walkable.cs:143-159`) hands the resolved Z on to the next step, which is what lets a
  flood follow a surface rather than a plane.
- `TryFindSeedZ` (`Walkable.cs:38-62`) honours the stored Z before the land Z - our fallback order,
  reversed from what we had.

Ours keeps its own `CanFit` flags (`requireSurface: true`, so it cannot answer with a Z in mid-air
over a hole) and adds one thing theirs does not: if nothing stands within the window of the hint,
it retries the window from the tile's own land surface before giving up. A hint can be stale, and
the ground is still the right answer when it is - it was only ever wrong as the *first* answer.

**This was not a pathfinder difference.** ServUO's `MovementPath` crosses that bridge perfectly
well when handed Z 6 to Z 3; we had never handed it the deck. `CoreSmoke` now walks four known
bridges as a regression test, because nothing cheaper catches this: the audit only paths edges we
have already authored, and there is no authored edge over a bridge nobody could cross.

**Two forms, and the split is the second half of the translation.** Their `TryFindSeedZ` and
`TryFindStandZ` are different functions for a reason the first port missed:

- `NavWalker.ResolveZ` is the **seed** form: the window around the hint, the window around the land,
  and then - new - a scan outward from the land as far as `SeedScanRange`, 60, nearest first
  (`Walkable.cs:33,38-62`). It answers "where would a mobile stand on *this* tile" for a waypoint,
  an arrival, an audit endpoint or a snap. It is what resolves Trinsic's south pier: uo-offline
  stores the **water's** Z, -15, for every generated dock, and the deck is at -2, thirteen above -
  outside both windows, so `TryPath` reported "nothing standable within 8 tiles" of a pier you can
  stand on.
- `NavWalker.ResolveStepZ` is the **step** form: the two windows and nothing wider. The floods in
  `NavCorridor.TryPath` and `BotWorkScout` use it when expanding tile to tile. Tried the other way:
  with the seed scan in the step, the flood went from flat ground at Z 10 straight onto a ramp tile
  whose stand Z is 17, a step of 7 the engine refuses, and proposed a road nobody could walk.
- `NavCorridor.TrySnap` resolves each ring with the seed form, so a record's own tile wins when
  anything on it stands. It was briefly two passes (step form over every ring, then seed form) to
  keep a wall-tile waypoint off the roof above it; that cost the Britain-Trinsic bridge its own
  waypoint, which snapped to the bank eight tiles away and kept the river bed as its Z.
- **Over ground, the scan climbs less than one storey.** Uncapped, it found roofs: three Trinsic
  shop arrivals sit on counter and display-case tiles where nothing stands on the floor, and the
  nearest surface above was the stone roof at 30 to 35. Roof tiles carry no `Roof` flag in this
  tiledata, so the guard is geometric - `SeedClimb`, 20, FastAStar's `PlaneHeight`: a goal a full
  plane above the start is not pathable from it anyway. It applies only where the land is ground.
  A deck over water has no ground storey: the Britain-Trinsic bridge stands 21 above the river bed
  its waypoint stores, and a ceiling measured from the bed put the deck out of reach and the
  engine refused both of that waypoint's edges. Downward stays unlimited, as theirs is.
- **A doorway answers before the scan.** A closed door is an Impassable *item*, so at a door tile
  nothing in either window stands - and the first version of the scan then found the floor above
  the door, twenty Z up, and aimed every walker at the tavern's upper storey. The walk probe went
  from zero recoveries to five bots mid-recovery in one run, and the life probe lost its bank crowd
  behind the bank's doors. `ResolveZ` now checks `DoorAt` at the hint and at the land Z before it
  scans, the same rule `NavCorridor.CanStand` applies; the next run walked clean (one repath, no
  sidesteps). uo-offline's seed has the same hole, but they only seed a destination tile, never a
  hop goal.

The window itself climbs 5 where theirs climbs 4 - see *The Z window climbs 5, not 4* under
Deviations. `CoreSmoke` now walks Trinsic's four canal bridges and the pier (with its stale Z) as
well as the four Britain bridges, and checks that a same-tile pair is a one-point path rather than
a failure.

### Bots move at a player's pace, and ride

Every bot moved at exactly half a second a step, which is the one speed nothing in UO moves at.
The half-second was `SpeedInfo.MaxDelay`: `GetSpeedsNew` derives a creature's speed from its Dex,
`PassiveSpeed` is twice `ActiveSpeed`, and `TransformMoveDelay` clamps the result to 0.5
(`Scripts/Mobiles/AI/SpeedInfo.cs:14,18-42,54-74`). Every bot sat on that clamp, which is why they
all moved alike whatever their Dex and why none ever played a run animation.

The delay is not decoration - `BaseAI.DoMoveImpl` derives the run flag from it
(`Scripts/Mobiles/AI/BaseAI.cs:2336-2343`):

```
delay   = TransformMoveDelay(CurrentSpeed) * 1000
running = CanRun && (mounted ? delay < Mobile.WalkMount : delay < Mobile.WalkFoot)
```

so 200ms on foot is a run and 400ms is a walk, exactly as for a player.

**And then it was overridden, and the panel said it was not.** The pace was written into
`CurrentSpeed` and the editor's Bots panel printed it - "running, on foot (200ms/step)" - while the
bot visibly walked and paused all the way to Trinsic. `DoMoveImpl` does not step on `CurrentSpeed`;
it steps on `TransformMoveDelay(CurrentSpeed)`, and `BotAI.TransformMoveDelay` returned a constant
0.5 whenever the bot was commuting, which is exactly whenever the walker steers. The constant and its
comment ("a brisk walk, not a run") were copied from `DailyLifeAI`, where they exist to escape
VendorAI's 30-120 *second* delay and where townsfolk are never given a pace. So `NextMove` advanced
500ms a step, `running = 500 < 400` was false, the client played a 400ms walk animation and then
nothing for 100ms. `[BotPace` measured it before the fix: a mounted bot given 100ms stepped every
543ms (median) with the running bit on none of 70 steps, while the walker's own timer ticked every
109ms and was turned away at the NextMove gate 649 times in 92 seconds. The walker was never the
limiter; the hypothesis that it ticked slower than the step was wrong by a factor of five in the
other direction.

The fix is one line: while commuting, the delay is the pace, untouched. Not `base`, either -
`SpeedInfo.TransformMoveDelay` clamps toward `MaxDelayWild` (0.8) for any uncontrolled creature
whose stamina is below its maximum, which would re-inflate a run the moment a bot took damage. The
panel now prints the delay the AI answers with, so it cannot make that claim again; and the walker
passes every already-satisfied step in one tick rather than spending a tick per hop boundary.

**The four delays are the engine's, not ours.** uo-offline aliases them
(`using MoveDelays = Server.Movement.Movement`, `CustomBots/Behaviors/TravelerBehavior.cs:23` and
`AdventurerBehavior.cs:28`) - not a local shadow, it resolves to ModernUO's
`Server/Mobiles/Movement.cs:18-36`, whose defaults are 400 / 200 / 200 / 100. ServUO holds the
identical four at `Server/Mobile.cs:3063-3071`. `BotMovement.DelayFor` reads them from there, so a
shard that retunes movement retunes its bots with it.

| | foot | mount |
| --- | --- | --- |
| walk | `Mobile.WalkFoot` 400ms | `Mobile.WalkMount` 200ms |
| run | `Mobile.RunFoot` 200ms | `Mobile.RunMount` 100ms |

Selected upstream at `CustomBots/Behaviors/TravelerBehavior.cs:3228-3238`, and here in
`BotMovement.DelayFor`, on the same two axes.

**Carried over verbatim:**

- **Run the long ones.** `running = forceRunning || dist > RunThresholdTiles`, with the threshold
  25 (`TravelerBehavior.cs:1803,1918-1927`).
- **Most bots own a horse.** A 70% roll at spawn, excluding Fishermen and gatherers
  (`PlayerBot.cs:743-753`). Class only - there is **no tier component upstream**, and none was
  invented for OWNERSHIP here either; the number moved to `bots.json` `mounts.ownChance` and is
  still 70%. A tier and trait component was invented for the **disposition** - whether a bot that
  owns one stays on it where it stops - which is a different question upstream does not ask,
  because it never dismounts on arrival at all. See the disposition bullet below.
- **The mount pool and its weighting.** Horse four times, then the three ostards and a llama
  (`BotMountHelper.cs:28-43`); 75% keep their coat, 25% take a muted hue (`:50-54`); five attempts
  (`:66-100`). Pack beasts are absent - nobody rides one.
- **The stables ritual.** A Tamer at the stables stables a horse it has 40% of the time, or claims
  one 60% of the time if it has none (`TravelerBehavior.cs:2399-2425`).
- **Gatherers work on foot** (`GathererBehavior.cs:108-119,258-268`) and **the mount does not
  survive its rider** (`PlayerBot.cs:779,856`).

**Where we deviate, and why:**

- **The threshold is measured over the whole route, not one hop.** Upstream's "leg" runs between
  destination waypoints and can be long; a leg here is one graph hop, capped at
  `Custom.NavHopMaxTiles` = 12, so `dist > 25` could never once be true and no bot would ever run.
  The route is what one of their legs corresponds to.
- **Speed is set on the creature, not by a per-behaviour step timer.** Upstream owns its own
  `_stepTimer` and calls `StepOnce`; we drive `BaseCreature.ActiveSpeed` / `PassiveSpeed` /
  `CurrentSpeed` and let `BaseAI.DoMoveImpl` do what it does for every other creature. Both speeds
  are written, because `BaseAI` picks between them by its own `ActionType` and a commuting bot is
  "wandering" as far as it is concerned - setting only `ActiveSpeed` left it on the clamp again.
  `CurrentSpeed` is only written when it changes: the setter restarts the AI think timer
  (`BaseAI.cs:3031-3037`), and writing it every tick would stop and restart that timer ten times a
  second and the bot would never think.
- **`NavWalker` drives at 100ms, not 250ms.** Its tick was documented as a ceiling that costs
  nothing, and that is true only while it sits above everything underneath it. At 250ms a bot told
  to run at 200ms on foot, or 100ms mounted, was held to 250 - so the ceiling *was* the speed. It
  is now `Mobile.RunMount`, the fastest step the engine defines, and the per-walker `MoveTo` is
  gated on `BaseAI.NextMove` so ticking two and a half times as often does not run a
  `MovementPath` two and a half times as often.
- **Who gets off is a disposition, drawn at birth.** This bullet used to say that
  `BotMovement.Settle` dismounts and drops to a walk, full stop, with upstream's "only at the
  stables, on death, or before swinging a pick" as the thing we deviated from and "a bank full of
  horses is not what a bank looks like" as the reason. Both halves were right and the conclusion was
  not, because **`Dismount` also deleted the horse and nothing anywhere re-mounted**: `TryMount` was
  reachable only from the birth roll, so a bot's first arrival put it on foot for the rest of its
  session. Measured over a fifteen-minute window of 61 bots: **70% own a horse at birth, 7.1% of
  travelling bots still had one, and 0.0% were mounted at arrival at every single tier.** A bank full
  of horses is not what a bank looks like; neither is a town where nobody owns one.

  So three things changed together:

  **Nothing is destroyed.** A dismounted mount is moved one tile off the bot's own tile,
  control-mastered to it and told to `Stay`, held on `PlayerBot.HeldMount`, and re-mounted on
  departure. `Rider = null` drops the animal on the *rider's* tile (`BaseMount.cs:111-124`), which
  would put a creature on an arrival point - the jam `PickScatteredHome` exists to avoid - and a
  riderless `BaseMount` left uncontrolled is a tamable creature with its own AI that wanders off and
  blocks bots on the way (`BaseCreature.OnMoveOver` refuses every uncontrolled creature's tile). An
  ethereal goes to the pack instead; nothing gives a bot one today, but upstream's dead branch is
  live here. `BotMovement.ReleaseMount` is the destructive half, for death and deletion.

  **The disposition is `MountDisposition`, rolled once at birth** from `tierFloor + tierSpan *
  BotSkillTierHelper.Fraction(tier)` plus `wealthyBonus` - so a Novice rides 10% of the time and a
  Grandmaster 85%, twenty points more if `Wealthy`. Riders still roll a small
  `dismountOnArrivalChance` each arrival, which is affordable *only* because nothing is destroyed:
  with the old delete the same number was an attrition rate, and 7.1% is what it converged on.
  Persisted as a byte at `PlayerBot` version 1, beside `Class`, `SkillTier` and `CrafterSpec`.

  **Ownership is still upstream's, verbatim** - a flat 70% at spawn, gated by class alone. **They
  have no tier or wealth component and none in their data**: `BotMountHelper` never reads
  `SkillTier` or `Gold`, and `playerbots/data/` carries no mount field at all. Their stables is not
  an economy to translate either - the "stabled" horse is `Delete()`d and the "claimed" one created
  fresh and free (`TravelerBehavior.cs:2411-2425`) - so there is no purchase to defer to 7f; a real
  one belongs with seams 11 and 12, where the gold is. The numbers live in `bots.json` `mounts`.

  **Where it applies, and the one place it does not.** BankSitter, Shopper and Crafter follow the
  disposition; the Crafter does so *because* upstream never dismounts a crafter - crafting has no
  mounted check anywhere in stock, so a forge full of bots on foot was ours and not theirs. The
  **Gatherer always dismounts**, and passes `mustDismount: true` to say so: `Mining.cs:500-502`
  refuses a mounted digger outright, which is not a matter of taste.

  **Measured, window C, 15 minutes of 61 bots.** Share mounted *at arrival*, which is the number the
  disposition is about:

  | tier | mounted at arrival | | phase | mounted |
  | --- | --- | --- | --- | --- |
  | Novice | 0.0% | | Traveler (on the road) | **57.9%** |
  | Apprentice | 0.0% | | BankSitter | **35.1%** |
  | Journeyman | 11.1% | | Shopper | **32.3%** |
  | Adept | 30.2% | | Crafter | 1.8% |
  | Expert | 49.5% | | | |
  | Master | **66.5%** | | **all arrived** | **24.8%** |
  | Grandmaster | 0.0% | | *before all of this* | **0.0%** |

  The curve is the design working: low tier walks, high tier rides. **Two zeros need reading rather
  than believing.** Novice and Apprentice are 10% and 22.5% dispositions times 70% ownership, so 7%
  and 16% expected over a handful of distinct bots - a plausible zero, not a broken one. Grandmaster
  is **one bot** in the whole window (the tier bell curve makes it 2% of the population), and it
  either did not own a horse or rolled the small chance; a single sample cannot say which. Crafter's
  1.8% is the staffed benches, which are a small, static, low-tier-skewed population - four Novices
  of ten - and a fixture's disposition is drawn once and never revisited. `57.9%` on the road against
  a 70% ownership roll is the honest ceiling: the missing tenth is bots arrived and dismounted at the
  moment of the sample.

  Two consequences worth knowing. A parked mount **reaches the save file**, where a deleted one never
  could, so `BotMovement.SweepStrayMounts` clears them at `Initialize` - by condition (a `BaseMount`
  whose rider or master is a `PlayerBot`) rather than by type, because a mount is a stock `Horse`,
  `Llama` or `Ostard` out of upstream's own pool and subclassing five of them to win a type test is a
  great deal of boilerplate for one sweep. And **a mounted body renders no human emote animation**,
  so a mounted sitter's bank-box bend and the hawker's wave will not show: a mounted sitter's idle
  texture is turns and speech. That is an argument for the disposition existing rather than for
  everybody riding.

**Verifying it.** The live snapshot carries `stepMs` and `mounted` per bot, and the editor's Bots
panel reads them back as "running, mounted (100ms/step)". Both are otherwise unobservable from
outside the process: timing a bot across two snapshots measures the town jam and the bends in the
road, not its speed. Measured on a live shard, mounted bots report 100ms and unmounted 200ms while
travelling - `Mobile.RunMount` and `Mobile.RunFoot` exactly.

### Work sites are weighted by how far they actually are

Weighting had no distance term at all, so a Miner weighed a face on the far side of the map exactly
as heavily as the one it was standing beside. A work site's weight is now multiplied by
`1 / (1 + routeTiles / destinations.distanceHalfTiles)` - 150 by default, meaning a site 150 tiles
away is half as attractive as one underfoot. A hyperbola rather than a cutoff, so a site twice as
far still wins when it is twice as good.

**Measured along the road, with the router the bot will itself use.** Straight-line distance calls
the west cliff close and is wrong about the only thing that matters: from the west gate it is 125
tiles to the northern outcrop and 350 to the west cliff, because the road out to the cliff leaves
by the town rather than the gate. A site it cannot route to at all scores zero and drops out -
though the place to FIX that is the island warning at load, not here.

Only work sites are routed, and only while they still have a weight worth spending an A* on, so a
class with no interest in rock costs nothing.

The roll is invisible from outside, so it is logged. `BotLogKind.Route`, once per pick where a site
was genuinely in the running:

```
site choice: brit-forge w3.81 165t, brit-mine-north w0.00, brit-mine-west w0.00 - chose 'brit-shop-tanner' instead
```

`*` marks the chosen one when a site won. A site with no tiles beside it was never measured, because
its class weight was already zero; one that says `no route:` carries the router's own reason, which
separates an island from an exclusive arrival that is merely reserved right now.

A `BankSitter` sets `Home` a few tiles off its arrival point and `RangeHome` to 2, and the stock
wander does the milling. It faces the nearest person occasionally and now and then bends over the
bank box. **No speech this session** - the hook is marked in `Tick` and fills in 7d.

**A behaviour that sets `Home` must clear it.** `PlayerBot`'s constructor sets it to zero and
everything downstream assumes that: `WalkRandomInHome` special-cases a zero `Home` into a *free*
wander (`BaseAI.cs:2516`), so a leftover `Home` would quietly leash a later Traveler back to the
bank whenever it stopped commuting.

## Speech

The corpus is `Data/Custom/BotChat/` — uo-offline's `PlayerBotChat/` copied byte for byte, 123
files plus 22 in `Gossip/`. `ChatLibrary` loads it; one file is one category, named by its stem.
Editing a `.txt` and running `[BotsReload` changes what bots say with no restart and no compile.

**The scan is flat, and that is load-bearing.** `Gossip/` is a subdirectory precisely so its files
are never served as ambient chatter: they are `{actor}`/`{other}`/`{place}`/`{when}` templates for
an event journal that does not exist yet, and a recursive scan would have a bot say
`{other} killed {actor} at {place}` out loud, verbatim. Upstream used a non-recursive
`EnumerateFiles` for exactly this reason.

### Three gates, in this order, and the order is the cost control

```
1. cooldown   a long, an integer compare
2. chance     a double compare
3. audience   a sector query - the only expensive one
```

Behaviour ticks run on a shared timer whether or not anyone is watching, so without gate 3 a
thousand bots would chatter at an empty world for ever. Putting it **last** means the query is paid
only when a line is otherwise about to be said.

`IsPlayerNearby` asks the sector, not `NetState.Instances`. A bounded `GetMobilesInRange(22)` is
not the `World.Mobiles` sweep §15 warns about — `FaceNearestPerson` already does the same — and
unlike the client list it can see a mobile with no `NetState`, which is what the chat probe's
synthetic listener is. Upstream tested "is a `PlayerMobile` and is not a `PlayerBot`"; here a
`PlayerBot` is a `BaseCreature`, so **bots cannot trigger each other by construction** and the
second test is unnecessary.

### Who says what

| | categories | chance / cooldown |
| --- | --- | --- |
| **BankSitter** Regular | `small_talk`, `bank_actions`, `wtb`, `lfg` | 0.25 / 15–45s |
| **BankSitter** Hawker | `wtb` | 0.55 / 10–25s |
| **BankSitter** Afk | — | silent |
| **Shopper** | `shopping`, `small_talk`, plus vendor triggers on a 12–28s clock | 0.18 / 20–50s |
| **Traveler** walking | `traveling`, `small_talk` | 0.10 / 30–90s |
| **Traveler** lingering | by destination `type`, discriminated by tag | as above |
| **Crafter** | `smith_talk` / `tailor_talk` / `carpenter_talk` + `craft_talk`; `craft_night` after dark; `craft_masterwork` on an exceptional piece; `craft_need` when dry | 0.18 / 20–50s |
| **Gatherer** | `gather_talk`, `small_talk`; `gather_haul` when the shift ends | 0.10 / 45–120s |
| **Idle** | `small_talk` | 0.05 / 60–180s |

Two texture layers steal an occasional ambient slot rather than adding one, so neither makes a bot
chattier: `night_talk` at 25% between 21:00 and 05:00 **real** time, and `emotes` at 10%.

`TravelerBehavior.ArrivalChatFor` is **severed seam 1 being paid off**: upstream keyed it on its
31-member `DestinationType` enum, and ours keys on our free `type` token discriminated by tag,
exactly as the destination weighting already does.

### The token ledger

`ChatTokens` is the single place that knows every `{token}` in the corpus, who owns it, and whether
it resolves yet. Each line is classified once, at load:

| | meaning | treatment |
| --- | --- | --- |
| **wired** | every token has a live resolver | enters the pool |
| **reserved** | every token is known, one or more unwired | counted, **never enters a pool** |
| **unknown** | a token nobody registered — a typo | counted, **Warn** |

`{place}` and `{dest}` read `NavDestination.Name` straight out of `navigation.json` and needed no
code change to start saying Britain's real building names — *"I'm off to The Cleaver"*, *"meet me
at First Bank Of Britain"*. That is the whole point of the naming pass: the names were already
being spoken, they were just placeholders. Sixteen destinations took their Stratics name; the mage
shop kept its generic one because the nearest atlas entry is 22 tiles away and may simply be a
different building, and ten records (the houses, the gates, the crier's stand, the outcrop) have no
atlas entry at all.

Ownership: `{place}` `{dest}` → nav (**wired**, from `NavDestination.Name`); `{name}` → identity
(**wired**); `{mat}` → crafting (**wired** since 7e, from `CrafterProfile.MaterialNoun`);
`{price}` `{item}` `{short}` → economy; `{pet}` → taming; `{dungeon}` → adventurer;
`{actor}` `{other}` `{when}` → journal.

`{mat}` moved owner rather than merely gaining a resolver, and the move is the point: it was filed
under economy because that is where upstream's shops live, but the session that could actually
answer it turned out to be the crafting one. `craft_need.txt` reads *"need {mat}"*, and a Crafter
knows its own profile. A bot of any other class resolves it to nothing and the runtime guard then
refuses the line — correctly, because a Warrior has no materials to be short of.

> `{short}` is **coin**, not a short name — `trade_short.txt` reads *"need {short} more"*, the gap
> between an offer and a price, and it always travels with `{price}`. It is economy-owned.

At the time of writing, after 7e wired `{mat}`: **1,224 non-comment lines across 107 categories —
1,068 wired, 156 reserved, 0 unknown** (it was 1,060 / 164 before `{mat}` came off the reserved
list). Reserved is a progress meter, not a
fault list: it falls as later sessions land. If it ever *rises*, a corpus edit has introduced lines
nothing can say.

**Classification is static; the guard is not.** A wired token can still fail at runtime — `{place}`
needs a destination and a bot may have none — so `ChatTokens.TryResolve` refuses any line still
holding a `{` when substitution finishes. Both are needed, and every category wired today happens
to be token-free, so the guard is currently belt to the static pass's braces.

### Answering

`BotSpeechResponder` is upstream's, with its ranges and probabilities intact: name at 10 tiles
(its own 15s guard, cutting through the general cooldown — you answer to your **name**), greeting
at 5 (answered 65% of the time), question at 5 (75%), anything said in your face at 2 (45%).
Deliberate silence is a valid answer; upstream's note is worth keeping — *"sometimes it just
ignores you, which is also exactly what a real player did."*

**One reply per utterance.** Every listener's `OnSpeech` runs in the same pass, so distance checks
alone cannot stop a chorus — ties and same-pass cooldown writes poison any "am I closest?" logic.
The first bot to answer **claims** the utterance for two seconds and the rest let it stand; a name
mention overrides a generic claim.

Replies come after a typing delay of `0.9 + rand(0..1.2) + min(len × 0.04, 1.2)` seconds, and the
bot turns to face you first. Upstream's reason, in `BotShopTalk`: *"an instant answer is the
loudest tell there is."*

### Bot speech is not a command

`bank_actions` has bots saying `bank`, `withdraw 1000` and `balance` out loud, and a bot carries
`Player = true` for the party gate — so the obvious worry is a real `Banker` treating those as
commands.

**It cannot.** `Mobile.Say` goes to `PublicOverheadMessage` (`Mobile.cs:7236-7239`), which only
sends packets; the listener pipeline hangs off `Mobile.DoSpeech`, and `DoSpeech` is invoked from
exactly two places, both real client speech packets (`PacketHandlers.cs:1547`, `:1628`). Scripted
speech never enters that path, so no keyword handler ever sees it and no `Handled` flag is needed —
there is no dispatch to suppress.

Which is exactly why `BotChatProbe` asserts it, against a sentinel mobile whose `HandlesOnSpeech`
is unconditionally true. Nothing else in this folder holds that invariant up, so nothing here would
notice it going: the day somebody routes bot speech through `DoSpeech` to make bots hear each
other, every `bank_actions` line becomes a live command in the same commit. The fix then is to
suppress the dispatch for bot speech — **never** to strip the lines out of the corpus.

## Working

Six classes had a trade and nothing to do with it. Now five of them do: **Smith**, **Tailor**,
**Carpenter**, **Miner** and **Lumberjack**. The Tailor is the odd one out of the five — it works
from its own bolt of cloth rather than from a haul, because no gatherer brings cloth, which is
upstream's note and still true. Fisherman is the sixth, and is [severed](#severed-seams).

```
Miner --walks--> brit-mine-north --mines--> ore --walks--> brit-forge
                                                               |
Smith --stationed at brit-forge----------------------------- takes it
      --crafts--> a dagger, out of those ingots, on a real skill check
```

Neither half is reachable from the lifecycle roll. Both are **arrival handoffs**, exactly as
upstream had it: `Crafter` and `Gatherer` are what you *become* by turning up somewhere that wants
one, and an 8.0/10.0 destination weight is what keeps taking you back. Rolling a Crafter out of
nowhere would put a smith at a bench that is not there.

### The yield is real, and upstream's was not

This is the one place the port deliberately does more than the thing it is porting, so it is worth
being exact about what changed.

| | uo-offline | here |
| --- | --- | --- |
| **Mining / lumberjacking** | `AddYield` → `new IronOre(2..6)` on a 35-second timer. The ground was never consulted | `Mining.System.StartHarvesting` / `Lumberjacking.System` against a real `LandTarget` / `StaticTarget` |
| **Crafting** | `TryProduce` → roll `MakeChance` by tier, roll a band, `new Dagger()`, consume a flat 6–20 "materials", stamp the maker's mark by hand | `CraftItem.Craft` — real skill check, real `ConsumeRes`, real exceptional roll, real maker's mark, real failure |
| **A "site"** | any waypoint ≥45 tiles from a city. Fine, because nothing was dug | a rock face, validated tile by tile against `Mining.m_MountainAndCaveTiles` |

Only their Fisherman drove a real engine (`Fishing.System`), so **there were no harvest or craft
calls to translate** — the ModernUO-to-ServUO mapping this session was scoped around does not exist
upstream. What was translated is the *shape*: the cadence, the states, the dry spell, the haul, the
chatter. What sits underneath it is ServUO's.

Three consequences of that swap, all load-bearing:

- **A bot can only ever get plain ore.** `Mining.GetResourceType` (`Mining.cs:187`) falls through to
  `resource.Types[0]` for anything that is not a `PlayerMobile`, and sand mining is
  `PlayerMobile`-gated outright (`Mining.cs:283`). That happens to match upstream's `IronOre`
  exactly — but a Miner will never bring home granite or gems, and that is the engine's decision,
  not a config one.
- **A full pack silently destroys the ore.** Neither definition sets `PlaceAtFeetIfFull`, so
  `HarvestSystem.cs:207-210` sends the pack-full message and calls `item.Delete()`. `Swing` checks
  capacity before every attempt; without that check a bot at its limit would spend the rest of its
  shift deleting what it dug.
- **Resource banks deplete for real** — 10–34 ore per 8×8 bank, respawning over 10–20 minutes. A bot
  that stood still would mine its bank out in a couple of minutes and then swing at nothing, which
  is why upstream's shuffle-along-the-face survives into a system that no longer needs it for
  decoration.

Their `MakeChance` roll is **gone**: a real skill check is a better version of the same idea, and
keeping both would have squared the odds. What survives is their **cadence** — a work cycle every
6–14 seconds — and it is now the only rate limiter there is. ServUO's craft delay is 1.25s for all
three systems, so a Grandmaster left to run flat out would bury the shard in daggers.

`AutoCraftTimer`, the engine's own make-N loop, is **never** used: `AutoCraft.cs:110` bails on
`NetState == null`, so it would silently do nothing for a bot.

### The sites are hand-picked, and the tool is not allowed to choose them

The brief asked for ~40 gathering sites from `uo-offline`. **They do not exist.** Their 40 were
generated at runtime from their 4,013-node Felucca waypoint graph and were **retired on 2026-07-06**
to be hand-authored later; because their yield was invented, a bare road node made a perfectly good
site. Of their three *authored* sites only `MiningSpot 448` is near Britain, and it is a **Felucca**
site — on Trammel that area holds 110 scattered mineable tiles across a 120×120 box and none within
18 tiles of their centre.

> **Re-checked against their September 2026 release (`fe18a469`)** and unchanged: still exactly
> three authored sites, `GatherSpots.cs` still the retired stub. This paragraph is the load-bearing
> premise of everything below it, so it is worth knowing it survived the update rather than having
> to re-derive it.

So the sites had to come from our own ground. **The first attempt at that shipped, and was wrong.**

It let the tool choose, by sweeping for anything `HarvestDefinition.Validate` accepted. That is a
far weaker test than it sounds:

> Stock ServUO's `m_MountainAndCaveTiles` contains land ids — **236–247 among them** — that this
> client's tiledata names **`'forest'`**. `Validate` accepts them happily.

The sweep therefore found scattered forest-transition tiles in the fields west of Castle Britannia
and called them a mine. Every arrival passed `Validate`. Every arrival had **one to four**
harvestable tiles in reach. The corridor crossed the castle moat. `[NavAudit` was clean, because the
geometry genuinely was. And `Bots.Shift` passed, because the miner was hauling the ore
`EquipmentTable` spawns it with (see below). Four independent checks, all green, all measuring the
wrong thing.

What real rock looks like, from `[BotOreSweep`:

```
MINE cell 1450,1520 count 51 dist 170 | rock/556 x27, rock/557 x5, rock/559 x5, ...
MINE cell 1190,1940 count 100 dist 250 | rock/556 x22, rock/557 x27, rock/558 x20, rock/559 x31
```

**Land 556–559, named `'rock'`, in cells that saturate at 100/100** — against the one-to-four
scatter the sweep had settled for. The name is the tell, and density is the measure.

The lesson is in the tool now: `BotWorkScout` **chooses nothing**. Its `Sites` array is hand-picked
coordinates, verified with `[BotSitePick` and walked in-game before being written down, and the
tool's only job is the part a human should not do by hand — finding a walkable road back to the
graph and proving every hop. And `Nav.Data` refuses to let a thin site pass quietly again; see
below.

### The site tools, and why reading the MUL files yourself does not work

The first cut of this data was derived **offline**, by a script reading `map1LegacyMUL.uop`,
`statics1.mul` and `tiledata.mul` directly. It looked entirely convincing: every arrival point sat
within two tiles of what the script believed was a mineable land tile, and every corridor hop passed
a breadth-first search over the land and item `Impassable` flags.

It was wrong, and the shard said so on the first boot:

```
[NavAudit] 109 walk edge(s) checked: 14 blocked, ...
BLOCKED 'brit-minenorth-1' (1351, 1587, 50) (authored z -5) -> ...
Work site 'brit-mine-north' is excluded: no arrival point has a harvestable tile within 2 tiles
Miner has no station: nothing on this facet is a 'mine' destination.
```

Read the Z values. The engine resolves that corridor at 30, 50, −15 and 11; the script had read a
flat −5 for all of it. The UOP chunk table is hash-keyed, the script's block-ordering assumption did
not survive contact with it, and it was close enough to look plausible on a handful of spot checks —
the Minoc mountain probe came back correct — while being useless everywhere it mattered. The
lumber site passed, because trees are statics and the statics reader was fine; both mine faces
failed, because rock is land.

So the authoring moved inside the engine. **`[BotWorkScout`** (Administrator, or
`Custom.BotWorkScoutOnStart=True` for a headless run) sweeps a seed radius for harvestable tiles,
picks spread-out standable arrival points, floods a walkable corridor back toward town and samples
it into hops — asking, at every step, exactly the question the runtime asks:

| Question | Asked with | Which is also what… |
| --- | --- | --- |
| can this be dug? | `HarvestDefinition.Validate` over `map.Tiles` | the bot asks when it swings |
| can a bot stand here? | `map.CanFit` at `NavWalker.ResolveZ` | the walker aims a hop at |
| is this hop walkable? | `MovementPath`, both directions | `NavAudit.CanWalk` does, verbatim |

Because the last row is *the same test*, anything the scout emits passes the audit. It writes
`Data/Live/work-scout.json` for a human to merge and **deliberately does not touch
`navigation.json`**: that file is authored, carries comments and an ordering somebody chose, and a
generator that rewrote it would quietly become its author.

Four commands, all Administrator, all read-only:

| Command | Answers |
| --- | --- |
| `[BotSiteAudit` | what is really under every work-site arrival and corridor waypoint — land id and **name**, statics, walkability, water, and how many harvestable tiles are in reach |
| `[BotOreSweep [radius]` | where dense harvestable ground actually is, per 10×10 cell, **with the land names** — the command that found `rock/556` |
| `[BotSitePick <x> <y> [mine\|chop]` | the best places to *stand* near a point, ranked by what is in reach from each |
| `[BotForgeAudit` | where the anvil and forge really are, and which tiles are within 2 of both |

`[BotSiteAudit` is the one to keep in your head: it is the check that would have caught the first
set of sites in seconds, and it is now permanent rather than a one-off.

One caveat it states about itself: it reports **distance and Z, never line of sight**.
`CheckAnvilAndForge` uses `Mobile.InLOS` and there is no mobile in a diagnostic;
`Map.LineOfSight`'s `Point3D` overload aims at the fixture's own tile and answers false even from
the tile the anvil stands on — which is how an earlier pass of it managed to report that no tile in
Britain can reach the Britain anvil. The pessimistic half is exact; the optimistic half is answered
for real by `CanCraft` the moment a Smith tries, and surfaces through `Blocked`.

It also samples **from the site inward**, stopping the moment a hop can reach the existing graph.
Sampling from the gate outward is the obvious way round and it is wrong — it re-walks Britain and
lays a second set of waypoints down streets that already have them. Growing inward cut the three
corridors from 60 waypoints to 36.

### `Nav.Data` will not let a thin site through again

The check that would have caught the first set of sites is now permanent, and it lives in
**`Nav.Data`** rather than `Bots.Work` on purpose: "there is no ore under this mine" is a fact about
`navigation.json`, and the person who needs to hear it is whoever just authored a site in the editor.

Any `mine` or `lumber` destination whose **best** arrival point reaches fewer than its type's floor
warns, naming the site and its actual reach. Reach is measured with the very same 5×5 sweep
`BotHarvest.FindTarget` performs, so it answers the only question that matters: standing here, will
the bot find something to swing at?

**The floor is per type, because rock and wood are not the same question.** A mine face saturates —
the authored ones reach 11 to 15 — so `Custom.BotWorkSiteMinReachMine` at **5** is a low bar there
and still catches a site authored on the wrong tiles. A wood does not: Britain is not ringed by
dense forest, and no cell within 500 tiles of the bank holds more than nine choppable tiles per
hundred, so the Britain wood reaches 4 and is a perfectly good place to send a Lumberjack, which is
why `Custom.BotWorkSiteMinReachLumber` is **3**. One number for both meant either warning about a
legitimate wood for ever, or lowering the bar for rock until it caught nothing. A type with no key
of its own falls back to `Custom.BotWorkSiteMinReach`.

**Every arrival's reach is written to `Data/Live/site-reach.json`**, not just the best one.
`Nav.Data` reports the best because its question is *"is this site worth sending anyone to at
all"*; somebody authoring a site needs the other question — which of these tiles is any good. A
face has thin edges by nature, and an arrival on one is not a data error, but it *is* the arrival
that produces a miner standing in the right place swinging at nothing. The editor draws those
numbers, and the `site-reach` token answers for tiles that are not in `navigation.json` yet, so a
spot can be judged as it is placed rather than after a save and a reload.

The navigation layer is Core and knows nothing about bots, so it does not reach upward to ask —
consumers register an auditor (`NavigationSystem.RegisterAuditor`) and `Bots` registers this one.
Anything later can add its own without that file learning what it is.

Two deliberate distinctions:

- **Thin warns; empty excludes.** A site with *nothing* in reach is excluded from the destination
  roll outright, because a bot sent there stands and swings at air. A site that is merely thin is
  still worked — which is what keeps a legitimately sparse wood usable rather than deleting it for
  being sparse.
- **The threshold is a floor on the BEST arrival**, not on every one. A face has thin edges by
  nature; what matters is whether the site has a good spot at all.

### The north face shipped as a trap anyway

Everything below was written when the site was authored, and the site still violated it. The zone
is **26 tiles deep** and had **one** approach waypoint at its southern end, so the far corner was
**21** tiles away against a 12-tile cap — and the authored arrival at `1450,1512` was **16** tiles
from the nearest waypoint. `Nav.TryPickArrival` chooses uniformly, so about **one miner in five was
stranded the instant it arrived**, mined a full shift, and then could never route home. The only
symptom was the work probe failing on delivery every few runs.

Three things were wrong at once, which is why it lasted:

- **The scout's clamp is described below as if it had been applied. It had not been** — or the site
  was re-authored afterwards without it.
- **`Nav.Data` only asked whether *any* arrival was reachable**, so a destination with one good
  point and four bad ones passed silently. It now names each offender and its real distance.
- **`CanGetHomeFrom` guards the shuffle, not the arrival.** No amount of care about where a bot
  *steps* helps when it is *placed* out of range to begin with.

Fixed by adding `brit-minenorth-2` and `-3` on two existing arrival tiles — coordinates
`BotWorkScout` had already proved standable — which is why two new edges across a mountain face
passed `[NavAudit` first time. Worst tile in the zone is now 9 tiles from a waypoint.

### A work site has to fit inside the hop cap, or it is a trap

The second thing the engine had to say, and it cost a probe run to hear.

`Nav.TryRouteFrom` refuses to route from anywhere with no waypoint within
`Custom.NavHopMaxTiles` — 12 — and says so plainly in its own comment: *"the mobile is somewhere the
data does not cover."* A gatherer spends its shift **shuffling around inside the site's zone**. So
if the zone is deeper than the hop cap from the waypoint it attaches by, some of its tiles are a
one-way trip: the bot walks in, mines perfectly happily, finishes its shift, asks for a way home
every tick and is told there is none. For ever.

The first work probe found exactly that. The north face is 18 tiles deep and its approach waypoint
sat just outside it, so a miner that wandered to the far side was 22 tiles from anything:

```
Bot work probe FAILED - the miner is still carrying its load - it never reached a
delivery point; no load was delivered
```

Three things now stand between that and a repeat, deliberately at three different levels:

1. **The scout emits the site anchor as a waypoint**, so the approach is *on* the face rather than
   one hop up the corridor.
2. **The scout clamps the zone** to one tile inside the hop cap of that approach. Losing a little
   rock at the rim is the right trade — those tiles were unreachable by definition. The three faces
   now measure 10, 10 and 11 tiles from approach to worst corner.
3. **`GathererBehavior` refuses the step anyway.** `CanGetHomeFrom` asks `Nav.NearestWaypoint`
   before shuffling, so the behaviour is correct even against bad data. It is paid only on the 15%
   shuffle roll, and the failure it prevents is silent, permanent and looks exactly like a broken
   walker — which is worth a hundred-waypoint scan.

| Site | Type / tag | Face | Best arrival reach | Arrivals | Corridor |
| --- | --- | --- | --- | --- | --- |
| `brit-mine-north` | `mine` / `mine-north` | 14×26 at 1443,1508 | **14**/25 | 5 | **3** waypoints, joining at `brit-inn-1` |
| `brit-forge` | `forge` / `craft town` | 1424,1557 | anvil `0x0FAF` at 1423,1556, forge `0x0FB1` at 1424,1558 | 4 (all exclusive) | in town, off `brit-smith-1` |

**One new waypoint and one new edge.** The outcrop north-east of Britain turns out to sit six tiles
from `brit-inn-1`, so the "corridor" is a single hop — which is the happiest possible outcome and
was not at all what the first attempt produced. — `costTags` already carried
`wilderness` at 1.4, so the schema was built for this before there was anything to put in it.

Each corridor attaches to the nearest existing town waypoint rather than starting from the gate, so
the only waypoints added are ones that are genuinely new. For the outcrop that came to exactly one.

**Two further sites were picked, verified, and then NOT written**, which is worth recording because
the reason is a limitation of the tool rather than of the ground:

| Site | Ground | Why it is not in the file |
| --- | --- | --- |
| `brit-mine-west`, the west cliff (1192,1750 group) | reach **15**, genuine `rock` | the corridor comes back as a **28-hop arc** swinging north-west around the range |
| `brit-lumber-south`, the wood at 1422,1832 | reach 4 | a **36-hop arc**, for a wood a hundred tiles due south |

Both arcs are real — every hop passes `MovementPath` — but they are the long way round, and the
cause is that **the corridor flood cannot model a town gate**. `FastAStarAlgorithm` sets
`MoveImpl.AlwaysIgnoreDoors` for a `BaseCreature` (`FastAStarAlgorithm.cs:93`), so a real bot walks
through Britain's gates; the flood, working in `map.CanFit`, refuses them and detours until it finds
open wall. Aiming at the eight nearest waypoints instead of one changed nothing, which is what
proved it was the gates rather than the target choice.

So those two want a **hand-authored corridor through the west gate and the south bridge** — exactly
the editor work the [later](#later) list describes, and exactly what the `Nav.Data` reach check now
makes safe to do.

`brit-forge`'s coordinates are upstream's authored *Britain Forge*, confirmed against
`Data/Decoration/Britannia/britain.cfg:1327,1338`. Its name is its Stratics one, **The Hammer and
Anvil** — the full real-name pass over Britain's destinations is a later session, but this
destination is new, so it was named right rather than added to the backlog.

### The Smith's station is the only one that is load-bearing

`DefBlacksmithy.CanCraft` wants a forge **and** an anvil within two tiles, with line of sight to
both, or it answers 1044267 (`DefBlacksmithy.cs:180`). `DefTailoring` and `DefCarpentry` have no
fixture requirement at all — their `CanCraft` bodies are identical four-liners — which is why only
the Smith gets a `forge` destination and the other two station at their shops, discriminated by the
new `tailoring` and `woodworking` tags.

Upstream's own arrival list for Britain Forge would have failed here: their forge exporter required
an anvil *near the forge* (`AnvilNear = 6`), not near each arrival, so one of their four tiles
(1423,1559) is three from the anvil. Ours are the tiles within two of **both**.

They are also the only arrivals in the file marked **`exclusive`** outside the guard posts, and for
the same reason those are: `NavArrivals` scatters a non-exclusive point by `Custom.NavArrivalScatter`
(2 tiles), and two tiles off one of these is out of `CheckAnvilAndForge`'s range. The work probe
found that the honest way — a smith standing at its own forge, reporting *"it is not beside both a
forge and an anvil"*. Scatter is right for a rock face, where it stops four bots stacking on one
tile; it is wrong for a spot chosen because of what is next to it.

> **All four are exclusive now, and the original "2 exclusive, 2 shared" was a bug.** Every one of
> the four carries a note saying it is an exact tile — but only two were given the flag, so the
> picker chose a shared one about half the time, `Scatter` moved the smith up to two tiles off, and
> the probe failed with the very message quoted above. It had done so since the site was authored;
> the run that caught it put the smith at 1425,1556, which is (1423,1558) plus (+2, −2).
>
> Making all four exclusive is a **stopgap, not the design**. Exclusivity and exactness are
> different properties, and paying for one with the other costs three of the four standing spots —
> `Nav.Data` now says so on every boot: *"'brit-forge' has 4 exclusive arrival point(s) but only 0
> shared one(s)"*. The fix is an `exact` flag on an arrival — no scatter, but not reserved — which
> is a schema change and belongs with the other schema work.

`BotWorkSites` checks that at load without line of sight, and says so: the pessimistic half — a
fixture that is simply not there, which is what a wrong coordinate actually looks like — is exact,
and the optimistic half is caught the moment a Smith tries and `Bots.Work` reports the refusal in
words. Asking the engine's own `CheckAnvilAndForge` would have meant placing a mobile in the world
to answer a config question, and `NavAudit.cs:330-334` already refused that trade for the same
reason.

### Capacity is the bank floor read backwards

```
crowds     fewer than this and the place pulls harder   -> a bank looks busy
capacity   more than this and it stops pulling at all   -> a work site fills up
```

Both come off `BotCrowds.CountFor`, deliberately — the same question about the same count, and two
counts would eventually disagree. `BotWorkSites.VacancyFactor` tapers linearly to zero at capacity,
so a half-full face is half as attractive rather than equally attractive right up to the last slot,
and **bots already routing there count as occupying it**, exactly as they already count toward a
bank floor. Without that, a whole shift converges on one face and arrives to find it full.

Default capacity is the destination's **authored arrival-point count**, because that is how many
bots can physically stand there: an uncontrolled `BaseCreature` cannot walk through another one
(`Movement.cs:411`). `capacity` in `bots.json` is keyed by **destination id**, not by type — two
faces of the same rock can hold different numbers of people — and ships empty.

### An artisan wants its own station, not every craft shop

Before this session `byTag.craft` gave Smith, Tailor and Carpenter **4.0** at all seven craft shops,
which was standing in for a station they did not have. They now have one, so that is down to 1.0 and
the pull lives in `byType.forge` / `byTag.tailoring` / `byTag.woodworking`. `mine` and `lumber` are
0.0 for everyone except the class that works them — nobody else wanders onto a rock face.

While `HaulPending`, a gatherer's weights are **replaced** rather than blended: 9.0 at a station
whose trade buys what it is carrying, 2.0 at a bank, 0.02 everywhere else. Blending would let a
Miner's own 10.0 site weight outvote the delivery it is on, and it would walk back out to the mine
still carrying the last load.

**Two numbers are ours rather than upstream's, and they are the same idea twice.** A station with
somebody actually at it scores **20.0**, and while such a station exists the bank drops from 2.0 to
**0.02** — it is the fallback, not a rival. Upstream's weights asked whether a destination *could*
buy the load, never whether anyone was there to buy it, and that is the question that decides
whether the haul turns into anything: a load left at an empty forge is a load in a bank box with
extra steps.

At 9 against the bank's 2 a laden miner banked about one trip in five; even at 20 against 2 it was
still one in eleven, and the work probe duly failed on it with everything else green — *16 units
mined, 16 delivered, the smith dry*. That is not a miner making a choice, it is a miner getting it
wrong. With nobody at the bench the bank returns to 2.0, which is right: ore in a bank box is ore
kept, and it is what a player would have done.

The original 9-vs-2 version was found the same way:

```
Bot work probe FAILED - the smith made nothing in 420s (0 attempt(s)) - it never
received any ore. miner ended as BankSitter, 1 load(s), 8 unit(s) delivered
```

Everything worked. The ore was mined, hauled and delivered; it just went to the bank while a dry
smith stood at the forge. `IsStaffed` costs one pass over the live crafters, and only for a station
that already matched the trade.

### The hand-over, and what does not change hands

`BotWorkDelivery` fires on arrival, **before** the handoff rolls: arriving is what completes the
errand, and a declined handoff would otherwise send the bot away still loaded. The ore comes out of
the pack and the panniers, and if a Crafter of the matching trade is working within 12 tiles it goes
into that crafter's stock. Otherwise it goes to the bank box — the "no buyer today" ending, not a
failure.

**No gold moves, in either direction.** Upstream paid the gatherer 2–4gp a unit out of the crafter's
purse and refused the sale when the crafter was broke; that is the economy, and the economy is 7f.
The *scene* still plays, on upstream's delays, because the scene is the visible part: the gatherer
says its line, the crafter answers two seconds later, the trade closes. An instant answer is the
loudest tell there is.

A crafter that runs out says so — `craft_need`, which is what wires `{mat}` — and then **stays dry
until somebody brings it something**. That is not a gap. It is the motive the whole loop exists to
create, and upstream papered over it by buying a restock for gold every three minutes.

### Clocking in is a gate, not a formality

A gatherer used to clock in wherever it was standing:

```csharp
_clockedIn = _site == null || Contains(_site, bot.Location);   // upstream's unpainted-site case
```

Upstream carried that so an unpainted site still worked. Here it produced a Miner clocked in on the
**west bridge at 1400,1748**, reporting *"working a work site, 0 swings, carrying 9/120"* — the 9
being its spawn kit, the 0 swings being the truth.

Clock-in now requires **both**:

1. inside the site's zone, and
2. at least one harvestable tile inside `BotHarvest.FindTarget`'s own 5×5 sweep.

The second is not redundant. A face has thin edges, and a bot standing on one is inside the zone and
still swinging at nothing for the length of its shift. Passing the reach test means the *next* swing
finds a target. A gatherer with no zone at all now says so and walks away rather than miming a shift
in a field, and the clock-in is logged with the zone and the reach count so "why is it working
there" has an answer.

### A hand-attached Crafter goes to its bench

`[BotBehavior Crafter` on a Smith standing in the blacksmith's shop used to anchor it *there* — four
tiles from the anvil, unable to work, reporting only *"cannot work at its station: not beside both a
forge and an anvil"* with no station to name, because `DestinationId` was null.

`OnAttached` now resolves the nearest station its class works (`BotClassHelper.StationFor`), and if
the bot is not already on one of that station's arrival tiles it **walks there** through `NavWalker`
like any other journey — watchdog, `Commuting` flag and all — and only settles when it arrives.
`CanTransition` is false while it walks, so the lifecycle cannot yank a bot mid-route.

The arrival handoff never had this problem: it delivers a bot already standing on a validated tile.
This is purely the by-hand path, which is exactly the path a person uses when testing.

### The pack animal

Fully contained on ServUO, and the reason is worth recording: `SetControlMaster`, `ControlOrder =
Follow` and `pack.DropItem` are all typed on `Mobile`, and every bit of `PlayerMobile` bookkeeping
inside `BaseCreature.AddFollowers` sits behind an `is PlayerMobile` guard a bot simply skips. So a
`BotPackHorse` or `BotPackLlama` spawns at clock-in, follows, doubles the haul from 60 to 120, and
is **released by deleting it** on delivery — upstream's own path when no stables is in range. Plus
their orphan reaper (a *dead* master keeps the beast waiting; only a deleted or off-facet one
orphans it), their world-load stray sweep, and delete-on-owner-delete.

## Class-weighted destinations

```
final = byType[type][class or "default"] * product over the destination's tags of byTag[tag][class or "default"]
```

Absent means **1.0** ("no opinion"), never 0. A weight of **0 excludes** — which is how `home`,
`guard` and `work` are kept off the list.

Then the home bias: a destination carrying the bot's `HomeTown` tag is multiplied by
`destinations.homeBias` (2.5, upstream's number). `destinations.towns` lists the tags that count
as towns; a bot rolls one at creation, weighted by how many destinations carry each. A town that
nothing is tagged with is reported by `Bots.Population` as `towns.<name>` among the unknown keys.

Two tables rather than one because of what the data looks like: our `type` is a free token with nine
values, but **13 of the 27 destinations are `shop`**, so type alone cannot tell a forge from a
bakery. The discrimination lives in the tags (`craft` on seven, `food` on three, `magic`, `luxury`,
`service`), which is what lets a Smith want a craft shop without wanting every shop. Upstream keyed
one table on a 31-member enum; ours is data.

Validation, at load:

| Problem | Verdict |
| --- | --- |
| A class name that is not one of the 17 rollable classes | **Fails the load**, keeps the live config. It is silent otherwise — the class falls through to `default` and the shard runs for ever with a weighting nobody asked for |
| A `type` or `tag` key that names nothing in `navigation.json` | **Warns.** Not wrong on a shard that has not authored that town yet, but a key matching nothing does nothing |
| A class that can reach zero destinations | **Warns.** It looks exactly like a walker bug from the outside: the bot asks every tick, gets nothing, stands still |

## Failure contract

**A failed load keeps the live caps.** `BotSystem.TryLoad` swaps `_store` only after the config has
bound and validated, so a bad edit to `bots.json` leaves bots being built to the previous good
limits rather than to a half-applied one — the damage from a wrong cap is baked into a bot's skills,
where it is invisible, rather than into a log line, where it is not.

With no config at all, caps resolve straight from `Config/PlayerCaps.cfg`, which is the intended
default and not a degraded state. `Bots.Config` failure is reported through `Bots.Population`.

## Commands

| Command | Access | Effect |
| --- | --- | --- |
| `[SpawnBot [class] [tier]` | GameMaster | Spawn a bot at your feet. Class and tier are rolled when omitted, and may be given in either order (alias `[SpawnTestBot`) |
| `[BotInfo` | GameMaster | Target a bot; dump its class, tier, stats and every non-zero skill against the caps in force |
| `[BotBehavior` | GameMaster | Target a bot; report its brain, status line, and its **voice** — chat categories, lines said, speech hue, and its bank role if it has one |
| `[BotBehavior <name>` | GameMaster | Target a bot; switch it (`Idle`, `Traveler`, `BankSitter`, `Shopper`, `Crafter`, `Gatherer`). Switching to `Crafter` or `Gatherer` also assigns the **nearest site or station that bot's class works**, because those two are arrival handoffs and are useless without a destination — a class with nowhere to work is refused out loud rather than silently walking off |
| `[BotLifecycle` | GameMaster | Report the roller, its cadence and the transition tally |
| `[BotLifecycle on\|off` | GameMaster | Pause the roller, so a behaviour can be watched without it being rolled away |
| `[BotWorkScout` | Administrator | Build and verify the road from each authored work site back to the town graph, and write `Data/Live/work-scout.json` for merging into `navigation.json`. An authoring tool, not a runtime one — see above |
| `[BotSiteAudit` | Administrator | What is really under every work-site arrival and corridor waypoint: land id **and name**, statics, walkability, water, and harvestable tiles in reach |
| `[BotOreSweep [radius]` | Administrator | Where dense harvestable ground actually is, per 10×10 cell, with the land names |
| `[BotSitePick <x> <y> [mine\|chop]` | Administrator | The best tiles to stand on near a point, ranked by what is in reach |
| `[BotForgeAudit` | Administrator | Where the anvil and forge really are, and which tiles are within 2 of both |
| *(config)* `Custom.BotWorkProbeOnStart` | — | Run the work probe on its own, without the six minutes of walk, lifecycle and chat probes that precede it in `[BotSmoke` |
| `[BotsReload` | GameMaster | Re-read `bots.json`, the player caps it defaults from, **and the chat corpus** (alias `[ReloadBots`). The corpus reloads either way — a bad edit to one has nothing to do with the other. Also **re-validates the work sites**, so after editing the graph the order is `[NavReload` then `[BotsReload` |
| `[BotSmoke` | Administrator | Spawn one bot per class, check every one against the caps, delete them |
| `[BotTrace on\|off` | GameMaster | Target a bot; echo everything it does to the console. `[BotTrace off all` quiets every traced bot, `[BotTrace list` names them |
| `[BotPace [seconds]` | GameMaster | Target a walking bot; sample its steps for N seconds (default 30) and report the walker tick, the pace it was given, the delay the engine used, the interval between steps and how many carried the running bit |
| `[BotPace auto [seconds]` | GameMaster | The same, with **no target**: takes any bot already walking a route. This is the form a headless run can use, and it is why the token exists — the targeted form was the only one, so the one instrument that reads both the pace given and the delay the engine used was unreachable from a run with no client. Refuses when no bot is walking rather than waiting, because a sampler that waited would report a window that never started as a window with no problems (token: `bot-pace`, body a window length or `last`; answers to `Data/Live/bot-pace.json`) |
| `[BotPopulationAudit` | Administrator | What the recipe would produce for each town, and whether `GG_BotPop.xml` still matches it. Spawns nothing and writes nothing (token: `botpop-audit`) |
| `[BotPopulationGen` | Administrator | Write the recipe to `Spawns/Custom/<facet>/GG_BotPop.xml`. Run `[GG_Reimport` afterwards (token: `botpop-gen`) |
| `[BotPopulation` | Administrator | Live count against the curve target, the tick cost, and how many bot spawners exist |
| `[BotPopulation <n>` | Administrator | Set the target for this session. Says out loud that it is memory only — the file is the record |
| `[BotSessions [on\|off]` | GameMaster | The curve's status, or pin the population where it is |

## Population

**Sessions 1 to 5 gave one bot a life; this gives the world a population.** Until now every bot
existed because `[SpawnBot` or a probe made it. Now the shard boots and Britain and Trinsic are
inhabited: a crowd at every bank, a shopper in every shop, a smith at every forge, and a roaming
remainder that rises and falls with the hour.

This is uo-offline's `[GenerateBots`, `BotPopulation`, `BankFixtures` and `BotSessionManager`, with
one large deviation and several small ones.

### The recipe is derived; the file is generated; neither is written by hand

Upstream's `[GenerateBots` walks a hardcoded nine-city table and **news up a `PlayerBotSpawner`
Item at each computed point** (`GenerateBotsCommand.cs:502-534`). Those Items land in the world
save and become the authoritative record of the population — which is why upstream needs
`[ClearSpawners`, a `StartupCap` "runaway brake" that prints *"you likely have stacked spawners"*
(`BotStartupManager.cs:134-138`), and a `BankFixtures` pass that tops up a spawner whose count was
*"baked in"* on an earlier boot (`BankFixtures.cs:140-150`). Every one of those exists to mop up
the same puddle: a **derived** thing was written somewhere that outlives the derivation.

Here the recipe writes a **file** instead:

```
[BotPopulationAudit     # what it would produce, and whether the file still matches
[BotPopulationGen       # write Spawns/Custom/trammel/GG_BotPop.xml
[GG_Reimport            # turn it into spawners, and fill them
```

`[GG_Reimport` already exists, is already tested, and is already scoped by the `GG_` prefix. So the
population is in git, is drawn in the editor's spawner layer beside every other `GG_` spawner, and
**cannot stack**: a regen replaces by `<UniqueId>`, and the UniqueId is an MD5 of the slot's own
identity rather than a fresh GUID. Nothing here needs a runaway brake because nothing can run away.

Two files, and the split matters: **`GG_BotPop.xml` is generated and the generator owns it
outright**; **`GG_Bots.xml` is hand-authored from the editor's form and the generator never touches
it.** Every generated spawner is named `GG_BotPop_<town>_<kind>_<destination>`, and the diff refuses
to call anything else stale.

### What the recipe is

Read off the same graph the bots read. There is no city column and no coordinate table: a town is a
tag in `bots.json` `destinations.towns`, a bank is a `bank` destination carrying that tag, a station
is whatever `CrafterProfiles` says the class works. **Author a destination in the editor and the
recipe grows; nothing in `BotPopulation.cs` knows the name "Britain".**

| Slot | Role | Rule |
| --- | --- | --- |
| `bank` | **Fixed** | One spawner per bank **destination**, holding `life.crowds.bank` sitters |
| `station` | **Fixed** | One crafter per craft-station **destination**, class forced from the station |
| `shop` | Lifecycle | One shopper per `shop` destination that is not already somebody's bench |
| `roam` | Lifecycle | The town's remaining share, in chunks of `population.perSpawner` |

**Per destination, not per arrival** — a deviation. Upstream pins one crafter per craft-station
*arrival point* (`GenerateBotsCommand.cs:445-466`), which on this graph would put **four smiths at
one anvil**: our forge arrivals are the four tiles within 2 of both the forge and the anvil, which
is a capacity ceiling and not a staffing target. "Staffed by construction" needs one.

**The roamer anchor is a rule over tags, not a table of town names**: `plaza` tag → `wander` type →
`gate` type → the town's bank. Britain resolves at the first stop (`brit-square` carries `plaza`);
Trinsic has none of the first three and lands on `trinsic-bank`. **Author a `plaza`-tagged Trinsic
square and the recipe picks it up with no code change** — and `Bots.Recipe` prints which stop each
town resolved at, so the day that happens it is visible rather than silent.

What it produces today, on this graph:

```
target 60 on Trammel: 60 bot(s) across 37 spawner(s), 17 of them fixed.
  britain: share 38, pinned 19 - 3 bank, 5 station, 11 shop, 19 roam. anchor brit-square (plaza tag).
  trinsic: share 22, pinned 19 - 6 bank, 3 station, 10 shop, 3 roam.  anchor trinsic-bank (bank).
```

### 60, not 1600, and the probe hands you the number to raise it with

Upstream's `TargetCount` is `1600` (`BotPopulation.cs:150`). This graph has **115 arrival points**
in total — Britain 88, Trinsic 27 — and `bots.json`'s own rule is that a site's capacity *is* its
authored arrival count, so 1600 is not a number to inherit unexamined. The port survey's
instruction for it was *"start at a fraction of it and measure"*.

So `BotTickManager` now times its own pass — the `LiveRegistry` sweep, every behaviour's `Tick`,
the lifecycle pass and the session pass, which together are the entire per-tick cost of the bot
layer — and `Bots.Recipe` and `Bots.Population` both report it beside the live count:

```
tick 0.4 ms mean / 13 ms max of 2000 ms over 200 pass(es) at 60 bot(s)
```

Sixty bots cost two hundredths of one percent of the tick budget. **Raising the target is now an
arithmetic question rather than a nerve one**, which is the whole reason the measurement exists.
Upstream measures nothing and ships 1600 behind a comment reading *"with <500 bots this is
trivially cheap"* — two numbers that cannot both be opinions about the same code.

**And the one dial that pretended to raise it has been removed.** `Custom.MeasurementProfile` used
to double `BotSession.TargetNow` — which is the session **ceiling**, how many lifecycle bots may be
*live*, and not how many *exist*. The population is spawner slots in a generated file, so a ceiling
of 120 against 60 authored slots fills all 60 and stops. Window E measured exactly that and wrote it
down: `target 120 now (peak 60)` beside `60 bot(s) live`.

It is gone rather than fixed. Making it real means a switch whose whole contract is that flipping
one line back to `False` undoes it, rewriting a tracked file — and a crash mid-window would leave
the doubled `GG_BotPop.xml` committed. Leaving it in place meant a banner, a health warning and two
READMEs all claiming "population ×2" about a number that does not move, which is worse than a
missing feature: it is a measurement lying about its own conditions. The tick-budget clamp went with
it, because it bounded a multiplier that no longer exists.

The profile still does the two things that worked: **visit windows ÷ 3** and a **flat session
curve**. Raising the real population is the three commands at the top of this section.

Everything is config, in `bots.json`'s `population` block: `target`, `perSpawner`, `spawnerRange`,
`respawnMinutes`, `sessionMinutes`, `curve` and `roles`. **`perTown` is deliberately empty**: with
no entry a town's share is its share of the graph's destinations, which is *the same rule
`BotHomeTowns.Roll` already uses* to decide where a bot is born — so a bot is born where the
population is, and one number cannot drift from the other. Upstream writes both down separately and
they have: its city weights bear no relation to its destination counts.

### The session curve

`BotSession` is `BotSessionManager`. A bot plays for one to four hours, says a `session_goodbye`
line, and vanishes three to six seconds later; its spawner refills the slot minutes afterwards, as
somebody else. A 24-hour curve — upstream's `HourCurve` verbatim, 0.15 at 05:00 and 1.00 at
19:00 — scales the target, and the first pass after a boot stamps *partial* sessions so the
standing population does not all leave in one wave three hours after every restart.

**Fixtures are outside all of it.** A `BotRole.Fixed` bot never re-rolls, never logs out and never
counts toward the target, so the bank crowds and the staffed benches are there at the 05:00 trough
exactly as they are at the peak. That is upstream's rule — *"fixtures are furniture, not sessions"*
— and it is also what makes the economy session's premise true at every hour rather than only at
peak.

### The seed, and why it is properties rather than a spawner subclass

Upstream has two spawner classes and `FixedRoleBotSpawner` declares **no state at all**: the
role-lock is purely a function of the spawner's *type*, read in `PlayerBot.OnAfterSpawn` as
`Spawner is FixedRoleBotSpawner` (`PlayerBot.cs:641`).

**That cannot work here, and the reason is the hinge the whole layer turns on.** This shard spawns
through XmlSpawner2, and XmlSpawner calls `OnAfterSpawn()` at `XmlSpawner2.cs:9337` and only
*then* applies the spawn string's property setters at `:9344`. A bot placed by an XmlSpawner
therefore cannot learn anything from its spawner inside `OnAfterSpawn` — the seed has not been
handed to it yet.

So the seed is five `[CommandProperty]` properties on the bot, set from the spawn string:

```
PlayerBot/Role/Fixed/SeedClass/Smith/SeedHome/trinsic/SeedStation/trinsic-forge/Seed/Crafter
```

Each setter records its value and queues **one** deferred apply, so the fields are order-free —
which matters, because the class has to be re-derived before the brain attaches (a Crafter's
`OnAttached` reads `TradeClass` to pick its profile) and nothing should depend on an author getting
two fields the right way round. It is also strictly more useful than a spawner subclass: it works
from `[set`, from `[props` and from `[SpawnBot`, none of which have a spawner at all.

**The separator is a slash because a colon cannot appear in an `<Objects2>` entry**: XmlSpawner
splits on `:MX=` and its ten siblings and silently *discards* an entry containing one
(`XmlSpawner2.cs:12701-12704`). That is why upstream's own `"Crafter:Smith"` convention could not
be carried across.

Applying a seed owes exactly what a hand switch owes — the visit window, the station and the phase
clock, the three obligations `BotCommands.TryHandSwitch` writes out — **minus the window for a
fixture**, because a window that lapses hands the brain back to a Traveler and would walk a smith
off its forge three hours into the shift.

### `[Constructable]` is load-bearing, and its absence is silent in both directions

**Thirty-seven spawners imported cleanly, ran for eight minutes and produced no bots at all.**

XmlSpawner will not construct a type whose constructor is not marked `[Constructable]`:
`CreateObject(type, itemtypestring)` defaults `requireconstructable` to true
(`XmlSpawner2.cs:11263-11265`) and `IsConstructable` is a bare attribute test (`:2283-2286`).
A spawner pointed at an unmarked type sets `status_str` to `"invalid type specification"` and
**returns `true`** — so the spawner reports success, its timer keeps rescheduling, and its count
sits at zero for ever with nothing in any log.

`PlayerBot`'s constructor now carries it. **And so does `VocabularySnapshot`'s filter**, which
previously excluded the attribute with a comment saying it *"gates the `[Add` command, not the
spawner"* — true of ServUO's stock `Spawner`, false of the only spawner this shard uses. The
editor's Type dropdown was therefore offering hundreds of types a spawner would silently refuse.
Both halves of that were the same bug from opposite ends.

### Boot

Upstream's `BotStartupManager` does two things: it sweeps `World.Mobiles` deleting stale bots, and
it forces every spawner to `Respawn` *"rather than waiting up to 15 minutes"*. **Only the second
half is ported**, at `EventSink.ServerStarted` rather than `Initialize` — the same reason
`BotSystem` already defers `[BotSmoke`, that spawning during `Initialize` puts mobiles in the world
before the map and the regions have settled.

The first half is already done, better, and elsewhere: `PlayerBot.Deserialize` ends in
`Timer.DelayCall(Delete)`, so every bot deletes itself on load however it got into the save. For
upstream that sweep is a mop for orphans, because ModernUO never wrote accountless bots at all; here
it is the mechanism, because ServUO writes every mobile in `World.Mobiles`.

A `[GG_Reimport` and a per-file spawn reload fill the bot spawners too, for the same reason the boot
does: a reimport that leaves the towns empty for a quarter of an hour reads as a reimport that did
not work.

### `Bots.Recipe`

Every other bot probe **spawns** what it measures. This one measures nothing of its own, because
the thing under test is the population itself — it is already standing there, and a probe that
spawned its own copy would be testing a copy. So it is a plain health check with no cleanup and no
window, which also means it runs on every `[CoreSmoke` rather than only inside the six-minute chain.

Four questions, in the order they are worth asking:

1. **Does `GG_BotPop.xml` still say what the recipe says?** A recipe is derived and a file is not,
   so authoring a destination changes the recipe's mind while the file — and therefore the world —
   goes on saying the old thing, for ever, silently. Nothing else would notice. It reports
   **REGEN NEEDED** with what changed.
2. **Is every staffed bench actually staffed?** Using `BotDestinations.IsStaffed`'s own predicate,
   so the probe cannot pass while the economy's haul roll disagrees. When one is not, it says
   **where that crafter went instead** — nine times in ten it is working at the other bench of its
   trade in the same town, because `CrafterBehavior.TakeUpStation` moves to one when reach at the
   authored station holds no standable tile, which means the fault is in the arrival point rather
   than in the bot.
3. **Did any fixture spawner quietly fail to place its bots?** XmlSpawner drops a spawn it cannot
   position and says nothing. Lifecycle spawners are exempt: one sitting under its count whenever
   the curve is below peak is the curve working.
4. **Are the towns within 25% of their share, and what does a tick cost?** The share is measured
   against the *curve's* target, not the peak, or every night would report as a fault.

Its first real run found `trinsic-shop-tailor-2`, and the first diagnosis of it — recorded here —
was **backwards**. It read: the shop stands at Z 35 and its arrival is twenty below the floor. The
truth is the other way round. The shop floor is **Z 15**; the arrival's Z was right all along and
its **X,Y** was inside a display case, and the *destination*'s stored Z 35 was the wrong value. The
tile at `1982,2832` has two standable storeys, 15 and 36, and the record was pointing at the upper
one.

Fixed: the arrival moved to `1987,2841` — open floor, eleven tiles from `uo-wp-170` and so inside
the hop cap — and the destination dropped to Z 15. The tailor clocks in.

**The lesson is about the resample, not the record.** `[NavResampleZ` *confirmed* Z 36 rather than
correcting it, because `TryResolveZ` searches a window around the stored Z and 36 is a real
standable surface there. **A hint that is wrong by more than the window is not detected — it is
ratified.** The resample can find a Z that has gone stale under a record; it cannot find a record
that was authored on the wrong storey to begin with. Only a person can, which is why the `z?` badge
draws against the pick map instead: that answers "is this marker where I think it is", which is the
question a wrong storey actually fails.

## The event log

Every bot keeps its last **50** events in a ring buffer (`BotLog`), and the whole table is written
to `Data/Live/botlog.json` beside `entities.json` — on the same timer, so a bot the editor is
drawing always has a history to show. Events are one of seven kinds: `behavior`, `route`,
`arrive`, `rung`, `clock`, `work`, `speech`.

**Every behaviour change now says why, at `Info`.** One line, one format:

```
[04:34:58] [INFO ] [Bots] Hal of Vesper Traveler -> Gatherer (hand switch) @ 1449,1521
```

The reason comes from `PlayerBot.SetBehavior(behavior, reason)`, which is the **single choke
point** for a brain change — the `Behavior` property setter is now a thin wrapper on it that
passes `null`, so a call site that has not been told what to say reads `(unspecified)` rather than
lying. The reasons in use are `lifecycle roll`, `arrival handoff`, `visit expired`, `hand switch`,
`no work zone`, `combat`, `walk-in gave up`, `shift over` and `probe setup`.

It is logged **before** `OnAttached` runs, because `OnAttached` is entitled to change the
behaviour again — `GathererBehavior` walks a bot with no work zone straight back to Traveler from
inside it. Logging afterwards recorded those two changes in the wrong order and lost the one that
explained the other.

### Why this exists

A Gatherer that cannot get inside its site gives up after 75 seconds and walks away, and the
**only** trace of that was a `Log.Debug` line — dropped entirely unless the shard runs with
`-debug`. Nothing counted it and no health check saw it, so from outside it was indistinguishable
from a bot that had simply chosen to go somewhere else. That failure now logs at `Info`, records a
`clock` event, and increments `BotTickManager.GaveUpTotal`, which `Bots.Work` reports.

### Two deliberate choices

- **A deleted bot keeps its log.** Deletion is usually the last and most interesting thing in it.
  The ring is keyed by `Serial` rather than by the mobile precisely so it can outlive it without
  pinning it, and the table is bounded by **recency** (64 bots) rather than by liveness.
- **The ring is not the console.** Everything goes in the ring; only behaviour changes are spoken
  aloud, and only a bot under `[BotTrace` says all of it. Twenty bots produce a few hundred events
  a minute, which is a useful file and an unreadable console.

`NavWalker` gained an optional `RungFired` callback beside `Arrived` so a stuck-recovery rung
lands in the log. Navigation is Core and knows nothing about bots, so the subscription goes the
other way — the same inversion `NavigationSystem.RegisterAuditor` already uses for the data
checks — and a subscriber that throws is caught and logged rather than being allowed to break the
recovery ladder it is watching.

## `[BotSmoke`

The EJ-caps equivalent of upstream's T2A audit rig, and the check that the caps genuinely became
configuration rather than moving from one set of literals to another.

It spawns one bot per rollable class **at Grandmaster** — the tier that spends the whole stat budget
and sits closest to every cap, so if any tier breaches, that one does — and asserts: every skill
within the per-skill cap, the total within the skill total, each stat within the per-stat cap, the
stat total within budget, a name, a backpack, something worn, `Player` set, and `InitialInnocent`.
Then it deletes them. A skill-total failure names every skill and its value, because a total that is
over cap is only actionable if you can see which template produced it.

It then runs the **party probe**: a throwaway `PlayerMobile` leader invites a bot, and the bot must
be a full member before the decline timer would fire. This is the one part of the layer that cannot
be checked synchronously — a bot answering instantly is the bug, not the fix — so the probe
schedules its own assertion and reports through `Bots.Party` when it lands. With the accept hook
disabled it fails with exactly the symptom it was written for: *"still not in a party 7s after the
invite (bot.Party is PlayerMobile). The DeclineTimer will refuse it at 30s."*

ServUO's console cannot invoke staff commands, so set `Custom.BotSmokeOnStart=True` in
`Config/Custom.cfg` and read the console. The result also shows up in `[CoreSmoke` as `Bots.Smoke`.

`Custom.BotSmokeKeep` (default 0) leaves that many audited bots standing instead of deleting them.
It is a verification aid, not a feature: checking that a bot reaches the live map, and that it does
*not* survive a restart, both need a bot alive when the snapshot is written and when the world
saves, and there is no way to spawn one from the console.

## Health

`Bots.Population` — live count, the class and tier spread, and always the caps clause. It reads
`LiveRegistry`, never `World.Mobiles` (CLAUDE.md §15), and `NamePool.InUseCount` is an O(1) census
of claimed names. A claimed name with no live bot behind it is reported as a warning: it is a leak
in the count the population manager will later depend on.

`Bots.Smoke` — the last audit result, or a note that it has not run this boot.

`Bots.Party` — the last party-probe result. Separate from `Bots.Smoke` because it completes several
seconds later, and folding an asynchronous result into a synchronous one would mean either blocking
the audit or reporting a result that had not happened yet.

`Bots.Travel` — the last five-traveller walk probe. See below.

`Bots.Life` — the last twelve-bot lifecycle probe.

`Bots.Chat` — the corpus: files, lines, and the wired / reserved / unknown ledger, plus how many
lines have actually been said since boot. **Fail** if the corpus is missing or nothing is
speakable — mute bots have no other symptom. **Warn** for an unrecognised token (a typo, and
therefore a line that can never be spoken), a rejected `*` line, or a category a behaviour draws
from that loaded nothing.

```
corpus 123 file(s) + 22 gossip held back, 1224 line(s): 1060 wired, 164 reserved
(adventurer 14, economy 146, taming 4), 0 unknown. 106 categor(ies) loaded.
spoken since boot: 3 (0 repl(ies)). Last load 06:29:43Z
```

`Bots.Work` — the working-class census: work sites with occupancy against capacity, crafters at
station, gatherers out and hauling, **units mined**, loads delivered, pack animals
live/reaped/released. **Warn** for a site excluded at load, a crafter blocked at its bench, a
`capacity` entry naming a destination that is not there, or a class whose station the graph does not
contain.

That last one is a **Warn and not a Fail**, which is a correction: the Fisherman has no `dock`
destination because the fishing half is a deliberately severed seam, and failing a health check for
a planned gap is how people learn to ignore health checks. Lumberjack joins it until the southern
wood gets its corridor.

**Units mined is reported separately from units delivered on purpose.** They diverge for exactly one
reason and it is the reason this whole session had to be redone: a gatherer spawns holding 3–15 of
its own good, so it can deliver a full load having mined nothing. Delivery proves the walk; mined
proves the work.

```
sites: brit-forge 1/4, brit-lumber-nw 0/4, brit-mine-north 1/4, brit-mine-south 0/4.
1 crafter(s) at station (0 dry, 0 bench-full, 3 made since attaching), 1 gatherer(s) out
(1 working, 0 walking in), 0 hauling. 2 load(s) delivered, 71 unit(s). 1 pack animal(s)
live, 0 reaped, 1 released.
```

`Bots.Shift` — the last work probe. Separate from `Bots.Work` for the reason every probe result is
separate from its live census: it lands four minutes later.

`Bots.Speech` — the last chat probe. Separate from `Bots.Chat` for the same reason `Bots.Party` is
separate from `Bots.Smoke`: it lands seconds later, and folding an asynchronous result into a
synchronous one means reporting something that has not happened yet.

The population line carries the recovery counts too:

```
6 bot(s) live (5 travelling, 0 lingering, 1 idle; 6 name(s) claimed) - 1 Archer, 1 Healer, ...
recovery: repath 6, sidestep 1, door 0, skip 0, teleport 0. caps: from PlayerCaps.cfg ...
```

Those totals are fleet-wide and **reset on `[NavReload`**: the counts describe a graph, and after an
edit they would otherwise describe two.

### The life probe also guards the hand switch

One of the twelve bots is switched to `Idle` **through the real `[BotBehavior` code path** —
`BotCommands.TryHandSwitch`, which the command and the probe now share rather than each having
their own copy. The probe then asserts the bot still holds that brain after
`HandSwitchPasses` lifecycle passes.

The fault it guards against already shipped once. Setting `Behavior` alone left `PhaseStartedAt`
untouched, so the roller's next pass asked *"is this bot's phase over?"*, got yes, and rolled the
switch away within seconds: every `[BotBehavior` appeared to work and then quietly undid itself.
Nothing exercised that path, which is the whole reason it survived.

Three details that are not incidental:

- **`Idle`, specifically.** It takes no visit window, so the phase clock is the only thing
  protecting it — and the phase clock is exactly what broke. A behaviour *with* a visit window is
  skipped by the roller outright, so the assertion would pass without testing anything.
- **Passes, not seconds.** A wall-clock assertion would really be an assertion about
  `Custom.BotLifecycleSeconds`, and would start passing for the wrong reason the moment somebody
  slowed the cadence. `BotLifecycle.PassCount` counts passes that actually ran.
- **The probe's cadence dropped from 10s to 5s to make two passes safe to assert.** A probe run's
  phase is *exactly* 30 seconds — `BotPhaseClamp.Apply` clamps the personality's 30–180 **minute**
  average down to `maxSeconds`, so the minimum never comes into it — and at 10s two passes was 20s,
  uncomfortably close. At 5s it is 10s.

**And the first run of this assertion failed, on a system that was working correctly.** Worth
recording, because the fix was in the lifecycle rather than in the probe:

```
a hand switch did not survive the roller - Myrwina was switched to Idle by hand
and the roller had it as Traveler 2 pass(es) later
```

The switch was at 06:24:00 and the roll at 06:24:36 — **thirty-six seconds later, against a
thirty-second phase**. Entirely legal. The trouble was that *"two passes" was not a bounded amount
of time*: `BotLifecycle.IntervalOverride` set the new five-second cadence but left `_nextPass`
holding the deadline the previous **sixty**-second cadence had already scheduled, so the first pass
did not arrive for another half-minute. Setting the override now brings the next pass forward — an
override that does not take effect until the thing it overrides has finished is not really an
override — and the sample records elapsed seconds alongside the pass count, because with only
`2 pass(es) later` in the message there was nothing to tell a real fault from this one.

It is also part of the early-exit condition rather than only the report: churn can be satisfied
inside twenty seconds, and exiting on churn alone would end the run before the hand-switched bot
had been looked at once — which is how a probe grows an assertion that is never evaluated.

## The five-traveller walk probe

Runs with `[BotSmoke`, reports through `Bots.Travel`.

It exists because rungs 2 to 5 of the recovery ladder went unverified when the ladder shipped — a
lone walker recovers at rung 1 essentially every time, since A* just routes around one obstruction.
**Contention is what reaches the deeper rungs**, so the probe manufactures it: five bots are sent to
one destination at once so they compete for the same arrival tiles, then dispersed.

**Pass is "nobody stuck, and no `Teleport` rung fired."** Rungs 2 to 4 firing is *information* — a
sidestep that worked is the ladder doing its job. The rung line prints either way, because a run
where nothing escalated is worth knowing about too: it means the contention did not bite.

**It crowds the WORST arrival the graph has, not the busiest one.** A bank plaza is a crowding
test - five bots competing for four open tiles - and it says nothing about the last hop, because a
bank arrival is a tile in the open that anybody can walk to. Nineteen authored arrivals on this
graph are tiles nothing can stand on: a counter, a brick wall, a table, an oak tree. A bot sent to
one has to retarget inside the arrival range or it cannot finish the walk at all, and that was 7 of
17 terminal failures in one measured window while this probe reported clean.

`PickBusiest` therefore reads `NavResampleZ.Scan()` and prefers a destination with an unstandable
arrival, falling back to the bank when there is none. **Read off the live scan rather than
hard-coded**, because the point of fixing an arrival is that it stops being on that list - a probe
naming a destination by hand would go on testing a case somebody had already repaired while missing
whichever one broke next. It says which it chose and why:

```
Walk probe: crowding 'brit-bank', whose arrival at 1432,1692 is unstandable - brick wall.
```

**"Stuck" is the walker's own answer**, `NavWalker.CurrentRung != None`, not a guess from outside.
The first version of this probe called a bot stuck if it was still walking when the window closed,
and duly reported five stuck bots that were simply getting on with it — a Traveler that arrives
lingers and then departs again of its own accord, so "still walking" is the *normal* steady state.

## The walk-in, and three ways it was broken

A Gatherer sent to a site from anywhere except the site itself did not work. It either turned back
into a Traveler within a tick, or walked most of the way, spawned its pack beast, and then stood
outside reporting *"standing outside a work site"* until it gave up seventy-five seconds later.

One symptom, three independent faults. Worth separating, because two of them were invisible and the
third was mis-attributed to the map.

**It was NOT the slope.** The obvious suspicion is that the site zone does not contain the arrival
tiles as the ground climbs from z 32 to z 45. It does. `NavZone.Contains(x, y)` never consults Z at
all, and all five `brit-mine-north` arrivals sit at least four tiles inside the rectangle — further
in than `Custom.NavArrivalScatter` can push them.

### 1. A hand switch handed over the brain but not the destination

`Crafter` and `Gatherer` are arrival handoffs: the Traveler passes the destination across at the
same moment it passes the brain across. `[BotBehavior Gatherer` passed only the brain, so
`DestinationId` was null, `ResolveSite` fell through to `Nav.Destination(null)`, and `OnAttached`
walked the bot straight back to Traveler. Typing the command appeared to do nothing.

That is also why the rest stayed hidden: **nobody could hold a gatherer still long enough to watch
it fail.** `BotWorkSites.NearestSiteFor` now supplies the missing half, and a class with nowhere to
work is told so instead of silently walking away.

### 2. Clocking in and staying clocked in asked different questions

`OnWalkedIn` clocked in on zone containment alone. `CanClockIn`, which decides whether the bot
*stays* clocked in on the very next tick, demands containment **and** something within harvest
reach. So a bot that landed on a thin tile clocked in — spawning its pack beast, which is what made
the failure look like a success — un-clocked one tick later, and asked to walk in again.

The re-walk cannot help, and this is the heart of it: **the route ends at an arrival point of a
destination the bot is already standing in**, so the walker finishes without taking a step. What was
needed was not a route across town but a few steps sideways, and nothing could take them —
`StepAlongTheFace` is reachable only from `Swing`, which runs only *after* clocking in.

`SeekReach` is that missing step: standing inside the site with nothing in reach, sweep
`SeekRadius` tiles for the nearest one that has something, and walk to it one tile per tick. Every
candidate passes the same three tests a shuffle does — inside the zone, standable, and still able to
route home — so it cannot go anywhere `StepAlongTheFace` would refuse.

This one was **not confined to the walk-in.** The event log caught the work probe's own miner —
spawned directly on a good tile, in a run that passed — clocking in twice in one shift:

```
clock  clocked in at 1447,1521 in 'brit-mine-north-face', reach 5
clock  clocked in at 1450,1520 in 'brit-mine-north-face', reach 5
clock  shift over - 21 swing(s), 17 mined, carrying 17
```

It had shuffled onto a zero-reach tile and had to walk back in. Every shift was paying this.

**And `SeekReach` did not stop it being announced as a shift.** What that fix corrected was the
walk-in asking a different question from `Tick`; it left `_clockedIn` meaning "has tiles in reach
right now", which `Tick` clears on every reach loss and sets again on every reach regain. So the
bot went back to work, which is what `SeekReach` is for, and said "clocked in" again each time it
did. A later session found one miner logging four clock-ins in a shift (reach 3, 12, 4, 3) and
another logging eight, some two seconds apart, none of which had left the site.

The shift now has a latch of its own, keyed on `VisitExpiresAt` rather than held as a plain bool:
a new `GathererBehavior` is usually a new shift, but the arrival handoff can carry an existing
window onto a fresh instance and a stuck-recovery teleport can re-arrive mid-shift, and an
instance bool would call both of those new shifts. Losing and regaining reach now logs
`lost reach` and `back on the face`, so the oscillation stays visible without being counted as a
shift it is not. `GathererBehavior.ClockIns` counts them and the work probe asserts exactly one -
the probe previously sampled `IsWorking` once, twelve seconds in, which cannot tell one clock-in
from eight, which is why it passed with the log above sitting in the same run.

### 3. The give-up clock was racing the walker's recovery ladder

`NavWalker.ArrivalRangeFor` returns **0** for a `NavStepKind.Arrival` — the last tile of a route
must be hit exactly — and the recovery ladder is five rungs at `HopTimeout` apiece, so a contended
one-tile approach can legitimately take 80 to 100 seconds to land. Against a flat 75-second budget
the give-up fired first, and `Release` then called `_walker.Stop()`, which drops the route with no
`Arrived` callback: **the recovery that was about to succeed was thrown away.**

Observed on a live run, on the forge arrival rather than the mine:

```
[WARN] [Nav] Enid Ashdown could not walk 'brit-smith-1' -> '(arrival)' after the whole recovery
             ladder with nobody watching; it was moved. Check that edge.
```

The deadline now stops running while the walker is active. That is not the same as removing it —
the walker has its own abandonment path, watched by the branch below it — so what remains bounded is
the time spent *not* walking, which is what the timeout was always meant to bound. The budget is
`Custom.BotWalkInSeconds`.

And the give-up now logs at `Info` and increments `BotTickManager.GaveUpTotal`, which `Bots.Work`
reports. Previously it logged at `Debug` and counted nothing, which is how three faults shared one
symptom for a whole session without anybody being able to tell them apart.

## The work probe

Runs with `[BotSmoke`, last of the four, reports through `Bots.Shift`.

It asserts the **whole loop**, not a piece of it: a Miner goes out, mines real ore off a real rock
face, walks the load back to town, hands it to a Smith, and the Smith turns it into at least one
crafted item. Every one of those steps is somewhere the session could be quietly broken while each
part looked fine on its own — a site with no ore, a corridor that does not route, a delivery that
matches the wrong trade, a forge the bot cannot reach from its own arrival tile — and none of them
would show up in a check of any single piece.

**There are two miners, and the second one is the point.** The first is placed directly *on* a
picked arrival tile, so it clocks in on its first tick and never exercises the walk-in at all. The
second starts at the **bank** and has to route out of town, get inside the zone, find something in
reach and swing at it. Every fault described in [The walk-in](#the-walk-in-and-three-ways-it-was-broken)
lived entirely in the path the first miner skips, which is why all of it survived a probe that was
otherwise asserting the whole loop. Swinging is the bar, not arriving: a bot standing on a thin tile
at the edge of the face arrives perfectly well and then mines nothing for its entire shift.

It runs on an **accelerated clamp installed in memory only**, the same discipline `BotLifeProbe`
keeps: a real shift is 4–8 minutes and a real crafter settles in for 3–6 *hours*, which is right for
a shard and useless for a probe.

**Both bots are stripped of their spawn kit first**, and that is the assertion rather than a detail.

`EquipmentTable` seeds a fresh smith with 40–90 iron ingots, and a fresh **miner with 3–15 IronOre**
— *"a working stash from the last shift"*. Either one is enough to make the whole cycle look like it
worked when nothing did:

> The probe was fixed once, for the smith, and shipped still broken for the miner. It then passed
> against mine faces that had no rock on them at all — the miner walked its spawn kit to the forge,
> the smith smelted that and crafted, and every assertion the probe had was satisfied. **That is
> how four green checks hid a feature that had never once mined anything.**

So the probe now asserts what it is actually about:

| Assertion | Guards against |
| --- | --- |
| `BotWorkSites.Mined` rose | the miner hauling its spawn kit and mining nothing |
| the miner was inside its site zone, working, at 12s | clocking in on a bridge |
| a load was delivered and `HaulPending` cleared | the walk home |
| the smith stood on a **validated station arrival tile** | crafting somewhere that merely had an anvil |
| the smith made an item | the trade |

`Mined` is counted by watching the pack grow, because `HarvestSystem.Give` drops ore in
asynchronously a second after the swing — the same way `CrafterBehavior` notices finished goods.

(The earlier strip used `ConsumeTotal(typeof(IronIngot), Int32.MaxValue)`, which is all-or-nothing:
it consumed *nothing*, returned false, and left the smith fully stocked. It deletes the items now.)

A **full bench is not a failure**, and separating that from a real fault was the other thing this
probe taught. `CrafterBehavior.Blocked` means something is wrong — no tool, wrong tool, not beside
an anvil. `IsFull` and `IsDry` mean the system is working and the crafter has nothing to do just
now. Folding them together once failed a run in which everything had gone right.

A passing run reads:

```
Bot work probe PASSED - a full cycle completed - miner ended as Traveler,
1 load(s), 16 unit(s) delivered, the smith made 2 item(s) in 3 attempt(s)
```

The Smith is **placed** at the forge rather than walked there. That half of the journey is the
Traveler's and `Bots.Travel` already proves it; spending two minutes of the window on a walk that is
covered elsewhere would only make this flakier.

### The phase clock was never started, and it broke every hand switch

`[BotBehavior Crafter` on a bot was overwritten within seconds, every time. `[BotInfo` said why, in
a number nobody read as a fault:

```
Phase Crafter: 63924344915s of 14160s elapsed, 0s left
```

Two thousand years. `PlayerBot`'s constructor assigns a `Personality` but **never assigned
`PhaseStartedAt`**, so it sat at `DateTime.MinValue` — and `BotLifecycle`'s "first sight" branch,
which starts the clock, only fires for a bot whose personality is *unassigned*. It therefore never
fired for anybody. Every bot was permanently overdue for a lifecycle roll from the moment it
spawned, and anything you set by hand was rolled away on the next pass.

Three changes, at three levels:

1. **The constructor starts the clock.** That is the actual bug.
2. **A hand switch is a transition.** `[BotBehavior` now sets `PhaseStartedAt`, clears
   `TransitionPending`, and gives visit behaviours their proper window — the same window the arrival
   handoff gives them, which now lives in `BotBehaviors.VisitWindowFor` so the two cannot drift. It
   reports what it did: *"Eamon is now Crafter for 291 minute(s)"*.
3. **`[BotInfo` refuses to print nonsense.** An unset clock is named — `Phase Crafter: CLOCK UNSET -
   this bot is permanently overdue` — and an elapsed time is clamped to its phase. A silly number
   reads as a display glitch; a named one reads as a bug.

And because it should now be impossible, `Bots.Population` **counts bots with an unset phase clock
and warns**. That is the deliberate-failure check for this class of fault: it hid for three sessions
behind a number that looked like a rendering error.

It was not only cosmetic. The life probe went from 15–21 brain changes to **26 with 6 lifecycle
transitions**, back in line with the pre-7e baseline — bots now serve a real first phase instead of
being rolled the instant they are born.

### A legacy Crafter has a sub-type, and everything must ask for it

`BotClass.Crafter` is one class with a `CrafterSpec` behind it — Smith, Tailor or Fisherman. Both
`StationFor` and `CrafterProfiles` are keyed on the *real* trade classes, and `Crafter` is not one
of them, so a bot reporting *"Class Crafter, tier Adept"* standing at the forge was told it had **no
station on this facet** and given a null profile.

`PlayerBot.TradeClass` resolves it — identical to `Class` for everyone else — and the bot-aware
overloads `BotClassHelper.StationFor(PlayerBot)` and `CrafterProfiles.For(PlayerBot)` use it. Prefer
those wherever a bot is in hand. `[BotInfo` now prints the sub-type, because *"Class Crafter"* alone
hides the very thing that decides where it works:

```
Sub-type Blacksmith (works as Blacksmith).
```

The failure message was wrong in the same way and is now specific: a Miner handed a `Crafter` brain
is told **"Miner has no crafting station"** rather than the misleading *"no station on this facet"*,
which suggested the graph was at fault when the class was.

### Probes report when they are done, not when the clock runs out

Every probe used to sleep its whole window through a one-shot `Timer.DelayCall(Window, Report)`,
whether or not the thing it was testing had finished in the first thirty seconds. Measured across
three consecutive chains, each ran its configured window to within two seconds — walk 90/92/92
against 45+45, life 181/183/181 against 180, chat 92/92/90 against 60+30, work 424/426/423 against
420. **789 seconds of window per chain**, of which the work probe was 54%; and in the run that
passed, the smith had crafted its item about two minutes in.

So probes poll now and report the moment their assertion holds; the window became a **timeout**
rather than a sleep, and `[BotSmoke`'s chain advances **on completion** rather than on a stopwatch —
waiting a fixed offset between probes would have thrown the whole saving away.

**A knob to divide the world's clocks was considered and rejected.** Scaling `NavWalker`'s move
delay, the harvest swing interval and the craft delay would have saved *nothing* on its own, because
the window was the cost rather than the work — and it would have made the probes test timings the
shard does not run. Production timings are the point.

Two stages deliberately keep their full window, and it is not an oversight: the chat probe's 30s
**silence** stage and the walk probe's 45s **disperse** stage are *absence* assertions — "nobody
spoke", "nobody got stuck" — and absence cannot be established early.

Every report now carries **elapsed against timeout**:

```
Bot work probe PASSED in 2m04s of 7m00s - a full cycle completed - 12 unit(s) actually mined ...
```

That is as much the point as the speed. A work probe that passes in 2m04s and later passes in 6m50s
has regressed, and the old reports could not show it.

Measured over a full chain afterwards: **8m17s against 13m35s**, and the honesty of the numbers is
worth as much as the saving —

```
Bot party probe PASSED - invite accepted in 4s of 7s
Bot walk  probe PASSED - in 1m32s of 1m30s      <- never exits early; bots were still travelling
Bot life  probe        - in 3m04s of 3m00s      <- never exits early; churn bar not met
Bot chat  probe PASSED - in 34s of 1m30s
Bot work  probe PASSED - in 2m59s of 7m00s
```

Almost all of it is the work probe. The walk and life probes ran their windows out because their
conditions genuinely were not met early — which is the point of printing elapsed against timeout
rather than assuming the saving applies everywhere.

### The probe leak, which was real

`BotWalkProbe.Report` and `BotLifeProbe.Report` were not wrapped, and both do substantial work —
walker and rung accessors, `BotCrowds.BelowFloor`, list indexing — before their single `Finish` at
the end. A throw anywhere in there skipped `Finish`, which left `_running` true for the life of the
process **and left `BotLifecycle.Override` and `IntervalOverride` installed**: a shard stuck on a
ten-second lifecycle cadence and 15–30s phase clamps, silently, until restart.

That was live, and it is exactly the failure `BotLifecycle`'s own comment claims to prevent —
*"Held in memory rather than written to bots.json so a probe that dies mid-run cannot leave the
shard permanently accelerated."* The in-memory half was done; the always-restore half was not.

Both are now `try` / `catch` into a failing `Finish`, plus a `finally` that clears the overrides
unconditionally. Catch rather than a bare finally because a logged failure becomes a red health
check somebody sees, where a rethrow into a `Timer` callback becomes a console line nobody reads.

### What the work layer did to the lifecycle probe

`Bots.Life` counts a bot as having done something by its **brain changes**, and step 7e broke that
proxy — for the second time, in the same direction.

The probe first asked the phase clock, which does not move on an arrival handoff, and reported busy
bots as idle. Brain changes fixed that. But a work site is a two-to-four minute walk each way, which
is longer than the probe's whole 180-second window, so a Miner that spent it walking to a mine
registers as one brain change and reads as a bot that never did anything — when in fact it did
precisely what it was told.

So a Traveler walking to a **work destination specifically** is now excused with a note, the same
way a wedged one already was. Deliberately narrow: "still walking" is a Traveler's normal steady
state and excusing it wholesale would blunt the probe to nothing — the walk probe learned that one
already. The unchanged line names the bot's class now too, because "Sif never changed behaviour"
tells you nothing and "Sif never changed behaviour, a Miner" tells you everything.

**Be careful reading anything into a single run of either probe.** Both looked like 7e regressions
and neither was. Measured against the pre-change tree rather than guessed at:

| | this tree | pre-7e |
| --- | --- | --- |
| walk probe, 4 boots each | 1 warn | **3 warns** |
| life probe, 3 boots each | 1 fail | 1 fail |
| brain changes per life run | 21, 15, 25 | 26, 25, 23 |

Both probes are intermittently red on **both** trees. The walk probe manufactures contention on
purpose and then asks whether anybody is still recovering when the music stops, so a warn is close
to a coin toss; the life probe inherits that directly, because a wedged Traveler cannot transition
and a run full of wedged bots is a run with low churn. The first baseline run happened to come back
clean, which is how a one-run comparison turns noise into a regression.

The **brain-change** column is the one real difference, and it is the feature rather than a fault: an
artisan that reaches its bench settles there for three to six hours, and a gatherer spends minutes
walking each way. Both mean fewer brain changes per 180 seconds — about four or five of them across
twelve bots, which is what ~1.7 work-class bots in a random dozen costs. The probe's churn metric
simply does not model a bot whose job is to stay put.

## Known simplifications

1. **The behaviour name is serialized but not restored.** There is no registry to construct a brain
   from a string yet, and a bot is deleted on load regardless.
2. **A crafter's ledger is counted, not tracked.** Upstream kept a `List<Item> _made`;
   `CrafterBehavior` counts pack items whose type is in its own bands instead. That is simpler and
   nothing goes stale when an item is moved or destroyed — but it means a starter prop the bot was
   spawned holding counts as something it made, which is exactly the fiction those props exist to
   sell, and it cannot tell two identical daggers apart.
3. **`Craft` is asynchronous and nothing waits on it.** The item appears ~1.25s later in
   `CompleteCraft`, so the behaviour learns what happened by looking at the pack on its next tick.
   A craft that fails and one that was never attempted look the same from outside; `Attempts` and
   `Made` are reported separately so the gap between them is visible.
4. **A dry crafter stays dry.** With no gold there is no counter restock, so a Smith with no Miner
   supplying it stands at a full forge saying "need iron ingots" indefinitely. That is the intended
   picture this session, not a bug — but on a shard with no gatherers alive it reads as a stuck
   bot.
5. **A bot only ever talks to the room.** It has no memory of a conversation, so it cannot be
   asked a follow-up: the second question gets another line from the same pool, not an answer to
   the first. That is deliberately upstream's shape too — these are passers-by, not quest NPCs.
6. **The curve's way up costs one wasted construction per refusal.** Upstream refuses a spawn
   *before* construction, in an override of `Spawner.Spawn`; XmlSpawner offers no such hook, and
   nothing knows whether a spawned bot is a fixture or a session until its seed has been applied —
   so the bot is built and then turned away. A bot spawner ticks every five to fifteen minutes, so
   it is a handful an hour at the trough and none at the peak. It is the price of having one
   spawner mechanism on this shard rather than two.
7. **A hand-authored *fixed* Gatherer would still leave its site.** `GathererBehavior` stamps its
   own visit window if it finds none (`GathererBehavior.cs:217-219`), and `CheckVisitExpired` now
   refuses for an exempt bot — so the invariant holds. But nothing pins a gatherer in the recipe
   (a gatherer's site is a place it travels *to*, and pinning one there would break the haul loop),
   so that path has never actually run.
8. **`[BotWhere` and `[BotGoals` were not ported.** Upstream's two population-inspection commands
   answer "where is everybody" and "what are they all doing"; the editor's live map and its bot
   card already answer both, continuously and for every bot at once, which is why they were
   skipped rather than translated.

### Later

- **THE POPULATION SCALE TEST, READY TO RUN AND DELIBERATELY NOT RUN.** *Deferred by Sean until
  after the bot-class spike, because a `PlayerMobile` rewrite would change what the curve measures.
  Everything it needs is shipped; this is the procedure, so the next session does not re-derive it.*

  `bots.json` ships `target: 60` against a graph with 115 arrival points, chosen as *"a fraction of
  upstream's 1600, and measure"* — and the measurement was never taken. `BotTickManager` reports
  **0.4 ms mean / 13 ms max against a 2000 ms budget at 60 bots**, two hundredths of one percent, so
  the headroom is large and unquantified.

  **`Custom.MeasurementProfile` stays OFF throughout.** It changes crowding, and crowding is part of
  what is being measured.

  At each of **100, 200, 400**: raise `population.target` in `Data/Custom/bots.json`, run
  `[BotPopulationGen` → `[GG_Reimport` → `[BotPopulationAudit`, let it settle, then **10 minutes**.

  | what | where it comes from |
  | --- | --- |
  | behaviour tick mean / max | `BotTickManager.DescribeCost()`, on `Bots.Recipe` and `Bots.Population` |
  | does the pace hold | **`[BotPace auto`**, or the `bot-pace` token — see below |
  | shard cycles per second | `Core.AverageCPS` / `CyclesPerSecond`, on the `Core.Process` health check |
  | walk failures per 100 walks | `NavWalkFailures`; `walk-failures clear` per step |
  | world save time | `Core.Process` again — it brackets `BeforeWorldSave` to `AfterWorldSave` |
  | memory | `GC.GetTotalMemory` and `WorkingSet64`, same check |
  | per-town split vs target | `[BotPopulationAudit`'s own share report |

  **Stop at the step where the pace slips or tick max crosses half the budget** — 1000 ms of the
  2000 ms `Custom.BotTickSeconds`. Half rather than all, so there is headroom for the max as well as
  the mean: the tick cost is a mean over passes and a single pass can be several times it. That
  threshold is the one `BotMeasurementProfile.TickBudgetShare` encoded before its clamp was deleted;
  it survives here as a rule rather than as code.

  Then report the curve, propose a standing population, and **put `bots.json` back to 60** with
  `GG_BotPop.xml` regenerated to match, unless Sean picks otherwise.

  **The baselines to compare against, all measured this session at 60 bots and 219,308 items:**

  | | |
  | --- | --- |
  | behaviour tick | 0.4 ms mean / 13 ms max of 2000 ms |
  | world save | **0.26 s** |
  | memory | 385 MB managed, 529 MB working set |
  | cycles per second | 80.7 mean |
  | pace | *"engine delay 100ms matches the pace: the bot steps at the pace it was given"* — 10.0 tiles/s, 203 of 203 steps at run pace |

  **`[BotPace` needed a no-client form, and that is shipped.** It is the only thing in the tree that
  reads both the pace written to `CurrentSpeed` and the delay `DoMoveImpl` derives from it off a
  live walker, and says which one the bot actually stepped at — and it `BeginTarget`s a mobile, so
  it was unreachable from exactly the kind of run this test is. **`[BotPace auto [seconds]`** takes
  any bot already walking a route; the `bot-pace` token is the same thing from the bridge, and both
  write `Data/Live/bot-pace.json` whether or not anybody is holding a gump. `auto` **refuses** when
  no bot is walking rather than waiting for one, because a sampler that waited would report a window
  that never started as a window with no problems.
- **~~THE BANK FLOOR IS NOT SHORT; `BotCrowds` CANNOT SEE ITS OWN GARRISON.~~** *Fixed. Both halves
  applied, `GG_BotPop.xml` regenerated and re-imported, and the facet-wide consequence measured over
  two fifteen-minute windows - see the table at the end of this entry. Kept in full because the
  measurement is the argument and a reader of an older diff will find the old framing.*

  The Britain rebase grew the roamer pool 26-49% per class and the suspicion was that the crowd of 3
  now ran one short. It does not. Every bank has **exactly three fixed-role sitters standing in it at
  every one of 449 samples across a fifteen-minute window** - `MaxCount 3` and
  `Role/Fixed/Seed/BankSitter` on a spawner per bank - and the total standing crowd is *above* the
  floor:

  | bank | fixed | visitors | total standing | min | what `BotCrowds` sees | under floor |
  | --- | --- | --- | --- | --- | --- | --- |
  | `brit-bank` | 3.00 | 1.94 | **4.94** | 3 | 2.88 | 39% |
  | `brit-bank-east` | 3.00 | 1.63 | **4.63** | 3 | 2.75 | 46% |
  | `trinsic-bank` | 3.00 | 0.47 | **3.47** | 3 | 1.41 | **100%** |
  | `trinsic-bank-2` | 3.00 | 1.12 | **4.12** | 3 | 2.38 | 59% |

  **The cause is two lines, and neither is in `BotCrowds`.** `BotCrowds.CountFor` matches
  `BankSitterBehavior.DestinationId`, and a seeded fixture never has one: `PlayerBot.ApplySeed`
  forwards `_seedStation` to a `CrafterBehavior` and a `GathererBehavior` and **to nothing else**,
  and `BotPopulation` does not put a `SeedStation` token on a bank spawner in the first place - the
  `station` string reads `.../SeedStation/brit-forge/Seed/Crafter` while the bank string reads
  `.../SeedHome/britain/Seed/BankSitter`. So twelve permanent sitters - four banks, three each -
  count toward **no destination's floor at all**. Confirmed by id: the same twelve serials, three per
  bank, 4 to 11 tiles from each bank's centre tile, for the whole window, every one of them with an
  empty `dest` in the live snapshot.

  **What it costs is not a thin bank.** The shortfall multiplies a destination's weight by up to
  four (`BotCrowds.Shortfall`), so every bank on the facet has been pulling at up to 4x for ever,
  against shops, taverns and inns that are not. That is a standing distortion of `BotDestinations`'
  whole roll - and **raising `life.crowds.bank` would make it worse, not better**, which is exactly
  the wrong conclusion the measurement was heading toward before the garrison was found.

  **Proposed, both halves, not applied:**

  1. `PlayerBot.ApplySeed` forwards `_seedStation` to a `BankSitterBehavior` as it already does for
     the other two. Three lines.
  2. `BotPopulation` emits `SeedStation/<destination id>` on a bank spawner. It already knows the id
     - the spawner is *named* after it (`GG_BotPop_britain_bank_brit-bank`) - so this is the token,
     not the lookup. Needs `[BotPopulationGen` and `[GG_Reimport` after, and it rewrites tracked
     spawner XML, which is why it is a decision rather than a tidy-up.

  Then the floor reads 4.94 against 3 at `brit-bank`, the 4x pull switches off, and bank traffic
  falls back to its weighted share. **Expect the visitor counts above to drop when it does** - that
  is the pull being removed, not a regression. `trinsic-bank` is the one to watch either way: 0.47
  visitors against Britain's 1.94, because Trinsic still has no `plaza`, `wander` or `gate`
  destination and no work sites, so its roamers fall back to the bank (see the note below).

  A third option was considered and rejected: leaving the count alone and lowering
  `life.crowds.bank` to compensate. It gets the same traffic by writing down a number that is not
  the crowd anybody wants, and the next person to read `crowds` would be misled by it.

  **What it actually did, measured.** Two fifteen-minute windows of 61 bots either side of the fix.
  The floor now reads **0% under floor at every bank** where it read 74-100%, with the count seeing
  3.09 to 4.51 where it saw 1.05 to 2.88 - and the standing crowd is unchanged at a minimum of 3 per
  bank, because the garrison was always there.

  Visit share by destination type, three ways, because "visit share" has three honest readings with
  different biases and a real shift moves all of them:

  | type | CHOSEN (the roll) | HEADING (in transit) | route events (after) |
  | --- | --- | --- | --- |
  | bank | 19.6% -> **8.5%** | 12.0% -> **9.5%** | **7.6%** |
  | shop | 56.1% -> 50.6% | 54.4% -> 48.8% | 55.1% |
  | tavern | 2.8% -> **8.5%** | 2.6% -> **7.4%** | **8.1%** |
  | forge | 1.9% -> 0.6% | 0.4% -> 0.5% | 0.5% |
  | other | 19.6% -> **31.7%** | 30.5% -> **33.7%** | **28.6%** |

  **Bank visits halve and everything else takes the difference**, with taverns tripling and `other` -
  inns, gates, plazas, and the docks and stables the rebase adopted - gaining most. `CHOSEN` counts a
  Traveler's destination changing (unbiased, small: 107 and 164 rolls); `HEADING` counts Traveler
  bot-samples by target (large, but weighted by journey length); the last column is every `departing
  for` line in `botlog.json`, 185 of them, which is the authoritative record and agrees with `CHOSEN`
  to within a point or two - a useful cross-check on the sampling. `STANDING` is deliberately not in
  the table: it is dominated by the twelve bank fixtures and reads 33.8% before and after, which is
  true and answers a different question.

  Two things the after-window also says, neither of them about banks:

  - **The per-tier mount curve is noisier than one window suggests.** Window C's 0.0 / 0.0 / 11.1 /
    30.2 / 49.5 / 66.5 read as a clean monotonic ladder; window D's 0.0 / 0.0 / 35.6 / 27.2 / 36.5 /
    25.6 does not. Sixty bots is about nine per tier, so a per-tier share is quantised to roughly 11
    points and swings by more than that. **The `ALL` figure is the stable one** - 24.8% and 23.8% -
    and the design is confirmed by the ends (Novice and Apprentice at zero in both) rather than by
    the middle. Quoting window C's ladder as established would have been reading luck as a curve.
  - **One edge produced four of five terminal walk failures**, and it is a real defect this session
    did not cause: `uo-wp-194-s1` -> `uo-wp-194` failed once in window C and four times in D, the
    rate rising only because traffic moved toward Trinsic's east side. `[NavHop` paths the edge in
    both directions and in segments, `[TileProbe` says the goal tile is clean stone with no statics,
    no items and all eight neighbour steps ALLOWED, `[NavAudit` walks it and `[WalkAudit` walked all
    2,304 edges without a failure. **The bot was not on the waypoint.** It gave up at 2035,2832, one
    tile southwest of `uo-wp-194-s1` at 2036,2830 - and `[NavHop` from *that* tile to the same goal
    answers `ok:false`. A one-tile displacement drops a bot into a pocket the pathfinder will not
    route out of. This is precisely the question `[WalkAudit` is structurally unable to ask, which
    commit `118be12f` already wrote down: it starts every walk AT a waypoint, so it can never ask
    whether a bot standing *beside* one can route away from it.

- **The name census drifts DOWN under a real population, and the cause is not fully found.**
  With sixty bots and the logout/refill cycle running, `NamePool.InUseCount` falls behind the live
  count — three short after ten minutes. One path to it is fixed: `PickUnique`'s last resort used to
  return a name without claiming it. The rest is open. What is known: `Bots.Population` now **names
  the bots wearing an unclaimed name** (`Dyson (Lifecycle, Traveler)`, …), they are ordinary bots
  with no pattern to their role or brain, and no two live bots share a name at the moment of
  measurement — so a claim is being removed while its wearer still lives, and `NamePool.Release` in
  `OnDelete` is the only caller.
  **The blast radius is smaller than it looks**: nothing in the population layer reads
  `InUseCount`. `BotSession.AllowSpawn` counts `LiveRegistry` instead — deliberately, because a
  separately-maintained tally is a thing that can drift, which is precisely what this is. It is a
  reported number that is wrong, not a decision that is wrong. Upstream's `AllowSpawn` *does* read
  its equivalent tally, which is worth knowing before anyone ports more of that file.
- **~~`trinsic-shop-tailor-2`'s arrival is on the wrong floor.~~** *Fixed, and the diagnosis in this
  bullet had it backwards — see the note under `Bots.Recipe` above. The shop floor is Z 15, the
  arrival's Z was correct and its X,Y was inside a display case, and it was the destination's Z 35
  that was wrong.* The arrival now sits at `1987,2841` and the tailor clocks in.
- **Trinsic has no work sites at all** — no mine, no lumber — and no `plaza`, `wander` or `gate`
  destination to anchor a roaming spawner on, so its roamers fall back to the bank. Both are data,
  not code: author a Trinsic square with a `plaza` tag and the recipe picks it up with no code
  change, and `Bots.Recipe` prints which stop each town's anchor resolved at so the switch is
  visible when it happens.
- **`byType` has no entry for `dock`, `shrine`, `stables` or `healer`** — all rolling at the
  implicit 1.0, so `trinsic-healer` outranks a Trinsic bank for a non-Merchant. **The Britain rebase
  made this bigger without changing it**: adopting uo-offline's twelve missing Britain destinations
  added two docks and the town's first stables to that set, and grew Britain's roamer pool by
  26–49% depending on class (Merchant 22.7 → 33.8, Bard 48.8 → 65.2, Smith 44.5 → 56.2, Miner
  49.3 → 67.0), so every existing Britain destination now takes proportionally fewer visits. A dock
  weighs 0.6 through its `craft` tag — exactly an ordinary craft shop — and the stables 1.0, exactly
  an inn, so nothing is over-weighted. Still harmless, still worth a weight when somebody is next in
  `bots.json`, and now worth measuring rather than assuming.
- **`brit-mine-west` has its road** — and it does not run where this section assumed. The corridor
  was never blocked by a town gate: the two southern arrivals sit on flat forest on the **far side**
  of the cliff, with impassable rock and forest across y 1758–1770, which is why `[BotWorkScout` kept
  answering *"no walkable hop from the face chain"*. The road comes round from the **east** instead —
  `wp-21 → brit-minewest-1 (1204,1768) → brit-minewest-2 (1200,1776)` — every leg pathed by
  `[NavHop` before a record was written. The scout's own arrival table had also drifted from
  `navigation.json` after Sean's editor pass and is now reconciled, reach re-measured at 15/14/14/14.
- **`brit-lumber-south` (1422,1832, reach 4) is still unwritten.** The ground is verified; only the
  road is missing. Hand-author a corridor out through the **south bridge** in the editor — the
  `Nav.Data` reach check and `[BotSiteAudit` are what make that safe to do by hand. **Until it
  lands, Lumberjacks have no station** and `Bots.Work` says so, loudly, at every boot.
- **Two more sites Sean has already scouted**: the **cave at 1263,1251** and the **north mountain at
  1438,1223**. Same procedure — `[BotSitePick` for the arrivals, then either the scout or a hand
  corridor.
- **One mine is not many.** `brit-mine-north` is 14×26 and mining banks are 8×8 holding 10–34 ore, so
  it is a handful of banks: fine for a miner or two, thin for a shard running a dozen.
- **Skill gain as a motive** — a Novice at the forge *because* Blacksmithy is rising. Upstream does
  not do this: skills are rolled once at creation from `BotSkillTemplate` and never change, and
  `BotSkillTier` is a static equipment and band-weight band rather than a progression axis. Their
  motives are economic instead (out of stock → buy; pack full → haul). So this would be **net-new
  design, not a translation** — which is the argument for it as much as against it, since the
  engine already gains skill for real on every craft and harvest a bot performs. Nothing reads it
  yet.
- **Lift `EquipmentTable`'s tables into `Data/Custom/`**, per the T2A note above.

Retired by later sessions, listed so a reader of an older diff is not misled:

- *"No behaviour — `IdleBehavior.Tick` is empty and nothing calls it."* `BotTickManager` has called
  it since session 2, and session 4 gave `IdleBehavior` a `Tick` body of its own.
- *"`Personality` is rolled and persisted but nothing reads it."* The session-3 lifecycle roller
  reads it on every phase expiry.
- *"`PlayerBotBehavior` is a stub of the upstream contract — the speech scheduling, chat categories
  and cooldowns are not ported."* Session 4 ported them, onto the shapes that were left for them.
- *"Every class is rollable, including the six artisan and gatherer classes whose station and
  starter kit are severed (seams 1 and 3). They are fully dressed and skilled; they just have
  nothing they have made yet."* Session 5 gave four of the six a station, a kit and a shift.
  Fisherman still has none, and now says so out loud — see seam 10.

## Reference

- `docs-src/uo-offline-port-survey.md` — the full survey, including the API-difference tables.
- `Scripts/Custom/Core/Navigation/nav-format-comparison.md` — the navigation data comparison.
- `Scripts/Custom/MODIFICATIONS.md` entry 4 — `<LangVersion>latest</LangVersion>`, which this
  folder is the reason for.
