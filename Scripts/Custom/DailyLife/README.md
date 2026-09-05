# Custom/DailyLife

Britain's day schedule: dawn/day/dusk/night phases driving tavern patrons, the night watch,
route-walking townsfolk, and shopkeepers who go home at dusk.

Config is `Data/Custom/britain-daily-life.json`. **Every location in it is a nav id** — including
the clock anchor. There are no coordinates in this file; a place is named, and where it is lives
in `Data/Custom/navigation.json`.

Ported from the ModernUO shard's `Custom/DailyLife/`, keeping its scheduler and behaviour rules
and replacing two things wholesale: coordinates became nav ids, and all movement goes through
`NavWalker`. **No actor owns its own pathing.**

## Phases

`<4` night, `<6` dawn, `<22` day, else dusk — boundaries deliberately identical to
`LightCycle.ComputeLevelFor` (`Scripts/Misc/LightCycle.cs:70-81`), so NPC behaviour and the light
players actually see always agree. Change one and you must change the other.

**Dawn is not after dark.** The tavern empties and the watch stands down at hour 4, when the ramp
starts — not at 6 when it finishes. `IsAfterDark()` is the single predicate every consumer uses.

The hour is sampled at the **town anchor**, not globally: `Clock.GetTime` adds `MapIndex * 320`
minutes per facet and `x / 16` for longitude (`Scripts/Items/Tools/Clocks.cs:93-109`), so "the
hour" differs by over seven hours across one map.

`DayCycleSystem` polls every 5 seconds and **recomputes, never increments** — in-game time is
derived from `DateTime.UtcNow`, so an NTP step moves it 12 minutes per real minute and can run
backwards, and downtime is not paused.

### Boot order

`DayCycleSystem.Initialize` is `[CallPriority(-10)]`, `DailyLifeSystem.Initialize` is `-5`, and
the four consumers are untagged (0). **The ModernUO original used `[CallPriority(10)]`**, which
meant "early" against a default of 50; ServUO's default is 0, so copying that number across would
run the day cycle *after* its consumers. `DailyLifeSystem.Configure` is `[CallPriority(110)]`, one
above `NavigationSystem`'s 100, because every id here needs the graph.

## The shopkeeper problem, and why there are vendor subclasses

Driving the stock vendors is **respawn-safe** — `XmlSpawner2.Defrag` (`XmlSpawner2.cs:7990`) drops
a spawn only when it is deleted, tamed, or despawn-timed-out in an inactive sector; no distance,
map or home check. `SmartSpawning` is `False` on all 2,572 Trammel spawners and `DespawnTime` is
0, so both despawn paths are disabled by data. ServUO's own return-home is dead too:
`IsSpawnerBound()` (`BaseCreature.cs:7787`) requires `Spawner is Spawner`, and
`XmlSpawner : Item, ISpawner`.

**But a stock `BaseVendor` cannot walk.** `GetMoveDelay` (`BaseVendor.cs:74`) is
`Utility.RandomMinMax(30, 120)` — *seconds per step* — returned verbatim by
`VendorAI.TransformMoveDelay` (`VendorAI.cs:148`), which ignores `CurrentSpeed`. Dusk to dawn is
6 UO hours = **30 real minutes**; a 40-tile walk at that rate takes 20–80. So daily life ships six
one-line subclasses (`Scripts/Custom/Mobiles/GGVendors.cs`) whose only job is to carry
`DailyLifeAI` through `ForcedAI` (`BaseCreature.cs:3342`).

`DailyLifeAI` does exactly two things, both scoped to `Commuting` so an idle shopkeeper still
paces its counter at the stock pace:

- **`DoActionWander` stands aside**, because `WalkRandomInHome` (`BaseAI.cs:2509`) has no spawner
  gate and would fight the route for every step.
- **`TransformMoveDelay` returns 0.5s**, so the journey finishes inside the night.

## Vendor migration

`[GG_MigrateVendors` hands Britain's six shopkeeper spawn points from the stock spawners to ours.
It is **explicit, confirmed, one-time and reversible** — never a boot-time mutation, because
silently rewriting spawners on every start is impossible to reason about later.

It switches the named stock spawners off (`Running = false` + `RemoveSpawnObjects`), logs each
one, and records it in `CustomPersistence` so it cannot re-run. Nothing is deleted and no upstream
file is edited. `[GG_RestoreVendors` puts it all back.

**Spawners are matched by name, scoped to the Britain region on Trammel.** Matching by vendor type
would take collateral: there is a second Britain jeweler (`Vendors#1` at 1650,1642) that daily
life does not manage.

**Two of the six stock spawners are multi-type** — `prov/cobbler/fisherman` and `Vendors#20`
(`carpenter:architect:realestatebroker`) — so `GG_DailyLife.xml` respawns the companions
alongside our replacement. Switching those spawners off must not quietly delete Britain's cobbler.

```
[GG_MigrateVendors     # switch the stock six off (asks for confirmation)
[GG_Reimport           # import Spawns/Custom, which brings ours in
```

