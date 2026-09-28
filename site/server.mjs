// Site do servidor: mapa no estilo do jogo + metricas basicas. Sem dependencias.
// Estatico em public/, dados do mapa recortados pelo explorado (terrain.mjs) e /api/* com
// consultas fixas ao VictoriaMetrics (nada de PromQL vindo do navegador).
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { createReadStream } from 'node:fs';
import { readdir, readFile, stat } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { gunzipSync, gzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { Archive } from './archive.mjs';
import { cookieHeader, filterPieces, filterPins, filterState, filterTrails, filterWorld, Hidden, readCookie } from './hidden.mjs';
import { MapData, maskTerrain } from './terrain.mjs';
import { LiveWorld } from './world.mjs';

const ROOT = fileURLToPath(new URL('.', import.meta.url));
const PUBLIC = join(ROOT, 'public');
const DATA = process.env.DATA_DIR || join(ROOT, 'data');
const VM = (process.env.VM_URL || 'http://victoriametrics:8428').replace(/\/$/, '');
const PORT = Number(process.env.PORT || 8080);
const STATE_TTL_MS = 5000;
const HISTORY_TTL_MS = 60000;
const MAP_DIR = process.env.MAP_DIR || null;
const SAVE_DIR = process.env.SAVE_DIR || null;
const MAP_CHECK_MS = 15000;
const BACKUPS_DIR = process.env.BACKUPS_DIR || null;
const ARCHIVE_DIR = process.env.ARCHIVE_DIR || null;
const WORLD = process.env.WORLD_NAME || '';
const ARCHIVE_EVERY_MS = 3600000;
// Escondidos: o unico estado que o site grava fora do historico.
const HIDDEN_FILE = process.env.HIDDEN_FILE || (ARCHIVE_DIR ? join(ARCHIVE_DIR, 'hidden.json') : null);

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.otf': 'font/otf',
  '.svg': 'image/svg+xml',
  '.bin': 'application/octet-stream',
};

async function query(q) {
  const res = await fetch(`${VM}/api/v1/query?query=${encodeURIComponent(q)}`);
  if (!res.ok) throw new Error(`victoria ${res.status}`);
  return (await res.json()).data.result;
}

async function queryRange(q, seconds, step) {
  const end = Math.floor(Date.now() / 1000);
  const url = `${VM}/api/v1/query_range?query=${encodeURIComponent(q)}&start=${end - seconds}&end=${end}&step=${step}`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`victoria ${res.status}`);
  return (await res.json()).data.result;
}

const num = (r) => (r.length ? Number(r[0].value[1]) : null);
const xz = (m) => ({ x: Number(m.x), z: Number(m.z) });

async function buildState() {
  const [online, fps, frameMax, zdos, explored, autosave, info, pos, biome, ping, pins, portals, beds, tables] =
    await Promise.all([
      query('valheim_players_connected'),
      query('rate(valheim_server_frame_seconds_count[1m])'),
      query('max_over_time(valheim_server_frame_max_seconds[5m])'),
      query('valheim_zdos'),
      query('valheim_map_explored_square_meters'),
      query('valheim_world_seconds_until_autosave'),
      query('valheim_exporter_info'),
      query('valheim_player_position_meters'),
      query('valheim_player_biome'),
      query('valheim_player_ping_seconds'),
      query('valheim_map_pin_info'),
      query('valheim_portal_info'),
      query('valheim_bed_info'),
      query('valheim_map_table_info'),
    ]);

  const players = new Map();
  const player = (m) => {
    const id = m.steam_id || m.player;
    if (!players.has(id)) players.set(id, { name: m.player, x: null, z: null });
    return players.get(id);
  };
  for (const r of pos) {
    const p = player(r.metric);
    if (r.metric.axis === 'x') p.x = Number(r.value[1]);
    if (r.metric.axis === 'z') p.z = Number(r.value[1]);
  }
  for (const r of biome) player(r.metric).biome = Number(r.value[1]);
  for (const r of ping) player(r.metric).ping = Number(r.value[1]);

  return {
    time: Date.now(),
    server: {
      online: num(online),
      fps: num(fps),
      frameMax: num(frameMax),
      zdos: num(zdos),
      exploredKm2: explored.length ? num(explored) / 1e6 : null,
      autosaveIn: num(autosave),
      version: info[0]?.metric.game_version ?? null,
    },
    players: [...players.values()].filter((p) => p.x !== null),
    pins: pins.map((r) => ({
      ...xz(r.metric),
      name: r.metric.name ?? '',
      type: r.metric.type,
      checked: r.metric.checked === 'sim',
      author: r.metric.author ?? '',
    })),
    portals: portals.map((r) => ({
      ...xz(r.metric),
      connected: r.metric.connected === 'sim',
      tag: r.metric.tag ?? '',
      // Onde o par esta, "x, z" arredondado a 1 m pelo plugin.
      target: r.metric.target ? r.metric.target.split(',').map(Number) : null,
    })),
    beds: beds.map((r) => ({ ...xz(r.metric), owner: r.metric.owner ?? '' })),
    tables: tables.map((r) => xz(r.metric)),
  };
}

