# Custom/Core

Shims for engine facilities ServUO does not provide. Everything else in `Custom/` depends on
these, so they land first.

- `LoopQueue.cs` — the `Core.LoopContext.Post` equivalent. A `ConcurrentQueue<Action>` filled
  from any thread and drained by a repeating `Timer` on the game thread. See CLAUDE.md §4.
- `JsonConfig.cs` — Newtonsoft load/save plus the compact one-object-per-line writer the shard
  editor expects, so an editor save is a minimal diff rather than a whole-file rewrite.
- `CustomLogger.cs` — a `LogFactory` shim over `Utility.WriteConsoleColor`.
- `CustomPersistence.cs` — reusable base wrapping `Server.Persistence` +
  `EventSink.WorldSave`/`WorldLoad`.
