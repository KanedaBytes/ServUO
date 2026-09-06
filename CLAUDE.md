# CLAUDE.md — ServUO `pub57` engine guide

Engine-level conventions for this codebase. Shard-specific facts (port, era, facet, custom
systems) are in `SHARD.md`. The upstream-edit log is `Scripts/Custom/MODIFICATIONS.md`.

All file:line references below were verified against this tree (assembly 57.4).

---

## 1. Target framework — the constraint behind everything

All three projects (`Server`, `Scripts`, `Ultima`) target **`net48`** (.NET Framework 4.8),
**x64 only**, `AllowUnsafeBlocks=True`, with `OutputPath=..\` so artifacts land at repo root.
`ServUO.exe.config` pins `.NETFramework,Version=v4.8`.

Consequences, all of which shape custom code:

- **No source-generated serialization.** Serialization is hand-written and versioned.
- **No built-in `System.Text.Json`.** JSON requires a package; this shard uses Newtonsoft.Json.
- **Linux means Mono**, not .NET Core. Keep custom code free of Windows-only APIs.

## 2. Build and run

| Action | Command |
| --- | --- |
| Windows | `.\build.ps1` (`-NoRun`, `-Debug`) — build, **abort on failure**, then run |
| Linux/Mono | `./build.sh` (`--no-run`, `--debug`) — same |
| *(stock, unused)* | `_winrelease.bat` / `_windebug.bat` / `make` — neither checks the build result |

**Scripts are precompiled into `Scripts.dll`; the core does not compile them at boot.**
`Scripts/Scripts.csproj` is an SDK-style project using default globbing, so **any `.cs` file
anywhere under `Scripts/` is compiled automatically** — adding custom scripts needs no
project-file edit. There is no `Scripts/Output/*.dll` cache (that `.gitignore` entry is a
RunUO leftover).

**This shard sets `Config/Compiler.cfg` to `Dynamic=False`.** Upstream ships `Dynamic=True`,
which makes `Server/ScriptCompiler.cs` shell out to `dotnet build Scripts/Scripts.csproj` on
every boot and then `Assembly.LoadFrom("Scripts.dll")`. We turn that off and build through
`build.ps1` / `build.sh` instead, because:

> ### How script compile errors surface
>
> **`ScriptCompiler.Compile()` never checks the `dotnet build` exit code.** It returns `true`
> regardless and loads whatever `Scripts.dll` already exists. A script that fails to compile
> therefore boots the **previous, stale DLL**, and the only evidence is raw MSBuild output
> scrolling past in the console. The `while (!ScriptCompiler.Compile(...))` retry loop and the
> "One or more scripts failed to compile" message only fire if `Assembly.LoadFrom` itself
> throws.
>
> **Verified by reproduction in this tree.** With a deliberate syntax error in one script,
> `dotnet build` exited 1 and left `Scripts.dll` byte-identical — and the server then booted
> straight past `Build FAILED. 3 Error(s)`, verified 6017 item and 1361 mobile types from the
> stale DLL, loaded 161,438 items and 15,379 mobiles, and bound port 2594. A completely
> working server, with your change silently absent.
>
> `Dynamic=False` plus the exit-code-checking build scripts closes this hole: the only path
> that produces a `Scripts.dll` is one that refuses to launch on a failed build.

**The server must be stopped to rebuild** — `Scripts.dll` is locked while loaded and there is
no hot-reload.

## 3. Boot sequence (`Server/Main.cs`)

```
Config.Load()
  -> ScriptCompiler.Compile()        // loads Scripts.dll
  -> Core.VerifySerialization()
  -> ScriptCompiler.Invoke("Configure")
  -> Region.Load()                   // parses Data/Regions.xml
  -> World.Load()                    // reads Saves/**, fires EventSink.WorldLoad at the end
  -> ScriptCompiler.Invoke("Initialize")
  -> MessagePump.Start(), Timer thread start, NetState.Initialize()
  -> EventSink.InvokeServerStarted()
  -> main loop
```

The working directory is set to `Core.BaseDirectory` early, which is why every `Data/`,
`Saves/` and `Config/` path in the codebase is relative.

**`Configure()` / `Initialize()` convention.** `ScriptCompiler.Invoke` reflects over every type
in every loaded assembly and calls every `public static void` parameterless method of that name.

- **`public static void Configure()`** — runs **before** `World.Load`. Register
  `EventSink.WorldSave` / `WorldLoad` handlers, read `Config.Get`, set static switches.
- **`public static void Initialize()`** — runs **after** `World.Load`. Register commands, start
  timers, generate or patch world content.

**`[CallPriority(n)]`** orders these; **lower runs first**, and untagged is **0**.
Examples in-tree: `Scripts/Misc/CurrentExpansion.cs` uses `Int32.MinValue`;
`Scripts/Services/Help/HelpPersistence.cs` uses 900.

> **Porting note:** ModernUO's `[CallPriority]` default is **50**, ServUO's is **0** (both
> lower-first). A ModernUO `[CallPriority(100)]` meaning "run last" still means that here, but
> `[CallPriority(10)]` meaning "run early" is now *later* than untagged code. **Re-derive every
> priority when porting** rather than copying the number across.

`Core.VerifySerialization()` warns in yellow (never fatally) for any `Item`/`Mobile` subclass
missing a `(Serial)` ctor or a declared `Serialize`/`Deserialize`.

## 4. Threading model

Threads: **Core Thread** (the game thread), **Timer Thread** (decides which timers are due and
enqueues them — it does *not* run callbacks), the socket accept thread + IOCP callbacks, and
per-save worker threads.

The main loop is **event-driven, not fixed-tick** — it blocks on an `AutoResetEvent` until
someone calls `Core.Set()`:

```csharp
while (!Closing)
{
    _Signal.WaitOne();
    Mobile.ProcessDeltaQueue();
    Item.ProcessDeltaQueue();
    Timer.Slice();          // timer callbacks execute HERE, on the game thread
    MessagePump.Slice();    // packet handlers run HERE, on the game thread
    NetState.FlushAll();
    NetState.ProcessDisposedQueue();
    if (Slice != null) Slice();   // per-tick hook; nothing in the tree subscribes to it
}
```

### What is safe from another thread

**Safe:** `Core.Set()` (a plain `AutoResetEvent.Set()`), and `ConcurrentQueue.Enqueue` onto
your own queue.

**Never safe** — must happen inside a game-thread callback: `World.Items` / `World.Mobiles`
(plain `Dictionary`), any `Item`/`Mobile` state, `Map` / `Sector` collections, the delta
queues, `Region`, gumps, `Config` (plain `Dictionary`), and `ScriptCompiler`'s type caches.

`Timer.DelayCall` is *nominally* thread-safe — mutations go through `lock (m_Changed)` — **but
custom code must not rely on that.** Under `-profile`, `TimerProfile.Acquire` reads and writes
an unsynchronized `Dictionary`, and `Timer.Slice()` holds `lock (m_Queue)` across the entire
callback batch, so an off-thread `DelayCall` can block behind tick execution.

### Marshalling onto the game loop — the house rule

There is **no `Core.LoopContext`, no `Core.Invoke`, and no general-purpose work queue drained
per tick.** This is the largest gap versus ModernUO's `LoopContext.Post`, and it is filled by
`Scripts/Custom/Core/LoopQueue.cs`.

> **Rule: a background thread enqueues work onto a `ConcurrentQueue`; a repeating `Timer` on
> the game thread drains it. Background threads never call `Timer.DelayCall` and never touch
> world state directly.**

```csharp
// Any background thread (e.g. the admin API listener):
LoopQueue.Post(() => { /* freely touches world state */ });

