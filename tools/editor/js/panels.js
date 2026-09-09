// The left column: which sections are open, and how wide it is.
//
// Both are remembered in localStorage, because both are answers to "how do I want to work today"
// rather than state of the data - and losing them on every reload is what made the column feel
// like something happening TO you rather than something you arranged.
//
// Ported in shape from uo-offline's map.html:1487-1505, which does the same two things with the
// same storage-per-id idea. Its MutationObserver is here too, and it earns its place for a
// specific reason spelled out below.
//
// STORAGE IS ALWAYS GUARDED. A private window, cleared site data, or a browser set to block site
// storage makes both the read and the WRITE throw, and an unguarded write in a toggle handler
// would take the whole handler with it - so a section would stop collapsing rather than merely
// stop being remembered.

const OPEN_KEY = (id) => `gg-editor:panel:${id}`;
const WIDTH_KEY = 'gg-editor:sidebar-width';

// The clamp. Below ~200 the layer rows wrap into unreadable stacks; above 40% of the window the
// map stops being the thing you are looking at. DEFAULT matches --sidebar in style.css.
export const MIN_WIDTH = 200;
export const DEFAULT_WIDTH = 260;
export const MAX_FRACTION = 0.4;

function read(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

function write(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        // Storage is unavailable. Nothing is remembered; everything still works.
    }
}

/**
 * Remembers which sections are open, and reopens one that has something to show.
 *
 * The observer is not decoration. Several things in this column un-hide themselves without
 * anybody clicking their section: the bot detail block when a bot is picked on the map, the floor
 * row when the base map switches to art, the reapply button on the banner. Inside a collapsed
 * section all of those appear to do nothing at all - the state changed, the pixels did not - and
 * that is indistinguishable from the feature being broken. So a child that stops being hidden
 * pops its section open.
 */
export function initSections(root) {
    const sections = [...root.querySelectorAll('details.grp')];

    for (const section of sections) {
        const stored = read(OPEN_KEY(section.id));

        if (stored !== null) {
            section.open = stored === '1';
        }

        section.addEventListener('toggle', () => write(OPEN_KEY(section.id), section.open ? '1' : '0'));
    }

    // `hidden` is an attribute, and everything in this editor hides with it rather than with a
    // display rule (see the [hidden] comment in style.css), so watching the attribute catches
    // every case without knowing which elements they are.
    new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            const element = mutation.target;

            if (element.nodeType !== 1 || element.hidden) {
                continue;
            }

            const section = element.closest('details.grp');

            if (section && !section.open) {
                section.open = true;
            }
        }
    }).observe(root, { attributes: true, attributeFilter: ['hidden'], subtree: true });

    return sections;
}

/** Clamps a width to the column's limits, against the window as it is now. */
export function clampWidth(width) {
    const max = Math.max(MIN_WIDTH, Math.round(window.innerWidth * MAX_FRACTION));

    return Math.min(max, Math.max(MIN_WIDTH, Math.round(width)));
}

function applyWidth(width) {
    document.documentElement.style.setProperty('--sidebar', `${width}px`);
}

/**
 * The drag handle between the column and the map.
 *
 * Pointer events with capture rather than mousemove on the document: capture means the drag
 * survives the pointer crossing the canvas, which owns its own pointer handlers and would
 * otherwise start panning the map halfway through a resize.
 *
 * Nothing tells the canvas about the new size. It does not need telling - render() calls
 * view.resize() every frame, and the ResizeObserver on the canvas requests a frame whenever its
 * box changes. The column is a flex item, so a width change moves that box.
 */
export function initResize(handle) {
    const stored = Number(read(WIDTH_KEY));

    applyWidth(clampWidth(Number.isFinite(stored) && stored > 0 ? stored : DEFAULT_WIDTH));

    let dragging = false;

    handle.addEventListener('pointerdown', (event) => {
        // Left button only: a right-click here should reach the context menu, not start a drag
        // that never gets a matching pointerup.
        if (event.button !== 0) {
            return;
        }

        dragging = true;
        handle.setPointerCapture(event.pointerId);
        handle.classList.add('dragging');
        event.preventDefault();
    });

    handle.addEventListener('pointermove', (event) => {
        if (!dragging) {
            return;
        }

        // From the viewport edge, not from a delta: a delta accumulates the clamp's own error, so
        // dragging past the limit and back would leave the column offset by however far past it
        // went.
        applyWidth(clampWidth(event.clientX));
    });

    const end = (event) => {
        if (!dragging) {
            return;
        }

        dragging = false;
        handle.classList.remove('dragging');

        try {
            handle.releasePointerCapture(event.pointerId);
        } catch {
            // Already released - the pointer left the window, or the capture was never taken.
        }

        write(WIDTH_KEY, String(currentWidth()));
    };

    handle.addEventListener('pointerup', end);
    handle.addEventListener('pointercancel', end);

    handle.addEventListener('dblclick', () => {
        applyWidth(DEFAULT_WIDTH);
        write(WIDTH_KEY, String(DEFAULT_WIDTH));
    });

    // A window narrowed below what the stored width allows would leave the column wider than the
    // cap it is supposed to obey. Re-clamped rather than re-stored: the width the author chose is
    // kept, and it comes back when the window is widened again.
    window.addEventListener('resize', () => applyWidth(clampWidth(currentWidth())));
}

function currentWidth() {
    const value = getComputedStyle(document.documentElement).getPropertyValue('--sidebar');

    return clampWidth(parseFloat(value) || DEFAULT_WIDTH);
}
