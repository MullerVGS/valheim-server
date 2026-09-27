// O que as paginas de base e de jogador precisam do save ao vivo (SAVE_DIR): baus com o que tem dentro,
// camas e quem construiu cada peca. Relido quando o autosave termina (marcador _main.<n>.ok novo), fora
// isso so stat. Nada sai de fora do explorado das mesas.
import { readdir, readFile, stat } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { MAP_SIZE, parseCatalog, readPlayerHistory, readWorld, stableHash } from './saves.mjs';

const PIXEL = 12;
// Grade da contagem de pecas por construtor, em metros.
const BUILDER_CELL = 8;
const SIGN_REACH = 2.5;

const KINDS = new Map(
  [
    ['piece_chest_wood', 'Baú de madeira'],
    ['piece_chest', 'Baú reforçado'],
    ['piece_chest_private', 'Baú pessoal'],
    ['piece_chest_blackmetal', 'Baú de metal negro'],
    ['piece_chest_barrel', 'Barril'],
    ['Cart', 'Carroça'],
    ['Raft', 'Jangada'],
    ['Karve', 'Karve'],
    ['VikingShip', 'Drakkar'],
    ['VikingShip_Ashlands', 'Drakkar das Cinzas'],
    ['Player_tombstone', 'Lápide'],
  ].map(([prefab, name]) => [stableHash(prefab), name]),
);
const TOMBSTONE = stableHash('Player_tombstone');

// Texto de placa sem rich text, sem icones (:wood:) e sem as marcas do plugin ({u}, >>).
export function signLabel(text) {
  return text
    .replace(/<[^>]*>/g, '')
    .replace(/:[^:\s][^:]*?:/g, ' ')
    .replace(/\{u\}|>>/g, ' ')
    .replace(/[↓↑←→]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 48);
}

async function playersTsv(mapDir) {
  try {
    const text = await readFile(join(mapDir, 'players.tsv'), 'utf8');
    return new Map(text.split('\n').filter(Boolean).map((l) => l.split('\t')));
  } catch {
    return new Map();
  }
}

export class LiveWorld {
  constructor({ saveDir, mapDir }) {
    this.saveDir = saveDir;
    this.mapDir = mapDir;
    this.marker = null;
    this.json = null; // { gz, etag }
  }

  // Ultimo save completo: o jogo grava _main.<n>.ok depois dos chunks.
  async latest() {
    let best = null;
    for (const name of await readdir(this.saveDir)) {
      const m = name.match(/_main\.(\d+)\.ok$/);
      if (m && (!best || Number(m[1]) > best.n)) best = { n: Number(m[1]), name };
    }
    if (!best) return null;
    const s = await stat(join(this.saveDir, best.name));
    return { ...best, key: `${best.name}:${Math.floor(s.mtimeMs)}`, time: s.mtimeMs };
  }

  async refresh() {
    if (!this.saveDir) return;
    const last = await this.latest();
    if (!last || last.key === this.marker) return;
    const t0 = performance.now();
    let catalog = null;
    try {
      catalog = parseCatalog(await readFile(join(this.mapDir, 'pieces-catalog.bin')));
    } catch {}
    const names = await readdir(this.saveDir);
    // Um arquivo por vez: o save inteiro nunca fica na memoria de uma so vez.
    const files = {
      keys: () => names[Symbol.iterator](),
      get: (n) => {
        try {
          return readFileSync(join(this.saveDir, n));
        } catch {
          return null;
        }
      },
    };
    const world = readWorld(files, catalog, { lean: true });
    const fwl = names.find((n) => n === `_main.${last.n}.fwl2`) ?? names.find((n) => n.endsWith('.fwl2') || n.endsWith('.fwl'));
    const history = fwl ? readPlayerHistory(await readFile(join(this.saveDir, fwl))) : [];
    const value = this.build(world, history, await playersTsv(this.mapDir), last.time);
    const body = gzipSync(JSON.stringify(value));
    this.json = { gz: body, etag: `"w-${createHash('sha1').update(body).digest('hex').slice(0, 16)}"` };
    this.marker = last.key;
    console.log(`mundo: ${value.containers.length} baus, ${value.beds.length} camas, ${value.builders.names.length} construtores ` +
      `(${(body.length / 1024).toFixed(0)} KiB) em ${Math.round(performance.now() - t0)} ms`);
  }

  build(world, history, steamNames, savedAt) {
    const { explored } = world;
    const seen = (x, z) => {
      const j = Math.floor(x / PIXEL + MAP_SIZE / 2);
      const i = Math.floor(z / PIXEL + MAP_SIZE / 2);
      return j >= 0 && i >= 0 && j < MAP_SIZE && i < MAP_SIZE && explored[i * MAP_SIZE + j] === 1;
    };

    // Jogador (id do personagem) -> nome: camas e lapides guardam os dois. Sem eles, o historico de
    // jogadores do mundo diz a conta Steam, e o plugin diz o personagem atual dela.
    const byPlayer = new Map();
    for (const o of world.owners) byPlayer.set(o.owner, o.name);
    for (const c of world.containers) if (c.prefab === TOMBSTONE && c.owner && c.ownerName) byPlayer.set(c.owner, c.ownerName);
    const byIndex = (index) => {
      const h = history[index];
      if (!h) return null;
      return steamNames.get(h.id.replace(/^Steam_/, '')) ?? h.name;
    };
    const who = (creator, index) => byPlayer.get(creator) ?? byIndex(index) ?? null;

    const names = [];
    const nameIndex = new Map();
    const idOf = (name) => {
      if (!nameIndex.has(name)) {
        nameIndex.set(name, names.length);
        names.push(name);
      }
      return nameIndex.get(name);
    };
    const cells = new Map();
    for (const zdo of world.candidates) {
      if (!zdo.creator || !seen(zdo.x, zdo.z)) continue;
      const name = who(zdo.creator, zdo.creatorIndex);
      if (!name) continue;
      const key = `${Math.floor(zdo.x / BUILDER_CELL)},${Math.floor(zdo.z / BUILDER_CELL)},${idOf(name)}`;
      cells.set(key, (cells.get(key) ?? 0) + 1);
    }

    const signs = world.signs.filter((s) => seen(s.x, s.z)).map((s) => ({ ...s, label: signLabel(s.text) })).filter((s) => s.label);
    const labelFor = (c) => {
      let best = null;
      let bestD = SIGN_REACH;
      for (const s of signs) {
        if (Math.abs(s.y - c.y) > 3) continue;
        const d = Math.hypot(s.x - c.x, s.z - c.z);
        if (d < bestD) {
          bestD = d;
          best = s.label;
        }
      }
      return best;
    };

    const round = (v) => Math.round(v * 10) / 10;
    const containers = [];
    for (const c of world.containers) {
      const tomb = c.prefab === TOMBSTONE;
      if ((!c.creator && !tomb) || !seen(c.x, c.z)) continue;
      containers.push({
        x: round(c.x),
        z: round(c.z),
        y: round(c.y),
        kind: KINDS.get(c.prefab) ?? 'Baú',
        tomb,
        label: tomb ? null : labelFor(c),
        owner: tomb ? c.ownerName ?? '' : who(c.creator, -1),
        items: c.items,
      });
    }
    const beds = world.owners.filter((o) => seen(o.x, o.z)).map((o) => ({ x: round(o.x), z: round(o.z), name: o.name }));

    return {
      savedAt,
      cell: BUILDER_CELL,
      containers,
      beds,
      builders: { names, cells: [...cells].map(([k, n]) => [...k.split(',').map(Number), n]) },
    };
  }
}
