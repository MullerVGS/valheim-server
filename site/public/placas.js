// Editor de placas: texto livre, a placa como o jogo desenha (signsim.js) e o que colar, em ordem
// (signcode.js, as regras do plugin). Icones e artes vem do servidor (/api/signs), lidos do mesmo
// catalogo que o plugin usa: arte nova no custom.txt do servidor aparece aqui sem deploy.
import { SignSim } from './signsim.js';
import { LIMIT, buildIcon, compose, flat, hoverText, normalize, parseIcon, plan } from './signcode.js';
import { serialize } from './signrich.js';
import { RichEditor } from './richedit.js';

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

// ---- o editor: visual (padrao) ou codigo. O textarea guarda o texto nos dois modos. ----

const MODE_KEY = 'valheim.placa.modo';
let visual = true;

const iconKey = (inner) => parseIcon(`:${inner}:`)?.key;
const rich = new RichEditor($('rich'), {
  env: {
    isTag: (raw) => SignSim.unknown(raw, {}).length === 0,
    isIcon: (inner) => { const key = iconKey(inner); return !!key && catalog.known(key); },
    isMacro: (name) => Object.hasOwn(catalog.macros, name),
  },
  chip: chipFor,
  onChange: (src) => { ta.value = src; schedule(); },
  onSelect: () => { paintToolbar(); caretMoved(); },
});

// Chip do editor visual: icone com a imagem e o nome; abreviacao e tag sem botao, como no codigo.
function chipFor(a) {
  const el = document.createElement('span');
  el.className = `chip chip-${a.kind}`;
  el.title = a.raw;
  if (a.kind !== 'icon') {
    el.textContent = a.raw;
    return el;
  }
  const p = parseIcon(a.raw);
  const id = p && catalog.resolve(p.key);
  const info = id && byId.get(id);
  const pic = document.createElement('i');
  if (info?.n != null) atlasCell(pic, info.n, 22);
  else pic.className = 'art';
  const name = document.createElement('span');
  name.textContent = (info?.name || p?.inner || a.raw) + (p?.brightness != null ? ` ${p.brightness}%` : '');
  el.append(pic, name);
  return el;
}

function setMode(toVisual, focus = true) {
  visual = toVisual;
  try { localStorage.setItem(MODE_KEY, visual ? 'rich' : 'code'); } catch {}
  $('mode-rich').setAttribute('aria-selected', String(visual));
  $('mode-code').setAttribute('aria-selected', String(!visual));
  document.querySelector('.editor').classList.toggle('is-code', !visual);
  closeSuggest();
  closePop();
  if (visual) {
    // O cursor passa para o mesmo ponto do codigo.
    const a = ta.selectionStart;
    const b = ta.selectionEnd;
    rich.setSource(ta.value);
    rich.sel = { start: rich.atomIndex(a), end: rich.atomIndex(b) };
    $('rich').hidden = false;
    ta.hidden = mirror.hidden = true;
    if (focus) rich.focus();
  } else {
    const { pos } = serialize(rich.atoms);
    const a = rich.src === ta.value ? pos[rich.sel.start] : ta.value.length;
    const b = rich.src === ta.value ? pos[rich.sel.end] : ta.value.length;
    $('rich').hidden = true;
    ta.hidden = mirror.hidden = false;
    if (focus) { ta.focus(); ta.setSelectionRange(a, b); }
  }
  paintToolbar();
  schedule();
}
$('mode-rich').addEventListener('click', () => setMode(true));
$('mode-code').addEventListener('click', () => setMode(false));

// ---- estado ----

let queued = false;
function schedule() {
  if (queued) return;
  queued = true;
  requestAnimationFrame(() => { queued = false; update(); });
}
let lastSource = null;
let lastUnknown = new Set();

let saveTimer = 0;
function save(text) {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => { try { localStorage.setItem(STORE, text); } catch {} }, 400);
}

