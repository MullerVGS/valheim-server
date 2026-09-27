import { MapRenderer, parseTerrain, sunDirection, WORLD_SIZE } from './mapgl.js';
import { groupSettlements, KINDS, parsePieces, pieceAt } from './pieces.js';

const GAME = 'game';
const STATE_EVERY_MS = 10000;
const HISTORY_EVERY_MS = 300000;
const MIN_MPP = 0.25; // zoom maximo do jogo: 0,015 do mapa na tela
const MAX_MPP = 40;
const LABELS_UNTIL_MPP = 9;

// Luz de um dia limpo no prado, em valores de inspetor (sRGB); o renderer lineariza.
const DAY = {
  sunDir: sunDirection(0.42),
  sunColor: [0.72, 0.74, 0.78, 1],
  ambientColor: [0.52, 0.57, 0.7, 1],
  sunFogColor: [0.7, 0.7, 0.72, 1],
};

// Indice do terrain.bin -> cor do Minimap. O prefab sobrescreve as do codigo; terras nebulosas
// e oceano nao sao serializados.
function biomeColors(art) {
  const b = art.biomes;
  const list = [[1, 1, 1], b.meadows, b.swamp, b.mountain, b.blackforest, b.heath, b.ashlands, b.deepnorth, [0.2, 0.2, 0.2]];
  return list.map((c) => c.map((v) => Math.round(v * 255)));
}

const BIOME_NAMES = {
  1: 'Prado', 2: 'Pântano', 4: 'Montanha', 8: 'Floresta Negra', 16: 'Planície', 32: 'Terras das Cinzas',
  64: 'Extremo Norte', 256: 'Oceano', 512: 'Terras Nebulosas',
};

// Minimap.PinType -> icone
const PIN_ICONS = { Icon0: 'fire', Icon1: 'house', Icon2: 'hammer', Icon3: 'pin', Icon4: 'portal', Boss: 'boss' };

const $ = (id) => document.getElementById(id);
const mapCanvas = $('map');
const overlay = $('overlay');
const ctx = overlay.getContext('2d');

const view = { x: -44, z: -68, metersPerPixel: 3.2, pixelRatio: 1 };
const layers = { pieces: true, pins: true, labels: true, portals: false, beds: false, players: true };
let state = null;
let renderer = null;
// Dia do historico em exibicao (null = agora, ao vivo) e os pins daquele dia.
let day = null;
let dayPins = [];
let biomeTable = null;
let icons = {};
let pieces = null;
let settlements = null;
let hits = [];
let dirty = true;

