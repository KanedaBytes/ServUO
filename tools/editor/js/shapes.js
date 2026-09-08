// Drawing, hit-testing and editing the three shape kinds.
//
// The one import worth explaining is edgeColor, from the coverage overlay: the question it answers
// is the same one - is this hop inside what the pathfinder can actually walk? An edge a drag has
// just stretched past the cap has to LOOK wrong immediately, because the shard accepts it with a
// warning and the NPC then walks into scenery.
//
// Every layer arrives in the same vocabulary - rect, polyline, point - because the API projects
// the underlying config files into it. Nothing here knows that a tavern rectangle is stored with a
// Z and a shop district is not, or that one route is a dictionary entry and another is an inline
// array. That is the whole reason the projection exists.

/**
 * The layer table: what it is called, what colour it is, which file it is saved to, which request
 * reloads that file, and when its labels are worth drawing.
 *
 * `labelAt` is a `view.scale` floor - screen pixels per game tile - and `labelPriority` decides who
 * wins the space when two labels overlap, higher first. Both live here rather than in the drawing
 * code because they are per-layer editorial decisions, not geometry.
 */
import { edgeColor } from './coverage.js';
import { traceWorldRect } from './iso.js';
import { HOP_CAP } from './validate.js';

export const LAYERS = {
    'nav-edges': {
        label: 'Nav edges', color: '#4a7f5a', file: 'navigation', reload: 'nav-reload',
        labelAt: Infinity, labelPriority: 0
    },
    nav: {
        // 75 waypoint ids. Interesting when you are editing the graph, which is close in.
        label: 'Nav waypoints', color: '#7bd88f', file: 'navigation', reload: 'nav-reload',
        labelAt: 3.0, labelPriority: 3
    },
    'nav-destinations': {
        // The landmarks, and the names actually worth reading at town scale.
        label: 'Destinations', color: '#ffd479', file: 'navigation', reload: 'nav-reload',
        labelAt: 0.25, labelPriority: 5
    },
    'nav-arrivals': {
        // Four and five deep around one destination, so hover or selection only until you are
        // zoomed far enough in for them to be individually meaningful.
        label: 'Arrival points', color: '#c792ea', file: 'navigation', reload: 'nav-reload',
        labelAt: 6.0, labelPriority: 1
    },
    'nav-zones': {
        // Six of them, and they are the frame - readable even fitting the whole facet.
        label: 'Nav zones', color: '#5b8db8', file: 'navigation', reload: 'nav-reload',
        labelAt: 0.08, labelPriority: 4
    },
    'nav-routes': {
        // A polyline is identifiable from its shape; ten of them labelled at once buries the town.
        label: 'Authored routes', color: '#ff9d5c', file: 'navigation', reload: 'nav-reload',
        labelAt: Infinity, labelPriority: 2
    },
    dailylife: {
        // Hover or selection only: a daily-life marker sits ON the destination it names, so its
        // label collides with that destination's by construction.
        label: 'Daily life', color: '#6fb3ff', file: 'dailyLife', reload: 'dailylife-reload',
        labelAt: Infinity, labelPriority: 3
    },
    restricted: {
        label: 'Restricted zones', color: '#ff7a6b', file: 'restrictedZones', reload: 'zones-reload',
        labelAt: 0.08, labelPriority: 4
    },
    spawners: {
        // Few, and named. `file` is null because the family is many files: which one a spawner
        // belongs to is carried in its own id, and fileOf reads it from there.
        label: 'GG spawners', color: '#ff6fd8', file: null, reload: 'spawn-reload',
        labelAt: 1.0, labelPriority: 4
    },
    // uo-offline's data, drawn OVER ours and never confusable with it: dimmer, and dashed where
    // an authored edge is solid. Labels on hover only, for the stock-spawner reason - there are
    // 3952 of these and the label pass is not for context.
    reference: {
        label: 'uo-offline waypoints', color: '#6b7a8f', file: null, reload: null,
        labelAt: Infinity, labelPriority: 0
    },
    'reference-edges': {
        label: 'uo-offline roads', color: '#5a6675', file: null, reload: null,
        labelAt: Infinity, labelPriority: 0
    },
    'reference-destinations': {
        label: 'uo-offline places', color: '#9c8f7a', file: null, reload: null,
        labelAt: Infinity, labelPriority: 0
    },

    'spawners-stock': {
        // 2,572 on Trammel alone, loaded by viewport and never written. Labels on hover only -
        // they are context, and 2,572 names in the label pass is the thing being avoided.
        label: 'Stock spawners', color: '#8a7f9c', file: null, reload: null,
        labelAt: Infinity, labelPriority: 0
    },
    entities: { label: 'Live entities', color: '#ffffff', file: null, reload: null }
};

