import { KINDS, pieceAt } from './pieces.js';
import { MapView } from './mapview.js';
import {
  $, ago, BASE_MARGIN, BASE_MIN_PIECES, baseAt, baseHref, baseName, BIOME_NAMES, buildersIn, columnChart,
  containerTitle, dayMonth, duration, el, fmt, getJSON, inBox, itemGrid, itemIcon, itemName, loadItems,
  materials, normalize, PIN_ICONS, playerHref,
} from './common.js';

const STATE_EVERY_MS = 10000;
const HISTORY_EVERY_MS = 300000;
const WORLD_EVERY_MS = 120000;
const TRAILS_EVERY_MS = 60000;
const LABELS_UNTIL_MPP = 9;
// Baus aparecem so de perto; os achados da busca aparecem sempre.
const CHESTS_UNTIL_MPP = 2.5;
const PLAY_STEP_MS = 1400;
// Recorte do par no cartao do portal: tamanho em px CSS e zoom.
const PAIR_VIEW = { width: 272, height: 150, metersPerPixel: 1.2 };

const layers = { pieces: true, pins: true, labels: true, portals: false, beds: false, players: true, trails: false, chests: false };
let state = null;
let world = null; // /api/world: baus, camas, construtores
let items = null;
let knownPlayers = [];
let trailHours = 6;
let trailData = null;
// Dia do historico em exibicao (null = agora, ao vivo) e os pins daquele dia.
let day = null;
let dayPins = [];
let hits = [];
// Cartao aberto: { anchor: [x, z], render: () => Node, portal? }.
let card = null;
// Portal sob o ponteiro: a linha ate o par aparece ja no hover.
let hoveredPortal = null;
// Busca nos baus: hash do item escolhido.
let found = null;

const hudCoords = $('coords');
const mapView = new MapView({
  map: $('map'),
  overlay: $('overlay'),
  view: { x: -44, z: -68, metersPerPixel: 3.2 },
  icons: ['fire', 'house', 'hammer', 'pin', 'portal', 'bed', 'checked', 'player_32', 'boss', 'death'],
  onDraw: drawOverlay,
  onHover: hover,
  onClick: click,
  onChange: writeHash,
});