// Request/response across the boundary:
LoopQueue.TryPostAndWait(() => World.Mobiles.Count, TimeSpan.FromSeconds(10), out count, out error);
```

Implemented in `Scripts/Custom/Core/LoopQueue.cs`: a `ConcurrentQueue<Action>` drained by one
repeating `Timer` at `TimerPriority.EveryTick`, capped at `Custom.LoopQueueBudget` items per
tick, skipping while `World.Loading || World.Saving`, and force-drained on `BeforeWorldSave` so
posted mutations land in the save. See `Scripts/Custom/Core/README.md`.

For **request/response** across the boundary, use a `TaskCompletionSource` created with
`TaskCreationOptions.RunContinuationsAsynchronously`, plus a timeout. Do **not** use a
`ManualResetEventSlim`: if a request times out and the loop later runs the posted work anyway,
setting a result nobody awaits is harmless, whereas signalling a *disposed* reset event would
throw **on the game thread** from a request that had already given up.

`Core.Slice` (the `public static Slice Slice;` delegate) is an unused per-tick hook and is an
alternative drain point, but a plain repeating `Timer` is more conventional and composable.

### Saving freezes the world

`World.Save` runs **synchronously on the core thread**: `NetState.Pause()` →
`EventSink.BeforeWorldSave` → save strategy → `EventSink.WorldSave` → `AfterWorldSave` →
`NetState.Resume()`. The Timer Thread also idles while `World.Saving`. Only the *disk writes*
can be backgrounded.

Therefore: custom `WorldSave` handlers must be fast and must never block, and any mutating
external request (the admin API) must be refused while `World.Saving || World.Loading`.

## 5. Serialization

Hand-written and versioned. `Server/Serialization.cs` defines `GenericReader` / `GenericWriter`
with entity-aware helpers (`ReadItem<T>()`, `ReadMobile()`, `ReadStrongItemList<T>()`,
`Point3D`, `TimeSpan`, and so on).

The pattern is: write an int version, then a `switch` with `goto case` fallthrough
(`Server/Item.cs:2617`, `Server/Mobile.cs:6372`). Subclasses call `base.Serialize(writer)`
first, then write their own version int.

```csharp
public class Thing : Item
{
    [Constructable] public Thing() : base(0x1234) { }
    public Thing(Serial serial) : base(serial) { }   // REQUIRED or the item silently fails to load