/** Layers the editor draws but never writes. */
export const READ_ONLY_LAYERS = new Set([
    'spawners-stock', 'entities', 'reference', 'reference-edges', 'reference-destinations']);

/** Layers whose shapes come from /api/reference. Read-only, and never saved. */
export const REFERENCE_LAYERS = new Set([
    'reference', 'reference-edges', 'reference-destinations']);

/** Layers whose shapes come from /api/spawners rather than /api/shapes. */
export const SPAWNER_LAYERS = new Set(['spawners', 'spawners-stock']);

// Edges are drawn first and everything else on top - there are more of them than anything else
// and they are the least interesting thing on the map when you are looking at a waypoint.
export const LAYER_ORDER = Object.keys(LAYERS);

const HANDLE = 5;
const GRAB = 7;

/**
 * Whether a shape is on the map at all.
 *
 * Not every shape is. The daily-life records - the tavern settings, a shopkeeper, a watch post -
 * are `kind: 'form'` with no points and no rect, because that file holds no coordinates: every
 * location in it is a nav id. They exist to be edited in the side panel and found by the filter.
 *
 * This is a guard rather than an assumption because the assumption already cost a bug. drawShape
 * fell through to its point branch and threw on `shape.points[0]`, which killed the draw loop
 * mid-frame - so the label pass never ran and the entity pass, which comes after it, never ran
 * either. One missing check, two invisible layers, and an exception inside requestAnimationFrame
 * that only the console ever saw.
 */
export function hasGeometry(shape) {
    return !!(shape.rect || (shape.points && shape.points.length > 0));
}

export function draw(ctx, view, shapes, visible, selected, hovered, matches) {
    const drawn = [];

    for (const shape of shapes) {
        if (!visible.has(shape.layer) || shape.map !== view.facet.name || !hasGeometry(shape)) {
            continue;
        }

        // Dimmed, not hidden: hiding a filtered-out shape reads as "it is gone", and the reason to
        // filter is to find one thing among the others, not to remove the others.
        const dimmed = matches !== null && !matches.has(shape);

        // save/restore rather than resetting by hand: drawShape returns from eight places, and the
        // reference style sets a line dash that would otherwise leak onto whatever is drawn next.
        ctx.save();
        ctx.globalAlpha = dimmed ? 0.22 : 1;
        drawShape(ctx, view, shape, shape === selected, shape === hovered);
        ctx.restore();

        drawn.push({ shape, dimmed });
    }

    drawLabels(ctx, view, drawn, selected, hovered, matches);
}

/**
 * Labels, in a second pass and with collision avoidance.
 *
 * Two passes because a label drawn during the first one gets painted over by the next shape's
 * fill, which is why zone labels used to disappear under other zones.
 *
 * Collision avoidance because the alternative on this data is a pile: the town square has four
 * arrivals, a destination, a tavern marker and half a dozen waypoints inside twenty tiles. A label
 * that collides tries below, right and left, and if all four positions are taken it is SKIPPED -
 * no dot, no ellipsis. An unreadable heap is the thing being fixed, and half of one is still it.
 */
function drawLabels(ctx, view, drawn, selected, hovered, matches) {
    const occupied = [];
    const soleMatch = matches !== null && matches.size === 1 ? [...matches][0] : null;

    // Selected and hovered reserve their space first, which is precisely why hovering something in
    // a crowd works: everything else has to fit around it.
    const rank = ({ shape }) => {
        if (shape === selected) return 1000;
        if (shape === hovered) return 900;
        if (shape === soleMatch) return 800;

        const layer = LAYERS[shape.layer];
        const area = shape.rect ? shape.rect[2] * shape.rect[3] : 0;

        return (layer ? layer.labelPriority || 0 : 0) * 10 - Math.min(area / 10000, 9);
    };

    for (const entry of [...drawn].sort((a, b) => rank(b) - rank(a))) {
        const shape = entry.shape;
        const layer = LAYERS[shape.layer] || {};
        const always = shape === selected || shape === hovered || shape === soleMatch;
        const threshold = layer.labelAt === undefined ? 0.35 : layer.labelAt;

        if (!always && view.scale < threshold) {
            continue;
        }

        ctx.globalAlpha = entry.dimmed ? 0.22 : 1;
        placeLabel(ctx, view, shape, occupied);
        ctx.globalAlpha = 1;
    }
}

