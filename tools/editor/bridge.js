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
// SECURITY. It binds 127.0.0.1 only, so nothing off this machine can reach it. That alone is not
// enough - a page you visit in another tab can still POST to localhost - so every mutating
// request is checked for a same-origin Sec-Fetch-Site or a matching Origin. Reads are harmless
// and unchecked; the only writes are request tokens, and those run reload commands.

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

// A save body is the shapes that changed, not the file, but a big multi-select could still be a
// few hundred KB. Over the limit is an answer, not a dropped connection.
const MAX_BODY = 1024 * 1024;

// How long to wait for the shard to answer a reload. The poller ticks once a second, so this is
// four ticks of grace before the editor is told the shard did not answer. The override exists so
// the timeout path can be tested in a fraction of a second rather than five of them.
const ACK_TIMEOUT_MS = Number(process.env.GG_ACK_TIMEOUT_MS) || 5000;

// Requests whose token body a nonce can ride along in. Most ignore their body entirely; spawn-reload
// reads a file name off the front of it and stops at the first space, which leaves the tail free.
// livemap-on is NOT here: it parses its whole body, and a nonce would be an argument it did not
// ask for.
// `broadcast` is deliberately NOT here for a different reason from livemap-on's: its body is the
// message every player is about to read, and appending "#a1b2c3d4" to that is not a thing to do.
const NONCED = new Set([
    'nav-reload', 'dailylife-reload', 'zones-reload', 'health', 'gg-reimport', 'spawn-reload',
    'botpop-audit', 'botpop-gen', 'botinfo',
    'core-smoke', 'bot-smoke', 'bots-reload', 'shutdown', 'tile-probe', 'bot-pace'
]);

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
 * just recorded, and a reformat is a change too.
 */
