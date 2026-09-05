# Navigation format: this shard vs. `uo-offline-server`

Written before `NavRecords.cs`, as a pre-check on step 4a.

*(Lives here rather than in `docs/` because ServUO's `[DocGen` calls `DeleteDirectory("docs/")`
before it writes, and `.gitignore` excludes that directory as generated output.)*

**Purpose: a documented, convertible mapping between the two formats — not compatibility.**
Their data is Felucca/T2A and has no facet field at all; ours is multi-facet from the start. A
converter is possible in both directions for the parts marked *adopt* and *keep*; the parts
marked *concept they don't have* / *concept we don't have* are the lossy edges, and this note
exists so a future converter author knows exactly where they are.

Sources read: `Distribution/Data/{Waypoints,Destinations,Zones}/*.json`, and the loaders
`CustomBots/Behaviors/{WaypointRegistry,WaypointGraph,DestinationCatalog,DestinationType}.cs`,
`CustomBots/ZoneRegistry.cs`.

The full port survey of `uo-offline-server` is deferred to before the bot-mobile layer.

---

## Shape of the two formats

| | uo-offline-server | this shard |
| --- | --- | --- |
| Files | 3 (`waypoints.json`, `destinations.json`, `zones.json`) + a 55 MB derived `fields_cache.bin` | 1 (`Data/Custom/navigation.json`) |
| Casing | `PascalCase` | `camelCase` (house rule, `Data/Custom/README.md`) |
| Reader | `System.Text.Json` `JsonDocument`, field-by-field `TryGetProperty` | Newtonsoft typed binding, `MissingMemberHandling.Error` |
| Unknown keys | silently ignored | **load failure** |
| Layout | `JsonSerializer` default indent — one *scalar* per line, ~30 lines per waypoint | `SerializeCompact` — one *record* per line |
| Facet | none; `Map.Felucca` implicit | `map` on every record, resolved with `JsonConfig.TryParseMap` |
| Identity | the display name, matched case-insensitively | opaque `id` (`[a-z0-9-]+`) separate from `name` |
| Versioning | none | `schemaVersion` |
| Failure mode | logs to console, loads what parsed | keeps live data, `Nav.Data` reports why |

The layout difference is the reason our list-valued fields are space-separated strings rather
than JSON arrays: `SerializeCompact` only collapses a container whose children are all scalars,
so a nested `"tags": [...]` would expand the whole record. Their format made the opposite trade
and pays ~30 lines per waypoint for it.

---

## 1. Waypoints

| Theirs | Ours | Call |
| --- | --- | --- |
| `Name` (identity **and** label) | `id` + `name` | **(b) keep ours.** Their own loader warns `'X' references unknown neighbor 'Y'` because renaming a node silently orphans every edge naming it. Splitting identity from label removes the failure class. |
| `X` `Y` `Z` | `x` `y` `z` | **(a) adopt** — same names, same meaning. `Z` is load-bearing for them ("Z values are critical — don't change them unless you re-verify"); for us it is advisory, because the pathing goal uses `map.GetAverageZ`. Convertible either way. |
| — | `map` | **(c) concept they don't have.** Felucca is implicit throughout their tree. Converting theirs → ours means stamping `"map":"Felucca"` on every record. |
| `Connects: []` (embedded, auto-symmetrised) | top-level `edges[]` records | **(b) keep ours.** Explicit-edges-only is a stated requirement, and a separate array lets an edge carry its own `kind` and `tags`. Convertible: their `Connects` fans out to one `edges` record per pair, deduplicated (they symmetrise on load, we do it in the graph build). |
| `ArrivalRange` (int, 0 = behaviour default) | `arrivalRange` | **(a) adopt, name and semantics.** Their comment is specific and hard-won: *"Override to 1 for door/entrance waypoints at unusual Z where the bot must actually step onto the exact tile (e.g. Z=27 raised entrances)."* Cheap field, prevents a real bug class. |
| — | `tags` | **(c) concept they don't have.** They have no tag vocabulary on nodes at all — see §5. |
| `_note` (free-form, ignored) | `note` | **(a) adopt the concept**, renamed without the underscore. Ours needs a modelled member because `MissingMemberHandling.Error` rejects unknown keys; declared `NullValueHandling.Ignore` so absent notes don't write `"note":null` on every line. |

