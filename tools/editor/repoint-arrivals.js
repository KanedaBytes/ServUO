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
// It keeps every listed waypoint that still SERVES, and puts the nearest serving one first.
// Keeping the others matters: a shop fronting two streets is approached from both, and the route
// picks whichever its search reaches first.
//
// WHAT "SERVES" MEANS, AND WHY IT CHANGED. An approach used to qualify on straight-line distance
// to the record's own tile. For a destination that tile is its CENTRE, and brit-inn-central's
// centre sits inside a sealed building: 7 tiles from `uo-central-britain-road-2`, inside the cap,
// so the tool kept it - while the route that waypoint really hands the walker, to the ARRIVAL, is
// 15 tiles and plans one way only. Distance to a centre is not routability to the arrivals.
//
// So a waypoint now qualifies only if it is inside the cap of EVERY arrival of the destination and
// the engine plans a route to each of them BOTH WAYS, on the bot class at the stock budget.
// Distance only orders the survivors.
//
// THIS NEEDS A RUNNING SHARD, and that is new. Only the engine can answer the question, so the
// pass asks it through the `nav-hop-probe` token before it decides anything. With no shard the
// rule falls back to the old distance ordering and every affected record is reported UNVERIFIED -
// the dry run still prints, and --write refuses. The editor keeps working the same way, which is
// the promise in js/repoint.js's header.
//
// USAGE
//
//     node tools/editor/repoint-arrivals.js --tag britain             # dry run
//     node tools/editor/repoint-arrivals.js --tag britain --write
//     node tools/editor/repoint-arrivals.js --dest brit-home-jeweler --write
//
// The bridge and the shard both have to be up for --write, and GG_BRIDGE_SECRET set, exactly as
// the save already needed.
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

// Not in whitelist.FILES on purpose: nothing serves it over HTTP, because only a node CLI reads
// it and a CLI reads navigation.json off disk already. Adding a route would widen the bridge's
// surface for a file the browser has no use for.
const HOP_PROBE = path.join(REPO_ROOT, 'Data', 'Live', 'nav-hop-probe.json');

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

/** A tile as the probe names it, and as the answer map is keyed. */
function tileKey(record) {
    return `${record.x},${record.y},${record.z === undefined ? 0 : record.z}`;
}

/**
 * Drop a request token through the bridge and wait for the shard's ack.
 *
 * The bridge deletes the previous ack before publishing the token (bridge.js:1052-1056), so an ack
 * that appears after this returns is this request's - `nav-hop-probe` is not in the NONCED set and
 * does not need to be for that reason. A 409 means a token of that name is still on disk, which is
 * the poller's per-operation busy answer rather than a failure to retry blindly.
 */
async function ask(port, name, body, timeoutMs = 60000) {
    const dropped = await request({
        host: '127.0.0.1', port, path: `/api/request/${name}`, method: 'POST',
        headers: Object.assign(
            { 'Content-Type': 'text/plain', 'Content-Length': Buffer.byteLength(body) },
            authHeaders())
    }, body);

    // 202, not 200: the bridge accepts a token and the shard answers later (bridge.js:1131). A
    // 409 is its per-operation busy reply - a token of that name is still on disk unread.
    if (dropped.status !== 202 && dropped.status !== 200) {
        fail(`the bridge answered ${dropped.status} dropping '${name}': ${dropped.text}`);
    }

    const deadline = Date.now() + timeoutMs;

    while (Date.now() < deadline) {
        const seen = await request({
            host: '127.0.0.1', port, path: `/api/ack/${name}`, method: 'GET'
        });

        if (seen.status === 200) {
            const ack = JSON.parse(seen.text);

            if (!ack.pending && ack.utc) {
                return ack;
            }
        }

        await new Promise((resolve) => setTimeout(resolve, 250));
    }

    fail(`the shard did not answer '${name}' within ${timeoutMs}ms. Is it running?`);
}

/**
 * Ask the engine which of these hops a BOT can actually walk, and answer from what it said.
 *
 * WHY THE BOT CLASS AND WHY THIS TOKEN. The fleet is what walks an arrival, and a bot is not a
 * BaseCreature to the pathfinder: `IgnoreMovableImpassables` is granted to the creature branch and
 * withheld from the bot one (FastAStarAlgorithm.cs:97-107), so a creature walks through the crates
 * a bot goes round - 297 expansions against 301 on the provisioner hop. `nav-hop` cannot be used
 * for this at all: it goes through MovementPath, which fails any goal within one tile
 * (MovementPath.cs:34-35), so the best approach a record could have would read as no route.
 *
 * Budget is left unsaid, which is the stock 300 - the number the live fleet actually plans at.
 *
 * Pairs are CHUNKED because a token body is capped at 4096 bytes (RequestPoller.MaxTokenBytes) and
 * a pair costs about two dozen. Unasked pairs stay absent from the map, and absent reads as `null`
 * - undecided, not refused, which is the distinction repointFor turns into `verified: false`.
 */
