// Site do servidor: mapa no estilo do jogo + metricas basicas. Sem dependencias.
// Estatico em public/, dados do mapa recortados pelo explorado (terrain.mjs) e /api/* com
// consultas fixas ao VictoriaMetrics (nada de PromQL vindo do navegador).
import { createServer } from 'node:http';
import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { MapData } from './terrain.mjs';

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

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
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
    portals: portals.map((r) => ({ ...xz(r.metric), connected: r.metric.connected === 'sim', tag: r.metric.tag ?? '' })),
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

// Dados do mapa em memoria, refeitos quando os arquivos mudam no disco.
const mapData = new MapData({ mapDir: MAP_DIR, dataDir: DATA, saveDir: SAVE_DIR });

async function refreshMap() {
  try {
    await mapData.refresh();
  } catch (err) {
    console.error('mapa:', err.message);
  }
}

function sendPacked(req, res, packed) {
  if (!packed) return notFound(res);
  const headers = {
    'Content-Type': 'application/octet-stream',
    'Content-Encoding': 'gzip',
    'Cache-Control': 'no-cache',
    ETag: packed.etag,
  };
  if (req.headers['if-none-match'] === packed.etag) {
    res.writeHead(304, headers);
    return res.end();
  }
  res.writeHead(200, { ...headers, 'Content-Length': packed.gz.length });
  res.end(packed.gz);
}

const state = cached(STATE_TTL_MS, buildState);
const history = cached(HISTORY_TTL_MS, buildHistory);

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
  try {
    if (url.pathname === '/api/state' || url.pathname === '/api/history') {
      const body = await (url.pathname === '/api/state' ? state() : history());
      res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
      return res.end(body);
    }
    if (url.pathname === '/healthz') {
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      return res.end('ok');
    }
    if (url.pathname === '/data/terrain.bin') return sendPacked(req, res, mapData.terrain);
    if (url.pathname === '/data/pieces.bin') return sendPacked(req, res, mapData.pieces);
    const rel = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
    const path = inside(PUBLIC, rel);
    if (!path) return notFound(res);
    return sendFile(req, res, path);
  } catch (err) {
    console.error(url.pathname, err.message);
    res.writeHead(502, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'upstream' }));
  }
});

await refreshMap();
setInterval(refreshMap, MAP_CHECK_MS).unref();
server.listen(PORT, () => console.log(`valheim-site em :${PORT} (victoria ${VM}, plugin ${MAP_DIR ?? '-'}, save ${SAVE_DIR ?? '-'})`));
