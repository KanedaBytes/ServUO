'use strict';

// node --test tools/editor/*.test.js
//
// 5a shipped no bridge test at all, which was a gap of its own: every claim in its README about
// refusing traversal and cross-site writes was a claim rather than an assertion. This file tests
// the real handleRequest over a real socket, and the save path end to end against a fake shard
// speaking the real token/ack file protocol.
//
// EVERYTHING RUNS AGAINST A TEMP COPY of the data files, selected by GG_EDITOR_ROOT. Data/Custom
// is not gitignored and holds hand-authored work; a test that wrote to the real tree would be one
// crash away from a truncated navigation.json.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');

const SOURCE_ROOT = path.resolve(__dirname, '..', '..');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gg-editor-'));

fs.mkdirSync(path.join(root, 'Data', 'Custom'), { recursive: true });
fs.mkdirSync(path.join(root, 'Data', 'Live', 'requests'), { recursive: true });
fs.mkdirSync(path.join(root, 'Config'), { recursive: true });

for (const name of ['navigation.json', 'britain-daily-life.json', 'restricted-zones.json']) {
    fs.copyFileSync(
        path.join(SOURCE_ROOT, 'Data', 'Custom', name),
        path.join(root, 'Data', 'Custom', name));
}

fs.copyFileSync(
    path.join(SOURCE_ROOT, 'Config', 'Custom.cfg'), path.join(root, 'Config', 'Custom.cfg'));

// Both must be set before the modules below are required: whitelist resolves its paths, and
// bridge.js reads the timeout, at require time.
process.env.GG_EDITOR_ROOT = root;
process.env.GG_ACK_TIMEOUT_MS = '600';

const whitelist = require('./whitelist.js');
const { handleRequest, hashOf } = require('./bridge.js');
const fakeShard = require('./fake-shard.js');

const NAV = whitelist.FILES.navigation;
const REQUESTS = whitelist.REQUEST_DIR;

let server;
let origin;
let shard;

test.before(async () => {
    server = http.createServer(handleRequest);

    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));

    origin = `http://127.0.0.1:${server.address().port}`;
});

test.after(() => {
    if (shard) shard.stop();
    server.close();
    fs.rmSync(root, { recursive: true, force: true });
});

test.beforeEach(() => {
    // Each test starts from the shipped file and an empty request directory, so one test's token
    // or ack can never be read as the next one's.
    fs.copyFileSync(path.join(SOURCE_ROOT, 'Data', 'Custom', 'navigation.json'), NAV);
    fs.rmSync(NAV + '.bak', { force: true });

    for (const name of fs.readdirSync(REQUESTS)) {
        fs.rmSync(path.join(REQUESTS, name), { force: true });
    }

    if (shard) {
        shard.stop();
        shard = null;
    }
});

function runShard(respond) {
    shard = fakeShard.start({ dir: REQUESTS, respond });
    return shard;
}

