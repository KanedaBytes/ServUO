# Custom/Core

Shims for engine facilities ServUO does not provide. Everything else in `Custom/` depends on
these, so they landed first. See `CLAUDE.md` for the engine constraints behind each one.

> **Namespace trap:** every file here uses `namespace Server.Custom`, **not**
> `Server.Custom.Core`. Inside a `Server.Custom.Core` namespace the identifier `Core` resolves
> to the *namespace*, not `Server.Core`, and every `Core.Debug` / `Core.Set()` / `Core.TickCount`
> reference fails to compile. Never create a namespace segment named `Core` under `Server.Custom`.

## `CustomLogger` — levelled console logging

```csharp
private static readonly CustomLogger Log = CustomLogger.For("MySystem");

Log.Info("Loaded {0} records.", count);
Log.Warn("Config missing; using defaults.");
Log.Error(ex, "Save failed.");
Log.Debug("Only visible with -debug.");
```

Output is `[HH:mm:ss] [LEVEL] [Source] message`, coloured per level. `Debug` is suppressed
unless `Core.Debug`. Nothing here ever throws — a bad format string degrades to the raw
template rather than propagating.

## `LoopQueue` — run work on the game thread

The `Core.LoopContext.Post` equivalent. ServUO has no general-purpose way to marshal work onto
the game loop; this is it.

```csharp
// From any thread. Safe before the world has loaded - work is queued, never dropped.
LoopQueue.Post(() => { /* freely touches world state */ });

// Request/response, for a background thread servicing a request (the admin API).
int count;
string error;
if (LoopQueue.TryPostAndWait(() => World.Mobiles.Count, TimeSpan.FromSeconds(10), out count, out error))
{
    // ...
}
```

- A repeating `Timer` at `TimerPriority.EveryTick` drains the queue on the game thread.
- Capped at `Custom.LoopQueueBudget` items per tick so a burst cannot stall the loop.
- Skips while `World.Loading || World.Saving`; work stays queued.
- Force-drains on `BeforeWorldSave`, so posted mutations land in the save.
- Each callback is individually guarded; a fault is logged and counted, never propagated.
- `TryPostAndWait` called *from* the game thread runs inline rather than deadlocking.
- `AcknowledgeFaults()` clears the health baseline after you have read the errors.
- `Submit(work)` returns a `LoopJob` handle whose `Outcome` says afterwards whether it ran.

**The outcome contract (21 September 2026, REVIEW.md section 2).** A caller may conclude one of
four things about work it handed to the queue, and nothing else:

| Outcome | Meaning | May the caller retry? |
| --- | --- | --- |
| `Completed` | ran and returned; an ack `ok:true` means this and only this | n/a |
| `Faulted` | ran and threw; an ack `ok:false` means it failed, and where | after reading why |
| `NotRun` | **never started**: its waiter timed out and withdrew it while still queued, or the shard shut down first | **yes** - nothing happened |
| `Unknown` | the waiter gave up while it was **running**, the process ended with it running, or no ack ever came | **no** - re-query the state it would have changed |

`TryPostAndWait(..., out LoopOutcome outcome)` reports which; on a timeout it withdraws the job by
compare-and-swap (`LoopJob.TryAbandon`) and says `NotRun` if it won, `Unknown` if the drain had
already begun the job. A save freezes the drain and queued work waits; a waiter expiring inside the
freeze withdraws its job. A shutdown orphans every queued job, wakes their waiters with `NotRun` and
says on the console how many there were; nothing runs after `Core.Closing`. A crash raises no
Shutdown event: waiters time out and withdraw, which is `NotRun` too. **No ack, a timeout, a save,
a shutdown or a crash are never reported as done and never as failed.** Long-running owners that
report through a `Data/Live` file - `nav-adopt`, `walk-audit` - use the same words in their
`status`: `done`, `failed` with the error, `unknown` for a run the shard stopped under
(`Bridge/LiveStatus.cs` marks a stale `working`/`running` file at boot).