// force: o catalogo chegou (ou um desenho): refaz o plano mesmo com o texto igual.
function update(force = true) {
  const source = ta.value;
  if (force || source !== lastSource || !current) {
    lastSource = source;
    const p = plan(source, index ? catalog : null);
    current = p;
    lastUnknown = new Set(SignSim.unknown(p.kind === 'icon' ? '' : flat(source), catalog.macros));
    paintSign(p);
    paintKnobs(p);
    paintSteps(p);
    paintStats(p, lastUnknown);
    markLibrary(p);
    save(source);
  }
  if (!visual) paintMirror(source, current, lastUnknown);
  $('rich').classList.toggle('whole-icon', current.kind === 'icon');
  suggest();
}
let caretQueued = false;
function caretMoved() {
  if (caretQueued) return;
  caretQueued = true;
  requestAnimationFrame(() => { caretQueued = false; update(false); });
}

// ---- a placa ----

function meters(v) { return `${v.toFixed(v < 10 ? 1 : 0).replace('.', ',')} m`; }

function paintSign(p) {
  const loading = p.kind === 'icon' && p.final == null;
  const info = SignSim.render(canvas, loading ? '' : p.final, { macros: {} });
  if (p.kind !== 'icon') rich.setAutoSize(info.size);
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

// O espelho leva um marcador vazio onde esta o cursor: e dele que o autocompletar tira a posicao.
const CARET = '<i class="caret-at" id="caret-at"></i>';
function paintMirror(source, p, unknown) {
  const at = ta.selectionStart === ta.selectionEnd ? ta.selectionEnd : -1;
  let html = '';
  let last = 0;
  let placed = false;
  const plain = (a, b) => {
    if (!placed && at >= a && at <= b) {
      placed = true;
      return esc(source.slice(a, at)) + CARET + esc(source.slice(at, b));
    }
    return esc(source.slice(a, b));
  };
  for (const m of source.matchAll(TOKEN)) {
    const cls = tokenClass(m[0], p, unknown);
    if (!cls) continue;
    html += plain(last, m.index);
    const style = cls[1] ? ` style="--sw:${cls[1]}"` : '';
    const end = m.index + m[0].length;
    const inside = !placed && at > m.index && at < end;
    if (inside) placed = true;
    const body = inside ? esc(m[0].slice(0, at - m.index)) + CARET + esc(m[0].slice(at - m.index)) : esc(m[0]);
    html += `<span class="${cls[0]}"${style}>${body}</span>`;
    last = end;
  }
  mirror.innerHTML = `${html}${plain(last, source.length)}\n `;
  mirror.scrollTop = ta.scrollTop;
}
ta.addEventListener('scroll', () => { mirror.scrollTop = ta.scrollTop; placeSuggest(); });
ta.addEventListener('input', schedule);
ta.addEventListener('click', caretMoved);
ta.addEventListener('keyup', (e) => { if (!['ArrowUp', 'ArrowDown', 'Enter', 'Tab', 'Escape'].includes(e.key) || ac.hidden) caretMoved(); });
ta.addEventListener('blur', () => setTimeout(() => { if (document.activeElement !== ta) closeSuggest(); }, 120));

// Troca pelo editor (entra no desfazer do navegador quando da).
function replaceRange(start, end, text, selectFrom, selectTo) {
  ta.focus();
  ta.setSelectionRange(start, end);
  if (!document.execCommand?.('insertText', false, text)) ta.setRangeText(text, start, end, 'end');
  if (selectFrom != null) ta.setSelectionRange(start + selectFrom, start + selectTo);
  schedule();
}
function replaceAll(text) {
  if (!visual) return replaceRange(0, ta.value.length, text);
  rich.setSource(text, { record: true });
}
function insertAtCaret(text, pick) {
  if (visual) {
    rich.focus();
    return rich.insertSource(text);
  }
  const at = pick ? text.indexOf(pick) : -1;
  if (at >= 0) replaceRange(ta.selectionStart, ta.selectionEnd, text, at, at + pick.length);
  else replaceRange(ta.selectionStart, ta.selectionEnd, text, text.length, text.length);
}

// ---- barra: formatar ----
// No visual, o botao muda o estilo do trecho escolhido (ou do que for digitado dali em diante). No codigo,
// poe as tags em volta da selecao.

function wrap(open, close, pick) {
  if (visual) {
    rich.focus();
    return rich.wrapSource(open, close);
  }
  const { selectionStart: s, selectionEnd: e, value } = ta;
  const inside = value.slice(s, e);
  const text = open + inside + (inside ? close : '');
  const at = pick ? open.indexOf(pick) : -1;
  if (at >= 0) replaceRange(s, e, text, at, at + pick.length);
  else replaceRange(s, e, text, text.length, text.length);
}

const TAGS = {
  c: (v) => [`<#${v}>`, '</color>'],
  z: (v) => [`<size=${v}>`, '</size>'],
  m: (v) => [`<mark=#${v}>`, '</mark>'],
  l: () => ['{u}', '</material>'],
  i: () => ['<i>', '</i>'],
  u: () => ['<u>', '</u>'],
  s: () => ['<s>', '</s>'],
};

// value: o novo valor, null = padrao, 'toggle' = liga/desliga. how 'pick': o seletor de cor ainda aberto.
function format(key, value, how) {
  if (visual) {
    if (value === 'toggle') rich.toggle(key);
    else rich.applyStyle(key, value, how);
    if (how !== 'pick') rich.focus();
    return;
  }
  const [open, close] = TAGS[key](value);
  if (value == null) insertAtCaret(close);
  else wrap(open, close);
}

// Cores com cara de Valheim, em 3 digitos (<#f80> custa 3 caracteres a menos que <#ff8800>).
const COLORS = [['fff', 'Branco'], ['fc6', 'Dourado'], ['f80', 'Laranja'], ['ff0', 'Amarelo'], ['f44', 'Vermelho'],
  ['f6c', 'Rosa'], ['c6f', 'Roxo'], ['69f', 'Azul'], ['4cf', 'Ciano'], ['6d4', 'Verde'], ['a63', 'Madeira'], ['888', 'Cinza'], ['000', 'Preto']];
const MARKS = [['000000aa', 'Sombra'], ['00000066', 'Sombra leve'], ['ffffff44', 'Clara'], ['ff880066', 'Laranja'],
  ['ff333366', 'Vermelha'], ['44dd4455', 'Verde'], ['3399ff66', 'Azul']];
const SIZES = ['2', '3', '4', '5', '6', '8'];

function hex3(value) {
  return [1, 3, 5].map((i) => Math.round(parseInt(value.substr(i, 2), 16) / 17).toString(16)).join('');
}

const pop = $('pop');
let popFor = null;

function swatch(value, label, current, key) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = value ? 'sw' : 'sw sw-none';
  if (value) b.style.setProperty('--c', `#${value}`);
  b.setAttribute('aria-label', label);
  b.dataset.tip = value ? `${label} · <#${value}>` : label;
  b.setAttribute('aria-pressed', String((current ?? null) === value));
  b.addEventListener('click', () => { closePop(); format(key, value); });
  return b;
}

