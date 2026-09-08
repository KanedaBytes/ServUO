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

/**
 * Tiles the shard proposed for a zone being authored, and the ones the author has taken.
 *
 * The Site tool used to work the other way round: the author clicked a tile, the shard answered
 * with the reach under it, and the answer was drawn whether or not anything could stand there.
 * Now the shard sweeps the whole zone and offers back the best it found, so a candidate is by
 * construction standable and at or above its type's floor - which is why nothing here filters
 * again. `taken` is a view of the tool's arrival list, not a second copy of it: the tool owns the
 * selection, this module only draws it.
 */
let probe = [];
let taken = new Set();

/** The key a candidate is identified by. Two clicks on one tile must not make two arrivals. */
export function tileKey(x, y) {
    return `${x},${y}`;
}

/** Work-site destination types. Anything else is an ordinary destination and is not drawn here. */
export const WORK_TYPES = new Set(['mine', 'lumber']);

export function setReach(data) {
    sites = new Map();

    // Deduped on the way in. The shard answers for what it was asked about, and the old tool
    // asked about its whole cumulative click list on every click - which is how one session
    // produced a 33-point token describing 15 distinct tiles, each drawn on top of itself.
    const seen = new Set();

    probe = [];

    for (const row of (data && data.probe) || []) {
        const key = tileKey(row.x, row.y);

        if (!seen.has(key)) {
            seen.add(key);
            probe.push(row);
        }
    }

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

/** The proposed tiles, best first - the shard already ranked them. */
export function candidates() {
    return probe;
}

/**
 * The best few, for the tool to take by default.
 *
 * Four because that is what the authored sites carry: brit-mine-north has five arrivals and
 * brit-lumber-south three, and a face wants more than one place to stand so two bots do not
 * queue for the same tile. The point of a default is that "Create, box, Enter, Save" is a
 * complete authoring session for the ordinary case.
 */
export function bestCandidates(count = 4) {
    return probe.slice(0, count).map((row) => [row.x, row.y]);
}

/** Which proposed tile is under this world point, if any. */
export function candidateAt(worldX, worldY) {
    const x = Math.floor(worldX);
    const y = Math.floor(worldY);

    return probe.find((row) => row.x === x && row.y === y) || null;
}

/** The tool tells us what it has taken, so a taken tile can be drawn as taken. */
export function setTaken(arrivals) {
    taken = new Set((arrivals || []).map(([x, y]) => tileKey(x, y)));
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
export function reachColor(reach, min, canFit = true) {
    // A tile nothing can stand on is red however much ore surrounds it. That is not a nicety:
    // the tiles up on the cliff face have the HIGHEST reach on the map - 19, 21, 25 against a
    // floor of 5 - because they are buried in rock, and drawing them green recommended precisely
    // the tiles a miner can never occupy. canFit is authoritative and beats reach outright.
    if (canFit === false || reach <= 0) {
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
            mark(ctx, view, point.x, point.y, point.z || 0, point.reach, site.min, false);
        }
    }

    // Probe points are drawn hollow so a tile being considered never looks like one that is
    // authored. The Site tool asks about tiles it has not written yet, and the difference between
    // "this is in the file" and "this is what the file would say" is the whole point of asking.
    //
    // A candidate the author has taken is filled, because at that moment it IS what the file
    // would say. Hollow means "on offer", filled means "you have this one" - which is the only
    // thing distinguishing the two states, and without it the tool offered fifteen identical
    // dashed circles and no way to tell which of them were going to be written.
    for (const point of probe) {
        mark(ctx, view, point.x, point.y, point.z || 0, point.reach, point.min,
            !taken.has(tileKey(point.x, point.y)), point.canFit);
    }

    ctx.restore();
}

function mark(ctx, view, x, y, z, reach, min, hollow, canFit) {
    // The Z is the tile's own, so in art a candidate on a cliff face draws on the cliff face -
    // which is where the whole question of standing on it is decided.
    const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z);
    const width = ctx.canvas.clientWidth;
    const height = ctx.canvas.clientHeight;

    if (sx < -30 || sy < -30 || sx > width + 30 || sy > height + 30) {
        return;
    }

    const color = reachColor(reach, min, canFit);
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
