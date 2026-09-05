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

const compact = require('./compact.js');
const { project } = require('./project.js');
const whitelist = require('./whitelist.js');

const DEFAULT_PORT = 8081;
const HOST = '127.0.0.1';

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

        sendJson(response, 200, { shapes });
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

        if (pathname.startsWith('/api/request/')) {
            dropToken(pathname.slice('/api/request/'.length), request, response);
            return;
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
 * Drops a request token for the shard's poller.
 *
 * The body, if any, becomes the token's contents - livemap-on carries its arguments that way.
 * The name is validated against a pattern rather than sanitised: a name that has to be cleaned
 * up before it is safe is a name worth refusing.
 */
function dropToken(name, request, response) {
    const file = whitelist.resolveToken(name);

    if (!file) {
        sendError(response, 400, 'Bad request name.');
        return;
    }

    let body = '';

    request.on('data', (chunk) => {
        body += chunk;

        if (body.length > 1024) {
            request.destroy();
        }
    });

    request.on('end', () => {
        try {
            fs.mkdirSync(whitelist.REQUEST_DIR, { recursive: true });
            fs.writeFileSync(file, body.trim() + '\n', 'utf8');

            sendJson(response, 202, { ok: true, request: name });
        } catch (error) {
            sendError(response, 500, error.message);
        }
    });
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

module.exports = { handleRequest, isSameSite };
