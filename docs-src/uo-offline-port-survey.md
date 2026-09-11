# Port survey: `uo-offline-server` PlayerBots → this shard

Written before step 6 (the bot-mobile layer), as the survey `nav-format-comparison.md` deferred.

*(Lives in `docs-src/` rather than `docs/` because ServUO's `[DocGen` calls
`DeleteDirectory("docs/")` before it writes — `Scripts/Commands/Docs.cs:453` — and `/Docs` is in
`.gitignore`. Same reason as `Scripts/Custom/Core/Navigation/nav-format-comparison.md`.)*

**Purpose: decide what to port, in what order, and what it costs — not to plan the port itself.**

## Status

| | |
| --- | --- |
| **Surveyed at** | `Klein187/uo-offline` @ `91848d8`, installed 2026-09-05, then at `E:\dev\UO\uo-offline-server` — **that path is stale; do not read it** |
| **Current reference** | `E:\dev\UO\uo-offline` @ `7f38c7c` — the git clone, which has the history and the per-file dates. `playerbots/source/CustomBots/` for source, `playerbots/data/` for data |
| **Also on disk** | `C:\Users\sean.GEKKOSTATE\uo-modernuo\ModernUO` @ `fe18a469`, installed 2026-09-06 — an *installed* copy, with the bot source deployed under `Projects/UOContent/CustomBots/`. Cite it only where a Deviations row already does. See the table at the top of `CLAUDE.md`, which is authoritative for all four trees |
| **Ported so far** | sessions 1–6 (shard steps 6–11): identity, movement, lifecycle, speech, work, **population** |
| **Stale below** | four modules rewritten by their September 2026 release — see §5 |

**Every line count and file count in §1 was taken at `91848d8` and has not been re-taken.** The
relative paths in the table below are still correct under the new root; only the root moved.

> **This header named the wrong tree as the current reference until 11 September 2026**, and it is
> item 4 of `SHARD.md`'s cold-pickup list - so a session following those instructions was sent to
> the installed snapshot rather than to the clone. The snapshot's bot content is untracked in that
> repository, so it has no git history and no per-file dates, which is exactly why the clone is the
> reference now. The navigation data is identical at both pins - 3952 waypoints, 8554 `Connects`,
> 488 destinations, record for record - and differs only in line endings, so nothing surveyed here
> changes because of the correction.

Source read for this survey: `C:\Users\sean.GEKKOSTATE\uo-modernuo` (was
`E:\dev\UO\uo-offline-server`), installed from `Klein187/uo-offline` (`uo-offline-version.json`).
**That is where the counts below were taken, not where new work should read** - see the Status
table. The brief's folder names do not match the tree; they resolve as:

| Brief | Actual |
| --- | --- |
| `playerbots/` | `ModernUO/Projects/UOContent/CustomBots/` — **136 files, 46,733 lines** |
| `patches/` | 8 modified upstream files, visible only as `git diff` in the ModernUO clone — 165 insertions |
| `tools/map/` | `map-editor/` at the install root — Python + HTML, ~2,900 lines |
| data | `ModernUO/Distribution/{Data,Spawners,Configuration}/` |

Their shard is **T2A, Felucca-only** (`Distribution/Configuration/expansion.json`: `Id: 1`,
`MapSelectionFlags.Felucca` alone true). Ours is **EJ, Trammel** (`Config/Expansion.cfg`,
`SHARD.md`). That gap is smaller than it looks — see §3.

---

## 1. Inventory

Line counts are exact. "Layer" uses the brief's names.

### Bot mobile — 3,464 lines

| File | Lines | What it is |
| --- | --- | --- |
| `PlayerBot.cs` | 1227 | `PlayerBot : PlayerMobile`. 15 overrides, hand-written versioned serialization at **version 6** |
| `EquipmentTable.cs` | 2029 | Outfit rolls per class/tier. Weighted `new Item()` arrays — **code, not data** |
| `BotClass.cs` | 157 | 18-value enum + roll weights |
| `BotSkillTier.cs` | 81 | 7 tiers, bell-curved 15/20/20/20/15/8/2 |
| `BotSkillTemplate.cs` | 374 | Real T2A 7-skill templates and stat spreads |
| `CrafterType.cs` | 102 | 3 artisan subtypes + legacy migration |
| `NamePool.cs` | 361 | Curated + algorithmic names. Also the **O(1) live-bot census** (`InUseCount`) |
| `BotItemFactory.cs` | 133 | Reflection over `Server.Items.*` type names |
| `SpeechHues.cs` | 72 | 17-hue palette; 90% of bots get a colour |
| `Behaviors/BotPersonality.cs` | 125 | `[Flags]` traits + per-behaviour tendency weights |

### Lifecycle — 1,065 lines

`Behaviors/BotLifecycleManager.cs` (304), `LifecycleTransitions.cs` (86),
`BehaviorTickManager.cs` (79), `BehaviorRegistry.cs` (87), `BotSessionManager.cs` (315),
`BotStartupManager.cs` (168), `BotPopulation.cs` (43) — plus `SetLifecycleCommand.cs` (75) and
`ForceLifecycleTickCommand.cs` (104).

There is **no lifecycle state enum** — the state *is* the behaviour name string.

### Behaviours — 13,499 lines

