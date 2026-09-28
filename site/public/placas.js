// Editor de placas: texto livre, a placa como o jogo desenha (signsim.js) e o que colar, em ordem
// (signcode.js, as regras do plugin). Icones e artes vem do servidor (/api/signs), lidos do mesmo
// catalogo que o plugin usa: arte nova no custom.txt do servidor aparece aqui sem deploy.
import { SignSim } from './signsim.js';
import { LIMIT, buildIcon, compose, flat, hoverText, normalize, parseIcon, plan } from './signcode.js';

const $ = (id) => document.getElementById(id);
const STORE = 'valheim.placa';
const SAMPLE = '<#fc6>Hidromel\n<size=4>{u}<#f80>da casa';
const DEFAULT_UNITS = 7.6;

const ta = $('text');
const mirror = $('mirror');
const canvas = $('sign');

let index = null;
const byId = new Map();
const entries = new Map();
let current = null;
const copied = new Set();

// O catalogo como signcode.plan quer: desenho chega sob demanda e redesenha quando chega.
const catalog = {
  params: {},
  macros: {},
  known: (key) => !!index && (byId.has(key) || Object.hasOwn(index.aliases, key)),
  resolve: (key) => (!index ? null : byId.has(key) ? key : index.aliases[key] ?? index.def),
  entry: (id) => {
    const e = entries.get(id);
    if (e && !(e instanceof Promise)) return e;
    if (!e) loadEntry(id).then(schedule, () => {});
    return undefined;
  },
};

function loadEntry(id) {
  const known = entries.get(id);
  if (known) return known instanceof Promise ? known : Promise.resolve(known);
  const p = fetch(`api/signs/entry/${id}?v=${index.v}`)
    .then((r) => (r.ok ? r.json() : Promise.reject(new Error(r.status))))
    .then((e) => { entries.set(id, e); return e; }, (err) => { entries.delete(id); throw err; });
  entries.set(id, p);
  return p;
}

// ---- estado ----

let queued = false;
function schedule() {
  if (queued) return;
  queued = true;
  requestAnimationFrame(() => { queued = false; update(); });
}

let saveTimer = 0;
function save(text) {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => { try { localStorage.setItem(STORE, text); } catch {} }, 400);
}

function update() {
  const source = ta.value;
  const p = plan(source, index ? catalog : null);
  current = p;
  const unknown = new Set(SignSim.unknown(p.kind === 'icon' ? '' : flat(source), catalog.macros));
  paintMirror(source, p, unknown);
  paintSign(p);
  paintKnobs(p);
  paintSteps(p);
  paintStats(p, unknown);
  markLibrary(p);
  save(source);
}

// ---- a placa ----

function meters(v) { return `${v.toFixed(v < 10 ? 1 : 0).replace('.', ',')} m`; }

function paintSign(p) {
  const loading = p.kind === 'icon' && p.final == null;
  const info = SignSim.render(canvas, loading ? '' : p.final, { macros: {} });
  const badge = $('overflow');
  badge.hidden = !loading && !info.overflow;
  badge.textContent = loading ? 'carregando o desenho…'
    : `${info.clipped ? 'Maior que a vista' : 'Passa da tábua'}: ${meters(info.width)} × ${meters(info.height)}`;
  const hover = $('hover');
  const read = p.kind === 'icon' ? p.icon.label.text.replace(/<[^<>]*>/g, '') : hoverText(p.final);
  hover.hidden = !read;
  hover.innerHTML = '';
  hover.append('Quem mira a placa lê: ');
  const b = document.createElement('b');
  b.textContent = read;
  hover.append(b);
}

// Lado e brilho do icone, escritos de volta no codigo (<size=N> e N%).
function paintKnobs(p) {
  const box = $('knobs');
  box.hidden = p.kind !== 'icon';
  if (box.hidden) return;
  const { size, brightness } = p.icon;
  if (document.activeElement !== $('size')) $('size').value = size ?? DEFAULT_UNITS;
  if (document.activeElement !== $('light')) $('light').value = Math.min(140, Math.max(10, brightness ?? 100));
  $('size-out').textContent = size == null ? 'padrão' : String(size).replace('.', ',');
  $('light-out').textContent = brightness == null ? 'padrão' : `${brightness}%`;
  $('knobs-reset').hidden = size == null && brightness == null;
}