/**
 * Edges the last audit could not path, keyed `from>to`.
 *
 * Module-level rather than threaded through draw(), following coverage.js: it is a derived overlay
 * that changes rarely and is read on every frame, and the alternative is a parameter on four
 * functions that only one of them uses.
 */
let audited = new Set();

/**
 * Re-derive every drawn line that is really made of ids.
 *
 * An edge IS two waypoint ids and a route IS a list of them - the line between them is a picture
 * of the data, not the data. Projecting them once at load left that picture frozen: dragging a
 * waypoint moved the dot and left its edges behind, pointing at where it used to be. The proposal
 * from the corridor tool made it obvious because it is nothing but waypoints and edges, but the
 * same staleness was there for every hand-placed edge in the graph.
 *
 * Called after anything that moves, creates or deletes a waypoint. Cheap: one pass to index and
 * one to rewrite, over shapes already in memory.
 */
export function syncDerived(shapes) {
    const points = new Map();

    for (const shape of shapes) {
        if (shape.layer === 'nav' && shape.points && shape.points.length > 0) {
            points.set(shape.props.id, shape.points[0]);
        }
    }

    for (const shape of shapes) {
        if (shape.layer === 'nav-edges') {
            const a = points.get(shape.props.from);
            const b = points.get(shape.props.to);

            if (a && b) {
                shape.points = [a.slice(), b.slice()];
            }

            continue;
        }

        if (shape.layer === 'nav-routes') {
            const list = String(shape.props.waypoints || '')
                .split(' ')
                .filter(Boolean)
                .map((id) => points.get(id))
                .filter(Boolean);

            if (list.length >= 2) {
                shape.points = list.map((point) => point.slice());
            }
        }
    }
}

/** edge id -> true when the shard has walked it since it was last moved, false when it refused. */
let hopFlags = new Map();

/**
 * Record what the shard said about a hop, so an edited proposal shows its own state.
 *
 * Separate from the audit flags above, which are about the SAVED graph. A proposal is not in the
 * file yet, so [NavAudit has nothing to say about it, and the author still needs to know whether
 * the hop they just dragged is one a bot can walk.
 */
export function setHopFlags(flags) {
    hopFlags = flags || new Map();
}

export function clearHopFlags() {
    hopFlags = new Map();
}

export function setAuditFlags(problems) {
    audited = new Set(
        (problems || [])
            .filter((problem) => problem.blocked)
            .flatMap((problem) => [`${problem.from}>${problem.to}`, `${problem.to}>${problem.from}`]));
}

/**
 * A walk edge's colour by how close it is to the hop cap, or null for everything else.
 *
 * Chebyshev, because that is what the shard measures with and what the pathfinder walks in. A gate
 * is a teleport and has no length worth colouring.
 */
function hopColor(shape) {
    if (shape.layer !== 'nav-edges' || shape.points.length !== 2) {
        return null;
    }

    if (shape.props && shape.props.kind === 'gate') {
        return null;
    }

    // A hop the shard has just walked for us wins over everything: it is the most specific and
    // most recent thing anybody knows about this edge.
    if (hopFlags.size > 0 && hopFlags.has(shape.id)) {
        return hopFlags.get(shape.id) ? '#7bd88f' : '#ff7a6b';
    }

    // An edge the audit could not walk is red regardless of length. Length is a proxy the editor
    // can compute; walkability is a fact only the engine's own pathfinder knows, so when it has
    // spoken it wins.
    if (audited.size > 0 && shape.props && audited.has(`${shape.props.from}>${shape.props.to}`)) {
        return 'rgba(255, 40, 40, 1)';
    }

    const [a, b] = shape.points;

    return edgeColor(Math.max(Math.abs(a[0] - b[0]), Math.abs(a[1] - b[1])), HOP_CAP);
}

function labelAnchor(shape) {
    if (shape.rect) {
        // A rect has no Z in the schema, so its label sits on the ground plane like its outline.
        return [shape.rect[0] + shape.rect[2] / 2, shape.rect[1], 0];
    }

    // Tile centre, matching where the marker itself is drawn. The two used to disagree by half a
    // tile, which is invisible until you are zoomed in far enough to see the marker as a circle.
    return [shape.points[0][0] + 0.5, shape.points[0][1] + 0.5, shape.points[0][2] || 0];
}