function hashOf(text) {
    return crypto.createHash('sha256').update(text, 'utf8').digest('hex').slice(0, 16);
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
 * Refuses a cross-site write.
 *
 * Sec-Fetch-Site is sent by every current browser and is the reliable signal; Origin is the
 * fallback. A request with neither is allowed, because that is curl and the command line is not
 * the threat being defended against here.
 */
function isSameSite(request) {
    const site = request.headers['sec-fetch-site'];

    if (site) {
        return site === 'same-origin' || site === 'same-site' || site === 'none';
    }

    const origin = request.headers.origin;

    if (!origin) {
        return true;
    }

    return origin === `http://${HOST}:${request.socket.localPort}`
        || origin === `http://localhost:${request.socket.localPort}`;
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
            files[name] = { hash: hashOf(readText(whitelist.FILES[name])) };
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

    if (request.method === 'POST') {
        if (!isSameSite(request)) {
            sendError(response, 403, 'Refused: cross-site request.');
            return;
        }

        // Every POST is gated by the same-origin check above, before any of these are reached.
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

    if (pathname.startsWith('/api/ack/')) {
        const file = whitelist.resolveAck(pathname.slice('/api/ack/'.length));

        if (!file) {
            sendError(response, 400, 'Bad request name.');
            return;
        }

        sendJson(response, 200, readJson(file) || { pending: true });
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

/**
 * Writes a request token, and returns the nonce that identifies this run of it.
 *
 * TWO THINGS STOP A STALE ACK BEING READ AS THIS RUN'S ANSWER, and they close different holes.
 *
 * The ack file is never deleted by the shard - it is overwritten in place - and /api/ack reports
 * "pending" only when the file is ABSENT. So from the second request of a session onwards, a
 * waiter would read the previous run's ack instantly and believe it. Deleting the ack before
 * dropping the token fixes that in one line and needs no change on the shard.
 *
 * That still races two editor tabs, and loses the answer if the bridge dies mid-sequence. So the
 * token body also carries a nonce, which WriteAck echoes back in its `token` field. The nonce is
 * generated here rather than in the browser: the browser must not be able to choose it, and only
 * the bridge knows which requests can carry one.
 */
function writeToken(name, body) {
    const file = whitelist.resolveToken(name);

    if (!file) {
        throw Object.assign(new Error('Bad request name.'), { status: 400 });
    }

    const nonce = NONCED.has(name) ? crypto.randomUUID().slice(0, 8) : null;
    const text = [(body || '').trim(), nonce ? `#${nonce}` : ''].filter(Boolean).join(' ');
    const ack = whitelist.resolveAck(name);

    fs.mkdirSync(whitelist.REQUEST_DIR, { recursive: true });

    if (ack) {
        fs.rmSync(ack, { force: true });
    }

    fs.writeFileSync(file, text + '\n', 'utf8');

    return nonce;
}

/** An ack that may be half-written or absent is simply not an ack yet. */
function readAck(file) {
    try {
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch {
        return null;
    }
}

/** Waits for THIS run's ack, or gives up. Returns null on the timeout rather than throwing. */
function waitForAck(name, nonce, timeoutMs) {
    const file = whitelist.resolveAck(name);
    const deadline = Date.now() + timeoutMs;

    return new Promise((resolve) => {
        const poll = () => {
            const ack = readAck(file);
            const mine = ack && ack.utc
                && (!nonce || String(ack.token || '').endsWith(`#${nonce}`));

            if (mine) {
                resolve(ack);
                return;
            }

            if (Date.now() >= deadline) {
                resolve(null);
                return;
            }

            setTimeout(poll, 200);
        };

        poll();
    });
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
        const nonce = writeToken(name, body);

        // The nonce goes back so the caller's own ack poll can tell this run from the last one.
        sendJson(response, 202, { ok: true, request: name, nonce });
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
 * "waiting" for its full two minutes and reported that the shard had not come back, which is true
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

async function runRestart() {
    const saveNonce = writeToken('save', '');
    const saved = await waitForAck('save', saveNonce, 60000);

    if (!saved || !saved.ok) {
        throw new Error(saved
            ? `The shard refused to save: ${saved.message}`
            : 'The shard did not answer the save. Nothing was stopped.');
    }

    restartStep('stopping', `Saved (${saved.message}). Stopping the shard...`);

    const killNonce = writeToken('shutdown', '');
    const killed = await waitForAck('shutdown', killNonce, 30000);

    if (!killed || !killed.ok) {
        throw new Error('The shard did not acknowledge the shutdown. It is still running.');
    }

    for (let attempt = 0; attempt < 60 && shardIsUp(); attempt++) {
        await wait(1000);
    }

    if (shardIsUp()) {
        throw new Error('ServUO.exe is still running after 60s. Nothing was started.');
    }

    restartStep('building', 'Stopped. Building and starting (build.ps1 refuses a failed build)...');

    await startShard();

    restartStep('waiting', 'Waiting for the shard to answer...');

    // The health snapshot is the shard saying it is up, and asking for a fresh one is the same
    // request the Health panel already makes. Two minutes covers a build and a world load.
    for (let attempt = 0; attempt < 120; attempt++) {
        await wait(1000);

        if (!shardIsUp()) {
            continue;
        }

        const nonce = writeToken('health', '');
        const ack = await waitForAck('health', nonce, 2000);

        if (ack && ack.ok) {
            restartStep('done', 'The shard is up.', { running: false, ok: true });
            return;
        }
    }

    throw new Error('The shard did not answer within two minutes. Check its window.');
}

/** Runs the reload for a file and shapes the half of the answer that comes from the shard. */
async function reloadFor(name) {
    const request = whitelist.reloadFor(name);

    if (!request) {
        return { reloaded: false, message: 'Nothing reloads that file.', errors: [], warnings: [] };
    }

    // spawn-reload is the only request that takes an argument: the file to reload, relative to
    // Spawns/Custom and never a path. The shard resolves it against that root and refuses anything
    // landing outside - the same shape as the whitelist here, enforced on both sides rather than
    // trusted from one.
    const relative = whitelist.spawnRelative(name);
    const nonce = writeToken(request, relative || '');
    const ack = await waitForAck(request, nonce, ACK_TIMEOUT_MS);

    if (!ack) {
        return {
            reloaded: false,
            message: `The shard did not answer within ${ACK_TIMEOUT_MS / 1000}s. Is it running?`,
            errors: [],
            warnings: []
        };
    }

    return {
        reloaded: ack.ok === true,
        message: ack.message || '',
        errors: ack.errors || [],
        warnings: ack.warnings || []
    };
}

/**
 * Saves one data file, then asks the shard to reload it.
 *
 * WRITTEN AND NOT RELOADED IS A 200. The write happened; the shard is running the previous config
 * and has said why. Reporting that as an HTTP error would send the caller down the "nothing was
 * written" path, and the file on disk would disagree with what the editor believed - which is the
 * exact failure the persistent banner exists for.
 *
 * The dry run validates. A real save does NOT: validate.js is a replica of the shard's rules, and
 * a false positive in a replica must never be able to stop someone writing a file the shard would
 * have accepted. The shard is the authority, and it gets to say no itself.
 */
async function handleSave(name, request, response) {
    const file = whitelist.resolveSave(name);

    if (!file) {
        sendError(response, 400, `Unknown file '${name}'.`);
        return;
    }

    let payload;

    try {
        payload = JSON.parse(await readBody(request, MAX_BODY));
    } catch (error) {
        sendError(response, error.status || 400, error.status ? error.message : 'Malformed request body.');
        return;
    }

    const current = readText(file);
    const hash = hashOf(current);

    if (payload.baseHash !== undefined && payload.baseHash !== hash) {
        sendJson(response, 409, {
            error: `${path.basename(file)} changed on disk since you loaded it.`,
            expected: payload.baseHash,
            actual: hash
        });

        return;
    }

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

    if (fs.existsSync(file)) {
        fs.copyFileSync(file, file + '.bak');
    }

    // Temp file then rename, mirroring AtomicFile.Write on the shard side: the shard polls these
    // files on its own schedule, and a truncate-then-write leaves a window where it reads nothing.
    fs.writeFileSync(file + '.tmp', next, 'utf8');
    fs.renameSync(file + '.tmp', file);

    const outcome = payload.reload === false
        ? { reloaded: false, message: 'Written; not reloaded.', errors: [], warnings: [] }
        : await reloadFor(name);

    sendJson(response, 200, {
        written: true,
        hash: hashOf(next),
        backup: path.basename(file) + '.bak',
        ...outcome
    });
}

/**
 * Puts a file back to the .bak, which is what makes "discard" mean discard.
 *
 * A save whose reload was rejected has already written to disk. Dropping the editor's local edits
 * alone would leave that file in place to fail at the next restart, so the file has to go back
 * too - and the .bak is the pre-save bytes exactly, which no reconstruction from shapes can
 * promise.
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

    fs.copyFileSync(backup, file + '.tmp');
    fs.renameSync(file + '.tmp', file);

    const outcome = await reloadFor(name);

    sendJson(response, 200, {
        written: true,
        restored: true,
        hash: hashOf(readText(file)),
        ...outcome
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

module.exports = { handleRequest, isSameSite, hashOf, ACK_TIMEOUT_MS, art };
