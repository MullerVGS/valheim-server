// O que o mapa e as paginas de base e de jogador dividem: nomes do jogo, formatos, itens e as regras
// que dizem qual base e qual e quem construiu.
import { KINDS } from './pieces.js';
import { GAME } from './mapview.js';

// Heightmap.Biome (metrica do jogador) e indice do terrain.bin (tools/build_terrain.py).
export const BIOME_NAMES = {
  1: 'Prado', 2: 'Pântano', 4: 'Montanha', 8: 'Floresta Negra', 16: 'Planície', 32: 'Terras das Cinzas',
  64: 'Extremo Norte', 256: 'Oceano', 512: 'Terras Nebulosas',
};
const TERRAIN_BIOMES = ['Oceano', 'Prado', 'Pântano', 'Montanha', 'Floresta Negra', 'Planície', 'Terras das Cinzas', 'Extremo Norte', 'Terras Nebulosas'];

// Minimap.PinType -> icone
export const PIN_ICONS = { Icon0: 'fire', Icon1: 'house', Icon2: 'hammer', Icon3: 'pin', Icon4: 'portal', Boss: 'boss' };
export const PLACE_PINS = new Set(['Icon0', 'Icon1', 'Icon2', 'Icon3']);
// Agrupamento de pecas com menos que isto e "construcao", nao base.
export const BASE_MIN_PIECES = 40;
// Folga ao redor da caixa da base para baus, camas e marcacoes.
export const BASE_MARGIN = 10;

export const $ = (id) => document.getElementById(id);
export const fmt = new Intl.NumberFormat('pt-BR');

export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v == null || v === false) continue;
    if (k === 'class') node.className = v;
    else if (k === 'text') node.textContent = v;
    else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c != null && c !== false) node.append(c);
  return node;
}

export async function getJSON(url) {
  const res = await fetch(url, { cache: 'no-cache' });
  if (!res.ok) throw new Error(`${url}: ${res.status}`);
  return res.json();
}

export function ago(ms) {
  const s = Math.max(0, (Date.now() - ms) / 1000);
  if (s < 90) return 'agora há pouco';
  if (s < 3600) return `há ${Math.round(s / 60)} min`;
  if (s < 86400 * 1.5) return `há ${Math.round(s / 3600)} h`;
  return `há ${Math.round(s / 86400)} dias`;
}

export function duration(minutes) {
  if (minutes < 60) return `${Math.round(minutes)} min`;
  const h = minutes / 60;
  return h < 10 ? `${h.toFixed(1).replace('.', ',')} h` : `${fmt.format(Math.round(h))} h`;
}

export const dayMonth = (ms) => new Date(ms).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' });
export const clock = (ms) => new Date(ms).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });

export const playerHref = (name) => `jogador/${encodeURIComponent(name)}`;
export const mapHref = (x, z, mpp = 1.2) => `./#${Math.round(x)},${Math.round(z)},${mpp}`;

export function biomeAt(terrain, x, z) {
  if (!terrain) return null;
  const j = Math.floor(x / terrain.pixelSize + terrain.size / 2);
  const i = Math.floor(z / terrain.pixelSize + terrain.size / 2);
  if (i < 0 || j < 0 || i >= terrain.size || j >= terrain.size) return null;
  return TERRAIN_BIOMES[terrain.biome[i * terrain.size + j]] ?? null;
}

// ---------- itens (public/game/items.json + items.webp, ver tools/extract_items.py) ----------

let itemsPromise = null;
export function loadItems() {
  itemsPromise ??= getJSON(`${GAME}/items.json`)
    .then((d) => ({ size: d.size, cols: d.cols, byHash: new Map(Object.entries(d.items).map(([h, v]) => [Number(h), v])) }))
    .catch(() => ({ size: 32, cols: 32, byHash: new Map() }));
  return itemsPromise;
}

export function itemName(items, hash) {
  const it = items.byHash.get(hash);
  return it ? it[1] || it[2] : 'Item desconhecido';
}

export function itemIcon(items, hash, px = 32) {
  const it = items.byHash.get(hash);
  const span = el('span', { class: 'item-icon', 'aria-hidden': 'true' });
  span.style.width = span.style.height = `${px}px`;
  if (it) {
    const k = px / items.size;
    const idx = it[3];
    span.style.backgroundImage = `url(${GAME}/items.webp)`;
    span.style.backgroundSize = `${items.cols * items.size * k}px auto`;
    span.style.backgroundPosition = `-${(idx % items.cols) * items.size * k}px -${Math.floor(idx / items.cols) * items.size * k}px`;
  }
  return span;
}

// Grade de itens com quantidade: [[hash, quantidade, qualidade]] somando repetidos.
export function itemGrid(items, list, { limit = Infinity, px = 32 } = {}) {
  const total = new Map();
  for (const [hash, stack, quality] of list) {
    const key = `${hash}|${quality}`;
    const prev = total.get(key);
    total.set(key, prev ? [hash, prev[1] + stack, quality] : [hash, stack, quality]);
  }
  const rows = [...total.values()].sort((a, b) => b[1] - a[1]);
  const grid = el('ul', { class: 'item-grid' });
  for (const [hash, stack, quality] of rows.slice(0, limit)) {
    const name = itemName(items, hash);
    grid.append(
      el('li', { title: `${name}${quality > 1 ? ` (nível ${quality})` : ''}: ${fmt.format(stack)}` },
        itemIcon(items, hash, px),
        el('span', { class: 'qty', text: stack > 1 ? fmt.format(stack) : '' }),
        quality > 1 ? el('span', { class: 'lvl', text: `★${quality}` }) : null),
    );
  }
  if (rows.length > limit) grid.append(el('li', { class: 'more', text: `+${rows.length - limit}` }));
  return grid;
}

