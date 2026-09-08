'use strict';

// The isometric renderer, as seen from the bridge.
//
// WHY THERE IS A CHILD PROCESS HERE AT ALL. A facet-wide isometric render cannot exist - Trammel's
// iso canvas is 247,808 pixels square, 61 gigapixels per floor, tens of hours and tens of
// gigabytes for one facet. So there is no batch export to run before the editor starts: a tile is
// rendered the first time somebody looks at it and then kept forever. That makes the renderer a
// service rather than a script, and MapExport.exe --serve is it.
//
// The protocol is one line each way over stdin and stdout (tools/MapExport/TileServer.cs):
//
//     <- {"ready":true,"version":3,...}          the handshake, once
//     -> items <path>
//     <- ok <count> <id>               or   err <message>
//     -> tile <layer> <floor> <level> <x> <y>
//     <- ok <ms> <bytes> <path>       or   empty   or   err <message>
//     -> pick <layer> <floor> <level> <x> <y>
//     <- ok <ms> <bytes> <path>       or   empty   or   err <message>
//     -> landz <x>,<y> <x>,<y> ...
//     <- ok <z> <z> ...               or   err <message>
//
// THERE ARE TWO LAYERS AND THEY EXPIRE DIFFERENTLY. `map` is the client's own world - land and
// statics - which never changes, so it is cached under the renderer version and kept forever.
// `items` is the shard's furniture, from Data/Live/world-items.json, which changes every time
// somebody decorates; it is cached under the SNAPSHOT's id instead, so a re-decorate throws away
// seconds of work rather than the minutes the map layer costs. Baking them together would have
// made every [Decorate invalidate the expensive one.
//
// ONE REQUEST AT A TIME, and that is not a simplification to be improved on later: Server's
// TileMatrix keeps its block buffers in static fields, so two renders at once corrupt each other.
// The queue here is what serialises them.
//
// THE QUEUE IS LIFO. A pan asks for a screenful of tiles and then abandons them a moment later;
// answering the newest first means the tiles you are looking at now do not wait behind the ones
// you have already left. Nothing is dropped - the browser's own six-connection cap is what bounds
// how many are ever outstanding.
//
// A MISS BLOCKS THE HTTP REQUEST until the tile is rendered. That is the whole placeholder
// protocol: there isn't one. The editor draws radar underneath the art layer, so a tile that has
// not arrived shows radar rather than a hole, and the browser's Image simply takes a moment.
//
// THE PICK MAP IS ASKED FOR SEPARATELY, and lazily. It is a sidecar beside the tile - which world
// tile and standing Z each pixel belongs to, so the editor can invert a projection that has no
// inverse - and it is only wanted for tiles somebody actually puts the cursor on, which is a small
// fraction of what gets drawn. Rendering it with every PNG would have meant bumping the renderer
// version and throwing away every tile already cached, for a file that changes no pixel. The cost
// is one extra paint pass, about 13 ms, the first time a tile is hovered, ever.

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const zlib = require('zlib');

const ROOT = path.resolve(__dirname, '..', '..');
const EXE = path.join(ROOT, 'tools', 'MapExport', 'bin', 'Release', 'MapExport.exe');
const TILES = path.join(__dirname, 'tiles');

/** Long enough for the coarsest art level on a cold art cache; past it, the browser retries. */
const RENDER_TIMEOUT_MS = Number(process.env.GG_ART_TIMEOUT_MS) || 30000;

const FLOORS = new Set(['ground', 'first', 'all']);

/** The shard's world-item snapshot, relative to the repo root. */
const SNAPSHOT = path.join(ROOT, 'Data', 'Live', 'world-items.json');

/**
 * A 1x1 fully transparent PNG, served for an item tile with nothing in it.
 *
 * Most item tiles are empty and the renderer says so without rasterising, but the browser still
 * asked for an image and has to get one - an error would trip the retry path, and a 204 would trip
 * it too. Stretching one transparent pixel over the tile is visually identical to a transparent
 * tile and costs 70 bytes instead of a file per empty tile on disk.
 *
 * BUILT RATHER THAN PASTED, because the pasted one was wrong. It was a "well-known 1x1 transparent
 * PNG" copied from memory, and decoding it showed a pixel of R=0 G=0 B=255 A=127 - a HALF
 * TRANSPARENT BLUE dot, stretched by drawImage over every empty tile in the art view. Every
 * item-less tile in Britain drew as a blue square and the map looked broken. Eleven lines of zlib
 * and a CRC cannot be wrong in that way, and empty-png.test.js decodes the result to be sure.
 */