function popTitle(text) {
  const h = document.createElement('p');
  h.className = 'pop-title';
  h.textContent = text;
  return h;
}

function openPop(btn) {
  if (popFor === btn) return closePop();
  closePop();
  const kind = btn.dataset.pop;
  const cur = visual ? rich.currentStyle() : {};
  popFor = btn;
  btn.setAttribute('aria-expanded', 'true');
  pop.textContent = '';
  pop.dataset.kind = kind;
  if (kind === 'color' || kind === 'mark') {
    const key = kind === 'color' ? 'c' : 'm';
    pop.append(popTitle(kind === 'color' ? 'Cor do texto' : 'Faixa atrás do texto'));
    const grid = document.createElement('div');
    grid.className = 'sw-grid';
    grid.append(swatch(null, kind === 'color' ? 'Padrão da placa' : 'Sem faixa', cur[key], key));
    for (const [v, label] of kind === 'color' ? COLORS : MARKS) grid.append(swatch(v, label, cur[key], key));
    pop.append(grid);
    if (kind === 'color') {
      const custom = document.createElement('label');
      custom.className = 'sw-custom';
      const input = document.createElement('input');
      input.type = 'color';
      input.value = cur.c && /^[0-9a-f]{3}$/.test(cur.c) ? `#${[...cur.c].map((d) => d + d).join('')}`
        : cur.c && /^[0-9a-f]{6}/.test(cur.c) ? `#${cur.c.slice(0, 6)}` : '#ff8800';
      // No visual a cor muda enquanto o seletor anda; no codigo entra so quando ele fecha.
      input.addEventListener('input', () => { if (visual) format('c', hex3(input.value), 'pick'); });
      input.addEventListener('change', () => { if (!visual) format('c', hex3(input.value)); closePop(); if (visual) rich.focus(); });
      custom.append(input, document.createTextNode('Outra cor…'));
      pop.append(custom);
    }
  } else if (kind === 'size') {
    pop.append(popTitle('Tamanho da letra'));
    const row = document.createElement('div');
    row.className = 'size-row';
    for (const v of [null, ...SIZES]) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'size-opt';
      b.textContent = v ?? 'Auto';
      if (v) b.style.fontSize = `${10 + Number(v) * 1.6}px`;
      b.setAttribute('aria-pressed', String((cur.z ?? null) === v));
      b.addEventListener('click', () => { closePop(); format('z', v); });
      row.append(b);
    }
    pop.append(row);
    const other = document.createElement('label');
    other.className = 'size-other';
    const input = document.createElement('input');
    input.type = 'number';
    input.min = '0.5';
    input.max = '40';
    input.step = '0.5';
    input.placeholder = 'outro';
    if (cur.z && cur.z !== 'mixed' && !SIZES.includes(cur.z)) input.value = cur.z;
    input.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      const v = Number(input.value);
      if (v > 0) { closePop(); format('z', String(Math.round(v * 10) / 10)); }
    });
    other.append(input, document.createTextNode(' Enter aplica'));
    pop.append(other);
    pop.insertAdjacentHTML('beforeend', '<p class="pop-note">Auto: a placa encaixa o texto sozinha (1 a 8). Tábua: 20 × 10.</p>');
  }
  pop.hidden = false;
  const r = btn.getBoundingClientRect();
  const w = pop.offsetWidth;
  pop.style.left = `${Math.max(8, Math.min(innerWidth - w - 8, r.left))}px`;
  pop.style.top = `${r.bottom + 6}px`;
}