function rewriteIcon(change) {
  const p = parseIcon(flat(ta.value));
  if (!p) return;
  replaceAll(buildIcon({ ...p, ...change }));
}
$('size').addEventListener('input', (e) => rewriteIcon({ size: Number(e.target.value) }));
$('light').addEventListener('input', (e) => rewriteIcon({ brightness: Number(e.target.value) === 100 ? null : Number(e.target.value) }));
$('knobs-reset').addEventListener('click', () => rewriteIcon({ size: null, brightness: null }));

// ---- o campo: textarea por cima de um espelho que pinta tags, abreviacoes e codigos ----

const TOKEN = /<[^<>\n]{1,128}>|\{[A-Za-z0-9]+\}|\\n|:[^:<>\n]{1,60}:/g;
const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function tokenClass(tok, p, unknown) {
  if (tok[0] === '<') {
    if (unknown.has(tok)) return ['bad'];
    const color = /^<(?:#|color=#)([0-9a-fA-F]{3,8})>$/.exec(tok);
    return ['t', color ? `#${color[1].length <= 4 ? color[1].slice(0, 3) : color[1].slice(0, 6)}` : null];
  }
  if (tok[0] === '{') return Object.hasOwn(catalog.macros, tok.slice(1, -1).toLowerCase()) ? ['m'] : null;
  if (tok === '\\n') return ['n'];
  const inner = tok.slice(1, -1).replace(/\s*[0-9]{1,4}\s*%$/, '');
  const key = normalize(inner);
  if (!key) return null;
  if (p.kind === 'icon' && normalize(p.icon.inner) === key) return [p.known ? 'i' : 'bad'];
  return catalog.known(key) ? ['i'] : null;
}

function paintMirror(source, p, unknown) {
  let html = '';
  let last = 0;
  for (const m of source.matchAll(TOKEN)) {
    const cls = tokenClass(m[0], p, unknown);
    if (!cls) continue;
    html += esc(source.slice(last, m.index));
    const style = cls[1] ? ` style="--sw:${cls[1]}"` : '';
    html += `<span class="${cls[0]}"${style}>${esc(m[0])}</span>`;
    last = m.index + m[0].length;
  }
  mirror.innerHTML = `${html}${esc(source.slice(last))}\n `;
  mirror.scrollTop = ta.scrollTop;
}
ta.addEventListener('scroll', () => { mirror.scrollTop = ta.scrollTop; });
ta.addEventListener('input', schedule);

// Troca pelo editor (entra no desfazer do navegador quando da).
function replaceRange(start, end, text, selectFrom, selectTo) {
  ta.focus();
  ta.setSelectionRange(start, end);
  if (!document.execCommand?.('insertText', false, text)) ta.setRangeText(text, start, end, 'end');
  if (selectFrom != null) ta.setSelectionRange(start + selectFrom, start + selectTo);
  schedule();
}
const replaceAll = (text) => replaceRange(0, ta.value.length, text);

// ---- barra de inserir ----

function wrap(open, close, pick) {
  const { selectionStart: s, selectionEnd: e, value } = ta;
  const inside = value.slice(s, e);
  const text = open + inside + (inside ? close : '');
  const at = pick ? open.indexOf(pick) : -1;
  if (at >= 0) replaceRange(s, e, text, at, at + pick.length);
  else replaceRange(s, e, text, text.length, text.length);
}

document.querySelector('.toolbar').addEventListener('click', (e) => {
  const kind = e.target.closest('[data-insert]')?.dataset.insert;
  if (kind === 'size') wrap('<size=5>', '</size>', '5');
  else if (kind === 'unlit') wrap('{u}', '</material>');
  else if (kind === 'mark') wrap('<mark=#000000aa>', '</mark>', '000000aa');
});

// Cor em 12 bits: <#f80> custa 3 caracteres a menos que <#ff8800> e no jogo nao se ve diferenca. Enquanto o
// seletor esta aberto, a mesma tag e reescrita.
let picking = null;
function hex3(value) {
  return [1, 3, 5].map((i) => Math.round(parseInt(value.substr(i, 2), 16) / 17).toString(16)).join('');
}
$('color').addEventListener('input', (e) => {
  const tag = `<#${hex3(e.target.value)}>`;
  if (picking && ta.value.slice(picking.start, picking.start + picking.length) === picking.tag) {
    replaceRange(picking.start, picking.start + picking.length, tag, tag.length, tag.length);
  } else {
    const start = ta.selectionStart;
    replaceRange(start, ta.selectionEnd, tag, tag.length, tag.length);
    picking = { start };
  }
  Object.assign(picking, { tag, length: tag.length });
});
$('color').addEventListener('change', () => { picking = null; });
$('color').addEventListener('click', () => { picking = null; });

$('clear').addEventListener('click', () => replaceAll(''));

function encodeShare(text) {
  const bytes = new TextEncoder().encode(text);
  let bin = '';
  bytes.forEach((b) => { bin += String.fromCharCode(b); });
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function decodeShare(code) {
  const bin = atob(code.replace(/-/g, '+').replace(/_/g, '/'));
  return new TextDecoder().decode(Uint8Array.from(bin, (c) => c.charCodeAt(0)));
}
$('share').addEventListener('click', async (e) => {
  const url = `${location.origin}/placas#t=${encodeShare(ta.value)}`;
  const ok = await copy(url);
  flash(e.currentTarget, ok ? 'Link copiado' : 'Não deu para copiar');
});

// ---- o passo a passo ----

async function copy(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const helper = Object.assign(document.createElement('textarea'), { value: text });
    helper.style.cssText = 'position:fixed;opacity:0';
    document.body.append(helper);
    helper.select();
    const ok = document.execCommand('copy');
    helper.remove();
    return ok;
  }
}

function flash(button, text) {
  const was = button.dataset.label ?? button.textContent;
  button.dataset.label = was;
  button.textContent = text;
  clearTimeout(button._flash);
  button._flash = setTimeout(() => { button.textContent = was; }, 1400);
}

function step(html, sub) {
  const li = document.createElement('li');
  li.className = 'step';
  li.innerHTML = `<p>${html}</p>${sub ? `<p class="sub">${sub}</p>` : ''}`;
  return li;
}

const K = (k) => `<kbd>${k}</kbd>`;

function paintSteps(p) {
  const list = $('steps');
  list.textContent = '';
  const note = $('steps-done');
  note.hidden = true;
  if (!p.parts.length) {
    const li = document.createElement('li');
    li.className = 'step-empty';
    li.textContent = 'Escreva no editor: aqui aparece o que colar na placa, em ordem.';
    list.append(li);
    return;
  }
  const many = p.parts.length > 1;
  list.append(step(`Mire a placa, aperte ${K('E')}, apague o que houver (${K('Ctrl')}+${K('A')}, ${K('Delete')}) e confirme vazia com ${K('Enter')}.`,
    'Placa nova pode pular. Numa que já tinha ícone ou texto longo, é isso que faz o servidor esquecer o anterior.'));
  let next = null;
  p.parts.forEach((part, n) => {
    const key = `${n}\u0001${part}`;
    const first = n === 0;
    const li = step(first
      ? `Aperte ${K('E')}, cole com ${K('Ctrl')}+${K('V')} e confirme com ${K('Enter')}.`
      : `Aperte ${K('E')} de novo, apague o que o campo mostrar, cole a continuação e ${K('Enter')}.`,
    !first && n === 1 ? 'Começa com >>: o servidor emenda no que a placa já tem. Até o último pedaço a placa mostra só o começo.' : '');
    const box = document.createElement('div');
    box.className = 'paste';
    const pre = document.createElement('pre');
    pre.textContent = part;
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'copy';
    btn.textContent = copied.has(key) ? 'Copiado' : 'Copiar';
    btn.addEventListener('click', async () => {
      if (!(await copy(part))) return flash(btn, 'Falhou');
      copied.add(key);
      paintSteps(current);
      list.querySelector('[data-next] .copy')?.focus({ preventScroll: false });
    });
    const meta = document.createElement('span');
    meta.className = 'meta';
    meta.textContent = `${many ? `${n + 1} de ${p.parts.length} · ` : ''}${part.length} de ${LIMIT} caracteres`;
    box.append(pre, btn, meta);
    li.append(box);
    if (copied.has(key)) li.dataset.done = '';
    else if (!next) next = li;
    list.append(li);
  });
  if (next) next.dataset.next = '';
  const last = p.kind === 'icon' ? 'Em até um segundo o servidor troca o código pelo desenho.'
    : p.kind === 'long' || p.kind === 'macro' ? 'Em até um segundo o servidor grava o texto inteiro, como na prévia.'
      : 'Pronto: a placa fica como na prévia.';
  list.append(step(last, p.kind === 'icon' || p.kind === 'long' || p.kind === 'macro'
    ? 'Quem abre a placa depois vê o texto cortado no campo; se confirmar sem mexer, o servidor devolve o inteiro.' : ''));

  const saved = flat(ta.value).length - p.typed.length;
  if (saved > 0) {
    note.hidden = false;
    note.textContent = `Encurtado sem mudar a placa: ${saved} caractere${saved > 1 ? 's' : ''} a menos (cores de 3 dígitos e {u}).`;
  }
}

// ---- rodape do editor ----

function paintStats(p, unknown) {
  const stats = $('stats');
  const n = p.typed.length;
  stats.innerHTML = !n ? '' : p.parts.length <= 1
    ? `<strong>${n}</strong> de ${LIMIT} · cabe numa colada`
    : `<strong>${n}</strong> caracteres · <strong>${p.parts.length}</strong> coladas`;
  const problems = [];
  if (p.kind === 'icon') {
    if (!p.known) problems.push(`:${p.icon.inner}: não existe; o jogo mostra o ícone padrão`);
    if (p.mode === 'hover') problems.push('nome com mais de 22 letras só aparece ao mirar');
  }
  if (p.notes.includes('icon-too-long')) problems.push('código de ícone só vale numa colada (até 50): assim sai escrito');
  if (p.notes.includes('starts-with-continuation')) problems.push('começar com >> emenda no texto que a placa já tem');
  if (unknown.size) problems.push(`o jogo não entende ${[...unknown].slice(0, 3).join(' ')}: sai escrito na placa`);
  $('problems').textContent = problems.join(' · ');
}

// ---- biblioteca ----

let tab = 'items';
let itemTiles = [];
const artTiles = new Map();

function nameOf(id) {
  return byId.get(id)?.name || id;
}

// O codigo que o clique escreve. Sem rotulo, quem mira le o que esta dentro do codigo: vale o nome
// legivel; com rotulo, o mais curto.
function codeFor(id, withLabel) {
  const info = byId.get(id);
  const codes = [id, ...(info?.aliases ?? [])];
  const shortest = codes.reduce((a, b) => (b.length < a.length ? b : a));
  const name = info?.name;
  if (!withLabel && name && name.length <= 30 && catalog.resolve(normalize(name)) === id) return name;
  return withLabel || !info?.art ? shortest : id;
}

function useCode(id) {
  const text = ta.value;
  const p = parseIcon(flat(text));
  if (p) replaceAll(buildIcon({ ...p, inner: codeFor(id, p.label.shown) }));
  else if (!text.trim()) replaceAll(`:${codeFor(id, false)}:`);
  else replaceAll(`${text.replace(/\s+$/, '')} :${codeFor(id, true)}:`);
}

function markLibrary(p) {
  const id = p.kind === 'icon' ? p.id : null;
  document.querySelectorAll('.lib-list [aria-pressed="true"]').forEach((b) => b.setAttribute('aria-pressed', 'false'));
  if (!id) return;
  (byId.get(id)?.tile)?.setAttribute('aria-pressed', 'true');
}

function buildItems() {
  const grid = document.createElement('div');
  grid.className = 'icon-grid';
  const px = 36;
  const url = `api/signs/atlas.png?v=${index.v}`;
  const rows = Math.ceil(index.items.length / index.cols);
  const sorted = index.items.map(([id], n) => ({ id, n })).sort((a, b) => nameOf(a.id).localeCompare(nameOf(b.id), 'pt'));
  itemTiles = sorted.map(({ id, n }) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'icon-tile';
    b.setAttribute('aria-pressed', 'false');
    b.setAttribute('aria-label', nameOf(id));
    b.dataset.id = id;
    const i = document.createElement('i');
    i.style.backgroundImage = `url(${url})`;
    i.style.backgroundSize = `${index.cols * px}px ${rows * px}px`;
    i.style.backgroundPosition = `${-(n % index.cols) * px}px ${-Math.floor(n / index.cols) * px}px`;
    b.append(i);
    byId.get(id).tile = b;
    grid.append(b);
    return b;
  });
  return grid;
}