function buildTransparentPng() {
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(1, 0);   // width
    ihdr.writeUInt32BE(1, 4);   // height
    ihdr[8] = 8;                // bit depth
    ihdr[9] = 6;                // colour type 6 = truecolour with alpha
    ihdr[10] = 0;               // deflate
    ihdr[11] = 0;               // adaptive filtering
    ihdr[12] = 0;               // no interlace

    // One scanline: filter type 0 (None), then RGBA all zero. Zero alpha is the whole point.
    const idat = zlib.deflateSync(Buffer.from([0, 0, 0, 0, 0]));

    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        pngChunk('IHDR', ihdr),
        pngChunk('IDAT', idat),
        pngChunk('IEND', Buffer.alloc(0))
    ]);
}

function pngChunk(type, body) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(body.length, 0);

    const payload = Buffer.concat([Buffer.from(type, 'ascii'), body]);

    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(payload), 0);

    return Buffer.concat([length, payload, crc]);
}

// Hand-rolled for the same reason tools/MapExport/PngWriter.cs hand-rolls it: zlib.crc32 is new
// enough that pinning a Node version for one checksum is a worse trade than twelve lines.
const CRC_TABLE = (() => {
    const table = new Uint32Array(256);

    for (let i = 0; i < 256; i++) {
        let c = i;

        for (let k = 0; k < 8; k++) {
            c = (c & 1) !== 0 ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
        }

        table[i] = c >>> 0;
    }

    return table;
})();

function crc32(buffer) {
    let c = 0xFFFFFFFF;

    for (let i = 0; i < buffer.length; i++) {
        c = CRC_TABLE[(c ^ buffer[i]) & 0xFF] ^ (c >>> 8);
    }

    return (c ^ 0xFFFFFFFF) >>> 0;
}

// Built after CRC_TABLE, not before it: a `const` does not hoist, and building the PNG at the top
// of the file threw a temporal-dead-zone error the moment the module was required.
const EMPTY_PNG = buildTransparentPng();

class ArtRenderer {
    constructor(options = {}) {
        this.exe = options.exe || EXE;
        this.tiles = options.tiles || TILES;
        this.facet = options.facet || 'Trammel';
        this.log = options.log || (() => {});

        this.child = null;
        this.info = null;

        // What the loaded snapshot is, and what the file looked like when it was loaded. The
        // mtime/size pair is how a re-decorate is noticed without watching anything.
        this.items = null;
        this.itemsStamp = null;

        // Tile keys the renderer has answered `empty` for. Remembered so a pan back over open
        // country does not ask again; the key carries the snapshot id, so a new snapshot empties
        // this by making every key different.
        this.empties = new Set();
        this.pending = null;      // the request the child is working on
        this.queue = [];
        this.starting = null;

        this.stats = { rendered: 0, failed: 0, lastMs: 0, totalMs: 0 };
    }

    get available() {
        return fs.existsSync(this.exe);
    }

    /** What the renderer says it is, for /api/artinfo. Null until it has been started once. */
    describe() {
        return this.info;
    }

    counters() {
        return {
            available: this.available,
            running: this.child !== null,
            version: this.info ? this.info.version : null,
            items: this.items,
            emptyTiles: this.empties.size,
            rendered: this.stats.rendered,
            failed: this.stats.failed,
            queued: this.queue.length + (this.pending ? 1 : 0),
            lastMs: this.stats.lastMs,
            avgMs: this.stats.rendered > 0 ? Math.round(this.stats.totalMs / this.stats.rendered) : 0
        };
    }

    /**
     * The cache path for a tile, or for the pick sidecar beside it. The renderer builds the same
     * one; both derive it from `info`.
     */
    tilePath(layer, floor, level, x, y, extension = 'png') {
        if (!this.info) {
            return null;
        }

        return path.join(
            this.tiles, 'iso', this.facet, `v${this.info.version}`,
            layer, floor, String(level), String(x), `${y}.${extension}`);
    }

    /** The directory name for a layer: constant for the map, the snapshot's id for items. */
    layerName(layer) {
        if (layer === 'map') {
            return 'map';
        }

        return this.items ? `items-${this.items.id}` : null;
    }

