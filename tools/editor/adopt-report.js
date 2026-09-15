#!/usr/bin/env node
//
// adopt-report.js - what an adopt proposal adopts, per town, as markdown.
//
//     node tools/editor/adopt-report.js            # reads Data/Live/nav-adopt.json
//     node tools/editor/adopt-report.js <proposal.json>
//
// The proposal already says everything in aggregate; this says it WHERE. A whole-facet adopt is one
// proposal of thousands of records, and "212 edges failed" is not something anybody can act on until
// it says which town they are in and why.
//
// WHAT A TOWN IS, because the reference has no such field on a waypoint. A town is a destination
// City slug from uo-offline's data - the converter writes it as the destination's last tag
// (tools/nav-import/uo-offline.js, `tags` = mapped tags, region tag, city slug) - and a record
// belongs to the town of the nearest such destination within TOWN_RADIUS tiles, else to
// `wilderness`. A subdivision belongs where its tile is. An edge belongs to the town of its from
// end; a failed edge to the town of its from coordinate.
//
// Pure except for main(): report() takes the proposal and the reference and returns lines, so
// adopt-report.test.js can hold the attribution rule to a fixture.

'use strict';

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..', '..');
const PROPOSAL = path.join(REPO, 'Data', 'Live', 'nav-adopt.json');
const REFERENCE = path.join(REPO, 'Data', 'Custom', 'reference', 'uo-offline-nav.trammel.json');

const TOWN_RADIUS = 150;
const WILDERNESS = 'wilderness';

/** The town slug a reference destination names, or null for a record with no City. */
function townOf(destination) {
    if (!destination || typeof destination.id !== 'string' || destination.id.startsWith('uo-')) {
        return null;
    }

    const tags = String(destination.tags || '').split(' ').filter(Boolean);

    return tags.length > 0 ? tags[tags.length - 1] : null;
}

/** Every reference destination with a town, as {x, y, town}. */
function anchors(reference) {
    const found = [];

    for (const destination of (reference && reference.destinations) || []) {
        const town = townOf(destination);

        if (town && !String(destination.tags || '').split(' ').includes('dungeon')) {
            found.push({ x: destination.x, y: destination.y, town });
        }
    }

    return found;
}

function nearestTown(points, x, y) {
    let best = WILDERNESS;
    let bestDistance = Infinity;

    for (const point of points) {
        const d = Math.max(Math.abs(point.x - x), Math.abs(point.y - y));

        if (d < bestDistance) {
            bestDistance = d;
            best = point.town;
        }
    }

    return bestDistance <= TOWN_RADIUS ? best : WILDERNESS;
}

/**
 * A failure reason with its coordinates taken out, so the same fault in two places counts as one
 * reason: "flood ok, engine refused: 1,2,3 -> 4,5,6 is not walkable" -> "flood ok, engine refused".
 */
function reasonClass(reason) {
    const text = String(reason || 'no reason');
    const colon = text.indexOf(':');
    const head = colon > 0 ? text.slice(0, colon) : text;

    return head.replace(/-?\d+(,-?\d+)+/g, 'x,y').replace(/\d+/g, 'N').trim();
}

function bucket(towns, town) {
    if (!towns.has(town)) {
        towns.set(town, {
            town, waypoints: 0, destinations: 0, edges: 0, failed: 0, dropped: 0, reasons: new Map()
        });
    }

    return towns.get(town);
}

/**
 * The per-town table and the lists around it, as markdown lines.
 *
 * `written` is what Save writes (js/adopt.js survivors), passed in rather than recomputed so the
 * report and the save cannot disagree about which records made it.
 */
function report(proposal, reference, written) {
    const points = anchors(reference);
    const towns = new Map();

    const location = new Map();

    for (const waypoint of proposal.waypoints || []) {
        location.set(waypoint.id, waypoint);
    }

    const dropped = new Set([...(proposal.stranded || []), ...(proposal.unreachable || [])]);

    for (const shape of written || []) {
        if (shape.layer === 'nav') {
            const [x, y] = shape.points[0];
            bucket(towns, nearestTown(points, x, y)).waypoints++;
        } else if (shape.layer === 'nav-destinations') {
            const [x, y] = shape.points[0];
            bucket(towns, townOf(shape.props) || nearestTown(points, x, y)).destinations++;
        } else if (shape.layer === 'nav-edges') {
            const from = location.get(shape.props.from) || location.get(shape.props.to);
            const town = from ? nearestTown(points, from.x, from.y) : WILDERNESS;
            bucket(towns, town).edges++;
        }
    }

    for (const failure of proposal.failures || []) {
        const entry = bucket(towns, nearestTown(points, failure.fromX, failure.fromY));
        const reason = reasonClass(failure.reason);

        entry.failed++;
        entry.reasons.set(reason, (entry.reasons.get(reason) || 0) + 1);
    }

    for (const id of dropped) {
        const waypoint = location.get(id);

        if (waypoint) {
            bucket(towns, nearestTown(points, waypoint.x, waypoint.y)).dropped++;
        }
    }

    const rows = [...towns.values()].sort((a, b) =>
        (a.town === WILDERNESS) - (b.town === WILDERNESS) || b.waypoints - a.waypoints || a.town.localeCompare(b.town));

    const total = { waypoints: 0, destinations: 0, edges: 0, failed: 0, dropped: 0 };
    const lines = [];

    lines.push('| Town | Waypoints adopted | Destinations adopted | Edges verified | Edges failed | Waypoints dropped | Top failure reasons |');
    lines.push('| --- | ---: | ---: | ---: | ---: | ---: | --- |');

    for (const row of rows) {
        for (const key of Object.keys(total)) total[key] += row[key];

        const top = [...row.reasons.entries()]
            .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
            .slice(0, 3)
            .map(([reason, count]) => `${reason} (${count})`)
            .join('; ');

        lines.push(`| ${row.town} | ${row.waypoints} | ${row.destinations} | ${row.edges} | ${row.failed} | ${row.dropped} | ${top || '-'} |`);
    }

    lines.push(`| **total** | **${total.waypoints}** | **${total.destinations}** | **${total.edges}** | **${total.failed}** | **${total.dropped}** | |`);
    lines.push('');

    lines.push('**Skipped, not failed:**');
    lines.push('');

    for (const box of proposal.skipBoxes || []) {
        lines.push(`- \`${box.name}\` ${box.x},${box.y} ${box.width}x${box.height}: `
            + `${box.waypoints} waypoint(s), ${box.destinations} destination(s)`);
    }

    const skipped = proposal.skipped || {};

    lines.push(`- refused by the existing rules: ${skipped.authored || 0} authored ground, `
        + `${skipped.region || 0} dungeon/Lost Lands, ${skipped.noArrival || 0} with no arrival, `
        + `${skipped.existing || 0} already in navigation.json`);
    lines.push(`- destinations with no arrival a written waypoint reaches: ${(proposal.skippedNoReach || []).length}`);
    lines.push('');

    return { lines, total, rows };
}

async function main() {
    const file = process.argv[2] ? path.resolve(process.argv[2]) : PROPOSAL;
    const proposal = JSON.parse(fs.readFileSync(file, 'utf8'));
    const reference = JSON.parse(fs.readFileSync(REFERENCE, 'utf8'));
    const adopt = await import('./js/adopt.js');

    const { lines } = report(proposal, reference, adopt.survivors(proposal));

    console.log(lines.join('\n'));
}

module.exports = { report, townOf, reasonClass, nearestTown, TOWN_RADIUS };

if (require.main === module) {
    main().catch((error) => {
        console.error(`adopt-report: ${error.message}`);
        process.exit(1);
    });
}