function readHash() {
  const m = location.hash.match(/^#(-?[\d.]+),(-?[\d.]+),([\d.]+)$/);
  if (m) mapView.goTo(Number(m[1]), Number(m[2]), Number(m[3]));
}

let hashTimer = 0;
function writeHash(view) {
  clearTimeout(hashTimer);
  hashTimer = setTimeout(() => {
    history.replaceState(null, '', `#${view.x.toFixed(0)},${view.z.toFixed(0)},${view.metersPerPixel.toFixed(2)}`);
  }, 250);
}

// ---------- o que esta sob o ponteiro ----------

function hitAt(sx, sy) {
  const icon = [...hits].reverse().find((h) => Math.abs(h.sx - sx) <= Math.max(h.r, 14) && Math.abs(h.sy - sy) <= Math.max(h.r, 14));
  if (icon) return icon;
  const [wx, wz] = mapView.toWorld(sx, sy);
  return pieceHit(wx, wz);
}

// Construcao sob o ponteiro: a base a que ela pertence.
function pieceHit(wx, wz) {
  const { pieces, settlements } = mapView;
  if (!pieces || !layers.pieces) return null;
  const i = pieceAt(pieces, settlements, wx, wz, mapView.view.metersPerPixel * 0.75);
  if (i < 0) return null;
  const g = settlements.groups[settlements.pieceGroup[i]];
  return baseHit(g, KINDS[pieces.kind[i]].name);
}

function baseHit(g, here) {
  const pins = day ? dayPins : state?.pins;
  const mix = materials(g)
    .filter((m) => m.count / g.count >= 0.08)
    .map((m) => `${m.name} ${Math.round((m.count / g.count) * 100)}%`);
  return {
    title: baseName(g, pins, world),
    lines: [g.count === 1 ? 'peça solta' : `${fmt.format(g.count)} peças`, mix.join(' · '), here ? `aqui: ${here}` : ''].filter(Boolean),
    anchor: [(g.minX + g.maxX) / 2, g.maxZ],
    card: () => baseCard(g),
  };
}

function hover(sx, sy) {
  if (sx == null) return hideTooltip();
  const [wx, wz] = mapView.toWorld(sx, sy);
  hudCoords.textContent = `x ${wx.toFixed(0)}, z ${wz.toFixed(0)}`;
  const hit = hitAt(sx, sy);
  mapView.map.classList.toggle('pointing', !!hit?.card);
  if ((hit?.portal ?? null) !== hoveredPortal) {
    hoveredPortal = hit?.portal ?? null;
    mapView.invalidate();
  }
  if (!hit) return hideTooltip();
  const tip = $('tooltip');
  tip.replaceChildren(el('strong', { text: hit.title }), ...hit.lines.flatMap((l) => [el('span', { text: l }), el('br')]));
  tip.hidden = false;
  const r = mapView.map.getBoundingClientRect();
  const x = Math.min(r.left + sx + 14, window.innerWidth - tip.offsetWidth - 8);
  const y = Math.min(r.top + sy + 14, window.innerHeight - tip.offsetHeight - 8);
  tip.style.left = `${x}px`;
  tip.style.top = `${y}px`;
}

function hideTooltip() {
  $('tooltip').hidden = true;
  if (hoveredPortal) {
    hoveredPortal = null;
    mapView.invalidate();
  }
}

function click(sx, sy) {
  hideTooltip();
  const hit = hitAt(sx, sy);
  if (!hit) return closeCard();
  openCard(hit.anchor ?? mapView.toWorld(sx, sy), hit.card ?? (() => simpleCard(hit)), hit.portal);
}

// ---------- cartao ----------

function openCard(anchor, render, portal = null) {
  card = { anchor, render, portal };
  mapView.invalidate();
  const box = $('card');
  box.replaceChildren(
    el('button', { class: 'card-close', type: 'button', 'aria-label': 'Fechar', onclick: closeCard, text: '×' }),
    render(),
  );
  box.hidden = false;
  placeCard();
}

function closeCard() {
  if (card?.portal) mapView.invalidate();
  card = null;
  $('card').hidden = true;
}

function refreshCard() {
  if (card) openCard(card.anchor, card.render, card.portal);
}

// Acima da coisa clicada, sem sair da tela. No celular o CSS prende o cartao embaixo.
function placeCard() {
  if (!card) return;
  const box = $('card');
  const [sx, sy] = mapView.toScreen(...card.anchor);
  const r = mapView.map.getBoundingClientRect();
  const w = box.offsetWidth;
  const h = box.offsetHeight;
  let x = r.left + sx - w / 2;
  let y = r.top + sy - h - 18;
  if (y < 8) y = r.top + sy + 22;
  x = Math.max(8, Math.min(window.innerWidth - w - 8, x));
  y = Math.max(8, Math.min(window.innerHeight - h - 8, y));
  box.style.left = `${x}px`;
  box.style.top = `${y}px`;
}

const cardHead = (title, sub) => el('header', {}, el('h3', { text: title }), sub ? el('p', { class: 'sub', text: sub }) : null);
const cardLink = (href, text) => el('a', { class: 'card-link', href }, text, ' →');

function simpleCard(hit) {
  return el('div', {}, cardHead(hit.title), ...hit.lines.map((l) => el('p', { text: l })));
}

function playerCard(p) {
  const known = knownPlayers.find((k) => k.name === p.name);
  const rows = [
    ['Onde', `${BIOME_NAMES[p.biome] ?? '—'} · ${p.x.toFixed(0)}, ${p.z.toFixed(0)}`],
    ['Ping', p.ping != null ? `${(p.ping * 1000).toFixed(0)} ms` : '—'],
    ['Jogou em 7 dias', known ? duration(known.minutes7d) : '—'],
  ];
  const g = mapView.settlements && baseAt(mapView.settlements, p.x, p.z, BASE_MARGIN);
  if (g && g.count >= BASE_MIN_PIECES) rows.push(['Está em', baseName(g, state?.pins, world)]);
  return el('div', {},
    cardHead(p.name, 'online agora'),
    el('dl', { class: 'card-stats' }, rows.map(([k, v]) => el('div', {}, el('dt', { text: k }), el('dd', { text: v })))),
    cardLink(playerHref(p.name), 'Página do jogador'));
}

function baseCard(g) {
  const pins = day ? dayPins : state?.pins;
  const builders = buildersIn(world, g);
  const chests = chestsIn(g);
  const stored = chests.flatMap((c) => c.items);
  const rows = [
    ['Peças', fmt.format(g.count)],
    ['Materiais', materials(g).slice(0, 2).map((m) => m.name).join(', ')],
  ];
  if (builders.length) rows.push(['Construída por', builders.slice(0, 3).map((b) => b.name).join(', ')]);
  if (chests.length) rows.push(['Baús', `${chests.length} · ${fmt.format(stored.reduce((a, i) => a + i[1], 0))} itens`]);
  return el('div', {},
    cardHead(baseName(g, pins, world), `${Math.round(g.maxX - g.minX)} × ${Math.round(g.maxZ - g.minZ)} m`),
    el('dl', { class: 'card-stats' }, rows.map(([k, v]) => el('div', {}, el('dt', { text: k }), el('dd', { text: v })))),
    day ? el('p', { class: 'sub', text: `Como estava em ${day.slice(8, 10)}/${day.slice(5, 7)}.` }) : null,
    cardLink(baseHref(g), 'Página da base'));
}

// ---------- portais ----------

const distanceText = (m) => (m < 1000 ? `${Math.round(m)} m` : `${(m / 1000).toFixed(1).replace('.', ',')} km`);

// O plugin manda onde o par esta (arredondado a 1 m); o par e o portal da lista naquele ponto.
function pairOf(p) {
  if (!p.target) return null;
  const [tx, tz] = p.target;
  return state?.portals.find((q) => q !== p && Math.abs(q.x - tx) <= 1.5 && Math.abs(q.z - tz) <= 1.5) ?? { x: tx, z: tz, tag: p.tag, connected: true, target: [p.x, p.z] };
}

function placeName(x, z) {
  const g = mapView.settlements && baseAt(mapView.settlements, x, z, BASE_MARGIN);
  return g && g.count >= BASE_MIN_PIECES ? baseName(g, state?.pins, world) : null;
}

function portalHit(p, sx, sy, r) {
  const pair = pairOf(p);
  return {
    sx, sy, r, title: p.tag || 'Portal sem nome',
    lines: [pair ? `par a ${distanceText(Math.hypot(pair.x - p.x, pair.z - p.z))}` : 'sem par'],
    anchor: [p.x, p.z],
    portal: p,
    card: () => portalCard(p),
  };
}

// Recorte do mapa em volta do par, com o portal marcado no meio.
function pairPreview(pair) {
  const { width, height, metersPerPixel } = PAIR_VIEW;
  const shot = mapView.snapshot(pair.x, pair.z, metersPerPixel, width, height);
  if (!shot) return el('div', { class: 'pair-shot empty', text: 'O mapa ainda está abrindo.' });
  const ctx = shot.getContext('2d');
  const dpr = shot.width / width;
  const img = mapView.icons.portal;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.beginPath();
  ctx.arc(width / 2, height / 2, 17, 0, Math.PI * 2);
  ctx.fillStyle = 'rgba(242,163,58,0.22)';
  ctx.fill();
  ctx.strokeStyle = '#f2a33a';
  ctx.lineWidth = 2;
  ctx.stroke();
  if (img) {
    ctx.shadowColor = 'rgba(0,0,0,0.8)';
    ctx.shadowBlur = 3;
    const k = 24 / Math.max(img.width, img.height);
    ctx.drawImage(img, width / 2 - (img.width * k) / 2, height / 2 - (img.height * k) / 2, img.width * k, img.height * k);
  }
  shot.className = 'pair-shot';
  shot.style.aspectRatio = `${width} / ${height}`;
  return shot;
}

function portalCard(p) {
  const pair = pairOf(p);
  const here = placeName(p.x, p.z);
  const head = cardHead(p.tag || 'Portal sem nome', here ? `em ${here}` : `${Math.round(p.x)}, ${Math.round(p.z)}`);
  if (!pair) return el('div', {}, head, el('p', { class: 'sub', text: 'Sem par: nenhum outro portal com esse nome.' }));
  const there = placeName(pair.x, pair.z);
  const go = async () => {
    closeCard();
    const arrived = await mapView.flyTo(pair.x, pair.z, Math.min(mapView.view.metersPerPixel, 1.5));
    if (arrived) openCard([pair.x, pair.z], () => portalCard(pair), pair);
  };
  return el('div', {},
    head,
    el('button', { class: 'pair', type: 'button', onclick: go, 'aria-label': `Ir para o portal par${there ? ` em ${there}` : ''}` },
      pairPreview(pair),
      el('span', { class: 'pair-caption' },
        el('span', { text: there ? `Par em ${there}` : `Par em ${Math.round(pair.x)}, ${Math.round(pair.z)}` }),
        el('span', { class: 'meta', text: distanceText(Math.hypot(pair.x - p.x, pair.z - p.z)) }))),
    el('p', { class: 'sub', text: 'Clique no recorte para ir até o par.' }));
}

// Linha entre o portal e o par: tinta tracejada com fio claro por baixo, como as rotas do mapa.
function drawPortalLink(ctx, mv, p) {
  const pair = pairOf(p);
  if (!pair) return;
  const [ax, ay] = mv.toScreen(p.x, p.z);
  const [bx, by] = mv.toScreen(pair.x, pair.z);
  ctx.save();
  ctx.shadowBlur = 0;
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(ax, ay);
  ctx.lineTo(bx, by);
  ctx.strokeStyle = 'rgba(255,245,225,0.6)';
  ctx.lineWidth = 5;
  ctx.stroke();
  ctx.setLineDash([10, 7]);
  ctx.strokeStyle = '#5b1f10';
  ctx.lineWidth = 2.5;
  ctx.stroke();
  ctx.restore();
  return pair;
}

function chestCard(c) {
  const g = mapView.settlements && baseAt(mapView.settlements, c.x, c.z, BASE_MARGIN);
  const count = c.items.reduce((a, i) => a + i[1], 0);
  return el('div', {},
    cardHead(containerTitle(c), c.tomb ? 'lápide' : `${c.kind}${c.owner ? ` · de ${c.owner}` : ''}`),
    c.items.length ? itemGrid(items, c.items, { limit: 24, px: 30 }) : el('p', { class: 'sub', text: 'Vazio.' }),
    c.items.length ? el('p', { class: 'sub', text: `${fmt.format(count)} itens` }) : null,
    g && g.count >= BASE_MIN_PIECES ? cardLink(baseHref(g), baseName(g, state?.pins, world)) : null);
}

// ---------- camada de cima ----------

const visibleOn = (sx, sy, w, h) => sx > -40 && sy > -40 && sx < w + 40 && sy < h + 40;

function chestsIn(g) {
  return world ? world.containers.filter((c) => !c.tomb && inBox(c.x, c.z, g, BASE_MARGIN)) : [];
}

// Rastro com tinta de mapa: mais velho mais apagado, fio claro por baixo para ler sobre qualquer bioma.
function drawTrail(ctx, points, step, now) {
  const span = trailHours * 3600;
  let prev = null;
  for (const [t, x, z] of points) {
    const [sx, sy] = mapView.toScreen(x, z);
    if (prev && t - prev.t <= step * 3 && Math.hypot(sx - prev.sx, sy - prev.sy) < 400) {
      const age = Math.min(1, (now - t) / span);
      ctx.globalAlpha = 0.95 - age * 0.7;
      ctx.beginPath();
      ctx.moveTo(prev.sx, prev.sy);
      ctx.lineTo(sx, sy);
      ctx.strokeStyle = 'rgba(255,245,225,0.55)';
      ctx.lineWidth = 4;
      ctx.stroke();
      ctx.strokeStyle = '#5b1f10';
      ctx.lineWidth = 2;
      ctx.stroke();
    }
    prev = { t, sx, sy };
  }
  ctx.globalAlpha = 1;
}

function drawChest(ctx, sx, sy, size, highlight) {
  const w = size;
  const h = size * 0.72;
  if (highlight) {
    ctx.beginPath();
    ctx.arc(sx, sy, size * 0.95, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(242,163,58,0.35)';
    ctx.fill();
    ctx.strokeStyle = '#f2a33a';
    ctx.lineWidth = 2;
    ctx.stroke();
  }
  ctx.fillStyle = '#7a4a22';
  ctx.strokeStyle = '#1b0f06';
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.roundRect(sx - w / 2, sy - h / 2, w, h, 2);
  ctx.fill();
  ctx.stroke();
  ctx.beginPath();
  ctx.moveTo(sx - w / 2, sy - h / 8);
  ctx.lineTo(sx + w / 2, sy - h / 8);
  ctx.stroke();
  ctx.fillStyle = '#d9b35b';
  ctx.fillRect(sx - 1.5, sy - h / 8 - 1, 3, 4);
}

function drawOverlay(ctx, mv) {
  hits = [];
  drawHud();
  placeCard();
  if (!state) return;
  const mpp = mv.view.metersPerPixel;
  const w = mv.width;
  const h = mv.height;
  const visible = (sx, sy) => visibleOn(sx, sy, w, h);
  const showLabels = layers.labels && mpp <= LABELS_UNTIL_MPP;
  const boxes = [];
  const iconSize = mpp > 12 ? 16 : mpp > 5 ? 20 : 26;

  if (layers.trails && trailData && !day) {
    const now = Date.now() / 1000;
    for (const p of trailData.players) drawTrail(ctx, p.points, trailData.step, now);
  }

  ctx.shadowColor = 'rgba(0,0,0,0.8)';
  ctx.shadowBlur = 3;

  if (layers.beds) {
    for (const b of day ? [] : state.beds) {
      const [sx, sy] = mv.toScreen(b.x, b.z);
      if (!visible(sx, sy)) continue;
      mv.icon('bed', sx, sy, iconSize * 0.75, 0.9);
      hits.push({ sx, sy, r: iconSize * 0.4, title: 'Cama', lines: [b.owner ? `de ${b.owner}` : ''].filter(Boolean), anchor: [b.x, b.z] });
    }
  }
  // O portal aberto no cartao e o do hover ligam ao par, mesmo com a camada desligada (voo ate o par).
  const linked = new Set(day ? [] : [card?.portal, hoveredPortal].filter(Boolean));
  const ends = [];
  for (const p of linked) {
    const pair = drawPortalLink(ctx, mv, p);
    if (pair) ends.push(p, pair);
  }
  if (layers.portals || ends.length) {
    const shown = layers.portals ? (day ? [] : state.portals) : ends;
    for (const p of new Set(shown)) {
      const [sx, sy] = mv.toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      const lit = ends.includes(p);
      mv.icon('portal', sx, sy, iconSize * (lit ? 1 : 0.8), p.connected || lit ? 1 : 0.55);
      hits.push(portalHit(p, sx, sy, iconSize * 0.4));
    }
  }

  // Baus: de perto todos; a busca destaca os que tem o item em qualquer zoom.
  if (world && !day) {
    ctx.shadowBlur = 2;
    for (const c of world.containers) {
      const match = found != null && c.items.some((i) => i[0] === found);
      const show = match || (c.tomb ? layers.pins : layers.chests && mpp <= CHESTS_UNTIL_MPP);
      if (!show) continue;
      const [sx, sy] = mv.toScreen(c.x, c.z);
      if (!visible(sx, sy)) continue;
      if (c.tomb) mv.icon('death', sx, sy, iconSize * 0.85);
      else drawChest(ctx, sx, sy, mpp > 2.5 ? 12 : 15, match);
      const lines = c.tomb ? [`${c.items.length} itens esperando`] : [c.kind, c.owner ? `de ${c.owner}` : ''].filter(Boolean);
      if (match) {
        const n = c.items.filter((i) => i[0] === found).reduce((a, i) => a + i[1], 0);
        lines.unshift(`${fmt.format(n)} × ${itemName(items, found)}`);
      }
      hits.push({ sx, sy, r: 10, title: containerTitle(c), lines, anchor: [c.x, c.z], card: () => chestCard(c) });
    }
  }

  const labelQueue = [];
  if (layers.pins) {
    for (const p of day ? dayPins : state.pins) {
      const [sx, sy] = mv.toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      const name = PIN_ICONS[p.type] ?? 'pin';
      mv.icon(name, sx, sy, iconSize, p.checked ? 0.6 : 1);
      if (p.checked) mv.icon('checked', sx, sy, iconSize * 0.9);
      const g = mv.settlements && baseAt(mv.settlements, p.x, p.z, 15);
      const base = g && g.count >= BASE_MIN_PIECES ? baseHit(g) : null;
      hits.push({
        sx, sy, r: iconSize * 0.45, title: p.name || 'Marcação',
        lines: [p.author ? `por ${p.author}` : '', p.checked ? 'riscada' : ''].filter(Boolean),
        anchor: [p.x, p.z],
        // Marcacao de uma base abre o cartao da base.
        card: base?.card,
      });
      if (showLabels) labelQueue.push([p.name, sx, sy + iconSize * 0.45, 15]);
    }
  }

  ctx.shadowBlur = 0;
  // Jogadores por cima de tudo, com nome sempre.
  const playerLabels = [];
  if (layers.players) {
    ctx.shadowBlur = 4;
    for (const p of day ? [] : state.players) {
      const [sx, sy] = mv.toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      mv.icon('player_32', sx, sy, iconSize + 4);
      hits.push({
        sx, sy, r: iconSize * 0.5, title: p.name,
        lines: [BIOME_NAMES[p.biome] ?? '', p.ping != null ? `ping ${(p.ping * 1000).toFixed(0)} ms` : ''].filter(Boolean),
        anchor: [p.x, p.z],
        card: () => playerCard(p),
      });
      playerLabels.push([p.name, sx, sy + iconSize * 0.55 + 2, 17]);
    }
    ctx.shadowBlur = 0;
  }
  for (const l of playerLabels) mv.label(...l);
  for (const l of labelQueue) mv.label(...l, boxes);
}

function drawHud() {
  const target = 120;
  const mpp = mapView.view.metersPerPixel;
  const meters = target * mpp;
  const pow = Math.pow(10, Math.floor(Math.log10(meters)));
  const nice = [1, 2, 5, 10].map((k) => k * pow).filter((v) => v <= meters).pop();
  $('scale-bar').style.width = `${nice / mpp}px`;
  $('scale-text').textContent = nice >= 1000 ? `${nice / 1000} km` : `${nice} m`;
}

// ---------- painel ----------

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
  renderPlayers();
}

function renderPlayers() {
  const list = $('players');
  list.replaceChildren();
  const online = state ? [...state.players].sort((a, b) => a.name.localeCompare(b.name)) : [];
  if (!online.length) list.append(el('li', { class: 'empty', text: 'Ninguém no mundo agora.' }));
  for (const p of online) {
    const li = el('li', {},
      el('span', { class: 'name', text: p.name }),
      el('span', { class: 'ping', text: p.ping != null ? `${(p.ping * 1000).toFixed(0)} ms` : '' }),
      el('span', { class: 'where', text: `${BIOME_NAMES[p.biome] ?? '—'} · ${p.x.toFixed(0)}, ${p.z.toFixed(0)}` }));
    li.addEventListener('click', () => {
      mapView.goTo(p.x, p.z, Math.min(mapView.view.metersPerPixel, 1.5));
      openCard([p.x, p.z], () => playerCard(p));
    });
    list.append(li);
  }
  const offline = knownPlayers.filter((k) => !online.some((p) => p.name === k.name));
  const others = $('others');
  others.replaceChildren(
    ...offline.map((k) =>
      el('li', {}, el('a', { href: playerHref(k.name) }, el('span', { class: 'name', text: k.name }), el('span', { class: 'seen', text: ago(k.lastSeen) })))),
  );
  $('others-wrap').hidden = !offline.length;
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
    state = await getJSON('api/state');
    renderPanel();
    mapView.invalidate();
  } catch {
    $('status').className = 'status offline';
    $('status-text').textContent = 'sem notícias do servidor';
  }
  setTimeout(pollState, STATE_EVERY_MS);
}

