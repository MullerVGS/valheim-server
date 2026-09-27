// Pagina de uma base: /base/<x>,<z> (centro da caixa quando o link foi feito). A base e o agrupamento de
// pecas que contem o ponto; se ela cresceu e o centro mudou, o ponto continua dentro dela.
import { MapView } from './mapview.js';
import {
  $, BASE_MARGIN, baseAt, baseName, biomeAt, buildersIn, columnChart, containerTitle, dayMonth,
  el, fmt, getJSON, inBox, itemGrid, itemName, loadItems, materials, normalize, PIN_ICONS, playerHref, shareBars,
} from './common.js';

const [cx, cz] = location.pathname.replace(/^\/base\//, '').split(',').map(Number);
const sheet = $('sheet');
let g = null;
let chests = [];
let flash = null;
let state = null;
let world = null;

const tile = (k, v, s) => el('div', { class: 'tile' }, el('div', { class: 'k', text: k }), el('div', { class: 'v', text: v }), s ? el('div', { class: 's', text: s }) : null);
const block = (title, ...children) => el('section', { class: 'block' }, el('h2', { text: title }), ...children);

function drawOverlay(ctx, mv) {
  if (!g) return;
  // Contorno da area da base, pontilhado como num mapa desenhado.
  const [ax, ay] = mv.toScreen(g.minX - BASE_MARGIN, g.maxZ + BASE_MARGIN);
  const [bx, by] = mv.toScreen(g.maxX + BASE_MARGIN, g.minZ - BASE_MARGIN);
  ctx.setLineDash([6, 5]);
  ctx.strokeStyle = 'rgba(40,20,8,0.7)';
  ctx.lineWidth = 2;
  ctx.strokeRect(ax, ay, bx - ax, by - ay);
  ctx.setLineDash([]);
  ctx.shadowColor = 'rgba(0,0,0,0.8)';
  ctx.shadowBlur = 2;
  const mpp = mv.view.metersPerPixel;
  const size = mpp > 1.5 ? 11 : 15;
  for (const c of chests) {
    const [sx, sy] = mv.toScreen(c.x, c.z);
    const hot = c === flash;
    if (hot) {
      ctx.beginPath();
      ctx.arc(sx, sy, size, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(242,163,58,0.4)';
      ctx.fill();
      ctx.strokeStyle = '#f2a33a';
      ctx.lineWidth = 2;
      ctx.stroke();
    }
    ctx.fillStyle = '#7a4a22';
    ctx.strokeStyle = '#1b0f06';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.roundRect(sx - size / 2, sy - size * 0.36, size, size * 0.72, 2);
    ctx.fill();
    ctx.stroke();
  }
  for (const b of world?.beds ?? []) if (inBox(b.x, b.z, g, BASE_MARGIN)) mv.icon('bed', ...mv.toScreen(b.x, b.z), 18);
  for (const p of state?.portals ?? []) if (inBox(p.x, p.z, g, BASE_MARGIN)) mv.icon('portal', ...mv.toScreen(p.x, p.z), 20, p.connected ? 1 : 0.55);
  for (const p of state?.pins ?? []) {
    if (!inBox(p.x, p.z, g, 15)) continue;
    const [sx, sy] = mv.toScreen(p.x, p.z);
    mv.icon(PIN_ICONS[p.type] ?? 'pin', sx, sy, 22);
    ctx.shadowBlur = 0;
    mv.label(p.name, sx, sy + 11, 15);
    ctx.shadowBlur = 2;
  }
  for (const p of state?.players ?? []) {
    const [sx, sy] = mv.toScreen(p.x, p.z);
    mv.icon('player_32', sx, sy, 26);
    ctx.shadowBlur = 0;
    mv.label(p.name, sx, sy + 14, 16);
    ctx.shadowBlur = 2;
  }
  ctx.shadowBlur = 0;
}

// Clique num bau do mapa leva ao cartao dele na lista.
function clickMap(mv, sx, sy) {
  let best = null;
  let bestD = 14;
  for (const c of chests) {
    const [x, y] = mv.toScreen(c.x, c.z);
    const d = Math.hypot(x - sx, y - sy);
    if (d < bestD) {
      bestD = d;
      best = c;
    }
  }
  if (!best) return;
  showChest(best, mv, false);
}

function showChest(c, mv, center) {
  flash = c;
  if (center) mv.goTo(c.x, c.z, Math.min(mv.view.metersPerPixel, 0.6));
  mv.invalidate();
  for (const node of document.querySelectorAll('.chest.flash')) node.classList.remove('flash');
  showAllChests();
  const node = document.getElementById(`chest-${chests.indexOf(c)}`);
  if (!node) return;
  node.classList.add('flash');
  if (!center) node.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

// Base grande tem centenas de baus: os primeiros abertos, o resto atras de um botao.
const CHESTS_SHOWN = 12;
function chestList(nodes) {
  const box = el('div', { class: 'chests' }, nodes.slice(0, CHESTS_SHOWN));
  if (nodes.length <= CHESTS_SHOWN) return box;
  const more = el('button', { class: 'more-button', type: 'button', text: `Mostrar os outros ${nodes.length - CHESTS_SHOWN} baús` });
  more.addEventListener('click', () => {
    box.append(...nodes.slice(CHESTS_SHOWN));
    more.remove();
  });
  return el('div', {}, box, more);
}

function showAllChests() {
  document.querySelector('.more-button')?.click();
}

function stockBlock(items) {
  const all = chests.flatMap((c) => c.items);
  const grid = el('div', { class: 'stock' }, itemGrid(items, all));
  const filter = el('input', { class: 'filter', type: 'search', placeholder: 'filtrar itens…', 'aria-label': 'Filtrar itens do estoque' });
  filter.addEventListener('input', () => {
    const q = normalize(filter.value.trim());
    const list = q ? all.filter(([hash]) => normalize(itemName(items, hash)).includes(q) || normalize(items.byHash.get(hash)?.[2] ?? '').includes(q)) : all;
    grid.replaceChildren(list.length ? itemGrid(items, list) : el('p', { class: 'empty', text: 'Nada disso aqui.' }));
  });
  return block('Estoque', el('p', { class: 'hint', text: `Tudo o que está nos ${chests.length} baús da base, somado.` }), filter, grid);
}

async function main() {
  const map = el('canvas', { id: 'map', 'aria-label': 'Mapa da base' });
  const overlay = el('canvas', { id: 'overlay', 'aria-hidden': 'true' });
  const mapBox = el('div', { class: 'minimap' }, map, overlay, el('span', { class: 'map-note', text: 'clique num baú para ver o que tem dentro' }));
  // O mapa precisa estar na pagina para medir o canvas; o resto chega depois.
  sheet.replaceChildren(el('p', { class: 'page-msg', text: 'Carregando…' }), el('div', { hidden: true }, mapBox));

  const mapView = new MapView({
    map,
    overlay,
    view: { x: cx, z: cz, metersPerPixel: 1 },
    icons: ['player_32', 'bed', 'portal', 'fire', 'house', 'hammer', 'pin', 'boss'],
    onDraw: drawOverlay,
    onClick: (sx, sy) => clickMap(mapView, sx, sy),
  });
  let items;
  try {
    [state, world, items] = await Promise.all([
      getJSON('api/state').catch(() => null),
      getJSON('api/world').catch(() => null),
      loadItems(),
      mapView.start(),
    ]);
  } catch (err) {
    sheet.replaceChildren(el('p', { class: 'page-msg', text: `Não deu para abrir o mapa: ${err.message}` }));
    return;
  }
  const { settlements } = mapView;
  g = Number.isFinite(cx) ? baseAt(settlements, cx, cz, 20) : null;
  if (!g) {
    sheet.replaceChildren(el('p', { class: 'page-msg' }, 'Não achei construção nenhuma aqui. ', el('a', { href: './', text: 'Voltar ao mapa' })));
    return;
  }
  const name = baseName(g, state?.pins, world);
  document.title = `${name} · Valheim`;
  $('back').href = `./#${Math.round((g.minX + g.maxX) / 2)},${Math.round((g.minZ + g.maxZ) / 2)},1`;
  chests = (world?.containers ?? []).filter((c) => !c.tomb && inBox(c.x, c.z, g, BASE_MARGIN)).sort((a, b) => b.items.length - a.items.length);
  const stored = chests.reduce((a, c) => a + c.items.reduce((s, i) => s + i[1], 0), 0);
  const builders = buildersIn(world, g);
  const beds = (world?.beds ?? []).filter((b) => inBox(b.x, b.z, g, BASE_MARGIN));
  const portals = (state?.portals ?? []).filter((p) => inBox(p.x, p.z, g, BASE_MARGIN));
  const here = (state?.players ?? []).filter((p) => inBox(p.x, p.z, g, BASE_MARGIN));
  const w = Math.round(g.maxX - g.minX);
  const h = Math.round(g.maxZ - g.minZ);
  const biome = biomeAt(mapView.terrain, (g.minX + g.maxX) / 2, (g.minZ + g.maxZ) / 2);
  const growth = el('div', {}, el('p', { class: 'empty', text: 'Contando os dias…' }));

  mapBox.parentElement.hidden = false;
  sheet.replaceChildren(
    el('section', { class: 'hero' },
      el('h1', { text: name }),
      el('p', {}, `${biome ?? ''}${biome ? ' · ' : ''}${Math.round((g.minX + g.maxX) / 2)}, ${Math.round((g.minZ + g.maxZ) / 2)} · ${w} × ${h} m`),
      here.length ? el('p', { class: 'online' }, el('span', { class: 'dot' }), `Aqui agora: ${here.map((p) => p.name).join(', ')}`) : null),
    el('div', { class: 'tiles' },
      tile('Peças', fmt.format(g.count)),
      tile('Baús', fmt.format(chests.length), `${fmt.format(stored)} itens guardados`),
      tile('Camas', fmt.format(beds.length), beds.length ? [...new Set(beds.map((b) => b.name))].join(', ') : 'ninguém dorme aqui'),
      tile('Portais', fmt.format(portals.length), portals.map((p) => p.tag).filter(Boolean).slice(0, 3).join(', ') || null),
    ),
    block('No mapa', mapBox),
    el('div', { class: 'grid2' },
      block('Construída por',
        builders.length
          ? shareBars(builders, { href: (r) => playerHref(r.name) })
          : el('p', { class: 'empty', text: 'Sem registro de quem construiu (o save ainda não passou por aqui).' })),
      block('Materiais', shareBars(materials(g).slice(0, 8))),
    ),
    block('Crescimento', el('p', { class: 'hint', text: 'Peças dentro da área da base no fim de cada dia guardado.' }), growth),
    chests.length ? stockBlock(items) : null,
    chests.length
      ? block('Baús',
          chestList(chests.map((c, i) =>
              el('article', { class: 'chest', id: `chest-${i}` },
                el('h3', { text: containerTitle(c) }),
                el('div', { class: 'meta' },
                  el('span', { text: `${c.label ? `${c.kind} · ` : ''}${c.owner ? `de ${c.owner}` : ''}` }),
                  el('button', { type: 'button', text: 'no mapa', onclick: () => { mapBox.scrollIntoView({ behavior: 'smooth', block: 'center' }); showChest(c, mapView, true); } })),
                c.items.length ? itemGrid(items, c.items, { limit: 40 }) : el('p', { class: 'empty', text: 'Vazio.' })))))
      : block('Baús', el('p', { class: 'empty', text: 'Nenhum baú nesta base.' })),
    beds.length || portals.length
      ? el('div', { class: 'grid2' },
          beds.length
            ? block('Quem dorme aqui', el('ul', { class: 'list' }, beds.map((b) => el('li', {}, el('a', { href: playerHref(b.name), text: b.name }), el('span', { class: 'meta', text: `${Math.round(b.x)}, ${Math.round(b.z)}` })))))
            : null,
          portals.length
            ? block('Portais', el('ul', { class: 'list' }, portals.map((p) => el('li', {}, el('span', { text: p.tag || 'sem nome' }), el('span', { class: 'meta', text: p.connected ? 'conectado' : 'sem par' })))))
            : null)
      : null,
  );
  mapView.resize();
  mapView.fit(g.minX - BASE_MARGIN, g.minZ - BASE_MARGIN, g.maxX + BASE_MARGIN, g.maxZ + BASE_MARGIN, 30);

  try {
    const days = await getJSON(`api/days/area?x0=${g.minX - BASE_MARGIN}&z0=${g.minZ - BASE_MARGIN}&x1=${g.maxX + BASE_MARGIN}&z1=${g.maxZ + BASE_MARGIN}`);
    const first = days.find((d) => d.pieces > 0);
    const rows = days.map((d) => ({ label: dayMonth(Date.parse(`${d.date}T12:00`)), value: d.pieces, tip: `${fmt.format(d.pieces)} peças` }));
    rows.push({ label: 'agora', value: g.count, tip: `${fmt.format(g.count)} peças` });
    growth.replaceChildren(
      columnChart(rows, { height: 110, format: (v) => fmt.format(v) }),
      el('p', { class: 'facts' },
        first
          ? el('span', {}, 'Aparece pela primeira vez em ', el('b', { text: dayMonth(Date.parse(`${first.date}T12:00`)) }), days[0] === first ? ' (o primeiro dia guardado)' : '')
          : el('span', { text: 'Nova: ainda não aparece em nenhum dia guardado.' })),
    );
  } catch {
    growth.replaceChildren(el('p', { class: 'empty', text: 'Sem histórico por enquanto.' }));
  }
}

main();