function readHash() {
  const m = location.hash.match(/^#(-?[\d.]+),(-?[\d.]+),([\d.]+)$/);
  if (!m) return;
  view.x = Number(m[1]);
  view.z = Number(m[2]);
  view.metersPerPixel = clampMpp(Number(m[3]));
}

let hashTimer = 0;
function writeHash() {
  clearTimeout(hashTimer);
  hashTimer = setTimeout(() => {
    history.replaceState(null, '', `#${view.x.toFixed(0)},${view.z.toFixed(0)},${view.metersPerPixel.toFixed(2)}`);
  }, 250);
}

const clampMpp = (v) => Math.min(MAX_MPP, Math.max(MIN_MPP, v));

function resize() {
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  view.pixelRatio = dpr;
  for (const c of [mapCanvas, overlay]) {
    c.width = Math.round(c.clientWidth * dpr);
    c.height = Math.round(c.clientHeight * dpr);
  }
  dirty = true;
}

function toScreen(x, z) {
  return [
    mapCanvas.clientWidth / 2 + (x - view.x) / view.metersPerPixel,
    mapCanvas.clientHeight / 2 - (z - view.z) / view.metersPerPixel,
  ];
}

function toWorld(sx, sy) {
  return [
    view.x + (sx - mapCanvas.clientWidth / 2) * view.metersPerPixel,
    view.z - (sy - mapCanvas.clientHeight / 2) * view.metersPerPixel,
  ];
}

function zoomAt(sx, sy, factor) {
  const [wx, wz] = toWorld(sx, sy);
  view.metersPerPixel = clampMpp(view.metersPerPixel * factor);
  const [nx, nz] = toWorld(sx, sy);
  view.x += wx - nx;
  view.z += wz - nz;
  changed();
}

function changed() {
  const half = WORLD_SIZE / 2;
  view.x = Math.max(-half, Math.min(half, view.x));
  view.z = Math.max(-half, Math.min(half, view.z));
  dirty = true;
  writeHash();
}

// Arrastar, roda do mouse, pinca.
function bindInput() {
  const pointers = new Map();
  let pinch = null;
  mapCanvas.addEventListener('pointerdown', (e) => {
    mapCanvas.setPointerCapture(e.pointerId);
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    mapCanvas.classList.add('dragging');
    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      pinch = { dist: Math.hypot(a.x - b.x, a.y - b.y) };
    }
  });
  mapCanvas.addEventListener('pointermove', (e) => {
    const prev = pointers.get(e.pointerId);
    if (!prev) {
      hover(e.clientX, e.clientY);
      return;
    }
    const cur = { x: e.clientX, y: e.clientY };
    pointers.set(e.pointerId, cur);
    if (pointers.size === 2 && pinch) {
      const [a, b] = [...pointers.values()];
      const dist = Math.hypot(a.x - b.x, a.y - b.y);
      if (dist > 0) zoomAt((a.x + b.x) / 2, (a.y + b.y) / 2, pinch.dist / dist);
      pinch.dist = dist;
      return;
    }
    if (pointers.size === 1) {
      view.x -= (cur.x - prev.x) * view.metersPerPixel;
      view.z += (cur.y - prev.y) * view.metersPerPixel;
      changed();
    }
  });
  const up = (e) => {
    pointers.delete(e.pointerId);
    if (pointers.size < 2) pinch = null;
    if (!pointers.size) mapCanvas.classList.remove('dragging');
  };
  mapCanvas.addEventListener('pointerup', up);
  mapCanvas.addEventListener('pointercancel', up);
  mapCanvas.addEventListener('pointerleave', () => hideTooltip());
  mapCanvas.addEventListener(
    'wheel',
    (e) => {
      e.preventDefault();
      const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
      zoomAt(e.clientX, e.clientY, Math.exp(delta * 0.0015));
    },
    { passive: false },
  );
  mapCanvas.addEventListener('dblclick', (e) => zoomAt(e.clientX, e.clientY, 0.5));
  window.addEventListener('resize', resize);

  for (const input of document.querySelectorAll('#layers input')) {
    layers[input.dataset.layer] = input.checked;
    input.addEventListener('change', () => {
      layers[input.dataset.layer] = input.checked;
      if (renderer) renderer.showPieces = layers.pieces;
      dirty = true;
    });
  }
  const panel = $('panel');
  $('panel-toggle').addEventListener('click', () => {
    const collapsed = panel.classList.toggle('collapsed');
    $('panel-toggle').setAttribute('aria-expanded', String(!collapsed));
  });
  if (window.matchMedia('(max-width: 720px)').matches) {
    panel.classList.add('collapsed');
    $('panel-toggle').setAttribute('aria-expanded', 'false');
  }
}

function hover(sx, sy) {
  const [wx, wz] = toWorld(sx, sy);
  $('coords').textContent = `x ${wx.toFixed(0)}, z ${wz.toFixed(0)}`;
  const hit = hits.find((h) => Math.abs(h.sx - sx) <= h.r && Math.abs(h.sy - sy) <= h.r) ?? pieceHit(wx, wz);
  if (!hit) return hideTooltip();
  const tip = $('tooltip');
  tip.innerHTML = '';
  const title = document.createElement('strong');
  title.textContent = hit.title;
  tip.append(title);
  for (const line of hit.lines) {
    const s = document.createElement('span');
    s.textContent = line;
    tip.append(s, document.createElement('br'));
  }
  tip.hidden = false;
  const x = Math.min(sx + 14, window.innerWidth - tip.offsetWidth - 8);
  const y = Math.min(sy + 14, window.innerHeight - tip.offsetHeight - 8);
  tip.style.left = `${x}px`;
  tip.style.top = `${y}px`;
}

const PLACE_PINS = new Set(['Icon0', 'Icon1', 'Icon2', 'Icon3']);

