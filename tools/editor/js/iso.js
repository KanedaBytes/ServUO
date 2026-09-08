// The isometric projection. One formula, used by the renderer, the camera and every layer.
//
// THE FORMULA IS NOT INVENTED HERE. It is Ultima/Multis.cs:502-511 - the one working isometric
// renderer already in this tree - and tools/MapExport/IsoTransform.cs is the other copy of it.
// `iso.test.js` pins the two together against the literals in that file, because a projection that
// exists twice and drifts once puts every layer a couple of tiles off the art it is drawn on.
//
//     anchor(x, y, z) = ((x - y) * 22, (x + y) * 22 - z * 4)
//
// and any sprite is drawn with its BOTTOM CENTRE on that anchor. Land art is 44x44, so a land tile
// lands at (ix - 22, iy - 44): the same rule, not a special case.
//
// Two things follow, and they are why the art view was a small change rather than a rewrite.
//
// TWENTY-TWO ISO PIXELS PER WORLD TILE, PER AXIS. So `view.scale` can go on meaning screen pixels
// per game tile in both projections, and every `labelAt` threshold, cull margin and hit-test slack
// in the editor keeps the meaning it already had. 1:1 art is scale 22.
//
// AT Z=0 THE PROJECTION IS LINEAR. dx moves the screen point by (+scale, +scale) and dy by
// (-scale, +scale), so it is an ordinary canvas matrix - which is how the radar underlay is drawn
// beneath the art with the existing world-space tile loop and a setTransform.
//
// THE INVERSE IS NOT A FUNCTION, and that is why there is a pick map. A screen pixel names a world
// tile only once you assume a Z, so `isoToWorld` answers for the ground plane and is off by
// z*4/44 tiles - about 2.7 in Britain, which sits near z 30. It survives here for panning and
// zooming, where being a couple of tiles out about the point under the cursor does not matter, and
// as the momentary estimate the readout marks with a ~ while a pick tile is still in flight.
//
// The real answer is a lookup: `canvasToTile` below turns a canvas pixel into a tile and a pixel
// inside it, and js/pickmap.js reads what the renderer recorded there while it was drawing.
//
// ------------------------------------------------------------------------------------------------
// WHERE A TILE ACTUALLY IS: THE ANCHOR IS NOT THE CENTRE
// ------------------------------------------------------------------------------------------------
//
// This distinction cost every waypoint placed in the art view one tile in x AND one tile in y, and
// it is worth being exact about because the two points are only 44 pixels apart and everything
// still looks plausible when they are confused.
//
//     A(x, y, z) = ((x - y) * 22, (x + y) * 22 - z * 4)
//
// A IS THE SPRITE ANCHOR - the point a sprite hangs from by its BOTTOM CENTRE (Ultima/Multis.cs:
// 505-507, `px -= bmp.Width / 2; py -= bmp.Height`). It is `worldToIso`. For a 44x44 land tile it
// is the diamond's SOUTH VERTEX, so it is 22 pixels BELOW the middle of the tile it belongs to.
//
// THE CENTRE of tile (x, y) is therefore A - (0, 22), which is `isoCorner(x + 0.5, y + 0.5, z)`.
//
// The client agrees, and this is the citation rather than a guess. ClassicUO, camera removed:
//
//   GameObject.UpdateRealScreenPosition
//       RealScreenPosition.X = ((X - Y) * 22) - offsetX - 22;
//       RealScreenPosition.Y = ((X + Y) * 22 - (Z << 2)) - offsetY - 22;
//
//   LandView.Draw            batcher.Draw(artInfo.Texture, new Vector2(posX, posY), ..., origin
//                            Vector2.Zero) - so RSP is the 44x44 art's TOP-LEFT, and the land
//                            diamond's centre is RSP + (22, 22) = A.
//
//   View.DrawStaticAnimated  index.Width  = (UV.Width >> 1) - 22;   x -= index.Width;
//                            index.Height =  UV.Height      - 44;   y -= index.Height;
//                            - so a static's sprite lands with its BOTTOM CENTRE at
//                            RSP + (22, 44) = A + (0, 22).
//
//   MobileView.Draw          posY -= 3; drawX += 22; drawY += 22; then the sprite is placed by
//                            `y -= spriteInfo.UV.Height + spriteInfo.Center.Y` - so a mobile's
//                            FEET land at A - (0, 3), which is the land diamond's centre give or
//                            take the three-pixel baseline of the mobile art.
//
// So in the client: land centre at A, a static's foot 22 below it, a mobile's feet on the land
// centre. THE RELATIVE GEOMETRY IS WHAT MATTERS, and ours matches it exactly - this renderer draws
// land with its bottom on A (centre A - 22) and a static's foot on A, which is the same 22-pixel
// gap. The whole picture sits 22 pixels higher than ClassicUO's, and that is camera placement: it
// is unobservable, and no pixel moves relative to any other. A 44x44 FLOOR STATIC tiles exactly
// with land in both, which is the invariant that pins it.
//
// WHAT THIS FILE THEREFORE MEANS BY A COORDINATE. `worldToIso` is the anchor and is for hanging
// sprites. Everything else - every marker, every label, every rect corner, every entity, every hit
// test - works in LATTICE space, where the point (x, y) is the corner shared by four tiles and tile
// (x, y) spans (x, y) to (x + 1, y + 1). `isoCorner` is that mapping and takes fractional
// coordinates, so `isoCorner(x + 0.5, y + 0.5, z)` is a tile's middle. `view.toScreen` uses it, and
// that makes the two projections agree: `toScreen(x, y)` is the same world point in radar and in
// art, and `+ 0.5` centres in both.
//
// The bug this replaces: `toScreen` used `worldToIso`, so `toScreen(x + 0.5, y + 0.5)` was A + 22
// rather than A - 22. Forty-four pixels low, which is one tile along (x + y) - one tile in x and
// one in y at once, exactly what the client reported against every waypoint placed on the art.

