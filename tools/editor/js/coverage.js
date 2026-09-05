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

const MAX_CELLS = 12000;

let cache = null;

/**
 * Builds the distance grid.
 *
 * The domain is the bounding box of the waypoints, padded - deliberately not the whole facet,
 * because ocean nobody has ever walked is not a gap. The cell size grows until the grid fits in
 * MAX_CELLS, which makes the cost independent of how large the covered area gets with no tuning.
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

    // A quarter of the cap: fine enough that a gap is located rather than merely reported.
    let cell = Math.max(2, Math.floor(hopCap / 4));
    let cols = Math.ceil((maxX - minX) / cell);
    let rows = Math.ceil((maxY - minY) / cell);

    while (cols * rows > MAX_CELLS) {
        cell = Math.ceil(cell * 1.5);
        cols = Math.ceil((maxX - minX) / cell);
        rows = Math.ceil((maxY - minY) / cell);
    }

    const distance = new Float32Array(cols * rows);

    for (let r = 0; r < rows; r++) {
        const cy = minY + r * cell + cell / 2;

        for (let c = 0; c < cols; c++) {
            const cx = minX + c * cell + cell / 2;

            let best = Infinity;

            for (const wp of waypoints) {
                // Chebyshev, matching the engine's own movement cost.
                const d = Math.max(Math.abs(wp.x - cx), Math.abs(wp.y - cy));

                if (d < best) {
                    best = d;
                }
            }

            distance[r * cols + c] = best;
        }
    }

    cache = { minX, minY, cell, cols, rows, distance, hopCap };

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

    for (let i = 0; i < cache.distance.length; i++) {
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

    const { minX, minY, cell, cols, rows, distance, hopCap } = cache;

    // Below this, coverage is good and nothing is painted.
    const fine = hopCap * 0.75;

    const size = Math.max(1, cell * view.scale);

    for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
            const d = distance[r * cols + c];

            if (d <= fine) {
                continue;
            }

            const [px, py] = view.toScreen(minX + c * cell, minY + r * cell);

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
