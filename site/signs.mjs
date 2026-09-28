// Placas: o mesmo catalogo que o plugin usa (VALHEIM_SIGN_ICONS_CATALOG) e o custom.txt ao lado dele,
// lidos do volume do jogo. Trocar qualquer um dos dois no servidor ja muda o site, sem deploy: a cada
// pedido (no maximo a cada 5 s) confere tamanho e data, e so rele o que parou de mudar ha 2 s.
// Nada disso roda enquanto ninguem abre a pagina de placas.

import { readFile, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { deflateSync, gzipSync } from 'node:zlib';
import { basename, dirname, join } from 'node:path';
import { normalize, parseCatalog } from './public/signcode.js';

const CHECK_MS = 5000;
const SETTLE_MS = 2000;
const CUSTOM = 'custom.txt';
const HEADER = /<cspace=-[0-9.]+>(?:<material=[^<>]*>)?<line-height=[0-9.]+><size=[0-9.]+>/;

export function createSigns(catalogPath, itemsPath) {
  const paths = catalogPath ? [catalogPath, join(dirname(catalogPath), CUSTOM)] : [];
  let loaded = null;
  let loadedStamp = null;
  let checkedAt = 0;
  let loading = null;
  const entries = new Map();

  async function stampOf() {
    const parts = [];
    let newest = 0;
    for (const path of paths) {
      try {
        const info = await stat(path);
        parts.push(`${basename(path)}:${info.size}:${Math.floor(info.mtimeMs)}`);
        newest = Math.max(newest, info.mtimeMs);
      } catch {
        parts.push(`${basename(path)}:-`);
      }
    }
    return { stamp: parts.join('|'), newest };
  }

  async function load(stamp) {
    const files = [];
    let level = 1;
    for (const [n, path] of paths.entries()) {
      let text;
      try {
        text = await readFile(path, 'utf8');
      } catch {
        continue;
      }
      const lines = text.split(/\r?\n/);
      const m = n === 0 && /\blevel=([0-9.]+)/.exec(lines[0] ?? '');
      if (m) level = Number(m[1]) || 1;
      files.push({ lines, kind: n === 0 ? 'catalog' : 'custom' });
    }
    const cat = parseCatalog(files);
    if (!cat.texts.size) return null;
    const version = createHash('sha1').update(stamp).digest('hex').slice(0, 12);
    const names = await itemNames(itemsPath);
    const atlas = buildAtlas(cat.items.map((id) => cat.texts.get(id)), level);
    const index = {
      v: version,
      params: cat.params,
      macros: cat.macros,
      def: cat.defaultIcon,
      cell: atlas.cell,
      cols: atlas.cols,
      items: cat.items.map((id) => [id, names.get(id) ?? '']),
      groups: cat.groups.map((g) => ({ title: g.title, hint: g.hint, ids: g.ids, shown: g.shown?.filter((id) => g.ids.includes(id)) ?? null })),
      names: Object.fromEntries([...cat.names].filter(([id]) => cat.texts.has(id))),
      aliases: cat.aliases,
    };
    entries.clear();
    return {
      version,
      cat,
      index: pack(JSON.stringify(index), `"s-${version}"`),
      atlas: { body: atlas.png, etag: `"a-${version}"` },
    };
  }

  // O catalogo em uso; null sem catalogo configurado ou legivel.
  async function current() {
    if (!paths.length) return null;
    const now = Date.now();
    if (loaded && now - checkedAt < CHECK_MS) return loaded;
    if (loading) return loading;
    checkedAt = now;
    loading = (async () => {
      const { stamp, newest } = await stampOf();
      if (stamp !== loadedStamp && (now - newest > SETTLE_MS || !loaded)) {
        const started = Date.now();
        const next = await load(stamp);
        if (next) {
          loaded = next;
          loadedStamp = stamp;
          console.log(`placas: catalogo ${next.version} lido em ${Date.now() - started} ms, `
            + `${next.cat.items.length} icones, ${next.cat.groups.length} grupos de arte`);
        }
      }
      return loaded;
    })().finally(() => { loading = null; });
    return loading;
  }

  // Um desenho (com e sem rotulo), para a pagina montar o que o servidor gravaria.
  function entry(state, id) {
    if (!state.cat.texts.has(id)) return null;
    let packed = entries.get(id);
    if (!packed) {
      if (entries.size >= 256) entries.delete(entries.keys().next().value);
      packed = pack(JSON.stringify({ i: state.cat.texts.get(id), t: state.cat.titled.get(id) ?? null }), `"e-${state.version}"`);
      entries.set(id, packed);
    }
    return packed;
  }

  return { current, entry };
}

function pack(body, etag) {
  return { gz: gzipSync(body), etag };
}

// Nome em portugues dos itens, do items.json do site (arte do jogo, extraida no host).
async function itemNames(path) {
  const names = new Map();
  try {
    const data = JSON.parse(await readFile(path, 'utf8'));
    for (const [prefab, pt, en] of Object.values(data.items ?? {})) names.set(normalize(prefab), pt || en || '');
  } catch {}
  return names;
}

// ---- miniaturas: os icones do catalogo sao pixel art de blocos; o atlas sai dos proprios textos ----

function hexRgba(hex) {
  const d = (i, w) => (w === 1 ? parseInt(hex[i], 16) * 17 : parseInt(hex.substr(i, 2), 16));
  if (hex.length === 3) return [d(0, 1), d(1, 1), d(2, 1), 255];
  if (hex.length === 4) return [d(0, 1), d(1, 1), d(2, 1), d(3, 1)];
  if (hex.length === 6) return [d(0, 2), d(2, 2), d(4, 2), 255];
  if (hex.length === 8) return [d(0, 2), d(2, 2), d(4, 2), d(6, 2)];
  return [0, 0, 0, 0];
}

function pixelsOf(text) {
  const header = text ? HEADER.exec(text) : null;
  if (!header) return null;
  const rows = [];
  let color = [0, 0, 0, 0];
  for (const line of text.slice(header.index + header[0].length).split('\n')) {
    const row = [];
    for (const m of line.matchAll(/<#([0-9a-fA-F]{3,8})>|(█)|<[^<>]*>|[^<]/g)) {
      if (m[1]) color = hexRgba(m[1]);
      else if (m[2]) row.push(color);
    }
    if (row.length) rows.push(row);
  }
  return rows.length ? rows : null;
}

// O catalogo guarda a cor do jogo vezes `level`; a miniatura mostra a cor cheia.
function buildAtlas(texts, level) {
  const grids = texts.map(pixelsOf);
  const cell = Math.max(1, ...grids.map((g) => (g ? Math.max(g.length, ...g.map((r) => r.length)) : 1)));
  const cols = 32;
  const width = cols * cell;
  const height = Math.max(1, Math.ceil(texts.length / cols)) * cell;
  const rgba = Buffer.alloc(width * height * 4);
  const gain = 1 / (level || 1);
  grids.forEach((grid, n) => {
    if (!grid) return;
    const w = Math.max(...grid.map((r) => r.length));
    const x0 = (n % cols) * cell + Math.floor((cell - w) / 2);
    const y0 = Math.floor(n / cols) * cell + Math.floor((cell - grid.length) / 2);
    grid.forEach((row, y) => row.forEach((c, x) => {
      if (!c[3]) return;
      const o = ((y0 + y) * width + x0 + x) * 4;
      rgba[o] = Math.min(255, Math.round(c[0] * gain));
      rgba[o + 1] = Math.min(255, Math.round(c[1] * gain));
      rgba[o + 2] = Math.min(255, Math.round(c[2] * gain));
      rgba[o + 3] = c[3];
    }));
  });
  return { png: encodePng(width, height, rgba), cell, cols };
}

const CRC = new Int32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c;
});
function crc32(buf) {
  let c = -1;
  for (const b of buf) c = CRC[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}
function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 'latin1');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
  return out;
}
function encodePng(width, height, rgba) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}