async function call(method, url, body, headers) {
    const response = await fetch(origin + url, {
        method,
        headers: { 'Content-Type': 'application/json', ...(headers || {}) },
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    return { status: response.status, body: await response.json() };
}

/** A save that moves one waypoint, which is the smallest real edit there is. */
function moveWaypoint(hash, extra) {
    return {
        baseHash: hash,
        updates: [{
            id: 'wp:brit-plaza-1', kind: 'point', map: 'Trammel', points: [[1500, 1500, 0]],
            props: { id: 'brit-plaza-1' }
        }],
        ...extra
    };
}

async function currentHash() {
    const { body } = await call('GET', '/api/shapes');
    return body.files.navigation.hash;
}

// ---- reading ----------------------------------------------------------------------------------

test('/api/shapes carries the hash each file was read at', async () => {
    const { status, body } = await call('GET', '/api/shapes');

    assert.strictEqual(status, 200);
    assert.ok(body.shapes.length > 0);
    assert.strictEqual(body.files.navigation.hash, hashOf(fs.readFileSync(NAV, 'utf8')));
    assert.ok(body.files.dailyLife.hash);
    assert.ok(body.files.restrictedZones.hash);
});

// ---- the stale-write guard ---------------------------------------------------------------------

test('a save from a stale editor is refused and writes nothing', async () => {
    const before = fs.readFileSync(NAV, 'utf8');
    const { status, body } = await call('POST', '/api/save/navigation', moveWaypoint('deadbeef'));

    assert.strictEqual(status, 409);
    assert.match(body.error, /navigation\.json changed on disk since you loaded it/);
    assert.strictEqual(body.actual, hashOf(before));
    assert.strictEqual(fs.readFileSync(NAV, 'utf8'), before, 'the file was written anyway');
    assert.strictEqual(fs.existsSync(NAV + '.bak'), false, 'a rejected save left a .bak');
});

// ---- the happy path ----------------------------------------------------------------------------

test('a save writes a .bak, the new file, a token, and reads its own ack', async () => {
    const before = fs.readFileSync(NAV, 'utf8');

    runShard(() => ({ ok: true, message: '75 waypoint(s), 27 destination(s), 0 warning(s)' }));

    const { status, body } = await call(
        'POST', '/api/save/navigation', moveWaypoint(hashOf(before)));

    assert.strictEqual(status, 200);
    assert.deepStrictEqual(
        { written: body.written, reloaded: body.reloaded }, { written: true, reloaded: true });
    assert.match(body.message, /75 waypoint\(s\)/);

    assert.strictEqual(fs.readFileSync(NAV + '.bak', 'utf8'), before,
        'the .bak is not the bytes that were there before the save');

    const after = fs.readFileSync(NAV, 'utf8');

    assert.notStrictEqual(after, before);
    assert.match(after, /"id":"brit-plaza-1","map":"Trammel","x":1500,"y":1500,"z":0/);
    assert.strictEqual(body.hash, hashOf(after));

    assert.deepStrictEqual(shard.seen.map((t) => t.name), ['nav-reload']);
    assert.match(shard.seen[0].body, /^#[0-9a-f]{8}$/, 'the token carried no nonce');

    assert.strictEqual(fs.existsSync(path.join(REQUESTS, 'nav-reload.token')), false,
        'the token was left on disk');
});

test('a written file whose reload is refused is a 200 carrying the shard reason', async () => {
    // The whole point of the persistent banner. An HTTP error here would send the editor down the
    // "nothing was written" path while the file on disk said otherwise.
    const before = fs.readFileSync(NAV, 'utf8');

    runShard(() => ({
        ok: false,
        message: "navigation.json: waypoints[3] duplicates the id 'brit-plaza-1'",
        errors: ["navigation.json: waypoints[3] duplicates the id 'brit-plaza-1'"]
    }));

    const { status, body } = await call(
        'POST', '/api/save/navigation', moveWaypoint(hashOf(before)));

    assert.strictEqual(status, 200);
    assert.strictEqual(body.written, true);
    assert.strictEqual(body.reloaded, false);
    assert.match(body.message, /duplicates the id 'brit-plaza-1'/);
    assert.strictEqual(body.errors.length, 1);
    assert.notStrictEqual(fs.readFileSync(NAV, 'utf8'), before, 'the write did not happen');
});

test('warnings from a successful reload come back alongside the success', async () => {
    runShard(() => ({
        ok: true,
        message: '75 waypoint(s), 27 destination(s), 1 warning(s)',
        warnings: ["waypoint 'brit-plaza-1' has no edges"]
    }));

    const { body } = await call('POST', '/api/save/navigation', moveWaypoint(await currentHash()));

    assert.strictEqual(body.reloaded, true);
    assert.deepStrictEqual(body.warnings, ["waypoint 'brit-plaza-1' has no edges"]);
});

test('a shard that never answers is reported as that, not as a failed write', async () => {
    runShard(() => null);

    const { status, body } = await call(
        'POST', '/api/save/navigation', moveWaypoint(await currentHash()));

    assert.strictEqual(status, 200);
    assert.strictEqual(body.written, true);
    assert.strictEqual(body.reloaded, false);
    assert.match(body.message, /did not answer/);
});

// ---- the stale ack -----------------------------------------------------------------------------

test('an ack left over from a previous run is not read as this run answer', async () => {
    // The bug this closes: the shard overwrites <name>.ack.json in place and never deletes it, and
    // /api/ack reports "pending" only when the file is ABSENT.
    fakeShard.writeAck(REQUESTS, 'nav-reload', '#stale', {
        ok: false, message: 'an answer from some earlier run'
    });

    runShard(() => ({ ok: true, message: 'the answer to this run' }));

    const { body } = await call('POST', '/api/save/navigation', moveWaypoint(await currentHash()));

    assert.strictEqual(body.message, 'the answer to this run');
    assert.strictEqual(body.reloaded, true);
});

test('the same save twice in one process gets two different answers', async () => {
    let n = 0;

    runShard(() => ({ ok: true, message: `run ${++n}` }));

    const first = await call('POST', '/api/save/navigation', moveWaypoint(await currentHash()));
    const second = await call('POST', '/api/save/navigation', moveWaypoint(await currentHash()));

    assert.strictEqual(first.body.message, 'run 1');
    assert.strictEqual(second.body.message, 'run 2', 'the second save read the first save ack');
});

// ---- the dry run -------------------------------------------------------------------------------

test('a dry run reports problems and touches nothing', async () => {
    const before = fs.readFileSync(NAV, 'utf8');

    const { status, body } = await call('POST', '/api/save/navigation', {
        baseHash: hashOf(before),
        dryRun: true,
        creates: [{
            id: 'wp:brit-plaza-1', kind: 'point', map: 'Sosaria', points: [[1, 2, 3]],
            props: { id: 'brit-plaza-1' }
        }]
    });

    // The create collides with an existing id, so it never reaches the validator - which is the
    // right answer, just a different one.
    assert.strictEqual(status, 400);
    assert.match(body.error, /already exists/);
    assert.strictEqual(fs.readFileSync(NAV, 'utf8'), before);
    assert.strictEqual(fs.existsSync(NAV + '.bak'), false);
});

test('a dry run of a fatally invalid edit is a 422 naming the shape', async () => {
    const before = fs.readFileSync(NAV, 'utf8');

    const { status, body } = await call('POST', '/api/save/navigation', {
        baseHash: hashOf(before),
        dryRun: true,
        updates: [{
            id: 'zone:brit-town', kind: 'rect', map: 'Trammel', rect: [1416, 1498, 0, 279],
            props: { id: 'brit-town' }
        }]
    });

    assert.strictEqual(status, 422);
    assert.strictEqual(body.dryRun, true);
    assert.match(body.fatal.map((p) => p.message).join('|'),
        /bounds must have a positive width and height \(found 0x279\)/);
    assert.strictEqual(body.fatal[0].shapeId, 'zone:brit-town');

    assert.strictEqual(fs.readFileSync(NAV, 'utf8'), before, 'a dry run wrote to the file');
    assert.strictEqual(fs.existsSync(NAV + '.bak'), false);
    assert.strictEqual(fs.existsSync(path.join(REQUESTS, 'nav-reload.token')), false);
});

test('a real save is not blocked by the validator, because the shard is the authority', async () => {
    // validate.js is a replica. A false positive in it must never stop a write the shard would
    // have accepted - so the same edit the dry run called fatal still gets written.
    runShard(() => ({ ok: false, message: 'Navigation NOT reloaded: zones[0] bounds ...' }));

    const { status, body } = await call('POST', '/api/save/navigation', {
        baseHash: await currentHash(),
        updates: [{
            id: 'zone:brit-town', kind: 'rect', map: 'Trammel', rect: [1416, 1498, 0, 279],
            props: { id: 'brit-town' }
        }]
    });

    assert.strictEqual(status, 200);
    assert.strictEqual(body.written, true);
    assert.strictEqual(body.reloaded, false);
    assert.match(fs.readFileSync(NAV, 'utf8'), /"id":"brit-town","map":"Trammel","x":1416,"y":1498,"width":0/);
});

// ---- restore -----------------------------------------------------------------------------------

test('restore puts the file back to the bytes before the save', async () => {
    const before = fs.readFileSync(NAV, 'utf8');

    runShard(() => ({ ok: true, message: 'reloaded' }));

    await call('POST', '/api/save/navigation', moveWaypoint(hashOf(before)));

    assert.notStrictEqual(fs.readFileSync(NAV, 'utf8'), before);

    const { status, body } = await call('POST', '/api/restore/navigation');

    assert.strictEqual(status, 200);
    assert.strictEqual(body.restored, true);
    assert.strictEqual(fs.readFileSync(NAV, 'utf8'), before);
});

test('restore with nothing to restore says so rather than inventing a file', async () => {
    const { status, body } = await call('POST', '/api/restore/navigation');

    assert.strictEqual(status, 409);
    assert.match(body.error, /no backup to restore/);
});

// ---- the sandbox -------------------------------------------------------------------------------

test('only the three data files can be saved, by name', async () => {
    for (const name of ['entities', 'health', 'nope']) {
        const { status, body } = await call('POST', `/api/save/${name}`, { creates: [] });

        assert.strictEqual(status, 400, name);
        assert.match(body.error, new RegExp(`Unknown file '${name}'`));
    }
});

test('traversal is refused however it is spelled', async () => {
    // A plain ../ is normalised away by the URL parser before any handler sees it, so it lands on
    // no endpoint at all; an encoded one survives as a name, and there is no such name.
    const plain = await call('POST', '/api/save/../../etc/passwd', {});

    assert.strictEqual(plain.status, 404);

    const encoded = await call('POST', '/api/save/..%2F..%2Fetc%2Fpasswd', {});

    assert.strictEqual(encoded.status, 400);
    assert.match(encoded.body.error, /Unknown file/);
    assert.strictEqual(fs.existsSync(path.join(root, 'etc')), false);
});

test('a cross-site save is refused before the file name is even looked at', async () => {
    const { status, body } = await call(
        'POST', '/api/save/navigation', moveWaypoint('x'), { 'Sec-Fetch-Site': 'cross-site' });

    assert.strictEqual(status, 403);
    assert.match(body.error, /cross-site/);
});

test('an oversized body is answered rather than dropped', async () => {
    const response = await fetch(origin + '/api/save/navigation', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ baseHash: 'x', filler: 'a'.repeat(2 * 1024 * 1024) })
    });

    assert.strictEqual(response.status, 413);
});

// ---- the two languages agree --------------------------------------------------------------------

test('every reload the editor can ask for exists in the shard dispatcher', async () => {
    // The drift this catches is not hypothetical: 5a had the restricted layer reloading navigation,
    // so an edit there reloaded the wrong system and reported success.
    const shapes = fs.readFileSync(path.join(__dirname, 'js', 'shapes.js'), 'utf8');
    const poller = fs.readFileSync(
        path.join(SOURCE_ROOT, 'Scripts', 'Custom', 'Core', 'Bridge', 'RequestPoller.cs'), 'utf8');

    const wanted = new Set(
        [...shapes.matchAll(/reload:\s*'([a-z-]+)'/g)].map((match) => match[1]));

    for (const name of Object.values(whitelist.WRITABLE)) {
        wanted.add(name);
    }

    for (const name of wanted) {
        assert.ok(poller.includes(`case "${name}":`),
            `the editor asks for '${name}' and RequestPoller.Dispatch has no case for it`);
    }
});
