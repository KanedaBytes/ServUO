'use strict';

// The transparent PNG the bridge serves for an item tile with nothing in it.
//
// THIS FILE EXISTS BECAUSE THE CONSTANT WAS WRONG. It started as a "well-known 1x1 transparent PNG"
// pasted from memory, and it decoded to R=0 G=0 B=255 A=127 - a half-transparent BLUE pixel.
// drawImage stretched it over every empty item tile, so all of Britain outside the buildings drew
// as blue squares and the art view looked broken in a way that had nothing to do with the renderer.
//
// A byte comparison against a known-good constant would only pin the same mistake if it were made
// again, so this decodes the PNG properly - IHDR, inflate, un-filter - and asserts what actually
// matters: every pixel is fully transparent. That is a claim about the image, not about 68 bytes.

const test = require('node:test');
const assert = require('node:assert');
const zlib = require('zlib');

const { EMPTY_PNG } = require('./artrenderer.js');

/**
 * A small PNG reader: enough to prove what is in one, and general enough that swapping the constant
 * for a bigger or differently-filtered image does not silently stop testing anything.
 */
function decode(buffer) {
    assert.deepStrictEqual(
        Array.from(buffer.subarray(0, 8)),
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        'not a PNG signature');

    let at = 8;
    let header = null;
    const idat = [];

    while (at < buffer.length) {
        const length = buffer.readUInt32BE(at);
        const type = buffer.toString('ascii', at + 4, at + 8);
        const body = buffer.subarray(at + 8, at + 8 + length);

        if (type === 'IHDR') {
            header = {
                width: body.readUInt32BE(0),
                height: body.readUInt32BE(4),
                depth: body[8],
                colourType: body[9],
                interlace: body[12]
            };
        } else if (type === 'IDAT') {
            idat.push(body);
        }

        at += 12 + length;
    }

    assert.ok(header, 'no IHDR');
    assert.strictEqual(header.depth, 8, 'only 8-bit is decoded here');
    assert.strictEqual(header.colourType, 6, 'expected truecolour with alpha');
    assert.strictEqual(header.interlace, 0, 'interlaced PNGs are not decoded here');

    const channels = 4;
    const stride = header.width * channels;
    const raw = zlib.inflateSync(Buffer.concat(idat));

    assert.strictEqual(raw.length, (stride + 1) * header.height, 'unexpected inflated size');

    const pixels = Buffer.alloc(stride * header.height);

    // Un-filtering, per the PNG spec. All five types, so the test does not quietly depend on the
    // writer choosing None.
    for (let y = 0; y < header.height; y++) {
        const filter = raw[y * (stride + 1)];
        const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));

        for (let i = 0; i < stride; i++) {
            const a = i >= channels ? pixels[y * stride + i - channels] : 0;
            const b = y > 0 ? pixels[(y - 1) * stride + i] : 0;
            const c = (i >= channels && y > 0) ? pixels[(y - 1) * stride + i - channels] : 0;

            let value;

            switch (filter) {
                case 0: value = line[i]; break;
                case 1: value = line[i] + a; break;
                case 2: value = line[i] + b; break;
                case 3: value = line[i] + ((a + b) >> 1); break;
                case 4: value = line[i] + paeth(a, b, c); break;
                default: throw new Error(`unknown filter type ${filter}`);
            }

            pixels[y * stride + i] = value & 0xFF;
        }
    }

    return { ...header, pixels };
}

function paeth(a, b, c) {
    const p = a + b - c;
    const pa = Math.abs(p - a);
    const pb = Math.abs(p - b);
    const pc = Math.abs(p - c);

    if (pa <= pb && pa <= pc) {
        return a;
    }

    return pb <= pc ? b : c;
}

test('the empty item tile decodes as a real PNG', () => {
    const image = decode(EMPTY_PNG);

    assert.ok(image.width >= 1 && image.height >= 1);
});

test('every pixel of the empty item tile is fully transparent', () => {
    // The whole point. A tile with no items must add nothing to what is already drawn beneath it,
    // and "nothing" means alpha zero on every pixel - not a low alpha, not a transparent-looking
    // colour. The bug this replaces was alpha 127.
    const image = decode(EMPTY_PNG);

    for (let i = 0; i < image.pixels.length; i += 4) {
        assert.strictEqual(
            image.pixels[i + 3], 0,
            `pixel ${i / 4} has alpha ${image.pixels[i + 3]}, so it would tint the map beneath it`);
    }
});

test('the decoder would notice a pixel that was not transparent', () => {
    // A test that cannot fail is not a test. This is the bad constant that actually shipped -
    // half-transparent blue - and the assertion above has to reject it.
    const bad = Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
        'base64');

    const image = decode(bad);

    assert.deepStrictEqual(
        Array.from(image.pixels),
        [0, 0, 255, 127],
        'the constant that caused the blue squares should decode as half-transparent blue');

    assert.notStrictEqual(image.pixels[3], 0, 'and it is not transparent');
});