    public override void Serialize(GenericWriter writer)
    {
        base.Serialize(writer);
        writer.Write(0); // version
    }

    public override void Deserialize(GenericReader reader)
    {
        base.Deserialize(reader);
        int version = reader.ReadInt();
    }
}
```

**A missing `(Serial)` ctor means the entity silently fails to load** — `World.Load` catches
the constructor exception and skips the entity. `Core.VerifySerialization` only warns.

File examples: `Scripts/Items/Decorative/Blood.cs` (minimal),
`Scripts/Items/Functional/CoralTheOwl.cs` (versioned, with `[CommandProperty]`).

**No `[AfterDeserialization]` equivalent.** For an NPC that should delete itself on world load
(the ephemeral-NPC pattern), put `Timer.DelayCall(Delete)` at the tail of `Deserialize`.

## 6. Persistence for non-entity data

`Server/Persistence/Persistence.cs` is the ServUO analogue of ModernUO's `GenericPersistence`
and the idiom for system-level singletons (~28 files use it). Unlike `GenericPersistence` it
does **not** self-register — wire it to the world events yourself in `Configure()`.

Canonical example, `Scripts/Services/DisguisePersistence.cs`:

```csharp
private static string FilePath = Path.Combine("Saves", "Disguises", "Persistence.bin");

public static void Configure()
{
    EventSink.WorldSave += OnSave;
    EventSink.WorldLoad += OnLoad;
}

private static void OnSave(WorldSaveEventArgs e) =>
    Persistence.Serialize(FilePath, writer => { writer.Write(0); /* version, then payload */ });

private static void OnLoad() =>
    Persistence.Deserialize(FilePath, reader => { var version = reader.ReadInt(); /* switch */ });