    /**
     * Loads the world-item snapshot if it has appeared or changed since last time.
     *
     * Stat rather than a watcher: the file is written by a shard that may not be running, through
     * an atomic replace, and the only moments anyone cares are the ones where somebody is about to
     * look at a tile or ask what the renderer has. Both call this.
     */
    async ensureItems() {
        if (!this.child) {
            return this.items;
        }

        let stat;

        try {
            stat = fs.statSync(SNAPSHOT);
        } catch (error) {
            // No snapshot is a normal state: the shard may never have been asked for one, or may
            // not be running. The item layer is simply off and the editor says so.
            this.items = null;
            this.itemsStamp = null;
            return null;
        }

        const stamp = `${stat.mtimeMs}:${stat.size}`;

        if (stamp === this.itemsStamp) {
            return this.items;
        }

        const reply = await this.send(`items ${SNAPSHOT}`);

        if (!reply.startsWith('ok ')) {
            this.log(`Art renderer refused the world-item snapshot: ${reply.replace(/^err /, '')}`);
            this.items = null;
            this.itemsStamp = stamp;   // do not retry a bad file on every tile
            return null;
        }

        const [, count, id] = reply.split(' ');

        this.items = { id, count: Number(count), mtime: stat.mtimeMs };
        this.itemsStamp = stamp;
        this.empties.clear();

        this.log(`World items: ${count} on ${this.facet}, snapshot ${id}`);

        return this.items;
    }

    /**
     * Starts the child and waits for its handshake. Idempotent, and safe to call concurrently:
     * every caller awaits the same promise.
     */
    start() {
        if (this.child) {
            return Promise.resolve(this.info);
        }

        if (this.starting) {
            return this.starting;
        }

        if (!this.available) {
            return Promise.reject(new Error(
                `No renderer at ${this.exe}. Build it with: dotnet build tools/MapExport/MapExport.csproj -c Release`));
        }

        this.starting = new Promise((resolve, reject) => {
            const child = spawn(this.exe, ['--serve', '--facet', this.facet, '--out', this.tiles], {
                stdio: ['pipe', 'pipe', 'pipe']
            });

            const lines = readline.createInterface({ input: child.stdout });
            let handshaken = false;

            // The renderer's own diagnostics, including stack traces. Kept separate from stdout on
            // purpose so a crash can never be parsed as a reply.
            readline.createInterface({ input: child.stderr }).on('line', (line) => {
                if (line.trim().length > 0) {
                    this.log(`  renderer: ${line}`);
                }
            });

            lines.on('line', (line) => {
                if (!handshaken) {
                    handshaken = true;

                    try {
                        this.info = JSON.parse(line);
                    } catch (error) {
                        reject(new Error(`Renderer handshake was not JSON: ${line}`));
                        return;
                    }

                    this.child = child;
                    this.starting = null;
                    this.log(`Art renderer ready: v${this.info.version}, ${this.facet}, levels 0-${this.info.maxLevel}`);
                    resolve(this.info);
                    return;
                }

                this.settle(line);
            });

            child.on('error', (error) => {
                this.starting = null;
                reject(error);
            });

            child.on('exit', (code) => {
                this.log(`Art renderer exited (${code}). It will be restarted on the next tile.`);
                this.child = null;
                this.starting = null;
                this.failAll(new Error('The renderer stopped.'));
            });
        });

        return this.starting;
    }

    stop() {
        if (this.child) {
            const child = this.child;
            this.child = null;
            child.kill();
        }
    }

    /** One request through the same queue a tile uses, so nothing overtakes a render. */
    send(line) {
        return new Promise((resolve, reject) => {
            this.queue.push({
                key: line,
                line,
                raw: true,
                waiters: [{ resolve, reject }]
            });

            this.pump();
        });
    }

    /**
     * The path to a rendered tile, rendering it first if it is not cached - or null when the
     * renderer says the tile holds no items, which is most item tiles.
     *
     * Rejects rather than resolving to null on a real failure, so the caller has a reason to log
     * and the browser gets a status rather than an empty 200 that would be cached as a blank tile.
     */
    async tile(layer, floor, level, x, y) {
        await this.prepare(layer, floor, level, x, y);

        if (level > this.info.maxLevel || level < this.info.maxLevel - (this.info.artLevelDepth - 1)) {
            throw new Error(`Level ${level} is outside the art range.`);
        }

        return this.render('tile', layer, floor, level, x, y, 'png');
    }

