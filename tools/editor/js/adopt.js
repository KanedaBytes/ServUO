/**
 * What an adopt proposal becomes in the editor, and what it says about itself.
 *
 * Pure on purpose: no DOM, no state, no fetch. The rule for WHICH proposed records Save will write
 * is the one rule in the adopt flow that was never tested, because it lived inside app.js beside
 * the banner code and app.js is browser-only. It is the rule that decides whether a road ends at
 * the edge of the map, so it lives here, where adopt.test.js can hold it to account.
 *
 * The shard's proposal (Data/Live/nav-adopt.json) has already walked every edge, sorted every
 * waypoint into reached / unreachable / stranded, and pruned arrivals against the survivors. This
 * module only has to agree with it: write what reaches the graph, drop what does not, and say
 * both counts out loud.
 */

/**
 * Ids Save will not write: stranded (no surviving edge at all) and unreachable (connected to each
 * other, but not to the graph we already have).
 */
export function dropped(proposal) {
    return new Set([...(proposal.stranded || []), ...(proposal.unreachable || [])]);
}

/**
 * The proposal as unsaved editor shapes, minus everything dropped.
 *
 * An edge goes with either dropped end. The join edges are the exception worth noticing: their far
 * end is one of OURS and is not among the proposed waypoints at all, and it passes here because it
 * is not in `dropped` either. Destinations and arrivals are taken as the shard pruned them.
 */
export function survivors(proposal) {
    const gone = dropped(proposal);
    const created = [];

    for (const waypoint of proposal.waypoints || []) {
        if (gone.has(waypoint.id)) {
            continue;
        }

        created.push({
            layer: 'nav', id: `wp:${waypoint.id}`, kind: 'point', map: waypoint.map,
            label: waypoint.name || waypoint.id,
            points: [[waypoint.x, waypoint.y, waypoint.z]],
            props: {
                id: waypoint.id,
                ...(waypoint.name ? { name: waypoint.name } : {}),
                arrivalRange: waypoint.arrivalRange || 0,
                tags: waypoint.tags || '',
                source: waypoint.source,
                // The reference's Z, present only when Adopt wrote the Z the walker stood on.
                ...(Number.isInteger(waypoint.refZ) ? { refZ: waypoint.refZ } : {})
            },
            fields: []
        });
    }

    for (const edge of proposal.edges || []) {
        if (gone.has(edge.from) || gone.has(edge.to)) {
            continue;
        }

        created.push({
            layer: 'nav-edges', id: `edge:${edge.from}>${edge.to}`, kind: 'polyline',
            map: proposal.map, label: '',
            points: [[0, 0, 0], [0, 0, 0]],
            props: {
                from: edge.from, to: edge.to, kind: 'walk',
                tags: edge.tags || '', source: edge.source
            },
            fields: []
        });
    }

    for (const destination of proposal.destinations || []) {
        created.push({
            layer: 'nav-destinations', id: `dest:${destination.id}`, kind: 'point',
            map: destination.map, label: destination.name || destination.id,
            points: [[destination.x, destination.y, destination.z]],
            props: {
                id: destination.id, name: destination.name, type: destination.type,
                tags: destination.tags || '', waypoints: destination.waypoints || '',
                source: destination.source
            },
            fields: []
        });
    }

    let index = 0;

    for (const arrival of proposal.arrivals || []) {
        created.push({
            layer: 'nav-arrivals', id: `arr:${arrival.destination}#${index++}`, kind: 'point',
            map: proposal.map, label: `${arrival.destination} arrival`,
            points: [[arrival.x, arrival.y, arrival.z]],
            props: {
                destination: arrival.destination, exclusive: false,
                waypoints: arrival.waypoints || '', source: arrival.source,
                ...(Number.isInteger(arrival.refZ) ? { refZ: arrival.refZ } : {})
            },
            fields: []
        });
    }

    return created;
}

