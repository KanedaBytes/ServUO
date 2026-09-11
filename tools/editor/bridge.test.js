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

// The spawn files too, since 5c saves those. The stock file is trimmed rather than copied: the
// real trammel.xml is 4 MB, and a bbox test only needs enough blocks to have some inside the box
// and some outside it.
fs.mkdirSync(path.join(root, 'Spawns', 'Custom', 'trammel'), { recursive: true });

for (const name of ['GG_DailyLife.xml', 'GG_OldMarta.xml']) {
    fs.copyFileSync(
        path.join(SOURCE_ROOT, 'Spawns', 'Custom', 'trammel', name),
        path.join(root, 'Spawns', 'Custom', 'trammel', name));
}

{
    const full = fs.readFileSync(path.join(SOURCE_ROOT, 'Spawns', 'trammel.xml'), 'utf8');
    const cut = nthIndex(full, '<Points>', 300);

    fs.writeFileSync(
        path.join(root, 'Spawns', 'trammel.xml'),
        full.slice(0, cut) + '</Spawns>', 'utf8');
}

/** How many lines a file has, without an escape sequence in the middle of an assertion. */
function countLines(text) {
    return text.split(String.fromCharCode(10)).length;
}

function nthIndex(text, needle, n) {
    let at = -1;

    for (let i = 0; i < n; i++) {
        at = text.indexOf(needle, at + 1);
    }

    return at;
}

// Both must be set before the modules below are required: whitelist resolves its paths, and
// bridge.js reads the timeout, at require time.
process.env.GG_EDITOR_ROOT = root;
process.env.GG_ACK_TIMEOUT_MS = '600';

const whitelist = require('./whitelist.js');
const bridge = require('./bridge.js');
const { handleRequest, hashOf } = bridge;
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

    // /api/artinfo starts the renderer, which is a long-lived child process holding the client
    // files open. Without this the test run finishes and node never exits, because the child is
    // still there - which is exactly the leak the bridge's own SIGINT handler exists to prevent.
    bridge.art.stop();

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

/**
 * Some waypoint that is really in navigation.json, read rather than typed.
 *
 * This used to be the literal `brit-plaza-1`, and the Britain rebase - which replaced the town's
 * road with uo-offline's - deleted it, failing seven tests about .bak files, reload acks and stale
 * hashes. None of those tests is about Britain. What they need is a record that exists.
 */
function someWaypoint() {
    return JSON.parse(fs.readFileSync(NAV, 'utf8')).waypoints[0].id;
}

