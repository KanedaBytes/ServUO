'use strict';

// A stand-in for RequestPoller, so the save round trip can be tested without starting the shard.
//
// It is worth being precise about what this does and does not prove. It is NOT a mock of the
// bridge's internals - it speaks the real file protocol: it watches the request directory, CLAIMS
// a token by renaming it, reads it, RUNS THE REQUEST, deletes the claimed file, and only then
// writes <name>.<id>.ack.json in exactly the shape RequestPoller.WriteAck builds by hand. That
// order and that shape are the contract between the two processes, and they are where the
// stale-ack bug lived; a mock of the bridge would have sailed straight past it.
//
// THE PROTOCOL IS WRITTEN DOWN in tools/editor/README.md ("The request channel"), and that text is
// the authority - not this file, and not RequestPoller.cs. REVIEW.md section 8 warned about
// overfitting a fake to one reading of the poller: this one's first version deleted the token
// BEFORE dispatching while the real one deleted afterwards, and both called it "delete first". The
// claim step closes that class of drift: a claimed token is a renamed file on both sides, and a
// test can see it.
//
// COMMIT AND RESTORE ARE BUILT IN, because since 22 September 2026 the shard is the writer of the
// editor's data files (DataFileCommit.cs). The fake does the same version check and the same
// replace in Node, with the same refusal text, and then asks `respond` for the reload half - so a
// test that scripts `respond(() => ({ ok: true, message: '75 waypoint(s) ...' }))` gets that as the
// commit's answer, exactly as the real poller's ack carries NavigationSystem.TryReload's words.
//
// What it cannot prove is that the shard reloads anything. That is what the live check at the end
// of the step is for.
//
// Polling rather than fs.watch: fs.watch on Windows misses and duplicates events often enough to
// make a test flaky, and a 25ms poll of one small directory costs nothing.

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const POLL_MS = 25;

// The same rule as RequestPoller.TokenName and whitelist.js's TOKEN_NAME + REQUEST_ID.
const TOKEN = /^([a-z][a-z0-9-]{0,63})\.([a-z0-9]{1,32})\.token$/;

const NO_FILE = 'none';

const OUTSIDE = 'outside the shard (a hand edit, git, or before this boot)';

/**
 * @param {object} options
 * @param {string} options.dir  the request directory to watch
 * @param {(name: string, body: string, id: string) => object|null} options.respond
 *        What the shard would answer: {ok, message, errors, warnings, generation, bootId}. For
 *        a commit or restore this is asked for the RELOAD half, with the reload's request name
 *        (nav-reload, spawn-reload, ...) and body. Returning null answers nothing at all, which
 *        is how the "shard died between dispatch and ack" path is tested.
 * @param {(key: string) => string|null} options.resolve
 *        The data file a commit key names - whitelist.resolveSave in the tests. Required for
 *        commit and restore; without it they are refused as unknown files.
 * @param {string} options.bootId   the boot this fake answers as; one random id per fake by default
 * @param {boolean} options.claimOnly  claim every token and then say nothing: a shard that died
 *        with the request running, which is the Unknown outcome
 * @param {string} options.ackId   put THIS id inside every ack rather than the token's, in the
 *        file named by the token's id: an ack in the right place carrying somebody else's id,
 *        which the bridge must ignore. (An ack under another file name is never opened at all -
 *        that is what naming the file by the id buys.)
 */