function placeLabel(ctx, view, shape, occupied) {
    if (!shape.label) {
        return;
    }

    const [worldX, worldY, worldZ] = labelAnchor(shape);
    const [sx, sy] = view.toScreen(worldX, worldY, worldZ);

    ctx.font = '11px ui-sans-serif, system-ui, sans-serif';

    const width = ctx.measureText(shape.label).width;
    const boxWidth = width + 6;

    const candidates = [
        [sx - boxWidth / 2, sy - 17],
        [sx - boxWidth / 2, sy + 6],
        [sx + 10, sy - 7],
        [sx - boxWidth - 10, sy - 7]
    ];

    for (const [left, top] of candidates) {
        const box = [left, top, left + boxWidth, top + 14];

        if (occupied.some((other) => overlaps(box, other))) {
            continue;
        }

        occupied.push(box);

        ctx.fillStyle = 'rgba(0, 0, 0, .65)';
        ctx.fillRect(left, top, boxWidth, 14);
        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'left';
        ctx.fillText(shape.label, left + 3, top + 11);

        return;
    }
}

function overlaps(a, b) {
    return a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];
}

function drawShape(ctx, view, shape, isSelected, isHovered) {
    // A layer the editor does not know about is drawn in grey rather than throwing inside the
    // draw loop, which would take the whole canvas down over one unexpected shape.
    const color = (LAYERS[shape.layer] || { color: '#888888' }).color;

    // Hover is deliberately quieter than selection: it answers "what would a click take?" without
    // competing with "what is currently taken".
    ctx.lineWidth = isSelected ? 2 : isHovered ? 2 : 1;
    ctx.strokeStyle = isSelected ? '#ffffff' : isHovered ? '#cfe4ff' : hopColor(shape) || color;
    ctx.fillStyle = color;

    // Reference records are somebody else's data drawn over ours, and the one thing that must
    // never happen is mistaking one for the other while editing. Dashed and half-opacity says
    // "this is not yours" at a glance, without needing the layer list to be read.
    const isReference = REFERENCE_LAYERS.has(shape.layer);

    if (isReference) {
        ctx.setLineDash(isSelected || isHovered ? [] : [4, 3]);
        ctx.globalAlpha *= isSelected || isHovered ? 0.9 : 0.55;
    }

    if (shape.kind === 'rect') {
        const [x, y, w, h] = shape.rect;
        const opacity = ctx.globalAlpha;

        // A world rect is a rect on screen in radar and a diamond in art, so `w * view.scale` is
        // only a rect in one of the two. traceWorldRect paths the four projected corners and says
        // whether it did; radar keeps the fillRect/strokeRect pair, unchanged.
        if (traceWorldRect(ctx, view, x, y, w, h)) {
            ctx.globalAlpha = opacity * (isHovered && !isSelected ? 0.3 : 0.18);
            ctx.fill();
            ctx.globalAlpha = opacity;
            ctx.stroke();
        } else {
            const [sx, sy] = view.toScreen(x, y);
            const sw = w * view.scale;
            const sh = h * view.scale;

            ctx.globalAlpha = opacity * (isHovered && !isSelected ? 0.3 : 0.18);
            ctx.fillRect(sx, sy, sw, sh);
            ctx.globalAlpha = opacity;
            ctx.strokeRect(sx, sy, sw, sh);
        }

        if (isSelected) {
            for (const [hx, hy] of rectHandles(shape.rect)) {
                const [px, py] = view.toScreen(hx, hy);
                ctx.fillStyle = '#ffffff';
                ctx.fillRect(px - HANDLE / 2, py - HANDLE / 2, HANDLE, HANDLE);
            }
        }

        return;
    }

    // A filled, closed shape - the same fill and stroke a rect gets, so a poly zone and a rect
    // zone read as the same KIND of thing on the map and only their outline differs.
    if (shape.kind === 'poly') {
        const points = shape.points || [];

        if (points.length < 3) {
            return;
        }

        ctx.beginPath();

        points.forEach(([x, y, z], i) => {
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z || 0);
            i === 0 ? ctx.moveTo(sx, sy) : ctx.lineTo(sx, sy);
        });

        ctx.closePath();

        const opacity = ctx.globalAlpha;

        ctx.globalAlpha = opacity * (isHovered && !isSelected ? 0.3 : 0.18);
        ctx.fill();
        ctx.globalAlpha = opacity;
        ctx.stroke();

        if (isSelected) {
            for (const [hx, hy, hz] of points) {
                const [px, py] = view.toScreen(hx + 0.5, hy + 0.5, hz || 0);
                ctx.fillStyle = '#ffffff';
                ctx.fillRect(px - HANDLE / 2, py - HANDLE / 2, HANDLE, HANDLE);
            }
        }

        return;
    }

    if (shape.kind === 'polyline') {
        ctx.beginPath();

        shape.points.forEach(([x, y, z], i) => {
            // Route nodes are tile centres, not corners; the half-tile offset is what makes a line
            // drawn between them sit on the tiles the NPC actually walks. The Z rides along, so in
            // art an edge climbing the hill out of town is drawn climbing it.
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z || 0);
            i === 0 ? ctx.moveTo(sx, sy) : ctx.lineTo(sx, sy);
        });

        if (shape.props && shape.props.closed) {
            ctx.closePath();
        }

        ctx.stroke();

        for (const [x, y, z] of shape.points) {
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z || 0);
            ctx.fillStyle = isSelected ? '#ffffff' : isHovered ? '#cfe4ff' : color;
            ctx.fillRect(sx - HANDLE / 2, sy - HANDLE / 2, HANDLE, HANDLE);
        }

        return;
    }

    const [x, y, z] = shape.points[0];
    const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z || 0);

    ctx.beginPath();
    ctx.arc(sx, sy, isSelected || isHovered ? 6 : 4, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
}

