'use strict';

// The editor bridge.
//
//   node tools/editor/bridge.js [--port 8081]
//
// Serves the editor, the rendered map tiles, and a projection of the shard's data files. It is
// the only thing between a browser and the shard, and it deliberately has no dependencies: the
// whole job is reading JSON, reshaping it, and serving static files, which is what Node's
// standard library is for. No package.json, no node_modules, nothing to audit.
//
// SECURITY, and what each layer is actually for.
//
//   the bind    127.0.0.1 only, so nothing off this machine can reach the socket.
//   the Host    every request, read or write, must address a loopback name on the port this
//               process is listening on. That is the DNS-rebinding defence: the bind does not help
//               when the browser is told that evil.example resolves to 127.0.0.1, and a rebound
//               page reading the world snapshot is a real loss even though it writes nothing.
//   the gate    a request that MUTATES or INVOKES - a token drop, a save, a restore, a restart -
//               must be same-origin by fetch metadata with an exactly-matching Origin, or carry
//               this session's secret. Sec-Fetch-Site is forbidden to page script, so a page in
//               another tab cannot forge it.
//
// WHAT CHANGED AND WHY (REVIEW.md section 2). The old check accepted `same-site` and `none` as
// well as `same-origin`, never looked at Origin when the metadata was present, and allowed a
// request carrying neither header at all. Two different ports on one host are same-SITE while
// being different origins, so http://127.0.0.1:9999 - any other local service, or anything a
// page can get you to open - counted as friendly; and "neither header" is every non-browser
// caller in the world, which is how curl was allowed to ask this process to shut the shard down.
// Reads stay unchecked past the Host: they are the ones that have to answer from a bookmark.

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const compact = require('./compact.js');
const { project, unproject } = require('./project.js');
const spawners = require('./spawners.js');
const reference = require('./reference.js');
const vocabulary = require('./vocabulary.js');
const whitelist = require('./whitelist.js');
const { ArtRenderer, parseTilePath, EMPTY_PNG } = require('./artrenderer.js');

const DEFAULT_PORT = 8081;
const HOST = '127.0.0.1';

// The hostnames a loopback request may arrive under. Anything else is a name that resolved here
// from outside - the rebinding case - and is refused before any handler sees it.
const LOCAL_HOSTNAMES = new Set(['127.0.0.1', 'localhost', '[::1]', '::1']);

/**
 * This run's secret, for callers that are not a browser.
 *
 * Printed on the startup line, because that is where somebody scripting against this bridge is
 * already looking, and regenerated every run so a secret pasted into a script stops working when
 * the bridge restarts - which is the correct lifetime for a thing whose only job is to prove the
 * caller is on this machine and meant it. The env override exists so the tests can know it.
 *
 * The editor page never needs it: it is same-origin, and that is what the gate below asks of it.
 */
const SESSION_SECRET = process.env.GG_BRIDGE_SECRET || crypto.randomBytes(16).toString('hex');

// A save body is the shapes that changed, not the file, but a big multi-select could still be a
// few hundred KB. Over the limit is an answer, not a dropped connection.
const MAX_BODY = 1024 * 1024;

// EXCEPT A NAV SAVE, which can legitimately be an adopt: the whole-Trammel adopt is thousands of
// waypoints and edges created in one save, several megabytes of shapes, and accept-adopt.js is the
// ordinary save path on purpose. The save route alone gets the larger limit; a token or any other
// body stays at a megabyte.
const MAX_SAVE_BODY = 32 * 1024 * 1024;

// How long to wait for the shard to answer a commit or a reload. The poller ticks once a second,
// so this is four ticks of grace before the editor is told the shard did not answer. The override
// exists so the timeout path can be tested in a fraction of a second rather than five of them.
const ACK_TIMEOUT_MS = Number(process.env.GG_ACK_TIMEOUT_MS) || 5000;

// The isometric art tiles are rendered on demand rather than exported in a batch, because a
// facet-wide iso render is 61 gigapixels per floor. This owns the child process that draws them;
// it starts on the first art tile anybody asks for and not before, so a session that never leaves
// radar never launches it. See artrenderer.js.
const art = new ArtRenderer({ log: (line) => console.log(line) });

// The same cap TileServer.LandZ enforces. A facet is 29 million tiles and a query string is not the
// place to ask about all of them; the editor batches its zone corners, and this is what stops a
// batch turning into a sweep.
const MAX_LANDZ_TILES = 256;

// validate.js is a browser ES module and this file is CommonJS, so it arrives as a promise. That
// is fine: every place it is awaited is already async, and awaiting a settled promise is free.
// One copy of the rules, shared by the preview in the browser and the dry run here.
const validator = import('./js/validate.js');

const CONTENT_TYPES = {
    '.html': 'text/html; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.png': 'image/png',
    '.svg': 'image/svg+xml',
    '.ico': 'image/x-icon',

    // Plain text, not text/markdown: a browser offers the latter as a download.
    '.md': 'text/plain; charset=utf-8'
};