| File | Lines | Heaviest dependency |
| --- | --- | --- |
| `TravelerBehavior.cs` | 3216 | `PathFollower`; its arrival handoff is the de-facto dispatcher for the whole system |
| `AdventurerBehavior.cs` | 2636 | The full `Server.Spells` engine, by reflection on type-name strings |
| `PKBehavior.cs` | 1267 | The engine's criminal/murderer/guard system, deliberately not bypassed |
| `DungeonCrawlerBehavior.cs` | 1172 | Real `Server.Items.Teleporter` pads; subclasses Adventurer |
| `CrafterBehavior.cs` | 716 | `Server.Engines.Harvest`, `Server.Targeting` |
| `MagicTravel.cs` | 726 | Subclasses the real `Server.Spells.Fourth.RecallSpell` |
| `BankSitterBehavior.cs` | 652 | Directly instantiates `ClumsySpell`/`CurseSpell`/… |
| `GathererBehavior.cs` | 444 | Only `using System; using Server;` — the most portable of the big ones |
| `WaypointGraph.cs` / `WaypointRegistry.cs` | 413 / 148 | **We have equivalents** — see §1.1 |
| `DestinationCatalog.cs` / `DestinationType.cs` | 482 / 390 | **We have equivalents** |
| `MoongateTravel.cs` | 358 | Gates discovered from the catalog, not hardcoded |
| `DungeonRegistry.cs` | 328 | Scoped queries over the catalog |
| `CrafterProfiles/Production/Stock.cs` | 286/172/120 | Real ingots/cloth/boards |
| `PlayerGroupBehavior.cs` | 194 | **`Server.Engines.PartySystem`** — a real player's party |
| `VisitorBehavior.cs` | 165 | `ChatLibrary` only |
| `CorpseReclaimBehavior.cs` / `GhostBehavior.cs` | 129 / 147 | `BotDeathManager` |
| `StreetCharacterBehaviors.cs` | 144 | Beggar + Newbie; follows real players |
| `ShopperBehavior.cs` | 125 | None — deliberately movement-free. The single most portable file |
| `PartyMemberBehavior.cs` | 89 | `BotPartyManager` |
| `WanderBehavior.cs` | 88 | **Retired** — `BehaviorRegistry.cs:28` maps `"Wander"` → `TravelerBehavior` |
| `IdleBehavior.cs` | 27 | None. The universal fallback |
| `PlayerBotBehavior.cs` | 370 | The base contract: **one** abstract member, `SerializableName` |

### Journal / gossip / speech — 2,136 lines

`BotSpeechResponder.cs` (530), `BotShopTalk.cs` (424), `BotEventJournal.cs` (422),
`ChatLibrary.cs` (160), `BotWantAd.cs` (352), `BotWants.cs` (161), `BotMurderReport.cs` (112),
`BotSocialGraph.cs` (67), plus the corpus itself.

### Economy / parties / guilds — 7,516 lines

`BotPartySystem.cs` (1303), `BotBuyOffer.cs` (972), `BotTreasureHunts.cs` (811),
`BotShop.cs` (786), `BotAppraisal.cs` (614), `BotDeathManager.cs` (605), `BotGrayWatch.cs` (514),
`BotTaming.cs` (502), `BotTradeWindow.cs` (484), `BotHousing.cs` (460), `BotShopDeal.cs` (445),
`BotFactionWar.cs` (426), `BotCombatPet.cs` (423), `BotSupplies.cs` (400), `BotDangerMap.cs` (396),
`BotEconomy.cs` (391), `BotDuelSystem.cs` (379), `BotBanking.cs` (298), `BotPlayerParty.cs` (259),
`RedTerritory.cs` (235), `BotPackAnimal.cs` (178), `BotMountHelper.cs` (166), `BotSeaEvents.cs` (163),
`BotGuilds.cs` (136), `BotFactionShields.cs` (40), `BotSceneRunner.cs` (71).

### Editor tooling / observability — 8,264 lines

`EditorReloadWatcher.cs` (1441), `AuditNavCommand.cs` (298), `AuditEdgesCommand.cs` (260),
`LiveMapSnapshot.cs` (302), `BotStuckTelemetry.cs` (545), `BotPadAudit.cs` (231),
`BotStatusPage.cs` (139), `AdminPanel/` (1149 across 5 files), the mark/record/del command set,
the generate/export command set, and `Nav/` (1795).

**This is the 18% we largely do not need** — it is their version of our `Core/Bridge` +
`tools/editor` + `NavigationCommands`, which are steps 4a/5a/5b/5c and already done.

### Core patches — 165 insertions across 8 files

Covered in §2. Only two are bot-driven; the rest are T2A/Felucca content tuning.

### 1.1 Navigation — what changed since `nav-format-comparison.md`

That document already maps their `waypoints.json` / `destinations.json` / `zones.json` against
`Data/Custom/navigation.json` field by field, with a reason for every divergence. **It is still
accurate and is not repeated here.** Only three things need adding:

- **Scale.** Theirs: 4,013 waypoints, 480 destinations, 6 zones, Felucca. Ours: 75 waypoints, 86
  edges, 27 destinations, 65 arrivals, 6 zones, 4 routes — Britain on Trammel. Their data does not
  transfer (wrong facet, wrong coordinates for our content); their *schema lessons* already did.
