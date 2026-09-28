// Pagina de um jogador: /jogador/<nome>. Rastro no mapa, quanto e quando joga, o que construiu,
// lapides, camas e marcacoes.
import { MapView } from './mapview.js';
import {
  $, ago, BASE_MIN_PIECES, baseAt, baseHref, baseName, BIOME_NAMES, clock, columnChart, dayMonth, duration, el,
  fmt, getJSON, itemGrid, loadItems, mapHref,
} from './common.js';

const RANGES = [
  [1, '1 h'],
  [6, '6 h'],
  [24, '24 h'],
  [72, '3 dias'],
  [168, '7 dias'],
];
// Salto maior que isto num passo e portal (ou teleporte), nao caminhada.
const JUMP_METERS = 150;
const JUMP_SPEED = 25;

const name = decodeURIComponent(location.pathname.replace(/^\/jogador\//, '').replace(/\/$/, ''));
const sheet = $('sheet');
let hours = 24;
let trail = null; // { step, points: [[t, x, z]] }
let scrubAt = null;
let live = null; // entrada do /api/state se online
let tombs = [];
let beds = [];
let mapView = null;

const tile = (k, v, s) => el('div', { class: 'tile' }, el('div', { class: 'k', text: k }), el('div', { class: 'v', text: v }), s ? el('div', { class: 's', text: s }) : null);
const block = (title, ...children) => el('section', { class: 'block' }, el('h2', { text: title }), ...children);

// Pedacos continuos do rastro e o que eles somam.
function segments(points, step) {
  const runs = [];
  let run = [];
  let meters = 0;
  let jumps = 0;
  for (let k = 0; k < points.length; k++) {
    const p = points[k];
    const q = points[k - 1];
    if (q) {
      const dt = p[0] - q[0];
      const d = Math.hypot(p[1] - q[1], p[2] - q[2]);
      if (dt > step * 3) {
        runs.push(run);
        run = [];
      } else if (d > Math.max(JUMP_METERS, dt * JUMP_SPEED)) {
        jumps++;
        runs.push(run);
        run = [];
      } else {
        meters += d;
      }
    }
    run.push(p);
  }
  if (run.length) runs.push(run);
  return { runs: runs.filter((r) => r.length), meters, jumps };
}

function drawOverlay(ctx, mv) {
  if (trail?.points.length) {
    const { runs } = segments(trail.points, trail.step);
    const t0 = trail.points[0][0];
    const t1 = trail.points[trail.points.length - 1][0];
    for (const run of runs) {
      for (let k = 1; k < run.length; k++) {
        const [ax, ay] = mv.toScreen(run[k - 1][1], run[k - 1][2]);
        const [bx, by] = mv.toScreen(run[k][1], run[k][2]);
        const age = (t1 - run[k][0]) / Math.max(1, t1 - t0);
        ctx.globalAlpha = 1 - age * 0.65;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(bx, by);
        ctx.strokeStyle = 'rgba(255,245,225,0.6)';
        ctx.lineWidth = 5;
        ctx.stroke();
        ctx.strokeStyle = '#5b1f10';
        ctx.lineWidth = 2.5;
        ctx.stroke();
      }
    }
    ctx.globalAlpha = 1;
  }
  ctx.shadowColor = 'rgba(0,0,0,0.8)';
  ctx.shadowBlur = 3;
  for (const b of beds) mv.icon('bed', ...mv.toScreen(b.x, b.z), 18);
  for (const t of tombs) mv.icon('death', ...mv.toScreen(t.x, t.z), 22);
  const at = scrubAt ?? (live ? [null, live.x, live.z] : trail?.points.at(-1));
  if (at) {
    const [sx, sy] = mv.toScreen(at[1], at[2]);
    mv.icon('player_32', sx, sy, 28);
    ctx.shadowBlur = 0;
    mv.label(name, sx, sy + 16, 16);
  }
  ctx.shadowBlur = 0;
}

async function loadTrail(facts, scrub, out) {
  try {
    const data = await getJSON(`api/trails?hours=${hours}&name=${encodeURIComponent(name)}`);
    trail = { step: data.step, points: data.players.find((p) => p.name === name)?.points ?? [] };
  } catch {
    trail = { step: 60, points: [] };
  }
  scrubAt = null;
  const { meters, jumps } = segments(trail.points, trail.step);
  const range = RANGES.find(([h]) => h === hours)[1];
  facts.replaceChildren(
    ...(trail.points.length
      ? [
          el('span', {}, 'Andou ', el('b', { text: meters >= 1000 ? `${(meters / 1000).toFixed(1).replace('.', ',')} km` : `${Math.round(meters)} m` }), ` em ${range}`),
          el('span', {}, el('b', { text: fmt.format(jumps) }), jumps === 1 ? ' portal ou teleporte' : ' portais ou teleportes'),
        ]
      : [el('span', { text: `Sem rastro nas últimas ${range}.` })]),
  );
  scrub.max = String(Math.max(0, trail.points.length - 1));
  scrub.value = scrub.max;
  scrub.disabled = trail.points.length < 2;
  out.textContent = trail.points.length ? `${dayMonth(trail.points.at(-1)[0] * 1000)} ${clock(trail.points.at(-1)[0] * 1000)}` : '—';
  if (mapView) {
    const pts = trail.points;
    if (pts.length) {
      // Enquadra onde ele passou o tempo: um ponto solto do outro lado de um portal nao manda no zoom.
      const pick = (vals) => {
        const s = [...vals].sort((a, b) => a - b);
        return [s[Math.floor(s.length * 0.03)], s[Math.ceil(s.length * 0.97) - 1]];
      };
      const [x0, x1] = pick(pts.map((p) => p[1]));
      const [z0, z1] = pick(pts.map((p) => p[2]));
      mapView.fit(x0, z0, x1, z1, 50);
      if (mapView.view.metersPerPixel < 0.6) mapView.goTo(mapView.view.x, mapView.view.z, 0.6);
    } else if (live) {
      mapView.goTo(live.x, live.z, 1.5);
    }
    mapView.invalidate();
  }
}

// Minutos por dia (data local) e por hora do dia, a partir das horas cheias do Victoria.
function activity(hourly) {
  const byDay = new Map();
  const byHour = new Array(24).fill(0);
  const today = new Date();
  today.setHours(12, 0, 0, 0);
  for (let k = 29; k >= 0; k--) {
    const d = new Date(today.getTime() - k * 86400000);
    byDay.set(d.toDateString(), { ms: d.getTime(), minutes: 0 });
  }
  for (const [t, minutes] of hourly) {
    const mid = new Date((t - 1800) * 1000);
    const day = byDay.get(mid.toDateString());
    if (day) day.minutes += minutes;
    byHour[mid.getHours()] += minutes;
  }
  return { days: [...byDay.values()], byHour };
}

function basesOf(world, settlements) {
  const idx = world.builders.names.indexOf(name);
  if (idx < 0) return { total: 0, bases: [] };
  const cell = world.cell;
  const mine = new Map();
  let total = 0;
  for (const [cx, cz, who, n] of world.builders.cells) {
    if (who !== idx) continue;
    total += n;
    const g = baseAt(settlements, (cx + 0.5) * cell, (cz + 0.5) * cell, cell);
    if (g) mine.set(g, (mine.get(g) ?? 0) + n);
  }
  const bases = [...mine]
    .filter(([g, n]) => g.count >= BASE_MIN_PIECES && n >= 10)
    .map(([g, n]) => ({ g, n }))
    .sort((a, b) => b.n - a.n);
  return { total, bases };
}

async function main() {
  const [known, state, info, world, items] = await Promise.all([
    getJSON('api/players').catch(() => []),
    getJSON('api/state').catch(() => null),
    getJSON(`api/player?name=${encodeURIComponent(name)}`).catch(() => null),
    getJSON('api/world').catch(() => null),
    loadItems(),
  ]);
  const me = known.find((p) => p.name === name);
  if (!me || !info) {
    sheet.replaceChildren(el('p', { class: 'page-msg' }, `Não conheço nenhum viking chamado “${name}”. `, el('a', { href: './', text: 'Voltar ao mapa' })));
    return;
  }
  document.title = `${name} · Jahmaica`;
  // Online com a posicao escondida (escondidos): aparece como online, sem lugar.
  const online = state?.players.find((p) => p.name === name) ?? null;
  live = online?.x != null ? online : null;
  tombs = world?.containers.filter((c) => c.tomb && c.owner === name) ?? [];
  beds = world?.beds.filter((b) => b.name === name) ?? [];
  const pins = state?.pins.filter((p) => p.author === name) ?? [];
  const { days, byHour } = activity(info.hourly);
  const minutes30 = days.reduce((a, d) => a + d.minutes, 0);

  const status = online
    ? el('p', { class: 'online' }, el('span', { class: 'dot' }), `Online agora · ${live ? BIOME_NAMES[live.biome] ?? '—' : 'posição escondida'}${online.ping != null ? ` · ping ${(online.ping * 1000).toFixed(0)} ms` : ''}`)
    : el('p', {}, el('span', { class: 'dot' }), `Visto por último ${ago(me.lastSeen)}`);

  // Mapa com o rastro
  const map = el('canvas', { id: 'map', 'aria-label': `Rastro de ${name} no mapa` });
  const overlay = el('canvas', { id: 'overlay', 'aria-hidden': 'true' });
  const facts = el('p', { class: 'facts' });
  const scrub = el('input', { type: 'range', min: '0', max: '0', value: '0', 'aria-label': 'Momento do rastro' });
  const scrubOut = el('output', { text: '—' });
  const buttons = RANGES.map(([h, label]) =>
    el('button', {
      type: 'button',
      'aria-pressed': String(h === hours),
      text: label,
      onclick: (e) => {
        hours = h;
        for (const b of buttons) b.setAttribute('aria-pressed', String(b === e.currentTarget));
        loadTrail(facts, scrub, scrubOut);
      },
    }));
  scrub.addEventListener('input', () => {
    const p = trail?.points[Number(scrub.value)];
    if (!p) return;
    scrubAt = p;
    scrubOut.textContent = `${dayMonth(p[0] * 1000)} ${clock(p[0] * 1000)}`;
    mapView?.invalidate();
  });

  sheet.replaceChildren(
    el('section', { class: 'hero' }, el('h1', { text: name }), status),
    el('div', { class: 'tiles' },
      tile('Jogou em 7 dias', duration(me.minutes7d)),
      tile('Jogou em 30 dias', duration(minutes30)),
      tile('Desde', dayMonth(me.firstSeen), `${duration(info.minutesTotal)} no total`),
      el('div', { class: 'tile', id: 'built-tile' }, el('div', { class: 'k', text: 'Peças construídas' }), el('div', { class: 'v', text: '…' })),
      tile('Mortes esperando', fmt.format(tombs.length), tombs.length ? 'lápides com itens' : 'nenhuma lápide'),
    ),
    block('Por onde andou',
      el('div', { class: 'range-buttons', role: 'group', 'aria-label': 'Janela do rastro' }, buttons),
      el('div', { class: 'minimap' }, map, overlay, el('span', { class: 'map-note', text: 'arraste e use a roda para ver de perto' })),
      el('div', { class: 'scrub' }, el('span', { text: 'Voltar no tempo' }), scrub, scrubOut),
      facts),
    block('Quando joga',
      el('div', { class: 'two-charts' },
        el('div', {},
          el('h3', { text: 'Horas por dia, últimos 30 dias' }),
          columnChart(days.map((d) => ({ label: dayMonth(d.ms), value: d.minutes / 60, tip: duration(d.minutes) })), {
            height: 110,
            format: (v) => `${v.toFixed(1).replace('.', ',')} h`,
          })),
        el('div', {},
          el('h3', { text: 'Hora do dia em que mais joga (30 dias)' }),
          columnChart(byHour.map((m, h) => ({ label: `${String(h).padStart(2, '0')}h`, value: m / 60, tip: duration(m) })), {
            height: 110,
            format: (v) => `${v.toFixed(1).replace('.', ',')} h`,
          })))),
    el('div', { class: 'grid2' },
      el('section', { class: 'block', id: 'bases' }, el('h2', { text: 'Bases que ergueu' }), el('p', { class: 'empty', text: 'Procurando…' })),
      block('Camas',
        beds.length
          ? el('ul', { class: 'list' }, beds.map((b) => el('li', {}, el('a', { href: mapHref(b.x, b.z), text: `${Math.round(b.x)}, ${Math.round(b.z)}` }), el('span', { class: 'meta', text: 'ver no mapa' }))))
          : el('p', { class: 'empty', text: 'Nenhuma cama com o nome dele.' })),
    ),
    tombs.length
      ? block('Lápides',
          el('p', { class: 'hint', text: 'Itens que ficaram para trás e ainda esperam ser buscados.' }),
          el('div', { class: 'chests' },
            tombs.map((t) =>
              el('article', { class: 'chest' },
                el('h3', { text: `${Math.round(t.x)}, ${Math.round(t.z)}` }),
                el('div', { class: 'meta' }, el('span', { text: `${t.items.length} itens` }), el('a', { href: mapHref(t.x, t.z, 0.8), text: 'ver no mapa' })),
                itemGrid(items, t.items, { limit: 30 })))))
      : null,
    pins.length
      ? block('Marcações nas mesas',
          el('ul', { class: 'list' },
            pins.slice(0, 40).map((p) =>
              el('li', {}, el('a', { href: mapHref(p.x, p.z, 1.5), text: p.name || 'sem nome' }), el('span', { class: 'meta', text: `${p.checked ? 'riscada · ' : ''}${Math.round(p.x)}, ${Math.round(p.z)}` })))))
      : null,
  );

  mapView = new MapView({ map, overlay, view: live ? { x: live.x, z: live.z, metersPerPixel: 1.5 } : {}, icons: ['player_32', 'bed', 'death'], onDraw: drawOverlay });
  loadTrail(facts, scrub, scrubOut);
  try {
    await mapView.start();
  } catch (err) {
    map.parentElement.replaceChildren(el('p', { class: 'page-msg', text: `Não deu para abrir o mapa: ${err.message}` }));
    return;
  }
  // Bases dependem dos agrupamentos de pecas, que so existem depois de carregar o mapa.
  const found = world ? basesOf(world, mapView.settlements) : { total: 0, bases: [] };
  $('built-tile').querySelector('.v').textContent = fmt.format(found.total);
  const basesBox = $('bases');
  basesBox.replaceChildren(
    el('h2', { text: 'Bases que ergueu' }),
    found.bases.length
      ? el('ul', { class: 'list' },
          found.bases.slice(0, 12).map(({ g, n }) =>
            el('li', {},
              el('a', { href: baseHref(g), text: baseName(g, state?.pins, world) }),
              el('span', { class: 'meta', text: `${fmt.format(n)} de ${fmt.format(g.count)} peças (${Math.round((n / g.count) * 100)}%)` }))))
      : el('p', { class: 'empty', text: 'Nenhuma base com peças dele por enquanto.' }),
  );
  if (live) {
    setInterval(async () => {
      try {
        const s = await getJSON('api/state');
        live = s.players.find((p) => p.name === name && p.x != null) ?? null;
        mapView.invalidate();
      } catch {}
    }, 15000);
  }
}

main();