async function pollHistory() {
  try {
    renderSpark((await getJSON('api/history')).online);
    knownPlayers = await getJSON('api/players');
    if (state) renderPlayers();
  } catch {}
  setTimeout(pollHistory, HISTORY_EVERY_MS);
}

async function pollWorld() {
  try {
    world = await getJSON('api/world');
    items ??= await loadItems();
    renderSearchResults();
    mapView.invalidate();
  } catch {}
  setTimeout(pollWorld, WORLD_EVERY_MS);
}

async function loadTrails() {
  if (!layers.trails) return;
  try {
    trailData = await getJSON(`api/trails?hours=${trailHours}`);
    mapView.invalidate();
  } catch {}
}

// ---------- busca nos baus ----------

function itemTotals() {
  const totals = new Map();
  for (const c of world?.containers ?? []) {
    if (c.tomb) continue;
    for (const [hash, stack] of c.items) {
      const t = totals.get(hash) ?? { hash, total: 0, chests: 0 };
      t.total += stack;
      totals.set(hash, t);
    }
    for (const hash of new Set(c.items.map((i) => i[0]))) totals.get(hash).chests++;
  }
  return totals;
}

function bindSearch() {
  const input = $('search');
  const list = $('suggestions');
  const suggest = () => {
    const q = normalize(input.value.trim());
    list.replaceChildren();
    if (q.length < 2 || !world || !items) return (list.hidden = true);
    const matches = [...itemTotals().values()]
      .map((t) => ({ ...t, name: itemName(items, t.hash), en: items.byHash.get(t.hash)?.[2] ?? '' }))
      .filter((t) => normalize(t.name).includes(q) || normalize(t.en).includes(q))
      .sort((a, b) => normalize(a.name).indexOf(q) - normalize(b.name).indexOf(q) || b.total - a.total)
      .slice(0, 8);
    if (!matches.length) list.append(el('li', { class: 'empty', text: 'Nenhum baú tem isso.' }));
    for (const m of matches) {
      list.append(
        el('li', {},
          el('button', { type: 'button', onclick: () => pick(m.hash) },
            itemIcon(items, m.hash, 24), el('span', { class: 'name', text: m.name }), el('span', { class: 'qty', text: fmt.format(m.total) }))),
      );
    }
    list.hidden = false;
  };
  const pick = (hash) => {
    found = hash;
    input.value = itemName(items, hash);
    list.hidden = true;
    renderSearchResults();
    mapView.invalidate();
  };
  input.addEventListener('input', suggest);
  input.addEventListener('focus', suggest);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') list.querySelector('button')?.click();
    if (e.key === 'Escape') list.hidden = true;
  });
  $('search-clear').addEventListener('click', () => {
    found = null;
    input.value = '';
    list.hidden = true;
    renderSearchResults();
    mapView.invalidate();
  });
}