// Construcao sob o mouse: material da peca e a base a que ela pertence.
function pieceHit(wx, wz) {
  if (!pieces || !layers.pieces) return null;
  const i = pieceAt(pieces, settlements, wx, wz, view.metersPerPixel * 0.75);
  if (i < 0) return null;
  const g = settlements.groups[settlements.pieceGroup[i]];
  const kind = KINDS[pieces.kind[i]];
  const pin = state?.pins.find(
    (p) => PLACE_PINS.has(p.type) && p.name && p.x >= g.minX - 15 && p.x <= g.maxX + 15 && p.z >= g.minZ - 15 && p.z <= g.maxZ + 15,
  );
  const mix = g.kinds
    .map((c, k) => [c, k])
    .filter(([c]) => c / g.count >= 0.08)
    .sort((a, b) => b[0] - a[0])
    .map(([c, k]) => `${KINDS[k].name} ${Math.round((c / g.count) * 100)}%`);
  const lines = [
    g.count === 1 ? 'peça solta' : `${fmt.format(g.count)} peças`,
    mix.join(' · '),
    `aqui: ${kind.name}`,
  ];
  return { title: pin ? pin.name : g.count >= 40 ? 'Base' : 'Construção', lines };
}

function hideTooltip() {
  $('tooltip').hidden = true;
}

function loadIcon(name) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => resolve([name, img]);
    img.onerror = () => resolve([name, null]);
    img.src = `${GAME}/icons/${name}.png`;
  });
}

// Camada de cima: marcacoes, portais, camas e jogadores, com a fonte e os icones do jogo.
function drawOverlay() {
  const dpr = view.pixelRatio;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, overlay.clientWidth, overlay.clientHeight);
  hits = [];
  if (!state) return;
  const w = overlay.clientWidth;
  const h = overlay.clientHeight;
  const showLabels = layers.labels && view.metersPerPixel <= LABELS_UNTIL_MPP;
  const boxes = [];
  const iconSize = view.metersPerPixel > 12 ? 16 : view.metersPerPixel > 5 ? 20 : 26;

  const visible = (sx, sy) => sx > -40 && sy > -40 && sx < w + 40 && sy < h + 40;
  const icon = (name, sx, sy, size, alpha = 1) => {
    const img = icons[name];
    if (!img) return;
    const k = size / Math.max(img.width, img.height);
    ctx.globalAlpha = alpha;
    ctx.drawImage(img, sx - (img.width * k) / 2, sy - (img.height * k) / 2, img.width * k, img.height * k);
    ctx.globalAlpha = 1;
  };
  const label = (text, sx, sy, size, force = false) => {
    if (!text) return;
    ctx.font = `700 ${size}px Norse, "Averia Serif Libre", serif`;
    const tw = ctx.measureText(text).width;
    const box = [sx - tw / 2 - 2, sy - 2, sx + tw / 2 + 2, sy + size + 2];
    if (!force && boxes.some((b) => box[0] < b[2] && box[2] > b[0] && box[1] < b[3] && box[3] > b[1])) return;
    boxes.push(box);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 3;
    ctx.strokeStyle = 'rgba(0,0,0,0.75)';
    ctx.strokeText(text, sx, sy);
    ctx.fillStyle = '#f4efe6';
    ctx.fillText(text, sx, sy);
  };

  ctx.shadowColor = 'rgba(0,0,0,0.8)';
  ctx.shadowBlur = 3;

  if (layers.beds) {
    for (const b of day ? [] : state.beds) {
      const [sx, sy] = toScreen(b.x, b.z);
      if (!visible(sx, sy)) continue;
      icon('bed', sx, sy, iconSize * 0.75, 0.9);
      hits.push({ sx, sy, r: iconSize * 0.4, title: 'Cama', lines: [b.owner ? `de ${b.owner}` : ''] });
    }
  }
  if (layers.portals) {
    for (const p of day ? [] : state.portals) {
      const [sx, sy] = toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      icon('portal', sx, sy, iconSize * 0.8, p.connected ? 1 : 0.55);
      hits.push({
        sx, sy, r: iconSize * 0.4, title: p.tag || 'Portal',
        lines: [p.connected ? 'conectado' : 'sem par'],
      });
    }
  }

  const labelQueue = [];
  if (layers.pins) {
    for (const p of day ? dayPins : state.pins) {
      const [sx, sy] = toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      const name = PIN_ICONS[p.type] ?? 'pin';
      icon(name, sx, sy, iconSize, p.checked ? 0.6 : 1);
      if (p.checked) icon('checked', sx, sy, iconSize * 0.9);
      hits.push({ sx, sy, r: iconSize * 0.45, title: p.name || 'Marcação', lines: [p.author ? `por ${p.author}` : '', p.checked ? 'riscada' : ''].filter(Boolean) });
      if (showLabels) labelQueue.push([p.name, sx, sy + iconSize * 0.45, 15]);
    }
  }

  ctx.shadowBlur = 0;
  // Jogadores por cima de tudo, com nome sempre.
  const playerLabels = [];
  if (layers.players) {
    ctx.shadowBlur = 4;
    for (const p of day ? [] : state.players) {
      const [sx, sy] = toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      icon('player_32', sx, sy, iconSize + 4);
      hits.push({ sx, sy, r: iconSize * 0.5, title: p.name, lines: [BIOME_NAMES[p.biome] ?? '', p.ping != null ? `ping ${(p.ping * 1000).toFixed(0)} ms` : ''].filter(Boolean) });
      playerLabels.push([p.name, sx, sy + iconSize * 0.55 + 2, 17]);
    }
    ctx.shadowBlur = 0;
  }
  for (const l of playerLabels) label(...l, true);
  for (const l of labelQueue) label(...l);
}