**The bridge's file protocol speaks the same words** (22 September 2026, REVIEW.md F3). Every
request is `Data/Live/requests/<name>.<id>.token`; `Bridge/RequestPoller.cs` claims it by renaming
it to `.claimed` before dispatching and acks to `<name>.<id>.ack.json` with the id and an `outcome`.
The bridge withdraws a token still unclaimed at its timeout (`NotRun`, retried once) and reports a
claimed one with no ack as `Unknown`; the poller sweeps everything a previous boot left, never
running it. The editor's data files are committed by the shard (`Bridge/DataFileCommit.cs`) against
the version a save was based on, and a refusal names who last wrote the file and when
(`Bridge/DataFileLedger.cs`, which `JsonConfig.TrySaveToken` notes into). `BridgeFixtures.cs` proves
it from `[CoreSmoke`; `tools/editor/README.md`, *The request channel*, is the written authority.

**Never** call `Timer.DelayCall` or touch world state from a background thread — see CLAUDE.md
section 4 for why `Timer.DelayCall` is not safe enough to rely on.

## `JsonConfig` — JSON configuration

```csharp
MyConfig config;
IList<string> errors;

if (!JsonConfig.TryLoad("Data/Custom/my-config.json", out config, out errors))
{
    foreach (string error in errors) { Log.Error(error); }
    return;   // keep whatever config was already live
}
```

Conventions, each of which encodes a bug that actually shipped on the ModernUO shard:

- **Every member carries an explicit `[JsonProperty("camelCase")]`.**
- **A wrapper object, never a bare array**, so a renamed root key is a loud error.
- **Unknown or misspelled keys are errors**, not silence (`MissingMemberHandling.Error`).
- **Validation collects every problem**, via `IValidatableConfig.Validate(ConfigErrors)`. The
  live config is replaced only on complete success.
- **`JsonConfig.TryParseMap`, never `Map.Parse`** — the compiled `Map.Parse` overload *throws*
  `ArgumentException` on an unknown facet name. Config resolving map names must load at
  `[CallPriority(100)]` or later, because maps are registered at priority 0.
- Files are written **one object per line**, so an editor save is a minimal diff rather than a
  whole-file rewrite. `TryLoadToken` / `TrySaveToken` patch the raw `JToken` tree so keys this
  shard does not model survive a round-trip.

## `CustomPersistence` — persisted system state

```csharp
public sealed class MyStore : CustomPersistence
{
    public MyStore() : base("MyStore", 0) { }

    protected override void SerializeCore(GenericWriter w) { w.Write(_value); }
    protected override void DeserializeCore(GenericReader r, int version)
    {
        switch (version) { case 0: _value = r.ReadInt(); break; }
    }
}
```

Construct the instance from a `Configure()` somewhere; the base handles all event wiring and
writes to `Saves/Custom/<Name>/Persistence.bin`.

**On disk, from 21 September 2026 (REVIEW.md F6): a stamped, atomic file.** `StampedFile.cs`
defines the format - a header with a magic, the world-save **generation**, the save time, the
store version, the payload length and the payload's SHA-256; the payload exactly as `SerializeCore`
wrote it; a trailer repeating the generation. The store is serialized into memory first, so a
`SerializeCore` that throws touches no file; the bytes go to `Persistence.bin.tmp` with
`Flush(true)` and are renamed over the live file (`AtomicFile.Write(path, byte[], ".prev", ...)`),
keeping the previous file as `Persistence.bin.prev` when there was one. A crash mid-write therefore
leaves the previous whole file live, never a half one. Files in the older format - an `int` version
then the payload, no stamp - still load, and are accepted only on a tree with no manifest anywhere.