function readJson(file) {
    try {
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (error) {
        if (error.code === 'ENOENT') {
            return null;
        }

        throw error;
    }
}

/** A file's exact bytes, or '' when it is not there yet. */
function readText(file) {
    try {
        return fs.readFileSync(file, 'utf8');
    } catch (error) {
        if (error.code === 'ENOENT') {
            return '';
        }

        throw error;
    }
}

/**
 * What the editor holds a file at, so a save can refuse to overwrite someone else's work.
 *
 * Over the raw text rather than the parsed document, because [NavMark and [NavRecord write this
 * same file from inside the shard - a stale editor must not be able to flatten a walk that was
 * just recorded, and a reformat is a change too. The shard computes the same thing from the
 * file's bytes (DataFileCommit.Hash16): identical for the BOM-less UTF-8 both writers produce.
 */
function hashOf(text) {
    return crypto.createHash('sha256').update(text, 'utf8').digest('hex').slice(0, 16);
}

/**
 * The version a file is at: its hash, or null when it does not exist. Null rather than the hash
 * of '' because the shard's version of an absent file is "none", and a save that creates a file
 * has to be based on that - not on the hash of nothing.
 */
function versionOf(file) {
    return fs.existsSync(file) ? hashOf(readText(file)) : null;
}

/** The hop cap the shard is actually configured with, so no message here quotes a literal. */
function configuredHopCap() {
    try {
        const cfg = fs.readFileSync(
            path.join(whitelist.REPO_ROOT, 'Config', 'Custom.cfg'), 'utf8');
        const match = cfg.match(/^NavHopMaxTiles=(\d+)/m);

        return match ? Number(match[1]) : undefined;
    } catch {
        return undefined;
    }
}

/** `x,y,width,height` in game tiles, or null when it is absent or nonsense. */
function parseBbox(text) {
    if (!text) {
        return null;
    }

    const parts = text.split(',').map(Number);

    if (parts.length !== 4 || parts.some((n) => !Number.isFinite(n))) {
        return null;
    }

    const [x, y, width, height] = parts;

    return width > 0 && height > 0 ? { x, y, width, height } : null;
}

/**
 * "x,y;x,y;..." into [[x, y], ...], or null if any of it is not a pair of integers.
 *
 * Capped here as well as in the renderer, and at the same number, so an over-long list is refused
 * by whichever end sees it first rather than by whichever end happens to be reached.
 */
function parseTileList(text) {
    if (!text) {
        return null;
    }

    const tiles = [];

    for (const token of text.split(';')) {
        const pair = token.split(',');

        if (pair.length !== 2) {
            return null;
        }

        const x = Number(pair[0]);
        const y = Number(pair[1]);

        if (!Number.isInteger(x) || !Number.isInteger(y)) {
            return null;
        }

        tiles.push([x, y]);
    }

    return tiles.length > 0 && tiles.length <= MAX_LANDZ_TILES ? tiles : null;
}

/**
 * A stock spawn file, cached until it changes.
 *
 * trammel.xml is 4 MB and 2,572 spawners; re-reading and re-parsing it on every pan would make the
 * bbox pointless. Keyed on mtime and size rather than a timer, so an edit made outside the editor
 * is picked up on the next request and nothing is served stale.
 */
const stockCache = new Map();

function readStockFile(facet) {
    const file = path.join(whitelist.REPO_ROOT, 'Spawns', `${facet.toLowerCase()}.xml`);

    let stat;

    try {
        stat = fs.statSync(file);
    } catch {
        return null;
    }

    const stamp = `${stat.mtimeMs}:${stat.size}`;
    const cached = stockCache.get(facet);

    if (cached && cached.stamp === stamp) {
        return cached.entry;
    }

    const entry = {
        key: `stock:${facet}`,
        relative: `${facet.toLowerCase()}.xml`,
        stock: true,
        text: readText(file)
    };

    stockCache.set(facet, { stamp, entry });

    return entry;
}

function countBlocks(text) {
    let count = 0;
    let at = 0;

    for (;;) {
        at = text.indexOf('<Points>', at);

        if (at === -1) {
            return count;
        }

        count++;
        at += 8;
    }
}

function sendJson(response, status, payload) {
    const body = JSON.stringify(payload);

    response.writeHead(status, {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': Buffer.byteLength(body),
        // The editor is a local tool being actively edited; a cached stale copy of its own data
        // is never what anyone wants.
        'Cache-Control': 'no-store'
    });

    response.end(body);
}

function sendError(response, status, message) {
    // The editor's error path reads payload.error, so every failure has to have that shape or it
    // surfaces as a bare 'HTTP 500' with nothing to act on.
    sendJson(response, status, { error: message });
}

/**
 * Whether this request addressed the bridge by a loopback name on the port it is listening on.
 *
 * Asked of EVERY request, including reads. A browser told that some hostname resolves to
 * 127.0.0.1 will happily connect to this socket and send that hostname as the Host - the bind
 * cannot see the difference, and the page then reads whatever the read endpoints serve. Comparing
 * the Host is what makes the connection's own name part of the check.
 *
 * The port comes from the socket rather than from a constant, so the tests' ephemeral port and a
 * `--port 9000` run are both covered without a special case.
 */
function hostIsLocal(request) {
    const host = request.headers.host;

    if (!host) {
        // HTTP/1.1 requires one. No Host is a hand-built request, and there is nothing to check.
        return false;
    }

    const at = host.lastIndexOf(':');
    const bracketed = host.startsWith('[');
    const hasPort = at > (bracketed ? host.lastIndexOf(']') : -1);

    const hostname = hasPort ? host.slice(0, at) : host;
    const port = hasPort ? host.slice(at + 1) : '';

    if (!LOCAL_HOSTNAMES.has(hostname.toLowerCase())) {
        return false;
    }

    // A bare host with no port means port 80, which this never binds.
    return port === String(request.socket.localPort);
}

/** The two spellings of this bridge's own origin. A browser sends one of these or nothing. */
function allowedOrigins(request) {
    return [
        `http://127.0.0.1:${request.socket.localPort}`,
        `http://localhost:${request.socket.localPort}`
    ];
}

/**
 * Why a MUTATING or INVOKING request - a token drop, a save, a restore, a restart - is refused,
 * or null when it may proceed.
 *
 * Two ways in, and they are for two different callers.
 *
 * A BROWSER proves it by being same-origin. `Sec-Fetch-Site` is set by the browser and cannot be
 * written by page script, so a page on another origin cannot claim `same-origin`; `same-site` and
 * `none` are NOT accepted, because a different port on this host is same-site while being a
 * different origin, and `none` is a typed-in address or a bookmark. When an `Origin` is present it
 * must match this bridge exactly - CORS decides whether the attacker may READ the answer, which is
 * no comfort at all when the side effect is "shut the shard down".
 *
 * A SCRIPT proves it with the secret from the startup line, in X-GG-Auth. Nothing in a browser can
 * read that line, so a page cannot obtain it; anything with a terminal on this machine already can.
 * That is the honest boundary: this defends against a PAGE, not against a person at this keyboard.
 *
 * A request with no metadata and no secret is refused. That is a behaviour change for curl, and it
 * is the point: until now any process, and any page able to reach a form post at this port, could
 * ask for a save, a shutdown or a restart with no header at all. tools/editor/accept-adopt.js and
 * repoint-arrivals.js are the two scripted callers in this tree; both read GG_BRIDGE_SECRET.
 */
function refusalFor(request) {
    const site = request.headers['sec-fetch-site'];

    // A BROWSER. The metadata decides, and the secret cannot override it: a request that says it
    // came from elsewhere is refused whatever else it carries, which costs a legitimate caller
    // nothing (a script sends no such header) and removes the question of whether a page could
    // ever obtain the secret from mattering.
    if (site !== undefined) {
        if (site !== 'same-origin') {
            return `Refused: cross-site request (Sec-Fetch-Site: ${site}).`;
        }

        const origin = request.headers.origin;

        if (origin && !allowedOrigins(request).includes(origin)) {
            return `Refused: Origin ${origin} is not this bridge.`;
        }

        return null;
    }

    // A SCRIPT. No fetch metadata at all, which is every non-browser caller - and until now was
    // accepted unconditionally, which is how curl could ask this process to shut the shard down.
    const offered = request.headers['x-gg-auth'];

    if (typeof offered === 'string' && offered.length > 0 && offered === SESSION_SECRET) {
        return null;
    }

    return "Refused: this request mutates, so it needs X-GG-Auth with the secret from the bridge's "
        + 'startup line (or a same-origin browser request).';
}

function serveStatic(urlPath, response) {
    const file = whitelist.resolveStatic(urlPath);

    if (!file) {
        sendError(response, 403, 'Refused: outside the editor directory.');
        return;
    }

    fs.readFile(file, (error, data) => {
        if (error) {
            sendError(response, 404, 'Not found.');
            return;
        }

        response.writeHead(200, {
            'Content-Type': CONTENT_TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream',
            'Content-Length': data.length,
            // Tiles are immutable until re-rendered; the editor's own source is not.
            'Cache-Control': urlPath.startsWith('/tiles/') ? 'public, max-age=86400' : 'no-cache'
        });

        response.end(data);
    });
}

const ROUTES = {
    '/api/status': (request, response) => {
        const nav = readJson(whitelist.FILES.navigation);
        const daily = readJson(whitelist.FILES.dailyLife);
        const entities = readJson(whitelist.FILES.entities);

        sendJson(response, 200, {
            // The editor drives its facet list from this; width and height must match what
            // MapExport rendered or the pyramid maths goes wrong.
            facets: [{ name: 'Trammel', width: 7168, height: 4096 }],
            navigationLoaded: nav !== null,
            dailyLifeLoaded: daily !== null,
            waypoints: nav ? (nav.waypoints || []).length : 0,
            destinations: nav ? (nav.destinations || []).length : 0,
            // Sequence and timestamp let the editor say "the shard stopped" rather than showing
            // a frozen map that looks like a quiet one.
            live: entities
                ? { sequence: entities.sequence, utc: entities.utc, scope: entities.scope, count: entities.entities.length }
                : null
        });
    },

    /**
     * What the art renderer is, so the editor can address a tile without holding a second copy of
     * the projection or the level maths. `available: false` is a normal answer - the tool may not
     * be built - and the editor stays in radar rather than offering a view it cannot draw.
     */
    '/api/artinfo': (request, response) => {
        if (!art.available) {
            sendJson(response, 200, { available: false, reason: 'MapExport.exe has not been built.' });
            return;
        }

        // Started lazily so a bridge nobody switches to art never launches a renderer, but
        // /api/artinfo is asked once at boot, and the answer needs the handshake.
        art.start()
            // The world-item snapshot is checked here rather than watched: this is asked at boot
            // and whenever the editor polls, which is every moment anybody cares whether the
            // furniture has changed.
            .then(() => art.ensureItems())
            .then(
                () => sendJson(response, 200, Object.assign({ available: true }, art.describe(), {
                    items: art.items
                })),
                (error) => sendJson(response, 200, { available: false, reason: error.message }));
    },

    /** Cached and rendered counts, and how long the last tile took. */
    '/api/artstats': (request, response) => {
        sendJson(response, 200, art.counters());
    },

    /**
     * The Z a mobile stands at on each of `?tiles=x,y;x,y;...`, in the order asked.
     *
     * What the editor draws zone rects with. A zone is a rectangle of ground and carries no Z in
     * its schema - none is being added, because where the ground is is a question with an answer,
     * so it is asked rather than stored. Same accessor as the pick map's land Z, so a zone corner
     * and a waypoint on the same tile agree by construction.
     *
     * It answers 200 with `z: null` rather than an error when there is no renderer: a zone still
     * has to draw, and a radar placement still has to succeed, when MapExport has not been built.
     */
    '/api/landz': (request, response) => {
        const url = new URL(request.url, `http://${HOST}`);
        const tiles = parseTileList(url.searchParams.get('tiles'));

        if (tiles === null) {
            sendError(response, 400, 'tiles must be "x,y;x,y;..." of integers.');
            return;
        }

        if (!art.available) {
            sendJson(response, 200, { z: null, reason: 'MapExport.exe has not been built.' });
            return;
        }

        art.landz(tiles).then(
            (z) => sendJson(response, 200, { z }),
            (error) => sendJson(response, 200, { z: null, reason: error.message }));
    },

    '/api/shapes': (request, response) => {
        const shapes = project({
            navigation: readJson(whitelist.FILES.navigation) || {},
            dailyLife: readJson(whitelist.FILES.dailyLife) || {},
            restrictedZones: readJson(whitelist.FILES.restrictedZones) || {}
        });

        // The hash each file was read at. A save sends the one it started from, and a mismatch
        // means somebody else - another tab, or [NavMark in game - got there first.
        const files = {};

        for (const name of Object.keys(whitelist.WRITABLE)) {
            files[name] = { hash: versionOf(whitelist.FILES[name]) };
        }

        sendJson(response, 200, { shapes, files });
    },

    /**
     * The spawner layers.
     *
     * The GG files are always returned in full - there are seven spawners in two files. The stock
     * ones are returned only for a bbox, because there are 2,572 on Trammel alone and putting them
     * all in the shape list would grow it tenfold and swamp the label pass for no gain: what you
     * want is what is already spawning near the thing you are editing.
     *
     * `total` comes back too, so the layer count can read "N in view / 2,572 total" and the number
     * never looks like the whole.
     */
    /**
     * The uo-offline reference inside a box.
     *
     * Bbox-loaded for the same reason the stock spawners are: 3952 waypoints and 4291 edges are
     * not drawable at facet scale, and serialising the 1988 dungeon ones on every pan to have the
     * browser drop them is work nobody asked for. The regions the caller wants are named in the
     * query, so a toggle that is off costs nothing on the wire.
     */
    /** The adopt proposal, progress and all. Polled while a run is in flight. */
    /**
     * The adopt proposal, progress and all.
     *
     * Polled while a run is in flight: a large region is minutes of flood-fill, and `done`/`total`
     * is the only thing that distinguishes working from hung.
     */
    '/api/adopt': (request, response) => {
        sendJson(response, 200, readJson(whitelist.FILES.navAdopt)
            || { utc: null, status: 'none', done: 0, total: 0 });
    },

    '/api/reference': (request, response) => {
        const url = new URL(request.url, `http://${HOST}`);
        const bbox = parseBbox(url.searchParams.get('bbox'));
        const facet = url.searchParams.get('facet') || 'Trammel';
        const regions = (url.searchParams.get('regions') || 'overworld')
            .split(',')
            .map((token) => token.trim())
            .filter(Boolean);

        sendJson(response, 200, {
            ...reference.project(bbox, { regions, facet }),
            ...reference.summary(),
            bbox: bbox || null
        });
    },

    '/api/spawners': (request, response) => {
        const url = new URL(request.url, `http://${HOST}`);
        const bbox = parseBbox(url.searchParams.get('bbox'));
        const facet = url.searchParams.get('facet') || 'Trammel';

        const custom = whitelist.listSpawnFiles().map((key) => ({
            key,
            relative: whitelist.spawnRelative(key),
            text: readText(whitelist.resolveSpawnFile(key))
        }));

        const files = {};

        for (const file of custom) {
            files[file.key] = { hash: hashOf(file.text) };
        }

        const stock = bbox ? readStockFile(facet) : null;

        sendJson(response, 200, {
            shapes: [
                ...spawners.project(custom),
                ...(stock ? spawners.project([stock], bbox) : [])
            ],
            files,
            // Everything in the file, so "68 in view" can say what it is 68 of.
            total: stock ? countBlocks(stock.text) : null,
            bbox: bbox || null,
            findings: spawners.findings(custom)
        });
    },

    '/api/entities': (request, response) => {
        const live = readJson(whitelist.FILES.entities);

        if (!live) {
            // Not an error: the snapshot is off until someone asks for it.
            sendJson(response, 200, { entities: [], sequence: 0, running: false });
            return;
        }

        sendJson(response, 200, { ...live, running: true });
    },

    /** The last [NavAudit, as records rather than prose, so the editor can draw it. */
    '/api/audit': (request, response) => {
        sendJson(response, 200, readJson(whitelist.FILES.navAudit) || { utc: null, problems: [] });
    },

    /**
     * The last [WalkAudit.
     *
     * A SEPARATE ENDPOINT FROM /api/audit, not a field on it, because the two are produced by
     * different runs at different times and cost. [NavAudit is seconds and runs on every nav save;
     * a walk sweep is minutes and runs when somebody asks. Folding them together would make the
     * cheap one wait for the expensive one, or make the expensive one's answer look current when
     * it is an hour old.
     *
     * `status` is what the button polls: the shard writes this file once at the START of a run
     * with status "running", because a sweep takes minutes and the request token cannot ack that
     * long.
     */
    '/api/walkaudit': (request, response) => {
        sendJson(
            response,
            200,
            readJson(whitelist.FILES.walkAudit) || { utc: null, status: 'none', rows: [] });
    },

    /**
     * How many harvestable tiles each work-site arrival can actually reach.
     *
     * Read-only, like every other Data/Live file: the shard measures it against real map data and
     * the editor only draws it. `probe` holds answers for points that are not in navigation.json
     * yet, which is what lets the Site tool judge a tile while it is being placed.
     */
    /**
     * The last road nav-route walked, as tiles plus the hops it verified.
     *
     * `hops` is what the editor proposes waypoints on, and `hopsWalkable` is the shard saying it
     * drove a real creature over every one of them. The raw `points` are drawn as the road itself
     * so an author can see what the proposal is a summary of.
     */
    '/api/route': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.navRoute)
                || { utc: null, map: null, ok: false, points: [], hops: [] });
    },

    /**
     * The last fifty events per bot, as the shard recorded them.
     *
     * Written beside entities.json and on the same timer, so a bot the map is drawing always has
     * a history to show. Read-only, like every Data/Live file.
     */
    /** Per-hop walkability and road snaps, for editing a proposal without re-walking it. */
    '/api/hops': (request, response) => {
        sendJson(response, 200, readJson(whitelist.FILES.navHop) || { hops: [], snap: null });
    },

    '/api/botlog': (request, response) => {
        sendJson(response, 200, readJson(whitelist.FILES.botLog) || { utc: null, bots: [] });
    },

    /**
     * The last [BotInfo report the shard was asked for.
     *
     * `serial` is echoed back by the shard so the card can tell its own answer from the answer to
     * the request before it - a bot is ephemeral and the one you selected may already be gone.
     */
    '/api/botinfo': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.botInfo) || { utc: null, serial: 0, name: null, lines: [] });
    },

    '/api/reach': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.siteReach) || { utc: null, map: null, arrivals: [], probe: [] });
    },

    /** What the spawners are actually doing, as opposed to what the files say. */
    '/api/spawner-state': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.spawnerState)
            || { sequence: 0, utc: null, total: 0, duplicateGroups: 0, duplicates: [], spawners: [] });
    },

    '/api/health': (request, response) => {
        const health = readJson(whitelist.FILES.health);

        sendJson(response, 200, health || { utc: null, worst: 'Unknown', checks: [] });
    },

    /**
     * The shard's console tail, and its sign-ins.
     *
     * `sequence` is what makes these readable: a tail that has stopped moving looks exactly like a
     * quiet shard until you know when it last moved, which is the same reason the Live panel shows
     * an age rather than only a count. An absent file is a shard that is down or has
     * `Custom.ConsoleTap=False`, and the empty shape says so without the panel having to guard.
     */
    '/api/console': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.console) || { utc: null, sequence: 0, lines: [] });
    },

    '/api/logins': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.logins) || { utc: null, sequence: 0, lines: [] });
    },

    /** What the engine sees at a tile. Written by the `tile-probe` token. */
    '/api/tile-probe': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.tileProbe) || { utc: null, lines: [] });
    },

    /** The last [BotPace sample. Written by the `bot-pace` token, N seconds after it is dropped. */
    '/api/bot-pace': (request, response) => {
        sendJson(response, 200,
            readJson(whitelist.FILES.botPace) || { utc: null, bot: null, lines: [] });
    },

    /** How the last restart is going. See handleRestart. */
    '/api/restart': (request, response) => {
        sendJson(response, 200, restartState);
    },

    /**
     * Every list the create forms offer, so no form has to carry one.
     *
     * Half of it is the shard's report of what it loaded and half is derived from the files here;
     * see vocabulary.js for which is which and why the split falls there. It answers with the
     * shard down, marking `shardSeen` false rather than refusing - the same rule the art view
     * follows.
     */
    '/api/vocabulary': (request, response) => {
        refreshVocabulary();
        sendJson(response, 200, vocabulary.build(readJson));
    }
};