- **`Nav/DistanceField.cs` + `Nav/DestinationFieldCache.cs` are live and load-bearing.** A bounded
  Dijkstra flood from each destination, radius 60, persisted to `Data/Navigation/fields_cache.bin`
  (**55 MB**), fingerprinted with FNV-1a over the destination set so editing `destinations.json`
  self-invalidates it. This is their *final approach*: inside 60 tiles the bot stops using
  waypoints and walks the gradient, threading doors and interior floors. `nav-format-comparison.md`
  §5 flagged this as "revisit at the bot layer" — this is that revisit, and the answer is that we
  do not need it yet (§5).
- **`Nav/HpaGraph.cs` + `Nav/HpaCommands.cs` (764 lines) are dead code.** The load is commented out
  at `BotStartupManager.cs:83-84`: *"HPA load disabled — waypoint Traveler doesn't use it; loading
  the 60k-node graph only cost RAM."* Drop them. That they built the automated alternative and did
  not switch is the reason we hand-author, and it is now confirmed rather than inferred.

---

## 2. Portability

### COPY — runtime-independent data

**The chat corpus.** `Distribution/Data/PlayerBotChat/` — **123 `.txt` files at top level plus 22
in `Gossip/`, 1,724 lines total.** One utterance per line, `#` comments and blanks skipped,
filename is the category. Plain UTF-8, no serials, no coordinates, no type names, no API surface.

Placeholder vocabulary across the corpus: `{price}` 141, `{other}` 121, `{place}` 113, `{actor}` 95,
`{when}` 80, `{item}` 49, `{dest}` 32, `{name}` 16, `{dungeon}` 16, `{pet}` 10, `{mat}` 9,
`{short}` 6.

**One structural rule travels with it:** ambient lines are scanned flat, gossip templates live in
the `Gossip/` **subdirectory**, and `ChatLibrary` uses a non-recursive
`Directory.EnumerateFiles`. Reproduce that or a raw `{actor}` template gets spoken as ambient
chatter. The `_self` pairing (`pk.txt` / `pk_self.txt`, and six more pairs) is likewise load-bearing.

Roughly 18 files carry T2A-flavoured content and would want an editing pass for an EJ shard, but
nothing in the format is era-bound.

**Also COPY:** `zones.json` (3 KB, 6 zones) as a schema reference only — the coordinates are
Felucca.

**Do not copy:** `fields_cache.bin` (55 MB, derived and fingerprinted), `walk_atlas.pgm.gz` +
`walk_strips/` (29 MB of derived walkability rasters, referenced by nothing in their tree — a
debugging snapshot), and `Data/Pathfinding/*.swb` (45 MB, stock ModernUO's own prebaked pathing,
not a bot artifact).

### TRANSLATE — C# that maps onto ServUO APIs

The ModernUO-only surface is **narrow and mechanical**. Measured across all 136 files:

| Their API | Count | ServUO target |
| --- | --- | --- |
| `Core.Now` | 387 | A cached shim. **Not** raw `DateTime.UtcNow` — theirs is a tick-aligned value read once per loop iteration; 387 call sites × 1,600 bots turns it into a syscall storm |
| `System.Text.Json` | ~20 files | `Scripts/Custom/Core/JsonConfig.cs`. Newtonsoft 13.0.3 is already referenced at `Scripts/Scripts.csproj:33` — no new package |
| `IGenericWriter` / `IGenericReader` | 2 each | `GenericWriter` / `GenericReader` |
| `[SerializationGenerator(n)]` | 8 (7 types) | Hand-written `Serialize`/`Deserialize` + version int. Delete the 7 `Migrations/*.v0.json` |
| `Region.GetRegion<T>()` | 9 | No generic form in ServUO — a 5-line extension shim over `GetRegion(Type)` |
| `GetMobilesInRange<T>` / `GetItemsInRange<T>` | 14 generic | Non-generic `IPooledEnumerable` + type test. **The other 40 non-generic sites also need `.Free()`, which none of them currently have** |
| `PooledRefList<T>` | 2 | `List<T>` |
| `ValueStringBuilder` | 1 | `StringBuilder` |
| `[Constructible]` | 12 | `[Constructable]` |
| `OnResponse(NetState, in RelayInfo)` | 2 | Drop the `in` |
| `Server.Engines.Spawners.Spawner` | 2 types | `Server.Mobiles.Spawner` (`Scripts/Services/Spawner/Spawner.cs:15`), whose `Spawn()` is virtual at `:440` |
| `MoveDelays = Server.Movement.Movement` | 2 files | ServUO exposes these on `Mobile` |
| `Race.RandomSkinHue()` | 1 | Exists — `Server/Race.cs:178` |

**Free wins — zero occurrences of any of these:** `Core.LoopContext`, `[GeneratedEvent]` /
`ModernUO.CodeGeneratedEvents`, Serilog / `Server.Logging`, `Timer.StartTimer`, `ArrayPool`,
`CommunityToolkit.HighPerformance`, `Map.TryParse`, `records`, file-scoped namespaces, list
patterns. Every log line is `Console.WriteLine`. There is exactly **one** `EventSink` subscription
in 46,733 lines (`BotGrayWatch.cs:146`, `EventSink.AggressiveAction`, which ServUO has).

Three claims worth correcting, because they change the effort estimate:

- **`PlayerBot` itself is not source-generated.** It uses hand-written versioned
  `Serialize`/`Deserialize` (`PlayerBot.cs:941`, `:956`) at version 6, with a real migration ladder.
  That maps onto ServUO's pattern essentially unchanged. Only 5 of 136 files touch the generator.