```

Also worth reading: `Scripts/Services/PointsSystems/PointsSystem.cs` (an abstract base with a
registry), and `Scripts/Misc/SpawnerPersistence.cs` (also the shard-version migration engine).

**Do not use `Server/Customs Framework/`** (`CustomSerial`, `SaveData`, `BaseCore`, persisted
to `Saves/Customs/`). It is a parallel, less-travelled entity system; custom shard code uses
`Persistence` + `EventSink` for consistency with the rest of the tree.

`ItemSocket` (`Server/Item.cs:6334`) attaches versioned, optionally self-expiring serializable
data to any `Item` without subclassing — useful where ModernUO code used a component.

## 7. Config (`.cfg`) and `Data/`

`Server/Config.cs`. `Config/**/*.cfg` is scanned recursively at boot, so **adding a config file
needs no registration** — drop it in and read it.

Format: `#` comment lines describe the option, a blank line terminates, then `Key=Value`.
`@Key=Value` forces the built-in default.

**Scope = folder path + filename.** `Config/Server.cfg` → `Config.Get("Server.Port", 2593)`;
`Config/Foo/Bar.cfg` → scope `Foo.Bar`.

```csharp
public static string ServerName { get; } = Config.Get("Server.Name", "My Shard");
public static int Port => Config.Get("Server.Port", 2593);
public static readonly Expansion Expansion = Config.GetEnum("Expansion.CurrentExpansion", Expansion.EJ);
```

Typed getters: `Get<T>`, `GetEnum<T>`, `GetArray<T>`, `GetDelegate<T>`, plus overloads for
`TimeSpan`, `DateTime`, `IPAddress` and `Version`. Mirrored `Set<T>`.

**Debug override:** when `Core.Debug`, `Config/_DEBUG.cfg` is consulted first, with
fully-qualified keys (`Server.Name=Test Centre`). Local-only, never committed.

**`Data/`** is read with paths relative to `Core.BaseDirectory`, and is **distinct** from the
client MUL/UOP directory (`Core.DataDirectories`, set from `Config/DataPath.cfg` by
`Scripts/Misc/DataPath.cs`). Formats in use: XML (`Regions.xml`, `SpawnDefinitions.xml`),
ad-hoc `.cfg` records (**a different format from `Config/*.cfg`** — tab and paren delimited),
`.csv`, and binary. **There is no JSON anywhere in the stock tree.**

## 8. Regions

Core: `Server/Region.cs`. Key virtuals — `OnEnter`(747), `OnExit`(750), `OnMoveInto`(742),
`OnLocationChanged`(805), `AllowHarmful`(889), `AllowBeneficial`(916), `OnSpeech`(955),
`OnSkillUse`(963), `OnBeginSpellCast`(978), `OnResurrect`(996), `AllowSpawn`.
`Register()`(276) / `Unregister()`(330).

**XML-defined** (`Data/Regions.xml`, loaded by `Region.Load()`:1167): any script class named in
`type=` is instantiated **by reflection** and must expose a
`(XmlElement xml, Map map, Region parent)` constructor. Supports nested `<region>` children,
`priority=`, and `expansion=` filtering.

```xml
<region type="Jail" priority="50" name="Jail">
  <rect x="5271" y="1159" width="41" height="33" />
  <go x="5275" y="1163" z="0" />
</region>
```

**Code-registered**: construct with `(name, map, priority|parent, params Rectangle2D[])` then
call `.Register()`. Examples: `Scripts/Quests/The Ritual/Puzzle.cs:346` (a one-liner),
`Scripts/Services/Doom/GaryRoom.cs:83` (uses `OnRegister`/`OnUnregister` to start and stop a
timer). Script base class: `Scripts/Regions/BaseRegion.cs`.

`Register()` re-resolves mobiles already standing in the affected sectors, so a newly created
or resized region applies to current occupants without a manual sweep.

## 9. Gumps

Core: `Server/Gumps/Gump.cs` (`AddButton`:166, `OnResponse(NetState, RelayInfo)`:487); sent
with `Mobile.SendGump`. Plain example: `Scripts/Gumps/ResurrectGump.cs`.

