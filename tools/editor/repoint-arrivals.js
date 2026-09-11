#!/usr/bin/env node
//
// repoint-arrivals.js — point every arrival AND every destination at the waypoint a bot should
// actually leave from.
//
// BOTH RECORDS CARRY THE SAME FIELD AND BOTH GET IT WRONG THE SAME WAY. A destination's list is
// checked here as well as its arrivals', because brit-home-baker had correct arrivals three tiles
// from the road and a destination still naming a waypoint 208 tiles away, and the walk audit failed
// both of its approaches with no path at all while every other instrument read clean.
//
// WHAT AN ARRIVAL'S `waypoints` FIELD IS FOR. A route ends at the first of them that exists and
// then appends the arrival tile, so it is the record that decides the LAST HOP - the one
// `Scripts/Custom/Core/Navigation/README.md` calls "the hop nobody audited". A waypoint twenty
// tiles from the arrival it is attached to gives the walker a leg it cannot plan, and the walker
// finds out about it standing in the street.
//
// WHY IT NEEDS DOING AT ALL. Nothing keeps the field honest as the graph moves underneath it.
// `Nav.Data` warns only when an arrival is beyond the hop cap of EVERY waypoint - which is the
// stranding case, not this one - so an arrival whose own listed waypoint is far away while some
// other waypoint is close by warns about nothing and reads as fine. The Britain rebase made that
// visible by moving the whole town's road at once: 21 arrivals came out over the cap from the
// waypoint they name, and 49 named something that was no longer the nearest.
//
// WHAT IT DOES NOT TOUCH. The arrival's tile, its Z, and its `exclusive` / `exact` / `range` flags
// are authored - somebody stood there and chose them - and this only ever rewrites the approach
// list. It also only considers waypoints that are REACHABLE from the graph's home waypoint,
// flooded here rather than assumed: pointing an arrival at a nearer waypoint on an island would
// trade a long last hop for no route at all.
//
// It keeps every listed waypoint that is still inside the cap, and puts the nearest reachable one
// first. Keeping the others matters: a shop fronting two streets is approached from both, and the
// route picks whichever its search reaches first.
//
// USAGE
//
//     node tools/editor/repoint-arrivals.js --tag britain             # dry run
//     node tools/editor/repoint-arrivals.js --tag britain --write
//     node tools/editor/repoint-arrivals.js --dest brit-home-jeweler --write
//
// `--tag` restricts it to destinations carrying that tag, because a town is the unit somebody
// re-bases; without one it would re-point the whole facet, and Trinsic is deliberately untouched.
//
// `--dest` narrows it to ONE destination, which is the unit somebody RELOCATES. The smaller unit
// earns its own flag because "nearest first" is not always an improvement: a waypoint an arrival
// names may be a DOOR rather than a road, and prepending a road waypoint two tiles away moves the
// last hop off the threshold and onto the street - brit-shop-tanner's cheapest approach went from
// 1 walked tile to 20 that way, with no instrument reading wrong (see commit 118be12f). So a
// whole-town pass is for a re-base, and a single moved record is re-pointed on its own.

'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');

const { REPO_ROOT, FILES } = require('./whitelist.js');

// THE RULE MOVED TO js/repoint.js AND THIS CALLS IT. It used to live here, which meant the editor
// could not run it - a record dragged in the browser kept naming whatever it named before, and
// brit-home-jeweler is what that cost. The flood, the nearest-first rebuild and the hop cap are all
// that module's now; this file keeps the CLI, the scoping flags and the save.
//
// `HOP_CAP` in particular used to be a third independent copy of the number, untested: the
// validate.js one is pinned to Config/Custom.cfg by coverage.test.js and this one was not.
//
// Imported dynamically because js/repoint.js is an ES module the browser loads and this file is
// CommonJS. `main()` is already async, so there is nothing to restructure.
let repoint;