/**
 * Asks the shard for a fresh type list when the one on disk is older than the assembly.
 *
 * Fire and forget, and deliberately not awaited: this request must answer at boot whether the
 * shard is up or not, and the answer it can give right now is the previous run's list - which is
 * correct unless Scripts.dll has been rebuilt, which is the one case this checks for. The fresh
 * copy is there by the next fetch.
 *
 * Scripts.dll rather than a timer because that is exactly when the answer can change: a type is
 * added or removed by a build, and a build means a restart.
 */
function refreshVocabulary() {
    let assembly;
    let snapshot;

    try {
        assembly = fs.statSync(path.join(whitelist.REPO_ROOT, 'Scripts.dll')).mtimeMs;
    } catch {
        // No assembly to compare against - a source checkout that has never been built. Nothing
        // to ask for.
        return;
    }

    try {
        snapshot = fs.statSync(path.join(whitelist.REPO_ROOT, 'Data', 'Live', 'vocabulary.json')).mtimeMs;
    } catch {
        snapshot = 0;
    }

    if (snapshot >= assembly) {
        return;
    }

    try {
        writeToken('vocabulary', '');
    } catch (error) {
        // The shard may be down, the directory may not exist yet. Neither is worth failing the
        // request over: the editor gets the derived half and says the shard has not answered.
        console.log(`vocabulary: could not ask the shard (${error.message})`);
    }
}