function closePop() {
  if (!popFor) return;
  popFor.setAttribute('aria-expanded', 'false');
  popFor = null;
  pop.hidden = true;
}

const toolbar = document.querySelector('.toolbar');
// Botao da barra nao rouba o foco: a selecao do texto continua la.
for (const el of [toolbar, pop]) {
  el.addEventListener('pointerdown', (e) => {
    if (e.target.closest('button') && !e.target.closest('#lib-open, .modes')) e.preventDefault();
  });
}
toolbar.addEventListener('click', (e) => {
  const popBtn = e.target.closest('[data-pop]');
  if (popBtn) return openPop(popBtn);
  const key = e.target.closest('[data-toggle]')?.dataset.toggle;
  if (key) format(key, 'toggle');
});
document.addEventListener('pointerdown', (e) => {
  if (popFor && !pop.contains(e.target) && !popFor.contains(e.target)) closePop();
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && popFor) {
    closePop();
    if (visual) rich.focus();
  }
});

// O estado do trecho escolhido nos botoes.
function paintToolbar() {
  const st = visual ? rich.currentStyle() : null;
  const paintSwatch = (el, v) => {
    el.classList.toggle('mixed', v === 'mixed');
    el.classList.toggle('none', !v);
    el.style.background = v && v !== 'mixed' ? `#${v}` : '';
  };
  paintSwatch($('color-swatch'), st?.c);
  paintSwatch($('mark-swatch'), st?.m);
  $('size-val').textContent = !st ? '' : st.z === 'mixed' ? 'vários' : st.z ?? 'auto';
  for (const b of toolbar.querySelectorAll('[data-toggle]')) {
    const v = st?.[b.dataset.toggle];
    b.setAttribute('aria-pressed', v === 'mixed' ? 'mixed' : String(v === true));
  }
}

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

