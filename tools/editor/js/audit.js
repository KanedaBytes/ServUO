/**
 * What [NavAudit found, in words.
 *
 * A module rather than a line inside the click handler because the line was wrong for a year and
 * nothing could have caught it: it read `problem.blocked`, a boolean, and printed "over cap" for
 * everything that was not blocked. NavAudit has emitted THREE kinds since the mobile-aware second
 * pass landed, and the third - a walkable edge with somebody standing on it - was being reported
 * as a hop over the length cap. An eight-tile edge against a twelve-tile cap, labelled over cap.
 *
 * `far` is the real over-cap kind, and NavAudit.cs:259 calls it that itself, so this is a
 * three-way branch and not a rename.
 *
 * The kinds are NavAuditKind in Scripts/Custom/Core/Navigation/NavAudit.cs, lowercased onto the
 * wire by its snapshot writer. audit.test.js reads that enum back out of the C# and fails if a
 * kind is added there without a label here.
 */

export const AUDIT_LABELS = {
    // The engine could not path it at all. The edge is wrong.
    blocked: 'BLOCKED',

    // Walkable, but longer than the hop cap, so the walker cannot plan it.
    far: 'over cap',

    // Geometry is fine; something is standing on it. A warning, never a block.
    occupied: 'occupied'
};

/** The label for one finding. An unknown kind is named rather than guessed at. */
export function auditLabel(problem) {
    if (!problem) {
        return '?';
    }

    const kind = String(problem.kind || '').toLowerCase();

    if (AUDIT_LABELS[kind]) {
        return AUDIT_LABELS[kind];
    }

    // Older snapshots pre-date the kind field. blocked is the only thing they could distinguish,
    // and saying so is better than inventing a kind for them.
    return problem.blocked ? AUDIT_LABELS.blocked : (kind || 'problem');
}

/**
 * One line for the banner.
 *
 * The distance is what matters for an over-cap hop and is near-noise for an occupied one, where
 * the useful half is `detail` - which names the mobile and the tile it is standing on, and which
 * the old line threw away entirely.
 */
export function auditLine(problem) {
    const head = `• ${auditLabel(problem)} ${problem.from} -> ${problem.to}`;

    if (String(problem.kind || '').toLowerCase() === 'occupied') {
        return problem.detail ? `${head} - ${problem.detail}` : head;
    }

    return `${head} (${problem.distance} tiles)`;
}

/** Whether any finding is one the shard would actually fail the audit on. */
export function hasBlocked(problems) {
    return (problems || []).some((problem) => problem.blocked);
}
