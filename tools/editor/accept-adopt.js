#!/usr/bin/env node
//
// accept-adopt.js — accept an adopt proposal WITHOUT the browser, through the editor's own save.
//
// WHY THIS EXISTS, and why it is not a second writer. NavAdopt's safety property is that it cannot
// write navigation data: it walks a region and writes a proposal to Data/Live/nav-adopt.json, and
// a person accepts it through the editor's ordinary Save. That property is worth keeping, and a
// script that read the proposal and edited navigation.json itself would quietly destroy it - there
// would then be a path from the adopt to the nav file that no validator and no hash check stood in.
//
// So this drives the SAME endpoint the Save button drives:
//
//     GET  /api/shapes                -> the shapes, and the hash of navigation.json
//     POST /api/save/navigation       -> {baseHash, creates, updates, deletes}
//
// Which means it inherits, rather than reimplements, every part of accepting a proposal:
//
//   * the same survivors() rule from js/adopt.js decides which proposed records are written -
//     the module that exists precisely so that rule is testable outside app.js;
//   * the same unproject() turns shapes back into our JSON layout;
//   * the same replicated validator runs on the dry run;
//   * the same stale-file check refuses on a baseHash mismatch, so a proposal walked against one
//     version of the graph cannot land on another;
//   * the same .bak, temp-file-and-rename write, and the same reload token afterwards.
//
// A REBASE PROPOSAL IS THE REASON IT IS NEEDED. An ordinary adopt is all creates and a person can
// click Save. A rebase also carries `removals` - waypoints of OURS the proposed road runs through -
// and `rewrites`, the approach and step lists that named one. Applying those by hand across a few
// hundred records is not review, it is transcription, and transcription is where the mistakes are.
//
// USAGE
//
//     node tools/editor/accept-adopt.js               # dry run: validate and report, write nothing
//     node tools/editor/accept-adopt.js --write       # accept
//     node tools/editor/accept-adopt.js --write --port 8081
//
// It refuses on anything short of a finished, unblocked proposal, and on any fatal finding from the
// dry run. Exit code is 0 only when what was asked for actually happened.

'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');

const REPO = path.join(__dirname, '..', '..');
const PROPOSAL = path.join(REPO, 'Data', 'Live', 'nav-adopt.json');

function fail(message) {
    console.error(`accept-adopt: ${message}`);
    process.exit(1);
}

/**
 * The secret a non-browser caller needs for a mutating request, from the environment.
 *
 * The bridge prints it on its startup line and accepts it in X-GG-Auth; a browser is authorised by
 * being same-origin instead, which a script cannot be. Set GG_BRIDGE_SECRET for both processes, or
 * copy the printed value into this one's environment:
 *
 *     $env:GG_BRIDGE_SECRET = '<the value from the bridge Auth: line>'
 *
 * Missing is not a failure here: it produces a 403 with the reason, which is a better message than
 * anything this file could invent about a value it cannot see.
 */
function authHeaders() {
    const secret = process.env.GG_BRIDGE_SECRET;

    return secret ? { 'X-GG-Auth': secret } : {};
}

function request(options, body) {
    return new Promise((resolve, reject) => {
        const req = http.request(options, (response) => {
            const chunks = [];

            response.on('data', (chunk) => chunks.push(chunk));
            response.on('end', () => resolve({
                status: response.statusCode,
                text: Buffer.concat(chunks).toString('utf8')
            }));
        });

        req.on('error', reject);

        if (body !== undefined) {
            req.write(body);
        }

        req.end();
    });
}

