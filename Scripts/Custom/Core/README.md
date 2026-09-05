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
writes to `Saves/Custom/<Name>/Persistence.bin`. A version `int` is written ahead of the payload.

**Failure contract.** A store that cannot load goes **degraded**: it loads empty, logs red, and
then **refuses to save**, so a corrupt or unreadable file is never overwritten with empty state.
The degraded flag surfaces through `HealthCheck`, so `[CoreSmoke` reports it.

It also **quarantines** the unreadable file to `Backups/Degraded/<Name>-<timestamp>.bin`.
That is not belt-and-braces: `AutoSave.Backup()` moves the whole `Saves/` directory into
`Backups/Automatic/Most Recent` before every save, so refusing to save only protects the
original for one cycle, and it is gone after three. `Backups/Degraded/` is outside the
rotation. Recovery is to stop the shard, restore the `.bin`, and restart.

Known limitation: the degraded flag does not survive a restart — the quarantined copy and the
red console log are the durable evidence.

Neither the load nor the save path is allowed to throw: `World.cs` turns any exception escaping
a `WorldSave` or `WorldLoad` handler into a fatal that terminates the shard.

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
