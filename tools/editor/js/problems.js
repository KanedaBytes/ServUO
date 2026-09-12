/**
 * Every badged, flagged or complained-about record, as one list you can click through.
 *
 * WHY THIS EXISTS. Four separate systems already know something is wrong with a record, and until
 * now three of them could only say so as a mark on a dot somewhere on a 6144-tile facet:
 *
 *   - `[NavAudit`'s unstandable arrivals   - a `z?` badge on a marker, and a line in a banner
 *                                            capped at twelve that scrolls away
 *   - `[NavAudit`'s blocked/far/occupied   - the same banner, same cap
 *     edges
 *   - the `z?` / `!` badges                - drawn on the marker and nowhere else
 *   - the replicated shard validator       - runs on EVERY edit (app.js validatePreview) and
 *                                            produces `{severity, where, message, shapeId}` for
 *                                            each finding, of which exactly ONE, `fatal[0]`, was
 *                                            ever shown, in the status line
 *
 * So the editor knew about a stranded destination, a pending road and a solid arrival tile, and
 * the only way to find any of them was to already know where to look. That is the fault this list
 * removes, and it is the same fault the Bots panel removed for a bot standing still.
 *
 * A MODULE, NOT A DOM BUILDER, for the reason audit.js says in its own header: the formatting it
 * replaced lived inside a click handler, was wrong for a year, and nothing could have caught it.
 * Everything here is pure - rows in, rows out - and problems.test.js tests it directly.
 *
 * ROW SHAPE, and why each field is in it:
 *
 *   kind      what sort of problem, for grouping and for the leading tag
 *   severity  'fatal' | 'warning' | 'note' - drives the colour, and note is deliberately not a
 *             warning (see validate.js's Report.note: a warning that is expected teaches everybody
 *             to skim, and the moment the list is skimmed the real one underneath is invisible)
 *   label     what to call the record - its id, or the two ends of an edge
 *   x, y      where to jump to, or null when the finding has no single place (an edge has two)
 *   reason    the shard's own words wherever there are any, never re-derived here
 *   shapeId   what to select, when the finding names a record the editor is holding
 */

/** Order the list is grouped in: worst first, and within a kind the shard's own order is kept. */
export const PROBLEM_ORDER = ['fatal', 'warning', 'note'];

const SEVERITY_RANK = { fatal: 0, warning: 1, note: 2 };

/**
 * The unstandable arrivals, in the shard's words.
 *
 * `reason` is NavResampleZ.Stranded.ToString() and carries the blocking static's id and name, the
 * nearest standable tile within two, and - where there is one - a standable height on the same
 * tile. Older snapshots have no `reason`, so the fallback says the one thing every one of them
 * means rather than inventing the detail it does not have.
 */
export function unstandableRows(unstandable, shapeAt) {
    return (unstandable || []).map((record) => ({
        kind: 'unstandable',
        severity: 'warning',
        label: record.destination,
        x: record.x,
        y: record.y,
        reason: record.reason
            || `nothing can stand at ${record.x},${record.y} z ${record.z} (land ${record.landZ})`,
        // The jump target is the tile either way; selecting the record too is a bonus when the
        // editor happens to be holding it.
        shapeId: shapeAt ? shapeAt(record.x, record.y) : null
    }));
}

/**
 * The audit's edge findings.
 *
 * An edge has two ends and therefore no single tile to jump to, so `x`/`y` are null and the row
 * carries the ids instead. `blocked` is the only kind the shard itself fails on, so it is the only
 * one that is fatal here - occupancy is a fact about right now and `far` is a data warning.
 */
export function edgeRows(problems, labelFor) {
    return (problems || []).map((problem) => {
        const kind = String(problem.kind || '').toLowerCase()
            || (problem.blocked ? 'blocked' : 'problem');

        return {
            kind,
            severity: problem.blocked ? 'fatal' : (kind === 'occupied' ? 'note' : 'warning'),
            label: `${problem.from} -> ${problem.to}`,
            x: null,
            y: null,
            reason: problem.detail
                || (labelFor ? `${labelFor(problem)}, ${problem.distance} tiles` : `${problem.distance} tiles`),
            shapeId: null
        };
    });
}