async function buildHistory() {
  const [online, fps] = await Promise.all([
    queryRange('max_over_time(valheim_players_connected[10m])', 86400, 600),
    queryRange('rate(valheim_server_frame_seconds_count[10m])', 86400, 600),
  ]);
  const series = (r) => (r[0]?.values ?? []).map(([t, v]) => [t, Number(v)]);
  return { online: series(online), fps: series(fps) };
}

// Nome de jogador dentro de um seletor PromQL. So chega aqui nome que o Victoria ja conhece.
const label = (name) => `"${name.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
// Minutos online: uma amostra a cada `every` minutos enquanto o ping do jogador existe. O Victoria
// recusa subconsulta com mais de 100 mil pontos, entao janela longa vai de 5 em 5.
const minutesOnline = (sel, range, every = 1) =>
  `count_over_time((valheim_player_ping_seconds{${sel}} > -1)[${range}:${every}m]) * ${every}`;

async function buildPlayers() {
  const [last, first, week] = await Promise.all([
    query('max by (player) (tlast_over_time(valheim_player_ping_seconds[180d]))'),
    query('min by (player) (tfirst_over_time(valheim_player_ping_seconds[180d]))'),
    query(`sum by (player) (${minutesOnline('', '7d')})`),
  ]);
  const players = new Map();
  const get = (name) => {
    if (!players.has(name)) players.set(name, { name, firstSeen: null, lastSeen: null, minutes7d: 0 });
    return players.get(name);
  };
  for (const r of last) get(r.metric.player).lastSeen = Math.round(Number(r.value[1]) * 1000);
  for (const r of first) get(r.metric.player).firstSeen = Math.round(Number(r.value[1]) * 1000);
  for (const r of week) get(r.metric.player).minutes7d = Number(r.value[1]);
  return [...players.values()].filter((p) => p.name).sort((a, b) => b.lastSeen - a.lastSeen);
}

async function buildPlayer(name) {
  const sel = `player=${label(name)}`;
  const [hourly, total] = await Promise.all([
    queryRange(`sum(${minutesOnline(sel, '1h')})`, 30 * 86400, 3600),
    query(`sum(${minutesOnline(sel, '180d', 5)})`),
  ]);
  return {
    hourly: (hourly[0]?.values ?? []).map(([t, v]) => [t, Number(v)]).filter(([, v]) => v > 0),
    minutesTotal: num(total) ?? 0,
  };
}

// Posicao ao longo do tempo, por jogador: [[t, x, z]] no passo pedido. O jogador offline some da serie.
async function trails(seconds, step, name) {
  const who = name ? `player=${label(name)},` : '';
  const rows = await queryRange(`valheim_player_position_meters{${who}axis=~"x|z"}`, seconds, step);
  const byPlayer = new Map();
  for (const r of rows) {
    const p = r.metric.player;
    if (!byPlayer.has(p)) byPlayer.set(p, new Map());
    const points = byPlayer.get(p);
    for (const [t, v] of r.values) {
      if (!points.has(t)) points.set(t, [t, null, null]);
      points.get(t)[r.metric.axis === 'x' ? 1 : 2] = Math.round(Number(v) * 10) / 10;
    }
  }
  return {
    step,
    players: [...byPlayer].map(([player, points]) => ({
      name: player,
      points: [...points.values()].filter((p) => p[1] !== null && p[2] !== null).sort((a, b) => a[0] - b[0]),
    })),
  };
}

function cached(ttl, build) {
  let value = null;
  let at = 0;
  let pending = null;
  return async () => {
    if (value && Date.now() - at < ttl) return value;
    pending ??= build()
      .then((v) => {
        value = JSON.stringify(v);
        at = Date.now();
        return value;
      })
      .finally(() => {
        pending = null;
      });
    return pending;
  };
}

// Mesmo cache, por chave (jogador, janela de tempo). Poucas chaves vivas: jogadores x janelas fixas.
function cachedBy(ttl, build) {
  const entries = new Map();
  return (key, ...args) => {
    const hit = entries.get(key);
    if (hit && Date.now() - hit.at < ttl) return hit.value;
    const value = build(...args).then((v) => JSON.stringify(v));
    value.catch(() => entries.delete(key));
    if (entries.size > 200) entries.clear();
    entries.set(key, { at: Date.now(), value });
    return value;
  };
}

// Dados do mapa em memoria, refeitos quando os arquivos mudam no disco.
const mapData = new MapData({ mapDir: MAP_DIR, dataDir: DATA, saveDir: SAVE_DIR });
// Baus, camas e construtores do save ao vivo, para as paginas.
const liveWorld = new LiveWorld({ saveDir: SAVE_DIR, mapDir: MAP_DIR });

const hidden = new Hidden(HIDDEN_FILE);
let hiddenPiecesEtag = null;

async function refreshMap() {
  try {
    await mapData.refresh();
  } catch (err) {
    console.error('mapa:', err.message);
  }
  if ((mapData.pieces?.etag ?? null) !== hiddenPiecesEtag) {
    hiddenPiecesEtag = mapData.pieces?.etag ?? null;
    hidden.setPieces(mapData.pieces?.raw ?? null);
  }
  try {
    await liveWorld.refresh();
  } catch (err) {
    console.error('mundo:', err.message);
  }
}

// Historico: um retrato por dia a partir dos backups (archive.mjs).
const archive = BACKUPS_DIR && ARCHIVE_DIR ? new Archive({ backupsDir: BACKUPS_DIR, archiveDir: ARCHIVE_DIR, mapDir: MAP_DIR, world: WORLD }) : null;
const dayTerrain = new Map();

async function runArchive() {
  try {
    await archive.run();
    dayTerrain.clear();
    dayPieces.clear();
  } catch (err) {
    console.error('historico:', err.message);
  }
}

// Minimap.PinType pelo numero, e os tokens de traducao que o servidor dedicado nao resolve.
const PIN_TYPES = ['Icon0', 'Icon1', 'Icon2', 'Icon3', 'Death', 'Bed', 'Icon4', 'Shout', 'None', 'Boss', 'Player',
  'RandomEvent', 'Ping', 'EventArea', 'Hildir1', 'Hildir2', 'Hildir3'];
const PIN_TOKENS = {
  $enemy_eikthyr: 'Eikthyr', $enemy_gdking: 'O Ancião', $enemy_bonemass: 'Massa Óssea', $enemy_dragon: 'Moder',
  $enemy_goblinking: 'Yagluth', $enemy_seekerqueen: 'A Rainha', $enemy_fader: 'Fader',
  $hud_pin_hildir1: 'Baú da Hildir 1', $hud_pin_hildir2: 'Baú da Hildir 2', $hud_pin_hildir3: 'Baú da Hildir 3',
};

async function playerNames() {
  try {
    const text = await readFile(join(MAP_DIR, 'players.tsv'), 'utf8');
    return new Map(text.split('\n').filter(Boolean).map((l) => l.split('\t')));
  } catch {
    return new Map();
  }
}

async function dayPins(date) {
  const names = await playerNames();
  return (await archive.pins(date)).map((p) => ({
    x: Math.round(p.x),
    z: Math.round(p.z),
    name: p.name.startsWith('$') ? PIN_TOKENS[p.name] ?? p.name.slice(1) : p.name,
    type: PIN_TYPES[p.type] ?? String(p.type),
    checked: p.checked,
    author: names.get(p.author.replace(/^Steam_/, '')) ?? '',
  }));
}

// Pecas de cada dia (so x e z), para contar quantas caem na area de uma base.
const dayPieces = new Map();
async function dayPieceXZ(date) {
  if (!dayPieces.has(date)) {
    const gz = await archive.piecesGz(date);
    let xz = null;
    if (gz) {
      const raw = gunzipSync(gz);
      const n = raw.readUInt32LE(4);
      xz = new Float32Array(n * 2);
      for (let k = 0; k < n; k++) {
        xz[k * 2] = raw.readFloatLE(8 + k * 29);
        xz[k * 2 + 1] = raw.readFloatLE(12 + k * 29);
      }
    }
    dayPieces.set(date, xz);
  }
  return dayPieces.get(date);
}

async function daysInArea(v, x0, z0, x1, z1) {
  const out = [];
  for (const d of [...archive.days].reverse()) {
    const xz = await dayPieceXZ(d.date);
    if (!xz) continue;
    let count = 0;
    const hides = v.overlapsArea([x0, z0, x1, z1]);
    for (let k = 0; k < xz.length; k += 2) {
      if (xz[k] >= x0 && xz[k] <= x1 && xz[k + 1] >= z0 && xz[k + 1] <= z1 && !(hides && v.inArea(xz[k], xz[k + 1]))) count++;
    }
    out.push({ date: d.date, pieces: count });
  }
  return out;
}

async function dayTerrainPacked(date) {
  if (!dayTerrain.has(date)) {
    if (!mapData.full) return null;
    if (dayTerrain.size >= 4) dayTerrain.delete(dayTerrain.keys().next().value);
    dayTerrain.set(date, maskTerrain(mapData.full, await archive.explored(date)));
  }
  return dayTerrain.get(date);
}

function sendJson(res, value, status = 200) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(value));
}

// `personal`: a resposta depende do cookie (escondidos); nenhum cache no caminho pode guardar.
function sendPacked(req, res, packed, personal = false) {
  if (!packed) return notFound(res);
  const headers = {
    'Content-Type': 'application/octet-stream',
    'Content-Encoding': 'gzip',
    'Cache-Control': personal ? 'private, no-cache' : 'no-cache',
    ETag: packed.etag,
  };
  if (personal) headers.Vary = 'Cookie';
  if (req.headers['if-none-match'] === packed.etag) {
    res.writeHead(304, headers);
    return res.end();
  }
  res.writeHead(200, { ...headers, 'Content-Length': packed.gz.length });
  res.end(packed.gz);
}

// Variantes filtradas por quem pede (escondidos), guardadas pela chave do que foi tirado.
const variants = new Map();
function variant(key, make) {
  if (!variants.has(key)) {
    if (variants.size >= 32) variants.delete(variants.keys().next().value);
    variants.set(key, make());
  }
  return variants.get(key);
}

function packJson(value) {
  const gz = gzipSync(JSON.stringify(value));
  return { gz, etag: `"h-${createHash('sha1').update(gz).digest('hex').slice(0, 16)}"` };
}

function packPieces(raw) {
  const gz = gzipSync(raw, { level: 6 });
  return { gz, etag: `"p-${createHash('sha1').update(gz).digest('hex').slice(0, 16)}"` };
}

// Construcoes (agora ou de um dia) sem as bases que os outros esconderam.
function piecesFor(v, source, key) {
  if (!source || !v.areas.length) return source && { gz: source.gz, etag: source.etag };
  return variant(`pieces|${key}|${hidden.version}|${v.key}`, () => packPieces(filterPieces(source.raw ?? gunzipSync(source.gz), v)));
}

async function readBody(req, limit = 4096) {
  let size = 0;
  const chunks = [];
  for await (const c of req) {
    size += c.length;
    if (size > limit) throw Object.assign(new Error('grande demais'), { status: 413 });
    chunks.push(c);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
}

const fingerprint = (body) => (typeof body.fp === 'string' && /^[a-f0-9]{64}$/.test(body.fp) ? body.fp : null);

// POST /api/me, /api/hide, /api/unhide e GET /api/hidden. So JSON: formulario de outro site nao chega aqui.
async function hiddenApi(req, res, url, viewer) {
  if (url.pathname === '/api/hidden') return sendJson(res, viewer ? hidden.mine(viewer) : []);
  if (req.method !== 'POST' || !(req.headers['content-type'] ?? '').startsWith('application/json')) {
    res.writeHead(405, { 'Content-Type': 'text/plain' });
    return res.end('POST com JSON');
  }
  const body = await readBody(req);
  const fp = fingerprint(body);
  if (url.pathname === '/api/me') {
    const me = hidden.identify(viewer, fp);
    res.setHeader('Set-Cookie', cookieHeader(me.id, req));
    return sendJson(res, { recovered: me.recovered, hides: hidden.mine(me.id) });
  }
  if (!viewer) return sendJson(res, { error: 'sem identidade' }, 401);
  if (url.pathname === '/api/hide') {
    try {
      return sendJson(res, hidden.add(viewer, fp, req.headers['user-agent'], body.hide));
    } catch (err) {
      return sendJson(res, { error: err.message }, 400);
    }
  }
  if (url.pathname === '/api/unhide') {
    return hidden.remove(viewer, String(body.id ?? '')) ? sendJson(res, { ok: true }) : sendJson(res, { error: 'nao e seu' }, 404);
  }
  return notFound(res);
}

const state = cached(STATE_TTL_MS, buildState);
const history = cached(HISTORY_TTL_MS, buildHistory);
const players = cached(HISTORY_TTL_MS, buildPlayers);
const player = cachedBy(HISTORY_TTL_MS, buildPlayer);
const trail = cachedBy(30000, trails);

// Janelas de rastro aceitas, em horas, e o passo de cada uma (segundos).
const TRAIL_STEPS = { 1: 10, 6: 30, 24: 60, 72: 180, 168: 300 };

async function knownPlayer(name) {
  if (!name) return false;
  return JSON.parse(await players()).some((p) => p.name === name);
}

// Arquivo com revalidacao por ETag: o navegador pergunta sempre e so baixa o que mudou.
async function sendFile(req, res, path) {
  let info;
  try {
    info = await stat(path);
  } catch {
    return notFound(res);
  }
  if (!info.isFile()) return notFound(res);
  const etag = `"${info.size.toString(36)}-${Math.floor(info.mtimeMs).toString(36)}"`;
  const headers = {
    'Content-Type': TYPES[extname(path)] || 'application/octet-stream',
    'Cache-Control': 'no-cache',
    ETag: etag,
  };
  if (req.headers['if-none-match'] === etag) {
    res.writeHead(304, headers);
    return res.end();
  }
  headers['Content-Length'] = info.size;
  res.writeHead(200, headers);
  createReadStream(path).pipe(res);
}

// A Cloudflare troca o no-cache de .js/.css por 4 h de cache no navegador; so o HTML chega sempre
// fresco. Entao a pagina aponta para o codigo com a versao no endereco, e os modulos importados entre
// si passam pelo mesmo mapa de importacao.
// Paginas: todas usam <base href="/">, entao /jogador/<nome> e /base/<x>,<z> acham o codigo na raiz.
async function sendPage(res, page) {
  let html = await readFile(join(PUBLIC, page), 'utf8');
  const v = {};
  for (const name of (await readdir(PUBLIC)).filter((n) => n.endsWith('.js') || n.endsWith('.css'))) {
    v[name] = createHash('sha1').update(await readFile(join(PUBLIC, name))).digest('hex').slice(0, 10);
  }
  const imports = Object.fromEntries(Object.keys(v).filter((n) => n.endsWith('.js')).map((n) => [`./${n}`, `./${n}?v=${v[n]}`]));
  html = html
    .replace(/href="([\w-]+\.css)"/g, (m, n) => (v[n] ? `href="${n}?v=${v[n]}"` : m))
    .replace(/<script type="module" src="([\w-]+\.js)"><\/script>/,
      (m, n) => `<script type="importmap">${JSON.stringify({ imports })}</script>\n  <script type="module" src="${n}?v=${v[n]}"></script>`);
  res.writeHead(200, { 'Content-Type': TYPES['.html'], 'Cache-Control': 'no-cache' });
  res.end(html);
}

const sendText = (res, body) => {
  res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(body);
};

function notFound(res) {
  res.writeHead(404, { 'Content-Type': 'text/plain' });
  res.end('not found');
}

function inside(base, rel) {
  const full = normalize(join(base, rel));
  return full.startsWith(base) ? full : null;
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  // Quem pede, para os escondidos. As rotas .bin (que a Cloudflare pode guardar) usam a vista de ninguem.
  const viewer = readCookie(req);
  const v = hidden.view(viewer);
  const anon = hidden.view(null);
  try {
    if (['/api/me', '/api/hide', '/api/unhide', '/api/hidden'].includes(url.pathname)) return await hiddenApi(req, res, url, viewer);
    if (url.pathname === '/api/state') {
      const body = await state();
      return v.empty ? sendText(res, body) : sendJson(res, filterState(JSON.parse(body), v));
    }
    if (url.pathname === '/api/history') return sendText(res, await history());
    if (url.pathname === '/healthz') {
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      return res.end('ok');
    }
    if (url.pathname === '/api/days') return sendJson(res, archive ? archive.days : []);
    if (url.pathname === '/api/world') {
      if (v.empty || !liveWorld.value) return sendPacked(req, res, liveWorld.json, true);
      return sendPacked(req, res, variant(`world|${liveWorld.json.etag}|${hidden.version}|${v.key}`, () => packJson(filterWorld(liveWorld.value, v))), true);
    }
    if (url.pathname === '/api/players') return sendText(res, await players());
    if (url.pathname === '/api/player') {
      const name = url.searchParams.get('name');
      if (!(await knownPlayer(name))) return notFound(res);
      return sendText(res, await player(name, name));
    }
    if (url.pathname === '/api/trails') {
      const hours = Number(url.searchParams.get('hours'));
      const step = TRAIL_STEPS[hours];
      const name = url.searchParams.get('name') || null;
      if (!step || (name && !(await knownPlayer(name)))) return notFound(res);
      if (name && v.players.has(name)) return sendJson(res, { step, players: [] });
      const body = await trail(`${hours}|${name ?? ''}`, hours * 3600, step, name);
      return v.empty ? sendText(res, body) : sendJson(res, filterTrails(JSON.parse(body), v));
    }
    if (url.pathname === '/api/days/area') {
      const box = ['x0', 'z0', 'x1', 'z1'].map((k) => Number(url.searchParams.get(k)));
      if (!archive || box.some((v) => !Number.isFinite(v))) return notFound(res);
      return sendJson(res, await daysInArea(v, ...box));
    }
    const day = url.pathname.match(/^\/(?:api|data)\/days\/(\d{4}-\d{2}-\d{2})\/(pins|terrain\.bin|pieces\.bin|pieces)$/);
    if (day) {
      const [, date, what] = day;
      if (!archive?.has(date)) return notFound(res);
      if (what === 'pins') return sendJson(res, filterPins(await dayPins(date), v));
      if (what === 'terrain.bin') return sendPacked(req, res, await dayTerrainPacked(date));
      const gz = await archive.piecesGz(date);
      const source = gz && { gz, etag: `"p-${date}-${gz.length}"` };
      // pieces (sem extensao) e por quem pede; pieces.bin e a vista de ninguem.
      return what === 'pieces' ? sendPacked(req, res, piecesFor(v, source, date), true) : sendPacked(req, res, piecesFor(anon, source, date));
    }
    if (url.pathname === '/data/terrain.bin') return sendPacked(req, res, mapData.terrain);
    if (url.pathname === '/data/pieces.bin') return sendPacked(req, res, piecesFor(anon, mapData.pieces, mapData.pieces?.etag));
    if (url.pathname === '/api/pieces') return sendPacked(req, res, piecesFor(v, mapData.pieces, mapData.pieces?.etag), true);
    if (url.pathname === '/' || url.pathname === '/index.html') return await sendPage(res, 'index.html');
    if (url.pathname.startsWith('/jogador/')) return await sendPage(res, 'player.html');
    if (url.pathname.startsWith('/base/')) return await sendPage(res, 'base.html');
    const rel = decodeURIComponent(url.pathname.slice(1));
    const path = inside(PUBLIC, rel);
    if (!path) return notFound(res);
    return sendFile(req, res, path);
  } catch (err) {
    console.error(url.pathname, err.message);
    if (err.status) return sendJson(res, { error: err.message }, err.status);
    if (err instanceof SyntaxError) return sendJson(res, { error: 'json' }, 400);
    res.writeHead(502, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'upstream' }));
  }
});

await hidden.load();
await refreshMap();
setInterval(refreshMap, MAP_CHECK_MS).unref();
if (archive) {
  runArchive();
  setInterval(runArchive, ARCHIVE_EVERY_MS).unref();
}
server.listen(PORT, () => console.log(`valheim-site em :${PORT} (victoria ${VM}, plugin ${MAP_DIR ?? '-'}, save ${SAVE_DIR ?? '-'})`));