    /**
     * The pick sidecar for a tile: which world tile and standing Z each of its pixels belongs to.
     *
     * It takes no level. There is only ever one - the deepest - because a pick map is read by
     * turning a screen point into a facet-global canvas pixel, which is the same number at every
     * zoom, so the 1:1 sidecar answers 1:2 and 1:8 exactly as well. See PickMap.cs.
     *
     * An items tile with no items in it composes to precisely the map layer's pick, so the renderer
     * answers `empty` and the MAP LAYER'S FILE IS THE ANSWER rather than a second identical copy of
     * it on disk. That is why this resolves a path from a different layer than it was asked about,
     * and why the caller never has to know.
     */
    async pick(layer, floor, x, y) {
        await this.start();

        const level = this.info.pickLevel;
        const own = await this.sidecar(layer, floor, level, x, y);

        return own === null && layer !== 'map' ? this.sidecar('map', floor, level, x, y) : own;
    }

    async sidecar(layer, floor, level, x, y) {
        await this.prepare(layer, floor, level, x, y);

        return this.render('pick', layer, floor, level, x, y, 'pick');
    }

    /**
     * The Z a mobile stands at on each of a list of `[x, y]` tiles, as an array of numbers.
     *
     * The editor draws zone rects with it: a zone is a rectangle of ground with no Z in its schema,
     * and none is being added, because where the ground is happens to be a question with an answer.
     * It is the same `map.GetAverageZ` the pick map records for land, from the same accessor, so a
     * zone corner and a waypoint on the same tile cannot disagree.
     */
    async landz(tiles) {
        if (!Array.isArray(tiles) || tiles.length === 0) {
            return [];
        }

        for (const [x, y] of tiles) {
            if (!Number.isInteger(x) || !Number.isInteger(y)) {
                throw new Error('Tiles must be pairs of integers.');
            }
        }

        await this.start();

        const reply = await this.send(`landz ${tiles.map(([x, y]) => `${x},${y}`).join(' ')}`);

        if (!reply.startsWith('ok')) {
            throw new Error(reply.replace(/^err /, ''));
        }

        return reply.split(' ').slice(1).map(Number);
    }

    /** The checks `tile` and `pick` share, and the snapshot the items layer needs. */
    async prepare(layer, floor, level, x, y) {
        if (!FLOORS.has(floor)) {
            throw new Error(`Unknown floor '${floor}'.`);
        }

        if (layer !== 'map' && layer !== 'items') {
            throw new Error(`Unknown layer '${layer}'.`);
        }

        if (!Number.isInteger(level) || !Number.isInteger(x) || !Number.isInteger(y)
            || level < 0 || x < 0 || y < 0) {
            throw new Error('Tile coordinates must be non-negative integers.');
        }

        await this.start();

        if (layer === 'items') {
            await this.ensureItems();

            if (!this.items) {
                throw new Error('No world-item snapshot. Run [WorldItems on the shard.');
            }
        }
    }

    /**
     * A cache hit, or a place in the queue behind one render of it.
     *
     * The key is the file path, which is what makes the dedupe work across callers - and what makes
     * a .png job and a .pick job for the same tile two different jobs. They are, deliberately: they
     * are asked for at different moments, and sharing a key would make a hover wait behind a render
     * it does not need.
     */
    render(verb, layer, floor, level, x, y, extension) {
        const file = this.tilePath(this.layerName(layer), floor, level, x, y, extension);

        if (fs.existsSync(file)) {
            return Promise.resolve(file);
        }

        if (this.empties.has(file)) {
            return Promise.resolve(null);
        }

        return new Promise((resolve, reject) => {
            // LIFO, and deduped: a pan can ask for the same tile several times before the first
            // answer arrives, and rendering it twice would be pure waste on a serial renderer.
            const existing = this.queue.find((job) => job.key === file);

            if (existing) {
                existing.waiters.push({ resolve, reject });
                return;
            }

            if (this.pending && this.pending.key === file) {
                this.pending.waiters.push({ resolve, reject });
                return;
            }

            this.queue.push({
                key: file,
                line: `${verb} ${layer} ${floor} ${level} ${x} ${y}`,
                waiters: [{ resolve, reject }]
            });

            this.pump();
        });
    }