/**
 * What [WalkAudit found when real walkers walked it.
 *
 * A DIFFERENT QUESTION FROM THE ROWS ABOVE, and the reason this list gains a fourth source. Every
 * other row here comes from asking the engine about a tile or a pair of tiles: is it standable, is
 * it pathable, is the record where it says it is. A walk-audit row comes from a mobile that tried,
 * with the recovery ladder running, starting where a walker actually starts. The shard's own
 * README has the case: 655 edges, 0 blocked, 0 over cap, and bots failing walks all day.
 *
 * The jump target is the STOP TILE, not the goal. Where the walk died is where somebody has to go
 * and look; the goal is already on the map as the record itself, and jumping there would show the
 * place that is fine rather than the place that is not.
 *
 * Occupancy is carried in the reason rather than made a separate kind. A mobile on the next-step
 * tile does not make this a different fault - the walk still failed - but it is the difference
 * between a road nobody can use and a road that was busy for ten seconds, so it has to be visible
 * on the row rather than inferred from a count somewhere else.
 */
/**
 * " [bot]" - which probe class walked this row, or nothing at all.
 *
 * CONDITIONAL ON PURPOSE. [WalkAudit walked one class until 12 September 2026 and every recorded
 * sweep in the Navigation README is a one-class number, so a file written before the rebaseline has
 * no probeClass on its rows and must still read exactly as it did. Absent field, absent tag.
 *
 * It matters once two classes walk the same graph: the same edge can fail for the PlayerBot probe
 * and pass for the BaseCreature one - that difference IS the door regression, and it is the whole
 * reason there are two probes - so a Problems row that did not say which probe failed would point
 * at a road when the answer is a class.
 */
function classTag(row) {
    return row && row.probeClass ? ` [${row.probeClass}]` : '';
}

export function walkAuditRows(walkAudit) {
    const rows = (walkAudit && walkAudit.rows) || [];

    return rows.filter((row) => !row.pass).map((row) => {
        const label = (row.kind === 'arrival'
            ? `${row.from} -> ${row.destination} (arrival)`
            : `${row.from} -> ${row.to}`) + classTag(row);

        const blocked = row.occupied
            ? `; next step blocked by ${row.occupied}`
                + (row.occupiedUnshovable ? ' - which a bot may not push' : '')
            : '';

        const rungs = row.rungs ? `; rungs: ${row.rungs}` : '';

        return {
            kind: 'walk',
            severity: 'warning',
            label,
            x: row.stopX,
            y: row.stopY,
            reason:
                `walked and failed: ${row.cause} (${row.endedBy})`
                + `, stopped at ${row.stopX},${row.stopY},${row.stopZ}`
                + ` after ${row.steps} step(s)${blocked}${rungs}`,
            shapeId: null
        };
    });
}

/**
 * Waypoints a bot can reach and then cannot leave.
 *
 * A cliff is the one thing neither instrument could see, because both start every measurement ON
 * the authored tile: the waypoint paths to its neighbour, and a tile inside the waypoint's own
 * arrival tolerance - where a walker is entitled to stop - does not. See NavNeighbourhood.cs.
 *
 * THE JUMP TARGET IS THE STRANDED TILE, not the waypoint, for the same reason a walk row jumps to
 * the stop tile: the waypoint is already on the map and is the half that works. Somebody has to go
 * and look at the pocket.
 *
 * Only [NavAudit full fills this, so an absent `cliffs` array means "not looked at" rather than
 * "none" - which is why the caller must not turn a missing array into a clean bill of health.
 */
export function cliffRows(audit) {
    if (!audit || !audit.cliffsChecked) {
        return [];
    }

    // ONE ROW PER WAYPOINT, not per (waypoint, neighbour) pair, and dedupe is the reason it has to
    // be. It keys a placed row on `kind|x,y`, so three neighbours stranded from the same pocket -
    // which is the usual shape, since a pocket is a pocket in every direction - would have become
    // one row and silently lost the other two. Grouping here says all three on purpose.
    const byWaypoint = new Map();

    for (const cliff of audit.cliffs || []) {
        const found = byWaypoint.get(cliff.waypoint);

        if (found) {
            found.push(cliff);
        } else {
            byWaypoint.set(cliff.waypoint, [cliff]);
        }
    }

    return [...byWaypoint.values()].map((cliffs) => {
        const first = cliffs[0];
        const worst = cliffs.reduce((a, b) => (b.stranded > a.stranded ? b : a), first);
        const names = cliffs.map((cliff) => cliff.neighbour).join(', ');

        return {
            kind: 'cliff',
            severity: 'warning',
            label: `${first.waypoint} (cliff)`,
            x: worst.tileX,
            y: worst.tileY,
            reason:
                `${worst.stranded} of ${worst.standable} standable approach tile(s) within `
                + `${worst.range} cannot path to ${names}, though the waypoint itself can`
                + ` - e.g. ${worst.tileX},${worst.tileY},${worst.tileZ}`
                + ` (${worst.tileDistance} tile(s) off the waypoint)`,
            shapeId: first.waypoint
        };
    });
}

