# Spawns/Custom

Custom XmlSpawner2 spawn XML, laid out as `Spawns/Custom/<facet>/GG_<Thing>.xml`.

Kept separate from the stock `Spawns/` files so `[XmlLoad Spawns` and world regeneration do not
mix custom content with upstream spawns.

## The `GG_` prefix is load bearing

**Every spawner's `<Name>` must start with `GG_`** (for "Goblin Gang").

This is not decoration. Both `[XmlLoad` and `[XmlUnLoad` take an optional second argument,
`SpawnerPrefixFilter`, which is an **ordinal, case-sensitive `Name.StartsWith`** test. The prefix
is what makes this shard's ~n spawners addressable as a set without touching the ~2,500 stock
ones, and it is what `[GG_Reimport` and the `Quests.Marta` health check match on.

## Importing

| Command | Access | Effect |
| --- | --- | --- |
| `[GG_Reimport` | Administrator | **Use this.** Deletes every `GG_`-prefixed spawner, then re-imports `Spawns/Custom` |
| `[XmlLoad Spawns/Custom GG_` | Administrator | The raw import. Replaces by `<UniqueId>`; leaves orphans behind |
| `[XmlUnLoad Spawns/Custom GG_` | Administrator | Deletes the spawners whose GUIDs appear in those files |

`[XmlLoad` recurses directories, and `<UniqueId>` is the identity — a re-import **replaces** a
spawner rather than stacking a second one.

**Prefer `[GG_Reimport`.** `[XmlLoad` alone is an upsert: a spawn point deleted from the XML keeps
its spawner in the world forever, because there is no longer a file naming that GUID for
`[XmlUnLoad` to find. `[GG_Reimport` sweeps by prefix first, so the world always matches the
files. Deleting an `XmlSpawner` removes its spawned mobiles with it
(`OnDelete` → `RemoveSpawnObjects`), and that is safe for players mid-quest —
`QuestHelper.InProgress` matches a quest to its giver by `Type`, not by instance.

Do **not** use `[XmlNewLoad`: it mints fresh GUIDs and appends a suffix to every name, duplicating
rather than replacing.

## File format

No XML declaration line — the file starts directly with `<Spawns>`, matching every file in
`Spawns/`. Root `<Spawns>`, one `<Points>` element per spawner, two-space indent, `True`/`False`
capitalised. Copy the full element block from an existing file; absent optional elements are read
in `try`/`catch` and are fine, but staying consistent keeps diffs readable.

Non-obvious element meanings:

| Element | Meaning |
| --- | --- |
| `<UniqueId>` | The identity. A fresh GUID per spawn point; never reuse one across two points |
| `<X>` `<Y>` `<Width>` `<Height>` | The spawn **area**. `0`/`0` size means the single tile at X,Y |
| `<CentreX>` `<CentreY>` `<CentreZ>` | The spawner **item's own** location. **There is no `<Z>` element** — `CentreZ` is the Z |
| `<Range>` | This is `HomeRange`, not a spawn radius name you would guess |
| `<MinDelay>` `<MaxDelay>` | Respawn delay, in **minutes** unless `<DelayInSec>True</DelayInSec>` |
| `<DespawnTime>` | Hours. `<Duration>` and the refractory pair are minutes |
| `<Objects2>` | `TypeName:MX=<max>:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1`, multiple entries joined by the literal `:OBJ=` |