export const HALF_WIDTH = 22;
export const HALF_HEIGHT = 22;
export const Z_STEP = 4;

/** Land art is always 44x44 (Ultima/Art.cs:528-529). */
export const LAND_ART_SIZE = 44;

// Must match tools/MapExport/IsoTransform.cs. The renderer reports them in its handshake and the
// editor prefers what it is told; these are the fallback for a bridge with no renderer yet.
export const MAX_SPRITE_HEIGHT = 512;
export const Z_LIFT = 128 * Z_STEP;
export const HEADROOM_TOP = Z_LIFT + MAX_SPRITE_HEIGHT;
export const HEADROOM_BOTTOM = Z_LIFT;
export const TILE_SIZE = 256;

/** How many of the deepest levels are art at all; below that the editor draws radar. */
export const ART_LEVEL_DEPTH = 4;

/** How many of the deepest levels carry separate floors; below that there is one composite. */
export const FLOOR_LEVEL_DEPTH = 2;

export const FLOORS = ['ground', 'first', 'all'];

export function worldToIso(x, y, z = 0) {
    return {
        ix: (x - y) * HALF_WIDTH,
        iy: (x + y) * HALF_HEIGHT - z * Z_STEP
    };
}

/**
 * The ground-plane inverse. Pass the z you are assuming; the caller has to know it is assuming
 * one. Derived by adding and subtracting the two equations above.
 */
export function isoToWorld(ix, iy, z = 0) {
    const lifted = iy + z * Z_STEP;

    return {
        x: (lifted / HALF_HEIGHT + ix / HALF_WIDTH) / 2,
        y: (lifted / HALF_HEIGHT - ix / HALF_WIDTH) / 2
    };
}

/**
 * A POINT ON THE WORLD LATTICE, in iso pixels. The editor's one meaning for a coordinate.
 *
 * The lattice point (x, y) is the corner shared by four tiles, and tile (x, y) spans (x, y) to
 * (x + 1, y + 1) - the same four points Server/Map.cs:552-592 samples for GetAverageZ. Integer
 * arguments give a grid corner, which is what land is stretched between; FRACTIONAL ARGUMENTS ARE
 * MEANT TO WORK, and `isoCorner(x + 0.5, y + 0.5, z)` is the middle of tile (x, y). See the note
 * at the top of this file on why that is 44 pixels above `worldToIso(x + 0.5, y + 0.5, z)`.
 *
 * It is the anchor formula applied to the corner's own cell and lifted one land tile. The lift is
 * what reconciles the two conventions: `worldToIso` gives the point a SPRITE hangs from - its
 * bottom centre, which for a 44x44 land tile is the diamond's south vertex - while a corner is a
 * vertex in its own right.
 *
 * The payoff is the invariant the whole stretched-terrain feature rests on, and the one iso.test.js
 * asserts: WHEN THE FOUR CORNER Zs ARE EQUAL, THE FOUR CORNERS ARE EXACTLY THE FOUR VERTICES OF THE
 * FLAT 44x44 ART. So a flat tile occupies the same pixels stretched or not, and stretching can only
 * move ground that is genuinely sloped.
 *
 * The renderer is the only consumer today, in C# - tools/MapExport/IsoTransform.cs IsoCornerX/Y.
 * This copy exists for the same reason every constant in this file does: so the two can be pinned
 * together, because a projection that exists twice and drifts once is the failure that reads as bad
 * nav data rather than a bad projection.
 */
