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
const whitelist = require('./whitelist.js');

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
const NONCED = new Set([
    'nav-reload', 'dailylife-reload', 'zones-reload', 'health', 'gg-reimport', 'spawn-reload'
]);

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
    '.ico': 'image/x-icon'
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

    '/api/health': (request, response) => {
        const health = readJson(whitelist.FILES.health);

        sendJson(response, 200, health || { utc: null, worst: 'Unknown', checks: [] });
    }
};

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
            ['/api/restore/', handleRestore]
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
    });
}

if (require.main === module) {
    main();
}

module.exports = { handleRequest, isSameSite, hashOf, ACK_TIMEOUT_MS };
