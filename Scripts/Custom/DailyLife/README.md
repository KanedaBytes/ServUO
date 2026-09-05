# Custom/DailyLife

Britain day schedule: dawn/day/dusk/night phases driving tavern patrons, the night watch,
route-walking townsfolk, and shopkeepers who go home at dusk.

Config lives in `Data/Custom/britain-daily-life.json`.

Phase boundaries must stay identical to `LightCycle.ComputeLevelFor` (`<4` night, `<6` dawn,
`<22` day, else dusk). Sample the hour at the town anchor with `Clock.GetTime`, not globally —
the clock is skewed per facet and by longitude. Ephemeral NPCs delete themselves on world load
(`Timer.DelayCall(Delete)` at the tail of `Deserialize`) so JSON stays the single source of
truth.
