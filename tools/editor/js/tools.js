// Creating records: the click-by-click tools, and the form that finishes each one.
//
// A tool collects geometry on the canvas, then a modal asks for the fields the geometry cannot
// supply. Nothing is validated for real here - validate.js previews and the shard decides - but
// the pickers are populated from the shapes already loaded, so a valid choice is the easy one.
//
// WHAT A TOOL IS: a data record, not an object with methods. `kind` names one of six behaviours
// and the state machine for all six lives in app.js. Adding a tool is a table entry; adding a new
// KIND is a change in three places, which is the right amount of friction for the difference.
//
// The ModernUO original's `route` and `point-then-route` kinds are gone rather than ported. They
// collected free polylines, and there is nothing in this schema to write one to: an edge's line is
// its two endpoints and a route's is its waypoint list. Their replacements collect ids instead.

const dom = {};

let resolveForm = null;

/**
 * Binds the modal.
 *
 * Called from boot() rather than run at module scope, which is how the ModernUO original did it -
 * and why importing that file threw until the modal existed. A module that must not be imported
 * before the DOM is ready stays a trap even once the DOM is ready.
 */
export function initTools() {
    dom.modal = document.getElementById('modal');
    dom.form = document.getElementById('modal-form');
    dom.title = document.getElementById('modal-title');
    dom.fields = document.getElementById('modal-fields');
    dom.error = document.getElementById('modal-error');
    dom.cancel = document.getElementById('modal-cancel');
    dom.ok = document.getElementById('modal-ok');

    dom.form.addEventListener('submit', (event) => {
        event.preventDefault();

        const values = {};

        for (const input of dom.fields.querySelectorAll('[name]')) {
            values[input.name] = input.type === 'checkbox' ? input.checked : input.value.trim();
        }

        for (const input of dom.fields.querySelectorAll('[required]')) {
            if (!values[input.name]) {
                dom.error.textContent = `${input.dataset.label} is required.`;
                input.focus();
                return;
            }
        }

        finish(values);
    });

    dom.cancel.addEventListener('click', () => finish(null));

    // Esc inside the modal must close the modal, not fall through to the canvas and deselect.
    dom.modal.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            event.stopPropagation();
            finish(null);
        }
    });
}

export const TOOLS = {
    waypoint: {
        layer: 'nav',
        label: 'Waypoint',
        kind: 'point',
        autoId: true,
        hint: 'Click where the waypoint goes. Esc cancels.',
        title: 'New waypoint',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'tags', label: 'Tags (space separated)', value: 'town road' },
            { key: 'arrivalRange', label: 'Arrival range (0 = walker default)', value: '0' }
        ]
    },

    destination: {
        layer: 'nav-destinations',
        label: 'Destination',
        kind: 'point',
        autoId: true,
        hint: 'Click where the destination is. Esc cancels.',
        title: 'New destination',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'name', label: 'Display name', required: true },
            { key: 'type', label: 'Type', required: true, value: 'shop' },
            { key: 'tags', label: 'Tags' },
            { key: 'waypoints', label: 'Approach waypoints', list: 'waypoint-list' }
        ]
    },

    arrival: {
        layer: 'nav-arrivals',
        label: 'Arrival',
        // Two phases: whose arrival it is, then where. An arrival with no destination is not a
        // thing the schema can express, so the destination is collected first rather than typed.
        kind: 'owner-then-point',
        picks: 'nav-destinations',
        hint: 'Click the destination this arrival belongs to. Esc cancels.',
        hint2: 'Now click a tile someone could stand on. Esc cancels.',
        title: 'New arrival point',
        fields: [
            { key: 'exclusive', label: 'Exclusive (a guard post - nobody else stands here)', checkbox: true },
            { key: 'waypoints', label: 'Approach waypoints', list: 'waypoint-list' }
        ]
    },

    edge: {
        layer: 'nav-edges',
        label: 'Edge',
        // No form at all: an edge is entirely its two ends, and a modal asking nothing would be
        // two clicks of ceremony for no information.
        kind: 'pair',
        picks: 'nav',
        hint: 'Click the first waypoint. Esc cancels.',
        hint2: 'Now click the second. Esc cancels.',
        title: 'New edge',
        fields: []
    },

    route: {
        layer: 'nav-routes',
        label: 'Route',
        kind: 'chain',
        picks: 'nav',
        min: 2,
        hint: 'Click each waypoint in order. Enter finishes, Esc cancels.',
        title: 'New route',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'mode', label: 'Mode', options: ['cycle', 'oneway', 'pingpong'] }
        ]
    },

    navzone: {
        layer: 'nav-zones',
        label: 'Nav zone',
        kind: 'rect',
        autoId: true,
        hint: 'Drag a rectangle for the zone. Esc cancels.',
        title: 'New nav zone',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'tags', label: 'Tags (the first one names auto-generated ids here)', value: 'town' }
        ]
    },

    restricted: {
        layer: 'restricted',
        label: 'Restricted zone',
        kind: 'rect',
        hint: 'Drag a rectangle for the restricted zone. Esc cancels.',
        title: 'New restricted zone',
        fields: [{ key: 'name', label: 'Name', required: true }]
    }
};

