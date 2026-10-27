/**
 * Generate the extension icons (PNG) with no third-party dependency.
 *
 * Writes a minimal, valid PNG using zlib (built into Node) and a hand-rolled
 * CRC32. Shapes are drawn procedurally so the pack stays self-contained.
 */
import { deflateSync } from 'node:zlib';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const typeBuf = Buffer.from(type, 'ascii');
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])));
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

/**
 * Encode RGBA pixel data as a PNG.
 * @param {number} width
 * @param {number} height
 * @param {Buffer} rgba width*height*4 bytes
 */
function encodePng(width, height, rgba) {
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0; // filter: none
    rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;  // bit depth
  ihdr[9] = 6;  // colour type RGBA
  ihdr[10] = 0; // deflate
  ihdr[11] = 0; // adaptive filtering
  ihdr[12] = 0; // no interlace
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** Draw an icon: rounded-rect background + a simple glyph. */
function drawIcon(size, kind) {
  const rgba = Buffer.alloc(size * size * 4);
  const set = (x, y, r, g, b, a) => {
    if (x < 0 || y < 0 || x >= size || y >= size) return;
    const i = (y * size + x) * 4;
    const alpha = a / 255;
    rgba[i] = Math.round(rgba[i] * (1 - alpha) + r * alpha);
    rgba[i + 1] = Math.round(rgba[i + 1] * (1 - alpha) + g * alpha);
    rgba[i + 2] = Math.round(rgba[i + 2] * (1 - alpha) + b * alpha);
    rgba[i + 3] = Math.min(255, rgba[i + 3] + a);
  };

  const palettes = {
    shield: [[16, 185, 129], [5, 150, 105]],
    speed: [[59, 130, 246], [37, 99, 235]],
    search: [[168, 85, 247], [124, 58, 237]],
  };
  const [c1, c2] = palettes[kind] ?? palettes.shield;

  const radius = size * 0.22;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      // Rounded-rect mask.
      const dx = Math.max(radius - x, 0, x - (size - 1 - radius));
      const dy = Math.max(radius - y, 0, y - (size - 1 - radius));
      if (Math.hypot(dx, dy) > radius) continue;
      const t = (x + y) / (size * 2);
      set(x, y,
        Math.round(c1[0] + (c2[0] - c1[0]) * t),
        Math.round(c1[1] + (c2[1] - c1[1]) * t),
        Math.round(c1[2] + (c2[2] - c1[2]) * t),
        255);
    }
  }

  const cx = size / 2;
  const cy = size / 2;
  const white = (x, y, a = 255) => set(x, y, 255, 255, 255, a);

  if (kind === 'shield') {
    // Shield outline.
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const nx = (x - cx) / (size * 0.28);
        const ny = (y - size * 0.28) / (size * 0.34);
        const inShield = nx * nx + ny * ny < 1;
        const inner = nx * nx + ny * ny < 0.55;
        if (inShield && !inner) white(x, y);
      }
    }
  } else if (kind === 'speed') {
    // Lightning bolt.
    const pts = [[0.55, 0.18], [0.34, 0.55], [0.48, 0.55], [0.4, 0.84], [0.66, 0.44], [0.5, 0.44]];
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const fx = x / size;
        const fy = y / size;
        for (const [px, py] of pts) {
          if (Math.hypot(fx - px, fy - py) < 0.075) { white(x, y); break; }
        }
      }
    }
  } else {
    // Magnifier.
    const r = size * 0.2;
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const d = Math.hypot(x - cx, y - cy * 0.92);
        if (Math.abs(d - r) < size * 0.055) white(x, y);
      }
    }
    for (let t = 0; t < size * 0.22; t++) {
      white(Math.round(cx + size * 0.16 + t * 0.7), Math.round(cy * 0.92 + size * 0.16 + t * 0.7));
    }
  }

  return encodePng(size, size, rgba);
}

const PACKS = ['agb-shield', 'agb-speed', 'agb-search'];
const KINDS = { 'agb-shield': 'shield', 'agb-speed': 'speed', 'agb-search': 'search' };

for (const pack of PACKS) {
  const dir = join(process.cwd(), 'packages', 'extensions', pack, 'icons');
  mkdirSync(dir, { recursive: true });
  for (const size of [16, 48, 128]) {
    writeFileSync(join(dir, `icon${size}.png`), drawIcon(size, KINDS[pack]));
  }
  console.log(`icons written for ${pack}`);
}