function step(html, soft) {
  const li = document.createElement('li');
  li.className = soft ? 'step soft' : 'step';
  li.innerHTML = `<p>${html}</p>`;
  return li;
}

const K = (k) => `<kbd>${k}</kbd>`;
const ARROW = ' → ';

function paintSteps(p) {
  const list = $('steps');
  list.textContent = '';
  const note = $('steps-done');
  note.hidden = true;
  if (!p.parts.length) {
    const li = document.createElement('li');
    li.className = 'step-empty';
    li.textContent = 'Escreva ao lado.';
    list.append(li);
    return;
  }
  const many = p.parts.length > 1;
  const server = p.kind === 'icon' || p.kind === 'long' || p.kind === 'macro';
  list.append(step(`Placa usada: ${K('E')}${ARROW}${K('Ctrl')}+${K('A')} ${K('Del')}${ARROW}${K('Enter')}`, true));
  let next = null;
  p.parts.forEach((part, n) => {
    const key = `${n}\u0001${part}`;
    const li = step(n === 0
      ? `${K('E')}${ARROW}colar${ARROW}${K('Enter')}`
      : `${K('E')}${ARROW}apagar${ARROW}colar${ARROW}${K('Enter')}`);
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
    meta.textContent = `${many ? `${n + 1}/${p.parts.length} · ` : ''}${part.length}/${LIMIT}`;
    box.append(pre, btn, meta);
    li.append(box);
    if (copied.has(key)) li.dataset.done = '';
    else if (!next) next = li;
    list.append(li);
  });
  if (next) next.dataset.next = '';
  list.append(step(p.kind === 'icon' ? '~1 s: servidor desenha.'
    : server ? '~1 s: servidor grava tudo.' : 'Pronto.', true));
  if (server) list.append(step('Campo mostra cortado: normal. Confirmar sem mexer não estraga.', true));

  const saved = flat(ta.value).length - p.typed.length;
  if (saved > 0) {
    note.hidden = false;
    note.textContent = `−${saved} caractere${saved > 1 ? 's' : ''} (cor curta, {u}), mesma placa.`;
  }
}

// ---- rodape do editor ----