// Miniatura de arte: o desenho como o servidor grava para `:codigo:`, na mesma simulacao da placa.
const thumbs = new IntersectionObserver((seen) => {
  for (const e of seen) {
    if (!e.isIntersecting) continue;
    thumbs.unobserve(e.target);
    const id = e.target.closest('.art').dataset.id;
    loadEntry(id).then((entry) => {
      const c = e.target;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      c.width = Math.round(c.clientWidth * dpr);
      c.height = Math.round(c.clientHeight * dpr);
      SignSim.still(c, compose(entry, { text: id, shown: false }, null, null, catalog), { macros: {} });
    }, () => {});
  }
}, { root: $('lib-list'), rootMargin: '120px' });

function artTile(id) {
  let b = artTiles.get(id);
  if (b) return b;
  b = document.createElement('button');
  b.type = 'button';
  b.className = 'art';
  b.dataset.id = id;
  b.setAttribute('aria-pressed', 'false');
  const c = document.createElement('canvas');
  const label = document.createElement('span');
  label.textContent = nameOf(id);
  b.append(c, label);
  byId.get(id).tile = b;
  artTiles.set(id, b);
  thumbs.observe(c);
  return b;
}

function showLibrary() {
  const list = $('lib-list');
  const q = normalize($('lib-q').value);
  list.textContent = '';
  $('tab-items').setAttribute('aria-selected', String(tab === 'items'));
  $('tab-arts').setAttribute('aria-selected', String(tab === 'arts'));
  list.setAttribute('aria-labelledby', tab === 'items' ? 'tab-items' : 'tab-arts');
  $('lib-q').placeholder = tab === 'items' ? 'Procurar item…' : 'Procurar arte…';
  if (!index) {
    list.innerHTML = '<p class="lib-empty">Sem catálogo de ícones neste servidor.</p>';
    return;
  }
  const hit = (id) => !q || byId.get(id).hay.includes(q);
  if (tab === 'items') {
    if (!itemTiles.length) $('lib-list').append(buildItems());
    else list.append(itemTiles[0].parentElement);
    let shown = 0;
    for (const b of itemTiles) {
      const on = hit(b.dataset.id);
      b.hidden = !on;
      shown += on;
    }
    if (!shown) list.insertAdjacentHTML('beforeend', '<p class="lib-empty">Nenhum item com esse nome.</p>');
    $('lib-foot').textContent = 'Clique para pôr o código no texto. Escreva um nome antes dele para sair na tábua.';
  } else {
    let any = false;
    for (const g of index.groups) {
      const ids = q ? g.ids.filter(hit) : g.shown ?? g.ids;
      if (!ids.length) continue;
      any = true;
      const sec = document.createElement('section');
      sec.className = 'art-group';
      const h = document.createElement('h3');
      h.textContent = g.title;
      sec.append(h);
      if (g.hint) {
        const hint = document.createElement('p');
        hint.className = 'hint';
        hint.textContent = g.hint;
        sec.append(hint);
      }
      const grid = document.createElement('div');
      grid.className = 'art-grid';
      ids.slice(0, 40).forEach((id) => grid.append(artTile(id)));
      sec.append(grid);
      const rest = (q ? ids.length : g.ids.length) - Math.min(ids.length, 40);
      if (rest > 0) sec.insertAdjacentHTML('beforeend', `<p class="lib-more">+ ${rest} pelo nome${q ? '' : ' (procure acima)'}</p>`);
      list.append(sec);
    }
    if (!any) list.insertAdjacentHTML('beforeend', '<p class="lib-empty">Nenhuma arte com esse nome.</p>');
    $('lib-foot').textContent = 'Artes feitas para o servidor. Clique para pôr o código no texto.';
  }
  if (current) markLibrary(current);
}

