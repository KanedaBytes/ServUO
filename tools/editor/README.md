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
| `js/pickmap.js` | Which world tile the cursor is on - the renderer's per-pixel answer, decoded |
| `js/landz.js` | Where the ground is, for the rects that are drawn on it and carry no Z |
| `../MapExport/` | The tile renderers, radar and isometric |

## The left column

Every section is a `<details>`, and **its id is the key its open state is remembered under**
(`js/panels.js`, `localStorage`). The ids are therefore part of the interface: renaming one
silently resets that section for everybody who had already collapsed it.

A `MutationObserver` reopens a collapsed section when something inside it stops being `hidden`.
That is not decoration — several things here un-hide themselves without anybody clicking their
section: the bot detail block when a bot is picked on the map, the floor row when the base map
switches to art, the reapply button on the banner. Inside a collapsed section all of those appear
to do nothing at all, which is indistinguishable from the feature being broken. Taken from
uo-offline's `map.html:1487-1505`, which does both of these for the same reasons.

The column's width is dragged from the handle on its right edge and remembered too, clamped to
200px and 40% of the window. Its own element rather than a CSS `resize` on `#sidebar`, because
`resize: horizontal` needs `overflow: hidden` and the sidebar has to scroll. Double-click resets
it. Nothing tells the canvas: `render()` calls `view.resize()` every frame and the `ResizeObserver`
on the canvas asks for a frame whenever its box changes, which a flex sibling's width does.

Every storage read *and write* is guarded. A private window or a browser set to block site storage
makes both throw, and an unguarded write inside a `toggle` handler would take the handler with it —
so a section would stop collapsing rather than merely stop being remembered.

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

1. **Click `Corridor`. Name the road**, and set its tags — the form opens before the first click,
   because the name is what every waypoint the road mints is called. **Then click where the road
   starts.** Start it *on* an existing waypoint when you mean to join the graph there — a road that
   starts within a tile of one reuses it rather than creating a second waypoint on the same tile,
   and the status line names what it joined.
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

**The waypoints are named after the road**, not after the zone they landed in: `<name>-WP-0001`
upward in walk order, zero-padded to four so they sort as text the way they sort on the ground —
`WP-10` between `WP-1` and `WP-2` is what makes a road look shuffled in the filter. It is the one
place a tool departs from `js/ids.js`, and it departs deliberately: a road is a thing rather than a
scattering of points, and named this way it reads as one set in the filter, in a `navigation.json`
diff and in an audit finding, exactly as a work site's three records already do. Any case, digits,
`-`, `_` and `.` are all legal in an id (`NavIds.IsValid`), so the shard takes it as written.

Waypoints **joined** at the ends keep their own ids. Renaming a record the author did not ask to
touch would be worse than a mixed-looking road, and the join is what stops the road becoming an
island in the first place.

**The edges carry the corridor's tags**, which is the other half of the road being one thing.

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

### Rebasing a town onto their roads

There is a second mode, and it is the one that changed Britain. An ordinary adopt **refuses to
propose over ground we have authored**, which is right while their roads and ours are in different
places. **Rebase** turns that refusal off for waypoints and edges — and only those — for the case
where they mapped the same streets and mapped them better:

> **Wherever uo-offline has roads, theirs replace ours. Wherever they have none, ours stay
> authored.**

Destinations, arrivals, sites, zones and routes keep the refusal in full, so nothing carrying
authored work — a name, a tag, an `exclusive` or `exact` flag, a position placed by eye — can be
proposed over. Britain's 53 destinations, 115 arrivals, 8 zones and 4 routes came through the
rebase unchanged while 78 of its road waypoints were replaced.

A rebase proposal carries three things an ordinary one does not, and the banner reads them out:

- **Removals** — waypoints of *ours* the proposed road runs through, each with the record it folds
  into and how far it stood from the new road. **This is the only proposal that asks Save to delete
  anything**, so the distances are worth reading: a merge at one tile is the same piece of road
  under two names, a merge at the radius has moved where a route ends.
- **Rewrites** — the destinations, arrivals and routes that named a removed waypoint, with the list
  they will carry instead. Carried explicitly because a dangling reference is only a *warning* on
  this shard: without them the reload would succeed and quietly point a shop at nothing.
- **Relinks and withdrawals** — a removal takes its edges with it, so every surviving neighbour
  gets a walked edge onto the new road. A removal whose relink will not walk is **withdrawn** and
  our waypoint kept, because two roads over one piece of ground is untidy and visible while a
  stranded shop is neither.

Accept it the same way as any proposal — `Save`. Headlessly,
`node tools/editor/accept-adopt.js --write` posts the same creates, updates and deletes to the same
endpoint, after a dry run it refuses to skip.

### After a rebase: re-point the approaches

`node tools/editor/repoint-arrivals.js --tag britain [--write]`

**A destination's and an arrival's `waypoints` field decides the last hop**, and nothing keeps
either honest when the graph moves underneath them. `Nav.Data` warns only when an arrival is beyond
the hop cap of *every* waypoint — the stranding case — so a record naming something far away while a
waypoint sits six tiles off warns about nothing and reads as fine. Britain's rebase left 21 arrivals
over the cap from the waypoint they named and 49 naming something that was no longer nearest.

The tool re-points both kinds to the nearest **reachable** waypoint, keeps every listed one still
inside the cap (a shop fronting two streets keeps both approaches), and never touches a tile, a Z or
an `exclusive`/`exact`/`range` flag. Reachability is flooded from the home waypoint rather than
assumed: a nearer waypoint on an island trades a long last hop for no route at all. `--tag` scopes it
to a town, because a town is the unit somebody re-bases.

**The rule lives in `js/repoint.js` and the editor calls it too** — see *Dragging a destination or
an arrival* below. The script is the CLI around it: the flags, the scoping and the save. It used to
be the whole thing, which is why the editor never ran it, and why `HOP_CAP` sat here as a third
independent copy of the number with nothing pinning it to `Config/Custom.cfg`.

