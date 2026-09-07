# `Data/Custom/reference` — navigation data from uo-offline

**The shard never loads anything in this directory.** It is authoring input: the editor draws it
as a read-only layer, and the Adopt action copies a region of it into `Data/Custom/navigation.json`
after re-walking every edge with the shard's own pathfinder. Nothing here is live data, and nothing
here has been verified against this shard's map.

## Source and licence

Converted from **[Klein187/uo-offline](https://github.com/Klein187/uo-offline)**, branch `main`,
commit **`fe18a469a47e1617579c216f68f4e433e3138ebf`**, installed 2026-09-06 — the version pinned in
`C:\Users\sean.GEKKOSTATE\uo-modernuo\uo-offline-version.json` and named in `CLAUDE.md`.

Source files, under that tree's `Distribution/Data/`:

| file | records |
| --- | --- |
| `Waypoints/waypoints.json` | 3952 waypoints, 8554 `Connects` entries |
| `Destinations/destinations.json` | 488 destinations |
| `Zones/zones.json` | 6 zone polygons |

**uo-offline is GPL-3.** This converted data is derived from it and carries the same licence; see
`LICENSE-BOTS` at the repository root, which covers the bot sources ported from the same tree.
`Distribution/Data/Navigation/fields_cache.bin` (55 MB of precomputed flow fields) is deliberately
not imported — see `nav-format-comparison.md` §5.

## Regenerating

```
node tools/nav-import/uo-offline.js
```

Reads the paths above, writes `uo-offline-nav.trammel.json`, and reports its counts and anything it
could not map. The converter is `tools/nav-import/uo-offline.js`; its tests are beside it and are
covered by `node --test tools/**/*.test.js`.

The file is committed rather than generated at build time, so that a change to it is a reviewable
diff. It is ~930 KB and 9,249 lines — one record per line, the same layout `Data/Custom` uses.

## What the conversion does

The field-by-field mapping is `Scripts/Custom/Core/Navigation/nav-format-comparison.md`, written
before this converter and unchanged by it. The decisions that are not a straight rename:

- **The facet is stamped `Trammel`, and the filename says so.** Their coordinates are Felucca and
  carry no facet field at all. Felucca and Trammel share terrain, so every coordinate is valid on
  both — and our world is on Trammel, where a walk edge has to be for anything to reach it. A
  Felucca conversion later sits beside this file rather than over it.
- **Ids are minted from their names**, which are their identity. `Britain Bank` becomes
  `britain-bank`, uniquified with a numeric suffix on collision — including across kinds, because
  `Nav.TryRoute` accepts an id naming a waypoint *or* a destination. Their name is kept verbatim as
  the display `name`.
- **Edges carry no tags.** Their `Connects` is a bare adjacency; we cannot know which of their
  roads is a road, and a guessed `road` tag would silently reweight every search through
  `costTags`.
- **A destination's `Polygon` becomes a zone.** Their own `ZoneRegistry` merges these in as
  synthetic zones at load, and the comparison doc calls the two homes a regret.
- **Dungeon and Lost Lands records are tagged, not dropped** — `dungeon` and `lostlands` — hidden
  by default in the editor layer and refused by Adopt until those steps exist.
- **Gather spots are dropped** (`MiningSpot` ×2, `LumberSpot` ×1). Our Site tool measures a face
  against real tiledata with the same 5×5 sweep the harvester runs; a coordinate copied from
  another shard's Felucca has been measured against nothing here.

## Counts

| | |
| --- | --- |
| waypoints | 3952 — **1921 overworld**, 1988 `dungeon`, 43 `lostlands` |
| edges | 4291, deduplicated from 8554 `Connects` entries |
| destinations | 485 (488 less the three gather spots) |
| arrivals | 472 |
| zones | 35 — 6 of theirs, plus 29 from destination polygons |
| unmapped | 3, all of them the dropped gather spots |

**2405 of the 4291 edges (56%) are longer than `Custom.NavHopMaxTiles`**, the longest 37. They
authored against a 38-tile leg cap; ours is 12, because ServUO's `FastAStarAlgorithm` searches a
38-tile box centred on the *midpoint* and a detour around a building leaves it. This is the single
most important fact about this file: **its edges are not walkable as they stand.** Adopt re-walks
each one with `NavCorridor.TryPath` and subdivides it under the cap, which is why adoption is
region-scoped and takes real time.

### The dungeon / Lost Lands boundary

Records east of x 5120 are Felucca's dungeon strip. Within it, the split at y 2300 is **measured,
not chosen**: the strip is dense from y 0 to 2099, *completely empty from 2100 to 2999*, then 43
waypoints at 3000 and above. Any cut inside that empty band gives an identical answer.

Destinations are tagged `dungeon` by their own `Dungeon` field rather than by coordinate, because
**ten of them sit at x < 5120** — a dungeon *entrance* is on the overworld, and the strip rule
alone would miss every one.
