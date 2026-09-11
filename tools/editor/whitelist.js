'use strict';

// The path sandbox.
//
// The data endpoints take no path from the caller at all - every file they read is a constant
// below. That is the real sandbox: there is nothing to traverse because there is nothing to
// steer. This module exists for the one endpoint that does take a name (the request token drop)
// and so that the boundary is a thing that can be tested rather than a claim.

const fs = require('fs');
const path = require('path');

// Normally the repo this file lives in. GG_EDITOR_ROOT redirects every path below at once, which
// is how the tests run against a temp copy: Data/Custom is not gitignored, so a test that wrote to
// the real tree would be one crash away from a truncated navigation.json.
const REPO_ROOT = process.env.GG_EDITOR_ROOT
    ? path.resolve(process.env.GG_EDITOR_ROOT)
    : path.resolve(__dirname, '..', '..');

// Roots the bridge may read from. Anything resolving outside all of these is refused.
const READ_ROOTS = [
    path.join(REPO_ROOT, 'Data', 'Custom'),
    path.join(REPO_ROOT, 'Data', 'Live'),
    path.join(REPO_ROOT, 'Spawns', 'Custom'),
    path.join(__dirname)
];

// Where request tokens are dropped for the shard's poller to find.
//
// Until 5b this was the only directory the bridge wrote to at all. It now also writes the three
// data files below - but only those three, only by the fixed names in WRITABLE, and never by a
// name the caller supplies.
const REQUEST_DIR = path.join(REPO_ROOT, 'Data', 'Live', 'requests');

// '.md' is here so the Help panel can link the README's own step lists rather than keeping a
// second copy of them in the sidebar. Served as text/plain, so it reads in a tab.
const READ_EXTENSIONS = new Set(
    ['.json', '.xml', '.html', '.css', '.js', '.png', '.ico', '.svg', '.md']);

// Token names are matched against this rather than sanitised. A name that has to be cleaned up
// before it is safe is a name we should be rejecting.
const TOKEN_NAME = /^[a-z][a-z0-9-]{0,63}$/;

const FILES = {
    navigation: path.join(REPO_ROOT, 'Data', 'Custom', 'navigation.json'),
    dailyLife: path.join(REPO_ROOT, 'Data', 'Custom', 'britain-daily-life.json'),
    restrictedZones: path.join(REPO_ROOT, 'Data', 'Custom', 'restricted-zones.json'),
    entities: path.join(REPO_ROOT, 'Data', 'Live', 'entities.json'),
    health: path.join(REPO_ROOT, 'Data', 'Live', 'health.json'),
    spawnerState: path.join(REPO_ROOT, 'Data', 'Live', 'spawners.json'),
    navAudit: path.join(REPO_ROOT, 'Data', 'Live', 'nav-audit.json'),

    // [WalkAudit's sweep: every edge and arrival walked by real probe walkers, with the ratio of
    // steps to straight line and the cause of every failure. Read-only like every Data/Live file.
    walkAudit: path.join(REPO_ROOT, 'Data', 'Live', 'walk-audit.json'),
    siteReach: path.join(REPO_ROOT, 'Data', 'Live', 'site-reach.json'),
    navRoute: path.join(REPO_ROOT, 'Data', 'Live', 'nav-route.json'),
    botLog: path.join(REPO_ROOT, 'Data', 'Live', 'botlog.json'),
    navHop: path.join(REPO_ROOT, 'Data', 'Live', 'nav-hop.json'),
    navAdopt: path.join(REPO_ROOT, 'Data', 'Live', 'nav-adopt.json'),

    // One bot's [BotInfo report, pulled on demand rather than pushed with every entity poll:
    // forty lines of skill table for sixty bots twice a second would be a hundred kilobytes
    // to answer a question about one of them. Not in WRITABLE - the shard writes it.
    botInfo: path.join(REPO_ROOT, 'Data', 'Live', 'botinfo.json'),

    // The shard's console, teed into a ring buffer by ConsoleTap because the console is otherwise
    // only readable in the window it is printed in. Read-only: the shard writes both.
    console: path.join(REPO_ROOT, 'Data', 'Live', 'console.json'),
    logins: path.join(REPO_ROOT, 'Data', 'Live', 'logins.json'),

    // What the engine sees at a tile. Written by the `tile-probe` token, read by the inspector.
    tileProbe: path.join(REPO_ROOT, 'Data', 'Live', 'tile-probe.json'),

    // [BotPace's sampler lines. Its own file rather than a field on health.json, because the value
    // is in the LINES and a pace reported as one number is no answer.
    botPace: path.join(REPO_ROOT, 'Data', 'Live', 'bot-pace.json'),

    // Authoring input, not live data: uo-offline's navigation converted into our schema.
    // Read-only by construction - resolveSave has no entry for it, so the save endpoint
    // cannot name it however the request is spelled.
    reference: path.join(
        REPO_ROOT, 'Data', 'Custom', 'reference', 'uo-offline-nav.trammel.json')
};

/**
 * True when a path is inside one of the allowed roots.
 *
 * Resolves symlinks first: a link inside an allowed root that points outside it would otherwise
 * pass a prefix test while reading anywhere on disk. The separator is appended so that a sibling
 * directory whose name merely starts with an allowed root cannot match.
 */