function renderSearchResults() {
  const out = $('search-results');
  $('search-clear').hidden = found == null;
  out.replaceChildren();
  if (found == null || !world) return;
  const chests = world.containers
    .filter((c) => !c.tomb)
    .map((c) => ({ c, n: c.items.filter((i) => i[0] === found).reduce((a, i) => a + i[1], 0) }))
    .filter((r) => r.n > 0)
    .sort((a, b) => b.n - a.n);
  const total = chests.reduce((a, r) => a + r.n, 0);
  out.append(el('p', { class: 'day-info', text: `${fmt.format(total)} em ${chests.length} ${chests.length === 1 ? 'baú' : 'baús'}` }));
  const ul = el('ul', { class: 'found' });
  for (const { c, n } of chests.slice(0, 30)) {
    const g = mapView.settlements && baseAt(mapView.settlements, c.x, c.z, BASE_MARGIN);
    const where = g && g.count >= BASE_MIN_PIECES ? baseName(g, state?.pins, world) : `${Math.round(c.x)}, ${Math.round(c.z)}`;
    ul.append(
      el('li', {},
        el('button', {
          type: 'button',
          onclick: () => {
            mapView.goTo(c.x, c.z, Math.min(mapView.view.metersPerPixel, 0.8));
            openCard([c.x, c.z], () => chestCard(c));
          },
        }, el('span', { class: 'name', text: containerTitle(c) }), el('span', { class: 'qty', text: fmt.format(n) }), el('span', { class: 'where', text: where }))),
    );
  }
  out.append(ul);
}