**Then check what a sweep cannot see.** `[WalkAudit` starts every walk at a waypoint, so it never
asks whether a bot standing at a *place* can route away from it. Flood the graph from the home
waypoint and cap-test every destination and arrival — that is the check that caught
`brit-shop-mage-east`, whose arrival sat exactly at the cap while its centre was fifteen tiles out,
and which failed live twice with `(start) -> uo-wp-5` after every other instrument read clean.

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

### Dragging a destination or an arrival re-points it

**Dragging a waypoint re-verified its hops; dragging a destination did nothing at all.** The drag
handler's commit branch was `if (shape.layer === 'nav')` — a waypoint — and everything else fell
past it, so a destination or an arrival moved across town kept naming whatever it named before.

`brit-home-jeweler` is what that cost. It was relocated to the east-side residential block and its
destination and **both** arrivals went on naming `uo-britain-bank`, **223 to 236 tiles back**, and
nothing said so: `Nav.Data` and `validate.js` both only ask whether *some* waypoint is within the
cap, and `uo-rec-7-1-s2` sat nine tiles from the new position and satisfied both. The shard booted
with zero nav warnings over a house no bot could reach. It matters because `Nav.TryResolve`
(`Nav.cs:262`) takes the **first listed waypoint that exists, with no distance test at all**, and
falls back to the nearest only when the list names nothing real.

So a dragged destination or arrival now re-points itself on drop, by `repoint-arrivals.js`'s own
rule in `js/repoint.js`: nearest reachable first, then every listed one still inside the cap. The
rewrite and the move go into **one** undo entry, so a single Ctrl-Z puts both back. Then it asks the
shard, with the `nav-hop` token the waypoint branch already uses, whether the engine will walk each
approach — which is the check that actually caught the jeweler, when both of its listed hops answered
`ok:false` and nothing else disagreed.

Two answers that are not a re-point, and both say so rather than guessing:

- **Stranded** — nothing reachable within the cap. The field is left **exactly** as it was, because
  a re-point onto something unreachable trades a long last hop for no route at all. This is a road
  somebody has to author.
- **The flood origin is missing** — `uo-britain-bank` is not in the graph. Every record on the facet
  would otherwise answer "nothing reachable", which is a sentence about the flood dressed up as a
  sentence about the data.

#### What the validator says about it, and why it is a note

`validate.js` gained the invariant that was actually missing: **every waypoint a record names must
be within the cap of it**, as opposed to the old question of whether any waypoint anywhere is. It
runs in `validatePreview()`, which fires on *every* edit, so it catches a properties-panel edit and
a paste as well as a drag.

It is a **note**, not a warning, and that is a measurement rather than a preference. On its first run
it found **23 shipped records in Trinsic** naming an approach 13 to 23 tiles off — `trinsic-forge`'s
arrivals among them at 20, 21 and 22. Every one was probed with `nav-hop`, before and after the
re-point the rule implies:

> **The engine walks 21 of the 23 as they stand, and not one of the proposed replacements is an
> improvement.** (The two that read worse are the adjacency artefact — `MovementPath` returns no
> path to a goal one tile away.)

The cap is calibrated conservatively against `FastAStarAlgorithm`'s 38×38 box, so an over-cap
approach is a real *risk* and not a real failure. A warning that fires 23 times on data that measures
clean is exactly how a panel teaches people to skim — which is the reason the third tier exists.
**Trinsic was deliberately not rewritten**: a whole-town `--tag trinsic` pass proposes 32 changes,
twenty of them to records already inside the cap, and commit `118be12f` records that nearest-first
can make the walked route worse (`brit-shop-tanner` went from 1 walked tile to 20 that way, with no
instrument reading wrong).

`trinsic-shop-tailor-2`'s destination is the one genuinely stranded record: nearest reachable is
`uo-wp-173` at 14 tiles, so no re-point can help it and it is left listed.

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

Everything the radar view can do, the art view does — on the art, at the tile the cursor is on.
Placing, dragging, the create tools, zone rects, the readout: all of it goes through the renderer's
own **pick map**, below, because the isometric projection has no inverse and guessing one put things
about 2.7 tiles from where they were clicked.

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

### The anchor is not the centre

The formula above gives the **sprite anchor** — where a sprite hangs by its bottom centre. For a
44×44 land tile that is the diamond's *south vertex*, so it is **22 pixels below the middle of the
tile it belongs to**. Confusing the two is a 22-pixel error; confusing `worldToIso(x + 0.5, y + 0.5)`
with the middle is a 44-pixel one, and 44 pixels along `(x + y)` is one whole tile in x *and* one in
y at once.

That was the bug, and it was invisible from inside the editor: a waypoint sat neatly in the middle
of *a* tile, just not the one it named. It took standing in the client on the placed tile and
reading the coordinates back — editor `1425,1555`, client `1426,1556`, three times over.

**The client settles it.** ClassicUO, camera removed, with `A(x,y,z) = ((x-y)*22, (x+y)*22 - z*4)`:

| ClassicUO | | lands at |
| --- | --- | --- |
| `GameObject.UpdateRealScreenPosition` | `RSP = ((X-Y)*22 - 22, (X+Y)*22 - (Z<<2) - 22)` | `A - (22, 22)` |
| `LandView.Draw` | `batcher.Draw(art, new Vector2(posX, posY), …, Vector2.Zero, …)` — origin zero, so RSP is the 44×44 art's **top-left** | land diamond centre at **`A`** |
| `View.DrawStaticAnimated` | `index.Width = (UV.Width >> 1) - 22; x -= index.Width;`<br>`index.Height = UV.Height - 44; y -= index.Height;` | static's **bottom centre** at `RSP + (22, 44)` = **`A + (0, 22)`** |
| `MobileView.Draw` | `posY -= 3; drawX += 22; drawY += 22;` then `y -= UV.Height + Center.Y` | mobile's **feet** at `A - (0, 3)` — the land centre |

