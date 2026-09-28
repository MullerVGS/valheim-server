// Escondidos: qualquer visitante pode tirar do site uma base, bau, portal, cama, marcacao ou jogador. Some
// para todo mundo, menos para quem escondeu, que continua vendo (marcado) e e o unico que pode mostrar de novo.
// Quem e quem: cookie com um id aleatorio; se o cookie se perde, uma impressao basica do navegador
// (mandada pelo front) devolve o mesmo id. Tudo num JSON em ARCHIVE_DIR, o unico volume gravavel do site.
import { randomBytes } from 'node:crypto';
import { readFile, rename, writeFile } from 'node:fs/promises';

export const KINDS = ['base', 'chest', 'portal', 'bed', 'pin', 'player'];
const COOKIE = 'jahmaica_id';
const COOKIE_MAX_AGE = 400 * 86400;
// Folga em volta da base: baus, camas e marcacoes na borda tambem somem.
const BASE_MARGIN = 15;
// Coisa de ponto: mesma posicao a menos disto (m). O bau vem a 0,1 m, pin e portal a 1 m.
const POINT_SLACK = 2;
const MAX_HIDES_PER_CLIENT = 60;
const MAX_FPS_PER_CLIENT = 6;
// Mesmo agrupamento de pecas do front (pieces.js groupSettlements): celula de 6 m, vizinhas ate 2 celulas.
const GROUP_CELL = 6;

const finite = (v) => typeof v === 'number' && Number.isFinite(v);
const inBox = (x, z, b) => x >= b[0] && x <= b[2] && z >= b[1] && z <= b[3];
const overlaps = (a, b) => a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];

// Caixas dos agrupamentos de pecas de um VPC1 cru.
function groupBoxes(raw) {
  const n = raw.readUInt32LE(4);
  const cells = new Map();
  for (let i = 0; i < n; i++) {
    const x = raw.readFloatLE(8 + i * 29);
    const z = raw.readFloatLE(12 + i * 29);
    const k = `${Math.floor(x / GROUP_CELL)},${Math.floor(z / GROUP_CELL)}`;
    let c = cells.get(k);
    if (!c) cells.set(k, (c = { cx: Math.floor(x / GROUP_CELL), cz: Math.floor(z / GROUP_CELL), pts: [], done: false }));
    c.pts.push(x, z);
  }
  const boxes = [];
  for (const start of cells.values()) {
    if (start.done) continue;
    start.done = true;
    const box = [Infinity, Infinity, -Infinity, -Infinity];
    const stack = [start];
    while (stack.length) {
      const c = stack.pop();
      for (let k = 0; k < c.pts.length; k += 2) {
        box[0] = Math.min(box[0], c.pts[k]);
        box[1] = Math.min(box[1], c.pts[k + 1]);
        box[2] = Math.max(box[2], c.pts[k]);
        box[3] = Math.max(box[3], c.pts[k + 1]);
      }
      for (let dx = -2; dx <= 2; dx++) {
        for (let dz = -2; dz <= 2; dz++) {
          const nb = cells.get(`${c.cx + dx},${c.cz + dz}`);
          if (nb && !nb.done) {
            nb.done = true;
            stack.push(nb);
          }
        }
      }
    }
    boxes.push(box);
  }
  return boxes;
}

// Resumo do navegador para a lista de escondidos ("Chrome no Windows").
function agentOf(ua = '') {
  const browser = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox'
    : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'navegador';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows'
    : /Mac OS X/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? `${browser} no ${os}` : browser;
}

export function readCookie(req) {
  const m = (req.headers.cookie ?? '').match(new RegExp(`(?:^|;\\s*)${COOKIE}=([a-f0-9]{32})`));
  return m ? m[1] : null;
}

export function cookieHeader(id, req) {
  const secure = req.headers['x-forwarded-proto'] === 'https' ? '; Secure' : '';
  return `${COOKIE}=${id}; Path=/; Max-Age=${COOKIE_MAX_AGE}; HttpOnly; SameSite=Lax${secure}`;
}

export class Hidden {
  constructor(path) {
    this.path = path;
    this.clients = {}; // id -> { fps: [], agent, since }
    this.hides = []; // { id, kind, by, at, title, x, z, box?, name? }
    this.areas = new Map(); // id da base escondida -> caixa efetiva (cresce com a base)
    this.version = 0;
    this.saving = Promise.resolve();
  }

