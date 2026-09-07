/**
 * The work-site overlay: which arrival tiles a bot can actually dig from.
 *
 * NOT a shape layer. Mine and lumber destinations, their zones and their arrival points are
 * already records in `nav-destinations`, `nav-zones` and `nav-arrivals`, and emitting a second
 * copy of them under another layer would mean two things to keep in step and two things to click.
 * This draws *over* those shapes instead, the way `coverage.js` draws over the map, and is
 * toggled by a synthetic layer row for the same reason.
 *
 * The numbers are measured by the shard, never here. `BotWorkSites.ReachFrom` runs the identical
 * 5x5 sweep `BotHarvest.FindTarget` performs against real map data, so the count answers the only
 * question worth asking - standing on this tile, will the bot find something to swing at? A
 * browser cannot answer that: it has no tiledata, and the last time this shard guessed at
 * harvestability from outside the engine it authored a mine on grass.
 */

/** destination id -> { type, min, points: [{index, x, y, z, reach}] } */
let sites = new Map();

/** Points the shard answered for that are not in navigation.json yet - the Site tool's live feed. */
let probe = [];

/** Work-site destination types. Anything else is an ordinary destination and is not drawn here. */
export const WORK_TYPES = new Set(['mine', 'lumber']);

export function setReach(data) {
    sites = new Map();
    probe = (data && data.probe) || [];

    for (const row of (data && data.arrivals) || []) {
        let site = sites.get(row.destination);

        if (!site) {
            site = { type: row.type, min: row.min, points: [] };
            sites.set(row.destination, site);
        }

        site.points.push(row);
    }
}

export function isComputed() {
    return sites.size > 0 || probe.length > 0;
}

/** How many arrivals across all sites fall below their type's floor. Shown in the layer row. */
export function thinCount() {
    let thin = 0;

    for (const site of sites.values()) {
        for (const point of site.points) {
            if (point.reach < site.min) {
                thin++;
            }
        }
    }

    return thin;
}

export function siteCount() {
    return sites.size;
}

/**
 * Green at or above the floor, amber below it, red at nothing at all.
 *
 * Three bands rather than a gradient because the decision the author is making is three-way:
 * this tile is fine, this tile is thin, this tile is useless. A gradient would make a reach of 4
 * and a reach of 5 look nearly the same when one is over the line and one is under it.
 */
export function reachColor(reach, min) {
    if (reach <= 0) {
        return '#ff7a6b';
    }

    return reach < min ? '#ffd479' : '#7bd88f';
}

export function draw(ctx, view) {
    if (sites.size === 0 && probe.length === 0) {
        return;
    }

    ctx.save();
    ctx.font = '10px ui-sans-serif, system-ui, sans-serif';
    ctx.textBaseline = 'middle';

    for (const site of sites.values()) {
        for (const point of site.points) {
            mark(ctx, view, point.x, point.y, point.reach, site.min, false);
        }
    }

    // Probe points are drawn hollow so a tile being considered never looks like one that is
    // authored. The Site tool asks about tiles it has not written yet, and the difference between
    // "this is in the file" and "this is what the file would say" is the whole point of asking.
    for (const point of probe) {
        mark(ctx, view, point.x, point.y, point.reach, point.min, true);
    }

    ctx.restore();
}

function mark(ctx, view, x, y, reach, min, hollow) {
    const [sx, sy] = view.toScreen(x + 0.5, y + 0.5);
    const width = ctx.canvas.clientWidth;
    const height = ctx.canvas.clientHeight;

    if (sx < -30 || sy < -30 || sx > width + 30 || sy > height + 30) {
        return;
    }

    const color = reachColor(reach, min);
    const radius = Math.max(3, Math.min(7, view.scale * 0.6));

    ctx.beginPath();
    ctx.arc(sx, sy, radius, 0, Math.PI * 2);

    if (hollow) {
        ctx.strokeStyle = color;
        ctx.lineWidth = 2;
        ctx.setLineDash([3, 2]);
        ctx.stroke();
        ctx.setLineDash([]);
    } else {
        ctx.fillStyle = color;
        ctx.fill();
        ctx.strokeStyle = 'rgba(0, 0, 0, .7)';
        ctx.lineWidth = 1;
        ctx.stroke();
    }

    // The count only when there is room for it to be read. Below this the dots still carry the
    // three-way colour, which is the part that survives being small.
    if (view.scale >= 1.5) {
        ctx.fillStyle = color;
        ctx.fillText(String(reach), sx + radius + 3, sy);
    }
}