So in the client: **a mobile's feet are on the centre of its tile's land diamond, and a static's
base sits 22 pixels below that.**

**Our renderer matches it, and no pixel had to move.** `Blit` hangs every sprite by its bottom
centre on `A`, so land (44×44) has its centre at `A - 22` and a static's base at `A` — the *same*
22-pixel gap. The whole picture sits 22 pixels higher than ClassicUO's, which is camera placement
and unobservable. The invariant that pins it: a 44×44 **floor static tiles exactly with land** in
both, which it must, or every wooden floor in Britain would sit half a tile off its own ground.

So the renderer was never wrong. What was wrong was `view.toScreen`, which projected the anchor:

```
toScreen(x, y, z)   was  iso.worldToIso   the sprite anchor      A
                    now  iso.isoCorner    the world lattice      A - (0, 22)
```

**One meaning for a coordinate, in both projections.** `toScreen(x, y)` is the lattice point (x, y)
— the corner shared by four tiles — and tile (x, y) spans `(x, y)` to `(x+1, y+1)`, which is the
same four points `Server/Map.cs:552-592` samples for `GetAverageZ`. So `+ 0.5` centres a marker in
its tile in radar *and* in art, and a rect's corners are its corners in both. Every caller in the
editor already passed either a lattice corner or a `+ 0.5` centre; not one wanted the anchor, which
is why the fix is one line and why the bug was perfectly uniform.

`iso.tileCentre(x, y, z)` names the common case, and `iso.isoToCorner` is the inverse `view.toWorld`
needs — `isoToWorld` inverts the *anchor* and is 22 pixels out for anything in lattice space.

`iso.test.js` pins it against the thing the renderer actually draws: **a tile's centre is the
centroid of the four corners of `landQuad(x, y, …)`**. That assertion needs no renderer, no
screenshot and no client, and it is the one that would have caught this.

### The pick map, and why the inverse stopped mattering

**The isometric inverse is not a function.** A screen pixel names a world tile only once you assume
a Z, so `isoToWorld` answers for the ground plane and is off by `z*4/44` tiles — about **2.7 tiles**
where Britain stands, near z 30. That is why placing and dragging were refused here for two
sessions: a waypoint dropped three tiles from where it was clicked would look right and be wrong.

The renderer does not have to invert anything. **It knew the answer while it was drawing**, so it
writes it down: beside each 1:1 tile, a `.pick` sidecar recording, per pixel, which world tile it
belongs to and the Z a mobile would stand at there.

```
tools/editor/tiles/iso/<Facet>/v<N>/<map|items-<id>>/<floor>/<maxLevel>/<x>/<y>.pick
```

Written by the **same painter's pass and the same floor filter as the picture**, at the same two
pixel-write sites (`IsoTileRenderer.Blit` and `.Triangle`, beside the existing `_owner` plane). So a
click lands on the tile the eye sees. Measured at the smithy:

| tile | stop | picked |
| --- | --- | --- |
| the yard paving `1424,1557` | any | `1424,1557 z30 land` |
| the anvil `1423,1556` | any, snapshot loaded | `1423,1556 z30 item` |
| inside the smithy `1418,1547` | **All** | `1421,1550 z56 static` — the roof |
| inside the smithy `1418,1547` | **Ground** | `1418,1547 z30 land` — the floor under it |

Those Zs are `navigation.json`'s own, which `[NavAudit` verified against real map data, and
`brit-plaza-1` reads back `z20` the same way.

#### One resolution answers every zoom

Pick maps exist **only at the deepest level**. The lookup goes through a *facet-global canvas
pixel* — `iso.canvasToTile`, the inverse half of `tileFor` — and a canvas pixel is the same number
whatever the camera is doing. So the 1:1 sidecar answers a click at 1:2 and at 1:8 exactly as well.

That is not a shortcut, it is the only honest option: `Halve` box-averages colour, and world
coordinates cannot be averaged. Any rule for picking one of a 2×2 is a lie about the other three.

It also means there is **no zoom gate**. The pick is exact for the pixel clicked at any art level;
what gets coarse as you zoom out is aiming, not correctness, and the readout showing the picked
`x, y, z` at every zoom is what makes coarse aiming visible rather than guessed.

#### Binary, not a PNG

Getting **exact** bytes back out of a PNG in a browser means a canvas round trip, and that path
premultiplies alpha and applies colour management. A pick map that is nearly right is a waypoint
three tiles out. And an RGBA8 pixel could not hold it anyway: world x is 13 bits, y is 12, Z is 8 —
33 bits, one over.

```
 0  "GGPK"  magic          16  size*size  kind   0 none, 1 land, 2 static, 3 world item
 4  u8      format             size*size  dx     worldX - baseX
 6  u16     size               size*size  dy     worldY - baseY
 8  i32,i32 baseX, baseY       size*size  z      standing Z + 128
```

**Planar, not interleaved**, because each plane is piecewise constant over a drawn sprite — one land
diamond is one value in all four — and deflate finds those runs only when they are adjacent. The
whole file is gzipped and served with `Content-Encoding: gzip`, so the browser inflates it natively
and `fetch().arrayBuffer()` hands over the bytes with no decoder written on either side.

Measured over 24 fresh tiles: **262,160 bytes raw, about 7.8 KB on the wire, 13 ms each** — against
roughly 100 KB for the PNG beside it. Z fits a byte exactly because UO's Z is an sbyte; `dx`/`dy` fit
because a tile spans about sixty world tiles per axis at worst, and the writer **refuses** rather
than wrapping if that is ever untrue.

#### Lazy, and one tile at a time