export function normalize(text) {
  return text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}

// ---------- bases ----------

export const inBox = (x, z, g, margin = 0) =>
  x >= g.minX - margin && x <= g.maxX + margin && z >= g.minZ - margin && z <= g.maxZ + margin;

export const baseCenter = (g) => [(g.minX + g.maxX) / 2, (g.minZ + g.maxZ) / 2];
export const baseHref = (g) => `base/${baseCenter(g).map(Math.round).join(',')}`;

// Base que contem (x, z): o agrupamento cuja caixa tem o ponto, o maior se houver mais de um.
export function baseAt(settlements, x, z, margin = 0) {
  let best = null;
  for (const g of settlements.groups) if (inBox(x, z, g, margin) && (!best || g.count > best.count)) best = g;
  return best;
}

// Contagem de pecas por construtor dentro da caixa, da grade do /api/world.
export function buildersIn(world, g) {
  const out = new Map();
  if (!world) return [];
  const cell = world.cell;
  for (const [cx, cz, who, n] of world.builders.cells) {
    const x = (cx + 0.5) * cell;
    const z = (cz + 0.5) * cell;
    if (!inBox(x, z, g, cell / 2)) continue;
    const name = world.builders.names[who];
    out.set(name, (out.get(name) ?? 0) + n);
  }
  return [...out].map(([name, count]) => ({ name, count })).sort((a, b) => b.count - a.count);
}

export function baseName(g, pins, world) {
  const pin = pins?.find((p) => PLACE_PINS.has(p.type) && p.name && inBox(p.x, p.z, g, 15));
  if (pin) return pin.name;
  if (g.count < BASE_MIN_PIECES) return 'Construção';
  const [top] = buildersIn(world, g);
  return top ? `Base de ${top.name}` : 'Base';
}

export function materials(g) {
  return g.kinds
    .map((c, k) => ({ name: KINDS[k].name, count: c }))
    .filter((m) => m.count > 0)
    .sort((a, b) => b.count - a.count);
}

export function containerTitle(c) {
  if (c.tomb) return `Lápide de ${c.owner || 'alguém'}`;
  return c.label || c.kind;
}

// Barras horizontais de parte do todo (construtores, materiais): rotulo, barra e numero.
export function shareBars(rows, { href } = {}) {
  const total = rows.reduce((a, r) => a + r.count, 0) || 1;
  const max = Math.max(...rows.map((r) => r.count), 1);
  return el('ul', { class: 'bars' },
    rows.map((r) => {
      const name = href ? el('a', { href: href(r), text: r.name }) : el('span', { text: r.name });
      const bar = el('span', { class: 'bar' });
      bar.style.setProperty('--w', `${(r.count / max) * 100}%`);
      return el('li', { title: `${r.name}: ${fmt.format(r.count)} (${Math.round((r.count / total) * 100)}%)` },
        el('span', { class: 'label' }, name),
        bar,
        el('span', { class: 'value', text: r.count / total < 0.005 ? '<1%' : `${Math.round((r.count / total) * 100)}%` }));
    }));
}

// Colunas por dia/hora com dica ao passar o mouse. rows: [{ key, value, label, tip }].
export function columnChart(rows, { height = 120, format = String, onPick } = {}) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  const tip = el('div', { class: 'chart-tip', hidden: true });
  const wrap = el('div', { class: 'columns', role: 'img' });
  wrap.style.setProperty('--h', `${height}px`);
  const cols = el('div', { class: 'cols' });
  for (const r of rows) {
    const bar = el('span', { class: 'col-bar' });
    bar.style.height = `${r.value > 0 ? Math.max(3, (r.value / max) * 100) : 0}%`;
    const col = el('button', { class: `col${r.active ? ' active' : ''}`, type: 'button', 'aria-label': `${r.label}: ${r.tip ?? format(r.value)}` }, bar);
    const show = () => {
      tip.textContent = `${r.label} · ${r.tip ?? format(r.value)}`;
      tip.hidden = false;
      const box = wrap.getBoundingClientRect();
      const c = col.getBoundingClientRect();
      tip.style.left = `${Math.min(box.width - tip.offsetWidth, Math.max(0, c.left - box.left + c.width / 2 - tip.offsetWidth / 2))}px`;
    };
    col.addEventListener('pointerenter', show);
    col.addEventListener('focus', show);
    col.addEventListener('pointerleave', () => (tip.hidden = true));
    col.addEventListener('blur', () => (tip.hidden = true));
    if (onPick) col.addEventListener('click', () => onPick(r));
    cols.append(col);
  }
  const axis = el('div', { class: 'axis' },
    el('span', { text: rows[0]?.label ?? '' }),
    el('span', { text: `máx ${format(max)}` }),
    el('span', { text: rows[rows.length - 1]?.label ?? '' }));
  wrap.append(tip, cols, axis);
  return wrap;
}