function isAllowed(target) {
    let resolved;

    try {
        resolved = fs.realpathSync(target);
    } catch {
        // Does not exist: fall back to the lexical path so a missing file is reported as missing
        // rather than as a sandbox violation, which would be a confusing lie.
        resolved = path.resolve(target);
    }

    return READ_ROOTS.some(
        (root) => resolved === root || resolved.startsWith(root + path.sep)
    );
}

/** Resolves a URL path under the editor directory for static serving. */
function resolveStatic(urlPath) {
    const relative = decodeURIComponent(urlPath.split('?')[0]).replace(/^\/+/, '');
    const target = path.resolve(__dirname, relative === '' ? 'index.html' : relative);

    if (!isAllowed(target)) {
        return null;
    }

    if (!READ_EXTENSIONS.has(path.extname(target).toLowerCase())) {
        return null;
    }

    return target;
}

/** Resolves a request-token name to the file the shard's poller will pick up. */
function resolveToken(name) {
    if (!TOKEN_NAME.test(name)) {
        return null;
    }

    const target = path.join(REQUEST_DIR, name + '.token');

    return isAllowed(target) ? target : null;
}

function resolveAck(name) {
    if (!TOKEN_NAME.test(name)) {
        return null;
    }

    const target = path.join(REQUEST_DIR, name + '.ack.json');

    return isAllowed(target) ? target : null;
}

/**
 * The files the editor may save, and the request that reloads each one.
 *
 * entities.json and health.json are deliberately absent: the shard writes those, and an editor
 * that could overwrite a snapshot would be able to lie to itself about the live world.
 */
const WRITABLE = {
    navigation: 'nav-reload',
    dailyLife: 'dailylife-reload',
    restrictedZones: 'zones-reload'
};

const SPAWN_ROOT = path.join(REPO_ROOT, 'Spawns', 'Custom');

// A spawn file is addressed as `spawn:<facet>/GG_<Thing>.xml`.
//
// This is the one writable thing that is a family rather than a fixed name, so it is the one place
// a caller supplies any part of a path. The answer is the same as TOKEN_NAME's: match a strict
// pattern and refuse, never sanitise. A name that has to be cleaned up before it is safe is a name
// worth refusing - and every accepted name is still run back through isAllowed afterwards, so the
// pattern is a first gate rather than the only one.
//
// The GG_ prefix is not decoration either: [XmlLoad and [XmlUnLoad filter on it with an ordinal
// StartsWith, and it is what makes this shard's spawners addressable as a set without touching the
// ~2,500 stock ones.
const SPAWN_NAME = /^spawn:([a-z][a-z0-9_-]{0,31})\/(GG_[A-Za-z0-9_-]{1,48}\.xml)$/;

/**
 * Resolves a writable file by its LOGICAL name, which is the whole sandbox.
 *
 * For the three fixed files there is no path to traverse, encode or escape: a name that is not one
 * of those keys resolves to nothing at all. Spawn files go through SPAWN_NAME and then isAllowed.
 */
function resolveSave(name) {
    if (Object.prototype.hasOwnProperty.call(WRITABLE, name)) {
        return FILES[name];
    }

    return resolveSpawnFile(name);
}

/** The `<facet>/<file>.xml` part of a spawn key, or null when the key is not one. */
function spawnRelative(name) {
    const match = typeof name === 'string' ? SPAWN_NAME.exec(name) : null;
    return match ? `${match[1]}/${match[2]}` : null;
}

function resolveSpawnFile(name) {
    const relative = spawnRelative(name);

    if (!relative) {
        return null;
    }

    const target = path.join(SPAWN_ROOT, ...relative.split('/'));

    return isAllowed(target) ? target : null;
}

/** Every spawn file on disk, as `spawn:` keys. One directory level, matching the layout. */
function listSpawnFiles() {
    const keys = [];

    let facets = [];

    try {
        facets = fs.readdirSync(SPAWN_ROOT, { withFileTypes: true });
    } catch {
        return keys;
    }

    for (const facet of facets) {
        if (!facet.isDirectory()) {
            continue;
        }

        for (const file of fs.readdirSync(path.join(SPAWN_ROOT, facet.name))) {
            const key = `spawn:${facet.name}/${file}`;

            if (resolveSpawnFile(key)) {
                keys.push(key);
            }
        }
    }

    return keys;
}

/** The .bak kept beside a file, matching what NavigationSystem.Save has always written. */
function resolveBackup(name) {
    const target = resolveSave(name);
    return target ? target + '.bak' : null;
}

/** The request that reloads whatever this name addresses. */
function reloadFor(name) {
    if (Object.prototype.hasOwnProperty.call(WRITABLE, name)) {
        return WRITABLE[name];
    }

    // Per file, not the whole tree: [GG_Reimport deletes every GG_ spawner in the world and
    // respawns them, which would make saving one file empty and refill the whole town.
    return spawnRelative(name) ? 'spawn-reload' : null;
}

module.exports = {
    REPO_ROOT, REQUEST_DIR, FILES, WRITABLE, SPAWN_ROOT,
    isAllowed, resolveStatic, resolveToken, resolveAck,
    resolveSave, resolveBackup, resolveSpawnFile, spawnRelative, listSpawnFiles, reloadFor
};
