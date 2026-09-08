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
//     <- {"ready":true,"version":1,...}      the handshake, once
//     -> tile <floor> <level> <x> <y>
//     <- ok <ms> <bytes> <path>   or   err <message>
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

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const readline = require('readline');

const ROOT = path.resolve(__dirname, '..', '..');
const EXE = path.join(ROOT, 'tools', 'MapExport', 'bin', 'Release', 'MapExport.exe');
const TILES = path.join(__dirname, 'tiles');

/** Long enough for the coarsest art level on a cold art cache; past it, the browser retries. */
const RENDER_TIMEOUT_MS = Number(process.env.GG_ART_TIMEOUT_MS) || 30000;

const FLOORS = new Set(['ground', 'first', 'all']);

class ArtRenderer {
    constructor(options = {}) {
        this.exe = options.exe || EXE;
        this.tiles = options.tiles || TILES;
        this.facet = options.facet || 'Trammel';
        this.log = options.log || (() => {});

        this.child = null;
        this.info = null;
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
            rendered: this.stats.rendered,
            failed: this.stats.failed,
            queued: this.queue.length + (this.pending ? 1 : 0),
            lastMs: this.stats.lastMs,
            avgMs: this.stats.rendered > 0 ? Math.round(this.stats.totalMs / this.stats.rendered) : 0
        };
    }

    /** The cache path for a tile. The renderer builds the same one; both derive it from `info`. */
    tilePath(floor, level, x, y) {
        if (!this.info) {
            return null;
        }

        return path.join(
            this.tiles, 'iso', this.facet, `v${this.info.version}`,
            floor, String(level), String(x), `${y}.png`);
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

    /**
     * The path to a rendered tile, rendering it first if it is not cached.
     *
     * Rejects rather than resolving to null, so the caller has a reason to log and the browser
     * gets a status rather than an empty 200 that would be cached as a blank tile.
     */
    async tile(floor, level, x, y) {
        if (!FLOORS.has(floor)) {
            throw new Error(`Unknown floor '${floor}'.`);
        }

        if (!Number.isInteger(level) || !Number.isInteger(x) || !Number.isInteger(y)
            || level < 0 || x < 0 || y < 0) {
            throw new Error('Tile coordinates must be non-negative integers.');
        }

        await this.start();

        if (level > this.info.maxLevel || level < this.info.maxLevel - (this.info.artLevelDepth - 1)) {
            throw new Error(`Level ${level} is outside the art range.`);
        }

        const file = this.tilePath(floor, level, x, y);

        if (fs.existsSync(file)) {
            return file;
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
                line: `tile ${floor} ${level} ${x} ${y}`,
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

        if (line.startsWith('ok ')) {
            const [, ms] = line.split(' ');
            const elapsed = Number(ms) || 0;

            this.stats.rendered++;
            this.stats.lastMs = elapsed;
            this.stats.totalMs += elapsed;

            this.log(`  art ${this.pending.line.slice('tile '.length)} rendered in ${elapsed} ms`);
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
 * Splits "/tiles/iso/<facet>/v<n>/<floor>/<level>/<x>/<y>.png" into its parts, or null.
 *
 * Every segment is checked against what it is allowed to be rather than sanitised, which is the
 * same rule the token names follow: a name that has to be cleaned up before it is safe is a name
 * worth refusing. There is no path from the caller reaching the filesystem here - the parts are
 * numbers and one of three floor names, and the path is rebuilt from them.
 */
function parseTilePath(urlPath) {
    const parts = urlPath.split('/').filter((part) => part.length > 0);

    // tiles iso <facet> v<n> <floor> <level> <x> <y>.png
    if (parts.length !== 8 || parts[0] !== 'tiles' || parts[1] !== 'iso') {
        return null;
    }

    if (!/^[A-Za-z]+$/.test(parts[2]) || !/^v\d+$/.test(parts[3]) || !FLOORS.has(parts[4])) {
        return null;
    }

    if (!/^\d+$/.test(parts[5]) || !/^\d+$/.test(parts[6]) || !/^\d+\.png$/.test(parts[7])) {
        return null;
    }

    return {
        facet: parts[2],
        version: Number(parts[3].slice(1)),
        floor: parts[4],
        level: Number(parts[5]),
        x: Number(parts[6]),
        y: Number(parts[7].slice(0, -'.png'.length))
    };
}

module.exports = { ArtRenderer, parseTilePath, RENDER_TIMEOUT_MS, EXE, TILES };