/**
 * What the walk audit said about one record, as a line for the properties panel.
 *
 * Keyed by the record's OWN id, not by an edge's pair, and it takes the worst row that mentions it
 * - an id appears in up to four rows (both directions of an edge, or one arrival per approach) and
 * the useful answer about a place is its worst case, not an average of its cases. A record with a
 * failing row reports that first; otherwise it reports the highest ratio, which is the number the
 * re-base argument is made of.
 *
 * Returns null when the sweep has never run or never touched this record, so the caller adds no
 * row rather than an empty one.
 */
export function walkAuditFor(walkAudit, id) {
    if (!walkAudit || !id) {
        return null;
    }

    const rows = (walkAudit.rows || []).filter(
        (row) => row.from === id || row.to === id || row.destination === id);

    if (rows.length === 0) {
        return null;
    }

    const failed = rows.filter((row) => !row.pass);

    if (failed.length > 0) {
        const worst = failed[0];

        return `FAILED${classTag(worst)} ${worst.cause} (${worst.endedBy})`
            + `, stopped ${worst.stopX},${worst.stopY}`;
    }

    let worst = rows[0];

    for (const row of rows) {
        if (row.ratio > worst.ratio) {
            worst = row;
        }
    }

    return `${worst.steps} step(s) / ${worst.tiles} tile(s) = ${worst.ratio.toFixed(2)}`
        + `, ${worst.seconds.toFixed(1)}s`
        + classTag(worst)
        + (worst.rungTotal > 0 ? ` - after ${worst.rungTotal} rung(s)` : '');
}

/**
 * "84 walked, 3 failed, 2 skipped, worst ratio 2.82" - the header the walk-audit rows carry.
 *
 * Its own line rather than folded into problemSummary, because those counts describe a RUN and the
 * summary describes the list. A sweep that walked fourteen hundred edges and found three faults has
 * said something about the other 1397 too, and a list can only ever show the three.
 */
export function walkAuditSummary(walkAudit) {
    if (!walkAudit || !walkAudit.status || walkAudit.status === 'none') {
        return 'no walk audit yet';
    }

    if (walkAudit.status === 'running') {
        return `walking... ${walkAudit.walked || 0} of ${walkAudit.total || 0}`;
    }

    const rows = walkAudit.rows || [];
    const failed = rows.filter((row) => !row.pass).length;

    let worst = null;

    for (const row of rows) {
        if (row.pass && (worst === null || row.ratio > worst.ratio)) {
            worst = row;
        }
    }

    // PER CLASS WHERE THE SHARD REPORTED IT, because the gate is per class and a sum hides
    // the one thing two probes exist to show. Older files have no `classes` block and read as
    // before.
    const classes = Array.isArray(walkAudit.classes) ? walkAudit.classes : [];

    const perClass = classes.length > 1
        ? ' - ' + classes
            .map((cls) => `${cls.label || cls.key} ${cls.walked || 0}/${cls.failed || 0}`)
            .join(', ')
        : '';

    return `${rows.length} walked, ${failed} failed, ${walkAudit.skipped || 0} skipped`
        + perClass
        + (worst ? `, worst ratio ${worst.ratio.toFixed(2)}` : '')
        + (walkAudit.fragile ? `, ${walkAudit.fragile} passed only after rungs` : '')
        + (walkAudit.contested ? `, ${walkAudit.contested} contested` : '')
        + ` (${Math.round(walkAudit.seconds || 0)}s, ${walkAudit.probes || 0} probes)`;
}

/**
 * Everything the replicated validator found, at all three tiers.
 *
 * Taken whole rather than filtered: `notes` is where a `pending-road` tag lands, and "a gap
 * somebody has already accepted" is exactly the sort of thing that should be visible in a list and
 * invisible in a status bar.
 */
export function validationRows(validation) {
    if (!validation) {
        return [];
    }

    const all = []
        .concat(validation.fatal || [])
        .concat(validation.warnings || [])
        .concat(validation.notes || []);

    return all.map((finding) => ({
        kind: finding.severity === 'note' ? 'pending' : 'validation',
        severity: finding.severity || 'warning',
        label: finding.where || finding.shapeId || 'record',
        x: null,
        y: null,
        reason: finding.message,
        shapeId: finding.shapeId || null
    }));
}