**The generation is `PersistenceGeneration`'s.** Every store in a save is stamped with the same
number, `Current + 1`, and the manifest written after them (`Saves/Custom/Manifest.json`, from
`AfterWorldSave` once every stock `WorldSave` handler has written too) lists each store's generation,
length and hash and fingerprints every other file under `Saves/` by length and last-write ticks.
`Current` advances only when the manifest wrote. At boot a `Configure()` at `Int32.MinValue + 1`
verifies the tree - manifest against stores, manifest against world files, and a missing manifest
against the last backup's - and **refuses with a plain message and exit code 3** when they disagree:
the file, both generations (or both lengths), the last complete backup, and three choices, of which
`Saves/Custom/ACCEPT` holding the printed generation is the only override. There is no automatic
repair. The refusal is also written to `Data/Live/boot-refusal.json` for the bridge. `Verify` is pure
over two directories, so `[CoreSmoke`'s fixtures run it on scratch trees. `Core.Persistence` is the
health check; every store's own check names the generation it loaded and last saved.

**The one hole that remains:** a crash inside the very first stamped save leaves a tree with no
manifest anywhere and only legacy files, which is the pre-upgrade tree and loads. Once one stamped
save has completed, `Most Recent` carries a manifest and the "last save did not complete" rule
catches every later one.

**Failure contract.** A store that cannot load goes **degraded**: it loads empty, logs red, and
then **refuses to save**, so a corrupt or unreadable file is never overwritten with empty state.
A store whose save fails is degraded too, its live file is the previous good generation untouched,
the manifest lists it `ok:false` with the reason, the `save` ack says so, and the next boot refuses
the tree until the operator decides. The degraded flag surfaces through `HealthCheck`, so
`[CoreSmoke` reports it.

It also **quarantines** the unreadable file to `Backups/Degraded/<Name>-<timestamp>.bin`.
That is not belt-and-braces: `AutoSave.Backup()` moves the whole `Saves/` directory into
`Backups/Automatic/Most Recent` before every save, so refusing to save only protects the
original for one cycle, and it is gone after three. `Backups/Degraded/` is outside the
rotation. Recovery is to stop the shard, restore the `.bin`, and restart.

Known limitation: the degraded flag does not survive a restart — the quarantined copy, the red
console log and the manifest's `ok:false` entry are the durable evidence.

Neither the load nor the save path is allowed to throw: `World.cs` turns any exception escaping
a `WorldSave`, `WorldLoad` or `AfterWorldSave` handler into a fatal that terminates the shard.

## `HealthCheck` — the shard health registry

```csharp
HealthCheck.Register("MySystem", () =>
    _loaded ? HealthResult.Ok("42 records") : HealthResult.Fail("never loaded"));
```

Register from `Initialize()` (or a constructor for per-instance checks). `[CoreSmoke` reports
every registered check, and the admin API's `/api/status` will too. Each check is individually
guarded, so one bad check cannot break the report.

**Every ported system should register a check here** rather than growing `[CoreSmoke`.

## `CoreSmoke` — the `[CoreSmoke` command

`AccessLevel.Administrator`. Exercises all four shims with no gameplay involved, then reports
every registered health check. Output goes to both the invoker and the console.

It is a **permanent diagnostic**, not test scaffolding — it is the post-upstream-merge health
check for the foundation layer.

Because ServUO's console cannot invoke staff commands, set `Custom.CoreSmokeOnStart=True` in
`Config/Custom.cfg` to run it automatically at startup. Useful headlessly (over SSH, or in CI);
leave it `False` on a live shard.

**Fixture suites it runs.** `SaveIntegrity.RunFixtures` (the disk side of a save),
`BridgeFixtures.RunFixtures` (the shard's half of the request protocol) and
`HaulFixtures.RunFixtures` (delivery conservation, REVIEW.md F5). Each is one
`public static bool RunFixtures(List<string> report)` plus one `passed &=` line in
`CoreSmoke.Finish`, and each reports `  ok: <what holds>` or `  FAIL: <the actual values>`. The
first two build a scratch tree under the OS temp directory; `HaulFixtures` builds real bots on
`Map.Internal` instead, because what it tests is items moving between containers, and deletes
them in a `finally`.
