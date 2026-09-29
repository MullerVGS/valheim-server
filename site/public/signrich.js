// O texto da placa como o editor visual ve: uma fila de pecas (letra, quebra de linha ou "chip") cada
// uma com o estilo que o jogo daria a ela. parse le o codigo que se cola no jogo; serialize escreve de
// volta o codigo mais curto que da o mesmo resultado. Sem DOM: os testes rodam no Node.
//
// Estilo: { c: cor hex (3, 4, 6 ou 8 digitos), z: tamanho da letra, l: sem luz, m: faixa (6 ou 8 digitos),
// i: italico, u: sublinhado, s: riscado }. Chave ausente = padrao da placa.
// Pecas: { t: 'c', ch } letra (um code point), { t: 'n' } quebra de linha, { t: 'x', kind, raw } o que o
// editor mostra como chip: 'icon' (:mel:), 'macro' ({abreviacao}) ou 'tag' (tag do jogo sem botao aqui).

export const KEYS = ['c', 'z', 'l', 'm', 'i', 'u', 's'];
export const UNLIT_MACRO = 'u';

const NAMED = { black: '000', blue: '00f', green: '0f0', grey: '808080', lightblue: 'add8e6', orange: 'ff8000',
  purple: 'a020f0', red: 'f00', white: 'fff', yellow: 'ff0' };
const HEX = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/;
const MARK = /^#([0-9a-f]{6}|[0-9a-f]{8})$/;
const SIZE = /^[0-9]{1,3}(?:\.[0-9]{1,3})?$/;
const UNLIT = /^valheim_fonts\/valheim-(norse|norsebold|rune|prstartk|averiasans|averiaserif)$/;
const TOGGLES = { i: 'i', u: 'u', s: 's', em: 'i', strikethrough: 's' };

export const sameStyle = (a, b) => a === b || KEYS.every((k) => (a[k] ?? null) === (b[k] ?? null));

export function withStyle(st, key, value) {
  const out = { ...st };
  if (value == null || value === false) delete out[key];
  else out[key] = value;
  return out;
}

// O que muda o estilo e vira botao no editor. null = nao e deste grupo (vira chip ou texto).
function styleTag(body) {
  const lower = body.toLowerCase();
  if (lower[0] === '#') return HEX.test(lower) ? { push: 'c', value: lower.slice(1) } : null;
  const eq = lower.indexOf('=');
  const name = eq < 0 ? lower : lower.slice(0, eq);
  const value = eq < 0 ? '' : lower.slice(eq + 1).replace(/^"(.*)"$/, '$1');
  if (eq < 0 && TOGGLES[name]) return { set: TOGGLES[name], value: true };
  if (name[0] === '/' && eq < 0 && TOGGLES[name.slice(1)]) return { set: TOGGLES[name.slice(1)], value: null };
  switch (name) {
    case 'color':
      if (HEX.test(value)) return { push: 'c', value: value.slice(1) };
      return NAMED[value] ? { push: 'c', value: NAMED[value] } : null;
    case '/color': return eq < 0 ? { pop: 'c' } : null;
    case 'size': return SIZE.test(value) ? { push: 'z', value: String(Number(value)) } : null;
    case '/size': return eq < 0 ? { pop: 'z' } : null;
    case 'mark': return MARK.test(value) ? { set: 'm', value: value.slice(1) } : null;
    case '/mark': return eq < 0 ? { set: 'm', value: null } : null;
    case 'material': return UNLIT.test(value) ? { push: 'l', value: true } : null;
    case '/material': return eq < 0 ? { pop: 'l' } : null;
  }
  return null;
}

