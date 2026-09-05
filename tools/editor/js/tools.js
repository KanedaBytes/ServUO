// Creating shapes: the click-by-click tools, and the form that finishes each one.
//
// A tool collects geometry on the canvas, then a modal asks for the fields the geometry cannot
// supply (a name, a vendor type, a creature). Nothing is validated for real here - the server
// resolves every type name and refuses with a reason - but the pickers are populated from
// /api/types so a valid choice is the easy one.

export const TOOLS = {
    zone: {
        layer: 'zones',
        label: 'Zone',
        kind: 'rect',
        hint: 'Drag a rectangle for the restricted zone. Esc cancels.',
        title: 'New restricted zone',
        fields: [{ key: 'name', label: 'Name', required: true }]
    },
    shop: {
        layer: 'dailylife',
        label: 'Shop',
        // Two phases: where the shopkeeper stands, then the walk home that empties the shop at dusk.
        kind: 'point-then-route',
        hint: 'Click where the shopkeeper stands. Esc cancels.',
        hint2: 'Now click the walk home, node by node. Enter finishes, Esc cancels.',
        title: 'New shop',
        fields: [{ key: 'vendor', label: 'Vendor type', required: true, list: 'vendor-list' }]
    },
    watchpost: {
        layer: 'dailylife',
        label: 'Watch post',
        kind: 'point',
        hint: 'Click where the watchman stands. Esc cancels.',
        title: 'New watch post',
        fields: [{ key: 'route', label: 'Route (blank = stands still)', list: 'route-list' }]
    },
    townsfolk: {
        layer: 'dailylife',
        label: 'Townsperson',
        // Not placed on the map: a townsperson starts wherever their route starts.
        kind: 'form',
        title: 'New townsperson',
        fields: [
            { key: 'name', label: 'Name', required: true },
            { key: 'title', label: 'Title' },
            { key: 'route', label: 'Route', required: true, list: 'route-list' },
            { key: 'body', label: 'Body', options: ['random', 'male', 'female'] }
        ]
    },
    route: {
        layer: 'dailylife',
        label: 'Route',
        kind: 'route',
        hint: 'Click each stop. Enter finishes, Esc cancels.',
        title: 'New route',
        fields: [{ key: 'name', label: 'Name', required: true }]
    },
    spawner: {
        layer: 'spawners',
        label: 'Spawner',
        kind: 'point',
        hint: 'Click where the spawner goes. Esc cancels.',
        title: 'New spawner',
        fields: [
            { key: 'creature', label: 'Creature or item type', required: true, list: 'creature-list' },
            { key: 'count', label: 'Count', value: '1' },
            { key: 'homeRange', label: 'Wander range', value: '2' },
            { key: 'name', label: 'Spawner name (optional)' },
            { key: 'file', label: 'Spawn file', value: 'Data/Spawns/custom/trammel/Britain.json' }
        ]
    }
};

const dom = {
    ok: document.getElementById('modal-ok'),
    modal: document.getElementById('modal'),
    form: document.getElementById('modal-form'),
    title: document.getElementById('modal-title'),
    fields: document.getElementById('modal-fields'),
    error: document.getElementById('modal-error'),
    cancel: document.getElementById('modal-cancel')
};

let resolveForm = null;

dom.form.addEventListener('submit', (event) => {
    event.preventDefault();

    const values = {};

    for (const input of dom.fields.querySelectorAll('[name]')) {
        values[input.name] = input.value.trim();
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
export function askFor(tool) {
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
            input.type = 'text';
            input.autocomplete = 'off';

            if (field.list) {
                // A datalist rather than a select: the creature list runs to thousands of entries,
                // and typing a few letters beats scrolling.
                input.setAttribute('list', field.list);
            }
        }

        input.name = field.key;
        input.dataset.label = field.label;

        if (field.value) {
            input.value = field.value;
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

/** Fills the type pickers. Failure is not fatal - the fields still accept free text. */
export async function loadTypeLists(api) {
    try {
        const types = await api.types();

        fill('vendor-list', types.vendors);
        fill('creature-list', types.creatures);

        return types;
    } catch {
        return { vendors: [], creatures: [] };
    }
}

export function fillRouteList(shapes) {
    fill(
        'route-list',
        shapes.filter((s) => s.id.startsWith('route:')).map((s) => s.label)
    );
}

export function fillList(id, values) {
    fill(id, values);
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
