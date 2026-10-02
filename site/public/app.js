import { KINDS, pieceAt } from './pieces.js';
import { MapView } from './mapview.js';
import {
  $, ago, BASE_MARGIN, BASE_MIN_PIECES, baseAt, baseHref, baseName, BIOME_NAMES, buildersIn, columnChart,
  baseCenter, containerTitle, dayMonth, duration, el, fmt, getJSON, inBox, itemGrid, itemIcon, itemName, loadItems,
  materials, normalize, PIN_ICONS, playerHref,
} from './common.js';
import { hiddenFor, hideIcon, kindName, myAreaAt, myHides, myPlayerHide, onHiddenChange, ready, unhide } from './hidden.js';
import { storeKey } from './world.js';
import { bandAt, loadedArea, outline } from './loaded.js';

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

const layers = { pieces: true, pins: true, labels: true, portals: false, beds: false, players: true, trails: false, chests: false, safe: false, loaded: false };
const HOME = { x: -44, z: -68, metersPerPixel: 3.2 };
// Camadas e janela dos rastros ficam no navegador de quem olha.
const PREFS_KEY = storeKey('map');
let state = null;
let world = null; // /api/world: baus, camas, construtores
let items = null;
let knownPlayers = [];
let trailHours = 6;
// Busca: a lista de achados recolhe para nao cobrir o mapa.
let resultsOpen = true;
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
  view: { ...HOME },
  icons: ['fire', 'house', 'hammer', 'pin', 'portal', 'bed', 'checked', 'player_32', 'boss', 'death'],
  animate: true,
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
  return pieceHit(wx, wz) ?? baseAreaHit(wx, wz) ?? loadedHit(wx, wz);
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
    lines: [myAreaAt(...baseCenter(g)) ? 'escondida — só você vê' : '', g.count === 1 ? 'peça solta' : `${fmt.format(g.count)} peças`, mix.join(' · '), here ? `aqui: ${here}` : ''].filter(Boolean),
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
    cardLink(playerHref(p.name), 'Página do jogador'),
    hideRow({ kind: 'player', title: p.name, name: p.name }));
}

// Escondido por este navegador naquele lugar: a coisa em si ou a base em volta.
function hiddenHere(kind, o) {
  const own = myHides().find((h) => h.kind === kind && Math.abs(h.x - o.x) <= 2 && Math.abs(h.z - o.z) <= 2);
  return own?.id ?? myAreaAt(o.x, o.z)?.id ?? null;
}

// Olho no canto do cartao, ao lado do fechar. No passado, so se ja estiver escondido.
function hideRow(spec) {
  if (day && !hiddenFor(spec)) return null;
  return hideIcon(spec, { className: 'card-hide' });
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
    cardLink(baseHref(g), 'Página da base'),
    hideRow({ kind: 'base', title: baseName(g, pins, world), x: baseCenter(g)[0], z: baseCenter(g)[1], box: [g.minX, g.minZ, g.maxX, g.maxZ] }));
}

function bedCard(b) {
  return el('div', {},
    cardHead('Cama', b.owner ? `de ${b.owner}` : null),
    b.owner ? cardLink(playerHref(b.owner), 'Página do jogador') : null,
    hideRow({ kind: 'bed', title: b.owner ? `Cama de ${b.owner}` : 'Cama', x: b.x, z: b.z }));
}

function pinCard(p) {
  const g = mapView.settlements && baseAt(mapView.settlements, p.x, p.z, 15);
  const base = g && g.count >= BASE_MIN_PIECES ? g : null;
  return el('div', {},
    cardHead(p.name || 'Marcação', [p.author ? `por ${p.author}` : '', p.checked ? 'riscada' : ''].filter(Boolean).join(' · ') || null),
    base ? cardLink(baseHref(base), baseName(base, day ? dayPins : state?.pins, world)) : null,
    hideRow({ kind: 'pin', title: p.name || 'Marcação', x: p.x, z: p.z }));
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
  const foot = hideRow({ kind: 'portal', title: p.tag || 'Portal sem nome', x: p.x, z: p.z });
  if (!pair) return el('div', {}, head, el('p', { class: 'sub', text: 'Sem par: nenhum outro portal com esse nome.' }), foot);
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
    el('p', { class: 'sub', text: 'Clique no recorte para ir até o par.' }),
    foot);
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
    g && g.count >= BASE_MIN_PIECES ? cardLink(baseHref(g), baseName(g, state?.pins, world)) : null,
    hideRow({ kind: 'chest', title: containerTitle(c), x: c.x, z: c.z }));
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

