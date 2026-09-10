# `Data/Custom/reference` — navigation data from uo-offline

**The shard never loads anything in this directory.** It is authoring input: the editor draws it
as a read-only layer, and the Adopt action copies a region of it into `Data/Custom/navigation.json`
after re-walking every edge with the shard's own pathfinder. Nothing here is live data, and nothing
here has been verified against this shard's map.

## Source and licence

Converted from **[Klein187/uo-offline](https://github.com/Klein187/uo-offline)**, branch `main`,
commit **`7f38c7cd586dc67dc96a4857754e65987351fe2e`** ("Update window and README for the September 8
update", 2026-09-08) — the git clone at `E:\dev\UO\uo-offline`, named in `CLAUDE.md`'s
reference-install table.

**Re-pointed from the `fe18a469` snapshot, and the conversion did not move.** This file was first
converted from the *installed* copy under `C:\Users\sean.GEKKOSTATE\uo-modernuo\ModernUO\Distribution\Data`,
whose bot content is untracked and so has no history and no per-file dates. The clone has both. The
two pins carry **identical navigation data** — 3952 waypoints, 8554 `Connects` and 488 destinations
on each side, with nothing added, removed or changed, because `git log` on `Waypoints/waypoints.json`
and `Destinations/destinations.json` last touches them at `f20a96e`, which predates `fe18a46`. Their
bytes differ only in line endings. Re-running the converter after the re-point produced this file
**byte-for-byte unchanged**, which is the check to repeat whenever `SOURCE` moves: a re-point that
changes the output is either a real upstream change or converter non-determinism, and both need
looking at before anything is adopted from it.

Source files, under that tree's `playerbots/data/`:

| file | records |
| --- | --- |
| `Waypoints/waypoints.json` | 3952 waypoints, 8554 `Connects` entries |
| `Destinations/destinations.json` | 488 destinations |
| `Zones/zones.json` | 6 zone polygons |

**uo-offline is GPL-3.** This converted data is derived from it and carries the same licence; see
`LICENSE-BOTS` at the repository root, which covers the bot sources ported from the same tree.
`Data/Navigation/fields_cache.bin` (55 MB of precomputed flow fields) is deliberately not imported —
see `nav-format-comparison.md` §5. It is not in the clone at all and never was: `install.ps1:507-508`
calls it "a generated cache the bots rebuild on first run", and
`playerbots/source/CustomBots/Nav/DestinationFieldCache.cs:73` writes it on their side from the
destination catalog plus live map geometry. There is nothing upstream to import even if we wanted it.

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
- **A waypoint's id is minted from its name and namespaced `uo-`.** `WP 157` becomes
  `uo-wp-157`, uniquified with a numeric suffix on collision — including across kinds, because
  `Nav.TryRoute` accepts an id naming a waypoint *or* a destination. Their name is kept verbatim
  as the display `name`. The prefix is not cosmetic; see **What Adopt has to know**.
- **A destination's id is `<town>-<kind>`**, descriptive rather than serial: `trinsic-bank`,
  `trinsic-dock-2`, `trinsic-shop-smith`. `uo-bank-41` said nothing, and the id is what appears in
  a route, a bot's status line and every log about it. The town comes from their `City`, which
  every one of the 184 overworld destinations carries — their six zones cannot supply it, being a
  bank area, a dock and four portals. A vendor keeps its trade, or a town's seven provisioners are
  seven numbered shops. Destinations are **not** `uo-` prefixed; provenance is the `source` field,
  and uniqueness against our own ids is asserted by the tests. `Britain` slugifies to `britain-`,
  which does not collide with our `brit-`.
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

## What Adopt has to know

Three facts about this file that the Adopt step cannot discover for itself.

### Ids are namespaced `uo-`, and that is load-bearing

Their waypoints are named `WP 157`, which slugifies to `wp-157` — and `wp-<n>` is exactly what our
corridor tool mints for an unnamed road waypoint. Before the prefix, **23 reference ids collided
with ids already in `navigation.json`**, among them `wp-1`: ours on the west road at 1381,1750,
theirs a different tile at 1564,1687. Adopting one would have overwritten authored data.

The prefix makes that impossible by construction rather than by a check somebody has to remember,
and `uo-offline.test.js` asserts the two id sets stay disjoint.

### An id is not derivable from the record beside it

A destination's id is its town and its kind, so a town with several of one kind gets `-2`, `-3`:
Trinsic has seven provisioners and two banks. **The suffix order is stable** — destinations are
sorted by position before ids are minted — because a proposal whose ids move between imports
cannot be reviewed by diffing.

Waypoint ids are uniquified the same way where two of their names slugify alike.

**Adopt must copy the `id` field, never re-derive it from `name` or `City`** — a re-import that
ordered two records differently would otherwise repoint an adopted edge at the wrong one.

### 177 destinations have no arrival point

Not a conversion loss: their `Arrivals` array is genuinely absent on those records, and their own
catalog falls back to `NearestWaypoint` at runtime. The split matters because a destination with no
arrival is one a bot can be sent to and cannot stand at — `Nav.Data` warns about exactly that.

| | destinations |
| --- | --- |
| with at least one arrival | 308 |
| **no arrival, tagged `dungeon`** | **173** — refused by Adopt anyway |
| **no arrival, overworld** | **4** — all duplicates of a better-covered record nearby |

So only four adoptable destinations lack one, and all four are duplicates of a better-covered
record a few tiles away. **Adopt should offer to skip a destination with no arrival**, rather than
write one that will warn at the next load.

Counts of arrivals per destination, for the 308 that have any: 254 have one, and the rest run to
eight — so the 485-vs-472 difference between the two totals is not a shortfall, it is that one
destination can own several.

### The dungeon / Lost Lands boundary

Records east of x 5120 are Felucca's dungeon strip. Within it, the split at y 2300 is **measured,
not chosen**: the strip is dense from y 0 to 2099, *completely empty from 2100 to 2999*, then 43
waypoints at 3000 and above. Any cut inside that empty band gives an identical answer.

Destinations are tagged `dungeon` by their own `Dungeon` field rather than by coordinate, because
**ten of them sit at x < 5120** — a dungeon *entrance* is on the overworld, and the strip rule
alone would miss every one.