/**
 * The badges the map draws, as rows.
 *
 * `flagged` is a list of `{shape, unstandable, staleZ}` decided by the caller, because the two
 * predicates live in shapes.js against module-level state that only the browser has. This module
 * stays pure and the test can feed it whatever it likes.
 *
 * A record that is BOTH badged and in the audit's unstandable list is not duplicated: dedupe()
 * drops it, and the audit's row wins because it carries the blocker's name.
 */
export function badgeRows(flagged) {
    const rows = [];

    for (const entry of flagged || []) {
        const shape = entry && entry.shape;
        const point = shape && shape.points && shape.points[0];

        if (!point) {
            continue;
        }

        if (entry.unstandable) {
            rows.push({
                kind: 'unstandable',
                severity: 'warning',
                label: shape.id,
                x: point[0],
                y: point[1],
                reason: 'badged unstandable - nothing fits on this tile at any height',
                shapeId: shape.id
            });
        }

        if (entry.staleZ) {
            rows.push({
                kind: 'stale-z',
                severity: 'warning',
                label: shape.id,
                x: point[0],
                y: point[1],
                reason: `stored z ${point[2] || 0} is more than a storey from the ground here`,
                shapeId: shape.id
            });
        }
    }

    return rows;
}

/**
 * One row per problem, worst first.
 *
 * Two sources genuinely report the same thing: an arrival in the audit's `unstandable` array is
 * also what puts the `z?` badge on its marker. They do NOT agree on what to call it - the audit
 * knows the destination's id, the badge knows the record's - so the key for a row that has a place
 * is its KIND AND ITS TILE, and the label is deliberately not part of it. The first of a duplicate
 * pair wins, and problemRows calls the audit first on purpose: its row is the one carrying the
 * blocking static's name and the nearest standable tile.
 *
 * A row with no place - an edge has two ends, a validator finding names a record rather than a
 * tile - keys on its own text instead. Keying those by kind alone would collapse every blocked
 * edge into one, and keying them by label alone would drop the second of two findings about the
 * same record, which is a real shape: a destination can be both stranded and pending-road.
 */
export function dedupe(rows) {
    const seen = new Set();
    const out = [];

    for (const row of rows) {
        const placed = row.x !== null && row.x !== undefined;
        const key = placed
            ? `${row.kind}|${row.x},${row.y}`
            : `${row.kind}|${row.label}|${row.reason}`;

        if (seen.has(key)) {
            continue;
        }

        seen.add(key);
        out.push(row);
    }

    return out;
}

/**
 * The whole list, from every source.
 *
 * `sources` is `{ audit, walkAudit, validation, flagged, shapeAt, labelFor }` - all optional,
 * because each one is independently absent in a real session: the audit has not been run, the walk
 * sweep has never been run at all, the shard is down, the landz batch has not arrived. A missing
 * source contributes nothing and never throws.
 *
 * The sort is by severity only, and is STABLE, so within a tier the shard's own ordering survives
 * - which matters because the audit emits its findings in graph order and that is the order
 * somebody walking the map would meet them in.
 */
export function problemRows(sources) {
    const { audit, walkAudit, validation, flagged, shapeAt, labelFor } = sources || {};

    // The walk rows go after the audit's and before the badges, which is the order of how much
    // each one knows: the audit carries the blocking static's name, a walk row carries what a
    // mobile actually did, and a badge carries neither. dedupe keeps the first of a pair.
    const rows = dedupe([]
        .concat(unstandableRows(audit && audit.unstandable, shapeAt))
        .concat(edgeRows(audit && audit.problems, labelFor))
        .concat(cliffRows(audit))
        .concat(walkAuditRows(walkAudit))
        .concat(badgeRows(flagged))
        .concat(validationRows(validation)));

    return rows
        .map((row, index) => ({ row, index }))
        .sort((a, b) => {
            const bySeverity = (SEVERITY_RANK[a.row.severity] ?? 9) - (SEVERITY_RANK[b.row.severity] ?? 9);

            return bySeverity !== 0 ? bySeverity : a.index - b.index;
        })
        .map((entry) => entry.row);
}

/** "3 blocked, 20 unstandable, 1 pending" - the summary the section header carries. */
export function problemSummary(rows) {
    if (!rows || rows.length === 0) {
        return 'nothing flagged';
    }

    const counts = new Map();

    for (const row of rows) {
        counts.set(row.kind, (counts.get(row.kind) || 0) + 1);
    }

    return [...counts.entries()].map(([kind, count]) => `${count} ${kind}`).join(', ');
}
