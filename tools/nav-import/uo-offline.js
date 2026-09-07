// Convert uo-offline's navigation data into our schema, as a reference file.
//
// Run once, by hand, when the pinned uo-offline version changes:
//
//   node tools/nav-import/uo-offline.js
//
// The output is a REFERENCE, not shard data. The shard never loads it; the editor draws it as a
// read-only layer and the Adopt action copies a region of it into Data/Custom/navigation.json
// after re-walking every edge. That is the whole point of keeping it separate: their edges are
// authored against a 38-tile leg cap and 56% of them are longer than the 12 tiles our engine's
// A* window can plan, so nothing here is usable until something walks it.
//
// The field mapping is Scripts/Custom/Core/Navigation/nav-format-comparison.md, written before
// this converter existed and unchanged by it. Where that document says "keep ours", this file is
// where the conversion happens.
//
// GPL-3 data. Source and pinned commit are recorded in Data/Custom/reference/README.md.

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..', '..');

const SOURCE = path.join(
    'C:', 'Users', 'sean.GEKKOSTATE', 'uo-modernuo', 'ModernUO', 'Distribution', 'Data');

const OUT = path.join(REPO, 'Data', 'Custom', 'reference', 'uo-offline-nav.trammel.json');

/**
 * Their flat 30-member enum onto our `type` + `tags`.
 *
 * TOTAL over what the data actually contains, and asserted so by the tests: an unmapped type must
 * fail loudly rather than convert to an empty string, because `NavRecords` rejects a destination
 * with no type at load and the failure would surface a step later as "invalid or missing type"
 * with nothing to say which record.
 *
 * Their enum conflates category, mechanism and resource - the comparison doc's §3 objection - so
 * a vendor becomes `shop` plus the trade as a tag, which is the split our consumers already read.
 */
const TYPES = {
    Bank: { type: 'bank', tags: 'service' },
    Inn: { type: 'inn', tags: 'service' },
    Forge: { type: 'forge', tags: 'craft' },
    Dock: { type: 'dock', tags: 'craft water' },
    Healer: { type: 'healer', tags: 'service' },
    Stables: { type: 'stables', tags: 'service' },
    Shrine: { type: 'shrine', tags: 'landmark' },
    Graveyard: { type: 'graveyard', tags: 'landmark' },

    // A moongate is a place you walk to. The TRANSITION it offers is a kind:"gate" edge, which
    // this converter does not mint: their Target fields describe teleporter linkage we have no
    // verified pair for, and inventing one would author a route nothing has walked.
    Moongate: { type: 'gate', tags: 'landmark travel' },

    VendorProvisioner: { type: 'shop', tags: 'craft provisioner' },
    VendorTailor: { type: 'shop', tags: 'craft tailor' },
    VendorSmith: { type: 'shop', tags: 'craft smith' },
    VendorWeaponer: { type: 'shop', tags: 'craft weaponer' },
    VendorAlchemist: { type: 'shop', tags: 'craft alchemist' },
    VendorCarpenter: { type: 'shop', tags: 'craft carpenter' },
    VendorMage: { type: 'shop', tags: 'craft mage' },
    VendorBowyer: { type: 'shop', tags: 'craft bowyer' },

    // Kept and tagged rather than dropped, so the dungeon step has them ready. Adopt refuses
    // anything tagged `dungeon` until that step exists.
    DungeonRoom: { type: 'room', tags: '' },
    DungeonEntrance: { type: 'entrance', tags: '' },
    DungeonDescend: { type: 'descend', tags: '' },
    DungeonAscend: { type: 'ascend', tags: '' }
};

/**
 * Their resource spots, dropped outright.
 *
 * Our Site tool measures a face against real tiledata with the same 5x5 sweep the harvester runs
 * (`BotWorkSites.ReachFrom`); a coordinate copied from another shard's Felucca has not been
 * measured against anything here. Three records, and authoring them properly is a five-minute job
 * with a tool that already exists.
 */
const DROPPED_TYPES = new Set(['MiningSpot', 'LumberSpot']);

/** Felucca keeps its dungeon maps in the strip east of x 5120. */
const DUNGEON_X = 5120;

