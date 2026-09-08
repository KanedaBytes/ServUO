# tools/editor — the shard editor and its bridge

A browser map editor for this shard's data, ported from the ModernUO shard's `ShardEditor`.

Step 5a made every layer visible and overlaid live entities; 5b makes it editable. Spawners are 5c.

You can add, move and delete waypoints, destinations, arrivals and zones; link and unlink edges;
author routes by clicking waypoints in order; and edit the daily-life config as a form. A save
writes the file, asks the shard to reload it, and tells you what the shard said.

```
tools\editor\export-tiles.ps1      # render the radar map, once
node tools\editor\bridge.js        # then browse http://127.0.0.1:8081/
```

The base map is radar by default. **Base map → Art** draws the real client art isometrically, with
a floor slider; those tiles are rendered on demand rather than exported, because a facet-wide
isometric render is 61 gigapixels per floor. See **The art view**.

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
| `js/worksites.js` | The work-site overlay: reach, candidates and which tiles are taken |
| `js/audit.js` | What `[NavAudit` found, in words |
| `reference.js` | uo-offline's converted data as shapes - bbox-loaded, read-only |
| `js/live.js` | The Live panel's line and snapshot age |
| `spawnxml.js` | Source-preserving reader and writer for XmlSpawner's spawn XML |
| `objects2.js` | The `<Objects2>` micro-format: what a spawner spawns |
| `spawners.js` | Spawn files to shapes and back - the XML counterpart to `project.js` |
| `fake-shard.js` | A stand-in `RequestPoller` for the tests: watches the request directory, answers acks |
| `*.test.js` | `node --test tools/*/*.test.js` - the whole of `tools/`, including the nav importer (the directory form fails on Node 22) |
| `../nav-import/` | Converts uo-offline's navigation data into `Data/Custom/reference/` |
| `modules.test.js` | That the browser code loads at all - see **Why `node --check` is not enough** |
| `js/`, `index.html`, `style.css` | The editor |
| `tiles/` | Rendered map, gitignored |
| `artrenderer.js` | The isometric renderer's child process, its queue and its cache paths |
| `js/iso.js` | The isometric projection - one formula, shared by the camera and every layer |
| `../MapExport/` | The tile renderers, radar and isometric |

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

## The uo-offline reference layer