export function isoCorner(cornerX, cornerY, cornerZ = 0) {
    return {
        ix: (cornerX - cornerY) * HALF_WIDTH,
        iy: (cornerX + cornerY) * HALF_HEIGHT - cornerZ * Z_STEP - LAND_ART_SIZE
    };
}

/**
 * The middle of tile (x, y) at height z - where a marker for that tile goes, and where a mobile
 * standing on it has its feet.
 *
 * Named because the alternative is `+ 0.5` scattered across six files, which is what it was, and
 * a half-tile idiom that means one thing in radar and another in art is how the two came apart.
 */
export function tileCentre(x, y, z = 0) {
    return isoCorner(x + 0.5, y + 0.5, z);
}

/**
 * The lattice point a projected iso pixel came from, at an assumed z - the inverse of `isoCorner`.
 *
 * `isoToWorld` inverts `worldToIso`, which is the ANCHOR, so it answers 22 pixels out for anything
 * working in lattice space. The whole reason `view.toWorld` exists is to undo `view.toScreen`, and
 * `toScreen` is lattice, so it has to undo the lattice mapping.
 */
export function isoToCorner(ix, iy, z = 0) {
    return isoToWorld(ix, iy + LAND_ART_SIZE, z);
}

/**
 * A land tile's four projected corners, north/east/south/west - the quad the texture is stretched
 * onto. The Z arguments are the land heights at (x,y), (x+1,y), (x+1,y+1) and (x,y+1), which is the
 * order Server/Map.cs:552-592 samples them in.
 */
export function landQuad(x, y, zTop, zRight, zBottom, zLeft) {
    return {
        north: isoCorner(x, y, zTop),
        east: isoCorner(x + 1, y, zRight),
        south: isoCorner(x + 1, y + 1, zBottom),
        west: isoCorner(x, y + 1, zLeft)
    };
}

/** Where a w x h sprite's top-left goes, given its anchor. Ultima/Multis.cs:505-507. */
export function spriteTopLeft(ix, iy, width, height) {
    return { x: ix - Math.floor(width / 2), y: iy - height };
}

/**
 * The canvas transform for the world->screen projection at z=0, as ctx.setTransform's first four
 * arguments: [a, b, c, d] = [dsx/dx, dsy/dx, dsx/dy, dsy/dy].
 */
export function isoMatrix(scale) {
    return [scale, scale, -scale, scale];
}

/** The iso pixel that is canvas column 0. Negative: (0, height-1) projects a long way left. */
export function originX(facetHeight) {
    return -(facetHeight * HALF_WIDTH);
}

/** The iso pixel that is canvas row 0. Negative, to leave room for lifted, tall sprites. */
export function originY() {
    return -HEADROOM_TOP;
}

export function canvasWidth(facetWidth, facetHeight) {
    return (facetWidth + facetHeight) * HALF_WIDTH;
}

export function canvasHeight(facetWidth, facetHeight) {
    return (facetWidth + facetHeight) * HALF_HEIGHT + HEADROOM_TOP + HEADROOM_BOTTOM;
}

/**
 * Ceil-halve until the canvas fits one tile - the same rule as IsoTransform.MaxLevel and, for the
 * radar pyramid, TilePyramid.LevelCount and view.js maxZoomFor. It has to stay the same rule or
 * the editor asks for a level the renderer does not believe in.
 */
export function maxLevel(facetWidth, facetHeight, tileSize = TILE_SIZE) {
    let width = canvasWidth(facetWidth, facetHeight);
    let height = canvasHeight(facetWidth, facetHeight);
    let levels = 0;

    while (width > tileSize || height > tileSize) {
        width = Math.ceil(width / 2);
        height = Math.ceil(height / 2);
        levels++;
    }

    return levels;
}

