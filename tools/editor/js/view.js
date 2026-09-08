// The camera, and the tile pyramids underneath it.
//
// World coordinates are game tiles. Screen coordinates are CSS pixels. `scale` is screen pixels
// per game tile in BOTH projections, which is the whole reason the art view was a small change:
// every labelAt threshold, cull margin and hit-test slack elsewhere in the editor keeps the
// meaning it already had.
//
// RADAR is one flat colour per tile from radarcol.mul, a facet-wide pyramid rendered once. scale 1
// is its deepest level - one pixel per tile - and anything above that is nearest-neighbour
// magnification of the same images. Rendering deeper levels would cost four times the disk for no
// extra detail, because there is no more radar data.
//
// ART is the real client art drawn isometrically, twenty-two iso pixels per world tile per axis,
// so 1:1 art is scale 22. It cannot be a facet-wide pyramid - Trammel's iso canvas is 61
// gigapixels per floor - so its tiles are rendered by the bridge on demand and cached forever, and
// only the deepest few levels exist at all. Below those the art view draws radar, which is what
// radar is good at.
//
// RADAR IS ALWAYS DRAWN UNDER ART. A tile that has not been rendered yet, one off the edge of the
// map, and one the renderer refused all look the same to this file: nothing is drawn and the radar
// shows through. That is why there is no placeholder protocol here.
//
// ART IS TWO LAYERS. `map` is the client's own world - land and statics - which never changes.
// `items` is the shard's furniture: forges, anvils, signs, doors, benches, every [Decorate addon,
// none of which exist in the client files at all. They are separate because they expire at
// different rates - a re-decorate must not invalidate a map cache that costs minutes to rebuild -
// and the item tiles are transparent everywhere the furniture is not, including everywhere the
// furniture is hidden behind something. The renderer works that out; this just draws one over the
// other.

import * as iso from './iso.js';

const TILE_SIZE = 256;

/** Radar magnifies a flat colour, so past 16 there is nothing more to see. */
const MAX_RADAR_SCALE = 16;

/** Art tops out a little past 1:1 (scale 22), which is where a tile is 44px across. */
const MAX_ART_SCALE = 32;

/*
 * Where to open a facet. Britannia is 7168x4096 and almost entirely empty of anything this editor
 * edits, so opening on the geometric centre lands in open sea a long way from Britain. Per
 * SHARD.md, Trammel is the primary facet and every configured shape is in Britain; Felucca shares
 * the same terrain, so the same point is right there too. A facet with no entry opens fitted to
 * the window.
 */
export const BRITAIN = { x: 1475, y: 1645 };

const FACET_FOCUS = {
    Trammel: { x: BRITAIN.x, y: BRITAIN.y, scale: 2 },
    Felucca: { x: BRITAIN.x, y: BRITAIN.y, scale: 2 }
};

export const DEFAULT_FACET = 'Trammel';

export class View {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.facet = null;
        this.centerX = 0;
        this.centerY = 0;
        this.scale = 1;
        this.images = new Map();
        this.onTileLoaded = null;

        // 'radar' or 'art'. Art needs `this.art` - what /api/artinfo said the renderer is - so it
        // can address a tile; without that it stays radar however it is set.
        this.projection = 'radar';
        this.floor = 'all';
        this.art = null;

        // What the shard's world-item snapshot is, or null when there is none. Null is a normal
        // state - the shard may not be running, or may never have been asked - and the art view
        // works without it, just without furniture.
        this.items = null;