`Data/Custom/reference/uo-offline-nav.trammel.json` is 3952 waypoints and 4291 edges converted
from uo-offline (see that directory's README). It is **authoring input, not shard data** — the
shard never loads it, and the Adopt step is the only thing that turns any of it into
`navigation.json`.

**Read-only by construction, not by convention.** `whitelist.resolveSave` serves the `WRITABLE`
table and the spawn files; `reference` is in neither, so there is no spelling of a save request
that reaches it. A test asserts that.

**Bbox-loaded, exactly as the stock spawners are**, and for the same arithmetic: 4364 shapes is a
grey smear that costs a megabyte to draw. One difference is worth knowing, because it was a bug
first: a null bbox means *omit* the stock spawners, but it would mean *the whole overworld* here,
so `refreshReference` drops what it has and asks for nothing below the zoom floor rather than
asking for everything.

The regions go on the wire too. Overworld is on by default; **dungeon (1988 waypoints) and Lost
Lands (43) are separate toggles**, off, because Adopt refuses both until those steps exist and
serialising them to have the browser drop them is the work the bbox is there to avoid.

Drawn dashed and at half opacity, labels on hover only. The one thing that must never happen is
mistaking somebody else's road for one of ours while editing, so the difference is visible without
reading the layer list. Every reference edge carries its length in `props.tiles`, because **56% of
them are longer than the hop cap** and that is the fact that decides whether adopting one is a copy
or a re-walk.

## The work-site overlay and the Site tool

A mine or a wood is **three records** — a destination to send bots to, a zone they may work
within, and the arrival tiles they stand on. Authoring them separately is how one gets forgotten,
and each omission fails differently: no zone and `GathererBehavior.ResolveSite` finds no work area
and walks the bot away; no arrivals and it has nowhere to stand. The **Site** tool collects all
three in one flow — click the centre, drag the zone, accept the tiles the shard offers, Enter to
finish — and writes them together or not at all.

**All three records carry their own layer.** That reads as housekeeping and is not: `fileOf` looks
a shape's file up by its layer, `filesWithEdits` drops a shape whose file comes back null, and a
record with no layer is therefore never drawn, never selectable, and **silently never saved while
Save still reports success**. The Site tool is the only tool building for three layers at once, it
inherited the single `LAYER_FOR[key]` lookup like every other tool, there is no `site` key in that
table, and a whole authored work site went to nothing because of it. `editor.test.js` now asserts
every built record names a real layer and that all of one build's records share one file.

**It asks for its fields first**, alone among the tools. The site type decides which harvest
definition the reach probe measures against, and the probe runs while the arrivals are going down,
several steps before a tool would normally ask anything. The first moment there are coordinates to
auto-generate an id from is the centre click, so that is where the form goes.

**The numbers come from the shard, never from here.** When the zone is drawn the editor drops a
`site-reach` token carrying the rect; `BotWorkSites.SweepZone` walks every tile in it with the
identical 5x5 sweep `BotHarvest.FindTarget` performs, against real map data, and the answer comes
back through `GET /api/reach`. A browser cannot answer this — it has no tiledata — and the last
time this shard guessed at harvestability from outside the engine it authored a mine on grass.

**The shard proposes; the author accepts.** It used to be the other way round: the author clicked
a tile, the shard said how much was in reach, and finding a good arrival on a cliff face meant
guessing one click at a time. Worse, the answer was drawn without its `canFit`, so the tiles with
the *highest* reach on the map — buried in rock, unstandable — drew green and were exactly what
the overlay recommended. Now only tiles that pass both tests are offered at all, the best four are
taken by default, and clicking a circle takes or drops it.

A rect costs nineteen bytes where the tiles inside it would cost thousands, which is the other
reason the zone form exists: `RequestPoller.MaxTokenBytes` is 4096 and a point list runs about ten
bytes a tile, so `brit-lumber-south` at 25x20 could not have been asked about as points. The sweep
is bounded at both ends — it warns above 1600 tiles and refuses above 4096 — and hands back the
best 24, ranked by reach with distance to the zone centre breaking ties. That tie-break is the
hop-cap trap in disguise: `brit-mine-north` once got an arrival 16 tiles from its nearest waypoint
against a 12-tile cap, and it stranded every miner sent to it.

Two fields, because they are different questions and a good tile needs both:

```
mine 1451,1517 z43  reach 14  canFit True    <- a face worth standing on
mine 1200,1200 z49  reach 25  canFit False   <- dense rock you cannot stand on
```

The overlay itself (`js/worksites.js`) is **not a shape layer**. Those records already exist in
`nav-destinations`, `nav-zones` and `nav-arrivals`, and a second copy would be two things to keep
in step and two things to click; it draws *over* them, toggled by a synthetic layer row exactly as
`coverage.js` is. Dots are green at or above the type's floor, amber below it, red at nothing at
all — three bands rather than a gradient, because the author's decision is three-way: fine, thin,
useless. **`canFit` beats reach outright**: a tile nothing can stand on is red however much ore
surrounds it. Probe points draw hollow, so a tile being considered never looks like one already
authored, and a candidate the author has taken draws filled — at that moment it *is* what the file
would say.

### Authoring a work site

The Site tool, in the editor. The other way to author a site is `[BotWorkScout` in game
(`Scripts/Custom/Bots/README.md`), which writes a proposal to `Data/Live/work-scout.json` for a
human to merge and deliberately never touches `navigation.json`. This is the one that writes.

The shard must be running: every number below is measured by it.

1. **Click `Work site`, then click the centre of the face.** The form opens straight away — alone
   among the tools, because the type decides which harvest definition the sweep measures against
   and the sweep runs several steps before a tool would normally ask anything. Set the type to
   `mine` or `lumber`; the id is auto-generated from the enclosing zone.
2. **Drag out the zone the bots may work inside.** This is where they wander and shuffle, not
   where they stand. It stays drawn for the rest of the flow.
3. **The shard sweeps it and offers the tiles worth standing on.** Every circle is standable and
   at or above the type's floor — 5 for a mine, 3 for a wood — and the number beside it is how
   many harvestable tiles are in reach from it. The best four are already taken.
4. Click a circle to take or drop it. Click bare ground to test a tile the sweep did not offer:
   it is measured the same way and either taken or refused with the reason, `cannot be stood on`
   or `reaches 2, needs 5`. The same tile can never be taken twice.
5. **Enter, or the `Finish` button.** Three records appear as pending edits.
6. **Save.** Nothing is written before this. Confirm the records landed in
   `Data/Custom/navigation.json` — a work site is the one create where a partial write is worse
   than no write.

If no circles appear, the status line says why. Usually the zone is on the wrong side of the
face; occasionally the floor is wrong for the ground, and `Custom.BotWorkSiteMinReachMine` /
`...Lumber` are the two keys that set it. Arrivals also have to sit within the hop cap of an
approach waypoint — see `Scripts/Custom/Core/Navigation/README.md` — which is a separate check the
audit makes and this tool does not.

### Authoring a road

The **Corridor** tool proposes waypoints and edges between two points; it creates nothing by
itself, and nothing is saved until you say so. The in-game equivalents are `[NavMark` /
`[NavRecord` / `[NavLink` (`Scripts/Custom/Core/Navigation/README.md`), which are the right reach
for a single waypoint rather than a road.

1. **Click `Corridor`, then click where the road starts.** Start it *on* an existing waypoint when
   you mean to join the graph there — a road that starts within a tile of one reuses it rather than
   creating a second waypoint on the same tile, and the status line names what it joined.
2. **Click via points to steer it, then the end.** Each leg is routed separately and joined, so a
   via point is how you make the road go round the mountain rather than at it.
3. **Enter, or the `Finish` button, routes it.** The status line counts the legs as they walk;
   every hop is verified against the engine's own `MovementPath`, which is why it is not instant.
4. **Drag the proposed waypoints until the road sits where you want it.** The hops derive from the
   waypoint positions, so dragging one moves the road rather than leaving a stale line behind.
   Right-click deletes a waypoint and relinks its neighbours, double-click inserts one, and
   shift-drag snaps. Each edited hop is re-verified through the `nav-hop` probe.
5. **Save**, then run the audit.

An over-cap hop is a **warning**, not a refusal — the shard accepts it and the walker may then be
unable to plan it, so it is worth fixing before it strands somebody. The audit and `validate.js`
both name the cap the hop broke.

**A road joined to nothing is the failure worth checking for.** The first road authored this way
put a duplicate waypoint on top of `brit-gate-w` instead of linking to it, and forty-three
waypoints and a mine hung off the graph as an island: every edge pathed, the audit was clean, and
nothing could walk there. Reusing an existing waypoint at the ends is what prevents it, and
`Nav.Data` now warns at load when any component other than the largest holds a destination or an
arrival, naming its waypoints. `[NavAudit` repeats the finding.

**An inserted point is named after its neighbours**, not after the zone it landed in — inserting
between `town-9` and `town-10` gives `town-10a`, even in the middle of the bank quarter, and it
carries the neighbour's display name if there is one. Only where neither neighbour is numbered does
it fall back to the zone scheme.

### Adopting a region

The **Adopt region** tool copies a box of uo-offline's roads into `navigation.json`, after the
shard has walked every edge in it. See `Data/Custom/reference/README.md` for what that data is and
`Scripts/Custom/Core/Navigation/README.md` for what Adopt refuses to do.

The shard must be running: every edge is re-walked with the engine's own pathfinder, and 56% of
them are longer than the hop cap and have to be subdivided.

1. **Turn on `uo-offline reference`** in the layer list and zoom in until the dashed roads appear.
   Below the zoom floor the layer draws nothing — 3952 waypoints at facet scale is a grey smear.
   Leave `...dungeons` and `...Lost Lands` off; Adopt refuses both until those steps exist.
2. **Click `Adopt region`, then drag a box** over the roads you want. The box is a question, not a
   record: nothing is created by drawing it.
3. **Watch the progress line.** It counts `walked N of M edge(s)` — each one is a real flood-fill,
   so a large box is minutes rather than seconds. That count is the only thing distinguishing
   working from hung.
4. **Read what came back.** The banner reports how many records were proposed, how many joins were
   made onto existing waypoints, and what was skipped and why. Three things need your eye:
   - **The two counts.** How many waypoints reach the graph you already have and will be written,
     and how many cannot and are **dropped**. A dropped record is not lost: it stays in the
     reference layer, dashed, and the next box that overlaps what you saved joins onto it.
   - **Red dashed edges** are roads the shard could not walk, or walked and the engine then refused
     (`flood ok, engine refused`). They are **not** proposed and will not be written. Hover one for
     the reason.
   - **Stranded waypoints** — any whose every edge failed — are listed and **dropped by Accept**.
     A waypoint with no road is the fault the west road shipped with.
   The banner also lists any pair of reference records that shared a tile and were folded into one,
   any record whose Z was moved to where the walker stood (the reference's Z is kept as `refZ`), and
   every **corridor** walked to a destination whose arrivals were all beyond the hop cap - with its
   length and the waypoint it starts from, and `REVIEW` past two hops so a far one gets a look
   before Save.
5. **Accept by saving.** The proposal is ordinary unsaved records, so `Save` writes them and
   `Discard` throws them away. Nothing reaches `navigation.json` before that. **Save writes the part
   that reaches the graph and drops the rest**; the one proposal it refuses outright is a box that
   reaches nothing at all (`NO JOIN WAS MADE`), because every record in that island is individually
   valid and the editor could not otherwise tell you it was one.
6. **Run `[NavAudit`.** It paths every walk edge, so it is the check that the adopted road is real
   rather than merely present.

**Adopt outward from Britain, one box at a time.** A box that touches nothing already saved is an
island: Save refuses it, and a bot could not walk to it. Each box should overlap ground you have
already accepted, so its edges can **join** onto records that exist — a join is an edge onto one of
our waypoints, which is additive and never changes the waypoint itself. Working outward from the
town in short hops means every box lands connected; a large box is saved only as far as it reaches,
and the rest waits, dashed, for the next box.

To send a bot along a newly adopted road and watch it arrive:

```
[BotSendTo <destination> [bot name]     # in game; target the bot, or name it
[BotInfo                                 # its behaviour, destination and leg progress
```

The editor's **Bots** panel shows the same thing live, with a per-bot event log.

### Editing a proposal

A proposal is ordinary waypoint and edge records, so dragging works because dragging works. Three
things are added on top, and the fourth is a bug fix that mattered more than any of them.

**The hops are derived, not drawn.** The first version drew the probe's walked path as a polyline,
so dragging a proposed waypoint moved the dot and left the road where the probe had walked - two
pictures of the same road, disagreeing. An edge IS two waypoint ids; the line between them is a
picture of that, and `syncDerived` now redraws every edge and route from where its waypoints are
NOW, on every frame of a drag. The tile-by-tile zigzag is gone entirely: a hop is a straight line
between two points, which is also what the bot will be asked to walk.

That staleness was not new and was not confined to proposals - every hand-placed edge in the graph
had it, and `app.js` even carried a comment about "every edge length that touches it" while only
invalidating the coverage overlay.

- **Right-click a point → Delete and relink.** Deleting a point out of a road otherwise leaves it
  in two pieces; this joins what it was between. Offered for any waypoint with exactly two hops,
  proposal or not. An end has one neighbour and nothing to join, so deleting it shortens the road,
  which is correct.
- **Double-click a hop → insert a point.** The search proposes a point every ten tiles whether or
  not the road bends there, so a bend usually needs one more exactly where it is. The gesture is on
  the hop because it needs a POSITION, and a menu would have thrown that away.
- **Shift-drag → snap to the nearest road.** Which tiles are road is a fact about the client's
  tiledata, so the shard answers it, using the same classification the search weights by rather
  than a second opinion that could disagree.

**Every edit re-verifies.** Any hop that moves is walked again by a real `BaseCreature` through
`nav-hop`, and drawn green or red. An edit that merely looked plausible is exactly what this tool
exists to stop somebody saving.

## Tiles

`export-tiles.ps1` renders a pyramid at `tiles/<facet>/<z>/<x>/<y>.png`, 256px, level 0 = the
whole facet in one tile, deepest = one pixel per game tile. Trammel is 599 tiles across levels
0–5 and takes about six seconds.

The level count must match `view.js maxZoomFor()` exactly or the editor requests tiles that were
never rendered; both ceil-halve until the facet fits one tile.

**A finer level is not an upscaling problem.** Below one pixel per game tile there is no more
radar data. The editor magnifies with nearest-neighbour, which is honest about what the data is —
and the genuine detail level, rendering art tiles instead of radar colours, is the art view below.

A second facet is another run, not a code change: `export-tiles.ps1 -Facet Felucca`.

## The art view

The real client art, drawn isometrically. **Base map → Art** in the sidebar.

```
tools\editor\export-tiles.ps1 -Prerender 1380,1495,365,345   # optional; warms Britain
node tools\editor\bridge.js
```

### There is no batch export, and there cannot be

Trammel's isometric canvas is `(7168 + 4096) × 22 = 247,808` pixels on each side — **61
gigapixels per floor**, about 937,000 tiles at 1:1, and roughly 70 GB and tens of hours across
three floors. So art is not exported. **A tile is rendered the first time somebody asks for it and
then kept forever.** Britain becomes art because you look at Britain; Trinsic becomes art by
panning there. `-Prerender` exists only so the first look at somewhere is a pan rather than a
short wait at every step; nothing depends on having run it.

```
browser ──GET /tiles/iso/…/z/x/y.png──▶ bridge ──"tile …" on stdin──▶ MapExport.exe --serve
            (radar drawn underneath)      │  hit: serve from disk        (one long-lived process,
                                          │  miss: render, then serve     warm art + TileMatrix)
                                          └──◀ "ok <ms> <bytes>" ─────────┘
```

**A miss blocks the HTTP request until the tile is drawn**, and that is the whole placeholder
protocol: there isn't one. Radar is drawn *under* the art layer always, so a tile that has not
arrived, one off the edge of the map, and one the renderer refused all look the same — radar shows
through. The browser's own six-connection cap is what throttles the queue, which suits a renderer
that has to be serial anyway (`Server.TileMatrix` keeps its block buffers in static fields).

The queue in `artrenderer.js` is **LIFO and deduped**. A pan asks for a screenful and abandons it a
moment later; answering the newest first means the tiles you are looking at now do not wait behind
the ones you have already left.

Measured on this machine, per tile: **1:1 about 13 ms, 1:2 about 9 ms, 1:4 about 21 ms, 1:8 about
260 ms** — so a screenful is well under a second at every level. `GET /api/artstats` carries the
counts and the last render time; the bridge logs each miss.

Prerendering the whole of Britain — `-Prerender 1380,1495,365,345`, 365 × 345 tiles — is **16,608
tiles in 3m26s and 873 MB**, at 12 ms each. Which is the argument for on demand rather than an
argument against art: that is one town out of a facet, and nobody pays it for the parts of the map
they never open.

### Which levels are art, and which have floors

| level | scale | world tiles per tile | floors |
| --- | --- | --- | --- |
| max (1:1) | 44px per game tile | ~12 × 12 | Ground / First floor / All |
| max−1 (1:2) | 22 | ~23 × 23 | Ground / First floor / All |
| max−2 (1:4) | 11 | ~46 × 46 | **All only** |
| max−3 (1:8) | 5.5 | ~93 × 93 | **All only** |
| below | — | — | **radar** |

Each step out quadruples the world area one render has to walk, and level 0 is the entire facet —
so art stops four levels down and the view falls back to radar, which is what radar is good at. A
coarse tile is rendered at 1:1 into a scratch buffer and box-downsampled, not sampled every Nth
column: a road one tile wide has to survive being zoomed out.

Floors stop two levels down for a different reason — at 1:4 a storey is five pixels tall and
peeling it off shows nothing. The slider stays visible there and goes inactive saying so, rather
than disappearing; a control that vanishes when you zoom out reads as a bug.

### The floor slider

Cut by **height above the ground**, never absolute Z: Britain's upper and lower town differ by
about thirty Z and one absolute threshold cannot suit both.

| stop | keeps | also |
| --- | --- | --- |
| Ground | statics below `groundZ + 20` | drops `TileFlag.Roof` |
| First floor | statics below `groundZ + 40` | drops `TileFlag.Roof` |
| All | everything | — |

#### What "the ground" is took two goes

The first version used the land tile in the static's **own column**, and it deleted the smithy's
back wall. That wall stands at `1415,1553`–`1415,1559` where the land is the river bank at **z −15**,
while the building's floor — which is *land* here, `0x040A wooden floor` — is at **z 30** one column
east. So the rule measured a ground-floor wall as forty-five above its ground and dropped it, at the
First stop as well as at Ground.

The ground is now **the highest land in the column's 3×3 neighbourhood that is not above the static
itself**. A wall sits on the *boundary* of the floor it belongs to, and at a cliff edge the column
under it is the drop; one tile is the smallest widening that lets a wall belong to its building, and
one tile is all it needs, because a wall is never further than that from its own floor. The cap at
"not above the static" stops a cliff *top* behind a building becoming the reference for something
standing at its foot.

**Land only, never a surface static**, and that is the part worth being careful about. The obvious
refinement — "the highest surface at or below this static" — collapses the whole idea: every item
stands on some floor, so every item would measure as storey zero and the slider would stop doing
anything at all. Land is the terrain and statics are the building; keeping that line is what makes a
storey countable.

**The client does not settle this.** Its own roof-hiding works off the *player's* Z rather than any
per-tile classification, so there is no client rule to copy — only the shard's habit of treating
land as the surface a mobile stands on (`Server/Map.cs` `GetAverageZ`). Said plainly rather than
dressed up as fidelity.

Measured with `--columns`, comparing both rules over every static in four boxes: **7 verdicts change
and all seven are the smithy's back wall.** The north outcrop (277 statics), the cemetery (328) and
the cave (395) are untouched.

UO's storey height is 20, which is where the numbers come from. Both are `--floor-ground` and
`--floor-first` on the renderer and are reported by `/api/artinfo`, because this is the rule most
likely to want an eyeball tune and a rebuild is a poor way to try a number. Roofs are dropped
outright below the top stop: the height cutoff alone would keep a one-storey roof at `groundZ + 20`
on the First floor stop, which is exactly the thing the slider is being moved to get rid of.

### Stretched terrain

A land tile is not a flat diamond. The client stretches it between the heights of its four corners
and paints a texture from `texmaps.mul` across the result; v1 of this renderer drew the flat 44×44
art at the tile's own Z, so a hillside came out as a staircase of diamonds **with white gaps
between them** — not merely terraced but holed, because diamonds at different Z do not tile.

```
stretched  iff  LandData.TextureID != 0
           and  Ultima.Textures.TestTexture(TextureID)
           and  the four corner Zs are not all equal
otherwise  the flat 44x44 art at the tile's own Z, exactly as before
```

**That rule is the client's and it is stated nowhere in this tree**, so it is cited rather than
inferred: ClassicUO's `Land.ApplyStretch`, which refuses to stretch when
`TexmapsLoader.GetValidRefEntry(TileData.TexID).Length <= 0`. Everything here points the other way —
`Server/TileData.cs:219` and `:259` *discard* the land texture id (`bin.ReadInt16(); // skip 2
bytes -- textureID`), `Ultima/Textures.cs` had no caller at all, and `Ultima/Map.cs` renders radar
colours and never opens a texmap.

**Both paths are load-bearing: only 4,085 of 16,384 land ids have a texture (24.9%).** A
stretched-only renderer would blank three land tiles in four. The texture id has to come from
`Ultima.TileData.LandTable[id & 0x3FFF].TextureID` — a deliberate exception to this tool's "map data
from Server, pixels from Ultima" rule, because a texture id is art metadata rather than map data.

**The four corners are the shard's own.** `Server/Map.cs:552-592` (`GetAverageZ`) samples `(x,y)`,
`(x+1,y)`, `(x,y+1)` and `(x+1,y+1)` — the lattice points around the tile — and every movement
decision on this shard is made from them. Reading the same four means a slope is drawn from the
numbers the walker walks, for the same reason the tool reads `Server.TileMatrix` rather than a
private `.mul` reader.

#### The invariant that makes it safe

A grid corner is the anchor formula applied to the corner's own cell, lifted one land tile:

```
isoCorner(cx, cy, cz) = ( (cx-cy)*22, (cx+cy)*22 - 4*cz - 44 )
```

**When the four corner Zs are equal, those four points are exactly the four vertices of the flat
44×44 art** — north `(ix, iy-44)`, east `(ix+22, iy-22)`, south `(ix, iy)`, west `(ix-22, iy-22)`.
So a level tile occupies the same pixels stretched or not, and this change can only move ground that
is genuinely sloped. `iso.test.js` asserts it, along with the fact that neighbouring tiles share
corner vertices exactly at integer pixels — which is why the quads tile watertight and the
rasteriser needs no seam filling.

The rasteriser is written from scratch; there was nothing in the repo to reuse. Two triangles,
barycentric, nearest sampling, and **inclusive edge tests** — a `>= 0` on all three coordinates
covers every shared edge from both sides, so some pixels are written twice (free: land is opaque and
first) and none are written zero times, which would be a one-pixel crack down every hillside.

#### How much of a hillside actually stretches

`--terrain-report x,y,w,h`, because "does this fix the slopes" deserves a number. Measured:

| site | box | sloped | stretched | terraced |
| --- | --- | --- | --- | --- |
| Britain Graveyard | `1333,1441,84,82` | 1,931 | **100%** | 0 |
| Behind LBCastle | `1480,1390,90,120` | 1,458 | **99.4%** | 9, all `water` (`0x00A8`) |
| `brit-mine-north` outcrop | `1438,1503,24,36` | 370 | **100%** | 0 |
| Cave entrance `1263,1251` | `1243,1231,40,40` | 1,421 | **100%** | 0 |

So the 75%-untextured figure does not bite in practice: the ids without texmaps are things like
animated water, which never slopes meaningfully. Warping the flat art onto a quad as a fallback —
the obvious next idea — has nothing to fix at these four sites, and the report is the tool for
checking that before assuming it elsewhere.

#### The cave is not a trench, and stretching was never going to make it one

`1263,1251` renders as a solid stretched mountain with a dark cave mouth against it. The passage
does not read as a trench, and the four tiles around it are **byte-identical at the All and Ground
stops** — because the floor slider filters *statics*, and a mountain is *land*. Land is always
drawn, at every stop, so there is no setting at which you can see into it.

Making a cave legible needs the cutoff to apply to land as well: drop land above the floor's height
and draw what is underneath. That is a different feature from the floor slider — it is a section
through the world rather than a storey of a building — and it is worth doing on its own terms rather
than bolting onto this one.

### World items

The renderer draws the world as it *shipped*. Everything `[Decorate` places — forges, anvils, signs,
doors, benches, bookcases — is a runtime item in the shard's own save, in none of the client files,
so the smithy yard at `brit-forge` rendered as empty paving with the nav markers floating on it.

```
[WorldItems                     # in game; or the `world-items` request token
```

`Scripts/Custom/Core/Bridge/WorldItemSnapshot.cs` writes `Data/Live/world-items.json`; the bridge
notices it and hands the path to the renderer. **The art view works without one** — the map layer is
the client's own files and needs nothing running — it just has no furniture, and the sidebar's art
line says `no world items - run [WorldItems` rather than leaving you to wonder.

#### What counts as a world item

```
item.Parent  == null      on the ground, not in a pack and not on a mobile
item.Map     == the facet and not Map.Internal
item.Movable == false     decoration, doors, signs, addon components, benches
item.Visible == true
item.Spawner == null      not spawner output
item.ItemID  > 1          an addon HOST is ItemID 1 and invisible
```

A shape, not a list of types — an allowlist is a thing to maintain, and anything the shard adds
later would be silently missing until somebody noticed a hole in a render. Measured: the world holds
**217,129 items**, 80,356 of which are on a facet at all, and **29,778** pass on Trammel — 1.37 MB of
JSON. In the Britain box that is 2,947 items, of which 2,830 draw; the 117 excluded are 90
`XmlSpawner`s and the invisible addon hosts, and exactly one item in the box is movable.

**Addons need no special handling.** `BaseAddon.AddComponent` calls
`c.MoveToWorld(new Point3D(X + x, Y + y, Z + z), Map)`, so every component is already an `Item` at
real world coordinates with its own `ItemID` and `Hue`. A plain walk sees them all.

There *is* a persisted decoration marker — `WeakEntityCollection` keyed `deco`/`door`/`sign`, 53,791
entries — and it is deliberately unused: it would miss the 143 items in the Britain box that belong
to no collection, including hand-placed ones.

**On an explicit request only, never a timer.** Finding items means walking `World.Items`, which
this shard reserves for explicit commands (CLAUDE.md §15). It does not need to be a timer either:
decoration does not move, so a snapshot is good until somebody decorates again.

#### Occlusion is exact, and that is why items are a full render

An item layer drawn on its own would put a forge on top of the wall in front of it — nothing in a
transparent overlay knows what is between the furniture and the viewer. So an item tile paints the
**whole** column, land and statics and items together in one sorted pass, records which pixels the
items ended up owning, and emits only those. A bench in the tavern is then behind the tavern's wall,
exactly as a static bench would be.

The cost is that an item tile that holds anything costs what a map tile costs. One that holds
nothing costs a dictionary lookup per column and is answered `empty` — **never written to disk**,
because tens of thousands of identical transparent PNGs is not a cache but litter. The bridge serves
a single 68-byte transparent pixel for those and remembers the key.

Items sort into the column by the same key the statics use and **the floor rules apply to them
unchanged** — a world item is a static as far as drawing goes, same art, same tiledata, same
`TileFlag.Roof`.

#### The item layer expires separately

```
tools/editor/tiles/iso/<Facet>/v<N>/map/<floor>/<level>/<x>/<y>.png
tools/editor/tiles/iso/<Facet>/v<N>/items-<snapshot id>/<floor>/<level>/<x>/<y>.png
```

The map layer is the client's world and never changes, so it is kept forever under the renderer
version. The item layer carries the **snapshot's own id**, so a re-decorate is a whole new set of
URLs — nothing to invalidate by hand, and the browser's cache turns over with it. Baking the two
together would have meant every `[Decorate` threw away a map cache that costs minutes to rebuild,
for furniture that costs seconds. Stale `items-*` trees are swept when a new snapshot loads.

### The cache key carries the renderer version

```
tools/editor/tiles/iso/<Facet>/v<N>/<floor>/<level>/<x>/<y>.png
```

`v<N>` is `TileServer.Version`, handed to the bridge in the handshake and to the editor by
`/api/artinfo`. Bumping it — a draw-order fix, a hue fix, a change to what a floor stop keeps —
orphans the whole old tree in one directory instead of leaving a cache that is half old and half
new. **v2 was stretched terrain and v3 is the ground rule above**, so `v1` and `v2` trees left on
disk are dead and can be deleted. `/tools/editor/tiles`
is already gitignored, which matters more here than for radar: **rendered client art is licensed
and must never be committed.**

### The projection, once

`js/iso.js` and `tools/MapExport/IsoTransform.cs` are two copies of one formula, taken from
`Ultima/Multis.cs:502-511` — the one working isometric renderer already in the tree:

```
anchor(x, y, z) = ((x - y) * 22, (x + y) * 22 - z * 4)
```

and any sprite hangs by its **bottom centre** from that anchor. Land art is 44×44, so a land tile
lands at `(ix - 22, iy - 44)`: the same rule, not a special case. `iso.test.js` pins the two copies
together by reading the C# constants out of the file, because a projection that exists twice and
drifts once puts every layer a couple of tiles off the art it is drawn on — which reads as bad nav
data rather than a bad projection.

Two things follow, and they are why this was a small change rather than a rewrite:

- **Twenty-two iso pixels per world tile, per axis.** So `view.scale` goes on meaning screen pixels
  per game tile in both projections, and every `labelAt` threshold, cull margin and hit-test slack
  keeps the meaning it already had. 1:1 art is scale 22.
- **At z=0 the projection is linear.** So the radar underlay is drawn by handing the matrix to
  `setTransform` and reusing the existing world-space tile loop — a radar tile becomes a rotated
  square and `drawImage` does the rest.

The camera stays in world space, so `centerOn`, `goTo`, `fitAll`, the Britain button and the
filter's jump-to-shape all work unchanged.

### Why placing and dragging are disabled in art view

**The inverse is not a function.** A screen pixel names a world tile only once you assume a Z, so
`isoToWorld` answers for the ground plane and is off by `z*4/44` tiles — about **2.7 tiles** where
Britain stands, near z 30. A waypoint dropped three tiles from where it was clicked would look
right and be wrong, which is worse than a refusal.

**Selecting still works**, because the editor drew every one of these shapes itself and drew them
at their own Z. So `hitTest` compares in *screen* space in art view: the projected anchor of each
candidate against the cursor's pixel. Points by anchor, polylines by projected segment, rects by
point-in-projected-diamond. No depth buffer needed, and no inverse.

Nudging with the arrow keys works too — it moves by whole tiles from the keyboard and never asks
where the cursor is.

The fix for the rest is a per-pixel pick map from the renderer, which is the next session's first
job.

### What the art does not show

- **World items, without a snapshot.** The renderer reads the client's files, so the shard's own
  furniture only appears once somebody has run `[WorldItems` — see **World items** below. Until
  then the smithy yard is empty paving, and the art line in the sidebar says so.
- **A cave passage as a trench.** See **Stretched terrain** — the floor slider filters statics,
  and a mountain is land, so there is no stop at which you can see into a cave mouth.
- **A zone rect at its true height.** Zones carry no Z in the schema, so they project on the ground
  plane and sit about 2.7 tiles off where Britain stands. Fixing it needs a land-Z lookup from the
  renderer, which is the same data the pick map needs.

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

## Why `node --check` is not enough

`js/app.js` once shipped with an unterminated string literal — a `.join('` with a real newline
inside single quotes where `'\n'` was meant. The editor loaded completely blank: no layers, no
health, `live: off`, an empty Create section. The bridge was fine and every endpoint answered 200;
the browser simply could not parse its entry module.

The whole test suite stayed green, for two reasons worth writing down.

**The tests never touched the browser code.** They `require()` the Node side — `bridge`, `project`,
`compact`, `whitelist`, `spawnxml` — and import a few pure helpers out of `js/`. Nothing loaded
`app.js`, which is the file with the canvas, the layer list and the Create section in it.

**And the obvious guard silently lies:**

```
node --check js/app.js        -> exits 0 on a file with a syntax error
node --check <same file>.mjs  -> exits 1 and points at the line
```

`--check` treats a `.js` file as CommonJS. When the CJS parse reaches a top-level `import` it falls
back to module detection, and that path does not surface the syntax error. A hand-rolled
"`node --check` every file" loop reports all clear — which is exactly what happened.

`modules.test.js` therefore does two things, because they catch different failures: it copies every
source to a temporary **`.mjs`** before checking it, which catches the syntax error; and it imports
the real module graph through a small DOM shim, which also catches a missing export (a link error,
which is not a syntax error) and anything that throws at module scope.

If a future change needs more of the DOM than the shim provides, widen the shim. The alternative is
going back to not knowing whether the editor loads.

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

## The Bots panel

Every live bot, what it is doing, and its recent history — fed by the same `entities.json` the map
draws, so a row and a dot are the same record and cannot disagree. Rows are coloured by
**behaviour** rather than by kind: a town of identically-coloured dots answers "where are they" and
nothing else, and cannot show that the miners never come back or that everybody is sitting at the
bank. `BEHAVIOR_COLORS` is checked against `BotBehaviors.cs` by a test, because a behaviour added on
the shard and forgotten here would draw as an ordinary bot and look deliberate.

The trailing slot shows the stuck rung when there is one, in preference to the behaviour name: a
wedged bot is the thing somebody opened this panel to find.

Selecting a bot shows its detail and its **event log**, read from `Data/Live/botlog.json` through
`GET /api/botlog` — fetched on the same poll as the entities, because the shard writes both on one
snapshot pass and two cadences could only ever show a bot's history from a different moment than
its position.

**This is the panel the Gatherer walk-in fault needed and did not have.** Three separate faults
shared one symptom — a bot standing still — and telling them apart meant reading a console that
logged almost none of it. A list where one bot reads *"walking in to The Northern Outcrop"* and
another reads *"standing outside a work site"* separates two of them at a glance.

## The Live panel

`live: 10 @ seq 147, 2s ago`, and the age is the point of it. A sequence number that has stopped
moving looks exactly like a quiet town until you know when it last moved, so past six seconds —
three missed writes at the two-second default — the line turns amber.

It ticks on its own one-second timer rather than only on a successful poll, because when the shard
stops answering `pollEntities` swallows the error and stops updating: an age computed only on
success would freeze at the last good value, which is the one number that must not.

## Spawners

Two layers, and the difference is what you may write to. `spawners` is `Spawns/Custom/<facet>/GG_*.xml`
- seven of them today, editable. `spawners-stock` is `Spawns/<facet>.xml` - 2,572 on Trammel alone,
read-only, and there to answer "what else is already spawning near the thing I am editing", which
the GG files cannot.

**Stock spawners load by viewport, not by facet.** `/api/spawners?bbox=x,y,w,h` filters on
`(Map, CentreX, CentreY)` rather than on filename, because `trammel.xml` contains 47 Felucca
spawners and `CentreX` reaches 7093 out in the Lost Lands. The response carries `total` too, so the
layer count can read "68 in view / 2,572 total" and the number never looks like the whole. The 4 MB
file is parsed once and cached against its mtime and size, so panning does not re-read it.

### Byte fidelity, again, for a format we did not design

`spawnxml.js` is to the spawn XML what `compact.js` is to the JSON, and separate for the same
reason: `compact.js` encodes one specific JSON layout rule, and XML has no such rule to encode.

The shard's writer is `DataSet.WriteXml` (`XmlSpawner2.cs:7566`) — no declaration, no BOM, no
schema, two-space indent, `True`/`False` capitalised, `<Elem />` for an empty string and no element
at all for an absent one, and `>` escaped as `&gt;` where a modern writer would leave it bare. So
nothing is reserialised: every `<Points>` block is kept as its raw source slice, along with the text
between blocks, and a field edit rewrites one element inside one slice.

**Line endings are measured, never assumed.** `Spawns/trammel.xml` is CRLF with no trailing newline;
`Spawns/Custom/trammel/GG_OldMarta.xml` is LF with one. Both are LF in git's object store —
`core.autocrlf` converts on checkout, and the custom files simply have not been checked out since
they were written. A writer that picked a convention would rewrite whole files it was asked to touch
one field of.

The test is not a fixture. It is every spawn file in the repo: 6,805 blocks across 20 files, ~11 MB,
byte-identical through parse and stringify.

### The corpus is stranger than the writer

Three things the real data does that the format does not:

- **Forty-seven blocks in `trammel.xml` have their `<UniqueId>` commented out** — `<!--guid-->`
  sitting exactly where the element belongs. `<UniqueId>` is what `[XmlLoad` replaces on, so those
  spawners are **duplicated on every import** rather than replaced. Preserved, and reported.
- **Sixteen blocks carry `<MinDelay>` twice and no `<MaxDelay>`**, a hand-edit where the second
  should have been the other one. `DataSet` schema inference turns a repeated element into a nested
  table, which may well mean every trammel spawner loads with default delays. Reported; whether it
  is actually true is a live question, not a settled one.
- **Two `<Objects2>` entries repeat a key and one stops three keys early.** Harmless — `GetParm`
  takes the first occurrence and absent keys default — and invisible until something looked.

None of these stop a file being read. `parse` reports them as findings rather than throwing, because
a reader that refused the file would just mean nobody could be shown the problem.

### The panel never sees `<Objects2>`

The bridge sends each spawner an `entries` list of `{type, max}` and rebuilds the string on save.
That is deliberate: the grammar below has no escaping and two of its rules fail silently, so a
second copy of it in the browser would be a second thing to get wrong. The panel adds, removes and
recounts entries; an entry whose type and count did not change keeps its original source text.

### `<Objects2>` has no escaping at all

`GGBaker:MX=1:SB=0:…`, entries joined by the literal `:OBJ=`. No quoting, no backslash, no encoding
— which makes two rules load-bearing, and both fail **silently** on the shard. A type name
containing `:MX=` makes the reader discard the whole entry (`XmlSpawner2.cs:12701`); one containing
any other key token is misparsed, because `GetParm` searches the entire entry. So a spawn list that
would vanish is refused before it is written, naming the token that would have done it.

`SpawnObject.Disabled` is not serialized to XML at all, so the editor cannot see or set it.

## Labels, measured

The thresholds are not guesses. At the default Britain view the old ones made **206 labels eligible
and 39 fit** — collision then culled 81% in a priority order no viewer can perceive, which is what
"the map is anonymous" actually described. Lowering thresholds makes that worse by adding
competitors; the fix is to let fewer things compete at each zoom.

| layer | was | now | why |
| --- | --- | --- | --- |
| nav zones, restricted | 0.10 | **0.08** | six of them, and they are the frame |
| destinations | 0.35 | **0.25** | 27 landmarks — the names worth reading at town scale |
| daily life | 0.50 | **hover/selection** | a marker sits *on* the destination it names |
| waypoints | 0.60 | **3.0** | 75 ids, interesting when you are editing the graph |
| arrivals | 1.20 | **6.0** | four and five deep around one destination |
| GG spawners | — | **1.0** | few, and named |
| stock spawners | — | **hover only** | 2,572 names is the thing being avoided |

At the default view that goes from 39 of 206 to **27 of 33**, and what survives is zones and
destination names rather than an arbitrary fifth of everything. A test pins it.

## Art tiles are the one route that is not a static file

`GET /tiles/iso/<facet>/v<n>/<floor>/<level>/<x>/<y>.png` is a real handler rather than
`serveStatic`, because a miss has to be rendered before it can be answered. Every segment is
checked against what it is allowed to *be* — three floor names and non-negative integers — and the
path is then rebuilt from those numbers, so there is nothing to traverse because there is nothing
to steer. Radar tiles keep falling through to `serveStatic`; they are exported ahead of time and
there is nothing to render.

## The request channel

The bridge drops `Data/Live/requests/<name>.token`; the shard's `RequestPoller` picks it up within
a second, runs the matching command path, deletes the token and writes `<name>.ack.json`.

| Request | Runs |
| --- | --- |
| `nav-reload` | `NavigationSystem.TryReload` |
| `dailylife-reload` | `DailyLifeCommands.TryReload` |
| `zones-reload` | `RestrictedZoneSystem.TryReload` |
| `spawn-reload` | `GGSpawnCommands.TryReloadFile`; the body is a file name relative to `Spawns/Custom` |
| `nav-audit` | `NavAudit.TryRun`, and writes the structured findings to `Data/Live/nav-audit.json` |
| `gg-reimport` | `GGSpawnCommands.TryReimport` |
| `livemap-on` / `livemap-off` | The entity snapshot; the body carries `<seconds> [custom|all] [zoneId]` |
| `nav-export-golden` | Writes the golden fixtures |
| `health` | Writes `health.json` now rather than waiting for the timer |
| `world-items` | `WorldItemSnapshot.TryWrite`; the body is an optional facet name. The art view's furniture |
| `save` | `Misc.AutoSave.Save()` — exactly what `[Save` runs, backup rotation included |
| `nav-hop` | Is this hop walkable, and where is the nearest road. Body `"verify x,y x,y ..."` (pairs) and/or `"snap x,y"`; answers to `Data/Live/nav-hop.json` |
| `site-reach` | Per-arrival harvest reach to `Data/Live/site-reach.json`. Body `"<mine\|lumber> x,y x,y …"` answers for tiles **not in `navigation.json` yet** |

**`save` exists because there is no other way to save from outside the game.** ServUO's console
takes no staff commands, and `HandleClosed` does *not* save on exit — it only waits for writes
already in flight. So a headless run had to sit out `Config/AutoSave.cfg`'s fifteen-minute timer
before it was safe to stop the shard and rebuild. It is dispatched inline: `Poll` is a `Timer`
callback, so it is already the game thread, which is where `World.Save` has to run; and `Poll`
refuses to dispatch anything while `World.Saving`, so it cannot re-enter a save in progress. No
editor button — it is an operator's tool, like `nav-export-golden`.

**`spawn-reload` reloads ONE file, and that is the point.** `[GG_Reimport` deletes every `GG_`
spawner in the world and re-imports the whole tree, and deleting an `XmlSpawner` deletes its spawned
mobiles - so using it per save would make editing Old Marta empty and refill the whole town. Instead
the token carries a file NAME relative to `Spawns/Custom`, never a path, resolved with
`Path.GetFullPath` against that root and refused if it lands outside, contains `..`, or is not an
existing `.xml`. The same check the whitelist here makes, enforced on both sides rather than trusted
from one.

It unloads from the **`.bak`** and loads the new file. `[XmlLoad` replaces by `<UniqueId>`, so
loading alone is an upsert and a spawner the editor deleted would be orphaned in the world forever;
the `.bak` the bridge writes before every save is precisely the old GUID set. No `.bak` means a
brand-new file with nothing to unload, and the ack says so rather than guessing.

**`[GG_Reimport` could wipe the world and report success, and now cannot.** `DeleteExisting()` ran
outside the try, and `XmlLoadFromStream` does not throw on a malformed file - it logs, sets a flag
and returns with zero spawners (`XmlSpawner2.cs:6141-6153`). So a bad XML file produced a cheerful
"deleted 7, imported 0" over an emptied world, and the catch below it caught nothing at all. Both
paths now read every file the way the loader will *before* anything is deleted, and compare the
imported count against the row count afterwards, since `XmlLoadFromFile` does not surface its own
per-row rejection counters.

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
