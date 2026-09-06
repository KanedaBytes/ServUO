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

Population management arrives in a later session.

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

### The bank crowd is a pull, not a garrison

Upstream keeps bank crowds with `BankFixtures`: a spawner at every bank holding five permanent,
**lifecycle-exempt**, curve-exempt sitters. Its lifecycle only ever adds extras, by teleporting a
rolled BankSitter to a uniformly random bank with **no occupancy check at all**.

This shard walks. So the floor is expressed as a weight multiplier on an under-floor destination -
bots *want* to go where the crowd is thin - and nobody is placed, teleported or commandeered. The
multiplier is capped at four times, and **bots already routing there count toward the floor**, so
one empty slot does not pull every traveller in town.

Consequence worth knowing: the floor is a tendency, not a guarantee. `Bots.Population` reports
`banks below floor` rather than asserting it.

### The Shopper walks

Upstream's is rotation-only, by explicit design - *"No movement = no wall-grinding"* - because it
leaned on a zone check to guarantee it was already in the right place. The tell is that their file
still carries `Home`, `HomeMap` and `VendorSpeakRange` fields that are written and never read:
vestiges of a walking version that was removed.

Ours walks between the shop's arrival points, because the hops are two or three tiles, they go
through `NavWalker` like every other walk here, and a shopper wedged behind a counter gets the
recovery ladder for free. What made movement dangerous for them is what steps 4a and 7b already
solved here.

### `PickScatteredHome` is kept, and matters more here than there

A BankSitter settles a few tiles *off* the arrival point rather than on it. Upstream's reason was
that "every bot homes on the exact tile it arrived at and the crowd stacks" - cosmetic. Here it is
load-bearing: an uncontrolled `BaseCreature` cannot walk through another mobile
(`Movement.cs:411`), so sitters parked on the arrival points make those tiles unreachable for the
next arrival. Dropping it turned the bank into a traffic jam, visible as a flood of Sidestep and
Door recoveries in the walk probe.

Ours additionally passes `checkMobiles: true` when picking the spot, which upstream does not.

## Severed seams

Nine places where this layer deliberately stops short. Each is marked with a one-line comment at
the site naming the session that restores it, so none of them has to be rediscovered.

| # | Site | Severed as | Restored by |
| --- | --- | --- | --- |
| 1 | `BotClassHelper.StationFor`, `CrafterTypeHelper.StationFor` | Not ported. They returned upstream's `DestinationType` enum, which belongs to its travel layer | Traveler/Crafter behaviours — returning **our** nav destination `type` **string**, not their enum |
| 2 | `EquipmentTable.AddMarkedRune` | A blank `RecallRune`. Upstream marked it from its `DestinationCatalog`; nothing routes yet, and a rune marked to somewhere no bot can walk is a lie in the pack | Travel session — wires to `Nav.Destinations(...)` |
| 3 | `EquipmentTable.SeedCrafterStarterProps`, `EquipCrafterTool` | Empty. Both read `CrafterProfiles`, a crafting-layer file | Economy session, which brings `CrafterProfiles` |
| 4 | `EquipmentTable` → `BotItemFactory` | **Not severed** — pulled in as a leaf, since it is self-contained and the outfit roller cannot work without it | n/a |
| 5 | **`BotAI`'s base class** | `: VendorAI` — the right stand-aside for a bot that only stands still | Combat session — becomes `MeleeAI`, `MageAI` or a purpose-built `BaseAI` |
| 6 | `BankSitterBehavior` Hawker's **WTS** half | Only `wtb`. Upstream's hawker shouts a line `BotShop` builds from a real item in its pack, so "WTS GM halberd 5k" means there *is* one and 5k buys it. A WTS from a bot holding nothing is the lie this system exists not to tell | Economy session, with `BotShop` |
| 7 | `BankSitterBehavior`'s **three macro roles** | `ResistMacro`, `HidingMacro` and `StealthMacro` roll as `Regular`. Their **weights are kept** in `RollRole` behind markers, so restoring them is deleting three redirects rather than re-deriving upstream's distribution | Combat session, which brings spellcasting and `Hidden` toggling |
| 8 | Gossip, and `PlayerBotBehavior`'s `GossipShare` branch | A marker comment. `Gossip/` is copied but never scanned, and its `{actor}`/`{other}`/`{when}` tokens are registered unwired — so the lines are doubly unreachable | Event-journal session, with `BotEventJournal` |
| 9 | `friend_greet` and `BotSocialGraph` | Not wired. `{name}` **resolves**, but nothing decides that two bots are friends, so no path picks the category | Social session |

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