// env: { isTag(raw) tag que o jogo entende, isIcon(inner) codigo de icone conhecido, isMacro(name) }.
// Cada peca leva `at`: onde o texto dela comeca no codigo (o cursor passa de um lado para o outro por ai).
export function parse(source, env = {}) {
  const atoms = [];
  let st = {};
  const stacks = { c: [], z: [], l: [] };
  const push = (atom, at) => { atom.st = st; atom.at = at; atoms.push(atom); };
  const apply = (r) => {
    if (r.push) {
      stacks[r.push].push(st[r.push]);
      st = withStyle(st, r.push, r.value);
    } else if (r.pop) st = withStyle(st, r.pop, stacks[r.pop].pop());
    else st = withStyle(st, r.set, r.value);
  };
  let i = 0;
  while (i < source.length) {
    const ch = source[i];
    if (ch === '<') {
      const close = source.indexOf('>', i + 1);
      const body = close > i ? source.slice(i + 1, close) : '';
      if (close > i && close - i <= 128 && !/[<\n]/.test(body)) {
        const r = styleTag(body);
        if (r) { apply(r); i = close + 1; continue; }
        const raw = source.slice(i, close + 1);
        if (env.isTag?.(raw)) { push({ t: 'x', kind: 'tag', raw }, i); i = close + 1; continue; }
      }
    } else if (ch === '{') {
      const m = /^\{([A-Za-z0-9]+)\}/.exec(source.slice(i, i + 40));
      if (m && m[1].toLowerCase() === UNLIT_MACRO) { apply({ push: 'l', value: true }); i += m[0].length; continue; }
      if (m && env.isMacro?.(m[1].toLowerCase())) { push({ t: 'x', kind: 'macro', raw: m[0] }, i); i += m[0].length; continue; }
    } else if (ch === ':' && env.isIcon) {
      const m = /^:([^:<>\n]{1,60}):/.exec(source.slice(i, i + 63));
      if (m && env.isIcon(m[1])) { push({ t: 'x', kind: 'icon', raw: m[0], inner: m[1] }, i); i += m[0].length; continue; }
    } else if (ch === '\\' && source[i + 1] === 'n') {
      push({ t: 'n' }, i);
      i += 2;
      continue;
    } else if (ch === '\n') {
      push({ t: 'n' }, i);
      i++;
      continue;
    } else if (ch === '\r') {
      i++;
      continue;
    }
    const full = String.fromCodePoint(source.codePointAt(i));
    push({ t: 'c', ch: full }, i);
    i += full.length;
  }
  return { atoms, end: st };
}

// Quebra de linha e tag solta nao tem cara: o estilo delas nao pede tag nenhuma.
const styled = (a) => a.t === 'c' || (a.t === 'x' && a.kind !== 'tag');

// O codigo mais curto para as pecas: so escreve o que muda, e nada no fim (o que sobra aberto nao custa).
// pos[i] = onde a peca i comeca no codigo; pos[atoms.length] = fim.
export function serialize(atoms) {
  let src = '';
  let cur = {};
  const depth = { c: 0, z: 0 };
  const pos = [];
  const stacked = (key, open, close, value) => {
    if (value != null) {
      src += open(value);
      depth[key]++;
    } else {
      src += close.repeat(depth[key]);
      depth[key] = 0;
    }
  };
  for (const a of atoms) {
    if (styled(a) && !sameStyle(cur, a.st)) {
      const st = a.st;
      if ((cur.c ?? null) !== (st.c ?? null)) stacked('c', (v) => `<#${v}>`, '</color>', st.c);
      if ((cur.z ?? null) !== (st.z ?? null)) stacked('z', (v) => `<size=${v}>`, '</size>', st.z);
      if (!!cur.l !== !!st.l) src += st.l ? `{${UNLIT_MACRO}}` : '</material>';
      if ((cur.m ?? null) !== (st.m ?? null)) src += st.m ? `<mark=#${st.m}>` : '</mark>';
      for (const k of ['i', 'u', 's']) if (!!cur[k] !== !!st[k]) src += st[k] ? `<${k}>` : `</${k}>`;
      cur = st;
    }
    pos.push(src.length);
    src += a.t === 'c' ? a.ch : a.t === 'n' ? '\n' : a.raw;
  }
  pos.push(src.length);
  return { src, pos };
}

// Onde cai, nas pecas, um ponto do codigo: a primeira peca que comeca nele ou depois.
export function atomAt(atoms, offset) {
  let lo = 0;
  let hi = atoms.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (atoms[mid].at < offset) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

// Estilo comum de um trecho: valor de cada chave se todas as letras concordam, 'mixed' se nao.
export function commonStyle(atoms, start, end) {
  const out = {};
  let first = true;
  for (let i = start; i < end; i++) {
    const a = atoms[i];
    if (!styled(a)) continue;
    for (const k of KEYS) {
      const v = a.st[k] ?? null;
      if (first) out[k] = v;
      else if (out[k] !== v) out[k] = 'mixed';
    }
    first = false;
  }
  return first ? null : out;
}

// Cor da tag no CSS: 3, 4, 6 ou 8 digitos o navegador ja entende.
export const cssColor = (hex) => `#${hex}`;
