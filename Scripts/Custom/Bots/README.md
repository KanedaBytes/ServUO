# Custom/Bots

> **Licence.** The code in this folder is a derived work of the PlayerBots system in
> [`Klein187/uo-offline`](https://github.com/Klein187/uo-offline), **Copyright (C) Klein187**,
> licensed under the **GNU General Public License, version 3**. It is therefore distributed under
> GPL-3.0, and every file here carries that notice. See [`LICENSE-BOTS`](../../../LICENSE-BOTS) at
> the repository root. The rest of this repository is GPL-2.0-or-later (RunUO/ServUO lineage),
> which combines lawfully with GPL-3.0; the combined work is GPL-3.0. Provenance, not legal advice.

Fake players: a `PlayerBot` with a class, a skill tier, a personality, a name, a speech colour and
an outfit. Config is `Data/Custom/bots.json`; namespace `Server.Custom`.

**This is session 1 of the bot layer — a bot you can spawn and inspect.** It does nothing. There is
one behaviour, `Idle`, and it has an empty `Tick`. Movement, speech, lifecycle, population and
everything else arrive in later sessions. What this session exists to prove is that the identity
model is right and that the two decisions underneath it hold: the era caps are configuration, and a
bot never survives a restart.

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
| **Party** | **Refused.** `AddPartyTarget.cs:30-32` answers a human-bodied non-player with *"Nay, I would rather stay here and watch a nail rust."* | `Player = true` |
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

## Severed seams

Five places where this session deliberately stops short. Each is marked with a one-line comment at
the site naming the session that restores it, so none of them has to be rediscovered.

| # | Site | Severed as | Restored by |
| --- | --- | --- | --- |
| 1 | `BotClassHelper.StationFor`, `CrafterTypeHelper.StationFor` | Not ported. They returned upstream's `DestinationType` enum, which belongs to its travel layer | Traveler/Crafter behaviours — returning **our** nav destination `type` **string**, not their enum |
| 2 | `EquipmentTable.AddMarkedRune` | A blank `RecallRune`. Upstream marked it from its `DestinationCatalog`; nothing routes yet, and a rune marked to somewhere no bot can walk is a lie in the pack | Travel session — wires to `Nav.Destinations(...)` |
| 3 | `EquipmentTable.SeedCrafterStarterProps`, `EquipCrafterTool` | Empty. Both read `CrafterProfiles`, a crafting-layer file | Economy session, which brings `CrafterProfiles` |
| 4 | `EquipmentTable` → `BotItemFactory` | **Not severed** — pulled in as a leaf, since it is self-contained and the outfit roller cannot work without it | n/a |
| 5 | **`BotAI`'s base class** | `: VendorAI` — the right stand-aside for a bot that only stands still | Combat session — becomes `MeleeAI`, `MageAI` or a purpose-built `BaseAI` |

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
| `[BotsReload` | GameMaster | Re-read `bots.json` and the player caps it defaults from (alias `[ReloadBots`) |
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

## Known simplifications

1. **No behaviour.** `IdleBehavior.Tick` is empty and nothing calls it — there is no tick manager,
   because with one behaviour there would be nothing for it to do.
2. **`PlayerBotBehavior` is a stub of the upstream contract.** The speech scheduling, chat
   categories, cooldowns and timed-visit machinery are not ported; the members that are here keep
   their upstream shapes so the speech layer drops onto this rather than replacing it.
3. **The behaviour name is serialized but not restored.** There is no registry to construct a brain
   from a string yet, and a bot is deleted on load regardless.
4. **`Personality` is rolled and persisted but nothing reads it.** It belongs to the lifecycle
   session; it is rolled now because it is part of the character and the save format is easier to
   get right before there is anything to migrate.
5. **Every class is rollable, including the six artisan and gatherer classes** whose station and
   starter kit are severed (seams 1 and 3). They are fully dressed and skilled; they just have
   nothing they have made yet, which is true of one that has not worked a shift.

## Reference

- `docs-src/uo-offline-port-survey.md` — the full survey, including the API-difference tables.
- `Scripts/Custom/Core/Navigation/nav-format-comparison.md` — the navigation data comparison.
- `Scripts/Custom/MODIFICATIONS.md` entry 4 — `<LangVersion>latest</LangVersion>`, which this
  folder is the reason for.