$('tab-items').addEventListener('click', () => { tab = 'items'; showLibrary(); });
$('tab-arts').addEventListener('click', () => { tab = 'arts'; showLibrary(); });
$('lib-q').addEventListener('input', showLibrary);
$('lib-list').addEventListener('click', (e) => {
  const b = e.target.closest('[data-id]');
  if (b) useCode(b.dataset.id);
});

// ---- dicas ----

const tip = $('tip');
let tipTimer = 0;
document.addEventListener('mouseover', (e) => {
  const el = e.target.closest('[data-tip], .lib-list [data-id]');
  clearTimeout(tipTimer);
  if (!el) { tip.hidden = true; return; }
  const text = el.dataset.tip ?? `${nameOf(el.dataset.id)} · :${codeFor(el.dataset.id, false)}:`;
  tipTimer = setTimeout(() => {
    tip.textContent = text;
    tip.hidden = false;
    const r = el.getBoundingClientRect();
    const t = tip.getBoundingClientRect();
    tip.style.left = `${Math.max(8, Math.min(innerWidth - t.width - 8, r.left + r.width / 2 - t.width / 2))}px`;
    tip.style.top = `${r.bottom + 6 + t.height > innerHeight ? r.top - t.height - 6 : r.bottom + 6}px`;
  }, tip.hidden ? 350 : 0);
});

