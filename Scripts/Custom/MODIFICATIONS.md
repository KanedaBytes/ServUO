# MODIFICATIONS.md

The log of every change this shard makes to **upstream ServUO** files, and of the upstream
behaviours our custom code silently depends on.

Read this before merging upstream. It is the checklist for every ServUO upgrade.

---

## The rules

1. **Never edit upstream files.** Prefer, in order: a new class in `Scripts/Custom/`,
   subclassing, an `EventSink` handler, or a `Configure()` / `Initialize()` hook.
2. **If an upstream edit is genuinely unavoidable**, log it below with:
   - the file and the diff,
   - *why* no `Custom/`-side approach worked (be specific — "the field is private" is a reason,
     "it was easier" is not),
   - the upstream commit it was written against.
3. **Flag any modification that touches persisted data.** A bad merge there corrupts saves
   rather than failing the build, so those entries carry a warning marker.
4. Config files, new `Data/Custom/` files, new `Spawns/Custom/` files and new `Config/*.cfg`
   are **not** modifications — they are additions. They go in the section below so nobody
   mistakes them for upstream edits during a merge.

---

## Upstream files modified

### 1. `Config/Compiler.cfg` — `Dynamic=True` → `Dynamic=False`

**Why.** `ScriptCompiler.Compile()` never checks the exit code of the `dotnet build` it shells
out to. It returns `true` regardless and calls `Assembly.LoadFrom("Scripts.dll")`, so a script
that fails to compile boots the **previous, stale DLL** and the server runs normally with the
change silently absent.

**Verified by reproduction in this tree.** A deliberate syntax error in one script made
`dotnet build` exit 1 and left `Scripts.dll` byte-identical; the server then booted straight
past `Build FAILED. 3 Error(s)`, verified 6017 item and 1361 mobile types from the stale DLL,
loaded 161,438 items and 15,379 mobiles, and bound port 2594.

**Fix.** Turn off boot-time compilation entirely and build through `build.ps1` / `build.sh`,
which check the exit code and refuse to launch a server whose scripts did not build.

**Merge note.** This is a config file, so an upstream change here will conflict loudly rather
than silently. If upstream ever fixes `ScriptCompiler.Compile()` to check the exit code, this
modification can be reverted.

### 2. `Scripts/Scripts.csproj` — added a `Newtonsoft.Json` PackageReference

```diff
   <ItemGroup>
     <PackageReference Include="System.Data.DataSetExtensions" Version="4.5.0" />
+    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
   </ItemGroup>
```

**Why.** There is no JSON support anywhere in stock ServUO, and `net48` has no built-in
`System.Text.Json`. Custom config (`Data/Custom/`) and the admin API both need JSON. This
cannot be done from `Custom/` — a package reference has to live in the project file.

**Why Newtonsoft rather than System.Text.Json.** Verified: on `net48`, Newtonsoft.Json 13.0.3
resolves as a **single package with zero transitive dependencies**. `System.Text.Json` would
pull in several `System.*` compatibility packages and has a weaker Mono story.

**Merge note.** Additive and in its own line, so an upstream change to this file conflicts
loudly rather than silently.

### 3. `.gitignore` — two shard additions

```diff
+!/build.sh
+/Newtonsoft.Json.dll
```

**Why.** The stock `.gitignore` ignores `*.sh` (line 35), which would silently exclude the new
`build.sh` build script — it is source, not an artifact. And root-level build artifacts are
listed individually rather than by wildcard, so the NuGet-restored `Newtonsoft.Json.dll` that
lands next to `ServUO.exe` was untracked-but-not-ignored.

**Merge note.** Appended in a clearly marked block at the end of the file, so upstream edits
near the top will not conflict.

### 4. `Scripts/Scripts.csproj` — added `<LangVersion>latest</LangVersion>`