The sidecar has its own request and is rendered on demand. 1,284 art tiles and 82 MB were already
cached under `v3`, and both `Prerender` and `ArtRenderer.tile` short-circuit on the PNG existing — so
emitting it eagerly would have meant bumping the renderer version and throwing all of that away, for
a file that changes no pixel. A pick map is only wanted for tiles somebody puts the cursor on, and a
256-pixel sidecar covers about twelve world tiles at 1:1, so crossing into a new one is rare and
costs 13 ms once, ever.

Neighbours are deliberately **not** prefetched: the renderer is serial and its queue is LIFO, so
eight speculative tiles would sit in front of the one the cursor is actually on.

A press whose pick has not arrived **waits** for it rather than falling back to the guess. It is
almost never reached, because the mousemove that positioned the cursor already asked.

#### What is in the Z

`map.GetAverageZ` for land — the shard's own four-corner rule, *called* rather than copied, through
one accessor (`IsoTileRenderer.StandingZ`) that the `landz` query below shares. For a static or a
world item it is `Z + CalcHeight` when the thing is a surface or a bridge, and its own `Z`
otherwise: a wall answers its foot, which is the only honest answer for something nothing stands on.

**One Z for the whole land tile**, not the corner Z interpolated across the quad — which the
stretched rasteriser could hand over for nothing. The picture is stretched; the record is not. A
waypoint names a tile, so both ends of the same square have to give the same number.

#### Verifying it

```
tools\MapExport\bin\Release\MapExport.exe --pick 1424,1557 --items Data\Live\world-items.json
```

renders the pick tile covering a world tile at each floor stop and prints the decoded `x y z kind`
at that tile's centre. **This tool has no test project**, so that mode is how the C# half is checked;
the JavaScript half is `pickmap.test.js` (the format, from fixtures built byte by byte against the
spec) and one end-to-end test in `bridge.test.js` that runs the whole chain — `tileFor`, the URL, the
render, the gzip, `pickmap.decode` — and asserts it lands on the tile it aimed at.

### Zones sit on the ground

A zone is a **rectangle of ground** and carries no Z in the schema; none has been added. Where the
land is under each corner is a question with an answer, so it is asked:

```
landz <x>,<y> <x>,<y> ...   ->   ok <z> <z> ...      on the renderer's own stdin channel
GET /api/landz?tiles=x,y;x,y;...
```

answering the **same** `StandingZ` the pick map records, so a zone corner and a waypoint on the same
tile cannot disagree. `js/landz.js` caches by tile and coalesces every request made before the next
animation frame into one query, so a draw pass over eight zones is one round trip.

The drawn quad, its selection handles and `hitTest`'s point-in-diamond all take the same corner Zs —
which matters more than it looks: they were all on the ground plane before, so the diamond you
clicked was not the diamond you saw.

Two callers deliberately keep the ground plane: the **stock region overlay** (hundreds of rectangles
across a facet) and the **coverage grid** (thousands of cells). Sampling those would be a facet's
worth of queries for two diagnostic overlays.

**Radar placement samples it too**, which is the one step this took outside the art view. Every
record the editor has ever created was written `z: 0`, because `build.js` flattened it — survivable
only because `navigation.json`'s Z is advisory and the shard falls back to `map.GetAverageZ` when
the authored one will not fit. With no renderer it still places, at `z: 0`, and the readout says
`z?` rather than letting a silent zero look like a measurement.

### What the art does not show

- **World items, without a snapshot.** The renderer reads the client's files, so the shard's own
  furniture only appears once somebody has run `[WorldItems` — see **World items** below. Until
  then the smithy yard is empty paving, and the art line in the sidebar says so.
- **A cave passage as a trench.** See **Stretched terrain** — the floor slider filters statics,
  and a mountain is land, so there is no stop at which you can see into a cave mouth.
- **A live bot's route at its own height.** The dot is drawn at the bot's Z and is clickable there;
  the trail behind it is a flat `[x, y, …]` list in the snapshot with no Z per step, and adding one
  would triple the payload. So a bot walks above its own route on a hillside.

**Clicking a bot** opens its inspector, and the rule is that **a bot beats a line or an area and
loses to a point or a drag handle** (`shapes.grabsOverEntity`). Entities are drawn last, over
everything, so picking what is visually on top is the least surprising rule — but a waypoint under a
wandering bot has to stay draggable, or a bot makes the map read-only wherever it goes. The first
version checked for a bot only when *nothing else* was hit, which sounded conservative and made bots
unclickable in the one place there are any: `brit-town` is a 324×279 zone covering the whole of
Britain, so every click inside it hit the zone's body first. Measured over 120 positions along real
roads: **six reachable before, 119 after.**

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

**Every tool declares its steps**, and the step counter reads them. It used to be
`tool.kind === 'site' ? 3 : 0` in `app.js`, so the Site tool was the only one with any step
guidance at all. The wording tracks the walkthroughs above, so a tool that changes changes in one
place and the two cannot end up saying different things.

Two tools ask their form before the geometry, for different reasons. **Corridor asks at the very
start**, because its name is what every waypoint the road mints is called and it has to exist
before there is anything to name. **Work site asks after the first click**, because its auto id is
generated from the zone the centre tile lands in, and the type it collects decides which harvest
definition the sweep two steps later measures against.

## No form carries a list of shard values

A field whose valid values are a finite set is populated from real data. Free text survives only
where the value is genuinely free — a display name, a corridor's name, a restricted zone's name.

Before this, half of them were free text with an example typed into the table: `value: 'shop'` for
a destination type, `'mine or lumber'` in a label, `'town road'` for tags. Each of those is a copy
of the shard's vocabulary that nothing checks and nothing updates.

