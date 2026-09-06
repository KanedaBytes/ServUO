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

### The sites are authored from the map, not from upstream's coordinates

The brief asked for ~40 gathering sites from `uo-offline`. **They do not exist**, and finding that
out reshaped the session.

Their 40 were generated at runtime from their 4,013-node Felucca waypoint graph — any node ≥45 tiles
from a city and ≥70 from another — and were **retired on 2026-07-06** to be hand-authored later.
Because their yield was invented, a bare road node made a perfectly good site; with a real harvest
system a road node yields nothing, so those coordinates are unusable here even if recovered.

Of their three *authored* sites, only `MiningSpot 448` is near Britain, and it is a **Felucca**
site. On Trammel — our facet — that area holds 110 scattered mineable tiles across a 120×120 box, at
most 8 per 10×10 cell, and **none within 18 tiles of their centre**. It is an outcrop field, not a
mine.

So the sites come from the ground, validated against the same tile tables the harvest system reads.
That is not an invention: it is the method `navigation.json`'s own comment already describes for its
waypoints — *"replaced with coordinates flood-filled from the map itself"*.

### `[BotWorkScout`, and why reading the MUL files yourself does not work

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

It also samples **from the site inward**, stopping the moment a hop can reach the existing graph.
Sampling from the gate outward is the obvious way round and it is wrong — it re-walks Britain and
lays a second set of waypoints down streets that already have them. Growing inward cut the three
corridors from 60 waypoints to 36.

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

| Site | Type / tag | Face | Harvestable | Arrivals | Corridor |
| --- | --- | --- | --- | --- | --- |
| `brit-mine-north` | `mine` / `mine-north` | 13×18 at 1274,1561 | 32 tiles | 4 | 14 waypoints, joining at `brit-north-8` |
| `brit-mine-south` | `mine` / `mine-south` | 11×13 at 1278,1677 | 25 tiles | 4 | 15 waypoints, joining at `brit-crier-a` |
| `brit-lumber-nw` | `lumber` / `lumber-nw` | 14×13 at 1339,1546 | 88 tiles | 4 | 7 waypoints, joining at `brit-north-8` |
| `brit-forge` | `forge` / `craft town` | 1424,1557 | anvil `0x0FAF` at 1423,1556, forge `0x0FB1` at 1424,1558 | 4 | in town, off `brit-smith-1` |

**36 new waypoints and 36 new edges**, all `wilderness`-tagged — `costTags` already carried
`wilderness` at 1.4, so the schema was built for this before there was anything to put in it.

**The corridors go around the mountain, not at it.** That is the single most surprising thing the
engine had to say, and it is obvious in hindsight: the range *is* what lies due west of the gate, so
there is no road through it. The route to the north face leaves by the north of town and runs west
along y≈1510; the route to the south face leaves by the south and runs west along y≈1744. Each one
attaches to the nearest existing town waypoint rather than starting from the gate, so the only
waypoints added are the ones that are genuinely new.

The two mine faces are **distinctly tagged** so `[BotInfo` and the editor's map layer can tell them
apart — they are on the same range and would otherwise be indistinguishable in a report.

Thirty-two mineable tiles is a smaller face than it sounds: mining banks are 8×8 and hold 10–34 ore
apiece, so a 13×18 face is six or so banks, which is a shift's work for one or two miners and not
more. That is the argument for the third site on the [later](#later) list, not against these two.

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

**One number is ours rather than upstream's: a station with somebody actually at it scores 20.0.**
Upstream's weights asked whether a destination *could* buy the load, never whether anyone was there
to buy it — and that is the question that decides whether the haul turns into anything, because a
load left at an empty forge is a load in a bank box with extra steps. At 9 against the bank's 2 a
laden miner still banks about one trip in five, which is how the work probe first found this:

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
| `[BotBehavior <name>` | GameMaster | Target a bot; switch it (`Idle`, `Traveler`, `BankSitter`, `Shopper`, `Crafter`, `Gatherer`) |
| `[BotLifecycle` | GameMaster | Report the roller, its cadence and the transition tally |
| `[BotLifecycle on\|off` | GameMaster | Pause the roller, so a behaviour can be watched without it being rolled away |
| `[BotWorkScout` | Administrator | Sweep for work sites and the roads to them, and write `Data/Live/work-scout.json` for merging into `navigation.json`. An authoring tool, not a runtime one — see above |
| *(config)* `Custom.BotWorkProbeOnStart` | — | Run the work probe on its own, without the six minutes of walk, lifecycle and chat probes that precede it in `[BotSmoke` |
| `[BotsReload` | GameMaster | Re-read `bots.json`, the player caps it defaults from, **and the chat corpus** (alias `[ReloadBots`). The corpus reloads either way — a bad edit to one has nothing to do with the other. Also **re-validates the work sites**, so after editing the graph the order is `[NavReload` then `[BotsReload` |
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

`Bots.Work` — the working-class census: work sites with occupancy against capacity, crafters at
station, gatherers out and hauling, loads delivered, pack animals live/reaped/released. **Fail** if
a class has a station the graph does not contain — that class can never work at all, and nothing
else in the system would say so. **Warn** for a site excluded at load, a crafter blocked at its
bench, or a `capacity` entry naming a destination that is not there.

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

## The work probe

Runs with `[BotSmoke`, last of the four, reports through `Bots.Shift`.

It asserts the **whole loop**, not a piece of it: a Miner goes out, mines real ore off a real rock
face, walks the load back to town, hands it to a Smith, and the Smith turns it into at least one
crafted item. Every one of those steps is somewhere the session could be quietly broken while each
part looked fine on its own — a site with no ore, a corridor that does not route, a delivery that
matches the wrong trade, a forge the bot cannot reach from its own arrival tile — and none of them
would show up in a check of any single piece.

It runs on an **accelerated clamp installed in memory only**, the same discipline `BotLifeProbe`
keeps: a real shift is 4–8 minutes and a real crafter settles in for 3–6 *hours*, which is right for
a shard and useless for a probe.

**The smith is stripped of its starter ingots first**, and that is the assertion rather than a
detail. `EquipmentTable` seeds a fresh smith with 40–90 of them, so a smith left alone would make
something whether or not the hand-over ever happened, and the probe would pass on a broken delivery.
Dry, the only ingots it can ever have are the ones the miner brings. (The first version stripped
them with `ConsumeTotal(typeof(IronIngot), Int32.MaxValue)` — which is all-or-nothing, so it
consumed *nothing*, returned false, and the probe cheerfully reported ten items made from ore that
had nothing to do with them. It deletes the items now.)

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

### Later

- **A third mine, north.** The real northern mountain at ~1480,1210 is 460+ tiles from the bank and
  wants a corridor of its own out of the north of town — a good deal more than the 33 waypoints this
  session added for everything. Add its coordinate to `BotWorkScout.Seeds` and the tool will do the
  work; the reason to bother is that the two faces here are only about six resource banks between
  them, so a shard running more than a handful of miners will exhaust them.
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