function fail(message) {
    console.error(`repoint-arrivals: ${message}`);
    process.exit(1);
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
    repoint = await import('./js/repoint.js');

    const { HOP_CAP, reachableFrom, repointFor } = repoint;
    const args = process.argv.slice(2);
    const write = args.includes('--write');
    const tagAt = args.indexOf('--tag');
    const tag = tagAt >= 0 ? args[tagAt + 1] : null;
    const destAt = args.indexOf('--dest');
    const only = destAt >= 0 ? args[destAt + 1] : null;
    const portAt = args.indexOf('--port');
    const port = portAt >= 0 ? Number(args[portAt + 1]) : 8081;
    const homeAt = args.indexOf('--home');
    const home = homeAt >= 0 ? args[homeAt + 1] : 'uo-britain-bank';

    const nav = JSON.parse(fs.readFileSync(FILES.navigation, 'utf8'));
    const byId = new Map(nav.waypoints.map((waypoint) => [waypoint.id, waypoint]));
    const destinations = new Map(nav.destinations.map((d) => [d.id, d]));

    if (!byId.has(home)) {
        fail(`the home waypoint '${home}' is not in navigation.json - pass --home.`);
    }

    const canReach = reachableFrom(nav, home);

    console.log(`repoint-arrivals: ${nav.waypoints.length} waypoint(s),`
        + ` ${canReach.size} reachable from ${home}.`);

    // Arrivals are addressed as the nth of their destination, exactly as the editor addresses
    // them (project.js `arr:<dest>#<n>`), so the index has to be counted the same way.
    const index = new Map();
    const updates = [];
    const stranded = [];
    const options = { canReach, byId, cap: HOP_CAP };

    // A DESTINATION HAS THE SAME FIELD AND THE SAME FAULT. A route ends at the first of its
    // `waypoints` that exists and then appends the arrival, so a destination naming a far waypoint
    // hands the walker a last hop it cannot plan - and this one is easy to miss because the arrival
    // list beside it can be perfectly correct. brit-home-baker is the case that found it: its
    // arrivals were re-pointed to a waypoint three tiles away while the destination still named one
    // 208 tiles off, and the walk audit failed both of its approaches with no path at all.
    for (const destination of nav.destinations) {
        if (only && destination.id !== only) {
            continue;
        }

        if (tag && !(destination.tags || '').split(/\s+/).includes(tag)) {
            continue;
        }

        const result = repointFor(nav, destination, { ...options, map: destination.map });

        if (result.stranded) {
            stranded.push(`${destination.id} (destination, ${destination.x},${destination.y})`
                + ` - nearest reachable is ${result.nearest || 'nothing'} at `
                + `${result.tiles === Infinity ? '-' : result.tiles} tiles`);
            continue;
        }

        if (result.changed) {
            updates.push({
                id: `dest:${destination.id}`,
                props: { waypoints: result.waypoints },
                was: destination.waypoints,
                now: result.waypoints,
                tiles: result.tiles
            });
        }
    }

    for (const arrival of nav.arrivals) {
        const at = index.get(arrival.destination) || 0;
        index.set(arrival.destination, at + 1);

        const destination = destinations.get(arrival.destination);

        if (!destination) {
            continue;
        }

        if (only && destination.id !== only) {
            continue;
        }

        if (tag && !(destination.tags || '').split(/\s+/).includes(tag)) {
            continue;
        }

        // An arrival carries no facet of its own; its destination's is the one that counts.
        const result = repointFor(nav, arrival, { ...options, map: destination.map });

        if (result.stranded) {
            // Nothing reachable is close enough. Left exactly as it is and reported: this is a
            // road that has to be authored, not a field that can be corrected.
            stranded.push(`${arrival.destination} (${arrival.x},${arrival.y}) - nearest reachable`
                + ` is ${result.nearest || 'nothing'} at `
                + `${result.tiles === Infinity ? '-' : result.tiles} tiles`);
            continue;
        }

        if (!result.changed) {
            continue;
        }

        updates.push({
            id: `arr:${arrival.destination}#${at}`,
            props: { waypoints: result.waypoints },
            was: arrival.waypoints,
            now: result.waypoints,
            tiles: result.tiles
        });
    }

    for (const update of updates) {
        console.log(`  ${update.id}: "${update.was}" -> "${update.now}" (${update.tiles} tiles)`);
    }

    console.log('');
    console.log(`repoint-arrivals: ${updates.length} arrival(s) to re-point,`
        + ` ${stranded.length} with nothing reachable inside the ${HOP_CAP}-tile cap.`);

    for (const line of stranded) {
        console.log(`  STRANDED: ${line}`);
    }

    if (updates.length === 0 || !write) {
        console.log('');
        console.log(write ? 'repoint-arrivals: nothing to do.'
            : 'repoint-arrivals: dry run. Pass --write to apply.');
        return;
    }

    const shapes = await request({ host: '127.0.0.1', port, path: '/api/shapes', method: 'GET' });

    if (shapes.status !== 200) {
        fail(`the bridge answered ${shapes.status} for /api/shapes. Is it running on ${port}?`);
    }

    const baseHash = JSON.parse(shapes.text).files.navigation.hash;
    const payload = JSON.stringify({
        baseHash,
        updates: updates.map((u) => ({ id: u.id, props: u.props })),
        creates: [],
        deletes: []
    });

    const saved = await request({
        host: '127.0.0.1', port, path: '/api/save/navigation', method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) }
    }, payload);

    const result = JSON.parse(saved.text);

    if (saved.status !== 200 || !result.written) {
        fail(`the save failed (${saved.status}): ${result.error || saved.text}`);
    }

    console.log('');
    console.log(`repoint-arrivals: written. reloaded ${result.reloaded} - ${result.message}`);

    for (const warning of result.warnings || []) {
        console.log(`  SHARD WARNING: ${warning}`);
    }

    if (!result.reloaded) {
        fail('the file was written but the shard refused to reload it.');
    }
}

main().catch((error) => fail(error.message));