function finish(values) {
    dom.modal.hidden = true;

    const resolve = resolveForm;

    resolveForm = null;
    resolve?.(values);
}

/**
 * Opens the modal and resolves with the field values, or null if cancelled.
 *
 * Takes anything with a title and a list of fields, so it serves both the create tools and one-off
 * prompts like "go to coordinate" without a second dialog implementation.
 */
export function askFor(tool, defaults) {
    dom.title.textContent = tool.title;
    dom.ok.textContent = tool.submit || 'Create';
    dom.error.textContent = '';
    dom.fields.innerHTML = '';

    for (const field of tool.fields) {
        const label = document.createElement('label');
        const caption = document.createElement('span');

        caption.textContent = field.label;

        let input;

        if (field.options) {
            input = document.createElement('select');

            for (const option of field.options) {
                const element = document.createElement('option');

                element.value = option;
                element.textContent = option;
                input.append(element);
            }
        } else {
            input = document.createElement('input');
            input.type = field.checkbox ? 'checkbox' : 'text';
            input.autocomplete = 'off';

            if (field.list) {
                input.setAttribute('list', field.list);
            }
        }

        input.name = field.key;
        input.dataset.label = field.label;

        const preset = defaults && field.key in defaults ? defaults[field.key] : field.value;

        if (preset !== undefined && preset !== null) {
            if (field.checkbox) {
                input.checked = preset === true || preset === 'true';
            } else {
                input.value = String(preset);
            }
        }

        if (field.required) {
            input.required = true;
        }

        label.append(caption, input);
        dom.fields.append(label);
    }

    dom.modal.hidden = false;
    dom.fields.querySelector('[name]')?.focus();

    return new Promise((resolve) => {
        resolveForm = resolve;
    });
}

export function showModalError(message) {
    dom.error.textContent = message;
}

/** Fills the id pickers from the shapes already loaded. */
export function fillLists(shapes) {
    const of = (prefix) => shapes
        .filter((shape) => shape.id.startsWith(prefix + ':'))
        .map((shape) => shape.id.slice(prefix.length + 1));

    const waypoints = of('wp');
    const destinations = of('dest');

    fill('waypoint-list', waypoints);
    fill('destination-list', destinations);
    fill('route-list', of('route'));

    // Anything a daily-life field may name: Nav.TryRoute takes either, and so does a shop or home.
    fill('navid-list', [...destinations, ...waypoints]);
}

function fill(id, values) {
    const list = document.getElementById(id);

    if (!list) {
        return;
    }

    list.innerHTML = '';

    for (const value of values) {
        const option = document.createElement('option');

        option.value = value;
        list.append(option);
    }
}