**uo-offline has the same fault, more visibly.** Its spawn types are a checked-in
`spawn_types.json` regenerated by hand from a Python source-scanner
(`map-editor/gen_spawn_types.py`), and its bot behaviours are a literal array in the page
(`map-editor/map.html:204`). Both drift, and neither says when.

### Who answers what

| | |
| --- | --- |
| **the shard** | what it actually **loaded** — every concrete `BaseCreature` with a public parameterless constructor, and which of them are `BaseVendor`; plus the C# enums (`BotClass`, `BotSkillTier`, `NavEdgeKind`, `NavRouteMode`, the gatherer site types). `VocabularySnapshot.cs` → `Data/Live/vocabulary.json`, on the `vocabulary` request |
| **the bridge** | what is **written down** — the destination types and tags in use in `navigation.json`, the ones `bots.json` weights, the spawn files on disk; and the one thing the shard cannot answer, which source folder a creature type came from. `vocabulary.js` → `GET /api/vocabulary` |

That last split is the interesting one. The spawner form wants Monster / NPC / Vendor, and **ServUO
exposes no source folder at runtime** — `Scripts/Mobiles/Normal` holds `Alligator` next to
`Balron`, so no property of a loaded `Type` separates a townsfolk from a dragon without
instantiating it, and instantiating 1,400 mobiles to fill a dropdown is not a trade worth making.
The folder *is* the answer and the folder is a fact about the repo. So the shard says what exists,
the bridge says where it was written, and neither of them is a file somebody re-runs. The vendor
question comes from the type chain and the folder scan gets no say in it, because `BaseVendor` is
exact and a folder is a guess.

The scan reads file **contents**, not names: `AlelleTheAborist.cs` declares `Alelle`, and several
ServUO files declare more than one class. It is cached against the tree's file count and newest
mtime, the same discipline `readStockFile` uses for the 4 MB `trammel.xml`.

A type the shard loaded that the scan never saw falls to Monster and is **counted** — and it is not
a small number (226 of 1,223 on this tree), because creature subclasses live under
`Scripts/Engines`, `Scripts/Services` and `Scripts/Custom` too.

### Select, or combo

A `<select>` only where the shard has a **closed set**: route mode, edge kind, spawner kind, bot
class, bot tier. Those are C# enums and a value outside them is a file the shard refuses to load.

Everywhere the shard accepts a free token — destination `type`, every tag field, the site type — a
typeable combo. `NavRecords.ValidateDestinations` says why in as many words: *"uo-offline-server's
flat enum conflated category and mechanism; ours is a free token deliberately."* A control stricter
than the file format would refuse a type the shard would have accepted. uo-offline reached the same
answer for the same reason — `map.html:126` is an `<input list>`, not a `<select>`.

The Selection panel uses the same vocabulary, from one table in `js/vocab.js` keyed on
`<layer>.<field>`. A dropdown when you create a destination and free text when you edit one is
exactly the drift this removes: the second form quietly teaches you that the vocabulary is optional.
Two entries in that table — an edge's `kind` and `tags` — are prepared rather than in use, because
an edge exposes no editable fields yet.

### With the shard down it still answers

Everything derived from the files is there, the shard's lists are empty rather than absent, and
`shardSeen` is false so the editor can say which half is missing. The spawner's kind dropdown reads
`Monster (0)` — an *absent* kind would look like a form that had not finished loading, and a count
of zero does not. The art view already works with the shard down; a create form that refused to
open without it would be a regression.

The bridge asks the shard for a fresh list when `vocabulary.json` is older than `Scripts.dll`, and
not on a timer: the answer only changes when the assembly does, which means a rebuild, which means
a restart.

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

## The Problems panel

Everything flagged, in one clickable list. It exists because **four systems already knew something
was wrong with a record and three of them could only say so as a mark on a dot** somewhere on a
6144-tile facet:

| source | what it could say before |
| --- | --- |
| `[NavAudit`'s unstandable arrivals | a `z?` badge on a marker, and a line in a banner capped at twelve |
| `[NavAudit`'s blocked / far / occupied edges | the same banner, same cap |
| the `z?` and `!` badges (`shapes.js`) | the marker, and nowhere else |

**The `z?` badge is the shard's answer, and used not to be.** It compared a record's stored Z
against the pick map's `StandingZ` - one line of `map.GetAverageZ`, which sees land and nothing
else - and flagged anything more than a storey off. That is every record standing on something
other than bare ground: `town-2` at z 28 over a riverbed at -15, `uo-wp-79` on the Trinsic bridge
deck, stock `trammel.xml` spawners on second storeys. The editor read **13** while `[NavAudit` read
**0**, and not one of the 13 was actionable.

There is one rule now and it is `NavWalker.TryResolveZ`'s: a record is stale when a mobile carrying
its stored Z as a hint would stand somewhere else, so a bridge deck or a floor static **at** the
stored Z counts as exactly right. `[NavAudit` exports the list as `staleZRecords` beside the count
it already wrote, `shapes.setStaleZFlags` consumes it the same way `setUnstandableFlags` consumes
the unstandable arrivals, and **a nav save re-runs the audit quietly** so the badge is never more
than one save behind. The Problems panel's `stale-z` count therefore equals the shard's by
construction rather than by coincidence.

Read-only layers are never badged at all - stock spawners, live entities and the uo-offline
reference are somebody else's content, and a flag on one is a fault nobody in this editor can fix.
`landz` still answers the properties row's `z? 28 (ground -15)` and the cursor readout, because
that number is worth *seeing* on the record you have selected. It just does not decide.
| the replicated shard validator | runs on **every** edit and produces `{severity, where, message, shapeId}` per finding - of which exactly one, `fatal[0]`, reached the status line |

That last row is the one worth noticing: `validatePreview()` was computing a full report on every
edit and discarding all of it but one string, in the same expression. A stranded destination, a
`pending-road` note and a duplicate id were all being found and thrown away.