        // Art tiles are rendered on request, so a failure can be a timeout rather than a fact.
        // Radar's onerror deliberately caches the miss forever; art's has to be able to try again,
        // or one slow tile is blank for the rest of the session. Bounded, so a tile the renderer
        // genuinely cannot draw is not asked for on every frame.
        this.artFailures = new Map();
    }

    setFacet(facet) {
        this.facet = facet;
        this.maxZoom = maxZoomFor(facet.width, facet.height);

        const focus = FACET_FOCUS[facet.name];

        // Scale first: clampCenter is bounds-only, but minScale depends on the canvas box, so this
        // has to run after the canvas has been laid out.
        this.scale = Math.max(focus ? focus.scale : fitScale(facet, this.canvas), this.minScale());
        this.centerX = focus ? focus.x : facet.width / 2;
        this.centerY = focus ? focus.y : facet.height / 2;
        this.clampCenter();
    }

    /** What the renderer told the bridge about itself, or null if there is no renderer. */
    setArtInfo(info) {
        this.art = info && info.version ? info : null;
        this.items = (info && info.items) || null;

        if (!this.art && this.projection === 'art') {
            this.projection = 'radar';
        }
    }

    /** A new world-item snapshot, which is a new layer id and so a whole new set of tile URLs. */
    setItems(items) {
        this.items = items || null;
    }

    setProjection(projection) {
        this.projection = projection === 'art' && this.art ? 'art' : 'radar';
        this.scale = Math.min(this.scale, this.maxScale());
        this.clampCenter();
    }

    setFloor(floor) {
        this.floor = iso.FLOORS.indexOf(floor) >= 0 ? floor : 'all';
    }

    get isArt() {
        return this.projection === 'art' && this.art !== null;
    }

    maxScale() {
        return this.isArt ? MAX_ART_SCALE : MAX_RADAR_SCALE;
    }

    // ---- radar addressing ----------------------------------------------------------------------

    /** Level whose pixels are closest to the current scale, clamped to what was rendered. */
    get zoom() {
        const wanted = this.maxZoom + Math.floor(Math.log2(this.scale));
        return Math.max(0, Math.min(this.maxZoom, wanted));
    }

    /** Game tiles covered by one pyramid tile at the current zoom. */
    get worldPerTile() {
        return TILE_SIZE * 2 ** (this.maxZoom - this.zoom);
    }

    // ---- art addressing ------------------------------------------------------------------------

    get artMaxLevel() {
        return this.art ? this.art.maxLevel : 0;
    }

    /**
     * The art level closest to the current scale. 1:1 is scale 22, and each level out halves it,
     * so the level is maxLevel + floor(log2(scale / 22)) - the same "prefer the coarser tile and
     * magnify" rule the radar zoom uses.
     */
    get artLevel() {
        const max = this.artMaxLevel;
        const wanted = max + Math.floor(Math.log2(this.scale / iso.HALF_WIDTH));

        return Math.max(iso.minArtLevel(max), Math.min(max, wanted));
    }

    /** True when the current scale is inside the range that has art at all. */
    get artAvailable() {
        if (!this.isArt) {
            return false;
        }

        const max = this.artMaxLevel;
        return max + Math.floor(Math.log2(this.scale / iso.HALF_WIDTH)) >= iso.minArtLevel(max);
    }

    /** The floor actually drawn: coarse levels carry only the composite. */
    get effectiveFloor() {
        return iso.hasFloors(this.artLevel, this.artMaxLevel) ? this.floor : 'all';
    }

    /** Screen pixels per iso pixel. */
    get isoScale() {
        return this.scale / iso.HALF_WIDTH;
    }

    isoToScreen(ix, iy) {
        const s = this.isoScale;
        const centre = iso.worldToIso(this.centerX, this.centerY, 0);

        return [
            (ix - centre.ix) * s + this.canvas.clientWidth / 2,
            (iy - centre.iy) * s + this.canvas.clientHeight / 2
        ];
    }

    // ---- the projection ------------------------------------------------------------------------

    /**
     * Z is honoured in the art projection and ignored in radar, exactly as the two maps do. A
     * caller with no Z passes none and gets the ground plane.
     */
    toScreen(x, y, z = 0) {
        if (this.isArt) {
            const { ix, iy } = iso.worldToIso(x, y, z);
            return this.isoToScreen(ix, iy);
        }

        return [
            (x - this.centerX) * this.scale + this.canvas.clientWidth / 2,
            (y - this.centerY) * this.scale + this.canvas.clientHeight / 2
        ];
    }

    /**
     * The inverse. In art this ANSWERS FOR THE GROUND PLANE and is off by z*4/44 tiles for
     * anything standing above it - about 2.7 tiles in Britain. Good enough to pan and zoom with,
     * which is all it is used for in art view; picking goes through projected screen anchors
     * instead, and placing and dragging are disabled.
     */
    toWorld(px, py) {
        if (this.isArt) {
            const s = this.isoScale;
            const centre = iso.worldToIso(this.centerX, this.centerY, 0);
            const ix = (px - this.canvas.clientWidth / 2) / s + centre.ix;
            const iy = (py - this.canvas.clientHeight / 2) / s + centre.iy;
            const { x, y } = iso.isoToWorld(ix, iy, 0);

            return [x, y];
        }

        return [
            (px - this.canvas.clientWidth / 2) / this.scale + this.centerX,
            (py - this.canvas.clientHeight / 2) / this.scale + this.centerY
        ];
    }

    /** Zooms about a screen point, so the world tile under the cursor stays under the cursor. */
    zoomAt(px, py, factor) {
        const [worldX, worldY] = this.toWorld(px, py);

        this.scale = clamp(this.scale * factor, this.minScale(), this.maxScale());

        const [afterX, afterY] = this.toWorld(px, py);

        this.centerX += worldX - afterX;
        this.centerY += worldY - afterY;
        this.clampCenter();
    }

    /**
     * Converts the screen delta through the projection rather than dividing by scale, because in
     * art a drag to the right is a move along both world axes at once.
     */
    panBy(dx, dy) {
        const [fromX, fromY] = this.toWorld(0, 0);
        const [toX, toY] = this.toWorld(dx, dy);

        this.centerX -= toX - fromX;
        this.centerY -= toY - fromY;
        this.clampCenter();
    }

    centerOn(x, y) {
        this.centerX = x;
        this.centerY = y;
        this.clampCenter();
    }

    /** Jump to a place, optionally changing zoom. */
    goTo(x, y, scale = null) {
        if (scale !== null) {
            this.scale = Math.max(Math.min(scale, this.maxScale()), this.minScale());
        }

        this.centerOn(x, y);
    }

    /** Zoom out until the whole facet is on screen. */
    fitAll() {
        this.scale = this.minScale();
        this.centerOn(this.facet.width / 2, this.facet.height / 2);
    }

    minScale() {
        // Never zoom out past the whole facet fitting on screen. In art the facet is a diamond
        // (width + height) tiles across on both screen axes, not a width x height rectangle.
        const across = this.isArt
            ? { x: this.facet.width + this.facet.height, y: this.facet.width + this.facet.height }
            : { x: this.facet.width, y: this.facet.height };

        return Math.min(
            this.canvas.clientWidth / across.x,
            this.canvas.clientHeight / across.y
        ) * 0.9;
    }

    clampCenter() {
        this.centerX = clamp(this.centerX, 0, this.facet.width);
        this.centerY = clamp(this.centerY, 0, this.facet.height);
    }

    /**
     * Sizes the drawing buffer from the element's CSS box, scaled by devicePixelRatio so the map is
     * sharp on a high-DPI display rather than a stretched low-res image. The transform then lets
     * every other coordinate in this file stay in CSS pixels.
     */
    resize() {
        const ratio = window.devicePixelRatio || 1;
        const width = this.canvas.clientWidth;
        const height = this.canvas.clientHeight;

        if (width === 0 || height === 0) {
            // Laid out at zero (still hidden, for instance). Writing a zero-sized buffer would
            // throw off every later scale calculation, so leave the last good one alone.
            return false;
        }

        this.canvas.width = Math.round(width * ratio);
        this.canvas.height = Math.round(height * ratio);
        this.ctx.setTransform(ratio, 0, 0, ratio, 0, 0);

        return true;
    }

    drawMap() {
        const ctx = this.ctx;

        ctx.imageSmoothingEnabled = false;
        ctx.fillStyle = '#0a0c10';
        ctx.fillRect(0, 0, this.canvas.clientWidth, this.canvas.clientHeight);

        this.drawRadarMap();

        if (this.isArt && this.artAvailable) {
            this.drawArtMap('map');

            // The shard's furniture, over the client's world. Skipped entirely without a snapshot
            // rather than drawn as a layer of empty tiles.
            if (this.items) {
                this.drawArtMap('items');
            }
        }
    }

    /**
     * The radar pyramid. In art this is the underlay, drawn through a canvas matrix rather than a
     * second tile loop: at z=0 the isometric projection is linear, so the same world-space loop
     * places a radar tile as a rotated square with no other change.
     */
    drawRadarMap() {
        const ctx = this.ctx;
        const width = this.canvas.clientWidth;
        const height = this.canvas.clientHeight;

        const span = this.worldPerTile;

        const bounds = this.worldBounds();
        const firstX = Math.max(0, Math.floor(bounds.left / span));
        const firstY = Math.max(0, Math.floor(bounds.top / span));
        const lastX = Math.floor(Math.min(bounds.right, this.facet.width - 1) / span);
        const lastY = Math.floor(Math.min(bounds.bottom, this.facet.height - 1) / span);

        const art = this.isArt;

        if (art) {
            ctx.save();

            const ratio = window.devicePixelRatio || 1;
            const [a, b, c, d] = iso.isoMatrix(this.scale);
            const centre = iso.worldToIso(this.centerX, this.centerY, 0);
            const s = this.isoScale;
            const e = -centre.ix * s + width / 2;
            const f = -centre.iy * s + height / 2;

            ctx.setTransform(a * ratio, b * ratio, c * ratio, d * ratio, e * ratio, f * ratio);
        }

        // Draw a hair wider than the tile: adjacent tiles otherwise show a seam at fractional
        // scales, where two neighbours round to screen positions a pixel apart. Under the art
        // matrix the units are world tiles, so the fudge is one screen pixel's worth of them.
        const size = art ? span + 1 / this.scale : span * this.scale + 1;

        for (let tx = firstX; tx <= lastX; tx++) {
            for (let ty = firstY; ty <= lastY; ty++) {
                const image = this.tile(tx, ty);

                if (!image || !image.complete || image.naturalWidth === 0) {
                    continue;
                }

                if (art) {
                    ctx.drawImage(image, tx * span, ty * span, size, size);
                } else {
                    const [sx, sy] = this.toScreen(tx * span, ty * span);
                    ctx.drawImage(image, sx, sy, size, size);
                }
            }
        }

        if (art) {
            ctx.restore();
        }
    }

    /** The isometric art, in canvas-pixel space rather than world space. */
    drawArtMap(layer) {
        const ctx = this.ctx;
        const width = this.canvas.clientWidth;
        const height = this.canvas.clientHeight;

        const level = this.artLevel;
        const max = this.artMaxLevel;
        const floor = this.effectiveFloor;
        const span = (this.art.tileSize || TILE_SIZE) * iso.divisor(level, max);
        const size = span * this.isoScale + 1;

        const centre = iso.worldToIso(this.centerX, this.centerY, 0);
        const s = this.isoScale;

        const left = (0 - width / 2) / s + centre.ix - iso.originX(this.facet.height);
        const right = (width - width / 2) / s + centre.ix - iso.originX(this.facet.height);
        const top = (0 - height / 2) / s + centre.iy - iso.originY();
        const bottom = (height - height / 2) / s + centre.iy - iso.originY();

        const firstX = Math.max(0, Math.floor(left / span));
        const firstY = Math.max(0, Math.floor(top / span));
        const lastX = Math.floor(right / span);
        const lastY = Math.floor(bottom / span);

        for (let tx = firstX; tx <= lastX; tx++) {
            for (let ty = firstY; ty <= lastY; ty++) {
                const image = this.artTile(layer, level, floor, tx, ty);

                if (!image || !image.complete || image.naturalWidth === 0) {
                    continue;
                }

                const [sx, sy] = this.isoToScreen(
                    tx * span + iso.originX(this.facet.height),
                    ty * span + iso.originY()
                );

                ctx.drawImage(image, sx, sy, size, size);
            }
        }
    }

    /** The world rectangle on screen. In art the corners of the viewport are not the corners. */
    worldBounds() {
        const width = this.canvas.clientWidth;
        const height = this.canvas.clientHeight;

        const corners = [
            this.toWorld(0, 0),
            this.toWorld(width, 0),
            this.toWorld(0, height),
            this.toWorld(width, height)
        ];

        return {
            left: Math.min(...corners.map(c => c[0])),
            right: Math.max(...corners.map(c => c[0])),
            top: Math.min(...corners.map(c => c[1])),
            bottom: Math.max(...corners.map(c => c[1]))
        };
    }

    tile(tx, ty) {
        const key = `${this.facet.name}/${this.zoom}/${tx}/${ty}`;
        const cached = this.images.get(key);

        if (cached) {
            return cached;
        }

        const image = new Image();
        image.src = `/tiles/${key}.png`;
        image.onload = () => this.onTileLoaded && this.onTileLoaded();

        // A missing tile is normal at the edges; cache the failure so it is not re-fetched forever.
        image.onerror = () => {};

        this.images.set(key, image);

        return image;
    }

    /**
     * An art tile. The request may take a second: the bridge renders a miss before it answers, so
     * the browser's own connection cap is what throttles the queue.
     */
    artTile(layer, level, floor, tx, ty) {
        // The item layer's directory carries the snapshot's id, so a re-decorate changes every URL
        // and the browser's own cache turns over with it - there is nothing to invalidate by hand.
        const name = layer === 'map' ? 'map' : `items-${this.items.id}`;
        const key = `iso/${this.facet.name}/v${this.art.version}/${name}/${floor}/${level}/${tx}/${ty}`;
        const cached = this.images.get(key);

        if (cached) {
            return cached;
        }

        const image = new Image();
        image.src = `/tiles/${key}.png`;
        image.onload = () => {
            this.artFailures.delete(key);
            this.onTileLoaded && this.onTileLoaded();
        };

        // Unlike radar, drop it so a later pan can ask again - a 503 here usually means the render
        // ran past the bridge's timeout, not that the tile does not exist. Bounded, so a tile the
        // renderer truly cannot draw stops being asked for.
        image.onerror = () => {
            const failures = (this.artFailures.get(key) || 0) + 1;
            this.artFailures.set(key, failures);

            if (failures < 3) {
                this.images.delete(key);
            }
        };

        this.images.set(key, image);

        return image;
    }
}

function maxZoomFor(width, height) {
    // Must match TilePyramid.LevelCount exactly, or the editor asks for tiles that were not
    // rendered.
    let levels = 0;

    while (width > TILE_SIZE || height > TILE_SIZE) {
        width = Math.ceil(width / 2);
        height = Math.ceil(height / 2);
        levels++;
    }

    return levels;
}

function fitScale(facet, canvas) {
    return Math.min(
        canvas.clientWidth / facet.width,
        canvas.clientHeight / facet.height
    ) * 0.95;
}

function clamp(value, low, high) {
    return Math.max(low, Math.min(high, value));
}
