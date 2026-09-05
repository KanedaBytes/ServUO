# Custom/Quests

Custom quests, plus the shard-wide auto-collect feature.

| File | What it is |
| --- | --- |
| `MartasFishRequest.cs` | Old Marta's fetch quest — 5 fish for 250 gold, once per character |
| `AutoCollectSystem.cs` | The shared 1 Hz sweep that flags matching backpack items |
| `QuestItemSafety.cs` | The predicate deciding what is safe to flag automatically |
| `QuestBootstrap.cs` | `Configure`/`Initialize` wiring and the two health checks |

The NPC is `Scripts/Custom/Mobiles/OldMarta.cs`. There is **no registry file** — ServUO binds a
quest to its giver through `MondainQuester.Quests`, so the only reference to `MartasFishRequest`
is that property on Marta.

## Which ServUO engine

`Server.Engines.Quests` — `BaseQuest`, `ObtainObjective`, `MondainQuester`, `QuestHelper`. Not the
legacy AOS `QuestSystem`, not XmlSpawner's quest layer. See CLAUDE.md §11.

The ModernUO original was built on `MLQuestSystem`, which **does not exist here**. Three of its
five files have no counterpart and were dropped: `CustomQuestRegistry` (no registry),
`AutoCollectInstaller` and `AutoCollectObjective` (nothing to wrap — see below).

## Once per character

`public override bool DoneOnce { get { return true; } }`. There is no `OneTimeOnly`.

On turn-in, `BaseQuest.RemoveQuest` appends a `QuestRestartInfo` to `PlayerMobile.DoneQuests`
(serialized inside the PlayerMobile save, *not* in `MLQuests.bin`), and `QuestHelper.Delayed`
refuses every later offer with cliloc 1075454.

**The record is only written when `Owner.AccessLevel == AccessLevel.Player`.** A staff character
can repeat the quest forever and `[ResetQuest` on one has nothing to clear. Test the lockout on a
real player character.

## Auto-collect

Stock ServUO has **no** auto-flagging. `QuestHelper.CheckItem` is the only function that turns an
item into obtain progress, and it has exactly two callers: the manual "Toggle Quest Item" context
entry on the player's own paperdoll, and crafting with `CraftQuestOption.QuestItem`. Fishing up a
fish with an active fish objective does nothing at all.

So this fills a real gap, and it does it by calling `QuestHelper.CheckItem` rather than
reimplementing the count.

**It sets `Item.QuestItem`, and that is not optional.** On ServUO the flag *is* the turn-in ledger
— `QuestHelper.TryDeleteItems` → `CountQuestItems` counts only flagged items — so incrementing
`CurProgress` alone would produce an objective that reads complete but cannot be handed in.

Because the flag also makes an item undroppable, untradeable and recoloured
(`Item.Nontransferable => QuestItem`, hue `0x04EA`), every candidate must first pass
`QuestItemSafety`, which rejects anything that is not plain: named, hued, insured, blessed,
player-crafted, non-standard craft resource, or carrying properties.

### Rules

- **Shard-wide.** Every active `ObtainObjective` on every quest, the ~100 stock ML quests
  included. That is the analogue of the ModernUO shard's `AutoCollectInstaller`, which wrapped
  every registered `CollectObjective`.
- **One shared timer**, not one per player. At 1 Hz across a populated shard, per-player timers
  would be hundreds of `Timer` instances competing for the same tick.
- **Top level of the backpack only**, matching the manual toggle (`ToggleQuestItem_Callback`
  requires `item.Parent == player.Backpack`, cliloc 1074769). A sub-container is therefore a safe
  stash for items the player does not want consumed.
- **Never re-touches a flagged item.** `ObtainObjective.Update` is a *toggle* — passing an
  already-flagged item back through `CheckItem` would un-flag it and subtract its progress.
- **Stops as soon as the objective completes**, so a player holding twenty fish for a five-fish
  errand does not get all twenty locked down. An oversized *stack* is still flagged whole, which
  is safe: `QuestHelper.DeleteItems` decrements the stack by exactly what the objective asked for
  and `RemoveStatus` unflags the remainder.
- **Says when the filter is why you are short.** If the objective is still incomplete and matching
  items were rejected, the player gets one throttled message naming the count and pointing at the
  manual toggle — which still works for anything the filter refuses. A rejection costs friction,
  never functionality.

### `MondainQuestData.QuestData`, never `pm.Quests`

The sweep reads `MondainQuestData.QuestData.TryGetValue(pm, out quests)`.

`PlayerMobile.Quests` is not a field — the getter is `MondainQuestData.GetQuests(this)`, which
**inserts an empty list for anyone it is asked about**, and `MondainQuestData.OnSave` writes every
entry it holds, empty ones included. Sweeping through the property at 1 Hz would grow and then
persist one junk row per online player. `OldMarta` reads the store the same way and for the same
reason.

### Config

`Config/Custom.cfg`:

| Key | Default | Effect |
| --- | --- | --- |
| `Custom.AutoCollectEnabled` | `True` | `False` reverts the whole shard to stock manual toggling |
| `Custom.AutoCollectNoticeSeconds` | `60` | Throttle on the "items were skipped" notice, per player |

## The Norton clash

Stock ServUO ships `Norton` "the fisher" (`Scripts/Mobiles/NPCs/Norton.cs`) offering
`DeliciousFishesQuest` — also `ObtainObjective(typeof(Fish), "fish", 5)` — and he **is spawned in
Trammel** (`Spawns/trammel.xml`).

`QuestHelper.CanOffer` refuses any quest sharing an objective `Type()` with one the player already
holds, so a player can carry Marta's errand or Norton's, never both. That is accepted rather than
worked around; it is arguably the right behaviour. What is *not* acceptable is the generic
refusal, so `OldMarta.OnTalk` detects the clash and says so in her own words before the engine's
"I have nothing for you" fires.

## Health checks

`[CoreSmoke` reports both.

- **`Quests.AutoCollect`** — `Fail` if enabled but the timer is dead or has not ticked for five
  seconds, `Warn` if disabled by config or faults have been recorded, `Ok` otherwise.
- **`Quests.Marta`** — `Fail` if the `GG_OldMarta` spawner is not in the world (run
  `[GG_Reimport`), `Warn` for the few seconds after an import before its first respawn tick, `Ok`
  once she is standing there.

## Staff commands

`[ResetQuest` and `[ResetAllQuests` live in `Scripts/Custom/Commands/ResetQuestCommands.cs` —
they are engine-wide staff tooling, not specific to this quest.