/** A save that moves one waypoint, which is the smallest real edit there is. */
function moveWaypoint(hash, extra) {
    const id = someWaypoint();

    return {
        baseHash: hash,
        updates: [{
            id: `wp:${id}`, kind: 'point', map: 'Trammel', points: [[1500, 1500, 0]],
            props: { id }
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
    const moved = someWaypoint();

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
    assert.ok(after.includes(`"id":"${moved}","map":"Trammel","x":1500,"y":1500,"z":0`));
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

    // The id has to be one that really exists, or the collision this test is about never happens
    // and the bad facet gets all the way to the validator instead (a 422, not a 400).
    const taken = someWaypoint();

    const { status, body } = await call('POST', '/api/save/navigation', {
        baseHash: hashOf(before),
        dryRun: true,
        creates: [{
            id: `wp:${taken}`, kind: 'point', map: 'Sosaria', points: [[1, 2, 3]],
            props: { id: taken }
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

// ---- spawners -----------------------------------------------------------------------------------

const GG_KEY = 'spawn:trammel/GG_OldMarta.xml';
const GG_FILE = whitelist.resolveSpawnFile(GG_KEY);

test.beforeEach(() => {
    fs.copyFileSync(
        path.join(SOURCE_ROOT, 'Spawns', 'Custom', 'trammel', 'GG_OldMarta.xml'), GG_FILE);
    fs.rmSync(GG_FILE + '.bak', { force: true });
});

test('the GG spawners come back always, and the stock ones only for a bbox', async () => {
    const all = await call('GET', '/api/spawners');

    assert.strictEqual(all.status, 200);
    assert.strictEqual(all.body.shapes.filter((s) => s.layer === 'spawners').length, 7);
    assert.strictEqual(all.body.shapes.filter((s) => s.layer === 'spawners-stock').length, 0,
        'stock spawners came back without a bbox');
    assert.ok(all.body.files[GG_KEY].hash);

    const boxed = await call('GET', '/api/spawners?bbox=1400,1580,360,300');
    const stock = boxed.body.shapes.filter((s) => s.layer === 'spawners-stock');

    assert.ok(stock.length > 0 && stock.length < boxed.body.total,
        `${stock.length} of ${boxed.body.total} is not a subset`);

    // Every one of them really is in the box - the whole point of asking for one.
    for (const shape of stock) {
        const [x, y] = shape.points[0];

        assert.ok(x >= 1400 && x < 1760 && y >= 1580 && y < 1880, `${shape.id} at ${x},${y}`);
    }
});

test('a nonsense bbox withholds the stock layer rather than serving the whole facet', async () => {
    const { body } = await call('GET', '/api/spawners?bbox=not,a,box,at-all');

    assert.strictEqual(body.shapes.filter((s) => s.layer === 'spawners-stock').length, 0);
    assert.strictEqual(body.bbox, null);
});

test('saving a spawn file writes a .bak and asks for that file by name, not the whole tree', async () => {
    const before = fs.readFileSync(GG_FILE, 'utf8');
    const { body: listed } = await call('GET', '/api/spawners');
    const shape = listed.shapes.find((s) => s.id.includes('GG_OldMarta'));

    runShard(() => ({ ok: true, message: 'trammel/GG_OldMarta.xml: removed 1, imported 1 spawner(s)' }));

    const { status, body } = await call('POST', `/api/save/${GG_KEY}`, {
        baseHash: listed.files[GG_KEY].hash,
        updates: [{ ...shape, points: [[1480, 1650, 20]] }]
    });

    assert.strictEqual(status, 200);
    assert.strictEqual(body.reloaded, true);
    assert.strictEqual(fs.readFileSync(GG_FILE + '.bak', 'utf8'), before);

    const after = fs.readFileSync(GG_FILE, 'utf8');

    assert.match(after, /<CentreX>1480<\/CentreX>/);
    assert.match(after, /<CentreY>1650<\/CentreY>/);

    // One field per line changed, and nothing else in the file moved.
    assert.strictEqual(after.split('\n').length, before.split('\n').length,
        'editing a spawn list changed the shape of the file');
    // GG mobile in the world on every save.
    assert.deepStrictEqual(shard.seen.map((t) => t.name), ['spawn-reload']);
    assert.match(shard.seen[0].body, /^trammel\/GG_OldMarta\.xml #[0-9a-f]{8}$/);
});

test('a stale spawn save is refused, and a stock file cannot be saved at all', async () => {
    const before = fs.readFileSync(GG_FILE, 'utf8');
    const stale = await call('POST', `/api/save/${GG_KEY}`, { baseHash: 'deadbeef', updates: [] });

    assert.strictEqual(stale.status, 409);
    assert.strictEqual(fs.readFileSync(GG_FILE, 'utf8'), before);

    for (const name of ['spawn:trammel/notgg.xml', 'spawn:../../etc/passwd.xml', 'stock:trammel']) {
        const { status } = await call('POST', `/api/save/${encodeURIComponent(name)}`, { updates: [] });

        assert.strictEqual(status, 400, name);
    }
});

test('a stock spawner shape is refused by name even when addressed at a writable file', async () => {
    const { body: listed } = await call('GET', '/api/spawners');
    const { status, body } = await call('POST', `/api/save/${GG_KEY}`, {
        baseHash: listed.files[GG_KEY].hash,
        updates: [{
            id: 'stock:trammel.xml#3', kind: 'point', map: 'Trammel', points: [[1, 2, 3]], props: {}
        }]
    });

    assert.strictEqual(status, 400);
    assert.match(body.error, /read-only/);
});

test('a spawn entry the shard would silently drop is refused before it is written', async () => {
    // A type name containing ':MX=' makes XmlSpawner discard the whole entry, so the spawner just
    // stops spawning with nothing said anywhere. Both routes into a spawn list are checked: the
    // panel's {type, max} list, and a raw Objects2 string on a shape that carries no entry list.
    const { body: listed } = await call('GET', '/api/spawners');
    const shape = listed.shapes.find((s) => s.id.includes('GG_OldMarta'));
    const hash = listed.files[GG_KEY].hash;

    const viaEntries = await call('POST', `/api/save/${GG_KEY}`, {
        baseHash: hash,
        updates: [{ ...shape, entries: [{ type: 'Bad:MX=9', max: '1' }] }]
    });

    assert.strictEqual(viaEntries.status, 400);
    assert.match(viaEntries.body.error, /discard the whole entry/);

    const { entries, ...noEntries } = shape;
    const viaProps = await call('POST', `/api/save/${GG_KEY}`, {
        baseHash: hash,
        updates: [{ ...noEntries, props: { ...shape.props, Objects2: 'Bad:MX=1:MX=2' } }]
    });

    assert.strictEqual(viaProps.status, 400);
    assert.match(viaProps.body.error, /does not have exactly one/);
});

test('the panel edits a spawn list without ever seeing the micro-format', async () => {
    // The browser gets {type, max} and sends it back; the bridge owns the grammar. Editing a count
    // must leave the other entries byte-identical, since they were not touched.
    const before = fs.readFileSync(GG_FILE, 'utf8');
    const { body: listed } = await call('GET', '/api/spawners');
    const shape = listed.shapes.find((s) => s.id.includes('GG_OldMarta'));

    assert.deepStrictEqual(shape.entries, [{ type: 'OldMarta', max: '1' }]);

    runShard(() => ({ ok: true, message: 'reloaded' }));

    const { status } = await call('POST', `/api/save/${GG_KEY}`, {
        baseHash: listed.files[GG_KEY].hash,
        updates: [{ ...shape, entries: [{ type: 'OldMarta', max: '3' }, { type: 'Fisherman', max: '2' }] }]
    });

    assert.strictEqual(status, 200);

    const after = fs.readFileSync(GG_FILE, 'utf8');

    assert.match(after, /<Objects2>OldMarta:MX=3:.*:OBJ=Fisherman:MX=2:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1<\/Objects2>/);
    assert.strictEqual(countLines(after), countLines(before),
        'editing a spawn list changed the shape of the file');
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

    // Also every token the app asks for directly. A reload name reaches this set through
    // shapes.js, but nav-audit, livemap-on and site-reach are requested straight from app.js and
    // would otherwise be outside the guard entirely - which is exactly where a name typed one way
    // in the browser and another way in the shard goes unnoticed.
    const app = fs.readFileSync(path.join(__dirname, 'js', 'app.js'), 'utf8');

    for (const match of app.matchAll(/api\.request\(\s*'([a-z-]+)'/g)) {
        wanted.add(match[1]);
    }

    // And every token the BRIDGE drops on its own account. `vocabulary` is asked for by the
    // bridge rather than by the browser - nothing in app.js names it - so without this it would
    // sit entirely outside the guard, which is precisely where a name spelled one way here and
    // another way in the shard goes unnoticed until a form is mysteriously empty.
    const bridgeSource = fs.readFileSync(path.join(__dirname, 'bridge.js'), 'utf8');

    for (const match of bridgeSource.matchAll(/writeToken\(\s*'([a-z-]+)'/g)) {
        wanted.add(match[1]);
    }

    for (const name of wanted) {
        assert.ok(poller.includes(`case "${name}":`),
            `the editor asks for '${name}' and RequestPoller.Dispatch has no case for it`);
    }
});

test('the vocabulary answers with the shard down, and never with a hand-kept list', async () => {
    // The temp root has no Data/Live/vocabulary.json and no Scripts/Mobiles, which is the shard
    // down and nothing scanned. The route still has to answer: a create form that refuses to open
    // without the shard is a worse editor than one with free-text fields.
    const { status, body } = await call('GET', '/api/vocabulary');

    assert.strictEqual(status, 200);
    assert.strictEqual(body.shardSeen, false);

    // The half that comes from the files is real, and it came from the files rather than from a
    // list in the editor.
    const types = body.destinationTypes.map((entry) => entry.value);
    const nav = JSON.parse(fs.readFileSync(NAV, 'utf8'));

    for (const destination of nav.destinations) {
        assert.ok(types.includes(destination.type),
            `${destination.type} is in navigation.json and must be offered`);
    }

    // The spawn files are the directory's, not the viewport's - which is what the old
    // spawnfile-list could never be, since it was built from whichever files had been loaded.
    // Relative, not `spawn:` keys: that is the form a spawner shape's id is built from, and
    // handing over the key instead is what made a spawner save match no file at all.
    assert.deepStrictEqual(
        body.spawnFiles.sort(),
        whitelist.listSpawnFiles().map((key) => key.replace(/^spawn:/, '')).sort());

    // Every kind is present at zero rather than absent. An absent kind reads as a form still
    // loading; `Monster (0)` reads as what it is.
    assert.deepStrictEqual(
        body.spawnKinds.map((kind) => kind.key),
        ['Monster', 'NPC', 'Vendor', 'PlayerBotFixed', 'PlayerBotLifecycle']
    );
});

// ---- the isometric art tiles ------------------------------------------------------------------
//
// The renderer itself is a C# child process and these tests do not build it, so what is checked
// here is the bridge's half: that a malformed path is refused before anything is spawned, and that
// the two info routes answer whether or not there is a renderer to answer for.

const { parseTilePath } = require('./artrenderer.js');

test('a tile path is parsed into numbers and a layer, or refused', () => {
    assert.deepStrictEqual(
        parseTilePath('/tiles/iso/Trammel/v2/map/ground/10/340/259.png'),
        {
            facet: 'Trammel', version: 2, layer: 'map', layerName: 'map',
            floor: 'ground', level: 10, x: 340, y: 259, extension: 'png'
        });

    // The pick sidecar sits beside the tile it answers for and is addressed the same way, so this
    // stays one function rather than two that have to agree about what a layer is.
    assert.deepStrictEqual(
        parseTilePath('/tiles/iso/Trammel/v2/map/ground/10/340/259.pick'),
        {
            facet: 'Trammel', version: 2, layer: 'map', layerName: 'map',
            floor: 'ground', level: 10, x: 340, y: 259, extension: 'pick'
        });

    // The item layer's directory carries the snapshot's own id, which is what makes a re-decorate
    // a different set of URLs rather than something to invalidate by hand.
    assert.deepStrictEqual(
        parseTilePath('/tiles/iso/Trammel/v2/items-trammel-1-20260908205527-29778/all/9/170/129.png'),
        {
            facet: 'Trammel', version: 2, layer: 'items',
            layerName: 'items-trammel-1-20260908205527-29778',
            floor: 'all', level: 9, x: 170, y: 129, extension: 'png'
        });

    // Every segment is checked against what it is allowed to BE rather than sanitised - the same
    // rule the token names follow. There is no spelling of a path that reaches the filesystem,
    // because the path is rebuilt from the parts rather than taken from the caller.
    const refused = [
        '/tiles/iso/Trammel/v2/map/attic/10/340/259.png',      // not a floor
        '/tiles/iso/Trammel/v2/map/all/10/340/259.jpg',        // not one of the two extensions
        '/tiles/iso/Trammel/v2/map/all/10/340/259.pick.png',   // nor is a second one appended
        '/tiles/iso/Trammel/v2/map/all/10/340/259.PICK',       // and the set is exact, not case-folded
        '/tiles/iso/Trammel/v2/map/all/10/-1/259.png',         // negative
        '/tiles/iso/Trammel/v2/map/all/10/340.png',            // too few segments
        '/tiles/iso/Trammel/v2/map/all/10/340/259/extra.png',  // too many
        '/tiles/iso/Trammel/v2/all/10/340/259.png',            // the old, layerless shape
        '/tiles/iso/Trammel/v2/statics/all/10/340/259.png',    // not a layer
        '/tiles/iso/Trammel/v2/items-../all/10/340/259.png',   // traversal in the snapshot id
        '/tiles/iso/Trammel/v2/items-a/b/all/10/340/259.png',
        '/tiles/iso/../../v2/map/all/10/340/259.png',
        '/tiles/iso/Trammel/v2/map/all/10/340/%2e%2e%2fboot.png'
    ];

    for (const bad of refused) {
        assert.strictEqual(parseTilePath(bad), null, bad + ' was accepted');
    }
});

test('a malformed art tile path is a 400 and never reaches the renderer', async () => {
    const response = await fetch(origin + '/tiles/iso/Trammel/v2/map/attic/10/340/259.png');

    assert.strictEqual(response.status, 400);
});

test('an item tile with no snapshot loaded is refused, not left hanging', async () => {
    // The art view has to work with the shard down - the map layer is the client's own files and
    // needs nothing running. Asking for furniture when nobody has taken a snapshot is a 503 with a
    // reason, so the editor can say "run [WorldItems" rather than showing empty paving forever.
    const response = await fetch(
        origin + '/tiles/iso/Trammel/v2/items-nothing-loaded/all/10/340/259.png');

    assert.ok(response.status === 503 || response.status === 200,
        `expected an answer, got ${response.status}`);
});

test('artinfo and artstats always answer, renderer or no renderer', async () => {
    // `available: false` is a normal answer - MapExport.exe may simply not have been built - and
    // it has to be an answer rather than a 500, because the editor asks this at boot and would
    // otherwise report the bridge as unreachable over a tool that is merely absent.
    const info = await (await fetch(origin + '/api/artinfo')).json();
    assert.strictEqual(typeof info.available, 'boolean');

    if (!info.available) {
        assert.ok(info.reason, 'an unavailable renderer has to say why');
    } else {
        assert.strictEqual(typeof info.maxLevel, 'number');
        assert.strictEqual(typeof info.version, 'number');
    }

    const stats = await (await fetch(origin + '/api/artstats')).json();

    for (const key of ['available', 'running', 'rendered', 'failed', 'queued', 'lastMs', 'avgMs']) {
        assert.ok(key in stats, 'artstats is missing ' + key);
    }
});

test('a pick sidecar is served as bytes, still compressed', async () => {
    // The sidecar is gzipped on disk and goes out with Content-Encoding: gzip, so the browser
    // inflates it natively and fetch().arrayBuffer() hands over the EXACT bytes. That exactness is
    // the whole reason it is not a PNG: a picture would have to come back through a canvas, which
    // premultiplies alpha and applies colour management, and a pick map that is nearly right is a
    // waypoint three tiles from where it was clicked.
    //
    // 503 is a legitimate answer here - MapExport.exe may not have been built - so this asserts the
    // shape of a success rather than that there is one, exactly as the item-tile test above does.
    const response = await fetch(origin + '/tiles/iso/Trammel/v2/map/all/10/340/259.pick');

    assert.ok(response.status === 200 || response.status === 503,
        `expected an answer, got ${response.status}`);

    if (response.status !== 200) {
        return;
    }

    assert.strictEqual(response.headers.get('content-type'), 'application/octet-stream');

    const bytes = new Uint8Array(await response.arrayBuffer());

    // fetch() has already inflated it, so what arrives is the header this file's format begins
    // with - "GGPK" and the format byte - rather than gzip's own 1f 8b.
    assert.deepStrictEqual(
        Array.from(bytes.slice(0, 4)), [0x47, 0x47, 0x50, 0x4B], 'not a GGPK pick map');
    assert.strictEqual(bytes[4], 1, 'unexpected pick format');
});

test('the browser read path lands on the tile it was aimed at, end to end', async () => {
    // THE ACCEPTANCE TEST, as far as it can be run without a browser: the forward projection picks
    // a tile and a pixel, the bridge renders and serves the sidecar, the browser's own decoder
    // reads it, and the answer has to be the world tile it started from. Every seam in the feature
    // is in this line - iso.tileFor, the URL shape, parseTilePath, the renderer's pass, the gzip,
    // and js/pickmap.js - and a drift in any of them shows up here as a tile in the wrong place
    // rather than as an error.
    //
    // brit-forge, the anvil in the smithy yard: 1424,1557 at z 30, which is where Britain's upper
    // town stands and therefore where a ground-plane inverse would be about 2.7 tiles out.
    const info = await (await fetch(origin + '/api/artinfo')).json();

    if (!info.available) {
        return;   // MapExport.exe has not been built; the shape of this is checked above.
    }

    const iso = await import('./js/iso.js');
    const pickmap = await import('./js/pickmap.js');

    const [x, y, z] = [1424, 1557, 30];
    const max = info.maxLevel;
    const at = iso.tileFor(x, y, z, max, max, 4096, info.tileSize);

    // The centre of the tile's diamond: the anchor is its south vertex, and land art is 44 tall.
    const centre = iso.canvasToTile(
        at.canvasX, at.canvasY - (iso.LAND_ART_SIZE / 2), max, max, info.tileSize);

    const url = `${origin}/tiles/iso/Trammel/v${info.version}/map/all/${max}`
        + `/${centre.tileX}/${centre.tileY}.pick`;

    const response = await fetch(url);

    assert.strictEqual(response.status, 200, url);

    const map = pickmap.decode(await response.arrayBuffer(), info.pickFormat);
    const found = map.at(centre.pixelX, centre.pixelY);

    assert.ok(found, 'nothing was drawn at the centre of the smithy yard');
    assert.deepStrictEqual(
        { x: found.x, y: found.y, z: found.z, what: found.what },
        { x, y, z, what: 'land' },
        'the pick map did not name the tile the projection aimed at');
});

test('a pick asked for at the wrong level is refused, not answered with another tile', async () => {
    // There is only one pick level, and the temptation is to ignore the one in the URL. Doing that
    // answered a request for tile 170,129 at 1:2 with tile 170,129 at 1:1 - a different part of the
    // world, confidently, with a 200. The level segment is part of the identity.
    const info = await (await fetch(origin + '/api/artinfo')).json();

    if (!info.available) {
        return;
    }

    const wrong = info.pickLevel - 1;
    const response = await fetch(
        `${origin}/tiles/iso/Trammel/v${info.version}/map/all/${wrong}/170/129.pick`);

    assert.strictEqual(response.status, 503);
    assert.match(await response.text(), /only at level/i);
});

test('landz validates its tile list before it reaches the renderer', async () => {
    const refused = [
        '',                       // absent
        'nonsense',               // not a pair
        '1424,1557,30',           // three
        '1424.5,1557',            // not integers
        Array.from({ length: 257 }, () => '1,1').join(';')   // over the cap
    ];

    for (const tiles of refused) {
        const response = await fetch(origin + '/api/landz?tiles=' + encodeURIComponent(tiles));

        assert.strictEqual(response.status, 400, JSON.stringify(tiles) + ' was accepted');
    }
});

test('landz answers 200 with a reason rather than failing when there is no renderer', async () => {
    // A zone still has to draw and a radar placement still has to succeed when MapExport has not
    // been built, so `z: null` with a reason is the answer and never a 4xx or a 5xx.
    const body = await (await fetch(origin + '/api/landz?tiles=1424,1557;1475,1645')).json();

    if (body.z === null) {
        assert.ok(body.reason, 'a null answer has to say why');
        return;
    }

    assert.strictEqual(body.z.length, 2);
    assert.ok(body.z.every((z) => Number.isInteger(z)), 'every Z is an integer');
});

test('the art cache is not writable through the save path', () => {
    // tiles/ is derived data the renderer owns; whitelist.resolveSave serves a three-entry table
    // plus the spawn files, so there is no name that reaches it. Asserted rather than assumed,
    // because "there is nothing to steer" is only true while the table stays a table.
    for (const name of ['iso', 'tiles', 'tiles/iso', '../tools/editor/tiles/iso']) {
        assert.strictEqual(whitelist.resolveSave(name), null, name + ' resolved');
    }
});

// --- the Admin section's endpoints --------------------------------------------------------------

test('the shard feeds answer with the shard down, in the shape the panel draws', async () => {
    // The temp root has no Data/Live/console.json: that is a shard that is down, or one with
    // Custom.ConsoleTap=False. A panel that had to guard against an absent key would be a panel
    // where one reader forgets to, so the empty shape is always the same shape.
    for (const [url, extra] of [['/api/console', true], ['/api/logins', true], ['/api/tile-probe', false]]) {
        const { status, body } = await call('GET', url);

        assert.strictEqual(status, 200, url);
        assert.ok(Array.isArray(body.lines), `${url} always carries a lines array`);

        if (extra) {
            assert.strictEqual(body.sequence, 0,
                `${url} carries a sequence, so a stalled feed is distinguishable from a quiet one`);
        }
    }
});

test('the restart endpoint reports idle before anything has asked for one', async () => {
    const { status, body } = await call('GET', '/api/restart');

    assert.strictEqual(status, 200);
    assert.strictEqual(body.running, false);
    assert.strictEqual(body.stage, 'idle');
});

test('a cross-site restart is refused', async () => {
    // The most destructive thing the bridge can do, and it goes through the same same-origin gate
    // as every other POST - which sits before the dispatch table rather than inside each handler,
    // so a new endpoint cannot forget it. Asserted here because this is the one where forgetting
    // would matter most.
    const response = await fetch(origin + '/api/restart', {
        method: 'POST',
        headers: { 'Sec-Fetch-Site': 'cross-site' }
    });

    assert.strictEqual(response.status, 403);

    const after = await call('GET', '/api/restart');

    assert.strictEqual(after.body.running, false, 'and nothing was started');
});

test('the restart launcher is one fixed script, with nothing the caller supplies', () => {
    // The bridge spawns exactly one child on this path. If that ever becomes a name built from a
    // request, the sandbox argument in the README stops being true - so the shape is asserted
    // rather than described.
    const source = fs.readFileSync(path.join(__dirname, 'bridge.js'), 'utf8');

    assert.ok(source.includes("'restart-shard.ps1'"),
        'the restart script is named as a constant');
    assert.ok(/execFile\(\s*\n?\s*'powershell\.exe'/.test(source),
        "powershell.exe with the extension - spawn() does not consult PATHEXT, and 'powershell' "
        + 'raised an ENOENT that a two-minute timeout then reported as the shard not coming back');

    const script = fs.readFileSync(
        path.join(SOURCE_ROOT, 'tools', 'restart-shard.ps1'), 'utf8');

    assert.ok(script.includes('build.ps1'),
        'a restart rebuilds, so a failed build still refuses to launch');
});