function paintStats(p, unknown) {
  const stats = $('stats');
  const n = p.typed.length;
  stats.innerHTML = !n ? '' : p.parts.length <= 1
    ? `<strong>${n}</strong>/${LIMIT} · 1 colada`
    : `<strong>${n}</strong> caracteres · <strong>${p.parts.length}</strong> coladas`;
  const problems = [];
  if (p.kind === 'icon') {
    if (!p.known) problems.push(`:${p.icon.inner}: não existe → ícone padrão`);
    if (p.mode === 'hover') problems.push('nome > 22 letras: só ao mirar');
  }
  if (p.notes.includes('icon-too-long')) problems.push('ícone só vale sozinho, até 50: sai escrito');
  if (p.notes.includes('starts-with-continuation')) problems.push('>> no começo emenda no que já está na placa');
  if (unknown.size) problems.push(`jogo não entende ${[...unknown].slice(0, 3).join(' ')}: sai escrito`);
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

// Placa que ja e so um icone troca o icone; senao o codigo entra no cursor.
function useCode(id) {
  const text = ta.value;
  const p = parseIcon(flat(text));
  if (p) return replaceAll(buildIcon({ ...p, inner: codeFor(id, p.label.shown) }));
  const code = `:${codeFor(id, !!text.trim())}:`;
  const before = visual ? rich.textBefore(1) : text.slice(0, ta.selectionStart);
  insertAtCaret(before && !/\s$/.test(before) ? ` ${code}` : code);
}

function markLibrary(p) {
  const id = p.kind === 'icon' ? p.id : null;
  document.querySelectorAll('.lib-list [aria-pressed="true"]').forEach((b) => b.setAttribute('aria-pressed', 'false'));
  if (!id) return;
  (byId.get(id)?.tile)?.setAttribute('aria-pressed', 'true');
}

function atlasCell(el, n, px) {
  const rows = Math.ceil(index.items.length / index.cols);
  el.style.backgroundImage = `url(api/signs/atlas.png?v=${index.v})`;
  el.style.backgroundSize = `${index.cols * px}px ${rows * px}px`;
  el.style.backgroundPosition = `${-(n % index.cols) * px}px ${-Math.floor(n / index.cols) * px}px`;
}

function buildItems() {
  const grid = document.createElement('div');
  grid.className = 'icon-grid';
  const px = 36;
  const sorted = index.items.map(([id], n) => ({ id, n })).sort((a, b) => nameOf(a.id).localeCompare(nameOf(b.id), 'pt'));
  itemTiles = sorted.map(({ id, n }) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'icon-tile';
    b.setAttribute('aria-pressed', 'false');
    b.setAttribute('aria-label', nameOf(id));
    b.dataset.id = id;
    const i = document.createElement('i');
    atlasCell(i, n, px);
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
  if (lib.hidden) return;
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
    $('lib-foot').textContent = 'Clique: código no cursor. Shift+clique: mais de um.';
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
    $('lib-foot').textContent = 'Artes do servidor. Placa inteira = só o código.';
  }
  if (current) markLibrary(current);
}

$('tab-items').addEventListener('click', () => { tab = 'items'; showLibrary(); });
$('tab-arts').addEventListener('click', () => { tab = 'arts'; showLibrary(); });
$('lib-q').addEventListener('input', showLibrary);
$('lib-list').addEventListener('click', (e) => {
  const b = e.target.closest('[data-id]');
  if (!b) return;
  useCode(b.dataset.id);
  if (!e.shiftKey) openLibrary(false);
});

// A gaveta: fechada ate o clique; a grade de itens so e montada na primeira abertura.
const lib = $('lib');
function openLibrary(open, which) {
  if (which) tab = which;
  lib.hidden = !open;
  $('lib-open').setAttribute('aria-expanded', String(open));
  if (open) {
    closeSuggest();
    showLibrary();
    $('lib-q').focus();
  } else if (document.activeElement && lib.contains(document.activeElement)) focusEditor();
}
$('lib-open').addEventListener('click', () => openLibrary(lib.hidden));
$('lib-close').addEventListener('click', () => openLibrary(false));
document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !lib.hidden) openLibrary(false); });
document.addEventListener('pointerdown', (e) => {
  if (!lib.hidden && !lib.contains(e.target) && !e.target.closest('#lib-open')) openLibrary(false);
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

// ---- autocompletar: ":" + nome abre a lista de icones no cursor ----

const ac = $('ac');
const QUERY = /:([A-Za-zÀ-ÿ][^:<>\n%]{0,40})$/;
let searchList = [];
let acRows = [];
let acSel = 0;
let acStart = -1;
let acDismissed = -1;

// Melhor chave de cada icone para a busca: igual, comeca com, contem (3+ letras). Itens antes de artes.
function search(q, max) {
  const hits = [];
  for (const e of searchList) {
    let best = null;
    for (const key of e.keys) {
      const score = key === q ? 0 : key.startsWith(q) ? 1 : q.length >= 3 && key.includes(q) ? 2 : 9;
      if (score < 9 && (!best || score < best.score || (score === best.score && key.length < best.key.length))) best = { score, key };
    }
    if (best) hits.push({ id: e.id, art: e.art, ...best });
  }
  hits.sort((a, b) => a.score - b.score || a.art - b.art || a.key.length - b.key.length);
  return hits.slice(0, max);
}

function closeSuggest() {
  ac.hidden = true;
  acRows = [];
  acStart = -1;
}

// O mesmo autocompletar nos dois modos: o texto antes do cursor e onde o cursor esta na tela.
function caretState() {
  if (visual) {
    if (!rich.hasFocus() || rich.sel.start !== rich.sel.end) return null;
    return { at: rich.sel.end, before: rich.textBefore(42) };
  }
  if (document.activeElement !== ta || ta.selectionStart !== ta.selectionEnd) return null;
  const at = ta.selectionEnd;
  return { at, before: ta.value.slice(Math.max(0, at - 42), at) };
}

function suggest() {
  const caret = index && lib.hidden ? caretState() : null;
  if (!caret) return closeSuggest();
  const m = QUERY.exec(caret.before);
  if (!m) { acDismissed = -1; return closeSuggest(); }
  // No visual o cursor anda em pecas (um code point cada); no codigo, em unidades do texto.
  const start = caret.at - (visual ? [...m[0]].length : m[0].length);
  if (start === acDismissed) return closeSuggest();
  const hits = search(normalize(m[1]), 8);
  if (!hits.length) return closeSuggest();
  if (start !== acStart) acSel = 0;
  acStart = start;
  acRows = hits;
  acSel = Math.min(acSel, hits.length - 1);
  ac.textContent = '';
  hits.forEach((h, n) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'ac-row';
    b.setAttribute('role', 'option');
    b.setAttribute('aria-selected', String(n === acSel));
    b.tabIndex = -1;
    const icon = document.createElement('i');
    const info = byId.get(h.id);
    if (h.art) { icon.className = 'art'; icon.textContent = 'arte'; } else atlasCell(icon, info.n, 28);
    const name = document.createElement('span');
    name.textContent = info.name || h.id;
    const code = document.createElement('code');
    code.textContent = `:${h.key}:`;
    b.append(icon, name, code);
    b.addEventListener('pointerdown', (e) => e.preventDefault());
    b.addEventListener('click', () => { acSel = n; acceptSuggest(); });
    ac.append(b);
  });
  ac.insertAdjacentHTML('beforeend', '<p class="ac-foot">↑↓ · Enter/Tab · Esc</p>');
  ac.hidden = false;
  placeSuggest();
}

function placeSuggest() {
  if (ac.hidden) return;
  const r = visual ? rich.caretRect() : document.getElementById('caret-at')?.getBoundingClientRect();
  if (!r) return;
  const field = ac.parentElement.getBoundingClientRect();
  const w = ac.offsetWidth;
  const h = ac.offsetHeight;
  ac.style.left = `${Math.max(4, Math.min(field.width - w - 4, r.left - field.left - 10))}px`;
  const below = r.bottom - field.top + 6;
  ac.style.top = `${below + h > field.height - 4 && r.top - field.top > h + 6 ? r.top - field.top - h - 6 : below}px`;
}

function acceptSuggest() {
  const h = acRows[acSel];
  if (!h) return;
  const start = acStart;
  closeSuggest();
  if (visual) {
    const end = rich.sel.end;
    const close = rich.atoms[end]?.ch === ':' ? 1 : 0;
    return rich.insertSource(`:${h.key}:`, { start, end: end + close });
  }
  const end = ta.selectionEnd;
  const close = ta.value[end] === ':' ? 1 : 0;
  replaceRange(start, end + close, `:${h.key}:`);
}

function acKeys(e) {
  if (ac.hidden) return;
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    acSel = (acSel + (e.key === 'ArrowDown' ? 1 : acRows.length - 1)) % acRows.length;
    ac.querySelectorAll('.ac-row').forEach((b, n) => b.setAttribute('aria-selected', String(n === acSel)));
  } else if (e.key === 'Enter' || e.key === 'Tab') acceptSuggest();
  else if (e.key === 'Escape') { acDismissed = acStart; closeSuggest(); }
  else return;
  e.preventDefault();
  e.stopImmediatePropagation();
}
ta.addEventListener('keydown', acKeys);
$('rich').addEventListener('keydown', acKeys, true);
$('rich').addEventListener('scroll', placeSuggest);
$('rich').addEventListener('blur', () => setTimeout(() => { if (!rich.hasFocus()) closeSuggest(); }, 120));