function handleRequest(request, response) {
    const url = new URL(request.url, `http://${HOST}`);
    const pathname = url.pathname;

    // Before anything, and before reads too: see hostIsLocal.
    if (!hostIsLocal(request)) {
        sendError(response, 403, 'Refused: this bridge answers 127.0.0.1 only.');
        return;
    }

    if (request.method === 'POST') {
        const refusal = refusalFor(request);

        if (refusal) {
            sendError(response, 403, refusal);
            return;
        }

        // Every POST is gated by the check above, before any of these are reached.
        const posts = [
            ['/api/request/', dropToken],
            ['/api/save/', handleSave],
            ['/api/restore/', handleRestore],
            ['/api/restart', handleRestart]
        ];

        for (const [prefix, handler] of posts) {
            if (pathname.startsWith(prefix)) {
                handler(pathname.slice(prefix.length), request, response)
                    .catch((error) => sendError(response, 500, error.message));

                return;
            }
        }

        sendError(response, 404, 'No such endpoint.');
        return;
    }

    if (request.method !== 'GET') {
        sendError(response, 405, 'Method not allowed.');
        return;
    }

    const route = ROUTES[pathname];

    if (route) {
        try {
            route(request, response);
        } catch (error) {
            sendError(response, 500, error.message);
        }

        return;
    }

    // An art tile is rendered on a miss, so it cannot be served as a static file: the request
    // is held until the renderer answers. Radar tiles fall through to serveStatic as they always
    // have - they are exported ahead of time and there is nothing to render.
    if (pathname.startsWith('/tiles/iso/')) {
        serveArtTile(pathname, response);
        return;
    }

    // /api/ack/<name>/<id>: the ack for one request, matched the way the bridge's own waits match
    // it, or where the request stands when there is no ack yet.
    if (pathname.startsWith('/api/ack/')) {
        const [name, id] = pathname.slice('/api/ack/'.length).split('/');
        const file = whitelist.resolveAck(name, id || '');

        if (!file) {
            sendError(response, 400, 'Bad request name or id: /api/ack/<name>/<id>.');
            return;
        }

        const ack = readAck(file);

        if (ack) {
            const verdict = matchAck(ack, id, currentBootId());

            sendJson(response, 200, verdict.matched ? ack : { pending: true, state: 'ignored', ignored: [verdict.reason] });
            return;
        }

        sendJson(response, 200, { pending: true, state: requestState(name, id) });
        return;
    }

    serveStatic(pathname, response);
}

/**
 * Serves one isometric art tile, rendering it first if it is not cached.
 *
 * A miss BLOCKS this request rather than answering with a placeholder, which is why there is no
 * placeholder protocol anywhere in the editor: the browser's Image simply takes a moment, and the
 * radar underlay is what is on screen until it arrives. A render that runs past the renderer's
 * timeout answers 503, and the editor drops that tile from its cache so a later pan asks again.
 */
function serveArtTile(pathname, response) {
    const parts = parseTilePath(pathname);

    if (!parts) {
        sendError(response, 400, 'Bad tile path.');
        return;
    }

    if (parts.extension === 'pick') {
        serveArtPick(parts, response);
        return;
    }

    art.tile(parts.layer, parts.floor, parts.level, parts.x, parts.y).then(
        (file) => {
            if (file === null) {
                // No items in this tile. One transparent pixel, stretched - see EMPTY_PNG. It is
                // cached for a day like any other tile, so an empty stretch of country costs one
                // request and then nothing.
                response.writeHead(200, {
                    'Content-Type': 'image/png',
                    'Content-Length': EMPTY_PNG.length,
                    'Cache-Control': 'public, max-age=86400'
                });

                response.end(EMPTY_PNG);
                return;
            }

            fs.readFile(file, (error, data) => {
                if (error) {
                    sendError(response, 500, 'The tile was rendered but could not be read.');
                    return;
                }

                response.writeHead(200, {
                    'Content-Type': 'image/png',
                    'Content-Length': data.length,
                    // Immutable: the renderer version is in the path, so a change of pixels is a
                    // change of URL and there is nothing to invalidate.
                    'Cache-Control': 'public, max-age=86400'
                });

                response.end(data);
            });
        },
        (error) => sendError(response, 503, error.message));
}

/**
 * Serves one pick sidecar: which world tile and standing Z each pixel of a tile belongs to.
 *
 * SENT STILL COMPRESSED. The file is gzipped on disk and goes out with Content-Encoding: gzip, so
 * the browser inflates it natively and fetch().arrayBuffer() hands over the exact bytes. That
 * exactness is the whole point of not making this a PNG: a picture would have to come back through
 * a canvas, which premultiplies alpha and applies colour management, and a pick map that is nearly
 * right is a waypoint three tiles from where it was clicked.
 *
 * There is no empty answer to serve here. An items tile with nothing in it composes to exactly the
 * map layer's pick, and art.pick() resolves the map layer's file for it rather than inventing a
 * placeholder - so by the time this runs there is always a real file.
 */
function serveArtPick(parts, response) {
    art.pick(parts.layer, parts.floor, parts.level, parts.x, parts.y).then(
        (file) => {
            fs.readFile(file, (error, data) => {
                if (error) {
                    sendError(response, 500, 'The pick map was rendered but could not be read.');
                    return;
                }

                response.writeHead(200, {
                    'Content-Type': 'application/octet-stream',
                    'Content-Encoding': 'gzip',
                    'Content-Length': data.length,
                    // Immutable for the same reason the tiles are: the renderer version is in the
                    // path, so a change of content is a change of URL.
                    'Cache-Control': 'public, max-age=86400'
                });

                response.end(data);
            });
        },
        (error) => sendError(response, 503, error.message));
}

/**
 * Reads a request body with a real limit and a real answer when it is exceeded.
 *
 * Over the limit, the rest of the body is drained and thrown away rather than the socket being
 * destroyed. Destroying it aborts the response too, so the caller gets a connection error instead
 * of the 413 explaining what happened - which is how 5a's token drop behaved, and it looked
 * exactly like the bridge having crashed.
 */
function readBody(request, limit) {
    return new Promise((resolve, reject) => {
        const chunks = [];
        let size = 0;
        let over = false;

        request.on('data', (chunk) => {
            if (over) {
                return;
            }

            size += chunk.length;

            if (size > limit) {
                over = true;
                chunks.length = 0;
                reject(Object.assign(new Error('That is too large to send.'), { status: 413 }));
                return;
            }

            chunks.push(chunk);
        });

        request.on('end', () => {
            if (!over) {
                resolve(Buffer.concat(chunks).toString('utf8'));
            }
        });

        request.on('error', (error) => {
            if (!over) {
                reject(error);
            }
        });
    });
}

// ---- the request channel (REVIEW.md F3) -----------------------------------------------------------
//
// EVERY REQUEST HAS AN ID, and the id is in the file name: Data/Live/requests/<name>.<id>.token,
// answered by <name>.<id>.ack.json with the id echoed inside. Until 22 September 2026 the token was
// <name>.token - one fixed file per operation - so a second request published while the shard was
// running the first was deleted, unread, by the first one's cleanup, and two requests dropped
// between two polls collapsed into one. Session 1's nonce (a `#abcd1234` on the body) let a waiter
// refuse somebody else's ack; it could not keep the request alive. The nonce is gone with the fixed
// name: the id does both jobs, and the body is arguments only.
//
// WHAT THE DISK SAYS ABOUT A REQUEST, which is what makes the outcome vocabulary honest here:
//
//   <name>.<id>.token     queued - the shard has not looked at it. The bridge may WITHDRAW it (a
//                         delete, which races the shard's claim atomically: one of the two wins),
//                         and then the outcome is NotRun - nothing happened, safe to ask again.
//   <name>.<id>.claimed   running - the shard renamed the token before dispatching. A waiter that
//                         gives up now reports Unknown.
//   <name>.<id>.ack.json  done - completed or faulted, in the ack's own words.
//   none of the three     Unknown: a boot swept it, or the ack was pruned, or it never existed.
//
// AN ACK MATCHES A REQUEST ONLY ON ITS ID AND THE SHARD'S CURRENT BOOT. The current boot is what
// Data/Live/health.json says - the shard's own statement, rewritten by every boot within seconds
// and deleted by the poller's boot sweep so it is absent rather than stale in between. An ack with
// no id, another id, or another boot's id is IGNORED and reported (it is somebody else's answer, or
// a previous process's), never taken as this request's. See matchAck.
//
// NotRun is the one outcome the bridge retries on its own, once, because it is the one outcome
// that says nothing happened. Unknown is never retried: re-query the state instead (LoopQueue.cs,
// the outcome contract).

