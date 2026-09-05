// The camera, and the tile pyramid underneath it.
//
// World coordinates are game tiles. Screen coordinates are CSS pixels. `scale` is screen pixels
// per game tile, so scale 1 is the deepest rendered zoom - one pixel per tile - and anything above
// that is nearest-neighbour magnification of the same images. Radar colour is one flat colour per
// tile, so magnifying loses nothing; rendering deeper levels would cost four times the disk for no
// extra detail.

const TILE_SIZE = 256;

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

    /** Level whose pixels are closest to the current scale, clamped to what was rendered. */
    get zoom() {
        const wanted = this.maxZoom + Math.floor(Math.log2(this.scale));
        return Math.max(0, Math.min(this.maxZoom, wanted));
    }

    /** Game tiles covered by one pyramid tile at the current zoom. */
    get worldPerTile() {
        return TILE_SIZE * 2 ** (this.maxZoom - this.zoom);
    }

    toScreen(x, y) {
        return [
            (x - this.centerX) * this.scale + this.canvas.clientWidth / 2,
            (y - this.centerY) * this.scale + this.canvas.clientHeight / 2
        ];
    }

    toWorld(px, py) {
        return [
            (px - this.canvas.clientWidth / 2) / this.scale + this.centerX,
            (py - this.canvas.clientHeight / 2) / this.scale + this.centerY
        ];
    }

    /** Zooms about a screen point, so the world tile under the cursor stays under the cursor. */
    zoomAt(px, py, factor) {
        const [worldX, worldY] = this.toWorld(px, py);

        this.scale = clamp(this.scale * factor, this.minScale(), 16);

        const [afterX, afterY] = this.toWorld(px, py);

        this.centerX += worldX - afterX;
        this.centerY += worldY - afterY;
        this.clampCenter();
    }

    panBy(dx, dy) {
        this.centerX -= dx / this.scale;
        this.centerY -= dy / this.scale;
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
            this.scale = Math.max(Math.min(scale, 16), this.minScale());
        }

        this.centerOn(x, y);
    }

    /** Zoom out until the whole facet is on screen. */
    fitAll() {
        this.scale = this.minScale();
        this.centerOn(this.facet.width / 2, this.facet.height / 2);
    }

    minScale() {
        // Never zoom out past the whole facet fitting on screen.
        return Math.min(
            this.canvas.clientWidth / this.facet.width,
            this.canvas.clientHeight / this.facet.height
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
        const width = this.canvas.clientWidth;
        const height = this.canvas.clientHeight;

        ctx.imageSmoothingEnabled = false;
        ctx.fillStyle = '#0a0c10';
        ctx.fillRect(0, 0, width, height);

        const span = this.worldPerTile;
        const [left, top] = this.toWorld(0, 0);
        const [right, bottom] = this.toWorld(width, height);

        const firstX = Math.max(0, Math.floor(left / span));
        const firstY = Math.max(0, Math.floor(top / span));
        const lastX = Math.floor(Math.min(right, this.facet.width - 1) / span);
        const lastY = Math.floor(Math.min(bottom, this.facet.height - 1) / span);

        // Draw a hair wider than the tile: adjacent tiles otherwise show a seam at fractional
        // scales, where two neighbours round to screen positions a pixel apart.
        const size = span * this.scale + 1;

        for (let tx = firstX; tx <= lastX; tx++) {
            for (let ty = firstY; ty <= lastY; ty++) {
                const image = this.tile(tx, ty);

                if (!image || !image.complete || image.naturalWidth === 0) {
                    continue;
                }

                const [sx, sy] = this.toScreen(tx * span, ty * span);
                ctx.drawImage(image, sx, sy, size, size);
            }
        }
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
