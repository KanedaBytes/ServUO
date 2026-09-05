# Custom/Quests

Custom quests plus the auto-collect feature.

Auto-collect: one shared 1-second timer sweeps players who have an active `ObtainObjective` and
counts matching backpack items for them, instead of making the player target each item through
the stock toggle.

**It sets `Item.QuestItem`, gated by `QuestItemSafety`.** On ServUO that flag *is* the turn-in
ledger — `QuestHelper.TryDeleteItems` → `CountQuestItems` counts only flagged items — so
incrementing `CurProgress` alone would produce an objective that reads complete but cannot be
handed in. Because the flag also makes an item undroppable, untradeable and recoloured
(`Item.Nontransferable => QuestItem`), `QuestItemSafety` must reject anything that is not plain:
named, hued, insured, player-crafted, non-standard resource, or carrying properties.