// ---- inicio ----

function startText() {
  const shared = /^#t=([A-Za-z0-9_-]*)$/.exec(location.hash);
  if (shared) {
    try {
      history.replaceState(null, '', location.pathname);
      return decodeShare(shared[1]);
    } catch {}
  }
  try { return localStorage.getItem(STORE) ?? SAMPLE; } catch { return SAMPLE; }
}

async function loadIndex() {
  const state = $('catalog-state');
  try {
    const r = await fetch('api/signs');
    if (!r.ok) throw new Error(r.status);
    index = await r.json();
  } catch {
    state.textContent = 'Sem catálogo de ícones: só texto.';
    state.classList.add('bad');
    showLibrary();
    return;
  }
  catalog.params = index.params;
  catalog.macros = index.macros;
  const aliases = new Map();
  for (const [alias, id] of Object.entries(index.aliases)) {
    if (!aliases.has(id)) aliases.set(id, []);
    aliases.get(id).push(alias);
  }
  const add = (id, name) => {
    const list = aliases.get(id) ?? [];
    byId.set(id, { name, aliases: list, hay: `${id} ${normalize(name)} ${list.join(' ')}`, tile: null });
  };
  index.items.forEach(([id, name]) => add(id, name));
  index.groups.forEach((g) => g.ids.forEach((id) => {
    add(id, index.names[id] ?? '');
    byId.get(id).art = true;
  }));
  const arts = index.groups.reduce((n, g) => n + g.ids.length, 0);
  $('count-items').textContent = index.items.length;
  $('count-arts').textContent = arts;
  showLibrary();
  schedule();
}

ta.value = startText();
SignSim.onFonts(schedule);
document.fonts?.ready.then(schedule);
update();
loadIndex();
