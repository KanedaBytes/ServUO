# tools/editor — the shard editor and its bridge

A browser map editor for this shard's data, ported from the ModernUO shard's `ShardEditor`.

Step 5a made every layer visible and overlaid live entities; 5b makes it editable. Spawners are 5c.

You can add, move and delete waypoints, destinations, arrivals and zones; link and unlink edges;
author routes by clicking waypoints in order; and edit the daily-life config as a form. A save
writes the file, asks the shard to reload it, and tells you what the shard said.

```
tools\editor\export-tiles.ps1      # render the map, once
node tools\editor\bridge.js        # then browse http://127.0.0.1:8081/
```

## Why a file bridge and not an API in the shard

The ModernUO version ran an `HttpListener` inside the shard with a bearer token. This one does
not, and the difference is worth stating because SHARD.md used to describe the other design.

The bridge is a separate local process. The shard writes files (`Data/Live/entities.json`,
`Data/Live/health.json`) and watches a directory (`Data/Live/requests/`); the bridge reads those
files and drops tokens into that directory. Neither process holds a socket to the other.

That buys three things. The shard has no HTTP surface and no auth to get wrong. Either side can
restart without the other noticing — a token written while the shard is down is picked up when it
comes back. And the whole channel is inspectable with `type` and `del`, which an API is not.

## Layout

| File | What it is |
| --- | --- |
| `bridge.js` | The server. Node stdlib only, no `package.json`, no `node_modules` |
| `whitelist.js` | The path sandbox |
| `compact.js` | Raw-preserving JSON parser and the writer that matches `JsonConfig.SerializeCompact` |
| `project.js` | `project()` our schema into editor shapes, `unproject()` back |
| `js/validate.js` | The shard's structural checks, replicated - shared by the browser, the bridge and the tests |
| `js/tools.js` | The create tools and the modal that finishes each one |
| `js/ids.js` | Auto-generated ids, mirroring `[NavMark` |
| `js/build.js` | A finished tool to a shape - the seam between the browser and the writer |
| `js/live.js` | The Live panel's line and snapshot age |
| `fake-shard.js` | A stand-in `RequestPoller` for the tests: watches the request directory, answers acks |
| `*.test.js` | `node --test tools/editor/*.test.js` (the directory form fails on Node 22) |
| `js/`, `index.html`, `style.css` | The editor |
| `tiles/` | Rendered map, gitignored |
| `../MapExport/` | The tile renderer |

## Security

The bridge binds `127.0.0.1` and nothing else, so nothing off this machine can reach it. That
alone is not sufficient — a page you visit in another tab can still POST to localhost — so every
mutating request is checked for a same-origin `Sec-Fetch-Site`, falling back to `Origin`. Reads
are unchecked because they are harmless. The check sits before the POST dispatch rather than inside
each handler, so a new endpoint cannot forget it.

The data endpoints take **no path from the caller at all**. Every file they read is a constant in
`whitelist.js`. That is the real sandbox: there is nothing to traverse because there is nothing to
steer. `whitelist.js` exists for the endpoints that do take a name — the token drop, and now the
save — and so that the boundary is testable rather than merely asserted. Token names are matched
against a pattern rather than sanitised; a name that has to be cleaned up before it is safe is a
name worth refusing. A save name is not even a pattern: it is a key in a three-entry table, so
`../` and `%2e%2e%2f` are both simply not files.

## The two writers, and the golden fixture

`unproject` has to write our JSON back in exactly the layout `JsonConfig.SerializeCompact`
produces, which means **that layout rule now exists twice** — once in C#, once in `compact.js`.
Nothing stops those drifting, and the symptom in 5b would not be an error: it would be a quietly
reformatted or corrupted data file.

So the C# writer is the authority and its output is committed:

```
[NavExportGolden                          # in-game, writes Data/Custom/golden/
node --test tools\editor\*.test.js       # asserts unproject with no edits is the identity
```

This caught a real difference on the first run. Newtonsoft writes the JSON number `3.0` as `3.0`;
`JSON.stringify` writes it as `3`, because JavaScript has one number type and `JSON.parse` loses
the integer/float distinction. `navigation.json` carries `{"multiplier":3.0}`. So `compact.js`
does not round-trip through JavaScript values at all — scalars keep the exact source text they
were parsed from, and only edited values are re-formatted.

