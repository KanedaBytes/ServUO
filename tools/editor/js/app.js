// The read-only editor.
//
// Step 5a shows every data layer and the live entities; it does not edit anything - that is 5b.
// view.js, shapes.js and overlays-style drawing are reused from the ModernUO editor unchanged,
// and this file is the part that was rewritten, because the original's whole spine is the edit
// cycle: dirty tracking, per-shape baselines, an undo stack, a save that patches and then
// reloads, and a banner for the case where the write succeeded but the reload was rejected.
//
// None of that has anything to hold onto yet. Carrying it in now would mean several hundred
// lines calling API methods the bridge does not have, which is worse than not having them: dead
// code that looks live is how you end up debugging a feature that was never wired. When editing
// arrives in 5b, that machinery is worth porting back deliberately - the comment in the original
// style.css records that the banner exists because a save once failed, reverted the view, and
// said so only in a status line that cleared itself after six seconds.

import { api } from './api.js';
import { View, DEFAULT_FACET, BRITAIN } from './view.js';
import { LAYERS, LAYER_ORDER, draw as drawShapes, drawEntities } from './shapes.js';
import * as coverage from './coverage.js';

const ENTITY_POLL_MS = 2000;
const HEALTH_POLL_MS = 15000;

// Must match Custom.NavHopMaxTiles. The coverage bands and the edge colouring are both meaningless
// if this disagrees with the shard.
const HOP_CAP = 12;

const state = {
    shapes: [],
    entities: [],
    health: null,
    live: null,
    facet: null,
    visible: new Set(LAYER_ORDER.filter((layer) => layer !== 'nav-edges')),
    coverageVisible: false,
    selected: null,
    filter: ''
};

const canvas = document.getElementById('map');
const view = new View(canvas);

// Exported for browser automation, as in the original - it is how you assert the port works
// without clicking anything.
window.__editor = { state, view, coverage };

async function boot() {
    try {
        const status = await api.status();

        const facet = status.facets.find((f) => f.name === DEFAULT_FACET) || status.facets[0];

        state.facet = facet;
        view.setFacet(facet);
        view.goTo(BRITAIN.x, BRITAIN.y, 2);

        setStatus(
            `${status.waypoints} waypoints, ${status.destinations} destinations.`,
            status.navigationLoaded ? 'ok' : 'error'
        );
    } catch (error) {
        setStatus(`Cannot reach the bridge: ${error.message}`, 'error');
        return;
    }

    buildLayerList();

    await refreshShapes();

    pollEntities();
    pollHealth();

    wireInput();
    requestAnimationFrame(render);
}

async function refreshShapes() {
    try {
        const response = await api.shapes();

        state.shapes = response.shapes;

        // The coverage grid is derived from the waypoints, so it has to be rebuilt whenever they
        // change - a stale grid is worse than none, because it looks authoritative.
        coverage.invalidate();

        if (state.coverageVisible) {
            computeCoverage();
        }

        updateCounts();
    } catch (error) {
        setStatus(`Could not load shapes: ${error.message}`, 'error');
    }
}

function computeCoverage() {
    const waypoints = state.shapes
        .filter((s) => s.layer === 'nav' && s.map === state.facet.name)
        .map((s) => ({ x: s.points[0][0], y: s.points[0][1] }));

    coverage.compute(waypoints, HOP_CAP);
    updateCounts();
}

// A setTimeout chain rather than setInterval, so a slow response cannot stack polls on top of
// each other.
async function pollEntities() {
    if (state.visible.has('entities')) {
        try {
            const response = await api.entities();

            state.entities = response.entities || [];
            state.live = response;

            updateCounts();
        } catch {
            // A bridge that is briefly unreachable is not worth a banner; the live count going
            // stale says it well enough.
        }
    }

    setTimeout(pollEntities, ENTITY_POLL_MS);
}

async function pollHealth() {
    try {
        state.health = await api.health();
        renderHealth();
    } catch {
        // Same reasoning as entities.
    }

    setTimeout(pollHealth, HEALTH_POLL_MS);
}

function buildLayerList() {
    const list = document.getElementById('layers');

    list.innerHTML = '';

    for (const layer of LAYER_ORDER) {
        list.appendChild(layerRow(layer, LAYERS[layer].label, LAYERS[layer].color, state.visible.has(layer), (on) => {
            if (on) {
                state.visible.add(layer);
            } else {
                state.visible.delete(layer);
            }

            updateCounts();
        }));
    }

    list.appendChild(layerRow('coverage', 'Coverage gaps', '#ff2800', false, (on) => {
        state.coverageVisible = on;

        // Computed on first show rather than on load: it is O(cells x waypoints) and most
        // sessions never ask for it.
        if (on && !coverage.isComputed()) {
            computeCoverage();
        }

        updateCounts();
    }));
}

function layerRow(key, label, color, checked, onChange) {
    const row = document.createElement('li');

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = checked;
    box.id = `layer-${key}`;
    box.addEventListener('change', () => onChange(box.checked));

    const swatch = document.createElement('span');
    swatch.className = 'swatch';
    swatch.style.background = color;

    const text = document.createElement('label');
    text.htmlFor = box.id;
    text.textContent = label;

    const count = document.createElement('span');
    count.className = 'count';
    count.dataset.layer = key;

    row.append(box, swatch, text, count);

    return row;
}