// Bau com a sombra ja pronta: base grande tem centenas, e sombra desfocada em cada um a cada quadro pesa.
function drawChest(mv, sx, sy, size, highlight) {
  const box = highlight ? size * 2 + 4 : size;
  mv.spriteAt(`chest:${highlight ? 1 : 0}`, sx, sy, box, box, (ctx) => {
    const cx = box / 2;
    const cy = box / 2;
    const w = size;
    const h = size * 0.72;
    if (highlight) {
      ctx.beginPath();
      ctx.arc(cx, cy, size * 0.95, 0, Math.PI * 2);
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
    ctx.roundRect(cx - w / 2, cy - h / 2, w, h, 2);
    ctx.fill();
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(cx - w / 2, cy - h / 8);
    ctx.lineTo(cx + w / 2, cy - h / 8);
    ctx.stroke();
    ctx.fillStyle = '#d9b35b';
    ctx.fillRect(cx - 1.5, cy - h / 8 - 1, 3, 4);
  });
}

// Olho riscado no canto de cima a direita do que so eu vejo.
function hiddenBadge(ctx, sx, sy, r) {
  const x = sx + r * 0.75;
  const y = sy - r * 0.75;
  ctx.save();
  ctx.shadowBlur = 0;
  ctx.beginPath();
  ctx.arc(x, y, 6.5, 0, Math.PI * 2);
  ctx.fillStyle = '#2a1d12';
  ctx.fill();
  ctx.strokeStyle = '#f2a33a';
  ctx.lineWidth = 1.2;
  ctx.stroke();
  ctx.beginPath();
  ctx.ellipse(x, y, 3.8, 2.2, 0, 0, Math.PI * 2);
  ctx.moveTo(x - 4, y + 4);
  ctx.lineTo(x + 4, y - 4);
  ctx.strokeStyle = '#f4efe6';
  ctx.lineWidth = 1.1;
  ctx.stroke();
  ctx.restore();
}

// Base que eu escondi: area hachurada com contorno tracejado e a etiqueta "escondida".
// ---------- area de base: onde nao nasce monstro ----------

// Pecas que abrem area de base (base-areas.json do servidor) em nome do jogo.
const BASE_AREA_NAMES = {
  piece_workbench: 'Bancada', forge: 'Forja', piece_stonecutter: 'Cortador de pedra', piece_artisanstation: 'Mesa de artesão',
  blackforge: 'Forja negra', piece_magetable: 'Mesa galdr', piece_FrostFoundry: 'Fundição de gelo', piece_FrostKiln: 'Forno de gelo',
  UpgradeStation: 'Estação de melhoria', fire_pit: 'Fogueira', fire_pit_iron: 'Fogueira de ferro', hearth: 'Lareira',
  bonfire: 'Fogueira grande', prop_bonfire: 'Fogueira', BogWitch_Fire_Pit: 'Fogueira da Bruxa', fire_pit_haldor: 'Fogueira do Haldor',
  fire_pit_hildir: 'Fogueira da Hildir', Morkhalla_firepit: 'Fogueira', bed: 'Cama', piece_bed02: 'Cama de dragão',
  ashwood_bed: 'Cama de freixo', portal_wood: 'Portal', portal_stone: 'Portal de pedra', portal: 'Portal', piece_groundtorch: 'Tocha de chão',
  piece_groundtorch_wood: 'Tocha de chão', piece_groundtorch_blue: 'Tocha azul', piece_groundtorch_green: 'Tocha verde',
  piece_groundtorch_mist: 'Tocha de Mistlands', piece_walltorch: 'Tocha de parede', piece_brazierfloor01: 'Braseiro',
  piece_brazierfloor02: 'Braseiro', piece_brazierceiling01: 'Braseiro suspenso', Candle_resin: 'Vela de resina',
  piece_hoodedlantern: 'Lanterna com capuz', piece_jackoturnip: 'Nabo-lanterna', piece_Lavalantern: 'Lanterna de lava',
  piece_dvergr_lantern: 'Lanterna dvergr', piece_dvergr_lantern_pole: 'Poste de lanterna dvergr', piece_snowlantern: 'Lanterna de neve',
  piece_wisplure: 'Farol de wisp', guard_stone: 'Pedra-guarda', dverger_guardstone: 'Pedra-guarda dvergr', piece_shieldgenerator: 'Gerador de escudo',
  charred_shieldgenerator: 'Gerador de escudo carbonizado', piece_turret: 'Balista', smelter: 'Fornalha', blastfurnace: 'Alto-forno',
  charcoal_kiln: 'Forno de carvão', eitrrefinery: 'Refinaria de eitr', windmill: 'Moinho', piece_spinningwheel: 'Roca',
  piece_oven: 'Forno de pedra', fermenter: 'Fermentador', piece_sapcollector: 'Coletor de seiva', incinerator: 'Obliterador',
};
const baseAreaName = (prefab) => BASE_AREA_NAMES[prefab] ?? prefab;
const BASE_AREA_FILL = 'rgba(255, 214, 120, 0.2)';
const BASE_AREA_INK = 'rgba(58, 36, 16, 0.85)';
let baseAreaCanvas = null;

// Uniao dos circulos: preenchimento unico (sem escurecer onde sobrepoe) e so a borda de fora.
function drawBaseAreas(ctx, mv) {
  if (!layers.safe || day || !world?.baseAreas?.length) return;
  const mpp = mv.view.metersPerPixel;
  const shown = [];
  for (const [x, z, r] of world.baseAreas) {
    const [sx, sy] = mv.toScreen(x, z);
    const rp = r / mpp;
    if (sx + rp < 0 || sy + rp < 0 || sx - rp > mv.width || sy - rp > mv.height) continue;
    shown.push([sx, sy, rp]);
  }
  if (!shown.length) return;
  const c = (baseAreaCanvas ??= document.createElement('canvas'));
  c.width = ctx.canvas.width;
  c.height = ctx.canvas.height;
  const a = c.getContext('2d');
  const dpr = mv.view.pixelRatio;
  a.setTransform(dpr, 0, 0, dpr, 0, 0);
  const union = (grow) => {
    a.beginPath();
    for (const [sx, sy, rp] of shown) {
      const r = Math.max(rp + grow, 0);
      a.moveTo(sx + r, sy);
      a.arc(sx, sy, r, 0, Math.PI * 2);
    }
  };
  const edge = mpp > 6 ? 1 : 1.5;
  a.lineWidth = edge * 2;
  a.strokeStyle = BASE_AREA_INK;
  union(0);
  a.stroke();
  a.globalCompositeOperation = 'destination-out';
  union(-edge);
  a.fill();
  a.globalCompositeOperation = 'destination-over';
  a.fillStyle = BASE_AREA_FILL;
  union(0);
  a.fill();
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.shadowBlur = 0;
  ctx.drawImage(c, 0, 0);
  ctx.restore();
}

// Ponteiro dentro da area: o que a abre ali.
function baseAreaHit(wx, wz) {
  if (!layers.safe || day || !world?.baseAreas) return null;
  const here = world.baseAreas.filter(([x, z, r]) => (x - wx) ** 2 + (z - wz) ** 2 <= r * r);
  if (!here.length) return null;
  const count = new Map();
  for (const [, , r, prefab] of here) {
    const key = `${baseAreaName(prefab)} (${r} m)`;
    count.set(key, (count.get(key) ?? 0) + 1);
  }
  const by = [...count].sort((p, q) => q[1] - p[1]).map(([k, n]) => (n > 1 ? `${n}× ${k}` : k));
  return {
    title: 'Sem spawn de monstros',
    lines: [by.slice(0, 4).join(' · ') + (by.length > 4 ? ` · +${by.length - 4}` : ''), 'raide e ninho ainda vêm'],
    anchor: [wx, wz],
  };
}

// ---------- area que o servidor mantem viva (chunk loader) ----------

const LOADED_INK = 'rgba(18, 48, 60, 0.9)';
const LOADED_ACTIVE = 'rgba(58, 138, 160, 0.32)';
const LOADED_NEAR = 'rgba(58, 138, 160, 0.13)';
let loadedCache = null;

function loadedGeometry() {
  const l = state?.loaded;
  if (!l) return null;
  const k = `${l.x}|${l.z}|${l.near}|${l.far}|${l.classic}`;
  if (loadedCache?.k !== k) {
    const area = loadedArea(l.x, l.z, l);
    loadedCache = { k, area, near: outline(area.near), outer: outline([...area.near, ...area.distant]) };
  }
  return loadedCache;
}

// Retangulo de mundo (x0..x1, z0..z1) na tela: z cresce para cima.
function screenRect(mv, x0, z0, x1, z1) {
  const [ax, ay] = mv.toScreen(x0, z1);
  const [bx, by] = mv.toScreen(x1, z0);
  return [ax, ay, bx - ax, by - ay];
}

function strokeEdges(ctx, mv, edges) {
  ctx.beginPath();
  for (const [x0, z0, x1, z1] of edges) {
    ctx.moveTo(...mv.toScreen(x0, z0));
    ctx.lineTo(...mv.toScreen(x1, z1));
  }
  ctx.stroke();
}

// Tres faixas, como o jogo decide: simulado (o servidor e dono e roda a IA), carregado (terreno e
// objetos existem, ninguem anda) e o anel dos objetos distantes; grade de zonas de perto.
function drawLoaded(ctx, mv) {
  if (!layers.loaded || day) return;
  const g = loadedGeometry();
  if (!g) return;
  const { area } = g;
  const l = state.loaded;
  const mpp = mv.view.metersPerPixel;
  const half = 32;
  ctx.save();
  ctx.shadowBlur = 0;

  ctx.fillStyle = LOADED_NEAR;
  ctx.beginPath();
  for (const [zx, zz] of area.near) ctx.rect(...screenRect(mv, zx * 64 - half, zz * 64 - half, zx * 64 + half, zz * 64 + half));
  ctx.fill();
  ctx.clip();

  const cx = area.center[0] * 64;
  const cz = area.center[1] * 64;
  const h = area.active.half;
  const square = () => {
    ctx.beginPath();
    ctx.rect(...screenRect(mv, cx - h, cz - h, cx + h, cz + h));
  };
  const circle = () => {
    const [sx, sy] = mv.toScreen(cx, cz);
    ctx.beginPath();
    ctx.arc(sx, sy, area.active.radius / mpp, 0, Math.PI * 2);
  };
  ctx.save();
  if (area.active.radius) {
    circle();
    ctx.clip();
  }
  square();
  ctx.fillStyle = LOADED_ACTIVE;
  ctx.fill();
  ctx.strokeStyle = LOADED_INK;
  ctx.lineWidth = 1.5;
  ctx.stroke();
  ctx.restore();
  if (area.active.radius) {
    ctx.save();
    square();
    ctx.clip();
    circle();
    ctx.strokeStyle = LOADED_INK;
    ctx.lineWidth = 1.5;
    ctx.stroke();
    ctx.restore();
  }

  if (mpp <= 4) {
    ctx.strokeStyle = 'rgba(18, 48, 60, 0.28)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (const [zx, zz] of area.near) ctx.rect(...screenRect(mv, zx * 64 - half, zz * 64 - half, zx * 64 + half, zz * 64 + half));
    ctx.stroke();
  }
  ctx.restore();

  ctx.save();
  ctx.shadowBlur = 0;
  ctx.strokeStyle = LOADED_INK;
  ctx.lineWidth = 1.5;
  strokeEdges(ctx, mv, g.near);
  ctx.setLineDash([6, 5]);
  ctx.lineWidth = 1;
  strokeEdges(ctx, mv, g.outer);
  ctx.setLineDash([]);

  const [sx, sy] = mv.toScreen(l.x, l.z);
  const r = mpp > 6 ? 4 : 6;
  ctx.beginPath();
  ctx.moveTo(sx, sy - r);
  ctx.lineTo(sx + r, sy);
  ctx.lineTo(sx, sy + r);
  ctx.lineTo(sx - r, sy);
  ctx.closePath();
  ctx.fillStyle = '#e8f4f6';
  ctx.fill();
  ctx.lineWidth = 1.5;
  ctx.stroke();
  ctx.restore();
  if (mpp <= 8) mv.label(l.loader ? 'Chunk loader' : 'Servidor', sx, sy - r - 19, 14);
}

function loadedHit(wx, wz) {
  if (!layers.loaded || day) return null;
  const g = loadedGeometry();
  if (!g) return null;
  const band = bandAt(g.area, wx, wz);
  if (!band) return null;
  const l = state.loaded;
  const [zx, zz] = g.area.center;
  const where = l.loader
    ? `centro: placa "chunkloader" em ${l.x.toFixed(0)}, ${l.z.toFixed(0)} (zona ${zx},${zz})`
    : `centro: origem do mundo, sem placa "chunkloader" (zona ${zx},${zz})`;
  const counts = band === 'active' && l.instances != null
    ? `agora: ${fmt.format(l.instances)} objetos, ${l.tamed ?? 0} domados e ${l.wild ?? 0} selvagens com o servidor`
    : '';
  const text = {
    active: ['Simulado pelo servidor', 'sem ninguém por perto: bichos comem e procriam, ovo aquecido choca'],
    near: ['Carregado, parado', 'terreno e objetos existem no servidor, mas sem jogador perto nada anda'],
    distant: ['Só objetos distantes', 'só objetos marcados como distantes existem aqui; nada anda'],
  }[band];
  return {
    title: text[0],
    lines: [text[1], counts, where, 'quem chega perto assume a simulação e devolve ao sair'].filter(Boolean),
    anchor: [wx, wz],
  };
}

function drawHiddenAreas(ctx, mv) {
  for (const h of myHides()) {
    if (h.kind !== 'base') continue;
    const [ax, ay] = mv.toScreen(h.box[0], h.box[3]);
    const [bx, by] = mv.toScreen(h.box[2], h.box[1]);
    const w = bx - ax;
    const hh = by - ay;
    if (bx < -40 || by < -40 || ax > mv.width + 40 || ay > mv.height + 40) continue;
    ctx.save();
    ctx.beginPath();
    ctx.rect(ax, ay, w, hh);
    ctx.clip();
    ctx.fillStyle = 'rgba(30,20,12,0.28)';
    ctx.fillRect(ax, ay, w, hh);
    ctx.strokeStyle = 'rgba(40,24,10,0.35)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let d = -hh; d < w; d += 9) {
      ctx.moveTo(ax + d, ay + hh);
      ctx.lineTo(ax + d + hh, ay);
    }
    ctx.stroke();
    ctx.restore();
    ctx.save();
    ctx.setLineDash([7, 5]);
    ctx.strokeStyle = '#f2a33a';
    ctx.lineWidth = 1.6;
    ctx.strokeRect(ax, ay, w, hh);
    ctx.restore();
    if (w > 60) {
      ctx.save();
      ctx.font = '600 12px "Averia Serif Libre", serif';
      const text = 'escondida · só você vê';
      const tw = ctx.measureText(text).width + 22;
      const lx = Math.max(ax, Math.min(ax + w - tw, ax + 6));
      const ly = Math.max(6, ay - 10);
      ctx.fillStyle = 'rgba(24,18,13,0.9)';
      ctx.beginPath();
      ctx.roundRect(lx, ly, tw, 18, 9);
      ctx.fill();
      ctx.strokeStyle = '#f2a33a';
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.fillStyle = '#f4efe6';
      ctx.textBaseline = 'middle';
      ctx.textAlign = 'left';
      ctx.fillText(text, lx + 16, ly + 9.5);
      ctx.beginPath();
      ctx.ellipse(lx + 9, ly + 9, 3.5, 2, 0, 0, Math.PI * 2);
      ctx.moveTo(lx + 5.5, ly + 12.5);
      ctx.lineTo(lx + 12.5, ly + 5.5);
      ctx.strokeStyle = '#f2a33a';
      ctx.stroke();
      ctx.restore();
    }
  }
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

  drawLoaded(ctx, mv);
  drawBaseAreas(ctx, mv);
  drawHiddenAreas(ctx, mv);

  ctx.shadowColor = 'rgba(0,0,0,0.8)';
  ctx.shadowBlur = 3;

  if (layers.beds) {
    for (const b of day ? [] : state.beds) {
      const [sx, sy] = mv.toScreen(b.x, b.z);
      if (!visible(sx, sy)) continue;
      const off = hiddenHere('bed', b);
      mv.icon('bed', sx, sy, iconSize * 0.75, off ? 0.45 : 0.9);
      if (off) hiddenBadge(ctx, sx, sy, iconSize * 0.4);
      hits.push({ sx, sy, r: iconSize * 0.4, title: 'Cama', lines: [b.owner ? `de ${b.owner}` : '', off ? 'escondida — só você vê' : ''].filter(Boolean), anchor: [b.x, b.z], card: () => bedCard(b) });
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
      const off = hiddenHere('portal', p);
      mv.icon('portal', sx, sy, iconSize * (lit ? 1 : 0.8), off ? 0.45 : p.connected || lit ? 1 : 0.55);
      if (off) hiddenBadge(ctx, sx, sy, iconSize * 0.4);
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
      const off = hiddenHere('chest', c);
      if (off) ctx.globalAlpha = 0.5;
      if (c.tomb) mv.icon('death', sx, sy, iconSize * 0.85, off ? 0.5 : 1);
      else drawChest(mv, sx, sy, mpp > 2.5 ? 12 : 15, match);
      ctx.globalAlpha = 1;
      if (off) hiddenBadge(ctx, sx, sy, 7);
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
      const off = hiddenHere('pin', p);
      mv.icon(name, sx, sy, iconSize, off ? 0.45 : p.checked ? 0.6 : 1);
      if (p.checked) mv.icon('checked', sx, sy, iconSize * 0.9, off ? 0.45 : 1);
      if (off) hiddenBadge(ctx, sx, sy, iconSize * 0.45);
      hits.push({
        sx, sy, r: iconSize * 0.45, title: p.name || 'Marcação',
        lines: [p.author ? `por ${p.author}` : '', p.checked ? 'riscada' : '', off ? 'escondida — só você vê' : ''].filter(Boolean),
        anchor: [p.x, p.z],
        card: () => pinCard(p),
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
      if (p.x == null) continue;
      const [sx, sy] = mv.toScreen(p.x, p.z);
      if (!visible(sx, sy)) continue;
      const off = myPlayerHide(p.name);
      mv.icon('player_32', sx, sy, iconSize + 4, off ? 0.5 : 1);
      if (off) hiddenBadge(ctx, sx, sy, iconSize * 0.5);
      hits.push({
        sx, sy, r: iconSize * 0.5, title: p.name,
        lines: [BIOME_NAMES[p.biome] ?? '', p.ping != null ? `ping ${(p.ping * 1000).toFixed(0)} ms` : '', off ? 'escondido — só você vê' : ''].filter(Boolean),
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
  $('status-text').textContent = online === 0 ? 'ninguém online' : online === 1 ? '1 viking online' : `${online} vikings online`;
  // Bolinha no botao do servidor: o jogo mira 120 FPS; abaixo de 60 ja se sente.
  const dot = $('server-dot');
  dot.className = `tool-dot ${s.fps == null ? '' : s.fps >= 90 ? 'good' : s.fps >= 50 ? 'warn' : 'bad'}`;
  $('tool-server').title = s.fps != null ? `Servidor · ${s.fps.toFixed(0)} FPS` : 'Servidor';
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
      el('span', { class: 'name', text: p.name + (myPlayerHide(p.name) ? ' · escondido' : '') }),
      el('span', { class: 'ping', text: p.ping != null ? `${(p.ping * 1000).toFixed(0)} ms` : '' }),
      el('span', { class: 'where', text: p.x == null ? 'posição escondida' : `${BIOME_NAMES[p.biome] ?? '—'} · ${p.x.toFixed(0)}, ${p.z.toFixed(0)}` }));
    li.addEventListener('click', () => {
      if (p.x == null) return (location.href = playerHref(p.name));
      closePops();
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
    $('server-dot').className = 'tool-dot bad';
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
  let active = -1;
  const buttons = () => [...list.querySelectorAll('button')];
  const highlight = (i) => {
    const all = buttons();
    active = all.length ? (i + all.length) % all.length : -1;
    all.forEach((b, k) => b.classList.toggle('active', k === active));
  };
  const suggest = () => {
    const q = normalize(input.value.trim());
    list.replaceChildren();
    active = -1;
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
    closePops();
  };
  const pick = (hash) => {
    found = hash;
    resultsOpen = true;
    input.value = itemName(items, hash);
    list.hidden = true;
    input.blur();
    renderSearchResults();
    mapView.invalidate();
  };
  input.addEventListener('input', suggest);
  input.addEventListener('focus', () => found == null && suggest());
  input.addEventListener('blur', () => setTimeout(() => (list.hidden = true), 150));
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (list.hidden) suggest();
      highlight(active + (e.key === 'ArrowDown' ? 1 : -1));
    }
    if (e.key === 'Enter') (buttons()[Math.max(0, active)])?.click();
    if (e.key === 'Escape') {
      if (!list.hidden) list.hidden = true;
      else input.blur();
    }
  });
  $('search-clear').addEventListener('click', () => {
    found = null;
    input.value = '';
    list.hidden = true;
    renderSearchResults();
    mapView.invalidate();
    input.focus();
  });
}

function renderSearchResults() {
  const out = $('search-results');
  $('search-clear').hidden = found == null && !$('search').value;
  out.replaceChildren();
  out.hidden = found == null || !world;
  if (out.hidden) return;
  const chests = world.containers
    .filter((c) => !c.tomb)
    .map((c) => ({ c, n: c.items.filter((i) => i[0] === found).reduce((a, i) => a + i[1], 0) }))
    .filter((r) => r.n > 0)
    .sort((a, b) => b.n - a.n);
  const total = chests.reduce((a, r) => a + r.n, 0);
  const head = el('button', {
    class: 'results-head', type: 'button', 'aria-expanded': String(resultsOpen),
    onclick: () => {
      resultsOpen = !resultsOpen;
      renderSearchResults();
    },
  },
  itemIcon(items, found, 22),
  el('span', { class: 'count' }, el('b', { text: fmt.format(total) }), ` em ${chests.length} ${chests.length === 1 ? 'baú' : 'baús'}`),
  el('span', { class: 'chev', 'aria-hidden': 'true' }));
  out.append(head);
  if (!resultsOpen) return;
  const ul = el('ul', { class: 'found' });
  for (const { c, n } of chests.slice(0, 30)) {
    const g = mapView.settlements && baseAt(mapView.settlements, c.x, c.z, BASE_MARGIN);
    const where = g && g.count >= BASE_MIN_PIECES ? baseName(g, state?.pins, world) : `${Math.round(c.x)}, ${Math.round(c.z)}`;
    ul.append(
      el('li', {},
        el('button', {
          type: 'button',
          onclick: () => {
            // No celular a lista cobre o mapa: recolhe ao escolher.
            if (window.matchMedia('(max-width: 720px)').matches) {
              resultsOpen = false;
              renderSearchResults();
            }
            mapView.goTo(c.x, c.z, Math.min(mapView.view.metersPerPixel, 0.8));
            openCard([c.x, c.z], () => chestCard(c));
          },
        }, el('span', { class: 'name', text: containerTitle(c) }), el('span', { class: 'qty', text: fmt.format(n) }), el('span', { class: 'where', text: where }))),
    );
  }
  if (chests.length > 30) ul.append(el('li', { class: 'more', text: `e mais ${chests.length - 30} baús com pouco` }));
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
  const live = $('day-live');
  const toggle = $('growth-toggle');
  const wrap = $('growth-wrap');
  toggle.addEventListener('click', () => {
    wrap.hidden = !wrap.hidden;
    toggle.setAttribute('aria-expanded', String(!wrap.hidden));
    $('timeline').classList.toggle('open', !wrap.hidden);
  });
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
    label.replaceChildren(...(d
      ? [`${dayMonth(Date.parse(`${d.date}T12:00`))} · ${d.time}`]
      : [el('span', { class: 'live-dot', 'aria-hidden': 'true' }), 'Ao vivo']));
    live.hidden = !d;
    info.textContent = d
      ? `${d.exploredKm2.toFixed(1)} km² explorados · ${d.pieces != null ? fmt.format(d.pieces) : '–'} construções · ${d.pins} marcações`
      : 'Construções por dia. Clique numa coluna para ver o mapa daquele dia.';
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
  const stop = () => {
    playing = false;
    play.textContent = '▶';
    play.setAttribute('aria-label', 'Tocar a linha do tempo');
  };
  live.addEventListener('click', () => {
    stop();
    go(ordered.length);
  });
  play.addEventListener('click', async () => {
    if (playing) return stop();
    playing = true;
    play.textContent = '❚❚';
    play.setAttribute('aria-label', 'Pausar');
    let i = Number(slider.value) >= ordered.length ? 0 : Number(slider.value);
    while (playing && i <= ordered.length) {
      const t0 = performance.now();
      await go(i);
      await loading;
      await new Promise((r) => setTimeout(r, Math.max(0, PLAY_STEP_MS - (performance.now() - t0))));
      i++;
    }
    stop();
  });
  describe();
}

// ---------- escondidos ----------

const KIND_ICONS = { base: 'house', chest: null, portal: 'portal', bed: 'bed', pin: 'pin', player: 'player_32' };

function renderHidden() {
  const mine = myHides();
  const count = $('hidden-count');
  count.hidden = !mine.length;
  count.textContent = String(mine.length);
  const list = $('hidden-list');
  list.replaceChildren();
  if (!mine.length) list.append(el('li', { class: 'empty', text: 'Nada escondido por este navegador.' }));
  for (const h of [...mine].sort((a, b) => b.at - a.at)) {
    const where = h.kind === 'player' ? null : [h.x, h.z];
    const img = KIND_ICONS[h.kind];
    list.append(el('li', {},
      img ? el('img', { src: `game/icons/${img}.png`, alt: '' }) : el('span', { class: 'chest-swatch', 'aria-hidden': 'true' }),
      el('span', { class: 'what' },
        el('span', { class: 'name', text: h.title || kindName(h.kind) }),
        el('span', { class: 'meta', text: `${kindName(h.kind)} · ${dayMonth(h.at)}${h.agent ? ` · ${h.agent}` : ''}` })),
      el('span', { class: 'acts' },
        where ? el('button', {
          type: 'button', class: 'btn small', text: 'Ir até',
          onclick: () => {
            closePops();
            if (h.kind === 'base') mapView.fit(h.box[0], h.box[1], h.box[2], h.box[3], 80);
            else mapView.flyTo(where[0], where[1], Math.min(mapView.view.metersPerPixel, 1.2));
          },
        }) : null,
        el('button', { type: 'button', class: 'btn small', title: 'Aparece de novo para todo mundo', onclick: () => unhide(h.id) }, 'Mostrar'))));
  }
}

// Escondeu ou mostrou: o servidor ja manda os dados de outro jeito, entao busca tudo de novo.
async function reloadAfterHidden() {
  renderHidden();
  mapView.invalidate();
  try {
    const [s, w] = await Promise.all([getJSON('api/state'), getJSON('api/world')]);
    state = s;
    world = w;
    renderPanel();
    renderSearchResults();
    if (mapView.renderer) await mapView.loadWorld(day);
    await loadTrails();
  } catch {}
  refreshCard();
  mapView.invalidate();
}

// ---------- popups ----------

function closePops(except = null) {
  for (const btn of document.querySelectorAll('[data-pop]')) {
    if (btn.dataset.pop === except) continue;
    btn.setAttribute('aria-expanded', 'false');
    $(btn.dataset.pop).hidden = true;
  }
}

function bindPops() {
  for (const btn of document.querySelectorAll('[data-pop]')) {
    btn.addEventListener('click', () => {
      const pop = $(btn.dataset.pop);
      closePops(btn.dataset.pop);
      pop.hidden = !pop.hidden;
      btn.setAttribute('aria-expanded', String(!pop.hidden));
    });
  }
  // Clique fora fecha; arrastar o mapa tambem.
  document.addEventListener('pointerdown', (e) => {
    if (e.target.closest('.pop, [data-pop]')) return;
    closePops();
  });
}

// ---------- entrada ----------

function loadPrefs() {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}');
    for (const k of Object.keys(layers)) if (typeof saved.layers?.[k] === 'boolean') layers[k] = saved.layers[k];
    if ([1, 6, 24].includes(saved.trailHours)) trailHours = saved.trailHours;
  } catch {}
}

function savePrefs() {
  try {
    localStorage.setItem(PREFS_KEY, JSON.stringify({ layers, trailHours }));
  } catch {}
}

function bindPanel() {
  loadPrefs();
  for (const input of document.querySelectorAll('#layers input')) {
    input.checked = layers[input.dataset.layer];
    input.addEventListener('change', () => {
      layers[input.dataset.layer] = input.checked;
      mapView.showPieces = layers.pieces;
      if (input.dataset.layer === 'trails') loadTrails();
      savePrefs();
      mapView.invalidate();
    });
  }
  // Atalhos embaixo da busca: espelho das caixas do popup de camadas.
  const quick = [...document.querySelectorAll('[data-quick]')];
  const markQuick = () => quick.forEach((b) => b.setAttribute('aria-pressed', String(layers[b.dataset.quick])));
  markQuick();
  for (const b of quick) {
    b.addEventListener('click', () => {
      const box = document.querySelector(`#layers input[data-layer="${b.dataset.quick}"]`);
      box.checked = !box.checked;
      box.dispatchEvent(new Event('change'));
    });
  }
  for (const input of document.querySelectorAll('#layers input')) input.addEventListener('change', markQuick);
  const hourButtons = [...document.querySelectorAll('#trail-hours button')];
  const markHours = () => hourButtons.forEach((b) => b.setAttribute('aria-pressed', String(Number(b.dataset.hours) === trailHours)));
  markHours();
  for (const b of hourButtons) {
    b.addEventListener('click', () => {
      trailHours = Number(b.dataset.hours);
      markHours();
      // Escolher a janela liga os rastros.
      layers.trails = true;
      document.querySelector('[data-layer="trails"]').checked = true;
      savePrefs();
      loadTrails();
    });
  }
  bindPops();
  const zoom = (k) => mapView.flyTo(mapView.view.x, mapView.view.z, mapView.view.metersPerPixel * k, 250);
  $('zoom-in').addEventListener('click', () => zoom(0.5));
  $('zoom-out').addEventListener('click', () => zoom(2));
  $('zoom-home').addEventListener('click', () => mapView.flyTo(HOME.x, HOME.z, HOME.metersPerPixel));
  document.addEventListener('keydown', (e) => {
    const typing = e.target.closest?.('input, textarea, select');
    if (e.key === 'Escape') {
      closeCard();
      closePops();
    }
    if (e.key === '/' && !typing) {
      e.preventDefault();
      $('search').focus();
    }
  });
}

async function main() {
  readHash();
  onHiddenChange(reloadAfterHidden);
  ready.then(renderHidden);
  bindPanel();
  bindSearch();
  pollState();
  pollHistory();
  pollWorld();
  setupTimeline();
  loadTrails();
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