- **All 59 item types in `EquipmentTable` exist in our tree** under `Server.Items` — spot-checked
  `ExecutionersAxe`, `GnarledStaff`, `MetalKiteShield`, `FemaleStuddedChest`, `RecallRune`,
  `BodySash`. So `BotItemFactory`'s reflection and `BotShop`'s `"Server.Items.X"` strings resolve
  as-is. The tables are T2A-*flavoured*, not era-*blocked*.
- **Their social systems do not use the engine's social systems.** `BotGuilds.cs:7-10` explicitly
  avoids `Server.Guilds` ("account-backed serialized entities; bots are transient") in favour of a
  static catalog plus one `int` per bot. `BotPartySystem` is its own manager, not
  `Server.Engines.PartySystem`. `BotFactionWar` is Order/Chaos simulated through shields and an
  `IsHarmfulCriminal` override, not real Factions. **The whole social layer is far more portable
  than the file names suggest.** Only `BotPlayerParty.cs` and `PlayerGroupBehavior.cs` touch the
  real `Party` API, and both are small.

#### The language-level decision — **settled: `LangVersion latest`**

No `<LangVersion>` is set in `Scripts/Scripts.csproj`, so SDK-style `net48` defaults to **C# 7.3**,
and there is no C# 8+ syntax anywhere in the ServUO tree today — our own custom code is uniformly
7.3-style. Their code uses ~91 target-typed `new()`, 74 switch expressions, 10 `{ get; init; }`,
8 range operators, and pervasive `is not` / `or` patterns.

**Decision: add `<LangVersion>latest</LangVersion>` plus an `IsExternalInit` polyfill**, rather than
rewriting ~250 syntax sites. `Scripts.csproj` is an upstream file, so **this goes in
`Scripts/Custom/MODIFICATIONS.md`** with the diff and the reason. The polyfill is a 5-line internal
static class and belongs in `Scripts/Custom/Core/`. Everything else in that list is compiler-only —
no runtime dependency, no new package.

### BLOCKED — and mostly not, once checked against our tree

This is where the survey changes the picture most. **Four of their eight patches are unnecessary
here**, and each was verified against our sources rather than assumed:

| Their patch | Verdict on ServUO pub57 |
| --- | --- |
| `SecureTrade.cs` — null `NetState` dereference | **Unnecessary.** ServUO's `SecureTrade` sends through `Mobile.Send(Packet)`, which already null-checks `m_NetState` and returns false — `Server/Mobile.cs:7398-7418`. A NetState-less bot can already be a trade participant |
| `BitmapAStarAlgorithm.cs` — doors are walls to a bot | **Unnecessary by construction** if the bot is a `BaseCreature`. `FastAStarAlgorithm.cs:93` sets `MoveImpl.AlwaysIgnoreDoors = bc.CanOpenDoors` for any `BaseCreature`, and `CanOpenDoors` defaults true for humanoid bodies (`BaseCreature.cs:1924`). Their patch exists *only* because their bot is a `PlayerMobile` |
| `BaseHouse.cs` — ownerless-house reaping | **Unnecessary.** ServUO's equivalent check is **already commented out** at `Scripts/Multis/BaseHouse.cs:3486`, and `RestrictDecay` exists with identical semantics (`:69`, `:77` → Ageless) |
| `CorpsePackets.cs` — null `EquipItems` | **Verify, do not assume.** ServUO's deserialize paths always assign a list (`Corpse.cs:848`, `:956`) and the ctor sets it (`:634`), but `Corpse.cs:1244` would still fault on a null. No evidence of the bug here; re-check if it ever appears |
| `StaminaSystem.cs`, `PublicMoongate.cs`, `SpellHelper.cs`, `map-definitions.json` | **Not bot code — do not port.** All four exist to make a Felucca-only T2A shard behave: pack-train stamina, a young-player moongate list that empties with no Trammel, T2A dungeon recall rules, and a Felucca season change. We are EJ/Trammel and want none of them |

**The one genuinely engine-coupled item is `BotHousing.cs`** (460 lines). It uses real `BaseHouse`,
`HousePlacement.Check` through a plain-Player probe (because `AccessLevel >= GameMaster`
short-circuits the check to Valid), then clears `Owner` and sets `RestrictDecay` so the house
outlives the ephemeral bot. All of those APIs exist in ServUO with the same names and the same
GM short-circuit, so it is portable — but it places permanent world content, and it is explicitly
a "research spike" in their own header. **Out of scope for the bot-mobile layer.**

`BotMurderReport.cs` (112 lines) is the other one to flag. It exists because a murder count comes
from the victim answering the report gump, and a bot has no `NetState` to show it to; theirs calls
`Server.Engines.PlayerMurderSystem` directly. ServUO has no such namespace — counts live on
`Mobile.Kills` (`Server/Mobile.cs:11766`) with the `ReportMurderer` gump path. Small file,
semantically delicate, and only needed once bots can kill each other.

---

## 3. Era and facet dependencies

Smaller than expected, and the split is clean.

### Facet — 39 `Map.Felucca` literals, all CODE, no abstraction