Rows carry the shard's own words. `[NavAudit`'s unstandable entries now ship a `reason` built by
`NavResampleZ.Stranded.ToString()`, which names **the blocking static's id and name**, the nearest
standable tile within two, and - where there is one - a standable height on the same tile:

```
arrival 'brit-bank' 1432,1692 z 0 (land 0, solid - nothing fits here at all)
  - blocked by 0x0034 'brick wall'; nearest standable 1431,1691,0 (1 tile away)
```

**The formatting is a module, `js/problems.js`, for the reason `js/audit.js` gives in its own
header**: the line it replaced lived inside a click handler, was wrong for a year, and nothing
could have caught it. `problems.test.js` tests the rows directly and reads `NavAudit.cs` back to
fail if the shard stops emitting `reason` - which would silently downgrade every row to a
generated sentence.

Two things about the list that are deliberate:

- **A `note` is not a `warning`.** `validate.js`'s third tier exists because a warning that is
  expected teaches everybody to skim, and the moment the list is skimmed the real warning
  underneath it is invisible. A `pending-road` gap is listed, counted, and coloured differently.
- **Deduping is by kind and tile, not by label.** The audit knows an unstandable arrival by its
  destination's id and the badge knows it by the record's, so keying on the name would list one
  fault twice under two names. The audit's row wins, because it is the one carrying the blocker.

Clicking a row selects the record and centres both views. A finding with no single place - an edge
has two ends - selects its record if it names one and otherwise does nothing, rather than jumping
somewhere arbitrary.

## Jump to a coordinate

A box in the **View** section: type `1475,1641` and press Enter. It accepts a comma, a space or
both, because all three are what a coordinate looks like in the places you copy one from - a
console warning, a walk-failure line, this editor's own readout.

There is one camera behind both projections (`js/view.js`), so this centres the radar and the art
view together and there is nothing to keep in sync. The toolbar's `Go to...` modal and the
Problems panel call the same `jumpTo(x, y)`, so "go to a coordinate" cannot come to mean two
slightly different things. `clampCenter()` already refuses anything off the facet, which is why
nothing range-checks.

## The Bots panel

Every live bot, what it is doing, and its recent history — fed by the same `entities.json` the map
draws, so a row and a dot are the same record and cannot disagree. Rows are coloured by
**behaviour** rather than by kind: a town of identically-coloured dots answers "where are they" and
nothing else, and cannot show that the miners never come back or that everybody is sitting at the
bank. `BEHAVIOR_COLORS` is checked against `BotBehaviors.cs` by a test, because a behaviour added on
the shard and forgotten here would draw as an ordinary bot and look deliberate.

The trailing slot shows the stuck rung when there is one, in preference to the behaviour name: a
wedged bot is the thing somebody opened this panel to find.

> **And it never once did.** `updateCounts` swept `document.querySelectorAll('.count')` to fill in
> the layer-row totals, and the Bots panel's trailing slot is a span of class `count` too. Those
> carry no `data-layer`, so they fell through to the last branch and were set to the number of
> shapes in layer `undefined` — zero. Every bot row read `0` where its behaviour or its stuck rung
> should be, for as long as the panel has existed. The sweep is now scoped to `#layers`, and a test
> asserts the selector rather than the rendered panel: ordering the two updates would also make it
> look right, and the next thing added to the sidebar with a count would break it again.

Selecting a bot shows its detail and its **event log**, read from `Data/Live/botlog.json` through
`GET /api/botlog` — fetched on the same poll as the entities, because the shard writes both on one
snapshot pass and two cadences could only ever show a bot's history from a different moment than
its position.

### The bot card

Clicking a bot — a row here, or its dot on the map in **either view** — also opens a floating card
over the map: the same detail, the same event log, closable and draggable by its header, with its
position remembered.

It exists because the panel's detail block can be a whole column's scroll away from the dot that
was clicked, and on the art view that dot is usually the only reason anybody is looking at that
part of the map. The panel selection still updates exactly as before — `selectBot` is one function
and both routes go through it, so the row highlights and scrolls itself into view either way.

**One function fills both.** `renderBotDetail` was split into `fillBotDetail(element, bot)` and is
called twice. Two views of one record that could each build their own DOM would be free to
disagree, which is the fault this panel was built to prevent between a row and a dot.

Esc is deliberately not bound to it. Esc already deselects and cancels a tool, and the card is open
for most of the time anybody is watching bots — taking the key for it would make Esc mean the more
useful thing only when the card happened to be shut.

**This is the panel the Gatherer walk-in fault needed and did not have.** Three separate faults
shared one symptom — a bot standing still — and telling them apart meant reading a console that
logged almost none of it. A list where one bot reads *"walking in to The Northern Outcrop"* and
another reads *"standing outside a work site"* separates two of them at a glance.

### The full `[BotInfo` report, selectable

The card ends in a `[BotInfo` block - the same forty lines the in-game command shows, in a
monospace `<pre>` you can select and copy. Class, tier, home, behaviour and status line, the leg it
is on, stats against the caps in force, notoriety, every non-zero skill in an aligned column,
personality and the phase clock.

**Pulled, not pushed.** `entities.json` carries sixty bots on every poll; forty lines of skill
table each would be a hundred kilobytes twice a second to answer a question about one of them. So
the block is collapsed until opened, and opening it drops a `botinfo` request token naming the
bot's serial. The shard writes `Data/Live/botinfo.json` and the card reads it back through
`GET /api/botinfo`.

Three details:

- **The serial is echoed back and checked.** A bot is ephemeral, and showing the previous bot's
  report under this one's name is the one failure a cached panel has that the command does not.
- **`BotCommands.Describe(bot)` builds the lines, and the command calls it too**, so the two
  renderings cannot drift - the same rule `fillBotDetail` follows for the row and the card.
- **The shard being down is not a gate.** The block says why it could not read it and everything
  else in the card is unchanged, the way `sendReach` already behaves.