/**
 * Whether Save must refuse this proposal outright: nothing in it reaches the graph we have.
 *
 * The shard decides (`blocked`), from what its flood reached. Every record in an island is
 * individually valid, which is what makes an island the one fault the editor cannot otherwise
 * show; but a proposal that reaches us in part is written in part, not refused.
 */
export function blocked(proposal) {
    return proposal.blocked === true;
}

/** The banner, one line per fact, with both counts where a proposal was split. */
export function summary(proposal, createdCount) {
    const failures = proposal.failures || [];
    const stranded = proposal.stranded || [];
    const unreachable = proposal.unreachable || [];
    const folded = proposal.folded || [];
    const corrected = proposal.corrected || [];

    const lines = [
        `${createdCount} record(s) proposed from uo-offline.`,
        `${(proposal.edges || []).length} edge(s) walked, pathed by the engine both ways, and`
            + ' subdivided under the hop cap.',
        `${proposal.links || 0} join(s) onto existing waypoints.`
    ];

    if (proposal.skipped) {
        lines.push(`${proposal.skipped.authored} skipped as already authored,`
            + ` ${proposal.skipped.noArrival} destination(s) skipped for having no arrival.`);
    }

    // THE TWO COUNTS. What Save writes and what it drops, on one line, before anything else
    // about the proposal - a road that ends at the edge of the map is the thing to know first.
    if (Number.isInteger(proposal.reachable)) {
        if (unreachable.length > 0) {
            lines.push('', `${proposal.reachable} waypoint(s) reach the existing graph and will be written;`
                + ` ${unreachable.length} cannot and are DROPPED. They stay in the uo-offline reference`
                + ' layer, dashed, to adopt from a box that overlaps this one once it is saved.');
        } else {
            lines.push('', `All ${proposal.reachable} waypoint(s) reach the existing graph.`);
        }
    }

    if (failures.length > 0) {
        lines.push('', `${failures.length} edge(s) could not be walked and are NOT`
            + ' proposed - they are drawn red; hover one for the reason.');
    }

    if (stranded.length > 0) {
        lines.push(`${stranded.length} waypoint(s) had no surviving edge and were dropped.`);
    }

    if (folded.length > 0) {
        // Two reference records on one tile: the second was folded into the first, and every
        // edge, destination and arrival naming it now names the first. Listed as "folded>kept".
        lines.push(`${folded.length} waypoint(s) sharing a tile with another were folded`
            + ` into it: ${folded.join(', ')}`);
    }

    if (corrected.length > 0) {
        // The reference stored a Z nothing stands at (the water under a pier); the record is
        // written at the Z the walker stood on, with theirs kept as refZ.
        lines.push(`${corrected.length} Z value(s) corrected to where the walker stood`
            + ` (reference Z kept as refZ): ${corrected.join('; ')}`);
    }

    const corridors = proposal.corridors || [];

    if (corridors.length > 0) {
        // A destination none of whose arrivals was inside the hop cap of any waypoint got a
        // corridor walked to it from the nearest reachable waypoint. Each is listed with its
        // length and where it starts; past two hops it is flagged so a far one is looked at
        // before Save rather than refused.
        lines.push('', `${corridors.length} corridor(s) walked to destinations whose arrivals were`
            + ' beyond the hop cap of every waypoint:');

        for (const corridor of corridors) {
            const state = corridor.walked === false ? 'FAILED (see the red edge)' : 'walked';
            const flag = corridor.review ? ' - REVIEW: over two hops' : '';
            lines.push(`  ${corridor.destination}: ${corridor.tiles} tile(s) from ${corridor.from}`
                + ` to ${corridor.x},${corridor.y}, ${state}${flag}`);
        }
    }

    for (const island of proposal.islands || []) {
        lines.push('', island);
    }

    lines.push('', blocked(proposal)
        ? 'Nothing is written yet. This proposal cannot be saved: Discard it.'
        : 'Nothing is written yet. Save to accept, or Discard.');

    return lines;
}