/**
 * The cut between the dungeon maps and the Lost Lands within that strip.
 *
 * Measured, not guessed: the strip is dense from y 0 to 2099, then EMPTY from 2100 to 2999, then
 * 43 waypoints at 3000+. Any value inside that empty band gives an identical split, which is what
 * makes this a boundary rather than a threshold somebody has to tune.
 */
const LOSTLANDS_Y = 2300;

/**
 * Every reference id is namespaced, and that is a safety property rather than a naming taste.
 *
 * Their waypoints are called things like `WP 157`, which slugifies to `wp-157` - and `wp-<n>` is
 * exactly what our own corridor tool mints for an unnamed road waypoint. Before this prefix, 23
 * reference ids collided with ids already in navigation.json, among them `wp-1`: ours at
 * 1381,1750 on the west road, theirs a different tile at 1564,1687. Adopting one would have
 * overwritten authored data, which is the one thing Adopt must never do.
 *
 * A prefix makes that impossible by construction instead of by a check somebody has to remember,
 * and it keeps the reference visibly distinct in the editor's own id lists.
 */
const PREFIX = 'uo-';

/** Our ids are `[a-z0-9-]`, so their display names have to be minted into one. */
function slugify(name) {
    const slug = String(name || '')
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '');

    return slug || 'wp';
}

/**
 * A slug nothing else has taken. Their names collide once case and punctuation are gone.
 *
 * Renames are RECORDED, not merely performed. `Britain Bank` is both a waypoint and a destination
 * in their data, so the destination becomes `uo-britain-bank-2` - and Adopt has to know that the
 * id it is copying is not always the slug of the name beside it, or a later re-import that
 * happens to order two colliding records the other way round would quietly repoint an adopted
 * edge at the wrong record.
 */
function uniqueId(base, taken, renamed) {
    if (!taken.has(base)) {
        taken.add(base);
        return base;
    }

    for (let n = 2; ; n++) {
        const candidate = `${base}-${n}`;

        if (!taken.has(candidate)) {
            taken.add(candidate);

            if (renamed) {
                renamed.push(`${base} -> ${candidate}`);
            }

            return candidate;
        }
    }
}

/** Which half of the world a coordinate is in. '' for the overworld. */
function regionTag(x, y) {
    if (x < DUNGEON_X) {
        return '';
    }

    return y < LOSTLANDS_Y ? 'dungeon' : 'lostlands';
}

function tagsFrom(...parts) {
    const seen = [];

    for (const part of parts) {
        for (const token of String(part || '').trim().split(/\s+/)) {
            if (token && !seen.includes(token)) {
                seen.push(token);
            }
        }
    }

    return seen.join(' ');
}

function readJson(file) {
    // Their writer emits a BOM; JSON.parse chokes on it.
    return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
}

/**
 * The conversion. Pure: takes the three parsed source documents, returns the document and a
 * report, touches no files. That is what lets the tests drive it with fixtures.
 */