`bridge.test.js` asserts that every `api.request('...')` in `app.js` has a matching `case "...":`
in `RequestPoller.cs`, so the browser half cannot ship without the shard half.

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

### The create form, and the save that never ran

The New GG spawner form is uo-offline's (`map-editor/map.html:118-133`, JS at `:1101-1141`): the
**kind** first, then a **type** list filtered by it, then count, home range and the respawn window,
with a line under each field saying what it decides. Kind first because the type list is meaningless
without it — 1,400 creature names in one combo is not a list anybody reads, and filtered to a kind
it is something you can scan. The type control stays typeable for the same reason uo-offline's is.

The counts on the kind labels — `Monster (818)`, `NPC (47)`, `Vendor (405)` — are ours rather than
theirs, and they are there because the split is derived rather than stored: a scan that found
nothing gives an empty type list, and an empty dropdown is indistinguishable from one still loading.

**`GG_` is applied, not typed.** `[XmlLoad` and `[XmlUnLoad` filter on it with an ordinal
`StartsWith`, so a spawner missing the prefix is invisible to `[GG_Reimport` for ever — never swept
and never replaced. A prefix everybody has to remember is a prefix somebody will forget. It is
idempotent, so typing it out of habit does not give `GG_GG_`.

> **And none of this could have been saved, because no spawner ever could.** `filesWithEdits`
> returned `['navigation', 'restrictedZones', 'dailyLife'].filter(...)` — a fixed list — and a spawn
> key is `spawn:<facet>/GG_Thing.xml`, dynamic by construction. So no spawner edit has ever survived
> that filter: `save()` iterates what it returns, an empty list runs zero iterations, and the run
> falls straight through to *"Saved and reloaded."* Editing a GG spawner's fields in the panel and
> creating one with the Spawner tool were both silent no-ops for as long as they have existed. The
> bridge's half worked the whole time and is tested end to end above; it was simply never called.
>
> The new form then hit the same wall a second way, in one afternoon: the file field was fed
> `whitelist.listSpawnFiles()`, which returns `spawn:` keys, where the shape id wants the relative
> path — giving `spawner:spawn:trammel/...`, then `fileOf` giving `spawn:spawn:trammel/...`, and a
> save that matched no writable file.
>
> Two independent bugs with one symptom, and the symptom was silence. So **`save()` now refuses out
> loud** when it has edits and can resolve no file to put them in, naming the shapes. That guard is
> what would have caught either of them on the first attempt, and it is the part worth keeping.

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

`GET /tiles/iso/<facet>/v<n>/<layer>/<floor>/<level>/<x>/<y>.png` is a real handler rather than
`serveStatic`, because a miss has to be rendered before it can be answered. Every segment is
checked against what it is allowed to *be* — three floor names, one of two extensions, and
non-negative integers — and the path is then rebuilt from those numbers, so there is nothing to
traverse because there is nothing to steer. Radar tiles keep falling through to `serveStatic`; they
are exported ahead of time and there is nothing to render.

**`.pick` goes down the same route**, because the sidecar is rendered on a miss exactly as the
picture is. The extension is a closed set — `png` or `pick`, never a pattern — for the same reason
every other segment is: a name that has to be cleaned up before it is safe is a name worth refusing.
It is served with `Content-Encoding: gzip` and `application/octet-stream`, so the browser inflates it
and `fetch().arrayBuffer()` gets the exact bytes.

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
| `tile-probe` | What the engine sees at a tile, and whether it will let a step onto it. Body `"x,y"` or `"x,y,z"` (several may be given), or `"sweep <lo> <hi>"` for every world item in an ItemID range; answers to `Data/Live/tile-probe.json` and in the ack's `warnings` |
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

The nonce set has grown with the Admin section: `core-smoke`, `bot-smoke`, `bots-reload`,
`shutdown` and `tile-probe` are all nonced now. **`broadcast` is deliberately not**, for a different
reason from `livemap-on`'s — its body is the message every player is about to read, and appending
`#a1b2c3d4` to that is not a thing to do. (`tile-probe` is safe to nonce because its parser skips
any word that is not an `x,y` pair.)

## The Admin section

Everything that acts on the shard rather than on its files. It was deliberately left out until now:
it is the only part of this editor that touches the shard **process**, it needed a console channel
that did not exist, and it is the only one that can leave the shard down.

**Every button here runs without a client.** That is not a coincidence — the `RequestPoller` cases
call each command's underlying static method, never a synthesised `CommandEventArgs`, which is the
shape the existing cases already had (`GGSpawnCommands.TryReimport(null, …)`,
`NavWalkAudit.TryStart(null, …)`). Anything that needs a client is listed below rather than stubbed.

| Button | Request | What it is |
| --- | --- | --- |
| Save world | `save` | Exactly what `[Save` runs, backup rotation included |
| Restart shard | *bridge* | Save, stop, rebuild, start. See below |
| Core smoke | `core-smoke` | `CoreSmoke.Run(null)` — the path `Custom.CoreSmokeOnStart` already takes |
| Bot smoke | `bot-smoke` | `BotSmoke.Run(null)` — walk, life, chat, work. Minutes |
| Reload bots | `bots-reload` | `BotSystem.TryReload` **and** `BotWorkSites.Validate` |
| Nav audit / full | `nav-audit` | Body empty or `full`; `full` adds the ~8 s cliff scan |
| Walk audit | `walk-audit` | Real probe walkers over the whole graph. Minutes |
| World items | `world-items` | The art view's furniture snapshot |
| Resync spawns | `gg-reimport` | Behind a typed `RESYNC` |
| Regen bot spawns | three | `botpop-gen` → `gg-reimport` → `botpop-audit`, behind `REGEN` |
| Broadcast | `broadcast` | `CommandHandlers.BroadcastMessage`, which needs no Mobile |

