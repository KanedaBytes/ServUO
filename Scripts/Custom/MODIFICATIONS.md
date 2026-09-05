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

*(No `.cs` file under `Server/` or `Scripts/` has been modified. Each future entry follows the
format above.)*

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

### Spawners

- `[XmlLoad` recurses directories and treats `<UniqueId>` as identity, so re-importing
  `Spawns/Custom` replaces rather than duplicates.
- **To verify before the daily-life port:** whether ServUO's spawner `Defrag` / `OnDefragSpawn`
  applies a distance check. On ModernUO it did not, which is what made it safe to walk a
  spawner-owned vendor home at dusk without the spawner deciding it was missing and spawning a
  duplicate. If ServUO *does* check distance, the shop schedule needs a different approach.
- **Also to verify:** that `BaseVendor`'s `FightMode.None` exempts it from `BaseCreature`'s
  return-to-home path, as it did on ModernUO.

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