The shipped `navigation.json` and `britain-daily-life.json` are byte-identical to their goldens,
so the first save does not produce a whole-file reformat diff — verified by making one: a no-op
save of all three files through the real bridge to the real shard came back byte-identical.

**Creating a record needs the same discipline for a different reason.** An edit changes a value in
place, so the layout takes care of itself; a new record has to choose its own field order, and
Newtonsoft emits the `[JsonProperty]` declaration order. So `project.js` holds a `TEMPLATES` table
transcribed from `NavRecords.cs` and `DailyLifeConfig.cs`, and a test walks every record in the
golden asserting its keys are a subsequence of the template — a subsequence, because an optional
field like `note` is legitimately absent. `omitWhenBlank` mirrors `NullValueHandling.Ignore`, and
**a created record never contains `null`**: an optional field is either written with a real value
or its key is not there.

A template rather than copying the shape of a sibling record, because `restricted-zones.json` ships
as `{"zones": []}` and has no sibling to copy — and a donor that happened to carry a `note` would
give every new record a `"note": ""`.

## Tiles

`export-tiles.ps1` renders a pyramid at `tiles/<facet>/<z>/<x>/<y>.png`, 256px, level 0 = the
whole facet in one tile, deepest = one pixel per game tile. Trammel is 599 tiles across levels
0–5 and takes about six seconds.

The level count must match `view.js maxZoomFor()` exactly or the editor requests tiles that were
never rendered; both ceil-halve until the facet fits one tile.

**A finer level is not an upscaling problem.** Below one pixel per game tile there is no more
radar data — a genuine detail level would mean rendering art tiles instead of radar colours,
which is a different tool. The editor magnifies with nearest-neighbour, which is honest about
what the data is. Deferred.

A second facet is another run, not a code change: `export-tiles.ps1 -Facet Felucca`.

## Coverage-gap overlay

Adapted from uo-offline-server's editor with three changes.

- **Chebyshev, not Euclidean.** Theirs measured `sqrt(dx²+dy²)` while every other distance in
  their system — and ours — is `max(|dx|,|dy|)`. UO movement is eight-directional, so Euclidean
  overstates by up to √2 and paints gaps around 40% early.
- **The bands come from the hop cap**, not from literals. Theirs were 28 and 38 against a 38-tile
  limit; ours are the same ¾ ratio against `Custom.NavHopMaxTiles`. A test asserts the editor's
  `HOP_CAP` matches the config, because an overlay whose numbers are not the pathfinder's numbers
  is decoration.
- **Nothing is painted where coverage is good**, which is what lets it be a permanent layer.

**Read it knowing what it measures.** It is distance to the nearest waypoint *node*, not
reachability *through the graph*. On our data roughly 60% of the padded bounding box reads as a
gap — which is correct and expected, because a nav graph is a road skeleton rather than a
covering, and a bot genuinely cannot path from the middle of a building. What it cannot see is a
river, a wall, or a waypoint island with no edges out of it: all three read as covered.

Making it truthful needs a walkability raster exported from the server's own movement rules.
uo-offline generates exactly that (`walk_atlas.pgm.gz`, 620 KB gzipped, from their
`Walkable.TryFindSeedZ`) and then never uses it. That is the natural follow-up, and it would also
let the editor flood-fill from the graph instead of measuring straight lines.

## Saving

`POST /api/save/<file>`, where `<file>` is one of `navigation`, `dailyLife`, `restrictedZones` —
a **logical name, never a path**. That is the same sandbox the read endpoints use: there is nothing
to traverse because there is nothing to steer. `entities` and `health` are deliberately not
writable; the shard writes those, and an editor able to overwrite a snapshot could lie to itself
about the live world.

The body is `{baseHash, updates, creates, deletes}` — the shapes that changed, not the document.
In order:

1. **The hash is checked.** `/api/shapes` hands out a hash of each file's raw bytes, and a save
   sends back the one it started from. `[NavMark` and `[NavRecord` write these same files from
   inside the shard, so a stale editor must not be able to flatten a walk somebody just recorded.
   Mismatch is a **409** carrying the hash the file is actually at, and nothing is written.