function updateCounts() {
    for (const element of document.querySelectorAll('.count')) {
        const layer = element.dataset.layer;

        if (layer === 'coverage') {
            element.textContent = coverage.isComputed() ? String(coverage.gapCount()) : '-';
            continue;
        }

        if (layer === 'entities') {
            element.textContent = String(state.entities.length);
            continue;
        }

        element.textContent = String(
            state.shapes.filter((s) => s.layer === layer && s.map === state.facet.name).length
        );
    }

    const live = document.getElementById('live-status');

    if (live) {
        live.textContent = state.live && state.live.running
            ? `live: ${state.live.count ?? state.entities.length} @ seq ${state.live.sequence}`
            : 'live: off';
    }
}

function renderHealth() {
    const panel = document.getElementById('health');

    if (!panel || !state.health) {
        return;
    }

    panel.innerHTML = '';

    for (const check of state.health.checks || []) {
        const row = document.createElement('li');
        row.className = `health ${check.status.toLowerCase()}`;
        row.title = check.detail;
        row.textContent = `${check.status.toUpperCase()} ${check.name}`;
        panel.appendChild(row);
    }
}

function matchingShapes() {
    if (!state.filter) {
        return null;
    }

    const needle = state.filter.toLowerCase();

    return new Set(
        state.shapes.filter(
            (s) => (s.label || '').toLowerCase().includes(needle) || s.id.toLowerCase().includes(needle)
        )
    );
}

function render() {
    view.resize();
    view.drawMap();

    const ctx = canvas.getContext('2d');

    // Under everything: it is a background condition, not a thing on the map.
    if (state.coverageVisible) {
        coverage.draw(ctx, view);
    }

    drawShapes(ctx, view, state.shapes, state.visible, state.selected, null, matchingShapes());

    if (state.visible.has('entities')) {
        drawEntities(ctx, view, state.entities);
    }

    requestAnimationFrame(render);
}

function wireInput() {
    let dragging = false;
    let lastX = 0;
    let lastY = 0;

    canvas.addEventListener('mousedown', (e) => {
        dragging = true;
        lastX = e.clientX;
        lastY = e.clientY;
    });

    window.addEventListener('mouseup', () => {
        dragging = false;
    });

    canvas.addEventListener('mousemove', (e) => {
        const [wx, wy] = view.toWorld(e.offsetX, e.offsetY);

        const coords = document.getElementById('coords');

        if (coords) {
            coords.textContent = `${Math.floor(wx)}, ${Math.floor(wy)}`;
        }

        if (dragging) {
            view.panBy(e.clientX - lastX, e.clientY - lastY);
            lastX = e.clientX;
            lastY = e.clientY;
        }
    });

    canvas.addEventListener('wheel', (e) => {
        e.preventDefault();
        view.zoomAt(e.offsetX, e.offsetY, e.deltaY < 0 ? 1.2 : 1 / 1.2);
    }, { passive: false });

    const filter = document.getElementById('filter');

    if (filter) {
        filter.addEventListener('input', () => {
            state.filter = filter.value.trim();
        });
    }

    const britain = document.getElementById('go-britain');

    if (britain) {
        britain.addEventListener('click', () => view.goTo(BRITAIN.x, BRITAIN.y, 2));
    }

    wireRequest('reload-nav', 'nav-reload', '', 'Navigation reloaded');
    wireRequest('reload-dailylife', 'dailylife-reload', '', 'Daily life reloaded');
    wireRequest('live-on', 'livemap-on', '2', 'Live map on');
    wireRequest('live-off', 'livemap-off', '', 'Live map off');
}

/**
 * Wires a button to a request token.
 *
 * The shard polls once a second, so this waits for the ack rather than reporting success the
 * moment the file is written - "reloaded" appearing before anything reloaded would be a lie, and
 * a failing reload would be invisible.
 */
function wireRequest(buttonId, requestName, body, successText) {
    const button = document.getElementById(buttonId);

    if (!button) {
        return;
    }

    button.addEventListener('click', async () => {
        button.disabled = true;
        setStatus(`Asking the shard: ${requestName}...`, 'ok');

        try {
            await api.request(requestName, body);

            const ack = await api.awaitAck(requestName);

            if (ack.ok) {
                setStatus(`${successText}: ${ack.message}`, 'ok');
                await refreshShapes();
            } else {
                setStatus(`${requestName} failed: ${ack.message}`, 'error');
            }
        } catch (error) {
            setStatus(`${requestName} failed: ${error.message}`, 'error');
        } finally {
            button.disabled = false;
        }
    });
}

let statusTimer = null;

function setStatus(message, kind) {
    const element = document.getElementById('status');

    if (!element) {
        return;
    }

    element.textContent = message;
    element.className = kind || '';

    if (statusTimer) {
        clearTimeout(statusTimer);
    }

    statusTimer = setTimeout(() => {
        element.textContent = '';
        element.className = '';
    }, 8000);
}

boot();
