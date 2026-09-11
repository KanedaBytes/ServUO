'use strict';

// A stand-in for RequestPoller, so the save round trip can be tested without starting the shard.
//
// It is worth being precise about what this does and does not prove. It is NOT a mock of the
// bridge's internals - it speaks the real file protocol: it watches the request directory, reads
// the token, RUNS THE REQUEST, deletes the token, and only then writes <name>.ack.json in exactly
// the shape RequestPoller.WriteAck builds by hand. That order and that shape are the contract
// between the two processes, and they are where the stale-ack bug lived; a mock of the bridge
// would have sailed straight past it.
//
// THE ORDER USED TO BE WRONG HERE, and it mattered. This deleted the token BEFORE calling respond,
// while RequestPoller.Handle dispatches first and deletes afterwards (RequestPoller.cs:116-131).
// Both files called that "delete first" and meant different things by it - the real one means
// before the ACKNOWLEDGEMENT. The difference is the entire F3 window: the shard is holding a token
// it has read and not yet deleted for as long as the request takes to run, and a second request
// written into that window is deleted, unread, by the first one's cleanup. A fake that deletes up
// front has no such window and cannot reproduce the defect it is supposed to guard (REVIEW.md
// section 8).
//
// What it cannot prove is that the shard reloads anything. That is what the live check at the end
// of the step is for.
//
// Polling rather than fs.watch: fs.watch on Windows misses and duplicates events often enough to
// make a test flaky, and a 25ms poll of one small directory costs nothing.

const fs = require('fs');
const path = require('path');

const POLL_MS = 25;

/**
 * @param {object} options
 * @param {string} options.dir  the request directory to watch
 * @param {(name: string, body: string) => object|null} options.respond
 *        What the shard would answer: {ok, message, errors, warnings}. Returning null answers
 *        nothing at all, which is how the "shard is not running" path is tested.
 */
function start(options) {
    const dir = options.dir;
    const respond = options.respond;
    const seen = [];

    let timer = null;
    let stopped = false;

    function tick() {
        if (stopped) {
            return;
        }

        let names = [];

        try {
            names = fs.readdirSync(dir).filter((name) => name.endsWith('.token'));
        } catch {
            // The directory need not exist yet; the bridge creates it when it first drops a token.
        }

        for (const file of names) {
            const name = file.slice(0, -'.token'.length);
            const full = path.join(dir, file);

            let body = '';

            try {
                body = fs.readFileSync(full, 'utf8').trim();
            } catch {
                continue;
            }

            seen.push({ name, body });

            // Read, run, delete, acknowledge - RequestPoller.Handle's order exactly. The delete is
            // before the ack and after the dispatch, and it is unconditional: a token that
            // survives its own failure is retried every tick, and one that outlives its ack looks
            // exactly like a bridge that never wrote it.
            const answer = respond ? respond(name, body) : { ok: true, message: 'ok' };

            fs.rmSync(full, { force: true });

            // A null answer is a shard that read the request and then said nothing - a crash
            // between dispatch and acknowledgement. The token is gone either way, which is what
            // makes that outcome UNKNOWN rather than merely failed.
            if (!answer) {
                continue;
            }

            writeAck(dir, name, body, answer);
        }

        timer = setTimeout(tick, POLL_MS);
    }

    tick();

    return {
        /** Every token this shard has picked up, in order, with its body. */
        seen,
        stop() {
            stopped = true;

            if (timer) {
                clearTimeout(timer);
            }
        }
    };
}

/** Byte-for-byte the shape RequestPoller.WriteAck produces, including the echoed token body. */
function writeAck(dir, name, token, answer) {
    const payload = {
        request: name,
        token,
        ok: answer.ok === true,
        message: answer.message || '',
        errors: answer.errors || [],
        warnings: answer.warnings || [],
        utc: new Date().toISOString()
    };

    const file = path.join(dir, name + '.ack.json');

    // Temp then rename, because AtomicFile.Write does: the bridge polls this file and must never
    // read it half-written.
    fs.writeFileSync(file + '.tmp', JSON.stringify(payload, null, 2) + '\n', 'utf8');
    fs.renameSync(file + '.tmp', file);
}

module.exports = { start, writeAck };