2. **`unproject` runs.** A shape that names no record, belongs to another file, or is derived is a
   **400**, and still nothing is written.
3. `<file>.bak`, then a temp file, then a rename — mirroring `AtomicFile.Write` and the `.bak`
   convention `NavigationSystem.Save` has always used.
4. A reload token is dropped and its ack awaited, bounded at five seconds.

**A write whose reload was refused answers 200, not an error.** The file *is* on disk and the shard
*is* still running the previous config; both halves have to reach the editor. Answering 4xx would
send it down the "nothing happened" path while the file on disk said otherwise — which is the exact
failure the persistent banner exists for. So the response is
`{written, reloaded, message, errors, warnings, hash, backup}`, and `written: true, reloaded: false`
is a normal outcome with the shard's own reason attached.

`POST /api/restore/<file>` copies the `.bak` back and reloads. That is what makes discard mean
discard: a save whose reload was refused has already written, so dropping the editor's local edits
alone would leave a bad file to fail at the next restart. The `.bak` is the pre-save bytes exactly,
which no reconstruction from shapes can promise.

## Two tiers of wrong, and which is which

`js/validate.js` reproduces the shard's structural checks so the editor can preview a save. It is
shared: the browser imports it, the bridge dynamic-imports it for `dryRun`, and the node tests
import it too — one copy of the rules, so a preview line and a banner line are the same sentence.

**The tiers are not what they look like.** On this shard a dangling edge id is a *warning*: the edge
is dropped and the reload succeeds. So are an over-cap hop and a destination with no arrivals. What
actually refuses a reload is a duplicate or malformed id, an unknown facet, a bad `kind` or `mode`,
a self-edge, a route with fewer than two waypoints — and **any unknown JSON key**, because
`JsonConfig` sets `MissingMemberHandling.Error`. Calling a warning fatal would block a legal edit;
calling a fatal a warning would report "saved" over data that had just been broken.

So **validate never gates a real save**. It runs live in the panel and for an explicit dry run, and
that is all. It is a replica, and a false positive in a replica must not be able to stop someone
writing a file the shard would have accepted. The shard says no itself, and says why.

Three checks are deliberately absent, and the module says so rather than implying completeness:
vendor type names (the shard resolves those against its own type table), selfTest pathability, and
`[NavAudit`'s blocked-edge check — the last two need real map data.

What the replica *can* do that the shard cannot is name the shape. The shard reports
`waypoints[37]`, an array index that shifts under a delete; a finding here carries `shapeId` too,
so the editor can select the record being complained about.

The replica is checked against the real thing rather than assumed: running the shard against a
deliberately over-long hop produced four warnings where this file produced three, because a `cycle`
route also walks the closing leg from its last waypoint back to its first. That is now a test.

## What came from the ModernUO editor, and what did not

`view.js` came over unchanged. `shapes.js` and `tools.js` came over and were changed. `api.js` and
`app.js` were rewritten, and `overlays.js` is still dead code waiting on a bridge route.

**Ported close to verbatim**, because they were right and the reasons are still the reasons: the
demand-driven render with its `pending` flag, `ResizeObserver` and device-pixel-ratio watcher; the
drag machine; the context menu and its edge-clamping; the filter and its clickable match list; the
persistent banner. The original's `style.css` records why the banner exists at all — a save once
failed, reverted the view, and said so only in a status line that cleared itself after six seconds
— and `refreshShapes` still refuses to run over unsaved edits for the same reason.

**Ported with changes.** The undo step widened from the original's geometry-only
`{shape, geometry}` to `{op, shapeId, before, after}`, so creates and deletes are undoable too, and
redo is new — the original had none. Every edit map is keyed by shape id rather than by the shape
object, because `refreshShapes` replaces every object it holds and an object key would silently
drop the lot on the first refresh. The drag machine refuses derived geometry: a route is edited by
its waypoint list and an edge by its two ends, so dragging their lines would write nothing.

**Not ported.** Ctrl+D duplicate, which needs a collision dance for id-bearing records. The
original's two free-polyline tool kinds, which had nothing in this schema to write to. And the
original's `save`/`discard`, for the reason below.