**Prefer `Scripts/Gumps/BaseGump.cs` for custom gumps.** `abstract BaseGump : Gump,
IDisposable` provides `static SendGump(BaseGump)` which **reuses an already-open instance and
`Refresh()`es it** rather than stacking a second copy, plus `abstract AddGumpLayout()`,
`Refresh(bool recompile, bool close)`, `GetGump<T>(pm, predicate)`, and a sealed `OnResponse`
forwarding to `virtual OnResponse(RelayInfo)`. This is the closest analogue to ModernUO's
`Singleton`.

Live-updating patterns already in the tree:

- countdown that forces close and acts on expiry — `Scripts/Gumps/StormLevelGump.cs:235`
- system-wide tick refreshing every viewer's gump —
  `Scripts/Services/Expansions/Time Of Legends/AuctionSafe/Auction.cs:23` + `Gumps.cs:40`
- refresh-only-if-open — `.../Cannons and Ammo/ShipCannon.cs:1065`

**There is no `StaticGump`, no cached layout, and no string placeholders.** A ModernUO
`StaticGump<T>` with `AddHtmlPlaceholder`/`SetHtmlText` becomes a plain `Gump`/`BaseGump`
rebuilt on each send with `AddHtml`. Emulate `Singleton` with `CloseGump(typeof(X))` before
`SendGump`, or let `BaseGump.SendGump` handle the reuse.

**A gump sent once per second will hit the client's 512-gump cap in under nine minutes** unless
the previous instance is closed or reused. This is not optional for countdown gumps.

## 10. Commands and targeting

`Server/Commands.cs` — `CommandSystem.Register(name, AccessLevel, CommandEventHandler)`, prefix
`[`. **Always register from `Initialize()`.** `[Usage]` / `[Description]` / `[Aliases]`
(`Server/Attributes.cs`) feed `Scripts/Commands/HelpInfo.cs`.

```csharp
public static void Initialize()
{
    CommandSystem.Register("MyCommand", AccessLevel.GameMaster, MyCommand_OnCommand);
}

[Usage("MyCommand <arg>")]
[Description("What it does.")]
private static void MyCommand_OnCommand(CommandEventArgs e) { }
```

Canonical file: `Scripts/Commands/Handlers.cs`. For `[global` / `[area` / `[region`-style
commands, subclass `BaseCommand` (`Scripts/Commands/Generic/Commands/BaseCommand.cs`) and
register it with `TargetCommands.Register`.

**Targeting:** `Server/Targeting/Target.cs` — subclass and override `OnTarget`
(example: `Scripts/Targets/MoveTarget.cs`), or use the callback form
`Mobile.BeginTarget(range, allowGround, TargetFlags, callback)` (`Server/Mobile.cs:2653`).

Useful helpers: `Scripts/Commands/BoundingBoxPicker.cs` for area selection (it takes a
`BoundingBoxCallback` **plus a state object**, not a closure), and
`Scripts/Gumps/WarningGump.cs` `(TextDefinition header, int headerColor, TextDefinition
content, int contentColor, int width, int height, WarningGumpCallback callback, object state)`.

## 11. Quest engines — three coexist

**There is no `MLQuestSystem` in ServUO.** Write that down; it is the most common incorrect
assumption carried over from RunUO and ModernUO.

### (A) Mondain's Legacy engine — use this one

Engine in `Scripts/Services/MondainsLegacyQuests/`, quest content in `Scripts/Quests/`.

- `Scripts/Quests/BaseQuest.cs` — `Objectives` (a mutable `public List<BaseObjective>`),
  `Rewards`, `Owner`, `Quester`, `ChainID`, and the text properties `Title` / `Description` /
  `Refuse` / `Uncomplete` / `Complete` (each an `object`, so either a cliloc int or a plain
  string). It runs a **1-second per-quest timer** driving `public virtual Slice()`.