## 2. Edges

They have no edge record — edges are `Connects` strings, cost is Euclidean distance, and every
cost adjustment is runtime-only.

| Theirs | Ours | Call |
| --- | --- | --- |
| implicit, always `walk` | `kind`: `walk` \| `gate` | **(c) concept they don't have.** They handle cross-region travel as a *fallback chain* in `TravelerBehavior` (Recall → nearest moongate → give up), not as graph edges. Ours makes the transition a first-class edge that search can cost. |
| cost = Euclidean distance | `distance × Π(tag multipliers)`, `costTags` in the JSON | **(b) keep ours.** They have three separate runtime mechanisms instead — `WaypointGraph.EdgePenalty` (a `static Func`), a `nodeCost` delegate passed per search, and `BotDangerMap` heat — and no authored cost at all. Authored multipliers are the thing you cannot retrofit cheaply. |
| `MaxLegDistance = 38`, warn only | `Custom.NavHopMaxTiles` default **12**, Warn | **(b) keep ours.** 38 is ModernUO's `BitmapAStarAlgorithm` window; ServUO's `FastAStarAlgorithm` window is also 38 (`AreaSize`) but centred on the *midpoint*, so a detour around a building leaves the box. The ModernUO shard independently settled on 15 for the same reason; 12 keeps margin. Same warn-don't-reject policy as theirs. |

## 3. Destinations

| Theirs | Ours | Call |
| --- | --- | --- |
| `Name` | `id` + `name` | **(b) keep ours**, as §1. |
| `X` `Y` `Z` | `x` `y` `z` | **(a) adopt.** |
| `Type` (flat 30-member enum) | `type` + `tags` | **(b) keep ours.** Their enum conflates category (`Bank`), mechanism (`DungeonDescend`) and resource (`MiningSpot`), so consumers are full of `if (type is A or B or C)` guards. Convertible theirs → ours by a lookup table; ours → theirs is lossy. |
| `City` (string) | `tags` + `zones` | **(b) keep ours.** 281 of their 480 destinations have `City` blank, and the field is denormalised from geography the zone data already knows. |
| `NearestWaypoint` (single, denormalised) | `waypoints` (space-separated list) | **(b) keep ours.** Theirs goes stale — `AuditNav` check #1 exists solely to catch it ("suggesting the real nearest", with a comment about drift of 220 tiles after remaps), and the catalog has a recompute-if-missing fallback. Plural because approaching from the north and the south are genuinely different routes. |
| `Arrivals[] {X,Y,Z,Waypoints[]}` | top-level `arrivals[]` keyed by `destination` | **(a) adopt the concept wholesale** — a destination is a *thing*, an arrival is a *standable tile with its own preferred approach waypoints*. This is the single most transferable idea in their tree. Flattened out of the destination record only so each arrival lands on one line. |
| `ArrivalX/Y/Z` (legacy single arrival, auto-migrated) | — | **(c) concept we don't have,** and won't: we start with the plural form. |
| — | `exclusive` on an arrival | **(c) concept they don't have.** They scatter unconditionally (`BankSitterBehavior.ScatterRadius = 5`). We need exact-tile spots for things like a guard post. |
| `Polygon[]` on the destination | `zones` only | **(b) keep ours.** They regret the split: `ZoneRegistry.Load` has to merge destination polygons in as synthetic zones because footprints ended up authored in two places. One home. |
| `Dungeon`, `Level`, `TargetX/Y/Z`, `TargetLevel` | `kind:"gate"` edges + zone tags | **(b) keep ours, generalised.** Their teleporter linkage is a destination-shaped special case; ours is an edge, which the router already understands. Scoping (`Dungeon` + `Level`) becomes zone tags. Convertible with a small transform. |

## 4. Zones