/** The shape a click would take right now - same rules as hitTest, ignoring handles. */
export function pick(view, shapes, visible, worldX, worldY, screenX = null, screenY = null) {
    const found = hitTest(view, shapes, visible, null, worldX, worldY, screenX, screenY);

    return found ? found.shape : null;
}

/** Draws whatever the active create tool has collected so far. */
export function drawDraft(ctx, view, draft) {
    if (!draft || (!draft.points.length && !draft.rect)) {
        return;
    }

    ctx.save();
    ctx.setLineDash([5, 4]);
    ctx.strokeStyle = '#ffd479';
    ctx.fillStyle = '#ffd479';
    ctx.lineWidth = 2;

    // The rect is drawn whenever there is one, NOT only while `kind` says 'rect'.
    //
    // The Site tool flips the draft to 'points' the instant the zone drag ends, so it can start
    // collecting arrivals - and the zone the author had just finished dragging vanished off the
    // map at exactly the moment they were asked to place tiles inside it. The two are
    // independent: `kind` says what the next click collects, `rect` says what has been drawn
    // already. Keying the drawing off `kind` conflated them.
    if (draft.rect) {
        const [x, y, w, h] = draft.rect;

        if (traceWorldRect(ctx, view, x, y, w, h)) {
            ctx.stroke();
        } else {
            const [sx, sy] = view.toScreen(x, y);
            ctx.strokeRect(sx, sy, w * view.scale, h * view.scale);
        }
    }

    // A rect-drag tool starts with no points at all, which the early return above used to treat
    // as nothing to draw - so navzone and restricted dragged out with no rubber band whatsoever.
    if (draft.kind !== 'rect' && draft.points.length > 0) {
        ctx.beginPath();

        draft.points.forEach(([x, y, z], i) => {
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z || 0);
            i === 0 ? ctx.moveTo(sx, sy) : ctx.lineTo(sx, sy);
        });

        ctx.stroke();

        for (const [x, y, z] of draft.points) {
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5, z || 0);
            ctx.fillRect(sx - 3, sy - 3, 6, 6);
        }
    }

    ctx.restore();
}


// Our kinds, not ModernUO's: the snapshot classifies by what the shard actually is - a managed
// shopkeeper, a daily-life actor, a stock creature - because "npc" covers all three and tells you
// nothing about which one just stopped moving.
const ENTITY_COLORS = {
    player: '#ffffff',
    staff: '#ffd479',
    vendor: '#6fb3ff',
    actor: '#7bd88f',
    bot: '#c98bdb',
    creature: '#9a8c98'
};

/**
 * A bot's colour is what it is DOING, not what it is.
 *
 * Every bot the same shade answers "where are they" and nothing else; a town full of identical
 * dots cannot show that the miners never come back or that everyone is sitting at the bank. The
 * kind colour above is the fallback, so an unrecognised behaviour still draws as a bot.
 *
 * BEHAVIOR_COLORS must cover every name BotBehaviors registers - modules.test.js asserts it
 * against the C# registry, because a behaviour added on the shard and forgotten here would draw
 * as an ordinary bot and look deliberate.
 */