- Objectives (`QuestObjectives.cs`): `BaseObjective` plus `SlayObjective`, `ObtainObjective`,
  `DeliverObjective`, `EscortObjective`, `ApprenticeObjective`, `QuestionAndAnswerObjective`.
  `BaseObjective.CurProgress` is publicly settable and fires `OnCompleted`/`OnFailed`.
- **Quest-to-NPC binding is a property on the quest giver, not a central registry:**

```csharp
public class MyNpc : MondainQuester   // MondainQuester : BaseVendor
{
    public override Type[] Quests => new[] { typeof(MyQuest) };
}
```

  ~150 NPC files do this. Offer flow: `MondainQuester.OnTalk` → `QuestHelper.RandomQuest` →
  `player.SendGump(new MondainQuestGump(quest))`, triggered from `OnDoubleClick` and from
  proximity in `OnMovement`.
- Player storage: `PlayerMobile.Quests => MondainQuestData.GetQuests(this)`.

### (B) Legacy AOS-era engine — do not build on it

`Scripts/Services/Quests/QuestSystem.cs`, with a hardcoded `QuestTypes` array and
`BaseQuester : BaseVendor`.

### (C) XmlSpawner's quest layer — do not build on it

`Scripts/Services/XmlSpawner/XmlSpawner Core/XmlQuest/`.

### The `QuestItem` flag is the ledger, not decoration

`ObtainObjective.Update` sets `item.QuestItem = true`, and turn-in
(`QuestHelper.TryDeleteItems` → `CountQuestItems`) counts **only flagged items**. So
incrementing `CurProgress` without setting the flag yields an objective that reads complete but
cannot be handed in.

The flag is expensive: `Item.Nontransferable => QuestItem` (`Server/Item.cs:3878`), enforced at
drop, trade and stack (`Item.cs:1733, 1761, 2112, 5019, 5066, 5097, 5149`), plus a forced hue
of `0x04EA`. Flagging an item makes it undroppable, untradeable and visibly recoloured — so
never flag anything that has not passed a safety gate (see
`Scripts/Custom/Quests/QuestItemSafety.cs`).

**There is no stock `[ResetQuest`.** The nearest stock command is `[Quests`
(`Scripts/Services/Expansions/MondainsLegacy.cs:201`, `AccessLevel.GameMaster`), which targets
a player and opens their `MondainQuestGump`. This shard supplies `[ResetQuest` and
`[ResetAllQuests` from `Scripts/Custom/Commands/ResetQuestCommands.cs` (port step 3).

**Once per character is `BaseQuest.DoneOnce`**, not `OneTimeOnly`. On turn-in,
`BaseQuest.RemoveQuest` appends a `QuestRestartInfo` to `PlayerMobile.DoneQuests` — serialized in
the **PlayerMobile save**, while active quests live separately in `MondainQuestData`
(`Saves/Quests/MLQuests.bin`). It is skipped entirely for `AccessLevel > Player`, so staff
characters can repeat a `DoneOnce` quest forever.

**`PlayerMobile.Quests` is not a field.** The getter is `MondainQuestData.GetQuests(this)`, which
*inserts* an empty list for anyone it is asked about, and the save writes every entry it holds.
Anything sweeping players on a timer must read `MondainQuestData.QuestData.TryGetValue` instead.

ServUO auto-counts quest items in exactly two places — crafting
(`Scripts/Services/Craft/Core/CraftItem.cs:1871` → `QuestHelper.CheckItem`) and quest rewards
(`QuestHelper.CheckRewardItem`, from `BaseQuest.cs:455`). Everything else requires the player to
target each item individually through `ToggleQuestItem_Callback`.

## 12. Spawners and world generation

- Standard spawner: `Scripts/Services/Spawner/Spawner.cs` (`Spawner : Item, ISpawner`).
- **XmlSpawner2** (`Scripts/Services/XmlSpawner/XmlSpawner Core/XmlSpawner2.cs`, ~7000 lines)
  is what actually populates the world.
- Region-driven spawns with no spawner item: `Scripts/Regions/Spawning/`, fed by `<spawning>`
  blocks in `Data/Regions.xml` and groups in `Data/SpawnDefinitions.xml`.