function start(options) {
    const dir = options.dir;
    const respond = options.respond;
    const resolve = options.resolve || (() => null);
    const bootId = options.bootId || FAKE_BOOT_ID;
    const seen = [];

    let timer = null;
    let stopped = false;

    function tick() {
        if (stopped) {
            return;
        }

        let names = [];

        try {
            names = fs.readdirSync(dir);
        } catch {
            // The directory need not exist yet; the bridge creates it when it first drops a token.
        }

        for (const file of names) {
            const match = TOKEN.exec(file);

            if (!match) {
                continue;
            }

            const [, name, id] = match;
            const full = path.join(dir, file);
            const claimed = path.join(dir, `${name}.${id}.claimed`);

            // CLAIM: the rename is atomic, so either this poll owns the request or the bridge
            // withdrew it a moment ago (its timeout deletes an unclaimed token and reports NotRun).
            try {
                fs.renameSync(full, claimed);
            } catch {
                continue;
            }

            let body = '';

            try {
                body = fs.readFileSync(claimed, 'utf8').trim();
            } catch {
                fs.rmSync(claimed, { force: true });
                continue;
            }

            seen.push({ name, id, body });

            if (options.claimOnly) {
                // Died mid-request: the claimed file is what a crash leaves, and the next boot's
                // sweep is what removes it. Left in place so a test can see "running".
                continue;
            }

            // Read, claim, run, delete, acknowledge - RequestPoller.Handle's order exactly.
            let answer;

            if (name === 'commit') {
                answer = commit(id, body, resolve, respond);
            } else if (name === 'restore') {
                answer = restore(id, body, resolve, respond);
            } else {
                answer = respond ? respond(name, body, id) : { ok: true, message: 'ok' };
            }

            fs.rmSync(claimed, { force: true });

            // A null answer is a shard that ran the request and then said nothing - a crash
            // between dispatch and acknowledgement. The token is gone either way, which is what
            // makes that outcome UNKNOWN rather than merely failed.
            if (!answer) {
                continue;
            }

            writeAck(dir, name, id, body, options.ackId ? Object.assign({ id: options.ackId }, answer) : answer, bootId);
        }

        timer = setTimeout(tick, POLL_MS);
    }

    tick();

    return {
        /** Every token this shard has claimed, in order, with its id and body. */
        seen,
        stop() {
            stopped = true;

            if (timer) {
                clearTimeout(timer);
            }
        }
    };
}

// ---- the version check and the replace, as DataFileCommit does them --------------------------------

/** sha256 of the file's bytes, first sixteen hex, or 'none' when absent - bridge.js hashOf's twin. */
function hash16(file) {
    if (!fs.existsSync(file)) {
        return NO_FILE;
    }

    return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').slice(0, 16);
}

function fields(body) {
    const out = {};

    for (const word of body.split(/\s+/)) {
        const eq = word.indexOf('=');

        if (eq > 0) {
            out[word.slice(0, eq)] = word.slice(eq + 1);
        }
    }

    return out;
}

function changedAt(file) {
    try {
        return fs.statSync(file).mtime.toISOString();
    } catch {
        return null;
    }
}

function describe(file) {
    const at = changedAt(file);

    return at === null ? 'there is no file to have been written' : `last written ${at} by ${OUTSIDE}`;
}

/** DataFileCommit.StaleMessage, word for word. */
function staleMessage(display, file, actual, expected, verb) {
    if (actual === NO_FILE) {
        return `${display} does not exist, but this ${verb} was based on version ${expected}; it was deleted or moved since you loaded it`;
    }

    if (expected === NO_FILE) {
        return `${display} already exists (at ${actual}), but this ${verb} expected to create it; ${describe(file)}. Reload and edit the file that is there`;
    }

    return `${display} is at ${actual}, not the ${expected} this ${verb} was based on; ${describe(file)}. Reload before writing it again`;
}

/** The reload request a data file key maps to, and the body that reload takes. */
function reloadFor(key) {
    const spawn = /^spawn:(.+)$/.exec(key);

    if (spawn) {
        return { name: 'spawn-reload', body: spawn[1] };
    }

    return {
        name: { navigation: 'nav-reload', dailyLife: 'dailylife-reload', restrictedZones: 'zones-reload' }[key],
        body: ''
    };
}

function runReload(key, id, info, written, wanted, respond) {
    if (!wanted) {
        return { ok: true, message: `${written}; not reloaded`, commit: info };
    }

    const reload = reloadFor(key);
    const answer = respond ? respond(reload.name, reload.body, id) : { ok: true, message: 'ok' };

    if (!answer) {
        return null;
    }

    info.reloaded = answer.ok === true;

    return Object.assign({}, answer, { commit: info });
}

function commit(id, body, resolve, respond) {
    const spec = fields(body);
    const live = resolve(spec.file);

    if (!live) {
        return { ok: false, message: `commit refused: no such data file '${spec.file}'` };
    }

    const display = path.basename(live);
    const staged = `${live}.${id}.staged`;
    const actual = hash16(live);

    const info = {
        written: false, refused: false, reloaded: false, hash: null, backupHash: null,
        actual, expected: spec.base, changedAt: changedAt(live), changedBy: OUTSIDE
    };

    if (actual !== spec.base) {
        fs.rmSync(staged, { force: true });
        info.refused = true;
        return { ok: false, message: staleMessage(display, live, actual, spec.base, 'save'), commit: info };
    }

    if (!fs.existsSync(staged)) {
        info.refused = true;
        return {
            ok: false, commit: info,
            message: `nothing is staged for ${display} under commit ${id}; the bridge did not write ${path.basename(staged)}, or a boot swept it`
        };
    }

    const wrote = hash16(staged);

    if (wrote !== spec.wrote) {
        fs.rmSync(staged, { force: true });
        info.refused = true;
        return {
            ok: false, commit: info,
            message: `the staged file for ${display} hashes to ${wrote}, not the ${spec.wrote} the bridge said it wrote; nothing was committed`
        };
    }

    if (fs.existsSync(live)) {
        fs.copyFileSync(live, live + '.bak');
    }

    fs.renameSync(staged, live);

    info.written = true;
    info.hash = wrote;
    info.backupHash = actual;
    info.changedAt = new Date().toISOString();
    info.changedBy = `editor commit ${id}`;

    return runReload(spec.file, id, info, `${display} committed at ${wrote} (was ${actual})`, spec.reload !== 'no', respond);
}