  async load() {
    if (!this.path) return;
    try {
      const data = JSON.parse(await readFile(this.path, 'utf8'));
      this.clients = data.clients ?? {};
      this.hides = data.hides ?? [];
    } catch (err) {
      if (err.code !== 'ENOENT') console.error('escondidos:', err.message);
    }
    this.version++;
  }

  save() {
    if (!this.path) return Promise.resolve();
    const body = JSON.stringify({ clients: this.clients, hides: this.hides }, null, 1);
    this.saving = this.saving.then(async () => {
      await writeFile(this.path + '.tmp', body);
      await rename(this.path + '.tmp', this.path);
    }).catch((err) => console.error('escondidos: gravar:', err.message));
    return this.saving;
  }

  // Quem pede: o cookie; sem ele (ou sem registro), a impressao do navegador acha o id antigo.
  identify(cookieId, fp) {
    if (cookieId) {
      const c = this.clients[cookieId];
      if (c && fp && !c.fps.includes(fp)) {
        c.fps = [fp, ...c.fps].slice(0, MAX_FPS_PER_CLIENT);
        this.save();
      }
      return { id: cookieId, recovered: false };
    }
    if (fp) {
      for (const [id, c] of Object.entries(this.clients)) if (c.fps.includes(fp)) return { id, recovered: true };
    }
    return { id: randomBytes(16).toString('hex'), recovered: false };
  }

  mine(id) {
    return this.hides.filter((h) => h.by === id).map((h) => this.public(h));
  }

  // O que o dono ve do proprio escondido: a caixa efetiva da base, sem o id de ninguem.
  public(h) {
    const out = { id: h.id, kind: h.kind, at: h.at, title: h.title, agent: this.clients[h.by]?.agent ?? null, x: h.x, z: h.z };
    if (h.kind === 'base') out.box = this.areas.get(h.id) ?? h.box;
    if (h.kind === 'player') out.name = h.name;
    return out;
  }

  add(by, fp, ua, input) {
    const kind = input?.kind;
    if (!KINDS.includes(kind)) throw new Error('tipo');
    const title = String(input.title ?? '').slice(0, 80);
    const h = { id: randomBytes(6).toString('hex'), kind, by, at: Date.now(), title };
    if (kind === 'player') {
      if (typeof input.name !== 'string' || !input.name || input.name.length > 64) throw new Error('nome');
      h.name = input.name;
      if (this.hides.some((o) => o.kind === 'player' && o.name === h.name)) throw new Error('ja escondido');
    } else {
      if (!finite(input.x) || !finite(input.z)) throw new Error('posicao');
      h.x = Math.round(input.x * 10) / 10;
      h.z = Math.round(input.z * 10) / 10;
      if (kind === 'base') {
        const b = input.box;
        if (!Array.isArray(b) || b.length !== 4 || !b.every(finite) || b[2] < b[0] || b[3] < b[1] || b[2] - b[0] > 2000 || b[3] - b[1] > 2000) throw new Error('caixa');
        h.box = b.map((v) => Math.round(v));
      }
    }
    if (this.hides.filter((o) => o.by === by).length >= MAX_HIDES_PER_CLIENT) throw new Error('limite');
    const c = (this.clients[by] ??= { fps: [], agent: agentOf(ua), since: Date.now() });
    if (fp && !c.fps.includes(fp)) c.fps = [fp, ...c.fps].slice(0, MAX_FPS_PER_CLIENT);
    this.hides.push(h);
    this.refreshAreas();
    this.save();
    return this.public(h);
  }

  remove(by, id) {
    const i = this.hides.findIndex((h) => h.id === id && h.by === by);
    if (i < 0) return false;
    this.hides.splice(i, 1);
    if (!this.hides.some((h) => h.by === by)) delete this.clients[by];
    this.refreshAreas();
    this.save();
    return true;
  }

  // Base escondida = os agrupamentos de pecas de agora que tocam a caixa guardada: se a base cresce,
  // a parte nova some junto. Refeito quando as pecas ou os escondidos mudam.
  setPieces(raw) {
    this.groups = raw ? groupBoxes(raw) : [];
    this.refreshAreas();
  }