**Spawn data lives outside `Data/`, at repo root**: `Spawns/` (13 facet XML files —
`trammel.xml` alone is ~108k lines and 2,572 points), `RevampedSpawns/`, `SpawnsOld/`.
Custom spawns go in `Spawns/Custom/`.

**The `ImportSpawners` equivalent is `[XmlLoad <file-or-dir>`** (registered at
`XmlSpawner2.cs:3669`; it recurses directories). Companions: `[XmlSave`, `[XmlUnLoad`,
`[XmlSpawnerWipe(All)`, `[XmlSpawnerRespawn(All)`. As in ModernUO, **`<UniqueId>` is the
identity** — a re-import replaces a spawner rather than stacking a second one.

World generation: `Scripts/Commands/CreateWorld.cs` (`[CreateWorld` / `[DeleteWorld`) is the
master table; the spawner row is literally `new CommandEntry("Spawners", "XmlLoad Spawns", …)`.
Decoration: `Scripts/Commands/Decorate.cs`, reading `Data/Decoration/<facet>/*.cfg`.
`Scripts/Misc/SpawnerPersistence.cs` is the shard-version migration engine.

## 13. Jail — what already exists

`Scripts/Regions/Jail.cs` (`Jail : BaseRegion`) blocks beneficial and harmful acts, housing,
spellcasting, skill use and combatant changes for non-staff, and sets `LightCycle.JailLevel`
(9).

Defined in `Data/Regions.xml:699` (Felucca) and `:1770` (Trammel): rect `5271,1159` 41×33,
`go 5275,1163,0`. **One undivided block — there are no cells.**

Ten subsystems already react to it: `AccountHandler.cs:466` (no auto-claim on login),
`SkillCheck.cs:366` (no skill gain), `SpellHelper.cs:874` (no travel in or out),
`HelpGump.cs:209,266` (`[help` stuck option disabled), `BagOfSending`, `BallOfSummoning`,
`BraceletOfBinding`, `HornOfRetreat`, `BaseCreature.cs:6605` (escape items disabled), and
`PreventInaccess.cs:24`.

The administrative layer — `[Jail` / `[Unjail` / `[JailInfo` / `[JailRecord`, sentence records,
escalation, persistence, release timers and the status gump — is **not** stock. It is supplied by
`Scripts/Custom/Jail/` (port step 2); see that folder's README for what is ours versus ServUO's.

Note `Jail.AllowAutoClaim` returning false is load-bearing: it is what keeps a prisoner's stabled
pets stabled for the length of the sentence.

## 14. Missing-equivalent table (ModernUO → ServUO)