function restore(id, body, resolve, respond) {
    const spec = fields(body);
    const live = resolve(spec.file);

    if (!live) {
        return { ok: false, message: `restore refused: no such data file '${spec.file}'` };
    }

    const display = path.basename(live);
    const backup = live + '.bak';
    const staged = `${live}.${id}.staged`;
    const actual = hash16(live);

    const info = {
        written: false, refused: false, reloaded: false, hash: null, backupHash: null,
        actual, expected: spec.base, changedAt: changedAt(live), changedBy: OUTSIDE
    };

    if (actual !== spec.base) {
        info.refused = true;
        return { ok: false, message: staleMessage(display, live, actual, spec.base, 'restore'), commit: info };
    }

    if (!fs.existsSync(backup)) {
        info.refused = true;
        info.expected = spec.backup;
        info.actual = NO_FILE;
        return { ok: false, message: `there is no ${display}.bak to restore`, commit: info };
    }

    const backupHash = hash16(backup);

    if (backupHash !== spec.backup) {
        info.refused = true;
        info.expected = spec.backup;
        info.actual = backupHash;
        info.changedAt = changedAt(backup);
        return {
            ok: false, commit: info,
            message: `${display}.bak is at ${backupHash}, not the ${spec.backup} this restore expected: the backup on disk `
                + `is not the one you would be restoring (${describe(backup)}). Reload and look at what is there.`
        };
    }

    // The replaced live file becomes the new .bak - what the world is running, which is what
    // spawn-reload has to unload.
    fs.copyFileSync(backup, staged);
    fs.copyFileSync(live, backup);
    fs.renameSync(staged, live);

    info.written = true;
    info.hash = backupHash;
    info.backupHash = actual;
    info.changedAt = new Date().toISOString();
    info.changedBy = `editor restore ${id}`;

    return runReload(spec.file, id, info,
        `${display} restored to ${backupHash}; the file it replaced (${actual}) is now the .bak`, spec.reload !== 'no', respond);
}

// ---- the ack -------------------------------------------------------------------------------------

/**
 * Byte-for-byte the shape RequestPoller.WriteAck produces, including the echoed token body.
 *
 * `id` is what the bridge matches on; `outcome` is completed or faulted (an ack never says
 * unknown - an ack is by definition an answer); `generation` and `bootId` are what every real ack
 * carries since 21 September 2026 - the completed persistence generation and the answering
 * process. The fake takes them from the answer so a test can script a shard at generation 7, and
 * defaults them the way a shard that has never saved would: generation 0, and one boot for the
 * life of this fake.
 */
const FAKE_BOOT_ID = 'fake' + Math.random().toString(16).slice(2, 14);

function writeAck(dir, name, id, token, answer, bootId) {
    const payload = {
        request: name,
        // The id inside is the token's unless the answer carries its own (the ackId seam).
        id: typeof answer.id === 'string' ? answer.id : id,
        outcome: answer.outcome || (answer.ok === true ? 'completed' : 'faulted'),
        token,
        ok: answer.ok === true,
        message: answer.message || '',
        errors: answer.errors || [],
        warnings: answer.warnings || []
    };

    if (answer.commit) {
        payload.commit = answer.commit;
    }

    payload.generation = Number.isInteger(answer.generation) ? answer.generation : 0;
    payload.bootId = answer.bootId || bootId || FAKE_BOOT_ID;
    payload.utc = new Date().toISOString();

    const file = path.join(dir, `${name}.${id}.ack.json`);

    // Temp then rename, because AtomicFile.Write does: the bridge polls this file and must never
    // read it half-written.
    fs.writeFileSync(file + '.tmp', JSON.stringify(payload, null, 2) + '\n', 'utf8');
    fs.renameSync(file + '.tmp', file);
}

module.exports = { start, writeAck, hash16, FAKE_BOOT_ID };
