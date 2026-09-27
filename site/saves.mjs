// Leitura de um save do Valheim (1.0, mundo em chunks) fora do jogo: o que as mesas de cartografia
// mostram, os pins delas e as construcoes. Formato conferido no codigo do jogo (ZDO.Save/Load,
// ChunkSaveMapping, ZDOMan.SaveChunk). So o que o mapa precisa; o resto do ZDO e pulado.
import { inflateRawSync, gunzipSync } from 'node:zlib';

export const MAP_SIZE = 2048;
const PIXEL = 12;

// String.GetStableHashCode do jogo (djb2 duplo, em int32).
export function stableHash(str) {
  let a = 5381;
  let b = a;
  for (let i = 0; i < str.length; i += 2) {
    a = (Math.imul(a, 33) ^ str.charCodeAt(i)) | 0;
    if (i === str.length - 1) break;
    b = (Math.imul(b, 33) ^ str.charCodeAt(i + 1)) | 0;
  }
  return (a + Math.imul(b, 1566083941)) | 0;
}

const TABLE = stableHash('piece_cartographytable');
const DATA = stableHash('data');
const CREATOR = stableHash('creator');

// Zip sem dependencia: diretorio central + inflate de cada entrada que o filtro aceita.
export function unzip(buf, accept = () => true) {
  let eocd = buf.length - 22;
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('zip sem diretorio central');
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const files = new Map();
  for (let k = 0; k < count; k++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('zip corrompido');
    const method = buf.readUInt16LE(p + 10);
    const size = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen);
    p += 46 + nameLen + extraLen + commentLen;
    if (name.endsWith('/') || !accept(name)) continue;
    const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
    const raw = buf.subarray(start, start + size);
    files.set(name, method === 0 ? Buffer.from(raw) : inflateRawSync(raw));
  }
  return files;
}

class Reader {
  constructor(buf) {
    this.b = buf;
    this.p = 0;
  }
  u8() { return this.b[this.p++]; }
  u16() { const v = this.b.readUInt16LE(this.p); this.p += 2; return v; }
  i16() { const v = this.b.readInt16LE(this.p); this.p += 2; return v; }
  i32() { const v = this.b.readInt32LE(this.p); this.p += 4; return v; }
  u32() { const v = this.b.readUInt32LE(this.p); this.p += 4; return v; }
  i64() { const v = this.b.readBigInt64LE(this.p); this.p += 8; return v; }
  f32() { const v = this.b.readFloatLE(this.p); this.p += 4; return v; }
  skip(n) { this.p += n; }
  numItems() {
    const n = this.u8();
    return n & 0x80 ? ((n & 0x7f) << 8) | this.u8() : n;
  }
  str() {
    let len = 0;
    let shift = 0;
    for (;;) {
      const c = this.u8();
      len |= (c & 0x7f) << shift;
      if (!(c & 0x80)) break;
      shift += 7;
    }
    const s = this.b.toString('utf8', this.p, this.p + len);
    this.p += len;
    return s;
  }
  bytes() { const n = this.i32(); const v = this.b.subarray(this.p, this.p + n); this.p += n; return v; }
}

// ZDO.Save da 1.0: flags, posicao (curta ou float), prefab, giro curto e os dados extras.
function readZdo(r) {
  const flags = r.u16();
  let x, y, z;
  if (flags & 0x2000) {
    x = r.i16();
    y = 0;
    z = r.i16();
  } else {
    x = r.f32();
    y = r.f32();
    z = r.f32();
  }
  const prefab = r.i32();
  let yaw = 0;
  if (flags & 0x1000) {
    const a = r.u16();
    if (a & 0x8000) yaw = (a & 0x7fff) * 0.5;
    else yaw = (((((a << 16) | r.u16()) >>> 10) & 0x3ff) * 0.5);
  }
  let creator = 0n;
  let data = null;
  if (flags & 0xff) {
    if (flags & 0x01) r.skip(5);
    const each = (flag, read) => {
      if (!(flags & flag)) return;
      const n = r.numItems();
      for (let k = 0; k < n; k++) read(r.i32());
    };
    each(0x02, () => r.skip(4));
    each(0x04, () => r.skip(12));
    each(0x08, () => r.skip(16));
    each(0x10, () => r.skip(4));
    each(0x20, (key) => {
      const v = r.i64();
      if (key === CREATOR) creator = v;
    });
    each(0x40, () => r.str());
    each(0x80, (key) => {
      const v = r.bytes();
      if (key === DATA && prefab === TABLE) data = v;
    });
  }
  return { x, y, z, prefab, yaw, creator, data };
}