Ownership: `{place}` `{dest}` → nav (**wired**, from `NavDestination.Name`); `{name}` → identity
(**wired**); `{price}` `{item}` `{mat}` `{short}` → economy; `{pet}` → taming; `{dungeon}` →
adventurer; `{actor}` `{other}` `{when}` → journal.

> `{short}` is **coin**, not a short name — `trade_short.txt` reads *"need {short} more"*, the gap
> between an offer and a price, and it always travels with `{price}`. It is economy-owned.

At the time of writing: **1,224 non-comment lines across 106 categories — 1,060 wired, 164
reserved (economy 146, adventurer 14, taming 4), 0 unknown.** Reserved is a progress meter, not a
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

## Class-weighted destinations

```
final = byType[type][class or "default"] * product over the destination's tags of byTag[tag][class or "default"]
```

Absent means **1.0** ("no opinion"), never 0. A weight of **0 excludes** — which is how `home`,
`guard` and `work` are kept off the list.

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
| `[BotBehavior <name>` | GameMaster | Target a bot; switch it (`Idle`, `Traveler`, `BankSitter`, `Shopper`) |
| `[BotLifecycle` | GameMaster | Report the roller, its cadence and the transition tally |
| `[BotLifecycle on\|off` | GameMaster | Pause the roller, so a behaviour can be watched without it being rolled away |
| `[BotsReload` | GameMaster | Re-read `bots.json`, the player caps it defaults from, **and the chat corpus** (alias `[ReloadBots`). The corpus reloads either way — a bad edit to one has nothing to do with the other |
| `[BotSmoke` | Administrator | Spawn one bot per class, check every one against the caps, delete them |

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

## The five-traveller walk probe

Runs with `[BotSmoke`, reports through `Bots.Travel`.

It exists because rungs 2 to 5 of the recovery ladder went unverified when the ladder shipped — a
lone walker recovers at rung 1 essentially every time, since A* just routes around one obstruction.
**Contention is what reaches the deeper rungs**, so the probe manufactures it: five bots are sent to
one destination at once so they compete for the same arrival tiles, then dispersed.

**Pass is "nobody stuck, and no `Teleport` rung fired."** Rungs 2 to 4 firing is *information* — a
sidestep that worked is the ladder doing its job. The rung line prints either way, because a run
where nothing escalated is worth knowing about too: it means the contention did not bite.

**"Stuck" is the walker's own answer**, `NavWalker.CurrentRung != None`, not a guess from outside.
The first version of this probe called a bot stuck if it was still walking when the window closed,
and duly reported five stuck bots that were simply getting on with it — a Traveler that arrives
lingers and then departs again of its own accord, so "still walking" is the *normal* steady state.

## Known simplifications

1. **The behaviour name is serialized but not restored.** There is no registry to construct a brain
   from a string yet, and a bot is deleted on load regardless.
2. **Every class is rollable, including the six artisan and gatherer classes** whose station and
   starter kit are severed (seams 1 and 3). They are fully dressed and skilled; they just have
   nothing they have made yet, which is true of one that has not worked a shift.
3. **A bot only ever talks to the room.** It has no memory of a conversation, so it cannot be
   asked a follow-up: the second question gets another line from the same pool, not an answer to
   the first. That is deliberately upstream's shape too — these are passers-by, not quest NPCs.

Retired by later sessions, listed so a reader of an older diff is not misled:

- *"No behaviour — `IdleBehavior.Tick` is empty and nothing calls it."* `BotTickManager` has called
  it since session 2, and session 4 gave `IdleBehavior` a `Tick` body of its own.
- *"`Personality` is rolled and persisted but nothing reads it."* The session-3 lifecycle roller
  reads it on every phase expiry.
- *"`PlayerBotBehavior` is a stub of the upstream contract — the speech scheduling, chat categories
  and cooldowns are not ported."* Session 4 ported them, onto the shapes that were left for them.

## Reference

- `docs-src/uo-offline-port-survey.md` — the full survey, including the API-difference tables.
- `Scripts/Custom/Core/Navigation/nav-format-comparison.md` — the navigation data comparison.
- `Scripts/Custom/MODIFICATIONS.md` entry 4 — `<LangVersion>latest</LangVersion>`, which this
  folder is the reason for.
