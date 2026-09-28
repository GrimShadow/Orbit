import { deflateSync } from 'node:zlib';

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buf: Buffer): number {
  let c = 0xffffffff;
  for (const byte of buf) c = CRC_TABLE[(c ^ byte) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const typeBuf = Buffer.from(type, 'ascii');
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])));
  return Buffer.concat([len, typeBuf, data, crc]);
}

/** A real, minimal 1x1 truecolor PNG whose single pixel encodes `seed` - so every fixture has genuinely unique bytes
 * (and therefore a unique SHA-256) rather than sharing one buffer across every upload. That distinction matters here:
 * the ingest pipeline's duplicate detection (spec Step 1.4, content hash) is real and would otherwise fold every
 * upload after the first into a single "duplicate" asset instead of 50 (or N) independently ingestable ones. */
export function uniquePng(seed: number): Buffer {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(1, 0); // width
  ihdr.writeUInt32BE(1, 4); // height
  ihdr.writeUInt8(8, 8); // bit depth
  ihdr.writeUInt8(2, 9); // color type: truecolor (RGB)
  ihdr.writeUInt8(0, 10); // compression
  ihdr.writeUInt8(0, 11); // filter
  ihdr.writeUInt8(0, 12); // interlace

  const r = seed & 0xff;
  const g = (seed >> 8) & 0xff;
  const b = (seed >> 16) & 0xff;
  const raw = Buffer.from([0, r, g, b]); // filter-type byte 0 (none) + one RGB pixel
  const idat = deflateSync(raw);

  return Buffer.concat([signature, chunk('IHDR', ihdr), chunk('IDAT', idat), chunk('IEND', Buffer.alloc(0))]);
}