export const BEHAVIOR_COLORS = {
    Traveler: '#c98bdb',
    BankSitter: '#ffd479',
    Shopper: '#6fb3ff',
    Crafter: '#ff9d5c',
    Gatherer: '#7bd88f',
    Idle: '#9a8c98'
};

export function drawEntities(ctx, view, entities) {
    const width = ctx.canvas.clientWidth;
    const height = ctx.canvas.clientHeight;

    for (const entity of entities) {
        if (entity.map !== view.facet.name) {
            continue;
        }

        const [sx, sy] = view.toScreen(entity.x + 0.5, entity.y + 0.5, entity.z || 0);

        // Culled, unlike the original. It had no cull and no cap because its entity list was
        // bounded by design; ours can be pointed at a whole town with [LiveMap all.
        if (sx < -20 || sy < -20 || sx > width + 20 || sy > height + 20) {
            continue;
        }

        // Where it is going, when a NavWalker is steering it. A stationary NPC and one halfway
        // through a walk home are otherwise identical on the map.
        if (entity.route && entity.route.length >= 4) {
            ctx.beginPath();
            ctx.moveTo(sx, sy);

            for (let i = 2; i < entity.route.length; i += 2) {
                // A route is a flat [x, y, ...] list with no Z, so it draws on the ground under
                // the bot rather than at its feet. Close enough to read as a route; a Z per step
                // would be three times the snapshot for a line.
                const [rx, ry] = view.toScreen(entity.route[i] + 0.5, entity.route[i + 1] + 0.5);
                ctx.lineTo(rx, ry);
            }

            ctx.strokeStyle = 'rgba(255, 120, 220, .75)';
            ctx.lineWidth = 1.5;
            ctx.stroke();
        }

        ctx.beginPath();
        ctx.arc(sx, sy, 4, 0, Math.PI * 2);
        ctx.fillStyle = (entity.behavior && BEHAVIOR_COLORS[entity.behavior])
            || ENTITY_COLORS[entity.kind] || '#ffffff';
        ctx.fill();
        ctx.strokeStyle = 'rgba(0, 0, 0, .7)';
        ctx.lineWidth = 1;
        ctx.stroke();

        if (view.scale >= 0.5 && entity.name) {
            ctx.font = '10px ui-sans-serif, system-ui, sans-serif';
            ctx.fillStyle = 'rgba(255, 255, 255, .85)';
            ctx.fillText(entity.name, sx + 7, sy + 3);
        }
    }
}

/**
 * Ray casting, mirroring NavZone.Contains so the editor and the shard agree about a tile.
 *
 * The tile CENTRE is tested, not its corner - a vertex landing exactly on a tile boundary would
 * otherwise make containment depend on which way a floating-point comparison happened to fall,
 * and it would fall differently in C# and JavaScript.
 */
function insidePolygon(points, worldX, worldY) {
    if (!points || points.length < 3) {
        return true;
    }

    const px = Math.floor(worldX) + 0.5;
    const py = Math.floor(worldY) + 0.5;
    let inside = false;

    for (let i = 0, j = points.length - 1; i < points.length; j = i, i++) {
        const [ix, iy] = points[i];
        const [jx, jy] = points[j];

        if ((iy > py) !== (jy > py) && px < ((jx - ix) * (py - iy)) / (jy - iy) + ix) {
            inside = !inside;
        }
    }

    return inside;
}

function rectHandles([x, y, w, h]) {
    return [[x, y], [x + w, y], [x, y + h], [x + w, y + h]];
}

/**
 * What is under the cursor, nearest first. Handles beat bodies so a corner is always grabbable
 * even when it sits inside another shape.
 */