function focusEditor() {
  if (visual) rich.focus();
  else ta.focus();
}

// ---- colinha: o que o plugin e o jogo entendem; clique poe o exemplo no cursor ----

const CHEAT = [
  ['Servidor', [
    { code: 'Mel :mel:', text: 'placa inteira = ícone; nome na tábua e ao mirar' },
    { code: ':mel:', text: 'só o ícone' },
    { code: '<size=12>:mel:', text: 'lado do ícone, 1–18', pick: '12' },
    { code: ':mel 50%:', text: 'brilho do ícone', pick: '50' },
    { code: ':mapa g:', text: 'artes do servidor', arts: true },
    { code: '{u}', text: 'brilho: o texto brilha no escuro' },
    { code: 'texto > 50', text: 'vira coladas com >>, sozinho', none: true },
  ]],
  ['Jogo', [
    { code: '<#f80>', text: 'cor; 3 dígitos bastam', pick: 'f80' },
    { code: '<size=4>', text: 'tamanho da letra; tábua 20 × 10', pick: '4' },
    { code: 'Enter', text: 'quebra linha', insert: '\n' },
    { code: '<b> <i> <u> <s>', text: 'negrito, itálico, sublinhado, riscado', wrap: ['<i>', '</i>'] },
    { code: '<mark=#000000aa>', text: 'faixa atrás; 6 ou 8 dígitos', wrap: ['<mark=#000000aa>', '</mark>'], pick: '000000aa' },
    { code: '<alpha=#80>', text: 'transparência', pick: '80' },
    { code: '<rotate=15>', text: 'gira cada letra', pick: '15' },
    { code: '<sup> <sub>', text: 'sobrescrito, subscrito', wrap: ['<sup>', '</sup>'] },
    { code: '<line-height=60%>', text: 'espaço entre linhas', pick: '60' },
    { code: '<cspace=0.5>', text: 'espaço entre letras', pick: '0.5' },
    { code: '🔥 ⚔ ✨', text: 'emoji sai na placa', insert: '🔥' },
  ]],
];