/** Iso pixels per rendered pixel at a level. 1 at the deepest. */
export function divisor(level, max) {
    return 2 ** (max - level);
}

/** Whether a level has its own floors, or only the composite. */
export function hasFloors(level, max) {
    return level > max - FLOOR_LEVEL_DEPTH;
}

/** The shallowest level that is rendered as art. Below it the editor falls back to radar. */
export function minArtLevel(max) {
    return Math.max(0, max - (ART_LEVEL_DEPTH - 1));
}

/**
 * Which world tile and pixel a point lands on, in the art pyramid at a level. The numeric half of
 * "the brit-forge marker sits on the forge tile", and what iso.test.js asserts.
 */
export function tileFor(x, y, z, level, max, facetHeight, tileSize = TILE_SIZE) {
    const { ix, iy } = worldToIso(x, y, z);
    const canvasX = ix - originX(facetHeight);
    const canvasY = iy - originY();
    const span = tileSize * divisor(level, max);

    return {
        canvasX,
        canvasY,
        tileX: Math.floor(canvasX / span),
        tileY: Math.floor(canvasY / span),
        pixelX: Math.floor((canvasX % span) / divisor(level, max)),
        pixelY: Math.floor((canvasY % span) / divisor(level, max))
    };
}

/**
 * Which tile of the art pyramid a canvas pixel falls in, and where inside it - the other half of
 * `tileFor`, and the first step of reading a pick map.
 *
 * This is what makes the pick map a lookup rather than an inverse. A FACET-GLOBAL CANVAS PIXEL IS
 * THE SAME NUMBER AT EVERY ZOOM: the camera decides how many screen pixels one of them is worth,
 * but not which one the cursor is over. So the 1:1 pick map answers a click at 1:2 and at 1:8 just
 * as exactly, and there is no second sidecar and no decimation - which matters, because world
 * coordinates cannot be box-averaged the way colour can.
 *
 * Floored rather than truncated, because a cursor dragged off the north or west edge of the facet
 * gives a negative canvas pixel, and `%` in JavaScript would put it in the wrong tile rather than
 * outside the pyramid where it belongs.
 */
export function canvasToTile(canvasX, canvasY, level, max, tileSize = TILE_SIZE) {
    const step = divisor(level, max);
    const span = tileSize * step;

    return {
        tileX: Math.floor(canvasX / span),
        tileY: Math.floor(canvasY / span),
        pixelX: Math.floor(floorMod(canvasX, span) / step),
        pixelY: Math.floor(floorMod(canvasY, span) / step)
    };
}

function floorMod(value, span) {
    return ((value % span) + span) % span;
}

/**
 * Paths a world rectangle's four projected corners, and says whether it did.
 *
 * A world rect is a rectangle on screen in radar and a diamond in art, so every place that built
 * one out of `w * view.scale` needs the corners projected instead. Callers keep their existing
 * fillRect/strokeRect when this returns false: that is the same picture in radar, and it is what
 * the drawing tests pin.
 *
 * `groundZ` is an optional `(x, y) => z | null` - where the land is under each corner. Without it
 * the quad sits on the ground plane, which is where it used to sit always, and is about 2.7 tiles
 * uphill of the land anywhere Britain's upper town stands. A corner it answers null for stays at
 * zero, so a zone drawn before its Zs have arrived is on the ground plane for a frame rather than
 * missing.
 *
 * A FUNCTION RATHER THAN FOUR NUMBERS, because the callers walk the corners in different orders -
 * this one is NW, NE, SE, SW and `rectHandles` is NW, NE, SW, SE - and an array would be one
 * transposition away from a zone drawn on one hillside and clicked on another.
 *
 * It takes a ctx, which is the one impure thing in this module - but the alternative is the rule
 * for "what shape is a rect" living in four files again, which is how it went wrong the first time.
 */
export function traceWorldRect(ctx, view, x, y, width, height, groundZ = null) {
    if (!view.isArt) {
        return false;
    }

    const corners = [[x, y], [x + width, y], [x + width, y + height], [x, y + height]];

    ctx.beginPath();

    corners.forEach(([cx, cy], i) => {
        const [sx, sy] = view.toScreen(cx, cy, (groundZ && groundZ(cx, cy)) || 0);
        i === 0 ? ctx.moveTo(sx, sy) : ctx.lineTo(sx, sy);
    });

    ctx.closePath();

    return true;
}