function drawHud() {
  const target = 120;
  const meters = target * view.metersPerPixel;
  const pow = Math.pow(10, Math.floor(Math.log10(meters)));
  const nice = [1, 2, 5, 10].map((k) => k * pow).filter((v) => v <= meters).pop();
  $('scale-bar').style.width = `${nice / view.metersPerPixel}px`;
  $('scale-text').textContent = nice >= 1000 ? `${nice / 1000} km` : `${nice} m`;
}

function frame(now) {
  if (renderer) {
    const t = now / 1000;
    const env = { ...DAY, cloudOffset: [t * 0.0012, 0, t * 0.0007] };
    renderer.draw(view, env, t);
  }
  if (dirty) {
    drawOverlay();
    drawHud();
    dirty = false;
  }
  requestAnimationFrame(frame);
}

const fmt = new Intl.NumberFormat('pt-BR');

function renderPanel() {
  const s = state.server;
  const status = $('status');
  const online = s.online ?? 0;
  status.className = 'status online';
  $('status-text').textContent = online === 1 ? '1 viking online' : `${online} vikings online`;
  $('s-fps').textContent = s.fps != null ? s.fps.toFixed(0) : '–';
  $('s-frame').textContent = s.frameMax != null ? `${(s.frameMax * 1000).toFixed(0)} ms` : '–';
  $('s-zdos').textContent = s.zdos != null ? fmt.format(s.zdos) : '–';
  $('s-explored').textContent = s.exploredKm2 != null ? `${s.exploredKm2.toFixed(1)} km²` : '–';
  $('s-version').textContent = s.version ?? '–';
  saveAt = s.autosaveIn != null ? Date.now() + s.autosaveIn * 1000 : null;
  tickSave();

  const list = $('players');
  list.innerHTML = '';
  if (!state.players.length) {
    const li = document.createElement('li');
    li.className = 'empty';
    li.textContent = 'Ninguém no mundo agora.';
    list.append(li);
  }
  for (const p of [...state.players].sort((a, b) => a.name.localeCompare(b.name))) {
    const li = document.createElement('li');
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = p.name;
    const ping = document.createElement('span');
    ping.className = 'ping';
    ping.textContent = p.ping != null ? `${(p.ping * 1000).toFixed(0)} ms` : '';
    const where = document.createElement('span');
    where.className = 'where';
    where.textContent = `${BIOME_NAMES[p.biome] ?? '—'} · ${p.x.toFixed(0)}, ${p.z.toFixed(0)}`;
    li.append(name, ping, where);
    li.addEventListener('click', () => {
      view.x = p.x;
      view.z = p.z;
      view.metersPerPixel = Math.min(view.metersPerPixel, 1.5);
      changed();
    });
    list.append(li);
  }
}

let saveAt = null;
function tickSave() {
  if (saveAt == null) return;
  const left = Math.max(0, Math.round((saveAt - Date.now()) / 1000));
  $('s-save').textContent = `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
}

function renderSpark(series) {
  const svg = $('spark-online');
  if (!series.length) return (svg.innerHTML = '');
  const max = Math.max(1, ...series.map(([, v]) => v));
  const t0 = series[0][0];
  const t1 = series[series.length - 1][0];
  const pts = series.map(([t, v]) => [((t - t0) / Math.max(1, t1 - t0)) * 240, 42 - (v / max) * 38]);
  const line = pts.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`).join('');
  svg.innerHTML =
    `<path d="${line}L240,44L0,44Z" fill="rgba(242,163,58,0.18)"/>` +
    `<path d="${line}" fill="none" stroke="#f2a33a" stroke-width="1.5" vector-effect="non-scaling-stroke"/>` +
    `<text x="238" y="10" text-anchor="end" font-size="10" fill="#b9a88f">máx ${max}</text>`;
}