| Theirs | Ours | Call |
| --- | --- | --- |
| `Name` | `id` | **(b) keep ours.** |
| `Points[[x,y]…]` polygon | `x`/`y`/`width`/`height` rect now; `shape:"poly"` + `points` later | **(b) keep ours for now,** per the agreed plan — rects first, polygons added as a `shape` discriminator defaulting to `"rect"`, so no migration. Their polygon is directly convertible into that later form. |
| `Kind`: `Area` \| `Portal` | `tags` | **(b) keep ours.** `Area` is our default; a `Portal` (a painted threshold at a doorless gap, so a bot routes *through* it instead of grinding the wall) is expressed in our format as an explicit waypoint in the gap with edges either side — which is strictly more useful, because the router can cost it. |
| `LinkedDest` | — | **(c) concept we don't have.** Their zone-to-destination binding exists because a polygon can *be* a destination. Ours are separate records; the link is by proximity/`zones` tags. |
| `Type` (mirrors `DestinationType`) | `tags` | **(b) keep ours.** |
| — | `map` | **(c) concept they don't have.** |

## 5. Concepts one side has and the other does not

**They have, we don't (and why not, yet):**

- **`fields_cache.bin` — precomputed `DistanceField` flow-fields per destination**, with an
  FNV-1a fingerprint over the source data so it self-invalidates. This is their *final approach*:
  inside 60 tiles the bot stops using waypoints and walks the gradient, threading doors and
  interior floors. Genuinely good, and 55 MB. Not needed for daily life; revisit at the bot layer.
- **`HpaGraph` — auto-built Hierarchical Pathfinding A\***, 96-tile clusters, coord-derived stable
  names, exposing the same API as the hand-authored graph. Its header says it *"replaces the
  hand-recorded WaypointGraph"* — and nothing calls it. That they built the automated alternative
  and did not switch is the reason we are hand-authoring.
- **`BotDangerMap` — runtime danger heat** with a 45-minute half-life, fed by observed murders.
  Our `costTags` is the authored half of the same idea; the runtime half comes later, and slots in
  as an extra multiplier on the same edge cost.
- **`RedTerritory` — a hardcoded C# AABB** for Buccaneer's Den. This is exactly what our tagged
  `zones` are for; it is the gap in their design, not a feature.
- **`destinations_generated.json` / `forges_generated.json`** — a generate → review → curate
  workflow, with a `_comment` telling you never to edit the generated file. Worth copying when we
  have a generator; our seed is hand-derived from `Spawns/trammel.xml` instead.

**We have, they don't:**

- **`map` / multi-facet.** Theirs is single-facet by construction.
- **`routes[]` — authored ordered sequences** with `mode` (`cycle` / `oneway` / `pingpong`).
  They only ever *search*; a patrol loop or a shopkeeper's walk home is not a shortest path and
  cannot be derived. This is what 4b consumes.
- **`schemaVersion`.** Their migration is ad-hoc — a Python `ensure_arrivals()` mutating legacy
  `ArrivalX/Y/Z` on write.
- **`costTags` — authored edge cost multipliers.**
- **`kind:"gate"` edges** as a first-class cross-map transition.
- **`selfTests[]`** — canary route pairs, so routing is verifiable from the console with no
  client. Their equivalent (`[AuditNav` at world start) is richer but command-driven.
- **Load-failure contract.** Theirs loads what parsed and logs the rest; ours keeps the live data
  and reports what it is still running on, per the `RestrictedZoneSystem` precedent.

## 6. Things adopted outright

Restating, since these are the deliberate borrowings rather than divergences:

1. `Arrivals` as a first-class record with its own approach waypoints.
2. `arrivalRange` per waypoint, name and semantics unchanged.
3. Random arrival pick + scatter instead of a reservation system.
4. A hop cap tied to the engine's A\* window, validated at load, **warn not reject**.
5. Free-form `note` annotation on records.
6. Audits that name the bug they prevent — their `AuditNavCommand` header is the model for
   `[NavAudit`, including the caveat that waypoints at closed doors are false positives.

## 7. Convertibility summary

**Theirs → ours** is mechanical apart from three lossy points: `Type` needs a lookup table into
`type` + `tags`; `Kind:"Portal"` zones need a human to place the waypoint pair; `City` and
`LinkedDest` are dropped in favour of zone geometry. Everything else is a rename plus stamping
`"map":"Felucca"` and minting ids from names.

**Ours → theirs** loses `map` (single-facet target), `routes`, `costTags`, `gate` edges,
`exclusive`, and `schemaVersion`.