// ---------- linha do tempo ----------

async function setupTimeline() {
  let days = [];
  try {
    days = await getJSON('api/days');
  } catch {}
  const info = $('day-info');
  const slider = $('day');
  const play = $('day-play');
  const label = $('day-label');
  if (!days.length) {
    info.textContent = 'Nenhum dia guardado ainda.';
    slider.disabled = play.disabled = true;
    return;
  }
  const ordered = [...days].reverse();
  // Ultima posicao = agora (ao vivo).
  slider.max = String(ordered.length);
  slider.value = slider.max;
  const at = (i) => (i >= ordered.length ? null : ordered[i]);
  const chart = $('growth');
  const renderChart = () => {
    chart.replaceChildren(columnChart(
      ordered.map((d, i) => ({
        key: d.date,
        value: d.pieces ?? 0,
        label: dayMonth(Date.parse(`${d.date}T12:00`)),
        tip: `${fmt.format(d.pieces ?? 0)} peças · ${d.exploredKm2.toFixed(1)} km²`,
        active: d.date === day,
        index: i,
      })),
      { height: 56, format: (v) => fmt.format(v), onPick: (r) => go(r.index) },
    ));
  };
  const describe = () => {
    const d = at(Number(slider.value));
    label.textContent = d ? `${dayMonth(Date.parse(`${d.date}T12:00`))} às ${d.time}` : 'Agora (ao vivo)';
    info.textContent = d
      ? `${d.exploredKm2.toFixed(1)} km² explorados · ${d.pieces != null ? fmt.format(d.pieces) : '–'} construções · ${d.pins} marcações`
      : 'Mapa ao vivo, com jogadores, baús, portais e camas.';
    renderChart();
  };
  let loading = null;
  const go = async (i) => {
    slider.value = String(i);
    const d = at(i);
    const date = d?.date ?? null;
    describe();
    if (date === day) return;
    const job = (async () => {
      if (date) dayPins = await getJSON(`api/days/${date}/pins`);
      if (mapView.renderer) await mapView.loadWorld(date);
      day = date;
      document.body.classList.toggle('past', !!date);
      closeCard();
      describe();
      mapView.invalidate();
    })();
    loading = job;
    try {
      await job;
    } catch (err) {
      console.error(err);
    }
  };
  slider.addEventListener('input', () => describe());
  slider.addEventListener('change', () => go(Number(slider.value)));
  let playing = false;
  play.addEventListener('click', async () => {
    playing = !playing;
    play.textContent = playing ? '❚❚' : '▶';
    play.setAttribute('aria-label', playing ? 'Pausar' : 'Tocar a linha do tempo');
    if (!playing) return;
    let i = Number(slider.value) >= ordered.length ? 0 : Number(slider.value);
    while (playing && i <= ordered.length) {
      const t0 = performance.now();
      await go(i);
      await loading;
      await new Promise((r) => setTimeout(r, Math.max(0, PLAY_STEP_MS - (performance.now() - t0))));
      i++;
    }
    playing = false;
    play.textContent = '▶';
    play.setAttribute('aria-label', 'Tocar a linha do tempo');
  });
  describe();
}