function convert(sources, { map = 'Trammel' } = {}) {
    const report = { unmapped: [], renamed: [], counts: {} };
    const taken = new Set();

    // --- waypoints ------------------------------------------------------------------------
    const byName = new Map();
    const waypoints = [];

    for (const source of sources.waypoints) {
        const id = uniqueId(PREFIX + slugify(source.Name), taken, report.renamed);
        const tags = regionTag(source.X, source.Y);

        byName.set(source.Name, id);

        const record = {
            id,
            name: source.Name,
            map,
            x: source.X,
            y: source.Y,
            z: source.Z,
            arrivalRange: source.ArrivalRange || 0,
            tags
        };

        if (source._note) {
            record.note = source._note;
        }

        waypoints.push(record);
    }

    // --- edges ----------------------------------------------------------------------------
    //
    // Their Connects is embedded and symmetrised on load; ours is an explicit record per pair, so
    // the fan-out is deduplicated on the unordered pair. No tags: we cannot know which of their
    // roads is a road, and a guessed tag would silently reweight the search through costTags.
    const edges = [];
    const seenEdge = new Set();

    for (const source of sources.waypoints) {
        const from = byName.get(source.Name);

        for (const neighbour of source.Connects || []) {
            const to = byName.get(neighbour);

            if (!to) {
                report.unmapped.push(`edge '${source.Name}' -> '${neighbour}': no such waypoint`);
                continue;
            }

            if (to === from) {
                report.unmapped.push(`edge '${source.Name}' -> itself: dropped`);
                continue;
            }

            const key = from < to ? `${from}|${to}` : `${to}|${from}`;

            if (seenEdge.has(key)) {
                continue;
            }

            seenEdge.add(key);
            edges.push({ from, to, kind: 'walk', tags: '' });
        }
    }

    // --- destinations and their arrivals --------------------------------------------------
    const destinations = [];
    const arrivals = [];
    const zones = [];

    for (const source of sources.destinations) {
        if (DROPPED_TYPES.has(source.Type)) {
            report.unmapped.push(`destination '${source.Name}': ${source.Type} dropped (gather spot)`);
            continue;
        }

        const mapped = TYPES[source.Type];

        if (!mapped) {
            report.unmapped.push(`destination '${source.Name}': unknown type '${source.Type}'`);
            continue;
        }

        const id = uniqueId(PREFIX + slugify(source.Name), taken, report.renamed);

        // A Dungeon field wins over the coordinate: ten of their dungeon records sit at x < 5120
        // because a dungeon ENTRANCE is on the overworld, and the strip rule alone misses all ten.
        const region = source.Dungeon != null ? 'dungeon' : regionTag(source.X, source.Y);

        destinations.push({
            id,
            name: source.Name,
            type: mapped.type,
            map,
            x: source.X,
            y: source.Y,
            z: source.Z,
            tags: tagsFrom(mapped.tags, region, source.City ? slugify(source.City) : ''),
            waypoints: waypointList(source.NearestWaypoint, null, byName)
        });

        for (const arrival of source.Arrivals || []) {
            arrivals.push({
                destination: id,
                x: arrival.X,
                y: arrival.Y,
                z: arrival.Z,
                exclusive: false,
                waypoints: waypointList(source.NearestWaypoint, arrival.Waypoints, byName)
            });
        }

        // Their legacy single-arrival form, which their own Python migrates on write. Only used
        // when the plural form is absent, or the destination would get the same tile twice.
        if (!(source.Arrivals || []).length && source.ArrivalX != null) {
            arrivals.push({
                destination: id,
                x: source.ArrivalX,
                y: source.ArrivalY,
                z: source.ArrivalZ || 0,
                exclusive: false,
                waypoints: waypointList(source.NearestWaypoint, null, byName)
            });
        }

        // A polygon authored on a destination becomes a zone. Their ZoneRegistry already merges
        // these in as synthetic zones at load, and the comparison doc calls the two homes a
        // regret - so here they get one.
        if (Array.isArray(source.Polygon) && source.Polygon.length > 0) {
            if (source.Polygon.length < 3) {
                report.unmapped.push(
                    `destination '${source.Name}': polygon has ${source.Polygon.length} point(s), needs 3`);
            } else {
                zones.push(polyZone(uniqueId(`${id}-area`, taken, report.renamed), source.Polygon, map,
                    tagsFrom(mapped.type, region)));
            }
        }
    }

    // --- zones ----------------------------------------------------------------------------
    for (const source of sources.zones) {
        if (!Array.isArray(source.Points) || source.Points.length < 3) {
            report.unmapped.push(`zone '${source.Name}': fewer than three points`);
            continue;
        }

        zones.push(polyZone(
            uniqueId(PREFIX + slugify(source.Name), taken, report.renamed),
            source.Points,
            map,
            tagsFrom(source.Kind ? slugify(source.Kind) : '', source.Type ? slugify(source.Type) : '')));
    }

    report.counts = {
        waypoints: waypoints.length,
        edges: edges.length,
        destinations: destinations.length,
        arrivals: arrivals.length,
        zones: zones.length,
        dungeon: waypoints.filter((w) => w.tags === 'dungeon').length,
        lostlands: waypoints.filter((w) => w.tags === 'lostlands').length,
        overworld: waypoints.filter((w) => w.tags === '').length
    };

    return {
        doc: { schemaVersion: 1, comment: comment(map), waypoints, edges, destinations, arrivals, zones },
        report
    };
}

