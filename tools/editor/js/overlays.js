// Read-only stock content drawn underneath the editable shapes.
//
// This is context, not content: upstream spawn points, where teleporters lead, and the region
// boundaries a new zone will sit inside. It is deliberately quiet - thin lines, small marks, no
// labels unless zoomed in - because the whole job of these layers is to be looked past.

export const OVERLAY_LAYERS = {
    stockSpawners: { label: 'Upstream spawners', color: '#8a7f6d' },
    teleporters: { label: 'Teleporters', color: '#59b8a8' },
    moongates: { label: 'Moongates', color: '#b07fd0' },
    regions: { label: 'Stock regions', color: '#6b7686' }
};

const EMPTY = { stockSpawners: [], teleporters: [], moongates: [], regions: [] };

export function emptyOverlays() {
    return EMPTY;
}

export async function fetchOverlays(api, mapName) {
    try {
        return await api.overlays(mapName);
    } catch {
        // Context is optional. Losing it should not stop the editor working.
        return EMPTY;
    }
}

export function counts(overlays) {
    return {
        stockSpawners: overlays.stockSpawners.length,
        teleporters: overlays.teleporters.length,
        moongates: overlays.moongates.length,
        regions: overlays.regions.length
    };
}

export function draw(ctx, view, overlays, visible) {
    ctx.save();

    if (visible.has('regions')) {
        drawRegions(ctx, view, overlays.regions);
    }

    if (visible.has('stockSpawners')) {
        drawMarks(ctx, view, overlays.stockSpawners, OVERLAY_LAYERS.stockSpawners.color, 'dot');
    }

    if (visible.has('teleporters')) {
        drawMarks(ctx, view, overlays.teleporters, OVERLAY_LAYERS.teleporters.color, 'cross');
    }

    if (visible.has('moongates')) {
        drawMarks(ctx, view, overlays.moongates, OVERLAY_LAYERS.moongates.color, 'ring');
    }

    ctx.restore();
}

function drawRegions(ctx, view, regions) {
    ctx.strokeStyle = OVERLAY_LAYERS.regions.color;
    ctx.lineWidth = 1;
    ctx.setLineDash([3, 3]);

    for (const region of regions) {
        for (const [x, y, w, h] of region.rects) {
            const [sx, sy] = view.toScreen(x, y);
            const sw = w * view.scale;
            const sh = h * view.scale;

            // Skip anything off screen; a facet can carry hundreds of rectangles.
            if (sx > view.canvas.clientWidth || sy > view.canvas.clientHeight || sx + sw < 0 || sy + sh < 0) {
                continue;
            }

            ctx.strokeRect(sx, sy, sw, sh);
        }
    }

    ctx.setLineDash([]);

    // Names only once they are readable, and only once per region rather than per rectangle.
    if (view.scale < 0.6) {
        return;
    }

    ctx.font = '10px ui-sans-serif, system-ui, sans-serif';
    ctx.fillStyle = 'rgba(180, 195, 215, .75)';

    for (const region of regions) {
        const [x, y] = region.rects[0];
        const [sx, sy] = view.toScreen(x + 1, y + 1);

        if (sx < -100 || sy < -20 || sx > view.canvas.clientWidth || sy > view.canvas.clientHeight) {
            continue;
        }

        ctx.fillText(region.name, sx, sy + 10);
    }
}

function drawMarks(ctx, view, marks, color, shape) {
    const width = view.canvas.clientWidth;
    const height = view.canvas.clientHeight;

    ctx.strokeStyle = color;
    ctx.fillStyle = color;
    ctx.lineWidth = 1;

    const size = shape === 'dot' ? 1.6 : 3;

    for (const [x, y] of marks) {
        const [sx, sy] = view.toScreen(x + 0.5, y + 0.5);

        if (sx < -8 || sy < -8 || sx > width + 8 || sy > height + 8) {
            continue;
        }

        if (shape === 'dot') {
            ctx.fillRect(sx - size, sy - size, size * 2, size * 2);
            continue;
        }

        if (shape === 'cross') {
            ctx.beginPath();
            ctx.moveTo(sx - size, sy - size);
            ctx.lineTo(sx + size, sy + size);
            ctx.moveTo(sx + size, sy - size);
            ctx.lineTo(sx - size, sy + size);
            ctx.stroke();
            continue;
        }

        ctx.beginPath();
        ctx.arc(sx, sy, size, 0, Math.PI * 2);
        ctx.stroke();
    }
}