// ---------- entrada ----------

function bindPanel() {
  for (const input of document.querySelectorAll('#layers input')) {
    layers[input.dataset.layer] = input.checked;
    input.addEventListener('change', () => {
      layers[input.dataset.layer] = input.checked;
      mapView.showPieces = layers.pieces;
      if (input.dataset.layer === 'trails') loadTrails();
      mapView.invalidate();
    });
  }
  $('trail-hours').addEventListener('change', (e) => {
    trailHours = Number(e.target.value);
    const box = document.querySelector('[data-layer="trails"]');
    if (!box.checked) {
      box.checked = true;
      layers.trails = true;
    }
    loadTrails();
  });
  const panel = $('panel');
  $('panel-toggle').addEventListener('click', () => {
    const collapsed = panel.classList.toggle('collapsed');
    $('panel-toggle').setAttribute('aria-expanded', String(!collapsed));
  });
  if (window.matchMedia('(max-width: 720px)').matches) {
    panel.classList.add('collapsed');
    $('panel-toggle').setAttribute('aria-expanded', 'false');
  }
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') closeCard();
  });
}

async function main() {
  readHash();
  bindPanel();
  bindSearch();
  pollState();
  pollHistory();
  pollWorld();
  setupTimeline();
  setInterval(tickSave, 1000);
  setInterval(loadTrails, TRAILS_EVERY_MS);
  try {
    await mapView.start();
    mapView.showPieces = layers.pieces;
    $('loading').classList.add('done');
    refreshCard();
  } catch (err) {
    console.error(err);
    const box = $('loading');
    box.classList.add('error');
    box.textContent = `Não deu para abrir o mapa: ${err.message}`;
  }
}

main();