async function askRoutes(pairs, port) {
    const answers = new Map();
    const chunk = 140;

    for (let from = 0; from < pairs.length; from += chunk) {
        const slice = pairs.slice(from, from + chunk);
        const body = `${slice.map(([a, b]) => `${tileKey(a)} ${tileKey(b)}`).join(' ')} bot`;

        const ack = await ask(port, 'nav-hop-probe', body);

        if (!ack.ok) {
            fail(`the shard refused the hop probe: ${ack.message}`);
        }

        // `hops` is the per-pair array; `rows` beside it is only its length.
        const hops = JSON.parse(fs.readFileSync(HOP_PROBE, 'utf8')).hops || [];

        for (const hop of hops) {
            if (hop.probeClass !== 'bot') {
                continue;
            }

            // `stockOk` is the stock pathfinder's own verdict, which is the one the fleet gets.
            answers.set(`${hop.startX},${hop.startY},${hop.startZ}>${hop.x},${hop.y},${hop.z}`,
                hop.stockOk === true);
        }
    }

    return (a, b) => {
        const seen = answers.get(`${tileKey(a)}>${tileKey(b)}`);

        return seen === undefined ? null : seen;
    };
}

async function main() {
    repoint = await import('./js/repoint.js');

    const { HOP_CAP, reachableFrom, repointFor, targetsFor, withinCap } = repoint;
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

    const inScope = (destination) => {
        if (only && destination.id !== only) {
            return false;
        }

        return !tag || (destination.tags || '').split(/\s+/).includes(tag);
    };

    // PASS ONE: WHICH HOPS ARE WORTH ASKING ABOUT. The gate is an engine route to every arrival,
    // both ways, and the engine is the only thing that can answer - so the pairs have to be
    // gathered before any of them is decided. Bounded by the cap first: over 1003 waypoints an
    // unbounded ask would be tens of thousands of plans for a handful of answers.
    const scoped = [];

    for (const destination of nav.destinations) {
        if (!inScope(destination)) {
            continue;
        }

        const targets = targetsFor(nav, destination);

        scoped.push({ record: destination, targets, map: destination.map });

        for (const arrival of nav.arrivals) {
            if (arrival.destination === destination.id) {
                scoped.push({ record: arrival, targets: [arrival], map: destination.map });
            }
        }
    }

    const pairs = [];
    const asked = new Set();

    for (const entry of scoped) {
        for (const waypoint of nav.waypoints) {
            if (waypoint.map !== entry.map || !canReach.has(waypoint.id)) {
                continue;
            }

            if (!withinCap(waypoint, entry.targets, HOP_CAP)) {
                continue;
            }

            for (const target of entry.targets) {
                for (const [a, b] of [[waypoint, target], [target, waypoint]]) {
                    const key = `${tileKey(a)}>${tileKey(b)}`;

                    if (!asked.has(key)) {
                        asked.add(key);
                        pairs.push([a, b]);
                    }
                }
            }
        }
    }

    console.log(`repoint-arrivals: ${scoped.length} record(s) in scope,`
        + ` ${pairs.length} hop(s) to ask the shard about (bot class, stock budget).`);

    const routes = pairs.length > 0 ? await askRoutes(pairs, port) : null;

    // Arrivals are addressed as the nth of their destination, exactly as the editor addresses
    // them (project.js `arr:<dest>#<n>`), so the index has to be counted the same way.
    const index = new Map();
    const updates = [];
    const stranded = [];
    const unverified = [];
    const options = { canReach, byId, cap: HOP_CAP, routes };

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

        const result = repointFor(nav, destination,
            { ...options, map: destination.map, targets: targetsFor(nav, destination) });

        if (!result.verified) {
            unverified.push(`dest:${destination.id}`);
        }

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
        const result = repointFor(nav, arrival,
            { ...options, map: destination.map, targets: [arrival] });

        if (!result.verified) {
            unverified.push(`arr:${arrival.destination}#${at}`);
        }

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

    // UNVERIFIED IS NOT REFUSED, AND IT IS NOT APPROVED EITHER. The rule falls back to the old
    // distance ordering when the engine would not answer, which is what keeps the editor usable
    // with the shard down - but the old ordering is the thing that kept brit-inn-central's
    // waypoint, so a file must not be written on it. The dry run still prints, because seeing the
    // proposal is exactly how somebody decides to go and start the shard.
    if (unverified.length > 0) {
        console.log('');
        console.log(`repoint-arrivals: ${unverified.length} record(s) UNVERIFIED - the engine did`
            + ' not answer for every candidate, so these fell back to straight-line distance:');

        for (const id of unverified.slice(0, 10)) {
            console.log(`  UNVERIFIED: ${id}`);
        }

        if (write) {
            fail('refusing to --write on an unverified pass. Start the shard and the bridge, or'
                + ' re-run without --write to see the proposal.');
        }
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
        headers: Object.assign(
            { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) },
            authHeaders())
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