Identity is `(file, pointer)` in the original and `<kind>:<our id>` here — never an array index,
because an index breaks the moment a record is reordered or deleted, and our records already have
stable ids.

**And the save is a different shape, so `save` and `discard` are rewrites rather than ports.** The
original PATCHed one record at a time by `(file, JSON pointer)` against an API inside the shard.
Ours sends the shapes that changed for one file and lets the bridge rewrite it, because there is no
API — which is also why the stale-hash check exists and the original needed no such thing.

The one place an index survives is `arr:<destId>#<n>`, an arrival's position among *its
destination's* arrivals. Arrivals have no id in the schema, and the hash makes the file immutable
between the editor reading it and saving it, so the array the browser counted is the array the
bridge parses. What the hash does not cover is one batch deleting an earlier arrival and moving a
later one — so `unproject` resolves every id to a node reference over the untouched tree *before*
it mutates anything, and deletes by node identity. A test runs a mixed batch forwards and backwards
and demands identical output.

## The tools

A tool is a data record in `tools.js`, not an object with methods: it names the geometry to collect
and the fields to ask for afterwards, and the state machine for all of them lives in `app.js`.
Adding a tool is a table entry; adding a KIND is a change in three places, which is about the right
friction for the difference.

| kind | what it collects | tools |
| --- | --- | --- |
| `point` | one click | waypoint, destination |
| `rect` | a drag | nav zone, restricted zone |
| `pair` | two existing shapes, no form | edge |
| `chain` | existing shapes in order, Enter finishes | route |
| `owner-then-point` | a shape, then a tile | arrival |

`pair`, `chain` and `owner-then-point` collect **existing shapes** rather than bare points, because
everything they build is made of ids: an edge is two waypoint ids, a route is a list of them, an
arrival belongs to a destination. That is also why the ModernUO original's free-polyline kinds are
gone rather than ported — there is nothing in this schema to write a free polyline to.

A click that hits nothing collectable says so in the readout rather than doing nothing. Silence is
how click-to-collect feels broken.

**Ids are auto-generated the way `[NavMark` does it** (`js/ids.js`, mirroring
`NavigationCommands.NextId`): the smallest zone containing the point, its first tag as the prefix,
then the first free number. Two deliberate differences, both stricter than the shard. It counts
every id in the file rather than only the waypoints that survived binding, so it cannot hand back
an id that collides with a record that was dropped with a warning; and it counts across kinds,
because `Nav.TryRoute` accepts an id naming a waypoint *or* a destination and a collision between
those two is genuinely ambiguous. The id is editable in the create form before the first save: an
auto id is a starting point, not a decision.

**Daily life has no create tools at all.** Its records are edited as a form in the side panel -
shopkeeper shop and home, watch post route or destinations, the tavern's patron count - and adding
a shopkeeper stays a JSON edit. The map markers for those records (`marker:shop:baker` and friends)
are *derived*: their coordinates live in `navigation.json`, so they are not draggable and the
bridge refuses them by name.

## Labels

Drawn in a second pass over the shapes, not inline with them, because a label drawn during the
first pass gets painted over by the next shape's fill.

**Not every shape is on the map.** The daily-life records — the tavern settings, a shopkeeper, a
watch post — are `kind: 'form'` with no points and no rect, because that file holds no coordinates:
every location in it is a nav id. `hasGeometry` is the guard, and it is a guard rather than an
assumption because the assumption cost a bug. `drawShape` fell through to its point branch, threw
on `shape.points[0]`, and killed the draw loop mid-frame — so the label pass never ran, and the
entity pass, which comes after it, never ran either. One missing check, two layers invisible, and
an exception inside `requestAnimationFrame` that only the console ever saw. `hitTest` walked the
same undefined array, so canvas clicks were throwing too.

Placement is collision-avoided against a per-frame list of boxes. A label that collides tries
below, then right, then left, and **if all four positions are taken it is skipped** — no dot, no
ellipsis. An unreadable heap is the thing being fixed and half of one is still it.

Priority decides who gets the space: selected, then hovered, then the sole filter match, then the
layer's `labelPriority`, then smaller shapes. Selected and hovered reserve their boxes first, which
is exactly why hovering something in a crowd works — everything else has to fit around it.