function buildCheat() {
  const box = $('cheat');
  for (const [title, rows] of CHEAT) {
    const h = document.createElement('h3');
    h.textContent = title;
    box.append(h);
    for (const row of rows) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'cheat-row';
      const code = document.createElement('code');
      code.textContent = row.code;
      const text = document.createElement('span');
      text.textContent = row.text;
      b.append(code, text);
      if (row.none) b.disabled = true;
      b.addEventListener('click', () => {
        if (row.arts) return openLibrary(true, 'arts');
        if (row.wrap) return wrap(row.wrap[0], row.wrap[1], row.pick);
        insertAtCaret(row.insert ?? row.code, row.pick);
      });
      box.append(b);
    }
  }
  box.insertAdjacentHTML('beforeend', '<p class="cheat-note">Ícone só vale com a placa só nele, até 50. Digite <code>:</code> + nome para procurar.</p>');
}

const CHEAT_KEY = 'valheim.placa.colinha';
function showCheat(open) {
  $('cheat').hidden = !open;
  $('cheat-toggle').setAttribute('aria-expanded', String(open));
  try { localStorage.setItem(CHEAT_KEY, open ? '1' : '0'); } catch {}
  placeSuggest();
}
$('cheat-toggle').addEventListener('click', () => showCheat($('cheat').hidden));
buildCheat();
try { showCheat(localStorage.getItem(CHEAT_KEY) !== '0'); } catch { showCheat(true); }

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
  index.items.forEach(([id, name], n) => { add(id, name); byId.get(id).n = n; });
  index.groups.forEach((g) => g.ids.forEach((id) => {
    add(id, index.names[id] ?? '');
    byId.get(id).art = true;
  }));
  if (visual) rich.refresh();
  searchList = [...byId].map(([id, e]) => ({
    id,
    art: e.art ? 1 : 0,
    keys: [...new Set([id, ...e.aliases, normalize(e.name || '')].filter(Boolean))],
  }));
  const arts = index.groups.reduce((n, g) => n + g.ids.length, 0);
  $('count-items').textContent = index.items.length;
  $('count-arts').textContent = arts;
  showLibrary();
  schedule();
}

ta.value = startText();
let startVisual = true;
try { startVisual = localStorage.getItem(MODE_KEY) !== 'code'; } catch {}
setMode(startVisual, false);
SignSim.onFonts(schedule);
document.fonts?.ready.then(schedule);
update();
loadIndex();