/** Twelve hex characters: enough that two requests in a session never share one. */
function newRequestId() {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
}

/**
 * Writes a request token, and returns its id.
 *
 * PUBLISHED BY RENAME, onto a name nobody else can be using. fs.writeFileSync truncates and then
 * writes, so a poll landing between the two reads an empty or half-written token; a rename within
 * one directory is atomic on NTFS and POSIX alike, so the shard sees the whole file or no file.
 * AtomicFile.Write does exactly this from the other side, for the same reason.
 *
 * ONE UNCLAIMED TOKEN PER OPERATION (session 1's interim guard, kept as policy): an unclaimed token
 * for this operation is a request the shard has not looked at yet, and a second one behind it
 * would only queue. A 409 says so. Per OPERATION rather than global: a nav-reload has no reason to
 * refuse because a botinfo is pending. A CLAIMED token does not count - that request is running,
 * and the next one queues behind it in its own file, which is the whole point of the id.
 */
function writeToken(name, body, id) {
    if (!whitelist.isRequestName(name)) {
        throw Object.assign(new Error('Bad request name.'), { status: 400 });
    }

    fs.mkdirSync(whitelist.REQUEST_DIR, { recursive: true });

    const pending = whitelist.listTokens(name);

    if (pending.length > 0) {
        throw Object.assign(
            new Error(`The shard has not picked up the last '${name}' request yet (${pending[0]}).`),
            { status: 409 });
    }

    const requestId = id || newRequestId();
    const file = whitelist.resolveToken(name, requestId);

    if (!file) {
        throw Object.assign(new Error('Bad request id.'), { status: 400 });
    }

    // A unique staging name, so two writers cannot collide on the temp file either, and in the
    // same directory, because a rename across volumes is a copy and is not atomic. It does not
    // end in .token, so the poller's name filter never sees it.
    const staged = `${file}.${crypto.randomUUID().slice(0, 8)}.tmp`;

    try {
        fs.writeFileSync(staged, (body || '').trim() + '\n', 'utf8');
        fs.renameSync(staged, file);
    } catch (error) {
        fs.rmSync(staged, { force: true });
        throw error;
    }

    return requestId;
}

