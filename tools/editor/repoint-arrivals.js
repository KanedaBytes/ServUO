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
//
// `--tag` restricts it to destinations carrying that tag, because a town is the unit somebody
// re-bases; without one it would re-point the whole facet, and Trinsic is deliberately untouched.

'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');

const { REPO_ROOT, FILES } = require('./whitelist.js');

const HOP_CAP = 12;

function fail(message) {
    console.error(`repoint-arrivals: ${message}`);
    process.exit(1);
}

/** Tile distance, which is what the hop cap is measured in. */
function tiles(a, b) {
    return Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y));
}

/**
 * Every waypoint the home waypoint can reach along walk edges.
 *
 * FLOODED, NOT ASSUMED. A nearer waypoint on a component nothing reaches is worse than the far one
 * an arrival already names: the last hop gets shorter and the route stops existing.
 */
function reachable(nav, home) {
    const links = new Map();

    for (const edge of nav.edges) {
        if (!links.has(edge.from)) links.set(edge.from, []);
        if (!links.has(edge.to)) links.set(edge.to, []);

        links.get(edge.from).push(edge.to);
        links.get(edge.to).push(edge.from);
    }

    const seen = new Set([home]);
    const queue = [home];

    while (queue.length > 0) {
        for (const next of links.get(queue.pop()) || []) {
            if (!seen.has(next)) {
                seen.add(next);
                queue.push(next);
            }
        }
    }

    return seen;
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
    const tagAt = args.indexOf('--tag');
    const tag = tagAt >= 0 ? args[tagAt + 1] : null;
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

    const canReach = reachable(nav, home);

    console.log(`repoint-arrivals: ${nav.waypoints.length} waypoint(s),`
        + ` ${canReach.size} reachable from ${home}.`);

    // Arrivals are addressed as the nth of their destination, exactly as the editor addresses
    // them (project.js `arr:<dest>#<n>`), so the index has to be counted the same way.
    const index = new Map();
    const updates = [];
    const stranded = [];

    /** The nearest reachable waypoint to a record, and how far. */
    function closest(record, map) {
        let nearest = null;
        let best = Infinity;

        for (const waypoint of nav.waypoints) {
            if (!canReach.has(waypoint.id) || waypoint.map !== map) {
                continue;
            }

            const distance = tiles(waypoint, record);

            if (distance < best) {
                best = distance;
                nearest = waypoint.id;
            }
        }

        return { nearest, best };
    }

    // A DESTINATION HAS THE SAME FIELD AND THE SAME FAULT. A route ends at the first of its
    // `waypoints` that exists and then appends the arrival, so a destination naming a far waypoint
    // hands the walker a last hop it cannot plan - and this one is easy to miss because the arrival
    // list beside it can be perfectly correct. brit-home-baker is the case that found it: its
    // arrivals were re-pointed to a waypoint three tiles away while the destination still named one
    // 208 tiles off, and the walk audit failed both of its approaches with no path at all.
    for (const destination of nav.destinations) {
        if (tag && !(destination.tags || '').split(/\s+/).includes(tag)) {
            continue;
        }

        const listed = (destination.waypoints || '').split(/\s+/).filter(Boolean);
        const { nearest, best } = closest(destination, destination.map);

        if (nearest === null || best > HOP_CAP) {
            stranded.push(`${destination.id} (destination, ${destination.x},${destination.y})`
                + ` - nearest reachable is ${nearest || 'nothing'} at ${best === Infinity ? '-' : best} tiles`);
            continue;
        }

        const kept = listed.filter((id) => {
            const waypoint = byId.get(id);

            return id !== nearest && waypoint && canReach.has(id) && tiles(waypoint, destination) <= HOP_CAP;
        });

        const next = [nearest, ...kept].join(' ');

        if (next !== destination.waypoints) {
            updates.push({
                id: `dest:${destination.id}`,
                props: { waypoints: next },
                was: destination.waypoints,
                now: next,
                tiles: best
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

        if (tag && !(destination.tags || '').split(/\s+/).includes(tag)) {
            continue;
        }

        const listed = (arrival.waypoints || '').split(/\s+/).filter(Boolean);
        const { nearest, best } = closest(arrival, destination.map);

        if (nearest === null || best > HOP_CAP) {
            // Nothing reachable is close enough. Left exactly as it is and reported: this is a
            // road that has to be authored, not a field that can be corrected.
            stranded.push(`${arrival.destination} (${arrival.x},${arrival.y}) - nearest reachable`
                + ` is ${nearest || 'nothing'} at ${best === Infinity ? '-' : best} tiles`);
            continue;
        }

        // The nearest first, then every listed one that is still inside the cap. A shop fronting
        // two streets keeps both approaches; one whose waypoint has moved away loses only that.
        const kept = listed.filter((id) => {
            const waypoint = byId.get(id);

            return id !== nearest && waypoint && canReach.has(id) && tiles(waypoint, arrival) <= HOP_CAP;
        });

        const next = [nearest, ...kept].join(' ');

        if (next === arrival.waypoints) {
            continue;
        }

        updates.push({
            id: `arr:${arrival.destination}#${at}`,
            props: { waypoints: next },
            was: arrival.waypoints,
            now: next,
            tiles: best
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