`DailyLife.Config` reports `6 shopkeeper(s) (0 in world)` and warns until this is done, because a
configured-but-absent shopkeeper is otherwise completely silent.

## Actors

| Class | What it is |
| --- | --- |
| `DailyLifePatron` | Tavern drinker. Never commutes; mills about its arrival spot on the stock wander |
| `DailyLifeTownsfolk` | Walks a nav route lap for ever |
| `DailyLifeWatchman` | `: DailyLifeTownsfolk` plus a lantern and leather |
| `GG*` vendors | The six managed shopkeepers |

Patrons, townsfolk and watchmen are **ephemeral**: `Timer.DelayCall(Delete)` at the tail of
`Deserialize` (ServUO has no `[AfterDeserialization]`), so a restart can never leave orphans and
the JSON stays the single source of truth. The GG vendors are **not** — they are spawner-owned and
persist, but `Commuting` is deliberately not serialized, so a shard that saves mid-walk comes back
with the flag clear and the schedule snaps them where they belong.

Looping lives above `NavWalker`, which is one-shot by design: `Nav.TryBuildRouteLap` builds a lap
that already contains its own return leg, and the actor re-follows it from the `Arrived` callback.
A patrol therefore costs one route computation no matter how long it runs.

## Watch posts

Each post has **exactly one** of `route` or `destinations`:

- `route` — an authored patrol line (`brit-watch-north`, `brit-watch-south`).
- one destination — a stationary post, standing on an arrival point. This is what the `exclusive`
  flag on `brit-guard-west`'s arrivals is for: a guard post is a specific tile, and a second
  watchman must not take it.
- two or more — a patrol routed between them through the graph, looping.

## No shop-district region

The ModernUO original registered a `ShopDistrictRegion : GuardedRegion` to grey the buy menu.
**ServUO's `GuardedRegion` exposes no parent-taking constructor** (`GuardedRegion.cs:22-34`), so
one laid over Britain would be unparented and could shadow the town's own guard rules — a real
gameplay risk for a purely cosmetic effect. The six vendors we own carry the check themselves
instead (`DailyLifeVendor.CheckAccess`), which is narrower and safer.

It is cosmetic either way: `VendorBuyEntry` calls `VendorBuy` without rechecking, and "vendor buy"
speech bypasses the menu. **The real "closed" is the shopkeeper not being there.**

## Failure contract

**A failed load keeps the live config.** Structural problems fail the load and are all collected
in one message — blank or duplicate id, a post with both or neither of `route`/`destinations`, an
unknown body, a vendor type that does not resolve. **Dangling nav ids do not**: they are collected
and reported by `DailyLife.Config`, so one typo cannot take the town offline.

## Commands

| Command | Access | Effect |
| --- | --- | --- |
| `[DayPhase` | GameMaster | Reports the phase, the anchor time, whether an override is active, and the actor counts |
| `[DayPhase <dawn\|day\|dusk\|night>` | GameMaster | Force a phase, pinning the light level to match |
| `[DayPhase clear` | GameMaster | Release the override |
| `[DailyLifeReload` | GameMaster | Re-read the config and rebuild (alias `[ReloadDailyLife`) |
| `[DailyLifeSmoke` | Administrator | Force a full cycle and check the town reacts |
| `[GG_MigrateVendors` / `[GG_RestoreVendors` | Administrator | The migration pair above |

**The light override is never sticky.** It is cleared on `Initialize`, on `[DailyLifeReload` and
on `[DayPhase clear`, and `[DayPhase` with no arguments says plainly when one is active — a pinned
night that outlives the reason for it is an evening spent wondering why the sun never comes up.

## `[DailyLifeSmoke`

A real cycle takes two real hours, so without this the only way to learn that a later change
stopped the tavern filling is to watch Britain for an evening. It forces dusk → night → dawn →
day, asserts the tavern, watch and shop direction at each, and restores the clock in a `finally`.

`Custom.DailyLifeSmokeOnStart` (default **False**) runs it headlessly, mirroring
`CoreSmokeOnStart`. Its last result surfaces in `[CoreSmoke` as `DailyLife.Smoke`, so the
whole-shard report says at a glance whether daily life was last seen working — without
`[CoreSmoke` itself spawning crowds on a live shard.

## Adding a seventh shopkeeper

No code, provided the trade already has a `GG*` subclass:

1. Place its home with `[NavMark` and `[NavArrival`, and add a `home`-type destination.
2. Add one line to `shopkeepers[]` with `spawner`, `stockSpawner`, `vendor`, `shop` and `home`.
3. `[DailyLifeReload`, then `[GG_MigrateVendors` if the stock spawner is still running.

Set `"closes": false` for a trade that stays open after dark — the healer, for instance.

## Known simplifications

- **Route nodes carry no speech.** The ModernUO original had a per-node `say`; nav routes have no
  per-node text and adding one would put content into the navigation layer, where it does not
  belong. The flavour lives in `chatter` and `watchChatter` instead.
- **Seven of the thirteen seeded shops are unmanaged** — they have no home destination yet. That
  is a data task, not a code one.