async function pollState() {
  try {
    const res = await fetch('api/state', { cache: 'no-store' });
    if (!res.ok) throw new Error(res.status);
    state = await res.json();
    renderPanel();
    dirty = true;
  } catch {
    $('status').className = 'status offline';
    $('status-text').textContent = 'sem notícias do servidor';
  }
  setTimeout(pollState, STATE_EVERY_MS);
}

async function pollHistory() {
  try {
    const res = await fetch('api/history', { cache: 'no-store' });
    if (res.ok) renderSpark((await res.json()).online);
  } catch {}
  setTimeout(pollHistory, HISTORY_EVERY_MS);
}

// Terreno e construcoes de agora ou de um dia do historico, trocados no renderer ja montado.
async function loadWorld(r, date) {
  const base = date ? `data/days/${date}/` : 'data/';
  const [terrainBuf, piecesBuf] = await Promise.all([
    // Sempre revalida: a Cloudflare manda o navegador guardar .bin por 4 h.
    fetch(base + 'terrain.bin', { cache: 'no-cache' }).then((res) => {
      if (!res.ok) throw new Error(`terreno ${res.status}`);
      return res.arrayBuffer();
    }),
    // Sem construcoes o mapa abre do mesmo jeito.
    fetch(base + 'pieces.bin', { cache: 'no-cache' })
      .then((res) => (res.ok ? res.arrayBuffer() : null))
      .catch(() => null),
  ]);
  pieces = piecesBuf && r.pieces ? parsePieces(piecesBuf) : null;
  settlements = pieces ? groupSettlements(pieces) : [];
  if (r.pieces) r.setPieces(pieces ?? parsePieces(emptyPieces()));
  r.setTerrain(parseTerrain(terrainBuf), biomeTable);
}

function emptyPieces() {
  const b = new ArrayBuffer(8);
  new Uint8Array(b).set([86, 80, 67, 49]);
  return b;
}

const fmtDay = (d) => `${d.date.slice(8, 10)}/${d.date.slice(5, 7)}`;

async function setupTimeline() {
  let days = [];
  try {
    days = await (await fetch('api/days', { cache: 'no-store' })).json();
  } catch {}
  const select = $('day');
  const info = $('day-info');
  if (!days.length) {
    info.textContent = 'Nenhum dia guardado ainda.';
    select.disabled = true;
    return;
  }
  for (const d of days) {
    const opt = document.createElement('option');
    opt.value = d.date;
    opt.textContent = `${fmtDay(d)} às ${d.time}`;
    select.append(opt);
  }
  const describe = () => {
    const d = days.find((x) => x.date === select.value);
    info.textContent = d
      ? `${d.exploredKm2.toFixed(1)} km² explorados · ${d.pieces != null ? fmt.format(d.pieces) : '–'} construções · ${d.pins} marcações`
      : 'Mapa ao vivo, com jogadores, portais e camas.';
  };
  describe();
  select.addEventListener('change', async () => {
    const date = select.value || null;
    select.disabled = true;
    try {
      if (date) dayPins = await (await fetch(`api/days/${date}/pins`)).json();
      if (renderer) await loadWorld(renderer, date);
      day = date;
      document.body.classList.toggle('past', !!date);
    } catch (err) {
      console.error(err);
    }
    select.disabled = false;
    describe();
    dirty = true;
  });
}

async function main() {
  readHash();
  resize();
  bindInput();
  pollState();
  pollHistory();
  setupTimeline();
  setInterval(tickSave, 1000);
  requestAnimationFrame(frame);
  try {
    const art = await (await fetch(`${GAME}/art.json`)).json();
    const r = new MapRenderer(mapCanvas);
    biomeTable = biomeColors(art);
    const [, iconList] = await Promise.all([
      r.loadArt(GAME, art),
      Promise.all(['fire', 'house', 'hammer', 'pin', 'portal', 'bed', 'checked', 'player_32', 'boss'].map(loadIcon)),
    ]);
    await loadWorld(r, null);
    r.showPieces = layers.pieces;
    icons = Object.fromEntries(iconList);
    await document.fonts.load('700 16px Norse');
    renderer = r;
    dirty = true;
    $('loading').classList.add('done');
  } catch (err) {
    console.error(err);
    const el = $('loading');
    el.classList.add('error');
    el.textContent = `Não deu para abrir o mapa: ${err.message}`;
  }
}

main();