// Chunks atuais pelo indice _main.<n>.chunks do save mais novo: (chunk u16, tamanho u8, versao u32, zdos i32).
function currentChunks(files) {
  let best = null;
  for (const name of files.keys()) {
    const m = name.match(/_main\.(\d+)\.chunks$/);
    if (m && (!best || Number(m[1]) > best.n)) best = { n: Number(m[1]), name };
  }
  if (!best) throw new Error('save sem indice de chunks');
  const dir = best.name.slice(0, best.name.lastIndexOf('/') + 1);
  const r = new Reader(files.get(best.name));
  r.u16();
  r.i32();
  const n = r.i32();
  const names = [];
  for (let k = 0; k < n; k++) {
    const chunk = r.u16();
    const size = r.u8();
    const version = r.u32();
    r.i32();
    const hex = (v) => v.toString(16).padStart(2, '0');
    names.push(`${dir}${hex(chunk >> 8)}_${hex(chunk & 0xff)}__${size}_${version}.chunk`);
  }
  return names;
}

// Minimap.GetSharedMapData: gzip de (versao, n, byte por pixel, pins).
function parseTable(gz) {
  const r = new Reader(gunzipSync(gz));
  const version = r.i32();
  const n = r.i32();
  if (n !== MAP_SIZE * MAP_SIZE) return null;
  const explored = r.b.subarray(r.p, r.p + n);
  r.skip(n);
  const pins = [];
  if (version >= 2) {
    const count = r.i32();
    for (let k = 0; k < count; k++) {
      r.i64();
      const name = r.str();
      const x = r.f32();
      r.f32();
      const z = r.f32();
      const type = r.i32();
      const checked = r.u8() !== 0;
      const author = version >= 3 ? r.str() : '';
      pins.push({ name, x, z, type, checked, author });
    }
  }
  return { explored, pins };
}

// Tudo o que o mapa precisa de um save: explorado (uniao das mesas), pins sem repetidos e os ZDOs
// candidatos a construcao (so prefab que o catalogo conhece).
export function readWorld(files, catalog) {
  const explored = new Uint8Array(MAP_SIZE * MAP_SIZE);
  const pins = [];
  const seen = new Set();
  const candidates = [];
  let tables = 0;
  for (const name of currentChunks(files)) {
    const buf = files.get(name);
    if (!buf) continue;
    const r = new Reader(buf);
    r.i16();
    const count = r.i32();
    for (let k = 0; k < count; k++) {
      const zdo = readZdo(r);
      if (zdo.data) {
        const table = parseTable(zdo.data);
        if (!table) continue;
        tables++;
        for (let i = 0; i < explored.length; i++) if (table.explored[i]) explored[i] = 1;
        for (const pin of table.pins) {
          const key = `${Math.round(pin.x)}|${Math.round(pin.z)}|${pin.type}|${pin.name}`;
          if (!seen.has(key)) {
            seen.add(key);
            pins.push(pin);
          }
        }
      } else if (catalog?.has(zdo.prefab)) {
        candidates.push(zdo);
      }
    }
  }
  return { explored, pins, candidates, tables };
}

// Catalogo do plugin (pieces-catalog.bin): hash -> forma.
export function parseCatalog(buf) {
  if (buf.toString('latin1', 0, 4) !== 'VPK1') throw new Error('catalogo em formato desconhecido');
  const n = buf.readUInt32LE(4);
  const map = new Map();
  for (let k = 0, o = 8; k < n; k++, o += 22)
    map.set(buf.readInt32LE(o), {
      kind: buf[o + 4],
      crop: buf[o + 5] === 1,
      cx: buf.readFloatLE(o + 6),
      cz: buf.readFloatLE(o + 10),
      hx: buf.readFloatLE(o + 14),
      hz: buf.readFloatLE(o + 18),
    });
  return map;
}

// Mesma regra da varredura do plugin (PieceScan): com criador ou planta, dentro do explorado. Sai no
// formato do pieces.bin (VPC1).
export function buildPieces(candidates, catalog, explored) {
  const marks = [];
  for (const zdo of candidates) {
    const shape = catalog.get(zdo.prefab);
    if (!shape.crop && zdo.creator === 0n) continue;
    const j = Math.floor(zdo.x / PIXEL + MAP_SIZE / 2);
    const i = Math.floor(zdo.z / PIXEL + MAP_SIZE / 2);
    if (j < 0 || i < 0 || j >= MAP_SIZE || i >= MAP_SIZE || !explored[i * MAP_SIZE + j]) continue;
    const rad = (zdo.yaw * Math.PI) / 180;
    const cos = Math.cos(rad);
    const sin = Math.sin(rad);
    // Giro do Unity em torno de y aplicado ao centro da forma no prefab.
    const ox = shape.cx * cos + shape.cz * sin;
    const oz = -shape.cx * sin + shape.cz * cos;
    marks.push([zdo.x + ox, zdo.z + oz, zdo.y, cos, sin, shape.hx, shape.hz, shape.kind]);
  }
  const out = Buffer.alloc(8 + marks.length * 29);
  out.write('VPC1', 0, 'latin1');
  out.writeUInt32LE(marks.length, 4);
  let o = 8;
  for (const m of marks) {
    for (let k = 0; k < 7; k++, o += 4) out.writeFloatLE(m[k], o);
    out[o++] = m[7];
  }
  return out;
}
