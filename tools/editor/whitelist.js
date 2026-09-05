'use strict';

// The path sandbox.
//
// The data endpoints take no path from the caller at all - every file they read is a constant
// below. That is the real sandbox: there is nothing to traverse because there is nothing to
// steer. This module exists for the one endpoint that does take a name (the request token drop)
// and so that the boundary is a thing that can be tested rather than a claim.

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..', '..');

// Roots the bridge may read from. Anything resolving outside all of these is refused.
const READ_ROOTS = [
    path.join(REPO_ROOT, 'Data', 'Custom'),
    path.join(REPO_ROOT, 'Data', 'Live'),
    path.join(REPO_ROOT, 'Spawns', 'Custom'),
    path.join(__dirname)
];

// The only directory the bridge writes to, and only ever request tokens.
const REQUEST_DIR = path.join(REPO_ROOT, 'Data', 'Live', 'requests');

const READ_EXTENSIONS = new Set(['.json', '.xml', '.html', '.css', '.js', '.png', '.ico', '.svg']);

// Token names are matched against this rather than sanitised. A name that has to be cleaned up
// before it is safe is a name we should be rejecting.
const TOKEN_NAME = /^[a-z][a-z0-9-]{0,63}$/;

const FILES = {
    navigation: path.join(REPO_ROOT, 'Data', 'Custom', 'navigation.json'),
    dailyLife: path.join(REPO_ROOT, 'Data', 'Custom', 'britain-daily-life.json'),
    restrictedZones: path.join(REPO_ROOT, 'Data', 'Custom', 'restricted-zones.json'),
    entities: path.join(REPO_ROOT, 'Data', 'Live', 'entities.json'),
    health: path.join(REPO_ROOT, 'Data', 'Live', 'health.json')
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

module.exports = { REPO_ROOT, REQUEST_DIR, FILES, isAllowed, resolveStatic, resolveToken, resolveAck };