```diff
   <PropertyGroup>
     <TargetFramework>net48</TargetFramework>
+    <LangVersion>latest</LangVersion>
     <OutputType>Library</OutputType>
```

**Why.** An SDK-style project targeting `net48` defaults to **C# 7.3**, because the SDK maps the
language version from the target framework and net48 predates the newer defaults. Nothing in
stock ServUO uses anything newer, so this was invisible until `Scripts/Custom/Bots/` — the ported
PlayerBots leaf set is built on **switch expressions** (C# 8) and `is not` / `or` patterns
(C# 9): 25 switch expressions in `BotSkillTemplate.cs`, 45 in `EquipmentTable.cs`. Rewriting
~250 syntax sites into 7.3 would have meant maintaining a permanent hand-divergence from the
upstream this code is ported from, and every future port would pay it again.

This cannot be done from `Custom/` — a language version is an MSBuild property and has to live
in the project file.

**Why `latest` rather than a pinned version.** These are all **compiler-only** features: switch
expressions, pattern combinators and target-typed `new` emit ordinary IL against the same net48
BCL and add no runtime dependency. The one C# 9 feature that *does* need a runtime type —
init-only setters — is covered by `Scripts/Custom/Core/IsExternalInit.cs`, a 5-line polyfill
supplying `System.Runtime.CompilerServices.IsExternalInit`, which the BCL only ships from
.NET 5 onward. Nothing in the tree uses `init` yet; the polyfill lands with this change so the
first file that wants one simply works.

**Nullable reference types are deliberately NOT enabled.** `latest` raises the language version
only; `<Nullable>` stays unset, so no existing file changes meaning and no new warnings appear.

**Merge note.** Additive, on its own line, inside the first `PropertyGroup`. An upstream change
to this file conflicts loudly rather than silently. If upstream ever sets `LangVersion` itself,
take theirs if it is C# 9 or higher.

### 5. `Scripts/Mobiles/Normal/BaseCreature.cs` — a `PlayerBot` may walk over an uncontrolled creature

```diff
     public override bool OnMoveOver(Mobile m)
     {
+        bool? bot = Server.Custom.BotShove.OnMoveOver(this, m);
+
+        if (bot.HasValue)
+        {
+            return bot.Value;
+        }
+
         if (m is BaseCreature && !((BaseCreature)m).Controlled)
         {
             return (!Alive || !m.Alive || IsDeadBondedPet || m.IsDeadBondedPet) || (Hidden && IsStaff());
         }
 
         return base.OnMoveOver(m);
     }
```

**The first `.cs` edit in this tree.** Written against ServUO `pub57` (assembly 57.4).

**Why.** A bot cannot step onto a tile a stock NPC is standing on, and Britain's shop doorways are
where stock NPCs stand permanently. Measured, over two windows:

- `brit-shop-armorer`'s arrival at `1446,1648` is a **counter** (`[NavAudit`), its only standable
  neighbour within one tile is `1445,1647`, and **Giles the Armorer stands on it**. Xaviera the
  Weaponsmith stands beside him. Three of one thirteen-minute window's four terminal failures were
  bots trying to get into or out of that shop.
- The vendor in the Trinsic alchemist doorway that prompted this **never reached any ledger at
  all**, because the stuck ladder waited her out — so the cost is systematically under-reported by
  every instrument that only records terminal failures.

**Why no `Custom/`-side approach works.** `OnMoveOver` is `virtual` on `Mobile`, and every mobile
this shard constructs already routes a bot mover through `Scripts/Custom/Bots/BotShove.cs` — that
is how a bot walks through another bot and through a daily-life actor. But the mobile being
**shoved** here is a stock `BaseCreature` that the shard does not construct: an armorer, a banker,
a guard, spawned by upstream data through upstream classes. There is no instance of ours to
override and no hook between `Mobile.Move` and the occupant's `OnMoveOver`.

**Why this file and not `Movement.cs`.** On a shard running `MovementImpl` this would need a second
edit, because there a mobile is refused at `CheckMovement` before `OnMoveOver` is consulted
(`Scripts/Services/Pathing/Movement.cs:345-356`, `:409`). **This shard runs `FastMovementImpl`,
which never builds a mobile list at all** (`FastMovement.cs:23-26`, `:361-484`), so the occupant's
`OnMoveOver` is the only gate and this is the only edit. That is not an assumption: the
`Nav.Movement` health check reports the installed implementation on every `[CoreSmoke`, and it
reads `FastMovementImpl`. **If a merge ever flips that back, this edit stops being sufficient and
the two-edit version has to be reconsidered rather than assumed.**

**What it costs.** It applies to **every** uncontrolled creature, monsters included — a bot walks
through a dragon. That is upstream uo-offline's rule too (`PlayerBot.CheckShove => true`,
`CustomBots/PlayerBot.cs:587-595`, with no engine change on their side at all), and there is no
combat layer here yet for a narrower rule to serve. Revisit with the combat session; seam 13 in the
Bots README is the place it will be felt first.

**What it deliberately does not change.** `Scripts/Mobiles/PlayerMobile.cs:3487-3494` is untouched,
so **a bot still yields to a real player**, and a real player shoving a bot still pays the engine's
full-stamina rule. That half of the deviation was always the right half.

**What travels under it, as of the walk-audit fix.** The guard calls
`BotShove.OnMoveOver(this, m)`, and that method's mover test is now `mover as IBotMover` rather
than `mover as PlayerBot` — so this edit also passes the walk audit's **probe**, which is a plain
`BaseCreature` in `Core/Navigation` and was therefore refused by every occupant class a real bot
walks through. **No second edit was needed and none was made**; the `Custom/`-side predicate
widened and this file did not change. The interface has exactly two implementers, `PlayerBot` and
`WalkAuditProbe`, and is tested for in exactly one place. Measured: `[WalkAudit selftest` read
`SELF-TEST BROKEN` before and `SELF-TEST OK` after, because its must-pass control hop at
`1849,2711` is a tile a mobile stands on and the probe could not push past it.

**Merge note.** Three lines at the top of one method, above two branches left exactly as upstream
wrote them, with a `CUSTOM SHARD EDIT` comment naming this entry. An upstream change to
`OnMoveOver` will conflict loudly. If it does, the guard goes back at the top of whatever the new
body is.

*(Entry 5 is the only `.cs` edit; entries 1 to 4 are project,
config or ignore files. Each future entry follows the format above.)*

---

## Deliberate deviations from stock helpers

Not upstream edits, but places where custom code knowingly does **not** use the stock helper.

### `CustomPersistence` writes its own file rather than calling `Persistence.Serialize`

`Server/Persistence/Persistence.cs:31` opens with `file.OpenWrite()`, which is
`FileMode.OpenOrCreate` and **does not truncate**. A save that writes fewer bytes than the
previous one therefore leaves stale tail bytes behind. That is harmless for a strictly
forward-reading loader and fatal for anything that reads to EOF.

`Scripts/Custom/Core/CustomPersistence.cs` opens its own `FileStream` with `FileMode.Create`
(which truncates) and wraps it in the same `BinaryFileWriter`. **Reads still go through
`Persistence.Deserialize`**, for its directory-creation and zero-length-file handling.

If upstream ever changes `Persistence.Serialize` to truncate, this deviation can be dropped.

---

## Files that look like modifications but are not

| Path | What it is |
| --- | --- |
| `CLAUDE.md`, `SHARD.md` | New shard documentation at repo root |
| `build.ps1`, `build.sh` | New build scripts. The stock `_winrelease.bat`, `_windebug.bat` and `makefile` are left untouched but are not the supported run path |
| `Scripts/Custom/**` | All custom code. Picked up automatically by `Scripts.csproj` default globbing — no project-file edit needed |
| `Data/Custom/**` | New folder for custom JSON config |
| `Spawns/Custom/**` | New folder for custom XmlSpawner XML, kept out of the stock `Spawns/` files |
| `Config/Server.cfg` | Shard name, and port `2594` (committed upstream of this work) |
| `Config/DataPath.cfg` | Client data path (committed upstream of this work) |
| `Config/Custom.cfg` | New config file, scope `Custom`. Auto-discovered at boot; no registration needed |
| `LICENSE-BOTS` | New file. `Scripts/Custom/Bots/` is a derived work of `Klein187/uo-offline` (GPL-3.0), so it carries its own licence notice. The repository's own `LICENSE` (GPL-2.0-or-later) is untouched |
| `Scripts/Custom/Core/IsExternalInit.cs` | New file, not a modification. The net48 polyfill that lets `init` accessors compile; paired with entry 4 above |
| `Spawns/Custom/trammel/GG_OldMarta.xml` | New custom spawn definition. Imported with `[GG_Reimport`; the `GG_` name prefix is the set handle |

---

## Upstream seams this shard depends on

Behaviours in stock ServUO that custom code relies on without owning. **If a merge changes any
of these, the dependent system breaks quietly.** Re-verify each after every upstream merge.

### Script loading

- `Scripts/Scripts.csproj` uses SDK **default globbing**, so files under `Scripts/Custom/` are
  compiled with no project-file entry. If upstream ever switches to explicit `<Compile Include>`
  items, every custom file stops building.

### Boot and priority

- `ScriptCompiler.Invoke("Configure"/"Initialize")` calls **every** `public static void`
  parameterless method of that name, ordered by `[CallPriority(n)]`, **lower first**, untagged
  = 0. Custom systems rely on `Configure()` running before `World.Load` and `Initialize()`
  after it.

### Threading

- `Timer.Slice()` runs timer callbacks on the core thread, and `MessagePump.Slice()` runs packet
  handlers there too. `Custom/Core/LoopQueue` assumes its drain `Timer` therefore executes on
  the game thread.
- `World.Save` freezes the world synchronously on the core thread. Custom `WorldSave` handlers
  assume they run inside that freeze, with no concurrent world mutation.

### Regions

- `Region.Load()` instantiates any class named in a `Data/Regions.xml` `type=` attribute by
  reflection, requiring a `(XmlElement, Map, Region)` constructor.
- `Region.Register()` re-resolves mobiles already standing in the affected sectors. The
  restricted-zone system depends on this so a newly drawn or resized zone applies to current
  occupants without a manual sweep.

### Jail

- `Scripts/Regions/Jail.cs` and the two `Jail` region entries in `Data/Regions.xml` (`:699`
  Felucca, `:1770` Trammel, rect `5271,1159` 41×33). The custom jail layer teleports into this
  region and reads its bounds.
- The ten subsystems that check for the jail region (`AccountHandler.cs:466`,
  `SkillCheck.cs:366`, `SpellHelper.cs:874`, `HelpGump.cs:209,266`, `BagOfSending`,
  `BallOfSummoning`, `BraceletOfBinding`, `HornOfRetreat`, `BaseCreature.cs:6605`,
  `PreventInaccess.cs:24`) are what make the sentence meaningful. We add no enforcement of our
  own, so if any of these checks is removed upstream, prisoners silently regain that ability.

### Quests

- **`Item.Nontransferable => QuestItem`** (`Server/Item.cs:3878`) and its enforcement at drop,
  trade and stack (`Item.cs:1733, 1761, 2112, 5019, 5066, 5097, 5149`).
- **`QuestHelper.TryDeleteItems` → `CountQuestItems` counts only items with `QuestItem == true`.**
  This is why auto-collect sets the flag rather than just incrementing `CurProgress`. If
  upstream ever changes turn-in to count unflagged items, auto-collect should stop setting the
  flag, and `QuestItemSafety` becomes advisory rather than protective.
- `BaseQuest.Objectives` is a mutable `public List<BaseObjective>`, and
  `BaseObjective.CurProgress` is publicly settable and fires `OnCompleted`.
- `MondainQuester.Quests` (`public abstract Type[]`) is the quest-to-NPC binding.
- **`QuestHelper.CheckItem(PlayerMobile, Item)` is the only path that turns an item into obtain
  progress**, and it has exactly two upstream callers: `SelectQuestItem.ToggleQuestItem_Callback`
  (the manual context entry) and `CraftItem.cs:1871`. `Custom/Quests/AutoCollectSystem` is a third
  caller. If its signature or its flag-then-count behaviour changes, auto-collect silently stops
  crediting anything.
- **`ObtainObjective.Update` is a TOGGLE, not an increment** (`QuestObjectives.cs:416`): on an
  unflagged item it adds progress and flags, on a flagged one it subtracts and un-flags.
  Auto-collect therefore must never pass an already-flagged item to `CheckItem`. If this ever
  becomes idempotent, the `!item.QuestItem` guard becomes redundant rather than wrong.
- **Once per character is `BaseQuest.DoneOnce` plus a `QuestRestartInfo` in
  `PlayerMobile.DoneQuests`**, written by `BaseQuest.RemoveQuest` and read by
  `QuestHelper.Delayed`. Two consequences custom code depends on: the record lives in the
  **PlayerMobile save**, not in `MLQuests.bin`, and `RemoveQuest` **skips it entirely when
  `Owner.AccessLevel != AccessLevel.Player`** — so staff characters never accumulate one and
  `[ResetQuest` on one is a no-op.
- **`QuestHelper.CanOffer` refuses any quest sharing an objective `Type()` with an already-active
  quest** (`QuestHelper.cs:105-118`). Stock `Norton` offers `DeliciousFishesQuest`, an identical
  `ObtainObjective(typeof(Fish), "fish", 5)`, and is spawned in `Spawns/trammel.xml`, so a player
  can hold Marta's errand or Norton's, never both. `OldMarta.OnTalk` detects that specific clash
  and explains it; if the rule changes upstream, that explanation becomes dead code.
- **`MondainQuestData.GetQuests` INSERTS an empty list for any player it is asked about**
  (`Helpers/Persistence.cs:17`), and `OnSave` writes every entry it holds. `PlayerMobile.Quests`
  is that getter, so `AutoCollectSystem` and `OldMarta` read
  `MondainQuestData.QuestData.TryGetValue` instead — a 1 Hz sweep through the property would grow
  and then persist one junk row per online player.
- **`MondainQuester` suppresses vendor behaviour through `IsActiveVendor => false` alone.** That
  one override silences `VendorBuy`/`VendorSell` and removes the whole Buy/Sell/BOD/Bribe/Claim
  block from `BaseVendor.AddCustomContextEntries`. It does **not** cover `CanTeach`, which
  `MondainQuester` inherits as `true`; `OldMarta` overrides it to false.
- **`BaseCreature.HandlesOnSpeech` ands the AI's answer with
  `from.InRange(this, RangePerception)`**, and `BaseVendor`'s constructor passes perception 2.
  Any quester that must hear speech further away has to override `HandlesOnSpeech` itself.
- **`BaseQuest.GiveRewards` mishandles a non-stackable reward with `Amount > 1`**
  (`BaseQuest.cs:428`): the same instance is placed in the backpack N times, yielding one item and
  N messages. Only stackables honour `Amount`. Use one `BaseReward` per item.

### Commands

- **`[Aliases(...)]` is documentation only.** `HelpInfo` and `Docs` read it, but
  `CommandSystem.Handle` dispatches on registered names alone, so every alias needs its own
  `CommandSystem.Register` call. **Known gap:** `[ReloadRestrictedZones`
  (`Scripts/Custom/Zones/RestrictedZoneCommands.cs`) is declared as an alias but never registered,
  so it does not work — `[RestrictedZonesReload` does.

### Spawners

- **`[XmlLoad` / `[XmlUnLoad` take an optional `SpawnerPrefixFilter` second argument**, matched
  with an ordinal `Name.StartsWith` (`XmlSpawner2.cs:6178`, `:4776`). The whole `GG_` naming
  convention and `[GG_Reimport` rest on it.
- **`XmlSpawner.XmlLoadFromFile` is `public static`** (`XmlSpawner2.cs:6068`), which is what lets
  `[GG_Reimport` import without re-entering the command parser.
- **Deleting an `XmlSpawner` removes its spawned mobiles** (`OnDelete` → `RemoveSpawnObjects()`,
  `XmlSpawner2.cs:2167`). `[GG_Reimport` relies on this for its pre-sweep.
- **There is no `<Z>` element in the spawn XML** — `CentreZ` is the spawner's own Z
  (`XmlSpawner2.cs:6683`), and `<Range>` is `HomeRange`.

#### Upstream data defect: 47 Khaldun spawners exist twice (found in 5c, NOT yet fixed)

`Spawns/trammel.xml` contains 47 `<Points>` blocks for Khaldun spawners **on Felucca**, with their
`<UniqueId>` **commented out** — `<!--guid-->` sitting exactly where the element belongs. The same
47 `(name, map, tile)` are in `Spawns/felucca.xml` with `<UniqueId>` intact, and **the commented
GUIDs are byte-identical to felucca.xml's**.

`[XmlLoad` replaces by `<UniqueId>`, and a row without one takes a freshly generated GUID
(`XmlSpawner2.cs:6182-6186`) — so those rows are **added** on every load rather than replaced. Each
file contributes one copy: the world holds 94 where 47 spawn points exist, confirmed by counting
every world spawner by name and tile (`Data/Live/spawners.json`).

Whoever commented them out was stopping two files fighting over the same records by GUID. The
effect is the opposite.

**Do not "fix" this by restoring the GUIDs.** That would make `trammel.xml`'s rows replace
`felucca.xml`'s — the right count, but two files permanently claiming the same records with
last-load-wins. The fix is to **delete the 47 rows from `trammel.xml`**, which has no business
holding Felucca content. That is an upstream-file edit and needs its own entry above when it is
made — and it is more than a file edit, because the world already holds the 47 extra spawners and
removing those is a separate decision.

Sixteen further blocks in the same file carry `<MinDelay>` twice and no `<MaxDelay>`, which
`DataSet` schema inference may well turn into every trammel spawner loading with default delays.
Unverified; `spawnxml.js` reports both defects and neither is acted on.

### Spawners

- `[XmlLoad` recurses directories and treats `<UniqueId>` as identity, so re-importing
  `Spawns/Custom` replaces rather than duplicates.
- **To verify before the daily-life port:** whether ServUO's spawner `Defrag` / `OnDefragSpawn`
  applies a distance check. On ModernUO it did not, which is what made it safe to walk a
  spawner-owned vendor home at dusk without the spawner deciding it was missing and spawning a
  duplicate. If ServUO *does* check distance, the shop schedule needs a different approach.
- **Also to verify:** that `BaseVendor`'s `FightMode.None` exempts it from `BaseCreature`'s
  return-to-home path, as it did on ModernUO.

### Timers

- **`Timer` `count == 0` means repeat forever** (`Server/Timer.cs:361`). Both the two-argument
  constructor and the three-argument `DelayCall` overloads forward `count: 0`.
- **The priority bucket, not `Interval`, governs how often a timer is examined**
  (`Server/Timer.cs:136`: `{0, 10, 25, 50, 250, 1000, 5000, 60000}` ms). An `Interval` shorter
  than its derived bucket's poll delay is silently coarsened, which is why
  `Custom/Core/LoopQueue` sets `Priority = TimerPriority.EveryTick` explicitly rather than
  relying on the default derived from the interval.

### Saving rotates the whole Saves/ directory

`Scripts/Misc/AutoSave.cs` `Backup()` **moves the entire `Saves/` directory** into
`Backups/Automatic/Most Recent` before each save, shifting the previous ones down through
`Second Backup` and `Third Backup`.

This is why a degraded `CustomPersistence` store refusing to save is not enough on its own:
the file it is protecting is moved out of `Saves/` by the very next save, is gone after three
rotations, and the boot after that finds no file, loads clean, and would quietly persist empty
state. `CustomPersistence` therefore **quarantines** an unreadable save to `Backups/Degraded/`
(which AutoSave never touches) at the moment the load fails.

Known limitation: the degraded flag itself does not survive a restart. The quarantined copy
and the red console log are the durable evidence.

### Regions

- **`Region.Register()` / `Unregister()` re-resolve every mobile in the affected sectors**
  (`Register()` -> `Sector.OnEnter` -> `UpdateMobileRegions()` -> `Mobile.UpdateRegion()` ->
  `Region.OnRegionChange`). This is what makes a newly drawn or resized restricted zone catch
  players already standing in it without a manual sweep. If upstream changes it, zone creation
  silently stops applying to current occupants.
- **`Region.OnEnter` / `OnExit` are dispatched per-region up the ancestor chain** by
  `Region.OnRegionChange` (`Server/Region.cs:1129`) and must NOT chain to the parent themselves.
  **`OnResurrect`, `OnDeath`, `OnBeforeDeath` and `OnLocationChanged` are the opposite** - they
  fire only on the innermost region and manually bubble via `m_Parent`, so an override that does
  not call `base` silently disables the parent region's rules.
- **`BaseRegion.OnEnter` is not empty.** It sends a `YoungDungeonWarning` gump to Young players
  when `!YoungProtected` (`Scripts/Regions/BaseRegion.cs:269`). Any `BaseRegion` subclass that is
  not a dungeon should override `YoungProtected => true`.
- **A null region name is required for dynamic regions.** `Map.RegisterRegion` skips nulls
  silently, but a *duplicate* name warns and `UnregisterRegion` removes by name - so
  unregistering the second of two same-named regions evicts the first one's entry.

### Gumps

- **`NetState.AddGump` disconnects the client at 512 gumps** (`Server/Network/NetState.cs:409`
  calls `Dispose()`). Any gump re-sent on a timer MUST `CloseGump(typeof(X))` first. At 1 Hz the
  cap is reached in under nine minutes.
- Flags are `Closable` / `Disposable` / `Dragable` (one 'g') / `Resizable`, all defaulting true.
  `Closable = false` blocks right-click; **`Disposable = false` is what blocks Escape.**

### Jail and pets

- **`Jail.AllowAutoClaim` returns false** (`Scripts/Regions/Jail.cs:13`), and
  `PlayerMobile.ClaimAutoStabledPets()` short-circuits on it — which is what withholds a
  prisoner's stabled pets for the whole sentence and returns them on the first login after
  release. `Custom/Jail` relies on this rather than tracking pets itself.
- **`BaseCreature.CanAutoStable` returns false for anything `Summoned` and for a mount whose
  `Rider != null`** (`Scripts/Mobiles/Normal/BaseCreature.cs:1065`). So `AutoStablePets()` must
  be preceded by a dismount and an explicit summon dismissal, or the mount and every summon are
  silently left in the world.
- **`Mobile.Frozen` blocks movement only** — three call sites, all `Move()`/packet flags. It is
  **not persisted** and `Kill()` clears it (`Server/Mobile.cs:4045`). Never use it to represent a
  sentence; only for short in-flight sequences.
- **`Mobile.Mount` is read-only.** Use `BaseMount.Dismount(mobile)`, which also handles Animal
  Form and gargoyle Flying.
- **The ten integration points that make a sentence meaningful** are listed in CLAUDE.md section
  13. We add no enforcement of our own, so removing any of them upstream silently gives prisoners
  that ability back.

### Login ordering

`EventSink.Login` fires *after* `Mobile.Map`/`Location` are restored (the restore is in the
`NetState` setter, `Server/Mobile.cs:9112`, and the event is raised at
`Server/Network/PacketHandlers.cs:2580`), so `Map`, `Location`, `Region` and `NetState` are all
valid and final in a handler. **`EventSink.Connected` fires before the restore** — do not use it
for anything location-dependent.

### CommandLogging

`CommandLogging.WriteLine(Mobile from, ...)` dereferences `from.NetState`, `from.Account` and
`from.AccessLevel`, so it **throws on a null actor**. Automatic actions (a zone expiry jailing
someone) legitimately have no actor and must guard before calling it.

### Fatal event handlers

Exceptions escaping these handlers **terminate the shard**, so custom handlers must swallow
their own:

- `EventSink.WorldSave` - rethrown by `World.cs:1168-1174` as
  `"FATAL: Exception in EventSink.WorldSave"`.
- `EventSink.BeforeWorldSave` - rethrown by `World.cs:1149-1156`.
- `EventSink.WorldLoad` - `EventSink.InvokeWorldLoad()` is uncaught inside `World.Load()`,
  which is itself outside any try in `Main.cs:547`; it reaches
  `AppDomain.UnhandledException` and kills the process.

This is the whole reason `CustomPersistence` degrades rather than throwing.

### Maps

- **`Map.Parse` throws `ArgumentException` on an unrecognised name.** The null-returning
  variant at `Server/Map.cs:446` is behind `#if Map_InternalProtection || Map_AllUpdates`,
  neither of which is defined; the compiled `#else` branch at `:493` throws. Custom code uses
  `JsonConfig.TryParseMap` instead.
- **Maps are registered in `Scripts/Misc/MapDefinitions.cs` `Configure()`, which is untagged
  and therefore `[CallPriority]` 0.** Any custom `Configure()` that resolves map names must
  carry a higher priority or the maps do not exist yet.
- `Map.AllMaps` includes `Map.Internal`; filter it out of anything player-facing.

### Time and light

- `Clock.GetTime(map, x, y, out hours, out minutes)` (`Scripts/Items/Tools/Clocks.cs:86`) and
  its per-facet / longitude skew. The daily-life schedule samples at the town anchor precisely
  because the hour is not uniform.
- `LightCycle.ComputeLevelFor`'s boundaries (`<4`, `<6`, `<22`, else). The `DayPhase` mapping
  must stay identical or the schedule disagrees with the sky players actually see.
- `LightCycle.LevelOverride` (`Scripts/Misc/LightCycle.cs:20`), used by the staff phase override.

---

## Carried-over findings from the ModernUO shard

Conclusions that cost real debugging time there and still apply here.

- **XmlSpawner was evaluated and rejected on ModernUO.** Not applicable in the same way here —
  ServUO ships XmlSpawner2 as the stock world spawner, and this shard uses it. The one idea
  worth keeping either way is `TODStart` / `TODEnd` / `TODMode` for time-of-day spawning.
- **Never auto-set a quest item flag without a safety gate.** The gate rejects anything not
  plain — named, hued, insured, player-crafted, non-standard resource, or carrying properties —
  because collect objectives consume with `Item.Delete()`, which bypasses insurance, and
  "collect 5 Bow" shares a CLR type with a runic bow.
- **A save that fails must never scroll past in a toast.** The editor shows a persistent banner;
  the original bug was edits vanishing because nobody saw the failure.
- **Config binding failures are silent.** On ModernUO a case mismatch made a whole feature inert
  with an empty Britain as the only symptom. Hence: explicit property names on every member, and
  validation that collects every problem rather than stopping at the first.
