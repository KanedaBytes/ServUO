// Waypoint coverage: where the nav graph has holes.
//
// Adapted from uo-offline-server's map editor, with three things changed.
//
// 1. CHEBYSHEV, NOT EUCLIDEAN. Theirs measures sqrt(dx^2+dy^2) while every other distance in
//    their system - and every distance in ours, including NavGraph.Chebyshev and the hop cap
//    itself - is max(|dx|,|dy|). UO movement is eight-directional, so Euclidean overstates by up
//    to sqrt(2) and paints gaps around 40% earlier than the pathfinder actually fails.
//
// 2. THE THRESHOLDS COME FROM THE HOP CAP, not from literals. Theirs were 28 and 38 against a
//    38-tile A* leg limit; ours are the same three-quarters ratio against Custom.NavHopMaxTiles.
//    An overlay whose numbers are not the pathfinder's numbers is decoration.
//
// 3. THE "FINE" BAND IS NOT DRAWN. Nothing is painted where coverage is good, which is what lets
//    this live as a permanent layer instead of a mode you toggle in and out of.
//
// KNOWN LIMITATION, and it matters: this measures distance to the nearest waypoint NODE, not
// reachability through the graph. It cannot see a river, a wall, or a waypoint island with no
// edges out of it - all of those read as covered. Making it truthful needs a walkability raster
// exported from the server's own movement rules, which is the natural follow-up.

import { traceWorldRect } from './iso.js';

const MAX_CELLS = 600000;

let cache = null;

/**
 * Builds the distance grid.
 *
 * The domain is the bounding box of the waypoints, padded, and a cell is measured ONLY against
 * waypoints within that pad of it - further than the pad from every waypoint is ocean or wilderness
 * nobody has walked, which is not a gap in the graph but the edge of it, and is neither drawn nor
 * counted.
 *
 * THAT RULE USED TO BE THE BOUNDING BOX, and moongates broke it. With nine gate waypoints spread
 * from Skara Brae to Moonglow the box became the whole facet, the cell grew to 32 tiles to fit the
 * old 12,000-cell budget, and the overlay went coarser than the hop cap it exists to measure. Now
 * each waypoint stamps its own padded square, so the cost is waypoints x (pad / cell)^2 rather than
 * cells x waypoints, and the grid can stay finer than the cap across a whole facet.
 *
 * `band` lists the cells worth drawing (past three-quarters of the cap), so drawing a facet-sized
 * grid costs what the gaps cost rather than what the facet does.
 */
export function compute(waypoints, hopCap) {
    cache = null;

    if (!waypoints.length) {
        return null;
    }

    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;

    for (const wp of waypoints) {
        if (wp.x < minX) minX = wp.x;
        if (wp.y < minY) minY = wp.y;
        if (wp.x > maxX) maxX = wp.x;
        if (wp.y > maxY) maxY = wp.y;
    }

    // Padded by twice the cap, so the band beyond the edge of the graph is visible rather than
    // clipped off exactly where it starts to matter.
    const pad = hopCap * 2;

    minX -= pad;
    minY -= pad;
    maxX += pad;
    maxY += pad;

    // A quarter of the cap: fine enough that a gap is located rather than merely reported. It grows
    // only to keep the array inside MAX_CELLS, and never past the cap.
    let cell = Math.max(2, Math.floor(hopCap / 4));
    let cols = Math.ceil((maxX - minX) / cell);
    let rows = Math.ceil((maxY - minY) / cell);

    while (cols * rows > MAX_CELLS && cell < hopCap) {
        cell = Math.min(hopCap, Math.ceil(cell * 1.5));
        cols = Math.ceil((maxX - minX) / cell);
        rows = Math.ceil((maxY - minY) / cell);
    }

    const distance = new Float32Array(cols * rows).fill(Infinity);

    // Each waypoint stamps the cells within `pad` (plus a cell, so every centre inside the pad is
    // reached) with its Chebyshev distance, keeping the nearest.
    const reach = pad + cell;

    for (const wp of waypoints) {
        const c0 = Math.max(0, Math.floor((wp.x - reach - minX) / cell));
        const c1 = Math.min(cols - 1, Math.floor((wp.x + reach - minX) / cell));
        const r0 = Math.max(0, Math.floor((wp.y - reach - minY) / cell));
        const r1 = Math.min(rows - 1, Math.floor((wp.y + reach - minY) / cell));

        for (let r = r0; r <= r1; r++) {
            const cy = minY + r * cell + cell / 2;

            for (let c = c0; c <= c1; c++) {
                const cx = minX + c * cell + cell / 2;

                // Chebyshev, matching the engine's own movement cost.
                const d = Math.max(Math.abs(wp.x - cx), Math.abs(wp.y - cy));

                if (d <= pad && d < distance[r * cols + c]) {
                    distance[r * cols + c] = d;
                }
            }
        }
    }

    const fine = hopCap * 0.75;
    const band = [];

    for (let i = 0; i < distance.length; i++) {
        if (distance[i] > fine && distance[i] !== Infinity) {
            band.push(i);
        }
    }

    cache = { minX, minY, cell, cols, rows, distance, band: Int32Array.from(band), hopCap };

    return cache;
}

export function invalidate() {
    cache = null;
}

export function isComputed() {
    return cache !== null;
}

/** Counts the cells worth acting on, for the layer's row in the sidebar. */
export function gapCount() {
    if (!cache) {
        return 0;
    }

    let count = 0;

    for (const i of cache.band) {
        if (cache.distance[i] > cache.hopCap) {
            count++;
        }
    }

    return count;
}

export function draw(ctx, view) {
    if (!cache) {
        return;
    }

    const { minX, minY, cell, cols, distance, band, hopCap } = cache;

    const size = Math.max(1, cell * view.scale);

    // Only the band: below three-quarters of the cap coverage is good and nothing is painted, and
    // beyond the pad is the edge of the graph rather than a hole in it. See compute.
    for (const index of band) {
        const r = Math.floor(index / cols);
        const c = index - r * cols;
        const d = distance[index];

        const [px, py] = view.toScreen(minX + c * cell, minY + r * cell);

        // The cull uses the projected corner in both projections; in art a cell is a diamond
        // whose widest point is `size` from that corner, so the same margin still holds.
        if (px + size < 0 || py + size < 0 || px > ctx.canvas.clientWidth || py > ctx.canvas.clientHeight) {
            continue;
        }

        if (d <= hopCap) {
            // Marginal: reachable, but one edit away from not being.
            ctx.fillStyle = 'rgba(255, 210, 0, 0.28)';
        } else {
            // A real gap, deepening with distance so the worst of it is obvious at a glance.
            const alpha = Math.min(0.6, 0.28 + (d - hopCap) / (hopCap * 6));
            ctx.fillStyle = `rgba(255, 40, 0, ${alpha})`;
        }

        // A coverage cell is a world square, so in art it is a diamond like every other rect.
        if (traceWorldRect(ctx, view, minX + c * cell, minY + r * cell, cell, cell)) {
            ctx.fill();
        } else {
            ctx.fillRect(px, py, size, size);
        }
    }
}

/**
 * Colours an edge by its length against the hop cap.
 *
 * The same numbers as the coverage bands, for the same reason: an edge longer than the cap is one
 * the engine cannot path, which is exactly what [NavAudit reports as FAR.
 */
export function edgeColor(length, hopCap) {
    if (length > hopCap) {
        return 'rgba(255, 60, 60, 0.95)';
    }

    if (length > hopCap - 3) {
        return 'rgba(255, 200, 50, 0.9)';
    }

    return 'rgba(80, 200, 110, 0.75)';
}