| ModernUO | ServUO |
| --- | --- |
| `Core.LoopContext.Post` | **None.** `Scripts/Custom/Core/LoopQueue.cs` |
| `[SerializationGenerator]` / `[SerializableField]` | **None.** Hand-write `Serialize`/`Deserialize` + version int |
| `[AfterDeserialization(false)]` | **None.** `Timer.DelayCall(Delete)` at the tail of `Deserialize` |
| `[GeneratedEvent]` / `[OnEvent]` | **None.** `public static event Action<…>`, or `EventSink.Login` |
| `JsonConfig` (`Server.Json`) | **None.** `Scripts/Custom/Core/JsonConfig.cs` (Newtonsoft) |
| `SpawnerJsonSerializer.SerializeCompact` | **None.** Custom compact writer |
| `GenericPersistence` | `Server/Persistence/Persistence.cs` + `EventSink.WorldSave`/`WorldLoad` |
| `StaticGump<T>` / placeholders / `Singleton` | `Scripts/Gumps/BaseGump.cs`; `CloseGump` before `SendGump` |
| `MLQuestSystem`, `CollectObjective`, `MLQuestContext` | **None.** `BaseQuest` + `MondainQuester.Quests` |
| `ImportSpawnersCommand.ImportFile` | `[XmlLoad` (XmlSpawner2) — XML, not JSON |
| `LogFactory.GetLogger` | **None.** `Utility.WriteConsoleColor`, or `Scripts/Custom/Core/CustomLogger.cs` |
| `Core.Now` | **Does not exist** in this build — use `DateTime.UtcNow` |
| `Core.TickCount` | Exists, but is `long` **milliseconds** (`Stopwatch`-based, monotonic) |
| `Map.TryParse` | **Only `Map.Parse`, and it *throws*** on an unknown name — use `JsonConfig.TryParseMap` |
| `Region.GetRegion<T>()` | **Absent** — only `GetRegion(Type)` and `IsPartOf<T>()` |
| `Gump.Movable` | It is spelled **`Dragable`** (one 'g'); `Disposable=false` is what blocks Escape |
| `Dictionary.Remove(key, out value)` | **Absent on net48** (netstandard2.1 only) — `TryGetValue` then `Remove` |
| `BoundingBoxPicker.Begin(from, lambda)` | `Begin(Mobile, BoundingBoxCallback, object state)` — no lambda overload |
| `BaseCreature.HomeMap` | **None** — `Home` is a `Point3D` only |
| `map.GetMobilesInRange<T>()` | Non-generic `IPooledEnumerable` — type-test it, and dispose it |
| `string.InsensitiveEquals` / `InsensitiveContains` | `Insensitive.Equals` / `Insensitive.Contains` |
| `World.WorldState == WorldState.Running` | Only `World.Saving` / `World.Loading` (weaker) |
| `ServerConfiguration.GetOrUpdateSetting` | `Config.Get` / `Config.Set` |
| `AssemblyHandler.FindTypeByName` | `ScriptCompiler.FindTypeByName` |
| `StaticWarningGump<T>(Action<bool>)` | `WarningGump(…, WarningGumpCallback, object state)` |
| `BoundingBoxPicker.Begin(from, lambda)` | `BoundingBoxPicker.Begin(from, BoundingBoxCallback, state)` |
| `Clock.GetTime`, `LightCycle.LevelOverride` | **Both present** — `Scripts/Items/Tools/Clocks.cs:86`, `Scripts/Misc/LightCycle.cs:20` |
| `CommandSystem.Register`, `[Usage]`, `[CallPriority]` | **Identical** (but see the priority-default note in §3) |
| `HttpListener`, `NetState.Instances`, `Core.TickCount` | **Identical** |

## 15. Custom code conventions

- All custom content lives in **`Scripts/Custom/`**, namespace root **`Server.Custom`**.
- **Never edit upstream files.** If it is genuinely unavoidable, log it in
  `Scripts/Custom/MODIFICATIONS.md` with the diff and why no `Custom/`-side approach worked.
- Custom JSON config in `Data/Custom/`; custom spawn XML in `Spawns/Custom/`; custom `.cfg` in
  `Config/` like any other.
- Keep everything **Mono-safe** — no Windows-only APIs (registry, WinForms, Windows-only path
  assumptions).
- Prefer iterating `NetState.Instances` (online clients) over `World.Mobiles`. If you must walk
  `World.Items` / `World.Mobiles`, justify it in a comment at the call site.
- Tick math must be wraparound-safe: compare by subtraction
  (`Core.TickCount - deadline >= 0`), never `a < b`.
- **Never use `node --check` to validate files under `tools/editor/js/`** — it passes ES modules
  that contain syntax errors, because `--check` parses a `.js` file as CommonJS and its fallback to
  module detection swallows the error. Run the `modules.test.js` suite instead
  (`node --test tools/editor/*.test.js`), which checks each file as a real `.mjs` and imports the
  browser module graph. This shipped a blank editor past 161 green tests once already.
- **Never use PowerShell `Get-Content` / `Set-Content` on source or config files.** They add UTF-8
  BOMs, `-NoNewline` flattens a whole file onto one line, and the round-trip corrupts non-ASCII
  (an em dash becomes `â€"`). Use the editor tools or Python for anything textual; keep PowerShell
  for running processes.
