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
 * `sources` is `{ audit, validation, flagged, shapeAt, labelFor }` - all optional, because each
 * one is independently absent in a real session: the audit has not been run, the shard is down,
 * the landz batch has not arrived. A missing source contributes nothing and never throws.
 *
 * The sort is by severity only, and is STABLE, so within a tier the shard's own ordering survives
 * - which matters because the audit emits its findings in graph order and that is the order
 * somebody walking the map would meet them in.
 */
export function problemRows(sources) {
    const { audit, validation, flagged, shapeAt, labelFor } = sources || {};

    const rows = dedupe([]
        .concat(unstandableRows(audit && audit.unstandable, shapeAt))
        .concat(edgeRows(audit && audit.problems, labelFor))
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