There is no facet indirection anywhere: `Nav/DestinationFieldCache.cs:57`, `LiveMapSnapshot.cs:38`,
`BankFixtures.cs:109`, `BotDeathManager.cs:579`, `Behaviors/LifecycleTransitions.cs:80,82`,
`BotTreasureHunts.cs:115`, `RedTerritory.cs:172,206`, `AdminPanel/BotPanelActions.cs:247-313`,
`EquipmentTable.cs:708` (every bot's recall rune is marked to Felucca), and the audit/editor
commands. **`Map.Trammel` appears zero times.**

There is no "no Trammel" *rule* in bot code — Trammel simply does not exist to it. The prohibition
is data-side, in `expansion.json`'s `MapSelectionFlags`. For us this is 39 single-token changes,
best done as a `BotMap` static during the port rather than after. Much of it sits in tooling we are
not porting anyway.

### Era — two live checks, and one executable specification

| Site | Kind | Detail |
| --- | --- | --- |
| `BotBanking.cs:52` | CODE | `Core.ML ? 60000 : 5000` — the withdrawal ceiling, read the way `Banker` reads it. **The only genuinely expansion-parameterised number in the tree.** On EJ it yields 60000, correctly |
| `BotDeathManager.cs:380` | CODE | `if (Core.AOS)` guarding corpse restore-info. Dead code on their shard; **live on ours**, which is the better path |
| `BotSkillTemplate.cs:4,58` | CODE | The T2A doctrine: stat cap 225 total / 100 each, skill cap 700 = seven GM skills. Every template is exactly 7 skills — the cap expressed *structurally* rather than as a number |
| `EditorReloadWatcher.cs:389,418,497` | CODE (test) | The `[t2a` audit rig: `RawInt <= 100 && sum <= 225`. This is the executable specification of the era rule |
| `Configuration/modernuo.json` | DATA | `stats.statMax: "100"` |
| `Configuration/FeatureFlags/flags.json` | DATA | `young_player_system: false` — the **only** young-player statement. Zero `Young` references in bot code; bots are never young |
| `Configuration/expansion.json` | DATA | `Id: 1`, T2A features only, `MapSelectionFlags.Felucca` alone |

**No `Core.T2A`, `Core.SE`, `Core.UOR`, `Core.LBR`, `Core.SA`, `Core.HS` or `Core.Expansion`
comparisons exist anywhere in `CustomBots/`.**

Critically, **`CustomBots` never assigns `SkillsCap` or `StatCap`.** `PlayerBot.cs:359-388` sets
`Skills[x].Base` and `RawStr/Dex/Int` directly; the caps are enforced by config plus templates that
are legal by construction. That is a clean design for porting — there is no cap plumbing to
reproduce, only a decision about what numbers to use. For an EJ shard the templates are a *starting
palette* to be rescaled, not a constraint.

**Guard rules are CODE and are the subtlest part.** The recurring correct idiom, stated twice with
a warning attached (`RedTerritory.cs:122-128`, `BotGrayWatch.cs:374-380`): use
`Region.Find(p, map).GetRegion<GuardedRegion>()`, **not** `IsPartOf<GuardedRegion>()`, because
`TownRegion` derives from `GuardedRegion`. The full predicate is
`region != null && !region.IsDisabled() && region.IsGuardCandidate(m)`. All of those APIs exist in
ServUO. Get this wrong and reds walk into guard zones and die on repeat — which is exactly the bug
their comments are memorialising.

**Item and price tables are era-curated CODE** — `EquipmentTable.cs` (gold by tier 40→1400;
`:1721` `WarFork()` with the comment *"T2A fencing — no wakizashi (SE-era)"* is the clearest
single era-exclusion line in the tree), `BotShop.cs:130-215`, and `BotAppraisal.cs:11-13`'s era
shorthand (`hally`, `bm`, `sa`, `xbow`, `vanq`). For EJ these are a flavour decision, not a
blocker.

---

## 4. Their bot mobile, and how ours should be structured

### Theirs

`PlayerBot : PlayerMobile` — `PlayerBot.cs:28`. Class rolled once from 18 `BotClass` values, tier
from 7 `BotSkillTier` values on a bell curve, both persisted. Personality is a struct of
per-behaviour tendency weights plus `[Flags]` traits. Serialization is hand-written at version 6
with a real migration ladder.

**Lifecycle** is three global timers, no per-bot timer:

| Timer | Interval | Budget |
| --- | --- | --- |
| Behaviour tick (`BehaviorTickManager.cs:22`) | 2 s | none — every live bot, every tick |
| Lifecycle (`BotLifecycleManager.cs:42`) | 60 s | `MaxTransitionsPerTick = 5` |
| Session / population curve (`BotSessionManager.cs:59`) | 60 s | `MaxLogoutsPerTick = 4` |

The lifecycle rolls among four targets — BankSitter, Adventurer, Traveler, Idle — weighted by
personality, halving the current behaviour's weight to bias toward change
(`BotLifecycleManager.cs:265-296`). Ten skip conditions guard it, including a **non-rate-limited**
force-rebrain of any bot that has gone `Murderer` into `PK`, which their comment documents as the
repair path for corrupted reds.

**Persistence: bots are transient by design**, with a session shape — every bot gets a
`SessionEndsAt` 1–4 h out, says a goodbye line, and logs out; a 24-hour population curve scales the
target (`BotPopulation.TargetCount = 1600`, dead at 05:00, packed at 19:00). Fixed-role bots from
the spawn editor are exempt.

### The finding that decides the architecture

Their whole transient model rests on a ModernUO behaviour **we do not have**. From
`BotStartupManager.cs:6-10`:

> ModernUO's world save persists player characters via their ACCOUNT, not as free-standing world
> mobiles — so accountless PlayerBots are NOT written to the world save and do NOT survive a
> restart.

ServUO's `StandardSaveStrategy.SaveMobiles` (`Server/Persistence/StandardSaveStrategy.cs:74-76`)
takes `World.Mobiles` and writes **every** mobile in it. There is no account filter. On our tree,
1,600 accountless bots would be serialized into every save, and `PlayerBot`'s currently-never-run
`Deserialize` would suddenly become live.

We already have the right idiom for this and use it: `Timer.DelayCall(Delete)` at the tail of
`Deserialize` (`DailyLifePatron.cs:137`, `DailyLifeTownsfolk.cs:187`; CLAUDE.md §5).

### Recommendation — **`BaseCreature`, not `PlayerMobile`** *(decided)*

Four independent reasons:

1. **`NavWalker` requires it.** `NavWalker(BaseCreature mobile)` — `NavWalker.cs:63`. It drives
   `BaseAI.DoMove`, warns on `ForceStayHome`, and works around `WalkRandomInHome`. Its own class
   comment says it is shared by daily life "and, later, the bot layer, because the hop-failure
   policy is the part that has to be right and there should be exactly one of it."
2. **It deletes a patch.** `CanOpenDoors` is stock on `BaseCreature` and defaults true for humanoid
   bodies, so `FastAStarAlgorithm` routes a bot through doors with no engine edit.
3. **The `PlayerMobile` dependency is shallow.** Only 15 overrides, and `OpenTrade`
   (`Server/Mobile.cs:10838`), `ApplyNameSuffix` (`:1152`), `CheckShove` (`:3516`) and
   `ShouldCheckStatTimers` (`:6260`) are all virtual on `Mobile`.
4. **It matches the actor pattern we already run** — `DailyLifeTownsfolk : BaseCreature,
   IDailyLifeActor` with a `DailyLifeAI : VendorAI` injected through `ForcedAI`.

### Where a `BaseCreature` bot reads as an NPC to a real player

This is the cost of the recommendation, and it is worth being precise about. Five surfaces:

| Surface | Behaviour as a plain `BaseCreature` | Fix |
| --- | --- | --- |
| **Notoriety** (name hue) | `Notoriety.cs:441-443`: a target that is not `InitialInnocent` and whose body is not human falls through to `CanBeAttacked` (grey). A **human body already escapes** that clause, but the `InitialInnocent` gate is what makes it read blue | **Overridable.** `BaseCreature.InitialInnocent` is `virtual` (`BaseCreature.cs:1786`) and defaults to an XmlAttach lookup. Override it to `true`. `Murderer`/`Kills` (`Mobile.cs:11766`, `:11849`) and `Criminal` are all on `Mobile`, so red/grey flagging works unchanged |
| **Paperdoll** | Works free. `Mobile.CanPaperdollBeOpenedBy` (`Mobile.cs:6654`) gates on `Body.IsHuman`; `BaseCreature.OnDoubleClick` (`:5723`) only intercepts for a GM on a non-human body, then calls base | None needed |
| **Context menu** | `BaseCreature.GetContextMenuEntries` (`:4559`) adds Rename, AI commands, Tame and Teach — all gated on `Controlled`, `Commandable`, `Tamable`, `CanTeach` | Leave those flags false and the menu is just `PaperdollEntry`, which is **exactly what a third party sees on a real player** — `PlayerMobile.GetContextMenuEntries` is almost entirely `from == this` self-entries |
| **Party** | **Breaks.** `AddPartyTarget.cs:30-32` refuses a human-bodied non-`Player` mobile with the classic NPC line *"Nay, I would rather stay here and watch a nail rust."* | **Fixable.** `Mobile.Player` has a public setter — set `Player = true`, which is precisely what their `PlayerBot` does at `:250`. Side effect to measure: it raises the combat timer to `TimerPriority.EveryTick` |
| **Name / guild display** | Works free. `Guild` (`Mobile.cs:9422`), `GuildTitle` (`:9266`) and `DisplayGuildTitle` (`:787`) are all on `Mobile` and serialized there (`:6485`, `:6511`). The name line is `prefix + Name + suffix` where suffix defaults to `Title` (`:12570-12580`) | `BaseCreature.ApplyNameSuffix` (`:1469`) only adds "(Paragon)" before deferring to base — override it for a guild tag exactly as theirs does |

So: **one real break (party), fixed by `Player = true`; one override (`InitialInnocent`); one
override for cosmetics (`ApplyNameSuffix`); three surfaces free.** `Player = true` on a
`BaseCreature` is the same lever they pull, and it is on `Mobile`, not `PlayerMobile`.

### What ours reuses rather than re-ports

- `NavWalker` — one walker, per its own comment. Do not port `TravelerBehavior`'s leg-walking.
- `Nav` as the query facade (`Nav.TryRoute`, `NearestDestination`, `TryPickArrival`, …). Their
  `WaypointGraph`/`WaypointRegistry`/`DestinationCatalog` are already answered by 4a.
- `LiveRegistry.Register` from **both** `OnAfterSpawn` and the tail of `Deserialize`, so bots draw
  on the editor's live layer without anyone walking `World.Mobiles`.
- `LoopQueue` for anything off the game thread. `Nav` and `FastAStarAlgorithm` are game-thread-only.
- An `IDailyLifeActor`-style `Commuting` stand-aside, so `BaseAI.DoActionWander` stops fighting the
  route — `DailyLifeAI.cs:53-70` already solves exactly this and explains why.
- `HealthCheck.Register("Bots.…", …)` rather than growing `[CoreSmoke`.
- `CustomLogger.For("Bots")` rather than `Console.WriteLine` ×hundreds.

Two house-rule notes: their `BehaviorTickManager` scans all of `World.Mobiles` every 2 seconds with
a comment reading *"with <500 bots this is trivially cheap"* while `TargetCount` is **1600** —
CLAUDE.md §15 forbids habitual `World.Mobiles` walks and `LiveRegistry` is the existing answer. And
a bot system needing staff notification is the **fourth** `NotifyStaff` caller that `SHARD.md:270`
said should become a `Custom/Core` helper.

**Population is the open sizing question.** 1,600 bots on net48, in a world that already holds
~20,000 mobiles, driven by a 2 s tick plus per-bot 200–400 ms step timers, is not a number to
inherit unexamined. Start at a fraction of it and measure.

---

## 5. Proposed order — the bot-mobile layer only

Sized in sessions. Coupling was measured, not guessed: `DestinationCatalog` is referenced by 30
files, `ChatLibrary` by 27, `WaypointGraph`/`WaypointRegistry` by 24. **We already have equivalents
for two of those three** — `ChatLibrary` is the only genuinely new foundation.

| # | Session | Contents | Visible result |
| --- | --- | --- | --- |
| **6a** | **A bot you can look at** | `BotClass`, `BotSkillTier`, `BotSkillTemplate`, `CrafterType`, `NamePool`, `SpeechHues`, `BotPersonality`, `EquipmentTable`, the bot mobile itself, `IdleBehavior`, `[SpawnBot`. ~3,500 lines, **zero cross-layer dependencies**. Includes the `LangVersion` change and its `MODIFICATIONS.md` entry | `[SpawnBot mage 6` drops a named, dressed, correctly-skilled human at your feet. Double-click opens its paperdoll. `[BotInfo` dumps its stats and skills |
| **6b** | **It talks** | `ChatLibrary` + the corpus copy, `PlayerBotBehavior`'s speech helpers, `BotSpeechResponder`, `SpeechHues` wiring | Bots answer when you greet them, by name, in their own colour |
| **6c** | **It moves** | `BehaviorRegistry`, `BehaviorTickManager` (rebuilt on `LiveRegistry`), a `Traveler` behaviour built on `NavWalker` + `Nav`, `Commuting` stand-aside, `LiveRegistry` registration | Bots walk Britain's real roads between real destinations, and appear on the editor's live layer |
| **6d** | **It has a day** | `BotLifecycleManager`, `LifecycleTransitions`, `BotPersonality` wiring, `BankSitter`, `Visitor`, `Shopper`, `BotSessionManager` + population curve, `HealthCheck` registration | The bank crowd forms and disperses; the town's population rises and falls on a curve |
| **6e** | **It persists correctly** — **DONE** | **Decided: XmlSpawner2, with the seed as `[CommandProperty]` setters on the bot.** A `Spawner` subclass was rejected: it is invisible to the editor's spawner layer, is not authorable from its form, and puts authored placement in the world save instead of in git. The recipe is derived from the nav graph and **written to `Spawns/Custom/<facet>/GG_BotPop.xml`** rather than minted as world items, which deletes upstream's whole stacked-spawner failure class — `[ClearSpawners`, the `StartupCap` runaway brake and `BankFixtures`' count top-up all exist only to mop that up. Ephemeral-delete on load was already done and is stronger than upstream's orphan sweep. `[SetBotPopulation` became `[BotPopulation`; `[BotWhere` and `[BotGoals` were **not** ported — the editor's live map and bot card already answer both. `BotSessionManager` ported as `BotSession`. New: `[BotPopulationAudit`, `[BotPopulationGen`, `Bots.Recipe` | Restart the shard and the population rebuilds itself, with nothing left in the save |

Everything beyond 6e — Adventurer, PK, dungeons, economy, parties, guilds, taming, housing,
treasure hunts — is a **separate layer** and out of scope for this ordering, as the brief asks.

**The whole 6a–6e ordering is now done.** One finding from 6e is worth carrying forward because it
is not a bot fact at all: **XmlSpawner2 refuses to construct a type whose constructor is not marked
`[Constructable]`, and reports success while doing it** — `CreateObject` defaults
`requireconstructable` to true (`XmlSpawner2.cs:11263-11265`), and a refused spawn sets `status_str`
to `"invalid type specification"` and returns `true`, so the spawner reschedules its timer and sits
at a count of zero for ever with nothing in any log. Anything this port ever spawns through a
spawner needs the attribute, and the editor's type vocabulary now filters on it.

The **population sizing question** §4 left open is answered and instrumented rather than guessed:
the shipped target is **60**, not 1600, and `BotTickManager` times its own pass so `Bots.Recipe`
reports the cost beside the live count (51 bots: 0.7 ms mean of a 2,000 ms budget). Raising it is
arithmetic now.

### Re-survey these four before porting them

Their **September 2026 release (`fe18a469`)** substantially rewrote four modules that this port has
not reached. The counts in §1 are stale for these specifically, and the portability judgements in §2
were made against code that no longer exists in that form. Measured against the `91848d8` tree:

| Module | Then → now | What changed |
| --- | --- | --- |
| **Resurrection** | `Behaviors/GhostBehavior.cs` 147 → **573**; **new** `BotResurrectAid.cs` (408) and `Behaviors/GhostExitBehavior.cs` (483); `BotDeathManager.cs` 605 → 793 | Ghosts now seek a healer NPC, a real ankh, or a willing bot — Magery ≥ 80 plus reagents, or Healing/Anatomy ≥ 80 plus a bandage. Blue helps blue; a red is helped only by another red or a partymate. Their header notes the old "a wandering healer found them" timer was ~17 of 19 resurrections in one soak and is gone |
| **Party formation** | `BotPartySystem.cs:140-145` — new `RecruitMin = 2` / `RecruitMax = 4` (was a hardcoded `RandomMinMax(1, 3)`); `RecruitRange` 20 → 30; `Behaviors/PartyMemberBehavior.cs` 89 → 169 | Leader + 2–4 = **3–5 members**. Their own file header still says "1-3 answer" and is stale — do not port the comment |
| **Combat kiting** | `Behaviors/AdventurerBehavior.cs` 2636 → **3522**; kite block rewritten ~2900-3045 | Crowd-aware standoff widening (retreat from the pack's centroid, not the target), and a `KiteBreakGrace` of 2.5s during which a retreating bot walks rather than rooting itself casting |
| **Gossip** | `BotEventJournal.cs` 422 → **806** | Repeat-killer gossip at 3+ murders by one actor, and conversational replies — a bystander answers the gossiper from the new `Gossip/react_*.txt`. `red` sightings deliberately down-weighted 2.0 → 0.8 |

Also new, and a **prerequisite for the resurrection and gossip sessions**: the corpus grew from 123
flat `.txt` + 22 in `Gossip/` to **143 + 24**, adding `ghost_plea`, `res_offer`, `res_fail`,
`party_lead_taken`, `pk_loot`, `pk_prize` and seven `Gossip/react*` files. Nothing was removed, so
`Data/Custom/BotChat/` is still a valid subset — but it wants refreshing before either lands.

Not changed, and worth knowing because a later session may hope otherwise: **the gather sites are
still exactly three** (`MiningSpot` 2, `LumberSpot` 1, `GatherSpot` 0) and `GatherSpots.cs` is still
the retired stub.

Session 6a is deliberately the whole visible payoff of the class/tier/equipment model with none of
its behaviour, because that is the part that is pure translation and proves the language-level and
serialization decisions before anything depends on them.

---

## 6. Licensing

**Not legal advice.**

| Tree | License | Holder |
| --- | --- | --- |
| `uo-offline-server` / `CustomBots/` | **GPL-3.0** (per the `Klein187/uo-offline` README) | **Klein187** |
| `ModernUO` | **GPL-3.0** (`ModernUO/LICENSE`, and per-file headers, e.g. `Projects/Server/SecureTrade.cs:1-14`) | ModernUO Development Team (Kamron Batman) |
| This shard / ServUO / RunUO | **GPL-2.0-or-later** — `LICENSE` line 294 `Copyright (C) 2013 ServUO`; per-file headers read *"either version 2 of the License, or (at your option) any later version"* (`Server/Gumps/GumpButton.cs:16-17`) | ServUO / The RunUO Software Team |

The `CustomBots/` tree carries **no per-file copyright or license headers** — all 136 files open
with the same `// ===…` prose banner, a house style rather than a notice — so it inherits
`uo-offline`'s GPL-3.0 by project.

**Conclusion: translated code may go in the public `KanedaBytes/ServUO` fork, as GPLv3-licensed
derived work, with attribution to Klein187 and the GPL-3 notice retained.** GPL-2.0-**or-later**
is upgrade-compatible with GPLv3, so the combination is permitted; the resulting combined work is
distributed under GPLv3. Practical consequences:

- Ported files should carry a header naming **Klein187** as the original author, the upstream
  (`Klein187/uo-offline`, GPL-3.0), and the GPL-3 notice. The provenance is unambiguous today and
  will not be later.
- Re-derive the patch equivalents against ServUO's own GPL-2.0-or-later sources rather than copying
  ModernUO diff text — and three of the four are unnecessary anyway (§2), so this is nearly free.
- The rest of the bundle does not travel and does not need to be considered: `ClassicUO/` and
  `Razor/` are shipped binaries. Note in passing that `Razor/license.txt` is a bespoke, non-free
  agreement with an explicit anti-redistribution clause — irrelevant to this port, but not
  something to bundle onward.

---

## Summary

- **46,733 lines**, of which ~8,300 (18%) is nav/editor tooling we already have as steps 4a/5a–5c,
  and 764 lines are confirmed dead code.
- The ModernUO-only API surface is **narrow and mechanical** — `Core.Now` ×387 and
  `System.Text.Json` ×20 files dominate it; there is no `LoopContext`, no generated events, no
  Serilog, no pooling library, and one `EventSink` subscription.
- **Four of their eight patches are unnecessary on ServUO**, verified against our sources. The
  other four are T2A/Felucca content tuning we do not want.
- The era surface is small and the facet surface is 39 mechanical literals. **No cap plumbing to
  reproduce.**
- The architecture decision is **`BaseCreature`**, which costs one real break (party, fixed by
  `Player = true`) and two overrides, and buys `NavWalker`, stock door pathing, and consistency with
  the daily-life actors.
- Their transient-bot model **does not survive the move** — ServUO saves every mobile — but the
  ephemeral-actor idiom we already use replaces it exactly.
- Licensing is clear: GPLv3 derived work, attributed to Klein187, in the public fork.
