# Custom/Commands

Cross-cutting staff commands that do not belong to a single system. Commands local to a system
live with that system.

| File | Commands | Access |
| --- | --- | --- |
| `ResetQuestCommands.cs` | `[ResetQuest <QuestTypeName>`, `[ResetAllQuests` | GameMaster |
| `GGSpawnCommands.cs` | `[GG_Reimport` (alias `[GGReimport`) | Administrator |

Both are engine-wide rather than tied to one quest or one spawner, which is why they live here
rather than under `Custom/Quests/`.

## `[ResetQuest` / `[ResetAllQuests`

Quest state lives in **two** unrelated stores, and both commands clear both:

- active instances — `MondainQuestData` → `Saves/Quests/MLQuests.bin`, reached through
  `PlayerMobile.Quests`. Cancelled with `BaseQuest.OnResign(true)`, the engine's own cancel path,
  which also un-flags the player's quest items and deletes any granted delivery items.
- the "already did this" record — a `QuestRestartInfo` in `PlayerMobile.DoneQuests`, inside the
  PlayerMobile save.

There is **no quest registry** to validate a type name against, so `[ResetQuest` resolves through
`ScriptCompiler.FindTypeByName` / `FindTypeByFullName` and then requires the result to be a
concrete `BaseQuest`. That assignability test is what stops a short name that collided with an
unrelated type from acting on the wrong thing.

`[ResetAllQuests` confirms through the stock `WarningGump` before erasing anything, and
deliberately **leaves earned permission flags alone** — `PlayerFlag.Spellweaving` and
`PlayerFlag.Bedlam` survive, because revoking a castable skill is not what "reset quests" means.

Note that `BaseQuest.RemoveQuest` only writes the completion record for
`AccessLevel.Player`, so neither command has anything to clear on a staff character.

## `[GG_Reimport`

Deletes every `GG_`-prefixed `XmlSpawner` and re-imports `Spawns/Custom`. See
`Spawns/Custom/README.md` for why the prefix matters and why this is preferred over a bare
`[XmlLoad`.

## Note on `[Aliases]`

`[Aliases("...")]` is **documentation only** — `HelpInfo` and `Docs` read it, but
`CommandSystem.Handle` dispatches on registered names alone. Register every alias explicitly with
its own `CommandSystem.Register` call, as `GGSpawnCommands` does and as XmlSpawner does upstream.