/** Their `[[x, y], ...]` into our bounding box plus a space-separated point list. */
function polyZone(id, points, map, tags) {
    const xs = points.map((p) => p[0]);
    const ys = points.map((p) => p[1]);
    const x = Math.min(...xs);
    const y = Math.min(...ys);

    return {
        id,
        map,
        x,
        y,
        width: Math.max(...xs) - x + 1,
        height: Math.max(...ys) - y + 1,
        shape: 'poly',
        points: points.map((p) => `${p[0]},${p[1]}`).join(' '),
        tags
    };
}

/** Their single NearestWaypoint plus an arrival's own list, as our space-separated ids. */
function waypointList(nearest, extra, byName) {
    const ids = [];

    for (const name of [nearest, ...(extra || [])]) {
        const id = name && byName.get(name);

        if (id && !ids.includes(id)) {
            ids.push(id);
        }
    }

    return ids.join(' ');
}

function comment(map) {
    return 'REFERENCE ONLY - the shard never loads this file. Converted from uo-offline'
        + " (Klein187/uo-offline @ fe18a469, GPL-3) by tools/nav-import/uo-offline.js. Their"
        + ' coordinates are Felucca and carry no facet; Felucca and Trammel share terrain, so they'
        + ` are stamped ${map} here, which is where this shard's world is. Edges are UNWALKED:`
        + " 56% of them are longer than Custom.NavHopMaxTiles, because they were authored against a"
        + ' 38-tile leg cap. The Adopt action re-walks and subdivides a region at a time. Records'
        + ' tagged dungeon or lostlands are hidden by default and refused by Adopt.';
}

/**
 * Written the way Data/Custom does it: two-space top level, one RECORD per line.
 *
 * Not JSON.stringify's indentation, which puts one scalar per line and would turn this into
 * 120,000 lines - the exact trade the comparison doc describes their format making.
 */
function serialize(doc) {
    const out = ['{'];

    out.push(`  "schemaVersion": ${doc.schemaVersion},`);
    out.push(`  "comment": ${JSON.stringify(doc.comment)},`);

    const arrays = ['waypoints', 'edges', 'destinations', 'arrivals', 'zones'];

    arrays.forEach((key, index) => {
        const rows = doc[key];

        out.push(`  "${key}": [`);
        rows.forEach((row, i) => {
            out.push(`    ${JSON.stringify(row)}${i === rows.length - 1 ? '' : ','}`);
        });
        out.push(`  ]${index === arrays.length - 1 ? '' : ','}`);
    });

    out.push('}');

    return `${out.join('\n')}\n`;
}

function main() {
    const sources = {
        waypoints: readJson(path.join(SOURCE, 'Waypoints', 'waypoints.json')).Waypoints,
        destinations: readJson(path.join(SOURCE, 'Destinations', 'destinations.json')).Destinations,
        zones: readJson(path.join(SOURCE, 'Zones', 'zones.json')).Zones
    };

    console.log(`read ${sources.waypoints.length} waypoint(s), `
        + `${sources.destinations.length} destination(s), ${sources.zones.length} zone(s)`);

    const { doc, report } = convert(sources);

    fs.mkdirSync(path.dirname(OUT), { recursive: true });
    // LF, like every other file under Data/Custom - see CLAUDE.md on the line-ending trap.
    fs.writeFileSync(OUT, serialize(doc), 'utf8');

    console.log('\nwrote', path.relative(REPO, OUT));

    for (const [key, value] of Object.entries(report.counts)) {
        console.log(`  ${key.padEnd(14)} ${value}`);
    }

    if (report.renamed.length > 0) {
        console.log(`
${report.renamed.length} id(s) renamed to avoid a collision:`);

        for (const line of report.renamed) {
            console.log(`  ${line}`);
        }
    }

    if (report.unmapped.length > 0) {
        console.log(`\n${report.unmapped.length} record(s) not mapped:`);

        const shown = report.unmapped.slice(0, 20);

        for (const line of shown) {
            console.log(`  ${line}`);
        }

        if (report.unmapped.length > shown.length) {
            console.log(`  ... and ${report.unmapped.length - shown.length} more`);
        }
    } else {
        console.log('\nevery record mapped.');
    }
}

module.exports = {
    convert, serialize, slugify, uniqueId, regionTag, polyZone, PREFIX,
    TYPES, DROPPED_TYPES, DUNGEON_X, LOSTLANDS_Y
};

if (require.main === module) {
    main();
}