Each layer also carries a `labelAt` zoom floor. Arrivals sit at 1.2 because they cluster four and
five deep around one destination; routes and edges are `Infinity`, so their labels appear only on
hover or selection, which is what the original did for polylines and for the same reason.

**An over-cap edge is drawn red**, using the coverage overlay's `edgeColor` against
`Custom.NavHopMaxTiles`. That matters because the shard *accepts* an over-cap hop with a warning
and the NPC then walks into scenery, so dragging a waypoint too far has to look wrong immediately
rather than at the next reload.

Both passes are tested against a stub canvas that records calls rather than pixels — enough for
the two failures that actually happened: a pass that never ran, and a pass that ran and rejected
every box.

## The Live panel

`live: 10 @ seq 147, 2s ago`, and the age is the point of it. A sequence number that has stopped
moving looks exactly like a quiet town until you know when it last moved, so past six seconds —
three missed writes at the two-second default — the line turns amber.

It ticks on its own one-second timer rather than only on a successful poll, because when the shard
stops answering `pollEntities` swallows the error and stops updating: an age computed only on
success would freeze at the last good value, which is the one number that must not.

## The request channel

The bridge drops `Data/Live/requests/<name>.token`; the shard's `RequestPoller` picks it up within
a second, runs the matching command path, deletes the token and writes `<name>.ack.json`.

| Request | Runs |
| --- | --- |
| `nav-reload` | `NavigationSystem.TryReload` |
| `dailylife-reload` | `DailyLifeCommands.TryReload` |
| `zones-reload` | `RestrictedZoneSystem.TryReload` |
| `gg-reimport` | `GGSpawnCommands.TryReimport` |
| `livemap-on` / `livemap-off` | The entity snapshot; the body carries `<seconds> [custom|all] [zoneId]` |
| `nav-export-golden` | Writes the golden fixtures |
| `health` | Writes `health.json` now rather than waiting for the timer |

**`zones-reload` is new in 5b, and its absence was a live bug rather than a gap.** The bridge served
`restricted-zones.json` and `shapes.js` mapped that layer to `nav-reload`, so an edit there would
have reloaded the wrong system and reported success. `bridge.test.js` now reads both files and
asserts that every reload the editor can ask for has a `case` in `Dispatch` — the cheapest possible
guard against the two languages drifting apart again.

**An unknown or malformed token is deleted and acked with an error, never ignored.** A token that
sits on disk forever looks exactly like a bridge that never wrote one, and a token that survives
its own failure is retried on every tick.

`livemap-on` with `all` and no zone is refused, at both the command and the token — 20,000 mobiles
every two seconds is not something to do by accident.

### The ack, and how it stopped answering the wrong question

`WriteAck` produces `{request, token, ok, message, errors, warnings, utc}`. `errors` and `warnings`
are new, and they carry **the shard's own validator strings unedited** — so the banner and the
console say the same thing. Before, a reload that succeeded with problems acked `"3 warning(s)"`
and there was no way to find out which three without reading a console the person driving the
editor is not looking at. Both arrays are always present, empty when there is nothing: an absent
key and an empty list only read alike in JavaScript if every reader remembers to guard, and one of
them will not.

`JsonConfig.TryLoad` already collected errors as a list and all three systems flattened it with
`"; "` at the door. They now keep the list — `TryReload(out error, out IList<string> errors)` — and
flatten only for their own console line. Re-splitting on `"; "` in JavaScript would have been
unsafe: a Newtonsoft parse message can contain anything.

**A stale ack used to be read as the current answer.** The shard overwrites `<name>.ack.json` in
place and never deletes it, and `/api/ack/` reports `pending` only when the file is *absent* — so
from the second request of a session onwards, a waiter got the previous run's answer instantly and
believed it. Save-then-reload is unusable like that. Two fixes, because they close different holes:
the bridge **deletes the ack before dropping the token**, which needs no shard change at all; and
the token body carries a **nonce** that `WriteAck` echoes back in `token`, which survives two
editor tabs and a bridge that dies mid-sequence. The nonce is generated in the bridge, not the
browser, and only for the four requests whose dispatch ignores its body — `livemap-on` parses
its body and is excluded.
