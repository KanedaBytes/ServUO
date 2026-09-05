# tools/editor — the shard editor and its bridge

A browser map editor for this shard's data, ported from the ModernUO shard's `ShardEditor`.
**Read-only** as of step 5a: every layer is visible and live entities are overlaid, but nothing
can be edited yet. Editing is 5b; spawners are 5c.

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
| `*.test.js` | `node --test tools/editor/project.test.js` |
| `js/`, `index.html`, `style.css` | The editor |
| `tiles/` | Rendered map, gitignored |
| `../MapExport/` | The tile renderer |

## Security

The bridge binds `127.0.0.1` and nothing else, so nothing off this machine can reach it. That
alone is not sufficient — a page you visit in another tab can still POST to localhost — so every
mutating request is checked for a same-origin `Sec-Fetch-Site`, falling back to `Origin`. Reads
are unchecked because they are harmless; the only writes are request tokens.

The data endpoints take **no path from the caller at all**. Every file they read is a constant in
`whitelist.js`. That is the real sandbox: there is nothing to traverse because there is nothing to
steer. `whitelist.js` exists for the one endpoint that does take a name — the token drop — and so
that the boundary is testable rather than merely asserted. Token names are matched against a
pattern rather than sanitised; a name that has to be cleaned up before it is safe is a name worth
refusing.

## The two writers, and the golden fixture

`unproject` has to write our JSON back in exactly the layout `JsonConfig.SerializeCompact`
produces, which means **that layout rule now exists twice** — once in C#, once in `compact.js`.
Nothing stops those drifting, and the symptom in 5b would not be an error: it would be a quietly
reformatted or corrupted data file.

So the C# writer is the authority and its output is committed:

```
[NavExportGolden                              # in-game, writes Data/Custom/golden/
node --test tools\editor\project.test.js      # asserts unproject(project(golden)) === golden
```

This caught a real difference on the first run. Newtonsoft writes the JSON number `3.0` as `3.0`;
`JSON.stringify` writes it as `3`, because JavaScript has one number type and `JSON.parse` loses
the integer/float distinction. `navigation.json` carries `{"multiplier":3.0}`. So `compact.js`
does not round-trip through JavaScript values at all — scalars keep the exact source text they
were parsed from, and only edited values are re-formatted.

The shipped `navigation.json` and `britain-daily-life.json` are byte-identical to their goldens,
so the first save in 5b will not produce a whole-file reformat diff.

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

## What was rewritten, and what to port back in 5b

`view.js`, `shapes.js`, `overlays.js` and `tools.js` came over close to unchanged. Two files did
not:

- **`api.js`** — the original spoke to the in-shard API with a bearer token. Rewritten for the
  bridge; there is no token.
- **`app.js`** — the original's spine is the edit cycle: dirty tracking, per-shape baselines, an
  undo stack, a save that patches then reloads, and a persistent banner for the case where the
  write succeeded but the reload was rejected. None of that has anything to hold onto in a
  read-only step, and carrying it in would have meant several hundred lines calling bridge methods
  that do not exist — dead code that looks live.

**When editing returns in 5b, port that machinery back deliberately rather than reinventing it.**
The original's `style.css` records why the banner exists: a save once failed, reverted the view,
and said so only in a status line that cleared itself after six seconds. `app.js:216` refuses to
clobber dirty state on refresh for the same kind of reason.

Also worth keeping from the original: identity is `(file, pointer)` there, but ours is
`<kind>:<our id>` — never an array index, because an index breaks the moment a record is reordered
or deleted, and our records already have stable ids.

## The request channel

The bridge drops `Data/Live/requests/<name>.token`; the shard's `RequestPoller` picks it up within
a second, runs the matching command path, deletes the token and writes `<name>.ack.json`.

| Request | Runs |
| --- | --- |
| `nav-reload` | `NavigationSystem.TryReload` |
| `dailylife-reload` | `DailyLifeCommands.TryReload` |
| `gg-reimport` | `GGSpawnCommands.TryReimport` |
| `livemap-on` / `livemap-off` | The entity snapshot; the body carries `<seconds> [custom|all] [zoneId]` |
| `nav-export-golden` | Writes the golden fixtures |
| `health` | Writes `health.json` now rather than waiting for the timer |

**An unknown or malformed token is deleted and acked with an error, never ignored.** A token that
sits on disk forever looks exactly like a bridge that never wrote one, and a token that survives
its own failure is retried on every tick.

`livemap-on` with `all` and no zone is refused, at both the command and the token — 20,000 mobiles
every two seconds is not something to do by accident.