export function hitTest(view, shapes, visible, selected, worldX, worldY, screenX = null, screenY = null) {
    const slack = GRAB / view.scale;

    // IN ART, PICKING HAPPENS IN SCREEN SPACE.
    //
    // The isometric inverse is not a function - a screen pixel names a world tile only once you
    // assume a Z - so `worldX, worldY` in art view is the GROUND-PLANE guess and is off by z*4/44
    // tiles, about 2.7 in Britain. Comparing in world space there would select whatever happens to
    // be three tiles north-west of what you clicked.
    //
    // But the editor drew every one of these shapes itself, and it drew them by projecting their
    // own Z. So the screen position of each candidate is known exactly, and the cursor's screen
    // position is known exactly, and the comparison is just a distance. No depth buffer needed.
    // What this does NOT give is a world position for a point the editor did not draw, which is
    // why placing and dragging stay disabled in art view until the renderer can hand over a pick
    // map.
    const art = Boolean(view.isArt) && screenX !== null && screenY !== null;

    const at = (point) => view.toScreen(point[0], point[1], point[2] || 0);

    const hits = art
        ? (point) => {
            const [sx, sy] = at(point);
            return Math.abs(sx - screenX) <= GRAB && Math.abs(sy - screenY) <= GRAB;
        }
        : (point) => near(point, worldX, worldY, slack);

    const hitsSegment = art
        ? (a, b) => {
            const [ax, ay] = at([a[0] + 0.5, a[1] + 0.5, a[2]]);
            const [bx, by] = at([b[0] + 0.5, b[1] + 0.5, b[2]]);

            // nearSegment offsets its ends to tile centres, so the projected pair is passed back
            // already offset and then un-offset. Ugly, and cheaper than a second copy of the
            // point-to-segment maths.
            return nearSegment([ax - 0.5, ay - 0.5], [bx - 0.5, by - 0.5], screenX, screenY, GRAB);
        }
        : (a, b) => nearSegment(a, b, worldX, worldY, slack);

    /** Is the cursor inside a world rect, projected? A rect is a diamond in art. */
    const insideRect = art
        ? ([x, y, w, h]) => insidePolygon(
            [[x, y], [x + w, y], [x + w, y + h], [x, y + h]].map((corner) => at(corner)),
            screenX,
            screenY)
        : ([x, y, w, h]) => worldX >= x && worldX <= x + w && worldY >= y && worldY <= y + h;

    if (selected && visible.has(selected.layer) && selected.map === view.facet.name) {
        if (selected.kind === 'rect') {
            const handles = rectHandles(selected.rect);

            for (let i = 0; i < handles.length; i++) {
                if (hits(handles[i])) {
                    return { shape: selected, mode: 'resize', index: i };
                }
            }
        } else {
            for (let i = 0; i < selected.points.length; i++) {
                const [px, py, pz] = selected.points[i];

                if (hits([px + 0.5, py + 0.5, pz])) {
                    return { shape: selected, mode: 'node', index: i };
                }
            }
        }
    }

    // By kind, then by size - never by draw order.
    //
    // Drawing order is the wrong rule for picking. The shop district is a 210x170 rectangle that
    // contains the tavern, every shop, two watch posts and several routes; picking by draw order
    // meant a click anywhere inside it selected the district and nothing else was reachable by
    // mouse at all.
    //
    // The tiers are points, then lines, then areas, and a tier wins outright - area never breaks
    // a tie across tiers, only within the area tier. This used to be one loop that returned the
    // first node it touched in reverse array order, and that is subtly not the same thing: an
    // edge's endpoints sit exactly on the waypoints it joins, edges are projected after
    // waypoints, so reverse order reached the edge first and a waypoint could not be dragged at
    // all with nav-edges on. The comment that used to sit here asserted the opposite - that the
    // loop "always returns the waypoint" - which was the assumption the bug was made of.
    const candidates = (test) => {
        for (let i = shapes.length - 1; i >= 0; i--) {
            const shape = shapes[i];

            if (!visible.has(shape.layer) || shape.map !== view.facet.name || !hasGeometry(shape)) {
                continue;
            }

            const hit = test(shape);

            if (hit) {
                return hit;
            }
        }

        return null;
    };

    const nodeIn = (shape) => {
        if (!shape.points) {
            return null;
        }

        for (let n = 0; n < shape.points.length; n++) {
            const [px, py, pz] = shape.points[n];

            if (hits([px + 0.5, py + 0.5, pz])) {
                return { shape, mode: shape.kind === 'point' ? 'move' : 'node', index: n };
            }
        }

        return null;
    };

    // 1. Points. Small, deliberate targets, and the thing a click on one unambiguously means.
    const point = candidates((shape) => (shape.kind === 'point' ? nodeIn(shape) : null));

    if (point) {
        return point;
    }

    // 2. The nodes of a line or a polygon - draggable vertices, still smaller targets than a body.
    const node = candidates((shape) =>
        (shape.kind === 'polyline' || shape.kind === 'poly' ? nodeIn(shape) : null));

    if (node) {
        return node;
    }

    // 3. The LINE of a polyline, not just its nodes. Above the areas, not below them: an edge
    //    drawn across a nav zone has to stay clickable, or there is no way to delete it.
    const line = candidates((shape) => {
        if (shape.kind !== 'polyline') {
            return null;
        }

        for (let n = 1; n < shape.points.length; n++) {
            if (hitsSegment(shape.points[n - 1], shape.points[n])) {
                return { shape, mode: 'body' };
            }
        }

        return null;
    });

    if (line) {
        return line;
    }

    // 4. Areas, smallest first - the one the click most specifically means. A poly is tested by
    //    its bounding box and then for real, the same order NavZone.Contains uses and for the
    //    same reason: the box rejects almost everything for four comparisons instead of a ray
    //    cast. Polys reach this at all only since the tiers were split; the old single loop sent
    //    every non-rect kind down the node path and `continue`d, so a polygon zone could be
    //    picked by a vertex but never by its interior.
    let best = null;
    let bestArea = Infinity;

    for (let i = shapes.length - 1; i >= 0; i--) {
        const shape = shapes[i];

        if (!visible.has(shape.layer) || shape.map !== view.facet.name || !hasGeometry(shape)) {
            continue;
        }

        if (shape.kind !== 'rect' && shape.kind !== 'poly') {
            continue;
        }

        const [x, y, w, h] = shape.rect;

        if (!insideRect(shape.rect)) {
            continue;
        }

        // The bounding box rejected almost everything above; this is the real test. In art the
        // polygon's own vertices are projected too, each at its own Z.
        if (shape.kind === 'poly') {
            const inside = art
                ? insidePolygon(shape.points.map((point) => at(point)), screenX, screenY)
                : insidePolygon(shape.points, worldX, worldY);

            if (!inside) {
                continue;
            }
        }

        if (w * h < bestArea) {
            best = { shape, mode: 'move' };
            bestArea = w * h;
        }
    }

    return best;
}