async function main() {
    const args = process.argv.slice(2);
    const write = args.includes('--write');
    const portAt = args.indexOf('--port');
    const port = portAt >= 0 ? Number(args[portAt + 1]) : 8081;

    if (!Number.isInteger(port) || port <= 0) {
        fail('--port needs a number.');
    }

    if (!fs.existsSync(PROPOSAL)) {
        fail(`no proposal at ${path.relative(REPO, PROPOSAL)}. Run an adopt first.`);
    }

    const proposal = JSON.parse(fs.readFileSync(PROPOSAL, 'utf8'));

    // The proposal is rewritten on every pass so the dialog can show progress, so an unfinished
    // one is a perfectly well-formed file describing a walk that is still happening. Accepting it
    // would write a partial road - and `status` says done only once joins, corridors, islands and
    // pruning have all run, which is the thing the editor once latched too early.
    if (proposal.status !== 'done') {
        fail(`the proposal is '${proposal.status}', not 'done' - the walk has not finished.`);
    }

    // The one refusal the editor makes outright: every record in an island is individually valid,
    // which is exactly what makes an island the fault nothing else can show.
    const adopt = await import('./js/adopt.js');

    if (adopt.blocked(proposal)) {
        fail('this proposal reaches nothing already saved (NO JOIN WAS MADE). Discard it.');
    }

    const creates = adopt.survivors(proposal);
    const updates = adopt.rewrites(proposal);
    const deletes = adopt.removals(proposal);

    console.log(adopt.summary(proposal, creates.length).join('\n'));
    console.log('');
    console.log(`accept-adopt: ${creates.length} create(s), ${updates.length} update(s),`
        + ` ${deletes.length} delete(s).`);

    const shapes = await request({
        host: '127.0.0.1', port, path: '/api/shapes', method: 'GET'
    });

    if (shapes.status !== 200) {
        fail(`the bridge answered ${shapes.status} for /api/shapes. Is it running on ${port}?`);
    }

    const baseHash = JSON.parse(shapes.text).files.navigation.hash;

    async function post(dryRun) {
        const body = JSON.stringify({ baseHash, creates, updates, deletes, dryRun });

        return request({
            host: '127.0.0.1', port, path: '/api/save/navigation', method: 'POST',
            headers: Object.assign({
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(body)
            }, authHeaders())
        }, body);
    }

    // ALWAYS THE DRY RUN FIRST, even when --write was asked for. The bridge deliberately does not
    // validate a real save - a false positive in a replica must never block a write the shard would
    // accept - so the dry run is the only place the validator speaks, and on a proposal this size
    // its findings are the review.
    const checked = await post(true);
    const findings = JSON.parse(checked.text);

    for (const key of ['fatal', 'warnings', 'notes']) {
        for (const finding of findings[key] || []) {
            console.log(`  ${key.toUpperCase().replace(/S$/, '')}: ${finding.message}`);
        }
    }

    if (checked.status !== 200) {
        fail(`the dry run refused (${checked.status}): ${findings.error || 'see the findings above'}.`);
    }

    if ((findings.fatal || []).length > 0) {
        fail(`${findings.fatal.length} fatal finding(s). Nothing was written.`);
    }

    if (!write) {
        console.log('');
        console.log('accept-adopt: dry run only. Nothing was written. Pass --write to accept.');
        return;
    }

    const saved = await post(false);
    const result = JSON.parse(saved.text);

    if (saved.status === 409) {
        fail('navigation.json changed since the proposal was walked'
            + ` (proposal saw ${result.expected}, the file is at ${result.actual}).`
            + ' Re-run the adopt against the current graph.');
    }

    if (saved.status !== 200 || !result.written) {
        fail(`the save failed (${saved.status}): ${result.error || saved.text}`);
    }

    console.log('');
    console.log(`accept-adopt: written. hash ${result.hash}, backup ${result.backup}.`);
    console.log(`accept-adopt: reloaded ${result.reloaded} - ${result.message}`);

    for (const warning of result.warnings || []) {
        console.log(`  SHARD WARNING: ${warning}`);
    }

    // WRITTEN AND NOT RELOADED IS NOT SUCCESS HERE, even though the bridge answers 200 for it. The
    // bridge is right to: the file is on disk and the caller has to be told both halves. But this
    // script's exit code is the only thing a person running it headlessly reads, and a nav file the
    // shard refused is not an accepted proposal.
    if (!result.reloaded) {
        fail('the file was written but the shard refused to reload it.'
            + ' Use POST /api/restore/navigation to put the .bak back.');
    }
}

main().catch((error) => fail(error.message));