/** An ack that may be half-written or absent is simply not an ack yet. */
function readAck(file) {
    try {
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch {
        return null;
    }
}

/**
 * The shard's current boot, as it states it in Data/Live/health.json, or null when there is no
 * such file - a shard that has not written one yet (the poller's boot sweep deletes the previous
 * boot's, and the first write is five seconds in), or one that never booted against this tree.
 * Read fresh each time; a cached value would be exactly the stale bootId this exists to refuse.
 */
function currentBootId() {
    const health = readAck(whitelist.FILES.health);

    return health && typeof health.bootId === 'string' && health.bootId.length > 0 ? health.bootId : null;
}

/**
 * Whether an ack answers THIS request. Pure, exported for the tests.
 *
 * Returns {matched: true}, or {matched: false, reason} where reason is null for "not an ack yet"
 * and a sentence for an ack that exists and is somebody else's: no id, another id, or a boot that
 * is not the one health.json names. Those are reported to the caller, never acted on.
 */
function matchAck(ack, id, bootId) {
    if (!ack || !ack.utc) {
        return { matched: false, reason: null };
    }

    if (typeof ack.id !== 'string' || ack.id.length === 0) {
        return {
            matched: false,
            reason: `an ack with no request id (for '${ack.request || '?'}'); a shard from before the request-id protocol?`
        };
    }

    if (ack.id !== id) {
        return { matched: false, reason: `an ack for request ${ack.id}, not ${id}` };
    }

    if (bootId && ack.bootId !== bootId) {
        return {
            matched: false,
            reason: ack.bootId
                ? `an ack from boot ${ack.bootId}, but the shard is at boot ${bootId} (health.json)`
                : `an ack with no bootId while the shard is at boot ${bootId} (health.json)`
        };
    }

    return { matched: true };
}

/** Where a request with no ack stands, read off the disk. */
function requestState(name, id) {
    if (fs.existsSync(whitelist.resolveToken(name, id))) {
        return 'queued';
    }

    if (fs.existsSync(whitelist.resolveClaimed(name, id))) {
        return 'running';
    }

    return 'gone';
}

/**
 * Waits for the ack to request `id`, or gives up and says which of NotRun and Unknown it is.
 *
 * Resolves {outcome, ack, id, ignored, message}. `outcome` is completed or faulted when an ack
 * matched (faulted is the ack's ok:false), notrun when the token was still unclaimed at the
 * timeout and was withdrawn, unknown otherwise. `ignored` lists every ack that was seen and
 * refused, with the reason. A matched ack is deleted: it has been read by the one process it was
 * for. A withdraw that finds the token already claimed - the shard got there between the check
 * and the delete - polls two more seconds for the ack before giving up as unknown.
 */
function waitForAck(name, id, timeoutMs) {
    const ackFile = whitelist.resolveAck(name, id);
    const tokenFile = whitelist.resolveToken(name, id);
    const deadline = Date.now() + timeoutMs;
    const ignored = [];
    const seconds = Math.round(timeoutMs / 1000);

    let grace = null;

    return new Promise((resolve) => {
        const poll = () => {
            const ack = readAck(ackFile);

            if (ack) {
                const verdict = matchAck(ack, id, currentBootId());

                if (verdict.matched) {
                    fs.rmSync(ackFile, { force: true });

                    resolve({
                        outcome: ack.outcome === 'notrun' ? 'notrun' : (ack.ok === true ? 'completed' : 'faulted'),
                        ack, id, ignored, message: ack.message || ''
                    });
                    return;
                }

                if (verdict.reason && !ignored.includes(verdict.reason)) {
                    ignored.push(verdict.reason);
                    console.log(`request: ${name} ${id} ignored ${verdict.reason}`);
                }
            }

            const now = Date.now();

            if (now < deadline || (grace !== null && now < grace)) {
                setTimeout(poll, 200);
                return;
            }

            if (grace === null && fs.existsSync(tokenFile)) {
                try {
                    // The withdraw. Without `force`, so a token the shard claimed a moment ago
                    // throws rather than "succeeds" - and then the request is running, not unrun.
                    fs.rmSync(tokenFile);

                    resolve({
                        outcome: 'notrun', ack: null, id, ignored,
                        message: `The shard did not pick up the ${name} request within ${seconds}s; it was withdrawn and nothing ran.`
                    });
                    return;
                } catch {
                    grace = Date.now() + 2000;
                    setTimeout(poll, 200);
                    return;
                }
            }

            resolve({
                outcome: 'unknown', ack: null, id, ignored,
                message: `The shard did not answer the ${name} request within ${seconds}s. Its outcome is unknown - `
                    + 'it may have run, or still be running.'
                    + (ignored.length > 0 ? ` Ignored: ${ignored.join('; ')}.` : '')
            });
        };

        poll();
    });
}

/**
 * Drops a token and waits for its ack: the one helper every caller in this file uses.
 *
 * `retryNotRun` asks once more, with a fresh id, when the shard never picked the token up - the
 * one outcome that says nothing happened. The second attempt's result carries `retried` naming
 * the first id. Nothing else is ever retried here.
 */
async function requestAndWait(name, body, timeoutMs, options) {
    const first = await waitForAck(name, writeToken(name, body), timeoutMs);

    if (first.outcome !== 'notrun' || !(options && options.retryNotRun)) {
        return first;
    }

    console.log(`request: ${name} ${first.id} was never picked up; asking once more`);

    const second = await waitForAck(name, writeToken(name, body), timeoutMs);

    second.retried = first.id;

    return second;
}

/**
 * Drops a request token for the shard's poller.
 *
 * The body, if any, becomes the token's contents - livemap-on carries its arguments that way.
 * The name is validated against a pattern rather than sanitised: a name that has to be cleaned
 * up before it is safe is a name worth refusing.
 */
async function dropToken(name, request, response) {
    let body;

    try {
        body = await readBody(request, MAX_BODY);
    } catch (error) {
        sendError(response, error.status || 400, error.message);
        return;
    }

    try {
        const id = writeToken(name, body);

        // The id goes back so the caller's own ack poll asks for this request and no other.
        sendJson(response, 202, { ok: true, request: name, id });
    } catch (error) {
        sendError(response, error.status || 500, error.message);
    }
}

// ---- restarting the shard ------------------------------------------------------------------------
//
// THE ONLY THING IN THIS BRIDGE THAT TOUCHES THE SHARD PROCESS, and the only one that can leave it
// down - which is why it was left out of the editor until now (see the README).
//
// The sequence is dev.ps1's, in order, and each step exists because skipping it loses something:
//
//   save      -> and WAIT FOR THE ACK. HandleClosed does not save on exit; it only waits for writes
//                already in flight. Killing without this loses everything since the last autosave.
//   shutdown  -> the shard writes its ack BEFORE Core.Kill, so "did it hear us" is answerable.
//   wait      -> for ServUO.exe to actually go. Scripts.dll is locked while loaded, so a build
//                started too early fails on a file lock rather than on anything real.
//   start     -> tools/restart-shard.ps1, which runs build.ps1 - so a restart REBUILDS, and a
//                failed build still refuses to launch. That is the whole reason this shard has
//                build scripts (CLAUDE.md section 2), and a restart that skipped it would be the
//                one path back around the hole they exist to close.
//   watch     -> for the shard to answer again, which is health.json moving.
//
// The bridge spawns exactly ONE known script and passes it nothing. artrenderer.js:50 is the
// existing precedent for the bridge spawning a child at all.
const RESTART_SCRIPT = path.join(whitelist.REPO_ROOT, 'tools', 'restart-shard.ps1');

/** Progress, polled by the panel. Deliberately a plain object: there is only ever one restart. */
let restartState = { running: false, stage: 'idle', message: '', startedAt: null, ok: null };

function restartStep(stage, message, extra) {
    restartState = Object.assign({}, restartState, { stage, message }, extra || {});
    console.log(`restart: ${stage} - ${message}`);
}

/** True while ServUO.exe is still in the process table. */
function shardIsUp() {
    try {
        const { execFileSync } = require('child_process');
        const out = execFileSync(
            'tasklist', ['/FI', 'IMAGENAME eq ServUO.exe', '/NH'], { encoding: 'utf8' });

        return /ServUO\.exe/i.test(out);
    } catch {
        // Cannot tell. Reported as still up, so the caller waits rather than racing a live process
        // into a build that will fail on a locked Scripts.dll.
        return true;
    }
}

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Runs tools/restart-shard.ps1 and waits for IT to finish - not for the shard it launches.
 *
 * `powershell.exe`, with the extension, and the pipes read rather than ignored. Both are the same
 * bug, found by this failing in the least useful way possible: `spawn('powershell', ...)` without
 * `shell: true` does not consult PATHEXT, so it raised ENOENT - onto an `error` event nothing was
 * listening for, with `stdio: 'ignore'` throwing the evidence away. The restart then sat in
 * "waiting" for its full timeout and reported that the shard had not come back, which is true
 * and says nothing about why. A launcher that cannot say it failed to launch is worse than no
 * launcher.
 *
 * The script starts the shard in its own window and returns immediately, so this resolves in
 * about a second; the wait for the shard itself is the caller's loop.
 */
function startShard() {
    return new Promise((resolve, reject) => {
        const { execFile } = require('child_process');

        execFile(
            'powershell.exe',
            ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', RESTART_SCRIPT],
            { cwd: whitelist.REPO_ROOT, windowsHide: true, timeout: 30000 },
            (error, stdout, stderr) => {
                const said = String(stdout || '').trim() || String(stderr || '').trim();

                if (error) {
                    reject(new Error(
                        `Could not start the shard: ${said || error.message}`));
                    return;
                }

                console.log(`restart: launcher said - ${said}`);
                resolve(said);
            });
    });
}

async function handleRestart(rest, request, response) {
    if (restartState.running) {
        sendJson(response, 409, Object.assign({ error: 'A restart is already running.' }, restartState));
        return;
    }

    restartState = {
        running: true, stage: 'saving', message: 'Saving the world...',
        startedAt: new Date().toISOString(), ok: null
    };

    // Answered immediately and followed by polling /api/restart. The whole sequence is a build and
    // a boot - tens of seconds - and a held connection would time out somewhere in the middle and
    // leave the caller unable to tell a slow restart from a failed one.
    sendJson(response, 202, restartState);

    runRestart().catch((error) => {
        restartStep('failed', error.message, { running: false, ok: false });
    });
}

// ---- what an ack has to say before the restart believes it -------------------------------------
//
// Three pure judgements, exported so the tests can drive them with hand-written acks. The rule
// they share (Scripts/Custom/Core/LoopQueue.cs, "the outcome contract"): an ack with ok:true means
// the operation completed; ok:false means it failed and the message says where; NO ack means the
// outcome is UNKNOWN - never done, never failed - and nothing that depends on it may proceed.
//
// The numbers: every ack carries `generation`, the completed persistence generation, and `bootId`,
// the process that answered. "Saved" is a generation that advanced; "stopped" is the same process
// stopping at that generation or later; "back" is a DIFFERENT process at the same generation.

/** The save leg. `ack` is the parsed save.ack.json, or null when none arrived in time. */
function judgeSaveAck(ack) {
    if (!ack) {
        return {
            ok: false,
            reason: 'The shard did not answer the save within 60s. Its outcome is unknown - it may '
                + 'have saved, or still be saving. Nothing was stopped.'
        };
    }

    if (!ack.ok) {
        return { ok: false, reason: `The shard refused to save: ${ack.message}` };
    }

    if (!Number.isInteger(ack.generation)) {
        return {
            ok: false,
            reason: `The save ack carries no generation (${ack.message}); an ack that cannot say `
                + 'which save completed is not treated as saved.'
        };
    }

    return { ok: true, generation: ack.generation, bootId: ack.bootId || null, message: ack.message };
}

/** The shutdown leg. `saved` is judgeSaveAck's answer for the save this shutdown follows. */
function judgeShutdownAck(ack, saved) {
    if (!ack) {
        return {
            ok: false,
            reason: 'The shard did not acknowledge the shutdown within 30s. Its outcome is unknown - '
                + 'it may still be running, or be stopping now. Nothing was started.'
        };
    }

    if (!ack.ok) {
        return { ok: false, reason: `The shard refused to stop: ${ack.message}` };
    }

    if (saved.bootId && ack.bootId && ack.bootId !== saved.bootId) {
        return {
            ok: false,
            reason: `A different shard (boot ${ack.bootId}) answered the shutdown than the one that `
                + `saved (boot ${saved.bootId}). Nothing was started.`
        };
    }

    if (Number.isInteger(ack.generation) && ack.generation < saved.generation) {
        return {
            ok: false,
            reason: `The shutdown ack says generation ${ack.generation}, but the save completed `
                + `${saved.generation}. Nothing was started.`
        };
    }

    return {
        ok: true,
        generation: Number.isInteger(ack.generation) ? ack.generation : saved.generation,
        bootId: ack.bootId || null
    };
}

/**
 * The boot leg: the first health ack after the new process is up. `stopped` is
 * judgeShutdownAck's answer. Returns `warning` rather than failing when the shard saved again
 * before it stopped (an autosave in the gap), because that generation is later and complete.
 */
function judgeBootAck(ack, stopped) {
    if (!ack || !ack.ok) {
        return { ok: false, reason: 'The shard answered the health request with a failure.' };
    }

    if (stopped.bootId && ack.bootId && ack.bootId === stopped.bootId) {
        return {
            ok: false,
            reason: `The process that answered (boot ${ack.bootId}) is the one that was asked to `
                + 'stop. It did not stop.'
        };
    }

    if (!Number.isInteger(ack.generation)) {
        return { ok: false, reason: `The shard is up but its ack carries no generation (${ack.message}).` };
    }

    if (ack.generation < stopped.generation) {
        return {
            ok: false,
            reason: `The shard booted at generation ${ack.generation} but the save completed `
                + `${stopped.generation}. A backup was restored, or Saves/Custom/ACCEPT was used; `
                + 'the world on disk is not the one that was saved.'
        };
    }

    if (ack.generation > stopped.generation) {
        return {
            ok: true,
            generation: ack.generation,
            warning: `The shard is up at generation ${ack.generation}, later than the ${stopped.generation} `
                + 'this restart saved: it saved again before it stopped.'
        };
    }

    return { ok: true, generation: ack.generation };
}

/**
 * The refusal PersistenceGeneration writes when it will not load Saves/, if it was written after
 * `since`. The shard exits before it has a console tap or a health file, so this is the only trace
 * the bridge can read - and reading it is what lets a restart fail in one second with the shard's
 * own words instead of asking a dead process for two to six minutes.
 */
function readBootRefusal(since) {
    const refusal = readJson(whitelist.FILES.bootRefusal);

    if (!refusal || !refusal.utc || !refusal.message) {
        return null;
    }

    if (since && Date.parse(refusal.utc) < Date.parse(since)) {
        return null;
    }

    return refusal;
}

/**
 * The words for a request that never got an answer, before the leg's own judge speaks: a token
 * the shard never picked up (twice) is NotRun and says so; anything else is the judge's "unknown".
 */
function unanswered(result, what) {
    if (result.outcome === 'notrun') {
        return `The shard did not pick up the ${what} request (${result.retried} and then ${result.id}); `
            + 'nothing ran. Is it running?';
    }

    return null;
}

async function runRestart() {
    const saveResult = await requestAndWait('save', '', 60000, { retryNotRun: true });
    const saved = judgeSaveAck(saveResult.ack);

    if (!saved.ok) {
        throw new Error(unanswered(saveResult, 'save') || saved.reason + ignoredSuffix(saveResult));
    }

    restartStep('stopping',
        `Saved generation ${saved.generation} (${saved.message}, request ${saveResult.id}). Stopping the shard...`,
        { generation: saved.generation, bootId: saved.bootId });

    const stopResult = await requestAndWait('shutdown', '', 30000, { retryNotRun: true });
    const stopped = judgeShutdownAck(stopResult.ack, saved);

    if (!stopped.ok) {
        throw new Error(unanswered(stopResult, 'shutdown') || stopped.reason + ignoredSuffix(stopResult));
    }

    for (let attempt = 0; attempt < 60 && shardIsUp(); attempt++) {
        await wait(1000);
    }

    if (shardIsUp()) {
        throw new Error('ServUO.exe is still running after 60s. Nothing was started.');
    }

    restartStep('building',
        `Stopped at generation ${stopped.generation}. Building and starting (build.ps1 refuses a failed build)...`,
        { generation: stopped.generation });

    const launchedAt = new Date().toISOString();

    await startShard();

    restartStep('waiting', `Waiting for the shard to answer at generation ${stopped.generation}...`);

    // The health snapshot is the shard saying it is up, and asking for a fresh one is the same
    // request the Health panel already makes.
    //
    // 120 ATTEMPTS, NOT TWO MINUTES. Each attempt sleeps a second, and once ServUO.exe is up it
    // also waits up to two more for the health ack - so the ceiling is nearer six minutes than
    // two, and the floor is two. Three messages here used to say two minutes flat. The attempt
    // count is deliberately kept rather than converted to a wall-clock deadline: a build plus a
    // world load can legitimately run past two minutes, and what this loop is really bounding is
    // the number of times it is willing to ask.
    for (let attempt = 0; attempt < 120; attempt++) {
        await wait(1000);

        if (!shardIsUp()) {
            // A process that came and went may have REFUSED the tree. PersistenceGeneration writes
            // its refusal to Data/Live before it exits, and that is the answer, not a timeout.
            const refusal = readBootRefusal(launchedAt);

            if (refusal) {
                throw new Error(`The shard refused to boot (exit ${refusal.exitCode}):\n${refusal.message}`);
            }

            continue;
        }

        let probe;

        try {
            // Two seconds, then withdrawn (NotRun) or given up on (Unknown - a token the boot
            // sweep took, or a poller not yet started). Both are "ask again next second"; only
            // an ack is an answer.
            probe = await requestAndWait('health', '', 2000);
        } catch (error) {
            // The previous poll's token is still on disk, which is the normal state of a shard
            // that is not up yet. Wait for the next second rather than treating it as an answer.
            if (error.status === 409) {
                continue;
            }

            throw error;
        }

        if (!probe.ack) {
            continue;
        }

        const back = judgeBootAck(probe.ack, stopped);

        if (!back.ok) {
            throw new Error(back.reason);
        }

        restartStep('done',
            back.warning
                ? `The shard is up. ${back.warning}`
                : `The shard is up at generation ${back.generation}, the generation it saved.`,
            { running: false, ok: true, generation: back.generation });
        return;
    }

    throw new Error('The shard did not answer after 120 attempts (at least two minutes, up to about six). Check its window.');
}

// ---- writing a data file: the shard commits, the bridge stages ----------------------------------
//
// THE SHARD IS THE WRITER of the editor's data files (from 22 September 2026; REVIEW.md, "Restore
// has a lost-update hole"). Until then this file wrote navigation.json itself after checking an
// OPTIONAL base hash, and restore checked nothing; and the check and the rename were two steps in
// one process while [NavRecord, in the other process, could write the same file between them. Now
// the bridge computes the new text, writes it BESIDE the target as <file>.<id>.staged, and drops a
// `commit` token carrying the version the save was based on; the shard - on the game thread, where
// every other writer of these files runs - compares, replaces, keeps the old file as .bak, reloads,
// and answers in its own words, naming who last wrote the file and when if it refuses.
//
// The version is REQUIRED. A save that cannot say what it was based on cannot be told whether that
// is still what is there, and "changed on disk since you loaded it" was only ever asked of savers
// who volunteered a hash. `null` means "this file does not exist yet" (the Spawner tool creating a
// file), which the shard spells `none`.
//
// WHAT THE ANSWERS MEAN (the outcome contract, LoopQueue.cs):
//   200 written:true reloaded:true      committed and reloaded. Carries id, generation, hash, backupHash.
//   200 written:true reloaded:false     committed; the shard refused to load it and says why. The
//                                       persistent banner's case: the file on disk is not what the
//                                       shard is running, and both halves have to reach the editor.
//   409                                 refused, nothing written: `error` is the shard's own message,
//                                       with expected/actual/changedAt/changedBy beside it.
//   503 outcome:notrun                  the shard never picked the commit up, twice; nothing written,
//                                       the staged file removed. A save with the shard down lands here.
//   504 outcome:unknown                 the shard claimed the commit and never answered, and the
//                                       file on disk is not the version this save wrote. Not written
//                                       as far as anyone can tell; never retried by the bridge.
//   200 outcome:unknown written:true    the same, but the file on disk IS the version this save
//                                       wrote: committed, reload unknown.

/** The base version a save or restore sent, as the shard spells it, or null when it sent nothing usable. */
function baseVersionOf(value) {
    if (value === null) {
        return 'none';
    }

    return typeof value === 'string' && /^[0-9a-f]{16}$/.test(value) ? value : null;
}

/** "; ignored: ..." for a response, when acks were seen and refused along the way. */
function ignoredSuffix(result) {
    return result.ignored && result.ignored.length > 0 ? ` Ignored: ${result.ignored.join('; ')}.` : '';
}

/** The common tail of every commit and restore answer. */
function stamp(result) {
    return {
        id: result.id,
        outcome: result.outcome,
        generation: result.ack && Number.isInteger(result.ack.generation) ? result.ack.generation : null,
        ignored: result.ignored || []
    };
}

/**
 * Stages `next` beside the file and asks the shard to commit it. Returns {status, body}.
 *
 * Retries once on NotRun with a fresh id and a fresh staged copy (the staged name carries the
 * id). The staged file is removed on NotRun and on any refusal - the shard removes it on its own
 * refusals too, and the boot sweep removes what neither of us could.
 */
async function commitFile(name, file, base, next, reload) {
    const wrote = hashOf(next);
    const display = path.basename(file);
    const seconds = Math.round(ACK_TIMEOUT_MS / 1000);

    let result = null;
    let retried = null;

    for (let attempt = 0; attempt < 2; attempt++) {
        const id = newRequestId();
        const staged = whitelist.resolveStaged(name, id);

        fs.writeFileSync(staged, next, 'utf8');

        try {
            result = await waitForAck(
                'commit',
                writeToken('commit', `file=${name} base=${base} wrote=${wrote} reload=${reload ? 'yes' : 'no'}`, id),
                ACK_TIMEOUT_MS);
        } catch (error) {
            fs.rmSync(staged, { force: true });
            throw error;
        }

        if (result.outcome !== 'notrun') {
            break;
        }

        fs.rmSync(staged, { force: true });
        retried = id;
        console.log(`commit: ${name} ${id} was never picked up; asking once more`);
    }

    result.retried = retried;

    if (result.outcome === 'notrun') {
        return {
            status: 503,
            body: {
                error: `The shard did not pick up the save (requests ${retried} and ${result.id}); nothing was written. Is it running?`,
                written: false,
                ...stamp(result)
            }
        };
    }

    if (result.outcome === 'unknown') {
        const now = versionOf(file);
        const written = now === wrote;

        return {
            status: written ? 200 : 504,
            body: written
                ? {
                    written: true,
                    reloaded: false,
                    hash: wrote,
                    backupHash: versionOf(file + '.bak'),
                    backup: display + '.bak',
                    message: `${result.message} The file on disk is the version this save wrote; whether the shard `
                        + 'reloaded it is unknown - it may have, or may still be doing so.',
                    errors: [],
                    warnings: [],
                    ...stamp(result)
                }
                : {
                    error: `${result.message} The file on disk is not the version this save wrote (it is at ${now}); `
                        + 'the commit may still run. Reload before saving again.',
                    written: false,
                    ...stamp(result)
                }
        };
    }

    const ack = result.ack;
    const commit = ack.commit || {};

    if (commit.refused) {
        return {
            status: 409,
            body: {
                error: ack.message,
                expected: commit.expected,
                actual: commit.actual,
                changedAt: commit.changedAt,
                changedBy: commit.changedBy,
                written: false,
                ...stamp(result)
            }
        };
    }

    if (!commit.written) {
        return { status: 500, body: { error: ack.message, written: false, ...stamp(result) } };
    }

    return {
        status: 200,
        body: {
            written: true,
            reloaded: commit.reloaded === true,
            hash: commit.hash,
            backupHash: commit.backupHash,
            backup: display + '.bak',
            message: ack.message || '',
            errors: ack.errors || [],
            warnings: ack.warnings || [],
            ...stamp(result)
        }
    };
}

/**
 * Saves one data file: computes the new text, stages it, and asks the shard to commit it.
 *
 * The dry run validates. A real save does NOT: validate.js is a replica of the shard's rules, and
 * a false positive in a replica must never be able to stop someone writing a file the shard would
 * have accepted. The shard is the authority, and it gets to say no itself - about the content on
 * the reload, and, since 22 September 2026, about the version too.
 */
async function handleSave(name, request, response) {
    const file = whitelist.resolveSave(name);

    if (!file) {
        sendError(response, 400, `Unknown file '${name}'.`);
        return;
    }

    let payload;

    try {
        payload = JSON.parse(await readBody(request, MAX_SAVE_BODY));
    } catch (error) {
        sendError(response, error.status || 400, error.status ? error.message : 'Malformed request body.');
        return;
    }

    // After the name, so "Unknown file" still wins for a name that is not one; before anything
    // else, because a save that cannot say what it was based on is not a save this bridge makes.
    const base = baseVersionOf(payload.baseHash);

    if (base === null) {
        sendError(response, 400,
            `A save has to say which version of ${path.basename(file)} it is based on: baseHash from /api/shapes `
            + 'or /api/spawners, or null for a file that does not exist yet.');
        return;
    }

    const current = readText(file);
    const relative = whitelist.spawnRelative(name);

    let next;

    try {
        // Two writers, because there are two file formats. Which one is decided by the name, and
        // the name is a key rather than a path, so there is nothing to sniff.
        next = relative
            ? spawners.unproject(relative, current || emptySpawnFile(), payload)
            : unproject(name, current, payload);
    } catch (error) {
        sendError(response, 400, error.message);
        return;
    }

    if (payload.dryRun) {
        const problems = relative
            ? { fatal: [], warnings: spawnFindings(relative, next) }
            : await validateText(name, next);

        sendJson(response, problems.fatal.length > 0 ? 422 : 200, { dryRun: true, ...problems });
        return;
    }

    const answer = await commitFile(name, file, base, next, payload.reload !== false);

    sendJson(response, answer.status, answer.body);
}

/**
 * Puts a file back to the .bak, which is what makes "discard" mean discard - and only the .bak the
 * caller means, over only the file the caller saw.
 *
 * A save whose reload was rejected has already written to disk. Dropping the editor's local edits
 * alone would leave that file in place to fail at the next restart, so the file has to go back
 * too - and the .bak is the pre-save bytes exactly, which no reconstruction from shapes can
 * promise. The body is {baseHash, backupHash}: the version the live file must still be at, and the
 * version the .bak must be at (the `backupHash` the save that made it answered with). The shard
 * checks both and refuses in its own words; a stale tab cannot put back a backup that is not the
 * one it remembers, over a file somebody else has since written.
 */
async function handleRestore(name, request, response) {
    const file = whitelist.resolveSave(name);
    const backup = whitelist.resolveBackup(name);

    if (!file) {
        sendError(response, 400, `Unknown file '${name}'.`);
        return;
    }

    if (!fs.existsSync(backup)) {
        sendError(response, 409, 'There is no backup to restore - nothing has been saved yet.');
        return;
    }

    let payload;

    try {
        const text = await readBody(request, MAX_BODY);

        payload = text.trim().length === 0 ? {} : JSON.parse(text);
    } catch (error) {
        sendError(response, error.status || 400, error.status ? error.message : 'Malformed request body.');
        return;
    }

    const base = baseVersionOf(payload.baseHash);
    const backupHash = baseVersionOf(payload.backupHash);

    if (base === null || base === 'none' || backupHash === null || backupHash === 'none') {
        sendError(response, 400,
            `A restore has to say which version of ${path.basename(file)} it is putting the backup over (baseHash) `
            + 'and which backup it means (backupHash, from the save that made it).');
        return;
    }

    const result = await requestAndWait(
        'restore', `file=${name} base=${base} backup=${backupHash}`, ACK_TIMEOUT_MS, { retryNotRun: true });

    if (result.outcome === 'notrun') {
        sendJson(response, 503, {
            error: `The shard did not pick up the restore (requests ${result.retried} and ${result.id}); nothing was written. Is it running?`,
            written: false,
            ...stamp(result)
        });
        return;
    }

    if (result.outcome === 'unknown') {
        sendJson(response, 504, {
            error: `${result.message} ${path.basename(file)} is at ${versionOf(file)} now. Reload before deciding anything.`,
            written: false,
            ...stamp(result)
        });
        return;
    }

    const ack = result.ack;
    const commit = ack.commit || {};

    if (commit.refused) {
        sendJson(response, 409, {
            error: ack.message,
            expected: commit.expected,
            actual: commit.actual,
            changedAt: commit.changedAt,
            changedBy: commit.changedBy,
            written: false,
            ...stamp(result)
        });
        return;
    }

    if (!commit.written) {
        sendJson(response, 500, { error: ack.message, written: false, ...stamp(result) });
        return;
    }

    sendJson(response, 200, {
        written: true,
        restored: true,
        reloaded: commit.reloaded === true,
        hash: commit.hash,
        backupHash: commit.backupHash,
        backup: path.basename(file) + '.bak',
        message: ack.message || '',
        errors: ack.errors || [],
        warnings: ack.warnings || [],
        ...stamp(result)
    });
}

/**
 * The skeleton a brand-new spawn file starts from.
 *
 * A spawn file with no <Points> is written by [XmlSave as zero bytes, which its own reader then
 * throws on - so this is only ever a scaffold for a create to append to, and stringify refuses to
 * write it back out empty.
 */
function emptySpawnFile() {
    return '<Spawns>\r\n</Spawns>';
}


/** What the spawn formats can say about a file. Structural only; the shard still decides. */
function spawnFindings(relative, text) {
    return spawners.findings([{ relative, text }]).map((message) => ({
        severity: 'warning', where: relative, message, shapeId: null
    }));
}

/** The dry run: the same rules the browser previews with, over the text that would be written. */
async function validateText(name, text) {
    const { validateFile } = await validator;
    const nav = name === 'dailyLife' ? readJson(whitelist.FILES.navigation) || {} : null;

    return validateFile(name, JSON.parse(text), nav, { hopCap: configuredHopCap() });
}

function main() {
    let port = DEFAULT_PORT;

    for (let i = 2; i < process.argv.length; i++) {
        if (process.argv[i] === '--port') {
            port = Number(process.argv[++i]);
        }
    }

    const server = http.createServer(handleRequest);

    server.listen(port, HOST, () => {
        console.log(`Shard editor bridge on http://${HOST}:${port}/`);
        console.log(`Repo:  ${whitelist.REPO_ROOT}`);

        // The line a scripted caller needs, and the only place this secret is ever shown. The
        // browser does not need it - it is same-origin - so this is here for curl and for
        // anything else driving the bridge from a terminal. See the editor README.
        console.log(`Auth:  X-GG-Auth: ${SESSION_SECRET}   (mutating requests from non-browsers)`);

        if (!fs.existsSync(path.join(__dirname, 'tiles'))) {
            console.log('No tiles yet - run tools/editor/export-tiles.ps1');
        }

        if (!art.available) {
            console.log('No art renderer - build it with: dotnet build tools/MapExport/MapExport.csproj -c Release');
        }
    });

    // The renderer is a child process holding the client files open, so it has to go when the
    // bridge does. Without this a Ctrl-C leaves a MapExport.exe behind, and the next run's child
    // competes with it for the same cache files.
    for (const signal of ['SIGINT', 'SIGTERM']) {
        process.on(signal, () => {
            art.stop();
            process.exit(0);
        });
    }
}

if (require.main === module) {
    main();
}

module.exports = {
    MAX_SAVE_BODY,
    handleRequest, hostIsLocal, refusalFor, hashOf, versionOf, ACK_TIMEOUT_MS, SESSION_SECRET, art,
    judgeSaveAck, judgeShutdownAck, judgeBootAck,
    matchAck, requestAndWait, currentBootId, newRequestId
};
