// The uo-offline reference, as editor shapes.
//
// Read-only, and never written back: `whitelist.resolveSave` has no entry for the reference file,
// so the save endpoint cannot name it however the request is spelled. The only way any of this
// reaches Data/Custom/navigation.json is the Adopt step, which re-walks every edge first.
//
// Kept out of project.js because it is a different job: project() round-trips OUR schema and its
// output has to unproject byte-identically. Nothing here round-trips - it is a drawing of somebody
// else's data, and mixing the two would put a writer's constraints on a viewer.

const fs = require('fs');

const whitelist = require('./whitelist.js');

/**
 * Loaded once and kept, keyed on mtime and size.
 *
 * 931 KB and ~4500 records; re-reading and re-parsing it on every pan would make the bbox
 * pointless, which is the same reason `readStockFile` caches trammel.xml. Keyed on the file rather
 * than a timer so a re-import is picked up on the next request without a bridge restart.
 */
let cache = null;

function read() {
    const file = whitelist.FILES.reference;

    let stat;

    try {
        stat = fs.statSync(file);
    } catch {
        // Not an error: a checkout that has never run the importer simply has no reference layer.
        return null;
    }

    const key = `${stat.mtimeMs}:${stat.size}`;

    if (cache && cache.key === key) {
        return cache.doc;
    }

    const doc = JSON.parse(fs.readFileSync(file, 'utf8'));

    cache = { key, doc };

    return doc;
}

/** Chebyshev containment, the same measure the rest of the editor uses for a box. */
function inBox(x, y, bbox) {
    return !bbox
        || (x >= bbox.x && x < bbox.x + bbox.width && y >= bbox.y && y < bbox.y + bbox.height);
}

/**
 * Which region tag a record carries, for the two toggles.
 *
 * A record is overworld unless it says otherwise, so a future tag nobody has taught the editor
 * about shows up rather than disappearing.
 */
function region(record) {
    const tags = String(record.tags || '').split(' ');

    if (tags.includes('dungeon')) {
        return 'dungeon';
    }

    return tags.includes('lostlands') ? 'lostlands' : 'overworld';
}

/**
 * The reference inside a box, as shapes.
 *
 * `regions` is the set the caller wants - the editor sends only what its toggles have on, so the
 * 1988 dungeon waypoints are not serialised on every pan to be dropped in the browser.
 *
 * An edge is included when EITHER end is in the box, so a road does not stop dead at the edge of
 * the view; its far waypoint is included with it, out of box, or the line would have nothing to
 * hang from.
 */
function project(bbox, { regions = ['overworld'], facet = 'Trammel' } = {}) {
    const doc = read();

    if (!doc) {
        return { shapes: [], total: 0, available: false };
    }

    const wanted = new Set(regions);
    const shapes = [];

    const points = new Map();
    const shown = new Set();

    for (const record of doc.waypoints) {
        if (record.map !== facet || !wanted.has(region(record))) {
            continue;
        }

        points.set(record.id, record);

        if (inBox(record.x, record.y, bbox)) {
            shown.add(record.id);
        }
    }

    // Waypoints in view, plus the far end of any edge that leaves it.
    const needed = new Set(shown);

    for (const edge of doc.edges) {
        if (!points.has(edge.from) || !points.has(edge.to)) {
            continue;
        }

        if (shown.has(edge.from) || shown.has(edge.to)) {
            needed.add(edge.from);
            needed.add(edge.to);
        }
    }

    for (const id of needed) {
        const record = points.get(id);

        shapes.push({
            layer: 'reference',
            id: `ref:${record.id}`,
            kind: 'point',
            map: record.map,
            label: record.name || record.id,
            points: [[record.x, record.y, record.z]],
            props: { id: record.id, name: record.name || '', tags: record.tags || '' },
            fields: []
        });
    }

    for (const edge of doc.edges) {
        if (!needed.has(edge.from) || !needed.has(edge.to)) {
            continue;
        }

        if (!shown.has(edge.from) && !shown.has(edge.to)) {
            continue;
        }

        const a = points.get(edge.from);
        const b = points.get(edge.to);

        shapes.push({
            layer: 'reference-edges',
            id: `ref:${edge.from}>${edge.to}`,
            kind: 'polyline',
            map: a.map,
            label: '',
            points: [[a.x, a.y, a.z], [b.x, b.y, b.z]],
            // The length, because more than half of these are longer than our hop cap and that is
            // the single thing an author needs to know before adopting one.
            props: {
                from: edge.from,
                to: edge.to,
                tiles: Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y))
            },
            fields: []
        });
    }

    for (const record of doc.destinations) {
        if (record.map !== facet || !wanted.has(region(record))) {
            continue;
        }

        if (!inBox(record.x, record.y, bbox)) {
            continue;
        }

        shapes.push({
            layer: 'reference-destinations',
            id: `ref:${record.id}`,
            kind: 'point',
            map: record.map,
            label: record.name || record.id,
            points: [[record.x, record.y, record.z]],
            props: {
                id: record.id,
                name: record.name || '',
                type: record.type,
                tags: record.tags || '',
                waypoints: record.waypoints || ''
            },
            fields: []
        });
    }

    return {
        shapes,
        total: doc.waypoints.length,
        available: true
    };
}

/** What the layer rows need before anything is drawn: how much there is, by region. */
function summary() {
    const doc = read();

    if (!doc) {
        return { available: false, overworld: 0, dungeon: 0, lostlands: 0, destinations: 0 };
    }

    const counts = { available: true, overworld: 0, dungeon: 0, lostlands: 0 };

    for (const record of doc.waypoints) {
        counts[region(record)]++;
    }

    counts.destinations = doc.destinations.length;

    return counts;
}

module.exports = { project, summary, region, inBox };