  refreshAreas() {
    this.areas.clear();
    for (const h of this.hides) {
      if (h.kind !== 'base') continue;
      const box = [...h.box];
      for (const g of this.groups ?? []) {
        if (!overlaps(g, h.box)) continue;
        box[0] = Math.min(box[0], g[0]);
        box[1] = Math.min(box[1], g[1]);
        box[2] = Math.max(box[2], g[2]);
        box[3] = Math.max(box[3], g[3]);
      }
      this.areas.set(h.id, [box[0] - BASE_MARGIN, box[1] - BASE_MARGIN, box[2] + BASE_MARGIN, box[3] + BASE_MARGIN]);
    }
    this.version++;
  }

  // O que some para `viewer`: tudo que os outros esconderam. O dele ele continua vendo; o front marca
  // pela lista de /api/hidden.
  view(viewer) {
    const others = this.hides.filter((h) => h.by !== viewer);
    const areas = others.filter((h) => h.kind === 'base').map((h) => this.areas.get(h.id) ?? h.box);
    const points = (kind) => others.filter((h) => h.kind === kind);
    return {
      key: others.map((h) => h.id).sort().join(','),
      empty: !others.length,
      areas,
      players: new Set(points('player').map((h) => h.name)),
      points: Object.fromEntries(['chest', 'portal', 'bed', 'pin'].map((k) => [k, points(k)])),
      inArea(x, z) {
        return areas.some((b) => inBox(x, z, b));
      },
      overlapsArea(box) {
        return areas.some((b) => overlaps(b, box));
      },
      // Escondido de ponto (de outro) naquela posicao?
      hit(kind, x, z) {
        return this.points[kind].some((h) => Math.abs(h.x - x) <= POINT_SLACK && Math.abs(h.z - z) <= POINT_SLACK);
      },
      gone(kind, x, z) {
        return this.inArea(x, z) || this.hit(kind, x, z);
      },
    };
  }
}

// ---------- filtros por tipo de resposta ----------

export function filterState(state, v) {
  // Jogador escondido (ou dentro de base escondida) continua online, sem lugar.
  const players = state.players.map((p) =>
    v.players.has(p.name) || v.inArea(p.x, p.z) ? { name: p.name, ping: p.ping, x: null, z: null, hidden: true } : p);
  const keep = (kind, list) => list.filter((o) => !v.gone(kind, o.x, o.z));
  const portals = keep('portal', state.portals).map((p) =>
    // O par escondido nao pode ser achado pela linha do portal de fora.
    p.target && v.gone('portal', p.target[0], p.target[1]) ? { ...p, target: null } : p);
  return {
    ...state,
    players,
    pins: keep('pin', state.pins),
    portals,
    beds: keep('bed', state.beds),
    tables: state.tables.filter((t) => !v.inArea(t.x, t.z)),
  };
}

export function filterPins(pins, v) {
  return pins.filter((p) => !v.gone('pin', p.x, p.z));
}

export function filterWorld(world, v) {
  const cell = world.cell;
  return {
    ...world,
    containers: world.containers.filter((c) => !v.gone('chest', c.x, c.z)),
    beds: world.beds.filter((b) => !v.gone('bed', b.x, b.z)),
    builders: {
      names: world.builders.names,
      cells: world.builders.cells.filter(([cx, cz]) => !v.inArea((cx + 0.5) * cell, (cz + 0.5) * cell)),
    },
  };
}

export function filterTrails(trails, v) {
  return {
    ...trails,
    players: trails.players
      .filter((p) => !v.players.has(p.name))
      .map((p) => ({ ...p, points: p.points.filter(([, x, z]) => !v.inArea(x, z)) })),
  };
}

// VPC1 sem as pecas dentro das bases escondidas.
export function filterPieces(raw, v) {
  if (!v.areas.length) return raw;
  const n = raw.readUInt32LE(4);
  const keep = [];
  for (let i = 0; i < n; i++) {
    const o = 8 + i * 29;
    if (!v.inArea(raw.readFloatLE(o), raw.readFloatLE(o + 4))) keep.push(o);
  }
  if (keep.length === n) return raw;
  const out = Buffer.alloc(8 + keep.length * 29);
  raw.copy(out, 0, 0, 4);
  out.writeUInt32LE(keep.length, 4);
  keep.forEach((o, k) => raw.copy(out, 8 + k * 29, o, o + 29));
  return out;
}