function near([x, y], worldX, worldY, slack) {
    return Math.abs(x - worldX) <= slack && Math.abs(y - worldY) <= slack;
}

/** Perpendicular distance to a segment, clamped to its ends. Nodes are offset to tile centres. */
export function nearSegment(a, b, worldX, worldY, slack) {
    const ax = a[0] + 0.5;
    const ay = a[1] + 0.5;
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const lengthSquared = dx * dx + dy * dy;

    if (lengthSquared === 0) {
        return near([ax, ay], worldX, worldY, slack);
    }

    const t = Math.max(0, Math.min(1, ((worldX - ax) * dx + (worldY - ay) * dy) / lengthSquared));

    return Math.hypot(ax + t * dx - worldX, ay + t * dy - worldY) <= slack;
}

export function geometryOf(shape) {
    return shape.kind === 'rect'
        ? { rect: shape.rect.slice() }
        : { points: shape.points.map((p) => p.slice()) };
}

export function applyGeometry(shape, geometry) {
    if (geometry.rect) {
        shape.rect = geometry.rect.slice();
    } else {
        shape.points = geometry.points.map((p) => p.slice());
    }
}

export function moveShape(shape, dx, dy) {
    if (shape.kind === 'rect') {
        shape.rect[0] += dx;
        shape.rect[1] += dy;
        return;
    }

    for (const point of shape.points) {
        point[0] += dx;
        point[1] += dy;
    }
}

/**
 * Drags one corner. The opposite corner is the anchor, so dragging past it flips the rectangle
 * rather than inverting its size - a negative width would be rejected by the server and is not a
 * state the editor should be able to reach.
 */
export function resizeRect(shape, index, worldX, worldY) {
    const [x, y, w, h] = shape.rect;
    const anchorX = index === 0 || index === 2 ? x + w : x;
    const anchorY = index === 0 || index === 1 ? y + h : y;

    const left = Math.min(anchorX, worldX);
    const top = Math.min(anchorY, worldY);

    shape.rect = [
        Math.round(left),
        Math.round(top),
        Math.max(1, Math.round(Math.abs(anchorX - worldX))),
        Math.max(1, Math.round(Math.abs(anchorY - worldY)))
    ];
}

export function moveNode(shape, index, worldX, worldY) {
    shape.points[index][0] = Math.round(worldX - 0.5);
    shape.points[index][1] = Math.round(worldY - 0.5);
}