**`bots-reload` runs both halves of the command, and the second is the one easy to lose.**
`BotsReload_OnCommand` re-validates the work sites *in the command body*, not inside `TryReload` —
so a dispatch calling only `TryReload` would silently do less than `[BotsReload` and leave a site
fixed by a nav edit excluded until a restart.

**Regenerating the bot population is three requests in one order and no other.** The recipe is
derived and the file is not, so a regen that is not re-imported leaves the world running the old
population for ever; the audit afterwards is what says the file and the recipe now agree.

### Started, not run

`[CoreSmoke`, `[BotSmoke` and `[WalkAudit` take minutes, and `Poll` is a `Timer` callback on the
game thread — dispatching one inline would freeze the world for its whole length and the ack would
arrive long after any caller had given up. So the ack says *started* and the result arrives in
**Health** and in the **console feed**. That is the pattern `walk-audit` already set, and it is the
reason the console feed had to exist before the buttons did.

Proved end to end: `[CoreSmoke` dropped as a token read
`RESULT: PASS (foundations); 4 warning` in the panel's own feed, with all forty-odd check lines.

### The console feed

**The shard's log IS its console, and nothing outside that window could read a line of it.** See
`SHARD.md` for the mechanism (`ConsoleTap`, and why `Core.MultiConsoleOut` is the seam that looks
right and is dead in Release). The panel reads `Data/Live/console.json` and `logins.json` through
`/api/console` and `/api/logins`.

It polls only **while the section is open** — the tail is 2000 lines — and redraws only when the
`sequence` has moved, because rewriting the `<pre>` on every poll would fight the scrollbar of
anybody reading it. The age beside the heading is the point of showing a sequence at all: a feed
that has stopped moving looks exactly like a quiet shard until you know when it last moved, which
is the Live panel's reason for showing an age rather than only a count.

The login feed is usually empty, and says so rather than looking broken: **bots never log in**, as
they have no `NetState`.

### Restart

The one thing here that can leave the shard down, so it is behind a typed `RESTART` and it reports
every stage. The sequence is `tools/dev.ps1`'s, and each step is there because skipping it loses
something:

1. **`save`, and wait for the ack.** `HandleClosed` does *not* save on exit — it only waits for
   writes already in flight. Killing without this loses everything since the last autosave.
2. **`shutdown`.** The shard writes its ack **before** `Core.Kill`, so "did it hear us" stays
   answerable; a caller polling afterwards would be polling a dead shard for its whole timeout.
3. **Wait for `ServUO.exe` to go.** `Scripts.dll` is locked while loaded, so a build started too
   early fails on a file lock rather than on anything real.
4. **`tools/restart-shard.ps1`**, which runs `build.ps1` — so a restart **rebuilds**, and a failed
   build still refuses to launch. That is the entire reason this shard has build scripts
   (`CLAUDE.md` section 2), and a restart that started `ServUO.exe` directly would be the one path
   back around the hole they exist to close. The script refuses outright if a shard is already
   running.
5. **Watch for the shard to answer**, which is a fresh `health` ack.

`POST /api/restart` starts it and answers immediately; `GET /api/restart` reports the stage. Two
calls rather than one held connection, because the whole thing is a build and a boot and a request
held across it would time out in the middle, leaving the caller unable to tell a slow restart from
a failed one.

The bridge spawns exactly **one known script and passes it nothing** — `artrenderer.js:50` is the
existing precedent for the bridge spawning a child at all.

> **It failed in the least useful way possible first, and that is why the launcher is read rather
> than ignored.** `spawn('powershell', …)` without `shell: true` does not consult `PATHEXT`, so it
> raised `ENOENT` — onto an `error` event nothing was listening for, with `stdio: 'ignore'` throwing
> the evidence away. The restart then sat in *waiting* for its full two minutes and reported that
> the shard had not come back: true, and silent about why. It is `execFile('powershell.exe', …)`
> now, with stdout and stderr read back into the failure message. **A launcher that cannot say it
> failed to launch is worse than no launcher.**

Measured on the shipped build: save 0.24 s, stop, rebuild, boot, answering again — the whole
restart in about 25 seconds.

### Needs a client, and stays out

Not stubbed, not greyed out — absent, and listed here instead:

- **`[BotSendTo`** — resolves its bot and destination by name perfectly well without a client, but
  delegates to `SendBot(Mobile from, …)` (`BotCommands.cs:747`), which calls `from.SendMessage` on
  every branch and would dereference null. A `TrySendTo(serial, destinationId, out message)` would
  fix it; nothing needs it yet.
- **`[BotPace`** and **`[BotTrace`** — both `BeginTarget` a mobile.
- Anything that opens a gump: `[Quests`, `[JailInfo`, `[Props`.

### Later, in rough order of how often you would want it

- **Who is connected** — `NetState.Instances` with account, IP and idle time, and a kick.
- **A save-progress line.** `World.Save` freezes the world on the core thread, so an editor polling
  through one has no way to say *why* everything stopped. `Core.Process` now reports the duration
  after the fact; during is a different problem.
- **The request log** — the last N tokens and their acks, which is the whole editor-to-shard channel
  and is readable today only with `type` in `Data/Live/requests/`.
- **Config, read-only** — the `Custom.*` keys actually in force, which today means opening
  `Config/Custom.cfg` and hoping `_DEBUG.cfg` is not overriding it.
- **Toggle the smoke-on-start flags**, the one edit anybody makes to `Custom.cfg` by hand and the
  one that has been committed as `True` by accident (`SHARD.md`).
- **Filter the console feed** — warnings and errors only, or a search box. 2000 lines is a lot to
  scroll when you know what you are looking for.
- **Build without restarting**, which would have to refuse while `Scripts.dll` is locked — exactly
  the failure `build.ps1` exists to make loud.