    pump() {
        if (this.pending || this.queue.length === 0 || !this.child) {
            return;
        }

        const job = this.queue.pop();
        this.pending = job;

        job.timer = setTimeout(() => {
            // The child is still working; the request is not. Answering now lets the browser move
            // on, and the tile it eventually writes is picked up as a cache hit next time.
            this.finish(new Error('The render took longer than the bridge waits.'), null);
        }, RENDER_TIMEOUT_MS);

        this.child.stdin.write(job.line + '\n');
    }

    settle(line) {
        if (!this.pending) {
            this.log(`Art renderer said "${line}" with nothing outstanding.`);
            return;
        }

        // A non-tile request - loading a snapshot - wants the reply verbatim rather than a path.
        if (this.pending.raw) {
            this.finish(null, line);
            return;
        }

        // Nothing in this tile. Not an error and not a file: remembered, so the same tile is never
        // asked about twice under this snapshot.
        if (line === 'empty') {
            this.empties.add(this.pending.key);
            this.finish(null, null);
            return;
        }

        if (line.startsWith('ok ')) {
            const [, ms] = line.split(' ');
            const elapsed = Number(ms) || 0;

            this.stats.rendered++;
            this.stats.lastMs = elapsed;
            this.stats.totalMs += elapsed;

            this.log(`  art ${this.pending.line} rendered in ${elapsed} ms`);
            this.finish(null, this.pending.key);
            return;
        }

        this.stats.failed++;
        this.finish(new Error(line.replace(/^err /, '')), null);
    }

    finish(error, file) {
        const job = this.pending;

        if (!job) {
            return;
        }

        clearTimeout(job.timer);
        this.pending = null;

        for (const waiter of job.waiters) {
            error ? waiter.reject(error) : waiter.resolve(file);
        }

        this.pump();
    }

    failAll(error) {
        const jobs = this.queue.splice(0, this.queue.length);

        if (this.pending) {
            clearTimeout(this.pending.timer);
            jobs.push(this.pending);
            this.pending = null;
        }

        for (const job of jobs) {
            for (const waiter of job.waiters) {
                waiter.reject(error);
            }
        }
    }
}

/**
 * Splits "/tiles/iso/<facet>/v<n>/<layer>/<floor>/<level>/<x>/<y>.<ext>" into its parts, or null.
 *
 * `ext` is a CLOSED SET - `png` for the picture, `pick` for the sidecar beside it - rather than a
 * pattern. A `\w+` here would be the same mistake as sanitising a name instead of refusing it: the
 * whole safety of this route is that every segment is checked against what it is allowed to BE and
 * the path is then rebuilt from the result.
 *
 * Every segment is checked against what it is allowed to be rather than sanitised, which is the
 * same rule the token names follow: a name that has to be cleaned up before it is safe is a name
 * worth refusing. There is no path from the caller reaching the filesystem here - the parts are
 * numbers, one of three floor names, and a layer that is either the literal `map` or `items-` and
 * a snapshot id, and the path is rebuilt from them rather than taken.
 */
function parseTilePath(urlPath) {
    const parts = urlPath.split('/').filter((part) => part.length > 0);

    // tiles iso <facet> v<n> <layer> <floor> <level> <x> <y>.png
    if (parts.length !== 9 || parts[0] !== 'tiles' || parts[1] !== 'iso') {
        return null;
    }

    if (!/^[A-Za-z]+$/.test(parts[2]) || !/^v\d+$/.test(parts[3])) {
        return null;
    }

    // A snapshot id is what WorldItemSnapshot.BuildId writes: facet, sequence, timestamp, count.
    if (parts[4] !== 'map' && !/^items-[A-Za-z0-9-]{1,120}$/.test(parts[4])) {
        return null;
    }

    if (!FLOORS.has(parts[5])) {
        return null;
    }

    if (!/^\d+$/.test(parts[6]) || !/^\d+$/.test(parts[7])) {
        return null;
    }

    const leaf = /^(\d+)\.(png|pick)$/.exec(parts[8]);

    if (!leaf) {
        return null;
    }

    return {
        facet: parts[2],
        version: Number(parts[3].slice(1)),
        layer: parts[4] === 'map' ? 'map' : 'items',
        layerName: parts[4],
        floor: parts[5],
        level: Number(parts[6]),
        x: Number(parts[7]),
        y: Number(leaf[1]),
        extension: leaf[2]
    };
}

module.exports = { ArtRenderer, parseTilePath, RENDER_TIMEOUT_MS, EXE, TILES, SNAPSHOT, EMPTY_PNG };
